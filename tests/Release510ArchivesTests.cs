using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// 5.1.0: archives: password-protected archives and Test archive.
    /// </summary>
    internal static class Release510ArchivesTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("5.1.0, archives: password-protected archives and Test archive:");
            CompressDialogAccessKeys();
            PlainTarNamedTarGz();
            TarHoldingOtherCompression();
            DamageWordInNames();
            IdleCacheSwept();
            HardLinkSizes();
            await Task.CompletedTask;
        }

        private static void WriteTar(string path, System.Formats.Tar.TarEntryFormat format,
            params System.Formats.Tar.TarEntry[] entries)
        {
            using var file = File.Create(path);
            using var writer = new System.Formats.Tar.TarWriter(file, format, leaveOpen: false);
            foreach (var e in entries) writer.WriteEntry(e);
        }

        private static System.Formats.Tar.UstarTarEntry UstarFile(string name, byte[] data) =>
            new(System.Formats.Tar.TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) };

        // Bug 3: a browser unpacked backup.tar.gz while saving; it is a plain tar.
        private static void PlainTarNamedTarGz()
        {
            var dir = Scratch();
            try
            {
                var path = Path.Combine(dir, "backup.tar.gz");
                WriteTar(path, System.Formats.Tar.TarEntryFormat.Ustar, UstarFile("notes.txt", new byte[] { 1, 2, 3 }));
                Equal("a plain tar named .tar.gz is read as a tar", "tar", ArchiveFormats.Identify(path)?.Id ?? "null");
                var listing = ArchiveBrowser.Open(path, null, CancellationToken.None);
                Check("and it opens with its file", listing.Find("notes.txt") != null && !listing.Damaged);

                var real = Path.Combine(dir, "real.tar.gz");
                using (var gz = new System.IO.Compression.GZipStream(File.Create(real), System.IO.Compression.CompressionLevel.Fastest))
                using (var writer = new System.Formats.Tar.TarWriter(gz, System.Formats.Tar.TarEntryFormat.Ustar))
                    writer.WriteEntry(UstarFile("a.txt", new byte[] { 9 }));
                Equal("a real .tar.gz is still one", "tar.gz", ArchiveFormats.Identify(real)?.Id ?? "null");
            }
            finally { Clean(dir); }
        }

        // Bug 4: a .tar that is xz, bzip2 or zstd inside was taken for a bare stream.
        private static void TarHoldingOtherCompression()
        {
            var dir = Scratch();
            try
            {
                void Case(string expected, params byte[] head)
                {
                    var path = Path.Combine(dir, expected + ".tar");
                    var bytes = new byte[600];
                    head.CopyTo(bytes, 0);
                    File.WriteAllBytes(path, bytes);
                    Equal($"a .tar holding {expected} is a tar.{expected}", "tar." + expected,
                        ArchiveFormats.Identify(path)?.Id ?? "null");
                }
                Case("xz", 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00);
                Case("bz2", 0x42, 0x5A, 0x68, 0x39);
                Case("zst", 0x28, 0xB5, 0x2F, 0xFD);
            }
            finally { Clean(dir); }
        }

        // Bug 5: a member called "damaged ..." made a whole archive damaged.
        private static void DamageWordInNames()
        {
            Check("a name with 'damaged' in it is not damage",
                !BsdTar.LooksDamaged(@"tar.exe: damaged photos/a.jpg: Can't create '\\?\C:\x\damaged photos\a.jpg': Invalid argument"));
            Check("nor one with 'corrupt' quoted",
                !BsdTar.LooksDamaged(@"Can't create '\\?\C:\x\Bob's corrupt.txt'"));
            Check("a truncated archive still is",
                BsdTar.LooksDamaged("tar.exe: Truncated input file (needed 512 bytes, only 0 available)"));
            Check("and so is one that will not open",
                BsdTar.LooksDamaged("tar.exe: Error opening archive: Failed to open 'C:\\x\\a.rar'"));
            Check("and a member said to be damaged",
                BsdTar.LooksDamaged("tar.exe: photos/a.jpg: Damaged tar archive"));
        }

        // Bug 6: open-, tree- and copy- folders stayed in %TEMP% until Quit.
        private static void IdleCacheSwept()
        {
            var root = Scratch();
            try
            {
                var now = DateTime.UtcNow;
                string Make(string name) { var f = Path.Combine(root, name); Directory.CreateDirectory(f); File.WriteAllText(Path.Combine(f, "x"), "x"); return f; }
                var oldOpen = Make("open-old");
                var oldTree = Make("tree-old");
                File.WriteAllText(oldTree + ".done", "");
                var freshOpen = Make("open-fresh");
                var notOurs = Make("open-earlierrun");
                ArchiveBrowser.Used(oldOpen, now - TimeSpan.FromDays(2));
                ArchiveBrowser.Used(oldTree, now - TimeSpan.FromDays(2));
                ArchiveBrowser.Used(freshOpen, now - TimeSpan.FromHours(1));
                ArchiveBrowser.SweepIdle(root, now);
                Check("an opened file unused for two days is cleared", !Directory.Exists(oldOpen));
                Check("so is an extracted archive, with its marker", !Directory.Exists(oldTree) && !File.Exists(oldTree + ".done"));
                Check("one used an hour ago stays", Directory.Exists(freshOpen));
                Check("a folder this run did not make is left to the launch sweep", Directory.Exists(notOurs));
            }
            finally { Clean(root); }
        }

        // Bug 7: hard links in a tar were listed as 0 bytes.
        private static void HardLinkSizes()
        {
            var dir = Scratch();
            try
            {
                var path = Path.Combine(dir, "layer.tar");
                WriteTar(path, System.Formats.Tar.TarEntryFormat.Gnu,
                    new System.Formats.Tar.GnuTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "bin/tool")
                    { DataStream = new MemoryStream(new byte[1000]) },
                    new System.Formats.Tar.GnuTarEntry(System.Formats.Tar.TarEntryType.HardLink, "bin/tool2")
                    { LinkName = "bin/tool" });
                var listing = ArchiveBrowser.Open(path, null, CancellationToken.None);
                Equal("a hard link is as big as its file", "1000", listing.Find("bin/tool2")?.Size.ToString() ?? "missing");
                Equal("and the folder counts both", "2000", listing.Find("bin")?.Size.ToString() ?? "missing");
            }
            finally { Clean(dir); }
        }

        private static string Scratch()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en510arc-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Clean(string dir)
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        private static void OnSta(Action body)
        {
            Exception? failure = null;
            var thread = new Thread(() => { try { body(); } catch (Exception e) { failure = e; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(60000)) Check("UI thread finished", false, "timed out");
            if (failure != null) Check("UI thread ran", false, failure.ToString());
        }

        private static IEnumerable<Control> All(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (var inner in All(c)) yield return inner;
            }
        }

        internal static char? AccessKey(string text)
        {
            for (int i = 0; i < text.Length - 1; i++)
            {
                if (text[i] != '&') continue;
                if (text[i + 1] == '&') { i++; continue; }
                return char.ToUpperInvariant(text[i + 1]);
            }
            return null;
        }

        // Bugs 1 and 2: Alt+C went to the Compression list, not the Compress button.
        private static void CompressDialogAccessKeys()
        {
            var dir = Scratch();
            try
            {
                var file = Path.Combine(dir, "a.txt");
                File.WriteAllText(file, "hello");
                OnSta(() =>
                {
                    using var window = new ArchiveForm(dir, new[] { file },
                        ArchiveFormats.Zip, ArchiveLevel.Balanced, singleFile: true);
                    var keys = All(window).Select(c => AccessKey(c.Text ?? ""))
                        .Where(k => k != null).Select(k => k!.Value).ToList();
                    Check("Compress dialog access keys are unique", keys.Count == keys.Distinct().Count(),
                        string.Join(",", keys));
                    var compress = All(window).OfType<Button>().FirstOrDefault(b => b.Text.Replace("&", "") == "Compress");
                    Check("Compress button keeps Alt+C", compress != null && AccessKey(compress.Text) == 'C');
                });
            }
            finally { Clean(dir); }
        }
    }
}
