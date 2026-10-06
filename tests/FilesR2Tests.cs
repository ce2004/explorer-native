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
    /// The second review of copying, moving and the main window: each fault
    /// reproduced with real files and real robocopy, and held here.
    /// </summary>
    internal static class FilesR2Tests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Copying and the window, second review:");

            Guarded("a summary over 2GB", BigSummaryParseTests);
            await Guarded("a 3GB file through robocopy", BigFileThroughRobocopyTests);
            await Guarded("waiting for an accented name", AccentedRetryNameTests);
            await Guarded("waiting for a best-fit name", BestFitRetryNameTests);
            await Guarded("streams that cannot come", StreamsBestEffortTests);
            await Guarded("a move by renaming, per item", RenamePerItemTests);
            await Guarded("progress never goes back", MonotonicProgressTests);
            await Guarded("a junction leading back inside", JunctionIntoItselfTests);
            Guarded("a subst letter", SubstTests);
            Guarded("the Windows menu and trailing dots", ShellMenuDotTests);
            Guarded("type-ahead folding", TypeAheadFoldingTests);
            Guarded("reset keeps what it says", ResetTests);
            Guarded("the window's source", MainFormSourceTests);

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
            var dir = Path.Combine(root, $"en-r2-{tag}-{Guid.NewGuid().ToString("N")[..8]}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Remove(string dir)
        {
            try
            {
                // Links first, removed as links, so nothing is followed.
                foreach (var d in Directory.EnumerateDirectories(dir, "*", new EnumerationOptions
                         { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList())
                    try { if (NameRules.IsLink(new DirectoryInfo(d))) Directory.Delete(d, false); } catch { }
                foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions
                         { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Directory.Delete(dir, true);
            }
            catch { }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputLength,
            IntPtr output, int outputLength, out int returned, IntPtr overlapped);

        private const uint FsctlSetSparse = 0x000900C4;

        /// <summary>A file of the given length that takes no disk: sparse, and never written.</summary>
        private static bool MakeSparse(string path, long length, DateTime written)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                if (!DeviceIoControl(fs.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    return false;
                fs.SetLength(length);
            }
            File.SetLastWriteTimeUtc(path, written);
            return (File.GetAttributes(path) & FileAttributes.SparseFile) != 0;
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

        private sealed class Seen<T> : IProgress<T>
        {
            public readonly List<T> Items = new();
            public void Report(T value) { lock (Items) Items.Add(value); }
            public List<T> Snapshot() { lock (Items) return Items.ToList(); }
        }

        private static string SourceText(string name)
        {
            var at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at != null && !Directory.Exists(Path.Combine(at.FullName, "src"))) at = at.Parent;
            if (at == null) return "";
            var path = Path.Combine(at.FullName, "src", name);
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch { return ""; }
        }

        /// <summary>The body of a method, from its signature to the next one at the same depth.</summary>
        private static string Method(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return "";
            int open = source.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
            }
            return source[start..];
        }

        // ------------------------------------------------------------------ tests

        /// <summary>
        /// Robocopy's Bytes row for anything over 2GB does not fit an int, and
        /// int.Parse threw out of the finished transfer.
        /// </summary>
        private static void BigSummaryParseTests()
        {
            var lines = new[]
            {
                "------------------------------------------------------------------------------",
                "",
                "               Total    Copied   Skipped  Mismatch    FAILED    Extras",
                "    Dirs :         2         1         1         0         0         0",
                "   Files :         5         2         3         0         0         0",
                "   Bytes :  3221225472  3221225472         0         0         0         0",
                "   Times :   0:00:02   0:00:00                       0:00:00   0:00:02",
            };
            RoboCopyEngine.RobocopyLog? log = null;
            string error = "";
            try { log = RoboCopyEngine.ParseLog(lines); }
            catch (Exception ex) { error = ex.GetType().Name; }
            Equal("a 3GB Bytes row does not throw", "", error);
            Equal("and the counts are still the Files row", "2 3",
                log?.Counts == null ? "none" : $"{log.Counts.Copied} {log.Counts.Skipped}");

            var huge = lines.Select(l => l.Replace("3221225472", "123456789012345678901234567890")).ToArray();
            try { log = RoboCopyEngine.ParseLog(huge); error = ""; }
            catch (Exception ex) { error = ex.GetType().Name; }
            Equal("nor does a number too big for a long", "", error);
            Check("and the counts survive it", log?.Counts?.Copied == 2);

            var engine = SourceText("RoboCopyEngine.cs");
            Check("no int.Parse is left on robocopy's summary",
                engine.Length > 0 && !Method(engine, "internal static RobocopyLog ParseLog(").Contains("int.Parse"));
        }

        /// <summary>
        /// A merge into a folder that already holds a 3GB file, through real
        /// robocopy. The file is sparse on both sides and identical, so robocopy
        /// skips it and nothing is written — but its summary says 3GB, which is
        /// what failed the copy. A second source is a second robocopy job, which
        /// never ran.
        /// </summary>
        private static async Task BigFileThroughRobocopyTests()
        {
            var root = NewDir("big");
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                var folder = Path.Combine(src, "Album");
                Directory.CreateDirectory(folder);
                Directory.CreateDirectory(Path.Combine(dst, "Album"));
                var when = new DateTime(2024, 1, 2, 3, 4, 6, DateTimeKind.Utc);
                const long three = 3L * 1024 * 1024 * 1024;
                bool sparse = MakeSparse(Path.Combine(folder, "huge.bin"), three, when) &&
                              MakeSparse(Path.Combine(dst, "Album", "huge.bin"), three, when);
                Check("big: the 3GB fixture is sparse on both sides", sparse);
                if (!sparse) return;
                File.WriteAllText(Path.Combine(folder, "note.txt"), "NOTE");
                var loose = Path.Combine(src, "after.txt");
                File.WriteAllText(loose, "AFTER");

                TransferResult? result = null;
                string thrown = "";
                try
                {
                    result = await RoboCopyEngine.RunAsync(new[] { folder, loose }, dst, move: false, 4,
                        PasteConflictPolicy.FillGaps, null, CancellationToken.None);
                }
                catch (Exception ex) { thrown = ex.GetType().Name + ": " + ex.Message; }

                Equal("big: a merge beside a 3GB file does not throw", "", thrown);
                Check("big: and is not reported as failed", result != null && result.Failed == 0,
                    result == null ? thrown : $"failed={result.Failed} {string.Join(" | ", result.Errors)}");
                Check("big: the small file in it arrived", File.Exists(Path.Combine(dst, "Album", "note.txt")));
                Check("big: and the job after it ran", File.Exists(Path.Combine(dst, "after.txt")));
                Check("big: the 3GB file was left as it was",
                    new FileInfo(Path.Combine(dst, "Album", "huge.bin")).Length == three);
            }
            finally { Remove(root); }
        }

        /// <summary>
        /// "Waiting for café.txt", not "Waiting for caf‚.txt": robocopy writes in
        /// the OEM code page, which was read as ANSI.
        /// </summary>
        private static async Task AccentedRetryNameTests()
        {
            var root = NewDir("accent");
            FileStream? held = null, held2 = null;
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(Path.Combine(src, "jp"));
                Directory.CreateDirectory(dst);
                var accented = Path.Combine(src, "café ñandú.txt");
                var japanese = Path.Combine(src, "jp", "日本.txt");
                File.WriteAllText(accented, "A");
                File.WriteAllText(japanese, "J");
                held = new FileStream(accented, FileMode.Open, FileAccess.Read, FileShare.None);
                held2 = new FileStream(japanese, FileMode.Open, FileAccess.Read, FileShare.None);

                var progress = new Seen<TransferProgress>();
                await RoboCopyEngine.RunAsync(new[] { accented, Path.Combine(src, "jp") }, dst, move: false, 4,
                    PasteConflictPolicy.AutoRename, progress, CancellationToken.None);

                var said = progress.Snapshot().Select(p => p.CurrentItem).Where(n => n.StartsWith("Waiting", StringComparison.Ordinal))
                    .Distinct().ToList();
                Check("accent: the window names an accented file in its own letters",
                    said.Any(n => n.StartsWith("Waiting for café ñandú.txt (retry 1", StringComparison.Ordinal)),
                    string.Join(" / ", said));
                Check("accent: and a Japanese one, found from the folder",
                    said.Any(n => n.StartsWith("Waiting for 日本.txt (retry 1", StringComparison.Ordinal)),
                    string.Join(" / ", said));
                Equal("accent: a name read in the wrong page is found by its shape", "café ñandú.txt",
                    RoboCopyEngine.RetryName(Path.Combine(src, "caf‚ ¤and£.txt")));
            }
            finally
            {
                held?.Dispose();
                held2?.Dispose();
                Remove(root);
            }
        }

        /// <summary>
        /// A stream that cannot be copied — another program holds it here; on
        /// FAT, exFAT or a NAS there is nowhere to put one — failed and deleted a
        /// keep-both copy whose file had arrived whole.
        /// </summary>
        private static async Task StreamsBestEffortTests()
        {
            var root = NewDir("streams");
            FileStream? held = null;
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);
                var file = Path.Combine(src, "keep.txt");
                File.WriteAllText(file, "CONTENT");
                File.WriteAllText(file + ":meta", "STREAM");
                File.WriteAllText(Path.Combine(dst, "keep.txt"), "THERE");
                held = new FileStream(file + ":meta", FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                var result = await FileOperations.RunAsync(new[] { file }, dst, false, PasteConflictPolicy.AutoRename,
                    4, 64, null, null, CancellationToken.None);

                var copy = Path.Combine(dst, "keep (2).txt");
                Check("streams: a stream that cannot be read does not fail the copy",
                    result.Failed == 0, string.Join(" | ", result.Errors));
                Check("streams: and the file arrives whole",
                    File.Exists(copy) && File.ReadAllText(copy) == "CONTENT");
                Check("streams: with no half stream beside it", !File.Exists(copy + ":meta"));

                // Straight to a destination with no streams to give: a path that
                // cannot take one stands in for FAT.
                string error = "";
                try { FileOperations.CopyStreams(file, Path.Combine(root, "no-such-folder", "x.txt")); }
                catch (Exception ex) { error = ex.GetType().Name; }
                Equal("streams: nowhere to write them is not an error", "", error);
            }
            finally
            {
                held?.Dispose();
                Remove(root);
            }
        }

        /// <summary>
        /// The managed engine decided "can this be a rename" per file, at about
        /// 0.8ms a call. It is decided once per item now, and a moved folder is
        /// still renamed file by file.
        /// </summary>
        private static async Task RenamePerItemTests()
        {
            var root = NewDir("peritem");
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                var album = Path.Combine(src, "Album");
                Directory.CreateDirectory(Path.Combine(album, "cd2"));
                Directory.CreateDirectory(Path.Combine(dst, "Album"));
                File.WriteAllText(Path.Combine(album, "a.txt"), "A");
                File.WriteAllText(Path.Combine(album, "cd2", "b.txt"), "B");
                var idA = FileId(Path.Combine(album, "a.txt"));
                var idB = FileId(Path.Combine(album, "cd2", "b.txt"));

                // Collides, so keep both takes it through the managed engine.
                var result = await FileOperations.RunAsync(new[] { album }, dst, true, PasteConflictPolicy.AutoRename,
                    4, 64, null, null, CancellationToken.None);
                var moved = Path.Combine(dst, "Album (2)");
                Check("per item: a colliding folder moved on one volume is renamed, file by file",
                    File.Exists(Path.Combine(moved, "a.txt")) && FileId(Path.Combine(moved, "a.txt")) == idA &&
                    File.Exists(Path.Combine(moved, "cd2", "b.txt")) && FileId(Path.Combine(moved, "cd2", "b.txt")) == idB,
                    string.Join(" | ", result.Errors));
                Check("per item: and the source is gone", !Directory.Exists(album));

                var source = SourceText("FileOperations.cs");
                var loop = source[source.IndexOf("await Parallel.ForEachAsync(", StringComparison.Ordinal)..];
                loop = loop[..loop.IndexOf("}).ConfigureAwait(false);", StringComparison.Ordinal)];
                Check("per item: nothing in the per-file loop asks about volumes",
                    loop.Length > 0 && !loop.Contains("SameVolume") && loop.Contains("item.Rename"));
            }
            finally { Remove(root); }
        }

        /// <summary>
        /// A move where one item is renamed and another falls back to copying:
        /// the window said "1 of 2" and then "0 of 1".
        /// </summary>
        private static async Task MonotonicProgressTests()
        {
            var root = NewDir("mono");
            FileStream? held = null;
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);
                var a = Path.Combine(src, "a.txt");
                var b = Path.Combine(src, "b.txt");
                File.WriteAllText(a, "A");
                File.WriteAllText(b, "B");

                // Open without delete sharing: the rename is refused, robocopy can
                // still read it, and so it is copied.
                held = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                var progress = new Seen<TransferProgress>();
                await RoboCopyEngine.RunAsync(new[] { a, b }, dst, move: true, 4,
                    PasteConflictPolicy.AutoRename, progress, CancellationToken.None);

                var counts = progress.Snapshot().Select(p => p.ItemsDone).ToList();
                bool rising = counts.Zip(counts.Skip(1), (x, y) => y >= x).All(ok => ok);
                Check("progress: the item count never goes backwards when a rename falls back to copying",
                    counts.Count > 1 && rising, string.Join(",", counts));
                Check("progress: and the renamed item was renamed", File.Exists(Path.Combine(dst, "a.txt")) && !File.Exists(a));
            }
            finally
            {
                held?.Dispose();
                Remove(root);
            }
        }

        /// <summary>
        /// A folder moved into a junction that points inside it. By their letters
        /// the two have nothing to do with each other; the copy then walked into
        /// its own output, S\inner\S\inner, until the paths were too long.
        /// </summary>
        private static async Task JunctionIntoItselfTests()
        {
            var root = NewDir("loop");
            try
            {
                var s = Path.Combine(root, "S");
                var inner = Path.Combine(s, "inner");
                Directory.CreateDirectory(inner);
                File.WriteAllText(Path.Combine(s, "top.txt"), "TOP");
                File.WriteAllText(Path.Combine(inner, "deep.txt"), "DEEP");
                var j = Path.Combine(root, "J");
                Junction(j, inner);
                if (!Directory.Exists(j)) { Check("loop: the junction fixture could be made", false); return; }

                Check("loop: a path through a junction is resolved to where it leads",
                    ReparseLinks.ResolvedWithin(Path.Combine(j, "S"), s),
                    ReparseLinks.Resolved(Path.Combine(j, "S")));
                Check("loop: and an ordinary folder beside it is not inside",
                    !ReparseLinks.ResolvedWithin(Path.Combine(root, "other", "S"), s));

                foreach (var (engine, move) in new[] { ("robocopy", true), ("robocopy", false), ("managed", true), ("managed", false) })
                {
                    int failed;
                    if (engine == "robocopy")
                        failed = (await RoboCopyEngine.RunAsync(new[] { s }, j, move, 4,
                            PasteConflictPolicy.AutoRename, null, CancellationToken.None)).Failed;
                    else
                        failed = (await FileOperations.RunAsync(new[] { s }, j, move, PasteConflictPolicy.AutoRename,
                            4, 64, null, null, CancellationToken.None)).Failed;

                    var what = $"loop ({engine} {(move ? "move" : "copy")})";
                    Check($"{what}: refused as into itself", failed > 0, $"failed={failed}");
                    Check($"{what}: nothing was made inside the source", !Directory.Exists(Path.Combine(inner, "S")));
                    Check($"{what}: and the source is whole",
                        File.Exists(Path.Combine(s, "top.txt")) && File.Exists(Path.Combine(inner, "deep.txt")));
                }
            }
            finally
            {
                try { Directory.Delete(Path.Combine(root, "J"), false); } catch { }
                Remove(root);
            }
        }

        /// <summary>
        /// A subst letter. GetVolumePathName answers for it, so the branch that
        /// resolved it never ran, and a move from it to the disk underneath was a
        /// copy. Opt-in, because it puts a drive letter in front of anything
        /// watching the drive list while it runs.
        /// </summary>
        private static void SubstTests()
        {
            var source = SourceText("RoboCopyEngine.cs");
            var volumePath = Method(source, "private static string? VolumePathOf(");
            Check("subst: the letter is resolved before the volume path is asked",
                volumePath.IndexOf("QueryDosDeviceW", StringComparison.Ordinal) >= 0 &&
                volumePath.IndexOf("QueryDosDeviceW", StringComparison.Ordinal) <
                volumePath.IndexOf("GetVolumePathNameW", StringComparison.Ordinal));

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
                Check("subst: a subst letter is the volume it stands for",
                    RoboCopyEngine.SameVolume(letter + "\\x.txt", Path.GetTempPath()),
                    $"{RoboCopyEngine.VolumeOf(letter + "\\x.txt") ?? "null"} against {RoboCopyEngine.VolumeOf(Path.GetTempPath())}");

                Directory.CreateDirectory(Path.Combine(folder, "dst"));
                var file = Path.Combine(folder, "move.bin");
                File.WriteAllText(file, "MOVE");
                var id = FileId(file);
                var result = RoboCopyEngine.RunAsync(new[] { letter + "\\move.bin" }, Path.Combine(folder, "dst"), move: true, 4,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None).GetAwaiter().GetResult();
                var moved = Path.Combine(folder, "dst", "move.bin");
                Check("subst: a move from the letter to the disk under it is a rename",
                    File.Exists(moved) && FileId(moved) == id, string.Join(" | ", result.Errors));
            }
            finally
            {
                Subst($"{letter} /d");
                Remove(folder);
            }
        }

        private static void ShellMenuDotTests()
        {
            Check("menu: a name ending in a dot is refused", ShellContextMenu.MisreadNames(new[] { @"C:\x\report." }));
            Check("menu: and one ending in a space", ShellContextMenu.MisreadNames(new[] { @"C:\x\a.txt", @"C:\x\trail " }));
            Check("menu: and one inside a folder ending in a dot", ShellContextMenu.MisreadNames(new[] { @"C:\fold.\a.txt" }));
            Check("menu: and the literal form of one", ShellContextMenu.MisreadNames(new[] { @"\\?\C:\x\report." }));
            Check("menu: an ordinary selection is not", !ShellContextMenu.MisreadNames(new[] { @"C:\x\report.txt", @"C:\x\.gitignore", @"D:\" }));

            var outcome = ShellContextMenu.Show(new System.Windows.Forms.NativeWindow(), new[] { @"C:\x\report." }, 0, 0, 0);
            Equal("menu: Show refuses it before building anything", "AwkwardName", outcome.ToString());

            var main = SourceText("MainForm.cs");
            Check("menu: the window says why", main.Contains("case ShellContextMenu.MenuOutcome.AwkwardName:"));
        }

        private static void TypeAheadFoldingTests()
        {
            Equal("fold: fullwidth letters", "ABC", TypeAhead.Fold("ＡＢＣ"));
            Equal("fold: a ligature", "file", TypeAhead.Fold("ﬁle"));
            Equal("fold: Æ", "AEble", TypeAhead.Fold("Æble"));
            Equal("fold: Ø", "Oresund", TypeAhead.Fold("Øresund"));
            Equal("fold: Ł", "Lodz", TypeAhead.Fold("Łódź"));
            Equal("fold: ß", "Strasse", TypeAhead.Fold("Straße"));
            Equal("fold: accents as before", "Eclair", TypeAhead.Fold("Éclair"));
            Equal("fold: ASCII untouched", "plain.txt", TypeAhead.Fold("plain.txt"));

            var names = new[] { "Apple", "Æble", "Banana", "Øresund", "Pear" };
            Equal("fold: typing ae finds Æble", "Æble", names[TypeAhead.Find(names.Length, i => names[i], "ae", 0)]);
            Equal("fold: typing or finds Øresund", "Øresund", names[TypeAhead.Find(names.Length, i => names[i], "or", 0)]);

            // A repeat is decided from what was typed, then folded: Æ twice was
            // "AEAE" and found nothing, and one ß was "ss", a repeated S.
            var letters = new[] { "Æble", "Ærø", "Apple", "Æsir", "Sand", "ssh", "Straße" };
            int ae = TypeAhead.Find(letters.Length, i => letters[i], "ÆÆ", 0);
            Equal("fold: Æ pressed twice moves to the next Æ name", "Ærø", ae < 0 ? "(nothing)" : letters[ae]);
            int ae3 = TypeAhead.Find(letters.Length, i => letters[i], "æÆæ", 1);
            Equal("fold: and a third time to the one after", "Æsir", ae3 < 0 ? "(nothing)" : letters[ae3]);
            int sz = TypeAhead.Find(letters.Length, i => letters[i], "ß", 0);
            Equal("fold: a lone ß looks for ss, not every S", "ssh", sz < 0 ? "(nothing)" : letters[sz]);
            int s = TypeAhead.Find(letters.Length, i => letters[i], "s", 4);
            Equal("fold: a plain s still cycles the S names", "ssh", s < 0 ? "(nothing)" : letters[s]);
            var es = new[] { "Eclair", "Éclat", "Fig" };
            int e = TypeAhead.Find(es.Length, i => es[i], "eé", 0);
            Equal("fold: e then é is still one key pressed twice", "Éclat", e < 0 ? "(nothing)" : es[e]);
        }

        /// <summary>
        /// "Waiting for x’y.txt", not "Waiting for x'y.txt": robocopy's OEM output
        /// best-fits what the page cannot hold, so an all-ASCII name may be no
        /// file at all. Real robocopy, real locked files.
        /// </summary>
        private static async Task BestFitRetryNameTests()
        {
            var root = NewDir("bestfit");
            var held = new List<FileStream>();
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);
                string quote = "x’y.txt", macron = "aāb.txt", ligature = "œuvre.txt";
                foreach (var n in new[] { quote, macron })
                {
                    var p = Path.Combine(src, n);
                    File.WriteAllText(p, n);
                    held.Add(new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None));
                }
                File.WriteAllText(Path.Combine(src, ligature), "o");

                Check("best fit: the OEM page really does flatten these names",
                    RoboCopyEngine.OemForm(macron) != macron && RoboCopyEngine.OemForm(quote) != quote,
                    RoboCopyEngine.OemForm(macron) + " / " + RoboCopyEngine.OemForm(quote));
                Equal("best fit: a flattened name is found in the folder", macron,
                    RoboCopyEngine.RetryName(Path.Combine(src, RoboCopyEngine.OemForm(macron))));
                Equal("best fit: a ligature too", ligature,
                    RoboCopyEngine.RetryName(Path.Combine(src, RoboCopyEngine.OemForm(ligature))));
                Equal("best fit: from the plan when the folder cannot be read", quote,
                    RoboCopyEngine.RetryName(Path.Combine(root, "gone", RoboCopyEngine.OemForm(quote)),
                        new[] { "other.txt", quote }));
                Equal("best fit: nothing matches, so the file in progress", "song.flac",
                    RoboCopyEngine.RetryName(Path.Combine(src, "nowhere.txt"), new[] { "other.txt" }, "song.flac"));
                Equal("best fit: a name that is there is said as it is", ligature,
                    RoboCopyEngine.RetryName(Path.Combine(src, ligature), null, "song.flac"));

                var progress = new Seen<TransferProgress>();
                await RoboCopyEngine.RunAsync(new[] { Path.Combine(src, quote), Path.Combine(src, macron) }, dst,
                    move: false, 4, PasteConflictPolicy.AutoRename, progress, CancellationToken.None);

                var said = progress.Snapshot().Select(p => p.CurrentItem).Where(n => n.StartsWith("Waiting", StringComparison.Ordinal))
                    .Distinct().ToList();
                Check("best fit: the window names x’y.txt, not x'y.txt",
                    said.Any(n => n.StartsWith($"Waiting for {quote} (retry 1", StringComparison.Ordinal)),
                    string.Join(" / ", said));
                Check("best fit: and aāb.txt, not aab.txt",
                    said.Any(n => n.StartsWith($"Waiting for {macron} (retry 1", StringComparison.Ordinal)),
                    string.Join(" / ", said));
            }
            finally
            {
                foreach (var h in held) h.Dispose();
                Remove(root);
            }
        }

        /// <summary>
        /// Reset keeps what makes the kept things work, and refuses while the
        /// file cannot be read — which used to save defaults over it.
        /// </summary>
        private static void ResetTests()
        {
            var old = new Settings
            {
                ConnectCode = "12345678",
                GoogleDriveEnabled = true,
                WebAppEnabled = true,
                WebAppPort = 50123,
                SetAsDefaultFileExplorer = true,
                RegisterContextMenu = true,
                ActiveTab = 1,
                FocusedPathTab1 = @"C:\a\x.txt",
                FocusedPathTab2 = @"D:\b\y.txt",
                AudioLastPath = @"C:\music\song.flac",
                StartPath = @"C:\start",
            };
            var reset = SettingsReset.Defaults(old, @"C:\one", @"D:\two");
            Check("reset: keeps Google Drive on", reset.GoogleDriveEnabled);
            Check("reset: keeps the web app on, on its port", reset.WebAppEnabled && reset.WebAppPort == 50123);
            Check("reset: keeps folders opening here and the context-menu entry",
                reset.SetAsDefaultFileExplorer && reset.RegisterContextMenu);
            Check("reset: keeps where you were",
                reset.ActiveTab == 1 && reset.FocusedPathTab1 == old.FocusedPathTab1 &&
                reset.FocusedPathTab2 == old.FocusedPathTab2 && reset.AudioLastPath == old.AudioLastPath &&
                reset.StartPath == old.StartPath);
            Check("reset: the question says so",
                SettingsReset.KeptSentence.Contains("Google Drive") && SettingsReset.KeptSentence.Contains("web app") &&
                SettingsReset.KeptSentence.Contains("opens your folders"));

            var form = SourceText("SettingsForm.cs");
            Check("reset: the question no longer warns of folders going back",
                form.Length > 0 && !form.Contains("Windows Explorer will take them back"));

            // The repro: settings.json held, Load, reset, and the file read again.
            const string scratch =
                @"C:\Users\Conner\AppData\Local\Temp\claude\C--Users-Conner\210da054-69e3-4f58-81af-73a744528057\scratchpad";
            var dir = Path.Combine(Directory.Exists(scratch) ? Path.Combine(scratch, "fix-r2-files") : Path.GetTempPath(),
                "settings-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var before = Settings.OverrideAppDataDir;
            Settings.OverrideAppDataDir = dir;
            try
            {
                new Settings { ConnectCode = "11112222", DriveSyncPairs = new List<DriveSyncPair> { new() } }.Save();
                Settings live;
                using (new FileStream(Path.Combine(dir, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    live = Settings.Load(out _);
                Check("reset: a held file is unreadable", live.UnreadableOnDisk);
                Check("reset: and a reset is refused, with a sentence", SettingsReset.Refusal(live) is { Length: > 0 });

                var after = Settings.Load();
                Check("reset: so the file still has its pairing code and its pairs",
                    after.ConnectCode == "11112222" && after.DriveSyncPairs.Count == 1);
                Check("reset: a readable file may be reset", SettingsReset.Refusal(after) == null);
            }
            finally
            {
                Settings.OverrideAppDataDir = before;
                try { Directory.Delete(dir, true); } catch { }
            }

            var main = SourceText("MainForm.cs");
            var core = Method(main, "private void OpenPreferencesCore(");
            Check("reset: the window asks before resetting", core.Contains("SettingsReset.Refusal(_settings)"));
            Check("reset: and says when the save failed",
                core.Contains("if (Program.ApplySettings(SettingsReset.Defaults("));
        }

        private static void MainFormSourceTests()
        {
            var main = SourceText("MainForm.cs");
            var update = Method(main, "private async void UpdateRowsInPlace(");
            Check("rows: an in-place update reads the exact file", update.Contains("new FileInfo(NameRules.ExactPath(path))"));
            Check("rows: and gives up when the folder has changed under it",
                update.Contains("|| pane.Loading)\r\n                return;") || update.Contains("|| pane.Loading)\n                return;"));
        }
    }
}
