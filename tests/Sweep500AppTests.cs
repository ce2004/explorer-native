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
            DisconnectAsksAndNeverBlocksTheWindow();
            SessionEndQuitsAndSaysWhy();
            LaunchesOutwaitAQuitThatIsDraining();
            TrayMenuAccessKeysAreUnique();
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

        // ---- 2, 3, 10 and 11: turning Drive off froze the window and never asked ----

        private static void DisconnectAsksAndNeverBlocksTheWindow()
        {
            // Preferences' Disconnect button, with the tray's hook answering No
            // and then Yes: No leaves everything as it was.
            var dir = Path.Combine(Path.GetTempPath(), "en-disconnect-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var before = Settings.OverrideAppDataDir;
            var stateHook = SettingsForm.AccountState;
            var disconnectHook = SettingsForm.DisconnectDrive;
            Settings.OverrideAppDataDir = dir;
            try
            {
                foreach (bool yes in new[] { false, true })
                {
                    int asked = 0;
                    bool ownedByPreferences = false;
                    bool enabledAfter = true;
                    string buttonAfter = "", error = "";
                    SettingsForm.AccountState = () => DriveAccountState.Connected;
                    SettingsForm.DisconnectDrive = owner =>
                    {
                        asked++;
                        ownedByPreferences = owner is SettingsForm;
                        return yes;
                    };
                    var thread = new System.Threading.Thread(() =>
                    {
                        try
                        {
                            using var window = new SettingsForm(new Settings { GoogleDriveEnabled = true }, "Google Drive");
                            window.BuildAllPages();
                            var button = All<System.Windows.Forms.Button>(window)
                                .First(b => b.Text == "&Disconnect Google Drive");
                            typeof(System.Windows.Forms.Control)
                                .GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                                .Invoke(button, new object[] { EventArgs.Empty });
                            buttonAfter = button.Text;
                            window.AcceptAsync().GetAwaiter().GetResult();
                            enabledAfter = window.Result.GoogleDriveEnabled;
                        }
                        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                    });
                    thread.SetApartmentState(System.Threading.ApartmentState.STA);
                    thread.Start();
                    thread.Join(TimeSpan.FromSeconds(30));

                    var answer = yes ? "Yes" : "No";
                    Equal($"disconnect ({answer}): Preferences builds", "", error);
                    Equal($"disconnect ({answer}): the tray is asked once", "1", asked.ToString());
                    Check($"disconnect ({answer}): Preferences owns the question", ownedByPreferences);
                    Equal($"disconnect ({answer}): the button afterwards",
                        yes ? "Co&nnect Google Drive" : "&Disconnect Google Drive", buttonAfter);
                    Equal($"disconnect ({answer}): Drive stays on only after No", (!yes).ToString(), enabledAfter.ToString());
                }
            }
            finally
            {
                SettingsForm.AccountState = stateHook;
                SettingsForm.DisconnectDrive = disconnectHook;
                Settings.OverrideAppDataDir = before;
                try { Directory.Delete(dir, true); } catch { }
            }

            // The tray, which the suite does not compile.
            var tray = Source("TrayApplicationContext.cs");
            var disconnect = Body(tray, "SettingsForm.DisconnectDrive =");
            Check("disconnect: the tray asks about unsent files before anything unmounts",
                At(disconnect, "ConfirmUnsavedDrive(") >= 0 &&
                At(disconnect, "UnmountInBackground()") > At(disconnect, "ConfirmUnsavedDrive("));
            Check("disconnect: no synchronous unmount on the Disconnect button",
                At(disconnect, "_drive.Unmount()") < 0);
            Check("disconnect: no synchronous unmount when OK turns Drive off",
                At(Body(tray, "public void ApplySettings("), "_drive.Unmount()") < 0);

            var background = Body(tray, "private void UnmountInBackground(");
            Check("disconnect: the unmount runs on a worker, after any earlier one",
                At(background, "_driveGoingDown") >= 0 && At(background, "TaskScheduler.Default") >= 0 &&
                At(background, "_drive.Unmount()") > At(background, "ContinueWith("));
            Check("disconnect: a new mount waits for the old one to finish going",
                At(Body(tray, "private void MountGoogleDrive("), "await goingDown") >= 0);
            var quit = Body(tray, "private void Quit(");
            Check("disconnect: Quit lets a disconnect's drain finish before tearing down",
                At(quit, "_driveGoingDown.Wait(") >= 0 && At(quit, "_drive.Dispose();") > At(quit, "_driveGoingDown.Wait("));
        }

        // ---- 4 and 5: signing out or shutting down lost Drive files still in memory ----

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        private static void SessionEndQuitsAndSaysWhy()
        {
            var results = new List<(string What, string Expected, string Actual)>();
            string error = "";
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var names = new List<string> { "Report.docx" };
                    int ended = 0;
                    using var window = new SessionEndWindow(() => names, () => ended++);
                    var h = window.Handle;
                    void Note(string what, object expected, object actual) =>
                        results.Add((what, expected.ToString()!, actual.ToString()!));

                    Note("session end: the window is top-level, so Windows asks it", true,
                        GetAncestor(h, 1) == GetDesktopWindow());
                    Note("session end: asked, it never vetoes", 1L,
                        (long)SendMessage(h, SessionEndWindow.WM_QUERYENDSESSION, IntPtr.Zero, IntPtr.Zero));
                    Note("session end: Windows is told why it should wait", "Saving Report.docx to Google Drive",
                        SessionEndWindow.BlockReasonOf(h) ?? "(none)");

                    SendMessage(h, SessionEndWindow.WM_ENDSESSION, IntPtr.Zero, IntPtr.Zero);
                    Note("session end: a cancelled shutdown quits nothing", 0, ended);
                    Note("session end: and takes the reason back", "(none)", SessionEndWindow.BlockReasonOf(h) ?? "(none)");

                    SendMessage(h, SessionEndWindow.WM_QUERYENDSESSION, IntPtr.Zero, IntPtr.Zero);
                    SendMessage(h, SessionEndWindow.WM_ENDSESSION, (IntPtr)1, IntPtr.Zero);
                    Note("session end: the session ending quits before WM_ENDSESSION returns", 1, ended);
                    Note("session end: and the reason goes once it has", "(none)", SessionEndWindow.BlockReasonOf(h) ?? "(none)");
                    SendMessage(h, SessionEndWindow.WM_ENDSESSION, (IntPtr)1, IntPtr.Zero);
                    Note("session end: only once", 1, ended);

                    names.Clear();
                    SendMessage(h, SessionEndWindow.WM_QUERYENDSESSION, IntPtr.Zero, IntPtr.Zero);
                    Note("session end: nothing on its way to Drive holds nothing up", "(none)",
                        SessionEndWindow.BlockReasonOf(h) ?? "(none)");
                    Note("session end: two files are counted", "Saving 2 files to Google Drive",
                        SessionEndWindow.ReasonFor(new[] { "a.txt", "b.txt" }));
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(30));
            Equal("session end: the window works", "", error);
            foreach (var (what, expected, actual) in results) Equal(what, expected, actual);

            var tray = Source("TrayApplicationContext.cs");
            Check("session end: the tray listens, and the session ending quits",
                At(tray, "new SessionEndWindow(") >= 0 && At(tray, "Quit(sessionEnding: true)") > At(tray, "new SessionEndWindow("));
            var ctor = Body(tray, "public TrayApplicationContext(");
            Check("session end: listening starts before Drive mounts",
                At(ctor, "new SessionEndWindow(") >= 0 && At(ctor, "MountGoogleDrive();") > At(ctor, "new SessionEndWindow("));
            var quit = Body(tray, "private void Quit(");
            Check("session end: a quit already under way is not started again",
                At(quit, "if (_quitting) return;") >= 0 && At(quit, "if (_quitting) return;") < At(quit, "_drive.Dispose();"));
            Check("session end: nothing is relaunched as Windows ends",
                At(quit, "!sessionEnding && _unserved.TryDequeue(") >= 0);
        }

        // ---- 7 and 14: Ctrl+R or a folder opened during a long quit left nothing running ----

        private static void LaunchesOutwaitAQuitThatIsDraining()
        {
            Check("launch waits: a launch waits longer than quitting's Drive drain and every bounded wait after it",
                SingleInstance.QuittingMost >= DriveMount.DrainFor + TimeSpan.FromSeconds(33),
                $"{SingleInstance.QuittingMost} against a drain of {DriveMount.DrainFor}");

            var program = Source("Program.cs");
            var main = Body(program, "static void Main(");
            int restartWait = At(main, "mutex.WaitOne(isRestart ?");
            Check("launch waits: a restart waits the whole quitting time for the lock",
                restartWait >= 0 && At(main.Substring(restartWait), "SingleInstance.QuittingMost") is >= 0 and < 80);
            int loop = At(main, "var until = ");
            Check("launch waits: a folder opened while quitting waits the whole quitting time",
                loop >= 0 && At(main.Substring(loop), "SingleInstance.QuittingMost") is >= 0 and < 120);
            // The last test of the lock, after every wait.
            int giveUp = main.LastIndexOf("if (!acquired)", StringComparison.Ordinal);
            var rest = giveUp >= 0 ? main.Substring(giveUp) : "";
            int deliver = At(rest, "Deliver("), returns = At(rest, "return;");
            Check("launch waits: giving up says so instead of ending silently",
                giveUp >= 0 && deliver >= 0 && returns > deliver);

            var restart = Body(Source("MainForm.cs"), "private void RestartApp(");
            Check("launch waits: Ctrl+R asks about unsent Drive files before it starts the new copy",
                At(restart, "ConfirmUnsavedDrive(") >= 0 && At(restart, "Process.Start(") > At(restart, "ConfirmUnsavedDrive("));
        }

        // ---- 8: the tray menu's P and W were each shared by two items ----

        private static void TrayMenuAccessKeysAreUnique()
        {
            var tray = Source("TrayApplicationContext.cs");
            int start = At(tray, "var menu = new ContextMenuStrip();"), end = At(tray, "\"E&xit\"");
            var menu = start >= 0 && end > start ? tray.Substring(start, end - start + 8) : "";
            var labels = System.Text.RegularExpressions.Regex.Matches(menu, "\"([^\"\\r\\n]*&[^\"\\r\\n]*)\"")
                .Select(m => m.Groups[1].Value).Where(l => !l.Contains("&&")).Distinct().ToList();
            Check("tray keys: the menu's labels were found", labels.Count >= 8, string.Join(" | ", labels));

            // Pause all and Resume all are one item, which says one or the other.
            var keys = new Dictionary<char, string>();
            var clashes = new List<string>();
            foreach (var label in labels)
            {
                int amp = label.IndexOf('&');
                if (amp < 0 || amp + 1 >= label.Length) { clashes.Add(label + " has no key"); continue; }
                char key = char.ToUpperInvariant(label[amp + 1]);
                bool sameItem = label.EndsWith("all syncing", StringComparison.Ordinal);
                if (keys.TryGetValue(key, out var other) && !(sameItem && other.EndsWith("all syncing", StringComparison.Ordinal)))
                    clashes.Add($"{key}: {other} and {label}");
                keys[key] = label;
            }
            Check("tray keys: no two tray menu items share an access key", clashes.Count == 0, string.Join("; ", clashes));
            Check("tray keys: P is play or pause", keys.TryGetValue('P', out var p) && p.StartsWith("Play", StringComparison.Ordinal), p);
            Check("tray keys: W is what is playing", keys.TryGetValue('W', out var w) && w.Contains("playing", StringComparison.Ordinal), w);
        }

        private static IEnumerable<T> All<T>(System.Windows.Forms.Control root) where T : System.Windows.Forms.Control
        {
            foreach (System.Windows.Forms.Control child in root.Controls)
            {
                if (child is T found) yield return found;
                foreach (var deeper in All<T>(child)) yield return deeper;
            }
        }
    }
}
