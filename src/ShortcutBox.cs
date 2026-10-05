using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// A field you set by pressing the keys, rather than by picking a modifier
    /// out of four tick boxes and a key out of a combo box.
    ///
    /// That is how every other application asks for a shortcut, and it is also
    /// much less to walk through with a screen reader: one control per action
    /// instead of five. The Hotkey page still uses the tick boxes — it has a
    /// single shortcut on it and predates this — but eight audio actions that way
    /// would be forty controls in a list read one at a time.
    ///
    /// Tab, Shift+Tab, Enter and Escape are never captured, so the box can always
    /// be left and the dialog can always be answered from inside it. Delete
    /// clears. Anything without a modifier is refused: registering a bare letter
    /// globally takes that letter away from every application on the machine.
    /// </summary>
    public sealed class ShortcutBox : TextBox
    {
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;

        [DllImport("user32.dll")] private static extern short GetKeyState(int vKey);

        private Shortcut _value;

        /// <summary>What the box now holds, and why — the caller announces it.</summary>
        public event Action<string>? Captured;

        /// <summary>
        /// Asks whoever owns the dialog whether a combination is already spoken
        /// for, and by what. Returning a name refuses the capture.
        ///
        /// It has to be asked rather than worked out here: only the dialog knows
        /// about the other boxes, and only it knows about shortcuts that are not
        /// boxes at all, like the one that summons the window.
        /// </summary>
        public Func<Shortcut, string?>? InUse { get; set; }

        public ShortcutBox(string label, Shortcut initial)
        {
            ReadOnly = true;

            // Otherwise Ctrl+C, Ctrl+V and friends are eaten by the text box
            // itself before they can be captured as shortcuts.
            ShortcutsEnabled = false;

            Width = 220;
            AccessibleName = label + " shortcut";
            AccessibleDescription =
                "Press the key combination to use, with Control, Alt or Shift held. " +
                "Press Delete to clear it. Tab moves on.";

            Value = initial;
        }

        public Shortcut Value
        {
            get => _value;
            set
            {
                _value = value;
                Text = value.IsAssigned ? value.ToString() : "(none)";
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            var modifiers = keyData & Keys.Modifiers;

            // The way out. Without these the box is a trap: no way to move on, no
            // way to press OK, no way to cancel.
            if (modifiers == Keys.None && key is Keys.Tab or Keys.Escape or Keys.Enter)
                return base.ProcessCmdKey(ref msg, keyData);
            if (modifiers == Keys.Shift && key == Keys.Tab)
                return base.ProcessCmdKey(ref msg, keyData);

            // A modifier held on its own is not yet a shortcut.
            if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu
                    or Keys.LControlKey or Keys.RControlKey
                    or Keys.LShiftKey or Keys.RShiftKey
                    or Keys.LMenu or Keys.RMenu
                    or Keys.LWin or Keys.RWin or Keys.None)
                return base.ProcessCmdKey(ref msg, keyData);

            if (modifiers == Keys.None && key is Keys.Delete or Keys.Back)
            {
                Value = Shortcut.None;
                Captured?.Invoke(AccessibleName + " cleared");
                return true;
            }

            uint mods = 0;
            if ((modifiers & Keys.Control) != 0) mods |= HotkeyManager.MOD_CONTROL;
            if ((modifiers & Keys.Alt) != 0) mods |= HotkeyManager.MOD_ALT;
            if ((modifiers & Keys.Shift) != 0) mods |= HotkeyManager.MOD_SHIFT;

            // Keys.Modifiers has no Windows bit — WinForms does not carry one —
            // so the Windows key has to be asked about directly.
            if ((GetKeyState(VK_LWIN) & 0x8000) != 0 || (GetKeyState(VK_RWIN) & 0x8000) != 0)
                mods |= HotkeyManager.MOD_WIN;

            var candidate = new Shortcut(mods, (uint)key);

            // A key with no name here — a Japanese IME key, an unnamed OEM key —
            // is written to the file as a number that never parses back, and the
            // shortcut then silently does not exist on the next start.
            if (!Shortcut.Parse(candidate.ToString()).Equals(candidate))
            {
                Captured?.Invoke("That key cannot be saved as a shortcut. Choose another.");
                return true;
            }

            if (!candidate.CanRegister)
            {
                Captured?.Invoke(
                    "A global shortcut needs Control, Alt or Shift, or one of the media keys. " +
                    "On its own, that key would stop working everywhere else.");
                return true;
            }

            // Somebody else's shortcut. Refused rather than taken, because the
            // alternative is two actions registered to one key: Windows gives it
            // to whichever registered first, the other silently never fires, and
            // nothing anywhere says which one won.
            var clash = InUse?.Invoke(candidate);
            if (!string.IsNullOrEmpty(clash))
            {
                Captured?.Invoke($"{candidate.Spoken} is already used by {clash}. Not changed.");
                return true;
            }

            Value = candidate;
            Captured?.Invoke(AccessibleName + " is now " + candidate.Spoken);
            return true;
        }
    }
}
