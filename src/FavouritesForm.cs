using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Favourites, Organise favourites: the list in Ctrl+Shift order. Alt+Up and
    /// Alt+Down move one, Delete removes it, Enter goes there. Every change is
    /// kept at once (<see cref="Changed"/>), so there is no OK; Escape closes.
    /// </summary>
    public sealed class FavouritesForm : Form
    {
        private readonly ListBox _list;
        private List<string> _folders;
        private bool _empty;

        /// <summary>Called with the new list after every move or removal.</summary>
        public Action<List<string>>? Changed { get; set; }

        /// <summary>The folder Enter chose, or null.</summary>
        public string? GoTo { get; private set; }

        public IReadOnlyList<string> Folders => _folders;

        public FavouritesForm(IReadOnlyList<string> folders)
        {
            _folders = Favourites.Clean(folders);
            Text = "Organise favourites";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(10);

            var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            var label = new Label { Text = "&Favourite folders:", AutoSize = true, Margin = new Padding(3, 3, 3, 4) };
            _list = new ListBox { Width = 520, Height = 220, IntegralHeight = false, HorizontalScrollbar = true };
            _list.AccessibleName = "Favourite folders";
            _list.AccessibleDescription = "Alt+Up and Alt+Down move a folder, Delete removes it, Enter goes there.";
            _list.KeyDown += List_KeyDown;
            _list.DoubleClick += (_, _) => Go();

            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
            void Make(string text, Action act)
            {
                var b = new Button { Text = text, AutoSize = true, Margin = new Padding(3, 3, 6, 3) };
                b.AccessibleName = text.Replace("&", "");
                b.Click += (_, _) => act();
                buttons.Controls.Add(b);
            }
            Make("&Go there", Go);
            Make("Move &up", () => MoveFolder(-1));
            Make("Move &down", () => MoveFolder(+1));
            Make("&Remove", Remove);
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(3, 3, 6, 3) };
            buttons.Controls.Add(close);
            CancelButton = close;

            layout.Controls.Add(label);
            layout.Controls.Add(_list);
            layout.Controls.Add(buttons);
            Controls.Add(layout);

            Fill(0);
            Shown += (_, _) => _list.Focus();
        }

        /// <summary>A row: "3, Downloads, C:\Users\Conner\Downloads", so the key is heard first.</summary>
        internal static string Row(IReadOnlyList<string> folders, int index)
        {
            var label = Favourites.Label(folders, index);
            var path = AppleMusic.Is(folders[index]) ? AppleMusic.DisplayPath(folders[index]) : folders[index];
            return string.Equals(label, path, StringComparison.OrdinalIgnoreCase)
                ? $"{index + 1}, {label}"
                : $"{index + 1}, {label}, {path}";
        }

        private void Fill(int select)
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            _empty = _folders.Count == 0;
            if (_empty) _list.Items.Add("No favourites yet. Press Ctrl+D in a folder to add it.");
            for (int i = 0; i < _folders.Count; i++) _list.Items.Add(Row(_folders, i));
            _list.EndUpdate();
            _list.SelectedIndex = Math.Clamp(select, 0, _list.Items.Count - 1);
        }

        private void List_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Alt && e.KeyCode is Keys.Up or Keys.Down)
            {
                MoveFolder(e.KeyCode == Keys.Up ? -1 : +1);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Delete && e.Modifiers == Keys.None)
            {
                Remove();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None)
            {
                Go();
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        private int Selected => _empty ? -1 : _list.SelectedIndex;

        private void MoveFolder(int by)
        {
            int at = Selected, to = at + by;
            if (at < 0 || to < 0 || to >= _folders.Count) return;
            _folders = Favourites.Move(_folders, at, by);
            Changed?.Invoke(_folders);
            // Every row's number may have changed its name (twins), so all are redone.
            Fill(to);
            _list.Focus();
        }

        private void Remove()
        {
            int at = Selected;
            if (at < 0) return;
            _folders = Favourites.RemoveAt(_folders, at);
            Changed?.Invoke(_folders);
            Fill(Math.Min(at, Math.Max(0, _folders.Count - 1)));
            _list.Focus();
        }

        private void Go()
        {
            int at = Selected;
            if (at < 0) return;
            GoTo = _folders[at];
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
