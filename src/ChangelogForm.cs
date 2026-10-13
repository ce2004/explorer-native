using System;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Help, Changelog: every version, newest first, from the CHANGES.txt built
    /// into this executable. No network, so it opens at once and works offline.
    ///
    /// One list, one change per line, with a "Version 2.3.3" line heading each
    /// version (Kova's layout). The arrows read line by line; Ctrl+Down and
    /// Ctrl+Up jump to the next or previous version; Ctrl+C copies the line.
    /// Escape, Enter or OK closes it.
    /// </summary>
    internal sealed class ChangelogForm : Form
    {
        private readonly ListBox _lines;

        public ChangelogForm()
        {
            Text = "Changelog";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;

            var lines = Changelog.Lines(Changelog.Embedded(), Updater.CurrentText);
            if (lines.Count == 0) lines.Add("This build has no changelog in it.");

            _lines = new ListBox
            {
                Dock = DockStyle.Fill,
                HorizontalScrollbar = true,
                IntegralHeight = false,
                AccessibleName = "Changelog",
                TabIndex = 0,
            };
            _lines.Items.AddRange(lines.ToArray());
            _lines.SelectedIndex = 0;
            _lines.KeyDown += OnListKeyDown;

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, TabIndex = 1 };
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Padding = new Padding(8),
            };
            bar.Controls.Add(ok);

            Controls.Add(_lines);
            Controls.Add(bar);
            AcceptButton = ok;
            CancelButton = ok;

            // Wide enough for a typical line at this font, tall enough for a page.
            var line = TextRenderer.MeasureText("M", Font);
            ClientSize = new Size(Math.Max(640, line.Width * 60), Math.Max(420, line.Height * 22) + bar.PreferredSize.Height);

            Shown += (_, _) => _lines.Focus();
        }

        private void OnListKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                e.Handled = e.SuppressKeyPress = true;
                if (_lines.SelectedItem is string text)
                {
                    try { Clipboard.SetText(text); Speak("Copied"); }
                    catch { Speak("Could not copy, another application is holding the clipboard"); }
                }
                return;
            }

            if (!e.Control || (e.KeyCode != Keys.Down && e.KeyCode != Keys.Up)) return;
            e.Handled = e.SuppressKeyPress = true;

            int step = e.KeyCode == Keys.Down ? 1 : -1;
            for (int i = _lines.SelectedIndex + step; i >= 0 && i < _lines.Items.Count; i += step)
            {
                if (Changelog.IsHeading((string)_lines.Items[i]))
                {
                    _lines.SelectedIndex = i;
                    return;
                }
            }
            Speak(step > 0 ? "No more versions" : "This is the newest version");
        }

        private static void Speak(string text)
        {
            try { Speech.Speak(text, interrupt: true); } catch { }
        }
    }
}
