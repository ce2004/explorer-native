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
    /// The folder monitor against the real Google Drive account, at scale:
    /// EXPLORERNATIVE_SELFTEST_ONLY=livesync, EXPLORERNATIVE_LIVESYNC_FILES=N
    /// (default 20,000), EXPLORERNATIVE_LIVESYNC_ROOT=an existing local test
    /// folder and EXPLORERNATIVE_LIVESYNC_DRIVEID=its Drive folder, to carry on
    /// with a run already made.
    ///
    /// Morgan's case: a big folder of tiny files that is already in My Drive.
    /// 1. The files are made locally and uploaded by the monitor itself.
    /// 2. A brand new pair (no records at all) is pointed at the same two
    ///    folders, as somebody who turned Google Drive for desktop off and set
    ///    this up instead. It must upload nothing and adopt everything.
    /// 3. New files arrive and a pass is cut off part way, then the app
    ///    "restarts": still exactly one copy of everything.
    /// After each step every file in the Drive folder is listed and counted,
    /// duplicates included. Its own settings folder, so the real app's sync
    /// records are never touched. The Drive folder is kept for inspection.
    /// </summary>
    internal static class LiveSyncProbe
    {
        private static int _failed;

        private static void Check(string what, bool ok, string detail = "")
        {
            if (!ok) _failed++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(ok || detail.Length == 0 ? "" : "  -> " + detail)}");
        }

        public static async Task<int> Run()
        {
            var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExplorerNative");
            var creds = GoogleAuth.Credentials(real);
            if (creds == null) { Console.WriteLine("no client credentials"); return 1; }
            var auth = new GoogleAuth(creds.Value.ClientId, creds.Value.ClientSecret,
                Path.Combine(real, $"google-drive-token-v{GoogleAuth.ScopeVersion}.dat")) { AllowConsent = false };
            var client = new DriveClient(auth);

            // EXPLORERNATIVE_LIVESYNC_TRASH=1: the test folder goes to the Drive
            // trash (one request, recoverable) and the local copy is deleted.
            if (Environment.GetEnvironmentVariable("EXPLORERNATIVE_LIVESYNC_TRASH") == "1")
            {
                var id = Environment.GetEnvironmentVariable("EXPLORERNATIVE_LIVESYNC_DRIVEID");
                var dir = Environment.GetEnvironmentVariable("EXPLORERNATIVE_LIVESYNC_ROOT");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(dir)) { Console.WriteLine("need ROOT and DRIVEID"); return 1; }
                using var quick = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await client.Trash(id, quick.Token);
                Console.WriteLine($"Drive folder {id} moved to the Google Drive trash");
                Directory.Delete(dir, recursive: true);
                Console.WriteLine($"Deleted {dir}");
                return 0;
            }

            // EXPLORERNATIVE_LIVESYNC_DUPS=1: every name held twice in one folder of
            // the test Drive folder, with each copy's id, size and time, and the
            // tag a create carries, to see how a second copy came about.
            if (Environment.GetEnvironmentVariable("EXPLORERNATIVE_LIVESYNC_DUPS") == "1")
            {
                var id = Environment.GetEnvironmentVariable("EXPLORERNATIVE_LIVESYNC_DRIVEID");
                if (string.IsNullOrEmpty(id)) { Console.WriteLine("need DRIVEID"); return 1; }
                using var quick = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var pending = new Stack<(string Id, string Path)>();
                pending.Push((id, ""));
                while (pending.Count > 0)
                {
                    var (folderId, path) = pending.Pop();
                    var children = await client.ListChildren(folderId, quick.Token, max: int.MaxValue);
                    foreach (var c in children.Where(c => c.IsFolder)) pending.Push((c.Id, path + c.Name + "/"));
                    foreach (var g in children.GroupBy(c => (c.IsFolder, c.Name)).Where(g => g.Count() > 1))
                    {
                        Console.WriteLine($"twice: {path}{g.Key.Name}{(g.Key.IsFolder ? "/" : "")}");
                        foreach (var c in g)
                        {
                            var json = await client.MadeWithTagProbe(c.Id, quick.Token);
                            Console.WriteLine($"    {c.Id}  size {c.Size}  modified {c.Modified:O}  md5 {c.Md5}  {json}");
                        }
                    }
                }
                return 0;
            }

            // EXPLORERNATIVE_LIVESYNC_ADDPAIR=1: a small test pair is added to the
            // RUNNING app's own settings (then ReloadSettingsCommand), so its
            // sync configurator can be watched doing a real sync.
            // EXPLORERNATIVE_LIVESYNC_REMOVEPAIR=1: that pair is taken out again
            // and its two folders cleaned up.
            const string watchName = "Watch test";
            if (Environment.GetEnvironmentVariable("EXPLORERNATIVE_LIVESYNC_ADDPAIR") == "1")
            {
                using var quick = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                int files = int.TryParse(Environment.GetEnvironmentVariable("EXPLORERNATIVE_LIVESYNC_FILES"), out var f) ? f : 3000;
                var tag = Guid.NewGuid().ToString("N")[..8];
                var watchLocal = Path.Combine(Path.GetTempPath(), "en-watch-" + tag, "D");
                MakeFiles(watchLocal, files);
                var folderName = "zz-watch-" + tag;
                var id = await client.CreateFolder(folderName, await client.RootId(quick.Token), quick.Token);
                var settings = Settings.Load();
                settings.DriveSyncPairs.RemoveAll(p => p.Name == watchName);
                settings.DriveSyncPairs.Add(new DriveSyncPair
                {
                    Name = watchName, LocalFolder = watchLocal, DriveFolderId = id, DriveFolderPath = "My Drive/" + folderName,
                    Mode = SyncMode.TwoWay, CopyDeletes = false, CheckMinutes = 1, IncludeSubfolders = true,
                });
                settings.Save();
                Console.WriteLine($"added pair: {watchLocal} <-> My Drive/{folderName} ({id}), {CountLocal(watchLocal):N0} files");
                Console.WriteLine("reload sent: " + SingleInstance.SendToRunningInstance(SingleInstance.ReloadSettingsCommand));
                return 0;
            }
            if (Environment.GetEnvironmentVariable("EXPLORERNATIVE_LIVESYNC_REMOVEPAIR") == "1")
            {
                using var quick = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var settings = Settings.Load();
                foreach (var pair in settings.DriveSyncPairs.Where(p => p.Name == watchName).ToList())
                {
                    settings.DriveSyncPairs.Remove(pair);
                    try { await client.Trash(pair.DriveFolderId, quick.Token); Console.WriteLine("trashed " + pair.DriveFolderPath); }
                    catch (Exception ex) { Console.WriteLine("could not trash " + pair.DriveFolderPath + ": " + ex.Message); }
                    var top = Path.GetDirectoryName(pair.LocalFolder)!;
                    if (Path.GetFileName(top).StartsWith("en-watch-", StringComparison.Ordinal))
                    {
                        foreach (var file in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories))
                            File.SetAttributes(file, FileAttributes.Normal);
                        Directory.Delete(top, recursive: true);
                        Console.WriteLine("deleted " + top);
                    }
                }
                settings.Save();
                Console.WriteLine("reload sent: " + SingleInstance.SendToRunningInstance(SingleInstance.ReloadSettingsCommand));
                return 0;
            }

            int count = int.TryParse(Environment.GetEnvironmentVariable("EXPLORERNATIVE_LIVESYNC_FILES"), out var n) ? n : 20_000;
            var work = Environment.GetEnvironmentVariable("EXPLORERNATIVE_LIVESYNC_ROOT") ??
                       Path.Combine(Path.GetTempPath(), "en-livesync-" + Guid.NewGuid().ToString("N")[..8]);
            var local = Path.Combine(work, "D");
            var sandbox = Path.Combine(work, "appdata");
            Directory.CreateDirectory(sandbox);
            var previous = Settings.OverrideAppDataDir;
            Settings.OverrideAppDataDir = sandbox;
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromDays(30));
                var token = stop.Token;

                var driveId = Environment.GetEnvironmentVariable("EXPLORERNATIVE_LIVESYNC_DRIVEID");
                var driveName = "zz-livesync-" + Path.GetFileName(work);
                if (string.IsNullOrEmpty(driveId))
                    driveId = await client.CreateFolder(driveName, await client.RootId(token), token);
                Console.WriteLine($"Local folder: {local}");
                Console.WriteLine($"Drive folder: My Drive/{driveName} (id {driveId})");
                Console.WriteLine($"To carry on later: EXPLORERNATIVE_LIVESYNC_ROOT={work} EXPLORERNATIVE_LIVESYNC_DRIVEID={driveId}");

                // ---- the files: tiny, a thousand to a folder, a few awkward names ----
                var clock = Stopwatch.StartNew();
                MakeFiles(local, count);
                // Counted, not remembered: a folder reused from an earlier run
                // also holds that run's late files.
                int made = CountLocal(local);
                Console.WriteLine($"{made:N0} files on the PC side ({clock.Elapsed.TotalSeconds:F0}s to make)");

                // ---- 1. upload them all with the monitor ----
                var first = Pair(local, driveId);
                var upload = await PassUntilSettled(client, first, "1. first upload", token);
                var (files, dups, _) = await CountDrive(client, driveId, token);
                Console.WriteLine($"Drive now holds {files:N0} files ({dups:N0} duplicated names)");
                Check("every file the PC has is on Drive once", files == made, $"{files:N0} on Drive, {made:N0} on the PC");
                Check("and no name is on Drive twice", dups == 0, $"{dups:N0} duplicated");
                Check("the .gdoc placeholder was not uploaded", !await OnDrive(client, driveId, "notes.gdoc", token));

                // ---- 2. Morgan: a brand new pair over what is already there ----
                var fresh = Pair(local, driveId);
                var adopt = await PassUntilSettled(client, fresh, "2. new pair over a folder already on Drive", token);
                Check("a new pair over matching folders uploads nothing", adopt.Uploaded == 0, $"{adopt.Uploaded:N0} uploaded");
                Check("and downloads nothing", adopt.Downloaded == 0, $"{adopt.Downloaded:N0} downloaded");
                Check("and finds every file already on both sides", adopt.Matched == made, $"{adopt.Matched:N0} matched of {made:N0}");
                (files, dups, _) = await CountDrive(client, driveId, token);
                Check("Drive still holds exactly one of each", files == made && dups == 0, $"{files:N0} files, {dups:N0} duplicated");

                // ---- 3. new files, a pass cut off part way, the app restarting ----
                int more = Math.Max(200, count / 50);
                var lateFolder = "late " + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                for (int i = 0; i < more; i++)
                {
                    // A folder of its own each run, so these are always new files:
                    // creates, which are what a cut-off pass can leave unknown.
                    var path = Path.Combine(local, lateFolder, $"late{i:D6}.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "late " + i);
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
                }
                using (var cut = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var monitor = NewMonitor();
                    var running = monitor.SyncOneAsync(client, fresh, cut.Token);
                    // Cut off once some of the new files are going up.
                    for (int i = 0; i < 600 && monitor.StatusOf(fresh.Id).Uploaded < more / 4; i++) await Task.Delay(100, token);
                    Console.WriteLine($"  cutting the pass off after {monitor.StatusOf(fresh.Id).Uploaded:N0} of {more:N0} new files");
                    cut.Cancel();
                    try { await running; } catch (OperationCanceledException) { }
                    monitor.Dispose();
                }
                var resumed = await PassUntilSettled(client, fresh, "3. after a pass was cut off", token);
                (files, dups, _) = await CountDrive(client, driveId, token);
                int expected = CountLocal(local);
                Check("after the cut and restart every file is on Drive once",
                    files == expected && dups == 0, $"{files:N0} files (expected {expected:N0}), {dups:N0} duplicated");
                Check("and nothing came back down", resumed.Downloaded == 0, $"{resumed.Downloaded:N0} downloaded");

                // ---- 4. nothing left to do ----
                var idle = await PassUntilSettled(client, fresh, "4. a pass with nothing changed", token);
                Check("a pass with nothing changed does nothing",
                    idle.Uploaded + idle.Downloaded + idle.Deleted == 0 && idle.Matched == expected,
                    $"{idle.Uploaded} up, {idle.Downloaded} down, {idle.Deleted} deleted, {idle.Matched:N0} matched");

                // ---- 5. the preview a new pair would show ----
                var lookClock = Stopwatch.StartNew();
                var preview = await DriveMonitor.PreviewAsync(client, Pair(local, driveId), token);
                Console.WriteLine($"\n5. preview ({lookClock.Elapsed.TotalSeconds:F1}s):\n    " +
                                  DriveMonitor.Describe(preview).Replace(Environment.NewLine, "\n    "));
                Check("the preview finds nothing to upload or download",
                    preview.Uploads == 0 && preview.Downloads == 0 && preview.BothKept == 0 && preview.Deletes == 0,
                    $"up {preview.Uploads}, down {preview.Downloads}, both {preview.BothKept}, delete {preview.Deletes}");
                Check("and counts every file on both sides", preview.OnPc == expected && preview.OnDrive == expected,
                    $"PC {preview.OnPc:N0}, Drive {preview.OnDrive:N0}, expected {expected:N0}");

                // ---- 6. a folder of 1,000 renamed on the PC ----
                var box = Directory.GetDirectories(Path.Combine(local, "part000")).OrderBy(d => d, StringComparer.Ordinal)
                    .FirstOrDefault(d => Directory.GetFiles(d).Length == 1000);
                if (box != null)
                {
                    var renamedBox = box + " renamed " + DateTime.Now.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                    Directory.Move(box, renamedBox);
                    var (renamed, took) = await OnePass(client, fresh, "6. a folder of 1,000 renamed on the PC", token);
                    (files, dups, _) = await CountDrive(client, driveId, token);
                    Check("a renamed folder of 1,000 uploads nothing", renamed.Uploaded == 0, $"{renamed.Uploaded:N0} uploaded");
                    Check("and moves all 1,000 by renaming Drive's folder", renamed.Moved == 1000, $"{renamed.Moved:N0} moved");
                    Check("and Drive still holds exactly one of each", files == expected && dups == 0, $"{files:N0} files, {dups:N0} duplicated");
                    Console.WriteLine($"    (folder rename of 1,000 files took {took.TotalSeconds:F1}s)");
                }
                else Check("found a folder of 1,000 to rename", false, "none in part000");

                // ---- 7. one edit, then nothing, on the changes feed ----
                var edited = Directory.EnumerateFiles(local, "f*.txt", SearchOption.AllDirectories).First();
                File.AppendAllText(edited, " edited");
                File.SetLastWriteTimeUtc(edited, DateTime.UtcNow.AddMinutes(-1));
                var (oneEdit, editTook) = await OnePass(client, fresh, "7a. one PC edit, on the changes feed", token);
                Check("one edit: exactly one file goes up, nothing comes down",
                    oneEdit.Uploaded == 1 && oneEdit.Downloaded == 0 && oneEdit.Moved == 0, $"{oneEdit.Uploaded} up, {oneEdit.Downloaded} down");
                Check("and that pass takes seconds, not a full listing", editTook < TimeSpan.FromSeconds(60), $"{editTook.TotalSeconds:F1}s");
                var (still, stillTook) = await OnePass(client, fresh, "7b. a pass with nothing changed, on the changes feed", token);
                Check("nothing changed: nothing moves", still.Uploaded + still.Downloaded + still.Deleted + still.Moved == 0 &&
                    still.Matched == expected, $"{still.Uploaded} up, {still.Downloaded} down, {still.Matched:N0} matched");
                Console.WriteLine($"    (one edit: {editTook.TotalSeconds:F1}s; nothing changed: {stillTook.TotalSeconds:F1}s, for {expected:N0} files)");

                // ---- 8. the same, a batch of folders at a time ----
                var (large, batchFiles) = (DriveMonitor.LargePairFiles, DriveMonitor.BatchFiles);
                DriveMonitor.LargePairFiles = 0;
                DriveMonitor.BatchFiles = 500;
                try
                {
                    var batched = Pair(local, driveId);
                    var (adoptB, adoptTook) = await OnePass(client, batched, "8a. a new pair a batch at a time, over what is there", token);
                    Check("a batch at a time: a new pair uploads and downloads nothing", adoptB.Uploaded + adoptB.Downloaded == 0,
                        $"{adoptB.Uploaded} up, {adoptB.Downloaded} down");
                    Check("and matches every file", adoptB.Matched == expected, $"{adoptB.Matched:N0} of {expected:N0}");
                    var renamedAgain = Directory.GetDirectories(Path.Combine(local, "part000")).FirstOrDefault(d => d.Contains(" renamed "));
                    if (renamedAgain != null)
                    {
                        var target = Path.Combine(local, "part000", "moved across batches");
                        Directory.Move(renamedAgain, target);
                        var (movedB, _) = await OnePass(client, batched, "8b. a folder moved, a batch at a time", token);
                        (files, dups, _) = await CountDrive(client, driveId, token);
                        Check("a batch at a time: the moved folder uploads nothing and moves 1,000",
                            movedB.Uploaded == 0 && movedB.Moved == 1000, $"{movedB.Uploaded} up, {movedB.Moved} moved");
                        Check("and Drive still holds exactly one of each", files == expected && dups == 0, $"{files:N0} files, {dups:N0} duplicated");
                    }
                    var (idleB, idleTook) = await OnePass(client, batched, "8c. nothing changed, a batch at a time", token);
                    Check("a batch at a time: nothing changed, nothing moves", idleB.Uploaded + idleB.Downloaded + idleB.Moved == 0 &&
                        idleB.Matched == expected, $"{idleB.Uploaded} up, {idleB.Downloaded} down, {idleB.Matched:N0} matched");
                    Console.WriteLine($"    (a batch at a time: first check {adoptTook.TotalSeconds:F1}s, nothing changed {idleTook.TotalSeconds:F1}s)");
                }
                finally
                {
                    DriveMonitor.LargePairFiles = large;
                    DriveMonitor.BatchFiles = batchFiles;
                }

                Console.WriteLine($"\nTotal time {clock.Elapsed}. {(_failed == 0 ? "ALL PASSED" : _failed + " FAILED")}");
                return _failed == 0 ? 0 : 1;
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
            }
        }

        /// <summary>The files a sync should carry: everything but the skipped placeholders.</summary>
        private static int CountLocal(string root) =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Count(f => !SyncPlanner.IsSkipped(Path.GetFileName(f)));

        private static DriveSyncPair Pair(string local, string driveId) => new()
        {
            Name = "Live sync test", LocalFolder = local, DriveFolderId = driveId,
            DriveFolderPath = "My Drive/zz-livesync", Mode = SyncMode.TwoWay, CopyDeletes = false,
            CheckMinutes = 0, IncludeSubfolders = true,
        };

        private static DriveMonitor NewMonitor() =>
            new(new GoogleDrive(), (id, message) => Console.WriteLine($"    said {id}: {message}"), _ => { });

        /// <summary>Passes until one moves nothing, printing the status as the window would show it.</summary>
        private static async Task<DriveMonitor.PairStatus> PassUntilSettled(
            DriveClient client, DriveSyncPair pair, string label, CancellationToken token)
        {
            Console.WriteLine($"\n{label}");
            int up = 0, down = 0, deleted = 0;
            DriveMonitor.PairStatus last = new(null, null, false);
            for (int pass = 1; pass <= 50; pass++)
            {
                var monitor = NewMonitor();
                var clock = Stopwatch.StartNew();
                var running = monitor.SyncOneAsync(client, pair, token);
                string said = "";
                while (!running.IsCompleted)
                {
                    await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(30), token));
                    var line = DriveMonitorForm.Describe(pair, monitor.StatusOf(pair.Id), DateTime.UtcNow);
                    if (line != said) { Console.WriteLine($"    [{clock.Elapsed:hh\\:mm\\:ss}] {line}"); said = line; }
                }
                await running;
                last = monitor.StatusOf(pair.Id);
                monitor.Dispose();
                Console.WriteLine($"  pass {pass}: {last.Uploaded:N0} up, {last.Downloaded:N0} down, {last.Deleted:N0} deleted, " +
                                  $"{last.Failed:N0} failed, {last.Matched:N0} matched, {clock.Elapsed.TotalSeconds:F0}s" +
                                  (last.Problem is { } p ? $", problem: {p}" : ""));
                up += last.Uploaded; down += last.Downloaded; deleted += last.Deleted;
                if (last.Uploaded + last.Downloaded + last.Deleted + last.Failed == 0 && last.Problem == null) break;
                if (last.Problem != null && !last.Problem.StartsWith("waiting", StringComparison.Ordinal))
                    await Task.Delay(TimeSpan.FromSeconds(20), token);
            }
            return last with { Uploaded = up, Downloaded = down, Deleted = deleted };
        }

        /// <summary>One pass, timed, with its status as the window would show it at the end.</summary>
        private static async Task<(DriveMonitor.PairStatus Status, TimeSpan Took)> OnePass(
            DriveClient client, DriveSyncPair pair, string label, CancellationToken token)
        {
            Console.WriteLine($"\n{label}");
            var monitor = NewMonitor();
            var clock = Stopwatch.StartNew();
            await monitor.SyncOneAsync(client, pair, token);
            var took = clock.Elapsed;
            var status = monitor.StatusOf(pair.Id);
            monitor.Dispose();
            Console.WriteLine($"  {took.TotalSeconds:F1}s: {DriveMonitorForm.Describe(pair, status, DateTime.UtcNow)}");
            return (status, took);
        }

        /// <summary>Every file under the Drive folder, counted, and names that appear more than once in one folder.</summary>
        private static async Task<(int Files, int Duplicates, int Folders)> CountDrive(
            DriveClient client, string rootId, CancellationToken token)
        {
            int files = 0, dups = 0, folders = 0;
            var pending = new Stack<string>();
            pending.Push(rootId);
            while (pending.Count > 0)
            {
                var id = pending.Pop();
                var children = await client.ListChildren(id, token, max: int.MaxValue);
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in children)
                {
                    if (c.IsFolder) { folders++; pending.Push(c.Id); if (!names.Add("/" + c.Name)) dups++; continue; }
                    files++;
                    if (!names.Add(c.Name)) dups++;
                }
            }
            return (files, dups, folders);
        }

        private static async Task<bool> OnDrive(DriveClient client, string rootId, string name, CancellationToken token) =>
            (await client.ListChildren(rootId, token, max: int.MaxValue)).Any(c => c.Name == name);

        /// <summary>
        /// Every awkward thing a real disk holds. Names are given as escapes so
        /// the invisible characters are really there. Returns how many files.
        /// </summary>
        private static int MakeHostile(string root, DateTime when)
        {
            int made = 0;
            void Put(string relative, byte[] data, DateTime? at = null, bool readOnly = false)
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, data);
                File.SetLastWriteTimeUtc(path, at ?? when);
                if (readOnly) File.SetAttributes(path, FileAttributes.ReadOnly);
                made++;
            }
            byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

            var names = new[]
            {
                "Café menu.txt",                       // accent, composed
                "日本語のファイル.txt",  // Japanese
                "中文文件.txt",              // Chinese
                "한국어.txt",                    // Korean
                "العربية.txt",        // Arabic
                "עברית‏.txt",  // Hebrew with a right-to-left mark
                "ภาษาไทย.txt",        // Thai
                "हिन्दी.txt",  // Devanagari
                "Русский.txt",        // Cyrillic
                "Ελληνικά.txt",  // Greek
                "song 🎵.txt",                     // emoji
                "family 👨‍👩‍👧.txt",  // emoji joined with ZWJ
                "thumbs 👍🏽.txt",       // skin tone
                "flag 🇬🇧.txt",         // flag
                "zero​width.txt",                       // zero-width space
                "no break.txt",                         // non-breaking space
                "Ｆｕｌｌｗｉｄｔｈ.txt",   // fullwidth
                "combining é̂̃.txt",         // stacked combining marks
                " leading space.txt",
                "two  spaces.txt",
                "-starts with dash.txt",
                ".starts with dot",
                "no extension",
                "many.dots.in.the.name.txt",
                "punct #%&'[]{}~`^,;=+@!$().txt",
                "UPPER.TXT",
                "mIxEd CaSe.Txt",
                new string('L', 251) + ".txt",               // 255 characters
            };
            foreach (var name in names) Put(Path.Combine("names", name), Text("name " + name));

            // The same visible name, decomposed, in a folder of its own (in one
            // folder the two spellings are one name to Drive and are left out).
            Put(Path.Combine("decomposed", "Café menu.txt"), Text("decomposed"));

            // Deeper than Windows' old 260-character limit, and 40 levels down.
            var deep = string.Join(Path.DirectorySeparatorChar.ToString(),
                Enumerable.Range(0, 40).Select(i => $"level {i:D2} with a long folder name"));
            Put(Path.Combine("deep", deep, "bottom.txt"), Text("bottom"));

            Put(Path.Combine("sizes", "empty.txt"), Array.Empty<byte>());
            Put(Path.Combine("sizes", "one byte.bin"), new byte[] { 7 });
            var big = new byte[6 * 1024 * 1024];
            new Random(42).NextBytes(big);
            Put(Path.Combine("sizes", "six megabytes.bin"), big);       // over the resumable threshold
            Put(Path.Combine("sizes", "read only.txt"), Text("read only"), readOnly: true);
            Put(Path.Combine("dates", "1980.txt"), Text("old"), new DateTime(1980, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            Put(Path.Combine("dates", "next year.txt"), Text("future"), DateTime.UtcNow.AddYears(1));
            Put(Path.Combine("dates", "odd second.txt"), Text("odd"), when.AddMilliseconds(999));

            // Folders with awkward names of their own, holding files.
            foreach (var folder in new[] { "Folder 日本", "folder with #hash", "📁 emoji folder", "dots.in.folder" })
                Put(Path.Combine("folders", folder, "inside.txt"), Text(folder));

            // An empty folder.
            Directory.CreateDirectory(Path.Combine(root, "empty folder"));
            return made;
        }

        /// <summary>The test files, made once: a thousand to a folder, a few hundred bytes each.</summary>
        private static int MakeFiles(string root, int count)
        {
            var marker = Path.Combine(root, "..", "made.txt");
            if (File.Exists(marker) && int.TryParse(File.ReadAllText(marker), out var already)) return already;

            Directory.CreateDirectory(root);
            var when = DateTime.UtcNow.AddDays(-30);
            int made = 0;
            var body = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                var folder = Path.Combine(root, $"part{i / 10_000:D3}", $"box{i / 1000 % 10:D2}");
                if (i % 1000 == 0) Directory.CreateDirectory(folder);
                body.Clear().Append("tiny file ").Append(i).Append(' ').Append('x', i % 300);
                var path = Path.Combine(folder, $"f{i:D8}.txt");
                File.WriteAllText(path, body.ToString());
                File.SetLastWriteTimeUtc(path, when.AddSeconds(i));
                made++;
            }

            made += MakeHostile(Path.Combine(root, "hostile"), when);
            File.WriteAllText(Path.Combine(root, "notes.gdoc"), "{\"url\": \"https://docs.google.com/document/d/x\"}");

            File.WriteAllText(marker, made.ToString());
            return made;
        }
    }
}
