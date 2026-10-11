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
            DateTime? LastSyncedUtc, string? Problem, bool Running, long DoneBytes = 0, long TotalBytes = 0,
            int CheckDone = 0, int CheckTotal = 0);

        /// <summary>A download part way through, and which Drive file it is part of.</summary>
        public sealed record PartialDownload(string RemoteId, long Size, DateTime ModifiedUtc, string? Md5);

        /// <summary>
        /// An upload session part way through, which local file it is for, and
        /// the Drive file it updates in place (null when it makes a new one).
        /// </summary>
        public sealed record UploadSession(string Session, long Size, DateTime ModifiedUtc, DateTime StartedUtc,
            string? Replaces = null);

        /// <summary>A replaced Drive copy still to be trashed, and what it held when it was replaced.</summary>
        public sealed record PendingTrash(long Size, DateTime ModifiedUtc, string? Md5);

        private sealed class PairState
        {
            public Dictionary<string, SyncBase> Files { get; set; } = new(SyncNameComparer.Instance);
            public Dictionary<string, PartialDownload> Partials { get; set; } = new(SyncNameComparer.Instance);
            public Dictionary<string, UploadSession> Uploads { get; set; } = new(SyncNameComparer.Instance);

            /// <summary>PC files' MD5s, so none is hashed twice. See <see cref="SyncHashes"/>.</summary>
            public Dictionary<string, SyncHashEntry> Hashes { get; set; } = new(SyncNameComparer.Instance);

            /// <summary>
            /// Drive copies replaced by an upload whose trash did not go through,
            /// by id, with what each held. Tried again every pass, and trashed only
            /// while it still holds that; left alone if it was edited since.
            /// Ordinal: ids are case-sensitive.
            /// </summary>
            public Dictionary<string, PendingTrash> PendingTrash { get; set; } = new(StringComparer.Ordinal);

            /// <summary>Which two folders this state is about. Changing either starts it again.</summary>
            public string? LocalFolder { get; set; }
            public string? DriveFolderId { get; set; }

            public DateTime? LastSyncedUtc { get; set; }
            public string? Problem { get; set; }
        }

        /// <summary>
        /// What of a pair's saved state still applies to it. Editing its options
        /// keeps everything. Changing its PC folder or its Drive folder throws
        /// the sync records away (the next pass adopts what already matches, so
        /// nothing is copied again); the MD5s survive while the PC folder is the
        /// same. A state from before folders were recorded is taken as the pair's.
        /// </summary>
        internal static (bool KeepFiles, bool KeepHashes) StateFits(string? stateLocal, string? stateDrive, DriveSyncPair pair)
        {
            if (stateLocal == null && stateDrive == null) return (true, true);
            bool sameLocal = string.Equals(stateLocal?.TrimEnd('\\'), pair.LocalFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            bool sameDrive = string.Equals(stateDrive, pair.DriveFolderId, StringComparison.Ordinal);
            return (sameLocal && sameDrive, sameLocal);
        }

        private const int ChunkBytes = 8 * 1024 * 1024;

        /// <summary>Google keeps a resumable session about a week; older ones are not tried.</summary>
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(6);

        private readonly GoogleDrive _drive;
        private readonly Action<string, string> _notify;
        private readonly Action<List<DriveSyncPair>> _save;
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private CancellationTokenSource _cutWait = new();
        private readonly HashSet<string> _removed = new();
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
        private readonly Action<DriveState> _onConnection;

        /// <summary>Bumped whenever a pair's settings change, so a running pass of the old settings stops.</summary>
        private readonly Dictionary<string, int> _generation = new();

        /// <summary>PC paths this monitor has just written, moved or recycled, and when: their watcher events are its own.</summary>
        private readonly Dictionary<string, long> _ownWrites = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>When each pair's state was last written, for <see cref="SaveSoon"/>.</summary>
        private readonly Dictionary<string, long> _savedAt = new();

        private readonly HashSet<string> _loadingStatus = new();

        /// <summary>A file at least this big has its resume point saved at once; smaller ones within two seconds.</summary>
        private const long SaveAtOnceBytes = 64L * 1024 * 1024;

        /// <summary>How many Drive folders are listed at once.</summary>
        private const int ListStreams = 4;

        /// <summary>Raised on a worker thread whenever a pair's status changes.</summary>
        public event Action? Changed;

        // ---- for the suite only; every one of these is the real thing by default ----

        /// <summary>Multiplies every wait for Google, so the suite can wait "for ever" in milliseconds.</summary>
        internal double WaitScale { get; set; } = 1;

        /// <summary>Free and total bytes of the PC folder's volume, and its letter.</summary>
        internal Func<string, (long Free, long Total, string Letter)> DiskOfRoot { get; set; } = DiskOf;

        /// <summary>
        /// Sends a PC file to the Recycle Bin with no window of any kind: null
        /// when it went, otherwise why it was kept.
        /// </summary>
        internal Func<string, string?> RecycleLocal { get; set; } = ShellDelete.RecycleWithoutUi;

        /// <summary>
        /// Called at the moments a crash would hurt most: after something has
        /// happened in Drive or on disk and before the state says so. The suite
        /// copies the state file aside there and throws, which is what a crash
        /// leaves behind.
        /// </summary>
        internal Action<string>? KillPoint { get; set; }

        private void Kill(string what) => KillPoint?.Invoke(what);

        /// <summary>
        /// How long one file waits out Google's "not now" before it is counted
        /// as failed and the pass moves on. A daily limit is not counted: it is
        /// waited out however long it takes.
        /// </summary>
        internal TimeSpan PatienceLimit { get; set; } = TimeSpan.FromHours(1);

        /// <summary>A PC file modified this recently may still be being written, and waits for a later pass.</summary>
        internal TimeSpan SettleTime { get; set; } = TimeSpan.FromSeconds(10);

        public DriveMonitor(GoogleDrive drive, Action<string, string> notify, Action<List<DriveSyncPair>> save)
        {
            _drive = drive;
            _notify = notify;
            _save = save;
            Current = this;

            // Back online: everything changed meanwhile, on either side, is found
            // by comparing with the last sync, so every pair runs now rather
            // than on its timer. Kept, so Dispose can let go of the drive.
            _onConnection = state =>
            {
                if (state == DriveState.Online) SyncNow();
            };
            _drive.ConnectionChanged += _onConnection;

            _ = Task.Run(LoopAsync);
        }

        private int GenerationOf(string pairId)
        {
            lock (_gate) return _generation.TryGetValue(pairId, out var g) ? g : 0;
        }

        /// <summary>
        /// Marks a PC path as the monitor's own doing for a few seconds, so the
        /// watcher event it causes (a download landing, a copy set aside, a
        /// recycle) does not start another pass of the whole pair.
        /// </summary>
        private void Mine(string path)
        {
            try { path = Path.GetFullPath(path); } catch { return; }
            lock (_gate) _ownWrites[path] = Environment.TickCount64;
        }

        internal bool IsMine(string path)
        {
            try { path = Path.GetFullPath(path); } catch { return false; }
            long now = Environment.TickCount64;
            lock (_gate)
            {
                if (_ownWrites.Count > 256)
                    foreach (var old in _ownWrites.Where(kv => now - kv.Value > 5000).Select(kv => kv.Key).ToList())
                        _ownWrites.Remove(old);
                return _ownWrites.TryGetValue(path, out var at) && now - at <= 5000;
            }
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
                var before = _pairs;
                _pairs = pairs.Select(p => p.Copy()).ToList();
                foreach (var gone in before.Where(old => _pairs.All(p => p.Id != old.Id))) _removed.Add(gone.Id);
                foreach (var pair in _pairs) _removed.Remove(pair.Id);

                // An edited pair's running pass is of the old settings (another
                // folder, another direction); it stops at the next file.
                foreach (var pair in _pairs)
                    if (before.FirstOrDefault(old => old.Id == pair.Id) is { } old &&
                        JsonSerializer.Serialize(old) != JsonSerializer.Serialize(pair))
                        _generation[pair.Id] = (_generation.TryGetValue(pair.Id, out var g) ? g : 0) + 1;

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
                        //
                        // The monitor's own writes are not news: a download
                        // landing used to make the next pass due, which listed the
                        // whole of Drive again for nothing.
                        var id = pair.Id;
                        FileSystemEventHandler changed = (_, e) =>
                        {
                            if (!SyncPlanner.IsSkipped(e.Name ?? "") && !IsMine(e.FullPath)) Soon(id);
                        };
                        watcher.Created += changed;
                        watcher.Changed += changed;
                        watcher.Deleted += changed;
                        watcher.Renamed += (_, e) =>
                        {
                            if (!IsMine(e.FullPath)) Soon(id);
                        };
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

            // Reloaded first: a removed pair is marked removed before its state
            // file is forgotten, so a pass still running cannot write it back.
            Reload(copy);
            _save(copy);
        }

        /// <summary>
        /// Waits, at most <paramref name="max"/>, until a pair's pass is no
        /// longer running. True when it has stopped. A paused, edited or removed
        /// pair's pass stops at its next file, so this is normally quick.
        /// </summary>
        public async Task<bool> WaitForPassAsync(string pairId, TimeSpan max)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                lock (_gate) if (_running != pairId) return true;
                if (clock.Elapsed >= max) return false;
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        /// <summary>Every pair, now: Sync now, at start, Drive connecting, Drive back online.</summary>
        public void SyncNow()
        {
            lock (_gate) _allDue = true;
            CutWait();
            Wake();
        }

        /// <summary>
        /// Ends a wait for Google that is in progress. Patiently then asks again
        /// at once, or stops the pair if it has been paused or removed. The wait
        /// for a daily limit is an hour, and Sync now, Pause and closing the app
        /// all have to be heard during it.
        /// </summary>
        private void CutWait()
        {
            CancellationTokenSource old;
            lock (_gate)
            {
                old = _cutWait;
                _cutWait = new CancellationTokenSource();
            }
            try { old.Cancel(); } catch { }
        }

        /// <summary>Whether a pair has been paused or removed since its pass began.</summary>
        private bool Stopped(DriveSyncPair pair)
        {
            lock (_gate)
                return _removed.Contains(pair.Id) || _pairs.Any(p => p.Id == pair.Id && p.Paused);
        }

        /// <summary>A pair paused or removed while its pass was waiting for Google.</summary>
        private sealed class PairStoppedException : OperationCanceledException
        {
            public PairStoppedException() : base("the pair was paused") { }
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

        /// <summary>
        /// A pair's status for the window: what is already known, or null while
        /// its state file is read and decrypted on a worker (Changed is raised
        /// when it is in). The UI thread never reads the file.
        /// </summary>
        public PairStatus? StatusIfLoaded(string pairId)
        {
            lock (_gate)
            {
                if (_status.TryGetValue(pairId, out var known)) return known with { Running = _running == pairId };
                if (!_loadingStatus.Add(pairId)) return null;
            }
            _ = Task.Run(() =>
            {
                try { StatusOf(pairId); }
                catch { }
                finally { lock (_gate) _loadingStatus.Remove(pairId); }
                try { Changed?.Invoke(); } catch { }
            });
            return null;
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
                if (!await SyncOneAsync(client, pair, token).ConfigureAwait(false)) return;
            }
        }

        /// <summary>
        /// One pair, with everything a pass does about how it went. False when
        /// Drive has gone offline and the rest of the pass should wait.
        /// </summary>
        internal async Task<bool> SyncOneAsync(DriveClient client, DriveSyncPair pair, CancellationToken token)
        {
            {
                lock (_gate) _running = pair.Id;
                SetStatus(pair.Id, StatusOf(pair.Id) with { Running = true, DoneBytes = 0, TotalBytes = 0 });

                try
                {
                    await SyncPairAsync(client, pair, token).ConfigureAwait(false);
                    lock (_gate) _lastChecked[pair.Id] = DateTime.UtcNow;
                    _saidOffline = false;
                }
                catch (PairStoppedException)
                {
                    // Paused or removed while waiting for Google. What was done
                    // is saved; the wait's "waiting, ..." is no longer true.
                    Interlocked.Exchange(ref _waitingEpisode, 0);
                    lock (_gate) _running = null;
                    SetStatus(pair.Id, StatusOf(pair.Id) with { Problem = null, Running = false });
                }
                catch (Exception ex) when (IsOffline(ex, token) && !SyncBackoff.IsTransient(ex))
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
                    return false;
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
            return true;
        }

        private int _waitingEpisode;

        /// <summary>
        /// Runs a Drive request, waiting out "not now" from Google for as long
        /// as it lasts: a rate limit or a server error is retried with growing,
        /// jittered waits capped at five minutes (Google's Retry-After honoured),
        /// and never fails or skips a file. The pair says it is waiting, and is
        /// announced once per episode and again when Google answers.
        ///
        /// Not for ever, though: one request refused for longer than
        /// <see cref="PatienceLimit"/> (a file Google will not take, and a
        /// server error that never clears) held up every file and every pair
        /// behind it. Past the limit it fails, so that file counts as failed and
        /// the pass moves on. Waits for a daily limit do not count towards it.
        /// </summary>
        private async Task<T> Patiently<T>(DriveSyncPair pair, Func<Task<T>> work, DriveClient client, CancellationToken token)
        {
            int attempt = 0;
            var random = new Random();
            var waited = TimeSpan.Zero;
            while (true)
            {
                try
                {
                    var result = await work().ConfigureAwait(false);
                    if (attempt > 0) Resumed(pair);
                    return result;
                }
                catch (Exception ex) when (!token.IsCancellationRequested && SyncBackoff.IsTransient(ex))
                {
                    var (asked, at) = client.LastRetryAfter;
                    var retryAfter = Environment.TickCount64 - at < 15_000 ? asked : null;
                    var wait = SyncBackoff.Delay(attempt++, retryAfter, random.NextDouble());
                    bool daily = SyncBackoff.IsDailyLimit(ex);
                    if (daily && wait < SyncBackoff.DailyLimitWait) wait = SyncBackoff.DailyLimitWait;
                    bool limited = ex.ToString().Contains("429") || ex.ToString().Contains("ateLimitExceeded") ||
                                   Limited(ex);
                    var why = daily ? "Google's daily limit for this account has been reached"
                        : limited ? "Google is limiting requests"
                        : "Google Drive is having trouble";
                    if (!daily)
                    {
                        if (waited >= PatienceLimit)
                        {
                            // The reason in words, not Google's text: that names
                            // the status, and would read as "not now" again.
                            Interlocked.Exchange(ref _waitingEpisode, 0);
                            SetStatus(pair.Id, StatusOf(pair.Id) with { Problem = null });
                            throw new GaveUpException($"given up after waiting {Seconds(waited)}, {why}");
                        }
                        waited += wait;
                    }
                    SetStatus(pair.Id, StatusOf(pair.Id) with
                    {
                        Problem = $"waiting, {why}, trying again in {Seconds(wait)}",
                    });
                    if (Interlocked.Exchange(ref _waitingEpisode, 1) == 0)
                        _notify("sync.ratelimited", $"{DisplayName(pair)}: waiting, {why}");

                    // Taken before the pair is checked, so a pause that lands in
                    // between still cuts this wait short.
                    CancellationToken cut;
                    lock (_gate) cut = _cutWait.Token;
                    if (Stopped(pair)) throw new PairStoppedException();

                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cut, _stop.Token);
                    try
                    {
                        await Task.Delay(WaitScale == 1 ? wait : TimeSpan.FromTicks((long)(wait.Ticks * WaitScale)),
                            linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        // Closing the app, pausing or removing the pair: stop.
                        // Sync now: ask Google again straight away.
                        if (_stop.IsCancellationRequested) throw;
                        if (Stopped(pair)) throw new PairStoppedException();
                    }
                }
            }
        }

        /// <summary>
        /// Google said "not now" for longer than <see cref="PatienceLimit"/>.
        /// Carries no inner exception on purpose: the transient one inside would
        /// make it read as "not now" again and be waited out once more.
        /// </summary>
        private sealed class GaveUpException : Exception
        {
            public GaveUpException(string message) : base(message) { }
        }

        /// <summary>A 429, or a 403 whose reason is a rate limit, anywhere in the chain.</summary>
        private static bool Limited(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e is DriveClient.DriveStatusException s &&
                    (s.Status == 429 || s.Reason is "rateLimitExceeded" or "userRateLimitExceeded"))
                    return true;
            return false;
        }

        private void Resumed(DriveSyncPair pair)
        {
            SetStatus(pair.Id, StatusOf(pair.Id) with { Problem = null });
            if (Interlocked.Exchange(ref _waitingEpisode, 0) == 1)
                _notify("sync.ratelimited", $"{DisplayName(pair)}: Google is answering again, carrying on");
        }

        private static string Seconds(TimeSpan wait)
        {
            int s = Math.Max(1, (int)Math.Round(wait.TotalSeconds));
            return s < 90 ? $"{s} seconds" : $"{(int)Math.Round(s / 60.0)} minutes";
        }

        /// <summary>
        /// Works out the MD5 of each PC file the plan needs one for, two at a
        /// time, remembering each in the encrypted state so it is never worked
        /// out again. Big checks are announced like big passes, and the pair
        /// says how far it has got.
        ///
        /// No thread priority: set on a pool thread it stayed with whatever ran
        /// there after the await, and the hashing itself is asynchronous I/O.
        /// Pausing, editing or removing the pair stops it at the next file.
        /// </summary>
        private async Task<Dictionary<string, string>> CheckWhatIsThereAsync(
            DriveSyncPair pair, PairState state, SyncRules rules,
            Dictionary<string, SyncFile> local, Dictionary<string, SyncFile> remote, int generation,
            CancellationToken token)
        {
            var wanted = SyncPlanner.NeedingHash(rules, local, remote, state.Files);
            var (known, toHash) = SyncHashes.Split(wanted, local, state.Hashes);
            if (toHash.Count == 0) return known;

            var name = DisplayName(pair);
            long bytes = toHash.Sum(p => local[p].Size);
            bool big = SyncSpace.IsBig(bytes, toHash.Count);
            if (big) _notify("sync.checking", $"{name}: checking what is already there, {toHash.Count:N0} files");

            int done = 0;
            var gate = new object();
            var saved = System.Diagnostics.Stopwatch.StartNew();
            void Progress()
            {
                lock (_gate)
                    if (_status.TryGetValue(pair.Id, out var was))
                        _status[pair.Id] = was with { CheckDone = done, CheckTotal = toHash.Count };
                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastProgressEvent) < 500) return;
                Interlocked.Exchange(ref _lastProgressEvent, now);
                try { Changed?.Invoke(); } catch { }
            }
            Progress();

            try
            {
                await Parallel.ForEachAsync(toHash, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token },
                    async (path, ct) =>
                    {
                        if (Stopped(pair) || GenerationOf(pair.Id) != generation) throw new PairStoppedException();
                        try
                        {
                            var full = Path.Combine(pair.LocalFolder, path.Replace('/', Path.DirectorySeparatorChar));
                            string md5;
                            await using (var file = new FileStream(full, FileMode.Open, FileAccess.Read,
                                             FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous))
                                md5 = Convert.ToHexString(await MD5.HashDataAsync(file, ct).ConfigureAwait(false)).ToLowerInvariant();

                            int before, after;
                            lock (gate)
                            {
                                known[path] = md5;
                                state.Hashes[path] = new SyncHashEntry(local[path].Size, local[path].ModifiedUtc, md5);
                                before = done;
                                after = ++done;
                                if (saved.ElapsedMilliseconds > 2000)
                                {
                                    SaveState(pair.Id, state);
                                    saved.Restart();
                                }
                            }
                            Progress();
                            if (big && SyncSpace.QuarterPassed(before, after, toHash.Count) is int percent and > 0)
                                _notify("sync.checking", $"{name}: {percent} percent checked");
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // Unreadable now (open in another program): not adopted,
                            // and not copied this pass either, since its hash is unknown.
                            lock (gate) done++;
                        }
                    }).ConfigureAwait(false);
            }
            finally
            {
                lock (gate) SaveState(pair.Id, state);
                lock (_gate)
                    if (_status.TryGetValue(pair.Id, out var was))
                        _status[pair.Id] = was with { CheckDone = 0, CheckTotal = 0 };
            }

            if (big) _notify("sync.checking", $"{name}: finished checking what is already there");
            return known;
        }

        private static bool IsOffline(Exception ex, CancellationToken token)
        {
            if (token.IsCancellationRequested) return false;
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is SocketException or TaskCanceledException or TimeoutException) return true;

                // Only one that never got an answer. One with a status is
                // Google's answer — a 403 rate limit among them, which is waited
                // out, not mistaken for the connection going.
                if (e is HttpRequestException { StatusCode: null }) return true;
            }
            return false;
        }

        /// <summary>Thrown when the connection is found to be down part way through a pass.</summary>
        private sealed class OfflineException : HttpRequestException
        {
            public OfflineException(Exception inner) : base("Google Drive could not be reached", inner) { }
        }

        private async Task SyncPairAsync(DriveClient client, DriveSyncPair pair, CancellationToken token)
        {
            int generation = GenerationOf(pair.Id);
            var name = DisplayName(pair);
            var (state, read) = LoadState(pair);

            // A state file that is there but cannot be read is not a first sync.
            // Taken for one, every record was gone and the pair started again
            // with no word said. Kept aside, said, and the pair paused: unpaused,
            // it compares both sides afresh, which adopts what matches and
            // deletes nothing.
            if (read == StateRead.Busy)
                throw new IOException("its sync records could not be opened just now, and will be tried again");
            if (read == StateRead.Damaged)
            {
                SetDamagedStateAside(pair, state);
                return;
            }

            var root = pair.LocalFolder;

            // A root that has gone (a USB drive unplugged, the folder removed in
            // Windows Explorer, the Drive folder trashed on the website) must
            // never read as "every file in it was deleted". A trashed Drive
            // folder even lists as empty. So both roots are checked before
            // anything is listed, and a missing one pauses the pair until it
            // is back; nothing is planned, so nothing can be deleted.
            bool localThere = Directory.Exists(root);
            bool driveThere = await Patiently(pair, () => client.FolderAlive(pair.DriveFolderId, token), client, token)
                .ConfigureAwait(false);
            if (SyncSafety.RootProblem(localThere, driveThere) is string gone)
            {
                bool newProblem = gone != state.Problem;
                state.Problem = gone;
                SaveState(pair.Id, state);
                SetStatus(pair.Id, new PairStatus(state.LastSyncedUtc, gone, false));
                if (newProblem) _notify("sync.paused", $"{name} {gone}");
                return;
            }

            string? refuse = Refusal(root);
            if (refuse != null)
            {
                Finish(pair, state, refuse, announce: true);
                return;
            }

            // Old Drive copies an earlier replace could not trash, before Drive
            // is listed, so the listing does not hold them as well.
            await TrashPendingAsync(client, pair, state, token).ConfigureAwait(false);

            // What either side holds and leaves out: never planned, so never
            // taken for deleted and never forgotten.
            var skips = new SyncSkips();
            var localClashing = new List<string>();
            var local = ScanLocal(root, pair.IncludeSubfolders, skips, localClashing);
            var unusable = new List<string>();
            var clashing = new List<string>();
            var shadowed = new Dictionary<string, List<SyncFile>>(SyncNameComparer.Instance);
            var remoteSkips = new SyncSkips();
            var (remote, folders) = await Patiently(pair,
                () =>
                {
                    unusable.Clear();
                    clashing.Clear();
                    shadowed.Clear();
                    remoteSkips = new SyncSkips();
                    return ScanRemoteAsync(client, pair.DriveFolderId, pair.IncludeSubfolders, token, unusable, shadowed,
                        clashing, remoteSkips, WaitScale);
                }, client, token)
                .ConfigureAwait(false);
            skips.Include(remoteSkips);

            // A download of a name that clashes left its part behind, and it
            // would be continued for ever. The clash is said instead.
            foreach (var path in state.Partials.Keys.Where(remoteSkips.Covers).ToList())
            {
                var part = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)) + ".partial";
                try { if (IsInside(root, part)) File.Delete(part); } catch { }
                state.Partials.Remove(path);
            }

            // Files already on both sides with nothing remembered about them (a
            // first sync over two copies of one library) are compared by MD5
            // before anything is planned, so a match is adopted, never copied.
            var rules = SyncRules.For(pair);
            var md5 = await CheckWhatIsThereAsync(pair, state, rules, local, remote, generation, token).ConfigureAwait(false);
            var plan = SyncPlanner.Ordered(SyncPlanner.Plan(rules, local, remote, state.Files, md5, skips), local, remote);

            // A download held back because the PC's file changed under it waits
            // in its ".partial". Once the plan decides that path another way (a
            // conflict, an upload) or Drive's file has gone, the part is let go.
            var decided = new HashSet<string>(plan
                .Where(a => a.Kind is not (SyncActionKind.Download or SyncActionKind.KeepBothRemoteWins))
                .Select(a => a.Path), SyncNameComparer.Instance);
            foreach (var path in state.Partials.Keys.Where(p => !remote.ContainsKey(p) || decided.Contains(p)).ToList())
            {
                var part = LocalPathFor(root, path) + ".partial";
                try { if (IsInside(root, part)) File.Delete(part); } catch { }
                state.Partials.Remove(path);
            }

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

            int uploaded = 0, downloaded = 0, deleted = 0, failed = 0, deferred = 0;
            string? lastError = null, spaceProblem = null;
            var conflicts = new List<string>();
            long uploadedThisPass = 0;

            var listings = new Dictionary<string, List<(string Name, bool Folder)>>(StringComparer.Ordinal);
            try
            {
                for (int i = 0; i < plan.Count; i++)
                {
                    var action = plan[i];
                    token.ThrowIfCancellationRequested();

                    // Pause, Remove and Edit are heard between files, not only
                    // while waiting for Google: a pass of hundreds of gigabytes
                    // used to carry on to the end under settings that had gone.
                    if (Stopped(pair) || GenerationOf(pair.Id) != generation) throw new PairStoppedException();
                    SaveSoon(pair.Id, state);

                    var path = action.Path;
                    var localPath = LocalPathFor(root, path, listings);
                    local.TryGetValue(path, out var scannedLocal);
                    SyncFile? onPc = local.ContainsKey(path) ? scannedLocal : null;

                    // Nothing is written, moved or deleted outside the pair's own
                    // folder, whatever a name turned out to say. The scan already
                    // leaves out names Windows cannot use; this is the backstop.
                    if ((action.Kind is SyncActionKind.Download or SyncActionKind.DeleteLocal
                            or SyncActionKind.KeepBothRemoteWins) &&
                        !IsInside(root, localPath))
                    {
                        failed++;
                        lastError = $"{path}: its name would put it outside {root}, so it was left alone";
                        continue;
                    }

                    // A file still being written goes up on a later pass. Sent
                    // now, it went up half written, or failed with a size that
                    // no longer matched and read as the connection going.
                    if (action.Kind is SyncActionKind.Upload or SyncActionKind.KeepBothLocalWins &&
                        StillBeingWritten(localPath, scannedLocal, DateTime.UtcNow, SettleTime))
                    {
                        // Only a file that was just modified brings the pair back
                        // within a minute. One that is simply held open (a mail
                        // store, a database) waits for the pair's own interval:
                        // otherwise it re-ran the whole pass every minute for ever.
                        var age = DateTime.UtcNow - scannedLocal.ModifiedUtc;
                        if (age >= TimeSpan.Zero && age < SettleTime) deferred++;
                        continue;
                    }

                    // Room before each file: the PC's disk keeps its margin, and
                    // Drive is never asked to hold more than its quota.
                    if (action.Kind is SyncActionKind.Download or SyncActionKind.KeepBothRemoteWins)
                    {
                        long need = remote[path].Size - PartialLength(state, path, localPath, remote[path]);
                        var (free, volume, letter) = DiskOfRoot(root);
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

                    // The PC's copy steps aside once, before Drive is asked for
                    // anything. Inside the retried part, a 503 on the download
                    // moved it again from where it no longer was, and the
                    // conflict failed with "Could not find file".
                    if (action.Kind == SyncActionKind.KeepBothRemoteWins)
                    {
                        // Only the file the scan saw: one saved since is a newer
                        // edit, and the next pass decides about it.
                        if (!LocalStillAsScanned(localPath, onPc))
                        {
                            deferred++;
                            continue;
                        }
                        try
                        {
                            var aside = SyncPlanner.ConflictName(path, DateTime.Now, p => remote.ContainsKey(p) || local.ContainsKey(p));
                            var asidePath = Path.Combine(root, aside.Replace('/', Path.DirectorySeparatorChar));
                            Mine(localPath);
                            Mine(asidePath);
                            File.Move(localPath, asidePath);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            failed++;
                            lastError = $"{Path.GetFileName(path)}: {ex.Message}";
                            continue;
                        }
                        Kill("set aside on the PC " + path);
                        onPc = null;
                    }

                    // Once more when a failure looked like the connection but
                    // Drive answered straight after: a dropped request is not a
                    // reason to end the pass for every pair.
                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
                            await Patiently(pair, async () =>
                            {
                            switch (action.Kind)
                            {
                                case SyncActionKind.Upload:
                                {
                                    remote.TryGetValue(path, out var old);
                                    string? replaces = null;
                                    if (old.Id != null)
                                    {
                                        // Replaced in place, and only while Drive's copy
                                        // still holds what the scan saw. Edited in Drive
                                        // since, it is a conflict for the next pass to see.
                                        var now = await client.FileNow(old.Id, token).ConfigureAwait(false);
                                        if (now != null && !SameAsScanned(now, old))
                                        {
                                            deferred++;
                                            break;
                                        }
                                        replaces = now?.Id;
                                    }
                                    var sent = await UploadAsync(client, folders, pair, state, path, localPath,
                                        local[path], replaces, old, Moved, token).ConfigureAwait(false);
                                    Kill("uploaded " + path);
                                    state.Files[path] = sent;
                                    uploadedThisPass += local[path].Size;
                                    uploaded++;
                                    break;
                                }

                                case SyncActionKind.Download:
                                {
                                    // A PC file that changed since the last sync (or was
                                    // never synced) is not lost under Drive's copy: it
                                    // goes to the Recycle Bin first. Download only and
                                    // "Google Drive's copy wins" overwrote it for good.
                                    bool keepOld = onPc is { } mine &&
                                                   (!state.Files.TryGetValue(path, out var rec) || SyncPlanner.LocalChanged(mine, rec));
                                    var got = await DownloadAsync(client, pair.Id, state, path, remote[path],
                                        localPath, onPc, keepOld, Moved, token).ConfigureAwait(false);
                                    if (got == null)
                                    {
                                        deferred++;
                                        break;
                                    }
                                    Kill("downloaded " + path);
                                    state.Files[path] = got;
                                    downloaded++;
                                    break;
                                }

                                case SyncActionKind.DeleteRemote:
                                {
                                    // Trashed only while it still holds what the scan
                                    // saw: an edit made in Drive since is not thrown
                                    // away for a delete made on the PC before it.
                                    var now = await client.FileNow(remote[path].Id!, token).ConfigureAwait(false);
                                    if (now == null)
                                    {
                                        state.Files.Remove(path);
                                        break;
                                    }
                                    if (!SameAsScanned(now, remote[path]))
                                    {
                                        deferred++;
                                        break;
                                    }
                                    await client.Trash(remote[path].Id!, token).ConfigureAwait(false);
                                    Kill("trashed " + path);
                                    state.Files.Remove(path);
                                    deleted++;
                                    break;
                                }

                                case SyncActionKind.DeleteLocal:
                                {
                                    // The same on the PC: a file saved again since the
                                    // scan is a new edit, not the one Drive deleted.
                                    if (!LocalStillAsScanned(localPath, onPc))
                                    {
                                        deferred++;
                                        break;
                                    }

                                    // Never a window: the monitor runs with nobody
                                    // watching. A file that could only be deleted for
                                    // good (no Recycle Bin on that drive) is kept and
                                    // said, and stays remembered so it is not sent
                                    // back up as new.
                                    Mine(localPath);
                                    var kept = RecycleLocal(localPath);
                                    if (kept != null)
                                    {
                                        failed++;
                                        lastError = $"{Path.GetFileName(path)} was kept: {kept}";
                                        break;
                                    }
                                    Kill("recycled " + path);
                                    state.Files.Remove(path);
                                    deleted++;
                                    break;
                                }

                                case SyncActionKind.KeepBothLocalWins:
                                {
                                    // Drive's copy steps aside under the conflict name; the PC's goes up.
                                    var aside = SyncPlanner.ConflictName(path, DateTime.Now, p => remote.ContainsKey(p) || local.ContainsKey(p));
                                    await client.MoveOrRename(remote[path].Id!, Path.GetFileName(aside), null, null, token).ConfigureAwait(false);
                                    Kill("set aside in Drive " + path);
                                    var sent = await UploadAsync(client, folders, pair, state, path, localPath,
                                        local[path], null, null, Moved, token).ConfigureAwait(false);
                                    Kill("uploaded " + path);
                                    state.Files[path] = sent;
                                    uploadedThisPass += local[path].Size;
                                    conflicts.Add(Path.GetFileName(path));
                                    uploaded++;
                                    break;
                                }

                                case SyncActionKind.KeepBothRemoteWins:
                                {
                                    // The PC's copy has stepped aside (above); Drive's comes down.
                                    var got = await DownloadAsync(client, pair.Id, state, path, remote[path],
                                        localPath, null, false, Moved, token).ConfigureAwait(false);
                                    if (got == null)
                                    {
                                        deferred++;
                                        break;
                                    }
                                    Kill("downloaded " + path);
                                    state.Files[path] = got;
                                    conflicts.Add(Path.GetFileName(path));
                                    downloaded++;
                                    break;
                                }

                                case SyncActionKind.Record:
                                    // A replacement that went up and was then cut off
                                    // before the old copy went to the trash: the old
                                    // copy is the very file this pair last synced, now
                                    // hidden behind a newer one of the same name that
                                    // matches the PC. Finished as the replace would have.
                                    //
                                    // Only while it still holds what was synced, asked
                                    // of Drive just before. Edited in Drive since, it is
                                    // an edit the PC never had, and it stays where it is.
                                    if (state.Files.TryGetValue(path, out var was) &&
                                        was.RemoteId != remote[path].Id &&
                                        shadowed.TryGetValue(path, out var hidden) &&
                                        hidden.FirstOrDefault(h => h.Id == was.RemoteId) is { Id: not null } synced &&
                                        StillAsSynced(synced, was) &&
                                        await client.FileNow(was.RemoteId, token).ConfigureAwait(false) is { } stillThere &&
                                        SameAsScanned(stillThere, synced))
                                        await client.Trash(was.RemoteId, token).ConfigureAwait(false);
                                    state.Files[path] = new SyncBase(local[path].Size, local[path].ModifiedUtc,
                                        remote[path].Size, remote[path].ModifiedUtc, remote[path].Id ?? "", remote[path].Md5);
                                    break;

                                case SyncActionKind.Forget:
                                    state.Files.Remove(path);
                                    break;
                            }
                            return true;
                            }, client, token).ConfigureAwait(false);
                            break;
                        }
                        catch (Exception ex) when (!token.IsCancellationRequested &&
                                                   (ex is not OperationCanceledException || IsOffline(ex, token)) &&
                                                   ex is not DriveQuotaException &&
                                                   !SyncBackoff.IsTransient(ex))
                        {
                            // A failure that might really be the connection going:
                            // asked of Drive itself, and if Drive cannot be reached
                            // the pass stops here and carries on when it is back.
                            // Any other answer, even a refusal, means it is there.
                            try { await client.RootId(token).ConfigureAwait(false); }
                            catch (Exception probe) when (IsOffline(probe, token)) { throw new OfflineException(probe); }
                            catch (Exception) when (!token.IsCancellationRequested) { }

                            // A PC file that changed under the upload is simply
                            // sent on a later pass, not counted as failed.
                            if (action.Kind is SyncActionKind.Upload or SyncActionKind.KeepBothLocalWins &&
                                !LocalStillAsScanned(localPath, onPc))
                            {
                                deferred++;
                                break;
                            }

                            if (IsOffline(ex, token) && attempt == 0) continue;

                            failed++;
                            lastError = $"{Path.GetFileName(path)}: {ex.Message}";
                            break;
                        }
                    }
                }
            }
            finally
            {
                // Everything done so far is remembered even if the connection
                // dropped part way. What was not done is found again next time.
                SaveState(pair.Id, state);

                // Something left for later (a file still being written, an edit
                // made after the scan) is looked at again within a minute,
                // whatever the pair's interval.
                if (deferred > 0) lock (_gate) _due.Add(pair.Id);
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

            // Everything else is in step; these few never can be, and say so
            // rather than failing on every pass or landing somewhere else.
            if (unusable.Count > 0 || clashing.Count > 0 || localClashing.Count > 0)
            {
                var left = new List<string>();
                if (unusable.Count > 0) left.Add(UnusableNames(unusable));
                if (clashing.Count > 0) left.Add(ClashingNames(clashing));
                if (localClashing.Count > 0) left.Add(ClashingOnPc(localClashing));
                Finish(pair, state, string.Join(". ", left), announce: true, synced: true);
                return;
            }

            Finish(pair, state, null, announce: false);
        }

        /// <summary>"x in Google Drive was left out, because Windows sees another item in that folder as the same name".</summary>
        internal static string ClashingNames(IReadOnlyList<string> names)
        {
            const int shown = 5;
            var list = string.Join(", ", names.Take(shown)) + (names.Count > shown ? $" and {names.Count - shown} more" : "");
            return names.Count == 1
                ? $"{names[0]} in Google Drive was left out, because Windows sees another item in that folder as the same name"
                : $"{names.Count} items in Google Drive were left out, because Windows sees other items in their folders " +
                  $"as the same names: {list}";
        }

        /// <summary>
        /// "café.txt on the PC was left out, because another item in that folder
        /// has the same name with its accents written differently".
        /// </summary>
        internal static string ClashingOnPc(IReadOnlyList<string> names)
        {
            const int shown = 5;
            var list = string.Join(", ", names.Take(shown)) + (names.Count > shown ? $" and {names.Count - shown} more" : "");
            return names.Count == 1
                ? $"{names[0]} on the PC was left out, because another item in that folder has the same name " +
                  "written a different way"
                : $"{names.Count} items on the PC were left out, because Google Drive would see other items in " +
                  $"their folders as the same names: {list}";
        }

        /// <summary>"2 files in Google Drive were left out, because Windows cannot use their names: a:b, x\y".</summary>
        internal static string UnusableNames(IReadOnlyList<string> names)
        {
            const int shown = 5;
            var list = string.Join(", ", names.Take(shown)) + (names.Count > shown ? $" and {names.Count - shown} more" : "");
            return names.Count == 1
                ? $"{names[0]} in Google Drive was left out, because Windows cannot use its name"
                : $"{names.Count} items in Google Drive were left out, because Windows cannot use their names: {list}";
        }

        /// <summary>Whether a path resolves to somewhere inside the pair's PC folder.</summary>
        internal static bool IsInside(string root, string path)
        {
            try
            {
                var top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(path);
                return full.StartsWith(top, StringComparison.OrdinalIgnoreCase) && full.Length > top.Length;
            }
            catch
            {
                return false;
            }
        }

        private void Finish(DriveSyncPair pair, PairState state, string? problem, bool announce, bool? synced = null)
        {
            bool newProblem = problem != null && problem != state.Problem;
            state.Problem = problem;
            if (synced ?? problem == null) state.LastSyncedUtc = DateTime.UtcNow;
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
            var skips = new SyncSkips();
            var local = Directory.Exists(pair.LocalFolder)
                ? ScanLocal(pair.LocalFolder, pair.IncludeSubfolders, skips)
                : new Dictionary<string, SyncFile>(SyncNameComparer.Instance);
            var (remote, _) = await ScanRemoteAsync(client, pair.DriveFolderId, pair.IncludeSubfolders, token,
                skipped: skips).ConfigureAwait(false);

            var plan = SyncPlanner.Plan(SyncRules.For(pair), local, remote,
                new Dictionary<string, SyncBase>(SyncNameComparer.Instance), null, skips);
            long down = 0, up = 0;
            foreach (var a in plan)
            {
                if (a.Kind is SyncActionKind.Download or SyncActionKind.KeepBothRemoteWins) down += remote[a.Path].Size;
                if (a.Kind is SyncActionKind.Upload or SyncActionKind.KeepBothLocalWins) up += local[a.Path].Size;
            }
            return (down, up);
        }

        // ---------------- scanning ----------------

        /// <summary>
        /// Every file under the PC folder that takes part, by relative path.
        ///
        /// What is there and left out — hidden or system files, everything in
        /// a hidden, system or unreadable folder, a file whose details
        /// could not be read — goes in <paramref name="skipped"/>. Simply not
        /// listed, it looked deleted: with deletes on, marking a file Hidden
        /// sent its Drive copy to the trash.
        /// </summary>
        /// <remarks>
        /// NTFS holds "café.txt" with the accent as one character and as two
        /// side by side, and Drive's comparer makes them one name. Kept, the
        /// second overwrote the first in this table: one entry with one's name
        /// and the other's size, one file uploaded, nothing said, and two
        /// folders merged the same way. So every spelling of such a name is
        /// left out, recorded in <paramref name="skipped"/> (nothing is planned
        /// for it on either side, so nothing is deleted in Drive for it) and
        /// listed in <paramref name="clashing"/> to be said.
        /// </remarks>
        internal static Dictionary<string, SyncFile> ScanLocal(string root, bool subfolders, SyncSkips? skipped = null,
            List<string>? clashing = null)
        {
            var files = new Dictionary<string, SyncFile>(SyncNameComparer.Instance);
            var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 };
            const FileAttributes leftOut = FileAttributes.Hidden | FileAttributes.System;

            var pending = new Stack<(DirectoryInfo Folder, string Relative)>();
            pending.Push((new DirectoryInfo(root), ""));

            while (pending.Count > 0)
            {
                var (folder, under) = pending.Pop();

                // The pair's own folder unreadable is a failed pass, never an
                // empty one; a subfolder is left out as a whole.
                List<FileSystemInfo> entries;
                try { entries = folder.EnumerateFileSystemInfos("*", options).ToList(); }
                catch (Exception) when (under.Length > 0)
                {
                    skipped?.Folder(under);
                    continue;
                }

                // Every spelling here of each name, as Drive's comparer sees it.
                var spellings = new Dictionary<string, HashSet<string>>(SyncNameComparer.Instance);
                foreach (var entry in entries)
                {
                    if (entry is DirectoryInfo && !subfolders) continue;
                    if (!spellings.TryGetValue(entry.Name, out var set))
                        spellings[entry.Name] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(entry.Name);
                }

                foreach (var entry in entries)
                {
                    var relative = under.Length == 0 ? entry.Name : under + "/" + entry.Name;
                    FileAttributes attributes;
                    try { attributes = entry.Attributes; }
                    catch { skipped?.File(relative); skipped?.Folder(relative); continue; }

                    // Two spellings of one name: both left out, file or folder.
                    bool isFolder = (attributes & FileAttributes.Directory) != 0;
                    if (!(isFolder && !subfolders) &&
                        spellings.TryGetValue(entry.Name, out var all) && all.Count > 1)
                    {
                        if (isFolder) skipped?.Folder(relative);
                        else skipped?.File(relative);
                        clashing?.Add(relative + (isFolder ? "/" : ""));
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!subfolders) continue;

                        // Not walked, as before — and now known to be there.
                        if ((attributes & leftOut) != 0)
                        {
                            skipped?.Folder(relative);
                            continue;
                        }
                        pending.Push(((DirectoryInfo)entry, relative));
                        continue;
                    }

                    if ((attributes & leftOut) != 0)
                    {
                        skipped?.File(relative);
                        continue;
                    }

                    // The planner leaves these out on both sides by name.
                    if (SyncPlanner.IsSkippedPath(relative)) continue;

                    try
                    {
                        var info = (FileInfo)entry;
                        files[relative] = new SyncFile(info.Length, Truncate(info.LastWriteTimeUtc));
                    }
                    catch { skipped?.File(relative); }
                }
            }

            return files;
        }

        /// <summary>
        /// Everything under the Drive folder, by relative path.
        ///
        /// A Drive name is anything at all: "a:b", "x\y", "AC/DC", "trail.",
        /// "..", "CON". On Windows those either land somewhere else (a backslash
        /// or a slash makes a folder, ".." climbs out) or are quietly changed (a
        /// trailing dot or space is dropped), and then the PC's file never
        /// matches Drive's under one name: it fails on every pass, or worse, the
        /// PC's copy reads as Drive's having been deleted and the original is
        /// trashed. So such a name is left out, with its whole folder when it is
        /// a folder, and listed in <paramref name="unusable"/> to be said.
        /// </summary>
        /// <summary>
        /// A name Windows cannot hold beside another in one folder — a file and
        /// a folder called "x" and "X", or two names that differ only in
        /// capitals or in how an accent is written — is left out with all its
        /// spellings, listed in <paramref name="clashing"/> and added to
        /// <paramref name="skipped"/>. The file beside a folder of its name
        /// failed on every pass and left its ".partial" behind; the second
        /// folder was dropped without a word. Two Drive folders of exactly one
        /// name are a clash too: the second used to be dropped, and with
        /// deletes on, what was in it read as deleted.
        ///
        /// Listed a level at a time, <see cref="ListStreams"/> folders at once
        /// and each tried again by itself a few times, so one folder's hiccup
        /// does not start the whole listing again. Results are taken in the
        /// order a one-at-a-time walk would take them.
        /// </summary>
        internal static async Task<(Dictionary<string, SyncFile> Files, Dictionary<string, string> Folders)> ScanRemoteAsync(
            DriveClient client, string rootId, bool subfolders, CancellationToken token, List<string>? unusable = null,
            Dictionary<string, List<SyncFile>>? shadowed = null, List<string>? clashing = null, SyncSkips? skipped = null,
            double waitScale = 1)
        {
            var files = new Dictionary<string, SyncFile>(SyncNameComparer.Instance);
            var folders = new Dictionary<string, string>(SyncNameComparer.Instance) { [""] = rootId };
            var level = new List<(string Id, string Relative)> { (rootId, "") };

            while (level.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var listed = new List<DriveEntry>[level.Count];
                await Parallel.ForEachAsync(Enumerable.Range(0, level.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = ListStreams, CancellationToken = token },
                    async (n, ct) => listed[n] = await ListOneFolder(client, level[n].Id, waitScale, ct).ConfigureAwait(false))
                    .ConfigureAwait(false);

                var next = new List<(string Id, string Relative)>();
                for (int n = 0; n < level.Count; n++)
                    Take(level[n].Relative, listed[n], next);
                level = next;
            }

            return (files, folders);

            void Take(string relative, List<DriveEntry> children, List<(string Id, string Relative)> next)
            {
                // Every spelling of each file name here that would take part, to
                // find the ones Windows would make into one, and how many folders
                // hold each name. The very same file name twice is not a clash:
                // Drive allows it, and the newer file counts (see below).
                var fileSpellings = new Dictionary<string, HashSet<string>>(SyncNameComparer.Instance);
                var folderCount = new Dictionary<string, int>(SyncNameComparer.Instance);
                foreach (var entry in children)
                {
                    if (entry.IsGoogleDocument && !entry.IsFolder) continue;
                    if (!NameRules.IsUsableName(entry.Name) || entry.Name.Length > 255) continue;
                    if (entry.IsFolder)
                    {
                        if (subfolders) folderCount[entry.Name] = folderCount.TryGetValue(entry.Name, out var c) ? c + 1 : 1;
                        continue;
                    }
                    if (!fileSpellings.TryGetValue(entry.Name, out var set))
                        fileSpellings[entry.Name] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(entry.Name);
                }

                var said = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in children)
                {
                    var path = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;

                    // Docs, Sheets and Slides are not files here whatever they are called.
                    if (!entry.IsFolder && entry.IsGoogleDocument) continue;
                    if (!NameRules.IsUsableName(entry.Name) || entry.Name.Length > 255)
                    {
                        if (entry.IsFolder && !subfolders) continue;
                        unusable?.Add(path + (entry.IsFolder ? "/" : ""));
                        continue;
                    }
                    if (entry.IsFolder && !subfolders) continue;

                    // A folder keeps its name against a file; two spellings of
                    // one name, two folders of one name, or a file against a
                    // folder, are all left out.
                    bool clash = entry.IsFolder
                        ? folderCount.TryGetValue(entry.Name, out var count) && count > 1
                        : fileSpellings.TryGetValue(entry.Name, out var all) && all.Count > 1;
                    bool fileBesideFolder = !entry.IsFolder && folderCount.ContainsKey(entry.Name);
                    if (clash || fileBesideFolder)
                    {
                        var shown = path + (entry.IsFolder ? "/" : "");
                        if (said.Add(shown)) clashing?.Add(shown);
                        if (entry.IsFolder) skipped?.Folder(path);
                        else skipped?.File(path);
                        continue;
                    }

                    if (entry.IsFolder)
                    {
                        if (folders.TryAdd(path, entry.Id)) next.Add((entry.Id, path));
                        continue;
                    }

                    // Docs, Sheets and Slides have no bytes to copy.
                    if (entry.IsGoogleDocument || SyncPlanner.IsSkippedPath(path)) continue;

                    // Two Drive files of one name: the newer is the one that counts.
                    // The other is remembered in <paramref name="shadowed"/>.
                    var file = new SyncFile(entry.Size, Truncate(entry.Modified.ToUniversalTime()), entry.Id, entry.Md5);
                    if (!files.TryGetValue(path, out var seen)) files[path] = file;
                    else
                    {
                        var older = file.ModifiedUtc > seen.ModifiedUtc ? seen : file;
                        if (file.ModifiedUtc > seen.ModifiedUtc) files[path] = file;
                        if (shadowed != null && older.Id != null)
                        {
                            if (!shadowed.TryGetValue(path, out var hidden)) shadowed[path] = hidden = new List<SyncFile>();
                            hidden.Add(older);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// One Drive folder's children, asked again a few times by itself when
        /// Google says "not now" or the request is lost. A daily limit is left to
        /// the pass's own wait, which says so; anything else that keeps failing
        /// is thrown, and the pass decides.
        /// </summary>
        private static async Task<List<DriveEntry>> ListOneFolder(DriveClient client, string id, double waitScale,
            CancellationToken token)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await client.ListChildren(id, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < 3 && !token.IsCancellationRequested &&
                                           !SyncBackoff.IsDailyLimit(ex) &&
                                           (SyncBackoff.IsTransient(ex) || IsOffline(ex, token)))
                {
                    var wait = SyncBackoff.Delay(attempt, null, Random.Shared.NextDouble());
                    await Task.Delay(TimeSpan.FromTicks((long)(wait.Ticks * waitScale)), token).ConfigureAwait(false);
                }
            }
        }

        private static DateTime Truncate(DateTime utc) =>
            new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

        /// <summary>
        /// Where a pair's relative path is on the PC, in the PC's own spelling.
        ///
        /// A path from Drive can spell a name the PC already holds another way
        /// ("Café" with the accent as two characters, against the PC's one). NTFS
        /// keeps those apart, so building the path from Drive's spelling put a
        /// download in a second, look-alike folder beside the PC's. Each segment
        /// is the one already on disk that Drive's comparer calls the same name;
        /// only a segment with no such entry keeps the path's own spelling.
        /// </summary>
        /// <param name="listings">
        /// Each folder's entries as first read in this pass, so a thousand
        /// downloads into one folder list it once. A name that is on disk in the
        /// spelling asked for is found without it, which covers a folder this
        /// pass has made since.
        /// </param>
        internal static string LocalPathFor(string root, string relative,
            Dictionary<string, List<(string Name, bool Folder)>>? listings = null)
        {
            var at = root;
            var parts = relative.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                var exact = Path.Combine(at, part);
                bool last = i == parts.Length - 1;
                if (!(last ? File.Exists(exact) || Directory.Exists(exact) : Directory.Exists(exact)))
                {
                    List<(string Name, bool Folder)>? entries = null;
                    if (listings == null || !listings.TryGetValue(at, out entries))
                    {
                        entries = new List<(string, bool)>();
                        try
                        {
                            if (Directory.Exists(at))
                                foreach (var entry in new DirectoryInfo(at).EnumerateFileSystemInfos("*",
                                             new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
                                    entries.Add((entry.Name, entry is DirectoryInfo));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                        if (listings != null) listings[at] = entries;
                    }
                    foreach (var (name, folder) in entries)
                        if ((last || folder) && SyncNameComparer.Instance.Equals(name, part))
                        {
                            part = name;
                            break;
                        }
                }
                at = Path.Combine(at, part);
            }
            return at;
        }

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

        /// <param name="replaces">
        /// The Drive file this upload becomes, updated in place (same id, its
        /// earlier contents kept as a revision), or null for a new file.
        /// </param>
        /// <param name="old">What the scan saw of the file being replaced.</param>
        private async Task<SyncBase> UploadAsync(
            DriveClient client, Dictionary<string, string> folders, DriveSyncPair pair, PairState state,
            string path, string localPath, SyncFile local, string? replaces, SyncFile? old, Action<long> moved,
            CancellationToken token)
        {
            int slash = path.LastIndexOf('/');
            var parent = await EnsureFolderAsync(client, folders, pair.DriveFolderId, slash < 0 ? "" : path[..slash], token)
                .ConfigureAwait(false);

            // A session saved from an earlier, interrupted attempt at this same
            // file carries on from where Google says it stopped. Only one of the
            // same kind: a session that updates a file is not resumed to make a
            // new one, or the other way round.
            string? resume = null;
            if (state.Uploads.TryGetValue(path, out var saved) && saved.Size == local.Size &&
                Math.Abs((saved.ModifiedUtc - local.ModifiedUtc).TotalSeconds) <= 2 &&
                DateTime.UtcNow - saved.StartedUtc < SessionLifetime &&
                (saved.Replaces == null || saved.Replaces == replaces))
                resume = saved.Session;

            long reported = 0;
            DriveClient.DriveUploaded sent;
            var stamp = DriveClient.UploadStamp(localPath);
            Task<DriveClient.DriveUploaded> Send(string? into, string? resuming) =>
                client.UploadDetailed(localPath, Path.GetFileName(localPath), parent,
                    p =>
                    {
                        long delta = p.BytesSent - reported;
                        if (delta > 0) { reported = p.BytesSent; moved(delta); }
                    },
                    token, resuming,
                    session =>
                    {
                        state.Uploads[path] = new UploadSession(session, local.Size, local.ModifiedUtc, DateTime.UtcNow, into);
                        SaveSoon(pair.Id, state, now: local.Size >= SaveAtOnceBytes);
                    },
                    into);
            try
            {
                try
                {
                    sent = await Send(replaces, resume).ConfigureAwait(false);
                }
                catch (Exception ex) when (replaces != null && !token.IsCancellationRequested && NotFound(ex))
                {
                    // The file to update went between the check and the upload
                    // (trashed on the website): this becomes a new file. Should
                    // the old one be there after all, it is trashed below.
                    state.Uploads.Remove(path);
                    sent = await Send(null, null).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!IsOffline(ex, token) && !token.IsCancellationRequested &&
                                       !SyncBackoff.IsTransient(ex))
            {
                // Not the connection, not a cancel (closing the app cancels the
                // pass, and the session is what lets the upload carry on next
                // time), and not Google saying "not now": a session that will not
                // take is not kept.
                state.Uploads.Remove(path);
                throw;
            }

            if (reported < local.Size) moved(local.Size - reported);
            state.Uploads.Remove(path);

            // Updated in place, there is nothing left over. A new file beside
            // the old one (a session started before updates were done in place)
            // means the old one goes to the Drive trash, where its contents can
            // still be had back; and if Drive will not take it now, it is
            // remembered and tried again every pass. Swallowed, it stayed as a
            // second file of the same name, and could later come down over the
            // PC's copy.
            if (!string.IsNullOrEmpty(replaces) && sent.Id != replaces)
            {
                Kill("replacement up, old copy not yet trashed " + path);
                if (old is { } was) state.PendingTrash[replaces] = new PendingTrash(was.Size, was.ModifiedUtc, was.Md5);
                SaveState(pair.Id, state);
                try
                {
                    await client.Trash(replaces, token).ConfigureAwait(false);
                    state.PendingTrash.Remove(replaces);
                }
                catch (Exception ex) when (!token.IsCancellationRequested && NotFound(ex))
                {
                    state.PendingTrash.Remove(replaces);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // Kept in PendingTrash for the next pass.
                }
            }

            // What Drive holds, as Drive says: the time this end sent (taken
            // before the bytes, not after), the size and checksum Google
            // recorded. A file edited while it was going up used to be recorded
            // with its new time against Drive's old one and no checksum, and
            // the next pass called it changed on both sides: a false conflict.
            return new SyncBase(local.Size, local.ModifiedUtc, sent.Size ?? local.Size,
                Truncate(sent.ModifiedUtc ?? stamp), sent.Id, sent.Md5);
        }

        /// <summary>Drive answered 404 somewhere in the chain: the file is not there.</summary>
        private static bool NotFound(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e is DriveClient.DriveStatusException { Status: 404 }) return true;
            return false;
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
        /// <summary>
        /// Whether a Drive file still holds what the last sync saw in it: the
        /// same size, and the same MD5 (or, with none to compare, the same time).
        /// </summary>
        internal static bool StillAsSynced(SyncFile now, SyncBase was) =>
            now.Size == was.RemoteSize &&
            (now.Md5 != null && was.RemoteMd5 != null
                ? string.Equals(now.Md5, was.RemoteMd5, StringComparison.OrdinalIgnoreCase)
                : Math.Abs((now.ModifiedUtc - was.RemoteModifiedUtc).TotalSeconds) <= 2);

        internal static bool SameDriveFile(PartialDownload partial, SyncFile remote) =>
            partial.RemoteId == remote.Id && partial.Size == remote.Size &&
            (partial.Md5 != null && remote.Md5 != null
                ? string.Equals(partial.Md5, remote.Md5, StringComparison.OrdinalIgnoreCase)
                : Math.Abs((partial.ModifiedUtc - remote.ModifiedUtc).TotalSeconds) <= 2);

        /// <summary>
        /// Whether a Drive file, asked about just now, still holds what the scan
        /// saw: the same size, and the same MD5 (or, with none, the same time).
        /// </summary>
        internal static bool SameAsScanned(DriveEntry now, SyncFile scanned) =>
            now.Size == scanned.Size &&
            (now.Md5 != null && scanned.Md5 != null
                ? string.Equals(now.Md5, scanned.Md5, StringComparison.OrdinalIgnoreCase)
                : Math.Abs((Truncate(now.Modified.ToUniversalTime()) - scanned.ModifiedUtc).TotalSeconds) <= 2);

        /// <summary>
        /// Whether a PC file is still what the scan saw: the same size and
        /// modified time, or still absent when the scan found none. Anything
        /// else is a change made since, and the next pass decides about it.
        /// </summary>
        internal static bool LocalStillAsScanned(string path, SyncFile? scanned)
        {
            try
            {
                if (scanned is not { } was) return !File.Exists(path) && !Directory.Exists(path);
                var info = new FileInfo(path);
                return info.Exists && info.Length == was.Size && Truncate(info.LastWriteTimeUtc) == was.ModifiedUtc;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Whether a PC file may still be being written: modified within
        /// <paramref name="settle"/>, or open somewhere for writing (it cannot
        /// be opened while refusing other writers). A time in the future is a
        /// wrong clock, not a file in use.
        /// </summary>
        internal static bool StillBeingWritten(string path, SyncFile scanned, DateTime nowUtc, TimeSpan settle)
        {
            var age = nowUtc - scanned.ModifiedUtc;
            if (age >= TimeSpan.Zero && age < settle) return true;
            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch
            {
                // Not ours to read at all: the upload says why.
                return false;
            }
        }

        /// <param name="scanned">The PC's file as the scan saw it, or null when it saw none.</param>
        /// <param name="keepOld">
        /// The PC's file changed since the last sync, or was never synced: it
        /// goes to the Recycle Bin before Drive's copy takes its place, and the
        /// download fails if it cannot.
        /// </param>
        /// <returns>What both sides now hold, or null when the PC's file changed since the scan and was left alone.</returns>
        private async Task<SyncBase?> DownloadAsync(
            DriveClient client, string pairId, PairState state, string path, SyncFile remote, string localPath,
            SyncFile? scanned, bool keepOld, Action<long> moved, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            var partial = localPath + ".partial";

            long offset = PartialLength(state, path, localPath, remote);
            if (offset == 0)
            {
                try { File.Delete(partial); } catch { }
            }

            // Remembered before the first byte, so a drop at any point resumes:
            // at once for a big file, within two seconds for a small one, which
            // costs little to fetch again. Writing the whole state for every
            // small file made a pass of many files quadratic.
            state.Partials[path] = new PartialDownload(remote.Id ?? "", remote.Size, remote.ModifiedUtc, remote.Md5);
            SaveSoon(pairId, state, now: remote.Size >= SaveAtOnceBytes);
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

            // The PC's file, just before it is replaced. Saved since the scan
            // (minutes ago, or hours on a big pass), it is an edit nobody has
            // seen: it is left alone, and the finished download waits whole in
            // its ".partial" for the next pass, which sees both.
            if (!LocalStillAsScanned(localPath, scanned)) return null;
            if (scanned != null && keepOld)
            {
                Mine(localPath);
                var kept = RecycleLocal(localPath);
                if (kept != null)
                    throw new IOException($"the PC's copy was kept, so Google Drive's was not put over it: {kept}");
            }

            Mine(localPath);
            File.Move(partial, localPath, overwrite: true);
            state.Partials.Remove(path);

            return new SyncBase(remote.Size, remote.ModifiedUtc, remote.Size, remote.ModifiedUtc, remote.Id ?? "", remote.Md5);
        }

        /// <summary>
        /// Trashes the old Drive copies earlier replaces could not, each only
        /// while it still holds what it held when it was replaced. Gone already,
        /// or edited since, it is let go of; Drive saying no keeps it for the
        /// next pass.
        /// </summary>
        private async Task TrashPendingAsync(DriveClient client, DriveSyncPair pair, PairState state, CancellationToken token)
        {
            foreach (var (id, was) in state.PendingTrash.ToList())
            {
                try
                {
                    var now = await Patiently(pair, () => client.FileNow(id, token), client, token).ConfigureAwait(false);
                    if (now != null && SameAsScanned(now, new SyncFile(was.Size, was.ModifiedUtc, id, was.Md5)))
                        await Patiently(pair, async () => { await client.Trash(id, token).ConfigureAwait(false); return true; },
                            client, token).ConfigureAwait(false);
                    state.PendingTrash.Remove(id);
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is not PairStoppedException && !IsOffline(ex, token))
                {
                }
            }
        }

        // ---------------- state ----------------

        private enum StateRead { Fine, Missing, Damaged, Busy }

        private static (PairState State, StateRead How) ReadState(string pairId)
        {
            var path = StatePath(pairId);
            try
            {
                if (!File.Exists(path)) return (new PairState(), StateRead.Missing);
                var state = JsonSerializer.Deserialize<PairState>(ProtectedFile.ReadAllText(path)) ?? new PairState();
                state.Files = Rekeyed(state.Files);
                state.Partials = Rekeyed(state.Partials);
                state.Uploads = Rekeyed(state.Uploads);
                state.Hashes = Rekeyed(state.Hashes);
                state.PendingTrash = new Dictionary<string, PendingTrash>(state.PendingTrash ?? new(), StringComparer.Ordinal);
                return (state, StateRead.Fine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Open in another program (a backup, a virus scanner) for a
                // moment: not damage.
                return (new PairState(), StateRead.Busy);
            }
            catch
            {
                return (new PairState(), StateRead.Damaged);
            }
        }

        private static PairState LoadState(string pairId) => ReadState(pairId).State;

        /// <summary>
        /// A saved table under the names comparer. One written before accents
        /// were compared that way can hold a name twice, once in each spelling;
        /// the first is kept, where copying the whole table at once threw and
        /// lost every record with it.
        /// </summary>
        private static Dictionary<string, T> Rekeyed<T>(Dictionary<string, T>? saved)
        {
            var result = new Dictionary<string, T>(SyncNameComparer.Instance);
            if (saved != null)
                foreach (var (key, value) in saved) result.TryAdd(key, value);
            return result;
        }

        /// <summary>The pair's state, started again where its folders changed (see <see cref="StateFits"/>).</summary>
        private static (PairState State, StateRead How) LoadState(DriveSyncPair pair)
        {
            var (state, how) = ReadState(pair.Id);
            var (keepFiles, keepHashes) = StateFits(state.LocalFolder, state.DriveFolderId, pair);
            if (!keepFiles)
            {
                state.Files.Clear();
                state.Partials.Clear();
                state.Uploads.Clear();
                state.LastSyncedUtc = null;
                state.Problem = null;
            }
            if (!keepHashes) state.Hashes.Clear();
            state.LocalFolder = pair.LocalFolder;
            state.DriveFolderId = pair.DriveFolderId;
            return (state, how);
        }

        /// <summary>
        /// A state file that would not decrypt or parse: kept beside it as
        /// "&lt;id&gt;.json.damaged-&lt;time&gt;", said, and the pair paused. Unpaused, the
        /// pair starts from nothing remembered, which is the first-sync rules:
        /// matching files adopted, nothing deleted on either side.
        /// </summary>
        private void SetDamagedStateAside(DriveSyncPair pair, PairState fresh)
        {
            var path = StatePath(pair.Id);
            var aside = path + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            lock (StateLock)
            {
                try { File.Move(path, aside); }
                catch { try { File.Copy(path, aside, overwrite: true); File.Delete(path); } catch { } }
            }

            var problem = "paused: what it remembered about the last sync could not be read, and was kept as " +
                          Path.GetFileName(aside) + ". Unpause it to compare both folders afresh; nothing will be deleted.";
            fresh.Problem = problem;
            SaveState(pair.Id, fresh);
            SetStatus(pair.Id, new PairStatus(null, problem, false));
            _notify("sync.error", $"{DisplayName(pair)} {problem}");

            List<DriveSyncPair>? paused = null;
            lock (_gate)
                if (_pairs.Any(p => p.Id == pair.Id))
                    paused = _pairs.Select(p =>
                    {
                        var copy = p.Copy();
                        if (copy.Id == pair.Id) copy.Paused = true;
                        return copy;
                    }).ToList();
            if (paused != null) SetPairs(paused);
        }

        private static readonly object StateLock = new();

        /// <summary>
        /// Writes a pair's state, unless the pair has been removed: a pass that
        /// was still running wrote its file back after Remove had deleted it.
        /// </summary>
        private void SaveState(string pairId, PairState state)
        {
            lock (StateLock)
            {
                lock (_gate)
                {
                    if (_removed.Contains(pairId)) return;
                    _savedAt[pairId] = Environment.TickCount64;
                }
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

        /// <summary>
        /// Writes the state if it has not been written for two seconds, or at
        /// once when <paramref name="now"/>. The pass writes it whole at its end,
        /// and a crash in between loses at most two seconds of records, which
        /// the next pass adopts again by comparing both sides.
        /// </summary>
        private void SaveSoon(string pairId, PairState state, bool now = false)
        {
            if (!now)
                lock (_gate)
                    if (_savedAt.TryGetValue(pairId, out var at) && Environment.TickCount64 - at < 2000) return;
            SaveState(pairId, state);
        }

        /// <summary>Removes what was remembered for pairs that no longer exist.</summary>
        public static void ForgetRemovedPairs(IEnumerable<DriveSyncPair> pairs)
        {
            try
            {
                if (!Directory.Exists(StateDirectory)) return;
                var keep = new HashSet<string>(pairs.Select(p => p.Id + ".json"), StringComparer.OrdinalIgnoreCase);

                // Under the state lock, so a save already under way finishes
                // first, and the next one sees the pair removed.
                lock (StateLock)
                    foreach (var file in Directory.GetFiles(StateDirectory, "*.json"))
                        if (!keep.Contains(Path.GetFileName(file))) { try { File.Delete(file); } catch { } }
            }
            catch { }
        }

        public void Dispose()
        {
            try { _stop.Cancel(); } catch { }
            try { _drive.ConnectionChanged -= _onConnection; } catch { }
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
