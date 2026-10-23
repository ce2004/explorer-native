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
    /// Hardening the folder sync (4.2.1): what the folder watcher takes for a
    /// change, the suite keeping out of the app's own trace, and the history a
    /// pass leaves. Run alone with EXPLORERNATIVE_SELFTEST_ONLY=synchardening.
    /// </summary>
    internal static class SyncHardeningTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        private const double Fast = 0.0005;
        private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;
            Console.WriteLine("Folder sync hardening:");

            var previous = Settings.OverrideAppDataDir;
            var sandbox = Path.Combine(Path.GetTempPath(), "en-synchardening-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            Settings.OverrideAppDataDir = Path.Combine(sandbox, "appdata");
            try
            {
                TraceStaysOutOfTheAppsLog(sandbox);
                NotNewsTests(sandbox);
                await WatcherIgnoresLeftOutChangesTests(sandbox);
                await FastLaneReplacementHistoryTests(sandbox);
                await OpenFileWaitsTests(sandbox);
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(sandbox, true); } catch { }
            }
            Console.WriteLine();
        }

        // ---------------- a small rig ----------------

        private sealed class Rig : IDisposable
        {
            public readonly FakeDrive Drive = new();
            public readonly string Local;
            public readonly string FolderId;
            public readonly DriveSyncPair Pair;
            public readonly DriveClient Client;
            public readonly DriveMonitor Monitor;
            public readonly List<string> Said = new();

            public Rig(string sandbox, SyncMode mode = SyncMode.TwoWay, bool deletes = false)
            {
                var top = Path.Combine(sandbox, Guid.NewGuid().ToString("N")[..8]);
                Local = Path.Combine(top, "P");
                Directory.CreateDirectory(Local);
                FolderId = Drive.AddFolder("P");
                Pair = new DriveSyncPair
                {
                    Name = "P", LocalFolder = Local, DriveFolderId = FolderId, DriveFolderPath = "My Drive/P",
                    Mode = mode, CopyDeletes = deletes, CheckMinutes = 0,
                };
                Client = new DriveClient(Drive, Fast);
                Monitor = new DriveMonitor(new GoogleDrive(), (id, message) => { lock (Said) Said.Add(id + ": " + message); }, _ => { })
                {
                    WaitScale = Fast,
                    DiskOfRoot = _ => (1L << 42, 1L << 43, "T:"),
                    RecycleLocal = path => { File.Delete(path); return null; },
                    PatienceLimit = TimeSpan.FromSeconds(5),
                    SettleTime = TimeSpan.Zero,
                };
            }

            public async Task<string?> Pass()
            {
                await Monitor.SyncOneAsync(Client, Pair, CancellationToken.None);
                return Monitor.StatusOf(Pair.Id).Problem;
            }

            public string Write(string relative, string text, DateTime when)
            {
                var path = Path.Combine(Local, relative.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
                File.SetLastWriteTimeUtc(path, when);
                return path;
            }

            public Dictionary<string, string> DriveTree() =>
                Drive.Tree(FolderId).ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetString(kv.Value), StringComparer.OrdinalIgnoreCase);

            public void Dispose()
            {
                Monitor.Dispose();
                try { Directory.Delete(Path.GetDirectoryName(Local)!, true); } catch { }
            }
        }

        // ---------------- the suite's trace ----------------

        /// <summary>
        /// The real app's drive-trace.log had the fake Drive's passes in it
        /// ("listing Drive whole, the feed failed: changes failed 410"), because
        /// EXPLORERNATIVE_DRIVELOG is a user variable and the suite inherited it.
        /// </summary>
        private static void TraceStaysOutOfTheAppsLog(string sandbox)
        {
            var before = Environment.GetEnvironmentVariable("EXPLORERNATIVE_DRIVELOG");
            var ownBefore = Environment.GetEnvironmentVariable("EXPLORERNATIVE_SELFTEST_DRIVELOG");
            var appsLog = Path.Combine(sandbox, "drive-trace.log");
            try
            {
                Environment.SetEnvironmentVariable("EXPLORERNATIVE_DRIVELOG", appsLog);
                Environment.SetEnvironmentVariable("EXPLORERNATIVE_SELFTEST_DRIVELOG", null);
                Tests.KeepDriveTraceToItself();
                GoogleDrive.Trace("sync P PC side: from the suite");
                Check("the suite's trace does not go into the app's drive-trace.log", !File.Exists(appsLog));

                var ownLog = Path.Combine(sandbox, "suite-trace.log");
                Environment.SetEnvironmentVariable("EXPLORERNATIVE_SELFTEST_DRIVELOG", ownLog);
                Tests.KeepDriveTraceToItself();
                GoogleDrive.Trace("sync P PC side: from the suite");
                Check("and goes where EXPLORERNATIVE_SELFTEST_DRIVELOG says when that is set",
                    File.Exists(ownLog) && !File.Exists(appsLog));
            }
            finally
            {
                Environment.SetEnvironmentVariable("EXPLORERNATIVE_SELFTEST_DRIVELOG", ownBefore);
                Environment.SetEnvironmentVariable("EXPLORERNATIVE_DRIVELOG", before);
            }
        }

        // ---------------- what the watcher takes for news ----------------

        private static void NotNewsTests(string sandbox)
        {
            var root = Path.Combine(sandbox, "news");
            var sync = Path.Combine(root, "US", ".sync");
            Directory.CreateDirectory(sync);
            new DirectoryInfo(sync).Attributes |= FileAttributes.Hidden;
            File.WriteAllText(Path.Combine(sync, "ID"), "x");
            Directory.CreateDirectory(Path.Combine(root, "US", "Book"));
            File.WriteAllText(Path.Combine(root, "US", "Book", "01.mp3"), "x");
            var hiddenFile = Path.Combine(root, "US", "Book", "secret.txt");
            File.WriteAllText(hiddenFile, "x");
            File.SetAttributes(hiddenFile, FileAttributes.Hidden);

            var plain = new SyncRules(SyncMode.UploadOnly, false);
            var skipping = new SyncRules(SyncMode.UploadOnly, false, SkipExtensions: ".sync");
            Check("a file Resilio writes in its hidden .sync folder is not news",
                DriveMonitor.NotNews(root, Path.Combine(sync, "drive_test_1792777620"), plain));
            Check("nor the .sync folder's own time changing",
                DriveMonitor.NotNews(root, sync, plain));
            Check("nor anything the skip list leaves out, gone already or not",
                DriveMonitor.NotNews(root, Path.Combine(root, "Gone.sync", "x"), skipping));
            Check("nor a name the planner always skips",
                DriveMonitor.NotNews(root, Path.Combine(root, "US", "Book", "02.mp3.partial"), plain));
            Check("nor a hidden file", DriveMonitor.NotNews(root, hiddenFile, plain));
            Check("a file the pair syncs is news",
                !DriveMonitor.NotNews(root, Path.Combine(root, "US", "Book", "01.mp3"), plain));
            Check("and so is one that has just gone",
                !DriveMonitor.NotNews(root, Path.Combine(root, "US", "Book", "deleted.mp3"), plain));
            Check("and a folder the pair syncs",
                !DriveMonitor.NotNews(root, Path.Combine(root, "US", "Book"), plain));
        }

        /// <summary>
        /// The AudioBooks pair (upload only, ".sync" skipped, Resilio's share)
        /// logged "PC side: 1 folders changed" and then "0 folders changed"
        /// every ten minutes with nothing changing: Resilio makes and deletes
        /// a drive_test file in .sync on every rescan, and each event made the
        /// pair due. Here a real watcher over a real folder: changes inside the
        /// hidden .sync must not make the pair due, and an ordinary file (the
        /// control) must.
        /// </summary>
        private static async Task WatcherIgnoresLeftOutChangesTests(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.UploadOnly);
            rig.Pair.SkipExtensions = ".sync";
            var sync = Path.Combine(rig.Local, "US", ".sync");
            Directory.CreateDirectory(sync);
            new DirectoryInfo(sync).Attributes |= FileAttributes.Hidden;
            Directory.CreateDirectory(Path.Combine(rig.Local, "US", "Book"));
            rig.Monitor.Reload(new[] { rig.Pair });

            // Wait for the watcher, with the control: an ordinary file makes it due.
            async Task<bool> DueWithin(int ms)
            {
                var until = Environment.TickCount64 + ms;
                while (Environment.TickCount64 < until)
                {
                    if (rig.Monitor.TakeDueForTests(rig.Pair.Id)) return true;
                    await Task.Delay(50);
                }
                return false;
            }
            bool watching = false;
            for (int i = 0; i < 40 && !watching; i++)
            {
                File.WriteAllText(Path.Combine(rig.Local, "US", "Book", "warm.mp3"), i.ToString());
                watching = await DueWithin(250);
            }
            Check("the control: a new file in a synced folder makes the pair due", watching);
            if (!watching) return;
            await Task.Delay(300);
            rig.Monitor.TakeDueForTests(rig.Pair.Id);

            var test = Path.Combine(sync, "drive_test_1792777620_3605816036");
            File.WriteAllText(test, "");
            await Task.Delay(100);
            File.Delete(test);
            File.WriteAllText(Path.Combine(sync, "IgnoreList"), "changed");
            Check("Resilio's test file in its hidden .sync folder does not make the pair due", !await DueWithin(1500));

            File.WriteAllText(Path.Combine(rig.Local, "US", "Book", "02.mp3"), "a new chapter");
            Check("and a new chapter still does", await DueWithin(3000));
        }

        // ---------------- a PC file open in another program ----------------

        /// <summary>
        /// Drive's copy of a file changed while the PC's copy was open in
        /// another program (a book being listened to). The download could not
        /// go over it, and that counted as a failure on every pass: "could not
        /// sync", in Windows' words, said aloud, until the program let go. It
        /// waits instead, says so quietly in the pair's status, and comes down
        /// once the file is free.
        /// </summary>
        private static async Task OpenFileWaitsTests(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.TwoWay);
            var path = rig.Write("book.txt", "first", T0);
            await rig.Pass();
            var id = rig.Drive.Find(rig.FolderId, "book.txt");
            Check("open file rig: the book went up", id != null);
            if (id == null) return;
            rig.Drive.Edit(id, Encoding.UTF8.GetBytes("edited in Drive"), T0.AddHours(1));

            string? problem;
            int saidBefore = rig.Said.Count;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                problem = await rig.Pass();
            var said = rig.Said.Skip(saidBefore).ToList();
            Check("a PC file open in another program is not said as a failure",
                !said.Any(s => s.StartsWith("sync.error")), string.Join(" | ", said));
            Check("the pair's status says it is open, in a sentence",
                problem == "book.txt is open in another program, so it syncs once that program lets go of it", problem);
            Check("and nothing was put over it meanwhile", File.ReadAllText(path) == "first");
            Check("its history has no failure for it",
                !SyncHistory.Read(rig.Pair.Id).Any(e => e.What == "Could not sync"));

            problem = await rig.Pass();
            Check("once the program lets go, Drive's edit comes down", File.ReadAllText(path) == "edited in Drive" && problem == null,
                problem);
        }

        // ---------------- history ----------------

        /// <summary>
        /// A batch of small edits goes up through the fast lane, which replaces
        /// each Drive file in place; the history called every one "Uploaded",
        /// as if it were new, where the careful loop says "Updated in Drive".
        /// </summary>
        private static async Task FastLaneReplacementHistoryTests(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.UploadOnly);
            int count = DriveMonitor.FastLaneMinimum + 6;
            for (int i = 0; i < count; i++) rig.Write($"notes/{i:D3}.txt", "first " + i, T0);
            await rig.Pass();
            Check("history rig: everything went up", rig.DriveTree().Count == count, rig.DriveTree().Count.ToString());

            for (int i = 0; i < count; i++) rig.Write($"notes/{i:D3}.txt", "edited " + i, T0.AddHours(1));
            int updatesBefore = rig.Drive.Updates;
            await rig.Pass();
            var lines = SyncHistory.Read(rig.Pair.Id).Take(count).ToList();
            Check("history rig: the edits went up in place", rig.Drive.Updates - updatesBefore == count &&
                rig.DriveTree().Values.All(v => v.StartsWith("edited")), $"{rig.Drive.Updates - updatesBefore} updates");
            Check("each edit sent in place is in the history as updated in Drive, not as a new upload",
                lines.Count == count && lines.All(e => e.What == "Updated in Drive"),
                string.Join(", ", lines.Select(e => e.What).Distinct()));
        }
    }
}
