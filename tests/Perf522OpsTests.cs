using System;
using System.Diagnostics;
using System.IO;
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
