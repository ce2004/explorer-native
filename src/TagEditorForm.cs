using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Title, artist, album and the rest, as one labelled box each.
    ///
    /// For one file the boxes hold its tags. For several, a box holds the value
    /// they all share, or is empty with "(different values, left unchanged)" in
    /// its label, which is also its spoken name, so arriving in it says why it
    /// is empty. Only boxes whose text was changed are written, to every file.
    ///
    /// The window reads nothing and writes nothing itself: the values come in,
    /// and <see cref="Changes"/> goes out to a worker.
    /// </summary>
    public sealed class TagEditorForm : Form
    {
        private readonly Dictionary<TagField, TextBox> _boxes = new();
        private readonly Dictionary<TagField, string> _original = new();
        private readonly CheckBox? _numberInOrder;
        private readonly TableLayoutPanel _table;

        /// <summary>The fields whose text changed, with their new text (blank removes the tag).</summary>
        public Dictionary<TagField, string> Changes { get; } = new();

        /// <summary>Whether each file gets its place in the list as its track number.</summary>
        public bool NumberInListOrder => _numberInOrder?.Checked == true;

        public TagEditorForm(string title, IReadOnlyDictionary<TagField, TagFieldState> fields, int fileCount)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(520, 360);

            var table = _table = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Location = new Point(12, 12),
                Padding = new Padding(0),
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));

            int tab = 0;
            TextBox? firstBox = null;
            foreach (var field in TagFields.All)
            {
                var state = fields.TryGetValue(field, out var s) ? s : new TagFieldState("", false);
                string name = TagFields.Label(field) + (state.Differs ? " (" + TagEditing.DiffersText + ")" : "");

                // The label's access key moves to the box after it, which is why
                // each label sits directly before its box in the tab order.
                var label = new Label
                {
                    Text = Mnemonic(field) + (state.Differs ? " (" + TagEditing.DiffersText + ")" : "") + ":",
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Margin = new Padding(0, 6, 8, 6),
                    TabIndex = tab++,
                };
                var box = new TextBox
                {
                    Text = state.Value,
                    Dock = DockStyle.Fill,
                    Margin = new Padding(0, 3, 0, 3),
                    TabIndex = tab++,
                    AccessibleName = name,
                };
                if (TagFields.IsList(field))
                    box.AccessibleDescription = "Separate several with semicolons.";

                _boxes[field] = box;
                _original[field] = state.Value;
                firstBox ??= box;

                table.Controls.Add(label);
                table.Controls.Add(box);
            }

            if (fileCount > 1)
            {
                _numberInOrder = new SpokenCheckBox
                {
                    Text = "&Number them in list order",
                    AutoSize = true,
                    Margin = new Padding(0, 6, 0, 6),
                    TabIndex = tab++,
                };
                _numberInOrder.AccessibleDescription =
                    $"Gives the {fileCount} files track numbers 1 to {fileCount}, in the order they are in the list.";
                _numberInOrder.CheckedChanged += (_, _) => _boxes[TagField.Track].Enabled = !_numberInOrder.Checked;
                table.Controls.Add(new Label { AutoSize = true, Text = "" });
                table.Controls.Add(_numberInOrder);
            }

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 10, 0, 0),
                TabIndex = tab++,
            };
            var save = new Button { Text = "&Save", AutoSize = true, TabIndex = 0 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, TabIndex = 1 };
            save.Click += (_, _) => Save();
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            table.Controls.Add(new Label { AutoSize = true, Text = "" });
            table.Controls.Add(buttons);

            Controls.Add(table);
            AcceptButton = save;
            CancelButton = cancel;

            Shown += (_, _) =>
            {
                firstBox?.Focus();
                firstBox?.SelectAll();
            };
        }

        /// <summary>Unique access keys: T, A, R, L, C, G, Y, K, plus N and S below.</summary>
        private static string Mnemonic(TagField field) => field switch
        {
            TagField.Title => "&Title",
            TagField.Artist => "&Artist",
            TagField.AlbumArtist => "Album a&rtist",
            TagField.Album => "A&lbum",
            TagField.Composer => "&Composer",
            TagField.Genre => "&Genre",
            TagField.Year => "&Year",
            _ => "Trac&k number",
        };

        private void Save()
        {
            Changes.Clear();
            foreach (var field in TagFields.All)
            {
                var box = _boxes[field];
                if (field == TagField.Track && NumberInListOrder) continue;

                var text = box.Text.Trim();
                if (string.Equals(text, _original[field].Trim(), StringComparison.Ordinal)) continue;

                if (TagFields.IsNumber(field) && text.Length > 0 && !TagFields.TryParseNumber(field, text, out _))
                {
                    MessageBox.Show(this,
                        field == TagField.Year
                            ? "The year must be a number from 0 to 9999, or blank."
                            : "The track number must be a whole number, or blank.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    box.Focus();
                    box.SelectAll();
                    return;
                }
                Changes[field] = text;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>Grown to fit what is on it, after the fonts are resolved.</summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            var wanted = _table.PreferredSize;
            ClientSize = new Size(
                Math.Max(ClientSize.Width, _table.Left + wanted.Width + 12),
                _table.Top + wanted.Height + 12);
        }
    }
}
