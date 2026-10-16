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
            File.SetAttributes(hidden, FileAttributes.Hidden);

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
        /// plainly: an answer lost on the way back then failed the file (Drive
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
}
