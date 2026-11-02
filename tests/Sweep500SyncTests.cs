using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of the folder sync.
    /// </summary>
    internal static class Sweep500SyncTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        private const double Fast = 0.0005;
        private static readonly DateTime T0 = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the folder sync:");
            var previous = Settings.OverrideAppDataDir;
            var sandbox = Path.Combine(Path.GetTempPath(), "en-sweep500-sync-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            Settings.OverrideAppDataDir = Path.Combine(sandbox, "appdata");
            try
            {
                await DeadWatcherTests(sandbox);
                await EditedWhileQueuedTests(sandbox);
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(sandbox, true); } catch { }
            }
            Console.WriteLine();
        }

        // ---------------- a rig: one Drive, one monitor, as many pairs as a test wants ----------------

        private sealed class Rig : IDisposable
        {
            public readonly FakeDrive Drive = new();
            public readonly string Top;
            public readonly DriveClient Client;
            public readonly DriveMonitor Monitor;
            public readonly List<string> Said = new();
            public readonly DriveSyncPair Pair;

            public Rig(string sandbox, SyncMode mode = SyncMode.TwoWay, bool deletes = false)
            {
                Top = Path.Combine(sandbox, Guid.NewGuid().ToString("N")[..8]);
                Client = new DriveClient(Drive, Fast);
                Monitor = new DriveMonitor(new GoogleDrive(), (id, message) => { lock (Said) Said.Add(id + ": " + message); }, _ => { })
                {
                    WaitScale = Fast,
                    DiskOfRoot = _ => (1L << 42, 1L << 43, "T:"),
                    RecycleLocal = path => { File.Delete(path); return null; },
                    PatienceLimit = TimeSpan.FromSeconds(5),
                    SettleTime = TimeSpan.Zero,
                };
                Pair = AddPair("A", mode, deletes);
            }

            /// <summary>Another pair on the same Drive and monitor, not yet given to the monitor.</summary>
            public DriveSyncPair AddPair(string name, SyncMode mode, bool deletes = false)
            {
                var local = Path.Combine(Top, name);
                Directory.CreateDirectory(local);
                return new DriveSyncPair
                {
                    Name = name, LocalFolder = local, DriveFolderId = Drive.AddFolder(name), DriveFolderPath = "My Drive/" + name,
                    Mode = mode, CopyDeletes = deletes, CheckMinutes = 0,
                };
            }

            public async Task<string?> Pass(DriveSyncPair? pair = null)
            {
                pair ??= Pair;
                await Monitor.SyncOneAsync(Client, pair, CancellationToken.None);
                return Monitor.StatusOf(pair.Id).Problem;
            }

            public string Write(DriveSyncPair pair, string relative, string text, DateTime when)
            {
                var path = Path.Combine(pair.LocalFolder, relative.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
                File.SetLastWriteTimeUtc(path, when);
                return path;
            }

            public string Put(DriveSyncPair pair, string relative, string text, DateTime when)
            {
                var parent = pair.DriveFolderId;
                var parts = relative.Split('/');
                foreach (var folder in parts[..^1])
                    parent = Drive.Find(parent, folder) ?? Drive.AddFolder(folder, parent);
                return Drive.AddFile(parts[^1], Encoding.UTF8.GetBytes(text), when, parent);
            }

            public Dictionary<string, string> DriveTree(DriveSyncPair pair) =>
                Drive.Tree(pair.DriveFolderId).ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetString(kv.Value), StringComparer.OrdinalIgnoreCase);

            public List<string> PcFiles(DriveSyncPair pair) =>
                !Directory.Exists(pair.LocalFolder)
                    ? new List<string>()
                    : Directory.EnumerateFiles(pair.LocalFolder, "*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(pair.LocalFolder, f).Replace('\\', '/'))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

            public void Dispose()
            {
                Monitor.Dispose();
                try { Directory.Delete(Top, true); } catch { }
            }
        }

        private static string Names(IEnumerable<string> names) => string.Join(",", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        /// <summary>Waits up to <paramref name="ms"/> for something to come true.</summary>
        private static async Task<bool> Until(Func<bool> ok, int ms)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                if (ok()) return true;
                await Task.Delay(50);
            }
            return ok();
        }

        // ---------------- 1. a watcher that died, or was never made ----------------

        /// <summary>A real watcher that can be made to fail the way a dropped share or a pulled USB drive makes it.</summary>
        private sealed class DyingWatcher : FileSystemWatcher
        {
            public DyingWatcher(string path) : base(path) { }

            public void Die()
            {
                EnableRaisingEvents = false;
                OnError(new ErrorEventArgs(new System.ComponentModel.Win32Exception(64)));   // the network name is no longer available
            }
        }

        /// <summary>
        /// On a PC folder with no change journal (exFAT, a share) the watcher
        /// says what changed. When it failed (a share dropping, a USB drive
        /// pulled), it was never replaced: one whole walk later its empty
        /// account was trusted again, so new PC files waited for the daily walk
        /// while Status said all was synced. And a folder missing when the app
        /// started never got a watcher at all, so PC changes waited for the
        /// minute's check or longer.
        /// </summary>
        private static async Task DeadWatcherTests(string sandbox)
        {
            var made = new List<DyingWatcher>();
            DyingWatcher? Newest() { lock (made) return made.Count > 0 ? made[^1] : null; }

            // A watcher that fails.
            using (var rig = new Rig(sandbox))
            {
                rig.Monitor.JournalOf = _ => null;
                rig.Monitor.MakeWatcher = path => { var w = new DyingWatcher(path); lock (made) made.Add(w); return w; };
                rig.Write(rig.Pair, "a.txt", "one", T0);
                rig.Monitor.Reload(new[] { rig.Pair });
                await Until(() => Newest()?.EnableRaisingEvents == true, 5000);
                await Task.Delay(200);
                await rig.Pass();
                await rig.Pass();
                Check("dead watcher: with no change journal, a pass goes by the watcher once it has watched a walk",
                    rig.Monitor.LastPcSide.EndsWith("folders changed", StringComparison.Ordinal), rig.Monitor.LastPcSide);

                Newest()!.Die();
                await rig.Pass();
                rig.Monitor.TakeDueForTests(rig.Pair.Id);
                rig.Write(rig.Pair, "late.txt", "two", T0);
                Check("dead watcher: a PC change after the watcher failed still makes the pair due within seconds",
                    await Until(() => rig.Monitor.TakeDueForTests(rig.Pair.Id), 5000));
                await rig.Pass();
                Check("dead watcher: and the new PC file goes up, not a day later", rig.DriveTree(rig.Pair).ContainsKey("late.txt"),
                    $"{rig.Monitor.LastPcSide}; Drive holds {Names(rig.DriveTree(rig.Pair).Keys)}");
            }

            // A PC folder that vanished and was made again: the old watcher went with the old folder.
            using (var rig = new Rig(sandbox))
            {
                rig.Monitor.JournalOf = _ => null;
                rig.Write(rig.Pair, "a.txt", "one", T0);
                rig.Monitor.Reload(new[] { rig.Pair });
                await Task.Delay(500);
                await rig.Pass();
                await rig.Pass();
                var away = rig.Pair.LocalFolder + " away";
                Directory.Move(rig.Pair.LocalFolder, away);
                await rig.Pass();
                Directory.CreateDirectory(rig.Pair.LocalFolder);
                File.Copy(Path.Combine(away, "a.txt"), Path.Combine(rig.Pair.LocalFolder, "a.txt"));
                File.SetLastWriteTimeUtc(Path.Combine(rig.Pair.LocalFolder, "a.txt"), T0);
                await rig.Pass();
                rig.Monitor.TakeDueForTests(rig.Pair.Id);
                rig.Write(rig.Pair, "late.txt", "two", T0);
                bool due = await Until(() => rig.Monitor.TakeDueForTests(rig.Pair.Id), 5000);
                await rig.Pass();
                Check("dead watcher: a PC folder made again is watched again, and its new file goes up",
                    due && rig.DriveTree(rig.Pair).ContainsKey("late.txt"),
                    $"due {due}; {rig.Monitor.LastPcSide}; Drive holds {Names(rig.DriveTree(rig.Pair).Keys)}");
            }

            // A PC folder missing when the app started (a USB drive not yet plugged in).
            using (var rig = new Rig(sandbox))
            {
                using var there = new ManualResetEventSlim(false);
                rig.Monitor.FolderThere = path => there.IsSet && Directory.Exists(path);
                rig.Write(rig.Pair, "a.txt", "one", T0);
                rig.Monitor.Reload(new[] { rig.Pair });
                await Task.Delay(500);
                there.Set();
                await rig.Pass();
                rig.Monitor.TakeDueForTests(rig.Pair.Id);
                rig.Write(rig.Pair, "b.txt", "two", T0);
                Check("missing at start: once the PC folder is there, a change in it makes the pair due within seconds",
                    await Until(() => rig.Monitor.TakeDueForTests(rig.Pair.Id), 5000));
            }

            // The watcher's own account, once it has failed.
            JournalDiesTests();
        }

        private static void JournalDiesTests()
        {
            var journal = new PcWatchJournal();
            journal.Take(out int epoch);
            journal.Walked(epoch);
            journal.Note("x/y.txt", WatcherChangeTypes.Created);
            Check("watch journal: trusted after a whole walk", journal.Take(out _) != null);
            journal.Die();
            journal.Take(out epoch);
            journal.Walked(epoch);
            Check("watch journal: a watcher that failed for good is never trusted again, however many walks follow",
                journal.Dead && journal.Take(out _) == null);
        }

        // ---------------- 2. a pair edited while another pair's pass runs ----------------

        /// <summary>
        /// The minute's loop listed the pairs due, then ran them one after
        /// another from that list. A pair edited while an earlier pair's pass
        /// ran was then synced with the copy from before the edit: Free up
        /// space just unticked still cleared its files off the PC. Its pass now
        /// takes the pair as settings hold it when its turn comes, and reads
        /// the generation with it, so an edit during its own wait for an
        /// unfinished pass counts too.
        /// </summary>
        private static async Task EditedWhileQueuedTests(string sandbox)
        {
            async Task<(bool OnPc, bool OnDrive)> Run(bool edit)
            {
                using var rig = new Rig(sandbox, SyncMode.UploadOnly);
                var b = rig.AddPair("B", SyncMode.UploadOnly);
                b.DeleteAfterUpload = true;
                rig.Write(rig.Pair, "a.txt", "for A", T0);
                var bFile = rig.Write(b, "b.txt", "for B", T0);
                rig.Monitor.Reload(new[] { rig.Pair, b });
                int edits = 0;
                if (edit)
                    rig.Monitor.KillPoint = what =>
                    {
                        if (what != "uploaded a.txt" || Interlocked.Exchange(ref edits, 1) != 0) return;
                        var kept = b.Copy();
                        kept.DeleteAfterUpload = false;
                        rig.Monitor.Reload(new[] { rig.Pair, kept });
                    };
                await rig.Monitor.RunDueAsync(rig.Client, all: true, DateTime.UtcNow, CancellationToken.None);
                rig.Monitor.KillPoint = null;
                return (File.Exists(bFile), rig.DriveTree(b).ContainsKey("b.txt"));
            }

            var control = await Run(edit: false);
            Check("queued edit: the control frees B's file once it is on Drive", !control.OnPc && control.OnDrive,
                $"on PC {control.OnPc}, on Drive {control.OnDrive}");
            var edited = await Run(edit: true);
            Check("queued edit: Free up space unticked while another pair's pass ran is honoured: B's file stays on the PC",
                edited.OnPc && edited.OnDrive, $"on PC {edited.OnPc}, on Drive {edited.OnDrive}");

            // The edit lands while B's pass waits after an unfinished one.
            var crashWait = DriveMonitor.AfterCrashWait;
            DriveMonitor.AfterCrashWait = TimeSpan.FromSeconds(1.5);
            try
            {
                using var rig = new Rig(sandbox, SyncMode.UploadOnly);
                var b = rig.Pair;
                b.DeleteAfterUpload = true;
                var bFile = rig.Write(b, "b.txt", "for B", T0);
                rig.Monitor.Reload(new[] { b });
                Directory.CreateDirectory(DriveMonitor.StateDirectory);
                File.WriteAllText(Path.Combine(DriveMonitor.StateDirectory, b.Id + ".running"),
                    DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                var editLater = Task.Run(async () =>
                {
                    await Task.Delay(400);
                    var kept = b.Copy();
                    kept.DeleteAfterUpload = false;
                    rig.Monitor.Reload(new[] { kept });
                });
                await rig.Monitor.RunDueAsync(rig.Client, all: true, DateTime.UtcNow, CancellationToken.None);
                await editLater;
                Check("queued edit: an edit while the pass waits after an unfinished one is honoured too",
                    File.Exists(bFile) && rig.DriveTree(b).ContainsKey("b.txt"),
                    $"on PC {File.Exists(bFile)}, Drive holds {Names(rig.DriveTree(b).Keys)}");
            }
            finally { DriveMonitor.AfterCrashWait = crashWait; }
        }
    }
}
