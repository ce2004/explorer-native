using System;
using System.IO;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// 5.1.0: Google Drive: the mounted letter, the client, the folder sync and the tray.
    /// These read the source, because the code they pin needs a live Drive, WinFsp or a window.
    /// </summary>
    internal static class Release510DriveTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        private static string Src(string name)
        {
            var at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at != null && !Directory.Exists(Path.Combine(at.FullName, "src"))) at = at.Parent;
            if (at == null) return "";
            var path = Path.Combine(at.FullName, "src", name);
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch { return ""; }
        }

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("5.1.0, Google Drive: the mounted letter, the client, the folder sync and the tray:");

            var monitor = Src("DriveMonitor.cs");
            var driveOnly = Src("DriveMonitor.DriveOnly.cs");
            var form = Src("DriveMonitorForm.cs");
            var mount = Src("DriveMount.cs");
            var volume = Src("DriveMount.Volume.cs");
            var tray = Src("TrayApplicationContext.cs");
            var drive = Src("GoogleDrive.cs");
            Check("sources were found", monitor.Length > 0 && form.Length > 0 && tray.Length > 0);

            Check("1: coming back on another letter tells the app",
                volume.Contains("OnLetterMoved?.Invoke") && drive.Contains("LetterMoved?.Invoke") && tray.Contains("_drive.LetterMoved +="));
            Check("2/3: a folder listing is linked to the unmount",
                mount.Contains("CreateLinkedTokenSource(_closing.Token);\n            deadline.CancelAfter") ||
                mount.Contains("CreateLinkedTokenSource(_closing.Token);\r\n            deadline.CancelAfter"));
            Check("4: the WinFsp offer stays set until the install ends",
                tray.Contains("if (!installing) _offeringWinFsp = false;"));
            Check("5: Reserve takes out only its own failed listing",
                Src("DriveUpload.cs").Contains("new KeyValuePair<string, Lazy<HashSet<string>?>>(folder, lazy)"));
            Check("6: a timed-out folder is not 'operation was canceled'",
                drive.Contains("Google Drive took too long to answer"));
            Check("7: the shared look checks the folder the drive was read for", monitor.Contains("_driveOfFolder"));
            Check("8: one second is not 'seconds'", monitor.Contains("s == 1 ? \"1 second\""));
            Check("9: unreadable files keep their folder in DirtyFolders", monitor.Contains("unread?.Add(path)") &&
                monitor.Contains("unread.Select(p => new SyncAction"));
            Check("10: a refused move is counted as failed", monitor.Contains("SyncHistory.Add(pair.Id, \"Could not sync\", move.To, ex.Message)"));
            Check("11: a stopped pair is not swallowed by the feed fallbacks",
                monitor.Split("ex is not PairStoppedException").Length >= 3);
            Check("12: Keep on Drive only checks the folder off the UI thread",
                !driveOnly.Contains("if (!Directory.Exists(pcFolder))") && driveOnly.Contains("Task.Run(() => Directory.Exists(pcFolder))"));
            Check("13: Put back refuses in an upload-only pair",
                driveOnly.Contains("pair.Mode == SyncMode.UploadOnly\n                return new(false, $\"{DisplayName(pair)} only uploads, so {Leaf(entry)}") ||
                driveOnly.Contains("cannot be put back on this PC"));
            Check("14: Delete in the sync window asks first", form.Contains("Stop syncing {DriveMonitor.DisplayName(_pairs[at])}?"));
            Check("15: the sync window, picker and help lists respect the speech switch",
                form.Contains("if (!MaySpeak) return;") && Src("ChangelogForm.cs").Contains("if (!MaySpeak) return;") &&
                Src("KeyboardShortcutsForm.cs").Contains("if (!MaySpeak) return;") && Src("MainForm.cs").Contains("MaySpeak = _settings.SpeakEnabled"));
            Check("16: putting back a staged folder brings back the ones it absorbed", form.Contains("_absorbed.Remove(gone.Relative"));
            Check("17: the picker names Google Drive at the top", form.Contains("(AtTop ? \"Google Drive\" : FolderPath) + \": \""));
            Check("18: a space-stopped full listing merges the folder map",
                monitor.Contains("if (full && tally.SpaceProblem != null)"));
            Check("19: the fast lanes hand folder failures to the careful loop",
                monitor.Contains("TryMakeFoldersAsync(client, pair, state, folders, batch.Select") &&
                !monitor.Contains("await MakeFoldersAsync(client, pair, state, folders, batch.Select"));
            Check("20: fast lane counts start from the pair's own", monitor.Contains("Uploaded = upBefore + count") &&
                monitor.Contains("Downloaded = downBefore + count"));
            Check("21: the updated message respects the speech switch", Src("Program.cs").Contains("if (mayAnnounce) Speech.Speak(updated);"));
            Check("22: WinFsp installed but not loading is not offered an install",
                drive.Contains("NeedsWinFsp = !winFspLoads && !WinFsp.Installed;"));
            Check("23: the output device is saved on OK", tray.Contains("if (outcome == DialogResult.OK) _settings.Save();"));

            await Task.CompletedTask;
        }
    }
}
