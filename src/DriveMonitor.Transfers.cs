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
    // The folder monitor, carrying the plan out: ExecuteAsync, the lanes for
    // many small files, uploads, downloads, folders made, trash, free up space
    // and empty folders. DriveMonitor.cs is the core.
    internal sealed partial class DriveMonitor
    {
        /// <summary>How old an empty folder has to be before free up space clears it, so one just made is not taken from under a file arriving in it.</summary>
        internal static TimeSpan EmptyFolderAge = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Free up space clears empty folders too (Conner, 3.0.16: the PC side
        /// should end up holding nothing but Resilio's .sync). A folder the pass
        /// looked at, or one left for later, is deleted when it holds nothing at
        /// all, Drive has it, it is not left out (.sync, the skip list), hidden,
        /// system or a link, and it was made over <see cref="EmptyFolderAge"/>
        /// ago; then the folder above it is tried the same way. A folder that
        /// arrives later with files in it is taken whole by the next pass, as
        /// any new folder is. The delete is Windows' own, which refuses a folder
        /// with anything in it, so nothing inside can go with it. True when the
        /// state changed.
        /// </summary>
        private bool ClearEmptyFolders(DriveSyncPair pair, PairState state, SyncRules rules, SyncSkips skips,
            IEnumerable<string> seen, IReadOnlyDictionary<string, string> onDrive)
        {
            if (!pair.FreesUpSpace || rules.Mode != SyncMode.UploadOnly || rules.CopyDeletes || !rules.IncludeSubfolders ||
                Stopped(pair))
                return false;
            var before = state.EmptyLater ?? new List<string>();
            var candidates = new HashSet<string>(seen, SyncNameComparer.Instance);
            candidates.UnionWith(before);
            var later = new HashSet<string>(SyncNameComparer.Instance);
            var now = DateTime.UtcNow;
            int cleared = 0;
            const FileAttributes kept = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;

            foreach (var folder in candidates.OrderByDescending(f => f.Count(c => c == '/')))
            {
                // Paused, removed or edited (Free up space unticked): no more
                // folders go, and those waiting their turn keep it.
                if (Stopped(pair))
                {
                    later.UnionWith(before);
                    break;
                }
                for (var at = folder; at.Length > 0; at = SyncMoves.Parent(at))
                {
                    if (!onDrive.ContainsKey(at) || IsLeftOutFolder(at, rules, skips) ||
                        at.Split('/').Any(part => part.Equals(".sync", StringComparison.OrdinalIgnoreCase)))
                        break;
                    var dir = new DirectoryInfo(Path.Combine(pair.LocalFolder, at.Replace('/', Path.DirectorySeparatorChar)));
                    try
                    {
                        if (!dir.Exists) continue;      // cleared already: the one above may be empty now
                        if ((dir.Attributes & kept) != 0 || dir.EnumerateFileSystemInfos().Any()) break;
                        if (now - dir.CreationTimeUtc < EmptyFolderAge) { later.Add(at); break; }
                        dir.Delete(recursive: false);
                        cleared++;
                    }
                    catch (Exception) { break; }
                }
            }

            state.EmptyLater = later.Count == 0 ? null : later.ToList();
            if (cleared > 0) GoogleDrive.Trace($"sync {DisplayName(pair)}: cleared {cleared} empty folders off the PC");
            return cleared > 0 || later.Count != before.Count || !later.SetEquals(before);
        }

        /// <summary>
        /// PC folders the pass walked through that Drive does not have, made in
        /// Drive: the files sync only makes a folder to put a file in, so an
        /// empty folder (or one holding only skipped files) never appeared.
        /// A depth at a time, <see cref="FolderLanes"/> at once, as
        /// <see cref="MakeFoldersAsync"/>: one at a time, the 3,400 folders
        /// Resilio Sync lays out before its files took most of an hour, and no
        /// file moved meanwhile. Not in download only. In two-way with deletes
        /// copied, a folder Drive had before this pass is not made again: it
        /// was deleted there. A folder that cannot be made counts as failed
        /// (and so do the folders under it), and the next pass that walks it
        /// tries again.
        /// </summary>
        private async Task MakeEmptyFoldersAsync(DriveClient client, DriveSyncPair pair, PairState state, SyncRules rules,
            IEnumerable<string> pcFolders, Dictionary<string, string> folders, IReadOnlySet<string> driveFoldersBefore,
            SyncSkips skips, PassTally tally, CancellationToken token)
        {
            if (rules.Mode == SyncMode.DownloadOnly || !rules.IncludeSubfolders) return;

            // The walk was before the moves. A folder renamed or moved on Drive
            // has been renamed on the PC since (so it is no longer there), or its
            // files moved one by one (so Drive still has the folder this pair
            // had there, under its new path): made again, the old tree stood
            // empty in Drive beside the renamed one, for good.
            var nowOnDrive = new HashSet<string>(folders.Values, StringComparer.Ordinal);
            bool MovedOnDrive(string folder) =>
                state.Remembered != null && state.Remembered.TryGetValue(folder, out var was) && nowOnDrive.Contains(was);
            bool StillOnPc(string folder)
            {
                try { return Directory.Exists(LocalPathFor(pair.LocalFolder, folder)); }
                catch { return false; }
            }

            var wanted = new HashSet<string>(SyncNameComparer.Instance);
            foreach (var pcFolder in pcFolders)
                if (pcFolder.Length > 0 && !folders.ContainsKey(pcFolder) && !IsLeftOutFolder(pcFolder, rules, skips) &&
                    !(rules.Mode == SyncMode.TwoWay && rules.CopyDeletes && driveFoldersBefore.Contains(pcFolder)) &&
                    !MovedOnDrive(pcFolder) && StillOnPc(pcFolder))
                    for (var at = pcFolder; at.Length > 0 && !folders.ContainsKey(at) && wanted.Add(at); at = SyncMoves.Parent(at)) { }
            foreach (var level in wanted.GroupBy(f => f.Count(c => c == '/')).OrderBy(g => g.Key))
            {
                if (Stopped(pair)) throw new PairStoppedException();
                var made = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(SyncNameComparer.Instance);
                Exception? cut = null;
                using var gate = new SemaphoreSlim(FolderLanes);

                // A create already sent finishes when the pass is stopped (see
                // Drain): cut off, Drive could have the folder with its id never
                // heard, and the next pass made it again.
                using var drain = new Drain(token);
                await Task.WhenAll(level.Select(async folder =>
                {
                    bool entered = false;
                    try
                    {
                        await gate.WaitAsync(token).ConfigureAwait(false);
                        entered = true;
                        if (Stopped(pair) || token.IsCancellationRequested) return;
                        var parent = SyncMoves.Parent(folder);
                        string? parentId = parent.Length == 0 ? pair.DriveFolderId : folders.GetValueOrDefault(parent);
                        if (parentId == null)
                        {
                            lock (_fastGate)
                            {
                                tally.Failed++;
                                tally.LastError = $"{folder}: the folder above it could not be made";
                            }
                            return;
                        }
                        made[folder] = await Patiently(pair,
                            () => MakeFolderAsync(client, parentId, folder, drain.Token, state.Remembered), client, drain.Token)
                            .ConfigureAwait(false);
                        SyncHistory.Add(pair.Id, "Made folder", folder);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or PairStoppedException)
                    {
                        Interlocked.CompareExchange(ref cut, ex, null);
                    }
                    catch (Exception ex)
                    {
                        lock (_fastGate)
                        {
                            tally.Failed++;
                            tally.LastError = $"{folder}: {ex.Message}";
                        }
                    }
                    finally { if (entered) gate.Release(); }
                })).ConfigureAwait(false);

                // Nothing writes the table while a depth runs; its parents are read
                // from it. What was made is kept even when the pass was stopped.
                foreach (var (folder, id) in made) folders[folder] = id;
                if (cut != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(cut);
            }
            if (Stopped(pair)) throw new PairStoppedException();
        }

        /// <summary>
        /// Free up space (<see cref="DriveSyncPair.DeleteAfterUpload"/>): each PC
        /// file whose copy in Drive is whole is deleted from the PC, so a folder
        /// bigger than the disk can go up through it. Only in upload only with
        /// deletes not copied, where the next pass simply forgets a PC file that
        /// has gone and Drive's copy stays. A file is deleted only when all of
        /// this holds, checked just before, file by file:
        ///   - it has a record of going up, with Drive's id and checksum;
        ///   - it is what went up (same size and time), has settled, and is not
        ///     hidden, system, a link, or anything the pair leaves out (half
        ///     downloads, .tmp, .partial, .!sync, Office locks, the skip list);
        ///   - Drive, asked now, still has that file, not trashed, with that
        ///     size and checksum;
        ///   - its own MD5, read through a handle that lets nobody write it,
        ///     equals Drive's; and it is deleted while that handle is held, so
        ///     nothing can change it between the check and the delete.
        /// Anything else, or any error, leaves the file for a later pass.
        /// </summary>
        private async Task FreeUpAsync(DriveClient client, DriveSyncPair pair, PairState state, SyncRules rules,
            IReadOnlyDictionary<string, SyncFile> local, SyncSkips skips, PassTally tally, CancellationToken token)
        {
            if (!pair.FreesUpSpace || rules.Mode != SyncMode.UploadOnly || rules.CopyDeletes) return;
            int before = tally.FreedUp;
            try { await FreeUpEachAsync(client, pair, state, rules, local, skips, tally, token).ConfigureAwait(false); }
            finally { if (tally.FreedUp != before) SaveState(pair.Id, state); }
        }

        /// <summary>The sweep at the end of a pass: every recorded file still on the PC, including ones that went up before.</summary>
        private async Task FreeUpEachAsync(DriveClient client, DriveSyncPair pair, PairState state, SyncRules rules,
            IReadOnlyDictionary<string, SyncFile> local, SyncSkips skips, PassTally tally, CancellationToken token)
        {
            // A few at a time: each is a question to Drive and a read of the
            // whole file, and one by one, a library of big books freed after
            // the fact held the pass (and every new file behind it) for an hour.
            var freed = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var gate = new SemaphoreSlim(FreeUpLanes);
            try
            {
                await Task.WhenAll(state.Files.Where(f => local.ContainsKey(f.Key)).ToList().Select(async f =>
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (Stopped(pair)) return;
                        if (await TryFreeUpOneAsync(client, pair, rules, skips, f.Key, f.Value, token).ConfigureAwait(false))
                            freed.Enqueue(f.Key);
                    }
                    finally { gate.Release(); }
                })).ConfigureAwait(false);
            }
            finally
            {
                // The records go with their files, whether the pair was stopped or
                // the app closed meanwhile (FreeUpAsync saves them): one that
                // outlived its file, with deletes turned on later, sent Drive's
                // copy to the trash.
                foreach (var path in freed) Forget(state, path);
                tally.FreedUp += freed.Count;
            }
            if (Stopped(pair)) throw new PairStoppedException();
        }

        /// <summary>How many files the end-of-pass sweep checks and frees at once.</summary>
        internal const int FreeUpLanes = 4;

        /// <summary>
        /// A freed file's record goes with it. Kept, the next pass saw a synced
        /// file gone from the PC, and an identical one arriving elsewhere (the
        /// same book in two folders) read as a move: Drive's copy was moved
        /// away from where it belongs.
        /// </summary>
        private static void Forget(PairState state, string path)
        {
            state.Files.Remove(path);
            state.Hashes.Remove(path);
        }

        /// <summary>
        /// One file of free up space, checked as <see cref="FreeUpAsync"/> says
        /// and deleted. True when it is gone. Touches no records, so the lane
        /// can run it beside the uploads.
        /// </summary>
        private async Task<bool> TryFreeUpOneAsync(DriveClient client, DriveSyncPair pair, SyncRules rules, SyncSkips skips,
            string path, SyncBase was, CancellationToken token)
        {
            // Read-only too: the delete would be refused anyway, after Drive was
            // asked and every byte read, and again every pass.
            const FileAttributes notOurs = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint |
                                           FileAttributes.ReadOnly;
            var root = pair.LocalFolder;
            if (string.IsNullOrEmpty(was.RemoteId) || string.IsNullOrEmpty(was.RemoteMd5)) return false;
            if (SyncPlanner.IsSkippedPath(path) || rules.LeavesOut(path) || skips.Covers(path)) return false;
            if (rules.MaxBytes > 0 && was.LocalSize > rules.MaxBytes) return false;
            var localPath = LocalPathFor(root, path);
            if (!IsInside(root, localPath)) return false;
            var wentUp = new SyncFile(was.LocalSize, was.LocalModifiedUtc);
            if (!LocalStillAsScanned(localPath, wentUp) || StillBeingWritten(localPath, wentUp, DateTime.UtcNow, SettleTime)) return false;
            try
            {
                if ((File.GetAttributes(localPath) & notOurs) != 0) return false;

                // Nobody may write it from here until it is gone.
                using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    1 << 20, FileOptions.SequentialScan);
                if (file.Length != was.LocalSize || Truncate(File.GetLastWriteTimeUtc(localPath)) != was.LocalModifiedUtc) return false;

                var onDrive = await Patiently(pair, () => client.FileNow(was.RemoteId, token), client, token).ConfigureAwait(false);
                if (onDrive == null || onDrive.Size != was.LocalSize ||
                    !string.Equals(onDrive.Md5, was.RemoteMd5, StringComparison.OrdinalIgnoreCase)) return false;

                var md5 = Convert.ToHexString(await MD5.HashDataAsync(file, token).ConfigureAwait(false));
                if (!string.Equals(md5, onDrive.Md5, StringComparison.OrdinalIgnoreCase)) return false;

                Kill("free up: checked, not yet deleted " + path);
                File.Delete(localPath);
                SyncHistory.Add(pair.Id, "Deleted from this PC", path, "it is safely in Google Drive");
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // In use, gone, Drive would not say, or a request that timed out
                // (which arrives as a cancellation nobody asked for): tried again
                // next pass.
                return false;
            }
        }

        /// <summary>
        /// Free up space during a pass: each file handed over as its upload is
        /// recorded is checked and deleted on a worker beside the uploads, so
        /// the PC's space comes back file by file rather than when a pass of
        /// thousands ends. The records of the files it deleted are dropped by
        /// the pass once the uploads are done (<see cref="Forget"/>); the
        /// worker never touches them, as the lanes write them meanwhile.
        /// </summary>
        private sealed class FreeUpLane
        {
            private readonly System.Threading.Channels.Channel<(string Path, SyncBase Was)> _queue =
                System.Threading.Channels.Channel.CreateUnbounded<(string, SyncBase)>(
                    new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

            private readonly System.Collections.Concurrent.ConcurrentQueue<string> _freed = new();
            private readonly Task _worker;
            private int _freedCount;

            /// <summary>How many files this lane has cleared off the PC.</summary>
            public int FreedCount => Volatile.Read(ref _freedCount);

            /// <param name="stopped">
            /// True once the pair is paused, removed or edited: what is still
            /// queued is left alone (the next pass's sweep sees to it), as the
            /// settings it was queued under may be gone, free up itself included.
            /// </param>
            public FreeUpLane(Func<string, SyncBase, Task<bool>> free, Func<bool> stopped, CancellationToken token)
            {
                _worker = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var (path, was) in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
                        {
                            if (stopped()) continue;
                            bool gone;
                            try { gone = await free(path, was).ConfigureAwait(false); }
                            catch (Exception) when (!token.IsCancellationRequested) { continue; }
                            if (!gone) continue;
                            _freed.Enqueue(path);
                            Interlocked.Increment(ref _freedCount);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Diag.Note(ex); }
                });
            }

            /// <summary>A file whose upload has just been recorded.</summary>
            public void Sent(string path, SyncBase was) => _queue.Writer.TryWrite((path, was));

            /// <summary>
            /// Drops the records of the files cleared off the PC since last
            /// time. Called by whoever may write the records just then (a lane
            /// under its lock, the careful loop between files), before it saves,
            /// so a pass cut off part way does not leave records of files that
            /// are gone.
            /// </summary>
            public void ForgetFreed(PairState state)
            {
                while (_freed.TryDequeue(out var path)) Forget(state, path);
            }

            /// <summary>No more files: waits for the ones handed over to be checked.</summary>
            public async Task FinishAsync()
            {
                _queue.Writer.TryComplete();
                try { await _worker.ConfigureAwait(false); } catch { }
            }
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
            CancellationToken token, bool saveWhenDone = true)
        {
            // Files already on both sides with nothing remembered about them (a
            // first sync over two copies of one library) are compared by MD5
            // before anything is planned, so a match is adopted, never copied.
            var name = DisplayName(pair);
            var root = pair.LocalFolder;
            await AdoptMatchingDuplicateAsync(root, state, local, remote, shadowed, token).ConfigureAwait(false);
            var unread = new HashSet<string>(SyncNameComparer.Instance);
            var md5 = await CheckWhatIsThereAsync(pair, state, rules, local, remote, generation, token, unread).ConfigureAwait(false);
            var plan = SyncPlanner.Ordered(SyncPlanner.Plan(rules, local, remote, state.Files, md5, skips), local, remote);
            if (scope != null) plan = plan.Where(a => scope.Contains(a.Path)).ToList();
            var asPlanned = plan;

            // Moved or renamed on one side: moved the same way on the other,
            // rather than deleted in one place and copied again in another.
            int movedFiles;
            var notMade = new HashSet<string>(SyncNameComparer.Instance);
            (plan, movedFiles) = await MoveWhatMovedAsync(client, pair, state, rules, plan, local, remote, folders, skips,
                root, generation, notMade, tally, token).ConfigureAwait(false);
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
            // A file whose checksum could not be read is in no action, but its folder
            // must be listed again too, or it is invisible until the daily walk.
            onPlan?.Invoke(plan.Concat(asPlanned.Where(a => notMade.Contains(a.Path)))
                .Concat(unread.Select(p => new SyncAction(SyncActionKind.Upload, p))).ToList());
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
                    ? $"Starting a big sync of {name}: {tally.Transfers:N0} files so far, with more to come"
                    : $"Starting a big sync of {name}: {transfers:N0} files, {Amounts(downBytes, upBytes)}");
            }
            SetProgress(pair.Id, 0, totalBytes);

            void Moved(long bytes)
            {
                long before = doneBytes;
                doneBytes += bytes;
                SetProgress(pair.Id, doneBytes, totalBytes);
                if (big && !tally.Batched && SyncSpace.QuarterPassed(before, doneBytes, totalBytes) is int percent and > 0)
                    _notify("sync.progress", $"{name} is {percent} percent synced");
            }

            int uploaded = 0, downloaded = 0, deleted = 0, failed = 0, deferred = 0;
            var inUse = new List<string>();
            var settleLeft = SettleTime;
            string? lastError = null, spaceProblem = null;
            var conflicts = new List<string>();
            long uploadedThisPass = 0;

            var listings = new Dictionary<string, List<(string Name, bool Folder)>>(StringComparer.Ordinal);
            var freeUp = pair.FreesUpSpace && rules.Mode == SyncMode.UploadOnly && !rules.CopyDeletes
                ? new FreeUpLane((path, was) => TryFreeUpOneAsync(client, pair, rules, skips, path, was, token),
                    () => Stopped(pair) || GenerationOf(pair.Id) != generation, token)
                : null;
            bool finished = false;
            try
            {
                // New small files first, many at once (see UploadNewSmallAsync).
                // One at a time, a folder of a million tiny files was a month
                // of waiting on Google's round trips, not of sending bytes.
                // Creates Google may have and would not say: never sent again
                // this pass (that made a second copy), but counted as failed,
                // so the next pass lists their folders and adopts what landed.
                var doubtful = new Dictionary<string, string>(SyncNameComparer.Instance);
                var sentFast = await UploadNewSmallAsync(client, pair, state, plan, local, remote, folders, root,
                    generation, uploadedThisPass, n => { lock (_fastGate) Moved(n); }, token, doubtful, freeUp).ConfigureAwait(false);
                uploaded += sentFast.Count;
                foreach (var path in sentFast)
                {
                    uploadedThisPass += local[path].Size;
                    Uploads.Add(local[path].Size, DateTime.UtcNow);
                    // The fast lane replaces Drive files in place too, and those
                    // are said as the careful loop says them, not as new uploads.
                    SyncHistory.Add(pair.Id, remote.TryGetValue(path, out var replaced) && replaced.Id != null
                        ? "Updated in Drive" : "Uploaded", path);
                }

                // New song-sized PC files, many at once too (see UploadNewMediumAsync).
                var sentMedium = await UploadNewMediumAsync(client, pair, state, plan, local, remote, folders, root,
                    generation, uploadedThisPass, n => { lock (_fastGate) Moved(n); }, token, doubtful, freeUp).ConfigureAwait(false);
                uploaded += sentMedium.Count;
                foreach (var path in sentMedium)
                {
                    uploadedThisPass += local[path].Size;
                    Uploads.Add(local[path].Size, DateTime.UtcNow);
                    SyncHistory.Add(pair.Id, "Uploaded", path);
                }
                sentFast.UnionWith(sentMedium);
                foreach (var (path, why) in doubtful)
                {
                    if (sentFast.Contains(path)) continue;
                    failed++;
                    lastError = $"{Path.GetFileName(path)}: {why}";
                    SyncHistory.Add(pair.Id, "Could not sync", path, why);
                    sentFast.Add(path);
                }

                // And new Drive files coming down, many at once (see DownloadNewSmallAsync).
                var gotFast = await DownloadNewSmallAsync(client, pair, state, plan, local, remote, root, generation,
                    n => { lock (_fastGate) Moved(n); }, token).ConfigureAwait(false);
                downloaded += gotFast.Count;
                sentFast.UnionWith(gotFast);

                // And PC deletes copied to Drive, many at once (see TrashGoneAsync).
                var trashedFast = await TrashGoneAsync(client, pair, state, plan, remote, folders, root, generation, token)
                    .ConfigureAwait(false);
                deleted += trashedFast.Count;
                sentFast.UnionWith(trashedFast);

                for (int i = 0; i < plan.Count; i++)
                {
                    if (sentFast.Contains(plan[i].Path)) continue;
                    var action = plan[i];
                    token.ThrowIfCancellationRequested();

                    // Pause, Remove and Edit are heard between files, not only
                    // while waiting for Google: a pass of hundreds of gigabytes
                    // used to carry on to the end under settings that had gone.
                    if (Stopped(pair) || GenerationOf(pair.Id) != generation) throw new PairStoppedException();
                    freeUp?.ForgetFreed(state);
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
                        if (age >= TimeSpan.Zero && age < SettleTime)
                        {
                            deferred++;
                            if (SettleTime - age < settleLeft) settleLeft = SettleTime - age;
                        }
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
                    if (action.Kind is SyncActionKind.Upload or SyncActionKind.KeepBothLocalWins && _drive.QuotaLimit > 0 && !OnSharedDrive(pair))
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
                            // Beside the PC's file, in the folder as the PC spells
                            // it (LocalPathFor), and under a name nothing on disk
                            // has: built from the plan's spelling, a folder whose
                            // accent was written the other way was not found, and
                            // the conflict failed on every pass.
                            var aside = SyncPlanner.ConflictName(path, DateTime.Now, p => remote.ContainsKey(p) || local.ContainsKey(p) ||
                                OnDisk(AsidePathFor(localPath, p)));
                            var asidePath = AsidePathFor(localPath, aside);
                            Mine(localPath);
                            Mine(asidePath);
                            File.Move(localPath, asidePath);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            if (InUseElsewhere(ex, localPath))
                            {
                                inUse.Add(Path.GetFileName(path));
                                continue;
                            }
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
                                        // Moved out of this folder in Drive since the records
                                        // (the feed only marks where it went): the PC's edit is
                                        // not written into a file that lives elsewhere now. The
                                        // folder is listed again next pass, and the file goes
                                        // up there as a new one.
                                        var recordedFolder = SyncMoves.Parent(path) is { Length: > 0 } up
                                            ? folders.TryGetValue(up, out var upId) ? upId : null
                                            : pair.DriveFolderId;
                                        if (now?.Parent != null && recordedFolder != null && now.Parent != recordedFolder)
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
                                    Uploads.Add(local[path].Size, DateTime.UtcNow);
                                    uploaded++;
                                    SyncHistory.Add(pair.Id, replaces != null ? "Updated in Drive" : "Uploaded", path);
                                    freeUp?.Sent(path, sent);
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
                                    // And the PC file is still gone: one put back
                                    // since the scan is not a delete.
                                    if (!LocalStillAsScanned(localPath, null))
                                    {
                                        deferred++;
                                        break;
                                    }
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
                                    // Moved out of this folder in Drive since the records
                                    // (the feed only marks where it went): not this pair's
                                    // to trash where it lives now.
                                    var recordedFolder = SyncMoves.Parent(path) is { Length: > 0 } up
                                        ? folders.TryGetValue(up, out var upId) ? upId : null
                                        : pair.DriveFolderId;
                                    if (now.Parent != null && recordedFolder != null && now.Parent != recordedFolder)
                                    {
                                        state.Files.Remove(path);
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

                                    // Deleted for good, never through the Recycle Bin
                                    // (5.2.1, Conner). Never a window: the monitor runs
                                    // with nobody watching. A file that could not be
                                    // deleted is kept and said, and stays remembered so
                                    // it is not sent back up as new.
                                    Mine(localPath);
                                    var kept = DeleteLocalFile(localPath);
                                    if (kept != null)
                                    {
                                        failed++;
                                        lastError = $"{Path.GetFileName(path)} was kept: {kept}";
                                        SyncHistory.Add(pair.Id, "Kept on the PC", path, kept);
                                        break;
                                    }
                                    Kill("deleted " + path);
                                    state.Files.Remove(path);
                                    deleted++;
                                    SyncHistory.Add(pair.Id, "Deleted from the PC", path, "deleted in Drive");
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
                                    Uploads.Add(local[path].Size, DateTime.UtcNow);
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

                            // A PC file another program has open (a song playing, a
                            // book being read) is not a failure. It counted as one on
                            // every pass, and was said as "could not sync" with
                            // Windows' wording, until the program let go of it. It
                            // waits for the pair's next look instead (not a few
                            // seconds' time: a file can stay open all day).
                            if (InUseElsewhere(ex, localPath))
                            {
                                inUse.Add(Path.GetFileName(path));
                                break;
                            }

                            failed++;
                            lastError = $"{Path.GetFileName(path)}: {ex.Message}";
                            SyncHistory.Add(pair.Id, "Could not sync", path, ex.Message);
                            break;
                        }
                    }
                }
                finished = true;
            }
            finally
            {
                // The files free up cleared off the PC meanwhile lose their records now, with no lane writing.
                if (freeUp != null)
                {
                    await freeUp.FinishAsync().ConfigureAwait(false);
                    freeUp.ForgetFreed(state);
                    tally.FreedUp += freeUp.FreedCount;
                }

                // Everything done so far is remembered even if the connection
                // dropped part way. What was not done is found again next time.
                // A caller that saves at its own end (a small pair: FinishPass,
                // or its catch when anything after this throws) is not written
                // twice: each pass wrote every record twice within moments,
                // tens of megabytes a minute for a quiet code folder.
                // Files cleared off the PC are written at once all the same.
                if (saveWhenDone || !finished || (freeUp?.FreedCount ?? 0) > 0) SaveState(pair.Id, state);

                tally.Uploaded += uploaded;
                tally.Downloaded += downloaded;
                tally.Deleted += deleted;
                tally.Failed += failed;
                tally.Deferred += deferred;
                tally.InUse.AddRange(inUse);
                if (lastError != null) tally.LastError = lastError;
                tally.SpaceProblem ??= spaceProblem;
                tally.Conflicts.AddRange(conflicts);
                Report(pair.Id, s => s with
                {
                    Uploaded = tally.Uploaded, Downloaded = tally.Downloaded, Deleted = tally.Deleted, Failed = tally.Failed,
                    Moved = tally.Moved,
                }, now: true);

                // Something left for later (a file still being written, an edit
                // made after the scan) is looked at again as soon as it has
                // settled. Left to the minute's check, a file saved a second
                // before a pass waited over a minute to go up.
                if (deferred > 0) SoonAfter(pair.Id, settleLeft + SettleMargin);
            }

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

        /// <summary>How many Drive folders of one depth are made at once.</summary>
        internal const int FolderLanes = 8;

        /// <summary>
        /// The Drive folders for <paramref name="wanted"/> and every folder above
        /// them, a depth at a time: the folders of one depth are made side by
        /// side, <see cref="FolderLanes"/> at once, their parents all being there
        /// by then, and recorded in <paramref name="folders"/> once that depth is
        /// done (nothing writes it while they run). Every folder above is in the
        /// list, so two folders never both make a missing parent. One at a time,
        /// a first sync over the 3,400 folders Resilio Sync had laid out spent
        /// most of an hour on folders before a single song moved.
        /// </summary>
        private async Task MakeFoldersAsync(DriveClient client, DriveSyncPair pair, PairState state,
            Dictionary<string, string> folders, IEnumerable<string> wanted, int generation,
            CancellationToken drainToken, CancellationToken token)
        {
            var all = new HashSet<string>(SyncNameComparer.Instance);
            foreach (var folder in wanted)
                for (var at = folder; at.Length > 0 && all.Add(at); at = SyncMoves.Parent(at)) { }

            foreach (var level in all.Where(f => !folders.ContainsKey(f))
                         .GroupBy(f => f.Count(c => c == '/')).OrderBy(g => g.Key))
            {
                token.ThrowIfCancellationRequested();
                if (Stopped(pair) || GenerationOf(pair.Id) != generation) throw new PairStoppedException();

                var made = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(SyncNameComparer.Instance);
                Exception? failure = null;
                using var gate = new SemaphoreSlim(FolderLanes);
                await Task.WhenAll(level.Select(async folder =>
                {
                    bool entered = false;
                    try
                    {
                        await gate.WaitAsync(token).ConfigureAwait(false);
                        entered = true;
                        if (Volatile.Read(ref failure) != null) return;
                        var parent = SyncMoves.Parent(folder);
                        var parentId = parent.Length == 0 ? pair.DriveFolderId : folders[parent];
                        made[folder] = await Patiently(pair,
                            () => MakeFolderAsync(client, parentId, folder, drainToken, state.Remembered), client, drainToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
                    finally { if (entered) gate.Release(); }
                })).ConfigureAwait(false);

                foreach (var (folder, id) in made) folders[folder] = id;
                if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
            }
        }

        /// <summary>
        /// MakeFoldersAsync for a fast lane: a refusal (no right to add here, a parent
        /// deleted on the web) returns false, so the careful loop fails only those
        /// files and the rest of the pass, downloads included, still runs.
        /// </summary>
        private async Task<bool> TryMakeFoldersAsync(DriveClient client, DriveSyncPair pair, PairState state,
            Dictionary<string, string> folders, IEnumerable<string> wanted, int generation,
            CancellationToken drainToken, CancellationToken token)
        {
            try
            {
                await MakeFoldersAsync(client, pair, state, folders, wanted, generation, drainToken, token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (!token.IsCancellationRequested &&
                ex is not (PairStoppedException or OperationCanceledException or OfflineException or GaveUpException))
            {
                GoogleDrive.Trace($"sync {DisplayName(pair)}: fast lane could not make folders ({ex.Message}); careful loop takes over");
                return false;
            }
        }

        /// <summary>
        /// One Drive folder in a parent that is there. A folder this pair had at
        /// that path before the pass is asked about by its id first, as
        /// <see cref="EnsureFolderAsync"/> does, so a folder made a moment ago is
        /// not made twice.
        /// </summary>
        private static async Task<string> MakeFolderAsync(DriveClient client, string parentId, string relativeFolder,
            CancellationToken token, IReadOnlyDictionary<string, string>? remembered)
        {
            var name = SyncMoves.Leaf(relativeFolder);
            if (remembered != null && remembered.TryGetValue(relativeFolder, out var before) &&
                await client.PlaceNow(before, token).ConfigureAwait(false) is { } place &&
                place.Parents.Contains(parentId, StringComparer.Ordinal) && SyncNameComparer.Instance.Equals(place.Name, name))
                return before;
            return await client.CreateFolder(name, parentId, token).ConfigureAwait(false);
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

        /// <summary>
        /// Below this many small files the fast lane is not worth it. It was
        /// 64 until 7.0.0: a build that changed forty files then went up one
        /// file a second, and the edit made after it waited behind them
        /// (Conner: "don't sync the moment they should").
        /// </summary>
        internal const int FastLaneMinimum = 8;

        /// <summary>Below this many deletes to copy to Drive they go one at a time.</summary>
        internal const int FastTrashMinimum = 4;

        /// <summary>
        /// Copies the plan's PC deletes to Drive many at once, and returns the
        /// paths done. One at a time each was two round trips, half a second:
        /// clearing a build folder of 400 files held every other change of
        /// the pair back for four minutes (7.0.0). The checks are the careful
        /// loop's own, in its order; anything that is not a plain "still as
        /// scanned, trashed" (changed in Drive since, put back on the PC, a
        /// failure) is left for that loop, which decides and says why.
        /// Records are changed only under _fastGate.
        /// </summary>
        private async Task<HashSet<string>> TrashGoneAsync(
            DriveClient client, DriveSyncPair pair, PairState state, List<SyncAction> plan,
            Dictionary<string, SyncFile> remote, Dictionary<string, string> folders,
            string root, int generation, CancellationToken token)
        {
            var gone = new HashSet<string>(SyncNameComparer.Instance);
            var batch = new List<string>();
            foreach (var action in plan)
            {
                if (action.Kind != SyncActionKind.DeleteRemote) continue;
                if (!remote.TryGetValue(action.Path, out var file) || file.Id == null) continue;
                batch.Add(action.Path);
            }
            if (batch.Count < FastTrashMinimum) return gone;

            int done = 0;
            int goneBefore = StatusOf(pair.Id).Deleted;
            var lanes = new AdaptiveLanes(FastLanes, FastLanesMin, FastLanesMax);
            await Parallel.ForEachAsync(batch,
                new ParallelOptions { MaxDegreeOfParallelism = FastLanesMax, CancellationToken = token },
                async (path, ct) =>
                {
                    await lanes.EnterAsync(ct).ConfigureAwait(false);
                    var clock = Stopwatch.StartNew();
                    bool trashed = false;
                    try { trashed = await One(path, ct).ConfigureAwait(false); }
                    finally { lanes.Leave(trashed && clock.ElapsedMilliseconds < SlowAnswerMilliseconds); }
                }).ConfigureAwait(false);
            return gone;

            async Task<bool> One(string path, CancellationToken ct)
            {
                if (Stopped(pair) || GenerationOf(pair.Id) != generation) return false;
                var scanned = remote[path];
                try
                {
                    if (!LocalStillAsScanned(LocalPathFor(root, path), null)) return false;
                    var now = await client.FileNow(scanned.Id!, ct).ConfigureAwait(false);
                    if (now == null || !SameAsScanned(now, scanned)) return false;
                    string? recordedFolder;
                    if (SyncMoves.Parent(path) is { Length: > 0 } up)
                        lock (folders) recordedFolder = folders.TryGetValue(up, out var upId) ? upId : null;
                    else recordedFolder = pair.DriveFolderId;
                    if (now.Parent != null && recordedFolder != null && now.Parent != recordedFolder) return false;
                    await client.Trash(scanned.Id!, ct).ConfigureAwait(false);
                }
                catch (Exception) when (!ct.IsCancellationRequested) { return false; }

                lock (_fastGate)
                {
                    state.Files.Remove(path);
                    gone.Add(path);
                    done++;
                    SaveSoon(pair.Id, state);
                    int count = done;
                    Report(pair.Id, s => s with { Deleted = goneBefore + count });
                }
                SyncHistory.Add(pair.Id, "Moved to the Drive trash", path, "deleted on the PC");
                Kill("trashed " + path);
                return true;
            }
        }

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
            string root, int generation, long alreadyUp, Action<long> moved, CancellationToken token,
            Dictionary<string, string> doubtful, FreeUpLane? freeUp = null)
        {
            var sent = new HashSet<string>(SyncNameComparer.Instance);
            long room = _drive.QuotaLimit > 0 && !OnSharedDrive(pair) ? _drive.QuotaLimit - _drive.QuotaUsed - alreadyUp : long.MaxValue;
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

            // The folders first, a depth at a time (see MakeFoldersAsync). A
            // replacement's folder is already known from the listing, so this
            // is a lookup. A create already sent is let finish if the pass
            // stops (see Drain).
            using var drain = new Drain(token);
            if (!await TryMakeFoldersAsync(client, pair, state, folders, batch.Select(SyncMoves.Parent), generation, drain.Token, token)
                    .ConfigureAwait(false))
                return sent;

            // Every parent is in `folders` now, so the lanes only read it.
            int done = 0;
            int upBefore = StatusOf(pair.Id).Uploaded;   // earlier batches and lanes count too
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
                            // Moved out of the folder in Drive: the careful loop defers it.
                            if (now.Parent != null && now.Parent != parent) return false;
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
                    catch (Exception ex) when (!ct.IsCancellationRequested && replaces == null &&
                                               DriveClient.UnknownIfLandedException.In(ex))
                    {
                        // Google may have it and will not say: never sent again
                        // this pass (the careful loop would, with no lookup, and
                        // make a second copy). The next pass lists its folder and
                        // adopts it if it landed.
                        lock (_fastGate) doubtful[path] = DriveClient.UnknownIfLandedException.Find(ex)!.Message;
                        return false;
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
                        freeUp?.Sent(path, state.Files[path]);
                        freeUp?.ForgetFreed(state);
                        sent.Add(path);
                        done++;
                        SaveSoon(pair.Id, state);
                        int count = done;
                        Report(pair.Id, s => s with { Uploaded = upBefore + count, StepDone = count });
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
            string root, int generation, long alreadyUp, Action<long> moved, CancellationToken token,
            Dictionary<string, string> doubtful, FreeUpLane? freeUp = null)
        {
            var sent = new HashSet<string>(SyncNameComparer.Instance);
            long room = _drive.QuotaLimit > 0 && !OnSharedDrive(pair) ? _drive.QuotaLimit - _drive.QuotaUsed - alreadyUp : long.MaxValue;
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
                if (!await TryMakeFoldersAsync(client, pair, state, folders, batch.Select(SyncMoves.Parent), generation, drain.Token, token)
                        .ConfigureAwait(false))
                    return sent;

            int done = 0;
            int upBefore = StatusOf(pair.Id).Uploaded;
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
                // One-shot uploads finish once started (see Drain), as on the careful loop.
                using var drainOne = new Drain(ct);
                var upToken = local[path].Size <= DriveClient.SmallUploadBytes ? drainOne.Token : ct;
                try
                {
                    up = await UploadAsync(client, folders, pair, state, path, LocalPathFor(root, path), local[path],
                        null, null, moved, upToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && DriveClient.UnknownIfLandedException.In(ex))
                {
                    // Not sent again this pass (see UploadNewSmallAsync).
                    lock (_fastGate) doubtful[path] = DriveClient.UnknownIfLandedException.Find(ex)!.Message;
                    return false;
                }
                catch (Exception) when (!ct.IsCancellationRequested) { return false; }
                lock (_fastGate)
                {
                    state.Files[path] = up;
                    freeUp?.Sent(path, up);
                    freeUp?.ForgetFreed(state);
                    sent.Add(path);
                    done++;
                    SaveSoon(pair.Id, state);
                    int count = done;
                    Report(pair.Id, s => s with { Uploaded = upBefore + count, StepDone = count });
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
            int downBefore = StatusOf(pair.Id).Downloaded;
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
                    Report(pair.Id, s => s with { Downloaded = downBefore + count, StepDone = count });
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
                    // the old one be there after all, it is trashed below. A new
                    // file is a create, sent once like any other (SendOnceAsync):
                    // sent plainly, an answer lost on the way back could be a
                    // second copy, or fail the file when Drive would not say.
                    lock (_fastGate) state.Uploads.Remove(path);
                    sent = await SendOnceAsync(pair, client, () => Send(null, null), local.Size, token).ConfigureAwait(false);
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
                    // Only while it still holds what the scan saw, asked just
                    // before (the upload can have taken an hour): an edit made in
                    // Drive meanwhile is not thrown away for one made on the PC
                    // before it. Edited, it is let go of, as TrashPendingAsync does.
                    var now = await client.FileNow(replaces, token).ConfigureAwait(false);
                    if (now != null && old is { } scanned && SameAsScanned(now, scanned))
                        await client.Trash(replaces, token).ConfigureAwait(false);
                    state.PendingTrash.Remove(replaces);
                }
                catch (Exception ex) when (!token.IsCancellationRequested && NotFound(ex))
                {
                    state.PendingTrash.Remove(replaces);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    // Kept in PendingTrash for the next pass.
                    Diag.Note(ex);
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

            // Over the PC's file only when there was one to replace. With none,
            // a file saved there since the check above is never overwritten:
            // the move fails, and the download waits whole in its ".partial".
            Mine(localPath);
            try { File.Move(partial, localPath, overwrite: scanned != null); }
            catch (IOException) when (scanned == null && OnDisk(localPath)) { return null; }
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
                    Diag.Note(ex);
                }
            }
        }
    }
}
