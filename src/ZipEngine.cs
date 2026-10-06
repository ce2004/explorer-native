using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Zip, written and read with every core.
    ///
    /// The writer is ours rather than <c>ZipArchive</c>'s, and the reason is the
    /// only reason that would justify it: <c>ZipArchive</c> cannot be made
    /// parallel from outside. It takes bytes and compresses them itself, on the
    /// calling thread, one entry at a time — there is no way to hand it an entry
    /// that is already deflated, so a caller that wanted every core compressing
    /// could only ever get one. Measured on the 142MB corpus in CLAUDE.md: 10
    /// MB/s through ZipFile, 12 MB/s through the tar.exe Windows ships, 41 MB/s
    /// here. Extraction, 81 MB/s through the system tar against 310 MB/s here.
    ///
    /// Four times rather than ten, and the missing six are not in this code.
    /// This machine reports ten processors and will not run more than about 3.8
    /// threads at once: a burn loop of pure arithmetic on ten dedicated threads
    /// measures 3.8 cores of processor time per second, the same figure the
    /// compressor gets. The scaling inside the pipeline is clean up to that
    /// point — one worker 1.0 cores, two 2.0, four 3.7 — and flat after it,
    /// which is what running out of machine looks like rather than running out
    /// of design.
    ///
    /// What makes it possible is a property of the format rather than a trick. A
    /// zip is not one compressed stream: it is a run of independently compressed
    /// entries, each with its own header, followed by a directory saying where
    /// each one starts. Nothing in an entry refers to any other entry. So entries
    /// can be deflated in any order by as many workers as there are cores, as
    /// long as one thread writes them out in the order the directory will claim
    /// they are in — which is what the pipeline below does. This is the same
    /// reason the reader parallelises: the directory gives every entry's offset,
    /// so ten threads with ten handles on the file can each extract a different
    /// one without ever reading the same byte twice.
    ///
    /// Three kinds of entry go through the writer, and which one an entry is
    /// decides where its bytes are compressed:
    ///
    /// - Already-compressed files — jpeg, mp4, flac, another zip — are *stored*,
    ///   not deflated. Deflating a jpeg spends a core to make the file 0.1%
    ///   bigger. Stored entries are copied straight through by the writer thread,
    ///   because there is no processor work to spread and the copy is bound by
    ///   the disk either way.
    /// - Ordinary files are compressed by a worker into memory and handed over.
    ///   This is the case that matters and the case that is fast.
    /// - Files above <see cref="WorkerMaxBytes"/> are deflated by the writer
    ///   thread as it streams them. One entry is one deflate stream and cannot be
    ///   split, so buffering a four gigabyte file to compress it in a worker
    ///   would cost four gigabytes of memory to parallelise nothing. Workers
    ///   carry on preparing the entries behind it while that happens.
    ///
    /// The last of those is the honest limit of this format: an archive that is
    /// one enormous file is single-threaded, here and in every other zip tool,
    /// because deflate is defined as a single stream of back-references. A
    /// gzipped tar has no such limit — see <see cref="ParallelGZip"/>, which cuts
    /// the stream into members — which is why the chooser offers it second.
    /// </summary>
    public static class ZipEngine
    {
        /// <summary>
        /// The largest file a worker will hold in memory to compress.
        ///
        /// Above this the writer streams it instead. Sixty-four megabytes is
        /// chosen against the budget below rather than in isolation: it is the
        /// point where one entry can still sit comfortably inside the in-flight
        /// allowance on any machine this runs on, so a single big file never
        /// stalls the pipeline waiting for room that a smaller one is using.
        /// </summary>
        private const long WorkerMaxBytes = 64L * 1024 * 1024;

        /// <summary>
        /// Sizes at or above this get a zip64 header even though four bytes could
        /// still hold them.
        ///
        /// The streamed path has to decide whether an entry needs 64-bit sizes
        /// *before* it knows what the compressed size will be, because the header
        /// goes down before the data. The planned size is what it decides from,
        /// and this leaves a quarter of a gigabyte of slack for a file that grows
        /// while it is being read. Below four gigabytes the question does not
        /// arise; above it there is no choice to get wrong.
        /// </summary>
        private const long Zip64Threshold = 0xF000_0000L;

        private const uint LocalSignature = 0x04034b50;
        private const uint CentralSignature = 0x02014b50;
        private const uint EndSignature = 0x06054b50;
        private const uint Zip64EndSignature = 0x06064b50;
        private const uint Zip64LocatorSignature = 0x07064b50;

        private const ushort MethodStored = 0;
        private const ushort MethodDeflate = 8;

        /// <summary>Bit 11: the name and comment are UTF-8. Always set; see below.</summary>
        private const ushort FlagUtf8 = 0x0800;

        /// <summary>
        /// Extensions whose contents are already compressed, and which are
        /// therefore stored rather than deflated.
        ///
        /// This is a list of extensions and not an entropy test on purpose. A
        /// sample-and-guess costs a read of the file before the decision and gets
        /// the answer wrong on the cases that matter — a WAV of silence looks
        /// incompressible in its first 64KB, an installer looks compressible in
        /// its header. The extension is what the file says it is, it costs
        /// nothing to consult, and being wrong about one merely means an entry is
        /// stored that could have shrunk by two percent.
        /// </summary>
        private static readonly HashSet<string> AlreadyCompressed = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".heic", ".heif", ".jxl",
            ".mp3", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".flac", ".wma", ".ape",
            ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".flv", ".ts",
            ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".lz4", ".br", ".cab",
            ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub", ".jar", ".apk", ".appx", ".msix",
            ".pdf", ".dmg", ".iso", ".crx", ".nupkg", ".whl", ".pak",
        };

        private static bool StoreWholesale(ArchiveItem item, ArchiveLevel level) =>
            level == ArchiveLevel.Fastest && item.Size >= WorkerMaxBytes ||
            AlreadyCompressed.Contains(Path.GetExtension(item.Name));

        // ---------- Writing ----------

        /// <summary>
        /// One entry, ready to be written: either its compressed bytes in hand,
        /// or a note that the writer has to produce them itself.
        /// </summary>
        private sealed class Prepared
        {
            public required ArchiveItem Item { get; init; }
            public ushort Method { get; init; }
            public uint Crc { get; init; }
            public long Uncompressed { get; init; }

            /// <summary>Null when the writer must stream this entry itself.</summary>
            public byte[]? Data { get; init; }

            public int Length { get; init; }

            /// <summary>What was taken from the in-flight budget, to be given back.</summary>
            public long Reserved { get; init; }

            /// <summary>Set when the file could not be read; nothing is written.</summary>
            public string? Error { get; init; }
        }

        /// <summary>
        /// How many uncompressed bytes may be held in memory by entries that are
        /// prepared but not yet written.
        ///
        /// Bounded in bytes rather than in entries because entries are not the
        /// same size: twenty slots is thirty kilobytes for a folder of text files
        /// and 1.2 gigabytes for a folder of raw camera files, and only one of
        /// those numbers is a limit.
        ///
        /// Taken by the producer, which walks the plan in order on a thread of its
        /// own, and given back by the writer as each entry lands. Both of those
        /// facts matter: because the producer is single-threaded and in order,
        /// entries reserve in exactly the order the writer will consume them, so
        /// the head of the queue is never waiting behind something further down
        /// that took the last of the budget. Reserving from the workers instead
        /// would deadlock, and it would do it only on large archives.
        /// </summary>
        private sealed class Budget
        {
            private readonly long _total;
            private long _free;
            private readonly object _gate = new();

            public Budget(long total) { _total = total; _free = total; }

            public void Take(long bytes, CancellationToken token)
            {
                lock (_gate)
                {
                    // The second half of the condition is what stops an entry
                    // larger than the whole budget waiting for ever: once
                    // everything else has been written it goes through alone.
                    while (_free < bytes && _free < _total)
                    {
                        token.ThrowIfCancellationRequested();
                        Monitor.Wait(_gate, 250);
                    }
                    _free -= bytes;
                }
            }

            public void Give(long bytes)
            {
                if (bytes <= 0) return;
                lock (_gate) { _free += bytes; Monitor.PulseAll(_gate); }
            }
        }

        public static int Create(
            IReadOnlyList<ArchiveItem> plan,
            string archivePath,
            ArchiveLevel level,
            int threads,
            ArchiveEngine.Reporter reporter,
            List<string> errors,
            CancellationToken token)
        {
            int lanes = Math.Max(1, threads);
            var compression = ArchiveEngine.ToCompressionLevel(level);
            var budget = new Budget(Math.Clamp(lanes * 24L * 1024 * 1024, 128L << 20, 512L << 20));

            // How many entries may be *being compressed* at once.
            //
            // This is not the same thing as how many may be queued, and the
            // difference was a bug that hid for exactly as long as nobody
            // measured it. The worker count used to control only the queue's
            // capacity, and the compression itself went to the thread pool, which
            // runs as many as it feels like — so asking for one worker and asking
            // for sixteen produced the same wall clock, and a machine could not be
            // told to leave itself some processor for anything else.
            //
            // Taken by the producer and released by the writer, exactly like the
            // byte budget below it. Doing it that way rather than waiting inside
            // the worker matters: a pool thread that is blocked on a semaphore is
            // a thread that is not compressing anything, and the pool answers that
            // by starting more of them.
            using var lane = new SemaphoreSlim(lanes, lanes);

            // Two spare slots so a worker never finishes into a full queue while
            // the writer is busy with a big entry.
            using var queue = new BlockingCollection<Task<Prepared>>(lanes + 2);

            Exception? producerFailure = null;

            // A thread of its own rather than a pool thread: this one blocks, on
            // the budget and on the queue, and a blocked pool thread is one that
            // is not compressing anything.
            var producer = new Thread(() =>
            {
                try
                {
                    foreach (var item in plan)
                    {
                        token.ThrowIfCancellationRequested();

                        if (item.IsDirectory)
                        {
                            queue.Add(Task.FromResult(new Prepared
                            {
                                Item = item,
                                Method = MethodStored,
                                Crc = 0,
                                Uncompressed = 0,
                                Data = null,
                                Length = 0,
                                Reserved = 0,
                            }), token);
                            continue;
                        }

                        // Streamed by the writer: nothing to reserve, nothing to
                        // spread. Data stays null and Length is -1 to say so.
                        if (item.Size > WorkerMaxBytes || StoreWholesale(item, level))
                        {
                            queue.Add(Task.FromResult(new Prepared
                            {
                                Item = item,
                                Method = StoreWholesale(item, level) ? MethodStored : MethodDeflate,
                                Uncompressed = -1,
                                Data = null,
                                Length = -1,
                                Reserved = 0,
                            }), token);
                            continue;
                        }

                        // Deflate adds five bytes per 64KB stored block to data that
                        // will not compress, which is what the buffer below is sized
                        // for; reserved the same.
                        long reserved = item.Size + (item.Size >> 12) + 64 * 1024;
                        budget.Take(reserved, token);
                        lane.Wait(token);

                        var work = Task.Run(() => CompressToMemory(item, compression, reserved, reporter, token), token);
                        try { queue.Add(work, token); }
                        catch { budget.Give(reserved); Release(lane, lanes); throw; }
                    }
                }
                catch (Exception ex) { producerFailure = ex; }
                finally { queue.CompleteAdding(); }
            })
            { IsBackground = true, Name = "zip-plan" };

            producer.Start();

            int written = 0;

            using (var output = new FileStream(archivePath, FileMode.Create, FileAccess.Write,
                       FileShare.None, 1 << 20))
            {
                var central = new List<byte[]>(plan.Count);
                long entryCount = 0;

                try
                {
                    foreach (var work in queue.GetConsumingEnumerable())
                    {
                        token.ThrowIfCancellationRequested();

                        Prepared prepared;
                        try { prepared = work.GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            errors.Add(ex.Message);
                            continue;
                        }

                        try
                        {
                            if (prepared.Error != null)
                            {
                                errors.Add(prepared.Error);
                                reporter.Finished(0);
                                continue;
                            }

                            // Not for a directory. A folder entry is a header and
                            // nothing else, and it is written the instant the
                            // pipeline starts — over the top of the name of the
                            // file a worker has just begun compressing, which then
                            // stands for the whole job. Compressing one track
                            // showed the folder's name from beginning to end.
                            if (!prepared.Item.IsDirectory) reporter.Starting(prepared.Item.Name);

                            long before = output.Position;

                            // One file that cannot be read costs that file, not
                            // the archive. The streamed path — anything over
                            // 64MB, and anything already compressed — threw, and
                            // nothing here caught it, so the exception escaped
                            // Create and the finished part of a multi-gigabyte
                            // job was deleted on the way out. A file locked
                            // exclusively by another program is enough to do it.
                            // Small files were never treated that way: their
                            // errors are collected and the archive goes on.
                            byte[] record;
                            try
                            {
                                record = WriteEntry(output, prepared, compression, reporter, token);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (IOException ex)
                            {
                                // Rewound over whatever the entry wrote — a local
                                // header and part of its data on a write that
                                // failed, which readers that walk the local
                                // headers in order then stumbled into.
                                try
                                {
                                    output.SetLength(before);
                                    output.Position = before;
                                }
                                catch { }
                                errors.Add(ex.Message);
                                reporter.Finished(0);
                                continue;
                            }

                            central.Add(record);
                            entryCount++;
                            written++;

                            RoboCopyEngine.Trace($"zip: {prepared.Item.Name} " +
                                                 $"{prepared.Item.Size} -> {output.Position - before}");
                        }
                        finally
                        {
                            budget.Give(prepared.Reserved);
                            if (prepared.Reserved > 0) Release(lane, lanes);
                        }
                    }
                }
                finally
                {
                    // Whatever happens, stop the producer and let go of anything
                    // it is holding, or a cancelled archive leaves a thread
                    // sitting on a budget nobody will ever give back.
                    if (!queue.IsAddingCompleted)
                    {
                        try { queue.CompleteAdding(); } catch { }
                    }

                    foreach (var abandoned in queue.GetConsumingEnumerable())
                        abandoned.ContinueWith(t =>
                        {
                            _ = t.Exception;
                            if (t.Status != TaskStatus.RanToCompletion || t.Result.Reserved <= 0) return;
                            budget.Give(t.Result.Reserved);
                            Release(lane, lanes);
                        }, TaskScheduler.Default);

                    producer.Join(TimeSpan.FromSeconds(10));
                }

                if (producerFailure is OperationCanceledException) throw producerFailure;
                if (producerFailure != null) throw new IOException(producerFailure.Message, producerFailure);

                token.ThrowIfCancellationRequested();
                WriteDirectory(output, central, entryCount);
            }

            return written;
        }

        /// <summary>
        /// Gives a lane back, and never throws doing it.
        ///
        /// Reached from a finally on the way out of a cancelled archive, where
        /// the counting may already have been unwound by the drain loop. An
        /// exception from a cleanup path would replace the reason the archive
        /// stopped with a complaint about the bookkeeping.
        /// </summary>
        private static void Release(SemaphoreSlim lane, int limit)
        {
            try { if (lane.CurrentCount < limit) lane.Release(); }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
        }

        /// <summary>
        /// Deflates one file into memory, on a worker.
        ///
        /// The source is streamed rather than read in one go, so the memory this
        /// holds is the size of the *result*, not of the file. That is what lets
        /// the budget above be a number worth setting: a folder of files that
        /// compress ten to one keeps ten times as many in flight as one that does
        /// not, without anybody choosing that.
        /// </summary>
        private static Prepared CompressToMemory(ArchiveItem item, CompressionLevel level,
            long reserved, ArchiveEngine.Reporter reporter, CancellationToken token)
        {
            try
            {
                using var source = new FileStream(item.Source, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);

                // Named as the work on it begins, not when the writer gets to it.
                //
                // The writer's own Starting is the only one there used to be, and
                // it runs *after* the entry has been compressed — so for a single
                // file the window named the folder above it for the whole job and
                // the file itself only as it was finishing. With several workers
                // this names whichever files are in flight, which is what is
                // actually happening.
                reporter.Starting(item.Name);

                // Room for data that does not compress, which deflate stores with
                // five bytes of framing per 64KB. At the plain size, anything
                // incompressible over about 13MB overflowed and the stream
                // doubled, holding twice the reservation until it was written.
                var into = new MemoryStream((int)Math.Min(int.MaxValue, item.Size + (item.Size >> 12) + 1024));
                uint crc = Crc32.Seed;
                long read = 0;

                var buffer = new byte[1 << 16];
                using (var deflate = new DeflateStream(into, level, leaveOpen: true))
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        int got = source.Read(buffer, 0, buffer.Length);
                        if (got <= 0) break;

                        crc = Crc32.Append(crc, buffer.AsSpan(0, got));
                        deflate.Write(buffer, 0, got);
                        read += got;

                        // Counted here, where the work happens, and *not* again
                        // when the entry is written.
                        //
                        // This is the whole of "the progress bar on making a
                        // compressed file just sits there". Every entry small
                        // enough to compress in memory is compressed by a worker
                        // and counted only as the writer takes it, so a zip of
                        // 44MB reported three times: nothing, nothing, and
                        // finished. The deflating is the part that takes the
                        // time, and it was the part nothing watched.
                        reporter.Advance(got);
                    }
                }

                return new Prepared
                {
                    Item = item,
                    Method = MethodDeflate,
                    Crc = crc,
                    Uncompressed = read,
                    Data = into.GetBuffer(),
                    Length = (int)into.Length,
                    Reserved = reserved,
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new Prepared
                {
                    Item = item,
                    Reserved = reserved,
                    Error = $"{item.Name}: {ex.Message}",
                };
            }
        }

        /// <summary>
        /// Writes one entry and returns its central-directory record.
        ///
        /// The record is built here rather than at the end because this is the
        /// only place that knows the entry's offset, and an offset worked out
        /// afterwards by adding up sizes is an offset that is wrong the first
        /// time anything is skipped.
        /// </summary>
        private static byte[] WriteEntry(FileStream output, Prepared prepared, CompressionLevel level,
            ArchiveEngine.Reporter reporter, CancellationToken token)
        {
            var item = prepared.Item;
            long offset = output.Position;

            var name = item.IsDirectory ? item.Name + "/" : item.Name;
            var nameBytes = Encoding.UTF8.GetBytes(name);

            var (dosTime, dosDate) = DosStamp(item.Modified);

            uint attributes = (uint)item.Attributes & 0xFF;
            if (item.IsDirectory) attributes |= 0x10;

            if (prepared.Data != null || prepared.Length == 0)
            {
                // Everything is known: header, then bytes, and nothing to patch.
                long uncompressed = Math.Max(0, prepared.Uncompressed);
                long compressed = prepared.Length;
                bool zip64 = uncompressed >= uint.MaxValue || compressed >= uint.MaxValue || offset >= uint.MaxValue;

                WriteLocalHeader(output, nameBytes, prepared.Method, dosTime, dosDate,
                    prepared.Crc, compressed, uncompressed, zip64);

                if (prepared.Data != null && prepared.Length > 0)
                    output.Write(prepared.Data, 0, prepared.Length);

                // Zero: these bytes were counted as they were compressed, which
                // is where the time went. Counting them again here would take the
                // total past a hundred percent.
                reporter.Finished(0);

                return CentralRecord(nameBytes, prepared.Method, dosTime, dosDate, prepared.Crc,
                    compressed, uncompressed, offset, attributes);
            }

            // Streamed. The header goes down with the sizes it cannot know yet and
            // is corrected once they are, which is why this path needs a seekable
            // output. The alternative the format offers — a data descriptor after
            // the data, flagged in the header — is read correctly by everything
            // modern and confuses enough older tools that patching is worth the
            // two seeks.
            bool wideNeeded = item.Size >= Zip64Threshold || offset >= uint.MaxValue;

            WriteLocalHeader(output, nameBytes, prepared.Method, dosTime, dosDate, 0, 0, 0, wideNeeded);

            long dataStart = output.Position;
            uint crc;
            long read;

            try
            {
                (crc, read) = prepared.Method == MethodStored
                    ? CopyStored(item, output, reporter, token)
                    : CopyDeflated(item, output, level, reporter, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Rewind over the header and the part-written data. The entry
                // never existed as far as the directory is concerned, which is the
                // only outcome that leaves a readable archive. The flush may fail
                // for the same reason the write did; the rewind still happens.
                try { output.Flush(); } catch { }
                try
                {
                    output.SetLength(offset);
                    output.Position = offset;
                }
                catch { }
                throw new IOException($"{item.Name}: {ex.Message}", ex);
            }

            long compressedSize = output.Position - dataStart;
            long end = output.Position;

            // Planned under four gigabytes and grew past them while it was read —
            // a recording or a virtual disk in use. The local header has no room
            // for the sizes, and a cut-down one disagrees with the directory.
            if (!wideNeeded && (read >= uint.MaxValue || compressedSize >= uint.MaxValue))
            {
                try
                {
                    output.SetLength(offset);
                    output.Position = offset;
                }
                catch { }
                throw new IOException($"{item.Name}: it grew past 4GB while it was being added");
            }

            PatchLocalHeader(output, offset, nameBytes.Length, crc, compressedSize, read, wideNeeded);
            output.Position = end;

            return CentralRecord(nameBytes, prepared.Method, dosTime, dosDate, crc,
                compressedSize, read, offset, attributes);
        }

        private static (uint crc, long read) CopyStored(ArchiveItem item, Stream output,
            ArchiveEngine.Reporter reporter, CancellationToken token)
        {
            using var source = new FileStream(item.Source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);

            var buffer = new byte[1 << 20];
            uint crc = Crc32.Seed;
            long read = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                int got = source.Read(buffer, 0, buffer.Length);
                if (got <= 0) break;

                crc = Crc32.Append(crc, buffer.AsSpan(0, got));
                output.Write(buffer, 0, got);
                read += got;
                reporter.Advance(got);
            }

            reporter.Finished(0);
            return (crc, read);
        }

        private static (uint crc, long read) CopyDeflated(ArchiveItem item, Stream output,
            CompressionLevel level, ArchiveEngine.Reporter reporter, CancellationToken token)
        {
            using var source = new FileStream(item.Source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);

            var buffer = new byte[1 << 20];
            uint crc = Crc32.Seed;
            long read = 0;

            using (var deflate = new DeflateStream(output, level, leaveOpen: true))
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int got = source.Read(buffer, 0, buffer.Length);
                    if (got <= 0) break;

                    crc = Crc32.Append(crc, buffer.AsSpan(0, got));
                    deflate.Write(buffer, 0, got);
                    read += got;
                    reporter.Advance(got);
                }
            }

            reporter.Finished(0);
            return (crc, read);
        }

        private static void WriteLocalHeader(Stream output, byte[] name, ushort method,
            ushort dosTime, ushort dosDate, uint crc, long compressed, long uncompressed, bool zip64)
        {
            int extraLength = zip64 ? 20 : 0;
            var header = new byte[30 + name.Length + extraLength];
            var span = header.AsSpan();

            BinaryPrimitives.WriteUInt32LittleEndian(span[0..], LocalSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)(zip64 ? 45 : 20));
            BinaryPrimitives.WriteUInt16LittleEndian(span[6..], FlagUtf8);
            BinaryPrimitives.WriteUInt16LittleEndian(span[8..], method);
            BinaryPrimitives.WriteUInt16LittleEndian(span[10..], dosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(span[12..], dosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(span[14..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(span[18..], zip64 ? uint.MaxValue : (uint)compressed);
            BinaryPrimitives.WriteUInt32LittleEndian(span[22..], zip64 ? uint.MaxValue : (uint)uncompressed);
            BinaryPrimitives.WriteUInt16LittleEndian(span[26..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)extraLength);

            name.CopyTo(span[30..]);

            if (zip64)
            {
                var extra = span[(30 + name.Length)..];
                BinaryPrimitives.WriteUInt16LittleEndian(extra[0..], 0x0001);
                BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 16);
                BinaryPrimitives.WriteInt64LittleEndian(extra[4..], uncompressed);
                BinaryPrimitives.WriteInt64LittleEndian(extra[12..], compressed);
            }

            output.Write(header, 0, header.Length);
        }

        /// <summary>
        /// Puts the real checksum and sizes into a header that was written before
        /// they were known.
        /// </summary>
        private static void PatchLocalHeader(FileStream output, long headerOffset, int nameLength,
            uint crc, long compressed, long uncompressed, bool zip64)
        {
            output.Flush();

            Span<byte> crcAndSizes = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(crcAndSizes[0..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(crcAndSizes[4..], zip64 ? uint.MaxValue : (uint)compressed);
            BinaryPrimitives.WriteUInt32LittleEndian(crcAndSizes[8..], zip64 ? uint.MaxValue : (uint)uncompressed);

            output.Position = headerOffset + 14;
            output.Write(crcAndSizes);

            if (!zip64) return;

            Span<byte> wide = stackalloc byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(wide[0..], uncompressed);
            BinaryPrimitives.WriteInt64LittleEndian(wide[8..], compressed);

            output.Position = headerOffset + 30 + nameLength + 4;
            output.Write(wide);
        }

        private static byte[] CentralRecord(byte[] name, ushort method, ushort dosTime, ushort dosDate,
            uint crc, long compressed, long uncompressed, long offset, uint attributes)
        {
            bool wideUncompressed = uncompressed >= uint.MaxValue;
            bool wideCompressed = compressed >= uint.MaxValue;
            bool wideOffset = offset >= uint.MaxValue;
            bool zip64 = wideUncompressed || wideCompressed || wideOffset;

            // The zip64 extra field carries only the fields that overflowed, in a
            // fixed order. Writing all three unconditionally is a common way to
            // produce an archive that some readers reject.
            int extraPayload = (wideUncompressed ? 8 : 0) + (wideCompressed ? 8 : 0) + (wideOffset ? 8 : 0);
            int extraLength = zip64 ? extraPayload + 4 : 0;

            var record = new byte[46 + name.Length + extraLength];
            var span = record.AsSpan();

            BinaryPrimitives.WriteUInt32LittleEndian(span[0..], CentralSignature);

            // High byte zero means MS-DOS, which is what makes the external
            // attributes below the DOS attribute bits rather than a unix mode.
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)(zip64 ? 45 : 20));
            BinaryPrimitives.WriteUInt16LittleEndian(span[6..], (ushort)(zip64 ? 45 : 20));
            BinaryPrimitives.WriteUInt16LittleEndian(span[8..], FlagUtf8);
            BinaryPrimitives.WriteUInt16LittleEndian(span[10..], method);
            BinaryPrimitives.WriteUInt16LittleEndian(span[12..], dosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(span[14..], dosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(span[16..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(span[20..], wideCompressed ? uint.MaxValue : (uint)compressed);
            BinaryPrimitives.WriteUInt32LittleEndian(span[24..], wideUncompressed ? uint.MaxValue : (uint)uncompressed);
            BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(span[30..], (ushort)extraLength);
            BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(span[36..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(span[38..], attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(span[42..], wideOffset ? uint.MaxValue : (uint)offset);

            name.CopyTo(span[46..]);

            if (zip64)
            {
                var extra = span[(46 + name.Length)..];
                BinaryPrimitives.WriteUInt16LittleEndian(extra[0..], 0x0001);
                BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], (ushort)extraPayload);

                int at = 4;
                if (wideUncompressed) { BinaryPrimitives.WriteInt64LittleEndian(extra[at..], uncompressed); at += 8; }
                if (wideCompressed) { BinaryPrimitives.WriteInt64LittleEndian(extra[at..], compressed); at += 8; }
                if (wideOffset) { BinaryPrimitives.WriteInt64LittleEndian(extra[at..], offset); }
            }

            return record;
        }

        /// <summary>
        /// The central directory and the record that says where it is.
        ///
        /// The zip64 structures go down only when something actually needs them.
        /// They are legal to write always, and writing them always is how an
        /// eleven-file archive ends up rejected by whatever the recipient happens
        /// to unpack it with.
        /// </summary>
        private static void WriteDirectory(FileStream output, List<byte[]> central, long entryCount)
        {
            long start = output.Position;
            foreach (var record in central) output.Write(record, 0, record.Length);
            long size = output.Position - start;

            bool zip64 = entryCount >= ushort.MaxValue || size >= uint.MaxValue || start >= uint.MaxValue;

            if (zip64)
            {
                long zip64End = output.Position;

                Span<byte> record = stackalloc byte[56];
                BinaryPrimitives.WriteUInt32LittleEndian(record[0..], Zip64EndSignature);
                BinaryPrimitives.WriteInt64LittleEndian(record[4..], 44);
                BinaryPrimitives.WriteUInt16LittleEndian(record[12..], 45);
                BinaryPrimitives.WriteUInt16LittleEndian(record[14..], 45);
                BinaryPrimitives.WriteUInt32LittleEndian(record[16..], 0);
                BinaryPrimitives.WriteUInt32LittleEndian(record[20..], 0);
                BinaryPrimitives.WriteInt64LittleEndian(record[24..], entryCount);
                BinaryPrimitives.WriteInt64LittleEndian(record[32..], entryCount);
                BinaryPrimitives.WriteInt64LittleEndian(record[40..], size);
                BinaryPrimitives.WriteInt64LittleEndian(record[48..], start);
                output.Write(record);

                Span<byte> locator = stackalloc byte[20];
                BinaryPrimitives.WriteUInt32LittleEndian(locator[0..], Zip64LocatorSignature);
                BinaryPrimitives.WriteUInt32LittleEndian(locator[4..], 0);
                BinaryPrimitives.WriteInt64LittleEndian(locator[8..], zip64End);
                BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);
                output.Write(locator);
            }

            Span<byte> end = stackalloc byte[22];
            BinaryPrimitives.WriteUInt32LittleEndian(end[0..], EndSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(end[4..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(end[6..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(end[8..], (ushort)Math.Min(entryCount, ushort.MaxValue));
            BinaryPrimitives.WriteUInt16LittleEndian(end[10..], (ushort)Math.Min(entryCount, ushort.MaxValue));
            BinaryPrimitives.WriteUInt32LittleEndian(end[12..], size >= uint.MaxValue ? uint.MaxValue : (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(end[16..], start >= uint.MaxValue ? uint.MaxValue : (uint)start);
            BinaryPrimitives.WriteUInt16LittleEndian(end[20..], 0);
            output.Write(end);
        }

        /// <summary>
        /// A timestamp in the format a zip stores: two bytes of date and two of
        /// time, counting from 1980 with two-second resolution.
        ///
        /// Clamped at both ends because the format cannot say anything else. A
        /// file dated 1970 — which is what an extraction of something built on a
        /// unix machine can leave behind — would otherwise wrap into a date in the
        /// 2090s rather than fail, and a wrong date that looks plausible is worse
        /// than an obviously clamped one.
        /// </summary>
        internal static (ushort time, ushort date) DosStamp(DateTime when)
        {
            if (when.Year < 1980) when = new DateTime(1980, 1, 1, 0, 0, 0);
            if (when.Year > 2107) when = new DateTime(2107, 12, 31, 23, 59, 58);

            ushort date = (ushort)(((when.Year - 1980) << 9) | (when.Month << 5) | when.Day);
            ushort time = (ushort)((when.Hour << 11) | (when.Minute << 5) | (when.Second / 2));
            return (time, date);
        }

        // ---------- Reading ----------

        /// <summary>
        /// Opens a zip for reading, with names decoded the way the writer meant
        /// them, and a plain sentence when it cannot be opened at all.
        /// </summary>
        internal static ZipArchive Open(Stream file)
        {
            try { return new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true, NameEncoding.Instance); }
            catch (InvalidDataException)
            {
                throw new InvalidDataException(
                    "This zip is damaged or incomplete, and nothing in it could be extracted.");
            }
        }

        /// <summary>
        /// How a name without the UTF-8 flag is read.
        ///
        /// A name with bit 11 set is UTF-8 and ZipArchive decodes it itself. One
        /// without is in the code page of whoever made it — and the zips made by
        /// Windows' own tar, by Explorer's "Send to compressed folder" and by
        /// older 7-Zip are all in the OEM code page: bsdtar writes "café" as
        /// 63 61 66 82, which is CP437. Decoded as UTF-8, as it was, every such
        /// name came out with a replacement character in it.
        ///
        /// Bytes that are valid UTF-8 are still read as UTF-8, because some tools
        /// write UTF-8 and forget the flag, and a CP437 name that happens to be
        /// valid UTF-8 is vanishingly unlikely.
        /// </summary>
        internal sealed class NameEncoding : Encoding
        {
            public static readonly NameEncoding Instance = new();

            private static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);
            private static readonly Lazy<Encoding> Oem = new(() =>
            {
                try
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    int page = 437;
                    try { page = (int)GetOEMCP(); } catch { }
                    try { return Encoding.GetEncoding(page); }
                    catch { return Encoding.GetEncoding(437); }
                }
                catch { return Encoding.Latin1; }
            });

            [System.Runtime.InteropServices.DllImport("kernel32.dll")]
            private static extern uint GetOEMCP();

            private static Encoding For(ReadOnlySpan<byte> bytes)
            {
                try { Strict.GetCharCount(bytes); return Strict; }
                catch (DecoderFallbackException) { return Oem.Value; }
            }

            public override string GetString(byte[] bytes, int index, int count) =>
                For(bytes.AsSpan(index, count)).GetString(bytes, index, count);

            public override int GetByteCount(char[] chars, int index, int count) => UTF8.GetByteCount(chars, index, count);
            public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) =>
                UTF8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
            public override int GetCharCount(byte[] bytes, int index, int count) =>
                For(bytes.AsSpan(index, count)).GetCharCount(bytes, index, count);
            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) =>
                For(bytes.AsSpan(byteIndex, byteCount)).GetChars(bytes, byteIndex, byteCount, chars, charIndex);
            public override int GetMaxByteCount(int charCount) => UTF8.GetMaxByteCount(charCount);
            public override int GetMaxCharCount(int byteCount) => byteCount + 1;
        }

        /// <summary>What is said about a zip whose every file needs a password.</summary>
        public const string ProtectedMessage =
            "This zip is password-protected. Explorer Native can't open protected zips.";

        /// <summary>One entry that needs a password, in a zip where others do not.</summary>
        private const string ProtectedEntry = "it is password-protected, which Explorer Native can't open";

        public static IReadOnlyList<ArchiveEntryInfo> List(string archivePath, CancellationToken token) =>
            List(archivePath, token, out _, out _);

        /// <param name="files">How many entries are files rather than folders.</param>
        /// <param name="locked">How many of those need a password.</param>
        public static IReadOnlyList<ArchiveEntryInfo> List(string archivePath, CancellationToken token,
            out int files, out int locked)
        {
            using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 1 << 16);
            using var archive = Open(file);

            var entries = new List<ArchiveEntryInfo>(archive.Entries.Count);
            files = 0;
            locked = 0;

            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();

                if (!entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\'))
                {
                    files++;
                    if (entry.IsEncrypted) locked++;
                }

                // A directory is an entry whose name ends in a slash and whose
                // Name — the last segment — is therefore empty. Not every zip
                // records its folders at all, which is why extraction creates
                // parents from the file paths rather than trusting these to exist.
                bool isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

                DateTime modified;
                try { modified = entry.LastWriteTime.LocalDateTime; }
                catch { modified = DateTime.Now; }

                entries.Add(new ArchiveEntryInfo(entry.FullName, isDirectory ? 0 : entry.Length,
                    modified, isDirectory));
            }

            return entries;
        }

        /// <summary>
        /// Extracts with one handle per worker.
        ///
        /// Each worker opens the archive itself. That looks wasteful and is not:
        /// a <c>ZipArchive</c> over a read-only file reads the central directory
        /// and then seeks to whichever entry it is asked for, so N handles are N
        /// independent readers of the same file, which is precisely what a
        /// solid-state disk answers best and what a single reader cannot ask for.
        /// The directory is parsed N times, which for an archive of ten thousand
        /// entries is a few milliseconds against however long it takes to write
        /// ten thousand files.
        ///
        /// This is also why extraction cannot be parallelised for the tar
        /// formats: a compressed tar has no directory and no offsets, so the only
        /// way to reach the last entry is to decompress everything before it.
        /// </summary>
        public static int Extract(string archivePath, ExtractionRules rules,
            int threads, ArchiveEngine.Reporter reporter, List<string> errors, CancellationToken token)
        {
            using var probe = new FileStream(archivePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 1 << 16);
            using var index = Open(probe);

            int count = index.Entries.Count;
            if (count == 0) return 0;

            // Folders first and on one thread. Creating them as a side effect of
            // writing the files inside works, but it loses every empty folder in
            // the archive — the same hole the copy engine had, arriving from the
            // other direction.
            //
            // And counted in what was extracted, as the tar reader and the
            // system tar's both count them: "Extracted 7 items" from a zip and
            // "Extracted 9 items" from the same tree as a .tar.gz was the same
            // extraction described two ways.
            int folders = 0;
            foreach (var entry in index.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (!entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\')) continue;

                // Counted as done whatever happens to it: the totals include
                // folder entries, and the count otherwise stopped short.
                reporter.Finished(0);

                var target = rules.TargetFor(entry.FullName, isDirectory: true, out var problem);
                if (target == null)
                {
                    if (problem != null) lock (errors) errors.Add(problem);
                    continue;
                }

                try { Directory.CreateDirectory(target); folders++; }
                catch (Exception ex) { lock (errors) errors.Add($"{entry.FullName}: {ex.Message}"); }
            }

            // Every file's destination is settled here, in archive order and on
            // one thread, so that of two entries whose names differ only in
            // capitals the first in the archive is the one kept — settled by the
            // workers, it was whichever of them got there first.
            var targets = new string?[count];
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var entry = index.Entries[i];
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;

                targets[i] = rules.TargetFor(entry.FullName, isDirectory: false, out var problem);
                if (targets[i] == null && problem != null) lock (errors) errors.Add(problem);
            }

            int next = -1;
            int done = 0;
            int lanes = Math.Clamp(threads, 1, Math.Max(1, count));

            // Which entries got as far as being written or refused. Every
            // replacement is recorded when its destination is settled above, so a
            // cancelled extraction reported every one it had planned — files
            // never touched announced as replaced. The rest are taken back.
            var settled = new int[count];

            var workers = new Task[lanes];
            for (int lane = 0; lane < lanes; lane++)
            {
                workers[lane] = Task.Run(() =>
                {
                    using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite, 1 << 16);
                    using var archive = Open(file);

                    var buffer = new byte[1 << 20];

                    while (true)
                    {
                        token.ThrowIfCancellationRequested();

                        int at = Interlocked.Increment(ref next);
                        if (at >= count) return;

                        var entry = archive.Entries[at];
                        if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;

                        var target = targets[at];
                        if (target == null)
                        {
                            // Skipped is dealt with, and charged its size: the
                            // totals count it, and a skip-everything extract
                            // finished at 0 percent.
                            reporter.Finished(Math.Max(0, entry.Length));
                            continue;
                        }

                        reporter.Starting(entry.FullName);

                        if (entry.IsEncrypted)
                        {
                            lock (errors) errors.Add($"{entry.FullName}: {ProtectedEntry}");
                            rules.Withdraw(target);
                            Volatile.Write(ref settled[at], 1);
                            reporter.Finished(Math.Max(0, entry.Length));
                            continue;
                        }

                        try
                        {
                            // Checked against the archive's own CRC and length
                            // before it is given its name. .NET's ZipArchive does
                            // not check either — measured: an entry with a broken
                            // CRC read back in full without complaint — so a
                            // damaged zip extracted "successfully".
                            ArchiveEngine.WriteCommitted(target, output =>
                            {
                                uint crc = Crc32.Seed;
                                long length = 0;

                                // Open refuses a method .NET has no decoder for;
                                // Read refuses data that will not decompress. Both
                                // say "unsupported compression method", which is
                                // right for the first and misleading for the second.
                                Stream source;
                                try { source = entry.Open(); }
                                catch (InvalidDataException)
                                {
                                    throw new NotSupportedException(
                                        "it is compressed in a way Explorer Native can't read");
                                }

                                using var opened = source;
                                while (true)
                                {
                                    token.ThrowIfCancellationRequested();
                                    int got;
                                    try { got = source.Read(buffer, 0, buffer.Length); }
                                    catch (InvalidDataException)
                                    {
                                        throw new InvalidDataException("it is damaged in the archive");
                                    }
                                    if (got <= 0) break;
                                    output.Write(buffer, 0, got);
                                    crc = Crc32.Append(crc, buffer.AsSpan(0, got));
                                    length += got;
                                    reporter.Advance(got);
                                }

                                if (length != entry.Length)
                                    throw new InvalidDataException(
                                        $"the archive holds {entry.Length:N0} bytes of it and {length:N0} came out");
                                if (crc != entry.Crc32)
                                    throw new InvalidDataException("it is damaged in the archive (its checksum does not match)");
                            });

                            // After the handle is closed, or the write time is
                            // whatever closing the file set it to.
                            try { File.SetLastWriteTime(target, entry.LastWriteTime.LocalDateTime); }
                            catch { }

                            Interlocked.Increment(ref done);
                            Volatile.Write(ref settled[at], 1);
                            reporter.Finished(0);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            lock (errors) errors.Add($"{entry.FullName}: {ex.Message}");
                            rules.Withdraw(target);
                            Volatile.Write(ref settled[at], 1);
                            reporter.Finished(0);
                        }
                    }
                }, token);
            }

            try { Task.WaitAll(workers); }
            catch (AggregateException bundle)
            {
                if (bundle.InnerExceptions.Any(e => e is OperationCanceledException))
                {
                    for (int i = 0; i < count; i++)
                        if (targets[i] is { } unwritten && Volatile.Read(ref settled[i]) == 0)
                            rules.Withdraw(unwritten);
                    throw new OperationCanceledException(token);
                }
                throw bundle.InnerExceptions[0];
            }

            return done + folders;
        }
    }
}
