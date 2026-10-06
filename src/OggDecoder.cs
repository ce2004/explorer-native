using System;
using System.IO;
using System.Threading;
using Concentus;
using NVorbis;

namespace ExplorerNative
{
    /// <summary>
    /// Ogg Vorbis and Ogg Opus, decoded in managed code because Windows will not
    /// decode them for us.
    ///
    /// Both codecs are pure IL — Concentus is a managed port of libopus, NVorbis
    /// is a managed Vorbis decoder — so there is no native binary, nothing per
    /// architecture, and nothing that behaves differently on ARM64. Together
    /// they are about half a megabyte inside a 160MB executable.
    ///
    /// The work here is not the decoding, which those two do. It is everything
    /// around it: telling the two containers apart, matching the device's rate
    /// and channel count, and keeping a position that means the same thing as
    /// <see cref="AudioDecoder"/>'s — because <see cref="WasapiPlayback"/> talks
    /// to whichever of them opened the file and must not be able to tell.
    /// </summary>
    internal sealed class OggDecoder : ITrackDecoder
    {
        /// <summary>
        /// Resampler quality, 0 to 10. Speex's own scale: 3 is telephony, 10 is
        /// mastering. Five is the middle and is inaudible against a Vorbis file
        /// that is already lossy — and this runs on the pump thread, where the
        /// budget is a network read, not arithmetic.
        /// </summary>
        private const int ResamplerQuality = 5;

        /// <summary>
        /// How hard the sniff tries to read the head of the file before giving up
        /// and answering "cannot tell".
        ///
        /// Twenty attempts fifty milliseconds apart — one second at the outside,
        /// against the fifteen the whole open is given. The head of a file is the
        /// first thing any download fetches and the part
        /// <see cref="AudioPrefetch"/> has already warmed, so this is waiting for
        /// something that is on its way rather than hoping. Getting it wrong the
        /// other way costs a fifteen-second timeout and a track that will not
        /// play at all — see <see cref="Sniff"/>.
        /// </summary>
        private const int SniffAttempts = 20;
        private const int SniffWaitMilliseconds = 50;

        private readonly object _gate = new();

        private Stream? _source;
        private StreamDecoder? _vorbis;

        /// <summary>Where a Vorbis stream ends, in its own samples; zero when unknown. See <see cref="DecodeSome"/>.</summary>
        private long _vorbisEnd;
        private OggOpusReader? _opus;
        private IResampler? _resampler;

        /// <summary>What the file is, before any conversion.</summary>
        private int _sourceRate, _sourceChannels;

        /// <summary>Decoded at the source rate, waiting to be converted.</summary>
        private float[] _decoded = Array.Empty<float>();
        private int _decodedFrames;

        /// <summary>Converted to the output format, waiting to be handed over.</summary>
        private float[] _ready = Array.Empty<float>();
        private int _readyFrames, _readyTaken;

        /// <summary>Resampled at the output rate, still in the source's channels.</summary>
        private float[] _resampled = Array.Empty<float>();

        private bool _finished;
        private long _emitted;
        private double _positionBase;

        /// <summary>
        /// Output frames still to be thrown away after a seek — the run-up the
        /// resampler is given so that what is heard starts clean. See
        /// <see cref="SeekTo"/>.
        /// </summary>
        private long _discard;

        /// <summary>How much run-up the resampler gets after a seek, in seconds.</summary>
        private const double ResamplerRunUp = 0.05;

        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public double Duration { get; private set; }
        public string Diagnostic { get; private set; } = "not opened";

        public double Position
        {
            get
            {
                // Not waited for, for the reason AudioDecoder.Position gives: a
                // read holds the lock across the source, and this is asked on
                // the window's thread.
                if (!Monitor.TryEnter(_gate)) return Volatile.Read(ref _lastPosition);
                try { return PublishPosition(); }
                finally { Monitor.Exit(_gate); }
            }
        }

        private double _lastPosition;

        private double PublishPosition()
        {
            // A run-up still to be thrown away is counted as if it already had
            // been: the next frame handed out is the target, and asked in
            // between, the clock said the run-up's start instead.
            double at = SampleRate > 0 ? _positionBase + (_emitted + _discard) / (double)SampleRate : 0;
            Volatile.Write(ref _lastPosition, at);
            return at;
        }

        /// <summary>The extensions this decoder is worth trying for.</summary>
        public static bool Handles(string? extension)
        {
            if (string.IsNullOrEmpty(extension)) return false;

            foreach (var known in new[] { ".ogg", ".oga", ".opus" })
                if (string.Equals(known, extension, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        public bool Open(string path, System.Runtime.InteropServices.ComTypes.IStream? stream,
            int sampleRate, int channels)
        {
            try
            {
                // The same stream the rest of the player reads through, when there
                // is one: a track on the Drive letter or a share is served out of
                // StreamingSource's download, so an Ogg file gets the same instant
                // skipping as everything else rather than a fresh network read per
                // page.
                _source = stream != null
                    ? new ComStream(stream)
                    : new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                        FileOptions.SequentialScan);

                if (_source.CanSeek) _source.Position = 0;

                // A positive identification is trusted and not second-guessed.
                // What it read is the format's own statement about itself, so a
                // stream that says Vorbis and will not open as Vorbis is a broken
                // file — and handing it on to the Opus reader could only produce
                // a second failure, since that reader wants an OpusHead first.
                //
                // Unknown keeps the order this has always had, so a sniff that
                // could not read anything is never worse than no sniff at all.
                // That is the lesson of the second fault recorded on Sniff, and
                // it is why there are three answers rather than two.
                bool opened = Sniff() switch
                {
                    Container.Vorbis => OpenVorbis(),
                    Container.Opus => OpenOpus(),
                    _ => OpenOpus() || OpenVorbis(),
                };

                if (!opened)
                {
                    Diagnostic = "not an Ogg Vorbis or Opus stream";
                    return false;
                }

                if (_sourceRate <= 0 || _sourceChannels <= 0)
                {
                    Diagnostic = "the Ogg stream did not say its format";
                    return false;
                }

                SetOutputFormat(sampleRate, channels);

                _finished = false;
                _emitted = 0;
                _positionBase = 0;
                Diagnostic = "ready";
                return true;
            }
            catch (Exception ex)
            {
                Diagnostic = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>Which codec the container says it holds, when it says.</summary>
        private enum Container
        {
            /// <summary>Nothing readable said either way. Decides nothing.</summary>
            Unknown,
            Opus,
            Vorbis,
        }

        /// <summary>
        /// Reads the first page and asks what the first packet begins with: the
        /// eight bytes "OpusHead", which RFC 7845 puts at the front of every Opus
        /// stream, or <c>0x01 "vorbis"</c>, which the Vorbis specification puts at
        /// the front of every Vorbis one.
        ///
        /// **It used to be `HasNextPacket`, and that was wrong.** The comment
        /// that stood here said that flag "is false when the container was not
        /// Opus at all, which is how this doubles as the sniff". Measured against
        /// Concentus 2.2.2 with a page carrying a Vorbis identification header:
        /// the reader constructs, `HasNextPacket` is **true**, and
        /// `DecodeNextPacket` returns null — so every Vorbis file was claimed by
        /// the Opus reader and decoded nothing.
        ///
        /// **And then the first fix was wrong in the other direction, which is
        /// the part worth keeping.** It read the page with `ReadExactly` and
        /// treated a failure as "not Opus". On the Drive letter the stream is a
        /// download: <see cref="StreamingSource.Read"/> returns *no bytes* when it
        /// has not reached that offset yet, `ReadExactly` turns that into an
        /// exception, and a genuine Opus file was therefore declared Vorbis and
        /// handed to NVorbis — which walked a large remote container looking for
        /// a Vorbis stream that was not there until the fifteen-second open
        /// budget ran out. "Could not play … it did not open in time."
        ///
        /// So there are three answers and not two, and **an unknown is never a
        /// no**. A head that will not read is waited for — it is the first thing
        /// any download fetches, so nothing is being asked for that is not on its
        /// way — and if it still will not come, the answer is Unknown and the
        /// order falls back to what it always was.
        /// </summary>
        private Container Sniff()
        {
            var source = _source;
            if (source == null || !source.CanSeek) return Container.Unknown;

            try
            {
                for (int attempt = 0; attempt < SniffAttempts; attempt++)
                {
                    source.Position = 0;

                    // 27 bytes of page header, then up to 255 lacing values, then
                    // the packet. Enough for the worst case rather than the usual
                    // one, because a short read here is the whole bug above.
                    var head = new byte[27 + 255 + 8];
                    int got = ReadWhatThereIs(source, head);

                    if (got >= 27 &&
                        head[0] == (byte)'O' && head[1] == (byte)'g' &&
                        head[2] == (byte)'g' && head[3] == (byte)'S')
                    {
                        int at = 27 + head[26];

                        if (got >= at + 8)
                        {
                            if (Matches(head, at, "OpusHead")) return Container.Opus;
                            if (head[at] == 1 && Matches(head, at + 1, "vorbis")) return Container.Vorbis;
                            return Container.Unknown;
                        }
                    }
                    else if (got >= 4)
                    {
                        // Readable, and not an Ogg at all. Neither reader is going
                        // to make anything of it, and saying so costs no waiting.
                        return Container.Unknown;
                    }

                    Thread.Sleep(SniffWaitMilliseconds);
                }
            }
            catch { }
            finally
            {
                try { source.Position = 0; } catch { }
            }

            return Container.Unknown;
        }

        /// <summary>
        /// Fills as much of <paramref name="into"/> as the stream will give right
        /// now. Zero is "nothing yet", not "end of file" — which is exactly the
        /// distinction the first version of the sniff got wrong.
        /// </summary>
        private static int ReadWhatThereIs(Stream source, byte[] into)
        {
            int got = 0;

            while (got < into.Length)
            {
                int read = source.Read(into, got, into.Length - got);
                if (read <= 0) break;
                got += read;
            }

            return got;
        }

        private static bool Matches(byte[] data, int at, string ascii)
        {
            if (at + ascii.Length > data.Length) return false;

            for (int i = 0; i < ascii.Length; i++)
                if (data[at + i] != (byte)ascii[i]) return false;

            return true;
        }

        /// <summary>
        /// Opus first, because telling it apart is cheap and certain: the first
        /// page of an Opus stream carries an "OpusHead" identification header.
        /// Vorbis is then whatever is left that NVorbis will accept.
        ///
        /// Read by <see cref="OggOpusReader"/>, which reads the head and the tail
        /// of the file to open it and nothing in between — see there for what
        /// the library reader did instead on a twenty-hour recording.
        /// </summary>
        private bool OpenOpus()
        {
            if (_source == null) return false;

            try
            {
                // Stereo at 48k, which is what the reader hands back interleaved
                // whatever the file holds; Concentus does the mixing.
                var reader = OggOpusReader.Open(_source);
                if (reader == null) return false;

                _opus = reader;
                _sourceRate = OggOpusReader.Rate;
                _sourceChannels = reader.Channels;
                Duration = reader.Duration;
                return true;
            }
            catch
            {
                CloseOpus();
                return false;
            }
        }

        private void CloseOpus()
        {
            try { _opus?.Dispose(); } catch { }
            _opus = null;
        }

        private bool OpenVorbis()
        {
            if (_source == null) return false;

            try
            {
                // NVorbis decodes; the container is read by OggVorbisPackets,
                // which does not index the whole file to answer the length.
                var packets = OggVorbisPackets.Open(_source);
                if (packets == null) return false;

                var reader = new StreamDecoder(packets) { ClipSamples = true };
                if (reader.Channels <= 0 || reader.SampleRate <= 0) { reader.Dispose(); return false; }

                // One seek a sample in and back, so the packet provider learns
                // where the stream starts before the length is read — see
                // OggVorbisPackets.EnsureBase. Cheap: the first page, twice.
                try
                {
                    reader.SeekTo(1);
                    reader.SeekTo(0);
                }
                catch { }

                _vorbis = reader;
                _vorbisEnd = packets.GetGranuleCount();
                _sourceRate = reader.SampleRate;
                _sourceChannels = reader.Channels;
                Duration = reader.TotalTime.TotalSeconds;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Sets up the conversion from what the file is to what the device wants.
        ///
        /// A resampler only when the rates really differ — Opus is 48kHz and so
        /// is nearly every Windows mix format, so the common case builds nothing
        /// and converts nothing.
        /// </summary>
        private void SetOutputFormat(int sampleRate, int channels)
        {
            SampleRate = sampleRate > 0 ? sampleRate : _sourceRate;
            Channels = channels > 0 ? channels : _sourceChannels;

            try { _resampler?.Dispose(); } catch { }
            _resampler = null;

            if (SampleRate != _sourceRate)
                _resampler = ResamplerFactory.CreateResampler(
                    _sourceChannels, _sourceRate, SampleRate, ResamplerQuality, null);
        }

        public bool Reopen(int sampleRate, int channels)
        {
            lock (_gate)
            {
                if (_vorbis == null && _opus == null) return false;

                try
                {
                    // The clock and any run-up still to throw away are counted in
                    // frames of the rate being left. Folded into the base and
                    // rescaled first, or the position reads wrong from here on.
                    int oldRate = Math.Max(1, SampleRate);
                    _positionBase += (_emitted + (_readyFrames - _readyTaken)) / (double)oldRate;
                    _emitted = 0;

                    SetOutputFormat(sampleRate, channels);
                    _discard = _discard * Math.Max(1, SampleRate) / oldRate;

                    // What is buffered belongs to a format the device is no
                    // longer playing at.
                    _decodedFrames = 0;
                    _readyFrames = _readyTaken = 0;
                    return true;
                }
                catch { return false; }
            }
        }

        public int Read(float[] destination, int frames)
        {
            lock (_gate)
            {
                try { return ReadLocked(destination, frames); }
                finally { PublishPosition(); }
            }
        }

        private int ReadLocked(float[] destination, int frames)
        {
            {
                if (_vorbis == null && _opus == null) return 0;
                if (Channels <= 0) return 0;

                int written = 0;

                while (written < frames)
                {
                    if (_readyTaken < _readyFrames && _discard > 0)
                    {
                        int skip = (int)Math.Min(_discard, _readyFrames - _readyTaken);
                        _readyTaken += skip;
                        _discard -= skip;
                        _emitted += skip;
                        continue;
                    }

                    if (_readyTaken < _readyFrames)
                    {
                        int take = Math.Min(frames - written, _readyFrames - _readyTaken);

                        Array.Copy(_ready, _readyTaken * Channels,
                            destination, written * Channels, take * Channels);

                        _readyTaken += take;
                        written += take;
                        continue;
                    }

                    if (_finished) break;
                    if (!Produce()) break;
                }

                _emitted += written;
                return written;
            }
        }

        /// <summary>
        /// Decodes one block and converts it, or says the file is finished.
        /// </summary>
        private bool Produce()
        {
            _readyFrames = _readyTaken = 0;

            int got = DecodeSome();
            if (got <= 0) { _finished = true; return false; }

            // Rate, then channels. Resampling first means the resampler works in
            // the file's own channel count, which is the layout it was built for
            // — and it means a mono file is resampled once rather than twice
            // after being duplicated into stereo.
            var atRate = Resample(_decoded, got, out int rateFrames);
            if (rateFrames <= 0) return true;

            int need = rateFrames * Channels;
            if (_ready.Length < need) _ready = new float[need];

            MapChannels(atRate, rateFrames, _ready);
            _readyFrames = rateFrames;
            return true;
        }

        /// <summary>One block of source-rate, source-channel frames.</summary>
        private int DecodeSome()
        {
            const int WantFrames = 4096;

            int need = WantFrames * _sourceChannels;
            if (_decoded.Length < need) _decoded = new float[need];

            if (_vorbis != null)
            {
                // A damaged packet makes NVorbis throw, and this runs on the pump
                // thread, which has nothing above it to catch: the application
                // exited. A packet that cannot be decoded ends the track instead.
                //
                // The end is trimmed here, to the last page's granule position,
                // rather than by NVorbis — see OggVorbisPackets.ReadPage for the
                // damaged granule that made its own trim spin for ever.
                long end = _vorbisEnd;
                long at;
                int floats;
                try
                {
                    at = _vorbis.SamplePosition;
                    if (end > 0 && at >= end) floats = 0;
                    else floats = _vorbis.Read(_decoded, 0, need);
                }
                catch { at = 0; floats = 0; }

                int got = floats / _sourceChannels;
                if (end > 0 && at + got > end) got = (int)Math.Max(0, end - at);

                _decodedFrames = got;
                return _decodedFrames;
            }

            if (_opus == null) return 0;

            // One packet at a time, and a packet is 2.5 to 120 milliseconds, so
            // several are gathered rather than making the ring wait for each.
            //
            // The reader's zero means the end and only the end: a packet it
            // decodes and throws away (pre-skip, pre-roll, a damaged one) it
            // passes over itself, so a zero here is never a stall to be counted.
            // This loop used to count them, because the library reader could
            // say it had a next packet and hand back nothing for ever.
            int frames = 0;

            while (frames < WantFrames)
            {
                // Grown rather than truncated: dropping the tail of a packet is a
                // gap in the music every time a block boundary lands mid-packet.
                int room = (frames + OggOpusReader.MaxPacketFrames) * _sourceChannels;
                if (_decoded.Length < room) Array.Resize(ref _decoded, room);

                int got;
                try { got = _opus.Read(_decoded, frames * _sourceChannels); }
                catch { break; }

                if (got <= 0) break;
                frames += got;
            }

            _decodedFrames = frames;
            return frames;
        }

        /// <summary>
        /// The block at the output rate, still in the source's channel count.
        /// Returns the source buffer itself when the rates already match, which
        /// is the ordinary case for Opus.
        /// </summary>
        private float[] Resample(float[] input, int frames, out int outFrames)
        {
            var resampler = _resampler;
            if (resampler == null) { outFrames = frames; return input; }

            // Generous: the ratio plus a margin for the filter's own delay.
            int capacity = (int)((long)frames * SampleRate / _sourceRate) + 64;
            int need = capacity * _sourceChannels;
            if (_resampled.Length < need) _resampled = new float[need];

            int inLength = frames;
            int outLength = capacity;

            resampler.ProcessInterleaved(
                input.AsSpan(0, frames * _sourceChannels), ref inLength,
                _resampled.AsSpan(0, need), ref outLength);

            outFrames = outLength;
            return _resampled;
        }

        /// <summary>
        /// From the file's channel count to the device's.
        ///
        /// Mono to stereo duplicates, which is what makes a mono recording sit in
        /// the middle rather than in one ear. More channels than the device has
        /// are folded down by averaging — crude against a proper downmix matrix,
        /// and the alternative is dropping the centre channel of a surround file,
        /// which is where the dialogue is.
        /// </summary>
        /// <summary>
        /// Surround to stereo, in the channel order Vorbis and Opus share, with
        /// the usual weights: centre and surrounds at -3dB into both sides, the
        /// LFE left out, and the sum scaled so a full-scale mix cannot clip.
        /// Copying the first two channels, which is what happened before, put a
        /// 5.1 file's front-left and centre in the two ears and lost the rest.
        /// </summary>
        private static void Downmix(float[] input, int at, int channels, float[] output, int into)
        {
            const float Half = 0.7071f;
            float l, r, c = 0, sl = 0, sr = 0;

            switch (channels)
            {
                case 3:     // L C R
                    l = input[at]; c = input[at + 1]; r = input[at + 2];
                    break;
                case 4:     // FL FR RL RR
                    l = input[at]; r = input[at + 1]; sl = input[at + 2]; sr = input[at + 3];
                    break;
                case 5:     // FL C FR RL RR
                case 6:     // FL C FR RL RR LFE
                    l = input[at]; c = input[at + 1]; r = input[at + 2]; sl = input[at + 3]; sr = input[at + 4];
                    break;
                case 7:     // FL C FR SL SR RC LFE
                    l = input[at]; c = input[at + 1]; r = input[at + 2];
                    sl = input[at + 3] + Half * input[at + 5];
                    sr = input[at + 4] + Half * input[at + 5];
                    break;
                default:    // 8: FL C FR SL SR RL RR LFE
                    l = input[at]; c = input[at + 1]; r = input[at + 2];
                    sl = input[at + 3] + input[at + 5];
                    sr = input[at + 4] + input[at + 6];
                    break;
            }

            float scale = channels == 3 ? 1f / (1 + Half)
                        : channels == 4 ? 1f / (1 + Half)
                        : channels >= 7 ? 1f / (1 + Half + 2 * Half)
                        : 1f / (1 + Half + Half);

            output[into] = (l + Half * c + Half * sl) * scale;
            output[into + 1] = (r + Half * c + Half * sr) * scale;
        }

        // For each Windows channel, the Vorbis/Opus channel that belongs there.
        // Windows: FL FR FC LFE BL BR (then SL SR for 7.1; BC SL SR for 6.1).
        private static readonly int[] ToWindows3 = { 0, 2, 1 };
        private static readonly int[] ToWindows5 = { 0, 2, 1, 3, 4 };
        private static readonly int[] ToWindows6 = { 0, 2, 1, 5, 3, 4 };
        private static readonly int[] ToWindows7 = { 0, 2, 1, 6, 5, 3, 4 };
        private static readonly int[] ToWindows8 = { 0, 2, 1, 7, 5, 6, 3, 4 };

        private void MapChannels(float[] input, int frames, float[] output)
        {
            int from = _sourceChannels, to = Channels;

            if (from == to)
            {
                // Same count, different order. Vorbis and Opus put the centre
                // second and the LFE last; a Windows device wants centre third and
                // LFE fourth. Copied straight across, a 5.1 film's dialogue came out
                // of the front right speaker.
                var order = from switch
                {
                    3 => ToWindows3,
                    5 => ToWindows5,
                    6 => ToWindows6,
                    7 => ToWindows7,
                    8 => ToWindows8,
                    _ => null,
                };

                if (order == null)
                {
                    Array.Copy(input, 0, output, 0, frames * to);
                    return;
                }

                for (int f = 0; f < frames; f++)
                {
                    int at = f * from;
                    for (int c = 0; c < to; c++) output[at + c] = input[at + order[c]];
                }
                return;
            }

            Span<float> speakers = stackalloc float[8];

            for (int f = 0; f < frames; f++)
            {
                int at = f * from, into = f * to;

                if (from == 1 && to <= 2)
                {
                    float value = input[at];
                    for (int c = 0; c < to; c++) output[into + c] = value;
                    continue;
                }

                if (to == 1)
                {
                    float sum = 0f;
                    for (int c = 0; c < from; c++) sum += input[at + c];
                    output[into] = sum / from;
                    continue;
                }

                if (to == 2 && from >= 3 && from <= 8)
                {
                    Downmix(input, at, from, output, into);
                    continue;
                }

                // Different counts, both above two: by speaker, not by position.
                // Copied across by position, a 5.1 file on a 7.1 device put the
                // centre in the front right and the LFE in a rear; and stereo on
                // a 5.1 device sent the full-range mix to the subwoofer.
                speakers.Clear();
                ToSpeakers(input, at, from, speakers);
                FromSpeakers(speakers, output, into, to);
            }
        }

        // The eight speakers, in Windows' order: FL FR FC LFE BL BR SL SR.
        private const int FL = 0, FR = 1, FC = 2, LF = 3, BL = 4, BR = 5, SL = 6, SR = 7;

        /// <summary>One frame in Vorbis/Opus order, placed on the eight speakers.</summary>
        private static void ToSpeakers(float[] input, int at, int channels, Span<float> s)
        {
            const float Half = 0.7071f;
            switch (channels)
            {
                case 1: s[FL] = s[FR] = input[at]; break;
                case 2: s[FL] = input[at]; s[FR] = input[at + 1]; break;
                case 3: s[FL] = input[at]; s[FC] = input[at + 1]; s[FR] = input[at + 2]; break;
                case 4:
                    s[FL] = input[at]; s[FR] = input[at + 1];
                    s[BL] = input[at + 2]; s[BR] = input[at + 3];
                    break;
                case 5:
                case 6:
                    s[FL] = input[at]; s[FC] = input[at + 1]; s[FR] = input[at + 2];
                    s[BL] = input[at + 3]; s[BR] = input[at + 4];
                    if (channels == 6) s[LF] = input[at + 5];
                    break;
                case 7:     // FL C FR SL SR RC LFE: the rear centre shared by both backs
                    s[FL] = input[at]; s[FC] = input[at + 1]; s[FR] = input[at + 2];
                    s[SL] = input[at + 3]; s[SR] = input[at + 4];
                    s[BL] = s[BR] = Half * input[at + 5];
                    s[LF] = input[at + 6];
                    break;
                default:    // FL C FR SL SR RL RR LFE, and anything past eight left out
                    s[FL] = input[at]; s[FC] = input[at + 1]; s[FR] = input[at + 2];
                    s[SL] = input[at + 3]; s[SR] = input[at + 4];
                    s[BL] = input[at + 5]; s[BR] = input[at + 6];
                    s[LF] = input[at + 7];
                    break;
            }
        }

        /// <summary>
        /// The eight speakers onto a device with <paramref name="channels"/>,
        /// three or more, in Windows' order. What the device has no speaker for
        /// is folded into the nearest one it has; the LFE is only ever sent to
        /// an LFE.
        /// </summary>
        private static void FromSpeakers(Span<float> s, float[] output, int into, int channels)
        {
            const float Half = 0.7071f;
            switch (channels)
            {
                case 3:
                    output[into] = s[FL] + Half * (s[BL] + s[SL]);
                    output[into + 1] = s[FR] + Half * (s[BR] + s[SR]);
                    output[into + 2] = s[FC];
                    break;
                case 4:
                    output[into] = s[FL] + Half * s[FC];
                    output[into + 1] = s[FR] + Half * s[FC];
                    output[into + 2] = s[BL] + s[SL];
                    output[into + 3] = s[BR] + s[SR];
                    break;
                case 5:
                    output[into] = s[FL]; output[into + 1] = s[FR]; output[into + 2] = s[FC];
                    output[into + 3] = s[BL] + s[SL];
                    output[into + 4] = s[BR] + s[SR];
                    break;
                case 6:
                    output[into] = s[FL]; output[into + 1] = s[FR]; output[into + 2] = s[FC];
                    output[into + 3] = s[LF];
                    output[into + 4] = s[BL] + s[SL];
                    output[into + 5] = s[BR] + s[SR];
                    break;
                case 7:     // FL FR FC LFE BC SL SR
                    output[into] = s[FL]; output[into + 1] = s[FR]; output[into + 2] = s[FC];
                    output[into + 3] = s[LF];
                    output[into + 4] = 0f;
                    output[into + 5] = s[SL] + s[BL];
                    output[into + 6] = s[SR] + s[BR];
                    break;
                default:
                    for (int c = 0; c < channels; c++) output[into + c] = c < 8 ? s[c] : 0f;
                    break;
            }
        }

        /// <summary>
        /// Moves to <paramref name="seconds"/>, to the sample.
        ///
        /// Both readers land exactly. What needs care is the resampler, when
        /// there is one: emptied at a seek, it starts from silence, and it lays
        /// its output grid from the first sample it is given — so a seek that
        /// hands it the target directly plays the first few milliseconds wrong,
        /// and everything after a fraction of a sample out from a straight
        /// decode. So the reader starts a little early, on a sample that is on
        /// both rates' grids, and the run-up is thrown away. Measured against a
        /// straight decode of an hour of Vorbis at 48kHz: identical, where it
        /// differed by 0.45 for the first ten milliseconds before.
        /// </summary>
        public bool SeekTo(double seconds)
        {
            lock (_gate)
            {
                try
                {
                    double target = Math.Max(0, seconds);
                    Volatile.Write(ref _lastPosition, target);
                    long start = (long)Math.Round(target * _sourceRate);
                    long discard = 0;

                    if (_resampler != null && target > 0)
                    {
                        long step = ExactSeek.GridStep(_sourceRate, SampleRate);
                        long early = Math.Max(0, start - (long)(ResamplerRunUp * _sourceRate));
                        early = early / step * step;

                        long wanted = (long)Math.Round(target * SampleRate);
                        discard = Math.Max(0, wanted - early * SampleRate / _sourceRate);
                        start = early;
                    }

                    // To the sample, which NVorbis does given a packet source
                    // that keeps its side of the bargain — see
                    // OggVorbisPackets.SeekTo.
                    if (_vorbis != null)
                        _vorbis.SeekTo(start, SeekOrigin.Begin);
                    // Lands on the sample, zero included. Zero is worth saying:
                    // the library reader this replaced latched end of stream when
                    // asked for it, which silenced "back to the start", a held
                    // skip-backward and the repeat loop point.
                    else if (_opus != null)
                    {
                        if (!_opus.SeekTo(start / (double)_sourceRate)) return false;
                    }
                    else return false;

                    // Everything buffered belongs to where we were, and the
                    // resampler is holding the tail of it inside its filter.
                    _decodedFrames = 0;
                    _readyFrames = _readyTaken = 0;
                    //
                    // Built again rather than reset: ResetMem empties the filter
                    // but leaves the fractional phase where it was, so audio after
                    // a seek came out shifted from a straight decode for as long as
                    // it played.
                    if (_resampler != null) SetOutputFormat(SampleRate, Channels);

                    _finished = false;
                    _emitted = 0;
                    _discard = discard;
                    _positionBase = start / (double)_sourceRate;
                    return true;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Closes the file without waiting for the decoder's lock, for a pump
        /// that did not come back when told to stop. Its next read finds the
        /// stream closed, which <see cref="DecodeSome"/> takes as the end.
        /// </summary>
        public void ReleaseSource()
        {
            var source = _source;
            if (source is FileStream) { try { source.Dispose(); } catch { } }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                CloseOpus();

                try { _vorbis?.Dispose(); } catch { }
                _vorbis = null;

                try { _resampler?.Dispose(); } catch { }
                _resampler = null;

                try { _source?.Dispose(); } catch { }
                _source = null;
            }
        }
    }

    /// <summary>
    /// A COM <c>IStream</c> seen as an ordinary <see cref="Stream"/>.
    ///
    /// <see cref="StreamingSource"/> is an IStream because that is what Media
    /// Foundation wraps, and the managed decoders want the .NET one. Without
    /// this an Ogg file on the Drive letter or a share would be read straight
    /// off the wire a page at a time, instead of out of the download the rest of
    /// the player already benefits from.
    ///
    /// Read and Seek are the whole of what the decoders use.
    /// </summary>
    internal sealed class ComStream : Stream
    {
        private readonly System.Runtime.InteropServices.ComTypes.IStream _inner;
        private readonly long _length;
        private long _position;

        public ComStream(System.Runtime.InteropServices.ComTypes.IStream inner)
        {
            _inner = inner;

            try
            {
                _inner.Stat(out var stat, 1);   // STATFLAG_NONAME
                _length = stat.cbSize;
            }
            catch
            {
                _length = 0;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return 0;

            // IStream reads into the start of the array it is given, so an
            // offset means reading into a temporary. Nearly every caller passes
            // zero, and the ones that do not are reading small amounts.
            var into = offset == 0 ? buffer : new byte[count];

            IntPtr read = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(int));
            try
            {
                _inner.Read(into, count, read);
                int got = System.Runtime.InteropServices.Marshal.ReadInt32(read);
                if (got <= 0) return 0;

                if (offset != 0) Array.Copy(into, 0, buffer, offset, got);

                _position += got;
                return got;
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(read); }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            IntPtr landed = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(long));
            try
            {
                _inner.Seek(offset, (int)origin, landed);
                _position = System.Runtime.InteropServices.Marshal.ReadInt64(landed);
                return _position;
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(landed); }
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // Deliberately not disposing the IStream. It belongs to StreamingSource,
        // which is owned by the player and outlives any one decoder.
        protected override void Dispose(bool disposing) { }
    }
}
