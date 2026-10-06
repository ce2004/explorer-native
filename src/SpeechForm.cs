using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Every message the application can produce, and whether it is spoken.
    ///
    /// A category list beside one checklist: tick a message to hear it. The
    /// checklist is a single control refilled when the category changes, not a
    /// row of controls per message. It used to be a combo box, a label, a
    /// preview button and an example label for every one of nearly two hundred
    /// messages, a thousand windows in all, which took over two seconds to open
    /// and a quarter of a second to switch category under a screen reader.
    ///
    /// A ListBox for the categories rather than a TabControl, for the reason
    /// Preferences gives: it announces the category and its position and
    /// nothing else. A checked list box is read as "checked" or "not checked"
    /// by NVDA, and Space toggles it, so the one question this window asks is
    /// answered with one key.
    ///
    /// Preview speaks the selected message's example even when it is switched
    /// off: a setting whose effect cannot be heard until the thing happens is a
    /// setting nobody trusts, and half of these only happen when something has
    /// gone wrong.
    /// </summary>
    public sealed class SpeechForm : Form
    {
        private readonly Settings _working;
        private readonly Dictionary<string, NotificationChannel> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly ListBox _categories;
        private readonly CheckedListBox _messages;
        private readonly TextBox _example;
        private readonly List<NotificationInfo> _shown = new();
        private bool _filling;

        public SpeechForm(Settings working)
        {
            _working = working;

            Text = "What is spoken";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MinimizeBox = false;
            MaximizeBox = false;
            Width = 760;
            Height = 560;
            DoubleBuffered = true;

            var saved = Notifications.ParseOverrides(_working.SpeechOverrides);
            foreach (var info in Notifications.All)
                _values[info.Id] = saved.TryGetValue(info.Id, out var c) ? c : info.Default;

            SuspendLayout();

            _categories = new ListBox
            {
                Dock = DockStyle.Left,
                Width = 240,
                IntegralHeight = false,
                AccessibleName = "Category",
                TabIndex = 0,
            };
            foreach (var category in Notifications.Categories)
                _categories.Items.Add(category);

            _messages = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                CheckOnClick = false,
                AccessibleName = "Messages. Checked ones are spoken",
                TabIndex = 1,
            };

            _example = new TextBox
            {
                Dock = DockStyle.Bottom,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                AccessibleName = "Example",
                TabIndex = 2,
            };

            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 4) };
            right.Controls.Add(_messages);
            right.Controls.Add(_example);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 48,
                Padding = new Padding(8),
                TabIndex = 3,
            };

            var ok = new Button { Text = "&OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "&Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var preview = new Button { Text = "&Preview", AutoSize = true };
            var allOn = new Button { Text = "Speak &all in this category", AutoSize = true };
            var allOff = new Button { Text = "&Silence all in this category", AutoSize = true };
            var defaults = new Button { Text = "Category &defaults", AutoSize = true };

            preview.Click += (_, _) => Preview();
            allOn.Click += (_, _) => SetCategory(_ => NotificationChannel.Speech);
            allOff.Click += (_, _) => SetCategory(_ => NotificationChannel.None);
            defaults.Click += (_, _) => SetCategory(n => n.Default);
            ok.Click += (_, _) => Apply();

            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(defaults);
            buttons.Controls.Add(allOff);
            buttons.Controls.Add(allOn);
            buttons.Controls.Add(preview);

            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(right);
            Controls.Add(_categories);
            Controls.Add(buttons);

            _categories.SelectedIndexChanged += (_, _) => Fill();
            _messages.ItemCheck += (_, e) =>
            {
                if (_filling || e.Index < 0 || e.Index >= _shown.Count) return;
                _values[_shown[e.Index].Id] =
                    e.NewValue == CheckState.Checked ? NotificationChannel.Speech : NotificationChannel.None;
            };
            _messages.SelectedIndexChanged += (_, _) => ShowExample();

            _categories.SelectedIndex = 0;
            ResumeLayout(true);

            Shown += (_, _) => _categories.Focus();
        }

        /// <summary>
        /// What OK does. Nothing changed leaves the text exactly as it was; a
        /// change keeps every entry this build does not know, because a newer
        /// build wrote it and will want it back.
        /// </summary>
        internal void Apply()
        {
            var opened = Notifications.ParseOverrides(_working.SpeechOverrides);
            bool changed = false;
            foreach (var info in Notifications.All)
            {
                var was = opened.TryGetValue(info.Id, out var c) ? c : info.Default;
                if (_values[info.Id] != was) { changed = true; break; }
            }
            if (!changed) return;

            var known = new HashSet<string>(Notifications.All.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
            var unknown = (_working.SpeechOverrides ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(part =>
                {
                    int equals = part.IndexOf('=');
                    return equals > 0 && !known.Contains(part[..equals].Trim());
                });

            var written = Notifications.FormatOverrides(_values);
            _working.SpeechOverrides = string.Join(";",
                new[] { written }.Where(s => s.Length > 0).Concat(unknown));
        }

        /// <summary>How many messages the current category lists. For the tests.</summary>
        internal int ShownCount => _shown.Count;

        /// <summary>Whether the message in the list at <paramref name="index"/> is spoken. For the tests.</summary>
        internal bool IsChecked(int index) => _messages.GetItemChecked(index);

        /// <summary>The category list. For the tests.</summary>
        internal ListBox CategoryList => _categories;

        /// <summary>
        /// The current category's messages, as one list. One control refilled,
        /// inside BeginUpdate, so a switch is one repaint however many there are.
        /// </summary>
        private void Fill()
        {
            int at = _categories.SelectedIndex;
            if (at < 0) return;
            var category = Notifications.Categories[at];

            _filling = true;
            _messages.BeginUpdate();
            try
            {
                _messages.Items.Clear();
                _shown.Clear();
                foreach (var info in Notifications.All)
                {
                    if (info.Category != category) continue;
                    _shown.Add(info);
                    _messages.Items.Add(info.Name, _values[info.Id] == NotificationChannel.Speech);
                }
                if (_messages.Items.Count > 0) _messages.SelectedIndex = 0;
            }
            finally
            {
                _messages.EndUpdate();
                _filling = false;
            }

            ShowExample();
        }

        private void ShowExample()
        {
            int i = _messages.SelectedIndex;
            _example.Text = i >= 0 && i < _shown.Count ? "Example: " + _shown[i].Preview() : "";
        }

        private void Preview()
        {
            int i = _messages.SelectedIndex;
            if (i < 0 || i >= _shown.Count) return;

            // Spoken even when the message is switched off: a preview button that
            // refuses to demonstrate the thing being configured is a broken button.
            if (_working.SpeakEnabled) Speech.Speak(_shown[i].Preview(), interrupt: true);
        }

        private void SetCategory(Func<NotificationInfo, NotificationChannel> choose)
        {
            foreach (var info in _shown) _values[info.Id] = choose(info);

            _filling = true;
            _messages.BeginUpdate();
            try
            {
                for (int i = 0; i < _shown.Count; i++)
                    _messages.SetItemChecked(i, _values[_shown[i].Id] == NotificationChannel.Speech);
            }
            finally
            {
                _messages.EndUpdate();
                _filling = false;
            }
        }
    }
}
