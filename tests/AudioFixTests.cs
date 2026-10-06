using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// What a review of the player found, held: damaged files of every format
    /// played, skipped and stopped without a hang, a spinning core or a file
    /// left locked; a skip after resuming that counts from the resume; a seek
    /// Media Foundation refused that leaves no dead reader behind; an Ogg file
    /// with the wrong name; and Stop while paused being as quick as Stop.
    ///
    /// Everything here plays at volume zero or decodes without a device, and
    /// everything that could hang runs under its own time limit.
    /// </summary>
    internal static class AudioFixTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        /// <summary>Damaged copies per format, and the time the fuzz may take in all.</summary>
        private const int SuiteCopies = 25;
        private const int SuitePlayerCopies = 12;
        private const int SuiteFuzzSeconds = 90;

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Player review fixes:");

            var dir = Path.Combine(Path.GetTempPath(), "ExplorerNativeAudioFix_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var fixtures = MakeFixtures(dir);

                OggWithTheWrongNameTests(dir);
                FailedSeekLeavesNoDeadReaderTests(dir);
                SkipAfterResumeTests(fixtures);
                StopWhilePausedTests(fixtures);
                ShortTrackOnRepeatTests(dir);

                int copies = SuiteCopies, playerCopies = SuitePlayerCopies, seconds = SuiteFuzzSeconds;
                if (int.TryParse(Environment.GetEnvironmentVariable("EXPLORERNATIVE_AUDIOFUZZ"), out int asked) && asked > 0)
                {
                    copies = asked;
                    playerCopies = asked;
                    seconds = 3600;
                }

                DamagedFileTests(dir, fixtures, copies, playerCopies, seconds);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }

            Console.WriteLine();
        }

        // ---------- Fixtures ----------

        private static List<string> MakeFixtures(string dir)
        {
            var files = new List<string>();
            const int rate = 44100, seconds = 8;
            var pcm = ExactSeekTests.Signal(rate, seconds);

            var wav = Path.Combine(dir, "tone.wav");
            ExactSeekTests.WriteWav(wav, pcm, rate);
            files.Add(wav);

            var flac = Path.Combine(dir, "tone.flac");
            ExactSeekTests.WriteFlac(flac, pcm, rate);
            files.Add(flac);

            var aiff = Path.Combine(dir, "tone.aiff");
            ExactSeekTests.WriteAiff(aiff, pcm, rate, "AIFF");
            files.Add(aiff);

            var opus = Path.Combine(dir, "tone.opus");
            if (ExactSeekTests.WriteOpus(opus, ExactSeekTests.Signal(48000, seconds))) files.Add(opus);

            foreach (var (name, codec) in new[]
            {
                ("tone.m4a", ExactSeekTests.MfEncoder.Aac),
                ("tone.mp3", ExactSeekTests.MfEncoder.Mp3),
                ("tone.wma", new Guid("00000161-0000-0010-8000-00AA00389B71")),
            })
            {
                var path = Path.Combine(dir, name);
                if (ExactSeekTests.MfEncoder.Write(path, pcm, rate, codec, out _)) files.Add(path);
            }

            var shipped = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            foreach (var name in new[] { "clicks.ogg", "clicks-vbr.mp3" })
            {
                var from = Path.Combine(shipped, name);
                if (!File.Exists(from)) { Check($"the {name} fixture is next to the suite", false, from); continue; }

                var to = Path.Combine(dir, name);
                File.Copy(from, to, true);
                files.Add(to);
            }

            return files;
        }

        // ---------- An Ogg file named .mp3 ----------

        private static void OggWithTheWrongNameTests(string dir)
        {
            var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "clicks.ogg");
            if (!File.Exists(source)) return;

            var misnamed = Path.Combine(dir, "really-ogg.mp3");
            File.Copy(source, misnamed, true);

            var decoder = TrackDecoder.Open(misnamed, null, 48000, 2, out string why);
            try
            {
                Check("an Ogg file named .mp3 still opens", decoder != null, why);
                if (decoder == null) return;

                Check("and it is the Ogg decoder that opened it", decoder is OggDecoder, decoder.GetType().Name);

                var buffer = new float[4096 * 2];
                int got = decoder.Read(buffer, 4096);
                Check("and it decodes", got > 0 && buffer.Take(got * 2).Any(v => v != 0), $"{got} frames");
            }
            finally { decoder?.Dispose(); }
        }

        // ---------- A seek Media Foundation refuses ----------

        /// <summary>
        /// The first 20KB of a variable-bitrate MP3, asked for a point past
        /// where its audio really ends. The reader that refused the seek was
        /// kept, and the next read into it never came back.
        /// </summary>
        private static void FailedSeekLeavesNoDeadReaderTests(string dir)
        {
            var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "clicks-vbr.mp3");
            if (!File.Exists(source)) return;

            var cut = Path.Combine(dir, "cut-vbr.mp3");
            var bytes = File.ReadAllBytes(source);
            File.WriteAllBytes(cut, bytes.Take(Math.Min(20 * 1024, bytes.Length)).ToArray());

            int afterBadSeek = -1, afterGoodSeek = -1;
            bool opened = false;

            bool finished = RunWithin(10_000, () =>
            {
                var decoder = new AudioDecoder();
                try
                {
                    if (!decoder.Open(cut, null, 48000, 2)) return;
                    opened = true;

                    var buffer = new float[4096 * 2];
                    decoder.SeekTo(6);
                    afterBadSeek = ReadSome(decoder, buffer, 20);

                    decoder.SeekTo(0.2);
                    afterGoodSeek = ReadSome(decoder, buffer, 4);
                }
                finally { decoder.Dispose(); }
            });

            Check("a truncated VBR MP3 opens", opened || !finished);
            Check("a read after a seek past its real end comes back", finished, "the read never returned");
            if (finished && opened)
                Check("and a seek back into the audio still plays", afterGoodSeek > 0,
                    $"{afterBadSeek} frames after the bad seek, {afterGoodSeek} after the good one");
        }

        private static int ReadSome(ITrackDecoder decoder, float[] buffer, int blocks)
        {
            int total = 0;
            int frames = buffer.Length / Math.Max(1, decoder.Channels);
            for (int i = 0; i < blocks; i++)
            {
                int got = decoder.Read(buffer, frames);
                if (got <= 0) break;
                total += got;
            }
            return total;
        }

        // ---------- Skipping right after resuming ----------

        private static void SkipAfterResumeTests(List<string> fixtures)
        {
            var wav = fixtures.FirstOrDefault(f => f.EndsWith(".wav"));
            if (wav == null) return;

            using var player = new AudioPlayer();
            player.VolumePercent = 0;

            // A finished track, played again, then skipped. The last skip's
            // target was the end, and it was still standing.
            if (player.Play(wav) == null) { Check("a track plays to test skipping after resume", false, player.Diagnostic); return; }
            WaitFor(() => player.DurationSeconds > 0, 5000);

            player.SeekToFraction(1);
            WaitFor(() => !player.IsPlaying, 4000);
            player.TogglePlayPause();
            Equal("a skip right after replaying a finished track counts from the start",
                AudioPlayer.SpokenTime(5), player.Seek(5));

            // Rewind on resume: paused at about six seconds, resumed five back,
            // then a skip of two. It counted from the six.
            player.RewindOnResumeSeconds = 5;
            player.SeekToFraction(0.75);
            Thread.Sleep(400);
            player.TogglePlayPause();
            Thread.Sleep(100);
            double pausedAt = player.PositionSeconds;
            player.TogglePlayPause();
            player.Seek(2);
            Thread.Sleep(500);
            double landed = player.PositionSeconds;

            Check("a skip right after a rewind on resume counts from the rewind",
                landed < pausedAt - 1.5,
                $"paused at {pausedAt:0.##}, skipped +2 after a 5s rewind, landed at {landed:0.##}");

            player.Stop();
        }

        // ---------- Stop while paused ----------

        private static void StopWhilePausedTests(List<string> fixtures)
        {
            var wav = fixtures.FirstOrDefault(f => f.EndsWith(".wav"));
            if (wav == null) return;

            using var player = new AudioPlayer();
            player.VolumePercent = 0;

            var times = new List<long>();
            for (int run = 0; run < 3; run++)
            {
                if (player.Play(wav) == null) { Check("a track plays to test Stop while paused", false, player.Diagnostic); return; }
                WaitFor(() => player.PositionSeconds > 0.1, 3000);
                player.TogglePlayPause();
                Thread.Sleep(600);

                var clock = Stopwatch.StartNew();
                player.Stop();
                times.Add(clock.ElapsedMilliseconds);
            }

            times.Sort();
            Check("Stop while paused is quick", times[1] < 60, string.Join(", ", times) + " ms");
        }

        // ---------- A sound shorter than the ring, on repeat ----------

        /// <summary>
        /// Several passes of a short sound are in the ring at once, and the
        /// clock measured back from the end by all of them: it went below
        /// zero and read 0 for as long as the sound repeated.
        /// </summary>
        private static void ShortTrackOnRepeatTests(string dir)
        {
            const int rate = 44100;
            // Shorter than the fifth of a second a local file keeps in the ring,
            // so more than one pass is always in it.
            var pcm = ExactSeekTests.Signal(rate, 1).Take(rate * 2 * 8 / 100).ToArray();
            var wav = Path.Combine(dir, "short.wav");
            ExactSeekTests.WriteWav(wav, pcm, rate);

            using var player = new AudioPlayer();
            player.VolumePercent = 0;
            player.RepeatTrack = true;
            if (player.Play(wav) == null) { Check("a short sound plays on repeat", false, player.Diagnostic); return; }
            WaitFor(() => player.DurationSeconds > 0, 3000);
            Thread.Sleep(500);

            var seen = new List<double>();
            for (int i = 0; i < 30; i++) { seen.Add(player.PositionSeconds); Thread.Sleep(67); }
            double duration = player.DurationSeconds;
            player.Stop();

            int zeros = seen.Count(s => s <= 0.0001);
            Check("a short sound on repeat shows where it is, not 0 every time",
                zeros <= 5 && seen.Max() > 0.01 && seen.All(s => s <= duration + 0.01),
                $"{zeros} of 30 at 0, up to {seen.Max():0.###}s of {duration:0.###}s: " +
                string.Join(" ", seen.Take(10).Select(s => s.ToString("0.###"))));
        }

        // ---------- Damaged files ----------

        /// <summary>
        /// Two bytes changed at random in a copy, as the review did, for every
        /// format: decoded with no device (open, read, skip three seconds,
        /// read, close), and for a share of them played at volume zero (play,
        /// skip three seconds, Stop). Nothing may outlast its time limit, keep
        /// the file open afterwards, or leave a core busy.
        /// </summary>
        private static void DamagedFileTests(string dir, List<string> fixtures, int copies, int playerCopies, int seconds)
        {
            var deadline = Stopwatch.StartNew();
            var damaged = Path.Combine(dir, "damaged");
            Directory.CreateDirectory(damaged);

            // Checked in groups of twenty, so a long run reports as it goes and
            // the suite's own stall watchdog never mistakes it for a hang.
            const int Group = 20;

            foreach (var original in fixtures)
            {
                var bytes = File.ReadAllBytes(original);
                var extension = Path.GetExtension(original);
                var stem = Path.GetFileNameWithoutExtension(original);

                using var player = new AudioPlayer();
                player.VolumePercent = 0;
                bool playerUsable = true;

                for (int first = 0; first < copies && deadline.Elapsed.TotalSeconds < seconds; first += Group)
                {
                    int last = Math.Min(copies, first + Group);
                    int decoded = 0, played = 0;
                    var trouble = new List<string>();

                    for (int i = first; i < last && deadline.Elapsed.TotalSeconds < seconds; i++)
                    {
                        string? problem = DamageOne(bytes, Path.Combine(damaged, $"{stem}-{i}{extension}"),
                            9000 + i, i < playerCopies && playerUsable ? player : null, out bool wasPlayed);

                        decoded++;
                        if (wasPlayed) played++;
                        if (problem == null) continue;

                        trouble.Add($"{stem}{extension} seed {9000 + i}: {problem}");
                        if (problem.StartsWith("play")) playerUsable = false;
                    }

                    Check($"damaged {stem}{extension}, copies {first} on: {decoded} decoded and {played} played " +
                          "without a hang, a slow Stop or the file left open",
                        trouble.Count == 0, string.Join("; ", trouble.Take(8)));
                }
            }

            // Whatever was started has finished: nothing left busy in the
            // background. A spinning decoder is a whole core.
            using var self = Process.GetCurrentProcess();
            var before = self.TotalProcessorTime;
            Thread.Sleep(1000);
            self.Refresh();
            double busy = (self.TotalProcessorTime - before).TotalMilliseconds / 1000.0;
            Check("and nothing is left spinning afterwards", busy < 0.5, $"{busy:0.00} of a core");
        }

        /// <summary>
        /// One damaged copy: two bytes changed with a fixed seed, decoded and
        /// skipped with no device, then — given a player — played, skipped and
        /// stopped. Null when all was well, or what went wrong.
        /// </summary>
        private static string? DamageOne(byte[] original, string path, int seed, AudioPlayer? player, out bool played)
        {
            played = false;

            var random = new Random(seed);
            var copy = (byte[])original.Clone();
            for (int flip = 0; flip < 2; flip++)
                copy[random.Next(copy.Length)] ^= (byte)random.Next(1, 256);
            File.WriteAllBytes(path, copy);

            bool finished = RunWithin(8000, () =>
            {
                var decoder = TrackDecoder.Open(path, null, 48000, 2, out _);
                if (decoder == null) return;
                try
                {
                    var buffer = new float[4096 * Math.Max(1, decoder.Channels)];
                    ReadSome(decoder, buffer, 6);
                    decoder.SeekTo(decoder.Position + 3);
                    ReadSome(decoder, buffer, 30);
                }
                finally { decoder.Dispose(); }
            });

            if (!finished) return "decode hung";

            if (player != null)
            {
                played = true;
                long stopMs = 0;
                bool playedOut = RunWithin(20_000, () =>
                {
                    if (player.Play(path) == null) return;
                    Thread.Sleep(150);
                    player.Seek(3);
                    Thread.Sleep(250);
                    var clock = Stopwatch.StartNew();
                    player.Stop();
                    stopMs = clock.ElapsedMilliseconds;
                });

                if (!playedOut) return "play hung";
                if (stopMs > 1000) return $"Stop took {stopMs}ms";
            }

            return Deletable(path) ? null : "the file was left open";
        }

        private static bool Deletable(string path)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try { File.Delete(path); return true; }
                catch { Thread.Sleep(50); }
            }
            return false;
        }

        // ---------- Helpers ----------

        /// <summary>Runs on a background thread and waits at most so long. False if it did not finish.</summary>
        private static bool RunWithin(int milliseconds, Action action)
        {
            var thread = new Thread(() => { try { action(); } catch { } })
            {
                IsBackground = true,
                Name = "audio fix test",
            };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            return thread.Join(milliseconds);
        }

        private static void WaitFor(Func<bool> condition, int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds && !condition()) Thread.Sleep(25);
        }
    }
}
