using System;
using System.IO;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;

namespace ExplorerNative
{
    /// <summary>
    /// Seeking to the sample in formats where Media Foundation's own seek does
    /// not say truthfully where it landed.
    ///
    /// **Most formats need nothing from this.** After
    /// <c>SetCurrentPosition</c> Media Foundation starts somewhere at or before
    /// the target and stamps the first sample with where that is, so
    /// <see cref="AudioDecoder"/> decodes up to the target and throws the rest
    /// away. Measured on hour-long files: WAV, AAC, WMA and constant-bitrate
    /// MP3 all stamp their landing correctly.
    ///
    /// Two do not, and they are two of the commonest formats there are:
    ///
    ///   * **Variable-bitrate MP3.** The Xing table of contents has a hundred
    ///     entries for the whole file, so a seek in an hour lands anywhere in a
    ///     36-second slot — measured 5.6 seconds early — and is stamped as the
    ///     target.
    ///   * **FLAC.** With a seek table (every FLAC encoder writes one) it lands
    ///     up to ten seconds early and says so — except before the first seek
    ///     point, where it starts from the beginning and stamps it as the target.
    ///     Without one it lands wherever, up to 35 seconds out, and stamps it as
    ///     the target every time.
    ///
    /// So for those two the frame is found here, from the file's own
    /// structure, and the decoder is reopened on a <see cref="VirtualStream"/>
    /// that starts at that frame. The time that frame starts at is then known
    /// exactly, whatever Media Foundation thinks.
    ///
    /// **Only from bytes already in hand.** A search is a few dozen small reads,
    /// and on the Drive letter each one that is not in memory is most of a
    /// second. Where the bytes are not there yet this answers "not now" and the
    /// decoder uses Media Foundation's seek, as it always did.
    /// </summary>
    internal abstract class ExactSeek : IDisposable
    {
        protected readonly ByteSource Bytes;

        /// <summary>Set once the seeker is closed, for work it started in the background.</summary>
        protected volatile bool Closed;

        protected ExactSeek(ByteSource bytes) => Bytes = bytes;

        /// <summary>
        /// The seeker for this file, or null when its format does not need one
        /// or its bytes cannot be read here. <paramref name="retry"/> is true
        /// when the answer might be different later — the head of a download
        /// has not arrived yet.
        /// </summary>
        public static ExactSeek? For(string path, IStream? stream, out bool retry)
        {
            retry = false;

            ByteSource? bytes = stream switch
            {
                null => FileBytes.TryOpen(path),
                StreamingSource download => new HeldBytes(download),
                _ => null,
            };
            if (bytes == null) return null;

            var head = new byte[10];
            if (!bytes.Holds(0, head.Length) || bytes.ReadHeld(0, head) < head.Length)
            {
                retry = bytes.Length >= head.Length;
                bytes.Dispose();
                return null;
            }

            ExactSeek? seeker = null;
            try
            {
                // Past any ID3v2 tags: some taggers put one in front of a FLAC,
                // and the file is still a FLAC.
                long start = 0;
                var probe = head;
                while (probe[0] == 'I' && probe[1] == 'D' && probe[2] == '3')
                {
                    long size = ((probe[6] & 0x7F) << 21) | ((probe[7] & 0x7F) << 14) |
                                ((probe[8] & 0x7F) << 7) | (probe[9] & 0x7F);
                    start += 10 + size + ((probe[5] & 0x10) != 0 ? 10 : 0);

                    probe = new byte[10];
                    if (!bytes.Holds(start, start + 10) || bytes.ReadHeld(start, probe) < 10)
                    {
                        retry = start + 10 <= bytes.Length;
                        probe = Array.Empty<byte>();
                        break;
                    }
                }

                if (probe.Length >= 4 && probe[0] == 'f' && probe[1] == 'L' && probe[2] == 'a' && probe[3] == 'C')
                    seeker = FlacSeek.TryCreate(bytes, start, out retry);
                else if ((head[0] == 'I' && head[1] == 'D' && head[2] == '3') ||
                         (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0))
                    seeker = Mp3Seek.TryCreate(bytes, out retry);
            }
            catch { seeker = null; }

            if (seeker == null) bytes.Dispose();
            return seeker;
        }

        /// <summary>
        /// A stream Media Foundation can open that starts decoding at or before
        /// <paramref name="seconds"/>, and the time its first sample stands for.
        /// False when that cannot be answered from what is in hand.
        ///
        /// <paramref name="outputRate"/> is the rate the decoder is converting
        /// to. The stream is made to start on a sample that falls on that rate's
        /// grid as well as the file's: Windows' resampler lays its output grid
        /// from the first sample it is given, so a start anywhere else plays the
        /// right audio a fraction of a sample late — measured as a difference of
        /// 0.1 against a straight decode, where starting on the grid gives none.
        /// </summary>
        public abstract bool TryLocate(double seconds, int outputRate, out IStream stream, out double start);

        /// <summary>
        /// The length of the audio as the file's own structure gives it, when
        /// that is known — which for MP3 is better than Media Foundation's
        /// estimate from the bitrate: measured at 78ms short on a minute, and
        /// eight seconds short on a file joined from three.
        /// </summary>
        public virtual double? Length => null;

        /// <summary>
        /// Every how many file samples the file's grid and the output's meet:
        /// 147 from 44.1kHz to 48kHz, 1 when the rates are the same.
        /// </summary>
        public static long GridStep(int fileRate, int outputRate)
        {
            if (fileRate <= 0 || outputRate <= 0) return 1;
            return fileRate / Gcd(fileRate, outputRate);
        }

        public static long Gcd(long a, long b)
        {
            while (b != 0) (a, b) = (b, a % b);
            return Math.Abs(a);
        }

        public void Dispose()
        {
            Closed = true;
            Bytes.Dispose();
        }
    }

    /// <summary>A file's bytes, read by position.</summary>
    internal abstract class ByteSource : IDisposable
    {
        public abstract long Length { get; }

        /// <summary>Whether this range can be read without going to the network.</summary>
        public abstract bool Holds(long from, long to);

        /// <summary>Where the part that can be read without the network ends. It always starts at zero.</summary>
        public abstract long HeldEnd { get; }

        /// <summary>Reads only what <see cref="Holds"/> would allow. For searching.</summary>
        public abstract int ReadHeld(long position, Span<byte> into);

        /// <summary>Reads whatever it takes. For the decoder to play from.</summary>
        public abstract int ReadAny(long position, Span<byte> into);

        public virtual void Dispose() { }
    }

    /// <summary>A file on this machine, opened for its own reads.</summary>
    internal sealed class FileBytes : ByteSource
    {
        private readonly SafeFileHandle _handle;

        private FileBytes(SafeFileHandle handle, long length)
        {
            _handle = handle;
            Length = length;
        }

        public static FileBytes? TryOpen(string path)
        {
            try
            {
                var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
                return new FileBytes(handle, RandomAccess.GetLength(handle));
            }
            catch
            {
                return null;
            }
        }

        public override long Length { get; }

        public override bool Holds(long from, long to) => true;

        public override long HeldEnd => Length;

        public override int ReadHeld(long position, Span<byte> into) => ReadAny(position, into);

        public override int ReadAny(long position, Span<byte> into)
        {
            try
            {
                if (position < 0 || position >= Length) return 0;
                return RandomAccess.Read(_handle, into, position);
            }
            catch { return 0; }
        }

        public override void Dispose()
        {
            try { _handle.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// A track being downloaded by <see cref="StreamingSource"/>. Searches are
    /// answered from memory only; playback reads the way the player's own
    /// reads do. The download belongs to the player and is not closed here.
    /// </summary>
    internal sealed class HeldBytes : ByteSource
    {
        private readonly StreamingSource _download;

        public HeldBytes(StreamingSource download) => _download = download;

        public override long Length => _download.Length;

        public override bool Holds(long from, long to) => _download.Holds(from, Math.Min(to, Length));

        public override long HeldEnd => Math.Min(_download.Available, Length);

        public override int ReadHeld(long position, Span<byte> into) => _download.ReadHeld(position, into);

        public override int ReadAny(long position, Span<byte> into) => _download.ReadAt(position, into);
    }
}
