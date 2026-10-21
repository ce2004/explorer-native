using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>What the Convert audio dialog was answered with.</summary>
    internal sealed record ConvertRequest(AudioFormatChoice Choice, bool Subfolders);

    /// <summary>
    /// The question converting asks: which format, how good, and for a folder,
    /// whether its subfolders come too. Two drop-downs rather than one list of
    /// fourteen, so the format is one arrow per format and the quality is then
    /// a short list of its own. A format this PC cannot write is still listed,
    /// saying so, and refused with the reason if chosen.
    ///
    /// Also used by the editor, without the subfolder box, to ask what to save
    /// as when the original's format cannot be written.
    /// </summary>
    internal sealed class AudioConvertForm : Form
    {
        private readonly ComboBox _format;
        private readonly ComboBox _quality;
        private readonly CheckBox? _subfolders;
        private readonly IReadOnlyDictionary<AudioFormatKind, string?> _unavailable;
        private List<AudioFormatChoice> _qualities = new();

        public ConvertRequest? Result { get; private set; }

        /// <param name="unavailable">Format to reason for each format this PC cannot write; asked on a worker beforehand.</param>
        /// <param name="offerSubfolders">Show the subfolder box, because the selection has a folder in it.</param>
        public AudioConvertForm(string title, string what, AudioFormatChoice start,
            IReadOnlyDictionary<AudioFormatKind, string?> unavailable, bool offerSubfolders, bool subfolders,
            string acceptText = "C&onvert")
        {
            _unavailable = unavailable;

            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;

            var layout = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12),
                Location = new Point(0, 0),
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));

            var heading = new Label { Text = what, AutoSize = true, MaximumSize = new Size(500, 0), Margin = new Padding(3, 3, 3, 10) };
            layout.Controls.Add(heading);
            layout.SetColumnSpan(heading, 2);

            layout.Controls.Add(new Label { Text = "&Format:", AutoSize = true, Anchor = AnchorStyles.Left });
            _format = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            _format.AccessibleName = "Format";
            foreach (var kind in AudioFormats.Kinds)
            {
                var name = AudioFormats.NameOf(kind) + " (" + AudioFormats.ExtensionOf(kind) + ")";
                if (unavailable.TryGetValue(kind, out var why) && why != null) name += ", not available on this PC";
                _format.Items.Add(name);
            }
            layout.Controls.Add(_format);

            layout.Controls.Add(new Label { Text = "&Quality:", AutoSize = true, Anchor = AnchorStyles.Left });
            _quality = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            _quality.AccessibleName = "Quality";
            layout.Controls.Add(_quality);

            if (offerSubfolders)
            {
                _subfolders = new SpokenCheckBox
                {
                    Text = "Include &subfolders",
                    Checked = subfolders,
                    AutoSize = true,
                    Margin = new Padding(3, 8, 3, 3),
                };
                layout.Controls.Add(_subfolders);
                layout.SetColumnSpan(_subfolders, 2);
            }

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 12, 0, 0),
            };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var ok = new Button { Text = acceptText, AutoSize = true };
            ok.Click += (_, _) => Accept();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            layout.Controls.Add(buttons);
            layout.SetColumnSpan(buttons, 2);

            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(layout);

            _format.SelectedIndexChanged += (_, _) => FillQualities(null);
            _format.SelectedIndex = Math.Max(0, AudioFormats.Kinds.ToList().IndexOf(start.Kind));
            FillQualities(start);

            Shown += (_, _) => _format.Focus();
        }

        /// <summary>Sized from what is on it, so nothing is cut off at 150 percent.</summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var panel = Controls[0];
            ClientSize = new Size(panel.PreferredSize.Width, panel.PreferredSize.Height);
        }

        private AudioFormatKind SelectedKind =>
            AudioFormats.Kinds[Math.Clamp(_format.SelectedIndex, 0, AudioFormats.Kinds.Count - 1)];

        private void FillQualities(AudioFormatChoice? keep)
        {
            _qualities = AudioFormats.QualitiesOf(SelectedKind).ToList();
            _quality.Items.Clear();
            foreach (var q in _qualities) _quality.Items.Add(q.QualityName);

            int at = keep != null ? _qualities.FindIndex(q => q.Id == keep.Id) : -1;
            if (at < 0)
            {
                // The usual middle answer for a lossy format; the only one otherwise.
                at = _qualities.Count >= 3 ? 1 : 0;
                if (SelectedKind == AudioFormatKind.Mp3) at = _qualities.FindIndex(q => q.Kbps == 192);
            }
            _quality.SelectedIndex = Math.Max(0, at);
            _quality.Enabled = _qualities.Count > 1;
        }

        private void Accept()
        {
            if (_unavailable.TryGetValue(SelectedKind, out var why) && why != null)
            {
                MessageBox.Show(this, why + ". Choose another format.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                _format.Focus();
                return;
            }

            var choice = _qualities[Math.Clamp(_quality.SelectedIndex, 0, _qualities.Count - 1)];
            Result = new ConvertRequest(choice, _subfolders?.Checked ?? false);
            DialogResult = DialogResult.OK;
        }

        public static ConvertRequest? Ask(IWin32Window owner, string title, string what, AudioFormatChoice start,
            IReadOnlyDictionary<AudioFormatKind, string?> unavailable, bool offerSubfolders, bool subfolders,
            string acceptText = "C&onvert")
        {
            using var dialog = new AudioConvertForm(title, what, start, unavailable, offerSubfolders, subfolders, acceptText);
            return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Result : null;
        }

        /// <summary>Which formats this PC cannot write, and why. Slow-ish (Media Foundation is asked): call on a worker.</summary>
        public static Dictionary<AudioFormatKind, string?> Availability() =>
            AudioFormats.Kinds.ToDictionary(k => k, AudioEncoders.WhyUnavailable);
    }
}
