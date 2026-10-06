using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ExplorerNative
{
    /// <summary>
    /// Copy, move and paste: the faults a review reproduced with real files, each
    /// held here by the same reproduction.
    ///
    /// Every check reads the disk afterwards. A count says something was noticed;
    /// only the files say it was done.
    /// </summary>
    internal static class TransferFixTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Copy, move and paste, as reproduced:");

            await Guarded("fill gaps resume", FillGapsResumeTests);
            await Guarded("moving a link", TopLevelLinkMoveTests);
            await Guarded("names ending in a dot or space", TrailingDotTests);
            await Guarded("a move within one volume", SameVolumeMoveTests);
            await Guarded("names in the progress window", UnrelatedNamesTests);
            await Guarded("a move into its own folder", MoveIntoOwnFolderTests);
            await Guarded("skips deeper in a tree", DeepSkipTests);
            await Guarded("the renamed copy", RenamedCopyKeepsMetadataTests);
            await Guarded("the smaller reports", SmallReportingTests);
            await Guarded("the Drive letter is never renamed into or out of", NoRenameOnTheLetterTests);
            await Guarded("a junction made again elsewhere", RecreatedLinkTests);
            Guarded("volumes and retries", VolumeAndRetryTests);
            Guarded("reading robocopy's log", LogParsingTests);
            await Guarded("a locked file", LockedFileTests);

            Console.WriteLine();
        }

        private static void Guarded(string what, Action test)
        {
            try { test(); }
            catch (Exception ex) { Check($"{what}: the test ran", false, ex.GetType().Name + ": " + ex.Message); }
        }

        private static async Task Guarded(string what, Func<Task> test)
        {
            try { await test(); }
            catch (Exception ex) { Check($"{what}: the test ran", false, ex.GetType().Name + ": " + ex.Message); }
        }

        // ---------------------------------------------------------------- helpers

        private static string NewDir(string tag)
        {
            var root = Environment.GetEnvironmentVariable("EXPLORERNATIVE_TRANSFERTEST_DIR");
            root = string.IsNullOrEmpty(root) ? Path.GetTempPath() : Path.GetFullPath(root);
            var dir = Path.Combine(root, $"en-xfer-{tag}-{Guid.NewGuid().ToString("N")[..8]}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Removed through the literal form, so a name ending in a dot is deleted
        /// rather than mistaken for its sibling, and read-only files first made
        /// writable.
        /// </summary>
        private static void Remove(string dir)
        {
            try
            {
                var literal = NameRules.LiteralPath(dir);
                foreach (var f in Directory.EnumerateFiles(literal, "*", new EnumerationOptions
                         { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Directory.Delete(literal, true);
            }
            catch { }
        }

        private static void MakeFile(string path, int megabytes, int seed)
        {
            var block = new byte[4 * 1024 * 1024];
            new Random(seed).NextBytes(block);
            using var fs = new FileStream(NameRules.LiteralPath(path), FileMode.Create, FileAccess.Write);
            for (int i = 0; i < megabytes / 4; i++)
            {
                block[0] = (byte)i;
                fs.Write(block, 0, block.Length);
            }
        }

        private static bool SameBytes(string a, string b)
        {
            try
            {
                using var fa = new FileStream(NameRules.LiteralPath(a), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var fb = new FileStream(NameRules.LiteralPath(b), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fa.Length != fb.Length) return false;
                var x = new byte[1 << 20];
                var y = new byte[1 << 20];
                while (true)
                {
                    int n = fa.Read(x, 0, x.Length);
                    if (n == 0) return true;
                    fb.ReadExactly(y, 0, n);
                    if (!x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, n))) return false;
                }
            }
            catch { return false; }
        }

        private static string Text(string path) => File.ReadAllText(NameRules.LiteralPath(path));
        private static bool FileThere(string path) => File.Exists(NameRules.LiteralPath(path));
        private static bool FolderThere(string path) => Directory.Exists(NameRules.LiteralPath(path));

        private static void Junction(string link, string target)
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);

        private static ulong FileId(string path)
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(h, out var info) ? ((ulong)info.IndexHigh << 32) | info.IndexLow : 0;
        }

        /// <summary>Reports collected on the thread that made them.</summary>
        private sealed class Seen<T> : IProgress<T>
        {
            public readonly List<T> Items = new();
            public void Report(T value) { lock (Items) Items.Add(value); }
            public List<T> Snapshot() { lock (Items) return Items.ToList(); }
        }

        // ------------------------------------------------------------------ tests

        /// <summary>
        /// Fill gaps, cancelled twice, then run to the end. The second cancel lands
        /// in a folder that already existed, which the cleanup used to leave
        /// alone — so its half-written file stayed, dated now, and every later
        /// Fill gaps excluded it as "newer" for ever while reporting success.
        /// </summary>
        private static async Task FillGapsResumeTests()
        {
            var root = NewDir("gaps");
            try
            {
                var from = Path.Combine(root, "from", "F");
                var to = Path.Combine(root, "to");
                Directory.CreateDirectory(from);
                Directory.CreateDirectory(to);
                for (int i = 0; i < 3; i++) MakeFile(Path.Combine(from, $"t{i}.bin"), 256, 100 + i);

                var landed = Path.Combine(to, "F");

                async Task<TransferResult> PasteAndCancelOnNewFile()
                {
                    var before = Directory.Exists(landed)
                        ? Directory.GetFiles(landed).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    using var cts = new CancellationTokenSource();
                    var watcher = Task.Run(async () =>
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            try
                            {
                                if (Directory.Exists(landed) &&
                                    Directory.GetFiles(landed).Any(f => !before.Contains(f)))
                                {
                                    await Task.Delay(30);
                                    cts.Cancel();
                                    return;
                                }
                            }
                            catch { }
                            await Task.Delay(1);
                        }
                    });

                    var result = await RoboCopyEngine.RunAsync(
                        new[] { from }, to, move: false, 1, PasteConflictPolicy.FillGaps, null, cts.Token);
                    cts.Cancel();
                    await watcher;
                    return result;
                }

                var first = await PasteAndCancelOnNewFile();
                var second = await PasteAndCancelOnNewFile();
                Console.WriteLine($"        first cancelled={first.Cancelled}, second cancelled={second.Cancelled}");

                // Whatever survived the second cancel is either the whole file or
                // nothing: a file of the right length with the wrong bytes is what
                // the next Fill gaps takes for finished.
                var wrong = Directory.Exists(landed)
                    ? Directory.GetFiles(landed)
                        .Where(f => !SameBytes(Path.Combine(from, Path.GetFileName(f)), f))
                        .Select(Path.GetFileName).ToList()
                    : new List<string?>();
                Check("fill gaps: a second cancel, into a folder that was already there, leaves nothing half-written",
                    wrong.Count == 0, "left: " + string.Join(", ", wrong));

                var third = await RoboCopyEngine.RunAsync(
                    new[] { from }, to, move: false, 1, PasteConflictPolicy.FillGaps, null, CancellationToken.None);

                bool allRight = Enumerable.Range(0, 3).All(i =>
                    SameBytes(Path.Combine(from, $"t{i}.bin"), Path.Combine(landed, $"t{i}.bin")));
                Check("fill gaps: and the run after it really does end with every file whole",
                    allRight, $"copied={third.Copied} failed={third.Failed}");
            }
            finally { Remove(root); }
        }

        /// <summary>
        /// A selected junction, moved. Robocopy's /XJ only skips junctions inside
        /// the tree, so the root was followed and what it pointed at moved.
        /// </summary>
        private static async Task TopLevelLinkMoveTests()
        {
            var root = NewDir("link");
            try
            {
                var target = Path.Combine(root, "tgt");
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);
                File.WriteAllText(Path.Combine(target, "precious.txt"), "PRECIOUS");

                var link = Path.Combine(src, "J");
                Junction(link, target);
                if (!Directory.Exists(link)) { Check("link: the junction fixture could be made", false); return; }

                await RoboCopyEngine.RunAsync(new[] { link }, dst, move: true, 4,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None);

                Check("link: moving a junction leaves what it points at where it was",
                    File.Exists(Path.Combine(target, "precious.txt")));
                var moved = Path.Combine(dst, "J");
                Check("link: the junction itself is what arrives",
                    Directory.Exists(moved) && NameRules.IsLink(new DirectoryInfo(moved)),
                    Directory.Exists(moved) ? "a real folder arrived" : "nothing arrived");
                Check("link: and it is gone from where it was", !Directory.Exists(link));

                // The same, colliding, which goes through the managed engine.
                var link2 = Path.Combine(src, "K");
                Junction(link2, target);
                Directory.CreateDirectory(Path.Combine(dst, "K"));
                await RoboCopyEngine.RunAsync(new[] { link2 }, dst, move: true, 4,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None);

                Check("link: a colliding junction moved under keep both leaves its target alone",
                    File.Exists(Path.Combine(target, "precious.txt")));
                var numbered = Path.Combine(dst, "K (2)");
                Check("link: and arrives as a link under the new name",
                    Directory.Exists(numbered) && NameRules.IsLink(new DirectoryInfo(numbered)));
            }
            finally { Remove(root); }
        }

        /// <summary>
        /// "report" and "report." in one folder. Windows strips the trailing dot
        /// from an ordinary path, so a move of one reached the other.
        /// </summary>
        private static async Task TrailingDotTests()
        {
            var root = NewDir("dots");
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);

                var plain = Path.Combine(src, "report");
                var dotted = Path.Combine(src, "report.");
                File.WriteAllText(plain, "PLAIN");
                File.WriteAllText(NameRules.LiteralPath(dotted), "DOTTED");
                File.WriteAllText(NameRules.LiteralPath(Path.Combine(dst, "report.")), "THERE");

                // Collides, so the managed engine renames it.
                var moved = await RoboCopyEngine.RunAsync(new[] { dotted }, dst, move: true, 4,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None);

                Check("dots: moving \"report.\" leaves its sibling \"report\" alone",
                    FileThere(plain) && Text(plain) == "PLAIN");
                Check("dots: and \"report.\" is what left", !FileThere(dotted));
                var arrived = Directory.GetFiles(NameRules.LiteralPath(dst)).Select(Path.GetFileName).ToList();
                Check("dots: and what arrived has the dotted file's contents",
                    Directory.GetFiles(NameRules.LiteralPath(dst)).Any(f => Path.GetFileName(f) != "report." && Text(f) == "DOTTED"),
                    "arrived: " + string.Join(", ", arrived) + "; errors: " + string.Join(" | ", moved.Errors));

                // Not colliding, through robocopy.
                var trail = Path.Combine(src, "trail ");
                File.WriteAllText(NameRules.LiteralPath(trail), "SPACE");
                var copied = await RoboCopyEngine.RunAsync(new[] { trail }, dst, move: false, 4,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None);
                Check("dots: a file whose name ends in a space copies",
                    FileThere(Path.Combine(dst, "trail ")) && copied.Failed == 0,
                    string.Join(" | ", copied.Errors));

                // A folder ending in a dot beside one without: robocopy, handed
                // "fold.", copies "fold".
                var fold = Path.Combine(src, "fold.");
                Directory.CreateDirectory(NameRules.LiteralPath(fold));
                File.WriteAllText(NameRules.LiteralPath(Path.Combine(fold, "in.txt")), "IN");
                Directory.CreateDirectory(Path.Combine(src, "fold"));
                File.WriteAllText(Path.Combine(src, "fold", "plainfold.txt"), "PF");

                var folder = await RoboCopyEngine.RunAsync(new[] { fold }, dst, move: false, 4,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None);
                Check("dots: a folder ending in a dot copies its own contents",
                    FileThere(Path.Combine(dst, "fold.", "in.txt")),
                    string.Join(" | ", folder.Errors));
                Check("dots: and not its sibling's",
                    !FileThere(Path.Combine(dst, "fold.", "plainfold.txt")) &&
                    !FolderThere(Path.Combine(dst, "fold")));
            }
            finally { Remove(root); }
        }

        /// <summary>
        /// A move on one volume is a rename. /MOVE copied every byte and then
        /// deleted the original, so a file id changed and a gigabyte took as
        /// long as copying one.
        /// </summary>
        private static async Task SameVolumeMoveTests()
        {
            var root = NewDir("rename");
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                Directory.CreateDirectory(Path.Combine(src, "Dir", "inner"));
                Directory.CreateDirectory(dst);
                var big = Path.Combine(src, "big.bin");
                MakeFile(big, 64, 7);
                File.WriteAllText(Path.Combine(src, "Dir", "x.txt"), "X");
                File.WriteAllText(Path.Combine(src, "Dir", "inner", "y.txt"), "Y");

                ulong idBefore = FileId(big);
                var dirId = FileId(Path.Combine(src, "Dir", "x.txt"));

                var progress = new Seen<TransferProgress>();
                var result = await RoboCopyEngine.RunAsync(new[] { big, Path.Combine(src, "Dir") }, dst,
                    move: true, 4, PasteConflictPolicy.AutoRename, progress, CancellationToken.None);

                var movedBig = Path.Combine(dst, "big.bin");
                Check("rename: a file moved within one volume arrives",
                    File.Exists(movedBig) && !File.Exists(big));
                Check("rename: as the same file, not a copy of it",
                    File.Exists(movedBig) && FileId(movedBig) == idBefore);
                var movedX = Path.Combine(dst, "Dir", "x.txt");
                Check("rename: a folder moved within one volume is the same folder",
                    File.Exists(movedX) && FileId(movedX) == dirId &&
                    File.Exists(Path.Combine(dst, "Dir", "inner", "y.txt")) &&
                    !Directory.Exists(Path.Combine(src, "Dir")));
                Check("rename: and it reports success, counting files",
                    result.Failed == 0 && result.Copied == 3,
                    $"copied={result.Copied} failed={result.Failed} {string.Join(" | ", result.Errors)}");
            }
            finally { Remove(root); }
        }

        /// <summary>
        /// A file held open. Twenty retries five seconds apart is a hundred
        /// seconds of a window at 100 percent saying nothing.
        /// </summary>
        private static async Task LockedFileTests()
        {
            var root = NewDir("locked");
            FileStream? held = null, held2 = null;
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);
                var locked = Path.Combine(src, "locked.txt");
                var ok = Path.Combine(src, "ok.txt");
                var unicode = Path.Combine(src, "日本語 ünï.txt");
                File.WriteAllText(locked, "LOCKED");
                File.WriteAllText(ok, "OK");
                File.WriteAllText(unicode, "UNICODE");
                held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
                held2 = new FileStream(unicode, FileMode.Open, FileAccess.Read, FileShare.None);

                var progress = new Seen<TransferProgress>();
                var clock = Stopwatch.StartNew();
                var result = await RoboCopyEngine.RunAsync(new[] { locked, ok, unicode }, dst, move: false, 4,
                    PasteConflictPolicy.AutoRename, progress, CancellationToken.None);
                clock.Stop();

                Check("locked: a locked file on a local disk gives up in seconds, not minutes",
                    clock.Elapsed < TimeSpan.FromSeconds(20), $"{clock.Elapsed.TotalSeconds:0.0} s");
                Check("locked: the window says what it is waiting for",
                    progress.Snapshot().Any(p => p.CurrentItem.StartsWith("Waiting for locked.txt (retry 1", StringComparison.Ordinal)),
                    string.Join(" / ", progress.Snapshot().Select(p => p.CurrentItem).Distinct()));
                Check("locked: the failure names the file in its own letters",
                    result.Errors.Any(e => e.Contains("日本語 ünï.txt", StringComparison.Ordinal)),
                    string.Join(" | ", result.Errors));
                Check("locked: failed counts files, not robocopy runs",
                    result.Failed == 2, $"failed={result.Failed}");
                Check("locked: and a file that failed is not counted as copied",
                    result.Copied == 1, $"copied={result.Copied}");
            }
            finally
            {
                held?.Dispose();
                held2?.Dispose();
                Remove(root);
            }
        }

        /// <summary>
        /// Something else writing inside the destination while a big file
        /// copies. The window named its files and counted them as items.
        /// </summary>
        private static async Task UnrelatedNamesTests()
        {
            var root = NewDir("names");
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                var busy = Path.Combine(dst, "busy");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(busy);
                var big = Path.Combine(src, "big.bin");
                MakeFile(big, 512, 11);

                using var stop = new CancellationTokenSource();
                var noise = Task.Run(async () =>
                {
                    int i = 0;
                    while (!stop.IsCancellationRequested)
                    {
                        try { File.WriteAllText(Path.Combine(busy, $"Cookies-journal{i++}"), "x"); } catch { }
                        await Task.Delay(3);
                    }
                });

                var progress = new Seen<TransferProgress>();
                await RoboCopyEngine.RunAsync(new[] { big }, dst, move: false, 1,
                    PasteConflictPolicy.AutoRename, progress, CancellationToken.None);
                stop.Cancel();
                await noise;

                var named = progress.Snapshot().Select(p => p.CurrentItem).Where(n => n.Contains("Cookies")).Distinct().ToList();
                Check("names: the progress window never names a file that is not being copied",
                    named.Count == 0, string.Join(", ", named.Take(5)));
                Check("names: nor counts one as an item",
                    progress.Snapshot().All(p => p.ItemsDone <= 1 && p.ItemsTotal <= 1),
                    string.Join(", ", progress.Snapshot().Select(p => $"{p.ItemsDone}/{p.ItemsTotal}").Distinct()));
            }
            finally { Remove(root); }
        }

        /// <summary>
        /// Cut, then paste into the same folder, under keep both: the items came
        /// back numbered.
        /// </summary>
        private static async Task MoveIntoOwnFolderTests()
        {
            var root = NewDir("own");
            try
            {
                var a = Path.Combine(root, "a.txt");
                var d = Path.Combine(root, "D");
                File.WriteAllText(a, "A");
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "x.txt"), "X");

                foreach (var engine in new[] { "robocopy", "managed" })
                {
                    if (engine == "robocopy")
                        await RoboCopyEngine.RunAsync(new[] { a, d }, root, move: true, 4,
                            PasteConflictPolicy.AutoRename, null, CancellationToken.None);
                    else
                        await FileOperations.RunAsync(new[] { a, d }, root, true, PasteConflictPolicy.AutoRename,
                            4, 64, null, null, CancellationToken.None);

                    var names = Directory.GetFileSystemEntries(root).Select(Path.GetFileName).OrderBy(n => n).ToList();
                    Equal($"own folder ({engine}): moving items into the folder they are in changes nothing",
                        "a.txt, D", string.Join(", ", names));
                    Check($"own folder ({engine}): and their contents are still there",
                        File.Exists(a) && File.Exists(Path.Combine(d, "x.txt")));
                }
            }
            finally { Remove(root); }
        }

        /// <summary>
        /// Two skips deeper in a tree that were reported as success: a file
        /// that robocopy will not put over a folder, and a Fill gaps move that
        /// leaves a file behind.
        /// </summary>
        private static async Task DeepSkipTests()
        {
            var root = NewDir("deep");
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                Directory.CreateDirectory(Path.Combine(src, "A"));
                Directory.CreateDirectory(Path.Combine(dst, "A", "x"));
                File.WriteAllText(Path.Combine(src, "A", "x"), "FILE");
                File.WriteAllText(Path.Combine(src, "A", "y"), "Y");
                File.WriteAllText(Path.Combine(dst, "A", "x", "inside"), "I");

                var over = await RoboCopyEngine.RunAsync(new[] { Path.Combine(src, "A") }, dst, move: false, 4,
                    PasteConflictPolicy.Overwrite, null, CancellationToken.None);
                Check("deep: a file that could not go over a folder deeper down is a failure",
                    over.Failed == 1, $"failed={over.Failed} copied={over.Copied}");
                Check("deep: and not counted as copied", over.Copied == 1, $"copied={over.Copied}");

                Directory.CreateDirectory(Path.Combine(src, "F"));
                Directory.CreateDirectory(Path.Combine(dst, "F"));
                File.WriteAllText(Path.Combine(src, "F", "both.txt"), "SRC");
                File.WriteAllText(Path.Combine(src, "F", "only.txt"), "ONLY");
                File.WriteAllText(Path.Combine(dst, "F", "both.txt"), "DESTINATION");

                var gaps = await RoboCopyEngine.RunAsync(new[] { Path.Combine(src, "F") }, dst, move: true, 4,
                    PasteConflictPolicy.FillGaps, null, CancellationToken.None);
                Check("deep: a Fill gaps move still leaves the original of what was already there",
                    File.Exists(Path.Combine(src, "F", "both.txt")) && Text(Path.Combine(dst, "F", "both.txt")) == "DESTINATION");
                Check("deep: and says so rather than calling it moved",
                    gaps.AlreadyThere == 1 && gaps.Failed == 0,
                    $"copied={gaps.Copied} failed={gaps.Failed} alreadyThere={gaps.AlreadyThere}");
                Check("deep: counting only what moved", gaps.Copied == 1, $"copied={gaps.Copied}");
            }
            finally { Remove(root); }
        }

        /// <summary>
        /// The " (2)" copy is made by the managed engine, and it kept the bytes
        /// and nothing else: not Hidden or ReadOnly, not the streams, not the
        /// creation date. Robocopy keeps all three.
        /// </summary>
        private static async Task RenamedCopyKeepsMetadataTests()
        {
            var root = NewDir("meta");
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);
                var file = Path.Combine(src, "keep.txt");
                File.WriteAllText(file, "CONTENT");
                File.WriteAllText(file + ":meta", "STREAM");
                var created = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
                var written = new DateTime(2002, 3, 4, 5, 6, 7, DateTimeKind.Utc);
                File.SetCreationTimeUtc(file, created);
                File.SetLastWriteTimeUtc(file, written);
                File.SetAttributes(file, FileAttributes.Hidden | FileAttributes.ReadOnly);
                File.WriteAllText(Path.Combine(dst, "keep.txt"), "THERE");

                var result = await FileOperations.RunAsync(new[] { file }, dst, false, PasteConflictPolicy.AutoRename,
                    4, 64, null, null, CancellationToken.None);

                var copy = Path.Combine(dst, "keep (2).txt");
                Check("metadata: the renamed copy arrives", File.Exists(copy), string.Join(" | ", result.Errors));
                if (!File.Exists(copy)) return;

                var attributes = File.GetAttributes(copy);
                Check("metadata: it keeps Hidden and ReadOnly",
                    attributes.HasFlag(FileAttributes.Hidden) && attributes.HasFlag(FileAttributes.ReadOnly),
                    attributes.ToString());
                string stream;
                try { stream = File.ReadAllText(copy + ":meta"); } catch (Exception ex) { stream = ex.Message; }
                Equal("metadata: it keeps its alternate data stream", "STREAM", stream);
                Equal("metadata: it keeps its creation date", created.ToString("o"), File.GetCreationTimeUtc(copy).ToString("o"));
                Equal("metadata: and its last-write date", written.ToString("o"), File.GetLastWriteTimeUtc(copy).ToString("o"));
            }
            finally { Remove(root); }
        }

        private static async Task SmallReportingTests()
        {
            var root = NewDir("small");
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);

                // A missing source whose name collides went down the managed
                // engine's plan, which had no branch for "neither a file nor a
                // folder", and the paste said "Copy complete".
                File.WriteAllText(Path.Combine(dst, "gone.txt"), "THERE");
                var missing = await RoboCopyEngine.RunAsync(new[] { Path.Combine(src, "gone.txt") }, dst, move: false, 4,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None);
                Check("small: a missing source that collides is a failure, not silence",
                    missing.Failed == 1, $"failed={missing.Failed}");

                var managed = await FileOperations.RunAsync(new[] { Path.Combine(src, "gone.txt") }, dst, false,
                    PasteConflictPolicy.AutoRename, 4, 64, null, null, CancellationToken.None);
                Check("small: and the managed engine says so too", managed.Failed == 1, $"failed={managed.Failed}");

                // Overwriting things with themselves.
                var self = Path.Combine(src, "self.txt");
                File.WriteAllText(self, "SELF");
                Directory.CreateDirectory(Path.Combine(src, "selfdir"));
                var onto = await RoboCopyEngine.RunAsync(new[] { self, Path.Combine(src, "selfdir") }, src, move: false, 4,
                    PasteConflictPolicy.Overwrite, null, CancellationToken.None);
                Check("small: overwriting items with themselves announces nothing replaced",
                    onto.Collisions.OverwrittenCount == 0, $"overwritten={onto.Collisions.OverwrittenCount}");
                Check("small: and says each was refused", onto.Failed == 2, $"failed={onto.Failed}");
                Check("small: and the file is untouched", File.ReadAllText(self) == "SELF");
            }
            finally { Remove(root); }
        }
            /// <summary>
        /// A move is renamed only off the Drive letter. A rename inside the sync
        /// root is local and the account never hears of it, so either end being
        /// on the letter — or the source being a cloud copy at all — must go the
        /// ordinary way.
        /// </summary>
        private static async Task NoRenameOnTheLetterTests()
        {
            var root = NewDir("letter");
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);

                var a = Path.Combine(src, "a.bin");
                MakeFile(a, 4, 3);
                ulong idA = FileId(a);
                await RoboCopyEngine.RunAsync(new[] { a }, dst, move: true, 4, PasteConflictPolicy.AutoRename, null,
                    CancellationToken.None, onCloudLetter: p => p.StartsWith(dst, StringComparison.OrdinalIgnoreCase));
                var movedA = Path.Combine(dst, "a.bin");
                Check("letter: a destination on the letter is not renamed into",
                    File.Exists(movedA) && !File.Exists(a) && FileId(movedA) != idA);

                var b = Path.Combine(src, "b.bin");
                MakeFile(b, 4, 4);
                ulong idB = FileId(b);
                await RoboCopyEngine.RunAsync(new[] { b }, dst, move: true, 4, PasteConflictPolicy.AutoRename, null,
                    CancellationToken.None, onCloudLetter: p => p.StartsWith(src, StringComparison.OrdinalIgnoreCase));
                var movedB = Path.Combine(dst, "b.bin");
                Check("letter: nor a source on it renamed out",
                    File.Exists(movedB) && !File.Exists(b) && FileId(movedB) != idB);

                var c = Path.Combine(src, "c.bin");
                MakeFile(c, 4, 5);
                ulong idC = FileId(c);
                await RoboCopyEngine.RunAsync(new[] { c }, dst, move: true, 4, PasteConflictPolicy.AutoRename, null,
                    CancellationToken.None, cloudSource: true);
                var movedC = Path.Combine(dst, "c.bin");
                Check("letter: nor anything copied out of the cloud",
                    File.Exists(movedC) && FileId(movedC) != idC);
            }
            finally { Remove(root); }
        }

        /// <summary>
        /// Moving a junction to another volume makes it again there. Tested on
        /// one volume by calling the half that a rename would otherwise skip.
        /// </summary>
        private static Task RecreatedLinkTests()
        {
            var root = NewDir("relink");
            try
            {
                var target = Path.Combine(root, "tgt");
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "precious.txt"), "PRECIOUS");
                var link = Path.Combine(root, "J");
                Junction(link, target);

                var copy = Path.Combine(root, "elsewhere", "J");
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                FileOperations.RecreateLink(link, copy);

                var info = new DirectoryInfo(copy);
                Check("relink: the new one is a junction", ReparseLinks.IsJunction(copy) && NameRules.IsLink(info));
                Check("relink: pointing where the old one did",
                    File.Exists(Path.Combine(copy, "precious.txt")),
                    info.LinkTarget ?? "no target");

                Directory.Delete(copy, recursive: false);
                Check("relink: and removing it leaves the target alone",
                    File.Exists(Path.Combine(target, "precious.txt")));
            }
            finally { Remove(root); }
            return Task.CompletedTask;
        }

        private static void VolumeAndRetryTests()
        {
            var temp = Path.GetTempPath();
            Check("volume: a local path has a volume name", RoboCopyEngine.VolumeOf(temp)?.StartsWith(@"\\?\Volume{") == true,
                RoboCopyEngine.VolumeOf(temp) ?? "null");
            Check("volume: two folders on one disk are on one volume",
                RoboCopyEngine.SameVolume(temp, Path.Combine(temp, "not-made-yet", "x")));
            Check("volume: a share is remote without asking the network", RoboCopyEngine.IsRemote(@"\\server\share\a.txt"));
            Check("volume: a local folder is not", !RoboCopyEngine.IsRemote(temp));

            Equal("retries: a local copy gives up in seconds", "2/1",
                Format(RoboCopyEngine.RetriesFor(new[] { Path.Combine(temp, "a") }, temp, cloudSource: false)));
            Equal("retries: a share keeps the long wait", "20/5",
                Format(RoboCopyEngine.RetriesFor(new[] { @"\\server\share\a" }, temp, cloudSource: false)));
            Equal("retries: so does a copy out of Drive", "20/5",
                Format(RoboCopyEngine.RetriesFor(new[] { Path.Combine(temp, "a") }, temp, cloudSource: true)));

            static string Format((int Count, int WaitSeconds) r) => $"{r.Count}/{r.WaitSeconds}";

            // A subst letter has no volume path of its own, and is asked as the
            // folder it stands for. Opt-in, because it puts a drive letter in
            // front of anything watching the drive list while it runs.
            if (Environment.GetEnvironmentVariable("EXPLORERNATIVE_TEST_SUBST") != "1") return;
            var letter = "QRSTUVWY".Select(c => c + ":").FirstOrDefault(l => !Directory.Exists(l + "\\"));
            if (letter == null) return;
            var folder = NewDir("subst");
            void Subst(string args)
            {
                using var p = Process.Start(new ProcessStartInfo("subst.exe", args) { UseShellExecute = false, CreateNoWindow = true })!;
                p.WaitForExit();
            }
            Subst($"{letter} \"{folder}\"");
            try
            {
                Check("volume: a subst letter is the volume it stands for",
                    RoboCopyEngine.SameVolume(letter + "\\x.txt", folder),
                    $"{RoboCopyEngine.VolumeOf(letter + "\\x.txt") ?? "null"} against {RoboCopyEngine.VolumeOf(folder)}");
            }
            finally
            {
                Subst($"{letter} /d");
                Remove(folder);
            }
        }

        /// <summary>
        /// The log, which is where the names are right. A failure retried into
        /// success is no failure; one that stayed failed keeps its reason; and
        /// the counts come from the summary whatever its labels say.
        /// </summary>
        private static void LogParsingTests()
        {
            var lines = new[]
            {
                "",
                "\t    New File  \t\t       6\tC:\\s\\日本語 ünï.txt",
                "2026/10/05 20:48:19 ERROR 32 (0x00000020) Copying File C:\\s\\日本語 ünï.txt",
                "The process cannot access the file because it is being used by another process.",
                "",
                "\t    New File  \t\t       2\tC:\\s\\ok.txt",
                "2026/10/05 20:48:19 ERROR 32 (0x00000020) Copying File C:\\s\\later.txt",
                "The process cannot access the file because it is being used by another process.",
                "Waiting 1 seconds...",
                "\t    New File  \t\t       5\tC:\\s\\later.txt",
                "\t  *MISMATCH   \t\t       4\tC:\\s\\A\\x",
                "",
                "------------------------------------------------------------------------------",
                "",
                "               Total    Copied   Skipped  Mismatch    FAILED    Extras",
                "    Dirs :         2         1         1         0         0         0",
                "   Files :         5         2         1         1         1         0",
                "   Bytes :        30        13         2         4         6         0",
                "   Times :   0:00:02   0:00:00                       0:00:00   0:00:02",
            };

            var log = RoboCopyEngine.ParseLog(lines);
            Equal("log: the failure that stayed failed is the only one", "C:\\s\\日本語 ünï.txt",
                string.Join(" | ", log.FileErrors.Keys));
            Check("log: with its reason joined on",
                log.FileErrors.Values.Single().EndsWith("used by another process.", StringComparison.Ordinal),
                log.FileErrors.Values.Single());
            Equal("log: a file refused over a folder is named", "C:\\s\\A\\x", string.Join(" | ", log.Mismatched));
            Equal("log: and the counts are the Files row", "2 1 1 1 0 0",
                log.Counts == null ? "none" : $"{log.Counts.Copied} {log.Counts.Skipped} {log.Counts.Mismatch} " +
                                              $"{log.Counts.Failed} {log.Counts.DirMismatch} {log.Counts.DirFailed}");
            Check("log: no summary is no counts, not zeros", RoboCopyEngine.ParseLog(lines.Take(5)).Counts == null);
        }
    }
}
