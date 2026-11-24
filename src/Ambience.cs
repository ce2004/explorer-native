using System;
using System.Collections.Generic;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// Everything the ambience isolator is set to, as one value, so the render
    /// thread takes all of it at once and never half of one change.
    /// </summary>
    public sealed record AmbienceSettings
    {
        /// <summary>0 both tests, 1 the stereo test only, 2 the tail test only.</summary>
        public int Method { get; init; }

        /// <summary>How hard the direct sound is pushed out, 0 to 100.</summary>
        public int StrengthPercent { get; init; } = 50;

        /// <summary>How long the recording's reverb rings, which the tail test expects.</summary>
        public int TailMilliseconds { get; init; } = 1500;

        /// <summary>Make-up gain on the ambience, which is always quieter than the music.</summary>
        public int LevelDecibels { get; init; } = 6;

        /// <summary>How much of the original is mixed back in; <see cref="Ambience.OffDecibels"/> is none.</summary>
        public int OriginalDecibels { get; init; } = Ambience.OffDecibels;

        public AmbienceSettings Clamped() => this with
        {
            Method = Math.Clamp(Method, 0, 2),
            StrengthPercent = Math.Clamp(StrengthPercent, 0, 100),
            TailMilliseconds = Math.Clamp(TailMilliseconds, 200, 10_000),
            LevelDecibels = Math.Clamp(LevelDecibels, -12, 30),
            OriginalDecibels = OriginalDecibels <= Ambience.OffDecibels ? Ambience.OffDecibels : Math.Clamp(OriginalDecibels, -60, 0),
        };

        public static AmbienceSettings From(Settings s) => new AmbienceSettings
        {
            Method = s.AudioAmbienceMethod,
            StrengthPercent = s.AudioAmbienceStrengthPercent,
            TailMilliseconds = s.AudioAmbienceTailMilliseconds,
            LevelDecibels = s.AudioAmbienceLevelDecibels,
            OriginalDecibels = s.AudioAmbienceOriginalDecibels,
        }.Clamped();

        public void WriteTo(Settings s)
        {
            s.AudioAmbienceMethod = Method;
            s.AudioAmbienceStrengthPercent = StrengthPercent;
            s.AudioAmbienceTailMilliseconds = TailMilliseconds;
            s.AudioAmbienceLevelDecibels = LevelDecibels;
            s.AudioAmbienceOriginalDecibels = OriginalDecibels;
        }
    }

    /// <summary>
    /// Takes the direct sound out of a recording and leaves its ambience: the
    /// room, the reverb tails, the audience, the space the instruments were in.
    /// Applied on the way out of <see cref="WasapiRenderer"/>, after the gain and
    /// the limiter and before the equaliser.
    ///
    /// It works in the frequency domain, a 2048-point short-time Fourier
    /// transform with three-quarters overlap, so each of about a thousand bands
    /// is judged on its own about ninety times a second, and the bands that are
    /// room are kept while the bands that are the performance are turned down.
    /// Two independent tests decide it:
    ///
    /// The stereo test. Per band it tracks the two channels' powers and their
    /// cross-correlation, and takes the smaller eigenvalue of that two by two
    /// covariance. A source panned anywhere, centre or hard left, is one
    /// direction in that space and lands entirely in the larger eigenvalue;
    /// what is left in the smaller one is sound the two microphones heard
    /// differently, which is the diffuse field of the room. Each channel keeps
    /// the share of its band that is that diffuse power.
    ///
    /// The tail test. Per band it compares the power now with the power fifty
    /// milliseconds ago. Falling at the rate a room of the given tail time
    /// falls is reverb and is kept; holding steady is a sustained note and
    /// rising is a new one, and both are pushed out. This is the only test that
    /// works on a mono recording, and with "both" it takes over by itself when
    /// the recording turns out to be mono, and in the bass, where a real room
    /// is coherent between two microphones and the stereo test is wrong.
    ///
    /// Both together multiply, so a band is kept only when it is diffuse and
    /// decaying, which is what reverb is. The gains drop at once on an attack,
    /// recover over sixty milliseconds, never go below thirty decibels down,
    /// and are smoothed across neighbouring bands, which is what keeps the
    /// result from sounding watery. Checked against the literature (Merimaa,
    /// Goodwin and Jot on stereo ambience; Lebart and Habets on late reverb);
    /// Waves' closest plugin, Clarity Vx DeReverb Pro's difference output, is a
    /// neural network trained on voices, and nothing like it exists small
    /// enough to run here.
    ///
    /// The whole path is delayed by one frame, 43 milliseconds at 48 kilohertz,
    /// and the original mixed back in goes through the same frames, so the two
    /// line up exactly. Off is a bypass, bit for bit, and switching it either
    /// way crossfades over thirty milliseconds.
    /// </summary>
    public sealed class Ambience
    {
        public const int OffDecibels = -100;

        private const int N = 2048;
        private const int Hop = N / 4;
        private const int Bins = N / 2 + 1;
        private const int MaxHistory = 32;
        private const double Eps = 1e-12;
        private const double FadeSeconds = 0.03;

        private AmbienceSettings _settings = new();
        private int _on;

        public bool Enabled
        {
            get => Volatile.Read(ref _on) != 0;
            set => Volatile.Write(ref _on, value ? 1 : 0);
        }

        public AmbienceSettings Settings
        {
            get => Volatile.Read(ref _settings);
            set => Volatile.Write(ref _settings, (value ?? new AmbienceSettings()).Clamped());
        }

        // ---- render thread state -------------------------------------------

        private static readonly double[] Window = MakeWindow();
        private static readonly int[] BitReverse = MakeBitReverse();
        private static readonly double[] TwiddleCos = new double[N / 2];
        private static readonly double[] TwiddleSin = new double[N / 2];

        static Ambience()
        {
            for (int i = 0; i < N / 2; i++)
            {
                TwiddleCos[i] = Math.Cos(2 * Math.PI * i / N);
                TwiddleSin[i] = Math.Sin(2 * Math.PI * i / N);
            }
        }

        private readonly float[] _inL = new float[N], _inR = new float[N];
        private readonly double[] _accL = new double[N], _accR = new double[N];
        private readonly double[] _dryL = new double[N], _dryR = new double[N];
        private readonly float[] _qAmbL = new float[Hop], _qAmbR = new float[Hop];
        private readonly float[] _qDryL = new float[Hop], _qDryR = new float[Hop];
        private int _fill;

        private readonly double[] _re = new double[N], _im = new double[N];
        private readonly double[] _pll = new double[Bins], _prr = new double[Bins];
        private readonly double[] _plrRe = new double[Bins], _plrIm = new double[Bins];
        private readonly double[] _power = new double[Bins];
        private readonly double[] _history = new double[Bins * MaxHistory];
        private int _historyAt;
        private readonly double[] _rawL = new double[Bins], _rawR = new double[Bins];
        private readonly double[] _gainL = new double[Bins], _gainR = new double[Bins];
        private double _stereoness = 0.05;

        private int _rate;
        private bool _primed;
        private double _fade;
        private double _levelNow = 1, _originalNow;

        public void Process(float[] buffer, int count, int channels, int sampleRate)
        {
            if (buffer == null || count <= 0) return;
            if (channels < 1) channels = 1;
            if (sampleRate <= 0) sampleRate = 48000;

            bool on = Enabled;
            if (!on && _fade <= 0)
            {
                if (_primed) Forget();
                return;
            }

            int frames = count / channels;
            if (frames <= 0) return;

            if (sampleRate != _rate) { _rate = sampleRate; Forget(); }

            var s = Settings;
            double levelTarget = Math.Pow(10, s.LevelDecibels / 20.0);
            double originalTarget = s.OriginalDecibels <= OffDecibels ? 0 : Math.Pow(10, s.OriginalDecibels / 20.0);
            if (!_primed)
            {
                _levelNow = levelTarget;
                _originalNow = originalTarget;
                _primed = true;
            }

            double smooth = 1.0 - Math.Exp(-1.0 / (0.01 * _rate));
            double fadeStep = 1.0 / (FadeSeconds * _rate);
            double fade = _fade;

            for (int f = 0; f < frames; f++)
            {
                int at = f * channels;
                float l = buffer[at];
                float r = channels > 1 ? buffer[at + 1] : l;

                _levelNow += (levelTarget - _levelNow) * smooth;
                _originalNow += (originalTarget - _originalNow) * smooth;
                if (on) { fade += fadeStep; if (fade > 1) fade = 1; }
                else { fade -= fadeStep; if (fade < 0) fade = 0; }

                float outL = (float)(_qAmbL[_fill] * _levelNow + _qDryL[_fill] * _originalNow);
                float outR = (float)(_qAmbR[_fill] * _levelNow + _qDryR[_fill] * _originalNow);

                _inL[N - Hop + _fill] = l;
                _inR[N - Hop + _fill] = r;
                if (++_fill == Hop)
                {
                    _fill = 0;
                    Frame(s);
                }

                float keep = (float)(1 - fade), take = (float)fade;
                if (channels == 1)
                    buffer[at] = l * keep + take * 0.5f * (outL + outR);
                else
                {
                    buffer[at] = l * keep + take * outL;
                    buffer[at + 1] = r * keep + take * outR;
                    float rest = (float)(1 - fade + fade * _originalNow);
                    for (int c = 2; c < channels; c++) buffer[at + c] *= rest;
                }
            }

            _fade = fade;
        }

        /// <summary>One hop: analyse the last frame, keep the ambience, add it back.</summary>
        private void Frame(AmbienceSettings s)
        {
            var w = Window;
            for (int n = 0; n < N; n++)
            {
                _re[n] = _inL[n] * w[n];
                _im[n] = _inR[n] * w[n];
            }

            Fft(_re, _im, inverse: false);

            double hopSeconds = (double)Hop / _rate;
            double stat = Math.Exp(-hopSeconds / 0.08);
            double quick = Math.Exp(-hopSeconds / 0.02);
            int delayFrames = Math.Clamp((int)Math.Round(0.05 / hopSeconds), 1, MaxHistory - 1);
            double delta = 3 * Math.Log(10) / (s.TailMilliseconds / 1000.0);
            double expected = Math.Exp(-2 * delta * delayFrames * hopSeconds);
            double power = 0.35 + 1.65 * s.StrengthPercent / 100.0;
            double mono = Math.Clamp((_stereoness - 0.002) / 0.008, 0, 1);

            int writeRow = _historyAt;
            int readRow = (_historyAt - delayFrames + MaxHistory) % MaxHistory;
            double sumMin = 0, sumMean = 0;
            double binHertz = (double)_rate / N;

            // First pass: the per-band statistics.
            for (int k = 0; k < Bins; k++)
            {
                int m = (N - k) & (N - 1);
                // The two real channels out of one complex transform.
                double lr = 0.5 * (_re[k] + _re[m]), li = 0.5 * (_im[k] - _im[m]);
                double rr = 0.5 * (_im[k] + _im[m]), ri = -0.5 * (_re[k] - _re[m]);
                double pl = lr * lr + li * li, pr = rr * rr + ri * ri;

                _pll[k] = stat * _pll[k] + (1 - stat) * pl;
                _prr[k] = stat * _prr[k] + (1 - stat) * pr;
                _plrRe[k] = stat * _plrRe[k] + (1 - stat) * (lr * rr + li * ri);
                _plrIm[k] = stat * _plrIm[k] + (1 - stat) * (li * rr - lr * ri);
                _power[k] = quick * _power[k] + (1 - quick) * (pl + pr);
            }

            // Second pass: the two tests, per band.
            for (int k = 0; k < Bins; k++)
            {
                // Stereo: the smaller eigenvalue of the covariance, averaged over
                // five neighbouring bands first. A short-time estimate from one
                // band alone is biased towards too little ambience and jumps about.
                double a = 0, b = 0, cr = 0, ci = 0;
                for (int j = Math.Max(0, k - 2); j <= Math.Min(Bins - 1, k + 2); j++)
                {
                    a += _pll[j]; b += _prr[j]; cr += _plrRe[j]; ci += _plrIm[j];
                }
                double mean = 0.5 * (a + b), half = 0.5 * (a - b);
                double disc = Math.Sqrt(half * half + cr * cr + ci * ci);
                double least = Math.Max(0, mean - disc);
                double sL = Math.Sqrt(Math.Min(1, least / (a + Eps)));
                double sR = Math.Sqrt(Math.Min(1, least / (b + Eps)));
                sumMin += least; sumMean += mean;

                // Tail: how this band has moved over the last fifty milliseconds.
                // Falling at the room's rate (or faster) is reverb; holding steady
                // is a sustained note; rising is a new note. Gating on the level
                // alone let a held note through at most of its strength.
                double now = _power[k];
                double was = _history[readRow * Bins + k];
                _history[writeRow * Bins + k] = now;
                double ratio = was > Eps ? now / was : (now > Eps ? 10 : 1);
                double t = Math.Clamp((1 - ratio) / (1 - expected), 0, 1);
                t = Math.Sqrt(t);

                double gl, gr;
                switch (s.Method)
                {
                    case 1: gl = sL; gr = sR; break;
                    case 2: gl = gr = t; break;
                    default:
                    {
                        // In the bass, real rooms are coherent between two
                        // microphones, so the stereo test says "direct" about
                        // ambience; the tail decides there, from 150 to 300 hertz
                        // handing over. And a mono recording has no stereo answer.
                        double f = k * binHertz;
                        double stereoWeight = mono * Math.Clamp((f - 150) / 150, 0, 1);
                        gl = stereoWeight * (sL * t) + (1 - stereoWeight) * t;
                        gr = stereoWeight * (sR * t) + (1 - stereoWeight) * t;
                        break;
                    }
                }

                _rawL[k] = k == 0 || k == Bins - 1 ? 0 : Math.Pow(gl, power);
                _rawR[k] = k == 0 || k == Bins - 1 ? 0 : Math.Pow(gr, power);
            }

            _historyAt = (_historyAt + 1) % MaxHistory;
            _stereoness = 0.9 * _stereoness + 0.1 * (sumMin / (sumMean + Eps));

            // Smoothed across neighbouring bands and over time: single bands
            // flicking on and off are what makes this kind of thing sound wet.
            // Down at once, so the attack of a note never leaks through; back up
            // over sixty milliseconds. A floor thirty decibels down keeps the
            // bands that are mostly shut from twittering.
            double rise = 1 - Math.Exp(-hopSeconds / 0.06);
            const double Floor = 0.0316;
            for (int k = 0; k < Bins; k++)
            {
                double l = _rawL[k] * 0.5 + (k > 0 ? _rawL[k - 1] : _rawL[k]) * 0.25 + (k < Bins - 1 ? _rawL[k + 1] : _rawL[k]) * 0.25;
                double r = _rawR[k] * 0.5 + (k > 0 ? _rawR[k - 1] : _rawR[k]) * 0.25 + (k < Bins - 1 ? _rawR[k + 1] : _rawR[k]) * 0.25;
                l = Math.Max(Floor, l); r = Math.Max(Floor, r);
                _gainL[k] = l < _gainL[k] ? l : _gainL[k] + (l - _gainL[k]) * rise;
                _gainR[k] = r < _gainR[k] ? r : _gainR[k] + (r - _gainR[k]) * rise;
                if (!double.IsFinite(_gainL[k])) _gainL[k] = 0;
                if (!double.IsFinite(_gainR[k])) _gainR[k] = 0;
            }

            // Back into one complex spectrum: kept left as real, kept right as imaginary.
            for (int k = 0; k < Bins; k++)
            {
                int m = (N - k) & (N - 1);
                double lr = 0.5 * (_re[k] + _re[m]) * _gainL[k], li = 0.5 * (_im[k] - _im[m]) * _gainL[k];
                double rr = 0.5 * (_im[k] + _im[m]) * _gainR[k], ri = -0.5 * (_re[k] - _re[m]) * _gainR[k];
                // Z = L + iR at k, and its mirror at N - k.
                double zr = lr - ri, zi = li + rr;
                double mr = lr + ri, mi = -li + rr;
                _re[k] = zr; _im[k] = zi;
                if (m != k) { _re[m] = mr; _im[m] = mi; }
            }

            Fft(_re, _im, inverse: true);

            // Sqrt-Hann in and out at a quarter hop sums to two.
            for (int n = 0; n < N; n++)
            {
                double ws = w[n] * 0.5 / N;
                _accL[n] += _re[n] * ws;
                _accR[n] += _im[n] * ws;
                double ww = w[n] * w[n] * 0.5;
                _dryL[n] += _inL[n] * ww;
                _dryR[n] += _inR[n] * ww;
            }

            for (int i = 0; i < Hop; i++)
            {
                _qAmbL[i] = Finite(_accL[i]);
                _qAmbR[i] = Finite(_accR[i]);
                _qDryL[i] = Finite(_dryL[i]);
                _qDryR[i] = Finite(_dryR[i]);
            }

            Shift(_accL); Shift(_accR); Shift(_dryL); Shift(_dryR);
            Array.Copy(_inL, Hop, _inL, 0, N - Hop);
            Array.Copy(_inR, Hop, _inR, 0, N - Hop);
        }

        private static float Finite(double v) => double.IsFinite(v) ? (float)v : 0f;

        private static void Shift(double[] a)
        {
            Array.Copy(a, Hop, a, 0, N - Hop);
            Array.Clear(a, N - Hop, Hop);
        }

        private void Forget()
        {
            _primed = false;
            _fade = 0;
            _fill = 0;
            Array.Clear(_inL); Array.Clear(_inR);
            Array.Clear(_accL); Array.Clear(_accR);
            Array.Clear(_dryL); Array.Clear(_dryR);
            Array.Clear(_qAmbL); Array.Clear(_qAmbR);
            Array.Clear(_qDryL); Array.Clear(_qDryR);
            Array.Clear(_pll); Array.Clear(_prr);
            Array.Clear(_plrRe); Array.Clear(_plrIm);
            Array.Clear(_power); Array.Clear(_history);
            Array.Clear(_gainL); Array.Clear(_gainR);
            _historyAt = 0;
            _stereoness = 0.05;
        }

        private static double[] MakeWindow()
        {
            var w = new double[N];
            for (int n = 0; n < N; n++) w[n] = Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / N));
            return w;
        }

        private static int[] MakeBitReverse()
        {
            var r = new int[N];
            int bits = 0;
            while ((1 << bits) < N) bits++;
            for (int i = 0; i < N; i++)
            {
                int x = i, y = 0;
                for (int b = 0; b < bits; b++) { y = (y << 1) | (x & 1); x >>= 1; }
                r[i] = y;
            }
            return r;
        }

        /// <summary>In-place radix-2 transform. The inverse is not scaled.</summary>
        private static void Fft(double[] re, double[] im, bool inverse)
        {
            for (int i = 0; i < N; i++)
            {
                int j = BitReverse[i];
                if (j > i)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            double sign = inverse ? 1 : -1;
            for (int size = 2; size <= N; size <<= 1)
            {
                int half = size >> 1, step = N / size;
                for (int start = 0; start < N; start += size)
                {
                    for (int j = 0; j < half; j++)
                    {
                        double wr = TwiddleCos[j * step], wi = sign * TwiddleSin[j * step];
                        int a = start + j, b = a + half;
                        double tr = re[b] * wr - im[b] * wi;
                        double ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                    }
                }
            }
        }

        // ---- the dialog's lists -------------------------------------------

        public static readonly string[] MethodNames =
        {
            "Both tests, stereo and tail",
            "Stereo only, what differs between left and right",
            "Tail only, what is still ringing (works on mono)",
        };

        public static readonly int[] StrengthLadder =
            { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };

        public static readonly int[] TailLadder =
            { 200, 300, 500, 700, 1000, 1200, 1500, 2000, 2500, 3000, 4000, 5000, 7000, 10000 };

        public static readonly int[] LevelLadder =
            { -12, -6, -3, 0, 3, 6, 9, 12, 15, 18, 24, 30 };

        public static readonly int[] OriginalLadder =
            { OffDecibels, -40, -30, -24, -18, -12, -9, -6, -3, 0 };

        public static string DescribeStrength(int percent) => percent + " percent, " + percent switch
        {
            < 30 => "gentle, some of the music stays",
            < 70 => "balanced",
            _ => "strong, only clear ambience",
        };

        public static string DescribeTail(int milliseconds)
        {
            double seconds = milliseconds / 1000.0;
            string text = seconds == 1 ? "1 second" : seconds.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + " seconds";
            return text + ", " + milliseconds switch
            {
                < 600 => "a dry room",
                < 1300 => "a room",
                < 2600 => "a hall",
                _ => "a very live space",
            };
        }

        public static string DescribeLevel(int decibels) => decibels switch
        {
            <= OffDecibels => "off",
            0 => "0 decibels",
            > 0 => "up " + decibels + (decibels == 1 ? " decibel" : " decibels"),
            _ => "down " + (-decibels) + (decibels == -1 ? " decibel" : " decibels"),
        };
    }
}
