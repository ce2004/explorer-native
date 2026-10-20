using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Keep on Drive only and Put back on PC (Conner, 4.1.3): one folder inside
    /// a two-way pair lives in Google Drive alone while the rest of the pair
    /// goes on syncing both ways.
    ///
    /// Keep on Drive only checks every file of the PC's folder against Drive's
    /// copy by MD5 before anything happens, then puts the folder on the pair's
    /// list (so the sync leaves it alone, see SyncPlanner), drops the pair's
    /// records of it (so nothing can later be taken for deleted), and only then
    /// clears it off the PC, file by file, each still as it was checked.
    /// Put back on PC takes it off the list, and the next pass downloads it:
    /// with no records, a file only Drive has is a new one.
    /// </summary>
    internal sealed partial class DriveMonitor
    {
        /// <summary>How Keep on Drive only or Put back on PC went, in words to say.</summary>
        internal sealed record DriveOnlyOutcome(bool Done, string Message);

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _recordsLocks = new();

        /// <summary>Held by a pair's pass, and by whatever changes its records between passes.</summary>
        private SemaphoreSlim RecordsLock(string pairId) => _recordsLocks.GetOrAdd(pairId, _ => new SemaphoreSlim(1, 1));

        /// <summary>
        /// Drops every record of a path <paramref name="under"/> picks out: the
        /// whole-tree tables, and a large pair's folder files. True when
        /// anything went. The caller saves.
        /// </summary>
        private static bool DropRecords(string pairId, PairState state, Func<string, bool> under)
        {
            bool changed = false;
            void Drop<T>(Dictionary<string, T> table)
            {
                if (table.Count == 0) return;
                var gone = table.Keys.Where(under).ToList();
                foreach (var key in gone) table.Remove(key);
                if (gone.Count > 0) changed = true;
            }
            Drop(state.Files);
            Drop(state.Hashes);
            Drop(state.Partials);
            Drop(state.Uploads);
            if (state.EmptyLater != null && state.EmptyLater.RemoveAll(f => under(f)) > 0) changed = true;

            if (state.Sharded)
            {
                // A folder's file holds the files directly in it, so the folder
                // itself and every one under it go whole.
                var store = new ShardStore(pairId);
                foreach (var folder in state.ShardFolders.Where(f => f.Length > 0 && under(f)).ToList())
                {
                    store.Delete(folder);
                    changed = true;
                }
                state.ShardFolders.RemoveAll(f => f.Length > 0 && under(f));
                state.RecordFolders.RemoveAll(f => f.Length > 0 && under(f));
            }
            return changed;
        }

        /// <summary>
        /// Drops a pair's records of one folder, between its passes. False when
        /// its saved state could not be read just now, so nothing was changed.
        /// </summary>
        internal async Task<bool> ForgetFolderAsync(string pairId, string relative, CancellationToken token,
            bool listAgain = false, Action? thenWhileHeld = null)
        {
            relative = DriveOnlyFolders.Normal(relative);
            if (relative.Length == 0) return false;
            var only = new List<string> { relative };

            // A pass under way stops at its next file once the pair is edited;
            // one waiting on Google is woken to notice.
            CutWait();
            var records = RecordsLock(pairId);
            await records.WaitAsync(token).ConfigureAwait(false);
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    var (state, how) = ReadState(pairId);
                    if (how == StateRead.Missing)
                    {
                        thenWhileHeld?.Invoke();
                        return true;
                    }
                    if (how == StateRead.Fine)
                    {
                        bool changed = DropRecords(pairId, state, path => DriveOnlyFolders.Covers(only, path));
                        // Put back: nothing in the folder changed in Drive, so the
                        // changes feed would never send the pass to it. Drive is
                        // listed whole once instead.
                        if (listAgain && state.ChangesToken != null)
                        {
                            ForgetFeed(state);
                            changed = true;
                        }
                        if (changed) SaveState(pairId, state);
                        // Settings changed while no pass can start: the next one
                        // reads them after taking the lock (see SyncOneAsync).
                        thenWhileHeld?.Invoke();
                        return true;
                    }
                    if (how == StateRead.Damaged || attempt >= 5) return false;
                    await Task.Delay(200, token).ConfigureAwait(false);
                }
            }
            finally { records.Release(); }
        }

        /// <summary>
        /// The Drive folder at <paramref name="relative"/> under a pair's Drive
        /// folder, by name a folder at a time; null when there is none, or two
        /// of one name (Drive allows it, and which is meant cannot be told).
        /// </summary>
        private static async Task<string?> DriveFolderAtAsync(DriveClient client, string rootId, string relative,
            CancellationToken token)
        {
            var id = rootId;
            foreach (var name in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var children = await ListOneFolder(client, id, 1, token).ConfigureAwait(false);
                var matches = children.Where(c => c.IsFolder && SyncNameComparer.Instance.Equals(c.Name, name)).ToList();
                if (matches.Count != 1) return null;
                id = matches[0].Id;
            }
            return id;
        }

        /// <summary>A file Windows makes for itself in a folder: never synced, and nothing to keep.</summary>
        internal static bool IsWindowsLeftover(string name) =>
            name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
            SyncPlanner.IsSkippedPath(name);

        /// <summary>One PC file checked against Drive's copy: as it was when its MD5 was read.</summary>
        private sealed record CheckedFile(string FullPath, long Size, DateTime ModifiedUtc);

        /// <summary>What checking a PC folder against Drive found.</summary>
        private sealed record FolderCheck(List<CheckedFile> Matching, List<string> Leftovers, List<string> NotOnDrive,
            string? Link);

        /// <summary>
        /// Every file under <paramref name="pcFolder"/>, hidden ones included,
        /// against Drive's listing of the same folder: the same size and the
        /// same MD5. Windows' own leftovers (desktop.ini, Thumbs.db, a download
        /// in progress) need no copy. A link inside is never followed.
        /// </summary>
        private static async Task<FolderCheck> CheckAgainstDriveAsync(string pcFolder,
            IReadOnlyDictionary<string, SyncFile> remote, CancellationToken token)
        {
            var matching = new List<CheckedFile>();
            var leftovers = new List<string>();
            var notOnDrive = new List<string>();
            var folders = new Stack<string>();
            folders.Push(pcFolder);
            while (folders.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var folder = folders.Pop();
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    var relative = DriveOnlyFolders.Normal(Path.GetRelativePath(pcFolder, entry.FullName));
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        return new FolderCheck(matching, leftovers, notOnDrive, relative);
                    if (entry is DirectoryInfo) { folders.Push(entry.FullName); continue; }
                    if (IsWindowsLeftover(entry.Name)) { leftovers.Add(entry.FullName); continue; }

                    var file = (FileInfo)entry;
                    if (!remote.TryGetValue(relative, out var onDrive) || onDrive.Md5 == null || onDrive.Size != file.Length)
                    {
                        notOnDrive.Add(relative);
                        continue;
                    }
                    string md5;
                    long size;
                    DateTime modified;
                    try
                    {
                        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                            FileShare.Read | FileShare.Delete, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
                        size = stream.Length;
                        modified = File.GetLastWriteTimeUtc(file.FullName);
                        md5 = Convert.ToHexString(await MD5.HashDataAsync(stream, token).ConfigureAwait(false));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        notOnDrive.Add(relative);      // in use, or not ours to read: kept
                        continue;
                    }
                    if (size == onDrive.Size && string.Equals(md5, onDrive.Md5, StringComparison.OrdinalIgnoreCase))
                        matching.Add(new CheckedFile(file.FullName, size, modified));
                    else
                        notOnDrive.Add(relative);
                }
            }
            return new FolderCheck(matching, leftovers, notOnDrive, null);
        }

        /// <summary>
        /// Runs a pass of one pair now and waits, at most <paramref name="max"/>,
        /// for it to finish. False when it did not (paused, offline, too long).
        /// </summary>
        private async Task<bool> SyncPairNowAsync(string pairId, TimeSpan max, CancellationToken token)
        {
            var asked = DateTime.UtcNow;
            lock (_gate) _due.Add(pairId);
            Wake();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < max)
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                lock (_gate)
                    if (_lastChecked.TryGetValue(pairId, out var at) && at > asked && _running != pairId) return true;
            }
            return false;
        }

        /// <summary>How long Keep on Drive only waits for a pass to send what is not in Drive yet. The suite shortens it.</summary>
        internal TimeSpan DriveOnlySyncWait { get; set; } = TimeSpan.FromMinutes(5);

        private static string Leaf(string relative) => SyncMoves.Leaf(relative);

        /// <summary>"notes.txt", "notes.txt and one more", "notes.txt and 4 more".</summary>
        private static string SomeOf(List<string> names) =>
            names.Count == 1 ? Leaf(names[0])
            : $"{Leaf(names[0])} and {(names.Count == 2 ? "one" : NameRules.Number(names.Count - 1))} more";

        /// <summary>The pairs as they are now, with one changed by <paramref name="change"/>, saved and synced.</summary>
        private DriveSyncPair? EditPair(string pairId, Action<DriveSyncPair> change)
        {
            var pairs = Pairs;
            var pair = pairs.FirstOrDefault(p => p.Id == pairId);
            if (pair == null) return null;
            change(pair);
            SetPairs(pairs);
            return pair;
        }

        /// <summary>
        /// Keep on Drive only: see the class summary. Nothing is removed from the
        /// PC unless every file in the folder is in Drive, byte for byte.
        /// </summary>
        public async Task<DriveOnlyOutcome> KeepOnDriveOnlyAsync(string pairId, string relative, CancellationToken token,
            DriveClient? client = null)
        {
            relative = DriveOnlyFolders.Normal(relative);
            var pair = Pairs.FirstOrDefault(p => p.Id == pairId);
            if (pair == null || relative.Length == 0) return new(false, "That folder is not part of a synced folder");
            var name = Leaf(relative);
            if (!DriveOnlyFolders.Offered(pair))
                return new(false, pair.Mode == SyncMode.UploadOnly
                    ? $"{DisplayName(pair)} only uploads, so {name} cannot be kept on Google Drive only. Free up space does that for the whole folder"
                    : $"{DisplayName(pair)} does not sync its subfolders, so {name} is not in Google Drive");
            if (DriveOnlyFolders.Covers(pair.DriveOnlyFolders, relative))
                return new(false, $"{name} is already kept on Google Drive only");
            var pcFolder = Path.Combine(pair.LocalFolder, relative.Replace('/', '\\'));
            if (!Directory.Exists(pcFolder)) return new(false, $"{name} is not on this PC");
            client ??= _drive.Client;
            if (client == null) return new(false, "Google Drive is not connected, so nothing was changed");

            // Drive's copy, and every PC file against it. Anything not there yet
            // gets one pass of the pair to go up, then it is looked at again.
            FolderCheck check;
            for (int round = 0; ; round++)
            {
                var folderId = await DriveFolderAtAsync(client, pair.DriveFolderId, relative, token).ConfigureAwait(false);
                var remote = folderId == null
                    ? new Dictionary<string, SyncFile>(SyncNameComparer.Instance)
                    : (await ScanRemoteAsync(client, folderId, subfolders: true, token).ConfigureAwait(false)).Files;
                check = await CheckAgainstDriveAsync(pcFolder, remote, token).ConfigureAwait(false);
                if (check.Link != null)
                    return new(false, $"{name} holds a link to somewhere else ({Leaf(check.Link)}), so it was left as it is");
                if (check.NotOnDrive.Count == 0 && folderId != null) break;
                if (round > 0 || pair.Paused || !await SyncPairNowAsync(pairId, DriveOnlySyncWait, token).ConfigureAwait(false))
                {
                    if (check.NotOnDrive.Count == 0)
                        return new(false, $"{name} is not in Google Drive yet. Nothing was removed. Try again once it has synced");
                    var count = NameRules.Counted(check.NotOnDrive.Count, "file");
                    return new(false, pair.Paused
                        ? $"{count} in {name} {(check.NotOnDrive.Count == 1 ? "is" : "are")} not in Google Drive yet ({SomeOf(check.NotOnDrive)}), and {DisplayName(pair)} is paused. Nothing was removed"
                        : $"{count} in {name} {(check.NotOnDrive.Count == 1 ? "is" : "are")} not in Google Drive yet ({SomeOf(check.NotOnDrive)}). Nothing was removed. Try again once it has synced");
                }
            }

            // On the list first: from here the sync leaves the folder alone. A
            // folder inside it that was already on the list is part of it now.
            if (EditPair(pairId, p =>
                {
                    p.DriveOnlyFolders.RemoveAll(f => DriveOnlyFolders.Covers(new[] { relative }, f));
                    p.DriveOnlyFolders.Add(relative);
                }) == null)
                return new(false, "That folder is no longer synced");

            // Its records go before its files, so no pass can ever see the files
            // gone while it still remembers them. Every pass drops them as well.
            await ForgetFolderAsync(pairId, relative, token).ConfigureAwait(false);

            // Cleared file by file, each only while it is as it was checked.
            int kept = 0;
            foreach (var file in check.Matching)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(file.FullPath);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file.FullPath, attributes & ~FileAttributes.ReadOnly);
                    using var held = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read | FileShare.Delete);
                    if (held.Length != file.Size || File.GetLastWriteTimeUtc(file.FullPath) != file.ModifiedUtc)
                    {
                        kept++;
                        continue;
                    }
                    Mine(file.FullPath);
                    File.Delete(file.FullPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { kept++; }
            }
            foreach (var leftover in check.Leftovers)
            {
                try
                {
                    File.SetAttributes(leftover, FileAttributes.Normal);
                    Mine(leftover);
                    File.Delete(leftover);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            // Then the folders, deepest first, each only once it is empty.
            try
            {
                var all = Directory.EnumerateDirectories(pcFolder, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true,
                    })
                    .OrderByDescending(d => d.Length).Append(pcFolder);
                foreach (var folder in all)
                {
                    try
                    {
                        new DirectoryInfo(folder).Attributes = FileAttributes.Directory;
                        Mine(folder);
                        Directory.Delete(folder, recursive: false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            SyncHistory.Add(pairId, "Kept on Google Drive only", relative, "cleared off this PC");
            SyncHistory.Flush(pairId);
            return kept == 0
                ? new(true, $"{name} is on Google Drive only now, and was cleared off this PC")
                : new(true, $"{name} is on Google Drive only now. {NameRules.Counted(kept, "file")} changed while it was checked, so {(kept == 1 ? "it was" : "they were")} left on this PC, in {name}");
        }

        /// <summary>
        /// Put back on PC: the folder (or the one on the list that holds it)
        /// comes off the list, and the pair syncs at once, which downloads it.
        /// </summary>
        public async Task<DriveOnlyOutcome> PutBackOnPcAsync(string pairId, string relative, CancellationToken token)
        {
            relative = DriveOnlyFolders.Normal(relative);
            var pair = Pairs.FirstOrDefault(p => p.Id == pairId);
            if (pair == null) return new(false, "That folder is not part of a synced folder");
            var entry = pair.DriveOnlyFolders.FirstOrDefault(f => DriveOnlyFolders.Covers(new[] { f }, relative));
            if (entry == null) return new(false, $"{Leaf(relative)} is not kept on Google Drive only");
            entry = DriveOnlyFolders.Normal(entry);

            // No records may be left from before it went to Drive only: with
            // deletes copied, one would read as "deleted on the PC" and Drive's
            // copy would go to the trash.
            // Off the list under the same lock, so no pass that began with it
            // still on can list Drive afresh, skip it, and leave the feed past it.
            bool listed = true;
            if (!await ForgetFolderAsync(pairId, entry, token, listAgain: true, thenWhileHeld: () =>
                    listed = EditPair(pairId, p => p.DriveOnlyFolders.RemoveAll(f =>
                        SyncNameComparer.Instance.Equals(DriveOnlyFolders.Normal(f), entry))) != null)
                .ConfigureAwait(false))
                return new(false, $"The sync records of {DisplayName(pair)} could not be opened just now, so {Leaf(entry)} was left on Google Drive only. Try again in a moment");
            if (!listed) return new(false, "That folder is no longer synced");
            SyncHistory.Add(pairId, "Put back on this PC", entry, "downloading it again");
            SyncHistory.Flush(pairId);

            return new(true, pair.Paused
                ? $"{Leaf(entry)} goes back on this PC once you resume {DisplayName(pair)}"
                : $"Putting {Leaf(entry)} back on this PC");
        }
    }
}
