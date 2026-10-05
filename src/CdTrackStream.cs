using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;

namespace ExplorerNative
{
    /// <summary>
    /// A track on an audio CD, presented to Media Foundation as the WAV file it
    /// amounts to.
    ///
    /// **A `.cda` file is not audio.** Windows' CD file system shows each track
    /// as `Track01.cda` and so on, and every one of them is the same 44 bytes: a
    /// RIFF header saying which track this is, the sector it starts on and how
    /// many sectors it runs for. Media Foundation has no handler for it, and
    /// reading the file gives you those 44 bytes and nothing else.
    ///
    /// The audio is read off the disc itself with `IOCTL_CDROM_RAW_READ`, which
    /// returns 2352-byte sectors of 16-bit little-endian stereo at 44.1kHz —
    /// exactly the body of a WAV file. So this is the same trick as
    /// <see cref="AiffStream"/>: a header of our own making, then the samples,
    /// and everything after that — rate conversion to the device, seeking,
    /// which is exact for WAV, the duration — is Windows' own, unchanged.
    ///
    /// Sectors are read twenty at a time and the last block is kept, because
    /// Media Foundation reads in pieces smaller than a block and a drive asked
    /// for the same sectors twice spins for them twice. A block the drive will
    /// not read — a scratch — is retried a sector at a time and what still will
    /// not read plays as silence, which is what a CD player does with it too.
    /// </summary>
    internal sealed class CdTrackStream : IStream, IDisposable
    {
        public const int SectorBytes = 2352;
        public const int SectorsPerSecond = 75;
        private const int SampleRate = 44100;
        private const int HeaderBytes = 44;

        /// <summary>
        /// 47KB. Under the 64KB a USB drive will take in one transfer, with room
        /// for drives that advertise less; a read larger than the drive allows
        /// fails outright rather than being split.
        /// </summary>
        private const int SectorsPerRead = 20;

        /// <summary>
        /// Seconds of unreadable sectors in a row before this stops treating
        /// them as scratches and treats them as the disc having gone. Without it
        /// an ejected disc plays silence to the end of the track.
        /// </summary>
        private const int GiveUpAfterSectors = 5 * SectorsPerSecond;

        private readonly object _gate = new();
        private readonly ICdSectors _drive;
        private readonly CdaTrack _track;
        private readonly byte[] _header;
        private readonly long _dataLength;
        private long _position;

        private readonly byte[] _block = new byte[SectorsPerRead * SectorBytes];
        private long _blockSector = -1;
        private int _blockSectors;
        private int _failedInARow;

        internal CdTrackStream(ICdSectors drive, CdaTrack track)
        {
            _drive = drive;
            _track = track;
            _dataLength = track.SectorCount * SectorBytes;
            _header = WavHeader(_dataLength);
            Length = HeaderBytes + _dataLength;
        }

        public long Length { get; }

        /// <summary>The track's length, which the stub says exactly.</summary>
        public double Seconds => _track.SectorCount / (double)SectorsPerSecond;

        public static bool IsCdaName(string? extension) =>
            string.Equals(extension, ".cda", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The track as WAV, or null when this is not a CD track or the drive
        /// will not open.
        /// </summary>
        public static CdTrackStream? TryOpen(string path)
        {
            if (!IsCdaName(Path.GetExtension(path))) return null;

            CdaTrack? track;
            try { track = ParseCda(File.ReadAllBytes(path)); }
            catch { return null; }
            if (track == null) return null;

            var drive = CdDrive.TryOpen(path);
            return drive == null ? null : new CdTrackStream(drive, track.Value);
        }

        /// <summary>
        /// What a <c>.cda</c> stub says: RIFF, CDDA, a <c>fmt </c> chunk of 24
        /// bytes holding the track number at 22, the first sector at 28 and the
        /// length in sectors at 32. The sector is a logical block address, the
        /// same numbering the raw read takes — MSF copies of both follow, with
        /// the two-second lead-in added, and are not needed.
        /// </summary>
        public static CdaTrack? ParseCda(ReadOnlySpan<byte> data)
        {
            if (data.Length < HeaderBytes) return null;
            if (!Is(data, 0, "RIFF") || !Is(data, 8, "CDDA") || !Is(data, 12, "fmt ")) return null;

            int number = BitConverter.ToUInt16(data.Slice(22, 2));
            long first = BitConverter.ToUInt32(data.Slice(28, 4));
            long count = BitConverter.ToUInt32(data.Slice(32, 4));
            if (count <= 0) return null;

            return new CdaTrack(number, first, count);
        }

        // ---------- the disc as a whole ----------

        /// <summary>
        /// The first track of the first audio CD in any drive, or null. This
        /// spins drives up and can take seconds, so never on the UI thread.
        /// </summary>
        public static string? FirstTrackOnAnyDisc()
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { return null; }

            foreach (var drive in drives)
            {
                try
                {
                    if (drive.DriveType != DriveType.CDRom || !drive.IsReady) continue;
                    var first = TracksIn(drive.RootDirectory.FullName).FirstOrDefault();
                    if (first != null) return first;
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// The track after this one on the same disc, or null at the last.
        /// A CD is an album meant to be heard through, which is why a disc
        /// carries on where a folder of files does not.
        /// </summary>
        public static string? NextTrack(string path)
        {
            try
            {
                var folder = Path.GetDirectoryName(path);
                if (folder == null) return null;

                return TracksIn(folder)
                    .FirstOrDefault(t => string.Compare(t, path, StringComparison.OrdinalIgnoreCase) > 0);
            }
            catch { return null; }
        }

        /// <summary>
        /// The tracks, in order. The names are <c>Track01.cda</c> to
        /// <c>Track99.cda</c>, zero-padded, so ordering them by name is ordering
        /// them by number.
        /// </summary>
        private static IEnumerable<string> TracksIn(string folder) =>
            Directory.EnumerateFiles(folder, "*.cda")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        // ---------- IStream ----------

        public void Read(byte[] buffer, int count, IntPtr bytesReadOut)
        {
            int read = 0;
            try
            {
                lock (_gate)
                {
                    long position = _position;
                    count = (int)Math.Max(0, Math.Min(Math.Min(count, buffer.Length), Length - position));

                    if (position < HeaderBytes && count > 0)
                    {
                        read = (int)Math.Min(count, HeaderBytes - position);
                        Array.Copy(_header, position, buffer, 0, read);
                    }

                    while (read < count)
                    {
                        long offset = position + read - HeaderBytes;
                        long sector = offset / SectorBytes;
                        if (!Load(sector)) break;

                        int within = (int)(offset - _blockSector * SectorBytes);
                        int available = _blockSectors * SectorBytes - within;
                        int n = Math.Min(available, count - read);
                        Array.Copy(_block, within, buffer, read, n);
                        read += n;
                    }

                    _position = position + read;
                }
            }
            catch { read = 0; }

            if (bytesReadOut != IntPtr.Zero) Marshal.WriteInt32(bytesReadOut, read);
        }

        /// <summary>
        /// Makes sure the block holds <paramref name="sector"/> (counted from the
        /// start of the track). False only when the disc looks to have gone.
        /// </summary>
        private bool Load(long sector)
        {
            if (_blockSector >= 0 && sector >= _blockSector && sector < _blockSector + _blockSectors)
                return true;

            int count = (int)Math.Min(SectorsPerRead, _track.SectorCount - sector);
            if (count <= 0) return false;

            long first = _track.FirstSector + sector;
            if (_drive.Read(first, count, _block))
            {
                _failedInARow = 0;
            }
            else
            {
                // One at a time, so a scratch costs the sectors it covers rather
                // than the whole block around it.
                var one = new byte[SectorBytes];
                for (int i = 0; i < count; i++)
                {
                    if (_drive.Read(first + i, 1, one))
                    {
                        Array.Copy(one, 0, _block, i * SectorBytes, SectorBytes);
                        _failedInARow = 0;
                    }
                    else
                    {
                        Array.Clear(_block, i * SectorBytes, SectorBytes);
                        if (++_failedInARow >= GiveUpAfterSectors) return false;
                    }
                }
            }

            _blockSector = sector;
            _blockSectors = count;
            return true;
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

        public void Dispose() => _drive.Dispose();

        // ---------- bytes ----------

        private static byte[] WavHeader(long dataLength)
        {
            var h = new byte[HeaderBytes];
            Put(h, 0, "RIFF");
            PutLe32(h, 4, (uint)(36 + dataLength));
            Put(h, 8, "WAVE");
            Put(h, 12, "fmt ");
            PutLe32(h, 16, 16);
            PutLe16(h, 20, 1);                      // PCM
            PutLe16(h, 22, 2);                      // stereo
            PutLe32(h, 24, SampleRate);
            PutLe32(h, 28, SampleRate * 4);         // bytes per second
            PutLe16(h, 32, 4);                      // bytes per frame
            PutLe16(h, 34, 16);                     // bits
            Put(h, 36, "data");
            PutLe32(h, 40, (uint)dataLength);
            return h;
        }

        private static bool Is(ReadOnlySpan<byte> data, int at, string ascii)
        {
            for (int i = 0; i < 4; i++)
                if (data[at + i] != (byte)ascii[i]) return false;
            return true;
        }

        private static void Put(byte[] data, int at, string ascii)
        {
            for (int i = 0; i < ascii.Length; i++) data[at + i] = (byte)ascii[i];
        }

        private static void PutLe16(byte[] data, int at, int value)
        {
            data[at] = (byte)value;
            data[at + 1] = (byte)(value >> 8);
        }

        private static void PutLe32(byte[] data, int at, uint value)
        {
            for (int i = 0; i < 4; i++) data[at + i] = (byte)(value >> (8 * i));
        }

        // Read only, as AiffStream is and for the same reason.
        public void Clone(out IStream copy) => throw new NotSupportedException();
        public void Commit(int flags) => throw new NotSupportedException();
        public void CopyTo(IStream target, long count, IntPtr read, IntPtr written) => throw new NotSupportedException();
        public void LockRegion(long offset, long count, int lockType) => throw new NotSupportedException();
        public void Revert() => throw new NotSupportedException();
        public void SetSize(long newSize) => throw new NotSupportedException();
        public void UnlockRegion(long offset, long count, int lockType) => throw new NotSupportedException();
        public void Write(byte[] buffer, int count, IntPtr written) => throw new NotSupportedException();
    }

    /// <summary>One track, as its <c>.cda</c> stub describes it.</summary>
    internal readonly record struct CdaTrack(int Number, long FirstSector, long SectorCount);

    /// <summary>Raw audio sectors from somewhere — a drive, or a test.</summary>
    internal interface ICdSectors : IDisposable
    {
        /// <summary>
        /// Fills <paramref name="into"/> with <paramref name="count"/> sectors of
        /// 2352 bytes starting at logical block <paramref name="first"/>.
        /// </summary>
        bool Read(long first, int count, byte[] into);
    }

    /// <summary>
    /// The drive itself, opened as a device. Reading raw audio needs only read
    /// access, which Windows grants an ordinary user on a CD drive.
    /// </summary>
    internal sealed class CdDrive : ICdSectors
    {
        private const uint GenericRead = 0x80000000;
        private const uint ShareReadWrite = 0x1 | 0x2;
        private const uint OpenExisting = 3;

        /// <summary>CTL_CODE(IOCTL_CDROM_BASE, 0x000F, METHOD_OUT_DIRECT, FILE_READ_ACCESS).</summary>
        private const uint IoctlCdromRawRead = 0x0002403E;

        /// <summary>The raw read's offset is counted in cooked 2048-byte sectors.</summary>
        private const long CookedSectorBytes = 2048;
        private const int TrackModeCdda = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RawReadInfo
        {
            public long DiskOffset;
            public uint SectorCount;
            public int TrackMode;
        }

        private readonly SafeFileHandle _handle;

        private CdDrive(SafeFileHandle handle) => _handle = handle;

        /// <summary>The drive <paramref name="path"/> is on, or null.</summary>
        public static CdDrive? TryOpen(string path)
        {
            string? root;
            try { root = Path.GetPathRoot(Path.GetFullPath(path)); }
            catch { return null; }
            if (root == null || root.Length < 2 || root[1] != ':') return null;

            var handle = CreateFileW(@"\\.\" + root.Substring(0, 2), GenericRead, ShareReadWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); return null; }

            return new CdDrive(handle);
        }

        public bool Read(long first, int count, byte[] into)
        {
            if (count <= 0 || into.Length < count * CdTrackStream.SectorBytes) return false;

            var info = new RawReadInfo
            {
                DiskOffset = first * CookedSectorBytes,
                SectorCount = (uint)count,
                TrackMode = TrackModeCdda,
            };

            return DeviceIoControl(_handle, IoctlCdromRawRead, ref info, Marshal.SizeOf<RawReadInfo>(),
                       into, count * CdTrackStream.SectorBytes, out int returned, IntPtr.Zero) != 0 &&
                   returned == count * CdTrackStream.SectorBytes;
        }

        public void Dispose() => _handle.Dispose();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int DeviceIoControl(SafeFileHandle device, uint code,
            ref RawReadInfo input, int inputSize, byte[] output, int outputSize,
            out int returned, IntPtr overlapped);
    }
}
