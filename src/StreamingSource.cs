using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Somewhere a track's bytes can be read from at any offset: a file on a
    /// share, or a file in Google Drive read straight from Google.
    /// </summary>
    public interface IRangeSource : IDisposable
    {
        long Length { get; }

        /// <summary>How many reads are worth running at once.</summary>
        int Streams { get; }

        /// <summary>Up to <c>into.Length</c> bytes at <paramref name="offset"/>; fewer only at the end.</summary>
        Task<int> ReadAsync(long offset, Memory<byte> into, CancellationToken token);
    }

    /// <summary>A file, read with positional asynchronous reads.</summary>
    public sealed class FileRangeSource : IRangeSource
    {
        private readonly FileStream _file;

        public FileRangeSource(string path)
        {
            // Asynchronous, so a read still out when the track is closed can be
            // called off. A process that exits with a read of a cloud placeholder
            // still pending never finishes exiting.
            _file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.RandomAccess | FileOptions.Asynchronous);
            Length = _file.Length;
        }

        public long Length { get; }

        /// <summary>
        /// One. On a share, several readers measured no faster than one: the link
        /// saturates at the same rate either way.
        /// </summary>
        public int Streams => 1;

        public async Task<int> ReadAsync(long offset, Memory<byte> into, CancellationToken token)
        {
            int filled = 0;
            while (filled < into.Length)
            {
                int got = await RandomAccess.ReadAsync(_file.SafeFileHandle, into[filled..], offset + filled, token)
                    .ConfigureAwait(false);
                if (got <= 0) break;
                filled += got;
            }
            return filled;
        }

        public void Dispose()
        {
            try { _file.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// A track that plays while it downloads, the way a video site does it.
    ///
    /// Media Foundation reads through this instead of opening the file itself.
    /// The file is held in memory in pieces, up to a budget, and the pieces
    /// that are downloaded are the ones **at and after where the decoder is
    /// reading**: the held part is a window that follows the track. When the
    /// budget is full, the pieces furthest behind the listener go first, and
    /// up to a quarter of the budget is kept behind so a skip back is free.
    ///
    /// Memory rather than a temporary file: nothing is written to disk.
    ///
    /// **A window that moved was tried once, as Prioritise, and reverted.** A
    /// Prioritise(position) superseded the running download and restarted it at
    /// a skip target, from a single buffer that was a prefix. A skip forward
    /// moved it, a skip back fell behind it, and the download was torn down and
    /// restarted on each change of direction — throwing away the bytes being
    /// played. This one does not do that: the pieces are independent, nothing
    /// is thrown away because of a skip, a download in progress always
    /// finishes, and the next one simply starts from the first missing piece at
    /// the listener. Skipping back into a held piece costs nothing; skipping
    /// ahead of the window reads that one spot directly while the window moves.
    ///
    /// It is an IStream because Media Foundation will wrap one of those into a
    /// byte stream for us. Every method here is called on Media Foundation's
    /// threads, several at once, so the state is under one lock.
    /// </summary>
    public sealed class StreamingSource : IStream, IDisposable
    {
        /// <summary>
        /// The piece size. On a cloud drive every request costs most of a second
        /// whatever is in it, so how fast a track downloads is decided by the
        /// number of requests and hardly at all by their size.
        /// </summary>
        private const int PieceBytes = 4 * 1024 * 1024;

        /// <summary>
        /// The head of the file, fetched first and alone. Everything a decoder
        /// reads while opening a music file is in its first megabyte — a FLAC is
        /// opened by sixteen bytes and then 64KB reads — and one request for it,
        /// with nothing else on the link, measured faster than a small head
        /// followed by a second request fighting four piece downloads: those
        /// took three to four seconds each, and a track five to six to start.
        /// </summary>
        private const int HeadBytes = 1024 * 1024;

        /// <summary>The smallest piece, for a budget smaller than a piece.</summary>
        private const int MinPieceBytes = 64 * 1024;

        /// <summary>This file's head: the above, or less for a small file or a small budget.</summary>
        private readonly int _headBytes;

        /// <summary>
        /// How long a read waits for a piece that is already on its way before
        /// fetching its own bytes. Two fetches of the same bytes on one
        /// connection are slower than one fetch and a wait — but a download that
        /// has stalled must not hold up playback.
        /// </summary>
        private const int PatienceMilliseconds = 400;

        /// <summary>The most a read that cannot wait fetches for itself.</summary>
        private const int DirectBytes = 1024 * 1024;

        /// <summary>How long a failing read keeps being tried before the track gives up.</summary>
        private const int ReadRetryMilliseconds = 20_000;

        private readonly object _gate = new();
        private readonly IRangeSource _source;
        private readonly long _cacheBytes;
        private readonly int _pieceBytes;
        private readonly int _pieceCount;

        /// <summary>
        /// Told once the window is full — the whole file for anything within the
        /// budget, the budget's worth otherwise. Called on a download worker.
        /// </summary>
        private readonly Action<long, long>? _filled;

        private readonly Dictionary<int, byte[]> _pieces = new();
        private readonly HashSet<int> _fetching = new();
        private long _held;
        private long _prefix;
        private bool _released;
        private bool _disposed;
        private bool _saidFilled;

        /// <summary>Where the decoder last read, which is where the window follows.</summary>
        private long _playhead;

        private long _position;

        /// <summary>The first bytes of the file, fetched ahead of everything else.</summary>
        private byte[]? _head;
        private bool _headComing;

        /// <summary>The last bytes fetched directly for a read the window did not have.</summary>
        private byte[]? _loose;
        private long _looseAt;

        /// <summary>
        /// Cancels the downloads of one run. Cancelled on release and dispose,
        /// never disposed: a read may still be registering on it.
        /// </summary>
        private CancellationTokenSource _run = new();

        /// <summary>Calls off direct reads still out when the track is closed.</summary>
        private readonly CancellationTokenSource _closing = new();

        /// <summary>Pulsed whenever a piece lands or the playhead moves.</summary>
        private readonly object _progress = new();

        /// <summary>
        /// Reads of a track's file happening right now, across every instance.
        /// On the Drive letter such reads are answered by this same process, so
        /// the provider must not be taken down while one is out.
        /// </summary>
        private static int _readsInFlight;

        /// <summary>Waits for those reads to finish, and says whether they did.</summary>
        public static bool WaitForReads(TimeSpan limit)
        {
            var clock = Stopwatch.StartNew();
            while (Volatile.Read(ref _readsInFlight) > 0)
            {
                if (clock.Elapsed >= limit) return false;
                Thread.Sleep(10);
            }
            return true;
        }

        /// <summary>
        /// Told about every read and every downloaded piece, for a probe finding
        /// out where a stream is waiting. Null costs one comparison.
        /// </summary>
        internal static Action<string>? Trace { get; set; }

        public long Length { get; }

        /// <summary>
        /// How much of the file is held without a gap, counting from its start.
        /// What the seek searches walk; zero once released.
        /// </summary>
        public long Available => Interlocked.Read(ref _prefix);

        /// <summary>How much of it can be held at once, given the budget.</summary>
        public long Cacheable => Math.Min(Length, _cacheBytes);

        /// <summary>The window is as full as it gets.</summary>
        public bool Complete
        {
            get { lock (_gate) return !_released && !_disposed && Cacheable > 0 && _held >= Cacheable; }
        }

        /// <summary>True while the track is being held in memory.</summary>
        public bool Held
        {
            get { lock (_gate) return !_released && !_disposed && Cacheable > 0; }
        }

        public StreamingSource(string remotePath, long cacheBytes, Action<long, long>? filled = null)
            : this(new FileRangeSource(remotePath), cacheBytes, filled)
        {
        }

        public StreamingSource(IRangeSource source, long cacheBytes, Action<long, long>? filled = null)
        {
            _source = source;
            _cacheBytes = Math.Max(0, cacheBytes);
            _filled = filled;
            Length = source.Length;

            // Pieces no bigger than the budget, so a small budget still holds
            // something; and at least the head's size.
            _pieceBytes = (int)Math.Clamp(Math.Min(PieceBytes, _cacheBytes), MinPieceBytes, PieceBytes);
            _headBytes = (int)Math.Min(Math.Min(HeadBytes, Length), Math.Max(MinPieceBytes, _cacheBytes));
            _pieceCount = (int)((Length + _pieceBytes - 1) / _pieceBytes);

            Start();
        }

        // ---------------- downloading ----------------

        private void Start()
        {
            CancellationToken token;
            lock (_gate)
            {
                if (_disposed || Cacheable <= 0) return;
                _released = false;
                _run = new CancellationTokenSource();
                token = _run.Token;
                if (_head == null && Length > 0) _headComing = true;
            }

            // The pieces wait for the head, so the one request that decides when
            // the track starts has the link to itself.
            _ = Task.Run(async () =>
            {
                await FetchHead(token).ConfigureAwait(false);
                for (int i = 0; i < Math.Max(1, _source.Streams); i++)
                    _ = Task.Run(() => Worker(token));
            });
        }

        private async Task FetchHead(CancellationToken token)
        {
            try
            {
                if (_head != null || Length == 0) return;
                var bytes = new byte[_headBytes];
                Interlocked.Increment(ref _readsInFlight);
                int got;
                try { got = await _source.ReadAsync(0, bytes, token).ConfigureAwait(false); }
                finally { Interlocked.Decrement(ref _readsInFlight); }
                if (got == bytes.Length) lock (_gate) if (!_disposed) _head = bytes;
                Trace?.Invoke($"head fetched: {got:N0}");
            }
            catch (Exception ex) { Trace?.Invoke($"head failed: {ex.Message}"); }
            finally
            {
                lock (_gate) _headComing = false;
                Pulse();
            }
        }

        private async Task Worker(CancellationToken token)
        {
            int failures = 0;
            while (!token.IsCancellationRequested)
            {
                int index;
                lock (_gate)
                {
                    if (_disposed || _released) return;
                    index = NextPiece();
                    if (index >= 0) _fetching.Add(index);
                }

                if (index < 0)
                {
                    // Nothing worth fetching until the listener moves or a piece
                    // is let go of.
                    lock (_progress) Monitor.Wait(_progress, 250);
                    continue;
                }

                long offset = (long)index * _pieceBytes;
                var bytes = new byte[(int)Math.Min(_pieceBytes, Length - offset)];
                bool stored = false;
                try
                {
                    Interlocked.Increment(ref _readsInFlight);
                    int got;
                    try { got = await _source.ReadAsync(offset, bytes, token).ConfigureAwait(false); }
                    finally { Interlocked.Decrement(ref _readsInFlight); }

                    if (got == bytes.Length)
                    {
                        stored = Store(index, bytes, token);
                        failures = 0;
                        Trace?.Invoke($"piece {index} at {offset:N0} held ({_held:N0} held in all)");
                    }
                    else throw new IOException($"the download read {got:N0} of {bytes.Length:N0}");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    Trace?.Invoke($"piece {index} failed: {ex.GetType().Name}: {ex.Message}");
                    // Never given up on while the track is open: a worker that
                    // quit after a few timeouts on a bad link left the window
                    // empty for the rest of the track.
                    failures = Math.Min(failures + 1, 20);
                    try { await Task.Delay(Math.Min(10_000, 500 * failures), token).ConfigureAwait(false); }
                    catch { return; }
                }
                finally
                {
                    lock (_gate) _fetching.Remove(index);
                    if (stored) Pulse();
                }
            }
        }

        /// <summary>
        /// The piece to fetch next, or -1. Under the lock.
        ///
        /// The first missing piece from the listener onwards, as far ahead as
        /// three quarters of the budget. A file that fits the budget is then
        /// filled in behind the listener too, until all of it is held.
        ///
        /// Not the last piece first, which this did for a while because Matroska
        /// keeps its index at the end: every music file paid four megabytes of
        /// link at the moment it was starting. A decoder reading the end gets it
        /// by a direct read, once.
        /// </summary>
        private int NextPiece()
        {
            if (_pieceCount == 0) return -1;
            bool fits = Length <= _cacheBytes;
            int here = (int)(Math.Clamp(_playhead, 0, Math.Max(0, Length - 1)) / _pieceBytes);

            long reach = fits ? long.MaxValue : _cacheBytes * 3 / 4;
            for (int k = here; k < _pieceCount; k++)
            {
                if ((long)(k - here) * _pieceBytes >= reach) break;
                if (Free(k)) return k;
            }

            if (fits)
                for (int k = 0; k < here; k++)
                    if (Free(k)) return k;

            return -1;
        }

        private bool Free(int index) => !_pieces.ContainsKey(index) && !_fetching.Contains(index);

        private bool Store(int index, byte[] bytes, CancellationToken token)
        {
            bool full;
            lock (_gate)
            {
                if (_disposed || _released || token.IsCancellationRequested) return false;
                if (_pieces.ContainsKey(index)) return false;

                MakeRoom(bytes.Length, index);
                _pieces[index] = bytes;
                _held += bytes.Length;
                RecountPrefix();

                full = _held >= Cacheable && !_saidFilled;
                if (full) _saidFilled = true;
            }

            if (full) try { _filled?.Invoke(Available, Length); } catch { }
            return true;
        }

        /// <summary>
        /// Lets pieces go until <paramref name="bytes"/> more fit. Furthest behind
        /// the listener first, then furthest ahead; never the piece being played
        /// or the one being stored. Under the lock.
        /// </summary>
        private void MakeRoom(int bytes, int storing)
        {
            if (_held + bytes <= _cacheBytes) return;
            int here = (int)(Math.Clamp(_playhead, 0, Math.Max(0, Length - 1)) / _pieceBytes);

            var order = new List<int>(_pieces.Keys);
            order.Sort((a, b) =>
            {
                // Behind before ahead; within each, furthest first.
                bool aBehind = a < here, bBehind = b < here;
                if (aBehind != bBehind) return aBehind ? -1 : 1;
                return Math.Abs(b - here).CompareTo(Math.Abs(a - here));
            });

            foreach (var victim in order)
            {
                if (_held + bytes <= _cacheBytes) break;
                if (victim == here || victim == storing) continue;
                _held -= _pieces[victim].Length;
                _pieces.Remove(victim);
            }
        }

        private void RecountPrefix()
        {
            long prefix = 0;
            for (int k = 0; k < _pieceCount && _pieces.TryGetValue(k, out var piece); k++) prefix += piece.Length;
            Interlocked.Exchange(ref _prefix, Math.Min(prefix, Length));
        }

        private void Pulse()
        {
            lock (_progress) Monitor.PulseAll(_progress);
        }

        /// <summary>
        /// Gives the memory back, and stops downloading. The track keeps playing:
        /// reads go straight to the file again. Nothing waits for a download.
        /// </summary>
        public void Release()
        {
            lock (_gate)
            {
                if (_disposed || _released || Cacheable <= 0) return;
                _released = true;
                try { _run.Cancel(); } catch { }
                _pieces.Clear();
                _fetching.Clear();
                _held = 0;
                _saidFilled = false;
                Interlocked.Exchange(ref _prefix, 0);
            }
            Pulse();
        }

        /// <summary>Starts downloading again after a release. Harmless if it never stopped.</summary>
        public void Resume()
        {
            lock (_gate)
            {
                if (_disposed || !_released) return;
            }
            Start();
        }

        // ---------------- reading ----------------

        public void Read(byte[] buffer, int count, IntPtr bytesReadOut)
        {
            long position;
            lock (_gate)
            {
                if (_disposed) { Report(bytesReadOut, 0); return; }
                position = _position;
            }

            int read = count > 0 ? ReadAt(position, buffer.AsSpan(0, Math.Min(count, buffer.Length))) : 0;

            lock (_gate)
            {
                // A seek that landed while the read was out moved the pointer on
                // purpose; the read does not get to put it back.
                if (!_disposed && _position == position) _position = position + read;
            }

            Report(bytesReadOut, read);
        }

        /// <summary>
        /// Reads at a position of the caller's choosing, from memory where the
        /// window has it and from the file where it does not, and moves the
        /// window there. A failing read is tried again for a while rather than
        /// answered with nothing, which is what the end of a file looks like.
        /// </summary>
        public int ReadAt(long position, Span<byte> into)
        {
            Stopwatch? since = null;
            for (int attempt = 0; ; attempt++)
            {
                int got = TryReadAt(position, into, out bool failed);
                if (!failed) return got;

                lock (_gate) if (_disposed) return 0;
                since ??= Stopwatch.StartNew();
                if (attempt >= 2 && since.ElapsedMilliseconds >= ReadRetryMilliseconds) return 0;
                Thread.Sleep(Math.Min(1000, 250 << Math.Min(attempt, 2)));
            }
        }

        private int TryReadAt(long position, Span<byte> into, out bool failed)
        {
            failed = false;
            if (into.Length <= 0 || position < 0) return 0;

            var trace = Trace;
            var clock = trace == null ? null : Stopwatch.StartNew();
            string from = "?";
            int read = 0;

            try
            {
                int wanted;
                bool moved;
                lock (_gate)
                {
                    if (_disposed || position >= Length) return 0;
                    wanted = (int)Math.Min(into.Length, Length - position);

                    moved = _playhead / _pieceBytes != position / _pieceBytes;
                    _playhead = position;

                    if (CopyHeld(position, into[..wanted]))
                    {
                        from = "held";
                        read = wanted;
                    }
                }

                // The workers pick their next piece from here.
                if (moved) Pulse();
                if (read > 0) return read;

                // Coming already? Wait a moment for it rather than fetching the
                // same bytes twice.
                if (WaitIfComing(position, wanted) && CopyHeldLocked(position, into[..wanted]))
                {
                    from = "held after waiting";
                    return read = wanted;
                }

                // Not here: fetched directly, a little more than asked for so the
                // reads that follow it are served from memory too.
                int ask = (int)Math.Min(Math.Max(wanted, DirectBytes), Length - position);
                var bytes = new byte[ask];
                Interlocked.Increment(ref _readsInFlight);
                int got;
                try
                {
                    got = _source.ReadAsync(position, bytes, _closing.Token).GetAwaiter().GetResult();
                }
                finally { Interlocked.Decrement(ref _readsInFlight); }

                if (got <= 0) { failed = true; from = "file, nothing"; return 0; }
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _loose = got == bytes.Length ? bytes : bytes[..got];
                        _looseAt = position;
                    }
                }

                read = Math.Min(got, wanted);
                bytes.AsSpan(0, read).CopyTo(into);
                from = "file";
                return read;
            }
            catch (Exception ex)
            {
                read = 0;
                failed = true;
                from += " FAILED " + ex.GetType().Name + ": " + ex.Message;
                return 0;
            }
            finally
            {
                trace?.Invoke($"read {into.Length:N0} at {position:N0} -> {read:N0} from {from} in {clock!.ElapsedMilliseconds}ms");
            }
        }

        private bool CopyHeldLocked(long position, Span<byte> into)
        {
            lock (_gate) return !_disposed && CopyHeld(position, into);
        }

        /// <summary>
        /// Copies <paramref name="into"/>'s length from whatever holds all of it —
        /// the pieces, the head, or the last direct read. Under the lock.
        /// </summary>
        private bool CopyHeld(long position, Span<byte> into)
        {
            long end = position + into.Length;

            if (_head != null && end <= _head.Length)
            {
                _head.AsSpan((int)position, into.Length).CopyTo(into);
                return true;
            }

            if (_loose != null && position >= _looseAt && end <= _looseAt + _loose.Length)
            {
                _loose.AsSpan((int)(position - _looseAt), into.Length).CopyTo(into);
                return true;
            }

            if (_released) return false;

            // Across as many pieces as it spans, all of them held.
            int first = (int)(position / _pieceBytes), last = (int)((end - 1) / _pieceBytes);
            for (int k = first; k <= last; k++)
                if (!_pieces.ContainsKey(k)) return false;

            int done = 0;
            for (int k = first; k <= last; k++)
            {
                var piece = _pieces[k];
                long start = (long)k * _pieceBytes;
                int from = (int)Math.Max(0, position + done - start);
                int take = Math.Min(piece.Length - from, into.Length - done);
                piece.AsSpan(from, take).CopyTo(into[done..]);
                done += take;
            }
            return done == into.Length;
        }

        /// <summary>
        /// Waits, briefly, when the bytes are already on their way: the head
        /// while it is being fetched, or a piece a worker has started. True when
        /// it waited for something.
        /// </summary>
        private bool WaitIfComing(long position, int count)
        {
            long end = position + count;
            var clock = Stopwatch.StartNew();
            bool waited = false;

            while (true)
            {
                bool coming;
                lock (_gate)
                {
                    if (_disposed) return waited;
                    bool headCovers = end <= _headBytes && _headComing;
                    int first = (int)(position / _pieceBytes), last = (int)((end - 1) / _pieceBytes);
                    bool piecesCover = true, anyFetching = false;
                    for (int k = first; k <= last; k++)
                    {
                        if (_pieces.ContainsKey(k)) continue;
                        if (_fetching.Contains(k)) anyFetching = true;
                        else piecesCover = false;
                    }
                    coming = headCovers || (piecesCover && anyFetching);
                    if (CopyHeldProbe(position, count)) return true;
                }

                // The head gets longer: it is one small request, and the file
                // cannot be opened without it.
                int patience = end <= _headBytes ? 3000 : PatienceMilliseconds;
                if (!coming || clock.ElapsedMilliseconds >= patience) return waited;

                waited = true;
                lock (_progress) Monitor.Wait(_progress, 50);
            }
        }

        /// <summary>Whether a copy would succeed. Under the lock.</summary>
        private bool CopyHeldProbe(long position, int count)
        {
            long end = position + count;
            if (_head != null && end <= _head.Length) return true;
            if (_loose != null && position >= _looseAt && end <= _looseAt + _loose.Length) return true;
            if (_released) return false;
            int first = (int)(position / _pieceBytes), last = (int)((end - 1) / _pieceBytes);
            for (int k = first; k <= last; k++)
                if (!_pieces.ContainsKey(k)) return false;
            return true;
        }

        /// <summary>Whether every byte from <paramref name="from"/> up to <paramref name="to"/> is held.</summary>
        public bool Holds(long from, long to)
        {
            if (from < 0) return false;
            to = Math.Min(to, Length);
            if (to <= from) return true;
            lock (_gate)
                return !_disposed && !_released && Cacheable > 0 && CopyHeldProbe(from, (int)Math.Min(int.MaxValue, to - from));
        }

        /// <summary>
        /// Copies held bytes without touching the read position or the window,
        /// for something other than the decoder looking inside the track — see
        /// <see cref="ExactSeek"/>. Never goes to the file. Returns how many bytes
        /// it copied: the held run from <paramref name="position"/>.
        /// </summary>
        public int ReadHeld(long position, Span<byte> into)
        {
            if (position < 0) return 0;
            lock (_gate)
            {
                if (_disposed || _released || Cacheable <= 0 || position >= Length) return 0;

                int done = 0;
                while (done < into.Length && position + done < Length)
                {
                    int k = (int)((position + done) / _pieceBytes);
                    if (!_pieces.TryGetValue(k, out var piece)) break;
                    long start = (long)k * _pieceBytes;
                    int from = (int)(position + done - start);
                    int take = Math.Min(piece.Length - from, into.Length - done);
                    piece.AsSpan(from, take).CopyTo(into[done..]);
                    done += take;
                }
                return done;
            }
        }

        public void Seek(long move, int origin, IntPtr newPositionOut)
        {
            lock (_gate)
            {
                long from = origin switch
                {
                    1 => _position,     // STREAM_SEEK_CUR
                    2 => Length,        // STREAM_SEEK_END
                    _ => 0,             // STREAM_SEEK_SET
                };

                _position = Math.Clamp(from + move, 0, Length);
                Report(newPositionOut, _position);
            }
        }

        public void Stat(out STATSTG stat, int flags)
        {
            stat = new STATSTG
            {
                type = 2,               // STGTY_STREAM
                cbSize = Length,
                grfMode = 0,            // STGM_READ
            };
        }

        private static void Report(IntPtr destination, long value)
        {
            if (destination != IntPtr.Zero) Marshal.WriteInt64(destination, value);
        }

        private static void Report(IntPtr destination, int value)
        {
            if (destination != IntPtr.Zero) Marshal.WriteInt32(destination, value);
        }

        // Read-only, and never cloned. Media Foundation's wrapper asks for none of
        // these when playing; refusing is the only honest answer, since there is
        // nothing sensible to write into somebody's track.
        public void Write(byte[] buffer, int count, IntPtr written) => throw new NotSupportedException();
        public void SetSize(long newSize) => throw new NotSupportedException();
        public void CopyTo(IStream target, long count, IntPtr read, IntPtr written) => throw new NotSupportedException();
        public void Commit(int flags) => throw new NotSupportedException();
        public void Revert() => throw new NotSupportedException();
        public void LockRegion(long offset, long count, int lockType) => throw new NotSupportedException();
        public void UnlockRegion(long offset, long count, int lockType) => throw new NotSupportedException();
        public void Clone(out IStream copy) => throw new NotSupportedException();

        public void Dispose()
        {
            // Without waiting for anything: changing track disposes the last one
            // from the thread the player is driven from. Downloads and reads still
            // out are called off — a read of a Drive placeholder left pending
            // when the process ends leaves it unable to finish exiting.
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                try { _run.Cancel(); } catch { }
                try { _closing.Cancel(); } catch { }
                _pieces.Clear();
                _fetching.Clear();
                _held = 0;
                _head = null;
                _loose = null;
                Interlocked.Exchange(ref _prefix, 0);
            }
            Pulse();

            // After the reads have been told to stop; a read still finishing
            // holds its own reference to the handle.
            try { _source.Dispose(); } catch { }
        }

        /// <summary>
        /// Removes the on-disk half-copies an older build left behind. Nothing is
        /// written to disk any more, so this only ever cleans up after that one.
        /// </summary>
        public static void SweepOldCopies()
        {
            try
            {
                var old = Path.Combine(Path.GetTempPath(), "ExplorerNative", "playing");
                if (!Directory.Exists(old)) return;
                foreach (var file in Directory.GetFiles(old))
                {
                    try { File.Delete(file); } catch { }
                }
                try { Directory.Delete(old); } catch { }
            }
            catch { }
        }
    }
}
