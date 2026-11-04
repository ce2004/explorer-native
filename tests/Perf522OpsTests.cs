using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// CPU and battery costs of big operations and the idle window (5.2.2).
    /// Each test pins a cost that was measured, not a guess.
    /// </summary>
    internal static class Perf522OpsTests
    {
        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            await ForgetCostsNoSearchPerEvent(check);
            await RenamedMoveReportsAtMostTenASecond(check);
        }

        /// <summary>Counts reports where they are made, with no window or thread hop.</summary>
        private sealed class CountingProgress : IProgress<TransferProgress>
        {
            public int Reports;
            public TransferProgress? Last;
            public void Report(TransferProgress value) { Interlocked.Increment(ref Reports); Last = value; }
        }

        /// <summary>
        /// A same-volume move of a big selection is one rename per item, and it
        /// reported every one: 50,000 selected files were 50,000 posts to the
        /// progress window. Now at most ten a second, with the last always said.
        /// The sources renamed away are taken out of the list in one pass, not
        /// one Remove (a shift of the whole list) per rename. And the volume
        /// question (about a millisecond of calls to Windows) is asked once per
        /// source folder, and where a file really is only for a folder.
        /// </summary>
        private static async Task RenamedMoveReportsAtMostTenASecond(Action<string, bool, string?> check)
        {
            var dir = Path.Combine(Path.GetTempPath(), "EN-perf522m-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                const int Files = 20_000;
                var from = Path.Combine(dir, "from");
                var to = Path.Combine(dir, "to");
                Directory.CreateDirectory(from);
                Directory.CreateDirectory(to);
                var sources = new List<string>(Files);
                for (int i = 0; i < Files; i++)
                {
                    var path = Path.Combine(from, $"photo {i:D5}.jpg");
                    File.WriteAllBytes(path, new byte[1]);
                    sources.Add(path);
                }

                // The per-folder answer is the answer SameVolume gives, asked once.
                var answers = new RoboCopyEngine.VolumeAnswers(to);
                check("Perf522 move: the volume answer per folder agrees with asking each file",
                    answers.SameAs(sources[0]) == RoboCopyEngine.SameVolume(sources[0], to) &&
                    answers.SameAs(sources[1]) == RoboCopyEngine.SameVolume(sources[1], to) &&
                    !answers.SameAs(@"Q:\nowhere\x.jpg"), null);

                var counting = new CountingProgress();
                var clock = Stopwatch.StartNew();
                var result = await RoboCopyEngine.RunAsync(sources, to, move: true, threads: 8,
                    PasteConflictPolicy.AutoRename, counting, CancellationToken.None);
                clock.Stop();
                Console.WriteLine($"  Perf522 move: {Files} renames took {clock.ElapsedMilliseconds}ms, {counting.Reports} reports");

                int landed = Directory.GetFiles(to).Length;
                check("Perf522 move: every file was moved", landed == Files && result.Failed == 0,
                    $"landed {landed}, failed {result.Failed}");
                check("Perf522 move: a renamed selection reports at most ten a second, not once per file",
                    counting.Reports <= 10 * (clock.ElapsedMilliseconds / 1000 + 1) + 5,
                    $"{counting.Reports} reports in {clock.ElapsedMilliseconds}ms");
                check("Perf522 move: the last report counts every file",
                    counting.Last is { } last && last.ItemsDone == Files, counting.Last?.ToString());
                // 36s here before: the volume asked of Windows for every file,
                // and where both paths really are asked for every file too.
                check("Perf522 move: 20,000 files renamed in under 20 seconds",
                    clock.ElapsedMilliseconds < 20_000, $"{clock.ElapsedMilliseconds}ms");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// The panes' size watcher watches everything below the folder on screen
        /// and calls Forget for every event, three or so per file. Forget used to
        /// search every cached size each time, so a 100,000-file paste into an
        /// open folder, after browsing a library with thousands of measured
        /// folders, was hundreds of millions of path comparisons. Now a change
        /// looks up only the folders above it, and searches for folders inside
        /// it only when something measured sits under it.
        /// </summary>
        private static async Task ForgetCostsNoSearchPerEvent(Action<string, bool, string?> check)
        {
            var dir = Path.Combine(Path.GetTempPath(), "EN-perf522-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                const int Folders = 1500;
                Directory.CreateDirectory(dir);
                for (int i = 0; i < Folders; i++)
                {
                    var sub = Path.Combine(dir, $"Artist {i:D4}");
                    Directory.CreateDirectory(sub);
                    File.WriteAllBytes(Path.Combine(sub, "a.flac"), new byte[10]);
                }

                using var sizes = new FolderSizeCalculator();
                for (int i = 0; i < Folders; i++)
                    await sizes.CalculateAsync(Path.Combine(dir, $"Artist {i:D4}"));
                var first = Path.Combine(dir, "Artist 0000");
                check("Perf522 sizes: measured folders are cached", sizes.TryGetCached(first, out _), null);

                // A paste of new files into the open folder, as the watcher reports it.
                const int Events = 60_000;
                var landing = Path.Combine(dir, "Pasted");
                var clock = Stopwatch.StartNew();
                for (int i = 0; i < Events; i++)
                    sizes.Forget(Path.Combine(landing, $"file {i}.bin"));
                clock.Stop();
                Console.WriteLine($"  Perf522 sizes: {Events} changes with {Folders} cached folders took {clock.ElapsedMilliseconds}ms");
                check("Perf522 sizes: a change searches no cache it cannot touch (60,000 changes under 1.5s)",
                    clock.ElapsedMilliseconds < 1500, $"{clock.ElapsedMilliseconds}ms");
                check("Perf522 sizes: new files beside measured folders leave their sizes alone",
                    sizes.TryGetCached(first, out _) && sizes.TryGetCached(Path.Combine(dir, $"Artist {Folders - 1:D4}"), out _), null);

                // Still right: above, itself, inside.
                var deep = Path.Combine(dir, "Box", "Inner", "Deep");
                Directory.CreateDirectory(deep);
                File.WriteAllBytes(Path.Combine(deep, "x.bin"), new byte[5]);
                var box = Path.Combine(dir, "Box");
                await sizes.CalculateAsync(box);
                await sizes.CalculateAsync(deep);
                sizes.Forget(Path.Combine(deep, "y.bin"));
                check("Perf522 sizes: a change still forgets every folder above it",
                    !sizes.TryGetCached(box, out _) && !sizes.TryGetCached(deep, out _), null);

                await sizes.CalculateAsync(deep);
                sizes.Forget(Path.Combine(dir, "Box", "Inner"));
                check("Perf522 sizes: a folder renamed or deleted still forgets what is inside it",
                    !sizes.TryGetCached(deep, out _), null);

                await sizes.CalculateAsync(deep);
                sizes.Forget(dir + "\\");
                check("Perf522 sizes: a change with a trailing slash still forgets what is inside",
                    !sizes.TryGetCached(deep, out _) && !sizes.TryGetCached(first, out _), null);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
