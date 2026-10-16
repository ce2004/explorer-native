using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ExplorerNative
{
    /// <summary>
    /// Sound files Media Foundation will not decode, decoded here. Tried only
    /// after it has refused the file (see TrackDecoder), so a file Windows plays
    /// is never decoded here. Measured: every one of these went to Windows
    /// Media Player as "cannot play".
    ///
    /// The containers: WAV (RIFF and RF64) and Sony's Wave64; Apple's CAF;
    /// Sun's AU (.au, .snd); Creative's VOC; and AIFF-C holding Apple's IMA
    /// ADPCM (the other AIFFs go to Windows as WAV, see AiffStream).
    ///
    /// The sound in them: PCM of 8, 16, 24 and 32 bits either way round, 32-
    /// and 64-bit float, A-law and mu-law, IMA ADPCM (Microsoft's WAV kind and
    /// Apple's IMA4), Microsoft ADPCM, and Apple Lossless (ALAC) in CAF.
    ///
    /// Gives the file's own rate and channels; the player converts for the
    /// device. Named for where it started; the class keeps the name.
    /// </summary>
    internal sealed class WavDecoder : ITrackDecoder
    {
        private enum Codec { Pcm, Float, ALaw, MuLaw, ImaWav, MsAdpcm, Ima4, Alac }

        private Stream? _source;
        private readonly object _gate = new();
        private Codec _codec;
        private int _bits, _blockAlign, _samplesPerBlock;
        private bool _bigEndian, _unsigned8 = true;
        private long _dataStart, _dataLength, _totalFrames, _frame;
        private short[] _coef1 = Array.Empty<short>(), _coef2 = Array.Empty<short>();
        private byte[] _block = Array.Empty<byte>();
        private float[] _decoded = Array.Empty<float>();
        private int _decodedFrames, _decodedTaken;

        /// <summary>VOC: the sound blocks, each (offset in the file, length), read as one.</summary>
        private List<(long Offset, long Length)>? _segments;

        /// <summary>CAF with packets of different sizes (ALAC): where each starts, and the decoder.</summary>
        private long[]? _packetStarts;
        private AlacDecoder? _alac;
        private long _packet;

        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public double Duration => SampleRate > 0 ? _totalFrames / (double)SampleRate : 0;
        public double Position { get { lock (_gate) return SampleRate > 0 ? _frame / (double)SampleRate : 0; } }
        public string Diagnostic { get; private set; } = "not opened";

        /// <summary>
        /// The bits per sample the file stores, as AudioDecoder.NativeBits gives
        /// it, so converting keeps the depth: PCM and float their own, ALAC the
        /// cookie's, and zero for the companded and ADPCM kinds.
        /// </summary>
        public int NativeBits => _codec switch
        {
            Codec.Pcm or Codec.Float => _bits,
            Codec.Alac => _alac?.BitDepth ?? 0,
            _ => 0,
        };

        /// <summary>
        /// True when a RIFF WAV over 4GB had its 32-bit data size wrapped and the
        /// sound's length was taken from the file's instead. Windows reads such
        /// a file only as far as the wrapped size, so TrackDecoder gives it here.
        /// </summary>
        public bool LengthFromFile { get; private set; }

        /// <summary>Whether a file begins as one of the containers above.</summary>
        public static bool IsWav(string path, System.Runtime.InteropServices.ComTypes.IStream? stream)
        {
            try
            {
                using var source = stream != null ? (Stream)new ComStream(stream)
                    : new FileStream(NameRules.ExactPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);   // as Open shares it: a file still being recorded
                if (source.CanSeek) source.Position = 0;
                var head = new byte[20];
                int got = source.Read(head, 0, 20);
                return Container(head, got) != null;
            }
            catch { return false; }
        }

        private static string? Container(byte[] head, int got)
        {
            if (got < 12) return null;
            var four = Encoding.ASCII.GetString(head, 0, 4);
            var eight = Encoding.ASCII.GetString(head, 8, 4);
            if ((four == "RIFF" || four == "RF64") && eight == "WAVE") return "wav";
            if (four == "riff" && head[4] == 0x2E && head[5] == 0x91) return "w64";
            if (four == "caff") return "caf";
            if (four == ".snd") return "au";
            if (four == "FORM" && eight == "AIFC") return "aifc";
            if (got >= 19 && Encoding.ASCII.GetString(head, 0, 19) == "Creative Voice File") return "voc";
            return null;
        }

        public bool Open(string path, System.Runtime.InteropServices.ComTypes.IStream? stream)
        {
            try
            {
                _source = stream != null
                    ? new ComStream(stream)
                    : new FileStream(NameRules.ExactPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
                var head = new byte[20];
                _source.Position = 0;
                int got = ReadFully(_source, head, 20);
                _source.Position = 0;
                var reader = new BigLittle(_source);
                string? why = Container(head, got) switch
                {
                    "wav" => ReadWav(reader),
                    "w64" => ReadW64(reader),
                    "caf" => ReadCaf(reader),
                    "au" => ReadAu(reader),
                    "aifc" => ReadAifc(reader),
                    "voc" => ReadVoc(reader),
                    _ => "not a sound file this player reads",
                };
                if (why == null) why = Settle();
                if (why != null) { Diagnostic = why; return false; }
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

        // ---------------- the containers ----------------

        /// <summary>Little-endian and big-endian reads off the source.</summary>
        private sealed class BigLittle
        {
            private readonly Stream _s;
            private readonly byte[] _b = new byte[8];
            public BigLittle(Stream s) { _s = s; }
            public Stream Stream => _s;
            private byte[] Take(int n) { if (ReadFully(_s, _b, n) < n) throw new EndOfStreamException(); return _b; }
            public string Tag() => Encoding.ASCII.GetString(Take(4), 0, 4);
            public int U8() => Take(1)[0];
            public int U16() { var b = Take(2); return b[0] | b[1] << 8; }
            public long U32() { var b = Take(4); return (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24); }
            public long U64() { var b = Take(8); return BitConverter.ToInt64(b, 0); }
            public int U16B() { var b = Take(2); return b[0] << 8 | b[1]; }
            public long U32B() { var b = Take(4); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
            public long I64B() { var b = Take(8); long v = 0; for (int i = 0; i < 8; i++) v = v << 8 | b[i]; return v; }
            public double F64B() => BitConverter.Int64BitsToDouble(I64B());
            public void Skip(long n) => _s.Position += n;
        }

        private string? ReadWav(BigLittle r)
        {
            var s = r.Stream;
            bool rf64 = r.Tag() == "RF64";
            r.U32();
            r.Tag();
            long rf64Data = -1;
            bool haveFormat = false;
            while (s.Position + 8 <= s.Length)
            {
                var id = r.Tag();
                long size = r.U32();
                long body = s.Position;
                if (id == "ds64" && rf64) { r.U64(); rf64Data = r.U64(); }
                else if (id == "fmt ") { var why = ReadWaveFormat(r, size); if (why != null) return why; haveFormat = true; }
                else if (id == "data")
                {
                    _dataStart = body;
                    _dataLength = rf64 && size == 0xFFFFFFFF && rf64Data >= 0 ? rf64Data : size;
                    // Over 4GB in a plain RIFF, the 32-bit size wraps (or is
                    // pinned at its largest), and the sound was cut to whatever
                    // was left of it: a 5GB recording played its last gigabyte's
                    // worth and stopped. When the file runs on past the size by
                    // whole multiples of 4GB (give or take the pad byte), the
                    // data chunk is the last one and the file's length is the truth.
                    if (!(rf64 && rf64Data >= 0))
                    {
                        long there = s.Length - body;
                        if (there > uint.MaxValue && (size == 0xFFFFFFFF || (there - size) % (1L << 32) <= 1))
                        {
                            _dataLength = there;
                            LengthFromFile = true;
                        }
                    }
                    if (haveFormat) break;
                    size = _dataLength;
                }
                s.Position = body + size + (size & 1);
            }
            return haveFormat ? null : "the WAV file has no format chunk";
        }

        /// <summary>Wave64: WAV's chunks, named by GUIDs and sized in 64 bits including their own header.</summary>
        private string? ReadW64(BigLittle r)
        {
            var s = r.Stream;
            s.Position = 40;
            bool haveFormat = false;
            while (s.Position + 24 <= s.Length)
            {
                var id = r.Tag();
                r.Skip(12);
                long size = r.U64();
                long body = s.Position;
                if (id == "fmt ") { var why = ReadWaveFormat(r, size - 24); if (why != null) return why; haveFormat = true; }
                else if (id == "data") { _dataStart = body; _dataLength = size - 24; if (haveFormat) break; }
                // The size counts the chunk's own 24-byte header; chunks are padded to 8 bytes.
                if (size < 24 || size > s.Length) break;
                s.Position = body - 24 + ((size + 7) & ~7L);
            }
            return haveFormat ? null : "the Wave64 file has no format chunk";
        }

        private string? ReadWaveFormat(BigLittle r, long size)
        {
            int format = r.U16();
            Channels = r.U16();
            SampleRate = (int)r.U32();
            r.U32();
            _blockAlign = r.U16();
            _bits = r.U16();
            int extra = size >= 18 ? r.U16() : 0;
            if (format == 0xFFFE && extra >= 22)
            {
                r.U16();
                r.U32();
                format = r.U16();
            }
            else if (format == 2 && extra >= 4)
            {
                _samplesPerBlock = r.U16();
                int count = r.U16();
                _coef1 = new short[count];
                _coef2 = new short[count];
                for (int i = 0; i < count; i++) { _coef1[i] = (short)r.U16(); _coef2[i] = (short)r.U16(); }
            }
            else if (format == 0x11 && extra >= 2) _samplesPerBlock = r.U16();

            switch (format)
            {
                case 1: _codec = Codec.Pcm; break;
                case 3: _codec = Codec.Float; break;
                case 6: _codec = Codec.ALaw; break;
                case 7: _codec = Codec.MuLaw; break;
                case 2: _codec = Codec.MsAdpcm; break;
                case 0x11: _codec = Codec.ImaWav; break;
                case 0x55: return "MP3 sound inside a WAV file";
                default: return $"a WAV format this player does not decode (format {format}, {_bits} bits)";
            }
            return null;
        }

        /// <summary>CAF: big-endian chunks with 64-bit sizes; 'desc' says the format, 'kuki' and 'pakt' serve ALAC.</summary>
        private string? ReadCaf(BigLittle r)
        {
            var s = r.Stream;
            s.Position = 8;
            string format = "";
            long flags = 0, bytesPerPacket = 0, framesPerPacket = 0;
            byte[]? cookie = null;
            var packetSizes = new List<long>();
            long validFrames = -1;
            while (s.Position + 12 <= s.Length)
            {
                var id = r.Tag();
                long size = r.I64B();
                long body = s.Position;
                switch (id)
                {
                    case "desc":
                        SampleRate = (int)Math.Round(r.F64B());
                        format = r.Tag();
                        flags = r.U32B();
                        bytesPerPacket = r.U32B();
                        framesPerPacket = r.U32B();
                        Channels = (int)r.U32B();
                        _bits = (int)r.U32B();
                        break;
                    case "kuki":
                        // The header's word is not trusted with memory: a real cookie is a few dozen bytes.
                        if (size < 0 || size > 1 << 20 || size > s.Length - body) return "the CAF file's decoder settings are damaged";
                        cookie = new byte[size];
                        ReadFully(s, cookie, (int)size);
                        break;
                    case "pakt":
                        long packets = r.I64B();
                        validFrames = r.I64B();
                        r.U32B();
                        r.U32B();
                        // Within the chunk, whatever count the header gives, and no packet
                        // bigger than a packet can be (one is read whole into memory).
                        long tableEnd = size < 0 ? s.Length : Math.Min(s.Length, body + size);
                        for (long i = 0; i < packets && s.Position < tableEnd; i++)
                        {
                            long v = 0;
                            int b, shifts = 0;
                            do { b = r.U8(); v = v << 7 | (uint)(b & 0x7F); } while ((b & 0x80) != 0 && ++shifts < 5);
                            if (v > 1 << 20) return "the CAF file's packet table is damaged";
                            packetSizes.Add(v);
                        }
                        break;
                    case "data":
                        _dataStart = body + 4;
                        _dataLength = size < 0 ? s.Length - _dataStart : size - 4;
                        break;
                }
                // Past the end (a cut-off file, or a damaged size that would
                // overflow the position): nothing after it can be read anyway.
                if (size < 0 || size > s.Length - body) break;
                s.Position = body + size;
            }
            if (_dataStart == 0) return "the CAF file has no sound in it";

            switch (format)
            {
                case "lpcm":
                    _codec = (flags & 1) != 0 ? Codec.Float : Codec.Pcm;
                    _bigEndian = (flags & 2) == 0;
                    _unsigned8 = false;
                    _blockAlign = (int)bytesPerPacket;
                    return null;
                case "ulaw": _codec = Codec.MuLaw; _blockAlign = Channels; _bits = 8; return null;
                case "alaw": _codec = Codec.ALaw; _blockAlign = Channels; _bits = 8; return null;
                case "ima4":
                    _codec = Codec.Ima4;
                    _blockAlign = 34 * Channels;
                    _samplesPerBlock = 64;
                    return null;
                case "alac":
                    if (cookie == null) return "the ALAC sound has no decoder settings (no 'kuki' chunk)";
                    _alac = AlacDecoder.FromCookie(cookie, out var why);
                    if (_alac == null) return "ALAC: " + why;
                    // The cookie decides how the packets decode; 'desc' disagreeing would size the buffer wrong.
                    Channels = _alac.Channels;
                    if (packetSizes.Count == 0) return "the ALAC sound has no packet table";
                    _codec = Codec.Alac;
                    // A packet holds at most the cookie's frame length. A damaged
                    // 'desc' said up to four billion, and the buffer sized from it
                    // ran the PC out of memory before a sound was heard.
                    if (framesPerPacket > _alac.FrameLength) return "the CAF file's packet size is damaged";
                    _samplesPerBlock = framesPerPacket > 0 ? (int)framesPerPacket : _alac.FrameLength;
                    _packetStarts = new long[packetSizes.Count + 1];
                    for (int i = 0; i < packetSizes.Count; i++) _packetStarts[i + 1] = _packetStarts[i] + packetSizes[i];
                    // No more frames than the packets hold, whatever 'pakt' claims.
                    long most = (long)packetSizes.Count * _samplesPerBlock;
                    _totalFrames = validFrames >= 0 ? Math.Min(validFrames, most) : most;
                    return null;
                default:
                    return $"CAF holding '{format.Trim()}' sound, which this player does not decode";
            }
        }

        /// <summary>Sun/NeXT AU: a big-endian header, then the sound.</summary>
        private string? ReadAu(BigLittle r)
        {
            var s = r.Stream;
            r.Tag();
            long offset = r.U32B(), size = r.U32B(), encoding = r.U32B();
            SampleRate = (int)r.U32B();
            Channels = (int)r.U32B();
            _dataStart = offset;
            _dataLength = size == 0xFFFFFFFF ? s.Length - offset : Math.Min(size, s.Length - offset);
            _bigEndian = true;
            _unsigned8 = false;
            (_codec, _bits) = encoding switch
            {
                1 => (Codec.MuLaw, 8),
                2 => (Codec.Pcm, 8),
                3 => (Codec.Pcm, 16),
                4 => (Codec.Pcm, 24),
                5 => (Codec.Pcm, 32),
                6 => (Codec.Float, 32),
                7 => (Codec.Float, 64),
                27 => (Codec.ALaw, 8),
                _ => (Codec.Pcm, 0),
            };
            if (_bits == 0) return $"an AU sound encoding this player does not decode ({encoding})";
            _blockAlign = _bits / 8 * Channels;
            return null;
        }

        /// <summary>AIFF-C with Apple's IMA ADPCM, the one AIFF Windows cannot be given as a WAV.</summary>
        private string? ReadAifc(BigLittle r)
        {
            var s = r.Stream;
            s.Position = 12;
            string compression = "";
            while (s.Position + 8 <= s.Length)
            {
                var id = r.Tag();
                long size = r.U32B();
                long body = s.Position;
                if (id == "COMM")
                {
                    Channels = r.U16B();
                    r.U32B();
                    _bits = r.U16B();
                    SampleRate = (int)Math.Round(Extended(r));
                    compression = size >= 22 ? r.Tag() : "NONE";
                }
                else if (id == "SSND")
                {
                    long offset = r.U32B();
                    r.U32B();
                    _dataStart = body + 8 + offset;
                    _dataLength = size - 8 - offset;
                }
                s.Position = body + size + (size & 1);
            }
            if (compression != "ima4") return $"AIFF-C holding '{compression}' sound, which this player does not decode";
            if (_dataStart == 0) return "the AIFF-C file has no sound in it";
            _codec = Codec.Ima4;
            _blockAlign = 34 * Channels;
            _samplesPerBlock = 64;
            return null;
        }

        /// <summary>The 80-bit extended float AIFF keeps its sample rate in.</summary>
        private static double Extended(BigLittle r)
        {
            int exponent = r.U16B();
            long mantissa = r.I64B();
            if (exponent == 0 && mantissa == 0) return 0;
            int sign = (exponent & 0x8000) != 0 ? -1 : 1;
            exponent &= 0x7FFF;
            return sign * (double)(ulong)mantissa * Math.Pow(2, exponent - 16383 - 63);
        }

        /// <summary>Creative VOC: numbered blocks; the sound blocks of one format are read as one stretch.</summary>
        private string? ReadVoc(BigLittle r)
        {
            var s = r.Stream;
            s.Position = 20;
            s.Position = r.U16();
            _segments = new List<(long, long)>();
            int stereoNext = 0, channelsNext = 1;
            while (s.Position < s.Length)
            {
                int type = r.U8();
                if (type == 0) break;
                long size = r.U8() | r.U8() << 8 | r.U8() << 16;
                long body = s.Position;
                if (type == 1)
                {
                    int rateCode = r.U8(), codec = r.U8();
                    if (codec != 0) return "a VOC file with compressed sound, which this player does not decode";
                    if (SampleRate == 0)
                    {
                        Channels = stereoNext > 0 ? channelsNext : 1;
                        SampleRate = stereoNext > 0 ? stereoNext : 1_000_000 / (256 - rateCode);
                        _codec = Codec.Pcm;
                        _bits = 8;
                    }
                    _segments.Add((body + 2, Math.Max(0, size - 2)));
                }
                else if (type == 2) _segments.Add((body, size));
                else if (type == 8)
                {
                    int constant = r.U16(); r.U8(); int mode = r.U8();
                    int channels = mode + 1;
                    channelsNext = channels;
                    stereoNext = (int)(256_000_000L / (channels * (65536 - constant)));
                }
                else if (type == 9)
                {
                    int rate = (int)r.U32(), bits = r.U8(), channels = r.U8(), codec = r.U16();
                    if (SampleRate == 0)
                    {
                        SampleRate = rate;
                        Channels = channels;
                        (_codec, _bits) = codec switch { 0 => (Codec.Pcm, 8), 4 => (Codec.Pcm, 16), 6 => (Codec.ALaw, 8), 7 => (Codec.MuLaw, 8), _ => (Codec.Pcm, 0) };
                        if (_bits == 0) return "a VOC file with compressed sound, which this player does not decode";
                    }
                    _segments.Add((body + 12, Math.Max(0, size - 12)));
                }
                s.Position = body + size;
            }
            if (_segments.Count == 0) return "the VOC file has no sound in it";
            _blockAlign = _bits / 8 * Channels;
            _dataStart = 0;
            _dataLength = 0;
            foreach (var (_, length) in _segments) _dataLength += length;
            return null;
        }

        /// <summary>The checks every container shares, and what follows from the format.</summary>
        private string? Settle()
        {
            if (Channels < 1 || Channels > 32 || SampleRate < 1000 || SampleRate > 768_000)
                return "the file's sound format makes no sense";
            if (_segments == null) _dataLength = Math.Max(0, Math.Min(_dataLength, _source!.Length - _dataStart));

            switch (_codec)
            {
                case Codec.Pcm when _bits is 8 or 16 or 24 or 32:
                case Codec.Float when _bits is 32 or 64:
                case Codec.ALaw or Codec.MuLaw:
                    if (_blockAlign < 1) _blockAlign = _bits / 8 * Channels;
                    // Smaller than one frame's samples, and decoding reads past each block.
                    if (_blockAlign < (_bits + 7) / 8 * Channels) return "the file's sound format makes no sense";
                    // Wider than any frame can be (eight bytes a sample): a damaged
                    // header's block size of 64KB made every read ask for 268MB.
                    if (_blockAlign > 8 * Channels) return "the file's sound format makes no sense";
                    _samplesPerBlock = 1;
                    break;
                // The header's samples per block, but never more than the block
                // holds: a damaged one counted frames that were not there (and
                // sized the buffer for them).
                case Codec.ImaWav when _bits == 4 && _blockAlign > 4 * Channels:
                {
                    int fits = (_blockAlign - 4 * Channels) * 2 / Channels + 1;
                    if (_samplesPerBlock <= 0 || _samplesPerBlock > fits) _samplesPerBlock = fits;
                    break;
                }
                case Codec.MsAdpcm when _bits == 4 && _blockAlign > 7 * Channels:
                {
                    int fits = (_blockAlign - 7 * Channels) * 2 / Channels + 2;
                    if (_samplesPerBlock <= 0 || _samplesPerBlock > fits) _samplesPerBlock = fits;
                    if (_coef1.Length == 0)
                    {
                        _coef1 = new short[] { 256, 512, 0, 192, 240, 460, 392 };
                        _coef2 = new short[] { 0, -256, 0, 64, 0, -208, -232 };
                    }
                    break;
                }
                case Codec.Ima4:
                case Codec.Alac:
                    break;
                default:
                    return $"a sound format this player does not decode ({_codec}, {_bits} bits)";
            }

            if (_codec != Codec.Alac) _totalFrames = _dataLength / _blockAlign * _samplesPerBlock;
            // A last ADPCM block cut short still decodes: its frames count too.
            // ALAC has no block size (its packets differ), and dividing by it
            // refused every ALAC .caf with "divide by zero".
            long rest = _blockAlign > 0 ? _dataLength % _blockAlign : 0;
            if (_codec == Codec.ImaWav && rest > 4 * Channels) _totalFrames += (rest - 4 * Channels) * 2 / Channels + 1;
            if (_codec == Codec.MsAdpcm && rest > 7 * Channels) _totalFrames += (rest - 7 * Channels) * 2 / Channels + 2;
            int perBlock = _codec == Codec.Alac ? Math.Max(_samplesPerBlock, _alac!.FrameLength) : _samplesPerBlock;
            _decoded = new float[Math.Max(perBlock, 4096) * Channels];
            _block = new byte[Math.Max(_blockAlign, 4096)];
            return null;
        }

        // ---------------- reading ----------------

        public int Read(float[] destination, int frames)
        {
            lock (_gate)
            {
                int done = 0;
                while (done < frames && _frame < _totalFrames)
                {
                    if (_decodedTaken >= _decodedFrames && !DecodeBlock()) break;
                    int take = (int)Math.Min(Math.Min(frames - done, _decodedFrames - _decodedTaken), _totalFrames - _frame);
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

        /// <summary>To a frame: the block or packet it is in, decoded, and the frames before it passed over.</summary>
        private void Seek(long frame)
        {
            frame = Math.Clamp(frame, 0, _totalFrames);
            long block = frame / _samplesPerBlock;
            _decodedFrames = _decodedTaken = 0;
            _frame = block * _samplesPerBlock;
            if (_codec == Codec.Alac) _packet = Math.Min(block, _packetStarts!.Length - 1);
            else _dataPosition = block * _blockAlign;
            int skip = (int)(frame - _frame);
            if (skip > 0 && DecodeBlock())
            {
                _decodedTaken = Math.Min(skip, _decodedFrames);
                _frame += _decodedTaken;
            }
        }

        public bool Reopen(int sampleRate, int channels) => true;

        /// <summary>Where the next read starts, within the sound (not the file).</summary>
        private long _dataPosition;

        /// <summary>Reads from the sound, wherever in the file (or VOC's blocks) it is.</summary>
        private int ReadData(byte[] buffer, int count)
        {
            int done = 0;
            var s = _source!;
            while (done < count && _dataPosition < _dataLength)
            {
                long file;
                long room;
                if (_segments == null) { file = _dataStart + _dataPosition; room = _dataLength - _dataPosition; }
                else
                {
                    long at = _dataPosition;
                    file = -1;
                    room = 0;
                    foreach (var (offset, length) in _segments)
                    {
                        if (at < length) { file = offset + at; room = length - at; break; }
                        at -= length;
                    }
                    if (file < 0) break;
                }
                s.Position = file;
                int n = s.Read(buffer, done, (int)Math.Min(count - done, room));
                if (n <= 0) break;
                done += n;
                _dataPosition += n;
            }
            return done;
        }

        /// <summary>The next stretch of sound into _decoded.</summary>
        private bool DecodeBlock()
        {
            _decodedTaken = 0;
            if (_codec == Codec.Alac)
            {
                if (_packet >= _packetStarts!.Length - 1) return false;
                long start = _packetStarts[_packet], length = _packetStarts[_packet + 1] - start;
                if (_block.Length < length) _block = new byte[length];
                _source!.Position = _dataStart + start;
                int got = ReadFully(_source, _block, (int)length);
                // Cut off: the sound ends where the file does, not in minutes of silence.
                if (got < length) return false;
                _packet++;
                _decodedFrames = _alac!.Decode(_block, got, _decoded);
                // A damaged packet is a packet's worth of silence, not the end.
                if (_decodedFrames == 0)
                {
                    _decodedFrames = _samplesPerBlock;
                    Array.Clear(_decoded, 0, _decodedFrames * Channels);
                }
                return true;
            }

            if (_samplesPerBlock == 1)
            {
                int frames = (int)Math.Min(4096, (_dataLength - _dataPosition) / _blockAlign);
                if (frames <= 0) return false;
                int bytes = frames * _blockAlign;
                if (_block.Length < bytes) _block = new byte[bytes];
                if (_decoded.Length < frames * Channels) _decoded = new float[frames * Channels];
                frames = ReadData(_block, bytes) / _blockAlign;
                if (frames <= 0) return false;
                DecodePcm(frames);
                _decodedFrames = frames;
                return true;
            }

            int read = ReadData(_block, _blockAlign);
            if (read <= 0) return false;
            _decodedFrames = _codec switch
            {
                Codec.ImaWav => read >= 4 * Channels ? DecodeIma(read) : 0,
                Codec.MsAdpcm => read >= 7 * Channels ? DecodeMs(read) : 0,
                Codec.Ima4 => read >= 34 * Channels ? DecodeIma4() : 0,
                _ => 0,
            };
            return _decodedFrames > 0;
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

        private void DecodePcm(int frames)
        {
            int samples = frames * Channels, bytesPer = _blockAlign / Channels;
            var b = _block;
            bool big = _bigEndian;
            for (int i = 0; i < samples; i++)
            {
                int at = i * bytesPer;
                _decoded[i] = (_codec, _bits) switch
                {
                    (Codec.Pcm, 8) => _unsigned8 ? (b[at] - 128) / 128f : (sbyte)b[at] / 128f,
                    (Codec.Pcm, 16) => (short)(big ? b[at] << 8 | b[at + 1] : b[at] | b[at + 1] << 8) / 32768f,
                    // Put in the top three bytes, then shifted down: the sign comes with it.
                    (Codec.Pcm, 24) => ((big ? b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8
                                             : b[at + 2] << 24 | b[at + 1] << 16 | b[at] << 8) >> 8) / 8388608f,
                    (Codec.Pcm, 32) => Int32(b, at, big) / 2147483648f,
                    (Codec.Float, 32) => BitConverter.Int32BitsToSingle(Int32(b, at, big)),
                    (Codec.Float, 64) => (float)BitConverter.Int64BitsToDouble(big
                        ? (long)Int32(b, at, true) << 32 | (uint)Int32(b, at + 4, true)
                        : (long)Int32(b, at + 4, false) << 32 | (uint)Int32(b, at, false)),
                    (Codec.ALaw, _) => ALawTable[b[at]] / 32768f,
                    (Codec.MuLaw, _) => MuLawTable[b[at]] / 32768f,
                    _ => 0f,
                };
            }
        }

        private static int Int32(byte[] b, int at, bool big) =>
            big ? b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]
                : b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24;

        // ---------------- IMA ADPCM, both kinds ----------------

        private static readonly int[] ImaIndex = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

        private static readonly int[] ImaStep =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80,
            88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544,
            598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749,
            3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635,
            13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
        };

        private static int ImaStepOnce(ref int predictor, ref int index, int nibble)
        {
            int step = ImaStep[index];
            int diff = step >> 3;
            if ((nibble & 4) != 0) diff += step;
            if ((nibble & 2) != 0) diff += step >> 1;
            if ((nibble & 1) != 0) diff += step >> 2;
            predictor = Math.Clamp(predictor + ((nibble & 8) != 0 ? -diff : diff), -32768, 32767);
            index = Math.Clamp(index + ImaIndex[nibble], 0, 88);
            return predictor;
        }

        /// <summary>Microsoft's IMA block: a header per channel (first sample, step index), then 4 bytes of each channel in turn.</summary>
        private int DecodeIma(int length)
        {
            int ch = Channels;
            var predictor = new int[ch];
            var index = new int[ch];
            for (int c = 0; c < ch; c++)
            {
                predictor[c] = BitConverter.ToInt16(_block, c * 4);
                index[c] = Math.Clamp((int)_block[c * 4 + 2], 0, 88);
                _decoded[c] = predictor[c] / 32768f;
            }

            int frames = 1, at = 4 * ch;
            while (at + 4 * ch <= length && frames < _samplesPerBlock)
            {
                for (int c = 0; c < ch; c++)
                    for (int i = 0; i < 8; i++)
                    {
                        int nibble = (_block[at + c * 4 + i / 2] >> (i % 2 * 4)) & 0xF;
                        int value = ImaStepOnce(ref predictor[c], ref index[c], nibble);
                        int frame = frames + i;
                        if (frame < _samplesPerBlock) _decoded[frame * ch + c] = value / 32768f;
                    }
                frames += 8;
                at += 4 * ch;
            }
            return Math.Min(frames, _samplesPerBlock);
        }

        /// <summary>
        /// Apple's IMA4: per channel, 34 bytes for 64 samples — a two-byte
        /// header (the predictor in the top nine bits, the step index in the
        /// bottom seven), then 32 bytes of nibbles, low nibble first.
        /// </summary>
        private int DecodeIma4()
        {
            int ch = Channels;
            for (int c = 0; c < ch; c++)
            {
                int at = c * 34;
                int header = _block[at] << 8 | _block[at + 1];
                int predictor = (short)(header & 0xFF80);
                int index = Math.Clamp(header & 0x7F, 0, 88);
                for (int i = 0; i < 64; i++)
                {
                    int nibble = (_block[at + 2 + i / 2] >> (i % 2 * 4)) & 0xF;
                    _decoded[i * ch + c] = ImaStepOnce(ref predictor, ref index, nibble) / 32768f;
                }
            }
            return 64;
        }

        // ---------------- Microsoft ADPCM ----------------

        private static readonly int[] MsAdaptation = { 230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230 };

        /// <summary>One MS ADPCM block: per channel a predictor, delta and two samples, then a nibble per sample.</summary>
        private int DecodeMs(int length)
        {
            int ch = Channels;
            var c1 = new int[ch];
            var c2 = new int[ch];
            var delta = new int[ch];
            var s1 = new int[ch];
            var s2 = new int[ch];
            int at = 0;
            for (int c = 0; c < ch; c++)
            {
                int p = Math.Min(_block[at++], _coef1.Length - 1);
                c1[c] = _coef1[p];
                c2[c] = _coef2[p];
            }
            for (int c = 0; c < ch; c++) { delta[c] = BitConverter.ToInt16(_block, at); at += 2; }
            for (int c = 0; c < ch; c++) { s1[c] = BitConverter.ToInt16(_block, at); at += 2; }
            for (int c = 0; c < ch; c++) { s2[c] = BitConverter.ToInt16(_block, at); at += 2; }

            for (int c = 0; c < ch; c++)
            {
                _decoded[c] = s2[c] / 32768f;
                _decoded[ch + c] = s1[c] / 32768f;
            }

            int sample = 2 * ch, most = _samplesPerBlock * ch;
            for (; at < length && sample < most; at++)
                for (int half = 0; half < 2 && sample < most; half++, sample++)
                {
                    int c = sample % ch;
                    int nibble = half == 0 ? _block[at] >> 4 : _block[at] & 0xF;
                    int signed = nibble >= 8 ? nibble - 16 : nibble;
                    int predicted = (s1[c] * c1[c] + s2[c] * c2[c]) >> 8;
                    int value = Math.Clamp(predicted + signed * delta[c], -32768, 32767);
                    s2[c] = s1[c];
                    s1[c] = value;
                    delta[c] = Math.Max(16, MsAdaptation[nibble] * delta[c] >> 8);
                    _decoded[sample] = value / 32768f;
                }
            return sample / ch;
        }

        // ---------------- A-law and mu-law ----------------

        private static readonly short[] ALawTable = Table(a =>
        {
            a ^= 0x55;
            int t = (a & 0x0F) << 4, seg = (a & 0x70) >> 4;
            t = seg switch { 0 => t + 8, 1 => t + 0x108, _ => (t + 0x108) << (seg - 1) };
            return (short)((a & 0x80) != 0 ? t : -t);
        });

        private static readonly short[] MuLawTable = Table(u =>
        {
            u = ~u & 0xFF;
            int t = ((u & 0x0F) << 3) + 0x84;
            t <<= (u & 0x70) >> 4;
            return (short)((u & 0x80) != 0 ? 0x84 - t : t - 0x84);
        });

        private static short[] Table(Func<int, short> decode)
        {
            var table = new short[256];
            for (int i = 0; i < 256; i++) table[i] = decode(i);
            return table;
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
