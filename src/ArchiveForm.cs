using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>What the Compress dialog was answered with.</summary>
    public sealed record ArchiveRequest(string Path, ArchiveFormat Format, ArchiveLevel Level);

    /// <summary>
    /// The one question compressing has to ask: what to call it, what kind, and
    /// how hard to squeeze.
    ///
    /// Three controls and no more. Everything else an archiver usually asks —
    /// dictionary size, word size, solid block, split volumes, filters — is
    /// either a number with one right answer or a number whose right answer is a
    /// property of the machine, and this codebase has a rule about both. The
    /// thread count is not here for exactly that reason: it is worked out from
    /// the processor rather than asked of somebody who would have to know what a
    /// good answer looked like.
    ///
    /// The name field comes first and is focused on the way in with the stem
    /// selected, the same bargain renaming makes — type to replace the name, and
    /// the extension stays. Changing the format rewrites that extension in place
    /// rather than leaving somebody with "photos.zip" that is a 7z, because a
    /// name that lies about its format is a file nothing will open by
    /// double-click.
    /// </summary>
    public sealed class ArchiveForm : Form
    {
        private readonly TextBox _name;
        private readonly ComboBox _format;
        private readonly ComboBox _level;
        private readonly List<ArchiveFormat> _formats;
        private readonly string _folder;

        private ArchiveFormat _current;

        private readonly IReadOnlyList<string> _sources;
        private readonly bool? _sourceIsFolder;

        /// <summary>The name last suggested, so a name nobody has edited follows the format.</summary>
        private string _suggested;

        public ArchiveRequest? Result { get; private set; }

        /// <param name="singleFile">
        /// Whether the selection is exactly one *file*, which decides whether a
        /// bare .gz is on the list — it holds one file and has nowhere to put a
        /// second. Answered by the caller, on a worker: this used to be a
        /// File.Exists here, on the UI thread, in the constructor, so selecting
        /// something on a share that had gone quiet meant no dialog appeared at
        /// all until the SMB timeout expired — not even a window to say that
        /// something was happening.
        /// </param>
        public ArchiveForm(string destinationFolder, IReadOnlyList<string> sources,
            ArchiveFormat startFormat, ArchiveLevel startLevel, bool singleFile)
        {
            _folder = destinationFolder;
            _sources = sources;

            // One item that is not a file is a folder; known without asking.
            _sourceIsFolder = sources.Count == 1 ? !singleFile : null;

            _formats = ArchiveFormats.Creatable(singleFile).ToList();

            _current = _formats.Contains(startFormat) ? startFormat : _formats[0];

            Text = "Compress";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(520, 210);

            var what = new Label
            {
                Text = Describe(sources),
                AutoSize = true,
                Location = new Point(14, 14),
            };
            what.AccessibleName = what.Text;

            var nameLabel = new Label { Text = "&File name:", AutoSize = true, Location = new Point(14, 48) };
            _name = new TextBox
            {
                Location = new Point(130, 44),
                Width = 370,
                Text = _suggested = ArchiveFormats.SuggestedName(sources, _current, _sourceIsFolder),
            };
            _name.AccessibleName = "File name";

            var formatLabel = new Label { Text = "F&ormat:", AutoSize = true, Location = new Point(14, 84) };
            _format = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(130, 80),
                Width = 370,
            };
            _format.AccessibleName = "Format";
            foreach (var format in _formats) _format.Items.Add(Label(format));
            _format.SelectedIndex = _formats.IndexOf(_current);

            var levelLabel = new Label { Text = "&Compression:", AutoSize = true, Location = new Point(14, 120) };
            _level = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(130, 116),
                Width = 370,
            };
            _level.AccessibleName = "Compression";
            _level.Items.AddRange(new object[]
            {
                "Fastest — least time, largest file",
                "Balanced — the usual answer",
                "Smallest — most time, smallest file",
            });
            _level.SelectedIndex = (int)startLevel;

            _format.SelectedIndexChanged += (_, _) => FormatChanged();

            var ok = new Button
            {
                Text = "&Compress",
                DialogResult = DialogResult.None,
                Location = new Point(300, 160),
                AutoSize = true,
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(410, 160),
                AutoSize = true,
            };

            // Not a DialogResult on the button: the name has to be checked before
            // the window is allowed to close, and a button carrying OK closes it
            // whatever this handler decides.
            ok.Click += (_, _) => Accept();

            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(what);
            Controls.Add(nameLabel);
            Controls.Add(_name);
            Controls.Add(formatLabel);
            Controls.Add(_format);
            Controls.Add(levelLabel);
            Controls.Add(_level);
            Controls.Add(ok);
            Controls.Add(cancel);

            Shown += (_, _) =>
            {
                _name.Focus();
                SelectStem();
            };
        }

        private static string Describe(IReadOnlyList<string> sources)
        {
            if (sources.Count != 1) return $"Compressing {sources.Count} items";

            var one = sources[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return $"Compressing \"{Path.GetFileName(one)}\"";
        }

        /// <summary>
        /// How a format reads in the list.
        ///
        /// The extension is in the label because it is the part somebody
        /// recognises — "Zip" and ".zip" are the same word to most people and
        /// "Zstandard-compressed tar" is not obviously ".tar.zst" to anybody.
        /// </summary>
        private static string Label(ArchiveFormat format) => $"{format.Name} ({format.Extension})";

        /// <summary>
        /// Puts the new format's extension on the name.
        ///
        /// Only when the name still ends with the old one. Somebody who has typed
        /// their own extension has said something, and rewriting it would be the
        /// dialog arguing with them.
        /// </summary>
        private void FormatChanged()
        {
            int at = _format.SelectedIndex;
            if (at < 0 || at >= _formats.Count) return;

            var chosen = _formats[at];
            var text = _name.Text;

            // Still the suggestion: suggested again. A bare gzip keeps the file's
            // own extension where an archive drops it, so swapping one ending for
            // another turned "song.zip" into "song.gz" and lost ".flac".
            if (string.Equals(text, _suggested, StringComparison.Ordinal))
            {
                text = _suggested = ArchiveFormats.SuggestedName(_sources, chosen, _sourceIsFolder);
            }
            else
            {
                foreach (var extension in _current.Extensions)
                    if (text.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    {
                        text = text[..^extension.Length] + chosen.Extension;
                        break;
                    }
            }

            _current = chosen;
            _name.Text = text;
            SelectStem();
        }

        /// <summary>
        /// Selects the name and not the extension, so typing replaces one and
        /// keeps the other.
        /// </summary>
        private void SelectStem()
        {
            var text = _name.Text;
            int keep = text.Length;

            foreach (var format in _formats)
                foreach (var extension in format.Extensions)
                    if (text.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                        keep = Math.Min(keep, text.Length - extension.Length);

            _name.Select(0, Math.Max(0, keep));
        }

        private void Accept()
        {
            var typed = _name.Text.Trim();

            if (NameRules.DescribeBadName(typed) is { } complaint)
            {
                Complain(complaint);
                return;
            }

            int at = _format.SelectedIndex;
            var format = at >= 0 && at < _formats.Count ? _formats[at] : _formats[0];

            // A name with no extension, or with somebody else's, gets this
            // format's put on the end rather than being refused. Refusing would
            // be correct and unhelpful: the dialog knows what was meant.
            //
            // A bare gzip of "data.tar" is "data.tar.gz", which reads by name as a
            // gzipped tar; it already ends with this format's extension, and
            // adding another made "data.tar.gz.gz".
            bool endsRight = format.SingleFile &&
                             typed.EndsWith(format.Extension, StringComparison.OrdinalIgnoreCase);
            if (ArchiveFormats.FromName(typed) != format && !endsRight) typed += format.Extension;

            var full = Path.Combine(_folder, typed);

            // Bounded. This runs on the UI thread of a modal dialog, and the
            // folder being written into can be a share that has gone to sleep —
            // an unbounded pair of existence checks is the window not answering
            // with no way to tell why. If it does not come back in two seconds
            // the question is asked anyway: a "replace it?" about a file that
            // turns out not to be there costs one keystroke, and the opposite
            // assumption costs somebody's archive.
            var probe = Task.Run(() =>
            {
                try { return File.Exists(full) || Directory.Exists(full); }
                catch { return false; }
            });

            bool alreadyThere = probe.Wait(TimeSpan.FromSeconds(2)) ? probe.Result : true;

            if (alreadyThere)
            {
                var answer = MessageBox.Show(this,
                    $"\"{typed}\" is already here. Replace it?",
                    "Compress", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (answer != DialogResult.Yes)
                {
                    _name.Focus();
                    SelectStem();
                    return;
                }
            }

            Result = new ArchiveRequest(full, format, (ArchiveLevel)Math.Clamp(_level.SelectedIndex, 0, 2));
            DialogResult = DialogResult.OK;
            Close();
        }

        private void Complain(string message)
        {
            MessageBox.Show(this, message, "Compress", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _name.Focus();
            _name.SelectAll();
        }

        /// <summary>
        /// Shows it and returns what was asked for, or null.
        /// </summary>
        public static ArchiveRequest? Ask(IWin32Window owner, string destinationFolder,
            IReadOnlyList<string> sources, ArchiveFormat startFormat, ArchiveLevel startLevel,
            bool singleFile)
        {
            using var dialog = new ArchiveForm(destinationFolder, sources, startFormat, startLevel, singleFile);
            return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Result : null;
        }
    }
}
