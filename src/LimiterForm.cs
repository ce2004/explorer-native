using System;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// The limiter's three controls, as three lists you arrow through.
    ///
    /// Every one of them changes the sound on the arrow press, not when the
    /// dialog is dismissed — the same bargain <see cref="SpeedForm"/> makes, for
    /// the same reason and more so. A compressor's attack, release and ceiling
    /// are three different ways of describing something that can only be judged
    /// by ear, against the actual music, and a dialog that applies its answer at
    /// the end makes you guess and then check. Escape puts all three back where
    /// they were, so trying things costs nothing.
    ///
    /// This is deliberately not on the Audio preferences page. It belongs where
    /// you are when you need it, which is in the middle of a track with the
    /// keyboard in your hand — hence a menu item and a global shortcut, and
    /// hence a dialog small enough to leave open while listening.
    ///
    /// Drop-down lists rather than sliders or number boxes. Arrowing a list
    /// moves between values worth having; a slider hands you 0.37 milliseconds,
    /// which is not a decision anybody made. A list is also the one control a
    /// screen reader reads out in full on every press without being asked, which
    /// matters here more than anywhere: the whole dialog is meant to be used
    /// with your attention on what you are hearing.
    /// </summary>
    public sealed class LimiterForm : Form
    {
        private readonly Action<int, int, int, bool, bool> _apply;
        private readonly int _wasAttack, _wasRelease, _wasCeiling;
        private readonly bool _wasWholeRange, _wasOn;
        private bool _keep;

        public int AttackMicroseconds { get; private set; }
        public int ReleaseMilliseconds { get; private set; }
        public int CeilingPercent { get; private set; }
        public bool WholeRange { get; private set; }

        /// <summary>
        /// Whether the limiter is in the path at all.
        ///
        /// It lives here now and nowhere else. It used to be a tick box on the
        /// Audio preferences page while the four controls below were on the
        /// menu, which is one effect with two homes and a rule joining them: the
        /// menu entry existed only while the box was ticked, so finding these
        /// controls meant knowing that a page you were not on decided whether a
        /// menu entry was there. And it is the control you most want to flick
        /// back and forth while listening, which makes it the last one that
        /// should be behind Ctrl+P, a category list and an OK button.
        ///
        /// Deliberately not called "Enabled": a Form already has one of those
        /// and it decides whether the window answers the keyboard, so a property
        /// shadowing it is a dialog that goes deaf when the effect is switched
        /// off. <see cref="EqualiserForm"/> makes the same choice for the same
        /// reason.
        /// </summary>
        public bool LimiterOn { get; private set; }

        /// <summary>
        /// <paramref name="apply"/> is handed attack, release, ceiling, whether
        /// the lift is the same at any volume, and whether the limiter is on at
        /// all, in that order, every time any of the five changes.
        /// </summary>
        public LimiterForm(int attackMicroseconds, int releaseMilliseconds, int ceilingPercent,
            bool wholeRange, bool on, Action<int, int, int, bool, bool> apply)
        {
            _apply = apply;

            AttackMicroseconds = _wasAttack = attackMicroseconds;
            ReleaseMilliseconds = _wasRelease = releaseMilliseconds;
            CeilingPercent = _wasCeiling = ceilingPercent;
            WholeRange = _wasWholeRange = wholeRange;
            LimiterOn = _wasOn = on;

            Text = "Limiter";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(460, 300);

            var attack = AddRow("&Attack:", 16, AudioPlayer.LimiterAttackLadder,
                AudioPlayer.DescribeAttack, attackMicroseconds,
                "How fast the limiter reacts. Arrow up and down; it applies as you go.");

            var release = AddRow("&Release:", 54, AudioPlayer.LimiterReleaseLadder,
                AudioPlayer.DescribeRelease, releaseMilliseconds,
                "How slowly it lets go again. Arrow up and down; it applies as you go.");

            var ceiling = AddRow("&Ceiling:", 92, AudioPlayer.LimiterCeilingLadder,
                AudioPlayer.DescribeCeiling, ceilingPercent,
                "How loud it aims for. Arrow up and down; it applies as you go.");

            // The fourth control, and it belongs here rather than in Preferences
            // for exactly the reason the other three do: it is a change you make
            // while listening and judge by ear. It is also the one most worth
            // toggling back and forth — at ordinary volumes it is the difference
            // between the limiter doing very little and doing a great deal.
            var wholeRangeBox = new CheckBox
            {
                Text = "Same lift at &any volume",
                Checked = wholeRange,
                AutoSize = true,
                Location = new Point(90, 132),
            };
            wholeRangeBox.AccessibleName = "Same lift at any volume";
            wholeRangeBox.AccessibleDescription =
                "Off, the limiter does nothing at or below 100 percent volume, and above that the " +
                "lift grows with the square of the volume. On, the whole lift is available at any " +
                "volume, including a very quiet one — which is the setting for listening into a " +
                "recording rather than for making one loud. Applies as you change it. Escape puts " +
                "it back.";
            Controls.Add(wholeRangeBox);

            // The switch itself, which used to be on the Audio preferences page.
            // After the lists in the tab order, one Tab from the list the dialog
            // opens on: it is the answer to "is this doing anything", and the
            // quickest way to compare the effect against none.
            var onBox = new CheckBox
            {
                Text = "Limiter &on",
                Checked = on,
                AutoSize = true,
                Location = new Point(90, 160),
            };
            onBox.AccessibleName = "Limiter on";
            onBox.AccessibleDescription =
                "Brings the quiet parts up towards full scale and leaves the loud where the volume " +
                "put them, clipping included. Applies as you change it, so it is the way to compare " +
                "the effect against no effect. Escape puts it back.";
            Controls.Add(onBox);

            // One handler for all five, so a change to any of them sends the
            // whole set. The renderer takes them together and there is no state
            // in between for a partial update to leave behind.
            //
            // A list that has not been touched keeps its value as it was. The
            // settings file can hold a value between two rungs, which the list
            // shows as the nearest one; read back from every list, ticking the
            // switch alone moved all three to their rungs and saved them.
            bool attackTouched = false, releaseTouched = false, ceilingTouched = false;

            void Changed()
            {
                if (attackTouched)
                    AttackMicroseconds = Chosen(attack, AudioPlayer.LimiterAttackLadder, AttackMicroseconds);
                if (releaseTouched)
                    ReleaseMilliseconds = Chosen(release, AudioPlayer.LimiterReleaseLadder, ReleaseMilliseconds);
                if (ceilingTouched)
                    CeilingPercent = Chosen(ceiling, AudioPlayer.LimiterCeilingLadder, CeilingPercent);
                WholeRange = wholeRangeBox.Checked;
                LimiterOn = onBox.Checked;
                _apply(AttackMicroseconds, ReleaseMilliseconds, CeilingPercent, WholeRange, LimiterOn);
            }

            attack.SelectedIndexChanged += (_, _) => { attackTouched = true; Changed(); };
            release.SelectedIndexChanged += (_, _) => { releaseTouched = true; Changed(); };
            ceiling.SelectedIndexChanged += (_, _) => { ceilingTouched = true; Changed(); };
            wholeRangeBox.CheckedChanged += (_, _) => Changed();
            onBox.CheckedChanged += (_, _) => Changed();

            var reset = new Button
            {
                Text = "&Defaults",
                Location = new Point(180, 240),
                AutoSize = true,
            };
            reset.Click += (_, _) =>
            {
                // Touched, all three: a saved value between rungs whose nearest
                // rung is already the default moves no list, and was then kept.
                attackTouched = releaseTouched = ceilingTouched = true;
                Select(attack, AudioPlayer.LimiterAttackLadder, 500);
                Select(release, AudioPlayer.LimiterReleaseLadder, 250);
                Select(ceiling, AudioPlayer.LimiterCeilingLadder, 90);
                wholeRangeBox.Checked = false;
                Changed();
            };

            var ok = new Button
            {
                Text = "&OK",
                DialogResult = DialogResult.OK,
                Location = new Point(280, 240),
                AutoSize = true,
            };
            var cancel = new Button
            {
                Text = "&Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(360, 240),
                AutoSize = true,
            };

            ok.Click += (_, _) => _keep = true;

            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(reset);
            Controls.Add(ok);
            Controls.Add(cancel);

            // Straight onto the first list, which is the one most worth moving.
            Shown += (_, _) => attack.Focus();
        }

        /// <summary>
        /// Grows the window until every control is on it.
        ///
        /// The same lesson <see cref="ProgressForm"/> learned: a size worked out
        /// in the constructor is worked out before the fonts are resolved, and a
        /// fifth control was just added to a fixed height that fitted four.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            var wanted = PreferredSize;
            ClientSize = new Size(
                Math.Max(ClientSize.Width, wanted.Width),
                Math.Max(ClientSize.Height, wanted.Height));
        }

        private ComboBox AddRow(string label, int top, int[] ladder,
            Func<int, string> describe, int current, string help)
        {
            var text = new Label { Text = label, AutoSize = true, Location = new Point(14, top + 4) };

            var box = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(90, top),
                Width = 350,
            };
            box.AccessibleName = label.Replace("&", "").TrimEnd(':');
            box.AccessibleDescription = help + " Escape puts it back.";

            foreach (int value in ladder) box.Items.Add(describe(value));
            Select(box, ladder, current);

            Controls.Add(text);
            Controls.Add(box);
            return box;
        }

        /// <summary>
        /// Puts the list on the rung nearest a value. Nearest rather than exact,
        /// because a settings file people can edit will hold values that are not
        /// on the ladder, and a list that refuses to show one is a dialog that
        /// opens on the wrong answer and then applies it.
        /// </summary>
        private static void Select(ComboBox box, int[] ladder, int value)
        {
            int best = 0;
            for (int i = 1; i < ladder.Length; i++)
                if (Math.Abs(ladder[i] - value) < Math.Abs(ladder[best] - value)) best = i;

            box.SelectedIndex = best;
        }

        private static int Chosen(ComboBox box, int[] ladder, int fallback) =>
            box.SelectedIndex >= 0 && box.SelectedIndex < ladder.Length
                ? ladder[box.SelectedIndex]
                : fallback;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Anything other than OK puts all five back. Closing with the X,
            // Escape, or Alt+F4 all mean the same thing: never mind.
            if (!_keep)
            {
                AttackMicroseconds = _wasAttack;
                ReleaseMilliseconds = _wasRelease;
                CeilingPercent = _wasCeiling;
                WholeRange = _wasWholeRange;
                LimiterOn = _wasOn;
                _apply(_wasAttack, _wasRelease, _wasCeiling, _wasWholeRange, _wasOn);
            }

            base.OnFormClosing(e);
        }
    }
}
