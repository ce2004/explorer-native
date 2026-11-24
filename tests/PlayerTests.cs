using Microsoft.Win32.SafeHandles;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System;

namespace ExplorerNative
{
    /// <summary>
    /// What the 3.0.8 sweep of the player found, held.
    ///
    /// The device was released at a floor equal to the ring's level, which the
    /// pump only reaches when the level is a whole number of its blocks away: on
    /// a 44.1kHz device every local track played in silence, and a quick device
    /// switch could do the same mid-track. And the limiter's 1.5ms attack rung
    /// was read out as "1 milliseconds", the same as the rung below it.
    /// </summary>
    internal static class Sweep308PlaybackTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Player sweep (3.0.8):");

            ReleaseFloorTests();
            AttackWordsTests();

            var dir = Path.Combine(Path.GetTempPath(), "ExplorerNativeSweep308_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                RealDeviceTests(dir);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }

            Console.WriteLine();
        }

        // ---------- The release floor against the pump's own filling rule ----------

        private static readonly int[] DeviceRates =
        {
            8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000,
            88200, 96000, 176400, 192000, 352800, 384000,
        };

        /// <summary>
        /// Where the pump leaves the ring when nothing drains it, starting from
        /// <paramref name="stored"/>: blocks of at most PumpBlockFrames, and
        /// nothing once less than SmallestWriteFrames of room is left. The same
        /// rule as WasapiPlayback.PumpCore, and a held device drains nothing.
        /// </summary>
        private static int Settle(int stored, int target)
        {
            while (true)
            {
                int room = target - stored;
                if (room < WasapiPlayback.SmallestWriteFrames) return stored;
                stored += Math.Min(room, WasapiPlayback.PumpBlockFrames);
            }
        }

        private static void ReleaseFloorTests()
        {
            var levels = WasapiPlayback.LevelsAt48k;
            var stuck = new List<string>();
            var tooLow = new List<string>();

            foreach (int rate in DeviceRates)
            {
                foreach (bool deep in new[] { false, true })
                {
                    int target = WasapiPlayback.AtRate(deep ? levels.Deep : levels.Shallow, rate);
                    int wanted = Math.Min(WasapiPlayback.AtRate(levels.Prebuffer, rate), target);
                    int floor = WasapiPlayback.ReleaseFloor(wanted, target);

                    // From an empty ring (a new track), and from every level a
                    // device switch can hold the device at, anywhere up to the
                    // level, since the old device may barely have drained it.
                    int step = Math.Max(1, target / 3000);
                    for (int start = 0; start <= target; start += step)
                        if (Settle(start, target) < floor) { stuck.Add($"{rate}Hz {(deep ? "deep" : "shallow")} from {start}"); break; }
                    for (int start = Math.Max(0, target - WasapiPlayback.SmallestWriteFrames); start <= target; start++)
                        if (Settle(start, target) < floor) { stuck.Add($"{rate}Hz {(deep ? "deep" : "shallow")} from {start}"); break; }

                    // And the floor still means something: within a pump write
                    // of what was asked for, never a token frame.
                    if (floor < wanted - WasapiPlayback.SmallestWriteFrames) tooLow.Add($"{rate}Hz: {floor} of {wanted}");
                }
            }

            Check("the ring always reaches the level that releases the device, at every device rate",
                stuck.Count == 0, string.Join("; ", stuck.Take(6)));
            Check("and the release still waits for nearly all of the prebuffer",
                tooLow.Count == 0, string.Join("; ", tooLow.Take(6)));

            // The control: the old floor, the level itself, against the same
            // rule. It must fail at 44.1kHz, or this test proves nothing.
            int t441 = WasapiPlayback.AtRate(levels.Shallow, 44100);
            int oldFloor = Math.Min(WasapiPlayback.AtRate(levels.Prebuffer, 44100), t441);
            Check("control: a floor equal to the level is never reached at 44.1kHz",
                Settle(0, t441) < oldFloor, $"settles at {Settle(0, t441)} of {oldFloor}");
        }

        // ---------- The limiter's attack, read out ----------

        private static void AttackWordsTests()
        {
            Equal("a 1.5ms attack says so",
                "1.5 milliseconds", AudioPlayer.DescribeAttack(1500).Split(',')[0]);
            Equal("and one millisecond is still singular",
                "1 millisecond", AudioPlayer.DescribeAttack(1000).Split(',')[0]);

            var said = AudioPlayer.LimiterAttackLadder.Select(a => AudioPlayer.DescribeAttack(a).Split(',')[0]).ToList();
            Check("every attack rung reads as a different number",
                said.Distinct().Count() == said.Count, string.Join(" | ", said));
        }

        // ---------- Real audio through the real device ----------

        /// <summary>
        /// A tone played at gain zero: the clock has to move, and it has to keep
        /// moving after a switch to a named device, the switch holds the device
        /// again with the ring near its level, which is the mid-track half of the
        /// release bug. On a 44.1kHz device the first check is the start half.
        /// </summary>
        private static void RealDeviceTests(string dir)
        {
            var endpoint = WasapiRenderer.DefaultEndpointId();
            if (endpoint == null)
            {
                Console.WriteLine("  (no playback device here; the real-device checks are skipped)");
                return;
            }

            var wav = Path.Combine(dir, "tone.wav");
            Tests.WriteTone(wav, 6, 0.5, 44100, 2);

            string startProblem = "", switchProblem = "";
            int deviceRate = 0;
            double beforeSwitch = 0, afterSwitch = 0;

            bool finished = RunWithin(60_000, () =>
            {
                using var playback = new WasapiPlayback { Gain = 0f };
                if (!playback.Start(wav, preferRaw: false))
                {
                    startProblem = "did not start: " + playback.Diagnostic;
                    return;
                }

                deviceRate = ParseRate(playback.Diagnostic);

                if (!WaitFor(() => playback.Position > 0.2, 4000))
                {
                    startProblem = $"the clock never moved (at {playback.Position:0.###}s)";
                    return;
                }

                bool switched = false, worked = false;
                playback.DeviceSwitched += (_, ok) => { worked = ok; switched = true; };
                playback.SwitchDevice(endpoint);
                WaitFor(() => switched, 15_000);
                if (!switched || !worked) { switchProblem = "the switch did not happen"; return; }

                Thread.Sleep(300);
                beforeSwitch = playback.Position;
                Thread.Sleep(1000);
                afterSwitch = playback.Position;
                if (afterSwitch <= beforeSwitch + 0.3)
                    switchProblem = $"the clock stood at {beforeSwitch:0.###}s then {afterSwitch:0.###}s";

                playback.Stop();
            });

            Check("a track's clock moves on this device" + (deviceRate > 0 ? $" ({deviceRate}Hz)" : ""),
                finished && startProblem.Length == 0, finished ? startProblem : "did not finish in 60 seconds");

            if (finished && startProblem.Length == 0)
                Check("and keeps moving after switching to a named device",
                    switchProblem.Length == 0, switchProblem);
        }

        private static int ParseRate(string diagnostic)
        {
            // "this application, 48000 Hz, 2 channels..."
            int hz = diagnostic.IndexOf(" Hz", StringComparison.Ordinal);
            if (hz < 0) return 0;
            int start = diagnostic.LastIndexOf(' ', hz - 1) + 1;
            return int.TryParse(diagnostic[start..hz], out int rate) ? rate : 0;
        }

        private static bool RunWithin(int milliseconds, Action action)
        {
            var thread = new Thread(() => { try { action(); } catch { } })
            {
                IsBackground = true,
                Name = "Sweep308 audio harness",
            };
            thread.Start();
            return thread.Join(milliseconds);
        }

        private static bool WaitFor(Func<bool> condition, int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                if (condition()) return true;
                Thread.Sleep(25);
            }
            return condition();
        }
    }

    // ---- merged from Sweep309PlaybackTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// What the 3.0.9 sweep of the player found, held.
    ///
    /// Resume on a track that had already played to its end started the device
    /// again with nothing ever to stop it. A seek to "not a number" threw away a
    /// skip that was still waiting, and a seek to infinity started the track
    /// again from the top. A download released and resumed fetched the same
    /// piece twice, because the old run's tidying took it off the new run's
    /// list. And the stream that asked for raw mode was not the stream that
    /// played: a second audio client was activated for that.
    /// </summary>
    internal static class Sweep309PlaybackTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Player sweep (3.0.9):");

            SeekNotANumberTests();
            StreamingGenerationTests();

            var dir = Path.Combine(Path.GetTempPath(), "ExplorerNativeSweep309_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                RealDeviceTests(dir);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }

            Console.WriteLine();
        }

        // ---------- A seek that is not a place ----------

        private static double PendingSeek(WasapiPlayback playback) =>
            (double)typeof(WasapiPlayback).GetField("_seekTo", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(playback)!;

        private static void SeekNotANumberTests()
        {
            // Nothing started, so nothing takes the seek: what is waiting is
            // exactly what the calls left there.
            using (var playback = new WasapiPlayback())
            {
                playback.SeekTo(2.0);
                playback.SeekTo(double.NaN);
                Check("a seek to NaN leaves a skip that is still waiting alone",
                    PendingSeek(playback) == 2.0, $"waiting: {PendingSeek(playback)}");

                playback.SeekTo(double.PositiveInfinity);
                Check("and a seek to infinity, with no length known, is ignored too",
                    PendingSeek(playback) == 2.0, $"waiting: {PendingSeek(playback)}");

                playback.SeekTo(-5);
                Check("a seek before the start is the start", PendingSeek(playback) == 0.0,
                    $"waiting: {PendingSeek(playback)}");
            }

            using var player = new AudioPlayer();
            var target = typeof(AudioPlayer).GetField("_seekTarget", BindingFlags.NonPublic | BindingFlags.Instance)!;
            player.SeekTo(5);
            player.SeekTo(double.NaN);
            Check("the player's own skip target survives a seek to NaN",
                (double)target.GetValue(player)! == 5.0, $"target: {target.GetValue(player)}");
        }

        // ---------- A released download, resumed ----------

        /// <summary>
        /// A source whose piece reads wait until the test lets each one go,
        /// and which pays no attention to being called off, the way a read
        /// already on the wire comes back after a release whether or not
        /// anybody still wants it. The head is answered at once.
        /// </summary>
        private sealed class GatedSource : IRangeSource
        {
            public sealed class Call
            {
                public long Offset;
                public int Length;
                public readonly TaskCompletionSource<bool> Go = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            private readonly object _gate = new();
            public readonly List<Call> Calls = new();

            public GatedSource(long length) => Length = length;

            public long Length { get; }
            public int Streams => 2;

            public async Task<int> ReadAsync(long offset, Memory<byte> into, CancellationToken token)
            {
                var call = new Call { Offset = offset, Length = into.Length };
                lock (_gate) Calls.Add(call);

                // The head: a megabyte at the very start.
                if (offset == 0 && into.Length == 1024 * 1024) return into.Length;

                await call.Go.Task.ConfigureAwait(false);
                return into.Length;
            }

            public List<Call> Pieces()
            {
                lock (_gate) return Calls.Where(c => c.Length == 4 * 1024 * 1024).ToList();
            }

            public void Dispose()
            {
                lock (_gate) foreach (var c in Calls) c.Go.TrySetResult(true);
            }
        }

        private static void StreamingGenerationTests()
        {
            const int Piece = 4 * 1024 * 1024;
            var source = new GatedSource(3L * Piece);
            var streaming = new StreamingSource(source, 64L * 1024 * 1024);

            try
            {
                // The first run's two workers take the first two pieces.
                bool firstRun = WaitFor(() => source.Pieces().Count >= 2, 10_000);
                Check("control: the first run fetches two pieces at once", firstRun,
                    $"piece reads: {source.Pieces().Count}");
                if (!firstRun) return;
                var old = source.Pieces();

                // Paused long enough to be let go of, then played again.
                streaming.Release();
                streaming.Resume();

                bool secondRun = WaitFor(() => source.Pieces().Count >= 4, 10_000);
                Check("control: the resumed run fetches its own two", secondRun,
                    $"piece reads: {source.Pieces().Count}");
                if (!secondRun) return;
                var fresh = source.Pieces().Skip(2).ToList();

                // The released run's reads come back now, late.
                foreach (var call in old) call.Go.TrySetResult(true);
                Thread.Sleep(200);

                // One of the new run's pieces lands, and its worker picks the next.
                var second = fresh.First(c => c.Offset == Piece);
                second.Go.TrySetResult(true);

                WaitFor(() => source.Pieces().Count >= 5, 5_000);
                var next = source.Pieces().Skip(4).FirstOrDefault();

                Check("after a release and a resume, the next piece is one nobody is fetching",
                    next != null && next.Offset == 2L * Piece,
                    next == null ? "no further read" : $"it fetched the piece at {next.Offset:N0} again");

                int atStart = source.Pieces().Count(c => c.Offset == 0);
                Check("and the first piece is fetched once per run, not twice in the new one",
                    atStart == 2, $"{atStart} reads of it");
            }
            finally
            {
                source.Dispose();
                streaming.Dispose();
            }
        }

        // ---------- Real audio through the real device ----------

        private static bool Suspended(WasapiPlayback playback)
        {
            var renderer = typeof(WasapiPlayback).GetField("_renderer", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(playback)!;
            return (bool)typeof(WasapiRenderer).GetField("_suspended", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(renderer)!;
        }

        private static void RealDeviceTests(string dir)
        {
            var endpoint = WasapiRenderer.DefaultEndpointId();
            if (endpoint == null)
            {
                Console.WriteLine("  (no playback device here; the real-device checks are skipped)");
                return;
            }

            DeviceListTests(endpoint);
            RawModeTests();
            ResumeAfterEndTests(dir);
            SeekPastEndTests(dir);
            LoopClockTests(dir);
        }

        private static void DeviceListTests(string endpoint)
        {
            // Every wrapper is let go of as the list is built. The list has to
            // come back the same every time, with the default in it, and the
            // default-change watcher, which holds an enumerator of its own,
            // must go on working after them.
            void Ignore(string _) { }
            WasapiRenderer.DefaultDeviceChanged += Ignore;
            try
            {
                var first = WasapiRenderer.Devices().Select(d => d.Id).ToList();
                bool same = true;
                for (int i = 0; i < 50 && same; i++)
                    same = WasapiRenderer.Devices().Select(d => d.Id).SequenceEqual(first) &&
                           WasapiRenderer.DefaultEndpointId() == endpoint;

                Check("the device list comes back the same, fifty times, with its wrappers released",
                    same && first.Count > 0, $"{first.Count} devices");
                Check("and the default is one of them",
                    first.Contains(endpoint, StringComparer.OrdinalIgnoreCase), endpoint);
            }
            finally { WasapiRenderer.DefaultDeviceChanged -= Ignore; }
        }

        private static void RawModeTests()
        {
            bool raw = false, same = false, opened = false;
            bool finished = RunWithin(60_000, () =>
            {
                using var renderer = new WasapiRenderer { Gain = 0f };
                opened = renderer.Start((buffer, frames) => 0, preferRaw: true);
                if (!opened) return;

                raw = renderer.RawMode;
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var client = typeof(WasapiRenderer).GetField("_client", flags)!.GetValue(renderer);
                var client2 = typeof(WasapiRenderer).GetField("_client2", flags)!.GetValue(renderer);
                same = client != null && ReferenceEquals(client, client2);
                renderer.Stop();
            });

            if (!finished || !opened)
            {
                Check("a renderer opens asking for raw mode", false, finished ? "did not open" : "did not finish in 60 seconds");
                return;
            }

            if (!raw)
            {
                Console.WriteLine("  (this device does not grant raw mode; the raw stream check is skipped)");
                return;
            }

            Check("raw mode is asked of the audio client that plays, not of a second one",
                same, "the stream was opened on a different client");
        }

        private static void ResumeAfterEndTests(string dir)
        {
            var wav = Path.Combine(dir, "short.wav");
            Tests.WriteTone(wav, 0.6, 0.5, 48000, 2);

            string problem = "";
            bool suspendedAtEnd = false, suspendedAfterResume = false, movedAfterSeek = false, endedAgain = false;

            bool finished = RunWithin(60_000, () =>
            {
                using var playback = new WasapiPlayback { Gain = 0f };
                if (!playback.Start(wav, preferRaw: false)) { problem = "did not start: " + playback.Diagnostic; return; }

                if (!WaitFor(() => playback.Ended, 10_000)) { problem = "never reached its end"; return; }
                Thread.Sleep(100);
                suspendedAtEnd = Suspended(playback);

                // Play pressed on a finished track, without the seek the player
                // puts in front of it, and the same as a resume that lands just
                // as the render thread decides the end.
                playback.Resume();
                Thread.Sleep(200);
                suspendedAfterResume = Suspended(playback);

                // A seek is what plays a finished track again, and it still does.
                playback.SeekTo(0);
                movedAfterSeek = WaitFor(() => !playback.Ended && playback.Position > 0.1, 5_000);
                endedAgain = WaitFor(() => playback.Ended, 10_000) && WaitFor(() => Suspended(playback), 2_000);

                playback.Stop();
            });

            if (!finished || problem.Length > 0)
            {
                Check("a short track plays to its end", false, finished ? problem : "did not finish in 60 seconds");
                return;
            }

            Check("control: the device is let go of at the end of a track", suspendedAtEnd);
            Check("resume on a track that has ended leaves the device let go of", suspendedAfterResume,
                "the device was started again with nothing to play");
            Check("and a seek after it plays the track again", movedAfterSeek);
            Check("which lets the device go again at its end", endedAgain);

            // A pause and a resume in the middle still start the device.
            var longer = Path.Combine(dir, "long.wav");
            Tests.WriteTone(longer, 6, 0.5, 48000, 2);
            bool pausedHeld = false, resumedRuns = false, clockMoves = false;
            finished = RunWithin(60_000, () =>
            {
                using var playback = new WasapiPlayback { Gain = 0f };
                if (!playback.Start(longer, preferRaw: false)) { problem = "did not start"; return; }
                if (!WaitFor(() => playback.Position > 0.2, 5_000)) { problem = "the clock never moved"; return; }

                playback.Pause();
                Thread.Sleep(100);
                pausedHeld = Suspended(playback);

                playback.Resume();
                Thread.Sleep(100);
                resumedRuns = !Suspended(playback);
                double at = playback.Position;
                clockMoves = WaitFor(() => playback.Position > at + 0.3, 3_000);
                playback.Stop();
            });

            Check("a pause in the middle stops the device and a resume starts it, and the clock moves",
                finished && problem.Length == 0 && pausedHeld && resumedRuns && clockMoves,
                finished ? $"{problem} paused {pausedHeld}, resumed {resumedRuns}, moved {clockMoves}" : "did not finish in 60 seconds");
        }

        private static void SeekPastEndTests(string dir)
        {
            var wav = Path.Combine(dir, "past.wav");
            Tests.WriteTone(wav, 4, 0.5, 44100, 2);

            string problem = "";
            bool ended = false;
            double at = 0;

            bool finished = RunWithin(60_000, () =>
            {
                using var playback = new WasapiPlayback { Gain = 0f };
                if (!playback.Start(wav, preferRaw: false)) { problem = "did not start"; return; }
                if (!WaitFor(() => playback.Position > 0.1, 5_000)) { problem = "the clock never moved"; return; }

                // Far past the end. A frame count this big overflows, and the
                // decoders read the overflow as the start of the track.
                playback.SeekTo(1e300);
                ended = WaitFor(() => playback.Ended, 3_000);
                at = playback.Position;
                playback.Stop();
            });

            Check("a seek far past the end ends the track rather than starting it again",
                finished && problem.Length == 0 && ended,
                finished ? $"{problem} at {at:0.###}s, ended {ended}" : "did not finish in 60 seconds");
        }

        /// <summary>
        /// The clock through repeats with every stage of the chain doing work:
        /// speed, an independent pitch shift and silence reduction. It has to
        /// run from near the start to near the end of each pass and never go
        /// back except at the turn.
        /// </summary>
        private static void LoopClockTests(string dir)
        {
            var wav = Path.Combine(dir, "loop.wav");
            Tests.WriteTone(wav, 2, 0.5, 48000, 2);

            string problem = "";
            var samples = new List<(long At, double Position)>();
            double duration = 0;

            bool finished = RunWithin(60_000, () =>
            {
                using var playback = new WasapiPlayback
                {
                    Gain = 0f,
                    Rate = 1.5,
                    PitchRatio = Resampler.RatioForSemitones(4),
                    SilenceOn = true,
                    Loop = true,
                };
                if (!playback.Start(wav, preferRaw: false)) { problem = "did not start"; return; }
                if (!WaitFor(() => playback.Position > 0.1, 5_000)) { problem = "the clock never moved"; return; }
                duration = playback.Duration;

                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 6_000)
                {
                    samples.Add((clock.ElapsedMilliseconds, playback.Position));
                    Thread.Sleep(10);
                }
                playback.Stop();
            });

            if (!finished || problem.Length > 0)
            {
                Check("a repeating track's clock is measured", false, finished ? problem : "did not finish in 60 seconds");
                return;
            }

            int turns = 0;
            var wrong = new List<string>();
            for (int i = 1; i < samples.Count; i++)
            {
                double before = samples[i - 1].Position, now = samples[i].Position;
                if (now < before - duration / 2)
                {
                    turns++;
                    if (before < duration - 0.3) wrong.Add($"turned at {before:0.###}s");
                    if (now > 0.3) wrong.Add($"started the pass at {now:0.###}s");
                    continue;
                }
                if (now < before - 0.02) wrong.Add($"went back from {before:0.###}s to {now:0.###}s");
                if (now > before + 0.3) wrong.Add($"jumped from {before:0.###}s to {now:0.###}s");
            }

            Check("the clock turns round at every repeat with speed, pitch and silence reduction on",
                turns >= 2, $"{turns} turns in six seconds of a two-second track at one and a half times");
            Check("and runs from start to end of each pass, never back and never jumping",
                wrong.Count == 0, string.Join("; ", wrong.Take(5)));
        }

        private static bool RunWithin(int milliseconds, Action action)
        {
            var thread = new Thread(() => { try { action(); } catch { } })
            {
                IsBackground = true,
                Name = "Sweep309 audio harness",
            };
            thread.Start();
            return thread.Join(milliseconds);
        }

        private static bool WaitFor(Func<bool> condition, int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                if (condition()) return true;
                Thread.Sleep(10);
            }
            return condition();
        }
    }

    // ---- merged from Sweep500PlayerTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// The 5.0.0 sweep of the audio player.
    /// </summary>
    internal static class Sweep500PlayerTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the audio player:");

            var dir = Path.Combine(Path.GetTempPath(), "en-sweep500player-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                await WithWatchdog("a WAV over 4GB played through a download", () => BigWavThroughDownloadTests(dir));
                await WithWatchdog("a share or Drive that stops answering", () => NotAnsweringTests(dir));
                await WithWatchdog("a track still opening is the one loaded", () => OpeningTrackIsTheLoadedOneTests(dir));
                OpeningTrackWiringTests();
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static async Task WithWatchdog(string what, Action body)
        {
            var run = Task.Run(body);
            if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60))) != run)
            {
                Check($"{what} within 60 seconds", false, "the watchdog fired");
                return;
            }
            if (run.Exception != null)
                Check($"{what} ran to the end", false, run.Exception.InnerException?.ToString() ?? run.Exception.ToString());
        }

        // ---------- A track still opening ----------

        /// <summary>
        /// While a new track opened, the player still named the old one: a
        /// second Next track stepped from it again and replayed the same track,
        /// and "what is playing" said nothing was.
        /// </summary>
        private static void OpeningTrackIsTheLoadedOneTests(string dir)
        {
            var a = Path.Combine(dir, "a.wav");
            var b = Path.Combine(dir, "b.wav");
            var c = Path.Combine(dir, "c.wav");
            Tests.WriteTone(a, 4, 0.3);
            Tests.WriteTone(b, 4, 0.3);
            Tests.WriteTone(c, 4, 0.3);

            using var player = new AudioPlayer();
            player.VolumePercent = 0;
            if (player.Play(a) == null)
            {
                Console.WriteLine("  (no output device; the opening-track checks are skipped: " + player.Diagnostic + ")");
                return;
            }

            using var reached = new System.Threading.ManualResetEventSlim();
            using var release = new System.Threading.ManualResetEventSlim();
            player.OpenHeldForTests = () =>
            {
                player.OpenHeldForTests = null;
                reached.Set();
                release.Wait(TimeSpan.FromSeconds(20));
            };
            var opening = Task.Run(() => player.Play(b));
            try
            {
                Check("the start of the next track is reached", reached.Wait(TimeSpan.FromSeconds(10)));
                Equal("while the next track opens, the track before is no longer named as playing", "", player.NowPlayingPath);
                Equal("while the next track opens, it is what Next and Previous step from", b, player.OpeningOrPlayingPath);
                Check("while the next track opens, what is playing says it is opening, not that nothing is",
                    player.Describe().StartsWith("Still opening"), player.Describe());
                Check("while the next track opens, the time elapsed says it is opening",
                    player.DescribeElapsed().StartsWith("Still opening"), player.DescribeElapsed());
                Equal("while the next track opens, a skip says it is still opening rather than a time", "Still opening", player.Seek(10));
                Equal("while the next track opens, back to the start says it is still opening", "Still opening", player.SeekToFraction(0));
            }
            finally
            {
                release.Set();
            }
            Check("the next track then plays", opening.Wait(TimeSpan.FromSeconds(20)) && opening.Result != null, opening.IsCompleted ? opening.Result : "still opening");
            Equal("and is then the one playing", b, player.NowPlayingPath);

            // Enter on another track, before its worker starts it: it counts as
            // opening at once, and the track playing is left playing meanwhile.
            player.CancelOpening(c);
            Equal("a track asked for counts as opening before its start begins", c, player.OpeningOrPlayingPath);
            Equal("and the track playing still plays until then", b, player.NowPlayingPath);
            Check("the track asked for then plays", player.Play(c) != null, player.Diagnostic);
            Equal("and is the one playing", c, player.NowPlayingPath);
            Equal("and nothing is left opening", c, player.OpeningOrPlayingPath);
            player.Stop();
        }

        /// <summary>The tray side, which the suite does not compile: Next and Previous track and the tag editor see the track opening.</summary>
        private static void OpeningTrackWiringTests()
        {
            var tray = Source("TrayApplicationContext.cs");
            if (tray == null) { Check("TrayApplicationContext.cs is found for the wiring checks", false); return; }
            int state = tray.IndexOf("AudioSnapshot AudioState", StringComparison.Ordinal);
            int end = state < 0 ? -1 : tray.IndexOf("private AudioSnapshot AudioStateOf", state, StringComparison.Ordinal);
            Check("the tray's audio state names the track opening, so Next track steps on from it",
                state >= 0 && end > state && tray.Substring(state, end - state).Contains("OpeningOrPlayingPath"));
            Check("Enter marks its track as opening as it calls off the one before",
                System.Text.RegularExpressions.Regex.IsMatch(tray, @"_audio\.CancelOpening\(\s*path\s*\)"));
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        // ---------- A share or Drive that stops answering ----------

        /// <summary>
        /// A track on a share that stopped answering, or on Drive while it is
        /// offline, was said as "is no longer there" (File.Exists answers false
        /// for a share it cannot reach) and handed to another app.
        /// </summary>
        private static void NotAnsweringTests(string dir)
        {
            var here = Path.Combine(dir, "here.wav");
            File.WriteAllBytes(here, new byte[16]);
            var gone = Path.Combine(dir, "gone.flac");
            var goneFolder = Path.Combine(dir, "no-such-folder", "gone.flac");
            var deadShare = @"\\localhost\no-such-share-" + Guid.NewGuid().ToString("N") + @"\Music\song.flac";

            Equal("a file that is there is There", "There", AudioPlayer.WhereIs(here).ToString());
            Equal("a file deleted from a folder that answers is Gone", "Gone", AudioPlayer.WhereIs(gone).ToString());
            Equal("a file whose folder went from a drive that answers is Gone", "Gone", AudioPlayer.WhereIs(goneFolder).ToString());
            Equal("a file on a share that does not answer is NotAnswering, not Gone", "NotAnswering", AudioPlayer.WhereIs(deadShare).ToString());

            var was = AudioPlayer.DriveUnreachable;
            try
            {
                AudioPlayer.DriveUnreachable = p => p.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
                Equal("a file on Drive while Drive is offline is NotAnswering", "NotAnswering", AudioPlayer.WhereIs(gone).ToString());
                AudioPlayer.DriveUnreachable = null;

                // End to end: what is said, and whether it is handed to another app.
                using var player = new AudioPlayer();
                player.VolumePercent = 0;
                var said = new System.Collections.Generic.List<(string id, string text)>();
                bool handedOver = false;
                player.Notify = (id, text) => { lock (said) said.Add((id, text)); };
                player.Failed += _ => handedOver = true;
                var answer = player.Play(deadShare);
                string all;
                lock (said) all = string.Join(" | ", said.ConvertAll(x => x.id + ": " + x.text));
                if (all.Contains("No audio output"))
                {
                    Console.WriteLine("  (no output device; the end-to-end share check is skipped)");
                    return;
                }
                Check("a track on a share that does not answer does not start", answer == null, answer);
                Check("a track on a share that does not answer is not said to be gone", !all.Contains("audiofile.missing"), all);
                Check("a track on a share that does not answer says the share isn't answering",
                    all.Contains("audio.failed") && all.Contains("isn't answering"), all);
                Check("a track on a share that does not answer is not handed to another app", !handedOver, all);
            }
            finally
            {
                AudioPlayer.DriveUnreachable = was;
            }
        }

        // ---------- A WAV over 4GB on a share or Drive ----------

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize,
            IntPtr output, int outputSize, out int returned, IntPtr overlapped);

        private const uint FSCTL_SET_SPARSE = 0x000900C4;
        private const int Rate = 44100;

        /// <summary>
        /// Every remote track is decoded through a download (a StreamingSource),
        /// and the 3.0.9 big-WAV reader was only tried without one, so a 5GB
        /// recording on the NAS stopped at its wrapped size.
        /// </summary>
        private static void BigWavThroughDownloadTests(string dir)
        {
            var path = Path.Combine(dir, "long.wav");
            const long length = (1L << 32) + 4 * 1024 * 1024;
            long dataBytes = length - 44;
            try
            {
                using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
                if (!DeviceIoControl(file.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                {
                    Console.WriteLine("  (this volume cannot make sparse files; the 4GB WAV test is skipped)");
                    return;
                }
                using var w = new BinaryWriter(file, Encoding.ASCII, leaveOpen: true);
                w.Write("RIFF"u8); w.Write((uint)((length - 8) & 0xFFFFFFFF)); w.Write("WAVE"u8);
                w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate);
                w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
                w.Write("data"u8); w.Write((uint)(dataBytes & 0xFFFFFFFF));
                file.SetLength(length);
                file.Position = length - Rate * 4L;
                for (int i = 0; i < Rate; i++)
                {
                    short v = (short)Math.Round(10000 * Math.Sin(2 * Math.PI * 500 * i / Rate));
                    w.Write(v); w.Write(v);
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine("  (no room for the sparse 4GB WAV: " + ex.Message + ")");
                return;
            }

            double expected = dataBytes / 4 / (double)Rate;
            using (var source = new StreamingSource(path, 16L << 20))
            {
                var decoder = TrackDecoder.Open(path, source, 0, 0, out string why);
                Check("a WAV over 4GB opens through a download", decoder != null, why);
                if (decoder != null)
                {
                    using (decoder)
                    {
                        Check($"a WAV over 4GB played through a download is as long as the file ({decoder.Duration:0.0}s, expected {expected:0.0}s)",
                            Math.Abs(decoder.Duration - expected) < 0.01, decoder.GetType().Name);
                    }
                }
            }
            try { File.Delete(path); } catch { }
        }
    }

    // ---- merged from Sweep500Player2Tests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// The 5.0.0 sweep of the audio output and effect dialogs.
    /// </summary>
    internal static class Sweep500Player2Tests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the audio output and effect dialogs:");
            EffectDialogAccessKeyTests();
            WholeRangeKeepsTheVolumeTests();

            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "en500-player2-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                ResetDeviceIsReopenedTests(dir);
                SlowOpenKeepsPlayingTests(dir);
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
            await Task.CompletedTask;
        }

        // ---- helpers ----

        private static string OnSta(Action work, int seconds = 60)
        {
            string error = "";
            var thread = new Thread(() =>
            {
                try { work(); }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(seconds))) return "timed out";
            return error;
        }

        private static List<Control> FindAll(Control root)
        {
            var found = new List<Control>();
            foreach (Control child in root.Controls)
            {
                found.Add(child);
                found.AddRange(FindAll(child));
            }
            return found;
        }

        private static char? AccessKey(Control control)
        {
            bool mnemonic = control is ButtonBase b ? b.UseMnemonic : control is Label l && l.UseMnemonic;
            if (!mnemonic) return null;
            var text = control.Text ?? "";
            for (int i = 0; i < text.Length - 1; i++)
            {
                if (text[i] != '&') continue;
                if (text[i + 1] == '&') { i++; continue; }
                return char.ToUpperInvariant(text[i + 1]);
            }
            return null;
        }

        private static IEnumerable<string> Clashes(string dialog, Control root)
        {
            var byKey = new Dictionary<char, List<string>>();
            foreach (var c in FindAll(root))
            {
                if (AccessKey(c) is not { } key) continue;
                if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<string>();
                list.Add(c.Text.Replace("&", ""));
            }
            return byKey.Where(p => p.Value.Count > 1)
                .Select(p => $"{dialog}: Alt+{p.Key}: {string.Join(" and ", p.Value)}");
        }

        private static bool PressAlt(Form form, char key)
        {
            var method = typeof(ContainerControl).GetMethod("ProcessMnemonic",
                BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(char) }, null)!;
            return (bool)method.Invoke(form, new object[] { key })!;
        }

        // ---- bugs 1, 2 and 3: access keys in the effect dialogs ----

        /// <summary>
        /// Alt+O pressed OK only if nothing before OK in the tab order used O,
        /// and WinForms gives a shared key to the next control after the focus.
        /// The on boxes used O (so Alt+O switched the effect instead of
        /// pressing OK), and the Limiter's Ceiling used C (Cancel threw the
        /// tuning away) and Same lift used A (it toggled instead of moving to
        /// Attack). Every key in these dialogs must be its own.
        /// </summary>
        private static void EffectDialogAccessKeyTests()
        {
            var clashes = new List<string>();
            bool eqStillOn = false, eqClosedOk = false;
            bool limiterOnKept = false, limiterOk = false, ceilingFocused = false, limiterOpen = false;
            string error = OnSta(() =>
            {
                using (var eq = new EqualiserForm(new int[Equaliser.BandCount], true, (_, _) => { }, _ => { }))
                    clashes.AddRange(Clashes("Equaliser", eq));
                using (var silence = new SilenceForm(-48, 400, 150, 20, 20, true, (_, _, _, _, _, _) => { }))
                    clashes.AddRange(Clashes("Silence reduction", silence));
                using (var limiter = new LimiterForm(500, 250, 90, false, true, (_, _, _, _, _) => { }))
                    clashes.AddRange(Clashes("Limiter", limiter));
                using (var ambience = new AmbienceForm(new AmbienceSettings(), true, (_, _) => { }))
                    clashes.AddRange(Clashes("Ambience", ambience));

                // The behaviour itself: Alt+O from a slider presses OK and
                // leaves the equaliser on.
                using (var eq = new EqualiserForm(new int[Equaliser.BandCount], true, (_, _) => { }, _ => { }))
                {
                    eq.StartPosition = FormStartPosition.Manual;
                    eq.Location = new System.Drawing.Point(-3000, -3000);
                    eq.ShowInTaskbar = false;
                    eq.Show();
                    Application.DoEvents();
                    var slider = FindAll(eq).OfType<TrackBar>().First();
                    slider.Focus();
                    eq.ActiveControl = slider;
                    PressAlt(eq, 'o');
                    Application.DoEvents();
                    eqStillOn = eq.EqualiserOn;
                    eqClosedOk = eq.DialogResult == DialogResult.OK;
                }

                // Alt+C from the Ceiling list's neighbour comes back to the
                // list instead of pressing Cancel; Alt+O presses OK.
                using (var limiter = new LimiterForm(500, 250, 90, false, true, (_, _, _, _, _) => { }))
                {
                    limiter.StartPosition = FormStartPosition.Manual;
                    limiter.Location = new System.Drawing.Point(-3000, -3000);
                    limiter.ShowInTaskbar = false;
                    limiter.Show();
                    Application.DoEvents();
                    var lists = FindAll(limiter).OfType<ComboBox>().ToList();
                    var boxes = FindAll(limiter).OfType<CheckBox>().ToList();
                    var ceiling = lists[2];
                    var after = boxes[0];
                    after.Focus();
                    limiter.ActiveControl = after;
                    PressAlt(limiter, limiter.Controls.OfType<Label>()
                        .First(l => l.Text.Replace("&", "") == "Ceiling:").Text
                        is var t && t.IndexOf('&') >= 0 ? char.ToLowerInvariant(t[t.IndexOf('&') + 1]) : 'c');
                    Application.DoEvents();
                    ceilingFocused = limiter.ActiveControl == ceiling;
                    limiterOpen = limiter.DialogResult == DialogResult.None;

                    lists[0].Focus();
                    limiter.ActiveControl = lists[0];
                    PressAlt(limiter, 'o');
                    Application.DoEvents();
                    limiterOnKept = boxes.All(b => b.AccessibleName != "Limiter on" || b.Checked);
                    limiterOk = limiter.DialogResult == DialogResult.OK;
                }
            });
            Equal("the effect dialogs build without an error", "", error);
            Equal("the Equaliser, Silence reduction and Limiter dialogs share no access keys",
                "", string.Join("; ", clashes));
            Check("Alt+O from an equaliser slider leaves the equaliser on", eqStillOn);
            Check("Alt+O from an equaliser slider presses OK", eqClosedOk);
            Check("the Ceiling access key from the Same lift box moves to the Ceiling list", ceilingFocused);
            Check("the Ceiling access key does not press Cancel", limiterOpen);
            Check("Alt+O from the Attack list leaves the limiter on", limiterOnKept);
            Check("Alt+O from the Attack list presses OK", limiterOk);
        }

        // ---- bug 4: a picked device that Windows resets ----

        private static bool RunWithin(int milliseconds, Action action)
        {
            var thread = new Thread(() => { try { action(); } catch { } })
            { IsBackground = true, Name = "Sweep500 player2 audio harness" };
            thread.Start();
            return thread.Join(milliseconds);
        }

        private static bool WaitFor(Func<bool> condition, int milliseconds)
        {
            long until = Environment.TickCount64 + milliseconds;
            while (Environment.TickCount64 < until)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }
            return condition();
        }

        private static WasapiRenderer RendererOf(WasapiPlayback playback) =>
            (WasapiRenderer)typeof(WasapiPlayback).GetField("_renderer",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(playback)!;

        private static void Lose(WasapiRenderer renderer) =>
            typeof(WasapiRenderer).GetMethod("RaiseLost", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(renderer, null);

        /// <summary>
        /// Windows invalidates a stream when a device is reset (format change,
        /// Bluetooth hands-free, driver restart, sleep) as well as when it is
        /// pulled out. A picked device lost that way went straight to the
        /// default for the rest of the track. It must be opened again by name
        /// first, and only once in a while, so one that keeps going quiet
        /// ends on the default rather than in a loop.
        /// </summary>
        private static void ResetDeviceIsReopenedTests(string dir)
        {
            var endpoint = WasapiRenderer.DefaultEndpointId();
            if (endpoint == null)
            {
                Console.WriteLine("  (no playback device here; the reset-device checks are skipped)");
                return;
            }

            var wav = System.IO.Path.Combine(dir, "reset.wav");
            Tests.WriteTone(wav, 20, 0.5, 44100, 2);

            string problem = "";
            string afterFirst = "?", afterSecond = "?";
            int switchesAfterSecond = 0;
            bool finished = RunWithin(60_000, () =>
            {
                using var playback = new WasapiPlayback { Gain = 0f };
                if (!playback.Start(wav, preferRaw: false)) { problem = "did not start: " + playback.Diagnostic; return; }
                if (!WaitFor(() => playback.Position > 0.2, 4000)) { problem = "the clock never moved"; return; }

                int switches = 0;
                playback.DeviceSwitched += (_, ok) => { if (ok) Interlocked.Increment(ref switches); };
                playback.SwitchDevice(endpoint);
                if (!WaitFor(() => Volatile.Read(ref switches) >= 1, 15_000)) { problem = "the pick never landed"; return; }
                if (RendererOf(playback).DeviceId != endpoint) { problem = "the pick was not by name"; return; }

                Lose(RendererOf(playback));
                if (!WaitFor(() => Volatile.Read(ref switches) >= 2, 15_000)) { problem = "nothing took over after the reset"; return; }
                afterFirst = RendererOf(playback).DeviceId;

                Lose(RendererOf(playback));
                if (!WaitFor(() => Volatile.Read(ref switches) >= 3, 15_000)) { problem = "nothing took over after the second reset"; return; }
                afterSecond = RendererOf(playback).DeviceId;
                Thread.Sleep(3000);
                switchesAfterSecond = Volatile.Read(ref switches) - 3;
                playback.Stop();
            });

            Check("the reset-device harness finished", finished && problem.Length == 0,
                finished ? problem : "did not finish in 60 seconds");
            Check("a picked device that Windows resets is opened again by name, not swapped for the default",
                afterFirst == endpoint, $"device after the reset: \"{afterFirst}\"");
            Check("a picked device reset twice within 30 seconds ends on the default",
                afterSecond == "", $"device after the second reset: \"{afterSecond}\"");
            Check("and nothing keeps switching after that", switchesAfterSecond == 0,
                $"{switchesAfterSecond} more switches");
        }

        // ---- bug 5: a slow device open ----

        /// <summary>
        /// The new device opens on the pump thread, the only thing that fills
        /// the ring, and a local track keeps only 0.2s there. While a slow
        /// Bluetooth endpoint opened, the old device played that and then
        /// silence, said as "Buffering". It must keep playing through the open.
        /// </summary>
        private static void SlowOpenKeepsPlayingTests(string dir)
        {
            var endpoint = WasapiRenderer.DefaultEndpointId();
            if (endpoint == null)
            {
                Console.WriteLine("  (no playback device here; the slow-open checks are skipped)");
                return;
            }

            var wav = System.IO.Path.Combine(dir, "slow.wav");
            Tests.WriteTone(wav, 8, 0.5, 44100, 2);

            string problem = "";
            double early = 0, late = 0;
            bool starvedBeforeSwitch = false;
            bool finished = RunWithin(60_000, () =>
            {
                using var playback = new WasapiPlayback { Gain = 0f };
                if (!playback.Start(wav, preferRaw: false)) { problem = "did not start: " + playback.Diagnostic; return; }
                if (!WaitFor(() => playback.Position > 0.3, 4000)) { problem = "the clock never moved"; return; }

                bool switched = false;
                playback.Starved += starving => { if (starving && !Volatile.Read(ref switched)) starvedBeforeSwitch = true; };
                playback.DeviceSwitched += (_, _) => Volatile.Write(ref switched, true);
                WasapiPlayback.BeforeSwitchOpenForTests = () => Thread.Sleep(1500);
                try
                {
                    playback.SwitchDevice(endpoint);
                    Thread.Sleep(100);
                    early = playback.Position;
                    Thread.Sleep(1200);
                    late = playback.Position;
                    if (!WaitFor(() => Volatile.Read(ref switched), 15_000)) problem = "the switch never finished";
                }
                finally { WasapiPlayback.BeforeSwitchOpenForTests = null; }
                playback.Stop();
            });

            Check("the slow-open harness finished", finished && problem.Length == 0,
                finished ? problem : "did not finish in 60 seconds");
            Check($"the old device keeps playing while a slow device opens ({early:0.##}s to {late:0.##}s)",
                late - early >= 1.0);
            Check("and nothing says Buffering while it opens", !starvedBeforeSwitch);
        }

        // ---- bug 6: Same lift at any volume and the volume control ----

        private static float SettledWide(float amplitude, float gain, int seconds = 3)
        {
            var renderer = new WasapiRenderer { Gain = gain, Limiter = true, LimiterWholeRange = true };
            var block = new float[4096];
            float last = 0f;
            int blocks = seconds * 48000 / block.Length;
            for (int b = 0; b < blocks; b++)
            {
                for (int i = 0; i < block.Length; i++) block[i] = amplitude;
                renderer.ApplyGainAndMeasure(block, block.Length);
                last = block[^1];
            }
            return last;
        }

        /// <summary>
        /// The limiter measured the music after the volume and aimed it at the
        /// ceiling, so with "Same lift at any volume" on, turning down (or a
        /// fade) was lifted straight back up to 0.9. The volume must stay a
        /// plain multiply on top of the lift gain 1 would give.
        /// </summary>
        private static void WholeRangeKeepsTheVolumeTests()
        {
            float loudQuiet = SettledWide(0.8f, 0.25f);
            Check($"whole range at 50 percent volume leaves a loud passage turned down ({loudQuiet:0.###})",
                loudQuiet <= 0.25f * 0.91f && loudQuiet >= 0.25f * 0.8f);

            float q1 = SettledWide(0.002f, 1f), q4 = SettledWide(0.002f, 0.25f);
            Check($"whole range gives the same lift at a quarter gain, so the volume still scales it ({q4 / q1:0.###})",
                Math.Abs(q4 / q1 - 0.25f) < 0.02f);

            var renderer = new WasapiRenderer { Gain = 1f, Limiter = true, LimiterWholeRange = true };
            var block = new float[1200];
            for (int b = 0; b < 3 * 48000 / block.Length; b++)
            {
                Array.Fill(block, 0.8f);
                renderer.ApplyGainAndMeasure(block, block.Length);
            }
            float settled = Math.Abs(block[^1]);
            var peaks = new List<float>();
            for (int step = 0; step <= 40; step++)
            {
                renderer.Gain = 1f - step / 40f;
                Array.Fill(block, 0.8f);
                renderer.ApplyGainAndMeasure(block, block.Length);
                peaks.Add(block.Max(Math.Abs));
            }
            bool falling = true;
            for (int i = 1; i < peaks.Count; i++) if (peaks[i] > peaks[i - 1] + 1e-4f) falling = false;
            Check($"a fade out with whole range on is halfway down at half time ({peaks[20]:0.###} of {settled:0.###})",
                peaks[20] <= settled * 0.6f);
            Check("a fade out with whole range on never gets louder on the way down", falling);
        }
    }

    // ---- merged from Release510PlaybackTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// 5.1.0: the player, decoders and the audio editor.
    /// </summary>
    internal static class Release510PlaybackTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("5.1.0, the player, decoders and the audio editor:");
            await Task.CompletedTask;

            var dir = Path.Combine(Path.GetTempPath(), "en510-playback-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                RestartKeepsDownload(dir);
                PendingLetGo(dir);
                ReleaseWords();
                OddlyNamedFiles(dir);
                SourceShapes();
                TempFiles(dir);
                AppleLossless(dir);
                Tags();
                TypeChange();
                MonoOpus(dir);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static T Field<T>(object on, string name) =>
            (T)on.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(on)!;

        private static void SetField(object on, string name, object? value) =>
            on.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(on, value);

        private static StreamingSource FullDownload(string dir, string name)
        {
            var file = Path.Combine(dir, name);
            File.WriteAllBytes(file, new byte[200_000]);
            var source = new StreamingSource(file, 1 << 20);
            for (int i = 0; i < 100 && !source.Complete; i++) Thread.Sleep(50);
            return source;
        }

        // 1: Enter on the track already playing keeps its finished download.
        private static void RestartKeepsDownload(string dir)
        {
            using var player = new AudioPlayer();
            var download = FullDownload(dir, "a.flac");
            Check("the test download finished", download.Complete);
            SetField(player, "_streaming", download);
            SetField(player, "_path", @"\\server\share\a.flac");

            Check("the same track again keeps its finished download",
                ReferenceEquals(player.KeepableDownload(@"\\SERVER\share\a.flac"), download));
            Check("another track does not take it", player.KeepableDownload(@"\\server\share\b.flac") == null);

            download.Release();
            Check("a download that was let go is not reused", player.KeepableDownload(@"\\server\share\a.flac") == null);
            SetField(player, "_streaming", null);
            download.Dispose();
        }

        // 4: the cursor back on the playing track lets go of a download readied for another row.
        private static void PendingLetGo(string dir)
        {
            using var player = new AudioPlayer();
            var pending = FullDownload(dir, "other.flac");
            SetField(player, "_path", @"\\server\share\playing.flac");
            SetField(player, "_pending", pending);
            SetField(player, "_pendingPath", @"\\server\share\other.flac");

            player.Prepare(@"\\server\share\playing.flac");
            Check("resting on the playing track drops the readied download",
                Field<object?>(player, "_pending") == null);
        }

        // 5: the release message.
        private static void ReleaseWords()
        {
            Equal("one minute is not minutes", "Released 3 MB after 1 minute paused",
                AudioPlayer.ReleaseMessage("3 MB", 1, false));
            Equal("a finished track is not called paused", "Released 3 MB, 10 minutes after the track finished",
                AudioPlayer.ReleaseMessage("3 MB", 10, true));
        }

        // 6: a WAV saved with an Ogg name still plays.
        private static void OddlyNamedFiles(string dir)
        {
            foreach (var ext in new[] { ".ogg", ".opus" })
            {
                var path = Path.Combine(dir, "mislabelled" + ext);
                Tests.WriteTone(path, 0.5, 0.5, 44100, 2);
                var decoder = TrackDecoder.Open(path, null, 0, 0, out var why);
                Check($"a WAV named {ext} is decoded anyway", decoder != null, why);
                if (decoder != null)
                {
                    var block = new float[4096 * Math.Max(1, decoder.Channels)];
                    int got = decoder.Read(block, 4096);
                    Check($"and gives sound ({ext})", got > 0 && block.Take(got).Any(v => Math.Abs(v) > 0.01f));
                    decoder.Dispose();
                }
            }
        }

        private static string SourceText(string name)
        {
            var at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at != null && !Directory.Exists(Path.Combine(at.FullName, "src"))) at = at.Parent;
            if (at == null) return "";
            var path = Path.Combine(at.FullName, "src", name);
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch { return ""; }
        }

        // 2, 3, 7, 12, 14: form and player code that a test cannot drive without a window or a device.
        private static void SourceShapes()
        {
            var player = SourceText("AudioPlayer.cs");
            Check("a track that installs paused starts the release clock",
                player.Contains("direct.Pause();\n                ArmRelease();") ||
                player.Contains("direct.Pause();\r\n                ArmRelease();"));
            Check("a new track clears the last track's release clock",
                player.Contains("if (keep == null) StopStreaming();") && player.Contains("CancelRelease();"));
            Check("Back to the start does not need a length",
                player.Contains("unknownLength && fraction > 0"));

            var silence = SourceText("SilenceForm.cs");
            Check("Defaults stops the one-row speech", silence.Contains("_settle.Stop();\n                _spoke = null;") ||
                silence.Contains("_settle.Stop();\r\n                _spoke = null;"));

            Check("Convert audio tabs to Convert before Cancel", SourceText("AudioConvertForm.cs").Contains("ok.TabIndex = 0;"));
            Check("the shortcuts page tabs to OK first", SourceText("ShortcutsForm.cs").Contains("ok.TabIndex = 0;"));
            Check("the speech page tabs in screen order", SourceText("SpeechForm.cs").Contains("preview.TabIndex = 0;"));
            Check("the pitch page tabs to the tempo box under the list", SourceText("PitchForm.cs").Contains("tempoBox.TabIndex = 2;"));

            var editor = SourceText("AudioEditorForm.cs");
            Check("closing the editor cancels the measuring", editor.Contains("measuring.Cancel()"));
            Check("the editor tells ALAC from AAC", editor.Contains("IsAppleLossless"));
        }

        // 11: stale temp files are swept, fresh ones left, and sync skips them.
        private static void TempFiles(string dir)
        {
            var folder = Path.Combine(dir, "music");
            Directory.CreateDirectory(folder);
            var old = Path.Combine(folder, ".converting-aaaa1111.flac");
            var fresh = Path.Combine(folder, ".converting-bbbb2222.flac");
            var song = Path.Combine(folder, "song.flac");
            foreach (var f in new[] { old, fresh, song }) File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));

            AudioConverter.SweepStaleTemps(new[] { folder });
            Check("an old half-written temp file is swept", !File.Exists(old));
            Check("a recent one (another conversion may be writing it) stays", File.Exists(fresh));
            Check("a real song is never touched", File.Exists(song));

            Check("sync does not upload a temp file", SyncPlanner.IsSkipped(".converting-aaaa1111.flac"));
            Check("sync still takes a real flac", !SyncPlanner.IsSkipped("song.flac"));
        }

        // 13: Apple Lossless is told from AAC by its sample description.
        private static void AppleLossless(string dir)
        {
            byte[] Fake(string format) => System.Text.Encoding.ASCII.GetBytes("....ftypM4A ....moovstsd" + "\0\0\0\0" + "\0\0\0\u0001" + "\0\0\0\0" + format);
            var alac = Path.Combine(dir, "lossless.m4a");
            var aac = Path.Combine(dir, "lossy.m4a");
            var named = Path.Combine(dir, "named.m4a");
            File.WriteAllBytes(alac, Fake("alac"));
            File.WriteAllBytes(aac, Fake("mp4a"));
            File.WriteAllBytes(named, System.Text.Encoding.ASCII.GetBytes("....ftypM4A title=alac stsd mp4a"));

            Check("an ALAC .m4a is recognised", AudioFormats.IsAppleLossless(alac));
            Check("an AAC .m4a is not", !AudioFormats.IsAppleLossless(aac));
            Check("a title that says alac does not fool it", !AudioFormats.IsAppleLossless(named));
            Check("Apple Lossless with no bit rate is asked about, not turned into AAC",
                AudioFormats.SameFormatAs(".m4a", null, true) == null);
        }

        // 15 and 16: tags that travel, and a failure message that stays short.
        private static void Tags()
        {
            var set = new AudioTagSet("T", null, null, null, null, null, null, "Composer Name", "2/3");
            Check("composer and disc count as tags", !set.IsEmpty);
            var fields = set.VorbisFields().ToList();
            Check("composer goes to Vorbis comments", fields.Contains(("COMPOSER", "Composer Name")));
            Check("disc goes to Vorbis comments", fields.Contains(("DISCNUMBER", "2/3")));

            var back = AudioTags.FromVorbisFields(fields);
            Equal("composer comes back from Vorbis comments", "Composer Name", back?.Composer ?? "");
            Equal("disc comes back from Vorbis comments", "2/3", back?.Disc ?? "");

            var results = Enumerable.Range(0, 40)
                .Select(i => new TagResult($@"C:\m\track{i}.opus", i < 15 ? null : "Windows cannot write tags to Ogg or Opus files, and Explorer Native does not write them itself yet."))
                .ToList();
            var summary = TagEditing.Summary(results);
            Check("the failure summary names three files at most", summary.Contains("track15.opus") && summary.Contains("track17.opus") && !summary.Contains("track18.opus"), summary);
            Check("and says how many more", summary.Contains("And 22 more."), summary);
        }

        // 8: a media type change mid-stream is renegotiated, driven through a fake.
        private static void TypeChange()
        {
            Check("the type-changed flag is noticed", AudioDecoder.TypeChanged(0, 0x20));
            Check("the stream-change error is noticed", AudioDecoder.TypeChanged(unchecked((int)0xC00D6D61), 0));
            Check("an ordinary read is not a change", !AudioDecoder.TypeChanged(0, 0));

            var asked = new System.Collections.Generic.List<(int Rate, int Channels)>();
            bool ok = AudioDecoder.Renegotiate((r, c) => { asked.Add((r, c)); return 0; }, 44100, 2);
            Check("the output type is put back to the running format", ok && asked.Count == 1 && asked[0] == (44100, 2));

            asked.Clear();
            ok = AudioDecoder.Renegotiate((r, c) => { asked.Add((r, c)); return r == 0 ? 0 : -1; }, 44100, 2);
            Check("if that is refused it asks again for the channels alone",
                ok && asked.Count == 2 && asked[1] == (0, 2));

            ok = AudioDecoder.Renegotiate((r, c) => -1, 44100, 2);
            Check("and gives up only when both are refused", !ok);
        }

        // 10: mono Opus stays mono.
        private static void MonoOpus(string dir)
        {
            var path = Path.Combine(dir, "mono.opus");
            var samples = new float[48000];
            for (int i = 0; i < samples.Length; i++) samples[i] = (float)Math.Sin(i * 0.05) * 0.4f;
            using (var encoder = new OpusFileEncoder(path, 1, 64, null))
            {
                encoder.Write(samples, samples.Length);
                encoder.Finish();
            }

            var decoder = TrackDecoder.Open(path, null, 0, 0, out var why);
            Check("a mono Opus file opens", decoder != null, why);
            if (decoder == null) return;
            Check("and has one channel", decoder.Channels == 1, "channels " + decoder.Channels);
            decoder.Dispose();
        }
    }

    // ---- merged from AudioFixTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

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
                SilenceYieldsTests();

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

        // ---------- A long silence does not hold the pump ----------

        /// <summary>
        /// Silence reduction reading through a long gap hands back nothing until
        /// the gap ends, and the pump could not see a skip, a stop or a device
        /// change until it did. Asked to yield, it stops early, says so, and
        /// carries on from where it was.
        /// </summary>
        private static void SilenceYieldsTests()
        {
            const int channels = 2, rate = 48000;
            const long silentFrames = rate * 120L;   // two minutes of nothing
            const long toneFrames = rate;

            long read = 0;
            int Source(float[] buffer, int frames)
            {
                long left = silentFrames + toneFrames - read;
                int n = (int)Math.Min(frames, Math.Max(0, left));
                for (int f = 0; f < n; f++)
                {
                    long at = read + f;
                    float v = at < silentFrames ? 0f : (float)(0.5 * Math.Sin(2 * Math.PI * 440 * at / rate));
                    for (int c = 0; c < channels; c++) buffer[f * channels + c] = v;
                }
                read += n;
                return n;
            }

            bool yieldNow = true;
            var stage = new SilenceReduction(channels, rate)
            {
                Source = Source,
                Enabled = true,
                ShouldYield = () => yieldNow,
            };

            var buffer = new float[1024 * channels];
            int got = stage.Read(buffer, 1024);
            bool yielded = stage.TakeYielded();

            Check("a read in a long gap stops when asked to yield", got == 0 && yielded,
                $"{got} frames, yielded {yielded}");
            Check("having read only a little of the gap",
                read <= SilenceReduction.YieldEveryBlocks * SilenceReduction.Detect * 2L,
                $"{read} of {silentFrames} silent frames read");
            Check("and the yield is reported once", !stage.TakeYielded(), null);

            // Left to carry on: the front of the gap it keeps, then the music.
            yieldNow = false;
            bool heard = false, yieldedSince = false;
            for (int reads = 0; reads < 100 && !heard; reads++)
            {
                got = stage.Read(buffer, 1024);
                yieldedSince |= stage.TakeYielded();
                if (got <= 0) break;
                heard = buffer.Take(got * channels).Any(v => v != 0);
            }
            Check("left to carry on, it reads through the rest of the gap to the music",
                heard && read > silentFrames, $"{read} read, heard {heard}");
            Check("and those reads did not yield", !yieldedSince, null);

            // Off, it is a bypass and never asks.
            bool asked = false;
            read = 0;
            var off = new SilenceReduction(channels, rate)
            {
                Source = Source,
                ShouldYield = () => asked = true,
            };
            got = off.Read(buffer, 1024);
            Check("switched off it never asks to yield", got == 1024 && !asked && !off.TakeYielded(),
                $"{got} frames, asked {asked}");
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
        /// skipped with no device, then, given a player, played, skipped and
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
    /// <summary>
    /// The ambience isolator: off is a bypass, centred direct sound goes,
    /// diffuse sound stays, a decaying tail stays, and nothing runs away.
    /// </summary>
    internal static class AmbienceTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            Console.WriteLine("Ambience isolator:");
            const int rate = 48000;

            // Off: the samples come out exactly as they went in.
            {
                var a = new Ambience();
                var buffer = Noise(rate, 1, correlated: true, seed: 1);
                var copy = (float[])buffer.Clone();
                a.Process(buffer, buffer.Length, 2, rate);
                bool same = true;
                for (int i = 0; i < buffer.Length; i++) if (buffer[i] != copy[i]) { same = false; break; }
                Check("off is a bypass, bit for bit", same);
            }

            // The same noise in both channels is direct sound: the stereo test removes it.
            double centred = Ratio(new AmbienceSettings { Method = 1, LevelDecibels = 0 }, Noise(rate, 3, correlated: true, seed: 2), rate);
            Check("a sound in the middle is taken out by the stereo test", centred < 0.01, centred.ToString("0.0000"));

            // Unrelated noise in each channel is diffuse: the stereo test keeps most of it.
            double diffuse = Ratio(new AmbienceSettings { Method = 1, LevelDecibels = 0 }, Noise(rate, 3, correlated: false, seed: 3), rate);
            Check("a diffuse sound is kept by the stereo test", diffuse > 0.3, diffuse.ToString("0.0000"));

            // A steady sound has no tail; a decaying one is all tail.
            var steady = Noise(rate, 3, correlated: false, seed: 4);
            var decaying = Noise(rate, 3, correlated: false, seed: 4);
            for (int f = 0; f < decaying.Length / 2; f++)
            {
                double t = (f % (rate / 2)) / (double)rate;
                float env = (float)Math.Exp(-3 * Math.Log(10) * t / 1.5);
                decaying[2 * f] *= env; decaying[2 * f + 1] *= env;
            }
            var tail = new AmbienceSettings { Method = 2, LevelDecibels = 0, TailMilliseconds = 1500 };
            double steadyKept = Ratio(tail, steady, rate), tailKept = Ratio(tail, decaying, rate);
            Check("the tail test keeps a decaying sound more than a steady one", tailKept > steadyKept * 1.5,
                tailKept.ToString("0.000") + " against " + steadyKept.ToString("0.000"));

            // Mono into both tests: the tail test takes over instead of silence.
            double mono = Ratio(new AmbienceSettings { LevelDecibels = 0 }, decaying.Select((v, i) => decaying[i - i % 2]).ToArray(), rate);
            Check("a mono recording still gives an ambience with both tests", mono > 0.01, mono.ToString("0.0000"));

            // Hard settings, long run, loud input: finite and bounded.
            {
                var a = new Ambience { Enabled = true, Settings = new AmbienceSettings { StrengthPercent = 0, LevelDecibels = 30, OriginalDecibels = 0 } };
                bool finite = true; float peak = 0;
                for (int block = 0; block < 400; block++)
                {
                    var b = Noise(rate / 100, 1, correlated: block % 2 == 0, seed: 10 + block, frames: 480);
                    for (int i = 0; i < b.Length; i++) b[i] *= 4;
                    a.Process(b, b.Length, 2, rate);
                    foreach (var v in b) { if (!float.IsFinite(v)) finite = false; peak = Math.Max(peak, Math.Abs(v)); }
                }
                Check("the output stays finite and bounded at the extremes", finite && peak < 1000, peak.ToString("0.0"));
            }

            // Every saved setting is on a list in the dialog.
            var d = new AmbienceSettings();
            Check("the defaults are on the dialog's lists",
                Ambience.StrengthLadder.Contains(d.StrengthPercent) && Ambience.TailLadder.Contains(d.TailMilliseconds) &&
                Ambience.LevelLadder.Contains(d.LevelDecibels) && Ambience.OriginalLadder.Contains(d.OriginalDecibels));
        }

        /// <summary>Output power over input power, past the first second (the stats settle).</summary>
        private static double Ratio(AmbienceSettings settings, float[] input, int rate)
        {
            var a = new Ambience { Enabled = true, Settings = settings };
            var buffer = (float[])input.Clone();
            for (int at = 0; at < buffer.Length; at += 960)
            {
                int n = Math.Min(960, buffer.Length - at);
                var block = new float[n];
                Array.Copy(buffer, at, block, 0, n);
                a.Process(block, n, 2, rate);
                Array.Copy(block, 0, buffer, at, n);
            }
            double pin = 0, pout = 0;
            for (int i = 2 * rate; i < buffer.Length; i++) { pin += input[i] * (double)input[i]; pout += buffer[i] * (double)buffer[i]; }
            return pout / Math.Max(1e-12, pin);
        }

        private static float[] Noise(int rate, int seconds, bool correlated, int seed, int frames = 0)
        {
            var random = new Random(seed);
            int count = frames > 0 ? frames : rate * seconds;
            var b = new float[count * 2];
            for (int f = 0; f < count; f++)
            {
                float l = (float)(random.NextDouble() * 0.5 - 0.25);
                float r = correlated ? l : (float)(random.NextDouble() * 0.5 - 0.25);
                b[2 * f] = l; b[2 * f + 1] = r;
            }
            return b;
        }
    }
}
