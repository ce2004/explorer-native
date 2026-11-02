using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
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
                SettingsForm.DisconnectDrive = () => order.Add(File.Exists(token) ? "disconnect, token still there" : "disconnect, token gone");

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
    }
}
