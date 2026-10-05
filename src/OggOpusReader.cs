using System;
using System.Collections.Generic;
using System.IO;
using Concentus;
using Concentus.Structs;

namespace ExplorerNative
{
    /// <summary>
    /// Ogg Opus, read a page at a time, with nothing held but the page being
    /// played.
    ///
    /// **This replaced Concentus.OggFile's `OpusOggReadStream`, and the reason
    /// is what that class does before it returns.** Given a seekable stream —
    /// which every stream here is — its constructor asks for the total length,
    /// and it can only answer by reading every page of the file, checksumming
    /// each one a byte at a time, and building a linked list with an object for
    /// every packet in it. Then it walks that list again to count the pages.
    /// Measured on a real 1.18GB, twenty-hour recording: **five seconds** before
    /// the first sample, 465MB allocated and 411MB still held while it played —
    /// and "back to the start" built the whole thing again. The fifteen-second
    /// open budget is all that stood between that and a file that would not
    /// play at all on a cold read.
    ///
    /// None of that was needed. The format says where everything is, and
    /// <see cref="OggPages"/> reads it that way: the length off the last page,
    /// a seek by bisection. What is Opus-specific is here — **every page says
    /// where its own packets end**, so a packet's place on the timeline is
    /// worked backwards from the granule of the page it finishes on. That is
    /// what lets a read start on any page and still know exactly which sample
    /// it is at.
    ///
    /// The codec is still Concentus. Only the container is read here.
    /// </summary>
    internal sealed class OggOpusReader : IDisposable
    {
        /// <summary>
        /// Opus always decodes at 48kHz. It is not a choice the format offers —
        /// the codec is defined at that rate and the encoder's original rate is
        /// only a tag.
        /// </summary>
        public const int Rate = 48000;

        /// <summary>
        /// The decoder's output: stereo for an ordinary stream (Concentus mixes
        /// mono up to it), and every channel of a surround one, which
        /// <see cref="OggDecoder"/> folds down to the device.
        /// </summary>
        public int Channels { get; private set; } = 2;

        /// <summary>The longest packet Opus allows: 120 milliseconds.</summary>
        public const int MaxPacketFrames = 5760;

        /// <summary>
        /// How much is decoded and thrown away before a seek target, so the
        /// decoder has converged by the time anything is heard.
        ///
        /// RFC 7845 asks for 80 milliseconds, which is enough to sound right and
        /// not enough to be right: measured against a straight decode, seeks with
        /// 80ms (and 200ms) of run-up differed by up to 0.0016 — faint, and
        /// still not the same audio. 400ms matched to the sample on every one of
        /// two hundred seeks; this is half a second, plus a longest-possible
        /// packet because the first packet after the page a seek lands on may be
        /// the tail of one that began earlier and is dropped.
        /// </summary>
        private const int PreRoll = 24000 + MaxPacketFrames;

        private readonly OggPages _pages;
        private IOpusDecoder? _codec;

        /// <summary>
        /// For a surround stream — channel mapping family 1 or 255 — whose
        /// packets hold several Opus streams each. The plain decoder read those
        /// as noise, or threw and left silence.
        /// </summary>
        private IOpusMultiStreamDecoder? _multi;
        private long _audioStart;

        // Decoding.
        private readonly Queue<(byte[] Packet, long Start)> _packets = new();
        private readonly List<byte[]> _finished = new();
        private long _clock = -1;
        private long _trimAt = long.MaxValue;
        private long _discardUntil;
        private bool _lastPage;
        private bool _ended;

        public int PreSkip { get; private set; }

        /// <summary>The granule position of the last page, or -1 when it could not be found.</summary>
        public long EndGranule { get; private set; } = -1;

        public double Duration => EndGranule > _base + PreSkip ? (EndGranule - _base - PreSkip) / (double)Rate : 0;

        /// <summary>
        /// The granule position the stream starts at. Zero for a file that was
        /// recorded from its beginning; a capture that began part way through a
        /// broadcast carries the broadcast's count on its first page, and
        /// measuring from zero made it that much too long and every seek that
        /// much early.
        /// </summary>
        private long _base;

        private OggOpusReader(Stream stream)
        {
            _pages = new OggPages(stream);
        }

        /// <summary>
        /// The stream as Ogg Opus, or null when it is not one. Reads the head of
        /// the file and a little of its tail, and nothing in between.
        /// </summary>
        public static OggOpusReader? Open(Stream stream)
        {
            if (!stream.CanSeek) return null;

            var reader = new OggOpusReader(stream);
            try
            {
                if (reader.ReadHeaders())
                {
                    reader.EndGranule = reader._pages.FindEndGranule(reader._audioStart);
                    reader.Restart(0);

                    // Where the first audio page says its packets began. Only
                    // a page that is not also the last can say: the last one's
                    // granule is trimmed, and RFC 7845 starts such a stream at 0.
                    if (reader.LoadPage() && !reader._lastPage && reader._packets.Count > 0)
                        reader._base = Math.Max(0, reader._packets.Peek().Start);

                    reader.Restart(0);
                    return reader;
                }
            }
            catch { }

            reader.Dispose();
            return null;
        }

        /// <summary>
        /// The identification header on the first page, then past the comment
        /// header to where the audio begins.
        /// </summary>
        private bool ReadHeaders()
        {
            var body = _pages.Body;
            if (!_pages.FindStream(page => _pages.FirstPacketLength(page) >= 19 && StartsWith(body, "OpusHead"),
                    out var first))
                return false;

            int length = _pages.FirstPacketLength(first);
            PreSkip = body[10] | (body[11] << 8);

            // The output gain, in the same 1/256 dB steps Concentus takes.
            int gain = (short)(body[16] | (body[17] << 8));

            int channels = body[9];
            int family = body[18];
            if (family == 0 || channels < 1)
            {
                _codec = OpusCodecFactory.CreateDecoder(Rate, 2, null);
                _codec.Gain = gain;
                Channels = 2;
            }
            else
            {
                if (length < 21 + channels) return false;
                var mapping = body.AsSpan(21, channels).ToArray();
                _multi = OpusCodecFactory.CreateMultiStreamDecoder(Rate, channels, body[19], body[20], mapping, null);
                _multi.Gain = gain;
                Channels = channels;
            }

            // The comment header can run across pages — a cover picture makes it
            // megabytes — so this walks until a packet finishes, which is where
            // it ends. The audio starts on that page or the next; starting on
            // that one is safe, because what is left of the comment header is
            // either a dropped fragment or recognised and skipped.
            long at = first.End;
            while (_pages.FindPage(at, _pages.Length, needGranule: false, out var page))
            {
                at = page.End;
                if (_pages.PacketsFinishing(page) > 0)
                {
                    _audioStart = page.Offset;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Moves to <paramref name="seconds"/> into the audio. Lands on the
        /// sample: the page is found by bisection, the pre-roll is decoded and
        /// thrown away, and the first frame handed back is the one asked for.
        /// </summary>
        public bool SeekTo(double seconds)
        {
            try
            {
                Restart(seconds);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void Restart(double seconds)
        {
            long target = _base + PreSkip + (long)Math.Round(Math.Max(0, seconds) * Rate);
            if (EndGranule >= 0 && target > EndGranule) target = EndGranule;

            long goal = target - PreRoll;
            long before = -1;
            _pages.Restart(goal > _base ? _pages.FindStart(goal, _audioStart, out before) : _audioStart);

            _codec?.ResetState();
            _multi?.ResetState();
            _packets.Clear();

            // Where the audio before the first page read ends, when that is
            // known. The last page's granule is where the stream stops, not where
            // its packets end, so a seek whose first page is the last one needs
            // this to place them; worked back from the end instead, everything
            // landed early by however much the end is trimmed.
            _clock = before;
            _trimAt = long.MaxValue;
            _discardUntil = target;
            _lastPage = false;
            _ended = false;
        }

        /// <summary>
        /// Decodes into <paramref name="into"/> at <paramref name="at"/>, which
        /// must have room for <see cref="MaxPacketFrames"/> frames of
        /// <see cref="Channels"/>, and
        /// returns how many frames it wrote. **Zero means the stream has ended**
        /// and nothing else: packets that are all pre-skip or pre-roll are
        /// decoded and passed over here, and every pass reads further into the
        /// file, so this always comes back.
        /// </summary>
        public int Read(float[] into, int at)
        {
            var output = into.AsSpan(at, MaxPacketFrames * Channels);

            while (true)
            {
                if (_packets.Count == 0)
                {
                    if (!LoadPage()) return 0;
                    continue;
                }

                var (packet, start) = _packets.Dequeue();

                int frames;
                try
                {
                    frames = _multi != null
                        ? _multi.DecodeMultistream(packet, output, MaxPacketFrames, false)
                        : _codec!.Decode(packet, output, MaxPacketFrames, false);
                }
                catch { continue; }

                long from = start, to = Math.Min(start + frames, _trimAt);
                long keepFrom = Math.Max(from, _discardUntil);
                if (keepFrom >= to) continue;

                int skip = (int)(keepFrom - from);
                int keep = (int)(to - keepFrom);
                if (skip > 0) output.Slice(skip * Channels, keep * Channels).CopyTo(output);
                return keep;
            }
        }

        /// <summary>
        /// Reads the next page of our stream and queues the packets that finish
        /// on it, each with the sample it starts at. False at the end.
        /// </summary>
        private bool LoadPage()
        {
            while (true)
            {
                if (_ended || _lastPage) { _ended = true; return false; }

                _finished.Clear();
                if (!_pages.ReadPackets(_finished, out var page, out _, out _)) { _ended = true; return false; }

                // The two headers are recognised by what they say rather than by
                // where they are, which covers a stream that starts its audio on
                // the page its comment header ends on.
                var durations = new List<(byte[] Packet, int Frames)>(_finished.Count);
                long total = 0;
                foreach (var packet in _finished)
                {
                    if (packet.Length == 0) continue;
                    if (StartsWith(packet, "OpusHead") || StartsWith(packet, "OpusTags")) continue;

                    int frames;
                    try { frames = OpusPacketInfo.GetNumSamples(packet, Rate); }
                    catch { continue; }
                    if (frames <= 0) continue;

                    durations.Add((packet, frames));
                    total += frames;
                }

                _lastPage = page.Last;
                _trimAt = page.Last && page.Granule >= 0 ? page.Granule : long.MaxValue;

                if (durations.Count == 0) continue;

                // Worked back from the end of the page — except on the last one,
                // whose granule is where the audio stops rather than where its
                // packets do. That is how the end of a file is trimmed to the
                // sample, so there the running clock places them instead.
                // A first page that is also the last has a granule that is already
                // trimmed, so working back from it lands before zero by the
                // encoder's padding; RFC 7845 says such a stream starts at zero.
                long start = (page.Last || page.Granule < 0) && _clock >= 0 ? _clock
                    : page.Granule >= 0 ? (page.Last ? Math.Max(0, page.Granule - total) : page.Granule - total)
                    : 0;

                foreach (var (packet, frames) in durations)
                {
                    _packets.Enqueue((packet, start));
                    start += frames;
                }

                _clock = start;
                return true;
            }
        }

        private static bool StartsWith(byte[] data, string ascii)
        {
            if (data.Length < ascii.Length) return false;
            for (int i = 0; i < ascii.Length; i++)
                if (data[i] != (byte)ascii[i]) return false;
            return true;
        }

        /// <summary>Closes the decoder. The stream belongs to the caller.</summary>
        public void Dispose()
        {
            try { _codec?.Dispose(); } catch { }
            try { _multi?.Dispose(); } catch { }
            _packets.Clear();
        }
    }
}
