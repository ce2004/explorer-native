using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The Google Drive folder monitor, run for real against <see cref="FakeDrive"/>:
    /// the real DriveClient (its retries and timeouts), the real DriveMonitor
    /// (its planner, transfers, state file and resume), scratch folders on the
    /// PC, and a Drive in memory. No account, no network, nothing under
    /// %APPDATA%\ExplorerNative: the state directory is redirected for the run.
    /// Waits for Google are scaled down so "waits for ever" takes milliseconds.
    /// </summary>
    internal static class FakeDriveMonitorTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        private const double Fast = 0.0005;

        private static readonly DateTime T0 = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;
            Console.WriteLine("Google Drive monitor, against a fake Drive:");

            var previous = Settings.OverrideAppDataDir;
            var sandbox = Path.Combine(Path.GetTempPath(), "en-monitor-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            Settings.OverrideAppDataDir = Path.Combine(sandbox, "appdata");
            try
            {
                TransientTests();
                await SharedLookTests(sandbox);
                await FastDownloadTests(sandbox);
                await WholeDriveTests(sandbox);
                await ModeTests(sandbox);
                await AdoptionTests(sandbox);
                await ConflictTests(sandbox);
                await DeleteTests(sandbox);
                await RootGoneTests(sandbox);
                await CancelAndResumeTests(sandbox);
                await RateLimitTests(sandbox);
                await ChangedDuringUploadTests(sandbox);
                await ManyFilesTests(sandbox);
                await SpaceTests(sandbox);
                await OddNameTests(sandbox);
                await FlakyConnectionTests(sandbox);
                await CrashTests(sandbox);
                await SecondReviewTests(sandbox);
                await ThirdRoundTests(sandbox);
                await FourthRoundTests(sandbox);
                await EmptyFolderTests(sandbox);
                await SettleWakeTests(sandbox);
                await FreeUpTests(sandbox);
                NoDialogTests();
                await CheckClientCancelTests();
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(sandbox, true); } catch { }
            }
        }

        // ---------------- the rig ----------------

        private sealed class Rig : IDisposable
        {
            public readonly FakeDrive Drive = new();
            public readonly string Local;
            public readonly string FolderId;
            public readonly DriveSyncPair Pair;
            public readonly List<string> Said = new();
            public readonly List<string> Recycled = new();
            public DriveClient Client;
            public DriveMonitor Monitor;

            public Rig(string sandbox, SyncMode mode, bool deletes = false)
            {
                var top = Path.Combine(sandbox, Guid.NewGuid().ToString("N")[..8]);
                Local = Path.Combine(top, "local");
                Directory.CreateDirectory(Local);
                FolderId = Drive.AddFolder("Sync");
                Pair = new DriveSyncPair
                {
                    Name = "Test", LocalFolder = Local, DriveFolderId = FolderId, DriveFolderPath = "My Drive/Sync",
                    Mode = mode, CopyDeletes = deletes, CheckMinutes = 0,
                };
                Client = new DriveClient(Drive, Fast);
                Monitor = NewMonitor();
            }

            public DriveMonitor NewMonitor() =>
                new(new GoogleDrive(), (id, message) => { lock (Said) Said.Add(id + ": " + message); }, _ => { })
                {
                    WaitScale = Fast,
                    DiskOfRoot = _ => (1L << 42, 1L << 43, "T:"),
                    RecycleLocal = path =>
                    {
                        lock (Recycled) Recycled.Add(Path.GetFileName(path));
                        File.Delete(path);
                        return null;
                    },
                };

            /// <summary>The app closing and starting again: a new monitor and client, the same state file.</summary>
            public void Restart()
            {
                var recycle = Monitor.RecycleLocal;
                var disk = Monitor.DiskOfRoot;
                Monitor.Dispose();
                Client = new DriveClient(Drive, Fast);
                Monitor = NewMonitor();
                Monitor.RecycleLocal = recycle;
                Monitor.DiskOfRoot = disk;
            }

            public string StatePath => Path.Combine(DriveMonitor.StateDirectory, Pair.Id + ".json");

            public async Task<string?> Pass(CancellationToken token = default)
            {
                await Monitor.SyncOneAsync(Client, Pair, token);
                return Monitor.StatusOf(Pair.Id).Problem;
            }

            public void Write(string relative, string text, DateTime modifiedUtc) =>
                Write(relative, Encoding.UTF8.GetBytes(text), modifiedUtc);

            public void Write(string relative, byte[] data, DateTime modifiedUtc)
            {
                var path = Path.Combine(Local, relative.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, data);
                File.SetLastWriteTimeUtc(path, modifiedUtc);
            }

            public string Put(string relative, string text, DateTime modifiedUtc)
            {
                var parent = FolderId;
                var parts = relative.Split('/');
                foreach (var folder in parts[..^1])
                    parent = Drive.Find(parent, folder) ?? Drive.AddFolder(folder, parent);
                return Drive.AddFile(parts[^1], Encoding.UTF8.GetBytes(text), modifiedUtc, parent);
            }

            public Dictionary<string, byte[]> LocalTree()
            {
                var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                if (!Directory.Exists(Local)) return result;
                foreach (var file in Directory.EnumerateFiles(Local, "*", SearchOption.AllDirectories))
                    if (!file.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                        result[Path.GetRelativePath(Local, file).Replace('\\', '/')] = File.ReadAllBytes(file);
                return result;
            }

            public Dictionary<string, byte[]> DriveTree() => Drive.Tree(FolderId);

            public (int, int, int, int, int) Counters() =>
                (Drive.Uploads, Drive.MediaReads, Drive.Trashes, Drive.Renames, Drive.Creates);

            public void Dispose()
            {
                Monitor.Dispose();
                try { Directory.Delete(Path.GetDirectoryName(Local)!, true); } catch { }
            }
        }

        private static string Names(Dictionary<string, byte[]> tree) =>
            string.Join(",", tree.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

        private static string Text(Dictionary<string, byte[]> tree, string key) =>
            tree.TryGetValue(key, out var bytes) ? Encoding.UTF8.GetString(bytes) : "(missing)";

        private static bool SameTrees(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && other.AsSpan().SequenceEqual(kv.Value));

        private static byte[] Noise(int length, int seed)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        // ---------------- empty folders, and files held back while they settle ----------------

        private static async Task EmptyFolderTests(string sandbox)
        {
            using (var rig = new Rig(sandbox, SyncMode.UploadOnly))
            {
                rig.Pair.SkipExtensions = ".sync";
                Directory.CreateDirectory(Path.Combine(rig.Local, "Canada"));
                Directory.CreateDirectory(Path.Combine(rig.Local, "US", "New York"));
                rig.Write(".sync/ID", "resilio", T0);
                rig.Write("US/book.mp3.!sync", "half", T0);
                var hidden = Directory.CreateDirectory(Path.Combine(rig.Local, "Hidden"));
                hidden.Attributes |= FileAttributes.Hidden;
                rig.Write("a.txt", "", T0);

                Equal("empty folders: the first pass has no problem", "", await rig.Pass() ?? "");
                Check("an empty folder is made in Drive", rig.Drive.Find(rig.FolderId, "Canada") != null);
                Check("and an empty folder inside an empty folder", rig.Drive.Find(rig.FolderId, "US/New York") != null);
                Check("a folder the skip list names is not", rig.Drive.Find(rig.FolderId, ".sync") == null);
                Check("nor a hidden folder", rig.Drive.Find(rig.FolderId, "Hidden") == null);
                Equal("and Resilio's download in progress is not uploaded", "a.txt", Names(rig.DriveTree()));

                int creates = rig.Drive.Creates;
                await rig.Pass();
                Equal("a second pass makes no folder twice", creates.ToString(), rig.Drive.Creates.ToString());

                Directory.CreateDirectory(Path.Combine(rig.Local, "Later"));
                await rig.Pass();
                Check("a folder made after the first pass is made in Drive too", rig.Drive.Find(rig.FolderId, "Later") != null);
            }

            using (var rig = new Rig(sandbox, SyncMode.DownloadOnly))
            {
                Directory.CreateDirectory(Path.Combine(rig.Local, "Canada"));
                await rig.Pass();
                Check("download only never makes a folder in Drive", rig.Drive.Find(rig.FolderId, "Canada") == null);
            }

            using (var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true))
            {
                Directory.CreateDirectory(Path.Combine(rig.Local, "Kept"));
                await rig.Pass();
                var id = rig.Drive.Find(rig.FolderId, "Kept");
                Check("two-way makes an empty folder too", id != null);
                rig.Drive.Trash(id!);
                await rig.Pass();
                Check("but with deletes on, one deleted in Drive is not made again", rig.Drive.Find(rig.FolderId, "Kept") == null);
            }
        }

        private static async Task SettleWakeTests(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.UploadOnly);
            rig.Monitor.SettleTime = TimeSpan.FromSeconds(3);
            rig.Write("new.txt", "just saved", DateTime.UtcNow.AddSeconds(-1));
            await rig.Pass();
            Equal("a file saved a moment ago waits to settle", "", Names(rig.DriveTree()));
            var wake = rig.Monitor.LastSettleWake;
            Check("and the pair asks to run again once it has, not on the minute",
                wake is { } w && w > TimeSpan.Zero && w <= TimeSpan.FromSeconds(3) + DriveMonitor.SettleMargin, wake?.ToString());
            await Task.Delay(wake ?? TimeSpan.Zero);
            await rig.Pass();
            Equal("and that pass sends it", "new.txt", Names(rig.DriveTree()));
        }

        // ---------------- free up space: PC files deleted once safely in Drive ----------------

        private static async Task FreeUpTests(string sandbox)
        {
            using (var rig = new Rig(sandbox, SyncMode.UploadOnly))
            {
                rig.Pair.DeleteAfterUpload = true;
                rig.Pair.SkipExtensions = ".iso .sync";
                rig.Monitor.SettleTime = TimeSpan.FromSeconds(5);
                rig.Write("a.txt", "alpha", T0);
                rig.Write("Book/01.mp3", Noise(300_000, 1), T0);
                rig.Write("disc.iso", "skipped by the list", T0);
                rig.Write(".sync/ID", "resilio's own", T0);
                rig.Write("Book/02.mp3.!sync", "resilio still downloading", T0);
                rig.Write("old.tmp", "temp", T0);
                rig.Write("song.partial", "half a download", T0);
                rig.Write("~$report.docx", "office lock", T0);
                rig.Write("hidden.txt", "hidden", T0);
                File.SetAttributes(Path.Combine(rig.Local, "hidden.txt"), FileAttributes.Hidden);
                rig.Write("fresh.txt", "saved a moment ago", DateTime.UtcNow);
                rig.Write("open.txt", "still being written", T0);
                using (var writer = new FileStream(Path.Combine(rig.Local, "open.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                    Equal("free up: the pass has no problem", "", await rig.Pass() ?? "");

                var onPc = rig.LocalTree();
                Check("a file safely in Drive is deleted from the PC", !onPc.ContainsKey("a.txt") && !onPc.ContainsKey("Book/01.mp3"),
                    Names(onPc));
                Check("and Drive keeps it, whole", Text(rig.DriveTree(), "a.txt") == "alpha" &&
                    rig.DriveTree().TryGetValue("Book/01.mp3", out var song) && song.AsSpan().SequenceEqual(Noise(300_000, 1)));
                foreach (var kept in new[] { "disc.iso", ".sync/ID", "Book/02.mp3.!sync", "old.tmp", "song.partial", "~$report.docx",
                                             "hidden.txt", "fresh.txt", "open.txt" })
                    Check($"free up never deletes {kept}", File.Exists(Path.Combine(rig.Local, kept.Replace('/', '\\'))));
                Check("nothing it left on the PC that never went up is in Drive",
                    !rig.DriveTree().Keys.Any(k => k.EndsWith(".!sync") || k.EndsWith(".tmp") || k.EndsWith(".partial") || k.StartsWith(".sync")));

                await rig.Pass();
                Check("the next pass leaves Drive's copies alone", rig.Drive.Trashes == 0 && rig.DriveTree().ContainsKey("a.txt"));

                // The same book turning up in another folder is a new file, not a move.
                rig.Write("Canada/01.mp3", Noise(300_000, 1), T0);
                await rig.Pass();
                var drive = rig.DriveTree();
                Check("an identical file elsewhere goes up beside it, never moving Drive's copy",
                    drive.ContainsKey("Book/01.mp3") && drive.ContainsKey("Canada/01.mp3"), Names(drive));
                Check("and is cleared off the PC in turn", !File.Exists(Path.Combine(rig.Local, "Canada", "01.mp3")));
                Check("the pass says what it cleared, apart from deletes",
                    rig.Said.Any(s => s.Contains("cleared off this PC")) && !rig.Said.Any(s => s.Contains(" deleted")), string.Join(" | ", rig.Said));
            }

            // A pass cut off after deleting a file but before its record was
            // dropped: the record outlives the file. The same book arriving in
            // another folder must not be taken for a move of Drive's copy.
            using (var rig = new Rig(sandbox, SyncMode.UploadOnly))
            {
                rig.Write("Book/01.mp3", Noise(300_000, 7), T0);
                rig.Write("locked.txt", "read-only", T0);
                File.SetAttributes(Path.Combine(rig.Local, "locked.txt"), FileAttributes.ReadOnly);
                await rig.Pass();
                rig.Pair.DeleteAfterUpload = true;
                File.Delete(Path.Combine(rig.Local, "Book", "01.mp3"));
                rig.Write("Canada/01.mp3", Noise(300_000, 7), T0);
                await rig.Pass();
                var drive = rig.DriveTree();
                Check("free up: a record left behind by a cut-off pass never moves Drive's copy",
                    drive.ContainsKey("Book/01.mp3") && drive.ContainsKey("Canada/01.mp3"), Names(drive));
                Check("free up: a read-only file is left on the PC",
                    File.Exists(Path.Combine(rig.Local, "locked.txt")) && drive.ContainsKey("locked.txt"));
                File.SetAttributes(Path.Combine(rig.Local, "locked.txt"), FileAttributes.Normal);
            }

            using (var rig = new Rig(sandbox, SyncMode.TwoWay))
            {
                rig.Pair.DeleteAfterUpload = true;
                rig.Write("a.txt", "alpha", T0);
                await rig.Pass();
                Check("free up is ignored in two-way", File.Exists(Path.Combine(rig.Local, "a.txt")));
            }

            using (var rig = new Rig(sandbox, SyncMode.UploadOnly, deletes: true))
            {
                rig.Pair.DeleteAfterUpload = true;
                rig.Write("a.txt", "alpha", T0);
                await rig.Pass();
                Check("and with deletes copied, where it would delete Drive's copy too",
                    File.Exists(Path.Combine(rig.Local, "a.txt")) && rig.DriveTree().ContainsKey("a.txt"));
            }
        }

        // ---------------- downloads: many at once, streamed, and the summary's listing ----------------

        private static async Task FastDownloadTests(string sandbox)
        {
            // Forty new songs come down at once, each in one request.
            using (var rig = new Rig(sandbox, SyncMode.DownloadOnly))
            {
                var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < 40; i++)
                {
                    var data = Noise(50_000 + i * 997, i);
                    var parent = rig.Drive.Find(rig.FolderId, $"Album {i % 4}") ?? rig.Drive.AddFolder($"Album {i % 4}", rig.FolderId);
                    rig.Drive.AddFile($"{i:00} song.mp3", data, T0.AddMinutes(i), parent);
                    expected[$"Album {i % 4}/{i:00} song.mp3"] = data;
                }
                // One already on the PC, different: the careful way, its old copy to the Recycle Bin.
                rig.Write("Album 0/00 song.mp3", "mine", T0.AddDays(-1));

                int reads = rig.Drive.MediaReads;
                await rig.Pass();
                var tree = rig.LocalTree();
                Check("fast downloads: every new song came down whole",
                    expected.All(kv => tree.TryGetValue(kv.Key, out var got) && got.AsSpan().SequenceEqual(kv.Value)),
                    $"{tree.Count} files on the PC");
                Equal("fast downloads: one request a file, not one per 8MB", "40", (rig.Drive.MediaReads - reads).ToString());
                Check("fast downloads: the PC's own different copy went to the Recycle Bin first, as before",
                    rig.Recycled.Contains("00 song.mp3"), string.Join(", ", rig.Recycled));
                Check("fast downloads: no .partial left behind",
                    !Directory.EnumerateFiles(rig.Local, "*.partial", SearchOption.AllDirectories).Any());
                Equal("fast downloads: and the history has each one", "40",
                    SyncHistory.Read(rig.Pair.Id).Count(e => e.What is "Downloaded" or "Updated on the PC").ToString());
                await rig.Pass();
                Check("fast downloads: the next pass has nothing to do", rig.Drive.MediaReads - reads == 40);
            }

            // Twelve new song-sized PC files go up at once, each exactly once.
            using (var rig = new Rig(sandbox, SyncMode.UploadOnly))
            {
                var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < 12; i++)
                {
                    var data = Noise(5 * 1024 * 1024 + i * 1000, 100 + i);
                    rig.Write($"Album {i % 3}/{i:00} song.flac", data, T0.AddMinutes(i));
                    expected[$"Album {i % 3}/{i:00} song.flac"] = data;
                }
                int uploads = rig.Drive.Uploads;
                await rig.Pass();
                var drive = rig.DriveTree();
                Check("fast uploads: every song-sized file went up whole",
                    expected.All(kv => drive.TryGetValue(kv.Key, out var got) && got.AsSpan().SequenceEqual(kv.Value)),
                    $"{drive.Count} on Drive");
                Equal("fast uploads: one upload each, no second copies", "12", (rig.Drive.Uploads - uploads).ToString());
                Check("fast uploads: no name twice in a Drive folder", rig.Drive.Duplicates(rig.FolderId).Count == 0,
                    string.Join(", ", rig.Drive.Duplicates(rig.FolderId)));
                Equal("fast uploads: and the history has each one", "12",
                    SyncHistory.Read(rig.Pair.Id).Count(e => e.What == "Uploaded").ToString());
            }

            // A big file streamed in one go, the connection dropping half way: picked up from where it stopped.
            using (var rig = new Rig(sandbox, SyncMode.DownloadOnly))
            {
                var data = Noise(20 * 1024 * 1024 + 123, 77);
                rig.Drive.AddFile("film.mkv", data, T0, rig.FolderId);
                rig.Drive.DropMediaMidBody = 1;
                int reads = rig.Drive.MediaReads;
                await rig.Pass();
                var tree = rig.LocalTree();
                Check("streamed download: a 20MB file whose connection dropped half way arrives exactly",
                    tree.TryGetValue("film.mkv", out var got) && got.AsSpan().SequenceEqual(data), $"{(got?.Length ?? 0):N0} bytes");
                Equal("streamed download: two requests, the second from where the first stopped", "2",
                    (rig.Drive.MediaReads - reads).ToString());
                Check("streamed download: the second asked from the middle, not from the start",
                    rig.Drive.MediaOffsets.Count >= 2 && rig.Drive.MediaOffsets[^1] > 0, string.Join(", ", rig.Drive.MediaOffsets));
            }
        }

        // ---------------- a whole drive, and Windows notifications ----------------

        private static async Task WholeDriveTests(string sandbox)
        {
            // A drive's own top folder stays "D:\": "D:" alone is the current folder on D.
            Equal("whole drive: D:\\ is kept as D:\\", @"D:\", DriveMonitor.FolderPathOf(@"D:\"));
            Equal("whole drive: a bare D: is the drive, not D's current folder", @"D:\", DriveMonitor.FolderPathOf("D:"));
            Check("the Windows drive typed bare is refused whole",
                DriveMonitor.Refusal(Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\'))?.Contains("drive Windows is on") == true);
            Equal("whole drive: D:\\. is the top of D too", @"D:\", DriveMonitor.FolderPathOf(@"D:\."));
            Equal("whole drive: an ordinary folder loses its last separator", @"D:\Music", DriveMonitor.FolderPathOf(@"D:\Music\"));
            Check("whole drive: the drive Windows is on is refused whole",
                DriveMonitor.Refusal(Path.GetPathRoot(Environment.SystemDirectory)!) is { } no && no.Contains("Windows is on"));
            Equal("whole drive: a whole drive is called D drive", "D drive",
                DriveMonitor.DisplayName(new DriveSyncPair { LocalFolder = @"D:\" }));
            var example = DrivePairForm.Example(SyncMode.TwoWay, @"D:\", "My Drive", false);
            Check("whole drive: the example says the D drive and My Drive",
                example.Contains("the D drive") && example.Contains("My Drive in Google Drive"), example);
            Equal("whole drive: a shared drive's name made safe for Windows", "Team_ A_", DriveMonitorForm.WindowsName("Team: A?"));

            // A real drive root, synced both ways: a spare letter put on a scratch folder.
            using var rig = new Rig(sandbox, SyncMode.TwoWay);
            var letter = "PQRSTUVWXYZ".Select(c => c + ":").FirstOrDefault(l => !Directory.Exists(l + "\\"));
            if (letter == null) { Check("whole drive: a spare drive letter to test with", false); return; }
            static bool Subst(string args)
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("subst", args)
                    { CreateNoWindow = true, UseShellExecute = false })!;
                p.WaitForExit(10000);
                return p.ExitCode == 0;
            }
            if (!Subst($"{letter} \"{rig.Local}\"")) { Check("whole drive: subst made a drive letter", false); return; }
            try
            {
                rig.Pair.LocalFolder = DriveMonitor.FolderPathOf(letter + "\\");
                rig.Write("song.mp3", "on the PC", T0);
                rig.Write("Album/track.mp3", "in a folder", T0);
                rig.Put("from drive.txt", "from Google", T0);
                await rig.Pass();
                var drive = rig.DriveTree();
                Check("whole drive: a drive's top folder syncs both ways",
                    drive.ContainsKey("song.mp3") && drive.ContainsKey("Album/track.mp3") &&
                    File.Exists(Path.Combine(rig.Local, "from drive.txt")),
                    $"Drive: {Names(drive)}; PC: {Names(rig.LocalTree())}");
                Check("whole drive: and nothing was put anywhere but the drive itself",
                    !File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "from drive.txt")));
            }
            finally { Subst($"{letter} /d"); }

            // Windows notifications: all off until turned on.
            var settings = new Settings();
            var shown = new List<string>();
            var before = WindowsToasts.Show;
            WindowsToasts.Show = (title, text) => shown.Add(title + ": " + text);
            try
            {
                Equal("notifications: none are on by default", "", settings.WindowsNotifications);
                WindowsToasts.Raise("sync.done", "Music synced: 3 uploaded", settings);
                Check("notifications: an event with its notification off shows nothing", shown.Count == 0);
                settings.WindowsNotifications = WindowsToasts.Format(new[] { "sync.done", "sync.space" });
                WindowsToasts.Raise("sync.done", "Music synced: 3 uploaded", settings);
                WindowsToasts.Raise("sync.error", "Music could not sync", settings);
                Check("notifications: one turned on shows, with no label for a title, and others still do not",
                    shown.Count == 1 && shown[0] == ": Music synced: 3 uploaded", string.Join(" | ", shown));
            }
            finally { WindowsToasts.Show = before; }
        }

        // ---------------- one look at Drive for every quiet pair ----------------

        private static async Task SharedLookTests(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.TwoWay);
            rig.Pair.CheckMinutes = 1;
            rig.Write("a.txt", "one", T0);
            var pairs = new List<DriveSyncPair> { rig.Pair };
            var t = DateTime.UtcNow;
            Task<List<DriveSyncPair>> Due(int minutes, bool all = false) =>
                rig.Monitor.DuePairsAsync(rig.Client, pairs, all, t.AddMinutes(minutes), default);

            Check("shared look: a pair asked for runs", (await Due(0, all: true)).Count == 1);
            await rig.Pass();
            await rig.Pass();
            Check("shared look: before there is an unbroken look at the feed, the pair runs", (await Due(2)).Count == 1);
            await rig.Pass();

            int requests = rig.Drive.Requests;
            var quiet = await Due(4);
            Check("shared look: a quiet minute does not run a pair whose last pass was clean", quiet.Count == 0);
            Equal("shared look: and costs one request", "1", (rig.Drive.Requests - requests).ToString());
            Check("shared look: nor does the next quiet minute", (await Due(6)).Count == 0);

            rig.Put("b.txt", "two", T0);
            Check("shared look: a change on Drive runs it", (await Due(8)).Count == 1);
            await rig.Pass();
            Check("shared look: and the new Drive file comes down", rig.LocalTree().ContainsKey("b.txt"));
            await rig.Pass();
            Check("shared look: quiet again afterwards", (await Due(10)).Count == 0);

            rig.Write("c.txt", "three", T0);
            rig.Drive.ChangesFail = 410;
            try { Check("shared look: a look Google refuses runs the pair", (await Due(12)).Count == 1); }
            finally { rig.Drive.ChangesFail = 0; }
            Check("shared look: and the look starts again before trusting quiet", (await Due(14)).Count == 1);
            await rig.Pass();
            Check("shared look: the PC file went up meanwhile", rig.DriveTree().ContainsKey("c.txt"));
            await rig.Pass();

            var most = DriveMonitor.QuietMost;
            DriveMonitor.QuietMost = TimeSpan.Zero;
            try { Check("shared look: however quiet Drive is, a pair still runs every so often", (await Due(16)).Count == 1); }
            finally { DriveMonitor.QuietMost = most; }

            rig.Pair.CheckMinutes = 0;
            Check("shared look: a pair checked only on request is not looked for", (await Due(18)).Count == 0);

            // The history those passes left, newest first.
            var history = SyncHistory.Read(rig.Pair.Id);
            var said = history.Select(e => SyncHistory.Describe(e, DateTime.Now)).ToList();
            Check("history: every file the passes copied is in it, newest first",
                history.Count == 3 && history[0].What == "Uploaded" && history[0].Path == "c.txt" &&
                history[1].What == "Downloaded" && history[1].Path == "b.txt" &&
                history[2].What == "Uploaded" && history[2].Path == "a.txt",
                string.Join(" | ", said));
            Check("history: a line reads as a sentence", said.Count > 0 && said[0].StartsWith("Uploaded c.txt, today at "), said.FirstOrDefault());

            const string big = "history-test-pair";
            for (int i = 0; i < 1500; i++) SyncHistory.Add(big, "Downloaded", $"song {i}.mp3");
            SyncHistory.Flush(big);
            var kept = SyncHistory.Read(big);
            Check("history: a big sync keeps its newest files and says how many more",
                kept.Count == SyncHistory.Keep && kept[0].Path == "song 1499.mp3" &&
                kept[^1].What == "And" && kept[^1].Path == "501 more files earlier in the same sync",
                $"{kept.Count} kept, newest {kept.FirstOrDefault()?.Path}, oldest {kept.LastOrDefault()?.What} {kept.LastOrDefault()?.Path}");
            DriveMonitor.ForgetRemovedPairs(new[] { rig.Pair });
            Check("history: a removed folder's history goes with it", !File.Exists(SyncHistory.PathOf(big)));

            // Sync all, Pause all, Resume all.
            using var all = rig.NewMonitor();
            Equal("sync all with nothing synced", "You are not syncing any folders with Google Drive yet", all.SyncAllNow());
            // One folder is named, never "1 synced folder" (Conner, 3.0.x).
            all.SetPairs(new List<DriveSyncPair>
            {
                new() { Name = "Music", LocalFolder = rig.Local, DriveFolderId = "m", CheckMinutes = 0 },
            });
            Equal("sync all with one folder names it", "Syncing Music now", all.SyncAllNow());
            Equal("pause all with one folder names it", "Paused Music. Nothing syncs until you resume", all.PauseAll(true));
            Equal("sync all with the one folder paused names it", "Music is paused. Resume it first", all.SyncAllNow());
            Equal("resume all with one folder names it", "Resumed Music. Syncing now", all.PauseAll(false));
            var two = new List<DriveSyncPair>
            {
                new() { Name = "A", LocalFolder = rig.Local, DriveFolderId = "a", CheckMinutes = 0 },
                new() { Name = "B", LocalFolder = rig.Local, DriveFolderId = "b", CheckMinutes = 0 },
            };
            all.SetPairs(two);
            Equal("sync all says how many, in words", "Syncing both folders now", all.SyncAllNow());
            Equal("pause all says what it did", "Paused both folders. Nothing syncs until you resume", all.PauseAll(true));
            Check("and every pair is paused, and the menu then offers Resume all", all.Pairs.All(p => p.Paused) && all.AllPaused);
            Equal("sync all while everything is paused says so", "Both folders are paused. Resume them first", all.SyncAllNow());
            Equal("resume all", "Resumed both folders. Syncing now", all.PauseAll(false));
            Check("and none is paused afterwards", all.Pairs.All(p => !p.Paused) && !all.AllPaused);
            var mixed = all.Pairs.ToList();
            mixed[1].Paused = true;
            all.SetPairs(mixed);
            Equal("sync all with one paused names the running one and the paused one", "Syncing A now. B is paused", all.SyncAllNow());
            all.PauseAll(false);
            foreach (var w in new[] { all.SyncAllNow(), all.PauseAll(true), all.PauseAll(false) })
                Check("sync all wording never says 'synced folder' or a bare 1", !w.Contains("synced folder") && !w.Contains(" 1 "), w);
        }

        // ---------------- what counts as "not now" ----------------

        private static void TransientTests()
        {
            var limited = FakeDrive.ErrorBody(403, "rateLimitExceeded", "Rate Limit Exceeded");
            var forbidden = FakeDrive.ErrorBody(403, "forbidden", "The user does not have permission");
            string Explained(int status, string body) => $"{status}: " + DriveClient.Explain(status, body);

            Check("a 403 rateLimitExceeded is waited out, though its sentence no longer names the reason",
                SyncBackoff.IsTransient(new DriveClient.DriveStatusException(403, limited, Explained(403, limited))));
            Check("a 403 that means no is not",
                !SyncBackoff.IsTransient(new DriveClient.DriveStatusException(403, forbidden, Explained(403, forbidden))));
            Check("a 502 whose body was an HTML page is waited out",
                SyncBackoff.IsTransient(new DriveClient.DriveStatusException(502, "<html>robot</html>",
                    "upload of a.txt failed after 6 attempts: Google Drive is temporarily unavailable")));
            Check("a 404 is not",
                !SyncBackoff.IsTransient(new DriveClient.DriveStatusException(404, "", "404: File not found")));
            Check("a resumable upload's last failure is looked into, not just its sentence",
                SyncBackoff.IsTransient(new InvalidOperationException("upload stopped at 4 MB of 12 MB after 5 attempts",
                    new DriveClient.UploadRefusedException(403, limited))));
        }

        // ---------------- the three modes ----------------

        private static async Task ModeTests(string sandbox)
        {
            using (var up = new Rig(sandbox, SyncMode.UploadOnly))
            {
                up.Write("a.txt", "pc a", T0);
                up.Write("sub/b.txt", "pc b", T0);
                up.Put("c.txt", "drive c", T0);
                var problem = await up.Pass();
                Equal("upload only: the PC's files go up, in their folders", "a.txt,c.txt,sub/b.txt", Names(up.DriveTree()));
                Equal("and nothing comes down", "a.txt,sub/b.txt", Names(up.LocalTree()));
                Check("with no problem", problem == null, problem);
                Equal("the bytes are the PC's", "pc b", Text(up.DriveTree(), "sub/b.txt"));
                Equal("and Drive is stamped with the PC's modified time", DriveClient.RfcTime(T0),
                    DriveClient.RfcTime(up.Drive.Get(up.Drive.Find(up.FolderId, "a.txt")!)!.Modified));
            }

            using (var down = new Rig(sandbox, SyncMode.DownloadOnly))
            {
                down.Write("a.txt", "pc a", T0);
                down.Put("c.txt", "drive c", T0.AddHours(1));
                down.Put("sub/d.txt", "drive d", T0);
                await down.Pass();
                Equal("download only: Drive's files come down, in their folders", "a.txt,c.txt,sub/d.txt", Names(down.LocalTree()));
                Equal("and nothing goes up", "c.txt,sub/d.txt", Names(down.DriveTree()));
                Equal("with Drive's modified time", T0.AddHours(1).ToString("O"),
                    File.GetLastWriteTimeUtc(Path.Combine(down.Local, "c.txt")).ToString("O"));
            }

            using (var two = new Rig(sandbox, SyncMode.TwoWay))
            {
                two.Write("a.txt", "pc a", T0);
                two.Write("deep/er/x.txt", "pc x", T0);
                two.Put("c.txt", "drive c", T0);
                two.Put("sub/d.txt", "drive d", T0);
                var problem = await two.Pass();
                Check("two-way: both sides end up the same", SameTrees(two.LocalTree(), two.DriveTree()),
                    Names(two.LocalTree()) + " | " + Names(two.DriveTree()));
                Equal("with everything from both", "a.txt,c.txt,deep/er/x.txt,sub/d.txt", Names(two.LocalTree()));
                Check("and no problem", problem == null, problem);

                var before = two.Counters();
                await two.Pass();
                Check("a second pass over a pair in step moves nothing", two.Counters() == before);

                two.Write("a.txt", "pc a, edited", T0.AddHours(2));
                two.Drive.Edit(two.Drive.Find(two.FolderId, "c.txt")!, Encoding.UTF8.GetBytes("drive c, edited"), T0.AddHours(3));
                await two.Pass();
                Equal("an edit on the PC goes up", "pc a, edited", Text(two.DriveTree(), "a.txt"));
                Equal("an edit in Drive comes down", "drive c, edited", Text(two.LocalTree(), "c.txt"));
                Check("and a replaced Drive file leaves no second copy behind", two.Drive.Duplicates(two.FolderId).Count == 0,
                    string.Join(",", two.Drive.Duplicates(two.FolderId)));
            }
        }

        // ---------------- adoption: the library on both sides ----------------

        private static async Task AdoptionTests(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.TwoWay);
            for (int i = 0; i < 30; i++)
            {
                var bytes = Noise(1000 + i * 37, i);
                // Different times on purpose: a copy made by hand keeps no dates.
                rig.Write($"album/track {i:D2}.flac", bytes, T0.AddDays(-i));
                var parent = rig.Drive.Find(rig.FolderId, "album") ?? rig.Drive.AddFolder("album", rig.FolderId);
                rig.Drive.AddFile($"track {i:D2}.flac", bytes, T0.AddMinutes(i), parent);
            }

            var problem = await rig.Pass();
            Check("a library already on both sides is adopted with nothing copied",
                rig.Drive.Uploads == 0 && rig.Drive.MediaReads == 0,
                $"uploads {rig.Drive.Uploads}, downloads {rig.Drive.MediaReads}");
            Check("and no conflict copies", !rig.LocalTree().Keys.Any(k => k.Contains("conflict")));
            Check("and no problem", problem == null, problem);

            var before = rig.Counters();
            await rig.Pass();
            Check("the next pass knows them all and moves nothing", rig.Counters() == before);

            rig.Write("album/track 03.flac", Noise(1000 + 3 * 37, 999), T0.AddDays(5));
            await rig.Pass();
            Check("an adopted file edited afterwards is sent like any other",
                rig.Drive.Uploads == 1 && rig.DriveTree()["album/track 03.flac"].AsSpan()
                    .SequenceEqual(rig.LocalTree()["album/track 03.flac"]));
        }

        // ---------------- conflicts ----------------

        private static async Task ConflictTests(string sandbox)
        {
            using (var rig = new Rig(sandbox, SyncMode.TwoWay))
            {
                rig.Write("song.txt", "first", T0);
                await rig.Pass();

                rig.Write("song.txt", "pc's edit", T0.AddHours(2));
                rig.Drive.Edit(rig.Drive.Find(rig.FolderId, "song.txt")!, Encoding.UTF8.GetBytes("drive's edit"), T0.AddHours(1));
                await rig.Pass();
                await rig.Pass();

                var local = rig.LocalTree();
                var aside = local.Keys.FirstOrDefault(k => k.StartsWith("song (conflict ", StringComparison.Ordinal));
                Equal("changed on both: the newer (the PC's) keeps the name", "pc's edit", Text(local, "song.txt"));
                Check("and Drive's is kept as a conflict copy", aside != null && Text(local, aside) == "drive's edit",
                    Names(local));
                Check("on both sides", SameTrees(local, rig.DriveTree()), Names(local) + " | " + Names(rig.DriveTree()));
                Check("announced", rig.Said.Any(s => s.StartsWith("sync.conflict", StringComparison.Ordinal)));
            }

            using (var rig = new Rig(sandbox, SyncMode.TwoWay))
            {
                rig.Write("song.txt", "first", T0);
                await rig.Pass();

                rig.Write("song.txt", "pc's edit", T0.AddHours(1));
                rig.Drive.Edit(rig.Drive.Find(rig.FolderId, "song.txt")!, Encoding.UTF8.GetBytes("drive's edit"), T0.AddHours(2));
                await rig.Pass();
                await rig.Pass();

                var local = rig.LocalTree();
                var aside = local.Keys.FirstOrDefault(k => k.StartsWith("song (conflict ", StringComparison.Ordinal));
                Equal("the newer being Drive's: Drive's keeps the name", "drive's edit", Text(local, "song.txt"));
                Check("and the PC's is the conflict copy, on both sides",
                    aside != null && Text(local, aside) == "pc's edit" && SameTrees(local, rig.DriveTree()), Names(local));
            }

            using (var rig = new Rig(sandbox, SyncMode.TwoWay))
            {
                rig.Pair.WhenBothChanged = ConflictChoice.PcWins;
                rig.Write("song.txt", "first", T0);
                await rig.Pass();
                rig.Write("song.txt", "pc's edit", T0.AddHours(1));
                rig.Drive.Edit(rig.Drive.Find(rig.FolderId, "song.txt")!, Encoding.UTF8.GetBytes("drive's edit"), T0.AddHours(2));
                await rig.Pass();
                Check("PC wins: the PC's copy replaces Drive's, and nothing is set aside",
                    Text(rig.DriveTree(), "song.txt") == "pc's edit" && rig.DriveTree().Count == 1 && rig.LocalTree().Count == 1);
            }
        }

        // ---------------- deletes ----------------

        private static async Task DeleteTests(string sandbox)
        {
            using (var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: false))
            {
                rig.Write("gone on pc.txt", "x", T0);
                rig.Write("gone in drive.txt", "y", T0);
                await rig.Pass();
                File.Delete(Path.Combine(rig.Local, "gone on pc.txt"));
                rig.Drive.Trash(rig.Drive.Find(rig.FolderId, "gone in drive.txt")!);
                await rig.Pass();
                Equal("deletes off: a file deleted on one side comes back from the other",
                    "gone in drive.txt,gone on pc.txt", Names(rig.LocalTree()));
                Check("on both sides", SameTrees(rig.LocalTree(), rig.DriveTree()));
                Check("and nothing is deleted anywhere", rig.Drive.Trashes == 0 && rig.Recycled.Count == 0);
            }

            using (var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true))
            {
                rig.Write("gone on pc.txt", "x", T0);
                rig.Write("gone in drive.txt", "y", T0);
                rig.Write("kept.txt", "z", T0);
                await rig.Pass();
                File.Delete(Path.Combine(rig.Local, "gone on pc.txt"));
                rig.Drive.Trash(rig.Drive.Find(rig.FolderId, "gone in drive.txt")!);
                var problem = await rig.Pass();
                Equal("deletes on: a delete on either side is copied", "kept.txt", Names(rig.LocalTree()));
                Equal("on both sides", "kept.txt", Names(rig.DriveTree()));
                Check("the Drive copy goes to Drive's trash, once", rig.Drive.Trashes == 1);
                Check("the PC copy goes through the no-window recycle", rig.Recycled.SequenceEqual(new[] { "gone in drive.txt" }));
                Check("with no problem", problem == null, problem);
            }

            // Fix 6: a drive with no Recycle Bin. The file is kept and said, it is
            // never deleted for good, and it is not sent back up as new.
            using (var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true))
            {
                rig.Write("stays.txt", "y", T0);
                await rig.Pass();
                rig.Drive.Trash(rig.Drive.Find(rig.FolderId, "stays.txt")!);
                int asked = 0;
                rig.Monitor.RecycleLocal = _ =>
                {
                    asked++;
                    return "that drive has no Recycle Bin, so it would have been deleted for good";
                };
                var problem = await rig.Pass();
                Check("no Recycle Bin: the PC file is kept", File.Exists(Path.Combine(rig.Local, "stays.txt")) && asked == 1);
                Check("and the pair says so", problem != null && problem.Contains("kept") && problem.Contains("Recycle Bin"),
                    problem);
                int uploads = rig.Drive.Uploads;
                await rig.Pass();
                Check("and it is not uploaded again as though it were new", rig.Drive.Uploads == uploads &&
                                                                          !rig.DriveTree().ContainsKey("stays.txt"));
            }
        }

        // ---------------- a root that has gone ----------------

        private static async Task RootGoneTests(string sandbox)
        {
            foreach (var how in new[] { "trashed", "deleted for good" })
            {
                using var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true);
                rig.Write("a.txt", "a", T0);
                rig.Write("b/c.txt", "c", T0);
                await rig.Pass();

                if (how == "trashed") rig.Drive.Trash(rig.FolderId);
                else rig.Drive.Remove(rig.FolderId);
                var problem = await rig.Pass();
                Check($"a Drive folder {how} pauses the pair", problem != null && problem.Contains("Google Drive folder is gone"),
                    problem);
                Equal($"and every PC file is still there ({how})", "a.txt,b/c.txt", Names(rig.LocalTree()));
                Check($"nothing was recycled or trashed ({how})", rig.Recycled.Count == 0 && rig.Drive.Trashes == 0);
            }

            using (var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true))
            {
                rig.Write("a.txt", "a", T0);
                await rig.Pass();
                Directory.Delete(rig.Local, true);
                var problem = await rig.Pass();
                Check("a PC folder that is gone pauses the pair", problem != null && problem.Contains("PC folder is gone"), problem);
                Check("and nothing in Drive is touched", rig.Drive.Trashes == 0 && rig.DriveTree().ContainsKey("a.txt"));
                Directory.CreateDirectory(rig.Local);
                rig.Write("a.txt", "a", T0);
                problem = await rig.Pass();
                Check("once it is back the pair carries on by itself", problem == null, problem);
            }
        }

        // ---------------- cancel mid-pass, then resume ----------------

        private static async Task CancelAndResumeTests(string sandbox)
        {
            using (var rig = new Rig(sandbox, SyncMode.DownloadOnly))
            {
                var big = Noise(20 * 1024 * 1024, 7);
                rig.Drive.AddFile("big.bin", big, T0, rig.FolderId);

                // The download is one streamed request now, so it is cancelled
                // part way through the stream: once 4MB of it is on disk.
                using var cancel = new CancellationTokenSource();
                var partial = Path.Combine(rig.Local, "big.bin.partial");
                rig.Drive.SlowMediaMs = 1;
                var watch = Task.Run(async () =>
                {
                    while (!cancel.IsCancellationRequested)
                    {
                        try { if (File.Exists(partial) && new FileInfo(partial).Length >= 4 * 1024 * 1024) cancel.Cancel(); }
                        catch { }
                        await Task.Delay(2);
                    }
                });
                bool cancelled = false;
                try { await rig.Pass(cancel.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                cancel.Cancel();
                await watch;
                rig.Drive.SlowMediaMs = 0;
                long kept = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                Check("a download cancelled part way stops", cancelled);
                Check("and leaves its part on disk", kept >= 4 * 1024 * 1024 && kept < big.Length, $"{kept:N0} bytes");

                rig.Restart();
                rig.Drive.MediaOffsets.Clear();
                var problem = await rig.Pass();
                Check("the next pass carries on from where it stopped",
                    rig.Drive.MediaOffsets.FirstOrDefault() == kept, $"{string.Join(",", rig.Drive.MediaOffsets)} against {kept:N0}");
                Check("and the file arrives whole", File.Exists(Path.Combine(rig.Local, "big.bin")) &&
                                                    File.ReadAllBytes(Path.Combine(rig.Local, "big.bin")).AsSpan().SequenceEqual(big) &&
                                                    !File.Exists(partial));
                Check("with no problem", problem == null, problem);
            }

            // Fix 4: cancelling (which is what closing the app does) used to throw
            // the upload session away, so every upload started again from zero.
            using (var rig = new Rig(sandbox, SyncMode.UploadOnly))
            {
                var big = Noise(12 * 1024 * 1024, 8);
                rig.Write("big.bin", big, T0);

                using var cancel = new CancellationTokenSource();
                rig.Drive.OnPutBytes = (received, _) =>
                {
                    if (received >= 4 * 1024 * 1024) cancel.Cancel();
                    return Task.CompletedTask;
                };
                bool cancelled = false;
                try { await rig.Pass(cancel.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Check("an upload cancelled part way stops", cancelled);

                rig.Drive.OnPutBytes = null;
                rig.Restart();
                rig.Drive.PutOffsets.Clear();
                var problem = await rig.Pass();
                Check("the next pass carries on in the same upload session", rig.Drive.SessionsBegun == 1,
                    $"{rig.Drive.SessionsBegun} sessions");
                Check("from where Google said it stopped", rig.Drive.PutOffsets.FirstOrDefault() >= 4 * 1024 * 1024,
                    string.Join(",", rig.Drive.PutOffsets));
                Check("and the file arrives whole", rig.DriveTree().TryGetValue("big.bin", out var got) && got.AsSpan().SequenceEqual(big));
                Check("once", rig.Drive.Uploads == 1 && rig.Drive.Duplicates(rig.FolderId).Count == 0);
                Check("with no problem", problem == null, problem);
            }

            // A connection that breaks in the middle of a big upload resumes in
            // the same pass, from Google's count.
            using (var rig = new Rig(sandbox, SyncMode.UploadOnly))
            {
                var big = Noise(9 * 1024 * 1024, 9);
                rig.Write("big.bin", big, T0);
                rig.Drive.CutPutsAfterBytes = 3 * 1024 * 1024;
                var problem = await rig.Pass();
                Check("a big upload whose connection broke part way finishes in the same pass",
                    problem == null && rig.DriveTree().TryGetValue("big.bin", out var got) && got.AsSpan().SequenceEqual(big), problem);
                Check("resuming rather than starting again", rig.Drive.SessionsBegun == 1 && rig.Drive.PutOffsets.Count == 2 &&
                                                            rig.Drive.PutOffsets[1] >= 3 * 1024 * 1024,
                    string.Join(",", rig.Drive.PutOffsets));
            }
        }

        // ---------------- rate limits and Google's bad moments ----------------

        private static async Task RateLimitTests(string sandbox)
        {
            var limited = FakeDrive.ErrorBody(403, "rateLimitExceeded", "Rate Limit Exceeded");
            var tooMany = FakeDrive.ErrorBody(429, "rateLimitExceeded", "Too Many Requests");
            const string robot = "<!DOCTYPE html><html><body>502. That's an error. Please try again in 30 seconds.</body></html>";

            using var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true);
            rig.Write("a.txt", "a", T0);
            rig.Write("b.txt", "b", T0);
            rig.Write("gone.txt", "g", T0);
            rig.Put("c.txt", "c", T0);
            await rig.Pass();
            File.Delete(Path.Combine(rig.Local, "gone.txt"));
            rig.Write("a.txt", "a, edited", T0.AddHours(1));
            rig.Write("new.txt", "n", T0);

            // Far more of each than any single request retries for by itself.
            rig.Drive.AddFault(new FakeDrive.Fault { Kind = "alive", Times = 9, Status = 503, Body = "{}" });
            rig.Drive.AddFault(new FakeDrive.Fault { Kind = "list", Times = 14, Status = 403, Body = limited });
            rig.Drive.AddFault(new FakeDrive.Fault
            {
                Kind = "multipart", Times = 15, Status = 429, Body = tooMany, RetryAfter = TimeSpan.FromSeconds(1),
                Subject = n => n == "a.txt",
            });
            rig.Drive.AddFault(new FakeDrive.Fault { Kind = "multipart", Times = 13, Status = 502, Body = robot, Subject = n => n == "new.txt" });
            rig.Drive.AddFault(new FakeDrive.Fault { Kind = "trash", Times = 4, Status = 500, Body = "{}" });

            var problem = await rig.Pass();
            Check("every fault was met and waited out", rig.Drive.FaultsLeft == 0, $"{rig.Drive.FaultsLeft} left");
            Check("rate limits and server errors never fail a file", problem == null, problem);
            Equal("everything arrived", "a.txt,b.txt,c.txt,new.txt", Names(rig.DriveTree()));
            Equal("with the edit", "a, edited", Text(rig.DriveTree(), "a.txt"));
            Check("and the delete was carried out", !rig.DriveTree().ContainsKey("gone.txt"));
            Check("the wait was said once, and the end of it",
                rig.Said.Count(s => s.StartsWith("sync.ratelimited", StringComparison.Ordinal)) >= 1 &&
                rig.Said.Any(s => s.Contains("answering again", StringComparison.Ordinal)));
            Check("no duplicates came of the retries", rig.Drive.Duplicates(rig.FolderId).Count == 0);
            Check("and nothing was said to have failed", !rig.Said.Any(s => s.StartsWith("sync.error", StringComparison.Ordinal)),
                string.Join(" | ", rig.Said));
        }

        // ---------------- the second review of the monitor ----------------

        private static async Task SecondReviewTests(string sandbox)
        {
            // A file made Hidden on the PC, and a folder made Hidden, with deletes
            // on: present, so their Drive copies stay.
            using (var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true))
            {
                rig.Write("a.txt", "a", T0);
                rig.Write("dir/b.txt", "b", T0);
                rig.Write("c.txt", "c", T0);
                await rig.Pass();
                File.SetAttributes(Path.Combine(rig.Local, "a.txt"), FileAttributes.Hidden);
                new DirectoryInfo(Path.Combine(rig.Local, "dir")).Attributes |= FileAttributes.Hidden;
                await rig.Pass();
                await rig.Pass();
                Check("a hidden file or folder on the PC is not taken for deleted",
                    rig.Drive.Trashes == 0 && Names(rig.DriveTree()) == "a.txt,c.txt,dir/b.txt",
                    $"{rig.Drive.Trashes} trashed; Drive holds {Names(rig.DriveTree())}");
                File.SetAttributes(Path.Combine(rig.Local, "a.txt"), FileAttributes.Normal);
                new DirectoryInfo(Path.Combine(rig.Local, "dir")).Attributes &= ~FileAttributes.Hidden;
                int uploads = rig.Drive.Uploads;
                var problem = await rig.Pass();
                Check("and when it is shown again it is still in step, with nothing sent",
                    problem == null && rig.Drive.Uploads == uploads, $"{problem}; {rig.Drive.Uploads - uploads} uploads");
            }

            // A 403 rate limit on "did that upload land?" is Google saying not
            // now, not the network going.
            using (var rig = new Rig(sandbox, SyncMode.UploadOnly))
            {
                var limited = FakeDrive.ErrorBody(403, "rateLimitExceeded", "Rate Limit Exceeded");
                rig.Write("a.txt", "a", T0);
                rig.Drive.AddFault(new FakeDrive.Fault { Kind = "multipart", Times = 1, Status = 503, Body = "{}" });
                rig.Drive.AddFault(new FakeDrive.Fault { Kind = "lookup", Times = 6, Status = 403, Body = limited });
                var problem = await rig.Pass();
                Check("a rate limit while checking an upload is waited out, not taken for offline",
                    problem == null && Names(rig.DriveTree()) == "a.txt" &&
                    !rig.Said.Any(s => s.StartsWith("sync.offline", StringComparison.Ordinal)),
                    $"{problem}; Drive holds {Names(rig.DriveTree())}; said {string.Join(" | ", rig.Said)}");
            }

            // A day's limit is waited out too, for an hour, and said.
            using (var rig = new Rig(sandbox, SyncMode.TwoWay))
            {
                var daily = FakeDrive.ErrorBody(403, "dailyLimitExceeded", "Daily Limit Exceeded");
                rig.Write("a.txt", "a", T0);
                rig.Drive.AddFault(new FakeDrive.Fault { Kind = "list", Times = 2, Status = 403, Body = daily });
                var problem = await rig.Pass();
                Check("a daily limit does not fail the pair: it waits and carries on",
                    problem == null && Names(rig.DriveTree()) == "a.txt" && rig.Drive.FaultsLeft == 0,
                    $"{problem}; Drive holds {Names(rig.DriveTree())}");
                Check("and says it is the daily limit",
                    rig.Said.Any(s => s.Contains("daily limit", StringComparison.Ordinal)), string.Join(" | ", rig.Said));
            }

            // Duplicate cleanup: the older copy holds an edit the PC never had.
            using (var rig = new Rig(sandbox, SyncMode.TwoWay))
            {
                rig.Write("song.txt", "A", T0);
                await rig.Pass();
                var x = rig.Drive.Find(rig.FolderId, "song.txt")!;
                rig.Write("song.txt", "B", T0.AddHours(2));
                rig.Drive.Edit(x, Encoding.UTF8.GetBytes("C, only in Drive"), T0.AddHours(1));
                rig.Drive.AddFile("song.txt", Encoding.UTF8.GetBytes("B"), T0.AddHours(3), rig.FolderId);
                await rig.Pass();
                await rig.Pass();
                Check("an older Drive copy with an edit the PC never had is not trashed as a duplicate",
                    rig.Drive.Get(x) is { Trashed: false }, $"{rig.Drive.Trashes} trashed");
            }

            // A file and a folder of one name in Drive.
            using (var rig = new Rig(sandbox, SyncMode.DownloadOnly))
            {
                rig.Drive.AddFile("x", Encoding.UTF8.GetBytes("a file called x"), T0, rig.FolderId);
                var folder = rig.Drive.AddFolder("X", rig.FolderId);
                rig.Drive.AddFile("y.txt", Encoding.UTF8.GetBytes("y"), T0, folder);
                var first = await rig.Pass();
                int reads = rig.Drive.MediaReads;
                var second = await rig.Pass();
                int said = rig.Said.Count(s => s.StartsWith("sync.error", StringComparison.Ordinal));
                Check("a file beside a Drive folder of its name is left out and said, and the folder still comes down",
                    first != null && first.Contains("x in Google Drive was left out", StringComparison.Ordinal) &&
                    Names(rig.LocalTree()) == "X/y.txt", $"{first}; PC holds {Names(rig.LocalTree())}");
                Check("once, without fetching it again on the next pass",
                    second == first && said == 1 && rig.Drive.MediaReads == reads, $"{said} said; {rig.Drive.MediaReads - reads} reads");
                Check("and no part of it is left behind",
                    !Directory.EnumerateFiles(rig.Local, "*.partial", SearchOption.AllDirectories).Any());
            }

            // Two Drive folders whose names differ only in capitals.
            using (var rig = new Rig(sandbox, SyncMode.DownloadOnly))
            {
                var a = rig.Drive.AddFolder("Docs", rig.FolderId);
                var b = rig.Drive.AddFolder("docs", rig.FolderId);
                rig.Drive.AddFile("one.txt", Encoding.UTF8.GetBytes("1"), T0, a);
                rig.Drive.AddFile("two.txt", Encoding.UTF8.GetBytes("2"), T0, b);
                rig.Drive.AddFile("fine.txt", Encoding.UTF8.GetBytes("f"), T0, rig.FolderId);
                var problem = await rig.Pass();
                Check("two Drive folders differing only in capitals are said, not one silently dropped",
                    problem != null && problem.Contains("Docs/", StringComparison.Ordinal) &&
                    problem.Contains("docs/", StringComparison.Ordinal) && Names(rig.LocalTree()) == "fine.txt",
                    $"{problem}; PC holds {Names(rig.LocalTree())}");
            }

            // An accented name written the Mac way in Drive and the Windows way on the PC.
            using (var rig = new Rig(sandbox, SyncMode.TwoWay))
            {
                rig.Write("café.txt", "same", T0);
                rig.Drive.AddFile("café.txt", Encoding.UTF8.GetBytes("same"), T0, rig.FolderId);
                var problem = await rig.Pass();
                await rig.Pass();
                Check("an accent written two ways is one file, not two on each side",
                    problem == null && rig.LocalTree().Count == 1 && rig.DriveTree().Count == 1 &&
                    rig.Drive.Uploads == 0 && rig.Drive.MediaReads == 0,
                    $"{problem}; PC {rig.LocalTree().Count}, Drive {rig.DriveTree().Count}, " +
                    $"{rig.Drive.Uploads} uploads, {rig.Drive.MediaReads} reads");
            }

            // Changed on both sides, Drive newer, and Google answering 503 in the
            // middle of it: the step is tried again, and the PC's copy, already
            // set aside, is not moved a second time from where it no longer is.
            // (ReadRange retries a 503 itself for twenty seconds before giving
            // up, so the 503 is put in at the step's end, where the wait for it
            // begins.)
            using (var rig = new Rig(sandbox, SyncMode.TwoWay))
            {
                rig.Write("song.txt", "first", T0);
                await rig.Pass();
                rig.Write("song.txt", "pc's edit", T0.AddHours(1));
                rig.Drive.Edit(rig.Drive.Find(rig.FolderId, "song.txt")!, Encoding.UTF8.GetBytes("drive's edit"), T0.AddHours(2));
                bool refused = false;
                rig.Monitor.KillPoint = what =>
                {
                    if (refused || !what.StartsWith("downloaded ", StringComparison.Ordinal)) return;
                    refused = true;
                    throw new System.Net.Http.HttpRequestException("Service Unavailable", null,
                        System.Net.HttpStatusCode.ServiceUnavailable);
                };
                var problem = await rig.Pass();
                rig.Monitor.KillPoint = null;
                var tree = rig.LocalTree();
                Check("a conflict whose download is refused for a while still ends with both copies",
                    problem == null && tree.Count == 2 && Text(tree, "song.txt") == "drive's edit" &&
                    tree.Any(kv => kv.Key.Contains("conflict") && Encoding.UTF8.GetString(kv.Value) == "pc's edit"),
                    $"{problem}; PC holds {Names(tree)}");
            }
        }

        // ---------------- round 3: spellings on the PC, and the long wait ----------------

        private static async Task ThirdRoundTests(string sandbox)
        {
            string nfc = "Café", nfd = "Café";

            // Two PC files whose names differ only in how the accent is written.
            using (var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true))
            {
                rig.Write(nfc + ".txt", "the first one", T0);
                rig.Write("fine.txt", "fine", T0);
                await rig.Pass();
                var before = rig.Counters();

                rig.Write(nfd + ".txt", "the second one, which is longer", T0.AddHours(1));
                rig.Write("Docs " + nfc + "/a.txt", "a", T0);
                rig.Write("Docs " + nfd + "/b.txt", "b", T0);
                var problem = await rig.Pass();
                await rig.Pass();
                var drive = rig.DriveTree();
                Check("two PC files that differ only in how an accent is written are left out, not merged",
                    Text(drive, nfc + ".txt") == "the first one" && rig.Drive.Uploads == before.Item1 &&
                    rig.Drive.Trashes == 0,
                    $"Drive holds {Names(drive)}; {rig.Drive.Uploads - before.Item1} uploads, {rig.Drive.Trashes} trashed");
                Check("and two such folders are not merged into one in Drive",
                    !drive.Keys.Any(k => k.StartsWith("Docs", StringComparison.Ordinal)), Names(drive));
                Check("the pair says which PC names were left out",
                    problem != null && problem.Contains("on the PC were left out", StringComparison.Ordinal) &&
                    problem.Contains(nfd + ".txt", StringComparison.Ordinal), problem);
                Check("once", rig.Said.Count(s => s.Contains("on the PC were left out", StringComparison.Ordinal)) == 1,
                    string.Join(" | ", rig.Said));
            }

            // Drive's folder spelled the other way from the PC's: what comes down
            // goes into the PC's folder, not a second look-alike beside it.
            using (var rig = new Rig(sandbox, SyncMode.TwoWay))
            {
                rig.Write(nfc + "/a.txt", "a", T0);
                var folder = rig.Drive.AddFolder(nfd, rig.FolderId);
                rig.Drive.AddFile("a.txt", Encoding.UTF8.GetBytes("a"), T0, folder);
                rig.Drive.AddFile("b.txt", Encoding.UTF8.GetBytes("b"), T0, folder);
                var problem = await rig.Pass();
                var folders = Directory.GetDirectories(rig.Local).Select(Path.GetFileName).ToList();
                Check("a download into a folder the PC spells differently lands in the PC's folder",
                    problem == null && folders.Count == 1 && folders[0] == nfc &&
                    File.Exists(Path.Combine(rig.Local, nfc, "b.txt")),
                    $"{problem}; PC folders: {string.Join(",", folders.Select(f => string.Join(" ", f!.Select(c => ((int)c).ToString("x")))))}");
            }

            // The hour's wait for Google's daily limit: Pause, Sync now and closing
            // the app each end it.
            var daily = FakeDrive.ErrorBody(403, "dailyLimitExceeded", "Daily Limit Exceeded");

            async Task<(Rig Rig, Task Pass)> Waiting()
            {
                var rig = new Rig(sandbox, SyncMode.TwoWay);
                rig.Monitor.WaitScale = 1;
                rig.Monitor.Reload(new[] { rig.Pair });
                rig.Write("a.txt", "a", T0);
                rig.Drive.AddFault(new FakeDrive.Fault { Kind = "list", Times = 1, Status = 403, Body = daily });
                var pass = rig.Pass();
                for (int i = 0; i < 200 && !(rig.Monitor.StatusOf(rig.Pair.Id).Problem ?? "").Contains("daily limit"); i++)
                    await Task.Delay(25);
                return (rig, pass);
            }

            static async Task<bool> EndsSoon(Task pass)
            {
                var done = await Task.WhenAny(pass, Task.Delay(5000));
                if (done != pass) return false;
                try { await pass; } catch (OperationCanceledException) { }
                return true;
            }

            {
                var (rig, pass) = await Waiting();
                using (rig)
                {
                    var paused = rig.Pair.Copy();
                    paused.Paused = true;
                    rig.Monitor.Reload(new[] { paused });
                    Check("pausing a pair ends its wait for the daily limit", await EndsSoon(pass));
                    Check("and it no longer says it is waiting",
                        !(rig.Monitor.StatusOf(rig.Pair.Id).Problem ?? "").Contains("waiting"),
                        rig.Monitor.StatusOf(rig.Pair.Id).Problem);
                }
            }
            {
                var (rig, pass) = await Waiting();
                using (rig)
                {
                    rig.Monitor.SyncNow();
                    Check("Sync now ends the wait for the daily limit and asks again",
                        await EndsSoon(pass) && Names(rig.DriveTree()) == "a.txt", Names(rig.DriveTree()));
                }
            }
            {
                var (rig, pass) = await Waiting();
                using (rig)
                {
                    rig.Monitor.Dispose();
                    Check("closing the app ends the wait for the daily limit", await EndsSoon(pass));
                }
            }
        }

        // ---------------- round 4: data safety under a big library ----------------

        private static async Task FourthRoundTests(string sandbox)
        {
            // An edit on the PC replaces Drive's copy in place: same id, no
            // trash, no moment with two copies. Small and resumable alike.
            using (var rig = new Rig(sandbox, SyncMode.TwoWay))
            {
                var big = Noise(7 * 1024 * 1024, 41);
                rig.Write("a.txt", "first", T0);
                rig.Write("big.bin", big, T0);
                await rig.Pass();
                var a = rig.Drive.Find(rig.FolderId, "a.txt");
                var b = rig.Drive.Find(rig.FolderId, "big.bin");
                var bigger = Noise(8 * 1024 * 1024, 42);
                rig.Write("a.txt", "second, longer", T0.AddHours(1));
                rig.Write("big.bin", bigger, T0.AddHours(1));
                var problem = await rig.Pass();
                Check("an edit on the PC updates Drive's file in place: the same files, nothing trashed, no second copy",
                    problem == null && rig.Drive.Find(rig.FolderId, "a.txt") == a && rig.Drive.Find(rig.FolderId, "big.bin") == b &&
                    rig.Drive.Updates == 2 && rig.Drive.Trashes == 0 && rig.Drive.Duplicates(rig.FolderId).Count == 0 &&
                    Text(rig.DriveTree(), "a.txt") == "second, longer" && rig.DriveTree()["big.bin"].AsSpan().SequenceEqual(bigger),
                    $"{problem}; {rig.Drive.Updates} updates, {rig.Drive.Trashes} trashed");
                var before = rig.Counters();
                await rig.Pass();
                Check("and the pair is in step afterwards", rig.Counters() == before);
            }

            // The old way's leftover: a new copy went up beside the old one and
            // Drive would not trash the old one. It is tried again next pass.
            using (var rig = new Rig(sandbox, SyncMode.TwoWay))
            {
                rig.Write("a.txt", "first", T0);
                await rig.Pass();
                var old = rig.Drive.Find(rig.FolderId, "a.txt")!;
                rig.Write("a.txt", "second", T0.AddHours(1));
                rig.Drive.AddFault(new FakeDrive.Fault
                {
                    Kind = "multipart", Times = 1, Status = 404, Body = FakeDrive.ErrorBody(404, "notFound", "File not found"),
                    Subject = n => n == "a.txt",
                });
                rig.Drive.AddFault(new FakeDrive.Fault
                {
                    Kind = "trash", Times = 1, Status = 403,
                    Body = FakeDrive.ErrorBody(403, "insufficientFilePermissions", "The user does not have permission"),
                });
                await rig.Pass();
                int twice = rig.Drive.Duplicates(rig.FolderId).Count;
                var problem = await rig.Pass();
                Check("an old copy Drive would not trash is remembered and trashed on the next pass",
                    twice == 1 && rig.Drive.Get(old) is { Trashed: true } && rig.Drive.Duplicates(rig.FolderId).Count == 0 &&
                    Text(rig.DriveTree(), "a.txt") == "second" && Text(rig.LocalTree(), "a.txt") == "second" && problem == null,
                    $"{twice} doubled after the first pass; old trashed {rig.Drive.Get(old)?.Trashed}; " +
                    $"{rig.Drive.Duplicates(rig.FolderId).Count} doubled now; Drive {Text(rig.DriveTree(), "a.txt")}; " +
                    $"PC {Text(rig.LocalTree(), "a.txt")}; {problem}");
            }

            // A PC edit made after the scan, while Drive's copy was coming down.
            using (var rig = new Rig(sandbox, SyncMode.TwoWay))
            {
                rig.Write("a.txt", "first", T0);
                await rig.Pass();
                rig.Drive.Edit(rig.Drive.Find(rig.FolderId, "a.txt")!, Encoding.UTF8.GetBytes("drive's edit"), T0.AddHours(1));
                bool edited = false;
                rig.Drive.OnMedia = (_, _) =>
                {
                    if (!edited) { edited = true; rig.Write("a.txt", "the pc's edit, made during the download", T0.AddHours(2)); }
                    return Task.CompletedTask;
                };
                await rig.Pass();
                rig.Drive.OnMedia = null;
                Check("a PC edit made while Drive's copy was coming down is not overwritten",
                    Text(rig.LocalTree(), "a.txt") == "the pc's edit, made during the download", Names(rig.LocalTree()));
                await rig.Pass();
                await rig.Pass();
                var local = rig.LocalTree();
                Check("and the next passes keep both, on both sides, with no part left behind",
                    local.Values.Any(v => Encoding.UTF8.GetString(v) == "drive's edit") &&
                    Text(local, "a.txt") == "the pc's edit, made during the download" && SameTrees(local, rig.DriveTree()) &&
                    !Directory.EnumerateFiles(rig.Local, "*.partial", SearchOption.AllDirectories).Any(),
                    Names(local) + " | " + Names(rig.DriveTree()));
            }

            // Download only, and Drive wins: the PC's own copy is not lost.
            using (var rig = new Rig(sandbox, SyncMode.DownloadOnly))
            {
                rig.Write("a.txt", "the pc's own", T0);
                rig.Put("a.txt", "drive's, which is different", T0.AddHours(1));
                var problem = await rig.Pass();
                Check("download only over a different PC file: the PC's goes to the Recycle Bin first",
                    problem == null && rig.Recycled.SequenceEqual(new[] { "a.txt" }) &&
                    Text(rig.LocalTree(), "a.txt") == "drive's, which is different", problem);
            }
            using (var rig = new Rig(sandbox, SyncMode.TwoWay))
            {
                rig.Pair.WhenBothChanged = ConflictChoice.DriveWins;
                rig.Write("a.txt", "first", T0);
                await rig.Pass();
                rig.Write("a.txt", "the pc's edit", T0.AddHours(1));
                rig.Drive.Edit(rig.Drive.Find(rig.FolderId, "a.txt")!, Encoding.UTF8.GetBytes("drive's edit"), T0.AddHours(2));
                rig.Monitor.RecycleLocal = _ => "that drive has no Recycle Bin, so it would have been deleted for good";
                var problem = await rig.Pass();
                Check("Drive wins with no Recycle Bin: the PC's edit is kept, and the pair says so",
                    Text(rig.LocalTree(), "a.txt") == "the pc's edit" && problem != null && problem.Contains("kept"), problem);
            }

            // Deletes act on what is there now, not on the scan.
            using (var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true))
            {
                rig.Write("x.txt", "x", T0);
                await rig.Pass();
                var x = rig.Drive.Find(rig.FolderId, "x.txt")!;
                File.Delete(Path.Combine(rig.Local, "x.txt"));
                bool edited = false;
                rig.Drive.BeforeAnswer = (kind, subject) =>
                {
                    if (kind != "alive" || subject != x || edited) return;
                    edited = true;
                    rig.Drive.Edit(x, Encoding.UTF8.GetBytes("edited in drive meanwhile"), T0.AddHours(1));
                };
                await rig.Pass();
                rig.Drive.BeforeAnswer = null;
                Check("a Drive file edited after the scan is not trashed for a delete made on the PC",
                    edited && rig.Drive.Get(x) is { Trashed: false } && rig.Drive.Trashes == 0);
                await rig.Pass();
                Equal("and the edit comes down", "edited in drive meanwhile", Text(rig.LocalTree(), "x.txt"));
            }
            using (var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true))
            {
                rig.Write("y.txt", "y", T0);
                await rig.Pass();
                rig.Drive.Trash(rig.Drive.Find(rig.FolderId, "y.txt")!);
                bool edited = false;
                rig.Drive.BeforeAnswer = (kind, _) =>
                {
                    if (kind != "list" || edited) return;
                    edited = true;
                    rig.Write("y.txt", "edited on the pc meanwhile", T0.AddHours(1));
                };
                await rig.Pass();
                rig.Drive.BeforeAnswer = null;
                Check("a PC file edited after the scan is not recycled for a delete made in Drive",
                    edited && rig.Recycled.Count == 0 && Text(rig.LocalTree(), "y.txt") == "edited on the pc meanwhile");
                await rig.Pass();
                Equal("and the edit goes up", "edited on the pc meanwhile", Text(rig.DriveTree(), "y.txt"));
            }

            // Two Drive folders of exactly one name.
            using (var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true))
            {
                rig.Write("Docs/one.txt", "1", T0);
                rig.Write("fine.txt", "f", T0);
                await rig.Pass();
                var second = rig.Drive.AddFolder("Docs", rig.FolderId);
                rig.Drive.AddFile("two.txt", Encoding.UTF8.GetBytes("2"), T0, second);
                var problem = await rig.Pass();
                Check("a second Drive folder of the same name is said, and nothing in either is taken for deleted",
                    problem != null && problem.Contains("Docs/", StringComparison.Ordinal) && rig.Recycled.Count == 0 &&
                    rig.Drive.Trashes == 0 && Names(rig.LocalTree()) == "Docs/one.txt,fine.txt", $"{problem}; PC {Names(rig.LocalTree())}");
            }

            // One file Google keeps refusing does not hold up the rest for ever.
            using (var rig = new Rig(sandbox, SyncMode.UploadOnly))
            {
                rig.Monitor.PatienceLimit = TimeSpan.FromMinutes(5);
                rig.Write("fine.txt", "f", T0);
                rig.Write("stuck.txt", "s", T0);
                rig.Drive.AddFault(new FakeDrive.Fault
                {
                    Kind = "multipart", Times = 100_000, Status = 503, Body = "{}", Subject = n => n == "stuck.txt",
                });
                var problem = await rig.Pass();
                Check("a file Google keeps refusing is given up on after the limit, and counted as failed",
                    Names(rig.DriveTree()) == "fine.txt" && problem != null && problem.Contains("stuck.txt") &&
                    problem.Contains("given up"), problem);
            }

            // Pause, Edit and Remove stop a running pass at its next file.
            foreach (var how in new[] { "paused", "edited", "removed" })
            {
                using var rig = new Rig(sandbox, SyncMode.UploadOnly);
                rig.Monitor.Reload(new[] { rig.Pair });
                for (int i = 0; i < 5; i++) rig.Write($"f{i}.txt", "x", T0);
                rig.Monitor.KillPoint = what =>
                {
                    if (what != "uploaded f0.txt") return;
                    var changed = rig.Pair.Copy();
                    if (how == "paused") changed.Paused = true;
                    if (how == "edited") changed.Mode = SyncMode.TwoWay;
                    if (how == "removed")
                    {
                        rig.Monitor.SetPairs(new List<DriveSyncPair>());
                        DriveMonitor.ForgetRemovedPairs(Array.Empty<DriveSyncPair>());
                    }
                    else rig.Monitor.Reload(new[] { changed });
                };
                await rig.Pass();
                rig.Monitor.KillPoint = null;
                Check($"a pair {how} during a pass stops at its next file", rig.DriveTree().Count == 1, Names(rig.DriveTree()));
                if (how == "removed")
                    Check("and a removed pair's state file is not written back", !File.Exists(rig.StatePath));
            }

            // A state file that will not read is not a first sync.
            using (var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true))
            {
                rig.Monitor.Reload(new[] { rig.Pair });
                rig.Write("a.txt", "a", T0);
                rig.Write("b.txt", "b", T0);
                await rig.Pass();
                File.Delete(Path.Combine(rig.Local, "b.txt"));
                File.WriteAllBytes(rig.StatePath, new byte[] { 1, 2, 3, 4, 5 });
                var problem = await rig.Pass();
                Check("unreadable sync records pause the pair and say so",
                    problem != null && problem.Contains("could not be read") && rig.Monitor.Pairs.Single().Paused &&
                    Directory.GetFiles(DriveMonitor.StateDirectory, rig.Pair.Id + ".json.damaged-*").Length == 1, problem);
                Check("and nothing was done meanwhile", rig.Drive.Trashes == 0 && Names(rig.DriveTree()) == "a.txt,b.txt");
                rig.Monitor.Reload(new[] { rig.Pair });
                problem = await rig.Pass();
                Check("unpaused, it compares afresh: nothing deleted, what is missing comes back",
                    problem == null && rig.Drive.Trashes == 0 && Names(rig.LocalTree()) == "a.txt,b.txt", problem);
            }

            // A file still being written waits.
            using (var rig = new Rig(sandbox, SyncMode.UploadOnly))
            {
                rig.Write("new.txt", "being written", DateTime.UtcNow);
                var problem = await rig.Pass();
                Check("a file modified a moment ago waits for a later pass", problem == null && rig.DriveTree().Count == 0, problem);
                File.SetLastWriteTimeUtc(Path.Combine(rig.Local, "new.txt"), T0);
                await rig.Pass();
                Equal("and goes up once it has settled", "new.txt", Names(rig.DriveTree()));

                rig.Write("open.txt", "open", T0);
                using (new FileStream(Path.Combine(rig.Local, "open.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    problem = await rig.Pass();
                    Check("a file open for writing in another program waits too",
                        problem == null && !rig.DriveTree().ContainsKey("open.txt"), problem);
                }
                await rig.Pass();
                Check("and goes up once it is closed", rig.DriveTree().ContainsKey("open.txt"));
            }

            // The monitor's own writes, and a folder that will not list for a moment.
            using (var rig = new Rig(sandbox, SyncMode.DownloadOnly))
            {
                for (int i = 0; i < 6; i++) rig.Put($"dir{i}/f.txt", "f" + i, T0);
                var dir3 = rig.Drive.Find(rig.FolderId, "dir3")!;
                rig.Drive.AddFault(new FakeDrive.Fault { Kind = "list", Times = 2, Status = 503, Body = "{}", Subject = s => s == dir3 });
                var problem = await rig.Pass();
                Check("one Drive folder that will not list for a moment is asked again by itself",
                    problem == null && rig.LocalTree().Count == 6 && rig.Drive.FaultsLeft == 0, problem);
                Check("and a file the monitor has just brought down is its own doing, so its watcher event starts no pass",
                    rig.Monitor.IsMine(Path.Combine(rig.Local, "dir0", "f.txt")));
            }

            var engine = Tests_Source("DriveMonitor.cs") ?? "";
            Check("hashing sets no thread priority (it stayed with the pool thread after the await)",
                engine.Length > 0 && !engine.Contains("ThreadPriority", StringComparison.Ordinal));
            Check("closing the monitor lets go of the drive's connection event",
                engine.Contains("ConnectionChanged -= _onConnection", StringComparison.Ordinal));
            var form = Tests_Source("DriveMonitorForm.cs") ?? "";
            Check("the configurator never reads a pair's state file on the UI thread",
                form.Contains("StatusIfLoaded(", StringComparison.Ordinal) && !form.Contains(".StatusOf(", StringComparison.Ordinal));
            Check("adding a folder saves and closes at once: the window reads neither folder (the first pass does, on its own thread)",
                !form.Contains("PreviewAsync", StringComparison.Ordinal) && !form.Contains("ScanLocal", StringComparison.Ordinal));
        }

        // ---------------- fix 5: a file changed while it went up ----------------

        private static async Task ChangedDuringUploadTests(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.TwoWay);
            rig.Write("notes.txt", "the first words", T0);
            bool changed = false;
            rig.Drive.AfterMultipartBody = name =>
            {
                if (name != "notes.txt" || changed) return;
                changed = true;
                // Somebody saves again while the bytes are on their way.
                rig.Write("notes.txt", "the first words, and a good many more after them", T0.AddHours(1));
            };
            await rig.Pass();
            rig.Drive.AfterMultipartBody = null;
            await rig.Pass();

            Check("a file saved again while it was uploading is not taken for a conflict",
                !rig.LocalTree().Keys.Concat(rig.DriveTree().Keys).Any(k => k.Contains("conflict")),
                Names(rig.LocalTree()) + " | " + Names(rig.DriveTree()));
            Equal("the newer save simply goes up next", "the first words, and a good many more after them",
                Text(rig.DriveTree(), "notes.txt"));
            var before = rig.Counters();
            await rig.Pass();
            Check("and then the pair is in step", rig.Counters() == before);
        }

        // ---------------- 500 small files ----------------

        private static async Task ManyFilesTests(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.TwoWay);
            for (int i = 0; i < 500; i++) rig.Write($"many/{i / 100}/file {i:D3}.txt", $"file {i}", T0.AddSeconds(i));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var problem = await rig.Pass();
            Check("500 small files all go up", rig.Drive.Uploads == 500 && rig.DriveTree().Count == 500,
                $"{rig.Drive.Uploads} uploads in {clock.ElapsedMilliseconds} ms");
            Check("and the five folders are made once each", rig.Drive.Creates == 6, $"{rig.Drive.Creates} folders");
            Check("with no problem", problem == null, problem);
            Check("a pass that big is announced", rig.Said.Any(s => s.StartsWith("sync.start", StringComparison.Ordinal)));

            using var other = new Rig(sandbox, SyncMode.DownloadOnly);
            var source = rig.DriveTree();
            foreach (var (path, bytes) in source)
            {
                var parent = other.FolderId;
                var parts = path.Split('/');
                foreach (var folder in parts[..^1])
                    parent = other.Drive.Find(parent, folder) ?? other.Drive.AddFolder(folder, parent);
                other.Drive.AddFile(parts[^1], bytes, T0, parent);
            }
            problem = await other.Pass();
            Check("and 500 come down", other.LocalTree().Count == 500 && SameTrees(other.LocalTree(), source), problem);
            var before = other.Counters();
            await other.Pass();
            Check("after which nothing moves", other.Counters() == before);
        }

        // ---------------- the space margin ----------------

        private static async Task SpaceTests(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.DownloadOnly);
            const long mb = 1024 * 1024;
            long volume = 100L * 1024 * mb;
            long margin = SyncSpace.Margin(volume);
            for (int i = 0; i < 3; i++) rig.Drive.AddFile($"part{i}.bin", Noise(3 * (int)mb, 20 + i), T0, rig.FolderId);

            long Written() => rig.LocalTree().Values.Sum(b => (long)b.Length);
            long free = margin + 5 * mb;
            rig.Monitor.DiskOfRoot = _ => (free - Written(), volume, "T:");

            var problem = await rig.Pass();
            Equal("only what fits above the margin comes down", "1", rig.LocalTree().Count.ToString());
            Check("and the pair says it is waiting for room", problem != null && problem.StartsWith("paused: T: is out of space"),
                problem);
            Check("said once", rig.Said.Count(s => s.StartsWith("sync.space", StringComparison.Ordinal)) == 1);

            await rig.Pass();
            Check("asking again while still full says nothing new",
                rig.Said.Count(s => s.StartsWith("sync.space", StringComparison.Ordinal)) == 1);

            free = margin + 50 * mb;
            problem = await rig.Pass();
            Check("once there is room it carries on by itself", problem == null && rig.LocalTree().Count == 3, problem);
        }

        // ---------------- names Windows cannot store ----------------

        private static async Task OddNameTests(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true);
            var odd = new[] { "a:b.txt", "x\\y.txt", "AC/DC.flac", "trail.", "trail ", "..", "CON", "CON.txt", "nul.flac" };
            foreach (var name in odd) rig.Drive.AddFile(name, Encoding.UTF8.GetBytes("odd " + name), T0, rig.FolderId);
            var bad = rig.Drive.AddFolder("bad:dir", rig.FolderId);
            rig.Drive.AddFile("inner.txt", Encoding.UTF8.GetBytes("inner"), T0, bad);
            rig.Drive.AddFolder("..", rig.FolderId);
            rig.Drive.AddFile("dup.txt", Encoding.UTF8.GetBytes("older dup"), T0, rig.FolderId);
            rig.Drive.AddFile("dup.txt", Encoding.UTF8.GetBytes("newer dup"), T0.AddHours(1), rig.FolderId);
            rig.Drive.AddFile("Notes", Array.Empty<byte>(), T0, rig.FolderId, "application/vnd.google-apps.document");
            rig.Put("ok.txt", "fine", T0);
            rig.Write("mine.txt", "from the pc", T0);

            var outside = Directory.GetFileSystemEntries(Path.GetDirectoryName(rig.Local)!).Length;
            var problem = await rig.Pass();
            Equal("only names Windows can hold come down, and the PC's goes up",
                "dup.txt,mine.txt,ok.txt", Names(rig.LocalTree()));
            Equal("of two Drive files with one name, the newer is the one", "newer dup", Text(rig.LocalTree(), "dup.txt"));
            Check("nothing was written beside the PC folder",
                Directory.GetFileSystemEntries(Path.GetDirectoryName(rig.Local)!).Length == outside);
            Check("the pair says which names were left out, and how many",
                problem != null && problem.StartsWith("11 items in Google Drive were left out", StringComparison.Ordinal) &&
                problem.Contains("Windows cannot use") && problem.Contains("bad:dir/"), problem);
            Check("and still counts as synced", rig.Monitor.StatusOf(rig.Pair.Id).LastSyncedUtc != null);
            Check("a Google document is not one of them", problem != null && !problem.Contains("Notes"), problem);

            await rig.Pass();
            Check("with deletes on, nothing in Drive is ever trashed for it", rig.Drive.Trashes == 0);
            foreach (var name in odd.Concat(new[] { "bad:dir/inner.txt", "dup.txt" }))
                Check($"\"{name}\" is still in Drive", rig.DriveTree().ContainsKey(name));
            Check("and none of them failed a pass", !rig.Said.Any(s => s.Contains("could not sync") && !s.Contains("left out")),
                string.Join(" | ", rig.Said));
        }

        // ---------------- dropped connections and slow bodies ----------------

        private static async Task FlakyConnectionTests(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.TwoWay);
            var media = Noise(3 * 1024 * 1024, 31);
            rig.Drive.AddFile("down.bin", media, T0, rig.FolderId);
            rig.Write("up.txt", "up", T0);
            rig.Drive.DropMediaMidBody = 2;
            rig.Drive.DropMultipartMidBody = 1;
            rig.Drive.SlowMediaMs = 5;
            rig.Drive.AddFault(new FakeDrive.Fault { Kind = "list", Times = 2, Status = 0 });

            var problem = await rig.Pass();
            Check("dropped connections, a body cut off and a slow body still sync", problem == null, problem);
            Check("the download is whole", rig.LocalTree().TryGetValue("down.bin", out var got) && got.AsSpan().SequenceEqual(media));
            Check("the upload landed once, not twice", rig.Drive.Duplicates(rig.FolderId).Count == 0 &&
                                                      Text(rig.DriveTree(), "up.txt") == "up");
        }

        // ---------------- a crash mid-pass ----------------

        /// <summary>
        /// A crash at each point where Drive or the disk has changed and the
        /// state file does not know yet. The state file is put back as it was
        /// on disk at that moment, the app "starts again", and two passes later
        /// both sides must agree, nothing may be lost or doubled, and the pair
        /// must be in step.
        /// </summary>
        private static async Task CrashTests(string sandbox)
        {
            var points = new[]
            {
                // An edit now replaces Drive's copy in place, so there is no
                // moment with the new copy up and the old one not yet trashed.
                "uploaded new.txt", "uploaded edited.txt",
                "downloaded fromdrive.txt", "trashed gone on pc.txt", "recycled gone in drive.txt",
                "set aside in Drive both.txt", "uploaded both.txt",
            };

            foreach (var point in points)
            {
                using var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true);
                foreach (var name in new[] { "keep.txt", "edited.txt", "both.txt", "gone on pc.txt", "gone in drive.txt" })
                    rig.Write(name, "was " + name, T0);
                await rig.Pass();

                rig.Write("new.txt", "new on the pc", T0.AddHours(1));
                rig.Write("edited.txt", "edited on the pc", T0.AddHours(1));
                rig.Write("both.txt", "both: the pc's", T0.AddHours(3));
                File.Delete(Path.Combine(rig.Local, "gone on pc.txt"));
                rig.Put("fromdrive.txt", "new in drive", T0.AddHours(1));
                rig.Drive.Trash(rig.Drive.Find(rig.FolderId, "gone in drive.txt")!);
                rig.Drive.Edit(rig.Drive.Find(rig.FolderId, "both.txt")!, Encoding.UTF8.GetBytes("both: drive's"), T0.AddHours(2));

                byte[]? snapshot = null;
                rig.Monitor.KillPoint = what =>
                {
                    if (what != point || snapshot != null) return;
                    snapshot = File.Exists(rig.StatePath) ? File.ReadAllBytes(rig.StatePath) : Array.Empty<byte>();
                    throw new OperationCanceledException("a crash, here");
                };
                try { await rig.Pass(); }
                catch (OperationCanceledException) { }
                if (snapshot == null)
                {
                    Check($"crash at \"{point}\" was reached", false);
                    continue;
                }

                // What a crash leaves: the state file as it was on disk.
                if (snapshot.Length == 0) File.Delete(rig.StatePath);
                else File.WriteAllBytes(rig.StatePath, snapshot);
                rig.Restart();

                var problem = await rig.Pass();
                await rig.Pass();
                var before = rig.Counters();
                await rig.Pass();

                var local = rig.LocalTree();
                var drive = rig.DriveTree();
                var conflict = local.Keys.FirstOrDefault(k => k.StartsWith("both (conflict ", StringComparison.Ordinal));
                string detail = Names(local) + " | " + Names(drive) + " | " + string.Join(",", rig.Drive.Duplicates(rig.FolderId)) +
                                " | " + problem;
                Check($"crash at \"{point}\": both sides agree afterwards", SameTrees(local, drive), detail);
                Check($"crash at \"{point}\": nothing lost, nothing that was deleted came back",
                    Names(local) == string.Join(",", new[] { "both.txt", conflict ?? "(no conflict copy)", "edited.txt", "fromdrive.txt", "keep.txt", "new.txt" }
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) &&
                    Text(local, "edited.txt") == "edited on the pc" && Text(local, "both.txt") == "both: the pc's" &&
                    conflict != null && Text(local, conflict) == "both: drive's", detail);
                Check($"crash at \"{point}\": no file is in Drive twice", rig.Drive.Duplicates(rig.FolderId).Count == 0, detail);
                Check($"crash at \"{point}\": and the pair is in step", rig.Counters() == before && problem == null, detail);
            }
        }

        // ---------------- fix 6: never a dialog ----------------

        private static void NoDialogTests()
        {
            var engine = Tests_Source("DriveMonitor.cs");
            Check("the monitor never deletes through the delete that can ask a question",
                engine != null && !engine.Contains("ShellDelete.Recycle(", StringComparison.Ordinal) &&
                engine.Contains("ShellDelete.RecycleWithoutUi", StringComparison.Ordinal));

            var shell = Tests_Source("ShellDelete.cs") ?? "";
            int start = shell.IndexOf("public static string? RecycleWithoutUi", StringComparison.Ordinal);
            var quiet = start < 0 ? "" : shell[start..];
            Check("and that one asks for no warning and has no window to own one",
                start > 0 && !quiet.Contains("FOF_WANTNUKEWARNING", StringComparison.Ordinal) &&
                quiet.Contains("FOF_NOERRORUI", StringComparison.Ordinal) &&
                quiet.Contains("TSF_DELETE_RECYCLE_IF_POSSIBLE", StringComparison.Ordinal));

            var unmounted = ShellDelete.RecycleWithoutUi(@"Q:\explorer-native-no-such-drive\file.txt");
            Check("a drive with no Recycle Bin is refused before anything is touched",
                unmounted != null && unmounted.Contains("no Recycle Bin"), unmounted);
            var share = ShellDelete.RecycleWithoutUi(@"\\explorer-native-no-such-server\share\file.txt");
            Check("and so is a network share", share != null && share.Contains("no Recycle Bin"), share);
        }

        private static string? Tests_Source(string name)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "src", name);
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
            }
            return null;
        }

        // ---------------- fix 7 ----------------

        private static async Task CheckClientCancelTests()
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            string outcome;
            try
            {
                var said = await GoogleAuth.CheckClientAsync("x.apps.googleusercontent.com", "GOCSPX-x", cancel.Token);
                outcome = "answered: " + said;
            }
            catch (OperationCanceledException)
            {
                outcome = "cancelled";
            }
            Equal("checking a client, called off, is a cancellation and not \"could not be reached\"", "cancelled", outcome);
        }
    }
}
