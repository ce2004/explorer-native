using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Every audio shortcut, on its own.
    ///
    /// Over twenty actions is a long page to walk past on the way to anything else,
    /// so the audio settings keep one button and the keys live here. It is also
    /// the only place that can tell whether a combination is already taken:
    /// checking needs every box at once, and a page that is only part of a larger
    /// dialog does not have them.
    /// </summary>
    public sealed class ShortcutsForm : Form
    {
        private readonly Settings _working;
        private readonly Dictionary<AudioAction, ShortcutBox> _boxes = new();
        private readonly Dictionary<AudioAction, CheckBox> _global = new();
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
                "Move to a box and press the keys you want. Delete clears one, and a cleared shortcut " +
                "is simply not registered. With Global ticked, the shortcut works everywhere in Windows. " +
                "Untick it and the shortcut works only while this application is in front, and the keys " +
                "do their usual job everywhere else.");

            foreach (var action in AudioActions.All)
            {
                var box = new ShortcutBox(action.Label, Shortcut.Parse(action.Get(_working)));
                box.Captured += Say;
                box.InUse = candidate => WhoUses(candidate, action.Action);
                _boxes[action.Action] = box;

                var global = new SpokenCheckBox
                {
                    Text = "Global",
                    AutoSize = true,
                    Checked = AudioActions.IsGlobal(_working, action.Action),
                    Margin = new Padding(8, 4, 0, 0),
                    AccessibleName = action.Label + " global",
                };
                _global[action.Action] = global;
                Labelled(page, action.Label, box, global);
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

            // A shortcut the settings already had that can no longer be one (Ctrl+Up
            // for the volume, from before Ctrl with the arrows was kept for the list):
            // the keyboard starts in its box and says why, so the fix is one press away.
            Shown += (_, _) =>
            {
                var refused = new List<string>();
                ShortcutBox? first = null;
                foreach (var action in AudioActions.All)
                {
                    if (!_boxes.TryGetValue(action.Action, out var box) || !box.Value.IsAssigned) continue;
                    if (ReservedShortcuts.OwnerOf(box.Value) is not { } owner) continue;
                    refused.Add($"{action.Label} is {box.Value.Spoken}, which is kept for {owner}");
                    first ??= box;
                }
                if (first == null) return;
                first.Focus();
                _message.Text = string.Join(". ", refused) + ". Press new keys in each, or Delete to clear it.";
                if (_working.SpeakEnabled) Speech.Speak(_message.Text, interrupt: false);
            };
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

        /// <summary>
        /// What OK does. Only a box somebody pressed keys in is written: the rest
        /// keep the text the file has, so OK neither blanks a shortcut this build
        /// cannot read nor rewrites "ctrl+alt+m" as "Ctrl+Alt+M".
        /// </summary>
        internal void Apply()
        {
            foreach (var action in AudioActions.All)
            {
                if (_boxes.TryGetValue(action.Action, out var box) && box.Changed)
                    action.Set(_working, box.Value.ToString());
                if (_global.TryGetValue(action.Action, out var global) &&
                    global.Checked != AudioActions.IsGlobal(_working, action.Action))
                    AudioActions.SetGlobal(_working, action.Action, global.Checked);
            }
        }

        private void Say(string message)
        {
            _message.Text = message;
            if (_working.SpeakEnabled) Speech.Speak(message, interrupt: true);
        }

        private static void Labelled(Control parent, string label, Control control, Control? after = null)
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
            if (after != null) row.Controls.Add(after);
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
