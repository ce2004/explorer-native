using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
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

        // Private-use characters standing in for code spans and escaped
        // punctuation while the Markdown is taken out; nothing in real notes
        // uses them.
        private static readonly string CodeOpen = ((char)0xE100).ToString();
        private static readonly string CodeClose = ((char)0xE101).ToString();
        private const char EscapeBase = (char)0xE000;

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

                // Code spans first, kept exactly as written: nothing inside one
                // is Markdown, backslashes included.
                var code = new List<string>();
                line = Regex.Replace(line, @"`([^`]*)`", m =>
                {
                    code.Add(m.Groups[1].Value);
                    return CodeOpen + (code.Count - 1).ToString(CultureInfo.InvariantCulture) + CodeClose;
                });

                // A backslash before punctuation means the character itself, so
                // \*e\* is "*e*" and not emphasis. Hidden from the rules below
                // and put back at the end.
                line = Regex.Replace(line, @"\\([!-/:-@\[-`{-~])", m => ((char)(EscapeBase + m.Groups[1].Value[0])).ToString());

                // [text](url) and ![alt](url) -> text, with the parentheses in an
                // address counted, so [x](u/(y)) does not leave ")" behind.
                line = Regex.Replace(line, @"!?\[([^\]]*)\]\((?>[^()]+|\((?<d>)|\)(?<-d>))*(?(d)(?!))\)", "$1");
                // Headings and list markers, and a task list's box after one.
                line = Regex.Replace(line, @"^(#+|[-*+]|\d+[.)])\s+(\[[ xX]\]\s+)?", "");
                // Only Markdown pairs, so "C#" and "track #3" survive.
                line = Regex.Replace(line, @"\*\*(.+?)\*\*", "$1");
                // Underscores only where a word starts and ends, so __init__.py
                // and snake__case keep theirs.
                line = Regex.Replace(line, @"(?<=^|[\s(\[""'])__(?=\S)(.+?)(?<=\S)__(?=$|\s|[,;:!?)\]""']|\.(?:\s|$))", "$1");
                line = Regex.Replace(line, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", "$1");

                line = Regex.Replace(line, CodeOpen + @"(\d+)" + CodeClose,
                    m => code[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]);
                line = new string(line.Select(c => c >= EscapeBase && c < EscapeBase + 128 ? (char)(c - EscapeBase) : c).ToArray());
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line.Equals("What's Changed", StringComparison.OrdinalIgnoreCase)) continue;

                result.Add(line);
            }
            return result;
        }
    }
}
