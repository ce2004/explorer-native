using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace ExplorerNative
{
    /// <summary>
    /// True Audio (.tta) files, decoded here because Windows has no decoder for
    /// them at all: Media Foundation does not know the format, so without this
    /// a .tta went to another application or nowhere.
    ///
    /// TTA1 is simple to follow: a 22-byte header, a table of every frame's
    /// size, then frames of 256 * rate / 245 samples (the last one shorter),
    /// each an adaptive Rice code fed through a per-channel adaptive filter
    /// and a fixed predictor, with the channels stored as differences from
    /// each other. Written from ffmpeg's tta.c and checked sample for sample
    /// against ffmpeg.
    ///
    /// Gives the file's own rate and channels; the player converts for the
    /// device. Password-protected files are refused by name, not decoded into
    /// noise.
    /// </summary>
    internal sealed class TtaDecoder : ITrackDecoder
    {
        private const int HeaderBytes = 22;

        /// <summary>1 &lt;&lt; i, saturating at the top bit, as the reference decoder's shift_1 table.</summary>
        private static readonly uint[] Shift1 = MakeShift1();

        private Stream? _source;
        private readonly object _gate = new();
        private int _bits, _bytesPerSample, _frameLength, _lastFrameLength, _frameCount, _nextFrame;
        private long _totalFrames, _frame;
        private long[] _frameStart = Array.Empty<long>();
        private int[] _frameSize = Array.Empty<int>();
        private byte[] _packet = Array.Empty<byte>();
        private int[] _samples = Array.Empty<int>();
        private float[] _decoded = Array.Empty<float>();
        private int _decodedFrames, _decodedTaken;
        private Channel[] _state = Array.Empty<Channel>();

        // The bit reader: least significant bit first, over one frame.
        private int _bytes;
        private long _at;

        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public double Duration => SampleRate > 0 ? _totalFrames / (double)SampleRate : 0;
        public double Position { get { lock (_gate) return SampleRate > 0 ? _frame / (double)SampleRate : 0; } }
        public string Diagnostic { get; private set; } = "not opened";

        /// <summary>The bits per sample the file stores, so converting keeps the depth.</summary>
        public int NativeBits => _bits;

        /// <summary>One channel's running state, reset at the start of every frame.</summary>
        private sealed class Channel
        {
            public readonly int[] Qm = new int[8], Dx = new int[8], Dl = new int[8];
            public int Error, Predictor;
            public uint K0, K1, Sum0, Sum1;
        }

        /// <summary>Whether a file begins as TTA1, after any ID3v2 tag in front of it.</summary>
        public static bool IsTta(string path, System.Runtime.InteropServices.ComTypes.IStream? stream)
        {
            try
            {
                using var source = stream != null ? (Stream)new ComStream(stream)
                    : new FileStream(NameRules.ExactPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);   // as Open shares it: a file still being recorded
                return FindHeader(source) >= 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Where "TTA1" starts: at 0, or straight after an ID3v2 tag, which
        /// taggers put in front of TTA files as they do MP3s. -1 if neither.
        /// </summary>
        private static long FindHeader(Stream s)
        {
            s.Position = 0;
            var head = new byte[10];
            if (ReadFully(s, head, 10) < 4) return -1;
            long at = 0;
            if (head[0] == 'I' && head[1] == 'D' && head[2] == '3')
            {
                long size = (head[6] & 0x7F) << 21 | (head[7] & 0x7F) << 14 | (head[8] & 0x7F) << 7 | (head[9] & 0x7F);
                at = 10 + size + ((head[5] & 0x10) != 0 ? 10 : 0);
                s.Position = at;
                if (ReadFully(s, head, 4) < 4) return -1;
            }
            return head[0] == 'T' && head[1] == 'T' && head[2] == 'A' && head[3] == '1' ? at : -1;
        }

        public bool Open(string path, System.Runtime.InteropServices.ComTypes.IStream? stream)
        {
            try
            {
                _source = stream != null
                    ? new ComStream(stream)
                    : new FileStream(NameRules.ExactPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
                if (!ReadHeader(out var why)) { Diagnostic = why; return false; }
                Seek(0);
                Diagnostic = "ready";
                return true;
            }
            catch (Exception ex)
            {
                Diagnostic = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private bool ReadHeader(out string why)
        {
            var s = _source!;
            long start = FindHeader(s);
            if (start < 0) { why = "not a TTA file"; return false; }

            var h = new byte[HeaderBytes];
            s.Position = start;
            if (ReadFully(s, h, HeaderBytes) < HeaderBytes) { why = "the TTA file ends inside its header"; return false; }

            int format = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(4));
            int channels = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(6));
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(8));
            uint rate = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(10));
            uint samples = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(14));

            if (format == 2)
            {
                why = "the TTA file is password-protected (encrypted), which this player does not decode";
                return false;
            }
            if (format != 1) { why = $"a TTA format this player does not decode (format {format})"; return false; }
            if (channels < 1 || channels > 32 || rate < 1000 || rate > 768_000)
            {
                why = "the TTA file's format makes no sense";
                return false;
            }
            if (bits < 8 || bits > 24) { why = $"a TTA bit depth this player does not decode ({bits} bits)"; return false; }
            if (samples == 0) { why = "the TTA file has no sound in it"; return false; }

            _bits = bits;
            _bytesPerSample = (bits + 7) / 8;
            _frameLength = (int)(256L * rate / 245);
            _lastFrameLength = (int)(samples % (uint)_frameLength);
            long frameCount = samples / _frameLength + (_lastFrameLength != 0 ? 1 : 0);

            // The seek table is four bytes a frame and then its own CRC. A count
            // the file could not possibly hold is a damaged header, not a
            // reason to allocate gigabytes.
            long tableStart = start + HeaderBytes;
            if (tableStart + frameCount * 4 + 4 > s.Length) { why = "the TTA file ends inside its seek table"; return false; }
            var table = new byte[frameCount * 4];
            s.Position = tableStart;
            if (ReadFully(s, table, table.Length) < table.Length) { why = "the TTA file ends inside its seek table"; return false; }

            _frameCount = (int)frameCount;
            _frameStart = new long[_frameCount];
            _frameSize = new int[_frameCount];
            long at = tableStart + table.Length + 4;
            for (int i = 0; i < _frameCount; i++)
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 4));
                _frameStart[i] = at;
                _frameSize[i] = (int)Math.Min(size, 64u << 20);
                at += size;
            }

            Channels = channels;
            SampleRate = (int)rate;
            _totalFrames = samples;
            _samples = new int[_frameLength * channels];
            _decoded = new float[_frameLength * channels];
            _state = new Channel[channels];
            for (int c = 0; c < channels; c++) _state[c] = new Channel();
            why = "";
            return true;
        }

        public int Read(float[] destination, int frames)
        {
            lock (_gate)
            {
                int done = 0;
                while (done < frames)
                {
                    if (_decodedTaken >= _decodedFrames && !DecodeFrame()) break;
                    int take = Math.Min(frames - done, _decodedFrames - _decodedTaken);
                    Array.Copy(_decoded, _decodedTaken * Channels, destination, done * Channels, take * Channels);
                    _decodedTaken += take;
                    done += take;
                    _frame += take;
                }
                return done;
            }
        }

        public bool SeekTo(double seconds)
        {
            lock (_gate)
            {
                try { Seek((long)Math.Round(Math.Max(0, seconds) * SampleRate)); return true; }
                catch { return false; }
            }
        }

        /// <summary>
        /// To a sample: the seek table says where its frame starts, the frame
        /// is decoded, and the samples before it in that frame passed over.
        /// TTA frames are independent, so that is exact.
        /// </summary>
        private void Seek(long frame)
        {
            frame = Math.Clamp(frame, 0, _totalFrames);
            int index = (int)Math.Min(frame / _frameLength, _frameCount);
            _nextFrame = index;
            _decodedFrames = _decodedTaken = 0;
            _frame = (long)index * _frameLength;
            int skip = (int)(frame - _frame);
            if (skip > 0 && DecodeFrame())
            {
                _decodedTaken = Math.Min(skip, _decodedFrames);
                _frame += _decodedTaken;
            }
        }

        public bool Reopen(int sampleRate, int channels) => true;

        /// <summary>
        /// The next frame into _decoded. A frame the file does not hold all of
        /// is the end; a frame that is there but will not decode plays as
        /// silence of the right length, so the clock and the rest of the track
        /// stay where they belong.
        /// </summary>
        private bool DecodeFrame()
        {
            if (_nextFrame >= _frameCount) return false;
            int index = _nextFrame;
            int length = index == _frameCount - 1 && _lastFrameLength != 0 ? _lastFrameLength : _frameLength;
            int size = _frameSize[index];
            if (size < 4) return false;

            var s = _source!;
            if (_packet.Length < size + 8) _packet = new byte[size + 8];
            s.Position = _frameStart[index];
            if (ReadFully(s, _packet, size) < size) return false;
            _nextFrame++;

            int count = length * Channels;
            if (DecodeSamples(_packet, size, length))
            {
                float scale = 1f / (1 << (_bits - 1));
                for (int i = 0; i < count; i++) _decoded[i] = _samples[i] * scale;
            }
            else
            {
                Array.Clear(_decoded, 0, count);
                Diagnostic = $"frame {index} of the TTA file is damaged and was played as silence";
            }
            _decodedFrames = length;
            _decodedTaken = 0;
            return true;
        }

        /// <summary>
        /// One frame into _samples, interleaved, at the file's bit depth.
        /// Internal so the comparison with ffmpeg can read integers.
        /// </summary>
        internal bool DecodeSamples(byte[] data, int size, int length)
        {
            try { return DecodeTta(data, size, length); }
            catch { return false; }
        }

        private bool DecodeTta(byte[] data, int size, int length)
        {
            unchecked
            {
                _packet = data;
                _bytes = size;
                _at = 0;
                long totalBits = (long)size * 8;
                int shift = _bytesPerSample switch { 1 => 10, 2 => 9, _ => 10 };
                int round = (int)Shift1[shift - 1];

                foreach (var c in _state)
                {
                    Array.Clear(c.Qm);
                    Array.Clear(c.Dx);
                    Array.Clear(c.Dl);
                    c.Error = 0;
                    c.Predictor = 0;
                    c.K0 = c.K1 = 10;
                    c.Sum0 = c.Sum1 = Shift1[10 + 4];
                }

                int channels = Channels, total = length * channels, channel = 0;
                var output = _samples;
                for (int p = 0; p < total; p++)
                {
                    var c = _state[channel];

                    if (!Unary(totalBits, out uint unary)) return false;
                    uint k;
                    bool second = unary != 0;
                    if (second) { k = c.K1; unary--; }
                    else k = c.K0;

                    if (totalBits - _at < k || k > 25) return false;
                    int value = k != 0 ? (int)((unary << (int)k) + Read((int)k)) : (int)unary;

                    if (second)
                    {
                        c.Sum1 += (uint)value - (c.Sum1 >> 4);
                        if (c.K1 > 0 && c.Sum1 < Shift1[c.K1 + 4]) c.K1--;
                        else if (c.Sum1 > Shift1[c.K1 + 5]) c.K1++;
                        value += (int)Shift1[c.K0];
                    }
                    c.Sum0 += (uint)value - (c.Sum0 >> 4);
                    if (c.K0 > 0 && c.Sum0 < Shift1[c.K0 + 4]) c.K0--;
                    else if (c.Sum0 > Shift1[c.K0 + 5]) c.K0++;

                    int sample = 1 + ((value >> 1) ^ ((value & 1) - 1));
                    Filter(c, ref sample, shift, round);

                    // The fixed predictor on top of the adaptive filter.
                    sample += _bytesPerSample switch
                    {
                        1 => (int)(((long)c.Predictor * 15) >> 4),
                        2 or 3 => (int)(((long)c.Predictor * 31) >> 5),
                        _ => c.Predictor,
                    };
                    c.Predictor = sample;
                    output[p] = sample;

                    if (channel < channels - 1)
                        channel++;
                    else
                    {
                        // The last channel was stored relative to the rest;
                        // undo the differences from the last channel down.
                        if (channels > 1)
                        {
                            output[p] += output[p - 1] / 2;
                            for (int r = p - 1; r > p - channels; r--) output[r] = output[r + 1] - output[r];
                        }
                        channel = 0;
                    }
                }
                return _at <= totalBits;
            }
        }

        /// <summary>The adaptive filter (ttadsp's filter_process): eight taps whose weights follow the sign of the last error.</summary>
        private static void Filter(Channel c, ref int value, int shift, int round)
        {
            unchecked
            {
                var qm = c.Qm;
                var dx = c.Dx;
                var dl = c.Dl;
                if (c.Error < 0) for (int j = 0; j < 8; j++) qm[j] -= dx[j];
                else if (c.Error > 0) for (int j = 0; j < 8; j++) qm[j] += dx[j];

                int sum = round;
                for (int j = 0; j < 8; j++) sum += dl[j] * qm[j];

                dx[0] = dx[1]; dx[1] = dx[2]; dx[2] = dx[3]; dx[3] = dx[4];
                dl[0] = dl[1]; dl[1] = dl[2]; dl[2] = dl[3]; dl[3] = dl[4];

                dx[4] = (dl[4] >> 30) | 1;
                dx[5] = ((dl[5] >> 30) | 2) & ~1;
                dx[6] = ((dl[6] >> 30) | 2) & ~1;
                dx[7] = ((dl[7] >> 30) | 4) & ~3;

                c.Error = value;
                value += sum >> shift;

                dl[4] = -dl[5]; dl[5] = -dl[6];
                dl[6] = value - dl[7]; dl[7] = value;
                dl[5] += dl[6]; dl[4] += dl[5];
            }
        }

        private static uint[] MakeShift1()
        {
            var t = new uint[40];
            for (int i = 0; i < t.Length; i++) t[i] = i < 32 ? 1u << i : 0x80000000u;
            return t;
        }

        // ---- bits, least significant first ----

        private ulong Window()
        {
            long b = _at >> 3;
            ulong w;
            if (b + 8 <= _bytes)
                w = BinaryPrimitives.ReadUInt64LittleEndian(_packet.AsSpan((int)b, 8));
            else
            {
                w = 0;
                for (int i = 7; i >= 0; i--)
                {
                    long at = b + i;
                    w = (w << 8) | (at < _bytes ? _packet[at] : 0u);
                }
            }
            return w >> (int)(_at & 7);
        }

        private uint Read(int n)
        {
            if (n <= 0) return 0;
            uint v = (uint)(Window() & ((1UL << n) - 1));
            _at += n;
            return v;
        }

        /// <summary>Ones up to the first zero, which is used up too; false if the frame runs out first.</summary>
        private bool Unary(long totalBits, out uint count)
        {
            count = 0;
            while (true)
            {
                if (_at >= totalBits) return false;
                uint w = (uint)Window();
                int ones = BitOperations.TrailingZeroCount(~w);
                if (ones < 32)
                {
                    _at += ones + 1;
                    count += (uint)ones;
                    return _at <= totalBits;
                }
                _at += 32;
                count += 32;
            }
        }

        private static int ReadFully(Stream s, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buffer, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                try { _source?.Dispose(); } catch { }
                _source = null;
            }
        }
    }
}
