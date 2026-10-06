using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// Tar, gzipped tar, the bare .gz — and, through <see cref="BsdTar"/>, the
    /// reading and writing half of every other format as well.
    ///
    /// A tar is a container with no compression in it at all: entry header, entry
    /// bytes, entry header, entry bytes, to the end. That is why it is paired
    /// with a compressor, and why the pairing is the interesting part. The whole
    /// tar goes through one compressed stream, so unlike a zip there is no
    /// per-entry boundary to hand to a different worker — but there does not need
    /// to be, because <see cref="ParallelGZip"/> cuts the *stream* into members
    /// instead. Every core is busy whether the archive is a hundred thousand
    /// small files or one virtual disk image, which is the case a zip cannot
    /// answer.
    ///
    /// The tar itself is <c>System.Formats.Tar</c>, in the framework since .NET 7.
    /// Written in the PAX format, which is the one that stores a name of any
    /// length, a size above eight gigabytes and a timestamp with more than
    /// seconds in it — the older ustar header has fixed-width octal fields for
    /// all three and silently truncates.
    ///
    /// The three methods at the bottom — <see cref="WriteTar"/>,
    /// <see cref="ReadIndex"/> and <see cref="ReadInto"/> — take a bare stream
    /// rather than a path, and that is what makes them the whole file layer for
    /// 7z, xz, bzip2 and zstandard too. bsdtar is driven as a codec on the other
    /// end of a pipe: it turns an archive into a tar for us to read, or takes a
    /// tar from us and compresses it. Every decision about names, safety,
    /// collisions and progress therefore happens in this code and is tested here,
    /// for every format, instead of being different for the four that Windows
    /// holds the codecs for.
    /// </summary>
    public static class TarEngine
    {
        /// <summary>
        /// The largest entry the reader will buffer to hand to a writer thread.
        ///
        /// Bigger than this is written by the reading thread itself, straight out
        /// of the decompressor. There is nothing to gain by buffering it — one
        /// large file is one sequential write, which a single thread already does
        /// at the speed of the disk — and a great deal to lose, because the buffer
        /// would be the size of the file.
        /// </summary>
        private const long InlineWriteBytes = 8L * 1024 * 1024;

        // ---------- The formats this file owns end to end ----------

        public static int Create(
            IReadOnlyList<ArchiveItem> plan,
            string archivePath,
            ArchiveFormat format,
            ArchiveLevel level,
            int threads,
            ArchiveEngine.Reporter reporter,
            List<string> errors,
            CancellationToken token)
        {
            if (format.SingleFile) return CompressOneFile(plan, archivePath, level, threads, reporter, token);

            var compression = ArchiveEngine.ToCompressionLevel(level);

            using var output = new FileStream(archivePath, FileMode.Create, FileAccess.Write,
                FileShare.None, 1 << 20);

            if (format.Id != "tar.gz") return WriteTar(output, plan, reporter, errors, token);

            // Closed before the file underneath it, so the last members are on
            // disk before the handle goes.
            using var gzip = new ParallelGZip.Writer(output, threads, compression, token, leaveOpen: true);
            return WriteTar(gzip, plan, reporter, errors, token);
        }

        public static IReadOnlyList<ArchiveEntryInfo> List(string archivePath, ArchiveFormat format,
            CancellationToken token)
        {
            // A bare .gz holds one file and no name for it. What comes out is
            // named after the archive with the suffix removed, which is the
            // convention gzip itself uses and the only information there is.
            if (format.SingleFile)
            {
                return new[]
                {
                    new ArchiveEntryInfo(ArchiveFormats.BaseName(archivePath), 0,
                        File.GetLastWriteTime(archivePath), false),
                };
            }

            using var file = OpenFile(archivePath);
            if (format.Id != "tar.gz") return ReadIndex(file, token);

            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return ReadIndex(gzip, token);
        }

        /// <param name="estimate">
        /// Whether the archive was not listed first, so that progress is measured
        /// through the archive file rather than against totals nobody has.
        /// </param>
        public static int Extract(string archivePath, ArchiveFormat format,
            ExtractionRules rules, int threads, ArchiveEngine.Reporter reporter, List<string> errors,
            CancellationToken token, bool estimate = false)
        {
            using var file = OpenFile(archivePath);
            var counted = new ReadCounter(file);

            // A bare .gz has no listing worth the name — one entry, and no size
            // without decompressing it — so it is always measured this way.
            if (estimate || format.SingleFile)
                reporter.Estimate(file.Length, () => counted.Consumed);

            if (format.SingleFile) return ExpandOneFile(archivePath, counted, rules, reporter, errors, token);

            if (format.Id != "tar.gz") return ReadInto(counted, rules, threads, reporter, errors, token);

            // GZipStream reads every member of a multi-member file and joins
            // them, which is what makes anything ParallelGZip writes readable
            // here — and anything pigz writes, and two .gz files concatenated.
            using var gzip = new GZipStream(counted, CompressionMode.Decompress, leaveOpen: true);
            return ReadInto(gzip, rules, threads, reporter, errors, token);
        }

        private static FileStream OpenFile(string path) =>
            new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);

        private static int CompressOneFile(IReadOnlyList<ArchiveItem> plan, string archivePath,
            ArchiveLevel level, int threads, ArchiveEngine.Reporter reporter, CancellationToken token)
        {
            var only = plan.First(p => !p.IsDirectory);
            reporter.Starting(only.Name);

            ParallelGZip.CompressFile(only.Source, archivePath, threads,
                ArchiveEngine.ToCompressionLevel(level), reporter.Advance, token);

            reporter.Finished(0);
            return 1;
        }

        private static int ExpandOneFile(string archivePath, Stream source, ExtractionRules rules,
            ArchiveEngine.Reporter reporter, List<string> errors, CancellationToken token)
        {
            var name = ArchiveFormats.BaseName(archivePath);
            var target = rules.TargetFor(name, isDirectory: false, out var problem);

            if (target == null)
            {
                if (problem != null) errors.Add(problem);
                return 0;
            }

            reporter.Starting(name);

            try
            {
                ArchiveEngine.WriteCommitted(target, output =>
                {
                    using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
                    var buffer = new byte[1 << 20];
                    uint crc = Crc32.Seed;
                    long length = 0;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        int got = gzip.Read(buffer, 0, buffer.Length);
                        if (got <= 0) break;
                        output.Write(buffer, 0, got);
                        crc = Crc32.Append(crc, buffer.AsSpan(0, got));
                        length += got;
                        reporter.Advance(got);
                    }

                    if (!TrailerAgrees(archivePath, output, crc, length, token))
                    {
                        // Something after the end — padding a transfer added, a
                        // signature tacked on — which gzip warns about and
                        // ignores. The file is whole when its checksum and
                        // length sit just before the extra bytes.
                        long extra = TrailingJunk(archivePath, output, crc, length, token);
                        if (extra <= 0) throw new InvalidDataException("the archive ends before the file does");
                        RoboCopyEngine.Trace($"gz: {name} extracted; {extra} bytes after the end were ignored");
                    }
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                // Half a file under the whole file's name, reported as extracted,
                // was what a truncated download produced.
                errors.Add($"This archive is damaged or incomplete, so {name} was not extracted.");
                rules.Withdraw(target);
                reporter.Finished(0);
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{name}: {ex.Message}");
                rules.Withdraw(target);
                reporter.Finished(0);
                return 0;
            }

            reporter.Finished(0);
            return 1;
        }

        /// <summary>
        /// Whether a .gz really ended where it says it did.
        ///
        /// GZipStream does not notice a file cut short: it reads what there is,
        /// returns the end of the stream, and a truncated download extracted as
        /// half a file with nothing said. Every gzip member ends with the CRC-32
        /// and length of what it holds, so the last eight bytes of the file are
        /// the last member's checksum — and in a truncated file they are
        /// compressed data that matches nothing.
        ///
        /// A file of one member is checked against the running figures with no
        /// further reading. One of several — anything ParallelGZip or pigz
        /// wrote, or two files joined — has its last member's output read back
        /// from the end of what was written.
        /// </summary>
        internal static bool TrailerAgrees(string archivePath, Stream output, uint crc, long length,
            CancellationToken token)
        {
            Span<byte> trailer = stackalloc byte[8];
            using (var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                // A header, the smallest deflate block and a trailer.
                if (file.Length < 20) return false;
                file.Position = file.Length - 8;
                file.ReadExactly(trailer);
            }

            uint storedCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(trailer);
            uint storedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]);

            if (storedCrc == crc && storedSize == (uint)length) return true;
            if (storedSize > length || !output.CanRead || !output.CanSeek) return false;

            output.Flush();
            long end = output.Position;
            output.Position = length - storedSize;

            var buffer = new byte[1 << 20];
            uint last = Crc32.Seed;
            long left = storedSize;
            while (left > 0)
            {
                token.ThrowIfCancellationRequested();
                int got = output.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (got <= 0) break;
                last = Crc32.Append(last, buffer.AsSpan(0, got));
                left -= got;
            }

            output.Position = end;
            return left == 0 && last == storedCrc;
        }

        /// <summary>
        /// How many bytes follow the real end of a .gz, or -1 when no complete
        /// member's trailer is found in front of them.
        ///
        /// GZipStream cannot tell "something after the end" from "cut short":
        /// it returns what it decoded either way. A truncated file's last bytes
        /// are compressed data, and a file with extra bytes after it has, just
        /// before them, the CRC-32 and length of what came out — eight bytes no
        /// truncation produces except by a one-in-2^64 accident. Only the last
        /// 64KB are searched, and only four candidates are checked against a
        /// last member shorter than the whole, because that check rereads it.
        /// </summary>
        internal static long TrailingJunk(string archivePath, Stream output, uint crc, long length,
            CancellationToken token)
        {
            byte[] tail;
            using (var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (file.Length < 21) return -1;
                int span = (int)Math.Min(64 * 1024, file.Length - 10);
                tail = new byte[span];
                file.Position = file.Length - span;
                file.ReadExactly(tail);
            }

            int slow = 0;
            for (int i = tail.Length - 9; i >= 0; i--)
            {
                uint storedCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i));
                uint storedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 4));
                long extra = tail.Length - (i + 8);

                if (storedCrc == crc && storedSize == (uint)length) return extra;

                // One of several members: its checksum covers only the end of
                // what came out.
                if (storedSize > 0 && storedSize < length && slow < 4 && output.CanRead && output.CanSeek)
                {
                    slow++;
                    output.Flush();
                    long end = output.Position;
                    output.Position = length - storedSize;
                    var buffer = new byte[1 << 20];
                    uint last = Crc32.Seed;
                    long left = storedSize;
                    while (left > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        int got = output.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                        if (got <= 0) break;
                        last = Crc32.Append(last, buffer.AsSpan(0, got));
                        left -= got;
                    }
                    output.Position = end;
                    if (left == 0 && last == storedCrc) return extra;
                }
            }

            return -1;
        }

        // ---------- The tar layer, over any stream ----------

        /// <summary>
        /// Writes the plan as a tar into <paramref name="sink"/>.
        ///
        /// The sink is a file for a .tar, a parallel gzip writer for a .tar.gz,
        /// and bsdtar's standard input for everything else. It never knows which.
        /// </summary>
        internal static int WriteTar(Stream sink, IReadOnlyList<ArchiveItem> plan,
            ArchiveEngine.Reporter reporter, List<string> errors, CancellationToken token)
        {
            using var writer = new TarWriter(sink, TarEntryFormat.Pax, leaveOpen: true);
            int written = 0;
            bool anyEntry = false;

            foreach (var item in plan)
            {
                token.ThrowIfCancellationRequested();
                reporter.Starting(item.Name);

                try
                {
                    if (item.IsDirectory)
                    {
                        writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, item.Name + "/")
                        {
                            ModificationTime = TarTime(item.Modified),
                        });

                        anyEntry = true;
                        written++;
                        reporter.Finished(0);
                        continue;
                    }

                    using var source = new FileStream(item.Source, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);

                    // Counted on the way past rather than added on at the end, so
                    // a single very large file moves the progress figures while it
                    // is being written instead of jumping when it finishes.
                    using var counted = new CountingStream(source, reporter);

                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, item.Name)
                    {
                        ModificationTime = TarTime(item.Modified),
                        DataStream = counted,
                    });

                    anyEntry = true;
                    reporter.Finished(0);

                    // The entry is exactly as long as its header says whatever the
                    // file did meanwhile — see CountingStream — so the archive
                    // stays readable. What it holds is still not the file.
                    if (counted.Changed != null)
                    {
                        errors.Add($"{item.Name}: {counted.Changed}");
                        continue;
                    }

                    written++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors.Add($"{item.Name}: {ex.Message}");
                    reporter.Finished(0);
                }
            }

            // TarWriter ends the archive with its two zero blocks only if it
            // wrote an entry, so a tar with nothing in it was 0 bytes (20 as a
            // .tar.gz) and every reader, ours included, called it damaged. An
            // empty tar is those two blocks and nothing else.
            if (!anyEntry)
            {
                writer.Dispose();
                sink.Write(new byte[1024]);
            }

            return written;
        }

        /// <summary>Every entry's name, size and date, without extracting anything.</summary>
        internal static IReadOnlyList<ArchiveEntryInfo> ReadIndex(Stream tar, CancellationToken token)
        {
            using var reader = new TarReader(tar, leaveOpen: true);
            var entries = new List<ArchiveEntryInfo>();

            while (true)
            {
                token.ThrowIfCancellationRequested();

                // A damaged or truncated archive ends the listing, not the
                // extraction: it threw "Unable to read beyond the end of the
                // stream" before a single good file had been saved. What was
                // read is enough to ask about collisions, and the extraction
                // that follows saves what it can and says where it stopped.
                TarEntry? entry;
                try { entry = reader.GetNextEntry(copyData: false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { break; }
                if (entry == null) break;

                // Not files, as ReadInto already knows: counted here, git
                // archive's global header made the total one more than anything
                // could finish, and took part in the clash check.
                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
                    continue;

                bool isDirectory = entry.EntryType == TarEntryType.Directory;
                var sparse = SparseOf(entry);
                entries.Add(new ArchiveEntryInfo(
                    sparse?.Name ?? entry.Name,
                    isDirectory ? 0 : sparse?.RealSize ?? entry.Length,
                    entry.ModificationTime.LocalDateTime,
                    isDirectory));
            }

            return entries;
        }

        // ---------- GNU sparse files ----------

        /// <summary>
        /// A file stored sparse: its real name and size, and where its data goes.
        ///
        /// Windows' own tar stores a sparse file this way — a virtual disk, a
        /// database, anything mostly holes — as PAX "GNU.sparse" 1.0: the
        /// header is named "GNUSparseFile.0/holes.bin", the real name and size
        /// are in extended attributes, and the data begins with a map of where
        /// each stored piece belongs. Read as an ordinary file, a 64MB file
        /// came out as "GNUSparseFile.0/holes.bin" of 262KB, with nothing said.
        /// </summary>
        /// <param name="Map">Offset and length of each stored piece; null when it is at the front of the data (1.0).</param>
        /// <param name="Refusal">Set for a layout that cannot be rebuilt (0.0, whose map a dictionary cannot hold).</param>
        private sealed record Sparse(string Name, long RealSize, List<(long Offset, long Length)>? Map, string? Refusal);

        private static Sparse? SparseOf(TarEntry entry)
        {
            if (entry is not PaxTarEntry pax) return null;
            var a = pax.ExtendedAttributes;
            if (!a.Keys.Any(k => k.StartsWith("GNU.sparse.", StringComparison.Ordinal))) return null;

            var name = a.TryGetValue("GNU.sparse.name", out var n) && !string.IsNullOrEmpty(n) ? n : entry.Name;

            static long Number(IReadOnlyDictionary<string, string> a, string key) =>
                a.TryGetValue(key, out var v) && long.TryParse(v, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : -1;

            if (a.TryGetValue("GNU.sparse.major", out var major) && major == "1")
            {
                long real = Number(a, "GNU.sparse.realsize");
                return real < 0
                    ? new Sparse(name, 0, null, "it is a sparse file whose real size is not recorded")
                    : new Sparse(name, real, null, null);
            }

            if (a.TryGetValue("GNU.sparse.map", out var text))
            {
                long size = Number(a, "GNU.sparse.size");
                var parts = text.Split(',', StringSplitOptions.TrimEntries);
                var map = new List<(long, long)>();
                bool ok = size >= 0 && parts.Length % 2 == 0;
                for (int i = 0; ok && i < parts.Length; i += 2)
                {
                    ok = long.TryParse(parts[i], out var offset) & long.TryParse(parts[i + 1], out var length) &&
                         offset >= 0 && length >= 0 && offset + length <= size;
                    if (ok) map.Add((offset, length));
                }
                return ok
                    ? new Sparse(name, size, map, null)
                    : new Sparse(name, 0, null, "it is a sparse file with a map that does not make sense");
            }

            return new Sparse(name, Math.Max(0, Number(a, "GNU.sparse.size")), null,
                "it is a sparse file in an old layout this cannot rebuild");
        }

        /// <summary>Reads the 1.0 map from the front of the data, and the padding after it.</summary>
        private static List<(long Offset, long Length)> ReadSparseMap(Stream data, long realSize, long dataLength)
        {
            long consumed = 0;
            var line = new StringBuilder();

            long Next()
            {
                line.Clear();
                while (true)
                {
                    int b = data.ReadByte();
                    if (b < 0) throw new InvalidDataException("the archive ends part way through it");
                    consumed++;
                    if (b == '\n') break;
                    if (b < '0' || b > '9' || line.Length >= 20)
                        throw new InvalidDataException("its sparse map is damaged");
                    line.Append((char)b);
                }
                return line.Length == 0 ? throw new InvalidDataException("its sparse map is damaged") : long.Parse(line.ToString());
            }

            long count = Next();
            if (count < 0 || count > dataLength) throw new InvalidDataException("its sparse map is damaged");

            var map = new List<(long, long)>();
            long stored = 0, previous = 0;
            for (long i = 0; i < count; i++)
            {
                long offset = Next(), length = Next();
                if (offset < previous || offset + length > realSize) throw new InvalidDataException("its sparse map is damaged");
                previous = offset + length;
                stored += length;
                map.Add((offset, length));
            }

            long pad = (512 - consumed % 512) % 512;
            for (long i = 0; i < pad; i++)
                if (data.ReadByte() < 0) throw new InvalidDataException("the archive ends part way through it");

            if (consumed + pad + stored > dataLength) throw new InvalidDataException("its sparse map is damaged");
            return map;
        }

        /// <summary>Writes a sparse entry's pieces where they belong, leaving the holes as holes.</summary>
        private static void WriteSparse(FileStream output, Stream data, Sparse sparse, long dataLength,
            ArchiveEngine.Reporter reporter, CancellationToken token)
        {
            var map = sparse.Map ?? ReadSparseMap(data, sparse.RealSize, dataLength);

            // Best effort: on NTFS the holes then take no space on disk, which
            // for a mostly empty virtual disk is the difference between fitting
            // and not.
            try { DeviceIoControl(output.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); }
            catch { }

            var buffer = new byte[1 << 20];
            long stored = 0;
            foreach (var (offset, length) in map)
            {
                output.Position = offset;
                long left = length;
                while (left > 0)
                {
                    token.ThrowIfCancellationRequested();
                    int got = data.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (got <= 0) throw new InvalidDataException("the archive ends part way through it");
                    output.Write(buffer, 0, got);
                    left -= got;
                    stored += got;
                    reporter.Advance(got);
                }
            }

            output.SetLength(sparse.RealSize);

            // The listing counts a sparse file at its full size, holes and all.
            reporter.Advance(Math.Max(0, sparse.RealSize - stored));
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code,
            IntPtr inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

        /// <summary>
        /// Reads a tar and writes what is in it to disk.
        ///
        /// Sequential where it has to be and parallel where it does not. A
        /// compressed tar has no index — the only way to reach the last entry is
        /// to decompress everything in front of it — so one thread reads. What
        /// that thread does with each entry is another matter: the file writes go
        /// to a pool, which is where the time actually goes for an archive of many
        /// small files, and it means the thread doing the decompression is never
        /// also the thread waiting on a disk.
        ///
        /// Nothing here depends on the order files land in, unlike writing an
        /// archive, where the directory at the end claims a fixed one.
        /// </summary>
        internal static int ReadInto(Stream tar, ExtractionRules rules, int threads,
            ArchiveEngine.Reporter reporter, List<string> errors, CancellationToken token)
        {
            int lanes = Math.Max(1, threads);
            using var queue = new BlockingCollection<Pending>(lanes * 2);
            int done = 0;

            // Set when the archive itself stops making sense — truncated, or a
            // compressed stream that will not decompress — as opposed to one
            // entry that could not be written. Said once at the end, with how
            // much was saved.
            bool damaged = false;

            // Where each file went, by its name in the archive, for the hard links
            // that name it; and the links, made once every file is written.
            var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var links = new List<(string Name, string Target, string LinkTo)>();

            var writers = new Task[lanes];
            for (int lane = 0; lane < lanes; lane++)
            {
                writers[lane] = Task.Run(() =>
                {
                    foreach (var pending in queue.GetConsumingEnumerable())
                    {
                        try
                        {
                            WriteFile(pending.Target, pending.Data, pending.Length, pending.Modified);
                            Interlocked.Increment(ref done);
                            lock (extracted) extracted[ArchiveName(pending.Name)] = pending.Target;
                        }
                        catch (Exception ex)
                        {
                            lock (errors) errors.Add($"{pending.Name}: {ex.Message}");
                            rules.Withdraw(pending.Target);
                        }
                        finally { reporter.Finished(0); }
                    }
                });
            }

            try
            {
                using var reader = new TarReader(tar, leaveOpen: true);

                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    TarEntry? entry;
                    try { entry = reader.GetNextEntry(copyData: false); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        damaged = true;
                        break;
                    }

                    if (entry == null) break;

                    // Metadata, not files: `git archive` begins every tarball with
                    // a global header, which was reported as a file that could not
                    // be extracted.
                    if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
                        continue;

                    bool isDirectory = entry.EntryType == TarEntryType.Directory;
                    var sparse = isDirectory ? null : SparseOf(entry);
                    var name = sparse?.Name ?? entry.Name;
                    var target = rules.TargetFor(name, isDirectory, out var problem);

                    if (target == null)
                    {
                        if (problem != null) lock (errors) errors.Add(problem);
                        // Skipped is dealt with: charged its size, which the
                        // listing counted, or the bar stops short of it.
                        reporter.Finished(isDirectory ? 0 : Math.Max(0, sparse?.RealSize ?? entry.Length));
                        continue;
                    }

                    reporter.Starting(name);

                    if (isDirectory)
                    {
                        try { Directory.CreateDirectory(target); Interlocked.Increment(ref done); }
                        catch (Exception ex) { lock (errors) errors.Add($"{entry.Name}: {ex.Message}"); }
                        reporter.Finished(0);
                        continue;
                    }

                    if (sparse != null)
                    {
                        if (sparse.Refusal != null)
                        {
                            lock (errors) errors.Add($"{name}: {sparse.Refusal}, so it was not extracted");
                            rules.Withdraw(target);
                            reporter.Advance(Math.Max(0, sparse.RealSize));
                        }
                        else
                        {
                            var stored = entry.DataStream ?? Stream.Null;
                            try
                            {
                                ArchiveEngine.WriteCommitted(target,
                                    output => WriteSparse(output, stored, sparse, entry.Length, reporter, token));
                                try { File.SetLastWriteTime(target, entry.ModificationTime.LocalDateTime); }
                                catch { }
                                Interlocked.Increment(ref done);
                                lock (extracted) extracted[ArchiveName(name)] = target;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                if (ex is InvalidDataException or EndOfStreamException) damaged = true;
                                lock (errors) errors.Add($"{name}: {ex.Message}");
                                rules.Withdraw(target);
                            }
                        }
                        reporter.Finished(0);
                        continue;
                    }

                    // A hard link is a second name for a file already in the
                    // archive, with no data of its own — GNU tar stores every
                    // extra name that way. Windows needs no privilege for the
                    // equivalent, so it is made: a copy of that file, once the
                    // file is on disk. Reported as not extracted, the file was
                    // simply missing under every name but the first.
                    if (entry.EntryType == TarEntryType.HardLink && !string.IsNullOrEmpty(entry.LinkName))
                    {
                        links.Add((entry.Name, target, entry.LinkName));
                        continue;
                    }

                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    {
                        // Symbolic and hard links, devices, named pipes. Windows
                        // either cannot represent them or needs a privilege to,
                        // and an extraction that silently left an empty file where
                        // a link was is worse than one that says what it left out.
                        lock (errors) errors.Add($"{entry.Name}: {Describe(entry.EntryType)} was not extracted");
                        rules.Withdraw(target);
                        reporter.Finished(0);
                        continue;
                    }

                    var data = entry.DataStream;
                    long length = entry.Length;

                    if (data == null || length <= InlineWriteBytes)
                    {
                        // Buffered and handed on. It has to be copied out here
                        // either way: a TarReader entry's data stream is a window
                        // onto the archive that stops being valid the moment the
                        // next entry is read.
                        var buffer = new byte[Math.Max(0, length)];
                        int filled;
                        try { filled = data == null ? 0 : ReadFully(data, buffer, token); }
                        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
                        {
                            // A gzip stream that stops decompressing said
                            // "unsupported compression method", from nowhere a
                            // person could make sense of, and nothing was kept.
                            lock (errors) errors.Add($"{entry.Name}: the archive is damaged at this point");
                            rules.Withdraw(target);
                            reporter.Finished(0);
                            damaged = true;
                            break;
                        }
                        if (filled > 0) reporter.Advance(filled);

                        // A truncated archive ends part way through an entry. That
                        // part is not the file, and writing it under the file's name
                        // was how a damaged download "extracted" cleanly.
                        if (filled < length)
                        {
                            lock (errors) errors.Add($"{entry.Name}: the archive ends part way through it");
                            rules.Withdraw(target);
                            reporter.Finished(0);
                            damaged = true;
                            continue;
                        }

                        queue.Add(new Pending(entry.Name, target, buffer, filled,
                            entry.ModificationTime.LocalDateTime), token);
                    }
                    else
                    {
                        try
                        {
                            ArchiveEngine.WriteCommitted(target, output =>
                            {
                                var buffer = new byte[1 << 20];
                                long copied = 0;
                                while (true)
                                {
                                    token.ThrowIfCancellationRequested();
                                    int got = data.Read(buffer, 0, buffer.Length);
                                    if (got <= 0) break;
                                    output.Write(buffer, 0, got);
                                    copied += got;
                                    reporter.Advance(got);
                                }

                                if (copied < length)
                                    throw new InvalidDataException("the archive ends part way through it");
                            });

                            try { File.SetLastWriteTime(target, entry.ModificationTime.LocalDateTime); }
                            catch { }

                            Interlocked.Increment(ref done);

                            // Only once it is written: a link to a file that failed
                            // copied whatever was already there under the link's name.
                            lock (extracted) extracted[ArchiveName(entry.Name)] = target;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            bool broken = ex is InvalidDataException or EndOfStreamException;
                            if (broken) damaged = true;
                            lock (errors) errors.Add(broken && ex.Message != "the archive ends part way through it"
                                ? $"{entry.Name}: the archive is damaged at this point"
                                : $"{entry.Name}: {ex.Message}");
                            rules.Withdraw(target);
                        }

                        reporter.Finished(0);
                    }
                }
            }
            finally
            {
                queue.CompleteAdding();

                // Waited for even on the way out of a cancellation: these hold
                // file handles, and returning while one is still writing leaves a
                // half-written file being created after the caller has already
                // started clearing up.
                try { Task.WaitAll(writers); } catch { }
            }

            token.ThrowIfCancellationRequested();

            foreach (var (name, target, linkTo) in links)
            {
                token.ThrowIfCancellationRequested();
                reporter.Starting(name);
                try
                {
                    string? source;
                    lock (extracted) extracted.TryGetValue(ArchiveName(linkTo), out source);
                    if (source == null || !File.Exists(source))
                        throw new IOException($"it is another name for {linkTo}, which was not extracted");

                    // Written beside and moved into place, like every other entry:
                    // a copy that failed part way had already destroyed the file
                    // it was replacing.
                    ArchiveEngine.WriteCommitted(target, output =>
                    {
                        using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                        from.CopyTo(output, 1 << 20);
                    });
                    try { File.SetLastWriteTime(target, File.GetLastWriteTime(source)); } catch { }
                    done++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{name}: {ex.Message}");
                    rules.Withdraw(target);
                }
                finally { reporter.Finished(0); }
            }

            if (damaged) lock (errors) errors.Add(ArchiveEngine.DamagedMessage(done));

            return done;
        }

        /// <summary>An entry's name as a link would give it: no leading "./" or slash.</summary>
        private static string ArchiveName(string name)
        {
            var cleaned = name.Replace('\\', '/');
            while (true)
            {
                if (cleaned.StartsWith("./", StringComparison.Ordinal)) cleaned = cleaned[2..];
                else if (cleaned.StartsWith('/')) cleaned = cleaned[1..];
                else return cleaned;
            }
        }

        /// <summary>
        /// A modification time tar will take. Before 1970 the entry refuses it
        /// and the file was left out of the archive; a camera with a dead clock
        /// dates its files 1601.
        /// </summary>
        private static DateTimeOffset TarTime(DateTime modified)
        {
            var at = new DateTimeOffset(modified.ToUniversalTime(), TimeSpan.Zero);
            return at < DateTimeOffset.UnixEpoch ? DateTimeOffset.UnixEpoch : at;
        }

        private sealed record Pending(string Name, string Target, byte[] Data, int Length, DateTime Modified);

        private static string Describe(TarEntryType type) => type switch
        {
            TarEntryType.SymbolicLink => "a symbolic link",
            TarEntryType.HardLink => "a hard link",
            TarEntryType.CharacterDevice => "a character device",
            TarEntryType.BlockDevice => "a block device",
            TarEntryType.Fifo => "a named pipe",
            TarEntryType.SparseFile => "a sparse file in an old layout this cannot rebuild",
            _ => "an entry of a kind Windows has no equivalent for",
        };

        private static int ReadFully(Stream source, byte[] into, CancellationToken token)
        {
            int filled = 0;
            while (filled < into.Length)
            {
                token.ThrowIfCancellationRequested();
                int got = source.Read(into, filled, into.Length - filled);
                if (got <= 0) break;
                filled += got;
            }
            return filled;
        }

        private static void WriteFile(string target, byte[] data, int length, DateTime modified)
        {
            ArchiveEngine.WriteCommitted(target, output => output.Write(data, 0, length),
                Math.Max(4096, Math.Min(1 << 20, length + 1)));

            try { File.SetLastWriteTime(target, modified); }
            catch { }
        }

        /// <summary>
        /// A read-only pass-through that counts what has been read from the
        /// archive file, for progress measured through the compressed bytes.
        /// The count is read from the reporting thread, so it is interlocked.
        /// </summary>
        private sealed class ReadCounter : Stream
        {
            private readonly Stream _inner;
            private long _read;

            public ReadCounter(Stream inner) => _inner = inner;

            public long Consumed => Interlocked.Read(ref _read);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position
            {
                get => _inner.Position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                int got = _inner.Read(buffer);
                if (got > 0) Interlocked.Add(ref _read, got);
                return got;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            // The file underneath belongs to the caller, which closes it.
            protected override void Dispose(bool disposing) { }
        }

        /// <summary>
        /// A read-only pass-through that tells the reporter how far it has got.
        ///
        /// It exists because TarWriter takes a stream and reads it itself, which
        /// is the right shape for everything except knowing how it is going. The
        /// alternative — reading the file here and handing over a MemoryStream —
        /// would mean the whole file in memory to draw a number.
        /// </summary>
        private sealed class CountingStream : Stream
        {
            private readonly Stream _inner;
            private readonly ArchiveEngine.Reporter _reporter;

            /// <summary>
            /// The length the file had when it was opened, which is the length the
            /// tar header records. Everything read is held to it.
            ///
            /// TarWriter writes the size from Length and then copies to the end of
            /// the stream. A file still being written — a log, a recording — gave
            /// it more bytes than the header said, and every entry after it in the
            /// archive was unreadable ("Unable to parse number"), while the result
            /// said the archive had been made.
            /// </summary>
            private readonly long _length;
            private long _consumed;

            public CountingStream(Stream inner, ArchiveEngine.Reporter reporter)
            {
                _inner = inner;
                _reporter = reporter;
                _length = inner.Length;
            }

            /// <summary>Why what was archived is not the file, or null when it is.</summary>
            public string? Changed { get; private set; }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _length;

            public override long Position
            {
                get => _consumed;
                set
                {
                    _inner.Position = value;
                    _consumed = value;
                }
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                long left = _length - _consumed;
                if (left <= 0)
                {
                    if (Changed == null && _inner.ReadByte() >= 0)
                        Changed = "it grew while it was being archived; the archive holds it as it was when it was opened";
                    return 0;
                }

                var into = buffer.Slice(0, (int)Math.Min(buffer.Length, left));
                int got;
                try { got = _inner.Read(into); }
                catch (IOException ex)
                {
                    got = 0;
                    Changed ??= $"it could not be read to the end ({ex.Message}); the rest is zeros in the archive";
                }

                if (got <= 0)
                {
                    // Shorter than the header now promises. Zeros keep the entry
                    // the length it claims, so everything after it still reads.
                    Changed ??= "it shrank while it was being archived; the missing part is zeros in the archive";
                    into.Clear();
                    got = into.Length;
                }

                _consumed += got;
                _reporter.Advance(got);
                return got;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                long target = origin switch
                {
                    SeekOrigin.Current => _consumed + offset,
                    SeekOrigin.End => _length + offset,
                    _ => offset,
                };
                Position = target;
                return target;
            }
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            // The inner stream belongs to the caller, which closes it.
            protected override void Dispose(bool disposing) { }
        }
    }
}
