using System;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Help, Keyboard shortcuts (Ctrl+F1): the built-in keys as one list, laid
    /// out like the changelog. A heading line starts each section; the arrows
    /// read a line at a time, Ctrl+Down and Ctrl+Up jump between sections,
    /// Ctrl+C copies the line, and Escape, Enter or OK closes it.
    /// </summary>
    internal sealed class KeyboardShortcutsForm : Form
    {
        private readonly ListBox _lines;

        public KeyboardShortcutsForm()
        {
            Text = "Keyboard shortcuts";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;

            _lines = new ListBox
            {
                Dock = DockStyle.Fill,
                HorizontalScrollbar = true,
                IntegralHeight = false,
                AccessibleName = "Keyboard shortcuts",
                TabIndex = 0,
            };
            _lines.Items.AddRange(BuiltInShortcuts.Lines().ToArray());
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
                if (BuiltInShortcuts.IsHeading((string)_lines.Items[i]))
                {
                    _lines.SelectedIndex = i;
                    return;
                }
            }
            Speak(step > 0 ? "This is the last section" : "This is the first section");
        }

        private static void Speak(string text)
        {
            try { Speech.Speak(text, interrupt: true); } catch { }
        }
    }
}
