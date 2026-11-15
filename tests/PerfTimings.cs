using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// A few timings printed the same way every run, so a slowdown shows up in
    /// the release it arrives in: compare the TIMING lines of two runs.
    /// Group "perf" (EXPLORERNATIVE_SELFTEST_ONLY=perf), and the end of the
    /// whole run. Each is the median of five, in milliseconds.
    ///
    ///   startup              the app's own exe to its first answer (--help),
    ///                        only when EXPLORERNATIVE_PERF_EXE names a built exe
    ///   open-folder-10000    listing a folder of 10,000 files and sorting it as
    ///                        a pane does (one scan, logical sort keys)
    ///   search-10000         a search of that folder for one name
    ///   drive-first-listing  a fresh Drive client's first listing of a folder
    ///                        of 2,000 files, against the stand-in Drive
    ///
    /// Nothing here fails a run for being slow (a shared runner's timing is not
    /// ours); a check fails only when the thing timed did not work.
    /// When GITHUB_STEP_SUMMARY is set the table is added to the job summary.
    /// </summary>
    internal static class PerfTimings
    {
        public const int Runs = 5;
        private static readonly List<(string Name, double Ms)> _results = new();

        public static IReadOnlyList<(string Name, double Ms)> Results => _results;

        public static async Task RunAll(Action<string, bool, string?> check)
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-perf-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                for (int i = 0; i < 10_000; i++)
                    File.WriteAllBytes(Path.Combine(dir, $"Track {i} of the perf folder.txt"), Array.Empty<byte>());

                int listed = 0;
                Add("open-folder-10000", Median(() => listed = OpenLikeAPane(dir)));
                check("perf: the 10,000 file folder lists whole", listed == 10_000, listed + " listed");

                int found = -1;
                Add("search-10000", Median(() =>
                    found = FileSearch.Local(dir, "Track 5000 of", false, false, 1000, CancellationToken.None).Hits.Count));
                check("perf: the search finds the one file", found == 1, found + " found");

                int driveCount = 0;
                var times = new List<double>();
                for (int run = 0; run < Runs; run++)
                {
                    var fake = new FakeDrive();
                    var folder = fake.AddFolder("Perf");
                    for (int i = 0; i < 2_000; i++)
                        fake.AddFile($"song {i}.mp3", Array.Empty<byte>(), DateTime.UtcNow, folder);
                    var client = new DriveClient(fake, 0.0005);
                    var watch = Stopwatch.StartNew();
                    var entries = await client.ListChildren(folder, CancellationToken.None);
                    times.Add(watch.Elapsed.TotalMilliseconds);
                    driveCount = entries.Count;
                }
                Add("drive-first-listing", MedianOf(times));
                check("perf: the first Drive listing has every file", driveCount == 2_000, driveCount + " listed");

                var exe = Environment.GetEnvironmentVariable("EXPLORERNATIVE_PERF_EXE");
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                {
                    int code = -1;
                    Add("startup", Median(() => code = StartAndWait(exe)));
                    check("perf: the app starts and answers --help", code == 0, "exit code " + code);
                }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        /// <summary>What a pane does with a folder: one scan, a logical sort key per name, sorted.</summary>
        private static int OpenLikeAPane(string dir)
        {
            var rows = new List<(string Name, byte[]? Key, string Path)>();
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                rows.Add((info.Name, NameRules.LogicalKey(info.Name), info.FullName));
            rows.Sort((a, b) => NameRules.CompareNames(a.Name, a.Key, a.Path, b.Name, b.Key, b.Path));
            return rows.Count;
        }

        private static int StartAndWait(string exe)
        {
            var info = new ProcessStartInfo(exe, "--help --quiet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info)!;
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30_000)) { try { process.Kill(); } catch { } return -1; }
            return process.ExitCode;
        }

        private static double Median(Action work)
        {
            var times = new List<double>();
            for (int run = 0; run < Runs; run++)
            {
                var watch = Stopwatch.StartNew();
                work();
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
            return MedianOf(times);
        }

        private static double MedianOf(List<double> times)
        {
            var sorted = times.OrderBy(t => t).ToList();
            return sorted[sorted.Count / 2];
        }

        private static void Add(string name, double ms)
        {
            lock (_results)
            {
                _results.RemoveAll(r => r.Name == name);
                _results.Add((name, ms));
            }
        }

        /// <summary>The TIMING lines: one per measurement, the same names every release.</summary>
        public static string Lines()
        {
            var text = new StringBuilder();
            lock (_results)
                foreach (var (name, ms) in _results)
                    text.AppendLine($"TIMING {name} {ms.ToString("0.0", CultureInfo.InvariantCulture)} ms");
            return text.ToString();
        }

        public static void Print()
        {
            if (_results.Count == 0) return;
            Console.WriteLine($"\nTimings, median of {Runs} runs each (compare with an earlier release's run):");
            Console.Write(Lines());

            var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            if (string.IsNullOrEmpty(summary)) return;
            try
            {
                var table = new StringBuilder("\n| Timing | Milliseconds |\n| --- | --- |\n");
                lock (_results)
                    foreach (var (name, ms) in _results)
                        table.Append($"| {name} | {ms.ToString("0.0", CultureInfo.InvariantCulture)} |\n");
                File.AppendAllText(summary, table.ToString());
            }
            catch { }
        }
    }
}
