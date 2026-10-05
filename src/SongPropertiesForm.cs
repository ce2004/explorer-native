using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// What a song says about itself.
    ///
    /// A list rather than a grid of labelled boxes, because a list is one thing
    /// to arrow through and each row reads as a sentence — "Artist, 10cc" — which
    /// is the same rule the file list is built on. The Windows properties sheet
    /// shows the same numbers, on a tab that has to be found first.
    ///
    /// The reading happens on a worker and the list fills in when it lands. A
    /// property store opens the file, and the file can be on a share whose server
    /// has gone away.
    /// </summary>
    public sealed class SongPropertiesForm : Form
    {
        private readonly ListView _list;
        private readonly string _path;
        private IReadOnlyList<SongProperty> _rows = Array.Empty<SongProperty>();

        public SongPropertiesForm(string path)
        {
            _path = path;

            Text = "Song properties — " + Path.GetFileName(path);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MinimizeBox = false;
            MaximizeBox = false;
            Width = 620;
            Height = 520;

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
            };
            _list.Columns.Add("Property", 180);
            _list.Columns.Add("Value", 380);
            _list.AccessibleName = "Song properties";

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 48,
                Padding = new Padding(8),
            };

            var close = new Button { Text = "&Close", DialogResult = DialogResult.Cancel, AutoSize = true };
            var copy = new Button { Text = "Copy &all", AutoSize = true };
            copy.Click += (_, _) => CopyEverything();

            buttons.Controls.Add(close);
            buttons.Controls.Add(copy);

            // Close first in the tab order, so shift-tabbing out of the list
            // reaches the way out rather than walking back round the buttons.
            close.TabIndex = 0;
            copy.TabIndex = 1;
            _list.TabIndex = 2;

            Controls.Add(_list);
            Controls.Add(buttons);

            AcceptButton = close;
            CancelButton = close;

            // Guarded, because this is `async void` in all but name: an exception
            // out of an event handler has nowhere to go but
            // Application.ThreadException, which puts the crash dialog up and
            // takes the window with it. A properties sheet that cannot read its
            // tags should show what it has and stop, not end the session.
            Shown += async (_, _) =>
            {
                try { await LoadAsync(); }
                catch (Exception ex)
                {
                    try
                    {
                        _list.Items.Clear();
                        _list.Items.Add(new ListViewItem(new[] { "Could not read", ex.Message }));
                    }
                    catch { }
                }
            };
        }

        private async Task LoadAsync()
        {
            _list.Items.Add(new ListViewItem(new[] { "Reading", Path.GetFileName(_path) }));
            _list.Focus();

            var path = _path;
            var rows = await Task.Run(() => AudioTags.Read(path));
            if (IsDisposed) return;

            _rows = rows;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var row in rows)
                _list.Items.Add(new ListViewItem(new[] { row.Name, row.Value }));
            _list.EndUpdate();

            if (_list.Items.Count > 0)
            {
                _list.Items[0].Selected = true;
                _list.Items[0].Focused = true;
            }

            // Focus the list *after* it has rows, or the screen reader announces
            // an empty list and then never mentions the contents arriving.
            _list.Focus();
        }

        private void CopyEverything()
        {
            var text = new System.Text.StringBuilder();
            foreach (var row in _rows) text.AppendLine($"{row.Name}: {row.Value}");

            if (text.Length == 0) return;

            if (!ClipboardInterop.SetText(text.ToString()))
                MessageBox.Show(this, "Another application is holding the clipboard.",
                    "Copy all", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
