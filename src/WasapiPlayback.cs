using System;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// One track, decoded by Media Foundation and rendered by us.
    ///
    /// <see cref="AudioDecoder"/> turns the file into float; <see cref="WasapiRenderer"/>
    /// puts float on the device. This is the piece in between, and it exists for
    /// one reason beyond tidiness: the render thread must never wait on a disk or
    /// a network. A decode that blocks for a second on a sleeping NAS would be a
    /// second of silence, so the reading happens on its own thread into a ring
    /// the renderer drains, and the renderer only ever touches memory.
    ///
    /// The old path had the same problem and solved it in the same shape —
    /// <see cref="StreamingSource"/> pulls a network file into memory so the
    /// decoder never waits on the share. This is that argument one layer up,
    /// where it now also covers a local disk that has gone to sleep.
    /// </summary>
    internal sealed class WasapiPlayback : IDisposable
    {
        /// <summary>
        /// Four seconds of audio at any ordinary rate.
        ///
        /// It was one, and one is not enough for a track coming off Google
        /// Drive. The pump reads through a download that is itself racing
        /// playback, and a single request to Google costs most of a second — so
        /// one slow chunk emptied the ring and the renderer got silence. That is
        /// heard as a stutter, and it is the whole of "it should never gap".
        ///
        /// The old note here worried that a long ring puts stale audio in front
        /// of a seek. It does not: a seek empties the ring, deliberately, so the
        /// depth costs nothing there. What it does cost is memory — four seconds
        /// of stereo float is about 1.5MB, against a 160MB executable — and that
        /// is not a trade worth thinking about twice.
        /// </summary>
        private const int RingFrames = 48000 * 4;

        /// <summary>
        /// How full the ring is actually kept once nothing is racing playback.
        ///
        /// **The depth above is latency.** Everything in the ring has already
        /// been decoded, stretched, resampled and had its silences taken out, so
        /// a change to any of those stages is not heard until the four seconds in
        /// front of it have played — which is what "adjusting the pitch is not
        /// instant" was. The limiter, the equaliser and the volume are instant
        /// because they run on the render thread, *behind* the ring; the pitch,
        /// the speed and silence reduction cannot be, because they change how
        /// long the music is and the ring is what absorbs that.
        ///
        /// So the ring is deep when it has to be and shallow when it does not.
        /// Four seconds exists for one case, written down where RingFrames is:
        /// a track coming off Google Drive through a download that is itself
        /// racing playback, where a single request costs most of a second. That
        /// case is exactly the one <see cref="StreamingSource.Complete"/> answers.
        /// Once the file is in memory — and for a local file, from the first
        /// frame — there is nothing to absorb, and a fifth of a second is ten
        /// renderer buffers of head start against a decode that runs hundreds of
        /// times faster than playback.
        ///
        /// Two hundred milliseconds is short enough to read as immediate on a
        /// control being set by ear. The capacity does not change with it: the
        /// ring stays four seconds wide and simply stops being filled past this,
        /// so a stream that has not finished arriving still gets the whole of it.
        /// </summary>
        private const int ShallowFrames = 48000 / 5;

        /// <summary>
        /// How much has to be in the ring before the renderer is let loose.
        ///
        /// Starting the device against an empty ring means the first buffers it
        /// asks for are silence, which is a click and a gap at the front of
        /// every track — most audible on exactly the tracks that are slowest to
        /// arrive. Half a second is enough to cover the first decode and short
        /// enough not to be a wait.
        ///
        /// Not applied to a source that has already finished: a file shorter
        /// than the floor would otherwise never start at all.
        /// </summary>
        private const int PrebufferFrames = 24000;

        private WasapiRenderer _renderer = new();
        private ITrackDecoder? _decoder;

        /// <summary>
        /// The download this track is being played through, if it is one.
        ///
        /// Held only to be asked whether it has finished arriving — see
        /// <see cref="ShallowFrames"/>. The decoder gets it as a plain IStream
        /// and neither knows nor cares what is behind it.
        /// </summary>
        private StreamingSource? _download;

        /// <summary>
        /// Set to move to another device; the pump picks it up. Null means no
        /// switch is pending, "" means whatever Windows calls the default.
        /// </summary>
        private string? _switchTo;

        /// <summary>
        /// Raised when the pump has consumed frames, so it does not have to poll.
        ///
        /// The battery rule for this thread. It used to sleep five milliseconds
        /// whenever the ring was full — two hundred wakeups a second, for as long
        /// as a track was loaded, whether or not anything was playing. Paused, the
        /// ring never drains, so every one of those was a wakeup to discover that
        /// there was still nothing to do.
        /// </summary>
        private readonly AutoResetEvent _drained = new(false);

        /// <summary>Carried so a device change asks for the same thing the first open did.</summary>
        private bool _preferRaw = true;
        private SilenceReduction? _silence;
        private TimeStretch? _stretch;
        private Resampler? _pitch;

        private double _rate = 1.0;
        private double _pitchRatio = 1.0;
        private bool _pitchMovesTempo;

        /// <summary>
        /// What the decoder actually produces, which is not always what it was
        /// asked for.
        ///
        /// <see cref="AudioDecoder"/> asks for the device's rate and channel
        /// count in three goes, most specific first, and the last of them lets
        /// the decoder choose both — because a source reader refuses an
        /// over-specified type outright, and half the WAVs in the world only
        /// open on the third try. It reads back what it settled on and says so
        /// honestly.
        ///
        /// Nothing used to *do* anything with that. The samples went to a device
        /// expecting 48kHz stereo whatever they were, so a 44.1kHz file played
        /// eight percent fast, a 22kHz one at more than double, and a mono file
        /// read as stereo ran off the end of its own buffer at twice the speed.
        /// The decoder's own comment predicted exactly this and the conversion
        /// was never written.
        /// </summary>
        private int _sourceRate;
        private int _sourceChannels;

        /// <summary>
        /// How many decoder frames one device frame is worth, from the rate
        /// difference alone. One when they agree, which is the common case.
        /// </summary>
        private double RateConversion =>
            _sourceRate > 0 && _renderer.SampleRate > 0
                ? _sourceRate / (double)_renderer.SampleRate
                : 1.0;

        /// <summary>
        /// How fast to play, where 1 is normal. The pitch does not follow it —
        /// see <see cref="TimeStretch"/>, which is the whole reason the media
        /// engine is no longer here.
        /// </summary>
        public double Rate
        {
            get => _rate;
            set { _rate = value <= 0 ? 1.0 : value; ApplyRates(); }
        }

        /// <summary>
        /// How far to shift the pitch, as a frequency ratio: 2 is an octave up,
        /// 0.5 an octave down, 1 untouched.
        /// </summary>
        public double PitchRatio
        {
            get => _pitchRatio;
            set { _pitchRatio = value <= 0 ? 1.0 : value; ApplyRates(); }
        }

        /// <summary>
        /// Whether shifting the pitch drags the tempo with it.
        ///
        /// Off — the default — the two are independent: the track plays at the
        /// speed it was set to and only the pitch moves. On, they move together,
        /// which is what a tape machine or a turntable does, and is the sound
        /// people mean by "slowed and reverbed" or "chipmunked".
        /// </summary>
        public bool PitchMovesTempo
        {
            get => _pitchMovesTempo;
            set { _pitchMovesTempo = value; ApplyRates(); }
        }

        /// <summary>
        /// Turns the two knobs somebody set into the two the audio path has.
        ///
        /// The stretcher changes length without pitch; the resampler changes
        /// both together. So <c>speed = stretch * resample</c> and
        /// <c>pitch = resample</c>, and getting the wanted pair out of them is
        /// one division:
        ///
        /// - Independent: resample by the pitch, stretch by <c>speed / pitch</c>,
        ///   so the resampler's effect on length is exactly cancelled.
        /// - Linked: resample by the pitch and stretch by the speed alone, so
        ///   the pitch shift is left to carry the tempo with it.
        ///
        /// Both are set together and from one place, because a moment with the
        /// new pitch and the old stretch is a moment at the wrong speed.
        /// </summary>
        private void ApplyRates()
        {
            double stretch = _pitchMovesTempo ? _rate : _rate / _pitchRatio;
            if (!(stretch > 0)) stretch = 1.0;

            var s = _stretch;
            if (s != null) s.Rate = stretch;

            // The resampler carries the rate conversion as well as the pitch,
            // and the two multiply because they are the same operation.
            //
            // Working it through: a source tone at f is at f/sourceRate per
            // sample in the decoder's output; the stretcher preserves that; the
            // resampler stepping by F puts it at F·f/sourceRate per output
            // sample, which played at the device rate is f·F/c where
            // c = sourceRate/deviceRate. Wanting pitch P gives F = P·c — and the
            // stretch rate falls out as S/P exactly as before, so a file at the
            // wrong rate costs nothing anywhere else.
            var p = _pitch;
            if (p != null)
            {
                p.Ratio = _pitchRatio * RateConversion;
                p.SampleRate = _renderer.SampleRate;
            }
        }

        private float[] _ring = Array.Empty<float>();
        private int _channels;
        private int _writeAt, _readAt, _stored;
        private readonly object _ringGate = new();

        private Thread? _pump;
        private volatile bool _pumping;
        private volatile bool _paused;
        private volatile bool _sourceDone;
        private volatile bool _announcedEnd;

        /// <summary>A seek asked for by another thread, in seconds, or NaN.</summary>
        private double _seekTo = double.NaN;

        /// <summary>
        /// Empty reads in a row. Reset by any read that produced audio.
        ///
        /// The end of a track and a track waiting for bytes look identical from
        /// here — both are a read that returned nothing — and the difference
        /// matters because one of them, with repeat on, restarts the song.
        /// </summary>
        private int _emptyReads;

        /// <summary>
        /// How many in a row before believing it. Three, about forty
        /// milliseconds: free at a genuine end, and enough to cover a skip
        /// waiting on a download.
        /// </summary>
        private const int EmptyReadsBeforeEnd = 3;

        /// <summary>
        /// Loop points taken since the last frame of audio. Reset by any read
        /// that produced some, so an ordinary repeat never counts past one.
        /// </summary>
        private int _loopAttempts;

        /// <summary>
        /// How many turns of a decoder that gives nothing back are worth trying
        /// before the track is allowed to end. Two, because the first is the
        /// repeat and the second is the benefit of the doubt.
        /// </summary>
        private const int LoopAttemptsBeforeEnd = 2;

        /// <summary>
        /// Frames in the ring that belong to the pass before the last loop point,
        /// counted down as the renderer plays them. Guarded by
        /// <see cref="_ringGate"/>, like everything else about the ring.
        ///
        /// This is what lets <see cref="Position"/> stay honest across a repeat —
        /// see the comment there. Zero at every other moment, which is every
        /// moment for a track that is not repeating.
        /// </summary>
        private int _lastPassFrames;

        /// <summary>
        /// Whether the end of the track is followed by the start of it.
        ///
        /// Owned here rather than by whoever raises Finished, because gapless
        /// means the turn has to happen where the frames are produced. Anything
        /// further out only finds out after the ring has emptied, and by then
        /// the gap has already been heard.
        /// </summary>
        public bool Loop { get; set; }

        public string Path { get; private set; } = "";

        public string Diagnostic { get; private set; } = "not started";

        /// <summary>Raised once when the track has played to its end.</summary>
        public event Action? Finished;

        /// <summary>
        /// True when the device asked for audio and the ring had none, false when
        /// it comes good again. Raised only on a change, so a listener does not
        /// have to remember which it said last.
        ///
        /// The media engine reported this as an event and it is why the ids
        /// exist; the ring is the thing that actually knows, because it is the
        /// thing that empties. A track whose decode cannot keep up — a share
        /// that has gone slow — is otherwise silence with nothing said about it.
        /// </summary>
        public event Action<bool>? Starved;

        /// <summary>
        /// Raised after a device change, with the device's name and whether it
        /// worked. Raised on the pump thread, so a listener that speaks must get
        /// itself somewhere else first.
        /// </summary>
        public event Action<string, bool>? DeviceSwitched;

        private volatile bool _starving;

        public double Duration => _decoder?.Duration ?? 0;

        /// <summary>
        /// Where playback has actually reached, rather than where the decoder has
        /// read to. Those differ by whatever is sitting in the ring, which is
        /// about a second — enough to make a position display visibly wrong.
        /// </summary>
        public double Position
        {
            get
            {
                var decoder = _decoder;
                if (decoder == null) return 0;

                int queued, lastPass;
                lock (_ringGate)
                {
                    queued = _channels > 0 ? _stored / _channels : 0;
                    lastPass = _lastPassFrames;
                }

                // The decoder has read further than has been heard, and at any
                // speed but normal the two are not the same distance. A frame
                // waiting in the ring is an *output* frame, and it stands for
                // Rate frames of the file; the stretcher is holding source
                // frames on top of that. Counting the ring alone made the
                // position run ahead by the speed, so at four times the clock
                // reached the end of the track a second before the music did.
                // Each frame in the ring is an *output* frame and stands for
                // `_rate` frames of the file, whatever the pitch is doing —
                // because ApplyRates divides the stretch by the pitch precisely
                // so that the two cancel and the speed on the dial is the speed
                // through the source. When the pitch is linked to the tempo they
                // do not cancel, and a ring frame stands for rate * pitch.
                //
                // Both stages hold source frames of their own on top of that.
                // The resampler's are counted in *its* input, which is the
                // stretcher's output, so they are worth `_rate` each as well.
                var stretch = _stretch;
                var pitch = _pitch;

                // In decoder frames, which is what decoder.Position is counted
                // in, so everything has to be converted into them.
                //
                // A ring frame is one device frame. It stands for `speed` source
                // seconds per output second, times the rate conversion — a
                // 44.1kHz file on a 48kHz device advances through itself slower
                // than one frame per frame, and a clock that ignored that ran
                // ahead of the music by eight percent and made every skip land
                // in the wrong place.
                //
                // The resampler's held frames are stretcher *output* frames, so
                // each is worth one stretch rate of decoder frames. The
                // stretcher's own are decoder frames already.
                double speed = _pitchMovesTempo ? _rate * _pitchRatio : _rate;
                double stretchRate = _pitchMovesTempo ? _rate : _rate / _pitchRatio;
                if (!(stretchRate > 0)) stretchRate = 1.0;

                int rate = decoder.SampleRate > 0 ? decoder.SampleRate : 1;

                // The end of the previous pass, still in the ring and still being
                // played, while the decoder is already reading the next one.
                //
                // Subtracting from the decoder is the whole method, and at a loop
                // point the two are no longer measuring the same pass: the
                // decoder has gone back to zero and the ring is still four
                // seconds of the end of the track. The subtraction then goes
                // negative and the clock reads zero — for the whole ring, which
                // is a fifth of a twenty-second track — while the last four
                // seconds of every pass are never reported at all. On the file
                // this was found with, the clock ran 0 to 16 on a track that is
                // 20 seconds long, then sat at 0 for four seconds, over and over.
                //
                // So while the tail is still ahead of the play head, the answer
                // is measured back from the *end* of the track instead.
                if (lastPass > 0)
                {
                    double behind = lastPass * speed * RateConversion / rate;
                    double atEnd = decoder.Duration - behind;
                    return atEnd < 0 ? 0 : atEnd;
                }

                // Silence reduction's held frames are decoder frames, like
                // the stretcher's, because it sits in front of it — the gap it
                // is still deciding about has been read and has not been heard,
                // and a clock that ignored it ran ahead by the whole lookahead.
                // What it has *dropped* is deliberately not counted anywhere:
                // those frames were read and will never be heard, which is what
                // makes the position jump across a gap that was removed.
                double sourceAhead = queued * speed * RateConversion
                                   + (pitch?.HeldSourceFrames ?? 0) * stretchRate
                                   + (stretch?.HeldSourceFrames ?? 0)
                                   + (_silence?.HeldSourceFrames ?? 0);

                double ahead = sourceAhead / rate;
                double at = decoder.Position - ahead;
                return at < 0 ? 0 : at;
            }
        }

        /// <summary>
        /// Whether sound is still coming out, which is not the same question as
        /// whether the decoder is still reading.
        ///
        /// It used to be the second one, and for a short track the two disagree:
        /// anything under the four seconds the ring holds is decoded to the end
        /// almost at once, so `_sourceDone` goes true while every note of it is
        /// still queued and audible. Pausing and resuming such a track answered
        /// "not playing" to a player that was plainly playing — intermittently,
        /// because it depended on whether the decode had finished by then — and
        /// the same answer reaches the menu labels and "say what is playing".
        /// </summary>
        public bool IsPlaying
        {
            get
            {
                if (!_pumping || _paused) return false;
                if (!_sourceDone) return true;

                lock (_ringGate) return _stored > 0;
            }
        }
        public bool IsPaused => _paused;

        /// <summary>Multiplied into every sample. Above one is the whole point.</summary>
        public float Gain
        {
            get => _renderer.Gain;
            set { lock (_swapGate) _renderer.Gain = value; }
        }

        /// <summary>Lift what is quiet; leave what is loud, clipping included.</summary>
        public bool Limiter
        {
            get => _renderer.Limiter;
            set { lock (_swapGate) _renderer.Limiter = value; }
        }

        /// <summary>
        /// The limiter's three shapes, in the units the renderer works in.
        ///
        /// Forwarded rather than held, so a change lands on the sound within one
        /// buffer — about ten milliseconds. That is what makes them adjustable
        /// while a track is playing, which is the only way any of them can be
        /// judged: they are all ways of describing something you have to hear.
        /// </summary>
        public float LimitCeiling
        {
            get => _renderer.LimitCeiling;
            set { lock (_swapGate) _renderer.LimitCeiling = value; }
        }

        public float LimitAttackSeconds
        {
            get => _renderer.LimitAttackSeconds;
            set { lock (_swapGate) _renderer.LimitAttackSeconds = value; }
        }

        public float LimitReleaseSeconds
        {
            get => _renderer.LimitReleaseSeconds;
            set { lock (_swapGate) _renderer.LimitReleaseSeconds = value; }
        }

        /// <summary>The same lift at any volume, rather than one that grows with it.</summary>
        public bool LimiterWholeRange
        {
            get => _renderer.LimiterWholeRange;
            set { lock (_swapGate) _renderer.LimiterWholeRange = value; }
        }

        /// <summary>
        /// The twenty tone controls, forwarded for the same reason the limiter's
        /// three are: a slider that took effect on the next track would be a
        /// slider nobody could judge.
        /// </summary>
        public bool EqualiserOn
        {
            get => _renderer.Equaliser.Enabled;
            set { lock (_swapGate) _renderer.Equaliser.Enabled = value; }
        }

        public int[] EqualiserGains
        {
            get => _renderer.Equaliser.Gains();
            set { lock (_swapGate) _renderer.Equaliser.SetGains(value); }
        }

        public void SetEqualiserBand(int band, int decibels)
        {
            lock (_swapGate) _renderer.Equaliser[band] = decibels;
        }

        /// <summary>
        /// Silence reduction's five controls and its switch.
        ///
        /// Held here as fields and pushed down, rather than forwarded to the
        /// object the way the equaliser's are, because the object does not exist
        /// for most of this class's life: the whole decode chain is built on the
        /// pump thread when a track starts and thrown away when it ends, so a
        /// value that lived only on the stage would go back to its default
        /// between two songs. <see cref="ApplySilence"/> is the one place that
        /// knows the list, and it is called from here and from both of the
        /// places that build a chain.
        /// </summary>
        public bool SilenceOn
        {
            get => _silenceOn;
            set { _silenceOn = value; ApplySilence(); }
        }

        public int SilenceThresholdDecibels
        {
            get => _silenceThresholdDecibels;
            set { _silenceThresholdDecibels = value; ApplySilence(); }
        }

        public int SilenceMinimumMilliseconds
        {
            get => _silenceMinimumMilliseconds;
            set { _silenceMinimumMilliseconds = value; ApplySilence(); }
        }

        public int SilenceLeaveMilliseconds
        {
            get => _silenceLeaveMilliseconds;
            set { _silenceLeaveMilliseconds = value; ApplySilence(); }
        }

        public int SilenceFadeOutMilliseconds
        {
            get => _silenceFadeOutMilliseconds;
            set { _silenceFadeOutMilliseconds = value; ApplySilence(); }
        }

        public int SilenceFadeInMilliseconds
        {
            get => _silenceFadeInMilliseconds;
            set { _silenceFadeInMilliseconds = value; ApplySilence(); }
        }

        private bool _silenceOn;
        private int _silenceThresholdDecibels = -48;
        private int _silenceMinimumMilliseconds = 400;
        private int _silenceLeaveMilliseconds = 150;
        private int _silenceFadeOutMilliseconds = 20;
        private int _silenceFadeInMilliseconds = 20;

        private void ApplySilence()
        {
            var s = _silence;
            if (s == null) return;

            s.ThresholdDecibels = _silenceThresholdDecibels;
            s.MinimumMilliseconds = _silenceMinimumMilliseconds;
            s.LeaveMilliseconds = _silenceLeaveMilliseconds;
            s.FadeOutMilliseconds = _silenceFadeOutMilliseconds;
            s.FadeInMilliseconds = _silenceFadeInMilliseconds;

            // Last, so a switch coming on finds the numbers already where they
            // were asked to be rather than working for one block on the previous
            // set. Every one of these is written while the pump thread is reading
            // them — they are volatile on the stage for exactly that reason — and
            // the order is the only thing this end can decide.
            s.Enabled = _silenceOn;
        }

        /// <summary>The loudest sample since this was last asked, and how much went over.</summary>
        public float ReadPeak() => _renderer.ReadPeak();

        public double ClippedPercent => _renderer.ClippedPercent;

        public bool Start(string path, bool preferRaw, double startAtSeconds = 0,
            System.Runtime.InteropServices.ComTypes.IStream? stream = null,
            string? deviceId = null)
        {
            Stop();

            // Stop marks the player as stopped so a device switch in flight does
            // not install a renderer into it — and this is a new start. Left set,
            // every switch for the life of the track opened the new device, saw
            // "stopped", and closed it again: the device never changed.
            lock (_swapGate) _stopped = false;

            Path = path;
            _preferRaw = preferRaw;
            _download = stream as StreamingSource;

            Watch(_renderer);
            if (!_renderer.Start(Fill, preferRaw, deviceId))
            {
                Diagnostic = "renderer: " + _renderer.Diagnostic;
                return false;
            }

            // The meter is about this track. Left running it would answer "since
            // the process started", which for an application the shell launches
            // on every folder open is a figure about a track nobody remembers.
            _renderer.ResetClipping();

            _channels = _renderer.Channels;
            _ring = new float[AtDeviceRate(RingFrames) * _channels];
            _writeAt = _readAt = _stored = _lastPassFrames = 0;
            _sourceDone = false;
            _announcedEnd = false;
            _paused = false;
            _emptyReads = 0;
            _loopAttempts = 0;

            // Held until the pump has decoded enough to feed the device without
            // running dry. Released in ReleaseWhenBuffered.
            //
            // A flag the *fill callback* reads, deliberately, rather than
            // suspending the renderer. Suspending stops the audio client and
            // releasing it starts the client again, so gating the device that
            // way put an extra Stop/Start on the endpoint at the front of every
            // single track — free on some drivers and emphatically not on
            // others, which is a slow start bought to fix a short one. The
            // device keeps running and is simply handed nothing until there is
            // something worth handing it, which is what it would have got
            // anyway.
            _waitingForBuffer = true;

            // The decoder is opened on the pump thread, for the same reason the
            // renderer opens its device on the render thread: a source reader
            // built on one apartment and read from another fails the
            // cross-apartment QueryInterface with E_NOINTERFACE, on the first
            // ReadSample, from inside a loop whose exceptions nobody sees.
            using var opened = new ManualResetEventSlim(false);
            bool ok = false;
            string why = "";

            _pumping = true;
            _pump = new Thread(() =>
            {
                // Whichever decoder can take the file. Media Foundation for
                // everything Windows decodes, and the managed one for Ogg
                // Vorbis and Opus, which it does not — see TrackDecoder.
                var decoder = TrackDecoder.Open(path, stream,
                    _renderer.SampleRate, _renderer.Channels, out why);
                ok = decoder != null;

                if (decoder == null) { opened.Set(); return; }

                if (startAtSeconds > 0) decoder.SeekTo(startAtSeconds);

                // Built here too, and pointed straight at the decoder. At rate 1
                // it hands the samples through untouched, so the ordinary case
                // is not paying for a feature it is not using.
                // The decoder's shape, not the device's. Everything up to the
                // ring works in whatever the decoder really produces; the
                // conversion to the device happens once, on the way in.
                _sourceRate = decoder.SampleRate;
                _sourceChannels = decoder.Channels < 1 ? 1 : decoder.Channels;

                // First in the chain, straight off the decoder, so the lengths it
                // is given are lengths of the recording rather than of the
                // recording at whatever the speed control is set to. Off it is a
                // bypass and the stretcher reads the decoder through it for the
                // cost of one delegate call.
                _silence = new SilenceReduction(_sourceChannels, _sourceRate)
                {
                    Source = decoder.Read,
                };
                ApplySilence();

                _stretch = new TimeStretch(_sourceChannels) { Source = _silence.Read };

                // Behind the stretcher, not in front of it. The stretcher's job
                // is to fix the length, and it has to be fixing the length of
                // whatever the resampler is actually going to be handed —
                // putting the resampler first would have it changing the length
                // again after the correction had been worked out.
                _pitch = new Resampler(_sourceChannels)
                {
                    Source = _stretch.Read,
                    SampleRate = _renderer.SampleRate,
                };

                // Both rates from the one place that knows how they combine, and
                // after both objects exist — see ApplyRates.
                ApplyRates();

                _decoder = decoder;
                opened.Set();

                try { PumpLoop(); }
                finally
                {
                    _decoder = null;
                    _silence = null;
                    _stretch = null;
                    _pitch = null;
                    decoder.Dispose();
                }
            })
            {
                IsBackground = true,
                Name = "ExplorerNative audio decode",
            };
            _pump.SetApartmentState(ApartmentState.MTA);
            _pump.Start();

            // Longer for a download. A decoder opening an 8GB Matroska file
            // reads its start and its index at the end, and on a busy link each
            // of those is seconds: fifteen failed a file that plays perfectly.
            // Nothing waits behind it — a newer Enter or Stop calls the open
            // off and its reads with it.
            if (!opened.Wait(TimeSpan.FromSeconds(_download != null ? 45 : 15)))
            {
                Diagnostic = "the file did not open in time";
                Stop();
                return false;
            }

            if (!ok)
            {
                Diagnostic = "decoder: " + why;
                Stop();
                return false;
            }

            Diagnostic = _renderer.Diagnostic;

            // Following the default means following it when it changes, not only
            // when the device it was goes away.
            WasapiRenderer.DefaultDeviceChanged += OnDefaultDeviceChanged;

            // And a change made while the track was opening — which on a share
            // can be seconds — was announced to nobody. Checked once now; the
            // pump drops it when there is nothing to do.
            if (Interlocked.CompareExchange(ref _switchTo, DefaultMoved, null) == null)
                try { _drained.Set(); } catch { }
            return true;
        }

        /// <summary>
        /// Turns the decoder round to the start of the track, for repeat. See
        /// the loop point in <see cref="PumpLoop"/>.
        /// </summary>
        private void TakeLoopPoint()
        {
            if (_decoder == null) return;

            // Everything in the ring right now is the end of the pass
            // that has just finished, and it has not been heard yet.
            // Noted before the decoder moves, because after that the
            // two are measuring different passes — see Position.
            lock (_ringGate)
                _lastPassFrames = _channels > 0 ? _stored / _channels : 0;

            _decoder.SeekTo(0);
            _emptyReads = 0;

            // A pass whose end was already announced is a pass that finished;
            // left set, the next end was never announced and the device never
            // let go.
            lock (_ringGate) _announcedEnd = false;
            Wake();

            // Both stages have to be told the source is alive again,
            // and forgetting this is what "moving pitch on repeat
            // breaks it" was.
            //
            // Each latches a "the source has run out" flag the first
            // time a read comes back empty — correctly, because a
            // finished source returns nothing for ever and asking
            // again per sample would be a call to prove it. At the
            // loop point that latch is wrong: the decoder has just
            // been turned round and has plenty to give. Left set,
            // neither stage ever draws again and the track is silent
            // from the first repeat onwards.
            //
            // It only ever showed up after somebody moved the pitch
            // or the speed, because at ratio 1 and rate 1 both are
            // pass-throughs that never latch at all.
            //
            // SourceRestarted, not Reset: what they hold is the tail
            // of the last hop, and letting it cross into the first
            // hop of the new pass is the join being seamless.
            _silence?.SourceRestarted();
            _stretch?.SourceRestarted();
            _pitch?.SourceRestarted();
            
        }

        /// <summary>
        /// Reads ahead into the ring, and does the waiting so the renderer does
        /// not have to.
        /// </summary>
        private void PumpLoop()
        {
            // Sized for the decoder's channel count, which is what the chain
            // above produces, and separately for the device's, which is what the
            // ring holds. They are usually the same number and occasionally are
            // not — see _sourceChannels.
            const int BlockFrames = 4096;
            var block = new float[BlockFrames * Math.Max(1, _sourceChannels)];
            var forRing = new float[BlockFrames * Math.Max(1, _channels)];

            while (_pumping)
            {
                // The device change happens here, on the thread that owns the
                // decoder — reopening it anywhere else is the cross-apartment
                // mistake this file already carries a paragraph about.
                //
                // And *before* the buffers are sized, which is the other half of
                // the paragraph below: sized first, a switch in the same pass
                // changed the channel counts after the check and the pass went on
                // with the old arrays.
                // A device that has gone, and a default that would not open in
                // its place: tried again, less and less often. Windows says
                // nothing when a default that was connecting becomes usable, and
                // the lost device never reports again.
                if (_renderer.Lost && Volatile.Read(ref _switchTo) == null &&
                    Environment.TickCount64 >= _lostRetryAt)
                {
                    Interlocked.CompareExchange(ref _switchTo, DefaultMoved, null);
                    _lostRetryAt = Environment.TickCount64 + _lostRetryDelay;
                    _lostRetryDelay = Math.Min(_lostRetryDelay * 2, 30_000);
                }

                var switchTo = Interlocked.Exchange(ref _switchTo, null);
                if (switchTo != null) SwitchNow(switchTo);

                // Both are sized from channel counts a device switch can change
                // underneath this loop — and the loop keeps the arrays it was
                // started with. Moving a playing track from stereo speakers to a
                // 5.1 endpoint asked the decoder for six channels into a buffer
                // holding two, which throws on a background thread with nobody to
                // catch it: the application does not go quiet, it exits. Two
                // comparisons an iteration is what that costs to prevent.
                int wantBlock = BlockFrames * Math.Max(1, _sourceChannels);
                if (block.Length != wantBlock) block = new float[wantBlock];

                int wantRing = BlockFrames * Math.Max(1, _channels);
                if (forRing.Length != wantRing) forRing = new float[wantRing];

                // Taken in one step. Reading it and then clearing it lost any
                // skip that arrived in between.
                double seek = Interlocked.Exchange(ref _seekTo, double.NaN);
                if (!double.IsNaN(seek))
                {
                    // Cleared *before* the ring is emptied, not after, and that
                    // order is the whole of a real bug.
                    //
                    // Fill runs on the render thread and treats "nothing in the
                    // ring and the source is finished" as the end of the track.
                    // Emptying the ring first opened a window where both were
                    // true at once — the ring already zeroed, _sourceDone not
                    // yet cleared — so a seek made near the end of a track
                    // raised Finished. With repeat switched on, Finished seeks
                    // back to zero, and what you hear after skipping is the
                    // beginning of the song again. Reported as "skipping repeats
                    // the first audio segment", and it needs no slow disk to
                    // happen, only the two operations landing in that order.
                    lock (_ringGate)
                    {
                        _sourceDone = false;
                        _announcedEnd = false;
                    }

                    // A skip starts the end-of-track question over. Empty reads
                    // counted against the position we have just left say nothing
                    // about the one we are going to.
                    _emptyReads = 0;
                    _loopAttempts = 0;

                    _decoder?.SeekTo(seek);
                    Wake();

                    // The stretcher is holding audio from where the track used
                    // to be, and half of a crossfade to go with it. Kept, it
                    // plays a fifth of a second of the old position after the
                    // jump and joins it to the new one.
                    //
                    // The resampler holds the same kind of thing — up to a
                    // thousand input frames of the old position, plus a
                    // fractional read cursor into them — so it is emptied on the
                    // same terms and for the same reason.
                    // And the reduction stage in front of them, holding a gap
                    // it has not decided about yet — audio from where the track
                    // used to be, on exactly the terms the two stages below it
                    // are emptied for.
                    _silence?.Reset();
                    _stretch?.Reset();
                    _pitch?.Reset();

                    lock (_ringGate) { _writeAt = _readAt = _stored = _lastPassFrames = 0; }
                }

                // Every time round, not only after a read that produced
                // something. A file shorter than the prebuffer floor reaches
                // _sourceDone without ever filling it, and a release that only
                // happened on a successful write left the device suspended for
                // ever — measured as a track that plays in total silence, which
                // is a far worse gap than the one the floor exists to close.
                ReleaseWhenBuffered();

                int stored, room;
                lock (_ringGate)
                {
                    stored = _stored / Math.Max(1, _channels);
                    room = (_ring.Length - _stored) / Math.Max(1, _channels);
                }

                // Only up to the level this track needs, not up to the brim. The
                // difference between those two is how long it takes a change to
                // the pitch, the speed or silence reduction to be heard — see
                // ShallowFrames.
                room = Math.Min(room, TargetFrames() - stored);

                // Repeat switched on after the decoder had already run out: the
                // end has not been heard yet, so the loop point is still ahead
                // of the listener and is taken now rather than never.
                if (_sourceDone && Loop && _decoder != null && _loopAttempts == 0 && !_announcedEnd)
                {
                    _sourceDone = false;
                    TakeLoopPoint();
                    continue;
                }

                if (room < 1024 || _sourceDone)
                {
                    // Woken when the renderer takes frames, so a playing track
                    // reacts immediately and a paused one costs four wakeups a
                    // second instead of two hundred. The timeout is the safety
                    // net for a signal that arrives while nothing is waiting.
                    _drained.WaitOne(250);
                    continue;
                }

                int want = Math.Min(room, BlockFrames);

                // From the end of the chain, which pulls the stretcher, which
                // pulls the decoder. At normal pitch the resampler hands the
                // stretcher's buffer straight through untouched.
                int got = _pitch?.Read(block, want)
                          ?? _stretch?.Read(block, want)
                          ?? _silence?.Read(block, want) ?? 0;

                if (got <= 0)
                {
                    // One empty read is not the end of the track, and treating
                    // it as one is expensive: the end of the track raises
                    // Finished, and Finished with repeat switched on seeks back
                    // to zero — so a momentary gap plays the song again from the
                    // beginning, which is a very loud way to report a hiccup.
                    //
                    // A decoder reading through a download can come up empty
                    // without being finished. A skip lands past what has
                    // arrived, the bytes are on their way, and Media Foundation
                    // hands back nothing for a moment rather than blocking. A
                    // decoder that really has run out returns nothing for ever,
                    // so asking three times over about forty milliseconds costs
                    // nothing at the true end and covers the gap at a skip.
                    //
                    // The stages in front of the decoder each latch "the source
                    // has run out" on their first empty read, so each retry tells
                    // them otherwise — without that, a retry only ever asked
                    // stages that had stopped asking, and a pause in a download
                    // ended the track whenever silence reduction, speed or pitch
                    // was in use.
                    if (++_emptyReads < EmptyReadsBeforeEnd)
                    {
                        Thread.Sleep(15);
                        _silence?.SourceRestarted();
                        _stretch?.SourceResumed();
                        _pitch?.SourceRestarted();
                        continue;
                    }

                    // Repeat happens here, and that is what makes it gapless.
                    //
                    // It used to happen at the other end of the track's life: the
                    // decoder ran out, the pump gave up, the ring drained to
                    // nothing, the *render* thread noticed the silence and raised
                    // Finished, and AudioPlayer answered by asking for a seek to
                    // zero — which the pump then applied by emptying the ring
                    // again and refilling it from cold. Every one of those steps
                    // is a real delay and the ring is empty for all of them, so
                    // a repeat was most of a second of silence between the last
                    // note and the first.
                    //
                    // Turning the decoder round here instead means the ring never
                    // empties: the frames after the end of the track are the
                    // frames from the start of it, written into the same buffer
                    // in the same pass. Nothing is cleared and nothing waits.
                    // A loop point that produced nothing is taken twice and then
                    // given up on, and the track ends the ordinary way instead.
                    //
                    // Without the count this is an infinite loop that makes no
                    // sound and says nothing: turn the decoder round, read
                    // nothing, turn it round again, for as long as the
                    // application is open. That is exactly what an Opus file did
                    // before the reader could be turned round at all — the clock
                    // stopped at zero and the track appeared to be playing. A
                    // decoder that cannot restart is a rare thing and a silent
                    // hang is the worst possible way to report it.
                    if (Loop && _decoder != null && ++_loopAttempts <= LoopAttemptsBeforeEnd)
                    {
                        TakeLoopPoint();
                        continue;
                    }

                    _sourceDone = true;
                    continue;
                }

                _emptyReads = 0;
                _loopAttempts = 0;

                // Thrown away if another skip arrived while this read was in
                // flight, and this is the whole of "holding the skip key repeats
                // segments of the track".
                //
                // That read is not always quick. On the Drive letter a skip
                // forward lands past whatever has been downloaded, so the decode
                // behind it goes to the network — hundreds of milliseconds. A
                // held key fires eight times a second. So by the time a block
                // comes back, two or three further skips have been asked for and
                // this audio belongs to a position nobody is at any more.
                //
                // It used to be written to the ring regardless. The ring is
                // empty at that instant — the seek that started this read
                // emptied it — and the renderer is starving, so those stale
                // frames went straight out of the speakers before the next seek
                // could clear them. Held down, that is a burst of eighty
                // millisecond fragments from every position the key passed
                // through, which is exactly what it sounds like.
                //
                // Checked after the read rather than before, because the whole
                // point is that the world changed *during* it.
                if (!double.IsNaN(Volatile.Read(ref _seekTo))) continue;

                // Into the device's channel layout. A no-op when they match,
                // which is nearly always — and the difference between music and
                // a fast, torn mess when they do not.
                var writing = ToDeviceChannels(block, forRing, got);

                lock (_ringGate)
                {
                    // Two copies at most — up to the end of the ring, then from its
                    // start — rather than a modulo per sample under the lock the
                    // render thread waits on.
                    int values = got * _channels;
                    int first = Math.Min(values, _ring.Length - _writeAt);
                    Array.Copy(writing, 0, _ring, _writeAt, first);
                    if (values > first) Array.Copy(writing, first, _ring, 0, values - first);
                    _writeAt = (_writeAt + values) % _ring.Length;
                    _stored += values;
                }
            }
        }

        /// <summary>
        /// Lays the decoder's channels out the way the device wants them.
        ///
        /// The decoder is asked for the device's channel count and does not
        /// always agree — see <see cref="_sourceChannels"/>. Writing its frames
        /// into a ring interleaved for a different count was reading past the
        /// end of its own buffer and playing the result at the wrong speed,
        /// which is one of the two things "some files play fast" turned out to
        /// be.
        ///
        /// Mono to many duplicates, which is what putting one channel on several
        /// speakers means. Many to one averages, because taking the left channel
        /// and calling it the mix silently loses whatever was only on the right.
        /// Anything else copies what lines up and leaves the rest silent: a
        /// five-channel film mix folded properly is a job for a matrix nobody
        /// here has asked for, and inventing one quietly would be worse than
        /// playing the channels that exist.
        /// </summary>
        private float[] ToDeviceChannels(float[] source, float[] destination, int frames)
        {
            int from = Math.Max(1, _sourceChannels);
            int to = Math.Max(1, _channels);
            if (from == to) return source;

            if (from == 1)
            {
                for (int f = 0; f < frames; f++)
                {
                    float v = source[f];
                    int at = f * to;
                    for (int c = 0; c < to; c++) destination[at + c] = v;
                }
                return destination;
            }

            if (to == 1)
            {
                for (int f = 0; f < frames; f++)
                {
                    double sum = 0;
                    int at = f * from;
                    for (int c = 0; c < from; c++) sum += source[at + c];
                    destination[f] = (float)(sum / from);
                }
                return destination;
            }

            int shared = Math.Min(from, to);
            for (int f = 0; f < frames; f++)
            {
                int in_ = f * from, out_ = f * to;
                for (int c = 0; c < shared; c++) destination[out_ + c] = source[in_ + c];
                for (int c = shared; c < to; c++) destination[out_ + c] = 0f;
            }
            return destination;
        }

        /// <summary>
        /// Lets the device start once there is enough in the ring to feed it.
        ///
        /// The renderer is opened suspended and stays that way until half a
        /// second has been decoded, because a device started against an empty
        /// ring asks for buffers that are not there and gets silence — a gap and
        /// a click at the front of every track, worst on exactly the tracks that
        /// are slowest to arrive.
        ///
        /// A source that has already finished releases it whatever the level
        /// is, or a file shorter than the floor would never start at all.
        /// </summary>
        private void ReleaseWhenBuffered()
        {
            if (!_waitingForBuffer) return;

            int stored;
            lock (_ringGate) stored = _stored;

            // Never more than this track is going to be given: a shallow ring
            // that is already at its level is as buffered as it will ever be, and
            // a floor above it would hold the device suspended for ever.
            int floor = Math.Min(AtDeviceRate(PrebufferFrames), TargetFrames()) * Math.Max(1, _channels);

            if (!_sourceDone && stored < floor) return;

            _waitingForBuffer = false;
        }

        /// <summary>
        /// How many frames the pump keeps in the ring.
        ///
        /// Deep only while a download is still racing playback. A local file has
        /// never needed it, and a remote one stops needing it the moment the last
        /// byte lands — everything after that is a read from memory. See
        /// <see cref="ShallowFrames"/> for why the difference is worth having.
        /// </summary>
        private int TargetFrames()
        {
            var download = _download;

            // Every byte in memory, not <see cref="StreamingSource.Complete"/>.
            // Complete means the *cache budget* is full, and the budget is capped
            // — so a track bigger than it is "complete" with most of itself still
            // on the network. That is not hypothetical: the folder this was found
            // in holds a 1.3GB Ogg, against a budget of one gigabyte. Asking the
            // plainer question costs nothing and cannot be wrong in the direction
            // that stutters.
            return download != null && download.Available < download.Length
                ? AtDeviceRate(RingFrames)
                : AtDeviceRate(ShallowFrames);
        }

        /// <summary>
        /// A frame count written for 48kHz, at the device's rate. The ring is four
        /// seconds and the shallow level a fifth of one; as frame counts they were
        /// a twentieth of a second on a 384kHz device and over a second of lag on
        /// a 16kHz headset.
        /// </summary>
        private int AtDeviceRate(int framesAt48k)
        {
            int rate = _renderer.SampleRate;
            if (rate <= 0) rate = 48000;
            return (int)Math.Max(1, (long)framesAt48k * rate / 48000);
        }

        /// <summary>True until the ring has enough for the device to be started.</summary>
        private volatile bool _waitingForBuffer;

        /// <summary>True while two renderers exist and only one of them is right.</summary>
        private volatile bool _switching;

        /// <summary>
        /// The renderer's callback. Memory only — no locks held for long, no I/O,
        /// nothing that can block. Everything expensive already happened on the
        /// pump thread.
        /// </summary>
        private int Fill(float[] destination, int frames)
        {
            if (_paused) return 0;

            // A device switch starts the new renderer before it stops the old
            // one, so for a device period or two both render threads are calling
            // this. They do not agree about the format: the frames in the ring
            // are laid out for whichever channel count is current, and the
            // buffer being written belongs to whichever renderer called. With
            // the counts different that is either an overrun — swallowed by the
            // render loop, taking the frames it had already consumed with it —
            // or the new device playing the tail of somebody else's buffer as
            // though it were music. A few milliseconds of silence is the honest
            // answer while the two overlap.
            if (_switching) return 0;

            // Still filling for the first time. Nothing is taken from the ring
            // until there is enough in it to keep going, so the first thing the
            // device plays is the start of the track rather than the start of
            // the track with holes in it.
            if (_waitingForBuffer) return 0;

            int values = Math.Min(frames * _channels, destination.Length);
            int taken = 0;

            lock (_ringGate)
            {
                // Two copies at most, around the end of the ring.
                taken = Math.Min(values, _stored);
                int first = Math.Min(taken, _ring.Length - _readAt);
                Array.Copy(_ring, _readAt, destination, 0, first);
                if (taken > first) Array.Copy(_ring, 0, destination, first, taken - first);
                _readAt = (_readAt + taken) % _ring.Length;
                _stored -= taken;

                // The tail of the previous pass is at the front of the ring, so
                // whatever was just played comes off it first. Two integer
                // operations, which is all this thread can afford — see Position
                // for what they are for.
                if (_lastPassFrames > 0 && _channels > 0)
                    _lastPassFrames -= Math.Min(taken / _channels, _lastPassFrames);
            }

            // Room has opened, so the pump has something to do. Cheap: setting an
            // event nobody is waiting on is a few instructions, and it is what
            // lets the pump wait instead of poll.
            if (taken > 0) { try { _drained.Set(); } catch { } }

            // The source is finished and the ring is empty: that is the end of
            // the track, and it is this thread that knows it first.
            if (taken == 0 && _sourceDone && !_announcedEnd && !_switching)
            {
                // Decided under the ring's lock, which a seek and a loop point
                // take to start the end-of-track question over and to wake the
                // device. Apart, a device suspended just after being woken stayed
                // suspended, and an end announced just after being cleared made a
                // playing track read as ended.
                bool ended = false;
                lock (_ringGate)
                {
                    if (_sourceDone && !_announcedEnd && _stored == 0)
                    {
                        _resting = true;
                        _announcedEnd = true;

                        // The device is let go, as a pause does: a finished track
                        // left loaded used to keep a Highest-priority thread
                        // feeding the endpoint silence for as long as it stayed
                        // there. Only a flag; the render loop stops the client.
                        _renderer.SetSuspended(true);
                        ended = true;
                    }
                }
                if (ended) ThreadPool.QueueUserWorkItem(_ => { try { Finished?.Invoke(); } catch { } });
            }

            // Short of what the device asked for, with the source still going:
            // the decode is not keeping up and there is about to be a gap. Not
            // raised at the end of a track, where coming up short is the point.
            bool starving = taken < values && !_sourceDone;
            if (starving != _starving)
            {
                _starving = starving;

                // Never inline. This is the render callback and it has a
                // deadline; a listener that speaks would miss the next buffer.
                ThreadPool.QueueUserWorkItem(_ => { try { Starved?.Invoke(starving); } catch { } });
            }

            return _channels > 0 ? taken / _channels : 0;
        }

        public void Pause()
        {
            _paused = true;

            // The device stops too, rather than being fed silence a hundred
            // times a second by a thread at Highest priority for as long as the
            // track sits paused. See WasapiRenderer.SetSuspended.
            _renderer.SetSuspended(true);
        }

        public void Resume()
        {
            _paused = false;
            _resting = false;
            _renderer.SetSuspended(false);

            // The pump may be sitting out a timeout with a full ring; nothing
            // will have drained while the device was stopped.
            try { _drained.Set(); } catch { }
        }

        /// <summary>
        /// Moves to another playback device without stopping the music.
        ///
        /// Empty means the system default. Picked up by the pump, which owns the
        /// decoder — see <see cref="SwitchNow"/> for what it costs.
        /// </summary>
        public void SwitchDevice(string deviceId)
        {
            Interlocked.Exchange(ref _switchTo, deviceId ?? "");
            try { _drained.Set(); } catch { }
        }

        /// <summary>
        /// The switch itself, on the pump thread.
        ///
        /// The ring is kept when the new device wants the same format, which is
        /// the usual case and the reason this is quick: the decoder is untouched,
        /// the audio already read stays read, and the only gap is the time it
        /// takes to open the device — tens of milliseconds. Nothing is reloaded
        /// and nothing is re-decoded.
        ///
        /// A device that mixes at a different rate is the exception. The decoder
        /// is producing at the old device's rate, and handing those samples to
        /// the new one would play the track at the wrong speed and pitch, so the
        /// decoder is reopened at the new rate and seeked back to where the music
        /// had reached. That costs a file open; from memory or a local disk it is
        /// still fast, and it is the honest price of the two devices disagreeing.
        /// </summary>
        /// <summary>
        /// A switch to the default queued because the default moved, rather than
        /// asked for. Dropped when a device picked by name is playing by the time
        /// it is reached, so it cannot undo a pick in whatever order the two
        /// arrive. Not a device id: those are braced GUID paths.
        /// </summary>
        private const string DefaultMoved = "default-moved";

        // The retry after a lost device, on the pump thread only.
        private long _lostRetryAt;
        private int _lostRetryDelay = 2000;
        private bool _lostFailureSaid;

        private void SwitchNow(string deviceId)
        {
            if (deviceId == DefaultMoved)
            {
                var playing = _renderer;
                if (playing.DeviceId.Length != 0 && !playing.Lost) return;
                SwitchCore("", automatic: true);
                return;
            }
            SwitchCore(deviceId, automatic: false);
        }

        /// <param name="automatic">
        /// Queued by the player itself — a default that moved, a device that
        /// went — rather than picked. Only those are kept quiet on a retry.
        /// </param>
        private void SwitchCore(string deviceId, bool automatic)
        {
            var decoder = _decoder;
            if (decoder == null) return;

            // Already on the default, and it is still there: a reopen of the same
            // endpoint is a gap for nothing.
            if (deviceId.Length == 0 && _renderer.DeviceId.Length == 0 && !_renderer.Lost &&
                WasapiRenderer.DefaultEndpointId() is { } current &&
                string.Equals(current, _renderer.EndpointId, StringComparison.OrdinalIgnoreCase))
                return;

            double at = Position;

            var old = _renderer;

            // Raised for the whole of the handover. Fill answers silence while
            // it is up, because between starting the new renderer and stopping
            // the old one there are two render threads and only one of them
            // matches the ring.
            _switching = true;

            var fresh = new WasapiRenderer
            {
                Gain = old.Gain,
                Limiter = old.Limiter,
                LimiterWholeRange = old.LimiterWholeRange,
                LimitCeiling = old.LimitCeiling,
                LimitAttackSeconds = old.LimitAttackSeconds,
                LimitReleaseSeconds = old.LimitReleaseSeconds,
            };

            // Carried across by value rather than by sharing the object. Both
            // renderers are running for the moment between here and old.Stop(),
            // and a filter's state belongs to one render thread — handing the
            // same one to two of them is a race over a few hundred samples of
            // somebody's music.
            fresh.Equaliser.Enabled = old.Equaliser.Enabled;
            fresh.Equaliser.SetGains(old.Equaliser.Gains());

            Watch(fresh);
            if (!fresh.Start(Fill, _preferRaw, string.IsNullOrEmpty(deviceId) ? null : deviceId))
            {
                // The old one is still running and still has the track. A device
                // that will not open is a reason to stay where we are, not a
                // reason to go silent.
                Diagnostic = old.Diagnostic;
                _lastSwitchProblem = "could not open that device: " + fresh.Diagnostic;

                var refused = NameFor(deviceId);
                fresh.Dispose();
                _switching = false;

                // Staying put means staying on the default the old renderer
                // followed, which may have moved while the other device was
                // opening — or on a device that has gone. Asked again rather
                // than remembered: a default move is skipped when there is
                // nothing to do. A switch already waiting has its turn first.
                //
                // Only after a named device failed. A failed move to the default
                // queued again is the same failure, retried as fast as a device
                // can refuse — silence and a spoken error each time. A default
                // that moves meanwhile is queued by the notification anyway.
                if (deviceId.Length != 0 && (old.DeviceId.Length == 0 || old.Lost) &&
                    Interlocked.CompareExchange(ref _switchTo, DefaultMoved, null) == null)
                    try { _drained.Set(); } catch { }

                // The retries after a lost device say so once, not every time.
                // Anything picked is always answered.
                bool retrying = automatic && old.Lost;
                if (!(retrying && _lostFailureSaid))
                    try { DeviceSwitched?.Invoke(refused, false); } catch { }
                if (retrying) _lostFailureSaid = true;
                return;
            }

            bool sameFormat = fresh.SampleRate == old.SampleRate && fresh.Channels == old.Channels;

            // Stop can give up waiting for this thread while a device takes its
            // time to open — ten seconds for some Bluetooth endpoints — and stop
            // the renderer it can see. Swapping in a new one after that left a
            // Highest-priority thread holding an endpoint for the life of the
            // process. The swap and Stop agree under one lock instead.
            lock (_swapGate)
            {
                if (_stopped)
                {
                    fresh.Stop();
                    fresh.Dispose();
                    _switching = false;
                    return;
                }

                // Again, from the old renderer, because a Bluetooth device can
                // take seconds to open and every change made meanwhile — mute,
                // the volume, the equaliser — went to the renderer about to be
                // thrown away.
                fresh.Gain = old.Gain;
                fresh.Limiter = old.Limiter;
                fresh.LimiterWholeRange = old.LimiterWholeRange;
                fresh.LimitCeiling = old.LimitCeiling;
                fresh.LimitAttackSeconds = old.LimitAttackSeconds;
                fresh.LimitReleaseSeconds = old.LimitReleaseSeconds;
                fresh.Equaliser.Enabled = old.Equaliser.Enabled;
                fresh.Equaliser.SetGains(old.Equaliser.Gains());

                _renderer = fresh;
            }

            _lostRetryAt = 0;
            _lostRetryDelay = 2000;
            _lostFailureSaid = false;

            old.Stop();
            old.Dispose();

            if (!sameFormat)
            {
                _channels = fresh.Channels;
                _ring = new float[AtDeviceRate(RingFrames) * _channels];

                lock (_ringGate) { _writeAt = _readAt = _stored = _lastPassFrames = 0; }

                decoder.Reopen(fresh.SampleRate, fresh.Channels);
                decoder.SeekTo(at);

                // What it settled on this time, which need not be what was asked
                // for any more than it was the first time.
                _sourceRate = decoder.SampleRate;
                _sourceChannels = decoder.Channels < 1 ? 1 : decoder.Channels;

                // Rebuilt here too, and for the reason the resampler below it
                // says: a stage left pointing at the old chain is a stage the
                // pump no longer reads through, so the effect silently stops
                // happening the moment somebody changes output device. Its
                // settings come back through ApplySilence, which is why they are
                // kept on this object rather than on the stage.
                _silence = new SilenceReduction(_sourceChannels, _sourceRate)
                {
                    Source = decoder.Read,
                };
                ApplySilence();

                _stretch = new TimeStretch(_sourceChannels) { Source = _silence.Read };

                // And the resampler, which this rebuild used to forget. Without
                // it the pump went on reading through the *old* one — pointed at
                // a stretcher that had been replaced — so moving to a device
                // with a different format silently dropped the pitch shift and
                // left a dead object in the chain.
                _pitch = new Resampler(_sourceChannels)
                {
                    Source = _stretch.Read,
                    SampleRate = fresh.SampleRate,
                };

                // Both rates together, from the one place that knows how they
                // combine — see ApplyRates.
                ApplyRates();

                _sourceDone = false;
                _announcedEnd = false;
                _emptyReads = 0;
                _loopAttempts = 0;
            }

            // From the state now, not the state when the switch began: a pause or
            // a resume during a slow open went to the old renderer, which is gone.
            fresh.SetSuspended(_paused || _resting);

            _lastSwitchProblem = "";
            Diagnostic = fresh.Diagnostic;
            _switching = false;

            try { DeviceSwitched?.Invoke(DisplayNameOf(fresh), true); } catch { }
        }

        /// <summary>What to call a device in a sentence.</summary>
        private static string DisplayNameOf(WasapiRenderer renderer) =>
            renderer.DeviceName.Length > 0 ? renderer.DeviceName : "the default device";

        private static string NameFor(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return "the default device";
            foreach (var device in WasapiRenderer.Devices())
                if (device.Id == deviceId) return device.Name;
            return "that device";
        }

        private string _lastSwitchProblem = "";

        /// <summary>Why the last device change did not happen, or empty.</summary>
        public string SwitchProblem => _lastSwitchProblem;

        /// <summary>Moves to a position in seconds; the pump picks it up.</summary>
        public void SeekTo(double seconds) => Volatile.Write(ref _seekTo, Math.Max(0, seconds));

        /// <summary>
        /// The track has played to its end and said so. Asked rather than
        /// worked out from the position against the length, which a track that
        /// ended early — a truncated file, a download that gave up — never
        /// reaches.
        /// </summary>
        public bool Ended => _announcedEnd;

        public void Stop()
        {
            _pumping = false;
            WasapiRenderer.DefaultDeviceChanged -= OnDefaultDeviceChanged;

            var pump = _pump;
            _pump = null;
            try { pump?.Join(2000); } catch { }

            // The pump owns the decoder and disposes it as it leaves, so there is
            // nothing to dispose here — and disposing it from this thread would
            // be the same cross-apartment mistake in the other direction.
            WasapiRenderer renderer;
            lock (_swapGate)
            {
                _stopped = true;
                renderer = _renderer;
            }
            renderer.Stop();

            lock (_ringGate) { _writeAt = _readAt = _stored = _lastPassFrames = 0; }
            Path = "";
        }

        public void Dispose()
        {
            Stop();

            WasapiRenderer renderer;
            lock (_swapGate) renderer = _renderer;
            renderer.Dispose();
        }

        private readonly object _swapGate = new();
        private bool _stopped;

        /// <summary>
        /// True once a track has played to its end and the device has been let
        /// go, which is not the same as paused: nobody asked for it, and a skip
        /// or a repeat has to wake it without a play press.
        /// </summary>
        private volatile bool _resting;

        /// <summary>Wakes a device that was let go at the end of the track, unless it is paused.</summary>
        private void Wake()
        {
            // Under the ring's lock, as the end of a track is decided: otherwise
            // the render thread could suspend the device just after this woke it,
            // and nothing would wake it again.
            lock (_ringGate)
            {
                if (!_resting) return;
                _resting = false;
                if (!_paused) _renderer.SetSuspended(false);
            }
        }

        /// <summary>
        /// Moves to the default device when the one playing disappears — the
        /// same move a person would make from the device list, and the one that
        /// keeps a track playing when headphones are pulled out.
        /// </summary>
        private void Watch(WasapiRenderer renderer)
        {
            renderer.DeviceLost += () =>
            {
                if (!_pumping || !ReferenceEquals(renderer, _renderer)) return;

                // Behind a pick already waiting, not over it: the pick is where
                // the music was going anyway.
                if (Interlocked.CompareExchange(ref _switchTo, DefaultMoved, null) == null)
                    try { _drained.Set(); } catch { }
            };
        }

        /// <summary>
        /// The system default moved. Followed when the device playing is the
        /// default rather than one chosen by name, and when it is not already
        /// the endpoint the default now names.
        /// </summary>
        private void OnDefaultDeviceChanged(string endpoint)
        {
            if (!_pumping || endpoint.Length == 0) return;

            // Queued as a default move whatever is going on, and sorted out by
            // the pump when it gets there: dropped if a device picked by name is
            // playing by then, skipped if the default is already what plays.
            // Deciding here, from switches caught half way, lost a move or
            // undid a pick depending on which instruction the race landed on.
            // Behind anything already waiting nothing is queued — a named pick
            // goes first, and if it fails it queues this again.
            if (Interlocked.CompareExchange(ref _switchTo, DefaultMoved, null) == null)
                try { _drained.Set(); } catch { }
        }
    }
}
