using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of the folder sync engine, second half.
    /// </summary>
    internal static class Sweep500Sync3Tests
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

            Console.WriteLine("Sweep 5.0.0, the folder sync engine, second half:");
            var previous = Settings.OverrideAppDataDir;
            var sandbox = Path.Combine(Path.GetTempPath(), "en-sweep500-sync3-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            Settings.OverrideAppDataDir = Path.Combine(sandbox, "appdata");
            try
            {
                await SharedDriveFeedTests(sandbox);
                await TwinWithRecordsTests(sandbox, "Disc");
                await TwinWithRecordsTests(sandbox, "disc");
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

            public Rig(string sandbox, SyncMode mode = SyncMode.TwoWay, bool deletes = false,
                Func<FakeDrive, HttpMessageHandler>? wrap = null, string drivePath = "My Drive/P", GoogleDrive? drive = null)
            {
                var top = Path.Combine(sandbox, Guid.NewGuid().ToString("N")[..8]);
                Local = Path.Combine(top, "P");
                Directory.CreateDirectory(Local);
                FolderId = Drive.AddFolder("P");
                Pair = new DriveSyncPair
                {
                    Name = "P", LocalFolder = Local, DriveFolderId = FolderId, DriveFolderPath = drivePath,
                    Mode = mode, CopyDeletes = deletes, CheckMinutes = 0,
                };
                Client = new DriveClient(wrap?.Invoke(Drive) ?? Drive, Fast);
                Monitor = new DriveMonitor(drive ?? new GoogleDrive(), (_, _) => { }, _ => { })
                {
                    WaitScale = Fast,
                    DiskOfRoot = _ => (1L << 42, 1L << 43, "T:"),
                    RecycleLocal = path => { File.Delete(path); return null; },
                    PatienceLimit = TimeSpan.FromSeconds(3),
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

            public string LocalPath(string relative) => Path.Combine(Local, relative.Replace('/', '\\'));

            public Dictionary<string, string> DriveTree() =>
                Drive.Tree(FolderId).ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetString(kv.Value), StringComparer.OrdinalIgnoreCase);

            public void Dispose()
            {
                Monitor.Dispose();
                try { Directory.Delete(Path.GetDirectoryName(Local)!, true); } catch { }
            }
        }

        private static async Task InBatches(Func<Task> run)
        {
            var (large, batch) = (DriveMonitor.LargePairFiles, DriveMonitor.BatchFiles);
            DriveMonitor.LargePairFiles = 0;
            DriveMonitor.BatchFiles = 7;
            try { await run(); }
            finally
            {
                DriveMonitor.LargePairFiles = large;
                DriveMonitor.BatchFiles = batch;
            }
        }

        // ---------------- a shared drive whose drive could not be told ----------------

        /// <summary>
        /// Answers "which drive is this folder on" as a shared drive (the fake
        /// has only one feed), fails that question when told to, and notes every
        /// changes-feed request so a test can see which feed was read.
        /// </summary>
        private sealed class SharedDriveHandler : DelegatingHandler
        {
            public int FailBlips;          // 503s still to give
            public bool Refuse;            // a lasting 400
            public readonly List<string> Feeds = new();

            public SharedDriveHandler(HttpMessageHandler inner) : base(inner) { }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.StartsWith("/drive/v3/changes", StringComparison.Ordinal))
                    lock (Feeds) Feeds.Add(uri.AbsolutePath + uri.Query);
                if (request.Method == HttpMethod.Get && uri.AbsolutePath.StartsWith("/drive/v3/files/", StringComparison.Ordinal)
                    && Uri.UnescapeDataString(uri.Query).Contains("fields=driveId", StringComparison.Ordinal))
                {
                    if (Refuse)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                        { Content = new StringContent("{\"error\":{\"code\":400,\"message\":\"bad\"}}") });
                    if (FailBlips > 0)
                    {
                        FailBlips--;
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        { Content = new StringContent("{\"error\":{\"code\":503,\"message\":\"backend\"}}") });
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{\"driveId\":\"sd1\"}", Encoding.UTF8, "application/json") });
                }
                return base.SendAsync(request, token);
            }
        }

        /// <summary>
        /// A batched pair on a shared drive: when the question "which drive"
        /// failed, the pass read My Drive's changes feed (no driveId) and saved
        /// its token as the pair's, so the shared drive's own changes were
        /// missed until the daily full listing. Now a blip is waited out, and a
        /// drive that cannot be told lists whole and saves no token.
        /// </summary>
        private static async Task SharedDriveFeedTests(string sandbox) => await InBatches(async () =>
        {
            SharedDriveHandler handler = null!;
            using var rig = new Rig(sandbox, wrap: d => handler = new SharedDriveHandler(d));
            rig.Write("a.txt", "a", T0);
            var problem = await rig.Pass();
            await rig.Pass();
            Check("shared drive feed: the pair settled first", problem == null && rig.DriveTree().ContainsKey("a.txt"), problem);

            lock (handler.Feeds) handler.Feeds.Clear();
            handler.FailBlips = 1;
            rig.Drive.AddFile("b.txt", Encoding.UTF8.GetBytes("b"), T0, rig.FolderId);
            await rig.Pass();
            await rig.Pass();
            Check("shared drive feed: a blip on 'which drive' is waited out and the new file comes down",
                File.Exists(rig.LocalPath("b.txt")));

            handler.Refuse = true;
            rig.Drive.AddFile("c.txt", Encoding.UTF8.GetBytes("c"), T0, rig.FolderId);
            problem = await rig.Pass();
            handler.Refuse = false;
            await rig.Pass();
            Check("shared drive feed: a drive that cannot be told still syncs, by a whole listing",
                File.Exists(rig.LocalPath("c.txt")), problem);

            string[] wrong;
            lock (handler.Feeds) wrong = handler.Feeds.Where(f => !f.Contains("driveId=sd1", StringComparison.Ordinal)).ToArray();
            Check("shared drive feed: My Drive's feed is never read or saved for a shared-drive pair",
                wrong.Length == 0, string.Join(" | ", wrong));
        });

        // ---------------- a recorded folder that became a Drive twin ----------------

        /// <summary>
        /// A batched pair has records in Disc. A second Drive folder called Disc
        /// (or disc) appears beside it, both holding files, so both are left out
        /// as a clash. After one full listing the old Disc id was gone from the
        /// folder map while the records still named Disc, and every later pass
        /// listed all of Drive again, for good. Now a folder Drive leaves out is
        /// known, and the feed carries on.
        /// </summary>
        private static async Task TwinWithRecordsTests(string sandbox, string twinName) => await InBatches(async () =>
        {
            using var rig = new Rig(sandbox);
            rig.Write("Disc/a.txt", "a", T0);
            for (int i = 1; i <= 3; i++) rig.Write($"Other{i}/o.txt", "o" + i, T0);
            var problem = await rig.Pass();
            await rig.Pass();
            Check($"twin {twinName}: the pair settled first", problem == null && rig.DriveTree().ContainsKey("Disc/a.txt"), problem);
            var discId = rig.Drive.Find(rig.FolderId, "Disc")!;
            var aId = rig.Drive.Find(rig.FolderId, "Disc/a.txt")!;

            var twin = rig.Drive.AddFolder(twinName, rig.FolderId);
            rig.Drive.AddFile("b.txt", Encoding.UTF8.GetBytes("b"), T0, twin);
            var every = DriveMonitor.FullListEvery;
            DriveMonitor.FullListEvery = TimeSpan.Zero;
            try { await rig.Pass(); }
            finally { DriveMonitor.FullListEvery = every; }

            // A listing of all of Drive lists the pair's other folders, which
            // nothing changed.
            var others = Enumerable.Range(1, 3).Select(i => rig.Drive.Find(rig.FolderId, $"Other{i}")!).ToHashSet();
            var listedOthers = new List<string>();
            rig.Drive.BeforeAnswer = (kind, subject) => { if (kind == "list" && others.Contains(subject)) lock (listedOthers) listedOthers.Add(subject); };
            problem = await rig.Pass();
            int lists;
            lock (listedOthers) { lists = listedOthers.Count; listedOthers.Clear(); }
            Check($"twin {twinName}: the next pass goes by the feed, not a listing of all of Drive", lists == 0, $"{lists} untouched folders listed");

            await rig.Pass();
            lock (listedOthers) lists = listedOthers.Count;
            Check($"twin {twinName}: and so does the one after", lists == 0, $"{lists} untouched folders listed");
            rig.Drive.BeforeAnswer = null;

            Check($"twin {twinName}: nothing was trashed or deleted on either side",
                File.Exists(rig.LocalPath("Disc/a.txt")) && rig.Drive.Get(aId) is { Trashed: false } && rig.Drive.Get(discId) is { Trashed: false });

            // The twin goes: Disc syncs again on the records it kept.
            rig.Drive.Trash(twin);
            rig.Write("Disc/new.txt", "new", T0);
            int uploads = rig.Drive.Uploads;
            problem = await rig.Pass();
            await rig.Pass();
            Check($"twin {twinName}: once the twin is gone, a new PC file goes up into Disc",
                rig.Drive.Find(discId, "new.txt") != null, problem);
            Check($"twin {twinName}: and the file already there is neither sent again nor trashed",
                rig.Drive.Uploads - uploads == 1 && rig.Drive.Get(aId) is { Trashed: false } && File.Exists(rig.LocalPath("Disc/a.txt")),
                $"{rig.Drive.Uploads - uploads} uploads");
        });
    }
}
