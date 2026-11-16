using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System;

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

    // ---- merged from Sweep309FilesTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// Regression tests for the 3.0.9 sweep of the copy and archive files: one
    /// per finding, each against a real archive or folder built for the case.
    /// Nothing here makes a multi-gigabyte file: the zip64 and gzip-length
    /// cases test the header logic directly.
    /// </summary>
    internal static class Sweep309FilesTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        public static async Task RunAll(Action<string, bool, string?> check)
        {
            _check = check;
            Console.WriteLine("Sweep 3.0.9 (files and archives):");

            ZipLocalAndCentralAgreeTests();
            ZipNameTooLongTests();
            GzipTrailerWrapsTests();
            await ContiguousTarEntryTests();
            await ZipFileOverFolderTests();
            SplitLineTests();
            await ColonNameThroughBsdTarTests();
            await ReplaceFolderSwapsNestedFilesTests();
            RecycleRefusesDottedTests();
        }

        private static string NewDir(string tag)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"en-sw309-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Remove(string dir)
        {
            try { ArchiveBrowser.DeleteTree(dir); } catch { }
        }

        private static (int Exit, string Errors) RunTar(params string[] args)
        {
            var start = new ProcessStartInfo(BsdTar.Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var p = Process.Start(start)!;
            _ = p.StandardOutput.ReadToEndAsync();
            var errors = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000)) { try { p.Kill(); } catch { } return (-1, "timed out"); }
            return (p.ExitCode, errors.Result);
        }

        /// <summary>
        /// A file planned at 3.75GB or more that deflated small: its local
        /// header was zip64 and its central record plain 32-bit. The writer's
        /// own header and record functions, put together by hand into a small
        /// zip with a zip64 local header, must now agree, and the result must
        /// still read in .NET and in the system tar.
        /// </summary>
        private static void ZipLocalAndCentralAgreeTests()
        {
            var dir = NewDir("zip64");
            try
            {
                var content = Encoding.UTF8.GetBytes("small after all");
                var name = Encoding.UTF8.GetBytes("big.bin");
                uint crc = Crc32.Append(Crc32.Seed, content);
                var (time, date) = ZipEngine.DosStamp(new DateTime(2024, 5, 6, 7, 8, 10));

                var plain = ZipEngine.CentralRecord(name, 0, time, date, crc, content.Length, content.Length, 0, 0);
                Check("control: a record for small sizes is plain 32-bit",
                    BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(20)) == (uint)content.Length &&
                    BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(30)) == 0);

                var wide = ZipEngine.CentralRecord(name, 0, time, date, crc, content.Length, content.Length, 0, 0,
                    wideSizes: true);
                Check("a record whose local header was zip64 is zip64 too",
                    BinaryPrimitives.ReadUInt32LittleEndian(wide.AsSpan(20)) == uint.MaxValue &&
                    BinaryPrimitives.ReadUInt32LittleEndian(wide.AsSpan(24)) == uint.MaxValue &&
                    BinaryPrimitives.ReadUInt16LittleEndian(wide.AsSpan(30)) == 20 &&
                    BinaryPrimitives.ReadInt64LittleEndian(wide.AsSpan(46 + name.Length + 4)) == content.Length &&
                    BinaryPrimitives.ReadInt64LittleEndian(wide.AsSpan(46 + name.Length + 12)) == content.Length,
                    Convert.ToHexString(wide));

                var archive = Path.Combine(dir, "wide.zip");
                using (var file = File.Create(archive))
                {
                    ZipEngine.WriteLocalHeader(file, name, 0, time, date, crc, content.Length, content.Length, zip64: true);
                    file.Write(content);
                    ZipEngine.WriteDirectory(file, new List<byte[]> { wide }, 1);
                }

                using (var file = File.OpenRead(archive))
                using (var zip = ZipEngine.Open(file))
                {
                    var entry = zip.Entries.Single();
                    using var reader = new StreamReader(entry.Open());
                    Check("and the zip reads back in .NET", reader.ReadToEnd() == "small after all");
                }

                if (BsdTar.Available)
                {
                    var output = Path.Combine(dir, "out");
                    Directory.CreateDirectory(output);
                    var (exit, errors) = RunTar("-x", "-f", archive, "-C", output);
                    var got = Path.Combine(output, "big.bin");
                    Check("and the system tar takes it",
                        exit == 0 && File.Exists(got) && File.ReadAllText(got) == "small after all",
                        $"exit {exit}: {errors}");
                }
            }
            catch (Exception ex) { Check("the zip64 agreement test ran", false, ex.ToString()); }
            finally { Remove(dir); }
        }

        /// <summary>
        /// A name over 65,535 UTF-8 bytes had its length cut to 16 bits and the
        /// record after it read from the middle of the name. Now that entry is
        /// left out with a reason, and the rest of the archive is whole.
        /// </summary>
        private static void ZipNameTooLongTests()
        {
            var dir = NewDir("zipname");
            try
            {
                var source = Path.Combine(dir, "a.txt");
                File.WriteAllText(source, "alpha");
                var when = File.GetLastWriteTime(source);

                var longName = string.Join("/", Enumerable.Repeat(new string('日', 200), 120)) + "/x.txt";
                Check("the name really is over the limit", Encoding.UTF8.GetByteCount(longName) > ZipEngine.MaxNameBytes);

                var plan = new List<ArchiveItem>
                {
                    new(source, "a.txt", 5, false, when, FileAttributes.Normal),
                    new(source, longName, 5, false, when, FileAttributes.Normal),
                    new(source, "b.txt", 5, false, when, FileAttributes.Normal),
                };

                var archive = Path.Combine(dir, "names.zip");
                var errors = new List<string>();
                int written = ZipEngine.Create(plan, archive, ArchiveLevel.Balanced, 2,
                    new ArchiveEngine.Reporter(null), errors, CancellationToken.None);

                Check("the long name is left out and said",
                    written == 2 && errors.Count == 1 && errors[0].Contains("too long to go in a zip"),
                    $"written={written} {string.Join(" | ", errors)}");

                using var file = File.OpenRead(archive);
                using var zip = ZipEngine.Open(file);
                Check("and the archive holds the other two, whole",
                    zip.Entries.Select(e => e.FullName).OrderBy(n => n).SequenceEqual(new[] { "a.txt", "b.txt" }) &&
                    zip.Entries.All(e => new StreamReader(e.Open()).ReadToEnd() == "alpha"),
                    string.Join(", ", zip.Entries.Select(e => e.FullName.Length)));
            }
            catch (Exception ex) { Check("the long-name zip test ran", false, ex.ToString()); }
            finally { Remove(dir); }
        }

        /// <summary>
        /// A gzip trailer holds the length modulo 2^32, so a last member of 4GB
        /// or more was read back from the wrong place and a good .gz was called
        /// damaged. Tested with the modulus at 2^16 and a 70,000-byte member.
        /// </summary>
        private static void GzipTrailerWrapsTests()
        {
            var dir = NewDir("gzwrap");
            try
            {
                var random = new Random(309);
                var prefix = new byte[1000];
                var last = new byte[70_000];
                random.NextBytes(prefix);
                random.NextBytes(last);

                uint crc = Crc32.Append(Crc32.Seed, last);
                var trailer = new byte[28];
                BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(20), crc);
                BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(24), (uint)(last.Length % 65536));
                var archive = Path.Combine(dir, "joined.gz");
                File.WriteAllBytes(archive, trailer);

                using var output = new MemoryStream();
                output.Write(prefix);
                output.Write(last);
                uint whole = Crc32.Append(Crc32.Append(Crc32.Seed, prefix), last);

                Check("control: read as the whole length, the wrapped trailer fails",
                    !TarEngine.TrailerAgrees(archive, output, whole, output.Length, CancellationToken.None, wrap: 1L << 32));
                Check("a last member longer than the length field holds is found by its wrap",
                    TarEngine.TrailerAgrees(archive, output, whole, output.Length, CancellationToken.None, wrap: 65536));
                Check("and the output is left where it was", output.Position == output.Length);

                BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(20), crc ^ 1);
                File.WriteAllBytes(archive, trailer);
                Check("a wrong checksum still fails",
                    !TarEngine.TrailerAgrees(archive, output, whole, output.Length, CancellationToken.None, wrap: 65536));
            }
            catch (Exception ex) { Check("the gzip wrap test ran", false, ex.ToString()); }
            finally { Remove(dir); }
        }

        /// <summary>A tar whose file is type '7', "contiguous file", which POSIX reads as a regular file.</summary>
        private static async Task ContiguousTarEntryTests()
        {
            var dir = NewDir("contig");
            try
            {
                using var built = new MemoryStream();
                using (var writer = new TarWriter(built, TarEntryFormat.Ustar, leaveOpen: true))
                {
                    writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "contig.txt")
                    {
                        DataStream = new MemoryStream(Encoding.UTF8.GetBytes("contiguous")),
                    });
                }

                var bytes = built.ToArray();
                bytes[156] = (byte)'7';
                for (int i = 148; i < 156; i++) bytes[i] = (byte)' ';
                int sum = 0;
                for (int i = 0; i < 512; i++) sum += bytes[i];
                var octal = Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0'));
                octal.CopyTo(bytes, 148);
                bytes[154] = 0;
                bytes[155] = (byte)' ';

                var archive = Path.Combine(dir, "contig.tar");
                File.WriteAllBytes(archive, bytes);

                var output = Path.Combine(dir, "out");
                var result = await ArchiveEngine.ExtractAsync(archive, output, PasteConflictPolicy.AutoRename, null,
                    2, null, CancellationToken.None);
                var got = Path.Combine(output, "contig.txt");
                Check("a contiguous-file entry extracts as a file",
                    File.Exists(got) && File.ReadAllText(got) == "contiguous" && result.Errors.Count == 0,
                    string.Join(" | ", result.Errors));

                var listing = ArchiveBrowser.Open(archive, null, CancellationToken.None);
                var opened = ArchiveBrowser.ExtractOne(listing, "contig.txt", null, CancellationToken.None);
                Check("and opens from a browsed tar", File.ReadAllText(opened) == "contiguous", opened);
                try { ArchiveBrowser.DeleteTree(Path.GetDirectoryName(opened)!); } catch { }
            }
            catch (Exception ex) { Check("the contiguous tar test ran", false, ex.ToString()); }
            finally { Remove(dir); }
        }

        /// <summary>
        /// A zip file entry extracted under Replace where a read-only folder of
        /// the same name is: the folder's read-only flag was cleared before the
        /// move failed. The folder is left exactly as it was, and it is said.
        /// </summary>
        private static async Task ZipFileOverFolderTests()
        {
            var dir = NewDir("overfolder");
            try
            {
                var archive = Path.Combine(dir, "one.zip");
                using (var file = File.Create(archive))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                using (var s = zip.CreateEntry("thing").Open())
                    s.Write(Encoding.UTF8.GetBytes("file"));

                var output = Path.Combine(dir, "out");
                var folder = Path.Combine(output, "thing");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "inside.txt"), "kept");
                var before = File.GetAttributes(folder) | FileAttributes.ReadOnly;
                File.SetAttributes(folder, before);

                var result = await ArchiveEngine.ExtractAsync(archive, output, PasteConflictPolicy.Overwrite, null,
                    2, null, CancellationToken.None);

                var after = File.GetAttributes(folder);
                Check("a folder in the way keeps its read-only flag",
                    (after & FileAttributes.ReadOnly) != 0 && (after & FileAttributes.Directory) != 0,
                    after.ToString());
                Check("and what was inside it",
                    File.ReadAllText(Path.Combine(folder, "inside.txt")) == "kept");
                Check("and the file is said not to have replaced it",
                    result.Errors.Any(e => e.Contains(ArchiveEngine.FolderInTheWay)) && result.Collisions.OverwrittenCount == 0,
                    string.Join(" | ", result.Errors));
                Check("and nothing temporary is left beside it",
                    !Directory.EnumerateFileSystemEntries(output).Any(p => Path.GetFileName(p).StartsWith(".extracting-")));

                try { File.SetAttributes(folder, after & ~FileAttributes.ReadOnly); } catch { }
            }
            catch (Exception ex) { Check("the file-over-folder test ran", false, ex.ToString()); }
            finally { Remove(dir); }
        }

        /// <summary>Where a bsdtar "x" line's name ends, when the name itself holds ": ".</summary>
        private static void SplitLineTests()
        {
            var staged = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Notes_ draft.txt", "f.bin", "a_ b.txt", "a", "dir", "dir/g.bin",
            };

            Check("a member called \"Notes: draft.txt\" is a name, not a failure",
                BsdTar.SplitLine("Notes: draft.txt", staged.Contains) == ("Notes: draft.txt", null));
            Check("a half-written file and its complaint split where the file is",
                BsdTar.SplitLine("f.bin: Truncated input file", staged.Contains) == ("f.bin", "Truncated input file"));
            Check("the longest staged name wins over a shorter one",
                BsdTar.SplitLine("a: b.txt: Truncated input file", staged.Contains) == ("a: b.txt", "Truncated input file"));
            Check("a link that was never made splits at the first \": \"",
                BsdTar.SplitLine("link: Can't create 'x': Invalid argument", staged.Contains) ==
                ("link", "Can't create 'x': Invalid argument"));
            Check("a \"./\" name in a folder is matched as staged",
                BsdTar.SplitLine("./dir/g.bin: Damaged tar archive", staged.Contains) == ("./dir/g.bin", "Damaged tar archive"));
            Check("a plain name is a plain name", BsdTar.SplitLine("plain.txt", staged.Contains) == ("plain.txt", null));
        }

        /// <summary>
        /// A .tar.xz holding "Notes: draft.txt" (made on Linux) and a file
        /// called "Notes". The line "x Notes: draft.txt" was read as a failure
        /// of "Notes": the renamed "Notes_ draft.txt" went into the folder and
        /// the real "Notes" was held back.
        /// </summary>
        private static async Task ColonNameThroughBsdTarTests()
        {
            if (!BsdTar.Available) return;

            var dir = NewDir("colon");
            try
            {
                var plainTar = Path.Combine(dir, "plain.tar");
                using (var file = File.Create(plainTar))
                using (var writer = new TarWriter(file, TarEntryFormat.Pax))
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Notes")
                    {
                        DataStream = new MemoryStream(Encoding.UTF8.GetBytes("the notes")),
                    });
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Notes: draft.txt")
                    {
                        DataStream = new MemoryStream(Encoding.UTF8.GetBytes("draft")),
                    });
                }

                var archive = Path.Combine(dir, "colon.tar.xz");
                var (exit, errors) = RunTar("-cJf", archive, "@" + plainTar);
                if (exit != 0 || !File.Exists(archive))
                {
                    Check("the system tar made the colon .tar.xz", false, $"exit {exit}: {errors}");
                    return;
                }

                var output = Path.Combine(dir, "out");
                var result = await ArchiveEngine.ExtractAsync(archive, output, PasteConflictPolicy.AutoRename, null,
                    2, null, CancellationToken.None);

                var notes = Path.Combine(output, "Notes");
                Check("the file really called Notes is extracted",
                    File.Exists(notes) && File.ReadAllText(notes) == "the notes", string.Join(" | ", result.Errors));
                Check("the name Windows cannot store is not slipped in under another",
                    !Directory.EnumerateFiles(output).Any(f => Path.GetFileName(f).StartsWith("Notes_", StringComparison.Ordinal)),
                    string.Join(", ", Directory.EnumerateFileSystemEntries(output).Select(Path.GetFileName)));
                Check("and it is refused by its own name",
                    result.Errors.Any(e => e.Contains("Notes: draft.txt") && e.Contains("colon", StringComparison.OrdinalIgnoreCase)),
                    string.Join(" | ", result.Errors));
            }
            catch (Exception ex) { Check("the colon-name .tar.xz test ran", false, ex.ToString()); }
            finally { Remove(dir); }
        }

        /// <summary>
        /// Replace on a folder that is already there: robocopy merged it and
        /// wrote over the files inside in place (and skipped any with the same
        /// size and date, so Replace did not replace them). It now goes through
        /// the engine that writes each file beside the old one and swaps.
        /// </summary>
        private static async Task ReplaceFolderSwapsNestedFilesTests()
        {
            var dir = NewDir("replace");
            try
            {
                var source = Path.Combine(dir, "src", "album");
                var destination = Path.Combine(dir, "dst");
                var target = Path.Combine(destination, "album");
                Directory.CreateDirectory(Path.Combine(source, "disc"));
                Directory.CreateDirectory(Path.Combine(target, "disc"));

                var stamp = new DateTime(2023, 3, 4, 5, 6, 8, DateTimeKind.Utc);
                var newFile = Path.Combine(source, "disc", "track.txt");
                var oldFile = Path.Combine(target, "disc", "track.txt");
                File.WriteAllText(newFile, "NEW-NEW");
                File.WriteAllText(oldFile, "old-old");
                File.SetLastWriteTimeUtc(newFile, stamp);
                File.SetLastWriteTimeUtc(oldFile, stamp);
                File.WriteAllText(Path.Combine(target, "extra.txt"), "only here");

                var result = await RoboCopyEngine.RunAsync(new[] { source }, destination, false, 4,
                    PasteConflictPolicy.Overwrite, null, CancellationToken.None);

                Check("a nested file is replaced even with the same size and date",
                    File.ReadAllText(oldFile) == "NEW-NEW", string.Join(" | ", result.Errors));
                Check("the folder is merged, not emptied",
                    File.Exists(Path.Combine(target, "extra.txt")));
                Check("it is counted as one replace and nothing failed",
                    result.Collisions.OverwrittenCount == 1 && result.Failed == 0,
                    $"replaced={result.Collisions.OverwrittenCount} failed={result.Failed} {string.Join(" | ", result.Errors)}");
                Check("and no temporary file is left in it",
                    !Directory.EnumerateFiles(target, ".copying-*", SearchOption.AllDirectories).Any());
                Check("the source is still there (a copy)", File.Exists(newFile));
            }
            catch (Exception ex) { Check("the Replace-folder test ran", false, ex.ToString()); }
            finally { Remove(dir); }
        }

        /// <summary>
        /// The shell reads "Backup." as "Backup". Recycling the first would put
        /// its sibling in the bin, so ShellDelete itself refuses it.
        /// </summary>
        private static void RecycleRefusesDottedTests()
        {
            var dir = NewDir("dotted");
            try
            {
                var sibling = Path.Combine(dir, "report");
                File.WriteAllText(sibling, "keep me");

                Check("a dotted path is known to be out of the shell's reach",
                    ShellDelete.UnreachableByShell(Path.Combine(dir, "report.")) &&
                    ShellDelete.UnreachableByShell(Path.Combine(dir, "fold.", "a.txt")) &&
                    !ShellDelete.UnreachableByShell(sibling));

                Check("recycling it without a window is refused",
                    ShellDelete.RecycleWithoutUi(Path.Combine(dir, "report.")) == ShellDelete.DottedRefusal);

                bool threw = false;
                try { ShellDelete.Recycle(Path.Combine(dir, "report."), IntPtr.Zero); }
                catch (IOException ex) { threw = ex.Message == ShellDelete.DottedRefusal; }
                Check("and through the shell's own delete", threw);
                Check("and the sibling it would have taken is still there", File.Exists(sibling));
            }
            catch (Exception ex) { Check("the dotted recycle test ran", false, ex.ToString()); }
            finally { Remove(dir); }
        }
    }

    // ---- merged from Sweep500FilesTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// The 5.0.0 sweep of copying, moving and deleting.
    /// </summary>
    internal static class Sweep500FilesTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, copying, moving and deleting:");
            await SystemToolsByFullPath();
            DefaultAppChangesAreSeen();
            RiskyTypesGoThroughTheShell();
            await StraightFromGoogleShowsProgress();
            StatusSentencesSurviveTheDriveCorrection();
            await MoveInsideOneShareIsARename();
        }

        private static string? FindSrc()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var src = Path.Combine(dir, "src");
                if (File.Exists(Path.Combine(src, "RoboCopyEngine.cs"))) return src;
                dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
            }
            return null;
        }

        /// <summary>
        /// robocopy and msiexec are started from System32 by full path, so a
        /// robocopy.exe or msiexec.exe in Downloads (beside a portable copy, or
        /// the current folder) never runs in their place.
        /// </summary>
        private static async Task SystemToolsByFullPath()
        {
            Check("robocopy is started by its full System32 path",
                Path.IsPathFullyQualified(RoboCopyEngine.RobocopyExecutable)
                && File.Exists(RoboCopyEngine.RobocopyExecutable)
                && RoboCopyEngine.RobocopyExecutable.StartsWith(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase),
                RoboCopyEngine.RobocopyExecutable);

            var src = FindSrc();
            if (src != null)
            {
                var bare = new Regex(@"(ProcessStartInfo\(\s*""|FileName\s*=\s*"")([^""\\:/]+\.exe)""", RegexOptions.IgnoreCase);
                var found = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
                    .SelectMany(f => bare.Matches(File.ReadAllText(f)).Select(m => Path.GetFileName(f) + ": " + m.Groups[2].Value))
                    .ToList();
                Check("no program in src is started by a bare exe name", found.Count == 0, string.Join(", ", found));
            }
            else Check("the source folder was found for the bare-name check", false);

            // Behaviour: a planted robocopy.exe in the current folder is not the one that runs.
            var root = Path.Combine(Path.GetTempPath(), "en500-planted-" + Guid.NewGuid().ToString("N"));
            var plant = Path.Combine(root, "plant");
            var from = Path.Combine(root, "from");
            var to = Path.Combine(root, "to");
            Directory.CreateDirectory(plant);
            Directory.CreateDirectory(from);
            Directory.CreateDirectory(to);
            var oldDir = Environment.CurrentDirectory;
            var oldVar = Environment.GetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath");
            try
            {
                File.Copy(Path.Combine(Environment.SystemDirectory, "hostname.exe"), Path.Combine(plant, "robocopy.exe"));
                File.WriteAllText(Path.Combine(from, "note.txt"), "hello");
                Environment.CurrentDirectory = plant;
                Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", null);
                var result = await RoboCopyEngine.RunAsync(new[] { Path.Combine(from, "note.txt") }, to, move: false, 1,
                    PasteConflictPolicy.FillGaps, null, CancellationToken.None);
                Check("a robocopy.exe in the current folder does not run in place of System32's",
                    File.Exists(Path.Combine(to, "note.txt")) && result.Failed == 0,
                    $"copied {result.Copied}, failed {result.Failed}: {string.Join("; ", result.Errors)}");
            }
            finally
            {
                Environment.CurrentDirectory = oldDir;
                Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", oldVar);
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>
        /// A default app changed while the app runs is used within half a
        /// minute, and the Open With picker is never remembered.
        /// </summary>
        private static void DefaultAppChangesAreSeen()
        {
            long clock = 0;
            string answer = @"C:\A\a.exe";
            FileLauncher.ForgetAll();
            FileLauncher.ClockForTests = () => clock;
            FileLauncher.ResolverForTests = _ => (answer, "\"%1\"");
            try
            {
                Equal("the first open uses the handler Windows names", @"C:\A\a.exe", FileLauncher.ResolveHandlerPath(".zzztest") ?? "(null)");
                answer = @"C:\B\b.exe";
                clock = 1_000;
                Equal("a moment later the handler is still the remembered one", @"C:\A\a.exe", FileLauncher.ResolveHandlerPath(".zzztest") ?? "(null)");
                clock = 31_000;
                Equal("half a minute after the default app changed, the new app opens the file", @"C:\B\b.exe", FileLauncher.ResolveHandlerPath(".zzztest") ?? "(null)");

                FileLauncher.ForgetAll();
                answer = @"C:\Windows\system32\OpenWith.exe";
                Equal("an unknown type goes to the Open With picker", answer, FileLauncher.ResolveHandlerPath(".zzzpick") ?? "(null)");
                answer = @"C:\B\b.exe";
                Equal("after choosing an app with Always, the next open goes to that app at once", @"C:\B\b.exe", FileLauncher.ResolveHandlerPath(".zzzpick") ?? "(null)");
            }
            finally
            {
                FileLauncher.ResolverForTests = null;
                FileLauncher.ClockForTests = null;
                FileLauncher.ForgetAll();
            }
        }

        private sealed class Seen<T> : IProgress<T>
        {
            private readonly System.Collections.Generic.List<(long At, T Value)> _items = new();
            private readonly System.Diagnostics.Stopwatch _clock;
            public Seen(System.Diagnostics.Stopwatch clock) => _clock = clock;
            public void Report(T value) { lock (_items) _items.Add((_clock.ElapsedMilliseconds, value)); }
            public System.Collections.Generic.List<(long At, T Value)> Snapshot() { lock (_items) return _items.ToList(); }
        }

        /// <summary>
        /// A copy out of Drive whose file robocopy gave up on is fetched straight
        /// from Google; the window keeps moving and says so all the way through.
        /// </summary>
        private static async Task StraightFromGoogleShowsProgress()
        {
            var root = Path.Combine(Path.GetTempPath(), "en500-google-" + Guid.NewGuid().ToString("N"));
            var src = Path.Combine(root, "src");
            var dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            var locked = Path.Combine(src, "locked.txt");
            File.WriteAllBytes(locked, new byte[100_000]);
            var saved = RoboCopyEngine.DownloadFromCloud;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long fakeStart = -1, fakeEnd = -1;
            FileStream? held = null;
            try
            {
                held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
                RoboCopyEngine.DownloadFromCloud = async (source, landing, moved, token) =>
                {
                    fakeStart = clock.ElapsedMilliseconds;
                    using (var f = new FileStream(landing, FileMode.Create, FileAccess.Write))
                    {
                        for (int i = 1; i <= 10; i++)
                        {
                            await Task.Delay(200, token);
                            f.Write(new byte[10_000]);
                            moved?.Invoke(i * 10_000L);
                        }
                    }
                    fakeEnd = clock.ElapsedMilliseconds;
                    return null;
                };
                var progress = new Seen<TransferProgress>(clock);
                var result = await RoboCopyEngine.RunAsync(new[] { locked }, dst, move: false, 1,
                    PasteConflictPolicy.AutoRename, progress, CancellationToken.None, cloudSource: true);

                var during = progress.Snapshot().Where(x => x.At > fakeStart && x.At <= fakeEnd).Select(x => x.Value).ToList();
                Check("straight from Google: the fallback ran and the file arrived",
                    fakeStart >= 0 && File.Exists(Path.Combine(dst, "locked.txt")) && result.Failed == 0,
                    $"start {fakeStart}, failed {result.Failed}: {string.Join("; ", result.Errors)}");
                Check("straight from Google: the window hears about it all the way through, not once",
                    during.Count >= 4, $"{during.Count} reports in {fakeEnd - fakeStart} ms");
                Check("straight from Google: the bytes and the clock move while it downloads",
                    during.Count >= 2 && during[^1].BytesDone > during[0].BytesDone && during[^1].Elapsed > during[0].Elapsed,
                    string.Join(", ", during.Select(p => $"{p.BytesDone}@{p.Elapsed.TotalSeconds:0.0}")));
                Check("straight from Google: the bytes never go backwards while it downloads",
                    during.Zip(during.Skip(1)).All(z => z.Second.BytesDone >= z.First.BytesDone));
                Check("straight from Google: the window says it is downloading straight from Google, and marks it",
                    during.Count > 0 && during.All(p => p.CurrentItem.StartsWith("Downloading locked.txt straight from Google Drive", StringComparison.Ordinal)
                                                        && p.ItemIsStatus && p.FromGoogle),
                    string.Join(" / ", during.Select(p => p.CurrentItem).Distinct()));
            }
            finally
            {
                RoboCopyEngine.DownloadFromCloud = saved;
                held?.Dispose();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>
        /// The Drive correction (what the mount served) never hides a sentence
        /// the engine wrote, and never overrides the straight-from-Google count.
        /// </summary>
        private static void StatusSentencesSurviveTheDriveCorrection()
        {
            var elapsed = TimeSpan.FromSeconds(4);
            var waiting = RoboCopyEngine.WithDownloads(
                new TransferProgress(5_000_000, 10_000_000, 0, 6, "Waiting for b.ogg (retry 1 of 2)", 0, elapsed, ItemIsStatus: true),
                4_000_000, "b.ogg", 0);
            Equal("a Waiting for sentence is kept over the mount's file name",
                "Waiting for b.ogg (retry 1 of 2)", waiting.CurrentItem);

            var plain = RoboCopyEngine.WithDownloads(
                new TransferProgress(5_000_000, 10_000_000, 0, 6, "a.ogg", 0, elapsed), 4_000_000, "b.ogg", 0);
            Equal("a plain file name still gives way to the file being downloaded", "b.ogg", plain.CurrentItem);

            var google = RoboCopyEngine.WithDownloads(
                new TransferProgress(3_000_000, 10_000_000, 0, 6, "Downloading b.ogg straight from Google Drive", 0, elapsed,
                    ItemIsStatus: true, FromGoogle: true),
                9_000_000, "b.ogg", 9_000_000);
            Equal("straight from Google, the engine's own count wins over the mount's stale one",
                "3000000", google.BytesDone.ToString());
            Equal("straight from Google, the sentence is kept",
                "Downloading b.ogg straight from Google Drive", google.CurrentItem);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileInformation info);

        private static ulong FileId(string path)
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(h, out var info) ? ((ulong)info.IndexHigh << 32) | info.IndexLow : 0;
        }

        /// <summary>
        /// A move inside one network share is a rename on the server, not every
        /// byte pulled down and pushed back.
        /// </summary>
        private static async Task MoveInsideOneShareIsARename()
        {
            Check("one share: two folders on one share are one volume",
                RoboCopyEngine.SameVolume(@"\\server\share\a\x", @"\\server\share\b"));
            Check("one share: the long form of a share path matches the short one",
                RoboCopyEngine.SameVolume(@"\\?\UNC\server\share\a", @"\\server\share\b"));
            Check("one share: share names are matched without regard to case",
                RoboCopyEngine.SameVolume(@"\\Server\Share\a", @"\\server\share\b"));
            Check("one share: another share on the same server is not",
                !RoboCopyEngine.SameVolume(@"\\server\share\a", @"\\server\other\a"));
            Check("one share: the same share name on another server is not",
                !RoboCopyEngine.SameVolume(@"\\server\share\a", @"\\other\share\a"));
            Check("one share: the Google Drive volume is never taken for a share",
                !RoboCopyEngine.SameVolume(@"\\GoogleDrive\acct\a", @"\\GoogleDrive\acct\b"));
            Check("one share: a local folder and a share are not one volume",
                !RoboCopyEngine.SameVolume(Path.GetTempPath(), @"\\server\share\x"));
            Check("one share: a local move is still a rename",
                RoboCopyEngine.SameVolume(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "x")));

            // End to end through the admin share, where there is one.
            var local = Path.Combine(Path.GetTempPath(), "en500-share-" + Guid.NewGuid().ToString("N"));
            if (local.Length < 3 || local[1] != ':') return;
            var unc = @"\\localhost\" + char.ToUpperInvariant(local[0]) + "$" + local[2..];
            Directory.CreateDirectory(local);
            try
            {
                if (!Directory.Exists(unc)) { Console.WriteLine("  (no admin share here: end-to-end share move skipped)"); return; }
                var src = Path.Combine(unc, "src");
                var dst = Path.Combine(unc, "dst");
                Directory.CreateDirectory(Path.Combine(src, "Dir"));
                Directory.CreateDirectory(dst);
                var big = Path.Combine(src, "big.bin");
                File.WriteAllBytes(big, new byte[1 << 20]);
                File.WriteAllText(Path.Combine(src, "Dir", "x.txt"), "X");
                ulong bigId = FileId(Path.Combine(local, "src", "big.bin"));
                ulong xId = FileId(Path.Combine(local, "src", "Dir", "x.txt"));

                var result = await RoboCopyEngine.RunAsync(new[] { big, Path.Combine(src, "Dir") }, dst, move: true, 4,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None);
                var movedBig = Path.Combine(local, "dst", "big.bin");
                var movedX = Path.Combine(local, "dst", "Dir", "x.txt");
                Check("one share: a file moved inside a share is the same file, renamed on the server",
                    File.Exists(movedBig) && !File.Exists(Path.Combine(local, "src", "big.bin")) && FileId(movedBig) == bigId,
                    $"failed {result.Failed}: {string.Join("; ", result.Errors)}");
                Check("one share: a folder moved inside a share is the same folder",
                    File.Exists(movedX) && FileId(movedX) == xId && !Directory.Exists(Path.Combine(local, "src", "Dir")));
            }
            finally
            {
                try { Directory.Delete(local, true); } catch { }
            }
        }

        /// <summary>
        /// Scripts and installers go through the shell, so Windows can show its
        /// security warning for a downloaded one.
        /// </summary>
        private static void RiskyTypesGoThroughTheShell()
        {
            FileLauncher.ForgetAll();
            foreach (var ext in new[] { ".js", ".vbs", ".msi", ".msc", ".cpl", ".reg", ".hta", ".wsf" })
            {
                Check($"{ext} is opened through the shell's security check",
                    FileLauncher.ResolveHandlerPath(ext) == null && FileLauncher.DescribeCommandLine(ext, @"C:\x\a" + ext) == null,
                    FileLauncher.ResolveHandlerPath(ext) ?? "(null)");
            }
            Check("the risky-type rule ignores case", FileLauncher.MustGoThroughShell(".JS"));
            Check("music and text still open the fast way",
                !FileLauncher.MustGoThroughShell(".flac") && !FileLauncher.MustGoThroughShell(".txt"));

            var src = FindSrc();
            var text = src == null ? "" : File.ReadAllText(Path.Combine(src, "FileLauncher.cs"));
            Check("the shell fallback lets the shell show its prompts for risky types",
                Regex.IsMatch(text, @"ErrorDialog\s*=\s*MustGoThroughShell"));
        }
    }

    // ---- merged from Release510FilesTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// 5.1.0: files: running transfers, pause and resume, and search by size, date and kind.
    /// </summary>
    internal static class Release510FilesTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("5.1.0, files: running transfers, pause and resume, and search by size, date and kind:");
            OpenRefusesDottedNames();
            MoveCleanupKeepsNewFiles();
            await PropertiesRefusesDottedNames();
            await PauseGateHolds();
            TransferLines();
            RunningListFollowsWindows();
            await ManagedCopyWaitsWhilePaused();
            await RobocopyWaitsWhilePaused();
            ProcessPauserSuspends();
        }

        // A robocopy in flight is suspended where it is while paused, and let go on Resume.
        private static void ProcessPauserSuspends()
        {
            var ping = System.IO.Path.Combine(Environment.SystemDirectory, "PING.EXE");
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ping, "-n 30 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            try
            {
                bool AllSuspended()
                {
                    process.Refresh();
                    foreach (System.Diagnostics.ProcessThread t in process.Threads)
                        if (t.ThreadState != System.Diagnostics.ThreadState.Wait ||
                            t.WaitReason != System.Diagnostics.ThreadWaitReason.Suspended) return false;
                    return true;
                }

                var pause = new TransferPause();
                using var pauser = new ProcessPauser(pause);
                pauser.Set(process);
                Check("a running process is not suspended", !AllSuspended());
                pause.Pause();
                Check("Pause suspends it", AllSuspended());
                pause.Pause();
                pause.Resume();
                Check("Resume lets it run again, once however often Pause was pressed", !AllSuspended());
                pause.Pause();
                pauser.Set(null);
                Check("letting go of it resumes it", !AllSuspended());
            }
            finally
            {
                try { process.Kill(); } catch { }
            }
        }

        // Feature 8: the gate holds new work while paused and lets it go on Resume.
        private static async Task PauseGateHolds()
        {
            var pause = new TransferPause();
            Check("a running gate is open", pause.WaitAsync(default).IsCompleted);
            var said = new System.Collections.Generic.List<bool>();
            pause.Changed += said.Add;
            pause.Pause();
            var waiting = pause.WaitAsync(default);
            await Task.Delay(50);
            Check("a paused gate holds", !waiting.IsCompleted);
            using (var piece = pause.Until(default))
                Check("a piece started while paused stops at once, as paused", piece.Token.IsCancellationRequested && piece.PausedIt);
            pause.Resume();
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Check("Resume lets it go", waiting.IsCompletedSuccessfully);
            Check("Changed says paused then resumed", said.Count == 2 && said[0] && !said[1]);

            using var cts = new System.Threading.CancellationTokenSource();
            pause.Pause();
            var held = pause.WaitAsync(cts.Token);
            cts.Cancel();
            try { await held; Check("cancel ends a paused wait", false); }
            catch (OperationCanceledException) { Check("cancel ends a paused wait", true); }
            pause.Resume();
        }

        private static void TransferLines()
        {
            var p = new TransferProgress(400, 1000, 4, 12, "a.flac", 5, TimeSpan.FromSeconds(10));
            Equal("a copy's line", "Copying 12 files to Music, 40 percent, about 2 minutes left",
                TransferText.Line("Copying to Music", p, false));
            Equal("a paused line", "Moving 12 files to Music, 40 percent, paused",
                TransferText.Line("Moving to Music", p, true));
            Equal("a line before any progress", "Extracting to Downloads, starting",
                TransferText.Line("Extracting to Downloads", null, false));
            Equal("nothing running", "Nothing is being copied or moved right now.",
                TransferText.Overall(Array.Empty<(string, TransferProgress?, bool)>()));
            var q = new TransferProgress(600, 1000, 1, 2, "b", 5, TimeSpan.FromSeconds(10));
            Equal("two at once", "2 transfers are running, 50 percent done in all, 1 paused.",
                TransferText.Overall(new (string, TransferProgress?, bool)[] { ("Copying to A", p, false), ("Copying to B", q, true) }));
            Equal("hours", "about 2 hours and 5 minutes left", TransferText.Left(TimeSpan.FromMinutes(125)));
        }

        // A transfer window is listed from when it is made until its transfer is over.
        private static void RunningListFollowsWindows()
        {
            // A form would install WinForms' context here, and the awaits after
            // it would wait for a message loop the suite does not run.
            var context = System.Threading.SynchronizationContext.Current;
            try { RunningListCore(); }
            finally { System.Threading.SynchronizationContext.SetSynchronizationContext(context); }
        }

        private static void RunningListCore()
        {
            using var cts = new System.Threading.CancellationTokenSource();
            using var window = new ProgressForm("Copying to Music", new Settings(), cts);
            Check("a new transfer window is listed", System.Linq.Enumerable.Contains(ProgressForm.Running, window));
            window.Update(new TransferProgress(50, 100, 1, 3, "x", 0, TimeSpan.Zero));
            Equal("it describes itself", "Copying 3 files to Music, 50 percent", window.Describe());
            window.Pause = new TransferPause();
            window.TogglePause();
            Check("Pause from the list pauses its transfer", window.IsPaused);
            window.TogglePause();
            Check("and Resume resumes it", !window.IsPaused);
            window.Finish();
            Check("a finished transfer leaves the list", !System.Linq.Enumerable.Contains(ProgressForm.Running, window));
        }

        private static string NewFolder(string tag)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"en510{tag}-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }

        // The managed engine waits at the gate: nothing lands while paused.
        private static async Task ManagedCopyWaitsWhilePaused()
        {
            var src = NewFolder("src");
            var dst = NewFolder("dst");
            try
            {
                var file = System.IO.Path.Combine(src, "song.flac");
                System.IO.File.WriteAllBytes(file, new byte[300_000]);
                var pause = new TransferPause();
                pause.Pause();
                var run = FileOperations.RunAsync(new[] { file }, dst, false, PasteConflictPolicy.AutoRename,
                    2, 64, null, null, default, pause: pause);
                await Task.Delay(400);
                Check("a paused managed copy writes nothing", !run.IsCompleted &&
                    !System.IO.File.Exists(System.IO.Path.Combine(dst, "song.flac")));
                pause.Resume();
                var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
                Check("resumed, it finishes", result.Succeeded == 1 &&
                    new System.IO.FileInfo(System.IO.Path.Combine(dst, "song.flac")).Length == 300_000);
            }
            finally
            {
                try { System.IO.Directory.Delete(src, true); } catch { }
                try { System.IO.Directory.Delete(dst, true); } catch { }
            }
        }

        // A robocopy paste waits at the gate before its job, then copies on Resume.
        private static async Task RobocopyWaitsWhilePaused()
        {
            var src = NewFolder("src");
            var dst = NewFolder("dst");
            try
            {
                var file = System.IO.Path.Combine(src, "track.wav");
                System.IO.File.WriteAllBytes(file, new byte[200_000]);
                var pause = new TransferPause();
                pause.Pause();
                var run = RoboCopyEngine.RunAsync(new[] { file }, dst, false, 2, PasteConflictPolicy.Overwrite,
                    null, default, pause: pause);
                await Task.Delay(1500);
                Check("a paused robocopy paste copies nothing", !run.IsCompleted &&
                    !System.IO.File.Exists(System.IO.Path.Combine(dst, "track.wav")));
                pause.Resume();
                var result = await run.WaitAsync(TimeSpan.FromSeconds(60));
                Check("resumed, robocopy copies it", !result.Cancelled && result.Failed == 0 &&
                    System.IO.File.Exists(System.IO.Path.Combine(dst, "track.wav")), string.Join("; ", result.Errors));
            }
            finally
            {
                try { System.IO.Directory.Delete(src, true); } catch { }
                try { System.IO.Directory.Delete(dst, true); } catch { }
            }
        }

        // Bug 1: "notes.txt." or a file under "Backup." would open its sibling.
        private static void OpenRefusesDottedNames()
        {
            var launched = false;
            FileLauncher.ResolverForTests = _ => { launched = true; return ("notepad.exe", "\"%1\""); };
            try
            {
                foreach (var path in new[] { @"C:\x\notes.txt.", @"C:\x\Backup.\a.txt", @"C:\x\name \a.txt" })
                {
                    string? why = null;
                    try { FileLauncher.Open(path); } catch (System.IO.IOException ex) { why = ex.Message; }
                    Check($"Open refuses {path}", why == FileLauncher.MisreadMessage, why);
                }
                Check("Open launches nothing for a dotted name", !launched);
            }
            finally
            {
                FileLauncher.ResolverForTests = null;
                FileLauncher.ForgetAll();
            }
        }

        // Bug 2: a file written into a moved folder after the emptiness walk survives.
        private static void MoveCleanupKeepsNewFiles()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "en510files-" + Guid.NewGuid().ToString("N"));
            var sub = System.IO.Path.Combine(root, "sub");
            var deeper = System.IO.Path.Combine(sub, "deeper");
            var other = System.IO.Path.Combine(root, "other");
            try
            {
                System.IO.Directory.CreateDirectory(deeper);
                System.IO.Directory.CreateDirectory(other);
                // Parents first, as the walk lists them; then a download lands.
                var walked = new System.Collections.Generic.List<string> { root, sub, deeper, other };
                var late = System.IO.Path.Combine(sub, "arrived.part");
                System.IO.File.WriteAllText(late, "late");
                FileOperations.DeleteDeepestFirst(walked);
                Check("the file written after the walk survives", System.IO.File.Exists(late));
                Check("empty folders beside it are cleared", !System.IO.Directory.Exists(other) && !System.IO.Directory.Exists(deeper));
                System.IO.File.Delete(late);
                FileOperations.DeleteDeepestFirst(walked);
                Check("an empty tree is cleared", !System.IO.Directory.Exists(root));
            }
            finally
            {
                try { System.IO.Directory.Delete(root, true); } catch { }
            }
        }

        // Bug 3: Properties on "report." would show and edit "report".
        private static async Task PropertiesRefusesDottedNames()
        {
            string? why = null;
            try { await ShellContextMenu.ShowPropertiesAsync(IntPtr.Zero, @"C:\x\report."); }
            catch (System.IO.IOException ex) { why = ex.Message; }
            Check("Properties refuses a name ending in a dot", why == ShellContextMenu.PropertiesMisreadMessage, why);
        }
    }

    // ---- merged from FilesR2Tests.cs (7.1.0: tests are filed by feature, not by fix round) ----

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
            try { return File.Exists(path) ? SourceFiles.Read(path) : ""; }
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
        /// skips it and nothing is written, but its summary says 3GB, which is
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
        /// A stream that cannot be copied, another program holds it here; on
        /// FAT, exFAT or a NAS there is nowhere to put one, failed and deleted a
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
                Check("per item: a colliding folder moved on one volume is renamed, not copied",
                    File.Exists(Path.Combine(moved, "a.txt")) && FileId(Path.Combine(moved, "a.txt")) == idA &&
                    File.Exists(Path.Combine(moved, "cd2", "b.txt")) && FileId(Path.Combine(moved, "cd2", "b.txt")) == idB,
                    string.Join(" | ", result.Errors));
                Check("per item: and the source is gone", !Directory.Exists(album));

                var source = SourceText("FileOperations.cs");
                // The per-file body, which the parallel loop calls for each
                // file of a run: from CopyOne to the tidying that follows it.
                var loop = source[source.IndexOf("async ValueTask CopyOne(", StringComparison.Ordinal)..];
                loop = loop[..loop.IndexOf("// A move leaves the source directory skeletons", StringComparison.Ordinal)];
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

            // The list folds each name once and searches the folded names. It has
            // to land exactly where folding on every keystroke did.
            var rows = new[] { "Apple", "Æble", "Banana", "Éclair", "eclat", "Øresund", "Straße", "ssh", "ﬁle", "日本語", "🎶 tune", "zebra", "Zoë" };
            var folded = rows.Select(TypeAhead.Fold).ToArray();
            string[] typed = { "a", "ae", "ÆÆ", "e", "é", "eé", "ec", "o", "or", "s", "ß", "st", "fi", "ﬁ", "日", "🎶", "🎶🎶", "z", "zo", "q", "" };
            int same = 0, asked = 0;
            foreach (var query in typed)
                for (int at = -1; at < rows.Length; at++)
                {
                    asked++;
                    if (TypeAhead.Find(rows.Length, i => rows[i], query, at) ==
                        TypeAhead.FindFolded(rows.Length, i => folded[i], query, at)) same++;
                }
            Check("fold: searching names folded once finds what folding per keystroke found", asked == same, $"{same} of {asked}");
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
        /// file cannot be read, which used to save defaults over it.
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

    // ---- merged from Sweep308DotPathTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// Folders whose name ends in a dot, away from the main window. Windows
    /// strips the dot from an ordinary path, so "Backup." asked about clearly is
    /// "Backup": a shortcut made inside it landed in the sibling (or nowhere),
    /// an archive with no telling extension in it was not recognised, Compress
    /// and Extract could not write into it, and its size came out as nothing.
    ///
    /// Every check runs beside a sibling "Backup" holding something else, so a
    /// call that loses the dot is caught reaching the wrong folder rather than
    /// only failing.
    /// </summary>
    internal static class Sweep308DotPathTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        public static void RunAll(Action<string, bool, string?> check)
        {
            _check = check;
            Console.WriteLine("Folders ending in a dot, outside the main window (3.0.8):");

            var root = Path.Combine(Path.GetTempPath(), "ExplorerNativeTests_dotpath_" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var dotted = Path.Combine(root, "Backup.");
                var sibling = Path.Combine(root, "Backup");
                Directory.CreateDirectory(NameRules.ExactPath(dotted));
                Directory.CreateDirectory(sibling);
                File.WriteAllText(NameRules.ExactPath(Path.Combine(dotted, "real.txt")), "the dotted folder's file");
                File.WriteAllText(Path.Combine(sibling, "decoy.txt"), "the sibling's file, which is longer than the other");

                Run("shortcut", () => Shortcuts(root, dotted, sibling));
                Run("identify", () => Identify(dotted));
                Run("compress", () => Compress(root, dotted, sibling));
                Run("extract", () => Extract(root, dotted, sibling));
                Run("folder size", () => FolderSize(root, dotted));
                Run("suggested name", () => SuggestedName(root));
                SourceRules();
            }
            catch (Exception ex)
            {
                Check("dot-path fixtures were made", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(NameRules.ExactPath(root), recursive: true); } catch { }
            }

            Console.WriteLine();
        }

        private static void Run(string what, Action body)
        {
            try { body(); }
            catch (Exception ex) { Check($"{what} checks ran", false, ex.GetType().Name + ": " + ex.Message); }
        }

        private static bool ExistsExactly(string path) =>
            File.Exists(NameRules.ExactPath(path)) || Directory.Exists(NameRules.ExactPath(path));

        /// <summary>
        /// Create shortcut. The .lnk itself is written into the dotted folder
        /// exactly; a target under a dotted name is refused, because the shell
        /// can only store it without the dot (it refuses the literal path).
        /// </summary>
        private static void Shortcuts(string root, string dotted, string sibling)
        {
            var plainTarget = Path.Combine(sibling, "decoy.txt");
            var inside = Path.Combine(dotted, ShellLink.NameFor(plainTarget));
            ShellLink.CreateAsync(plainTarget, inside).GetAwaiter().GetResult();

            Check("a shortcut saved into a folder ending in a dot is written there",
                File.Exists(NameRules.ExactPath(inside)));
            Check("and not into the sibling without the dot",
                !File.Exists(Path.Combine(sibling, ShellLink.NameFor(plainTarget))));
            var pointsAt = ShellLink.TargetOfAsync(inside).GetAwaiter().GetResult();
            Check("and it reads back from there with its target",
                string.Equals(pointsAt, plainTarget, StringComparison.OrdinalIgnoreCase), pointsAt);

            foreach (var target in new[] { Path.Combine(dotted, "real.txt"), dotted })
            {
                var link = Path.Combine(root, ShellLink.NameFor(target));
                string? refusal = null;
                try { ShellLink.CreateAsync(target, link).GetAwaiter().GetResult(); }
                catch (NotSupportedException ex) { refusal = ex.Message; }

                Check($"a shortcut to {Path.GetFileName(target)} under a dotted name is refused in words, not pointed at the sibling",
                    refusal != null && refusal.Contains("dot") && !File.Exists(NameRules.ExactPath(link)),
                    refusal ?? (File.Exists(NameRules.ExactPath(link)) ? ShellLink.TargetOfAsync(link).GetAwaiter().GetResult() : "nothing thrown"));
            }
        }

        /// <summary>An archive whose name says nothing is known by its bytes, read through the exact path.</summary>
        private static void Identify(string dotted)
        {
            var zip = Path.Combine(dotted, "named.zip");
            var made = ArchiveEngine.CompressAsync(new[] { Path.Combine(dotted, "real.txt") }, zip,
                ArchiveFormats.Zip, ArchiveLevel.Fastest, 1, null, CancellationToken.None).GetAwaiter().GetResult();
            Check("a zip is written into a folder ending in a dot", ExistsExactly(zip) && made.Failed == 0,
                string.Join("; ", made.Errors));

            var blob = Path.Combine(dotted, "blob");
            File.Copy(NameRules.ExactPath(zip), NameRules.ExactPath(blob));
            Check("an archive with no extension in a dotted folder is still recognised by its bytes",
                ArchiveFormats.Identify(blob) == ArchiveFormats.Zip,
                ArchiveFormats.Identify(blob)?.Id ?? "null");
        }

        /// <summary>Compress from the dotted folder, and into it, in each engine.</summary>
        private static void Compress(string root, string dotted, string sibling)
        {
            foreach (var format in Formats())
            {
                var outside = Path.Combine(root, "from" + format.Extension);
                bool ours = format.Engine != ArchiveEngineKind.BsdTar;
                if (ours)
                {
                    var from = ArchiveEngine.CompressAsync(new[] { dotted }, outside, format,
                        ArchiveLevel.Fastest, 1, null, CancellationToken.None).GetAwaiter().GetResult();
                    Check($"{format.Extension}: the dotted folder compresses with its own file",
                        from.Failed == 0 && File.Exists(outside) &&
                        Listed(outside).Any(n => n.EndsWith("real.txt")) && !Listed(outside).Any(n => n.EndsWith("decoy.txt")),
                        string.Join("; ", from.Errors.Concat(File.Exists(outside) ? Listed(outside) : Array.Empty<string>())));
                }
                else
                {
                    // bsdtar is handed the folder as a junction, and the file by name.
                    var from = ArchiveEngine.CompressAsync(new[] { Path.Combine(dotted, "real.txt") }, outside, format,
                        ArchiveLevel.Fastest, 1, null, CancellationToken.None).GetAwaiter().GetResult();
                    Check($"{format.Extension}: a file inside the dotted folder compresses",
                        from.Failed == 0 && File.Exists(outside), string.Join("; ", from.Errors));

                    // The dotted folder itself it can only be given by name, which
                    // it reads without the dot: refused, in words.
                    string? refusal = null;
                    try
                    {
                        ArchiveEngine.CompressAsync(new[] { dotted }, Path.Combine(root, "whole" + format.Extension), format,
                            ArchiveLevel.Fastest, 1, null, CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (NotSupportedException ex) { refusal = ex.Message; }
                    Check($"{format.Extension}: the dotted folder itself is refused in words, not read as the sibling",
                        refusal != null && refusal.Contains("dot") && !File.Exists(Path.Combine(root, "whole" + format.Extension)),
                        refusal ?? "nothing thrown");
                }

                var into = Path.Combine(dotted, "into" + format.Extension);
                var made = ArchiveEngine.CompressAsync(new[] { Path.Combine(sibling, "decoy.txt") }, into, format,
                    ArchiveLevel.Fastest, 1, null, CancellationToken.None).GetAwaiter().GetResult();
                Check($"{format.Extension}: an archive is written into the dotted folder",
                    made.Failed == 0 && ExistsExactly(into) && !File.Exists(Path.Combine(sibling, "into" + format.Extension)),
                    string.Join("; ", made.Errors));

                // Made a second time over the first: the replace path, staged
                // beside it and moved into place.
                var again = ArchiveEngine.CompressAsync(new[] { Path.Combine(sibling, "decoy.txt") }, into, format,
                    ArchiveLevel.Fastest, 1, null, CancellationToken.None).GetAwaiter().GetResult();
                Check($"{format.Extension}: and replaced there",
                    again.Failed == 0 && !again.Cancelled && ExistsExactly(into),
                    string.Join("; ", again.Errors));
            }
        }

        /// <summary>Extract an archive that sits in the dotted folder, into the dotted folder.</summary>
        private static void Extract(string root, string dotted, string sibling)
        {
            foreach (var format in Formats())
            {
                var source = Path.Combine(root, "x" + format.Extension);
                ArchiveEngine.CompressAsync(new[] { Path.Combine(sibling, "decoy.txt") }, source, format,
                    ArchiveLevel.Fastest, 1, null, CancellationToken.None).GetAwaiter().GetResult();
                var inDotted = Path.Combine(dotted, "x" + format.Extension);
                File.Copy(source, NameRules.ExactPath(inDotted), overwrite: true);

                // Enter on it: browsed as a folder, and one file taken out to open.
                try
                {
                    var listing = ArchiveBrowser.Open(inDotted, null, CancellationToken.None);
                    var opened = ArchiveBrowser.ExtractOne(listing, "decoy.txt", null, CancellationToken.None);
                    Check($"{format.Extension}: an archive in a dotted folder opens as a folder, and a file comes out of it",
                        listing.Find("decoy.txt") != null && File.ReadAllText(opened).StartsWith("the sibling's file"), opened);
                }
                catch (Exception ex)
                {
                    Check($"{format.Extension}: an archive in a dotted folder opens as a folder, and a file comes out of it",
                        false, ex.GetType().Name + ": " + ex.Message);
                }

                var destination = Path.Combine(dotted, "out" + format.Id);
                var result = ArchiveEngine.ExtractAsync(inDotted, destination, PasteConflictPolicy.AutoRename, null,
                    1, null, CancellationToken.None).GetAwaiter().GetResult();
                Check($"{format.Extension}: an archive in a dotted folder extracts into it",
                    result.Failed == 0 && !result.Cancelled && File.Exists(NameRules.ExactPath(Path.Combine(destination, "decoy.txt"))) &&
                    !Directory.Exists(Path.Combine(sibling, "out" + format.Id)),
                    string.Join("; ", result.Errors));

                // Straight into the dotted folder, where a file of that name is
                // already waiting: the clash has to be seen there, not in the sibling.
                File.WriteAllText(NameRules.ExactPath(Path.Combine(dotted, "decoy.txt")), "already here");
                var clash = ArchiveEngine.ExtractAsync(source, dotted, PasteConflictPolicy.AutoRename, null,
                    1, null, CancellationToken.None).GetAwaiter().GetResult();
                Check($"{format.Extension}: extracting into the dotted folder keeps what was there and renames the arrival",
                    clash.Failed == 0 &&
                    File.ReadAllText(NameRules.ExactPath(Path.Combine(dotted, "decoy.txt"))) == "already here" &&
                    File.Exists(NameRules.ExactPath(Path.Combine(dotted, "decoy (2).txt"))),
                    string.Join("; ", clash.Errors));
                try { File.Delete(NameRules.ExactPath(Path.Combine(dotted, "decoy.txt"))); } catch { }
                try { File.Delete(NameRules.ExactPath(Path.Combine(dotted, "decoy (2).txt"))); } catch { }
            }
        }

        /// <summary>The dotted folder's size is its own, and a dotted subfolder is walked into.</summary>
        private static void FolderSize(string root, string dotted)
        {
            var sized = Path.Combine(root, "sized.");
            Directory.CreateDirectory(NameRules.ExactPath(Path.Combine(sized, "inner.")));
            File.WriteAllBytes(NameRules.ExactPath(Path.Combine(sized, "a.bin")), new byte[100]);
            File.WriteAllBytes(NameRules.ExactPath(Path.Combine(sized, "inner.", "b.bin")), new byte[23]);
            Directory.CreateDirectory(Path.Combine(root, "sized", "inner"));
            File.WriteAllBytes(Path.Combine(root, "sized", "decoy.bin"), new byte[5000]);

            using var sizes = new FolderSizeCalculator();
            var result = sizes.CalculateAsync(sized).GetAwaiter().GetResult();
            Check("a folder ending in a dot is measured, dotted subfolder included",
                result.Complete && result.Bytes == 123 && result.Files == 2 && result.Folders == 1,
                $"{result.Bytes} bytes, {result.Files} files, {result.Folders} folders, complete {result.Complete}");

            var again = sizes.CalculateAsync(sized).GetAwaiter().GetResult();
            Check("and measured the same from the cache", again.Bytes == 123, again.Bytes.ToString());
        }

        /// <summary>A folder has no extension, so "Backup." compressed is "Backup..zip", not "Backup.zip".</summary>
        private static void SuggestedName(string root)
        {
            // No sibling "Solo": asked clearly, the folder is not there and was
            // taken for a file, whose "extension" (the dot) was stripped.
            var solo = Path.Combine(root, "Solo.");
            Directory.CreateDirectory(NameRules.ExactPath(solo));
            var name = ArchiveFormats.SuggestedName(new[] { solo }, ArchiveFormats.Zip);
            Check("the suggested name keeps a dotted folder's whole name", name == "Solo..zip", name);
        }

        private static void SourceRules()
        {
            var form = Source("ArchiveForm.cs");
            Check("Compress's \"already here\" question asks about the exact path",
                form != null && form.Contains("NameRules.ExactPath(full)"));

            // Opening "Backup." from the shell verb or a command line: checked
            // exactly, and never through GetFullPath, which drops the dot.
            var program = Source("Program.cs");
            int at = program?.IndexOf("FirstPathArgument(string[] args)", StringComparison.Ordinal) ?? -1;
            var body = at < 0 ? "" : program![at..Math.Min(program.Length, at + 1500)];
            Check("a launch path is checked exactly and kept as given when it needs that",
                body.Contains("NameRules.ExactPath(candidate)") && body.Contains("exact != candidate ? candidate"));
        }

        /// <summary>Our own zip and tar.gz writers, and one the system tar does when it is here.</summary>
        private static ArchiveFormat[] Formats() => BsdTar.Available
            ? new[] { ArchiveFormats.Zip, ArchiveFormats.TarGz, ArchiveFormats.TarXz }
            : new[] { ArchiveFormats.Zip, ArchiveFormats.TarGz };

        private static string[] Listed(string archive)
        {
            try
            {
                var entries = archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? ZipEngine.List(archive, CancellationToken.None)
                    : TarEngine.List(archive, ArchiveFormats.TarGz, CancellationToken.None);
                return entries.Select(e => e.Name).ToArray();
            }
            catch (Exception ex) { return new[] { "listing failed: " + ex.Message }; }
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }

    // ---- merged from MainFileOpsFixTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// The 2.4.0 sweep of the main window and the file operations: Replace over
    /// a read-only file by a same-volume move and by a converted file, a file
    /// and a folder never "replaced" by each other, the clipboard not freezing
    /// the window for five seconds, converting into OneDrive-style subfolders,
    /// "1 item" in the progress window, and the source rules for the timers,
    /// Go to path and the audio editor's save.
    /// </summary>
    internal static class MainFileOpsFixTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Main window and file operation fixes:");
            var root = Path.Combine(Path.GetTempPath(), "en-mainfileops-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            try
            {
                await MoveOverReadOnly(root);
                await FileAndFolderNeverReplaced(root);
                ConvertedFileReplacesReadOnly(root);
                GatherWalksPlaceholderFolders(root);
            }
            finally
            {
                try { ClearReadOnly(root); Directory.Delete(root, recursive: true); } catch { }
            }

            ProgressSaysOneItem();
            ClipboardDoesNotFreeze();
            SourceRules();
            Console.WriteLine();
        }

        private static void ClearReadOnly(string root)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }

        private static string Fresh(string root, string name)
        {
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>(a) A same-volume move is a rename, and Replace over a read-only file must still replace it.</summary>
        private static async Task MoveOverReadOnly(string root)
        {
            var from = Fresh(root, "move-from");
            var to = Fresh(root, "move-to");
            var source = Path.Combine(from, "song.txt");
            var target = Path.Combine(to, "song.txt");
            File.WriteAllText(source, "new");
            File.WriteAllText(target, "old");
            File.SetAttributes(target, FileAttributes.ReadOnly);

            var result = await FileOperations.RunAsync(new[] { source }, to, move: true,
                PasteConflictPolicy.Overwrite, 2, 64, null, null, CancellationToken.None);

            Equal("a move over a read-only file fails nothing", "0", result.Failed.ToString());
            Equal("and the new file is the one there", "new", File.Exists(target) ? File.ReadAllText(target) : "(missing)");
            Check("and the original has gone, as a move", !File.Exists(source));
            Equal("and it is said as replaced", "1", result.Collisions.OverwrittenCount.ToString());

            // The swap the copy path already did, now shared: still replaces.
            var copyFrom = Fresh(root, "copy-from");
            var copySource = Path.Combine(copyFrom, "song.txt");
            File.WriteAllText(copySource, "copied");
            File.SetAttributes(target, FileAttributes.ReadOnly);
            var copied = await FileOperations.RunAsync(new[] { copySource }, to, move: false,
                PasteConflictPolicy.Overwrite, 2, 64, null, null, CancellationToken.None);
            Equal("a copy over a read-only file still replaces it", "copied|0",
                (File.Exists(target) ? File.ReadAllText(target) : "(missing)") + "|" + copied.Failed);
        }

        /// <summary>(b) Replace never swaps a file and a folder, and never calls that a replacement.</summary>
        private static async Task FileAndFolderNeverReplaced(string root)
        {
            var from = Fresh(root, "kind-from");
            var to = Fresh(root, "kind-to");

            // A file arriving where a folder of its name is.
            var file = Path.Combine(from, "notes");
            File.WriteAllText(file, "a file");
            var folderThere = Fresh(to, "notes");
            File.WriteAllText(Path.Combine(folderThere, "inside.txt"), "kept");

            var result = await FileOperations.RunAsync(new[] { file }, to, move: false,
                PasteConflictPolicy.Overwrite, 2, 64, null, null, CancellationToken.None);
            Equal("a file over a folder is not counted as replaced", "0", result.Collisions.OverwrittenCount.ToString());
            Equal("it is a failure", "1", result.Failed.ToString());
            Check("which says a folder is there",
                result.Errors.Any(e => e.Contains("folder of that name is already there")), string.Join(" | ", result.Errors));
            Check("and the folder and what is in it are untouched",
                File.Exists(Path.Combine(folderThere, "inside.txt")));

            // A folder arriving where a file of its name is.
            var folder = Fresh(from, "photos");
            File.WriteAllText(Path.Combine(folder, "a.jpg"), "jpg");
            var fileThere = Path.Combine(to, "photos");
            File.WriteAllText(fileThere, "a file called photos");

            var reverse = await FileOperations.RunAsync(new[] { folder }, to, move: true,
                PasteConflictPolicy.Overwrite, 2, 64, null, null, CancellationToken.None);
            Equal("a folder over a file is not counted as replaced", "0", reverse.Collisions.OverwrittenCount.ToString());
            Equal("it is one failure", "1", reverse.Failed.ToString());
            Equal("the file there is untouched", "a file called photos", File.ReadAllText(fileThere));
            Check("and the move left the source folder where it was", File.Exists(Path.Combine(folder, "a.jpg")));

            // Asked, and answered Replace: the same.
            var asked = await FileOperations.RunAsync(new[] { file }, to, move: false,
                PasteConflictPolicy.Ask, 2, 64, null, _ => FileOperations.ConflictChoice.Overwrite, CancellationToken.None);
            Equal("Replace chosen in the question is refused the same way", "0|1",
                asked.Collisions.OverwrittenCount + "|" + asked.Failed);

            // A file over a file is still a replacement.
            var plain = Path.Combine(from, "plain.txt");
            File.WriteAllText(plain, "new");
            File.WriteAllText(Path.Combine(to, "plain.txt"), "old");
            var same = await FileOperations.RunAsync(new[] { plain }, to, move: false,
                PasteConflictPolicy.Overwrite, 2, 64, null, null, CancellationToken.None);
            Equal("a file over a file is still replaced", "1|0|new",
                same.Collisions.OverwrittenCount + "|" + same.Failed + "|" + File.ReadAllText(Path.Combine(to, "plain.txt")));
        }

        /// <summary>A converted file chosen to replace a read-only one replaces it.</summary>
        private static void ConvertedFileReplacesReadOnly(string root)
        {
            var dir = Fresh(root, "convert-place");
            var target = Path.Combine(dir, "song.mp3");
            File.WriteAllText(target, "old");
            File.SetAttributes(target, FileAttributes.ReadOnly);
            var temp = AudioConverter.TempPathBeside(target);
            File.WriteAllText(temp, "converted");

            string placed = "", failure = "";
            try { placed = AudioConverter.Place(temp, target, replace: true); }
            catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }

            Check("Replace puts a converted file over a read-only one", failure.Length == 0, failure);
            Equal("under the name asked for", target, placed);
            Equal("with the new contents", "converted", File.ReadAllText(target));
            Check("and the temporary file has gone", !File.Exists(temp));
        }

        /// <summary>Converting a folder goes into subfolders that are not links, reparse point or not.</summary>
        private static void GatherWalksPlaceholderFolders(string root)
        {
            var top = Fresh(root, "gather");
            var sub = Fresh(top, "Disc 1");
            File.WriteAllBytes(Path.Combine(sub, "track.wav"), new byte[16]);

            var elsewhere = Fresh(root, "gather-elsewhere");
            File.WriteAllBytes(Path.Combine(elsewhere, "linked.wav"), new byte[16]);
            bool junction = true;
            try { ReparseLinks.CreateJunction(Path.Combine(top, "link"), elsewhere); }
            catch { junction = false; }

            var found = AudioConverter.Gather(new[] { top }, subfolders: true, null, CancellationToken.None)
                .Select(Path.GetFileName).ToList();
            Check("a subfolder's audio is gathered", found.Contains("track.wav"), string.Join(", ", found));
            if (junction)
                Check("a junction is still not followed", !found.Contains("linked.wav"), string.Join(", ", found));

            // A cloud placeholder (OneDrive, Files On-Demand) is a reparse point
            // that is not a link, and cannot be made in a test: the rule is read.
            var source = Source("AudioConvert.cs");
            var gather = source == null ? null : Between(source, "public static List<string> Gather(", "public static string TargetFor(");
            Check("Gather skips links, not every reparse point",
                gather != null && gather.Contains("NameRules.IsLink(") && !gather.Contains("FileAttributes.ReparsePoint) == 0"));
        }

        private static void ProgressSaysOneItem()
        {
            string text = "", error = "";
            var thread = new Thread(() =>
            {
                try
                {
                    using var cts = new CancellationTokenSource();
                    using var window = new ProgressForm("Copying", new Settings(), cts);
                    _ = window.Handle;
                    window.Update(new TransferProgress(0, 0, 1, 0, "a.txt", 0, TimeSpan.Zero));
                    var box = AllControls(window).OfType<TextBox>().FirstOrDefault(b => b.AccessibleName == "Progress");
                    text = box?.Text ?? "(no progress line)";
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(30))) error = "timed out";

            Check("the progress window could be built", error.Length == 0, error);
            Equal("one item with no total is \"1 item\", not \"1 items\"", "1 item so far, total not yet known", text);
        }

        private static System.Collections.Generic.IEnumerable<Control> AllControls(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (var inner in AllControls(child)) yield return inner;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        /// <summary>
        /// Copy path while another program holds the clipboard gives up in well
        /// under a second, rather than five nested retry loops of one second
        /// each on the UI thread. The clipboard's contents are never changed: it
        /// is held for the whole call, so nothing can be written to it.
        /// </summary>
        private static void ClipboardDoesNotFreeze()
        {
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            bool holding = false;
            var holder = new Thread(() =>
            {
                for (int attempt = 0; attempt < 20 && !holding; attempt++)
                {
                    holding = OpenClipboard(IntPtr.Zero);
                    if (!holding) Thread.Sleep(50);
                }
                held.Set();
                if (!holding) return;
                release.Wait(TimeSpan.FromSeconds(20));
                CloseClipboard();
            }) { IsBackground = true };
            holder.Start();
            held.Wait(TimeSpan.FromSeconds(5));

            if (!holding)
            {
                Check("the clipboard could be held for the test (skipped: another program has it)", true);
                release.Set();
                return;
            }

            bool result = true;
            long elapsed = 0;
            string error = "";
            var caller = new Thread(() =>
            {
                try
                {
                    var clock = Stopwatch.StartNew();
                    result = ClipboardInterop.SetText("explorer native clipboard test");
                    elapsed = clock.ElapsedMilliseconds;
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            }) { IsBackground = true };
            caller.SetApartmentState(ApartmentState.STA);
            caller.Start();
            bool finished = caller.Join(TimeSpan.FromSeconds(15));
            release.Set();
            holder.Join(TimeSpan.FromSeconds(5));

            Check("setting text on a held clipboard returns", finished && error.Length == 0, error);
            Check("and says it could not", !result);
            Check("within two and a half seconds, not five", elapsed < 2500, $"{elapsed}ms");

            var source = Source("ClipboardInterop.cs");
            var clear = source == null ? null : Between(source, "public static bool ClearIfStillOurs(", "public static bool SetText(");
            Check("clearing a spent cut asks Clipboard.Clear once, which retries by itself",
                clear != null && Regex.Matches(clear, @"Clipboard\.Clear\(\)").Count == 1 &&
                !clear.Contains("Sleep("));
        }

        private static void SourceRules()
        {
            var main = Source("MainForm.cs");
            if (main == null) { Check("MainForm.cs can be read", false); return; }

            // Every WinForms timer the window makes is let go of with it.
            var dispose = Between(main, "protected override void Dispose(bool disposing)", "base.Dispose(disposing);");
            var timers = Regex.Matches(main, @"private\s+System\.Windows\.Forms\.Timer\??\s+(_\w+)")
                .Select(m => m.Groups[1].Value).Distinct().ToList();
            var undisposed = timers.Where(t => dispose == null ||
                !Regex.IsMatch(dispose, Regex.Escape(t) + @"\??\.Dispose\(\)")).ToList();
            Check("the window has timers to look at", timers.Count >= 5, string.Join(", ", timers));
            Check("every timer the window makes is disposed with it", undisposed.Count == 0,
                "not disposed: " + string.Join(", ", undisposed));

            // Go to path is async void from Ctrl+L: guarded like every other command.
            var goTo = Between(main, "private async void GoToPath()", "private async Task GoToPathAsync()")
                       ?? Between(main, "private async void GoToPath()", "private async void Refresh_()");
            Check("Go to path catches what would otherwise take the window down",
                goTo != null && goTo.Contains("catch (Exception"));

            // The audio editor stays busy through the whole save, not just the render.
            var editor = Source("AudioEditorForm.cs");
            if (editor == null) { Check("AudioEditorForm.cs can be read", false); return; }
            var busy = Between(editor, "private async Task BusyWhile(", "private async Task SaveAsNewCore(");
            Check("replacing the original runs busy from start to end",
                Regex.IsMatch(editor, @"void\s+ReplaceOriginal\(\)\s*=>\s*await\s+BusyWhile\(") &&
                Regex.IsMatch(editor, @"void\s+SaveAsNew\(\)\s*=>\s*await\s+BusyWhile\(") &&
                busy != null && busy.Contains("_busy = true") && busy.Contains("finally"));
            var render = Between(editor, "private async Task<string?> Render(", "// ---------------------------------------------------------- closing");
            Check("and the render puts busy back as it found it rather than clearing it",
                render != null && !Regex.IsMatch(render, @"_busy\s*=\s*false"));

            // A file that would not open says so on every key, not "still opening".
            var keys = Between(editor, "protected override bool ProcessCmdKey(", "private static bool IsEditorKey(");
            Check("a failed open is what the keys say afterwards",
                keys != null && keys.Contains("_openFailure") && editor.Contains("_openFailure = _lastMessage"));
        }

        private static string? Between(string text, string start, string end)
        {
            int a = text.IndexOf(start, StringComparison.Ordinal);
            if (a < 0) return null;
            int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
            return b < 0 ? null : text[a..b];
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? SourceFiles.Read(path) : null;
        }
    }

    // ---- merged from TransferFixTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

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
            await Guarded("a cancelled overwrite", OverwriteKeepsOriginalTests);
            await Guarded("fill gaps in the managed engine", ManagedFillGapsTests);
            await Guarded("a link inside a folder being moved", NestedLinkMoveTests);

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
        /// alone, so its half-written file stayed, dated now, and every later
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
        /// on the letter, or the source being a cloud copy at all, must go the
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
            Equal("retries: a copy out of Drive tries twice, then fetches the file straight from Google", "2/5",
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

            // A failure cleared by the same name copying again, in another folder
            // by bare name and in this one by full path, through the index.
            var again = RoboCopyEngine.ParseLog(new[]
            {
                "2026/10/11 10:00:00 ERROR 32 (0x00000020) Copying File C:\\s\\one\\cover.jpg",
                "The process cannot access the file because it is being used by another process.",
                "2026/10/11 10:00:00 ERROR 32 (0x00000020) Copying File C:\\s\\two\\cover.jpg",
                "The process cannot access the file because it is being used by another process.",
                "\t    New File  \t\t     100\tC:\\s\\one\\cover.jpg",
            });
            Equal("log: a full path clears only that file", "C:\\s\\two\\cover.jpg",
                string.Join(" | ", again.FileErrors.Keys));
        }

        /// <summary>
        /// Overwrite, stopped part way. The managed engine opened the file it was
        /// replacing FileMode.Create, so the original was emptied before the first
        /// byte, and the cleanup then deleted what was left: a cancelled overwrite
        /// cost the file it was meant to replace.
        /// </summary>
        private static async Task OverwriteKeepsOriginalTests()
        {
            var root = NewDir("overwrite");
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);
                var source = Path.Combine(src, "big.bin");
                var target = Path.Combine(dst, "big.bin");
                MakeFile(source, 64, 7);
                File.WriteAllText(target, "THE ORIGINAL");

                using var cts = new CancellationTokenSource();
                var stopAtFirst = new CancelOnReport<FileOpProgress>(cts);
                var stopped = await FileOperations.RunAsync(new[] { source }, dst, false, PasteConflictPolicy.Overwrite,
                    1, 64, stopAtFirst, null, cts.Token);

                Check("overwrite: the cancel was a cancel", stopped.Cancelled,
                    $"succeeded={stopped.Succeeded} failed={stopped.Failed}");
                Check("overwrite: a cancelled replace leaves the original exactly as it was",
                    FileThere(target) && Text(target) == "THE ORIGINAL",
                    FileThere(target) ? $"{new FileInfo(target).Length} bytes" : "the file is gone");
                Check("overwrite: and nothing temporary is left beside it",
                    Directory.GetFiles(dst).Length == 1, string.Join(", ", Directory.GetFiles(dst).Select(Path.GetFileName)));

                // A read-only original is replaced too, once the copy is whole.
                File.SetAttributes(target, FileAttributes.ReadOnly);
                var done = await FileOperations.RunAsync(new[] { source }, dst, false, PasteConflictPolicy.Overwrite,
                    1, 1024, null, null, CancellationToken.None);
                Check("overwrite: run to the end, the file is replaced", SameBytes(source, target),
                    string.Join(" | ", done.Errors));
                Check("overwrite: still with nothing temporary beside it", Directory.GetFiles(dst).Length == 1);
            }
            finally { Remove(root); }
        }

        /// <summary>Cancels on the first report, on the thread that makes it.</summary>
        private sealed class CancelOnReport<T> : IProgress<T>
        {
            private readonly CancellationTokenSource _cts;
            public CancelOnReport(CancellationTokenSource cts) => _cts = cts;
            public void Report(T value) => _cts.Cancel();
        }

        /// <summary>
        /// Fill gaps through the managed engine, the one the Ask dialog's "Fill
        /// gaps" answer reaches, and any paste the window hands it. It fell into
        /// keep both and made "F (2)" with everything copied a second time.
        /// </summary>
        private static async Task ManagedFillGapsTests()
        {
            var root = NewDir("mgaps");
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                Directory.CreateDirectory(Path.Combine(src, "F", "deep"));
                Directory.CreateDirectory(Path.Combine(dst, "F", "deep"));
                File.WriteAllText(Path.Combine(src, "F", "a.txt"), "new a");
                File.WriteAllText(Path.Combine(src, "F", "b.txt"), "new b");
                File.WriteAllText(Path.Combine(src, "F", "deep", "c.txt"), "new c");
                File.WriteAllText(Path.Combine(src, "loose.txt"), "new loose");
                File.WriteAllText(Path.Combine(dst, "F", "a.txt"), "OLD A");
                File.WriteAllText(Path.Combine(dst, "F", "deep", "c.txt"), "OLD C");
                File.WriteAllText(Path.Combine(dst, "loose.txt"), "OLD LOOSE");

                var result = await FileOperations.RunAsync(
                    new[] { Path.Combine(src, "F"), Path.Combine(src, "loose.txt") }, dst, false,
                    PasteConflictPolicy.FillGaps, 4, 64, null, null, CancellationToken.None);

                Check("managed fill gaps: no failures", result.Failed == 0, string.Join(" | ", result.Errors));
                Check("managed fill gaps: the folder is merged into, not copied beside",
                    !FolderThere(Path.Combine(dst, "F (2)")) && !FileThere(Path.Combine(dst, "loose (2).txt")),
                    string.Join(", ", Directory.GetFileSystemEntries(dst).Select(Path.GetFileName)));
                Check("managed fill gaps: what was missing arrives",
                    FileThere(Path.Combine(dst, "F", "b.txt")) && Text(Path.Combine(dst, "F", "b.txt")) == "new b");
                Check("managed fill gaps: what was there is left alone, at any depth",
                    Text(Path.Combine(dst, "F", "a.txt")) == "OLD A" &&
                    Text(Path.Combine(dst, "F", "deep", "c.txt")) == "OLD C" &&
                    Text(Path.Combine(dst, "loose.txt")) == "OLD LOOSE");
                Equal("managed fill gaps: one copied", "1", result.Succeeded.ToString());

                // The Ask dialog's answer goes the same way.
                File.Delete(Path.Combine(dst, "F", "b.txt"));
                var asked = await FileOperations.RunAsync(new[] { Path.Combine(src, "F") }, dst, false,
                    PasteConflictPolicy.Ask, 4, 64, null, _ => FileOperations.ConflictChoice.FillGaps,
                    CancellationToken.None);
                Check("managed fill gaps: answered from Ask, it merges too",
                    !FolderThere(Path.Combine(dst, "F (2)")) && FileThere(Path.Combine(dst, "F", "b.txt")) &&
                    Text(Path.Combine(dst, "F", "a.txt")) == "OLD A",
                    string.Join(" | ", asked.Errors));
            }
            finally { Remove(root); }
        }

        /// <summary>
        /// A folder moved file by file, here because the destination already
        /// has one of its name, with a junction inside. The walk skipped the
        /// junction and nothing after it looked, so the move left it in the
        /// source and said "Move complete".
        /// </summary>
        private static async Task NestedLinkMoveTests()
        {
            var root = NewDir("nested");
            try
            {
                var src = Path.Combine(root, "src");
                var dst = Path.Combine(root, "dst");
                var elsewhere = Path.Combine(root, "elsewhere");
                Directory.CreateDirectory(Path.Combine(src, "F"));
                Directory.CreateDirectory(Path.Combine(dst, "F"));
                Directory.CreateDirectory(elsewhere);
                File.WriteAllText(Path.Combine(elsewhere, "kept.txt"), "KEPT");
                File.WriteAllText(Path.Combine(src, "F", "a.txt"), "a");
                Junction(Path.Combine(src, "F", "J"), elsewhere);
                if (!Directory.Exists(Path.Combine(src, "F", "J")))
                {
                    Console.WriteLine("        (no junction could be made here; skipped)");
                    return;
                }

                var result = await FileOperations.RunAsync(new[] { Path.Combine(src, "F") }, dst, true,
                    PasteConflictPolicy.Overwrite, 4, 64, null, null, CancellationToken.None);

                var moved = new DirectoryInfo(Path.Combine(dst, "F", "J"));
                Check("nested link: it arrives as a link", moved.Exists && moved.LinkTarget != null,
                    string.Join(" | ", result.Errors));
                Check("nested link: the source folder is gone", !Directory.Exists(Path.Combine(src, "F")),
                    string.Join(" | ", result.Errors));
                Check("nested link: and what it pointed at is untouched",
                    File.Exists(Path.Combine(elsewhere, "kept.txt")));
                Check("nested link: no failures", result.Failed == 0, string.Join(" | ", result.Errors));
            }
            finally
            {
                // The junctions first, so nothing is deleted through them.
                foreach (var j in new[] { Path.Combine(root, "src", "F", "J"), Path.Combine(root, "dst", "F", "J") })
                    try { if (Directory.Exists(j)) Directory.Delete(j, false); } catch { }
                Remove(root);
            }
        }
    }

    // ---- merged from Perf522OpsTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// CPU and battery costs of big operations and the idle window (5.2.2).
    /// Each test pins a cost that was measured, not a guess.
    /// </summary>
    internal static class Perf522OpsTests
    {
        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            await ForgetCostsNoSearchPerEvent(check);
            await RenamedMoveReportsAtMostTenASecond(check);
        }

        /// <summary>Counts reports where they are made, with no window or thread hop.</summary>
        private sealed class CountingProgress : IProgress<TransferProgress>
        {
            public int Reports;
            public TransferProgress? Last;
            public void Report(TransferProgress value) { Interlocked.Increment(ref Reports); Last = value; }
        }

        /// <summary>
        /// A same-volume move of a big selection is one rename per item, and it
        /// reported every one: 50,000 selected files were 50,000 posts to the
        /// progress window. Now at most ten a second, with the last always said.
        /// The sources renamed away are taken out of the list in one pass, not
        /// one Remove (a shift of the whole list) per rename. And the volume
        /// question (about a millisecond of calls to Windows) is asked once per
        /// source folder, and where a file really is only for a folder.
        /// </summary>
        private static async Task RenamedMoveReportsAtMostTenASecond(Action<string, bool, string?> check)
        {
            var dir = Path.Combine(Path.GetTempPath(), "EN-perf522m-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                const int Files = 20_000;
                var from = Path.Combine(dir, "from");
                var to = Path.Combine(dir, "to");
                Directory.CreateDirectory(from);
                Directory.CreateDirectory(to);
                var sources = new List<string>(Files);
                for (int i = 0; i < Files; i++)
                {
                    var path = Path.Combine(from, $"photo {i:D5}.jpg");
                    File.WriteAllBytes(path, new byte[1]);
                    sources.Add(path);
                }

                // The per-folder answer is the answer SameVolume gives, asked once.
                var answers = new RoboCopyEngine.VolumeAnswers(to);
                check("Perf522 move: the volume answer per folder agrees with asking each file",
                    answers.SameAs(sources[0]) == RoboCopyEngine.SameVolume(sources[0], to) &&
                    answers.SameAs(sources[1]) == RoboCopyEngine.SameVolume(sources[1], to) &&
                    !answers.SameAs(@"Q:\nowhere\x.jpg"), null);

                var counting = new CountingProgress();
                var clock = Stopwatch.StartNew();
                var result = await RoboCopyEngine.RunAsync(sources, to, move: true, threads: 8,
                    PasteConflictPolicy.AutoRename, counting, CancellationToken.None);
                clock.Stop();
                Console.WriteLine($"  Perf522 move: {Files} renames took {clock.ElapsedMilliseconds}ms, {counting.Reports} reports");

                int landed = Directory.GetFiles(to).Length;
                check("Perf522 move: every file was moved", landed == Files && result.Failed == 0,
                    $"landed {landed}, failed {result.Failed}");
                check("Perf522 move: a renamed selection reports at most ten a second, not once per file",
                    counting.Reports <= 10 * (clock.ElapsedMilliseconds / 1000 + 1) + 5,
                    $"{counting.Reports} reports in {clock.ElapsedMilliseconds}ms");
                check("Perf522 move: the last report counts every file",
                    counting.Last is { } last && last.ItemsDone == Files, counting.Last?.ToString());
                // 36s here before: the volume asked of Windows for every file,
                // and where both paths really are asked for every file too.
                check("Perf522 move: 20,000 files renamed in under 20 seconds",
                    clock.ElapsedMilliseconds < 20_000, $"{clock.ElapsedMilliseconds}ms");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// The panes' size watcher watches everything below the folder on screen
        /// and calls Forget for every event, three or so per file. Forget used to
        /// search every cached size each time, so a 100,000-file paste into an
        /// open folder, after browsing a library with thousands of measured
        /// folders, was hundreds of millions of path comparisons. Now a change
        /// looks up only the folders above it, and searches for folders inside
        /// it only when something measured sits under it.
        /// </summary>
        private static async Task ForgetCostsNoSearchPerEvent(Action<string, bool, string?> check)
        {
            var dir = Path.Combine(Path.GetTempPath(), "EN-perf522-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                const int Folders = 1500;
                Directory.CreateDirectory(dir);
                for (int i = 0; i < Folders; i++)
                {
                    var sub = Path.Combine(dir, $"Artist {i:D4}");
                    Directory.CreateDirectory(sub);
                    File.WriteAllBytes(Path.Combine(sub, "a.flac"), new byte[10]);
                }

                using var sizes = new FolderSizeCalculator();
                for (int i = 0; i < Folders; i++)
                    await sizes.CalculateAsync(Path.Combine(dir, $"Artist {i:D4}"));
                var first = Path.Combine(dir, "Artist 0000");
                check("Perf522 sizes: measured folders are cached", sizes.TryGetCached(first, out _), null);

                // A paste of new files into the open folder, as the watcher reports it.
                const int Events = 60_000;
                var landing = Path.Combine(dir, "Pasted");
                var clock = Stopwatch.StartNew();
                for (int i = 0; i < Events; i++)
                    sizes.Forget(Path.Combine(landing, $"file {i}.bin"));
                clock.Stop();
                Console.WriteLine($"  Perf522 sizes: {Events} changes with {Folders} cached folders took {clock.ElapsedMilliseconds}ms");
                check("Perf522 sizes: a change searches no cache it cannot touch (60,000 changes under 1.5s)",
                    clock.ElapsedMilliseconds < 1500, $"{clock.ElapsedMilliseconds}ms");
                check("Perf522 sizes: new files beside measured folders leave their sizes alone",
                    sizes.TryGetCached(first, out _) && sizes.TryGetCached(Path.Combine(dir, $"Artist {Folders - 1:D4}"), out _), null);

                // Still right: above, itself, inside.
                var deep = Path.Combine(dir, "Box", "Inner", "Deep");
                Directory.CreateDirectory(deep);
                File.WriteAllBytes(Path.Combine(deep, "x.bin"), new byte[5]);
                var box = Path.Combine(dir, "Box");
                await sizes.CalculateAsync(box);
                await sizes.CalculateAsync(deep);
                sizes.Forget(Path.Combine(deep, "y.bin"));
                check("Perf522 sizes: a change still forgets every folder above it",
                    !sizes.TryGetCached(box, out _) && !sizes.TryGetCached(deep, out _), null);

                await sizes.CalculateAsync(deep);
                sizes.Forget(Path.Combine(dir, "Box", "Inner"));
                check("Perf522 sizes: a folder renamed or deleted still forgets what is inside it",
                    !sizes.TryGetCached(deep, out _), null);

                await sizes.CalculateAsync(deep);
                sizes.Forget(dir + "\\");
                check("Perf522 sizes: a change with a trailing slash still forgets what is inside",
                    !sizes.TryGetCached(deep, out _) && !sizes.TryGetCached(first, out _), null);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    // ---- merged from DeleteTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// Deleting: the permanent delete that replaced the VB helper, and the shape
    /// of the batched Recycle Bin delete.
    ///
    /// Nothing here sends anything to the real Recycle Bin. The permanent half is
    /// tested against real files, because what it promises (read-only goes, a
    /// junction is never followed, odd names and long paths work) is about the
    /// filesystem and nothing else.
    /// </summary>
    internal static class DeleteTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            Console.WriteLine("Deleting:");

            var top = Path.Combine(Path.GetTempPath(), "en-delete-tests-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(top);
            try
            {
                ReadOnly(top);
                Links(top);
                LongPaths(top);
                DotsAndSpaces(top);
                Failures(top);
            }
            catch (Exception ex) { Check("the delete tests ran to the end", false, ex.ToString()); }
            finally
            {
                try { ShellDelete.DeletePermanently(top); } catch { }
            }

            Batching();
            Console.WriteLine();
        }

        private static string L(string path) => @"\\?\" + path;

        private static bool Gone(string path) => !File.Exists(L(path)) && !Directory.Exists(L(path));

        private static void Try(string what, string path)
        {
            try { ShellDelete.DeletePermanently(path); Check(what, Gone(path)); }
            catch (Exception ex) { Check(what, false, ex.GetType().Name + ": " + ex.Message); }
        }

        private static void ReadOnly(string top)
        {
            var tree = Path.Combine(top, "ro");
            Directory.CreateDirectory(Path.Combine(tree, "sub"));
            File.WriteAllText(Path.Combine(tree, "a.txt"), "x");
            File.WriteAllText(Path.Combine(tree, "sub", "b.txt"), "x");
            File.SetAttributes(Path.Combine(tree, "a.txt"), FileAttributes.ReadOnly);
            File.SetAttributes(Path.Combine(tree, "sub", "b.txt"), FileAttributes.ReadOnly | FileAttributes.Hidden);
            new DirectoryInfo(Path.Combine(tree, "sub")).Attributes |= FileAttributes.ReadOnly;
            new DirectoryInfo(tree).Attributes |= FileAttributes.ReadOnly;
            Try("a folder of read-only and hidden files, itself read-only, is deleted", tree);

            var file = Path.Combine(top, "lonely.txt");
            File.WriteAllText(file, "x");
            File.SetAttributes(file, FileAttributes.ReadOnly);
            Try("a read-only file on its own is deleted", file);
        }

        private static void Junction(string link, string target)
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
        }

        private static void Links(string top)
        {
            var outside = Path.Combine(top, "outside");
            Directory.CreateDirectory(outside);
            var keep = Path.Combine(outside, "keep.txt");
            File.WriteAllText(keep, "precious");
            File.SetAttributes(keep, FileAttributes.ReadOnly);
            bool Intact() => File.Exists(keep) && File.ReadAllText(keep) == "precious" &&
                             (File.GetAttributes(keep) & FileAttributes.ReadOnly) != 0;

            // Directory.Delete meeting a junction asks for it to be unmounted
            // first, which unelevated is refused: the delete has to cope with that.
            var tree = Path.Combine(top, "withlink");
            Directory.CreateDirectory(Path.Combine(tree, "inner"));
            File.WriteAllText(Path.Combine(tree, "inner", "x.txt"), "x");
            Junction(Path.Combine(tree, "inner", "j"), outside);
            Check("(a junction to a folder outside the tree was made)", File.Exists(Path.Combine(tree, "inner", "j", "keep.txt")));
            Try("a tree with a junction in it is deleted", tree);
            Check("and what the junction pointed at is untouched", Intact());

            var both = Path.Combine(top, "withlink-ro");
            Directory.CreateDirectory(both);
            File.WriteAllText(Path.Combine(both, "r.txt"), "x");
            File.SetAttributes(Path.Combine(both, "r.txt"), FileAttributes.ReadOnly);
            Junction(Path.Combine(both, "j"), outside);
            Try("a tree with a junction and a read-only file is deleted", both);
            Check("and the clearing of read-only did not reach through the junction", Intact());

            var lone = Path.Combine(top, "lonejunction");
            Junction(lone, outside);
            Try("a junction chosen on its own is removed", lone);
            Check("and its target is untouched", Intact());
        }

        private static void LongPaths(string top)
        {
            var root = Path.Combine(top, "long");
            var deep = root;
            for (int i = 0; i < 8; i++) deep = Path.Combine(deep, new string((char)('a' + i), 40));
            Directory.CreateDirectory(L(deep));
            File.WriteAllText(L(Path.Combine(deep, "deep.txt")), "x");
            File.SetAttributes(L(Path.Combine(deep, "deep.txt")), FileAttributes.ReadOnly);
            Check("(the path is past 260 characters)", Path.Combine(deep, "deep.txt").Length > 300);
            Try("a tree with a path past 260 characters is deleted", root);

            Directory.CreateDirectory(L(deep));
            File.WriteAllText(L(Path.Combine(deep, "deep.txt")), "x");
            Try("a file at the end of such a path, chosen by itself, is deleted", Path.Combine(deep, "deep.txt"));
            try { ShellDelete.DeletePermanently(root); } catch { }
        }

        private static void DotsAndSpaces(string top)
        {
            var dots = Path.Combine(top, "dots");
            Directory.CreateDirectory(dots);
            Directory.CreateDirectory(L(Path.Combine(dots, "x.", "inner ")));
            File.WriteAllText(L(Path.Combine(dots, "x.", "in.txt")), "x");
            File.WriteAllText(L(Path.Combine(dots, "x.", "inner ", "y.")), "x");
            Directory.CreateDirectory(Path.Combine(dots, "x"));
            File.WriteAllText(Path.Combine(dots, "x", "sibling.txt"), "keep");
            Try("a folder called \"x.\" holding names ending in dots and spaces is deleted", Path.Combine(dots, "x."));
            Check("and the folder \"x\" beside it is not", File.Exists(Path.Combine(dots, "x", "sibling.txt")));

            File.WriteAllText(L(Path.Combine(dots, "report.")), "dot");
            File.WriteAllText(Path.Combine(dots, "report"), "plain");
            Try("a file called \"report.\" is deleted", Path.Combine(dots, "report."));
            Check("and \"report\" beside it is not",
                File.Exists(Path.Combine(dots, "report")) && File.ReadAllText(Path.Combine(dots, "report")) == "plain");

            // Inside a folder whose own name ends in a dot, every path below it
            // needs the exact form too, not only the last name.
            Directory.CreateDirectory(L(Path.Combine(dots, "fold.", "child")));
            File.WriteAllText(L(Path.Combine(dots, "fold.", "child", "z.txt")), "x");
            Directory.CreateDirectory(Path.Combine(dots, "fold", "child"));
            File.WriteAllText(Path.Combine(dots, "fold", "child", "z.txt"), "keep");
            Try("a folder inside \"fold.\" is deleted", Path.Combine(dots, "fold.", "child"));
            Check("and the one inside \"fold\" is not", File.Exists(Path.Combine(dots, "fold", "child", "z.txt")));
        }

        private static void Failures(string top)
        {
            var locked = Path.Combine(top, "locked");
            Directory.CreateDirectory(locked);
            File.WriteAllText(Path.Combine(locked, "open.txt"), "x");
            using (new FileStream(Path.Combine(locked, "open.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { ShellDelete.DeletePermanently(locked); Check("a file held open stops the delete", false); }
                catch (Exception ex) { Check("a file held open stops the delete, with its reason", ex is IOException && ex.Message.Length > 0, ex.Message); }
            }
            Try("and once it is closed the delete goes through", locked);

            try { ShellDelete.DeletePermanently(Path.Combine(top, "nothing-here")); Check("something not there is an error", false); }
            catch (Exception ex) { Check("something not there is an error", ex is FileNotFoundException || ex is DirectoryNotFoundException, ex.Message); }
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? SourceFiles.Read(path) : null;
        }

        private static void Batching()
        {
            var empty = ShellDelete.RecycleMany(Array.Empty<string>(), IntPtr.Zero);
            Check("recycling nothing asks the shell nothing", !empty.Declined && empty.Error == 0);

            var main = Source("MainForm.cs");
            var shell = Source("ShellDelete.cs");
            if (main == null || shell == null) { Check("the delete source can be read", false); return; }

            int start = main.IndexOf("private async void DeleteSelected(", StringComparison.Ordinal);
            int end = start < 0 ? -1 : main.IndexOf("private ", start + 30, StringComparison.Ordinal);
            var delete = start < 0 || end < 0 ? "" : main[start..end];
            Check("the window's delete queues recycles and sends them in one go",
                delete.Contains("toRecycle.Add(path);", StringComparison.Ordinal) &&
                delete.Contains("RecycleAll(toRecycle", StringComparison.Ordinal) &&
                !delete.Contains("ShellDelete.Recycle(", StringComparison.Ordinal));
            Check("and deletes for good through Directory.Delete, not the VB helper",
                delete.Contains("ShellDelete.DeletePermanently(path)", StringComparison.Ordinal) &&
                !main.Contains("FileSystem.DeleteDirectory", StringComparison.Ordinal) &&
                !main.Contains("FileSystem.DeleteFile", StringComparison.Ordinal));
            Check("the dot and space refusal is still there",
                delete.Contains("Windows cannot put a name ending in a dot", StringComparison.Ordinal));

            int batch = shell.IndexOf("public static BatchOutcome RecycleMany", StringComparison.Ordinal);
            int after = batch < 0 ? -1 : shell.IndexOf("public static void DeletePermanently", batch, StringComparison.Ordinal);
            var many = batch < 0 || after < 0 ? "" : shell[batch..after];
            Check("the batch still asks before anything is deleted for good",
                many.Contains("FOF_WANTNUKEWARNING", StringComparison.Ordinal) &&
                many.Contains("FOF_ALLOWUNDO", StringComparison.Ordinal));

            int all = main.IndexOf("private static int RecycleAll(", StringComparison.Ordinal);
            var recycleAll = all < 0 ? "" : main.Substring(all, Math.Min(2000, main.Length - all));
            Check("what the shell kept is found by looking, and a No keeps it",
                recycleAll.Contains("if (!Directory.Exists(path) && !File.Exists(path)) { done++; continue; }", StringComparison.Ordinal) &&
                recycleAll.Contains("batch.Declined", StringComparison.Ordinal));
        }
    }
}
