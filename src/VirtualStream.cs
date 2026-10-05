using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ExplorerNative
{
    /// <summary>
    /// A file as it would be if it started somewhere else: a few bytes of
    /// header, then the file from a chosen frame to its end. What
    /// <see cref="ExactSeek"/> opens the decoder on, so that decoding begins
    /// at a frame whose place in the file is known exactly.
    ///
    /// Nothing is copied but the header. The rest is read from the file as it
    /// is asked for, by position — so this never moves the read position of
    /// the <see cref="StreamingSource"/> underneath, which is still the decoder
    /// that was playing a moment ago as far as it is concerned.
    ///
    /// Media Foundation calls this from several threads, so every method holds
    /// the lock, as in <see cref="LocalByteStream"/>.
    /// </summary>
    internal sealed class VirtualStream : IStream
    {
        private readonly object _gate = new();
        private readonly byte[] _header;
        private readonly ByteSource _source;
        private readonly long _from;
        private long _position;

        public VirtualStream(byte[] header, ByteSource source, long from)
        {
            _header = header;
            _source = source;
            _from = from;
            Length = header.Length + Math.Max(0, source.Length - from);
        }

        public long Length { get; }

        public void Read(byte[] buffer, int count, IntPtr bytesReadOut)
        {
            int read = 0;
            try
            {
                long position;
                lock (_gate) position = _position;

                count = (int)Math.Max(0, Math.Min(count, Length - position));
                while (read < count)
                {
                    long at = position + read;
                    int got;

                    if (at < _header.Length)
                    {
                        got = (int)Math.Min(count - read, _header.Length - at);
                        Array.Copy(_header, at, buffer, read, got);
                    }
                    else
                    {
                        got = _source.ReadAny(_from + at - _header.Length, buffer.AsSpan(read, count - read));
                        if (got <= 0) break;
                    }

                    read += got;
                }

                lock (_gate)
                {
                    if (_position == position) _position = position + read;
                }
            }
            catch { read = 0; }

            if (bytesReadOut != IntPtr.Zero) Marshal.WriteInt32(bytesReadOut, read);
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
                if (newPositionOut != IntPtr.Zero) Marshal.WriteInt64(newPositionOut, _position);
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

        // Read only, as LocalByteStream is and for the same reason.
        public void Clone(out IStream copy) => throw new NotSupportedException();
        public void Commit(int flags) => throw new NotSupportedException();
        public void CopyTo(IStream target, long count, IntPtr read, IntPtr written) => throw new NotSupportedException();
        public void LockRegion(long offset, long count, int lockType) => throw new NotSupportedException();
        public void Revert() => throw new NotSupportedException();
        public void SetSize(long newSize) => throw new NotSupportedException();
        public void UnlockRegion(long offset, long count, int lockType) => throw new NotSupportedException();
        public void Write(byte[] buffer, int count, IntPtr written) => throw new NotSupportedException();
    }
}
