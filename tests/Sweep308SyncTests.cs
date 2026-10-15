using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

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
}
