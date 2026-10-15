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
    /// </summary>
    internal sealed class DriveMonitor : IDisposable
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
            try { Changed?.Invoke(); } catch { }
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
                    _watchJournals.Remove(gone);
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

                    // A new watcher has seen nothing yet: until it has watched a
                    // whole walk, what it says is not the whole story.
                    _watchJournals.Remove(pair.Id);
                    if (pair.Paused || !Directory.Exists(pair.LocalFolder)) continue;

                    try
                    {
                        var watcher = new FileSystemWatcher(pair.LocalFolder)
                        {
                            IncludeSubdirectories = pair.IncludeSubfolders,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                           NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
                            InternalBufferSize = 64 * 1024,
                        };

                        // Events make a pass due, and say which folders to list
                        // again where the volume has no change journal (see
                        // LookAtPc). A buffer overflow (Error) loses that, and the
                        // next pass walks the whole folder.
                        //
                        // The monitor's own writes are noted but are not news: a
                        // download landing used to make the next pass due, which
                        // listed the whole of Drive again for nothing.
                        var id = pair.Id;
                        var root = pair.LocalFolder;
                        var journal = new PcWatchJournal();
                        _watchJournals[pair.Id] = journal;
                        void Seen(string fullPath, WatcherChangeTypes kind)
                        {
                            journal.Note(Relative(root, fullPath), kind);
                            if (!IsMine(fullPath)) Soon(id);
                        }
                        FileSystemEventHandler changed = (_, e) =>
                        {
                            if (!SyncPlanner.IsSkipped(e.Name ?? "")) Seen(e.FullPath, e.ChangeType);
                        };
                        watcher.Created += changed;
                        watcher.Changed += changed;
                        watcher.Deleted += changed;
                        watcher.Renamed += (_, e) =>
                        {
                            journal.Note(Relative(root, e.OldFullPath), WatcherChangeTypes.Deleted);
                            Seen(e.FullPath, WatcherChangeTypes.Renamed);
                        };
                        watcher.Error += (_, _) =>
                        {
                            journal.Lose();
                            Soon(id);
                        };
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

        /// <summary>
        /// Sync all now (tray, File menu, Ctrl+Shift+Y): every pair that is not
        /// paused, now. Says what it did, for the caller to show and speak.
        /// </summary>
        public string SyncAllNow()
        {
            var pairs = Pairs;
            if (pairs.Count == 0) return "No folders are synced with Google Drive";
            int running = pairs.Count(p => !p.Paused);
            if (running == 0) return $"All {SyncedFolders(pairs.Count)} are paused. Resume them first";
            SyncNow();
            return running == 1 ? "Syncing 1 synced folder now" : $"Syncing all {SyncedFolders(running)} now";
        }

        /// <summary>
        /// Pause all or Resume all: every pair at once, saved like an edit in
        /// the sync window (a pass under way stops at its next file).
        /// </summary>
        public string PauseAll(bool pause)
        {
            var pairs = Pairs;
            if (pairs.Count == 0) return "No folders are synced with Google Drive";
            if (pairs.Any(p => p.Paused != pause))
            {
                foreach (var pair in pairs) pair.Paused = pause;
                SetPairs(pairs);
            }
            return pause
                ? $"Paused {(pairs.Count == 1 ? "the synced folder" : $"all {SyncedFolders(pairs.Count)}")}. Nothing is copied until you resume"
                : $"Resumed {(pairs.Count == 1 ? "the synced folder" : $"all {SyncedFolders(pairs.Count)}")}. Syncing now";
        }

        /// <summary>True when there are pairs and every one is paused: the tray and menu then offer Resume all.</summary>
        public bool AllPaused
        {
            get { lock (_gate) return _pairs.Count > 0 && _pairs.All(p => p.Paused); }
        }

        private static string SyncedFolders(int count) => count == 1 ? "1 synced folder" : $"{count:N0} synced folders";

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
        /// a minute has passed since Drive was last checked. Zero minutes (tests
        /// only) means only on request.
        /// </summary>
        private Due TakeDue(DriveSyncPair pair, bool all, DateTime now)
        {
            lock (_gate)
            {
                if (all || _due.Remove(pair.Id)) return Due.Now;
                if (pair.CheckMinutes <= 0) return Due.No;
                return !_lastChecked.TryGetValue(pair.Id, out var last) ||
                       now - last >= TimeSpan.FromMinutes(pair.CheckMinutes)
                    ? Due.OnTheMinute
                    : Due.No;
            }
        }

        private enum Due { No, Now, OnTheMinute }

        /// <summary>
        /// Pairs whose last pass ended with nothing left to do (no failures,
        /// nothing waiting, no folder to look at again): on their minute they
        /// run only if Drive has changed at all, which one shared look at the
        /// feed says for every pair on that drive.
        /// </summary>
        private readonly HashSet<string> _clean = new();

        /// <summary>When each pair's last pass began.</summary>
        private readonly Dictionary<string, DateTime> _ranFrom = new();

        /// <summary>Each pair's drive (null for My Drive), asked once: a folder's drive does not change.</summary>
        private readonly Dictionary<string, string?> _driveOf = new();

        /// <summary>
        /// The shared look at each drive's feed (keyed by drive id, "" for My
        /// Drive): where it has read up to, and since when it has been unbroken.
        /// A pair is skipped only if it began its last pass after that, so no
        /// change between its own read of the feed and the shared one is missed.
        /// </summary>
        private readonly Dictionary<string, (string Token, DateTime Since)> _shared = new();

        /// <summary>However quiet Drive is, a pair runs at least this often (a missed PC event, the daily walk).</summary>
        internal static TimeSpan QuietMost { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>Shared looks at the feed so far. For tests.</summary>
        internal int SharedLooks;

        /// <summary>
        /// Which of <paramref name="pairs"/> run now. Asked for, PC changes and
        /// work left over run at once; a pair due only because its minute is up
        /// and whose last pass was clean runs only if the shared look at its
        /// drive's feed found anything. So ten quiet pairs cost one request a
        /// minute, not twenty (each pass also asks which drive its folder is on).
        /// Anything uncertain (a failed look, no unbroken look yet) runs the pair.
        /// </summary>
        internal async Task<List<DriveSyncPair>> DuePairsAsync(DriveClient client, IReadOnlyList<DriveSyncPair> pairs,
            bool all, DateTime now, CancellationToken token)
        {
            var run = new List<DriveSyncPair>();
            var quiet = new List<DriveSyncPair>();
            foreach (var pair in pairs)
            {
                if (pair.Paused || string.IsNullOrEmpty(pair.DriveFolderId)) continue;
                var due = TakeDue(pair, all, now);
                if (due == Due.No) continue;
                bool candidate;
                lock (_gate)
                    candidate = due == Due.OnTheMinute && _clean.Contains(pair.Id) &&
                                _ranFrom.TryGetValue(pair.Id, out var ran) && DateTime.UtcNow - ran < QuietMost;
                (candidate ? quiet : run).Add(pair);
            }
            if (quiet.Count == 0) return run;

            var groups = new Dictionary<string, List<DriveSyncPair>>(StringComparer.Ordinal);
            foreach (var pair in quiet)
            {
                string? driveId;
                bool known;
                lock (_gate) known = _driveOf.TryGetValue(pair.Id, out driveId);
                if (!known)
                {
                    try { driveId = await client.DriveOf(pair.DriveFolderId, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch { run.Add(pair); continue; }
                    lock (_gate) _driveOf[pair.Id] = driveId;
                }
                var key = driveId ?? "";
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
                list.Add(pair);
            }

            foreach (var (key, group) in groups)
            {
                string? driveId = key.Length == 0 ? null : key;
                (string Token, DateTime Since) look;
                bool have;
                lock (_gate) have = _shared.TryGetValue(key, out look);
                try
                {
                    if (!have)
                    {
                        // The first look only finds where the feed is; the pairs
                        // run, and from their next minute the look decides.
                        var since = DateTime.UtcNow;
                        var start = await client.ChangesStartToken(driveId, token).ConfigureAwait(false);
                        lock (_gate) _shared[key] = (start, since);
                        run.AddRange(group);
                        continue;
                    }

                    var (changes, next) = await client.SyncChangesSince(look.Token, driveId, token).ConfigureAwait(false);
                    Interlocked.Increment(ref SharedLooks);
                    lock (_gate) _shared[key] = (next, look.Since);
                    foreach (var pair in group)
                    {
                        bool unbroken;
                        lock (_gate) unbroken = _ranFrom.TryGetValue(pair.Id, out var ran) && ran >= look.Since;
                        if (changes.Count > 0 || !unbroken) run.Add(pair);
                        else lock (_gate) _lastChecked[pair.Id] = now;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch
                {
                    // A refused or expired token: the look starts again, and
                    // until it has, every pair on that drive runs as before.
                    lock (_gate) _shared.Remove(key);
                    run.AddRange(group);
                }
            }

            // In the order the person listed them.
            return pairs.Where(p => run.Contains(p)).ToList();
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
            try { Changed?.Invoke(); } catch { }
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

            foreach (var pair in await DuePairsAsync(client, Pairs, all, now, token).ConfigureAwait(false))
            {
                if (token.IsCancellationRequested) return;
                if (!await SyncOneAsync(client, pair, token).ConfigureAwait(false)) return;
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
            catch { }
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

        internal async Task<bool> SyncOneAsync(DriveClient client, DriveSyncPair pair, CancellationToken token)
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
                    try
                    {
                        await SyncPairAsync(client, pair, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        // Reached however the pass ended, short of the process
                        // dying: anything it sent has been answered (see Drain).
                        try { File.Delete(RunningMarker(pair.Id)); } catch { }
                        SyncHistory.Flush(pair.Id);
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
        /// time, or eight when they are small, remembering each in the encrypted state so it is never worked
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
            long gap = 2000;

            // Two at a time keeps a hard disk from seeking between big files.
            // Millions of tiny ones are a file open each, mostly waiting on the
            // disk and the virus scanner, so more go at once.
            int lanes = bytes / toHash.Count < HashSmallAverage ? HashLanesSmall : 2;
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
                await Parallel.ForEachAsync(toHash, new ParallelOptions { MaxDegreeOfParallelism = lanes, CancellationToken = token },
                    async (path, ct) =>
                    {
                        if (Stopped(pair) || GenerationOf(pair.Id) != generation) throw new PairStoppedException();
                        try
                        {
                            var full = Path.Combine(pair.LocalFolder, path.Replace('/', Path.DirectorySeparatorChar));
                            string md5;
                            // A buffer the size of the file, up to a megabyte: a
                            // megabyte each for millions of tiny files was most of
                            // the work.
                            int buffer = (int)Math.Clamp(local[path].Size, 4096, 1 << 20);
                            await using (var file = new FileStream(full, FileMode.Open, FileAccess.Read,
                                             FileShare.ReadWrite | FileShare.Delete, buffer, FileOptions.SequentialScan | FileOptions.Asynchronous))
                                md5 = Convert.ToHexString(await MD5.HashDataAsync(file, ct).ConfigureAwait(false)).ToLowerInvariant();

                            int before, after;
                            lock (gate)
                            {
                                known[path] = md5;
                                state.Hashes[path] = new SyncHashEntry(local[path].Size, local[path].ModifiedUtc, md5);
                                before = done;
                                after = ++done;
                                if (saved.ElapsedMilliseconds > gap)
                                {
                                    SaveState(pair.Id, state);
                                    saved.Restart();
                                    lock (_gate) gap = SaveGap(_saveCost.TryGetValue(pair.Id, out var cost) ? cost : 0);
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

        /// <summary>
        /// Carries out the moves and renames <see cref="SyncMoves.Find"/> sees in
        /// the plan, and returns the plan without the deletes and copies they
        /// replace, and how many files moved.
        ///
        /// A folder renamed on the PC becomes one rename of the Drive folder
        /// (the same ids, nothing uploaded) when that is exactly what happened:
        /// every Drive file under it moved with it, nothing under it is left out
        /// of the sync, the old folder is gone from the PC and nothing is at the
        /// new path on Drive. Anything less goes file by file. A file moved on
        /// the PC is checked by MD5 against Drive's copy first, and Drive's copy
        /// must still be what the scan saw. A file moved on Drive is the same id,
        /// and the PC's file must still be what the scan saw. A crash anywhere in
        /// here leaves both sides holding the file once: the next pass adopts it
        /// at its new path by MD5 and forgets the old one.
        /// </summary>
        private async Task<(List<SyncAction> Plan, int Moved)> MoveWhatMovedAsync(
            DriveClient client, DriveSyncPair pair, PairState state, SyncRules rules, List<SyncAction> plan,
            Dictionary<string, SyncFile> local, Dictionary<string, SyncFile> remote, Dictionary<string, string> folders,
            SyncSkips skips, string root, int generation, HashSet<string> notMade, CancellationToken token)
        {
            var found = SyncMoves.Find(rules, plan, local, remote, state.Files);
            if (found.Count == 0) return (plan, 0);

            // A file moved on the PC is only taken for the file Drive holds when
            // its MD5 agrees. Read once, and kept like any other.
            var checkedMoves = new List<SyncMove>();
            foreach (var move in found)
            {
                token.ThrowIfCancellationRequested();
                if (!move.OnDrive || move.Was.RemoteMd5 == null) { checkedMoves.Add(move); continue; }
                var l = local[move.To];
                string? md5 = state.Hashes.TryGetValue(move.To, out var h) && h.Size == l.Size && h.ModifiedUtc == l.ModifiedUtc
                    ? h.Md5 : null;
                if (md5 == null)
                {
                    try
                    {
                        await using var file = new FileStream(LocalPathFor(root, move.To), FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
                        md5 = Convert.ToHexString(await MD5.HashDataAsync(file, token).ConfigureAwait(false)).ToLowerInvariant();
                        state.Hashes[move.To] = new SyncHashEntry(l.Size, l.ModifiedUtc, md5);
                    }
                    catch (Exception) when (!token.IsCancellationRequested) { continue; }
                }
                if (string.Equals(md5, move.Was.RemoteMd5, StringComparison.OrdinalIgnoreCase)) checkedMoves.Add(move);
            }
            if (checkedMoves.Count == 0) return (plan, 0);

            var done = new HashSet<string>(SyncNameComparer.Instance);
            int moved = 0;

            void Recorded(SyncMove move, SyncFile at)
            {
                lock (_fastGate)
                {
                    // Its checksum goes with it (the file is the same), and nothing
                    // half done is left at the old path to be carried on there.
                    if (state.Hashes.Remove(move.From, out var hash)) state.Hashes[move.To] = hash;
                    state.Partials.Remove(move.From);
                    state.Uploads.Remove(move.From);
                    state.Files.Remove(move.From);
                    var l = local.TryGetValue(move.To, out var onPc) ? onPc : local[move.From];
                    state.Files[move.To] = new SyncBase(l.Size, l.ModifiedUtc, at.Size, at.ModifiedUtc, at.Id ?? move.Was.RemoteId, at.Md5);
                    done.Add(move.From);
                    done.Add(move.To);
                    moved++;
                    SyncHistory.Add(pair.Id, "Moved", move.To,
                        $"from {move.From}, as it was {(move.OnDrive ? "on the PC" : "in Drive")}");
                    SaveSoon(pair.Id, state);
                    int count = moved;
                    Report(pair.Id, s => s with { Moved = count });
                }
            }

            // ---- whole folders first ----
            foreach (var group in checkedMoves.GroupBy(m => (m.OnDrive, Folder: SyncMoves.FolderOf(m)))
                         .Where(g => g.Key.Folder != null).ToList())
            {
                if (Stopped(pair) || GenerationOf(pair.Id) != generation) throw new PairStoppedException();
                var (fromFolder, toFolder) = group.Key.Folder!.Value;
                var moves = group.ToList();
                var froms = new HashSet<string>(moves.Select(m => m.From), SyncNameComparer.Instance);
                var prefix = fromFolder + "/";

                if (group.Key.OnDrive)
                {
                    // Renamed on the PC: rename Drive's folder, if it is exactly that.
                    if (!folders.TryGetValue(fromFolder, out var folderId) || folders.ContainsKey(toFolder)) continue;
                    if (skips.AnyUnder(fromFolder) || skips.AnyUnder(toFolder)) continue;
                    if (remote.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !froms.Contains(k))) continue;
                    if (Directory.Exists(LocalPathFor(root, fromFolder))) continue;
                    if (folders.Keys.Any(k => k.StartsWith(toFolder + "/", StringComparison.OrdinalIgnoreCase))) continue;

                    var fromParent = SyncMoves.Parent(fromFolder);
                    var toParent = SyncMoves.Parent(toFolder);
                    var parentId = await Patiently(pair, () => EnsureFolderAsync(client, folders, pair.DriveFolderId, toParent, token, state.Remembered),
                        client, token).ConfigureAwait(false);
                    var oldParentId = fromParent.Length == 0 ? pair.DriveFolderId : folders[fromParent];
                    string? newName = SyncMoves.Leaf(fromFolder) == SyncMoves.Leaf(toFolder) ? null : SyncMoves.Leaf(toFolder);
                    bool sameParent = string.Equals(parentId, oldParentId, StringComparison.Ordinal);
                    await Patiently(pair, async () =>
                    {
                        await client.MoveOrRename(folderId, newName, sameParent ? null : oldParentId, sameParent ? null : parentId, token)
                            .ConfigureAwait(false);
                        return true;
                    }, client, token).ConfigureAwait(false);
                    Kill("moved folder " + fromFolder);

                    // Every folder under it is at its new path now.
                    foreach (var key in folders.Keys.Where(k => SyncNameComparer.Instance.Equals(k, fromFolder) ||
                                 k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        var id = folders[key];
                        folders.Remove(key);
                        folders[toFolder + key[fromFolder.Length..]] = id;
                    }
                    foreach (var move in moves)
                    {
                        var at = remote[move.From];
                        remote.Remove(move.From);
                        remote[move.To] = at;
                        Recorded(move, at);
                    }
                }
                else
                {
                    // Renamed on Drive: rename the PC's folder, if it is exactly that.
                    var oldPath = LocalPathFor(root, fromFolder);
                    var newPath = LocalPathFor(root, toFolder);
                    if (!IsInside(root, oldPath) || !IsInside(root, newPath)) continue;
                    if (!Directory.Exists(oldPath) || Directory.Exists(newPath) || File.Exists(newPath)) continue;
                    if (folders.ContainsKey(fromFolder)) continue;      // still there on Drive: not a rename
                    if (skips.AnyUnder(fromFolder) || skips.AnyUnder(toFolder)) continue;
                    if (local.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !froms.Contains(k))) continue;
                    bool anythingElse;
                    try { anythingElse = Directory.EnumerateFileSystemEntries(oldPath, "*", SearchOption.AllDirectories)
                              .Any(e => File.Exists(e) && !froms.Contains(Relative(root, e))); }
                    catch { continue; }
                    if (anythingElse) continue;
                    if (moves.Any(m => !LocalStillAsScanned(LocalPathFor(root, m.From), local[m.From]))) continue;

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                        Mine(oldPath);
                        Mine(newPath);
                        Directory.Move(oldPath, newPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    Kill("moved local folder " + fromFolder);
                    foreach (var move in moves)
                    {
                        var at = local[move.From];
                        local.Remove(move.From);
                        local[move.To] = at;
                        Recorded(move, remote[move.To]);
                    }
                }
            }

            // ---- then file by file ----
            var single = checkedMoves.Where(m => !done.Contains(m.From)).ToList();
            foreach (var folder in single.Where(m => m.OnDrive).Select(m => SyncMoves.Parent(m.To))
                         .Distinct(SyncNameComparer.Instance).OrderBy(f => f.Count(c => c == '/')))
            {
                if (Stopped(pair) || GenerationOf(pair.Id) != generation) throw new PairStoppedException();
                await Patiently(pair, () => EnsureFolderAsync(client, folders, pair.DriveFolderId, folder, token, state.Remembered), client, token)
                    .ConfigureAwait(false);
            }

            // Read before the lanes start: they write `remote` (under the gate) as they go.
            var scanned = single.Where(m => m.OnDrive).ToDictionary(m => m.From, m => remote[m.From], SyncNameComparer.Instance);
            var lanes = new AdaptiveLanes(FastLanes, FastLanesMin, FastLanesMax);
            await Parallel.ForEachAsync(single.Where(m => m.OnDrive),
                new ParallelOptions { MaxDegreeOfParallelism = FastLanesMax, CancellationToken = token },
                async (move, ct) =>
                {
                    await lanes.EnterAsync(ct).ConfigureAwait(false);
                    var clock = Stopwatch.StartNew();
                    bool ok = false;
                    try { ok = await MoveOnDrive(move, ct).ConfigureAwait(false); }
                    finally { lanes.Leave(ok && clock.ElapsedMilliseconds < SlowAnswerMilliseconds); }
                }).ConfigureAwait(false);

            foreach (var move in single.Where(m => !m.OnDrive))
            {
                token.ThrowIfCancellationRequested();
                if (Stopped(pair) || GenerationOf(pair.Id) != generation) throw new PairStoppedException();
                var oldPath = LocalPathFor(root, move.From);
                var newPath = LocalPathFor(root, move.To);
                if (!IsInside(root, oldPath) || !IsInside(root, newPath)) continue;
                if (!LocalStillAsScanned(oldPath, local[move.From]) || File.Exists(newPath) || Directory.Exists(newPath)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                    Mine(oldPath);
                    Mine(newPath);
                    File.Move(oldPath, newPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                Kill("moved local " + move.From);
                var at = local[move.From];
                local.Remove(move.From);
                local[move.To] = at;
                Recorded(move, remote[move.To]);
            }

            // A move tried and not made (Drive's copy or the PC's changed since the
            // scan, or the move itself failed) leaves both halves alone this pass:
            // as a delete and a copy they could leave the file twice. The next
            // pass looks again.
            var attempted = new HashSet<string>(SyncNameComparer.Instance);
            foreach (var m in checkedMoves) { attempted.Add(m.From); attempted.Add(m.To); }
            var rest = plan.Where(a => !done.Contains(a.Path) && !attempted.Contains(a.Path)).ToList();
            notMade.UnionWith(attempted.Where(p => !done.Contains(p)));
            return (rest, moved);

            async Task<bool> MoveOnDrive(SyncMove move, CancellationToken ct)
            {
                if (Stopped(pair) || GenerationOf(pair.Id) != generation) return false;
                var at = scanned[move.From];
                try
                {
                    // Still what the scan saw, or it is an edit and the plan decides.
                    var now = await Patiently(pair, () => client.FileNow(at.Id!, ct), client, ct).ConfigureAwait(false);
                    if (now == null || !SameAsScanned(now, at)) return false;
                    var fromParent = SyncMoves.Parent(move.From);
                    var toParent = SyncMoves.Parent(move.To);
                    var oldParentId = fromParent.Length == 0 ? pair.DriveFolderId : folders[fromParent];
                    var newParentId = toParent.Length == 0 ? pair.DriveFolderId : folders[toParent];
                    bool sameParent = string.Equals(oldParentId, newParentId, StringComparison.Ordinal);
                    var newName = Path.GetFileName(LocalPathFor(root, move.To));
                    await Patiently(pair, async () =>
                    {
                        await client.MoveOrRename(at.Id!, newName, sameParent ? null : oldParentId, sameParent ? null : newParentId, ct)
                            .ConfigureAwait(false);
                        return true;
                    }, client, ct).ConfigureAwait(false);
                }
                catch (Exception) when (!ct.IsCancellationRequested) { return false; }
                lock (_fastGate)
                {
                    remote.Remove(move.From);
                    remote[move.To] = at;
                }
                Recorded(move, at);
                Kill("moved " + move.From);
                return true;
            }
        }

        /// <summary>A full path under <paramref name="root"/> as the pair's relative path.</summary>
        private static string Relative(string root, string full) =>
            Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');

        /// <summary>
        /// How long a pair may go on the changes feed alone before Drive is
        /// listed whole again, to catch anything the feed did not say.
        /// </summary>
        internal static TimeSpan FullListEvery { get; set; } = TimeSpan.FromHours(24);

        /// <summary>Whether a pass may list only what the changes feed says changed.</summary>
        internal static bool CanListChangesOnly(PairStateView state, DriveSyncPair pair, DateTime nowUtc) =>
            !string.IsNullOrEmpty(state.ChangesToken) &&
            state.FolderCount > 0 && state.RootFolderId == pair.DriveFolderId &&
            state.ScanSubfolders == pair.IncludeSubfolders &&
            state.LastFullListUtc is { } full && nowUtc - full < FullListEvery && nowUtc >= full;

        /// <summary>What <see cref="CanListChangesOnly"/> needs to know about a pair's saved state.</summary>
        internal sealed record PairStateView(string? ChangesToken, int FolderCount, string? RootFolderId, bool? ScanSubfolders,
            DateTime? LastFullListUtc);

        /// <summary>
        /// Drive's side of a pass: what is in the pair's Drive folder, its
        /// folders by path, the changes-feed token to keep, and whether this was
        /// a full listing.
        ///
        /// Listed whole the first time, once a day, and whenever anything is in
        /// doubt. Otherwise only the folders the changes feed says changed are
        /// listed again (plus any folder a new PC file would go up into, and any
        /// left over from last time), and the rest is what the last sync
        /// recorded, which nothing since has touched. A folder renamed, moved or
        /// deleted on Drive, a token Google will not take, or any failure at all
        /// here means the whole listing instead, never a partial one taken for
        /// complete. And a new file is never uploaded on the strength of the
        /// records alone: its folder is always freshly listed first.
        /// </summary>
        private async Task<(Dictionary<string, SyncFile> Remote, Dictionary<string, string> Folders, string? Token, bool Full,
            SyncSkips Skips)> ListDriveSideAsync(DriveClient client, DriveSyncPair pair, PairState state,
            Dictionary<string, SyncFile> local, List<string> unusable, Dictionary<string, List<SyncFile>> shadowed,
            List<string> clashing, CancellationToken token, (List<DriveClient.DriveSyncChange> Changes, string Next)? read = null,
            string? knownDriveId = null)
        {
            string? driveId = knownDriveId;
            if (CanListChangesOnly(ViewOf(state), pair, DateTime.UtcNow))
            {
                try
                {
                    driveId ??= await client.DriveOf(pair.DriveFolderId, token).ConfigureAwait(false);
                    var changed = await ListChangedOnlyAsync(client, pair, state, local, driveId, unusable, shadowed, clashing, token, read)
                        .ConfigureAwait(false);
                    if (changed != null)
                        return (changed.Value.Remote, changed.Value.Folders, changed.Value.Token, false, changed.Value.Skips);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // Anything at all: listed whole instead.
                }
                unusable.Clear();
                clashing.Clear();
                shadowed.Clear();
            }

            // Taken before the listing, so a change made while it runs is in
            // the next pass's feed rather than lost between the two.
            string? start = null;
            try
            {
                driveId ??= await client.DriveOf(pair.DriveFolderId, token).ConfigureAwait(false);
                start = await client.ChangesStartToken(driveId, token).ConfigureAwait(false);
            }
            catch (Exception) when (!token.IsCancellationRequested) { start = null; }

            var remoteSkips = new SyncSkips();
            var (remote, folders) = await Patiently(pair,
                () =>
                {
                    unusable.Clear();
                    clashing.Clear();
                    shadowed.Clear();
                    remoteSkips = new SyncSkips();
                    return ScanRemoteAsync(client, pair.DriveFolderId, pair.IncludeSubfolders, token, unusable, shadowed,
                        clashing, remoteSkips, WaitScale, found: n => Report(pair.Id, s => s with { DriveFound = n }));
                }, client, token)
                .ConfigureAwait(false);
            return (remote, folders, start, true, remoteSkips);
        }

        /// <summary>The watchers' account of what changed, per pair, for PC folders with no change journal.</summary>
        private readonly Dictionary<string, PcWatchJournal> _watchJournals = new();

        /// <summary>The settings a whole walk of the PC folder was for: change any and the next pass walks it again.</summary>
        internal static string PcWalkKeyOf(DriveSyncPair pair) =>
            JsonSerializer.Serialize(new { pair.LocalFolder, pair.DriveFolderId, Rules = SyncRules.For(pair) });

        /// <summary>What a pass knows about the PC side before it looks: see <see cref="LookAtPc"/>.</summary>
        private sealed class PcLook
        {
            /// <summary>The folders that changed; null when the pass walks the whole PC folder.</summary>
            public PcMarks? Marks;

            /// <summary>Where the volume's change journal is now: the next pass reads from here.</summary>
            public UsnJournal.Position? Journal;

            public PcWatchJournal? Watch;
            public PcMarks? FromWatch;
            public int Epoch;
            public bool Planned;
            public string Why = "";
        }

        /// <summary>
        /// Which PC folders changed since the last pass, so that only those are
        /// listed again, the way only the Drive folders the changes feed names
        /// are. From the volume's change journal, which Windows keeps whether or
        /// not the app is running; or, with none, from the folder watcher, once
        /// it has watched a whole walk through. The whole folder is walked
        /// instead on the first sync, once a day, after the pair's settings
        /// change, when a walk met a junction or link, and whenever the journal
        /// cannot account for every change since.
        /// </summary>
        private PcLook LookAtPc(DriveSyncPair pair, PairState state, bool hasRecords)
        {
            var look = new PcLook { Journal = UsnJournal.Where(pair.LocalFolder) };
            lock (_gate) _watchJournals.TryGetValue(pair.Id, out look.Watch);
            look.FromWatch = look.Watch?.Take(out look.Epoch);

            var now = DateTime.UtcNow;
            if (!hasRecords) look.Why = "nothing synced yet";
            else if (state.LastPcWalkUtc is not { } walked || now - walked >= FullListEvery || now < walked) look.Why = "a day since the last walk";
            else if (state.PcWalkKey != PcWalkKeyOf(pair)) look.Why = "the pair's settings changed";
            else if (state.PcHasLinks) look.Why = "the folder holds a junction or link";
            else if (look.Journal != null)
            {
                if (state.UsnJournalId == look.Journal.JournalId && state.UsnNext is long from)
                {
                    look.Marks = UsnJournal.Since(pair.LocalFolder, look.Journal.JournalId, from, look.Journal.Next);
                    if (look.Marks == null) look.Why = "the change journal could not account for everything since";
                }
                else look.Why = "no place in the change journal yet";
            }
            else if (look.FromWatch != null) look.Marks = look.FromWatch;
            else look.Why = "no change journal, and the watcher has not watched a whole walk";

            LastPcSide = look.Marks != null ? $"{look.Marks.Count} folders changed" : "walking it whole, " + look.Why;
            GoogleDrive.Trace($"sync {DisplayName(pair)} PC side: " + LastPcSide);
            return look;
        }

        /// <summary>For the suite: how the last pass found what changed on the PC ("3 folders changed", or "walking it whole, why").</summary>
        internal string LastPcSide { get; private set; } = "";

        /// <summary>For the suite: the folders the last batched pass visited.</summary>
        internal int LastFoldersVisited { get; private set; }

        /// <summary>
        /// The pass has planned: where the journal was is saved with the records
        /// it goes with, and a whole walk is noted. Called from the same place
        /// the changes feed's token is.
        /// </summary>
        private void PcPlanned(DriveSyncPair pair, PairState state, PcLook look, bool walkedWhole, bool links)
        {
            state.UsnJournalId = look.Journal?.JournalId;
            state.UsnNext = look.Journal?.Next;
            if (walkedWhole)
            {
                state.LastPcWalkUtc = DateTime.UtcNow;
                state.PcWalkKey = PcWalkKeyOf(pair);
                state.PcHasLinks = links;
                if (!look.Planned) look.Watch?.Walked(look.Epoch);
            }
            else if (links) state.PcHasLinks = true;
            look.Planned = true;
        }

        /// <summary>A pass that ended before it planned: what the watcher had seen is wanted by the next.</summary>
        private static void PcUnplanned(PcLook look)
        {
            if (!look.Planned) look.Watch?.PutBack(look.FromWatch, look.Epoch);
        }

        private static PairStateView ViewOf(PairState state) =>
            new(state.ChangesToken, state.FolderIds.Count, state.FolderIds.TryGetValue("", out var r) ? r : null,
                state.ScanSubfolders, state.LastFullListUtc);

        /// <summary>
        /// The folders a small pair's changes feed says to list again, with
        /// those last pass left work in and those holding a record with no Drive
        /// id. Null when the feed says the folders themselves changed (renamed,
        /// moved, deleted), and Drive has to be listed whole.
        /// </summary>
        private static HashSet<string>? FeedDirty(PairState state, List<DriveClient.DriveSyncChange> changes)
        {
            var folderIds = state.FolderIds;
            var folderAt = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (path, id) in folderIds) if (!folderAt.TryAdd(id, path)) return null;
            var fileAt = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (path, was) in state.Files)
                if (!string.IsNullOrEmpty(was.RemoteId)) fileAt.TryAdd(was.RemoteId, path);

            var dirty = new HashSet<string>(state.DirtyFolders, SyncNameComparer.Instance);
            foreach (var c in changes)
            {
                if (folderAt.TryGetValue(c.Id, out var folderPath))
                {
                    if (folderPath.Length == 0) { dirty.Add(""); continue; }     // the pair's own folder: its name is not ours
                    if (c.Gone || !c.Folder) return null;
                    var parentPath = SyncMoves.Parent(folderPath);
                    var parentId = folderIds.TryGetValue(parentPath, out var p) ? p : null;
                    if (c.Parent != parentId || c.Name == null ||
                        !SyncNameComparer.Instance.Equals(c.Name, SyncMoves.Leaf(folderPath))) return null;   // renamed or moved
                    dirty.Add(folderPath);
                    continue;
                }
                if (c.Folder)
                {
                    // A folder new to the pair: its parent's listing finds it and goes in.
                    if (!c.Gone && c.Parent != null && folderAt.TryGetValue(c.Parent, out var into)) dirty.Add(into);
                    continue;
                }
                if (fileAt.TryGetValue(c.Id, out var was)) dirty.Add(SyncMoves.Parent(was));
                if (!c.Gone && c.Parent != null && folderAt.TryGetValue(c.Parent, out var now)) dirty.Add(now);
            }

            foreach (var (path, was) in state.Files)
                if (string.IsNullOrEmpty(was.RemoteId)) dirty.Add(SyncMoves.Parent(path));
            return dirty;
        }

        /// <summary>
        /// The changes-feed half of <see cref="ListDriveSideAsync"/>. Null when
        /// the feed says the folders themselves changed, and Drive has to be
        /// listed whole.
        /// </summary>
        private async Task<(Dictionary<string, SyncFile> Remote, Dictionary<string, string> Folders, string Token,
            SyncSkips Skips)?> ListChangedOnlyAsync(DriveClient client, DriveSyncPair pair, PairState state,
            Dictionary<string, SyncFile> local, string? driveId, List<string> unusable,
            Dictionary<string, List<SyncFile>> shadowed, List<string> clashing, CancellationToken token,
            (List<DriveClient.DriveSyncChange> Changes, string Next)? read = null)
        {
            var (changes, next) = read ?? await Patiently(pair, () => client.SyncChangesSince(state.ChangesToken!, driveId, token),
                client, token).ConfigureAwait(false);

            var folderIds = state.FolderIds;
            if (FeedDirty(state, changes) is not { } dirty) return null;

            // What the records say, for every folder nothing touched.
            var remote = new Dictionary<string, SyncFile>(SyncNameComparer.Instance);
            foreach (var (path, was) in state.Files)
            {
                if (dirty.Contains(SyncMoves.Parent(path))) continue;
                remote[path] = new SyncFile(was.RemoteSize, was.RemoteModifiedUtc, was.RemoteId, was.RemoteMd5);
            }

            // A new PC file is never sent on the records alone: the folder it
            // would go into (or the nearest one Drive is known to have) is listed.
            foreach (var path in local.Keys)
            {
                if (state.Files.ContainsKey(path) || remote.ContainsKey(path)) continue;
                var folder = SyncMoves.Parent(path);
                while (folder.Length > 0 && !folderIds.ContainsKey(folder)) folder = SyncMoves.Parent(folder);
                dirty.Add(folder);
            }
            // And their records were set aside above for a folder now listed.
            foreach (var path in remote.Keys.Where(p => dirty.Contains(SyncMoves.Parent(p))).ToList()) remote.Remove(path);

            // A folder that should be listed but is not known on Drive cannot be
            // listed, and its records cannot be trusted either: listed whole.
            if (dirty.Any(f => !folderIds.ContainsKey(f))) return null;
            var only = dirty.Select(f => (folderIds[f], f)).ToList();
            var skips = new SyncSkips();

            // What the last listing left out stays left out (and said), except in
            // folders listed again now, which say for themselves.
            void CarryLeftOut()
            {
                bool Unlisted(string entry) => !dirty.Contains(SyncMoves.Parent(entry.TrimEnd('/')));
                unusable.AddRange(state.DriveUnusable.Where(Unlisted));
                clashing.AddRange(state.DriveClashing.Where(Unlisted));
            }
            if (only.Count == 0)
            {
                CarryLeftOut();
                return (remote, new Dictionary<string, string>(folderIds, SyncNameComparer.Instance), next, skips);
            }

            var (fresh, folders) = await Patiently(pair,
                () =>
                {
                    unusable.Clear();
                    clashing.Clear();
                    shadowed.Clear();
                    skips = new SyncSkips();
                    return ScanRemoteAsync(client, pair.DriveFolderId, pair.IncludeSubfolders, token, unusable, shadowed,
                        clashing, skips, WaitScale, only: only, known: folderIds);
                }, client, token)
                .ConfigureAwait(false);
            foreach (var (path, file) in fresh) remote[path] = file;
            CarryLeftOut();
            return (remote, folders, next, skips);
        }

        /// <summary>
        /// Drive holding one name twice (Google Drive for desktop can leave that
        /// behind), with nothing synced there yet: the newer copy counts, and if
        /// the PC's file was the older one, the pass saw a conflict and made a
        /// third copy. So the PC's file is hashed here, and when it is exactly
        /// one of the older copies, that copy is the one this path is paired
        /// with. The other copy is left on Drive as it is. Rare, so only these
        /// few files are read.
        /// </summary>
        private static async Task AdoptMatchingDuplicateAsync(string root, PairState state,
            Dictionary<string, SyncFile> local, Dictionary<string, SyncFile> remote,
            Dictionary<string, List<SyncFile>> shadowed, CancellationToken token)
        {
            foreach (var (path, older) in shadowed)
            {
                if (!local.TryGetValue(path, out var mine) || !remote.TryGetValue(path, out var newer)) continue;

                // Synced before with one of the older copies: that is the pair's
                // copy, pass after pass, rather than whichever is newest. Unless
                // the newer one is the PC's own file (a replacement that went up
                // and was cut off before the old copy went to the trash), which
                // the Record step finishes.
                bool synced = state.Files.TryGetValue(path, out var was);
                var syncedId = synced ? was!.RemoteId : null;
                var candidates = synced
                    ? older.Where(c => c.Id == syncedId).ToList()
                    : older.Where(c => c.Md5 != null && c.Size == mine.Size).ToList();
                if (candidates.Count == 0) continue;
                string md5;
                try
                {
                    await using var file = new FileStream(LocalPathFor(root, path), FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
                    md5 = Convert.ToHexString(await MD5.HashDataAsync(file, token).ConfigureAwait(false)).ToLowerInvariant();
                }
                catch (Exception) when (!token.IsCancellationRequested) { continue; }
                if (newer.Md5 != null && string.Equals(newer.Md5, md5, StringComparison.OrdinalIgnoreCase)) continue;
                var match = synced
                    ? candidates[0]
                    : candidates.FirstOrDefault(c => string.Equals(c.Md5, md5, StringComparison.OrdinalIgnoreCase));
                if (match.Id == null) continue;
                remote[path] = match;
                older.Remove(match);
                older.Add(newer);
            }
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

            // A large pair goes a batch of folders at a time (see SyncInBatchesAsync).
            if (UseBatches(state, pair))
            {
                // Its first pass a batch at a time walks everything, which also
                // gives it the totals a pass of only some folders goes on.
                bool batchedBefore = state.Sharded;
                if (!state.Sharded) MigrateToShards(pair.Id, state);
                var batchLook = LookAtPc(pair, state, batchedBefore && state.ShardFolders.Count > 0);
                try { await SyncInBatchesAsync(client, pair, state, generation, batchLook, token).ConfigureAwait(false); }
                finally { PcUnplanned(batchLook); }
                return;
            }

            var look = LookAtPc(pair, state, state.Files.Count > 0);
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
                catch (Exception) when (!token.IsCancellationRequested) { }
            }

            bool links = false;
            Dictionary<string, SyncFile> WalkWhole() =>
                ScanLocal(root, pair.IncludeSubfolders, skips, localClashing,
                    found: n => Report(pair.Id, s => s with { LocalFound = n }), linked: _ => links = true);
            var local = changedOnPc != null
                ? ScanLocalPartly(root, pair.IncludeSubfolders, state.Files, changedOnPc, skips, localClashing, state.LocalClashing,
                    linked: _ => links = true)
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
                }, scope: null, hold: null, token).ConfigureAwait(false);
            FinishPass(pair, state, tally, unusable, clashing, localClashing);
        }

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
            string? driveId = null;
            try { driveId = await client.DriveOf(pair.DriveFolderId, token).ConfigureAwait(false); }
            catch (Exception) when (!token.IsCancellationRequested) { }

            bool full = forceFull || !CanListChangesOnly(ViewOf(header), pair, DateTime.UtcNow) ||
                        recordFolders.Any(f => !header.FolderIds.ContainsKey(f));
            string why = !CanListChangesOnly(ViewOf(header), pair, DateTime.UtcNow) ? "no feed to go on"
                : full ? "records in a folder Drive is not known to have: " + recordFolders.First(f => !header.FolderIds.ContainsKey(f))
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
                try { nextToken = await client.ChangesStartToken(driveId, token).ConfigureAwait(false); }
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
            var listedAll = new HashSet<string>(comparer);
            var nextDirty = new HashSet<string>(comparer);
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
                    while (queue.Count > 0 && (batch.Count == 0 || local.Count < BatchFiles))
                    {
                        var f = queue.Dequeue();
                        if (!visited.Add(f)) continue;
                        batch.Add(f);
                        subs.Clear();
                        var dir = new DirectoryInfo(f.Length == 0 ? root : LocalPathFor(root, f));
                        if (f.Length == 0 || dir.Exists)
                            ScanLocalFolder(dir, f, pair.IncludeSubfolders, local, skips, localClashing, subs, null, _ => links = true);
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
                    Report(pair.Id, s => s with { LocalFound = seenSoFar.Pc, DriveFound = seenSoFar.Drive }, now: true);

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
                    Func<SyncAction, bool>? hold = hasRecords
                        ? a => tally.Held.Count < HeldMost && IsMoveHalf(a, local, remote, records)
                        : null;
                    int heldBefore = tally.Held.Count;
                    await ExecuteAsync(client, pair, slice, rules, local, remote, folders, skips, shadowed, generation, tally,
                        plan => nextDirty.UnionWith(plan.Where(a => a.Kind is not (SyncActionKind.Record or SyncActionKind.Forget))
                            .Select(a => SyncMoves.Parent(a.Path))),
                        scope: null, hold, token).ConfigureAwait(false);
                    GoogleDrive.Trace($"sync {DisplayName(pair)} batch: {batch.Count} folders, {local.Count} PC files, " +
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

                // ---- records of folders neither side has any more ----
                // (A pass that looked at only some folders left the rest alone on purpose.)
                foreach (var f in partial ? new List<string>() : shardFolders.ToList())
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
                try { SaveState(pair.Id, header); } catch { }
                throw;
            }

            // What was left out before and has not been listed again this pass.
            bool Unlisted(string entry) => !listedAll.Contains(SyncMoves.Parent(entry.TrimEnd('/')));
            if (!full)
            {
                unusable.AddRange(header.DriveUnusable.Where(Unlisted));
                clashing.AddRange(header.DriveClashing.Where(Unlisted));
            }

            header.ChangesToken = nextToken;
            header.FolderIds = folders;
            header.DirtyFolders = nextDirty.ToList();
            if (full)
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
            header.PcFiles = totals.Pc + tally.Downloaded;
            header.DriveFiles = totals.Drive + tally.Uploaded;
            header.MatchedFiles = totals.Matched + tally.Uploaded + tally.Downloaded;     // a moved file is counted as matched already
            Report(pair.Id, s => s with { LocalFound = totals.Pc, DriveFound = totals.Drive, Matched = totals.Matched }, now: true);
            // Stopped short for space: folders it never reached are still owed
            // the changes read for them, so the journal's place stays where it was.
            if (tally.SpaceProblem == null) PcPlanned(pair, header, look, walkedWhole: !partial, links);
            SaveState(pair.Id, header);
            FinishPass(pair, header, tally, unusable, clashing, localClashing);
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

        /// <summary>
        /// How long a create already on the wire is let finish once the pass is
        /// stopped (Pause, Edit, Remove, closing). Cut off mid-request, Google may
        /// still store it with the answer lost; a pass that starts straight after
        /// can list the folder before Google's listing shows it, and send it again:
        /// a second copy, seen once on the real account. Let finish, it is recorded.
        /// </summary>
        internal static TimeSpan InFlightGrace { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>
        /// A token for one create (an upload, a new folder): it ends
        /// <see cref="InFlightGrace"/> after <paramref name="pass"/> does, not with it.
        /// Nothing new should be started on it once the pass has stopped.
        /// </summary>
        private sealed class Drain : IDisposable
        {
            private readonly CancellationTokenSource _late = new();
            private readonly CancellationTokenRegistration _when;
            public Drain(CancellationToken pass)
            {
                _when = pass.Register(() => { try { _late.CancelAfter(InFlightGrace); } catch (ObjectDisposedException) { } });
            }
            public CancellationToken Token => _late.Token;
            public void Dispose() { _when.Dispose(); _late.Dispose(); }
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
            public string? LastError, SpaceProblem;

            /// <summary>Going a batch at a time: the start of a big pass is said once, not per batch.</summary>
            public bool Batched, Announced;

            /// <summary>Transfers and bytes so far in a batched pass, to tell when it has become a big one.</summary>
            public long Bytes;
            public int Transfers;
            public readonly List<string> Conflicts = new();

            /// <summary>Actions held back by a batch for the end of the pass.</summary>
            public readonly List<SyncAction> Held = new();
        }

        /// <summary>
        /// The doing half of a pass, for one set of files: the PC's, Drive's and
        /// what was recorded, already listed. Duplicates adopted, checksums
        /// compared, the plan made, moves followed, then the files themselves.
        /// <paramref name="scope"/> limits the plan to those paths (the rest of
        /// what was loaded is left exactly as it is); <paramref name="hold"/>
        /// takes actions out of this run and into <see cref="PassTally.Held"/>;
        /// <paramref name="onPlan"/> sees the plan that will run, before it does.
        /// </summary>
        private async Task ExecuteAsync(DriveClient client, DriveSyncPair pair, PairState state, SyncRules rules,
            Dictionary<string, SyncFile> local, Dictionary<string, SyncFile> remote, Dictionary<string, string> folders,
            SyncSkips skips, Dictionary<string, List<SyncFile>> shadowed, int generation, PassTally tally,
            Action<List<SyncAction>>? onPlan, IReadOnlySet<string>? scope, Func<SyncAction, bool>? hold,
            CancellationToken token)
        {
            // Files already on both sides with nothing remembered about them (a
            // first sync over two copies of one library) are compared by MD5
            // before anything is planned, so a match is adopted, never copied.
            var name = DisplayName(pair);
            var root = pair.LocalFolder;
            await AdoptMatchingDuplicateAsync(root, state, local, remote, shadowed, token).ConfigureAwait(false);
            var md5 = await CheckWhatIsThereAsync(pair, state, rules, local, remote, generation, token).ConfigureAwait(false);
            var plan = SyncPlanner.Ordered(SyncPlanner.Plan(rules, local, remote, state.Files, md5, skips), local, remote);
            if (scope != null) plan = plan.Where(a => scope.Contains(a.Path)).ToList();
            var asPlanned = plan;

            // Moved or renamed on one side: moved the same way on the other,
            // rather than deleted in one place and copied again in another.
            int movedFiles;
            var notMade = new HashSet<string>(SyncNameComparer.Instance);
            (plan, movedFiles) = await MoveWhatMovedAsync(client, pair, state, rules, plan, local, remote, folders, skips,
                root, generation, notMade, token).ConfigureAwait(false);
            tally.Moved += movedFiles;

            // Held back for the end of the pass (see SyncInBatchesAsync): what
            // might be one half of a move whose other half is in another batch.
            if (hold != null)
            {
                // Decided once per action: asked twice, an action near the limit
                // could be held and run as well.
                var held = new HashSet<SyncAction>();
                foreach (var a in plan) if (hold(a)) { held.Add(a); tally.Held.Add(a); }
                if (held.Count > 0) plan = plan.Where(a => !held.Contains(a)).ToList();
            }
            // A recorded file that a listing just left out may not be gone at all:
            // Google's folder listing can lag a second or two behind a create or a
            // move. Taken at its word, the PC's copy went up again (deletes off) or
            // to the Recycle Bin (deletes on). So the file itself is asked, which is
            // current; still in its folder, the action waits for the next pass.
            var stale = await ListedButStillThereAsync(client, pair, state, plan, local, remote, folders, token).ConfigureAwait(false);
            // With the halves of any move tried and not made: they leave the pass,
            // and their folders must still be looked at again next time, since
            // the change that started it has been read.
            onPlan?.Invoke(plan.Concat(asPlanned.Where(a => notMade.Contains(a.Path))).ToList());
            if (stale.Count > 0)
            {
                plan = plan.Where(a => !stale.Contains(a.Path)).ToList();
                tally.Deferred += stale.Count;
            }

            // A download held back because the PC's file changed under it waits
            // in its ".partial". Once the plan decides that path another way (a
            // conflict, an upload) or Drive's file has gone, the part is let go.
            var decided = new HashSet<string>(plan
                .Where(a => a.Kind is not (SyncActionKind.Download or SyncActionKind.KeepBothRemoteWins))
                .Select(a => a.Path), SyncNameComparer.Instance);
            foreach (var path in state.Partials.Keys.Where(p => (scope == null || scope.Contains(p)) &&
                         (!remote.ContainsKey(p) || decided.Contains(p))).ToList())
            {
                var part = PartialPath(LocalPathFor(root, path));
                try { if (IsInside(root, part)) File.Delete(part); } catch { }
                state.Partials.Remove(path);
            }

            // What is already the same on both sides, and how many steps the
            // rest is, for the detailed status.
            var planned = new HashSet<string>(plan.Where(a => a.Kind is not (SyncActionKind.Record or SyncActionKind.Forget)).Select(a => a.Path), SyncNameComparer.Instance);
            int matched = 0;
            foreach (var key in local.Keys)
                if (remote.ContainsKey(key) && !planned.Contains(key)) matched++;
            tally.Matched += matched;
            int matchedSoFar = tally.Matched;
            Report(pair.Id, s => s with { Phase = PhaseSyncing, Matched = matchedSoFar, StepTotal = plan.Count, StepDone = 0 }, now: true);

            // How big this pass is, for the progress said along the way.
            long downBytes = 0, upBytes = 0;
            int transfers = 0;
            foreach (var a in plan)
            {
                if (a.Kind is SyncActionKind.Download or SyncActionKind.KeepBothRemoteWins) { downBytes += remote[a.Path].Size; transfers++; }
                if (a.Kind is SyncActionKind.Upload or SyncActionKind.KeepBothLocalWins) { upBytes += local[a.Path].Size; transfers++; }
            }
            long totalBytes = downBytes + upBytes, doneBytes = 0;
            tally.Bytes += totalBytes;
            tally.Transfers += transfers;
            bool big = tally.Batched ? SyncSpace.IsBig(tally.Bytes, tally.Transfers) : SyncSpace.IsBig(totalBytes, transfers);
            if (big && !tally.Announced)
            {
                tally.Announced = true;
                _notify("sync.start", tally.Batched
                    ? $"{name}: a big sync, {tally.Transfers:N0} files so far and more to come"
                    : $"{name}: {Amounts(downBytes, upBytes)}, {transfers:N0} files");
            }
            SetProgress(pair.Id, 0, totalBytes);

            void Moved(long bytes)
            {
                long before = doneBytes;
                doneBytes += bytes;
                SetProgress(pair.Id, doneBytes, totalBytes);
                if (big && !tally.Batched && SyncSpace.QuarterPassed(before, doneBytes, totalBytes) is int percent and > 0)
                    _notify("sync.progress", $"{name}: {percent} percent synced");
            }

            int uploaded = 0, downloaded = 0, deleted = 0, failed = 0, deferred = 0;
            string? lastError = null, spaceProblem = null;
            var conflicts = new List<string>();
            long uploadedThisPass = 0;

            var listings = new Dictionary<string, List<(string Name, bool Folder)>>(StringComparer.Ordinal);
            try
            {
                // New small files first, many at once (see UploadNewSmallAsync).
                // One at a time, a folder of a million tiny files was a month
                // of waiting on Google's round trips, not of sending bytes.
                var sentFast = await UploadNewSmallAsync(client, pair, state, plan, local, remote, folders, root,
                    generation, uploadedThisPass, n => { lock (_fastGate) Moved(n); }, token).ConfigureAwait(false);
                uploaded += sentFast.Count;
                foreach (var path in sentFast)
                {
                    uploadedThisPass += local[path].Size;
                    SyncHistory.Add(pair.Id, "Uploaded", path);
                }

                // New song-sized PC files, many at once too (see UploadNewMediumAsync).
                var sentMedium = await UploadNewMediumAsync(client, pair, state, plan, local, remote, folders, root,
                    generation, uploadedThisPass, n => { lock (_fastGate) Moved(n); }, token).ConfigureAwait(false);
                uploaded += sentMedium.Count;
                foreach (var path in sentMedium)
                {
                    uploadedThisPass += local[path].Size;
                    SyncHistory.Add(pair.Id, "Uploaded", path);
                }
                sentFast.UnionWith(sentMedium);

                // And new Drive files coming down, many at once (see DownloadNewSmallAsync).
                var gotFast = await DownloadNewSmallAsync(client, pair, state, plan, local, remote, root, generation,
                    n => { lock (_fastGate) Moved(n); }, token).ConfigureAwait(false);
                downloaded += gotFast.Count;
                sentFast.UnionWith(gotFast);

                for (int i = 0; i < plan.Count; i++)
                {
                    if (sentFast.Contains(plan[i].Path)) continue;
                    var action = plan[i];
                    token.ThrowIfCancellationRequested();

                    // Pause, Remove and Edit are heard between files, not only
                    // while waiting for Google: a pass of hundreds of gigabytes
                    // used to carry on to the end under settings that had gone.
                    if (Stopped(pair) || GenerationOf(pair.Id) != generation) throw new PairStoppedException();
                    SaveSoon(pair.Id, state);
                    int step = i;
                    int up = tally.Uploaded + uploaded, down = tally.Downloaded + downloaded,
                        gone = tally.Deleted + deleted, bad = tally.Failed + failed;
                    Report(pair.Id, s => s with
                    {
                        StepDone = step, Uploaded = up, Downloaded = down, Deleted = gone, Failed = bad,
                    });

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
                        SyncHistory.Add(pair.Id, "Left out", path, $"its name would put it outside {root}");
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
                            SyncHistory.Add(pair.Id, "Could not sync", path, ex.Message);
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
                                    // One-shot uploads finish once started (see Drain); a big one
                                    // resumes from its session, so it stops when told.
                                    using var drainOne = new Drain(token);
                                    var upToken = local[path].Size <= DriveClient.SmallUploadBytes ? drainOne.Token : token;
                                    var sent = await UploadAsync(client, folders, pair, state, path, localPath,
                                        local[path], replaces, old, Moved, upToken).ConfigureAwait(false);
                                    Kill("uploaded " + path);
                                    state.Files[path] = sent;
                                    uploadedThisPass += local[path].Size;
                                    uploaded++;
                                    SyncHistory.Add(pair.Id, replaces != null ? "Updated in Drive" : "Uploaded", path);
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
                                    SyncHistory.Add(pair.Id, onPc != null ? "Updated on the PC" : "Downloaded", path,
                                        keepOld ? "the PC's old copy went to the Recycle Bin" : null);
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
                                    SyncHistory.Add(pair.Id, "Moved to the Drive trash", path, "deleted on the PC");
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
                                        SyncHistory.Add(pair.Id, "Kept on the PC", path, kept);
                                        break;
                                    }
                                    Kill("recycled " + path);
                                    state.Files.Remove(path);
                                    deleted++;
                                    SyncHistory.Add(pair.Id, "Moved to the Recycle Bin", path, "deleted in Drive");
                                    break;
                                }

                                case SyncActionKind.KeepBothLocalWins:
                                {
                                    // Drive's copy steps aside under the conflict name; the PC's goes up.
                                    var aside = SyncPlanner.ConflictName(path, DateTime.Now, p => remote.ContainsKey(p) || local.ContainsKey(p));
                                    await client.MoveOrRename(remote[path].Id!, Path.GetFileName(aside), null, null, token).ConfigureAwait(false);
                                    Kill("set aside in Drive " + path);
                                    using var drainOne = new Drain(token);
                                    var upToken = local[path].Size <= DriveClient.SmallUploadBytes ? drainOne.Token : token;
                                    var sent = await UploadAsync(client, folders, pair, state, path, localPath,
                                        local[path], null, null, Moved, upToken).ConfigureAwait(false);
                                    Kill("uploaded " + path);
                                    state.Files[path] = sent;
                                    uploadedThisPass += local[path].Size;
                                    conflicts.Add(Path.GetFileName(path));
                                    uploaded++;
                                    SyncHistory.Add(pair.Id, "Kept both copies of", path,
                                        $"it changed in both places; Drive's copy is now {Path.GetFileName(aside)}");
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
                                    SyncHistory.Add(pair.Id, "Kept both copies of", path,
                                        "it changed in both places; the PC's copy was renamed with the word conflict and the date");
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
                            SyncHistory.Add(pair.Id, "Could not sync", path, ex.Message);
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

                tally.Uploaded += uploaded;
                tally.Downloaded += downloaded;
                tally.Deleted += deleted;
                tally.Failed += failed;
                tally.Deferred += deferred;
                if (lastError != null) tally.LastError = lastError;
                tally.SpaceProblem ??= spaceProblem;
                tally.Conflicts.AddRange(conflicts);
                Report(pair.Id, s => s with
                {
                    Uploaded = tally.Uploaded, Downloaded = tally.Downloaded, Deleted = tally.Deleted, Failed = tally.Failed,
                    Moved = tally.Moved,
                }, now: true);

                // Something left for later (a file still being written, an edit
                // made after the scan) is looked at again within a minute,
                // whatever the pair's interval.
                if (deferred > 0) lock (_gate) _due.Add(pair.Id);
            }

        }

        /// <summary>The end of a pass: what it says, and the pair's state as it is left.</summary>
        private void FinishPass(DriveSyncPair pair, PairState state, PassTally tally,
            List<string> unusable, List<string> clashing, List<string> localClashing)
        {
            var name = DisplayName(pair);
            var conflicts = tally.Conflicts;

            // Nothing left for a later pass: until Drive or the PC changes, the
            // next minute need not look (DuePairsAsync).
            if (tally.SpaceProblem == null && tally.Failed == 0 && tally.Deferred == 0 && tally.Held.Count == 0 &&
                state.DirtyFolders.Count == 0)
                lock (_gate) _clean.Add(pair.Id);
            if (conflicts.Count > 0)
                _notify("sync.conflict", conflicts.Count == 1
                    ? $"{name}: {conflicts[0]} changed in both places; both copies kept"
                    : $"{name}: {conflicts.Count} files changed in both places; both copies kept");

            if (tally.Uploaded + tally.Downloaded + tally.Deleted + tally.Moved > 0)
                _notify("sync.done", $"{name} synced: " + Tally(tally.Uploaded, tally.Downloaded, tally.Deleted, tally.Moved));

            var spaceProblem = tally.SpaceProblem;
            if (spaceProblem != null)
            {
                // Not a setting: the pair is not marked paused, so it carries on
                // by itself on a later pass once there is room.
                bool newProblem = spaceProblem != state.Problem;
                state.Problem = spaceProblem;
                SaveState(pair.Id, state);
                SetStatus(pair.Id, StatusOf(pair.Id) with { LastSyncedUtc = state.LastSyncedUtc, Problem = spaceProblem, Running = false, Phase = null });
                if (newProblem) _notify("sync.space", $"{name} {spaceProblem}");
                return;
            }

            if (tally.Failed > 0)
            {
                Finish(pair, state, tally.Failed == 1 ? tally.LastError : $"{tally.Failed} files could not be synced. {tally.LastError}", announce: true);
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
            SetStatus(pair.Id, StatusOf(pair.Id) with { LastSyncedUtc = state.LastSyncedUtc, Problem = problem, Running = false, Phase = null });
            if (announce && newProblem) _notify("sync.error", $"{DisplayName(pair)} could not sync: {problem}");
        }

        internal static string Tally(int uploaded, int downloaded, int deleted, int moved = 0)
        {
            var parts = new List<string>();
            if (moved > 0) parts.Add($"{moved} moved");
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

        /// <summary>What a pair would do if it started now: see <see cref="PreviewAsync"/>.</summary>
        internal sealed record SyncPreview(
            int OnPc, int OnDrive, int Same, int ToCompare, int Uploads, int Downloads, int BothKept,
            int Deletes, long DownBytes, long UpBytes, int LeftOut);

        /// <summary>
        /// What a pair would copy right now, for the space check and the preview
        /// shown before a new pair starts: both sides listed through the API and
        /// planned under the pair's own rules, with nothing transferred and
        /// nothing written. Files with the same name and size on both sides are
        /// not read here (that could be hours on a big disk): they are counted
        /// as to be compared, and the first pass compares their checksums before
        /// it skips a single one.
        /// </summary>
        internal static async Task<SyncPreview> PreviewAsync(DriveClient client, DriveSyncPair pair, CancellationToken token,
            Action<string>? progress = null)
        {
            var skips = new SyncSkips();
            var local = Directory.Exists(pair.LocalFolder)
                ? ScanLocal(pair.LocalFolder, pair.IncludeSubfolders, skips,
                    found: n => progress?.Invoke($"Reading the PC folder, {n:N0} files so far"))
                : new Dictionary<string, SyncFile>(SyncNameComparer.Instance);
            progress?.Invoke($"{local.Count:N0} files on the PC. Reading Google Drive");

            var (remote, _) = await ScanRemoteAsync(client, pair.DriveFolderId, pair.IncludeSubfolders, token,
                skipped: skips, found: n => progress?.Invoke($"{local.Count:N0} files on the PC, {n:N0} in Google Drive so far"))
                .ConfigureAwait(false);

            var rules = SyncRules.For(pair);
            var none = new Dictionary<string, SyncBase>(SyncNameComparer.Instance);
            var plan = SyncPlanner.Plan(rules, local, remote, none, null, skips);
            int compare = SyncPlanner.NeedingHash(rules, local, remote, none).Count;
            int same = 0, up = 0, down = 0, kept = 0, deletes = 0;
            long downBytes = 0, upBytes = 0;
            foreach (var a in plan)
            {
                switch (a.Kind)
                {
                    case SyncActionKind.Record: same++; break;
                    case SyncActionKind.Upload: up++; upBytes += local[a.Path].Size; break;
                    case SyncActionKind.Download: down++; downBytes += remote[a.Path].Size; break;
                    case SyncActionKind.KeepBothLocalWins: kept++; upBytes += local[a.Path].Size; break;
                    case SyncActionKind.KeepBothRemoteWins: kept++; downBytes += remote[a.Path].Size; break;
                    case SyncActionKind.DeleteLocal or SyncActionKind.DeleteRemote: deletes++; break;
                }
            }
            return new SyncPreview(local.Count, remote.Count, same, compare, up, down, kept, deletes,
                downBytes, upBytes, skips.Count);
        }

        /// <summary>The preview as one passage, to be read in one go.</summary>
        internal static string Describe(SyncPreview p)
        {
            static string Files(int n) => n == 1 ? "1 file" : $"{n:N0} files";
            static string Are(int n) => n == 1 ? "is" : "are";

            var lines = new List<string>
            {
                p.OnPc == 0 ? "The folder on this PC is empty." : $"The folder on this PC has {Files(p.OnPc)}.",
                p.OnDrive == 0 ? "The Google Drive folder is empty." : $"The Google Drive folder has {Files(p.OnDrive)}.",
            };
            if (p.Same > 0) lines.Add($"{Files(p.Same)} {Are(p.Same)} already the same in both places.");
            if (p.ToCompare > 0)
                lines.Add($"{Files(p.ToCompare)} {Are(p.ToCompare)} in both places with the same name and size. " +
                          "They are checked with Google first, and the ones that match are not copied again.");
            lines.Add(p.Uploads > 0
                ? $"{Files(p.Uploads)}, {Words(p.UpBytes)}, will be copied up to Google Drive."
                : "Nothing will be copied up to Google Drive.");
            lines.Add(p.Downloads > 0
                ? $"{Files(p.Downloads)}, {Words(p.DownBytes)}, will be copied down to this PC."
                : "Nothing will be copied down to this PC.");
            if (p.BothKept > 0)
                lines.Add($"{Files(p.BothKept)} {Are(p.BothKept)} different in each place. Both copies will be kept.");
            if (p.Deletes > 0) lines.Add($"{Files(p.Deletes)} will be deleted.");
            if (p.LeftOut > 0)
                lines.Add($"{Files(p.LeftOut)} will be skipped, because {(p.LeftOut == 1 ? "it is" : "they are")} hidden, " +
                          "system files, or named in a way Windows cannot use.");
            return string.Join(Environment.NewLine, lines);
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
            List<string>? clashing = null, Action<int>? found = null, Action<string>? linked = null)
        {
            var files = new Dictionary<string, SyncFile>(SyncNameComparer.Instance);
            ScanLocalTree(new DirectoryInfo(root), "", subfolders, files, skipped, clashing, found, linked);
            return files;
        }

        /// <summary>A folder and everything under it, into <paramref name="files"/>, as <see cref="ScanLocal"/> does the whole pair.</summary>
        private static void ScanLocalTree(DirectoryInfo top, string topRelative, bool subfolders, Dictionary<string, SyncFile> files,
            SyncSkips? skipped, List<string>? clashing, Action<int>? found, Action<string>? linked)
        {
            var pending = new Stack<(DirectoryInfo Folder, string Relative)>();
            pending.Push((top, topRelative));
            var under = new List<(DirectoryInfo Folder, string Relative)>();

            // A small tree (what a partial look lists, mostly) is read here, one
            // folder at a time. Once there is more than that waiting, the rest
            // is read by several workers: listing a folder is mostly waiting on
            // the file system, and on 33,000 folders of C:\Windows one reader
            // took 2.0 s, four 0.7 s and eight 0.5 s.
            int serial = 0;
            while (pending.Count > 0)
            {
                if (serial >= SerialFolders && pending.Count >= ParallelFrom && ScanWorkers > 1)
                {
                    ScanLocalTreeParallel(pending, subfolders, files, skipped, clashing, found, linked);
                    return;
                }
                var (folder, relative) = pending.Pop();
                under.Clear();
                ScanLocalFolder(folder, relative, subfolders, files, skipped, clashing, under, found, linked);
                foreach (var child in under) pending.Push(child);
                serial++;
            }
        }

        /// <summary>Folders read one at a time before a walk may go parallel.</summary>
        private const int SerialFolders = 32;

        /// <summary>Folders waiting before more readers are worth starting.</summary>
        private const int ParallelFrom = 8;

        /// <summary>How many folders are read at once by a big walk. Tests set 1 to compare.</summary>
        internal static int ScanWorkers { get; set; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

        /// <summary>
        /// The rest of <see cref="ScanLocalTree"/> on <see cref="ScanWorkers"/>
        /// threads. Each folder is read into the worker's own scratch table and
        /// added to <paramref name="files"/> under a lock, so memory is what a
        /// walk on one thread would hold; what is left out is gathered per worker
        /// and merged at the end. The same files, left-outs and clashes as one
        /// thread finds, in another order.
        /// </summary>
        private static void ScanLocalTreeParallel(Stack<(DirectoryInfo Folder, string Relative)> start, bool subfolders,
            Dictionary<string, SyncFile> files, SyncSkips? skipped, List<string>? clashing,
            Action<int>? found, Action<string>? linked)
        {
            var pending = new System.Collections.Concurrent.ConcurrentStack<(DirectoryInfo Folder, string Relative)>(start.Reverse());
            int busy = 0;
            int reported = files.Count;
            var gate = new object();
            Exception? failure = null;
            var linkedSafe = linked == null ? null : new Action<string>(path => { lock (gate) linked(path); });
            var workerSkips = new SyncSkips?[ScanWorkers];
            var workerClashes = new List<string>?[ScanWorkers];

            void Work(int me)
            {
                var mine = new Dictionary<string, SyncFile>(SyncNameComparer.Instance);
                var skips = skipped == null ? null : workerSkips[me] = new SyncSkips();
                var clashes = clashing == null ? null : workerClashes[me] = new List<string>();
                var under = new List<(DirectoryInfo Folder, string Relative)>();
                var spin = new SpinWait();
                try
                {
                    while (true)
                    {
                        if (Volatile.Read(ref failure) != null) return;
                        // Busy before the pop, so an empty stack with nobody
                        // busy really is the end: no folder is between hands.
                        Interlocked.Increment(ref busy);
                        if (!pending.TryPop(out var next))
                        {
                            Interlocked.Decrement(ref busy);
                            if (Volatile.Read(ref busy) == 0 && pending.IsEmpty) return;
                            spin.SpinOnce();
                            continue;
                        }
                        spin.Reset();
                        try
                        {
                            under.Clear();
                            mine.Clear();
                            ScanLocalFolder(next.Folder, next.Relative, subfolders, mine, skips, clashes, under, null, linkedSafe);
                            if (under.Count > 0) pending.PushRange(under.ToArray());
                            if (mine.Count > 0)
                            {
                                int count;
                                lock (gate)
                                {
                                    foreach (var (path, file) in mine) files[path] = file;
                                    count = files.Count;
                                    if (found != null && count / 512 != reported / 512) { reported = count; found(count); }
                                }
                            }
                        }
                        finally { Interlocked.Decrement(ref busy); }
                    }
                }
                catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
            }

            var threads = new Thread[ScanWorkers];
            for (int i = 0; i < threads.Length; i++)
            {
                int me = i;
                threads[i] = new Thread(() => Work(me)) { IsBackground = true, Name = "PC folder scan" };
                threads[i].Start();
            }
            foreach (var thread in threads) thread.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);

            foreach (var skips in workerSkips) if (skips != null) skipped!.Include(skips);
            foreach (var clashes in workerClashes) if (clashes != null) clashing!.AddRange(clashes);
        }

        /// <summary>
        /// The PC side of a pass that knows which folders changed (from the
        /// change journal or the watcher): what the records say for every folder
        /// nothing touched, and the folders in <paramref name="marks"/> listed
        /// again. Gives what <see cref="ScanLocal"/> would, without walking the
        /// rest: the records hold each file as the PC had it when it was last
        /// synced, and nothing in an untouched folder has changed since.
        ///
        /// A subfolder found with no records under it (made, moved in, or
        /// shown again) is listed whole; one with records that is not there any
        /// more (deleted, moved out, renamed) loses them, so its files read as
        /// gone, as a walk would find them. A marked folder inside a hidden or
        /// system folder is not listed, as a walk would not reach it.
        /// </summary>
        /// <param name="clashingBefore">The last pass's PC names left out for clashing; kept for every folder not listed again.</param>
        internal static Dictionary<string, SyncFile> ScanLocalPartly(string root, bool subfolders,
            IReadOnlyDictionary<string, SyncBase> records, PcMarks marks, SyncSkips skipped, List<string> clashing,
            IReadOnlyCollection<string> clashingBefore, Action<string>? linked = null)
        {
            var comparer = SyncNameComparer.Instance;

            // Every folder with records, and the folders above them, by parent.
            var childrenWithRecords = new Dictionary<string, HashSet<string>>(comparer);
            var recorded = new HashSet<string>(comparer) { "" };
            foreach (var path in records.Keys)
                for (var folder = SyncMoves.Parent(path); folder.Length > 0 && recorded.Add(folder); folder = SyncMoves.Parent(folder))
                {
                    var parent = SyncMoves.Parent(folder);
                    if (!childrenWithRecords.TryGetValue(parent, out var set)) childrenWithRecords[parent] = set = new HashSet<string>(comparer);
                    set.Add(folder);
                }

            var listed = new HashSet<string>(comparer);       // listed again on their own
            var replaced = new HashSet<string>(comparer);     // everything under them is what was found now (or nothing)
            var fresh = new Dictionary<string, SyncFile>(comparer);
            var trees = new List<string>();                   // listed whole, at the end

            bool UnderReplaced(string path)
            {
                for (var f = path; ; f = SyncMoves.Parent(f))
                {
                    if (replaced.Contains(f)) return true;
                    if (f.Length == 0) return false;
                }
            }

            // Whether a walk would reach this folder: all of it there, and none
            // of the folders above it hidden, system or unreadable.
            string? Blocked(string folder, out bool gone)
            {
                gone = false;
                if (folder.Length == 0) return null;
                var parts = folder.Split('/');
                var path = root;
                for (int i = 0; i < parts.Length; i++)
                {
                    path = Path.Combine(path, parts[i]);
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); }
                    catch (FileNotFoundException) { gone = true; return null; }
                    catch (DirectoryNotFoundException) { gone = true; return null; }
                    catch { return string.Join('/', parts[..(i + 1)]); }
                    if ((attributes & FileAttributes.Directory) == 0) { gone = true; return null; }
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return string.Join('/', parts[..(i + 1)]);
                }
                return null;
            }

            // Reachable, hidden, or gone: sorted out before anything is listed.
            // False when the folder is not to be listed at all.
            bool Reach(string folder)
            {
                if (!subfolders && folder.Length > 0) return false;
                if (UnderReplaced(folder)) return false;
                if (folder.Length > 0 && skipped.Covers(folder + "/")) return false;
                if (Blocked(folder, out bool gone) is { } hidden)
                {
                    skipped.Folder(hidden);
                    return false;
                }
                if (gone)
                {
                    // Not there: what the records had under it is gone with it.
                    replaced.Add(folder);
                    return false;
                }
                return true;
            }

            // Shallowest first, so a tree claims everything under it before any
            // folder inside it is listed on its own, and is listed once, at the end.
            static int Depth(string f) => f.Length == 0 ? 0 : f.Count(c => c == '/') + 1;
            foreach (var folder in marks.Deep.OrderBy(Depth))
                if (Reach(folder))
                {
                    replaced.Add(folder);
                    trees.Add(folder);
                }

            var subs = new List<(DirectoryInfo Folder, string Relative)>();
            foreach (var folder in marks.Folders.OrderBy(Depth))
            {
                if (listed.Contains(folder) || !Reach(folder)) continue;
                var dir = new DirectoryInfo(folder.Length == 0 ? root : LocalPathFor(root, folder));
                subs.Clear();
                listed.Add(folder);
                ScanLocalFolder(dir, folder, subfolders, fresh, skipped, clashing, subs, null, linked);
                if (!subfolders) continue;
                var here = new HashSet<string>(comparer);
                foreach (var (_, sub) in subs)
                {
                    here.Add(sub);
                    if (!recorded.Contains(sub) && !UnderReplaced(sub))
                    {
                        // Arrived (made, moved in, shown again): new from top to bottom.
                        replaced.Add(sub);
                        trees.Add(sub);
                    }
                }
                if (childrenWithRecords.TryGetValue(folder, out var had))
                    foreach (var sub in had)
                        if (!here.Contains(sub) && !skipped.Covers(sub + "/")) replaced.Add(sub);
            }

            foreach (var tree in trees)
            {
                // One inside another tree is already covered by it.
                if (tree.Length > 0 && UnderReplacedAbove(tree)) continue;
                var dir = new DirectoryInfo(tree.Length == 0 ? root : LocalPathFor(root, tree));
                ScanLocalTree(dir, tree, subfolders, fresh, skipped, clashing, null, linked);
            }

            bool UnderReplacedAbove(string tree)
            {
                for (var f = SyncMoves.Parent(tree); ; f = SyncMoves.Parent(f))
                {
                    if (replaced.Contains(f)) return true;
                    if (f.Length == 0) return false;
                }
            }

            var local = new Dictionary<string, SyncFile>(comparer);
            foreach (var (path, was) in records)
            {
                var parent = SyncMoves.Parent(path);
                if (listed.Contains(parent) || UnderReplaced(parent)) continue;
                if (!subfolders && parent.Length > 0) continue;
                local[path] = new SyncFile(was.LocalSize, was.LocalModifiedUtc);
            }
            foreach (var (path, file) in fresh) local[path] = file;

            // Names left out last time stay left out, and said, wherever nothing was listed again.
            foreach (var entry in clashingBefore)
            {
                var path = entry.TrimEnd('/');
                var parent = SyncMoves.Parent(path);
                if (listed.Contains(parent) || UnderReplaced(parent)) continue;
                clashing.Add(entry);
                if (entry.EndsWith('/')) skipped.Folder(path);
                else skipped.File(path);
                local.Remove(path);
            }
            return local;
        }

        private static readonly EnumerationOptions LocalScanOptions = new() { IgnoreInaccessible = false, AttributesToSkip = 0 };

        /// <summary>
        /// One PC folder of <see cref="ScanLocal"/>: its files into
        /// <paramref name="files"/>, the subfolders to walk into
        /// <paramref name="subfoldersOut"/>, and whatever is left out into
        /// <paramref name="skipped"/>. The pair's own folder unreadable throws
        /// (a failed pass, never an empty one); a subfolder is left out whole.
        /// </summary>
        internal static void ScanLocalFolder(DirectoryInfo folder, string under, bool subfolders,
            Dictionary<string, SyncFile> files, SyncSkips? skipped, List<string>? clashing,
            List<(DirectoryInfo Folder, string Relative)> subfoldersOut, Action<int>? found = null, Action<string>? linked = null)
        {
            const FileAttributes leftOut = FileAttributes.Hidden | FileAttributes.System;

            List<FileSystemInfo> entries;
            try { entries = folder.EnumerateFileSystemInfos("*", LocalScanOptions).ToList(); }
            catch (Exception) when (under.Length > 0)
            {
                skipped?.Folder(under);
                return;
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

                if (isFolder)
                {
                    if (!subfolders) continue;

                    // Not walked, as before, and now known to be there.
                    if ((attributes & leftOut) != 0)
                    {
                        skipped?.Folder(relative);
                        continue;
                    }
                    // A junction or symbolic link: what changes behind it is on
                    // another folder's account, and maybe another volume's journal.
                    if (linked != null && (attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        try { if (entry.LinkTarget != null) linked(relative); } catch { linked(relative); }
                    }
                    subfoldersOut.Add(((DirectoryInfo)entry, relative));
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
                    if (found != null && (files.Count & 511) == 0) found(files.Count);
                }
                catch { skipped?.File(relative); }
            }
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
            double waitScale = 1, Action<int>? found = null,
            IReadOnlyList<(string Id, string Relative)>? only = null, IReadOnlyDictionary<string, string>? known = null,
            bool descend = true)
        {
            // With `only`, just those folders are listed (and any folder found in
            // them that `known` does not have yet, all the way down): the folders
            // the changes feed says changed. Without, the whole tree.
            var files = new Dictionary<string, SyncFile>(SyncNameComparer.Instance);
            var folders = known != null
                ? new Dictionary<string, string>(known, SyncNameComparer.Instance)
                : new Dictionary<string, string>(SyncNameComparer.Instance) { [""] = rootId };
            var level = only != null ? only.ToList() : new List<(string Id, string Relative)> { (rootId, "") };
            int seen = 0;

            while (level.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var listed = new List<DriveEntry>[level.Count];
                await Parallel.ForEachAsync(Enumerable.Range(0, level.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = ListStreams, CancellationToken = token },
                    async (n, ct) =>
                    {
                        listed[n] = await ListOneFolder(client, level[n].Id, waitScale, ct).ConfigureAwait(false);
                        if (found != null) found(Interlocked.Add(ref seen, listed[n].Count(e => !e.IsFolder)));
                    })
                    .ConfigureAwait(false);

                var next = new List<(string Id, string Relative)>();
                for (int n = 0; n < level.Count; n++)
                {
                    Take(level[n].Relative, listed[n], next);
                }
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
                        if (folders.TryAdd(path, entry.Id) && descend) next.Add((entry.Id, path));
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
                    return await client.ListChildren(id, token, max: int.MaxValue).ConfigureAwait(false);
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

        /// <summary>
        /// The Drive folder for a path, made if it is not there. Before making one,
        /// a folder this pair already had at that path (from before this pass) is
        /// asked about by its id: a folder made a moment ago, by a pass that was
        /// then stopped, may not be in a listing yet, and making another split the
        /// files between two folders of one name.
        /// </summary>
        private static async Task<string> EnsureFolderAsync(
            DriveClient client, Dictionary<string, string> folders, string rootId, string relativeFolder,
            CancellationToken token, IReadOnlyDictionary<string, string>? remembered = null)
        {
            if (relativeFolder.Length == 0) return rootId;
            if (folders.TryGetValue(relativeFolder, out var known)) return known;

            int slash = relativeFolder.LastIndexOf('/');
            var parent = await EnsureFolderAsync(client, folders, rootId,
                slash < 0 ? "" : relativeFolder[..slash], token, remembered).ConfigureAwait(false);
            var name = relativeFolder[(slash + 1)..];
            if (remembered != null && remembered.TryGetValue(relativeFolder, out var before) &&
                await client.PlaceNow(before, token).ConfigureAwait(false) is { } place &&
                place.Parents.Contains(parent, StringComparer.Ordinal) && SyncNameComparer.Instance.Equals(place.Name, name))
            {
                folders[relativeFolder] = before;
                return before;
            }
            var id = await client.CreateFolder(name, parent, token).ConfigureAwait(false);
            folders[relativeFolder] = id;
            return id;
        }

        /// <summary>Serialises the fast lane's changes to the pair's records and its counters.</summary>
        private readonly object _fastGate = new();

        /// <summary>How many new small files go up at once. As DriveUpload: Google's limit, not bandwidth.</summary>
        internal const int FastLanes = 16;

        /// <summary>The fewest and most lanes <see cref="AdaptiveLanes"/> may settle on.</summary>
        internal const int FastLanesMin = 4, FastLanesMax = 48;

        /// <summary>An answer slower than this is Google pushing back: one lane fewer.</summary>
        internal const int SlowAnswerMilliseconds = 3000;

        /// <summary>Files up to this size go in the fast lane; bigger ones resume, so they go one at a time.</summary>
        internal const long FastLaneMaxBytes = 4L * 1024 * 1024;

        /// <summary>Below this many new small files the fast lane is not worth what it costs in Pause latency.</summary>
        internal const int FastLaneMinimum = 64;

        /// <summary>
        /// Uploads the plan's small files, new ones and in-place replacements,
        /// <see cref="FastLanes"/> at a time, and returns the paths that went up.
        /// Only files with no saved session and settled on the PC; the rest
        /// (conflicts, big files, anything that fails here) is left for the
        /// careful one-at-a-time loop, which also retries what this could not do.
        ///
        /// Duplicates are the danger in doing this in parallel, so: every folder
        /// the batch needs is made first, one at a time, before any file goes; a
        /// file is uploaded by the same call as the slow path (which looks for
        /// its own tag before ever sending again); and the records are written
        /// under one lock.
        /// </summary>
        private async Task<HashSet<string>> UploadNewSmallAsync(
            DriveClient client, DriveSyncPair pair, PairState state, List<SyncAction> plan,
            Dictionary<string, SyncFile> local, Dictionary<string, SyncFile> remote, Dictionary<string, string> folders,
            string root, int generation, long alreadyUp, Action<long> moved, CancellationToken token)
        {
            var sent = new HashSet<string>(SyncNameComparer.Instance);
            long room = _drive.QuotaLimit > 0 ? _drive.QuotaLimit - _drive.QuotaUsed - alreadyUp : long.MaxValue;
            var batch = new List<string>();
            foreach (var action in plan)
            {
                // New files, and replacements of a Drive file in place (the same
                // id, so never a second copy). Conflicts stay on the slow path.
                if (action.Kind != SyncActionKind.Upload) continue;
                if (!local.TryGetValue(action.Path, out var file) || file.Size > FastLaneMaxBytes) continue;
                if (state.Uploads.ContainsKey(action.Path)) continue;
                var localPath = LocalPathFor(root, action.Path);
                if (StillBeingWritten(localPath, file, DateTime.UtcNow, SettleTime)) continue;
                if (file.Size > room) break;
                room -= file.Size;
                batch.Add(action.Path);
            }
            // Only for a big batch. A handful goes the careful way, one file
            // at a time, where Pause and Edit are heard between every file.
            if (batch.Count < FastLaneMinimum) return sent;

            // The folders, shallowest first, one at a time. A replacement's
            // folder is already known from the listing, so this is a lookup.
            // A create already sent is let finish if the pass stops (see Drain).
            using var drain = new Drain(token);
            foreach (var folder in batch.Select(p => p.LastIndexOf('/') is var at and > 0 ? p[..at] : "")
                         .Distinct(SyncNameComparer.Instance).OrderBy(f => f.Count(c => c == '/')))
            {
                token.ThrowIfCancellationRequested();
                if (Stopped(pair) || GenerationOf(pair.Id) != generation) throw new PairStoppedException();
                await Patiently(pair, () => EnsureFolderAsync(client, folders, pair.DriveFolderId, folder, drain.Token, state.Remembered), client, drain.Token)
                    .ConfigureAwait(false);
            }

            // Every parent is in `folders` now, so the lanes only read it.
            int done = 0;
            var lanes = new AdaptiveLanes(FastLanes, FastLanesMin, FastLanesMax);
            var live = LiveFor(pair.Id);
            await Parallel.ForEachAsync(batch,
                new ParallelOptions { MaxDegreeOfParallelism = FastLanesMax, CancellationToken = token },
                async (path, ct) =>
                {
                    await lanes.EnterAsync(ct).ConfigureAwait(false);
                    var clock = Stopwatch.StartNew();
                    bool landed = false;
                    // Started only while the pass runs; once started, finished on
                    // the drain token, so it is recorded rather than left unknown.
                    try { landed = !ct.IsCancellationRequested && await One(path, drain.Token).ConfigureAwait(false); }
                    finally
                    {
                        live.End(path, Environment.TickCount64);
                        lanes.Leave(landed && clock.ElapsedMilliseconds < SlowAnswerMilliseconds);
                    }
                }).ConfigureAwait(false);
            return sent;

            async Task<bool> One(string path, CancellationToken ct)
            {
                    if (Stopped(pair) || GenerationOf(pair.Id) != generation) return false;
                    var localPath = LocalPathFor(root, path);
                    int slash = path.LastIndexOf('/');
                    var parent = slash < 0 ? pair.DriveFolderId : folders[path[..slash]];
                    var file = local[path];
                    var stamp = DriveClient.UploadStamp(localPath);
                    long reported = 0;
                    DriveClient.DriveUploaded up;
                    remote.TryGetValue(path, out var old);
                    string? replaces = old.Id;
                    live.Begin(path, up: true, file.Size, Environment.TickCount64);
                    try
                    {
                        void Progress(UploadProgress p)
                        {
                            long delta = p.BytesSent - Interlocked.Read(ref reported);
                            if (delta > 0)
                            {
                                Interlocked.Add(ref reported, delta);
                                live.Add(path, up: true, delta, Environment.TickCount64);
                                moved(delta);
                            }
                        }
                        if (replaces != null)
                        {
                            // As the slow path: replaced only while Drive's copy still
                            // holds what the scan saw. Edited in Drive since, it is a
                            // conflict, and the slow path defers it for the next pass.
                            var now = await Patiently(pair, () => client.FileNow(replaces, ct), client, ct).ConfigureAwait(false);
                            if (now == null || !SameAsScanned(now, old)) return false;
                            // An update cannot make a second file, so a resend is safe.
                            up = await Patiently(pair, () => client.UploadDetailed(localPath, Path.GetFileName(localPath), parent,
                                Progress, ct, null, _ => { }, replaces), client, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            up = await SendOnceAsync(pair, client, () => client.UploadDetailed(localPath, Path.GetFileName(localPath), parent,
                                Progress, ct, null, _ => { }, null), file.Size, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        // Never landed: left for the one-at-a-time loop, which
                        // says why if it fails there too.
                        return false;
                    }

                    // Landed. Recorded before anything else can go wrong, so the
                    // slow loop can never send it a second time.
                    if (reported < file.Size)
                    {
                        live.Add(path, up: true, file.Size - reported, Environment.TickCount64);
                        moved(file.Size - reported);
                    }
                    lock (_fastGate)
                    {
                        // Drive made a new file instead of updating (as the slow
                        // path allows for): the old copy is trashed next pass, only
                        // while it still holds what it held.
                        if (replaces != null && up.Id != replaces)
                            state.PendingTrash[replaces] = new PendingTrash(old.Size, old.ModifiedUtc, old.Md5);
                        state.Files[path] = new SyncBase(file.Size, file.ModifiedUtc, up.Size ?? file.Size,
                            Truncate(up.ModifiedUtc ?? stamp), up.Id, up.Md5);
                        sent.Add(path);
                        done++;
                        SaveSoon(pair.Id, state);
                        int count = done;
                        Report(pair.Id, s => s with { Uploaded = count, StepDone = count });
                    }
                    Kill("uploaded " + path);
                    return true;
            }
        }

        /// <summary>New PC files up to this size (and over <see cref="FastLaneMaxBytes"/>) go up many at once.</summary>
        internal const long FastUploadMaxBytes = 64L * 1024 * 1024;

        /// <summary>
        /// New PC files between <see cref="FastLaneMaxBytes"/> and
        /// <see cref="FastUploadMaxBytes"/> (most songs), <see cref="FastLanes"/>
        /// at a time, through the same UploadAsync as the careful loop: a
        /// resumable session saved as it starts, and a create sent once
        /// (SendOnceAsync looks for its own tag before ever sending again), so
        /// a lost answer is never a second copy. Folders are made first, one at
        /// a time. Only new files: a file Drive already has is replaced on the
        /// careful loop. Anything that fails is left there too, which resumes
        /// its session and says why.
        /// </summary>
        private async Task<HashSet<string>> UploadNewMediumAsync(
            DriveClient client, DriveSyncPair pair, PairState state, List<SyncAction> plan,
            Dictionary<string, SyncFile> local, Dictionary<string, SyncFile> remote, Dictionary<string, string> folders,
            string root, int generation, long alreadyUp, Action<long> moved, CancellationToken token)
        {
            var sent = new HashSet<string>(SyncNameComparer.Instance);
            long room = _drive.QuotaLimit > 0 ? _drive.QuotaLimit - _drive.QuotaUsed - alreadyUp : long.MaxValue;
            var batch = new List<string>();
            foreach (var action in plan)
            {
                if (action.Kind != SyncActionKind.Upload) continue;
                if (!local.TryGetValue(action.Path, out var file) || file.Size <= FastLaneMaxBytes || file.Size > FastUploadMaxBytes) continue;
                if (remote.TryGetValue(action.Path, out var there) && there.Id != null) continue;
                if (StillBeingWritten(LocalPathFor(root, action.Path), file, DateTime.UtcNow, SettleTime)) continue;
                if (file.Size > room) break;
                room -= file.Size;
                batch.Add(action.Path);
            }
            if (batch.Count < FastDownloadMinimum) return sent;

            using (var drain = new Drain(token))
                foreach (var folder in batch.Select(p => p.LastIndexOf('/') is var at and > 0 ? p[..at] : "")
                             .Distinct(SyncNameComparer.Instance).OrderBy(f => f.Count(c => c == '/')))
                {
                    token.ThrowIfCancellationRequested();
                    if (Stopped(pair) || GenerationOf(pair.Id) != generation) throw new PairStoppedException();
                    await Patiently(pair, () => EnsureFolderAsync(client, folders, pair.DriveFolderId, folder, drain.Token, state.Remembered),
                        client, drain.Token).ConfigureAwait(false);
                }

            int done = 0;
            var lanes = new AdaptiveLanes(FastLanes, FastLanesMin, FastLanesMax);
            await Parallel.ForEachAsync(batch,
                new ParallelOptions { MaxDegreeOfParallelism = FastLanesMax, CancellationToken = token },
                async (path, ct) =>
                {
                    await lanes.EnterAsync(ct).ConfigureAwait(false);
                    var clock = Stopwatch.StartNew();
                    bool landed = false;
                    try { landed = await One(path, ct).ConfigureAwait(false); }
                    finally { lanes.Leave(landed && clock.ElapsedMilliseconds < SlowAnswerMilliseconds * 4); }
                }).ConfigureAwait(false);
            return sent;

            async Task<bool> One(string path, CancellationToken ct)
            {
                if (Stopped(pair) || GenerationOf(pair.Id) != generation) return false;
                SyncBase up;
                try
                {
                    up = await UploadAsync(client, folders, pair, state, path, LocalPathFor(root, path), local[path],
                        null, null, moved, ct).ConfigureAwait(false);
                }
                catch (Exception) when (!ct.IsCancellationRequested) { return false; }
                lock (_fastGate)
                {
                    state.Files[path] = up;
                    sent.Add(path);
                    done++;
                    SaveSoon(pair.Id, state);
                    int count = done;
                    Report(pair.Id, s => s with { Uploaded = count, StepDone = count });
                }
                Kill("uploaded " + path);
                return true;
            }
        }

        /// <summary>Drive files up to this size, new to the PC, come down many at once; bigger ones one at a time.</summary>
        internal const long FastDownloadMaxBytes = 64L * 1024 * 1024;

        /// <summary>Below this many, downloading at once is not worth it. The live probe raises it to compare.</summary>
        internal static int FastDownloadMinimum { get; set; } = 8;

        /// <summary>
        /// Downloads the plan's new Drive files (nothing at that path on the
        /// PC) up to <see cref="FastDownloadMaxBytes"/>, <see cref="FastLanes"/>
        /// at a time, and returns the paths that came down. One at a time, each
        /// file waited on a round trip to Google before a byte moved: a first
        /// sync of 9,000 songs spent hours just waiting.
        ///
        /// Each goes through the same DownloadAsync as the careful loop (a
        /// .partial, resumable, checked by MD5, and put in place only if nothing
        /// has appeared at that path since). A file already on the PC is never
        /// taken here, so nothing is replaced or recycled in parallel. Anything
        /// that fails is left for the careful loop, which tries again and says
        /// why. Records are changed only under _fastGate.
        /// </summary>
        private async Task<HashSet<string>> DownloadNewSmallAsync(
            DriveClient client, DriveSyncPair pair, PairState state, List<SyncAction> plan,
            Dictionary<string, SyncFile> local, Dictionary<string, SyncFile> remote,
            string root, int generation, Action<long> moved, CancellationToken token)
        {
            var got = new HashSet<string>(SyncNameComparer.Instance);
            var batch = new List<string>();
            var (free, volume, _) = DiskOfRoot(root);
            long need = 0;
            foreach (var action in plan)
            {
                if (action.Kind != SyncActionKind.Download || local.ContainsKey(action.Path)) continue;
                if (!remote.TryGetValue(action.Path, out var file) || file.Size > FastDownloadMaxBytes || file.Id == null) continue;
                if (!IsInside(root, LocalPathFor(root, action.Path))) continue;
                // Room for all of it, with the margin the careful loop keeps.
                if (volume > 0 && !SyncSpace.Fits(need + file.Size, free, volume)) break;
                need += file.Size;
                batch.Add(action.Path);
            }
            if (batch.Count < FastDownloadMinimum) return got;

            int done = 0;
            var lanes = new AdaptiveLanes(FastLanes, FastLanesMin, FastLanesMax);
            await Parallel.ForEachAsync(batch,
                new ParallelOptions { MaxDegreeOfParallelism = FastLanesMax, CancellationToken = token },
                async (path, ct) =>
                {
                    await lanes.EnterAsync(ct).ConfigureAwait(false);
                    var clock = Stopwatch.StartNew();
                    bool landed = false;
                    try { landed = await One(path, ct).ConfigureAwait(false); }
                    finally { lanes.Leave(landed && clock.ElapsedMilliseconds < SlowAnswerMilliseconds); }
                }).ConfigureAwait(false);
            return got;

            async Task<bool> One(string path, CancellationToken ct)
            {
                if (Stopped(pair) || GenerationOf(pair.Id) != generation) return false;
                SyncBase? landed;
                try
                {
                    landed = await DownloadAsync(client, pair.Id, state, path, remote[path], LocalPathFor(root, path),
                        null, false, moved, ct).ConfigureAwait(false);
                }
                catch (Exception) when (!ct.IsCancellationRequested) { return false; }
                if (landed == null) return false;

                lock (_fastGate)
                {
                    state.Files[path] = landed;
                    got.Add(path);
                    done++;
                    SaveSoon(pair.Id, state);
                    int count = done;
                    Report(pair.Id, s => s with { Downloaded = count, StepDone = count });
                }
                SyncHistory.Add(pair.Id, "Downloaded", path);
                Kill("downloaded " + path);
                return true;
            }
        }

        /// <summary>
        /// Sends a new file, once. When Google lost the answer and would not
        /// say whether it landed, it is never simply sent again (that was a
        /// second copy, five in the worst test): Drive is asked again, patiently,
        /// whether the tagged upload is there. There: that is the upload. Drive
        /// says it is not: sent. Drive still will not say: the file fails this
        /// pass, and the next pass lists the folder and adopts it if it landed.
        /// </summary>
        private async Task<DriveClient.DriveUploaded> SendOnceAsync(DriveSyncPair pair, DriveClient client,
            Func<Task<DriveClient.DriveUploaded>> send, long size, CancellationToken token)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return await Patiently(pair, send, client, token).ConfigureAwait(false); }
                catch (Exception ex) when (attempt < 3 && !token.IsCancellationRequested &&
                                           DriveClient.UnknownIfLandedException.Find(ex) is { Lookup: not null })
                {
                    var unknown = DriveClient.UnknownIfLandedException.Find(ex)!;
                    var there = await Patiently(pair,
                        () => DriveClient.ResolveUploadAsync(unknown, size, token), client, token).ConfigureAwait(false);
                    if (there != null) return there;
                    // Drive says it is not there: sending is safe.
                }
            }
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
            // Shown as moving, with its speed, until it is done or given up.
            var live = LiveFor(pair.Id);
            live.Begin(path, up: true, local.Size, Environment.TickCount64);
            try
            {
                return await UploadOneAsync(client, folders, pair, state, path, localPath, local, replaces, old,
                    n => { live.Add(path, up: true, n, Environment.TickCount64); moved(n); }, token).ConfigureAwait(false);
            }
            finally { live.End(path, Environment.TickCount64); }
        }

        private async Task<SyncBase> UploadOneAsync(
            DriveClient client, Dictionary<string, string> folders, DriveSyncPair pair, PairState state,
            string path, string localPath, SyncFile local, string? replaces, SyncFile? old, Action<long> moved,
            CancellationToken token)
        {
            int slash = path.LastIndexOf('/');
            var parent = await EnsureFolderAsync(client, folders, pair.DriveFolderId, slash < 0 ? "" : path[..slash], token, state.Remembered)
                .ConfigureAwait(false);

            // A session saved from an earlier, interrupted attempt at this same
            // file carries on from where Google says it stopped. Only one of the
            // same kind: a session that updates a file is not resumed to make a
            // new one, or the other way round.
            // The records are shared by every upload running at once
            // (UploadNewMediumAsync), so they are only touched under _fastGate.
            string? resume = null;
            UploadSession? saved;
            lock (_fastGate) state.Uploads.TryGetValue(path, out saved);
            if (saved != null && saved.Size == local.Size &&
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
                        lock (_fastGate)
                        {
                            state.Uploads[path] = new UploadSession(session, local.Size, local.ModifiedUtc, DateTime.UtcNow, into);
                            SaveSoon(pair.Id, state, now: local.Size >= SaveAtOnceBytes);
                        }
                    },
                    into);
            try
            {
                try
                {
                    sent = replaces == null
                        ? await SendOnceAsync(pair, client, () => Send(null, resume), local.Size, token).ConfigureAwait(false)
                        : await Send(replaces, resume).ConfigureAwait(false);
                }
                catch (Exception ex) when (replaces != null && !token.IsCancellationRequested && NotFound(ex))
                {
                    // The file to update went between the check and the upload
                    // (trashed on the website): this becomes a new file. Should
                    // the old one be there after all, it is trashed below.
                    lock (_fastGate) state.Uploads.Remove(path);
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
                lock (_fastGate) state.Uploads.Remove(path);
                throw;
            }

            if (reported < local.Size) moved(local.Size - reported);
            lock (_fastGate) state.Uploads.Remove(path);

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
        /// <summary>
        /// Where a download is written until it is whole: the name plus
        /// ".partial", shortened (with a hash, so two long names never share one)
        /// when that would pass Windows' 255 characters. A 255-character name
        /// from Drive used to fail every pass at this step.
        /// </summary>
        internal static string PartialPath(string localPath)
        {
            const string suffix = ".partial";
            var name = Path.GetFileName(localPath);
            if (name.Length + suffix.Length <= 255) return localPath + suffix;
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(name)))[..8];
            int keep = 255 - suffix.Length - hash.Length - 1;
            if (char.IsHighSurrogate(name[keep - 1])) keep--;
            return Path.Combine(Path.GetDirectoryName(localPath) ?? "", name[..keep] + "~" + hash + suffix);
        }

        private static bool NotFound(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e is DriveClient.DriveStatusException { Status: 404 }) return true;
            return false;
        }

        /// <summary>How much of a download is already on disk and can be continued from.</summary>
        private static long PartialLength(PairState state, string path, string localPath, SyncFile remote)
        {
            var partial = PartialPath(localPath);
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
            // Shown as moving, with its speed, until it is done or given up.
            var live = LiveFor(pairId);
            live.Begin(path, up: false, remote.Size, Environment.TickCount64);
            try
            {
                return await DownloadOneAsync(client, pairId, state, path, remote, localPath, scanned, keepOld,
                    n => { live.Add(path, up: false, n, Environment.TickCount64); moved(n); }, token).ConfigureAwait(false);
            }
            finally { live.End(path, Environment.TickCount64); }
        }

        private async Task<SyncBase?> DownloadOneAsync(
            DriveClient client, string pairId, PairState state, string path, SyncFile remote, string localPath,
            SyncFile? scanned, bool keepOld, Action<long> moved, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            var partial = PartialPath(localPath);

            // The records are shared by every download running at once
            // (DownloadNewSmallAsync), so they are only touched under _fastGate.
            long offset;
            lock (_fastGate) offset = PartialLength(state, path, localPath, remote);
            if (offset == 0)
            {
                try { File.Delete(partial); } catch { }
            }

            // Remembered before the first byte, so a drop at any point resumes:
            // at once for a big file, within two seconds for a small one, which
            // costs little to fetch again. Writing the whole state for every
            // small file made a pass of many files quadratic.
            lock (_fastGate)
            {
                state.Partials[path] = new PartialDownload(remote.Id ?? "", remote.Size, remote.ModifiedUtc, remote.Md5);
                SaveSoon(pairId, state, now: remote.Size >= SaveAtOnceBytes);
            }
            if (offset > 0) moved(offset);

            // One request for the rest of the file, written and hashed as it
            // arrives (DownloadTo). It was 8MB at a time with a round trip to
            // Google between each, and then the whole file read back from disk
            // a second time for its checksum.
            using var md5 = remote.Md5 != null ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
            await using (var file = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Open,
                             FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                file.SetLength(offset);

                // A resumed download: the part already here is hashed first,
                // once, so the checksum still covers the whole file.
                if (md5 != null && offset > 0)
                {
                    var head = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
                    try
                    {
                        file.Position = 0;
                        int n;
                        while (file.Position < offset &&
                               (n = await file.ReadAsync(head.AsMemory(0, (int)Math.Min(head.Length, offset - file.Position)), token)
                                   .ConfigureAwait(false)) > 0)
                            md5.AppendData(head, 0, n);
                    }
                    finally { System.Buffers.ArrayPool<byte>.Shared.Return(head); }
                }

                file.Position = offset;
                await client.DownloadTo(remote.Id!, offset, remote.Size, file, block =>
                {
                    md5?.AppendData(block.Span);
                    moved(block.Length);
                }, token).ConfigureAwait(false);
            }

            // Checked before it replaces anything: the size, and the checksum
            // when Drive gives one. A bad copy is thrown away and fetched again.
            bool good = new FileInfo(partial).Length == remote.Size;
            if (good && md5 != null)
                good = string.Equals(Convert.ToHexString(md5.GetHashAndReset()), remote.Md5, StringComparison.OrdinalIgnoreCase);
            if (!good)
            {
                try { File.Delete(partial); } catch { }
                lock (_fastGate) state.Partials.Remove(path);
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
            lock (_fastGate) state.Partials.Remove(path);

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
                if (state.Store != null)
                {
                    long began = Environment.TickCount64;
                    try { state.Store.SaveSlice(state); } catch { }
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
                catch { }
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
            try
            {
                if (!Directory.Exists(StateDirectory)) return;
                var keep = new HashSet<string>(pairs.Select(p => p.Id + ".json"), StringComparer.OrdinalIgnoreCase);

                // Under the state lock, so a save already under way finishes
                // first, and the next one sees the pair removed.
                lock (StateLock)
                {
                    foreach (var file in Directory.GetFiles(StateDirectory, "*.json"))
                        if (!keep.Contains(Path.GetFileName(file))) { try { File.Delete(file); } catch { } }
                    foreach (var dir in Directory.GetDirectories(StateDirectory, "*.shards"))
                        if (!keep.Contains(Path.GetFileNameWithoutExtension(dir) + ".json")) { try { Directory.Delete(dir, true); } catch { } }
                    foreach (var pattern in new[] { "*.history", "*.listing" })
                        foreach (var file in Directory.GetFiles(StateDirectory, pattern))
                            if (!keep.Contains(Path.GetFileNameWithoutExtension(file) + ".json")) { try { File.Delete(file); } catch { } }
                }
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
