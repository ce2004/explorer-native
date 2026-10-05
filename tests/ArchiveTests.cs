using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Compressing and extracting, checked against something other than itself.
    ///
    /// The trap with an archiver is that a writer and a reader written by the
    /// same hand agree with each other about whatever they both get wrong. A
    /// round trip through our own code proves the two halves are consistent and
    /// proves nothing about whether the file is a zip. So every format written
    /// here is also handed to the bsdtar Windows ships — a different
    /// implementation, by different people, that has no idea this application
    /// exists — and the zips are additionally opened with ZipArchive, which is
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
        /// The corpus is chosen for the cases that have broken archivers before —
        /// an empty folder, a zero-byte file, a name that is not in English, a
        /// file whose extension says it is already compressed, and enough
        /// compressible text that the compression is doing something.
        /// </summary>
        private static async Task RoundTripTests()
        {
            foreach (var format in ArchiveFormats.All.Where(f => f.CanCreate && !f.SingleFile))
            {
                if (format.Engine == ArchiveEngineKind.BsdTar && !BsdTar.Available) continue;

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
            // of deflating it — a different path through the writer.
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

                if (!BsdTar.Available)
                {
                    Check("the system tar is present to check against", false, "tar.exe missing");
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
        /// they so much as open — SettingsForm went nine pages without one — and
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
        /// and nothing in this suite could see it — the round trips pass a null
        /// progress sink, and the totals at the end were always correct.
        ///
        /// **One file, deliberately.** With several, the old code still reported
        /// something partway — as each entry was written — so a folder of six
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
        /// cannot be reasoned about — a report is posted to the thread that owns
        /// the window and drawn only when that thread pumps — and "the progress
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
                    // updated from the wrong thread — which is the bug this is
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
                    // a ceiling — the window is allowed to take a moment to draw
                    // the last number, not allowed never to draw it.
                    var settle = Stopwatch.StartNew();
                    while (settle.Elapsed < TimeSpan.FromSeconds(2))
                    {
                        Application.DoEvents();
                        if (percents.LastOrDefault() != percentBox.Text) percents.Add(percentBox.Text);
                        if (items.LastOrDefault() != itemBox.Text) items.Add(itemBox.Text);
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
        /// bsdtar refuses an option it does not recognise and stops — "Undefined
        /// option", exit code 1 — so a name that has drifted is not a setting
        /// quietly ignored, it is a compress that fails outright in front of
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

            if (!BsdTar.Available)
            {
                Check("the system tar is present", false, "tar.exe missing");
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
        /// It has an entry point of its own — MainForm reads three diagnostics
        /// straight off Program — so the two command handlers behind Ctrl+Shift+C
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
                context.Contains("Item(\"&Compress…\", Keys.None, CompressSelection)", StringComparison.Ordinal));

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
            // is made from the name — deciding what goes on a menu must never
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
            // inside — and ShowDialog is exactly what a later edit reaches for
            // out of habit.
            Check("both handlers name the progress window the way the modal check expects",
                Occurrences(main, "using var window = new ProgressForm(") >= 4,
                Occurrences(main, "using var window = new ProgressForm(").ToString());

            // Both are async void, which means an exception has no caller to
            // catch it and goes to Application.ThreadException — the crash log
            // and a dialog, instead of a message.
            foreach (var handler in new[] { "CompressSelection", "ExtractFocused" })
            {
                int at = main.IndexOf("private async void " + handler, StringComparison.Ordinal);
                Check($"{handler} exists and is an event handler", at >= 0);
                if (at < 0) continue;

                int next = main.IndexOf("\n        private ", at + 10, StringComparison.Ordinal);
                var body = next > at ? main[at..next] : main[at..];

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
            // checks the reverse direction already — that everything marked
            // Raised appears in the source — so this closes the loop.
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
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
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
}
