using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
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
        /// and which pays no attention to being called off — the way a read
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
            // default-change watcher — which holds an enumerator of its own —
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
                // puts in front of it — and the same as a resume that lands just
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
}
