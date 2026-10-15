using System;
using System.Buffers.Binary;
using System.Numerics;

namespace ExplorerNative
{
    /// <summary>
    /// Apple Lossless (ALAC) packets turned back into samples, one packet at a
    /// time. The container (an .m4a's sample table, a .caf's packet table) is
    /// someone else's job: this takes the magic cookie once and then whatever
    /// packets it is handed.
    ///
    /// Written from the semantics of Apple's open-source reference decoder
    /// (Apache 2.0) and ffmpeg's alac.c, and checked sample for sample against
    /// ffmpeg. Where the two differ in what they tolerate, this follows ffmpeg,
    /// because ffmpeg is what the comparison is made with.
    ///
    /// A damaged packet is not an exception: Decode answers 0 frames and the
    /// caller carries on with the next one, which is how a scratched rip or a
    /// cut-off download should sound, a moment of silence and not a stop.
    /// </summary>
    internal sealed class AlacDecoder
    {
        // Element tags, as in Apple's ALACAudioTypes.h.
        private const int Sce = 0, Cpe = 1, Lfe = 3, Dse = 4, Fil = 6, End = 7;

        /// <summary>
        /// Where each channel of the stream lands in the output, by channel
        /// count. ALAC stores the centre first (C, then L R, ...); everything
        /// downstream of the decoder expects the WAV order (L R C LFE ...), the
        /// same table ffmpeg uses, so the output matches ffmpeg's channel for
        /// channel.
        /// </summary>
        private static readonly byte[][] Placement =
        {
            new byte[] { 0 },
            new byte[] { 0, 1 },
            new byte[] { 2, 0, 1 },
            new byte[] { 2, 0, 1, 3 },
            new byte[] { 2, 0, 1, 3, 4 },
            new byte[] { 2, 0, 1, 4, 5, 3 },
            new byte[] { 2, 0, 1, 4, 5, 6, 3 },
            new byte[] { 2, 6, 7, 0, 1, 4, 5, 3 },
        };

        private readonly int _historyMult, _initialHistory, _riceLimit;
        private readonly int[][] _planar;
        private readonly int[][] _residual = new int[2][];
        private readonly int[][] _shifted = new int[2][];
        private readonly short[][] _coefs = { new short[32], new short[32] };
        private int[] _ints = Array.Empty<int>();

        // The bit reader: most significant bit first, over one packet.
        private byte[] _data = Array.Empty<byte>();
        private int _bytes;
        private long _at;

        public int Channels { get; }
        public int SampleRate { get; }
        public int BitDepth { get; }
        public int FrameLength { get; }

        private AlacDecoder(int frameLength, int bitDepth, int pb, int mb, int kb, int channels, int sampleRate)
        {
            FrameLength = frameLength;
            BitDepth = bitDepth;
            _historyMult = pb;
            _initialHistory = mb;
            _riceLimit = kb;
            Channels = channels;
            SampleRate = sampleRate;
            _planar = new int[channels][];
            for (int c = 0; c < channels; c++) _planar[c] = new int[frameLength];
            for (int c = 0; c < 2; c++)
            {
                _residual[c] = new int[frameLength];
                _shifted[c] = new int[frameLength];
            }
        }

        /// <summary>
        /// A decoder for the stream the magic cookie describes, or null with
        /// <paramref name="why"/> saying what was wrong with it.
        ///
        /// The cookie comes in more than one shape. An .m4a's 'alac' atom
        /// holds the 24-byte ALACSpecificConfig after its own 12-byte header; a
        /// .caf's 'kuki' chunk may hold the bare config, or a 'frma' atom
        /// followed by that same 'alac' atom, depending on what wrote it. So the
        /// wrappers are stepped over where they are recognised, and failing
        /// that the cookie is searched for a config that makes sense, rather
        /// than refusing a file over packaging.
        /// </summary>
        public static AlacDecoder? FromCookie(byte[] cookie, out string why)
        {
            if (cookie == null || cookie.Length < 24)
            {
                why = "the Apple Lossless setup (magic cookie) is missing or too short";
                return null;
            }

            int at = FindConfig(cookie);
            if (at < 0)
            {
                why = "the Apple Lossless setup (magic cookie) does not describe a stream this player decodes";
                return null;
            }

            int frameLength = (int)BinaryPrimitives.ReadUInt32BigEndian(cookie.AsSpan(at));
            int bitDepth = cookie[at + 5];
            int pb = cookie[at + 6], mb = cookie[at + 7], kb = cookie[at + 8];
            int channels = cookie[at + 9];
            int sampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(cookie.AsSpan(at + 20));
            why = "";
            return new AlacDecoder(frameLength, bitDepth, pb, mb, kb, channels, sampleRate);
        }

        /// <summary>Where the 24-byte config starts, or -1.</summary>
        private static int FindConfig(byte[] cookie)
        {
            int at = 0;
            while (at + 12 <= cookie.Length)
            {
                if (Tag(cookie, at + 4, "frma"))
                {
                    int size = (int)BinaryPrimitives.ReadUInt32BigEndian(cookie.AsSpan(at));
                    if (size < 8 || at + size > cookie.Length) break;
                    at += size;
                }
                else if (Tag(cookie, at + 4, "alac"))
                    at += 12;   // size, 'alac', version and flags
                else
                    break;
            }
            if (Plausible(cookie, at)) return at;

            // Not in any shape recognised: any 'alac' tag the config follows,
            // then any place at all where a sensible config sits.
            for (int i = 0; i + 4 <= cookie.Length; i++)
                if (Tag(cookie, i, "alac") && Plausible(cookie, i + 8)) return i + 8;
            for (int i = 0; i + 24 <= cookie.Length; i++)
                if (Plausible(cookie, i)) return i;
            return -1;
        }

        private static bool Tag(byte[] b, int at, string tag) =>
            at >= 0 && at + 4 <= b.Length &&
            b[at] == tag[0] && b[at + 1] == tag[1] && b[at + 2] == tag[2] && b[at + 3] == tag[3];

        /// <summary>Whether 24 bytes at <paramref name="at"/> read as a config this decoder can follow.</summary>
        private static bool Plausible(byte[] b, int at)
        {
            if (at < 0 || at + 24 > b.Length) return false;
            uint frameLength = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
            int version = b[at + 4], bits = b[at + 5], channels = b[at + 9];
            uint rate = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at + 20));
            return frameLength >= 1 && frameLength <= 1 << 20 && version == 0 &&
                   bits is 16 or 20 or 24 or 32 && channels >= 1 && channels <= 8 &&
                   rate >= 1 && rate <= 1_536_000;
        }

        /// <summary>
        /// Decodes one packet into interleaved float frames, full scale being
        /// 1.0, in WAV channel order. <paramref name="output"/> must hold at
        /// least FrameLength times Channels samples. Returns the frames written,
        /// or 0 for a packet that would not decode.
        /// </summary>
        public int Decode(byte[] packet, int length, float[] output)
        {
            if (_ints.Length < FrameLength * Channels) _ints = new int[FrameLength * Channels];
            int frames = DecodeSamples(packet, length, _ints);
            if (output == null || output.Length < frames * Channels) return 0;
            float scale = 1f / (1L << (BitDepth - 1));
            int samples = frames * Channels;
            for (int i = 0; i < samples; i++) output[i] = _ints[i] * scale;
            return frames;
        }

        /// <summary>
        /// The same as <see cref="Decode"/>, but the samples as integers at the
        /// stream's own bit depth: what the comparison with ffmpeg reads, since
        /// a 32-bit sample does not survive the trip through a float.
        /// </summary>
        internal int DecodeSamples(byte[] packet, int length, int[] output)
        {
            try
            {
                if (packet == null || output == null || length <= 0 || length > packet.Length) return 0;
                int frames = DecodePacket(packet, length);
                if (frames <= 0 || output.Length < frames * Channels) return 0;
                for (int c = 0; c < Channels; c++)
                {
                    var plane = _planar[c];
                    for (int i = 0, o = c; i < frames; i++, o += Channels) output[o] = plane[i];
                }
                return frames;
            }
            catch
            {
                // Anything a damaged packet trips over is the same answer.
                return 0;
            }
        }

        /// <summary>
        /// Walks the packet's elements until END: single channels, channel
        /// pairs and LFE are decoded; data and fill elements are passed over,
        /// as Apple's decoder does. Returns the frame count, or 0.
        /// </summary>
        private int DecodePacket(byte[] packet, int length)
        {
            _data = packet;
            _bytes = length;
            _at = 0;
            long totalBits = (long)length * 8;
            int channel = 0, frames = 0;

            while (true)
            {
                if (_at + 3 > totalBits) return 0;
                int element = (int)Read(3);
                if (element == End) break;

                switch (element)
                {
                    case Sce:
                    case Cpe:
                    case Lfe:
                    {
                        int count = element == Cpe ? 2 : 1;
                        if (channel + count > Channels) return 0;
                        int place = Placement[Channels - 1][channel];
                        if (place + count > Channels) return 0;
                        int got = DecodeElement(count, place, frames);
                        if (got <= 0) return 0;
                        frames = got;
                        channel += count;
                        break;
                    }
                    case Dse:
                    {
                        Skip(4);    // element instance tag
                        bool align = Read(1) != 0;
                        int count = (int)Read(8);
                        if (count == 255) count += (int)Read(8);
                        if (align) _at = (_at + 7) & ~7L;
                        _at += count * 8L;
                        break;
                    }
                    case Fil:
                    {
                        int count = (int)Read(4);
                        if (count == 15) count += (int)Read(8) - 1;
                        _at += count * 8L;
                        break;
                    }
                    default:
                        return 0;
                }
                if (_at > totalBits) return 0;
            }

            return channel == Channels ? frames : 0;
        }

        /// <summary>
        /// One SCE, LFE or CPE: header, then either compressed residuals run
        /// back through the adaptive predictor (and unmixed, for a pair) or
        /// plain samples, then the low "shift-off" bits put back on.
        /// </summary>
        private int DecodeElement(int count, int place, int framesSoFar)
        {
            unchecked
            {
                Skip(4);    // element instance tag
                Skip(12);   // unused
                bool hasSize = Read(1) != 0;
                int extraBits = (int)Read(2) << 3;
                int bps = BitDepth - extraBits + count - 1;
                bool compressed = Read(1) == 0;

                // The width only matters to compressed samples: a 32-bit pair
                // stored plain has no side channel needing a 33rd bit, and
                // Apple's decoder takes it (ffmpeg refuses it).
                if (compressed && (bps > 32 || bps < 1)) return -1;

                uint n = hasSize ? Read(32) : (uint)FrameLength;
                if (n == 0 || n > (uint)FrameLength) return -1;
                int frames = (int)n;
                if (framesSoFar != 0 && frames != framesSoFar) return -1;

                long totalBits = (long)_bytes * 8;
                int mixShift = 0, mixWeight = 0;

                if (compressed)
                {
                    if (_riceLimit == 0) return -1;
                    mixShift = (int)Read(8);
                    mixWeight = (int)Read(8);
                    if (count == 2 && mixWeight != 0 && mixShift > 31) return -1;

                    Span<int> type = stackalloc int[2], quant = stackalloc int[2],
                              mult = stackalloc int[2], order = stackalloc int[2];
                    for (int c = 0; c < count; c++)
                    {
                        type[c] = (int)Read(4);
                        quant[c] = (int)Read(4);
                        mult[c] = (int)Read(3);
                        order[c] = (int)Read(5);
                        if (order[c] >= FrameLength || quant[c] == 0) return -1;
                        for (int i = order[c] - 1; i >= 0; i--) _coefs[c][i] = (short)Read(16);
                    }

                    if (extraBits != 0)
                    {
                        if (totalBits - _at < (long)frames * count * extraBits) return -1;
                        for (int i = 0; i < frames; i++)
                            for (int c = 0; c < count; c++)
                                _shifted[c][i] = (int)Read(extraBits);
                    }

                    for (int c = 0; c < count; c++)
                    {
                        if (!Rice(_residual[c], frames, bps, mult[c] * _historyMult / 4)) return -1;

                        // Type 15 runs a first-order pass before the real one.
                        // Apple's encoder never writes it, but the format has it.
                        if (type[c] == 15) Predict(_residual[c], _residual[c], frames, bps, null, 31, 0);
                        Predict(_residual[c], _planar[place + c], frames, bps, _coefs[c], order[c], quant[c]);
                    }
                }
                else
                {
                    // The escape: samples stored as they are, used when
                    // compressing would have made the packet bigger.
                    if (totalBits - _at < (long)frames * count * BitDepth) return -1;
                    for (int i = 0; i < frames; i++)
                        for (int c = 0; c < count; c++)
                            _planar[place + c][i] = ReadSigned(BitDepth);
                    extraBits = 0;
                }

                if (_at > totalBits) return -1;

                if (count == 2 && mixWeight != 0)
                {
                    // Mid/side back to left and right.
                    var u = _planar[place];
                    var v = _planar[place + 1];
                    for (int i = 0; i < frames; i++)
                    {
                        int a = u[i], b = v[i];
                        a -= (b * mixWeight) >> mixShift;
                        b += a;
                        u[i] = b;
                        v[i] = a;
                    }
                }

                if (extraBits != 0)
                {
                    for (int c = 0; c < count; c++)
                    {
                        var plane = _planar[place + c];
                        var low = _shifted[c];
                        for (int i = 0; i < frames; i++) plane[i] = (plane[i] << extraBits) | low[i];
                    }
                }

                return frames;
            }
        }

        /// <summary>
        /// Apple's adaptive Golomb decoding of the residuals (dyn_decomp): the
        /// Rice parameter follows a running history of how big values have
        /// been, and a quiet stretch switches to counting runs of zeros.
        /// </summary>
        private bool Rice(int[] output, int frames, int bps, int historyMult)
        {
            unchecked
            {
                long totalBits = (long)_bytes * 8;
                uint history = (uint)_initialHistory, mult = (uint)historyMult, signModifier = 0;

                for (int i = 0; i < frames; i++)
                {
                    if (_at >= totalBits) return false;

                    int k = Math.Min(BitOperations.Log2((history >> 9) + 3), _riceLimit);
                    uint x = Scalar(k, bps) + signModifier;
                    signModifier = 0;
                    output[i] = (int)(x >> 1) ^ -(int)(x & 1);

                    if (x > 0xffff) history = 0xffff;
                    else history += x * mult - ((history * mult) >> 9);

                    if (history < 128 && i + 1 < frames)
                    {
                        k = Math.Min(7 - BitOperations.Log2(history) + (int)((history + 16) >> 6), _riceLimit);
                        int block = (int)Scalar(k, 16);
                        if (block > 0)
                        {
                            if (block >= frames - i) block = frames - i - 1;
                            Array.Clear(output, i + 1, block);
                            i += block;
                        }
                        if (block <= 0xffff) signModifier = 1;
                        history = 0;
                    }
                }
                return _at <= totalBits;
            }
        }

        /// <summary>One value: a unary prefix of up to 8, then k bits; 9 ones means the value follows whole.</summary>
        private uint Scalar(int k, int bps)
        {
            unchecked
            {
                uint p = Peek(9) << 23;
                int x = BitOperations.LeadingZeroCount(~p);
                if (x >= 9)
                {
                    Skip(9);
                    return Read(bps);
                }
                Skip(x + 1);
                uint value = (uint)x;
                if (k != 1)
                {
                    uint extra = Peek(k);
                    value = (value << k) - value;
                    if (extra > 1)
                    {
                        value += extra - 1;
                        Skip(k);
                    }
                    else
                        Skip(k - 1);
                }
                return value;
            }
        }

        /// <summary>
        /// The adaptive predictor (unpc_block): each sample predicted from the
        /// ones before it, the error added back, and the coefficients nudged
        /// toward whatever would have predicted better. Order 31 is the plain
        /// first-difference special case.
        /// </summary>
        private static void Predict(int[] error, int[] output, int frames, int bps, short[]? coefs, int order, int quant)
        {
            unchecked
            {
                output[0] = error[0];
                if (frames <= 1) return;

                if (order == 0)
                {
                    if (!ReferenceEquals(error, output)) Array.Copy(error, 1, output, 1, frames - 1);
                    return;
                }

                if (order == 31 || coefs == null)
                {
                    for (int n = 1; n < frames; n++) output[n] = SignExtend(output[n - 1] + error[n], bps);
                    return;
                }

                int i = 1;
                for (; i <= order && i < frames; i++) output[i] = SignExtend(output[i - 1] + error[i], bps);

                long round = 1L << (quant - 1);
                for (; i < frames; i++)
                {
                    int based = i - order - 1;
                    int d = output[based];
                    int val = 0;
                    for (int j = 0; j < order; j++) val += (output[based + 1 + j] - d) * coefs[j];
                    val = (int)((val + round) >> quant);
                    int errorVal = error[i];
                    val += d + errorVal;
                    output[i] = SignExtend(val, bps);

                    int errorSign = Math.Sign(errorVal);
                    if (errorSign != 0)
                    {
                        for (int j = 0; j < order && errorVal * errorSign > 0; j++)
                        {
                            int v = d - output[based + 1 + j];
                            int sign = Math.Sign(v) * errorSign;
                            coefs[j] = (short)(coefs[j] - sign);
                            v *= sign;
                            errorVal -= (v >> quant) * (j + 1);
                        }
                    }
                }
            }
        }

        private static int SignExtend(int value, int bits) =>
            bits >= 32 ? value : (value << (32 - bits)) >> (32 - bits);

        // ---- bits, most significant first ----

        private uint Peek(int n)
        {
            if (n <= 0) return 0;
            long b = _at >> 3;
            ulong w;
            if (b + 8 <= _bytes)
                w = BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan((int)b, 8));
            else
            {
                w = 0;
                for (int i = 0; i < 8; i++)
                {
                    long at = b + i;
                    w = (w << 8) | (at < _bytes ? _data[at] : 0u);
                }
            }
            return (uint)((w << (int)(_at & 7)) >> (64 - n));
        }

        private uint Read(int n)
        {
            uint v = Peek(n);
            _at += n;
            return v;
        }

        private int ReadSigned(int n)
        {
            uint v = Read(n);
            return n >= 32 ? (int)v : ((int)(v << (32 - n))) >> (32 - n);
        }

        private void Skip(int n) => _at += n;
    }
}
