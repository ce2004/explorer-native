using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Asks for one line of text.
    ///
    /// Replaces Microsoft.VisualBasic.Interaction.InputBox, which is a legacy
    /// dialog with no association between its prompt and its field: a screen
    /// reader landing in the box has nothing to say about what the box is for.
    /// Here the label is the field's accessible name, so arriving in it says
    /// "Folder name, edit" and the caret is already there — no tabbing to find
    /// where to type.
    /// </summary>
    public sealed class PromptForm : Form
    {
        private readonly TextBox _box;

        public string Value => _box.Text.Trim();

        /// <param name="selectStem">
        /// Select only the part before the extension, the way renaming works
        /// everywhere else — typing replaces the name and keeps ".flac".
        /// </param>
        public PromptForm(string title, string label, string initial, bool selectStem = false)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 128);

            var prompt = new Label
            {
                Text = label,
                AutoSize = true,
                Location = new Point(12, 14),
            };

            _box = new TextBox
            {
                Text = initial,
                Location = new Point(12, 38),
                Width = ClientSize.Width - 24,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            };
            // What a screen reader reads on arriving in the field. The Label above
            // is decoration; this is the part that is actually spoken.
            _box.AccessibleName = label.TrimEnd(':');

            var ok = new Button
            {
                Text = "&OK",
                DialogResult = DialogResult.OK,
                Location = new Point(ClientSize.Width - 176, 78),
                Width = 78,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            };
            var cancel = new Button
            {
                Text = "&Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(ClientSize.Width - 90, 78),
                Width = 78,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            };

            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(prompt);
            Controls.Add(_box);
            Controls.Add(ok);
            Controls.Add(cancel);

            // The field first in the tab order and focused on arrival, so the
            // dialog opens ready to type into.
            _box.TabIndex = 0;
            ok.TabIndex = 1;
            cancel.TabIndex = 2;

            Shown += (_, _) =>
            {
                _box.Focus();

                if (selectStem)
                {
                    int dot = _box.Text.LastIndexOf('.');
                    // A leading dot is the whole name (".gitignore"), not an
                    // extension, so there is no stem to select separately.
                    if (dot > 0) _box.Select(0, dot);
                    else _box.SelectAll();
                }
                else _box.SelectAll();
            };
        }

        /// <summary>
        /// Shows the prompt and returns what was typed, or null if it was
        /// cancelled or left empty.
        /// </summary>
        public static string? Ask(IWin32Window owner, string title, string label,
                                  string initial = "", bool selectStem = false)
        {
            using var dialog = new PromptForm(title, label, initial, selectStem);
            if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
            return string.IsNullOrWhiteSpace(dialog.Value) ? null : dialog.Value;
        }
    }
}
