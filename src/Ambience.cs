using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Everything the ambience isolator is set to, as one value, so the render
    /// thread takes all of it at once and never half of one change.
    /// </summary>
    public sealed record AmbienceSettings
    {
        /// <summary>How much of the voice is taken out, 0 to 100.</summary>
        public int RemovalPercent { get; init; } = 100;

        /// <summary>Make-up gain on the ambience, which is always quieter than the recording.</summary>
        public int LevelDecibels { get; init; } = 6;

        /// <summary>How much of the original is mixed back in; <see cref="Ambience.OffDecibels"/> is none.</summary>
        public int OriginalDecibels { get; init; } = Ambience.OffDecibels;

        public AmbienceSettings Clamped() => this with
        {
            RemovalPercent = Math.Clamp(RemovalPercent, 0, 100),
            LevelDecibels = Math.Clamp(LevelDecibels, -12, 30),
            // Down to the off value itself: anything quieter than that is off,
            // anything louder is kept as it was saved.
            OriginalDecibels = Math.Clamp(OriginalDecibels, Ambience.OffDecibels, 0),
        };

        public static AmbienceSettings From(Settings s) => new AmbienceSettings
        {
            RemovalPercent = s.AudioAmbienceRemovalPercent,
            LevelDecibels = s.AudioAmbienceLevelDecibels,
            OriginalDecibels = s.AudioAmbienceOriginalDecibels,
        }.Clamped();

        public void WriteTo(Settings s)
        {
            s.AudioAmbienceRemovalPercent = RemovalPercent;
            s.AudioAmbienceLevelDecibels = LevelDecibels;
            s.AudioAmbienceOriginalDecibels = OriginalDecibels;
        }
    }

    /// <summary>
    /// Takes the voice out of a recording and leaves its ambience: the room,
    /// the reverb, the noise and the space around it. Applied on the way out of
    /// <see cref="WasapiRenderer"/>, after the gain and the limiter and before
    /// the equaliser.
    ///
    /// 7.2.0 did this with signal processing (a stereo test and a reverb-tail
    /// test per band), and Conner's verdict on it was that it sounded bad. From
    /// 7.3.0 it is a neural network, the way Waves' Clarity Vx DeReverb Pro's
    /// "difference" output does it: DeepFilterNet 3 (Schröter et al., MIT and
    /// Apache, native\deepfilter.dll, built from github.com/Rikorose/DeepFilterNet
    /// with the model inside it) finds the voice, and what is played is the
    /// recording with that voice subtracted. The subtraction happens inside the
    /// library on the same spectral frame the model judged, before the inverse
    /// transform, so the two line up exactly and no delay has to be guessed here
    /// (the en_ functions added to its C interface, see docs\audio-player.md).
    ///
    /// The model runs at 48 kilohertz in frames of 480 samples, one model per
    /// channel so the stereo image survives. At any other device rate each
    /// channel is resampled to 48 kilohertz and back. It is trained on speech, so
    /// it is at its best on spoken and sung recordings; in a full music mix it
    /// takes out the voice and leaves the instruments.
    ///
    /// The model loads on a worker the first time it is turned on and is kept
    /// for the rest of the run; until it is ready the sound passes untouched.
    /// Off is a bypass, bit for bit, and switching it either way crossfades over
    /// thirty milliseconds once the model's first frame has come out.
    /// </summary>
    public sealed class Ambience
    {
        public const int OffDecibels = -100;

        private const double FadeSeconds = 0.03;

        private AmbienceSettings _settings = new();
        private int _on;

        public bool Enabled
        {
            get => Volatile.Read(ref _on) != 0;
            set
            {
                Volatile.Write(ref _on, value ? 1 : 0);
                if (value) VoiceModel.Warm();
            }
        }

        public AmbienceSettings Settings
        {
            get => Volatile.Read(ref _settings);
            set => Volatile.Write(ref _settings, (value ?? new AmbienceSettings()).Clamped());
        }

        /// <summary>Why the model cannot run, or null while it can (or is loading).</summary>
        public static string? Unavailable => VoiceModel.Failure;

        // ---- render thread state -------------------------------------------

        private readonly ChannelPath?[] _paths = new ChannelPath?[2];
        private int _rate;
        private double _fade;
        private bool _flowing;
        private bool _broken;

        public void Process(float[] buffer, int count, int channels, int sampleRate)
        {
            if (buffer == null || count <= 0) return;
            if (channels < 1) channels = 1;
            if (sampleRate <= 0) sampleRate = 48000;

            bool on = Enabled && !_broken;
            if (!on && _fade <= 0)
            {
                if (_paths[0] != null) Forget();
                return;
            }

            int frames = count / channels;
            if (frames <= 0) return;

            if (sampleRate != _rate) { Forget(); _rate = sampleRate; }

            int used = Math.Min(channels, 2);
            for (int c = 0; c < used; c++)
            {
                if (_paths[c] != null) continue;
                var model = VoiceModel.Take();
                if (model == null) return; // still loading, or it cannot: untouched
                _paths[c] = new ChannelPath(model, sampleRate);
            }

            var s = Settings;
            double level = Math.Pow(10, s.LevelDecibels / 20.0);
            double original = s.OriginalDecibels <= OffDecibels ? 0 : Math.Pow(10, s.OriginalDecibels / 20.0);
            float noisyGain = (float)(level + original);
            float voiceGain = (float)(-level * s.RemovalPercent / 100.0);

            double fadeStep = 1.0 / (FadeSeconds * sampleRate);
            double fade = _fade;
            var left = _paths[0]!;
            var right = used > 1 ? _paths[1] : null;

            for (int f = 0; f < frames; f++)
            {
                int at = f * channels;
                float l = buffer[at];
                float r = right != null ? buffer[at + 1] : 0;

                if (!left.Push(l, noisyGain, voiceGain) ||
                    (right != null && !right.Push(r, noisyGain, voiceGain)))
                {
                    // The library said no. Play the recording as it is from here
                    // on, rather than silence or noise, and say so once.
                    _broken = true;
                    Diag.Note(new InvalidOperationException("DeepFilterNet failed a frame; ambience is off for this run"));
                    _fade = 0;
                    return;
                }

                bool ready = left.Ready && (right == null || right.Ready);
                float outL = ready ? left.Pull() : 0;
                float outR = ready && right != null ? right.Pull() : 0;
                if (ready) _flowing = true;

                if (_flowing)
                {
                    if (on) { fade += fadeStep; if (fade > 1) fade = 1; }
                    else { fade -= fadeStep; if (fade < 0) fade = 0; }
                }

                float keep = (float)(1 - fade), take = (float)fade;
                buffer[at] = l * keep + take * outL;
                if (right != null) buffer[at + 1] = r * keep + take * outR;
                if (channels > 2)
                {
                    float rest = (float)(1 - fade + fade * original);
                    for (int c = 2; c < channels; c++) buffer[at + c] *= rest;
                }
            }

            _fade = fade;
        }

        private void Forget()
        {
            _fade = 0;
            _flowing = false;
            for (int c = 0; c < _paths.Length; c++)
            {
                _paths[c]?.Release();
                _paths[c] = null;
            }
        }

        /// <summary>
        /// One channel's way through the model: resampled to 48 kilohertz if it
        /// is not already, cut into 480-sample frames, run, resampled back, and
        /// queued for playing at a fixed delay.
        /// </summary>
        private sealed class ChannelPath
        {
            private readonly VoiceModel _model;
            private readonly SincResampler? _up, _down;
            private readonly float[] _in, _out;
            private readonly SampleQueue _queue = new(16384);
            private readonly SampleQueue _rateQueue = new(4096);
            private int _fill;
            private float _noisy = 1, _voice = -1;
            private bool _ok = true;

            public ChannelPath(VoiceModel model, int rate)
            {
                _model = model;
                _in = new float[model.FrameLength];
                _out = new float[model.FrameLength];
                if (rate != model.SampleRate)
                {
                    _up = new SincResampler(rate, model.SampleRate);
                    _down = new SincResampler(model.SampleRate, rate);
                }
            }

            /// <summary>True once the first frame has come out, and from then on.</summary>
            public bool Ready { get; private set; }

            public bool Push(float x, float noisy, float voice)
            {
                _noisy = noisy; _voice = voice;
                if (_up == null) { Feed(x); return _ok; }
                _up.Push(x, _rateQueue);
                while (_rateQueue.Count > 0) Feed(_rateQueue.Dequeue());
                return _ok;
            }

            public float Pull() => _queue.Count > 0 ? _queue.Dequeue() : 0f;

            private void Feed(float x)
            {
                _in[_fill++] = x;
                if (_fill < _in.Length) return;
                _fill = 0;
                if (!_model.Process(_in, _out, _noisy, _voice)) { _ok = false; return; }
                if (_down == null) for (int i = 0; i < _out.Length; i++) _queue.Enqueue(_out[i]);
                else for (int i = 0; i < _out.Length; i++) _down.Push(_out[i], _queue);
                Ready = true;
            }

            public void Release() => VoiceModel.Give(_model);
        }

        // ---- the dialog's lists -------------------------------------------

        public static readonly int[] RemovalLadder =
            { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };

        public static readonly int[] LevelLadder =
            { -12, -6, -3, 0, 3, 6, 9, 12, 15, 18, 24, 30 };

        public static readonly int[] OriginalLadder =
            { OffDecibels, -40, -30, -24, -18, -12, -9, -6, -3, 0 };

        public static string DescribeRemoval(int percent) => percent + " percent, " + percent switch
        {
            0 => "the voice stays",
            < 50 => "the voice turned down a little",
            < 100 => "most of the voice gone",
            _ => "only the ambience",
        };

        public static string DescribeLevel(int decibels) => decibels switch
        {
            <= OffDecibels => "off",
            0 => "0 decibels",
            > 0 => "up " + decibels + (decibels == 1 ? " decibel" : " decibels"),
            _ => "down " + (-decibels) + (decibels == -1 ? " decibel" : " decibels"),
        };
    }

    /// <summary>
    /// DeepFilterNet 3 in native\deepfilter.dll, one mono model per instance.
    /// Loaded once, by the same route as opus.dll and the NVDA client: beside
    /// the runtime's own native libraries, where a single-file build unpacks it.
    /// Models are made on a worker (a few hundred milliseconds each) and kept in
    /// a pool, so a device change or turning it off and on never loads again.
    /// </summary>
    internal sealed unsafe class VoiceModel : IDisposable
    {
        private static readonly delegate* unmanaged[Cdecl]<IntPtr> _create;
        private static readonly delegate* unmanaged[Cdecl]<IntPtr, nuint> _frameLength;
        private static readonly delegate* unmanaged[Cdecl]<IntPtr, nuint> _sampleRate;
        private static readonly delegate* unmanaged[Cdecl]<IntPtr, float*, float*, float, float, int> _process;
        private static readonly delegate* unmanaged[Cdecl]<IntPtr, void> _free;
        private static readonly string? _loadFailure;

        static VoiceModel()
        {
            try
            {
                if (!System.Runtime.InteropServices.NativeLibrary.TryLoad("deepfilter.dll", typeof(VoiceModel).Assembly, null, out IntPtr lib) &&
                    !System.Runtime.InteropServices.NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "deepfilter.dll"), out lib))
                {
                    _loadFailure = "the voice model's library (deepfilter.dll) is missing";
                    return;
                }
                IntPtr Export(string name) => System.Runtime.InteropServices.NativeLibrary.GetExport(lib, name);
                _create = (delegate* unmanaged[Cdecl]<IntPtr>)Export("en_create");
                _frameLength = (delegate* unmanaged[Cdecl]<IntPtr, nuint>)Export("en_frame_length");
                _sampleRate = (delegate* unmanaged[Cdecl]<IntPtr, nuint>)Export("en_sample_rate");
                _process = (delegate* unmanaged[Cdecl]<IntPtr, float*, float*, float, float, int>)Export("en_process");
                _free = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("en_free");
            }
            catch (Exception ex)
            {
                _loadFailure = "the voice model's library would not load: " + ex.Message;
                _create = null;
            }
        }

        private static readonly ConcurrentBag<VoiceModel> Pool = new();
        private static int _warming;
        private static volatile string? _failure;

        /// <summary>Why no model can be made, or null.</summary>
        public static string? Failure => _loadFailure ?? _failure;

        /// <summary>
        /// Tops the pool up to two models on a worker, so turning it on never
        /// waits on the render thread. Again whenever it runs low: a player
        /// replaced while it was on (a device change) does not hand its back.
        /// </summary>
        public static void Warm()
        {
            if (Failure != null || Pool.Count >= 2 || Interlocked.Exchange(ref _warming, 1) == 1) return;
            Task.Run(() =>
            {
                try
                {
                    while (Pool.Count < 2)
                    {
                        var m = Create();
                        if (m == null) { _failure = "the voice model would not start"; return; }
                        Pool.Add(m);
                    }
                }
                catch (Exception ex) { _failure = "the voice model would not start"; Diag.Note(ex); }
                finally { Volatile.Write(ref _warming, 0); }
            });
        }

        /// <summary>For the tests: warms and waits until two models are ready, or it fails.</summary>
        internal static bool WaitReady(int milliseconds)
        {
            var until = Environment.TickCount64 + milliseconds;
            while (Environment.TickCount64 < until)
            {
                if (Failure != null) return false;
                if (Pool.Count >= 2) return true;
                Warm();
                Thread.Sleep(20);
            }
            return false;
        }

        /// <summary>A model from the pool, or null while they are still loading.</summary>
        public static VoiceModel? Take()
        {
            if (Pool.TryTake(out var m)) return m;
            Warm();
            return null;
        }

        public static void Give(VoiceModel m) => Pool.Add(m);

        /// <summary>Made here and now, on the calling thread; null if it cannot be.</summary>
        public static VoiceModel? Create()
        {
            if (_create == null) return null;
            IntPtr h = _create();
            return h == IntPtr.Zero ? null : new VoiceModel(h);
        }

        private IntPtr _handle;

        private VoiceModel(IntPtr handle)
        {
            _handle = handle;
            FrameLength = (int)_frameLength(handle);
            SampleRate = (int)_sampleRate(handle);
        }

        public int FrameLength { get; }
        public int SampleRate { get; }

        /// <summary>output = noisy * input + voice * (what the model heard as voice), delayed by the model.</summary>
        public bool Process(float[] input, float[] output, float noisy, float voice)
        {
            if (_handle == IntPtr.Zero || input.Length < FrameLength || output.Length < FrameLength) return false;
            fixed (float* i = input)
            fixed (float* o = output)
                return _process(_handle, i, o, noisy, voice) == 0;
        }

        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (h != IntPtr.Zero) _free(h);
            GC.SuppressFinalize(this);
        }

        ~VoiceModel()
        {
            var h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (h != IntPtr.Zero) _free(h);
        }
    }

    /// <summary>A fixed ring of samples that grows if it has to.</summary>
    internal sealed class SampleQueue
    {
        private float[] _ring;
        private int _head, _count;

        public SampleQueue(int capacity) => _ring = new float[capacity];

        public int Count => _count;

        public void Enqueue(float x)
        {
            if (_count == _ring.Length)
            {
                var bigger = new float[_ring.Length * 2];
                for (int i = 0; i < _count; i++) bigger[i] = _ring[(_head + i) % _ring.Length];
                _ring = bigger;
                _head = 0;
            }
            _ring[(_head + _count) % _ring.Length] = x;
            _count++;
        }

        public float Dequeue()
        {
            float x = _ring[_head];
            _head = (_head + 1) % _ring.Length;
            _count--;
            return x;
        }
    }

    /// <summary>
    /// Streaming band-limited resampler: a 32-tap Kaiser-windowed sinc, read
    /// from a table at 512 steps between samples, with the position kept as an
    /// exact fraction so it never drifts. Down to a lower rate it filters at the
    /// lower rate's limit, so nothing folds back.
    /// </summary>
    internal sealed class SincResampler
    {
        private const int Half = 16;
        private const int Steps = 512;
        private const int Ring = 64;

        private readonly int _from, _to;
        private readonly float[] _table;
        private readonly float[] _ring = new float[Ring];
        private long _count;     // samples pushed
        private long _whole;     // integer part of the next output position, in input samples
        private int _frac;       // fractional part, in units of 1 / _to

        public SincResampler(int from, int to)
        {
            _from = from; _to = to;
            double cut = 0.97 * Math.Min(1.0, (double)to / from);
            _table = new float[2 * Half * Steps + 1];
            const double Beta = 8.6;
            double i0b = I0(Beta);
            for (int n = 0; n < _table.Length; n++)
            {
                double d = (double)n / Steps - Half;
                double sinc = d == 0 ? 1 : Math.Sin(Math.PI * cut * d) / (Math.PI * cut * d);
                double r = d / Half;
                double w = Math.Abs(r) >= 1 ? 0 : I0(Beta * Math.Sqrt(1 - r * r)) / i0b;
                _table[n] = (float)(cut * sinc * w);
            }
            _whole = -Half; // the first output waits until it has a full window behind it
        }

        public void Push(float x, SampleQueue output)
        {
            _ring[_count & (Ring - 1)] = x;
            _count++;
            // Output at position p = _whole + _frac / _to needs input up to floor(p) + Half.
            while (_whole + Half <= _count - 1)
            {
                double frac = (double)_frac / _to;
                double sum = 0;
                for (int j = -Half + 1; j <= Half; j++)
                {
                    long n = _whole + j;
                    if (n < 0) continue;
                    double d = j - frac; // distance from the output position, in -Half..Half
                    double at = (d + Half) * Steps;
                    int k = (int)at;
                    if (k < 0 || k >= _table.Length - 1) continue;
                    double t = at - k;
                    double h = _table[k] + (_table[k + 1] - _table[k]) * t;
                    sum += _ring[n & (Ring - 1)] * h;
                }
                output.Enqueue((float)sum);
                _frac += _from;
                while (_frac >= _to) { _frac -= _to; _whole++; }
            }
        }

        private static double I0(double x)
        {
            double sum = 1, term = 1, q = x * x / 4;
            for (int k = 1; k < 50; k++)
            {
                term *= q / (k * k);
                sum += term;
                if (term < 1e-12 * sum) break;
            }
            return sum;
        }
    }
}
