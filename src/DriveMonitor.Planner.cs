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
    // The folder monitor, planning: listing both sides (the PC walk and its
    // partial form, Drive whole or through the changes feed), what is already
    // there, moves and renames, and the preview, from which SyncPlanner decides
    // what to upload, download and delete. DriveMonitor.cs is the core.
    internal sealed partial class DriveMonitor
    {
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
            CancellationToken token, HashSet<string>? unread = null)
        {
            var wanted = SyncPlanner.NeedingHash(rules, local, remote, state.Files);
            var (known, toHash) = SyncHashes.Split(wanted, local, state.Hashes);
            if (toHash.Count == 0) return known;

            var name = DisplayName(pair);
            long bytes = toHash.Sum(p => local[p].Size);
            bool big = SyncSpace.IsBig(bytes, toHash.Count);
            if (big) _notify("sync.checking", $"Checking which of {toHash.Count:N0} files in {name} are already in Google Drive");

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
                try { Changed?.Invoke(); } catch (Exception ex) { Diag.Note(ex); }
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
                                _notify("sync.checking", $"Checked {percent} percent of {name}");
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // Unreadable now (open in another program): not adopted,
                            // and not copied this pass either, since its hash is unknown.
                            // Its folder is looked at again next pass (see the caller).
                            lock (gate) { done++; unread?.Add(path); }
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

            if (big) _notify("sync.checking", $"Finished checking {name}");
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
            SyncSkips skips, string root, int generation, HashSet<string> notMade, PassTally tally, CancellationToken token)
        {
            // Free up space: files leave the PC as they go up, so a recorded file
            // gone from the PC is that and nothing more. A record that outlived
            // its file (a pass cut off before it was dropped) and the same book
            // arriving in another folder were taken for a move, and Drive's copy
            // was moved away from where it belongs.
            if (pair.FreesUpSpace) return (plan, 0);
            var found = SyncMoves.Find(rules, plan, local, remote, state.Files);
            if (found.Count == 0) return (plan, 0);

            // A file moved on the PC is only taken for the file Drive holds when
            // its MD5 agrees. Read once, and kept like any other.
            var checkedMoves = new List<SyncMove>();

            // A move whose new file cannot be read just now (open in another
            // program) is not known to be a move or not. Left to the plan, its
            // halves were a delete and an upload: with deletes on, Drive's copy
            // went to the trash at once and the file went up again later as a
            // new one. Both halves wait for a later pass instead, as a move tried
            // and not made does.
            var unread = new List<SyncMove>();
            foreach (var move in found)
            {
                token.ThrowIfCancellationRequested();
                // Drive's checksum as recorded, or as listed now when the record
                // has none (an older record): taken on size and time alone, two
                // different files alike in both were paired, and the new one's
                // contents never went up.
                var driveMd5 = move.Was.RemoteMd5 ?? (move.OnDrive && remote.TryGetValue(move.From, out var listed) ? listed.Md5 : null);
                if (!move.OnDrive || driveMd5 == null) { checkedMoves.Add(move); continue; }
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
                    catch (Exception) when (!token.IsCancellationRequested) { unread.Add(move); continue; }
                }
                if (string.Equals(md5, driveMd5, StringComparison.OrdinalIgnoreCase)) checkedMoves.Add(move);
            }
            if (unread.Count > 0)
            {
                var waiting = new HashSet<string>(SyncNameComparer.Instance);
                foreach (var m in unread) { waiting.Add(m.From); waiting.Add(m.To); }
                plan = plan.Where(a => !waiting.Contains(a.Path)).ToList();
                notMade.UnionWith(waiting);
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
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Refused or failed: said, counted and in History, not retried in silence.
                    if (ex is not PairStoppedException)
                    {
                        lock (_fastGate)
                        {
                            tally.Failed++;
                            tally.LastError = $"{move.To}: {ex.Message}";
                        }
                        SyncHistory.Add(pair.Id, "Could not sync", move.To, ex.Message);
                    }
                    return false;
                }
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

        /// <summary>
        /// Where the PC's copy of a conflict steps aside: the conflict name's own
        /// file name (<paramref name="asideRelative"/>), in the folder the PC's
        /// file is really in (<paramref name="localPath"/>, from LocalPathFor).
        /// </summary>
        internal static string AsidePathFor(string localPath, string asideRelative) =>
            Path.Combine(Path.GetDirectoryName(localPath) ?? "", SyncMoves.Leaf(asideRelative));

        private static bool OnDisk(string path)
        {
            // Never throws (Exists answers false on any error), so the name
            // search always ends; a clash it misses fails the move, not the file.
            try { return File.Exists(path) || Directory.Exists(path); }
            catch { return false; }
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
                catch (Exception ex) when (!token.IsCancellationRequested && ex is not PairStoppedException)
                {
                    // Anything at all: listed whole instead.
                    Diag.Note(ex);
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
                lines.Add($"{Files(p.LeftOut)} will be skipped, because {(p.LeftOut == 1 ? "it is a system file" : "they are system files")}, " +
                          "or named in a way Windows cannot use.");
            return string.Join(Environment.NewLine, lines);
        }

        // ---------------- scanning ----------------

        /// <summary>
        /// Every file under the PC folder that takes part, by relative path.
        ///
        /// What is there and left out, hidden or system files, everything in
        /// a hidden, system or unreadable folder, a file whose details
        /// could not be read, goes in <paramref name="skipped"/>. Simply not
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
        /// <param name="folders">When given, every subfolder walked into, so ones with no files can be made in Drive too.</param>
        internal static Dictionary<string, SyncFile> ScanLocal(string root, bool subfolders, SyncSkips? skipped = null,
            List<string>? clashing = null, Action<int>? found = null, Action<string>? linked = null, ISet<string>? folders = null)
        {
            var files = new Dictionary<string, SyncFile>(SyncNameComparer.Instance);
            ScanLocalTree(new DirectoryInfo(root), "", subfolders, files, skipped, clashing, found, linked, folders);
            return files;
        }

        /// <summary>A folder and everything under it, into <paramref name="files"/>, as <see cref="ScanLocal"/> does the whole pair.</summary>
        private static void ScanLocalTree(DirectoryInfo top, string topRelative, bool subfolders, Dictionary<string, SyncFile> files,
            SyncSkips? skipped, List<string>? clashing, Action<int>? found, Action<string>? linked, ISet<string>? folders = null,
            HashSet<string>? linkTargets = null)
        {
            // Where this walk's links have led, so none is walked twice or round in a loop.
            linkTargets ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<(DirectoryInfo Folder, string Relative)>();
            pending.Push((top, topRelative));
            if (topRelative.Length > 0) folders?.Add(topRelative);
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
                    ScanLocalTreeParallel(pending, subfolders, files, skipped, clashing, found, linked, folders, linkTargets);
                    return;
                }
                var (folder, relative) = pending.Pop();
                under.Clear();
                ScanLocalFolder(folder, relative, subfolders, files, skipped, clashing, under, found, linked, linkTargets);
                foreach (var child in under)
                {
                    pending.Push(child);
                    folders?.Add(child.Relative);
                }
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
            Action<int>? found, Action<string>? linked, ISet<string>? folders = null, HashSet<string>? linkTargets = null)
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
                            ScanLocalFolder(next.Folder, next.Relative, subfolders, mine, skips, clashes, under, null, linkedSafe, linkTargets);
                            if (under.Count > 0)
                            {
                                pending.PushRange(under.ToArray());
                                if (folders != null) lock (gate) foreach (var (_, sub) in under) folders.Add(sub);
                            }
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
                // Background work: a whole walk of a big pair (24,000 folders)
                // keeps up to eight threads busy, and must give way to whatever
                // the person is doing. With the PC otherwise idle it runs just
                // as fast.
                threads[i] = new Thread(() => Work(me))
                {
                    IsBackground = true, Name = "PC folder scan", Priority = ThreadPriority.BelowNormal,
                };
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
            IReadOnlyCollection<string> clashingBefore, Action<string>? linked = null, ISet<string>? folders = null)
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

            var linkTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // where links led (see WalkIntoLink)
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
            // of the folders above it hidden, system or unreadable. Looked at as
            // the PC spells it (LocalPathFor): a folder the changes feed named
            // (Drive's spelling, "Café" with the accent as one character, where
            // the PC's has two) read as gone, its files as deleted, and every
            // pass deferred them again.
            string? Blocked(string folder, out bool gone)
            {
                gone = false;
                if (folder.Length == 0) return null;
                var parts = folder.Split('/');
                var onDisk = parts;
                try
                {
                    var resolved = Path.GetRelativePath(root, LocalPathFor(root, folder)).Split(Path.DirectorySeparatorChar);
                    if (resolved.Length == parts.Length) onDisk = resolved;
                }
                catch { }
                var path = root;
                for (int i = 0; i < parts.Length; i++)
                {
                    path = Path.Combine(path, onDisk[i]);
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); }
                    catch (FileNotFoundException) { gone = true; return null; }
                    catch (DirectoryNotFoundException) { gone = true; return null; }
                    catch { return string.Join('/', parts[..(i + 1)]); }
                    if ((attributes & FileAttributes.Directory) == 0) { gone = true; return null; }
                    if ((attributes & LeftOutAttributes) != 0 || IsResilioFolder(parts[i])) return string.Join('/', parts[..(i + 1)]);
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
                ScanLocalFolder(dir, folder, subfolders, fresh, skipped, clashing, subs, null, linked, linkTargets);
                if (!subfolders) continue;
                var here = new HashSet<string>(comparer);
                foreach (var (_, sub) in subs)
                {
                    here.Add(sub);
                    folders?.Add(sub);
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
                ScanLocalTree(dir, tree, subfolders, fresh, skipped, clashing, null, linked, folders, linkTargets);
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
        /// Whether a walk goes on into a junction or symbolic link found in
        /// <paramref name="folder"/> (at <paramref name="under"/> inside the
        /// pair). Not when it leads to the pair's own folder or anything inside
        /// it (that tree is walked already), to the folder being read or a
        /// folder above it (a loop), to a folder another link of this walk
        /// already led to or into, or somewhere Windows cannot say.
        /// <paramref name="visited"/> is shared by a walk's workers, locked on itself.
        /// </summary>
        private static bool WalkIntoLink(DirectoryInfo link, DirectoryInfo folder, string under, HashSet<string>? visited)
        {
            string target;
            try
            {
                var resolved = link.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved == null) return false;
                target = Plain(resolved.FullName);
            }
            catch { return false; }

            try
            {
                // The pair's folder: as the walk spells it, and wherever it
                // really is when it is a link itself.
                var root = folder;
                if (under.Length > 0)
                    foreach (var _ in under.Split('/'))
                        root = root.Parent ?? root;
                var roots = new List<string> { Plain(root.FullName) };
                try { if (root.ResolveLinkTarget(returnFinalTarget: true) is { } real) roots.Add(Plain(real.FullName)); } catch { }

                foreach (var r in roots)
                    if (Within(target, r)) return false;              // the pair's tree again
                if (Within(Plain(folder.FullName), target)) return false; // a folder above this one: a loop

                if (visited != null)
                    lock (visited)
                    {
                        foreach (var seen in visited)
                            if (Within(target, seen)) return false;     // that tree is walked already
                        visited.Add(target);
                    }
                return true;
            }
            catch { return false; }

            static string Plain(string path)
            {
                if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) path = @"\\" + path[8..];
                else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
                return path.TrimEnd('\\', '/');
            }

            // Whether path is inside, or is, folder.
            static bool Within(string path, string folder) =>
                path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                (path.Length > folder.Length && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
                 (path[folder.Length] == '\\' || path[folder.Length] == '/'));
        }

        /// <summary>
        /// One PC folder of <see cref="ScanLocal"/>: its files into
        /// <paramref name="files"/>, the subfolders to walk into
        /// <paramref name="subfoldersOut"/>, and whatever is left out into
        /// <paramref name="skipped"/>. The pair's own folder unreadable throws
        /// (a failed pass, never an empty one); a subfolder is left out whole.
        /// </summary>
        internal static void ScanLocalFolder(DirectoryInfo folder, string under, bool subfolders,
            Dictionary<string, SyncFile> files, SyncSkips? skipped, List<string>? clashing,
            List<(DirectoryInfo Folder, string Relative)> subfoldersOut, Action<int>? found = null, Action<string>? linked = null,
            HashSet<string>? linkTargets = null)
        {
            const FileAttributes leftOut = LeftOutAttributes;

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
                    if ((attributes & leftOut) != 0 || IsResilioFolder(entry.Name))
                    {
                        skipped?.Folder(relative);
                        continue;
                    }
                    // A junction or symbolic link: what changes behind it is on
                    // another folder's account, and maybe another volume's journal.
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        bool isLink;
                        try { isLink = entry.LinkTarget != null; } catch { isLink = true; }
                        if (isLink)
                        {
                            linked?.Invoke(relative);
                            // A link back to the pair's folder or above it went
                            // round and round (Music/All/All/...), a copy of the
                            // whole library each time, up to 63 deep. Left out
                            // instead, so nothing under it reads as deleted.
                            if (!WalkIntoLink((DirectoryInfo)entry, folder, under, linkTargets))
                            {
                                skipped?.Folder(relative);
                                continue;
                            }
                        }
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
        /// or a slash makes a folder, ".." climbs out) or are silently changed (a
        /// trailing dot or space is dropped), and then the PC's file never
        /// matches Drive's under one name: it fails on every pass, or worse, the
        /// PC's copy reads as Drive's having been deleted and the original is
        /// trashed. So such a name is left out, with its whole folder when it is
        /// a folder, and listed in <paramref name="unusable"/> to be said.
        /// </summary>
        /// <summary>
        /// A name Windows cannot hold beside another in one folder, a file and
        /// a folder called "x" and "X", or two names that differ only in
        /// capitals or in how an accent is written, is left out with all its
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
    }
}
