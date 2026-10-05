using System;

namespace ExplorerNative
{
    /// <summary>
    /// Reads its source faster or slower than it hands samples out, which
    /// changes the pitch and the speed together.
    ///
    /// This is the other half of pitch shifting. On its own it is a tape
    /// machine: run the tape fast and everything gets higher *and* shorter.
    /// <see cref="TimeStretch"/> is the opposite — it changes the length and
    /// leaves the pitch alone. Put one behind the other and the two effects can
    /// be dialled independently, because they are the same two knobs in a
    /// different basis:
    ///
    /// <code>
    ///   stretch by R, then resample by F
    ///     speed = R * F
    ///     pitch =     F
    /// </code>
    ///
    /// So for a wanted pitch P and speed S, resample by P and stretch by S/P.
    /// That division is the whole of how the player offers pitch and speed as
    /// separate controls, and it is done in <see cref="WasapiPlayback"/> where
    /// both numbers are known.
    ///
    /// **Ratio exactly 1 is a bypass**, bit for bit, for the reason everything
    /// else in this audio path is: normal is overwhelmingly the common case and
    /// must not pay for a feature it is not using, or be coloured by it.
    /// </summary>
    public sealed class Resampler
    {
        private readonly int _channels;

        /// <summary>
        /// Where the next output sample sits in the input, as a whole part and
        /// a fraction. Kept between calls, because a hop that ends three
        /// quarters of the way between two input frames has to start there next
        /// time — rounding it to a whole frame is a phase step at every buffer
        /// boundary, which is a click at the buffer rate.
        /// </summary>
        private double _position;

        /// <summary>
        /// Input frames, with three frames of history in front of the read
        /// position because the interpolation looks backwards one and forwards
        /// two.
        /// </summary>
        private float[] _input = Array.Empty<float>();
        private float[] _block = Array.Empty<float>();
        private int _have;
        private bool _sourceDone;

        /// <summary>The anti-alias filter, one biquad per channel. See Design.</summary>
        private double[] _lowpass = Array.Empty<double>();

        /// <summary>
        /// Two sections of a fourth-order Butterworth: b0, b1, b2, a1, a2 for
        /// each, one after the other. See <see cref="Design"/>.
        /// </summary>
        private readonly double[] _sections = new double[2 * 5];
        private bool _filtering;
        private double _designedFor = double.NaN;

        /// <summary>
        /// How many input frames one output frame advances. Above one is faster
        /// through the source, which is higher and shorter.
        /// </summary>
        public double Ratio { get; set; } = 1.0;

        /// <summary>The device rate, needed to place the anti-alias filter.</summary>
        public int SampleRate { get; set; } = 48000;

        /// <summary>Fills a buffer with input frames; returns how many it gave.</summary>
        public Func<float[], int, int>? Source { get; set; }

        /// <summary>
        /// How many source frames are held but not yet handed out, so the clock
        /// can account for them. See <see cref="WasapiPlayback.Position"/>.
        /// </summary>
        public double HeldSourceFrames => Math.Max(0, _have - _position);

        public Resampler(int channels)
        {
            _channels = channels < 1 ? 1 : channels;
        }

        /// <summary>
        /// Throws away everything held. For a seek, where what is in here is
        /// audio from the place the track used to be.
        /// </summary>
        public void Reset()
        {
            _have = 0;
            _position = 0;
            _sourceDone = false;
            Array.Clear(_lowpass);
        }

        /// <summary>
        /// The source has more to give after all — it was seeked back rather
        /// than being finished with.
        ///
        /// <see cref="_sourceDone"/> is a latch, and it has to be: a source that
        /// has run out returns nothing for ever, and asking again for every
        /// output sample would be a call per sample to prove it. That is right
        /// at the end of a track and wrong at the *loop* point of one, where a
        /// gapless repeat turns the decoder round without stopping anything.
        ///
        /// Left latched, this stage never draws again and the track is silent
        /// from the first repeat onwards — and only when the pitch is not
        /// normal, because at ratio 1 this is a pass-through that never latches.
        /// "Moving pitch on repeat breaks it", exactly.
        ///
        /// Only the flag is cleared. The held tail and the fractional read
        /// position stay, so the frames after the end of the track are the
        /// frames from the start of it with no seam between them.
        /// </summary>
        public void SourceRestarted() => _sourceDone = false;

        /// <summary>
        /// Fills <paramref name="destination"/> with up to
        /// <paramref name="frames"/> output frames. Returns how many, and 0 when
        /// the source has run out.
        /// </summary>
        public int Read(float[] destination, int frames)
        {
            var source = Source;
            if (source == null || frames <= 0) return 0;

            double ratio = Ratio;
            if (!(ratio > 0)) ratio = 1.0;

            Design(ratio);

            // Bit for bit, and not by accident: at normal pitch the samples go
            // straight from the stretcher to the ring without passing through an
            // interpolator that would round every one of them.
            //
            // Coming back to normal pitch, what is still held is handed out as
            // it is first — from the nearest whole frame, which is at most half
            // a sample's shift, once — and then the stage is out of the way.
            // It used to wait for a seek to empty itself, interpolating at ratio
            // one with a fractional position in the meantime: a treble loss of
            // 3dB at 16kHz on audio that was supposed to be untouched.
            if (ratio == 1.0 && !_filtering)
            {
                if (_have == 0) return source(destination, frames);
                return Drain(source, destination, frames);
            }

            int made = 0;
            while (made < frames)
            {
                // Three frames of lookahead, because the cubic reads i-1 to i+2.
                int need = (int)_position + 4;
                if (_have < need && !_sourceDone) Refill(source, need);

                int at = (int)_position;
                if (at + 2 >= _have)
                {
                    if (_sourceDone) break;
                    continue;
                }

                double t = _position - at;

                for (int c = 0; c < _channels; c++)
                {
                    // Catmull-Rom, not linear. Linear interpolation is a
                    // triangular filter, which rolls the top off audibly and
                    // folds what it does not remove back down as aliasing —
                    // exactly the two things a pitch control gets blamed for.
                    // Four points and a cubic cost a handful of multiplies and
                    // sound like the recording.
                    double p0 = _input[(at - 1 < 0 ? 0 : at - 1) * _channels + c];
                    double p1 = _input[at * _channels + c];
                    double p2 = _input[(at + 1) * _channels + c];
                    double p3 = _input[(at + 2) * _channels + c];

                    double a = 0.5 * (-p0 + 3 * p1 - 3 * p2 + p3);
                    double b = 0.5 * (2 * p0 - 5 * p1 + 4 * p2 - p3);
                    double d = 0.5 * (-p0 + p2);

                    destination[made * _channels + c] = (float)(((a * t + b) * t + d) * t + p1);
                }

                _position += ratio;
                made++;
            }

            return made;
        }

        private int Drain(Func<float[], int, int> source, float[] destination, int frames)
        {
            int at = (int)Math.Round(_position);
            int made = 0;

            int held = _have - at;
            if (held > 0)
            {
                made = Math.Min(frames, held);
                Array.Copy(_input, at * _channels, destination, 0, made * _channels);
                _position = at + made;
            }

            // Held frames still to hand out. Asked of the frames, not of the
            // position: a position within half a frame of the end rounds to the
            // end, holds nothing, and still compared as less than it — so this
            // returned nothing for ever, and the track ended early.
            if (held > made) return made;

            _have = 0;
            _position = 0;

            if (made < frames)
            {
                if (_block.Length < (frames - made) * _channels) _block = new float[(frames - made) * _channels];
                int got = source(_block, frames - made);
                if (got > 0)
                {
                    Array.Copy(_block, 0, destination, made * _channels, got * _channels);
                    made += got;
                }
            }

            return made;
        }

        /// <summary>
        /// Pulls more input in, keeping one frame of history behind the read
        /// position so the cubic always has its p0.
        /// </summary>
        private void Refill(Func<float[], int, int> source, int need)
        {
            const int Chunk = 1024;

            // Drop everything already stepped past, except the one frame the
            // interpolator still looks back at.
            int keepFrom = (int)_position - 1;
            if (keepFrom > 1)
            {
                int keep = _have - keepFrom;
                if (keep > 0)
                    Array.Copy(_input, keepFrom * _channels, _input, 0, keep * _channels);
                _have = keep < 0 ? 0 : keep;
                _position -= keepFrom;
            }

            int wanted = Math.Max(need, _have + Chunk);
            if (_input.Length < wanted * _channels)
                Array.Resize(ref _input, wanted * _channels);

            // Kept between refills: a new one each time was eight kilobytes of
            // garbage forty-odd times a second, on the decode thread.
            if (_block.Length < Chunk * _channels) _block = new float[Chunk * _channels];
            var block = _block;
            int got = source(block, Chunk);
            if (got <= 0) { _sourceDone = true; return; }

            if (_filtering) Filter(block, got);

            if (_input.Length < (_have + got) * _channels)
                Array.Resize(ref _input, (_have + got) * _channels);

            Array.Copy(block, 0, _input, _have * _channels, got * _channels);
            _have += got;
        }

        /// <summary>
        /// The anti-alias filter, and it only exists in one direction.
        ///
        /// Reading the source *faster* than it is played — pitching up — moves
        /// every frequency in it upwards, and anything that lands above half the
        /// output rate does not vanish, it folds back down as a tone that was
        /// never in the recording. So the input is band-limited before it is
        /// stepped through, to the highest frequency that will still fit after
        /// the shift.
        ///
        /// Pitching *down* has the opposite problem, which is no problem: the
        /// spectrum contracts, nothing crosses Nyquist, and filtering would only
        /// remove treble that is entitled to be there. Hence the one-sided test.
        /// </summary>
        private void Design(double ratio)
        {
            bool wanted = ratio > 1.0001;

            if (!wanted)
            {
                if (_filtering) { _filtering = false; Array.Clear(_lowpass); }
                _designedFor = double.NaN;
                return;
            }

            if (_filtering && Math.Abs(ratio - _designedFor) < 1e-9) return;

            _designedFor = ratio;
            _filtering = true;

            if (_lowpass.Length != _channels * 4) _lowpass = new double[_channels * 4];

            // 0.45 of the output Nyquist, divided by how far things are about to
            // move up. Fourth order, as two sections with the Butterworth Qs: a
            // single second-order section left what folded back into the
            // audible band only nine to twenty decibels down at a twelve-semitone
            // shift, which is audible as a metallic edge on anything bright. Still
            // no resonant peak at the corner, which would whistle.
            double rate = SampleRate > 0 ? SampleRate : 48000;
            double cutoff = 0.45 * rate / ratio;
            if (cutoff > rate * 0.49) cutoff = rate * 0.49;

            double w0 = 2 * Math.PI * cutoff / rate;
            double cos = Math.Cos(w0), sin = Math.Sin(w0);

            var qs = new[] { 0.54119610, 1.30656296 };
            for (int n = 0; n < 2; n++)
            {
                double alpha = sin / (2 * qs[n]);
                double a0 = 1 + alpha;
                _sections[n * 5] = (1 - cos) / 2 / a0;
                _sections[n * 5 + 1] = (1 - cos) / a0;
                _sections[n * 5 + 2] = (1 - cos) / 2 / a0;
                _sections[n * 5 + 3] = -2 * cos / a0;
                _sections[n * 5 + 4] = (1 - alpha) / a0;
            }
        }

        private void Filter(float[] block, int frames)
        {
            for (int n = 0; n < 2; n++)
            {
                double b0 = _sections[n * 5], b1 = _sections[n * 5 + 1], b2 = _sections[n * 5 + 2];
                double a1 = _sections[n * 5 + 3], a2 = _sections[n * 5 + 4];

                for (int c = 0; c < _channels; c++)
                {
                    int state = (c * 2 + n) * 2;
                    double s1 = _lowpass[state], s2 = _lowpass[state + 1];

                    for (int f = 0; f < frames; f++)
                    {
                        int i = f * _channels + c;
                        double x = block[i];
                        double y = b0 * x + s1;
                        s1 = b1 * x - a1 * y + s2;
                        s2 = b2 * x - a2 * y;
                        block[i] = (float)y;
                    }

                    // Flushed, for the same reason the equaliser's state is: a
                    // filter ringing down through denormals costs more than the
                    // music it is filtering. Non-finite is flushed too, as there.
                    _lowpass[state] = double.IsFinite(s1) && (s1 <= -1e-20 || s1 >= 1e-20) ? s1 : 0;
                    _lowpass[state + 1] = double.IsFinite(s2) && (s2 <= -1e-20 || s2 >= 1e-20) ? s2 : 0;
                }
            }
        }

        /// <summary>
        /// Semitones to a frequency ratio. Twelve to an octave, which is a
        /// doubling — the only formula in music that everybody agrees on.
        /// </summary>
        public static double RatioForSemitones(double semitones) =>
            Math.Pow(2.0, semitones / 12.0);

        /// <summary>
        /// A semitone setting as something to read out. Zero is "normal"
        /// rather than "0 semitones", because that is the answer to the
        /// question somebody arrowing the list is asking.
        /// </summary>
        public static string DescribeSemitones(int semitones)
        {
            if (semitones == 0) return "Normal pitch";

            int steps = Math.Abs(semitones);
            var unit = steps == 1 ? "semitone" : "semitones";
            var way = semitones > 0 ? "up" : "down";

            // An octave is worth naming. It is the one interval a listener
            // recognises without counting, and "12 semitones up" is the same
            // thing said in arithmetic.
            if (steps % 12 == 0)
            {
                int octaves = steps / 12;
                return $"{octaves} octave{(octaves == 1 ? "" : "s")} {way}";
            }

            return $"{steps} {unit} {way}";
        }
    }
}
