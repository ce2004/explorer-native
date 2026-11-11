using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// A file sent to Google Drive while it is still being written, with
    /// nothing on the PC's disk (6.1.0, Conner: "if the file is 100 gigs alone
    /// it should split the file and handle putting it all back together").
    ///
    /// Whatever writes here (an audio encoder) sees an ordinary seekable
    /// stream. What it has written is cut into pieces of
    /// <see cref="PieceBytes"/> and handed to <see cref="Sender"/> in order, a
    /// few held in memory at most: a writer faster than the upload waits. The
    /// newest piece or two are kept back, so going back a little costs nothing
    /// and the last piece can carry the total.
    ///
    /// An encoder that goes back to finish its header writes below what has
    /// already gone. Those bytes are kept in <see cref="Late"/> (they are
    /// small) and whoever owns the upload puts them in afterwards. A write
    /// that changes nothing is not late.
    /// </summary>
    internal sealed class SpoolStream : Stream
    {
        /// <summary>Every piece but the last. A multiple of Drive's 256 KiB.</summary>
        internal const int PieceBytes = 8 * 1024 * 1024;

        /// <summary>The start of the file, kept as last written, to tell a real change from a rewrite of the same bytes.</summary>
        internal const int HeadBytes = 1024 * 1024;

        /// <summary>Late writes held at most: more than this is not a header being finished.</summary>
        internal const long LateMost = 64L * 1024 * 1024;

        /// <summary>Sends one piece and returns once it is held. <paramref name="total"/> is given with the last.</summary>
        public delegate void Sender(long offset, byte[] buffer, int count, long? total);

        private readonly Sender _send;
        private readonly int _piece;
        private readonly CancellationToken _token;
        private readonly object _gate = new();
        private readonly BlockingCollection<(long Offset, byte[] Buffer, int Count)> _queue;
        private readonly Thread _uploader;
        private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

        private byte[] _tail;
        private int _tailLength;
        private long _frozen;
        private long _position, _length;
        private readonly byte[] _head = new byte[HeadBytes];
        private readonly List<(long Offset, byte[] Bytes)> _late = new();
        private long _lateBytes;
        private volatile Exception? _failed;
        private bool _completed, _stopped;
        private string? _md5Hex;
        private long _sent;

        public SpoolStream(Sender send, CancellationToken token, int pieceBytes = PieceBytes, int queued = 3)
        {
            _send = send;
            _token = token;
            _piece = pieceBytes;
            _tail = new byte[Math.Min(pieceBytes, 1 << 20)];
            _queue = new BlockingCollection<(long, byte[], int)>(Math.Max(1, queued));
            _uploader = new Thread(Upload) { IsBackground = true, Name = "Drive spool" };
            _uploader.Start();
        }

        /// <summary>Writes below what had already been sent, oldest first. Read after <see cref="Complete"/>.</summary>
        public IReadOnlyList<(long Offset, byte[] Bytes)> Late => _late;

        /// <summary>The MD5 of what was sent, in hex. Not the file's once anything is in <see cref="Late"/>.</summary>
        public string Md5Hex => _md5Hex ?? "";

        /// <summary>Bytes Google holds so far.</summary>
        public long Sent => Interlocked.Read(ref _sent);

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length { get { lock (_gate) return _length; } }

        public override long Position
        {
            get { lock (_gate) return _position; }
            set { lock (_gate) _position = Math.Max(0, value); }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (_gate)
            {
                long from = origin switch { SeekOrigin.Current => _position, SeekOrigin.End => _length, _ => 0 };
                _position = Math.Max(0, from + offset);
                return _position;
            }
        }

        /// <summary>Only what has been written counts: a size set ahead (Media Foundation does) is not content.</summary>
        public override void SetLength(long value) { }

        public override void Flush() { }

        private void ThrowIfFailed()
        {
            if (_failed is { } failure)
                throw new IOException("sending it to Google Drive failed: " + failure.Message, failure);
            _token.ThrowIfCancellationRequested();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            lock (_gate)
            {
                if (_completed) throw new ObjectDisposedException(nameof(SpoolStream));
                ThrowIfFailed();

                while (data.Length > 0)
                {
                    int n;
                    if (_position < _frozen)
                    {
                        n = (int)Math.Min(data.Length, _frozen - _position);
                        WriteLate(_position, data[..n]);
                    }
                    else
                    {
                        long index = _position - _frozen;
                        // Room for it, and never a jump far past the end.
                        if (index > _tailLength + (long)_piece) throw new NotSupportedException("a write far past the end of the file");
                        n = (int)Math.Min(data.Length, 4L * 1024 * 1024);
                        int need = (int)index + n;
                        if (need > _tail.Length) Array.Resize(ref _tail, Math.Max(need, Math.Min(_tail.Length * 2, 3 * _piece)));
                        if (index > _tailLength) Array.Clear(_tail, _tailLength, (int)index - _tailLength);
                        data[..n].CopyTo(_tail.AsSpan((int)index));
                        _tailLength = Math.Max(_tailLength, need);
                        KeepHead(_position, data[..n]);
                    }

                    _position += n;
                    _length = Math.Max(_length, _position);
                    data = data[n..];

                    while (_tailLength >= 2 * _piece) Ship();
                }
            }
        }

        private void KeepHead(long at, ReadOnlySpan<byte> data)
        {
            if (at >= HeadBytes) return;
            int n = (int)Math.Min(data.Length, HeadBytes - at);
            data[..n].CopyTo(_head.AsSpan((int)at));
        }

        private void WriteLate(long at, ReadOnlySpan<byte> data)
        {
            // The same bytes again (a header whose numbers were right all along) change nothing.
            if (at + data.Length <= HeadBytes && data.SequenceEqual(_head.AsSpan((int)at, data.Length))) return;

            _lateBytes += data.Length;
            if (_lateBytes > LateMost)
                throw new NotSupportedException("too much of the file was rewritten after it had been sent");
            _late.Add((at, data.ToArray()));
            KeepHead(at, data);
        }

        /// <summary>Hands the oldest piece to the uploader, waiting while it has enough to do.</summary>
        private void Ship()
        {
            int n = Math.Min(_piece, _tailLength);
            // Rented and handed back once sent: a big file is hundreds of these.
            var copy = System.Buffers.ArrayPool<byte>.Shared.Rent(n);
            Buffer.BlockCopy(_tail, 0, copy, 0, n);
            Buffer.BlockCopy(_tail, n, _tail, 0, _tailLength - n);
            _tailLength -= n;
            long at = _frozen;
            _frozen += n;

            while (true)
            {
                ThrowIfFailed();
                if (_queue.TryAdd((at, copy, n), 250)) return;
            }
        }

        private void Upload()
        {
            try
            {
                foreach (var (offset, buffer, count) in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (_failed != null || _stopped) continue;  // drained, so the writer is never left waiting
                        _md5.AppendData(buffer, 0, count);
                        _send(offset, buffer, count, null);
                        Interlocked.Add(ref _sent, count);
                    }
                    catch (Exception ex) { _failed = ex; }
                    finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
                }
            }
            catch (Exception ex) { _failed ??= ex; }
        }

        /// <summary>
        /// The writer has finished: sends what is left, the last piece with the
        /// total, and returns once Google holds the whole file as it was first
        /// written. Throws what stopped the upload, if anything did.
        /// </summary>
        public void Complete()
        {
            lock (_gate)
            {
                if (_completed) return;
                ThrowIfFailed();
                while (_tailLength > _piece) Ship();
                _completed = true;
            }

            _queue.CompleteAdding();
            _uploader.Join();
            ThrowIfFailed();
            if (_length <= 0 || _tailLength <= 0) throw new IOException("nothing was written for it");

            _md5.AppendData(_tail, 0, _tailLength);
            _md5Hex = Convert.ToHexString(_md5.GetHashAndReset());
            _send(_frozen, _tail, _tailLength, _length);
            Interlocked.Add(ref _sent, _tailLength);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                if (_position >= _length || count <= 0) return 0;
                int n;
                if (_position >= _frozen)
                {
                    n = (int)Math.Min(count, _frozen + _tailLength - _position);
                    if (n <= 0) return 0;
                    Buffer.BlockCopy(_tail, (int)(_position - _frozen), buffer, offset, n);
                }
                else if (_position < HeadBytes)
                {
                    n = (int)Math.Min(count, Math.Min(HeadBytes, _frozen) - _position);
                    Buffer.BlockCopy(_head, (int)_position, buffer, offset, n);
                }
                else throw new NotSupportedException("that part of the file has already gone to Google Drive");
                _position += n;
                return n;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stopped = true;
                try { _queue.CompleteAdding(); } catch { }
                // The uploader ends by itself; a piece in the air is cut by the caller's token.
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A managed stream as the COM IStream Media Foundation writes a file
    /// through (its sink writer goes back to finish MP4 and FLAC headers, so
    /// Seek and Write are both real). Media Foundation calls from its own
    /// threads; the stream underneath does its own locking.
    /// </summary>
    internal sealed class WritableIStream : IStream
    {
        private readonly Stream _inner;
        private readonly object _gate = new();

        public WritableIStream(Stream inner) { _inner = inner; }

        /// <summary>What a write threw, if one did: Media Foundation only reports a number.</summary>
        public Exception? Failure { get; private set; }

        public void Read(byte[] buffer, int count, IntPtr bytesReadOut)
        {
            int read = 0;
            lock (_gate)
            {
                while (read < count)
                {
                    int n = _inner.Read(buffer, read, count - read);
                    if (n <= 0) break;
                    read += n;
                }
            }
            if (bytesReadOut != IntPtr.Zero) Marshal.WriteInt32(bytesReadOut, read);
        }

        public void Write(byte[] buffer, int count, IntPtr writtenOut)
        {
            lock (_gate)
            {
                try { _inner.Write(buffer, 0, count); }
                catch (Exception ex) { Failure ??= ex; throw; }
            }
            if (writtenOut != IntPtr.Zero) Marshal.WriteInt32(writtenOut, count);
        }

        public void Seek(long move, int origin, IntPtr newPositionOut)
        {
            long now;
            lock (_gate) now = _inner.Seek(move, (SeekOrigin)origin);
            if (newPositionOut != IntPtr.Zero) Marshal.WriteInt64(newPositionOut, now);
        }

        public void Stat(out System.Runtime.InteropServices.ComTypes.STATSTG stat, int flags)
        {
            lock (_gate)
                stat = new System.Runtime.InteropServices.ComTypes.STATSTG { type = 2, cbSize = _inner.Length, grfMode = 2 };
        }

        public void SetSize(long newSize) { }
        public void Commit(int flags) { }
        public void Clone(out IStream copy) => throw new NotSupportedException();
        public void CopyTo(IStream target, long count, IntPtr read, IntPtr written) => throw new NotSupportedException();
        public void LockRegion(long offset, long count, int lockType) => throw new NotSupportedException();
        public void Revert() => throw new NotSupportedException();
        public void UnlockRegion(long offset, long count, int lockType) => throw new NotSupportedException();
    }
}
