using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

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

        /// <summary>
        /// How often Drive is checked, in minutes: always 1, since one check is
        /// one small request to the changes feed however big the folder is. It
        /// was a setting ("Check Drive every") and is no longer saved, so an old
        /// 60 or "only when I press Sync now" is read as 1. Tests set 0 so that
        /// passes run only when asked.
        /// </summary>
        [JsonIgnore]
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
    /// Names compared the way both sides mean them: capitals ignored, and an
    /// accent written as one character the same as a letter followed by the
    /// accent on its own.
    ///
    /// macOS writes "café" the second way and Windows the first, so a library
    /// copied from a Mac had every accented name twice over — the PC's sent up
    /// beside Drive's, and Drive's brought down beside the PC's. The names are
    /// kept as each side spells them; only the comparing is normalised.
    /// </summary>
    public sealed class SyncNameComparer : IEqualityComparer<string>, IComparer<string>
    {
        public static readonly SyncNameComparer Instance = new();

        /// <summary>
        /// The name in composed form (NFC), by Windows itself. The shipped app
        /// runs with InvariantGlobalization, where string.Normalize leaves NFD
        /// alone, so "Cafe" + U+0301 (as a Mac writes it) and the composed
        /// "Café" were two names: each side got a second copy of the other.
        /// A name with nothing at or above U+0300 has no combining mark to
        /// compose and is already NFC, which is almost every name, for free.
        /// </summary>
        internal static string Key(string name)
        {
            bool plain = true;
            foreach (var c in name) if (c >= 0x0300) { plain = false; break; }
            if (plain) return name;

            try
            {
                const int NormalizationC = 1;
                int size = NormalizeString(NormalizationC, name, name.Length, null, 0);
                for (int attempt = 0; attempt < 3 && size > 0; attempt++)
                {
                    var buffer = new char[size];
                    int written = NormalizeString(NormalizationC, name, name.Length, buffer, buffer.Length);
                    if (written > 0) return new string(buffer, 0, written);
                    // ERROR_INSUFFICIENT_BUFFER: the first answer was an estimate.
                    if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 122) break;
                    size = Math.Max(-written, buffer.Length * 2);
                }
            }
            catch { }
            return name;    // half a surrogate pair, or Windows refused: compared as it is
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern int NormalizeString(int form, string source, int sourceLength,
            char[]? destination, int destinationLength);

        public bool Equals(string? a, string? b) =>
            a == null || b == null ? a == b : StringComparer.OrdinalIgnoreCase.Equals(Key(a), Key(b));

        public int GetHashCode(string name) => StringComparer.OrdinalIgnoreCase.GetHashCode(Key(name));

        public int Compare(string? a, string? b) =>
            a == null || b == null ? string.CompareOrdinal(a, b) : StringComparer.OrdinalIgnoreCase.Compare(Key(a), Key(b));
    }

    /// <summary>
    /// What one side holds and leaves out of the sync: a hidden or system
    /// file on the PC, a folder that could not be read, a Drive name that
    /// clashes with another. Present, so never mistaken for deleted.
    /// </summary>
    public sealed class SyncSkips
    {
        private readonly HashSet<string> _files = new(SyncNameComparer.Instance);
        private readonly List<string> _folders = new();

        public void File(string path) => _files.Add(path);

        /// <summary>Everything under a folder, by its relative path ("" for the whole pair).</summary>
        public void Folder(string relative) =>
            _folders.Add(relative.Length == 0 ? "" : SyncNameComparer.Key(relative.TrimEnd('/')) + "/");

        public bool IsEmpty => _files.Count == 0 && _folders.Count == 0;

        /// <summary>How many files and folders are left out.</summary>
        public int Count => _files.Count + _folders.Count;

        /// <summary>Everything the other side's scan left out as well.</summary>
        public void Include(SyncSkips other)
        {
            _files.UnionWith(other._files);
            _folders.AddRange(other._folders);
        }

        /// <summary>
        /// Whether anything at or under <paramref name="folder"/> is left out, so
        /// moving the folder whole would carry along things the sync never saw.
        /// </summary>
        public bool AnyUnder(string folder)
        {
            if (folder.Length == 0) return !IsEmpty;
            var key = SyncNameComparer.Key(folder.TrimEnd('/')) + "/";
            foreach (var f in _folders)
                if (f.Length == 0 || f.StartsWith(key, StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith(f, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var f in _files)
                if (SyncNameComparer.Key(f).StartsWith(key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public bool Covers(string path)
        {
            if (_files.Contains(path)) return true;
            if (_folders.Count == 0) return false;
            var key = SyncNameComparer.Key(path);
            foreach (var folder in _folders)
                if (folder.Length == 0 || key.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

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
        internal static bool RemoteChanged(SyncFile now, SyncBase was)
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

        /// <summary>Whether the PC's file is no longer what the last sync left there.</summary>
        public static bool LocalChanged(SyncFile now, SyncBase was) => Changed(now, was.LocalSize, was.LocalModifiedUtc);

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
            // The small link files Google Drive for desktop leaves for Docs,
            // Sheets and the rest. The documents themselves are not files (the
            // Drive scan leaves them out), so these would go up as junk copies.
            if (Array.IndexOf(GoogleLinkExtensions, Path.GetExtension(name).ToLowerInvariant()) >= 0) return true;
            return false;
        }

        private static readonly string[] GoogleLinkExtensions =
        {
            ".gdoc", ".gsheet", ".gslides", ".gdraw", ".gform", ".gtable", ".gmap", ".gsite", ".gjam",
            ".glink", ".gscript", ".gnote", ".gvid",
        };

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
        /// deleting it. The same goes for anything in <paramref name="skipped"/>:
        /// a hidden file on the PC is there, just not synced, and taking it for
        /// deleted sent the Drive copy to the trash.
        /// </summary>
        public static List<SyncAction> Plan(
            SyncRules rules,
            IReadOnlyDictionary<string, SyncFile> local,
            IReadOnlyDictionary<string, SyncFile> remote,
            IReadOnlyDictionary<string, SyncBase> last,
            IReadOnlyDictionary<string, string>? localMd5 = null,
            SyncSkips? skipped = null)
        {
            var mode = rules.Mode;
            bool copyDeletes = rules.CopyDeletes;
            bool up = mode != SyncMode.DownloadOnly;
            bool down = mode != SyncMode.UploadOnly;

            // The PC's spelling first, so a path both sides hold under two
            // spellings is acted on under the one that finds the PC's file.
            var paths = new SortedSet<string>(SyncNameComparer.Instance);
            paths.UnionWith(local.Keys);
            paths.UnionWith(remote.Keys);
            paths.UnionWith(last.Keys);

            var actions = new List<SyncAction>();
            void Do(SyncActionKind kind, string path) => actions.Add(new SyncAction(kind, path));

            foreach (var path in paths)
            {
                if (IsSkippedPath(path)) continue;
                if (skipped != null && skipped.Covers(path)) continue;
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

    /// <summary>
    /// A file that moved or was renamed on one side since the last sync, to be
    /// moved the same way on the other: <see cref="OnDrive"/> means the PC's
    /// file moved and Drive's copy follows (one request, the same Drive id);
    /// otherwise Drive's moved and the PC's file follows.
    /// </summary>
    public sealed record SyncMove(string From, string To, bool OnDrive, SyncBase Was);

    /// <summary>
    /// Moves and renames, found in a plan that would otherwise delete a file in
    /// one place and copy it again in another. A renamed folder of a hundred
    /// thousand files was a hundred thousand uploads and, with deletes not
    /// copied, both copies on both sides for good.
    ///
    /// Never a guess. A move on Drive is the same Drive id at a new path, which
    /// is certain. A move on the PC is matched against what the last sync
    /// recorded: the same size and the same modified time (a move keeps both),
    /// only where exactly one vanished file and one new file fit, or among
    /// several alike the one with the same name, and the engine then checks the
    /// new file's MD5 against Drive's before moving anything. Whatever is unsure
    /// is left to the plan as it was.
    /// </summary>
    public static class SyncMoves
    {
        public static List<SyncMove> Find(SyncRules rules, IReadOnlyList<SyncAction> plan,
            IReadOnlyDictionary<string, SyncFile> local, IReadOnlyDictionary<string, SyncFile> remote,
            IReadOnlyDictionary<string, SyncBase> last)
        {
            var moves = new List<SyncMove>();
            var kinds = new Dictionary<string, SyncActionKind>(SyncNameComparer.Instance);
            foreach (var a in plan) kinds[a.Path] = a.Kind;
            bool up = rules.Mode != SyncMode.DownloadOnly, down = rules.Mode != SyncMode.UploadOnly;

            // ---- moved on Drive: the same id at a new path ----
            if (down)
            {
                var gone = new Dictionary<string, (string Path, SyncBase Was)>(StringComparer.Ordinal);
                var twice = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (path, was) in last)
                {
                    if (string.IsNullOrEmpty(was.RemoteId) || remote.ContainsKey(path)) continue;
                    if (!local.TryGetValue(path, out var l) || SyncPlanner.LocalChanged(l, was)) continue;
                    if (!kinds.TryGetValue(path, out var k) ||
                        k is not (SyncActionKind.DeleteLocal or SyncActionKind.Upload or SyncActionKind.Forget)) continue;
                    if (!gone.TryAdd(was.RemoteId, (path, was))) twice.Add(was.RemoteId);
                }
                foreach (var id in twice) gone.Remove(id);
                if (gone.Count > 0)
                    foreach (var (path, r) in remote)
                    {
                        if (r.Id == null || !gone.TryGetValue(r.Id, out var from)) continue;
                        if (last.ContainsKey(path) || local.ContainsKey(path)) continue;
                        if (!kinds.TryGetValue(path, out var k) || k != SyncActionKind.Download) continue;
                        if (SyncPlanner.RemoteChanged(r, from.Was)) continue;      // moved and edited: left to the plan
                        moves.Add(new SyncMove(from.Path, path, OnDrive: false, from.Was));
                        gone.Remove(r.Id);
                    }
            }

            // ---- moved on the PC: a vanished file and a new one that are the same file ----
            if (up)
            {
                var vanished = new Dictionary<(long, DateTime), List<string>>();
                foreach (var (path, was) in last)
                {
                    if (local.ContainsKey(path) || string.IsNullOrEmpty(was.RemoteId)) continue;
                    if (!remote.TryGetValue(path, out var r) || r.Id != was.RemoteId || SyncPlanner.RemoteChanged(r, was)) continue;
                    if (!kinds.TryGetValue(path, out var k) ||
                        k is not (SyncActionKind.DeleteRemote or SyncActionKind.Download or SyncActionKind.Forget)) continue;
                    var key = (was.LocalSize, was.LocalModifiedUtc);
                    if (!vanished.TryGetValue(key, out var list)) vanished[key] = list = new List<string>();
                    list.Add(path);
                }
                if (vanished.Count > 0)
                {
                    var arrived = new Dictionary<(long, DateTime), List<string>>();
                    foreach (var (path, l) in local)
                    {
                        if (last.ContainsKey(path) || remote.ContainsKey(path)) continue;
                        if (!kinds.TryGetValue(path, out var k) || k != SyncActionKind.Upload) continue;
                        var key = (l.Size, l.ModifiedUtc);
                        if (!vanished.ContainsKey(key)) continue;
                        if (!arrived.TryGetValue(key, out var list)) arrived[key] = list = new List<string>();
                        list.Add(path);
                    }
                    foreach (var (key, to) in arrived)
                    {
                        var from = vanished[key];
                        if (from.Count == 1 && to.Count == 1)
                        {
                            moves.Add(new SyncMove(from[0], to[0], OnDrive: true, last[from[0]]));
                            continue;
                        }
                        // Several alike (files written in one go): paired by name,
                        // and only where that name is unique on both sides.
                        var fromByName = from.GroupBy(Leaf, SyncNameComparer.Instance)
                            .Where(g => g.Count() == 1)
                            .ToDictionary(g => g.Key, g => g.First(), SyncNameComparer.Instance);
                        foreach (var g in to.GroupBy(Leaf, SyncNameComparer.Instance))
                            if (g.Count() == 1 && fromByName.TryGetValue(g.Key, out var f))
                                moves.Add(new SyncMove(f, g.First(), OnDrive: true, last[f]));
                    }
                }
            }
            return moves;
        }

        public static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

        public static string Parent(string path) => path.LastIndexOf('/') is var at and > 0 ? path[..at] : "";

        /// <summary>Whether a path is somewhere under a folder ("" holds everything), as the names comparer sees it.</summary>
        public static bool IsUnder(string path, string folder) =>
            folder.Length == 0 ||
            (path.Length > folder.Length && path[folder.Length] == '/' &&
             SyncNameComparer.Instance.Equals(path[..folder.Length], folder));

        /// <summary>
        /// The folder a move really renames: "a/b/c.txt" to "x/b/c.txt" renames
        /// "a" to "x", and "a/c.txt" to "x/y/c.txt" moves "a" to "x/y". Null when
        /// the file's own name changed, or nothing above it did.
        /// </summary>
        public static (string From, string To)? FolderOf(SyncMove move)
        {
            var from = move.From.Split('/');
            var to = move.To.Split('/');
            int i = from.Length - 1, j = to.Length - 1;
            if (!SyncNameComparer.Instance.Equals(from[i], to[j])) return null;
            while (i > 0 && j > 0 && SyncNameComparer.Instance.Equals(from[i - 1], to[j - 1])) { i--; j--; }
            if (i == 0 || j == 0) return null;   // the difference is not a whole folder on both sides
            return (string.Join('/', from[..i]), string.Join('/', to[..j]));
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
        /// <summary>
        /// How long to wait out a daily limit: an hour, not the five-minute cap,
        /// because asking every five minutes is how a day's limit stays spent.
        /// </summary>
        public static readonly TimeSpan DailyLimitWait = TimeSpan.FromHours(1);

        /// <summary>Google's per-day limit for the account, in either of the forms it arrives in.</summary>
        public static bool IsDailyLimit(Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
            {
                if (e is DriveQuotaException { ResetsOnItsOwn: true }) return true;
                if (e is DriveClient.DriveStatusException { Status: 403, Reason: "dailyLimitExceeded" }) return true;
            }
            return false;
        }

        public static bool IsTransient(Exception error)
        {
            // Never retried: a resend could be a second copy. See UnknownIfLandedException.
            if (DriveClient.UnknownIfLandedException.In(error)) return false;
            for (var e = error; e != null; e = e.InnerException)
            {
                if (e is DriveQuotaException quota) return quota.ResetsOnItsOwn;

                // A day's limit resets by itself. As a plain refusal it failed
                // the pair, which then said "could not sync" until tomorrow.
                if (e is DriveClient.DriveStatusException { Status: 403, Reason: "dailyLimitExceeded" }) return true;

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
                    var id = idOf(raw);
                    if (id != null && id == pair.DriveFolderId)
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

                    // The saved path is only a reading aid and goes stale when the
                    // folder is renamed or moved. A folder now at that very path
                    // whose id is known and is another one is not the pair's;
                    // a folder above it still holds it, whatever its id.
                    bool exactly = mapped.Equals(path, StringComparison.OrdinalIgnoreCase);
                    if (exactly && id != null) continue;
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

    /// <summary>
    /// What is moving right now for one pair, and how fast each way: the files
    /// started and not yet finished, and the bytes of the last few seconds.
    /// Thread-safe; times are Environment.TickCount64 milliseconds.
    /// </summary>
    internal sealed class SyncLive
    {
        /// <summary>Speeds are worked out over this many milliseconds.</summary>
        internal const long WindowMs = 5000;

        internal sealed record Moving(string Path, bool Up, long Done, long Size);

        internal sealed record Snapshot(long UpPerSecond, long DownPerSecond, IReadOnlyList<Moving> Files);

        private sealed class Entry { public bool Up; public long Done, Size, Started; }

        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _active = new(StringComparer.Ordinal);
        private readonly Queue<(long At, long Up, long Down)> _samples = new();
        private long _up, _down, _lastSample = long.MinValue;

        public void Begin(string path, bool up, long size, long now)
        {
            lock (_gate) _active[path] = new Entry { Up = up, Size = size, Started = now };
        }

        public void Add(string path, bool up, long bytes, long now)
        {
            lock (_gate)
            {
                // Sampled first, so the window starts before these bytes, not after.
                Sample(now);
                if (_active.TryGetValue(path, out var entry)) entry.Done = Math.Min(entry.Size, entry.Done + bytes);
                if (up) _up += bytes; else _down += bytes;
            }
        }

        public void End(string path, long now)
        {
            lock (_gate) { _active.Remove(path); Sample(now); }
        }

        /// <summary>Speeds each way over the last few seconds, and the files moving, oldest first.</summary>
        public Snapshot Read(long now)
        {
            lock (_gate)
            {
                Sample(now, force: true);
                var (at, up, down) = _samples.Peek();
                double seconds = Math.Max(1000, now - at) / 1000.0;
                var files = _active.OrderBy(e => e.Value.Started)
                    .Select(e => new Moving(e.Key, e.Value.Up, e.Value.Done, e.Value.Size)).ToList();
                return new Snapshot((long)((_up - up) / seconds), (long)((_down - down) / seconds), files);
            }
        }

        private void Sample(long now, bool force = false)
        {
            if (force || now - _lastSample >= 250)
            {
                _samples.Enqueue((now, _up, _down));
                _lastSample = now;
            }
            while (_samples.Count > 1 && now - _samples.Peek().At > WindowMs) _samples.Dequeue();
        }
    }

    /// <summary>
    /// How many uploads run at once, found rather than fixed. Measured on the
    /// real account: 4 at a time gave 4.6 files a second, 8 gave 8.2, 16 gave
    /// 15.6, and past about 30 across the account Google stalled requests and
    /// the rate fell to 6.7. So it starts at a safe width, adds one lane after a
    /// run of fast answers, and gives one back at once on a slow answer or a
    /// failure (additive increase, multiplicative-ish decrease, one lane at a
    /// time). Thread-safe.
    /// </summary>
    internal sealed class AdaptiveLanes
    {
        private readonly SemaphoreSlim _gate;
        private readonly int _min, _max;
        private readonly object _lock = new();
        private int _limit, _good;

        public AdaptiveLanes(int start, int min, int max)
        {
            _min = Math.Max(1, min);
            _max = Math.Max(_min, max);
            _limit = Math.Clamp(start, _min, _max);
            _gate = new SemaphoreSlim(_limit, _max);
        }

        public int Limit { get { lock (_lock) return _limit; } }

        public Task EnterAsync(CancellationToken token) => _gate.WaitAsync(token);

        /// <summary>Ends one upload. <paramref name="fast"/>: it landed quickly, with no push-back.</summary>
        public void Leave(bool fast)
        {
            lock (_lock)
            {
                if (fast)
                {
                    if (++_good >= _limit && _limit < _max)
                    {
                        _limit++;
                        _good = 0;
                        _gate.Release();
                    }
                }
                else
                {
                    _good = 0;
                    if (_limit > _min)
                    {
                        _limit--;
                        return;     // this lane's permit is kept back: one fewer from now on
                    }
                }
            }
            _gate.Release();
        }
    }
}
