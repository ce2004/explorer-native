using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of the web app.
    /// </summary>
    internal static class Sweep500WebTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the web app:");
            WebDeleteNeverAsksOnThePc();
            AudioThatStopsArrivingFails();
            LongAudioIsNotCut();
            await Task.CompletedTask;
        }

        // ---------------- helpers ----------------

        private static string? Source(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            try { return File.ReadAllText(Path.Combine(dir.FullName, relative)); } catch { return null; }
        }

        /// <summary>The text from <paramref name="start"/> up to <paramref name="end"/>, or empty.</summary>
        private static string Slice(string? text, string start, string end)
        {
            if (text == null) return "";
            int a = text.IndexOf(start, StringComparison.Ordinal);
            if (a < 0) return "";
            int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
            return b < 0 ? text[a..] : text[a..b];
        }

        // ---------------- 1. web delete never puts a question on the PC ----------------

        private static void WebDeleteNeverAsksOnThePc()
        {
            var body = Slice(Source(Path.Combine("src", "ConnectFiles.cs")), "public async Task<ConnectDeleted> DeleteAsync(", "[StructLayout");
            Check("web delete: the source of DeleteAsync was found", body.Length > 0);
            Check("web delete recycles without any window, so a file too big for the bin is kept rather than asked about on the PC",
                body.Contains("ShellDelete.RecycleWithoutUi(", StringComparison.Ordinal) &&
                !body.Contains("ShellDelete.Recycle(", StringComparison.Ordinal));
        }

        // ---------------- 2. /api/audio fails, rather than going silent, when the bytes stop ----------------

        /// <summary>Serves a file's bytes up to a cut, then fails: no bytes, or an exception.</summary>
        private sealed class CutSource : IRangeSource
        {
            private readonly byte[] _bytes;
            private readonly bool _throw;
            /// <summary>Where the bytes stop coming; set after opening, as Google stalls part way through playing.</summary>
            public long Cut = long.MaxValue;
            public int Refused;
            public CutSource(byte[] bytes, bool throws) { _bytes = bytes; _throw = throws; }
            public long Length => _bytes.Length;
            public int Streams => 1;
            public Task<int> ReadAsync(long offset, Memory<byte> into, CancellationToken token)
            {
                if (offset >= Cut)
                {
                    Refused++;
                    if (_throw) throw new IOException("Google stopped answering");
                    return Task.FromResult(0);
                }
                int n = (int)Math.Min(into.Length, Math.Min(Cut, _bytes.Length) - offset);
                _bytes.AsSpan((int)offset, n).CopyTo(into.Span);
                return Task.FromResult(n);
            }
            public void Dispose() { }
        }

        private static byte[]? OpusTone(int seconds)
        {
            try
            {
                const int rate = 48000;
                var pcm = new short[rate * seconds * 2];
                for (int i = 0; i < rate * seconds; i++)
                {
                    short v = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000);
                    pcm[2 * i] = v;
                    pcm[2 * i + 1] = v;
                }
                using var file = new MemoryStream();
                var encoder = Concentus.OpusCodecFactory.CreateEncoder(48000, 2, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO, null);
                var writer = new Concentus.Oggfile.OpusOggWriteStream(encoder, file, null, 48000, 2, false);
                writer.WriteSamples(pcm, 0, pcm.Length);
                writer.Finish();
                return file.ToArray();
            }
            catch (Exception e) { Check("an Opus tone could be made", false, e.Message); return null; }
        }

        private static void AudioThatStopsArrivingFails()
        {
            var bytes = OpusTone(20);
            if (bytes == null) return;

            using (var whole = ConnectAudio.Open("tone.opus", new CutSource(bytes, false), bytes.Length, out var why))
            {
                Check("audio: a Drive track that arrives whole opens", whole != null, why);
                if (whole != null)
                {
                    var ms = new MemoryStream();
                    string? failed = null;
                    try { whole.WriteRange(0, whole.Plan.TotalBytes - 1, ms, CancellationToken.None); }
                    catch (Exception e) { failed = e.Message; }
                    Check("audio: a track that arrives whole is served whole, with no error", failed == null && ms.Length == whole.Plan.TotalBytes,
                        $"{failed} {ms.Length} vs {whole.Plan.TotalBytes}");
                }
            }

            foreach (var throws in new[] { false, true })
            {
                string how = throws ? "with an error" : "with no bytes";
                var source = new CutSource(bytes, throws);
                using var audio = ConnectAudio.Open("tone.opus", source, bytes.Length, out var why);
                source.Cut = bytes.Length * 2 / 5;
                Check($"audio: a track that stops arriving {how} still opens", audio != null, why);
                if (audio == null) continue;
                var ms = new MemoryStream();
                Exception? caught = null;
                try { audio.WriteRange(0, audio.Plan.TotalBytes - 1, ms, CancellationToken.None); }
                catch (Exception e) { caught = e; }
                Check($"audio: a track that stops arriving {how} fails the response, so the phone retries, instead of playing silence to the end",
                    caught is IOException && ms.Length < audio.Plan.TotalBytes, $"{caught?.GetType().Name} {ms.Length} of {audio.Plan.TotalBytes}, {source.Refused} reads refused");
            }
        }

        // ---------------- 3. a long track is not cut at the 4 GB WAV limit ----------------

        private static void LongAudioIsNotCut()
        {
            const long most = uint.MaxValue - 36L;
            var three = ConnectAudio.PlanFor(48000, 2, 3L * 3600 * 48000);
            Check("audio: a three-hour track stays 24-bit stereo at its own rate",
                three.Bits == 24 && three.Channels == 2 && three.Rate == 48000 && three.Frames == 3L * 3600 * 48000, three.ToString());

            var six = ConnectAudio.PlanFor(48000, 2, 6L * 3600 * 48000);
            Check("audio: a six-hour Opus audiobook is served whole, as 16-bit, under the WAV limit",
                six.Frames / (double)six.Rate >= 6 * 3600 - 1 && six.DataBytes <= most && six.Bits == 16, six.ToString());

            var twenty = ConnectAudio.PlanFor(48000, 2, 20L * 3600 * 48000);
            Check("audio: a twenty-hour track is served whole, mono at a lower rate, under the WAV limit",
                twenty.Frames / (double)twenty.Rate >= 20 * 3600 - 1 && twenty.DataBytes <= most && twenty.Channels == 1 && twenty.Rate < 48000,
                twenty.ToString());

            var h = ConnectAudio.Header(six);
            Check("audio: a 16-bit plan's header says 16 bits, with the block and byte rate to match",
                BitConverter.ToUInt16(h, 34) == 16 && BitConverter.ToUInt16(h, 32) == six.Channels * 2 &&
                BitConverter.ToUInt32(h, 28) == six.Rate * six.Channels * 2 && BitConverter.ToUInt32(h, 40) == six.DataBytes);
        }
    }
}
