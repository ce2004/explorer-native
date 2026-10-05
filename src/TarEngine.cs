using System;
using System.Collections.Concurrent;
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

        public static int Extract(string archivePath, ArchiveFormat format,
            ExtractionRules rules, int threads, ArchiveEngine.Reporter reporter, List<string> errors,
            CancellationToken token)
        {
            if (format.SingleFile) return ExpandOneFile(archivePath, rules, reporter, errors, token);

            using var file = OpenFile(archivePath);
            if (format.Id != "tar.gz") return ReadInto(file, rules, threads, reporter, errors, token);

            // GZipStream reads every member of a multi-member file and joins
            // them, which is what makes anything ParallelGZip writes readable
            // here — and anything pigz writes, and two .gz files concatenated.
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
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

        private static int ExpandOneFile(string archivePath, ExtractionRules rules,
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

            ArchiveEngine.WriteCommitted(target, output =>
            {
                using var file = OpenFile(archivePath);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                var buffer = new byte[1 << 20];
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int got = gzip.Read(buffer, 0, buffer.Length);
                    if (got <= 0) break;
                    output.Write(buffer, 0, got);
                    reporter.Advance(got);
                }
            });

            reporter.Finished(0);
            return 1;
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

            return written;
        }

        /// <summary>Every entry's name, size and date, without extracting anything.</summary>
        internal static IReadOnlyList<ArchiveEntryInfo> ReadIndex(Stream tar, CancellationToken token)
        {
            using var reader = new TarReader(tar, leaveOpen: true);
            var entries = new List<ArchiveEntryInfo>();

            while (reader.GetNextEntry(copyData: false) is { } entry)
            {
                token.ThrowIfCancellationRequested();

                // Not files, as ReadInto already knows: counted here, git
                // archive's global header made the total one more than anything
                // could finish, and took part in the clash check.
                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
                    continue;

                bool isDirectory = entry.EntryType == TarEntryType.Directory;
                entries.Add(new ArchiveEntryInfo(
                    entry.Name,
                    isDirectory ? 0 : entry.Length,
                    entry.ModificationTime.LocalDateTime,
                    isDirectory));
            }

            return entries;
        }

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
                    catch (Exception ex)
                    {
                        lock (errors) errors.Add($"The archive could not be read past this point: {ex.Message}");
                        break;
                    }

                    if (entry == null) break;

                    // Metadata, not files: `git archive` begins every tarball with
                    // a global header, which was reported as a file that could not
                    // be extracted.
                    if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
                        continue;

                    bool isDirectory = entry.EntryType == TarEntryType.Directory;
                    var target = rules.TargetFor(entry.Name, isDirectory, out var problem);

                    if (target == null)
                    {
                        if (problem != null) lock (errors) errors.Add(problem);
                        reporter.Finished(0);
                        continue;
                    }

                    reporter.Starting(entry.Name);

                    if (isDirectory)
                    {
                        try { Directory.CreateDirectory(target); Interlocked.Increment(ref done); }
                        catch (Exception ex) { lock (errors) errors.Add($"{entry.Name}: {ex.Message}"); }
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
                        int filled = data == null ? 0 : ReadFully(data, buffer, token);
                        if (filled > 0) reporter.Advance(filled);

                        // A truncated archive ends part way through an entry. That
                        // part is not the file, and writing it under the file's name
                        // was how a damaged download "extracted" cleanly.
                        if (filled < length)
                        {
                            lock (errors) errors.Add($"{entry.Name}: the archive ends part way through it");
                            rules.Withdraw(target);
                            reporter.Finished(0);
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
                            lock (errors) errors.Add($"{entry.Name}: {ex.Message}");
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
