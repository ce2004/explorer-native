using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Silence reduction's five numbers and its switch, each of them set while
    /// the music is playing.
    ///
    /// Every one of them changes the sound on the arrow press rather than when
    /// the dialog is dismissed — the same bargain <see cref="LimiterForm"/> and
    /// <see cref="SpeedForm"/> make, and the only one that works here. What
    /// counts as silence in *this* recording, and how long a gap has to be before
    /// taking it out is an improvement rather than a mangling, are questions with
    /// no answer away from the music: you set them by listening to the gaps they
    /// are deciding about. Escape puts all six back, so trying the far end of a
    /// range costs nothing.
    ///
    /// **These are spin boxes, and they are the exception in this application.**
    /// Every other number in it is a drop-down list, for the reason
    /// <see cref="LimiterForm"/> sets out: a list cannot hold a value nobody
    /// offered, and it reads its value out on every press. That is the right
    /// trade for an attack time, whose useful values are decades apart. It is the
    /// wrong one here — these are lengths of silence in milliseconds, every one
    /// of them is a value somebody can want and can hear, and a ladder of
    /// suggestions is a ladder that decides for you.
    ///
    /// Two ways of getting there were tried and are recorded so they are not
    /// tried again:
    ///
    /// - **An editable combo box** — the list for arrowing, typing for anything
    ///   else. It stopped announcing what it was set to, and a control that
    ///   cannot be heard is not a control in this application.
    /// - **A list holding every value in the range.** It announces perfectly and
    ///   it takes **1030ms to open** — eight thousand items across five combo
    ///   boxes, and a combo with its handle made takes one window message per
    ///   item. Measured, not guessed, by the check that is still in the suite:
    ///   105ms to build the strings and nine hundred more to hand them to
    ///   Windows. This window opens on a keystroke while music is playing.
    ///
    /// What a spin box gives up is the words, and the words are the point — "150"
    /// is not an answer to "how long a gap", and "150 milliseconds, between
    /// words" is. So the application says them itself, once the arrowing stops:
    /// see <see cref="SettleMilliseconds"/>. Sweeping through a range is a run of
    /// bare numbers, which is what makes sweeping quick, and stopping on one
    /// tells you what you have stopped on.
    /// </summary>
    public sealed class SilenceForm : Form
    {
        /// <summary>
        /// How long after the last press the words are spoken.
        ///
        /// Long enough that a held key or a fast sweep is a run of numbers rather
        /// than a queue of sentences, short enough that stopping on a value and
        /// being told what it is feels like one action. The timer is restarted by
        /// every change, so it only ever fires once per stop.
        /// </summary>
        public const int SettleMilliseconds = 400;

        /// <summary>
        /// One number: the box, what it is called, the values worth jumping
        /// between, and the words that go with wherever it is.
        /// </summary>
        private sealed class Row
        {
            public NumericUpDown Box = null!;
            public Label Words = null!;
            public int[] Suggested = Array.Empty<int>();
            public Func<int, string> Describe = _ => "";

            public int Value
            {
                get => (int)Box.Value;
                set => Box.Value = Math.Clamp(value, (int)Box.Minimum, (int)Box.Maximum);
            }
        }

        private readonly List<Row> _rows = new();
        private readonly System.Windows.Forms.Timer _settle = new() { Interval = SettleMilliseconds };
        private Row? _spoke;

        private readonly Action<int, int, int, int, int, bool> _apply;
        private readonly int _wasThreshold, _wasMinimum, _wasLeave, _wasFadeOut, _wasFadeIn;
        private readonly bool _wasOn;
        private bool _keep;

        public int ThresholdDecibels { get; private set; }
        public int MinimumMilliseconds { get; private set; }
        public int LeaveMilliseconds { get; private set; }
        public int FadeOutMilliseconds { get; private set; }
        public int FadeInMilliseconds { get; private set; }

        /// <summary>
        /// Whether silence reduction is in the path at all.
        ///
        /// Deliberately not called "Enabled": a Form already has one of those and
        /// it decides whether the window answers the keyboard, so a property
        /// shadowing it is a dialog that goes deaf when the effect is switched
        /// off. <see cref="LimiterForm.LimiterOn"/> and
        /// <see cref="EqualiserForm"/> make the same choice for the same reason.
        /// </summary>
        public bool SilenceOn { get; private set; }

        /// <summary>
        /// <paramref name="apply"/> is handed the threshold, the minimum gap, how
        /// much to leave, the fade out, the fade in, and whether the whole thing
        /// is on — in that order, every time any of the six changes.
        /// </summary>
        public SilenceForm(int thresholdDecibels, int minimumMilliseconds, int leaveMilliseconds,
            int fadeOutMilliseconds, int fadeInMilliseconds, bool on,
            Action<int, int, int, int, int, bool> apply)
        {
            _apply = apply;

            ThresholdDecibels = _wasThreshold = thresholdDecibels;
            MinimumMilliseconds = _wasMinimum = minimumMilliseconds;
            LeaveMilliseconds = _wasLeave = leaveMilliseconds;
            FadeOutMilliseconds = _wasFadeOut = fadeOutMilliseconds;
            FadeInMilliseconds = _wasFadeIn = fadeInMilliseconds;
            SilenceOn = _wasOn = on;

            Text = "Silence reduction";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(620, 340);

            const string howToMove =
                " Up and down move by one, page up and page down jump between the usual values, " +
                "home and end are the ends, or type the number. It applies as you go, and it says " +
                "what it has landed on when you stop. Escape puts it back.";

            var threshold = AddRow("&Silence is below:", 16,
                SilenceReduction.LowestThresholdDecibels,
                SilenceReduction.HighestThresholdDecibels,
                AudioPlayer.SilenceThresholdLadder, AudioPlayer.DescribeSilenceThreshold,
                thresholdDecibels,
                "How quiet the recording has to get before it counts as a gap, in decibels below " +
                "full scale, from minus eighty to minus one." + howToMove);

            var minimum = AddRow("Only gaps &longer than:", 54,
                SilenceReduction.ShortestMinimumMilliseconds,
                SilenceReduction.LongestMinimumMilliseconds,
                AudioPlayer.SilenceMinimumLadder, AudioPlayer.DescribeSilenceLength,
                minimumMilliseconds,
                "A gap shorter than this is played exactly as it was recorded. One millisecond to " +
                "five thousand." + howToMove);

            var leave = AddRow("S&horten them to:", 92,
                0, SilenceReduction.LongestLeaveMilliseconds,
                AudioPlayer.SilenceLeaveLadder, AudioPlayer.DescribeSilenceLeave,
                leaveMilliseconds,
                "What is left of a gap that was shortened. Nothing is a hard cut from one sound " +
                "straight to the next; up to two thousand milliseconds." + howToMove);

            // Two fades, because they are two events. An earlier version had one
            // number for both and took it out of the front of the gap it was
            // leaving, so "shorten them to nothing" silently meant "and splice
            // it" — a control quietly overruled by another control. The fade out
            // now reaches back into the music when the gap is too short to hold
            // it, and neither number can be overruled by anything else here.
            var fadeOut = AddRow("&Fade out over:", 130,
                0, SilenceReduction.LongestFadeMilliseconds,
                AudioPlayer.SilenceFadeLadder, AudioPlayer.DescribeSilenceFadeOut,
                fadeOutMilliseconds,
                "How long the music takes to fade away into the gap, up to five hundred " +
                "milliseconds. It ends at the cut and reaches back into the music if it is longer " +
                "than the gap that is left." + howToMove);

            var fadeIn = AddRow("Fade &back in over:", 168,
                0, SilenceReduction.LongestFadeMilliseconds,
                AudioPlayer.SilenceFadeLadder, AudioPlayer.DescribeSilenceFadeIn,
                fadeInMilliseconds,
                "How long the music takes to come back after the cut, up to five hundred " +
                "milliseconds." + howToMove);

            // The switch, after the five numbers in the tab order: the dialog
            // opens on the first number, and this is the answer to "is this
            // doing anything" — the way to compare the effect against none.
            var onBox = new CheckBox
            {
                Text = "Silence reduction &on",
                Checked = on,
                AutoSize = true,
                Location = new Point(160, 212),
            };
            onBox.AccessibleName = "Silence reduction on";
            onBox.AccessibleDescription =
                "Shortens every gap longer than the minimum to the length below it, with a fade " +
                "either side. Off is a true bypass, so this is the way to compare the effect " +
                "against no effect. Applies as you change it. Escape puts it back.";
            Controls.Add(onBox);

            // One handler for all six, so a change to any of them sends the whole
            // set. The stage takes them together and there is no state in between
            // for a partial update to leave behind.
            void Changed()
            {
                // A typed number is committed when its box loses focus, which on
                // Escape is after the values have been put back — and applying it
                // then kept the sound the person had just cancelled.
                if (_closing) return;

                ThresholdDecibels = threshold.Value;
                MinimumMilliseconds = minimum.Value;
                LeaveMilliseconds = leave.Value;
                FadeOutMilliseconds = fadeOut.Value;
                FadeInMilliseconds = fadeIn.Value;
                SilenceOn = onBox.Checked;

                _apply(ThresholdDecibels, MinimumMilliseconds, LeaveMilliseconds,
                    FadeOutMilliseconds, FadeInMilliseconds, SilenceOn);
            }

            foreach (var row in _rows)
            {
                var changing = row;
                changing.Box.ValueChanged += (_, _) =>
                {
                    changing.Words.Text = changing.Describe(changing.Value);
                    Changed();

                    // Restarted rather than left running, so a sweep speaks once
                    // at the end of it rather than queueing a sentence per press.
                    _spoke = changing;
                    _settle.Stop();
                    _settle.Start();
                };
            }

            _settle.Tick += (_, _) => SayWhereItLanded();
            onBox.CheckedChanged += (_, _) => Changed();

            var reset = new Button
            {
                Text = "&Defaults",
                Location = new Point(300, 278),
                AutoSize = true,
            };
            reset.Click += (_, _) =>
            {
                threshold.Value = -48;
                minimum.Value = 400;
                leave.Value = 150;
                fadeOut.Value = 20;
                fadeIn.Value = 20;
            };

            var ok = new Button
            {
                Text = "&OK",
                DialogResult = DialogResult.OK,
                Location = new Point(430, 278),
                AutoSize = true,
            };
            var cancel = new Button
            {
                Text = "&Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(520, 278),
                AutoSize = true,
            };

            ok.Click += (_, _) => _keep = true;

            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(reset);
            Controls.Add(ok);
            Controls.Add(cancel);

            // Straight onto the first number, which is the one that decides
            // whether anything at all happens.
            Shown += (_, _) => threshold.Box.Focus();
        }

        /// <summary>
        /// Says the words that go with the number somebody has stopped on.
        ///
        /// The application says these, not the screen reader, because a spin box
        /// has nowhere to put them — it announces its value and that is all it
        /// has. "150" is not an answer to "how long a gap"; "150 milliseconds,
        /// between words" is, and this file's neighbours have carried that
        /// sentence on every rung of every list since the limiter was written.
        ///
        /// It is the same bargain as reading the first row of a folder aloud: the
        /// reader may also say the number, and hearing it twice is better than
        /// not hearing what it means at all.
        /// </summary>
        private void SayWhereItLanded()
        {
            _settle.Stop();

            var row = _spoke;
            _spoke = null;
            if (row == null || IsDisposed) return;

            if (!MaySpeak()) return;
            try { Speech.Speak(row.Describe(row.Value), interrupt: false); }
            catch { /* a reader that is not there is not a fault */ }
        }

        /// <summary>
        /// Page Up and Page Down jump between the suggested values; Home and End
        /// are the ends of the range.
        ///
        /// A spin box handles neither by itself, and a range five thousand long
        /// with a step of one needs a coarse gear — that is what the old ladder
        /// of suggestions was for, and it is still the right set of numbers to
        /// jump between even when every number between them can be reached.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message message, Keys key)
        {
            if (key is Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End)
                foreach (var row in _rows)
                {
                    if (!row.Box.Focused) continue;

                    row.Value = key switch
                    {
                        Keys.Home => (int)row.Box.Minimum,
                        Keys.End => (int)row.Box.Maximum,
                        _ => Next(row, key == Keys.PageDown),
                    };
                    return true;
                }

            return base.ProcessCmdKey(ref message, key);
        }

        /// <summary>
        /// The suggested value on the far side of where this row is now, or the
        /// end of the range when there is none.
        /// </summary>
        private static int Next(Row row, bool downwards)
        {
            int at = row.Value;

            if (downwards)
            {
                int best = (int)row.Box.Minimum;
                foreach (int rung in row.Suggested)
                    if (rung < at && rung > best) best = rung;
                return best;
            }

            int highest = (int)row.Box.Maximum;
            foreach (int rung in row.Suggested)
                if (rung > at && rung < highest) highest = rung;
            return highest;
        }

        /// <summary>
        /// Grows the window until every control is on it — the lesson
        /// <see cref="ProgressForm"/> learned and <see cref="LimiterForm"/>
        /// repeats: a size worked out in the constructor is worked out before the
        /// fonts are resolved, and at 150 percent scaling a fixed height that fits
        /// at 100 covers the bottom row.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            var wanted = PreferredSize;
            ClientSize = new Size(
                Math.Max(ClientSize.Width, wanted.Width),
                Math.Max(ClientSize.Height, wanted.Height));
        }

        private Row AddRow(string label, int top, int lowest, int highest,
            int[] suggested, Func<int, string> describe, int current, string help)
        {
            var text = new Label { Text = label, AutoSize = true, Location = new Point(14, top + 4) };

            var box = new NumericUpDown
            {
                Location = new Point(160, top),
                Width = 90,
                Minimum = lowest,
                Maximum = highest,
                Increment = 1,
                ThousandsSeparator = false,
                TextAlign = HorizontalAlignment.Right,
                Value = Math.Clamp(current, lowest, highest),
            };
            box.AccessibleName = label.Replace("&", "").TrimEnd(':');
            box.AccessibleDescription = help;

            // What the number means, beside it. For anyone looking at the window
            // this is the whole of what the old lists read out; for anyone
            // listening, SayWhereItLanded is.
            var words = new Label
            {
                Text = describe(Math.Clamp(current, lowest, highest)),
                AutoSize = true,
                Location = new Point(262, top + 4),
            };

            var row = new Row { Box = box, Words = words, Suggested = suggested, Describe = describe };

            _rows.Add(row);
            Controls.Add(text);
            Controls.Add(box);
            Controls.Add(words);
            return row;
        }

        /// <summary>
        /// Whether the application may speak at all. The words are the
        /// application's own speech, and "spoken announcements" switched off
        /// means none — this dialog was the one place that did not ask.
        /// </summary>
        public Func<bool> MaySpeak { get; set; } = () => true;

        private bool _closing;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _settle.Stop();

            // OK commits a number still being typed before anything is read;
            // anything else refuses it, whenever it arrives.
            if (_keep) ValidateChildren();
            _closing = true;

            // Anything other than OK puts all six back. Closing with the X,
            // Escape, or Alt+F4 all mean the same thing: never mind.
            if (!_keep)
            {
                ThresholdDecibels = _wasThreshold;
                MinimumMilliseconds = _wasMinimum;
                LeaveMilliseconds = _wasLeave;
                FadeOutMilliseconds = _wasFadeOut;
                FadeInMilliseconds = _wasFadeIn;
                SilenceOn = _wasOn;
                _apply(_wasThreshold, _wasMinimum, _wasLeave, _wasFadeOut, _wasFadeIn, _wasOn);
            }

            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _settle.Dispose();
            base.Dispose(disposing);
        }
    }
}
