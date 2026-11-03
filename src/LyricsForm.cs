using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// The playing Apple Music song's lyrics (5.1.0): a read-only text box NVDA
    /// reads line by line, titled with the song and artist. Escape closes it.
    /// </summary>
    internal sealed class LyricsForm : Form
    {
        public LyricsForm(string title, IReadOnlyList<string> lines)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Padding = new Padding(10);
            ClientSize = new Size(560, 480);
            MinimumSize = new Size(320, 240);

            var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = new Label { Text = "&Lyrics:", AutoSize = true, Margin = new Padding(3, 3, 3, 4) };
            var text = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                Dock = DockStyle.Fill,
                Text = string.Join("\r\n", lines),
                AccessibleName = "Lyrics",
                // Read-only boxes are grey by default; these are for reading.
                BackColor = SystemColors.Window,
            };
            var close = new Button { Text = "&Close", AutoSize = true, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right, AccessibleName = "Close" };
            CancelButton = close;
            layout.Controls.Add(label, 0, 0);
            layout.Controls.Add(text, 0, 1);
            layout.Controls.Add(close, 0, 2);
            Controls.Add(layout);

            // From the first line, with nothing selected (a selected whole text is read as one block).
            Shown += (_, _) => { text.Focus(); text.Select(0, 0); };
        }
    }
}
