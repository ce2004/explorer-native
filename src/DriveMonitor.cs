using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The Google Drive monitor: keeps folder pairs in step while Explorer
    /// Native runs and Drive is connected, and keeps going through offline
    /// spells until everything is across.
    ///
    /// Detection never depends on events. Every pass lists both sides and
    /// compares them with what both held at the last sync (kept per pair under
    /// AppData\ExplorerNative\drive-monitor, encrypted), so a change made
    /// offline, while the app was closed, or whose watcher event was lost is
    /// found all the same. Events, Sync now, Drive connecting and Drive coming
    /// back online only decide when a pass runs.
    ///
    /// Interrupted transfers resume part way: a download continues from the
    /// end of its ".partial" file with a ranged read, an upload continues its
    /// resumable session, both remembered in the same encrypted state, which is
    /// saved as each file completes. Disk space is checked before each
    /// download, keeping a margin (<see cref="SyncSpace"/>); Drive's quota
    /// before each upload.
    ///
    /// Drive is reached only through the API; the drive letter and its sync
    /// root are never walked. <see cref="SyncPlanner"/> decides; this carries
    /// it out.
    /// </summary>
    internal sealed class DriveMonitor : IDisposable
    {
        /// <summary>The running monitor, for the Preferences dialog. Null before the tray starts it.</summary>
        public static DriveMonitor? Current { get; private set; }

        public sealed record PairStatus(
            DateTime? LastSyncedUtc, string? Problem, bool Running, long DoneBytes = 0, long TotalBytes = 0);

        /// <summary>A download part way through, and which Drive file it is part of.</summary>
        public sealed record PartialDownload(string RemoteId, long Size, DateTime ModifiedUtc, string? Md5);

        /// <summary>An upload session part way through, and which local file it is for.</summary>
        public sealed record UploadSession(string Session, long Size, DateTime ModifiedUtc, DateTime StartedUtc);

        private sealed class PairState
        {
            public Dictionary<string, SyncBase> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, PartialDownload> Partials { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, UploadSession> Uploads { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public DateTime? LastSyncedUtc { get; set; }
            public string? Problem { get; set; }
        }

        private const int ChunkBytes = 8 * 1024 * 1024;

        /// <summary>Google keeps a resumable session about a week; older ones are not tried.</summary>
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(6);

        private readonly GoogleDrive _drive;
        private readonly Action<string, string> _notify;
        private readonly Action<List<DriveSyncPair>> _save;
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _wake = new(0);
        private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
        private readonly Dictionary<string, PairStatus> _status = new();
        private readonly HashSet<string> _due = new();
        private readonly Dictionary<string, DateTime> _lastChecked = new();
        private List<DriveSyncPair> _pairs = new();
        private System.Threading.Timer? _debounce;
        private bool _allDue = true;
        private bool _saidOffline;
        private string? _running;
        private long _lastProgressEvent;

        /// <summary>Raised on a worker thread whenever a pair's status changes.</summary>
        public event Action? Changed;

        public DriveMonitor(GoogleDrive drive, Action<string, string> notify, Action<List<DriveSyncPair>> save)
        {
            _drive = drive;
            _notify = notify;
            _save = save;
            Current = this;

            // Back online: everything changed meanwhile, on either side, is found
            // by comparing with the last sync, so every pair runs now rather
            // than on its timer.
            _drive.ConnectionChanged += state =>
            {
                if (state == DriveState.Online) SyncNow();
            };

            _ = Task.Run(LoopAsync);
        }

        public static string StateDirectory => Path.Combine(Settings.AppDataDir, "drive-monitor");

        private static string StatePath(string pairId) => Path.Combine(StateDirectory, pairId + ".json");

        public List<DriveSyncPair> Pairs
        {
            get { lock (_gate) return _pairs.Select(p => p.Copy()).ToList(); }
        }

        /// <summary>The live drive, for the space and quota checks in the add/edit dialog.</summary>
        internal GoogleDrive Drive => _drive;

        /// <summary>Takes the pairs settings now hold, and watches their PC folders.</summary>
        public void Reload(IEnumerable<DriveSyncPair> pairs)
        {
            lock (_gate)
            {
                _pairs = pairs.Select(p => p.Copy()).ToList();

                foreach (var gone in _watchers.Keys.Where(id => _pairs.All(p => p.Id != id)).ToList())
                {
                    try { _watchers[gone].Dispose(); } catch { }
                    _watchers.Remove(gone);
                    _status.Remove(gone);
                }

                foreach (var pair in _pairs)
                {
                    if (_watchers.TryGetValue(pair.Id, out var existing))
                    {
                        if (!pair.Paused &&
                            string.Equals(existing.Path, pair.LocalFolder, StringComparison.OrdinalIgnoreCase) &&
                            existing.IncludeSubdirectories == pair.IncludeSubfolders) continue;
                        try { existing.Dispose(); } catch { }
                        _watchers.Remove(pair.Id);
                    }

                    if (pair.Paused || !Directory.Exists(pair.LocalFolder)) continue;

                    try
                    {
                        var watcher = new FileSystemWatcher(pair.LocalFolder)
                        {
                            IncludeSubdirectories = pair.IncludeSubfolders,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                           NotifyFilters.LastWrite | NotifyFilters.Size,
                            InternalBufferSize = 64 * 1024,
                        };

                        // Events only say "look soon". Nothing is learned from them:
                        // the pass compares the whole folder with the last sync, so
                        // a lost event or a buffer overflow (Error) loses nothing.
                        var id = pair.Id;
                        FileSystemEventHandler changed = (_, e) =>
                        {
                            if (!SyncPlanner.IsSkipped(e.Name ?? "")) Soon(id);
                        };
                        watcher.Created += changed;
                        watcher.Changed += changed;
                        watcher.Deleted += changed;
                        watcher.Renamed += (_, _) => Soon(id);
                        watcher.Error += (_, _) => Soon(id);
                        watcher.EnableRaisingEvents = true;
                        _watchers[pair.Id] = watcher;
                    }
                    catch { }
                }
            }

            try { Changed?.Invoke(); } catch { }
            SyncNow();
        }

        /// <summary>Saves an edited list of pairs at once (through settings) and syncs.</summary>
        public void SetPairs(List<DriveSyncPair> pairs)
        {
            var copy = pairs.Select(p => p.Copy()).ToList();
            _save(copy);
            Reload(copy);
        }

        /// <summary>Every pair, now: Sync now, at start, Drive connecting, Drive back online.</summary>
        public void SyncNow()
        {
            lock (_gate) _allDue = true;
            Wake();
        }

        private void Wake()
        {
            try { if (_wake.CurrentCount == 0) _wake.Release(); } catch { }
        }

        /// <summary>One pair, after three quiet seconds, so a burst of saves is one pass.</summary>
        private void Soon(string pairId)
        {
            lock (_gate)
            {
                _due.Add(pairId);
                _debounce ??= new System.Threading.Timer(_ => Wake());
                _debounce.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>
        /// Whether a pair runs in this pass: asked for, its PC folder changed, or
        /// its "check Drive every" time is up. Zero minutes means only on request.
        /// </summary>
        private bool TakeDue(DriveSyncPair pair, bool all, DateTime now)
        {
            lock (_gate)
            {
                if (all || _due.Remove(pair.Id)) return true;
                if (pair.CheckMinutes <= 0) return false;
                return !_lastChecked.TryGetValue(pair.Id, out var last) ||
                       now - last >= TimeSpan.FromMinutes(pair.CheckMinutes);
            }
        }

        public PairStatus StatusOf(string pairId)
        {
            lock (_gate)
            {
                if (_status.TryGetValue(pairId, out var known)) return known with { Running = _running == pairId };
            }

            var state = LoadState(pairId);
            var status = new PairStatus(state.LastSyncedUtc, state.Problem, false);
            lock (_gate) _status[pairId] = status;
            return status;
        }

        private void SetStatus(string pairId, PairStatus status)
        {
            lock (_gate) _status[pairId] = status;
            try { Changed?.Invoke(); } catch { }
        }

        /// <summary>Live progress, at most a few times a second.</summary>
        private void SetProgress(string pairId, long done, long total)
        {
            lock (_gate)
            {
                if (!_status.TryGetValue(pairId, out var was)) return;
                _status[pairId] = was with { DoneBytes = done, TotalBytes = total };
            }
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastProgressEvent) < 500) return;
            Interlocked.Exchange(ref _lastProgressEvent, now);
            try { Changed?.Invoke(); } catch { }
        }

        private async Task LoopAsync()
        {
            var token = _stop.Token;
            while (!token.IsCancellationRequested)
            {
                try { await _wake.WaitAsync(TimeSpan.FromSeconds(60), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                try { await PassAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch { }
            }
        }

        private async Task PassAsync(CancellationToken token)
        {
            // Not connected: nothing is queued and nothing is forgotten. The
            // first pass after Drive connects compares everything again.
            var client = _drive.Client;
            if (client == null)
            {
                lock (_gate) _allDue = true;
                return;
            }

            bool all;
            lock (_gate)
            {
                all = _allDue;
                _allDue = false;
            }
            var now = DateTime.UtcNow;

            foreach (var pair in Pairs)
            {
                if (token.IsCancellationRequested) return;
                if (pair.Paused || string.IsNullOrEmpty(pair.DriveFolderId)) continue;
                if (!TakeDue(pair, all, now)) continue;

                lock (_gate) _running = pair.Id;
                SetStatus(pair.Id, StatusOf(pair.Id) with { Running = true, DoneBytes = 0, TotalBytes = 0 });

                try
                {
                    await SyncPairAsync(client, pair, token).ConfigureAwait(false);
                    lock (_gate) _lastChecked[pair.Id] = DateTime.UtcNow;
                    _saidOffline = false;
                }
                catch (Exception ex) when (IsOffline(ex, token))
                {
                    // Whatever was done is saved; whatever was not is found
                    // again by the next pass. Every pair stays due, so the
                    // reconnect (or the next minute) carries straight on.
                    lock (_gate)
                    {
                        _running = null;
                        _allDue = true;
                    }
                    SetStatus(pair.Id, StatusOf(pair.Id) with
                    {
                        Problem = "offline, will carry on when Google Drive is back", Running = false,
                    });
                    if (!_saidOffline)
                    {
                        _saidOffline = true;
                        _notify("sync.offline", "Drive monitor paused, offline");
                    }
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Anything else (an expired sign-in, a Drive folder that is
                    // gone) is this pair's problem, shown in the list and said
                    // once; the other pairs still run.
                    var problem = ex is GoogleSignInRequiredException
                        ? "Google needs you to sign in again"
                        : ex.Message;
                    var was = StatusOf(pair.Id).Problem;
                    lock (_gate) _running = null;
                    SetStatus(pair.Id, StatusOf(pair.Id) with { Problem = problem, Running = false });
                    if (was != problem) _notify("sync.error", $"{DisplayName(pair)} could not sync: {problem}");
                }
                finally
                {
                    lock (_gate) if (_running == pair.Id) _running = null;
                    try { Changed?.Invoke(); } catch { }
                }
            }
        }

        private static bool IsOffline(Exception ex, CancellationToken token)
        {
            if (token.IsCancellationRequested) return false;
            for (var e = ex; e != null; e = e.InnerException)
                if (e is HttpRequestException or SocketException or TaskCanceledException or TimeoutException)
                    return true;
            return false;
        }

        /// <summary>Thrown when the connection is found to be down part way through a pass.</summary>
        private sealed class OfflineException : HttpRequestException
        {
            public OfflineException(Exception inner) : base("Google Drive could not be reached", inner) { }
        }

        private async Task SyncPairAsync(DriveClient client, DriveSyncPair pair, CancellationToken token)
        {
            var state = LoadState(pair.Id);
            var root = pair.LocalFolder;
            var name = DisplayName(pair);

            string? refuse = Refusal(root);
            if (refuse != null)
            {
                Finish(pair, state, refuse, announce: true);
                return;
            }

            var local = ScanLocal(root, pair.IncludeSubfolders);
            var (remote, folders) = await ScanRemoteAsync(client, pair.DriveFolderId, pair.IncludeSubfolders, token)
                .ConfigureAwait(false);
            var plan = SyncPlanner.Ordered(SyncPlanner.Plan(SyncRules.For(pair), local, remote, state.Files), local, remote);

            // How big this pass is, for the progress said along the way.
            long downBytes = 0, upBytes = 0;
            int transfers = 0;
            foreach (var a in plan)
            {
                if (a.Kind is SyncActionKind.Download or SyncActionKind.KeepBothRemoteWins) { downBytes += remote[a.Path].Size; transfers++; }
                if (a.Kind is SyncActionKind.Upload or SyncActionKind.KeepBothLocalWins) { upBytes += local[a.Path].Size; transfers++; }
            }
            long totalBytes = downBytes + upBytes, doneBytes = 0;
            bool big = SyncSpace.IsBig(totalBytes, transfers);
            if (big) _notify("sync.start", $"{name}: {Amounts(downBytes, upBytes)}, {transfers:N0} files");
            SetProgress(pair.Id, 0, totalBytes);

            void Moved(long bytes)
            {
                long before = doneBytes;
                doneBytes += bytes;
                SetProgress(pair.Id, doneBytes, totalBytes);
                if (big && SyncSpace.QuarterPassed(before, doneBytes, totalBytes) is int percent and > 0)
                    _notify("sync.progress", $"{name}: {percent} percent synced");
            }

            int uploaded = 0, downloaded = 0, deleted = 0, failed = 0;
            string? lastError = null, spaceProblem = null;
            var conflicts = new List<string>();
            long uploadedThisPass = 0;

            var saved = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                for (int i = 0; i < plan.Count; i++)
                {
                    var action = plan[i];
                    token.ThrowIfCancellationRequested();
                    if (saved.ElapsedMilliseconds > 2000)
                    {
                        SaveState(pair.Id, state);
                        saved.Restart();
                    }

                    var path = action.Path;
                    var localPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

                    // Room before each file: the PC's disk keeps its margin, and
                    // Drive is never asked to hold more than its quota.
                    if (action.Kind is SyncActionKind.Download or SyncActionKind.KeepBothRemoteWins)
                    {
                        long need = remote[path].Size - PartialLength(state, path, localPath, remote[path]);
                        var (free, volume, letter) = DiskOf(root);
                        if (volume > 0 && !SyncSpace.Fits(need, free, volume))
                        {
                            long stillToCome = plan.Skip(i)
                                .Where(a => a.Kind is SyncActionKind.Download or SyncActionKind.KeepBothRemoteWins)
                                .Sum(a => remote[a.Path].Size);
                            spaceProblem = $"paused: {letter} is out of space, needs " +
                                           $"{Words(SyncSpace.Shortfall(stillToCome, free, volume))} more";
                            break;
                        }
                    }
                    if (action.Kind is SyncActionKind.Upload or SyncActionKind.KeepBothLocalWins && _drive.QuotaLimit > 0)
                    {
                        long room = _drive.QuotaLimit - _drive.QuotaUsed - uploadedThisPass;
                        if (local[path].Size > room)
                        {
                            long stillToGo = plan.Skip(i)
                                .Where(a => a.Kind is SyncActionKind.Upload or SyncActionKind.KeepBothLocalWins)
                                .Sum(a => local[a.Path].Size);
                            spaceProblem = $"paused: Google Drive is full, needs {Words(stillToGo - Math.Max(0, room))} more";
                            break;
                        }
                    }

                    try
                    {
                        switch (action.Kind)
                        {
                            case SyncActionKind.Upload:
                                remote.TryGetValue(path, out var old);
                                state.Files[path] = await UploadAsync(client, folders, pair, state, path, localPath,
                                    local[path], old.Id, Moved, token).ConfigureAwait(false);
                                uploadedThisPass += local[path].Size;
                                uploaded++;
                                break;

                            case SyncActionKind.Download:
                                state.Files[path] = await DownloadAsync(client, pair.Id, state, path, remote[path],
                                    localPath, Moved, token).ConfigureAwait(false);
                                downloaded++;
                                break;

                            case SyncActionKind.DeleteRemote:
                                await client.Trash(remote[path].Id!, token).ConfigureAwait(false);
                                state.Files.Remove(path);
                                deleted++;
                                break;

                            case SyncActionKind.DeleteLocal:
                                ShellDelete.Recycle(localPath, IntPtr.Zero);
                                state.Files.Remove(path);
                                deleted++;
                                break;

                            case SyncActionKind.KeepBothLocalWins:
                            {
                                // Drive's copy steps aside under the conflict name; the PC's goes up.
                                var aside = SyncPlanner.ConflictName(path, DateTime.Now, p => remote.ContainsKey(p) || local.ContainsKey(p));
                                await client.MoveOrRename(remote[path].Id!, Path.GetFileName(aside), null, null, token).ConfigureAwait(false);
                                state.Files[path] = await UploadAsync(client, folders, pair, state, path, localPath,
                                    local[path], null, Moved, token).ConfigureAwait(false);
                                uploadedThisPass += local[path].Size;
                                conflicts.Add(Path.GetFileName(path));
                                uploaded++;
                                break;
                            }

                            case SyncActionKind.KeepBothRemoteWins:
                            {
                                // The PC's copy steps aside under the conflict name; Drive's comes down.
                                var aside = SyncPlanner.ConflictName(path, DateTime.Now, p => remote.ContainsKey(p) || local.ContainsKey(p));
                                File.Move(localPath, Path.Combine(root, aside.Replace('/', Path.DirectorySeparatorChar)));
                                state.Files[path] = await DownloadAsync(client, pair.Id, state, path, remote[path],
                                    localPath, Moved, token).ConfigureAwait(false);
                                conflicts.Add(Path.GetFileName(path));
                                downloaded++;
                                break;
                            }

                            case SyncActionKind.Record:
                                state.Files[path] = new SyncBase(local[path].Size, local[path].ModifiedUtc,
                                    remote[path].Size, remote[path].ModifiedUtc, remote[path].Id ?? "", remote[path].Md5);
                                break;

                            case SyncActionKind.Forget:
                                state.Files.Remove(path);
                                break;
                        }
                    }
                    catch (Exception ex) when (!IsOffline(ex, token) && ex is not OperationCanceledException
                                                                     && ex is not DriveQuotaException)
                    {
                        // A failure that might really be the connection going:
                        // asked of Drive itself, and if Drive cannot be reached
                        // the pass stops here and carries on when it is back.
                        try { await client.RootId(token).ConfigureAwait(false); }
                        catch (Exception probe) when (IsOffline(probe, token)) { throw new OfflineException(probe); }

                        failed++;
                        lastError = $"{Path.GetFileName(path)}: {ex.Message}";
                    }
                }
            }
            finally
            {
                // Everything done so far is remembered even if the connection
                // dropped part way. What was not done is found again next time.
                SaveState(pair.Id, state);
            }

            if (conflicts.Count > 0)
                _notify("sync.conflict", conflicts.Count == 1
                    ? $"{name}: {conflicts[0]} changed in both places; both copies kept"
                    : $"{name}: {conflicts.Count} files changed in both places; both copies kept");

            if (uploaded + downloaded + deleted > 0)
                _notify("sync.done", $"{name} synced: " + Tally(uploaded, downloaded, deleted));

            if (spaceProblem != null)
            {
                // Not a setting: the pair is not marked paused, so it carries on
                // by itself on a later pass once there is room.
                bool newProblem = spaceProblem != state.Problem;
                state.Problem = spaceProblem;
                SaveState(pair.Id, state);
                SetStatus(pair.Id, new PairStatus(state.LastSyncedUtc, spaceProblem, false));
                if (newProblem) _notify("sync.space", $"{name} {spaceProblem}");
                return;
            }

            if (failed > 0)
            {
                Finish(pair, state, failed == 1 ? lastError : $"{failed} files could not be synced. {lastError}", announce: true);
                return;
            }

            Finish(pair, state, null, announce: false);
        }

        private void Finish(DriveSyncPair pair, PairState state, string? problem, bool announce)
        {
            bool newProblem = problem != null && problem != state.Problem;
            state.Problem = problem;
            if (problem == null) state.LastSyncedUtc = DateTime.UtcNow;
            SaveState(pair.Id, state);
            SetStatus(pair.Id, new PairStatus(state.LastSyncedUtc, problem, false));
            if (announce && newProblem) _notify("sync.error", $"{DisplayName(pair)} could not sync: {problem}");
        }

        internal static string Tally(int uploaded, int downloaded, int deleted)
        {
            var parts = new List<string>();
            if (uploaded > 0) parts.Add($"{uploaded} uploaded");
            if (downloaded > 0) parts.Add($"{downloaded} downloaded");
            if (deleted > 0) parts.Add($"{deleted} deleted");
            return string.Join(", ", parts);
        }

        /// <summary>"214 gigabytes to download, 3 gigabytes to upload".</summary>
        internal static string Amounts(long down, long up)
        {
            var parts = new List<string>();
            if (down > 0) parts.Add($"{Words(down)} to download");
            if (up > 0) parts.Add($"{Words(up)} to upload");
            return parts.Count == 0 ? "nothing to copy" : string.Join(", ", parts);
        }

        internal static string Words(long bytes) => SizeFormatter.Format(Math.Max(0, bytes), SizeUnitStyle.FullWords);

        public static string DisplayName(DriveSyncPair pair) =>
            !string.IsNullOrWhiteSpace(pair.Name) ? pair.Name.Trim()
            : Path.GetFileName(pair.LocalFolder.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } leaf ? leaf
            : pair.LocalFolder;

        /// <summary>
        /// Why a PC folder cannot be monitored, or null when it can. The Drive
        /// letter and the app's own settings folder (which holds Drive's sync
        /// root) are refused: walking them reads every file from Google.
        /// </summary>
        public static string? Refusal(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return "No folder on this PC was chosen.";

            string full;
            try { full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return "That is not a folder path."; }

            if (!Directory.Exists(full)) return $"{full} does not exist.";

            var appData = Path.GetFullPath(Settings.AppDataDir).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(appData, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(appData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                appData.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "That folder holds Explorer Native's own settings and cannot be monitored.";

            var letter = Current?._drive.Letter;
            if (letter != null && full.StartsWith(letter.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return "That folder is on the Google Drive letter. Choose a folder on this PC.";

            return null;
        }

        /// <summary>Free and total bytes of the volume a folder is on, and its letter, such as "C:".</summary>
        internal static (long Free, long Total, string Letter) DiskOf(string folder)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(folder)) ?? "";
                var drive = new DriveInfo(root);
                return (drive.AvailableFreeSpace, drive.TotalSize, root.TrimEnd(Path.DirectorySeparatorChar));
            }
            catch
            {
                return (0, 0, folder);
            }
        }

        /// <summary>
        /// What a pair would copy right now, in bytes each way, for the space
        /// check when it is added or edited: files the other side does not
        /// already hold at the same size, and only in the directions the mode
        /// allows, under the pair's own rules.
        /// </summary>
        internal static async Task<(long Down, long Up)> NeedsAsync(DriveClient client, DriveSyncPair pair, CancellationToken token)
        {
            var local = Directory.Exists(pair.LocalFolder)
                ? ScanLocal(pair.LocalFolder, pair.IncludeSubfolders)
                : new Dictionary<string, SyncFile>(StringComparer.OrdinalIgnoreCase);
            var (remote, _) = await ScanRemoteAsync(client, pair.DriveFolderId, pair.IncludeSubfolders, token).ConfigureAwait(false);

            var plan = SyncPlanner.Plan(SyncRules.For(pair), local, remote,
                new Dictionary<string, SyncBase>(StringComparer.OrdinalIgnoreCase));
            long down = 0, up = 0;
            foreach (var a in plan)
            {
                if (a.Kind is SyncActionKind.Download or SyncActionKind.KeepBothRemoteWins) down += remote[a.Path].Size;
                if (a.Kind is SyncActionKind.Upload or SyncActionKind.KeepBothLocalWins) up += local[a.Path].Size;
            }
            return (down, up);
        }

        // ---------------- scanning ----------------

        internal static Dictionary<string, SyncFile> ScanLocal(string root, bool subfolders)
        {
            var files = new Dictionary<string, SyncFile>(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = subfolders,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };

            foreach (var path in Directory.EnumerateFiles(root, "*", options))
            {
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (SyncPlanner.IsSkippedPath(relative)) continue;
                try
                {
                    var info = new FileInfo(path);
                    files[relative] = new SyncFile(info.Length, Truncate(info.LastWriteTimeUtc));
                }
                catch { }
            }

            return files;
        }

        private static async Task<(Dictionary<string, SyncFile> Files, Dictionary<string, string> Folders)> ScanRemoteAsync(
            DriveClient client, string rootId, bool subfolders, CancellationToken token)
        {
            var files = new Dictionary<string, SyncFile>(StringComparer.OrdinalIgnoreCase);
            var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [""] = rootId };
            var queue = new Queue<(string Id, string Relative)>();
            queue.Enqueue((rootId, ""));

            while (queue.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var (id, relative) = queue.Dequeue();
                foreach (var entry in await client.ListChildren(id, token).ConfigureAwait(false))
                {
                    var path = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                    if (entry.IsFolder)
                    {
                        if (subfolders && folders.TryAdd(path, entry.Id)) queue.Enqueue((entry.Id, path));
                        continue;
                    }

                    // Docs, Sheets and Slides have no bytes to copy.
                    if (entry.IsGoogleDocument || SyncPlanner.IsSkippedPath(path)) continue;

                    // Two Drive files of one name: the newer is the one that counts.
                    var file = new SyncFile(entry.Size, Truncate(entry.Modified.ToUniversalTime()), entry.Id, entry.Md5);
                    if (!files.TryGetValue(path, out var seen) || file.ModifiedUtc > seen.ModifiedUtc) files[path] = file;
                }
            }

            return (files, folders);
        }

        private static DateTime Truncate(DateTime utc) =>
            new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

        // ---------------- copying ----------------

        private static async Task<string> EnsureFolderAsync(
            DriveClient client, Dictionary<string, string> folders, string rootId, string relativeFolder,
            CancellationToken token)
        {
            if (relativeFolder.Length == 0) return rootId;
            if (folders.TryGetValue(relativeFolder, out var known)) return known;

            int slash = relativeFolder.LastIndexOf('/');
            var parent = await EnsureFolderAsync(client, folders, rootId,
                slash < 0 ? "" : relativeFolder[..slash], token).ConfigureAwait(false);
            var id = await client.CreateFolder(relativeFolder[(slash + 1)..], parent, token).ConfigureAwait(false);
            folders[relativeFolder] = id;
            return id;
        }

        private async Task<SyncBase> UploadAsync(
            DriveClient client, Dictionary<string, string> folders, DriveSyncPair pair, PairState state,
            string path, string localPath, SyncFile local, string? replaces, Action<long> moved, CancellationToken token)
        {
            int slash = path.LastIndexOf('/');
            var parent = await EnsureFolderAsync(client, folders, pair.DriveFolderId, slash < 0 ? "" : path[..slash], token)
                .ConfigureAwait(false);

            // A session saved from an earlier, interrupted attempt at this same
            // file carries on from where Google says it stopped.
            string? resume = null;
            if (state.Uploads.TryGetValue(path, out var saved) && saved.Size == local.Size &&
                Math.Abs((saved.ModifiedUtc - local.ModifiedUtc).TotalSeconds) <= 2 &&
                DateTime.UtcNow - saved.StartedUtc < SessionLifetime)
                resume = saved.Session;

            long reported = 0;
            string id;
            try
            {
                id = await client.Upload(localPath, Path.GetFileName(localPath), parent,
                    p =>
                    {
                        long delta = p.BytesSent - reported;
                        if (delta > 0) { reported = p.BytesSent; moved(delta); }
                    },
                    token, resume,
                    session =>
                    {
                        state.Uploads[path] = new UploadSession(session, local.Size, local.ModifiedUtc, DateTime.UtcNow);
                        SaveState(pair.Id, state);
                    }).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsOffline(ex, token))
            {
                // Not the connection: a session that will not take is not kept.
                state.Uploads.Remove(path);
                throw;
            }

            if (reported < local.Size) moved(local.Size - reported);
            state.Uploads.Remove(path);

            // The new copy is up before the old one goes, and the old one goes to
            // the Drive trash, where its earlier contents can still be had back.
            if (!string.IsNullOrEmpty(replaces))
            {
                try { await client.Trash(replaces, token).ConfigureAwait(false); } catch { }
            }

            var stamp = DriveClient.UploadStamp(localPath);
            return new SyncBase(local.Size, local.ModifiedUtc, local.Size, Truncate(stamp), id);
        }

        /// <summary>How much of a download is already on disk and can be continued from.</summary>
        private static long PartialLength(PairState state, string path, string localPath, SyncFile remote)
        {
            var partial = localPath + ".partial";
            if (!state.Partials.TryGetValue(path, out var p) || !File.Exists(partial)) return 0;
            long length;
            try { length = new FileInfo(partial).Length; } catch { return 0; }
            return SyncSpace.ResumeOffset(length, remote.Size, SameDriveFile(p, remote));
        }

        /// <summary>
        /// Whether a partial download is still part of this Drive file: same id
        /// and size, and the same checksum (or, without one, the same time).
        /// </summary>
        internal static bool SameDriveFile(PartialDownload partial, SyncFile remote) =>
            partial.RemoteId == remote.Id && partial.Size == remote.Size &&
            (partial.Md5 != null && remote.Md5 != null
                ? string.Equals(partial.Md5, remote.Md5, StringComparison.OrdinalIgnoreCase)
                : Math.Abs((partial.ModifiedUtc - remote.ModifiedUtc).TotalSeconds) <= 2);

        private async Task<SyncBase> DownloadAsync(
            DriveClient client, string pairId, PairState state, string path, SyncFile remote, string localPath,
            Action<long> moved, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            var partial = localPath + ".partial";

            long offset = PartialLength(state, path, localPath, remote);
            if (offset == 0)
            {
                try { File.Delete(partial); } catch { }
            }

            // Remembered before the first byte, so a drop at any point resumes.
            state.Partials[path] = new PartialDownload(remote.Id ?? "", remote.Size, remote.ModifiedUtc, remote.Md5);
            SaveState(pairId, state);
            if (offset > 0) moved(offset);

            await using (var file = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Open,
                             FileAccess.Write, FileShare.None))
            {
                file.SetLength(offset);
                file.Position = offset;
                while (offset < remote.Size)
                {
                    int want = (int)Math.Min(ChunkBytes, remote.Size - offset);
                    var bytes = await client.ReadRange(remote.Id!, offset, want, token).ConfigureAwait(false);
                    if (bytes.Length == 0) throw new IOException("Google Drive sent nothing back.");
                    await file.WriteAsync(bytes, token).ConfigureAwait(false);
                    offset += bytes.Length;
                    moved(bytes.Length);
                }
            }

            // Checked before it replaces anything: the size, and the checksum
            // when Drive gives one. A bad copy is thrown away and fetched again.
            bool good = new FileInfo(partial).Length == remote.Size;
            if (good && remote.Md5 != null)
            {
                await using var check = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1 << 20, FileOptions.SequentialScan);
                var md5 = Convert.ToHexString(await MD5.HashDataAsync(check, token).ConfigureAwait(false));
                good = string.Equals(md5, remote.Md5, StringComparison.OrdinalIgnoreCase);
            }
            if (!good)
            {
                try { File.Delete(partial); } catch { }
                state.Partials.Remove(path);
                throw new IOException("the downloaded copy did not match Google Drive's and will be fetched again");
            }

            File.SetLastWriteTimeUtc(partial, remote.ModifiedUtc);
            File.Move(partial, localPath, overwrite: true);
            state.Partials.Remove(path);

            return new SyncBase(remote.Size, remote.ModifiedUtc, remote.Size, remote.ModifiedUtc, remote.Id ?? "", remote.Md5);
        }

        // ---------------- state ----------------

        private static PairState LoadState(string pairId)
        {
            try
            {
                var path = StatePath(pairId);
                if (!File.Exists(path)) return new PairState();
                var state = JsonSerializer.Deserialize<PairState>(ProtectedFile.ReadAllText(path)) ?? new PairState();
                state.Files = new Dictionary<string, SyncBase>(state.Files ?? new(), StringComparer.OrdinalIgnoreCase);
                state.Partials = new Dictionary<string, PartialDownload>(state.Partials ?? new(), StringComparer.OrdinalIgnoreCase);
                state.Uploads = new Dictionary<string, UploadSession>(state.Uploads ?? new(), StringComparer.OrdinalIgnoreCase);
                return state;
            }
            catch
            {
                return new PairState();
            }
        }

        private static readonly object StateLock = new();

        private static void SaveState(string pairId, PairState state)
        {
            lock (StateLock)
            {
                try
                {
                    Directory.CreateDirectory(StateDirectory);
                    var path = StatePath(pairId);
                    var temp = path + "." + Environment.ProcessId + ".tmp";
                    ProtectedFile.WriteAllText(temp, JsonSerializer.Serialize(state));
                    File.Move(temp, path, overwrite: true);
                }
                catch { }
            }
        }

        /// <summary>Removes what was remembered for pairs that no longer exist.</summary>
        public static void ForgetRemovedPairs(IEnumerable<DriveSyncPair> pairs)
        {
            try
            {
                if (!Directory.Exists(StateDirectory)) return;
                var keep = new HashSet<string>(pairs.Select(p => p.Id + ".json"), StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.GetFiles(StateDirectory, "*.json"))
                    if (!keep.Contains(Path.GetFileName(file))) { try { File.Delete(file); } catch { } }
            }
            catch { }
        }

        public void Dispose()
        {
            try { _stop.Cancel(); } catch { }
            lock (_gate)
            {
                foreach (var watcher in _watchers.Values) { try { watcher.Dispose(); } catch { } }
                _watchers.Clear();
                _debounce?.Dispose();
                _debounce = null;
            }
            if (Current == this) Current = null;
        }
    }
}
