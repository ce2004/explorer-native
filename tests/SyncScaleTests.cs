using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The folder sync at scale: moves and renames followed rather than copied,
    /// the changes feed instead of listing everything, replacements in parallel,
    /// and the preview before a first sync. Every test counts what reached the
    /// fake Drive, so a duplicate shows up as a number, not as a hope.
    /// </summary>
    internal static class SyncScaleTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private const double Fast = 0.0005;
        private static readonly DateTime T0 = new(2025, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        public static async Task RunAll(Action<string, bool, string?> check)
        {
            _check = check;
            Console.WriteLine("Drive sync at scale: moves, changes feed, parallel replacements, preview:");

            var previous = Settings.OverrideAppDataDir;
            var previousFull = DriveMonitor.FullListEvery;
            var sandbox = Path.Combine(Path.GetTempPath(), "en-syncscale-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            Settings.OverrideAppDataDir = Path.Combine(sandbox, "appdata");
            try
            {
                PureMoveRules();

                await FolderRenamedOnPc(sandbox, deletes: false);
                await FolderRenamedOnPc(sandbox, deletes: true);
                await FileRenamedAndMovedOnPc(sandbox);
                await LookAlikesAreNotMoved(sandbox);
                await FolderRenameWithSomethingNewOnDrive(sandbox);
                await RenamedOnDrive(sandbox);
                await CrashDuringMoves(sandbox);
                await MovesInEveryMode(sandbox);

                await NothingChangedCostsTheFeedOnly(sandbox);
                await OneEditOnEachSide(sandbox);
                await FeedTroubleListsEverything(sandbox);
                await FeedMissesAFile(sandbox);
                await DriveFolderRenamedMeansAFullListing(sandbox);

                await StoppedMidUploadThenStraightBack(sandbox);
                await ReplacementsInParallel(sandbox);
                await PreviewCounts(sandbox);
                StatusWords();

                await MigratesToBatches(sandbox);
                if (Environment.GetEnvironmentVariable("EXPLORERNATIVE_SYNCSCALE_HEAVY") == "1") await MemoryAtScale(sandbox);
            }
            finally
            {
                DriveMonitor.FullListEvery = previousFull;
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(sandbox, true); } catch { }
            }
            Console.WriteLine();
        }

        /// <summary>Only the memory measurement: EXPLORERNATIVE_SELFTEST_ONLY=syncmemory.</summary>
        public static async Task RunMemory(Action<string, bool, string?> check)
        {
            _check = check;
            var previous = Settings.OverrideAppDataDir;
            var sandbox = Path.Combine(Path.GetTempPath(), "en-syncmem-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            Settings.OverrideAppDataDir = Path.Combine(sandbox, "appdata");
            try { await MemoryAtScale(sandbox); }
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
            public DriveClient Client;
            public DriveMonitor Monitor;
            private readonly Dictionary<string, string> _folders = new(StringComparer.Ordinal);

            public Rig(string sandbox, SyncMode mode = SyncMode.TwoWay, bool deletes = false)
            {
                var top = Path.Combine(sandbox, Guid.NewGuid().ToString("N")[..8]);
                Local = Path.Combine(top, "D");
                Directory.CreateDirectory(Local);
                FolderId = Drive.AddFolder("D");
                _folders[""] = FolderId;
                Pair = new DriveSyncPair
                {
                    Name = "D", LocalFolder = Local, DriveFolderId = FolderId, DriveFolderPath = "My Drive/D",
                    Mode = mode, CopyDeletes = deletes, CheckMinutes = 0,
                };
                Client = new DriveClient(Drive, Fast);
                Monitor = NewMonitor();
            }

            public DriveMonitor NewMonitor() =>
                new(new GoogleDrive(), (_, _) => { }, _ => { })
                {
                    WaitScale = Fast,
                    DiskOfRoot = _ => (1L << 42, 1L << 43, "T:"),
                    RecycleLocal = path => { File.Delete(path); return null; },
                    PatienceLimit = TimeSpan.FromSeconds(5),
                };

            public void Restart()
            {
                Monitor.Dispose();
                Client = new DriveClient(Drive, Fast);
                Monitor = NewMonitor();
            }

            public async Task<DriveMonitor.PairStatus> Pass(CancellationToken token = default)
            {
                await Monitor.SyncOneAsync(Client, Pair, token);
                return Monitor.StatusOf(Pair.Id);
            }

            public string LocalPath(string relative) => Path.Combine(Local, relative.Replace('/', '\\'));

            public void WriteLocal(string relative, byte[] data, DateTime modifiedUtc)
            {
                var path = LocalPath(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, data);
                File.SetLastWriteTimeUtc(path, modifiedUtc);
            }

            public string DriveFolder(string relative)
            {
                if (relative.Length == 0) return FolderId;
                if (_folders.TryGetValue(relative, out var id)) return id;
                int slash = relative.LastIndexOf('/');
                var parent = DriveFolder(slash < 0 ? "" : relative[..slash]);
                return _folders[relative] = Drive.AddFolder(relative[(slash + 1)..], parent);
            }

            public string PutDrive(string relative, byte[] data, DateTime modifiedUtc)
            {
                int slash = relative.LastIndexOf('/');
                return Drive.AddFile(relative[(slash + 1)..], data, modifiedUtc, DriveFolder(slash < 0 ? "" : relative[..slash]));
            }

            public List<string> LocalFiles() =>
                Directory.EnumerateFiles(Local, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(Local, f).Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal).ToList();

            private List<FakeDrive.Item> Items() =>
                ((System.Collections.IDictionary)typeof(FakeDrive)
                    .GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Drive)!)
                .Values.Cast<FakeDrive.Item>().ToList();

            /// <summary>Every live file under the pair's Drive folder, and names held twice in one folder.</summary>
            public (List<string> Paths, int Twice) DriveFiles()
            {
                var items = Items();
                var children = items.Where(i => !i.Trashed).ToLookup(i => i.Parent);
                var paths = new List<string>();
                int twice = 0;
                var queue = new Queue<(string Id, string Prefix)>();
                queue.Enqueue((FolderId, ""));
                while (queue.Count > 0)
                {
                    var (id, prefix) = queue.Dequeue();
                    var here = children[id].ToList();
                    twice += here.GroupBy(c => c.Name, SyncNameComparer.Instance).Count(g => g.Count() > 1);
                    foreach (var c in here)
                    {
                        if (c.IsFolder) queue.Enqueue((c.Id, prefix + c.Name + "/"));
                        else if (!c.IsDocument) paths.Add(prefix + c.Name);
                    }
                }
                paths.Sort(StringComparer.Ordinal);
                return (paths, twice);
            }

            /// <summary>Whether a folder of that name is directly in the pair's folder on Drive, live.</summary>
            public bool DriveHasFolder(string name) =>
                Items().Any(i => !i.Trashed && i.IsFolder && i.Parent == FolderId && i.Name == name);

            public int Kind(string kind) { lock (Drive.Kinds) return Drive.Kinds.Count(k => k == kind); }

            public void ClearKinds() { lock (Drive.Kinds) Drive.Kinds.Clear(); }

            public string Counts() =>
                $"uploads {Drive.Uploads}, in place {Drive.Updates}, creates {Drive.Creates}, trashes {Drive.Trashes}, " +
                $"renames {Drive.Renames}, moves {Drive.Moves}, media reads {Drive.MediaReads}, listings {Kind("list")}";

            public void Dispose()
            {
                Monitor.Dispose();
                try { Directory.Delete(Path.GetDirectoryName(Local)!, true); } catch { }
            }
        }

        private static byte[] Bytes(int i, int length = 64)
        {
            var b = new byte[length];
            new Random(i * 31 + 7).NextBytes(b);
            return b;
        }

        /// <summary>A pair over <paramref name="count"/> files in folders of 50, synced and settled.</summary>
        private static async Task<Rig> Settled(string sandbox, int count, string top = "Music", SyncMode mode = SyncMode.TwoWay,
            bool deletes = false)
        {
            var rig = new Rig(sandbox, mode, deletes);
            for (int i = 0; i < count; i++)
            {
                var rel = $"{top}/album {i / 50:D3}/track {i:D5}.flac";
                var data = Bytes(i);
                rig.WriteLocal(rel, data, T0.AddSeconds(i));
                rig.PutDrive(rel, data, T0.AddSeconds(i));
            }
            await rig.Pass();
            await rig.Pass();
            return rig;
        }

        // ---------------- moves ----------------

        private static void PureMoveRules()
        {
            Check("a renamed top folder is that folder's rename",
                SyncMoves.FolderOf(new SyncMove("a/b/c.txt", "x/b/c.txt", true, Was())) == ("a", "x"));
            Check("a folder moved deeper is that folder's move",
                SyncMoves.FolderOf(new SyncMove("a/c.txt", "x/y/c.txt", true, Was())) == ("a", "x/y"));
            Check("a file renamed is not a folder move",
                SyncMoves.FolderOf(new SyncMove("a/c.txt", "a/d.txt", true, Was())) == null);
            Check("a file moved out of the top is not a folder move",
                SyncMoves.FolderOf(new SyncMove("c.txt", "x/c.txt", true, Was())) == null);

            var rules = new SyncRules(SyncMode.TwoWay, false);
            var last = new Dictionary<string, SyncBase>(SyncNameComparer.Instance)
            {
                ["old/a.txt"] = new SyncBase(10, T0, 10, T0, "id-a", "m-a"),
                ["old/b.txt"] = new SyncBase(10, T0, 10, T0, "id-b", "m-b"),
            };
            var remote = new Dictionary<string, SyncFile>(SyncNameComparer.Instance)
            {
                ["old/a.txt"] = new SyncFile(10, T0, "id-a", "m-a"),
                ["old/b.txt"] = new SyncFile(10, T0, "id-b", "m-b"),
            };
            var local = new Dictionary<string, SyncFile>(SyncNameComparer.Instance)
            {
                ["new/a.txt"] = new SyncFile(10, T0),
                ["new/b.txt"] = new SyncFile(10, T0),
            };
            var plan = SyncPlanner.Plan(rules, local, remote, last);
            var moves = SyncMoves.Find(rules, plan, local, remote, last);
            Check("two alike files that moved together are paired by name",
                moves.Count == 2 && moves.All(m => SyncMoves.Leaf(m.From) == SyncMoves.Leaf(m.To)),
                string.Join("; ", moves.Select(m => m.From + ">" + m.To)));

            local = new Dictionary<string, SyncFile>(SyncNameComparer.Instance)
            {
                ["new/c.txt"] = new SyncFile(10, T0),
                ["new/d.txt"] = new SyncFile(10, T0),
            };
            plan = SyncPlanner.Plan(rules, local, remote, last);
            Check("two alike files that cannot be told apart are not guessed at",
                SyncMoves.Find(rules, plan, local, remote, last).Count == 0);

            var down = new SyncRules(SyncMode.DownloadOnly, false);
            local = new Dictionary<string, SyncFile>(SyncNameComparer.Instance) { ["new/a.txt"] = new SyncFile(10, T0) };
            plan = SyncPlanner.Plan(down, local, remote, last);
            Check("download only never moves anything on Drive",
                SyncMoves.Find(down, plan, local, remote, last).All(m => !m.OnDrive));
        }

        private static SyncBase Was() => new(1, T0, 1, T0, "x");

        private static async Task FolderRenamedOnPc(string sandbox, bool deletes)
        {
            var label = deletes ? "deletes copied" : "deletes not copied";
            using var rig = await Settled(sandbox, 1000, deletes: deletes);
            var before = rig.DriveFiles();
            int uploads = rig.Drive.Uploads, renames = rig.Drive.Renames, creates = rig.Drive.Creates, trashes = rig.Drive.Trashes;

            Directory.Move(rig.LocalPath("Music"), rig.LocalPath("Songs"));
            var clock = Stopwatch.StartNew();
            var s = await rig.Pass();
            var after = rig.DriveFiles();

            Check($"folder of 1,000 renamed on the PC ({label}): nothing uploaded",
                rig.Drive.Uploads == uploads, rig.Counts());
            Check($"folder renamed on the PC ({label}): one rename of Drive's folder, no new folders, nothing trashed",
                rig.Drive.Renames == renames + 1 && rig.Drive.Creates == creates && rig.Drive.Trashes == trashes, rig.Counts());
            Check($"folder renamed on the PC ({label}): Drive holds the same 1,000 files once, under the new name",
                after.Paths.Count == 1000 && after.Twice == 0 && after.Paths.All(p => p.StartsWith("Songs/", StringComparison.Ordinal)) &&
                !rig.DriveHasFolder("Music"),
                $"{after.Paths.Count} files, {after.Twice} twice, first {after.Paths.FirstOrDefault()}");
            Check($"folder renamed on the PC ({label}): the PC still has its 1,000, nothing came back down",
                rig.LocalFiles().Count == 1000 && rig.LocalFiles().All(p => p.StartsWith("Songs/", StringComparison.Ordinal)) &&
                rig.Drive.MediaReads == 0, rig.Counts());
            Check($"folder renamed on the PC ({label}): the status says 1,000 moved", s.Moved == 1000, s.Moved.ToString());
            Console.WriteLine($"    (folder rename of 1,000 files: {clock.ElapsedMilliseconds} ms, {rig.Counts()})");

            int writes = rig.Drive.Uploads + rig.Drive.Renames + rig.Drive.Creates + rig.Drive.Trashes + rig.Drive.Moves;
            rig.ClearKinds();
            var again = await rig.Pass();
            Check($"folder renamed on the PC ({label}): the next pass goes by the changes feed, not a full listing",
                rig.Kind("list") <= 3, $"{rig.Kind("list")} folders listed");
            Check($"folder renamed on the PC ({label}): the next pass does nothing",
                rig.Drive.Uploads + rig.Drive.Renames + rig.Drive.Creates + rig.Drive.Trashes + rig.Drive.Moves == writes &&
                again.Matched == 1000, $"{rig.Counts()}, matched {again.Matched}");
        }

        private static async Task FileRenamedAndMovedOnPc(string sandbox)
        {
            using var rig = await Settled(sandbox, 100);
            int uploads = rig.Drive.Uploads;
            File.Move(rig.LocalPath("Music/album 000/track 00001.flac"), rig.LocalPath("Music/album 000/renamed.flac"));
            File.Move(rig.LocalPath("Music/album 001/track 00051.flac"), rig.LocalPath("Music/album 000/moved here.flac"));
            await rig.Pass();
            var after = rig.DriveFiles();
            Check("a file renamed and a file moved on the PC: nothing uploaded, both followed on Drive",
                rig.Drive.Uploads == uploads && rig.Drive.Moves >= 1 &&
                after.Paths.Contains("Music/album 000/renamed.flac") && after.Paths.Contains("Music/album 000/moved here.flac") &&
                !after.Paths.Contains("Music/album 000/track 00001.flac") && !after.Paths.Contains("Music/album 001/track 00051.flac") &&
                after.Paths.Count == 100 && after.Twice == 0, rig.Counts());
        }

        private static async Task LookAlikesAreNotMoved(string sandbox)
        {
            // The same size and the same time, but not the same file: one is
            // deleted and another written, by chance alike. The MD5 tells them
            // apart, so this is a delete and a new file, never a move.
            using var rig = await Settled(sandbox, 10);
            var old = rig.LocalPath("Music/album 000/track 00003.flac");
            File.Delete(old);
            var other = Bytes(999);
            rig.WriteLocal("Music/album 000/a new song.flac", other, T0.AddSeconds(3));
            int renames = rig.Drive.Renames, moves = rig.Drive.Moves;
            await rig.Pass();
            var after = rig.DriveFiles();
            Check("a look-alike (same size and time, different bytes) is not taken for a move",
                rig.Drive.Renames == renames && rig.Drive.Moves == moves && after.Paths.Contains("Music/album 000/a new song.flac") &&
                after.Twice == 0, rig.Counts());
            var onDrive = rig.Drive.Find(rig.FolderId, "Music/album 000/a new song.flac");
            Check("and what went up is the new file's bytes",
                onDrive != null && rig.Drive.Get(onDrive)!.Data.SequenceEqual(other));
        }

        private static async Task FolderRenameWithSomethingNewOnDrive(string sandbox)
        {
            // Renamed on the PC while a file was added to the old folder on the
            // web: renaming Drive's folder would carry that file somewhere the
            // person never put it, so it goes file by file and the new file stays.
            using var rig = await Settled(sandbox, 100);
            rig.PutDrive("Music/album 000/added on the web.flac", Bytes(5000), T0.AddDays(1));
            int uploads = rig.Drive.Uploads;
            Directory.Move(rig.LocalPath("Music"), rig.LocalPath("Songs"));
            await rig.Pass();
            var after = rig.DriveFiles();
            Check("renamed on the PC with a new file in the old folder on Drive: nothing uploaded",
                rig.Drive.Uploads == uploads, rig.Counts());
            Check("and the 100 moved one by one, the web's file left where it was (and brought down)",
                after.Paths.Count(p => p.StartsWith("Songs/", StringComparison.Ordinal)) == 100 &&
                after.Paths.Contains("Music/album 000/added on the web.flac") && after.Twice == 0,
                string.Join(", ", after.Paths.Where(p => !p.StartsWith("Songs/", StringComparison.Ordinal))));
        }

        private static async Task RenamedOnDrive(string sandbox)
        {
            using var rig = await Settled(sandbox, 200);
            int reads = rig.Drive.MediaReads;
            // Renamed on the web: the folder, and one file moved to another folder.
            var music = rig.Drive.Find(rig.FolderId, "Music")!;
            rig.Drive.Get(music)!.Name = "Library";
            rig.Drive.Changed(music);
            await rig.Pass();
            var files = rig.LocalFiles();
            Check("a folder renamed on Drive is renamed on the PC: nothing downloaded, nothing twice",
                rig.Drive.MediaReads == reads && files.Count == 200 && files.All(f => f.StartsWith("Library/", StringComparison.Ordinal)),
                $"{rig.Counts()}, first {files.FirstOrDefault()}");

            var song = rig.Drive.Find(rig.FolderId, "Library/album 000/track 00002.flac")!;
            var other = rig.Drive.Find(rig.FolderId, "Library/album 001")!;
            rig.Drive.Get(song)!.Parent = other;
            rig.Drive.Get(song)!.Name = "moved on the web.flac";
            rig.Drive.Changed(song);
            await rig.Pass();
            files = rig.LocalFiles();
            Check("a file moved and renamed on Drive is moved on the PC, not downloaded again",
                rig.Drive.MediaReads == reads && files.Contains("Library/album 001/moved on the web.flac") &&
                !files.Contains("Library/album 000/track 00002.flac") && files.Count == 200, rig.Counts());
            var drive = rig.DriveFiles();
            Check("and Drive still holds each once", drive.Paths.Count == 200 && drive.Twice == 0);
        }

        private static async Task CrashDuringMoves(string sandbox)
        {
            foreach (var point in new[] { "moved folder Music", "moved Music/album 000/track 00004.flac" })
            {
                using var rig = await Settled(sandbox, 100);
                int uploads = rig.Drive.Uploads;
                if (point.StartsWith("moved folder", StringComparison.Ordinal))
                    Directory.Move(rig.LocalPath("Music"), rig.LocalPath("Songs"));
                else
                    File.Move(rig.LocalPath("Music/album 000/track 00004.flac"), rig.LocalPath("Music/album 000/four.flac"));
                rig.Monitor.KillPoint = what => { if (what == point) throw new IOException("the app was closed here"); };
                try { await rig.Pass(); } catch { }
                rig.Monitor.KillPoint = null;
                rig.Restart();
                await rig.Pass();
                await rig.Pass();
                var after = rig.DriveFiles();
                Check($"a crash just after \"{point}\": nothing uploaded, every file once on each side",
                    rig.Drive.Uploads == uploads && after.Paths.Count == 100 && after.Twice == 0 && rig.LocalFiles().Count == 100,
                    $"{rig.Counts()}, Drive {after.Paths.Count}, PC {rig.LocalFiles().Count}");
            }
        }

        private static async Task MovesInEveryMode(string sandbox)
        {
            foreach (var mode in new[] { SyncMode.UploadOnly, SyncMode.DownloadOnly })
            {
                using var rig = await Settled(sandbox, 60, mode: mode);
                int uploads = rig.Drive.Uploads, renames = rig.Drive.Renames;
                Directory.Move(rig.LocalPath("Music"), rig.LocalPath("Songs"));
                await rig.Pass();
                var after = rig.DriveFiles();
                if (mode == SyncMode.UploadOnly)
                    Check("upload only: a folder renamed on the PC is renamed on Drive, nothing uploaded",
                        rig.Drive.Uploads == uploads && rig.Drive.Renames == renames + 1 && after.Paths.Count == 60 && after.Twice == 0,
                        rig.Counts());
                else
                    Check("download only: a folder renamed on the PC is not renamed on Drive",
                        rig.Drive.Renames == renames && rig.Drive.Uploads == uploads && after.Paths.All(p => p.StartsWith("Music/")),
                        rig.Counts());
            }
        }

        // ---------------- the changes feed ----------------

        private static async Task NothingChangedCostsTheFeedOnly(string sandbox)
        {
            using var rig = await Settled(sandbox, 2000);
            rig.ClearKinds();
            var clock = Stopwatch.StartNew();
            var s = await rig.Pass();
            Check("a pass with nothing changed asks the changes feed and lists no folder",
                rig.Kind("list") == 0 && rig.Kind("changes") >= 1, $"listings {rig.Kind("list")}, feed {rig.Kind("changes")}");
            Check("and still knows every file is on both sides", s.Matched == 2000, s.Matched.ToString());
            Console.WriteLine($"    (a pass over 2,000 files with nothing changed: {clock.ElapsedMilliseconds} ms)");
        }

        private static async Task OneEditOnEachSide(string sandbox)
        {
            using var rig = await Settled(sandbox, 500);
            int uploads = rig.Drive.Uploads, reads = rig.Drive.MediaReads, creates = rig.Drive.Creates;

            rig.WriteLocal("Music/album 003/track 00151.flac", Bytes(7777), T0.AddDays(2));
            rig.ClearKinds();
            await rig.Pass();
            Check("one PC edit: that one file goes up, in place, and no folder is listed",
                rig.Drive.Uploads == uploads + 1 && rig.Drive.Creates == creates && rig.Kind("list") == 0,
                $"{rig.Counts()}");

            var id = rig.Drive.Find(rig.FolderId, "Music/album 005/track 00260.flac")!;
            rig.Drive.Edit(id, Bytes(8888), T0.AddDays(3));
            rig.ClearKinds();
            await rig.Pass();
            // Its folder, and the one the PC edit went up into (this app's own
            // upload is in the feed too): two of the eleven.
            Check("one Drive edit: that one file comes down, and only the folders that changed are listed",
                rig.Drive.MediaReads == reads + 1 && rig.Kind("list") is >= 1 and <= 2, $"{rig.Counts()}");
            Check("and the PC has Drive's bytes",
                File.ReadAllBytes(rig.LocalPath("Music/album 005/track 00260.flac")).SequenceEqual(Bytes(8888)));

            rig.WriteLocal("Music/album 009/brand new.flac", Bytes(9999), T0.AddDays(4));
            rig.PutDrive("Music/album 002/new on the web.flac", Bytes(9998), T0.AddDays(4));
            await rig.Pass();
            var after = rig.DriveFiles();
            Check("a new file on each side: one up, one down, each once",
                after.Paths.Count == 502 && after.Twice == 0 && rig.LocalFiles().Count == 502, rig.Counts());

            File.Delete(rig.LocalPath("Music/album 004/track 00200.flac"));
            int trashes = rig.Drive.Trashes;
            await rig.Pass();
            Check("a PC delete with deletes not copied: put back from Drive, not trashed",
                rig.Drive.Trashes == trashes && File.Exists(rig.LocalPath("Music/album 004/track 00200.flac")), rig.Counts());
        }

        private static async Task FeedTroubleListsEverything(string sandbox)
        {
            foreach (var status in new[] { 400, 410, 404 })
            {
                using var rig = await Settled(sandbox, 300);
                rig.Drive.ChangesFail = status;
                rig.PutDrive("Music/album 001/arrived.flac", Bytes(4242), T0.AddDays(1));
                rig.ClearKinds();
                await rig.Pass();
                rig.Drive.ChangesFail = 0;
                Check($"the feed refusing ({status}): Drive is listed whole, and what changed still arrives",
                    rig.Kind("list") >= 7 && rig.LocalFiles().Contains("Music/album 001/arrived.flac") &&
                    rig.DriveFiles().Twice == 0, $"listings {rig.Kind("list")}, {rig.Counts()}");
            }

            using (var rig = await Settled(sandbox, 100))
            {
                DriveMonitor.FullListEvery = TimeSpan.Zero;
                rig.ClearKinds();
                await rig.Pass();
                DriveMonitor.FullListEvery = TimeSpan.FromHours(24);
                Check("once the day's full listing is due, Drive is listed whole", rig.Kind("list") >= 3, $"listings {rig.Kind("list")}");
            }
        }

        private static async Task FeedMissesAFile(string sandbox)
        {
            // Google never says a file arrived, and the same file is then made on
            // the PC. On the records alone it would go up a second time; the rule
            // is that a new file's folder is always listed first.
            using var rig = await Settled(sandbox, 200);
            int uploads = rig.Drive.Uploads;
            var data = Bytes(31337);
            rig.Drive.AddFileUnseen("both made it.flac", data, T0.AddDays(1), rig.Drive.Find(rig.FolderId, "Music/album 002")!);
            rig.WriteLocal("Music/album 002/both made it.flac", data, T0.AddDays(1));
            await rig.Pass();
            var after = rig.DriveFiles();
            Check("a file the feed never mentioned, made on the PC too: adopted, not uploaded a second time",
                rig.Drive.Uploads == uploads && after.Twice == 0 && after.Paths.Count == 201, rig.Counts());

            // In a folder the PC has never had: listed from the nearest folder Drive is known to have.
            var fresh = rig.DriveFolder("Music/album 002/unseen folder");
            rig.Drive.AddFileUnseen("deep.flac", Bytes(424242), T0.AddDays(1), fresh);
            rig.WriteLocal("Music/album 002/unseen folder/deep.flac", Bytes(424242), T0.AddDays(1));
            await rig.Pass();
            after = rig.DriveFiles();
            Check("the same in a new folder: adopted, not a second copy or a second folder",
                rig.Drive.Uploads == uploads && after.Twice == 0 && after.Paths.Count == 202, rig.Counts());
        }

        private static async Task DriveFolderRenamedMeansAFullListing(string sandbox)
        {
            using var rig = await Settled(sandbox, 300);
            var album = rig.Drive.Find(rig.FolderId, "Music/album 001")!;
            rig.Drive.Get(album)!.Name = "album one";
            rig.Drive.Changed(album);
            rig.ClearKinds();
            await rig.Pass();
            var files = rig.LocalFiles();
            Check("a folder renamed on Drive: listed whole, the PC's folder renamed, nothing twice",
                rig.Kind("list") >= 7 && files.Count == 300 && files.Count(f => f.StartsWith("Music/album one/")) == 50 &&
                rig.DriveFiles().Twice == 0, $"listings {rig.Kind("list")}, {rig.Counts()}");
        }

        // ---------------- stopped with uploads on the wire ----------------

        /// <summary>
        /// Seen on the real account: a pass cut off while uploads were in flight,
        /// Google storing one whose answer was lost, and the next pass, started at
        /// once, listing the folder before Google's listing showed it, and sending
        /// it again. Here the fake lags its listings and answers slowly, and the
        /// pass is cut off and restarted straight away. The control turns the
        /// grace off and must see the duplicate, or this test proves nothing.
        /// </summary>
        private static async Task StoppedMidUploadThenStraightBack(string sandbox)
        {
            var grace = DriveMonitor.InFlightGrace;
            try
            {
                foreach (var withGrace in new[] { true, false })
                {
                    DriveMonitor.InFlightGrace = withGrace ? TimeSpan.FromSeconds(20) : TimeSpan.Zero;
                    using var rig = await Settled(sandbox, 50);
                    for (int i = 0; i < 300; i++) rig.WriteLocal($"new/n{i:D4}.bin", Bytes(70_000 + i), T0.AddDays(1).AddSeconds(i));
                    rig.Drive.AnswerDelayMs = 150;
                    rig.Drive.ListingLag = TimeSpan.FromSeconds(3);
                    using (var cut = new CancellationTokenSource())
                    {
                        var running = rig.Monitor.SyncOneAsync(rig.Client, rig.Pair, cut.Token);
                        for (int i = 0; i < 400 && rig.Drive.Uploads < 60; i++) await Task.Delay(25);
                        cut.Cancel();
                        try { await running; } catch { }
                    }
                    rig.Restart();
                    rig.Drive.AnswerDelayMs = 0;
                    await rig.Pass();
                    rig.Drive.ListingLag = TimeSpan.Zero;
                    await rig.Pass();
                    await rig.Pass();
                    var after = rig.DriveFiles();
                    if (withGrace)
                        Check("cut off with uploads on the wire and back at once: every file once, nothing sent twice",
                            after.Paths.Count == 350 && after.Twice == 0, $"{after.Paths.Count} files, {after.Twice} twice, {rig.Counts()}");
                    else
                        Check("(control) the same with no grace for uploads on the wire does leave a second copy",
                            after.Twice > 0 || after.Paths.Count > 350, $"{after.Paths.Count} files, {after.Twice} twice");
                }
            }
            finally { DriveMonitor.InFlightGrace = grace; }
        }

        // ---------------- replacements in parallel ----------------

        private static async Task ReplacementsInParallel(string sandbox)
        {
            using var rig = await Settled(sandbox, 400);
            int creates = rig.Drive.Creates, updates = rig.Drive.Updates;
            var ids = rig.DriveFiles().Paths.ToDictionary(p => p, p => rig.Drive.Find(rig.FolderId, p));
            for (int i = 0; i < 400; i++)
                rig.WriteLocal($"Music/album {i / 50:D3}/track {i:D5}.flac", Bytes(i + 100_000), T0.AddDays(5).AddSeconds(i));
            var clock = Stopwatch.StartNew();
            var s = await rig.Pass();
            var after = rig.DriveFiles();
            Check("400 edited files go up as 400 in-place updates: the same ids, no new files",
                rig.Drive.Updates == updates + 400 && rig.Drive.Creates == creates && after.Paths.Count == 400 && after.Twice == 0 &&
                after.Paths.All(p => rig.Drive.Find(rig.FolderId, p) == ids[p]), $"{rig.Counts()}, status uploaded {s.Uploaded}");
            Console.WriteLine($"    (400 in-place updates: {clock.ElapsedMilliseconds} ms against the fake)");

            // Edited on Drive between the scan and the update: not overwritten.
            var target = "Music/album 000/track 00007.flac";
            rig.WriteLocal(target, Bytes(123), T0.AddDays(6));
            for (int i = 0; i < 100; i++)
                rig.WriteLocal($"Music/album 001/track {50 + i % 50:D5}.flac", Bytes(i + 300_000), T0.AddDays(6).AddSeconds(i));
            var targetId = rig.Drive.Find(rig.FolderId, target)!;
            bool edited = false;
            rig.Drive.BeforeAnswer = (kind, subject) =>
            {
                if (!edited && kind is "get" or "alive" && subject == targetId)
                {
                    edited = true;
                    rig.Drive.Edit(targetId, Bytes(456), T0.AddDays(6).AddHours(1));
                }
            };
            await rig.Pass();
            rig.Drive.BeforeAnswer = null;
            Check("a file edited on Drive just before its in-place update keeps Drive's edit",
                edited && rig.Drive.Get(targetId)!.Data.SequenceEqual(Bytes(456)), rig.Counts());
        }

        // ---------------- the preview ----------------

        private static async Task PreviewCounts(string sandbox)
        {
            using var rig = new Rig(sandbox);
            for (int i = 0; i < 100; i++)
            {
                rig.WriteLocal($"Music/{i:D3}.flac", Bytes(i), T0);
                rig.PutDrive($"Music/{i:D3}.flac", Bytes(i), T0);
            }
            for (int i = 0; i < 10; i++) rig.WriteLocal($"Only here/{i}.txt", Bytes(1000 + i, 1000), T0);
            for (int i = 0; i < 5; i++) rig.PutDrive($"Only there/{i}.txt", Bytes(2000 + i, 2000), T0);
            int writes = rig.Drive.Uploads + rig.Drive.Creates + rig.Drive.Trashes + rig.Drive.MediaReads;

            var lines = new List<string>();
            var p = await DriveMonitor.PreviewAsync(rig.Client, rig.Pair, CancellationToken.None, lines.Add);
            Check("the preview counts both sides",
                p.OnPc == 110 && p.OnDrive == 105, $"PC {p.OnPc}, Drive {p.OnDrive}");
            Check("the files alike on both sides are to be compared, never to be uploaded",
                p.ToCompare == 100 && p.Uploads == 10 && p.Downloads == 5 && p.BothKept == 0 && p.Deletes == 0,
                $"compare {p.ToCompare}, up {p.Uploads}, down {p.Downloads}, both {p.BothKept}, delete {p.Deletes}");
            Check("and the preview changed nothing anywhere",
                rig.Drive.Uploads + rig.Drive.Creates + rig.Drive.Trashes + rig.Drive.MediaReads == writes &&
                rig.LocalFiles().Count == 110);
            Check("it said how far it had got while it worked", lines.Count >= 1, lines.Count.ToString());
            var text = DriveMonitor.Describe(p);
            Check("the preview reads as plain sentences with the numbers",
                text.Contains("110") && text.Contains("105") && text.Contains("To upload: 10") && text.Contains("To download: 5") &&
                text.IndexOfAny(new[] { '*', '#', '`' }) < 0, text);

            var original = new DriveSyncPair();
            Check("a new pair is a first sync", DrivePairForm.IsFirstSync(original, rig.Pair));
            var same = rig.Pair.Copy();
            same.Mode = SyncMode.UploadOnly;
            Check("changing a pair's options is not", !DrivePairForm.IsFirstSync(rig.Pair, same));
            var moved = rig.Pair.Copy();
            moved.LocalFolder = rig.Pair.LocalFolder + "2";
            Check("changing its PC folder is", DrivePairForm.IsFirstSync(rig.Pair, moved));
            var other = rig.Pair.Copy();
            other.DriveFolderId = "another";
            Check("and so is changing its Drive folder", DrivePairForm.IsFirstSync(rig.Pair, other));
        }

        // ---------------- large pairs ----------------

        private static async Task MigratesToBatches(string sandbox)
        {
            var (large, batch) = (DriveMonitor.LargePairFiles, DriveMonitor.BatchFiles);
            try
            {
                DriveMonitor.LargePairFiles = int.MaxValue;
                using var rig = await Settled(sandbox, 300);
                var statePath = Path.Combine(DriveMonitor.StateDirectory, rig.Pair.Id + ".json");
                var shards = Path.Combine(DriveMonitor.StateDirectory, rig.Pair.Id + ".shards");
                Check("a small pair keeps one records file", File.Exists(statePath) && !Directory.Exists(shards));

                DriveMonitor.LargePairFiles = 100;
                DriveMonitor.BatchFiles = 40;
                int writes = rig.Drive.Uploads + rig.Drive.Creates + rig.Drive.Trashes + rig.Drive.MediaReads + rig.Drive.Renames;
                var s = await rig.Pass();
                Check("grown past the limit, its records move to a file per folder",
                    Directory.Exists(shards) && Directory.GetFiles(shards, "*.json").Length == 6,
                    Directory.Exists(shards) ? Directory.GetFiles(shards).Length.ToString() : "none");
                Check("and the move changes nothing on either side",
                    rig.Drive.Uploads + rig.Drive.Creates + rig.Drive.Trashes + rig.Drive.MediaReads + rig.Drive.Renames == writes &&
                    rig.LocalFiles().Count == 300 && s.Matched == 300, $"{rig.Counts()}, matched {s.Matched}");
                Check("the records file is now only the pair's header",
                    new FileInfo(statePath).Length < 64 * 1024, new FileInfo(statePath).Length.ToString());

                // And it carries on as a large pair: an edit, a move, nothing twice.
                rig.WriteLocal("Music/album 002/track 00120.flac", Bytes(5150), T0.AddDays(9));
                File.Move(rig.LocalPath("Music/album 004/track 00201.flac"), rig.LocalPath("Music/album 000/was 201.flac"));
                int uploads = rig.Drive.Uploads;
                await rig.Pass();
                var after = rig.DriveFiles();
                Check("as a large pair, an edit goes up in place and a move is a move",
                    rig.Drive.Uploads == uploads + 1 && after.Paths.Count == 300 && after.Twice == 0 &&
                    after.Paths.Contains("Music/album 000/was 201.flac"), rig.Counts());
                var nothing = await rig.Pass();
                Check("and a pass with nothing changed then does nothing",
                    rig.Drive.Uploads == uploads + 1 && nothing.Matched == 300, $"{rig.Counts()}, matched {nothing.Matched}");
            }
            finally
            {
                DriveMonitor.LargePairFiles = large;
                DriveMonitor.BatchFiles = batch;
            }
        }

        /// <summary>
        /// Memory and time over a mirrored tree of many files, the whole-tree way
        /// and a batch at a time. EXPLORERNATIVE_SYNCSCALE_HEAVY=1, and
        /// EXPLORERNATIVE_SYNCSCALE_FILES for how many (default 200,000).
        /// </summary>
        private static async Task MemoryAtScale(string sandbox)
        {
            int count = int.TryParse(Environment.GetEnvironmentVariable("EXPLORERNATIVE_SYNCSCALE_FILES"), out var n) ? n : 200_000;
            var (large, batch) = (DriveMonitor.LargePairFiles, DriveMonitor.BatchFiles);
            try
            {
                var modes = Environment.GetEnvironmentVariable("EXPLORERNATIVE_SYNCSCALE_MODES") == "batched"
                    ? new[] { true } : new[] { false, true };
                foreach (var batched in modes)
                {
                    DriveMonitor.LargePairFiles = batched ? 0 : int.MaxValue;
                    DriveMonitor.BatchFiles = 20_000;
                    using var rig = new Rig(sandbox);
                    for (int i = 0; i < count; i++)
                    {
                        var rel = $"Lib/part {i / 10_000:D3}/box {i / 500 % 20:D2}/f{i:D7}.bin";
                        var data = Bytes(i, 16);
                        rig.WriteLocal(rel, data, T0.AddSeconds(i));
                        rig.PutDrive(rel, data, T0.AddSeconds(i));
                    }
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    long before = GC.GetTotalMemory(true);
                    long peak = 0;
                    using var stop = new CancellationTokenSource();
                    var sampler = Task.Run(async () =>
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            // What is alive, not what is waiting to be collected.
                            peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true));
                            try { await Task.Delay(1000, stop.Token); } catch { }
                        }
                    });
                    var clock = Stopwatch.StartNew();
                    var s = await rig.Pass();
                    var first = clock.Elapsed;
                    clock.Restart();
                    var again = await rig.Pass();
                    var second = clock.Elapsed;
                    stop.Cancel();
                    await sampler;
                    var mode = batched ? "a batch at a time" : "whole tree";
                    Console.WriteLine($"    ({count:N0} mirrored files, {mode}: first pass {first.TotalSeconds:F1} s, " +
                                      $"pass with nothing changed {second.TotalSeconds:F1} s, live managed heap at most " +
                                      $"{(peak - before) / (1024 * 1024):N0} MB above the start, matched {s.Matched:N0} then {again.Matched:N0}, " +
                                      $"{rig.Counts()})");
                    Check($"{count:N0} mirrored files, {mode}: nothing uploaded or downloaded, all matched",
                        rig.Drive.Uploads == 0 && rig.Drive.MediaReads == 0 && s.Matched == count && again.Matched == count,
                        $"{rig.Counts()}, matched {s.Matched}, {again.Matched}");
                }
            }
            finally
            {
                DriveMonitor.LargePairFiles = large;
                DriveMonitor.BatchFiles = batch;
            }
        }

        private static void StatusWords()
        {
            var s = new DriveMonitor.PairStatus(null, null, true)
            {
                Phase = DriveMonitor.PhaseSyncing, Matched = 1000, StepDone = 0, StepTotal = 3, Moved = 1000,
            };
            Check("the progress line says how many moved", DriveMonitorForm.Progress(s).Contains("1,000 moved"), DriveMonitorForm.Progress(s));
            Equal("and the tally after a pass", "1000 moved, 2 uploaded", DriveMonitor.Tally(2, 0, 0, 1000));
        }

        private static void Equal(string what, string expected, string actual) =>
            Check(what, expected == actual, $"expected \"{expected}\", got \"{actual}\"");
    }
}
