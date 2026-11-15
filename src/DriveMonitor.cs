using System;
using System.Text;
using System.Diagnostics;
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
    ///
    /// Split by role: this file is the core (pairs, the loop, a pass, batches,
    /// state); DriveMonitor.Watcher.cs watches for changes and decides when a
    /// pair is due; DriveMonitor.Planner.cs lists both sides for the planner;
    /// DriveMonitor.Transfers.cs carries the plan out; DriveMonitor.DriveOnly.cs
    /// is Keep on Drive only.
    /// </summary>
    internal sealed partial class DriveMonitor : IDisposable
    {
        /// <summary>The running monitor, for the Preferences dialog. Null before the tray starts it.</summary>
        public static DriveMonitor? Current { get; private set; }

        public sealed record PairStatus(
            DateTime? LastSyncedUtc, string? Problem, bool Running, long DoneBytes = 0, long TotalBytes = 0,
            int CheckDone = 0, int CheckTotal = 0)
        {
            /// <summary>Where a running pass is: <see cref="PhaseScanning"/> and the rest. Null when not running.</summary>
            public string? Phase { get; init; }

            /// <summary>Files found on the PC side, so far or in all.</summary>
            public int LocalFound { get; init; }

            /// <summary>Files found in the Drive folder, so far or in all.</summary>
            public int DriveFound { get; init; }

            /// <summary>Files on both sides that are already the same, so nothing is done to them.</summary>
            public int Matched { get; init; }

            /// <summary>Which of the planned uploads, downloads and deletes this pass is on.</summary>
            public int StepDone { get; init; }
            public int StepTotal { get; init; }

            public int Uploaded { get; init; }
            public int Downloaded { get; init; }

            /// <summary>Files moved or renamed to follow a move on the other side.</summary>
            public int Moved { get; init; }
            public int Deleted { get; init; }
            public int Failed { get; init; }
        }

        /// <summary>
        /// Why <paramref name="pair"/> may not be added beside <paramref name="others"/>:
        /// the same or a nested PC folder, or the same or a nested Drive folder.
        /// Either way two pairs would sync the same files, and each would upload
        /// what the other had not recorded. Null when it does not overlap.
        /// </summary>
        internal static string? Overlap(DriveSyncPair pair, IEnumerable<DriveSyncPair> others)
        {
            static bool Nested(string a, string b, char separator)
            {
                a = a.TrimEnd(separator); b = b.TrimEnd(separator);
                if (a.Length == 0 || b.Length == 0) return a.Length == b.Length;
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
                       a.StartsWith(b + separator, StringComparison.OrdinalIgnoreCase) ||
                       b.StartsWith(a + separator, StringComparison.OrdinalIgnoreCase);
            }
            foreach (var other in others)
            {
                if (other.Id == pair.Id) continue;
                if (Nested(pair.LocalFolder, other.LocalFolder, Path.DirectorySeparatorChar))
                    return $"{pair.LocalFolder} overlaps {other.LocalFolder}, which is already monitored as " +
                           $"{DisplayName(other)}. Two monitored folders may not share files, or both would upload them.";
                if (pair.DriveFolderId == other.DriveFolderId ||
                    Nested(pair.DriveFolderPath, other.DriveFolderPath, '/'))
                    return $"The Drive folder {pair.DriveFolderPath} overlaps {other.DriveFolderPath}, which " +
                           $"{DisplayName(other)} already syncs. Choose a Drive folder of its own.";
            }
            return null;
        }

        public const string PhaseScanning = "scanning";
        public const string PhaseListing = "listing";
        public const string PhaseSyncing = "syncing";

        /// <summary>
        /// Changes a running pass's status, and tells the window at most twice
        /// a second (<paramref name="now"/> always), so a scan of two million
        /// files does not flood the list or the screen reader.
        /// </summary>
        private void Report(string pairId, Func<PairStatus, PairStatus> change, bool now = false)
        {
            lock (_gate)
            {
                if (!_status.TryGetValue(pairId, out var was)) return;
                _status[pairId] = change(was);
            }
            long tick = Environment.TickCount64;
            if (!now && tick - Interlocked.Read(ref _lastProgressEvent) < 500) return;
            Interlocked.Exchange(ref _lastProgressEvent, tick);
            try { Changed?.Invoke(); } catch (Exception ex) { Diag.Note(ex); }
        }

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

            /// <summary>
            /// The changes feed: where to read from next, the pair's Drive folders by
            /// path as last seen, folders to list again whatever the feed says (they
            /// had something left to do), when Drive was last listed whole, with
            /// subfolders or not, and what that listing left out. See ListDriveSideAsync.
            /// </summary>
            public string? ChangesToken { get; set; }
            public Dictionary<string, string> FolderIds { get; set; } = new(SyncNameComparer.Instance);
            public List<string> DirtyFolders { get; set; } = new();

            /// <summary>Free up space: empty PC folders too new to clear yet, looked at again next pass.</summary>
            public List<string>? EmptyLater { get; set; }

            /// <summary>
            /// Free up space: how many files the pair's Drive folder holds, from
            /// the last whole listing plus what went up since. Its records are
            /// forgotten as files are cleared, so a pass on the changes feed
            /// counted only the folders it listed ("On Google Drive: 205 files").
            /// </summary>
            public int? DriveFileTotal { get; set; }
            public DateTime? LastFullListUtc { get; set; }
            public bool? ScanSubfolders { get; set; }
            public List<string> DriveUnusable { get; set; } = new();
            public List<string> DriveClashing { get; set; } = new();

            /// <summary>
            /// A large pair's records are a file per folder (see ShardStore):
            /// these are the folders that have one. Files, Hashes, Partials and
            /// Uploads above are then empty in the saved state.
            /// </summary>
            public bool Sharded { get; set; }
            public List<string> ShardFolders { get; set; } = new();

            /// <summary>Of those, the folders with records of files: each must be a folder Drive is known to have.</summary>
            public List<string> RecordFolders { get; set; } = new();

            /// <summary>
            /// The PC side, as the Drive side has the changes feed: where the
            /// volume's change journal was when the last pass began (the next
            /// reads from there), when the PC folder was last walked whole and
            /// with which settings, and whether that walk met a junction or
            /// link (changes behind one are not in this journal). See PcMarksFor.
            /// </summary>
            public ulong? UsnJournalId { get; set; }
            public long? UsnNext { get; set; }
            public DateTime? LastPcWalkUtc { get; set; }
            public string? PcWalkKey { get; set; }
            public bool PcHasLinks { get; set; }

            /// <summary>PC names left out for clashing, carried for folders a pass does not list again.</summary>
            public List<string> LocalClashing { get; set; } = new();

            /// <summary>A batched pair's files on each side and in step once its last pass was done, for passes that visit only some folders.</summary>
            public int PcFiles { get; set; }
            public int DriveFiles { get; set; }
            public int MatchedFiles { get; set; }

            /// <summary>Set on a batch's state: it saves into these folders' records, not over the whole state.</summary>
            internal ShardStore? Store;

            /// <summary>The pair's Drive folders by path as they were when the pass began (see EnsureFolderAsync).</summary>
            internal IReadOnlyDictionary<string, string>? Remembered;
            internal HashSet<string>? SliceFolders;

            /// <summary>Set when the state holds only these paths of its folders (see ShardStore.SavePaths).</summary>
            internal HashSet<string>? SlicePaths;

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
        private System.Threading.Timer? _settled;
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
        private const int ListStreams = 16;

        /// <summary>Raised on a worker thread whenever a pair's status changes.</summary>
        public event Action? Changed;

        // ---- for the suite only; every one of these is the real thing by default ----

        /// <summary>Multiplies every wait for Google, so the suite can wait "for ever" in milliseconds.</summary>
        internal double WaitScale { get; set; } = 1;

        /// <summary>Free and total bytes of the PC folder's volume, and its letter.</summary>
        internal Func<string, (long Free, long Total, string Letter)> DiskOfRoot { get; set; } = DiskOf;

        /// <summary>The free space below which a synced folder's drive is said to be getting full (Settings.LowDiskWarningGigabytes); 0 never.</summary>
        internal Func<long>? LowDiskWarningBytes { get; set; }

        /// <summary>Drives already said to be getting full, until they have room again.</summary>
        private readonly HashSet<string> _lowDiskSaid = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Says once when a synced folder's drive drops below the warning, and
        /// again only after it has had room (a tenth over, at most 5 GB) since:
        /// Resilio could fill C: while Google's daily limit held the uploads.
        /// Called at the end of each pass and before each wait on Google.
        /// </summary>
        private void WarnIfLowOnSpace(DriveSyncPair pair)
        {
            long limit = LowDiskWarningBytes?.Invoke() ?? 0;
            if (limit <= 0) return;
            (long Free, long Total, string Letter) disk;
            try { disk = DiskOfRoot(pair.LocalFolder); } catch { return; }
            if (disk.Total <= 0 || string.IsNullOrEmpty(disk.Letter)) return;
            bool say = false;
            lock (_gate)
            {
                if (disk.Free < limit) say = _lowDiskSaid.Add(disk.Letter);
                else if (disk.Free > limit + Math.Min(limit / 10, 5L << 30)) _lowDiskSaid.Remove(disk.Letter);
            }
            if (say)
                _notify("sync.lowspace", $"{disk.Letter} is getting full: only {Words(disk.Free)} left. " +
                                         "Your synced folders keep going, but you may want to make some room");
        }

        /// <summary>
        /// Sends a PC file to the Recycle Bin with no window of any kind: null
        /// when it went, otherwise why it was kept.
        /// </summary>
        internal Func<string, string?> RecycleLocal { get; set; } = ShellDelete.RecycleWithoutUi;

        /// <summary>
        /// Deletes a PC file for good, with no window: null when it went (or was
        /// already gone), otherwise why it was kept. Used when deletes are copied
        /// and the file was deleted on the other side (5.2.1, Conner: code synced
        /// between two PCs filled the laptop's Recycle Bin). The Recycle Bin is
        /// kept only for a PC edit about to be overwritten by Drive's copy.
        /// </summary>
        internal Func<string, string?> DeleteLocalFile { get; set; } = DeleteForGood;

        internal static string? DeleteForGood(string path)
        {
            try
            {
                // A file only. The check before this saw a file, and if a folder
                // has taken its name since, deleting that for good with all
                // inside it is not what Drive's delete of one file means.
                if ((File.GetAttributes(NameRules.LiteralPath(path)) & FileAttributes.Directory) != 0)
                    return "it is a folder now";
                ShellDelete.DeletePermanently(path);
                return null;
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception ex) { return "it could not be deleted: " + ex.Message; }
        }

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
        /// <remarks>
        /// Five seconds since 7.0.0 (it was ten): a file still open for writing
        /// is held back by the open handle whatever its age, and with the three
        /// quiet seconds before a pass a saved file waited a quarter of a minute.
        /// </remarks>
        internal TimeSpan SettleTime { get; set; } = TimeSpan.FromSeconds(5);

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

            _loop = Task.Run(LoopAsync);
        }

        private int GenerationOf(string pairId)
        {
            lock (_gate) return _generation.TryGetValue(pairId, out var g) ? g : 0;
        }

        /// <summary>
        /// What every pair has sent to Drive in the last 24 hours, so the
        /// status can show Google's 750 GB a day coming (Conner, 3.0.16). By
        /// the hour, kept in drive-monitor\uploaded.json across restarts.
        /// </summary>
        internal sealed class UploadedRecently
        {
            /// <summary>Google's upload allowance a day, in bytes.</summary>
            public const long DailyAllowance = 750L << 30;     // said as "750 gigabytes", as Google says it

            private readonly object _gate = new();
            private readonly string _file;
            private Dictionary<long, long>? _byHour;
            private long _savedAt;

            /// <summary>How often the file is written at most; the suite sets 0.</summary>
            internal static int SaveEveryMs = 30_000;

            public UploadedRecently(string file) { _file = file; }

            private Dictionary<long, long> Hours()
            {
                if (_byHour != null) return _byHour;
                try
                {
                    _byHour = File.Exists(_file)
                        ? JsonSerializer.Deserialize<Dictionary<long, long>>(ProtectedFile.ReadAllUtf8(_file)) ?? new()
                        : new();
                }
                catch { _byHour = new(); }
                return _byHour;
            }

            private static long HourOf(DateTime utc) => utc.Ticks / TimeSpan.TicksPerHour;

            public void Add(long bytes, DateTime nowUtc)
            {
                if (bytes <= 0) return;
                lock (_gate)
                {
                    var hours = Hours();
                    long hour = HourOf(nowUtc);
                    hours[hour] = hours.GetValueOrDefault(hour) + bytes;
                    foreach (var old in hours.Keys.Where(h => h < hour - 24).ToList()) hours.Remove(old);
                    _unsaved = true;
                    if (_savedAt != 0 && Environment.TickCount64 - _savedAt < SaveEveryMs) return;
                    Write(hours);
                }
            }

            private bool _unsaved;

            private void Write(Dictionary<long, long> hours)
            {
                _savedAt = Environment.TickCount64;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                    ProtectedFile.WriteAllUtf8(_file, JsonSerializer.SerializeToUtf8Bytes(hours));
                    _unsaved = false;
                }
                catch (Exception ex) { Diag.Note(ex); }
            }

            /// <summary>Writes what the last 30 seconds added, if anything (on the way out).</summary>
            public void Flush()
            {
                lock (_gate)
                    if (_unsaved && _byHour != null) Write(_byHour);
            }

            /// <summary>Bytes sent in the last 24 hours (the current hour and the 23 whole ones before it, plus part of the 24th).</summary>
            public long Last24Hours(DateTime nowUtc)
            {
                lock (_gate)
                {
                    long hour = HourOf(nowUtc);
                    double into = (nowUtc.Ticks % TimeSpan.TicksPerHour) / (double)TimeSpan.TicksPerHour;
                    long sum = 0;
                    foreach (var (h, bytes) in Hours())
                        if (h > hour - 24 && h <= hour) sum += bytes;
                        else if (h == hour - 24) sum += (long)(bytes * (1 - into));
                    return sum;
                }
            }
        }

        /// <summary>The name of <see cref="UploadedRecently"/>'s file in <see cref="StateDirectory"/>; not a pair's records.</summary>
        internal const string UploadedFileName = "uploaded.json";

        private UploadedRecently? _uploadedRecently;

        /// <summary>See <see cref="UploadedRecently"/>.</summary>
        internal UploadedRecently Uploads =>
            _uploadedRecently ??= new UploadedRecently(Path.Combine(StateDirectory, UploadedFileName));

        /// <summary>What every pair has sent to Google Drive in the last 24 hours, in bytes.</summary>
        public long UploadedLast24Hours => Uploads.Last24Hours(DateTime.UtcNow);

        private static string StatePath(string pairId) => Path.Combine(StateDirectory, pairId + ".json");

        public List<DriveSyncPair> Pairs
        {
            get { lock (_gate) return _pairs.Select(p => p.Copy()).ToList(); }
        }

        /// <summary>The live drive, for the space and quota checks in the add/edit dialog.</summary>
        internal GoogleDrive Drive => _drive;

        /// <summary>
        /// Takes the pairs settings now hold, and watches their PC folders.
        ///
        /// Called on the UI thread (the sync window saves every change at once),
        /// so only the bookkeeping is done here. Looking at a PC folder and
        /// starting its watcher touch the disk: a pair on a share that had gone
        /// away held the window, and the engine waiting on the same lock, for as
        /// long as Windows took to give up. That is done on a worker, one per
        /// pair, and a watcher is put in place only if it is still the one wanted.
        /// </summary>
        public void Reload(IEnumerable<DriveSyncPair> pairs)
        {
            var retired = new List<FileSystemWatcher>();
            var wanted = new List<(DriveSyncPair Pair, long Ticket)>();
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

                foreach (var gone in before.Where(old => _pairs.All(p => p.Id != old.Id)))
                {
                    if (_watchers.Remove(gone.Id, out var watcher)) retired.Add(watcher);
                    _watchJournals.Remove(gone.Id);
                    _watchTickets.Remove(gone.Id);
                    _status.Remove(gone.Id);
                }
                foreach (var gone in _watchers.Keys.Where(id => _pairs.All(p => p.Id != id)).ToList())
                {
                    retired.Add(_watchers[gone]);
                    _watchers.Remove(gone);
                    _watchJournals.Remove(gone);
                    _watchTickets.Remove(gone);
                }

                _watchRules.Clear();
                foreach (var pair in _pairs)
                {
                    // Its skip list is read once here, not raced for by the watcher's threads.
                    var rules = SyncRules.For(pair);
                    rules.SkipsType("");
                    _watchRules[pair.Id] = rules;
                }

                foreach (var pair in _pairs)
                {
                    if (_watchers.TryGetValue(pair.Id, out var existing))
                    {
                        if (!pair.Paused &&
                            string.Equals(existing.Path, pair.LocalFolder, StringComparison.OrdinalIgnoreCase) &&
                            existing.IncludeSubdirectories == pair.IncludeSubfolders) continue;
                        retired.Add(existing);
                        _watchers.Remove(pair.Id);
                    }

                    // A new watcher has seen nothing yet: until it has watched a
                    // whole walk, what it says is not the whole story. A watcher
                    // still being made for this pair's old settings is no longer
                    // wanted (its ticket is out of date).
                    _watchJournals.Remove(pair.Id);
                    long ticket = ++_lastTicket;
                    _watchTickets[pair.Id] = ticket;
                    if (!pair.Paused) wanted.Add((pair.Copy(), ticket));
                }
            }

            if (retired.Count > 0)
                _ = Task.Run(() => { foreach (var watcher in retired) { try { watcher.Dispose(); } catch { } } });
            foreach (var (pair, ticket) in wanted)
                _ = Task.Run(() => WatchAsWanted(pair, ticket));

            try { Changed?.Invoke(); } catch (Exception ex) { Diag.Note(ex); }
            SyncNow();
        }

        /// 827 files of one repository had never gone up). Windows' own
        /// clutter is hidden and system both, and stays out.
        /// </summary>
        internal const FileAttributes LeftOutAttributes = FileAttributes.System;

        /// <summary>
        /// Resilio Sync's own folder, left out by name wherever it is: it used
        /// to be left out for being hidden.
        /// </summary>
        internal static bool IsResilioFolder(string name) => name.Equals(".sync", StringComparison.OrdinalIgnoreCase);

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

        /// <summary>
        /// Sync all now (tray, File menu, Ctrl+Shift+Y): every pair that is not
        /// paused, now. Says what it did, for the caller to show and speak.
        /// </summary>
        public string SyncAllNow()
        {
            var pairs = Pairs;
            if (pairs.Count == 0) return "You are not syncing any folders with Google Drive yet";
            int running = pairs.Count(p => !p.Paused);
            if (running == 0)
                return pairs.Count == 1
                    ? $"{DisplayName(pairs[0])} is paused. Resume it first"
                    : $"{Capitalise(AllFolders(pairs.Count))} are paused. Resume them first";
            SyncNow();
            int paused = pairs.Count - running;
            string syncing = running == 1
                ? $"Syncing {DisplayName(pairs.First(p => !p.Paused))} now"
                : paused == 0
                    ? $"Syncing {AllFolders(running)} now"
                    : $"Syncing {NameRules.Counted(running, "folder")} now";
            if (paused == 0) return syncing;
            return paused == 1
                ? $"{syncing}. {DisplayName(pairs.First(p => p.Paused))} is paused"
                : $"{syncing}. {Capitalise(NameRules.Number(paused))} are paused";
        }

        /// <summary>"both folders", "all three folders".</summary>
        private static string AllFolders(int count) =>
            count == 2 ? "both folders" : $"all {NameRules.Counted(count, "folder")}";

        private static string Capitalise(string text) =>
            text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

        /// <summary>
        /// Pause all or Resume all: every pair at once, saved like an edit in
        /// the sync window (a pass under way stops at its next file).
        /// </summary>
        public string PauseAll(bool pause)
        {
            var pairs = Pairs;
            if (pairs.Count == 0) return "You are not syncing any folders with Google Drive yet";
            if (pairs.Any(p => p.Paused != pause))
            {
                foreach (var pair in pairs) pair.Paused = pause;
                SetPairs(pairs);
            }
            string which = pairs.Count == 1 ? DisplayName(pairs[0]) : AllFolders(pairs.Count);
            return pause
                ? $"Paused {which}. Nothing syncs until you resume"
                : $"Resumed {which}. Syncing now";
        }

        /// <summary>True when there are pairs and every one is paused: the tray and menu then offer Resume all.</summary>
        public bool AllPaused
        {
            get { lock (_gate) return _pairs.Count > 0 && _pairs.All(p => p.Paused); }
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
        /// at once, or stops the pair if it has been paused, removed or edited.
        /// The wait for a daily limit is an hour, and Sync now, Pause, Edit and
        /// closing the app all have to be heard during it.
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

        /// <summary>The generation each running pass began with (see <see cref="Stopped"/>).</summary>
        private readonly Dictionary<string, int> _passGeneration = new();

        /// <summary>
        /// Whether a pair has been paused, removed or edited since its pass
        /// began. An edited pair's pass is of the old settings (Free up space
        /// unticked, switched to download only), so it stops at its next action
        /// as a paused one does, waits for Google and the end-of-pass steps
        /// included, and the next pass runs with the new ones.
        /// </summary>
        private bool Stopped(DriveSyncPair pair)
        {
            lock (_gate)
                return _removed.Contains(pair.Id) || _pairs.Any(p => p.Id == pair.Id && p.Paused) ||
                       (_passGeneration.TryGetValue(pair.Id, out var began) &&
                        (_generation.TryGetValue(pair.Id, out var now) ? now : 0) != began);
        }

        /// <summary>A pair paused, removed or edited while its pass was waiting for Google.</summary>
        private sealed class PairStoppedException : OperationCanceledException
        {
            public PairStoppedException() : base("the pair was paused") { }
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
                catch (Exception ex) { Diag.Note(ex); }
                finally { lock (_gate) _loadingStatus.Remove(pairId); }
                try { Changed?.Invoke(); } catch (Exception ex) { Diag.Note(ex); }
            });
            return null;
        }

        private void SetStatus(string pairId, PairStatus status)
        {
            lock (_gate) _status[pairId] = status;
            try { Changed?.Invoke(); } catch (Exception ex) { Diag.Note(ex); }
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SyncLive> _live = new();

        private SyncLive LiveFor(string pairId) => _live.GetOrAdd(pairId, _ => new SyncLive());

        /// <summary>What is moving for the pair right now and how fast, or null if nothing ever has.</summary>
        public SyncLive.Snapshot? LiveOf(string pairId) =>
            _live.TryGetValue(pairId, out var live) ? live.Read(Environment.TickCount64) : null;

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
            try { Changed?.Invoke(); } catch (Exception ex) { Diag.Note(ex); }
        }

        private async Task LoopAsync()
        {
            var token = _stop.Token;
            while (!token.IsCancellationRequested)
            {
                // With no pairs there is nothing to check: Reload ends in
                // SyncNow, so adding one wakes this straight away.
                bool idle;
                lock (_gate) idle = _pairs.Count == 0;
                var wait = idle ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(60);
                try { await _wake.WaitAsync(wait, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                try { await PassAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex) { Diag.Note(ex); }
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
            await RunDueAsync(client, all, DateTime.UtcNow, token).ConfigureAwait(false);
        }

        /// <summary>
        /// The pairs due now, one after another, each with its settings as they
        /// are when its turn comes. A pair edited while an earlier pair's pass
        /// ran (Free up space unticked, deletes turned off, switched to download
        /// only) was run with the copy listed before the edit, under the rules
        /// just switched off; one paused or removed meanwhile is skipped.
        /// </summary>
        internal async Task RunDueAsync(DriveClient client, bool all, DateTime now, CancellationToken token)
        {
            foreach (var listed in await DuePairsAsync(client, Pairs, all, now, token).ConfigureAwait(false))
            {
                if (token.IsCancellationRequested) return;
                bool gone;
                lock (_gate) gone = !_pairs.Any(p => p.Id == listed.Id && !p.Paused) || _removed.Contains(listed.Id);
                if (gone) continue;
                if (!await SyncOneAsync(client, listed, token, fromLive: true).ConfigureAwait(false)) return;
            }
        }

        /// <summary>
        /// One pair, with everything a pass does about how it went. False when
        /// Drive has gone offline and the rest of the pass should wait.
        /// </summary>
        /// <summary>A small file that says a pass is under way, from when.</summary>
        private static string RunningMarker(string pairId) => Path.Combine(StateDirectory, pairId + ".running");

        /// <summary>How long after an unfinished pass the next waits before looking at Drive.</summary>
        internal static TimeSpan AfterCrashWait { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// A pass that died with the process (closed by force, the power going)
        /// can have sent a file Google stored after all. Listed again within
        /// seconds, Drive may not show it yet, and it would be sent a second
        /// time; seen once on the real account, 1.1 seconds apart. So when the
        /// last pass never finished and began less than two minutes ago, this
        /// one waits until <see cref="AfterCrashWait"/> after it began. Then it
        /// marks itself as running. A pass that ends any other way removes the
        /// mark, so this costs nothing in the ordinary course.
        /// </summary>
        private async Task AfterAnUnfinishedPassAsync(DriveSyncPair pair, CancellationToken token)
        {
            var marker = RunningMarker(pair.Id);
            try
            {
                if (File.Exists(marker) &&
                    DateTime.TryParse(File.ReadAllText(marker), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var began))
                {
                    var since = DateTime.UtcNow - began.ToUniversalTime();
                    if (since >= TimeSpan.Zero && since < TimeSpan.FromMinutes(2) && since < AfterCrashWait)
                    {
                        SetStatus(pair.Id, StatusOf(pair.Id) with { Problem = "waiting a moment for Google, after the last sync was cut short" });
                        await Task.Delay(AfterCrashWait - since, token).ConfigureAwait(false);
                        SetStatus(pair.Id, StatusOf(pair.Id) with { Problem = null });
                    }
                }
                Directory.CreateDirectory(StateDirectory);
                File.WriteAllText(marker, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { Diag.Note(ex); }
        }

        /// <summary>Allocated by one pass, above which its memory is handed back to Windows straight after.</summary>
        internal static long GiveBackAboveBytes { get; set; } = 64L * 1024 * 1024;

        /// <summary>
        /// After a pass that went through a lot of memory (a whole walk of a big
        /// folder, a first sync), the heap is compacted and what it no longer
        /// needs is returned to Windows. Left to itself, .NET keeps the peak
        /// committed, and the app sits at its largest pass's size all day.
        /// A quiet pass allocates little and costs nothing here.
        /// </summary>
        private static void GiveMemoryBack(long allocated)
        {
            if (allocated < GiveBackAboveBytes) return;
            MemoryGiveBack.Collect();
        }

        /// <remarks>
        /// <paramref name="fromLive"/>: run the pair as settings hold it once the
        /// records are its own (see <see cref="RunDueAsync"/>). The suite passes
        /// pairs of its own, and keeps the one it gave.
        /// </remarks>
        internal async Task<bool> SyncOneAsync(DriveClient client, DriveSyncPair pair, CancellationToken token, bool fromLive = false)
        {
            {
                lock (_gate)
                {
                    _running = pair.Id;
                    _ranFrom[pair.Id] = DateTime.UtcNow;
                    _clean.Remove(pair.Id);
                }
                SetStatus(pair.Id, StatusOf(pair.Id) with { Running = true, DoneBytes = 0, TotalBytes = 0 });

                try
                {
                    await AfterAnUnfinishedPassAsync(pair, token).ConfigureAwait(false);
                    long allocatedBefore = GC.GetTotalAllocatedBytes();
                    // The pair's records are its pass's alone: Keep on Drive
                    // only and Put back on PC change them only between passes.
                    var records = RecordsLock(pair.Id);
                    try
                    {
                        await records.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            // Kept on Drive only as it is now: Put back changes it while
                            // holding the lock, and a pass that waited for it with the
                            // old list listed Drive afresh and skipped the folder for good.
                            // From the loop, every setting as it is now, read with the
                            // generation it goes with: an edit after this stops the pass
                            // at its next action (see Stopped); one before it is this pass.
                            DriveSyncPair? live;
                            int generation;
                            lock (_gate)
                            {
                                live = _removed.Contains(pair.Id) ? null : _pairs.FirstOrDefault(p => p.Id == pair.Id);
                                generation = _generation.TryGetValue(pair.Id, out var g) ? g : 0;
                            }
                            if (fromLive)
                            {
                                if (live == null || live.Paused || string.IsNullOrEmpty(live.DriveFolderId)) return true;
                                pair = live.Copy();
                            }
                            else if (live != null)
                            {
                                pair = pair.Copy();
                                pair.DriveOnlyFolders = new List<string>(live.DriveOnlyFolders ?? new());
                            }
                            await SyncPairAsync(client, pair, token, fromLive ? generation : null).ConfigureAwait(false);
                        }
                        finally { records.Release(); }
                    }
                    finally
                    {
                        // Reached however the pass ended, short of the process
                        // dying: anything it sent has been answered (see Drain).
                        try { File.Delete(RunningMarker(pair.Id)); } catch { }
                        bool removed;
                        lock (_gate) removed = _removed.Contains(pair.Id);
                        if (removed) SyncHistory.Discard(pair.Id);
                        else SyncHistory.Flush(pair.Id);
                        GiveMemoryBack(GC.GetTotalAllocatedBytes() - allocatedBefore);
                    }
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
                        Problem = "offline, will start again when Google Drive is back", Running = false,
                    });
                    if (!_saidOffline)
                    {
                        _saidOffline = true;
                        _notify("sync.offline", "You are offline, so syncing has paused. It starts again when you are back");
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
                    lock (_gate)
                    {
                        if (_running == pair.Id) _running = null;
                        // Outside a pass (Keep on Drive only, Put back) only Pause and Remove are heard.
                        _passGeneration.Remove(pair.Id);
                    }
                    try { Changed?.Invoke(); } catch (Exception ex) { Diag.Note(ex); }
                }
            }
            return true;
        }

        private int _waitingEpisode;

        /// <summary>How long a rate limit has to last before it is taken for the 750 GB daily upload cap.</summary>
        internal static readonly TimeSpan DailyLimitAfter = TimeSpan.FromMinutes(3);

        /// <summary>How often Google is asked again once the daily upload cap is reached.</summary>
        internal static readonly TimeSpan DailyCapRecheck = TimeSpan.FromMinutes(15);

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
            var limitedFor = TimeSpan.Zero;

            // This wait says why in the pair's status and stops on Pause; the
            // client's own rate-limit wait would hide both for up to an hour.
            DriveClient.CallerWaitsOutRateLimits.Value = true;
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
                    bool limited = ex.ToString().Contains("429") || ex.ToString().Contains("ateLimitExceeded") ||
                                   Limited(ex);

                    // Google's 750 GB a day comes back as the same "user rate
                    // limit exceeded" as a minute's throttling. One that lasts
                    // past DailyLimitAfter is the day's: said as that, asked
                    // about every DailyCapRecheck, never given up on.
                    bool daily = SyncBackoff.IsDailyLimit(ex);
                    bool dailyCap = !daily && limited && limitedFor >= DailyLimitAfter;
                    if (limited) limitedFor += wait;
                    if (daily && wait < SyncBackoff.DailyLimitWait) wait = SyncBackoff.DailyLimitWait;
                    if (dailyCap && wait < DailyCapRecheck) wait = DailyCapRecheck;
                    daily |= dailyCap;
                    var why = daily ? DriveClient.DailyUploadLimitSaid
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
                        Problem = daily
                            ? $"{why}. Trying again in {Seconds(wait)}"
                            : $"waiting, {why}, trying again in {Seconds(wait)}",
                    });
                    // Said once when it starts, and once more when a wait turns
                    // out to be the day's limit.
                    int episode = daily ? 2 : 1;
                    if (Interlocked.Exchange(ref _waitingEpisode, episode) < episode)
                        _notify("sync.ratelimited", daily
                            ? $"{why}, so {DisplayName(pair)} is waiting until it resets"
                            : $"{DisplayName(pair)} is waiting, because {why}");

                    WarnIfLowOnSpace(pair);

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
                        // Closing the app, pausing, removing or editing the pair:
                        // stop. Sync now: ask Google again straight away.
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
            if (Interlocked.Exchange(ref _waitingEpisode, 0) != 0)
                _notify("sync.ratelimited", $"Google is answering again, so {DisplayName(pair)} is syncing again");
        }

        private static string Seconds(TimeSpan wait)
        {
            int s = Math.Max(1, (int)Math.Round(wait.TotalSeconds));
            return s == 1 ? "1 second" : s < 90 ? $"{s} seconds" : $"{(int)Math.Round(s / 60.0)} minutes";
        }

        private static bool IsOffline(Exception ex, CancellationToken token)
        {
            if (token.IsCancellationRequested) return false;
            if (DriveClient.UnknownIfLandedException.In(ex)) return false;
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

        private async Task SyncPairAsync(DriveClient client, DriveSyncPair pair, CancellationToken token, int? generationRead = null)
        {
            int generation = generationRead ?? GenerationOf(pair.Id);
            lock (_gate) _passGeneration[pair.Id] = generation;
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

            // Folders kept on Drive only have no records, whichever way the pass
            // goes (a batch reads only the folders it visits).
            if (DropRecords(pair.Id, state, path => DriveOnlyFolders.Covers(pair.DriveOnlyFolders, path)))
                SaveState(pair.Id, state);

            var root = pair.LocalFolder;
            state.Remembered = new Dictionary<string, string>(state.FolderIds, SyncNameComparer.Instance);

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
                // A watcher on a folder that has gone watches nothing (or the
                // old folder, moved away); a new one is made once it is back.
                if (!localThere) RetireWatcher(pair.Id);
                bool newProblem = gone != state.Problem;
                state.Problem = gone;
                SaveState(pair.Id, state);
                SetStatus(pair.Id, new PairStatus(state.LastSyncedUtc, gone, false));
                if (newProblem) _notify("sync.paused", $"{name} is {gone}");
                return;
            }

            string? refuse = Refusal(root);
            if (refuse != null)
            {
                Finish(pair, state, refuse, announce: true);
                return;
            }
            EnsureWatcher(pair.Id);

            // Old Drive copies an earlier replace could not trash, before Drive
            // is listed, so the listing does not hold them as well.
            await TrashPendingAsync(client, pair, state, token).ConfigureAwait(false);

            // A large pair goes a batch of folders at a time (see SyncInBatchesAsync).
            if (UseBatches(state, pair))
            {
                // Its first pass a batch at a time walks everything, which also
                // gives it the totals a pass of only some folders goes on.
                bool batchedBefore = state.Sharded;
                if (!state.Sharded) MigrateToShards(pair.Id, state);
                var batchLook = LookAtPc(pair, state, batchedBefore && HasSyncedBefore(pair, state, state.ShardFolders.Count > 0));
                try { await SyncInBatchesAsync(client, pair, state, generation, batchLook, token).ConfigureAwait(false); }
                finally { PcUnplanned(batchLook); }
                return;
            }

            var look = LookAtPc(pair, state, HasSyncedBefore(pair, state, state.Files.Count > 0));
            try
            {
                await SyncSmallPairAsync(client, pair, state, generation, look, token).ConfigureAwait(false);
            }
            finally { PcUnplanned(look); }
        }

        /// <summary>A pair small enough to hold whole: both sides listed, planned and synced in one go.</summary>
        private async Task SyncSmallPairAsync(DriveClient client, DriveSyncPair pair, PairState state, int generation,
            PcLook look, CancellationToken token)
        {
            var root = pair.LocalFolder;

            // What either side holds and leaves out: never planned, so never
            // taken for deleted and never forgotten.
            var skips = new SyncSkips();
            var localClashing = new List<string>();
            Report(pair.Id, s => s with
            {
                Phase = PhaseScanning, LocalFound = 0, DriveFound = 0, Matched = 0, StepDone = 0, StepTotal = 0,
                Uploaded = 0, Downloaded = 0, Deleted = 0, Failed = 0, Moved = 0,
            }, now: true);

            // Only the PC folders that changed, when the journal or the watcher
            // says which and Drive is on its changes feed; the feed is read
            // first, so every folder Drive has news of is listed on the PC too.
            PcMarks? changedOnPc = null;
            (List<DriveClient.DriveSyncChange> Changes, string Next)? feed = null;
            string? driveId = null;
            if (look.Marks != null && CanListChangesOnly(ViewOf(state), pair, DateTime.UtcNow))
            {
                try
                {
                    driveId = await client.DriveOf(pair.DriveFolderId, token).ConfigureAwait(false);
                    var read = await Patiently(pair, () => client.SyncChangesSince(state.ChangesToken!, driveId, token),
                        client, token).ConfigureAwait(false);
                    if (FeedDirty(state, read.Changes) is { } onDrive)
                    {
                        feed = read;
                        changedOnPc = new PcMarks();
                        changedOnPc.Include(look.Marks);
                        changedOnPc.Folders.UnionWith(onDrive);
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is not PairStoppedException) { Diag.Note(ex); }
            }

            bool links = false;
            var pcFolders = new HashSet<string>(SyncNameComparer.Instance);
            var driveFoldersBefore = new HashSet<string>(state.FolderIds.Keys, SyncNameComparer.Instance);
            Dictionary<string, SyncFile> WalkWhole()
            {
                pcFolders.Clear();
                return ScanLocal(root, pair.IncludeSubfolders, skips, localClashing,
                    found: n => Report(pair.Id, s => s with { LocalFound = n }), linked: _ => links = true, folders: pcFolders);
            }
            var local = changedOnPc != null
                ? ScanLocalPartly(root, pair.IncludeSubfolders, state.Files, changedOnPc, skips, localClashing, state.LocalClashing,
                    linked: _ => links = true, folders: pcFolders)
                : WalkWhole();
            Report(pair.Id, s => s with { Phase = PhaseListing, LocalFound = local.Count }, now: true);
            var unusable = new List<string>();
            var clashing = new List<string>();
            var shadowed = new Dictionary<string, List<SyncFile>>(SyncNameComparer.Instance);
            var (remote, folders, feedToken, fullList, remoteSkips) = await ListDriveSideAsync(client, pair, state, local,
                unusable, shadowed, clashing, token, feed, driveId).ConfigureAwait(false);

            // Drive was listed whole after all: so is the PC, never half of each.
            if (changedOnPc != null && fullList)
            {
                skips = new SyncSkips();
                localClashing.Clear();
                changedOnPc = null;
                local = WalkWhole();
            }
            skips.Include(remoteSkips);
            Report(pair.Id, s => s with { LocalFound = local.Count, DriveFound = remote.Count }, now: true);

            // A download of a name that clashes left its part behind, and it
            // would be continued for ever. The clash is said instead.
            foreach (var path in state.Partials.Keys.Where(remoteSkips.Covers).ToList())
            {
                var part = PartialPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
                try { if (IsInside(root, part)) File.Delete(part); } catch { }
                state.Partials.Remove(path);
            }

            var rules = SyncRules.For(pair);
            var tally = new PassTally();
            await ExecuteAsync(client, pair, state, rules, local, remote, folders, skips, shadowed, generation, tally,
                plan =>
                {
                    // The feed's place, saved with the records it goes with. Every
                    // folder this pass has anything to do in is listed again next
                    // time, whatever the feed says, so a download that fails or
                    // waits is not left to the daily full listing.
                    state.ChangesToken = feedToken;
                    state.FolderIds = folders;
                    state.DirtyFolders = plan.Where(a => a.Kind is not (SyncActionKind.Record or SyncActionKind.Forget))
                        .Select(a => SyncMoves.Parent(a.Path)).Distinct(SyncNameComparer.Instance).ToList();
                    if (fullList)
                    {
                        state.LastFullListUtc = DateTime.UtcNow;
                        state.ScanSubfolders = pair.IncludeSubfolders;
                    }
                    state.DriveUnusable = unusable.ToList();
                    state.DriveClashing = clashing.ToList();
                    state.LocalClashing = localClashing.Distinct(StringComparer.Ordinal).ToList();
                    PcPlanned(pair, state, look, walkedWhole: changedOnPc == null, links);
                }, scope: null, hold: null, token, saveWhenDone: false).ConfigureAwait(false);
            // From here the state is written once, by FinishPass (every way out
            // of it saves), or by the catch below if anything stops the pass.
            try
            {
                await MakeEmptyFoldersAsync(client, pair, state, rules, pcFolders, folders, driveFoldersBefore, skips, tally, token)
                    .ConfigureAwait(false);
                ClearFoldersGoneFromDrive(pair, rules, skips, pcFolders, folders, driveFoldersBefore, _ => fullList);
                await FreeUpAsync(client, pair, state, rules, local, skips, tally, token).ConfigureAwait(false);
                ClearEmptyFolders(pair, state, rules, skips, pcFolders, folders);
                await TidyTwinFoldersAsync(client, pair, rules, clashing, folders, tally, token).ConfigureAwait(false);
                if (pair.FreesUpSpace)
                {
                    state.DriveFileTotal = fullList || state.DriveFileTotal is not int before
                        ? remote.Count + tally.Uploaded
                        : Math.Max(remote.Count, before) + tally.Uploaded;
                    int total = state.DriveFileTotal.Value;
                    Report(pair.Id, s => s with { DriveFound = total }, now: true);
                }
                FinishPass(pair, state, tally, unusable, clashing, localClashing);
            }
            catch
            {
                // What the pass did (the records, the folders made before a
                // stop: the table is the state's own) is remembered.
                SaveState(pair.Id, state);
                throw;
            }
        }

        /// <summary>A folder the pair leaves out: by name, by the skip list, or hidden, system or clashing.</summary>
        internal static bool IsLeftOutFolder(string folder, SyncRules rules, SyncSkips skips) =>
            SyncPlanner.IsSkippedPath(folder) || rules.LeavesOut(folder) || skips.Covers(folder + "/");

        // ---------------- large pairs: a batch of folders at a time ----------------

        /// <summary>
        /// Above this many files a pair is synced a batch of folders at a time,
        /// with its records kept a folder per file (see <see cref="ShardStore"/>).
        /// Measured over mirrored trees, the whole-tree way kept 787 MB alive at
        /// 200,000 files (about 4 KB a file) and its state file took 69 seconds
        /// to write at two million; a batch at a time (20,000 to a batch) kept
        /// 54 MB at 200,000 and the same 54 MB at 600,000. Settable for the
        /// tests, which run everything through batches of a few files.
        /// </summary>
        internal static int LargePairFiles { get; set; } = 100_000;

        /// <summary>How many PC files a batch gathers before it is synced.</summary>
        internal static int BatchFiles { get; set; } = 50_000;

        /// <summary>The most actions held back for the end of a pass (possible halves of a move).</summary>
        internal static int HeldMost { get; set; } = 200_000;

        /// <summary>Whether a pair goes a batch at a time: already does, or is big, or its PC folder is.</summary>
        private static bool UseBatches(PairState state, DriveSyncPair pair) =>
            state.Sharded || state.Files.Count > LargePairFiles ||
            (state.Files.Count == 0 && CountLocalUpTo(pair.LocalFolder, pair.IncludeSubfolders, LargePairFiles + 1) > LargePairFiles);

        /// <summary>Files under a folder, counted up to <paramref name="limit"/> and no further. Nothing kept.</summary>
        private static int CountLocalUpTo(string root, bool subfolders, int limit)
        {
            int count = 0;
            try
            {
                var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = subfolders, AttributesToSkip = 0 };
                foreach (var _ in Directory.EnumerateFiles(root, "*", options))
                    if (++count >= limit) break;
            }
            catch { }
            return count;
        }

        /// <summary>
        /// A pair's records, one encrypted file per folder that has any: what a
        /// batch loads and saves, and nothing else. Each file says which folder
        /// it is for, so a stale or mismatched one is never taken for another.
        /// </summary>
        private sealed class ShardStore
        {
            public readonly string Directory;
            public ShardStore(string pairId) { Directory = Path.Combine(StateDirectory, pairId + ".shards"); }

            public sealed class Shard
            {
                public string Folder { get; set; } = "";
                public Dictionary<string, SyncBase> Files { get; set; } = new(SyncNameComparer.Instance);
                public Dictionary<string, SyncHashEntry> Hashes { get; set; } = new(SyncNameComparer.Instance);
                public Dictionary<string, PartialDownload> Partials { get; set; } = new(SyncNameComparer.Instance);
                public Dictionary<string, UploadSession> Uploads { get; set; } = new(SyncNameComparer.Instance);
            }

            /// <summary>The file for a folder: named by a hash of the folder as the names comparer sees it.</summary>
            public string PathOf(string folder)
            {
                var key = SyncNameComparer.Key(folder).ToUpperInvariant();
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
                return Path.Combine(Directory, hash + ".json");
            }

            public bool Has(string folder) => File.Exists(PathOf(folder));

            /// <summary>
            /// What each folder held when it was loaded for the batch now running,
            /// so a save writes only the folders that changed: a pass where nothing
            /// did used to rewrite every record of every folder.
            /// </summary>
            private readonly Dictionary<string, Shard> _asLoaded = new(SyncNameComparer.Instance);

            public void ForgetLoaded() => _asLoaded.Clear();

            private static bool Same<T>(Dictionary<string, T> a, Dictionary<string, T> b)
            {
                if (a.Count != b.Count) return false;
                foreach (var (k, v) in a)
                    if (!b.TryGetValue(k, out var w) || !EqualityComparer<T>.Default.Equals(v, w)) return false;
                return true;
            }

            private static bool Same(Shard a, Shard b) =>
                Same(a.Files, b.Files) && Same(a.Hashes, b.Hashes) && Same(a.Partials, b.Partials) && Same(a.Uploads, b.Uploads);

            private static Shard CopyOf(Shard s) => new()
            {
                Folder = s.Folder,
                Files = new Dictionary<string, SyncBase>(s.Files, SyncNameComparer.Instance),
                Hashes = new Dictionary<string, SyncHashEntry>(s.Hashes, SyncNameComparer.Instance),
                Partials = new Dictionary<string, PartialDownload>(s.Partials, SyncNameComparer.Instance),
                Uploads = new Dictionary<string, UploadSession>(s.Uploads, SyncNameComparer.Instance),
            };

            /// <summary>The folder's records, or none. Throws <see cref="InvalidDataException"/> when the file is damaged.</summary>
            public Shard Load(string folder)
            {
                var path = PathOf(folder);
                byte[] text;
                try
                {
                    if (!File.Exists(path)) return new Shard { Folder = folder };
                    text = ProtectedFile.ReadAllUtf8(path);
                }
                catch (FileNotFoundException) { return new Shard { Folder = folder }; }
                catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
                {
                    throw new InvalidDataException(path, ex);
                }
                Shard? shard;
                try { shard = JsonSerializer.Deserialize<Shard>(text); }
                catch (Exception ex) { throw new InvalidDataException(path, ex); }
                if (shard == null) throw new InvalidDataException(path);
                if (!SyncNameComparer.Instance.Equals(shard.Folder, folder)) return new Shard { Folder = folder };
                shard.Files = Rekeyed(shard.Files);
                shard.Hashes = Rekeyed(shard.Hashes);
                shard.Partials = Rekeyed(shard.Partials);
                shard.Uploads = Rekeyed(shard.Uploads);
                return shard;
            }

            /// <summary>For each folder saved this pass, whether it now has records. Nothing else decides the list.</summary>
            private readonly Dictionary<string, bool> _present = new(SyncNameComparer.Instance);

            /// <summary>Whether a folder saved this pass has records, or null when it was not saved.</summary>
            public bool? Present(string folder) => _present.TryGetValue(folder, out var p) ? p : null;

            /// <summary>For each folder saved this pass, whether it holds records of files (not only checksums).</summary>
            private readonly Dictionary<string, bool> _hasFiles = new(SyncNameComparer.Instance);

            public bool? HasFiles(string folder) => _hasFiles.TryGetValue(folder, out var f) ? f : null;

            public void Save(Shard shard)
            {
                var path = PathOf(shard.Folder);
                bool any = shard.Files.Count + shard.Hashes.Count + shard.Partials.Count + shard.Uploads.Count > 0;
                _present[shard.Folder] = any;
                _hasFiles[shard.Folder] = shard.Files.Count > 0;
                if (!any)
                {
                    try { File.Delete(path); } catch { }
                    return;
                }
                System.IO.Directory.CreateDirectory(Directory);
                var temp = path + "." + Environment.ProcessId + ".tmp";
                ProtectedFile.WriteAllUtf8(temp, JsonSerializer.SerializeToUtf8Bytes(shard));
                File.Move(temp, path, overwrite: true);
            }

            public void Delete(string folder) { try { File.Delete(PathOf(folder)); } catch { } }

            /// <summary>
            /// Records files for folders the pass did not reach and the list does not
            /// have (a pass cut off between saving a folder and saving the list):
            /// their folder is gone from both sides, so they go too.
            /// </summary>
            public void SweepOrphans(IReadOnlySet<string> listed, IReadOnlySet<string> visited, SyncSkips skips)
            {
                if (!System.IO.Directory.Exists(Directory)) return;
                var known = new HashSet<string>(listed.Select(PathOf), StringComparer.OrdinalIgnoreCase);
                foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json"))
                {
                    if (known.Contains(file)) continue;
                    try
                    {
                        var shard = JsonSerializer.Deserialize<Shard>(ProtectedFile.ReadAllUtf8(file));
                        if (shard == null || visited.Contains(shard.Folder) || skips.Covers(shard.Folder + "/")) continue;
                        File.Delete(file);
                    }
                    catch { }
                }
            }

            /// <summary>
            /// Puts a folder's records into a batch's state. A folder's file is what
            /// was last saved for it: everything that would make one stale (a damaged
            /// state, a move to this store, a pair's folders changing) takes the
            /// whole store with it, so a file that is there is current, including one
            /// saved just before a pass was cut off (an upload session in it, say).
            /// </summary>
            public void LoadInto(string folder, PairState slice)
            {
                var shard = Load(folder);
                _asLoaded[folder] = CopyOf(shard);
                foreach (var (k, v) in shard.Files) slice.Files[k] = v;
                foreach (var (k, v) in shard.Hashes) slice.Hashes[k] = v;
                foreach (var (k, v) in shard.Partials) slice.Partials[k] = v;
                foreach (var (k, v) in shard.Uploads) slice.Uploads[k] = v;
            }

            /// <summary>
            /// Puts only <paramref name="paths"/> of a folder's records into a state
            /// (the end of a batched pass, which needs a few files from many folders).
            /// </summary>
            public void LoadPathsInto(string folder, IReadOnlySet<string> paths, PairState slice)
            {
                var shard = Load(folder);
                foreach (var (k, v) in shard.Files) if (paths.Contains(k)) slice.Files[k] = v;
                foreach (var (k, v) in shard.Hashes) if (paths.Contains(k)) slice.Hashes[k] = v;
                foreach (var (k, v) in shard.Partials) if (paths.Contains(k)) slice.Partials[k] = v;
                foreach (var (k, v) in shard.Uploads) if (paths.Contains(k)) slice.Uploads[k] = v;
            }

            /// <summary>Writes every folder of a batch from its state, each entry to the folder it is in.</summary>
            public void SaveSlice(PairState slice)
            {
                if (slice.SlicePaths != null) { SavePaths(slice); return; }
                var shards = new Dictionary<string, Shard>(SyncNameComparer.Instance);
                foreach (var folder in slice.SliceFolders!) shards[folder] = new Shard { Folder = folder };
                Shard? Of(string path) => shards.TryGetValue(SyncMoves.Parent(path), out var s) ? s : null;
                foreach (var (k, v) in slice.Files) Of(k)?.Files.TryAdd(k, v);
                foreach (var (k, v) in slice.Hashes) Of(k)?.Hashes.TryAdd(k, v);
                foreach (var (k, v) in slice.Partials) Of(k)?.Partials.TryAdd(k, v);
                foreach (var (k, v) in slice.Uploads) Of(k)?.Uploads.TryAdd(k, v);
                foreach (var shard in shards.Values)
                {
                    if (_asLoaded.TryGetValue(shard.Folder, out var was) && Same(was, shard)) continue;
                    Save(shard);
                    _asLoaded[shard.Folder] = CopyOf(shard);
                }
            }

            /// <summary>
            /// For a state holding only some paths of each folder: each folder's
            /// file read again and only those paths changed in it, everything else
            /// kept exactly as it was.
            /// </summary>
            private void SavePaths(PairState slice)
            {
                var paths = slice.SlicePaths!;
                foreach (var folder in slice.SliceFolders!)
                {
                    var shard = Load(folder);
                    var before = CopyOf(shard);
                    void Merge<T>(Dictionary<string, T> into, Dictionary<string, T> from)
                    {
                        foreach (var k in into.Keys.Where(k => paths.Contains(k) && !from.ContainsKey(k)).ToList()) into.Remove(k);
                        foreach (var (k, v) in from)
                            if (paths.Contains(k) && SyncNameComparer.Instance.Equals(SyncMoves.Parent(k), folder)) into[k] = v;
                    }
                    Merge(shard.Files, slice.Files);
                    Merge(shard.Hashes, slice.Hashes);
                    Merge(shard.Partials, slice.Partials);
                    Merge(shard.Uploads, slice.Uploads);
                    if (!Same(before, shard)) Save(shard);
                }
            }
        }

        /// <summary>
        /// Moves a whole-tree state into a folder-per-file store. The shards are
        /// written first and the state that says "sharded" last, so a crash in
        /// between leaves the old state, which is simply moved again.
        /// </summary>
        private void MigrateToShards(string pairId, PairState state)
        {
            var store = new ShardStore(pairId);
            try { if (Directory.Exists(store.Directory)) Directory.Delete(store.Directory, true); } catch { }
            var shards = new Dictionary<string, ShardStore.Shard>(SyncNameComparer.Instance);
            ShardStore.Shard Of(string path)
            {
                var folder = SyncMoves.Parent(path);
                if (!shards.TryGetValue(folder, out var shard)) shards[folder] = shard = new ShardStore.Shard { Folder = folder };
                return shard;
            }
            foreach (var (k, v) in state.Files) Of(k).Files[k] = v;
            foreach (var (k, v) in state.Hashes) Of(k).Hashes[k] = v;
            foreach (var (k, v) in state.Partials) Of(k).Partials[k] = v;
            foreach (var (k, v) in state.Uploads) Of(k).Uploads[k] = v;
            foreach (var shard in shards.Values) store.Save(shard);

            state.ShardFolders = shards.Values.Where(s => s.Files.Count + s.Hashes.Count + s.Partials.Count + s.Uploads.Count > 0)
                .Select(s => s.Folder).ToList();
            state.RecordFolders = shards.Values.Where(s => s.Files.Count > 0).Select(s => s.Folder).ToList();
            state.Files.Clear();
            state.Hashes.Clear();
            state.Partials.Clear();
            state.Uploads.Clear();
            state.Sharded = true;
            SaveState(pairId, state);
        }

        /// <summary>What a held action needs at the end of the pass: what each side held, and what was recorded.</summary>
        private sealed record HeldItem(SyncAction Action, SyncFile? Local, SyncFile? Remote, SyncBase? Was);

        /// <summary>
        /// A pass over a large pair, a batch of folders at a time, so memory is
        /// what one batch needs whatever the size of the tree.
        ///
        /// The folders are walked shallowest first: each batch is scanned on the
        /// PC, its records loaded, Drive's side listed afresh (always on a full
        /// listing; otherwise where the changes feed, a new PC file or a new PC
        /// folder says to) or taken from the records, and synced exactly as a
        /// whole small pair is. The subfolders found on either side, or in the
        /// records, are the next batches.
        ///
        /// A move can have its halves in two batches. So once a pair has
        /// records, whatever could be one half (a new file on one side, a file
        /// gone from it that the records have) is held back, and at the end the
        /// held files are synced together, where a move is seen as a move.
        /// </summary>
        private async Task SyncInBatchesAsync(DriveClient client, DriveSyncPair pair, PairState header, int generation,
            PcLook look, CancellationToken token)
        {
            try { await SyncInBatchesCoreAsync(client, pair, header, generation, look, forceFull: false, token).ConfigureAwait(false); }
            catch (FullListingNeededException why)
            {
                GoogleDrive.Trace($"sync {DisplayName(pair)}: listing Drive whole after all, {why.Message}");
                await SyncInBatchesCoreAsync(client, pair, header, generation, look, forceFull: true, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// A pass on the changes feed found something the records and the feed
        /// disagree about (records in a folder Drive is not known to have): the
        /// pass starts again, listing Drive whole, rather than guess.
        /// </summary>
        private sealed class FullListingNeededException : Exception
        {
            public FullListingNeededException(string why) : base(why) { }
        }

        private async Task SyncInBatchesCoreAsync(DriveClient client, DriveSyncPair pair, PairState header, int generation,
            PcLook look, bool forceFull, CancellationToken token)
        {
            var root = pair.LocalFolder;
            var rules = SyncRules.For(pair);
            var store = new ShardStore(pair.Id);
            var tally = new PassTally { Batched = true };
            var unusable = new List<string>();
            var clashing = new List<string>();
            var localClashing = new List<string>();
            var comparer = SyncNameComparer.Instance;

            Report(pair.Id, s => s with
            {
                Phase = PhaseScanning, LocalFound = 0, DriveFound = 0, Matched = 0, StepDone = 0, StepTotal = 0,
                Uploaded = 0, Downloaded = 0, Deleted = 0, Failed = 0, Moved = 0,
            }, now: true);

            // ---- Drive: the feed, or a full listing ----
            var shardFolders = new HashSet<string>(header.ShardFolders, comparer);
            var recordFolders = new HashSet<string>(header.RecordFolders, comparer);
            // Which drive's feed to read. A blip is waited out like the feed read;
            // a drive that cannot be told means a full listing and no token: with
            // driveId null the feed is My Drive's, and a shared-drive pair read
            // and saved the wrong feed, missing its own changes for up to a day.
            string? driveId = null;
            bool driveKnown = true;
            try { driveId = await Patiently(pair, () => client.DriveOf(pair.DriveFolderId, token), client, token).ConfigureAwait(false); }
            catch (Exception) when (!token.IsCancellationRequested) { driveKnown = false; }
            if (driveKnown) lock (_gate) { _driveOf[pair.Id] = driveId; _driveOfFolder[pair.Id] = pair.DriveFolderId; }

            // The retry after FullListingNeededException comes here with the feed
            // still usable and every recorded folder known: First() then threw
            // "Sequence contains no matching element", and the pair failed every
            // pass until the daily full listing.
            bool canFeed = CanListChangesOnly(ViewOf(header), pair, DateTime.UtcNow);
            // Folders Drive has but the pair leaves out (twins, spelling clashes,
            // unusable names) are known, not missing: read as missing, a folder
            // with records that became a twin listed all of Drive every pass.
            var leftOut = new SyncSkips();
            foreach (var e in header.DriveClashing.Concat(header.DriveUnusable))
                if (e.EndsWith('/')) leftOut.Folder(e.TrimEnd('/'));
            var unknownFolder = recordFolders.FirstOrDefault(f => !header.FolderIds.ContainsKey(f) && !leftOut.Covers(f + "/"));
            bool full = forceFull || !canFeed || unknownFolder != null || !driveKnown;
            string why = !driveKnown ? "could not tell which drive the folder is on"
                : !canFeed ? "no feed to go on"
                : unknownFolder != null ? "records in a folder Drive is not known to have: " + unknownFolder
                : forceFull ? "a batch met records in a folder Drive is not known to have"
                : "";
            var dirty = new HashSet<string>(comparer);
            string? nextToken = null;
            if (!full)
            {
                try
                {
                    var (changes, next) = await Patiently(pair, () => client.SyncChangesSince(header.ChangesToken!, driveId, token),
                        client, token).ConfigureAwait(false);
                    var folderAt = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (path, id) in header.FolderIds)
                        if (!folderAt.TryAdd(id, path)) { full = true; why = "one Drive folder at two paths: " + path; }
                    foreach (var c in changes)
                    {
                        if (full) break;
                        if (folderAt.TryGetValue(c.Id, out var folderPath))
                        {
                            if (folderPath.Length == 0) { dirty.Add(""); continue; }
                            var parentId = header.FolderIds.TryGetValue(SyncMoves.Parent(folderPath), out var pid) ? pid : null;
                            if (c.Gone || !c.Folder || c.Parent != parentId || c.Name == null ||
                                !comparer.Equals(c.Name, SyncMoves.Leaf(folderPath)))
                            {
                                full = true;
                                why = $"the folder {folderPath} changed on Drive (now {c.Name}{(c.Gone ? ", gone" : "")})";
                                break;
                            }
                            dirty.Add(folderPath);
                            continue;
                        }
                        // A new folder, or a file: its folder is listed again. A file
                        // moved here from elsewhere in the pair is found at the end.
                        if (c.Parent != null && folderAt.TryGetValue(c.Parent, out var into)) dirty.Add(into);
                    }
                    nextToken = next;
                    dirty.UnionWith(header.DirtyFolders);
                }
                catch (Exception ex) when (!token.IsCancellationRequested) { full = true; why = "the feed failed: " + GoogleNet.Reason(ex); }
            }
            GoogleDrive.Trace($"sync {DisplayName(pair)} a batch at a time: " + (full ? "listing Drive whole, " + why : $"{dirty.Count} folders changed on Drive"));
            if (full)
            {
                dirty.Clear();
                try { nextToken = driveKnown ? await client.ChangesStartToken(driveId, token).ConfigureAwait(false) : null; }
                catch (Exception) when (!token.IsCancellationRequested) { nextToken = null; }
            }

            var folders = full
                ? new Dictionary<string, string>(comparer) { [""] = pair.DriveFolderId }
                : new Dictionary<string, string>(header.FolderIds, comparer);
            var knownAtStart = new HashSet<string>(folders.Keys, comparer);
            var kids = new Dictionary<string, HashSet<string>>(comparer);
            void Kid(string folder)
            {
                if (folder.Length == 0) return;
                var parent = SyncMoves.Parent(folder);
                if (!kids.TryGetValue(parent, out var set)) kids[parent] = set = new HashSet<string>(comparer);
                set.Add(folder);
            }

            // Only the folders that changed, on either side, when both sides can
            // say which: those, the folders above them (to get there), and any
            // subfolder that is on one side and not the other (arrived or gone,
            // so taken whole). Every other folder is left exactly as it is: not
            // scanned, not loaded, not listed.
            bool partial = look.Marks != null && !full;
            var focus = new HashSet<string>(comparer);
            var wholeTree = new HashSet<string>(comparer);
            if (partial)
            {
                foreach (var f in look.Marks!.Folders.Concat(look.Marks.Deep).Concat(dirty))
                    for (var up = f; up.Length > 0 && focus.Add(up); up = SyncMoves.Parent(up)) { }
                wholeTree.UnionWith(look.Marks.Deep);
            }
            GoogleDrive.Trace($"sync {DisplayName(pair)} a batch at a time: " +
                              (partial ? $"{focus.Count} folders to look at" : "every folder on the PC"));

            // Which folders each folder holds, on Drive or in the records. A
            // walk of everything needs them all; a pass of only some folders
            // only theirs, and a folder that turns up whole is looked up when
            // it is reached, not a table of a hundred thousand built for three.
            var indexed = new HashSet<string>(comparer);
            if (!partial)
            {
                foreach (var f in folders.Keys) Kid(f);
                foreach (var f in shardFolders) Kid(f);
            }
            else
            {
                indexed.Add("");
                indexed.UnionWith(focus);
                foreach (var f in folders.Keys.Concat(shardFolders))
                    if (f.Length > 0 && indexed.Contains(SyncMoves.Parent(f))) Kid(f);
            }
            HashSet<string>? KidsOf(string parent)
            {
                if (partial && indexed.Add(parent))
                    foreach (var f in folders.Keys.Concat(shardFolders))
                        if (f.Length > parent.Length && SyncMoves.IsUnder(f, parent) && f.IndexOf('/', parent.Length + 1) < 0) Kid(f);
                return kids.TryGetValue(parent, out var set) ? set : null;
            }
            bool links = false;
            int recordsSeen = 0;
            (int Pc, int Drive, int Matched) Totals(int pcSeen, int driveSeen, int matched) => partial
                ? (Math.Max(0, header.PcFiles - recordsSeen + pcSeen), Math.Max(0, header.DriveFiles - recordsSeen + driveSeen),
                   Math.Max(0, header.MatchedFiles - recordsSeen + matched))
                : (pcSeen, driveSeen, matched);

            bool hasRecords = shardFolders.Count > 0;
            var held = new List<HeldItem>();
            var allSkips = new SyncSkips();
            var visited = new HashSet<string>(comparer);
            var linkTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // where links led (see WalkIntoLink)
            var listedAll = new HashSet<string>(comparer);
            var nextDirty = new HashSet<string>(comparer);
            var foldersToMake = new List<string>();
            var queue = new Queue<string>();
            queue.Enqueue("");
            int localSeen = 0, driveSeen = 0;

            try
            {
                while (queue.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (Stopped(pair) || GenerationOf(pair.Id) != generation) throw new PairStoppedException();
                    if (tally.SpaceProblem != null) break;

                    // ---- gather: PC first ----
                    var timing = Stopwatch.StartNew();
                    long scanMs, recordsMs = 0, listMs = 0;
                    var batch = new List<string>();
                    var local = new Dictionary<string, SyncFile>(comparer);
                    var skips = new SyncSkips();
                    var subs = new List<(DirectoryInfo Folder, string Relative)>();
                    var localSubfolders = new Dictionary<string, List<string>>(comparer);
                    var onPcHere = new List<string>();
                    while (queue.Count > 0 && (batch.Count == 0 || local.Count < BatchFiles))
                    {
                        var f = queue.Dequeue();
                        if (!visited.Add(f)) continue;
                        batch.Add(f);
                        subs.Clear();
                        var dir = new DirectoryInfo(f.Length == 0 ? root : LocalPathFor(root, f));
                        if (f.Length == 0 || dir.Exists)
                        {
                            ScanLocalFolder(dir, f, pair.IncludeSubfolders, local, skips, localClashing, subs, null, _ => links = true, linkTargets);
                            onPcHere.Add(f);
                        }
                        localSubfolders[f] = subs.Select(x => x.Relative).ToList();
                    }
                    localSeen += local.Count;
                    scanMs = timing.ElapsedMilliseconds;

                    // ---- the records ----
                    var slice = new PairState
                    {
                        Store = store, SliceFolders = new HashSet<string>(batch, comparer), PendingTrash = header.PendingTrash,
                        LocalFolder = header.LocalFolder, DriveFolderId = header.DriveFolderId, Remembered = header.Remembered,
                    };
                    store.ForgetLoaded();
                    foreach (var f in batch) store.LoadInto(f, slice);
                    recordsSeen += slice.Files.Count;
                    recordsMs = timing.ElapsedMilliseconds - scanMs;

                    // ---- Drive: afresh, or as recorded ----
                    var freshFolders = new HashSet<string>(comparer);
                    foreach (var path in local.Keys)
                        if (!slice.Files.ContainsKey(path)) freshFolders.Add(SyncMoves.Parent(path));
                    foreach (var (path, was) in slice.Files)
                        if (string.IsNullOrEmpty(was.RemoteId)) freshFolders.Add(SyncMoves.Parent(path));
                    var listNow = new List<(string Id, string Relative)>();
                    foreach (var f in batch)
                    {
                        if (!folders.TryGetValue(f, out var id)) continue;
                        bool afresh = full || dirty.Contains(f) || !knownAtStart.Contains(f) || freshFolders.Contains(f) ||
                                      localSubfolders[f].Any(sub => !folders.ContainsKey(sub));
                        if (afresh) listNow.Add((id, f));
                    }
                    var listed = new HashSet<string>(listNow.Select(x => x.Relative), comparer);
                    listedAll.UnionWith(listed);

                    // On the feed, every recorded file's folder must be one Drive is
                    // known to have; one that is not would read as deleted.
                    if (!full && slice.Files.Keys.FirstOrDefault(k => !folders.ContainsKey(SyncMoves.Parent(k))) is { } orphan)
                        throw new FullListingNeededException("records in " + SyncMoves.Parent(orphan) + ", a folder Drive is not known to have");

                    var remote = new Dictionary<string, SyncFile>(comparer);
                    foreach (var (path, was) in slice.Files)
                    {
                        var folder = SyncMoves.Parent(path);
                        if (listed.Contains(folder) || string.IsNullOrEmpty(was.RemoteId) || !folders.ContainsKey(folder)) continue;
                        remote[path] = new SyncFile(was.RemoteSize, was.RemoteModifiedUtc, was.RemoteId, was.RemoteMd5);
                    }

                    var shadowed = new Dictionary<string, List<SyncFile>>(comparer);
                    if (listNow.Count > 0)
                    {
                        var listSkips = new SyncSkips();
                        var u = new List<string>();
                        var cl = new List<string>();
                        var known = folders;
                        var (fresh, found) = await Patiently(pair,
                            () =>
                            {
                                u.Clear();
                                cl.Clear();
                                shadowed.Clear();
                                listSkips = new SyncSkips();
                                return ScanRemoteAsync(client, pair.DriveFolderId, pair.IncludeSubfolders, token, u, shadowed, cl,
                                    listSkips, WaitScale, only: listNow, known: known, descend: false);
                            }, client, token)
                            .ConfigureAwait(false);
                        foreach (var (path, file) in fresh) remote[path] = file;
                        foreach (var (path, id) in found)
                            if (folders.TryAdd(path, id)) Kid(path);
                        skips.Include(listSkips);
                        unusable.AddRange(u);
                        clashing.AddRange(cl);
                    }
                    driveSeen += remote.Count;
                    listMs = timing.ElapsedMilliseconds - scanMs - recordsMs;
                    allSkips.Include(skips);
                    var seenSoFar = Totals(localSeen, driveSeen, tally.Matched);
                    int driveSoFar = pair.FreesUpSpace && !full && header.DriveFileTotal is int savedTotal ? savedTotal : seenSoFar.Drive;
                    Report(pair.Id, s => s with { LocalFound = seenSoFar.Pc, DriveFound = driveSoFar }, now: true);

                    // ---- the next batches: subfolders on either side, or in the records ----
                    if (pair.IncludeSubfolders)
                        foreach (var f in batch)
                        {
                            var onPc = new HashSet<string>(localSubfolders[f], comparer);
                            var known = KidsOf(f) ?? new HashSet<string>(comparer);
                            var next = new HashSet<string>(onPc, comparer);
                            next.UnionWith(known);
                            foreach (var n in next)
                            {
                                if (visited.Contains(n) || allSkips.Covers(n + "/")) continue;
                                // Left out on Drive last time, and its folder not
                                // listed again: still left out, records kept.
                                if (!full && !listed.Contains(f) && leftOut.Covers(n + "/")) { allSkips.Folder(n); continue; }
                                if (partial)
                                {
                                    if (wholeTree.Contains(f) || onPc.Contains(n) != known.Contains(n)) wholeTree.Add(n);
                                    else if (!focus.Contains(n)) continue;
                                }
                                queue.Enqueue(n);
                            }
                        }

                    // ---- sync the batch ----
                    var records = slice.Files;
                    Func<SyncAction, bool>? hold = hasRecords && !pair.FreesUpSpace
                        ? a => tally.Held.Count < HeldMost && IsMoveHalf(a, local, remote, records)
                        : null;
                    int heldBefore = tally.Held.Count;
                    await ExecuteAsync(client, pair, slice, rules, local, remote, folders, skips, shadowed, generation, tally,
                        plan => nextDirty.UnionWith(plan.Where(a => a.Kind is not (SyncActionKind.Record or SyncActionKind.Forget))
                            .Select(a => SyncMoves.Parent(a.Path))),
                        scope: null, hold, token).ConfigureAwait(false);
                    // Made at the end, after the held halves: a folder renamed on
                    // the PC is one rename of Drive's folder there, and making
                    // the new name first turned it into a move of every file.
                    foreach (var f in onPcHere) if (!folders.ContainsKey(f)) foldersToMake.Add(f);
                    await FreeUpAsync(client, pair, slice, rules, local, skips, tally, token).ConfigureAwait(false);
                    GoogleDrive.Trace($"sync {DisplayName(pair)} batch:{batch.Count} folders, {local.Count} PC files, " +
                                      $"{remote.Count} Drive files ({listNow.Count} folders listed), scan {scanMs} ms, " +
                                      $"records {recordsMs} ms, listing {listMs} ms, sync {timing.ElapsedMilliseconds - scanMs - recordsMs - listMs} ms");
                    for (int i = heldBefore; i < tally.Held.Count; i++)
                    {
                        var a = tally.Held[i];
                        held.Add(new HeldItem(a,
                            local.TryGetValue(a.Path, out var l) ? l : null,
                            remote.TryGetValue(a.Path, out var r) ? r : null,
                            records.TryGetValue(a.Path, out var w) ? w : null));
                    }

                    // The folders holding records, worked out once: asked folder by
                    // folder of every file in the batch, a batch of thousands of
                    // small folders was hundreds of millions of name comparisons,
                    // and a pass with nothing to do took most of an hour.
                    HashSet<string>? withRecords = null;
                    bool HasRecords(string folder)
                    {
                        withRecords ??= new HashSet<string>(slice.Files.Keys.Select(SyncMoves.Parent), comparer);
                        return withRecords.Contains(folder);
                    }
                    foreach (var f in batch)
                    {
                        if (store.Present(f) is bool p)
                        {
                            if (p) shardFolders.Add(f);
                            else shardFolders.Remove(f);
                        }
                        else if (HasRecords(f)) shardFolders.Add(f);
                        if (store.HasFiles(f) is bool h)
                        {
                            if (h) recordFolders.Add(f);
                            else recordFolders.Remove(f);
                        }
                        else if (HasRecords(f)) recordFolders.Add(f);
                    }
                    // The folders map only on the feed, where it only grows: part
                    // way through a full listing it is only part of the tree.
                    if (!full) header.FolderIds = folders;
                    header.ShardFolders = shardFolders.ToList();
                    header.RecordFolders = recordFolders.ToList();
                    SaveState(pair.Id, header);
                }

                // ---- the held halves, together ----
                if (held.Count > 0 && tally.SpaceProblem == null)
                    await SyncHeldAsync(client, pair, header, store, rules, held, folders, allSkips, shardFolders, recordFolders, full,
                        generation, tally, nextDirty, token).ConfigureAwait(false);

                // ---- empty PC folders Drive does not have ----
                // "Drive had it before this pass" is what Drive had when the pass
                // began: on a full listing knownAtStart is only the root, and a
                // folder deleted in Drive was made again straight away.
                if (foldersToMake.Count > 0 && tally.SpaceProblem == null)
                {
                    var driveBefore = new HashSet<string>(header.Remembered?.Keys ?? header.FolderIds.Keys, comparer);
                    await MakeEmptyFoldersAsync(client, pair, header, rules, foldersToMake, folders, driveBefore, allSkips, tally, token)
                        .ConfigureAwait(false);
                    ClearFoldersGoneFromDrive(pair, rules, allSkips, foldersToMake, folders, driveBefore,
                        f => full || listedAll.Contains(SyncMoves.Parent(f)));
                }

                // ---- records of folders neither side has any more ----
                // (A pass that looked at only some folders left the rest alone on purpose,
                // and one stopped short for space never reached the rest.)
                foreach (var f in partial || tally.SpaceProblem != null ? new List<string>() : shardFolders.ToList())
                {
                    if (visited.Contains(f) || allSkips.Covers(f + "/")) continue;
                    if (!pair.IncludeSubfolders && f.Length > 0) continue;
                    store.Delete(f);
                    shardFolders.Remove(f);
                    recordFolders.Remove(f);
                }
                if (full && pair.IncludeSubfolders && tally.SpaceProblem == null) store.SweepOrphans(shardFolders, visited, allSkips);
            }
            catch (InvalidDataException damaged)
            {
                PauseForDamagedShard(pair, header, damaged.Message);
                return;
            }
            catch
            {
                // Stopped part way: the folders it made and the folders with
                // records are kept, so the next pass neither makes a folder twice
                // nor misses records it wrote.
                foreach (var (path, id) in folders)
                    if (!knownAtStart.Contains(path) || !full) header.FolderIds[path] = id;
                header.ShardFolders = shardFolders.ToList();
                header.RecordFolders = recordFolders.ToList();
                try { SaveState(pair.Id, header); } catch (Exception ex) { Diag.Note(ex); }
                throw;
            }

            // What was left out before and has not been listed again this pass.
            bool Unlisted(string entry) => !listedAll.Contains(SyncMoves.Parent(entry.TrimEnd('/')));
            if (!full)
            {
                unusable.AddRange(header.DriveUnusable.Where(Unlisted));
                clashing.AddRange(header.DriveClashing.Where(Unlisted));
            }

            // Stopped short for space: the changes read for folders it never
            // reached are read again next pass, not used up.
            if (tally.SpaceProblem == null) header.ChangesToken = nextToken;
            // A whole listing stopped for space reached only part of the tree: add what
            // it found to the old map instead of replacing it.
            if (full && tally.SpaceProblem != null)
                foreach (var (path, id) in folders) header.FolderIds[path] = id;
            else
                header.FolderIds = folders;
            header.DirtyFolders = nextDirty.ToList();
            if (full && tally.SpaceProblem == null)
            {
                header.LastFullListUtc = DateTime.UtcNow;
                header.ScanSubfolders = pair.IncludeSubfolders;
            }
            header.DriveUnusable = unusable.Distinct(StringComparer.Ordinal).ToList();
            header.DriveClashing = clashing.Distinct(StringComparer.Ordinal).ToList();
            header.ShardFolders = shardFolders.ToList();
            header.RecordFolders = recordFolders.ToList();
            header.Sharded = true;

            // The PC side: names left out where nothing was looked at stay said,
            // the totals cover the folders not visited, and the journal's place
            // is kept with the records it goes with.
            if (partial)
                localClashing.AddRange(header.LocalClashing.Where(e => !visited.Contains(SyncMoves.Parent(e.TrimEnd('/')))));
            header.LocalClashing = localClashing.Distinct(StringComparer.Ordinal).ToList();
            LastFoldersVisited = visited.Count;
            var totals = Totals(localSeen, driveSeen, tally.Matched);
            // Kept as they stand once this pass is done: what it copied is in step now.
            // Free up space: freed files have no records, so a listed folder's
            // Drive files were added on top of the saved total and nothing taken
            // off, and "On Google Drive" grew every pass. As the small path, the
            // total is what a whole listing found, plus what went up since.
            int driveTotal = totals.Drive + tally.Uploaded;
            if (pair.FreesUpSpace)
            {
                if (!(full && tally.SpaceProblem == null) && header.DriveFileTotal is int before) driveTotal = before + tally.Uploaded;
                header.DriveFileTotal = driveTotal;
            }
            header.PcFiles = totals.Pc + tally.Downloaded;
            header.DriveFiles = driveTotal;
            header.MatchedFiles = totals.Matched + tally.Uploaded + tally.Downloaded;     // a moved file is counted as matched already
            int driveFound = pair.FreesUpSpace ? driveTotal : totals.Drive;
            Report(pair.Id, s => s with { LocalFound = totals.Pc, DriveFound = driveFound, Matched = totals.Matched }, now: true);
            // Stopped short for space: folders it never reached are still owed
            // the changes read for them, so the journal's place stays where it was.
            if (tally.SpaceProblem == null) PcPlanned(pair, header, look, walkedWhole: !partial, links);
            ClearEmptyFolders(pair, header, rules, allSkips, visited, folders);
            SaveState(pair.Id, header);
            await TidyTwinFoldersAsync(client, pair, rules, clashing, folders, tally, token).ConfigureAwait(false);
            FinishPass(pair, header, tally, unusable, clashing, localClashing);
        }

        /// <summary>
        /// Whether the pair's Drive folder is in a shared drive. Files there
        /// count against the organisation's storage, not the person's own, so
        /// the account's quota says nothing about room there (a full My Drive
        /// stopped every upload to a shared drive as "Google Drive is full").
        /// Google's own refusal still stops a shared drive that really is full.
        /// </summary>
        private bool OnSharedDrive(DriveSyncPair pair)
        {
            if ((pair.DriveFolderPath ?? "").StartsWith("Shared drives/", StringComparison.Ordinal)) return true;
            lock (_gate) return _driveOf.TryGetValue(pair.Id, out var driveId) && driveId != null;
        }

        /// <summary>
        /// Two-way with deletes: a folder deleted in Google Drive goes from the
        /// PC too, once the pass has recycled its files. Otherwise the empty PC
        /// folder stayed, and the next walk made it in Drive again. Only folders
        /// Drive had when the pass began, whose folder above was listed this
        /// pass (<paramref name="parentListed"/>), not left out, and holding
        /// nothing at all: the delete is Windows' own, which refuses a folder
        /// with anything in it, so a file kept or edited on the PC keeps its
        /// folder. Deepest first, then the folder above the same way.
        /// </summary>
        private void ClearFoldersGoneFromDrive(DriveSyncPair pair, SyncRules rules, SyncSkips skips, IEnumerable<string> pcFolders,
            IReadOnlyDictionary<string, string> folders, IReadOnlySet<string> driveBefore, Func<string, bool> parentListed)
        {
            if (rules.Mode != SyncMode.TwoWay || !rules.CopyDeletes || !rules.IncludeSubfolders) return;
            const FileAttributes kept = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;
            bool Gone(string at) => at.Length > 0 && !folders.ContainsKey(at) && driveBefore.Contains(at) && parentListed(at) &&
                                    !IsLeftOutFolder(at, rules, skips) &&
                                    !at.Split('/').Any(part => part.Equals(".sync", StringComparison.OrdinalIgnoreCase));
            foreach (var folder in pcFolders.Distinct(SyncNameComparer.Instance).OrderByDescending(f => f.Count(c => c == '/')))
            {
                if (Stopped(pair)) break;
                for (var at = folder; Gone(at); at = SyncMoves.Parent(at))
                {
                    var dir = new DirectoryInfo(LocalPathFor(pair.LocalFolder, at));
                    try
                    {
                        if (!dir.Exists) continue;      // gone already: the one above may be empty now
                        if ((dir.Attributes & kept) != 0 || dir.EnumerateFileSystemInfos().Any()) break;
                        dir.Delete(recursive: false);
                        SyncHistory.Add(pair.Id, "Deleted folder", at, "it was deleted in Google Drive");
                    }
                    catch (Exception) { break; }
                }
            }
        }

        /// <summary>
        /// Whether an action could be one half of a move: a new file on one side,
        /// or a recorded file gone from one side and still on the other.
        /// </summary>
        private static bool IsMoveHalf(SyncAction a, IReadOnlyDictionary<string, SyncFile> local,
            IReadOnlyDictionary<string, SyncFile> remote, IReadOnlyDictionary<string, SyncBase> records)
        {
            bool hasL = local.ContainsKey(a.Path), hasR = remote.ContainsKey(a.Path), hasB = records.ContainsKey(a.Path);
            return a.Kind switch
            {
                SyncActionKind.Upload => (!hasB && !hasR && hasL) || (hasB && hasL && !hasR),
                SyncActionKind.Download => (!hasB && !hasL && hasR) || (hasB && !hasL && hasR),
                SyncActionKind.DeleteRemote => hasB && !hasL && hasR,
                SyncActionKind.DeleteLocal => hasB && hasL && !hasR,
                SyncActionKind.Forget => hasB && (hasL != hasR),
                _ => false,
            };
        }

        /// <summary>
        /// The end of a batched pass: everything held back, synced together, so
        /// a move between two batches is seen as one. On the changes feed, a
        /// file that turned up somewhere new on Drive may have come from a folder
        /// the feed did not mark; the records are searched for its id, and the
        /// file it was is added, but only when both are still exactly what was
        /// recorded, so that the pair is certain to be taken for a move and
        /// nothing else.
        /// </summary>
        private async Task SyncHeldAsync(DriveClient client, DriveSyncPair pair, PairState header, ShardStore store,
            SyncRules rules, List<HeldItem> held, Dictionary<string, string> folders, SyncSkips skips,
            HashSet<string> shardFolders, HashSet<string> recordFolders, bool full, int generation, PassTally tally, HashSet<string> nextDirty,
            CancellationToken token)
        {
            var comparer = SyncNameComparer.Instance;
            var root = pair.LocalFolder;
            var local = new Dictionary<string, SyncFile>(comparer);
            var remote = new Dictionary<string, SyncFile>(comparer);
            var scope = new HashSet<string>(comparer);
            var involved = new HashSet<string>(comparer);
            foreach (var h in held)
            {
                scope.Add(h.Action.Path);
                involved.Add(SyncMoves.Parent(h.Action.Path));
                if (h.Local is { } l) local[h.Action.Path] = l;
                if (h.Remote is { } r) remote[h.Action.Path] = r;
            }

            // Moved on Drive from a folder the feed said nothing about.
            if (!full)
            {
                var wanted = new Dictionary<string, (string Path, SyncFile At)>(StringComparer.Ordinal);
                foreach (var h in held)
                    if (h.Was == null && h.Local == null && h.Remote is { Id: { } id } r) wanted.TryAdd(id, (h.Action.Path, r));
                if (wanted.Count > 0)
                    foreach (var folder in shardFolders.ToList())
                    {
                        token.ThrowIfCancellationRequested();
                        foreach (var (path, was) in store.Load(folder).Files)
                        {
                            if (scope.Contains(path) || !wanted.TryGetValue(was.RemoteId, out var to)) continue;
                            if (SyncPlanner.RemoteChanged(to.At, was)) continue;
                            var full_ = LocalPathFor(root, path);
                            FileInfo info;
                            try { info = new FileInfo(full_); if (!info.Exists) continue; }
                            catch { continue; }
                            var now = new SyncFile(info.Length, Truncate(info.LastWriteTimeUtc));
                            if (SyncPlanner.LocalChanged(now, was)) continue;
                            local[path] = now;
                            scope.Add(path);
                            involved.Add(SyncMoves.Parent(path));
                            wanted.Remove(was.RemoteId);
                        }
                        if (wanted.Count == 0) break;
                    }
            }

            // Only these paths' records, however many folders they are in. A move
            // can land in a folder that is in no batch's records (a new one), so
            // the destinations' folders are in the set too.
            var slice = new PairState
            {
                Store = store, SliceFolders = involved, SlicePaths = scope, PendingTrash = header.PendingTrash,
                LocalFolder = header.LocalFolder, DriveFolderId = header.DriveFolderId, Remembered = header.Remembered,
            };
            foreach (var f in involved) store.LoadPathsInto(f, scope, slice);

            await ExecuteAsync(client, pair, slice, rules, local, remote, folders, skips,
                new Dictionary<string, List<SyncFile>>(comparer), generation, tally,
                plan => nextDirty.UnionWith(plan.Where(a => a.Kind is not (SyncActionKind.Record or SyncActionKind.Forget))
                    .Select(a => SyncMoves.Parent(a.Path))),
                scope, hold: null, token).ConfigureAwait(false);

            foreach (var f in involved)
            {
                if (store.Present(f) is bool p)
                {
                    if (p) shardFolders.Add(f);
                    else shardFolders.Remove(f);
                }
                if (store.HasFiles(f) is bool h)
                {
                    if (h) recordFolders.Add(f);
                    else recordFolders.Remove(f);
                }
            }
        }

        /// <summary>A records file that will not read: kept aside, said, and the pair paused, as for the whole state.</summary>
        private void PauseForDamagedShard(DriveSyncPair pair, PairState header, string path)
        {
            var aside = path + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            try { File.Move(path, aside); } catch { try { File.Delete(path); } catch { } }
            var problem = "paused: part of what it remembered about the last sync could not be read, and was kept as " +
                          Path.GetFileName(aside) + ". Unpause it to compare that folder afresh; nothing will be deleted.";
            header.Problem = problem;
            SaveState(pair.Id, header);
            SetStatus(pair.Id, new PairStatus(header.LastSyncedUtc, problem, false));
            _notify("sync.error", $"{DisplayName(pair)} is {problem}");

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

        /// <summary>
        /// The plan's paths that act on a recorded file being gone from Drive (the
        /// PC's copy sent up as new, recycled, or forgotten) while Drive, asked
        /// about that very file, still has it in the same folder: the listing was
        /// behind. Asked in parallel, and only for these few.
        /// </summary>
        private async Task<HashSet<string>> ListedButStillThereAsync(DriveClient client, DriveSyncPair pair, PairState state,
            List<SyncAction> plan, Dictionary<string, SyncFile> local, Dictionary<string, SyncFile> remote,
            Dictionary<string, string> folders, CancellationToken token)
        {
            var stale = new HashSet<string>(SyncNameComparer.Instance);
            var ask = new List<(string Path, string Id, string Folder)>();
            foreach (var a in plan)
            {
                if (a.Kind is not (SyncActionKind.DeleteLocal or SyncActionKind.Upload or SyncActionKind.KeepBothLocalWins
                        or SyncActionKind.Forget)) continue;
                if (remote.ContainsKey(a.Path) || !local.ContainsKey(a.Path)) continue;
                if (!state.Files.TryGetValue(a.Path, out var was) || string.IsNullOrEmpty(was.RemoteId)) continue;
                var parent = SyncMoves.Parent(a.Path);
                var folderId = parent.Length == 0 ? pair.DriveFolderId : folders.TryGetValue(parent, out var f) ? f : null;
                if (folderId == null) continue;      // its folder is gone from Drive: really gone
                ask.Add((a.Path, was.RemoteId, folderId));
            }
            if (ask.Count == 0) return stale;

            await Parallel.ForEachAsync(ask, new ParallelOptions { MaxDegreeOfParallelism = ListStreams, CancellationToken = token },
                async (q, ct) =>
                {
                    (string Name, IReadOnlyList<string> Parents)? place;
                    try { place = await Patiently(pair, () => client.PlaceNow(q.Id, ct), client, ct).ConfigureAwait(false); }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        // Could not ask: not acted on this pass either.
                        lock (stale) stale.Add(q.Path);
                        return;
                    }
                    // Still in the same folder under the same name: the listing was
                    // behind. Renamed or moved, it really is not at this path.
                    if (place is { } here && here.Parents.Contains(q.Folder, StringComparer.Ordinal) &&
                        SyncNameComparer.Instance.Equals(here.Name, SyncMoves.Leaf(q.Path)))
                        lock (stale) stale.Add(q.Path);
                }).ConfigureAwait(false);
            return stale;
        }

        /// <summary>What a pass did, added up across its batches.</summary>
        private sealed class PassTally
        {
            public int Uploaded, Downloaded, Deleted, Failed, Deferred, Moved, Matched;

            /// <summary>PC files free up space deleted, their copies safely in Drive. Said apart from deletes.</summary>
            public int FreedUp;
            public string? LastError, SpaceProblem;

            /// <summary>Drive folders with a twin of exactly the same name beside them, left out (see TidyTwinFoldersAsync).</summary>
            public List<string> Twins = new();

            /// <summary>Going a batch at a time: the start of a big pass is said once, not per batch.</summary>
            public bool Batched, Announced;

            /// <summary>Transfers and bytes so far in a batched pass, to tell when it has become a big one.</summary>
            public long Bytes;
            public int Transfers;
            public readonly List<string> Conflicts = new();

            /// <summary>Actions held back by a batch for the end of the pass.</summary>
            public readonly List<SyncAction> Held = new();

            /// <summary>Names of PC files left for a later pass because another program had them open.</summary>
            public readonly List<string> InUse = new();
        }

        /// <summary>The end of a pass: what it says, and the pair's state as it is left.</summary>
        private void FinishPass(DriveSyncPair pair, PairState state, PassTally tally,
            List<string> unusable, List<string> clashing, List<string> localClashing)
        {
            WarnIfLowOnSpace(pair);
            var name = DisplayName(pair);
            var conflicts = tally.Conflicts;

            // Nothing left for a later pass: until Drive or the PC changes, the
            // next minute need not look (DuePairsAsync).
            if (tally.SpaceProblem == null && tally.Failed == 0 && tally.Deferred == 0 && tally.Held.Count == 0 &&
                tally.InUse.Count == 0 && state.DirtyFolders.Count == 0)
                lock (_gate) _clean.Add(pair.Id);
            if (conflicts.Count > 0)
                _notify("sync.conflict", conflicts.Count == 1
                    ? $"{conflicts[0]} in {name} changed in both places, so both copies were kept"
                    : $"{Capitalise(NameRules.Counted(conflicts.Count, "file"))} in {name} changed in both places, so both copies of each were kept");

            if (tally.Uploaded + tally.Downloaded + tally.Deleted + tally.Moved + tally.FreedUp > 0)
                _notify("sync.done", $"Synced {name}: " + Tally(tally.Uploaded, tally.Downloaded, tally.Deleted, tally.Moved, tally.FreedUp));

            var spaceProblem = tally.SpaceProblem;
            if (spaceProblem != null)
            {
                // Not a setting: the pair is not marked paused, so it carries on
                // by itself on a later pass once there is room.
                bool newProblem = spaceProblem != state.Problem;
                state.Problem = spaceProblem;
                SaveState(pair.Id, state);
                SetStatus(pair.Id, StatusOf(pair.Id) with { LastSyncedUtc = state.LastSyncedUtc, Problem = spaceProblem, Running = false, Phase = null });
                if (newProblem) _notify("sync.space", $"{name} is {spaceProblem}");
                return;
            }

            if (tally.Failed > 0)
            {
                Finish(pair, state, tally.Failed == 1 ? tally.LastError : $"{tally.Failed} files could not be synced. {tally.LastError}", announce: true);
                return;
            }

            // Everything else is in step; these few never can be, and say so
            // rather than failing on every pass or landing somewhere else.
            if (unusable.Count > 0 || clashing.Count > 0 || localClashing.Count > 0 || tally.Twins.Count > 0 ||
                tally.InUse.Count > 0)
            {
                var left = new List<string>();
                if (tally.InUse.Count > 0) left.Add(InUseNames(tally.InUse));
                if (unusable.Count > 0) left.Add(UnusableNames(unusable));
                if (tally.Twins.Count > 0) left.Add(TwinNames(tally.Twins));
                if (clashing.Count > 0) left.Add(ClashingNames(clashing));
                if (localClashing.Count > 0) left.Add(ClashingOnPc(localClashing));
                // A file that is only open somewhere is shown, not said: it sorts itself out.
                Finish(pair, state, string.Join(". ", left), announce: left.Count > (tally.InUse.Count > 0 ? 1 : 0), synced: true);
                return;
            }

            Finish(pair, state, null, announce: false);
        }


        /// <summary>"Google Drive has two folders called x in the same place, so it was left out. ..."</summary>
        internal static string TwinNames(IReadOnlyList<string> names)
        {
            const int shown = 5;
            var list = string.Join(", ", names.Take(shown)) + (names.Count > shown ? $" and {names.Count - shown} more" : "");
            return names.Count == 1
                ? $"Google Drive has two folders called {names[0]} in the same place, and both have files in them, so it was left out. " +
                  "Move what is in one into the other in Google Drive, and it syncs again"
                : $"Google Drive has {names.Count} places with two folders of the same name, both with files in them, so they were left out: {list}. " +
                  "Move what is in one of each pair into the other in Google Drive, and they sync again";
        }

        /// <summary>
        /// Two Drive folders of exactly one name in one place are left out (the
        /// sync cannot tell which is the PC's). Most come from a create whose
        /// answer was lost, and one of them is empty (Ruby 3/Disc_7_of_10 sat
        /// out of every pass because of one, its files never going up). So: an
        /// exact twin whose others are all empty has the empty ones put in
        /// Drive's trash, where they can be restored, and the folder syncs again
        /// on the next pass (the trash shows in the changes feed). Twins that
        /// both hold something are said as such, not as a Windows spelling
        /// clash. Never in download only, which does not write to Drive.
        /// </summary>
        private async Task TidyTwinFoldersAsync(DriveClient client, DriveSyncPair pair, SyncRules rules,
            List<string> clashing, IReadOnlyDictionary<string, string> folders, PassTally tally, CancellationToken token)
        {
            foreach (var entry in clashing.Where(e => e.EndsWith('/')).Distinct(StringComparer.Ordinal).ToList())
            {
                if (token.IsCancellationRequested || Stopped(pair)) return;
                var path = entry.TrimEnd('/');
                var parent = SyncMoves.Parent(path);
                var parentId = parent.Length == 0 ? pair.DriveFolderId : folders.GetValueOrDefault(parent);
                if (parentId == null) continue;
                var leaf = SyncMoves.Leaf(path);
                try
                {
                    var siblings = await Patiently(pair, () => client.ListChildren(parentId, token, max: int.MaxValue), client, token).ConfigureAwait(false);
                    var same = siblings.Where(e => e.IsFolder && string.Equals(e.Name, leaf, StringComparison.Ordinal)).ToList();
                    if (same.Count < 2) continue;      // two spellings, not twins: said as before
                    clashing.Remove(entry);
                    if (rules.Mode == SyncMode.DownloadOnly) { tally.Twins.Add(path); continue; }

                    var empty = new List<DriveEntry>();
                    foreach (var twin in same)
                    {
                        var inside = await Patiently(pair, () => client.ListChildren(twin.Id, token, max: 1, partial: true), client, token)
                            .ConfigureAwait(false);
                        if (inside.Count == 0) empty.Add(twin);
                    }
                    if (empty.Count < same.Count - 1) { tally.Twins.Add(path); continue; }

                    // All empty: one stays. Otherwise the one holding something stays.
                    var toTrash = empty.Count == same.Count ? empty.Skip(1).ToList() : empty;
                    foreach (var twin in toTrash)
                        await Patiently(pair, async () => { await client.Trash(twin.Id, token).ConfigureAwait(false); return true; },
                            client, token).ConfigureAwait(false);
                    SyncHistory.Add(pair.Id, "Removed empty duplicate folder", path);
                    _notify("sync.done", $"{path} had an empty copy of itself in Google Drive, so the empty one was put in the trash and it syncs again");
                    SoonAfter(pair.Id, TimeSpan.FromSeconds(5));
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    tally.Twins.Add(path);
                }
            }
        }

        /// <summary>"notes.docx is open in another program, so it syncs once that program lets go of it".</summary>
        internal static string InUseNames(IReadOnlyList<string> names)
        {
            const int shown = 5;
            var distinct = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var list = string.Join(", ", distinct.Take(shown)) + (distinct.Count > shown ? $" and {distinct.Count - shown} more" : "");
            return distinct.Count == 1
                ? $"{distinct[0]} is open in another program, so it syncs once that program lets go of it"
                : $"{distinct.Count} files are open in other programs, so they sync once those programs let go of them: {list}";
        }

        /// <summary>
        /// Windows refused because another program has the file open: a sharing
        /// or lock violation, anywhere in the chain. Waited out, not failed.
        /// </summary>
        /// <remarks>
        /// Moving a download over a file another program has open is refused
        /// as "access denied", the same words as a file the account may not
        /// touch at all; so that refusal is checked by opening the file with
        /// nobody else allowed in, which only a file in use refuses as a
        /// sharing violation.
        /// </remarks>
        internal static bool InUseElsewhere(Exception ex, string? localPath = null)
        {
            static bool Sharing(Exception e) => e is IOException && (e.HResult & 0xFFFF) is 32 or 33;   // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
            bool denied = false;
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (Sharing(e)) return true;
                if (e is UnauthorizedAccessException) denied = true;
            }
            if (!denied || localPath == null) return false;
            try
            {
                using var alone = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.None);
                return false;
            }
            catch (Exception probe) { return Sharing(probe); }
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
            SetStatus(pair.Id, StatusOf(pair.Id) with { LastSyncedUtc = state.LastSyncedUtc, Problem = problem, Running = false, Phase = null });
            if (announce && newProblem) _notify("sync.error", $"{DisplayName(pair)} could not sync: {problem}");
        }

        internal static string Tally(int uploaded, int downloaded, int deleted, int moved = 0, int freedUp = 0)
        {
            var parts = new List<string>();
            if (moved > 0) parts.Add($"{moved} moved");
            if (uploaded > 0) parts.Add($"{uploaded} uploaded");
            if (downloaded > 0) parts.Add($"{downloaded} downloaded");
            if (deleted > 0) parts.Add($"{deleted} deleted");
            if (freedUp > 0) parts.Add($"{freedUp} cleared off this PC");
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
            : RootName(pair.LocalFolder);

        /// <summary>A whole drive's name, as a synced folder is called by default: "D drive".</summary>
        internal static string RootName(string folder) =>
            folder.Length >= 2 && folder[1] == ':' ? $"{char.ToUpperInvariant(folder[0])} drive" : folder;

        /// <summary>
        /// Why a PC folder cannot be monitored, or null when it can. The Drive
        /// letter and the app's own settings folder (which holds Drive's sync
        /// root) are refused: walking them reads every file from Google.
        /// </summary>
        public static string? Refusal(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return "No folder on this PC was chosen.";

            string full;
            try { full = FolderPathOf(folder); }
            catch { return "That is not a folder path."; }

            if (!Directory.Exists(full)) return $"{full} does not exist.";

            // A whole drive may be synced (an external drive, all of D), but not
            // the one Windows is on: Program Files, Windows and every user's
            // settings would go up to Google Drive.
            if (IsDriveRoot(full) && string.Equals(Path.GetPathRoot(Environment.SystemDirectory), full,
                    StringComparison.OrdinalIgnoreCase))
                return $"{full} is the drive Windows is on, so all of it cannot be synced. Choose a folder on it, or another drive.";
            full = full.TrimEnd(Path.DirectorySeparatorChar);

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

        /// <summary>
        /// A PC folder as a synced folder keeps it: the full path with no
        /// separator at the end, except a whole drive, which keeps its own
        /// ("D:\"). Trimmed, "D:" means the current folder on D, not its top.
        /// </summary>
        internal static string FolderPathOf(string folder)
        {
            folder = folder.Trim();
            // A bare "C:" is the current folder on C, not the drive (see CLAUDE.md).
            if (folder.Length == 2 && folder[1] == ':' && char.IsAsciiLetter(folder[0])) folder += Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(folder);
            if (IsDriveRoot(full)) return Path.GetPathRoot(full)!.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.TrimEnd(Path.DirectorySeparatorChar);
        }

        /// <summary>Whether a full path is a drive's (or a share's) own top folder.</summary>
        internal static bool IsDriveRoot(string full)
        {
            var root = Path.GetPathRoot(full);
            return !string.IsNullOrEmpty(root) &&
                   full.TrimEnd(Path.DirectorySeparatorChar).Length <= root.TrimEnd(Path.DirectorySeparatorChar).Length;
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

        // ---------------- state ----------------

        private enum StateRead { Fine, Missing, Damaged, Busy }

        private static (PairState State, StateRead How) ReadState(string pairId)
        {
            var path = StatePath(pairId);
            try
            {
                if (!File.Exists(path)) return (new PairState(), StateRead.Missing);
                var state = JsonSerializer.Deserialize<PairState>(ProtectedFile.ReadAllUtf8(path)) ?? new PairState();
                state.Files = Rekeyed(state.Files);
                state.Partials = Rekeyed(state.Partials);
                state.Uploads = Rekeyed(state.Uploads);
                state.Hashes = Rekeyed(state.Hashes);
                state.FolderIds = Rekeyed(state.FolderIds);
                state.DirtyFolders ??= new();
                state.DriveUnusable ??= new();
                state.DriveClashing ??= new();
                state.ShardFolders ??= new();
                state.RecordFolders ??= new();
                state.LocalClashing ??= new();
                state.PendingTrash = new Dictionary<string, PendingTrash>(state.PendingTrash ?? new(), StringComparer.Ordinal);
                ShareFolderNames(state);
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

        /// <summary>
        /// One string per folder path, however many of a large pair's lists name
        /// it. Read from the file, the folder table and the two folder lists each
        /// had their own copy: a hundred thousand folders kept three of every
        /// path alive for the whole pass.
        /// </summary>
        private static void ShareFolderNames(PairState state)
        {
            if (state.ShardFolders.Count + state.RecordFolders.Count + state.DirtyFolders.Count == 0) return;
            var pool = new HashSet<string>(state.FolderIds.Keys, StringComparer.Ordinal);
            void Share(List<string> list)
            {
                for (int i = 0; i < list.Count; i++)
                    if (pool.TryGetValue(list[i], out var same)) list[i] = same;
                    else pool.Add(list[i]);
            }
            Share(state.ShardFolders);
            Share(state.RecordFolders);
            Share(state.DirtyFolders);
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

        /// <summary>Drops what the changes feed relies on, so the next pass lists Drive whole.</summary>
        private static void ForgetFeed(PairState state)
        {
            state.ChangesToken = null;
            state.FolderIds.Clear();
            state.DirtyFolders.Clear();
            state.LastFullListUtc = null;
            state.ScanSubfolders = null;
            state.DriveUnusable.Clear();
            state.DriveClashing.Clear();
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
                ForgetFeed(state);
                if (state.Sharded)
                {
                    try { Directory.Delete(new ShardStore(pair.Id).Directory, true); } catch { }
                    state.Sharded = false;
                    state.ShardFolders.Clear();
                    state.RecordFolders.Clear();
                }
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

                // A large pair's records by folder go with it: without the state
                // that lists them they are not what was synced.
                var shards = new ShardStore(pair.Id).Directory;
                try { if (Directory.Exists(shards)) Directory.Move(shards, shards + ".damaged-" + Path.GetFileName(aside)[^15..]); }
                catch { try { Directory.Delete(shards, true); } catch { } }
            }

            var problem = "paused: what it remembered about the last sync could not be read, and was kept as " +
                          Path.GetFileName(aside) + ". Unpause it to compare both folders afresh; nothing will be deleted.";
            fresh.Problem = problem;
            SaveState(pair.Id, fresh);
            SetStatus(pair.Id, new PairStatus(null, problem, false));
            _notify("sync.error", $"{DisplayName(pair)} is {problem}");

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

        /// <summary>State files (or slices) written by this monitor. For tests.</summary>
        internal int StateSaves;

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
                Interlocked.Increment(ref StateSaves);
                if (state.Store != null)
                {
                    long began = Environment.TickCount64;
                    try { state.Store.SaveSlice(state); } catch (Exception ex) { Diag.Note(ex); }
                    lock (_gate) _saveCost[pairId] = Environment.TickCount64 - began;
                    return;
                }
                try
                {
                    Directory.CreateDirectory(StateDirectory);
                    var path = StatePath(pairId);
                    var temp = path + "." + Environment.ProcessId + ".tmp";
                    long started = Environment.TickCount64;
                    ProtectedFile.WriteAllUtf8(temp, JsonSerializer.SerializeToUtf8Bytes(state));
                    File.Move(temp, path, overwrite: true);
                    lock (_gate) _saveCost[pairId] = Environment.TickCount64 - started;
                }
                catch (Exception ex) { Diag.Note(ex); }
            }
        }

        /// <summary>
        /// Writes the state if it has not been written for two seconds, or at
        /// once when <paramref name="now"/>. The pass writes it whole at its end,
        /// and a crash in between loses at most two seconds of records, which
        /// the next pass adopts again by comparing both sides.
        /// </summary>
        /// <summary>How long a save took last time, per pair, in milliseconds.</summary>
        private readonly Dictionary<string, long> _saveCost = new();

        /// <summary>
        /// Two seconds, or twenty times as long as the last save took. With
        /// millions of files the records are hundreds of megabytes, and writing
        /// them every two seconds was the pass doing little else. A crash then
        /// loses a little more, which the next pass adopts again by MD5.
        /// </summary>
        internal static long SaveGap(long lastCostMilliseconds) => Math.Max(2000, lastCostMilliseconds * 20);

        /// <summary>Files checked at once when they average under <see cref="HashSmallAverage"/> bytes.</summary>
        internal const int HashLanesSmall = 8;
        internal const long HashSmallAverage = 1 << 20;

        private void SaveSoon(string pairId, PairState state, bool now = false)
        {
            if (!now)
                lock (_gate)
                    if (_savedAt.TryGetValue(pairId, out var at) &&
                        Environment.TickCount64 - at < SaveGap(_saveCost.TryGetValue(pairId, out var cost) ? cost : 0)) return;
            SaveState(pairId, state);
        }

        /// <summary>Removes what was remembered for pairs that no longer exist.</summary>
        public static void ForgetRemovedPairs(IEnumerable<DriveSyncPair> pairs)
        {
            List<string> asked;
            try { asked = pairs.Select(p => p.Id).ToList(); }
            catch { return; }

            // Off the UI thread: a pass writing a big state holds StateLock for
            // the whole write, and Pause or Edit froze the window (NVDA silent)
            // until it finished. One clean-up at a time, in the order asked.
            if (SynchronizationContext.Current != null)
            {
                lock (ForgetChainGate)
                    _forgetChain = _forgetChain.ContinueWith(_ => ForgetRemovedPairsNow(asked),
                        CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                return;
            }
            ForgetRemovedPairsNow(asked);
        }

        private static readonly object ForgetChainGate = new();
        private static Task _forgetChain = Task.CompletedTask;

        private static void ForgetRemovedPairsNow(List<string> asked)
        {
            try
            {
                if (!Directory.Exists(StateDirectory)) return;

                // Under the state lock, so a save already under way finishes
                // first, and the next one sees the pair removed.
                lock (StateLock)
                {
                    // What to keep is decided now, not when this was asked: a
                    // pair added since (the monitor has it) is never deleted.
                    var keep = new HashSet<string>(asked.Select(id => id + ".json"), StringComparer.OrdinalIgnoreCase);
                    if (Current is { } monitor)
                        foreach (var pair in monitor.Pairs) keep.Add(pair.Id + ".json");

                    foreach (var file in Directory.GetFiles(StateDirectory, "*.json"))
                    {
                        // The 24-hour upload tally lives here too, and is no pair's.
                        if (Path.GetFileName(file).Equals(UploadedFileName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!keep.Contains(Path.GetFileName(file))) { try { File.Delete(file); } catch { } }
                    }
                    foreach (var dir in Directory.GetDirectories(StateDirectory, "*.shards"))
                        if (!keep.Contains(Path.GetFileNameWithoutExtension(dir) + ".json")) { try { Directory.Delete(dir, true); } catch { } }
                    foreach (var pattern in new[] { "*.history", "*.listing", "*.running" })
                        foreach (var file in Directory.GetFiles(StateDirectory, pattern))
                            if (!keep.Contains(Path.GetFileNameWithoutExtension(file) + ".json")) { try { File.Delete(file); } catch { } }
                }
            }
            catch { }
        }

        /// <summary>The loop that runs the passes, waited for (briefly) on the way out.</summary>
        private readonly Task _loop;

        /// <summary>
        /// How long closing (Quit, or an update standing this copy down) waits
        /// for a pass under way to stop at its next file and save where it got
        /// to. Not waited for, the process could end between saves: the next
        /// start then took it for a crash and waited 30 seconds before going on.
        /// </summary>
        internal static TimeSpan StopWait { get; set; } = TimeSpan.FromSeconds(5);

        public void Dispose()
        {
            try { _stop.Cancel(); } catch { }
            CutWait();
            try { _drive.ConnectionChanged -= _onConnection; } catch { }
            lock (_gate)
            {
                _disposed = true;
                foreach (var watcher in _watchers.Values) { try { watcher.Dispose(); } catch { } }
                _watchers.Clear();
                _debounce?.Dispose();
                _debounce = null;
                _settled?.Dispose();
                _settled = null;
            }
            try { _loop.Wait(StopWait); } catch { }
            // The last 30 seconds of uploads are kept for the 24-hour tally too.
            try { _uploadedRecently?.Flush(); } catch (Exception ex) { Diag.Note(ex); }
            if (Current == this) Current = null;
        }
    }
}
