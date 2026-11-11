using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Ogg Opus through Concentus, on every core (6.1.0, Conner: "get this as
    /// fast as it'll go"; one 20 GB recording used to crawl along on one).
    ///
    /// The audio is cut into sections of <see cref="SectionPackets"/> packets.
    /// Each section is encoded on its own thread by its own encoder, and the
    /// packets are written into one Ogg stream in order, by our own page
    /// writer. An encoder starting cold in the middle of a recording would
    /// make a click at the join, so each one is first run over the
    /// <see cref="RollPackets"/> packets before its section and those packets
    /// thrown away: by the join it is in the state one long encoder would be
    /// in. Sections start on packet edges, so every packet covers exactly the
    /// audio it would have covered encoded in one go.
    ///
    /// Always 48k; the tags go into the OpusTags header as Vorbis comments.
    /// </summary>
    internal sealed class OpusFileEncoder : IAudioEncoder
    {
        /// <summary>Frames in a packet: 20 ms at 48k.</summary>
        private const int Frame = 960;

        /// <summary>
        /// Packets in a section: thirty seconds. Longer sections spend less of
        /// their time warming up (half a second in thirty, where it was one in ten).
        /// </summary>
        internal static int SectionPackets = 1500;

        /// <summary>Packets an encoder is warmed up on before its section: half a second.</summary>
        internal const int RollPackets = 25;

        /// <summary>
        /// How hard the encoder works, 0 to 10. Ten below 192 kilobits, where
        /// the extra care can be heard; five from there up, where it cannot and
        /// costs a third of the time (measured on a real recording at 320:
        /// 111 times real time a core at ten, 164 at five).
        /// </summary>
        internal static int ComplexityFor(int bitrate) => bitrate >= 192000 ? 5 : 10;

        /// <summary>Packets on an Ogg page at most: a page a second, for seeking.</summary>
        private const int PagePackets = 50;

        /// <summary>Sections being encoded at once across every file: one a core.</summary>
        private static readonly SemaphoreSlim Cores = new(Math.Max(1, Environment.ProcessorCount));

        private readonly Stream _file;
        private readonly bool _owns;
        private readonly int _channels, _bitrate, _preSkip, _ahead;
        private readonly Queue<Task<List<byte[]>>> _pending = new();

        /// <summary>
        /// Sections are rented and handed back: at ten seconds each they are big
        /// enough to sit on the large object heap, and a long recording made
        /// thousands (measured: over 4 GB in use on a 66 hour file).
        /// </summary>
        private static readonly System.Buffers.ArrayPool<float> Pool = System.Buffers.ArrayPool<float>.Shared;

        private readonly int _capacity;         // frames in a full section
        private float[] _section;
        private int _filled;                    // frames in _section
        private float[] _roll = Array.Empty<float>();
        private int _rollFrames;
        private long _totalFrames;

        private readonly uint _serial = (uint)Random.Shared.Next(1, int.MaxValue);
        private uint _pageNumber;
        private long _granule;
        private readonly List<byte[]> _page = new();
        private int _pageSegments;

        public bool TagsAfterwards => false;

        public OpusFileEncoder(string path, int channels, int kbps, AudioTagSet? tags, Stream? output = null)
        {
            _channels = channels;
            // Never more than the encoder takes for this many channels.
            _bitrate = Math.Min(kbps, channels == 1 ? 300 : AudioFormats.MostOpusKbps) * 1000;
            _capacity = SectionPackets * Frame;
            _section = Pool.Rent(_capacity * channels);
            _ahead = Math.Max(2, Environment.ProcessorCount + 1);

            _file = output ?? new FileStream(NameRules.ExactPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);
            _owns = output == null;

            try
            {
                using (var probe = NewEncoder()) _preSkip = probe.Lookahead;

                var head = new byte[19];
                "OpusHead"u8.CopyTo(head);
                head[8] = 1;
                head[9] = (byte)channels;
                BitConverter.TryWriteBytes(head.AsSpan(10), (ushort)_preSkip);
                BitConverter.TryWriteBytes(head.AsSpan(12), 48000u);
                WriteWholePacket(head, 0x02);

                using var comments = new MemoryStream();
                comments.Write("OpusTags"u8);
                var vendor = Encoding.UTF8.GetBytes("Explorer Native");
                comments.Write(BitConverter.GetBytes(vendor.Length));
                comments.Write(vendor);
                var fields = new List<byte[]>();
                if (tags != null)
                    foreach (var (key, value) in tags.VorbisFields())
                        fields.Add(Encoding.UTF8.GetBytes($"{key}={value}"));
                comments.Write(BitConverter.GetBytes(fields.Count));
                foreach (var field in fields)
                {
                    comments.Write(BitConverter.GetBytes(field.Length));
                    comments.Write(field);
                }
                WriteWholePacket(comments.ToArray(), 0);
            }
            catch
            {
                if (_owns) _file.Dispose();
                throw;
            }
        }

        /// <summary>The real libopus when it loads, the managed port otherwise: the same packets either way, a third of the time.</summary>
        private IOpusSection NewEncoder() =>
            NativeOpus.Available && !ManagedOnly
                ? new NativeOpus(_channels, _bitrate)
                : new ManagedOpus(_channels, _bitrate);

        /// <summary>For the suite: both encoders are held to the same tests.</summary>
        internal static bool ManagedOnly;

        public void Write(float[] interleaved, int frames)
        {
            int at = 0;
            int capacity = _capacity;
            while (at < frames)
            {
                int take = Math.Min(frames - at, capacity - _filled);
                Array.Copy(interleaved, at * _channels, _section, _filled * _channels, take * _channels);
                _filled += take;
                at += take;
                _totalFrames += take;
                if (_filled == capacity) Submit(last: false);
            }
        }

        /// <summary>Hands the section to a core, and writes whatever sections before it are ready.</summary>
        private void Submit(bool last)
        {
            int frames = _filled;
            if (last)
            {
                // To the end of a packet, and far enough past the audio for the
                // encoder's own delay to come out the other side.
                long whole = (_totalFrames + _preSkip + Frame - 1) / Frame * Frame;
                long before = _totalFrames - _filled;
                frames = (int)Math.Max(0, whole - before);
                if (frames * _channels > _section.Length)
                {
                    var bigger = Pool.Rent(frames * _channels);
                    Array.Copy(_section, bigger, _filled * _channels);
                    Pool.Return(_section);
                    _section = bigger;
                }
                Array.Clear(_section, _filled * _channels, (frames - _filled) * _channels);
            }

            var section = _section;
            var roll = _roll;
            int rolls = _rollFrames / Frame;
            int channels = _channels;

            if (!last)
            {
                // The next section warms up on the end of this one.
                int keep = Math.Min(RollPackets * Frame, frames);
                _roll = Pool.Rent(keep * channels);
                _rollFrames = keep;
                Array.Copy(section, (frames - keep) * channels, _roll, 0, keep * channels);
                _section = Pool.Rent(_capacity * channels);
            }
            _filled = 0;
            if (last) _section = Array.Empty<float>();
            if (frames == 0) { if (section.Length > 0) Pool.Return(section); return; }

            _pending.Enqueue(ArchiveWorkers.RunLong(() =>
            {
                Cores.Wait();
                try
                {
                    using var encoder = NewEncoder();
                    var packet = new byte[1500];
                    // The packet either side of a join leans on nothing before it
                    // (no prediction from the last packet's levels, no pitch
                    // filter carried over): what the player holds after the
                    // section before is then exactly what this encoder assumed.
                    // Measured without it: the 20 ms at a join was ten times
                    // further from the original than anywhere else.
                    for (int i = 0; i < rolls; i++)
                    {
                        encoder.PredictionDisabled = i == rolls - 1;
                        encoder.Encode(roll.AsSpan(i * Frame * channels, Frame * channels), Frame, packet);
                    }

                    int count = frames / Frame;
                    var packets = new List<byte[]>(count);
                    for (int i = 0; i < count; i++)
                    {
                        encoder.PredictionDisabled = (i == 0 && rolls > 0) || (i == count - 1 && !last);
                        int n = encoder.Encode(section.AsSpan(i * Frame * channels, Frame * channels), Frame, packet);
                        packets.Add(packet.AsSpan(0, n).ToArray());
                    }
                    return packets;
                }
                finally
                {
                    Cores.Release();
                    Pool.Return(section);
                    if (roll.Length > 0) Pool.Return(roll);
                }
            }));

            while (_pending.Count > _ahead) WriteNext();
        }

        private void WriteNext()
        {
            List<byte[]> packets;
            try { packets = _pending.Dequeue().GetAwaiter().GetResult(); }
            catch (Exception ex) { throw new AudioEncodeException("the Opus encoder stopped: " + ex.Message); }

            foreach (var packet in packets)
            {
                // Flushed before a packet is added, never after: the last page
                // always has the last packet, and carries the true length.
                int segments = packet.Length / 255 + 1;
                if (_page.Count >= PagePackets || _pageSegments + segments > 255) FlushPage(0, _granule);
                _page.Add(packet);
                _pageSegments += segments;
                _granule += Frame;
            }
        }

        public void Finish()
        {
            Submit(last: true);
            while (_pending.Count > 0) WriteNext();
            if (_page.Count > 0) FlushPage(0x04, Math.Min(_granule, _preSkip + _totalFrames));
            AudioEncoders.FlushToDisk(_file);
        }

        public void Dispose()
        {
            if (_owns) try { _file.Dispose(); } catch { }
        }

        // ------------------------------------------------------------ Ogg

        private void FlushPage(byte flags, long granule)
        {
            int body = 0;
            foreach (var packet in _page) body += packet.Length;
            var lacing = new List<byte>(_pageSegments);
            foreach (var packet in _page)
            {
                int left = packet.Length;
                while (left >= 255) { lacing.Add(255); left -= 255; }
                lacing.Add((byte)left);
            }

            var page = new byte[27 + lacing.Count + body];
            int at = 27 + lacing.Count;
            lacing.CopyTo(page, 27);
            foreach (var packet in _page)
            {
                packet.CopyTo(page, at);
                at += packet.Length;
            }
            WritePage(page, lacing.Count, flags, granule);
            _page.Clear();
            _pageSegments = 0;
        }

        /// <summary>A header packet on pages of its own, carried over pages when it is a big one.</summary>
        private void WriteWholePacket(byte[] packet, byte firstFlags)
        {
            int at = 0;
            bool first = true, done = false;
            while (!done)
            {
                var lacing = new List<byte>();
                int start = at;
                while (lacing.Count < 255)
                {
                    int left = packet.Length - at;
                    if (left >= 255) { lacing.Add(255); at += 255; }
                    else { lacing.Add((byte)left); at += left; done = true; break; }
                }

                var page = new byte[27 + lacing.Count + (at - start)];
                lacing.CopyTo(page, 27);
                Array.Copy(packet, start, page, 27 + lacing.Count, at - start);
                // 0x01: this page goes on with a packet begun on the one before.
                WritePage(page, lacing.Count, (byte)(first ? firstFlags : 0x01), done ? 0 : -1);
                first = false;
            }
        }

        private void WritePage(byte[] page, int segments, byte flags, long granule)
        {
            "OggS"u8.CopyTo(page);
            page[5] = flags;
            BitConverter.TryWriteBytes(page.AsSpan(6), granule);
            BitConverter.TryWriteBytes(page.AsSpan(14), _serial);
            BitConverter.TryWriteBytes(page.AsSpan(18), _pageNumber++);
            page[26] = (byte)segments;
            BitConverter.TryWriteBytes(page.AsSpan(22), OggCrc(page));
            _file.Write(page, 0, page.Length);
        }

        private static readonly uint[] CrcTable = MakeCrcTable();

        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint r = i << 24;
                for (int bit = 0; bit < 8; bit++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
                table[i] = r;
            }
            return table;
        }

        /// <summary>Ogg's checksum: CRC-32 with no reflection, from zero, over the page with its own field zero.</summary>
        private static uint OggCrc(byte[] page)
        {
            uint crc = 0;
            foreach (byte b in page) crc = (crc << 8) ^ CrcTable[((crc >> 24) ^ b) & 0xFF];
            return crc;
        }
    }

    /// <summary>One Opus encoder, for one section of a file.</summary>
    internal interface IOpusSection : IDisposable
    {
        int Lookahead { get; }
        bool PredictionDisabled { set; }
        int Encode(ReadOnlySpan<float> interleaved, int frames, byte[] packet);
    }

    internal sealed class ManagedOpus : IOpusSection
    {
        private readonly Concentus.IOpusEncoder _encoder;

        public ManagedOpus(int channels, int bitrate)
        {
            _encoder = Concentus.OpusCodecFactory.CreateEncoder(
                48000, channels, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO, null);
            _encoder.Bitrate = bitrate;
            _encoder.Complexity = OpusFileEncoder.ComplexityFor(bitrate);
        }

        public int Lookahead => _encoder.Lookahead;
        public bool PredictionDisabled { set => _encoder.PredictionDisabled = value; }
        public int Encode(ReadOnlySpan<float> interleaved, int frames, byte[] packet) =>
            _encoder.Encode(interleaved, frames, packet, packet.Length);
        public void Dispose() { }
    }

    /// <summary>
    /// libopus itself (native\win-x64 and win-arm64\opus.dll, 1.6.1, built in
    /// Documents\code\TTCode\opus with a static runtime). Loaded once, by the
    /// same route as the NVDA client: beside the runtime's own native
    /// libraries, where a single-file build unpacks it.
    /// </summary>
    internal sealed unsafe class NativeOpus : IOpusSection
    {
        private const int Audio = 2049;
        private const int SetBitrate = 4002, SetComplexity = 4010, GetLookahead = 4027, SetPredictionDisabled = 4042;

        private static readonly delegate* unmanaged[Cdecl]<int, int, int, int*, IntPtr> _create;
        private static readonly delegate* unmanaged[Cdecl]<IntPtr, float*, int, byte*, int, int> _encode;
        private static readonly delegate* unmanaged[Cdecl]<IntPtr, int, int, int> _set;
        private static readonly delegate* unmanaged[Cdecl]<IntPtr, int, int*, int> _get;
        private static readonly delegate* unmanaged[Cdecl]<IntPtr, void> _destroy;

        /// <summary>Whether the library loaded and has what is used.</summary>
        public static bool Available { get; }

        static NativeOpus()
        {
            try
            {
                if (!System.Runtime.InteropServices.NativeLibrary.TryLoad("opus.dll", typeof(NativeOpus).Assembly, null, out IntPtr lib) &&
                    !System.Runtime.InteropServices.NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "opus.dll"), out lib))
                    return;

                IntPtr Export(string name) => System.Runtime.InteropServices.NativeLibrary.GetExport(lib, name);
                _create = (delegate* unmanaged[Cdecl]<int, int, int, int*, IntPtr>)Export("opus_encoder_create");
                _encode = (delegate* unmanaged[Cdecl]<IntPtr, float*, int, byte*, int, int>)Export("opus_encode_float");
                // Variadic in C; an int or a pointer after the request goes the same way on x64 and ARM64.
                _set = (delegate* unmanaged[Cdecl]<IntPtr, int, int, int>)Export("opus_encoder_ctl");
                _get = (delegate* unmanaged[Cdecl]<IntPtr, int, int*, int>)Export("opus_encoder_ctl");
                _destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("opus_encoder_destroy");

                // Proved once: an encoder is made, takes its settings and encodes a packet.
                using var probe = new NativeOpus(2, 96000);
                var silence = new float[960 * 2];
                Available = probe.Lookahead > 0 && probe.Encode(silence, 960, new byte[1500]) > 0;
            }
            catch { Available = false; }
        }

        private IntPtr _encoder;

        /// <summary>Packets libopus has encoded this run, so a test can tell which encoder did the work.</summary>
        internal static long Packets;

        public NativeOpus(int channels, int bitrate)
        {
            int error = 0;
            _encoder = _create(48000, channels, Audio, &error);
            if (_encoder == IntPtr.Zero || error != 0) throw new AudioEncodeException($"the Opus encoder would not start ({error})");
            _set(_encoder, SetBitrate, bitrate);
            _set(_encoder, SetComplexity, OpusFileEncoder.ComplexityFor(bitrate));
        }

        public int Lookahead
        {
            get
            {
                int value = 0;
                _get(_encoder, GetLookahead, &value);
                return value;
            }
        }

        public bool PredictionDisabled { set => _set(_encoder, SetPredictionDisabled, value ? 1 : 0); }

        public int Encode(ReadOnlySpan<float> interleaved, int frames, byte[] packet)
        {
            fixed (float* pcm = interleaved)
            fixed (byte* into = packet)
            {
                int n = _encode(_encoder, pcm, frames, into, packet.Length);
                System.Threading.Interlocked.Increment(ref Packets);
                if (n < 0) throw new AudioEncodeException($"the Opus encoder stopped ({n})");
                return n;
            }
        }

        public void Dispose()
        {
            var encoder = System.Threading.Interlocked.Exchange(ref _encoder, IntPtr.Zero);
            if (encoder != IntPtr.Zero) _destroy(encoder);
        }
    }
}
