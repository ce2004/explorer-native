using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Every global hotkey the application owns, on a hidden message-only window
    /// so they survive the main form being closed to the tray.
    ///
    /// One window, many hotkeys, each with its own id. That matters for the audio
    /// player: it has no window at all, so a global hotkey is the *only* way to
    /// reach it, and it needs several.
    /// </summary>
    public sealed class HotkeyManager : IDisposable
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        private const int WM_HOTKEY = 0x0312;

        /// <summary>The one that shows and hides the window.</summary>
        public const int MainWindowHotkeyId = 0xB00C;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
            WinEventProc callback, uint process, uint thread, uint flags);

        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        private readonly MessageWindow _window = new();
        private readonly HashSet<int> _registered = new();

        /// <summary>
        /// Shortcuts that are this application's only: claimed while one of its
        /// windows is in front and handed back the moment another program is.
        /// </summary>
        private readonly Dictionary<int, Shortcut> _local = new();
        private readonly HashSet<int> _localClaimed = new();
        private readonly uint _ownProcess = (uint)Environment.ProcessId;

        // Kept in a field so the collector cannot take the delegate Windows is calling.
        private WinEventProc? _foregroundProc;
        private IntPtr _foregroundHook;

        public event Action<int>? HotkeyPressed;

        public HotkeyManager()
        {
            _window.HotkeyReceived += id => HotkeyPressed?.Invoke(id);
        }

        /// <summary>
        /// Claims a combination only while this application is in front.
        ///
        /// Still RegisterHotKey, so holding, MOD_NOREPEAT and every check behave
        /// exactly as for a global one. The difference is when: a foreground
        /// change event registers it as one of our windows comes forward and
        /// unregisters it as another program's does, so outside this application
        /// the key does its usual job. Returns false only if it can never be
        /// registered; another program owning it shows up as the key doing nothing.
        /// </summary>
        public bool RegisterLocal(int id, Shortcut shortcut)
        {
            Unregister(id);
            if (!shortcut.CanRegister) return false;

            _local[id] = shortcut;
            if (_foregroundHook == IntPtr.Zero)
            {
                _foregroundProc = (_, _, hwnd, _, _, _, _) => SyncLocal(hwnd);
                _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero, _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);
            }
            SyncLocal(GetForegroundWindow());
            return true;
        }

        private void SyncLocal(IntPtr foreground)
        {
            bool ours = false;
            if (foreground != IntPtr.Zero)
            {
                GetWindowThreadProcessId(foreground, out var process);
                ours = process == _ownProcess;
            }

            foreach (var (id, shortcut) in _local)
            {
                if (ours && !_localClaimed.Contains(id))
                {
                    if (RegisterHotKey(_window.Handle, id, shortcut.Modifiers | MOD_NOREPEAT, shortcut.Key))
                        _localClaimed.Add(id);
                }
                else if (!ours && _localClaimed.Remove(id))
                {
                    try { UnregisterHotKey(_window.Handle, id); } catch { }
                }
            }
        }

        /// <summary>
        /// Claims one combination. Returns false if it is unregisterable or
        /// already taken — by another application, or by one of ours.
        ///
        /// Always MOD_NOREPEAT: one press, one message. Windows will repeat a
        /// held hotkey by itself, but only at the keyboard's typing rate, which
        /// is slow to start and slow to run. Anything that should repeat while
        /// held is driven by <see cref="HoldRepeat"/> instead, at whatever rate
        /// the preferences ask for — and the two together would double every
        /// press.
        /// </summary>
        public bool Register(int id, Shortcut shortcut)
        {
            Unregister(id);
            if (!shortcut.CanRegister) return false;

            if (!RegisterHotKey(_window.Handle, id, shortcut.Modifiers | MOD_NOREPEAT, shortcut.Key))
                return false;

            _registered.Add(id);
            return true;
        }

        public void Unregister(int id)
        {
            _local.Remove(id);
            if (!_registered.Remove(id) && !_localClaimed.Remove(id)) return;
            try { UnregisterHotKey(_window.Handle, id); } catch { }
        }

        public void UnregisterAll()
        {
            foreach (int id in new List<int>(_registered))
                Unregister(id);
            foreach (int id in new List<int>(_local.Keys))
                Unregister(id);
        }

        public void Dispose()
        {
            UnregisterAll();
            if (_foregroundHook != IntPtr.Zero)
            {
                try { UnhookWinEvent(_foregroundHook); } catch { }
                _foregroundHook = IntPtr.Zero;
            }
            try { _window.DestroyHandle(); } catch { }
        }

        private sealed class MessageWindow : NativeWindow
        {
            /// <summary>
            /// Parenting to HWND_MESSAGE is what actually makes this message-only.
            /// Without it CreateParams produces a real top-level window: it never
            /// paints, but it exists in the window list, is enumerated by Alt+Tab
            /// tooling and by screen readers, and takes part in broadcast messages
            /// it has no business seeing.
            /// </summary>
            private static readonly IntPtr HwndMessage = new(-3);

            public event Action<int>? HotkeyReceived;

            public MessageWindow() => CreateHandle(new CreateParams { Parent = HwndMessage });

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY) HotkeyReceived?.Invoke(m.WParam.ToInt32());
                base.WndProc(ref m);
            }
        }
    }
}
