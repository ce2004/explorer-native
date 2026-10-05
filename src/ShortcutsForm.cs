using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Every audio shortcut, on its own.
    ///
    /// Eighteen actions is a long page to walk past on the way to anything else,
    /// so the audio settings keep one button and the keys live here. It is also
    /// the only place that can tell whether a combination is already taken:
    /// checking needs every box at once, and a page that is only part of a larger
    /// dialog does not have them.
    /// </summary>
    public sealed class ShortcutsForm : Form
    {
        private readonly Settings _working;
        private readonly Dictionary<AudioAction, ShortcutBox> _boxes = new();
        private readonly Label _message;

        public ShortcutsForm(Settings working)
        {
            _working = working;

            Text = "Audio keyboard shortcuts";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MinimizeBox = false;
            MaximizeBox = false;
            Width = 660;
            Height = 620;

            var page = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(12),
            };

            Info(page,
                "These work everywhere in Windows, not only in this application — that is what makes " +
                "a player with no window usable. Move to a box and press the keys you want. Delete " +
                "clears one, and a cleared shortcut is simply not registered.");

            foreach (var action in AudioActions.All)
            {
                var box = new ShortcutBox(action.Label, Shortcut.Parse(action.Get(_working)));
                box.Captured += Say;
                box.InUse = candidate => WhoUses(candidate, action.Action);
                _boxes[action.Action] = box;
                Labelled(page, action.Label, box);
            }

            // Last, and not grey: a refusal has to be visible as well as spoken,
            // because the box it refers to looks exactly the same afterwards as
            // it did before.
            _message = new Label
            {
                Text = "",
                AutoSize = true,
                MaximumSize = new Size(600, 0),
                Margin = new Padding(3, 12, 3, 4),
            };
            _message.AccessibleName = "Last message";
            page.Controls.Add(_message);

            Info(page,
                "A combination already claimed by another application cannot be registered, and the " +
                "tray icon says which ones failed. The media keys work on their own, without Control " +
                "or Alt — but taking Volume Up that way means it no longer changes the volume of " +
                "Windows itself.");

            Info(page,
                "A shortcut can also register cleanly and still never fire, if something with a " +
                "keyboard hook takes it first — Control+Alt with the arrow keys is NVDA's table " +
                "navigation, and NVDA sees it before Windows does. Nothing can warn about that, so " +
                "if a shortcut does nothing, that is why: pick different keys.");

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 48,
                Padding = new Padding(8),
            };

            var ok = new Button { Text = "&OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "&Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            ok.Click += (_, _) => Apply();

            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(page);
            Controls.Add(buttons);
        }

        /// <summary>
        /// Which action already has this combination, or null when it is free.
        ///
        /// The window's own shortcut counts too. It is not on this page, and
        /// clashing with it is exactly the mistake somebody would make and then
        /// spend an afternoon on.
        /// </summary>
        private string? WhoUses(Shortcut candidate, AudioAction except)
        {
            // The window's own keys, and the ones every application relies on.
            // These matter more than a clash between two audio actions: a global
            // registration is exclusive machine-wide, so taking Ctrl+C here would
            // stop copying working everywhere, this application included.
            var reserved = ReservedShortcuts.OwnerOf(candidate);
            if (reserved != null) return reserved;

            foreach (var action in AudioActions.All)
            {
                if (action.Action == except) continue;
                if (!_boxes.TryGetValue(action.Action, out var box)) continue;
                if (box.Value.Equals(candidate)) return action.Label;
            }

            var window = new Shortcut(_working.HotkeyModifiers, _working.HotkeyKey);
            if (_working.GlobalHotkeyEnabled && window.IsAssigned && window.Equals(candidate))
                return "showing this window";

            return null;
        }

        private void Apply()
        {
            foreach (var action in AudioActions.All)
                if (_boxes.TryGetValue(action.Action, out var box))
                    action.Set(_working, box.Value.ToString());
        }

        private void Say(string message)
        {
            _message.Text = message;
            if (_working.SpeakEnabled) Speech.Speak(message, interrupt: true);
        }

        private static void Labelled(Control parent, string label, Control control)
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(3, 4, 3, 4),
            };
            row.Controls.Add(new Label
            {
                Text = label + ":",
                AutoSize = true,
                Margin = new Padding(0, 6, 6, 0),
                Width = 210,
            });
            row.Controls.Add(control);
            parent.Controls.Add(row);
        }

        private static void Info(Control parent, string text)
        {
            parent.Controls.Add(new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(600, 0),
                Margin = new Padding(3, 10, 3, 4),
                ForeColor = SystemColors.GrayText,
            });
        }
    }
}
