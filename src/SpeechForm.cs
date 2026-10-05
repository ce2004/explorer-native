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
    /// This was the Notifications dialog, and it offered eight destinations per
    /// message: off, the status bar, speech, a tray balloon, and the five ways
    /// of combining them. Six of the eight named a balloon that no longer
    /// exists, and the remaining two — "status bar" and "off" — sound exactly
    /// the same to somebody who is listening rather than looking. So there is
    /// one question left, asked once per message: speak it, or do not.
    ///
    /// A hundred and fifty rows in a flow of labels would be unusable, so this
    /// is a category list beside a panel — the same shape as Preferences itself,
    /// and the same reason: a ListBox announces the category and its position
    /// and nothing else, where a TabControl re-announces itself on every arrow
    /// press.
    ///
    /// Every row can be previewed. A setting whose effect you cannot hear until
    /// the thing happens is a setting nobody will ever be confident they have
    /// set correctly, and half of these only happen when something has gone
    /// wrong.
    /// </summary>
    public sealed class SpeechForm : Form
    {
        private readonly Settings _working;
        private readonly Dictionary<string, ComboBox> _choosers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, NotificationChannel> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Control> _panels = new();

        public SpeechForm(Settings working)
        {
            _working = working;

            Text = "What is spoken";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MinimizeBox = false;
            MaximizeBox = false;
            Width = 900;
            Height = 640;

            var saved = Notifications.ParseOverrides(_working.SpeechOverrides);
            foreach (var info in Notifications.All)
                _values[info.Id] = saved.TryGetValue(info.Id, out var c) ? c : info.Default;

            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0) };
            var categories = Notifications.Categories;

            foreach (var category in categories)
            {
                var page = BuildCategory(category);
                page.Dock = DockStyle.Fill;
                page.Visible = false;
                page.AccessibleName = category + " messages";
                _panels.Add(page);
                host.Controls.Add(page);
            }

            var list = new ListBox { Dock = DockStyle.Left, Width = 220, IntegralHeight = false };
            list.AccessibleName = "Category";
            foreach (var category in categories)
                list.Items.Add($"{category} ({Notifications.All.Count(n => n.Category == category)})");

            list.SelectedIndexChanged += (_, _) =>
            {
                for (int i = 0; i < _panels.Count; i++) _panels[i].Visible = i == list.SelectedIndex;
            };
            list.SelectedIndex = 0;

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 48,
                Padding = new Padding(8),
            };

            var ok = new Button { Text = "&OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "&Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };

            var allOn = new Button { Text = "Speak this categor&y", AutoSize = true };
            var allOff = new Button { Text = "Silence this categor&y", AutoSize = true };
            var defaults = new Button { Text = "Category &defaults", AutoSize = true };

            allOn.Click += (_, _) => SetCategory(categories[list.SelectedIndex], _ => NotificationChannel.Speech);
            allOff.Click += (_, _) => SetCategory(categories[list.SelectedIndex], _ => NotificationChannel.None);
            defaults.Click += (_, _) => SetCategory(categories[list.SelectedIndex], n => n.Default);

            ok.Click += (_, _) => _working.SpeechOverrides = Notifications.FormatOverrides(_values);

            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(defaults);
            buttons.Controls.Add(allOff);
            buttons.Controls.Add(allOn);

            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(host);
            Controls.Add(list);
            Controls.Add(buttons);

            Shown += (_, _) => list.Focus();
        }

        private FlowLayoutPanel BuildCategory(string category)
        {
            var page = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(10),
            };

            // Built once for the whole dialog rather than per row. This used to
            // be Choices.ToList().IndexOf(...) inside the loop and again inside
            // every SelectedIndexChanged — a fresh list allocated to answer a
            // question about a two-item collection, a hundred and fifty times.
            var choices = Notifications.Choices;

            foreach (var info in Notifications.All.Where(n => n.Category == category))
            {
                var row = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    AutoSize = true,
                    WrapContents = false,
                    Margin = new Padding(3, 3, 3, 3),
                };

                row.Controls.Add(new Label
                {
                    Text = info.Name + ":",
                    AutoSize = true,
                    Width = 260,
                    Margin = new Padding(0, 6, 6, 0),
                });

                var chooser = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Width = 130,
                };
                chooser.AccessibleName = info.Raised ? info.Name : info.Name + ", not raised yet";
                foreach (var choice in choices) chooser.Items.Add(Notifications.Describe(choice));

                chooser.SelectedIndex = IndexOfChoice(_values[info.Id]);

                var id = info.Id;
                chooser.SelectedIndexChanged += (_, _) =>
                    _values[id] = choices[Math.Max(0, chooser.SelectedIndex)];

                _choosers[info.Id] = chooser;
                row.Controls.Add(chooser);

                // Hearing it is the point. Half of these only happen when
                // something has gone wrong, and a setting you cannot check
                // without breaking something is a setting nobody trusts.
                var preview = new Button { Text = "Preview", AutoSize = true, Margin = new Padding(6, 1, 3, 1) };
                preview.AccessibleName = "Preview " + info.Name;
                preview.Click += (_, _) => Preview(info);
                row.Controls.Add(preview);

                // Said in the label as well as in the accessible name: most of
                // these are catalogued but not yet raised by anything, and
                // configuring one of those should not look identical to
                // configuring one that works.
                var sample = new Label
                {
                    Text = info.Raised ? info.Preview() : info.Preview() + "   — not raised yet",
                    AutoSize = true,
                    MaximumSize = new Size(280, 0),
                    ForeColor = SystemColors.GrayText,
                    Margin = new Padding(8, 6, 3, 0),
                };
                sample.AccessibleName = info.Name + " example";
                row.Controls.Add(sample);

                page.Controls.Add(row);
            }

            return page;
        }

        private static int IndexOfChoice(NotificationChannel value)
        {
            var choices = Notifications.Choices;
            for (int i = 0; i < choices.Count; i++)
                if (choices[i] == value) return i;
            return 0;
        }

        private void Preview(NotificationInfo info)
        {
            var text = info.Preview();

            // Spoken even when the setting says otherwise. This is a preview
            // button: refusing to demonstrate the thing being configured because
            // it is currently switched off is not caution, it is a broken button.
            if (_working.SpeakEnabled) Speech.Speak(text, interrupt: true);

            Text = $"What is spoken — {Notifications.Describe(_values[info.Id])}: {text}";
        }

        private void SetCategory(string category, Func<NotificationInfo, NotificationChannel> choose)
        {
            foreach (var info in Notifications.All.Where(n => n.Category == category))
            {
                _values[info.Id] = choose(info);
                if (_choosers.TryGetValue(info.Id, out var chooser))
                    chooser.SelectedIndex = IndexOfChoice(_values[info.Id]);
            }
        }
    }
}
