using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// EXPLORERNATIVE_SELFTEST_ONLY=playprobe, EXPLORERNATIVE_PLAYPROBE_DIR=a
    /// folder of media files (and any more, separated by ";"). Every file is
    /// played through the real AudioPlayer, as Enter does, three ways: calmly,
    /// one after another with a moment of each heard; quickly, as somebody
    /// arrowing down a folder pressing Enter; and the same file twice at once.
    /// Every failure is printed with the player's own reason, which is what the
    /// "cannot play, opening it instead" message never says.
    /// </summary>
    internal static class PlayProbe
    {
        public static int Run()
        {
            var folders = (Environment.GetEnvironmentVariable("EXPLORERNATIVE_PLAYPROBE_DIR") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            var files = folders.SelectMany(f => Directory.EnumerateFiles(f, "*", SearchOption.AllDirectories))
                .Where(f => Environment.GetEnvironmentVariable("EXPLORERNATIVE_PLAYPROBE_ALL") == "1" ||
                            AudioFiles.IsAudio(f, new Settings().AudioExtensions))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            int rounds = int.TryParse(Environment.GetEnvironmentVariable("EXPLORERNATIVE_PLAYPROBE_ROUNDS"), out var r) ? r : 3;
            Console.WriteLine($"{files.Count} playable files, {rounds} quick rounds");

            var failures = new List<string>();
            int plays = 0;

            // Every WAV through WavDecoder directly, whether or not Windows takes
            // it: the generated ones are 8 seconds of a 440Hz tone, so the length
            // and the tone itself say whether the decoding is right.
            // With PLAYPROBE_ALL, every file through TrackDecoder as the player
            // opens it (on an MTA thread, as the player's pump is), else the WAVs
            // through WavDecoder.
            bool everything = Environment.GetEnvironmentVariable("EXPLORERNATIVE_PLAYPROBE_ALL") == "1";
            Console.WriteLine(everything ? "every file decoded as the player does:" : "WavDecoder on every WAV:");
            foreach (var file in files.Where(f => everything || (f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) &&
                                                  Path.GetFileName(f).StartsWith("wav ", StringComparison.Ordinal))))
            {
                string line = "";
                bool good = false;
                var worker = new Thread(() =>
                {
                    ITrackDecoder? opened = null;
                    string why = "";
                    if (everything) opened = TrackDecoder.Open(file, null, 48000, 2, out why);
                    else { var w = new WavDecoder(); if (w.Open(file, null)) opened = w; else { why = w.Diagnostic; w.Dispose(); } }
                    if (opened == null) { line = $"  refused {Path.GetFileName(file)}: {why}"; good = true; return; }
                    using var wav = opened;
                    var all = new List<float>();
                    var buffer = new float[4096 * wav.Channels];
                    int n;
                    // Every channel added together, so a tone in any one of them counts.
                    while ((n = wav.Read(buffer, 4096)) > 0)
                        for (int i = 0; i < n; i++)
                        {
                            float sum = 0;
                            for (int c = 0; c < wav.Channels; c++) sum += buffer[i * wav.Channels + c];
                            all.Add(sum);
                        }
                    double seconds = all.Count / (double)wav.SampleRate;
                    double tone = Goertzel(all, wav.SampleRate, 440), off = Goertzel(all, wav.SampleRate, 1234);
                    good = Math.Abs(seconds - 8) < 0.1 && tone > 20 * off && tone > 0.01;
                    line = $"  {(good ? "ok  " : "BAD ")} {Path.GetFileName(file)}: {wav.SampleRate} Hz, {wav.Channels} ch, " +
                           $"{seconds:F2} s, tone {tone:F3} against {off:F5}";
                });
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start();
                worker.Join();
                Console.WriteLine(line);
                if (!good) failures.Add($"decode: {Path.GetFileName(file)}");
            }
            using var player = new AudioPlayer();

            bool Try(string file, string how)
            {
                plays++;
                var clock = Stopwatch.StartNew();
                var said = player.Play(file);
                if (said != null) return true;
                failures.Add($"{how}: {Path.GetFileName(file)} after {clock.ElapsedMilliseconds} ms: {player.Diagnostic}");
                Console.WriteLine("  FAIL " + failures[^1]);
                return false;
            }

            Console.WriteLine("calmly, a moment of each:");
            foreach (var file in files)
            {
                if (!Try(file, "calm")) continue;
                Thread.Sleep(700);
            }

            var random = new Random(3005);
            for (int round = 1; round <= rounds; round++)
            {
                Console.WriteLine($"quickly, round {round}:");
                foreach (var file in files.OrderBy(_ => random.Next()))
                {
                    Try(file, $"quick {round}");
                    Thread.Sleep(random.Next(30, 350));
                }
            }

            Console.WriteLine("the same file twice in a row:");
            foreach (var file in files)
            {
                Try(file, "twice, first");
                Try(file, "twice, second");
            }

            player.Stop();
            Console.WriteLine($"\n{plays} plays, {failures.Count} failed");
            foreach (var group in failures.GroupBy(f => f[(f.IndexOf(": ", StringComparison.Ordinal) + 2)..].Split(" after ")[0]))
                Console.WriteLine($"  {group.Key}: {group.Count()} times");
            return failures.Count == 0 ? 0 : 1;
        }

        /// <summary>How much of one frequency is in a signal, as an amplitude.</summary>
        private static double Goertzel(List<float> samples, int rate, double frequency)
        {
            double w = 2 * Math.PI * frequency / rate, coeff = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            foreach (var x in samples)
            {
                double s0 = x + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }
            return Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - coeff * s1 * s2)) / samples.Count * 2;
        }
    }
}
