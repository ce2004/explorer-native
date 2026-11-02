using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of keys, speech and search.
    /// </summary>
    internal static class Sweep500KeysTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, keys, speech and search:");
            WinKeyMaskIsNotAPress();
            OwnHotkeysPauseWhileChoosingShortcuts();
            DatesFollowTheWindowsRegion();
            LettersTypedWhileLoadingWaitForTheRows();
            await Task.CompletedTask;
        }

        /// <summary>
        /// After the hook takes Win+E it presses its unassigned mask key so the
        /// Start menu stays shut. That key reaches the focused shortcut box and
        /// must not be answered with "That key cannot be saved".
        /// </summary>
        private static void WinKeyMaskIsNotAPress()
        {
            var winE = new Shortcut(HotkeyManager.MOD_WIN, (uint)Keys.E);
            using var box = new ShortcutBox("Play", Shortcut.None);
            string said = "";
            box.Captured += s => said = s;
            box.Accept(winE);
            var press = typeof(ShortcutBox).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var msg = new Message();
            var handled = (bool)press.Invoke(box, new object[] { msg, (Keys)WinKeyHook.MaskKeyCode })!;
            Check("the hook's mask key is swallowed by a shortcut box", handled);
            Equal("the mask key after Win+E leaves Win+E in the box", winE.ToString(), box.Value.ToString());
            Equal("and the last thing said is still that Win+E was taken", "Play shortcut is now " + winE.Spoken, said);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>
        /// While the shortcut page is open the app's own hotkeys are let go, so
        /// pressing one in a box types it there (or says who has it) instead of
        /// running the action. They come back when the page closes.
        /// </summary>
        private static void OwnHotkeysPauseWhileChoosingShortcuts()
        {
            bool before = false, during = false, after = true, restoredFires = false;
            string error = "";
            var thread = new Thread(() =>
            {
                try
                {
                    var combo = new Shortcut(HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT | HotkeyManager.MOD_SHIFT, (uint)Keys.F9);
                    using var hotkeys = new HotkeyManager();
                    if (!hotkeys.Register(0x7E58, combo)) { error = "Ctrl+Alt+Shift+F9 is taken on this PC"; return; }
                    var probe = new NativeWindow();
                    probe.CreateHandle(new CreateParams());
                    try
                    {
                        before = RegisterHotKey(probe.Handle, 1, combo.Modifiers, combo.Key);
                        if (before) UnregisterHotKey(probe.Handle, 1);
                        using (var form = new ShortcutsForm(new Settings { SpeakEnabled = false }))
                        {
                            form.Show();
                            Application.DoEvents();
                            during = RegisterHotKey(probe.Handle, 1, combo.Modifiers, combo.Key);
                            if (during) UnregisterHotKey(probe.Handle, 1);
                            form.Close();
                        }
                        Application.DoEvents();
                        after = RegisterHotKey(probe.Handle, 1, combo.Modifiers, combo.Key);
                        if (after) UnregisterHotKey(probe.Handle, 1);
                        hotkeys.Unregister(0x7E58);
                        restoredFires = RegisterHotKey(probe.Handle, 1, combo.Modifiers, combo.Key);
                        if (restoredFires) UnregisterHotKey(probe.Handle, 1);
                    }
                    finally { probe.DestroyHandle(); }
                }
                catch (Exception ex) { error = ex.Message; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(20000);
            Equal("the hotkey pause test ran", "", error);
            Check("an app hotkey is held before the shortcut page opens", !before);
            Check("the shortcut page lets the app's hotkeys go so a box can take them", during);
            Check("closing the page claims them again", !after);
            Check("and unregistering afterwards still lets go", restoredFires);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Ms; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetDateFormatEx(string? locale, uint flags, ref SystemTime date, string? format, char[] buffer, int size, string? calendar);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetTimeFormatEx(string? locale, uint flags, ref SystemTime time, string? format, char[] buffer, int size);

        /// <summary>
        /// The shipped exe is InvariantGlobalization, so .NET's "g" is always
        /// MM/dd/yyyy HH:mm. Exact dates must come out the way Windows writes them
        /// for this PC's region, even with an invariant culture.
        /// </summary>
        private static void DatesFollowTheWindowsRegion()
        {
            var stamp = new DateTime(2026, 3, 4, 14, 30, 0);
            var st = new SystemTime { Year = 2026, Month = 3, DayOfWeek = (ushort)stamp.DayOfWeek, Day = 4, Hour = 14, Minute = 30 };
            var b = new char[128];
            int n = GetDateFormatEx(null, 0x1, ref st, null, b, b.Length, null);
            string date = new string(b, 0, n - 1);
            n = GetTimeFormatEx(null, 0x2, ref st, null, b, b.Length);
            string expected = date + " " + new string(b, 0, n - 1);
            n = GetDateFormatEx(null, 0x2, ref st, null, b, b.Length, null);
            string longExpected = new string(b, 0, n - 1) + expected.Substring(date.Length);

            var culture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                Equal("an exact Modified date is written the way this PC's region writes it, even in invariant mode",
                    expected, TimeFormatter.Format(stamp, verbose: true));
                Equal("Song properties' Modified date is the region's long date and time",
                    longExpected, TimeFormatter.WindowsDateTime(stamp, longDate: true));
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
            Console.WriteLine("  (.NET says " + stamp.ToString("g") + ", Windows " + expected + (stamp.ToString("g") == expected ? ", the same" : ", different") + ")");
        }

        /// <summary>
        /// Letters typed while a slow folder loads used to expire after the
        /// quarter-second pause, before there was anything to search.
        /// </summary>
        private static void LettersTypedWhileLoadingWaitForTheRows()
        {
            var held = new TypeAheadBuffer { TimeoutMilliseconds = 250 };
            held.Accept('m', 1000);
            held.Hold();
            Equal("a word typed while a folder loads waits for it", "m", held.Current(1900));
            held.Accept('u', 2600);
            Equal("letters typed while still loading join it", "mu", held.Current(2700));
            held.Release(3000);
            Equal("rows in: one more pause to keep typing", "mu", held.Current(3200));
            Equal("then it ends as before, so Space selects again", "", held.Current(3300));
            held.Accept('x', 4000);
            held.Hold();
            held.Reset();
            Equal("navigating away ends a held word", "", held.Current(4001));
            held.Accept('y', 5000);
            Equal("and a reset buffer expires normally", "", held.Current(5300));
            var idle = new TypeAheadBuffer { TimeoutMilliseconds = 250 };
            idle.Hold();
            idle.Accept('z', 6000);
            Equal("holding with no word typed holds nothing", "", idle.Current(6300));

            var src = File.ReadAllText(Path.Combine(SourceDir(), "MainForm.cs"));
            string Body(string start)
            {
                int i = src.IndexOf(start, StringComparison.Ordinal);
                if (i < 0) return "";
                int next = src.IndexOf("\n        private ", i + start.Length, StringComparison.Ordinal);
                int nextPublic = src.IndexOf("\n            public void ", i + start.Length, StringComparison.Ordinal);
                int end = new[] { next, nextPublic, src.Length }.Where(x => x > 0).Min();
                return src.Substring(i, end - i);
            }
            Check("a letter typed while the folder loads holds the word", Body("void List_KeyPress(").Contains("TypeAhead.Hold()"));
            Check("SetEntries lets the word go once it has been used", Body("void SetEntries(").Contains("TypeAhead.Release("));
            Check("the progressive load's full listing lets the word go", Body("public void Finish(").Contains("TypeAhead.Release("));
            Check("a navigation that ends without rows lets the word go",
                Body("Task<bool> NavigateAsync(").Contains("TypeAhead.Release("));
        }

        private static string SourceDir()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "src", "MainForm.cs")))
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            return Path.Combine(dir ?? ".", "src");
        }
    }
}
