using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// 7z through the bundled 7-Zip (4.2.1): every core but one, names in any
    /// language, locked files said, damage said, Cancel leaves nothing.
    /// </summary>
    internal static class SevenZipTests
    {
        private sealed class Collect : IProgress<TransferProgress>
        {
            public readonly List<TransferProgress> Seen = new();
            public void Report(TransferProgress value) { lock (Seen) Seen.Add(value); }
        }

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var exe = BsdTar.SevenZipExecutable;
            check("7-Zip is carried in the build and unpacks", exe != null && File.Exists(exe), exe);
            if (exe == null) return;
            check("7z goes through 7-Zip", BsdTar.UsesSevenZip(ArchiveFormats.SevenZip), null);
            // 4.2.5: every core, below normal priority (it left one free before).
            check("7-Zip is given every core", BsdTar.SevenZipThreads == Environment.ProcessorCount, null);
            check("a second ask reuses the unpacked copy", BsdTar.SevenZipExecutable == exe, null);

            var dir = Path.Combine(Path.GetTempPath(), "EN-7z-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var src = Path.Combine(dir, "Música 日本");
                Directory.CreateDirectory(Path.Combine(src, "sub", "empty"));
                var random = new Random(7);
                var big = new byte[3_000_000];
                random.NextBytes(big);
                File.WriteAllBytes(Path.Combine(src, "sub", "random.bin"), big);
                File.WriteAllText(Path.Combine(src, "café ü.txt"), string.Concat(Enumerable.Repeat("hello ", 50_000)));
                File.WriteAllText(Path.Combine(src, "Δelta – “quoted”.txt"), "unicode");

                // Round trip, names in several scripts, an empty folder.
                var archive = Path.Combine(dir, "out.7z");
                var progress = new Collect();
                var made = await ArchiveEngine.CompressAsync(new[] { src }, archive, ArchiveFormats.SevenZip,
                    ArchiveLevel.Balanced, 4, progress, CancellationToken.None);
                check("7-Zip made the archive with no failures", made.Failed == 0 && File.Exists(archive),
                    string.Join("; ", made.Errors));
                check("compressing reported progress", progress.Seen.Count > 0, null);

                var into = Path.Combine(dir, "back");
                var back = await ArchiveEngine.ExtractAsync(archive, into, PasteConflictPolicy.AutoRename, null, 4,
                    null, CancellationToken.None);
                check("7-Zip extracted with no failures", back.Failed == 0, string.Join("; ", back.Errors));
                var root = Path.Combine(into, "Música 日本");
                check("non-ASCII names come back exactly", File.Exists(Path.Combine(root, "café ü.txt")) &&
                                                         File.Exists(Path.Combine(root, "Δelta – “quoted”.txt")), null);
                check("contents come back exactly",
                    File.Exists(Path.Combine(root, "sub", "random.bin")) &&
                    File.ReadAllBytes(Path.Combine(root, "sub", "random.bin")).AsSpan().SequenceEqual(big), null);
                check("an empty folder comes back", Directory.Exists(Path.Combine(root, "sub", "empty")), null);
                check("no staging folder is left behind",
                    !Directory.EnumerateDirectories(into, ".extracting-*").Any(), null);

                // Extracting again: the conflict rules still apply.
                var again = await ArchiveEngine.ExtractAsync(archive, into, PasteConflictPolicy.AutoRename, null, 4,
                    null, CancellationToken.None);
                check("extracting over the same folder keeps both", again.Failed == 0 &&
                    Directory.Exists(Path.Combine(into, "Música 日本 (2)")) || Directory.GetDirectories(into).Length == 2,
                    string.Join(", ", Directory.GetDirectories(into).Select(Path.GetFileName)));

                // A file another program holds is named and left out; the rest go in.
                var locked = Path.Combine(src, "locked.bin");
                File.WriteAllBytes(locked, new byte[1000]);
                var archive2 = Path.Combine(dir, "locked.7z");
                ArchiveResult withLock;
                using (File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    withLock = await ArchiveEngine.CompressAsync(new[] { src }, archive2, ArchiveFormats.SevenZip,
                        ArchiveLevel.Fastest, 4, null, CancellationToken.None);
                check("a locked file is said by name", withLock.Errors.Any(e => e.Contains("locked.bin", StringComparison.Ordinal)),
                    string.Join("; ", withLock.Errors));
                check("and the archive is still made", File.Exists(archive2), null);
                File.Delete(locked);

                // A damaged archive: said, never shown as fine.
                var bytes = File.ReadAllBytes(archive);
                var cut = Path.Combine(dir, "cut.7z");
                File.WriteAllBytes(cut, bytes[..(bytes.Length / 2)]);
                var damaged = await ArchiveEngine.ExtractAsync(cut, Path.Combine(dir, "cutout"),
                    PasteConflictPolicy.AutoRename, null, 4, null, CancellationToken.None);
                check("a cut-off 7z is said to be damaged", damaged.Failed > 0 &&
                    damaged.Errors.Any(e => e.Contains("damaged", StringComparison.OrdinalIgnoreCase)),
                    string.Join("; ", damaged.Errors));

                // Cancel while compressing: no archive left with the name.
                var bigSrc = Path.Combine(dir, "big");
                Directory.CreateDirectory(bigSrc);
                var chunk = new byte[16_000_000];
                for (int i = 0; i < 12; i++) { random.NextBytes(chunk); File.WriteAllBytes(Path.Combine(bigSrc, $"f{i}.bin"), chunk); }
                var cancelled = Path.Combine(dir, "cancelled.7z");
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
                var stopped = await ArchiveEngine.CompressAsync(new[] { bigSrc }, cancelled, ArchiveFormats.SevenZip,
                    ArchiveLevel.Smallest, 4, null, cts.Token);
                check("Cancel stops 7-Zip", stopped.Cancelled, null);
                check("and leaves no half-made archive", !File.Exists(cancelled), null);
                await Task.Delay(300);
                check("and no 7-Zip is left running",
                    !System.Diagnostics.Process.GetProcessesByName("7za").Any(p =>
                    {
                        try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); }
                        catch { return false; }
                    }), null);

                // tar.xz and tar.bz2 are made and unpacked by 7-Zip in two halves,
                // one writing or reading the tar and one compressing it (4.2.1).
                {
                    var plain = Path.Combine(dir, "plain");
                    Directory.CreateDirectory(Path.Combine(plain, "deep", "er"));
                    File.WriteAllBytes(Path.Combine(plain, "deep", "er", "random.bin"), big);
                    File.WriteAllText(Path.Combine(plain, "note.txt"), "plain");
                    File.WriteAllText(Path.Combine(plain, "café 日本.txt"), "named");
                    foreach (var format in new[] { ArchiveFormats.ById("tar.xz")!, ArchiveFormats.ById("tar.bz2")! })
                    {
                        check($"{format.Id} is made and unpacked by 7-Zip",
                            BsdTar.UsesSevenZip(format) && BsdTar.UnpacksWithSevenZip(format), null);
                        var packed = Path.Combine(dir, "plain" + format.Extension);
                        var madeTar = await ArchiveEngine.CompressAsync(new[] { plain }, packed, format,
                            ArchiveLevel.Fastest, 4, null, CancellationToken.None);
                        check($"{format.Id}: 7-Zip made it, a name in another language too", madeTar.Failed == 0 && File.Exists(packed),
                            string.Join("; ", madeTar.Errors));
                        var outDir = Path.Combine(dir, "out-" + format.Id);
                        var seen = new Collect();
                        var unpacked = await ArchiveEngine.ExtractAsync(packed, outDir, PasteConflictPolicy.AutoRename,
                            null, 4, seen, CancellationToken.None);
                        var got = Path.Combine(outDir, "plain", "deep", "er", "random.bin");
                        check($"{format.Id}: 7-Zip unpacked it whole", unpacked.Failed == 0 && File.Exists(got) &&
                            File.ReadAllBytes(got).AsSpan().SequenceEqual(big) &&
                            File.ReadAllText(Path.Combine(outDir, "plain", "note.txt")) == "plain" &&
                            File.ReadAllText(Path.Combine(outDir, "plain", "café 日本.txt")) == "named",
                            string.Join("; ", unpacked.Errors));
                        check($"{format.Id}: and reported progress", seen.Seen.Count > 0, null);
                        check($"{format.Id}: no staging folder is left",
                            !Directory.EnumerateDirectories(outDir, ".extracting-*").Any(), null);

                        var raw = File.ReadAllBytes(packed);
                        var broken = Path.Combine(dir, "broken" + format.Extension);
                        File.WriteAllBytes(broken, raw[..(raw.Length * 2 / 3)]);
                        var bad = await ArchiveEngine.ExtractAsync(broken, Path.Combine(dir, "bad-" + format.Id),
                            PasteConflictPolicy.AutoRename, null, 4, null, CancellationToken.None);
                        check($"{format.Id}: a cut-off one is said to be damaged", bad.Failed > 0 &&
                            bad.Errors.Any(e => e.Contains("damaged", StringComparison.OrdinalIgnoreCase)),
                            string.Join("; ", bad.Errors));
                    }
                    await Task.Delay(300);
                    check("and no 7-Zip half is left running after them",
                        !System.Diagnostics.Process.GetProcessesByName("7za").Any(p =>
                        {
                            try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); }
                            catch { return false; }
                        }), null);
                }

                // The warnings summary, as 7-Zip writes it.
                var warned = BsdTar.FileWarnings(new[]
                {
                    "WARNINGS for files:", "", @"C:\a\b c.txt : The process cannot access the file.", "----------------",
                });
                equal("a warning line gives the path", @"C:\a\b c.txt", warned.FirstOrDefault().Path);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
