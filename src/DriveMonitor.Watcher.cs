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
    // The folder monitor, change watching: which PC changes are news (watchers,
    // the change journal through PcChanges, own writes), when a pair becomes due
    // (Soon, the burst limit, SoonAfter, the shared look at Drive's changes
    // feed), and how a pass learns which PC folders to list again (LookAtPc).
    // DriveMonitor.cs is the core: pairs, the loop, passes, batches, state.
    internal sealed partial class DriveMonitor
    {
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
                if (OwnWritesSweepDue(_ownWrites.Count, now, _ownWritesSweptAt))
                {
                    _ownWritesSweptAt = now;
                    foreach (var old in _ownWrites.Where(kv => now - kv.Value > 5000).Select(kv => kv.Key).ToList())
                        _ownWrites.Remove(old);
                }
                return _ownWrites.TryGetValue(path, out var at) && now - at <= 5000;
            }
        }

        private long _ownWritesSweptAt = long.MinValue / 2;

        /// <summary>
        /// Whether the own-writes table is swept of entries past five seconds:
        /// once it holds over 256, and at most once a second. Swept on every
        /// watcher event, a fast download of small files (thousands written in
        /// five seconds, several events each) walked the whole table per event
        /// under the monitor's lock.
        /// </summary>
        internal static bool OwnWritesSweepDue(int count, long now, long sweptAt) =>
            count > 256 && now - sweptAt >= 1000;

        public static string StateDirectory => Path.Combine(Settings.AppDataDir, "drive-monitor");

        /// <summary>Each watched pair's rules as they are now, for <see cref="NotNews"/> (an edit can keep the watcher).</summary>
        private readonly Dictionary<string, SyncRules> _watchRules = new();

        /// <summary>
        /// Whether a change the watcher saw is one no pass would act on: a
        /// name the planner always skips, something the pair's own settings
        /// leave out (the skip list, kept on Drive only), or anything hidden or
        /// system, or inside a hidden or system folder, which a walk never
        /// reaches. A path that cannot be looked at (gone already) is news.
        /// </summary>
        /// <summary>
        /// The attributes that keep a file or folder out of the sync: system
        /// only. Hidden was here too until 7.0.0, and Git hides the .git
        /// folder it makes, so a repository made on one PC never reached the
        /// other while one that came down from Drive (unhidden) synced both
        /// ways (Conner: "the code folder isn't entirely completely synced";

        internal static bool NotNews(string root, string fullPath, SyncRules? rules, VisibleFolders? visible = null)
        {
            string relative;
            try { relative = Relative(root, fullPath); }
            catch { return false; }
            if (relative.Length == 0 || relative == "." || relative.StartsWith("../", StringComparison.Ordinal)) return false;
            if (SyncPlanner.IsSkippedPath(relative) || rules?.LeavesOut(relative) == true) return true;
            var at = root;
            var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            long now = Environment.TickCount64;
            for (int i = 0; i < parts.Length; i++)
            {
                at = Path.Combine(at, parts[i]);
                bool folderAbove = i < parts.Length - 1;
                if (folderAbove && IsResilioFolder(parts[i])) return true;
                if (folderAbove && visible?.Knows(at, now) == true) continue;
                try
                {
                    var attributes = File.GetAttributes(at);
                    if ((attributes & LeftOutAttributes) != 0) return true;
                    if (!folderAbove && (attributes & FileAttributes.Directory) != 0 && IsResilioFolder(parts[i])) return true;
                }
                catch { return false; }
                if (folderAbove) visible?.Note(at, now);
            }
            return false;
        }

        /// <summary>
        /// Folders above a watched change lately seen neither hidden nor
        /// system, so a burst of events in one deep folder costs one attribute
        /// read each (the item itself), not one per folder above it: a download
        /// of thousands of small files eight folders down read every folder's
        /// attributes for each of the several events per file. Only "not hidden"
        /// is kept, for <see cref="ForMs"/>: a folder just hidden can at worst
        /// make one needless pass, never a missed change. A hidden one is read
        /// every time.
        /// </summary>
        internal sealed class VisibleFolders
        {
            internal const long ForMs = 2000;
            private const int Most = 4096;
            private readonly object _gate = new();
            private readonly Dictionary<string, long> _seen = new(StringComparer.OrdinalIgnoreCase);

            public bool Knows(string folder, long now)
            {
                lock (_gate) return _seen.TryGetValue(folder, out var at) && now - at < ForMs;
            }

            public void Note(string folder, long now)
            {
                lock (_gate)
                {
                    if (_seen.Count >= Most) _seen.Clear();
                    _seen[folder] = now;
                }
            }
        }

        /// <summary>The newest watcher asked for, per pair: one made for an older ticket is thrown away.</summary>
        private readonly Dictionary<string, long> _watchTickets = new();
        private long _lastTicket;
        private bool _disposed;

        /// <summary>Whether a PC folder is there, asked by the watcher's worker. The suite holds it to stand for a dead share.</summary>
        internal Func<string, bool> FolderThere { get; set; } = Directory.Exists;

        /// <summary>Makes a pair's folder watcher. The suite swaps in one it can make fail, as a dropped share does.</summary>
        internal Func<string, FileSystemWatcher> MakeWatcher { get; set; } = path => new FileSystemWatcher(path);

        /// <summary>
        /// Makes a pair's folder watcher, on a worker (see <see cref="Reload"/>),
        /// and puts it in place only while <paramref name="ticket"/> is still the
        /// pair's newest and the monitor is open.
        /// </summary>
        private void WatchAsWanted(DriveSyncPair pair, long ticket)
        {
            FileSystemWatcher? watcher = null;
            try
            {
                if (!FolderThere(pair.LocalFolder)) return;
                lock (_gate) if (_disposed || !_watchTickets.TryGetValue(pair.Id, out var now) || now != ticket) return;

                watcher = MakeWatcher(pair.LocalFolder);
                watcher.IncludeSubdirectories = pair.IncludeSubfolders;
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                       NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes;
                watcher.InternalBufferSize = 64 * 1024;

                // Events make a pass due, and say which folders to list
                // again where the volume has no change journal (see
                // LookAtPc). A buffer overflow (Error) loses that, and the
                // next pass walks the whole folder.
                //
                // The monitor's own writes are noted but are not news: a
                // download landing used to make the next pass due, which
                // listed the whole of Drive again for nothing.
                //
                // Nor is anything the pair leaves out (see NotNews): Resilio
                // Sync writes and deletes a test file in its hidden .sync
                // folder every ten minutes, and each one ran a pass, then
                // another for the folder's own time changing a moment later.
                var id = pair.Id;
                var root = pair.LocalFolder;
                var journal = new PcWatchJournal();
                var visible = new VisibleFolders();
                bool Ignored(string fullPath)
                {
                    SyncRules? rules;
                    lock (_gate) _watchRules.TryGetValue(id, out rules);
                    return NotNews(root, fullPath, rules, visible);
                }
                void Seen(string fullPath, WatcherChangeTypes kind)
                {
                    if (Ignored(fullPath)) return;
                    journal.Note(Relative(root, fullPath), kind);
                    if (!IsMine(fullPath)) Soon(id);
                }
                FileSystemEventHandler changed = (_, e) => Seen(e.FullPath, e.ChangeType);
                watcher.Created += changed;
                watcher.Changed += changed;
                watcher.Deleted += changed;
                watcher.Renamed += (_, e) =>
                {
                    if (!Ignored(e.OldFullPath)) journal.Note(Relative(root, e.OldFullPath), WatcherChangeTypes.Deleted);
                    // Renamed from a name the pair syncs to one it leaves out
                    // (a download finished as .tmp, say) is still news: the old one has gone.
                    if (!Ignored(e.FullPath)) Seen(e.FullPath, WatcherChangeTypes.Renamed);
                    else if (!Ignored(e.OldFullPath) && !IsMine(e.FullPath)) Soon(id);
                };
                // An overflow loses events, but the watcher reads on. Any other
                // failure (a share dropping, a USB drive pulled, the folder
                // deleted) leaves it reading nothing for good, sometimes still
                // saying it is on: it is retired, and the pair's next pass makes
                // a new one (EnsureWatcher). Kept, its empty account was trusted
                // again after one walk, and new PC files waited for the daily walk.
                watcher.Error += (sender, e) =>
                {
                    if (e.GetException() is InternalBufferOverflowException && sender is FileSystemWatcher { EnableRaisingEvents: true })
                        journal.Lose();
                    else
                        RetireWatcher(id, sender as FileSystemWatcher, journal);
                    Soon(id);
                };
                watcher.EnableRaisingEvents = true;

                lock (_gate)
                {
                    if (!_disposed && _watchTickets.TryGetValue(pair.Id, out var now) && now == ticket &&
                        !_watchers.ContainsKey(pair.Id))
                    {
                        _watchers[pair.Id] = watcher;
                        _watchJournals[pair.Id] = journal;
                        watcher = null;
                    }
                }
            }
            catch { }
            finally
            {
                try { watcher?.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Lets go of a pair's watcher that has stopped for good, or of any it
        /// has when <paramref name="watcher"/> is null (its folder has gone).
        /// Its account dies with it, so nothing seen before the gap is trusted
        /// after it. Only the watcher and journal named go: a newer pair's are kept.
        /// </summary>
        private void RetireWatcher(string pairId, FileSystemWatcher? watcher = null, PcWatchJournal? journal = null)
        {
            journal?.Die();
            FileSystemWatcher? retired = null;
            lock (_gate)
            {
                if (_watchers.TryGetValue(pairId, out var now) && (watcher == null || ReferenceEquals(now, watcher)))
                {
                    retired = now;
                    _watchers.Remove(pairId);
                }
                if (_watchJournals.TryGetValue(pairId, out var held) && (journal == null || ReferenceEquals(held, journal)))
                {
                    held.Die();
                    _watchJournals.Remove(pairId);
                }
            }
            retired ??= watcher;
            if (retired != null) _ = Task.Run(() => { try { retired.Dispose(); } catch { } });
        }

        /// <summary>
        /// Makes a pair's watcher if it has none, or only a dead one: its PC
        /// folder was missing when the pair was loaded (a USB drive not plugged
        /// in yet, a share not up), or the watcher failed. Called by the pass,
        /// on its worker, once the folder has answered, and before the PC side
        /// is looked at, so this pass's whole walk is the one the new watcher
        /// is trusted after.
        /// </summary>
        private void EnsureWatcher(string pairId)
        {
            DriveSyncPair copy;
            long ticket;
            FileSystemWatcher? dead = null;
            lock (_gate)
            {
                if (_disposed || _removed.Contains(pairId)) return;
                var live = _pairs.FirstOrDefault(p => p.Id == pairId);
                if (live == null || live.Paused) return;
                if (_watchers.ContainsKey(pairId) && _watchJournals.TryGetValue(pairId, out var journal) && !journal.Dead) return;
                if (_watchers.Remove(pairId, out var old)) dead = old;
                _watchJournals.Remove(pairId);
                ticket = ++_lastTicket;
                _watchTickets[pairId] = ticket;
                copy = live.Copy();
            }
            if (dead != null) _ = Task.Run(() => { try { dead.Dispose(); } catch { } });
            WatchAsWanted(copy, ticket);
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
                _debounce ??= new System.Threading.Timer(_ =>
                {
                    lock (_gate) _burstSince = 0;
                    Wake();
                });
                // Three quiet seconds, but never more than BurstMost from the
                // first change: a build writing files for a minute put the
                // pass off for all of it, and the edit saved before the build
                // waited with it (7.0.0).
                long now = Environment.TickCount64;
                if (_burstSince == 0) _burstSince = now;
                var left = BurstMost - TimeSpan.FromMilliseconds(now - _burstSince);
                var quiet = TimeSpan.FromSeconds(3);
                _debounce.Change(left < quiet ? (left > TimeSpan.Zero ? left : TimeSpan.Zero) : quiet, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>When the burst of changes now being waited out began (tick count), 0 for none.</summary>
        private long _burstSince;

        /// <summary>The longest a run of changes can put a pass off.</summary>
        internal static TimeSpan BurstMost { get; set; } = TimeSpan.FromSeconds(12);

        /// <summary>A pair again once what a pass left for later has settled (see <see cref="SettleTime"/>).</summary>
        private void SoonAfter(string pairId, TimeSpan delay)
        {
            lock (_gate)
            {
                _due.Add(pairId);
                LastSettleWake = delay;
                if (_disposed) return;
                _settled ??= new System.Threading.Timer(_ => Wake());
                _settled.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>For the suite: whether something has made the pair due since it last asked (and takes it).</summary>
        internal bool TakeDueForTests(string pairId)
        {
            lock (_gate) return _due.Remove(pairId);
        }

        /// <summary>For the suite: how soon the last pass that held a file back asked to run again.</summary>
        internal TimeSpan? LastSettleWake { get; private set; }

        /// <summary>Past the settle time before a held-back file is looked at again, so it has surely settled.</summary>
        internal static TimeSpan SettleMargin { get; set; } = TimeSpan.FromSeconds(1);

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
        private readonly Dictionary<string, string?> _driveOfFolder = new();

        /// <summary>
        /// The shared look at each drive's feed (keyed by drive id, "" for My
        /// Drive): where it has read up to, since when it has been unbroken, and
        /// when it last found (and so used up) any change. A pair is skipped
        /// only if it began its last pass after both, so no change between its
        /// own read of the feed and the shared one is missed: a pair not due in
        /// the minute whose look used up a change missed it until QuietMost.
        /// </summary>
        private readonly Dictionary<string, (string Token, DateTime Since, DateTime Found)> _shared = new();

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
                // Known only for the Drive folder it was read for: a pair's folder can change.
                lock (_gate)
                    known = _driveOf.TryGetValue(pair.Id, out driveId) &&
                            _driveOfFolder.TryGetValue(pair.Id, out var forFolder) && forFolder == pair.DriveFolderId;
                if (!known)
                {
                    try { driveId = await client.DriveOf(pair.DriveFolderId, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch { run.Add(pair); continue; }
                    lock (_gate) { _driveOf[pair.Id] = driveId; _driveOfFolder[pair.Id] = pair.DriveFolderId; }
                }
                var key = driveId ?? "";
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
                list.Add(pair);
            }

            foreach (var (key, group) in groups)
            {
                string? driveId = key.Length == 0 ? null : key;
                (string Token, DateTime Since, DateTime Found) look;
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
                        lock (_gate) _shared[key] = (start, since, DateTime.MinValue);
                        run.AddRange(group);
                        continue;
                    }

                    var (changes, next) = await client.SyncChangesSince(look.Token, driveId, token).ConfigureAwait(false);
                    Interlocked.Increment(ref SharedLooks);
                    // Taken once the read is in: a pass that began after this saw the change itself.
                    var found = changes.Count > 0 ? DateTime.UtcNow : look.Found;
                    lock (_gate) _shared[key] = (next, look.Since, found);
                    foreach (var pair in group)
                    {
                        bool unbroken;
                        lock (_gate) unbroken = _ranFrom.TryGetValue(pair.Id, out var ran) && ran >= look.Since && ran >= found;
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

        /// <summary>The watchers' account of what changed, per pair, for PC folders with no change journal.</summary>
        private readonly Dictionary<string, PcWatchJournal> _watchJournals = new();

        /// <summary>Where a PC folder's change journal is, or null with none. The suite answers null to stand for exFAT or a share.</summary>
        internal Func<string, UsnJournal.Position?> JournalOf { get; set; } = UsnJournal.Where;

        /// <summary>
        /// The settings a whole walk of the PC folder was for: change any and the
        /// next pass walks it again. Walk is raised when a walk starts finding
        /// something earlier ones did not (2: empty folders, made in Drive), so
        /// every pair walks once after the update.
        /// </summary>
        internal static string PcWalkKeyOf(DriveSyncPair pair) =>
            JsonSerializer.Serialize(new { pair.LocalFolder, pair.DriveFolderId, Rules = SyncRules.For(pair), Walk = 4 });

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
        /// <summary>
        /// Whether the PC side may be looked at by what changed rather than
        /// walked whole. Normally that is having records. With Free up space,
        /// every file cleared off the PC is forgotten, so a folder that has done
        /// its job has none, and every pass walked all of it again ("nothing
        /// synced yet"; every few seconds while Resilio was adding files to a
        /// 24,000-folder tree). There, a whole walk done before is enough.
        /// </summary>
        private static bool HasSyncedBefore(DriveSyncPair pair, PairState state, bool hasRecords) =>
            hasRecords || (pair.FreesUpSpace && state.LastPcWalkUtc != null && state.UsnNext != null);

        private PcLook LookAtPc(DriveSyncPair pair, PairState state, bool hasRecords)
        {
            var look = new PcLook { Journal = JournalOf(pair.LocalFolder) };
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
    }
}
