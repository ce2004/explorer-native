using System;
using System.Collections.Generic;
using System.IO;

namespace ExplorerNative
{
    /// <summary>
    /// The Ogg container, read a page at a time: find a page, check it, and
    /// turn the pages of one logical stream back into packets.
    ///
    /// Shared by <see cref="OggOpusReader"/> and <see cref="OggVorbisPackets"/>,
    /// and it exists for the reason written on the first of them: both
    /// libraries' own Ogg readers read and index every page of a file before
    /// they will say how long it is or seek in it. That is seconds on a long
    /// recording, and a network read of the whole file on the Drive letter.
    /// Everything here reads the pages it needs and no others:
    ///
    ///   * the length is the granule position on the last page, so only the
    ///     tail is read for it;
    ///   * granule positions only increase, so a seek target is found by
    ///     bisection in about twenty page reads however long the file is.
    /// </summary>
    internal sealed class OggPages
    {
        /// <summary>
        /// A page is 27 bytes of header, up to 255 lacing values, and up to 255
        /// segments of 255 bytes.
        /// </summary>
        private const int MaxPageBody = 255 * 255;
        public const int MaxPage = 27 + 255 + MaxPageBody;

        /// <summary>
        /// Where bisection stops and the pages are simply walked. A few pages'
        /// worth, which is fewer reads than the halvings it would take to get
        /// any closer.
        /// </summary>
        private const long BisectFloor = 64 * 1024;

        private static readonly uint[] CrcTable = BuildCrcTable();
        private static readonly byte[] ZeroCrc = new byte[4];

        private readonly Stream _stream;

        // The page just read: its header and lacing, and its body. Valid until
        // the next page is read.
        private readonly byte[] _head = new byte[27 + 255];
        private readonly byte[] _scan = new byte[64 * 1024];

        public OggPages(Stream stream)
        {
            _stream = stream;
            Length = stream.Length;
        }

        public long Length { get; }

        /// <summary>The logical stream being followed. Pages of any other are passed over.</summary>
        public int Serial { get; set; }

        /// <summary>The lacing values of the page just read.</summary>
        public byte[] Lacing { get; } = new byte[255];

        /// <summary>The body of the page just read.</summary>
        public byte[] Body { get; } = new byte[MaxPageBody];

        public readonly struct Page
        {
            public readonly long Offset;
            public readonly int Size;
            public readonly byte Flags;
            public readonly long Granule;
            public readonly int Serial;
            public readonly int Segments;

            public Page(long offset, int size, byte flags, long granule, int serial, int segments)
            {
                Offset = offset;
                Size = size;
                Flags = flags;
                Granule = granule;
                Serial = serial;
                Segments = segments;
            }

            public long End => Offset + Size;
            public bool Continued => (Flags & 1) != 0;
            public bool First => (Flags & 2) != 0;
            public bool Last => (Flags & 4) != 0;
        }

        /// <summary>The length of the page's first packet, or -1 when it runs on to the next page.</summary>
        public int FirstPacketLength(in Page page)
        {
            int length = 0;
            for (int i = 0; i < page.Segments; i++)
            {
                length += Lacing[i];
                if (Lacing[i] < 255) return length;
            }
            return -1;
        }

        /// <summary>How many packets finish on the page just read.</summary>
        public int PacketsFinishing(in Page page)
        {
            int count = 0;
            for (int i = 0; i < page.Segments; i++)
                if (Lacing[i] < 255) count++;
            return count;
        }

        /// <summary>
        /// The first page of our stream that starts at or after
        /// <paramref name="from"/> and before <paramref name="limit"/>, and when
        /// asked, one that has a granule position.
        /// </summary>
        public bool FindPage(long from, long limit, bool needGranule, out Page page)
        {
            long at = from;

            while (at < limit)
            {
                if (TryPageAt(at, out page))
                {
                    if (page.Serial == Serial && (!needGranule || page.Granule >= 0)) return true;
                    at = page.End;
                    continue;
                }

                at = FindCapture(at + 1, limit);
                if (at < 0) break;
            }

            page = default;
            return false;
        }

        /// <summary>
        /// The start page of the first logical stream whose first packet
        /// <paramref name="wanted"/> accepts, which then becomes the stream
        /// followed. Every stream's start page comes before any other page, so
        /// this reads only the head of the file. The page's lacing and body are
        /// left in <see cref="Lacing"/> and <see cref="Body"/>.
        ///
        /// Page zero alone was the old test, and a file that leads with some
        /// other stream — an Ogg Skeleton track, say — would not open at all.
        /// </summary>
        public bool FindStream(Func<Page, bool> wanted, out Page first)
        {
            long at = 0;
            while (TryPageAt(at, out first) && first.First)
            {
                if (wanted(first))
                {
                    Serial = first.Serial;
                    return true;
                }
                at = first.End;
            }

            first = default;
            return false;
        }

        /// <summary>
        /// The granule position of the last page of our stream, or -1 when it
        /// could not be found.
        ///
        /// Normally in the last window of the file. When the file goes on past
        /// our stream — a chained file, one recording after another — the pages
        /// of each stream are all together, so where ours ends is found by
        /// bisection on the serial numbers rather than by reading backwards: the
        /// old way read up to seventeen megabytes, a network read apiece on the
        /// Drive letter, and still gave up.
        /// </summary>
        public long FindEndGranule(long notBefore)
        {
            StreamLimit = Length;
            long window = 2L * MaxPage;
            long last = LastGranuleFrom(Math.Max(notBefore, Length - window), Length);
            if (last >= 0) return last;

            // A few wider looks first. An Ogg with video in it interleaves the two
            // streams, and a bisection that assumes each stream's pages sit
            // together follows the video to the start of the file and calls the
            // audio a few seconds long. Audio pages come every second or so, so
            // one of these finds the last of them.
            for (long wider = window * 2; wider <= window * 8; wider *= 2)
            {
                if (Length - wider <= notBefore) break;
                last = LastGranuleFrom(Length - wider, Length);
                if (last >= 0) return last;
            }

            long lo = notBefore, hi = Length;
            while (hi - lo > window)
            {
                long mid = lo + (hi - lo) / 2;
                if (!FindAnyPage(mid, hi, out var page)) { hi = mid; continue; }
                if (page.Serial == Serial) lo = page.Offset;
                else hi = mid;
            }

            // Our stream is over by here, and a seek has no reason to look past
            // it: bisecting to the end of the file walked every page of every
            // recording after this one, a page at a time.
            StreamLimit = Math.Min(Length, hi + window);
            return LastGranuleFrom(lo, StreamLimit);
        }

        /// <summary>
        /// Where our stream's pages end, as far as <see cref="FindEndGranule"/>
        /// found out: the end of the file, unless the file goes on to another
        /// recording.
        /// </summary>
        public long StreamLimit { get; private set; } = long.MaxValue;

        private long LastGranuleFrom(long from, long limit)
        {
            long last = -1;
            long at = from;
            while (FindPage(at, limit, needGranule: true, out var page))
            {
                last = page.Granule;
                at = page.End;
            }
            return last;
        }

        /// <summary>The first page of any stream at or after <paramref name="from"/>.</summary>
        private bool FindAnyPage(long from, long limit, out Page page)
        {
            long at = from;
            while (at < limit)
            {
                if (TryPageAt(at, out page)) return true;
                at = FindCapture(at + 1, limit);
                if (at < 0) break;
            }

            page = default;
            return false;
        }

        /// <summary>
        /// The same, and the granule position of the page it starts after — the
        /// end of the audio before the first packet that will be read — or -1
        /// when it starts at <paramref name="start"/>.
        /// </summary>
        public long FindStart(long goal, long start, out long granule)
        {
            long limit = Math.Min(Length, StreamLimit);
            long lo = start, hi = limit, best = start;
            granule = -1;

            while (hi - lo > BisectFloor)
            {
                long mid = lo + (hi - lo) / 2;

                if (!FindPage(mid, hi, needGranule: true, out var page)) { hi = mid; continue; }

                if (page.Granule <= goal)
                {
                    best = page.End;
                    granule = page.Granule;
                    lo = page.End;
                }
                else hi = mid;
            }

            // Every page from here to the first one past the goal is at most a
            // window and a page away.
            long at = lo;
            while (FindPage(at, limit, needGranule: true, out var page) && page.Granule <= goal)
            {
                best = page.End;
                granule = page.Granule;
                at = page.End;
            }

            return best;
        }

        /// <summary>The next "OggS" at or after <paramref name="from"/>, or -1.</summary>
        private long FindCapture(long from, long limit)
        {
            long at = from;

            while (at < limit)
            {
                int want = (int)Math.Min(_scan.Length, Length - at);
                int got = ReadAt(at, _scan, want);
                if (got < 4) return -1;

                for (int i = 0; i + 3 < got; i++)
                {
                    if (_scan[i] == (byte)'O' && _scan[i + 1] == (byte)'g' &&
                        _scan[i + 2] == (byte)'g' && _scan[i + 3] == (byte)'S')
                        return at + i < limit ? at + i : -1;
                }

                // Three bytes back, in case the pattern straddles the window.
                at += got - 3;
            }

            return -1;
        }

        /// <summary>
        /// Reads the page at <paramref name="at"/> and checks its CRC. The
        /// lacing values land in <see cref="Lacing"/> and the body in
        /// <see cref="Body"/>.
        /// </summary>
        public bool TryPageAt(long at, out Page page)
        {
            page = default;
            if (at < 0 || at + 27 > Length) return false;

            if (ReadAt(at, _head, 27) != 27) return false;
            if (_head[0] != (byte)'O' || _head[1] != (byte)'g' ||
                _head[2] != (byte)'g' || _head[3] != (byte)'S' || _head[4] != 0)
                return false;

            int segments = _head[26];
            if (ReadAt(at + 27, Lacing, segments) != segments) return false;
            Array.Copy(Lacing, 0, _head, 27, segments);

            int header = 27 + segments;
            int body = 0;
            for (int i = 0; i < segments; i++) body += Lacing[i];

            if (at + header + body > Length) return false;
            if (ReadAt(at + header, Body, body) != body) return false;

            uint crc = Crc(0, _head, 0, 22);
            crc = Crc(crc, ZeroCrc, 0, 4);
            crc = Crc(crc, _head, 26, header - 26);
            crc = Crc(crc, Body, 0, body);
            if (crc != BitConverter.ToUInt32(_head, 22)) return false;

            page = new Page(at, header + body, _head[5],
                BitConverter.ToInt64(_head, 6), BitConverter.ToInt32(_head, 14), segments);
            return true;
        }

        /// <summary>
        /// Always into the start of the buffer: <see cref="ComStream"/> reads a
        /// non-zero offset through a temporary array, and this runs for every
        /// page played.
        /// </summary>
        private int ReadAt(long position, byte[] into, int count)
        {
            if (count <= 0) return 0;

            _stream.Position = position;
            int got = 0;
            while (got < count)
            {
                int read = _stream.Read(into, got, count - got);
                if (read <= 0) break;
                got += read;
            }
            return got;
        }

        // ---------- packets ----------

        private MemoryStream? _partial;

        /// <summary>Where the next page is read from.</summary>
        public long NextPage { get; private set; }

        /// <summary>
        /// Starts reading packets from the page at <paramref name="offset"/>.
        /// Whatever was being assembled belongs to somewhere else.
        /// </summary>
        public void Restart(long offset)
        {
            NextPage = offset;
            _partial = null;
        }

        /// <summary>
        /// Reads the next page of our stream and adds the packets that finish on
        /// it to <paramref name="finished"/>. False at the end of the file.
        ///
        /// A continuation with nothing to continue — the first page read after
        /// a <see cref="Restart"/>, or after damage — is the tail of a packet
        /// that began somewhere this read never saw. It cannot be decoded, so it
        /// is dropped, and <paramref name="dropped"/> says so: the packets after
        /// it are still placed correctly, because a page's packets are placed
        /// from its end.
        /// </summary>
        public bool ReadPackets(List<byte[]> finished, out Page page, out bool dropped, out bool resync)
        {
            resync = false;

            while (true)
            {
                dropped = false;
                if (NextPage >= Length) { page = default; return false; }

                if (!TryPageAt(NextPage, out page))
                {
                    // Damaged or truncated. Whatever was being assembled is gone
                    // with it; carry on from the next page that checks out.
                    _partial = null;
                    resync = true;
                    if (!FindPage(NextPage + 1, Length, needGranule: false, out page))
                    {
                        NextPage = Length;
                        return false;
                    }
                }

                NextPage = page.End;
                if (page.Serial != Serial) continue;

                if (!page.Continued) _partial = null;
                bool dropFirst = page.Continued && _partial == null;
                dropped = dropFirst;

                int offset = 0, length = 0;

                for (int i = 0; i < page.Segments; i++)
                {
                    length += Lacing[i];
                    if (Lacing[i] == 255) continue;

                    if (dropFirst) dropFirst = false;
                    else if (_partial != null)
                    {
                        _partial.Write(Body, offset, length);
                        finished.Add(_partial.ToArray());
                    }
                    else finished.Add(Body.AsSpan(offset, length).ToArray());

                    _partial = null;
                    offset += length;
                    length = 0;
                }

                if (page.Segments > 0 && Lacing[page.Segments - 1] == 255 && !dropFirst)
                {
                    _partial ??= new MemoryStream();
                    _partial.Write(Body, offset, length);
                }

                return true;
            }
        }

        /// <summary>Ogg's CRC: polynomial 0x04C11DB7, not reflected, starting at zero.</summary>
        private static uint Crc(uint crc, byte[] data, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
                crc = (crc << 8) ^ CrcTable[((crc >> 24) ^ data[i]) & 0xFF];
            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint r = i << 24;
                for (int bit = 0; bit < 8; bit++)
                    r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
                table[i] = r;
            }
            return table;
        }
    }
}
