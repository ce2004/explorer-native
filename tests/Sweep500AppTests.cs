using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of the tray, settings, install and updates.
    /// </summary>
    internal static class Sweep500AppTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the tray, settings, install and updates:");
            QuitSaysWhatDriveDidNotSave();
            await Task.CompletedTask;
        }

        // ---- helpers ----

        /// <summary>A file under src, read from the project the suite was built from.</summary>
        private static string Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return "";
            try { return File.ReadAllText(Path.Combine(dir.FullName, "src", name)); } catch { return ""; }
        }

        /// <summary>The body of the first member whose declaration contains <paramref name="signature"/>, braces matched.</summary>
        private static string Body(string text, string signature)
        {
            int at = text.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0) return "";
            int open = text.IndexOf('{', at);
            if (open < 0) return "";
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text.Substring(open, i - open + 1);
            }
            return "";
        }

        private static int At(string text, string what) => text.IndexOf(what, StringComparison.Ordinal);

        // ---- 1 and 6: quitting drops "not saved to Google Drive" ----

        private static void QuitSaysWhatDriveDidNotSave()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-quitnotices-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var before = Settings.OverrideAppDataDir;
            Settings.OverrideAppDataDir = dir;
            try
            {
                var notices = new QuitNotices();
                Check("quit notices: drive.save.failed is held while quitting",
                    notices.Hold("drive.save.failed", "report.docx was not saved to Google Drive before it disconnected"));
                Check("quit notices: other Drive messages are not held",
                    !notices.Hold("drive.unmounted", "Google Drive disconnected"));
                var held = notices.TakeHeld();
                Equal("quit notices: the held message comes back once", "1", held.Count.ToString());
                Equal("quit notices: and only once", "0", notices.TakeHeld().Count.ToString());

                QuitNotices.SaveForNextStart(held);
                var next = QuitNotices.TakeFromLastRun();
                Check("quit notices: the next start reads what the last quit left",
                    next.Length == 1 && next[0].StartsWith("report.docx was not saved", StringComparison.Ordinal),
                    string.Join(" | ", next));
                Equal("quit notices: and reads it once", "0", QuitNotices.TakeFromLastRun().Length.ToString());
                Equal("quit notices: said as last time's",
                    "Last time Explorer Native closed, could not save a.txt to Google Drive: no room",
                    QuitNotices.AsLastTime("Could not save a.txt to Google Drive: no room"));
            }
            finally
            {
                Settings.OverrideAppDataDir = before;
                try { Directory.Delete(dir, true); } catch { }
            }

            var tray = Source("TrayApplicationContext.cs");
            Check("quit notices: the tray's Drive subscription is not a bare PostUi",
                tray.Contains("_drive.Notification += OnDriveNotification", StringComparison.Ordinal) &&
                !tray.Contains("_drive.Notification += (id, message) => PostUi(", StringComparison.Ordinal));
            var handler = Body(tray, "private void OnDriveNotification(");
            Check("quit notices: the handler holds messages while quitting",
                At(handler, "_quitting") >= 0 && At(handler, "_quitNotices.Hold(") > At(handler, "_quitting"));

            var quit = Body(tray, "private void Quit(");
            int dispose = At(quit, "_drive.Dispose();"), deliver = At(quit, "DeliverHeldDriveMessages()"),
                trayGone = At(quit, "_tray.Dispose()");
            Check("quit notices: Quit delivers what Drive held after the drain and before the tray goes",
                dispose >= 0 && deliver > dispose && trayGone > deliver, $"{dispose} {deliver} {trayGone}");

            var deliverBody = Body(tray, "private void DeliverHeldDriveMessages(");
            Check("quit notices: delivery logs, leaves it for the next start and waits for speech",
                At(deliverBody, "WriteLog(") >= 0 && At(deliverBody, "SaveForNextStart(") >= 0 &&
                At(deliverBody, "Speech.WaitUntilSpoken(") >= 0 && At(deliverBody, "PostUi(") < 0);
            Check("quit notices: the next start says what the last quit left",
                At(tray, "QuitNotices.TakeFromLastRun()") >= 0);
        }
    }
}
