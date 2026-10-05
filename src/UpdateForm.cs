using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Help, Check for updates, when there is one.
    ///
    /// Four stops, in this order, and nothing else: the sentence (where focus
    /// starts, so it is read when the window opens), the changes as a list read
    /// with the arrow keys, Update, Cancel. The release notes are shown one
    /// change per row with every Markdown symbol taken out, because NVDA reads
    /// asterisks and pound signs aloud.
    /// </summary>
    internal sealed class UpdateForm : Form
    {
        public UpdateForm(Updater.Release release)
        {
            var version = Updater.Text(release.Version);

            Text = "Update available";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 330);
            Font = SystemFonts.MessageBoxFont;

            var message = new TextBox
            {
                Text = $"Version {version} is available. You have {Updater.CurrentText}. " +
                       "Install it now? Explorer Native will restart.",
                ReadOnly = true,
                Multiline = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                Location = new Point(12, 12),
                Size = new Size(496, 40),
                TabIndex = 0,
                AccessibleRole = AccessibleRole.StaticText,
                AccessibleName = "Update available",
            };

            var changesLabel = new Label
            {
                Text = $"&Changes in {version}:",
                AutoSize = true,
                Location = new Point(12, 60),
                TabIndex = 1,
            };

            var changes = new ListBox
            {
                Location = new Point(12, 80),
                Size = new Size(496, 200),
                TabIndex = 2,
                HorizontalScrollbar = true,
                AccessibleName = $"Changes in {version}",
            };
            var lines = Changes(release.Notes);
            if (lines.Count == 0) lines.Add("No list of changes was published for this version.");
            changes.Items.AddRange(lines.ToArray());
            changes.SelectedIndex = 0;

            var update = new Button
            {
                Text = "&Update",
                DialogResult = DialogResult.OK,
                Location = new Point(332, 292),
                Size = new Size(84, 28),
                TabIndex = 3,
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(424, 292),
                Size = new Size(84, 28),
                TabIndex = 4,
            };

            Controls.AddRange(new Control[] { message, changesLabel, changes, update, cancel });
            AcceptButton = update;
            CancelButton = cancel;

            Shown += (_, _) =>
            {
                message.Focus();
                message.Select(0, 0);
            };
        }

        /// <summary>
        /// Release notes as plain rows: one per line, list markers, emphasis,
        /// headings and links' Markdown taken out, the "Full Changelog" link and
        /// the commit attribution lines dropped.
        /// </summary>
        internal static List<string> Changes(string notes)
        {
            var result = new List<string>();
            foreach (var raw in notes.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.Contains("Full Changelog", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("Co-Authored-By", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("Claude-Session", StringComparison.OrdinalIgnoreCase)) continue;

                line = Regex.Replace(line, @"\[([^\]]*)\]\([^)]*\)", "$1");   // [text](url) -> text
                line = Regex.Replace(line, @"^(#+|[-*+]|\d+\.)\s+", "");       // headings, list markers
                // Only Markdown pairs, so "C#" and "track #3" survive.
                line = Regex.Replace(line, @"\*\*(.+?)\*\*", "$1");
                line = Regex.Replace(line, @"__(.+?)__", "$1");
                line = Regex.Replace(line, @"`([^`]*)`", "$1");
                line = Regex.Replace(line, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", "$1");
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line.Equals("What's Changed", StringComparison.OrdinalIgnoreCase)) continue;

                result.Add(line);
            }
            return result;
        }
    }
}
