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

        /// <summary>Stops a running pass of the pair at its next file, as an edit of its settings does.</summary>
        internal void InterruptPair(string pairId)
        {
            lock (_gate) _generation[pairId] = (_generation.TryGetValue(pairId, out var g) ? g : 0) + 1;
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

            // A pass under way holds the lock to its end, which on a big pair is
            // hours. Keep on Drive only has already edited the pair, so it stops
            // at its next file; Put back has not yet (it edits under the lock),
            // so it tells the pass to stop here. One waiting on Google is woken
            // to notice. The next pass picks up whatever was left.
            if (listAgain) InterruptPair(pairId);
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
            name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Never synced, but somebody's: a .tmp, an Office lock file, a download
        /// Resilio is still writing. Not on Drive, so never deleted; left on the
        /// PC (its folder stays). 4.1.3 deleted them with the leftovers.
        /// </summary>
        internal static bool IsLeftOnPc(string name) => !IsWindowsLeftover(name) && SyncPlanner.IsSkippedPath(name);

        /// <summary>One PC file checked against Drive's copy: as it was when its MD5 was read.</summary>
        private sealed record CheckedFile(string FullPath, long Size, DateTime ModifiedUtc);

        /// <summary>What checking a PC folder against Drive found.</summary>
        private sealed record FolderCheck(List<CheckedFile> Matching, List<string> Leftovers, List<string> NotOnDrive,
            string? Link, List<string> NeverSynced, List<string>? LeftOnPc = null, List<string>? LeftOutByRules = null);

        /// <summary>
        /// Every file under <paramref name="pcFolder"/>, hidden ones included,
        /// against Drive's listing of the same folder: the same size and the
        /// same MD5. Windows' own leftovers (desktop.ini, Thumbs.db, a download
        /// in progress) need no copy. A link inside is never followed.
        /// </summary>
        private static async Task<FolderCheck> CheckAgainstDriveAsync(string pcFolder,
            IReadOnlyDictionary<string, SyncFile> remote, CancellationToken token,
            SyncRules? rules = null, string pairRelative = "")
        {
            var matching = new List<CheckedFile>();
            var leftovers = new List<string>();
            var notOnDrive = new List<string>();
            var neverSynced = new List<string>();
            var leftOnPc = new List<string>();
            var leftOutByRules = new List<string>();
            // Each folder carries whether the scan would leave it out: it skips a
            // hidden or system folder whole (ScanLocalFolder), so nothing inside
            // one is ever uploaded, whatever the file's own attributes say.
            const FileAttributes leftOut = FileAttributes.Hidden | FileAttributes.System;
            bool topLeftOut = false;
            try { topLeftOut = (File.GetAttributes(pcFolder) & leftOut) != 0; } catch { }
            var folders = new Stack<(string Path, bool LeftOut)>();
            folders.Push((pcFolder, topLeftOut));
            while (folders.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var (folder, folderLeftOut) = folders.Pop();
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    var relative = DriveOnlyFolders.Normal(Path.GetRelativePath(pcFolder, entry.FullName));
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        return new FolderCheck(matching, leftovers, notOnDrive, relative, neverSynced);
                    if (entry is DirectoryInfo)
                    {
                        folders.Push((entry.FullName, folderLeftOut || (entry.Attributes & leftOut) != 0));
                        continue;
                    }
                    if (IsWindowsLeftover(entry.Name)) { leftovers.Add(entry.FullName); continue; }
                    if (IsLeftOnPc(entry.Name)) { leftOnPc.Add(relative); continue; }

                    var file = (FileInfo)entry;
                    if (!remote.TryGetValue(relative, out var onDrive) || onDrive.Md5 == null || onDrive.Size != file.Length)
                    {
                        // Hidden and system files are never uploaded (the scan leaves
                        // them out), so waiting for a pass to send them was 5 minutes
                        // for a refusal that could never come right.
                        if (folderLeftOut || (file.Attributes & leftOut) != 0) neverSynced.Add(relative);
                        // So are files the pair's own settings leave out: the skip
                        // list, and anything over its size limit.
                        else if (rules != null &&
                                 (rules.SkipsType(pairRelative.Length == 0 ? relative : pairRelative + "/" + relative) ||
                                  (rules.MaxBytes > 0 && file.Length > rules.MaxBytes)))
                            leftOutByRules.Add(relative);
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
            return new FolderCheck(matching, leftovers, notOnDrive, null, neverSynced, leftOnPc, leftOutByRules);
        }

        /// <summary>
        /// Runs a pass of one pair now and waits, at most <paramref name="max"/>,
        /// for it to finish. False when it did not (paused, stopped, too long),
        /// with the pair's problem when the pass hit one that lasts. True when
        /// it went through, with the problem it ended on (files refused, Drive
        /// full), if any. Only a
        /// pass that began after the ask counts: one already running may have
        /// looked at the folder before the files came.
        /// </summary>
        private async Task<(bool Synced, string? Problem)> SyncPairNowAsync(string pairId, TimeSpan max, CancellationToken token)
        {
            var asked = DateTime.UtcNow;
            lock (_gate) _due.Add(pairId);
            Wake();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < max)
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                lock (_gate)
                {
                    // Paused or removed meanwhile: no pass is coming.
                    if (_removed.Contains(pairId) || !_pairs.Any(p => p.Id == pairId && !p.Paused)) return (false, null);
                    if (_running == pairId || !_ranFrom.TryGetValue(pairId, out var ran) || ran <= asked) continue;
                    // A pass sets _lastChecked only when it went through, before
                    // _running is cleared.
                    // It went through, but may have ended on a problem it noted
                    // without stopping (files Google refused, Drive full): handed
                    // back, for the caller to give if files are still missing.
                    if (_lastChecked.TryGetValue(pairId, out var at) && at >= ran)
                    {
                        var left = _status.TryGetValue(pairId, out var done) ? done.Problem : null;
                        return (true, left == null || left.StartsWith("offline", StringComparison.Ordinal) ? null : left);
                    }
                    var problem = _status.TryGetValue(pairId, out var status) ? status.Problem : null;
                    // No problem left: it was stopped (paused, edited or removed).
                    if (problem == null) return (false, null);
                    // Offline: the pair stays due and runs again on reconnect.
                    if (problem.StartsWith("offline", StringComparison.Ordinal)) continue;
                    // Anything else (Google's "not now" is waited out inside the
                    // pass) would only happen again.
                    return (false, problem);
                }
            }
            return (false, null);
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
            // Off the UI thread, with a limit: a share or disk that stopped answering
            // would otherwise freeze the window.
            var there = Task.Run(() => Directory.Exists(pcFolder));
            if (await Task.WhenAny(there, Task.Delay(5000, token)).ConfigureAwait(false) != there)
                return new(false, $"This PC's folder is not answering, so {name} was not changed");
            if (!there.Result) return new(false, $"{name} is not on this PC");
            client ??= _drive.Client;
            if (client == null) return new(false, "Google Drive is not connected, so nothing was changed");

            // Drive's copy, and every PC file against it. Anything not there yet
            // gets one pass of the pair to go up, then it is looked at again.
            var rules = SyncRules.For(pair);
            FolderCheck check;
            string? lastPassSaid = null;   // the problem the pass went through with, if any
            for (int round = 0; ; round++)
            {
                var folderId = await DriveFolderAtAsync(client, pair.DriveFolderId, relative, token).ConfigureAwait(false);
                var remote = folderId == null
                    ? new Dictionary<string, SyncFile>(SyncNameComparer.Instance)
                    : (await ScanRemoteAsync(client, folderId, subfolders: true, token).ConfigureAwait(false)).Files;
                check = await CheckAgainstDriveAsync(pcFolder, remote, token, rules, relative).ConfigureAwait(false);
                if (check.Link != null)
                    return new(false, $"{name} holds a link to somewhere else ({Leaf(check.Link)}), so it was left as it is");
                if (check.NotOnDrive.Count == 0 && folderId != null) break;
                if (check.NeverSynced.Count > 0)
                {
                    var hidden = NameRules.Counted(check.NeverSynced.Count, "hidden file");
                    return new(false, $"{hidden} in {name} {(check.NeverSynced.Count == 1 ? "is" : "are")} never synced ({SomeOf(check.NeverSynced)}), " +
                                      "so it can't be kept on Google Drive only. Nothing was removed");
                }
                if (check.LeftOutByRules is { Count: > 0 } byRules)
                {
                    var files = NameRules.Counted(byRules.Count, "file");
                    return new(false, $"{files} in {name} {(byRules.Count == 1 ? "is" : "are")} left out by the sync settings of {DisplayName(pair)} ({SomeOf(byRules)}), " +
                                      "so it can't be kept on Google Drive only. Nothing was removed");
                }
                // Download only never sends anything up, so a file only on the PC
                // would wait for ever: said now, with no pass. A file Drive has
                // that differs still gets the pass, which may bring Drive's down.
                if (pair.Mode == SyncMode.DownloadOnly)
                {
                    var pcOnly = check.NotOnDrive.Where(r => !remote.ContainsKey(r)).ToList();
                    if (pcOnly.Count > 0)
                    {
                        bool one = pcOnly.Count == 1;
                        return new(false, $"{NameRules.Counted(pcOnly.Count, "file")} in {name} {(one ? "is" : "are")} only on this PC ({SomeOf(pcOnly)}), " +
                                          $"and {DisplayName(pair)} only copies from Google Drive, so {(one ? "it" : "they")} will never go up. " +
                                          $"Move {(one ? "it" : "them")} out of {name}, or put {(one ? "it" : "them")} in Google Drive, then try again. Nothing was removed");
                    }
                    if (folderId == null)
                        return new(false, $"{name} is not in Google Drive, and {DisplayName(pair)} only copies from Google Drive, so it never will be. Nothing was removed");
                }
                bool synced = false;
                string? passProblem = null;
                if (round == 0 && !pair.Paused)
                    (synced, passProblem) = await SyncPairNowAsync(pairId, DriveOnlySyncWait, token).ConfigureAwait(false);
                if (!synced && passProblem != null)
                    return new(false, $"{DisplayName(pair)} could not sync just now ({passProblem}), so {name} was left as it is. Nothing was removed");
                // A pass that went through but ended on a problem (a file Google
                // refused, Drive full) is only the reason if the look after it
                // still finds files missing: it may be about another folder.
                if (synced) lastPassSaid = passProblem;
                if (!synced)
                {
                    // Download only, after a pass: what still differs is a PC edit,
                    // which that mode never sends.
                    if (pair.Mode == SyncMode.DownloadOnly && round > 0 && check.NotOnDrive.Count > 0)
                    {
                        bool one = check.NotOnDrive.Count == 1;
                        return new(false, $"{NameRules.Counted(check.NotOnDrive.Count, "file")} in {name} {(one ? "is" : "are")} different from Google Drive's copy ({SomeOf(check.NotOnDrive)}), " +
                                          $"and {DisplayName(pair)} only copies from Google Drive, so the changes on this PC never go up. Nothing was removed");
                    }
                    // Said instead of "try again", which would never help.
                    var why = lastPassSaid != null && !pair.Paused
                        ? $" {DisplayName(pair)} could not send {(check.NotOnDrive.Count == 1 ? "it" : "them")}: {lastPassSaid}. Nothing was removed"
                        : null;
                    if (check.NotOnDrive.Count == 0)
                        return new(false, why != null
                            ? $"{name} is not in Google Drive yet.{why}"
                            : $"{name} is not in Google Drive yet. Nothing was removed. Try again once it has synced");
                    var count = NameRules.Counted(check.NotOnDrive.Count, "file");
                    var notYet = $"{count} in {name} {(check.NotOnDrive.Count == 1 ? "is" : "are")} not in Google Drive yet ({SomeOf(check.NotOnDrive)})";
                    return new(false, pair.Paused
                        ? $"{notYet}, and {DisplayName(pair)} is paused. Nothing was removed"
                        : why != null ? notYet + "." + why
                        : $"{notYet}. Nothing was removed. Try again once it has synced");
                }
            }

            // A file open in another program that refuses deleting (a player
            // through the C runtime shares read and write, not delete) could not
            // be cleared, and would be left in a folder the sync then ignores:
            // asked before anything changes, so the person can close it and retry.
            var open = check.Matching.Where(f => OpenWithoutDelete(f.FullPath)).Select(f => Path.GetFileName(f.FullPath)).ToList();
            if (open.Count > 0)
            {
                bool one = open.Count == 1;
                return new(false, $"{NameRules.Counted(open.Count, "file")} in {name} {(one ? "is" : "are")} open in another program ({SomeOf(open)}), " +
                                  $"so {name} can't be kept on Google Drive only yet. Nothing was removed. Close {(one ? "it" : "them")} and try again");
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

            BeforeClearingForTests?.Invoke(pcFolder);

            // Cleared file by file, each only while it is as it was checked. What
            // stays is sorted by why: changed, open elsewhere, or refused.
            var changed = new List<string>();
            var inUse = new List<string>();
            var refusedFiles = new List<string>();
            foreach (var file in check.Matching)
            {
                token.ThrowIfCancellationRequested();
                var leaf = Path.GetFileName(file.FullPath);
                FileAttributes? cleared = null;
                try
                {
                    using var held = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read | FileShare.Delete);
                    if (held.Length != file.Size || File.GetLastWriteTimeUtc(file.FullPath) != file.ModifiedUtc)
                    {
                        changed.Add(leaf);
                        continue;
                    }
                    var attributes = File.GetAttributes(file.FullPath);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file.FullPath, attributes & ~FileAttributes.ReadOnly);
                        cleared = attributes;
                    }
                    Mine(file.FullPath);
                    File.Delete(file.FullPath);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }   // already gone
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    (InUseElsewhere(ex, file.FullPath) ? inUse : refusedFiles).Add(leaf);
                    if (cleared is { } was)
                        try { if (File.Exists(file.FullPath)) File.SetAttributes(file.FullPath, was); }
                        catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
                }
            }
            int kept = changed.Count + inUse.Count + refusedFiles.Count;
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

            // Temporary files and downloads still being written never sync, so
            // they stay, and so does their folder: "cleared off this PC" would
            // not be true. Said, with what they are.
            var left = check.LeftOnPc ?? new List<string>();
            SyncHistory.Add(pairId, "Kept on Google Drive only", relative,
                kept == 0 && left.Count == 0 ? "cleared off this PC" : "cleared off this PC, some files left");
            SyncHistory.Flush(pairId);
            var stayed = left.Count == 0 ? ""
                : $" {NameRules.Counted(left.Count, "file")} {(left.Count == 1 ? "was" : "were")} left on this PC because " +
                  $"{(left.Count == 1 ? "it's a temporary file or a download that isn't finished" : "they're temporary files or downloads that aren't finished")}, " +
                  $"which never sync ({SomeOf(left)})";
            if (kept == 0)
                return left.Count == 0
                    ? new(true, $"{name} is on Google Drive only now, and was cleared off this PC")
                    : new(true, $"{name} is on Google Drive only now, and the rest was cleared off this PC.{stayed}");
            var said = $"{name} is on Google Drive only now.";
            if (changed.Count > 0)
            {
                bool one = changed.Count == 1;
                said += $" {NameRules.Counted(changed.Count, "file")} changed while {(one ? "it was" : "they were")} checked ({SomeOf(changed)}), " +
                        $"so {(one ? "it was" : "they were")} left on this PC in {name}. {(one ? "It doesn't" : "They don't")} sync while {name} is kept on Google Drive only. " +
                        $"Move {(one ? "it" : "them")} out of {name} to sync {(one ? "it" : "them")}.";
            }
            if (inUse.Count > 0)
            {
                bool one = inUse.Count == 1;
                said += $" {NameRules.Counted(inUse.Count, "file")} {(one ? "was" : "were")} open in another program ({SomeOf(inUse)}), " +
                        $"so {(one ? "it was" : "they were")} left on this PC in {name}. Google Drive has {(one ? "it" : "them")}. " +
                        $"Close {(one ? "it" : "them")} and delete {(one ? "it" : "them")} yourself.";
            }
            if (refusedFiles.Count > 0)
                said += $" Windows wouldn't let {NameRules.Counted(refusedFiles.Count, "file")} be removed ({SomeOf(refusedFiles)}). Google Drive has {(refusedFiles.Count == 1 ? "it" : "them")}.";
            return new(true, said + stayed);
        }

        /// <summary>The suite's way in between the check and the clearing, to open or delete a file there.</summary>
        internal Action<string>? BeforeClearingForTests { get; set; }

        /// <summary>
        /// Whether another program holds the file open without letting it be
        /// deleted: an open asking for delete access is refused with a sharing
        /// or lock violation. A read-only open would not notice such a handle.
        /// </summary>
        private static bool OpenWithoutDelete(string path)
        {
            const uint Delete = 0x00010000, ReadAttributes = 0x80, ShareAll = 7, OpenExisting = 3, OpenReparsePoint = 0x00200000;
            using var handle = CreateFileW(path, Delete | ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
            if (!handle.IsInvalid) return false;
            return System.Runtime.InteropServices.Marshal.GetLastWin32Error() is 32 or 33;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

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

            // An upload-only pass never downloads, so nothing would come back.
            if (pair.Mode == SyncMode.UploadOnly)
                return new(false, $"{DisplayName(pair)} only uploads, so {Leaf(entry)} cannot be put back on this PC. Change its direction to bring it down");

            // No records may be left from before it went to Drive only: with
            // deletes copied, one would read as "deleted on the PC" and Drive's
            // copy would go to the trash.
            // Off the list under the same lock, so no pass that began with it
            // still on can list Drive afresh, skip it, and leave the feed past it.
            // On a worker: with the lock free nothing below really waits, and the
            // state file (decrypted, read, rewritten) would be handled on the UI
            // thread, freezing the window on a big pair.
            bool listed = true;
            if (!await Task.Run(() => ForgetFolderAsync(pairId, entry, token, listAgain: true, thenWhileHeld: () =>
                    listed = EditPair(pairId, p => p.DriveOnlyFolders.RemoveAll(f =>
                        SyncNameComparer.Instance.Equals(DriveOnlyFolders.Normal(f), entry))) != null), token)
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
