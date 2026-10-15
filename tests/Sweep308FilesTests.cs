using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Regression tests for the 3.0.8 sweep of the copy and archive files: one
    /// per finding, each on a real archive built for the case.
    /// </summary>
    internal static class Sweep308FilesTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        public static async Task RunAll(Action<string, bool, string?> check)
        {
            _check = check;
            Console.WriteLine("Sweep 3.0.8 (files and archives):");

            BrowseDotSlashTarTests();
            BrowseDotSlashZipTests();
            DotSegmentsHelperTests();
            await DotSlashThroughBsdTarTests();
        }

        private static string NewDir(string tag)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"en-sw308-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Remove(string dir)
        {
            try { ArchiveBrowser.DeleteTree(dir); } catch { }
        }

        private static void AddFile(TarWriter writer, string name, string text)
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)),
            });
        }

        /// <summary>
        /// A tarball made with `tar -czf x.tgz .` names every entry "./…". Browsed,
        /// every one of them was refused as pointing outside the archive, so it
        /// opened as an empty folder with "N items are not shown".
        /// </summary>
        private static void BrowseDotSlashTarTests()
        {
            var dir = NewDir("browse-tgz");
            try
            {
                var archive = Path.Combine(dir, "dotslash.tar.gz");
                using (var file = File.Create(archive))
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./"));
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./docs/"));
                    AddFile(writer, "./docs/a.txt", "alpha");
                    AddFile(writer, "./b.txt", "bravo");
                    AddFile(writer, "../evil.txt", "evil");
                    AddFile(writer, "//server/share/far.txt", "far");
                }

                var listing = ArchiveBrowser.Open(archive, null, CancellationToken.None);
                Check("a tar of \"./\" names browses its files",
                    listing.Find("docs/a.txt") != null && listing.Find("b.txt") != null,
                    $"count={listing.Count} unsafe={listing.Unsafe}");
                Check("and its folder is a folder", listing.IsFolder("docs"));
                Check("only the names that really point outside are counted as unsafe",
                    listing.Unsafe == 2, $"unsafe={listing.Unsafe}");
                Check("the top of it lists the folder and the file, and nothing called \".\"",
                    listing.Children("").Select(c => c.Name).OrderBy(n => n).SequenceEqual(new[] { "b.txt", "docs" }),
                    string.Join(", ", listing.Children("").Select(c => c.Name)));

                var opened = ArchiveBrowser.ExtractOne(listing, "docs/a.txt", null, CancellationToken.None);
                Check("a \"./\" member opens with its contents",
                    File.Exists(opened) && File.ReadAllText(opened) == "alpha", opened);
                try { ArchiveBrowser.DeleteTree(Path.GetDirectoryName(opened)!); } catch { }
            }
            catch (Exception ex) { Check("browsing a \"./\" tar ran", false, ex.ToString()); }
            finally { Remove(dir); }
        }

        /// <summary>The same for a zip whose names start "./".</summary>
        private static void BrowseDotSlashZipTests()
        {
            var dir = NewDir("browse-zip");
            try
            {
                var archive = Path.Combine(dir, "dotslash.zip");
                using (var file = File.Create(archive))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    using (var s = zip.CreateEntry("./notes/one.txt").Open()) s.Write(Encoding.UTF8.GetBytes("one"));
                    using (var s = zip.CreateEntry("./two.txt").Open()) s.Write(Encoding.UTF8.GetBytes("two"));
                }

                var listing = ArchiveBrowser.Open(archive, null, CancellationToken.None);
                Check("a zip of \"./\" names browses its files",
                    listing.Find("notes/one.txt") != null && listing.Find("two.txt") != null && listing.Unsafe == 0,
                    $"count={listing.Count} unsafe={listing.Unsafe}");

                var opened = ArchiveBrowser.ExtractOne(listing, "two.txt", null, CancellationToken.None);
                Check("and a member of it opens", File.Exists(opened) && File.ReadAllText(opened) == "two", opened);
                try { ArchiveBrowser.DeleteTree(Path.GetDirectoryName(opened)!); } catch { }
            }
            catch (Exception ex) { Check("browsing a \"./\" zip ran", false, ex.ToString()); }
            finally { Remove(dir); }
        }

        private static void DotSegmentsHelperTests()
        {
            Check("\"./a.txt\" is checked as a.txt", BsdTar.WithoutDotSegments("./a.txt") == "a.txt");
            Check("a \".\" in the middle goes too", BsdTar.WithoutDotSegments("d/./e") == "d/e");
            Check("a network path keeps its two slashes",
                BsdTar.WithoutDotSegments("//host/share/f").StartsWith("//", StringComparison.Ordinal));
            Check("\"..\" is left for SafeTarget to refuse", BsdTar.WithoutDotSegments("./../x") == "../x");
            Check("a lone \".\" is nothing", BsdTar.WithoutDotSegments(".") == "");
        }

        /// <summary>
        /// A .tar.xz made with `tar -cJf x.tar.xz -C folder .`: bsdtar prints
        /// "x ./a.txt", and that printed name was refused as pointing outside,
        /// so every file of the archive was withheld from the extraction.
        /// </summary>
        private static async Task DotSlashThroughBsdTarTests()
        {
            if (!BsdTar.Available) return;

            var dir = NewDir("bsd-dot");
            try
            {
                var source = Path.Combine(dir, "src");
                Directory.CreateDirectory(Path.Combine(source, "sub"));
                File.WriteAllText(Path.Combine(source, "a.txt"), "alpha");
                File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "bravo");

                var archive = Path.Combine(dir, "dotslash.tar.xz");
                var start = new ProcessStartInfo(BsdTar.Executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                foreach (var a in new[] { "-cJf", archive, "-C", source, "." }) start.ArgumentList.Add(a);
                using (var p = Process.Start(start)!)
                {
                    _ = p.StandardOutput.ReadToEndAsync();
                    _ = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(60_000)) { try { p.Kill(); } catch { } }
                    if (p.ExitCode != 0)
                    {
                        Check("the system tar made a \"./\" .tar.xz", false, $"exit {p.ExitCode}");
                        return;
                    }
                }

                var output = Path.Combine(dir, "out");
                var result = await ArchiveEngine.ExtractAsync(archive, output, PasteConflictPolicy.AutoRename, null,
                    2, null, CancellationToken.None);

                var a1 = Path.Combine(output, "a.txt");
                var b1 = Path.Combine(output, "sub", "b.txt");
                Check("a \"./\" .tar.xz extracts its files",
                    File.Exists(a1) && File.ReadAllText(a1) == "alpha" && File.Exists(b1) && File.ReadAllText(b1) == "bravo",
                    string.Join(" | ", result.Errors));
                Check("and says nothing pointed outside the folder",
                    !result.Errors.Any(e => e.Contains("outside", StringComparison.OrdinalIgnoreCase)),
                    string.Join(" | ", result.Errors));
            }
            catch (Exception ex) { Check("extracting a \"./\" .tar.xz ran", false, ex.ToString()); }
            finally { Remove(dir); }
        }
    }
}
