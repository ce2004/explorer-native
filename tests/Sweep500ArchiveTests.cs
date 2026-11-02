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
    }
}
