using System;
using System.Collections.Generic;
using System.IO;

namespace ExplorerNative
{
    public enum AudioAction
    {
        PlayPause,
        Stop,
        VolumeUp,
        VolumeDown,
        Mute,
        SeekBackward,
        SeekForward,
        SeekBackwardLong,
        SeekForwardLong,
        RestartTrack,
        SpeedUp,
        SpeedDown,
        ToggleRepeat,
        WhatIsPlaying,
        SayElapsed,
        SayRemaining,
        SayTotal,
        SpeedDialog,
        DeviceDialog,
        LimiterDialog,
        EqualiserDialog,
        PitchDialog,
        SilenceDialog,
    }

    /// <summary>
    /// Which preference decides whether an action says anything.
    ///
    /// Grouped rather than one switch per action, because the useful question is
    /// "do I want to hear about the volume" and not "do I want to hear about
    /// volume up but not volume down".
    /// </summary>
    public enum AudioSay
    {
        /// <summary>Play, pause, stop, mute, repeat, speed.</summary>
        Transport,
        Volume,
        Seek,

        /// <summary>Asked for directly, so always answered.</summary>
        Always,
    }

    /// <summary>
    /// What the player is doing, taken all at once.
    ///
    /// The Audio menu needs several of these facts to label itself, and asking
    /// the player for them one at a time as the menu opens invites the state to
    /// change halfway down — a menu reading "Pause" above "Unmute" for a track
    /// that stopped in between.
    /// </summary>
    public sealed record AudioSnapshot(
        bool HasTrack, bool Playing, bool Muted, bool Repeating, string Path, string Name);

    /// <summary>
    /// One configurable global shortcut: what it is called, where it is kept,
    /// how it behaves when held, and which announcement preference governs what
    /// it says.
    ///
    /// <paramref name="AllowRepeat"/> means holding the key keeps firing it,
    /// driven by <see cref="HoldRepeat"/> rather than by the keyboard's own
    /// repeat, which is set for typing.
    /// </summary>
    public sealed record AudioActionInfo(
        AudioAction Action,
        string Label,
        bool AllowRepeat,
        AudioSay Say,
        Func<Settings, string> Get,
        Action<Settings, string> Set)
    {
        /// <summary>
        /// How often this one repeats while held, or 0 for the general setting.
        ///
        /// Almost everything wants the general setting, which is as fast as a
        /// timer goes. Only playback speed does not: its ladder is short enough
        /// that a hold would run off the end of it before the first rung was
        /// audible.
        /// </summary>
        public int RepeatMilliseconds { get; init; }

        /// <summary>
        /// How far the step is allowed to grow while held. 1 means no
        /// acceleration at all — the right answer for anything whose step is
        /// already large, since the growth is there to rescue a step of one.
        /// </summary>
        public int AccelerationCap { get; init; } = 20;

        /// <summary>
        /// How many repeats between each growth of the step. Smaller ramps
        /// harder.
        ///
        /// Volume wants a harder ramp than everything else because its range is
        /// ten times what it was: the dial goes to 1000 now, and a curve tuned
        /// for 0 to 100 takes ten times as long to cross it. The cap moved with
        /// it — 60 was chosen when the top was 300, and leaving it there turned
        /// a held key from a sweep into a countdown the moment the range grew.
        /// </summary>
        public int AccelerationEvery { get; init; } = 3;

        /// <summary>
        /// How long this one waits before it starts repeating, or 0 for the
        /// general setting.
        ///
        /// Volume takes less. The delay exists so a tap cannot be read as a hold,
        /// and the cost of getting that wrong is proportional to the damage one
        /// extra repeat does — for play/pause it is the difference between
        /// playing and paused, and for volume it is five percent.
        /// </summary>
        public int HoldDelayMilliseconds { get; init; }

        /// <summary>
        /// The Audio menu's wording, ampersand and all. Kept here with the rest
        /// of the action so the menu is built from the same list as the shortcuts
        /// and the preferences, and so the mnemonics can be checked for
        /// collisions by a test rather than by eye.
        /// </summary>
        public string MenuText { get; init; } = "";

        /// <summary>
        /// The wording for the other state of a toggle — "&amp;Play" against
        /// "&amp;Pause". Deliberately the same letter, so the key does not move
        /// under the hand when the state changes.
        /// </summary>
        public string? AlternateMenuText { get; init; }

        /// <summary>
        /// Which entry in <see cref="Notifications"/> decides where this action's
        /// answer goes.
        ///
        /// The catalogue supplies the routing, not the wording. The player knows
        /// the real volume, the real time and the real file name and this list
        /// does not, so the sentence still comes from the player — only the
        /// decision about whether anybody hears it comes from here.
        /// </summary>
        public string NotificationId { get; init; } = "";
    }

    /// <summary>
    /// The one list of audio shortcuts. Preferences builds its rows from it, the
    /// Audio menu builds its items from it, and the tray registers from it — so
    /// an action added here appears in all three without any of them being
    /// edited.
    ///
    /// There is no next or previous. This plays a file, not a playlist — the
    /// list of what to play next is the folder you are standing in, and it is
    /// already on screen with the keyboard in it.
    /// </summary>
    public static class AudioActions
    {
        /// <summary>
        /// Speed and skipping run on their own, slower clock.
        ///
        /// Playback speed has eighteen rungs in the whole ladder, from a quarter
        /// to four times. At fifty repeats a second a hold crosses all of them
        /// before a finger can leave the key, and each one restarts the decoder's
        /// rate. Eight a second walks the ladder at about the speed you can hear
        /// each rung, which is the point of nudging it by key at all.
        ///
        /// Skipping needs it for a different reason and needs it more. A seek
        /// costs about a millisecond now that the track plays out of memory,
        /// which is what the old throttle was removed for — but *cost* was never
        /// the problem with holding the key. Distance was. Fifty ten-second
        /// skips a second is five hundred seconds of track per second held, so a
        /// three minute song is over in under half a second of holding, and the
        /// end is where it stays: every press after that clamps to the same
        /// place and the key reads as dead. That is what "holding or hitting
        /// skip isn't perfect, hitting it twice keeps it at the same place"
        /// turned out to be — the first press had already run to the end.
        ///
        /// Eight a second is eighty seconds of track per second held. This
        /// application's own Preferences page has claimed skipping ran at that
        /// rate the whole time it was running at fifty.
        /// </summary>
        public const int NudgeRepeatMilliseconds = 120;

        /// <summary>
        /// Volume starts repeating sooner than the general setting.
        ///
        /// A wrongly-detected hold costs one extra step, and one extra step of
        /// volume is five percent — against play/pause, where it is the
        /// difference between playing and paused. So volume can afford to be
        /// twitchier, and it is the key most often held.
        /// </summary>
        public const int VolumeHoldDelayMilliseconds = 110;

        /// <summary>
        /// How far a held skip key may grow its step.
        ///
        /// Skipping used not to accelerate at all, on the grounds that a
        /// ten-second step is already large enough not to need rescuing. That
        /// reasoning assumed the step *is* ten seconds. It is whatever "Skip by"
        /// is set to, and at one second — a perfectly reasonable setting for
        /// finding your place in a podcast — a held key covered eight seconds of
        /// track per second and crossing a three minute song took twenty-two
        /// seconds of holding. Which is what "make skipping faster" meant.
        ///
        /// Twelve, so a one-second step reaches twelve and the held key covers
        /// about a hundred seconds of track per second, while a tap is still
        /// exactly one second. Acceleration only ever applies to a repeat, so
        /// the precise short skip that made somebody choose one second in the
        /// first place is untouched.
        /// </summary>
        public const int SeekAccelerationCap = 12;

        /// <summary>
        /// The same for the long skip, and lower for the obvious reason: it
        /// starts at a minute a press, so four is already four minutes a press
        /// and thirty-two minutes per second of holding.
        /// </summary>
        public const int LongSeekAccelerationCap = 4;

        public static readonly IReadOnlyList<AudioActionInfo> All = new[]
        {
            // Held down, this toggles as fast as the repeat rate allows. That is
            // a deliberate choice rather than an oversight: it is asked for, and
            // it is what the hold-repeat setting is there to control.
            new AudioActionInfo(AudioAction.PlayPause, "Play or pause", true, AudioSay.Transport,
                s => s.AudioPlayPauseShortcut, (s, v) => s.AudioPlayPauseShortcut = v)
                { NotificationId = "audio.pause", MenuText = "&Pause", AlternateMenuText = "&Play" },

            new AudioActionInfo(AudioAction.Stop, "Stop", false, AudioSay.Transport,
                s => s.AudioStopShortcut, (s, v) => s.AudioStopShortcut = v)
                { NotificationId = "audio.stop", MenuText = "&Stop" },

            // Held down, these keep moving. Anything with AllowRepeat false does
            // its thing once however long the key is held.
            new AudioActionInfo(AudioAction.VolumeUp, "Volume up", true, AudioSay.Volume,
                s => s.AudioVolumeUpShortcut, (s, v) => s.AudioVolumeUpShortcut = v)
                { NotificationId = "volume.up", MenuText = "Volume &up", AccelerationCap = 200, AccelerationEvery = 2, HoldDelayMilliseconds = VolumeHoldDelayMilliseconds },

            new AudioActionInfo(AudioAction.VolumeDown, "Volume down", true, AudioSay.Volume,
                s => s.AudioVolumeDownShortcut, (s, v) => s.AudioVolumeDownShortcut = v)
                { NotificationId = "volume.down", MenuText = "Volume &down", AccelerationCap = 200, AccelerationEvery = 2, HoldDelayMilliseconds = VolumeHoldDelayMilliseconds },

            new AudioActionInfo(AudioAction.Mute, "Mute or unmute", false, AudioSay.Transport,
                s => s.AudioMuteShortcut, (s, v) => s.AudioMuteShortcut = v)
                { NotificationId = "volume.muted", MenuText = "&Mute", AlternateMenuText = "Un&mute" },

            // Skipping repeats on the slower clock and does not accelerate. Both
            // halves are about distance rather than cost: at the general rate a
            // held key covers five hundred seconds of track per second, which is
            // a three minute song finished in under half a second of holding —
            // and then stuck at the end, where every further press clamps to the
            // same place. See NudgeRepeatMilliseconds.
            new AudioActionInfo(AudioAction.SeekBackward, "Skip backward", true, AudioSay.Seek,
                s => s.AudioSeekBackwardShortcut, (s, v) => s.AudioSeekBackwardShortcut = v)
                { NotificationId = "seek.back", MenuText = "Skip &backward",
                  RepeatMilliseconds = NudgeRepeatMilliseconds,
                  AccelerationCap = SeekAccelerationCap },

            new AudioActionInfo(AudioAction.SeekForward, "Skip forward", true, AudioSay.Seek,
                s => s.AudioSeekForwardShortcut, (s, v) => s.AudioSeekForwardShortcut = v)
                { NotificationId = "seek.forward", MenuText = "Skip &forward",
                  RepeatMilliseconds = NudgeRepeatMilliseconds,
                  AccelerationCap = SeekAccelerationCap },

            // The long skip is sixty seconds a press, so a held key covers eight
            // minutes a second even on the slow clock. That is the right speed
            // for the thing it exists for — moving through something an hour
            // long — and the wrong one for anything else, which is why it has
            // its own pair of shortcuts rather than being the same key faster.
            new AudioActionInfo(AudioAction.SeekBackwardLong, "Long skip backward", true, AudioSay.Seek,
                s => s.AudioSeekBackwardLongShortcut, (s, v) => s.AudioSeekBackwardLongShortcut = v)
                { NotificationId = "seek.back.long", MenuText = "Long skip bac&kward",
                  RepeatMilliseconds = NudgeRepeatMilliseconds,
                  AccelerationCap = LongSeekAccelerationCap },

            new AudioActionInfo(AudioAction.SeekForwardLong, "Long skip forward", true, AudioSay.Seek,
                s => s.AudioSeekForwardLongShortcut, (s, v) => s.AudioSeekForwardLongShortcut = v)
                { NotificationId = "seek.forward.long", MenuText = "Long skip for&ward",
                  RepeatMilliseconds = NudgeRepeatMilliseconds,
                  AccelerationCap = LongSeekAccelerationCap },

            new AudioActionInfo(AudioAction.RestartTrack, "Back to the start", false, AudioSay.Seek,
                s => s.AudioRestartTrackShortcut, (s, v) => s.AudioRestartTrackShortcut = v)
                { NotificationId = "audio.restart", MenuText = "Back to the s&tart" },

            // E and L rather than the obvious P and D: both were already taken by
            // Pause and Volume down, and two items sharing a letter means the key
            // stops acting and starts cycling.
            new AudioActionInfo(AudioAction.SpeedUp, "Speed up", true, AudioSay.Transport,
                s => s.AudioSpeedUpShortcut, (s, v) => s.AudioSpeedUpShortcut = v)
                { NotificationId = "audio.speed.up", RepeatMilliseconds = NudgeRepeatMilliseconds, AccelerationCap = 1 },

            new AudioActionInfo(AudioAction.SpeedDown, "Slow down", true, AudioSay.Transport,
                s => s.AudioSpeedDownShortcut, (s, v) => s.AudioSpeedDownShortcut = v)
                { NotificationId = "audio.speed.down", RepeatMilliseconds = NudgeRepeatMilliseconds, AccelerationCap = 1 },

            new AudioActionInfo(AudioAction.ToggleRepeat, "Repeat on or off", false, AudioSay.Transport,
                s => s.AudioRepeatShortcut, (s, v) => s.AudioRepeatShortcut = v)
                { NotificationId = "audio.repeat.on", MenuText = "&Repeat this track" },

            new AudioActionInfo(AudioAction.WhatIsPlaying, "Say what is playing", false, AudioSay.Always,
                s => s.AudioWhatIsPlayingShortcut, (s, v) => s.AudioWhatIsPlayingShortcut = v)
                { NotificationId = "audio.whatisplaying", MenuText = "Say what is pla&ying" },

            // One number each. "What is playing" says everything at once, which
            // is right when you have lost your bearings and wrong when you only
            // want to know how much is left.
            new AudioActionInfo(AudioAction.SayElapsed, "Say time elapsed", false, AudioSay.Always,
                s => s.AudioSayElapsedShortcut, (s, v) => s.AudioSayElapsedShortcut = v)
                { NotificationId = "position.elapsed", MenuText = "Time &into the track" },

            new AudioActionInfo(AudioAction.SayRemaining, "Say time remaining", false, AudioSay.Always,
                s => s.AudioSayRemainingShortcut, (s, v) => s.AudioSayRemainingShortcut = v)
                { NotificationId = "position.remaining", MenuText = "Time still t&o go" },

            new AudioActionInfo(AudioAction.SayTotal, "Say the total length", false, AudioSay.Always,
                s => s.AudioSayTotalShortcut, (s, v) => s.AudioSayTotalShortcut = v)
                { NotificationId = "position.total", MenuText = "Tot&al length" },

            // Opens a dialog, so holding it would open a stack of them.
            new AudioActionInfo(AudioAction.SpeedDialog, "Choose the playback speed", false, AudioSay.Always,
                s => s.AudioSpeedDialogShortcut, (s, v) => s.AudioSpeedDialogShortcut = v)
                { NotificationId = "audio.speed.dialog", MenuText = "Playba&ck speed…" },

            // The same shape, and a dialog for the same reason.
            new AudioActionInfo(AudioAction.DeviceDialog, "Choose the output device", false, AudioSay.Always,
                s => s.AudioDeviceDialogShortcut, (s, v) => s.AudioDeviceDialogShortcut = v)
                { NotificationId = "audio.device.dialog", MenuText = "Output de&vice…" },

            // And the same again, for the three numbers that shape the limiter.
            // They are not on the preferences page on purpose: an attack time is
            // only meaningful against music that is actually playing, so this
            // has to be reachable from where you are when you need it — which is
            // in the middle of a track with the keyboard in your hand.
            //
            // L, because P, S, U, D, M, B, F, K, W, T, C, V, R, Y, I, O, A, G
            // and N are all already taken by the menu items above. A shared
            // letter stops acting and starts cycling, which a test checks.
            new AudioActionInfo(AudioAction.LimiterDialog, "Set up the limiter", false, AudioSay.Always,
                s => s.AudioLimiterDialogShortcut, (s, v) => s.AudioLimiterDialogShortcut = v)
                { NotificationId = "audio.limiter.dialog", MenuText = "&Limiter…" },

            // Twenty sliders, and they belong here rather than in Preferences for
            // the reason every other entry in this group does, and rather more
            // strongly: a tone control is not a number you can decide on. The
            // whole point of the dialog is that it is open while music is
            // playing and each band lands on the sound as you arrow it.
            //
            // **Q, and it used to be E.** Silence reduction arrived later and
            // every letter in those two words was already an access key here —
            // the menu has taken P, S, U, D, M, B, F, K, W, T, C, V, L, R, Y, I,
            // O, A, G, N and H between them. J, Q, X and Z were what was left,
            // and "equaliser" is the only label in the whole menu that contains
            // one of them. So this word gave its E up and took the Q out of its
            // own middle, and silence reduction has the E.
            //
            // The *shortcut* did not move. Ctrl+Alt+Shift+E is in somebody's
            // settings.json and in somebody's fingers; an access key inside a
            // menu that is open is a much cheaper thing to change than a key that
            // works everywhere in Windows. They disagree now, and that is the
            // cheaper of the two disagreements.
            new AudioActionInfo(AudioAction.EqualiserDialog, "Open the equaliser", false, AudioSay.Always,
                s => s.AudioEqualiserDialogShortcut, (s, v) => s.AudioEqualiserDialogShortcut = v)
                { NotificationId = "audio.equaliser.dialog", MenuText = "E&qualiser…" },

            // Pitch, beside speed and on the same terms. The two are separate
            // controls because the audio path keeps them separate — the
            // stretcher changes length without pitch and the resampler changes
            // both, and dividing one by the other is what lets either move on
            // its own. See Resampler.
            //
            // H, because P is Pause, C is Playback speed, and every other
            // obvious letter in this menu is taken. A shared letter stops
            // acting and starts cycling, which a test checks.
            new AudioActionInfo(AudioAction.PitchDialog, "Change the pitch", false, AudioSay.Always,
                s => s.AudioPitchDialogShortcut, (s, v) => s.AudioPitchDialogShortcut = v)
                { NotificationId = "audio.pitch.dialog", MenuText = "Pitc&h…" },

            // Silence reduction, here rather than in Preferences on the same terms
            // as the limiter and the equaliser, and rather more strongly than
            // either: a threshold for what counts as silence is not a number
            // anybody can decide on, it is a thing you set while listening to the
            // gaps it is deciding about.
            //
            // E, which the equaliser above gave up for it — see the note there.
            // The access key is on the second word because S, R, D, U, C, T, I, O
            // and N are every other letter in "silence reduction" and every one
            // of them was already taken.
            new AudioActionInfo(AudioAction.SilenceDialog, "Silence reduction", false, AudioSay.Always,
                s => s.AudioSilenceDialogShortcut, (s, v) => s.AudioSilenceDialogShortcut = v)
                { NotificationId = "audio.silence.dialog", MenuText = "Silence r&eduction…" },
        };

        /// <summary>The two Audio menu entries that are not shortcut actions.</summary>
        public const string SongPropertiesMenuText = "Son&g properties…";
        public const string PreferencesMenuText = "Audio prefere&nces…";

        /// <summary>
        /// Hotkey ids, kept clear of <see cref="HotkeyManager.MainWindowHotkeyId"/>.
        /// </summary>
        public static int HotkeyId(AudioAction action) => 0xB100 + (int)action;

        public static AudioActionInfo? ByHotkeyId(int id)
        {
            foreach (var info in All)
                if (HotkeyId(info.Action) == id) return info;
            return null;
        }

        /// <summary>
        /// How much bigger a step gets the longer its key is held.
        ///
        /// A held key that moves the volume one percent at a time takes seconds
        /// to cross the dial — that is not a volume control, it is a countdown,
        /// and no repeat rate fixes it because ten milliseconds is as fast as a
        /// timer goes. The step grows every third repeat instead, so a tap is
        /// still exactly one step and a hold sweeps the whole range in well under
        /// a second. Skipping accelerates the same way, which is what makes
        /// reaching the middle of a long track quick.
        /// </summary>
        public static int Accelerated(int step, int repeat, bool accelerate, int cap = 20, int every = 3)
        {
            if (!accelerate || repeat <= 0 || cap <= 1) return step;
            if (every < 1) every = 1;
            return step * Math.Min(1 + repeat / every, cap);
        }

        public static AudioActionInfo For(AudioAction action)
        {
            foreach (var info in All)
                if (info.Action == action) return info;
            throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    /// <summary>
    /// Which files the built-in player takes.
    ///
    /// A list of extensions rather than an attempt to ask Windows what it can
    /// decode: there is no cheap way to ask, the answer changes with whatever
    /// codec packages are installed, and a file the engine turns out not to
    /// handle is recovered anyway — the failure hands it to the associated
    /// application, which is where it would have gone in the first place.
    /// </summary>
    public static class AudioFiles
    {
        /// <summary>
        /// FLAC and ALAC are the reason this player uses Media Foundation at all.
        /// Ogg and Opus are here on the same terms as the rest: Windows decodes
        /// them if the Web Media Extensions are installed, and hands the file
        /// over to whatever owns it if not.
        ///
        /// **`.mp4` and `.mov` are video containers and they belong here anyway.**
        /// A recording of a conversation is a recording of a conversation whether
        /// the camera was running or not, and for somebody listening rather than
        /// watching, handing it to a video player is handing it to a window with
        /// no keyboard route to the volume. They are the same MPEG-4 container
        /// `.m4a` already is — the registry points all three at byte-stream
        /// handler {271C3902-…} — and <see cref="AudioDecoder"/> deselects every
        /// stream and then selects only the first *audio* one, so a video track
        /// is never read rather than being skipped over.
        ///
        /// Shift+Enter still opens either of them in whatever owns the extension,
        /// which is the way to actually watch one.
        /// </summary>
        public const string DefaultExtensions =
            ".mp3;.flac;.m4a;.m4b;.m4r;.aac;.wav;.wma;.aif;.aiff;.alac;.ogg;.oga;.opus;.mka;" +
            ".mp4;.mov;.mkv;.avi;.cda";

        /// <summary>
        /// Extensions added after the list first shipped. Anyone already carrying
        /// the old default gets these appended once, rather than being left
        /// without formats the player can perfectly well play.
        ///
        /// Appending is idempotent — every migration that runs this checks first
        /// — so a settings file of any age ends up with all of them exactly once,
        /// and one somebody has pruned on purpose keeps its pruning for anything
        /// not on this list.
        /// </summary>
        public static readonly string[] LaterExtensions = { ".oga", ".m4r", ".mp4", ".mov", ".mkv", ".avi" };

        /// <summary>
        /// The two added in settings version 12 — Matroska and AVI video, for
        /// the sound in them. Media Foundation has a byte-stream handler for
        /// both, and the decoder reads only the first audio stream. A file
        /// whose audio Windows has no decoder for (Dolby in some MKVs) is handed
        /// to the application that owns it, as any unplayable file is.
        /// </summary>
        public static readonly string[] AddedInVersion12 = { ".mkv", ".avi" };

        /// <summary>
        /// Audio CD tracks, added in settings version 13. A <c>.cda</c> is a
        /// pointer to the audio rather than the audio; <see cref="CdTrackStream"/>
        /// reads the disc it points into.
        /// </summary>
        public static readonly string[] AddedInVersion13 = { ".cda" };

        /// <summary>
        /// Whether Media Foundation has anything at all registered that can open
        /// this kind of file — asked of the registry rather than assumed.
        ///
        /// This exists because the obvious advice is wrong on this machine, and
        /// wrong advice about a file that will not play is worse than none. The
        /// Web Media Extensions are the documented answer for Ogg and Opus, they
        /// are installed here (2.1.38.0, ARM64), and Opus still fails with
        /// MF_E_UNSUPPORTED_BYTESTREAM_TYPE — because that package registers its
        /// handlers *inside the package*, where only other packaged applications
        /// can see them. A plain desktop program gets nothing.
        ///
        /// So the message has to be able to tell the two cases apart: a format
        /// Windows could decode if something were installed, and one it cannot
        /// decode for us however much is installed. The byte-stream handler key
        /// is what distinguishes them, and it is a local registry read.
        /// </summary>
        public static bool WindowsHasHandlerFor(string? extension)
        {
            if (string.IsNullOrEmpty(extension)) return false;

            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows Media Foundation\ByteStreamHandlers\" +
                    extension.ToLowerInvariant());

                return key != null;
            }
            catch
            {
                // No answer is not the same as "no", and claiming a format is
                // unsupported because a registry read failed would be a
                // confident lie. The play attempt itself is the real test.
                return true;
            }
        }

        /// <summary>
        /// What to say about a file the player could not open, beyond the fact.
        /// Empty when there is nothing useful to add.
        ///
        /// Only for something that is actually an extension. A file with none at
        /// all fails for reasons that have nothing to do with codecs, and
        /// explaining the Web Media Extensions to somebody who pressed Enter on
        /// a file called "notes" is noise dressed up as help.
        /// </summary>
        public static string AdviceFor(string? extension)
        {
            if (string.IsNullOrEmpty(extension)) return "";
            if (extension.Length < 2 || extension[0] != '.') return "";

            // Ogg Vorbis and Opus are decoded here rather than by Windows, so a
            // failure on one of those is about the file and not about what is
            // installed. Saying "Windows has no decoder" would be true and
            // completely beside the point — it never had one, and it is not
            // being asked.
            if (OggDecoder.Handles(extension)) return "";

            // The same for AIFF, which is read here as the WAV it amounts to.
            if (AudioDecoder.IsAiffName(extension)) return "";

            if (WindowsHasHandlerFor(extension)) return "";

            return " Windows has no decoder for it that desktop applications can use, " +
                   "even with the Web Media Extensions installed. Shift+Enter opens it " +
                   "in whichever application does handle it.";
        }

        public static bool IsAudio(string path, string? configured)
        {
            var extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension)) return false;

            // Empty means the defaults, not "nothing is audio" — Settings puts the
            // default list back when the box is cleared, and a caller that has not
            // been through Settings must land in the same place.
            if (string.IsNullOrWhiteSpace(configured)) configured = DefaultExtensions;

            foreach (var raw in configured.Split(
                         new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = raw.Trim();
                if (candidate.Length == 0) continue;

                // Written with or without the dot; both mean the same thing to
                // anyone typing this into a settings box.
                if (candidate[0] != '.') candidate = "." + candidate;

                if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }
    }
}




