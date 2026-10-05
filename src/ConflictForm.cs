using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Asks what to do about names that are already in the destination.
    ///
    /// This is what `PasteConflictPolicy.Ask` was always supposed to reach and
    /// never did. `RoboCopyEngine` carried a comment saying "Ask, which the
    /// caller resolves before us" — describing a contract nothing implemented —
    /// so the option sat in Preferences behaving exactly like "keep both".
    /// A setting that cannot change what happens is worse than no setting,
    /// because somebody will pick it to protect a file and be told nothing when
    /// it does not.
    ///
    /// **Asked once for the whole paste, never once per file.** Two hundred
    /// collisions is two hundred modal dialogs, each one read out in full, on a
    /// window driven by keyboard — and the second opens inside the first one's
    /// modal loop, which this codebase has already measured going wrong with
    /// two. The list of what clashes goes in the label instead.
    ///
    /// A drop-down of sentences rather than a row of buttons, for the reason
    /// every other list in this application is one: it announces the choice and
    /// its position, arrows move through it, and it cannot hold an answer that
    /// was never offered. Escape and Cancel both mean "do nothing at all", which
    /// is the answer somebody reaches for when the list of names is a surprise.
    /// </summary>
    public sealed class ConflictForm : Form
    {
        private readonly ComboBox _choice;

        /// <summary>
        /// The options, in the order they are offered. Keeping both is first and
        /// therefore the default: it is the only one of the three that cannot
        /// destroy anything, and it is what the broken version silently did.
        /// </summary>
        private static readonly (string Text, FileOperations.ConflictChoice Choice)[] Options =
        {
            // First, and so the default, because it is what somebody pasting the
            // same folder a second time almost always means — and because it is
            // the only one of the four that neither destroys anything nor leaves
            // anything behind. A transfer that stopped part way is finished by
            // choosing this and nothing else.
            ("Fill in what is missing — go into folders that are already there",
                FileOperations.ConflictChoice.FillGaps),
            ("Keep both — the new one gets a number", FileOperations.ConflictChoice.Rename),
            ("Replace what is already there", FileOperations.ConflictChoice.Overwrite),
            ("Skip these entirely and keep what is there", FileOperations.ConflictChoice.Skip),
        };

        public FileOperations.ConflictChoice Choice =>
            Options[Math.Clamp(_choice.SelectedIndex, 0, Options.Length - 1)].Choice;

        public ConflictForm(IReadOnlyList<string> clashing, string destinationName)
        {
            Text = "Already there";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 150);

            var question = Describe(clashing, destinationName);

            var prompt = new Label
            {
                Text = question,
                AutoSize = false,
                Location = new Point(12, 12),
                Size = new Size(ClientSize.Width - 24, 46),
            };

            _choice = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(12, 64),
                Width = ClientSize.Width - 24,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            };
            foreach (var option in Options) _choice.Items.Add(option.Text);
            _choice.SelectedIndex = 0;

            // The label is decoration; this is what is spoken on arrival, and it
            // has to carry the question because the box is where the keyboard
            // lands.
            _choice.AccessibleName = question;

            var ok = new Button
            {
                Text = "&OK",
                DialogResult = DialogResult.OK,
                Location = new Point(ClientSize.Width - 176, 104),
                Width = 78,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            };
            var cancel = new Button
            {
                Text = "&Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(ClientSize.Width - 90, 104),
                Width = 78,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            };

            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(prompt);
            Controls.Add(_choice);
            Controls.Add(ok);
            Controls.Add(cancel);

            _choice.TabIndex = 0;
            ok.TabIndex = 1;
            cancel.TabIndex = 2;

            Shown += (_, _) => _choice.Focus();
        }

        /// <summary>
        /// Grows the window until the question and the list are both on it.
        ///
        /// The same lesson `ProgressForm` learned: a fixed height fits at 100%
        /// scaling and clips at 150%, and a clipped control is still built, still
        /// reachable with Tab and still read out perfectly — which is the one kind
        /// of missing control that is hard to notice from either direction.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            int wanted = _choice.Bottom + 12 + 30 + 12 + Padding.Vertical;
            if (ClientSize.Height < wanted) ClientSize = new Size(ClientSize.Width, wanted);
        }

        /// <summary>
        /// The question, naming what clashes.
        ///
        /// One name is the whole message and is worth saying; a handful are worth
        /// listing; past that the count is the story, because a screen reader
        /// reading forty file names before the question is a dialog nobody can
        /// answer. The destination is named too — a paste can land in the pane
        /// you were not looking at.
        /// </summary>
        internal static string Describe(IReadOnlyList<string> clashing, string destinationName)
        {
            var where = string.IsNullOrEmpty(destinationName) ? "the destination" : $"\"{destinationName}\"";

            // Nothing named, because nothing could be looked up. Asked anyway,
            // and this is the sentence that makes that honest: a Google Drive
            // folder that will not list is a folder we cannot rule a collision
            // out of, and the old answer was to assume there was none and
            // quietly keep both — which on Drive means a second file with the
            // same name as the first, because Drive lets a folder hold two.
            if (clashing.Count == 0)
                return $"Google Drive would not say what is already in {where}. " +
                       "What should happen if something is?";

            if (clashing.Count == 1)
                return $"\"{clashing[0]}\" is already in {where}. What should happen?";

            if (clashing.Count <= 4)
                return $"{string.Join(", ", clashing.Select(c => $"\"{c}\""))} are already in " +
                       $"{where}. What should happen?";

            return $"{clashing.Count} items are already in {where}, including " +
                   $"\"{clashing[0]}\". What should happen?";
        }

        /// <summary>
        /// Shows it and returns the choice, or Cancel when the paste should not
        /// happen at all.
        /// </summary>
        public static FileOperations.ConflictChoice Ask(
            IWin32Window owner, IReadOnlyList<string> clashing, string destinationName)
        {
            using var dialog = new ConflictForm(clashing, destinationName);
            return dialog.ShowDialog(owner) == DialogResult.OK
                ? dialog.Choice
                : FileOperations.ConflictChoice.Cancel;
        }
    }
}
