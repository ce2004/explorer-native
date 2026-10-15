using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Bug sweep, Drive mount half: a file warmed for a paste at the moment
    /// Drive is unmounted. The warm could open its cache after the unmount had
    /// already let every cache go, so its download ran on with nothing left to
    /// stop it.
    /// </summary>
    internal static class Sweep308MountTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        private static readonly DateTime When = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        public static Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            Console.WriteLine("Drive mount sweep:");

            WarmAfterUnmountOpensNothing();
            WarmRacingUnmountLeavesNoCache();

            Console.WriteLine();
            return Task.CompletedTask;
        }

        /// <summary>
        /// A mount over a temporary folder that is never registered: the map is
        /// told the one file's Drive id directly, which is all Warm looks at.
        /// </summary>
        private static DriveMount MountWithOneFile(DriveClient client, string root, string fileId, out string path)
        {
            Directory.CreateDirectory(root);
            path = Path.Combine(root, "one.flac");
            File.WriteAllBytes(path, new byte[64 * 1024]);

            var mount = new DriveMount(client, root, 64L * 1024 * 1024, _ => { });
            var ids = (ConcurrentDictionary<string, string>)typeof(DriveMount)
                .GetField("_ids", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mount)!;
            ids["one.flac"] = fileId;
            return mount;
        }

        private static void WarmAfterUnmountOpensNothing()
        {
            var root = Path.Combine(Path.GetTempPath(), "en-sw308m-" + Guid.NewGuid().ToString("N")[..8]);
            var drive = new FakeDrive();
            var client = new DriveClient(drive, 0.0005);
            var fileId = drive.AddFile("one.flac", new byte[64 * 1024], When, FakeDrive.Root);
            try
            {
                var mount = MountWithOneFile(client, root, fileId, out var path);
                int warmed = mount.Warm(new[] { path });
                Check("a file on the mount can be warmed (control)", warmed == 1 && mount.OpenCaches == 1,
                    $"warmed {warmed}, open {mount.OpenCaches}");

                mount.Dispose();
                Check("unmounting lets the warmed file go", mount.OpenCaches == 0, $"open {mount.OpenCaches}");

                File.WriteAllBytes(path, new byte[64 * 1024]);
                warmed = mount.Warm(new[] { path });
                Check("a warm after the unmount opens nothing", warmed == 0 && mount.OpenCaches == 0,
                    $"warmed {warmed}, open {mount.OpenCaches}");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>
        /// Warm and Dispose started together, many times over. Whichever wins,
        /// nothing may be left open afterwards: a cache still in the table once
        /// Dispose has returned is a download nobody will ever cancel.
        /// </summary>
        private static void WarmRacingUnmountLeavesNoCache()
        {
            var root = Path.Combine(Path.GetTempPath(), "en-sw308r-" + Guid.NewGuid().ToString("N")[..8]);
            var drive = new FakeDrive();
            var client = new DriveClient(drive, 0.0005);
            var fileId = drive.AddFile("one.flac", new byte[64 * 1024], When, FakeDrive.Root);
            int left = 0, warmedAny = 0;
            const int Rounds = 300;
            try
            {
                for (int round = 0; round < Rounds; round++)
                {
                    var mount = MountWithOneFile(client, root, fileId, out var path);
                    using var start = new Barrier(2);
                    var warming = Task.Run(() =>
                    {
                        start.SignalAndWait();
                        if (mount.Warm(new[] { path }) > 0) Interlocked.Increment(ref warmedAny);
                    });
                    var unmounting = Task.Run(() =>
                    {
                        start.SignalAndWait();
                        mount.Dispose();
                    });
                    Task.WaitAll(new[] { warming, unmounting }, 10_000);
                    if (mount.OpenCaches != 0) left++;
                }

                Check("a warm racing the unmount never leaves a cache open after it",
                    left == 0, $"{left} of {Rounds} rounds left one open ({warmedAny} warms got in first)");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
