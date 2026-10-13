using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The mount's two housekeeping timers (the idle sweep and the hand-back of
    /// fully-populated marks) only tick while there is work for them, and the
    /// work they do still happens on time.
    /// </summary>
    internal static class DriveMountTimerTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        private static readonly DateTime When = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        public static Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            Console.WriteLine("Google Drive mount timers, armed only while there is work:");

            var keepInterval = DriveMount.SweepInterval;
            var keepFresh = DriveMount.FreshFor;
            try
            {
                WorkTimerTests();
                MountTimerTests();
            }
            finally
            {
                DriveMount.SweepInterval = keepInterval;
                DriveMount.FreshFor = keepFresh;
            }
            return Task.CompletedTask;
        }

        private static bool Eventually(Func<bool> condition, int milliseconds = 5000)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }
            return condition();
        }

        private static void WorkTimerTests()
        {
            DriveMount.SweepInterval = TimeSpan.FromMilliseconds(1);

            int pending = 0;
            int ticks = 0;
            using var timer = new DriveMount.WorkTimer(
                () => { Interlocked.Increment(ref ticks); Interlocked.Exchange(ref pending, 0); },
                () => Volatile.Read(ref pending) > 0);

            Check("a work timer with nothing to do is not armed", !timer.Armed);
            Thread.Sleep(50);
            Check("and never ticks", Volatile.Read(ref ticks) == 0, $"{ticks} ticks");

            Interlocked.Increment(ref pending);
            timer.Arm();
            Check("work arms it", timer.Armed);
            Check("it ticks, does the work and stands down again",
                Eventually(() => Volatile.Read(ref pending) == 0 && !timer.Armed),
                $"pending {pending}, armed {timer.Armed}");

            // Many threads adding while ticks keep stopping the timer: an add is
            // never left behind with the timer stopped.
            var producers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 2000; i++)
                {
                    Interlocked.Increment(ref pending);
                    timer.Arm();
                    if ((i & 63) == 0) Thread.Sleep(1);
                }
            })).ToArray();
            Task.WaitAll(producers);
            Check("work added from many threads at once is never stranded",
                Eventually(() => Volatile.Read(ref pending) == 0 && !timer.Armed),
                $"pending {pending}, armed {timer.Armed}");

            timer.Dispose();
            Interlocked.Increment(ref pending);
            timer.Arm();
            Check("a disposed work timer does not start again", !timer.Armed);
        }

        private static void MountTimerTests()
        {
            DriveMount.SweepInterval = TimeSpan.FromMilliseconds(100);
            DriveMount.FreshFor = TimeSpan.FromMilliseconds(1000);

            var root = Path.Combine(Path.GetTempPath(), "en-mtimer-" + Guid.NewGuid().ToString("N")[..8]);
            var drive = new FakeDrive();
            var client = new DriveClient(drive, 0.0005);
            DriveMount? mount = null;
            try
            {
                mount = new DriveMount(client, root, 64L * 1024 * 1024, _ => { });
                Check("a new mount has no timer ticking", !mount.SweepArmed && !mount.ReopenArmed);

                // On Drive before the root is listed, so the root's listing knows it.
                var folder = drive.AddFolder("Tracks");
                drive.AddFile("one.flac", new byte[64 * 1024], When, folder);

                mount.Start(FakeDrive.Root, assignLetter: false);
                mount.Populate(root);
                Thread.Sleep(300);
                Check("a mount that has listed only its root still has no timer ticking",
                    !mount.SweepArmed && !mount.ReopenArmed,
                    $"sweep {mount.SweepArmed}, reopen {mount.ReopenArmed}, marked {mount.MarkedFullCount}");
                Check("the root is never put up to be handed back", mount.MarkedFullCount == 0,
                    $"marked {mount.MarkedFullCount}");

                // ---- the hand-back of fully-populated marks ----
                var folderPath = Path.Combine(root, "Tracks");
                mount.Populate(folderPath);
                Check("marking a folder fully populated arms the hand-back",
                    mount.MarkedFullCount >= 1 && mount.ReopenArmed,
                    $"marked {mount.MarkedFullCount}, reopen {mount.ReopenArmed}");
                Check("the timer hands it back after FreshFor and then stands down",
                    Eventually(() => mount.MarkedFullCount == 0 && !mount.ReopenArmed),
                    $"marked {mount.MarkedFullCount}, reopen {mount.ReopenArmed}");

                // ---- the idle sweep ----
                Check("no cache, no sweep", !mount.SweepArmed);
                int warmed = mount.Warm(new[] { Path.Combine(folderPath, "one.flac") });
                Check("a warmed file has a cache", warmed == 1 && mount.OpenCaches == 1,
                    $"warmed {warmed}, open {mount.OpenCaches}");
                Check("with idle release off, a cache does not keep the sweep ticking", !mount.SweepArmed);

                // Callbacks in flight hold the sweep off, as they always have.
                var inFlight = typeof(DriveMount).GetField("_callbacksInFlight",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                inFlight.SetValue(mount, 1);
                mount.IdleRelease = TimeSpan.FromMilliseconds(200);
                Check("switching idle release on with a cache open arms the sweep", mount.SweepArmed);
                Thread.Sleep(800);
                Check("the sweep never releases a cache while a callback is running",
                    mount.OpenCaches == 1 && mount.SweepArmed,
                    $"open {mount.OpenCaches}, sweep {mount.SweepArmed}");
                inFlight.SetValue(mount, 0);
                Check("once the callbacks are done, the idle cache is released and the sweep stands down",
                    Eventually(() => mount.OpenCaches == 0 && !mount.SweepArmed),
                    $"open {mount.OpenCaches}, sweep {mount.SweepArmed}");

                warmed = mount.Warm(new[] { Path.Combine(folderPath, "one.flac") });
                Check("a cache opened again arms the sweep again", warmed == 1 && mount.SweepArmed,
                    $"warmed {warmed}, sweep {mount.SweepArmed}");
                Check("and it is released again after the idle time",
                    Eventually(() => mount.OpenCaches == 0 && !mount.SweepArmed),
                    $"open {mount.OpenCaches}, sweep {mount.SweepArmed}");
            }
            finally
            {
                try { mount?.Dispose(); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
