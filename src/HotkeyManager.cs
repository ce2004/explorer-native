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

        private readonly MessageWindow _window = new();
        private readonly HashSet<int> _registered = new();

        public event Action<int>? HotkeyPressed;

        public HotkeyManager()
        {
            _window.HotkeyReceived += id => HotkeyPressed?.Invoke(id);
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
            if (!_registered.Remove(id)) return;
            try { UnregisterHotKey(_window.Handle, id); } catch { }
        }

        public void UnregisterAll()
        {
            foreach (int id in new List<int>(_registered))
                Unregister(id);
        }

        public void Dispose()
        {
            UnregisterAll();
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
