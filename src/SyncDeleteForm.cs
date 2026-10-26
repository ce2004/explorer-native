using System;
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
        private const int Margin12 = 12;

        private readonly TextBox _message;
        private readonly FlowLayoutPanel _buttons;

        public SyncDeleteChoice Choice { get; private set; } = SyncDeleteChoice.Cancel;

        public SyncDeleteForm(IReadOnlyList<DriveSyncPair> pairs, bool driveSide)
        {
            Text = pairs.Count == 1 ? "Folder is synced" : "Folders are synced";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
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
                Location = new Point(Margin12, Margin12),
                Size = new Size(536, 180),
                TabIndex = 0,
                AccessibleRole = AccessibleRole.StaticText,
                AccessibleName = Text,
            };
            _message = message;

            var remove = new Button { Text = "&Remove the sync, then delete", AutoSize = true, TabIndex = 1 };
            var anyway = new Button { Text = "&Delete anyway", AutoSize = true, TabIndex = 2 };
            var cancel = new Button
            {
                Text = "Cancel", DialogResult = DialogResult.Cancel,
                AutoSize = true, MinimumSize = new Size(84, 30), TabIndex = 3,
            };

            remove.Click += (_, _) => { Choice = SyncDeleteChoice.RemoveSyncThenDelete; DialogResult = DialogResult.OK; Close(); };
            anyway.Click += (_, _) => { Choice = SyncDeleteChoice.DeleteAnyway; DialogResult = DialogResult.OK; Close(); };

            // The buttons flow left to right, so a bigger font or DPI pushes
            // them apart instead of laying one over the next.
            _buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Location = new Point(Margin12 - 3, message.Bottom + 14),
                TabIndex = 1,
            };
            _buttons.Controls.AddRange(new Control[] { remove, anyway, cancel });

            Controls.AddRange(new Control[] { message, _buttons });
            AcceptButton = cancel;
            CancelButton = cancel;

            // The warning is read first; Enter then cancels.
            Shown += (_, _) =>
            {
                message.Focus();
                message.Select(0, 0);
            };
        }

        /// <summary>
        /// Grows the window to fit the buttons once the fonts are resolved,
        /// and widens the warning to match.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            _buttons.Top = _message.Bottom + 14;
            var row = _buttons.PreferredSize;
            int width = Math.Max(ClientSize.Width, _buttons.Left + row.Width + Margin12);
            int height = Math.Max(ClientSize.Height, _buttons.Top + row.Height + Margin12);
            ClientSize = new Size(width, height);
            _message.Width = ClientSize.Width - _message.Left - Margin12;
        }
    }
}
