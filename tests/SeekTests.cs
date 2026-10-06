using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// A seek lands on the sample, in every format the player decodes.
    ///
    /// The question each check asks is the one a listener would: after
    /// skipping to a moment, is what plays exactly what would have played at
    /// that moment had the track run there on its own? So every file is
    /// decoded twice — once straight through to the target, once by seeking —
    /// and the two have to agree sample for sample, at the device's rate as
    /// well as the file's. Before this, a variable-bitrate MP3 answered that
    /// 5.6 seconds out on an hour-long file, FLAC up to 35 seconds, and the
    /// clock said the target either way. See <see cref="ExactSeek"/>.
    ///
    /// The files are made here, in formats this machine can write without help:
    /// WAV and FLAC byte by byte, AAC and MP3 by Windows' own encoders, Opus by
    /// Concentus, ALAC and AIFF by hand. The signal is noise with a gliding tone
    /// in it, because a seek that is out by a few samples still matches a sine
    /// wave somewhere and does not match noise anywhere. Ogg Vorbis and
    /// Xing-headed MP3 have no encoder here, so those two are small fixtures in
    /// tests\Fixtures.
    ///
    /// Beyond the two targets checked in every shape, each file also takes a
    /// run of seeks on one decoder — the edges (a sample in, the run-up's own
    /// length, the last half second) and a spread in between — because the
    /// faults this found lived at the edges and in what one seek left behind
    /// for the next. WMA is left out: its container keeps time to the
    /// millisecond, and a seek into it is only that exact (see CLAUDE.md).
    /// </summary>
    internal static class ExactSeekTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        private const int FileRate = 44100;
        private const int Seconds = 90;
        private static readonly double[] Targets = { 7.3, 61.77 };

        public static void RunAll(Action<string, bool, string?> check)
        {
            _check = check;

            Console.WriteLine("Seeking to the sample:");

            var dir = Path.Combine(Path.GetTempPath(), "ExplorerNativeSeek_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var pcm = Signal(FileRate, Seconds);

                var files = new List<string>();

                var wav = Path.Combine(dir, "noise.wav");
                WriteWav(wav, pcm, FileRate);
                files.Add(wav);

                var flac = Path.Combine(dir, "noise.flac");
                WriteFlac(flac, pcm, FileRate);
                files.Add(flac);

                foreach (var (name, subtype) in new[] { ("noise.m4a", MfEncoder.Aac), ("noise.mp3", MfEncoder.Mp3) })
                {
                    var path = Path.Combine(dir, name);
                    if (MfEncoder.Write(path, pcm, FileRate, subtype, out string why)) files.Add(path);
                    else Console.WriteLine($"  ({name}: this machine will not encode it — {why})");
                }

                var opus = Path.Combine(dir, "noise.opus");
                if (WriteOpus(opus, Signal(48000, Seconds))) files.Add(opus);

                var aiff = Path.Combine(dir, "noise.aiff");
                WriteAiff(aiff, pcm, FileRate, "AIFF");
                files.Add(aiff);

                var alac = Path.Combine(dir, "noise-alac.m4a");
                WriteAlac(alac, pcm, FileRate);
                files.Add(alac);

                AiffVariantTests(dir, pcm, wav);
                CdTrackTests(pcm, wav);

                var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
                foreach (var name in new[] { "clicks.ogg", "clicks-vbr.mp3" })
                {
                    var fixture = Path.Combine(fixtures, name);
                    if (File.Exists(fixture)) files.Add(fixture);
                    else Check($"the {name} fixture is next to the suite", false, fixture);
                }

                foreach (var path in files)
                {
                    foreach (int rate in new[] { 48000, path.EndsWith(".opus") ? 44100 : FileRate })
                    {
                        foreach (double target in Targets)
                            if (target < 10 || !path.Contains("clicks")) SeekMatchesStraightDecode(path, rate, target);

                        SeekRun(path, rate);
                    }
                }

                DownloadSeekTests(flac);

                foreach (var path in files.Where(f => f.EndsWith(".opus") || f.EndsWith(".ogg")))
                    LateStartTests(dir, path);

                // A Vorbis file cut a second and a half in, which was under the
                // old late-start threshold: it opened and played nothing. And
                // one cut a few milliseconds in, under the new one, which has to
                // play even though its seeks are out by the cut.
                foreach (var path in files.Where(f => f.EndsWith(".ogg")))
                {
                    LateStartTests(dir, path, 66_150);
                    StartsSoonAfterZero(dir, path, 3_000);
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }

            Console.WriteLine();
        }

        private static void SeekMatchesStraightDecode(string path, int rate, double target)
        {
            string label = $"{Path.GetExtension(path)} at {rate}Hz, {target}s";

            var truth = StraightTo(path, null, rate, target);
            if (truth == null) { Check($"{label}: it opens", false); return; }

            using var decoder = TrackDecoder.Open(path, null, rate, 2, out string why);
            if (decoder == null) { Check($"{label}: it opens", false, why); return; }

            // An MP3's frame walk runs in the background from the moment the file
            // opens; a skip comes later than this in any real use.
            Thread.Sleep(200);

            var clock = Stopwatch.StartNew();
            bool moved = decoder.SeekTo(target);
            long ms = clock.ElapsedMilliseconds;
            var got = Take(decoder, rate / 2);

            Check($"{label}: the seek is quick ({ms}ms)", moved && ms < 250, $"{ms}ms");
            Check($"{label}: and plays what a straight decode plays there",
                Same(truth, got, out string detail), detail);

            double expected = target + got.Length / 2 / (double)rate;
            Check($"{label}: and the clock agrees",
                Math.Abs(decoder.Position - expected) < 1.0 / rate,
                $"{decoder.Position:0.######} against {expected:0.######}");
        }

        /// <summary>
        /// Forty seeks on one decoder, every one compared with a straight decode
        /// of the whole file: the edges first, then a spread.
        /// </summary>
        private static void SeekRun(string path, int rate)
        {
            float[] whole;
            using (var straight = TrackDecoder.Open(path, null, rate, 2, out _))
            {
                if (straight == null) return;
                whole = TakeAll(straight);
            }

            long total = whole.Length / 2;
            double length = total / (double)rate;

            var targets = new List<double> { 1.0 / rate, 0.001, 0.2, 0.25, 0.3, 3.5, length - 0.5, length - 0.01, 0 };
            var random = new Random(5);
            while (targets.Count < 40) targets.Add(Math.Round(random.NextDouble() * (length - 0.3), 4));

            using var decoder = TrackDecoder.Open(path, null, rate, 2, out _);
            if (decoder == null) return;
            Thread.Sleep(200);

            int wrong = 0;
            string first = "";
            foreach (var target in targets)
            {
                if (!decoder.SeekTo(target)) { wrong++; first = first.Length > 0 ? first : $"{target}s was refused"; continue; }

                bool clockRight = Math.Abs(decoder.Position - target) <= 1.0 / rate;
                var got = Take(decoder, 2400);

                long at = (long)Math.Round(target * rate);
                int expected = (int)Math.Max(0, Math.Min(2400, total - at));
                bool same = got.Length == expected * 2;
                for (int i = 0; same && i < got.Length; i++)
                    same = Math.Abs(got[i] - whole[at * 2 + i]) < 1e-6;

                if (!same || !clockRight)
                {
                    wrong++;
                    if (first.Length == 0)
                        first = $"{target}s: " + (!same ? $"{got.Length / 2} frames, not those of a straight decode" : $"clock said {decoder.Position}");
                }
            }

            Check($"{Path.GetFileName(path)} at {rate}Hz: forty seeks in a row, each to the sample", wrong == 0,
                $"{wrong} wrong; first: {first}");
        }

        /// <summary>
        /// The same file played the way a Drive track is: through a
        /// <see cref="StreamingSource"/>, with the seek answered from the
        /// downloaded bytes by a <see cref="VirtualStream"/>.
        /// </summary>
        private static void DownloadSeekTests(string flac)
        {
            const int rate = 48000;
            const double target = 61.77;

            var truth = StraightTo(flac, null, rate, target);
            if (truth == null) return;

            using var source = new StreamingSource(flac, 1L << 30);
            using var decoder = TrackDecoder.Open(flac, source, rate, 2, out string why);
            if (decoder == null) { Check("a downloading FLAC opens", false, why); return; }

            var clock = Stopwatch.StartNew();
            while (!source.Complete && clock.ElapsedMilliseconds < 10_000) Thread.Sleep(10);

            bool moved = decoder.SeekTo(target);
            var got = Take(decoder, rate / 2);
            bool same = Same(truth, got, out string detail);

            Check("a downloaded FLAC seeks to the sample too",
                moved && same, moved ? detail : "the seek failed");
        }

        private static float[]? StraightTo(string path, System.Runtime.InteropServices.ComTypes.IStream? stream,
            int rate, double target)
        {
            using var decoder = TrackDecoder.Open(path, stream, rate, 2, out _);
            if (decoder == null) return null;

            long skip = (long)Math.Round(target * rate);
            var scratch = new float[65536 * 2];
            while (skip > 0)
            {
                int got = decoder.Read(scratch, (int)Math.Min(65536, skip));
                if (got <= 0) return null;
                skip -= got;
            }

            return Take(decoder, rate / 2);
        }

        /// <summary>
        /// A stream that starts at a large granule position — a capture begun
        /// part way through a broadcast — plays, measures and seeks exactly like
        /// the same audio starting at zero.
        /// </summary>
        private static void LateStartTests(string dir, string original, long offset = 10_000_000)
        {
            var name = Path.GetFileName(original) + (offset == 10_000_000 ? "" : $" ({offset:N0} samples in)");
            var shifted = Path.Combine(dir, $"late{offset}-" + Path.GetFileName(original));
            File.WriteAllBytes(shifted, ShiftGranules(File.ReadAllBytes(original), offset));

            using var plain = TrackDecoder.Open(original, null, 48000, 2, out _);
            using var late = TrackDecoder.Open(shifted, null, 48000, 2, out string why);
            if (plain == null || late == null) { Check($"{name} starting late: it opens", false, why); return; }

            if (offset != 10_000_000)
            {
                var first = Take(late, 4800);
                Check($"{name} starting late: it plays from the start",
                    Same(Take(plain, 4800), first, out string played), played);
                plain.SeekTo(0);
                late.SeekTo(0);
            }

            Check($"{name} starting late: the length is the audio's, not the broadcast's",
                Math.Abs(plain.Duration - late.Duration) < 0.002,
                $"{late.Duration:0.###}s against {plain.Duration:0.###}s");

            double target = Math.Min(5.3, plain.Duration / 2);
            plain.SeekTo(target);
            late.SeekTo(target);
            var expected = Take(plain, 4800);
            var got = Take(late, 4800);
            Check($"{name} starting late: a seek lands where it does in the ordinary file",
                Same(expected, got, out string detail), detail);
        }

        /// <summary>
        /// A stream that starts too little way in to count as a late start
        /// still plays, from its first sample.
        /// </summary>
        private static void StartsSoonAfterZero(string dir, string original, long offset)
        {
            var name = $"{Path.GetFileName(original)} ({offset:N0} samples in)";
            var shifted = Path.Combine(dir, $"soon{offset}-" + Path.GetFileName(original));
            File.WriteAllBytes(shifted, ShiftGranules(File.ReadAllBytes(original), offset));

            using var plain = TrackDecoder.Open(original, null, 48000, 2, out _);
            using var soon = TrackDecoder.Open(shifted, null, 48000, 2, out string why);
            if (plain == null || soon == null) { Check($"{name}: it opens", false, why); return; }

            Check($"{name}: it plays from the start",
                Same(Take(plain, 4800), Take(soon, 4800), out string detail), detail);
        }

        /// <summary>Adds <paramref name="offset"/> to every audio page's granule, fixing the checksums.</summary>
        private static byte[] ShiftGranules(byte[] data, long offset)
        {
            var copy = (byte[])data.Clone();
            int at = 0;
            while (at + 27 <= copy.Length)
            {
                if (copy[at] != 'O' || copy[at + 1] != 'g' || copy[at + 2] != 'g' || copy[at + 3] != 'S') { at++; continue; }
                int segments = copy[at + 26];
                int body = 0;
                for (int i = 0; i < segments; i++) body += copy[at + 27 + i];
                int length = 27 + segments + body;

                long granule = BitConverter.ToInt64(copy, at + 6);
                if (granule > 0)
                {
                    BitConverter.GetBytes(granule + offset).CopyTo(copy, at + 6);
                    Array.Clear(copy, at + 22, 4);
                    uint crc = 0;
                    for (int i = at; i < at + length; i++)
                    {
                        crc ^= (uint)copy[i] << 24;
                        for (int b = 0; b < 8; b++)
                            crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
                    }
                    BitConverter.GetBytes(crc).CopyTo(copy, at + 22);
                }
                at += length;
            }
            return copy;
        }

        private static float[] Take(ITrackDecoder decoder, int frames)
        {
            var all = new float[frames * 2];
            var block = new float[4096 * 2];
            int have = 0;

            while (have < frames)
            {
                int got = decoder.Read(block, Math.Min(4096, frames - have));
                if (got <= 0) break;
                Array.Copy(block, 0, all, have * 2, got * 2);
                have += got;
            }

            Array.Resize(ref all, have * 2);
            return all;
        }

        private static float[] TakeAll(ITrackDecoder decoder)
        {
            var all = new List<float>();
            var block = new float[65536 * 2];
            int got;
            while ((got = decoder.Read(block, 65536)) > 0)
                all.AddRange(new ArraySegment<float>(block, 0, got * 2));
            return all.ToArray();
        }

        private static bool Same(float[] expected, float[] actual, out string detail, double tolerance = 1e-6)
        {
            if (expected.Length != actual.Length || expected.Length == 0)
            {
                detail = $"{actual.Length / 2} frames against {expected.Length / 2}";
                return false;
            }

            double worst = 0;
            int at = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                double d = Math.Abs(expected[i] - actual[i]);
                if (d > worst) { worst = d; at = i / 2; }
            }

            detail = $"largest difference {worst:0.######} at frame {at}";
            return worst < tolerance;
        }

        /// <summary>Band-limited noise with a gliding tone, as 16-bit stereo.</summary>
        internal static short[] Signal(int rate, int seconds)
        {
            var random = new Random(1234);
            var pcm = new short[rate * seconds * 2];
            double l1 = 0, l2 = 0, phase = 0;

            for (int i = 0; i < rate * seconds; i++)
            {
                l1 += 0.3 * (random.NextDouble() * 2 - 1 - l1);
                l2 += 0.3 * (random.NextDouble() * 2 - 1 - l2);

                double t = i / (double)rate;
                phase += 2 * Math.PI * (300 + 200 * Math.Sin(2 * Math.PI * t / 7.3)) / rate;
                double tone = 0.2 * Math.Sin(phase);

                pcm[i * 2] = (short)(Math.Clamp(0.6 * l1 + tone, -1, 1) * 32000);
                pcm[i * 2 + 1] = (short)(Math.Clamp(0.6 * l2 + tone, -1, 1) * 32000);
            }

            return pcm;
        }

        /// <summary>
        /// Every AIFF shape <see cref="AiffStream"/> claims to read decodes to
        /// exactly what the same samples as WAV decode to.
        /// </summary>
        private static void AiffVariantTests(string dir, short[] pcm, string wav)
        {
            var reference = StraightTo(wav, null, FileRate, 3.0);
            if (reference == null) { Check("the WAV reference decodes", false); return; }

            foreach (var kind in new[] { "AIFF", "NONE", "sowt", "fl32", "in24" })
            {
                var path = Path.Combine(dir, $"variant-{kind}.aif");
                WriteAiff(path, pcm, FileRate, kind);

                var decoded = StraightTo(path, null, FileRate, 3.0);
                bool same = decoded != null && Same(reference, decoded, out _, tolerance: kind == "fl32" ? 1e-4 : 1e-6);
                Check($"an AIFF written as {kind} plays the same samples as the WAV", same,
                    decoded == null ? "it did not open" : "");
            }

            // And one that is not an AIFF at all, named as one, is refused
            // rather than played as noise.
            var fake = Path.Combine(dir, "fake.aiff");
            File.WriteAllBytes(fake, System.Text.Encoding.ASCII.GetBytes("FORM\0\0\0\x10WAVEnot really"));
            using var refused = TrackDecoder.Open(fake, null, FileRate, 2, out _);
            Check("a file named .aiff that is not one is refused", refused == null);
        }

        /// <summary>
        /// A CD track, served from sectors held in memory, decodes to exactly
        /// what the same samples as WAV decode to — at the disc's own rate and
        /// converted to the device's. The track starts part way into the "disc"
        /// so a sector numbered from the track rather than the disc would show.
        /// </summary>
        private static void CdTrackTests(short[] pcm, string wav)
        {
            var stub = new byte[44];
            System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(stub, 0);
            BitConverter.GetBytes(36).CopyTo(stub, 4);
            System.Text.Encoding.ASCII.GetBytes("CDDAfmt ").CopyTo(stub, 8);
            BitConverter.GetBytes(24).CopyTo(stub, 16);
            BitConverter.GetBytes((ushort)1).CopyTo(stub, 20);
            BitConverter.GetBytes((ushort)3).CopyTo(stub, 22);
            BitConverter.GetBytes(12345u).CopyTo(stub, 28);
            BitConverter.GetBytes(6750u).CopyTo(stub, 32);

            var track = CdTrackStream.ParseCda(stub);
            Check("a .cda stub gives its track, first sector and length",
                track is { Number: 3, FirstSector: 12345, SectorCount: 6750 }, track?.ToString());
            Check("something that is not a .cda stub is refused",
                CdTrackStream.ParseCda(new byte[44]) == null);

            var bytes = new byte[pcm.Length * 2];
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
            var disc = new MemorySectors(bytes, firstSector: 12345);
            var parsed = new CdaTrack(3, 12345, bytes.Length / CdTrackStream.SectorBytes);

            foreach (int rate in new[] { FileRate, 48000 })
            {
                var reference = StraightTo(wav, null, rate, 7.3);
                using var stream = new CdTrackStream(disc, parsed);
                var decoded = StraightTo("track03.wav", stream, rate, 7.3);
                Check($"a CD track at {rate}Hz plays the same samples as the WAV",
                    reference != null && decoded != null && Same(reference, decoded, out _, tolerance: 1e-6),
                    decoded == null ? "it did not open" : "");
            }

            // A scratch plays as silence and the track carries on past it.
            var scratched = new MemorySectors(bytes, firstSector: 12345) { Unreadable = 12345 + 100 };
            using var damaged = new CdTrackStream(scratched, parsed);
            var buffer = new byte[CdTrackStream.SectorBytes * 3];
            damaged.Seek(44 + 99L * CdTrackStream.SectorBytes, 0, IntPtr.Zero);
            var got = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
            try
            {
                damaged.Read(buffer, buffer.Length, got);
                int n = System.Runtime.InteropServices.Marshal.ReadInt32(got);
                bool silent = true;
                for (int i = CdTrackStream.SectorBytes; i < 2 * CdTrackStream.SectorBytes; i++) silent &= buffer[i] == 0;
                bool after = buffer.AsSpan(2 * CdTrackStream.SectorBytes).SequenceEqual(
                    bytes.AsSpan(101 * CdTrackStream.SectorBytes, CdTrackStream.SectorBytes));
                Check("an unreadable sector plays as silence and the next one plays", n == buffer.Length && silent && after);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(got); }
        }

        private sealed class MemorySectors : ICdSectors
        {
            private readonly byte[] _audio;
            private readonly long _first;
            public long Unreadable { get; init; } = -1;

            public MemorySectors(byte[] audio, long firstSector) { _audio = audio; _first = firstSector; }

            public bool Read(long first, int count, byte[] into)
            {
                if (Unreadable >= first && Unreadable < first + count) return false;
                long at = (first - _first) * CdTrackStream.SectorBytes;
                int length = count * CdTrackStream.SectorBytes;
                if (at < 0 || at + length > _audio.Length) return false;
                Array.Copy(_audio, at, into, 0, length);
                return true;
            }

            public void Dispose() { }
        }

        /// <summary>
        /// AIFF, or AIFF-C in the named encoding. Big-endian unless "sowt".
        /// </summary>
        internal static void WriteAiff(string path, short[] pcm, int rate, string kind)
        {
            bool aifc = kind != "AIFF";
            int bytes = kind switch { "fl32" => 4, "in24" => 3, _ => 2 };
            long frames = pcm.Length / 2;
            long dataBytes = frames * 2 * bytes;

            using var w = new BinaryWriter(File.Create(path));
            void Be32(long v) { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v); }
            void Be16(int v) { w.Write((byte)(v >> 8)); w.Write((byte)v); }

            int commSize = aifc ? 18 + 4 + 2 : 18;          // + compression type and an empty pascal string
            long formSize = 4 + (8 + commSize) + (8 + 8 + dataBytes) + (aifc ? 12 : 0);

            w.Write("FORM"u8); Be32(formSize); w.Write(aifc ? "AIFC"u8 : "AIFF"u8);
            if (aifc) { w.Write("FVER"u8); Be32(4); Be32(0xA2805140); }

            w.Write("COMM"u8); Be32(commSize);
            Be16(2); Be32(frames); Be16(bytes * 8);
            // 44100 as an 80-bit extended float.
            int exponent = 16383 + 15;
            Be16(exponent);
            ulong mantissa = (ulong)rate << (63 - 15);
            for (int i = 0; i < 8; i++) w.Write((byte)(mantissa >> (56 - 8 * i)));
            if (aifc)
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes(kind));
                w.Write((byte)0); w.Write((byte)0);
            }

            w.Write("SSND"u8); Be32(8 + dataBytes); Be32(0); Be32(0);
            foreach (short v in pcm)
            {
                switch (kind)
                {
                    case "sowt": w.Write(v); break;
                    case "fl32":
                        int bits = BitConverter.SingleToInt32Bits(v / 32768f);
                        Be32(bits);
                        break;
                    case "in24": w.Write((byte)(v >> 8)); w.Write((byte)v); w.Write((byte)0); break;
                    default: Be16(v); break;
                }
            }
        }

        /// <summary>
        /// ALAC in an MP4, with every frame stored uncompressed — which ALAC
        /// allows, and which needs no encoder. Windows' own ALAC encoder
        /// crashes finalising on this machine, so this is how there is an ALAC
        /// file to test at all.
        /// </summary>
        private static void WriteAlac(string path, short[] pcm, int rate)
        {
            const int frameLength = 4096;
            long total = pcm.Length / 2;
            var frames = new List<byte[]>();

            for (long first = 0; first < total; first += frameLength)
            {
                int count = (int)Math.Min(frameLength, total - first);
                var bits = new BitWriter();
                bits.Put(1, 3);                     // channel pair element
                bits.Put(0, 4);                     // instance tag
                bits.Put(0, 12);                    // unused
                bits.Put(count != frameLength ? 1u : 0u, 1);
                bits.Put(0, 2);                     // no shifted bytes
                bits.Put(1, 1);                     // not compressed
                if (count != frameLength) bits.Put((uint)count, 32);
                for (int i = 0; i < count; i++)
                    for (int c = 0; c < 2; c++)
                        bits.Put((ushort)pcm[(first + i) * 2 + c], 16);
                bits.Put(7, 3);                     // end
                frames.Add(bits.ToArray());
            }

            int maxFrame = 0;
            foreach (var f in frames) maxFrame = Math.Max(maxFrame, f.Length);

            var ftyp = Box("ftyp", Cat("M4A "u8.ToArray(), U32(0), "M4A "u8.ToArray(), "mp42"u8.ToArray(), "isom"u8.ToArray()));
            long mdatStart = ftyp.Length + 8;

            var offsets = new List<byte[]>();
            var sizes = new List<byte[]>();
            long at = mdatStart;
            foreach (var f in frames)
            {
                offsets.Add(U32((uint)at));
                sizes.Add(U32((uint)f.Length));
                at += f.Length;
            }

            var cookie = Cat(U32(frameLength), new byte[] { 0, 16, 40, 10, 14, 2 }, U16(255),
                U32((uint)maxFrame), U32(0), U32((uint)rate));
            var alacBox = Box("alac", Cat(U32(0), cookie));
            var entry = Box("alac", Cat(new byte[6], U16(1), U16(0), U16(0), U32(0),
                U16(2), U16(16), U16(0), U16(0), U32((uint)rate << 16), alacBox));

            uint duration = (uint)total;
            int full = (int)(total / frameLength);
            int rest = (int)(total % frameLength);
            var stts = rest == 0
                ? Cat(U32(0), U32(1), U32((uint)full), U32(frameLength))
                : Cat(U32(0), U32(2), U32((uint)full), U32(frameLength), U32(1), U32((uint)rest));

            var stbl = Box("stbl", Cat(
                Box("stsd", Cat(U32(0), U32(1), entry)),
                Box("stts", stts),
                Box("stsc", Cat(U32(0), U32(1), U32(1), U32(1), U32(1))),
                Box("stsz", Cat(U32(0), U32(0), U32((uint)frames.Count), Cat(sizes.ToArray()))),
                Box("stco", Cat(U32(0), U32((uint)frames.Count), Cat(offsets.ToArray())))));

            var matrix = Cat(U32(0x10000), U32(0), U32(0), U32(0), U32(0x10000), U32(0), U32(0), U32(0), U32(0x40000000));
            var minf = Box("minf", Cat(
                Box("smhd", new byte[8]),
                Box("dinf", Box("dref", Cat(U32(0), U32(1), Box("url ", U32(1))))),
                stbl));
            var mdia = Box("mdia", Cat(
                Box("mdhd", Cat(U32(0), U32(0), U32(0), U32((uint)rate), U32(duration), U16(0x55C4), U16(0))),
                Box("hdlr", Cat(U32(0), U32(0), "soun"u8.ToArray(), new byte[12], "SoundHandler\0"u8.ToArray())),
                minf));
            var trak = Box("trak", Cat(
                Box("tkhd", Cat(U32(7), U32(0), U32(0), U32(1), U32(0), U32(duration), new byte[8],
                    U16(0), U16(0), U16(0x0100), U16(0), matrix, U32(0), U32(0))),
                mdia));
            var moov = Box("moov", Cat(
                Box("mvhd", Cat(U32(0), U32(0), U32(0), U32((uint)rate), U32(duration), U32(0x10000), U16(0x0100),
                    new byte[10], matrix, new byte[24], U32(2))),
                trak));

            using var file = File.Create(path);
            file.Write(ftyp);
            long mdatSize = 8 + (at - mdatStart);
            file.Write(U32((uint)mdatSize));
            file.Write("mdat"u8);
            foreach (var f in frames) file.Write(f);
            file.Write(moov);
        }

        private static byte[] U32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        private static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };

        private static byte[] Cat(params byte[][] parts)
        {
            int length = 0;
            foreach (var p in parts) length += p.Length;
            var all = new byte[length];
            int at = 0;
            foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }
            return all;
        }

        private static byte[] Box(string type, byte[] body) =>
            Cat(U32((uint)(8 + body.Length)), System.Text.Encoding.ASCII.GetBytes(type), body);

        private sealed class BitWriter
        {
            private readonly List<byte> _bytes = new();
            private int _bits;
            private int _count;

            public void Put(uint value, int width)
            {
                for (int i = width - 1; i >= 0; i--)
                {
                    _bits = (_bits << 1) | (int)((value >> i) & 1);
                    if (++_count == 8) { _bytes.Add((byte)_bits); _bits = 0; _count = 0; }
                }
            }

            public byte[] ToArray()
            {
                if (_count > 0) { _bytes.Add((byte)(_bits << (8 - _count))); _bits = 0; _count = 0; }
                return _bytes.ToArray();
            }
        }

        internal static void WriteWav(string path, short[] pcm, int rate)
        {
            using var w = new BinaryWriter(File.Create(path));
            int bytes = pcm.Length * 2;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
            w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write("data"u8); w.Write(bytes);
            foreach (short s in pcm) w.Write(s);
        }

        /// <summary>
        /// FLAC with verbatim subframes: uncompressed, which is still FLAC, and
        /// is the one kind that needs no encoder. No seek table, which is the
        /// case Media Foundation got most wrong.
        /// </summary>
        internal static void WriteFlac(string path, short[] pcm, int rate)
        {
            const int block = 4096;
            long total = pcm.Length / 2;

            using var file = File.Create(path);
            file.Write("fLaC"u8);

            var info = new byte[34];
            info[0] = block >> 8; info[1] = block & 0xFF;
            info[2] = block >> 8; info[3] = block & 0xFF;
            ulong packed = ((ulong)rate << 44) | (1UL << 41) | (15UL << 36) | (ulong)total;
            for (int i = 0; i < 8; i++) info[10 + i] = (byte)(packed >> (56 - 8 * i));
            file.WriteByte(0x80); file.WriteByte(0); file.WriteByte(0); file.WriteByte(34);
            file.Write(info);

            var frame = new List<byte>();
            for (long first = 0, number = 0; first < total; first += block, number++)
            {
                int size = (int)Math.Min(block, total - first);
                frame.Clear();
                frame.Add(0xFF); frame.Add(0xF8);
                frame.Add((byte)(((size == block ? 12 : 7) << 4) | 9));      // 4096 or explicit; 44.1kHz
                frame.Add((1 << 4) | (4 << 1));                               // left/right; 16-bit
                Utf8(frame, number);
                if (size != block) { frame.Add((byte)((size - 1) >> 8)); frame.Add((byte)(size - 1)); }
                frame.Add(Crc8(frame));

                for (int channel = 0; channel < 2; channel++)
                {
                    frame.Add(0x02);                                          // verbatim
                    for (int i = 0; i < size; i++)
                    {
                        short v = pcm[(first + i) * 2 + channel];
                        frame.Add((byte)(v >> 8));
                        frame.Add((byte)v);
                    }
                }

                ushort crc = Crc16(frame);
                frame.Add((byte)(crc >> 8));
                frame.Add((byte)crc);
                file.Write(frame.ToArray());
            }
        }

        private static void Utf8(List<byte> into, long value)
        {
            if (value < 0x80) { into.Add((byte)value); return; }

            int extra = value < 0x800 ? 1 : value < 0x10000 ? 2 : value < 0x200000 ? 3 : 4;
            int lead = (0xFF << (7 - extra)) & 0xFF;
            into.Add((byte)(lead | (int)(value >> (6 * extra))));
            for (int i = extra - 1; i >= 0; i--) into.Add((byte)(0x80 | ((value >> (6 * i)) & 0x3F)));
        }

        private static byte Crc8(List<byte> data)
        {
            int crc = 0;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++) crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
            }
            return (byte)crc;
        }

        private static ushort Crc16(List<byte> data)
        {
            int crc = 0;
            foreach (byte b in data)
            {
                crc ^= b << 8;
                for (int i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
            return (ushort)crc;
        }

        internal static bool WriteOpus(string path, short[] pcm)
        {
            try
            {
                using var file = File.Create(path);
                var encoder = Concentus.OpusCodecFactory.CreateEncoder(
                    48000, 2, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO, null);
                var writer = new Concentus.Oggfile.OpusOggWriteStream(encoder, file, null, 48000, 2, false);
                writer.WriteSamples(pcm, 0, pcm.Length);
                writer.Finish();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  (Opus: could not be written — {ex.Message})");
                return false;
            }
        }

        /// <summary>Windows' own encoders, through the sink writer.</summary>
        internal static class MfEncoder
        {
            public static readonly Guid Aac = new("00001610-0000-0010-8000-00AA00389B71");
            public static readonly Guid Mp3 = new("00000055-0000-0010-8000-00AA00389B71");

            [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IMFMediaType
            {
                void R00(); void R01(); void R02(); void R03();
                [PreserveSig] int GetUINT32(ref Guid key, out uint value);          // 4
                void R05(); void R06(); void R07(); void R08(); void R09();
                void R10(); void R11(); void R12(); void R13(); void R14();
                void R15(); void R16(); void R17();
                [PreserveSig] int SetUINT32(ref Guid key, uint value);              // 18
                void R19(); void R20();
                [PreserveSig] int SetGUID(ref Guid key, ref Guid value);            // 21
            }

            [ComImport, Guid("5BC8A76B-869A-46a3-9B03-FA218A66AEBE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IMFCollection
            {
                [PreserveSig] int GetElementCount(out uint count);
                [PreserveSig] int GetElement(uint index, [MarshalAs(UnmanagedType.IUnknown)] out object element);
            }

            [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IMFMediaBuffer
            {
                [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
                [PreserveSig] int Unlock();
                void R02();
                [PreserveSig] int SetCurrentLength(uint length);
            }

            [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IMFSample
            {
                void A00(); void A01(); void A02(); void A03(); void A04();
                void A05(); void A06(); void A07(); void A08(); void A09();
                void A10(); void A11(); void A12(); void A13(); void A14();
                void A15(); void A16(); void A17(); void A18(); void A19();
                void A20(); void A21(); void A22(); void A23(); void A24();
                void A25(); void A26(); void A27(); void A28(); void A29();
                void S30(); void S31(); void S32();
                [PreserveSig] int SetSampleTime(long time);                         // 33
                void S34();
                [PreserveSig] int SetSampleDuration(long duration);                 // 35
                void S36(); void S37(); void S38();
                [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);                 // 39
            }

            [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IMFSinkWriter
            {
                [PreserveSig] int AddStream(IMFMediaType targetType, out uint streamIndex);
                [PreserveSig] int SetInputMediaType(uint streamIndex, IMFMediaType inputType, IntPtr parameters);
                [PreserveSig] int BeginWriting();
                [PreserveSig] int WriteSample(uint streamIndex, IMFSample sample);
                void R04(); void R05(); void R06(); void R07();
                [PreserveSig] int DoFinalize();                                     // 8
            }

            [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
            [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IMFMediaType type);
            [DllImport("mfplat.dll")] private static extern int MFCreateSample(out IMFSample sample);
            [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint length, out IMFMediaBuffer buffer);

            [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
            private static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream,
                IntPtr attributes, out IMFSinkWriter writer);

            [DllImport("mf.dll")]
            private static extern int MFTranscodeGetAudioOutputAvailableTypes(ref Guid subtype, uint flags,
                IntPtr configuration, out IMFCollection types);

            private static Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
            private static Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
            private static Guid Audio = new("73647561-0000-0010-8000-00AA00389B71");
            private static Guid Pcm = new("00000001-0000-0010-8000-00AA00389B71");
            private static Guid Rate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
            private static Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
            private static Guid Bits = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
            private static Guid Align = new("322de230-9eeb-43bd-ab7a-ff412251541d");
            private static Guid BytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
            private static Guid AacPayload = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");

            public static bool Write(string path, short[] pcm, int rate, Guid codec, out string why)
            {
                why = "";
                try
                {
                    MFStartup(0x00020070, 0);

                    IMFMediaType? output = null;
                    if (codec == Aac)
                    {
                        // The AAC encoder takes one of a handful of exact shapes and
                        // lists none of them usefully; this is one it accepts.
                        MFCreateMediaType(out output);
                        output.SetGUID(ref MajorType, ref Audio);
                        output.SetGUID(ref Subtype, ref codec);
                        output.SetUINT32(ref Bits, 16);
                        output.SetUINT32(ref Rate, (uint)rate);
                        output.SetUINT32(ref Channels, 2);
                        output.SetUINT32(ref BytesPerSecond, 24000);
                        output.SetUINT32(ref AacPayload, 0);
                    }
                    else
                    {
                        int hr0 = MFTranscodeGetAudioOutputAvailableTypes(ref codec, 0x3F, IntPtr.Zero, out var types);
                        if (hr0 != 0) { why = $"no encoder (0x{hr0:X8})"; return false; }

                        types.GetElementCount(out uint count);
                        uint best = 0;
                        for (uint i = 0; i < count; i++)
                        {
                            types.GetElement(i, out var element);
                            var type = (IMFMediaType)element;
                            type.GetUINT32(ref Rate, out uint r);
                            type.GetUINT32(ref Channels, out uint c);
                            type.GetUINT32(ref BytesPerSecond, out uint b);
                            if (r == rate && c == 2 && b <= 24000 && b >= best) { output = type; best = b; }
                        }
                        if (output == null) { why = "no 44.1kHz stereo output type"; return false; }
                    }

                    MFCreateMediaType(out var input);
                    input.SetGUID(ref MajorType, ref Audio);
                    input.SetGUID(ref Subtype, ref Pcm);
                    input.SetUINT32(ref Bits, 16);
                    input.SetUINT32(ref Rate, (uint)rate);
                    input.SetUINT32(ref Channels, 2);
                    input.SetUINT32(ref Align, 4);
                    input.SetUINT32(ref BytesPerSecond, (uint)(rate * 4));

                    int hr = MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out var writer);
                    if (hr != 0) { why = $"sink writer 0x{hr:X8}"; return false; }
                    if ((hr = writer.AddStream(output, out uint stream)) != 0) { why = $"AddStream 0x{hr:X8}"; return false; }
                    if ((hr = writer.SetInputMediaType(stream, input, IntPtr.Zero)) != 0) { why = $"input 0x{hr:X8}"; return false; }
                    if ((hr = writer.BeginWriting()) != 0) { why = $"BeginWriting 0x{hr:X8}"; return false; }

                    int perBlock = rate / 10;
                    long frames = 0;
                    var bytes = new byte[perBlock * 4];
                    for (int at = 0; at < pcm.Length; at += perBlock * 2)
                    {
                        int count = Math.Min(perBlock * 2, pcm.Length - at);
                        Buffer.BlockCopy(pcm, at * 2, bytes, 0, count * 2);

                        MFCreateMemoryBuffer((uint)(count * 2), out var buffer);
                        buffer.Lock(out IntPtr into, out _, out _);
                        Marshal.Copy(bytes, 0, into, count * 2);
                        buffer.Unlock();
                        buffer.SetCurrentLength((uint)(count * 2));

                        MFCreateSample(out var sample);
                        sample.AddBuffer(buffer);
                        long start = 10_000_000L * frames / rate;
                        frames += count / 2;
                        sample.SetSampleTime(start);
                        sample.SetSampleDuration(10_000_000L * frames / rate - start);

                        if ((hr = writer.WriteSample(stream, sample)) != 0) { why = $"WriteSample 0x{hr:X8}"; return false; }
                        Marshal.ReleaseComObject(sample);
                        Marshal.ReleaseComObject(buffer);
                    }

                    if ((hr = writer.DoFinalize()) != 0) { why = $"Finalize 0x{hr:X8}"; return false; }
                    return true;
                }
                catch (Exception ex)
                {
                    why = ex.GetType().Name + ": " + ex.Message;
                    return false;
                }
            }
        }
    }
}
