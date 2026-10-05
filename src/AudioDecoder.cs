using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// A file turned into float samples, through Media Foundation's source reader.
    ///
    /// The decoding is still Windows' — that was never the part worth replacing.
    /// MP3, AAC, WMA, WAV, ALAC and FLAC all decode here for nothing, which is the
    /// reason this application chose Media Foundation in the first place and the
    /// reason it keeps choosing it. What changes is what happens next: the reader
    /// hands back PCM instead of playing it, so the samples pass through our
    /// hands on the way to the device.
    ///
    /// The reader is also asked to produce the *device's* rate and channel count,
    /// so Windows' own resampler does the conversion once, correctly, inside the
    /// decode — rather than the mixer doing it afterwards to whatever came out.
    /// </summary>
    internal sealed class AudioDecoder : ITrackDecoder
    {
        [ComImport, Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSourceReader
        {
            [PreserveSig] int GetStreamSelection(uint streamIndex, out int selected);
            [PreserveSig] int SetStreamSelection(uint streamIndex, int selected);
            [PreserveSig] int GetNativeMediaType(uint streamIndex, uint typeIndex, out IntPtr type);
            [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IntPtr type);
            [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IntPtr type);
            [PreserveSig] int SetCurrentPosition(ref Guid format, ref PropVariant position);
            [PreserveSig] int ReadSample(uint streamIndex, uint flags, out uint actualStreamIndex,
                out uint streamFlags, out long timestamp, out IntPtr sample);
            [PreserveSig] int Flush(uint streamIndex);
            [PreserveSig] int GetServiceForStream(uint streamIndex, ref Guid service, ref Guid iid,
                [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int GetPresentationAttribute(uint streamIndex, ref Guid attribute,
                out PropVariant value);
        }

        [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            // IMFAttributes, thirty of them, then IMFSample's own. Written out
            // rather than inherited: position is what matters and a [ComImport]
            // base is not laid out first.
            void R00(); void R01(); void R02(); void R03(); void R04();
            void R05(); void R06(); void R07(); void R08(); void R09();
            void R10(); void R11(); void R12(); void R13(); void R14();
            void R15(); void R16(); void R17(); void R18(); void R19();
            void R20(); void R21(); void R22(); void R23(); void R24();
            void R25(); void R26(); void R27(); void R28(); void R29();

            [PreserveSig] int GetSampleFlags(out uint flags);
            [PreserveSig] int SetSampleFlags(uint flags);
            [PreserveSig] int GetSampleTime(out long time);
            [PreserveSig] int SetSampleTime(long time);
            [PreserveSig] int GetSampleDuration(out long duration);
            [PreserveSig] int SetSampleDuration(long duration);
            [PreserveSig] int GetBufferCount(out uint count);
            [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        }

        [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength(out uint length);
            [PreserveSig] int SetCurrentLength(uint length);
            [PreserveSig] int GetMaxLength(out uint length);
        }

        /// <summary>
        /// Enough of <c>IMFMediaType</c> to describe what we want back.
        ///
        /// The positions are the whole declaration, so they are counted out here
        /// rather than trusted. <c>IMFAttributes</c> is thirty methods and these
        /// are the ones that matter:
        ///
        /// <code>
        ///  0 GetItem          9 GetString          18 SetUINT32
        ///  1 GetItemType     10 GetAllocatedString 19 SetUINT64
        ///  2 CompareItem     11 GetBlobSize        20 SetDouble
        ///  3 Compare         12 GetBlob            21 SetGUID
        ///  4 GetUINT32       13 GetAllocatedBlob   22 SetString
        ///  5 GetUINT64       14 GetUnknown         ...
        ///  6 GetDouble       15 SetItem
        ///  7 GetGUID         16 DeleteItem
        ///  8 GetStringLength 17 DeleteAllItems
        /// </code>
        ///
        /// Getting this wrong is silent: <c>SetUINT32</c> one slot late lands on
        /// <c>SetUINT64</c>, which accepts the call, writes a differently sized
        /// value under the key, and leaves a media type that looks filled in and
        /// is refused with <c>MF_E_TOPO_CODEC_NOT_FOUND</c> — an error about
        /// codecs, for a mistake about arithmetic.
        /// </summary>
        [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType
        {
            void R00(); void R01(); void R02(); void R03();     // 0-3
            [PreserveSig] int GetUINT32(ref Guid key, out uint value);          // 4
            void R05(); void R06();                             // 5-6
            [PreserveSig] int GetGUID(ref Guid key, out Guid value);            // 7
            void R08(); void R09(); void R10(); void R11(); void R12();  // 8-12
            void R13(); void R14(); void R15(); void R16(); void R17();  // 13-17
            [PreserveSig] int SetUINT32(ref Guid key, uint value);              // 18
            void R19(); void R20();                             // 19-20
            [PreserveSig] int SetGUID(ref Guid key, ref Guid value);            // 21
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public long Long;
        }

        [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] private static extern int MFShutdown();
        [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IMFMediaType type);

        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
        private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes,
            out IMFSourceReader reader);

        /// <summary>
        /// Opaque on purpose. Nothing here calls a method on it — it is created
        /// from an IStream and handed straight to the source reader — so the
        /// vtable never matters, which is the one kind of COM declaration that
        /// cannot be got wrong.
        /// </summary>
        [ComImport, Guid("AD4C1B00-4BF7-422F-9175-756693D9130D"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFByteStream { }

        /// <summary>
        /// Wraps an IStream so Media Foundation can read a track through it, and
        /// does the asynchronous plumbing that implementing IMFByteStream by
        /// hand would otherwise mean.
        /// </summary>
        [DllImport("mfplat.dll")]
        private static extern int MFCreateMFByteStreamOnStream(
            [MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IStream stream,
            out IMFByteStream byteStream);

        [DllImport("mfreadwrite.dll")]
        private static extern int MFCreateSourceReaderFromByteStream(IMFByteStream stream,
            IntPtr attributes, out IMFSourceReader reader);

        private static Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
        private static Guid MFAudioFormat_Float = new("00000003-0000-0010-8000-00AA00389B71");
        private static Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
        private static Guid MF_MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
        private static Guid MF_MT_AUDIO_NUM_CHANNELS = new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
        private static Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA");
        private static Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");

        // Block alignment, average bytes per second and "all samples
        // independent" were here. They are all derivable from the rest, and
        // asking for them as well is what the source reader rejects as an
        // over-specified type — see the three-step negotiation in Open. They are
        // gone rather than commented out because a GUID sitting in a file is an
        // invitation to add it back to the media type.
        private static Guid MF_PD_DURATION = new("6C990D33-BB8E-477A-8598-0D5D96FCD88A");
        private static Guid GUID_NULL = Guid.Empty;

        private const uint FirstAudioStream = 0xFFFFFFFD;
        private const uint AllStreams = 0xFFFFFFFE;
        private const uint MediaSource = 0xFFFFFFFF;

        private const uint StreamFlagEndOfStream = 0x2;

        /// <summary>How far before a seek target Media Foundation is asked to start, in seconds.</summary>
        private const double SeekPreRoll = 0.25;

        /// <summary>
        /// The frame a seek in each compressed format lands on the start of, in
        /// the format's own samples. See <see cref="SeekRequest"/>. Anything not
        /// here is taken to land where it is asked, which uncompressed audio does.
        /// </summary>
        private static int FrameFor(Guid subtype, uint profile) => subtype.ToString().Substring(0, 8).ToUpperInvariant() switch
        {
            // AAC (raw and ADTS). High-efficiency AAC doubles the frame: its
            // profile-and-level values run from 0x2C to 0x33.
            "00001610" or "00001600" => profile >= 0x2C && profile <= 0x33 ? 2048 : 1024,
            // ALAC: the GUID Windows actually reports (616C6163-767A-494D-…),
            // and the "alac" FourCC form for anything that uses that instead.
            "616C6163" or "63616C61" => 4096,
            "00000055" or "00000050" => 1152,                   // MP3, MPEG-1 layer II
            "00002000" or "A7FB87AF" => 1536,                   // AC-3, E-AC-3
            _ => 1,
        };

        private static Guid MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION = new("7632F0E6-9538-4D61-ACDA-EA29C8C14456");
        private const int MF_VERSION = 0x00020070;

        private static int _startups;

        /// <summary>
        /// Whether *this* decoder is holding one of those startups. Without it
        /// the balance is kept by hoping every instance is opened exactly once
        /// and disposed exactly once, and neither is something a caller can be
        /// relied on for — a decoder disposed twice, or disposed having never
        /// been opened, each drives the count below what is really outstanding.
        /// </summary>
        private int _started;

        private IMFSourceReader? _reader;
        private readonly object _gate = new();

        /// <summary>
        /// A local file wrapped as a byte stream, when one was needed. Owned
        /// here, unlike the streaming source handed in from outside, so it has
        /// to be closed on the way out.
        /// </summary>
        private LocalByteStream? _local;

        /// <summary>An AIFF file presented as WAV, when that is what this is. Owned here too.</summary>
        private AiffStream? _aiff;

        /// <summary>A CD track presented as WAV, when that is what this is. Owned here too.</summary>
        private CdTrackStream? _cd;

        /// <summary>What the file's own reader reads, so it can be opened again after a virtual one.</summary>
        private System.Runtime.InteropServices.ComTypes.IStream? _opened;

        /// <summary>Left over from the last sample, when it did not fit.</summary>
        private float[] _spill = Array.Empty<float>();
        private int _spillOffset;
        private int _spillCount;

        private bool _finished;

        // What the reader was opened on and asked for, so that another can be
        // opened the same way — see SeekTo.
        private string _path = "";
        private System.Runtime.InteropServices.ComTypes.IStream? _external;
        private int _askedRate, _askedChannels;

        /// <summary>The file's own sample rate, before Windows converts it. Zero when it would not say.</summary>
        private int _nativeRate;

        /// <summary>The frame a seek lands on the start of, in the file's own samples. See <see cref="FrameFor"/>.</summary>
        private int _nativeFrame = 1;

        /// <summary>
        /// The FLAC or MP3 frame finder, for the formats whose seek Media
        /// Foundation stamps wrongly. See <see cref="ExactSeek"/>.
        /// </summary>
        private ExactSeek? _seeker;
        private bool _seekerSettled;

        /// <summary>True while the reader is decoding a <see cref="VirtualStream"/> rather than the file.</summary>
        private bool _virtual;

        /// <summary>
        /// The clock: the frame at the front of the spill is
        /// <c>_clockBase + _clockFrames / SampleRate</c> seconds in. Counted in
        /// frames rather than added up in seconds, because twenty hours of
        /// adding a small double drifts by a sample.
        /// </summary>
        private double _clockBase;
        private long _clockFrames;

        /// <summary>
        /// Everything before this is decoded and thrown away, so that a seek
        /// lands where it was asked to rather than where the format could start.
        /// Negative when there is nothing to throw away.
        /// </summary>
        private double _discardUntil = -1;

        /// <summary>True from a seek on the file's own reader until its first sample has said where it is.</summary>
        private bool _awaitingStamp;

        public int SampleRate { get; private set; }
        public int Channels { get; private set; }

        /// <summary>
        /// Length in seconds, or 0 when the source will not say. The file's own
        /// count when there is one — see <see cref="ExactSeek.Length"/> — and
        /// Media Foundation's otherwise.
        /// </summary>
        public double Duration
        {
            get => _seeker?.Length ?? _reportedDuration;
            private set => _reportedDuration = value;
        }

        private double _reportedDuration;

        /// <summary>Where the next frame handed out comes from, in seconds.</summary>
        public double Position
        {
            get
            {
                // Never waited for. A read holds the lock for as long as the
                // source takes, which on the Drive letter past the download is
                // hundreds of milliseconds — and this is asked on the window's
                // thread by every skip, pause and "what is playing". Busy, the
                // answer is the one published by the last read or seek.
                if (!Monitor.TryEnter(_gate)) return Volatile.Read(ref _lastPosition);
                try { return PublishPosition(); }
                finally { Monitor.Exit(_gate); }
            }
        }

        private double _lastPosition;

        /// <summary>The position, worked out and published. Under the lock.</summary>
        private double PublishPosition()
        {
            // A seek still to be caught up with is at its target already as
            // far as anybody asking is concerned: the next frame handed out
            // is the target's. Asked in between, this used to say where the
            // run-up began.
            double at = _discardUntil >= 0 ? _discardUntil
                : SampleRate > 0 ? _clockBase + _clockFrames / (double)SampleRate
                : _clockBase;
            Volatile.Write(ref _lastPosition, at);
            return at;
        }

        public string Diagnostic { get; private set; } = "not opened";

        /// <summary>
        /// Opens a file and asks for float at the given rate and channel count.
        ///
        /// The rate matters: giving the reader the *device's* rate makes Windows
        /// resample once, inside the decoder, with the source's full precision
        /// still available. Letting it come out at the file's rate and fixing it
        /// afterwards is a second conversion of something already converted.
        /// </summary>
        public bool Open(string path, int sampleRate, int channels) =>
            Open(path, null, sampleRate, channels);

        /// <summary>
        /// Asks the decoder for a different output format, without reopening the
        /// file.
        ///
        /// This is what makes moving to a device that mixes at another rate
        /// cheap. The source reader will re-negotiate its output type on demand,
        /// so there is no second open, no re-parse and no network read — the same
        /// reader simply starts producing 44100 instead of 48000. The spill from
        /// the old format is dropped, because those samples belong to a rate the
        /// device is no longer playing at.
        /// </summary>
        public bool Reopen(int sampleRate, int channels)
        {
            lock (_gate)
            {
                var reader = _reader;
                if (reader == null) return false;

                try
                {
                    // The clock is counted in frames of the rate being left, and
                    // the spill about to be dropped is audio the reader has
                    // already moved past. Both are folded into the base first, or
                    // a virtual stream's clock — which nothing re-stamps — comes out
                    // of a rate change reading minutes wrong.
                    int oldRate = Math.Max(1, SampleRate);
                    int oldChannels = Math.Max(1, Channels);
                    _clockBase += (_clockFrames + _spillCount / oldChannels) / (double)oldRate;
                    _clockFrames = 0;

                    _askedRate = sampleRate;
                    _askedChannels = channels;

                    int hr = TryType(reader, sampleRate, channels);
                    if (hr != 0) hr = TryType(reader, sampleRate, 0);
                    if (hr != 0) hr = TryType(reader, 0, 0);

                    if (hr != 0)
                    {
                        Diagnostic = $"the decoder will not change format (0x{hr:X8})";
                        return false;
                    }

                    if (!ReadBackFormat(reader)) return false;

                    _spillCount = 0;
                    _spillOffset = 0;
                    _finished = false;
                    return true;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// The same, reading through <paramref name="stream"/> rather than
        /// opening the path.
        ///
        /// That is how a track on a share is played out of memory instead of
        /// off the wire: <see cref="StreamingSource"/> downloads it in the
        /// background and serves each read from whichever copy answers fastest,
        /// which is what makes a skip cost a millisecond instead of half a
        /// second. The path is still wanted for the diagnostics.
        /// </summary>
        public bool Open(string path, System.Runtime.InteropServices.ComTypes.IStream? stream,
            int sampleRate, int channels)
        {
            try
            {
                EnsureStarted();

                _path = path;
                _external = stream;
                _askedRate = sampleRate;
                _askedChannels = channels;

                // A local file whose extension Windows has no handler for is
                // opened through a byte stream instead, so Media Foundation
                // identifies it by what is inside it.
                //
                // MFCreateSourceReaderFromURL picks a byte-stream handler by
                // *extension*, so a container it decodes perfectly well is
                // refused outright when the name does not match anything
                // registered. `.m4b` is exactly that: an audiobook is an
                // ordinary MP4 full of AAC, `.mp4` and `.m4a` and `.aac` all
                // have handlers, and `.m4b` has none — so the one extension
                // audiobooks actually use was the one that would not open,
                // while sharing every byte of its format with three that do.
                //
                // From a byte stream there is no name to go on and the resolver
                // sniffs the content, which is the answer for any extension in
                // this position rather than a special case for one of them.
                // A CD track, read off the disc as WAV. The .cda file itself is
                // a 44-byte pointer to the audio, not the audio.
                if (CdTrackStream.IsCdaName(Path.GetExtension(path)))
                {
                    _cd = CdTrackStream.TryOpen(path);
                    if (_cd == null) { Diagnostic = "decoder: the CD drive would not open"; return false; }
                }

                _local = _cd == null && stream == null && !AudioFiles.WindowsHasHandlerFor(Path.GetExtension(path))
                    ? LocalByteStream.TryOpen(path)
                    : null;

                // AIFF, which nothing in Windows reads, as the WAV it amounts to.
                // Tried by extension, and for anything Windows has no handler
                // for — the content decides, not the name.
                var extension = Path.GetExtension(path);
                if (IsAiffName(extension) || _local != null)
                    _aiff = AiffStream.TryOpen(path, stream);

                _opened = (System.Runtime.InteropServices.ComTypes.IStream?)_cd ?? _aiff ?? stream ?? _local;

                var reader = CreateReader(_opened, out string why);
                if (reader == null) { Diagnostic = why; return false; }
                _reader = reader;

                (_nativeRate, _nativeFrame) = NativeFormat(reader);

                // Duration, when the source knows it. A stream that does not say
                // is playable, it just cannot be scrubbed.
                if (reader.GetPresentationAttribute(MediaSource, ref MF_PD_DURATION, out var value) == 0)
                    Duration = value.Long / 10_000_000.0;

                _finished = false;
                _virtual = false;
                _clockBase = 0;
                _clockFrames = 0;
                _discardUntil = -1;
                _awaitingStamp = true;

                // Looked for now so that an MP3's frame walk is under way long
                // before anybody skips. Cheap when the format needs nothing: the
                // first ten bytes, and no.
                FindSeeker();

                Diagnostic = "ready";
                return true;
            }
            catch (Exception ex)
            {
                Diagnostic = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// A source reader on <paramref name="source"/>, or on the path when there
        /// is none, with only the first audio stream selected and float asked
        /// for in the shape last requested. Null, with the reason, when it will
        /// not open. <see cref="SampleRate"/> and <see cref="Channels"/> are
        /// what it settled on.
        /// </summary>
        private IMFSourceReader? CreateReader(System.Runtime.InteropServices.ComTypes.IStream? source,
            out string why)
        {
            int hr;
            IMFSourceReader? reader;

            if (source != null)
            {
                hr = MFCreateMFByteStreamOnStream(source, out var bytes);
                if (hr != 0 || bytes == null)
                {
                    why = $"cannot stream {_path} (0x{hr:X8})";
                    return null;
                }

                // The reader takes its own reference. Ours was left to the
                // garbage collector, which kept the stream underneath — a whole
                // download, for a Drive track — alive for however long that took.
                try { hr = MFCreateSourceReaderFromByteStream(bytes, IntPtr.Zero, out reader); }
                finally { try { Marshal.ReleaseComObject(bytes); } catch { } }
            }
            else
            {
                hr = MFCreateSourceReaderFromURL(_path, IntPtr.Zero, out reader);
            }

            if (hr != 0 || reader == null) { why = $"cannot read {_path} (0x{hr:X8})"; return null; }

            // Audio only, and only the first audio stream.
            reader.SetStreamSelection(AllStreams, 0);
            reader.SetStreamSelection(FirstAudioStream, 1);

            // Asked for in three goes, most specific first.
            //
            // The source reader refuses a media type it considers
            // over-specified, and it does it with MF_E_INVALIDMEDIATYPE
            // rather than by telling you which field it disliked. Block
            // alignment, average bytes and "all samples independent" are all
            // derivable from the rest, and asking for them as well is what
            // turned a perfectly ordinary WAV into 0xC00D5212. So: try the
            // full shape, then just the rate, then bare float — and believe
            // what comes back rather than what was asked for.
            hr = TryType(reader, _askedRate, _askedChannels);
            if (hr != 0) hr = TryType(reader, _askedRate, 0);
            if (hr != 0) hr = TryType(reader, 0, 0);

            if (hr != 0)
            {
                why = $"the decoder will not produce float (0x{hr:X8})";
                Release(reader);
                return null;
            }

            // What it actually settled on. Requesting 48000Hz stereo and
            // assuming you got it is how a mono file ends up played at half
            // speed through one channel.
            if (!ReadBackFormat(reader))
            {
                why = "the decoder would not say what it is producing";
                Release(reader);
                return null;
            }

            why = "";
            return reader;
        }

        public static bool IsAiffName(string? extension) =>
            string.Equals(extension, ".aif", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".aiff", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".aifc", StringComparison.OrdinalIgnoreCase);

        private static (int Rate, int Frame) NativeFormat(IMFSourceReader reader)
        {
            IntPtr typePtr = IntPtr.Zero;
            try
            {
                if (reader.GetNativeMediaType(FirstAudioStream, 0, out typePtr) != 0 || typePtr == IntPtr.Zero)
                    return (0, 1);

                var type = (IMFMediaType)Marshal.GetObjectForIUnknown(typePtr);
                try
                {
                    int rate = type.GetUINT32(ref MF_MT_AUDIO_SAMPLES_PER_SECOND, out uint r) == 0 ? (int)r : 0;
                    int frame = 1;
                    if (type.GetGUID(ref MF_MT_SUBTYPE, out Guid subtype) == 0)
                    {
                        type.GetUINT32(ref MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, out uint profile);
                        frame = FrameFor(subtype, profile);
                    }
                    return (rate, frame);
                }
                finally { try { Marshal.ReleaseComObject(type); } catch { } }
            }
            catch { return (0, 1); }
            finally { if (typePtr != IntPtr.Zero) Marshal.Release(typePtr); }
        }

        /// <summary>
        /// Where to ask Media Foundation to start for a seek to
        /// <paramref name="seconds"/>: a quarter of a second early, and when the
        /// rate is being converted, back to a sample that is on both rates'
        /// grids and on a frame boundary of the format.
        ///
        /// The grid matters because Windows' resampler lays its output grid from
        /// the first sample it is given; anywhere else and the audio after the
        /// seek is a fraction of a sample out from a straight decode. The frame
        /// boundary matters because AAC — the one format here that stamps its
        /// landing truthfully but cannot land anywhere but the start of a
        /// 1024-sample frame — lands on the frame the request falls in. Asking
        /// for a frame start that is also a grid point is how it ends up on one.
        /// Uncompressed audio lands exactly where it is asked, so the same
        /// request serves it. The cost is up to three and a half seconds of run-up
        /// at 44.1kHz, a few milliseconds of decoding.
        /// </summary>
        private double SeekRequest(double seconds)
        {
            double early = Math.Max(0, seconds - SeekPreRoll);
            if (_nativeRate <= 0 || SampleRate <= 0 || _nativeRate == SampleRate) return early;

            long grid = ExactSeek.GridStep(_nativeRate, SampleRate);
            long step = grid / ExactSeek.Gcd(grid, _nativeFrame) * _nativeFrame;
            long sample = (long)(early * _nativeRate) / step * step;
            double aligned = sample / (double)_nativeRate;

            // The run-up is read from the file, and on a download that has not
            // got that far every byte of it is a network read — up to fourteen
            // seconds of ALAC. A fraction of a sample is not worth that.
            if (_external is StreamingSource download && !download.Complete && seconds - aligned > 1.0)
                return early;

            return aligned;
        }

        private static void Release(IMFSourceReader? reader)
        {
            if (reader == null) return;
            try { Marshal.ReleaseComObject(reader); } catch { }
        }

        /// <summary>
        /// Offers one shape of float output. Zero for a field means "you choose".
        /// </summary>
        private static int TryType(IMFSourceReader reader, int sampleRate, int channels)
        {
            if (MFCreateMediaType(out var wanted) != 0 || wanted == null) return -1;

            try
            {
                wanted.SetGUID(ref MF_MT_MAJOR_TYPE, ref MFMediaType_Audio);
                wanted.SetGUID(ref MF_MT_SUBTYPE, ref MFAudioFormat_Float);
                wanted.SetUINT32(ref MF_MT_AUDIO_BITS_PER_SAMPLE, 32);
                if (sampleRate > 0) wanted.SetUINT32(ref MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)sampleRate);
                if (channels > 0) wanted.SetUINT32(ref MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);

                var typePtr = Marshal.GetIUnknownForObject(wanted);
                try { return reader.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, typePtr); }
                finally { Marshal.Release(typePtr); }
            }
            catch { return -1; }
            finally { try { Marshal.ReleaseComObject(wanted); } catch { } }
        }

        /// <summary>The rate and channel count the reader is really producing.</summary>
        private bool ReadBackFormat(IMFSourceReader reader)
        {
            IntPtr typePtr = IntPtr.Zero;
            try
            {
                if (reader.GetCurrentMediaType(FirstAudioStream, out typePtr) != 0 || typePtr == IntPtr.Zero)
                    return false;

                var type = (IMFMediaType)Marshal.GetObjectForIUnknown(typePtr);
                try
                {
                    if (type.GetUINT32(ref MF_MT_AUDIO_SAMPLES_PER_SECOND, out uint rate) != 0) return false;
                    if (type.GetUINT32(ref MF_MT_AUDIO_NUM_CHANNELS, out uint ch) != 0) return false;

                    SampleRate = (int)rate;
                    Channels = (int)ch;
                    return SampleRate > 0 && Channels > 0;
                }
                finally { try { Marshal.ReleaseComObject(type); } catch { } }
            }
            catch { return false; }
            finally { if (typePtr != IntPtr.Zero) Marshal.Release(typePtr); }
        }

        private void EnsureStarted()
        {
            // MFStartup is reference counted by Media Foundation itself, but
            // calling it once per decoder and never shutting down leaks the
            // platform. Counted here so the last one out turns the light off.
            //
            // Once per instance, and matched exactly once in Dispose. An
            // unbalanced MFShutdown does not fail and does not report anything:
            // it takes Media Foundation down for the whole process, and the
            // media engine still holding a track simply stops. Measured — a
            // speed key handed a track from here to the engine, this decoder was
            // disposed on the way past, and from that moment every track was
            // silent for the life of the process while the window went on saying
            // it was playing and the engine's clock sat frozen at the position
            // the last one had reached.
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            if (Interlocked.Increment(ref _startups) == 1) MFStartup(MF_VERSION, 0);
        }

        /// <summary>
        /// Fills <paramref name="destination"/> with interleaved float frames and
        /// returns how many it managed. Fewer than asked means the file is done.
        ///
        /// Called on the render thread. It reads from the source, which for a
        /// network file can block — the buffer ahead of it is what stops that
        /// being heard, exactly as the old streaming path did.
        /// </summary>
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
                var reader = _reader;
                if (reader == null) return 0;

                int values = frames * Channels;
                int written = 0;

                while (written < values)
                {
                    if (_spillCount > 0)
                    {
                        int take = Math.Min(_spillCount, values - written);
                        Array.Copy(_spill, _spillOffset, destination, written, take);
                        _spillOffset += take;
                        _spillCount -= take;
                        written += take;
                        _clockFrames += take / Channels;
                        continue;
                    }

                    if (_finished) break;
                    if (!ReadOneSample()) break;
                }

                return Channels > 0 ? written / Channels : 0;
            }
        }

        private bool ReadOneSample()
        {
            var reader = _reader;
            if (reader == null) return false;

            int hr = reader.ReadSample(FirstAudioStream, 0, out _, out uint flags,
                out long timestamp, out IntPtr samplePtr);

            if (hr != 0) { _finished = true; return false; }

            if ((flags & StreamFlagEndOfStream) != 0)
            {
                _finished = true;
                if (samplePtr == IntPtr.Zero) return false;
            }

            if (samplePtr == IntPtr.Zero) return !_finished;

            IMFSample? sample = null;
            IMFMediaBuffer? buffer = null;
            bool locked = false;

            try
            {
                sample = (IMFSample)Marshal.GetObjectForIUnknown(samplePtr);
                if (sample.ConvertToContiguousBuffer(out buffer) != 0 || buffer == null) return false;
                if (buffer.Lock(out IntPtr data, out _, out uint length) != 0) return false;
                locked = true;

                int count = (int)(length / sizeof(float));
                if (count <= 0) return true;

                if (_spill.Length < count) _spill = new float[count];
                Marshal.Copy(data, _spill, 0, count);
                _spillOffset = 0;
                _spillCount = count - count % Math.Max(1, Channels);

                // The first sample after an open or a seek says where the reader
                // is, and from there the clock counts frames. Only the first: the
                // later timestamps agree with the count for every format but one,
                // and that one — WMA, whose container keeps time to the
                // millisecond — moved the clock by up to 44 samples at every
                // sample, and a seek's run-up was thrown away against the moved
                // clock. Not on a virtual stream at all: there the reader counts
                // from wherever the stream begins, and where that is was worked
                // out before it was opened.
                //
                // Zero counts. Taken only when above zero, a seek that Media
                // Foundation answered from the very start kept the target as the
                // clock, threw nothing away, and played the track from the top.
                if (_awaitingStamp && !_virtual && timestamp >= 0)
                {
                    _clockBase = timestamp / 10_000_000.0;
                    _clockFrames = 0;
                }
                _awaitingStamp = false;

                if (_discardUntil >= 0) Discard();
                return true;
            }
            catch { return false; }
            finally
            {
                try { if (locked && buffer != null) buffer.Unlock(); } catch { }
                if (buffer != null) { try { Marshal.ReleaseComObject(buffer); } catch { } }
                if (sample != null) { try { Marshal.ReleaseComObject(sample); } catch { } }
                if (samplePtr != IntPtr.Zero) Marshal.Release(samplePtr);
            }
        }

        /// <summary>
        /// Drops the part of the sample in hand that comes before
        /// <see cref="_discardUntil"/>. The whole of it, if it ends there — the
        /// read loop then simply fetches the next.
        /// </summary>
        private void Discard()
        {
            int frames = _spillCount / Math.Max(1, Channels);

            // In whole samples, each side rounded on its own. The front is a
            // sample boundary that the timestamp's hundred-nanosecond steps blur;
            // the target is rounded the one way every decoder here rounds it. The
            // difference of the two in seconds, rounded once, came out a sample
            // off for any target halfway between two.
            long frontSample = (long)Math.Round(_clockBase * SampleRate) + _clockFrames;
            long targetSample = (long)Math.Round(_discardUntil * SampleRate);
            long drop = targetSample - frontSample;

            if (drop <= 0)
            {
                _discardUntil = -1;
                return;
            }

            if (drop >= frames)
            {
                _clockFrames += frames;
                _spillOffset += frames * Channels;
                _spillCount = 0;
                return;
            }

            _spillOffset += (int)drop * Channels;
            _spillCount -= (int)drop * Channels;

            // Arrived. The clock reads the target itself rather than the target
            // give or take the half-sample the rounding above allows.
            _clockBase = _discardUntil;
            _clockFrames = 0;
            _discardUntil = -1;
        }

        /// <summary>
        /// Moves to a position in seconds, to the sample. Anything buffered is
        /// dropped.
        ///
        /// Media Foundation's seek starts wherever the format lets it — the
        /// frame before, the seek point before, ten seconds before — and the
        /// part between there and the target is decoded and thrown away here.
        /// That relies on it stamping where it started, which FLAC and
        /// variable-bitrate MP3 do not do truthfully; for those,
        /// <see cref="ExactSeek"/> finds the frame and the reader is reopened
        /// on the file from there.
        /// </summary>
        public bool SeekTo(double seconds)
        {
            lock (_gate)
            {
                if (_reader == null) return false;
                seconds = Math.Max(0, seconds);

                // Published before the seek's own reading, which can be slow.
                Volatile.Write(ref _lastPosition, seconds);

                try
                {
                    // Whatever was already decoded belongs to where we were.
                    _spillCount = 0;
                    _spillOffset = 0;
                    _finished = false;

                    if (seconds > 0 && SeekExact(seconds)) return true;

                    double request = SeekRequest(seconds);

                    // Back on the file's own reader, if a seek had moved off it —
                    // and a fresh one for anything whose run-up starts at the very
                    // beginning. Windows' decoders do not all restart the same way
                    // after a seek to zero as they start on open: WMA came back 15
                    // samples later, so "back to the start" was not the start.
                    if (_virtual || request <= 0)
                    {
                        var own = CreateReader(_opened, out _);
                        if (own == null) return false;
                        Release(_reader);
                        _reader = own;
                        _virtual = false;
                    }

                    // A little early, and the difference thrown away: an AAC frame
                    // decoded without the one before it is wrong for its first ten
                    // milliseconds, and the resampler starts from silence.
                    // Rounded up to the next hundred nanoseconds, so that a request
                    // exactly on a frame start is not read as the frame before.
                    if (request > 0)
                    {
                        var position = new PropVariant
                        {
                            Type = 20,                                   // VT_I8
                            Long = (long)Math.Ceiling(request * 10_000_000.0),
                        };

                        int hr = _reader.SetCurrentPosition(ref GUID_NULL, ref position);
                        if (hr != 0) return false;
                    }

                    _clockBase = seconds;
                    _clockFrames = 0;
                    _discardUntil = seconds > 0 ? seconds : -1;
                    _awaitingStamp = true;
                    return true;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Reopens the reader on the file from the frame <see cref="ExactSeek"/>
        /// finds. False, with nothing changed, when there is no seeker or it
        /// cannot answer from what is in hand.
        /// </summary>
        private bool SeekExact(double seconds)
        {
            var seeker = FindSeeker();
            if (seeker == null) return false;

            if (!seeker.TryLocate(seconds, SampleRate, out var stream, out double start)) return false;

            var reader = CreateReader(stream, out _);
            if (reader == null) return false;

            Release(_reader);
            _reader = reader;
            _virtual = true;

            _clockBase = start;
            _clockFrames = 0;
            _discardUntil = seconds;
            return true;
        }

        private ExactSeek? FindSeeker()
        {
            if (_seeker != null || _seekerSettled) return _seeker;

            if (_aiff != null || _cd != null) { _seekerSettled = true; return null; }

            _seeker = ExactSeek.For(_path, _external, out bool retry);
            _seekerSettled = _seeker != null || !retry;
            return _seeker;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_reader != null)
                {
                    Release(_reader);
                    _reader = null;
                }

                // After the reader: a virtual stream it may still hold reads
                // through the seeker's file.
                try { _seeker?.Dispose(); } catch { }
                _seeker = null;
                _seekerSettled = true;

                // Ours to close. The streaming source handed in from outside is
                // the player's and outlives this.
                if (_aiff != null)
                {
                    try { _aiff.Dispose(); } catch { }
                    _aiff = null;
                }

                if (_local != null)
                {
                    try { _local.Dispose(); } catch { }
                    _local = null;
                }

                if (_cd != null)
                {
                    try { _cd.Dispose(); } catch { }
                    _cd = null;
                }
            }

            // Only if this instance really took one, and only the first time it
            // is disposed. See EnsureStarted for what the unbalanced version
            // costs — it is not a leak, it is the whole player going silent.
            if (Interlocked.Exchange(ref _started, 0) == 1 &&
                Interlocked.Decrement(ref _startups) == 0)
            {
                try { MFShutdown(); } catch { }
            }
        }
    }
}
