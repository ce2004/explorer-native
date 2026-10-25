using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Folder sizes on the Drive letter, worked out from Drive's own listing of
    /// the whole account and kept current by the changes feed.
    /// </summary>
    internal static class DriveSizeTests
    {
        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var fake = new FakeDrive();
            var client = new DriveClient(fake, 0.0005);
            var old = new DateTime(2015, 6, 1, 0, 0, 0, DateTimeKind.Utc);

            var music = fake.AddFolder("Music");
            var artist = fake.AddFolder("Artist", music);
            var album = fake.AddFolder("Album", artist);
            fake.AddFile("a.flac", new byte[100], old, album);
            fake.AddFile("b.flac", new byte[50], DateTime.UtcNow, album);
            fake.AddFile("cover.jpg", new byte[7], old, artist);
            fake.AddFile("Notes", Array.Empty<byte>(), old, music, "application/vnd.google-apps.document");
            var empty = fake.AddFolder("Empty");

            // Over a page, spread across years, so the listing has to split by time.
            var many = fake.AddFolder("Many");
            for (int i = 0; i < 2500; i++)
                fake.AddFile($"t{i}.mp3", new byte[2], new DateTime(2001 + i % 25, 1 + i % 12, 1, 0, 0, 0, DateTimeKind.Utc), many);

            var index = new DriveSizeIndex(null);
            await index.Refresh(client, CancellationToken.None);

            var m = index.SizeOf(music);
            check("Drive sizes: a folder holds everything below it", m is { Bytes: 157, Files: 3, Folders: 2 },
                m?.ToString());
            check("Drive sizes: Google documents are not counted, as the letter does not show them",
                m?.Files == 3, m?.ToString());
            var e = index.SizeOf(empty);
            check("Drive sizes: an empty folder is zero, not unknown", e is { Bytes: 0, Files: 0 }, e?.ToString());
            var all = index.SizeOf(many);
            check("Drive sizes: a listing over a page, split by time, misses nothing",
                all is { Files: 2500, Bytes: 5000 }, all?.ToString());
            var root = index.SizeOf("root");
            check("Drive sizes: the root alias resolves to the real root", root is { Files: 2503 }, root?.ToString());
            check("Drive sizes: an unknown id is unknown", index.SizeOf("nope") == null, null);

            // Changes since: a new file, and one trashed.
            fake.AddFile("c.flac", new byte[1000], DateTime.UtcNow, album);
            fake.Trash(fake.Find(album, "a.flac")!);
            int before = fake.WholeListings;
            index.Expire();
            await index.Refresh(client, CancellationToken.None);
            var after = index.SizeOf(music);
            check("Drive sizes: the changes feed adds and removes", after is { Bytes: 1057, Files: 3 }, after?.ToString());
            equal("Drive sizes: a refresh does not list the account again", "0", (fake.WholeListings - before).ToString());

            // Fresh: a second question within the window asks nothing.
            int requests = fake.Requests;
            await index.Refresh(client, CancellationToken.None);
            equal("Drive sizes: a fresh index asks Drive nothing", "0", (fake.Requests - requests).ToString());

            // 4.2.1 (Conner: "sizes don't show up on Google Drive half the
            // time"): once built, a slow changes feed is not waited out. The held
            // sizes answer within AnswerWithin and the feed finishes behind.
            check("Drive sizes: a listed index says it is built", index.Built, null);
            fake.AddFile("d.flac", new byte[5], DateTime.UtcNow, album);
            index.Expire();
            fake.ChangesDelayMs = 4000;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var slow = index.Refresh(client, CancellationToken.None);
            bool timedOut = false;
            try { await slow.WaitAsync(DriveSizeIndex.AnswerWithin); }
            catch (TimeoutException) { timedOut = true; }
            var held = index.SizeOf(music);
            check("Drive sizes: a slow feed is not waited for", timedOut && clock.Elapsed < TimeSpan.FromSeconds(3),
                clock.Elapsed.ToString());
            check("Drive sizes: and the sizes held still answer", held is { Bytes: 1057 }, held?.ToString());
            fake.ChangesDelayMs = 0;
            await slow;
            check("Drive sizes: the feed catches up behind the answer", index.SizeOf(music) is { Bytes: 1062 },
                index.SizeOf(music)?.ToString());

            var source = SourceText("GoogleDrive.cs");
            check("Drive sizes: a built index answers within AnswerWithin, not after the feed",
                source.Contains("if (index.Built) await refresh.WaitAsync(DriveSizeIndex.AnswerWithin, token)"), null);
            check("Drive sizes: the Shared drives folder adds up its drives",
                source.Contains("IsSharedDrivesFolder(path)) return await SharedDrivesTotalAsync"), null);
            check("Drive sizes: the index is started as Drive connects",
                source.Contains("WarmSizes(mount.Letter ?? mount.Root)"), null);

            await LiveSizeTests(check);
            await GzipTests(check, equal);
            await SavedIndexTests(check, equal);
        }

        /// <summary>
        /// 4.2.4: listings ask Google for gzip; a file's own content never does,
        /// since a compressed answer to a Range request has the wrong offsets.
        /// </summary>
        private static async Task GzipTests(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            const string F = "https://www.googleapis.com/drive/v3/files";
            check("Gzip: a file listing asks for it", DriveClient.IsListing(F + "?q=x&pageSize=1000"), null);
            check("Gzip: the changes feed asks for it",
                DriveClient.IsListing("https://www.googleapis.com/drive/v3/changes?pageToken=1"), null);
            check("Gzip: the list of shared drives asks for it",
                DriveClient.IsListing("https://www.googleapis.com/drive/v3/drives?pageSize=100"), null);
            check("Gzip: a file's content does not", !DriveClient.IsListing(F + "/abc?alt=media"), null);
            check("Gzip: one file's details do not", !DriveClient.IsListing(F + "/abc?fields=name"), null);
            check("Gzip: an upload does not",
                !DriveClient.IsListing("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable"), null);

            var fake = new FakeDrive();
            var client = new DriveClient(fake, 0.0005);
            var folder = fake.AddFolder("Box");
            var data = new byte[5000];
            new Random(4).NextBytes(data);
            var file = fake.AddFile("x.bin", data, DateTime.UtcNow, folder);
            for (int i = 0; i < 1500; i++) fake.AddFile($"f{i}", new byte[3], new DateTime(2010 + i % 10, 1, 1, 0, 0, 0, DateTimeKind.Utc), folder);

            long before = DriveClient.GzipAnswers;
            var index = new DriveSizeIndex(null);
            await index.Refresh(client, CancellationToken.None);
            check("Gzip: the size listing came back compressed and was read right",
                DriveClient.GzipAnswers > before && index.SizeOf(folder) is { Files: 1501, Bytes: 9500 },
                $"{DriveClient.GzipAnswers - before} gzip, {index.SizeOf(folder)}");

            var part = await client.ReadRange(file, 1000, 200, CancellationToken.None);
            check("Gzip: a ranged read is not asked compressed, and its bytes are right",
                fake.MediaGzipAsked == 0 && part.AsSpan().SequenceEqual(data.AsSpan(1000, 200)), $"{fake.MediaGzipAsked} asked");

            fake.ChangesFail = 400;
            long errorsBefore = DriveClient.GzipAnswers;
            string? said = null;
            try { await client.ChangesSince("0", null, CancellationToken.None); }
            catch (DriveClient.DriveStatusException ex) { said = ex.Body; }
            catch (Exception ex) { said = "other: " + ex.Message; }
            fake.ChangesFail = 0;
            check("Gzip: a compressed error answer is unpacked before it is read",
                said != null && said.Contains("Invalid page token", StringComparison.Ordinal) && DriveClient.GzipAnswers > errorsBefore,
                said);
        }

        /// <summary>
        /// 4.2.4: the index is saved between runs, so a restart asks the changes
        /// feed what changed rather than listing a whole drive again.
        /// </summary>
        private static async Task SavedIndexTests(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EN-savedsizes-" + Guid.NewGuid().ToString("N")[..8]);
            System.IO.Directory.CreateDirectory(dir);
            var oldMaySave = DriveSizeIndex.MaySave;
            DriveSizeIndex.MaySave = null;
            try
            {
                // The format by itself: a parent outside the list, none at all, a big size.
                var items = new List<DriveSizeItem>
                {
                    new("a", null, 0, true, true),
                    new("b", "a", 5_000_000_000_000, false, true),
                    new("c", "outside", 7, false, false),
                };
                var (rootId, changes, back) = DriveSizeIndex.Decode(DriveSizeIndex.Encode("d1", "r", "42", items), "d1");
                check("Saved sizes: the format keeps every item exactly",
                    rootId == "r" && changes == "42" && back.Count == 3 && back[0] == items[0] && back[1] == items[1] && back[2] == items[2],
                    string.Join(", ", back));
                bool wrongDrive = false;
                try { DriveSizeIndex.Decode(DriveSizeIndex.Encode("d1", "r", "42", items), "d2"); }
                catch (System.IO.InvalidDataException) { wrongDrive = true; }
                check("Saved sizes: a copy saved for another drive is refused", wrongDrive, null);

                var fake = new FakeDrive();
                var client = new DriveClient(fake, 0.0005);
                var music = fake.AddFolder("Music");
                var album = fake.AddFolder("Album", music);
                fake.AddFile("a.flac", new byte[100], DateTime.UtcNow, album);
                for (int i = 0; i < 1200; i++) fake.AddFile($"t{i}", new byte[1], new DateTime(2005 + i % 15, 1, 1, 0, 0, 0, DateTimeKind.Utc), music);
                var path = System.IO.Path.Combine(dir, "acct-mydrive.bin");

                var first = new DriveSizeIndex(null, path);
                await first.Refresh(client, CancellationToken.None);
                var raw = System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : Array.Empty<byte>();
                check("Saved sizes: a whole listing is saved at once", raw.Length > 0, null);
                check("Saved sizes: and the file is encrypted", ProtectedFile.IsProtected(raw), null);

                // A change while the app is closed, then a restart.
                fake.AddFile("b.flac", new byte[50], DateTime.UtcNow, album);
                int listings = fake.WholeListings;
                var second = new DriveSizeIndex(null, path);
                await second.Refresh(client, CancellationToken.None);
                equal("Saved sizes: a restart does not list the drive again", "0", (fake.WholeListings - listings).ToString());
                check("Saved sizes: and the changes feed brings it up to date",
                    second.SizeOf(music) is { Files: 1202, Bytes: 1350 }, second.SizeOf(music)?.ToString());
                check("Saved sizes: the root alias survives the save", second.SizeOf("root") is { Files: 1202 },
                    second.SizeOf("root")?.ToString());

                // Unmount saves what the feed brought in; the next start begins there.
                second.SaveNow();
                var third = new DriveSizeIndex(null, path);
                await third.Refresh(client, CancellationToken.None);
                check("Saved sizes: what the feed brought is saved at unmount", third.SizeOf(music) is { Files: 1202 },
                    third.SizeOf(music)?.ToString());

                // The feed refuses the saved bookmark: listed whole, as before.
                fake.AddFault(new FakeDrive.Fault { Kind = "changes", Status = 410, Times = 1,
                    Body = FakeDrive.ErrorBody(410, "invalid", "Invalid page token") });
                listings = fake.WholeListings;
                var expired = new DriveSizeIndex(null, path);
                await expired.Refresh(client, CancellationToken.None);
                check("Saved sizes: a refused bookmark lists the drive whole again",
                    fake.WholeListings > listings && expired.SizeOf(music) is { Files: 1202 },
                    $"{fake.WholeListings - listings} listings, {expired.SizeOf(music)}");

                // A damaged copy is thrown away and the drive listed.
                System.IO.File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                listings = fake.WholeListings;
                var damaged = new DriveSizeIndex(null, path);
                await damaged.Refresh(client, CancellationToken.None);
                check("Saved sizes: a damaged copy is listed afresh and replaced",
                    fake.WholeListings > listings && damaged.SizeOf(music) is { Files: 1202 } &&
                    ProtectedFile.IsProtected(System.IO.File.ReadAllBytes(path)), damaged.SizeOf(music)?.ToString());

                // Signed out: nothing is written back.
                System.IO.File.Delete(path);
                DriveSizeIndex.MaySave = () => false;
                var signedOut = new DriveSizeIndex(null, path);
                await signedOut.Refresh(client, CancellationToken.None);
                check("Saved sizes: nothing is saved once signed out", !System.IO.File.Exists(path), null);
                DriveSizeIndex.MaySave = null;

                // Kept per account and drive; another account's copies go, sign-out takes them all.
                var oldAppData = Settings.OverrideAppDataDir;
                Settings.OverrideAppDataDir = dir;
                try
                {
                    var mine = GoogleDrive.SavedSizesPath("Me@example.com", null);
                    var mineShared = GoogleDrive.SavedSizesPath("me@example.com", "0AbcShared");
                    var theirs = GoogleDrive.SavedSizesPath("other@example.com", null);
                    check("Saved sizes: named by account and drive, without the address",
                        mine != mineShared && mine != theirs && !mine.Contains("example", StringComparison.OrdinalIgnoreCase), mine);
                    System.IO.Directory.CreateDirectory(GoogleDrive.SavedSizesDirectory);
                    foreach (var p in new[] { mine, mineShared, theirs }) System.IO.File.WriteAllBytes(p, new byte[] { 1 });
                    GoogleDrive.ForgetSavedSizes(keepAccount: "me@example.com");
                    check("Saved sizes: another account's copies are deleted, this one's kept",
                        System.IO.File.Exists(mine) && System.IO.File.Exists(mineShared) && !System.IO.File.Exists(theirs), null);
                    GoogleDrive.ForgetSavedSizes(keepAccount: null);
                    check("Saved sizes: signing out deletes them all",
                        !System.IO.File.Exists(mine) && !System.IO.File.Exists(mineShared), null);
                }
                finally { Settings.OverrideAppDataDir = oldAppData; }

                var source = SourceText("GoogleDrive.cs");
                check("Saved sizes: sign-out forgets the saved sizes",
                    source.Contains("ForgetSavedSizes(keepAccount: null);"), null);
                check("Saved sizes: unmount saves what changed", source.Contains("index.SaveNow();"), null);
            }
            finally
            {
                DriveSizeIndex.MaySave = oldMaySave;
                try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// 4.2.1 (Conner: sizes "don't report correctly after changing on local
        /// and google drive"). A folder's own date moves only for a change
        /// directly inside it, so a file added deeper answered the old size.
        /// </summary>
        private static async Task LiveSizeTests(Action<string, bool, string?> check)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EN-livesize-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var deep = System.IO.Path.Combine(dir, "Music", "Artist", "Album");
                System.IO.Directory.CreateDirectory(deep);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(deep, "a.flac"), new byte[100]);
                var music = System.IO.Path.Combine(dir, "Music");
                using var sizes = new FolderSizeCalculator();
                var first = await sizes.CalculateAsync(music);
                check("Live sizes: measured once", first.Bytes == 100, first.ToString());

                var added = System.IO.Path.Combine(deep, "b.flac");
                System.IO.File.WriteAllBytes(added, new byte[50]);
                var stale = await sizes.CalculateAsync(music);
                check("Live sizes: (control) a file added two levels down is missed by the cache alone",
                    stale.Bytes == 100, stale.ToString());

                sizes.Forget(added);
                check("Live sizes: a change forgets every folder above it",
                    !sizes.TryGetCached(music, out _), null);
                var fresh = await sizes.CalculateAsync(music);
                check("Live sizes: and the folder is measured right again", fresh.Bytes == 150, fresh.ToString());

                await sizes.CalculateAsync(deep);
                sizes.Forget(System.IO.Path.Combine(dir, "Music", "Artist"));
                check("Live sizes: a renamed or deleted folder forgets what is inside it",
                    !sizes.TryGetCached(deep, out _), null);
                await sizes.CalculateAsync(deep);
                sizes.Forget(System.IO.Path.Combine(dir, "Other", "x.txt"));
                check("Live sizes: a change elsewhere leaves unrelated folders alone",
                    sizes.TryGetCached(deep, out _), null);

                var main = SourceText("MainForm.cs");
                check("Live sizes: the window watches below the folder for sizes",
                    main.Contains("IncludeSubdirectories = true") && main.Contains("_folderSizes.Forget(plain)"), null);
                check("Live sizes: the 4-second timer re-measures what changed",
                    main.Contains("_availabilityTimer.Tick += (_, _) => RefreshLiveSizes();"), null);
                check("Live sizes: a row is redrawn only when its size changed",
                    main.Contains("if (pane.Entries[i].SizeOverride == text) return;"), null);
                check("Live sizes: Drive folders ask Drive's changes feed while shown",
                    main.Contains("drive!.ExpireSizesIfIdle();"), null);
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// Times a whole size listing of one real drive, found by name, without
        /// gzip and then with it, and a save and load of the result. Talks to
        /// the Drive API only; never touches the letter.
        /// </summary>
        internal static async Task<int> Bench(string driveName)
        {
            var creds = GoogleAuth.Credentials(GoogleDrive.CredentialsDirectory);
            if (creds == null || !System.IO.File.Exists(GoogleDrive.SavedTokenPath))
            {
                Console.WriteLine("bench: no saved Google sign-in");
                return 2;
            }
            var auth = new GoogleAuth(creds.Value.ClientId, creds.Value.ClientSecret, GoogleDrive.SavedTokenPath) { AllowConsent = false };
            var client = new DriveClient(auth);

            string? driveId = null;
            if (driveName.Length > 0)
            {
                var drives = await client.ListSharedDrives(CancellationToken.None);
                var match = drives.Find(d => d.Name.Equals(driveName, StringComparison.OrdinalIgnoreCase))
                            ?? drives.Find(d => d.Name.Contains(driveName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    Console.WriteLine($"bench: no shared drive called {driveName}");
                    return 2;
                }
                driveId = match.Id;
                Console.WriteLine($"bench: {match.Name} ({driveId})");
            }

            List<DriveSizeItem>? items = null;
            foreach (var gzip in new[] { false, true, false, true })
            {
                DriveClient.ListingGzip = gzip;
                long before = DriveClient.GzipAnswers;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                items = await client.ListForSizes(driveId, CancellationToken.None);
                Console.WriteLine($"bench: gzip {(gzip ? "on " : "off")}  {items.Count} items  " +
                                  $"{clock.Elapsed.TotalSeconds:0.0} s  ({DriveClient.GzipAnswers - before} gzip answers)");
            }
            DriveClient.ListingGzip = true;

            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EN-sizebench-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
            try
            {
                var save = System.Diagnostics.Stopwatch.StartNew();
                var data = DriveSizeIndex.Encode(driveId, driveId ?? "root", "1", items!);
                ProtectedFile.WriteAllBytes(path, data);
                Console.WriteLine($"bench: saved {items!.Count} items, {new System.IO.FileInfo(path).Length / 1024} KB on disk, {save.ElapsedMilliseconds} ms");
                var load = System.Diagnostics.Stopwatch.StartNew();
                var back = DriveSizeIndex.Decode(ProtectedFile.ReadAllBytes(path), driveId);
                Console.WriteLine($"bench: loaded {back.Items.Count} items in {load.ElapsedMilliseconds} ms");
            }
            finally { try { System.IO.File.Delete(path); } catch { } }

            // A start and a restart through the real index: the second must load
            // the saved copy and ask only the changes feed.
            var store = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EN-sizebench-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
            var oldLog = DriveSizeIndex.Log;
            DriveSizeIndex.Log = line => Console.WriteLine("bench index: " + line);
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var first = new DriveSizeIndex(driveId, store);
                await first.Refresh(client, CancellationToken.None);
                var a = first.SizeOf(driveId ?? "root");
                Console.WriteLine($"bench: first start {clock.Elapsed.TotalSeconds:0.0} s, root {a}");

                clock.Restart();
                var second = new DriveSizeIndex(driveId, store);
                await second.Refresh(client, CancellationToken.None);
                var b = second.SizeOf(driveId ?? "root");
                Console.WriteLine($"bench: restart {clock.Elapsed.TotalSeconds:0.0} s, root {b}, same: {a == b}");
            }
            finally
            {
                DriveSizeIndex.Log = oldLog;
                try { System.IO.File.Delete(store); } catch { }
            }
            return 0;
        }

        private static string SourceText(string name)
        {
            var at = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (at != null && !System.IO.Directory.Exists(System.IO.Path.Combine(at.FullName, "src"))) at = at.Parent;
            if (at == null) return "";
            var path = System.IO.Path.Combine(at.FullName, "src", name);
            try { return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : ""; }
            catch { return ""; }
        }
    }
}
