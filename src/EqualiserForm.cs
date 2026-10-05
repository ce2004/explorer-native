using System;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// The twenty bands, as twenty sliders.
    ///
    /// Sliders here, where <see cref="LimiterForm"/> and <see cref="SpeedForm"/>
    /// next door both argue for drop-down lists — and the difference is worth
    /// writing down, because the rule those two follow is a good one. A list is
    /// right when the useful values are unevenly spaced and few: a limiter's
    /// attack matters below a millisecond and hardly at all above ten, so a
    /// slider there would hand out 0.37ms, which is not a decision anybody made.
    ///
    /// Decibels are not like that. They are already a perceptual scale, evenly
    /// spaced by construction, and every value on this one is worth having —
    /// there is no rung between -3 and -4 that a person would not choose. What
    /// there is instead is a shape: twenty bands make a *curve*, and the thing
    /// you want to know while building one is where this band sits relative to
    /// its neighbours. A row of sliders is that shape, both to look at and to
    /// walk with the arrow keys, and Home and End land on the extremes without
    /// a scroll through a list to get there.
    ///
    /// Every press applies immediately, which is the bargain all three of these
    /// dialogs make: a tone control means nothing except against music that is
    /// playing. Escape puts the whole curve back where it was.
    /// </summary>
    public sealed class EqualiserForm : Form
    {
        private readonly Action<int, int> _applyBand;
        private readonly Action<bool> _applyEnabled;
        private readonly int[] _was;
        private readonly bool _wasEnabled;
        private readonly TrackBar[] _sliders = new TrackBar[Equaliser.BandCount];
        private readonly Label _readout;
        private bool _keep;
        private bool _loading = true;

        public int[] Gains { get; private set; }

        /// <summary>
        /// Whether the equaliser is switched on — deliberately not called
        /// "Enabled". A Form already has an <c>Enabled</c>, which is what
        /// WinForms reads to decide whether the window answers the keyboard at
        /// all, and a property that shadows it is a dialog that goes deaf
        /// whenever somebody switches the effect off.
        /// </summary>
        public bool EqualiserOn { get; private set; }

        /// <summary>
        /// <paramref name="applyBand"/> is handed a band index and its new
        /// setting in decibels, every time one moves. One band at a time rather
        /// than the whole set, because that is what changed — pushing twenty
        /// values down to move one is nineteen chances to write a stale value
        /// over something.
        /// </summary>
        public EqualiserForm(int[] gains, bool enabled,
            Action<int, int> applyBand, Action<bool> applyEnabled)
        {
            _applyBand = applyBand;
            _applyEnabled = applyEnabled;

            _was = new int[Equaliser.BandCount];
            for (int b = 0; b < Equaliser.BandCount; b++)
                _was[b] = gains != null && b < gains.Length
                    ? Math.Clamp(gains[b], -Equaliser.MaximumDecibels, Equaliser.MaximumDecibels)
                    : 0;

            Gains = (int[])_was.Clone();
            EqualiserOn = _wasEnabled = enabled;

            Text = "Equaliser";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Font;

            // A vertical slider for each band, laid out left to right in the
            // order they are heard. The label under each one names its frequency
            // and never changes, because the name of a focused control changing
            // is an announcement — see the rule in CLAUDE.md about never
            // renaming the focused control. The number lives in the slider's
            // value, which a screen reader reads on every press without being
            // asked, and in the one read-out line below.
            var strip = new TableLayoutPanel
            {
                ColumnCount = Equaliser.BandCount,
                RowCount = 2,
                Location = new Point(12, 12),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            for (int b = 0; b < Equaliser.BandCount; b++)
                strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            strip.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            strip.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            for (int b = 0; b < Equaliser.BandCount; b++)
            {
                int band = b;

                var slider = new TrackBar
                {
                    Orientation = Orientation.Vertical,
                    Minimum = -Equaliser.MaximumDecibels,
                    Maximum = Equaliser.MaximumDecibels,

                    // One decibel an arrow press, six a page. The default page
                    // size is a fifth of the range, which across forty-eight
                    // decibels is a jump of nine — enough to cross from a deep
                    // cut to a boost in one press and land somewhere nobody
                    // chose.
                    //
                    // Six rather than three now that the range is twice what it
                    // was: a page is meant to be the coarse control, and eight
                    // presses from the middle to the top is coarse enough while
                    // still being a number you can predict.
                    SmallChange = 1,
                    LargeChange = 6,
                    TickFrequency = 6,
                    TickStyle = TickStyle.None,
                    Height = 170,
                    Width = 42,
                    Value = _was[b],
                    Margin = new Padding(0),
                };

                slider.AccessibleName = Equaliser.DescribeFrequency(Equaliser.Frequencies[b]);
                slider.AccessibleDescription =
                    "Up and down arrow moves this band a decibel at a time and it applies as you " +
                    "go; Page Up and Page Down move six at a time. Home and End are the extremes, " +
                    "which are 24 decibels up and 24 down. Escape puts the whole curve back.";

                slider.ValueChanged += (_, _) => BandMoved(band);
                slider.Enter += (_, _) => Describe(band);

                var caption = new Label
                {
                    Text = ShortLabel(Equaliser.Frequencies[b]),
                    AutoSize = true,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Anchor = AnchorStyles.None,
                    Margin = new Padding(0, 2, 0, 0),
                };

                strip.Controls.Add(slider, b, 0);
                strip.Controls.Add(caption, b, 1);
                _sliders[b] = slider;
            }

            Controls.Add(strip);

            // One line saying what the focused band is doing, in words.
            //
            // The slider already reports its number to a screen reader, and a
            // number on its own is the wrong half of the answer: "minus three"
            // does not say which band, and after a few presses along the row
            // neither does memory. This says both, and says zero as "flat"
            // rather than as a number, because that is the thing being asked.
            _readout = new Label
            {
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(14, strip.Bottom + 10),
                Size = new Size(520, 22),
            };
            _readout.AccessibleRole = AccessibleRole.StaticText;
            Controls.Add(_readout);

            var onBox = new CheckBox
            {
                Text = "Equaliser &on",
                Checked = enabled,
                AutoSize = true,
                Location = new Point(14, _readout.Bottom + 8),
            };
            onBox.AccessibleName = "Equaliser on";
            onBox.AccessibleDescription =
                "Off is a true bypass, not a flat curve — the samples come out exactly as they " +
                "went in. Applies as you change it, so it is the way to compare a curve against " +
                "no curve at all.";
            onBox.CheckedChanged += (_, _) =>
            {
                EqualiserOn = onBox.Checked;
                if (!_loading) _applyEnabled(EqualiserOn);
            };
            Controls.Add(onBox);

            int buttonRow = onBox.Bottom + 12;

            var flat = new Button { Text = "&Flat", Location = new Point(14, buttonRow), AutoSize = true };
            flat.AccessibleDescription = "Puts every band back to zero.";
            flat.Click += (_, _) => { for (int b = 0; b < _sliders.Length; b++) _sliders[b].Value = 0; };

            var ok = new Button
            {
                Text = "&OK",
                DialogResult = DialogResult.OK,
                Location = new Point(340, buttonRow),
                AutoSize = true,
            };
            var cancel = new Button
            {
                Text = "&Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(430, buttonRow),
                AutoSize = true,
            };

            ok.Click += (_, _) => _keep = true;

            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(flat);
            Controls.Add(ok);
            Controls.Add(cancel);

            ClientSize = new Size(
                Math.Max(strip.Right + 12, cancel.Right + 14),
                buttonRow + cancel.Height + 14);

            _loading = false;

            // Onto the lowest band, which is the left-hand end of the row and
            // the one people reach for first — unless the equaliser is switched
            // off, in which case the switch is the only control that does
            // anything and every slider is a control that does not.
            Shown += (_, _) =>
            {
                if (onBox.Checked) { _sliders[0].Focus(); Describe(0); }
                else { onBox.Focus(); _readout.Text = "The equaliser is off"; }
            };
        }

        /// <summary>
        /// Grows the window until everything is on it.
        ///
        /// The same lesson <see cref="ProgressForm"/> learned the hard way: a
        /// size worked out in the constructor is a size worked out before the
        /// fonts are resolved, and at 150% scaling a row of twenty sliders is
        /// half as wide again as it was measured at. Measured rather than padded
        /// with a guess, because a guessed number is what was wrong last time.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            var wanted = PreferredSize;
            ClientSize = new Size(
                Math.Max(ClientSize.Width, wanted.Width),
                Math.Max(ClientSize.Height, wanted.Height));
        }

        private void BandMoved(int band)
        {
            Gains[band] = _sliders[band].Value;
            Describe(band);
            if (_loading) return;

            _applyBand(band, Gains[band]);
        }

        /// <summary>
        /// Says what this band is doing, in words, on the one line that reports
        /// it. Never spoken directly — the slider's own value change is what a
        /// screen reader announces, and a second sentence over the top of it is
        /// the "say less, not more" mistake this codebase keeps a rule about.
        /// </summary>
        private void Describe(int band)
        {
            _readout.Text = Equaliser.DescribeFrequency(Equaliser.Frequencies[band]) +
                            ": " + Equaliser.DescribeGain(Gains[band]);
        }

        /// <summary>
        /// The caption under a slider, which is read by eye rather than aloud —
        /// so it is short. "1.4k", not "1.4 kilohertz"; the accessible name on
        /// the slider itself is the spoken one.
        /// </summary>
        private static string ShortLabel(int hertz)
        {
            if (hertz < 1000) return hertz.ToString();
            double k = hertz / 1000.0;
            return (k == Math.Floor(k) ? k.ToString("0") : k.ToString("0.0")) + "k";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Anything other than OK puts the whole curve back — twenty bands
            // and the switch. Closing with the X, with Escape, or with Alt+F4
            // all mean the same thing: never mind.
            if (!_keep)
            {
                for (int b = 0; b < Equaliser.BandCount; b++)
                {
                    Gains[b] = _was[b];
                    _applyBand(b, _was[b]);
                }

                EqualiserOn = _wasEnabled;
                _applyEnabled(_wasEnabled);
            }

            base.OnFormClosing(e);
        }
    }
}
