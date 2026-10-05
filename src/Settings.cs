using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExplorerNative
{
    public enum SortColumn { Name, Size, Type, Modified }
    /// <summary>
    /// Stored as a number. "Never" was 0 and behaved exactly like OnDemand —
    /// nothing anywhere asked for it — so it is gone and the numbers kept.
    /// </summary>
    public enum FolderSizeMode { OnDemand = 1, Automatic = 2 }
    /// <summary>
    /// What a paste does about something that is already at the destination.
    ///
    /// <c>FillGaps</c> is appended rather than inserted, and that matters: these
    /// are stored in the settings file as numbers, so putting a new value in the
    /// middle would silently change what everybody's saved setting means.
    /// </summary>
    public enum PasteConflictPolicy
    {
        AutoRename,
        Overwrite,
        Skip,
        Ask,

        /// <summary>
        /// Copy only what is not there yet — go *into* folders that already
        /// exist and fill in the files that are missing.
        ///
        /// The difference from <see cref="Skip"/> is depth, and it is the whole
        /// point. Skip works on the names being pasted: a folder that is already
        /// there is skipped whole, so a thousand-file folder that copied six
        /// hundred files and stopped is skipped entirely on the next attempt and
        /// the four hundred never arrive. This merges into it and asks the
        /// question about every file underneath instead.
        /// </summary>
        FillGaps,
    }
    public enum DeleteMode { RecycleBin, Permanent }

    public class Settings
    {
        /// <summary>
        /// Bumped whenever a default changes in a way an existing settings file
        /// must be brought along with, rather than being left on the old value
        /// forever because it was written down once.
        /// </summary>
        public const int CurrentSettingsVersion = 13;

        /// <summary>
        /// What the window is called when nothing else has been chosen. Lives
        /// here rather than on Program so the settings can be built and tested
        /// without dragging in the entry point.
        /// </summary>
        public const string DefaultWindowTitle = "Explorer Native";

        /// <summary>
        /// Version of the file this was loaded from. Files written before this
        /// existed have no such property and so arrive as 0, which is exactly the
        /// signal the migration needs.
        /// </summary>
        public int SettingsVersion { get; set; }

        // ---------- Speech ----------
        public bool SpeakEnabled { get; set; } = true;
        public bool SpeakOperations { get; set; } = true;          // "copied file", "cut 3 items"

        // There is no SpeakPositionInList. It existed, defaulted to false, was
        // offered nowhere in the dialog and read nowhere in the app — a switch
        // that did nothing whichever way it was set. Position is NVDA's to
        // announce ("3 of 47", at the end of the row) and saying it again only
        // ever produced an echo, so the setting is gone rather than left lying
        // in the file implying otherwise.

        public bool InterruptSpeech { get; set; } = true;
        public bool SpeakErrors { get; set; } = true;
        public bool SpeakProgress { get; set; } = true;            // long copy progress milestones
        /// <summary>"Opening" and the name when launching a file. Off: the app that opens speaks for itself.</summary>
        public bool SpeakOnOpen { get; set; } = false;

        // ---------- Display ----------
        public bool ShowHiddenFiles { get; set; } = false;
        public bool ShowSystemFiles { get; set; } = false;
        public bool ShowExtensions { get; set; } = true;
        public bool FoldersFirst { get; set; } = true;
        public SortColumn SortBy { get; set; } = SortColumn.Name;
        public bool SortAscending { get; set; } = true;
        public SizeUnitStyle SizeUnits { get; set; } = SizeUnitStyle.FullWords;

        // There is no SizeDecimals or DateFormat setting. One decimal place and
        // the short date-and-time format are simply the right answers, and both
        // cost a row in a dialog that is walked one control at a time by keyboard.
        // See SizeFormatter.DefaultDecimals and TimeFormatter.DefaultFormat.
        /// <summary>On: exact date and time. Off: "today", "3 days ago", "a long time ago".</summary>
        public bool VerboseModifiedInfo { get; set; } = true;

        public int FontSize { get; set; } = 9;

        /// <summary>
        /// What the window is called — in its title bar, in the taskbar, and in
        /// the tray tooltip. Applied the moment it is changed.
        /// </summary>
        public string WindowTitle { get; set; } = DefaultWindowTitle;

        /// <summary>
        /// What to call this application when speaking to whoever is using it.
        ///
        /// One place, because there were six and only two of them obeyed the
        /// setting. The window and the tray tooltip were renamed; every tray
        /// balloon, the "ready" announcement, the restore and the quit still said
        /// "Explorer Native" — so somebody who had renamed the window was told
        /// about it by an application with a different name, which is the sort of
        /// thing that reads as a second program having something to say.
        ///
        /// [JsonIgnore] because it is derived, not stored. Without it the
        /// serialiser writes it into settings.json as a seventh copy of the same
        /// string, and the round-trip test then has a property it can set and
        /// never read back.
        /// </summary>
        [JsonIgnore]
        public string DisplayTitle =>
            string.IsNullOrWhiteSpace(WindowTitle) ? DefaultWindowTitle : WindowTitle.Trim();

        // ---------- Behaviour ----------
        public bool WrapArrowNavigation { get; set; } = false;

        /// <summary>
        /// How long typed letters keep building one search word before the next
        /// keystroke starts a new one. See <see cref="TypeAheadBuffer"/>.
        /// </summary>
        public int TypeAheadMilliseconds { get; set; } = TypeAheadBuffer.DefaultTimeoutMilliseconds;
        public bool SpeakTabSwitch { get; set; } = true;
        public bool ConfirmDelete { get; set; } = true;
        public DeleteMode Delete { get; set; } = DeleteMode.RecycleBin;
        public PasteConflictPolicy PasteConflict { get; set; } = PasteConflictPolicy.AutoRename;
        public bool RememberLastFolder { get; set; } = true;

        // RestoreWindowPosition was here, and it was always true — a window that
        // comes back where you left it is not a preference, it is what a window
        // does. The size and position are still saved and still restored; there
        // is simply no switch for it, and one fewer control between the top of
        // the Behaviour page and the ones that decide something.

        public bool CloseToTray { get; set; } = true;
        public bool SelectFirstItemOnNavigate { get; set; } = true;

        // ---------- Folder sizes ----------
        public FolderSizeMode FolderSizes { get; set; } = FolderSizeMode.OnDemand;
        public int FolderSizeTimeoutSeconds { get; set; } = 30;

        // FolderSizeMaxDepth was here. Sixty-four levels is deeper than Windows
        // will let a path go in the first place, so the only values that changed
        // anything were the ones that made the answer wrong — a folder measured
        // to depth 3 reports a size that is not its size, with nothing in the
        // number to say so. The timeout above is the honest ceiling: it bounds
        // the work without lying about the result. See
        // FolderSizeCalculator.MaxDepth.

        // Folder sizes are always cached. The cache is keyed on the folder's own
        // last-write time, so it invalidates itself the moment the folder changes
        // — there was never a case where turning it off was the right answer, only
        // a switch that made re-measuring an unchanged tree the user's problem.

        // ---------- Performance ----------

        // There is no thread-count setting either. The right number depends on
        // the machine, not on anyone's preference, so it is worked out from the
        // processor count — see FileOperations.RecommendedThreads.

        public int CopyBufferKilobytes { get; set; } = 1024;

        // ---------- Archives ----------
        //
        // Two settings and neither is in Preferences, because neither is a
        // preference in the sense that page is for: they are the last answers
        // given to the Compress dialog, remembered so the second archive does
        // not have to be told the same two things as the first. Somebody who
        // works in .tar.gz should not choose it every time, and somebody who
        // chose Smallest once for a backup should not have it silently applied
        // to a thirty gigabyte folder next week without seeing it — which is
        // why they are shown, filled in, in the dialog that uses them.
        //
        // There is no thread count here for the same reason there is none for
        // copying: the right number is a property of the machine. See
        // ArchiveEngine.RecommendedThreads.
        //
        // The format is stored as its id rather than as an enum so that a file
        // written by a version that knew about one more format still loads. It
        // is not clamped on the way in either — see the note on
        // GoogleDriveLetter below about the round-trip guarantee — and
        // MainForm falls back to zip for anything it does not recognise.
        public string ArchiveFormatId { get; set; } = "zip";
        public ArchiveLevel ArchiveCompression { get; set; } = ArchiveLevel.Balanced;
        // UseBackgroundEnumeration was here. A folder is always listed on a
        // worker now, and shown as it arrives; listing on the UI thread was a
        // way of freezing the window, not a preference.

        // ---------- Hotkey ----------
        public bool GlobalHotkeyEnabled { get; set; } = true;
        public uint HotkeyModifiers { get; set; } = HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT;
        public uint HotkeyKey { get; set; } = (uint)System.Windows.Forms.Keys.E;

        // ---------- Audio ----------

        /// <summary>
        /// The built-in player. It has no window — it is the global shortcuts
        /// below and what they say, and nothing else.
        /// </summary>
        public bool AudioPlayerEnabled { get; set; } = true;

        /// <summary>
        /// Enter on an audio file plays it here rather than handing it to
        /// whichever application owns the extension. Shift+Enter always opens it
        /// in that application, whatever this says.
        /// </summary>
        public bool PlayAudioInApp { get; set; } = true;

        public string AudioExtensions { get; set; } = AudioFiles.DefaultExtensions;

        // ---- Volume ----

        /// <summary>Persisted, because there is no window to set it in on the next run.</summary>
        public int AudioVolumePercent { get; set; } = 70;

        /// <summary>Milliseconds to come up from silence when a track starts or resumes. 0 for none.</summary>
        public int AudioFadeInMilliseconds { get; set; }

        /// <summary>Milliseconds to go down to silence before a pause takes effect. 0 for none.</summary>
        public int AudioFadeOutMilliseconds { get; set; }

        // ---- Seeking and speed ----

        public int AudioSeekSeconds { get; set; } = 10;

        /// <summary>The bigger jump, on its own pair of shortcuts.</summary>
        public int AudioLongSeekSeconds { get; set; } = 60;

        public int AudioPlaybackRatePercent { get; set; } = 100;

        // AudioSpeedStepPercent was here. The speed keys do not move by a
        // percentage any more and never really did: they step along
        // AudioPlayer.SpeedLadder, which is close together where a few percent
        // is audible and wide apart past double, where nobody is choosing
        // between 210 and 220. A step size set in percent could only ever put
        // the speed somewhere between two rungs of that ladder.

        // AudioLowLatencyStart was here. It set MF_MEDIA_ENGINE_REAL_TIME_MODE,
        // which went with the media engine — the buffering it shortened is now
        // the ring buffer in WasapiPlayback, and that is sized here rather than
        // asked of Windows. A value left in an old settings file is ignored on
        // load and dropped on the next save.

        // AudioDirectOutput was here. It chose between rendering the audio
        // ourselves and letting the media engine do it, and it stopped meaning
        // anything the moment the engine was removed: there is one path now, and
        // a tick box that cannot change what happens is worse than no tick box,
        // because somebody will turn it off to test a theory and conclude the
        // wrong thing when nothing changes.

        // AudioBypassSystemEffects was here, and it is now simply always asked
        // for. It is AUDCLNT_STREAMOPTIONS_RAW: shared mode still, so the device
        // is not taken away from anything else, and it only removes Windows'
        // enhancements from this stream's path. It is a request rather than a
        // demand — a device is free to refuse it, and playback carries on either
        // way — which is exactly why there was nothing for a tick box to decide.
        // WasapiRenderer.RawMode still reports what was actually granted.

        /// <summary>
        /// The limiter.
        ///
        /// Not a peak limiter — it does not stop anything clipping, and it is not
        /// meant to. What it does is lift whatever is quiet at that moment up
        /// towards the ceiling, so the room tone, the reverb tail, the breath
        /// before the line, all the things that live thirty or forty decibels
        /// under the music, come up where they can be heard. Loud passages are
        /// left exactly where the volume put them, which above 100 percent means
        /// they clip, and that is the sound of it.
        ///
        /// It gets more extreme the higher the volume goes, because the lift it
        /// is allowed grows with the gain — see WasapiRenderer.Limiter. At 100
        /// percent it is a gentle leveller; at 800 it is a different recording.
        /// </summary>
        public bool AudioLimiter { get; set; }

        /// <summary>
        /// What the limiter aims for, as a percentage of full scale.
        ///
        /// Not 100: a leveller that puts everything at exactly full scale leaves
        /// nothing for the transient that arrives after the envelope was
        /// measured, and the result is a continuous crunch rather than an open
        /// sound. Lower is safer and quieter; higher is louder and dirtier.
        /// </summary>
        public int AudioLimiterCeilingPercent { get; set; } = 90;

        /// <summary>
        /// How fast the limiter reacts, in microseconds — a five-hundredth of a
        /// second by default.
        ///
        /// Microseconds rather than milliseconds because the interesting part of
        /// the range is under one: a tenth of a millisecond and a whole one are
        /// audibly different settings, and a number in whole milliseconds could
        /// not tell them apart.
        /// </summary>
        public int AudioLimiterAttackMicroseconds { get; set; } = 500;

        /// <summary>How slowly it lets go again, in milliseconds.</summary>
        public int AudioLimiterReleaseMilliseconds { get; set; } = 250;

        /// <summary>
        /// Lift the quiet parts by the same amount at any volume.
        ///
        /// The limiter's lift normally grows with the square of the gain, which
        /// is what makes turning the volume up open the background further. That
        /// has one consequence nobody expects until they meet it: at or below
        /// 100 percent there is no gain to square, so the effect is at its
        /// mildest exactly where somebody sits down to find out what it does.
        ///
        /// On, the whole lift is available at any volume — including a very
        /// quiet one. This is the setting for listening *into* a recording
        /// rather than for making one loud.
        /// </summary>
        public bool AudioLimiterWholeRange { get; set; }

        /// <summary>Opens the limiter's three controls, to be set while listening.</summary>
        public string AudioLimiterDialogShortcut { get; set; } = "Ctrl+Alt+Shift+L";

        /// <summary>
        /// Whether the twenty-band equaliser is in the audio path.
        ///
        /// Off is a genuine bypass rather than a flat curve, so this is worth
        /// keeping separate from the band settings: it is what makes a curve
        /// comparable against no curve at all, which is the only way to tell
        /// whether one is an improvement.
        /// </summary>
        public bool AudioEqualiser { get; set; }

        /// <summary>
        /// Each band's setting in decibels, lowest frequency first. Twenty of
        /// them; see <see cref="Equaliser.Frequencies"/> for which is which.
        /// </summary>
        public int[] AudioEqualiserBands { get; set; } = new int[Equaliser.BandCount];

        /// <summary>Opens the equaliser, to be set while listening.</summary>
        public string AudioEqualiserDialogShortcut { get; set; } = "Ctrl+Alt+Shift+E";

        /// <summary>
        /// How far the pitch is shifted, in semitones. Twelve is an octave.
        /// Zero is the recording's own pitch, which is a true bypass.
        /// </summary>
        public int AudioPitchSemitones { get; set; }

        /// <summary>
        /// Whether shifting the pitch drags the tempo with it, the way a tape
        /// machine does. Off, the two are independent.
        /// </summary>
        public bool AudioPitchMovesTempo { get; set; }

        /// <summary>Opens the pitch control, to be set while listening.</summary>
        public string AudioPitchDialogShortcut { get; set; } = "Ctrl+Alt+Shift+H";

        /// <summary>
        /// Whether silence reduction is in the audio path.
        ///
        /// Off is a genuine bypass rather than a threshold nothing crosses, so
        /// it is worth keeping separate from the five numbers below it — the
        /// switch is what makes a setting comparable against no effect at all.
        /// </summary>
        public bool AudioSilenceReduction { get; set; }

        /// <summary>What counts as silence, in decibels below full scale.</summary>
        public int AudioSilenceThresholdDecibels { get; set; } = -48;

        /// <summary>How long a gap has to last before it is shortened.</summary>
        public int AudioSilenceMinimumMilliseconds { get; set; } = 400;

        /// <summary>How much of that gap is left behind.</summary>
        public int AudioSilenceLeaveMilliseconds { get; set; } = 150;

        /// <summary>How long the music takes to fade away into the gap.</summary>
        public int AudioSilenceFadeOutMilliseconds { get; set; } = 20;

        /// <summary>And how long it takes to come back out of it.</summary>
        public int AudioSilenceFadeInMilliseconds { get; set; } = 20;

        /// <summary>Opens silence reduction, to be set while listening.</summary>
        public string AudioSilenceDialogShortcut { get; set; } = "Ctrl+Alt+Shift+Q";

        // ---- Holding a key down ----

        // The three numbers that used to sit here — the wait before repeating,
        // the rate, and whether a hold accelerates — are now HoldRepeat.Delay,
        // HoldRepeat.Interval and an unconditional yes. They were measured into
        // place rather than chosen, and every one of them had exactly one right
        // answer:
        //
        // The wait is 200ms because a finger is on a key for about a tenth of a
        // second and Windows' own fastest keyboard setting is 250. Below a
        // tenth there is no such thing as a press — one tap of play played,
        // paused, played and paused again — and 400, which this shipped with,
        // reads as the key ignoring you before it gives in.
        //
        // The rate is 20ms because ten is the floor Windows will schedule a
        // timer at, and every action the player has costs a fraction of a
        // millisecond, so nothing above fifty a second was ever the limit.
        //
        // And acceleration is not optional, because without it the rate is
        // beside the point: one percent of volume fifty times a second still
        // takes two seconds to cross the dial. A tap is always exactly one step
        // whatever this does, so there was never anything to protect.

        /// <summary>Play the same track again when it ends instead of stopping.</summary>
        public bool AudioRepeatTrack { get; set; }

        /// <summary>
        /// Jump back this far when resuming from a pause, so picking a track up
        /// again does not drop you into the middle of a word. 0 for none.
        /// </summary>
        public int AudioRewindOnResumeSeconds { get; set; }

        // There is no "carry on where I left off", deliberately. Pressing Enter on
        // a track means play that track, from the start; a player that sometimes
        // begins two minutes in because of something you did an hour ago is one
        // you cannot predict. The rewind above is different — that is resuming
        // from a pause you just made, in the track you are already listening to.

        // ---- Reading ahead ----

        /// <summary>
        /// How much of the file under the cursor to read in the background, so
        /// that pressing Enter does not have to wait for it. 0 turns it off.
        ///
        /// Two megabytes because that is what the measurements said: on a NAS,
        /// half a megabyte made no difference and two took a cold start from
        /// about 1.3 seconds down to about 0.6.
        /// </summary>
        public int AudioPrefetchKilobytes { get; set; } = 2048;

        // Three switches went from here, and the things they switched are all
        // still on:
        //
        // Downloading the track into memory while it plays is what makes a skip
        // on the NAS cost a millisecond instead of half a second. Turning it off
        // was never a preference, it was a way of making skipping slow again.
        //
        // The memory ceiling is a gigabyte — see AudioPlayer.CacheBytes — and it
        // exists for the pathological track rather than the ordinary one, which
        // is tens of megabytes. Nothing accumulates: one track at a time, and
        // starting another releases the last.
        //
        // And reading ahead only from network drives is not a taste either. On a
        // local disk it buys nothing worth the reading and competes with
        // everything else that wants the disk, which is measurable in the wrong
        // direction.

        /// <summary>
        /// How long a track may sit paused before its memory is handed back.
        /// Nothing is unloaded — playing it again reads from the share and starts
        /// downloading afresh. 0 keeps it for ever.
        /// </summary>
        public int AudioReleaseAfterMinutes { get; set; } = 10;

        /// <summary>
        /// Whether Google Drive is put on a drive letter at startup.
        ///
        /// Off by default, and it must stay that way: this application is the
        /// shell handler for every folder on the machine, so it starts afresh
        /// constantly, and signing in to Google on the way in would be a network
        /// round trip nobody asked for.
        /// </summary>
        public bool GoogleDriveEnabled { get; set; }

        /// <summary>The letter to try first. The next free one is used if it is taken.</summary>
        public string GoogleDriveLetter { get; set; } = "G";

        /// <summary>
        /// The pairing code Explorer Native Connect on the iPhone has to send with every request. Made on first
        /// run; the tray's Phone connection item shows it. Not in Preferences: there is nothing to choose.
        /// </summary>
        public string ConnectCode { get; set; } = "";

        // ---------- What is spoken ----------

        /// <summary>
        /// Only the messages whose spoken-or-not differs from the default, as
        /// "id=1" or "id=0" separated by semicolons.
        ///
        /// A hundred and fifty entries written out in full would be a settings
        /// file nobody could read, and a migration every time one was added. An
        /// id that no longer exists is dropped on load, so a removed message
        /// leaves nothing behind.
        ///
        /// Renamed from NotificationOverrides, and deliberately: the numbers in
        /// it used to be a three-bit set of channels and are now one bit. Under
        /// the old name a saved 1 meant "status bar, silent" and would be read
        /// here as "speak", which is the one migration that cannot be got right
        /// by guessing. A new name means an old file's value is simply not
        /// found, and every message goes back to its default — which for the
        /// vast majority is the value it had anyway.
        /// </summary>
        public string SpeechOverrides { get; set; } = "";

        // ---- What it says ----

        /// <summary>
        /// Off. Pressing Enter on a track should play the track, not talk about
        /// playing the track — the silence that follows is the confirmation, and
        /// the name was just read out on the row that was activated.
        /// </summary>
        public bool AudioAnnounceStart { get; set; }

        /// <summary>Off, for the same reason: the music stopping is the announcement.</summary>
        public bool AudioAnnounceTrackEnd { get; set; }

        /// <summary>
        /// Play, pause, stop, mute, repeat and speed.
        ///
        /// Off. Every one of them is audible the instant it happens — the music
        /// starts, stops, goes quiet or changes speed — so saying it as well is
        /// narration over the thing being narrated.
        /// </summary>
        public bool AudioAnnounceTransport { get; set; }

        /// <summary>
        /// Off. A volume key held down is a stream of "forty-five percent, fifty
        /// percent" over the top of the music whose volume is being changed, and
        /// the change itself is the feedback.
        /// </summary>
        public bool AudioAnnounceVolume { get; set; }

        /// <summary>Off, for the same reason: the track jumping is the answer.</summary>
        public bool AudioAnnounceSeek { get; set; }

        /// <summary>
        /// Name the track by its Title tag rather than its file name. Read on a
        /// worker, so a file with no tags or a slow share costs nothing.
        /// </summary>
        public bool AudioAnnounceUsesTitleTag { get; set; }

        // ---- State ----

        /// <summary>
        /// The last track played. Not to resume it — nothing resumes — but to
        /// warm Media Foundation at startup, which is a per-process cost paid
        /// against whichever file happens to be handy.
        /// </summary>
        public string AudioLastPath { get; set; } = "";

        // Shortcuts are stored as text — "Ctrl+Alt+P" — so settings.json stays
        // something a person can read and edit. See Shortcut.Parse; anything it
        // does not understand is simply not registered.
        //
        // Ctrl+Alt is the pattern because the window's own hotkey is Ctrl+Alt+E,
        // so the whole application is reachable with one hand shape.
        //
        // Letters, not arrows. Ctrl+Alt with the four arrow keys is NVDA's table
        // navigation, and NVDA's keyboard hook sits above RegisterHotKey — so
        // those registrations succeed, report no conflict, and then never fire.
        // Up, Down, Back and Forward are what the letters stand for.
        public string AudioPlayPauseShortcut { get; set; } = "Ctrl+Alt+P";
        public string AudioStopShortcut { get; set; } = "Ctrl+Alt+S";
        public string AudioVolumeUpShortcut { get; set; } = "Ctrl+Alt+U";
        public string AudioVolumeDownShortcut { get; set; } = "Ctrl+Alt+D";
        public string AudioMuteShortcut { get; set; } = "Ctrl+Alt+M";
        public string AudioSeekBackwardShortcut { get; set; } = "Ctrl+Alt+B";
        public string AudioSeekForwardShortcut { get; set; } = "Ctrl+Alt+F";
        public string AudioWhatIsPlayingShortcut { get; set; } = "Ctrl+Alt+W";

        // One number each, on the digits: how far in, how far to go, how long.
        public string AudioSayElapsedShortcut { get; set; } = "Ctrl+Alt+1";
        public string AudioSayRemainingShortcut { get; set; } = "Ctrl+Alt+2";
        public string AudioSayTotalShortcut { get; set; } = "Ctrl+Alt+3";

        /// <summary>Opens the speed list. The speed keys above are the quick way.</summary>
        public string AudioSpeedDialogShortcut { get; set; } = "Ctrl+Alt+Shift+S";

        /// <summary>Opens the output device list.</summary>
        public string AudioDeviceDialogShortcut { get; set; } = "Ctrl+Alt+Shift+O";

        /// <summary>
        /// Which device to play through, as the endpoint id Windows gives it.
        ///
        /// Empty means follow the system default, which is the right default and
        /// not the same as naming whichever device is default today: plugging in
        /// headphones should move the music, and it only does if this is empty.
        /// An id belonging to something that is no longer plugged in falls back
        /// to the default rather than refusing to play.
        /// </summary>
        public string AudioOutputDeviceId { get; set; } = "";

        // The second rank, on the same letters with Shift added, so the long
        // skip sits under the finger already doing the short one.
        public string AudioSeekBackwardLongShortcut { get; set; } = "Ctrl+Alt+Shift+B";
        public string AudioSeekForwardLongShortcut { get; set; } = "Ctrl+Alt+Shift+F";
        public string AudioSpeedUpShortcut { get; set; } = "Ctrl+Alt+Shift+U";
        public string AudioSpeedDownShortcut { get; set; } = "Ctrl+Alt+Shift+D";
        public string AudioRestartTrackShortcut { get; set; } = "Ctrl+Alt+0";
        public string AudioRepeatShortcut { get; set; } = "Ctrl+Alt+R";

        // ---------- Startup ----------
        public bool StartWithWindows { get; set; } = true;
        public bool StartMinimised { get; set; } = false;

        // ---------- Shell integration ----------
        /// <summary>"Open in Explorer Native" on folders and drives.</summary>
        public bool RegisterContextMenu { get; set; } = false;

        /// <summary>
        /// When the command line last gave folders to this application, in UTC
        /// ticks. A running copy that queued a release while this file could not
        /// be read lets a later takeover stand; the file's own date cannot tell
        /// that apart from any other write.
        /// </summary>
        public long FoldersTakenAt { get; set; }

        /// <summary>
        /// Take over opening folders from File Explorer: double-clicking a folder,
        /// opening a drive, and Win+E all come here instead.
        ///
        /// Off by default, and deliberately so — it changes how the whole desktop
        /// behaves, which is not something an app should assume on first run.
        /// </summary>
        public bool SetAsDefaultFileExplorer { get; set; } = false;
        /// <summary>
        /// Where the exe was when we last wrote the registry. The app is meant to
        /// be portable, so if it has since been moved the stale paths are rewritten
        /// on next launch rather than pointing at a file that is no longer there.
        /// </summary>
        public string RegisteredExePath { get; set; } = "";

        // ---------- State (persisted, not user-editable in the dialog) ----------
        public string StartPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public string LastFolder { get; set; } = "";
        public string LastFolderTab2 { get; set; } = "";
        /// <summary>Which tab was in front, and what was focused in each — restored on restart.</summary>
        public int ActiveTab { get; set; }
        public string FocusedPathTab1 { get; set; } = "";
        public string FocusedPathTab2 { get; set; } = "";
        public int WindowX { get; set; } = -1;
        public int WindowY { get; set; } = -1;
        public int WindowWidth { get; set; } = 900;
        public int WindowHeight { get; set; } = 600;

        /// <summary>
        /// Folder passed on the command line (the shell verb supplies one).
        /// Deliberately not persisted: a one-off launch target must never
        /// rewrite the user's saved preferences.
        /// </summary>
        [JsonIgnore]
        public string? InitialFolderOverride { get; set; }

        /// <summary>
        /// Overrides where settings are read and written.
        ///
        /// Exists for the self-test, which exercises loading, clamping and
        /// migration against real files. Without it those tests ran against the
        /// user's own settings.json — backing it up and restoring it afterwards,
        /// which is fine right up until the application is running at the same
        /// time and writes its own copy over the top. That is not a risk worth
        /// taking with somebody's configuration to test a code path.
        /// </summary>
        [JsonIgnore]
        public static string? OverrideAppDataDir { get; set; }

        [JsonIgnore]
        public static string AppDataDir =>
            OverrideAppDataDir ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExplorerNative");

        [JsonIgnore]
        private static string SettingsPath => Path.Combine(AppDataDir, "settings.json");


        /// <summary>
        /// What a newer build wrote that this one has no property for.
        ///
        /// Kept only when the file came from a newer build, and written back out
        /// with everything else: going back a version and forward again used to
        /// cost every setting the newer build had added, because the older one
        /// saved the file without them. From an older or the same build the
        /// unknowns are settings that were removed, and those are dropped — the
        /// next save writing the file without them is how a removal is finished.
        /// </summary>
        [JsonExtensionData]
        public System.Collections.Generic.Dictionary<string, JsonElement>? UnknownSettings { get; set; }

        /// <summary>
        /// Set when the file on disk could not be read at all, so the first save
        /// keeps a copy before writing over it. See <see cref="Load"/>.
        /// </summary>
        [JsonIgnore]
        private bool _unreadableOnDisk;

        /// <summary>
        /// The file was there and could not be opened — held by another program
        /// — so these are the defaults standing in for settings nobody has seen.
        /// Nothing is reconciled against them, and they are not saved over it.
        /// </summary>
        [JsonIgnore]
        public bool UnreadableOnDisk => _unreadableOnDisk;

        /// <summary>
        /// How the file is written.
        ///
        /// The relaxed encoder is what keeps it readable. The default one escapes
        /// every character that could mean something in a web page — so
        /// "Ctrl+Alt+P" was written to the file as Ctrl+Alt+P, and a
        /// window title or path with an accent came out escaped too. The name says
        /// "unsafe" because it is unsafe to drop into HTML or a script; this is a
        /// settings file on disk, read only by the deserialiser above.
        /// </summary>
        private static readonly JsonSerializerOptions JsonFormat = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// How the file is read: forgiving of what a person editing it by hand
        /// leaves behind. A trailing comma or a comment is not a reason to lose
        /// every preference in the file.
        /// </summary>
        private static readonly JsonSerializerOptions ReadFormat = new()
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        private static readonly JsonDocumentOptions ReadDocument = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };

        /// <summary>
        /// The settings on disk, or the defaults — and never the defaults for a
        /// file that is merely damaged.
        ///
        /// It used to be all or nothing: one value of the wrong type, one stray
        /// comma, and the whole file was thrown away for defaults, which the next
        /// save then wrote over the original. So a value that will not read is
        /// dropped on its own and the rest kept; a file that will not parse at all
        /// is copied aside before anything can overwrite it; and a file that could
        /// not be *opened* — another process swapping it in at that instant — is
        /// tried again rather than read as missing.
        /// </summary>
        public static Settings Load() => Load(out _);

        /// <param name="failed">
        /// A file was there and nothing usable came out of it, so what came back
        /// is the defaults standing in for it.
        /// </param>
        public static Settings Load(out bool failed)
        {
            failed = false;
            string? json = null;
            bool present = false;
            bool wasPlain = false;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!File.Exists(SettingsPath)) break;
                    present = true;

                    // Shared for delete as well, so a save in another process can
                    // still swap its file in while this one is reading.
                    using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    // Encrypted for this Windows account; see ProtectedFile.
                    var bytes = buffer.ToArray();
                    json = ProtectedFile.Decode(bytes);
                    wasPlain = !ProtectedFile.IsProtected(bytes);
                    break;
                }
                catch (FileNotFoundException) { present = false; break; }
                // Encrypted for another Windows account, or damaged: unreadable,
                // and kept rather than overwritten, like any other.
                catch (System.Security.Cryptography.CryptographicException) { break; }
                catch (IOException) { System.Threading.Thread.Sleep(20 * (attempt + 1)); }
                catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(20 * (attempt + 1)); }
            }

            if (json == null)
            {
                var fresh = new Settings();
                // There, and unreadable after every attempt: keep it safe from the
                // first save rather than overwrite something we never saw.
                fresh._unreadableOnDisk = present;
                failed = present;
                return fresh;
            }

            RememberDisk(json);

            Settings? loaded;
            try
            {
                loaded = JsonSerializer.Deserialize<Settings>(json, ReadFormat);

                // Left in the clear by an older build — the one an update
                // replaced saves on its way out. Encrypted now, same content.
                if (wasPlain && loaded != null)
                {
                    try
                    {
                        var temp = $"{SettingsPath}.{Environment.ProcessId}.enc.tmp";
                        ProtectedFile.WriteAllText(temp, json);
                        File.Move(temp, SettingsPath, overwrite: true);
                    }
                    catch { }
                }
            }
            catch (Exception)
            {
                loaded = Salvage(json);
                if (loaded != null) KeepCopy("damaged");
            }

            if (loaded == null)
            {
                KeepCopy("unreadable");
                failed = true;
                return new Settings();
            }

            try { return loaded.Validated(); }
            catch
            {
                // Nothing in Validated is expected to throw; if something does, the
                // file is still kept rather than silently replaced.
                KeepCopy("unreadable");
                failed = true;
                return new Settings();
            }
        }

        /// <summary>
        /// Reads a file whose whole-file read failed, one property at a time,
        /// leaving out any that will not read. Null when it is not a JSON object
        /// at all.
        /// </summary>
        private static Settings? Salvage(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json, ReadDocument);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

                var kept = new System.Collections.Generic.List<JsonProperty>();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    try
                    {
                        var one = "{" + JsonSerializer.Serialize(property.Name) + ":" +
                                  property.Value.GetRawText() + "}";
                        JsonSerializer.Deserialize<Settings>(one, ReadFormat);
                        kept.Add(property);
                    }
                    catch
                    {
                        // This value is the problem; the rest are not.
                    }
                }

                var buffer = new System.Text.StringBuilder("{");
                for (int i = 0; i < kept.Count; i++)
                {
                    if (i > 0) buffer.Append(',');
                    buffer.Append(JsonSerializer.Serialize(kept[i].Name)).Append(':')
                          .Append(kept[i].Value.GetRawText());
                }
                buffer.Append('}');

                return JsonSerializer.Deserialize<Settings>(buffer.ToString(), ReadFormat);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Copies the settings file aside, once per kind of problem, so a person
        /// can see what they had. A copy, not a move: the original stays where it
        /// is until a save that has something better to put there.
        /// </summary>
        private static void KeepCopy(string why)
        {
            try
            {
                var copy = $"{SettingsPath}.{why}-{DateTime.Now:yyyyMMdd-HHmmss}";
                if (!File.Exists(copy)) File.Copy(SettingsPath, copy);
            }
            catch
            {
                // Nothing more can be done from here, and starting matters more.
            }
        }

        /// <summary>
        /// Brings a settings file written by an older build up to date.
        ///
        /// Only defaults that were *wrong* are forced, and each one is forced
        /// once. Anything the user has deliberately set since is left alone,
        /// because the version stamp records that the correction already
        /// happened.
        /// </summary>
        private void Migrate()
        {
            if (SettingsVersion >= CurrentSettingsVersion)
            {
                // From this build, the unknowns are removed settings; from a newer
                // one they are its settings, and are kept for it.
                if (SettingsVersion == CurrentSettingsVersion) UnknownSettings = null;
                return;
            }

            // An older file: anything unrecognised is a setting since removed.
            UnknownSettings = null;

            if (SettingsVersion < 3)
            {
                // The audio player first shipped with Ctrl+Alt and the four arrow
                // keys, which is NVDA's table navigation. NVDA's keyboard hook
                // takes those before RegisterHotKey is consulted, so all four
                // registered cleanly, reported no conflict, and did nothing at
                // all. Only a value still sitting on the broken default is moved;
                // anything chosen since is a decision, not a mistake to correct.
                if (AudioVolumeUpShortcut == "Ctrl+Alt+Up") AudioVolumeUpShortcut = "Ctrl+Alt+U";
                if (AudioVolumeDownShortcut == "Ctrl+Alt+Down") AudioVolumeDownShortcut = "Ctrl+Alt+D";
                if (AudioSeekBackwardShortcut == "Ctrl+Alt+Left") AudioSeekBackwardShortcut = "Ctrl+Alt+B";
                if (AudioSeekForwardShortcut == "Ctrl+Alt+Right") AudioSeekForwardShortcut = "Ctrl+Alt+F";
            }

            if (SettingsVersion < 4)
            {
                // The player used to say "Playing X" on Enter and "Finished X"
                // when a track ran out. Both talk over the thing they are
                // describing: the row was just read aloud, and music starting or
                // stopping is not something that needs narrating. Off, once, for
                // anyone who has the old value written down.
                AudioAnnounceTrackEnd = false;
                AudioAnnounceStart = false;
            }

            if (SettingsVersion < 5)
            {
                // The rest of it. A global shortcut that speaks is a shortcut
                // that talks over whatever the screen reader was already saying,
                // and the shortcuts worth having here all do something you can
                // hear: the volume moves, the track jumps, the music stops.
                // Asking what is playing still answers, and so does a key that
                // could not do anything.
                AudioAnnounceVolume = false;
                AudioAnnounceSeek = false;
                AudioAnnounceTransport = false;
            }

            // Version 7 corrected the hold-repeat rate and forced acceleration
            // on. Both of those are constants now — see the note where the
            // settings used to be — so there is nothing left for it to correct.

            if (SettingsVersion < 8)
            {
                // Formats the player can play that the list simply never
                // mentioned. Appended rather than replaced, so a list somebody
                // has pruned on purpose keeps its pruning.
                foreach (var extension in AudioFiles.LaterExtensions)
                {
                    if (!AudioFiles.IsAudio("x" + extension, AudioExtensions))
                        AudioExtensions = AudioExtensions.TrimEnd(';') + ";" + extension;
                }
            }

            if (SettingsVersion < 11)
            {
                // The MPEG-4 video containers, which the player can play the
                // audio of and never claimed. The same append as version 8, over
                // the same list, and it has to be its own block rather than a
                // widening of that one: everybody is already past 8, so a list
                // that grows is only ever delivered by a version that has not
                // run yet.
                //
                // Idempotent, so a file old enough to run both blocks ends up
                // with each extension exactly once.
                foreach (var extension in AudioFiles.LaterExtensions)
                {
                    if (!AudioFiles.IsAudio("x" + extension, AudioExtensions))
                        AudioExtensions = AudioExtensions.TrimEnd(';') + ";" + extension;
                }
            }

            if (SettingsVersion < 12)
            {
                // Matroska and AVI. Only these two: everybody is past 11, and
                // appending the whole later list again would put back what
                // somebody had pruned from it on purpose.
                foreach (var extension in AudioFiles.AddedInVersion12)
                {
                    if (!AudioFiles.IsAudio("x" + extension, AudioExtensions))
                        AudioExtensions = AudioExtensions.TrimEnd(';') + ";" + extension;
                }
            }

            if (SettingsVersion < 13)
            {
                // Audio CDs. The same append, for the same reason.
                foreach (var extension in AudioFiles.AddedInVersion13)
                {
                    if (!AudioFiles.IsAudio("x" + extension, AudioExtensions))
                        AudioExtensions = AudioExtensions.TrimEnd(';') + ";" + extension;
                }
            }

            // Version 9 moved a 400ms hold delay to 200. That setting is gone
            // too — HoldRepeat.Delay is 200 for everybody now.

            // Version 10 needs no migration and that is the point of the note.
            // Nine settings were removed in it, and a removed property does not
            // need bringing forward: the deserialiser ignores what it does not
            // recognise, and the next save writes the file without it. The
            // tenth change — notifications becoming speech — is why
            // SpeechOverrides has a different name from the property it
            // replaces, rather than a migration that would have to guess which
            // numbering a saved value came from.

            SettingsVersion = CurrentSettingsVersion;
        }

        /// <summary>
        /// Every string the file could have made null, put back to empty.
        ///
        /// These properties are declared as <c>string</c>, not <c>string?</c>, so
        /// every line of code that reads one is written against a value that is
        /// never null — and `"AudioPlayPauseShortcut": null` in settings.json
        /// makes it null anyway, because that is what the file says and the
        /// deserialiser has no opinion about nullable annotations.
        ///
        /// Most consumers turn out to survive it by luck rather than by design.
        /// One does not: <see cref="Migrate"/> calls <c>AudioExtensions.TrimEnd</c>,
        /// which throws, and <see cref="Load"/> catches everything and returns a
        /// default Settings — so a single null in the file silently threw away
        /// every preference in it, and the next save wrote the defaults back over
        /// the original. Nothing was reported, because from the outside it is just
        /// an application that came up with its settings reset.
        ///
        /// So this runs *before* Migrate, and it is by reflection rather than as a
        /// list of names for the same reason the round-trip test is: a string
        /// setting added next year is covered without anybody remembering. The
        /// nullability annotation is the rule — a property genuinely declared
        /// <c>string?</c> is meant to be null and is left alone.
        /// </summary>
        private static readonly System.Reflection.PropertyInfo[] NonNullableStrings =
            FindNonNullableStrings();

        private static System.Reflection.PropertyInfo[] FindNonNullableStrings()
        {
            var context = new System.Reflection.NullabilityInfoContext();
            var found = new System.Collections.Generic.List<System.Reflection.PropertyInfo>();

            foreach (var property in typeof(Settings).GetProperties(
                         System.Reflection.BindingFlags.Public |
                         System.Reflection.BindingFlags.Instance))
            {
                if (property.PropertyType != typeof(string)) continue;
                if (!property.CanRead || !property.CanWrite) continue;

                try
                {
                    if (context.Create(property).WriteState !=
                        System.Reflection.NullabilityState.Nullable)
                        found.Add(property);
                }
                catch
                {
                    // No annotation to read is not a reason to skip the property;
                    // an unexpected null is the thing being defended against.
                    found.Add(property);
                }
            }

            return found.ToArray();
        }

        private void NoNullStrings()
        {
            foreach (var property in NonNullableStrings)
                if (property.GetValue(this) == null) property.SetValue(this, "");
        }

        /// <summary>Clamps anything that could wedge the UI if hand-edited to nonsense.</summary>
        private Settings Validated()
        {
            // Before Migrate, which reads several of these and would throw on a
            // null — taking the whole settings file with it. See NoNullStrings.
            NoNullStrings();

            Migrate();

            FontSize = Math.Clamp(FontSize, 7, 24);

            // A date in the future would outrank every release for good.
            FoldersTakenAt = Math.Clamp(FoldersTakenAt, 0, DateTime.UtcNow.Ticks);
            if (!Enum.IsDefined(FolderSizes)) FolderSizes = FolderSizeMode.OnDemand;

            // Used as a list index by the Compress dialog, which threw on a
            // number from a hand-edited file before its guard was reached.
            if (!Enum.IsDefined(ArchiveCompression)) ArchiveCompression = ArchiveLevel.Balanced;
            CopyBufferKilobytes = Math.Clamp(CopyBufferKilobytes, 4, 16384);
            FolderSizeTimeoutSeconds = Math.Clamp(FolderSizeTimeoutSeconds, 1, 600);
            // Below about a tenth of a second no two keystrokes would ever join up;
            // above a few seconds a forgotten letter is still waiting to ambush the
            // next one.
            TypeAheadMilliseconds = Math.Clamp(TypeAheadMilliseconds, 100, 3000);
            WindowWidth = Math.Clamp(WindowWidth, 400, 10000);
            WindowHeight = Math.Clamp(WindowHeight, 300, 10000);
            ActiveTab = Math.Clamp(ActiveTab, 0, 1);

            AudioVolumePercent = Math.Clamp(AudioVolumePercent, 0, AudioPlayer.LoudestPercent);

            // GoogleDriveLetter is deliberately left alone, like the shortcuts.
            // Validating strings here breaks the round-trip guarantee the whole
            // settings file rests on — a value written must come back — and
            // GoogleDrive.PreferredLetter already falls back to G for anything it
            // cannot use. Rubbish costs a different letter, not a failure.
            AudioSeekSeconds = Math.Clamp(AudioSeekSeconds, 1, 300);
            AudioLongSeekSeconds = Math.Clamp(AudioLongSeekSeconds, 1, 3600);

            // Below a quarter speed nothing is intelligible and Media Foundation
            // stops resampling; above four times it is chipmunks.
            AudioPlaybackRatePercent = Math.Clamp(AudioPlaybackRatePercent, 25, 400);

            // The limiter's three shapes. Each bound is the point past which the
            // setting stops describing the thing it is named after: a ceiling of
            // nothing is silence, an attack under fifty microseconds follows the
            // waveform rather than its outline, and a release of five seconds is
            // not a release, it is a fader somebody left alone.
            AudioLimiterCeilingPercent = Math.Clamp(AudioLimiterCeilingPercent, 10, 100);
            AudioLimiterAttackMicroseconds = Math.Clamp(AudioLimiterAttackMicroseconds, 50, 100_000);
            AudioLimiterReleaseMilliseconds = Math.Clamp(AudioLimiterReleaseMilliseconds, 5, 5000);

            // The equaliser, out of a file people are invited to edit. A missing
            // or short list is filled in flat rather than rejected — half a curve
            // is still a curve — and a longer one is cut, because a twenty-first
            // band would be a setting for a slider that does not exist.
            var bands = new int[Equaliser.BandCount];
            for (int b = 0; b < bands.Length; b++)
                bands[b] = AudioEqualiserBands != null && b < AudioEqualiserBands.Length
                    ? Math.Clamp(AudioEqualiserBands[b],
                        -Equaliser.MaximumDecibels, Equaliser.MaximumDecibels)
                    : 0;
            AudioEqualiserBands = bands;

            // Silence reduction. Each bound is the point past which the
            // number stops describing the thing it is named after — a threshold
            // above minus three is the whole recording, and a gap shorter than
            // twenty milliseconds is not a gap, it is a waveform crossing zero.
            // The far ends are deliberately absurd rather than safe; see
            // SilenceReduction, which is where the range is argued.
            AudioSilenceThresholdDecibels = Math.Clamp(AudioSilenceThresholdDecibels,
                SilenceReduction.LowestThresholdDecibels,
                SilenceReduction.HighestThresholdDecibels);
            AudioSilenceMinimumMilliseconds = Math.Clamp(AudioSilenceMinimumMilliseconds,
                SilenceReduction.ShortestMinimumMilliseconds,
                SilenceReduction.LongestMinimumMilliseconds);
            AudioSilenceLeaveMilliseconds = Math.Clamp(AudioSilenceLeaveMilliseconds,
                0, SilenceReduction.LongestLeaveMilliseconds);
            AudioSilenceFadeOutMilliseconds = Math.Clamp(AudioSilenceFadeOutMilliseconds,
                0, SilenceReduction.LongestFadeMilliseconds);
            AudioSilenceFadeInMilliseconds = Math.Clamp(AudioSilenceFadeInMilliseconds,
                0, SilenceReduction.LongestFadeMilliseconds);

            // Two octaves each way, which is what the resampler will do without
            // turning the recording into a sound effect.
            AudioPitchSemitones = Math.Clamp(AudioPitchSemitones,
                AudioPlayer.LowestSemitones, AudioPlayer.HighestSemitones);

            // Ten seconds of fade is already absurd; it is the point at which a
            // hand-edited value stops being a fade and starts being a fault.
            AudioFadeInMilliseconds = Math.Clamp(AudioFadeInMilliseconds, 0, 10000);
            AudioFadeOutMilliseconds = Math.Clamp(AudioFadeOutMilliseconds, 0, 10000);
            AudioRewindOnResumeSeconds = Math.Clamp(AudioRewindOnResumeSeconds, 0, 30);

            // Zero is "do not read ahead"; the ceiling is one gigabyte,
            // past which this stops being a read-ahead and starts being a copy.
            AudioPrefetchKilobytes = Math.Clamp(AudioPrefetchKilobytes, 0, 1048576);

            AudioReleaseAfterMinutes = Math.Clamp(AudioReleaseAfterMinutes, 0, 1440);

            // An empty list would mean the player never takes anything, which is
            // what the "enable" tick box is for; emptying the box means "the ones
            // you shipped with".
            if (string.IsNullOrWhiteSpace(AudioExtensions)) AudioExtensions = AudioFiles.DefaultExtensions;

            // The shortcut strings are deliberately not touched. They are parsed
            // where they are used, and an unparseable one costs a shortcut rather
            // than being rewritten into something the user did not ask for.

            // A blank title leaves a window with no name, which is a window a
            // screen reader cannot announce and Alt+Tab cannot label.
            WindowTitle = WindowTitle?.Trim() ?? "";
            if (WindowTitle.Length == 0) WindowTitle = DefaultWindowTitle;
            if (WindowTitle.Length > 120) WindowTitle = WindowTitle[..120];

            return this;
        }

        /// <summary>
        /// Writes the file, and says whether it managed to.
        ///
        /// It returned void and swallowed the exception, which meant the window
        /// announced "Preferences saved" whether or not anything had been
        /// written — an announcement that could simply be false, which is worse
        /// than none at all. Callers that genuinely do not care still ignore the
        /// result; the one that tells somebody it worked now checks.
        /// </summary>
        public bool Save()
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);

                // Stamped on the way out, so a file this build wrote is never
                // migrated again on the next load. Never *lowered*: a file a newer
                // build wrote, saved by this one, would otherwise run that build's
                // migrations a second time when it came back — undoing whatever
                // somebody had set since.
                SettingsVersion = Math.Max(SettingsVersion, CurrentSettingsVersion);

                var json = JsonSerializer.Serialize(this, JsonFormat);

                // A file that could not be opened is somebody's settings, not
                // damage: written over, a folder launch while a backup tool held
                // the file replaced every preference with the defaults. Left
                // alone while it is still there; the running copy reads it again
                // once it can.
                if (_unreadableOnDisk)
                {
                    if (File.Exists(SettingsPath)) return false;
                    _unreadableOnDisk = false;
                }

                // Written beside the target and swapped in, so a crash or a
                // power cut mid-write cannot leave a half-file that silently
                // resets every preference on next launch.
                // Named per process. One shared ".tmp" is two processes writing
                // the same file: --register-default runs outside the
                // single-instance lock, so its save can interleave with the
                // running copy's save-on-exit, and the first Move publishes the
                // other's half-written buffer. Load catches the broken JSON and
                // hands back defaults, which is every preference silently reset.
                var temp = $"{SettingsPath}.{Environment.ProcessId}.tmp";
                try
                {
                    ProtectedFile.WriteAllText(temp, json);

                    // The swap is refused while another process has the file open
                    // without delete sharing — a reader in the moment of reading —
                    // so it is tried a few times before being called a failure.
                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
                            if (!ReplaceWhileRead(temp, SettingsPath))
                                File.Move(temp, SettingsPath, overwrite: true);
                            RememberDisk(json);
                            return true;
                        }
                        catch (Exception ex) when (attempt < 5 &&
                                                   ex is IOException or UnauthorizedAccessException)
                        {
                            System.Threading.Thread.Sleep(25 * (attempt + 1));
                        }
                    }
                }
                finally
                {
                    // Gone after a successful move; after a failed one it is a
                    // stray file in AppData for ever.
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
            catch
            {
                // Losing a settings write is not worth crashing over, but it is
                // worth saying so — the caller announces success otherwise.
                return false;
            }
        }

        public Settings Clone()
        {
            var copy = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this))!;

            // Not in the file, so not in the copy unless carried: Preferences
            // saves a copy, and without this a settings file that could not be
            // read at startup was overwritten with no copy kept.
            copy._unreadableOnDisk = _unreadableOnDisk;
            return copy;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(
            Microsoft.Win32.SafeHandles.SafeFileHandle file, int infoClass, IntPtr info, int size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string name, int access, FileShare share, IntPtr security, int disposition, int flags, IntPtr template);

        private const int FileRenameInfoEx = 22;
        private const int FILE_RENAME_FLAG_REPLACE_IF_EXISTS = 0x1;
        private const int FILE_RENAME_FLAG_POSIX_SEMANTICS = 0x2;

        /// <summary>
        /// Renames <paramref name="from"/> over <paramref name="to"/> even while
        /// something is reading <paramref name="to"/>, and says whether it did.
        ///
        /// An ordinary replace is refused while the target is open, however that
        /// reader shared it, so a save failed whenever another copy of the
        /// application happened to be loading the file at that instant. The POSIX
        /// rename (Windows 10 1809 and later, on NTFS) replaces the name and lets
        /// the reader finish on the file it opened, which is what Load's
        /// share-for-delete is there to allow. Anything else falls back to the
        /// ordinary move.
        /// </summary>
        private static bool ReplaceWhileRead(string from, string to)
        {
            try
            {
                var target = Path.GetFullPath(to);
                // DELETE access is what a rename needs, and FileStream never asks for it.
                using var handle = CreateFileW(Path.GetFullPath(from), 0x00010000 /* DELETE */ | 0x00100000 /* SYNCHRONIZE */,
                    FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
                if (handle.IsInvalid) return false;

                // FILE_RENAME_INFO_EX: Flags, RootDirectory, FileNameLength, FileName.
                int nameBytes = target.Length * 2;
                int size = 20 + nameBytes + 2;
                var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
                try
                {
                    unsafe
                    {
                        new Span<byte>((void*)buffer, size).Clear();
                    }
                    System.Runtime.InteropServices.Marshal.WriteInt32(buffer, 0,
                        FILE_RENAME_FLAG_REPLACE_IF_EXISTS | FILE_RENAME_FLAG_POSIX_SEMANTICS);
                    System.Runtime.InteropServices.Marshal.WriteIntPtr(buffer, 8, IntPtr.Zero);
                    System.Runtime.InteropServices.Marshal.WriteInt32(buffer, 16, nameBytes);
                    System.Runtime.InteropServices.Marshal.Copy(target.ToCharArray(), 0, buffer + 20, target.Length);
                    return SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, size);
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                return false;
            }
        }

        private static readonly object DiskLock = new();
        private static string? _diskPath;
        private static string? _diskJson;

        /// <summary>What this process last read from or wrote to the file.</summary>
        private static void RememberDisk(string json)
        {
            lock (DiskLock)
            {
                _diskPath = SettingsPath;
                _diskJson = json;
            }
        }

        /// <summary>
        /// The file as somebody else has just changed it, laid over what this
        /// process is running with.
        ///
        /// Taking the file wholesale threw away everything this session had not
        /// written yet — the volume, the speed, repeat, the window's place, the
        /// sort order, all of which are saved on the way out — so turning folder
        /// handling on from the command line put the volume back to wherever it
        /// was when the application started. Only what differs between the file
        /// as this process last saw it and the file now is taken.
        /// </summary>
        public static Settings ReloadOnto(Settings live)
        {
            Settings? before = null;
            lock (DiskLock)
            {
                if (_diskJson != null && string.Equals(_diskPath, SettingsPath, StringComparison.OrdinalIgnoreCase))
                {
                    try { before = JsonSerializer.Deserialize<Settings>(_diskJson, ReadFormat)?.Validated(); }
                    catch { before = null; }
                }
            }

            // A file that would not read is not a file full of defaults. Merged
            // as one, every setting that differed from its default was put back
            // to it — the folder takeover included, which the next reconcile then
            // handed back to File Explorer.
            var fresh = Load(out bool failed);
            if (failed) return live;

            // Never read here: the live values are defaults standing in for a
            // file that could not be opened, with this session's changes on
            // them. The file, with those changes laid over it — taken wholesale,
            // every change made meanwhile was lost. Whatever else has read the
            // file since: that reading is not what these values were based on,
            // and merging against it handed the unreadable copy back.
            if (live.UnreadableOnDisk)
                return WithChanges(fresh, new Settings(), live);

            return before == null ? fresh : WithChanges(live, before, fresh);
        }

        /// <summary>
        /// <paramref name="live"/>, with every setting that differs between
        /// <paramref name="before"/> and <paramref name="after"/> taken from
        /// <paramref name="after"/>.
        ///
        /// This is what OK in Preferences means. The dialog edits a copy taken
        /// when it opened, and handing that copy back wholesale put back every
        /// setting that had changed in the meantime without it — a volume key
        /// pressed while the dialog was up, a speed change, repeat — so pressing
        /// OK on a page nobody had touched moved the volume.
        /// </summary>
        public static Settings WithChanges(Settings live, Settings before, Settings after)
        {
            var merged = live.Clone();

            foreach (var property in typeof(Settings).GetProperties(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite) continue;
                if (property.GetIndexParameters().Length > 0) continue;
                if (Attribute.IsDefined(property, typeof(JsonIgnoreAttribute))) continue;

                var was = property.GetValue(before);
                var now = property.GetValue(after);
                if (SameValue(was, now)) continue;

                property.SetValue(merged, now is int[] numbers ? (int[])numbers.Clone() : now);
            }

            // The version is the file's business, not a setting anybody changed.
            merged.SettingsVersion = Math.Max(live.SettingsVersion, after.SettingsVersion);
            return merged;
        }

        private static bool SameValue(object? a, object? b)
        {
            if (a is int[] x && b is int[] y) return x.AsSpan().SequenceEqual(y);
            if (a is System.Collections.Generic.Dictionary<string, JsonElement> da &&
                b is System.Collections.Generic.Dictionary<string, JsonElement> db)
            {
                if (da.Count != db.Count) return false;
                foreach (var pair in da)
                    if (!db.TryGetValue(pair.Key, out var other) ||
                        pair.Value.GetRawText() != other.GetRawText()) return false;
                return true;
            }
            return Equals(a, b);
        }
    }
}




