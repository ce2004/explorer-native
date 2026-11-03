using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
            await ProtectedArchives();
            TestArchive();
        }

        // ---------- Feature B: Test archive ----------

        private static void Flip(string path, long at)
        {
            var bytes = File.ReadAllBytes(path);
            bytes[at] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
        }

        private static void TestArchive()
        {
            var dir = Scratch();
            var before = ArchivePasswords.Asker;
            try
            {
                var src = Path.Combine(dir, "src");
                Directory.CreateDirectory(Path.Combine(src, "sub"));
                var random = new Random(5);
                var files = new List<string>();
                for (int i = 1; i <= 4; i++)
                {
                    var data = new byte[200_000];
                    random.NextBytes(data);
                    var f = Path.Combine(src, i == 4 ? "sub" : "", $"f{i}.bin");
                    File.WriteAllBytes(f, data);
                    files.Add(f);
                }

                ArchiveTestResult Run(string path) => ArchiveTester.Test(path, null, CancellationToken.None);

                // Zip, stored so a flipped byte lands inside one file.
                var zip = Path.Combine(dir, "photos.zip");
                using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
                    for (int i = 0; i < files.Count; i++)
                        z.CreateEntryFromFile(files[i], i == 3 ? "sub/f4.bin" : $"f{i + 1}.bin", CompressionLevel.NoCompression);
                Check("Test archive is offered for a zip", ArchiveTester.CanTest(zip));
                Check("but not for a text file", !ArchiveTester.CanTest(Path.Combine(dir, "notes.txt")));
                Equal("a good zip is fine", "photos.zip is fine: all 4 files are intact.", Run(zip).Sentence);
                var badZip = Path.Combine(dir, "bad.zip");
                File.Copy(zip, badZip);
                Flip(badZip, 300_000);
                var r = Run(badZip);
                Equal("a zip with a flipped byte is damaged", "bad.zip is damaged: 1 of 4 files is bad.", r.Sentence);
                Equal("and names the bad file", "f2.bin", string.Join(",", r.Bad));

                // tar.gz through the gzip reader.
                var tgz = Path.Combine(dir, "backup.tar.gz");
                using (var gz = new GZipStream(File.Create(tgz), CompressionLevel.Fastest))
                using (var writer = new System.Formats.Tar.TarWriter(gz, System.Formats.Tar.TarEntryFormat.Pax))
                    for (int i = 0; i < files.Count; i++) writer.WriteEntry(files[i], $"f{i + 1}.bin");
                Equal("a good tar.gz is fine", "backup.tar.gz is fine: all 4 files are intact.", Run(tgz).Sentence);
                var badTgz = Path.Combine(dir, "badbackup.tar.gz");
                File.Copy(tgz, badTgz);
                Flip(badTgz, 300_000);
                r = Run(badTgz);
                Check("a tar.gz with a flipped byte is damaged", !r.Fine, r.Sentence);
                Check("and says where", r.Bad.Count == 1 && r.Bad[0].StartsWith("f", StringComparison.Ordinal), r.Sentence);

                if (BsdTar.SevenZipExecutable == null) { Check("7-Zip is bundled for Test archive", false); return; }

                // 7z through 7za t.
                var sz = Path.Combine(dir, "photos.7z");
                Check("7za makes a 7z", Make7za(sz, new[] { "-t7z", "-ms=off", "-mx=1" }, src + "\\*"));
                Equal("a good 7z is fine", "photos.7z is fine: all 4 files are intact.", Run(sz).Sentence);
                var badSz = Path.Combine(dir, "bad.7z");
                File.Copy(sz, badSz);
                Flip(badSz, 300_000);
                r = Run(badSz);
                Equal("a 7z with a flipped byte is damaged", "bad.7z is damaged: 1 of 4 files is bad.", r.Sentence);

                // tar.xz counts the files inside the tar.
                var txz = Path.Combine(dir, "backup.tar.xz");
                var plainTar = Path.Combine(dir, "backup.tar");
                using (var t = File.Create(plainTar))
                using (var writer = new System.Formats.Tar.TarWriter(t, System.Formats.Tar.TarEntryFormat.Pax))
                    for (int i = 0; i < files.Count; i++) writer.WriteEntry(files[i], $"f{i + 1}.bin");
                Equal("a plain tar is fine", "backup.tar is fine: all 4 files are intact.", Run(plainTar).Sentence);
                Check("7za makes a tar.xz", Make7za(txz, new[] { "-txz" }, plainTar));
                Equal("a good tar.xz is fine", "backup.tar.xz is fine: all 4 files are intact.", Run(txz).Sentence);
                var badTxz = Path.Combine(dir, "bad.tar.xz");
                File.Copy(txz, badTxz);
                Flip(badTxz, 300_000);
                Check("a tar.xz with a flipped byte is damaged", !Run(badTxz).Fine);

                // A protected archive asks for the password first.
                var locked = Path.Combine(dir, "locked.7z");
                Check("7za makes a protected 7z", Make7za(locked, new[] { "-t7z", "-psesame", "-mhe=on" }, files[0]));
                ArchivePasswords.ForgetAll();
                var asked = FakeAsker("sesame");
                Equal("a protected 7z is tested with its password", "locked.7z is fine: its one file is intact.", Run(locked).Sentence);
                Equal("after one question", "locked.7z needs a password", string.Join(" | ", asked));

                // Cancelling stops it.
                bool cancelled = false;
                using (var cts = new CancellationTokenSource())
                {
                    cts.Cancel();
                    try { ArchiveTester.Test(zip, null, cts.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                }
                Check("Test archive can be cancelled", cancelled);

                Check("the menu item has its own access key",
                    SourceText("MainForm.cs").Contains("Item(\"Test &archive\"", StringComparison.Ordinal));
            }
            finally
            {
                ArchivePasswords.Asker = before;
                ArchivePasswords.ForgetAll();
                Clean(dir);
            }
        }

        private static string SourceText(string name)
        {
            var at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at != null && !Directory.Exists(Path.Combine(at.FullName, "src"))) at = at.Parent;
            if (at == null) return "";
            var path = Path.Combine(at.FullName, "src", name);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        // ---------- Feature A: password-protected archives ----------

        /// <summary>Makes an archive with the bundled 7za. Test-only: the password is on its command line.</summary>
        internal static bool Make7za(string archive, IEnumerable<string> switches, params string[] files)
        {
            var exe = BsdTar.SevenZipExecutable;
            if (exe == null) return false;
            var start = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("a");
            foreach (var s in switches) start.ArgumentList.Add(s);
            start.ArgumentList.Add("-sccUTF-8");
            start.ArgumentList.Add(archive);
            foreach (var f in files) start.ArgumentList.Add(f);
            using var p = System.Diagnostics.Process.Start(start)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            return p.ExitCode == 0 && File.Exists(archive);
        }

        /// <summary>An asker that gives the answers in turn and notes each question.</summary>
        internal static List<string> FakeAsker(params string?[] answers)
        {
            var asked = new List<string>();
            var queue = new Queue<string?>(answers);
            ArchivePasswords.Asker = (path, wrong) =>
            {
                lock (asked) asked.Add(wrong ? ArchivePasswords.WrongSaid(path) : ArchivePasswords.Question(path));
                return queue.Count > 0 ? queue.Dequeue() : null;
            };
            return asked;
        }

        private static async Task ProtectedArchives()
        {
            if (BsdTar.SevenZipExecutable == null) { Check("7-Zip is bundled for the password tests", false); return; }
            var dir = Scratch();
            var before = ArchivePasswords.Asker;
            var logBefore = Environment.GetEnvironmentVariable("EXPLORERNATIVE_TRANSFERLOG");
            const string secret = "Tr0ub4dor-x9";
            try
            {
                var src = Path.Combine(dir, "src");
                Directory.CreateDirectory(src);
                var a = Path.Combine(src, "a.txt");
                var b = Path.Combine(src, "b.txt");
                File.WriteAllText(a, "hello");
                File.WriteAllText(b, "world");

                var hidden = Path.Combine(dir, "photos.7z");
                var aes = Path.Combine(dir, "aes.zip");
                var crypto = Path.Combine(dir, "crypto.zip");
                var accented = Path.Combine(dir, "accented.7z");
                Check("7za makes a 7z with hidden names", Make7za(hidden, new[] { "-t7z", "-p" + secret, "-mhe=on" }, a, b));
                Check("7za makes an AES zip", Make7za(aes, new[] { "-tzip", "-p" + secret, "-mem=AES256" }, a, b));
                Check("7za makes a ZipCrypto zip", Make7za(crypto, new[] { "-tzip", "-p" + secret, "-mem=ZipCrypto" }, a, b));
                Check("7za makes a 7z with an accented password", Make7za(accented, new[] { "-t7z", "-pPässwörd", "-mhe=on" }, a));

                ArchivePasswords.ForgetAll();
                var log = Path.Combine(dir, "transfer.log");
                Environment.SetEnvironmentVariable("EXPLORERNATIVE_TRANSFERLOG", log);

                // A wrong password is said plainly and asked again.
                var asked = FakeAsker("wrong one", secret);
                var into = Path.Combine(dir, "out7z");
                var result = await ArchiveEngine.ExtractAsync(hidden, into, PasteConflictPolicy.AutoRename, null, 2, null, CancellationToken.None);
                Equal("a 7z with hidden names comes out with the password", "hello",
                    File.Exists(Path.Combine(into, "a.txt")) ? File.ReadAllText(Path.Combine(into, "a.txt")) : "missing");
                Equal("and nothing failed", "0", result.Failed.ToString());
                Equal("it asked twice", "photos.7z needs a password | That password is not right for photos.7z. Type it again",
                    string.Join(" | ", asked));
                Check("the wrong try left nothing behind", !File.Exists(Path.Combine(into, "a (2).txt")) &&
                    !Directory.EnumerateDirectories(into, ".extracting-*").Any());

                // Remembered for the session: browsing the same archive asks nothing.
                var listing = ArchiveBrowser.Open(hidden, null, CancellationToken.None);
                Check("browsing it afterwards shows its files", listing.Find("b.txt") != null);
                Equal("without asking again", "2", asked.Count.ToString());
                Environment.SetEnvironmentVariable("EXPLORERNATIVE_TRANSFERLOG", logBefore);
                var logged = File.Exists(log) ? File.ReadAllText(log) : "";
                Check("the transfer log was written", logged.Contains("7-Zip extract", StringComparison.Ordinal));
                Check("and the password is not in it", !logged.Contains(secret, StringComparison.Ordinal));

                // AES zip: browsing and opening one file inside.
                asked = FakeAsker(secret);
                var zipListing = ArchiveBrowser.Open(aes, null, CancellationToken.None);
                var opened = ArchiveBrowser.ExtractOne(zipListing, "a.txt", null, CancellationToken.None);
                Equal("a file inside an AES zip opens", "hello", File.ReadAllText(opened));
                Equal("after one question", "aes.zip needs a password", string.Join(" | ", asked));

                // ZipCrypto zip, extracted whole.
                asked = FakeAsker(secret);
                var intoZip = Path.Combine(dir, "outzip");
                result = await ArchiveEngine.ExtractAsync(crypto, intoZip, PasteConflictPolicy.AutoRename, null, 2, null, CancellationToken.None);
                Equal("a ZipCrypto zip comes out", "world",
                    File.Exists(Path.Combine(intoZip, "b.txt")) ? File.ReadAllText(Path.Combine(intoZip, "b.txt")) : "missing");

                // A password outside ASCII.
                FakeAsker("Pässwörd");
                var intoAccent = Path.Combine(dir, "outaccent");
                await ArchiveEngine.ExtractAsync(accented, intoAccent, PasteConflictPolicy.AutoRename, null, 2, null, CancellationToken.None);
                Check("a password with accents works", File.Exists(Path.Combine(intoAccent, "a.txt")));

                // Cancel stops quietly and leaves no folder.
                ArchivePasswords.ForgetAll();
                FakeAsker((string?)null);
                var intoCancel = Path.Combine(dir, "outcancel");
                bool declined = false;
                try { await ArchiveEngine.ExtractAsync(hidden, intoCancel, PasteConflictPolicy.AutoRename, null, 2, null, CancellationToken.None); }
                catch (ArchivePasswords.Declined) { declined = true; }
                Check("cancelling the question stops the extraction", declined);
                Check("and leaves no folder behind", !Directory.Exists(intoCancel));
            }
            finally
            {
                Environment.SetEnvironmentVariable("EXPLORERNATIVE_TRANSFERLOG", logBefore);
                ArchivePasswords.Asker = before;
                ArchivePasswords.ForgetAll();
                Clean(dir);
            }
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
