using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Tools, Running transfers (Ctrl+Shift+T): every copy, move, compress,
    /// extract, convert and editor save still running, one line each. The
    /// first line says everything at once. Enter brings a transfer's own window
    /// forward, P pauses or resumes it, Delete asks to cancel it. The lines
    /// refresh every second without moving the cursor.
    /// </summary>
    internal sealed class RunningTransfersForm : Form
    {
        private static RunningTransfersForm? _open;

        private readonly ListView _list;
        private readonly Button _show;
        private readonly Button _pause;
        private readonly Button _cancel;
        private readonly System.Windows.Forms.Timer _timer;

        /// <summary>Row 0 is the overall sentence; row i is _forms[i - 1].</summary>
        private ProgressForm[] _forms = Array.Empty<ProgressForm>();
        private string[] _lines = Array.Empty<string>();

        /// <summary>
        /// Opens the list, or brings it forward if it is open. With nothing
        /// running it opens nothing and returns false, for the caller to say so.
        /// </summary>
        public static bool ShowFor(IWin32Window owner)
        {
            if (_open is { IsDisposed: false } open)
            {
                open.Activate();
                return true;
            }
            if (ProgressForm.Running.Count == 0) return false;
            _open = new RunningTransfersForm();
            _open.Show(owner);
            return true;
        }

        /// <summary>What Ctrl+Alt+T says: everything running, in one sentence.</summary>
        public static string Overall() => TransferText.Overall(Snapshot(ProgressForm.Running));

        private static List<(string Title, TransferProgress? Progress, bool Paused)> Snapshot(IReadOnlyList<ProgressForm> forms) =>
            forms.Where(f => !f.IsDisposed).Select(f => (f.Text, f.LastProgress, f.IsPaused)).ToList();

        private RunningTransfersForm()
        {
            Text = "Running transfers";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 260);

            var label = new Label { Text = "&Transfers:", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(6, 6, 6, 2) };

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                HeaderStyle = ColumnHeaderStyle.None,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                VirtualMode = true,
                // Read as a list, not a table: see FileListView.
                AccessibleRole = AccessibleRole.List,
                AccessibleName = "Transfers",
            };
            _list.Columns.Add("Transfer", 600);
            _list.RetrieveVirtualItem += (_, e) =>
                e.Item = new ListViewItem(e.ItemIndex < _lines.Length ? _lines[e.ItemIndex] : "");
            _list.KeyDown += List_KeyDown;
            _list.SelectedIndexChanged += (_, _) => UpdateButtons();
            _list.Resize += (_, _) => _list.Columns[0].Width = Math.Max(100, _list.ClientSize.Width - 4);

            _show = new Button { Text = "&Show its window", AutoSize = true };
            _show.Click += (_, _) => ShowFocused();
            _pause = new Button { Text = "&Pause", AutoSize = true };
            _pause.Click += (_, _) => PauseFocused();
            _cancel = new Button { Text = "&Cancel transfer", AutoSize = true };
            _cancel.Click += (_, _) => CancelFocused();
            var close = new Button { Text = "C&lose", AutoSize = true, DialogResult = DialogResult.None };
            close.Click += (_, _) => Close();

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(4),
            };
            buttons.Controls.AddRange(new Control[] { _show, _pause, _cancel, close });

            Controls.Add(_list);
            Controls.Add(label);
            Controls.Add(buttons);
            CancelButton = close;

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (_, _) => Reload();
            FormClosed += (_, _) =>
            {
                _timer.Stop();
                _timer.Dispose();
                if (_open == this) _open = null;
            };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Reload();
            // On the first transfer, past the overall line, when there is one.
            int start = _lines.Length > 1 ? 1 : 0;
            if (_lines.Length > 0) FocusRow(start);
            _timer.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _list.Focus();
        }

        /// <summary>
        /// Re-reads every transfer. The cursor stays on the transfer it was on;
        /// a changed line is redrawn, which NVDA does not read out on its own.
        /// </summary>
        private void Reload()
        {
            if (IsDisposed) return;
            var focusedForm = FocusedForm();
            int focusedIndex = _list.VirtualListSize > 0 ? FocusedIndex() : -1;

            var forms = ProgressForm.Running.Where(f => !f.IsDisposed).ToArray();
            var lines = new string[forms.Length + 1];
            lines[0] = TransferText.Overall(Snapshot(forms));
            for (int i = 0; i < forms.Length; i++) lines[i + 1] = forms[i].Describe();

            bool sameRows = forms.SequenceEqual(_forms);
            _forms = forms;
            _lines = lines;

            if (sameRows)
            {
                if (_list.VirtualListSize > 0) _list.RedrawItems(0, _list.VirtualListSize - 1, true);
                UpdateButtons();
                return;
            }

            _list.VirtualListSize = lines.Length;
            int keep = focusedForm != null ? Array.IndexOf(forms, focusedForm) + 1 : 0;
            if (keep <= 0) keep = Math.Clamp(focusedIndex, 0, lines.Length - 1);
            FocusRow(keep);
            _list.Invalidate();
            UpdateButtons();
        }

        private int FocusedIndex()
        {
            if (_list.SelectedIndices.Count > 0) return _list.SelectedIndices[0];
            return _list.FocusedItem?.Index ?? 0;
        }

        private void FocusRow(int index)
        {
            if (index < 0 || index >= _list.VirtualListSize) return;
            var item = _list.Items[index];
            item.Focused = true;
            item.Selected = true;
            item.EnsureVisible();
        }

        private ProgressForm? FocusedForm()
        {
            if (_list.VirtualListSize == 0) return null;
            int index = FocusedIndex();
            return index >= 1 && index <= _forms.Length ? _forms[index - 1] : null;
        }

        private void UpdateButtons()
        {
            var form = FocusedForm();
            bool live = form != null && !form.IsDisposed;
            _show.Visible = live;
            _cancel.Visible = live;
            // Only for a transfer that can pause, as in its own window.
            _pause.Visible = live && form!.Pause != null;
            if (_pause.Visible)
            {
                _pause.Text = form!.IsPaused ? "&Resume" : "&Pause";
                _pause.AccessibleName = form.IsPaused ? "Resume" : "Pause";
            }
        }

        private void List_KeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.KeyData)
            {
                case Keys.Enter: ShowFocused(); break;
                case Keys.Delete: CancelFocused(); break;
                case Keys.P: PauseFocused(); break;
                default: return;
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void ShowFocused() => FocusedForm()?.BringForward();

        private void PauseFocused()
        {
            var form = FocusedForm();
            if (form?.Pause == null) return;
            form.TogglePause();
            Reload();
        }

        /// <summary>Asks first, No by default: a slip of Delete must not stop a long copy.</summary>
        private void CancelFocused()
        {
            var form = FocusedForm();
            if (form == null || form.IsDisposed) return;
            var answer = MessageBox.Show(this,
                $"Do you want to cancel this? {form.Describe()}.",
                "Cancel transfer", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes && !form.IsDisposed) form.CancelTransfer();
            _list.Focus();
            Reload();
        }
    }
}
