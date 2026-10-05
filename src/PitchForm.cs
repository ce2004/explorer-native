using System;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// The pitch, as a list you arrow through, and one switch beside it.
    ///
    /// A list rather than a slider, which is the opposite of the choice
    /// <see cref="EqualiserForm"/> makes next door, and the difference is the
    /// same one that decided it there. A slider is right for a *curve* — twenty
    /// bands whose shape relative to each other is the thing you are reading.
    /// This is one number, its useful values are named intervals rather than
    /// points on a scale, and a list says "3 semitones up" where a slider says
    /// "3" and leaves you to know what three of anything means.
    ///
    /// Every press applies immediately, which is the bargain the speed, limiter
    /// and equaliser dialogs all make: a pitch shift can only be judged against
    /// music that is playing. Escape puts both controls back.
    /// </summary>
    public sealed class PitchForm : Form
    {
        private readonly Action<int, bool> _apply;
        private readonly int _wasSemitones;
        private readonly bool _wasMovesTempo;
        private bool _keep;
        private bool _loading = true;

        public int Semitones { get; private set; }

        /// <summary>Whether the pitch shift takes the tempo with it.</summary>
        public bool MovesTempo { get; private set; }

        /// <summary>
        /// <paramref name="apply"/> is handed the semitones and whether the
        /// tempo follows, together, every time either changes — because the
        /// audio path sets both from one place and a moment with one applied
        /// and not the other is a moment at the wrong speed.
        /// </summary>
        public PitchForm(int semitones, bool movesTempo, Action<int, bool> apply)
        {
            _apply = apply;

            Semitones = _wasSemitones = Math.Clamp(
                semitones, AudioPlayer.LowestSemitones, AudioPlayer.HighestSemitones);
            MovesTempo = _wasMovesTempo = movesTempo;

            Text = "Pitch";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(440, 190);

            var label = new Label { Text = "&Pitch:", AutoSize = true, Location = new Point(14, 22) };

            var list = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(80, 18),
                Width = 340,
            };
            list.AccessibleName = "Pitch";
            list.AccessibleDescription =
                "Arrow up and down to move the pitch a semitone at a time, and it applies as you " +
                "go. Twelve semitones is an octave. Escape puts it back.";

            int chosen = 0;
            for (int i = 0; i < AudioPlayer.PitchLadder.Length; i++)
            {
                list.Items.Add(AudioPlayer.DescribePitch(AudioPlayer.PitchLadder[i]));
                if (AudioPlayer.PitchLadder[i] == Semitones) chosen = i;
            }
            list.SelectedIndex = chosen;

            var tempoBox = new CheckBox
            {
                Text = "Change the &tempo with it",
                Checked = movesTempo,
                AutoSize = true,
                Location = new Point(80, 62),
            };
            tempoBox.AccessibleName = "Change the tempo with it";
            tempoBox.AccessibleDescription =
                "Off, only the pitch moves and the track plays at the speed it was already set to. " +
                "On, the two move together the way a tape machine or a turntable does — pitching " +
                "down also slows it down. Applies as you change it. Escape puts it back.";
            Controls.Add(tempoBox);

            // One handler for both, so a change to either sends the pair.
            void Changed()
            {
                if (list.SelectedIndex >= 0 && list.SelectedIndex < AudioPlayer.PitchLadder.Length)
                    Semitones = AudioPlayer.PitchLadder[list.SelectedIndex];

                MovesTempo = tempoBox.Checked;
                if (!_loading) _apply(Semitones, MovesTempo);
            }

            list.SelectedIndexChanged += (_, _) => Changed();
            tempoBox.CheckedChanged += (_, _) => Changed();

            var reset = new Button { Text = "&Normal", Location = new Point(80, 132), AutoSize = true };
            reset.AccessibleDescription = "Back to the recording's own pitch.";
            reset.Click += (_, _) =>
            {
                for (int i = 0; i < AudioPlayer.PitchLadder.Length; i++)
                    if (AudioPlayer.PitchLadder[i] == 0) { list.SelectedIndex = i; break; }
            };

            var ok = new Button
            {
                Text = "&OK",
                DialogResult = DialogResult.OK,
                Location = new Point(260, 132),
                AutoSize = true,
            };
            var cancel = new Button
            {
                Text = "&Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(345, 132),
                AutoSize = true,
            };

            ok.Click += (_, _) => _keep = true;

            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(label);
            Controls.Add(list);
            Controls.Add(reset);
            Controls.Add(ok);
            Controls.Add(cancel);

            _loading = false;

            Shown += (_, _) => list.Focus();
        }

        /// <summary>
        /// Grows the window until everything is on it. The same lesson
        /// <see cref="ProgressForm"/> learned: a size worked out in the
        /// constructor is worked out before the fonts are resolved.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            var wanted = PreferredSize;
            ClientSize = new Size(
                Math.Max(ClientSize.Width, wanted.Width),
                Math.Max(ClientSize.Height, wanted.Height));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Anything other than OK puts both back. Closing with the X, with
            // Escape, or with Alt+F4 all mean the same thing: never mind.
            if (!_keep)
            {
                Semitones = _wasSemitones;
                MovesTempo = _wasMovesTempo;
                _apply(_wasSemitones, _wasMovesTempo);
            }

            base.OnFormClosing(e);
        }
    }
}
