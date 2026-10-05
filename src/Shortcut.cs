using System;
using System.Text;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// One key combination, written down the way a person would say it:
    /// "Ctrl+Alt+P", "Ctrl+Alt+Up", "MediaPlayPause".
    ///
    /// Text, not a packed integer, because that is what ends up in settings.json.
    /// Somebody reading or hand-editing that file should be able to see what the
    /// shortcut is, and a value the app cannot parse costs them a shortcut that
    /// does nothing rather than a shortcut that does something unexpected.
    /// </summary>
    public readonly record struct Shortcut(uint Modifiers, uint Key)
    {
        public static readonly Shortcut None = default;

        public bool IsAssigned => Key != 0;

        /// <summary>
        /// Whether this is something Windows will hand us globally.
        ///
        /// A bare letter is refused on purpose. RegisterHotKey takes the key away
        /// from every other application on the machine, so a global hotkey of "P"
        /// means no program ever sees a P again — including this one. The media
        /// keys are the exception: they are already global by nature, and a
        /// keyboard that has them is a keyboard where taking them is the point.
        /// </summary>
        public bool CanRegister => IsAssigned && (Modifiers != 0 || IsSelfContained((Keys)Key));

        /// <summary>
        /// Keys worth registering with no modifier at all — they exist for exactly
        /// this and nothing types them.
        /// </summary>
        public static bool IsSelfContained(Keys key) => key switch
        {
            Keys.MediaPlayPause or Keys.MediaStop or Keys.MediaNextTrack or Keys.MediaPreviousTrack => true,
            Keys.VolumeUp or Keys.VolumeDown or Keys.VolumeMute => true,
            Keys.Play or Keys.Pause or Keys.SelectMedia => true,
            >= Keys.F13 and <= Keys.F24 => true,
            _ => false,
        };

        public override string ToString()
        {
            if (!IsAssigned) return "";

            var sb = new StringBuilder();
            if ((Modifiers & HotkeyManager.MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((Modifiers & HotkeyManager.MOD_ALT) != 0) sb.Append("Alt+");
            if ((Modifiers & HotkeyManager.MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((Modifiers & HotkeyManager.MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(NameOf((Keys)Key));
            return sb.ToString();
        }

        /// <summary>
        /// How a screen reader should hear it. "Ctrl+Alt+P" read literally comes
        /// out as one run-together word in some voices; the spelled-out form is
        /// what NVDA says for a menu accelerator, so it is what this says too.
        /// </summary>
        public string Spoken => IsAssigned ? ToString().Replace("+", " plus ") : "not set";

        /// <summary>
        /// Reads a shortcut back. Anything unrecognised gives <see cref="None"/> —
        /// a shortcut that is simply not registered — rather than a guess.
        /// </summary>
        public static Shortcut Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return None;

            uint modifiers = 0;
            uint key = 0;

            foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;

                switch (part.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": modifiers |= HotkeyManager.MOD_CONTROL; continue;
                    case "alt":
                    case "menu": modifiers |= HotkeyManager.MOD_ALT; continue;
                    case "shift": modifiers |= HotkeyManager.MOD_SHIFT; continue;
                    case "win":
                    case "windows": modifiers |= HotkeyManager.MOD_WIN; continue;
                }

                uint parsed = ParseKey(part);

                // An unknown word, or a second key: the whole thing is meaningless
                // rather than partly right.
                if (parsed == 0 || key != 0) return None;
                key = parsed;
            }

            return key == 0 ? None : new Shortcut(modifiers, key);
        }

        private static uint ParseKey(string part)
        {
            if (part.Length == 1)
            {
                char c = char.ToUpperInvariant(part[0]);
                if (c is >= 'A' and <= 'Z') return (uint)(Keys.A + (c - 'A'));
                if (c is >= '0' and <= '9') return (uint)(Keys.D0 + (c - '0'));
                return 0;
            }

            // Enum.TryParse also accepts raw numbers and comma-separated lists,
            // neither of which is a key name — "80" would silently become P.
            foreach (char c in part)
                if (!char.IsLetterOrDigit(c)) return 0;
            if (char.IsDigit(part[0])) return 0;

            return Enum.TryParse<Keys>(part, ignoreCase: true, out var k) && IsUsable(k) ? (uint)k : 0;
        }

        /// <summary>A modifier on its own is not a key you can register.</summary>
        private static bool IsUsable(Keys key) => key switch
        {
            Keys.None or Keys.Modifiers => false,
            Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => false,
            Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => false,
            Keys.Menu or Keys.LMenu or Keys.RMenu => false,
            Keys.LWin or Keys.RWin => false,
            _ => true,
        };

        private static string NameOf(Keys key) => key switch
        {
            >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
            _ => key.ToString(),
        };
    }
}
