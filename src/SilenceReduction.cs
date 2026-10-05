using System;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// Shortens the silences: a gap that lasts longer than you asked for is cut
    /// down to the length you asked it to be left at, with a fade out into it and
    /// a fade in out of it.
    ///
    /// It is a <em>pull stage</em>, like <see cref="TimeStretch"/> and
    /// <see cref="Resampler"/> — it has a <see cref="Source"/>, it is read from,
    /// and it hands back fewer frames than it consumed. That is the whole reason
    /// it cannot live where the equaliser and the limiter live: those are
    /// arithmetic on a buffer that goes in and comes out the same length, and
    /// this one changes how long the music is. <see cref="WasapiRenderer"/> has
    /// nowhere to put a stage that does that.
    ///
    /// **It sits in front of the stretcher, at the decoder's own rate.** So the
    /// numbers mean what they say: "gaps longer than four hundred milliseconds"
    /// is four hundred milliseconds of the recording, not of the recording played
    /// at whatever speed the speed control is on. Behind the stretcher the same
    /// setting would mean a different length of music at every speed, which is a
    /// control that cannot be learned.
    ///
    /// **Off is a bypass, bit for bit.** Not "a threshold nothing crosses" — the
    /// samples are handed straight from the source to the caller and never
    /// copied, which is what <see cref="TimeStretch"/> promises at rate 1 and
    /// what <see cref="Equaliser"/> promises when it is switched off. It is also
    /// what makes the switch in the dialog worth having: it compares this
    /// against nothing at all rather than against a slightly rounded version of
    /// nothing.
    ///
    /// **The clock is not lied to.** Frames dropped here are frames the decoder
    /// has genuinely read past, so the position runs on through a gap that was
    /// removed — which is the truth about where in the file the music is. What
    /// is held and not yet heard is reported by <see cref="HeldSourceFrames"/>,
    /// exactly as the two stages behind it report theirs, or the clock would run
    /// ahead by the length of the lookahead.
    /// </summary>
    internal sealed class SilenceReduction
    {
        /// <summary>
        /// The most frames one decision is ever made about.
        ///
        /// A hundred and twenty-eight frames is under three milliseconds at
        /// 48kHz, which is short enough that a cut lands where the ear says the
        /// gap started and long enough to hold a peak worth reading. Shorter
        /// blocks are not more accurate, they are noisier: a 50Hz waveform spends
        /// whole milliseconds near zero on its way past, so a detector looking at
        /// half a millisecond calls the middle of a bass note silence.
        ///
        /// The *fades* are applied frame by frame regardless. Only the decision
        /// is taken a block at a time.
        /// </summary>
        public const int Detect = 128;

        /// <summary>
        /// The fewest, for a minimum gap short enough that the block above would
        /// be the real floor.
        ///
        /// <see cref="DetectFrames"/> takes a quarter of whatever the minimum gap
        /// is set to, between these two. That matters because the minimum goes
        /// down to a single millisecond, and a decision taken in blocks of 2.7ms
        /// cannot tell one millisecond from three — the control would stop
        /// meaning anything below the block size while still offering the
        /// numbers, which is a dial with nothing on the end of it.
        ///
        /// At any ordinary setting this changes nothing: a quarter of twenty
        /// milliseconds is already past the cap, so everything from 20ms upwards
        /// decides in the same 128-frame blocks it always did. It only bites at
        /// the bottom of the range, which is where it is wanted, and it is noisy
        /// there on purpose — asking for gaps of a millisecond is asking for a
        /// detector that reacts inside one cycle of a bass note.
        /// </summary>
        public const int SmallestDetect = 8;

        /// <summary>
        /// What counts as silence, in decibels below full scale.
        ///
        /// The top of the range is deliberately absurd. Minus eighty is the noise
        /// floor of a good recording and is the setting for "take out the gaps
        /// between the tracks"; minus one is very nearly everything, and what it
        /// does is chop the recording into the handful of samples that touch full
        /// scale. That is not a mistake in the range — it is a thing somebody
        /// asked to be able to do, and the whole point of a control you can hear
        /// moving is that the far end of it is reachable.
        ///
        /// Nought is not offered, and that is the one bound here that is a real
        /// limit rather than a taste: at nought decibels nothing is above the
        /// threshold, the whole recording is a gap, and the stage correctly hands
        /// back nothing at all. A setting whose only outcome is silence is not a
        /// setting, it is a way to make the player look broken.
        /// </summary>
        public const int LowestThresholdDecibels = -80;
        public const int HighestThresholdDecibels = -1;

        /// <summary>
        /// How long a gap has to last before anything is done to it.
        ///
        /// Twenty milliseconds is shorter than the gap between two words and
        /// about as long as one closed consonant. Five seconds only touches the
        /// silence between movements. One millisecond — the floor — is shorter
        /// than a single cycle of anything below a kilohertz, so what it takes
        /// out is not gaps at all but the part of every waveform that passes near
        /// zero. That is a sound rather than a setting, and it is reachable for
        /// the same reason the rest of these ranges run past the useful part.
        /// </summary>
        public const int ShortestMinimumMilliseconds = 1;
        public const int LongestMinimumMilliseconds = 5000;

        /// <summary>
        /// How much of the gap survives, in milliseconds. Zero is a hard cut
        /// straight from the last sound to the next.
        /// </summary>
        public const int LongestLeaveMilliseconds = 2000;

        /// <summary>
        /// How long each fade is. Two of them, set separately, because they are
        /// two different events: one is the music going away and the other is the
        /// music coming back, and there is no reason the same number should be
        /// right for both.
        ///
        /// **The fade out ends at the cut**, which is where the sound stops. It
        /// is laid across the tail of the gap that is kept, and back into the
        /// music behind it when it is longer than that gap — so it is never
        /// limited by how much of the gap is left.
        ///
        /// Reaching backwards into music that has already been decided about is
        /// what <see cref="_line"/> is for. An earlier version took the fade out
        /// of the front of the kept gap and had nowhere to put one when nothing
        /// was being kept: "shorten them to nothing" silently meant "and splice
        /// it", however long the fade was set to. A control that is quietly
        /// overruled by another control is not a control.
        /// </summary>
        public const int LongestFadeMilliseconds = 500;

        private readonly int _channels;
        private readonly int _rate;

        /// <summary>Where the frames come from: fills a buffer, returns frames.</summary>
        public Func<float[], int, int>? Source { get; set; }

        // Written by the UI thread while the pump thread is reading them, so each
        // is its own volatile int. The same arrangement Equaliser uses, for the
        // same reason: these are meant to be moved while the music is playing,
        // which is the only way any of them can be judged.
        private int _on;
        private int _thresholdDecibels = -48;
        private int _minimumMilliseconds = 400;
        private int _leaveMilliseconds = 150;
        private int _fadeOutMilliseconds = 20;
        private int _fadeInMilliseconds = 20;

        public bool Enabled
        {
            get => Volatile.Read(ref _on) != 0;
            set => Volatile.Write(ref _on, value ? 1 : 0);
        }

        public int ThresholdDecibels
        {
            get => Volatile.Read(ref _thresholdDecibels);
            set => Volatile.Write(ref _thresholdDecibels,
                Math.Clamp(value, LowestThresholdDecibels, HighestThresholdDecibels));
        }

        public int MinimumMilliseconds
        {
            get => Volatile.Read(ref _minimumMilliseconds);
            set => Volatile.Write(ref _minimumMilliseconds,
                Math.Clamp(value, ShortestMinimumMilliseconds, LongestMinimumMilliseconds));
        }

        public int LeaveMilliseconds
        {
            get => Volatile.Read(ref _leaveMilliseconds);
            set => Volatile.Write(ref _leaveMilliseconds,
                Math.Clamp(value, 0, LongestLeaveMilliseconds));
        }

        public int FadeOutMilliseconds
        {
            get => Volatile.Read(ref _fadeOutMilliseconds);
            set => Volatile.Write(ref _fadeOutMilliseconds,
                Math.Clamp(value, 0, LongestFadeMilliseconds));
        }

        public int FadeInMilliseconds
        {
            get => Volatile.Read(ref _fadeInMilliseconds);
            set => Volatile.Write(ref _fadeInMilliseconds,
                Math.Clamp(value, 0, LongestFadeMilliseconds));
        }

        private enum Phase
        {
            /// <summary>Audio, handed on as it arrives.</summary>
            Passing,

            /// <summary>A gap that may or may not turn out to be long enough.</summary>
            Holding,

            /// <summary>A gap that was, and whose remainder is being thrown away.</summary>
            Dropping,
        }

        private Phase _phase = Phase.Passing;

        /// <summary>A gap being kept until it is known whether it is long enough.</summary>
        private float[] _hold = Array.Empty<float>();
        private int _holdFrames;

        /// <summary>
        /// Frames that have been decided about but are still reachable, so a fade
        /// out can be applied to them after the fact.
        ///
        /// A ring holding the most recent <see cref="FadeOutMilliseconds"/> of
        /// output. Nothing else about the stage needs it: it exists purely so
        /// that when a gap turns out to be long enough to cut, the music *behind*
        /// that gap has not gone out of reach yet and can still be faded down
        /// into it. At a fade out of nothing it is empty and every frame goes
        /// straight through, which is the ordinary case and costs one comparison.
        ///
        /// It delays the stage's output by the length of the fade out — at most
        /// half a second, entirely hidden behind the four seconds the pump
        /// buffers before the device is released, and declared through
        /// <see cref="HeldSourceFrames"/> so the clock does not run ahead of it.
        /// </summary>
        private float[] _line = Array.Empty<float>();
        private int _lineAt, _lineFrames, _lineCapacity;

        /// <summary>Decided frames, waiting to be handed over.</summary>
        private float[] _out = Array.Empty<float>();
        private int _outFrames, _outTaken;

        /// <summary>Part of a detection block, waiting for the rest of it.</summary>
        private readonly float[] _pend;
        private int _pendFrames;

        private readonly float[] _scratch;
        private readonly float[] _bypass;

        private int _fadeInLeft, _fadeInTotal;
        private bool _sourceDone;
        private long _removed;

        public SilenceReduction(int channels, int sampleRate)
        {
            _channels = Math.Max(1, channels);
            _rate = sampleRate > 0 ? sampleRate : 48000;

            _pend = new float[Detect * _channels];
            _scratch = new float[Detect * _channels];

            // Only ever used when the switch is off *and* something decided
            // earlier is still on its way out, which is the frame or two after
            // somebody unticks the box. Small on purpose.
            _bypass = new float[2048 * _channels];
        }

        /// <summary>
        /// Source frames read but not yet heard — the lookahead, plus anything
        /// decided and still queued. The position display subtracts these.
        ///
        /// Frames that were *dropped* are deliberately not in here. They have
        /// been read and they will never be heard, which is exactly what makes
        /// the clock jump forward across a gap that was removed.
        /// </summary>
        public int HeldSourceFrames =>
            _holdFrames + _pendFrames + _lineFrames + (_outFrames - _outTaken);

        /// <summary>How much has been taken out, in frames, since this was built.</summary>
        public long RemovedFrames => Interlocked.Read(ref _removed);

        public void Reset()
        {
            _holdFrames = 0;
            _outFrames = _outTaken = 0;
            _pendFrames = 0;
            _lineAt = _lineFrames = 0;
            _phase = Phase.Passing;
            _fadeInLeft = _fadeInTotal = 0;
            _sourceDone = false;
        }

        /// <summary>
        /// The source was turned round rather than finished with — see
        /// <see cref="TimeStretch.SourceRestarted"/>, which latches for the same
        /// reason and is cleared here on the same terms.
        /// </summary>
        public void SourceRestarted() => _sourceDone = false;

        /// <summary>
        /// Fills <paramref name="destination"/> with up to <paramref name="frames"/>
        /// frames. Fewer means the source has run out — never "a gap is being
        /// removed", because an empty read is how the pump decides a track has
        /// ended. Dropping goes on inside this call until there is something to
        /// hand back or there is nothing left to read.
        /// </summary>
        public int Read(float[] destination, int frames)
        {
            var source = Source;
            if (source == null || destination == null || frames <= 0) return 0;

            int written = TakeOut(destination, 0, frames);

            if (!Enabled)
            {
                // Off is a bypass. Anything already held was read and not yet
                // heard, so it goes out rather than being lost — switching this
                // off must not put a hole in the music — and after that the
                // samples are handed straight through, untouched.
                if (_holdFrames > 0 || _lineFrames > 0 || _pendFrames > 0)
                {
                    // Hold first, then the line: what is held was emitted *after*
                    // what the line is carrying, and flushing them the other way
                    // round would hand back the music in the wrong order.
                    //
                    // And the part of a block not yet decided about, which comes
                    // after both. Left behind it was a hole of up to 128 frames
                    // at the switch, counted by the clock as still held, and
                    // played in front of new audio when the stage came back on.
                    FlushHold();
                    if (_pendFrames > 0)
                    {
                        Emit(_pend, _pendFrames);
                        _pendFrames = 0;
                    }
                    ReleaseLine(_lineFrames);
                    written += TakeOut(destination, written, frames - written);
                }

                _phase = Phase.Passing;
                _fadeInLeft = _fadeInTotal = 0;

                while (written < frames)
                {
                    int got = ReadInto(source, destination, written, frames - written);
                    if (got <= 0) break;
                    written += got;
                }

                return written;
            }

            while (written < frames)
            {
                if (_outTaken == _outFrames && !Produce(source)) break;
                written += TakeOut(destination, written, frames - written);
            }

            return written;
        }

        /// <summary>
        /// Reads and decides until there is something to hand over, or says the
        /// source is finished.
        /// </summary>
        private bool Produce(Func<float[], int, int> source)
        {
            _outFrames = _outTaken = 0;

            while (_outFrames == 0)
            {
                if (_sourceDone)
                {
                    // The last part-block, then whatever is still held. A track
                    // that ends inside a gap ends with that gap: it was read and
                    // it is the tail of the recording, and dropping it because
                    // the file happened to stop there would shorten the track by
                    // however long somebody left running at the end.
                    if (_pendFrames > 0) { Consider(_pend, _pendFrames); _pendFrames = 0; continue; }
                    if (_holdFrames > 0) { FlushHold(); continue; }
                    if (_lineFrames > 0) { ReleaseLine(_lineFrames); continue; }
                    return false;
                }

                // Read fresh every time round, because the minimum gap can be
                // moved while a track is playing and the block size follows it.
                // A part-block already gathered that is now long enough is
                // decided about as it stands rather than being topped up to a
                // size nobody is asking for any more.
                int want = DetectFrames();

                if (_pendFrames >= want)
                {
                    Consider(_pend, _pendFrames);
                    _pendFrames = 0;
                    continue;
                }

                int got = source(_scratch, want - _pendFrames);
                if (got <= 0) { _sourceDone = true; continue; }

                Array.Copy(_scratch, 0, _pend, _pendFrames * _channels, got * _channels);
                _pendFrames += got;

                if (_pendFrames < want) continue;

                Consider(_pend, _pendFrames);
                _pendFrames = 0;
            }

            return true;
        }

        /// <summary>One block, and what becomes of it.</summary>
        private void Consider(float[] block, int frames)
        {
            bool quiet = Peak(block, frames) < Amplitude(ThresholdDecibels);

            switch (_phase)
            {
                case Phase.Passing:
                    if (!quiet) { Emit(block, frames); return; }
                    _phase = Phase.Holding;
                    Hold(block, frames);
                    break;

                case Phase.Holding:
                    if (!quiet)
                    {
                        // Too short to be worth touching, so it is played exactly
                        // as it was recorded. This is the common case in music
                        // and it has to cost nothing audible: not a shorter gap,
                        // not a quieter one, the same samples in the same order.
                        FlushHold();
                        _phase = Phase.Passing;
                        Emit(block, frames);
                        return;
                    }
                    Hold(block, frames);
                    break;

                case Phase.Dropping:
                    if (quiet) { Add(ref _removed, frames); return; }
                    _phase = Phase.Passing;
                    Emit(block, frames);
                    return;
            }

            if (_phase == Phase.Holding && _holdFrames >= FramesOf(MinimumMilliseconds))
            {
                Cut();
                _phase = Phase.Dropping;
            }
        }

        /// <summary>
        /// The gap has lasted long enough: keep the front of it, throw the rest
        /// away, and arm the fade for whatever comes back.
        /// </summary>
        private void Cut()
        {
            // A leave longer than the minimum would be a gap made *longer* than
            // the one that triggered it, which is not a reduction. The two
            // controls are independent in the dialog because they are independent
            // ideas; where they contradict each other, the shorter wins.
            int keep = Math.Min(FramesOf(LeaveMilliseconds), _holdFrames);

            // The fade out ends at the cut, which is where the sound stops.
            // Whatever will not fit in the gap that is kept is applied backwards
            // into the music the line is still holding — which is the whole
            // reason the line exists.
            int fade = FramesOf(FadeOutMilliseconds);
            int inGap = Math.Min(fade, keep);
            int inLine = Math.Min(fade - inGap, _lineFrames);

            // The music behind the gap, faded first because it goes out first.
            // The ramp is read from its own start, so a fade that runs off the
            // front of what is still reachable starts part way down rather than
            // being squashed into the frames there are — a squashed fade is a
            // different length from the one that was asked for, which is the one
            // thing a control set by ear must not be.
            if (inLine > 0) FadeLine(inLine, fade, fade - inGap - inLine);
            ReleaseLine(_lineFrames);

            if (keep > 0)
            {
                EnsureOut(_outFrames + keep);
                Array.Copy(_hold, 0, _out, _outFrames * _channels, keep * _channels);

                for (int f = keep - inGap; f < keep; f++)
                {
                    double gain = Ramp(fade - (keep - f), fade);

                    int at = (_outFrames + f) * _channels;
                    for (int c = 0; c < _channels; c++) _out[at + c] *= (float)gain;
                }

                _outFrames += keep;
            }

            Add(ref _removed, _holdFrames - keep);
            _holdFrames = 0;

            // Armed now and spent on whatever arrives, which may be several
            // blocks away. The cut lands wherever the threshold said it should,
            // and at the top of the threshold range that is the middle of the
            // music rather than a quiet place — so the fade in is the only thing
            // between a setting somebody wanted to try and a click.
            _fadeInLeft = _fadeInTotal = FramesOf(FadeInMilliseconds);
        }

        /// <summary>
        /// A fade's gain at position <paramref name="at"/> of
        /// <paramref name="total"/> — one at the start, nothing at the end.
        ///
        /// Raised cosine rather than a straight line, for the reason
        /// <see cref="TimeStretch"/> uses one: linear is the right gain law and
        /// its corners are not. The slope changes instantly at both ends, and a
        /// discontinuity in the first derivative is heard, faintly, as the very
        /// edge the fade was put there to remove.
        /// </summary>
        private static double Ramp(int at, int total)
        {
            if (total <= 0) return 0.0;
            if (at <= 0) return 1.0;
            if (at >= total) return 0.0;
            return 0.5 + 0.5 * Math.Cos(Math.PI * (at + 0.5) / total);
        }

        private void Hold(float[] block, int frames)
        {
            EnsureHold(_holdFrames + frames);
            Array.Copy(block, 0, _hold, _holdFrames * _channels, frames * _channels);
            _holdFrames += frames;
        }

        private void FlushHold()
        {
            if (_holdFrames <= 0) return;
            Emit(_hold, _holdFrames);
            _holdFrames = 0;
        }

        /// <summary>
        /// Hands frames on: any armed fade in is spent on them, then they go into
        /// the line, and whatever the line no longer needs to keep within reach
        /// goes on to the queue.
        ///
        /// The block is faded in place. Both callers hand over a buffer they are
        /// about to forget — the part-block being decided about, or the gap that
        /// turned out to be too short — so there is nothing to copy first.
        /// </summary>
        private void Emit(float[] block, int frames)
        {
            if (_fadeInLeft > 0 && _fadeInTotal > 0)
            {
                int fading = Math.Min(_fadeInLeft, frames);
                for (int f = 0; f < fading; f++)
                {
                    int done = _fadeInTotal - _fadeInLeft + f;

                    // The fade out's ramp, read backwards: one at the end rather
                    // than at the start, so the two are the same curve and a cut
                    // with equal fades is symmetrical.
                    double gain = 1.0 - Ramp(done, _fadeInTotal);

                    int at = f * _channels;
                    for (int c = 0; c < _channels; c++) block[at + c] *= (float)gain;
                }
                _fadeInLeft -= fading;
            }

            // What the line has to hold, not what it is being asked to keep: the
            // fade out can be shortened while the line is fuller than the new
            // number, and sizing from the setting rather than from the contents
            // would wrap a push over the frames it had not released yet.
            EnsureLine(_lineFrames + frames);
            PushLine(block, frames);

            ReleaseLine(_lineFrames - FramesOf(FadeOutMilliseconds));
        }

        /// <summary>Appends frames to the line, wrapping once at the end of it.</summary>
        private void PushLine(float[] block, int frames)
        {
            if (frames <= 0) return;

            int at = (_lineAt + _lineFrames) % _lineCapacity;
            int first = Math.Min(frames, _lineCapacity - at);

            Array.Copy(block, 0, _line, at * _channels, first * _channels);
            if (first < frames)
                Array.Copy(block, first * _channels, _line, 0, (frames - first) * _channels);

            _lineFrames += frames;
        }

        /// <summary>Moves the oldest frames out of the line and onto the queue.</summary>
        private void ReleaseLine(int frames)
        {
            if (frames <= 0) return;
            if (frames > _lineFrames) frames = _lineFrames;

            EnsureOut(_outFrames + frames);

            int first = Math.Min(frames, _lineCapacity - _lineAt);
            Array.Copy(_line, _lineAt * _channels, _out, _outFrames * _channels, first * _channels);
            if (first < frames)
                Array.Copy(_line, 0, _out, (_outFrames + first) * _channels, (frames - first) * _channels);

            _outFrames += frames;
            _lineAt = (_lineAt + frames) % _lineCapacity;
            _lineFrames -= frames;
        }

        /// <summary>
        /// Fades the newest <paramref name="count"/> frames in the line, reading
        /// the ramp from <paramref name="from"/> of <paramref name="total"/>.
        /// </summary>
        private void FadeLine(int count, int total, int from)
        {
            int start = _lineFrames - count;

            for (int f = 0; f < count; f++)
            {
                double gain = Ramp(from + f, total);
                int at = ((_lineAt + start + f) % _lineCapacity) * _channels;
                for (int c = 0; c < _channels; c++) _line[at + c] *= (float)gain;
            }
        }

        /// <summary>
        /// Grows the line, straightening it out on the way — the contents are put
        /// back in order from the start of the new buffer, because a ring copied
        /// across verbatim is a ring whose wrap point has moved.
        /// </summary>
        private void EnsureLine(int frames)
        {
            if (_lineCapacity >= frames) return;

            int capacity = Math.Max(frames, _lineCapacity * 2);
            var bigger = new float[capacity * _channels];

            if (_lineFrames > 0)
            {
                int first = Math.Min(_lineFrames, _lineCapacity - _lineAt);
                Array.Copy(_line, _lineAt * _channels, bigger, 0, first * _channels);
                if (first < _lineFrames)
                    Array.Copy(_line, 0, bigger, first * _channels, (_lineFrames - first) * _channels);
            }

            _line = bigger;
            _lineCapacity = capacity;
            _lineAt = 0;
        }

        private int TakeOut(float[] destination, int at, int frames)
        {
            int take = Math.Min(frames, _outFrames - _outTaken);
            if (take <= 0) return 0;

            Array.Copy(_out, _outTaken * _channels, destination, at * _channels, take * _channels);
            _outTaken += take;
            return take;
        }

        /// <summary>Reads straight from the source into the middle of a buffer.</summary>
        private int ReadInto(Func<float[], int, int> source, float[] destination, int at, int frames)
        {
            if (at == 0) return source(destination, frames);

            int room = Math.Min(frames, _bypass.Length / _channels);
            int got = source(_bypass, room);
            if (got <= 0) return got;

            Array.Copy(_bypass, 0, destination, at * _channels, got * _channels);
            return got;
        }

        private float Peak(float[] block, int frames)
        {
            float peak = 0f;
            int samples = frames * _channels;
            for (int i = 0; i < samples; i++)
            {
                float v = block[i] < 0 ? -block[i] : block[i];
                if (v > peak) peak = v;
            }
            return peak;
        }

        /// <summary>
        /// Decibels below full scale as an amplitude. Cached, because it is asked
        /// once per detection block and <see cref="Math.Pow"/> is not free.
        /// </summary>
        private float Amplitude(int decibels)
        {
            if (decibels != _cachedDecibels)
            {
                _cachedAmplitude = (float)Math.Pow(10.0, decibels / 20.0);
                _cachedDecibels = decibels;
            }
            return _cachedAmplitude;
        }

        private int _cachedDecibels = int.MinValue;
        private float _cachedAmplitude;

        private int FramesOf(int milliseconds) =>
            milliseconds <= 0 ? 0 : (int)((long)milliseconds * _rate / 1000);

        /// <summary>
        /// How many frames this decision covers: a quarter of the minimum gap,
        /// between <see cref="SmallestDetect"/> and <see cref="Detect"/>.
        ///
        /// A quarter, so a gap at the minimum is resolved to within a quarter of
        /// itself. Anything from twenty milliseconds upwards is already past the
        /// cap and decides in the full block, which is every setting anybody
        /// listens at.
        /// </summary>
        private int DetectFrames() =>
            Math.Clamp(FramesOf(MinimumMilliseconds) / 4, SmallestDetect, Detect);

        /// <summary>
        /// Grows the gap buffer, and is never asked for more than
        /// <see cref="LongestMinimumMilliseconds"/> plus a block: the gap is cut
        /// the moment it reaches the minimum, so what is held can only ever be
        /// one detection block past the longest minimum there is. No clamp here,
        /// deliberately — a cap that disagreed with that bound would under-
        /// allocate rather than protect anything.
        /// </summary>
        private void EnsureHold(int frames)
        {
            if (_hold.Length >= frames * _channels) return;
            Array.Resize(ref _hold, Math.Max(frames, _hold.Length / _channels * 2) * _channels);
        }

        private void EnsureOut(int frames)
        {
            if (_out.Length >= frames * _channels) return;
            Array.Resize(ref _out, Math.Max(frames, _out.Length / _channels * 2) * _channels);
        }

        private static void Add(ref long total, int frames)
        {
            if (frames > 0) Interlocked.Add(ref total, frames);
        }
    }
}
