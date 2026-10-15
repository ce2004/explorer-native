using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ExplorerNative
{
    /// <summary>
    /// Sample-for-sample comparison of the managed ALAC and TTA decoders with
    /// ffmpeg. Not part of the suite, because it needs ffmpeg's output on disk:
    /// EXPLORERNATIVE_CODECDIR names a folder of inputs (.m4a, .caf, .tta),
    /// each beside a .s32 file of ffmpeg's decode as raw signed 32-bit
    /// little-endian (ffmpeg -i x -f s32le -c:a pcm_s32le x.s32).
    /// </summary>
    internal static class CodecProbe
    {
        private static int _failed;

        private static void Say(string what, bool ok, string detail = "")
        {
            if (!ok) _failed++;
            Console.WriteLine((ok ? "PASS " : "FAIL ") + what + (detail.Length > 0 ? " - " + detail : ""));
        }

        private static string Folder() =>
            Environment.GetEnvironmentVariable("EXPLORERNATIVE_CODECDIR") ?? Path.Combine(Path.GetTempPath(), "codecprobe");

        private static int[] Reference(string input)
        {
            var bytes = File.ReadAllBytes(Path.ChangeExtension(input, ".s32"));
            var r = new int[bytes.Length / 4];
            for (int i = 0; i < r.Length; i++) r[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4));
            return r;
        }

        // ---- ALAC ----

        public static int RunAlac()
        {
            foreach (var path in Directory.GetFiles(Folder()))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is not (".m4a" or ".caf")) continue;
                string name = Path.GetFileName(path);
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    var (cookie, packets) = ext == ".m4a" ? FromMp4(bytes) : FromCaf(bytes);
                    var alac = AlacDecoder.FromCookie(cookie, out var why);
                    if (alac == null) { Say(name + " cookie", false, why); continue; }

                    var expected = Reference(path);
                    var ints = new int[alac.FrameLength * alac.Channels];
                    var floats = new float[alac.FrameLength * alac.Channels];
                    int shift = 32 - alac.BitDepth;
                    long at = 0, mismatches = 0, firstBad = -1, zeroPackets = 0;
                    bool floatsAgree = true;
                    foreach (var (offset, size) in packets)
                    {
                        var packet = new byte[size];
                        Array.Copy(bytes, offset, packet, 0, size);
                        int frames = alac.DecodeSamples(packet, size, ints);
                        if (frames == 0) zeroPackets++;
                        int fframes = alac.Decode(packet, size, floats);
                        if (fframes != frames) floatsAgree = false;
                        for (int i = 0; i < frames * alac.Channels; i++)
                        {
                            if (alac.BitDepth <= 24 && floats[i] * (float)(1L << (alac.BitDepth - 1)) != ints[i]) floatsAgree = false;
                            long o = at + i;
                            if (o >= expected.Length || (ints[i] << shift) != expected[o])
                            {
                                if (firstBad < 0) firstBad = o;
                                mismatches++;
                            }
                        }
                        at += frames * alac.Channels;
                    }
                    Say($"{name}: {alac.BitDepth}-bit, {alac.Channels} ch, {alac.SampleRate} Hz, frame {alac.FrameLength}, {packets.Count} packets",
                        mismatches == 0 && at == expected.Length && zeroPackets == 0 && floatsAgree,
                        $"decoded {at} samples, ffmpeg {expected.Length}, mismatches {mismatches}, first at {firstBad}, failed packets {zeroPackets}, float path agrees {floatsAgree}");

                    // A damaged packet answers 0 or some frames, never throws.
                    var rng = new Random(1);
                    bool calm = true;
                    for (int t = 0; t < 200 && packets.Count > 0; t++)
                    {
                        var (offset, size) = packets[rng.Next(packets.Count)];
                        var packet = new byte[size];
                        Array.Copy(bytes, offset, packet, 0, size);
                        for (int f = 0; f < 4; f++) packet[rng.Next(size)] ^= (byte)rng.Next(1, 256);
                        try { alac.Decode(packet, rng.Next(1, size + 1), floats); }
                        catch { calm = false; }
                    }
                    Say(name + ": damaged packets decode without throwing", calm);
                }
                catch (Exception ex) { Say(name, false, ex.ToString()); }
            }

            // Cookie shapes: bare, wrapped in an 'alac' atom, behind 'frma'.
            var config = new byte[24];
            BinaryPrimitives.WriteUInt32BigEndian(config, 4096);
            config[5] = 16; config[6] = 40; config[7] = 10; config[8] = 14; config[9] = 2;
            BinaryPrimitives.WriteUInt32BigEndian(config.AsSpan(20), 44100);
            var atom = new byte[36];
            BinaryPrimitives.WriteUInt32BigEndian(atom, 36);
            Encoding.ASCII.GetBytes("alac").CopyTo(atom, 4);
            config.CopyTo(atom, 12);
            var frma = new byte[12 + 36 + 24];
            BinaryPrimitives.WriteUInt32BigEndian(frma, 12);
            Encoding.ASCII.GetBytes("frmaalac").CopyTo(frma, 4);
            atom.CopyTo(frma, 12);
            foreach (var (label, cookie) in new[] { ("bare", config), ("alac atom", atom), ("frma + alac + layout", frma) })
            {
                var d = AlacDecoder.FromCookie(cookie, out var why);
                Say("cookie " + label, d != null && d.Channels == 2 && d.BitDepth == 16 && d.SampleRate == 44100 && d.FrameLength == 4096, why);
            }
            Say("cookie of zeros refused", AlacDecoder.FromCookie(new byte[24], out _) == null);

            Console.WriteLine($"\n{_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        /// <summary>The cookie and every packet's place, from the first ALAC track's sample table.</summary>
        private static (byte[] cookie, List<(long, int)> packets) FromMp4(byte[] b)
        {
            byte[]? cookie = null;
            var sizes = new List<int>();
            var chunks = new List<long>();
            var stsc = new List<(int first, int count)>();

            void Walk(long start, long end)
            {
                long at = start;
                while (at + 8 <= end)
                {
                    long size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)at));
                    string type = Encoding.ASCII.GetString(b, (int)at + 4, 4);
                    int header = 8;
                    if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan((int)at + 8)); header = 16; }
                    else if (size == 0) size = end - at;
                    long body = at + header;
                    switch (type)
                    {
                        case "moov" or "trak" or "mdia" or "minf" or "stbl":
                            if (cookie == null || sizes.Count == 0) Walk(body, at + size);
                            break;
                        case "stsd" when cookie == null:
                        {
                            long entry = body + 8;
                            string format = Encoding.ASCII.GetString(b, (int)entry + 4, 4);
                            int entrySize = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)entry));
                            if (format == "alac") cookie = b.AsSpan((int)entry + 36, entrySize - 36).ToArray();
                            break;
                        }
                        case "stsz" when cookie != null && sizes.Count == 0:
                        {
                            int fixedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)body + 4));
                            int count = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)body + 8));
                            for (int i = 0; i < count; i++)
                                sizes.Add(fixedSize != 0 ? fixedSize : (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)body + 12 + i * 4)));
                            break;
                        }
                        case "stco" when cookie != null && chunks.Count == 0:
                        case "co64" when cookie != null && chunks.Count == 0:
                        {
                            int count = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)body + 4));
                            for (int i = 0; i < count; i++)
                                chunks.Add(type == "stco"
                                    ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)body + 8 + i * 4))
                                    : (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan((int)body + 8 + i * 8)));
                            break;
                        }
                        case "stsc" when cookie != null && stsc.Count == 0:
                        {
                            int count = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)body + 4));
                            for (int i = 0; i < count; i++)
                                stsc.Add(((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)body + 8 + i * 12)),
                                          (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)body + 12 + i * 12))));
                            break;
                        }
                    }
                    at += size;
                }
            }

            Walk(0, b.Length);
            if (cookie == null) throw new InvalidDataException("no ALAC track");

            var packets = new List<(long, int)>();
            int sample = 0;
            for (int chunk = 0; chunk < chunks.Count && sample < sizes.Count; chunk++)
            {
                int perChunk = 0;
                foreach (var (first, count) in stsc) if (first - 1 <= chunk) perChunk = count;
                long at = chunks[chunk];
                for (int i = 0; i < perChunk && sample < sizes.Count; i++, sample++)
                {
                    packets.Add((at, sizes[sample]));
                    at += sizes[sample];
                }
            }
            return (cookie, packets);
        }

        /// <summary>The cookie from 'kuki' and every packet from 'pakt' over 'data'.</summary>
        private static (byte[] cookie, List<(long, int)> packets) FromCaf(byte[] b)
        {
            byte[]? cookie = null;
            var sizes = new List<int>();
            long data = -1;
            long at = 8;
            while (at + 12 <= b.Length)
            {
                string type = Encoding.ASCII.GetString(b, (int)at, 4);
                long size = BinaryPrimitives.ReadInt64BigEndian(b.AsSpan((int)at + 4));
                long body = at + 12;
                if (size < 0) size = b.Length - body;
                if (type == "kuki") cookie = b.AsSpan((int)body, (int)size).ToArray();
                else if (type == "data") data = body + 4;
                else if (type == "pakt")
                {
                    long count = BinaryPrimitives.ReadInt64BigEndian(b.AsSpan((int)body));
                    int p = (int)body + 24;
                    for (long i = 0; i < count; i++)
                    {
                        int v = 0;
                        byte x;
                        do { x = b[p++]; v = (v << 7) | (x & 0x7F); } while ((x & 0x80) != 0);
                        sizes.Add(v);
                    }
                }
                at = body + size;
            }
            if (cookie == null || data < 0) throw new InvalidDataException("no kuki or data chunk");
            var packets = new List<(long, int)>();
            foreach (var s in sizes) { packets.Add((data, s)); data += s; }
            return (cookie, packets);
        }

        // ---- TTA ----

        public static int RunTta()
        {
            foreach (var path in Directory.GetFiles(Folder(), "*.tta"))
            {
                string name = Path.GetFileName(path);
                try
                {
                    var expected = Reference(path);
                    Say(name + ": IsTta", TtaDecoder.IsTta(path, null));

                    using var tta = new TtaDecoder();
                    if (!tta.Open(path, null)) { Say(name + " open", false, tta.Diagnostic); continue; }
                    int ch = tta.Channels, rate = tta.SampleRate;

                    var all = ReadAll(tta);
                    long mismatches = 0, firstBad = -1;
                    for (int i = 0; i < Math.Min(all.Count, expected.Length); i++)
                        if (ToS32(all[i]) != expected[i]) { if (firstBad < 0) firstBad = i; mismatches++; }
                    Say($"{name}: {ch} ch, {rate} Hz, {tta.Duration:0.000}s", mismatches == 0 && all.Count == expected.Length,
                        $"decoded {all.Count} samples, ffmpeg {expected.Length}, mismatches {mismatches}, first at {firstBad}");
                    Say(name + ": duration matches", Math.Abs(tta.Duration * rate - expected.Length / ch) < 0.5,
                        $"{tta.Duration * rate} vs {expected.Length / ch}");

                    // Seeks land on the exact sample, across frame boundaries and at the end.
                    long total = expected.Length / ch;
                    int frameLength = 256 * rate / 245;
                    var targets = new List<long> { 0, 1, frameLength - 1, frameLength, frameLength + 7, total / 2, total - 3, total };
                    var rng = new Random(7);
                    for (int t = 0; t < 6; t++) targets.Add(rng.NextInt64(total));
                    var buffer = new float[3000 * ch];
                    foreach (var target in targets)
                    {
                        bool sought = tta.SeekTo(target / (double)rate);
                        int got = tta.Read(buffer, 3000);
                        long want = Math.Min(3000, total - target);
                        bool same = sought && got == want && Math.Abs(tta.Position * rate - (target + got)) < 0.5;
                        for (int i = 0; same && i < got * ch; i++) same = ToS32(buffer[i]) == expected[target * ch + i];
                        Say($"{name}: seek to sample {target}", same, $"read {got}, wanted {want}");
                    }

                    // Through a COM IStream, as the player reads a file it downloaded.
                    using (var stream = LocalByteStream.TryOpen(path))
                    {
                        using var viaCom = new TtaDecoder();
                        bool opened = stream != null && viaCom.Open(path, stream);
                        var com = opened ? ReadAll(viaCom) : new List<float>();
                        bool same = opened && com.Count == all.Count;
                        for (int i = 0; same && i < com.Count; i++) same = com[i] == all[i];
                        Say(name + ": same through an IStream", same, viaCom.Diagnostic);
                    }
                }
                catch (Exception ex) { Say(name, false, ex.ToString()); }
            }

            // Refusals: not TTA, encrypted, truncated.
            string scratch = Path.Combine(Path.GetTempPath(), "ttaprobe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                var any = Directory.GetFiles(Folder(), "*.tta");
                if (any.Length > 0)
                {
                    var good = File.ReadAllBytes(any[0]);
                    var encrypted = (byte[])good.Clone();
                    encrypted[4] = 2;
                    var file = Path.Combine(scratch, "enc.tta");
                    File.WriteAllBytes(file, encrypted);
                    using (var d = new TtaDecoder())
                        Say("encrypted refused with a reason", !d.Open(file, null) && d.Diagnostic.Contains("password"), d.Diagnostic);

                    file = Path.Combine(scratch, "cut.tta");
                    File.WriteAllBytes(file, good.AsSpan(0, good.Length * 2 / 3).ToArray());
                    using (var d = new TtaDecoder())
                    {
                        bool opened = d.Open(file, null);
                        var got = opened ? ReadAll(d) : new List<float>();
                        Say("truncated file plays what it has and stops", opened && got.Count > 0 && got.Count < good.Length, $"{got.Count} samples");
                    }

                    var damaged = (byte[])good.Clone();
                    var rng = new Random(3);
                    for (int i = 0; i < 50; i++) damaged[200 + rng.Next(damaged.Length - 200)] ^= 0x5A;
                    file = Path.Combine(scratch, "damaged.tta");
                    File.WriteAllBytes(file, damaged);
                    using (var d = new TtaDecoder())
                    {
                        bool calm = true;
                        try { if (d.Open(file, null)) ReadAll(d); } catch { calm = false; }
                        Say("damaged file decodes without throwing", calm, d.Diagnostic);
                    }
                }
                var notTta = Path.Combine(scratch, "x.tta");
                File.WriteAllBytes(notTta, Encoding.ASCII.GetBytes("RIFF....WAVEfmt "));
                Say("a WAV is not TTA", !TtaDecoder.IsTta(notTta, null));
            }
            finally { try { Directory.Delete(scratch, true); } catch { } }

            Console.WriteLine($"\n{_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static List<float> ReadAll(TtaDecoder d)
        {
            var all = new List<float>();
            var buffer = new float[1000 * d.Channels];
            while (true)
            {
                int got = d.Read(buffer, 1000);
                for (int i = 0; i < got * d.Channels; i++) all.Add(buffer[i]);
                if (got < 1000) break;
            }
            return all;
        }

        private static int ToS32(float f) => (int)Math.Clamp(Math.Round(f * 2147483648.0), int.MinValue, int.MaxValue);
    }
}
