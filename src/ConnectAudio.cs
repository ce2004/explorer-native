using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// An <see cref="IRangeSource"/> as the COM stream a decoder reads through, with a position of its own.
    ///
    /// What lets <c>/api/audio</c> decode a Drive track out of the same shared download <c>/api/file</c> uses
    /// (<see cref="ConnectStreams"/>) rather than through the letter: each request's decoder gets one of these
    /// over its own lease, so two decoders never fight over one stream's read pointer.
    /// </summary>
    internal sealed class RangeStream : IStream, IDisposable
    {
        private readonly IRangeSource _source;
        private readonly CancellationTokenSource _closing = new();
        private long _position;

        public RangeStream(IRangeSource source) => _source = source;

        public void Read(byte[] buffer, int count, IntPtr bytesReadOut)
        {
            int total = 0;
            try
            {
                while (total < count)
                {
                    long at = Interlocked.Read(ref _position) + total;
                    if (at >= _source.Length) break;
                    int got = _source.ReadAsync(at, buffer.AsMemory(total, count - total), _closing.Token).GetAwaiter().GetResult();
                    if (got <= 0) break;
                    total += got;
                }
            }
            catch { }
            Interlocked.Add(ref _position, total);
            if (bytesReadOut != IntPtr.Zero) Marshal.WriteInt32(bytesReadOut, total);
        }

        public void Seek(long move, int origin, IntPtr newPositionOut)
        {
            long from = origin switch { 1 => Interlocked.Read(ref _position), 2 => _source.Length, _ => 0 };
            long to = Math.Clamp(from + move, 0, _source.Length);
            Interlocked.Exchange(ref _position, to);
            if (newPositionOut != IntPtr.Zero) Marshal.WriteInt64(newPositionOut, to);
        }

        public void Stat(out STATSTG stat, int flags) =>
            stat = new STATSTG { type = 2, cbSize = _source.Length, grfMode = 0 };

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
            try { _closing.Cancel(); } catch { }
            try { _source.Dispose(); } catch { }
        }
    }

    /// <summary>What <c>/api/audio</c> serves for one file: 24-bit PCM WAV at the source's own rate.</summary>
    public sealed record AudioPlan(int Rate, int Channels, long Frames)
    {
        public const int HeaderBytes = 44;
        public int BlockAlign => Channels * 3;
        public long DataBytes => Frames * BlockAlign;
        public long TotalBytes => HeaderBytes + DataBytes;
    }

    /// <summary>
    /// Any file Explorer Native plays, as a WAV the phone can play and seek: the application's own decoder
    /// chain (<see cref="TrackDecoder"/>: Media Foundation, then Ogg Opus and Vorbis, AIFF, CD tracks), only the
    /// audio of a video, 24-bit at the file's own rate, stereo at most.
    ///
    /// The header states the whole length up front, from the decoder's duration, so AVPlayer sees an ordinary
    /// seekable file. A byte range maps to a sample frame and the decoder is seeked there — its seeks are the
    /// player's sample-exact ones (<see cref="ExactSeek"/>) — so a scrub lands on the same samples a straight
    /// decode would have. A decoder that ends early is padded with silence; one that runs long is cut.
    /// </summary>
    internal sealed class ConnectAudio : IDisposable
    {
        private readonly ITrackDecoder _decoder;
        private readonly RangeStream? _stream;
        private readonly int _decoderChannels;

        public AudioPlan Plan { get; }

        /// <summary>Frames decoded per step: about a tenth of a second at 44.1kHz.</summary>
        private const int Step = 4096;

        /// <summary>A RIFF size is 32 bits; a longer decode is cut to fit.</summary>
        private const long MostDataBytes = uint.MaxValue - 36L;

        private ConnectAudio(ITrackDecoder decoder, RangeStream? stream, AudioPlan plan)
        {
            _decoder = decoder;
            _stream = stream;
            _decoderChannels = decoder.Channels;
            Plan = plan;
        }

        /// <summary>
        /// Opens the file at its own rate and channel count (asking for 0 of each is how both decoders say
        /// "as it is"), then for stereo when it has more. Null, with the decoders' reason, when neither opens it.
        /// <paramref name="remote"/> is read instead of the path when given, and is owned from here on.
        /// </summary>
        public static ConnectAudio? Open(string path, IRangeSource? remote, long fileBytes, out string why)
        {
            var stream = remote != null ? new RangeStream(remote) : null;
            ITrackDecoder? decoder = null;
            try
            {
                decoder = TrackDecoder.Open(path, stream, 0, 0, out why);
                if (decoder == null || decoder.SampleRate <= 0 || decoder.Channels <= 0)
                {
                    decoder?.Dispose();
                    stream?.Dispose();
                    if (decoder != null) why = "the decoder did not say its format";
                    return null;
                }

                // More than two channels: the decoder's own downmix when it has one, ours otherwise.
                if (decoder.Channels > 2) decoder.Reopen(decoder.SampleRate, 2);
                int channels = Math.Min(2, decoder.Channels);
                int rate = decoder.SampleRate;

                long frames = decoder.Duration > 0
                    ? (long)Math.Round(decoder.Duration * rate)
                    : EstimateFrames(fileBytes, rate);
                frames = Math.Clamp(frames, 0, MostDataBytes / (channels * 3));
                return new ConnectAudio(decoder, stream, new AudioPlan(rate, channels, frames));
            }
            catch (Exception e)
            {
                why = e.Message;
                decoder?.Dispose();
                stream?.Dispose();
                return null;
            }
        }

        /// <summary>
        /// A length for a file whose decoder will not say, generous on purpose: as though it were 64 kilobits a
        /// second, which is below almost anything, and never under a minute. The end is silence, not a cut.
        /// </summary>
        internal static long EstimateFrames(long fileBytes, int rate)
        {
            double seconds = Math.Max(60, fileBytes / 8000.0);
            return (long)Math.Ceiling(seconds * rate);
        }

        /// <summary>The canonical 44-byte header for 24-bit PCM.</summary>
        public static byte[] Header(AudioPlan plan)
        {
            var h = new byte[AudioPlan.HeaderBytes];
            var s = h.AsSpan();
            "RIFF"u8.CopyTo(s);
            BitConverter.TryWriteBytes(s[4..], (uint)(36 + plan.DataBytes));
            "WAVE"u8.CopyTo(s[8..]);
            "fmt "u8.CopyTo(s[12..]);
            BitConverter.TryWriteBytes(s[16..], 16u);
            BitConverter.TryWriteBytes(s[20..], (ushort)1);                  // PCM
            BitConverter.TryWriteBytes(s[22..], (ushort)plan.Channels);
            BitConverter.TryWriteBytes(s[24..], (uint)plan.Rate);
            BitConverter.TryWriteBytes(s[28..], (uint)(plan.Rate * plan.BlockAlign));
            BitConverter.TryWriteBytes(s[32..], (ushort)plan.BlockAlign);
            BitConverter.TryWriteBytes(s[34..], (ushort)24);
            "data"u8.CopyTo(s[36..]);
            BitConverter.TryWriteBytes(s[40..], (uint)plan.DataBytes);
            return h;
        }

        /// <summary>
        /// Writes bytes <paramref name="from"/> to <paramref name="to"/> inclusive of the WAV to
        /// <paramref name="output"/>, decoding as it goes. Blocking: run it on a worker.
        /// </summary>
        public void WriteRange(long from, long to, Stream output, CancellationToken token)
        {
            to = Math.Min(to, Plan.TotalBytes - 1);
            long at = from;
            if (at < AudioPlan.HeaderBytes)
            {
                var header = Header(Plan);
                int end = (int)Math.Min(AudioPlan.HeaderBytes, to + 1);
                output.Write(header, (int)at, end - (int)at);
                at = end;
            }
            if (at > to) return;

            int align = Plan.BlockAlign;
            long frame = (at - AudioPlan.HeaderBytes) / align;
            int skip = (int)((at - AudioPlan.HeaderBytes) % align);

            // A quarter of a frame in, so that turning seconds back into a frame count, whichever way the
            // decoder rounds, lands on this frame and not the one before.
            if (frame > 0 && !_decoder.SeekTo((frame + 0.25) / Plan.Rate))
                throw new IOException("the decoder would not seek there");

            var decoded = new float[Step * Math.Max(1, _decoderChannels)];
            var bytes = new byte[Step * align];
            bool ended = false;

            while (at <= to)
            {
                token.ThrowIfCancellationRequested();
                long lastFrame = (to - AudioPlan.HeaderBytes) / align;
                int want = (int)Math.Min(Step, Math.Min(lastFrame - frame + 1, Plan.Frames - frame));
                if (want <= 0) break;

                int got = ended ? 0 : _decoder.Read(decoded, want);
                if (got < want) ended = true;
                got = Math.Max(0, got);
                Pack(decoded, got, bytes);
                // What the decoder did not have is silence, to the length the header promised.
                Array.Clear(bytes, got * align, (want - got) * align);

                int start = skip;
                int length = (int)Math.Min(want * (long)align - start, to - at + 1);
                output.Write(bytes, start, length);
                at += length;
                frame += want;
                skip = 0;
            }
            output.Flush();
        }

        /// <summary>Float frames to 24-bit little-endian, downmixed to stereo when the decoder gave more.</summary>
        private void Pack(float[] decoded, int frames, byte[] into)
        {
            int inCh = Math.Max(1, _decoderChannels), outCh = Plan.Channels;
            int o = 0;
            for (int f = 0; f < frames; f++)
            {
                int b = f * inCh;
                for (int c = 0; c < outCh; c++)
                {
                    float v;
                    if (inCh == outCh) v = decoded[b + c];
                    else
                    {
                        // Every channel of this side's parity, averaged: left takes 0, 2, 4…, right 1, 3, 5….
                        float sum = 0;
                        int n = 0;
                        for (int k = c; k < inCh; k += 2) { sum += decoded[b + k]; n++; }
                        v = n > 0 ? sum / n : 0;
                    }
                    int s = (int)Math.Round(Math.Clamp(v, -1f, 1f) * 8388607.0);
                    into[o++] = (byte)s;
                    into[o++] = (byte)(s >> 8);
                    into[o++] = (byte)(s >> 16);
                }
            }
        }

        public void Dispose()
        {
            try { _decoder.Dispose(); } catch { }
            _stream?.Dispose();
        }
    }
}
