using System;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// The ambience isolator's controls: lists you arrow through, each one
    /// landing on the sound as it moves, the bargain <see cref="LimiterForm"/>
    /// makes. Escape, Cancel or the close box put everything back.
    /// </summary>
    public sealed class AmbienceForm : Form
    {
        private readonly Action<AmbienceSettings, bool> _apply;
        private readonly AmbienceSettings _was;
        private readonly bool _wasOn;
        private bool _keep;

        public AmbienceSettings Chosen { get; private set; }

        /// <summary>Not "Enabled": a Form already has one, and shadowing it deafens the dialog.</summary>
        public bool AmbienceOn { get; private set; }

        public AmbienceForm(AmbienceSettings settings, bool on, Action<AmbienceSettings, bool> apply)
        {
            _apply = apply;
            Chosen = _was = settings.Clamped();
            AmbienceOn = _wasOn = on;

            Text = "Ambience";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(520, 260);

            int top = 16;
            if (Ambience.Unavailable is { } why)
            {
                // Said first, where the cursor lands, rather than a box that does nothing.
                var note = new TextBox
                {
                    ReadOnly = true,
                    BorderStyle = BorderStyle.None,
                    Location = new Point(14, top),
                    Width = 486,
                    Text = "Ambience cannot run: " + why + ".",
                    AccessibleName = "Problem",
                };
                Controls.Add(note);
                top += 34;
            }

            var removal = AddRow("&Voice removed:", top, Ambience.RemovalLadder, Ambience.DescribeRemoval, Chosen.RemovalPercent,
                "How much of the voice the model takes out. All of it leaves only the ambience.");
            var level = AddRow("&Level:", top + 38, Ambience.LevelLadder, Ambience.DescribeLevel, Chosen.LevelDecibels,
                "How loud the ambience is played. It is always quieter than the recording it came from.");
            var original = AddRow("O&riginal mixed in:", top + 76, Ambience.OriginalLadder, Ambience.DescribeLevel, Chosen.OriginalDecibels,
                "How much of the untouched recording is mixed back in. Off means you hear only the ambience.");

            var onBox = new SpokenCheckBox
            {
                Text = "&Ambience on",
                Checked = on,
                AutoSize = true,
                Location = new Point(150, top + 116),
            };
            onBox.AccessibleName = "Ambience on";
            onBox.AccessibleDescription =
                "Plays only the ambience of the recording: the room, the reverb, the noise and the space, " +
                "with the voice taken out by a neural network. Applies as you change it. Escape puts it back.";
            Controls.Add(onBox);

            bool loading = false;

            void Changed()
            {
                if (loading) return;
                Chosen = new AmbienceSettings
                {
                    RemovalPercent = Pick(removal, Ambience.RemovalLadder, Chosen.RemovalPercent),
                    LevelDecibels = Pick(level, Ambience.LevelLadder, Chosen.LevelDecibels),
                    OriginalDecibels = Pick(original, Ambience.OriginalLadder, Chosen.OriginalDecibels),
                }.Clamped();
                AmbienceOn = onBox.Checked;
                _apply(Chosen, AmbienceOn);
            }

            removal.SelectedIndexChanged += (_, _) => Changed();
            level.SelectedIndexChanged += (_, _) => Changed();
            original.SelectedIndexChanged += (_, _) => Changed();
            onBox.CheckedChanged += (_, _) => Changed();

            int buttons = top + 156;
            var reset = new Button { Text = "&Defaults", Location = new Point(230, buttons), AutoSize = true };
            reset.Click += (_, _) =>
            {
                var d = new AmbienceSettings();
                loading = true;
                Select(removal, Ambience.RemovalLadder, d.RemovalPercent);
                Select(level, Ambience.LevelLadder, d.LevelDecibels);
                Select(original, Ambience.OriginalLadder, d.OriginalDecibels);
                loading = false;
                Changed();
            };

            var ok = new Button { Text = "&OK", DialogResult = DialogResult.OK, Location = new Point(330, buttons), AutoSize = true };
            var cancel = new Button { Text = "&Cancel", DialogResult = DialogResult.Cancel, Location = new Point(420, buttons), AutoSize = true };
            ok.Click += (_, _) => _keep = true;
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(reset);
            Controls.Add(ok);
            Controls.Add(cancel);

            Shown += (_, _) => removal.Focus();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var wanted = PreferredSize;
            ClientSize = new Size(Math.Max(ClientSize.Width, wanted.Width), Math.Max(ClientSize.Height, wanted.Height));
        }

        private ComboBox AddRow(string label, int top, int[] ladder, Func<int, string> describe, int current, string help)
        {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(150, top), Width = 350 };
            foreach (int value in ladder) box.Items.Add(describe(value));
            Select(box, ladder, current);
            box.AccessibleName = label.Replace("&", "").TrimEnd(':');
            box.AccessibleDescription = help + " Arrow up and down; it applies as you go. Escape puts it back.";
            Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(14, top + 4) });
            Controls.Add(box);
            return box;
        }

        private static void Select(ComboBox box, int[] ladder, int value)
        {
            int best = 0;
            for (int i = 1; i < ladder.Length; i++)
                if (Math.Abs(ladder[i] - value) < Math.Abs(ladder[best] - value)) best = i;
            box.SelectedIndex = best;
        }

        private static int Pick(ComboBox box, int[] ladder, int fallback) =>
            box.SelectedIndex >= 0 && box.SelectedIndex < ladder.Length ? ladder[box.SelectedIndex] : fallback;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_keep)
            {
                Chosen = _was;
                AmbienceOn = _wasOn;
                _apply(_was, _wasOn);
            }
            base.OnFormClosing(e);
        }
    }
}
