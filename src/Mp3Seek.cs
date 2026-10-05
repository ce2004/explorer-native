using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// Finds the MP3 frame a seek target is in, by counting frames.
    ///
    /// An MPEG audio frame does not say where it is — there is nothing to
    /// bisect on — so the only exact answer is to walk the frame headers from
    /// the start, which is what every player that seeks MP3 accurately does.
    /// It is a walk over headers, not a decode: for an hour-long file, a read of
    /// the file and a hundred and fifty thousand four-byte parses. It runs once,
    /// in the background, keeps the offset of every eighth frame, and follows a
    /// download as it arrives — a seek into any part it has walked is exact, and
    /// one past it uses Media Foundation's own.
    ///
    /// Decoding from a frame in the middle of the file needs a little run-up:
    /// Layer III borrows bits from up to half a kilobyte of earlier frames, so
    /// the stream starts <see cref="PreRollFrames"/> frames early and the
    /// decoder throws that part away.
    ///
    /// **Which frame is sample zero is Media Foundation's decision**, and it is
    /// matched here rather than argued with: a first frame carrying a Xing or
    /// VBRI header is skipped by it and one carrying LAME's "Info" header is
    /// decoded as a frame of silence. Measured on a variable- and a
    /// constant-bitrate file made by the same encoder — the difference between
    /// them is exactly one frame.
    ///
    /// **And it drops a frame wherever the stream breaks** — after a tag in
    /// the middle of the file, a stretch of zeros or junk, anything the frame
    /// parser has to find its way past, the first frame found produces no
    /// output (its bit reservoir points back into bytes that were not audio).
    /// Measured on joined files: one frame's worth less at every such join.
    /// So the walk gives that frame no number, and indexes the one after it: a
    /// seek never starts on the dropped frame, and one that decodes across the
    /// join drops it just as a straight decode does.
    /// </summary>
    internal sealed class Mp3Seek : ExactSeek
    {
        private const int PreRollFrames = 10;
        private const int Stride = 8;
        private const int ChunkBytes = 1024 * 1024;

        /// <summary>How often a download is looked at for more to walk.</summary>
        private const int WaitForDownloadMilliseconds = 250;

        /// <summary>The longest frame there is: layer I at 448kbps and 32kHz, less a little.</summary>
        private const int MaxFrame = 2881;

        private readonly long _first;
        private readonly Header _format;
        private readonly bool _tagFrame;
        private readonly int _skipped;

        // What the walk has found so far. Read by seeks on another thread, so
        // behind the lock.
        private readonly object _gate = new();
        private readonly List<(long Frame, long Offset, bool AfterJoin)> _offsets = new();

        // AfterJoin is kept for anyone reading the index; walking forward from
        // an entry never crosses a join, because every join has an entry.
        private long _walked;
        private bool _complete;
        private int _started;

        private Mp3Seek(ByteSource bytes, long first, Header format, bool tagFrame, int skipped)
            : base(bytes)
        {
            _first = first;
            _format = format;
            _tagFrame = tagFrame;
            _skipped = skipped;
        }

        public static Mp3Seek? TryCreate(ByteSource bytes, out bool retry)
        {
            retry = false;

            // Past any ID3v2 tags at the front. A tag can be megabytes of cover
            // art, which is why its size is read rather than scanned over.
            long at = 0;
            var id3 = new byte[10];
            while (true)
            {
                if (!bytes.Holds(at, at + 10)) { retry = true; return null; }
                if (bytes.ReadHeld(at, id3) < 10) return null;
                if (id3[0] != 'I' || id3[1] != 'D' || id3[2] != '3') break;

                long size = ((id3[6] & 0x7F) << 21) | ((id3[7] & 0x7F) << 14) | ((id3[8] & 0x7F) << 7) | (id3[9] & 0x7F);
                at += 10 + size + ((id3[5] & 0x10) != 0 ? 10 : 0);
            }

            // The first frame: a header followed by another like it.
            var buffer = new byte[64 * 1024];
            if (!bytes.Holds(at, Math.Min(bytes.Length, at + buffer.Length))) { retry = true; return null; }
            int got = bytes.ReadHeld(at, buffer);

            for (int i = 0; i + 4 <= got; i++)
            {
                if (!Header.TryParse(buffer.AsSpan(i), out var header)) continue;
                int next = i + header.Length;
                if (next + 4 > got || !Header.TryParse(buffer.AsSpan(next), out var second) || !second.Matches(header))
                    continue;

                // Xing/Info sit after the side information; VBRI at a fixed
                // offset. Looked for anywhere in the frame's first bytes.
                int end = Math.Min(got, i + Math.Min(header.Length, 64));
                bool xing = Contains(buffer, i + 4, end, "Xing") || Contains(buffer, i + 4, end, "VBRI");
                bool info = Contains(buffer, i + 4, end, "Info");

                var seeker = new Mp3Seek(bytes, at + i, header, xing || info, xing ? 1 : 0);
                seeker.Start();
                return seeker;
            }

            return null;
        }

        private static bool Contains(byte[] data, int from, int to, string ascii)
        {
            for (int i = from; i + ascii.Length <= to; i++)
            {
                int k = 0;
                while (k < ascii.Length && data[i + k] == ascii[k]) k++;
                if (k == ascii.Length) return true;
            }
            return false;
        }

        /// <summary>
        /// Starts the frame walk. On a download it walks what has arrived and
        /// waits for more, so a seek anywhere already downloaded is exact long
        /// before the rest is there.
        /// </summary>
        private void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;

            // A thread of its own rather than the pool, because on a download it
            // spends most of its life waiting.
            new Thread(() =>
            {
                try { Walk(); }
                catch { }
            })
            {
                IsBackground = true,
                Name = "ExplorerNative MP3 frame walk",
                Priority = ThreadPriority.BelowNormal,
            }.Start();
        }

        /// <summary>More than this left after the frames run out is not tags; see Walk.</summary>
        private const long AbandonedBytes = 1024 * 1024;

        private void Walk()
        {
            var chunk = new byte[ChunkBytes];
            long chunkAt = -1;
            int chunkLength = 0;
            long at = _first, frames = 0;
            bool broke = false, dropped = false, abandoned = false;

            while (at + 4 <= Bytes.Length && !Closed)
            {
                // The whole of the next frame has to be here before it is
                // counted, or a frame cut off by the end of the download would
                // read as lost sync.
                long need = Math.Min(Bytes.Length, at + MaxFrame + 4);
                if (Bytes.HeldEnd < need)
                {
                    Thread.Sleep(WaitForDownloadMilliseconds);
                    chunkAt = -1;
                    continue;
                }

                if (chunkAt < 0 || at < chunkAt || need > chunkAt + chunkLength)
                {
                    chunkAt = at;
                    long held = Bytes.HeldEnd - at;
                    if (held < 4) { chunkAt = -1; Thread.Sleep(WaitForDownloadMilliseconds); continue; }
                    chunkLength = Bytes.ReadHeld(at, chunk.AsSpan(0, (int)Math.Min(chunk.Length, held)));

                    // Short, with the bytes said to be held: the download was let
                    // go between the two questions. Waited out, not taken as the
                    // end — a walk stopped here was marked complete, and the
                    // length and every later seek believed a partial count.
                    if (chunkLength < 4)
                    {
                        // Only a download can be let go and come back; a local
                        // file that reads short is finished, or broken.
                        if (Bytes.HeldEnd >= Bytes.Length) break;
                        chunkAt = -1;
                        Thread.Sleep(WaitForDownloadMilliseconds);
                        continue;
                    }
                }

                var here = chunk.AsSpan((int)(at - chunkAt), chunkLength - (int)(at - chunkAt));
                if (!Header.TryParse(here, out var header) || !header.Matches(_format))
                {
                    // A tag in the middle — joined audiobook chapters carry one
                    // each, often with a picture — is stepped over by its size.
                    if (here.Length >= 10 && here[0] == 'I' && here[1] == 'D' && here[2] == '3')
                    {
                        long size = ((here[6] & 0x7F) << 21) | ((here[7] & 0x7F) << 14) |
                                    ((here[8] & 0x7F) << 7) | (here[9] & 0x7F);
                        at += 10 + size + ((here[5] & 0x10) != 0 ? 10 : 0);
                        chunkAt = -1;
                        broke = frames > 0;
                        continue;
                    }

                    // Lost sync — damage, or the tags at the end. Find where it
                    // resumes, however far that is: a 64KB look used to be all
                    // there was, and anything longer ended the walk and called
                    // it complete, so every later seek decoded its way from the
                    // last frame found.
                    long found = Resync(at + 1, out bool waiting);
                    if (found < 0)
                    {
                        // Nothing more that matches the first frame's format. At the
                        // tail that is the tags; with most of the file still to go it
                        // is a part at another sample rate, and a length counted up
                        // to here would be the length of the first part only.
                        if (!waiting)
                        {
                            abandoned = Bytes.Length - at > AbandonedBytes;
                            break;
                        }
                        Thread.Sleep(WaitForDownloadMilliseconds);
                        continue;
                    }
                    at = found;
                    chunkAt = -1;
                    broke = frames > 0;
                    continue;
                }

                lock (_gate)
                {
                    // The first frame after a join is the one Media Foundation
                    // drops: no number, and the next frame is indexed.
                    if (broke)
                    {
                        broke = false;
                        dropped = true;
                    }
                    else
                    {
                        if (dropped || frames % Stride == 0) _offsets.Add((frames, at, dropped));
                        dropped = false;
                        frames++;
                        _walked = frames;
                    }
                }

                at += header.Length;
            }

            lock (_gate) _complete = !Closed && !abandoned;
        }

        /// <summary>
        /// The next place three matching frame headers follow one another, at
        /// or after <paramref name="from"/>, or -1. <paramref name="waiting"/>
        /// says the search stopped at the end of what has been downloaded rather
        /// than at the end of the file.
        /// </summary>
        private long Resync(long from, out bool waiting)
        {
            waiting = false;
            var window = new byte[256 * 1024];
            long at = from;

            while (!Closed)
            {
                long held = Math.Min(Bytes.HeldEnd, Bytes.Length);
                if (at + 4 > held)
                {
                    waiting = held < Bytes.Length;
                    return -1;
                }

                int got = Bytes.ReadHeld(at, window.AsSpan(0, (int)Math.Min(window.Length, held - at)));

                // Held a moment ago and not now: released, not finished.
                if (got < 4) { waiting = Bytes.HeldEnd < Bytes.Length; return -1; }

                // Candidates whose following frames lie past this window are
                // left for the next one, which starts at the first of them.
                int usable = at + got >= held ? got : got - 3 * MaxFrame;
                for (int i = 0; i + 4 <= got && i < usable; i++)
                {
                    if (!Header.TryParse(window.AsSpan(i), out var one) || !one.Matches(_format)) continue;

                    int second = i + one.Length;
                    if (second + 4 > got) { if (at + got < held) break; continue; }
                    if (!Header.TryParse(window.AsSpan(second), out var two) || !two.Matches(_format)) continue;

                    int third = second + two.Length;
                    if (third + 4 > got) { if (at + got < held) break; return at + i; }
                    if (Header.TryParse(window.AsSpan(third), out var three) && three.Matches(_format))
                        return at + i;
                }

                if (at + got >= held) { waiting = held < Bytes.Length; return -1; }
                at += Math.Max(1, usable);
            }

            return -1;
        }

        public override double? Length
        {
            get
            {
                // Less Layer III's 529-sample decoder delay, which Media
                // Foundation's output is short of at the end: measured on three
                // files, the frame count came out exactly that much long.
                lock (_gate)
                    return _complete && _walked > _skipped
                        ? ((_walked - _skipped) * (long)_format.Samples - (_format.Layer == 3 ? 529 : 0)) / (double)_format.Rate
                        : null;
            }
        }

        public override bool TryLocate(double seconds, int outputRate, out IStream stream, out double start)
        {
            stream = null!;
            start = 0;

            int perFrame = _format.Samples;
            int rate = _format.Rate;

            // Frames counted from the first one after the ID3 tags, the tag
            // frame included. Media Foundation's sample zero is the first frame
            // it decodes.
            long target = (long)Math.Floor(Math.Max(0, seconds) * rate / perFrame) + _skipped;
            long firstAudio = _tagFrame ? 1 : 0;
            long from = target - PreRollFrames;

            long offset, indexedFrame;
            lock (_gate)
            {
                // Past what has been walked: only a finished walk can say that
                // is past the end, and then the last frame will do.
                if (target >= _walked)
                {
                    // Past the end of a finished walk is only believable a few
                    // frames past it — the last frame's own samples. Further than
                    // that, the walk stopped short of the audio, and Media
                    // Foundation's seek is the better guess.
                    if (!_complete || _walked == 0 || target > _walked + PreRollFrames) return false;
                    from = Math.Min(from, _walked - 1);
                }

                // Back to a frame whose first sample is on the output's grid too.
                long gridStep = GridStep(rate, outputRate);
                long step = gridStep / Gcd(gridStep, perFrame);
                from = _skipped + (long)Math.Floor((from - _skipped) / (double)step) * step;
                if (from < firstAudio)
                {
                    // Before the first frame on the grid: the first audio frame
                    // itself, which is sample zero unless an Info frame came first.
                    from = firstAudio;
                    if ((from - _skipped) * perFrame % gridStep != 0) return false;
                }

                // The last entry at or before it. Entries are in frame order.
                int lo = 0, hi = _offsets.Count - 1, found = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    if (_offsets[mid].Frame <= from) { found = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                if (found < 0) return false;

                (indexedFrame, offset, _) = _offsets[found];
            }

            // From the nearest indexed frame, walking the few in between — never
            // across a join, since every join has an entry of its own.
            var header = new byte[4];
            for (long frame = indexedFrame; frame < from; frame++)
            {
                if (Bytes.ReadHeld(offset, header) < 4 || !Header.TryParse(header, out var parsed)) return false;
                offset += parsed.Length;
            }

            stream = new VirtualStream(Array.Empty<byte>(), Bytes, offset);
            start = (from - _skipped) * (double)perFrame / rate;
            return true;
        }

        /// <summary>An MPEG audio frame header.</summary>
        private readonly struct Header
        {
            public readonly int Version;    // 3 = MPEG-1, 2 = MPEG-2, 0 = MPEG-2.5
            public readonly int Layer;      // 1, 2 or 3
            public readonly int Rate;
            public readonly int Length;
            public readonly int Samples;

            private Header(int version, int layer, int rate, int length, int samples)
            {
                Version = version;
                Layer = layer;
                Rate = rate;
                Length = length;
                Samples = samples;
            }

            public bool Matches(in Header other) =>
                Version == other.Version && Layer == other.Layer && Rate == other.Rate;

            private static readonly int[,] Bitrates =
            {
                // MPEG-1: layer I, II, III
                { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 },
                { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },
                { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 },
                // MPEG-2 and 2.5: layer I, then II and III
                { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 },
                { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
            };

            public static bool TryParse(ReadOnlySpan<byte> h, out Header header)
            {
                header = default;
                if (h.Length < 4 || h[0] != 0xFF || (h[1] & 0xE0) != 0xE0) return false;

                int version = (h[1] >> 3) & 3;
                int layerBits = (h[1] >> 1) & 3;
                int bitrateIndex = h[2] >> 4;
                int rateIndex = (h[2] >> 2) & 3;
                if (version == 1 || layerBits == 0 || bitrateIndex == 0 || bitrateIndex == 15 || rateIndex == 3)
                    return false;

                int layer = 4 - layerBits;
                int rate = (rateIndex switch { 0 => 44100, 1 => 48000, _ => 32000 }) >> (version == 3 ? 0 : version == 2 ? 1 : 2);
                int padding = (h[2] >> 1) & 1;

                int row = version == 3 ? layer - 1 : layer == 1 ? 3 : 4;
                int kbps = Bitrates[row, bitrateIndex];

                int length = layer == 1
                    ? (12000 * kbps / rate + padding) * 4
                    : (version == 3 ? 144000 : 72000) * kbps / rate + padding;
                if (layer == 2 && version != 3) length = 144000 * kbps / rate + padding;

                int samples = layer == 1 ? 384 : layer == 2 ? 1152 : version == 3 ? 1152 : 576;
                if (length < 4) return false;

                header = new Header(version, layer, rate, length, samples);
                return true;
            }
        }
    }
}
