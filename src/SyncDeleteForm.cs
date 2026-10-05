using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>What to do about a delete that would cut an end off a sync pair.</summary>
    internal enum SyncDeleteChoice
    {
        Cancel,

        /// <summary>Remove the pair and its remembered state, then delete this side only.</summary>
        RemoveSyncThenDelete,

        /// <summary>Delete; with deletes on, the other side's folder goes too. The pair is removed.</summary>
        DeleteAnyway,
    }

    /// <summary>
    /// The warning before deleting, in Explorer Native, a folder tied to a sync
    /// pair. A small form rather than a MessageBox so the buttons can say what
    /// they do. Cancel is the default and Escape.
    /// </summary>
    internal sealed class SyncDeleteForm : Form
    {
        public SyncDeleteChoice Choice { get; private set; } = SyncDeleteChoice.Cancel;

        public SyncDeleteForm(IReadOnlyList<DriveSyncPair> pairs, bool driveSide)
        {
            Text = pairs.Count == 1 ? "Folder is synced" : "Folders are synced";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 250);
            Font = SystemFonts.MessageBoxFont;

            var message = new TextBox
            {
                Text = SyncSafety.DeleteWarning(pairs, driveSide).Replace("\n", "\r\n"),
                ReadOnly = true,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                Location = new Point(12, 12),
                Size = new Size(536, 180),
                TabIndex = 0,
                AccessibleRole = AccessibleRole.StaticText,
                AccessibleName = Text,
            };

            var remove = new Button { Text = "&Remove the sync, then delete", AutoSize = true, Location = new Point(12, 206), TabIndex = 1 };
            var anyway = new Button { Text = "&Delete anyway", AutoSize = true, Location = new Point(250, 206), TabIndex = 2 };
            var cancel = new Button
            {
                Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(464, 206),
                Size = new Size(84, 30), TabIndex = 3,
            };

            remove.Click += (_, _) => { Choice = SyncDeleteChoice.RemoveSyncThenDelete; DialogResult = DialogResult.OK; Close(); };
            anyway.Click += (_, _) => { Choice = SyncDeleteChoice.DeleteAnyway; DialogResult = DialogResult.OK; Close(); };

            Controls.AddRange(new Control[] { message, remove, anyway, cancel });
            AcceptButton = cancel;
            CancelButton = cancel;

            // The warning is read first; Enter then cancels.
            Shown += (_, _) =>
            {
                message.Focus();
                message.Select(0, 0);
            };
        }
    }
}
