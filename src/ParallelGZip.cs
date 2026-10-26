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
                _emitter = ArchiveWorkers.RunLong(Emit);
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
            /// <summary>
            /// One empty gzip member: the header (deflate, no flags, no time,
            /// unknown OS), a final empty fixed-Huffman block, and a trailer of
            /// CRC 0 and length 0.
            /// </summary>
            private static readonly byte[] EmptyMember =
            {
                0x1f, 0x8b, 0x08, 0x00, 0, 0, 0, 0, 0x00, 0xff,
                0x03, 0x00,
                0, 0, 0, 0, 0, 0, 0, 0,
            };

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

                var work = ArchiveWorkers.Run(() =>
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
                        // Except for an empty input: since .NET 9 (zlib-ng) a
                        // GZipStream that was never given a byte writes nothing,
                        // not an empty member, and a zero-byte .gz does not open.
                        if (length == 0) return new Chunk((byte[])EmptyMember.Clone(), EmptyMember.Length);

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
        /// Reads a gzip file on every core, when it is made of members the way
        /// <see cref="Writer"/> makes them, and straight through
        /// <see cref="GZipStream"/> when it is not.
        ///
        /// A member cannot be found without decompressing what is before it,
        /// in general. But a member that <see cref="Writer"/> (or .NET, or
        /// zlib) wrote starts with the same ten bytes every time: the magic,
        /// deflate, no flags and no time. So the file is cut at every place
        /// those bytes appear, each piece is inflated by a worker on its own,
        /// and a piece is believed only when it holds exactly one member: what
        /// came out has the checksum and length in the last eight bytes of the
        /// piece. The first piece starts at the start of the file, and every
        /// believed piece ends where the next begins, so every cut that is
        /// used is a real boundary between members.
        ///
        /// Anything else — a member written by another tool (a name, a time),
        /// a file of one member, the ten bytes turning up by chance inside the
        /// compressed data, damage, something after the end — makes a piece
        /// that is not believed. From the start of that piece the rest of the
        /// file is read the ordinary way, one thread and GZipStream, exactly as
        /// before, so what comes out and every error it raises are what they
        /// always were. Measured on 1GB of ParallelGZip's own output, eight
        /// cores: see CLAUDE.md, Compressing and extracting.
        /// </summary>
        public sealed class Reader : Stream
        {
            /// <summary>The ten bytes every plain member begins with, less the last two (XFL and OS vary).</summary>
            private static readonly byte[] PlainHeader = { 0x1f, 0x8b, 0x08, 0x00, 0, 0, 0, 0 };

            /// <summary>
            /// The longest piece looked for before giving up on finding the
            /// next member: a member of <see cref="ChunkBytes"/> compresses to
            /// at most a little over that, so a piece this long is a file made
            /// by something else, read the ordinary way.
            /// </summary>
            internal const int MaxPiece = 8 * 1024 * 1024;

            /// <summary>The most one piece may inflate to before it is not believed.</summary>
            internal const int MaxOutput = 64 * 1024 * 1024;

            private const int ReadBlock = 1 << 20;

            private readonly Stream _file;
            private readonly long _origin;
            private readonly int _ahead;
            private readonly CancellationToken _token;
            private readonly bool _leaveOpen;

            private readonly System.Collections.Generic.Queue<Task<Piece>> _pending = new();

            /// <summary>What has been read from the file and not yet handed to a worker; starts at file offset _bufferAt.</summary>
            private byte[] _buffer = new byte[2 * ReadBlock];
            private int _buffered;
            private long _bufferAt;
            private int _searchedTo;
            private bool _fileEnded;
            private bool _cutsDone;

            private Piece? _current;
            private int _currentAt;

            /// <summary>Set once the pieces stopped being believed: the rest of the file, read the ordinary way.</summary>
            private GZipStream? _plain;
            private CountedRead? _plainSource;
            private long _plainFrom;

            private long _consumed;

            /// <summary>For the tests: how many pieces were inflated in parallel, and whether the ordinary way took over.</summary>
            internal int ParallelPieces { get; private set; }
            internal bool FellBack => _plain != null;

            private sealed record Piece(long Start, long End, byte[]? Data, int Length);

            /// <param name="file">Readable and seekable, positioned at the start of the gzip data.</param>
            public Reader(Stream file, int threads, CancellationToken token, bool leaveOpen = true)
            {
                _file = file;
                _origin = file.Position;
                _bufferAt = _origin;
                _ahead = Math.Max(2, Math.Max(1, threads) * 2);
                _token = token;
                _leaveOpen = leaveOpen;

                // One thread, or a stream that cannot be repositioned: nothing
                // to gain, and no way back to the ordinary read if a piece is
                // not believed. Read the ordinary way from the start.
                if (threads <= 1 || !file.CanSeek) StartPlain(_origin, seek: false);
            }

            /// <summary>How far through the file reading has got, for progress.</summary>
            public long Consumed => Interlocked.Read(ref _consumed);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> into)
            {
                if (into.IsEmpty) return 0;

                while (true)
                {
                    _token.ThrowIfCancellationRequested();

                    if (_plain != null)
                    {
                        int got = _plain.Read(into);
                        Interlocked.Exchange(ref _consumed, _plainFrom - _origin + _plainSource!.Count);
                        return got;
                    }

                    if (_current != null && _currentAt < _current.Length)
                    {
                        int take = Math.Min(into.Length, _current.Length - _currentAt);
                        _current.Data.AsSpan(_currentAt, take).CopyTo(into);
                        _currentAt += take;
                        return take;
                    }

                    _current = null;
                    TopUp();
                    if (_pending.Count == 0) return 0;

                    var next = _pending.Dequeue();
                    Piece piece;
                    try
                    {
                        next.Wait(_token);
                        piece = next.Result;
                    }
                    catch (AggregateException bundle) when (bundle.InnerException is OperationCanceledException cancelled)
                    {
                        throw new OperationCanceledException(cancelled.Message, cancelled, _token);
                    }

                    if (piece.Data == null)
                    {
                        // Not believed: from here on, the ordinary way.
                        StartPlain(piece.Start, seek: true);
                        continue;
                    }

                    Interlocked.Exchange(ref _consumed, piece.End - _origin);
                    _current = piece;
                    _currentAt = 0;
                }
            }

            /// <summary>Keeps enough pieces in the workers' hands that none of them waits.</summary>
            private void TopUp()
            {
                while (!_cutsDone && _pending.Count < _ahead)
                {
                    _token.ThrowIfCancellationRequested();
                    if (!NextPiece()) break;
                }
            }

            /// <summary>Cuts off the next piece and hands it to a worker. False when there is no more.</summary>
            private bool NextPiece()
            {
                while (true)
                {
                    // The next header after this piece's own.
                    int from = Math.Max(_searchedTo, 1);
                    int found = -1;
                    if (_buffered - from >= PlainHeader.Length)
                    {
                        int at = _buffer.AsSpan(from, _buffered - from).IndexOf(PlainHeader);
                        if (at >= 0) found = from + at;
                        else _searchedTo = _buffered - PlainHeader.Length + 1;
                    }

                    if (found > 0)
                    {
                        Dispatch(found, last: false);
                        return true;
                    }

                    if (_fileEnded)
                    {
                        if (_buffered == 0) { _cutsDone = true; return false; }
                        Dispatch(_buffered, last: true);
                        _cutsDone = true;
                        return true;
                    }

                    if (_buffered >= MaxPiece)
                    {
                        // No member boundary where one of ours would have one:
                        // not our kind of file. Everything from here is read
                        // the ordinary way.
                        _pending.Enqueue(Task.FromResult(new Piece(_bufferAt, _bufferAt, null, 0)));
                        _cutsDone = true;
                        return true;
                    }

                    Fill();
                }
            }

            private void Fill()
            {
                if (_buffer.Length - _buffered < ReadBlock)
                    Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _buffered + ReadBlock));

                int got = _file.Read(_buffer, _buffered, ReadBlock);
                if (got <= 0) _fileEnded = true;
                else _buffered += got;
            }

            private void Dispatch(int length, bool last)
            {
                var bytes = _buffer.AsSpan(0, length).ToArray();
                long start = _bufferAt;
                long end = _bufferAt + length;

                // What is left becomes the front of the buffer.
                int rest = _buffered - length;
                Buffer.BlockCopy(_buffer, length, _buffer, 0, rest);
                _buffered = rest;
                _bufferAt = end;
                _searchedTo = 0;

                ParallelPieces++;
                _pending.Enqueue(ArchiveWorkers.Run(() => Inflate(bytes, start, end)));
            }

            /// <summary>
            /// One piece, inflated and believed only if it is exactly one whole
            /// member; otherwise a piece with no data, which hands over to the
            /// ordinary read.
            /// </summary>
            private static Piece Inflate(byte[] bytes, long start, long end)
            {
                var refused = new Piece(start, end, null, 0);

                // A header, the smallest deflate block and a trailer.
                if (bytes.Length < 20) return refused;

                uint storedCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8));
                uint storedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4));

                try
                {
                    int guess = storedSize <= MaxOutput ? (int)storedSize : bytes.Length * 4;
                    var output = new byte[Math.Max(64, Math.Min(MaxOutput, guess))];
                    int length = 0;

                    using (var gzip = new GZipStream(new MemoryStream(bytes, writable: false), CompressionMode.Decompress))
                    {
                        while (true)
                        {
                            if (length == output.Length)
                            {
                                if (output.Length >= MaxOutput) return refused;
                                Array.Resize(ref output, (int)Math.Min(MaxOutput, (long)output.Length * 2));
                            }
                            int got = gzip.Read(output, length, output.Length - length);
                            if (got <= 0) break;
                            length += got;
                        }
                    }

                    if ((uint)length != storedSize) return refused;
                    if (Crc32.Append(Crc32.Seed, output.AsSpan(0, length)) != storedCrc) return refused;
                    return new Piece(start, end, output, length);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
                {
                    return refused;
                }
            }

            private void StartPlain(long from, bool seek)
            {
                // Whatever is still being inflated is not wanted; its result is
                // dropped when it ends.
                while (_pending.Count > 0)
                    _pending.Dequeue().ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                _cutsDone = true;
                _buffer = Array.Empty<byte>();
                _buffered = 0;

                if (seek) _file.Position = from;
                _plainFrom = from;
                _plainSource = new CountedRead(_file);
                _plain = new GZipStream(_plainSource, CompressionMode.Decompress, leaveOpen: true);
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    while (_pending.Count > 0)
                        _pending.Dequeue().ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                    _plain?.Dispose();
                    if (!_leaveOpen) _file.Dispose();
                }
                base.Dispose(disposing);
            }

            /// <summary>A pass-through that counts what the ordinary read has taken, for progress.</summary>
            private sealed class CountedRead : Stream
            {
                private readonly Stream _inner;
                private long _count;
                public CountedRead(Stream inner) => _inner = inner;
                public long Count => Interlocked.Read(ref _count);
                public override bool CanRead => true;
                public override bool CanSeek => false;
                public override bool CanWrite => false;
                public override long Length => _inner.Length;
                public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
                public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
                public override int Read(Span<byte> buffer)
                {
                    int got = _inner.Read(buffer);
                    if (got > 0) Interlocked.Add(ref _count, got);
                    return got;
                }
                public override void Flush() { }
                public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
                public override void SetLength(long value) => throw new NotSupportedException();
                public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
                protected override void Dispose(bool disposing) { }
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
