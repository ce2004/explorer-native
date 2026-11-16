using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text.Json.Nodes;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System;

namespace ExplorerNative
{
    /// <summary>
    /// Sweep 3.0.8, folder sync: a batched pass that had to list Drive whole
    /// after all failing every time with "Sequence contains no matching
    /// element"; a size limit not in the list silently taken away by saving any
    /// other change to the folder; and the add/change dialog's OK looking at the
    /// PC folder on the UI thread.
    /// </summary>
    internal static class Sweep308SyncTests
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
            Console.WriteLine("Sweep 3.0.8, folder sync:");

            var previous = Settings.OverrideAppDataDir;
            var sandbox = Path.Combine(Path.GetTempPath(), "en-sweep308-sync-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            Settings.OverrideAppDataDir = Path.Combine(sandbox, "appdata");
            try
            {
                await BatchedRetryListsWholeTests(sandbox);
                SizeLimitKeptTests();
                OkOffTheUiThreadTests();
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(sandbox, true); } catch { }
            }
            Console.WriteLine();
        }

        // ---------------- a batched pass told to list Drive whole ----------------

        /// <summary>
        /// A large pair on the changes feed meets records in a folder its Drive
        /// folder map does not have (the folder gone from the PC, so nothing
        /// lists it): it starts again listing Drive whole. That second start
        /// worked out why with First() over the recorded folders, which found
        /// none, threw, and failed the pair on every pass until the daily full
        /// listing. Now it lists Drive whole and carries on: two-way with deletes
        /// off, the file gone from the PC comes back down.
        /// </summary>
        private static async Task BatchedRetryListsWholeTests(string sandbox)
        {
            int largeBefore = DriveMonitor.LargePairFiles;
            var top = Path.Combine(sandbox, Guid.NewGuid().ToString("N")[..8]);
            var local = Path.Combine(top, "local");
            Directory.CreateDirectory(local);
            var drive = new FakeDrive();
            var folderId = drive.AddFolder("Sync");
            var pair = new DriveSyncPair
            {
                Name = "Batched", LocalFolder = local, DriveFolderId = folderId, DriveFolderPath = "My Drive/Sync",
                Mode = SyncMode.TwoWay, CopyDeletes = false, CheckMinutes = 0,
            };
            var said = new List<string>();
            var client = new DriveClient(drive, Fast);
            var monitor = new DriveMonitor(new GoogleDrive(), (id, message) => { lock (said) said.Add(id + ": " + message); }, _ => { })
            {
                WaitScale = Fast,
                DiskOfRoot = _ => (1L << 42, 1L << 43, "T:"),
                RecycleLocal = path => { File.Delete(path); return null; },
            };
            try
            {
                // Every pair a batch at a time, as a big one is.
                DriveMonitor.LargePairFiles = 0;

                var file = Path.Combine(local, "A", "f.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "kept in a folder");
                File.SetLastWriteTimeUtc(file, T0);

                await monitor.SyncOneAsync(client, pair, CancellationToken.None);
                Check("batched retry: the first pass sent the file up",
                    drive.Tree(folderId).ContainsKey("A/f.txt"), monitor.StatusOf(pair.Id).Problem);
                await monitor.SyncOneAsync(client, pair, CancellationToken.None);   // on the feed now, nothing to do
                Equal("batched retry: the second pass, on the feed, had no problem", "",
                    monitor.StatusOf(pair.Id).Problem ?? "");

                // The folder map lost "A" (as a stopped pass or an older state can
                // leave it), and the folder is gone from the PC, so no listing on
                // the feed finds it again. A whole walk of the PC, so the pass
                // reaches A's records.
                var statePath = Path.Combine(DriveMonitor.StateDirectory, pair.Id + ".json");
                var node = JsonNode.Parse(ProtectedFile.ReadAllUtf8(statePath))!.AsObject();
                Check("batched retry: the pair went a batch at a time",
                    node["Sharded"]?.GetValue<bool>() == true, node.ToJsonString().Length.ToString());
                if (node["FolderIds"] is JsonObject ids)
                    foreach (var key in ids.Select(kv => kv.Key).Where(k => k.Equals("A", StringComparison.OrdinalIgnoreCase)).ToList())
                        ids.Remove(key);
                if (node["RecordFolders"] is JsonArray recorded)
                    for (int i = recorded.Count - 1; i >= 0; i--)
                        if (string.Equals(recorded[i]?.GetValue<string>(), "A", StringComparison.OrdinalIgnoreCase)) recorded.RemoveAt(i);
                node["LastPcWalkUtc"] = null;
                ProtectedFile.WriteAllUtf8(statePath, Encoding.UTF8.GetBytes(node.ToJsonString()));
                Directory.Delete(Path.Combine(local, "A"), true);

                await monitor.SyncOneAsync(client, pair, CancellationToken.None);
                var problem = monitor.StatusOf(pair.Id).Problem;
                Check("batched retry: listing Drive whole after all is not a failure (it threw: no matching element)",
                    problem == null, problem);
                Check("batched retry: and the pass carried on: the file deleted on the PC, deletes off, came back down",
                    File.Exists(file) && File.ReadAllText(file) == "kept in a folder",
                    string.Join(", ", Directory.Exists(local) ? Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories) : Array.Empty<string>()));
                Check("batched retry: nothing was trashed on Drive", drive.Trashes == 0, drive.Trashes.ToString());
            }
            finally
            {
                DriveMonitor.LargePairFiles = largeBefore;
                monitor.Dispose();
                try { Directory.Delete(top, true); } catch { }
            }
        }

        // ---------------- a size limit not in the list ----------------

        /// <summary>
        /// A pair's "Skip files larger than" of a value the list does not have
        /// (250 MB, set in settings.json) showed as "No limit", and OK saved it
        /// as no limit: changing only the name started syncing files the pair had
        /// always left out.
        /// </summary>
        private static void SizeLimitKeptTests()
        {
            var usual = DrivePairForm.SizeChoices(500);
            Check("size limit: a value in the list gives the list as it was",
                usual.Length == 5 && usual.Any(s => s.Megabytes == 500));
            Equal("size limit: no limit gives the list as it was", "5", DrivePairForm.SizeChoices(0).Length.ToString());
            var odd = DrivePairForm.SizeChoices(250);
            int at = Array.FindIndex(odd, s => s.Megabytes == 250);
            Check("size limit: a value not in the list is offered, so saving keeps it", at >= 0,
                string.Join(", ", odd.Select(s => s.Text)));
            Check("size limit: still in order, No limit first",
                odd[0].Megabytes == 0 && odd.Select(s => s.Megabytes).SequenceEqual(odd.Select(s => s.Megabytes).OrderBy(m => m)));

            string shown = "", error = "";
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = new DrivePairForm(new DriveSyncPair { LocalFolder = @"C:\Music", DriveFolderId = "x", MaxFileMegabytes = 250 },
                        "Change a synced folder");
                    var combo = FindAll<ComboBox>(form).FirstOrDefault(c => c.AccessibleName == "Skip files larger than");
                    shown = combo?.SelectedItem as string ?? "(no combo)";
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(60))) error = "timed out";
            Equal("size limit: the dialog shows the pair's own limit, not No limit", "250 megabytes", error.Length > 0 ? error : shown);
        }

        private static List<T> FindAll<T>(Control root) where T : Control
        {
            var found = new List<T>();
            foreach (Control child in root.Controls)
            {
                if (child is T match) found.Add(match);
                found.AddRange(FindAll<T>(child));
            }
            return found;
        }

        // ---------------- the dialog's OK, off the UI thread ----------------

        /// <summary>
        /// Refusal reads the disk (Directory.Exists). The dialog asked it on the
        /// UI thread, so a PC folder on a share that had gone away froze the
        /// window. Every call in the configurator is on a worker now.
        /// </summary>
        private static void OkOffTheUiThreadTests()
        {
            var form = Source("DriveMonitorForm.cs");
            Check("OK: the configurator's source is there to read", form != null);
            if (form == null) return;
            int calls = 0, offThread = 0;
            for (int i = form.IndexOf("DriveMonitor.Refusal(", StringComparison.Ordinal); i >= 0;
                 i = form.IndexOf("DriveMonitor.Refusal(", i + 1, StringComparison.Ordinal))
            {
                calls++;
                int line = form.LastIndexOf('\n', i) + 1;
                if (form[line..i].Contains("Task.Run(", StringComparison.Ordinal)) offThread++;
            }
            Check("OK: the folder is checked by Refusal", calls > 0);
            Equal("OK: every Refusal in the configurator runs on a worker, never on the UI thread", calls.ToString(), offThread.ToString());
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }

    // ---- merged from Sweep309SyncTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// Sweep 3.0.9, folder sync: the sync window freezing on a pair whose PC
    /// folder is on a dead share; a PC move whose new file was open in another
    /// program trashing Drive's copy; a conflict copy that could not step aside;
    /// a replacement whose Drive file had gone being sent unguarded, and the old
    /// copy trashed without a second look; a folder the changes feed named in
    /// Drive's spelling read as gone; a deep folder's changes missed by the
    /// change journal; a removed pair's history written back; and the folder
    /// dialogs looking at the disk on the UI thread.
    /// </summary>
    internal static class Sweep309SyncTests
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
            Console.WriteLine("Sweep 3.0.9, folder sync:");

            var previous = Settings.OverrideAppDataDir;
            var sandbox = Path.Combine(Path.GetTempPath(), "en-sweep309-sync-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            Settings.OverrideAppDataDir = Path.Combine(sandbox, "appdata");
            try
            {
                ReloadOffTheUiThreadTests(sandbox);
                await LockedMoveWaitsTests(sandbox);
                await MoveWithoutRecordedChecksumTests(sandbox);
                await ConflictAsideTests(sandbox);
                await ReplacementGoneTests(sandbox);
                await ReplacedCopyRecheckedTests(sandbox);
                await FeedFolderSpelledByDriveTests(sandbox);
                DeepJournalFolderTests(sandbox);
                await RemovedPairHistoryTests(sandbox);
                await DialogsOffTheUiThreadTests(sandbox);
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

            public Rig(string sandbox, SyncMode mode = SyncMode.TwoWay, bool deletes = false, string localName = "P")
            {
                var top = Path.Combine(sandbox, Guid.NewGuid().ToString("N")[..8]);
                Local = Path.Combine(top, localName);
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

        private static string Names(IEnumerable<string> names) => string.Join(",", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        // ---------------- Reload looks at no disk on the caller's thread ----------------

        /// <summary>
        /// The sync window saves every change through Reload, on the UI thread.
        /// Reload looked at each PC folder (Directory.Exists) and started its
        /// watcher while holding the engine's lock, so a pair on a share that had
        /// gone away froze the window, and the engine waiting on the same lock.
        /// Here the look at the folder is held for as long as the test likes:
        /// Reload must come straight back, and the engine's lock stay free.
        /// </summary>
        private static void ReloadOffTheUiThreadTests(string sandbox)
        {
            var folder = Path.Combine(sandbox, "watched");
            Directory.CreateDirectory(folder);
            using var held = new ManualResetEventSlim(false);
            int asked = 0;
            var monitor = new DriveMonitor(new GoogleDrive(), (_, _) => { }, _ => { })
            {
                FolderThere = path =>
                {
                    Interlocked.Increment(ref asked);
                    held.Wait(TimeSpan.FromSeconds(30));      // a share that does not answer
                    return Directory.Exists(path);
                },
            };
            try
            {
                var pair = new DriveSyncPair { Name = "W", LocalFolder = folder, DriveFolderId = "w", CheckMinutes = 0 };
                var clock = Stopwatch.StartNew();
                monitor.Reload(new[] { pair });
                long took = clock.ElapsedMilliseconds;
                Check("reload: saving a pair whose folder does not answer comes straight back", took < 1500, $"{took} ms");

                var count = Task.Run(() => monitor.Pairs.Count);
                Check("reload: and the engine's lock is free meanwhile", count.Wait(2000) && count.Result == 1);

                var paused = pair.Copy();
                paused.Paused = true;
                clock.Restart();
                monitor.Reload(new[] { paused });
                Check("reload: a second change meanwhile is taken at once too", clock.ElapsedMilliseconds < 1500 && monitor.AllPaused,
                    $"{clock.ElapsedMilliseconds} ms");
                for (int i = 0; i < 100 && Volatile.Read(ref asked) == 0; i++) Thread.Sleep(20);
                Check("reload: the folder is still looked at, on a worker", Volatile.Read(ref asked) > 0);
            }
            finally
            {
                held.Set();
                monitor.Dispose();
            }
        }

        // ---------------- a PC move whose new file is open elsewhere ----------------

        /// <summary>
        /// A file moved on the PC is followed on Drive only once its MD5 agrees
        /// with Drive's copy. When the new file could not be read (open in
        /// another program), the move fell back to the plain plan: with deletes
        /// on, Drive's copy went to the trash straight away, and the file went
        /// up again later as a new one. Now both halves wait, and the next pass
        /// moves it.
        /// </summary>
        private static async Task LockedMoveWaitsTests(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true);
            var first = rig.Write("song.txt", "a song that moves", T0);
            var problem = await rig.Pass();
            Check("locked move: the file went up first", problem == null && rig.DriveTree().ContainsKey("song.txt"), problem);
            var id = rig.Drive.Find(rig.FolderId, "song.txt");

            var moved = Path.Combine(rig.Local, "Album", "song.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
            File.Move(first, moved);
            using (new FileStream(moved, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                problem = await rig.Pass();
                Check("locked move: Drive's copy is not trashed while the moved file cannot be read",
                    rig.Drive.Trashes == 0 && rig.DriveTree().ContainsKey("song.txt"),
                    $"{rig.Drive.Trashes} trashed; Drive has {Names(rig.DriveTree().Keys)}");
            }

            problem = await rig.Pass();
            Check("locked move: once it can be read, the move is followed on Drive",
                problem == null && Names(rig.DriveTree().Keys) == "Album/song.txt" && rig.Drive.Find(rig.FolderId, "Album/song.txt") == id,
                $"{problem}; Drive has {Names(rig.DriveTree().Keys)}");
            Check("locked move: nothing trashed, nothing sent twice", rig.Drive.Trashes == 0 && rig.Drive.Uploads == 1,
                $"{rig.Drive.Trashes} trashed, {rig.Drive.Uploads} uploads");
        }

        /// <summary>
        /// A PC move is followed only when the new file's MD5 matches Drive's
        /// copy. A record with no checksum (written by an older version) skipped
        /// the check, so a file deleted and another of the same size and time
        /// made were taken for one moved: Drive's old file was renamed to the new
        /// name, and the new file's contents never went up. Drive's checksum as
        /// listed is used now.
        /// </summary>
        private static async Task MoveWithoutRecordedChecksumTests(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.TwoWay);
            var old = rig.Write("old.txt", "AAAA", T0);
            await rig.Pass();
            var statePath = Path.Combine(DriveMonitor.StateDirectory, rig.Pair.Id + ".json");
            var node = System.Text.Json.Nodes.JsonNode.Parse(ProtectedFile.ReadAllUtf8(statePath))!.AsObject();
            foreach (var (_, record) in node["Files"]!.AsObject()) record!.AsObject()["RemoteMd5"] = null;
            ProtectedFile.WriteAllUtf8(statePath, Encoding.UTF8.GetBytes(node.ToJsonString()));

            File.Delete(old);
            rig.Write("new.txt", "BBBB", T0);
            var problem = await rig.Pass();
            Equal("no recorded checksum: a different file of the same size and time is sent up, not taken for a move",
                "BBBB", rig.DriveTree().TryGetValue("new.txt", out var text) ? text : "(none) " + problem);
        }

        // ---------------- a conflict copy that has to step aside ----------------

        /// <summary>
        /// The PC's copy of a conflict steps aside under "name (conflict date)".
        /// That name was checked only against the files the scan took part in,
        /// so a hidden file of that name (left out of the sync) made the move
        /// fail, on every pass. And the path was built from the plan's spelling
        /// rather than the folder the file is really in.
        /// </summary>
        private static async Task ConflictAsideTests(string sandbox)
        {
            var nfdFolder = Path.Combine(sandbox, "Café");
            var local = Path.Combine(nfdFolder, "x.txt");
            Equal("conflict: the copy steps aside in the folder the PC's file is in",
                nfdFolder, Path.GetDirectoryName(DriveMonitor.AsidePathFor(local, "Café/x (conflict 2026-10-16).txt"))!);
            Equal("conflict: under the conflict name's own file name", "x (conflict 2026-10-16).txt",
                Path.GetFileName(DriveMonitor.AsidePathFor(local, "Café/x (conflict 2026-10-16).txt")));

            using var rig = new Rig(sandbox, SyncMode.TwoWay);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            rig.Write("x.txt", "pc", T0);
            rig.Drive.AddFile("x.txt", Encoding.UTF8.GetBytes("drive copy"), T0.AddHours(1), rig.FolderId);
            var hidden = rig.Write($"x (conflict {stamp}).txt", "somebody's hidden file", T0);
            File.SetAttributes(hidden, FileAttributes.System);

            var problem = await rig.Pass();
            if (DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) != stamp) return;   // midnight
            var second = Path.Combine(rig.Local, $"x (conflict {stamp} 2).txt");
            Check("conflict: a name a hidden file already has is passed over, not failed on every pass",
                problem == null && File.Exists(second) && File.ReadAllText(second) == "pc", problem);
            Check("conflict: the hidden file is untouched", File.ReadAllText(hidden) == "somebody's hidden file");
            Check("conflict: and Drive's copy came down under the name",
                File.ReadAllText(Path.Combine(rig.Local, "x.txt")) == "drive copy");
        }

        // ---------------- a replacement whose Drive file has gone ----------------

        /// <summary>
        /// An edit on the PC updates Drive's file in place. When that file had
        /// gone (404) by the time the bytes went, a new file was sent instead,
        /// clearly: an answer lost on the way back then failed the file (Drive
        /// would not say whether it had landed), or could leave it twice. It is
        /// sent once now, as every new file is: Drive is asked again, and it goes.
        /// </summary>
        private static async Task ReplacementGoneTests(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.TwoWay);
            rig.Write("notes.txt", "the first words", T0);
            await rig.Pass();
            rig.Write("notes.txt", "the first words, and more after them", T0.AddHours(1));

            var gone = FakeDrive.ErrorBody(404, "notFound", "File not found");
            rig.Drive.AddFault(new FakeDrive.Fault { Kind = "multipart", Subject = s => s == "notes.txt", Status = 404, Body = gone });
            rig.Drive.AddFault(new FakeDrive.Fault { Kind = "multipart", Subject = s => s == "notes.txt", Status = 0 });
            rig.Drive.AddFault(new FakeDrive.Fault { Kind = "lookup", Times = 4, Status = 400, Body = FakeDrive.ErrorBody(400, "badRequest", "no") });

            var problem = await rig.Pass();
            Check("replacement gone: Drive would not say whether the new file landed, and it was asked again rather than failed",
                problem == null, problem);
            Equal("replacement gone: the PC's edit is in Drive", "the first words, and more after them",
                rig.DriveTree().TryGetValue("notes.txt", out var text) ? text : "(none)");
            Check("replacement gone: once", rig.Drive.Duplicates(rig.FolderId).Count == 0,
                string.Join(", ", rig.Drive.Duplicates(rig.FolderId)));
        }

        /// <summary>
        /// When a replacement became a new file, the old Drive copy was trashed
        /// at once, without asking what it held: an edit made in Drive while the
        /// upload ran (an hour, for a big file) went to the trash. It is asked
        /// first now, and left alone when it changed.
        /// </summary>
        private static async Task ReplacedCopyRecheckedTests(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.TwoWay);
            rig.Write("plan.txt", "version one", T0);
            await rig.Pass();
            var oldId = rig.Drive.Find(rig.FolderId, "plan.txt")!;
            rig.Write("plan.txt", "version two, from the PC", T0.AddHours(1));

            rig.Drive.AddFault(new FakeDrive.Fault
            {
                Kind = "multipart", Subject = s => s == "plan.txt", Status = 404,
                Body = FakeDrive.ErrorBody(404, "notFound", "File not found"),
            });
            int sends = 0;
            rig.Drive.BeforeAnswer = (kind, subject) =>
            {
                // The second send is the new file: meanwhile somebody edits the
                // old copy on the website.
                if (kind == "multipart" && subject == "plan.txt" && ++sends == 2)
                    rig.Drive.Edit(oldId, Encoding.UTF8.GetBytes("edited on the website"), T0.AddHours(2));
            };
            var problem = await rig.Pass();
            rig.Drive.BeforeAnswer = null;
            var old = rig.Drive.Get(oldId);
            Check("replaced copy: an edit made in Drive during the upload is not trashed",
                rig.Drive.Trashes == 0 && old != null && !old.Trashed && Encoding.UTF8.GetString(old.Data) == "edited on the website",
                $"{problem}; {rig.Drive.Trashes} trashed");
        }

        // ---------------- a folder the feed named in Drive's spelling ----------------

        /// <summary>
        /// The PC folder "Café" written with the accent as two characters (as a
        /// Mac writes it), Drive's with one: one folder to the sync. A change in
        /// Drive marks the folder to be looked at on the PC under Drive's
        /// spelling, and the look took that spelling to disk, found nothing, and
        /// read the folder as gone: its files as deleted on the PC, fetched
        /// again into ".partial" files and deferred, on every pass.
        /// </summary>
        private static async Task FeedFolderSpelledByDriveTests(string sandbox)
        {
            if (UsnJournal.Where(sandbox) == null) { Console.WriteLine("  (no change journal here: feed spelling test skipped)"); return; }
            using var rig = new Rig(sandbox, SyncMode.TwoWay);
            var cafeOnDrive = rig.Drive.AddFolder("Café", rig.FolderId);
            rig.Drive.AddFile("a.txt", Encoding.UTF8.GetBytes("in both"), T0, cafeOnDrive);
            rig.Write("Café/a.txt", "in both", T0);

            var problem = await rig.Pass();
            Check("feed spelling: the first pass takes the two folders for one", problem == null &&
                Directory.GetDirectories(rig.Local).Length == 1 && rig.Drive.Uploads == 0 && rig.Drive.MediaReads == 0,
                $"{problem}; {Names(Directory.GetDirectories(rig.Local).Select(Path.GetFileName)!)}");

            rig.Drive.AddFile("b.txt", Encoding.UTF8.GetBytes("new in Drive"), T0, cafeOnDrive);
            problem = await rig.Pass();
            var partials = Directory.EnumerateFiles(rig.Local, "*.partial", SearchOption.AllDirectories).ToList();
            Check("feed spelling: the PC folder named by the feed is found as the PC spells it, its files not taken for deleted",
                problem == null && partials.Count == 0, $"{problem}; {string.Join(", ", partials.Select(Path.GetFileName))}");
            Check("feed spelling: the new Drive file came down into the PC's own folder",
                File.Exists(Path.Combine(rig.Local, "Café", "b.txt")) && Directory.GetDirectories(rig.Local).Length == 1);

            int reads = rig.Drive.MediaReads;
            await rig.Pass();
            Equal("feed spelling: and the next pass fetches nothing again", reads.ToString(), rig.Drive.MediaReads.ToString());
        }

        // ---------------- the change journal and a deep folder ----------------

        /// <summary>
        /// The change journal names each change's folder by id, and its path
        /// came from a 1,024-character buffer. A folder deeper than that had no
        /// path, read as outside the pair, and its changes were not synced until
        /// the daily walk.
        /// </summary>
        private static void DeepJournalFolderTests(string sandbox)
        {
            var root = Path.Combine(sandbox, "deep");
            Directory.CreateDirectory(root);
            if (UsnJournal.Where(root) is not { } before) { Console.WriteLine("  (no change journal here: deep folder test skipped)"); return; }
            var parts = Enumerable.Range(0, 10).Select(i => $"{i:D2}" + new string('d', 118)).ToList();
            var deep = Path.Combine(new[] { root }.Concat(parts).ToArray());
            try { Directory.CreateDirectory(deep); }
            catch (Exception ex) { Console.WriteLine("  (long paths not available: deep folder test skipped, " + ex.Message + ")"); return; }
            before = UsnJournal.Where(root)!;
            File.WriteAllText(Path.Combine(deep, "changed.txt"), "changed deep down");
            var after = UsnJournal.Where(root)!;
            var marks = UsnJournal.Since(root, before.JournalId, before.Next, after.Next);
            var relative = string.Join('/', parts);
            Check("deep folder: a change more than 1,024 characters down is found",
                marks != null && marks.Folders.Contains(relative),
                marks == null ? "no marks" : $"{marks.Folders.Count} folders, deepest {marks.Folders.Select(f => f.Length).DefaultIfEmpty(0).Max()} characters");
        }

        // ---------------- a removed pair's history ----------------

        /// <summary>
        /// Remove deletes a pair's records and history. A pass still running
        /// then wrote its history back when it stopped: the names of the pair's
        /// files, kept for a sync that no longer exists.
        /// </summary>
        private static async Task RemovedPairHistoryTests(string sandbox)
        {
            SyncHistory.Add("discarded", "Uploaded", "x.txt");
            SyncHistory.Discard("discarded");
            SyncHistory.Flush("discarded");
            Check("history: lines let go of are not written", !File.Exists(SyncHistory.PathOf("discarded")));

            using var rig = new Rig(sandbox, SyncMode.UploadOnly);
            rig.Monitor.Reload(new[] { rig.Pair });
            for (int i = 0; i < 4; i++) rig.Write($"f{i}.txt", "x" + i, T0);
            rig.Monitor.KillPoint = what =>
            {
                if (what != "uploaded f0.txt") return;
                rig.Monitor.SetPairs(new List<DriveSyncPair>());
                DriveMonitor.ForgetRemovedPairs(Array.Empty<DriveSyncPair>());
            };
            await rig.Pass();
            rig.Monitor.KillPoint = null;
            Check("history: a pair removed during its pass leaves no history behind",
                !File.Exists(SyncHistory.PathOf(rig.Pair.Id)));
        }

        // ---------------- the folder dialogs ----------------

        /// <summary>
        /// Browse in the add/change dialog looked at the typed folder with
        /// Directory.Exists on the UI thread (a dead share froze the window); the
        /// Drive folder picker disposed its CancellationTokenSource as it closed
        /// (CLAUDE.md: cancel, never dispose); and a second Backspace or Enter
        /// while a folder was loading started a second load, so the folder the
        /// list showed and the one saved could differ.
        /// </summary>
        private static async Task DialogsOffTheUiThreadTests(string sandbox)
        {
            Equal("dialogs: a folder that is there is offered as the start", sandbox, await DrivePairForm.FolderIfThereAsync(sandbox));
            Equal("dialogs: one that is not, is not", "", await DrivePairForm.FolderIfThereAsync(Path.Combine(sandbox, "not here")));
            Equal("dialogs: nothing typed, nothing looked at", "", await DrivePairForm.FolderIfThereAsync("  "));

            var form = Source("DriveMonitorForm.cs");
            Check("dialogs: the configurator's source is there to read", form != null);
            if (form == null) return;
            Check("dialogs: Browse never asks Directory.Exists on the UI thread",
                !form.Contains("Directory.Exists(_local.Text)", StringComparison.Ordinal) &&
                form.Contains("FolderIfThereAsync(", StringComparison.Ordinal));
            Check("dialogs: the picker cancels its token source and never disposes it",
                !form.Contains("_cancel.Dispose()", StringComparison.Ordinal));
            int keys = form.IndexOf("_list.KeyDown += async", StringComparison.Ordinal);
            int enter = keys < 0 ? -1 : form.IndexOf("Keys.Enter)", keys, StringComparison.Ordinal);
            Check("dialogs: Enter and Backspace wait while a folder is loading",
                keys >= 0 && enter > keys && form[keys..enter].Contains("_loading", StringComparison.Ordinal));
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }

    // ---- merged from Sweep500SyncTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

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
                await SharedLookOutOfStepTests(sandbox);
                await EditDuringDailyLimitTests(sandbox);
                await RenamedOnDriveTests(sandbox);
                await FreeUpSweepStopsTests(sandbox);
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

        // ---------------- 3. the shared look using up a change for a pair not due that minute ----------------

        /// <summary>
        /// Clean pairs on one drive share a look at its feed each minute. A pair
        /// that ran early (a PC change) is out of step with the others; when
        /// another pair's minute came first, its look read the change made in
        /// the first pair's folder and moved the feed past it, and the first
        /// pair was skipped every minute after, until QuietMost (15 minutes).
        /// </summary>
        private static async Task SharedLookOutOfStepTests(string sandbox)
        {
            using var rig = new Rig(sandbox);
            var a = rig.Pair;
            var b = rig.AddPair("B", SyncMode.TwoWay);
            a.CheckMinutes = b.CheckMinutes = 1;
            rig.Write(a, "a.txt", "for A", T0);
            rig.Write(b, "b.txt", "for B", T0);
            var t = DateTime.UtcNow;
            Task<List<DriveSyncPair>> Due(int minutes, params DriveSyncPair[] pairs) =>
                rig.Monitor.DuePairsAsync(rig.Client, pairs, false, t.AddMinutes(minutes), default);

            foreach (var pair in new[] { a, b }) { await rig.Pass(pair); await rig.Pass(pair); }
            await Due(2, a, b);                         // the first look finds where the feed is
            await rig.Pass(a);
            await rig.Pass(b);
            Check("shared look out of step: both pairs are quiet on a quiet minute", (await Due(4, a, b)).Count == 0);

            await rig.Pass(a);                          // A runs early, as a PC change makes it
            rig.Put(a, "new.txt", "made on the phone", T0);
            var onlyB = await Due(6, b);                // only B's minute is up, and its look reads the change
            Check("shared look out of step: the look on B's minute sees the change", onlyB.Count == 1);

            var later = await Due(8, a, b);
            Check("shared look out of step: A, not due when B's look used up its change, still runs on its next minute",
                later.Contains(a), $"due: {string.Join(",", later.Select(p => p.Name))}");
            await rig.Pass(a);
            Check("shared look out of step: and the new Drive file comes down", File.Exists(Path.Combine(a.LocalFolder, "new.txt")));
            await rig.Pass(a);
            await rig.Pass(b);
            Check("shared look out of step: both are quiet again afterwards", (await Due(10, a, b)).Count == 0);
        }

        // ---------------- 4. an edit during a wait for Google ----------------

        /// <summary>
        /// Under Google's daily limit a pass waits an hour at a time. Pause and
        /// Remove ended the wait; an edit (switching to download only so the
        /// downloads could go on) cut the wait, but the pass asked Google again
        /// under the old settings, was refused, and waited another hour, with
        /// every other pair held up behind it.
        /// </summary>
        private static async Task EditDuringDailyLimitTests(string sandbox)
        {
            using var rig = new Rig(sandbox);
            rig.Monitor.WaitScale = 1;
            rig.Monitor.Reload(new[] { rig.Pair });
            rig.Write(rig.Pair, "a.txt", "a", T0);
            rig.Drive.AddFault(new FakeDrive.Fault
            {
                Kind = "list", Times = 3, Status = 403, Body = FakeDrive.ErrorBody(403, "dailyLimitExceeded", "Daily Limit Exceeded"),
            });
            var pass = rig.Pass();
            await Until(() => (rig.Monitor.StatusOf(rig.Pair.Id).Problem ?? "").Contains("daily", StringComparison.OrdinalIgnoreCase), 5000);
            var edited = rig.Pair.Copy();
            edited.Mode = SyncMode.DownloadOnly;
            rig.Monitor.Reload(new[] { edited });
            bool ended = await Task.WhenAny(pass, Task.Delay(5000)) == pass;
            Check("edit while waiting: editing a pair ends its wait for the daily limit, without asking Google again under the old settings",
                ended && rig.Drive.FaultsLeft == 2, $"ended {ended}, {rig.Drive.FaultsLeft} refusals left");
            Check("edit while waiting: and it no longer says it is waiting",
                ended && !(rig.Monitor.StatusOf(rig.Pair.Id).Problem ?? "").Contains("waiting", StringComparison.OrdinalIgnoreCase),
                rig.Monitor.StatusOf(rig.Pair.Id).Problem);
            if (!ended) rig.Monitor.Dispose();
            try { await pass; } catch (OperationCanceledException) { }
        }

        // ---------------- 5. a folder renamed on Drive ----------------

        /// <summary>
        /// A folder renamed on Drive is renamed on the PC too. But the PC walk
        /// had already listed the old name, and the empty-folder step then made
        /// the whole old tree in Drive again, empty, beside the renamed one, for
        /// good. Also when the PC folder could not be renamed whole and its files
        /// were moved one by one, leaving the old folders empty on the PC.
        /// </summary>
        private static async Task RenamedOnDriveTests(string sandbox)
        {
            async Task<Rig> Synced(bool newFileUnder)
            {
                var rig = new Rig(sandbox);
                rig.Write(rig.Pair, "Music/album 000/a.txt", "a song", T0);
                rig.Write(rig.Pair, "Music/album 001/b.txt", "another", T0);
                await rig.Pass();
                await rig.Pass();
                var music = rig.Drive.Find(rig.Pair.DriveFolderId, "Music")!;
                rig.Drive.Get(music)!.Name = "Library";
                rig.Drive.Changed(music);
                if (newFileUnder) rig.Write(rig.Pair, "Music/new.txt", "new on the PC", T0);
                return rig;
            }

            using (var rig = await Synced(newFileUnder: false))
            {
                int creates = rig.Drive.Creates;
                var problem = await rig.Pass();
                Equal("renamed on Drive: the PC folder is renamed too", "Library/album 000/a.txt,Library/album 001/b.txt", Names(rig.PcFiles(rig.Pair)));
                Check("renamed on Drive: and no empty copy of the old folder is made in Drive",
                    rig.Drive.Find(rig.Pair.DriveFolderId, "Music") == null && rig.Drive.Creates == creates,
                    $"{problem}; {rig.Drive.Creates - creates} folders made; Music there: {rig.Drive.Find(rig.Pair.DriveFolderId, "Music") != null}");
            }

            using (var rig = await Synced(newFileUnder: true))
            {
                var problem = await rig.Pass();
                Check("renamed on Drive, moved file by file: the old folders left empty on the PC are not made in Drive again",
                    rig.Drive.Find(rig.Pair.DriveFolderId, "Music/album 000") == null &&
                    rig.Drive.Find(rig.Pair.DriveFolderId, "Music/album 001") == null &&
                    rig.Drive.Find(rig.Pair.DriveFolderId, "Library/album 000/a.txt") != null,
                    $"{problem}; Drive holds {Names(rig.DriveTree(rig.Pair).Keys)}; PC holds {Names(rig.PcFiles(rig.Pair))}");
            }
        }

        // ---------------- 6. Free up's end sweep after an edit, or when the app closes ----------------

        /// <summary>
        /// Free up space's sweep at the end of a pass can take an hour on a big
        /// library. It heard Pause and Remove but not an edit, so unticking Free
        /// up space during it still cleared files off the PC. And closing the app
        /// during it kept the records of files already cleared: turned to
        /// deletes on later, the next pass took them as deleted on the PC and
        /// sent Drive's copies to the trash.
        /// </summary>
        private static async Task FreeUpSweepStopsTests(string sandbox)
        {
            async Task<Rig> Synced()
            {
                var rig = new Rig(sandbox, SyncMode.UploadOnly);
                for (int i = 0; i < 12; i++) rig.Write(rig.Pair, $"book {i:00}.txt", $"book {i}", T0);
                rig.Monitor.Reload(new[] { rig.Pair });
                await rig.Pass();
                rig.Pair.DeleteAfterUpload = true;
                rig.Monitor.Reload(new[] { rig.Pair });
                return rig;
            }

            // The trash is what a record that outlived its file would cost.
            async Task<int> TrashesWithDeletesOn(Rig rig)
            {
                rig.Pair.DeleteAfterUpload = false;
                rig.Pair.CopyDeletes = true;
                rig.Monitor.Reload(new[] { rig.Pair });
                await rig.Pass();
                return rig.Drive.Trashes;
            }

            using (var rig = await Synced())
            {
                int edits = 0;
                rig.Monitor.KillPoint = what =>
                {
                    if (!what.StartsWith("free up: checked, not yet deleted", StringComparison.Ordinal) ||
                        Interlocked.Exchange(ref edits, 1) != 0) return;
                    var kept = rig.Pair.Copy();
                    kept.DeleteAfterUpload = false;
                    rig.Monitor.Reload(new[] { kept });
                };
                await rig.Pass();
                rig.Monitor.KillPoint = null;
                int left = rig.PcFiles(rig.Pair).Count;
                Check("free up sweep: unticking Free up space during the sweep stops it, past the files already in hand",
                    left >= 12 - DriveMonitor.FreeUpLanes && left < 12, $"{left} of 12 left on the PC");
                int trashes = await TrashesWithDeletesOn(rig);
                Check("free up sweep: and the files it did clear leave no records behind", trashes == 0 && rig.DriveTree(rig.Pair).Count == 12,
                    $"{trashes} trashed, Drive holds {rig.DriveTree(rig.Pair).Count}");
            }

            using (var rig = await Synced())
            {
                using var quit = new CancellationTokenSource();
                rig.Monitor.KillPoint = what =>
                {
                    if (what.StartsWith("free up: checked, not yet deleted", StringComparison.Ordinal)) quit.Cancel();
                };
                try { await rig.Monitor.SyncOneAsync(rig.Client, rig.Pair, quit.Token); }
                catch (OperationCanceledException) { }
                rig.Monitor.KillPoint = null;
                int left = rig.PcFiles(rig.Pair).Count;
                int trashes = await TrashesWithDeletesOn(rig);
                Check("free up sweep: closing the app during the sweep keeps no record of a file it cleared",
                    left < 12 && trashes == 0 && rig.DriveTree(rig.Pair).Count == 12,
                    $"{left} of 12 left on the PC, {trashes} trashed, Drive holds {rig.DriveTree(rig.Pair).Count}");
            }
        }
    }

    // ---- merged from Sweep500Sync2Tests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// The 5.0.0 sweep of the folder sync: Keep on Drive only and the sync window.
    /// </summary>
    internal static class Sweep500Sync2Tests
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

            Console.WriteLine("Sweep 5.0.0, the folder sync: Keep on Drive only and the sync window:");
            var previous = Settings.OverrideAppDataDir;
            var sandbox = Path.Combine(Path.GetTempPath(), "en-sync2-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            Settings.OverrideAppDataDir = Path.Combine(sandbox, "appdata");
            try
            {
                await KeepGivesThePassProblem(sandbox);
                await KeepInDownloadOnlySaysWhyNot(sandbox);
                await KeepSaysWhyFilesStayed(sandbox);
                SyncListsAreLists();
                WindowSavesOntoLivePairs(sandbox);
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(sandbox, true); } catch { }
            }
        }

        /// <summary>A pair over a fake Drive, its monitor with no loop: passes run when the test says.</summary>
        private sealed class Rig : IDisposable
        {
            public readonly FakeDrive Drive = new();
            public readonly string Local;
            public readonly string FolderId;
            public readonly DriveSyncPair Pair;
            public readonly DriveClient Client;
            public readonly DriveMonitor Monitor;

            public Rig(string sandbox, SyncMode mode)
            {
                Local = Path.Combine(sandbox, Guid.NewGuid().ToString("N")[..8], "local");
                Directory.CreateDirectory(Local);
                FolderId = Drive.AddFolder("Sync");
                Pair = new DriveSyncPair
                {
                    Name = "Test", LocalFolder = Local, DriveFolderId = FolderId, DriveFolderPath = "My Drive/Sync",
                    Mode = mode, CopyDeletes = false, CheckMinutes = 0,
                };
                Client = new DriveClient(Drive, Fast);
                Monitor = new DriveMonitor(new GoogleDrive(), (_, _) => { }, _ => { })
                {
                    WaitScale = Fast,
                    DiskOfRoot = _ => (1L << 42, 1L << 43, "T:"),
                    RecycleLocal = path => { File.Delete(path); return null; },
                };
            }

            public async Task Pass() => await Monitor.SyncOneAsync(Client, Pair, default);

            public string PathOf(string relative) => Path.Combine(Local, relative.Replace('/', '\\'));

            public void Write(string relative, string text, DateTime modifiedUtc)
            {
                var path = PathOf(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
                File.SetLastWriteTimeUtc(path, modifiedUtc);
            }

            public void Put(string relative, string text, DateTime modifiedUtc)
            {
                var parent = FolderId;
                var parts = relative.Split('/');
                foreach (var folder in parts[..^1])
                    parent = Drive.Find(parent, folder) ?? Drive.AddFolder(folder, parent);
                Drive.AddFile(parts[^1], Encoding.UTF8.GetBytes(text), modifiedUtc, parent);
            }

            public List<string> LiveDriveOnly() => Monitor.Pairs.First(p => p.Id == Pair.Id).DriveOnlyFolders;

            public void Dispose()
            {
                Monitor.Dispose();
                try { Directory.Delete(Path.GetDirectoryName(Local)!, true); } catch { }
            }
        }

        /// <summary>
        /// Bug 1: a pass that went through but could not send a file (Google
        /// refused it) gives that reason, not "try again once it has synced".
        /// </summary>
        private static async Task KeepGivesThePassProblem(string sandbox)
        {
            const string refusal = "The user does not have sufficient permissions for this file.";
            foreach (bool refused in new[] { true, false })
            {
                using var rig = new Rig(sandbox, SyncMode.TwoWay);
                rig.Write("School/one.txt", "one", T0);
                await rig.Pass();
                rig.Monitor.Reload(new[] { rig.Pair });
                rig.Write("School/bad.txt", "refused by Google", T0.AddMinutes(1));
                if (refused)
                    rig.Drive.AddFault(new FakeDrive.Fault
                    {
                        Kind = "multipart", Status = 403, Times = 1000, Subject = s => s.Contains("bad.txt"),
                        Body = "{\"error\":{\"code\":403,\"message\":\"" + refusal + "\",\"errors\":[{\"reason\":\"insufficientFilePermissions\",\"message\":\"" + refusal + "\"}]}}",
                    });
                rig.Monitor.DriveOnlySyncWait = TimeSpan.FromSeconds(20);
                var ask = rig.Monitor.KeepOnDriveOnlyAsync(rig.Pair.Id, "School", default, rig.Client);
                await Task.Delay(300);
                await rig.Pass();
                var outcome = await ask;
                if (refused)
                {
                    Check("keep on Drive only: a file Google refused is not kept", !outcome.Done, outcome.Message);
                    Check("and the message names the file", outcome.Message.Contains("bad.txt"), outcome.Message);
                    Check("and gives Google's reason, from the pass that went through", outcome.Message.Contains("sufficient permissions"), outcome.Message);
                    Check("and does not say try again once it has synced, which never helps", !outcome.Message.Contains("Try again once it has synced"), outcome.Message);
                    Check("and nothing was removed", File.Exists(rig.PathOf("School/bad.txt")) && File.Exists(rig.PathOf("School/one.txt")));
                }
                else
                    Check("control: with nothing refused the same pass sends it and the folder is kept", outcome.Done, outcome.Message);
            }
        }

        /// <summary>
        /// Bug 2: in a download-only pair, a file only on the PC never goes up,
        /// so Keep on Drive only says so at once instead of "try again once it
        /// has synced"; a PC edit still differing after the pass is said too.
        /// </summary>
        private static async Task KeepInDownloadOnlySaysWhyNot(string sandbox)
        {
            using (var rig = new Rig(sandbox, SyncMode.DownloadOnly))
            {
                rig.Put("School/one.txt", "one", T0);
                await rig.Pass();
                Check("download only rig: Drive's file came down", File.Exists(rig.PathOf("School/one.txt")));
                rig.Monitor.Reload(new[] { rig.Pair });
                rig.Monitor.DriveOnlySyncWait = TimeSpan.FromSeconds(20);
                rig.Write("School/notes.txt", "pc only", T0.AddMinutes(1));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var outcome = await rig.Monitor.KeepOnDriveOnlyAsync(rig.Pair.Id, "School", default, rig.Client);
                Check("download only: a file only on the PC is not kept on Drive only", !outcome.Done, outcome.Message);
                Check("and the message names it and says the pair only copies from Drive",
                    outcome.Message.Contains("notes.txt") && outcome.Message.Contains("only copies from Google Drive"), outcome.Message);
                Check("and does not say try again once it has synced", !outcome.Message.Contains("Try again once it has synced"), outcome.Message);
                Check("and it is said at once, without waiting on a pass that can never send it", clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
                Check("and nothing was removed", File.Exists(rig.PathOf("School/one.txt")) && File.Exists(rig.PathOf("School/notes.txt")));
                Check("and the folder is not on the list", rig.LiveDriveOnly().Count == 0);

                Directory.CreateDirectory(rig.PathOf("Empty"));
                var empty = await rig.Monitor.KeepOnDriveOnlyAsync(rig.Pair.Id, "Empty", default, rig.Client);
                Check("download only: an empty folder Drive lacks is said to never go up",
                    !empty.Done && empty.Message.Contains("only copies from Google Drive") && !empty.Message.Contains("Try again"), empty.Message);

                // A PC edit of a file Drive has: the pass runs (it could bring a
                // newer copy down), then the edit is said to never go up.
                File.Delete(rig.PathOf("School/notes.txt"));
                rig.Write("School/one.txt", "ONE", T0.AddMinutes(2));
                var ask = rig.Monitor.KeepOnDriveOnlyAsync(rig.Pair.Id, "School", default, rig.Client);
                await Task.Delay(300);
                await rig.Pass();
                var edited = await ask;
                Check("download only: a PC edit still differing after the pass is said as never going up",
                    !edited.Done && edited.Message.Contains("one.txt") && edited.Message.Contains("different from Google Drive's copy")
                    && !edited.Message.Contains("Try again"), edited.Message);
                Check("and the edit is still on the PC", File.ReadAllText(rig.PathOf("School/one.txt")) == "ONE");
            }
        }

        /// <summary>
        /// Bug 4: the sync window's Synced folders and Status lists and the
        /// History list (all LineList) say they are lists to UIA, not tables.
        /// </summary>
        private static void SyncListsAreLists()
        {
            string role = "", controlType = "";
            var thread = new System.Threading.Thread(() =>
            {
                using var list = new DriveMonitorForm.LineList();
                role = list.AccessibleRole.ToString();
                try
                {
                    _ = list.Handle;
                    var accessible = list.AccessibilityObject;
                    var get = accessible.GetType().GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                        .FirstOrDefault(m => m.Name == "GetPropertyValue" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
                    if (get != null)
                    {
                        var value = get.Invoke(accessible, new[] { Enum.ToObject(get.GetParameters()[0].ParameterType, 30003) });
                        var plain = value?.GetType().GetMethod("ToObject", Type.EmptyTypes)?.Invoke(value, null) ?? value;
                        controlType = plain?.ToString() ?? "";
                    }
                }
                catch (Exception ex) { controlType = "probe failed: " + ex.GetType().Name; }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
            Equal("sync window lists say they are lists, not tables", "List", role);
            if (controlType.Length > 0 && !controlType.StartsWith("probe failed", StringComparison.Ordinal))
                Equal("and UIA hears ControlType List (50008), not Table (50036)", "50008", controlType);
            else
                Console.WriteLine("  (UIA control type not readable here: " + controlType + ")");
        }

        /// <summary>
        /// Bug 5: the sync window saves the person's one change onto the pairs
        /// as the monitor has them, so Pause all from the tray, or the sync's own
        /// pause, made while the window is open is never silently undone.
        /// </summary>
        private static void WindowSavesOntoLivePairs(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.TwoWay);
            var otherFolder = Path.Combine(sandbox, Guid.NewGuid().ToString("N")[..8], "other");
            Directory.CreateDirectory(otherFolder);
            var b = new DriveSyncPair
            {
                Name = "Other", LocalFolder = otherFolder, DriveFolderId = rig.Drive.AddFolder("Other"), DriveFolderPath = "My Drive/Other",
                Mode = SyncMode.TwoWay, CheckMinutes = 0,
            };
            var a = rig.Pair;
            rig.Monitor.SetPairs(new List<DriveSyncPair> { a, b });
            bool Paused(string id) => rig.Monitor.Pairs.First(p => p.Id == id).Paused;

            string? failure = null;
            string pauseText = "", afterResume = "", afterRemove = "";
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                    using var form = new DriveMonitorForm(rig.Monitor.Pairs);
                    _ = form.Handle;
                    var type = typeof(DriveMonitorForm);
                    var list = (DriveMonitorForm.LineList)type.GetField("_list", Any)!.GetValue(form)!;
                    var pause = (System.Windows.Forms.Button)type.GetField("_pause", Any)!.GetValue(form)!;
                    _ = list.Handle;
                    void Call(string method, params object[] args) => type.GetMethod(method, Any)!.Invoke(form, args);

                    // Pause all from the tray while the window is open.
                    rig.Monitor.PauseAll(true);
                    Call("Fill", false);
                    list.SelectedIndex = 0;
                    Call("ShowButtons");
                    pauseText = pause.Text;
                    // The button now offers Resume for the first folder; pressed,
                    // only that folder resumes.
                    Call("PauseOrResume");
                    afterResume = $"{Paused(a.Id)},{Paused(b.Id)}";

                    // The sync pauses the first folder by itself (damaged records);
                    // removing the other from the window must not resume it.
                    var live = rig.Monitor.Pairs;
                    live.First(p => p.Id == a.Id).Paused = true;
                    rig.Monitor.SetPairs(live);
                    list.SelectedIndex = rig.Monitor.Pairs.FindIndex(p => p.Id == b.Id);
                    Call("RemovePair");
                    afterRemove = string.Join(",", rig.Monitor.Pairs.Select(p => $"{p.Name}:{p.Paused}"));
                }
                catch (Exception ex) { failure = ex.ToString(); }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
            Check("sync window rig ran", failure == null, failure);
            Equal("after Pause all from the tray, the window's button offers Resume", "Res&ume", pauseText);
            Equal("pressing it resumes only that folder; the other stays paused", "False,True", afterResume);
            Equal("removing a folder from the window keeps a pause the sync made meanwhile", "Test:True", afterRemove);
        }

        /// <summary>
        /// Bug 3: a file open in another program that refuses deleting (a player
        /// sharing read and write, not delete) stops Keep on Drive only before
        /// anything changes; one opened or deleted after the check is said for
        /// what it is, never as "changed while it was checked".
        /// </summary>
        private static async Task KeepSaysWhyFilesStayed(string sandbox)
        {
            async Task<Rig> Ready()
            {
                var rig = new Rig(sandbox, SyncMode.TwoWay);
                rig.Write("School/one.txt", "one", T0);
                rig.Write("School/two.txt", "two", T0);
                await rig.Pass();
                rig.Monitor.Reload(new[] { rig.Pair });
                rig.Monitor.DriveOnlySyncWait = TimeSpan.FromMilliseconds(300);
                return rig;
            }

            using (var rig = await Ready())
            {
                var one = rig.PathOf("School/one.txt");
                File.SetAttributes(one, FileAttributes.ReadOnly);
                DriveMonitor.DriveOnlyOutcome outcome;
                using (new FileStream(one, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    outcome = await rig.Monitor.KeepOnDriveOnlyAsync(rig.Pair.Id, "School", default, rig.Client);
                Check("keep on Drive only: a file open without delete sharing stops it", !outcome.Done, outcome.Message);
                Check("and says it is open in another program, by name",
                    outcome.Message.Contains("open in another program") && outcome.Message.Contains("one.txt"), outcome.Message);
                Check("and the folder is not on the list, so it can be tried again", rig.LiveDriveOnly().Count == 0);
                Check("and every file is still there, read-only kept",
                    File.Exists(rig.PathOf("School/two.txt")) && (File.GetAttributes(one) & FileAttributes.ReadOnly) != 0);
                File.SetAttributes(one, FileAttributes.Normal);
            }

            using (var rig = await Ready())
            {
                DriveMonitor.DriveOnlyOutcome outcome;
                using (new FileStream(rig.PathOf("School/one.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    outcome = await rig.Monitor.KeepOnDriveOnlyAsync(rig.Pair.Id, "School", default, rig.Client);
                Check("control: a handle that shares delete does not stop it", outcome.Done, outcome.Message);
            }

            using (var rig = await Ready())
            {
                var one = rig.PathOf("School/one.txt");
                File.SetAttributes(one, FileAttributes.ReadOnly);
                FileStream? late = null;
                rig.Monitor.BeforeClearingForTests = _ => late = new FileStream(one, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                DriveMonitor.DriveOnlyOutcome outcome;
                try { outcome = await rig.Monitor.KeepOnDriveOnlyAsync(rig.Pair.Id, "School", default, rig.Client); }
                finally { late?.Dispose(); }
                Check("a file opened after the check is said as open in another program",
                    outcome.Message.Contains("open in another program") && outcome.Message.Contains("one.txt"), outcome.Message);
                Check("not as changed while it was checked", !outcome.Message.Contains("changed while"), outcome.Message);
                Check("and it keeps its read-only mark", File.Exists(one) && (File.GetAttributes(one) & FileAttributes.ReadOnly) != 0);
                Check("and the rest was cleared", !File.Exists(rig.PathOf("School/two.txt")));
                File.SetAttributes(one, FileAttributes.Normal);
            }

            using (var rig = await Ready())
            {
                rig.Monitor.BeforeClearingForTests = _ => File.Delete(rig.PathOf("School/one.txt"));
                var outcome = await rig.Monitor.KeepOnDriveOnlyAsync(rig.Pair.Id, "School", default, rig.Client);
                Equal("a file deleted after the check leaves nothing to say", "School is on Google Drive only now, and was cleared off this PC", outcome.Message);
            }

            using (var rig = await Ready())
            {
                rig.Monitor.BeforeClearingForTests = _ => rig.Write("School/one.txt", "ONE", T0.AddMinutes(3));
                var outcome = await rig.Monitor.KeepOnDriveOnlyAsync(rig.Pair.Id, "School", default, rig.Client);
                Check("a file really changed after the check is still said as changed, by name",
                    outcome.Message.Contains("changed while") && outcome.Message.Contains("one.txt"), outcome.Message);
            }
        }
    }

    // ---- merged from Sweep500Sync3Tests.cs (7.1.0: tests are filed by feature, not by fix round) ----

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
                await InBatches(() => FolderDeletedOnDriveTests(sandbox, "batched"));
                await FolderDeletedOnDriveTests(sandbox, "small");
                await InBatches(() => FreeUpDriveCountTests(sandbox));
                await SharedDriveQuotaTests(sandbox);
                await InBatches(() => MovedOutOfPairTests(sandbox, files: 1));
                await InBatches(() => MovedOutOfPairTests(sandbox, files: 70));
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

        // ---------------- a folder deleted in Google Drive ----------------

        /// <summary>
        /// Two-way with deletes: the person trashes the folder Old in Google
        /// Drive. Its files were recycled on the PC, but a batched pass made Old
        /// again in Drive at once (its "Drive had it before this pass" was only
        /// the root on a full listing), and both kinds of pair left the empty PC
        /// folder, so the next whole walk made it again. Now the delete is
        /// mirrored: the folder goes from the PC and stays gone from Drive.
        /// </summary>
        private static async Task FolderDeletedOnDriveTests(string sandbox, string kind)
        {
            using var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true);
            rig.Write("Old/a.txt", "a", T0);
            rig.Write("Keep/k.txt", "k", T0);
            rig.Write("Kept/Inner/edited.txt", "e", T0);
            var problem = await rig.Pass();
            await rig.Pass();
            Check($"folder deleted on Drive ({kind}): the pair settled first", problem == null && rig.DriveTree().ContainsKey("Old/a.txt"), problem);

            rig.Drive.Trash(rig.Drive.Find(rig.FolderId, "Old")!);
            problem = await rig.Pass();
            Check($"folder deleted on Drive ({kind}): it is not made again in Drive", rig.Drive.Find(rig.FolderId, "Old") == null, problem);
            Check($"folder deleted on Drive ({kind}): its file and folder go from the PC too",
                !File.Exists(rig.LocalPath("Old/a.txt")) && !Directory.Exists(rig.LocalPath("Old")));

            var every = DriveMonitor.FullListEvery;
            DriveMonitor.FullListEvery = TimeSpan.Zero;
            try { problem = await rig.Pass(); }
            finally { DriveMonitor.FullListEvery = every; }
            Check($"folder deleted on Drive ({kind}): and the next whole walk does not bring it back", rig.Drive.Find(rig.FolderId, "Old") == null, problem);
            Check($"folder deleted on Drive ({kind}): folders Drive still has stay on the PC",
                File.Exists(rig.LocalPath("Keep/k.txt")) && File.Exists(rig.LocalPath("Kept/Inner/edited.txt")));
        }

        // ---------------- free up: the Google Drive count ----------------

        /// <summary>
        /// A free up pair in batches: freed files have no records, so each time
        /// a folder was listed again its Drive files were added to the saved
        /// count and nothing taken off. "On Google Drive" grew every pass. Now
        /// it is what the last whole listing found plus what went up since.
        /// </summary>
        private static async Task FreeUpDriveCountTests(string sandbox)
        {
            using var rig = new Rig(sandbox, SyncMode.UploadOnly);
            rig.Pair.DeleteAfterUpload = true;
            for (int f = 0; f < 6; f++)
                for (int i = 0; i < 3; i++)
                    rig.Write($"F{f:D2}/s{i}.txt", $"{f}-{i}", T0);
            await rig.Pass();
            await rig.Pass();
            await rig.Pass();
            Check("free up count: everything went up and was cleared off the PC",
                rig.DriveTree().Count == 18 && !File.Exists(rig.LocalPath("F00/s0.txt")), $"{rig.DriveTree().Count} on Drive");
            Equal("free up count: On Google Drive says 18", "18", rig.Monitor.StatusOf(rig.Pair.Id).DriveFound.ToString());

            rig.Write("F00/new.txt", "new", T0);
            await rig.Pass();
            await rig.Pass();
            await rig.Pass();
            Check("free up count: the new file went up", rig.DriveTree().Count == 19, $"{rig.DriveTree().Count} on Drive");
            Equal("free up count: and On Google Drive says 19, not the folder counted again", "19",
                rig.Monitor.StatusOf(rig.Pair.Id).DriveFound.ToString());

            var every = DriveMonitor.FullListEvery;
            DriveMonitor.FullListEvery = TimeSpan.Zero;
            try { await rig.Pass(); }
            finally { DriveMonitor.FullListEvery = every; }
            Equal("free up count: a whole listing agrees", "19", rig.Monitor.StatusOf(rig.Pair.Id).DriveFound.ToString());
        }

        // ---------------- a shared drive and the person's own quota ----------------

        /// <summary>
        /// Files in a shared drive count against the organisation's storage,
        /// not the person's own. With their own Drive full, every upload to a
        /// shared-drive pair stopped as "Google Drive is full", in the careful
        /// loop and the fast lanes alike. A My Drive pair still stops.
        /// </summary>
        private static async Task SharedDriveQuotaTests(string sandbox)
        {
            async Task<(string? Problem, int OnDrive)> Run(string drivePath, int files, bool batched = false, bool sharedById = false)
            {
                var account = new GoogleDrive();
                account.SetQuotaForTests(1000, 1000);
                (string?, int) result = default;
                async Task Go()
                {
                    using var rig = new Rig(sandbox, SyncMode.UploadOnly, drivePath: drivePath, drive: account,
                        wrap: sharedById ? d => new SharedDriveHandler(d) : null);
                    for (int i = 0; i < files; i++) rig.Write($"f{i:D3}.txt", "ten bytes!", T0);
                    var problem = await rig.Pass();
                    result = (problem, rig.DriveTree().Count);
                }
                if (batched) await InBatches(Go); else await Go();
                return result;
            }

            var (problem, onDrive) = await Run("Shared drives/Team", 1);
            Check("shared drive quota: a full My Drive does not stop uploads to a shared drive", problem == null && onDrive == 1, problem);
            (problem, onDrive) = await Run("Shared drives/Team", 70);
            Check("shared drive quota: nor the fast lane's", problem == null && onDrive == 70, $"{onDrive} up; {problem}");
            (problem, onDrive) = await Run("Team folder", 1, batched: true, sharedById: true);
            Check("shared drive quota: a shared drive known by its drive id, not its path, too", problem == null && onDrive == 1, problem);
            (problem, onDrive) = await Run("My Drive/Music", 1);
            Check("shared drive quota: a My Drive pair still stops when My Drive is full",
                problem != null && problem.StartsWith("paused: Google Drive is full", StringComparison.Ordinal) && onDrive == 0, problem);
        }

        // ---------------- a file moved out of the pair on Drive, edited on the PC ----------------

        /// <summary>
        /// A batched pair on the feed. The person moves Music/t000.txt out of
        /// the synced folder on drive.google.com; the feed marks only where it
        /// went, so Music is not listed again. Then the PC copy is edited: the
        /// edit was written into the file at its new home outside the pair,
        /// and Music never got it. Now the in-place update checks the file's
        /// folder first, as the trash does, and the edit goes up into Music.
        /// With many files the fast lane makes the same check.
        /// </summary>
        private static async Task MovedOutOfPairTests(string sandbox, int files)
        {
            var label = $"moved out of the pair ({files} edited): ";
            using var rig = new Rig(sandbox, SyncMode.TwoWay, deletes: true);
            for (int i = 0; i < files; i++) rig.Write($"Music/t{i:D3}.txt", "old " + i, T0);
            rig.Write("Other/o.txt", "o", T0);
            var problem = await rig.Pass();
            await rig.Pass();
            Check(label + "the pair settled first", problem == null && rig.DriveTree().ContainsKey("Music/t000.txt"), problem);

            var outside = rig.Drive.AddFolder("Elsewhere");
            var id = rig.Drive.Find(rig.FolderId, "Music/t000.txt")!;
            rig.Drive.MoveTo(id, outside);
            rig.Drive.Changed(id);
            await rig.Pass();

            for (int i = 0; i < files; i++) rig.Write($"Music/t{i:D3}.txt", "new edit " + i, T0.AddHours(1));
            await rig.Pass();
            await rig.Pass();
            await rig.Pass();

            var moved = rig.Drive.Get(id)!;
            Equal(label + "the file outside the pair keeps what it held", "old 0", Encoding.UTF8.GetString(moved.Data));
            Check(label + "and stays where the person put it", moved.Parent == outside && !moved.Trashed);
            Check(label + "the PC's edit is in Music on Drive",
                rig.DriveTree().TryGetValue("Music/t000.txt", out var text) && text == "new edit 0", text);
            Check(label + "and the PC copy is still there", File.Exists(rig.LocalPath("Music/t000.txt")));
        }

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

    // ---- merged from Perf522SyncTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

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
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.System);
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
            try { return dir == null ? "" : SourceFiles.Read(Path.Combine(dir.FullName, "src", name)); }
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
