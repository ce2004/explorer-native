using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// 4.2.5, "use 100 percent but don't freeze the PC": every core, below
    /// normal priority, memory sized from what is free; gzip inflated on every
    /// core; big tar entries written beside the reading; the zip writer's lanes
    /// freed as the work ends. Each test is on real bytes, and every faster
    /// path is checked against the plain one it replaces.
    /// </summary>
    internal static class ArchiveSpeedTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        public static async Task RunAll(Action<string, bool, string?> check)
        {
            _check = check;
            CrcTests();
            MachineTests();
            await PriorityTests();
            GzipReaderTests();
            await TarGzRoundTripTests();
            await BigTarEntryTests();
            await ZipSameBytesTests();
            BigFirstTests();
            await ManyRenamedTests();
            PreallocationTests();
        }

        /// <summary>
        /// Big files have their space asked for up front. A header that claims
        /// more than any disk has must not turn into "not enough space": the
        /// file is written as it always was and comes out its real size.
        /// </summary>
        private static void PreallocationTests()
        {
            var dir = NewDir("prealloc");
            try
            {
                var target = Path.Combine(dir, "big.bin");
                var data = Words(3 * 1024 * 1024, 30);
                ArchiveEngine.WriteCommitted(target, output => output.Write(data), bufferSize: 0, expectedLength: data.Length);
                Check("a preallocated file comes out its real size", new FileInfo(target).Length == data.Length);

                var liar = Path.Combine(dir, "liar.bin");
                ArchiveEngine.WriteCommitted(liar, output => output.Write(data, 0, 5000), bufferSize: 0,
                    expectedLength: 1L << 52);
                Check("a size no disk has is not an error", File.Exists(liar) && new FileInfo(liar).Length == 5000);

                var shorter = Path.Combine(dir, "shorter.bin");
                ArchiveEngine.WriteCommitted(shorter, output => output.Write(data, 0, 2_000_000), bufferSize: 0,
                    expectedLength: data.Length);
                Check("a file shorter than promised is its own length, not the promise",
                    new FileInfo(shorter).Length == 2_000_000);
                Check("and nothing temporary is left", !Directory.EnumerateFiles(dir, ".extracting-*").Any());
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>
        /// Forty items renamed on the way out were announced as 32: the count
        /// came from the names kept for the sentence, which stop at 32.
        /// </summary>
        private static async Task ManyRenamedTests()
        {
            var dir = NewDir("renamed");
            try
            {
                var src = Path.Combine(dir, "src");
                Directory.CreateDirectory(src);
                var files = new List<string>();
                for (int i = 0; i < 40; i++)
                {
                    var file = Path.Combine(src, $"item {i}.txt");
                    File.WriteAllText(file, "x" + i);
                    files.Add(file);
                }
                var archive = Path.Combine(dir, "many.zip");
                var made = await ArchiveEngine.CompressAsync(files, archive, ArchiveFormats.Zip, ArchiveLevel.Fastest,
                    4, null, CancellationToken.None);
                var back = await ArchiveEngine.ExtractAsync(archive, src, PasteConflictPolicy.AutoRename, null,
                    4, null, CancellationToken.None);
                Check("forty items renamed on the way out are counted as forty",
                    made.Failed == 0 && back.Failed == 0 && back.Collisions.RenamedCount == 40,
                    back.Collisions.RenamedCount.ToString());
                Check("and every one arrived beside the original",
                    File.Exists(Path.Combine(src, "item 39 (2).txt")) && File.ReadAllText(Path.Combine(src, "item 39 (2).txt")) == "x39");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static string NewDir(string tag)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"EN-speed-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Text that compresses but is not one repeated run: words drawn from a small vocabulary.</summary>
        private static byte[] Words(int length, int seed)
        {
            var random = new Random(seed);
            var vocabulary = Enumerable.Range(0, 400)
                .Select(i => new string(Enumerable.Range(0, 2 + random.Next(9)).Select(_ => (char)('a' + random.Next(26))).ToArray()))
                .ToArray();
            var bytes = new byte[length];
            int at = 0;
            while (at < length)
            {
                var word = vocabulary[random.Next(vocabulary.Length)];
                for (int i = 0; i < word.Length && at < length; i++) bytes[at++] = (byte)word[i];
                if (at < length) bytes[at++] = (byte)(random.Next(12) == 0 ? '\n' : ' ');
            }
            return bytes;
        }

        // ---------- The checksum ----------

        /// <summary>
        /// The fast checksum (the ARM instruction, or carry-less multiplication
        /// on x64) against the plain tables, at every length and alignment
        /// round the places it changes method, and carried across calls.
        /// </summary>
        private static void CrcTests()
        {
            var random = new Random(11);
            var data = new byte[5000];
            random.NextBytes(data);

            int wrong = 0;
            string? first = null;
            for (int start = 0; start < 17; start++)
            for (int length = 0; length <= 600 && start + length <= data.Length; length++)
            {
                uint seed = (uint)random.Next() | ((uint)random.Next() << 16);
                var span = data.AsSpan(start, length);
                if (Crc32.Append(seed, span) == Crc32.Table(seed, span)) continue;
                wrong++;
                first ??= $"start {start}, length {length}";
            }
            Check("the fast checksum agrees with the tables at every length and alignment", wrong == 0, first);

            var big = new byte[10_000_019];
            random.NextBytes(big);
            Check("and over megabytes", Crc32.Append(Crc32.Seed, big) == Crc32.Table(Crc32.Seed, big));

            uint carried = Crc32.Seed;
            for (int at = 0; at < big.Length; at += 777_777)
                carried = Crc32.Append(carried, big.AsSpan(at, Math.Min(777_777, big.Length - at)));
            Check("and carried across calls", carried == Crc32.Table(Crc32.Seed, big));

            var nines = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("123456789", 20)));
            uint known = Crc32.Table(Crc32.Seed, nines);
            Check("and the tables still give the published answer", Crc32.Table(Crc32.Seed, "123456789"u8) == 0xCBF43926);
            Check("and a long input of known text agrees", Crc32.Append(Crc32.Seed, nines) == known);
        }

        // ---------- How hard the machine is pushed ----------

        private static void MachineTests()
        {
            Check("archives use every core (4.2.5)", ArchiveWorkers.Threads == Environment.ProcessorCount,
                $"{ArchiveWorkers.Threads} of {Environment.ProcessorCount}");
            Check("and the engine is handed the same number", ArchiveEngine.RecommendedThreads == ArchiveWorkers.Threads);

            Check("headroom is at least 2GB", ArchiveWorkers.Headroom(4L << 30) == 2L << 30);
            Check("and 15 percent on a big machine", ArchiveWorkers.Headroom(100L << 30) == 15L << 30);

            var (total, available) = ArchiveWorkers.Memory();
            Check("Windows says how much memory there is", total > 0 && available > 0 && available <= total,
                $"{total >> 20}MB total, {available >> 20}MB free");
            Check("what archives may take leaves the headroom free",
                ArchiveWorkers.Spare() <= Math.Max(0, available - ArchiveWorkers.Headroom(total)) + (256L << 20));

            Check("the zip budget never falls under 128MB", ArchiveWorkers.ZipBudget(8, 0) == 128L << 20);
            Check("nor passes 2GB", ArchiveWorkers.ZipBudget(64, 100L << 30) == 2L << 30);
            Check("and grows with the workers", ArchiveWorkers.ZipBudget(8, 10L << 30) == 512L << 20,
                ArchiveWorkers.ZipBudget(8, 10L << 30).ToString());
            Check("and shrinks with free memory", ArchiveWorkers.ZipBudget(8, 1L << 30) == 256L << 20,
                ArchiveWorkers.ZipBudget(8, 1L << 30).ToString());

            Check("7-Zip is given at least 256MB", ArchiveWorkers.SevenZipMegabytes(0) == 256);
            Check("and what is spare, in megabytes", ArchiveWorkers.SevenZipMegabytes(6L << 30) == 6144);
            Check("in a switch 7-Zip takes", BsdTar.SevenZipMemory.StartsWith("-mmemuse=", StringComparison.Ordinal) &&
                                              BsdTar.SevenZipMemory.EndsWith("m", StringComparison.Ordinal), BsdTar.SevenZipMemory);

            int cores = ArchiveWorkers.Threads;
            Check("7z at Balanced asks two threads a core (LZMA2 runs them in pairs)",
                BsdTar.SevenZipMakeThreads(ArchiveFormats.SevenZip, ArchiveLevel.Balanced) == Math.Min(128, cores * 2));
            Check("and so does tar.xz at Smallest",
                BsdTar.SevenZipMakeThreads(ArchiveFormats.TarXz, ArchiveLevel.Smallest) == Math.Min(128, cores * 2));
            Check("but Fastest asks one a core",
                BsdTar.SevenZipMakeThreads(ArchiveFormats.SevenZip, ArchiveLevel.Fastest) == cores);
            Check("and so does bzip2", BsdTar.SevenZipMakeThreads(ArchiveFormats.TarBz2, ArchiveLevel.Balanced) == cores);
        }

        private static async Task PriorityTests()
        {
            var inside = await ArchiveWorkers.Run(() => Thread.CurrentThread.Priority);
            Check("archive work runs below normal priority", inside == ThreadPriority.BelowNormal, inside.ToString());
            var (longPriority, pooled) = await ArchiveWorkers.RunLong(() => (Thread.CurrentThread.Priority, Thread.CurrentThread.IsThreadPoolThread));
            Check("and work that waits runs on a thread of its own, below normal",
                longPriority == ThreadPriority.BelowNormal && !pooled, $"{longPriority}, pool {pooled}");

            // Eight writers waiting on a queue must not keep short work from
            // running: the starvation that held tar.gz unpacking to one core.
            using (var gate = new ManualResetEventSlim())
            {
                var waiters = Enumerable.Range(0, Environment.ProcessorCount * 2)
                    .Select(_ => ArchiveWorkers.RunLong(() => gate.Wait(10_000))).ToArray();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var quick = Enumerable.Range(0, Environment.ProcessorCount * 2).Select(i => ArchiveWorkers.Run(() => i)).ToArray();
                bool ran = Task.WaitAll(quick, 2000);
                gate.Set();
                Task.WaitAll(waiters);
                Check("short work runs at once while long waits are held", ran && clock.ElapsedMilliseconds < 2000,
                    $"{clock.ElapsedMilliseconds}ms");
            }

            var thread = Thread.CurrentThread;
            var before = thread.Priority;
            var during = ArchiveWorkers.Lowered(() => thread.Priority);
            Check("lowered while it runs", during == ThreadPriority.BelowNormal || before < ThreadPriority.BelowNormal, during.ToString());
            Check("and put back afterwards (pool threads are shared)", thread.Priority == before, thread.Priority.ToString());

            try { ArchiveWorkers.Lowered<int>(() => throw new InvalidOperationException("x")); }
            catch (InvalidOperationException) { }
            Check("and put back when the work throws", thread.Priority == before, thread.Priority.ToString());

            var made = ArchiveWorkers.NewThread(() => { }, "probe");
            Check("a thread of its own starts below normal", made.Priority == ThreadPriority.BelowNormal && made.IsBackground);
        }

        // ---------- Gzip on every core ----------

        /// <summary>Every byte a stream gives, and the kind of exception that ended it, if one did.</summary>
        private static (byte[] Data, string? Failure) Drain(Stream stream)
        {
            var into = new MemoryStream();
            var buffer = new byte[70_000];   // not a multiple of anything the reader uses
            try
            {
                int got;
                while ((got = stream.Read(buffer, 0, buffer.Length)) > 0) into.Write(buffer, 0, got);
                return (into.ToArray(), null);
            }
            catch (Exception ex) { return (into.ToArray(), ex.GetType().Name); }
        }

        private static (byte[] Data, string? Failure) Plain(byte[] gz)
        {
            using var gzip = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
            return Drain(gzip);
        }

        private static (byte[] Data, string? Failure, int Pieces, bool FellBack) Parallel(byte[] gz, int threads = 8)
        {
            using var reader = new ParallelGZip.Reader(new MemoryStream(gz), threads, CancellationToken.None);
            var (data, failure) = Drain(reader);
            return (data, failure, reader.ParallelPieces, reader.FellBack);
        }

        private static byte[] OurGzip(byte[] data, int threads = 4)
        {
            var into = new MemoryStream();
            using (var writer = new ParallelGZip.Writer(into, threads, CompressionLevel.Fastest, CancellationToken.None, leaveOpen: true))
                writer.Write(data);
            return into.ToArray();
        }

        private static void Same(string what, byte[] gz, bool expectParallel)
        {
            var plain = Plain(gz);
            var fast = Parallel(gz);

            // Where a read fails, how much of the broken member came out first
            // depends on where the reads fell, which is no promise of either:
            // what is broken is thrown away. What came before it must agree.
            int agree = plain.Failure != null && fast.Failure != null
                ? Math.Min(plain.Data.Length, fast.Data.Length)
                : -1;
            bool same = agree < 0
                ? plain.Data.AsSpan().SequenceEqual(fast.Data)
                : plain.Data.AsSpan(0, agree).SequenceEqual(fast.Data.AsSpan(0, agree));
            Check(what + ": the same bytes as GZipStream", same, $"{plain.Data.Length} against {fast.Data.Length}");
            Check(what + ": the same ending", plain.Failure == fast.Failure, $"{plain.Failure ?? "none"} against {fast.Failure ?? "none"}");
            if (expectParallel)
                Check(what + ": inflated in parallel", fast.Pieces > 1 && !fast.FellBack, $"{fast.Pieces} pieces, fell back {fast.FellBack}");
        }

        private static void GzipReaderTests()
        {
            // Five members and a bit: the writer cuts every 2MB.
            var data = Words(ParallelGZip.ChunkBytes * 5 + 12_345, 1);
            var ours = OurGzip(data);
            Same("ParallelGZip's own output", ours, expectParallel: true);
            Check("which really is the input", Parallel(ours).Data.AsSpan().SequenceEqual(data));

            // One thread asks for the ordinary read from the start.
            var single = Parallel(ours, threads: 1);
            Check("one thread reads the ordinary way", single.FellBack && single.Data.AsSpan().SequenceEqual(data));

            // One member, as GZipStream or zlib writes a whole file.
            var one = new MemoryStream();
            using (var gzip = new GZipStream(one, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(data);
            Same("a file of one member", one.ToArray(), expectParallel: false);

            // A member with a name in its header (gzip -c file): not cut there,
            // read the ordinary way from the first piece not believed.
            var named = one.ToArray().ToArray();
            named[3] = 0x08;   // FNAME
            var withName = named.Take(10).Concat(System.Text.Encoding.ASCII.GetBytes("f.txt\0")).Concat(named.Skip(10)).ToArray();
            Same("a member with a name in it, then ours", withName.Concat(ours).ToArray(), expectParallel: false);
            Same("ours, then a member with a name in it", ours.Concat(withName).ToArray(), expectParallel: false);

            // The ten bytes a member starts with, inside a stored member: a cut
            // that is not a boundary, so that piece is not believed.
            var header = new byte[] { 0x1f, 0x8b, 0x08, 0x00, 0, 0, 0, 0, 0, 0x0a };
            var trap = Enumerable.Repeat(header, 3000).SelectMany(h => h.Concat(Words(37, 2))).ToArray();
            var stored = new MemoryStream();
            using (var gzip = new GZipStream(stored, CompressionLevel.NoCompression, leaveOpen: true)) gzip.Write(trap);
            var tricked = Parallel(ours.Concat(stored.ToArray()).Concat(ours).ToArray());
            Check("member headers inside the data do not fool it",
                tricked.Data.AsSpan().SequenceEqual(data.Concat(trap).Concat(data).ToArray()) && tricked.Failure == null,
                $"{tricked.Data.Length} bytes, {tricked.Failure}");

            // Damage and odd endings come out exactly as GZipStream had them.
            Same("cut short in the last member", ours[..^100], expectParallel: false);
            Same("cut short in the middle", ours[..(ours.Length / 2)], expectParallel: false);
            Same("cut inside a trailer", ours[..^3], expectParallel: false);
            var junk = new byte[37];
            new Random(3).NextBytes(junk);
            Same("something after the end", ours.Concat(junk).ToArray(), expectParallel: false);
            var flipped = ours.ToArray();
            flipped[ours.Length / 3] ^= 0x55;
            Same("a damaged byte in the middle", flipped, expectParallel: false);
            Same("an empty file", Array.Empty<byte>(), expectParallel: false);
            Same("an empty input's one member", OurGzip(Array.Empty<byte>()), expectParallel: false);
            Same("exactly one chunk", OurGzip(Words(ParallelGZip.ChunkBytes, 4)), expectParallel: false);

            // Over a stream that cannot be repositioned there is no way back to
            // the ordinary read, so it is the ordinary read from the start.
            using (var reader = new ParallelGZip.Reader(new OneWay(new MemoryStream(ours)), 8, CancellationToken.None))
            {
                var (got, failure) = Drain(reader);
                Check("a stream that cannot seek is read the ordinary way", reader.FellBack && failure == null &&
                                                                           got.AsSpan().SequenceEqual(data));
            }

            // Cancelling stops it.
            using var cts = new CancellationTokenSource();
            using (var reader = new ParallelGZip.Reader(new MemoryStream(ours), 8, cts.Token))
            {
                var buffer = new byte[1000];
                reader.Read(buffer, 0, buffer.Length);
                cts.Cancel();
                bool stopped = false;
                try { while (reader.Read(buffer, 0, buffer.Length) > 0) { } }
                catch (OperationCanceledException) { stopped = true; }
                Check("cancelling stops the parallel read", stopped);
            }
        }

        private sealed class OneWay : Stream
        {
            private readonly Stream _inner;
            public OneWay(Stream inner) => _inner = inner;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // ---------- Through the engine ----------

        private static bool SameTree(string a, string b, out string detail)
        {
            var left = Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(a, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
            var right = Directory.EnumerateFiles(b, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(b, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (!left.SequenceEqual(right)) { detail = $"{left.Count} files against {right.Count}"; return false; }
            foreach (var name in left)
            {
                if (!File.ReadAllBytes(Path.Combine(a, name)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(b, name))))
                {
                    detail = name + " differs";
                    return false;
                }
            }
            detail = $"{left.Count} files";
            return true;
        }

        /// <summary>A folder with small text files, a stored "photo", and big files of both kinds.</summary>
        private static string MakeSource(string dir, int bigBytes)
        {
            var src = Path.Combine(dir, "Source");
            Directory.CreateDirectory(Path.Combine(src, "small"));
            Directory.CreateDirectory(Path.Combine(src, "empty"));
            for (int i = 0; i < 40; i++)
                File.WriteAllBytes(Path.Combine(src, "small", $"note {i}.txt"), Words(500 + i * 997, 10 + i));
            var photo = new byte[300_000];
            new Random(5).NextBytes(photo);
            File.WriteAllBytes(Path.Combine(src, "small", "photo.jpg"), photo);
            File.WriteAllBytes(Path.Combine(src, "log.txt"), Words(bigBytes, 20));
            var noise = new byte[bigBytes];
            new Random(6).NextBytes(noise);
            File.WriteAllBytes(Path.Combine(src, "noise.bin"), noise);
            return src;
        }

        private static async Task TarGzRoundTripTests()
        {
            var dir = NewDir("tgz");
            try
            {
                var src = MakeSource(dir, 12 * 1024 * 1024);
                var archive = Path.Combine(dir, "out.tar.gz");
                var made = await ArchiveEngine.CompressAsync(new[] { src }, archive, ArchiveFormats.TarGz,
                    ArchiveLevel.Fastest, 8, null, CancellationToken.None);
                Check("a tar.gz is made", made.Failed == 0 && File.Exists(archive), string.Join("; ", made.Errors));

                using (var file = File.OpenRead(archive))
                using (var reader = new ParallelGZip.Reader(file, 8, CancellationToken.None))
                {
                    var (_, failure) = Drain(reader);
                    Check("and its members are inflated in parallel", reader.ParallelPieces > 4 && !reader.FellBack && failure == null,
                        $"{reader.ParallelPieces} pieces, fell back {reader.FellBack}");
                }

                var into = Path.Combine(dir, "out");
                var back = await ArchiveEngine.ExtractAsync(archive, into, PasteConflictPolicy.AutoRename, null,
                    8, null, CancellationToken.None);
                Check("and extracts whole", back.Failed == 0, string.Join("; ", back.Errors));
                Check("to the same files, byte for byte", SameTree(src, Path.Combine(into, "Source"), out var detail), detail);
                Check("empty folders included", Directory.Exists(Path.Combine(into, "Source", "empty")));

                // Listing first (into a folder that is not empty) goes through the same reader.
                var listed = TarEngine.List(archive, ArchiveFormats.TarGz, CancellationToken.None);
                Check("and lists every entry", listed.Count(e => !e.IsDirectory) == 43, listed.Count.ToString());

                // A bare .gz of one big file, inflated in parallel and checked by its trailer.
                var one = Path.Combine(dir, "log.txt.gz");
                var gz = await ArchiveEngine.CompressAsync(new[] { Path.Combine(src, "log.txt") }, one, ArchiveFormats.Gz,
                    ArchiveLevel.Fastest, 8, null, CancellationToken.None);
                var outOne = Path.Combine(dir, "one");
                var unpacked = await ArchiveEngine.ExtractAsync(one, outOne, PasteConflictPolicy.AutoRename, null,
                    8, null, CancellationToken.None);
                Check("a bare .gz comes back whole", gz.Failed == 0 && unpacked.Failed == 0 &&
                    File.ReadAllBytes(Path.Combine(outOne, "log.txt")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(src, "log.txt"))),
                    string.Join("; ", unpacked.Errors));

                // Cut short: said, and nothing half-written under its name.
                var cut = Path.Combine(dir, "cut.gz");
                var bytes = File.ReadAllBytes(one);
                File.WriteAllBytes(cut, bytes[..(bytes.Length * 2 / 3)]);
                var outCut = Path.Combine(dir, "cut");
                var damaged = await ArchiveEngine.ExtractAsync(cut, outCut, PasteConflictPolicy.AutoRename, null,
                    8, null, CancellationToken.None);
                Check("a bare .gz cut short is said to be damaged", damaged.Failed > 0 &&
                    damaged.Errors.Any(e => e.Contains("damaged", StringComparison.Ordinal)), string.Join("; ", damaged.Errors));
                Check("and leaves no file under its name", !File.Exists(Path.Combine(outCut, "cut")));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>
        /// Big tar entries are written by a writer while the reader goes on,
        /// through a pipe. The cases that matter: they arrive whole, several
        /// at once, and a big entry cut short is said and never kept.
        /// </summary>
        private static async Task BigTarEntryTests()
        {
            var dir = NewDir("bigtar");
            try
            {
                var src = MakeSource(dir, 20 * 1024 * 1024);
                File.Copy(Path.Combine(src, "noise.bin"), Path.Combine(src, "small", "noise copy.bin"));

                foreach (var format in new[] { ArchiveFormats.Tar, ArchiveFormats.TarGz })
                {
                    var archive = Path.Combine(dir, "out" + format.Extension);
                    var made = await ArchiveEngine.CompressAsync(new[] { src }, archive, format,
                        ArchiveLevel.Fastest, 8, null, CancellationToken.None);
                    var into = Path.Combine(dir, "out-" + format.Id);
                    var back = await ArchiveEngine.ExtractAsync(archive, into, PasteConflictPolicy.AutoRename, null,
                        8, null, CancellationToken.None);
                    Check($"{format.Id}: big entries come out whole", made.Failed == 0 && back.Failed == 0 &&
                        SameTree(src, Path.Combine(into, "Source"), out var detail), string.Join("; ", back.Errors));
                    Check($"{format.Id}: and keep their dates",
                        Math.Abs((File.GetLastWriteTime(Path.Combine(into, "Source", "noise.bin")) -
                                  File.GetLastWriteTime(Path.Combine(src, "noise.bin"))).TotalSeconds) < 2.5);
                    Check($"{format.Id}: and leave no temporary files",
                        !Directory.EnumerateFiles(into, ".extracting-*", SearchOption.AllDirectories).Any());
                }

                // A plain tar cut in the middle of the 20MB noise.bin.
                var tar = Path.Combine(dir, "out.tar");
                var whole = File.ReadAllBytes(tar);
                long at = IndexOf(whole, File.ReadAllBytes(Path.Combine(src, "noise.bin")).AsSpan(0, 4096).ToArray());
                Check("the test can find the big entry", at > 0);
                var cut = Path.Combine(dir, "cut.tar");
                File.WriteAllBytes(cut, whole[..(int)(at + 8 * 1024 * 1024)]);
                var outCut = Path.Combine(dir, "cut");
                var damaged = await ArchiveEngine.ExtractAsync(cut, outCut, PasteConflictPolicy.AutoRename, null,
                    8, null, CancellationToken.None);
                Check("a big entry cut short is said", damaged.Errors.Any(e => e.Contains("noise.bin") &&
                    e.Contains("ends part way", StringComparison.Ordinal)), string.Join("; ", damaged.Errors));
                Check("and the archive is said to be damaged",
                    damaged.Errors.Any(e => e.Contains("damaged or incomplete", StringComparison.Ordinal)), string.Join("; ", damaged.Errors));
                Check("and the half that came is not kept under its name",
                    !File.Exists(Path.Combine(outCut, "Source", "noise.bin")));
                Check("nor left as a temporary file",
                    !Directory.Exists(outCut) || !Directory.EnumerateFiles(outCut, ".extracting-*", SearchOption.AllDirectories).Any());

                // Cancelled part way through: the engine stops and says so.
                using var cts = new CancellationTokenSource();
                var progress = new Progress<TransferProgress>(p => { if (p.BytesDone > 4 * 1024 * 1024) cts.Cancel(); });
                var outStop = Path.Combine(dir, "stop");
                var stopped = await ArchiveEngine.ExtractAsync(tar, outStop, PasteConflictPolicy.AutoRename, null,
                    8, progress, cts.Token);
                // It may finish before the cancel lands on a fast disk; either
                // way it ends cleanly.
                Check("a cancelled extraction says it was cancelled, or finished", stopped.Cancelled || stopped.Failed == 0);
                Check("and leaves no temporary files",
                    !Directory.Exists(outStop) || !Directory.EnumerateFiles(outStop, ".extracting-*", SearchOption.AllDirectories).Any());
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static long IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);

        /// <summary>
        /// One worker and sixteen still make the same zip, now that stored
        /// files are read by workers and the lanes go back as work ends.
        /// </summary>
        private static async Task ZipSameBytesTests()
        {
            var dir = NewDir("zip");
            try
            {
                var src = MakeSource(dir, 70 * 1024 * 1024);
                var plan = ArchiveEngine.Plan(new[] { src }, CancellationToken.None);
                var hashes = new List<string>();
                foreach (int threads in new[] { 1, 3, 16 })
                {
                    var path = Path.Combine(dir, $"t{threads}.zip");
                    var errors = new List<string>();
                    int written = await Task.Run(() => ZipEngine.Create(plan, path, ArchiveLevel.Balanced, threads,
                        new ArchiveEngine.Reporter(null), errors, CancellationToken.None));
                    Check($"zip with {threads} workers writes every entry", written == plan.Count && errors.Count == 0,
                        $"{written} of {plan.Count}; {string.Join("; ", errors)}");
                    using (var stream = File.OpenRead(path)) hashes.Add(Convert.ToHexString(SHA256.HashData(stream)));
                }
                Check("one, three and sixteen workers make the same zip, byte for byte", hashes.Distinct().Count() == 1,
                    string.Join(", ", hashes.Select(h => h[..8])));

                using (var zip = ZipFile.OpenRead(Path.Combine(dir, "t16.zip")))
                {
                    var photo = zip.Entries.First(e => e.Name == "photo.jpg");
                    Check("a small photo is still stored, not deflated", photo.CompressedLength == photo.Length);
                    var note = zip.Entries.First(e => e.Name == "note 39.txt");
                    Check("and a small text file is deflated", note.CompressedLength < note.Length);
                }

                var into = Path.Combine(dir, "out");
                var back = await ArchiveEngine.ExtractAsync(Path.Combine(dir, "t16.zip"), into, PasteConflictPolicy.AutoRename, null,
                    16, null, CancellationToken.None);
                Check("and it extracts to the same files", back.Failed == 0 &&
                    SameTree(src, Path.Combine(into, "Source"), out var detail), string.Join("; ", back.Errors));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static void BigFirstTests()
        {
            var runs = new List<(int Start, int End)> { (0, 5), (5, 6), (6, 9), (9, 10), (10, 12) };
            var bytes = new Dictionary<int, long> { [0] = 1000, [5] = 20L << 20, [6] = 30, [9] = 90L << 20, [10] = 8L << 20 };
            var order = ZipEngine.BigFirst(runs, r => bytes[r.Start]);
            Check("zip extraction hands out the biggest runs first",
                order.Select(r => r.Start).SequenceEqual(new[] { 9, 5, 10, 0, 6 }), string.Join(",", order.Select(r => r.Start)));
            Check("and loses none", order.Count == runs.Count && order.Distinct().Count() == runs.Count);
        }
    }
}
