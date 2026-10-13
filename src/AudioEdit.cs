using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ExplorerNative
{
    /// <summary>
    /// A run of the source file, in its own frames, played at a gain. The edited
    /// recording is these one after another.
    /// </summary>
    internal readonly record struct EditSegment(long Start, long End, double GainDb)
    {
        public long Length => End - Start;
    }

    /// <summary>
    /// The editor's whole state: which parts of the source play, in order, and
    /// how loud. Immutable, so undo is keeping the old one. Positions taken and
    /// given are on the edited timeline, in frames at the source's own rate.
    ///
    /// Nothing here touches audio. Playback reads through it
    /// (<see cref="EditedDecoder"/>), and so does saving, so what is heard is
    /// what is written.
    /// </summary>
    internal sealed class EditList
    {
        public int SampleRate { get; }
        public long SourceFrames { get; }
        public IReadOnlyList<EditSegment> Segments { get; }

        /// <summary>The edited recording's length, in frames.</summary>
        public long Length { get; }

        public double Seconds => SampleRate > 0 ? Length / (double)SampleRate : 0;

        public EditList(long sourceFrames, int sampleRate)
            : this(sampleRate, sourceFrames,
                sourceFrames > 0 ? new List<EditSegment> { new(0, sourceFrames, 0) } : new List<EditSegment>())
        {
        }

        private EditList(int sampleRate, long sourceFrames, List<EditSegment> segments)
        {
            SampleRate = sampleRate;
            SourceFrames = sourceFrames;
            Segments = Merge(segments);
            Length = Segments.Sum(s => s.Length);
        }

        /// <summary>True when this is the source as it was: all of it, at its own level.</summary>
        public bool IsUnchanged =>
            Segments.Count == 1 && Segments[0].Start == 0 && Segments[0].End == SourceFrames &&
            Math.Abs(Segments[0].GainDb) < 1e-9;

        public long FrameAt(double seconds) => Math.Clamp((long)Math.Round(seconds * SampleRate), 0, Length);

        /// <summary>Everything but [from, to).</summary>
        public EditList Cut(long from, long to)
        {
            (from, to) = Order(from, to);
            if (from >= to) return this;
            return new EditList(SampleRate, SourceFrames, Pieces(from, to).Where(p => !p.Inside).Select(p => p.Segment).ToList());
        }

        /// <summary>Only [from, to).</summary>
        public EditList KeepOnly(long from, long to)
        {
            (from, to) = Order(from, to);
            if (from >= to) return this;
            return new EditList(SampleRate, SourceFrames, Pieces(from, to).Where(p => p.Inside).Select(p => p.Segment).ToList());
        }

        /// <summary>[from, to) made louder or quieter by <paramref name="decibels"/>, on top of what it already had.</summary>
        public EditList ChangeGain(long from, long to, double decibels)
        {
            (from, to) = Order(from, to);
            if (from >= to || Math.Abs(decibels) < 1e-9) return this;
            return new EditList(SampleRate, SourceFrames, Pieces(from, to)
                .Select(p => p.Inside ? p.Segment with { GainDb = p.Segment.GainDb + decibels } : p.Segment)
                .ToList());
        }

        /// <summary>
        /// Which segment the edited frame falls in, and how far into it. At the
        /// very end the index is <see cref="Segments"/>.Count.
        /// </summary>
        public (int Index, long Offset) Locate(long edited)
        {
            edited = Math.Clamp(edited, 0, Length);
            long at = 0;
            for (int i = 0; i < Segments.Count; i++)
            {
                long length = Segments[i].Length;
                if (edited < at + length) return (i, edited - at);
                at += length;
            }
            return (Segments.Count, 0);
        }

        private (long, long) Order(long from, long to)
        {
            if (from > to) (from, to) = (to, from);
            return (Math.Clamp(from, 0, Length), Math.Clamp(to, 0, Length));
        }

        /// <summary>The segments split at the two edited positions, each marked inside [from, to) or not.</summary>
        private IEnumerable<(EditSegment Segment, bool Inside)> Pieces(long from, long to)
        {
            long at = 0;
            foreach (var s in Segments)
            {
                long end = at + s.Length;
                long pieceStart = at;
                long sourceStart = s.Start;

                foreach (var cut in new[] { from, to })
                {
                    if (cut <= pieceStart || cut >= end) continue;
                    long length = cut - pieceStart;
                    yield return (new EditSegment(sourceStart, sourceStart + length, s.GainDb), pieceStart >= from && pieceStart < to);
                    sourceStart += length;
                    pieceStart = cut;
                }

                yield return (new EditSegment(sourceStart, s.End, s.GainDb), pieceStart >= from && pieceStart < to);
                at = end;
            }
        }

        /// <summary>Joins neighbours that are one run of the source at one level, and drops empty ones.</summary>
        private static List<EditSegment> Merge(List<EditSegment> segments)
        {
            var merged = new List<EditSegment>(segments.Count);
            foreach (var s in segments)
            {
                if (s.Length <= 0) continue;
                if (merged.Count > 0)
                {
                    var last = merged[^1];
                    if (last.End == s.Start && Math.Abs(last.GainDb - s.GainDb) < 1e-9)
                    {
                        merged[^1] = last with { End = s.End };
                        continue;
                    }
                }
                merged.Add(s);
            }
            return merged;
        }

        /// <summary>
        /// A position with tenths of a second, the way marks are spoken: "4.2
        /// seconds", "1 minute 4.2 seconds", "1 hour 2 minutes 4.2 seconds".
        /// </summary>
        public static string SpokenPrecise(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            long tenths = (long)Math.Round(seconds * 10);
            long hours = tenths / 36000;
            long minutes = tenths / 600 % 60;
            double rest = tenths % 600 / 10.0;

            string Plural(long n, string word) => n == 1 ? $"{n} {word}" : $"{n} {word}s";
            var secondsText = rest.ToString("0.#", CultureInfo.InvariantCulture) +
                              (Math.Abs(rest - 1) < 1e-9 ? " second" : " seconds");

            if (hours > 0) return $"{Plural(hours, "hour")} {Plural(minutes, "minute")} {secondsText}";
            if (minutes > 0) return $"{Plural(minutes, "minute")} {secondsText}";
            return secondsText;
        }

        /// <summary>
        /// A volume change typed into the prompt: "3", "+3", "-6", "-6 dB",
        /// "2.5db". Null for anything else or anything past the sensible range.
        /// </summary>
        public static double? ParseDecibels(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim().Replace(',', '.');
            if (t.EndsWith("db", StringComparison.OrdinalIgnoreCase)) t = t[..^2].Trim();
            if (t.StartsWith('+')) t = t[1..].Trim();
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return null;
            if (double.IsNaN(value) || value < -60 || value > 30) return null;
            return value;
        }
    }

    /// <summary>Undo and redo over <see cref="EditList"/>s, each step named for what it did.</summary>
    internal sealed class EditHistory
    {
        private readonly List<(EditList List, string What)> _undo = new();
        private readonly List<(EditList List, string What)> _redo = new();
        private EditList _saved;

        public EditHistory(EditList start)
        {
            Current = start;
            _saved = start;
        }

        public EditList Current { get; private set; }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        /// <summary>Whether there is anything not yet saved.</summary>
        public bool Unsaved => !ReferenceEquals(Current, _saved);

        public void MarkSaved() => _saved = Current;

        /// <summary>Makes <paramref name="next"/> current. A step that changes nothing is not a step.</summary>
        public bool Apply(EditList next, string what)
        {
            if (ReferenceEquals(next, Current)) return false;
            _undo.Add((Current, what));
            _redo.Clear();
            Current = next;
            return true;
        }

        /// <summary>Goes back one step and says which, or null when there is nothing to undo.</summary>
        public string? Undo()
        {
            if (_undo.Count == 0) return null;
            var (previous, what) = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add((Current, what));
            Current = previous;
            return what;
        }

        public string? Redo()
        {
            if (_redo.Count == 0) return null;
            var (next, what) = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add((Current, what));
            Current = next;
            return what;
        }
    }

    /// <summary>
    /// A decoder of the source read through an edit list: cuts skipped, gains
    /// applied, the clock on the edited timeline. The player's pipeline plays
    /// it like any other track, and the converter encodes it like any other
    /// file, so playback and save cannot disagree.
    ///
    /// Each segment start is an exact seek of the source decoder, so a cut
    /// lands on the sample (WMA within about a millisecond, as everywhere).
    /// </summary>
    internal sealed class EditedDecoder : ITrackDecoder
    {
        private readonly ITrackDecoder _inner;
        private readonly EditList _edits;

        private int _segment;
        private long _segmentLeft;          // decoder frames still to read in this segment
        private float _gain = 1f;
        private long _innerAt;              // where the source decoder is, in decoder frames; -1 unknown
        private double _base;               // edited seconds at the last seek
        private long _emitted;              // decoder frames handed out since it
        private float[] _scratch = Array.Empty<float>();

        public EditedDecoder(ITrackDecoder inner, EditList edits)
        {
            _inner = inner;
            _edits = edits;
            _innerAt = 0;                   // freshly opened: at the start
            SeekTo(0);
        }

        public int SampleRate => _inner.SampleRate;
        public int Channels => _inner.Channels;
        public double Duration => _edits.Seconds;
        public double Position => Math.Min(Duration, _base + (SampleRate > 0 ? _emitted / (double)SampleRate : 0));

        private long ToDecoder(long sourceFrame) =>
            _edits.SampleRate == SampleRate
                ? sourceFrame
                : (long)Math.Round(sourceFrame * (double)SampleRate / _edits.SampleRate);

        public bool SeekTo(double seconds)
        {
            long edited = _edits.FrameAt(seconds);
            var (index, offset) = _edits.Locate(edited);
            _base = edited / (double)Math.Max(1, _edits.SampleRate);
            _emitted = 0;
            _segment = index;
            if (index >= _edits.Segments.Count) { _segmentLeft = 0; return true; }
            return Enter(index, _edits.Segments[index].Start + offset);
        }

        /// <summary>Points the source decoder at <paramref name="sourceFrame"/> inside segment <paramref name="index"/>.</summary>
        private bool Enter(int index, long sourceFrame)
        {
            var segment = _edits.Segments[index];
            long start = ToDecoder(sourceFrame);
            _segmentLeft = ToDecoder(segment.End) - start;
            _gain = (float)Math.Pow(10, segment.GainDb / 20);

            if (_innerAt == start) return true;
            bool ok = _inner.SeekTo(sourceFrame / (double)_edits.SampleRate);
            _innerAt = ok ? start : -1;
            return ok;
        }

        public int Read(float[] destination, int frames)
        {
            int channels = Math.Max(1, Channels);
            int written = 0;
            int empties = 0;

            while (written < frames && _segment < _edits.Segments.Count)
            {
                if (_segmentLeft <= 0)
                {
                    _segment++;
                    if (_segment >= _edits.Segments.Count) break;
                    Enter(_segment, _edits.Segments[_segment].Start);
                    continue;
                }

                int want = (int)Math.Min(frames - written, _segmentLeft);
                if (_scratch.Length < want * channels) _scratch = new float[want * channels];

                int got = _inner.Read(_scratch, want);
                if (got <= 0)
                {
                    // The source ran out before the segment did (its length was
                    // an estimate). Bounded: the segment is over.
                    if (++empties >= 3) { _segmentLeft = 0; _innerAt = -1; empties = 0; }
                    continue;
                }
                empties = 0;

                int count = got * channels;
                if (_gain == 1f) Array.Copy(_scratch, 0, destination, written * channels, count);
                else
                    for (int i = 0; i < count; i++) destination[written * channels + i] = _scratch[i] * _gain;

                written += got;
                _segmentLeft -= got;
                if (_innerAt >= 0) _innerAt += got;
            }

            _emitted += written;
            return written;
        }

        public bool Reopen(int sampleRate, int channels)
        {
            double at = Position;
            bool ok = _inner.Reopen(sampleRate, channels);
            _innerAt = -1;
            SeekTo(at);
            return ok;
        }

        public void Dispose() => _inner.Dispose();
    }
}
