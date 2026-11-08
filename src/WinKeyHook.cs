using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// Windows key shortcuts that Windows will not hand over through
    /// RegisterHotKey.
    ///
    /// The shell keeps almost every Windows key letter for itself: measured on
    /// Windows 11, 24 of the 26 Win+letter combinations fail with error 1409,
    /// and so do a third of the Win+Shift, Win+Alt and Win+Ctrl ones. Somebody
    /// who picked Win+E in Preferences got a shortcut that never fired. So a
    /// combination that carries the Windows key and is refused is caught here
    /// instead, by a low-level keyboard hook that sees the key before the shell
    /// does, takes it, and posts the same WM_HOTKEY the normal route would have.
    ///
    /// The hook only exists while there is something for it to catch, and it
    /// lives on a thread of its own: Windows drops a low-level hook that keeps
    /// it waiting, and the UI thread is sometimes busy for longer than that.
    /// </summary>
    internal static class WinKeyHook
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        private const int WM_HOTKEY = 0x0312, WM_QUIT = 0x0012;
        private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        /// <summary>
        /// An unassigned virtual key, pressed and let go after a key is taken.
        /// Without it the shell sees Windows go down and up with nothing between
        /// and opens the Start menu on the way up.
        /// </summary>
        internal const int MaskKeyCode = 0xE8;
        private const byte MaskKey = (byte)MaskKeyCode;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        /// <summary>Marks our own mask key, so the hook lets it straight through.</summary>
        private static readonly UIntPtr OwnInput = new(0x454E4B48);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode, scanCode, flags, time;
            public UIntPtr dwExtraInfo;
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);

        private const int WM_TIMER = 0x0113;
        private const int WM_REHOOK = 0x8001;   // WM_APP + 1
        private const int WM_DROPHOOK = 0x8002; // WM_APP + 2, tests only

        /// <summary>
        /// How often the hook is put back. Windows removes a low-level hook that
        /// once kept it waiting past LowLevelHooksTimeout, silently: a blocking
        /// compacting collection (MemoryGiveBack) can do that, and every Windows
        /// key shortcut then did nothing until Preferences OK or a restart.
        /// </summary>
        internal const uint RehookEveryMs = 30000;

        /// <summary>Tests only: how many coming installs fail, as SetWindowsHookEx can.</summary>
        internal static int FailInstallsForTests;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        private sealed record Entry(IntPtr Window, int Id, Shortcut Shortcut, bool OnlyWhenInFront, bool Front = false);

        /// <summary>Front-only catches of keys with no Windows key: one per id, kept apart from the Windows key ones.</summary>
        private const int FrontIdBit = 0x40000000;

        /// <summary>
        /// While above zero (the shortcut page is capturing a key), front catches
        /// stand aside so the key reaches the box.
        /// </summary>
        internal static volatile int FrontPaused;

        private static volatile bool AnyFront;

        /// <summary>
        /// Catches one of our shortcuts at the keyboard while this application is
        /// in front, before any other program's hotkey or hook sees it (Conner,
        /// 5.5.0: with Explorer Native in front its own keys always win). Posts
        /// WM_HOTKEY with <paramref name="id"/>, so the action is the same.
        /// </summary>
        public static bool AddFront(IntPtr window, int id, Shortcut shortcut)
        {
            lock (Gate)
            {
                var before = _entries;
                var next = new Dictionary<(IntPtr, int), Entry>(_entries)
                {
                    [(window, id | FrontIdBit)] = new Entry(window, id, shortcut, OnlyWhenInFront: true, Front: true)
                };
                _entries = next;
                AnyFront = true;
                if (EnsureRunning()) return true;
                _entries = before;
                AnyFront = before.Values.Any(e => e.Front);
                return false;
            }
        }

        public static void RemoveFront(IntPtr window, int id) => Remove(window, id | FrontIdBit);

        private static readonly object Gate = new();

        /// <summary>Replaced whole, never changed in place, so the hook reads it without a lock.</summary>
        private static volatile Dictionary<(IntPtr, int), Entry> _entries = new();

        private static volatile Action<uint, uint>? _capture;

        private static Thread? _thread;
        private static uint _threadId;

        /// <summary>Keys taken on the way down, so their repeats and the release are taken too.</summary>
        private static readonly bool[] _taken = new bool[256];
        private static readonly long[] _seenAt = new long[256];
        private static readonly uint _ownProcess = (uint)Environment.ProcessId;

        /// <summary>
        /// Catches <paramref name="shortcut"/> for <paramref name="window"/> under
        /// <paramref name="id"/>, posting WM_HOTKEY with that id when it is
        /// pressed. <paramref name="onlyWhenInFront"/> limits it to while one of
        /// this application's windows is in front.
        /// </summary>
        /// <returns>False if the hook could not be installed; the next Add tries again.</returns>
        public static bool Add(IntPtr window, int id, Shortcut shortcut, bool onlyWhenInFront)
        {
            lock (Gate)
            {
                var before = _entries;
                var next = new Dictionary<(IntPtr, int), Entry>(_entries)
                {
                    [(window, id)] = new Entry(window, id, shortcut, onlyWhenInFront)
                };
                _entries = next;
                if (EnsureRunning()) return true;
                _entries = before;
                return false;
            }
        }

        /// <summary>
        /// Puts the hook back, in case Windows removed it. Called after a
        /// blocking collection, the likeliest thing to have kept it waiting.
        /// </summary>
        internal static void Refresh()
        {
            lock (Gate)
            {
                if (_thread != null) PostThreadMessage(_threadId, WM_REHOOK, IntPtr.Zero, IntPtr.Zero);
            }
        }

        /// <summary>Tests only: takes the hook away the way a timeout does, and changes nothing else.</summary>
        internal static void DropForTests()
        {
            lock (Gate)
            {
                if (_thread != null) PostThreadMessage(_threadId, WM_DROPHOOK, IntPtr.Zero, IntPtr.Zero);
            }
        }

        public static void Remove(IntPtr window, int id)
        {
            lock (Gate)
            {
                if (!_entries.ContainsKey((window, id))) return;
                var next = new Dictionary<(IntPtr, int), Entry>(_entries);
                next.Remove((window, id));
                _entries = next;
                AnyFront = next.Values.Any(e => e.Front);
                StopIfIdle();
            }
        }

        /// <summary>
        /// While set, every Windows key combination pressed with one of this
        /// application's windows in front goes here, as modifiers and key, rather
        /// than to the shell. A shortcut box sets it while it has focus, so
        /// Win+E can be typed into it at all: the shell otherwise takes the key
        /// before the box ever sees it. Called on the hook's thread.
        /// </summary>
        public static void SetCapture(Action<uint, uint>? capture)
        {
            lock (Gate)
            {
                _capture = capture;
                if (capture != null) EnsureRunning();
                else StopIfIdle();
            }
        }

        public static void ClearCapture(Action<uint, uint> capture)
        {
            lock (Gate)
            {
                if (_capture != capture) return;
                _capture = null;
                StopIfIdle();
            }
        }

        /// <summary>
        /// True while a key this hook took is still held down. The shell never
        /// saw it go down, so GetAsyncKeyState says it is up, and holding a
        /// shortcut to repeat it would stop after the first press. Guarded by
        /// time as well: a key whose release was somehow missed stops counting
        /// as held once its repeats stop arriving.
        /// </summary>
        public static bool IsHeld(int vk)
        {
            if (vk < 0 || vk > 255 || !Volatile.Read(ref _taken[vk])) return false;
            return Environment.TickCount64 - Volatile.Read(ref _seenAt[vk]) < 1200;
        }

        /// <summary>For the tests: whether the hook is installed right now.</summary>
        internal static bool Running { get { lock (Gate) return _thread != null; } }

        private static IntPtr Install(HookProc proc)
        {
            if (FailInstallsForTests > 0) { FailInstallsForTests--; return IntPtr.Zero; }
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
        }

        /// <summary>Starts the hook's thread. False if the hook could not be installed.</summary>
        private static bool EnsureRunning()
        {
            if (_thread != null) return true;

            var ready = new ManualResetEventSlim();
            bool ok = false;
            uint id = 0;
            var thread = new Thread(() =>
            {
                id = GetCurrentThreadId();
                // A delegate of this thread's own, kept alive until its hook is
                // gone: a static one was replaced by the next thread while this
                // one's hook could still be installed.
                HookProc proc = OnKey;
                var hook = Install(proc);
                ok = hook != IntPtr.Zero;
                if (ok) SetTimer(IntPtr.Zero, UIntPtr.Zero, RehookEveryMs, IntPtr.Zero);
                ready.Set();
                if (!ok) return;

                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message is WM_TIMER or WM_REHOOK)
                    {
                        // The new one first, so no key slips past in a gap. Both
                        // call OnKey; unhooking one Windows already removed is harmless.
                        var fresh = Install(proc);
                        if (fresh != IntPtr.Zero)
                        {
                            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
                            hook = fresh;
                        }
                    }
                    else if (msg.message == WM_DROPHOOK && hook != IntPtr.Zero)
                    {
                        UnhookWindowsHookEx(hook);
                        hook = IntPtr.Zero;
                    }
                }

                if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
                GC.KeepAlive(proc);
                Array.Clear(_taken);
            })
            { IsBackground = true, Name = "Windows key shortcuts" };
            thread.Start();
            if (!ready.Wait(2000))
            {
                // Never answered: stop it if it ever does, and try again next time.
                if (id != 0) PostThreadMessage(id, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                return false;
            }
            ready.Dispose();
            if (!ok) return false;
            _threadId = id;
            _thread = thread;
            return true;
        }

        private static void StopIfIdle()
        {
            if (_thread == null || _entries.Count > 0 || _capture != null) return;
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread = null;
        }

        private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        private static bool OurWindowInFront()
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            GetWindowThreadProcessId(foreground, out var process);
            return process == _ownProcess;
        }

        private static IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

            try
            {
                var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (key.dwExtraInfo == OwnInput || key.vkCode > 255)
                    return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

                int vk = (int)key.vkCode;
                int message = (int)wParam;

                if (message is WM_KEYUP or WM_SYSKEYUP)
                {
                    if (!_taken[vk]) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
                    Volatile.Write(ref _taken[vk], false);
                    return (IntPtr)1;
                }

                if (message is not (WM_KEYDOWN or WM_SYSKEYDOWN))
                    return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

                // A repeat of a key already taken: taken again, and nothing fired,
                // the same as MOD_NOREPEAT. HoldRepeat does the repeating.
                if (_taken[vk])
                {
                    Volatile.Write(ref _seenAt[vk], Environment.TickCount64);
                    return (IntPtr)1;
                }

                if (IsModifierKey(vk)) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
                bool win = Down(VK_LWIN) || Down(VK_RWIN);
                // Without the Windows key only our own shortcuts with this
                // application in front are caught (5.5.0), so any other key
                // costs nothing more than this look.
                if (!win && (FrontPaused > 0 || !AnyFront || !OurWindowInFront()))
                    return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

                uint mods = win ? HotkeyManager.MOD_WIN : 0u;
                if (Down(VK_CONTROL)) mods |= HotkeyManager.MOD_CONTROL;
                if (Down(VK_MENU)) mods |= HotkeyManager.MOD_ALT;
                if (Down(VK_SHIFT)) mods |= HotkeyManager.MOD_SHIFT;

                var capture = _capture;
                if (win && capture != null && OurWindowInFront())
                {
                    Take(vk);
                    capture(mods, (uint)vk);
                    return (IntPtr)1;
                }

                foreach (var entry in _entries.Values)
                {
                    if (!Matches(entry.Shortcut, mods, (uint)vk)) continue;
                    if (entry.OnlyWhenInFront && !OurWindowInFront()) continue;
                    if (entry.Front && FrontPaused > 0) continue;

                    Take(vk);
                    PostMessage(entry.Window, WM_HOTKEY, (IntPtr)entry.Id, IntPtr.Zero);
                    return (IntPtr)1;
                }
            }
            catch
            {
                // Never let a fault here take the keyboard with it.
            }

            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        /// <summary>
        /// Whether a press is this shortcut. Only the four modifier bits count:
        /// a saved value can carry MOD_NOREPEAT, and that is not a key.
        /// </summary>
        internal static bool Matches(Shortcut shortcut, uint mods, uint vk) =>
            shortcut.IsAssigned && shortcut.Key == vk && (shortcut.Modifiers & 0xF) == (mods & 0xF);

        private static bool IsModifierKey(int vk) => vk is
            VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN or
            0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

        private static void Take(int vk)
        {
            Volatile.Write(ref _seenAt[vk], Environment.TickCount64);
            Volatile.Write(ref _taken[vk], true);
            keybd_event(MaskKey, 0, 0, OwnInput);
            keybd_event(MaskKey, 0, KEYEVENTF_KEYUP, OwnInput);
        }
    }
}
