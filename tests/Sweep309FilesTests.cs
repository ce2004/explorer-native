using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
}
