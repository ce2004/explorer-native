using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// How a percentage on the dial becomes a number the engine understands.
    ///
    /// Linear is what it says. Perceptual squares it, because loudness is not
    /// heard on a straight line — halfway down a linear dial is barely quieter,
    /// and all the useful adjustment is crowded into the bottom of the range.
    /// </summary>
    public enum VolumeCurve { Linear, Perceptual }

    /// <summary>
    /// Plays one audio file. There is no window and never will be — the whole
    /// player is the global hotkeys, the Audio menu, the Audio pages in
    /// Preferences, and what it says when you press a key.
    ///
    /// This decides what happens and says what to announce; the sound itself is
    /// three other files. <see cref="AudioDecoder"/> turns a file into float
    /// through Media Foundation's source reader — which decodes whatever Windows
    /// decodes, MP3, AAC, WMA, WAV and the ones that actually matter here, FLAC
    /// and ALAC. <see cref="TimeStretch"/> changes the speed without changing
    /// the pitch. <see cref="WasapiRenderer"/> puts the samples on the device.
    ///
    /// It used to be built on the Media Foundation *media engine*, the object
    /// behind an HTML5 audio element, which did the decoding and the rendering
    /// together. That was the right first answer and the wrong last one: an
    /// engine that renders for itself offers exactly one hook into its audio
    /// path, on this machine that hook takes the sound and never gives it back,
    /// and nothing above full scale, no bypass of the system effects and no
    /// meter can be reached from outside it. Owning the samples is what makes
    /// all of those ordinary. The one thing the engine did better was change
    /// speed without changing pitch, and that is now TimeStretch's job.
    ///
    /// Every method answers with the sentence that describes what it did rather
    /// than speaking itself. Whether that sentence is ever said is a preference,
    /// and where it goes — the status bar or straight to NVDA — depends on
    /// whether there is a window up. Only the caller knows either.
    /// </summary>
    public sealed class AudioPlayer : IDisposable
    {
        // ---------- Media Foundation interop ----------

        private const uint MF_VERSION = 0x00020070;   // MF_SDK_VERSION 2, MF_API_VERSION 0x70

        [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);

        // ---------- State ----------

        private static int _mfStarted;

        /// <summary>
        /// Whether "Buffering" is currently on screen and owed a withdrawal.
        /// </summary>
        private bool _saidBuffering;

        private readonly object _gate = new();
        private bool _disposed;

        /// <summary>
        /// The track, as the user knows it. Not what the engine is reading — a
        /// remote file is read through <see cref="StreamingSource"/>, which is
        /// memory — but the name, the tags and what gets remembered as the last
        /// track all mean this.
        /// </summary>
        private string _path = "";

        /// <summary>
        /// The track being downloaded while it plays, when there is one. Held
        /// because Media Foundation is reading through it, and because disposing
        /// it is what deletes the part-downloaded file.
        /// </summary>
        private StreamingSource? _streaming;

        private string? _displayName;
        private int _volumePercent = 70;
        private bool _muted;

        // ---------- Settings the owner pushes in ----------

        /// <summary>
        /// Asks Media Foundation to buffer as little as it can before starting.
        /// Read once, when the engine is built.
        /// </summary>

        private VolumeCurve _curve = VolumeCurve.Linear;

        /// <summary>
        /// Which curve turns the dial into the engine's number.
        /// </summary>
        public VolumeCurve Curve
        {
            get => _curve;
            // Fixed at Perceptual in the application; only SelfCheck changes it,
            // so there is nothing to announce.
            set => _curve = value;
        }

        /// <summary>0 for none. Applied when a track starts and when it resumes.</summary>
        public int FadeInMilliseconds { get; set; }

        /// <summary>0 for none. A pause waits for the fade before it actually pauses.</summary>
        public int FadeOutMilliseconds { get; set; }

        /// <summary>How far back to jump when resuming from a pause. 0 for none.</summary>
        public int RewindOnResumeSeconds { get; set; }

        /// <summary>The slowest and fastest the player will go.</summary>
        public const int SlowestPercent = 25;
        public const int FastestPercent = 400;

        /// <summary>
        /// The speeds worth offering, close together where it matters.
        ///
        /// Near normal a few percent is audible and worth stepping through; past
        /// double, nobody is choosing between 210 and 220. It ends at four times,
        /// and every entry is a speed somebody would actually pick — which is why
        /// this is a list to arrow through rather than a slider handing out 137.
        /// </summary>
        public static readonly int[] SpeedLadder =
        {
            25, 50, 75, 85, 90, 95, 100, 105, 110, 120, 125, 150, 175, 200, 250, 300, 350, 400,
        };

        /// <summary>
        /// The percentage first, because that is what is being chosen and what a
        /// screen reader should say first. The words after it are only for the
        /// speeds that have a name people actually use.
        /// </summary>
        public static string DescribeSpeed(int percent) => percent switch
        {
            25 => "25 percent, a quarter speed",
            50 => "50 percent, half speed",
            100 => "100 percent, normal",
            200 => "200 percent, double speed",
            300 => "300 percent, three times",
            400 => "400 percent, four times",
            _ => percent + " percent",
        };

        /// <summary>
        /// The limiter's three ladders, and how each rung is said out loud.
        ///
        /// Ladders for the same reason the speed is one: these are values chosen
        /// by ear against real music, and the useful ones are not evenly spaced.
        /// Attack matters most below a millisecond and hardly at all above ten;
        /// release is the other way round. A slider would hand out 0.37ms, which
        /// is not a decision anybody made.
        ///
        /// The words after the number are the point of <see cref="DescribeAttack"/>
        /// and its neighbours. "500" is not an answer to "how fast"; "half a
        /// millisecond, an edge on transients" is, and it is what gets read out
        /// on every arrow press while the music is playing.
        /// </summary>
        public static readonly int[] LimiterAttackLadder =
        {
            50, 100, 200, 300, 500, 750, 1000, 1500, 2000, 3000, 5000, 10000, 20000, 50000, 100000,
        };

        public static readonly int[] LimiterReleaseLadder =
        {
            5, 10, 20, 30, 50, 75, 100, 150, 200, 250, 300, 400, 500, 750,
            1000, 1500, 2000, 3000, 5000,
        };

        public static readonly int[] LimiterCeilingLadder =
        {
            10, 20, 30, 40, 50, 60, 70, 75, 80, 85, 90, 95, 100,
        };

        public static string DescribeAttack(int microseconds)
        {
            string number = microseconds < 1000
                ? (microseconds / 1000.0).ToString("0.0#") + " milliseconds"
                : (microseconds / 1000) + (microseconds == 1000 ? " millisecond" : " milliseconds");

            return microseconds switch
            {
                <= 100 => number + ", follows the waveform, growls on bass",
                <= 500 => number + ", an edge on transients",
                <= 2000 => number + ", a click on transients",
                <= 10000 => number + ", lets the attack of a note through",
                _ => number + ", barely reacts",
            };
        }

        public static string DescribeRelease(int milliseconds)
        {
            string number = milliseconds < 1000
                ? milliseconds + " milliseconds"
                : (milliseconds / 1000.0).ToString("0.##") +
                  (milliseconds == 1000 ? " second" : " seconds");

            return milliseconds switch
            {
                <= 30 => number + ", pumps with the beat",
                <= 150 => number + ", breathes",
                <= 400 => number + ", smooth",
                <= 1000 => number + ", slow, keeps the dynamics",
                _ => number + ", barely lets go",
            };
        }

        public static string DescribeCeiling(int percent) => percent switch
        {
            100 => "100 percent, full scale, no headroom at all",
            >= 95 => percent + " percent, crunchy",
            >= 85 => percent + " percent, loud",
            >= 60 => percent + " percent, open",
            _ => percent + " percent, gentle",
        };

        private int _ratePercent = 100;
        public int PlaybackRatePercent
        {
            get => _ratePercent;
            set
            {
                int was = _ratePercent;
                _ratePercent = Math.Clamp(value, SlowestPercent, FastestPercent);
                if (_ratePercent == was) return;

                // One line, and it takes effect on the track that is playing.
                //
                // This used to hand the track between two backends, because only
                // the media engine could change the rate without changing the
                // pitch: the track was reopened, the position carried across and
                // the play state restored, and every one of those steps was a
                // way for a speed key to lose the music. The stretcher does it
                // in place — see TimeStretch — so there is nothing to hand over
                // and nothing to go wrong.
                var direct = _direct;
                if (direct != null) direct.Rate = _ratePercent / 100.0;
            }
        }

        /// <summary>
        /// Whether this speed can really be played.
        ///
        /// It used to be a question, and a real one: Media Foundation was free
        /// to refuse a rate, and a refused one played at the old speed while the
        /// dial claimed otherwise. The stretcher refuses nothing inside the
        /// range the dial offers, so the honest answer is now always yes.
        /// </summary>
        // SupportsSpeed was here. It asked the media engine whether it would
        // really play at a rate, because a refused one was accepted silently and
        // played at the old speed while the dial claimed otherwise. Every speed
        // on the ladder works for every file now, so the question has one answer
        // and asking it was the only thing keeping the "some speeds are missing"
        // apology in the dialog.

        private bool _repeat;
        public bool RepeatTrack
        {
            get => _repeat;

            // Pushed down to the pump, which is what makes repeat gapless.
            //
            // It used to be honoured here, in OnDirectFinished — and by the time
            // that runs the ring has already drained to nothing, because the
            // render thread is what noticed the silence and raised the event.
            // Answering it with a seek then emptied the ring again and refilled
            // it from cold. Every step of that is a delay with no audio coming
            // out, so a repeat was most of a second of silence between the last
            // note and the first.
            //
            // The pump turns the decoder round the moment it runs out, writing
            // the start of the track into the same ring in the same pass, so
            // nothing ever empties. OnDirectFinished is now only the *end* of a
            // track that is not repeating.
            set { _repeat = value; if (_direct != null) _direct.Loop = value; }
        }

        /// <summary>Why the player could not start, for the Preferences page.</summary>
        public string Diagnostic { get; private set; } = "not started";

        /// <summary>
        /// How to get back to the thread that owns the engine. Optional; without
        /// it the timers call the engine directly.
        ///
        /// This matters because the engine is a COM object created on the UI
        /// thread. A call to it from a timer's thread is a cross-apartment call,
        /// which does not complete until the UI thread pumps — so work posted
        /// here runs where the engine actually lives instead of queueing behind
        /// whatever that thread happens to be doing.
        /// </summary>
        public Action<Action>? Post { get; set; }

        private void OnOwnerThread(Action work)
        {
            var post = Post;
            if (post == null) { work(); return; }
            try { post(work); } catch { work(); }
        }

        /// <summary>
        /// What just happened, as a catalogue id and the sentence for it.
        ///
        /// The player has no Settings, no screen reader and no status bar, and it
        /// is not getting any — every command answers with a sentence and lets the
        /// caller decide where it goes, because the same key press has to reach the
        /// status bar when the window is up and NVDA when it is not. This is that
        /// same bargain for the things nobody pressed a key for: a download
        /// finishing, a fade starting, a file that was not there.
        ///
        /// Where a command does return a sentence, the id raised here names the
        /// same event more precisely — volume.max for the press that reached the
        /// top — and carries the sentence that command returned. It is a finer name
        /// for what the caller is already saying, not a second thing to say.
        ///
        /// Null changes nothing. Raised on whichever thread the event happened on,
        /// which for the engine's events and the downloader's is a worker, so a
        /// handler that touches the window gets itself home first.
        /// </summary>
        public Action<string, string>? Notify { get; set; }

        /// <summary>
        /// Never while holding <see cref="_gate"/>, and never fatal. This crosses
        /// into the tray, whose handler is free to turn round and ask the player
        /// what it is doing — which takes the same lock.
        /// </summary>
        private void Raise(string notificationId, string message)
        {
            var notify = Notify;
            if (notify == null) return;
            try { notify(notificationId, message); } catch { }
        }

        /// <summary>
        /// The answer to a command with no track behind it. Said whatever the
        /// announcement preferences are set to, because a key that is silent
        /// because nothing is loaded is indistinguishable from a broken one.
        /// </summary>
        private string NothingLoaded()
        {
            const string message = "Nothing is loaded";
            Raise("audio.nothing", message);
            return message;
        }

        /// <summary>The track ran to its end. Raised on a Media Foundation thread.</summary>
        public event Action<string>? Finished;

        /// <summary>
        /// The file could not be decoded. Raised on a Media Foundation thread.
        /// The extension list is deliberately generous, so this is the normal way
        /// of finding out that Windows has no decoder for something.
        /// </summary>
        public event Action<string>? Failed;

        public string NowPlayingPath { get { lock (_gate) return _path; } }

        /// <summary>
        /// What to call the track out loud. The file name until something better
        /// arrives — the owner reads the title tag on a worker and sets it, if
        /// that is what the preferences ask for.
        /// </summary>
        public string DisplayName
        {
            get
            {
                lock (_gate)
                {
                    if (!string.IsNullOrEmpty(_displayName)) return _displayName!;
                    return _path.Length == 0 ? "" : Path.GetFileName(_path);
                }
            }
        }

        public void SetDisplayName(string forPath, string? name)
        {
            lock (_gate)
            {
                // The track may already have moved on by the time a tag read
                // finishes; naming the wrong song is worse than naming none.
                if (!string.Equals(forPath, _path, StringComparison.OrdinalIgnoreCase)) return;
                _displayName = name;
            }
        }

        public bool HasTrack => NowPlayingPath.Length > 0;

        public bool IsMuted => _muted;

        // ---- the direct path ------------------------------------------------

        /// <summary>
        /// Our own decode-and-render. Null when nothing is loaded.
        /// </summary>
        private WasapiPlayback? _direct;

        /// <summary>Ask for the system effects to be bypassed.</summary>
        public bool PreferRawOutput { get; set; } = true;

        /// <summary>
        /// Which playback device to use. Empty means whatever Windows calls the
        /// default, and that is not the same as naming the device that happens to
        /// be default today: it follows the system when headphones are plugged in.
        ///
        /// Changing it moves a playing track across without stopping it.
        /// </summary>
        public string OutputDeviceId
        {
            get => _deviceId;
            set
            {
                // Only on a change. The settings are pushed into the player on
                // every Enter and every Preferences OK, and reopening the device
                // each time put a gap in whatever was playing and announced a
                // device nobody had changed.
                value ??= "";
                if (string.Equals(value, _deviceId, StringComparison.Ordinal)) return;

                _deviceId = value;
                _direct?.SwitchDevice(_deviceId);
            }
        }

        private string _deviceId = "";

        /// <summary>Every device that could be chosen, for the picker.</summary>
        public static IReadOnlyList<AudioDevice> OutputDevices() => WasapiRenderer.Devices();

        /// <summary>
        /// The limiter. Lifts what is quiet towards full scale and leaves what is
        /// loud where the volume put it — clipping included, on purpose. Takes
        /// effect on the track that is playing.
        /// </summary>
        public bool Limiter
        {
            get => _limiter;
            set { _limiter = value; if (_direct != null) _direct.Limiter = value; }
        }

        private bool _limiter;

        /// <summary>
        /// Lift the quiet parts by the same amount at any volume, rather than by
        /// one that grows with it. For listening into a recording rather than
        /// for making it loud.
        /// </summary>
        public bool LimiterWholeRange
        {
            get => _limiterWholeRange;
            set { _limiterWholeRange = value; if (_direct != null) _direct.LimiterWholeRange = value; }
        }

        private bool _limiterWholeRange;

        /// <summary>
        /// The limiter's shape: how loud it aims for, how fast it reacts, how
        /// slowly it lets go. Microseconds and milliseconds because that is what
        /// the ladders in <see cref="LimiterForm"/> are written in; a tenth of a
        /// millisecond and a whole one are different sounds, and a setting in
        /// whole milliseconds could not tell them apart.
        ///
        /// Held here as well as pushed down, because the renderer is rebuilt on
        /// every track and on every device change — a value that lived only on
        /// the renderer would quietly go back to its default at the start of the
        /// next song, which is the sort of thing somebody spends an evening
        /// blaming on their ears.
        /// </summary>
        public int LimiterCeilingPercent
        {
            get => _ceilingPercent;
            set
            {
                _ceilingPercent = Math.Clamp(value, 10, 100);
                if (_direct != null) _direct.LimitCeiling = _ceilingPercent / 100f;
            }
        }

        public int LimiterAttackMicroseconds
        {
            get => _attackMicroseconds;
            set
            {
                _attackMicroseconds = Math.Clamp(value, 50, 100_000);
                if (_direct != null) _direct.LimitAttackSeconds = _attackMicroseconds / 1_000_000f;
            }
        }

        public int LimiterReleaseMilliseconds
        {
            get => _releaseMilliseconds;
            set
            {
                _releaseMilliseconds = Math.Clamp(value, 5, 5000);
                if (_direct != null) _direct.LimitReleaseSeconds = _releaseMilliseconds / 1000f;
            }
        }

        private int _ceilingPercent = 90;
        private int _attackMicroseconds = 500;
        private int _releaseMilliseconds = 250;

        /// <summary>
        /// The twenty tone controls: whether they are in the path, and where
        /// each one is.
        ///
        /// Held here as well as pushed down, for exactly the reason the limiter's
        /// numbers are — the renderer is built afresh for every track and for
        /// every device change, so a curve that lived only on the renderer would
        /// silently flatten at the start of the next song.
        /// </summary>
        public bool Equaliser
        {
            get => _equaliser;
            set { _equaliser = value; if (_direct != null) _direct.EqualiserOn = value; }
        }

        private bool _equaliser;

        /// <summary>
        /// Every band's setting, in decibels, lowest frequency first. Always
        /// <see cref="ExplorerNative.Equaliser.BandCount"/> long, clamped, and a
        /// copy — so nothing outside can reach in and change one behind the
        /// player's back.
        /// </summary>
        public int[] EqualiserGains
        {
            get => (int[])_bandGains.Clone();
            set
            {
                for (int b = 0; b < _bandGains.Length; b++)
                    _bandGains[b] = value != null && b < value.Length
                        ? Math.Clamp(value[b],
                            -ExplorerNative.Equaliser.MaximumDecibels,
                            ExplorerNative.Equaliser.MaximumDecibels)
                        : 0;

                if (_direct != null) _direct.EqualiserGains = _bandGains;
            }
        }

        /// <summary>
        /// Moves one band. Separate from the whole set because that is what a
        /// slider does, and pushing twenty values down to change one is twenty
        /// chances for a value the dialog has not yet read back to be written
        /// over the one somebody is holding.
        /// </summary>
        public void SetEqualiserBand(int band, int decibels)
        {
            if (band < 0 || band >= _bandGains.Length) return;

            _bandGains[band] = Math.Clamp(decibels,
                -ExplorerNative.Equaliser.MaximumDecibels,
                ExplorerNative.Equaliser.MaximumDecibels);

            _direct?.SetEqualiserBand(band, _bandGains[band]);
        }

        private readonly int[] _bandGains = new int[ExplorerNative.Equaliser.BandCount];

        /// <summary>
        /// Silence reduction: whether it is in the path, and the five numbers
        /// that shape it.
        ///
        /// Held here as well as pushed down, for the reason the limiter's three
        /// and the equaliser's twenty are — the whole decode chain is rebuilt for
        /// every track and for every device change, and a value that lived only
        /// down there would go back to its default at the start of the next song.
        /// </summary>
        public bool SilenceReduction
        {
            get => _silenceOn;
            set { _silenceOn = value; if (_direct != null) _direct.SilenceOn = value; }
        }

        public int SilenceThresholdDecibels
        {
            get => _silenceThresholdDecibels;
            set
            {
                _silenceThresholdDecibels = Math.Clamp(value,
                    ExplorerNative.SilenceReduction.LowestThresholdDecibels,
                    ExplorerNative.SilenceReduction.HighestThresholdDecibels);
                if (_direct != null) _direct.SilenceThresholdDecibels = _silenceThresholdDecibels;
            }
        }

        public int SilenceMinimumMilliseconds
        {
            get => _silenceMinimumMilliseconds;
            set
            {
                _silenceMinimumMilliseconds = Math.Clamp(value,
                    ExplorerNative.SilenceReduction.ShortestMinimumMilliseconds,
                    ExplorerNative.SilenceReduction.LongestMinimumMilliseconds);
                if (_direct != null) _direct.SilenceMinimumMilliseconds = _silenceMinimumMilliseconds;
            }
        }

        public int SilenceLeaveMilliseconds
        {
            get => _silenceLeaveMilliseconds;
            set
            {
                _silenceLeaveMilliseconds = Math.Clamp(value, 0,
                    ExplorerNative.SilenceReduction.LongestLeaveMilliseconds);
                if (_direct != null) _direct.SilenceLeaveMilliseconds = _silenceLeaveMilliseconds;
            }
        }

        public int SilenceFadeOutMilliseconds
        {
            get => _silenceFadeOutMilliseconds;
            set
            {
                _silenceFadeOutMilliseconds = Math.Clamp(value, 0,
                    ExplorerNative.SilenceReduction.LongestFadeMilliseconds);
                if (_direct != null) _direct.SilenceFadeOutMilliseconds = _silenceFadeOutMilliseconds;
            }
        }

        public int SilenceFadeInMilliseconds
        {
            get => _silenceFadeInMilliseconds;
            set
            {
                _silenceFadeInMilliseconds = Math.Clamp(value, 0,
                    ExplorerNative.SilenceReduction.LongestFadeMilliseconds);
                if (_direct != null) _direct.SilenceFadeInMilliseconds = _silenceFadeInMilliseconds;
            }
        }

        private bool _silenceOn;
        private int _silenceThresholdDecibels = -48;
        private int _silenceMinimumMilliseconds = 400;
        private int _silenceLeaveMilliseconds = 150;
        private int _silenceFadeOutMilliseconds = 20;
        private int _silenceFadeInMilliseconds = 20;

        /// <summary>
        /// The rungs each of silence reduction's five numbers offers, and the words
        /// that go after them.
        ///
        /// **These are suggestions, not the whole range.** Every one of the five
        /// controls is an editable list: arrowing walks these rungs and reads the
        /// words out, and any number in the range can be typed into the same box
        /// instead.
        ///
        /// That is a deliberate exception to the rule <see cref="LimiterForm"/>
        /// argues for next door — that a list cannot hold a value nobody offered,
        /// which is a feature. It is a good rule for an attack time, where the
        /// rungs are decades apart and the space between two of them is not a
        /// decision anybody made. It is the wrong rule here: these are lengths of
        /// silence in milliseconds, the useful value depends on the recording in
        /// front of you, and the difference between a hundred and eighty and two
        /// hundred is one somebody can hear. A ladder that is the only way in is
        /// a ladder that decides for you.
        ///
        /// The words still do the work on the way past — "minus forty-eight
        /// decibels" is not an answer to "what counts as silence" and "quiet room
        /// tone" is — so the rungs carry them, and a number typed off the ladder
        /// is read back with its unit rather than bare.
        ///
        /// The far ends of all five are deliberately past the useful range. A
        /// threshold of minus one is very nearly the whole recording; a minimum
        /// of one millisecond is shorter than a single cycle of anything below a
        /// kilohertz. Those are not settings anybody should live at, and being
        /// able to reach them is how somebody finds out what the middle of the
        /// range is doing.
        /// </summary>
        public static readonly int[] SilenceThresholdLadder =
        {
            -80, -72, -66, -60, -54, -48, -42, -36, -30, -24, -18, -12, -9, -6, -3, -2, -1,
        };

        public static readonly int[] SilenceMinimumLadder =
        {
            1, 2, 3, 5, 10, 15, 20, 30, 50, 75, 100, 150, 200, 300, 400, 500, 750,
            1000, 1500, 2000, 3000, 5000,
        };

        public static readonly int[] SilenceLeaveLadder =
        {
            0, 10, 20, 30, 50, 75, 100, 150, 200, 300, 400, 500, 750, 1000, 1500, 2000,
        };

        public static readonly int[] SilenceFadeLadder =
        {
            0, 1, 2, 5, 10, 20, 30, 50, 75, 100, 150, 200, 300, 500,
        };

        public static string DescribeSilenceThreshold(int decibels) => decibels switch
        {
            <= -72 => decibels + " decibels, only true digital silence",
            <= -54 => decibels + " decibels, a clean recording's noise floor",
            <= -42 => decibels + " decibels, quiet room tone",
            <= -30 => decibels + " decibels, a noisy room, tape hiss",
            <= -18 => decibels + " decibels, breath and page turns count as silence",
            <= -9 => decibels + " decibels, anything but the loud parts",
            _ => decibels + " decibels, absurd, chops the music itself",
        };

        public static string DescribeSilenceLength(int milliseconds)
        {
            string number = milliseconds < 1000
                ? milliseconds + (milliseconds == 1 ? " millisecond" : " milliseconds")
                : (milliseconds / 1000.0).ToString("0.##") +
                  (milliseconds == 1000 ? " second" : " seconds");

            return milliseconds switch
            {
                // Shorter than one cycle of anything below a kilohertz, so what
                // it finds is not a gap but the part of a waveform that passes
                // near zero.
                <= 5 => number + ", inside a single waveform",
                <= 15 => number + ", a click",
                <= 50 => number + ", between syllables",
                <= 150 => number + ", between words",
                <= 400 => number + ", between sentences",
                <= 1000 => number + ", a pause",
                _ => number + ", between tracks",
            };
        }

        public static string DescribeSilenceLeave(int milliseconds) => milliseconds switch
        {
            0 => "nothing, a hard cut from one sound to the next",
            <= 30 => milliseconds + " milliseconds, barely a breath",
            <= 150 => milliseconds + " milliseconds, a beat",
            <= 500 => milliseconds + " milliseconds, a pause you would notice",
            _ => DescribeSilenceLength(milliseconds).Split(',')[0] + ", a real gap",
        };

        /// <summary>
        /// The two fades read differently even though they offer the same rungs,
        /// because they are two different events — the music going away and the
        /// music coming back — and the word that makes a number mean something is
        /// not the same word in both directions.
        /// </summary>
        public static string DescribeSilenceFadeOut(int milliseconds) => milliseconds switch
        {
            0 => "nothing, cut it dead",
            <= 5 => milliseconds + " milliseconds, just enough to kill the click",
            <= 30 => milliseconds + " milliseconds, a soft edge",
            <= 100 => milliseconds + " milliseconds, an audible fade down",
            _ => milliseconds + " milliseconds, a long fall away",
        };

        public static string DescribeSilenceFadeIn(int milliseconds) => milliseconds switch
        {
            0 => "nothing, straight back in",
            <= 5 => milliseconds + " milliseconds, takes the edge off the entry",
            <= 30 => milliseconds + " milliseconds, a soft entry",
            <= 100 => milliseconds + " milliseconds, an audible swell",
            _ => milliseconds + " milliseconds, a long rise",
        };

        /// <summary>
        /// The pitch shift in semitones, where 0 is the recording's own pitch.
        ///
        /// Held here as well as pushed down for the reason every other audio
        /// setting is: the renderer and the whole decode chain are rebuilt for
        /// each track, and a value that lived only down there would go back to
        /// normal at the start of the next song.
        /// </summary>
        public int PitchSemitones
        {
            get => _pitchSemitones;
            set
            {
                _pitchSemitones = Math.Clamp(value, LowestSemitones, HighestSemitones);
                if (_direct != null)
                    _direct.PitchRatio = Resampler.RatioForSemitones(_pitchSemitones);
            }
        }

        /// <summary>Whether a pitch change drags the tempo along with it.</summary>
        public bool PitchMovesTempo
        {
            get => _pitchMovesTempo;
            set { _pitchMovesTempo = value; if (_direct != null) _direct.PitchMovesTempo = value; }
        }

        private int _pitchSemitones;
        private bool _pitchMovesTempo;

        /// <summary>
        /// How far the pitch will go, in semitones.
        ///
        /// Two octaves each way. Past that a recording is not transposed, it is
        /// a sound effect — and the resampler is reading the source at four
        /// times or a quarter, where what comes out is more interpolation than
        /// recording. Two octaves covers every musical use and the chipmunk.
        /// </summary>
        public const int LowestSemitones = -24;
        public const int HighestSemitones = 24;

        /// <summary>
        /// The semitones worth offering, which is all of them.
        ///
        /// Unlike the speed ladder, this one is evenly spaced and complete: a
        /// semitone is already the unit music is written in, every one of them
        /// is a step somebody would choose, and there are only forty-nine. There
        /// is no equivalent of "nobody is choosing between 210 and 220 percent".
        /// </summary>
        public static int[] PitchLadder { get; } = BuildPitchLadder();

        private static int[] BuildPitchLadder()
        {
            var made = new int[HighestSemitones - LowestSemitones + 1];
            for (int i = 0; i < made.Length; i++) made[i] = LowestSemitones + i;
            return made;
        }

        public static string DescribePitch(int semitones) =>
            Resampler.DescribeSemitones(semitones);

        /// <summary>
        /// What the meter has to say, in a sentence.
        ///
        /// Deliberately not raised by anything on its own: it is an answer to a
        /// question, which is what the Preferences page asks it. Saying it
        /// unprompted would be narration over the music it is describing, which
        /// is the rule this whole player is built around.
        /// </summary>
        public string DescribeLevel()
        {
            var direct = _direct;
            if (direct == null || !HasTrack) return "Nothing is playing";

            double over = direct.ClippedPercent;
            string limiter = _limiter ? "limiter on" : "limiter off";

            if (over <= 0) return $"{limiter}, nothing clipping";
            if (over < 0.05) return $"{limiter}, the occasional peak clipping";
            return $"{limiter}, {over:0.#} percent of the audio clipping";
        }

        /// <summary>Whatever is actually playing, said plainly.</summary>
        public string OutputDiagnostic
        {
            get
            {
                string now =
                    _direct != null ? _direct.Diagnostic
                    : _lastDirectDiagnostic.Length > 0 ? _lastDirectDiagnostic + " (last track)"
                    : "nothing has played yet";

                // A recorded problem is appended rather than replaced, because
                // the state a failure leaves behind reads perfectly well as an
                // answer, and reporting only that is how a broken start gets
                // described as a working one.
                string problem = _lastProblem;
                return problem.Length == 0 ? now : now + " — but " + problem;
            }
        }

        private string _lastDirectDiagnostic = "";
        private string _lastProblem = "";

        public bool IsPlaying => _direct?.IsPlaying ?? false;

        /// <summary>
        /// Pays the cost of the first track before anyone presses anything.
        ///
        /// Measured on this machine, a cold first play is about 290ms from Enter
        /// to sound and a warm one about 50ms. The difference is two one-off
        /// costs, neither of which has anything to do with the file being played:
        /// building the media engine (~50-100ms) and Media Foundation resolving a
        /// source for the first time (~150ms) — loading the byte-stream handler,
        /// the decoder and the audio renderer.
        ///
        /// Both are per-process, which is what makes this worth doing: a source
        /// resolved here makes every later track start fast, not just this one.
        /// <paramref name="preloadPath"/> is decoded a little way and thrown
        /// away — nothing is ever sent to a device, so nothing is heard.
        /// </summary>
        public void Warm(string? preloadPath = null)
        {
            // Media Foundation itself is the larger half, and it costs nothing
            // to start it even when there is no file to practise on.
            EnsureStarted();

            if (string.IsNullOrEmpty(preloadPath) || !File.Exists(preloadPath)) return;

            try
            {
                // A decoder, opened and closed. That resolves the byte-stream
                // handler and the codec for the process, which is the part that
                // is paid once — and unlike the engine's preload there is no
                // renderer involved, so there is nothing that could make a sound
                // even if it went wrong.
                using var decoder = new AudioDecoder();
                if (decoder.Open(preloadPath, 48000, 2)) decoder.Read(new float[512], 128);
            }
            catch
            {
                // A preload that fails has cost nothing worth reporting; the
                // first real play simply pays what it would have paid anyway.
            }
        }

        /// <summary>How long the track is, or NaN before the metadata has landed.</summary>
        public double DurationSeconds
        {
            get
            {
                var direct = _direct;
                if (direct == null) return double.NaN;
                return direct.Duration > 0 ? direct.Duration : double.NaN;
            }
        }

        /// <summary>Jumps to an absolute position, with no clamping or announcement.</summary>
        public void SeekTo(double seconds) => _direct?.SeekTo(Math.Max(0, seconds));

        /// <summary>Where the track has got to, in seconds. Zero when there is none.</summary>
        public double PositionSeconds => _direct?.Position ?? 0;

        public int VolumePercent
        {
            get => _volumePercent;
            set
            {
                _volumePercent = Math.Clamp(value, 0, VolumeCeiling);
                CancelFade();
                ApplyVolume();
            }
        }

        /// <summary>
        /// The most the volume control offers.
        /// </summary>
        /// <remarks>
        /// A thousand, not three hundred, because the ceiling is not ours to set.
        ///
        /// Measured through the endpoint: the Windows shared-mode mixer carries
        /// float samples above full scale linearly and clips at the *endpoint*,
        /// after the master volume. So the real limit moves with the Windows
        /// volume slider — at 10% there is eight times full scale of room before
        /// anything clips, and at 100% there is none. A fixed 300 was this
        /// application deciding the answer for every one of those cases at once.
        ///
        /// Ten times is +20dB, which is enough to bring a badly recorded voice
        /// memo up to full scale, and past that the numbers stop describing
        /// anything anybody wants. It is a limit on the *control*, not on the
        /// hardware: what actually stops the sound getting louder is the endpoint,
        /// which is where it belongs.
        /// </remarks>
        public const int LoudestPercent = 1000;

        /// <summary>
        /// How high the dial may go, which is all the way.
        ///
        /// This used to have three states and a long argument attached, because
        /// the ceiling depended on whether a transform had been accepted into
        /// somebody else's pipeline — and the state *before* anything had been
        /// created to ask was the one that got it wrong, clamping a saved volume
        /// on the way in and writing it back to disk that way. Owning the
        /// samples removes the question: above full scale is a multiply, and it
        /// is available from the first sample of the first track.
        /// </summary>
        private int VolumeCeiling => LoudestPercent;

        /// <summary>
        /// The multiplier for a volume above 100, and exactly 1 at or below it.
        /// Linear in amplitude rather than perceptual: this is gain, and the
        /// number wanted is "twice as loud as the loudest the file goes".
        /// </summary>
        public static float GainFor(int percent) =>
            percent <= 100 ? 1f : percent / 100f;

        /// <summary>
        /// The curve, for a position at or below full scale.
        ///
        /// Pure, so it can be tested without an audio device — which is the only
        /// way to test it at all, since the difference between the two curves is
        /// something you hear.
        /// </summary>
        public static double EngineVolume(int percent, VolumeCurve curve)
        {
            // Anything above full is the effect's job; the engine stays at 1.0.
            double level = Math.Clamp(percent, 0, 100) / 100.0;
            return curve == VolumeCurve.Perceptual ? level * level : level;
        }

        /// <summary>
        /// Loads Media Foundation ahead of the first key press.
        ///
        /// Starting MF pulls in a dozen DLLs and takes a noticeable moment. It is
        /// process-wide and thread-safe, so it is paid for on a worker at startup
        /// and the first play does not wear it.
        /// </summary>
        public static void Prewarm() => ThreadPool.QueueUserWorkItem(_ => { try { EnsureStarted(); } catch { } });

        private static bool EnsureStarted()
        {
            if (Volatile.Read(ref _mfStarted) == 1) return true;
            int hr = MFStartup(MF_VERSION, 0);
            if (hr < 0) return false;
            Volatile.Write(ref _mfStarted, 1);
            return true;
        }

        // ---------- The direct path ----------

        /// <summary>
        /// Starts a track on our own renderer. False means it could not, and the
        /// caller should fall back rather than report a failure.
        /// </summary>
        private bool StartDirect(string path, out bool abandoned)
        {
            abandoned = false;

            // Which start this is. Stop while a track is still opening — a
            // second and more on the NAS, up to fifteen on a cold Drive file —
            // said "Nothing is loaded", and the track started a moment later.
            int generation;
            lock (_gate)
            {
                generation = ++_openGeneration;
                _startingPath = path;
                _pauseOnStart = false;
            }

            try
            {
                return StartDirectCore(path, generation, out abandoned);
            }
            finally
            {
                lock (_gate)
                    if (_openGeneration == generation) _startingPath = "";
            }
        }

        /// <summary>
        /// Calls off a track still opening, for a newer one: Enter on a cold
        /// Drive track and then on another waited out the whole of the first
        /// open, and then played the first until the second was ready. What is
        /// already playing is left alone — the opening start stopped it.
        /// </summary>
        public void CancelOpening()
        {
            StreamingSource? opening = null;
            lock (_gate)
            {
                if (_startingPath.Length == 0) return;
                _openGeneration++;
                _startingPath = "";
                _pauseOnStart = false;
                opening = _streaming;
                _streaming = null;
            }
            try { opening?.Dispose(); } catch { }
        }

        /// <summary>A start that Stop has called off, by number; see StartDirect.</summary>
        private int _openGeneration;

        /// <summary>The track being opened right now, or empty.</summary>
        private string _startingPath = "";

        /// <summary>Play/pause pressed while the track was opening.</summary>
        private bool _pauseOnStart;

        private bool StartDirectCore(string path, int generation, out bool abandoned)
        {
            abandoned = false;
            StopDirect();
            StopStreaming();
            CancelFade();
            ForgetSeekTarget();

            var direct = new WasapiPlayback
            {
                Limiter = _limiter,
                LimiterWholeRange = _limiterWholeRange,
                LimitCeiling = _ceilingPercent / 100f,
                LimitAttackSeconds = _attackMicroseconds / 1_000_000f,
                LimitReleaseSeconds = _releaseMilliseconds / 1000f,
                Rate = _ratePercent / 100.0,

                // Silent from the first sample when fading in, or the start of
                // the track is heard at full volume before the fade takes over.
                Gain = _muted || FadeInMilliseconds > 0 ? 0f : DirectGain(_volumePercent, Curve),
                EqualiserOn = _equaliser,
                EqualiserGains = _bandGains,
                SilenceOn = _silenceOn,
                SilenceThresholdDecibels = _silenceThresholdDecibels,
                SilenceMinimumMilliseconds = _silenceMinimumMilliseconds,
                SilenceLeaveMilliseconds = _silenceLeaveMilliseconds,
                SilenceFadeOutMilliseconds = _silenceFadeOutMilliseconds,
                SilenceFadeInMilliseconds = _silenceFadeInMilliseconds,
                PitchRatio = Resampler.RatioForSemitones(_pitchSemitones),
                PitchMovesTempo = _pitchMovesTempo,
                Loop = _repeat,
            };

            // A remote track plays through a download held in memory, so a skip
            // into it costs about a millisecond rather than a network round
            // trip. Null for a local file, and null if it could not be set up —
            // in which case the decoder simply opens the path, which is what
            // always happened before this existed.
            var streaming = OpenStreaming(path, generation);

            // Called off while the download was opening: nothing to start.
            lock (_gate)
            {
                if (_disposed || generation != _openGeneration)
                {
                    // The read-ahead waits on this name; left set, Prepare did
                    // nothing for this track until another one started.
                    _openingPath = "";
                    abandoned = !_disposed;
                    direct.Dispose();
                    return false;
                }
            }

            bool started;
            var openedOn = _deviceId;
            try
            {
                started = direct.Start(path, PreferRawOutput, 0, streaming,
                    openedOn.Length == 0 ? null : openedOn);
            }
            finally
            {
                lock (_gate) _openingPath = "";
            }

            if (!started)
            {
                // Stopped while it opened: Stop disposes the download, and the
                // decoder reading through it then fails — which is not the file
                // failing, and must not hand it to another application.
                lock (_gate)
                {
                    if (_disposed || generation != _openGeneration)
                    {
                        abandoned = !_disposed;
                        direct.Dispose();
                        return false;
                    }
                }

                _lastProblem = "could not start: " + direct.Diagnostic;
                Diagnostic = direct.Diagnostic;
                direct.Dispose();
                StopStreaming();
                return false;
            }

            lock (_gate)
            {
                // Disposed while the track was opening — quitting during a cold
                // Drive open. Keeping it would leave a decoder reading the letter
                // after the provider behind it has gone.
                // Or stopped, or replaced by a newer start, while it opened.
                // Whoever called it off has already let the download go.
                if (_disposed || generation != _openGeneration)
                {
                    abandoned = !_disposed;
                    direct.Dispose();
                    return false;
                }

                // Installed under the same lock as the check, so a Stop landing
                // between the two cannot miss the player and leave it playing
                // with no track recorded.
                _path = path;
                _displayName = null;

                // No longer opening, under the same lock: an Enter now calls off
                // nothing, where it used to dispose the download this track had
                // just started playing from.
                _startingPath = "";
                direct.Finished += OnDirectFinished;
                direct.Starved += OnUnderrun;
                direct.DeviceSwitched += OnDeviceSwitched;
                _direct = direct;
            }

            // Everything again, now that the setters can reach it. Every change
            // made while the track was opening — mute, the volume, the speed,
            // repeat — was kept in the fields and never got to this player: a
            // track started muted played aloud, and one with repeat switched on
            // ended and said nothing.
            direct.Limiter = _limiter;
            direct.LimiterWholeRange = _limiterWholeRange;
            direct.LimitCeiling = _ceilingPercent / 100f;
            direct.LimitAttackSeconds = _attackMicroseconds / 1_000_000f;
            direct.LimitReleaseSeconds = _releaseMilliseconds / 1000f;
            direct.Rate = _ratePercent / 100.0;
            direct.Gain = _muted || FadeInMilliseconds > 0 ? 0f : DirectGain(_volumePercent, Curve);
            direct.EqualiserOn = _equaliser;
            direct.EqualiserGains = _bandGains;
            direct.SilenceOn = _silenceOn;
            direct.SilenceThresholdDecibels = _silenceThresholdDecibels;
            direct.SilenceMinimumMilliseconds = _silenceMinimumMilliseconds;
            direct.SilenceLeaveMilliseconds = _silenceLeaveMilliseconds;
            direct.SilenceFadeOutMilliseconds = _silenceFadeOutMilliseconds;
            direct.SilenceFadeInMilliseconds = _silenceFadeInMilliseconds;
            direct.PitchRatio = Resampler.RatioForSemitones(_pitchSemitones);
            direct.PitchMovesTempo = _pitchMovesTempo;
            direct.Loop = _repeat;

            // And the device, if another was picked while this opened.
            if (!string.Equals(_deviceId, openedOn, StringComparison.Ordinal))
                direct.SwitchDevice(_deviceId);

            bool pause;
            lock (_gate) pause = _pauseOnStart;
            if (pause) direct.Pause();

            // Kept past the end of the track, because the question it answers —
            // did the device grant the effects bypass — is one somebody asks in
            // Preferences, which is exactly where nothing is playing.
            _lastDirectDiagnostic = direct.Diagnostic;

            // And a problem from last time is over. Left standing it would be
            // reported against every track that played perfectly afterwards.
            _lastProblem = "";
            return true;
        }

        /// <summary>
        /// Says where the sound went, or that it could not go there.
        ///
        /// The failure is the one worth hearing: a device that will not open
        /// leaves the music playing exactly where it was, which is the right
        /// thing to do and indistinguishable from the key having been ignored.
        /// </summary>
        private void OnDeviceSwitched(string device, bool ok)
        {
            if (ok) Raise("audio.device.changed", "Playing through " + device);
            else Raise("audio.device.failed", "Could not play through " + device);
        }

        private void OnDirectFinished()
        {
            string path = NowPlayingPath;
            if (path.Length == 0) return;

            // Repeat is not handled here any more, and that is the whole of what
            // makes it gapless: by the time this runs the ring has drained to
            // nothing, which is how the render thread knew to raise it. The pump
            // turns the decoder round instead, before anything empties. The
            // guard stays because a track can be repeating and still reach here
            // if the decoder failed outright rather than merely running out.
            if (_repeat) return;

            Finished?.Invoke(path);
        }

        private void StopDirect()
        {
            var direct = _direct;
            _direct = null;
            if (direct == null) return;

            direct.Finished -= OnDirectFinished;
            direct.Starved -= OnUnderrun;
            direct.DeviceSwitched -= OnDeviceSwitched;

            // Any "Buffering" this track put on screen is withdrawn with it,
            // or the next track starts under a warning about the last one.
            _saidBuffering = false;

            try { direct.Dispose(); } catch { }
        }

        // ---------- Commands ----------

        /// <summary>
        /// Starts a file, from the beginning, always. Returns what it would say,
        /// or null if there is no player on this machine — in which case the
        /// caller should open the file the ordinary way rather than leaving the
        /// user with silence.
        ///
        /// There is deliberately no "carry on where I left off". Pressing Enter
        /// on a track means play that track, and a player that sometimes starts
        /// two minutes in because of something you did an hour ago is a player
        /// you cannot predict.
        /// </summary>
        public string? Play(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            try
            {
                if (StartDirect(path, out bool abandoned))
                {
                    // Muted stays silent: the fade would have brought it up to
                    // full volume with the player still saying "Muted".
                    if (FadeInMilliseconds > 0 && !_muted)
                        StartFade(DirectGain(_volumePercent, Curve), FadeInMilliseconds, false, 0);

                    return _direct?.IsPaused == true ? "Paused" : "Playing " + DisplayName;
                }

                // Stopped while it opened: nothing to report and nothing to hand
                // to another application.
                if (abandoned) return "";
            }
            catch (Exception ex)
            {
                Diagnostic = ex.Message;
                lock (_gate) { _path = ""; _displayName = null; }
                Raise("audio.failed", $"Could not play {Path.GetFileName(path)}: {ex.Message}");

                // The same hand-over as every other failure: the listener is
                // what opens the file elsewhere.
                Failed?.Invoke(path);
                return null;
            }

            // It did not start, and *why* it did not start is the whole of what
            // gets said here.
            //
            // This used to say "Windows cannot decode .flac" for every failure
            // there is. StartDirect returns false when the device will not open,
            // when the file has gone, when a share is not answering and when the
            // decoder times out, and all four came out as an accusation against
            // the file format — which for somebody whose whole library is FLAC
            // reads as "this player cannot play my music". It is also the least
            // likely of the four: Windows decodes FLAC and has since 10.
            //
            // The extension list is deliberately generous — Ogg and Opus are on
            // it and Windows decodes those only with the Web Media Extensions
            // installed — so a genuine "no decoder" is a normal answer rather
            // than an error, and it still hands the file to whichever
            // application owns it. That is what returning null means.
            lock (_gate) { _path = ""; _displayName = null; }

            var extension = Path.GetExtension(path);
            var reason = _lastProblem;

            if (reason.Contains("renderer", StringComparison.OrdinalIgnoreCase))
            {
                // Not the file's fault at all, and the sentence has to be built
                // that way round rather than merely contain the fact.
                //
                // It used to read "Could not play <name>: no audio output",
                // which puts the file in the subject and the cause after the
                // colon — and that is exactly how it was read. A folder of
                // perfectly good WAVs got taken apart looking for what was wrong
                // with them, because a remembered output device had been
                // unplugged and every track in the application was failing. The
                // file is not named here at all now: naming it is what did the
                // damage, and a device that will not open affects all of them.
                Raise("audio.failed",
                    "No audio output — nothing will play until an output device is available. " +
                    "Check the device in Preferences.");
            }
            else if (!FileIsThere(path))
            {
                Raise("audiofile.missing", Path.GetFileName(path) + " is no longer there");
            }
            else if (reason.Contains("in time", StringComparison.OrdinalIgnoreCase))
            {
                // A cloud or network file whose first read never came back. The
                // extension has nothing to do with it.
                Raise("audio.failed",
                    $"Could not play {Path.GetFileName(path)}: it did not open in time");
            }
            else
            {
                Raise("audiofile.unsupported", "Windows cannot decode " +
                    (extension.Length == 0 ? Path.GetFileName(path) : extension) +
                    "." + AudioFiles.AdviceFor(extension));
            }

            Failed?.Invoke(path);
            return null;
        }

        /// <summary>
        /// Whether the file is still there, asked only once something has
        /// already gone wrong.
        ///
        /// Never on the way in. This is a cloud or network path as often as not,
        /// and an existence check on one costs a round trip — the whole
        /// application is built around not making that call on a path somebody
        /// is waiting on. Here the waiting is over and the question is what to
        /// tell them.
        /// </summary>
        private static bool FileIsThere(string path)
        {
            try { return File.Exists(path); }
            catch { return true; }
        }

        public string TogglePlayPause()
        {
            var direct = _direct;
            if (direct == null || !HasTrack)
            {
                // Still opening: the answer is kept for when it is open.
                lock (_gate)
                {
                    if (_startingPath.Length > 0)
                    {
                        _pauseOnStart = !_pauseOnStart;
                        return _pauseOnStart ? "Paused" : "Playing";
                    }
                }
                return NothingLoaded();
            }

            try
            {
                // Paused, about to be, or finished: all three answer "play".
                // A fading pause has not reached the device yet, and treating it
                // as playing made the second press pause again; a finished track
                // is not paused, and treating it as playing made the first press
                // after the end say "Paused" and do nothing.
                bool pausing;
                lock (_gate) pausing = _fading && _pauseWhenFaded;

                if (!direct.IsPaused && !pausing && direct.IsPlaying)
                {
                    PauseWithFade(direct);

                    // Something left paused indefinitely should not go on holding
                    // tens of megabytes for a track nobody is listening to.
                    ArmRelease();
                    return "Paused";
                }

                CancelRelease();
                ResumeBuffering();

                // Restarting something that ran to the end plays it again, which
                // is what pressing play on a finished track means everywhere else.
                double now = direct.Position;
                double length = direct.Duration;

                // Ended is the player's own word for it. A track that stopped
                // short of its length — a truncated file, a download that gave
                // up — was "resumed" into silence for ever otherwise.
                if (direct.Ended || (length > 0 && now >= length - 0.05)) direct.SeekTo(0);
                else if (RewindOnResumeSeconds > 0 && !pausing)
                {
                    // The last few seconds again, so picking a track back up does
                    // not drop you into the middle of a word. Not when the pause
                    // was still fading out: nothing was ever paused, and a double
                    // tap jumped back.
                    direct.SeekTo(Math.Max(0, now - RewindOnResumeSeconds));
                }

                bool fadeIn = FadeInMilliseconds > 0 && !_muted;
                double level = DirectGain(_volumePercent, Curve);
                double from = pausing ? direct.Gain : 0;

                // A pause still fading out is called off, not carried out: done
                // here, the device was stopped and started again for nothing,
                // with a buffer at full level in between.
                if (pausing)
                {
                    lock (_gate)
                    {
                        _fadeGeneration++;
                        _fading = false;
                        _pauseWhenFaded = false;
                        _fadeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    }
                }
                else CancelFade();

                // Muted is silent whatever else is going on; it was put back to
                // full volume here with the player still saying "Muted".
                direct.Gain = _muted ? 0f : fadeIn ? (float)from : (float)level;
                direct.Resume();
                if (fadeIn) StartFade(level, FadeInMilliseconds, false, from);

                // Picking a track back up, which is not the same event as starting
                // one — the same key does both and only this branch resumed.
                var resumed = "Playing " + DisplayName;
                Raise("audio.resume", resumed);
                return resumed;
            }
            catch { return "The player did not answer"; }
        }

        public string Stop()
        {
            bool starting;
            lock (_gate) starting = _startingPath.Length > 0;
            if ((_direct == null || !HasTrack) && !starting) return NothingLoaded();

            try
            {
                // Stop means stop. Fading out of it would be a slower way of
                // doing what pause already does. A track still opening is called
                // off too, and never installed.
                // The open, if there is one, is called off first: the start
                // checks this number under the same lock it installs under.
                lock (_gate)
                {
                    _openGeneration++;
                    _startingPath = "";
                    _pauseOnStart = false;
                }

                CancelFade();
                ForgetSeekTarget();
                StopDirect();
                StopStreaming();
                lock (_gate) { _path = ""; _displayName = null; }
                return "Stopped";
            }
            catch { return "The player did not answer"; }
        }

        /// <summary>
        /// Moves the volume by a step and says where it landed. Works with no
        /// track loaded — setting the level before starting one is reasonable,
        /// and there is no window to set it in.
        /// </summary>
        public string ChangeVolume(int deltaPercent)
        {
            int before = _volumePercent;
            VolumePercent = _volumePercent + deltaPercent;

            if (_muted && deltaPercent > 0)
            {
                // Turning it up while muted and hearing nothing is a bug report
                // waiting to happen.
                _muted = false;
                ApplyVolume();

                var unmuted = $"Volume {_volumePercent} percent, unmuted";
                Raise("volume.unmuted.auto", unmuted);
                return unmuted;
            }

            // Said on arriving at an end, not on being clipped by one. Held down,
            // the key would otherwise go quiet at exactly the moment the listener
            // wants to know it has stopped moving.
            int top = VolumeCeiling;

            string edge =
                _volumePercent == top && deltaPercent > 0 ? ", maximum" :
                _volumePercent == 0 && deltaPercent < 0 ? ", minimum" : "";

            string message = $"Volume {_volumePercent} percent{edge}";

            // Crossing above full scale, said on the way past rather than on
            // every step above it: from here the samples themselves are being
            // multiplied and the file has no more to give.
            if (before <= 100 && _volumePercent > 100)
                Raise("volume.gain", $"Volume {_volumePercent} percent, {GainFor(_volumePercent):0.0} times gain");

            if (_volumePercent == top && deltaPercent > 0)
            {
                Raise("volume.max", message);
            }
            else if (_volumePercent == 0 && deltaPercent < 0)
            {
                Raise("volume.min", message);
            }

            return message;
        }

        public string ToggleMute()
        {
            _muted = !_muted;

            // A fade in flight would write the gain again a few milliseconds
            // later, from numbers that know nothing about the mute — so muting
            // during a fade went silent for one tick and then came back up,
            // while the player still said "Muted". The volume keys already
            // cancel for this reason; this one did not.
            CancelFade();

            // There is no mute of its own: silence is a gain of zero, and going
            // through ApplyVolume means unmuting puts back whatever the dial says
            // rather than a level remembered separately.
            ApplyVolume();

            if (_muted) return "Muted";

            var unmuted = $"Unmuted, volume {_volumePercent} percent";
            Raise("volume.unmuted", unmuted);
            return unmuted;
        }

        /// <summary>
        /// Moves by a step and says where that lands, at once.
        ///
        /// The step is counted from where the last press was aiming, not from
        /// where the engine says it is. `GetCurrentTime` does not move until a
        /// seek has actually completed, so five quick presses all read the same
        /// position, all compute the same target, and all land in the same
        /// place — the key appeared to do nothing at all after the first press.
        /// That much is still needed and always will be.
        ///
        /// What is gone is the throttling. Every press used to be gathered up and
        /// applied once the key came up, because a seek over the network cost
        /// between a third of a second and two seconds and forty of those would
        /// tear the decode apart. The track now plays out of memory, where a seek
        /// costs about a millisecond, so there is nothing left to protect and the
        /// throttle was only ever felt as lag.
        /// </summary>
        public string Seek(double deltaSeconds)
        {
            // Not "is there an engine": the direct path has no engine and is the
            // one playing whenever the speed is normal, so guarding on the engine
            // made every skip a no-op that still reported the position it meant
            // to reach.
            if (!HasTrack)
            {
                // Opening: the skip would be applied to nothing and said as if
                // it had worked.
                lock (_gate)
                    if (_startingPath.Length > 0) return "Still opening";
                return NothingLoaded();
            }

            try
            {
                double duration = DurationSeconds;
                long now = Environment.TickCount64;

                // Read outside the lock: it is a call into the engine.
                double live = PositionSeconds;

                double target;
                bool atStart = false, wrapped = false;
                lock (_gate)
                {
                    bool fresh = !double.IsNaN(_seekTarget) && now - _seekTargetAt < SeekTargetLifetimeMs;
                    target = (fresh ? _seekTarget : live) + deltaSeconds;

                    // The start is where a backward step stops, and it is said
                    // outside the lock: skipping into it is the moment a step
                    // stops being a step, which the time alone does not say.
                    if (target <= 0) { target = 0; atStart = deltaSeconds < 0; }

                    // **Forward past the end starts the track again.** It used to
                    // stop a quarter of a second from the end, so a held key ran
                    // to that spot and stayed there — every further press landed
                    // in the same place, and getting back to the music meant
                    // pressing "back to the start" or rewinding by hand. Skipping
                    // forward is a wheel now: the far edge of the track is the
                    // near edge of it.
                    //
                    // Zero rather than the overshoot carried round, because "it
                    // starts over" is the thing that was asked for and a step
                    // that lands a few seconds in has already missed the
                    // beginning. From there the next press steps forward
                    // normally, so holding the key sweeps round and round.
                    if (!double.IsNaN(duration) && !double.IsInfinity(duration) && duration > 0 &&
                        target > duration && deltaSeconds > 0)
                    {
                        target = 0;
                        wrapped = true;
                    }

                    _seekTarget = target;
                    _seekTargetAt = now;
                }

                ApplySeek();

                if (atStart) Raise("seek.start", "At the start");
                else if (wrapped) Raise("seek.wrapped", "Starting over");

                return SpokenTime(target);
            }
            catch { return "This track cannot be moved through"; }
        }

        /// <summary>Jumps to a fraction of the track: 0 for the start, 0.5 for halfway.</summary>
        public string SeekToFraction(double fraction)
        {
            if (!HasTrack) return NothingLoaded();

            try
            {
                double duration = DurationSeconds;
                if (double.IsNaN(duration) || double.IsInfinity(duration) || duration <= 0)
                    return "This track has no length to move through";

                double target = Math.Min(Math.Clamp(fraction, 0, 1) * duration, Math.Max(0, duration - 0.25));

                // An absolute jump ends any burst of relative steps: the next
                // "skip forward" counts from here, not from where they were going.
                lock (_gate) { _seekTarget = target; _seekTargetAt = Environment.TickCount64; }
                ApplySeek();

                // The two ends of the track are the two fractions anybody jumps to
                // by name — "back to the start" is this method with a zero.
                if (target <= 0) Raise("seek.start", "At the start");
                else if (fraction >= 1) Raise("seek.end", "At the end");

                return SpokenTime(target);
            }
            catch { return "This track cannot be moved through"; }
        }

        /// <summary>
        /// One rung up or down the speed ladder. <paramref name="direction"/> is
        /// +1 or -1.
        ///
        /// It used to be a step in percent, out of a preference. That could only
        /// ever land the speed between two of the values the chooser offers —
        /// and the ladder is not evenly spaced on purpose: five percent is
        /// audible near normal and nobody is choosing between 210 and 220, so a
        /// single number cannot be right at both ends. Stepping the ladder is
        /// the same list the speed dialog shows, which means the two keys and
        /// the dialog can no longer disagree about what the speeds are.
        /// </summary>
        public string StepSpeed(int direction)
        {
            int before = _ratePercent;
            PlaybackRatePercent = NextSpeed(_ratePercent, direction);

            string message = _ratePercent == 100 ? "Speed normal" : $"Speed {_ratePercent} percent";

            if (_ratePercent == 100 && before != 100)
            {
                Raise("audio.speed.normal", message);
            }
            else if (_ratePercent == before && direction != 0)
            {
                // The setter clamped it, so the key did nothing. Held at either end
                // of the ladder that is otherwise a key that has gone dead.
                Raise("audio.speed.limit", "Already at " + DescribeSpeed(_ratePercent));
            }
            // There is no "this file will not play at that speed" branch any
            // more, because there is no longer such a file. The rate used to
            // belong to the media engine and Windows' FLAC decoder refused
            // everything above normal; TimeStretch works on decoded samples and
            // cannot tell what container they came from.

            return message;
        }

        /// <summary>
        /// The rung above or below <paramref name="percent"/>, whether or not it
        /// is itself on the ladder.
        ///
        /// Pure, so the walking can be tested without an audio device. A value
        /// off the ladder — hand-edited into settings.json, or left by an older
        /// build — steps to the nearest rung in the direction asked for rather
        /// than refusing to move, which is the failure that reads as a dead key.
        /// </summary>
        public static int NextSpeed(int percent, int direction)
        {
            if (direction > 0)
            {
                foreach (int rung in SpeedLadder)
                    if (rung > percent) return rung;
                return SpeedLadder[^1];
            }

            if (direction < 0)
            {
                for (int i = SpeedLadder.Length - 1; i >= 0; i--)
                    if (SpeedLadder[i] < percent) return SpeedLadder[i];
                return SpeedLadder[0];
            }

            return percent;
        }

        public string ToggleRepeat()
        {
            RepeatTrack = !RepeatTrack;
            if (RepeatTrack) return "Repeat on";

            Raise("audio.repeat.off", "Repeat off");
            return "Repeat off";
        }

        /// <summary>
        /// The three time questions, each answering only what was asked.
        ///
        /// "What is playing" says everything at once, which is right when you
        /// have lost your bearings and wrong when you only want to know how much
        /// is left. These are one number each.
        /// </summary>
        public string DescribeElapsed()
        {
            if (!HasTrack) return "Nothing is playing";
            return SpokenTime(PositionSeconds) + " elapsed";
        }

        public string DescribeRemaining()
        {
            if (!HasTrack) return "Nothing is playing";

            double duration = DurationSeconds;
            if (double.IsNaN(duration) || double.IsInfinity(duration) || duration <= 0)
                return LengthNotKnown();

            return SpokenTime(Math.Max(0, duration - PositionSeconds)) + " remaining";
        }

        public string DescribeTotal()
        {
            if (!HasTrack) return "Nothing is playing";

            double duration = DurationSeconds;
            if (double.IsNaN(duration) || double.IsInfinity(duration) || duration <= 0)
                return LengthNotKnown();

            return SpokenTime(duration) + " in total";
        }

        /// <summary>
        /// The answer when the metadata has not landed. Not an error: the duration
        /// arrives with the metadata, on the engine's own thread, and for a format
        /// whose header does not carry one it is still being worked out.
        /// </summary>
        private string LengthNotKnown()
        {
            const string message = "The length is not known yet";
            Raise("position.unknown", message);
            return message;
        }

        /// <summary>What the "what is playing" key says.</summary>
        public string Describe()
        {
            var direct = _direct;
            if (direct == null || !HasTrack) return "Nothing is playing";

            try
            {
                string name = DisplayName;

                double now = direct.Position;
                double duration = DurationSeconds;
                if (double.IsNaN(now)) now = 0;

                bool ended = duration > 0 && !double.IsNaN(duration) && now >= duration - 0.05;
                string state = ended ? "Finished" : direct.IsPaused ? "Paused" : "Playing";

                string where = double.IsNaN(duration) || double.IsInfinity(duration)
                    ? SpokenTime(now)
                    : $"{SpokenTime(now)} of {SpokenTime(duration)}";

                string volume = _muted ? "muted" : $"volume {_volumePercent} percent";
                string speed = _ratePercent == 100 ? "" : $", speed {_ratePercent} percent";
                string repeat = _repeat ? ", repeating" : "";

                return $"{state} {name}, {where}, {volume}{speed}{repeat}";
            }
            catch { return "Playing " + DisplayName; }
        }

        /// <summary>
        /// Spoken, not punctuated. "1:20" is read out by NVDA as a pair of
        /// numbers with a colon between them, which is not what anyone means by
        /// a position in a track.
        /// </summary>
        public static string SpokenTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;

            var span = TimeSpan.FromSeconds(Math.Round(seconds));
            if (span.TotalHours >= 1)
                return $"{(int)span.TotalHours} hour{Plural((int)span.TotalHours)} {span.Minutes} minute{Plural(span.Minutes)}";
            if (span.TotalMinutes >= 1)
                return $"{span.Minutes} minute{Plural(span.Minutes)} {span.Seconds}";
            return $"{span.Seconds} second{Plural(span.Seconds)}";
        }

        private static string Plural(int n) => n == 1 ? "" : "s";

        // ---------- Seeking ----------

        /// <summary>
        /// After this long with no further press, the engine's own clock is the
        /// truth again — it has had time to complete the seek and carry on.
        /// </summary>
        private const int SeekTargetLifetimeMs = 2000;

        private double _seekTarget = double.NaN;
        private long _seekTargetAt;

        /// <summary>
        /// Whether to download a track while it plays and read through the
        /// download, rather than reading the file where it sits.
        /// </summary>
        public bool StreamWhilePlaying { get; set; } = true;

        /// <summary>The most of one track to hold in memory.</summary>
        public long CacheBytes { get; set; } = 1024L * 1024 * 1024;

        /// <summary>
        /// How long a track may sit paused before its memory is handed back.
        /// Nothing is unloaded — it keeps playing from the share if resumed, and
        /// starts downloading again.
        /// </summary>
        public TimeSpan ReleaseAfter { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Opens a download to play a remote track through, or null when that is
        /// not wanted or not possible — in which case the caller reads the file
        /// where it sits, which is what always happened before this existed.
        ///
        /// The decoder takes the stream from here; Media Foundation wraps it as
        /// a byte stream inside <see cref="AudioDecoder"/>. That indirection used
        /// to belong to the media engine, and it is the reason a skip on a NAS
        /// costs a millisecond rather than half a second.
        /// </summary>
        /// <summary>
        /// Starts downloading a track before anybody presses Enter on it.
        ///
        /// The read-ahead used to pull two megabytes through the operating
        /// system's cache and throw them away, on the theory that the copy that
        /// matters is the one Windows now holds. On the Drive letter that theory
        /// is only half true — the bytes land in the provider's own memory cache
        /// rather than on disk — and either way the work is thrown away: pressing
        /// Enter then builds a <see cref="StreamingSource"/> that starts its
        /// download again from zero.
        ///
        /// This keeps it instead. The download opened here is the one the track
        /// plays through, so by the time Enter is pressed the beginning of the
        /// file is already in memory and the decoder's header reads cost nothing.
        /// Measured on a cold Drive track: opening the decoder took 2.7 seconds
        /// against 186ms once the bytes were already held.
        ///
        /// One at a time, like everything else here. Resting on a new row
        /// releases the last one, so arrowing through a folder holds one track's
        /// worth of memory rather than the folder's.
        /// </summary>
        public void Prepare(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!StreamWhilePlaying || !AudioPrefetch.LooksRemote(path)) { CancelPrepare(); return; }

            int mine;
            lock (_gate)
            {
                if (_disposed) return;
                mine = ++_prepareSequence;

                // Already playing it, already opening it, or already getting
                // ready for it. Opening counts: Enter right after the cursor
                // settled used to start a second full download of the same track.
                if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return;
                if (string.Equals(_openingPath, path, StringComparison.OrdinalIgnoreCase)) return;
                if (_pending != null &&
                    string.Equals(_pendingPath, path, StringComparison.OrdinalIgnoreCase)) return;
            }

            StreamingSource? fresh;
            try { fresh = NewStreaming(path); }
            catch { return; }

            StreamingSource? going;
            lock (_gate)
            {
                // Disposed, or something started playing while this was opening —
                // or the cursor has moved on and a later Prepare has been asked
                // for. Opening runs on the pool, and a slow open for the row left
                // behind used to finish last and replace the download for the row
                // the cursor is on.
                if (_disposed || mine != _prepareSequence ||
                    string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_openingPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    try { fresh.Dispose(); } catch { }
                    return;
                }

                going = _pending;
                _pending = fresh;
                _pendingPath = path;
            }

            try { going?.Dispose(); } catch { }
        }

        /// <summary>
        /// A download opened by <see cref="Prepare"/> and not yet played, and
        /// which track it is for.
        /// </summary>
        private StreamingSource? _pending;

        /// <summary>The remote track a download is being opened for right now, or empty. See Prepare.</summary>
        private string _openingPath = "";
        private string _pendingPath = "";

        private int _prepareSequence;

        /// <summary>
        /// The cursor has settled on something that will not be played: whatever
        /// was being readied is let go, rather than a whole track going on
        /// downloading for a row nobody is on.
        /// </summary>
        public void CancelPrepare()
        {
            lock (_gate) _prepareSequence++;
            DropPending();
        }

        /// <summary>Throws away a readied download that nothing is going to use.</summary>
        private void DropPending()
        {
            StreamingSource? going;
            lock (_gate)
            {
                going = _pending;
                _pending = null;
                _pendingPath = "";
            }
            try { going?.Dispose(); } catch { }
        }

        /// <summary>
        /// Where a remote track can be read from other than its path — set to
        /// Google Drive's, so a track on the letter downloads straight from
        /// Google. Null, or null for a path, means the file itself.
        /// </summary>
        public static Func<string, IRangeSource?>? RangeSourceFor { get; set; }

        private StreamingSource NewStreaming(string path, Action<long, long>? filled = null)
        {
            IRangeSource? range = null;
            try { range = RangeSourceFor?.Invoke(path); } catch { }
            return range != null
                ? new StreamingSource(range, CacheBytes, filled)
                : new StreamingSource(path, CacheBytes, filled);
        }

        private StreamingSource? OpenStreaming(string path, int generation)
        {
            // A local file gains nothing: reading it through a downloader that is
            // copying it to the same disk is strictly more work for the same
            // speed.
            if (!StreamWhilePlaying || !AudioPrefetch.LooksRemote(path)) return null;

            // The one the read-ahead already opened for this very track, if the
            // cursor rested on it long enough. That is the whole point of
            // Prepare: the download is already running and the head of the file
            // is already in memory, so the decoder's header reads are free.
            lock (_gate)
            {
                if (_pending != null &&
                    string.Equals(_pendingPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    var adopted = _pending;
                    _pending = null;
                    _pendingPath = "";
                    // Not for a start already called off: Stop has let the
                    // download go, and one installed now would run on unowned.
                    if (_disposed || generation != _openGeneration)
                    {
                        try { adopted.Dispose(); } catch { }
                        return null;
                    }
                    _streaming = adopted;

                    Raise("audiofile.remote", Path.GetFileName(path) + " is on a network drive");
                    return adopted;
                }
            }

            // Readied for something else, and that something else is not being
            // played. Nothing will ever use it now.
            DropPending();

            // Free: LooksRemote has just answered it, and it is the whole reason
            // there is a download in front of this track at all.
            Raise("audiofile.remote", Path.GetFileName(path) + " is on a network drive");

            StreamingSource? source = null;
            lock (_gate) _openingPath = path;
            try
            {
                // The callback goes in through the constructor because the download
                // starts inside it — a short track handed a callback afterwards
                // could have finished before it was there to be called.
                source = NewStreaming(path, (downloaded, total) =>
                    Raise("position.buffered",
                        $"{SizeFormatter.Format(downloaded, SizeUnitStyle.FullWords)} of " +
                        $"{SizeFormatter.Format(total, SizeUnitStyle.FullWords)} downloaded"));

                // Opening a file on a sleeping share takes a second and more,
                // and a Stop or a quit in that time found nothing to let go.
                bool calledOff;
                lock (_gate)
                {
                    calledOff = _disposed || generation != _openGeneration;
                    if (!calledOff) _streaming = source;
                }
                if (calledOff)
                {
                    try { source.Dispose(); } catch { }
                    return null;
                }

                if (source.Held)
                    Raise("audiofile.cached",
                        SizeFormatter.Format(source.Cacheable, SizeUnitStyle.FullWords) + " held in memory");

                // Bigger than the budget: a window of it is held, moving with
                // the track. A skip outside the window is network speed.
                if (source.Cacheable < source.Length)
                    Raise("audiofile.cache.full",
                        $"This track is bigger than {SizeFormatter.Format(CacheBytes, SizeUnitStyle.FullWords)}; " +
                        "that much of it is kept downloaded around where it is playing");

                return source;
            }
            catch (Exception ex)
            {
                Diagnostic = "streaming unavailable: " + ex.Message;
                try { source?.Dispose(); } catch { }
                lock (_gate) _streaming = null;

                // The download opens the file before the decoder does, so it is
                // the first to find out that the file has gone or that something
                // else holds it. Playing carries on regardless — the decoder
                // opens it itself and fails in its own way — but the reason is
                // only here.
                var name = Path.GetFileName(path);
                if (ex is FileNotFoundException or DirectoryNotFoundException)
                    Raise("audiofile.missing", name + " is no longer there");
                else if (ex is IOException &&
                         (ex.HResult == SharingViolation || ex.HResult == LockViolation))
                    Raise("audiofile.locked", name + " is open in another application");

                return null;
            }
        }

        // What Windows says when somebody else has the file open on terms that do
        // not include us. Only these two: everything else that can come out of
        // opening a file is reported by the engine's own failure a moment later.
        private const int SharingViolation = unchecked((int)0x80070020);
        private const int LockViolation = unchecked((int)0x80070021);

        private void StopStreaming()
        {
            StreamingSource? going;
            lock (_gate)
            {
                going = _streaming;
                _streaming = null;
            }
            try { going?.Dispose(); } catch { }
        }

        // ---------- Giving the memory back ----------

        private Timer? _releaseTimer;

        /// <summary>
        /// Starts the clock on a paused track's memory. Called wherever playback
        /// stops; cancelled wherever it starts again.
        /// </summary>
        private void ArmRelease()
        {
            lock (_gate)
            {
                if (_disposed || _streaming == null) return;
                if (ReleaseAfter <= TimeSpan.Zero) return;

                _releaseTimer ??= new Timer(_ => OnOwnerThread(ReleaseBuffer), null,
                    Timeout.Infinite, Timeout.Infinite);
                _releaseTimer.Change((int)ReleaseAfter.TotalMilliseconds, Timeout.Infinite);
            }
        }

        private void CancelRelease()
        {
            lock (_gate) _releaseTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void ReleaseBuffer()
        {
            StreamingSource? streaming;
            lock (_gate) streaming = _streaming;

            // Only if it is still sitting paused. Something that started playing
            // again in the meantime has cancelled this anyway, but the timer may
            // already have been on its way.
            if (streaming == null || IsPlaying) return;

            // Read before the release, which is what takes them both to zero.
            long held = streaming.Cacheable;
            bool wasHolding = streaming.Held;

            streaming.Release();

            if (wasHolding)
                Raise("audiofile.cache.released",
                    $"Released {SizeFormatter.Format(held, SizeUnitStyle.FullWords)} " +
                    $"after {(int)ReleaseAfter.TotalMinutes} minutes paused");
        }

        /// <summary>Downloading starts again when a released track is played.</summary>
        private void ResumeBuffering()
        {
            CancelRelease();
            StreamingSource? streaming;
            lock (_gate) streaming = _streaming;
            streaming?.Resume();
        }

        private void ApplySeek()
        {
            double target;
            lock (_gate)
            {
                if (_disposed) return;
                target = _seekTarget;
            }

            if (double.IsNaN(target)) return;

            // One call, immediately, every press. Both backends jump within
            // memory — the engine through the streaming source, the direct path
            // through its own ring — so there is no reload and nothing worth
            // deferring on either.
            SeekTo(target);
        }

        // A "point the download at the skip" hint was here, and it is gone
        // because it made seeking worse rather than better.
        //
        // The idea was sound in isolation: the download walks the file from byte
        // zero, so a skip past what has arrived reads from the network. Moving
        // the download to the skip point fixes that one case. What it does to
        // every other case is thrash — a skip forward moved the window, a skip
        // back then fell *behind* it, and the download was torn down and started
        // again for each change of direction, throwing away the very bytes being
        // played. Holding the key alternates directions constantly.
        //
        // A music track is a few megabytes and lands whole within seconds. The
        // sequential download is the right shape for that, and the case the
        // window helped is the rare one. See CLAUDE.md.

        /// <summary>Forgets where the last burst of presses was aiming.</summary>
        private void ForgetSeekTarget()
        {
            lock (_gate) _seekTarget = double.NaN;
        }

        // ---------- Fading ----------

        private Timer? _fadeTimer;
        private int _fadeGeneration;

        /// <summary>
        /// Whether a fade is meant to be running at all.
        ///
        /// The generation on its own is not enough, and the gap is small but
        /// real: a cancel advances the counter, and a tick that happened to
        /// sample it *after* that would match the number and carry on. Two
        /// different questions are being asked — "is this the fade that is
        /// running" and "is one running" — and only the first has a number.
        /// </summary>
        private bool _fading;

        private double _fadeFrom, _fadeTo;
        private long _fadeStartedAt;
        private int _fadeMilliseconds;
        private bool _pauseWhenFaded;

        private const int FadeStepMilliseconds = 25;

        /// <summary>
        /// Puts the current level on the renderer.
        ///
        /// The whole dial is one multiply: the curve is applied here and the
        /// result is allowed to exceed one, which is the point — nothing
        /// downstream clamps it, and the endpoint is what finally says no.
        /// </summary>
        private void ApplyVolume()
        {
            var direct = _direct;
            if (direct != null) direct.Gain = _muted ? 0f : DirectGain(_volumePercent, Curve);
        }

        /// <summary>
        /// The multiplier for a position on the dial, over the whole range.
        ///
        /// Curved below full scale, because loudness is not heard on a straight
        /// line. Above it the curve is left behind and the number is taken
        /// literally: 300 percent is three times, because past full scale
        /// "perceptual" has nothing left to describe.
        /// </summary>
        public static float DirectGain(int percent, VolumeCurve curve)
        {
            if (percent <= 0) return 0f;
            if (percent <= 100) return (float)EngineVolume(percent, curve);
            return percent / 100f;
        }

        /// <summary>
        /// Abandons any fade in flight. The generation counter is what makes that
        /// safe: a tick from the old fade sees a number that has moved on and
        /// does nothing, so a second fade started mid-fade cannot be fought over.
        /// </summary>
        /// <summary>
        /// Stops a fade, and **carries out the pause it was on its way to** if
        /// there was one.
        ///
        /// Pausing with a fade does not pause: it ramps the gain down and leaves
        /// the actual pause to the last tick. Cancelling used to drop that
        /// intent on the floor, and the volume keys cancel the fade on every
        /// press — so pressing play/pause and then touching the volume inside
        /// the fade left the track playing, at the new volume, having already
        /// announced "Paused". Nothing took it back: the menu said Playing, the
        /// memory was never given back, and the only clue was the music.
        /// </summary>
        private void CancelFade()
        {
            bool pauseNow;
            WasapiPlayback? direct;

            lock (_gate)
            {
                _fadeGeneration++;
                _fading = false;
                pauseNow = _pauseWhenFaded;
                _pauseWhenFaded = false;
                _fadeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                direct = _direct;
            }

            if (pauseNow && direct != null)
            {
                // Paused, and the level put back where the dial says — the same
                // two steps the last tick of the fade would have taken, so a
                // track paused this way does not resume silent.
                try { direct.Pause(); ApplyVolume(); } catch { }
            }
        }

        private void PauseWithFade(WasapiPlayback direct)
        {
            if (FadeOutMilliseconds <= 0)
            {
                CancelFade();
                direct.Pause();
                ApplyVolume();
                return;
            }

            StartFade(0, FadeOutMilliseconds, pauseAtEnd: true, direct.Gain);
        }

        private void StartFade(double to, int milliseconds, bool pauseAtEnd, double? from = null)
        {
            // Where the fade begins. Read outside the lock, and from the renderer
            // rather than the dial: a fade started part way through another one
            // has to carry on from the level actually being heard.
            double start = from ?? (_direct?.Gain ?? 0);

            lock (_gate)
            {
                if (_disposed) return;

                _fadeGeneration++;
                _fading = true;
                _fadeFrom = start;
                _fadeTo = to;
                _fadeMilliseconds = Math.Max(1, milliseconds);
                _fadeStartedAt = Environment.TickCount64;
                _pauseWhenFaded = pauseAtEnd;

                // The generation is sampled on the timer thread and carried to
                // the owner thread, so a tick that was already in flight when the
                // fade was cancelled arrives holding a number that has moved on.
                // Without carrying it there is nothing for FadeStep to compare
                // against and the check below could never fire.
                _fadeTimer ??= new Timer(_ =>
                {
                    int generation = Volatile.Read(ref _fadeGeneration);
                    OnOwnerThread(() => FadeStep(generation));
                }, null, Timeout.Infinite, Timeout.Infinite);
                _fadeTimer.Change(FadeStepMilliseconds, FadeStepMilliseconds);
            }

            // Outside the lock, and only once the fade is really running: a
            // disposed player returns above without starting one.
            if (pauseAtEnd) Raise("volume.fade.out", "Fading out");
            else Raise("volume.fade.in", "Fading in");
        }

        /// <summary>
        /// One step of the fade identified by <paramref name="generation"/>.
        ///
        /// The number is what makes cancelling safe, and it has to be checked
        /// here or the counter is decoration. The timer is periodic and its work
        /// is posted to the thread that owns the engine, so by the time a tick
        /// runs the fade it belongs to may already have been abandoned —
        /// CancelFade stops the timer but cannot recall what is already queued.
        /// A stale tick that carried on would write its own fade's level over
        /// whatever replaced it: pressing a volume key part way through a fade in
        /// left the volume at the fade's value and stopped, because the timer was
        /// then stopped too and nothing came along to correct it.
        /// </summary>
        private void FadeStep(int generation)
        {
            WasapiPlayback? direct;
            double value;
            bool finished;
            bool pause;

            lock (_gate)
            {
                if (_disposed) return;
                if (!_fading || generation != _fadeGeneration) return;
                direct = _direct;
                if (direct == null) return;

                double elapsed = Environment.TickCount64 - _fadeStartedAt;
                double progress = Math.Clamp(elapsed / _fadeMilliseconds, 0, 1);
                value = _fadeFrom + (_fadeTo - _fadeFrom) * progress;

                finished = progress >= 1;
                pause = finished && _pauseWhenFaded;

                if (finished)
                {
                    _fadeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    _fading = false;
                    _pauseWhenFaded = false;
                }
            }

            try
            {
                direct.Gain = (float)value;
                if (pause)
                {
                    direct.Pause();

                    // Put the level back where the dial says, so the next play
                    // does not start silent when there is no fade in.
                    ApplyVolume();
                }
            }
            catch { }
        }

        // ---------- Buffering ----------

        /// <summary>
        /// Says, once, that the audio has run out while the track is still
        /// playing — and takes it back when it comes good.
        ///
        /// The media engine raised this as an event; the direct path knows it
        /// more directly, because the ring buffer between the decoder and the
        /// device is the thing that empties. Only after a real underrun: saying
        /// "Playing" on every start would be narration over a thing you can
        /// already hear, which is the rule this player is built around.
        /// </summary>
        private void OnUnderrun(bool starved)
        {
            if (NowPlayingPath.Length == 0) return;

            if (starved)
            {
                if (_saidBuffering) return;
                _saidBuffering = true;
                Raise("position.buffering", "Buffering");
                return;
            }

            if (!_saidBuffering) return;
            _saidBuffering = false;
            Raise("position.resumed", "Playing");
        }

        /// <summary>
        /// Proves the interop, which is the part of this that cannot be reasoned
        /// about — a COM interface is a list of offsets, and a wrong offset calls
        /// the wrong function with the right arguments.
        ///
        /// Returns null when everything lines up, or what went wrong. Silent: it
        /// sets the volume to zero before it loads anything.
        /// </summary>
        public string? SelfCheck(string audioFilePath, double expectedSeconds, int timeoutMs = 10000)
        {
            // Silent throughout: the level goes to nothing before anything is
            // loaded, so a suite run makes no sound whatever is being played.
            int volumeWas = VolumePercent;
            int rateWas = PlaybackRatePercent;

            try
            {
                Curve = VolumeCurve.Linear;
                FadeInMilliseconds = 0;
                FadeOutMilliseconds = 0;
                PlaybackRatePercent = 100;
                VolumePercent = 0;

                if (Play(audioFilePath) == null) return "could not start: " + Diagnostic;
                if (!HasTrack) return "nothing was loaded";

                // The decoder knows the length from the header, so unlike the
                // engine there is no revision upwards as the file is indexed —
                // but the pump opens it on its own thread, so it is worth
                // waiting for rather than reading immediately.
                double settled = double.NaN;
                for (int waited = 0; waited < timeoutMs; waited += 25)
                {
                    settled = DurationSeconds;
                    if (!double.IsNaN(settled) && settled > 0) break;
                    Thread.Sleep(25);
                }

                if (double.IsNaN(settled) || settled <= 0) return "no duration was ever reported";

                // Windows' FLAC parser reports whole seconds, rounded down —
                // 19.505 comes back as 19, and the shell's own property handler
                // agrees, so it is the platform rather than this code. WAV is
                // exact. Both are accepted.
                bool close = Math.Abs(settled - expectedSeconds) <= 0.5;
                bool truncated = Math.Abs(settled - Math.Floor(expectedSeconds)) < 0.001;
                if (!close && !truncated)
                    return $"duration came back {settled:0.###}s, expected about {expectedSeconds:0.###}s";

                // The clock has to move, which is the one check that cannot be
                // satisfied by a player that merely holds a file open. A track
                // that reports itself playing while the position sits still is
                // exactly the failure this whole rewrite was chasing.
                double moved = 0;
                for (int waited = 0; waited < 3000 && moved <= 0; waited += 25)
                {
                    Thread.Sleep(25);
                    moved = PositionSeconds;
                }
                if (moved <= 0) return "the position never moved";

                if (TogglePlayPause() != "Paused") return "Pause did not pause";

                // "Playing <name>", not "Playing": resuming is a different event
                // from starting and says which track it picked back up.
                var resumed = TogglePlayPause();
                if (!resumed.StartsWith("Playing", StringComparison.Ordinal))
                    return "Play did not resume (said \"" + resumed + "\")";
                if (!IsPlaying) return "Play said it resumed and did not";

                // Speed is ours now, so this is a real check of the stretcher
                // being wired up rather than of a setter on somebody else's
                // object. Both directions, because only one of them extends the
                // buffer the stretcher reads through.
                foreach (int percent in new[] { 150, 50 })
                {
                    PlaybackRatePercent = percent;
                    if (PlaybackRatePercent != percent) return $"speed would not go to {percent}";

                    // Back to the start before each measurement, because otherwise
                    // this measures how long the fixture is.
                    //
                    // The checks above have already spent most of the track: up to
                    // three seconds waiting for the clock to move, then a pause and
                    // a resume — and the file the suite generates is under two
                    // seconds of audio. A track that has simply *finished* has a
                    // clock that does not move, which arrives here as "the clock
                    // stopped at 150 percent speed" — and 150 is the one that gets
                    // there first, because it is the one consuming the remainder
                    // half again as fast. Intermittent, blamed on the stretcher,
                    // and nothing at all to do with it.
                    SeekTo(0);

                    // A seek takes effect when the pump next reads it, so the
                    // position is asked for after that has had a chance to happen
                    // rather than before.
                    Thread.Sleep(100);

                    double before = PositionSeconds;
                    Thread.Sleep(300);
                    if (PositionSeconds <= before) return $"the clock stopped at {percent} percent speed";
                }

                PlaybackRatePercent = 100;

                RepeatTrack = true;
                if (!RepeatTrack) return "repeat did not take";
                RepeatTrack = false;
                if (RepeatTrack) return "repeat would not turn off";

                Stop();
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                PlaybackRatePercent = rateWas;
                VolumePercent = volumeWas;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Before the streaming teardown and before the engine: this owns two
            // threads and a device, and they have to be off the machine before
            // anything they might still call into goes away.
            StopDirect();

            StopStreaming();
            DropPending();

            lock (_gate)
            {
                _fadeGeneration++;
                _fading = false;
                try { _fadeTimer?.Dispose(); } catch { }
                _fadeTimer = null;
                try { _releaseTimer?.Dispose(); } catch { }
                _releaseTimer = null;

                _path = "";
            }
        }
    }
}










