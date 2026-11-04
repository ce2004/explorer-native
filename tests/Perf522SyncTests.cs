using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The folder sync does no work it does not need: a quiet pass writes its
    /// records once, a watcher event reads one item's attributes rather than
    /// every folder above it, the own-writes table is not swept on every event,
    /// and a whole PC walk runs below normal priority.
    /// </summary>
    internal static class Perf522SyncTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private const double Fast = 0.0005;

        public static async Task RunAll(Action<string, bool, string?> check)
        {
            _check = check;
            Console.WriteLine("Sync performance (5.2.2):");

            var previous = Settings.OverrideAppDataDir;
            var sandbox = Path.Combine(Path.GetTempPath(), "en-perf522-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            Settings.OverrideAppDataDir = Path.Combine(sandbox, "appdata");
            try
            {
                await AQuietPassSavesOnce(sandbox);
                WatcherEventsReadOnlyTheItem(sandbox);
                OwnWritesSweptAtMostOnceASecond();
                WalkThreadsBelowNormal();
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(sandbox, true); } catch { }
            }
            Console.WriteLine();
        }

        private static DriveMonitor NewMonitor() =>
            new(new GoogleDrive(), (_, _) => { }, _ => { })
            {
                WaitScale = Fast,
                DiskOfRoot = _ => (1L << 42, 1L << 43, "T:"),
                RecycleLocal = path => { File.Delete(path); return null; },
                PatienceLimit = TimeSpan.FromSeconds(5),
                SettleTime = TimeSpan.Zero,
            };

        private static async Task AQuietPassSavesOnce(string sandbox)
        {
            var drive = new FakeDrive();
            var local = Path.Combine(sandbox, "quiet", "P");
            Directory.CreateDirectory(local);
            var folderId = drive.AddFolder("P");
            var pair = new DriveSyncPair
            {
                Name = "P", LocalFolder = local, DriveFolderId = folderId, DriveFolderPath = "My Drive/P",
                Mode = SyncMode.TwoWay, CopyDeletes = true, CheckMinutes = 0,
            };
            var when = DateTime.UtcNow.AddMinutes(-5);
            foreach (var name in new[] { "a.txt", "src/b.cs", "src/deep/c.cs" })
            {
                var path = Path.Combine(local, name.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "text of " + name);
                File.SetLastWriteTimeUtc(path, when);
            }

            var client = new DriveClient(drive, Fast);
            var monitor = NewMonitor();
            try
            {
                await monitor.SyncOneAsync(client, pair, CancellationToken.None);
                Check("quiet pass: the first pass uploads everything", drive.Tree(folderId).Count == 3,
                    string.Join(", ", drive.Tree(folderId).Keys));

                int before = monitor.StateSaves;
                await monitor.SyncOneAsync(client, pair, CancellationToken.None);
                int saves = monitor.StateSaves - before;
                Check("quiet pass: a pass with nothing to do writes its records once, not twice", saves == 1, $"{saves} saves");
                Check("quiet pass: and ends clean", monitor.StatusOf(pair.Id).Problem == null, monitor.StatusOf(pair.Id).Problem);
            }
            finally { monitor.Dispose(); }

            // What that one save holds is enough: a fresh monitor sends nothing again.
            monitor = NewMonitor();
            try
            {
                int uploadsBefore = drive.Tree(folderId).Count;
                await monitor.SyncOneAsync(client, pair, CancellationToken.None);
                var history = SyncHistory.Read(pair.Id);
                Check("quiet pass: after a restart nothing is sent again",
                    drive.Tree(folderId).Count == uploadsBefore &&
                    history.Count(e => e.What is "Uploaded" or "Updated in Drive") == 3,
                    string.Join("; ", history.Select(e => e.What + " " + e.Path)));
            }
            finally { monitor.Dispose(); }
        }

        private static void WatcherEventsReadOnlyTheItem(string sandbox)
        {
            var root = Path.Combine(sandbox, "watch");
            var deep = Path.Combine(root, "a", "b", "c");
            Directory.CreateDirectory(deep);
            var file = Path.Combine(deep, "f.txt");
            File.WriteAllText(file, "x");
            var hidden = Path.Combine(root, "hid");
            Directory.CreateDirectory(hidden);
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
            var inHidden = Path.Combine(hidden, "x.txt");
            File.WriteAllText(inHidden, "x");

            var visible = new DriveMonitor.VisibleFolders();
            long now = Environment.TickCount64;
            bool plain = DriveMonitor.NotNews(root, file, null);
            bool cached = DriveMonitor.NotNews(root, file, null, visible);
            Check("watcher: a change in a plain folder is news, with or without the cache", !plain && !cached);
            Check("watcher: the folders above it are remembered as not hidden",
                visible.Knows(Path.Combine(root, "a"), now) && visible.Knows(Path.Combine(root, "a", "b"), now) &&
                visible.Knows(deep, now));
            Check("watcher: the item itself is not remembered (it is what changed)", !visible.Knows(file, now));
            Check("watcher: a second event there is news again", !DriveMonitor.NotNews(root, file, null, visible));

            bool hiddenOnce = DriveMonitor.NotNews(root, inHidden, null, visible);
            bool hiddenTwice = DriveMonitor.NotNews(root, inHidden, null, visible);
            Check("watcher: a change in a hidden folder is not news, every time", hiddenOnce && hiddenTwice);
            Check("watcher: a hidden folder is never remembered", !visible.Knows(hidden, now));
            Check("watcher: what is remembered goes stale",
                !visible.Knows(deep, now + DriveMonitor.VisibleFolders.ForMs + 1));
        }

        private static void OwnWritesSweptAtMostOnceASecond()
        {
            Check("own writes: a small table is not swept", !DriveMonitor.OwnWritesSweepDue(100, 10_000, long.MinValue / 2));
            Check("own writes: a big one is", DriveMonitor.OwnWritesSweepDue(300, 10_000, long.MinValue / 2));
            Check("own writes: but not again within a second", !DriveMonitor.OwnWritesSweepDue(300, 10_500, 10_000));
            Check("own writes: and again after one", DriveMonitor.OwnWritesSweepDue(300, 11_000, 10_000));
        }

        private static string SourceFile(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            try { return dir == null ? "" : File.ReadAllText(Path.Combine(dir.FullName, "src", name)); }
            catch { return ""; }
        }

        private static void WalkThreadsBelowNormal()
        {
            var source = SourceFile("DriveMonitor.cs");
            int at = source.IndexOf("\"PC folder scan\"", StringComparison.Ordinal);
            int start = Math.Max(0, at - 300);
            var around = at < 0 ? "" : source.Substring(start, Math.Min(source.Length - start, 600));
            Check("walk: the parallel PC walk's threads run below normal priority",
                around.Contains("ThreadPriority.BelowNormal", StringComparison.Ordinal));
        }
    }
}
