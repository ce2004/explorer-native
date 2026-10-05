using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.ComTypes;

namespace ExplorerNative
{
    /// <summary>
    /// Finds the FLAC frame a seek target is in, from the frames themselves.
    ///
    /// Every FLAC frame header carries its own position — a frame number, or
    /// in a variable-blocksize stream the sample number — protected by a CRC-8.
    /// Positions only increase through the file, so the frame is found by
    /// bisection, narrowed first by the seek table when there is one. The
    /// decoder is then reopened on the stream's STREAMINFO followed by the file
    /// from that frame on; FLAC frames decode independently, so the first one
    /// is exact.
    ///
    /// A frame sync is two bytes and turns up inside audio data by chance, so a
    /// candidate has to agree with STREAMINFO about rate, channels and depth,
    /// pass its CRC, and be followed by a frame whose position is its own plus
    /// its length. See <see cref="ExactSeek"/> for why Media Foundation's own
    /// seek is not used.
    /// </summary>
    internal sealed class FlacSeek : ExactSeek
    {
        /// <summary>A twentieth of a second of run-up before the target.</summary>
        private const int PreRollDivisor = 20;

        /// <summary>Where bisection gives way to walking frame by frame.</summary>
        private const long BisectFloor = 64 * 1024;

        /// <summary>
        /// How far a search looks for the next frame header. A frame of 65535
        /// samples of eight 32-bit channels is the theoretical worst case; real
        /// frames are a few kilobytes.
        /// </summary>
        private const int MaxFrameGap = 4 * 1024 * 1024;

        private static readonly byte[] Crc8Table = BuildCrc8();

        private readonly byte[] _streamInfo;
        private readonly long _audioStart;
        private readonly int _rate, _channels, _bits;
        private readonly long _totalSamples;
        private readonly bool _variable;
        private readonly int _fixedBlock;
        private readonly List<(long Sample, long Offset)> _seekPoints = new();
        private readonly byte[] _window = new byte[64 * 1024];
        private long _windowAt = -1;
        private int _windowLength;

        private FlacSeek(ByteSource bytes, byte[] streamInfo, long audioStart, bool variable, int fixedBlock)
            : base(bytes)
        {
            _streamInfo = streamInfo;
            _audioStart = audioStart;
            _variable = variable;
            _fixedBlock = fixedBlock;

            ulong packed = ReadUInt64(streamInfo, 10);
            _rate = (int)(packed >> 44);
            _channels = (int)((packed >> 41) & 7) + 1;
            _bits = (int)((packed >> 36) & 31) + 1;
            _totalSamples = (long)(packed & 0xFFFFFFFFFUL);
        }

        public static FlacSeek? TryCreate(ByteSource bytes, long start, out bool retry)
        {
            retry = false;

            // The metadata can hold a cover picture of several megabytes, so it
            // is walked block by block rather than read whole. Seek table offsets
            // count from the first frame, wherever the file puts it.
            long at = start + 4;
            byte[]? streamInfo = null;
            var table = new List<(long, long)>();
            var header = new byte[4];

            while (true)
            {
                if (!bytes.Holds(at, at + 4)) { retry = true; return null; }
                if (bytes.ReadHeld(at, header) < 4) return null;

                bool last = (header[0] & 0x80) != 0;
                int type = header[0] & 0x7F;
                int length = (header[1] << 16) | (header[2] << 8) | header[3];

                if (type == 0 || type == 3)
                {
                    if (!bytes.Holds(at + 4, at + 4 + length)) { retry = true; return null; }
                    var block = new byte[length];
                    if (bytes.ReadHeld(at + 4, block) < length) return null;

                    if (type == 0 && length == 34) streamInfo = block;
                    else if (type == 3)
                    {
                        for (int i = 0; i + 18 <= length; i += 18)
                        {
                            long sample = (long)ReadUInt64(block, i);
                            if (sample == -1) continue;             // placeholder
                            table.Add((sample, (long)ReadUInt64(block, i + 8)));
                        }
                    }
                }

                at += 4 + length;
                if (last) break;
                if (at >= bytes.Length) return null;
            }

            if (streamInfo == null) return null;

            // The first frame says whether positions are frame numbers or sample
            // numbers, and how long a frame is when they are frame numbers.
            if (!bytes.Holds(at, at + 32)) { retry = true; return null; }
            var probe = new FlacSeek(bytes, streamInfo, at, false, 0);
            if (!probe.TryHeaderAt(at, out var first, useFirst: false)) return null;

            var seeker = new FlacSeek(bytes, streamInfo, at, first.Variable, first.Block);
            foreach (var point in table) seeker._seekPoints.Add(point);
            seeker._seekPoints.Sort();
            return seeker;
        }

        public override double? Length => _totalSamples > 0 && _rate > 0 ? _totalSamples / (double)_rate : null;

        public override bool TryLocate(double seconds, int outputRate, out IStream stream, out double start)
        {
            stream = null!;
            start = 0;

            long target = (long)Math.Round(Math.Max(0, seconds) * _rate);
            if (_totalSamples > 0 && target >= _totalSamples) target = _totalSamples - 1;

            // A little before the target, so the resampler's first moments are
            // among what gets thrown away.
            target = Math.Max(0, target - _rate / PreRollDivisor);

            // And back to a frame on the output's grid. Fixed-size frames make
            // that a frame number; a variable-size stream is left where it is.
            if (!_variable && _fixedBlock > 0)
            {
                long step = GridStep(_rate, outputRate);
                long frames = step / Gcd(step, _fixedBlock);
                target = target / _fixedBlock / frames * frames * _fixedBlock;
            }

            if (!Find(target, out var frame)) return false;

            stream = new VirtualStream(HeaderFor(frame.Sample), Bytes, frame.Offset);
            start = frame.Sample / (double)_rate;
            return true;
        }

        /// <summary>
        /// "fLaC" and STREAMINFO alone, marked as the last metadata block, with
        /// the sample count reduced to what is left and the MD5 cleared — it
        /// describes the shortened stream, not the file.
        /// </summary>
        private byte[] HeaderFor(long startSample)
        {
            var header = new byte[4 + 4 + 34];
            header[0] = (byte)'f'; header[1] = (byte)'L'; header[2] = (byte)'a'; header[3] = (byte)'C';
            header[4] = 0x80;
            header[7] = 34;
            Array.Copy(_streamInfo, 0, header, 8, 34);

            ulong packed = ReadUInt64(_streamInfo, 10);
            long remaining = _totalSamples > 0 ? Math.Max(0, _totalSamples - startSample) : 0;
            packed = (packed & ~0xFFFFFFFFFUL) | ((ulong)remaining & 0xFFFFFFFFFUL);
            for (int i = 0; i < 8; i++) header[8 + 10 + i] = (byte)(packed >> (56 - 8 * i));
            Array.Clear(header, 8 + 18, 16);
            return header;
        }

        private readonly struct Frame
        {
            public readonly long Offset, Sample;
            public readonly int Block;
            public readonly bool Variable;

            public Frame(long offset, long sample, int block, bool variable)
            {
                Offset = offset;
                Sample = sample;
                Block = block;
                Variable = variable;
            }
        }

        /// <summary>The last genuine frame starting at or before <paramref name="target"/>.</summary>
        private bool Find(long target, out Frame best)
        {
            best = default;

            long lo = _audioStart, hi = Bytes.Length;

            // The seek table brackets it: the last point at or before the target
            // and the first after it.
            foreach (var (sample, offset) in _seekPoints)
            {
                long at = _audioStart + offset;
                if (at >= Bytes.Length) break;
                if (sample <= target) lo = Math.Max(lo, at);
                else { hi = Math.Min(hi, at + MaxFrameGap); break; }
            }

            // Part way through a download, the search stays inside what has
            // arrived — which is enough whenever the target is in it, and the
            // last whole frame there says whether it is.
            long held = Bytes.HeldEnd;
            if (lo >= held) return false;
            if (hi > held)
            {
                if (!NextFrame(Math.Max(lo, held - BisectFloor), held, out var edge) || edge.Sample <= target)
                    return false;
                hi = held;
            }

            if (!NextFrame(lo, Bytes.Length, out best) || best.Sample > target)
            {
                if (lo == _audioStart) return false;
                lo = _audioStart;
                if (!NextFrame(lo, Bytes.Length, out best) || best.Sample > target) return false;
            }

            while (hi - lo > BisectFloor)
            {
                long mid = lo + (hi - lo) / 2;

                if (!NextFrame(mid, hi, out var frame)) { hi = mid; continue; }

                if (frame.Sample <= target)
                {
                    best = frame;
                    lo = frame.Offset + 1;
                }
                else hi = mid;
            }

            // Frame by frame from the best so far until one starts past the target.
            long from = best.Offset + 1;
            while (NextFrame(from, Bytes.Length, out var frame) && frame.Sample <= target)
            {
                best = frame;
                from = frame.Offset + 1;
            }

            return true;
        }

        /// <summary>
        /// The first genuine frame starting at or after <paramref name="from"/>
        /// and before <paramref name="limit"/>: a header that checks out,
        /// followed by another that continues it — or the last frame of the file.
        /// </summary>
        private bool NextFrame(long from, long limit, out Frame frame)
        {
            long at = from;
            long give = Math.Min(limit, from + MaxFrameGap);

            while (FindHeader(at, give, out frame))
            {
                long expect = frame.Sample + frame.Block;
                if (_totalSamples > 0 && expect >= _totalSamples) return true;

                if (FindHeader(frame.Offset + 1, frame.Offset + 1 + MaxFrameGap, out var next))
                {
                    if (next.Sample == expect) return true;
                }
                else if (!Bytes.Holds(frame.Offset, frame.Offset + MaxFrameGap) ||
                         frame.Offset + MaxFrameGap >= Bytes.Length)
                {
                    // Nothing follows: the last frame, or the end of what is here.
                    return Bytes.Holds(frame.Offset, Bytes.Length);
                }

                at = frame.Offset + 1;
            }

            return false;
        }

        private bool FindHeader(long from, long limit, out Frame frame)
        {
            limit = Math.Min(limit, Bytes.Length);

            for (long at = from; at < limit;)
            {
                if (!Fill(at)) break;

                int start = (int)(at - _windowAt);
                int end = (int)Math.Min(_windowLength - 1, limit - _windowAt);

                for (int i = start; i < end; i++)
                {
                    if (_window[i] == 0xFF && (_window[i + 1] & 0xFE) == 0xF8 &&
                        TryHeaderAt(_windowAt + i, out frame, useFirst: true))
                        return true;
                }

                // A sync on the last byte of the window has its second byte in
                // the next one.
                long next = _windowAt + Math.Max(end, start + 1);
                if (next <= at) break;
                at = next;
            }

            frame = default;
            return false;
        }

        /// <summary>Loads the window so that it starts at <paramref name="at"/>, when it does not already cover enough.</summary>
        private bool Fill(long at)
        {
            if (_windowAt >= 0 && at >= _windowAt && at + 32 <= _windowAt + _windowLength) return true;

            int want = (int)Math.Min(_window.Length, Bytes.Length - at);
            if (want < 2 || !Bytes.Holds(at, at + want)) return false;

            _windowLength = Bytes.ReadHeld(at, _window.AsSpan(0, want));
            _windowAt = at;
            return _windowLength >= 2;
        }

        /// <summary>
        /// Parses and checks a frame header. <paramref name="useFirst"/> holds
        /// it to the stream's blocking strategy, which is only known once the
        /// first frame has been read.
        /// </summary>
        private bool TryHeaderAt(long at, out Frame frame, bool useFirst)
        {
            frame = default;

            Span<byte> h = stackalloc byte[16];
            int got = Bytes.ReadHeld(at, h);
            if (got < 6) return false;
            if (h[0] != 0xFF || (h[1] & 0xFE) != 0xF8) return false;

            bool variable = (h[1] & 1) != 0;
            if (useFirst && variable != _variable) return false;

            int blockCode = h[2] >> 4, rateCode = h[2] & 15;
            int channelCode = h[3] >> 4, bitsCode = (h[3] >> 1) & 7;
            if (blockCode == 0 || rateCode == 15 || channelCode > 10 || bitsCode == 3 || (h[3] & 1) != 0)
                return false;

            // The position, UTF-8 coded: up to 36 bits.
            int p = 4;
            int lead = h[p];
            int extra;
            long number;
            if (lead < 0x80) { number = lead; extra = 0; }
            else if (lead >= 0xC0 && lead < 0xE0) { number = lead & 0x1F; extra = 1; }
            else if (lead >= 0xE0 && lead < 0xF0) { number = lead & 0x0F; extra = 2; }
            else if (lead >= 0xF0 && lead < 0xF8) { number = lead & 0x07; extra = 3; }
            else if (lead >= 0xF8 && lead < 0xFC) { number = lead & 0x03; extra = 4; }
            else if (lead >= 0xFC && lead < 0xFE) { number = lead & 0x01; extra = 5; }
            else if (lead == 0xFE) { number = 0; extra = 6; }
            else return false;

            p++;
            if (p + extra + 4 > got) return false;
            for (int i = 0; i < extra; i++, p++)
            {
                if ((h[p] & 0xC0) != 0x80) return false;
                number = (number << 6) | (uint)(h[p] & 0x3F);
            }

            int block = blockCode switch
            {
                1 => 192,
                >= 2 and <= 5 => 576 << (blockCode - 2),
                6 => h[p++] + 1,
                7 => ((h[p++] << 8) | h[p++]) + 1,
                _ => 256 << (blockCode - 8),
            };

            int rate = rateCode switch
            {
                0 => _rate,
                1 => 88200, 2 => 176400, 3 => 192000, 4 => 8000, 5 => 16000, 6 => 22050,
                7 => 24000, 8 => 32000, 9 => 44100, 10 => 48000, 11 => 96000,
                12 => h[p++] * 1000,
                13 => (h[p++] << 8) | h[p++],
                _ => ((h[p++] << 8) | h[p++]) * 10,
            };

            if (p >= got) return false;
            if (Crc8(h.Slice(0, p)) != h[p]) return false;

            int channels = channelCode <= 7 ? channelCode + 1 : 2;
            int bits = bitsCode switch { 0 => _bits, 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, _ => 32 };
            if (_rate > 0 && (rate != _rate || channels != _channels || bits != _bits)) return false;

            long sample = variable ? number : number * (useFirst ? _fixedBlock : block);
            if (_totalSamples > 0 && sample >= _totalSamples) return false;

            frame = new Frame(at, sample, block, variable);
            return true;
        }

        private static byte Crc8(ReadOnlySpan<byte> data)
        {
            byte crc = 0;
            foreach (byte b in data) crc = Crc8Table[crc ^ b];
            return crc;
        }

        private static byte[] BuildCrc8()
        {
            var table = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int c = i;
                for (int bit = 0; bit < 8; bit++) c = (c & 0x80) != 0 ? ((c << 1) ^ 0x07) & 0xFF : (c << 1) & 0xFF;
                table[i] = (byte)c;
            }
            return table;
        }

        private static ulong ReadUInt64(byte[] data, int at)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++) value = (value << 8) | data[at + i];
            return value;
        }
    }
}
