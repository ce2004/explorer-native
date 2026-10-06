using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ExplorerNative
{
    /// <summary>Which way a monitored folder pair copies.</summary>
    public enum SyncMode
    {
        /// <summary>PC to Drive only: a backup. Nothing in Drive is ever copied down.</summary>
        UploadOnly,

        /// <summary>Drive to PC only. Nothing on the PC is ever copied up.</summary>
        DownloadOnly,

        /// <summary>Both ways, so the two folders stay the same.</summary>
        TwoWay,
    }

    /// <summary>What happens when the same file changed on both sides in two-way.</summary>
    public enum ConflictChoice
    {
        /// <summary>Keep both: the newer keeps the name, the other is renamed as a conflict copy.</summary>
        KeepBoth,

        /// <summary>The PC's copy replaces Drive's (Drive's goes to the Drive trash).</summary>
        PcWins,

        /// <summary>Drive's copy replaces the PC's.</summary>
        DriveWins,
    }

    /// <summary>
    /// One folder on the PC kept in step with one folder in Google Drive.
    /// Persisted in settings.json, which is encrypted.
    /// </summary>
    public sealed class DriveSyncPair
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string LocalFolder { get; set; } = "";
        public string DriveFolderId { get; set; } = "";

        /// <summary>For reading out only, such as "My Drive/Music". The id is what is used.</summary>
        public string DriveFolderPath { get; set; } = "";

        public SyncMode Mode { get; set; } = SyncMode.TwoWay;

        /// <summary>Off by default: a delete on one side is not copied to the other.</summary>
        public bool CopyDeletes { get; set; }

        public bool Paused { get; set; }

        /// <summary>Folders inside the folder are synced too. On by default.</summary>
        public bool IncludeSubfolders { get; set; } = true;

        /// <summary>File types never synced, such as ".iso; .mkv". Empty by default.</summary>
        public string SkipExtensions { get; set; } = "";

        /// <summary>Files bigger than this many megabytes are never synced. Zero for no limit.</summary>
        public int MaxFileMegabytes { get; set; }

        public ConflictChoice WhenBothChanged { get; set; } = ConflictChoice.KeepBoth;

        /// <summary>How often Drive is checked, in minutes. Zero: only when Sync now is pressed.</summary>
        public int CheckMinutes { get; set; } = 1;

        public DriveSyncPair Copy() => (DriveSyncPair)MemberwiseClone();
    }

    /// <summary>A file as one side sees it now.</summary>
    /// <summary>A file as one side sees it now. Md5 is Drive's checksum, when Drive gives one.</summary>
    public readonly record struct SyncFile(long Size, DateTime ModifiedUtc, string? Id = null, string? Md5 = null);

    /// <summary>What both sides held the last time the pair was in step.</summary>
    public sealed record SyncBase(
        long LocalSize, DateTime LocalModifiedUtc, long RemoteSize, DateTime RemoteModifiedUtc, string RemoteId,
        string? RemoteMd5 = null);

    /// <summary>Everything about a pair the planner needs.</summary>
    public sealed record SyncRules(
        SyncMode Mode, bool CopyDeletes, ConflictChoice WhenBothChanged = ConflictChoice.KeepBoth,
        bool IncludeSubfolders = true, string SkipExtensions = "", long MaxBytes = 0)
    {
        public static SyncRules For(DriveSyncPair pair) => new(
            pair.Mode, pair.CopyDeletes, pair.WhenBothChanged, pair.IncludeSubfolders, pair.SkipExtensions ?? "",
            pair.MaxFileMegabytes > 0 ? pair.MaxFileMegabytes * 1024L * 1024 : 0);

        private HashSet<string>? _skipped;

        /// <summary>The skipped file types, written with or without the dot, separated by ; , or spaces.</summary>
        public bool SkipsType(string path)
        {
            _skipped ??= new HashSet<string>(
                SkipExtensions.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(e => e.Trim().StartsWith('.') ? e.Trim() : "." + e.Trim()),
                StringComparer.OrdinalIgnoreCase);
            return _skipped.Count > 0 && _skipped.Contains(Path.GetExtension(path));
        }
    }

    public enum SyncActionKind
    {
        Upload,
        Download,
        DeleteRemote,
        DeleteLocal,

        /// <summary>Changed on both sides; the PC copy is newer and keeps the name.</summary>
        KeepBothLocalWins,

        /// <summary>Changed on both sides; the Drive copy is newer and keeps the name.</summary>
        KeepBothRemoteWins,

        /// <summary>Both sides already agree; remember them as in step.</summary>
        Record,

        /// <summary>Nothing left to track for this path.</summary>
        Forget,
    }

    public sealed record SyncAction(SyncActionKind Kind, string Path);

    /// <summary>
    /// Decides what a sync does, from what each side holds now and what both
    /// held at the last sync. Pure: no files, no network, so every rule here is
    /// tested directly.
    ///
    /// The rules, in plain words:
    /// - New or changed on one side is copied to the other, if the mode lets
    ///   it go that way.
    /// - Changed on both sides in two-way keeps both: the newer keeps the name
    ///   and the other becomes "name (conflict date).ext".
    /// - A delete is copied only when the pair says so, and never on the first
    ///   sync, which has nothing to say a file was ever there.
    /// - A rename is a delete and an add: the new name is copied, and the old
    ///   name is removed only when deletes are copied.
    /// </summary>
    public static class SyncPlanner
    {
        /// <summary>FAT and some network shares keep times to two seconds.</summary>
        private const double SlackSeconds = 2;

        public static bool Same(SyncFile a, SyncFile b) =>
            a.Size == b.Size && Math.Abs((a.ModifiedUtc - b.ModifiedUtc).TotalSeconds) <= SlackSeconds;

        /// <summary>
        /// Drive's side changed: a different file id, a different size, or (when
        /// Drive gave a checksum both times) different contents. Without
        /// checksums the modified time decides.
        /// </summary>
        private static bool RemoteChanged(SyncFile now, SyncBase was)
        {
            if (!string.Equals(now.Id, was.RemoteId, StringComparison.Ordinal)) return true;
            if (now.Size != was.RemoteSize) return true;
            if (now.Md5 != null && was.RemoteMd5 != null)
                return !string.Equals(now.Md5, was.RemoteMd5, StringComparison.OrdinalIgnoreCase);
            return Math.Abs((now.ModifiedUtc - was.RemoteModifiedUtc).TotalSeconds) > SlackSeconds;
        }

        /// <summary>
        /// Whether a file on both sides, with nothing remembered to say they
        /// are in step, is the same file. Different sizes never are. With Drive's
        /// MD5 the PC file's MD5 decides (null while it is not known yet); with
        /// none, the size and the modified time within two seconds.
        /// </summary>
        public static bool? Adoptable(string path, SyncFile local, SyncFile remote,
            IReadOnlyDictionary<string, string>? localMd5)
        {
            if (local.Size != remote.Size) return false;
            if (remote.Md5 != null)
                return localMd5 != null && localMd5.TryGetValue(path, out var md5)
                    ? string.Equals(md5, remote.Md5, StringComparison.OrdinalIgnoreCase)
                    : null;
            return Same(local, remote);
        }

        /// <summary>
        /// The PC files whose MD5 the next plan needs: on both sides, nothing
        /// remembered (or changed on both), the same size, and a checksum on
        /// Drive to compare with. Under the same rules as the plan.
        /// </summary>
        public static List<string> NeedingHash(
            SyncRules rules, IReadOnlyDictionary<string, SyncFile> local,
            IReadOnlyDictionary<string, SyncFile> remote, IReadOnlyDictionary<string, SyncBase> last)
        {
            var result = new List<string>();
            foreach (var (path, l) in local)
            {
                if (!remote.TryGetValue(path, out var r) || r.Md5 == null || r.Size != l.Size) continue;
                if (IsSkippedPath(path) || (!rules.IncludeSubfolders && path.Contains('/')) || rules.SkipsType(path)) continue;
                if (rules.MaxBytes > 0 && l.Size > rules.MaxBytes) continue;
                if (last.TryGetValue(path, out var b) &&
                    !(Changed(l, b.LocalSize, b.LocalModifiedUtc) && RemoteChanged(r, b))) continue;
                result.Add(path);
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static bool Changed(SyncFile now, long size, DateTime modified) =>
            now.Size != size || Math.Abs((now.ModifiedUtc - modified).TotalSeconds) > SlackSeconds;

        /// <summary>
        /// Files never synced: Windows' own folder files, Office lock files, and
        /// half-written downloads, including this monitor's own.
        /// </summary>
        public static bool IsSkipped(string name)
        {
            name = Path.GetFileName(name.Replace('/', Path.DirectorySeparatorChar));
            if (name.Length == 0) return true;
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("~$", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>True when any part of a relative path is a skipped name.</summary>
        public static bool IsSkippedPath(string relative) =>
            relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(IsSkipped);

        public static List<SyncAction> Plan(
            SyncMode mode, bool copyDeletes,
            IReadOnlyDictionary<string, SyncFile> local,
            IReadOnlyDictionary<string, SyncFile> remote,
            IReadOnlyDictionary<string, SyncBase> last) =>
            Plan(new SyncRules(mode, copyDeletes), local, remote, last);

        /// <summary>
        /// The plan for one pass. It always compares both sides as they are
        /// now with what both held at the last sync, so nothing depends on
        /// having seen a change happen: an edit made offline, or while the app
        /// was closed, is found the same way as one made a second ago.
        ///
        /// A file the rules skip (its type, its size on either side, or a
        /// subfolder when subfolders are off) gets no action at all and keeps
        /// whatever was remembered about it, so skipping a file never reads as
        /// deleting it.
        /// </summary>
        public static List<SyncAction> Plan(
            SyncRules rules,
            IReadOnlyDictionary<string, SyncFile> local,
            IReadOnlyDictionary<string, SyncFile> remote,
            IReadOnlyDictionary<string, SyncBase> last,
            IReadOnlyDictionary<string, string>? localMd5 = null)
        {
            var mode = rules.Mode;
            bool copyDeletes = rules.CopyDeletes;
            bool up = mode != SyncMode.DownloadOnly;
            bool down = mode != SyncMode.UploadOnly;

            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            paths.UnionWith(local.Keys);
            paths.UnionWith(remote.Keys);
            paths.UnionWith(last.Keys);

            var actions = new List<SyncAction>();
            void Do(SyncActionKind kind, string path) => actions.Add(new SyncAction(kind, path));

            foreach (var path in paths)
            {
                if (IsSkippedPath(path)) continue;
                if (!rules.IncludeSubfolders && path.Contains('/')) continue;
                if (rules.SkipsType(path)) continue;

                bool hasL = local.TryGetValue(path, out var l);
                bool hasR = remote.TryGetValue(path, out var r);
                bool hasB = last.TryGetValue(path, out var b);

                if (rules.MaxBytes > 0 && ((hasL && l.Size > rules.MaxBytes) || (hasR && r.Size > rules.MaxBytes)))
                    continue;

                bool changedL = hasL && (!hasB || Changed(l, b!.LocalSize, b.LocalModifiedUtc));
                bool changedR = hasR && (!hasB || RemoteChanged(r, b!));

                if (hasL && hasR)
                {
                    if (!hasB || (changedL && changedR))
                    {
                        // Already the same file on both sides (a first sync over
                        // two copies of one library): remembered, never copied.
                        var same = Adoptable(path, l, r, localMd5);
                        if (same == true) Do(SyncActionKind.Record, path);
                        // Waiting for this PC file's checksum: left alone this pass.
                        else if (same == null) { }
                        else if (mode == SyncMode.UploadOnly) Do(SyncActionKind.Upload, path);
                        else if (mode == SyncMode.DownloadOnly) Do(SyncActionKind.Download, path);
                        else if (rules.WhenBothChanged == ConflictChoice.PcWins) Do(SyncActionKind.Upload, path);
                        else if (rules.WhenBothChanged == ConflictChoice.DriveWins) Do(SyncActionKind.Download, path);
                        else Do(l.ModifiedUtc >= r.ModifiedUtc ? SyncActionKind.KeepBothLocalWins : SyncActionKind.KeepBothRemoteWins, path);
                    }
                    else if (changedL)
                    {
                        // Download only: an edit on the PC is left alone, never overwritten.
                        if (up) Do(SyncActionKind.Upload, path);
                    }
                    else if (changedR)
                    {
                        if (down) Do(SyncActionKind.Download, path);
                    }
                }
                else if (hasL)
                {
                    if (!hasB) { if (up) Do(SyncActionKind.Upload, path); }
                    // Deleted in Drive, and untouched here since: copy the delete.
                    else if (copyDeletes && down && !changedL) Do(SyncActionKind.DeleteLocal, path);
                    else if (up) Do(SyncActionKind.Upload, path);
                    else Do(SyncActionKind.Forget, path);
                }
                else if (hasR)
                {
                    if (!hasB) { if (down) Do(SyncActionKind.Download, path); }
                    // Deleted on the PC, and untouched in Drive since: copy the delete.
                    else if (copyDeletes && up && !changedR) Do(SyncActionKind.DeleteRemote, path);
                    else if (down) Do(SyncActionKind.Download, path);
                    else Do(SyncActionKind.Forget, path);
                }
                else if (hasB)
                {
                    Do(SyncActionKind.Forget, path);
                }
            }

            return actions;
        }

        /// <summary>"report (conflict 2026-10-05).docx", for the copy that loses the name.</summary>
        public static string ConflictName(string relative, DateTime when, Func<string, bool> taken)
        {
            int slash = relative.LastIndexOf('/');
            string folder = slash < 0 ? "" : relative[..(slash + 1)];
            string file = relative[(slash + 1)..];
            string stem = Path.GetFileNameWithoutExtension(file);
            string ext = Path.GetExtension(file);
            string stamp = when.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

            for (int n = 1; ; n++)
            {
                var candidate = folder + stem + (n == 1 ? $" (conflict {stamp})" : $" (conflict {stamp} {n})") + ext;
                if (!taken(candidate)) return candidate;
            }
        }

        /// <summary>
        /// The order to carry a plan out in: the quick bookkeeping first, then
        /// transfers smallest first, so one huge file does not hold back every
        /// small one.
        /// </summary>
        public static List<SyncAction> Ordered(
            List<SyncAction> plan, IReadOnlyDictionary<string, SyncFile> local, IReadOnlyDictionary<string, SyncFile> remote)
        {
            long SizeOf(SyncAction a) => a.Kind switch
            {
                SyncActionKind.Upload or SyncActionKind.KeepBothLocalWins =>
                    local.TryGetValue(a.Path, out var l) ? l.Size : 0,
                SyncActionKind.Download or SyncActionKind.KeepBothRemoteWins =>
                    remote.TryGetValue(a.Path, out var r) ? r.Size : 0,
                _ => -1,
            };
            return plan.Select((a, i) => (a, i, size: SizeOf(a)))
                .OrderBy(x => x.size).ThenBy(x => x.i)
                .Select(x => x.a).ToList();
        }
    }

    /// <summary>A PC file's MD5, as it was when it was worked out.</summary>
    public sealed record SyncHashEntry(long Size, DateTime ModifiedUtc, string Md5);

    /// <summary>
    /// The MD5s remembered per pair, so no file is hashed twice: an entry is
    /// good while the file's size and modified time are what they were.
    /// </summary>
    public static class SyncHashes
    {
        /// <summary>Which of <paramref name="wanted"/> are already known, and which still need hashing.</summary>
        public static (Dictionary<string, string> Known, List<string> ToHash) Split(
            IEnumerable<string> wanted, IReadOnlyDictionary<string, SyncFile> local,
            IReadOnlyDictionary<string, SyncHashEntry> cache)
        {
            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var toHash = new List<string>();
            foreach (var path in wanted)
            {
                if (cache.TryGetValue(path, out var entry) && local.TryGetValue(path, out var file) &&
                    entry.Size == file.Size && entry.ModifiedUtc == file.ModifiedUtc)
                    known[path] = entry.Md5;
                else
                    toHash.Add(path);
            }
            return (known, toHash);
        }
    }

    /// <summary>
    /// Waiting out Google: which failures are "not now" rather than "no", and
    /// how long to wait. A rate limit or a server error is waited out for as
    /// long as it lasts, and never counts a file as failed or skips it.
    /// </summary>
    public static class SyncBackoff
    {
        public static readonly TimeSpan Cap = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan First = TimeSpan.FromSeconds(2);

        /// <summary>
        /// The wait before attempt <paramref name="attempt"/> (0 for the first
        /// retry): doubling from two seconds, capped at five minutes, jittered
        /// down by up to half (<paramref name="jitter"/> from 0 to 1) so several
        /// waits do not all come back at once. Google's own Retry-After is
        /// honoured whenever it asks for longer.
        /// </summary>
        public static TimeSpan Delay(int attempt, TimeSpan? retryAfter, double jitter)
        {
            double seconds = First.TotalSeconds * Math.Pow(2, Math.Clamp(attempt, 0, 30));
            seconds = Math.Min(seconds, Cap.TotalSeconds) * (1 - 0.5 * Math.Clamp(jitter, 0, 1));
            var wait = TimeSpan.FromSeconds(seconds);
            return retryAfter is { } asked && asked > wait ? asked : wait;
        }

        private static readonly System.Text.RegularExpressions.Regex Status = new(
            @"(?:^|failed |answered |status )(429|500|502|503|504)\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Whether a failure is Google saying "not now": 429, 403 for a rate
        /// limit (userRateLimitExceeded, rateLimitExceeded, or a daily limit that
        /// resets by itself), or 500, 502, 503, 504. Not a full account, not a
        /// missing file, and not the connection being down, which is offline.
        /// </summary>
        public static bool IsTransient(Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
            {
                if (e is DriveQuotaException quota) return quota.ResetsOnItsOwn;

                // The status and Google's reason, carried as facts. The message
                // is prose by now and no longer names either.
                if (e is DriveClient.DriveStatusException { Status: > 0 } status) return status.Transient;
                if (e is System.Net.Http.HttpRequestException { StatusCode: { } code } &&
                    (int)code is 408 or 429 or 500 or 502 or 503 or 504) return true;

                var text = e.Message ?? "";
                if (text.Contains("userRateLimitExceeded", StringComparison.Ordinal) ||
                    text.Contains("rateLimitExceeded", StringComparison.Ordinal)) return true;
                if (Status.IsMatch(text)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// What makes a pass unsafe to plan at all, and what the delete warning
    /// in the window says. Pure, so both are tested directly.
    /// </summary>
    public static class SyncSafety
    {
        /// <summary>
        /// Why a pair must pause rather than sync, or null. A missing root is
        /// never planned against: every file in it would read as deleted.
        /// </summary>
        public static string? RootProblem(bool localFolderThere, bool driveFolderThere) =>
            !driveFolderThere ? "paused: its Google Drive folder is gone. Remove the sync or restore the folder."
            : !localFolderThere ? "paused: its PC folder is gone. Remove the sync or restore the folder."
            : null;

        /// <summary>
        /// The sync pairs a delete of <paramref name="paths"/> would cut an end
        /// off, and whether it is the Drive end. A pair is touched when a path is
        /// its PC folder or holds it, or is its Drive folder on the drive letter
        /// (by id, through the mount's map, never by walking) or holds it (its
        /// Drive path mapped onto the letter).
        /// </summary>
        public static List<(DriveSyncPair Pair, bool DriveSide)> PairsTouchedBy(
            IEnumerable<string> paths, IEnumerable<DriveSyncPair> pairs, string? driveLetter, Func<string, string?> idOf)
        {
            static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\');
            static bool AtOrUnder(string inner, string outer) =>
                inner.Equals(outer, StringComparison.OrdinalIgnoreCase) ||
                inner.StartsWith(outer + "\\", StringComparison.OrdinalIgnoreCase);

            string? letter = string.IsNullOrEmpty(driveLetter) ? null : Norm(driveLetter);
            var result = new List<(DriveSyncPair, bool)>();
            var list = pairs.ToList();

            foreach (var raw in paths)
            {
                var path = Norm(raw);
                bool onDrive = letter != null && AtOrUnder(path, letter);
                foreach (var pair in list)
                {
                    if (result.Any(r => r.Item1.Id == pair.Id)) continue;

                    if (!onDrive && pair.LocalFolder.Length > 0 && AtOrUnder(Norm(pair.LocalFolder), path))
                    {
                        result.Add((pair, false));
                        continue;
                    }

                    if (!onDrive) continue;
                    if (idOf(raw) is string id && id == pair.DriveFolderId)
                    {
                        result.Add((pair, true));
                        continue;
                    }

                    // "My Drive/Music" is G:\Music; a shared drive keeps its folder name.
                    var drivePath = pair.DriveFolderPath ?? "";
                    var rest = drivePath.StartsWith("My Drive", StringComparison.OrdinalIgnoreCase)
                        ? drivePath["My Drive".Length..].TrimStart('/')
                        : drivePath;
                    var mapped = rest.Length == 0 ? letter! : letter + "\\" + Norm(rest);
                    if (drivePath.Length > 0 && AtOrUnder(mapped, path)) result.Add((pair, true));
                }
            }
            return result;
        }

        /// <summary>
        /// The warning before a folder that is part of sync pairs is deleted
        /// in Explorer Native. One paragraph per pair, then the buttons do the rest.
        /// </summary>
        public static string DeleteWarning(IReadOnlyList<DriveSyncPair> pairs, bool deletingDriveSide)
        {
            var text = new System.Text.StringBuilder();
            foreach (var pair in pairs)
            {
                var name = string.IsNullOrWhiteSpace(pair.Name) ? pair.LocalFolder : pair.Name.Trim();
                text.Append($"This folder is part of the sync pair {name}. Deleting it follows that sync's settings. ")
                    .Append("Remove the sync first if you only want to delete it here. ");
                if (pair.CopyDeletes)
                    text.Append("If you delete it anyway, it is deleted on both sides: the other copy goes to ")
                        .Append(deletingDriveSide ? "the Recycle Bin." : "the Google Drive trash.");
                else
                    text.Append("If you delete it anyway, only this side is deleted; the other copy is kept.");
                text.Append("\n\n");
            }
            return text.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Disk space and resume arithmetic for the monitor, kept pure so it is
    /// tested directly. A disk is never filled past a margin: the larger of
    /// 2 gigabytes or 5 percent of the volume.
    /// </summary>
    public static class SyncSpace
    {
        public const long TwoGigabytes = 2L * 1024 * 1024 * 1024;

        public static long Margin(long volumeTotal) => Math.Max(TwoGigabytes, volumeTotal / 20);

        /// <summary>What can still be written while keeping the margin. Never negative.</summary>
        public static long Room(long free, long volumeTotal) => Math.Max(0, free - Margin(volumeTotal));

        public static bool Fits(long need, long free, long volumeTotal) => need <= Room(free, volumeTotal);

        /// <summary>How much more room <paramref name="need"/> would take; zero when it fits.</summary>
        public static long Shortfall(long need, long free, long volumeTotal) =>
            Math.Max(0, need - Room(free, volumeTotal));

        /// <summary>
        /// How many of these files, in order, can be written before the margin
        /// is reached. A pass stops at the first one that does not fit.
        /// </summary>
        public static int HowManyFit(IEnumerable<long> sizes, long free, long volumeTotal)
        {
            long room = Room(free, volumeTotal);
            int count = 0;
            foreach (var size in sizes)
            {
                if (size > room) break;
                room -= size;
                count++;
            }
            return count;
        }

        /// <summary>
        /// Where an interrupted download carries on from: the end of what is
        /// already on disk, if it is the same Drive file and no longer than it.
        /// Otherwise from the start.
        /// </summary>
        public static long ResumeOffset(long partialLength, long remoteSize, bool sameFile) =>
            sameFile && partialLength > 0 && partialLength <= remoteSize ? partialLength : 0;

        /// <summary>
        /// The quarter mark (25, 50 or 75) a pass has just passed, or zero.
        /// </summary>
        public static int QuarterPassed(long before, long after, long total)
        {
            if (total <= 0) return 0;
            int was = (int)(before * 4 / total), now = (int)(after * 4 / total);
            return now > was && now < 4 ? now * 25 : 0;
        }

        /// <summary>A pass this big is announced as it goes.</summary>
        public static bool IsBig(long bytes, int files) => bytes > 1024L * 1024 * 1024 || files > 200;
    }
}
