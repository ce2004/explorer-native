using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Preferences, Audio, Choose formats to play: every file type the player
    /// can play, as a list of check boxes. Space checks or unchecks one; the
    /// checked ones are what Enter plays in Explorer Native instead of opening
    /// in another application.
    /// </summary>
    internal sealed class FormatsForm : Form
    {
        private readonly CheckedListBox _list;

        /// <summary>The checked extensions, separated by semicolons, after OK.</summary>
        public string Extensions { get; private set; }

        public FormatsForm(string current)
        {
            Extensions = current;

            Text = "Formats to play";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(300, 380);
            Font = SystemFonts.MessageBoxFont;

            var supported = Split(AudioFiles.DefaultExtensions);
            var chosen = new HashSet<string>(
                Split(string.IsNullOrWhiteSpace(current) ? AudioFiles.DefaultExtensions : current),
                StringComparer.OrdinalIgnoreCase);

            var label = new Label { Text = "&Formats to play:", AutoSize = true, Location = new Point(12, 12) };
            _list = new CheckedListBox
            {
                Location = new Point(12, 32),
                Size = new Size(276, 300),
                CheckOnClick = true,
                AccessibleName = "Formats to play",
                TabIndex = 0,
            };
            foreach (var ext in supported) _list.Items.Add(ext, chosen.Contains(ext));

            // Anything already in the list that is not one of the built-in
            // formats — added by hand to an older build — stays, checked, so
            // pressing OK does not quietly drop it.
            foreach (var ext in chosen.Where(e => !supported.Contains(e, StringComparer.OrdinalIgnoreCase)))
                _list.Items.Add(ext, true);
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;

            var ok = new Button { Text = "OK", Location = new Point(122, 344), Size = new Size(80, 28), TabIndex = 1 };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(208, 344),
                Size = new Size(80, 28),
                TabIndex = 2,
            };
            ok.Click += (_, _) =>
            {
                var picked = _list.CheckedItems.Cast<string>().ToList();
                if (picked.Count == 0)
                {
                    MessageBox.Show(this, "Check at least one format.", Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    _list.Focus();
                    return;
                }
                Extensions = string.Join(";", picked);
                DialogResult = DialogResult.OK;
                Close();
            };

            Controls.AddRange(new Control[] { label, _list, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private static List<string> Split(string list) =>
            // The same separators AudioFiles.IsAudio accepts.
            list.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
