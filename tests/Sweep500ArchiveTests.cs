using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of archives.
    /// </summary>
    internal static class Sweep500ArchiveTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, archives:");
            await MarkOfTheWebTests();
            await SevenZipLineCapTests();
            await ChosenLinkTests();
            await PasswordSevenZipTests();
            await SevenZipLinkTests();
            await RepeatedNamesTests();
            await SevenZipBadNameTests();
        }

        /// <summary>Runs the bundled 7za with these arguments in a folder, for making fixtures.</summary>
        private static int SevenZip(string folder, params string[] arguments)
        {
            var start = new System.Diagnostics.ProcessStartInfo(BsdTar.SevenZipExecutable!)
            {
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in arguments) start.ArgumentList.Add(a);
            using var process = System.Diagnostics.Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }

        private static string NewDir(string what)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"EN-s500-{what}-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string Said(ArchiveResult result) => string.Join(" | ", result.Errors);

        // ---------- 1 and 2: the Mark of the Web ----------

        /// <summary>
        /// A downloaded archive carries a Zone.Identifier stream. Every file taken
        /// out of it must carry the same one, or Office opens a macro document
        /// outside Protected View and an .exe runs with no SmartScreen check
        /// (the class of 7-Zip's CVE-2025-0411). Extract, Extract here and
        /// browsing (Enter on a member) all wrote files with no mark.
        /// </summary>
        private static async Task MarkOfTheWebTests()
        {
            var dir = NewDir("motw");
            try
            {
                const string zone = "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/invoice.zip\r\n";
                var probe = Path.Combine(dir, "probe.txt");
                File.WriteAllText(probe, "x");
                try { File.WriteAllText(probe + ":Zone.Identifier", zone); }
                catch
                {
                    Console.WriteLine("  (Mark of the Web skipped: the temp folder holds no streams)");
                    return;
                }

                var src = Path.Combine(dir, "src");
                Directory.CreateDirectory(Path.Combine(src, "sub"));
                File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
                File.WriteAllText(Path.Combine(src, "sub", "b.docm"), "macro");

                static string? Mark(string path)
                {
                    try { return File.ReadAllText(path + ":Zone.Identifier"); }
                    catch { return null; }
                }

                static string Unmarked(string root)
                {
                    var files = Directory.Exists(root)
                        ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList()
                        : new List<string>();
                    if (files.Count == 0) return "no files came out";
                    return string.Join(", ", files.Where(f => Mark(f)?.Contains("ZoneId=3") != true)
                        .Select(f => Path.GetRelativePath(root, f)));
                }

                // Zip, through ZipEngine.
                var zip = Path.Combine(dir, "invoice.zip");
                ZipFile.CreateFromDirectory(src, zip);
                File.WriteAllText(zip + ":Zone.Identifier", zone);
                var zipOut = Path.Combine(dir, "zip-out");
                var fromZip = await ArchiveEngine.ExtractAsync(zip, zipOut, PasteConflictPolicy.AutoRename, null, 2,
                    null, CancellationToken.None);
                Check("motw: extracting a downloaded zip marks every file as downloaded",
                    fromZip.Failed == 0 && Unmarked(zipOut).Length == 0, Unmarked(zipOut) + " " + Said(fromZip));
                Check("motw: the mark is the archive's own, address and all",
                    Mark(Path.Combine(zipOut, "a.txt")) == zone, Mark(Path.Combine(zipOut, "a.txt")));

                // tar.gz, through TarEngine.
                var tgz = Path.Combine(dir, "invoice.tar.gz");
                using (var file = File.Create(tgz))
                using (var gz = new GZipStream(file, CompressionLevel.Fastest))
                    TarFile.CreateFromDirectory(src, gz, includeBaseDirectory: false);
                File.WriteAllText(tgz + ":Zone.Identifier", zone);
                var tgzOut = Path.Combine(dir, "tgz-out");
                var fromTgz = await ArchiveEngine.ExtractAsync(tgz, tgzOut, PasteConflictPolicy.AutoRename, null, 2,
                    null, CancellationToken.None);
                Check("motw: extracting a downloaded tar.gz marks every file as downloaded",
                    fromTgz.Failed == 0 && Unmarked(tgzOut).Length == 0, Unmarked(tgzOut) + " " + Said(fromTgz));

                // Enter on the zip, then Enter on a member: the copy that is opened.
                var listing = ArchiveBrowser.Open(zip, null, CancellationToken.None);
                var opened = ArchiveBrowser.ExtractOne(listing, "sub/b.docm", null, CancellationToken.None);
                Check("motw: a file opened from inside a downloaded zip is marked as downloaded",
                    Mark(opened)?.Contains("ZoneId=3") == true, opened);
                var tarListing = ArchiveBrowser.Open(tgz, null, CancellationToken.None);
                var tarOpened = ArchiveBrowser.ExtractOne(tarListing, "a.txt", null, CancellationToken.None);
                Check("motw: a file opened from inside a downloaded tar.gz is marked as downloaded",
                    Mark(tarOpened)?.Contains("ZoneId=3") == true, tarOpened);

                // 7z, through 7-Zip, staging and Rehome; and its browse tree.
                if (BsdTar.SevenZipExecutable != null)
                {
                    var seven = Path.Combine(dir, "invoice.7z");
                    var made = await ArchiveEngine.CompressAsync(new[] { Path.Combine(src, "a.txt"), Path.Combine(src, "sub") },
                        seven, ArchiveFormats.SevenZip, ArchiveLevel.Fastest, 2, null, CancellationToken.None);
                    File.WriteAllText(seven + ":Zone.Identifier", zone);
                    var sevenOut = Path.Combine(dir, "7z-out");
                    var from7z = await ArchiveEngine.ExtractAsync(seven, sevenOut, PasteConflictPolicy.AutoRename, null, 2,
                        null, CancellationToken.None);
                    Check("motw: extracting a downloaded 7z marks every file as downloaded",
                        made.Failed == 0 && from7z.Failed == 0 && Unmarked(sevenOut).Length == 0,
                        Unmarked(sevenOut) + " " + Said(made) + " " + Said(from7z));

                    var sevenListing = ArchiveBrowser.Open(seven, null, CancellationToken.None);
                    var sevenOpened = ArchiveBrowser.ExtractOne(sevenListing, "sub/b.docm", null, CancellationToken.None);
                    Check("motw: a file opened from inside a downloaded 7z is marked as downloaded",
                        Mark(sevenOpened)?.Contains("ZoneId=3") == true, sevenOpened);
                }

                // Controls: no mark, and a mark that is not the internet's, give files with none.
                var plain = Path.Combine(dir, "plain.zip");
                ZipFile.CreateFromDirectory(src, plain);
                var plainOut = Path.Combine(dir, "plain-out");
                await ArchiveEngine.ExtractAsync(plain, plainOut, PasteConflictPolicy.AutoRename, null, 2, null,
                    CancellationToken.None);
                var local = Path.Combine(dir, "local.zip");
                ZipFile.CreateFromDirectory(src, local);
                File.WriteAllText(local + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=2\r\n");
                var localOut = Path.Combine(dir, "local-out");
                await ArchiveEngine.ExtractAsync(local, localOut, PasteConflictPolicy.AutoRename, null, 2, null,
                    CancellationToken.None);
                Check("motw: an archive that was not downloaded gives files with no mark",
                    Mark(Path.Combine(plainOut, "a.txt")) == null && Mark(Path.Combine(localOut, "a.txt")) == null &&
                    File.Exists(Path.Combine(plainOut, "a.txt")), null);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        // ---------- 3: 7-Zip's line cap ----------

        /// <summary>
        /// With -bb1 7-Zip prints a "- name" line per file, and only the first
        /// 20,000 lines were kept. The ERROR line naming the damaged file of a
        /// big cut tar.xz came after them and was lost, so under Replace the
        /// half-written file went over the good one. The cap is lowered to 20
        /// here, so 50 files stand for 20,000.
        /// </summary>
        private static async Task SevenZipLineCapTests()
        {
            var full = new List<string>();
            for (int i = 0; i < 30; i++) BsdTar.KeepLine(full, "- file" + i);
            int cap = BsdTar.SevenZipLineCap;
            BsdTar.SevenZipLineCap = 20;
            try
            {
                var lines = new List<string>();
                for (int i = 0; i < 30; i++) BsdTar.KeepLine(lines, "- file" + i);
                for (int i = 0; i < 30; i++) BsdTar.KeepLine(lines, "Size: " + i);
                BsdTar.KeepLine(lines, "ERROR: Data Error : zz_big.bin");
                Check("7-Zip cap: an ERROR line is kept after the cap is full",
                    lines.Contains("ERROR: Data Error : zz_big.bin"), string.Join(", ", lines.Take(5)));
                Check("7-Zip cap: entry lines do not use it up", !lines.Any(l => l.StartsWith("- ")) && full.Count == 0,
                    lines.Count.ToString());

                if (BsdTar.SevenZipExecutable == null) return;
                var dir = NewDir("cap");
                try
                {
                    var src = Path.Combine(dir, "src");
                    Directory.CreateDirectory(src);
                    for (int i = 0; i < 50; i++) File.WriteAllText(Path.Combine(src, $"f{i:00}.txt"), "ten bytes!");
                    var random = new Random(11);
                    var cut = new byte[600_000];
                    random.NextBytes(cut);
                    File.WriteAllBytes(Path.Combine(src, "zz_big.bin"), cut);

                    var archive = Path.Combine(dir, "big.tar.xz");
                    var made = await ArchiveEngine.CompressAsync(new[] { src }, archive, ArchiveFormats.TarXz,
                        ArchiveLevel.Fastest, 2, null, CancellationToken.None);
                    var bytes = File.ReadAllBytes(archive);
                    File.WriteAllBytes(archive, bytes[..(int)(bytes.Length * 0.8)]);

                    var into = Path.Combine(dir, "into");
                    Directory.CreateDirectory(Path.Combine(into, "src"));
                    var good = new byte[600_000];
                    Array.Fill(good, (byte)7);
                    File.WriteAllBytes(Path.Combine(into, "src", "zz_big.bin"), good);

                    var result = await ArchiveEngine.ExtractAsync(archive, into, PasteConflictPolicy.Overwrite, null, 2,
                        null, CancellationToken.None);
                    var now = File.ReadAllBytes(Path.Combine(into, "src", "zz_big.bin"));
                    Check("7-Zip cap: a cut tar.xz with many files leaves the good file under Replace",
                        made.Failed == 0 && now.AsSpan().SequenceEqual(good), $"{now.Length} bytes; " + Said(result));
                    Check("7-Zip cap: the damaged file is named",
                        result.Errors.Any(e => e.Contains("zz_big.bin: the archive is damaged at this point")), Said(result));
                }
                finally
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
            finally
            {
                BsdTar.SevenZipLineCap = cap;
            }
        }

        // ---------- 4: a chosen junction made into a 7z ----------

        /// <summary>
        /// 7-Zip runs with -snl, so a chosen junction or folder symlink went in
        /// as one empty link, and "Created Projects.7z" was said for an archive
        /// with no data. Now it holds the files or is refused in words; a link
        /// inside a chosen folder is still stored as a link.
        /// </summary>
        private static async Task ChosenLinkTests()
        {
            if (BsdTar.SevenZipExecutable == null) return;
            var dir = NewDir("link");
            var link = Path.Combine(dir, "Projects");
            var plain = Path.Combine(dir, "plain");
            var nested = Path.Combine(plain, "nested");
            try
            {
                var target = Path.Combine(dir, "target");
                Directory.CreateDirectory(Path.Combine(target, "inner"));
                File.WriteAllText(Path.Combine(target, "inner", "f.txt"), "data");
                Directory.CreateDirectory(plain);
                File.WriteAllText(Path.Combine(plain, "a.txt"), "a");
                try
                {
                    ReparseLinks.CreateJunction(link, target);
                    ReparseLinks.CreateJunction(nested, target);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  (chosen link skipped: no junction: " + ex.Message + ")");
                    return;
                }

                foreach (var format in new[] { ArchiveFormats.SevenZip, ArchiveFormats.TarXz, ArchiveFormats.TarBz2 })
                {
                    var archive = Path.Combine(dir, "Projects." + format.Id);
                    string outcome;
                    bool holds = false, refused = false;
                    try
                    {
                        var made = await ArchiveEngine.CompressAsync(new[] { link }, archive, format,
                            ArchiveLevel.Fastest, 1, null, CancellationToken.None);
                        var back = Path.Combine(dir, "back-" + format.Id);
                        var got = await ArchiveEngine.ExtractAsync(archive, back, PasteConflictPolicy.AutoRename, null, 1,
                            null, CancellationToken.None);
                        holds = File.Exists(Path.Combine(back, "Projects", "inner", "f.txt"));
                        outcome = "made: " + Said(made) + " / back: " + Said(got);
                    }
                    catch (NotSupportedException ex)
                    {
                        outcome = ex.Message;
                        refused = ex.Message.Contains("link") && !File.Exists(archive);
                    }
                    Check($"chosen link: a {format.Id} of a junction holds its files or is refused in words",
                        holds || refused, outcome);
                }
                Check("chosen link: what the junction leads to is untouched",
                    File.Exists(Path.Combine(target, "inner", "f.txt")), null);

                var made7z = await ArchiveEngine.CompressAsync(new[] { plain }, Path.Combine(dir, "plain.7z"),
                    ArchiveFormats.SevenZip, ArchiveLevel.Fastest, 1, null, CancellationToken.None);
                Check("chosen link: a chosen folder holding a junction still makes a 7z",
                    made7z.Failed == 0 && File.Exists(Path.Combine(dir, "plain.7z")), Said(made7z));
            }
            finally
            {
                try { Directory.Delete(link); } catch { }
                try { Directory.Delete(nested); } catch { }
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        // ---------- 5: a 7z with its names encrypted ----------

        /// <summary>
        /// A protected 7z made with "Encrypt file names" (-mhe=on) was said to
        /// be damaged, because only "ERROR: " lines were searched for the
        /// password and "Headers Error" then counted as damage. A plain
        /// protected 7z got the password sentence and a false "damaged" line.
        /// </summary>
        private static async Task PasswordSevenZipTests()
        {
            if (BsdTar.SevenZipExecutable == null) return;
            var dir = NewDir("pw");
            try
            {
                File.WriteAllText(Path.Combine(dir, "secret.txt"), string.Concat(Enumerable.Repeat("secret ", 2000)));
                SevenZip(dir, "a", "-t7z", "-psecret", "-mhe=on", "headerpw.7z", "secret.txt");
                SevenZip(dir, "a", "-t7z", "-psecret", "plainpw.7z", "secret.txt");

                foreach (var name in new[] { "headerpw.7z", "plainpw.7z" })
                {
                    var archive = Path.Combine(dir, name);
                    var result = await ArchiveEngine.ExtractAsync(archive, Path.Combine(dir, "out-" + name), PasteConflictPolicy.AutoRename,
                        null, 1, null, CancellationToken.None);
                    Check($"password 7z: {name} is said to be protected with a password, not damaged",
                        File.Exists(archive) && result.Items == 0 &&
                        result.Errors.Any(e => e.Contains("protected with a password")) &&
                        !result.Errors.Any(e => e.Contains("damaged", StringComparison.OrdinalIgnoreCase)), Said(result));
                }

                string opened;
                try { ArchiveBrowser.Open(Path.Combine(dir, "headerpw.7z"), null, CancellationToken.None); opened = "opened"; }
                catch (Exception ex) { opened = ex.Message; }
                Check("password 7z: Enter on one with encrypted names says it needs a password",
                    opened.Contains("password") && !opened.Contains("damaged"), opened);

                // Control: a cut plain 7z is still damaged.
                File.WriteAllBytes(Path.Combine(dir, "big.bin"), Enumerable.Range(0, 400_000).Select(i => (byte)(i * 7919 >> 3)).ToArray());
                SevenZip(dir, "a", "-t7z", "-mx1", "cut.7z", "big.bin", "secret.txt");
                var bytes = File.ReadAllBytes(Path.Combine(dir, "cut.7z"));
                bytes[bytes.Length / 3] ^= 0xFF;
                bytes[bytes.Length / 3 + 1] ^= 0xFF;
                File.WriteAllBytes(Path.Combine(dir, "cut.7z"), bytes);
                var cut = await ArchiveEngine.ExtractAsync(Path.Combine(dir, "cut.7z"), Path.Combine(dir, "out-cut"),
                    PasteConflictPolicy.AutoRename, null, 1, null, CancellationToken.None);
                Check("password 7z: a damaged plain 7z is still said to be damaged",
                    cut.Errors.Any(e => e.Contains("damaged")) && !cut.Errors.Any(e => e.Contains("password")), Said(cut));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        /// <summary>A tar of these entries: a name and its text, or a name, null and where it links to.</summary>
        private static void WriteTar(string path, params (string Name, string? Text, string? LinkTo)[] entries)
        {
            using var file = File.Create(path);
            using var writer = new TarWriter(file, TarEntryFormat.Pax);
            foreach (var (name, text, linkTo) in entries)
            {
                if (linkTo != null)
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, name) { LinkName = linkTo });
                    continue;
                }
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text ?? "")),
                });
            }
        }

        // ---------- 6: links in a tar.xz through 7-Zip ----------

        /// <summary>
        /// 7-Zip cannot make a link without the privilege, and refuses any link
        /// with ".." in it as dangerous (node's bin/npm). Both leave an empty
        /// file in staging. Each was said as "the archive is damaged at this
        /// point" (the dangerous one under its target's name, with the empty
        /// file moved in), the staging path was quoted, and browsing called the
        /// archive damaged. The decompressing half's inner tar was named as an
        /// entry of its own.
        /// </summary>
        private static async Task SevenZipLinkTests()
        {
            if (BsdTar.SevenZipExecutable == null) return;
            var dir = NewDir("links7");
            try
            {
                WriteTar(Path.Combine(dir, "links.tar"),
                    ("lib/cli.js", "cli", null),
                    ("bin/npm", null, "../lib/cli.js"),
                    ("out", null, "../../outside"),
                    ("lib/same.js", null, "cli.js"),
                    ("last.txt", "last", null));
                SevenZip(dir, "a", "-txz", "links.tar.xz", "links.tar");
                var archive = Path.Combine(dir, "links.tar.xz");

                var into = Path.Combine(dir, "into");
                Directory.CreateDirectory(Path.Combine(into, "bin"));
                File.WriteAllText(Path.Combine(into, "bin", "npm"), "old");
                var result = await ArchiveEngine.ExtractAsync(archive, into, PasteConflictPolicy.Overwrite, null, 1,
                    null, CancellationToken.None);
                var said = Said(result);
                Check("7-Zip links: a link it refused does not land as an empty file, over a real one or new",
                    File.ReadAllText(Path.Combine(into, "bin", "npm")) == "old" && !File.Exists(Path.Combine(into, "out")), said);
                Check("7-Zip links: each link left out is said as a link",
                    said.Contains("bin/npm: a symbolic link was not extracted") &&
                    said.Contains("out: a symbolic link was not extracted") &&
                    said.Contains("lib/same.js: a symbolic link was not extracted"), said);
                Check("7-Zip links: a link is not called damage, and no staging folder or link target is quoted",
                    !said.Contains("damaged") && !said.Contains(".extracting-") && !said.Contains("cli.js:") &&
                    !said.Contains(@"..\lib") && !said.Contains("../lib"), said);
                Check("7-Zip links: the files around the links come out",
                    File.Exists(Path.Combine(into, "last.txt")) && File.Exists(Path.Combine(into, "lib", "cli.js")), said);

                var listing = ArchiveBrowser.Open(archive, null, CancellationToken.None);
                Check("7-Zip links: browsing a tarball with links does not call it damaged",
                    !listing.Damaged && listing.Partial == null, listing.Partial);

                // A cut tar.xz: the decompressing half names the inner "cut.tar".
                var random = new Random(5);
                var big = new byte[300_000];
                random.NextBytes(big);
                WriteTar(Path.Combine(dir, "cut.tar"), ("a.txt", "a", null), ("b.bin", Convert.ToBase64String(big), null));
                SevenZip(dir, "a", "-txz", "-mx1", "cut.tar.xz", "cut.tar");
                var bytes = File.ReadAllBytes(Path.Combine(dir, "cut.tar.xz"));
                File.WriteAllBytes(Path.Combine(dir, "cut.tar.xz"), bytes[..(bytes.Length * 7 / 10)]);
                var cut = await ArchiveEngine.ExtractAsync(Path.Combine(dir, "cut.tar.xz"), Path.Combine(dir, "cut-out"),
                    PasteConflictPolicy.AutoRename, null, 1, null, CancellationToken.None);
                Check("7-Zip links: a cut tar.xz does not name its inner tar as a damaged entry",
                    !Said(cut).Contains("cut.tar:") && Said(cut).Contains("damaged") &&
                    !File.Exists(Path.Combine(dir, "cut-out", "b.bin")), Said(cut));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        // ---------- 7: repeated names through 7-Zip ----------

        /// <summary>
        /// 7-Zip's -aos keeps the first of two entries with one name (or two
        /// that differ only in capitals) and skips the rest with exit 0, and
        /// nothing was said. The tar route says how many repeated a name.
        /// </summary>
        private static async Task RepeatedNamesTests()
        {
            if (BsdTar.SevenZipExecutable == null) return;
            var dir = NewDir("repeat");
            try
            {
                WriteTar(Path.Combine(dir, "dup.tar"),
                    ("A.txt", "upper", null), ("a.txt", "lower", null),
                    ("dup.txt", "first", null), ("dup.txt", "second", null),
                    ("other.txt", "other", null));
                SevenZip(dir, "a", "-txz", "dup.tar.xz", "dup.tar");
                SevenZip(dir, "a", "-tbzip2", "dup.tar.bz2", "dup.tar");

                foreach (var name in new[] { "dup.tar.xz", "dup.tar.bz2" })
                {
                    var into = Path.Combine(dir, "out-" + name);
                    var result = await ArchiveEngine.ExtractAsync(Path.Combine(dir, name), into,
                        PasteConflictPolicy.AutoRename, null, 1, null, CancellationToken.None);
                    Check($"repeated names: a {name} with repeated names says how many",
                        result.Errors.Any(e => e.Contains("2 items in the archive repeated a name")), Said(result));
                    Check($"repeated names: the first of each is kept from a {name}",
                        File.Exists(Path.Combine(into, "A.txt")) && File.ReadAllText(Path.Combine(into, "A.txt")) == "upper" &&
                        File.ReadAllText(Path.Combine(into, "dup.txt")) == "first" &&
                        File.Exists(Path.Combine(into, "other.txt")), Said(result));
                }

                // Control: no repeats, nothing said.
                WriteTar(Path.Combine(dir, "plain.tar"), ("one.txt", "1", null), ("two.txt", "2", null));
                SevenZip(dir, "a", "-txz", "plain.tar.xz", "plain.tar");
                var plain = await ArchiveEngine.ExtractAsync(Path.Combine(dir, "plain.tar.xz"), Path.Combine(dir, "out-plain"),
                    PasteConflictPolicy.AutoRename, null, 1, null, CancellationToken.None);
                Check("repeated names: an archive with none says nothing", plain.Errors.Count == 0 && plain.Items == 2,
                    Said(plain));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        // ---------- 8: names 7-Zip renames on its own ----------

        /// <summary>
        /// 7-Zip writes a name Windows cannot hold under one it makes up: CON as
        /// _CON, "trail." as trail_, "a:b" as a_b. Withheld looked for bsdtar's
        /// spellings, so those went in under 7-Zip's names without a word,
        /// against the rule that a bad name is refused by the name it had.
        /// </summary>
        private static async Task SevenZipBadNameTests()
        {
            Equal("7-Zip names: CON is spelled as 7-Zip writes it", "_CON", BsdTar.CleanLike7Zip("CON"));
            Equal("7-Zip names: aux.txt is spelled as 7-Zip writes it", "_aux.txt", BsdTar.CleanLike7Zip("aux.txt"));
            Equal("7-Zip names: a trailing dot is spelled as 7-Zip writes it", "trail_", BsdTar.CleanLike7Zip("trail."));
            Equal("7-Zip names: a trailing space is spelled as 7-Zip writes it", "name_", BsdTar.CleanLike7Zip("name "));
            Equal("7-Zip names: a colon is spelled as 7-Zip writes it", "résumé_v2.txt", BsdTar.CleanLike7Zip("résumé:v2.txt"));
            Equal("7-Zip names: a reserved name in a folder is spelled as 7-Zip writes it", "dir/_CON.txt",
                BsdTar.CleanLike7Zip("dir/CON.txt"));

            if (BsdTar.SevenZipExecutable == null) return;
            var dir = NewDir("bad7");
            try
            {
                WriteTar(Path.Combine(dir, "bad.tar"),
                    ("ok.txt", "ok", null), ("CON", "c", null), ("aux.txt", "a", null), ("nul.flac", "n", null),
                    ("trail.", "t", null), ("name ", "s", null), ("résumé:v2.txt", "r", null),
                    ("dir/CON.txt", "d", null), ("bad./x.txt", "x", null), ("last.txt", "last", null));
                SevenZip(dir, "a", "-txz", "bad.tar.xz", "bad.tar");
                SevenZip(dir, "a", "-tbzip2", "bad.tar.bz2", "bad.tar");

                foreach (var archive in new[] { "bad.tar.xz", "bad.tar.bz2" })
                {
                    var into = Path.Combine(dir, "out-" + archive);
                    var result = await ArchiveEngine.ExtractAsync(Path.Combine(dir, archive), into,
                        PasteConflictPolicy.AutoRename, null, 1, null, CancellationToken.None);
                    var said = Said(result);
                    Check($"7-Zip names: the good files of a {archive} come out",
                        File.Exists(Path.Combine(into, "ok.txt")) && File.Exists(Path.Combine(into, "last.txt")) &&
                        !Directory.EnumerateDirectories(into, ".extracting-*").Any(), said);
                    var renamed = new[] { "_CON", "_aux.txt", "_nul.flac", "trail_", "name_", "résumé_v2.txt", "dir/_CON.txt", "bad_/x.txt" }
                        .Where(n => File.Exists(Path.Combine(into, n))).ToList();
                    Check($"7-Zip names: nothing from a {archive} goes in under a name 7-Zip made up",
                        renamed.Count == 0, string.Join(", ", renamed));
                    var unsaid = new[] { "CON", "aux.txt", "nul.flac", "trail.", "name ", "résumé:v2.txt", "dir/CON.txt", "bad./x.txt" }
                        .Where(n => !said.Contains($"\"{n}\"")).ToList();
                    Check($"7-Zip names: each bad name in a {archive} is refused by the name it had",
                        unsaid.Count == 0, "unsaid: " + string.Join(", ", unsaid) + " / " + said);
                }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
