using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

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
                    // device switch can hold the device at — anywhere up to the
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
        /// moving after a switch to a named device — the switch holds the device
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
}
