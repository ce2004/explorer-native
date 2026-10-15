using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// What the 3.0.8 sweep of the decoders and the converter found, held:
    /// Apple Lossless in a .caf opens and decodes to the sample (it was refused
    /// with "divide by zero"), and a 24-bit or 64-bit float file that Windows
    /// will not open itself keeps its bit depth when converted (it came out
    /// 16-bit). Fixtures are tones made here, and every run has a 60-second
    /// watchdog.
    /// </summary>
    internal static class Sweep308DecoderTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        private const int Rate = 44100;

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Decoder sweep (3.0.8):");

            var dir = Path.Combine(Path.GetTempPath(), "ExplorerNativeSweep308_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                WithWatchdog("ALAC in CAF decodes", () => AlacInCafTests(dir));
                WithWatchdog("conversion keeps the source's bit depth", () => BitDepthTests(dir));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }

            Console.WriteLine();
        }

        private static void WithWatchdog(string what, Action body)
        {
            var run = Task.Run(body);
            if (!run.Wait(TimeSpan.FromSeconds(60)) && !run.IsCompleted)
            {
                Check($"{what} within 60 seconds", false, "the watchdog fired");
                return;
            }
            if (run.Exception != null)
                Check($"{what} ran to the end", false, run.Exception.InnerException?.ToString() ?? run.Exception.ToString());
        }

        /// <summary>Two tones, one per ear, as interleaved 16-bit samples.</summary>
        private static short[] Tone(int frames)
        {
            var pcm = new short[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                double t = i / (double)Rate;
                pcm[i * 2] = (short)Math.Round(16000 * Math.Sin(2 * Math.PI * 440 * t));
                pcm[i * 2 + 1] = (short)Math.Round(12000 * Math.Sin(2 * Math.PI * 660 * t));
            }
            return pcm;
        }

        // ---------- ALAC in CAF ----------

        private static void AlacInCafTests(string dir)
        {
            const int frameLength = 4096;
            const int total = 3 * frameLength + 1808;     // a short last packet too
            var pcm = Tone(total);
            var path = Path.Combine(dir, "tone.caf");
            WriteAlacCaf(path, pcm, frameLength);

            Check("an ALAC .caf is recognised as a container the managed decoder reads", WavDecoder.IsWav(path, null));

            using var decoder = new WavDecoder();
            bool opened = decoder.Open(path, null);
            Check("an ALAC .caf opens (it was refused: divide by zero)", opened, decoder.Diagnostic);
            if (!opened) return;

            Equal("its rate", Rate.ToString(), decoder.SampleRate.ToString());
            Equal("its channels", "2", decoder.Channels.ToString());
            Equal("its length in frames", total.ToString(), Math.Round(decoder.Duration * Rate).ToString());
            Equal("its bit depth is the cookie's", "16", decoder.NativeBits.ToString());

            var all = ReadAll(decoder, 2);
            Equal("every frame decodes", total.ToString(), (all.Count / 2).ToString());
            int wrong = 0;
            for (int i = 0; i < Math.Min(all.Count, pcm.Length); i++)
                if (all[i] != pcm[i] / 32768f) wrong++;
            Check("and every sample is the one written (not silence)", all.Count == pcm.Length && wrong == 0,
                $"{wrong} samples differ");

            // A seek lands on the sample, packets being independent.
            const int target = frameLength + 1234;
            decoder.SeekTo(target / (double)Rate);
            var after = new float[16 * 2];
            int got = decoder.Read(after, 16);
            bool same = got == 16;
            for (int i = 0; i < got * 2 && same; i++) same = after[i] == pcm[target * 2 + i] / 32768f;
            Check("a seek into the second packet lands on the sample", same);
        }

        private static List<float> ReadAll(ITrackDecoder decoder, int channels)
        {
            var all = new List<float>();
            var block = new float[4096 * channels];
            int empties = 0;
            while (empties < 3)
            {
                int got = decoder.Read(block, 4096);
                if (got <= 0) { empties++; continue; }
                empties = 0;
                for (int i = 0; i < got * channels; i++) all.Add(block[i]);
            }
            return all;
        }

        /// <summary>
        /// A .caf of stereo 16-bit Apple Lossless with every packet stored
        /// uncompressed, which ALAC allows and which needs no encoder.
        /// </summary>
        private static void WriteAlacCaf(string path, short[] pcm, int frameLength)
        {
            int total = pcm.Length / 2;
            var packets = new List<byte[]>();
            for (int first = 0; first < total; first += frameLength)
            {
                int count = Math.Min(frameLength, total - first);
                var bits = new Bits();
                bits.Put(1, 3);                         // channel pair element
                bits.Put(0, 4);                         // instance tag
                bits.Put(0, 12);                        // unused
                bits.Put(count != frameLength ? 1u : 0u, 1);
                bits.Put(0, 2);                         // no shifted bytes
                bits.Put(1, 1);                         // not compressed
                if (count != frameLength) bits.Put((uint)count, 32);
                for (int i = 0; i < count; i++)
                    for (int c = 0; c < 2; c++)
                        bits.Put((ushort)pcm[(first + i) * 2 + c], 16);
                bits.Put(7, 3);                         // end
                packets.Add(bits.ToArray());
            }

            int maxPacket = 0;
            foreach (var p in packets) maxPacket = Math.Max(maxPacket, p.Length);

            using var file = new MemoryStream();
            file.Write("caff"u8);
            file.Write(BE16(1));
            file.Write(BE16(0));

            // desc
            var desc = new MemoryStream();
            desc.Write(BE64(BitConverter.DoubleToInt64Bits(Rate)));
            desc.Write("alac"u8);
            desc.Write(BE32(1));                        // 16-bit source
            desc.Write(BE32(0));                        // bytes per packet: they vary
            desc.Write(BE32((uint)frameLength));
            desc.Write(BE32(2));
            desc.Write(BE32(0));
            Chunk(file, "desc", desc.ToArray());

            // kuki: the bare 24-byte ALACSpecificConfig
            var kuki = new MemoryStream();
            kuki.Write(BE32((uint)frameLength));
            kuki.Write(new byte[] { 0, 16, 40, 10, 14, 2 });
            kuki.Write(BE16(255));
            kuki.Write(BE32((uint)maxPacket));
            kuki.Write(BE32(0));
            kuki.Write(BE32(Rate));
            Chunk(file, "kuki", kuki.ToArray());

            // pakt: the packet count, the frames, priming, remainder, then each size
            var pakt = new MemoryStream();
            pakt.Write(BE64(packets.Count));
            pakt.Write(BE64(total));
            pakt.Write(BE32(0));
            pakt.Write(BE32((uint)(packets.Count * frameLength - total)));
            foreach (var p in packets) Varint(pakt, p.Length);
            Chunk(file, "pakt", pakt.ToArray());

            // data: an edit count, then the packets
            var data = new MemoryStream();
            data.Write(BE32(0));
            foreach (var p in packets) data.Write(p);
            Chunk(file, "data", data.ToArray());

            File.WriteAllBytes(path, file.ToArray());
        }

        private static void Chunk(Stream into, string id, byte[] body)
        {
            into.Write(Encoding.ASCII.GetBytes(id));
            into.Write(BE64(body.Length));
            into.Write(body);
        }

        private static void Varint(Stream into, long value)
        {
            var groups = new List<byte>();
            do { groups.Insert(0, (byte)(value & 0x7F)); value >>= 7; } while (value > 0);
            for (int i = 0; i < groups.Count - 1; i++) groups[i] |= 0x80;
            into.Write(groups.ToArray());
        }

        private static byte[] BE16(int v) => new[] { (byte)(v >> 8), (byte)v };
        private static byte[] BE32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        private static byte[] BE32(int v) => BE32((uint)v);
        private static byte[] BE64(long v)
        {
            var b = new byte[8];
            for (int i = 0; i < 8; i++) b[i] = (byte)(v >> (56 - 8 * i));
            return b;
        }

        /// <summary>Most significant bit first, as ALAC is written.</summary>
        private sealed class Bits
        {
            private readonly List<byte> _bytes = new();
            private ulong _acc;
            private int _count;

            public void Put(uint value, int bits)
            {
                for (int i = bits - 1; i >= 0; i--)
                {
                    _acc = (_acc << 1) | ((value >> i) & 1);
                    if (++_count == 8) { _bytes.Add((byte)_acc); _acc = 0; _count = 0; }
                }
            }

            public byte[] ToArray()
            {
                if (_count > 0) { _bytes.Add((byte)(_acc << (8 - _count))); _acc = 0; _count = 0; }
                return _bytes.ToArray();
            }
        }

        // ---------- bit depth through the converter ----------

        private static void BitDepthTests(string dir)
        {
            const int frames = Rate * 2;
            var pcm = Tone(frames);

            // 24-bit big-endian PCM in a .caf, which Windows does not open.
            var caf = Path.Combine(dir, "deep.caf");
            WriteLpcmCaf24(caf, pcm);
            ConvertAndCheck(caf, "a 24-bit .caf", expectedBits: 24, expectedFormat: 1);

            // 64-bit float WAV, which Windows refuses: kept as float, 32-bit.
            var wav64 = Path.Combine(dir, "double.wav");
            WriteFloat64Wav(wav64, pcm);
            ConvertAndCheck(wav64, "a 64-bit float WAV", expectedBits: 32, expectedFormat: 3);
        }

        private static void ConvertAndCheck(string source, string what, int expectedBits, int expectedFormat)
        {
            var decoder = TrackDecoder.Open(source, null, 0, 0, out string why);
            Check($"{what} opens", decoder != null, why);
            if (decoder == null) return;
            using (decoder)
                Check($"{what}: the decoder says how deep it is ({decoder.GetType().Name})",
                    AudioConverter.NativeBitsOf(decoder) >= expectedBits,
                    AudioConverter.NativeBitsOf(decoder).ToString());

            var choice = AudioFormats.ById("wav-keep");
            var target = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + "-out.wav");
            var notes = new List<string>();
            string written;
            try
            {
                written = AudioConverter.ConvertFile(new ConvertJob(source, target, new FileInfo(source).Length, false),
                    choice, null, CancellationToken.None, notes);
            }
            catch (Exception ex)
            {
                Check($"{what} converts to WAV keeping its depth", false, ex.Message);
                return;
            }

            var head = new byte[36];
            using (var file = File.OpenRead(written)) file.ReadExactly(head, 0, head.Length);
            int format = BitConverter.ToInt16(head, 20);
            int bits = BitConverter.ToInt16(head, 34);
            Equal($"{what} converted to WAV keeps its bit depth (it came out 16-bit)", expectedBits.ToString(), bits.ToString());
            Equal($"{what} converted to WAV keeps its sample format", expectedFormat.ToString(), format.ToString());

            var back = TrackDecoder.Open(written, null, 0, 0, out why);
            Check($"{what}: the WAV written plays", back != null, why);
            if (back == null) return;
            using (back)
            {
                var all = ReadAll(back, Math.Max(1, back.Channels));
                float peak = 0;
                foreach (var v in all) peak = Math.Max(peak, Math.Abs(v));
                Check($"{what}: and it is the tone, not silence (peak {peak:0.###})", peak > 0.4f && peak < 0.6f);
            }
        }

        private static void WriteLpcmCaf24(string path, short[] pcm)
        {
            using var file = new MemoryStream();
            file.Write("caff"u8);
            file.Write(BE16(1));
            file.Write(BE16(0));

            var desc = new MemoryStream();
            desc.Write(BE64(BitConverter.DoubleToInt64Bits(Rate)));
            desc.Write("lpcm"u8);
            desc.Write(BE32(0));                        // integer, big-endian
            desc.Write(BE32(6));                        // bytes per packet
            desc.Write(BE32(1));                        // frames per packet
            desc.Write(BE32(2));
            desc.Write(BE32(24));
            Chunk(file, "desc", desc.ToArray());

            var data = new MemoryStream();
            data.Write(BE32(0));
            foreach (var s in pcm)
            {
                // The 16-bit tone with eight more bits below it, so 24 bits are really used.
                int v = s * 256 + 0x5A;
                data.WriteByte((byte)(v >> 16));
                data.WriteByte((byte)(v >> 8));
                data.WriteByte((byte)v);
            }
            Chunk(file, "data", data.ToArray());

            File.WriteAllBytes(path, file.ToArray());
        }

        private static void WriteFloat64Wav(string path, short[] pcm)
        {
            using var file = new MemoryStream();
            using var w = new BinaryWriter(file);
            int blockAlign = 2 * 8;
            w.Write("RIFF"u8);
            w.Write(36 + pcm.Length * 8);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)3);
            w.Write((short)2);
            w.Write(Rate);
            w.Write(Rate * blockAlign);
            w.Write((short)blockAlign);
            w.Write((short)64);
            w.Write("data"u8);
            w.Write(pcm.Length * 8);
            foreach (var s in pcm) w.Write(s / 32768.0);
            w.Flush();
            File.WriteAllBytes(path, file.ToArray());
        }
    }
}
