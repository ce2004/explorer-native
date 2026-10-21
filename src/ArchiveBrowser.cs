using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>One file or folder inside an archive, as the pane shows it.</summary>
    public sealed class ArchiveNode
    {
        public ArchiveNode(string name, string innerPath, bool isDirectory, long size, DateTime modified)
        {
            Name = name;
            InnerPath = innerPath;
            IsDirectory = isDirectory;
            Size = size;
            Modified = modified;
        }

        /// <summary>The last segment: what the Name column says.</summary>
        public string Name { get; }

        /// <summary>Where it is inside the archive, forward slashes, no leading slash; "" is the top.</summary>
        public string InnerPath { get; }

        public bool IsDirectory { get; }

        /// <summary>A file's uncompressed size; a folder's is everything below it.</summary>
        public long Size { get; internal set; }

        public DateTime Modified { get; internal set; }
    }

    /// <summary>
    /// What is inside one archive, read once and held as a tree of folders, so
    /// moving about inside it is a dictionary lookup and never a read.
    /// </summary>
    public sealed class ArchiveListing
    {
        private readonly Dictionary<string, ArchiveNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ArchiveNode>> _children = new(StringComparer.OrdinalIgnoreCase);

        internal ArchiveListing(string archivePath, ArchiveFormat format, string? tree, string key)
        {
            ArchivePath = archivePath;
            Format = format;
            Tree = tree;
            Key = key;
            _children[""] = new List<ArchiveNode>();
        }

        public string ArchivePath { get; }
        public ArchiveFormat Format { get; }

        /// <summary>
        /// For the formats only Windows' tar can read: the folder the whole
        /// archive was extracted into, which is where every name came from.
        /// Null for zip and tar, which are read directly.
        /// </summary>
        public string? Tree { get; }

        /// <summary>Which archive, at which size and date: what the cache is keyed by.</summary>
        internal string Key { get; }

        /// <summary>Entries left out because their names point outside the archive or cannot exist on Windows.</summary>
        public int Unsafe { get; private set; }

        /// <summary>Whether the archive stopped making sense part way, so only the start of it is shown.</summary>
        public bool Damaged { get; internal set; }

        /// <summary>Why only part of it could be taken out (Windows' tar crashed or stalled), or null.</summary>
        public string? Partial { get; internal set; }

        /// <summary>Something worth saying once the archive is open, or null.</summary>
        public string? Warning
        {
            get
            {
                var parts = new List<string>();
                if (Partial != null) parts.Add($"Only part of this archive is shown. {Partial.TrimEnd('.')}.");
                else if (Damaged) parts.Add("This archive is damaged, so only part of it is shown.");
                if (Unsafe > 0)
                    parts.Add($"{NameRules.Items(Unsafe)} with {(Unsafe == 1 ? "a name" : "names")} that would land " +
                              $"outside the archive or that Windows cannot store {(Unsafe == 1 ? "is" : "are")} not shown.");
                return parts.Count == 0 ? null : string.Join(" ", parts);
            }
        }

        public int Count => _nodes.Count;

        /// <summary>What is directly inside a folder of the archive; empty for anything that is not one.</summary>
        public IReadOnlyList<ArchiveNode> Children(string inner) =>
            _children.TryGetValue(inner, out var list) ? list : Array.Empty<ArchiveNode>();

        public ArchiveNode? Find(string inner) => _nodes.TryGetValue(inner, out var node) ? node : null;

        public bool IsFolder(string inner) => inner.Length == 0 || _nodes.TryGetValue(inner, out var node) && node.IsDirectory;

        /// <summary>Every file and folder below a folder, the folder itself not included.</summary>
        public IEnumerable<ArchiveNode> Below(string inner)
        {
            var stack = new Stack<string>();
            stack.Push(inner);
            while (stack.Count > 0)
            {
                foreach (var child in Children(stack.Pop()))
                {
                    yield return child;
                    if (child.IsDirectory) stack.Push(child.InnerPath);
                }
            }
        }

        /// <summary>Bytes, files and folders below a folder of the archive, counted from the listing.</summary>
        public (long Bytes, int Files, int Folders) Measure(string inner)
        {
            long bytes = 0;
            int files = 0, folders = 0;
            foreach (var node in Below(inner))
            {
                if (node.IsDirectory) folders++;
                else { files++; bytes += Math.Max(0, node.Size); }
            }
            return (bytes, files, folders);
        }

        /// <summary>
        /// Builds the tree from an archive's entries.
        ///
        /// Folders an archive never names — most zips only list files — are
        /// made from the paths of what is inside them. A name that could only be
        /// extracted by writing outside the folder, or that Windows cannot hold,
        /// is not shown at all: it could never be opened or copied out, and a row
        /// that can only fail is a row heard for nothing. They are counted, and
        /// the count is said.
        /// </summary>
        internal static ArchiveListing Build(string archivePath, ArchiveFormat format, string? tree, string key,
            IEnumerable<ArchiveEntryInfo> entries, CancellationToken token)
        {
            var listing = new ArchiveListing(archivePath, format, tree, key);

            // Any folder will do: SafeTarget only resolves names against it.
            var fence = Path.Combine(Path.GetTempPath(), "archive-fence");

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();

                // Judged as extraction judges it: cleaned first, so the "./" that
                // `tar -czf x.tgz .` puts on every name is not taken for a name
                // pointing outside — every entry of such an archive was hidden
                // and counted as unsafe, and it opened as an empty folder. A
                // network path is refused by its raw form, which cleaning loses.
                var name = ExtractionRules.Normalise(entry.Name);
                if (entry.Name.Replace('\\', '/').StartsWith("//", StringComparison.Ordinal))
                {
                    listing.Unsafe++;
                    continue;
                }
                if (name.Length == 0) continue;

                if (ArchiveEngine.SafeTarget(fence, name, out var reason) == null)
                {
                    if (reason != null) listing.Unsafe++;
                    continue;
                }

                // A name nested thousands deep is no real file: Windows could
                // never extract it, and every level of it was a string kept per
                // prefix (half a gigabyte from one 32KB name) and a frame in the
                // folder total, which overflowed the stack and ended the app.
                if (name.Length > MaxNameLength || name.Count(c => c == '/') > MaxDepth)
                {
                    listing.Unsafe++;
                    continue;
                }

                // Every folder on the way down, made if the archive did not say.
                int slash = -1;
                bool blocked = false;
                while ((slash = name.IndexOf('/', slash + 1)) >= 0)
                {
                    if (listing.EnsureFolder(name[..slash]) == null) { blocked = true; break; }
                }
                if (blocked) continue;

                if (entry.IsDirectory)
                {
                    var folder = listing.EnsureFolder(name);
                    if (folder != null && folder.Modified == default) folder.Modified = entry.Modified;
                    continue;
                }

                // The first of two entries with one name is the one kept, as
                // extracting keeps it.
                if (listing._nodes.ContainsKey(name)) continue;

                listing.Add(new ArchiveNode(LastSegment(name), name, false, Math.Max(0, entry.Size), entry.Modified));
            }

            listing.TotalFolders("");
            return listing;
        }

        private ArchiveNode? EnsureFolder(string inner)
        {
            if (_nodes.TryGetValue(inner, out var existing)) return existing.IsDirectory ? existing : null;
            var node = new ArchiveNode(LastSegment(inner), inner, true, 0, default);
            Add(node);
            _children[inner] = new List<ArchiveNode>();
            return node;
        }

        private void Add(ArchiveNode node)
        {
            _nodes[node.InnerPath] = node;
            var parent = ArchiveBrowser.ParentOf(node.InnerPath);
            if (!_children.TryGetValue(parent, out var siblings))
                _children[parent] = siblings = new List<ArchiveNode>();
            siblings.Add(node);
        }

        /// <summary>The deepest and longest member names a listing takes; see Build.</summary>
        internal const int MaxDepth = 512, MaxNameLength = 4096;

        /// <summary>
        /// Every folder's size, as the total of what is under it. Walked with a
        /// stack of its own, not by recursion, so no archive can run the
        /// thread's stack out.
        /// </summary>
        private long TotalFolders(string inner)
        {
            var order = new List<ArchiveNode>();
            var pending = new Stack<string>();
            pending.Push(inner);
            while (pending.Count > 0)
            {
                foreach (var child in Children(pending.Pop()))
                    if (child.IsDirectory) { order.Add(child); pending.Push(child.InnerPath); }
            }
            // Deepest first, so each folder's children are already totalled.
            for (int i = order.Count - 1; i >= 0; i--)
                order[i].Size = Children(order[i].InnerPath).Sum(c => c.Size);
            return Children(inner).Sum(c => c.Size);
        }

        private static string LastSegment(string inner)
        {
            int slash = inner.LastIndexOf('/');
            return slash < 0 ? inner : inner[(slash + 1)..];
        }
    }

    /// <summary>
    /// Opening an archive as though it were a folder: reading what is in it,
    /// and taking single members out of it, without extracting the rest.
    ///
    /// Zip is read from its central directory and tar from its headers, through
    /// <see cref="ZipEngine"/>'s reader and System.Formats.Tar, so a listing
    /// never decompresses a file's data (a .tar.gz has to be decompressed to
    /// reach its headers, which is the format, not a choice).
    ///
    /// The formats only Windows' tar can read are different, and the reason is
    /// in <see cref="BsdTar"/>: libarchive loses every name outside ASCII
    /// wherever it has to print one, so its listing cannot be trusted for the
    /// one thing a listing is for. The only names that survive are the ones it
    /// writes to disk. So a 7z, .tar.xz, rar or iso is extracted whole, once, into
    /// a cache folder, and browsed from there; everything it holds is then on
    /// disk and opening or copying a member out of it is immediate.
    /// </summary>
    public static class ArchiveBrowser
    {
        /// <summary>What is said for anything that would change an archive.</summary>
        public const string ReadOnlyMessage = "Archives are read-only here";

        /// <summary>
        /// Whether Enter should open this file as a folder. By name only, like
        /// every decision made on a keystroke: the bytes are read when it opens.
        /// A bare .gz or .xz wraps one file with no folders and is opened in its
        /// own app instead.
        ///
        /// Not a disc image either. Windows mounts an .iso as a drive of its own
        /// on Enter, which reads only what is opened; browsing it here would mean
        /// extracting gigabytes through Windows' tar first.
        /// </summary>
        public static bool CanBrowse(string? path) =>
            ArchiveFormats.FromName(path) is { CanExtract: true, SingleFile: false } format &&
            format != ArchiveFormats.Iso;

        /// <summary>The path a folder inside an archive is shown as: "C:\Music\album.zip\Disc 1".</summary>
        public static string VirtualPath(string archivePath, string inner) =>
            inner.Length == 0 ? archivePath : archivePath + "\\" + inner.Replace('/', '\\');

        /// <summary>
        /// Where <paramref name="path"/> is inside the archive at
        /// <paramref name="archivePath"/>: "" for the archive itself, null when it
        /// is not inside it at all.
        /// </summary>
        public static string? InnerPathOf(string archivePath, string? path)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(archivePath)) return null;
            var trimmed = path.Replace('/', '\\').TrimEnd('\\');
            var archive = archivePath.TrimEnd('\\');
            if (string.Equals(trimmed, archive, StringComparison.OrdinalIgnoreCase)) return "";
            if (!trimmed.StartsWith(archive + "\\", StringComparison.OrdinalIgnoreCase)) return null;
            var inner = trimmed[(archive.Length + 1)..].Replace('\\', '/');
            return ExtractionRules.Normalise(inner);
        }

        /// <summary>The folder an inner path is in; "" at the top.</summary>
        public static string ParentOf(string inner)
        {
            int slash = inner.LastIndexOf('/');
            return slash < 0 ? "" : inner[..slash];
        }

        // ---------- Reading what is inside ----------

        /// <summary>
        /// Reads an archive's contents. On a worker: it is a read of the file and,
        /// for the formats Windows' tar handles, a whole extraction.
        /// </summary>
        /// <exception cref="NotSupportedException">Not an archive this can open, or a protected zip.</exception>
        /// <exception cref="InvalidDataException">Damaged beyond reading anything.</exception>
        public static ArchiveListing Open(string archivePath, IProgress<TransferProgress>? progress,
            CancellationToken token)
        {
            var name = Path.GetFileName(archivePath);
            var format = ArchiveFormats.Identify(archivePath);

            if (format == null)
                throw new NotSupportedException($"{name} is not an archive this can open.");
            if (!format.CanExtract || format.SingleFile)
                throw new NotSupportedException(
                    $"{name} holds one compressed file rather than folders, so it cannot be opened as a folder.");

            var key = KeyFor(archivePath);

            switch (format.Engine)
            {
                case ArchiveEngineKind.Zip:
                {
                    IReadOnlyList<ArchiveEntryInfo> entries;
                    int files, locked;
                    // Opened exactly: inside "Backup." the ordinary path is "Backup\...",
                    // and the archive would not open. Kept plain for the listing's paths.
                    try { entries = ZipEngine.List(NameRules.ExactPath(archivePath), token, out files, out locked); }
                    catch (InvalidDataException)
                    {
                        throw new InvalidDataException($"{name} is damaged or incomplete, so it cannot be opened.");
                    }

                    // Every file locked is a protected zip, said once, rather
                    // than a folder of rows that each fail on Enter.
                    if (locked > 0 && locked == files) throw new NotSupportedException(ZipEngine.ProtectedMessage);
                    return ArchiveListing.Build(archivePath, format, null, key, entries, token);
                }

                case ArchiveEngineKind.Tar:
                {
                    using var file = new FileStream(NameRules.ExactPath(archivePath), FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);

                    // A .tar.gz has no index; reaching the last header is
                    // decompressing everything before it. The progress is how
                    // far through the file that has got.
                    var reporter = new ArchiveEngine.Reporter(progress);
                    reporter.Estimate(file.Length, () => file.Position);
                    using var counted = new TickingStream(file, () => reporter.Advance(0));

                    IReadOnlyList<ArchiveEntryInfo> entries;
                    bool damaged;
                    if (format.Id == "tar.gz")
                    {
                        using var gzip = new GZipStream(counted, CompressionMode.Decompress, leaveOpen: true);
                        entries = TarEngine.ReadIndex(gzip, token, out damaged);
                    }
                    else entries = TarEngine.ReadIndex(counted, token, out damaged);

                    if (damaged && entries.Count == 0)
                        throw new InvalidDataException(
                            $"{name} is damaged, or is not really a {format.Name} archive, so it cannot be opened.");

                    var listing = ArchiveListing.Build(archivePath, format, null, key, entries, token);
                    listing.Damaged = damaged;
                    return listing;
                }

                default:
                    return OpenThroughTree(archivePath, format, key, progress, token);
            }
        }

        /// <summary>
        /// The bsdtar formats: extracted whole into the cache, once per archive
        /// per session, and listed from the disk — the only place their names
        /// are right. See the class summary.
        /// </summary>
        private static ArchiveListing OpenThroughTree(string archivePath, ArchiveFormat format, string key,
            IProgress<TransferProgress>? progress, CancellationToken token)
        {
            var tree = Path.Combine(CacheRoot, "tree-" + Hash(key));
            var finished = tree + ".done";

            // One extraction per archive at a time. A second open (the other
            // tab, or Enter again after stepping away from a slow 7z) used to
            // delete the folder the first was still writing into.
            var gate = TreeGates.GetOrAdd(tree, new SemaphoreSlim(1, 1));
            gate.Wait(token);
            try { return OpenThroughTreeLocked(archivePath, format, key, tree, finished, progress, token); }
            finally { gate.Release(); }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> TreeGates =
            new(StringComparer.OrdinalIgnoreCase);

        private static ArchiveListing OpenThroughTreeLocked(string archivePath, ArchiveFormat format, string key,
            string tree, string finished, IProgress<TransferProgress>? progress, CancellationToken token)
        {
            bool damaged = false;
            string? partial = null;
            if (!File.Exists(finished) || !Directory.Exists(tree))
            {
                if (Directory.Exists(tree)) DeleteTree(tree);
                Directory.CreateDirectory(tree);

                var reporter = new ArchiveEngine.Reporter(progress);
                var errors = new List<string>();
                try
                {
                    var (done, _) = BsdTar.Extract(archivePath, tree, PasteConflictPolicy.AutoRename, null,
                        reporter, errors, token);
                    token.ThrowIfCancellationRequested();

                    // The reason, not a per-entry line ("name: not extracted ..."):
                    // a crash used to be said as one file's line, or as a
                    // temporary folder that could not be removed.
                    var reason = errors.FirstOrDefault(e => !e.Contains(": ", StringComparison.Ordinal)) ??
                                 errors.FirstOrDefault();
                    if (done == 0 && reason != null)
                        throw new InvalidDataException(reason.TrimEnd('.') + ".");
                    damaged = errors.Any(e => e.Contains("damaged", StringComparison.OrdinalIgnoreCase));
                    if (!damaged && reason != null) partial = reason;
                }
                catch
                {
                    try { DeleteTree(tree); } catch { }
                    throw;
                }

                // A tree tar did not finish is never kept as the whole archive:
                // shown once with the reason, and taken out again next time.
                if (partial == null) File.WriteAllText(finished, damaged ? "damaged" : "");
            }
            else
            {
                try { damaged = File.ReadAllText(finished) == "damaged"; } catch { }
            }

            var listing = ArchiveListing.Build(archivePath, format, tree, key, WalkTree(tree, token), token);
            listing.Damaged = damaged;
            listing.Partial = partial;
            return listing;
        }

        /// <summary>A folder on disk as archive entries, names straight from the disk. Links are not followed.</summary>
        internal static IEnumerable<ArchiveEntryInfo> WalkTree(string root, CancellationToken token)
        {
            var stack = new Stack<(DirectoryInfo Folder, string Prefix)>();
            stack.Push((new DirectoryInfo(root), ""));
            while (stack.Count > 0)
            {
                var (folder, prefix) = stack.Pop();
                foreach (var child in folder.EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                    var name = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
                    if (child is DirectoryInfo sub)
                    {
                        yield return new ArchiveEntryInfo(name, 0, sub.LastWriteTime, true);
                        stack.Push((sub, name));
                    }
                    else if (child is FileInfo file)
                    {
                        yield return new ArchiveEntryInfo(name, file.Length, file.LastWriteTime, false);
                    }
                }
            }
        }

        // ---------- Taking members out ----------

        private static readonly ConcurrentDictionary<string, string> _opened = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// One file out of the archive, onto disk, for the player or the file's
        /// own app. Returns where it is. The same file asked for twice in a
        /// session is taken out once.
        /// </summary>
        public static string ExtractOne(ArchiveListing listing, string inner,
            IProgress<TransferProgress>? progress, CancellationToken token)
        {
            var node = listing.Find(inner);
            if (node == null || node.IsDirectory)
                throw new FileNotFoundException("it is no longer in the archive");

            if (listing.Tree != null)
            {
                var onDisk = ArchiveEngine.SafeTarget(listing.Tree, inner, out var reason)
                             ?? throw new IOException(reason ?? "it cannot be reached");
                if (!File.Exists(onDisk)) throw new FileNotFoundException("it is no longer in the archive");
                return onDisk;
            }

            var key = listing.Key + "|" + inner;
            if (_opened.TryGetValue(key, out var earlier) && File.Exists(earlier)) return earlier;

            var folder = NewCacheFolder("open");
            var errors = new List<string>();
            ExtractInto(listing, new[] { inner }, folder, progress, errors, token);

            var target = Path.Combine(folder, node.Name);
            if (!File.Exists(target))
            {
                try { DeleteTree(folder); } catch { }
                throw new IOException(errors.Count > 0 ? StripName(errors[0], inner) : "it could not be taken out of the archive");
            }

            _opened[key] = target;
            return target;
        }

        /// <summary>
        /// The chosen members, folders with everything in them, into
        /// <paramref name="destination"/> under their own names. Returns the
        /// top-level paths that arrived, in the order asked for, which is what
        /// goes on the clipboard.
        /// </summary>
        public static List<string> ExtractSelection(ArchiveListing listing, IReadOnlyList<string> inners,
            string destination, IProgress<TransferProgress>? progress, List<string> errors, CancellationToken token)
        {
            ExtractInto(listing, inners, destination, progress, errors, token);

            var arrived = new List<string>();
            foreach (var inner in inners)
            {
                var node = listing.Find(inner);
                if (node == null) continue;
                var path = Path.Combine(destination, node.Name);
                if (node.IsDirectory ? Directory.Exists(path) : File.Exists(path)) arrived.Add(path);
            }
            return arrived;
        }

        /// <summary>
        /// Which name under the destination an archive entry is written to, for
        /// a selection of members: the entry is one of them or inside one, and
        /// lands relative to the folder that member is in. Null for everything
        /// else. A ".." left in the name is kept, so SafeTarget refuses it.
        /// </summary>
        public static Func<string, string?> SelectionMap(IReadOnlyList<string> inners)
        {
            var chosen = inners.Select(ExtractionRules.Normalise).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return name =>
            {
                foreach (var member in chosen)
                {
                    if (name.Equals(member, StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(member + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        var parent = ParentOf(member);
                        return parent.Length == 0 ? name : name[(parent.Length + 1)..];
                    }
                }
                return null;
            };
        }

        private static int ExtractInto(ArchiveListing listing, IReadOnlyList<string> inners, string destination,
            IProgress<TransferProgress>? progress, List<string> errors, CancellationToken token)
        {
            Directory.CreateDirectory(destination);
            var select = SelectionMap(inners);

            if (listing.Tree != null) return CopyFromTree(listing, inners, destination, progress, errors, token);

            if (listing.Format.Engine == ArchiveEngineKind.Zip)
                return ExtractZip(listing.ArchivePath, select, destination, progress, errors, token);

            var reporter = new ArchiveEngine.Reporter(progress);
            var rules = ExtractionRules.Selecting(destination, select);
            int done = TarEngine.Extract(NameRules.ExactPath(listing.ArchivePath), listing.Format, rules,
                Math.Min(4, ArchiveEngine.RecommendedThreads), reporter, errors, token, estimate: true);

            // A chosen member that is a hard link (GNU tar's second name for a
            // file) has no data of its own, and the file it names was not part
            // of the selection, so it was never written and the link failed.
            // Those files are taken out once more, straight to the link's name.
            const string marker = ": it is another name for ", tail = ", which was not extracted";
            var again = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = errors.Count - 1; i >= 0; i--)
            {
                var line = errors[i];
                int at = line.IndexOf(marker, StringComparison.Ordinal);
                int end = line.LastIndexOf(tail, StringComparison.Ordinal);
                if (at <= 0 || end <= at) continue;
                var relative = select(ExtractionRules.Normalise(line[..at]));
                var source = ExtractionRules.Normalise(line[(at + marker.Length)..end]);
                if (relative == null || source.Length == 0 || again.ContainsKey(source)) continue;
                again[source] = relative;
                errors.RemoveAt(i);
            }
            if (again.Count == 0) return done;

            var second = ExtractionRules.Selecting(destination, n => again.TryGetValue(n, out var to) ? to : null);
            return done + TarEngine.Extract(NameRules.ExactPath(listing.ArchivePath), listing.Format, second,
                1, new ArchiveEngine.Reporter(null), errors, token, estimate: false);
        }

        /// <summary>
        /// The chosen entries of a zip and nothing else: the central directory
        /// says where each one is, so the rest of the file is never read. Each
        /// is checked against its own CRC and length before it is given its
        /// name, as the full extraction checks it.
        /// </summary>
        private static int ExtractZip(string archivePath, Func<string, string?> select, string destination,
            IProgress<TransferProgress>? progress, List<string> errors, CancellationToken token)
        {
            using var file = new FileStream(NameRules.ExactPath(archivePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using var archive = ZipEngine.Open(file);

            var chosen = new List<(ZipArchiveEntry Entry, string Name)>();
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                var mapped = select(ExtractionRules.Normalise(entry.FullName));
                if (mapped == null) continue;

                // A network path loses its two slashes when it is cleaned up,
                // so it is refused by its raw form.
                if (entry.FullName.Replace('\\', '/').StartsWith("//", StringComparison.Ordinal))
                {
                    errors.Add($"{entry.FullName} names a network path and was not extracted");
                    continue;
                }
                chosen.Add((entry, mapped));
            }

            var reporter = new ArchiveEngine.Reporter(progress);
            reporter.SetTotals(chosen.Sum(c => IsFolder(c.Entry) ? 0 : Math.Max(0, c.Entry.Length)), chosen.Count);

            var given = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var buffer = new byte[1 << 20];
            int done = 0;

            foreach (var (entry, name) in chosen)
            {
                token.ThrowIfCancellationRequested();

                var target = ArchiveEngine.SafeTarget(destination, name, out var reason);
                if (target == null)
                {
                    errors.Add(reason ?? $"{entry.FullName} cannot be written here");
                    reporter.Finished(0);
                    continue;
                }

                if (IsFolder(entry))
                {
                    try { Directory.CreateDirectory(target); done++; }
                    catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
                    reporter.Finished(0);
                    continue;
                }

                if (!given.Add(target))
                {
                    errors.Add($"{name}: the archive holds this name more than once; the first was kept");
                    reporter.Finished(0);
                    continue;
                }

                if (entry.IsEncrypted)
                {
                    errors.Add($"{name}: it is password-protected, which Explorer Native can't open");
                    reporter.Finished(0);
                    continue;
                }

                if (ZipEngine.IsLink(entry))
                {
                    errors.Add($"{name}: a symbolic link was not extracted");
                    reporter.Finished(0);
                    continue;
                }

                reporter.Starting(name);
                try
                {
                    DateTime? modified;
                    try { modified = entry.LastWriteTime.LocalDateTime; } catch { modified = null; }

                    ArchiveEngine.WriteCommitted(target, output =>
                    {
                        Stream source;
                        try { source = entry.Open(); }
                        catch (InvalidDataException)
                        {
                            throw new NotSupportedException("it is compressed in a way Explorer Native can't read");
                        }

                        using var opened = source;
                        uint crc = Crc32.Seed;
                        long length = 0;
                        while (true)
                        {
                            token.ThrowIfCancellationRequested();
                            int got;
                            try { got = source.Read(buffer, 0, buffer.Length); }
                            catch (InvalidDataException) { throw new InvalidDataException("it is damaged in the archive"); }
                            if (got <= 0) break;
                            output.Write(buffer, 0, got);
                            crc = Crc32.Append(crc, buffer.AsSpan(0, got));
                            length += got;
                            reporter.Advance(got);
                        }

                        if (length != entry.Length)
                            throw new InvalidDataException(
                                $"the archive holds {entry.Length:N0} bytes of it and {length:N0} came out");
                        if (crc != entry.Crc32)
                            throw new InvalidDataException("it is damaged in the archive (its checksum does not match)");
                    }, bufferSize: 0, modified: modified);

                    done++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
                reporter.Finished(0);
            }

            reporter.Done();
            return done;
        }

        private static bool IsFolder(ZipArchiveEntry entry) =>
            entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

        /// <summary>For the bsdtar formats, whose members are already on disk: a copy out of the cache.</summary>
        private static int CopyFromTree(ArchiveListing listing, IReadOnlyList<string> inners, string destination,
            IProgress<TransferProgress>? progress, List<string> errors, CancellationToken token)
        {
            var tree = listing.Tree!;
            var plan = new List<(string Source, string Target, bool Folder, long Size)>();

            foreach (var inner in inners)
            {
                var node = listing.Find(inner);
                if (node == null) { errors.Add($"{inner}: it is no longer in the archive"); continue; }

                var top = ArchiveEngine.SafeTarget(destination, node.Name, out var reason);
                var source = ArchiveEngine.SafeTarget(tree, inner, out _);
                if (top == null || source == null) { errors.Add(reason ?? $"{inner} cannot be written here"); continue; }

                plan.Add((source, top, node.IsDirectory, node.Size));
                if (!node.IsDirectory) continue;

                var parent = ParentOf(inner);
                foreach (var below in listing.Below(inner))
                {
                    var relative = parent.Length == 0 ? below.InnerPath : below.InnerPath[(parent.Length + 1)..];
                    var target = ArchiveEngine.SafeTarget(destination, relative, out var why);
                    var from = ArchiveEngine.SafeTarget(tree, below.InnerPath, out _);
                    if (target == null || from == null) { errors.Add(why ?? $"{below.InnerPath} cannot be written here"); continue; }
                    plan.Add((from, target, below.IsDirectory, below.IsDirectory ? 0 : below.Size));
                }
            }

            var reporter = new ArchiveEngine.Reporter(progress);
            reporter.SetTotals(plan.Where(p => !p.Folder).Sum(p => p.Size), plan.Count);

            int done = 0;
            foreach (var (source, target, folder, size) in plan)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (folder) Directory.CreateDirectory(target);
                    else
                    {
                        reporter.Starting(Path.GetFileName(target));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(source, target, overwrite: false);
                        reporter.Advance(size);
                    }
                    done++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{Path.GetFileName(target)}: {ex.Message}");
                }
                reporter.Finished(0);
            }

            reporter.Done();
            return done;
        }

        /// <summary>An error line without the member's name on the front, for a sentence that already says it.</summary>
        private static string StripName(string error, string inner)
        {
            var name = inner.Contains('/') ? inner[(inner.LastIndexOf('/') + 1)..] : inner;
            foreach (var prefix in new[] { inner + ": ", name + ": " })
                if (error.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return error[prefix.Length..];
            return error;
        }

        // ---------- The cache ----------

        /// <summary>
        /// Where members taken out of archives are put: %TEMP%\ExplorerNative\archives.
        /// "open-" folders hold files being played or opened, "tree-" folders
        /// whole bsdtar archives, both cleared on exit. "copy-" folders are what
        /// a Copy put on the clipboard, kept a day, because a paste can come
        /// after the application has closed.
        /// </summary>
        public static string CacheRoot => Path.Combine(Path.GetTempPath(), "ExplorerNative", "archives");

        /// <summary>How long what was copied to the clipboard is kept.</summary>
        public static readonly TimeSpan CopyKeptFor = TimeSpan.FromDays(1);

        public static string NewCacheFolder(string kind)
        {
            var folder = Path.Combine(CacheRoot, kind + "-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>
        /// Clears what a run before this one left: every "open-" and "tree-"
        /// folder made before <paramref name="startedAt"/>, and copies older than
        /// a day. Runs on a worker at launch.
        /// </summary>
        public static void SweepCache(DateTime startedAt) => SweepCache(CacheRoot, startedAt, DateTime.Now);

        internal static void SweepCache(string root, DateTime startedAt, DateTime now)
        {
            if (!Directory.Exists(root)) return;

            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                try
                {
                    var name = Path.GetFileName(folder);
                    var made = Directory.GetCreationTime(folder);
                    bool go = name.StartsWith("copy-", StringComparison.OrdinalIgnoreCase)
                        ? now - Directory.GetLastWriteTime(folder) > CopyKeptFor
                        : made < startedAt;
                    if (go) DeleteTree(folder);
                }
                catch { }
            }

            foreach (var file in Directory.EnumerateFiles(root))
            {
                try { if (File.GetCreationTime(file) < startedAt) File.Delete(file); }
                catch { }
            }
        }

        /// <summary>
        /// On exit: the files that were played or opened, and the extracted
        /// bsdtar archives. A file another program still has open is left for
        /// the next launch's sweep.
        /// </summary>
        public static void ClearOpened()
        {
            _opened.Clear();
            var root = CacheRoot;
            if (!Directory.Exists(root)) return;

            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(folder);
                if (name.StartsWith("copy-", StringComparison.OrdinalIgnoreCase)) continue;
                try { DeleteTree(folder); } catch { }
            }
            foreach (var file in Directory.EnumerateFiles(root, "*.done"))
            {
                try { File.Delete(file); } catch { }
            }
        }

        /// <summary>Deletes a folder, read-only files included; a link is removed, never followed.</summary>
        internal static void DeleteTree(string folder)
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (entry is DirectoryInfo) Directory.Delete(entry.FullName, recursive: false);
                    else File.Delete(entry.FullName);
                    continue;
                }

                if (entry is DirectoryInfo) { DeleteTree(entry.FullName); continue; }

                try { File.SetAttributes(entry.FullName, FileAttributes.Normal); } catch { }
                File.Delete(entry.FullName);
            }

            try { File.SetAttributes(folder, FileAttributes.Directory); } catch { }
            Directory.Delete(folder, recursive: false);
        }

        /// <summary>The archive's path, size and date: a changed archive is a different archive.</summary>
        private static string KeyFor(string archivePath)
        {
            long length = 0, ticks = 0;
            try
            {
                var info = new FileInfo(NameRules.ExactPath(archivePath));
                length = info.Length;
                ticks = info.LastWriteTimeUtc.Ticks;
            }
            catch { }
            return $"{archivePath.ToUpperInvariant()}|{length}|{ticks}";
        }

        private static string Hash(string text) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

        /// <summary>A read-through stream that calls back on every read, so progress is reported while headers are read.</summary>
        private sealed class TickingStream : Stream
        {
            private readonly Stream _inner;
            private readonly Action _tick;

            public TickingStream(Stream inner, Action tick)
            {
                _inner = inner;
                _tick = tick;
            }

            public override bool CanRead => true;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int got = _inner.Read(buffer, offset, count);
                _tick();
                return got;
            }

            public override int Read(Span<byte> buffer)
            {
                int got = _inner.Read(buffer);
                _tick();
                return got;
            }

            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
