using System;
using System.Collections.Generic;
using System.IO;
using NVorbis;
using IPacket = NVorbis.Contracts.IPacket;
using IPacketProvider = NVorbis.Contracts.IPacketProvider;
using GetPacketGranuleCount = NVorbis.Contracts.GetPacketGranuleCount;

namespace ExplorerNative
{
    /// <summary>
    /// Ogg Vorbis packets for NVorbis, read the way <see cref="OggPages"/>
    /// reads: the length off the last page and a seek by bisection.
    ///
    /// **NVorbis's own container reader indexes every page of the file** the
    /// first time anything asks for the length or a seek — measured at 290ms
    /// and 68MB allocated for an hour of audio, so seconds for a long recording
    /// and a whole-file network read on the Drive letter. This is the same
    /// fix as <see cref="OggOpusReader"/>, and NVorbis still does all of the
    /// decoding: <c>StreamDecoder</c> takes any <see cref="IPacketProvider"/>.
    ///
    /// The contract that matters is <see cref="SeekTo"/>'s, and it is exact:
    /// the decoder reads one packet of pre-roll, then one packet whose output
    /// contains the target, and skips into it by however far the returned
    /// granule position is short of the target. So the packet has to be found
    /// by its own granule position, not just the page — see there.
    /// </summary>
    internal sealed class OggVorbisPackets : IPacketProvider
    {
        /// <summary>
        /// How far before a seek target reading starts: twice the longest block
        /// Vorbis allows. Enough that the packet holding the target and the one
        /// before it are both read whole, even when the first packet read is a
        /// fragment that has to be dropped.
        /// </summary>
        private const long SeekMargin = 2 * 8192;

        private readonly OggPages _pages;
        private readonly Queue<Packet> _queue = new();
        private readonly List<byte[]> _finished = new();
        private long _audioStart;

        /// <summary>The stream's short and long block sizes. See <see cref="TrailingShift"/>.</summary>
        private int _short, _long;

        /// <summary>
        /// Whether the three header packets are being handed out. Only from the
        /// top of the file: after any seek, a packet whose first bit says
        /// "header" is passed over rather than handed to a decoder that has
        /// already read its headers.
        /// </summary>
        private bool _headers;

        private OggVorbisPackets(Stream stream)
        {
            _pages = new OggPages(stream);
        }

        public bool CanSeek => true;

        public int StreamSerial => _pages.Serial;

        /// <summary>The granule position of the last page, or -1 when it could not be found.</summary>
        public long EndGranule { get; private set; } = -1;

        /// <summary>
        /// The stream's packets, or null when the file does not start with a
        /// Vorbis identification header.
        /// </summary>
        public static OggVorbisPackets? Open(Stream stream)
        {
            if (!stream.CanSeek) return null;

            try
            {
                var packets = new OggVorbisPackets(stream);
                if (!packets.ReadHeaders()) return null;

                packets.EndGranule = packets._pages.FindEndGranule(packets._audioStart);
                packets._pages.Restart(0);
                packets._headers = true;
                return packets;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The identification header on the first page, then on to the page the
        /// third header ends on. The audio starts after it — or on it, in a file
        /// that does not start its audio on a fresh page, which the header
        /// filter then covers.
        /// </summary>
        private bool ReadHeaders()
        {
            var body = _pages.Body;
            if (!_pages.FindStream(page => _pages.FirstPacketLength(page) >= 30 && body[0] == 1 &&
                    body[1] == (byte)'v' && body[2] == (byte)'o' && body[3] == (byte)'r' &&
                    body[4] == (byte)'b' && body[5] == (byte)'i' && body[6] == (byte)'s',
                    out var first))
                return false;

            // The two block sizes, as powers of two in one byte.
            _short = 1 << (body[28] & 0x0F);
            _long = 1 << (body[28] >> 4);

            int finished = _pages.PacketsFinishing(first);
            long at = first.End;
            if (finished >= 3) { _audioStart = first.Offset; return true; }

            while (_pages.FindPage(at, _pages.Length, needGranule: false, out var page))
            {
                at = page.End;
                finished += _pages.PacketsFinishing(page);
                if (finished < 3) continue;

                _audioStart = finished == 3 ? page.End : page.Offset;
                return true;
            }

            return false;
        }

        public long GetGranuleCount() => Math.Max(0, EndGranule - _base);

        /// <summary>
        /// The granule position the stream starts at, when that is far from zero
        /// — a capture begun part way through a broadcast carries the broadcast's
        /// count. Everything NVorbis is told is relative to it, and everything
        /// read off a page is absolute, so the two are converted here. Found on
        /// the first seek, because only then is NVorbis's own count of a packet
        /// to hand; see <see cref="EnsureBase"/>.
        /// </summary>
        private long _base;
        private bool _baseKnown;

        /// <summary>
        /// Below this the difference between a page's granule and the samples
        /// counted on it is the ordinary rounding of block boundaries, not a
        /// stream that started late — and treating it as an offset would move
        /// every seek of every ordinary file.
        /// </summary>
        private const long LateStartSamples = 96_000;

        private void EnsureBase(GetPacketGranuleCount count)
        {
            if (_baseKnown) return;
            _baseKnown = true;

            _queue.Clear();
            _headers = false;
            _endOfStream = false;
            _pages.Restart(_audioStart);

            if (ReadPage(out var packets, out var page, out _) && !page.Last && page.Granule >= 0)
            {
                // The stream's first audio packet produces nothing — it has no
                // predecessor to overlap — so it is left out of what the page
                // accounts for, whatever count NVorbis gives it. And NVorbis
                // ends the page where it ends it, which is past the granule
                // when the page closes on a long block before a short one.
                var counts = new int[packets.Count];
                long total = 0;
                for (int i = 0; i < packets.Count; i++)
                {
                    counts[i] = packets[i].IsResync ? 0 : count(packets[i]);
                    packets[i].Reset();
                    if (i > 0) total += counts[i];
                }
                long start = page.Granule + (TrailingShift(counts, null) ?? 0) - total;
                if (start > LateStartSamples) _base = start;
            }

            _queue.Clear();
            _headers = false;
            _endOfStream = false;
        }

        public IPacket GetNextPacket()
        {
            if (_queue.Count == 0 && !Fill()) return null!;
            return _queue.Dequeue();
        }

        public IPacket PeekNextPacket()
        {
            if (_queue.Count == 0 && !Fill()) return null!;
            return _queue.Peek();
        }

        /// <summary>Queues the next page's packets. False at the end of the stream.</summary>
        private bool Fill()
        {
            while (_queue.Count == 0)
            {
                if (!ReadPage(out var packets, out _, out _)) return false;
                foreach (var packet in packets) _queue.Enqueue(packet);
            }
            return true;
        }

        private bool _endOfStream;

        /// <summary>
        /// The packets finishing on the next page, with the page's granule
        /// position on the last of them and the end-of-stream flag where it
        /// belongs.
        /// </summary>
        private bool ReadPage(out List<Packet> packets, out OggPages.Page page, out bool dropped)
        {
            packets = new List<Packet>();
            page = default;
            dropped = false;

            while (packets.Count == 0)
            {
                if (_endOfStream) return false;

                _finished.Clear();
                if (!_pages.ReadPackets(_finished, out page, out dropped, out bool resync)) return false;

                foreach (var data in _finished)
                {
                    if (data.Length == 0) continue;

                    // Headers have their lowest bit set and audio packets do not;
                    // that is the first thing the decoder itself reads.
                    if (!_headers && (data[0] & 1) != 0) continue;

                    packets.Add(new Packet(data) { IsResync = resync && packets.Count == 0 });
                }

                if (page.Last) _endOfStream = true;
                if (packets.Count == 0) continue;

                // The granule position goes only where NVorbis needs it: on the
                // last page, where it trims the end to it, and after lost sync,
                // where it has to learn where it is again. Given on every page, a
                // decode from the top learned its position from the first page —
                // and a first page ending on a long block before a short one says
                // a position 448 samples from where NVorbis's output really is.
                // Measured: a straight decode then played 16 samples of padding
                // past the end of the file.
                var last = packets[packets.Count - 1];
                if (page.Granule >= 0 && (page.Last || resync)) last.GranulePosition = page.Granule - _base;
                if (page.Last) last.IsEndOfStream = true;
            }

            return true;
        }

        /// <summary>
        /// Positions the stream for the decoder's seek, and returns where the
        /// output of the packet after the next one begins.
        ///
        /// What <c>StreamDecoder.SeekTo</c> does with the answer: reads one
        /// packet, which produces nothing because it has no predecessor to
        /// overlap with; reads a second, whose output starts at the returned
        /// granule position; and skips the difference between that and the
        /// target. So the second packet must be the one whose output contains
        /// the target — it ends past it and starts at or before it — and the
        /// first must be the one immediately before it, read whole. A page is
        /// not precise enough: its granule position only says where its *last*
        /// packet ends.
        ///
        /// So each packet's end is worked back from the end of the page it
        /// finishes on, using the decoder's own count of what each packet
        /// produces. The count reads bits out of the packet, which is why every
        /// packet is rewound afterwards before anything else reads it.
        /// </summary>
        public long SeekTo(long granulePos, int preRoll, GetPacketGranuleCount getPacketGranuleCount)
        {
            _queue.Clear();
            _headers = false;
            _endOfStream = false;

            if (granulePos <= 0)
            {
                _pages.Restart(_audioStart);
                return 0;
            }

            // From here on in the file's own numbers.
            EnsureBase(getPacketGranuleCount);
            long relative = granulePos;
            granulePos += _base;

            long goal = granulePos - SeekMargin;
            long previous = -1;
            long from = goal > 0 ? _pages.FindStart(goal, _audioStart, out previous) : _audioStart;

            // Never starting on the last page: its granule position is where the
            // stream is trimmed, not where its packets end, so nothing on it can
            // be placed without the page before. A page earlier, then.
            for (int back = 0; back < 2 && previous > 0 &&
                               _pages.TryPageAt(from, out var first) && first.Last; back++)
                from = _pages.FindStart(previous - 1, _audioStart, out previous);

            _pages.Restart(from);

            if (Find(granulePos, getPacketGranuleCount, previous, out long start)) return start - _base;

            // The page found was too late to hold the packet before the target,
            // which the margin should make impossible. From the top is slow and
            // right.
            _queue.Clear();
            _endOfStream = false;
            _pages.Restart(_audioStart);
            if (Find(granulePos, getPacketGranuleCount, -1, out start)) return start - _base;

            // Past the end. Nothing more to read; the decoder decides whether
            // that is where it was asked to be.
            _queue.Clear();
            _pages.Restart(_pages.Length);
            _endOfStream = true;
            return relative;
        }

        /// <summary>
        /// Reads pages from where the stream has been put until it finds the
        /// packet whose output holds <paramref name="target"/>, and queues it
        /// with the one before. <paramref name="previous"/> is the granule
        /// position of the page before the first one read, or -1.
        ///
        /// Each packet's end is worked back from its page's granule position
        /// with the decoder's own count of what each packet produces — and then
        /// moved to where NVorbis really ends it, which is not always where the
        /// format says. See <see cref="TrailingShift"/>.
        /// </summary>
        private bool Find(long target, GetPacketGranuleCount count, long previous, out long start)
        {
            start = 0;
            Packet? before = null;
            long beforeEnd = 0;
            bool? beforeLong = null;

            while (ReadPage(out var packets, out var page, out bool dropped))
            {
                int n = packets.Count;
                var counts = new int[n];
                long total = 0;
                for (int i = 0; i < n; i++)
                {
                    counts[i] = packets[i].IsResync ? 0 : count(packets[i]);
                    packets[i].Reset();
                    total += counts[i];
                }

                long? shift = TrailingShift(counts, beforeLong);
                var ends = new long[n];

                if (before != null && (page.Last || shift == null || page.Granule < 0))
                {
                    // Forward from the page before, whose end is already where
                    // NVorbis puts it. Always so for the last page, whose granule
                    // is where the stream is trimmed rather than where its
                    // packets end.
                    long end = beforeEnd;
                    for (int i = 0; i < n; i++) { end += counts[i]; ends[i] = end; }
                }
                else
                {
                    if (shift == null)
                    {
                        // Undecidable from the packets alone: the first page read,
                        // ending on a packet whose neighbours are not known. The
                        // page before says how the two ends differ, and at most
                        // one of them is shifted.
                        long difference = previous >= 0 && !dropped ? previous - (page.Granule - total) : 0;
                        shift = difference > 0 ? difference : 0;
                    }

                    long end = page.Granule + shift.Value;
                    for (int i = n - 1; i >= 0; i--) { ends[i] = end; end -= counts[i]; }
                }

                for (int i = 0; i < n; i++)
                {
                    var prior = i > 0 ? packets[i - 1] : before;
                    long priorEnd = i > 0 ? ends[i - 1] : beforeEnd;

                    if (ends[i] > target && prior != null && priorEnd <= target)
                    {
                        _queue.Enqueue(prior);
                        for (int j = i; j < n; j++) _queue.Enqueue(packets[j]);
                        start = priorEnd;
                        return true;
                    }

                    if (ends[i] > target) return false;
                }

                before = packets[n - 1];
                beforeEnd = ends[n - 1];
                beforeLong = IsLong(counts[n - 1]);
                previous = page.Granule;
            }

            return false;
        }

        private bool IsLong(int produced) => produced != _short / 2;

        /// <summary>
        /// How far past its page's granule position NVorbis ends the page's last
        /// packet: a quarter of the difference between the block sizes when
        /// that packet is a long block followed by a short one, and nothing
        /// otherwise. Null when the packets on hand cannot say.
        ///
        /// The format ends a packet's output at the centre of its window.
        /// NVorbis ends a long block's output where the next window starts to
        /// overlap it, which is later when the next block is short, and makes up
        /// the difference in that short block. Its own seek corrects for this
        /// with a guess from neighbouring pages; measured without any correction,
        /// seeks near such a boundary landed 448 samples late at 44.1kHz.
        ///
        /// What a packet produces, by NVorbis's count, gives it away: a short
        /// block always produces half a short block, and a long one half a long
        /// block, give or take that quarter-difference depending on its
        /// neighbours — more when the one before is long and the one after short,
        /// less the other way round, and neither when they match, in which case
        /// the packet before decides.
        /// </summary>
        private long? TrailingShift(int[] counts, bool? beforeLong)
        {
            if (_long == _short || counts.Length == 0) return 0;

            int half = _long / 2;
            int quarter = (_long - _short) / 4;
            int last = counts[^1];

            if (last <= 0) return null;
            if (!IsLong(last)) return 0;
            if (last == half + quarter) return quarter;
            if (last == half - quarter) return 0;
            if (last != half) return null;

            bool? priorLong = counts.Length >= 2 ? (counts[^2] > 0 ? IsLong(counts[^2]) : null) : beforeLong;
            if (priorLong == null) return null;

            // Both neighbours the same: long means the next is long too, short
            // means the next is short.
            return priorLong.Value ? 0 : quarter;
        }

        /// <summary>One packet, read a bit at a time by NVorbis.</summary>
        private sealed class Packet : DataPacket
        {
            private readonly byte[] _data;
            private int _next;

            public Packet(byte[] data) => _data = data;

            protected override int TotalBits => _data.Length * 8;

            protected override int ReadNextByte() => _next < _data.Length ? _data[_next++] : -1;

            public override void Reset()
            {
                _next = 0;
                base.Reset();
            }
        }
    }
}
