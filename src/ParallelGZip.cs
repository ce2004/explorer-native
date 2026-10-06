using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Gzip that uses every core, by writing a gzip file the way the format says
    /// one may be written: as a series of members.
    ///
    /// Deflate is a single stream of back-references into the last 32KB of what
    /// it has already emitted, so one deflate stream cannot be produced by more
    /// than one thread. That is the whole difficulty, and it is why gzip is
    /// single-threaded everywhere it appears — including in the tar.exe Windows
    /// ships, measured here at 11 MB/s with one core busy and the rest idle.
    ///
    /// RFC 1952 section 2.2: "A gzip file consists of a series of 'members'
    /// (compressed data sets)." Members are self-contained — header, deflate
    /// stream, checksum, length — and a decompressor is required to run through
    /// all of them and concatenate the results. So the input is cut into chunks,
    /// each chunk is compressed into a member of its own by whichever worker is
    /// free, and the members are written out in order. The result is an ordinary
    /// .gz that gzip, 7-Zip, bsdtar, .NET and every browser read as one file,
    /// because it is one.
    ///
    /// This is what pigz does, and it costs about what pigz costs. A chunk cannot
    /// refer back into the chunk before it, so the first 32KB of each one — the
    /// length of the window — compresses without history. At
    /// <see cref="ChunkBytes"/> that is under one percent of the data, plus
    /// eighteen bytes of member overhead per chunk. Measured on the corpus in
    /// CLAUDE.md: 41 MB/s against the system tar's 11, for an archive **0.09%**
    /// larger than the one single-stream deflate produces.
    ///
    /// One honest limitation, and it is the format's rather than this code's: the
    /// four-byte uncompressed length in the trailer belongs to the last member,
    /// not to the file. Anything that reads the size of a .gz without
    /// decompressing it — "gzip -l" — reports the size of the final chunk. That
    /// is equally true of anything pigz produces and of two .gz files
    /// concatenated with copy, and it is not true of anything that actually
    /// decompresses, which is everything that matters.
    /// </summary>
    public static class ParallelGZip
    {
        /// <summary>
        /// How much input becomes one member.
        ///
        /// The trade is ratio against memory, and it is decided by deflate's 32KB
        /// window rather than by taste. Every chunk begins with no history, so
        /// the fraction of the data compressed worse is 32KB divided by this —
        /// 0.8% at two megabytes, 3% at half a megabyte, 25% at 128KB. Going the
        /// other way, in-flight memory is roughly this times the worker count
        /// twice over (a chunk being compressed and a chunk waiting to be
        /// written), so eight megabytes here would be 160MB outstanding on a
        /// ten-core machine to buy a ratio improvement in the third decimal
        /// place.
        /// </summary>
        public const int ChunkBytes = 2 * 1024 * 1024;

        /// <summary>
        /// A write-only stream that gzips what is written to it, in parallel, and
        /// emits the members in order.
        ///
        /// It is a Stream rather than a "compress this file" function because the
        /// thing being compressed is usually not a file: it is a tar being
        /// generated on the fly by <see cref="TarEngine"/>, which would otherwise
        /// have to be written to disk in full before compression could start.
        /// </summary>
        public sealed class Writer : Stream
        {
            private readonly Stream _destination;
            private readonly CompressionLevel _level;
            private readonly CancellationToken _token;
            private readonly bool _leaveOpen;

            /// <summary>
            /// Chunks in flight, in order. Bounded, which is what keeps memory
            /// under control: a producer that outruns the workers blocks here
            /// rather than queueing the whole input.
            /// </summary>
            private readonly BlockingCollection<Task<Chunk>> _queue;

            /// <summary>
            /// How many chunks may be *being compressed* at once, which is not the
            /// same as how many may be queued.
            ///
            /// Taken here and released by the worker the moment its member is
            /// finished — not by the emitter when it writes one out. Releasing it
            /// at the far end would mean a chunk that had already been compressed
            /// went on holding a worker's place in the queue while it waited its
            /// turn to be written, and the number actually compressing would
            /// collapse towards the speed of the one thread doing the writing.
            /// That is exactly the bug the zip writer had, found by measuring
            /// rather than by reading: asking for one worker and asking for
            /// sixteen produced the same wall clock.
            /// </summary>
            private readonly SemaphoreSlim _lane;

            private readonly int _lanes;

            private readonly Task _emitter;

            private byte[] _current;
            private int _used;
            private long _written;
            private bool _closed;

            /// <summary>
            /// The first thing that went wrong, kept so it can be thrown from the
            /// caller's own thread rather than lost on a worker.
            /// </summary>
            private Exception? _failure;

            private sealed record Chunk(byte[] Buffer, int Length);

            /// <summary>
            /// One raw deflate stream rather than gzip members — what a zip entry
            /// holds. See the constructor.
            /// </summary>
            private readonly bool _raw;

            /// <summary>
            /// A final, empty fixed-Huffman block: what zlib itself writes to end a
            /// stream with nothing left to say. BFINAL set, block type 01, and the
            /// end-of-block code.
            /// </summary>
            private static readonly byte[] FinalBlock = { 0x03, 0x00 };

            /// <param name="raw">
            /// One deflate stream instead of a series of gzip members, for a zip
            /// entry too big to hand to a worker whole. Each chunk is deflated on
            /// its own and ended with a sync flush, which leaves it byte-aligned
            /// and not final, so the chunks joined end to end are one valid stream
            /// once a final empty block is added — the same cut pigz makes, and
            /// the same price: each chunk starts without the 32KB of history the
            /// one before it would have given. A zip of one 70MB log was deflated
            /// by the writer thread alone, and was most of the time of the whole
            /// archive.
            /// </param>
            public Writer(Stream destination, int threads, CompressionLevel level,
                CancellationToken token, bool leaveOpen = false, bool raw = false)
            {
                _raw = raw;
                _destination = destination;
                _level = level;
                _token = token;
                _leaveOpen = leaveOpen;

                _lanes = Math.Max(1, threads);
                _lane = new SemaphoreSlim(_lanes, _lanes);

                // Two chunks per worker plus a couple: enough that a worker never
                // waits for the writer to catch up, few enough that the memory is
                // a number rather than a function of how fast the input arrives.
                _queue = new BlockingCollection<Task<Chunk>>(_lanes * 2 + 2);

                _current = new byte[ChunkBytes];
                _emitter = Task.Run(Emit);
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => _written;
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count) =>
                Write(new ReadOnlySpan<byte>(buffer, offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                ThrowIfFailed();

                while (!buffer.IsEmpty)
                {
                    _token.ThrowIfCancellationRequested();

                    int room = ChunkBytes - _used;
                    int take = Math.Min(room, buffer.Length);
                    buffer[..take].CopyTo(_current.AsSpan(_used));
                    _used += take;
                    _written += take;
                    buffer = buffer[take..];

                    if (_used == ChunkBytes) Dispatch();
                }
            }

            public override void WriteByte(byte value)
            {
                Span<byte> one = stackalloc byte[1];
                one[0] = value;
                Write(one);
            }

            /// <summary>
            /// Hands the filled buffer to a worker and takes a fresh one.
            ///
            /// The buffer is given away rather than copied out of — a chunk is two
            /// megabytes and copying every one of them would be a second pass over
            /// the whole input for no reason. The replacement is allocated here
            /// because the old one belongs to a worker until the member is
            /// written.
            /// </summary>
            private void Dispatch(bool evenIfEmpty = false)
            {
                if (_used == 0 && !evenIfEmpty) return;

                var buffer = _current;
                int length = _used;
                _current = new byte[ChunkBytes];
                _used = 0;

                var level = _level;
                bool raw = _raw;

                _lane.Wait(_token);

                var work = Task.Run(() =>
                {
                    try
                    {
                        if (raw)
                        {
                            // Flushed, not closed: the flush ends the chunk on a
                            // byte boundary with a block that is not the last, and
                            // only what was written by then is kept. Closing it
                            // would add a final block in the middle of the entry.
                            var part = new MemoryStream(Math.Max(64, length / 2));
                            var deflate = new DeflateStream(part, level, leaveOpen: true);
                            deflate.Write(buffer, 0, length);
                            deflate.Flush();
                            int kept = (int)part.Length;
                            deflate.Dispose();
                            return new Chunk(part.GetBuffer(), kept);
                        }

                        // A member is a header, a deflate stream and a trailer, and
                        // GZipStream writes all three. Nothing here hand-rolls the
                        // format: the only thing being done by hand is deciding
                        // which bytes go into which member.
                        var into = new MemoryStream(Math.Max(64, length / 2));
                        using (var gzip = new GZipStream(into, level, leaveOpen: true))
                            gzip.Write(buffer, 0, length);

                        return new Chunk(into.GetBuffer(), (int)into.Length);
                    }
                    finally
                    {
                        try { if (_lane.CurrentCount < _lanes) _lane.Release(); }
                        catch (ObjectDisposedException) { }
                        catch (SemaphoreFullException) { }
                    }
                }, _token);

                try { _queue.Add(work, _token); }
                catch (Exception) { work.ContinueWith(_ => { }, TaskScheduler.Default); throw; }
            }

            /// <summary>
            /// Writes finished members to the destination, in the order they were
            /// dispatched.
            ///
            /// One thread, deliberately. The output is a file being appended to
            /// and there is exactly one right order for what goes into it, so
            /// there is nothing here to parallelise — the work this feature had to
            /// spread across cores is the deflate, and that is already elsewhere.
            /// </summary>
            private void Emit()
            {
                try
                {
                    foreach (var work in _queue.GetConsumingEnumerable())
                    {
                        var chunk = work.GetAwaiter().GetResult();
                        _destination.Write(chunk.Buffer, 0, chunk.Length);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref _failure, ex, null);

                    // Drain, so nothing dispatched behind the failure is left
                    // holding a chunk and so Dispatch never blocks on a queue
                    // nobody is reading. The results are discarded; the first
                    // exception is the one reported.
                    try
                    {
                        foreach (var rest in _queue.GetConsumingEnumerable())
                            rest.ContinueWith(_ => { }, TaskScheduler.Default);
                    }
                    catch { }
                }
            }

            private void ThrowIfFailed()
            {
                var failure = _failure;
                if (failure == null) return;

                // A cancellation stays a cancellation. Disposal finishes the last
                // member, which takes a lane — and taking a lane on a token that
                // has just been cancelled throws, so the failure recorded here is
                // routinely the cancellation itself. Wrapped in an IOException it
                // stopped matching the catch that reports "Compressing cancelled"
                // and a deliberate Escape came back as "Could not compress:
                // Compression failed: The operation was canceled".
                if (failure is OperationCanceledException cancelled)
                    throw new OperationCanceledException(cancelled.Message, cancelled);

                throw new IOException("Compression failed: " + failure.Message, failure);
            }

            /// <summary>
            /// Flush cannot mean what it usually means here, and quietly doing
            /// nothing is the honest answer.
            ///
            /// Flushing a compressor normally means "end the block so what I have
            /// given you so far is readable". Doing that per call would end a
            /// member per call, and a TarWriter that flushes after each entry
            /// would turn a folder of ten thousand small files into ten thousand
            /// gzip members — eighteen bytes of overhead each and no compression
            /// across any of them. Nothing waits to read this stream while it is
            /// being written, so there is nobody for a flush to be for.
            /// </summary>
            public override void Flush() => ThrowIfFailed();

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (!disposing || _closed) { base.Dispose(disposing); return; }
                _closed = true;

                try
                {
                    // The tail is a member like any other, however short.
                    // And an empty input is one empty member: a zero-byte .gz is
                    // not a gzip file, and everything refuses to open it.
                    // A raw stream has no members, so nothing is dispatched for
                    // an empty one: the final block below is the whole of it.
                    if (_failure == null && (_used > 0 || (_written == 0 && !_raw))) Dispatch(evenIfEmpty: true);
                }
                catch (Exception ex) { Interlocked.CompareExchange(ref _failure, ex, null); }

                _queue.CompleteAdding();

                try { _emitter.GetAwaiter().GetResult(); }
                catch (Exception ex) { Interlocked.CompareExchange(ref _failure, ex, null); }

                // The end of the one deflate stream, after every chunk.
                if (_raw && _failure == null)
                {
                    try { _destination.Write(FinalBlock); }
                    catch (Exception ex) { Interlocked.CompareExchange(ref _failure, ex, null); }
                }

                _queue.Dispose();
                _lane.Dispose();

                if (!_leaveOpen) _destination.Dispose();

                base.Dispose(disposing);

                // Thrown last, so the destination is closed either way. A caller
                // that sees this has a half-written archive, which is what the
                // engines above delete on the way out.
                ThrowIfFailed();
            }
        }

        /// <summary>
        /// Gzips one file into another, in parallel.
        ///
        /// The single-file .gz path. There is no container and no name inside the
        /// result: what comes back out is the bytes that went in, which is why
        /// <see cref="ArchiveFormats.SuggestedName"/> keeps the whole original
        /// name — "song.flac.gz" is the only record that it was a .flac.
        /// </summary>
        public static void CompressFile(string source, string destination, int threads,
            CompressionLevel level, Action<long>? read, CancellationToken token)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
                FileShare.None, 1 << 20);
            using var writer = new Writer(output, threads, level, token, leaveOpen: true);

            var buffer = new byte[1 << 20];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int got = input.Read(buffer, 0, buffer.Length);
                if (got <= 0) break;
                writer.Write(buffer, 0, got);
                read?.Invoke(got);
            }
        }
    }
}
