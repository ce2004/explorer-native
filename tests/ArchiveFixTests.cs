using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ExplorerNative
{
    /// <summary>
    /// Regression tests for the compress-and-extract review of 2026-10-05: one
    /// per finding, each on a real archive built for the case.
    /// </summary>
    internal static class ArchiveFixTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

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

            if (!BsdTar.Available) return;

            await ManyLockedFilesTests();

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
                    if (format.Engine == ArchiveEngineKind.BsdTar && !BsdTar.Available) continue;
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
                // "café.txt" as CP437 writes it, no UTF-8 flag — what bsdtar,
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

                if (BsdTar.Available)
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

                string? said = null;
                try { await Extract(all, Path.Combine(dir, "allout")); }
                catch (NotSupportedException ex) { said = ex.Message; }
                Check("a password-protected zip says so in one sentence",
                    said == ZipEngine.ProtectedMessage, said);
                Check("and makes no folder for it", !Directory.Exists(Path.Combine(dir, "allout")));

                var some = Path.Combine(dir, "some.zip");
                File.WriteAllBytes(some, RawZip(
                    (Encoding.ASCII.GetBytes("open.txt"), 0, Encoding.ASCII.GetBytes("readable")),
                    (Encoding.ASCII.GetBytes("secret.txt"), 1, header)));
                var mixed = await Extract(some, Path.Combine(dir, "someout"));
                Check("a zip with one protected entry extracts the rest and names the protected one",
                    File.Exists(Path.Combine(dir, "someout", "open.txt")) &&
                    !File.Exists(Path.Combine(dir, "someout", "secret.txt")) &&
                    Said(mixed).Contains("secret.txt: it is password-protected"), Said(mixed));
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
                Check("and nothing is moved in under the name bsdtar quietly changed it to",
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
                if (BsdTar.Available) formats.AddRange(new[] { ArchiveFormats.SevenZip, ArchiveFormats.TarXz });

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
    }
}
