using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ExplorerNative
{
    /// <summary>
    /// What the second, deeper 3.0.9 sweep of the decoders, the converter, the
    /// editor and the tags found, held:
    /// a song in a folder whose name ends in a dot ("Backup.\song.wav") plays,
    /// converts, and has its tags read and written as itself (it reached the
    /// file of the same name in "Backup"); damaged CAF and WAV headers fail
    /// with a sentence instead of running out of memory; a WAV over 4GB whose
    /// size wrapped plays to its real end; the editor's audio stays right when
    /// the source refuses a seek; FLAC in Ogg with damage at its end keeps
    /// what came before. Fixtures are tones made here; each run has a 60-second
    /// watchdog.
    /// </summary>
    internal static class Sweep309DecoderTests
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

            Console.WriteLine("Decoder sweep (3.0.9):");

            var dir = Path.Combine(Path.GetTempPath(), "ExplorerNativeSweep309_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                WithWatchdog("a song in a folder ending in a dot", () => DottedFolderTests(dir));
                WithWatchdog("damaged headers fail with a sentence", () => DamagedHeaderTests(dir));
                WithWatchdog("a WAV over 4GB whose size wrapped", () => WrappedWavTests(dir));
                WithWatchdog("the editor when the source refuses a seek", EditedRefusedSeekTests);
                WithWatchdog("FLAC in Ogg damaged at the end", () => OggFlacDamageTests(dir));
                WithWatchdog("Media Foundation reads are bounded", SourceShapeTests);
                WithWatchdog("Opus in CAF", () => CafOpusTests(dir));
            }
            finally
            {
                try { Directory.Delete(@"\\?\" + dir, true); } catch { }
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

        // ---------- fixtures ----------

        /// <summary>A stereo 16-bit WAV of a tone, written to the path exactly as given (a \\?\ path included).</summary>
        private static void WriteToneWav(string path, double seconds, double hertz)
        {
            int frames = (int)(seconds * Rate);
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(file);
            w.Write("RIFF"u8);
            w.Write(36 + frames * 4);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write((short)2);
            w.Write(Rate);
            w.Write(Rate * 4);
            w.Write((short)4);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(frames * 4);
            for (int i = 0; i < frames; i++)
            {
                short v = (short)Math.Round(12000 * Math.Sin(2 * Math.PI * hertz * i / Rate));
                w.Write(v);
                w.Write(v);
            }
        }

        private static List<float> ReadAll(ITrackDecoder decoder)
        {
            int channels = Math.Max(1, decoder.Channels);
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

        private static float Peak(IEnumerable<float> samples)
        {
            float peak = 0;
            foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
            return peak;
        }

        // ---------- a folder whose name ends in a dot ----------

        private static void DottedFolderTests(string dir)
        {
            var plainFolder = Path.Combine(dir, "Backup");
            var dottedFolder = plainFolder + ".";                 // as the app names it
            var dottedExact = @"\\?\" + dottedFolder;            // as Windows must be told it
            Directory.CreateDirectory(plainFolder);
            Directory.CreateDirectory(dottedExact);

            // Different sounds of different lengths, so which one answered is plain.
            WriteToneWav(Path.Combine(plainFolder, "song.wav"), 3.0, 880);
            WriteToneWav(dottedExact + @"\song.wav", 1.5, 440);
            File.WriteAllText(Path.Combine(plainFolder, "notes.txt"), "one two three four five six");
            File.WriteAllText(dottedExact + @"\notes.txt", "alpha beta");

            var dottedSong = Path.Combine(dottedFolder, "song.wav");

            // Playing (the converter and editor open it the same way).
            var decoder = TrackDecoder.Open(dottedSong, null, 0, 0, out string why);
            Check("a WAV in \"Backup.\" opens", decoder != null, why);
            if (decoder != null)
                using (decoder)
                {
                    Check($"and it is that folder's file, not \"Backup\"'s ({decoder.Duration:0.00}s)",
                        Math.Abs(decoder.Duration - 1.5) < 0.05);
                    var all = ReadAll(decoder);
                    Check("and it decodes to the end, not silence",
                        Math.Abs(all.Count / (double)Math.Max(1, decoder.Channels) / decoder.SampleRate - 1.5) < 0.05 &&
                        Peak(all) > 0.3f, $"{all.Count} samples, peak {Peak(all):0.###}");
                }

            // Gathering a selection of the folder itself.
            var gathered = AudioConverter.Gather(new[] { dottedFolder }, false, null, CancellationToken.None);
            Check("gathering \"Backup.\" finds its song, by its own name",
                gathered.Count == 1 && string.Equals(gathered[0], dottedSong, StringComparison.OrdinalIgnoreCase),
                string.Join(" | ", gathered));

            // Converting to FLAC: written beside it, in the dotted folder.
            var choice = AudioFormats.ById("flac");
            var plan = AudioConverter.Plan(new[] { dottedSong }, choice);
            var target = plan.Jobs[0].Target;
            Check("its FLAC target is in \"Backup.\"", string.Equals(target, Path.Combine(dottedFolder, "song.flac"),
                StringComparison.OrdinalIgnoreCase), target);
            string written = "";
            try
            {
                written = AudioConverter.ConvertFile(plan.Jobs[0], choice, null, CancellationToken.None, new List<string>());
            }
            catch (Exception ex) { Check("converting a song in \"Backup.\" works", false, ex.Message); }
            Check("the FLAC lands in \"Backup.\" and not in \"Backup\"",
                File.Exists(dottedExact + @"\song.flac") && !File.Exists(Path.Combine(plainFolder, "song.flac")), written);
            Check("and nothing temporary is left behind",
                Directory.GetFiles(dottedExact, AudioConverter.TempMarker + "*").Length == 0 &&
                Directory.GetFiles(plainFolder, AudioConverter.TempMarker + "*").Length == 0);

            var flacDotted = Path.Combine(dottedFolder, "song.flac");
            var flac = TrackDecoder.Open(flacDotted, null, 0, 0, out why);
            Check("the FLAC written there plays", flac != null, why);
            if (flac != null)
                using (flac)
                    Check($"and is the 1.5-second tone ({flac.Duration:0.00}s)", Math.Abs(flac.Duration - 1.5) < 0.05);

            // A clash is a clash in the dotted folder, whatever "Backup" has.
            var again = AudioConverter.Plan(new[] { dottedSong }, choice);
            Check("converting again sees the FLAC already in \"Backup.\"", again.Clashing.Count == 1);

            // Tags: a FLAC beside it in "Backup" too, titled differently.
            try
            {
                AudioConverter.ConvertFile(new ConvertJob(Path.Combine(plainFolder, "song.wav"),
                    Path.Combine(plainFolder, "song.flac"), 1, false), choice, null, CancellationToken.None, new List<string>());
            }
            catch (Exception ex) { Check("the plain folder's FLAC is made", false, ex.Message); }

            var plainFlac = Path.Combine(plainFolder, "song.flac");
            var plainError = AudioTags.WriteFields(plainFlac, new Dictionary<TagField, string> { [TagField.Title] = "Plain" });
            if (plainError != null)
            {
                // No FLAC property handler on this Windows: nothing to compare.
                Console.WriteLine("  (no FLAC tag handler here: " + plainError + ")");
            }
            else
            {
                var error = AudioTags.WriteFields(flacDotted, new Dictionary<TagField, string> { [TagField.Title] = "Dotted é" });
                Check("tags are written to a FLAC in \"Backup.\"", error == null, error);
                var dottedFields = AudioTags.ReadFields(flacDotted);
                var plainFields = AudioTags.ReadFields(plainFlac);
                Equal("and read back from it", "Dotted é", dottedFields?[TagField.Title] ?? "(none)");
                Equal("while \"Backup\"'s file keeps its own title (it was overwritten)", "Plain",
                    plainFields?[TagField.Title] ?? "(none)");

                var rows = new List<SongProperty>(AudioTags.Read(flacDotted));
                var length = rows.Find(r => r.Name == "Length")?.Value;
                Check("the properties sheet shows the dotted file's length, not \"Backup\"'s",
                    length != null && length != AudioPlayer.SpokenTime(3.0) &&
                    (length == AudioPlayer.SpokenTime(1.4) || length == AudioPlayer.SpokenTime(1.5) ||
                     length == AudioPlayer.SpokenTime(1.6)),
                    string.Join("; ", rows.ConvertAll(r => r.Name + "=" + r.Value)));

                var set = AudioTags.ReadTagSet(flacDotted);
                Equal("the converter carries the dotted file's own tags", "Dotted é", set?.Title ?? "(none)");
            }

            var details = FileDetails.Describe(Path.Combine(dottedFolder, "notes.txt"), null);
            Equal("word counts read the dotted folder's text file", "2 words, 10 characters", details ?? "(none)");
        }

        // ---------- damaged headers ----------

        private static void DamagedHeaderTests(string dir)
        {
            // A 16-bit stereo WAV claiming 64KB per frame, as a damaged header
            // can. Media Foundation refuses it, and the managed reader asked for
            // 268MB a read.
            var wide = Path.Combine(dir, "wide.wav");
            using (var file = new FileStream(wide, FileMode.Create))
            using (var w = new BinaryWriter(file))
            {
                w.Write("RIFF"u8); w.Write(36 + 65535 * 4); w.Write("WAVE"u8);
                w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate);
                w.Write(Rate * 4); w.Write((short)-1); w.Write((short)16);
                w.Write("data"u8); w.Write(65535 * 4); w.Write(new byte[65535 * 4]);
            }
            using (var wav = new WavDecoder())
            {
                long before = GC.GetTotalAllocatedBytes();
                bool opened = wav.Open(wide, null);
                long allocated = GC.GetTotalAllocatedBytes() - before;
                Check("a WAV with a 64KB block size is refused", !opened, wav.Diagnostic);
                Check("in a sentence", wav.Diagnostic.Contains("makes no sense"), wav.Diagnostic);
                Check($"without a huge allocation ({allocated / 1024}KB)", allocated < 16 << 20);
            }

            // ALAC in CAF with four billion frames a packet.
            var caf = Path.Combine(dir, "huge.caf");
            WriteAlacCafHeaderOnly(caf, framesPerPacket: 0x7FFFFFF0u);
            using (var alac = new WavDecoder())
            {
                long before = GC.GetTotalAllocatedBytes();
                bool opened = false;
                Exception? thrown = null;
                try { opened = alac.Open(caf, null); } catch (Exception ex) { thrown = ex; }
                long allocated = GC.GetTotalAllocatedBytes() - before;
                Check("a CAF claiming four billion frames a packet is refused", !opened && thrown == null, alac.Diagnostic);
                Check("in a sentence, not out of memory", alac.Diagnostic.Contains("damaged") && !alac.Diagnostic.Contains("Memory"),
                    alac.Diagnostic);
                Check($"without a huge allocation ({allocated / 1024}KB)", allocated < 16 << 20);
            }

            // IMA ADPCM whose header claims more samples a block than the block holds.
            var ima = Path.Combine(dir, "ima.wav");
            const int blockAlign = 36, blocks = 100;      // mono: 65 samples a block fit
            using (var file = new FileStream(ima, FileMode.Create))
            using (var w = new BinaryWriter(file))
            {
                w.Write("RIFF"u8); w.Write(40 + blockAlign * blocks); w.Write("WAVE"u8);
                w.Write("fmt "u8); w.Write(20); w.Write((short)0x11); w.Write((short)1); w.Write(8000);
                w.Write(4000); w.Write((short)blockAlign); w.Write((short)4); w.Write((short)2); w.Write(unchecked((short)60000));
                w.Write("data"u8); w.Write(blockAlign * blocks);
                var rng = new Random(5);
                var body = new byte[blockAlign * blocks];
                rng.NextBytes(body);
                for (int b = 0; b < blocks; b++) { body[b * blockAlign + 2] = 10; body[b * blockAlign + 3] = 0; }
                w.Write(body);
            }
            using (var adpcm = new WavDecoder())
            {
                bool opened = adpcm.Open(ima, null);
                Check("an IMA ADPCM WAV with an overstated block opens", opened, adpcm.Diagnostic);
                if (opened)
                {
                    Equal("and its length counts what the blocks hold (it counted 60000 a block)",
                        (65 * blocks).ToString(), Math.Round(adpcm.Duration * 8000).ToString());
                    Equal("and every counted frame decodes", (65 * blocks).ToString(), ReadAll(adpcm).Count.ToString());
                }
            }
        }

        /// <summary>
        /// An iPhone voice message: Opus in CAF, 24kHz mono, 20ms packets (4.1.6,
        /// Conner's "Audio message from ....caf" said CAF holding 'opus' sound).
        /// </summary>
        internal static void CafOpusTests(string dir)
        {
            const int rate = 24000, perPacket = 480, seconds = 2;
            var encoder = Concentus.OpusCodecFactory.CreateEncoder(rate, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO, null);
            var packets = new List<byte[]>();
            var pcm = new float[perPacket];
            var outBuf = new byte[1500];
            for (int p = 0; p < rate * seconds / perPacket; p++)
            {
                for (int i = 0; i < perPacket; i++) pcm[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * (p * perPacket + i) / rate);
                int n = encoder.Encode(pcm, perPacket, outBuf, outBuf.Length);
                packets.Add(outBuf.AsSpan(0, n).ToArray());
            }

            var path = Path.Combine(dir, "Audio message.caf");
            using (var file = new MemoryStream())
            {
                file.Write("caff"u8); file.Write(BE16(1)); file.Write(BE16(0));
                var desc = new MemoryStream();
                desc.Write(BE64(BitConverter.DoubleToInt64Bits(rate)));
                desc.Write("opus"u8);
                desc.Write(BE32(0)); desc.Write(BE32(0)); desc.Write(BE32(perPacket)); desc.Write(BE32(1)); desc.Write(BE32(0));
                Chunk(file, "desc", desc.ToArray());
                var pakt = new MemoryStream();
                pakt.Write(BE64(packets.Count)); pakt.Write(BE64(rate * seconds)); pakt.Write(BE32(0)); pakt.Write(BE32(0));
                foreach (var packet in packets)
                {
                    int v = packet.Length;
                    if (v >= 128) pakt.WriteByte((byte)(0x80 | v >> 7));
                    pakt.WriteByte((byte)(v & 0x7F));
                }
                Chunk(file, "pakt", pakt.ToArray());
                var data = new MemoryStream();
                data.Write(BE32(0));
                foreach (var packet in packets) data.Write(packet);
                Chunk(file, "data", data.ToArray());
                File.WriteAllBytes(path, file.ToArray());
            }

            // EXPLORERNATIVE_CAFFILE=a real one: how loud it decodes, second by second.
            var real = Environment.GetEnvironmentVariable("EXPLORERNATIVE_CAFFILE");
            if (!string.IsNullOrEmpty(real))
            {
                using var r = new WavDecoder();
                Console.WriteLine($"  {Path.GetFileName(real)}: opens {r.Open(real, null)} ({r.Diagnostic}), {r.SampleRate} Hz, {r.Channels} ch, {r.Duration:F2} s");
                var samples = ReadAll(r);
                double all2 = 0, allPeak = 0;
                foreach (var x in samples) { all2 += x * x; allPeak = Math.Max(allPeak, Math.Abs(x)); }
                Console.WriteLine($"    whole file: mean {10 * Math.Log10(all2 / Math.Max(1, samples.Count)):F1} dB, max {20 * Math.Log10(allPeak):F1} dB");
                for (int s = 0; s * r.SampleRate < samples.Count; s += 5)
                {
                    double sum = 0, peak = 0; int n = 0;
                    for (int i = s * r.SampleRate; i < Math.Min(samples.Count, (s + 1) * r.SampleRate); i++) { sum += samples[i] * samples[i]; peak = Math.Max(peak, Math.Abs(samples[i])); n++; }
                    Console.WriteLine($"    second {s}: rms {Math.Sqrt(sum / Math.Max(1, n)):F4}, peak {peak:F3}");
                }
            }

            using var caf = new WavDecoder();
            bool opened = caf.Open(path, null);
            Check("Opus in CAF opens", opened, caf.Diagnostic);
            if (!opened) return;
            const int played = 48000;   // Opus is decoded at its own rate
            Equal("at Opus's own rate, in its channels", "48000 1", $"{caf.SampleRate} {caf.Channels}");
            Equal("two seconds long", "2.00", caf.Duration.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            var all = ReadAll(caf);
            Equal("every frame decodes", (played * seconds).ToString(), all.Count.ToString());
            double tone = Goertzel(all, played, 440), off = Goertzel(all, played, 1234);
            Check($"and it is the tone ({tone:F3} against {off:F5})", tone > 20 * off && tone > 0.01);
            double top = 0;
            foreach (var x in all.Skip(played / 2)) top = Math.Max(top, Math.Abs(x));
            Check($"at the level it went in at (peak {top:F2}, made at 0.5)", top > 0.35 && top < 0.7);

            Check("a seek to one second", caf.SeekTo(1.0));
            Equal("lands there", "1.00", caf.Position.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            var rest = ReadAll(caf);
            Equal("and the rest decodes", (played * seconds / 2).ToString(), rest.Count.ToString());
            Check("still the tone after the seek", Goertzel(rest, played, 440) > 20 * Goertzel(rest, played, 1234));
        }

        private static double Goertzel(List<float> samples, int rate, double frequency)
        {
            double k = 2 * Math.Cos(2 * Math.PI * frequency / rate), s1 = 0, s2 = 0;
            foreach (var x in samples) { double s0 = x + k * s1 - s2; s2 = s1; s1 = s0; }
            return Math.Sqrt(s1 * s1 + s2 * s2 - k * s1 * s2) / samples.Count;
        }

        /// <summary>A CAF of ALAC with a valid cookie and packet table, and 'desc' saying <paramref name="framesPerPacket"/>.</summary>
        private static void WriteAlacCafHeaderOnly(string path, uint framesPerPacket)
        {
            using var file = new MemoryStream();
            file.Write("caff"u8); file.Write(BE16(1)); file.Write(BE16(0));

            var desc = new MemoryStream();
            desc.Write(BE64(BitConverter.DoubleToInt64Bits(Rate)));
            desc.Write("alac"u8);
            desc.Write(BE32(1)); desc.Write(BE32(0)); desc.Write(BE32(framesPerPacket)); desc.Write(BE32(2)); desc.Write(BE32(0));
            Chunk(file, "desc", desc.ToArray());

            var kuki = new MemoryStream();
            kuki.Write(BE32(4096));
            kuki.Write(new byte[] { 0, 16, 40, 10, 14, 2 });
            kuki.Write(BE16(255)); kuki.Write(BE32(100)); kuki.Write(BE32(0)); kuki.Write(BE32(Rate));
            Chunk(file, "kuki", kuki.ToArray());

            var pakt = new MemoryStream();
            pakt.Write(BE64(2)); pakt.Write(BE64(8192)); pakt.Write(BE32(0)); pakt.Write(BE32(0));
            pakt.WriteByte(10); pakt.WriteByte(10);
            Chunk(file, "pakt", pakt.ToArray());

            var data = new MemoryStream();
            data.Write(BE32(0)); data.Write(new byte[20]);
            Chunk(file, "data", data.ToArray());
            File.WriteAllBytes(path, file.ToArray());
        }

        private static void Chunk(Stream into, string id, byte[] body)
        {
            into.Write(Encoding.ASCII.GetBytes(id));
            into.Write(BE64(body.Length));
            into.Write(body);
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

        // ---------- a WAV over 4GB ----------

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize,
            IntPtr output, int outputSize, out int returned, IntPtr overlapped);

        private const uint FSCTL_SET_SPARSE = 0x000900C4;

        private static void WrappedWavTests(string dir)
        {
            // Sparse, so four gigabytes of nothing costs no disk: only the tone at
            // the end is really written.
            var path = Path.Combine(dir, "long.wav");
            const long length = (1L << 32) + 4 * 1024 * 1024;
            const int toneFrames = Rate;                     // the last second is a tone
            long dataBytes = length - 44;
            try
            {
                using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
                {
                    if (!DeviceIoControl(file.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    {
                        Console.WriteLine("  (this volume cannot make sparse files; the 4GB WAV test is skipped)");
                        return;
                    }
                    using var w = new BinaryWriter(file, Encoding.ASCII, leaveOpen: true);
                    w.Write("RIFF"u8); w.Write((uint)((length - 8) & 0xFFFFFFFF)); w.Write("WAVE"u8);
                    w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate);
                    w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
                    w.Write("data"u8); w.Write((uint)(dataBytes & 0xFFFFFFFF));   // wrapped: 4MB and change
                    file.SetLength(length);
                    file.Position = length - toneFrames * 4L;
                    for (int i = 0; i < toneFrames; i++)
                    {
                        short v = (short)Math.Round(10000 * Math.Sin(2 * Math.PI * 500 * i / Rate));
                        w.Write(v); w.Write(v);
                    }
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine("  (no room for the sparse 4GB WAV: " + ex.Message + ")");
                return;
            }

            double expected = dataBytes / 4 / (double)Rate;
            var decoder = TrackDecoder.Open(path, null, 0, 0, out string why);
            Check("a WAV over 4GB with a wrapped size opens", decoder != null, why);
            if (decoder == null) return;
            using (decoder)
            {
                Check($"and is as long as the file ({decoder.Duration:0.0}s, expected {expected:0.0}s; it was cut to the wrapped size)",
                    Math.Abs(decoder.Duration - expected) < 0.01, decoder.GetType().Name);
                decoder.SeekTo(expected - 0.5);
                var tail = new float[Rate * 2];
                int got = decoder.Read(tail, Rate);
                float peak = 0;
                for (int i = 0; i < got * 2; i++) peak = Math.Max(peak, Math.Abs(tail[i]));
                Check($"and its last second plays (peak {peak:0.###})", got > Rate / 3 && peak > 0.25f);
            }
            try { File.Delete(path); } catch { }
        }

        // ---------- the editor when a seek is refused ----------

        /// <summary>A ramp: frame n is n / 100000, mono. Refuses every seek but to the start.</summary>
        private sealed class RampRefusingSeeks : ITrackDecoder
        {
            private long _at;
            public long Frames = 20000;
            public int SampleRate => 8000;
            public int Channels => 1;
            public double Duration => Frames / 8000.0;
            public double Position => _at / 8000.0;
            public int Read(float[] destination, int frames)
            {
                int n = (int)Math.Min(frames, Frames - _at);
                for (int i = 0; i < n; i++) destination[i] = (_at + i) / 100000f;
                _at += Math.Max(0, n);
                return Math.Max(0, n);
            }
            public bool SeekTo(double seconds)
            {
                if (seconds > 0) { _at = 777; return false; }   // refused, and left somewhere else
                _at = 0;
                return true;
            }
            public bool Reopen(int sampleRate, int channels) => true;
            public void Dispose() { }
        }

        private static void EditedRefusedSeekTests()
        {
            var source = new RampRefusingSeeks();
            var edits = new EditList(source.Frames, 8000).Cut(1000, 3000).Cut(5000, 9000);   // keeps 0-1000, 3000-7000, 11000-20000
            Equal("the edit keeps three runs", "3", edits.Segments.Count.ToString());

            using var decoder = new EditedDecoder(source, edits);
            var all = ReadAll(decoder);
            Equal("every kept frame plays", edits.Length.ToString(), all.Count.ToString());

            bool right = all.Count == edits.Length;
            long wrongAt = -1;
            long at = 0;
            foreach (var segment in edits.Segments)
                for (long f = segment.Start; f < segment.End && right; f++, at++)
                    if (Math.Abs(all[(int)at] - f / 100000f) > 1e-7f) { right = false; wrongAt = at; }
            Check("and each run is the source's own frames, though the source refused to seek (the cut came back)",
                right, wrongAt >= 0 ? $"frame {wrongAt} was {all[(int)wrongAt] * 100000:0}" : null);

            // A seek inside the edited timeline, refused underneath, still lands.
            decoder.SeekTo(1500 / 8000.0);                  // edited 1500 = source 3500
            var one = new float[4];
            int got = decoder.Read(one, 4);
            Check("a seek into a later run lands on its frame", got == 4 && Math.Abs(one[0] - 3500 / 100000f) < 1e-7f,
                got > 0 ? $"{one[0] * 100000:0}" : "nothing");
        }

        // ---------- FLAC in Ogg, damaged at the end ----------

        private static void OggFlacDamageTests(string dir)
        {
            // A real FLAC, made by our own encoder from a tone.
            var flacPath = Path.Combine(dir, "inner.flac");
            const int frames = Rate * 2;
            using (var encoder = new FlacEncoder(flacPath, Rate, 2, 16, null))
            {
                var block = new float[4096 * 2];
                for (int done = 0; done < frames; done += 4096)
                {
                    int n = Math.Min(4096, frames - done);
                    for (int i = 0; i < n; i++)
                    {
                        float v = (float)(0.4 * Math.Sin(2 * Math.PI * 330 * (done + i) / Rate));
                        block[i * 2] = v;
                        block[i * 2 + 1] = v;
                    }
                    encoder.Write(block, n);
                }
                encoder.Finish();
            }
            var flac = File.ReadAllBytes(flacPath);

            // Into Ogg pages: the mapping's first packet carries "fLaC" and
            // STREAMINFO, the rest of the file follows as further packets.
            var ogg = new MemoryStream();
            uint sequence = 0;
            var first = new MemoryStream();
            first.WriteByte(0x7F); first.Write("FLAC"u8); first.WriteByte(1); first.WriteByte(0);
            first.WriteByte(0); first.WriteByte(1);
            first.Write(flac, 0, 42);
            Page(ogg, first.ToArray(), sequence++, 2);
            for (int at = 42; at < flac.Length; at += 3000)
                Page(ogg, flac.AsSpan(at, Math.Min(3000, flac.Length - at)).ToArray(), sequence++,
                    at + 3000 >= flac.Length ? 4 : 0);

            // Damage after the last good page.
            var garbage = new byte[200];
            new Random(9).NextBytes(garbage);
            ogg.Write(garbage);

            var path = Path.Combine(dir, "damaged.oga");
            File.WriteAllBytes(path, ogg.ToArray());

            var rewrapped = Rewrap.TryRewrap(path, null, out var kind);
            Check("FLAC in Ogg with damage at the end is still rewrapped (it was refused whole)", rewrapped != null, kind);
            rewrapped?.Dispose();

            var decoder = TrackDecoder.Open(path, null, 0, 0, out string why);
            Check("and it plays", decoder != null, why);
            if (decoder == null) return;
            using (decoder)
            {
                var all = ReadAll(decoder);
                double seconds = all.Count / (double)Math.Max(1, decoder.Channels) / Math.Max(1, decoder.SampleRate);
                Check($"all of it ({seconds:0.00}s of 2)", Math.Abs(seconds - 2) < 0.05 && Peak(all) > 0.3f);
            }
        }

        /// <summary>One Ogg page holding one whole packet (lacing only; no CRC, which Rewrap does not ask for).</summary>
        private static void Page(Stream into, byte[] packet, uint sequence, int flags)
        {
            var lacing = new List<byte>();
            int left = packet.Length;
            while (left >= 255) { lacing.Add(255); left -= 255; }
            lacing.Add((byte)left);
            into.Write("OggS"u8);
            into.WriteByte(0);
            into.WriteByte((byte)flags);
            into.Write(BitConverter.GetBytes(0L));
            into.Write(BitConverter.GetBytes(1234));
            into.Write(BitConverter.GetBytes(sequence));
            into.Write(BitConverter.GetBytes(0));
            into.WriteByte((byte)lacing.Count);
            into.Write(lacing.ToArray());
            into.Write(packet);
        }

        // ---------- Media Foundation reads are bounded ----------

        private static void SourceShapeTests()
        {
            var root = AppContext.BaseDirectory;
            string? source = null;
            for (var d = new DirectoryInfo(root); d != null && source == null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "src", "AudioDecoder.cs");
                if (File.Exists(candidate)) source = File.ReadAllText(candidate);
            }
            if (source == null) { Console.WriteLine("  (AudioDecoder.cs not found beside the tests)"); return; }

            // The decision, not the punctuation: a read of samples with no audio
            // in them stops after a bounded number rather than looping for ever.
            int at = source.IndexOf("private bool ReadOneSample", StringComparison.Ordinal);
            int end = at < 0 ? -1 : source.IndexOf("private void Discard", at, StringComparison.Ordinal);
            var body = at >= 0 && end > at ? source[at..end] : "";
            Check("an empty sample from Media Foundation is counted against a bound",
                body.Contains("EmptySamplesPerRead") && !body.Contains("return !_finished;"));
        }
    }
}
