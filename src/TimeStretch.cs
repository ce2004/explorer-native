using System;

namespace ExplorerNative
{
    /// <summary>
    /// Changes how fast a track plays without changing its pitch.
    ///
    /// This is the one thing the media engine did that the direct path could
    /// not, and it is why the engine survived so long after it stopped being
    /// the player: resampling alone gives you a chipmunk or a drunk, and the
    /// speed keys are useless if the music changes key when you use them.
    ///
    /// The method is WSOLA — overlap-add, with the overlap position chosen by
    /// similarity rather than fixed. Output is produced in hops of
    /// <see cref="Hop"/> frames; each hop consumes <c>Hop * Rate</c> frames of
    /// input, which is the whole of how the speed changes. The join between one
    /// hop and the next is the part that decides whether it sounds like music:
    /// a fixed join puts two uncorrelated waveforms against each other and
    /// produces a periodic click at the hop rate, so each new segment is
    /// searched for within a window and the position with the best correlation
    /// against what is already playing is the one used. That search is the
    /// difference between "speeded up" and "broken".
    ///
    /// <para>
    /// At <see cref="Rate"/> exactly 1 it is bypassed and the samples pass
    /// through untouched, bit for bit. Normal speed is overwhelmingly the common
    /// case and it must not pay for this, nor be coloured by it.
    /// </para>
    ///
    /// <para>
    /// The first working version of this sounded speeded-up rather than good,
    /// and four things were wrong with it. All four are fixed here and all four
    /// are worth knowing about, because each of them is the kind of thing that
    /// measures fine and sounds bad.
    /// </para>
    /// </summary>
    internal sealed class TimeStretch
    {
        /// <summary>
        /// Output frames produced per pass — about 21ms at 48kHz.
        ///
        /// It was 2048, which is 43ms, and that is long enough to hear. A hop is
        /// a piece of the recording repeated or skipped, and at 43ms the ear
        /// stops taking it as a change of tempo and starts taking it as a short
        /// echo — the "flanged" or "underwater" quality that speeded-up speech
        /// gets. Half of that is under the ear's threshold for the same effect
        /// while still being long enough to correlate a low note against, which
        /// is the other constraint: a hop shorter than a bass period cannot find
        /// a join and warbles instead.
        /// </summary>
        private const int Hop = 1024;

        /// <summary>
        /// How much of each hop is crossfaded into the one before.
        ///
        /// All of it. This was a quarter of the hop, which means three quarters
        /// of every segment was butted straight on to the crossfade with no
        /// smoothing at all, and the discontinuity at that boundary is a click
        /// at the hop rate — the exact artefact the search exists to avoid,
        /// reintroduced a few hundred samples later. A 50 percent overlap is
        /// what WSOLA is normally written with, and it means every output sample
        /// is the sum of two windowed inputs rather than most of them being one
        /// raw input with a seam on either side.
        /// </summary>
        private const int Overlap = Hop;

        /// <summary>
        /// How far either side of the nominal position the join is searched.
        ///
        /// About 13ms at 48kHz, which covers a full period of anything down to
        /// 75Hz. Below that there is nothing to align to but the fundamental,
        /// and the bass is where a bad join is heard as warble rather than as
        /// grit.
        /// </summary>
        private const int Search = 640;

        /// <summary>Input frames a single pass can consume, at the fastest speed.</summary>
        // Sixteen, because the stretch is speed divided by pitch: four times the
        // speed two octaves down is sixteen, and a cap of four played it at
        // normal speed with the clock saying otherwise.
        private const int MaxHopIn = Hop * 16;

        /// <summary>
        /// The crossfade shape, and the reason it is a table.
        ///
        /// A straight line was what this used before. Linear is the right
        /// *gain* law for two correlated signals — which is what the search has
        /// just gone to some trouble to produce — but its corners are not: the
        /// slope changes instantly at both ends of the fade, and a discontinuity
        /// in the first derivative is heard, faintly, as the same periodic edge
        /// the fade was there to remove. A raised cosine has the same
        /// complementary property (w and 1-w still sum to exactly one, so
        /// correlated material keeps its level) with no corners at all.
        ///
        /// Computed once for the type rather than per instance, because it
        /// depends on nothing but <see cref="Overlap"/>.
        /// </summary>
        private static readonly float[] Window = BuildWindow();

        private static float[] BuildWindow()
        {
            var window = new float[Overlap];
            for (int i = 0; i < Overlap; i++)
                window[i] = (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / (Overlap - 1)));
            return window;
        }

        /// <summary>
        /// How much of the overlap the similarity is measured over.
        ///
        /// The whole of it. It was a quarter, on the grounds that a join which
        /// starts badly stays bad — which is true, and is also how a join gets
        /// chosen that is perfect for four milliseconds and wrong for the
        /// twenty after it. With the coarse pass below, correlating the whole
        /// overlap costs less than correlating a quarter of it did.
        /// </summary>
        private const int CorrelateOver = Overlap;

        /// <summary>
        /// The stride of the first, coarse pass over the search window.
        ///
        /// A normalised correlation over 1281 offsets and 1024 samples each is
        /// 1.3 million multiply-adds per 21ms of output, which is real work on a
        /// laptop processor. The correlation surface is smooth at this scale —
        /// it is a waveform against a copy of itself — so a pass every eighth
        /// offset finds the right lobe, and a second pass either side of the
        /// winner finds the peak within it. Sixteen times less arithmetic for
        /// the same answer.
        /// </summary>
        private const int Coarse = 8;

        /// <summary>Samples skipped inside the coarse pass, for the same reason.</summary>
        private const int CoarseStride = 4;

        private readonly int _channels;

        /// <summary>Source frames read but not yet turned into output.</summary>
        private float[] _in = Array.Empty<float>();
        private int _inFrames;

        /// <summary>Output frames made but not yet handed over.</summary>
        private float[] _out = Array.Empty<float>();
        private int _outFrames, _outTaken;

        /// <summary>The tail of the last hop, waiting to be crossfaded out.</summary>
        private readonly float[] _tail;
        private bool _haveTail;

        /// <summary>
        /// The tail again, mixed down to one channel, for the correlation.
        ///
        /// Mono rather than the first channel. Correlating channel zero alone is
        /// what the first version did, and on anything with a wide stereo image
        /// — a guitar hard left, say — the search is aligning to whatever
        /// happens to be on the left while the join is applied to both sides. A
        /// sum is what is actually being joined.
        /// </summary>
        private readonly float[] _tailMono;

        /// <summary>The search window, mixed down the same way, rebuilt per pass.</summary>
        private readonly float[] _inMono;

        /// <summary>Left over when a hop consumed a fractional number of frames.</summary>
        private double _debt;

        private bool _sourceDone;

        /// <summary>Somewhere to read into that is not the output buffer.</summary>
        private readonly float[] _scratch;

        public TimeStretch(int channels)
        {
            _channels = Math.Max(1, channels);
            _tail = new float[Overlap * _channels];
            _tailMono = new float[Overlap];

            // The largest read any pass makes, plus the largest it can consume.
            _in = new float[(2 * Search + Hop + Overlap + MaxHopIn) * _channels];
            _out = new float[Hop * _channels];
            _scratch = new float[Hop * _channels];

            // Everything the correlation can look at: the whole search window
            // plus one correlation length past the far end of it.
            _inMono = new float[2 * Search + CorrelateOver + 1];
        }

        /// <summary>1 is untouched; 2 is twice as fast; 0.5 is half.</summary>
        public double Rate { get; set; } = 1.0;

        /// <summary>Where the frames come from: fills a buffer, returns frames.</summary>
        public Func<float[], int, int>? Source { get; set; }

        /// <summary>
        /// Source frames held here — read from the file but not yet heard.
        /// The position display has to subtract these or it runs ahead.
        /// </summary>
        public int HeldSourceFrames => _inFrames;

        public void Reset()
        {
            _inFrames = 0;
            _outFrames = _outTaken = 0;
            _haveTail = false;
            _tailTaken = 0;
            _resume = 0;
            _dropFromSource = 0;
            _plain = true;
            _debt = 0;
            _sourceDone = false;
        }

        // ---- the joins between plain and stretched ----
        //
        // Three places the stretcher used to butt audio together without a
        // crossfade, each a click and a skip or repeat of up to a few dozen
        // milliseconds:
        //
        //   * going from normal speed to anything else, where the first hop
        //     started Search frames into the input — also the first 13ms after
        //     every seek at a speed other than normal;
        //   * coming back to normal, where playback resumed from the front of
        //     the input rather than from where the last hop's tail leads;
        //   * the end of a stretched track, which played what was left from the
        //     front of the input and dropped the tail altogether.
        //
        // The tail is the continuation of the last hop: its first frame follows
        // the hop's last. So leaving stretched playback plays the tail and then
        // carries on from the input just past it (_resume), and entering it
        // starts the first hop exactly where plain playback stopped (_plain).

        /// <summary>How much of the tail has been played on the way out of stretched playback.</summary>
        private int _tailTaken;

        /// <summary>
        /// Where, in the input as it stands, plain playback carries on once the
        /// tail has been played. Can be past what is held, in which case the rest
        /// is read from the source and dropped (<see cref="_dropFromSource"/>).
        /// </summary>
        private int _resume;
        private int _dropFromSource;

        /// <summary>True while what was last heard came straight through rather than from a hop.</summary>
        private bool _plain = true;

        /// <summary>
        /// The source has more to give after all — it was seeked back rather
        /// than being finished with.
        ///
        /// <see cref="_sourceDone"/> is a latch: once the source returns nothing
        /// this stops asking, which is right at the end of a track and wrong at
        /// the *loop* point of one. A gapless repeat turns the decoder round
        /// without stopping anything, so the stage above has to be told the well
        /// is full again or it will never draw from it — the track goes silent
        /// for ever, at the first repeat, and only when the stretcher is doing
        /// any work at all. At rate 1 it is a pass-through and never latches,
        /// which is why this only ever showed up after somebody moved the speed
        /// or the pitch.
        ///
        /// Deliberately not <see cref="Reset"/>: what is held is the tail of the
        /// last hop, and letting it cross into the first hop of the new pass is
        /// the join being seamless. Only the latch is cleared.
        /// </summary>
        /// <summary>
        /// The source may have more after all — an empty read retried — without
        /// having gone back anywhere: a skip still owed is still owed.
        /// </summary>
        public void SourceResumed() => _sourceDone = false;

        public void SourceRestarted()
        {
            _sourceDone = false;

            // A skip still owed belonged to the pass that ended; against the new
            // one it would cut the start off.
            _dropFromSource = 0;
        }

        /// <summary>
        /// Fills <paramref name="destination"/> with up to <paramref name="frames"/>
        /// frames at the current rate. Fewer means the source has run out.
        /// </summary>
        public int Read(float[] destination, int frames)
        {
            var source = Source;
            if (source == null) return 0;

            int written = 0;

            // Anything already made, and at normal speed anything already read,
            // comes out first — whatever the rate is now. Coming back to normal
            // speed must not throw away a fifth of a second that has been read
            // and not yet heard: that is a gap in the music at the exact moment
            // a key was pressed.
            written += TakeMade(destination, written, frames - written);

            if (Rate == 1.0)
            {
                // The way out of stretched playback: the tail, then the input
                // from just past it.
                if (_haveTail)
                {
                    written += TakeTail(destination, written, frames - written);
                    if (_tailTaken < Overlap) return written;

                    _haveTail = false;
                    _tailTaken = 0;
                    int skip = Math.Max(0, _resume);
                    _dropFromSource = Math.Max(0, skip - _inFrames);
                    Consume(Math.Min(skip, _inFrames));
                }

                _plain = true;
                written += TakeHeld(destination, written, frames - written);

                while (_dropFromSource > 0)
                {
                    int dropped = source(_scratch, Math.Min(_dropFromSource, _scratch.Length / _channels));
                    if (dropped <= 0) break;
                    _dropFromSource -= dropped;
                }

                // Still owed: the source came up short. Reading on would play
                // what is to be skipped, and skip later audio instead.
                if (_dropFromSource > 0) return written;

                // And from here it is a plain pass-through, bit for bit.
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
                written += TakeMade(destination, written, frames - written);
            }

            return written;
        }

        /// <summary>Hands over frames already stretched and waiting.</summary>
        private int TakeMade(float[] destination, int at, int frames)
        {
            int take = Math.Min(frames, _outFrames - _outTaken);
            if (take <= 0) return 0;

            Array.Copy(_out, _outTaken * _channels, destination, at * _channels, take * _channels);
            _outTaken += take;
            return take;
        }

        /// <summary>The tail of the last hop, played plainly on the way back to normal speed.</summary>
        private int TakeTail(float[] destination, int at, int frames)
        {
            int take = Math.Min(frames, Overlap - _tailTaken);
            if (take <= 0) return 0;

            Array.Copy(_tail, _tailTaken * _channels, destination, at * _channels, take * _channels);
            _tailTaken += take;
            return take;
        }

        /// <summary>
        /// Hands over source frames read ahead but never stretched. Only correct
        /// at rate 1 — anywhere else these are the stretcher's working input.
        /// </summary>
        private int TakeHeld(float[] destination, int at, int frames)
        {
            int take = Math.Min(frames, _inFrames);
            if (take <= 0) return 0;

            Array.Copy(_in, 0, destination, at * _channels, take * _channels);
            Consume(take);
            return take;
        }

        /// <summary>Reads straight from the source into the middle of a buffer.</summary>
        private int ReadInto(Func<float[], int, int> source, float[] destination, int at, int frames)
        {
            if (at == 0) return source(destination, frames);

            int room = Math.Min(frames, _scratch.Length / _channels);
            int got = source(_scratch, room);
            if (got <= 0) return got;

            Array.Copy(_scratch, 0, destination, at * _channels, got * _channels);
            return got;
        }

        /// <summary>Makes one hop of output, or says the source is finished.</summary>
        private bool Produce(Func<float[], int, int> source)
        {
            _outFrames = _outTaken = 0;

            // Input already heard, still owed to be skipped when the source came
            // up short at the moment it was to be passed over.
            while (_dropFromSource > 0)
            {
                int dropped = source(_scratch, Math.Min(_dropFromSource, _scratch.Length / _channels));
                if (dropped <= 0) break;
                _dropFromSource -= dropped;
            }
            if (_dropFromSource > 0) return false;

            // A tail partly handed out at normal speed, and the speed changed
            // before the rest went: crossfading against the whole of it played
            // again what had just been heard. The rest goes out plainly, the
            // input is brought to just past it, and stretching starts afresh.
            if (_haveTail && _tailTaken > 0)
            {
                int rest = Overlap - _tailTaken;
                Array.Copy(_tail, _tailTaken * _channels, _out, 0, rest * _channels);
                _haveTail = false;
                _tailTaken = 0;

                int skip = Math.Max(0, _resume);
                Fill(source, skip);
                _dropFromSource = Math.Max(0, skip - _inFrames);
                Consume(Math.Min(skip, _inFrames));

                _plain = true;
                _outFrames = rest;
                return true;
            }

            // How much input this pass will step over. Kept to a whole number of
            // frames with the remainder carried, because dropping it would make
            // the speed wrong by a little on every hop, which over a track is
            // wrong by a lot.
            double want = Hop * Rate + _debt;
            int step = (int)want;
            _debt = want - step;

            // Never step further than the buffer can hold, whatever rate somebody
            // sets. Past the clamp the speed simply stops increasing, which is a
            // dull failure rather than a read off the end of the array.
            step = Math.Clamp(step, 1, MaxHopIn);

            int needed = Math.Max(2 * Search + Hop + Overlap, step);
            Fill(source, needed);

            if (_inFrames < needed)
            {
                // The end of the track. What is left is too short to search
                // within, so it is emitted plainly — but only as much of it as
                // the rate calls for. Emitting all of it would play the last
                // fragment at normal speed however fast the track was set to,
                // and at four times that is a sixth of a second of the ending
                // arriving late and in the wrong tempo.
                //
                // The tail first: it is what follows the last hop, and the
                // leftover input is only right from the point just past it.
                // Every way out of here leaves nothing to join to, so what
                // comes next — a repeat's first hop, or the track after a
                // momentary empty read — starts plainly. Left as it was, that
                // first hop took the no-tail branch below, skipped its first
                // stretch of input and butted the rest on with no fade.
                if (_sourceDone && _haveTail)
                {
                    Array.Copy(_tail, 0, _out, 0, Overlap * _channels);
                    _haveTail = false;
                    _plain = true;
                    Consume(Math.Clamp(_resume, 0, _inFrames));
                    _outFrames = Overlap;
                    return true;
                }

                if (!_sourceDone) return false;
                _plain = true;
                if (_inFrames == 0) return false;

                int emit = Math.Min(Math.Min((int)(_inFrames / Rate), _inFrames), Hop);
                if (emit <= 0) { _inFrames = 0; return false; }

                Array.Copy(_in, 0, _out, 0, emit * _channels);

                // At least one frame, always: a slow rate consumes a fraction of
                // what it emits, and rounding that to nothing never terminates.
                Consume(Math.Max(1, (int)(emit * Rate)));
                _outFrames = emit;
                return true;
            }

            // Straight after plain playback, the first hop starts exactly where
            // that left off — the front of the input — and nothing is faded.
            if (_plain)
            {
                _plain = false;
                _haveTail = false;
                Array.Copy(_in, 0, _out, 0, Overlap * _channels);
                Array.Copy(_in, Hop * _channels, _tail, 0, Overlap * _channels);
                _haveTail = true;
                _tailTaken = 0;
                _resume = Hop + Overlap - step;
                MixDownTail();
                Consume(step);
                _outFrames = Hop;
                return true;
            }

            // The join. Search is centred at Search frames in, so an offset may
            // be negative without reading before the start of the buffer.
            int at = _haveTail ? Search + BestOffset() : Search;

            if (_haveTail)
            {
                for (int i = 0; i < Overlap; i++)
                {
                    float w = Window[i];
                    float back = 1f - w;
                    int from = (at + i) * _channels;
                    int to = i * _channels;

                    for (int c = 0; c < _channels; c++)
                        _out[to + c] = _tail[to + c] * back + _in[from + c] * w;
                }
            }
            else
            {
                Array.Copy(_in, at * _channels, _out, 0, Overlap * _channels);
            }

            // There is no "and the rest of the hop, straight through" any more,
            // and its absence is the point. Overlap is the whole hop, so every
            // output sample is a crossfade of two windowed inputs — there is no
            // raw stretch left anywhere with a seam at each end of it for a
            // click to come out of.

            // The frames that will be crossfaded into the next hop.
            Array.Copy(_in, (at + Hop) * _channels, _tail, 0, Overlap * _channels);
            _haveTail = true;
            _tailTaken = 0;
            _resume = at + Hop + Overlap - step;
            MixDownTail();

            Consume(step);
            _outFrames = Hop;
            return true;
        }

        /// <summary>The tail as one channel, ready to be correlated against.</summary>
        private void MixDownTail()
        {
            if (_channels == 1)
            {
                Array.Copy(_tail, _tailMono, Overlap);
                return;
            }

            for (int i = 0; i < Overlap; i++)
            {
                int at = i * _channels;
                float sum = 0f;
                for (int c = 0; c < _channels; c++) sum += _tail[at + c];
                _tailMono[i] = sum;
            }
        }

        /// <summary>The search window as one channel, for this pass.</summary>
        private void MixDownWindow()
        {
            int span = Math.Min(_inMono.Length, _inFrames);

            if (_channels == 1)
            {
                Array.Copy(_in, _inMono, span);
                for (int i = span; i < _inMono.Length; i++) _inMono[i] = 0f;
                return;
            }

            for (int i = 0; i < span; i++)
            {
                int at = i * _channels;
                float sum = 0f;
                for (int c = 0; c < _channels; c++) sum += _in[at + c];
                _inMono[i] = sum;
            }
            for (int i = span; i < _inMono.Length; i++) _inMono[i] = 0f;
        }

        /// <summary>
        /// Where, within the search window, the input best continues what is
        /// already playing — normalised, so a loud passage is not preferred to a
        /// well-matched quiet one.
        ///
        /// Two passes. The first walks the whole window every
        /// <see cref="Coarse"/> offsets, skipping samples as it goes; the second
        /// walks every offset within one coarse step of the winner, at full
        /// resolution. The surface being searched is a waveform against a copy
        /// of itself, so it has no structure finer than the coarse step to be
        /// missed by it — and the arithmetic is a sixteenth of what a single
        /// full-resolution pass would cost, which is what makes correlating the
        /// whole overlap affordable at all.
        /// </summary>
        private int BestOffset()
        {
            MixDownWindow();

            int best = 0;
            double bestScore = double.NegativeInfinity;

            for (int offset = -Search; offset <= Search; offset += Coarse)
            {
                double score = Score(Search + offset, CoarseStride);
                if (score > bestScore) { bestScore = score; best = offset; }
            }

            int from = Math.Max(-Search, best - Coarse);
            int to = Math.Min(Search, best + Coarse);

            int refined = best;
            bestScore = double.NegativeInfinity;

            for (int offset = from; offset <= to; offset++)
            {
                double score = Score(Search + offset, 1);
                if (score > bestScore) { bestScore = score; refined = offset; }
            }

            return refined;
        }

        /// <summary>
        /// Normalised cross-correlation of the tail against the input at
        /// <paramref name="at"/>, sampled every <paramref name="stride"/>.
        /// </summary>
        private double Score(int at, int stride)
        {
            double dot = 0, energy = 0;

            for (int i = 0; i < CorrelateOver; i += stride)
            {
                float a = _tailMono[i];
                float b = _inMono[at + i];
                dot += a * b;
                energy += b * b;
            }

            return energy > 1e-12 ? dot / Math.Sqrt(energy) : 0;
        }

        /// <summary>Reads until there are <paramref name="frames"/> or the source stops.</summary>
        private void Fill(Func<float[], int, int> source, int frames)
        {
            int capacity = _in.Length / _channels;

            while (_inFrames < frames && !_sourceDone)
            {
                int room = Math.Min(capacity - _inFrames, _scratch.Length / _channels);
                if (room <= 0) break;

                int got = source(_scratch, room);
                if (got <= 0) { _sourceDone = true; break; }

                Array.Copy(_scratch, 0, _in, _inFrames * _channels, got * _channels);
                _inFrames += got;
            }
        }

        /// <summary>Drops frames from the front of the input.</summary>
        private void Consume(int frames)
        {
            if (frames >= _inFrames) { _inFrames = 0; return; }

            Array.Copy(_in, frames * _channels, _in, 0, (_inFrames - frames) * _channels);
            _inFrames -= frames;
        }
    }
}
