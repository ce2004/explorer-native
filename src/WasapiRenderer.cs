using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// One playback device, as something to choose from a list.
    ///
    /// The id is what gets remembered and the name is what a person picks from.
    /// Not the other way round: names are neither unique nor stable — two
    /// identical headsets give the same string, and a driver update rewrites it.
    /// </summary>
    public readonly record struct AudioDevice(string Id, string Name);

    /// <summary>
    /// Audio out, through WASAPI, with the samples in our hands.
    ///
    /// The media engine renders for itself and offers exactly one hook into its
    /// audio path — an MFT — and on this machine the pipeline never pulls output
    /// back out of one, so the player is silent whenever the gain transform is
    /// inserted. That is the immediate reason this exists. The larger reason is
    /// that owning the render loop is the only way to reach any of the things
    /// that need a sample in hand: gain past full scale, a limiter instead of
    /// hard clipping, knowing when a peak clipped so it can be *said*, matching
    /// the file's rate to the device instead of letting Windows resample, and
    /// bypassing the system effects without taking the device exclusively.
    ///
    /// Shared mode always. Exclusive would take the endpoint away from
    /// everything else on the machine, which is not a trade a file manager gets
    /// to make on somebody's behalf.
    ///
    /// <para>
    /// It is also the only shape of this that can be tested. Media Foundation
    /// uses hardware offload here, which bypasses the software mixer entirely —
    /// a WASAPI loopback capture reads silence while the player is at full scale,
    /// and every audio test in the suite plays a generated *silent* WAV, so a
    /// render path that emitted nothing at all looked exactly like one that
    /// worked. What comes out of here can be counted.
    /// </para>
    /// </summary>
    internal sealed class WasapiRenderer : IDisposable
    {
        // ---- COM ------------------------------------------------------------

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
        }

        /// <summary>
        /// Told when the endpoints change. The property key arrives by value and
        /// is twenty bytes, which both ARM64 and x64 pass as a pointer to a
        /// copy, so it is declared as one.
        /// </summary>
        [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMNotificationClient
        {
            [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
            [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
            [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key);
        }

        [ComVisible(true)]
        private sealed class DefaultWatcher : IMMNotificationClient
        {
            public int OnDeviceStateChanged(string id, uint state) => 0;
            public int OnDeviceAdded(string id) => 0;
            public int OnDeviceRemoved(string id) => 0;
            public int OnPropertyValueChanged(string id, IntPtr key) => 0;

            public int OnDefaultDeviceChanged(int flow, int role, string? id)
            {
                // Only the render default for the role this opens with, and
                // never answered here: this is Windows' thread, and nothing that
                // opens a device may run on it.
                if (flow != RenderFlow || role != ConsoleRole) return 0;
                var changed = id ?? "";
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { _defaultChanged?.Invoke(changed); } catch { }
                });
                return 0;
            }
        }

        private static readonly object WatchGate = new();
        private static Action<string>? _defaultChanged;
        private static object? _watchEnumerator;
        private static DefaultWatcher? _watcher;

        /// <summary>
        /// The system's default output has changed to the endpoint named, on the
        /// thread pool.
        ///
        /// A stream opened on the default endpoint stays on that endpoint: only
        /// one opened through ActivateAudioInterfaceAsync is moved by Windows. So
        /// "follow the system default" followed it only when the old device went
        /// away, and headphones that became the default while the speakers stayed
        /// plugged in played nothing — the music stayed on the speakers.
        /// </summary>
        public static event Action<string>? DefaultDeviceChanged
        {
            add
            {
                EnsureWatching();
                lock (WatchGate) _defaultChanged += value;
            }
            remove
            {
                lock (WatchGate) _defaultChanged -= value;
            }
        }

        private static void EnsureWatching()
        {
            lock (WatchGate)
            {
                if (_watcher != null) return;
            }

            static void Register()
            {
                lock (WatchGate)
                {
                    if (_watcher != null) return;
                    var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                    var watcher = new DefaultWatcher();
                    if (enumerator.RegisterEndpointNotificationCallback(watcher) == 0)
                    {
                        _watchEnumerator = enumerator;
                        _watcher = watcher;
                    }
                }
            }

            // From a multithreaded apartment: from the window's thread the
            // callback would be marshalled through a message loop that is not
            // always pumping. Directly when already on one — a play runs on the
            // thread pool, and blocking a pool thread on a pool task is a wait
            // that grows with how busy the pool is.
            try
            {
                if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA) Register();
                else Task.Run(Register).Wait(TimeSpan.FromSeconds(5));
            }
            catch { }
        }

        /// <summary>The endpoint actually opened, whether it was named or the default.</summary>
        public string EndpointId { get; private set; } = "";

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int context, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }

        /// <summary>
        /// Only GetValue is called, and only for the friendly name. The rest are
        /// here to put it at the right offset.
        /// </summary>
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid FormatId;
            public uint PropertyId;
        }

        /// <summary>
        /// Enough of a PROPVARIANT to read a string out of one. The union is
        /// eight bytes in, and for VT_LPWSTR it holds the pointer.
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr Pointer;
        }

        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

        private const uint StorageRead = 0;
        private const uint DeviceStateActive = 1;
        private const ushort VtLpwstr = 31;

        /// <summary>PKEY_Device_FriendlyName — "Speakers (Realtek Audio)".</summary>
        private static PropertyKey FriendlyName = new()
        {
            FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            PropertyId = 14,
        };

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration,
                long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }

        /// <summary>
        /// The only reason this is here is <see cref="SetClientProperties"/>, and
        /// through it AUDCLNT_STREAMOPTIONS_RAW — the stream option that takes the
        /// system effects out of the way without taking the device.
        ///
        /// All twelve of IAudioClient's methods are written out again rather than
        /// inherited. Inheriting a [ComImport] interface does not lay the base out
        /// first, which this codebase has already paid for once: the derived
        /// interface's first method landed on a base slot, read its arguments as
        /// something else, and killed the process inside the marshaller with an
        /// error pointing nowhere near the cause.
        /// </summary>
        [ComImport, Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient2
        {
            [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration,
                long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

            [PreserveSig] int IsOffloadCapable(int category, out int offloadCapable);
            [PreserveSig] int SetClientProperties(ref AudioClientProperties properties);
            [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven,
                out long minDuration, out long maxDuration);
        }

        [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioRenderClient
        {
            [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
            [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientProperties
        {
            public uint Size;
            public int IsOffload;
            public int Category;
            public uint Options;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WaveFormatEx
        {
            public ushort FormatTag;
            public ushort Channels;
            public uint SamplesPerSecond;
            public uint AverageBytesPerSecond;
            public ushort BlockAlign;
            public ushort BitsPerSample;
            public ushort ExtraSize;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset,
            bool initialState, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr block);

        private const int RenderFlow = 0;
        private const int ConsoleRole = 0;
        private const int SharedMode = 0;
        private const int ClsCtxAll = 23;

        private const uint EventCallback = 0x00040000;
        private const uint NoPersist = 0x40000000;
        private const uint AutoConvertPcm = 0x80000000;
        private const uint SrcDefaultQuality = 0x08000000;

        private const uint BufferSilent = 0x2;

        private const uint StreamOptionsRaw = 0x1;

        /// <summary>WAVE_FORMAT_IEEE_FLOAT, and the extensible tag that stands in for it.</summary>
        private const ushort FormatFloat = 3;
        private const ushort FormatExtensible = 0xFFFE;

        // ---- state ----------------------------------------------------------

        private IAudioClient? _client;
        private IAudioClient2? _client2;
        private IAudioRenderClient? _render;
        private IntPtr _formatBlock;
        private IntPtr _readyEvent;
        private Thread? _thread;
        private volatile bool _running;

        /// <summary>Frames the device asks for at a time.</summary>
        private uint _bufferFrames;

        /// <summary>What the device is playing: rate, channels, and whether it is float.</summary>
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public bool IsFloat { get; private set; }
        public int BitsPerSample { get; private set; }

        /// <summary>
        /// Whether the stream really was opened with the system effects bypassed.
        ///
        /// Asked for rather than assumed: raw mode is a request, and a device or
        /// a policy may refuse it. Reporting "raw" when the request failed would
        /// be exactly the kind of claim this codebase keeps having to unpick.
        /// </summary>
        public bool RawMode { get; private set; }

        /// <summary>Why it could not start, when it could not.</summary>
        public string Diagnostic { get; private set; } = "not started";

        /// <summary>
        /// Every playback device that is plugged in and switched on.
        ///
        /// Ids rather than names for the choosing, because names are neither
        /// unique nor stable — two identical headsets give the same string, and
        /// a driver update rewrites it. The name is what a person picks from and
        /// the id is what gets remembered.
        /// </summary>
        public static IReadOnlyList<AudioDevice> Devices()
        {
            var found = new List<AudioDevice>();
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (enumerator.EnumAudioEndpoints(RenderFlow, (int)DeviceStateActive, out var devices) != 0)
                    return found;

                if (devices.GetCount(out uint count) != 0) return found;

                for (uint i = 0; i < count; i++)
                {
                    if (devices.Item(i, out var device) != 0 || device == null) continue;
                    if (device.GetId(out string id) != 0 || string.IsNullOrEmpty(id)) continue;
                    found.Add(new AudioDevice(id, NameOf(device) ?? id));
                }
            }
            catch
            {
                // A machine with no audio at all is not an error worth throwing
                // over; it is an empty list and a menu that says so.
            }
            return found;
        }

        private static string? NameOf(IMMDevice device)
        {
            try
            {
                if (device.OpenPropertyStore(StorageRead, out var store) != 0 || store == null) return null;

                var key = FriendlyName;
                if (store.GetValue(ref key, out var value) != 0) return null;

                try
                {
                    return value.Type == VtLpwstr && value.Pointer != IntPtr.Zero
                        ? Marshal.PtrToStringUni(value.Pointer)
                        : null;
                }
                finally { try { PropVariantClear(ref value); } catch { } }
            }
            catch { return null; }
        }

        /// <summary>
        /// Which device this is playing on, and what it is called. Empty id means
        /// whatever Windows calls the default, which is what "follow the system"
        /// has to mean: the answer changes when somebody plugs in headphones.
        /// </summary>
        public string DeviceId { get; private set; } = "";
        public string DeviceName { get; private set; } = "";

        /// <summary>
        /// Fills <paramref name="destination"/> with up to <paramref name="frames"/>
        /// frames of interleaved float and returns how many it wrote. Fewer than
        /// asked means the source has run out; the rest is filled with silence.
        ///
        /// Called on the render thread, which must never block for long: this is
        /// the thread the device is waiting on, and a stall here is a dropout.
        /// </summary>
        public delegate int FillCallback(float[] destination, int frames);

        private FillCallback? _fill;

        /// <summary>Raised on the render thread when the source has no more to give.</summary>
        public event Action? Drained;

        /// <summary>
        /// The endpoint has gone — unplugged, disabled, or no longer the default
        /// it was opened as. Raised once, on the thread pool.
        ///
        /// Before this, the render loop went on timing out every two seconds
        /// with nothing said: the ring stayed full, so the player reported
        /// itself playing, and the clock stood still.
        /// </summary>
        public event Action? DeviceLost;

        private int _lostRaised;

        /// <summary>Whether this renderer's endpoint has gone.</summary>
        public bool Lost => Volatile.Read(ref _lostRaised) != 0;

        /// <summary>The system's default render endpoint now, or null.</summary>
        public static string? DefaultEndpointId()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (enumerator.GetDefaultAudioEndpoint(RenderFlow, ConsoleRole, out var device) != 0 || device == null)
                    return null;
                return device.GetId(out var id) == 0 ? id : null;
            }
            catch { return null; }
        }
        private int _quietWaits;

        private const int DeviceInvalidated = unchecked((int)0x88890004);   // AUDCLNT_E_DEVICE_INVALIDATED

        private void RaiseLost()
        {
            if (Interlocked.Exchange(ref _lostRaised, 1) != 0) return;
            ThreadPool.QueueUserWorkItem(_ => { try { DeviceLost?.Invoke(); } catch { } });
        }

        /// <summary>
        /// The loudest absolute sample seen since this was last read, and cleared
        /// by reading it.
        ///
        /// Worth having for its own sake: it is the only honest answer to "is it
        /// clipping", and this application has somebody who cannot look at a
        /// meter. See <see cref="ClippedSamples"/>.
        /// </summary>
        public float ReadPeak() => Interlocked.Exchange(ref _peakBits, 0) is var bits
            ? BitConverter.Int32BitsToSingle(bits)
            : 0f;

        private int _peakBits;
        private long _clipped;
        private long _measured;

        /// <summary>How many samples have gone past full scale since the last reset.</summary>
        public long ClippedSamples => Interlocked.Read(ref _clipped);

        /// <summary>How many samples have been through the gain at all, for the same window.</summary>
        public long MeasuredSamples => Interlocked.Read(ref _measured);

        /// <summary>
        /// What proportion of the audio is over the top, as a percentage.
        ///
        /// The count on its own answers nothing — a million clipped samples is
        /// either a badly overdriven track or four seconds out of an hour, and
        /// which of those it is decides whether anything is wrong. This is the
        /// number that can be said out loud.
        /// </summary>
        public double ClippedPercent
        {
            get
            {
                long total = MeasuredSamples;
                return total <= 0 ? 0 : ClippedSamples * 100.0 / total;
            }
        }

        /// <summary>
        /// Starts the count again, at the start of a track.
        ///
        /// It never used to be reset, so the figure was "since this process
        /// started" — which for an application the shell launches on every
        /// folder open is a number about a track nobody remembers playing.
        /// </summary>
        public void ResetClipping()
        {
            Interlocked.Exchange(ref _clipped, 0);
            Interlocked.Exchange(ref _measured, 0);
            Interlocked.Exchange(ref _peakBits, 0);
        }

        /// <summary>
        /// Multiplied into every sample on the way out. One is untouched.
        ///
        /// This is the whole point of owning the render loop: above one the
        /// samples go past full scale, and what happens to them then is decided
        /// here rather than by whatever the pipeline felt like doing.
        /// </summary>
        public float Gain
        {
            get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _gainBits));
            set => Volatile.Write(ref _gainBits, BitConverter.SingleToInt32Bits(value <= 0 ? 0f : value));
        }

        private int _gainBits = BitConverter.SingleToInt32Bits(1f);

        /// <summary>
        /// The limiter: lift whatever is quiet, and leave the loud alone.
        ///
        /// This is not a peak limiter and does not stop anything clipping. It is
        /// the other half of the same idea — an upward compressor with a very
        /// high ceiling on how much lift it is allowed — and what it does is
        /// take the parts of a track that sit thirty or forty decibels under the
        /// music and push them up towards full scale. Room tone, reverb tails,
        /// the breath before a line, the second guitar in the far corner of the
        /// mix: all the things a recording has and nobody hears.
        ///
        /// Loud passages are left exactly where the volume put them, which above
        /// 100 percent means they clip, and that is deliberate. Protecting the
        /// peaks would flatten the difference this exists to open up.
        ///
        /// It gets more extreme the further the volume is pushed, because the
        /// lift it may apply grows with the square of the gain — see
        /// <see cref="LimitCeiling"/> and the loop below. At normal volume it is
        /// a gentle leveller. At eight times it is a different recording.
        /// </summary>
        public bool Limiter { get; set; }

        /// <summary>
        /// Where the limiter is aiming to put quiet material, in amplitude.
        ///
        /// Not 1.0 by default. A leveller that pushes everything to exactly full
        /// scale leaves no room at all for the transient that arrives a
        /// millisecond after the envelope was measured, and the result is a
        /// continuous crunch rather than an open-sounding one.
        ///
        /// Settable, unlike the rest of this file's constants, because it is one
        /// of the three that can only be judged by ear against real music — see
        /// <see cref="LimiterForm"/>, which changes it while a track plays.
        /// </summary>
        public float LimitCeiling { get; set; } = 0.9f;

        /// <summary>
        /// How quickly the envelope follows the music upwards, in seconds.
        ///
        /// Also the whole of the overshoot, because there is no lookahead: the
        /// first instant of a note arriving into a fully-open limiter is lifted
        /// before the envelope has seen it. At two milliseconds that is a click
        /// at the start of every phrase; at a half it is an edge; below a tenth
        /// the envelope starts to follow the waveform of a bass note rather than
        /// its outline, which is intermodulation distortion — audible as a
        /// growl on low notes, and the reason the fast end of the ladder is
        /// worth having as well as worth avoiding.
        /// </summary>
        public float LimitAttackSeconds { get; set; } = 0.0005f;

        /// <summary>
        /// How slowly it comes back down, in seconds. This is what makes the
        /// envelope a peak follower rather than a waveform follower, and it is
        /// what decides whether the lift creeps up through a quiet passage or
        /// pumps in time with the beat.
        /// </summary>
        public float LimitReleaseSeconds { get; set; } = 0.25f;

        /// <summary>
        /// The most the limiter will ever lift, at a gain of one.
        ///
        /// Eighteen decibels. Enough to be plainly audible on the quiet parts of
        /// an ordinary track without turning a silent passage between songs into
        /// a wall of hiss — that is what the volume control is for, and it
        /// multiplies this.
        /// </summary>
        private const float BaseBoost = 8f;

        /// <summary>
        /// Lift the quiet parts by the same amount whatever the volume is.
        ///
        /// Off, the lift the limiter may apply grows with the square of the gain
        /// — which is what makes pushing the volume up open the background
        /// further, and is the shape the limiter was asked for. It has one
        /// consequence that is not obvious until you meet it: at or below 100
        /// percent there is no gain to square, so the effect is at its mildest
        /// exactly where somebody is most likely to be sitting when they want to
        /// hear what it does.
        ///
        /// On, the ceiling is <see cref="WholeRangeBoost"/> regardless — about
        /// 54dB of available lift at any volume, including a very quiet one. It
        /// is the setting for listening *into* a recording rather than for
        /// making one loud.
        /// </summary>
        public bool LimiterWholeRange { get; set; }

        /// <summary>
        /// The lift allowed when the volume is taken out of it. Five hundred and
        /// twelve — the same figure the square would reach at eight times gain,
        /// which is where the effect stops being subtle and starts being the
        /// point.
        /// </summary>
        private const float WholeRangeBoost = 512f;

        /// <summary>The envelope, and the smoothed lift being applied to it.</summary>
        private float _envelope;
        private float _boost = 1f;

        /// <summary>
        /// Twenty bands of tone control, applied after everything above.
        ///
        /// After, not before, and that is the whole of what "no compression on
        /// the bands" means here. The limiter watches an envelope and lifts what
        /// is quiet; anything raised in front of it is something it then reacts
        /// to, so a band boosted by six decibels arrives as rather less than six
        /// and moves about while it does. Behind it there is nothing left to
        /// argue with the setting.
        ///
        /// Which also means the equaliser is the last thing before the meter, so
        /// a boost that pushes past full scale is counted as the clipping it is.
        /// </summary>
        public Equaliser Equaliser { get; } = new();

        // ---- lifetime -------------------------------------------------------

        /// <summary>
        /// Opens the default endpoint in shared mode and starts the render thread.
        ///
        /// <paramref name="preferRaw"/> asks for the system effects to be
        /// bypassed. It is a request; <see cref="RawMode"/> says what was granted.
        /// </summary>
        /// <summary>
        /// Opens the device and runs the render loop, both on the render thread.
        ///
        /// Everything COM here is created on the thread that uses it, and that is
        /// not tidiness. A WASAPI client built on the caller's apartment and then
        /// touched from the render thread fails the cross-apartment
        /// QueryInterface with E_NOINTERFACE — which surfaces as a player that
        /// reports itself playing and never produces a sample, because the render
        /// loop's own catch swallows it. Build it where it will be used.
        /// </summary>
        public bool Start(FillCallback fill, bool preferRaw, string? deviceId = null)
        {
            _fill = fill ?? throw new ArgumentNullException(nameof(fill));

            using var opened = new ManualResetEventSlim(false);
            bool ok = false;

            _thread = new Thread(() =>
            {
                ok = Open(preferRaw, deviceId);

                // Claimed as opened in one step, against Start claiming it as
                // abandoned. Two separate flags left a window where each side
                // read the other's before it was written: the thread went on to
                // render, and Dispose then skipped freeing what it had opened.
                bool abandoned = Interlocked.CompareExchange(ref _openState, OpenDone, Opening) != Opening;
                opened.Set();

                // Given up on while this was still opening. Whatever Open managed
                // to create belongs to nobody now, so this thread frees it rather
                // than leaving it to a Dispose that has already run: the caller
                // stops waiting after ten seconds and disposes, and an Open still
                // assigning _client, _formatBlock and two event handles into the
                // object being disposed is a use-after-free in both directions —
                // and an audio client left Started that nothing can ever stop.
                if (abandoned)
                {
                    try { _client?.Stop(); } catch { }
                    FreeResources();
                    return;
                }

                if (ok) RenderLoop();
            })
            {
                IsBackground = true,
                Name = "ExplorerNative audio render",
                Priority = ThreadPriority.Highest,
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _running = true;
            _thread.Start();

            if (!opened.Wait(TimeSpan.FromSeconds(10)) &&
                Interlocked.CompareExchange(ref _openState, OpenAbandoned, Opening) == Opening)
            {
                Diagnostic = "the device did not open in time";
                _running = false;

                // The thread is still inside Open. It owns what it makes.
                _thread = null;
                return false;
            }

            // The open finished just as the wait ran out; it is ours after all.
            opened.Wait();

            if (!ok) _running = false;
            return ok;
        }

        private bool Open(bool preferRaw, string? deviceId)
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();

                IMMDevice? device = null;
                int hr = 0;

                // A remembered device that is not plugged in any more falls back
                // to the default rather than refusing to play. The alternative is
                // an application that goes silent because of a decision somebody
                // made last week about a headset they have since unplugged.
                //
                // **The state check is the whole of it, and it was missing.**
                // GetDevice *succeeds* for an endpoint that is unplugged or
                // disabled — it hands back a perfectly good IMMDevice for a thing
                // that is not there — and the refusal only arrives later, when
                // Activate answers AUDCLNT_E_DEVICE_INVALIDATED (0x88890004). So
                // the fallback above never fired, the open failed at the next
                // step, and the player reported "no audio output" *naming the
                // file somebody had just pressed Enter on*. The comment three
                // lines up described behaviour this method did not have, and it
                // cost somebody an afternoon looking at a folder of perfectly
                // good WAVs.
                if (!string.IsNullOrEmpty(deviceId))
                {
                    hr = enumerator.GetDevice(deviceId, out device);
                    if (hr != 0) device = null;

                    if (device != null &&
                        (device.GetState(out uint state) != 0 || state != DeviceStateActive))
                        device = null;
                }

                if (device == null)
                {
                    hr = enumerator.GetDefaultAudioEndpoint(RenderFlow, ConsoleRole, out device);
                    if (hr != 0 || device == null) { Diagnostic = $"no endpoint (0x{hr:X8})"; return false; }
                    DeviceId = "";
                }
                else DeviceId = deviceId!;

                EndpointId = device.GetId(out var endpoint) == 0 ? endpoint ?? "" : "";
                DeviceName = NameOf(device) ?? "";

                // IAudioClient2 first, because raw mode is only reachable through
                // it — and because SetClientProperties has to happen *before*
                // Initialize, which is the whole reason the order matters here.
                var iid2 = typeof(IAudioClient2).GUID;
                if (device.Activate(ref iid2, ClsCtxAll, IntPtr.Zero, out object raw2) == 0 &&
                    raw2 is IAudioClient2 client2)
                {
                    _client2 = client2;

                    if (preferRaw)
                    {
                        var properties = new AudioClientProperties
                        {
                            Size = (uint)Marshal.SizeOf<AudioClientProperties>(),
                            IsOffload = 0,
                            Category = 1,                 // AudioCategory_Media
                            Options = StreamOptionsRaw,
                        };
                        RawMode = client2.SetClientProperties(ref properties) == 0;
                    }
                }

                var iid = typeof(IAudioClient).GUID;
                hr = device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object rawClient);
                if (hr != 0 || rawClient is not IAudioClient client)
                {
                    Diagnostic = $"no audio client (0x{hr:X8})";
                    return false;
                }
                _client = client;

                // The device's own mix format. Asking for anything else in shared
                // mode means Windows resamples, and the point of being here is to
                // decide that for ourselves rather than have it decided.
                hr = client.GetMixFormat(out _formatBlock);
                if (hr != 0 || _formatBlock == IntPtr.Zero)
                {
                    Diagnostic = $"no mix format (0x{hr:X8})";
                    return false;
                }

                var format = Marshal.PtrToStructure<WaveFormatEx>(_formatBlock);
                SampleRate = (int)format.SamplesPerSecond;
                Channels = format.Channels;
                BitsPerSample = format.BitsPerSample;
                IsFloat = format.FormatTag == FormatFloat ||
                          (format.FormatTag == FormatExtensible && format.BitsPerSample == 32);

                if (!IsFloat)
                {
                    // Every machine this is likely to run on mixes in float. If one
                    // does not, say so rather than writing float into an integer
                    // buffer, which is not quiet audio, it is noise.
                    Diagnostic = $"device mixes in {format.BitsPerSample}-bit, not float";
                    return false;
                }

                _readyEvent = CreateEventW(IntPtr.Zero, false, false, null);
                if (_readyEvent == IntPtr.Zero) { Diagnostic = "no event"; return false; }

                // A second event, for pausing and for stopping. Without it a
                // suspended loop would sit out its whole timeout before noticing
                // either, which is a second of the key having done nothing.
                _wake = CreateEventW(IntPtr.Zero, false, false, null);
                if (_wake == IntPtr.Zero) { Diagnostic = "no wake event"; return false; }

                // Event driven, so the render thread sleeps until the device wants
                // more instead of spinning on the clock. NOPERSIST keeps this
                // stream's volume out of the per-application mixer's memory, which
                // is a setting the user did not ask us to change.
                hr = client.Initialize(SharedMode,
                    EventCallback | NoPersist | AutoConvertPcm | SrcDefaultQuality,
                    0, 0, _formatBlock, IntPtr.Zero);

                if (hr != 0)
                {
                    // AUTOCONVERTPCM is only valid on some paths; without it the
                    // mix format still works, it just will not resample for us.
                    hr = client.Initialize(SharedMode, EventCallback | NoPersist,
                        0, 0, _formatBlock, IntPtr.Zero);
                }

                if (hr != 0) { Diagnostic = $"initialise failed (0x{hr:X8})"; return false; }

                hr = client.SetEventHandle(_readyEvent);
                if (hr != 0) { Diagnostic = $"event handle refused (0x{hr:X8})"; return false; }

                hr = client.GetBufferSize(out _bufferFrames);
                if (hr != 0) { Diagnostic = $"no buffer size (0x{hr:X8})"; return false; }

                var renderIid = typeof(IAudioRenderClient).GUID;
                hr = client.GetService(ref renderIid, out object rawRender);
                if (hr != 0 || rawRender is not IAudioRenderClient render)
                {
                    Diagnostic = $"no render client (0x{hr:X8})";
                    return false;
                }
                _render = render;

                // One buffer of silence before starting, so the device has
                // something to play while the first real fill is being made.
                PrimeSilence();

                hr = client.Start();
                if (hr != 0) { Diagnostic = $"start failed (0x{hr:X8})"; return false; }

                // Phrased to be read after "Playing through:" on the audio
                // preferences page, which is the only place it is shown.
                Diagnostic = RawMode
                    ? $"this application, {SampleRate} Hz, {Channels} channels, system effects bypassed"
                    : $"this application, {SampleRate} Hz, {Channels} channels";
                return true;
            }
            catch (Exception ex)
            {
                Diagnostic = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private void PrimeSilence()
        {
            if (_render == null) return;
            if (_render.GetBuffer(_bufferFrames, out _) != 0) return;
            _render.ReleaseBuffer(_bufferFrames, BufferSilent);
        }

        /// <summary>
        /// Stops the device while nothing is being played, and starts it again.
        ///
        /// This is the battery rule, and it is not a micro-optimisation. An
        /// audio client that has been Started keeps asking for buffers at the
        /// device period for as long as it exists — about a hundred times a
        /// second, on a thread at Highest priority, filling them with silence —
        /// and it holds the endpoint awake while it does. A track paused and
        /// left alone did that indefinitely. Stopped, the callbacks cease
        /// entirely and the hardware is free to idle.
        /// </summary>
        public void SetSuspended(bool suspended)
        {
            if (_suspended == suspended) return;
            _suspended = suspended;

            // Woken so the loop notices at once rather than at the next timeout.
            try { SetEvent(_wake); } catch { }
        }

        private volatile bool _suspended;
        private IntPtr _wake;

        private void RenderLoop()
        {
            var scratch = new float[_bufferFrames * Channels];
            bool started = true;

            while (_running)
            {
                if (_suspended)
                {
                    if (started)
                    {
                        try { _client?.Stop(); } catch { }
                        started = false;
                    }

                    // Nothing to do and nothing asking. One wakeup a second, and
                    // only to notice that the flag has not changed.
                    WaitForSingleObject(_wake, 1000);
                    continue;
                }

                if (!started)
                {
                    try { _client?.Start(); } catch { }
                    started = true;
                }

                // Two seconds is far longer than any device period; reaching it
                // means the endpoint has stopped asking, which is a disconnect
                // rather than a slow moment.
                uint waited = WaitForSingleObject(_readyEvent, 2000);
                if (!_running) break;
                if (waited != 0)
                {
                    // Twice running is four seconds with nothing asked for.
                    if (++_quietWaits >= 2) RaiseLost();
                    continue;
                }

                _quietWaits = 0;

                try
                {
                    var client = _client;
                    var render = _render;
                    if (client == null || render == null) break;

                    int hr = client.GetCurrentPadding(out uint padding);
                    if (hr == DeviceInvalidated) { RaiseLost(); continue; }
                    if (hr != 0) continue;
                    uint free = _bufferFrames - padding;
                    if (free == 0) continue;

                    hr = render.GetBuffer(free, out IntPtr data);
                    if (hr == DeviceInvalidated) { RaiseLost(); continue; }
                    if (hr != 0 || data == IntPtr.Zero) continue;

                    int wanted = (int)free;
                    int got = 0;

                    // Everything between here and ReleaseBuffer is inside a
                    // finally, and that is not tidiness. A buffer claimed from
                    // WASAPI and not given back makes every later GetBuffer fail
                    // with AUDCLNT_E_OUT_OF_ORDER, which this loop reads as
                    // "continue" — so one exception anywhere in the copy would
                    // leave the device permanently silent while the player went
                    // on reporting that it was playing.
                    try
                    {
                        try { got = _fill?.Invoke(scratch, wanted) ?? 0; }
                        catch { got = 0; }

                        if (got < 0) got = 0;
                        if (got > wanted) got = wanted;

                        ApplyGainAndMeasure(scratch, got * Channels);

                        unsafe
                        {
                            int values = got * Channels;
                            int total = wanted * Channels;
                            var destination = new Span<float>((float*)data, total);
                            scratch.AsSpan(0, values).CopyTo(destination);

                            // Anything the source could not supply is silence
                            // rather than whatever the buffer held last time.
                            destination.Slice(values).Clear();
                        }
                    }
                    finally
                    {
                        render.ReleaseBuffer(free, 0);
                    }

                    if (got < wanted)
                    {
                        try { Drained?.Invoke(); } catch { }
                    }
                }
                catch
                {
                    // A render thread that throws takes the audio with it and
                    // nothing else notices. Keep going; the diagnostic and the
                    // drained event are how anything is reported.
                }
            }
        }

        /// <summary>
        /// The gain, the ceiling, and the meter — one pass over the samples.
        ///
        /// Separated out and internal so it can be tested with real numbers
        /// rather than only heard, which is the mistake that let a silent
        /// pipeline ship: every audio test in this suite plays generated silence,
        /// and silence times anything is silence.
        /// </summary>
        internal void ApplyGainAndMeasure(float[] buffer, int count)
        {
            float gain = Gain;
            float peak = 0f;
            long clipped = 0;

            int channels = Channels < 1 ? 1 : Channels;
            int frames = count / channels;

            // A sample that is not a number is silence. One infinite value in a
            // damaged float file drove the limiter's envelope to infinity and
            // then to NaN, after which the lift sat at its maximum for the rest
            // of the track; the meter could not count it either.
            for (int i = 0; i < count; i++)
                if (!float.IsFinite(buffer[i])) buffer[i] = 0f;

            // How fast the lift follows the music. Attack is quick, so a note
            // arriving under a fully-open limiter is turned down before it has
            // finished its first cycle rather than after it; release is slow, so
            // the lift creeps back up through a quiet passage instead of pumping
            // in time with the beat.
            //
            // Worked out from the device rate rather than assumed, because these
            // are times and the loop counts frames: 44.1 and 48 and 96 kHz all
            // have to sound the same.
            //
            // Half a millisecond of attack sounds far too fast for a compressor
            // and is not, because the release is a quarter of a second: what
            // this follows is the *peak* envelope, which rises with the music
            // and comes down slowly, rather than the waveform. With a fast
            // release as well it would track the shape of a bass note and
            // modulate the gain at the note's own frequency, which is
            // intermodulation distortion rather than a limiter.
            //
            // Half a millisecond is also the whole of the overshoot. There is no
            // lookahead here, so the first instant of a note arriving into a
            // fully-open limiter is lifted before the envelope has seen it — at
            // two milliseconds that is a click at the start of every phrase, and
            // at a half it is an edge.
            int rate = SampleRate > 0 ? SampleRate : 48000;
            float ceiling32 = LimitCeiling;
            float attack = Coefficient(rate, LimitAttackSeconds);
            float release = Coefficient(rate, LimitReleaseSeconds);

            float envelope = _envelope;
            float boost = _boost;

            // How much lift is allowed. It grows with the square of the gain,
            // which is the whole "the higher, the crazier" of it: at 200 percent
            // this is 32, at 400 it is 128, and at the top of the dial it is
            // thousands, by which point the background *is* the track.
            float drive = gain > 1f ? gain : 1f;
            float ceiling = LimiterWholeRange ? WholeRangeBoost : BaseBoost * drive * drive;

            // At or below full scale, with the whole-range switch off, the
            // limiter does nothing at all.
            //
            // It used to lift by up to eight times here, because the ceiling was
            // BaseBoost * 1 * 1 at a gain of one and eight is not one. That made
            // "the lift grows with the volume" untrue at the only place most
            // listening happens: turn the volume to a hundred with the limiter
            // on and the quiet parts still came up eighteen decibels, which is
            // not a leveller doing least, it is a leveller doing a great deal.
            //
            // The switch beside it is what says otherwise. "Same lift at any
            // volume" means exactly that and is the setting for listening *into*
            // a recording; without it the limiter is for what happens above a
            // hundred, and below a hundred there is nothing for it to do.
            bool limit = Limiter && (LimiterWholeRange || gain > 1f);

            // Switched off — or the volume brought back below a hundred — the
            // lift comes down over a few milliseconds rather than at the buffer
            // boundary. Dropping from as much as five hundred times to one in a
            // single sample was a click in the middle of whatever was playing.
            float settle = Coefficient(rate, 0.005);
            if (!limit) envelope = 0f;
            if (!float.IsFinite(envelope)) envelope = 0f;
            if (!float.IsFinite(boost)) boost = 1f;

            for (int f = 0; f < frames; f++)
            {
                int at = f * channels;

                if (limit)
                {
                    // One envelope for every channel, from the loudest of them.
                    // Following each channel separately would move the stereo
                    // image around whenever one side was busier than the other,
                    // which is the same mistake TimeStretch is careful not to
                    // make with its correlation.
                    float loudest = 0f;
                    for (int c = 0; c < channels; c++)
                    {
                        float s = buffer[at + c] * gain;
                        float m = s < 0 ? -s : s;
                        if (m > loudest) loudest = m;
                    }

                    // The very first frame starts the envelope where the music
                    // is rather than ramping up to it from silence. Ramping made
                    // every track begin with the limiter wide open for a couple
                    // of milliseconds — brief, but at a ceiling of a hundred
                    // times it is brief and very loud.
                    //
                    // And again after silence. "Nothing yet" used to be exactly
                    // zero, which an envelope decaying through a pause never
                    // reaches, so after the first silence the envelope ramped up
                    // from almost nothing and the first note arrived wide open.
                    if (envelope < ColdEnvelope) envelope = loudest;
                    else
                    {
                        float coefficient = loudest > envelope ? attack : release;
                        envelope += (loudest - envelope) * coefficient;
                    }

                    // Aim the quiet at the ceiling and leave the loud alone. No
                    // pow, no log, no exp: this runs per frame on the render
                    // thread, and a divide is the whole of it.
                    //
                    // Through silence the lift is held where it is. Aimed at the
                    // ceiling, it climbed all the way there while nothing was
                    // playing, and whatever came next began at hundreds of times.
                    float wanted = envelope >= ColdEnvelope ? ceiling32 / envelope : boost;
                    if (wanted < 1f) wanted = 1f;
                    if (wanted > ceiling) wanted = ceiling;

                    // The lift itself is smoothed as well, and asymmetrically:
                    // coming *down* is instant, so a transient is never lifted
                    // by a figure worked out while it was still quiet, and going
                    // up takes the release time, so nothing swells audibly
                    // inside a note.
                    boost = wanted < boost ? wanted : boost + (wanted - boost) * release;
                }
                else if (boost != 1f)
                {
                    boost += (1f - boost) * settle;
                    if (boost > 0.9999f && boost < 1.0001f) boost = 1f;
                }

                for (int c = 0; c < channels; c++)
                {
                    int i = at + c;
                    buffer[i] *= gain * boost;
                }
            }

            // Any odd samples left over by a count that is not a whole number of
            // frames. There should never be any; a buffer that is short of one
            // must not be left holding the previous track's audio.
            for (int i = frames * channels; i < count; i++) buffer[i] *= gain * boost;

            _envelope = envelope;
            _boost = boost;

            // The tone controls, after the gain and after the limiter. See
            // Equaliser: putting them in front means the compressor answers back
            // to every band that is raised.
            Equaliser.Process(buffer, count, channels, rate);

            // And the meter reads what actually leaves, which is why it is a pass
            // of its own now rather than folded into the loop above. Measuring
            // before the equaliser would report the peak of a signal nobody is
            // going to hear, and under-report the clipping of the one they are.
            for (int i = 0; i < count; i++)
            {
                float v = buffer[i];

                // Nothing is clamped. Measured through the endpoint: the
                // Windows shared mixer carries float above 1.0 linearly and
                // clips at the *endpoint*, after the master volume — amplitude 4
                // came back at exactly four times, and only at 8 did it stop,
                // with the Windows volume at 10%. Clamping here would put the
                // ceiling back in the application and throw away every bit of
                // that headroom.
                //
                // Counted, because "how much of this is over" is the only honest
                // answer to "is it clipping", and there is somebody here who
                // cannot look at a meter to find out.
                if (v > 1f || v < -1f) clipped++;

                float magnitude = v < 0 ? -v : v;
                if (magnitude > peak) peak = magnitude;
            }

            if (count > 0) Interlocked.Add(ref _measured, count);
            if (clipped > 0) Interlocked.Add(ref _clipped, clipped);

            // Highest wins until somebody reads it, so a peak between reads is
            // never lost to the sample that came after it.
            int bits = BitConverter.SingleToInt32Bits(peak);
            int seen = Volatile.Read(ref _peakBits);
            while (BitConverter.Int32BitsToSingle(seen) < peak)
            {
                int was = Interlocked.CompareExchange(ref _peakBits, bits, seen);
                if (was == seen) break;
                seen = was;
            }
        }

        /// <summary>
        /// A one-pole smoothing coefficient for a time constant, at a rate.
        ///
        /// The textbook form is 1 - exp(-1 / (seconds * rate)). This is worked
        /// out once per buffer rather than per sample, so the exp is free.
        /// </summary>
        /// <summary>
        /// Below this the envelope counts as silence: about -100dB, far under
        /// anything a recording carries as its quiet.
        /// </summary>
        private const float ColdEnvelope = 1e-5f;

        internal static float Coefficient(int sampleRate, double seconds)
        {
            if (sampleRate <= 0 || seconds <= 0) return 1f;
            double samples = seconds * sampleRate;
            if (samples < 1) return 1f;
            return (float)(1.0 - Math.Exp(-1.0 / samples));
        }

        public void Stop()
        {
            _running = false;

            // Both, because the loop may be waiting on either one — the device's
            // event while playing, or the wake event while suspended.
            try { if (_readyEvent != IntPtr.Zero) SetEvent(_readyEvent); } catch { }
            try { if (_wake != IntPtr.Zero) SetEvent(_wake); } catch { }

            var thread = _thread;
            _thread = null;
            try { thread?.Join(2000); } catch { }

            try { _client?.Stop(); } catch { }
        }

        /// <summary>
        /// Whether the device open is still running, finished, or was given up
        /// on by Start — whichever side gets there first decides, atomically.
        /// </summary>
        private int _openState;
        private const int Opening = 0, OpenDone = 1, OpenAbandoned = 2;

        private int _freed;

        /// <summary>
        /// Releases the device, the format block and the two events, once.
        ///
        /// Whichever of the two possible owners gets here first does it: the
        /// caller disposing normally, or an abandoned open finishing after the
        /// caller has stopped waiting. Freeing twice is a handle closed twice
        /// and memory freed twice, which is not an error anybody sees — it is
        /// the kind that shows up somewhere else entirely.
        /// </summary>
        private void FreeResources()
        {
            if (Interlocked.Exchange(ref _freed, 1) != 0) return;

            if (_render != null) { try { Marshal.ReleaseComObject(_render); } catch { } _render = null; }
            if (_client2 != null) { try { Marshal.ReleaseComObject(_client2); } catch { } _client2 = null; }
            if (_client != null) { try { Marshal.ReleaseComObject(_client); } catch { } _client = null; }

            if (_formatBlock != IntPtr.Zero) { try { CoTaskMemFree(_formatBlock); } catch { } _formatBlock = IntPtr.Zero; }
            if (_readyEvent != IntPtr.Zero) { try { CloseHandle(_readyEvent); } catch { } _readyEvent = IntPtr.Zero; }
            if (_wake != IntPtr.Zero) { try { CloseHandle(_wake); } catch { } _wake = IntPtr.Zero; }
        }

        public void Dispose()
        {
            Stop();

            // An abandoned open frees its own, on the thread that is still
            // inside it; touching them from here is the race this avoids.
            if (Volatile.Read(ref _openState) == OpenAbandoned) return;

            FreeResources();
        }
    }
}
