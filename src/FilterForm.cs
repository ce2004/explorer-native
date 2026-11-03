using System;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// The box for Filter this folder (Ctrl+F). Each pause in typing hands the
    /// text to <c>onChange</c>, which narrows the list behind it. Enter keeps
    /// the filter (DialogResult.OK); Escape clears it (Cancel).
    /// </summary>
    internal sealed class FilterForm : Form
    {
        /// <summary>How long typing has to pause before the list follows.</summary>
        public const int PauseMs = 300;

        private readonly TextBox _box;
        private readonly Timer _pause = new() { Interval = PauseMs };
        private readonly Action<string> _onChange;
        private string _applied;

        public string Value => _box.Text;

        public FilterForm(string folderName, string initial, Action<string> onChange)
        {
            _onChange = onChange;
            _applied = initial;

            Text = "Filter this folder";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            var label = $"Show only items in {folderName} matching:";
            var prompt = new Label { Text = label, AutoSize = true, Location = new Point(12, 14) };
            int width = Math.Min(Math.Max(460, prompt.PreferredWidth + 24), 900);
            ClientSize = new Size(width, 128);

            _box = new TextBox
            {
                Text = initial,
                Location = new Point(12, 38),
                Width = width - 24,
                AccessibleName = label.TrimEnd(':'),
                AccessibleDescription = "Words, size, modified and kind work as in Search. Enter keeps the filter, Escape clears it",
            };

            var ok = new Button { Text = "&OK", DialogResult = DialogResult.OK, Location = new Point(width - 176, 78), Width = 78 };
            var clear = new Button { Text = "C&lear", DialogResult = DialogResult.Cancel, Location = new Point(width - 90, 78), Width = 78 };
            AcceptButton = ok;
            CancelButton = clear;

            Controls.Add(prompt);
            Controls.Add(_box);
            Controls.Add(ok);
            Controls.Add(clear);
            _box.TabIndex = 0;
            ok.TabIndex = 1;
            clear.TabIndex = 2;

            _box.TextChanged += (_, _) => { _pause.Stop(); _pause.Start(); };
            _pause.Tick += (_, _) => Flush();
            Shown += (_, _) => { _box.Focus(); _box.SelectAll(); };
            FormClosing += (_, _) =>
            {
                _pause.Stop();
                if (DialogResult == DialogResult.OK) Flush();
            };
        }

        /// <summary>Hands over what is typed now, if it changed.</summary>
        private void Flush()
        {
            _pause.Stop();
            var text = _box.Text;
            if (string.Equals(text, _applied, StringComparison.Ordinal)) return;
            _applied = text;
            try { _onChange(text); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _pause.Dispose();
            base.Dispose(disposing);
        }
    }
}
