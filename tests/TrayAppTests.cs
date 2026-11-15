using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System;

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
            QuitDuringTheWinFspQuestionLeavesDriveOn();
            OkMovesDriveOnlyForANewLetter();
            AccountButtonDoesNotBlameTheSignIn();
            await Task.CompletedTask;
        }

        // ---- helpers ----

        /// <summary>A file under src, read from the project the suite was built from.</summary>
        private static string Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return "";
            try { return SourceFiles.Read(Path.Combine(dir.FullName, "src", name)); } catch { return ""; }
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
                At(ctor, "new SessionEndWindow(") >= 0 && At(ctor, "MountGoogleDrive(") > At(ctor, "new SessionEndWindow("));
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

        // ---- 12 (and 18): an <exit> answered inside the WinFsp question turned Drive off ----

        private static void QuitDuringTheWinFspQuestionLeavesDriveOn()
        {
            var tray = Source("TrayApplicationContext.cs");
            var offer = Body(tray, "private void OfferWinFsp(");
            int asked = At(offer, "ShowDriveMessage("), turnOff = At(offer, "TurnOffDriveWithoutWinFsp(null)");
            int guard = asked >= 0 ? offer.IndexOf("_quitting", asked, StringComparison.Ordinal) : -1;
            Check("winfsp question: a quit during the question is not taken for No",
                asked >= 0 && guard > asked && turnOff > guard, $"{asked} {guard} {turnOff}");

            var turnOffBody = Body(tray, "private void TurnOffDriveWithoutWinFsp(");
            Check("winfsp question: Drive is never saved as off once quitting has begun",
                At(turnOffBody, "_quitting") >= 0 && At(turnOffBody, "_settings.Save()") > At(turnOffBody, "_quitting"));
        }

        // ---- 13: every OK remounted a Drive that was on its fallback letter ----

        private static void OkMovesDriveOnlyForANewLetter()
        {
            Check("drive letter: the same letter written differently is no change", !GoogleDrive.LetterSettingChanged("G", "g:"));
            Check("drive letter: blank is G", !GoogleDrive.LetterSettingChanged("", "G"));
            Check("drive letter: nothing before is G too", !GoogleDrive.LetterSettingChanged(null, "G"));
            Check("drive letter: a new letter is a change", GoogleDrive.LetterSettingChanged("G", "K"));

            var apply = Body(Source("TrayApplicationContext.cs"), "public void ApplySettings(");
            int move = At(apply, "_drive.MoveLetter(_settings.GoogleDriveLetter)");
            int ifAt = move >= 0 ? apply.LastIndexOf("if (", move, StringComparison.Ordinal) : -1;
            Check("drive letter: OK moves the drive only when the letter setting changed",
                ifAt >= 0 && apply.IndexOf("LetterSettingChanged(", ifAt, StringComparison.Ordinal) is int changed &&
                changed > ifAt && changed < move);
        }

        // ---- 9: Preferences blamed the sign-in when Drive was only offline or connecting ----

        private static void AccountButtonDoesNotBlameTheSignIn()
        {
            Check("account button: signed in but not on its letter is not a sign-in problem",
                SettingsForm.AccountStateOf(enabled: true, savedSignIn: true, mounted: false, needsSignIn: false)
                    != DriveAccountState.NeedsSignIn);
            Equal("account button: a lapsed sign-in on a live drive still asks for one", "NeedsSignIn",
                SettingsForm.AccountStateOf(true, true, true, true).ToString());
            Equal("account button: a working drive disconnects", "Connected",
                SettingsForm.AccountStateOf(true, true, true, false).ToString());
            Equal("account button: Drive off connects", "NotConnected",
                SettingsForm.AccountStateOf(false, true, true, false).ToString());
            Equal("account button: no saved sign-in connects", "NotConnected",
                SettingsForm.AccountStateOf(true, false, false, false).ToString());

            var tray = Source("TrayApplicationContext.cs");
            Check("account button: the tray uses the mapping",
                At(Body(tray, "public TrayApplicationContext("), "SettingsForm.AccountStateOf(") >= 0);

            // What the button says in that state.
            var dir = Path.Combine(Path.GetTempPath(), "en-account-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var before = Settings.OverrideAppDataDir;
            var hook = SettingsForm.AccountState;
            Settings.OverrideAppDataDir = dir;
            string text = "", description = "", error = "";
            try
            {
                SettingsForm.AccountState = () => DriveAccountState.NotConnectedYet;
                var thread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        using var window = new SettingsForm(new Settings { GoogleDriveEnabled = true }, "Google Drive");
                        window.BuildAllPages();
                        var button = All<System.Windows.Forms.Button>(window)
                            .First(b => b.Text.Contains("Google Drive", StringComparison.Ordinal) && !b.Text.Contains("monitor"));
                        text = button.Text;
                        description = button.AccessibleDescription ?? "";
                    }
                    catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                });
                thread.SetApartmentState(System.Threading.ApartmentState.STA);
                thread.Start();
                thread.Join(TimeSpan.FromSeconds(30));
            }
            finally
            {
                SettingsForm.AccountState = hook;
                Settings.OverrideAppDataDir = before;
                try { Directory.Delete(dir, true); } catch { }
            }
            Equal("account button: Preferences builds when Drive is not connected yet", "", error);
            Equal("account button: not connected yet offers to reconnect, on N", "Reco&nnect Google Drive", text);
            Check("account button: and says nothing about signing in",
                description.Length is > 0 and < 70 && !description.Contains("sign in", StringComparison.OrdinalIgnoreCase), description);
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

    // ---- merged from Sweep500App2Tests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// The 5.0.0 sweep of Preferences and the tray's audio.
    /// </summary>
    internal static class Sweep500App2Tests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, Preferences and the tray's audio:");
            HotkeyPageChecksTheBoxesAsTicked();
            SignOutTurnsDriveOffAtOnce();
            CheckBoxNamesHaveNoAccessKeyMarker();
            NewAppleMusicSignInReachesThePlayer();
            HeldKeyAtAnEdgeSpeaksOncePerHold();
            AudioWarmStaysOffTheUiThread();
            await Task.CompletedTask;
        }

        private static string NewSettingsDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-sweep500app2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Settings.OverrideAppDataDir = dir;
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        private static string OnSta(Action work, int seconds = 60)
        {
            string error = "";
            var thread = new Thread(() =>
            {
                try { work(); }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(seconds))) return "timed out";
            return error;
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

        private static string Source(string file)
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var candidate = Path.Combine(dir, "src", file);
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
            }
            throw new FileNotFoundException(file);
        }

        /// <summary>The text of a member, from its declaration to the next one at the same indent.</summary>
        private static string Body(string source, string declaration)
        {
            int at = source.IndexOf(declaration, StringComparison.Ordinal);
            if (at < 0) return "";
            int end = source.IndexOf("\n        private ", at + declaration.Length, StringComparison.Ordinal);
            int end2 = source.IndexOf("\n        public ", at + declaration.Length, StringComparison.Ordinal);
            int e = new[] { end, end2 }.Where(x => x > 0).DefaultIfEmpty(source.Length).Min();
            return source[at..e];
        }

        /// <summary>
        /// The Hotkey page checked the modifiers the dialog opened with, because
        /// the boxes were only copied out by the applies, which run after the check.
        /// A fresh install could never set Ctrl+Alt+E, and unticking Alt saved Ctrl+C.
        /// </summary>
        private static void HotkeyPageChecksTheBoxesAsTicked()
        {
            var dir = NewSettingsDir();
            var oldWarn = SettingsForm.Warn;
            try
            {
                (bool accepted, string warned, uint mods, uint key) Run(Settings start, Action<Func<string, CheckBox>, ComboBox> change)
                {
                    bool accepted = false; string warned = ""; uint mods = 99, key = 99;
                    SettingsForm.Warn = (_, text, _) => warned = text;
                    var error = OnSta(() =>
                    {
                        using var window = new SettingsForm(start, null);
                        window.BuildAllPages();
                        var boxes = FindAll<CheckBox>(window);
                        var keyBox = FindAll<ComboBox>(window).First(c => c.AccessibleName == "Hotkey letter or key");
                        change(name => boxes.First(c => c.Text == name), keyBox);
                        accepted = window.AcceptAsync().GetAwaiter().GetResult();
                        mods = window.Result.HotkeyModifiers;
                        key = window.Result.HotkeyKey;
                    });
                    Equal("Preferences builds for the hotkey page", "", error);
                    return (accepted, warned, mods, key);
                }

                uint ctrlAlt = HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT;

                // Fresh install: tick Control and Alt, pick E.
                var r = Run(new Settings { SpeakEnabled = false, GlobalHotkeyEnabled = true }, (box, keys) =>
                {
                    box("Control").Checked = true;
                    box("Alt").Checked = true;
                    keys.SelectedIndex = keys.Items.IndexOf("E");
                });
                Check("hotkey: Ctrl+Alt+E is accepted on a fresh install", r.accepted, r.warned);
                Equal("hotkey: and nothing is refused", "", r.warned);
                Equal("hotkey: and Ctrl+Alt+E is saved", $"{ctrlAlt} {(uint)Keys.E}", $"{r.mods} {r.key}");

                // Ctrl+Alt+C saved, Alt unticked: Ctrl+C is reserved.
                Settings Saved() => new Settings
                {
                    SpeakEnabled = false, GlobalHotkeyEnabled = true,
                    HotkeyModifiers = ctrlAlt, HotkeyKey = (uint)Keys.C,
                };
                r = Run(Saved(), (box, _) => box("Alt").Checked = false);
                Check("hotkey: unticking Alt from Ctrl+Alt+C is refused", !r.accepted);
                Check("hotkey: and the refusal names Ctrl plus C", r.warned.Contains("Ctrl plus C", StringComparison.Ordinal), r.warned);
                Equal("hotkey: and Ctrl+Alt+C is kept", $"{ctrlAlt} {(uint)Keys.C}", $"{r.mods} {r.key}");

                // Every box unticked: a bare C.
                r = Run(Saved(), (box, _) =>
                {
                    box("Control").Checked = false;
                    box("Alt").Checked = false;
                });
                Check("hotkey: unticking every modifier is refused", !r.accepted);
                Equal("hotkey: and nothing changes", $"{ctrlAlt} {(uint)Keys.C}", $"{r.mods} {r.key}");

                // Shift and P: the refusal names Shift plus P, not a bare P.
                r = Run(new Settings { SpeakEnabled = false, GlobalHotkeyEnabled = true }, (box, keys) =>
                {
                    box("Shift").Checked = true;
                    keys.SelectedIndex = keys.Items.IndexOf("P");
                });
                Check("hotkey: Shift+P is refused", !r.accepted);
                Check("hotkey: and the refusal names Shift plus P", r.warned.Contains("Shift plus P", StringComparison.Ordinal), r.warned);
            }
            finally
            {
                SettingsForm.Warn = oldWarn;
                Settings.OverrideAppDataDir = null;
                Cleanup(dir);
            }
        }

        /// <summary>
        /// Sign out of Google deleted the token but left Drive on in the dialog's
        /// copy only, so Cancel kept Drive on with no sign-in, and the next start
        /// opened Google's sign-in page in the browser unasked. It now switches
        /// the live drive off and unmounts it at once, before the token goes.
        /// </summary>
        private static void SignOutTurnsDriveOffAtOnce()
        {
            var dir = NewSettingsDir();
            var oldConfirm = SettingsForm.Confirm;
            var oldInform = SettingsForm.Inform;
            var oldDisconnect = SettingsForm.DisconnectDrive;
            try
            {
                var token = GoogleDrive.SavedTokenPath;
                Directory.CreateDirectory(Path.GetDirectoryName(token)!);
                File.WriteAllText(token, "dummy");

                var order = new List<string>();
                string told = "";
                SettingsForm.Confirm = (_, _, _) => true;
                SettingsForm.Inform = (_, text, _) => told = text;
                SettingsForm.DisconnectDrive = _ => { order.Add(File.Exists(token) ? "disconnect, token still there" : "disconnect, token gone"); return true; };

                bool found = false;
                var error = OnSta(() =>
                {
                    using var window = new SettingsForm(new Settings { SpeakEnabled = false, GoogleDriveEnabled = true }, null);
                    window.BuildAllPages();
                    var button = FindAll<Button>(window).FirstOrDefault(b => b.Text.Replace("&", "") == "Sign out of Google");
                    found = button != null && button.Enabled;
                    // OnClick, not PerformClick: the window is never shown, and
                    // PerformClick does nothing on a control that cannot be selected.
                    if (button != null)
                        typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                            .Invoke(button, new object[] { EventArgs.Empty });
                    // Closed without OK: Cancel.
                });
                Equal("sign out: Preferences builds", "", error);
                Check("sign out: the button is there and enabled with a saved sign-in", found);
                Equal("sign out: the live drive is switched off and unmounted before the token goes",
                    "disconnect, token still there", string.Join("; ", order));
                Check("sign out: the token is deleted", !File.Exists(token));
                Check("sign out: the message says Drive is off now, not at the next start",
                    !told.Contains("next time", StringComparison.OrdinalIgnoreCase) && told.Contains("off", StringComparison.Ordinal), told);
            }
            finally
            {
                SettingsForm.Confirm = oldConfirm;
                SettingsForm.Inform = oldInform;
                SettingsForm.DisconnectDrive = oldDisconnect;
                Settings.OverrideAppDataDir = null;
                Cleanup(dir);
            }
        }

        /// <summary>
        /// Preferences' check boxes with an access key were named with the "&amp;"
        /// in it, and NVDA read "Show Google Drive's and Trash folder".
        /// </summary>
        private static void CheckBoxNamesHaveNoAccessKeyMarker()
        {
            var dir = NewSettingsDir();
            try
            {
                var offenders = new List<string>();
                string trash = "";
                var error = OnSta(() =>
                {
                    using var window = new SettingsForm(new Settings { SpeakEnabled = false }, null);
                    window.BuildAllPages();
                    foreach (var b in FindAll<ButtonBase>(window))
                    {
                        if (!b.UseMnemonic || b.AccessibleName == null) continue;
                        if (b.AccessibleName.Replace("&&", "").Contains('&')) offenders.Add(b.AccessibleName);
                        if (b.Text.Contains("&Trash", StringComparison.Ordinal)) trash = b.AccessibilityObject.Name ?? "";
                    }
                });
                Equal("check box names: Preferences builds", "", error);
                Equal("check box names: no control is named with an access-key marker", "", string.Join(" | ", offenders));
                Equal("check box names: the Trash box reads as written", "Show Google Drive's Trash folder", trash);
            }
            finally
            {
                Settings.OverrideAppDataDir = null;
                Cleanup(dir);
            }
        }

        /// <summary>
        /// A new cookies.txt reached browsing but not the player's page, which
        /// kept the token it started with: "Load a new cookies.txt" never helped.
        /// Configure now says whether the sign-in changed, and the tray closes
        /// the player's page when it did.
        /// </summary>
        private static void NewAppleMusicSignInReachesThePlayer()
        {
            try
            {
                AppleMusic.ResetForTests();
                bool first = AppleMusic.Configure("a");
                bool same = AppleMusic.Configure("a");
                bool other = AppleMusic.Configure("b");
                bool forgotten = AppleMusic.Configure("");
                Equal("apple sign-in: Configure says whether the sign-in changed",
                    "True False True True", $"{first} {same} {other} {forgotten}");
            }
            finally { AppleMusic.ResetForTests(); }

            var tray = Source("TrayApplicationContext.cs");
            var apply = Body(tray, "void ApplySettings(");
            Check("apple sign-in: Preferences tells the player when the sign-in changed",
                System.Text.RegularExpressions.Regex.IsMatch(apply,
                    @"if\s*\(\s*AppleMusic\.Configure\([^;]*\)\s*\)\s*AppleMusicPlayer\.SignInChanged\(\)"));

            var player = Source("AppleMusicPlayer.cs");
            var signIn = Body(player, "public static void SignInChanged(");
            Check("apple sign-in: the player closes its page on a new sign-in", signIn.Contains("Reset()", StringComparison.Ordinal));
            Check("apple sign-in: and drops what was playing so a waiting start gives up", signIn.Contains("_generation++", StringComparison.Ordinal));
        }

        /// <summary>
        /// Held at the top or bottom of the volume, or with nothing loaded, the
        /// same sentence was spoken on every 20ms repeat, cutting itself off.
        /// The once-per-hold set now covers the same-sentence path and Apple
        /// Music's, and is cleared before either.
        /// </summary>
        private static void HeldKeyAtAnEdgeSpeaksOncePerHold()
        {
            var core = Body(Source("TrayApplicationContext.cs"), "private void RunAudioActionCore(");
            Check("held edge: RunAudioActionCore found", core.Length > 0);

            int clear = core.IndexOf("_saidThisHold.Clear()", StringComparison.Ordinal);
            int apple = core.IndexOf("AppleMusicPlayer.IsActive", StringComparison.Ordinal);
            int same = core.IndexOf("finerMessage == message", StringComparison.Ordinal);
            Check("held edge: a new press clears the hold before Apple Music's branch", clear >= 0 && apple > clear);
            Check("held edge: and before the same-sentence branch", clear >= 0 && same > clear);

            // The same-sentence branch, up to its return, consults the set.
            int sameEnd = same < 0 ? -1 : core.IndexOf("return;", same, StringComparison.Ordinal);
            Check("held edge: the same-sentence branch speaks once per hold",
                same >= 0 && sameEnd > same && core[same..sameEnd].Contains("_saidThisHold.Add(", StringComparison.Ordinal));

            // Apple Music's branch, up to the file player's switch, does too.
            int filePlayer = core.IndexOf("_audio.TogglePlayPause()", StringComparison.Ordinal);
            Check("held edge: Apple Music's maximum and minimum are said once per hold",
                apple >= 0 && filePlayer > apple && core[apple..filePlayer].Contains("_saidThisHold.Add(", StringComparison.Ordinal));
        }

        /// <summary>
        /// The start-up audio warm ran on the UI thread: file checks, then a
        /// Media Foundation open and decode of the last track. With a CD track or
        /// a sleeping disk last played, the window froze for seconds at start.
        /// The warm now runs on a worker, never wakes a CD or removable drive,
        /// and the Drive guard it promised is wired.
        /// </summary>
        private static void AudioWarmStaysOffTheUiThread()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-sweep500app2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var local = Path.Combine(dir, "tone.wav");
                File.WriteAllBytes(local, new byte[64]);
                Check("audio warm: a CD track is not woken for a warm", AudioPlayer.SlowToWake(@"D:\Track05.cda"));
                Check("audio warm: a CD track named in capitals is not either", AudioPlayer.SlowToWake(@"E:\TRACK01.CDA"));
                Check("audio warm: a file on the local disk is still warmed", !AudioPlayer.SlowToWake(local));

                var warm = Body(Source("AudioPlayer.cs"), "public void Warm(");
                int slow = warm.IndexOf("SlowToWake(", StringComparison.Ordinal);
                int exists = warm.IndexOf("File.Exists(", StringComparison.Ordinal);
                Check("audio warm: the slow-drive check comes before any access to the file", slow >= 0 && exists > slow);

                var tray = Source("TrayApplicationContext.cs");
                var warmAudio = Body(tray, "private void WarmAudio(");
                int run = warmAudio.IndexOf("Task.Run(", StringComparison.Ordinal);
                if (run < 0) run = warmAudio.IndexOf("ThreadPool.", StringComparison.Ordinal);
                int call = warmAudio.IndexOf("_audio.Warm(", StringComparison.Ordinal);
                Check("audio warm: the warm runs on a worker, not in the UI post", run >= 0 && call > run);
                Check("audio warm: the Drive guard is wired",
                    System.Text.RegularExpressions.Regex.IsMatch(tray, @"AudioPlayer\.IsDrivePath\s*="));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
