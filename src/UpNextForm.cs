using System;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Apple Music's Up next (Conner, 4.1.6): the songs to come, Play next
    /// choices first, then the rest of the list (shuffled when shuffle is on).
    /// Every change lands on the player at once, so there is no OK: Alt+Up and
    /// Alt+Down move a song, Delete takes it out, Enter plays it now (and the
    /// ones above it are passed over), Clear all empties it. Escape closes.
    /// </summary>
    public sealed class UpNextForm : Form
    {
        private readonly ListBox _list;
        private bool _empty;

        public UpNextForm()
        {
            Text = "Up next";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(10);

            var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            var label = new Label { Text = "&Songs to come:", AutoSize = true, Margin = new Padding(3, 3, 3, 4) };
            _list = new ListBox { Width = 520, Height = 320, IntegralHeight = false, HorizontalScrollbar = true };
            _list.AccessibleName = "Up next";
            _list.AccessibleDescription = "Alt+Up and Alt+Down move a song, Delete takes it out, Enter plays it now.";
            _list.KeyDown += List_KeyDown;
            _list.DoubleClick += (_, _) => PlayNow();

            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
            Button Make(string text, Action act)
            {
                var b = new Button { Text = text, AutoSize = true, Margin = new Padding(3, 3, 6, 3) };
                b.AccessibleName = text.Replace("&", "");
                b.Click += (_, _) => act();
                buttons.Controls.Add(b);
                return b;
            }
            Make("&Play now", PlayNow);
            Make("Move &up", () => MoveSong(-1));
            Make("Move &down", () => MoveSong(+1));
            Make("&Remove", Remove);
            Make("Clear &all", ClearAll);
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

        /// <summary>What the rows show, kept beside them: a library can put thousands here, so changes touch only their rows.</summary>
        private System.Collections.Generic.List<AppleMusicPlayer.UpNextSong> _songs = new();

        /// <summary>Reads Up next from the player and puts the cursor on <paramref name="select"/>.</summary>
        private void Fill(int select)
        {
            _songs = AppleMusicPlayer.UpNext();
            _list.BeginUpdate();
            _list.Items.Clear();
            _empty = _songs.Count == 0;
            if (_empty) _list.Items.Add(AppleMusicPlayer.IsActive ? "Nothing is up next" : "Nothing is playing");
            var rows = new object[_songs.Count];
            for (int i = 0; i < rows.Length; i++) rows[i] = Label(_songs[i]);
            _list.Items.AddRange(rows);
            _list.EndUpdate();
            _list.SelectedIndex = Math.Clamp(select, 0, _list.Items.Count - 1);
        }

        private static string Label(AppleMusicPlayer.UpNextSong song) => song.Chosen ? song.Name + ", play next" : song.Name;

        private void List_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Alt && e.KeyCode is Keys.Up or Keys.Down)
            {
                MoveSong(e.KeyCode == Keys.Up ? -1 : +1);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Delete && e.Modifiers == Keys.None)
            {
                Remove();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None)
            {
                PlayNow();
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        private int Selected => _empty ? -1 : _list.SelectedIndex;

        private void MoveSong(int delta)
        {
            int at = Selected, to = at + delta;
            if (at < 0 || to < 0 || to >= _songs.Count) return;
            int now = AppleMusicPlayer.MoveUpNext(at, delta, _songs[at].Path);
            if (now < 0) { Fill(at); _list.Focus(); return; }   // a song ended meanwhile: shown afresh
            // Each keeps the kind of its place: Play next choices stay in front.
            (_songs[at], _songs[to]) = (_songs[to] with { Chosen = _songs[at].Chosen }, _songs[at] with { Chosen = _songs[to].Chosen });
            _list.Items[at] = Label(_songs[at]);
            _list.Items[to] = Label(_songs[to]);
            _list.SelectedIndex = to;
            _list.Focus();
        }

        private void Remove()
        {
            int at = Selected;
            if (at < 0) return;
            if (!AppleMusicPlayer.RemoveUpNext(at, _songs[at].Path)) { Fill(at); _list.Focus(); return; }
            _songs.RemoveAt(at);
            if (_songs.Count == 0) { Fill(0); _list.Focus(); return; }
            _list.Items.RemoveAt(at);
            _list.SelectedIndex = Math.Min(at, _songs.Count - 1);
            _list.Focus();
        }

        private void ClearAll()
        {
            if (_empty) return;
            AppleMusicPlayer.ClearUpNext();
            Fill(0);
            _list.Focus();
        }

        private void PlayNow()
        {
            int at = Selected;
            if (at < 0) return;
            var said = AppleMusicPlayer.PlayUpNext(at, _songs[at].Path);
            if (said == null) { Fill(0); _list.Focus(); return; }
            Close();
            if (said.Length > 0) AppleMusicPlayer.Say?.Invoke("audio.play", said);
        }
    }
}
