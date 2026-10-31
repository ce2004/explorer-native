using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Windows key shortcuts (4.2.8, Conner: "you can't use the windows hot key
    /// with any letter, even if you set it in settings"). Windows refuses
    /// RegisterHotKey for 24 of the 26 Win+letter combinations, so they are
    /// caught by WinKeyHook instead. Quick group: EXPLORERNATIVE_SELFTEST_ONLY=winkeys,
    /// which runs the shell registration tests too.
    /// </summary>
    internal static class WinKeyTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

        public static Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            Console.WriteLine("Windows key shortcuts:");

            var winE = new Shortcut(HotkeyManager.MOD_WIN, (uint)Keys.E);
            Check("Win+E matches a press of Win+E", WinKeyHook.Matches(winE, HotkeyManager.MOD_WIN, (uint)Keys.E));
            Check("...but not Win+Shift+E", !WinKeyHook.Matches(winE, HotkeyManager.MOD_WIN | HotkeyManager.MOD_SHIFT, (uint)Keys.E));
            Check("...and a saved MOD_NOREPEAT bit is not a modifier",
                WinKeyHook.Matches(new Shortcut(HotkeyManager.MOD_WIN | 0x4000, (uint)Keys.E), HotkeyManager.MOD_WIN, (uint)Keys.E));
            Check("a Windows key letter is allowed as a global shortcut", winE.CanRegister);

            // The Preferences box takes it once the key reaches it.
            using (var box = new ShortcutBox("Play", Shortcut.None))
            {
                string? said = null;
                box.Captured += s => said = s;
                box.Accept(winE);
                Check("a shortcut box accepts Win+E", box.Value.Equals(winE) && box.Changed, said);
            }

            var done = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                try { Live(); }
                catch (Exception ex) { Check("the live Win+E test ran", false, ex.Message); }
                finally { done.Set(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            done.Wait(20000);
            return Task.CompletedTask;
        }

        private static void Live()
        {
            // Keys pressed into a locked desktop go nowhere: say so rather than fail.
            var desktop = OpenInputDesktop(0, false, 0x0100);
            if (desktop == IntPtr.Zero)
            {
                Console.WriteLine("  (skipped the live Win+E press: the desktop is locked)");
                return;
            }
            CloseDesktop(desktop);

            var winE = new Shortcut(HotkeyManager.MOD_WIN, (uint)Keys.E);
            using var hotkeys = new HotkeyManager();
            int fired = 0;
            bool heldWhileDown = false;
            hotkeys.HotkeyPressed += id => { if (id == 0x7E57) fired++; };

            Check("Win+E registers even though Windows keeps it", hotkeys.Register(0x7E57, winE));
            Check("...through the keyboard hook", WinKeyHook.Running);

            int explorersBefore = ExplorerWindows();

            const byte VK_LWIN = 0x5B, VK_E = 0x45;
            const uint UP = 0x0002, EXTENDED = 0x0001;
            keybd_event(VK_LWIN, 0, EXTENDED, UIntPtr.Zero);
            keybd_event(VK_E, 0, 0, UIntPtr.Zero);
            Pump(300);
            heldWhileDown = WinKeyHook.IsHeld(VK_E);
            keybd_event(VK_E, 0, 0, UIntPtr.Zero);      // a repeat while held
            Pump(100);
            keybd_event(VK_E, 0, UP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, EXTENDED | UP, UIntPtr.Zero);
            Pump(1500);

            Check("pressing Win+E fires the shortcut once", fired == 1, $"fired {fired} times");
            Check("...counts as held for repeating while the key is down", heldWhileDown);
            Check("...is no longer held after it is let go", !WinKeyHook.IsHeld(VK_E));
            Check("...and File Explorer did not open", ExplorerWindows() <= explorersBefore,
                $"{explorersBefore} explorer windows before, {ExplorerWindows()} after");

            hotkeys.Unregister(0x7E57);
            Check("unregistering the last one takes the hook away", !WinKeyHook.Running);

            // Win+E with nothing registered goes to Windows as normal: the hook
            // is gone. Not pressed here, because that opens a File Explorer window.
        }

        private static void Pump(int ms)
        {
            var until = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < until)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }

        private static int ExplorerWindows()
        {
            int count = 0;
            EnumWindows((hwnd, _) =>
            {
                var name = new StringBuilder(64);
                GetClassName(hwnd, name, name.Capacity);
                if (name.ToString() == "CabinetWClass" && IsWindowVisible(hwnd)) count++;
                return true;
            }, IntPtr.Zero);
            return count;
        }
    }
}
