using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of the decoders.
    /// </summary>
    internal static class Sweep500DecoderTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the decoders:");
            var dir = Path.Combine(Path.GetTempPath(), "en-sweep500-decoders-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                await WithWatchdog("CAF Opus fixed packets", () => CafOpusFixedPacketsAreCountedFromTheFile(dir));
                await WithWatchdog("FLAC in Ogg seeks exactly", () => OggFlacSeeksLandOnTheSample(dir));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static async Task WithWatchdog(string name, Action body)
        {
            var run = Task.Run(body);
            if (await Task.WhenAny(run, Task.Delay(60000)) != run) { Check($"{name} finished within 60s", false); return; }
            try { await run; }
            catch (Exception ex) { Check($"{name} ran without throwing", false, ex.ToString()); }
        }

        // ---- FLAC in Ogg: the rewrapped stream gets the exact seeker ----

        private static void OggPage(Stream into, byte[] packet, uint sequence, int flags)
        {
            var lacing = new System.Collections.Generic.List<byte>();
            int left = packet.Length;
            while (left >= 255) { lacing.Add(255); left -= 255; }
            lacing.Add((byte)left);
            into.Write("OggS"u8);
            into.WriteByte(0);
            into.WriteByte((byte)flags);
            into.Write(BitConverter.GetBytes(0L));
            into.Write(BitConverter.GetBytes(4321));
            into.Write(BitConverter.GetBytes(sequence));
            into.Write(BitConverter.GetBytes(0));
            into.WriteByte((byte)lacing.Count);
            into.Write(lacing.ToArray());
            into.Write(packet);
        }

        private static void OggFlacSeeksLandOnTheSample(string dir)
        {
            const int rate = 44100, seconds = 70;
            var flacPath = Path.Combine(dir, "inner.flac");
            ExactSeekTests.WriteFlac(flacPath, ExactSeekTests.Signal(rate, seconds), rate);   // no seek table
            var flac = File.ReadAllBytes(flacPath);

            // The Ogg FLAC mapping: "fLaC" and STREAMINFO in the first packet, the rest after it.
            var ogg = new MemoryStream();
            uint sequence = 0;
            var first = new MemoryStream();
            first.WriteByte(0x7F); first.Write("FLAC"u8); first.WriteByte(1); first.WriteByte(0);
            first.WriteByte(0); first.WriteByte(0);
            first.Write(flac, 0, 42);
            OggPage(ogg, first.ToArray(), sequence++, 2);
            for (int at = 42; at < flac.Length; at += 4000)
                OggPage(ogg, flac.AsSpan(at, Math.Min(4000, flac.Length - at)).ToArray(), sequence++,
                    at + 4000 >= flac.Length ? 4 : 0);
            var path = Path.Combine(dir, "long.oga");
            File.WriteAllBytes(path, ogg.ToArray());

            var rewrapped = Rewrap.TryRewrap(path, null, out var kind);
            Check("FLAC in Ogg is rewrapped", rewrapped != null, kind);
            if (rewrapped == null) return;
            using (var com = new ManagedIStream(rewrapped))
            {
                var seeker = ExactSeek.For(path, com, out _);
                Check("the rewrapped FLAC gets the exact FLAC seeker", seeker is FlacSeek, seeker?.GetType().Name ?? "none");
                seeker?.Dispose();
            }

            // A straight decode, then a seek on another decoder: the same samples, and the clock agrees.
            float[] whole;
            using (var straight = TrackDecoder.Open(path, null, rate, 2, out string why))
            {
                Check("FLAC in Ogg opens", straight != null, why);
                if (straight == null) return;
                var all = new System.Collections.Generic.List<float>();
                var buffer = new float[8192 * 2];
                int n;
                while ((n = straight.Read(buffer, 8192)) > 0) all.AddRange(buffer.AsSpan(0, n * 2).ToArray());
                whole = all.ToArray();
            }
            foreach (double target in new[] { 37.25, 61.77 })
            {
                using var decoder = TrackDecoder.Open(path, null, rate, 2, out _);
                if (decoder == null) return;
                Check($"a seek to {target}s is taken", decoder.SeekTo(target));
                var got = new float[rate / 2 * 2];
                var scratch = new float[got.Length];
                int have = 0;
                while (have < rate / 2)
                {
                    int r = decoder.Read(scratch, rate / 2 - have);
                    if (r <= 0) break;
                    Array.Copy(scratch, 0, got, have * 2, r * 2);
                    have += r;
                }
                long from = (long)Math.Round(target * rate) * 2;
                double worst = 0;
                for (int i = 0; i < have * 2 && from + i < whole.Length; i++) worst = Math.Max(worst, Math.Abs(whole[from + i] - got[i]));
                Check($"after a seek to {target}s it plays what a straight decode plays there (worst difference {worst:F4})",
                    have == rate / 2 && worst < 1e-4);
                double expected = target + have / (double)rate;
                Check($"and the clock at {target}s agrees ({decoder.Position:F4} against {expected:F4})",
                    Math.Abs(decoder.Position - expected) < 2.0 / rate);
            }
        }

        // ---- CAF Opus with a fixed packet size and a damaged 'data' size ----

        private static byte[] CafWithFixedOpusPackets(long bytesPerPacket, long claimedDataSize, int realPayload)
        {
            var file = new MemoryStream();
            file.Write("caff"u8); file.Write(new byte[] { 0, 1, 0, 0 });
            var desc = new byte[32];
            BinaryPrimitives.WriteInt64BigEndian(desc.AsSpan(0), BitConverter.DoubleToInt64Bits(48000));
            "opus"u8.CopyTo(desc.AsSpan(8));
            BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(12), 0);
            BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(16), (uint)bytesPerPacket);
            BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(20), 960);
            BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(24), 1);
            BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(28), 0);
            WriteChunkHeader(file, "desc", desc.Length);
            file.Write(desc);
            WriteChunkHeader(file, "data", claimedDataSize);
            file.Write(new byte[4]);                       // edit count
            var payload = new byte[realPayload];
            for (int i = 0; i < payload.Length; i++) payload[i] = 0xF8;   // a CELT TOC byte
            file.Write(payload);
            return file.ToArray();
        }

        private static void WriteChunkHeader(Stream s, string id, long size)
        {
            s.Write(System.Text.Encoding.ASCII.GetBytes(id));
            var b = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, size);
            s.Write(b);
        }

        private static void CafOpusFixedPacketsAreCountedFromTheFile(string dir)
        {
            var small = Path.Combine(dir, "fixed packets.caf");
            File.WriteAllBytes(small, CafWithFixedOpusPackets(1, 1L << 24, 50));
            long before = GC.GetAllocatedBytesForCurrentThread();
            using (var caf = new WavDecoder())
            {
                bool opened = caf.Open(small, null);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Check("CAF Opus with fixed packets and an oversized data claim opens", opened, caf.Diagnostic);
                Equal("its length comes from the bytes really in the file (50 packets of 20ms)", "1.00",
                    caf.Duration.ToString("0.00", CultureInfo.InvariantCulture));
                Check($"and opening it allocates little ({allocated / 1048576.0:F1} MB)", allocated < 8L << 20);
            }

            var huge = Path.Combine(dir, "huge packets.caf");
            File.WriteAllBytes(huge, CafWithFixedOpusPackets(0xFFFFFFF0, 1L << 40, 50));
            using (var caf = new WavDecoder())
            {
                bool opened = caf.Open(huge, null);
                Check("CAF Opus with a four-gigabyte packet size is refused", !opened);
                Check("and says the packet size is damaged", (caf.Diagnostic ?? "").Contains("packet size is damaged"), caf.Diagnostic);
            }
        }
    }
}
