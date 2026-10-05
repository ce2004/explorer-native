using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ExplorerNative
{
    /// <summary>
    /// A file on disk, as the COM <c>IStream</c> Media Foundation wants.
    ///
    /// The mirror image of <see cref="ComStream"/>, and it exists for one
    /// reason: <c>MFCreateSourceReaderFromURL</c> chooses a decoder by looking
    /// the *extension* up in the registry, so a file whose format Windows
    /// decodes perfectly well is refused outright when its name does not match
    /// anything registered. Opening the same bytes through a byte stream gives
    /// the resolver nothing to go on but the content, which is what it should
    /// have been going on all along for this case.
    ///
    /// Read only, and deliberately so — nothing in this application writes
    /// audio, and an IStream that cannot write is one that cannot corrupt
    /// somebody's file through a mistake somewhere else.
    ///
    /// <para>
    /// Media Foundation calls this from several of its own threads at once, so
    /// every method holds the lock. The position lives here rather than on the
    /// FileStream because Seek and Read arrive interleaved from those threads
    /// and a shared file position would have them reading each other's bytes.
    /// </para>
    /// </summary>
    internal sealed class LocalByteStream : IStream, IDisposable
    {
        private readonly object _gate = new();
        private FileStream? _file;
        private long _position;

        public long Length { get; }

        private LocalByteStream(FileStream file)
        {
            _file = file;
            Length = file.Length;
        }

        /// <summary>
        /// Opens the file, or answers null. Null means the caller falls back to
        /// the ordinary path, which will fail in its own way and report why —
        /// this is a better route to the same file, not the only one.
        /// </summary>
        public static LocalByteStream? TryOpen(string path)
        {
            try
            {
                // Shared as widely as the rest of the player shares things: the
                // file must stay movable and deletable while it is being read.
                var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                    FileOptions.RandomAccess);

                return new LocalByteStream(file);
            }
            catch
            {
                return null;
            }
        }

        public void Read(byte[] buffer, int count, IntPtr bytesReadOut)
        {
            int read = 0;
            try
            {
                lock (_gate)
                {
                    if (_file != null && count > 0 && _position < Length)
                    {
                        _file.Position = _position;
                        read = _file.Read(buffer, 0, (int)Math.Min(count, Length - _position));
                        _position += read;
                    }
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

        public void Dispose()
        {
            lock (_gate)
            {
                try { _file?.Dispose(); } catch { }
                _file = null;
            }
        }

        // Nothing here writes, clones or locks. Media Foundation asks for none
        // of it on a read-only source, and refusing is better than pretending.
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
