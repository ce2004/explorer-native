using System;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Playback speed, as one list you arrow through.
    ///
    /// The speed changes on every arrow press, not when the dialog is dismissed.
    /// That is the whole point: choosing a speed by ear means hearing each one,
    /// and a dialog that only applies its answer at the end makes you guess and
    /// then check. Escape puts back whatever it was when the dialog opened, so
    /// trying things costs nothing.
    ///
    /// A drop-down list rather than a slider or a number box. Arrowing a list
    /// moves between speeds that are worth having; a slider hands you 137 percent
    /// and a spin box makes you type. It is also the one control a screen reader
    /// reads out in full on every press without being asked.
    /// </summary>
    public sealed class SpeedForm : Form
    {
        private readonly Action<int> _apply;
        private readonly int _original;
        private bool _keep;

        public int SelectedPercent { get; private set; }

        public SpeedForm(int currentPercent, Action<int> apply)
        {
            _apply = apply;
            _original = currentPercent;
            SelectedPercent = currentPercent;

            Text = "Playback speed";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(340, 120);

            var label = new Label
            {
                Text = "&Speed:",
                AutoSize = true,
                Location = new Point(14, 20),
            };

            var speeds = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(80, 16),
                Width = 240,
            };
            speeds.AccessibleName = "Playback speed";
            speeds.AccessibleDescription =
                "Arrow up and down to change the speed. It applies as you go. " +
                "Escape puts it back.";

            // Every speed, for every file.
            //
            // The list used to be filtered by what the decoder would agree to,
            // and it was a real filter: playback rate belonged to the media
            // engine, Windows' FLAC decoder refused everything above normal, and
            // a whole library of FLAC showed a list where half the entries
            // silently did nothing. The speed is ours now — TimeStretch works on
            // decoded samples and does not care what container they came out of
            // — so there is nothing left to refuse and nothing to explain.
            int nearest = 0;
            foreach (int percent in AudioPlayer.SpeedLadder)
            {
                int index = speeds.Items.Add(AudioPlayer.DescribeSpeed(percent));
                if (Math.Abs(percent - currentPercent) < Math.Abs(Value(speeds, nearest) - currentPercent))
                    nearest = index;
            }

            speeds.SelectedIndex = Math.Min(nearest, speeds.Items.Count - 1);

            // Applied here, so it fires for arrow keys, for the mouse, and for
            // typing the first letter of an entry — every way a list is used.
            speeds.SelectedIndexChanged += (_, _) =>
            {
                SelectedPercent = Value(speeds, speeds.SelectedIndex);
                _apply(SelectedPercent);
            };

            var ok = new Button
            {
                Text = "&OK",
                DialogResult = DialogResult.OK,
                Location = new Point(164, 70),
                AutoSize = true,
            };
            var cancel = new Button
            {
                Text = "&Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(244, 70),
                AutoSize = true,
            };

            ok.Click += (_, _) => _keep = true;

            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(label);
            Controls.Add(speeds);
            Controls.Add(ok);
            Controls.Add(cancel);

            // Straight onto the list: this dialog is the list.
            Shown += (_, _) => speeds.Focus();
        }

        /// <summary>The percentage an entry stands for, read back out of its text.</summary>
        private static int Value(ComboBox box, int index)
        {
            if (index < 0 || index >= box.Items.Count) return 100;
            var text = box.Items[index]?.ToString() ?? "";
            int space = text.IndexOf(' ');
            return space > 0 && int.TryParse(text[..space], out int percent) ? percent : 100;
        }


        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Anything other than OK puts back what was there. Closing with the
            // X, Escape, or Alt+F4 all mean the same thing: never mind.
            if (!_keep)
            {
                SelectedPercent = _original;
                _apply(_original);
            }

            base.OnFormClosing(e);
        }
    }
}

