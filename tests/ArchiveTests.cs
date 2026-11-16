using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System;

namespace ExplorerNative
{
    /// <summary>
    /// Compressing and extracting, checked against something other than itself.
    ///
    /// The trap with an archiver is that a writer and a reader written by the
    /// same hand agree with each other about whatever they both get wrong. A
    /// round trip through our own code proves the two halves are consistent and
    /// proves nothing about whether the file is a zip. So every format written
    /// here is also handed to the bsdtar Windows ships, a different
    /// implementation, by different people, that has no idea this application
    /// exists, and the zips are also opened with ZipArchive, which is
    /// the reader everybody else's .NET code would use on them.
    /// </summary>
    internal static class ArchiveTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            ChecksumTests();
            FormatTableTests();
            PathSafetyTests();
            GZipMemberTests();
            await RoundTripTests();
            await OtherToolsAgreeTests();
            await ConflictTests();
            await CancelTests();
            await EdgeShapeTests();
            await ProgressMovesTests();
            ProgressWindowChainTests();
            ExtractionRenameTests();
            CompressWindowTests();
            SystemTarOptionTests();
            CommandWiringTests();
            await ArchiveFixTests.RunAll(check);
            await ArchiveSpeedTests.RunAll(check);
        }

        // ---------- CRC-32 ----------

        /// <summary>
        /// Against the published vectors, not against a second copy of the same
        /// arithmetic.
        ///
        /// A checksum is the one part of this that a round trip cannot test: an
        /// archive written with a wrong CRC and read with the same wrong CRC
        /// verifies perfectly, and fails the first time anybody else opens it.
        /// </summary>
        private static void ChecksumTests()
        {
            Equal("CRC-32 of nothing is zero", "00000000", Crc32.Append(Crc32.Seed, Array.Empty<byte>()).ToString("x8"));

            Equal("CRC-32 of \"123456789\" is the standard check value",
                "cbf43926", Crc32.Append(Crc32.Seed, Encoding.ASCII.GetBytes("123456789")).ToString("x8"));

            Equal("CRC-32 of the quick brown fox",
                "414fa339",
                Crc32.Append(Crc32.Seed, Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog")).ToString("x8"));

            // Every slice length around the eight-byte boundary, because the fast
            // path takes eight at a time and the tail loop takes the rest, and an
            // off-by-one between them is invisible on any input that is a multiple
            // of eight.
            var data = new byte[64];
            new Random(7).NextBytes(data);

            bool everyLength = true;
            for (int length = 0; length <= data.Length; length++)
            {
                uint whole = Crc32.Append(Crc32.Seed, data.AsSpan(0, length));

                uint piecemeal = Crc32.Seed;
                for (int i = 0; i < length; i++) piecemeal = Crc32.Append(piecemeal, data.AsSpan(i, 1));

                if (whole != piecemeal) everyLength = false;
            }
            Check("a CRC fed eight at a time matches one fed a byte at a time, at every length", everyLength);
        }

        // ---------- The format table ----------

        private static void FormatTableTests()
        {
            Equal("a .tar.gz is a gzipped tar, not a gzip",
                "tar.gz", ArchiveFormats.FromName("album.tar.gz")?.Id ?? "(none)");

            Equal("and a .tgz is the same thing",
                "tar.gz", ArchiveFormats.FromName("album.tgz")?.Id ?? "(none)");

            Equal("a bare .gz is a gzip", "gz", ArchiveFormats.FromName("song.flac.gz")?.Id ?? "(none)");
            Equal("a .tar.xz is not an .xz", "tar.xz", ArchiveFormats.FromName("src.tar.xz")?.Id ?? "(none)");
            Equal("a .7z is a 7-Zip", "7z", ArchiveFormats.FromName("backup.7z")?.Id ?? "(none)");

            Check("a name that is only an extension is not an archive",
                ArchiveFormats.FromName(".zip") == null);

            Check("something with no extension is not an archive by name",
                ArchiveFormats.FromName("README") == null);

            Equal("the extension comes off a gzipped tar in one piece",
                "album", ArchiveFormats.BaseName(@"C:\music\album.tar.gz"));

            Equal("and off a zip", "photos", ArchiveFormats.BaseName(@"C:\x\photos.zip"));

            Equal("something unrecognised keeps everything before the last dot",
                "notes", ArchiveFormats.BaseName(@"C:\x\notes.txt"));

            // The bytes, for the cases the name cannot answer.
            Equal("PK at the front is a zip", "zip",
                ArchiveFormats.FromBytes(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0 })?.Id ?? "(none)");

            Equal("and the 7z signature is a 7z", "7z",
                ArchiveFormats.FromBytes(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C })?.Id ?? "(none)");

            var ustar = new byte[512];
            Encoding.ASCII.GetBytes("ustar").CopyTo(ustar, 257);
            Equal("a tar is recognised from the magic 257 bytes in", "tar",
                ArchiveFormats.FromBytes(ustar)?.Id ?? "(none)");

            Check("nothing recognisable is nothing", ArchiveFormats.FromBytes(new byte[] { 1, 2, 3, 4 }) == null);

            // The naming rules the Compress dialog starts from.
            Equal("one folder names the archive after itself",
                "photos.zip", ArchiveFormats.SuggestedName(new[] { @"C:\x\photos" }, ArchiveFormats.Zip));

            Equal("one file loses its extension",
                "notes.zip", ArchiveFormats.SuggestedName(new[] { @"C:\x\notes.txt" }, ArchiveFormats.Zip));

            Equal("unless the format wraps one file, where the extension is the only record of what it was",
                "notes.txt.gz", ArchiveFormats.SuggestedName(new[] { @"C:\x\notes.txt" }, ArchiveFormats.Gz));

            Equal("several are named after the folder they are in",
                "x.zip", ArchiveFormats.SuggestedName(new[] { @"C:\x\a.txt", @"C:\x\b.txt" }, ArchiveFormats.Zip));

            Equal("and a folder whose name merely looks like it has an extension keeps it",
                "Season 1.5.zip", ArchiveFormats.SuggestedName(new[] { @"C:\x\Season 1.5" }, ArchiveFormats.Zip));

            Check("the bare compressors that Windows has no reader for are offered for neither",
                !ArchiveFormats.Xz.CanCreate && !ArchiveFormats.Xz.CanExtract &&
                !ArchiveFormats.Bz2.CanCreate && !ArchiveFormats.Bz2.CanExtract &&
                !ArchiveFormats.Zst.CanCreate && !ArchiveFormats.Zst.CanExtract);

            Check("but their tarred forms are offered for both",
                ArchiveFormats.TarXz.CanCreate && ArchiveFormats.TarBz2.CanCreate && ArchiveFormats.TarZst.CanCreate);

            Check("read-only formats are never offered as somewhere to compress to",
                ArchiveFormats.Creatable(true).All(f => f.CanCreate) &&
                !ArchiveFormats.Creatable(true).Contains(ArchiveFormats.Rar));

            Check("a single-file format is only offered when one file was chosen",
                !ArchiveFormats.Creatable(singleFile: false).Any(f => f.SingleFile) &&
                ArchiveFormats.Creatable(singleFile: true).Any(f => f.SingleFile));

            var ids = ArchiveFormats.All.Select(f => f.Id).ToList();
            Check("no two formats share an id", ids.Count == ids.Distinct().Count());

            Check("every extension in the table starts with a dot",
                ArchiveFormats.All.SelectMany(f => f.Extensions).All(e => e.StartsWith('.')));

            Check("every format's own primary extension is one it answers to",
                ArchiveFormats.All.All(f => f.Extensions.Contains(f.Extension)));
        }

        // ---------- Where an entry is allowed to land ----------

        /// <summary>
        /// The check that stops an archive writing outside the folder it is being
        /// extracted into.
        ///
        /// One line of archive mounts it and the payload is a legal string in both
        /// zip and tar, so neither format refuses it and the extractor is the only
        /// thing standing there. Tested from both ends: the escapes are refused
        /// and the ordinary names are not, because a check that refuses everything
        /// passes the first half of this and breaks extraction entirely.
        /// </summary>
        private static void PathSafetyTests()
        {
            var root = Path.Combine(Path.GetTempPath(), "en-safe-root");

            string? Refuse(string name)
            {
                return ArchiveEngine.SafeTarget(root, name, out _);
            }

            Check("a parent-relative name is refused", Refuse("../evil.txt") == null);
            Check("a deeply parent-relative name is refused", Refuse("a/b/../../../../evil.txt") == null);
            Check("a backslash parent-relative name is refused too", Refuse(@"..\evil.txt") == null);
            Check("an absolute Windows path is refused", Refuse(@"C:\Windows\System32\evil.dll") == null);
            Check("a drive-relative path is refused", Refuse("C:evil.txt") == null);
            Check("a UNC path is refused", Refuse(@"\\server\share\evil.txt") == null);
            Check("a lone dot is refused", Refuse(".") == null);
            Check("a reserved device name is refused", Refuse("docs/CON") == null);
            Check("a name ending in a dot is refused", Refuse("docs/report.") == null);
            Check("an empty name is refused", Refuse("") == null);

            var ok = ArchiveEngine.SafeTarget(root, "docs/2019/report.txt", out _);
            Check("an ordinary nested name is allowed", ok != null &&
                ok.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                ok.EndsWith(Path.Combine("docs", "2019", "report.txt"), StringComparison.OrdinalIgnoreCase));

            var unicode = ArchiveEngine.SafeTarget(root, "音楽/日本語 🎵.txt", out _);
            Check("and so is one that is not in English", unicode != null);

            var leading = ArchiveEngine.SafeTarget(root, "./notes.txt", out _);
            Check("a leading current-directory segment is refused rather than silently dropped",
                leading == null);

            ArchiveEngine.SafeTarget(root, "../evil.txt", out var why);
            Check("and the refusal says why, with the name in it",
                why != null && why.Contains("evil.txt") && why.Contains("outside"), why);
        }

        // ---------- Parallel gzip ----------

        /// <summary>
        /// The multi-member trick, checked by three different readers.
        ///
        /// This is the part of the feature most likely to produce a file that
        /// looks fine and is not, because "a series of members" is a corner of RFC
        /// 1952 that a decompressor is allowed to be lazy about. If .NET, the
        /// system tar and a byte-for-byte comparison all agree, it is a gzip.
        /// </summary>
        private static void GZipMemberTests()
        {
            var dir = NewDir("gz");
            try
            {
                // Deliberately several times the chunk size, so there are members
                // to get wrong, and not a multiple of it, so the last one is short.
                var original = new byte[ParallelGZip.ChunkBytes * 3 + 12345];
                var random = new Random(23);
                for (int i = 0; i < original.Length; i++)
                    original[i] = (byte)(i % 251 == 0 ? random.Next(256) : i % 97);

                var packed = Path.Combine(dir, "corpus.bin.gz");

                using (var source = new MemoryStream(original))
                using (var output = new FileStream(packed, FileMode.Create, FileAccess.Write))
                using (var writer = new ParallelGZip.Writer(output, 8, CompressionLevel.Optimal,
                           CancellationToken.None, leaveOpen: true))
                    source.CopyTo(writer);

                Check("the parallel writer produced something smaller than it was given",
                    new FileInfo(packed).Length < original.Length,
                    $"{new FileInfo(packed).Length} vs {original.Length}");

                using (var file = File.OpenRead(packed))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                using (var into = new MemoryStream())
                {
                    gzip.CopyTo(into);
                    Check("and .NET reads every member back, byte for byte",
                        into.ToArray().AsSpan().SequenceEqual(original));
                }

                // More than one member was written, proved from the file rather
                // than from the writer: the last four bytes of a gzip are the
                // uncompressed length of the *final member*, so a single-member
                // file ends with the whole length and a multi-member one ends
                // with the length of its tail chunk.
                //
                // This is also the limitation that comes with the trick, recorded
                // here so nobody later "fixes" it: anything reading the size of a
                // .gz without decompressing it reports the last chunk. Equally
                // true of pigz output and of two .gz files joined with copy, and
                // not true of anything that actually decompresses.
                var trailer = File.ReadAllBytes(packed);
                uint lastMemberSize = BitConverter.ToUInt32(trailer, trailer.Length - 4);

                Equal("the trailer holds the last member length, not the whole file length",
                    (original.Length % ParallelGZip.ChunkBytes).ToString(), lastMemberSize.ToString());

                // Two members concatenated by hand read as one file, which is the
                // same property from the other direction and the reason the format
                // allows this at all.
                var half = new byte[] { 1, 2, 3, 4, 5 };
                using (var into = new MemoryStream())
                {
                    for (int i = 0; i < 2; i++)
                        using (var gzip = new GZipStream(into, CompressionLevel.Fastest, leaveOpen: true))
                            gzip.Write(half, 0, half.Length);

                    into.Position = 0;
                    using var reader = new GZipStream(into, CompressionMode.Decompress);
                    using var got = new MemoryStream();
                    reader.CopyTo(got);
                    Check("two hand-concatenated members read as one stream", got.ToArray().Length == 10);
                }

            }
            finally { Cleanup(dir); }
        }

        // ---------- Round trips ----------

        /// <summary>
        /// Every format that can be created: compress a real tree, list it,
        /// extract it, and compare what came out against what went in.
        ///
        /// The corpus is chosen for the cases that have broken archivers before,
        /// an empty folder, a zero-byte file, a name that is not in English, a
        /// file whose extension says it is already compressed, and enough
        /// compressible text that the compression is doing something.
        /// </summary>
        private static async Task RoundTripTests()
        {
            foreach (var format in ArchiveFormats.All.Where(f => f.CanCreate && !f.SingleFile))
            {
                if (format.Engine == ArchiveEngineKind.BsdTar && !BsdTar.HandlesNames) continue;

                var dir = NewDir("rt");
                try
                {
                    var source = Path.Combine(dir, "in");
                    BuildCorpus(source);

                    var archive = Path.Combine(dir, "made" + format.Extension);

                    var made = await ArchiveEngine.CompressAsync(
                        new[] { Path.Combine(source, "top") }, archive, format,
                        ArchiveLevel.Balanced, 4, null, CancellationToken.None);

                    Check($"{format.Id}: compressing reported no failures", made.Failed == 0,
                        string.Join("; ", made.Errors));
                    Check($"{format.Id}: the archive exists and is not empty",
                        File.Exists(archive) && new FileInfo(archive).Length > 0);

                    if (format.Engine == ArchiveEngineKind.BsdTar)
                    {
                        // No listing for these, deliberately: see ArchiveEngine.
                        // The sizes are still readable, and the totals are what a
                        // progress window is actually about.
                        var totals = BsdTar.Totals(archive, CancellationToken.None);
                        Check($"{format.Id}: the totals count every entry",
                            totals.Items >= 7, totals.Items.ToString());
                        Check($"{format.Id}: and add up to something like what went in",
                            totals.Bytes > 100_000, totals.Bytes.ToString());
                    }
                    else
                    {
                        var listed = ListArchive(archive);
                        Check($"{format.Id}: the listing has every file in it",
                            listed.Count(e => !e.IsDirectory) == 6,
                            $"{listed.Count(e => !e.IsDirectory)} files listed");
                    }

                    var into = Path.Combine(dir, "out");
                    var back = await ArchiveEngine.ExtractAsync(archive, into,
                        PasteConflictPolicy.AutoRename, null, 4, null, CancellationToken.None);

                    Check($"{format.Id}: extracting reported no failures", back.Failed == 0,
                        string.Join("; ", back.Errors));

                    Compare(format.Id, Path.Combine(source, "top"), Path.Combine(into, "top"));
                }
                finally { Cleanup(dir); }
            }

            // The bare gzip, which has one file in it and no names at all.
            {
                var dir = NewDir("gzone");
                try
                {
                    var file = Path.Combine(dir, "notes.txt");
                    var text = string.Concat(Enumerable.Repeat("the same sentence, over and over. ", 5000));
                    File.WriteAllText(file, text);

                    var archive = Path.Combine(dir, "notes.txt.gz");
                    var made = await ArchiveEngine.CompressAsync(new[] { file }, archive,
                        ArchiveFormats.Gz, ArchiveLevel.Balanced, 4, null, CancellationToken.None);

                    Check("gz: one file went in", made.Items == 1 && made.Failed == 0);
                    Check("gz: and it got much smaller",
                        new FileInfo(archive).Length < new FileInfo(file).Length / 10);

                    var into = Path.Combine(dir, "out");
                    await ArchiveEngine.ExtractAsync(archive, into, PasteConflictPolicy.AutoRename,
                        null, 4, null, CancellationToken.None);

                    Equal("gz: what comes out is named after the archive and holds what went in",
                        text, File.ReadAllText(Path.Combine(into, "notes.txt")));
                }
                finally { Cleanup(dir); }
            }
        }

        /// <summary>
        /// The corpus, and every file in it is here because of a way an archiver
        /// can be wrong.
        /// </summary>
        private static void BuildCorpus(string root)
        {
            var top = Path.Combine(root, "top");
            Directory.CreateDirectory(top);
            Directory.CreateDirectory(Path.Combine(top, "sub"));

            // An empty folder, which a plan made only of files loses silently.
            Directory.CreateDirectory(Path.Combine(top, "empty"));

            File.WriteAllText(Path.Combine(top, "notes.txt"),
                string.Concat(Enumerable.Repeat("compressible text, repeated. ", 4000)));

            // Zero bytes: the length that divides by every buffer size and is the
            // one an off-by-one loop skips.
            File.WriteAllBytes(Path.Combine(top, "zero.dat"), Array.Empty<byte>());

            var random = new byte[120_000];
            new Random(3).NextBytes(random);
            File.WriteAllBytes(Path.Combine(top, "data.bin"), random);

            // Says it is already compressed, so the zip writer stores it instead
            // of deflating it, a different path through the writer.
            File.WriteAllBytes(Path.Combine(top, "photo.jpg"), random);

            File.WriteAllText(Path.Combine(top, "sub", "deep.txt"), "two levels down");

            // A name Windows can store and the console code page cannot print,
            // which is the case that decided bsdtar would never be asked for names.
            File.WriteAllText(Path.Combine(top, "日本語 🎵.txt"), "unicode name");
        }

        private static void Compare(string label, string expected, string actual)
        {
            var wanted = Snapshot(expected);
            var got = Snapshot(actual);

            var missing = wanted.Keys.Except(got.Keys).OrderBy(x => x).ToList();
            var extra = got.Keys.Except(wanted.Keys).OrderBy(x => x).ToList();

            Check($"{label}: every name came back", missing.Count == 0 && extra.Count == 0,
                $"missing: {string.Join(", ", missing)}; extra: {string.Join(", ", extra)}");

            var wrong = wanted.Keys.Intersect(got.Keys)
                .Where(k => !wanted[k].AsSpan().SequenceEqual(got[k]))
                .OrderBy(x => x).ToList();

            Check($"{label}: every file came back byte for byte", wrong.Count == 0,
                string.Join(", ", wrong));
        }

        /// <summary>
        /// A tree as a dictionary of relative name to contents. Folders are in it
        /// too, as an empty array under a name ending in a slash, so an empty one
        /// going missing is a difference rather than an absence of evidence.
        /// </summary>
        private static Dictionary<string, byte[]> Snapshot(string root)
        {
            var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (!Directory.Exists(root)) return map;

            foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                map[Relative(root, path) + "/"] = Array.Empty<byte>();

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                map[Relative(root, path)] = File.ReadAllBytes(path);

            return map;
        }

        private static string Relative(string root, string path) =>
            Path.GetRelativePath(root, path).Replace('\\', '/');

        // ---------- What everybody else makes of it ----------

        /// <summary>
        /// The archives this writes, opened by things that did not write them.
        ///
        /// This is the check the round trip above cannot make. A zip whose central
        /// directory disagrees with its local headers, whose CRC is computed over
        /// the compressed bytes, whose sizes are swapped, or whose UTF-8 flag is
        /// missing, round-trips perfectly through the code that made it and fails
        /// the moment it is sent to anybody.
        /// </summary>
        private static async Task OtherToolsAgreeTests()
        {
            var dir = NewDir("compat");
            try
            {
                var source = Path.Combine(dir, "in");
                BuildCorpus(source);
                var top = Path.Combine(source, "top");

                var zip = Path.Combine(dir, "ours.zip");
                await ArchiveEngine.CompressAsync(new[] { top }, zip, ArchiveFormats.Zip,
                    ArchiveLevel.Balanced, 6, null, CancellationToken.None);

                // ZipArchive is the reader anybody else's .NET would use, and it
                // verifies the CRC of every entry as it reads it.
                using (var file = File.OpenRead(zip))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    bool allRead = true;
                    string? failedOn = null;

                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith('/')) continue;
                        try
                        {
                            using var content = entry.Open();
                            using var into = new MemoryStream();
                            content.CopyTo(into);
                            if (into.Length != entry.Length) { allRead = false; failedOn = entry.FullName; }
                        }
                        catch (Exception ex) { allRead = false; failedOn = $"{entry.FullName}: {ex.Message}"; }
                    }

                    Check("ZipArchive reads every entry we wrote and every checksum agrees", allRead, failedOn);

                    Check("the empty folder is in the zip as an entry of its own",
                        archive.Entries.Any(e => e.FullName.Replace('\\', '/').EndsWith("empty/")));

                    Check("the name that is not in English survived as itself",
                        archive.Entries.Any(e => e.FullName.EndsWith("日本語 🎵.txt", StringComparison.Ordinal)));

                    var photo = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("photo.jpg"));
                    Check("a file that says it is already compressed was stored rather than deflated",
                        photo != null && photo.CompressedLength >= photo.Length,
                        photo == null ? "no entry" : $"{photo.Length} -> {photo.CompressedLength}");
                }

                if (!BsdTar.HandlesNames)
                {
                    Check("the system tar is present to check against", BsdTar.Available, "tar.exe missing");
                    return;
                }

                // And the other implementation entirely.
                Check("the system tar lists our zip without complaint",
                    RunTar(out var zipList, "-tf", zip) == 0, zipList);

                var extracted = Path.Combine(dir, "bytar");
                Directory.CreateDirectory(extracted);
                Check("and extracts it",
                    RunTar(out var zipOut, "-xf", zip, "-C", extracted) == 0, zipOut);
                Compare("zip via the system tar", top, Path.Combine(extracted, "top"));

                var targz = Path.Combine(dir, "ours.tar.gz");
                await ArchiveEngine.CompressAsync(new[] { top }, targz, ArchiveFormats.TarGz,
                    ArchiveLevel.Balanced, 6, null, CancellationToken.None);

                var untarred = Path.Combine(dir, "bytargz");
                Directory.CreateDirectory(untarred);
                Check("the system tar extracts our parallel gzip, every member of it",
                    RunTar(out var gzOut, "-xf", targz, "-C", untarred) == 0, gzOut);
                Compare("tar.gz via the system tar", top, Path.Combine(untarred, "top"));

                // The other direction: archives nothing here wrote.
                var theirTarGz = Path.Combine(dir, "theirs.tar.gz");
                Check("the system tar made a gzipped tar to read back",
                    RunTar(out var madeOut, "-a", "-c", "-f", theirTarGz, "-C", source, "top") == 0, madeOut);

                var mine = Path.Combine(dir, "fromtheirs");
                var read = await ArchiveEngine.ExtractAsync(theirTarGz, mine,
                    PasteConflictPolicy.AutoRename, null, 4, null, CancellationToken.None);
                Check("and we extract what it wrote", read.Failed == 0, string.Join("; ", read.Errors));
                Compare("their gzipped tar through our reader", top, Path.Combine(mine, "top"));

                // And a zip of theirs, on a tree with only ASCII names in it.
                //
                // Only ASCII, and that is a fact about bsdtar rather than a
                // convenience here: libarchive zip writer on Windows converts
                // names through the process code page and does not set the UTF-8
                // flag, so a zip it makes from a Japanese file name contains
                // underscores and question marks instead -- measured. Our own zip
                // writer sets the flag and stores UTF-8, which is what the round
                // trip above proves. There is nothing to fix on this side; the
                // check is scoped to what the other tool can actually express.
                var plain = Path.Combine(dir, "plain");
                Directory.CreateDirectory(Path.Combine(plain, "docs", "deep"));
                File.WriteAllText(Path.Combine(plain, "docs", "a.txt"), "one");
                File.WriteAllText(Path.Combine(plain, "docs", "deep", "b.txt"), "two");

                var theirZip = Path.Combine(dir, "theirs.zip");
                Check("the system tar made a zip to read back",
                    RunTar(out var zipMade, "-a", "-c", "-f", theirZip, "-C", plain, "docs") == 0, zipMade);

                var fromZip = Path.Combine(dir, "fromtheirzip");
                var readZip = await ArchiveEngine.ExtractAsync(theirZip, fromZip,
                    PasteConflictPolicy.AutoRename, null, 4, null, CancellationToken.None);
                Check("and we extract their zip", readZip.Failed == 0, string.Join("; ", readZip.Errors));
                Compare("their zip through our reader", Path.Combine(plain, "docs"), Path.Combine(fromZip, "docs"));
            }
            finally { Cleanup(dir); }
        }

        // ---------- Landing somewhere that is already occupied ----------

        private static async Task ConflictTests()
        {
            var dir = NewDir("clash");
            try
            {
                var source = Path.Combine(dir, "in");
                BuildCorpus(source);
                var top = Path.Combine(source, "top");

                var archive = Path.Combine(dir, "a.zip");
                await ArchiveEngine.CompressAsync(new[] { top }, archive, ArchiveFormats.Zip,
                    ArchiveLevel.Fastest, 4, null, CancellationToken.None);

                // Rename: the whole tree goes somewhere new and what was there is
                // untouched, which is what a paste does with the same answer.
                var into = Path.Combine(dir, "out");
                Directory.CreateDirectory(Path.Combine(into, "top"));
                File.WriteAllText(Path.Combine(into, "top", "mine.txt"), "was here first");

                var renamed = await ArchiveEngine.ExtractAsync(archive, into,
                    PasteConflictPolicy.AutoRename, null, 4, null, CancellationToken.None);

                Check("renaming leaves what was already there alone",
                    File.ReadAllText(Path.Combine(into, "top", "mine.txt")) == "was here first");
                Check("and puts the archive beside it under a new name",
                    Directory.Exists(Path.Combine(into, "top (2)")), string.Join(", ",
                        Directory.GetDirectories(into).Select(Path.GetFileName)));
                Check("and says that it did", renamed.Collisions.RenamedCount > 0);
                Compare("renamed tree", top, Path.Combine(into, "top (2)"));

                // Skip: nothing lands at all.
                var skipInto = Path.Combine(dir, "skip");
                Directory.CreateDirectory(Path.Combine(skipInto, "top"));
                var skipped = await ArchiveEngine.ExtractAsync(archive, skipInto,
                    PasteConflictPolicy.Skip, null, 4, null, CancellationToken.None);

                Check("skipping writes nothing into the folder that was in the way",
                    Directory.GetFileSystemEntries(Path.Combine(skipInto, "top")).Length == 0);
                Check("and counts what it skipped", skipped.Collisions.SkippedCount > 0);

                // Fill gaps: goes in, and only the missing files arrive.
                var fillInto = Path.Combine(dir, "fill");
                Directory.CreateDirectory(Path.Combine(fillInto, "top"));
                File.WriteAllText(Path.Combine(fillInto, "top", "notes.txt"), "do not replace me");

                await ArchiveEngine.ExtractAsync(archive, fillInto,
                    PasteConflictPolicy.FillGaps, null, 4, null, CancellationToken.None);

                Equal("filling gaps leaves a file that was already there",
                    "do not replace me", File.ReadAllText(Path.Combine(fillInto, "top", "notes.txt")));
                Check("and brings in the ones that were not",
                    File.Exists(Path.Combine(fillInto, "top", "data.bin")));

                // Overwrite: the file that was there is replaced and it is recorded.
                var overInto = Path.Combine(dir, "over");
                Directory.CreateDirectory(Path.Combine(overInto, "top"));
                File.WriteAllText(Path.Combine(overInto, "top", "notes.txt"), "short");

                var over = await ArchiveEngine.ExtractAsync(archive, overInto,
                    PasteConflictPolicy.Overwrite, null, 4, null, CancellationToken.None);

                Check("overwriting replaces what was there",
                    new FileInfo(Path.Combine(overInto, "top", "notes.txt")).Length > 100);
                Check("and says so, because it is the one that destroys something",
                    over.Collisions.OverwrittenCount > 0);

                // Cancel, which is not a failure.
                var askInto = Path.Combine(dir, "ask");
                Directory.CreateDirectory(Path.Combine(askInto, "top"));
                var cancelled = await ArchiveEngine.ExtractAsync(archive, askInto,
                    PasteConflictPolicy.Ask, _ => FileOperations.ConflictChoice.Cancel,
                    4, null, CancellationToken.None);

                Check("answering the question with Cancel is a cancellation, not a failure",
                    cancelled.Cancelled && cancelled.Failed == 0);
                Check("and nothing was written", Directory.GetFileSystemEntries(Path.Combine(askInto, "top")).Length == 0);

                // The question is asked about top-level names, once, not about
                // every file underneath.
                IReadOnlyList<string>? asked = null;
                var oneQuestion = Path.Combine(dir, "one");
                Directory.CreateDirectory(Path.Combine(oneQuestion, "top"));
                await ArchiveEngine.ExtractAsync(archive, oneQuestion, PasteConflictPolicy.Ask,
                    names => { asked = names; return FileOperations.ConflictChoice.Skip; },
                    4, null, CancellationToken.None);

                Check("the collision question names the folder, not the files inside it",
                    asked != null && asked.Count == 1 && asked[0] == "top",
                    asked == null ? "never asked" : string.Join(", ", asked));
            }
            finally { Cleanup(dir); }
        }

        // ---------- Stopping ----------

        private static async Task CancelTests()
        {
            var dir = NewDir("cancel");
            try
            {
                var source = Path.Combine(dir, "in");
                Directory.CreateDirectory(source);

                // Enough to still be going when the cancel arrives.
                var block = new byte[2_000_000];
                new Random(5).NextBytes(block);
                for (int i = 0; i < 40; i++)
                    File.WriteAllBytes(Path.Combine(source, $"f{i}.bin"), block);

                var archive = Path.Combine(dir, "stopped.zip");

                using var cts = new CancellationTokenSource();
                var task = ArchiveEngine.CompressAsync(
                    Directory.GetFiles(source), archive, ArchiveFormats.Zip,
                    ArchiveLevel.Smallest, 4, null, cts.Token);

                cts.CancelAfter(60);

                ArchiveResult? result = null;
                try { result = await task; }
                catch (OperationCanceledException) { /* also a legitimate answer */ }

                Check("a cancelled compress does not report success",
                    result == null || result.Cancelled, result?.ToString());

                // The half-written archive is the point. It has the right name and
                // the right extension and it opens to an error in the middle, at
                // whatever later moment somebody needs what is in it.
                Check("and it does not leave a broken archive behind", !File.Exists(archive));
            }
            finally { Cleanup(dir); }
        }

        // ---------- Shapes that have broken archivers ----------

        private static async Task EdgeShapeTests()
        {
            var dir = NewDir("edge");
            try
            {
                // Nothing at all.
                var empty = Path.Combine(dir, "empty");
                Directory.CreateDirectory(empty);
                var emptyZip = Path.Combine(dir, "empty.zip");

                var made = await ArchiveEngine.CompressAsync(new[] { empty }, emptyZip,
                    ArchiveFormats.Zip, ArchiveLevel.Balanced, 4, null, CancellationToken.None);

                Check("an empty folder compresses to an archive with one entry in it", made.Items == 1);

                using (var file = File.OpenRead(emptyZip))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
                    Check("which ZipArchive opens", archive.Entries.Count == 1);

                // The same name from two different folders, which the second pane
                // makes easy to select.
                var a = Path.Combine(dir, "a");
                var b = Path.Combine(dir, "b");
                Directory.CreateDirectory(a);
                Directory.CreateDirectory(b);
                File.WriteAllText(Path.Combine(a, "same.txt"), "from a");
                File.WriteAllText(Path.Combine(b, "same.txt"), "from b");

                var both = Path.Combine(dir, "both.zip");
                await ArchiveEngine.CompressAsync(
                    new[] { Path.Combine(a, "same.txt"), Path.Combine(b, "same.txt") },
                    both, ArchiveFormats.Zip, ArchiveLevel.Balanced, 4, null, CancellationToken.None);

                var listed = ListArchive(both);
                Check("two sources with the same name both make it into the archive",
                    listed.Count == 2 && listed.Select(e => e.Name).Distinct().Count() == 2,
                    string.Join(", ", listed.Select(e => e.Name)));

                var out2 = Path.Combine(dir, "out2");
                await ArchiveEngine.ExtractAsync(both, out2, PasteConflictPolicy.AutoRename,
                    null, 4, null, CancellationToken.None);
                Check("and both come back out", Directory.GetFiles(out2).Length == 2);

                // A file large enough to take the streamed path through the zip
                // writer instead of the in-memory one, which is different code.
                var big = Path.Combine(dir, "big");
                Directory.CreateDirectory(big);
                var large = Path.Combine(big, "large.dat");

                using (var writing = new FileStream(large, FileMode.Create, FileAccess.Write))
                {
                    var chunk = new byte[1 << 20];
                    for (int i = 0; i < chunk.Length; i++) chunk[i] = (byte)(i % 211);
                    for (int i = 0; i < 70; i++) writing.Write(chunk, 0, chunk.Length);
                }

                var bigZip = Path.Combine(dir, "big.zip");
                await ArchiveEngine.CompressAsync(new[] { big }, bigZip, ArchiveFormats.Zip,
                    ArchiveLevel.Balanced, 4, null, CancellationToken.None);

                using (var file = File.OpenRead(bigZip))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    var entry = archive.Entries.First(e => e.FullName.EndsWith("large.dat"));
                    Check("an entry too big to buffer is written with the right size",
                        entry.Length == 70L * (1 << 20), entry.Length.ToString());

                    using var content = entry.Open();
                    long counted = 0;
                    var scratch = new byte[1 << 20];
                    int got;
                    while ((got = content.Read(scratch, 0, scratch.Length)) > 0) counted += got;

                    // Reading it through ZipArchive checks the patched header and
                    // the checksum together: a wrong CRC throws here.
                    Check("and reads back in full, with the checksum the header claims",
                        counted == entry.Length, counted.ToString());
                }

                var backOut = Path.Combine(dir, "bigout");
                await ArchiveEngine.ExtractAsync(bigZip, backOut, PasteConflictPolicy.AutoRename,
                    null, 4, null, CancellationToken.None);
                Compare("streamed entry", big, Path.Combine(backOut, "big"));

                // Thread count must not change the file. It is the one property
                // that says the ordering in the pipeline is real rather than
                // accidental, and it is what makes an archive reproducible.
                var corpus = Path.Combine(dir, "corp");
                BuildCorpus(corpus);
                var one = Path.Combine(dir, "one.zip");
                var many = Path.Combine(dir, "many.zip");

                await ArchiveEngine.CompressAsync(new[] { Path.Combine(corpus, "top") }, one,
                    ArchiveFormats.Zip, ArchiveLevel.Balanced, 1, null, CancellationToken.None);
                await ArchiveEngine.CompressAsync(new[] { Path.Combine(corpus, "top") }, many,
                    ArchiveFormats.Zip, ArchiveLevel.Balanced, 16, null, CancellationToken.None);

                Check("one worker and sixteen produce the same archive, byte for byte",
                    File.ReadAllBytes(one).AsSpan().SequenceEqual(File.ReadAllBytes(many)),
                    $"{new FileInfo(one).Length} vs {new FileInfo(many).Length}");

                // Something that is not an archive.
                var nonsense = Path.Combine(dir, "notanarchive.txt");
                File.WriteAllText(nonsense, "just some words");

                bool refused = false;
                try
                {
                    await ArchiveEngine.ExtractAsync(nonsense, Path.Combine(dir, "refused"), PasteConflictPolicy.AutoRename, null, 1, null, CancellationToken.None);
                }
                catch (NotSupportedException) { refused = true; }

                Check("something that is not an archive is refused rather than half read", refused);

                // A bare .xz, which is recognised and still cannot be opened, and
                // deserves a different sentence from the one above.
                var bare = Path.Combine(dir, "thing.xz");
                File.WriteAllBytes(bare, new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 1, 2, 3 });

                string? said = null;
                try { await ArchiveEngine.ExtractAsync(bare, Path.Combine(dir, "refused"), PasteConflictPolicy.AutoRename, null, 1, null, CancellationToken.None); }
                catch (NotSupportedException ex) { said = ex.Message; }

                Check("a bare .xz is refused with an explanation and a way forward",
                    said != null && said.Contains(".tar.xz"), said);
            }
            finally { Cleanup(dir); }
        }

        /// <summary>What an archive this application wrote holds, read by the engine that wrote it.</summary>
        private static IReadOnlyList<ArchiveEntryInfo> ListArchive(string path)
        {
            var format = ArchiveFormats.Identify(path)!;
            return format.Engine == ArchiveEngineKind.Zip
                ? ZipEngine.List(path, CancellationToken.None)
                : TarEngine.List(path, format, CancellationToken.None);
        }

        // ---------- The Compress window ----------

        /// <summary>
        /// That it opens, that everything on it is reachable and named, and that
        /// the two things it does on its own are right.
        ///
        /// This codebase has a rule about hand-built windows with no test that
        /// they so much as open, SettingsForm went nine pages without one, and
        /// this one has arithmetic in it besides: it rewrites the extension when
        /// the format changes, and it decides which formats are even offered from
        /// how many things were selected. Both are the kind of thing that is
        /// invisible until somebody ends up with a "photos.zip" that is a 7z.
        ///
        /// Run on a thread of its own with an apartment state, because that is
        /// what a Form requires and the suite's own thread is not one.
        /// </summary>
        /// <summary>
        /// The bar has to move while the work is happening, which is a different
        /// claim from "the totals are right at the end" and the one that was
        /// wrong. Every entry small enough to compress in memory was counted as
        /// the writer picked it up, so a 44MB zip reported three times: zero,
        /// zero, and finished. Reported as "the progress bar just sits there",
        /// and nothing in this suite could see it, the round trips pass a null
        /// progress sink, and the totals at the end were always correct.
        ///
        /// **One file, deliberately.** With several, the old code still reported
        /// something partway, as each entry was written, so a folder of six
        /// measured "0%, 91%, 100%" and a check for any partway report passed
        /// against the bug. One file is the case that was reported and the case
        /// that cannot fake it: nothing at all until it is finished.
        ///
        /// Measured on the real engine, so the file has to be big enough that
        /// deflating it takes longer than the hundred-millisecond throttle.
        /// </summary>
        private static async Task ProgressMovesTests()
        {
            Console.WriteLine("Archive progress:");

            var dir = Path.Combine(Path.GetTempPath(), "en-arcprog-" + Guid.NewGuid().ToString("N")[..8]);
            var source = Path.Combine(dir, "stuff");
            Directory.CreateDirectory(source);

            try
            {
                // Compressible, but not so compressible that deflate finishes
                // before the throttle lets a second report through. Written in
                // blocks rather than a line at a time: this is a fixture, and a
                // fixture that takes longer to write than the thing under test
                // is a tax on every run of the suite.
                //
                // Sixty-four megabytes, not the twenty-four it was: on mains
                // power this machine compresses twenty-four in about 300ms,
                // which is room for two reports at 100ms apart, and the check
                // below wants three. How much there is sets how long the
                // compression runs, which is the only thing this depends on.
                var rng = new Random(11);
                var block = new byte[1 << 20];
                for (int i = 0; i < block.Length; i++)
                    block[i] = (byte)(i % 97 == 0 ? rng.Next(256) : 'a' + (i % 23));

                using (var w = new FileStream(Path.Combine(source, "one.bin"), FileMode.Create, FileAccess.Write))
                    for (int mb = 0; mb < ProgressFixtureMegabytes; mb++)
                        w.Write(block, 0, block.Length);

                var seen = new List<TransferProgress>();
                var sink = new RecordingProgress(p => { lock (seen) seen.Add(p); });

                var archive = Path.Combine(dir, "made.zip");
                var made = await ArchiveEngine.CompressAsync(
                    new[] { source }, archive, ArchiveFormats.Zip,
                    ArchiveLevel.Balanced, ArchiveEngine.RecommendedThreads, sink, CancellationToken.None);

                Check("the measured compress reported no failures", made.Failed == 0,
                    string.Join("; ", made.Errors));

                List<TransferProgress> reports;
                lock (seen) reports = new List<TransferProgress>(seen);

                var moving = reports.Count(p => p.TotalKnown && p.BytesDone > 0 && p.BytesDone < p.BytesTotal);
                Check("the bar moves while a single file is being compressed",
                    moving >= 3,
                    $"{reports.Count} reports, {moving} of them partway: " +
                    string.Join(", ", reports.Select(p => $"{p.Percent:0}%")));

                var last = reports.LastOrDefault();
                Check("and it ends on the whole of it",
                    last != null && last.BytesDone == last.BytesTotal && last.BytesTotal > 0,
                    last == null ? "no reports" : $"{last.BytesDone} of {last.BytesTotal}");

                Check("and never claims more than there was",
                    reports.All(p => !p.TotalKnown || p.BytesDone <= p.BytesTotal),
                    string.Join(", ", reports.Where(p => p.TotalKnown && p.BytesDone > p.BytesTotal)
                                             .Select(p => $"{p.BytesDone}/{p.BytesTotal}")));
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// Two things arriving at one name.
        ///
        /// The renaming rule asked the filesystem and nothing else, so a name it
        /// invented could be a name the archive was already bringing: an archive
        /// holding both "a" and "a (2)", extracted where "a" exists, renamed the
        /// arriving "a" to "a (2)" and left the archive's own "a (2)" alone. Both
        /// trees were written into one folder, same-named files inside them
        /// overwriting each other, and it reported a clean extraction.
        /// </summary>
        private static void ExtractionRenameTests()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-extrename-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(dir, "a"));   // already there, so "a" clashes

            try
            {
                var entries = new List<ArchiveEntryInfo>
                {
                    new("a/one.txt", 10, DateTime.UtcNow, false),
                    new("a (2)/two.txt", 10, DateTime.UtcNow, false),
                };

                var rules = ExtractionRules.Build(entries, dir, PasteConflictPolicy.AutoRename, null,
                    CancellationToken.None);

                Check("the rules were built", rules != null);
                if (rules == null) return;

                var first = rules.TargetFor("a/one.txt", false, out _);
                var second = rules.TargetFor("a (2)/two.txt", false, out _);

                Check("the clashing folder is renamed away from the one on disk",
                    first != null && !first.StartsWith(Path.Combine(dir, "a") + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase),
                    first ?? "(refused)");

                Check("and never onto a name the archive is already bringing",
                    first != null && second != null &&
                    !string.Equals(Path.GetDirectoryName(first), Path.GetDirectoryName(second),
                        StringComparison.OrdinalIgnoreCase),
                    $"{first} / {second}");
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// The whole chain, once: a real compression, through a real
        /// <c>Progress&lt;T&gt;</c>, into a real <see cref="ProgressForm"/>, with
        /// the window's own fields read back as it runs.
        ///
        /// The engine check above proves the reports are made; this proves they
        /// arrive somewhere a person can see. Between them sits the piece that
        /// cannot be reasoned about, a report is posted to the thread that owns
        /// the window and drawn only when that thread pumps, and "the progress
        /// bar just sits there" is a sentence about the end of the chain, not
        /// about any one link in it.
        ///
        /// The window is real and it is off screen at -4000,-4000, out of
        /// everybody's way: this is a test, not something to be shown a window by.
        /// </summary>
        /// <summary>
        /// How big the file the progress tests compress is. See the measured
        /// compress: the tests need the work to outlast a few 100ms reports.
        /// </summary>
        private const int ProgressFixtureMegabytes = 64;

        private static void ProgressWindowChainTests()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-arcwin-" + Guid.NewGuid().ToString("N")[..8]);
            var source = Path.Combine(dir, "stuff");
            Directory.CreateDirectory(source);

            var percents = new List<string>();
            var items = new List<string>();
            bool namedWhilePartway = false;
            string error = "";

            var thread = new Thread(() =>
            {
                try
                {
                    var rng = new Random(3);
                    var block = new byte[1 << 20];
                    for (int i = 0; i < block.Length; i++)
                        block[i] = (byte)(i % 97 == 0 ? rng.Next(256) : 'a' + (i % 23));

                    using (var w = new FileStream(Path.Combine(source, "one.bin"), FileMode.Create, FileAccess.Write))
                        for (int mb = 0; mb < ProgressFixtureMegabytes; mb++)
                            w.Write(block, 0, block.Length);

                    // What the application installs on its UI thread. Without it
                    // Progress<T> posts to the thread pool and the window is
                    // updated from the wrong thread, which is the bug this is
                    // here to catch, so it must not be arranged away.
                    SynchronizationContext.SetSynchronizationContext(
                        new WindowsFormsSynchronizationContext());

                    using var cts = new CancellationTokenSource();
                    using var window = new ProgressForm("Compressing", new Settings(), cts);
                    window.StartPosition = FormStartPosition.Manual;
                    window.Location = new System.Drawing.Point(-4000, -4000);
                    window.ShowInTaskbar = false;
                    window.Show();
                    Application.DoEvents();

                    var progress = new Progress<TransferProgress>(p => window.Update(p));

                    var task = ArchiveEngine.CompressAsync(
                        new[] { source }, Path.Combine(dir, "made.zip"), ArchiveFormats.Zip,
                        ArchiveLevel.Balanced, ArchiveEngine.RecommendedThreads, progress, cts.Token);

                    var boxes = FindAll<TextBox>(window);
                    var percentBox = boxes.FirstOrDefault(b => b.AccessibleName == "Progress");
                    var itemBox = boxes.FirstOrDefault(b => b.AccessibleName == "Current item");

                    if (percentBox == null || itemBox == null)
                    {
                        error = "the window has no Progress or Current item field";
                        return;
                    }

                    // Pumping is what the UI thread does; the reports are drawn as
                    // it does. Sampled from here rather than from inside Update, so
                    // what is recorded is what the field actually holds.
                    var clock = Stopwatch.StartNew();
                    while (!task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(60))
                    {
                        Application.DoEvents();
                        if (percents.LastOrDefault() != percentBox.Text) percents.Add(percentBox.Text);
                        if (items.LastOrDefault() != itemBox.Text) items.Add(itemBox.Text);

                        // The pair, not either on its own: a name that only turns
                        // up on the closing report is the bug wearing a hat.
                        if (itemBox.Text.Contains("one.bin", StringComparison.Ordinal) &&
                            percentBox.Text.Contains("percent", StringComparison.Ordinal) &&
                            !percentBox.Text.StartsWith("100 percent", StringComparison.Ordinal))
                            namedWhilePartway = true;

                        Thread.Sleep(15);
                    }

                    task.GetAwaiter().GetResult();

                    // The closing report is posted from the engine's thread and
                    // drawn when this one pumps, so a single DoEvents can run
                    // before it has even been queued. Pumped until it lands, with
                    // a ceiling, the window is allowed to take a moment to draw
                    // the last number, not allowed never to draw it.
                    // Ten seconds: a shared CI runner drew reports seconds late
                    // (7.1.0), and the loop leaves the moment 100 percent lands.
                    var settle = Stopwatch.StartNew();
                    while (settle.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        Application.DoEvents();
                        if (percents.LastOrDefault() != percentBox.Text) percents.Add(percentBox.Text);
                        if (items.LastOrDefault() != itemBox.Text) items.Add(itemBox.Text);
                        // A partway report drawn late is still one the window
                        // showed partway; the closing one says 100 percent and
                        // never counts.
                        if (itemBox.Text.Contains("one.bin", StringComparison.Ordinal) &&
                            percentBox.Text.Contains("percent", StringComparison.Ordinal) &&
                            !percentBox.Text.StartsWith("100 percent", StringComparison.Ordinal))
                            namedWhilePartway = true;
                        if (percentBox.Text.StartsWith("100 percent", StringComparison.Ordinal)) break;
                        Thread.Sleep(15);
                    }
                }
                catch (Exception ex) { error = ex.Message; }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(90));

            try
            {
                Check("the progress window can be driven by a real compression", error.Length == 0, error);

                Check("the window's own percentage moves while the archive is written",
                    percents.Count(p => p.Contains("percent", StringComparison.Ordinal)) >= 3,
                    string.Join(" -> ", percents));

                Check("and it ends showing all of it",
                    percents.LastOrDefault()?.StartsWith("100 percent", StringComparison.Ordinal) == true,
                    percents.LastOrDefault() ?? "(nothing)");

                Check("and it names the file while it is compressing it, not the folder above it",
                    namedWhilePartway,
                    string.Join(" -> ", items));
            }
            finally { Cleanup(dir); }
        }

        private static List<T> FindAll<T>(Control root) where T : Control
        {
            var found = new List<T>();
            foreach (Control child in root.Controls)
            {
                if (child is T match) found.Add(match);
                found.AddRange(FindAll<T>(child));
            }
            return found;
        }

        /// <summary>Hands each report straight to a check, on the thread that made it.</summary>
        private sealed class RecordingProgress : IProgress<TransferProgress>
        {
            private readonly Action<TransferProgress> _onReport;
            public RecordingProgress(Action<TransferProgress> onReport) => _onReport = onReport;
            public void Report(TransferProgress value) => _onReport(value);
        }

        private static void CompressWindowTests()
        {
            Console.WriteLine("The Compress window:");

            var dir = NewDir("dialog");
            var file = Path.Combine(dir, "notes.txt");
            File.WriteAllText(file, "x");
            var folder = Path.Combine(dir, "photos");
            Directory.CreateDirectory(folder);

            string failure = "";
            bool opened = false, unnamed = false, singleOffered = false, singleHidden = false;
            string startName = "", afterFormat = "", chosenFormat = "";
            int clipped = 0;

            var thread = new Thread(() =>
            {
                try
                {
                    // One file: the bare gzip is a format that can hold it.
                    using (var window = new ArchiveForm(dir, new[] { file },
                               ArchiveFormats.Zip, ArchiveLevel.Balanced, singleFile: true))
                    {
                        // Bigger text, for the same reason ProgressForm scales it:
                        // the suite runs at 100% and the clipping happens at 150%.
                        window.Font = new System.Drawing.Font(
                            window.Font.FontFamily, window.Font.Size * 1.5f);

                        window.StartPosition = FormStartPosition.Manual;
                        window.Location = new System.Drawing.Point(-4000, -4000);
                        window.ShowInTaskbar = false;
                        window.Show();
                        Application.DoEvents();
                        opened = true;

                        var name = Boxes<TextBox>(window).FirstOrDefault();
                        var lists = Boxes<ComboBox>(window);

                        startName = name?.Text ?? "(none)";

                        foreach (Control control in Everything(window))
                        {
                            if (control is not (TextBox or ComboBox or Button)) continue;
                            if (string.IsNullOrEmpty(control.AccessibleName) &&
                                string.IsNullOrEmpty(control.Text)) unnamed = true;

                            var at = window.PointToClient(control.PointToScreen(System.Drawing.Point.Empty));
                            if (at.Y + control.Height > window.ClientSize.Height ||
                                at.X + control.Width > window.ClientSize.Width) clipped++;
                        }

                        var formats = lists.FirstOrDefault();
                        if (formats != null)
                        {
                            for (int i = 0; i < formats.Items.Count; i++)
                                if ((formats.Items[i]?.ToString() ?? "").Contains(".gz)")) singleOffered = true;

                            // Move to the gzipped tar and watch the name follow.
                            int targz = -1;
                            for (int i = 0; i < formats.Items.Count; i++)
                                if ((formats.Items[i]?.ToString() ?? "").Contains("(.tar.gz)")) targz = i;

                            if (targz >= 0)
                            {
                                formats.SelectedIndex = targz;
                                Application.DoEvents();
                                afterFormat = name?.Text ?? "";
                                chosenFormat = formats.Items[targz]?.ToString() ?? "";
                            }
                        }
                    }

                    // Two items: nothing that holds only one may be offered.
                    using (var window = new ArchiveForm(dir, new[] { file, folder },
                               ArchiveFormats.Zip, ArchiveLevel.Balanced, singleFile: false))
                    {
                        window.StartPosition = FormStartPosition.Manual;
                        window.Location = new System.Drawing.Point(-4000, -4000);
                        window.ShowInTaskbar = false;
                        window.Show();
                        Application.DoEvents();

                        var formats = Boxes<ComboBox>(window).FirstOrDefault();
                        singleHidden = true;
                        if (formats != null)
                            for (int i = 0; i < formats.Items.Count; i++)
                                if ((formats.Items[i]?.ToString() ?? "").EndsWith("(.gz)")) singleHidden = false;
                    }
                }
                catch (Exception ex) { failure = ex.ToString(); }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(60_000);

            Check("it opens without throwing", opened && failure.Length == 0,
                failure.Length > 300 ? failure[..300] : failure);
            Check("every control on it is named or labelled", !unnamed);
            Check("and none of it is off the edge at 150% text", clipped == 0, clipped.ToString());
            Equal("the name is suggested from the file and the format",
                "notes.zip", startName);
            Check("a single-file format is offered when one file was chosen", singleOffered);
            Equal("changing the format rewrites the extension in place",
                "notes.tar.gz", afterFormat);
            Check("the format list says what each one is called on disk",
                chosenFormat.Contains("(.tar.gz)"), chosenFormat);
            Check("and a single-file format is not offered for two items", singleHidden);

            Cleanup(dir);
        }

        private static List<T> Boxes<T>(Control root) where T : Control =>
            Everything(root).OfType<T>().ToList();

        private static List<Control> Everything(Control root)
        {
            var found = new List<Control>();
            foreach (Control child in root.Controls)
            {
                found.Add(child);
                found.AddRange(Everything(child));
            }
            return found;
        }

        // ---------- What gets handed to the system tar ----------

        /// <summary>
        /// The option strings, checked against the tar on this machine rather
        /// than against what they were written to say.
        ///
        /// bsdtar refuses an option it does not recognise and stops, "Undefined
        /// option", exit code 1, so a name that has drifted is not a setting
        /// silently ignored, it is a compress that fails outright in front of
        /// somebody. Every string this produces is therefore handed to the real
        /// thing on a real file.
        /// </summary>
        private static void SystemTarOptionTests()
        {
            Console.WriteLine("Options for the system tar:");

            // Never more threads than asked for, and never none.
            Check("xz threads are capped by memory at the heaviest preset",
                BsdTar.XzThreads(9, 64) <= 64 && BsdTar.XzThreads(9, 64) >= 1,
                BsdTar.XzThreads(9, 64).ToString());

            Check("and the lightest preset is not capped below what was asked for",
                BsdTar.XzThreads(1, 4) == 4, BsdTar.XzThreads(1, 4).ToString());

            Check("asking for one always gets one", BsdTar.XzThreads(9, 1) == 1);

            Check("the heaviest preset never gets more threads than the lightest",
                BsdTar.XzThreads(9, 16) <= BsdTar.XzThreads(1, 16));

            Check("the machine's memory is readable", BsdTar.PhysicalMemoryBytes() > 0,
                BsdTar.PhysicalMemoryBytes().ToString());

            if (!BsdTar.HandlesNames)
            {
                Check("the system tar is present", BsdTar.Available, "tar.exe missing");
                return;
            }

            var dir = NewDir("opts");
            try
            {
                var source = Path.Combine(dir, "one.txt");
                File.WriteAllText(source, "something to compress");

                foreach (var format in ArchiveFormats.All.Where(
                             f => f.CanCreate && f.Engine == ArchiveEngineKind.BsdTar))
                    foreach (var level in new[] { ArchiveLevel.Fastest, ArchiveLevel.Balanced, ArchiveLevel.Smallest })
                    {
                        var options = BsdTar.Options(format, level, 4);
                        if (options == null) continue;

                        var archive = Path.Combine(dir, $"probe-{format.Id}-{level}{format.Extension}");
                        int code = RunTar(out var complaint, "-a", "-c", "-f", archive,
                            "--options", options, "-C", dir, "one.txt");

                        Check($"{format.Id} at {level}: the system tar accepts \"{options}\"",
                            code == 0 && File.Exists(archive), complaint);
                    }
            }
            finally { Cleanup(dir); }
        }

        // ---------- The commands, as the window wires them ----------

        /// <summary>
        /// Read out of MainForm.cs, because that file cannot be compiled into
        /// this suite.
        ///
        /// It has an entry point of its own, MainForm reads three diagnostics
        /// straight off Program, so the two command handlers behind Ctrl+Shift+C
        /// and Ctrl+Shift+E are the one part of this feature no test can call.
        /// Reading the source is what the pane-naming rule already does for the
        /// same reason: the rule lives somewhere the compiler cannot see it.
        ///
        /// What is checked here is only the handful of things that are invisible
        /// while reading and expensive when wrong.
        /// </summary>
        private static void CommandWiringTests()
        {
            Console.WriteLine("The archive commands:");

            var main = Source("MainForm.cs");
            var reserved = Source("ReservedShortcuts.cs");

            if (main == null || reserved == null)
            {
                Check("MainForm.cs and ReservedShortcuts.cs can be read", false);
                return;
            }

            // On the context menu, which is what Shift+F10 shows, and not on the
            // Tools menu. A command that acts on the selection belongs one
            // keystroke from the row it is about.
            int contextAt = main.IndexOf("private void BuildContextMenu()", StringComparison.Ordinal);
            int contextEnd = contextAt >= 0
                ? main.IndexOf("\n        private ", contextAt + 10, StringComparison.Ordinal)
                : -1;
            var context = contextAt >= 0 && contextEnd > contextAt ? main[contextAt..contextEnd] : "";

            Check("Compress is on the context menu",
                context.Contains("Item(\"Co&mpress…\", Keys.None, CompressSelection)", StringComparison.Ordinal));

            Check("Extract is on the context menu",
                context.Contains("ExtractFocused(intoNewFolder: true)", StringComparison.Ordinal));

            Check("and so is Extract here",
                context.Contains("ExtractFocused(intoNewFolder: false)", StringComparison.Ordinal));

            int toolsAt = main.IndexOf("var tools = new ToolStripMenuItem", StringComparison.Ordinal);
            int toolsEnd = toolsAt >= 0
                ? main.IndexOf("menu.Items.Add(tools);", toolsAt, StringComparison.Ordinal)
                : -1;
            var tools = toolsAt >= 0 && toolsEnd > toolsAt ? main[toolsAt..toolsEnd] : "";

            Check("and none of the three is also on the Tools menu",
                !tools.Contains("CompressSelection", StringComparison.Ordinal) &&
                !tools.Contains("ExtractFocused", StringComparison.Ordinal));

            // No shortcut means nothing to reserve. A reservation for a key
            // nothing uses is a key taken away from the audio hotkeys for no
            // reason, and ReservedShortcuts says it lists the window's own.
            Check("no shortcut is reserved for a command that no longer has one",
                !reserved.Contains("\"Compress\"", StringComparison.Ordinal) &&
                !reserved.Contains("\"Extract\"", StringComparison.Ordinal));

            // Extract is hidden on a row it could not work on, and the decision
            // is made from the name, deciding what goes on a menu must never
            // cost a read of a file that may be on a share.
            Check("the popup decides whether Extract belongs on it",
                main.Contains("_extractItem.Visible = looksLikeArchive;", StringComparison.Ordinal) &&
                main.Contains("_extractHereItem.Visible = looksLikeArchive;", StringComparison.Ordinal));

            Check("and it decides by name rather than by opening the file",
                main.Contains("ArchiveFormats.FromName(focused)", StringComparison.Ordinal) &&
                !main.Contains("ArchiveFormats.Identify(focused)", StringComparison.Ordinal));

            // The progress window has to be called "window" in both handlers.
            // That is not a naming preference: the suite's own rule against a
            // modal transfer looks for "window.ShowDialog", so a handler that
            // called it "dialog" would be outside a check it is meant to be
            // inside, and ShowDialog is exactly what a later edit reaches for
            // out of habit.
            Check("both handlers name the progress window the way the modal check expects",
                Occurrences(main, "using var window = new ProgressForm(") >= 4,
                Occurrences(main, "using var window = new ProgressForm(").ToString());

            // Both are async void, which means an exception has no caller to
            // catch it and goes to Application.ThreadException, the crash log
            // and a dialog, instead of a message.
            foreach (var handler in new[] { "CompressSelection", "ExtractFocused" })
            {
                int at = main.IndexOf("private async void " + handler, StringComparison.Ordinal);
                Check($"{handler} exists and is an event handler", at >= 0);
                if (at < 0) continue;

                int next = main.IndexOf("\n        private ", at + 10, StringComparison.Ordinal);
                var body = next > at ? main[at..next] : main[at..];

                // Since 4.2.1 ExtractFocused starts one ExtractOneAsync per
                // selected archive; the window and the Drive refusal live there.
                if (handler == "ExtractFocused")
                {
                    int one = main.IndexOf("private async Task ExtractOneAsync(", StringComparison.Ordinal);
                    Check("ExtractFocused hands each archive to ExtractOneAsync",
                        one >= 0 && body.Contains("ExtractOneAsync(", StringComparison.Ordinal));
                    if (one >= 0)
                    {
                        int after = main.IndexOf("\n        private ", one + 10, StringComparison.Ordinal);
                        body += after > one ? main[one..after] : main[one..];
                    }
                }

                Check($"{handler} catches everything it can throw",
                    body.Contains("catch (Exception ex)", StringComparison.Ordinal), handler);

                Check($"{handler} releases the folder it registered in a finally",
                    body.Contains("finally { RunningTransfers--; EndTransferTo(destination); }",
                        StringComparison.Ordinal), handler);

                Check($"{handler} shows the progress window rather than holding it modal",
                    body.Contains("window.Show();", StringComparison.Ordinal) &&
                    !body.Contains("window.ShowDialog", StringComparison.Ordinal), handler);

                // Robocopy cannot write into a placeholder tree and neither can
                // this. Said out loud rather than attempted, because the failure
                // otherwise is an archive that looks created and holds nothing.
                Check($"{handler} refuses the Google Drive letter rather than half working",
                    body.Contains("Drive.Owns(", StringComparison.Ordinal), handler);
            }

            // Every id the handlers raise has to be in the catalogue. The suite
            // checks the reverse direction already, that everything marked
            // Raised appears in the source, so this closes the loop.
            foreach (var id in new[]
                     {
                         "archive.start", "archive.done", "archive.partial", "archive.failed",
                         "archive.cancelled", "archive.unknown", "extract.start", "extract.done",
                         "extract.partial", "extract.failed", "extract.cancelled",
                     })
            {
                Check($"the catalogue knows {id}", Notifications.ById(id) != null);
                Check($"and {id} is raised somewhere in the window",
                    main.Contains("\"" + id + "\"", StringComparison.Ordinal), id);
            }
        }

        private static int Occurrences(string text, string needle)
        {
            int count = 0, at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
            return count;
        }

        /// <summary>
        /// A production source file, found by walking up from wherever the suite
        /// was run rather than by a path relative to the binary.
        /// </summary>
        private static string? Source(string name)
        {
            var here = new DirectoryInfo(AppContext.BaseDirectory);

            for (int up = 0; up < 8 && here != null; up++, here = here.Parent)
            {
                var candidate = Path.Combine(here.FullName, "src", name);
                if (File.Exists(candidate)) return SourceFiles.Read(candidate);
            }

            return null;
        }

        // ---------- Plumbing ----------

        private static int RunTar(out string output, params string[] arguments)
        {
            var start = new ProcessStartInfo
            {
                FileName = BsdTar.Executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start)!;
            var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(120_000);

            output = text.Length > 500 ? text[..500] : text;
            return process.ExitCode;
        }

        private static string NewDir(string tag)
        {
            var dir = Path.Combine(Path.GetTempPath(),
                $"en-arc-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---- merged from ArchiveFixTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// Regression tests for the compress-and-extract review of 2026-10-05: one
    /// per finding, each on a real archive built for the case.
    /// </summary>
    internal static class ArchiveFixTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        /// <summary>
        /// Where the chosen tar is the libarchive 3.8.8 that crashes on a
        /// non-ASCII name: the crash put "tar.exe - Application Error" over
        /// everything and the compress hung behind it until OK was clicked.
        /// Now the tar dies silently and the result says why in words.
        /// </summary>
        private static async Task CrashShowsNoBoxTests()
        {
            if (!BsdTar.Available) return;
            var dir = Path.Combine(Path.GetTempPath(), "EN-crashbox-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "日本語.txt");
                File.WriteAllText(file, "probe");
                var archive = Path.Combine(dir, "out.7z");
                var run = ArchiveEngine.CompressAsync(new[] { file }, archive, ArchiveFormats.SevenZip,
                    ArchiveLevel.Balanced, 2, null, CancellationToken.None);
                bool finished = await Task.WhenAny(run, Task.Delay(20_000)) == run;
                string said = "";
                if (finished)
                {
                    try
                    {
                        var result = await run;
                        said = result.Failed > 0 ? string.Join("; ", result.Errors) : "";
                    }
                    catch (IOException ex) { said = ex.Message; }
                }
                var box = FindWindowW(null, "tar.exe - Application Error");
                if (box != IntPtr.Zero) PostMessageW(box, 0x0010, IntPtr.Zero, IntPtr.Zero);
                Check("a crashing tar puts up no Windows error box", box == IntPtr.Zero);
                Check("and the compress finishes instead of waiting on one", finished);
                if (finished)
                    Check("and says in words that Windows' tool crashed", said.Contains("crashed", StringComparison.Ordinal), said);
                else
                {
                    foreach (var tar in Process.GetProcessesByName("tar")) try { tar.Kill(); } catch { }
                }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string? className, string title);

        [DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr window, uint message, IntPtr w, IntPtr l);

        public static async Task RunAll(Action<string, bool, string?> check)
        {
            _check = check;
            Console.WriteLine("Archive fixes:");

            ReporterCapTests();
            await VanishedSourceTests();
            await TruncatedGzTests();
            await DamagedTarTests();
            await SparseTarTests();
            await OemZipTests();
            await ProtectedZipTests();
            await SkipAndCountTests();
            await CancelledReplaceTests();
            await FreshFolderSkipsListingTests();
            await NothingReadableKeepsOldArchiveTests();
            EmptyTarHasEndBlocksTests();
            await ProgressEndsAtTheEndTests();
            await GzWithExtraBytesTests();
            await GrowingFileKeepsTarTests();
            await DiskFullKeepsOldArchiveTests();
            TarStopsOnAFailedWriteTests();
            await TrailingDotSourceTests();
            await ReadOnlyArchiveReplacedTests();
            ExtractionLaneCapTests();

            if (!BsdTar.HandlesNames)
            {
                // A 7z is what makes this tar crash, and 7z now goes through
                // 7-Zip, so Windows' tar is put back for this one.
                BsdTar.SevenZipOff = true;
                try { await CrashShowsNoBoxTests(); }
                finally { BsdTar.SevenZipOff = false; }
                return;
            }

            await ManyLockedFilesTests();
            await LookalikeNamesThroughBsdTarTests();
            await LockedUpFrontTests();

            await TarDiesWithTheAppTests();

            await BadNamesThroughBsdTarTests();
            await DuplicatesThroughBsdTarTests();
            await LinkDoesNotHoldBackLastFileTests();
            await MixedCaseExtensionTests();
            await LockedSourceTests();
            await LongPathTests();
            await DamagedBsdTarTests();
            await BsdTarProgressTests();
            ChoiceKeyTests();
        }

        // ---------- Helpers ----------

        private static string NewDir(string tag)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"en-fix-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        private static Task<ArchiveResult> Extract(string archive, string into,
            PasteConflictPolicy policy = PasteConflictPolicy.AutoRename, IProgress<TransferProgress>? progress = null,
            CancellationToken token = default) =>
            ArchiveEngine.ExtractAsync(archive, into, policy, null, 4, progress, token);

        private static string Said(ArchiveResult result) => string.Join(" | ", result.Errors);

        /// <summary>A tar of hand-chosen entries, written by .NET's own TarWriter.</summary>
        private static byte[] Tar(params (string Name, string? Data)[] entries)
        {
            using var buffer = new MemoryStream();
            using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
            {
                foreach (var (name, data) in entries)
                {
                    if (data == null)
                    {
                        writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name));
                        continue;
                    }
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                    {
                        DataStream = new MemoryStream(Encoding.UTF8.GetBytes(data)),
                    });
                }
            }
            return buffer.ToArray();
        }

        private static int RunTar(out string output, params string[] arguments)
        {
            var start = new ProcessStartInfo
            {
                FileName = BsdTar.Executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var err = process.StandardError.ReadToEndAsync();
            output = process.StandardOutput.ReadToEnd() + err.Result;
            process.WaitForExit();
            return process.ExitCode;
        }

        /// <summary>A .tar.xz of the given tar, compressed by the system tar from our own bytes.</summary>
        private static string TarXz(string dir, string name, byte[] tar)
        {
            var plain = Path.Combine(dir, name + ".tar");
            File.WriteAllBytes(plain, tar);
            var xz = Path.Combine(dir, name + ".tar.xz");
            // -P, or bsdtar strips a drive letter from the names while it writes
            // them, and the archive no longer holds the name being tested.
            RunTar(out _, "-c", "-J", "-P", "-f", xz, "@" + plain);
            File.Delete(plain);
            return xz;
        }

        private static bool NoStaging(string dir) =>
            !Directory.Exists(dir) || !Directory.EnumerateDirectories(dir, ".extracting-*", SearchOption.AllDirectories).Any();

        // ---------- Progress never passes the end ----------

        private static void ReporterCapTests()
        {
            var seen = new List<TransferProgress>();
            var reporter = new ArchiveEngine.Reporter(new Sink(p => seen.Add(p)));
            reporter.SetTotals(100, 2);
            reporter.Finished(80);
            reporter.Finished(80);
            reporter.Finished(80);
            reporter.Done();
            Check("progress never claims more than the total, in bytes or in items",
                seen.All(p => p.BytesDone <= p.BytesTotal && p.ItemsDone <= p.ItemsTotal),
                string.Join(", ", seen.Select(p => $"{p.BytesDone}/{p.BytesTotal} {p.ItemsDone}/{p.ItemsTotal}")));
        }

        private sealed class Sink : IProgress<TransferProgress>
        {
            private readonly Action<TransferProgress> _on;
            public Sink(Action<TransferProgress> on) => _on = on;
            public void Report(TransferProgress value) { lock (this) _on(value); }
        }

        // ---------- A source that is gone ----------

        private static async Task VanishedSourceTests()
        {
            var dir = NewDir("gone");
            try
            {
                var missing = Path.Combine(dir, "was-here.txt");
                foreach (var format in new[] { ArchiveFormats.Zip, ArchiveFormats.SevenZip })
                {
                    if (format.Engine == ArchiveEngineKind.BsdTar && !BsdTar.HandlesNames) continue;
                    var archive = Path.Combine(dir, "x" + format.Extension);
                    string? said = null;
                    try
                    {
                        await ArchiveEngine.CompressAsync(new[] { missing }, archive, format,
                            ArchiveLevel.Fastest, 2, null, CancellationToken.None);
                    }
                    catch (IOException ex) { said = ex.Message; }

                    Check($"{format.Id}: a chosen item that is gone is a failure naming it, not \"Created\"",
                        said != null && said.Contains("was-here.txt") && said.Contains("no longer there"), said);
                    Check($"{format.Id}: and no archive is left", !File.Exists(archive));
                }
            }
            finally { Cleanup(dir); }
        }

        // ---------- The bare .gz ----------

        private static async Task TruncatedGzTests()
        {
            var dir = NewDir("gz");
            try
            {
                // Big enough to be several ParallelGZip members, so the last-member
                // check is the one that runs on the intact file.
                var source = Path.Combine(dir, "big.bin");
                var data = new byte[5 * 1024 * 1024 + 123];
                new Random(9).NextBytes(data);
                for (int i = 0; i < data.Length; i += 3) data[i] = 0;
                File.WriteAllBytes(source, data);

                var gz = Path.Combine(dir, "big.bin.gz");
                await ArchiveEngine.CompressAsync(new[] { source }, gz, ArchiveFormats.Gz,
                    ArchiveLevel.Fastest, 4, null, CancellationToken.None);

                var whole = await Extract(gz, Path.Combine(dir, "whole"));
                Check("an intact multi-member .gz extracts with no complaint", whole.Failed == 0, Said(whole));
                Check("and comes out byte for byte",
                    File.ReadAllBytes(Path.Combine(dir, "whole", "big.bin")).AsSpan().SequenceEqual(data));

                // One member, as gzip itself writes.
                var single = Path.Combine(dir, "one.txt.gz");
                using (var file = File.Create(single))
                using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
                    gzip.Write(data, 0, 300_000);
                var one = await Extract(single, Path.Combine(dir, "single"));
                Check("an intact single-member .gz extracts with no complaint", one.Failed == 0, Said(one));

                var cut = Path.Combine(dir, "cut.bin.gz");
                var bytes = File.ReadAllBytes(gz);
                File.WriteAllBytes(cut, bytes[..(bytes.Length * 6 / 10)]);
                var truncated = await Extract(cut, Path.Combine(dir, "cut"));
                Check("a truncated .gz is reported as damaged, not extracted as half a file",
                    truncated.Failed > 0 && Said(truncated).Contains("damaged or incomplete") &&
                    !File.Exists(Path.Combine(dir, "cut", "cut.bin")), Said(truncated));

                var bad = Path.Combine(dir, "bad.bin.gz");
                var corrupt = (byte[])bytes.Clone();
                for (int i = 2000; i < 2100; i++) corrupt[i] ^= 0x55;
                File.WriteAllBytes(bad, corrupt);
                var damaged = await Extract(bad, Path.Combine(dir, "bad"));
                Check("a corrupt .gz says it is damaged rather than \"unsupported compression method\"",
                    damaged.Failed > 0 && Said(damaged).Contains("damaged") &&
                    !Said(damaged).Contains("unsupported", StringComparison.OrdinalIgnoreCase), Said(damaged));
            }
            finally { Cleanup(dir); }
        }

        // ---------- Truncated and corrupt tars ----------

        private static async Task DamagedTarTests()
        {
            var dir = NewDir("tar");
            try
            {
                var source = Path.Combine(dir, "src", "top");
                Directory.CreateDirectory(source);
                var rng = new Random(2);
                for (int i = 0; i < 8; i++)
                {
                    var block = new byte[300_000];
                    rng.NextBytes(block);
                    File.WriteAllBytes(Path.Combine(source, $"f{i}.bin"), block);
                }

                foreach (var format in new[] { ArchiveFormats.Tar, ArchiveFormats.TarGz })
                {
                    var archive = Path.Combine(dir, "whole" + format.Extension);
                    await ArchiveEngine.CompressAsync(new[] { source }, archive, format,
                        ArchiveLevel.Fastest, 4, null, CancellationToken.None);
                    var bytes = File.ReadAllBytes(archive);

                    var cut = Path.Combine(dir, "cut" + format.Extension);
                    File.WriteAllBytes(cut, bytes[..(bytes.Length / 2)]);

                    // Into a new folder (no listing) and into one already holding
                    // something (listed first, which used to throw).
                    foreach (var into in new[] { Path.Combine(dir, format.Id + "-new"), Path.Combine(dir, format.Id + "-full") })
                    {
                        if (into.EndsWith("full"))
                        {
                            Directory.CreateDirectory(into);
                            File.WriteAllText(Path.Combine(into, "already.txt"), "here");
                        }

                        var result = await Extract(cut, into);
                        int saved = Directory.Exists(Path.Combine(into, "top"))
                            ? Directory.GetFiles(Path.Combine(into, "top")).Length : 0;
                        Check($"{format.Id} cut in half ({Path.GetFileName(into)}): what was readable is saved",
                            saved >= 2 && saved < 8, $"{saved} files; {Said(result)}");
                        Check($"{format.Id} cut in half ({Path.GetFileName(into)}): and it says the archive is damaged and how much was saved",
                            Said(result).Contains("damaged or incomplete") && Said(result).Contains("extracted before the damage"),
                            Said(result));
                    }
                }

                // A gzipped tar whose compressed data is damaged part way.
                var tgz = Path.Combine(dir, "whole.tar.gz");
                var corrupt = File.ReadAllBytes(tgz);
                for (int i = corrupt.Length / 2; i < corrupt.Length / 2 + 200; i++) corrupt[i] ^= 0x5A;
                var bad = Path.Combine(dir, "bad.tar.gz");
                File.WriteAllBytes(bad, corrupt);
                var damaged = await Extract(bad, Path.Combine(dir, "badout"));
                Check("a corrupt .tar.gz says it is damaged, not \"unsupported compression method\"",
                    Said(damaged).Contains("damaged") &&
                    !Said(damaged).Contains("unsupported", StringComparison.OrdinalIgnoreCase), Said(damaged));
            }
            finally { Cleanup(dir); }
        }

        // ---------- Sparse files ----------

        private static byte[] SparseTar(string version, string realName, long realSize,
            (long Offset, byte[] Data)[] pieces)
        {
            using var buffer = new MemoryStream();
            using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
            {
                var attributes = new Dictionary<string, string>();
                var stored = new MemoryStream();
                if (version == "1.0")
                {
                    attributes["GNU.sparse.major"] = "1";
                    attributes["GNU.sparse.minor"] = "0";
                    attributes["GNU.sparse.name"] = realName;
                    attributes["GNU.sparse.realsize"] = realSize.ToString();
                    var map = new StringBuilder();
                    map.Append(pieces.Length).Append('\n');
                    foreach (var (offset, data) in pieces) map.Append(offset).Append('\n').Append(data.Length).Append('\n');
                    var mapBytes = Encoding.ASCII.GetBytes(map.ToString());
                    stored.Write(mapBytes);
                    stored.Write(new byte[(512 - mapBytes.Length % 512) % 512]);
                }
                else
                {
                    attributes["GNU.sparse.size"] = realSize.ToString();
                    attributes["GNU.sparse.numblocks"] = pieces.Length.ToString();
                    attributes["GNU.sparse.map"] = string.Join(",", pieces.Select(p => $"{p.Offset},{p.Data.Length}"));
                    attributes["GNU.sparse.name"] = realName;
                }
                foreach (var (_, data) in pieces) stored.Write(data);
                stored.Position = 0;

                var name = version == "1.0" ? "GNUSparseFile.0/" + Path.GetFileName(realName) : realName;
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name, attributes) { DataStream = stored });
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "after.txt")
                {
                    DataStream = new MemoryStream(Encoding.ASCII.GetBytes("after")),
                });
            }
            return buffer.ToArray();
        }

        private static async Task SparseTarTests()
        {
            var dir = NewDir("sparse");
            try
            {
                var head = Enumerable.Repeat((byte)'H', 4096).ToArray();
                var middle = Enumerable.Repeat((byte)'M', 10_000).ToArray();
                const long size = 3 * 1024 * 1024;
                var pieces = new[] { (0L, head), (2L * 1024 * 1024, middle) };

                var expected = new byte[size];
                head.CopyTo(expected, 0);
                middle.CopyTo(expected, 2 * 1024 * 1024);

                foreach (var version in new[] { "1.0", "0.1" })
                {
                    var tar = Path.Combine(dir, $"sparse-{version}.tar");
                    File.WriteAllBytes(tar, SparseTar(version, "disk/holes.bin", size, pieces));

                    var listed = TarEngine.List(tar, ArchiveFormats.Tar, CancellationToken.None);
                    Check($"sparse {version}: listed under its real name and size",
                        listed.Any(e => e.Name == "disk/holes.bin" && e.Size == size),
                        string.Join(", ", listed.Select(e => $"{e.Name}={e.Size}")));

                    var into = Path.Combine(dir, "out-" + version);
                    var result = await Extract(tar, into);
                    var made = Path.Combine(into, "disk", "holes.bin");
                    Check($"sparse {version}: extracted under its real name, not GNUSparseFile.0",
                        File.Exists(made) && !Directory.Exists(Path.Combine(into, "GNUSparseFile.0")), Said(result));
                    Check($"sparse {version}: at its real size, with every piece where it belongs",
                        File.Exists(made) && File.ReadAllBytes(made).AsSpan().SequenceEqual(expected));
                    Check($"sparse {version}: and the entry after it is intact",
                        File.Exists(Path.Combine(into, "after.txt")) && result.Failed == 0, Said(result));
                }

                // The real thing: a sparse file tarred by Windows' own tar, which is
                // what produced GNUSparseFile.0/holes.bin at 262KB for a 64MB file.
                var systemTar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
                var holes = Path.Combine(dir, "src", "holes.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(holes)!);
                using (var file = new FileStream(holes, FileMode.Create, FileAccess.ReadWrite))
                {
                    bool sparse = DeviceIoControl(file.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                    file.SetLength(16 * 1024 * 1024);
                    file.Write(head);
                    file.Position = 9 * 1024 * 1024;
                    file.Write(middle);
                    if (!sparse) Console.WriteLine("  (the volume would not make a sparse file)");
                }
                var real = Path.Combine(dir, "real.tar");
                var start = new ProcessStartInfo(systemTar) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var a in new[] { "-c", "-f", real, "-C", Path.GetDirectoryName(holes)!, "holes.bin" }) start.ArgumentList.Add(a);
                using (var p = Process.Start(start)!) { p.StandardError.ReadToEnd(); p.WaitForExit(); }

                var realOut = Path.Combine(dir, "real-out");
                var fromWindows = await Extract(real, realOut);
                var back = Path.Combine(realOut, "holes.bin");
                Check("a sparse file tarred by Windows' tar comes back whole, under its own name",
                    File.Exists(back) && File.ReadAllBytes(back).AsSpan().SequenceEqual(File.ReadAllBytes(holes)),
                    $"{(File.Exists(back) ? new FileInfo(back).Length : -1)} bytes; {Said(fromWindows)}");
            }
            finally { Cleanup(dir); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, int inSize,
            IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

        // ---------- Zips: names in the OEM code page, and passwords ----------

        /// <summary>A stored zip with the given raw name bytes and flags, written byte by byte.</summary>
        private static byte[] RawZip(params (byte[] Name, ushort Flags, byte[] Data)[] entries)
        {
            using var zip = new MemoryStream();
            var central = new MemoryStream();
            foreach (var (name, flags, data) in entries)
            {
                long offset = zip.Position;
                uint crc = Crc32.Append(Crc32.Seed, data);
                var local = new byte[30];
                BinaryPrimitives.WriteUInt32LittleEndian(local, 0x04034b50);
                BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(4), 20);
                BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(6), flags);
                BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(14), crc);
                BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(18), (uint)data.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(22), (uint)data.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(26), (ushort)name.Length);
                zip.Write(local); zip.Write(name); zip.Write(data);

                var record = new byte[46];
                BinaryPrimitives.WriteUInt32LittleEndian(record, 0x02014b50);
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 20);
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 20);
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8), flags);
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), crc);
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), (uint)data.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)data.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(28), (ushort)name.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(42), (uint)offset);
                central.Write(record); central.Write(name);
            }
            long start = zip.Position;
            central.Position = 0;
            central.CopyTo(zip);
            var end = new byte[22];
            BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
            BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), (ushort)entries.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), (ushort)entries.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), (uint)central.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), (uint)start);
            zip.Write(end);
            return zip.ToArray();
        }

        private static async Task OemZipTests()
        {
            var dir = NewDir("oem");
            try
            {
                // "café.txt" as CP437 writes it, no UTF-8 flag, what bsdtar,
                // Explorer and older 7-Zip produce. And one UTF-8 name that forgot
                // the flag, and one that set it.
                var zip = Path.Combine(dir, "oem.zip");
                File.WriteAllBytes(zip, RawZip(
                    (new byte[] { (byte)'c', (byte)'a', (byte)'f', 0x82, (byte)'.', (byte)'t', (byte)'x', (byte)'t' }, 0, Encoding.ASCII.GetBytes("cp437")),
                    (Encoding.UTF8.GetBytes("naïve.txt"), 0, Encoding.ASCII.GetBytes("utf8 unflagged")),
                    (Encoding.UTF8.GetBytes("日本.txt"), 0x0800, Encoding.ASCII.GetBytes("utf8 flagged"))));

                var into = Path.Combine(dir, "out");
                var result = await Extract(zip, into);
                var names = Directory.Exists(into) ? Directory.GetFiles(into).Select(Path.GetFileName).ToList() : new List<string?>();
                Check("a zip name in the OEM code page comes out as itself, not with a replacement character",
                    names.Contains("café.txt") && !names.Any(n => n!.Contains('�')), string.Join(", ", names) + "; " + Said(result));
                Check("a UTF-8 name without the flag, and one with it, come out as themselves",
                    names.Contains("naïve.txt") && names.Contains("日本.txt"), string.Join(", ", names));

                if (BsdTar.HandlesNames)
                {
                    var src = Path.Combine(dir, "src");
                    Directory.CreateDirectory(src);
                    File.WriteAllText(Path.Combine(src, "café.txt"), "x");
                    var theirs = Path.Combine(dir, "theirs.zip");
                    RunTar(out var said, "-a", "-c", "-f", theirs, "-C", src, "café.txt");
                    var fromTar = Path.Combine(dir, "fromtar");
                    var r = await Extract(theirs, fromTar);
                    Check("a zip the system tar made from \"café.txt\" extracts as \"café.txt\"",
                        File.Exists(Path.Combine(fromTar, "café.txt")),
                        string.Join(", ", Directory.Exists(fromTar) ? Directory.GetFiles(fromTar).Select(Path.GetFileName) : Array.Empty<string>()) + said + Said(r));
                }
            }
            finally { Cleanup(dir); }
        }

        private static async Task ProtectedZipTests()
        {
            var dir = NewDir("pw");
            try
            {
                // Bit 0 of the flags: encrypted. ZipCrypto's twelve-byte header is
                // in front of the data, which is all a reader gets to see before
                // it needs the password.
                var header = new byte[12];
                new Random(1).NextBytes(header);

                var all = Path.Combine(dir, "all.zip");
                File.WriteAllBytes(all, RawZip(
                    (Encoding.ASCII.GetBytes("secret.txt"), 1, header),
                    (Encoding.ASCII.GetBytes("plans.txt"), 1, header)));

                // 5.1.0: protected zips go to 7-Zip with a password. With nobody
                // to ask (as here), the result still says it needs one.
                string? said = null;
                try { said = Said(await Extract(all, Path.Combine(dir, "allout"))); }
                catch (NotSupportedException ex) { said = ex.Message; }
                Check("a password-protected zip says so in one sentence",
                    said != null && said.Contains("password", StringComparison.OrdinalIgnoreCase), said);
                Check("and makes no folder for it", !Directory.Exists(Path.Combine(dir, "allout")));

                var some = Path.Combine(dir, "some.zip");
                File.WriteAllBytes(some, RawZip(
                    (Encoding.ASCII.GetBytes("open.txt"), 0, Encoding.ASCII.GetBytes("readable")),
                    (Encoding.ASCII.GetBytes("secret.txt"), 1, header)));
                var mixed = await Extract(some, Path.Combine(dir, "someout"));
                Check("a zip with one protected entry extracts the rest and names the protected one",
                    File.Exists(Path.Combine(dir, "someout", "open.txt")) &&
                    !File.Exists(Path.Combine(dir, "someout", "secret.txt")) &&
                    Said(mixed).Contains("password", StringComparison.OrdinalIgnoreCase), Said(mixed));
            }
            finally { Cleanup(dir); }
        }

        // ---------- Counting ----------

        private static async Task SkipAndCountTests()
        {
            var dir = NewDir("count");
            try
            {
                var top = Path.Combine(dir, "src", "top");
                Directory.CreateDirectory(Path.Combine(top, "sub"));
                for (int i = 0; i < 5; i++) File.WriteAllText(Path.Combine(top, $"f{i}.txt"), "x");
                File.WriteAllText(Path.Combine(top, "sub", "g.txt"), "y");

                var zip = Path.Combine(dir, "t.zip");
                var tgz = Path.Combine(dir, "t.tar.gz");
                await ArchiveEngine.CompressAsync(new[] { top }, zip, ArchiveFormats.Zip, ArchiveLevel.Fastest, 2, null, CancellationToken.None);
                await ArchiveEngine.CompressAsync(new[] { top }, tgz, ArchiveFormats.TarGz, ArchiveLevel.Fastest, 2, null, CancellationToken.None);

                var fromZip = await Extract(zip, Path.Combine(dir, "z"));
                var fromTar = await Extract(tgz, Path.Combine(dir, "t"));
                Check("the same tree extracts as the same number of items from a zip and a tar.gz",
                    fromZip.Items == fromTar.Items && fromZip.Items == 8, $"zip {fromZip.Items}, tar.gz {fromTar.Items}");

                var skipInto = Path.Combine(dir, "skip");
                Directory.CreateDirectory(Path.Combine(skipInto, "top"));
                var skipped = await Extract(zip, skipInto, PasteConflictPolicy.Skip);
                Check("skipping a folder counts one skip, not one per entry inside it",
                    skipped.Collisions.SkippedCount == 1, skipped.Collisions.SkippedCount.ToString());
            }
            finally { Cleanup(dir); }
        }

        private static async Task CancelledReplaceTests()
        {
            var dir = NewDir("cancelrep");
            try
            {
                var top = Path.Combine(dir, "src", "top");
                Directory.CreateDirectory(top);
                // Big enough that one worker is still writing well after the first
                // progress report, which is where the cancel is pressed.
                var block = new byte[3_000_000];
                new Random(4).NextBytes(block);
                for (int i = 0; i < 60; i++) File.WriteAllBytes(Path.Combine(top, $"f{i:00}.bin"), block);
                var zip = Path.Combine(dir, "t.zip");
                await ArchiveEngine.CompressAsync(new[] { top }, zip, ArchiveFormats.Zip, ArchiveLevel.Fastest, 4, null, CancellationToken.None);

                var into = Path.Combine(dir, "into");
                Directory.CreateDirectory(Path.Combine(into, "top"));
                for (int i = 0; i < 60; i++) File.WriteAllText(Path.Combine(into, "top", $"f{i:00}.bin"), "old");

                using var cts = new CancellationTokenSource();
                var sink = new Sink(p => { if (p.BytesDone > 0) cts.Cancel(); });
                var result = await ArchiveEngine.ExtractAsync(zip, into, PasteConflictPolicy.Overwrite, null, 1, sink, cts.Token);

                int replaced = Directory.GetFiles(Path.Combine(into, "top")).Count(f => new FileInfo(f).Length > 3);
                Check("a cancelled extraction reports only the replacements that happened",
                    result.Cancelled && result.Collisions.OverwrittenCount == replaced && replaced < 60,
                    $"cancelled {result.Cancelled}, said {result.Collisions.OverwrittenCount}, replaced {replaced}");
            }
            finally { Cleanup(dir); }
        }

        private static async Task FreshFolderSkipsListingTests()
        {
            var dir = NewDir("fresh");
            try
            {
                var top = Path.Combine(dir, "src", "top");
                Directory.CreateDirectory(top);
                var block = new byte[2_000_000];
                new Random(6).NextBytes(block);
                for (int i = 0; i < 6; i++) File.WriteAllBytes(Path.Combine(top, $"f{i}.bin"), block);
                var tgz = Path.Combine(dir, "t.tar.gz");
                await ArchiveEngine.CompressAsync(new[] { top }, tgz, ArchiveFormats.TarGz, ArchiveLevel.Fastest, 4, null, CancellationToken.None);

                var seen = new List<TransferProgress>();
                var result = await Extract(tgz, Path.Combine(dir, "new"), progress: new Sink(p => seen.Add(p)));
                var last = seen.LastOrDefault();
                Check("extracting into a new folder needs no listing and still ends on the whole of it",
                    result.Failed == 0 && result.Items == 7 && last != null && last.BytesDone == last.BytesTotal &&
                    last.ItemsDone == last.ItemsTotal && last.ItemsTotal == 7,
                    last == null ? "no reports" : $"{last.BytesDone}/{last.BytesTotal} {last.ItemsDone}/{last.ItemsTotal}; {Said(result)}");
                Check("and its progress never goes past the end",
                    seen.All(p => p.BytesDone <= p.BytesTotal && p.ItemsDone <= Math.Max(p.ItemsTotal, p.ItemsDone)));
            }
            finally { Cleanup(dir); }
        }

        // ---------- Through the system tar ----------

        private static async Task BadNamesThroughBsdTarTests()
        {
            var dir = NewDir("badnames");
            try
            {
                var xz = TarXz(dir, "bad", Tar(
                    ("ok.txt", "ok"), ("q?", "q"), ("x*y", "x"), ("trail.", "t"), ("name ", "n"),
                    ("CON", "c"), ("dir/", null), ("dir/inner.txt", "i"), ("a:b", "colon"),
                    ("last.txt", "last")));

                var into = Path.Combine(dir, "out");
                var result = await Extract(xz, into);
                var names = Directory.EnumerateFileSystemEntries(into, "*", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(into, p).Replace('\\', '/')).OrderBy(n => n).ToList();
                var said = Said(result);

                Check("a name Windows cannot store does not take the rest of a .tar.xz with it",
                    names.Contains("ok.txt") && names.Contains("dir/inner.txt") && names.Contains("last.txt"),
                    string.Join(", ", names) + " | " + said);
                Check("and nothing is moved in under the name bsdtar silently changed it to",
                    !names.Any(n => n is "q_" or "x_y" or "trail" or "name" or "CON" or "b"),
                    string.Join(", ", names));
                Check("each is refused by the name it had",
                    new[] { "\"q?\"", "\"x*y\"", "\"trail.\"", "\"name \"", "\"CON\"", "\"a:b\"" }
                        .All(n => said.Contains(n)), said);
                Check("and no staging folder is left behind", NoStaging(into));
            }
            finally { Cleanup(dir); }
        }

        private static async Task DuplicatesThroughBsdTarTests()
        {
            var dir = NewDir("dupes");
            try
            {
                var xz = TarXz(dir, "dupes", Tar(
                    ("A.txt", "upper"), ("a.txt", "lower"), ("dup.txt", "first"), ("dup.txt", "second"), ("other.txt", "o")));
                var into = Path.Combine(dir, "out");
                var result = await Extract(xz, into);
                Check("names repeated in a .tar.xz are said, not silently lost",
                    Said(result).Contains("2 items in the archive repeated a name"), Said(result));
                Check("and the first of each is kept, as zip and tar keep it",
                    File.ReadAllText(Path.Combine(into, "dup.txt")) == "first" &&
                    File.ReadAllText(Path.Combine(into, "A.txt")) == "upper");
            }
            finally { Cleanup(dir); }
        }

        private static async Task LinkDoesNotHoldBackLastFileTests()
        {
            var dir = NewDir("link");
            try
            {
                using var buffer = new MemoryStream();
                using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "first.txt") { DataStream = new MemoryStream(Encoding.ASCII.GetBytes("new first")) });
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "first.txt" });
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "last.txt") { DataStream = new MemoryStream(Encoding.ASCII.GetBytes("new last")) });
                }
                var xz = TarXz(dir, "link", buffer.ToArray());

                var into = Path.Combine(dir, "out");
                Directory.CreateDirectory(into);
                File.WriteAllText(Path.Combine(into, "first.txt"), "old");
                File.WriteAllText(Path.Combine(into, "last.txt"), "old");

                var result = await Extract(xz, into, PasteConflictPolicy.Overwrite);
                var said = Said(result);
                Check("one link in a .tar.xz does not withhold its last file",
                    File.ReadAllText(Path.Combine(into, "last.txt")) == "new last" && !said.Contains("stopped while"), said);
                Check("and under Replace the other files are replaced, not \"left as it was\"",
                    File.ReadAllText(Path.Combine(into, "first.txt")) == "new first" && !said.Contains("left as it was"), said);
                Check("and the link is named as not extracted",
                    said.Contains("link") && result.Failed >= 1, said);
            }
            finally { Cleanup(dir); }
        }

        private static async Task MixedCaseExtensionTests()
        {
            var dir = NewDir("case");
            try
            {
                var top = Path.Combine(dir, "src", "top");
                Directory.CreateDirectory(top);
                File.WriteAllText(Path.Combine(top, "a.txt"), "hello");

                foreach (var name in new[] { "BACKUP.7Z", "x.Tar.Xz", "y.TAR.BZ2", "z.Tar.Zst" })
                {
                    var archive = Path.Combine(dir, name);
                    var format = ArchiveFormats.FromName(name)!;
                    ArchiveResult? made = null;
                    string? failure = null;
                    try { made = await ArchiveEngine.CompressAsync(new[] { top }, archive, format, ArchiveLevel.Fastest, 2, null, CancellationToken.None); }
                    catch (Exception ex) { failure = ex.Message; }
                    var back = made == null ? null : await Extract(archive, Path.Combine(dir, "out-" + name));
                    Check($"{name}: an upper or mixed-case extension compresses and extracts",
                        made != null && made.Failed == 0 && back != null &&
                        File.Exists(Path.Combine(dir, "out-" + name, "top", "a.txt")), failure ?? (back == null ? "" : Said(back)));
                }
            }
            finally { Cleanup(dir); }
        }

        private static async Task LockedSourceTests()
        {
            var dir = NewDir("locked");
            try
            {
                var top = Path.Combine(dir, "src", "top");
                Directory.CreateDirectory(top);
                foreach (var n in new[] { "a.txt", "locked.txt", "z.txt" }) File.WriteAllText(Path.Combine(top, n), n);

                foreach (var format in new[] { ArchiveFormats.SevenZip, ArchiveFormats.TarXz })
                {
                    var archive = Path.Combine(dir, "x" + format.Extension);
                    ArchiveResult made;
                    using (new FileStream(Path.Combine(top, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                        made = await ArchiveEngine.CompressAsync(new[] { top }, archive, format, ArchiveLevel.Fastest, 2, null, CancellationToken.None);

                    Check($"{format.Id}: one locked file keeps the archive and names the file",
                        File.Exists(archive) && made.Failed == 1 && Said(made).Contains("locked.txt"), Said(made));

                    var into = Path.Combine(dir, "out-" + format.Id);
                    await Extract(archive, into);
                    Check($"{format.Id}: and the files after it in the same folder are still in it",
                        File.Exists(Path.Combine(into, "top", "a.txt")) && File.Exists(Path.Combine(into, "top", "z.txt")) &&
                        !File.Exists(Path.Combine(into, "top", "locked.txt")));
                }
            }
            finally { Cleanup(dir); }
        }

        private static readonly string[] Lookalikes =
        {
            "plain.txt", "aāb.txt", "œuvre.txt", "x’y.txt", "日本語.txt",
            "café.txt", "Δelta.txt", "smile\U0001F600.txt",
        };

        /// <summary>
        /// bsdtar prints a name its code page cannot hold as a best-fit lookalike,
        /// and for "aāb.txt" that is the plain-ASCII "aab.txt", which was taken
        /// as some other entry, so the file was said not to be in an archive that
        /// held it.
        /// </summary>
        private static async Task LookalikeNamesThroughBsdTarTests()
        {
            var dir = NewDir("lookalike");
            try
            {
                var top = Path.Combine(dir, "src", "top");
                Directory.CreateDirectory(top);
                foreach (var n in Lookalikes) File.WriteAllText(Path.Combine(top, n), "contents of " + n);

                foreach (var format in new[] { ArchiveFormats.SevenZip, ArchiveFormats.TarXz, ArchiveFormats.TarBz2, ArchiveFormats.TarZst })
                {
                    var archive = Path.Combine(dir, "names" + format.Extension);
                    var made = await ArchiveEngine.CompressAsync(new[] { top }, archive, format, ArchiveLevel.Fastest, 2,
                        null, CancellationToken.None);
                    Check($"{format.Id}: eight names a code page cannot all hold give no failures",
                        made.Failed == 0 && made.Errors.Count == 0, Said(made));

                    var into = Path.Combine(dir, "out-" + format.Id);
                    await Extract(archive, into);
                    var got = Directory.Exists(Path.Combine(into, "top"))
                        ? Directory.GetFiles(Path.Combine(into, "top")).Select(Path.GetFileName).ToList()
                        : new List<string?>();
                    Check($"{format.Id}: and every one of them is in it",
                        Lookalikes.All(n => got.Contains(n)), string.Join(", ", got));
                }

                // The arithmetic on its own, against the lines bsdtar printed.
                var plan = new List<ArchiveItem>
                {
                    new("x", "top", 0, true, DateTime.Now, FileAttributes.Directory),
                    new("x", "top/aāb.txt", 1, false, DateTime.Now, FileAttributes.Normal),
                    new("x", "top/aab.txt", 1, false, DateTime.Now, FileAttributes.Normal),
                    new("x", "top/Δelta.txt", 1, false, DateTime.Now, FileAttributes.Normal),
                    new("x", "top/goneā.txt", 1, false, DateTime.Now, FileAttributes.Normal),
                };
                var fit = BsdTar.PrintedForm("aāb.txt");
                Check("ā is printed as its plain lookalike", fit == "aab.txt", fit);
                var missing = BsdTar.NotAdded(plan, new[] { "top", "top/aab.txt", "top/aab.txt", "top/?elta.txt", "top/other.txt" });
                Check("a lookalike line accounts for its file, and an unrelated ASCII line for nothing",
                    missing.Count == 1 && missing[0].Name == "top/goneā.txt",
                    string.Join(",", missing.Select(p => p.Name)));
                Check("a locked file's message is matched by its lookalike",
                    BsdTar.MatchPlan("top/aab.txt", plan.Where(p => p.Name != "top/aab.txt").ToList())?.Name == "top/aāb.txt");
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// Locked files are found before bsdtar runs, so one run makes the
        /// archive. Each run found one more and made it all again: five locked
        /// files out of fifty were six compressions.
        /// </summary>
        private static async Task LockedUpFrontTests()
        {
            var dir = NewDir("lockedfront");
            try
            {
                var top = Path.Combine(dir, "src", "top");
                Directory.CreateDirectory(top);
                foreach (var n in new[] { "a.txt", "aāb.txt", "z.txt" }) File.WriteAllText(Path.Combine(top, n), n);

                var archive = Path.Combine(dir, "one.7z");
                ArchiveResult made;
                using (new FileStream(Path.Combine(top, "aāb.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                    made = await ArchiveEngine.CompressAsync(new[] { top }, archive, ArchiveFormats.SevenZip, ArchiveLevel.Fastest, 2,
                        null, CancellationToken.None);
                Check("a locked ā file is said to be in use, by its real name",
                    made.Failed == 1 && made.Errors.Count == 1 &&
                    made.Errors[0].Contains("top/aāb.txt") && made.Errors[0].Contains("in use"), Said(made));
                var into = Path.Combine(dir, "out-one");
                await Extract(archive, into);
                Check("and the other files are in the archive",
                    File.Exists(Path.Combine(into, "top", "a.txt")) && File.Exists(Path.Combine(into, "top", "z.txt")) &&
                    !File.Exists(Path.Combine(into, "top", "aāb.txt")));

                var many = Path.Combine(dir, "src2", "top");
                Directory.CreateDirectory(many);
                var names = Enumerable.Range(1, 50).Select(i => $"f{i:00}.txt").ToList();
                foreach (var n in names) File.WriteAllText(Path.Combine(many, n), "contents of " + n);
                var locked = new[] { 3, 11, 24, 37, 49 }.Select(i => names[i]).ToList();

                foreach (var format in new[] { ArchiveFormats.SevenZip, ArchiveFormats.TarXz })
                {
                    var path = Path.Combine(dir, "fifty" + format.Extension);
                    var holds = locked.Select(n => new FileStream(Path.Combine(many, n), FileMode.Open, FileAccess.Read,
                        FileShare.None)).ToList();
                    int before = BsdTar.CreateRuns;
                    ArchiveResult result;
                    try
                    {
                        result = await ArchiveEngine.CompressAsync(new[] { many }, path, format, ArchiveLevel.Fastest, 2,
                            null, CancellationToken.None);
                    }
                    finally { foreach (var h in holds) h.Dispose(); }
                    int runs = BsdTar.CreateRuns - before;

                    Check($"{format.Id}: five locked files out of fifty take one tar run", runs == 1, $"{runs} runs");
                    Check($"{format.Id}: and each locked file is said to be in use",
                        result.Failed == 5 && locked.All(n => result.Errors.Any(e => e.Contains("top/" + n) && e.Contains("in use"))),
                        Said(result));
                    var back = Path.Combine(dir, "out-fifty-" + format.Id);
                    await Extract(path, back);
                    var got = Directory.Exists(Path.Combine(back, "top"))
                        ? Directory.GetFiles(Path.Combine(back, "top")).Select(Path.GetFileName).ToList()
                        : new List<string?>();
                    Check($"{format.Id}: and the other forty-five are in it",
                        got.Count == 45 && names.Except(locked).All(n => got.Contains(n)), $"{got.Count} files");
                }
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// Six locked files in a folder of ten. bsdtar gives up on the rest of a
        /// folder after each one, so it takes seven attempts; five kept only the
        /// first file, and the last four were never mentioned.
        /// </summary>
        private static async Task ManyLockedFilesTests()
        {
            var dir = NewDir("manylocked");
            try
            {
                var top = Path.Combine(dir, "src", "top");
                Directory.CreateDirectory(top);
                var names = Enumerable.Range(1, 10).Select(i => $"f{i:00}.txt").ToList();
                foreach (var n in names) File.WriteAllText(Path.Combine(top, n), "contents of " + n);
                var locked = names.Skip(1).Take(6).ToList();
                var readable = names.Except(locked).ToList();

                foreach (var format in new[] { ArchiveFormats.SevenZip, ArchiveFormats.TarXz })
                {
                    var archive = Path.Combine(dir, "many" + format.Extension);
                    var holds = locked.Select(n => new FileStream(Path.Combine(top, n), FileMode.Open, FileAccess.Read,
                        FileShare.None)).ToList();
                    ArchiveResult made;
                    try
                    {
                        made = await ArchiveEngine.CompressAsync(new[] { top }, archive, format, ArchiveLevel.Fastest, 2,
                            null, CancellationToken.None);
                    }
                    finally { foreach (var h in holds) h.Dispose(); }

                    var into = Path.Combine(dir, "out-" + format.Id);
                    if (File.Exists(archive)) await Extract(archive, into);
                    var got = Directory.Exists(Path.Combine(into, "top"))
                        ? Directory.GetFiles(Path.Combine(into, "top")).Select(Path.GetFileName).ToList()
                        : new List<string?>();
                    Check($"{format.Id}: every readable file of ten is in it, with six locked",
                        readable.All(n => got.Contains(n)), $"holds {string.Join(",", got)}; said {Said(made)}");
                    Check($"{format.Id}: every locked file is named as not added",
                        locked.All(n => made.Errors.Any(e => e.Contains("top/" + n) && e.Contains("not added"))), Said(made));
                    Check($"{format.Id}: and every file is either in it or said",
                        names.All(n => got.Contains(n) || made.Errors.Any(e => e.Contains("top/" + n))), Said(made));
                }

                // A plan line printed in another code page still accounts for its file.
                var plan = new List<ArchiveItem>
                {
                    new("x", "top", 0, true, DateTime.Now, FileAttributes.Directory),
                    new("x", "top/日本語.txt", 1, false, DateTime.Now, FileAttributes.Normal),
                    new("x", "top/plain.txt", 1, false, DateTime.Now, FileAttributes.Normal),
                    new("x", "top/gone.txt", 1, false, DateTime.Now, FileAttributes.Normal),
                };
                var notAdded = BsdTar.NotAdded(plan, new[] { "top", "top/???.txt", "top/plain.txt" });
                Check("a mangled name is matched to its file, and only the file with no line is missing",
                    notAdded.Count == 1 && notAdded[0].Name == "top/gone.txt",
                    string.Join(",", notAdded.Select(p => p.Name)));
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// A file that grows while it is being read is written to the tar as it
        /// was when opened. The empty-archive guard did not count it and threw
        /// the whole archive away.
        /// </summary>
        private static async Task GrowingFileKeepsTarTests()
        {
            var dir = NewDir("grow");
            try
            {
                var source = Path.Combine(dir, "grow.bin");
                var data = new byte[4 * 1024 * 1024];
                new Random(11).NextBytes(data);
                File.WriteAllBytes(source, data);

                // Called on the compressing thread. Each report waits long enough
                // for the next to get past the throttle; the first one with bytes
                // read appends to the file mid-read.
                bool appended = false;
                var sink = new Sink(p =>
                {
                    if (appended) return;
                    if (p.CurrentItem.EndsWith("grow.bin", StringComparison.OrdinalIgnoreCase) && p.BytesDone > 0)
                    {
                        using var more = new FileStream(source, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        more.Write(new byte[1000]);
                        appended = true;
                        return;
                    }
                    Thread.Sleep(ArchiveEngine.ReportMilliseconds + 50);
                });

                var archive = Path.Combine(dir, "grow.tar");
                ArchiveResult? made = null;
                string? thrown = null;
                try { made = await ArchiveEngine.CompressAsync(new[] { source }, archive, ArchiveFormats.Tar,
                        ArchiveLevel.Fastest, 1, sink, CancellationToken.None); }
                catch (Exception ex) { thrown = ex.Message; }

                Check("a file that grew while it was read is appended to, for this test", appended);
                Check("a .tar of a file that grew while it was read is kept",
                    made != null && File.Exists(archive), thrown ?? (made == null ? "" : Said(made)));
                Check("and says it grew",
                    made != null && made.Errors.Any(e => e.Contains("grew")), thrown ?? (made == null ? "" : Said(made)));
                if (made != null && File.Exists(archive))
                {
                    var back = await Extract(archive, Path.Combine(dir, "out"));
                    var file = Path.Combine(dir, "out", "grow.bin");
                    Check("and holds the file as it was when it was opened",
                        File.Exists(file) && File.ReadAllBytes(file).AsSpan().SequenceEqual(data), Said(back));
                }
            }
            finally { Cleanup(dir); }
        }

        private static async Task LongPathTests()
        {
            var dir = NewDir("long");
            try
            {
                var deep = dir;
                for (int i = 0; i < 9; i++) deep = Path.Combine(deep, $"a-rather-long-folder-name-number-{i:00}");
                var top = Path.Combine(deep, "top");
                Directory.CreateDirectory(Path.Combine(top, "inner"));
                File.WriteAllText(Path.Combine(top, "file.txt"), "deep");
                File.WriteAllText(Path.Combine(top, "inner", "x.txt"), "x");

                foreach (var format in new[] { ArchiveFormats.SevenZip, ArchiveFormats.TarXz })
                {
                    var archive = Path.Combine(dir, "long" + format.Extension);
                    string? failure = null;
                    ArchiveResult? made = null;
                    try { made = await ArchiveEngine.CompressAsync(new[] { top }, archive, format, ArchiveLevel.Fastest, 2, null, CancellationToken.None); }
                    catch (Exception ex) { failure = ex.Message; }
                    Check($"{format.Id}: a folder past 260 characters compresses", made != null && made.Failed == 0, failure);

                    var into = Path.Combine(deep, "out-" + format.Id);
                    var back = made == null ? null : await Extract(archive, into);
                    Check($"{format.Id}: and extracts into a folder past 260 characters",
                        back != null && back.Failed == 0 && File.ReadAllText(Path.Combine(into, "top", "inner", "x.txt")) == "x",
                        back == null ? "" : Said(back));
                }

                var links = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExplorerNative", "tarlinks");
                Check("and the short links it used are gone afterwards",
                    !Directory.Exists(links) || !Directory.EnumerateFileSystemEntries(links).Any(e =>
                        DateTime.UtcNow - Directory.GetCreationTimeUtc(e) < TimeSpan.FromMinutes(5)));
            }
            finally { Cleanup(dir); }
        }

        private static async Task DamagedBsdTarTests()
        {
            var dir = NewDir("bsddamage");
            try
            {
                var top = Path.Combine(dir, "src", "top");
                Directory.CreateDirectory(top);
                var rng = new Random(8);
                for (int i = 0; i < 6; i++)
                {
                    var block = new byte[200_000];
                    rng.NextBytes(block);
                    File.WriteAllBytes(Path.Combine(top, $"f{i}.bin"), block);
                }

                foreach (var format in new[] { ArchiveFormats.SevenZip, ArchiveFormats.TarXz })
                {
                    var archive = Path.Combine(dir, "whole" + format.Extension);
                    await ArchiveEngine.CompressAsync(new[] { top }, archive, format, ArchiveLevel.Fastest, 2, null, CancellationToken.None);
                    var bytes = File.ReadAllBytes(archive);
                    var cut = Path.Combine(dir, "cut" + format.Extension);
                    File.WriteAllBytes(cut, bytes[..(bytes.Length * 6 / 10)]);

                    var into = Path.Combine(dir, "out-" + format.Id);
                    ArchiveResult? result = null;
                    string? thrown = null;
                    try { result = await Extract(cut, into); }
                    catch (Exception ex) { thrown = ex.Message; }
                    var said = result == null ? thrown ?? "" : Said(result);
                    Check($"truncated {format.Id}: says the archive is damaged or incomplete, not \"(null)\"",
                        said.Contains("damaged or incomplete") && !said.Contains("(null)"), said);
                    Check($"truncated {format.Id}: no staging folder is left", NoStaging(into));
                }
            }
            finally { Cleanup(dir); }
        }

        private static async Task BsdTarProgressTests()
        {
            var dir = NewDir("bsdprog");
            try
            {
                // Non-ASCII names print in another code page and match nothing in
                // the plan, which is what took the bar past 100 percent.
                var top = Path.Combine(dir, "src", "top");
                Directory.CreateDirectory(top);
                var block = new byte[300_000];
                new Random(3).NextBytes(block);
                for (int i = 0; i < 12; i++) File.WriteAllBytes(Path.Combine(top, $"日本語 {i} 🎵.bin"), block);
                File.WriteAllBytes(Path.Combine(top, "small.txt"), new byte[10]);

                var seen = new List<TransferProgress>();
                var archive = Path.Combine(dir, "p.7z");
                await ArchiveEngine.CompressAsync(new[] { top }, archive, ArchiveFormats.SevenZip, ArchiveLevel.Fastest, 2,
                    new Sink(p => seen.Add(p)), CancellationToken.None);
                Check("7z progress never goes past 100 percent",
                    seen.All(p => p.BytesDone <= p.BytesTotal),
                    string.Join(", ", seen.Where(p => p.BytesDone > p.BytesTotal).Select(p => $"{p.BytesDone}/{p.BytesTotal}")));

                var extracting = new List<TransferProgress>();
                var back = await Extract(archive, Path.Combine(dir, "out"), progress: new Sink(p => extracting.Add(p)));
                var last = extracting.LastOrDefault();
                Check("7z extraction needs no listing pass and its progress ends on the whole of it",
                    back.Failed == 0 && last != null && last.BytesDone == last.BytesTotal && last.BytesTotal > 0 &&
                    Directory.GetFiles(Path.Combine(dir, "out", "top")).Length == 13,
                    last == null ? Said(back) : $"{last.BytesDone}/{last.BytesTotal}; {Said(back)}");
            }
            finally { Cleanup(dir); }
        }

        // ---------- Round 2 ----------

        /// <summary>
        /// Every chosen file locked by another program: the old archive stays,
        /// and a new one is not made empty.
        /// </summary>
        private static async Task NothingReadableKeepsOldArchiveTests()
        {
            var dir = NewDir("allLocked");
            try
            {
                var formats = new List<ArchiveFormat> { ArchiveFormats.Zip, ArchiveFormats.Tar, ArchiveFormats.TarGz };
                if (BsdTar.HandlesNames) formats.AddRange(new[] { ArchiveFormats.SevenZip, ArchiveFormats.TarXz });

                foreach (var format in formats)
                {
                    var doc = Path.Combine(dir, $"doc-{format.Id}.txt");
                    File.WriteAllText(doc, "important contents");
                    var archive = Path.Combine(dir, "backup" + format.Extension);
                    await ArchiveEngine.CompressAsync(new[] { doc }, archive, format, ArchiveLevel.Fastest, 2, null, CancellationToken.None);
                    var before = File.ReadAllBytes(archive);

                    string? refused = null;
                    var fresh = Path.Combine(dir, "fresh" + format.Extension);
                    string? refusedFresh = null;
                    using (new FileStream(doc, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        try { await ArchiveEngine.CompressAsync(new[] { doc }, archive, format, ArchiveLevel.Fastest, 2, null, CancellationToken.None); }
                        catch (IOException ex) { refused = ex.Message; }
                        try { await ArchiveEngine.CompressAsync(new[] { doc }, fresh, format, ArchiveLevel.Fastest, 2, null, CancellationToken.None); }
                        catch (IOException ex) { refusedFresh = ex.Message; }
                    }

                    Check($"{format.Id}: re-making it with its only file locked keeps the old archive, and says why",
                        refused != null && File.ReadAllBytes(archive).AsSpan().SequenceEqual(before) &&
                        !Directory.EnumerateFiles(dir, "*creating*").Any(),
                        refused ?? $"{new FileInfo(archive).Length} bytes, was {before.Length}");
                    Check($"{format.Id}: and a new one is not made empty",
                        refusedFresh != null && !File.Exists(fresh), refusedFresh ?? "an archive was made");
                }
            }
            finally { Cleanup(dir); }
        }

        /// <summary>A tar with nothing in it is the two zero blocks, which every reader takes as an empty archive.</summary>
        private static void EmptyTarHasEndBlocksTests()
        {
            using var buffer = new MemoryStream();
            var errors = new List<string>();
            TarEngine.WriteTar(buffer, Array.Empty<ArchiveItem>(), new ArchiveEngine.Reporter(null), errors, CancellationToken.None);
            buffer.Position = 0;
            int listed = -1;
            try { listed = TarEngine.ReadIndex(buffer, CancellationToken.None).Count; } catch { }
            Check("an empty tar ends with its two zero blocks and reads back as empty",
                buffer.Length == 1024 && listed == 0, $"{buffer.Length} bytes, {listed} entries");
        }

        /// <summary>A finished extract ends at 100 percent, whatever it skipped.</summary>
        private static async Task ProgressEndsAtTheEndTests()
        {
            var dir = NewDir("progEnd");
            try
            {
                var src = Path.Combine(dir, "p");
                Directory.CreateDirectory(Path.Combine(src, "sub"));
                var random = new Random(5);
                for (int i = 0; i < 12; i++)
                {
                    var data = new byte[random.Next(1, 200_000)];
                    random.NextBytes(data);
                    File.WriteAllBytes(Path.Combine(src, i % 2 == 0 ? "sub" : "", $"f{i}.bin"), data);
                }

                foreach (var format in new[] { ArchiveFormats.Zip, ArchiveFormats.Tar, ArchiveFormats.TarGz })
                {
                    var archive = Path.Combine(dir, "p" + format.Extension);
                    await ArchiveEngine.CompressAsync(new[] { src }, archive, format, ArchiveLevel.Fastest, 2, null, CancellationToken.None);
                    var into = Path.Combine(dir, "out-" + format.Id);
                    await Extract(archive, into);

                    var seen = new List<TransferProgress>();
                    var again = await Extract(archive, into, PasteConflictPolicy.Skip, new Sink(p => seen.Add(p)));
                    var last = seen.LastOrDefault();
                    Check($"{format.Id}: an extract that skips everything still ends at 100 percent",
                        last != null && last.BytesTotal > 0 && last.BytesDone == last.BytesTotal,
                        last == null ? Said(again) : $"{last.BytesDone}/{last.BytesTotal}");
                }

                // A sparse file into a folder that is not empty, which lists first:
                // its holes are counted in the total and were never in the bar.
                var head = Enumerable.Repeat((byte)'H', 4096).ToArray();
                var tar = Path.Combine(dir, "sparse.tar");
                File.WriteAllBytes(tar, SparseTar("1.0", "disk/holes.bin", 3 * 1024 * 1024, new[] { (0L, head) }));
                var full = Path.Combine(dir, "not-empty");
                Directory.CreateDirectory(full);
                File.WriteAllText(Path.Combine(full, "already.txt"), "x");
                var sparseSeen = new List<TransferProgress>();
                var sparse = await Extract(tar, full, progress: new Sink(p => sparseSeen.Add(p)));
                var end = sparseSeen.LastOrDefault();
                Check("a sparse file extracted after listing ends at 100 percent",
                    end != null && end.BytesTotal > 0 && end.BytesDone == end.BytesTotal,
                    end == null ? Said(sparse) : $"{end.BytesDone}/{end.BytesTotal}");
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// A .gz with something after its end extracts, as gzip does; one cut
        /// short is still refused.
        /// </summary>
        private static async Task GzWithExtraBytesTests()
        {
            var dir = NewDir("gzJunk");
            try
            {
                var data = new byte[3_000_000];
                new Random(4).NextBytes(data);
                foreach (var (name, extra) in new[] { ("junk", Encoding.ASCII.GetBytes("JUNKJUNK")), ("one", new byte[] { 0x1f }) })
                {
                    byte[] gz;
                    using (var memory = new MemoryStream())
                    {
                        using (var zip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true)) zip.Write(data);
                        gz = memory.ToArray();
                    }
                    var path = Path.Combine(dir, name + ".bin.gz");
                    File.WriteAllBytes(path, gz.Concat(extra).ToArray());

                    var into = Path.Combine(dir, "out-" + name);
                    var result = await Extract(path, into);
                    var back = Path.Combine(into, name + ".bin");
                    Check($"a .gz with {extra.Length} extra bytes after its end extracts whole",
                        result.Failed == 0 && File.Exists(back) && File.ReadAllBytes(back).AsSpan().SequenceEqual(data),
                        Said(result));
                }
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// A tar the app starts is in a job that ends it when the app ends, so
        /// a crash or Task Manager does not leave it compressing on its own.
        /// </summary>
        private static async Task TarDiesWithTheAppTests()
        {
            var dir = NewDir("job");
            try
            {
                var src = Path.Combine(dir, "big");
                Directory.CreateDirectory(src);
                var block = new byte[1 << 20];
                var random = new Random(9);
                using (var file = File.Create(Path.Combine(src, "big.bin")))
                    for (int i = 0; i < 96; i++) { random.NextBytes(block); file.Write(block); }

                using var cancel = new CancellationTokenSource();
                var running = ArchiveEngine.CompressAsync(new[] { src }, Path.Combine(dir, "big.tar.xz"), ArchiveFormats.TarXz,
                    ArchiveLevel.Smallest, 2, null, cancel.Token);

                bool inJob = false;
                var clock = Stopwatch.StartNew();
                while (!inJob && !running.IsCompleted && clock.ElapsedMilliseconds < 15_000)
                {
                    foreach (var tar in Process.GetProcessesByName("tar"))
                        using (tar)
                            try { inJob |= BsdTar.InJob(tar); } catch { }
                    if (!inJob) await Task.Delay(50);
                }

                cancel.Cancel();
                try { await running; } catch { }
                Check("tar.exe runs in a job that ends it if the app is killed", inJob);
            }
            finally { Cleanup(dir); }
        }

        /// <summary>The tar chosen is remembered against the DLL's bytes, not only its version string.</summary>
        private static void ChoiceKeyTests()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            var source = dir == null ? "" : File.ReadAllText(Path.Combine(dir.FullName, "src", "BsdTar.cs"));
            Check("the remembered tar choice is keyed on the DLL's size and date as well as its version",
                source.Contains("dllInfo.LastWriteTimeUtc.Ticks") && source.Contains("dllInfo.Length"));
        }

        // ---------- Review of 2026-10-11 ----------

        /// <summary>A stream that takes so many bytes and then says the disk is full.</summary>
        private sealed class FullDisk : Stream
        {
            private readonly Stream _inner;
            private readonly long _room;
            public bool Filled;

            public FullDisk(Stream inner, long room) { _inner = inner; _room = room; }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => true;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => _inner.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count) =>
                Write(new ReadOnlySpan<byte>(buffer, offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (_inner.Position + buffer.Length > _room)
                {
                    Filled = true;
                    throw new IOException("There is not enough space on the disk.");
                }
                _inner.Write(buffer);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner.Dispose();
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// The disk fills while an archive is being re-made over an older one.
        /// The failed write was counted as one unreadable file, the archive went
        /// on without it, and the short result was moved over the good one.
        /// </summary>
        private static async Task DiskFullKeepsOldArchiveTests()
        {
            var dir = NewDir("full");
            try
            {
                var folder = Path.Combine(dir, "photos");
                Directory.CreateDirectory(folder);
                var random = new Random(11);
                for (int i = 0; i < 6; i++)
                {
                    var bytes = new byte[1 << 20];
                    random.NextBytes(bytes);
                    File.WriteAllBytes(Path.Combine(folder, $"p{i}.jpg"), bytes);
                }

                var archive = Path.Combine(dir, "backup.zip");
                var small = Path.Combine(dir, "small.txt");
                File.WriteAllText(small, "the good archive");
                await ArchiveEngine.CompressAsync(new[] { small }, archive, ArchiveFormats.Zip,
                    ArchiveLevel.Balanced, 2, null, CancellationToken.None);
                var good = File.ReadAllBytes(archive);

                FullDisk? disk = null;
                ZipEngine.OutputForTests = s => disk = new FullDisk(s, 5L << 19);
                Exception? thrown = null;
                ArchiveResult? result = null;
                try
                {
                    result = await ArchiveEngine.CompressAsync(new[] { folder }, archive, ArchiveFormats.Zip,
                        ArchiveLevel.Balanced, 2, null, CancellationToken.None);
                }
                catch (Exception ex) { thrown = ex; }
                finally { ZipEngine.OutputForTests = null; }

                Check("disk full: the full disk was reached", disk?.Filled == true);
                Check("disk full: it is a failure, not an archive with a file missing", thrown != null,
                    result == null ? "" : $"made {result.Items} items, {result.Failed} failed: {Said(result)}");
                Check("disk full: the archive it was replacing is exactly as it was",
                    File.ReadAllBytes(archive).AsSpan().SequenceEqual(good));
                Check("disk full: and nothing half-made is left beside it",
                    Directory.GetFiles(dir, "*.creating-*").Length == 0,
                    string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));

                // A file that cannot be read is still only that file.
                var locked = Path.Combine(folder, "p0.jpg");
                using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var skipped = await ArchiveEngine.CompressAsync(new[] { folder }, archive, ArchiveFormats.Zip,
                        ArchiveLevel.Balanced, 2, null, CancellationToken.None);
                    Check("disk full: a file that cannot be read still only costs that file",
                        skipped.Failed == 1 && skipped.Items == 6, $"items={skipped.Items} failed={skipped.Failed}");
                }
                using (var zip = ZipFile.OpenRead(archive))
                    Check("disk full: and the archive holds the rest",
                        zip.Entries.Count(e => e.Name.Length > 0) == 5, zip.Entries.Count.ToString());
            }
            finally { ZipEngine.OutputForTests = null; Cleanup(dir); }
        }

        /// <summary>
        /// A tar whose sink fails part way through an entry. Its header was down,
        /// the failure was counted against the file, and the next entry was
        /// written straight after half of one.
        /// </summary>
        private static void TarStopsOnAFailedWriteTests()
        {
            var dir = NewDir("tarfull");
            try
            {
                var plan = new List<ArchiveItem>();
                var random = new Random(12);
                for (int i = 0; i < 4; i++)
                {
                    var path = Path.Combine(dir, $"f{i}.bin");
                    var bytes = new byte[256 * 1024];
                    random.NextBytes(bytes);
                    File.WriteAllBytes(path, bytes);
                    plan.Add(new ArchiveItem(path, $"f{i}.bin", bytes.Length, false, DateTime.Now, FileAttributes.Normal));
                }

                var errors = new List<string>();
                using var sink = new FullDisk(new MemoryStream(), 600 * 1024);
                Exception? thrown = null;
                try
                {
                    TarEngine.WriteTar(sink, plan, new ArchiveEngine.Reporter(null), errors, CancellationToken.None);
                }
                catch (Exception ex) { thrown = ex; }

                Check("tar: a write that fails ends the archive rather than skipping the file",
                    thrown is IOException, thrown?.GetType().Name ?? $"carried on; errors: {string.Join(" | ", errors)}");

                // And a file that cannot be opened is still only that file.
                var gone = plan.Append(new ArchiveItem(Path.Combine(dir, "missing.bin"), "missing.bin", 5, false,
                    DateTime.Now, FileAttributes.Normal)).ToList();
                var fine = new List<string>();
                using var roomy = new MemoryStream();
                int written = TarEngine.WriteTar(roomy, gone, new ArchiveEngine.Reporter(null), fine, CancellationToken.None);
                Check("tar: a file that cannot be opened is skipped and the rest written",
                    written == 4 && fine.Count == 1, $"written={written} errors={string.Join(" | ", fine)}");
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// "report." beside "report". Through a plain path every call reached the
        /// one without the dot, so the archive held the sibling's content under
        /// this one's name.
        /// </summary>
        private static async Task TrailingDotSourceTests()
        {
            var dir = NewDir("dot");
            try
            {
                File.WriteAllText(Path.Combine(dir, "report"), "PLAIN");
                File.WriteAllText(NameRules.LiteralPath(Path.Combine(dir, "report.")), "WITH THE DOT");
                var folder = Path.Combine(dir, "fold.");
                Directory.CreateDirectory(NameRules.LiteralPath(folder));
                Directory.CreateDirectory(Path.Combine(dir, "fold"));
                File.WriteAllText(NameRules.LiteralPath(Path.Combine(folder, "inside.txt")), "IN THE DOTTED ONE");
                File.WriteAllText(Path.Combine(dir, "fold", "inside.txt"), "IN THE SIBLING");

                foreach (var format in new[] { ArchiveFormats.Zip, ArchiveFormats.Tar })
                {
                    var archive = Path.Combine(dir, "out" + format.Extension);
                    var made = await ArchiveEngine.CompressAsync(
                        new[] { Path.Combine(dir, "report."), folder }, archive, format,
                        ArchiveLevel.Balanced, 2, null, CancellationToken.None);

                    var contents = new Dictionary<string, string>();
                    if (format == ArchiveFormats.Zip)
                    {
                        using var zip = ZipFile.OpenRead(archive);
                        foreach (var e in zip.Entries.Where(e => e.Name.Length > 0))
                            using (var r = new StreamReader(e.Open())) contents[e.FullName] = r.ReadToEnd();
                    }
                    else
                    {
                        using var file = File.OpenRead(archive);
                        using var reader = new TarReader(file);
                        while (reader.GetNextEntry() is { } e)
                            if (e.DataStream != null)
                                using (var r = new StreamReader(e.DataStream)) contents[e.Name] = r.ReadToEnd();
                    }

                    Check($"{format.Id}: a name ending in a dot holds its own content",
                        contents.GetValueOrDefault("report.") == "WITH THE DOT",
                        string.Join(" | ", contents.Select(kv => $"{kv.Key}={kv.Value}")) + " " + Said(made));
                    Check($"{format.Id}: and so does a file in a folder ending in a dot",
                        contents.GetValueOrDefault("fold./inside.txt") == "IN THE DOTTED ONE",
                        string.Join(" | ", contents.Select(kv => $"{kv.Key}={kv.Value}")));
                }
            }
            finally
            {
                try { Directory.Delete(NameRules.LiteralPath(dir), recursive: true); } catch { }
            }
        }

        /// <summary>
        /// Re-making an archive that is read-only. The move over it was refused
        /// after the whole archive had been built.
        /// </summary>
        private static async Task ReadOnlyArchiveReplacedTests()
        {
            var dir = NewDir("ro");
            try
            {
                var source = Path.Combine(dir, "a.txt");
                File.WriteAllText(source, "first");
                var archive = Path.Combine(dir, "a.zip");
                await ArchiveEngine.CompressAsync(new[] { source }, archive, ArchiveFormats.Zip,
                    ArchiveLevel.Balanced, 2, null, CancellationToken.None);
                File.SetAttributes(archive, FileAttributes.ReadOnly);

                File.WriteAllText(source, "second, and longer");
                Exception? thrown = null;
                try
                {
                    await ArchiveEngine.CompressAsync(new[] { source }, archive, ArchiveFormats.Zip,
                        ArchiveLevel.Balanced, 2, null, CancellationToken.None);
                }
                catch (Exception ex) { thrown = ex; }

                string inside = "";
                try
                {
                    using var zip = ZipFile.OpenRead(archive);
                    using var r = new StreamReader(zip.Entries.Single().Open());
                    inside = r.ReadToEnd();
                }
                catch { }
                Check("a read-only archive is replaced", thrown == null && inside == "second, and longer",
                    thrown?.Message ?? inside);
            }
            finally
            {
                foreach (var f in Directory.GetFiles(dir)) try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Cleanup(dir);
            }
        }

        /// <summary>Each extraction lane holds the whole central directory, so a huge zip gets fewer.</summary>
        private static void ExtractionLaneCapTests()
        {
            Check("lanes: a small zip may have as many as asked for", ZipEngine.LanesFor(1000) >= 64);
            Check("lanes: a million entries get one", ZipEngine.LanesFor(1_000_000) == 1);
            Check("lanes: and never none", ZipEngine.LanesFor(5_000_000) == 1 && ZipEngine.LanesFor(0) >= 1);
        }
    }

    // ---- merged from Sweep500ArchiveTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

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

    // ---- merged from Sweep500Archive2Tests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// The 5.0.0 sweep of archives opened as folders and the tar reader.
    /// </summary>
    internal static class Sweep500Archive2Tests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, archives opened as folders and the tar reader:");
            TreeWithWriteFailuresIsPartialNotKept();
            IncrementalTarFoldersAreFolders();
            ReadFailureIsNotDamage("tar");
            ReadFailureIsNotDamage("tar.gz");
            OneMemberOfATarSkipsTheRest();
            await Task.CompletedTask;
        }

        /// <summary>Passes everything through and counts the bytes read.</summary>
        private sealed class CountingStream : Stream
        {
            private readonly Stream _inner;
            public long BytesRead;
            public CountingStream(Stream inner) => _inner = inner;
            public override bool CanRead => true;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
            public override int Read(Span<byte> buffer)
            {
                int got = _inner.Read(buffer);
                Interlocked.Add(ref BytesRead, got);
                return got;
            }
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // Bug 4: Enter on a small file in a big .tar read the whole archive,
        // because the stream under TarReader said it could not seek.
        private static void OneMemberOfATarSkipsTheRest()
        {
            var dir = NewTemp("seek");
            try
            {
                var path = Path.Combine(dir, "backup.tar");
                var readme = new byte[2048];
                new Random(4).NextBytes(readme);
                using (var file = File.Create(path))
                using (var writer = new TarWriter(file, TarEntryFormat.Pax))
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "readme.txt") { DataStream = new MemoryStream(readme) });
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "big.bin") { DataStream = new MemoryStream(new byte[64 * 1024 * 1024]) });
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "after.txt") { DataStream = new MemoryStream(readme) });
                }

                var dest = Path.Combine(dir, "out");
                Directory.CreateDirectory(dest);
                var errors = new List<string>();
                long read;
                using (var counting = new CountingStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                {
                    TarEngine.Extract(counting, path, Format("tar"),
                        ExtractionRules.Selecting(dest, ArchiveBrowser.SelectionMap(new[] { "readme.txt" })), 2,
                        new ArchiveEngine.Reporter(null), errors, CancellationToken.None, estimate: true);
                    read = counting.BytesRead;
                }

                Equal("archives2 seek: one member comes out without complaint", "", string.Join(" | ", errors));
                var got = Path.Combine(dest, "readme.txt");
                Check("archives2 seek: the chosen file arrives whole", File.Exists(got) && File.ReadAllBytes(got).AsSpan().SequenceEqual(readme));
                Check("archives2 seek: files not chosen stay in the archive", !File.Exists(Path.Combine(dest, "big.bin")));
                Check("archives2 seek: the big file not chosen is jumped over, not read", read < 4 * 1024 * 1024, $"{read:N0} bytes read");

                // Everything still comes out when everything is asked for.
                var all = Path.Combine(dir, "all");
                Directory.CreateDirectory(all);
                var errors2 = new List<string>();
                TarEngine.Extract(path, Format("tar"), ExtractionRules.Selecting(all, n => n), 2,
                    new ArchiveEngine.Reporter(null), errors2, CancellationToken.None, estimate: true);
                Check("archives2 seek: a whole extraction still gives every file",
                    errors2.Count == 0 && new FileInfo(Path.Combine(all, "big.bin")).Length == 64L * 1024 * 1024 &&
                    File.ReadAllBytes(Path.Combine(all, "after.txt")).AsSpan().SequenceEqual(readme),
                    string.Join(" | ", errors2));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>Eight 300KB files, a 12MB one (past the inline limit, so it goes through a pipe), then four more.</summary>
        private static void WriteMixedTar(string path, bool gzip)
        {
            var rng = new Random(500);
            using var file = File.Create(path);
            using Stream outer = gzip ? new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest) : file;
            using var writer = new TarWriter(outer, TarEntryFormat.Pax, leaveOpen: true);
            void Add(string name, int size)
            {
                var data = new byte[size];
                rng.NextBytes(data);
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
            }
            for (int i = 0; i < 8; i++) Add($"a{i}.bin", 300 * 1024);
            Add("big.bin", 12 * 1024 * 1024);
            for (int i = 0; i < 4; i++) Add($"z{i}.bin", 300 * 1024);
        }

        // Bug 3: a share that went away (here, a range another handle locked)
        // was said as "This archive is damaged or incomplete".
        private static void ReadFailureIsNotDamage(string id)
        {
            var dir = NewTemp("readfail");
            try
            {
                var path = Path.Combine(dir, "backup." + id);
                WriteMixedTar(path, gzip: id == "tar.gz");
                long len = new FileInfo(path).Length;
                using (var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    holder.Lock(len / 2, len - len / 2);

                    var dest = Path.Combine(dir, "out");
                    Directory.CreateDirectory(dest);
                    var errors = new List<string>();
                    int done = 0;
                    try
                    {
                        done = TarEngine.Extract(path, Format(id), ExtractionRules.Selecting(dest, n => n), 2,
                            new ArchiveEngine.Reporter(null), errors, CancellationToken.None);
                    }
                    catch (Exception ex) { errors.Add("threw " + ex.GetType().Name + ": " + ex.Message); }
                    var said = string.Join(" | ", errors);
                    Check($"archives2 read failure ({id}): extracting says the archive could not be read",
                        errors.Any(e => e.Contains("could not be read", StringComparison.OrdinalIgnoreCase) &&
                                        e.Contains("locked", StringComparison.OrdinalIgnoreCase)), said);
                    Check($"archives2 read failure ({id}): the reason does not repeat the archive's path",
                        !said.Contains(dir, StringComparison.OrdinalIgnoreCase), said);
                    Check($"archives2 read failure ({id}): extracting does not call it damaged",
                        !said.Contains("damaged", StringComparison.OrdinalIgnoreCase), said);
                    Check($"archives2 read failure ({id}): what came before was saved", done > 0 && File.Exists(Path.Combine(dest, "a0.bin")),
                        done.ToString());

                    string opened;
                    try
                    {
                        var listing = ArchiveBrowser.Open(path, null, CancellationToken.None);
                        opened = listing.Damaged ? "listed as damaged" : "listed whole";
                    }
                    catch (InvalidDataException ex) { opened = "damaged: " + ex.Message; }
                    catch (IOException ex) { opened = "io: " + ex.Message; }
                    Check($"archives2 read failure ({id}): browsing says the read failed, not damage",
                        opened.StartsWith("io: ", StringComparison.Ordinal) && opened.Contains("locked", StringComparison.OrdinalIgnoreCase),
                        opened);

                    holder.Unlock(len / 2, len - len / 2);
                }

                // The control: a truncated copy is still damage.
                var cut = Path.Combine(dir, "cut." + id);
                var bytes = File.ReadAllBytes(path);
                File.WriteAllBytes(cut, bytes.AsSpan(0, bytes.Length / 2).ToArray());
                var dest2 = Path.Combine(dir, "out2");
                Directory.CreateDirectory(dest2);
                var errors2 = new List<string>();
                TarEngine.Extract(cut, Format(id), ExtractionRules.Selecting(dest2, n => n), 2,
                    new ArchiveEngine.Reporter(null), errors2, CancellationToken.None);
                Check($"archives2 read failure ({id}): a truncated archive is still damaged",
                    errors2.Any(e => e.StartsWith("This archive is damaged or incomplete", StringComparison.Ordinal)),
                    string.Join(" | ", errors2));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static ArchiveFormat Format(string id) => ArchiveFormats.All.First(f => f.Id == id);

        private static string NewTemp(string what)
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-sweep500a2-" + what + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Changes one header's type flag and puts its checksum right, for types TarWriter will not write.</summary>
        private static void Retype(byte[] tar, string name, char type)
        {
            for (int at = 0; at + 512 <= tar.Length; at += 512)
            {
                int len = Array.IndexOf(tar, (byte)0, at, 100) - at;
                if (len <= 0 || System.Text.Encoding.ASCII.GetString(tar, at, len) != name) continue;
                tar[at + 156] = (byte)type;
                for (int i = 148; i < 156; i++) tar[at + i] = (byte)' ';
                int sum = 0;
                for (int i = 0; i < 512; i++) sum += tar[at + i];
                var octal = System.Text.Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0'));
                Array.Copy(octal, 0, tar, at + 148, 6);
                tar[at + 154] = 0;
                tar[at + 155] = (byte)' ';
                return;
            }
            throw new InvalidOperationException("no header " + name);
        }

        // Bug 2: GNU incremental tar's dumpdir folders ('D') were files that hid
        // everything under them, and a volume label ('V') was a file that failed.
        private static void IncrementalTarFoldersAreFolders()
        {
            var ms = new MemoryStream();
            using (var writer = new TarWriter(ms, TarEntryFormat.Gnu, leaveOpen: true))
            {
                writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "label"));
                writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "home/"));
                writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "home/empty/"));
                writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "home/conner/notes.txt")
                    { DataStream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("hello notes")) });
            }
            var bytes = ms.ToArray();
            Retype(bytes, "label", 'V');
            Retype(bytes, "home/", 'D');
            Retype(bytes, "home/empty/", 'D');

            var index = TarEngine.ReadIndex(new MemoryStream(bytes), CancellationToken.None);
            Check("archives2 dumpdir: a 'D' entry is listed as a folder",
                index.Any(e => e.Name == "home/" && e.IsDirectory),
                string.Join(", ", index.Select(e => $"{e.Name}={e.IsDirectory}")));
            Check("archives2 dumpdir: a volume label is not listed", index.All(e => e.Name != "label"));

            var dir = NewTemp("dumpdir");
            try
            {
                var path = Path.Combine(dir, "home.tar");
                File.WriteAllBytes(path, bytes);
                var listing = ArchiveListing.Build(path, Format("tar"), null, path, index, CancellationToken.None);
                Check("archives2 dumpdir: the browsed folder is a folder", listing.IsFolder("home"));
                Check("archives2 dumpdir: files under it are shown", listing.Find("home/conner/notes.txt") != null);
                Equal("archives2 dumpdir: nothing is hidden as unsafe", "0", listing.Unsafe.ToString());

                var dest = Path.Combine(dir, "out");
                Directory.CreateDirectory(dest);
                var errors = new List<string>();
                TarEngine.Extract(path, Format("tar"), ExtractionRules.Selecting(dest, n => n), 2,
                    new ArchiveEngine.Reporter(null), errors, CancellationToken.None);
                Equal("archives2 dumpdir: extracting reports no failures", "", string.Join(" | ", errors));
                Check("archives2 dumpdir: an empty dumpdir folder is made", Directory.Exists(Path.Combine(dest, "home", "empty")));
                Check("archives2 dumpdir: the file arrives", File.Exists(Path.Combine(dest, "home", "conner", "notes.txt")));
                Check("archives2 dumpdir: no file is made for the volume label", !File.Exists(Path.Combine(dest, "label")));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static string SourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("src not found above the test exe");
        }

        // Bug 1: files that could not be written into the browse cache left an
        // incomplete tree kept as whole, or a full disk under 7-Zip kept as damaged.
        private static void TreeWithWriteFailuresIsPartialNotKept()
        {
            var write = ArchiveBrowser.JudgeTree(new[] { "big.bin: Write failed", "other.bin: Write failed" });
            Check("archives2 tree: a failed write makes the tree partial", write.Lost != null && !write.Damaged,
                $"{write.Damaged} {write.Lost}");
            Check("archives2 tree: the partial reason counts the files", write.Lost?.Contains("2 items") == true,
                write.Lost);

            var space = ArchiveBrowser.JudgeTree(new[]
            {
                "a.bin: the archive is damaged at this point",
                "There isn't enough space on the disk",
            });
            Check("archives2 tree: 7-Zip short of space is not damage", !space.Damaged);
            Equal("archives2 tree: 7-Zip short of space says so", "There isn't enough space on the disk", space.Lost ?? "");

            var named = ArchiveBrowser.JudgeTree(new[] { "a.bin: the archive is damaged at this point", "Cannot open output file : Access is denied." });
            Check("archives2 tree: a file 7-Zip named without damage is lost, not damage", !named.Damaged && named.Lost != null);

            foreach (var note in new[]
                     {
                         "link: a symbolic link was not extracted",
                         "2 items in the archive repeated a name already used (names that differ only in capitals are one name here); the first of each was kept",
                         "Damaged photos/link: a symbolic link was not extracted",
                     })
            {
                var j = ArchiveBrowser.JudgeTree(new[] { note });
                Check($"archives2 tree: a note keeps the tree whole and undamaged ({note[..12]})", !j.Damaged && j.Lost == null,
                    $"{j.Damaged} {j.Lost}");
            }

            var real = ArchiveBrowser.JudgeTree(new[] { "f: the archive is damaged at this point", ArchiveEngine.DamagedMessage(3) });
            Check("archives2 tree: real damage is still damage", real.Damaged && real.Lost == null);
            Check("archives2 tree: damage with nothing extracted is damage",
                ArchiveBrowser.JudgeTree(new[] { ArchiveEngine.DamagedMessage(0) }).Damaged);

            var source = File.ReadAllText(Path.Combine(SourceRoot(), "src", "ArchiveBrowser.cs"));
            Check("archives2 tree: the cache marker is written only for a whole tree",
                source.Contains("if (partial == null) File.WriteAllText(finished", StringComparison.Ordinal));
            Check("archives2 tree: a lost file makes the browse partial",
                source.Contains("judged.Lost", StringComparison.Ordinal));
        }
    }

    // ---- merged from Release510ArchivesTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

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
            return File.Exists(path) ? SourceFiles.Read(path) : "";
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

                // A wrong password is said clearly and asked again.
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

                // Cancel stops silently and leaves no folder.
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
