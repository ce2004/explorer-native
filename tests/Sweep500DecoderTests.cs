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
