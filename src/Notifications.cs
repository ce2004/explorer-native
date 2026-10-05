using System;
using System.Collections.Generic;
using System.Linq;

namespace ExplorerNative
{
    /// <summary>
    /// Whether a message is spoken. Two states, because there are only two
    /// things a message can usefully do here.
    ///
    /// There used to be three channels and eight combinations of them — status
    /// bar, speech and a tray balloon. The balloon is gone: this application is
    /// driven by ear, and a Windows notification is a thing you have to be
    /// looking at a screen to know happened, so every one of them was either
    /// invisible or a duplicate of something already said. And the status bar
    /// stopped being a choice, because it never should have been one: writing a
    /// line into a bar nobody has to listen to costs nothing and interrupts
    /// nobody, so it happens for every message the application delivers.
    ///
    /// What is left is the only question anybody was ever really answering —
    /// do I want to hear this.
    /// </summary>
    public enum NotificationChannel
    {
        /// <summary>Shown in the status bar and never spoken.</summary>
        None = 0,

        /// <summary>Spoken through the screen reader, as well as shown.</summary>
        Speech = 1,
    }

    /// <summary>
    /// One thing the application can tell you, and where it goes by default.
    ///
    /// <paramref name="Template"/> is a composite format string;
    /// <paramref name="Example"/> is a set of arguments that fits it, so every
    /// notification can be rendered — and therefore tested, and previewed in
    /// Preferences — without waiting for the thing to actually happen.
    /// </summary>
    public sealed record NotificationInfo(
        string Id,
        string Name,
        string Category,
        NotificationChannel Default,
        string Template,
        object[] Example)
    {
        public string Render(params object[] args) =>
            string.Format(Template, args.Length == 0 ? Example : args);

        public string Preview() => Render();

        /// <summary>
        /// Whether the application actually raises this one yet.
        ///
        /// The catalogue was written first and wired afterwards, and for a while
        /// none of it was wired at all — 167 configurable messages and no code
        /// anywhere that consulted the setting before speaking. Marking what is
        /// live keeps that honest: Preferences says so, and a test compares this
        /// flag against the ids that really appear in the source, so it cannot
        /// drift in either direction.
        /// </summary>
        public bool Raised { get; init; }
    }

    /// <summary>
    /// Every message the application produces, in one list.
    ///
    /// It exists because they were scattered: a hundred and fifty strings spread
    /// across a dozen files, each deciding for itself whether to speak, whether
    /// to write to the status bar, and whether anybody could turn it off. Three
    /// of them could be silenced, the rest could not, and there was no way to
    /// find out what the application was even capable of saying without reading
    /// all of it.
    ///
    /// Gathered here, each one has an id, a category, a default, and an example
    /// that renders — which makes them configurable one at a time, previewable
    /// before they happen, and testable all at once.
    ///
    /// The defaults follow the rule the audio player already lived by: say less,
    /// not more. Anything you can already tell by other means is off; anything
    /// you asked for directly, or that went wrong, is on.
    /// </summary>
    public static class Notifications
    {
        public const string AudioTransport = "Audio: playing";
        public const string AudioVolume = "Audio: volume";
        public const string AudioPosition = "Audio: position";
        public const string AudioFiles = "Audio: files";
        public const string DriveConnection = "Google Drive: connection";
        public const string DriveOperations = "Google Drive: files";
        public const string FileOperations = "Files: operations";
        public const string Navigation = "Navigation";
        public const string Clipboard = "Clipboard and selection";
        public const string Searching = "Searching";
        public const string Application = "Application";

        // Five names collapse into two, because the three channels did.
        //
        // Off and Bar were both "not spoken" — one of them wrote the status bar
        // and one did not, and the bar is now written either way. Pop was a tray
        // balloon, which for somebody listening rather than looking was a message
        // that never arrived at all; the only honest replacement for it is
        // speech, so the handful of things that used it — the drive is ready, the
        // install worked, folders open here now — are spoken. Loud was speech
        // and a balloon together, and is just speech.
        //
        // The names are kept so the table below still reads as a table.
        private const NotificationChannel Off = NotificationChannel.None;
        private const NotificationChannel Bar = NotificationChannel.None;
        private const NotificationChannel Say = NotificationChannel.Speech;
        private const NotificationChannel Pop = NotificationChannel.Speech;
        private const NotificationChannel Loud = NotificationChannel.Speech;

        private static NotificationInfo N(
            string id, string name, string category, NotificationChannel channel,
            string template, params object[] example) =>
            new(id, name, category, channel, template, example);

        /// <summary>
        /// The same, for one the application actually raises. A test checks that
        /// exactly these appear in the source, so the two cannot drift.
        /// </summary>
        private static NotificationInfo Live(
            string id, string name, string category, NotificationChannel channel,
            string template, params object[] example) =>
            new(id, name, category, channel, template, example) { Raised = true };

        public static readonly IReadOnlyList<NotificationInfo> All = new[]
        {
            // ---- Audio: playing ----------------------------------------------
            Live("audio.play", "Started playing", AudioTransport, Bar, "Playing {0}", "Ashley Cooke - your place.flac"),
            Live("audio.pause", "Paused", AudioTransport, Bar, "Paused"),
            Live("audio.resume", "Resumed", AudioTransport, Bar, "Playing"),
            Live("audio.stop", "Stopped", AudioTransport, Bar, "Stopped"),
            Live("audio.ended", "Track finished", AudioTransport, Off, "Finished {0}", "your place.flac"),
            Live("audio.failed", "Track would not play", AudioTransport, Say, "Could not play {0}: {1}", "track.flac", "no decoder"),
            Live("audio.handedover", "Opened in another application", AudioTransport, Bar, "{0} opened in its usual application", "song.ogg"),
            Live("audio.repeat.on", "Repeat turned on", AudioTransport, Bar, "Repeating this track"),
            Live("audio.repeat.off", "Repeat turned off", AudioTransport, Bar, "Not repeating"),
            Live("audio.restart", "Back to the start", AudioTransport, Bar, "Back to the start"),
            Live("audio.nothing", "Nothing is loaded", AudioTransport, Say, "Nothing is loaded"),
            Live("audio.whatisplaying", "What is playing", AudioTransport, Say, "Playing {0}, {1} of {2}", "your place", "1:44", "3:02"),
            Live("audio.speed.up", "Playback faster", AudioTransport, Bar, "Speed {0} percent", 125),
            Live("audio.speed.down", "Playback slower", AudioTransport, Bar, "Speed {0} percent", 75),
            Live("audio.speed.normal", "Playback back to normal", AudioTransport, Bar, "Speed normal"),
            Live("audio.speed.limit", "Speed cannot go further", AudioTransport, Say, "Already at {0}", "400 percent, four times"),
            // "audio.speed.refused" was here — "FLAC will not play faster than
            // normal". True while the media engine owned the playback rate and
            // Windows' FLAC decoder refused everything above normal; the speed
            // is applied to decoded samples now and no format can refuse it.
            Live("audio.speed.dialog", "Speed chooser opened", AudioTransport, Off, "Playback speed"),
            Live("audio.device.dialog", "Output device chooser opened", AudioTransport, Off, "Output device"),
            Live("audio.limiter.dialog", "Limiter settings opened", AudioTransport, Off, "Limiter"),
            Live("audio.equaliser.dialog", "Equaliser opened", AudioTransport, Off, "Equaliser"),
            Live("audio.pitch.dialog", "Pitch control opened", AudioTransport, Off, "Pitch"),
            Live("audio.silence.dialog", "Silence reduction opened", AudioTransport, Off, "Silence reduction"),
            Live("audio.device.changed", "Output device changed", AudioTransport, Bar, "Playing through {0}", "Headphones"),
            Live("audio.device.failed", "Could not use that device", AudioTransport, Loud, "Could not play through {0}", "Headphones"),

            // ---- Audio: volume ------------------------------------------------
            Live("volume.up", "Volume up", AudioVolume, Off, "Volume {0} percent", 75),
            Live("volume.down", "Volume down", AudioVolume, Off, "Volume {0} percent", 65),
            Live("volume.max", "Volume at maximum", AudioVolume, Say, "Volume {0} percent, maximum", 1000),
            Live("volume.min", "Volume at minimum", AudioVolume, Say, "Volume 0 percent, minimum"),
            Live("volume.muted", "Muted", AudioVolume, Bar, "Muted"),
            Live("volume.unmuted", "Unmuted", AudioVolume, Bar, "Unmuted, volume {0} percent", 70),
            Live("volume.unmuted.auto", "Unmuted by turning up", AudioVolume, Bar, "Volume {0} percent, unmuted", 15),
            Live("volume.gain", "Above full volume", AudioVolume, Off, "Volume {0} percent, {1} times gain", 200, "2.0"),
            // "volume.gain.unavailable" was here. It existed because the volume
            // could only exceed full scale if Windows accepted a transform into
            // the media engine's pipeline, and on some machines it would not.
            // Rendering the samples ourselves means the answer is always yes, so
            // the message can never be true and an untrue message is worse than
            // a missing one. An override left in a settings file names an id
            // nothing knows and is dropped on load.
            Live("volume.curve", "Volume curve changed", AudioVolume, Bar, "Volume curve: {0}", "perceptual"),
            Live("volume.fade.in", "Fading in", AudioVolume, Off, "Fading in"),
            Live("volume.fade.out", "Fading out", AudioVolume, Off, "Fading out"),

            // ---- Audio: position ----------------------------------------------
            Live("seek.forward", "Skipped forward", AudioPosition, Off, "{0}", "1:12"),
            Live("seek.back", "Skipped backward", AudioPosition, Off, "{0}", "0:58"),
            Live("seek.forward.long", "Long skip forward", AudioPosition, Off, "{0}", "6:12"),
            Live("seek.back.long", "Long skip backward", AudioPosition, Off, "{0}", "2:04"),
            Live("seek.start", "At the beginning", AudioPosition, Bar, "At the start"),
            Live("seek.end", "At the end", AudioPosition, Bar, "At the end"),
            Live("seek.wrapped", "Skipped past the end, so it started again",
                 AudioPosition, Bar, "Starting over"),
            Live("position.elapsed", "Time elapsed", AudioPosition, Say, "{0} elapsed", "1:44"),
            Live("position.remaining", "Time remaining", AudioPosition, Say, "{0} remaining", "1:18"),
            Live("position.total", "Total time", AudioPosition, Say, "{0} long", "3:02"),
            Live("position.unknown", "Length not known yet", AudioPosition, Say, "Length not known yet"),
            Live("position.buffering", "Waiting for audio", AudioPosition, Bar, "Buffering"),

            // The counterpart, and the reason it exists: "Buffering" was raised
            // and never taken back, so the status bar sat reading it while the
            // track played perfectly. Sixteen seconds into a playing file it
            // still said the player was waiting for audio.
            Live("position.resumed", "Playing again after waiting", AudioPosition, Bar, "Playing"),
            Live("position.buffered", "Enough downloaded to skip freely", AudioPosition, Off, "{0} of {1} downloaded", "12 MB", "36 MB"),

            // ---- Audio: files --------------------------------------------------
            Live("audiofile.unsupported", "Format not supported", AudioFiles, Say, "Windows cannot decode {0}", ".opus"),
            Live("audiofile.missing", "File has gone", AudioFiles, Say, "{0} is no longer there", "song.flac"),
            Live("audiofile.locked", "File is in use", AudioFiles, Say, "{0} is open in another application", "song.flac"),
            Live("audiofile.tags", "Tags read", AudioFiles, Off, "{0} by {1}", "your place", "Ashley Cooke"),
            Live("audiofile.tags.none", "No tags", AudioFiles, Off, "{0} has no tags", "track.wav"),
            N("audiofile.tags.slow", "Tags are taking a while", AudioFiles, Off, "Still reading the tags"),
            Live("audiofile.cached", "Track held in memory", AudioFiles, Off, "{0} held in memory", "34 MB"),
            Live("audiofile.cache.released", "Memory released", AudioFiles, Off, "Released {0} after {1} minutes paused", "34 MB", 10),
            Live("audiofile.cache.full", "Track too big to hold whole", AudioFiles, Bar, "This track is bigger than {0}; that much of it is kept downloaded around where it is playing", "1 gigabyte"),
            Live("audiofile.prefetch", "Warming a track", AudioFiles, Off, "Reading ahead {0}", "song.flac"),
            Live("audiofile.remote", "Track is on a slow drive", AudioFiles, Off, "{0} is on a network drive", "song.flac"),
            N("audiofile.external", "Sent to another player", AudioFiles, Bar, "Opening {0} in its usual application", "song.flac"),

            // ---- Google Drive: connection --------------------------------------
            Live("drive.mounting", "Connecting to Drive", DriveConnection, Bar, "Connecting to Google Drive"),
            Live("drive.mounted", "Drive is ready", DriveConnection, Pop, "Google Drive is on {0}", "G:"),
            Live("drive.unmounted", "Drive disconnected", DriveConnection, Bar, "Google Drive disconnected"),
            Live("drive.offline", "Drive went offline", DriveConnection, Loud, "Google Drive is offline ({0})", "timed out"),
            Live("drive.online", "Drive came back", DriveConnection, Pop, "Google Drive is back online"),
            Live("drive.signin.needed", "Sign-in expired", DriveConnection, Loud, "Google Drive sign-in has expired. Open Preferences, Google Drive, to sign in again."),
            Live("drive.signin.opened", "Consent page opened", DriveConnection, Bar, "Waiting for you to allow access in the browser"),
            Live("drive.signin.done", "Signed in", DriveConnection, Pop, "Signed in to Google Drive as {0}", "you@example.com"),
            Live("drive.signin.refused", "Sign-in refused", DriveConnection, Loud, "Google Drive sign-in was refused: {0}", "access_denied"),
            Live("drive.signin.timeout", "Sign-in timed out", DriveConnection, Loud, "Google sign-in timed out. Press Connect Google Drive again."),
            Live("drive.credentials.missing", "No credentials", DriveConnection, Loud, "No client_secret json in {0}", "%APPDATA%\\ExplorerNative"),
            Live("drive.letter.taken", "Preferred letter unavailable", DriveConnection, Bar, "{0} was taken, using {1}", "G:", "H:"),
            Live("drive.letter.none", "No drive letter free", DriveConnection, Loud, "No drive letter is free for Google Drive"),
            Live("drive.root.stuck", "Old mount could not be cleared", DriveConnection, Bar, "A previous mount could not be removed; using {0}", "GoogleDrive-2"),
            Live("drive.quota", "Drive storage", DriveConnection, Bar, "{0} used of {1}", "656 GB", "5 TB"),
            Live("drive.quota.low", "Drive nearly full", DriveConnection, Loud, "Google Drive is nearly full: {0} left", "2.1 GB"),
            Live("drive.ratelimited", "Google is throttling", DriveConnection, Bar, "Google is rate limiting; slowing down"),

            // ---- Google Drive: files -------------------------------------------
            Live("sync.done", "Monitored folder synced", DriveOperations, Say, "{0} synced: {1}", "Music", "3 uploaded, 1 downloaded"),
            Live("sync.conflict", "Changed in both places", DriveOperations, Say, "{0}: {1} changed in both places; both copies kept", "Music", "notes.txt"),
            Live("sync.error", "Monitored folder could not sync", DriveOperations, Loud, "{0} could not sync: {1}", "Music", "access denied"),
            Live("sync.offline", "Drive monitor offline", DriveOperations, Say, "Drive monitor paused, offline"),
            Live("sync.paused", "Monitored folder is gone", DriveOperations, Loud, "{0} paused: its {1} folder is gone. Remove the sync or restore the folder.", "Music", "Google Drive"),
            Live("sync.start","A big sync starting", DriveOperations, Say, "{0}: {1}, {2} files", "Music", "214 gigabytes to download", "3,120"),
            Live("sync.progress", "Big sync progress", DriveOperations, Say, "{0}: {1} percent synced", "Music", 50),
            Live("sync.space", "Monitored folder out of space", DriveOperations, Loud, "{0} paused: {1} is out of space, needs {2} more", "Music", "C:", "30 gigabytes"),
            Live("drive.folder.loading", "Fetching a folder", DriveOperations, Bar, "Fetching {0} from Google Drive", "artists"),
            Live("drive.folder.loaded", "Folder fetched", DriveOperations, Off, "{0}: {1} items", "artists", 2118),
            Live("drive.folder.slow", "Folder is taking a while", DriveOperations, Bar, "{0} is large; still fetching", "artists"),
            Live("drive.folder.empty", "Folder is empty", DriveOperations, Bar, "{0} is empty", "audiobooks"),
            Live("drive.folder.failed", "Folder would not load", DriveOperations, Say, "Could not read {0}: {1}", "artists", "offline"),
            Live("drive.upload.start", "Upload started", DriveOperations, Bar, "Uploading {0} to Google Drive", "album.flac"),
            Live("drive.upload.progress", "Upload progress", DriveOperations, Off, "{0} percent of {1}", 40, "1 GB"),
            Live("drive.upload.done", "Upload finished", DriveOperations, Say, "Uploaded {0}", "album.flac"),
            Live("drive.upload.failed", "Upload failed", DriveOperations, Loud, "Could not upload {0}: {1}", "album.flac", "connection lost"),
            Live("drive.upload.resumed", "Upload resumed", DriveOperations, Bar, "Resuming {0} from {1}", "album.flac", "512 MB"),
            // The status bar rather than speech. A retry is the application
            // handling something rather than something going wrong, and a folder
            // of small files through a bad minute produces a good few of them —
            // spoken, that is the screen reader talking over everything else to
            // report a problem that is being dealt with.
            Live("drive.upload.retrying", "Upload being retried", DriveOperations, Bar, "Retrying {0}: {1}", "album.flac", "Google Drive is temporarily unavailable"),
            // Spoken, unlike the retries. A transfer that has stopped and is
            // waiting for the internet looks exactly like one that has hung, and
            // this is the sentence that tells them apart — it is worth
            // interrupting for, because the alternative is somebody watching a
            // progress window that has not moved for four minutes.
            Live("drive.offline.waiting", "Waiting for the internet", DriveOperations, Say, "Waiting for the internet to come back before {0}", "sending album.flac"),
            Live("drive.offline.back", "Back online", DriveOperations, Say, "Back online; carrying on"),
            Live("drive.upload.cancelled", "Upload cancelled", DriveOperations, Say, "Upload of {0} cancelled", "album.flac"),
            Live("drive.download.start", "Fetching a file", DriveOperations, Off, "Fetching {0}", "song.flac"),
            Live("drive.download.done", "File fetched", DriveOperations, Off, "{0} ready", "song.flac"),
            Live("drive.trash", "Moved to Drive trash", DriveOperations, Say, "{0} moved to the Google Drive trash", "song.flac"),
            Live("drive.trash.many", "Several moved to trash", DriveOperations, Say, "{0} items moved to the Google Drive trash", 12),
            Live("drive.trash.failed", "Could not trash", DriveOperations, Loud, "Could not delete {0}: {1}", "song.flac", "permission denied"),
            Live("drive.folder.created", "Folder created in Drive", DriveOperations, Say, "Created {0} in Google Drive", "New folder"),
            Live("drive.renamed", "Renamed in Drive", DriveOperations, Say, "Renamed to {0}", "album (2).flac"),
            Live("drive.moved", "Moved within Drive", DriveOperations, Say, "Moved {0} to {1}", "song.flac", "archive"),

            // Sharing. Two ids for the copy rather than one with a flag, because
            // the whole point of the second command is that it changed something
            // — a spoken "Copied the Drive link" after granting the world read
            // access would be the one sentence that matters left unsaid.
            Live("drive.link.asking", "Asking for a link", DriveOperations, Bar, "Asking Google Drive for a link"),
            Live("drive.link.copied", "Drive link copied", DriveOperations, Say, "Copied the Drive link"),
            Live("drive.link.public", "Public link copied", DriveOperations, Say, "Copied a public link"),
            Live("drive.link.failed", "Could not get a link", DriveOperations, Loud, "Could not get a link: {0}", "permission denied"),

            // "drive.folders.unsupported" was here — "Cannot copy folders to
            // Google Drive yet; {0} skipped". Folders upload now, so the message
            // is gone rather than left saying something untrue. An id that no
            // longer exists is dropped out of SpeechOverrides on load, so nobody
            // carries a preference for a message that cannot happen.

            // ---- Files: operations ----------------------------------------------
            Live("copy.start", "Copy started", FileOperations, Bar, "Copying {0} items", 24),
            Live("copy.done", "Copy finished", FileOperations, Say, "Copied {0} items", 24),
            Live("copy.failed", "Copy failed", FileOperations, Loud, "Copy failed: {0}", "the disk is full"),
            Live("copy.partial", "Copy partly finished", FileOperations, Loud, "Copied {0} of {1}; {2} failed", 20, 24, 4),
            Live("copy.cancelled", "Copy cancelled", FileOperations, Say, "Copy cancelled after {0} items", 9),
            Live("move.start", "Move started", FileOperations, Bar, "Moving {0} items", 5),
            Live("move.done", "Move finished", FileOperations, Say, "Moved {0} items", 5),
            Live("move.failed", "Move failed", FileOperations, Loud, "Move failed: {0}", "the file is in use"),
            Live("move.cancelled", "Move cancelled", FileOperations, Say, "Move cancelled after {0} items", 3),
            Live("delete.recycled", "Sent to the Recycle Bin", FileOperations, Say, "{0} sent to the Recycle Bin", "notes.txt"),
            Live("delete.recycled.many", "Several recycled", FileOperations, Say, "{0} items sent to the Recycle Bin", 7),
            Live("delete.permanent", "Deleted permanently", FileOperations, Say, "{0} deleted permanently", "notes.txt"),
            Live("delete.failed", "Delete failed", FileOperations, Loud, "Could not delete {0}: {1}", "notes.txt", "access denied"),
            Live("delete.cancelled", "Delete cancelled", FileOperations, Bar, "Delete cancelled"),
            Live("rename.done", "Renamed", FileOperations, Say, "Renamed to {0}", "notes-old.txt"),
            Live("rename.failed", "Rename failed", FileOperations, Loud, "Could not rename: {0}", "a file with that name exists"),
            Live("rename.invalid", "Name not allowed", FileOperations, Say, "{0} cannot be used in a name", "a colon"),
            Live("folder.created", "Folder created", FileOperations, Say, "Created {0}", "New folder"),
            Live("shortcut.created", "Shortcut created", FileOperations, Say, "Created shortcut {0}",
                "notes.txt - Shortcut.lnk"),
            N("folder.exists", "Name already used", FileOperations, Say, "{0} already exists here", "New folder"),
            // ---- Files: archives ----
            //
            // Compressing borrows the transfer window and therefore the transfer
            // vocabulary: a verb at the start, a count at the end, the failures
            // counted separately from the items because a single unreadable
            // folder is thousands of files. What it adds is the ratio, because
            // that is the one number somebody made an archive to find out and
            // the only place it is ever available is the moment it finishes.
            Live("archive.start", "Compressing started", FileOperations, Bar, "Compressing {0} items", 24),
            Live("archive.done", "Archive created", FileOperations, Say, "Created {0}, {1} smaller", "photos.zip", "43 percent"),
            Live("archive.partial", "Archive partly created", FileOperations, Loud, "Created {0}; {1} items could not be read", "photos.zip", 2),
            Live("archive.failed", "Could not compress", FileOperations, Loud, "Could not compress: {0}", "the disk is full"),
            Live("archive.cancelled", "Compressing cancelled", FileOperations, Say, "Compressing cancelled"),
            Live("archive.unknown", "Not an archive", FileOperations, Say, "{0} is not an archive this can open", "notes.txt"),
            Live("extract.start", "Extracting started", FileOperations, Bar, "Extracting {0}", "photos.zip"),
            Live("extract.done", "Extracted", FileOperations, Say, "Extracted {0} items to {1}", 24, "photos"),
            Live("extract.partial", "Partly extracted", FileOperations, Loud, "Extracted {0} items; {1} failed", 20, 4),
            Live("extract.failed", "Could not extract", FileOperations, Loud, "Could not extract: {0}", "the archive is damaged"),
            Live("extract.cancelled", "Extracting cancelled", FileOperations, Say, "Extracting cancelled"),

            Live("conflict.overwrite", "Overwriting", FileOperations, Bar, "Replacing {0}", "song.flac"),
            Live("conflict.rename", "Renamed to avoid a clash", FileOperations, Bar, "Saved as {0}", "song (2).flac"),
            Live("conflict.skip", "Skipped an existing file", FileOperations, Bar, "Skipped {0}", "song.flac"),
            N("permission.denied", "Permission denied", FileOperations, Loud, "Access denied: {0}", "C:\\Windows\\System32"),
            N("disk.full", "Disk full", FileOperations, Loud, "Not enough room on {0}", "E:"),
            N("path.toolong", "Path too long", FileOperations, Loud, "That path is too long for Windows"),

            // ---- Navigation -------------------------------------------------------
            Live("nav.empty", "Folder is empty", Navigation, Say, "{0} is empty", "Downloads"),
            Live("nav.gone", "Folder disappeared", Navigation, Loud, "{0} disappeared, going to {1}", "Old", "Documents"),
            Live("nav.denied", "Folder cannot be opened", Navigation, Loud, "Cannot open {0}: {1}", "System Volume Information", "access denied"),
            N("nav.slow", "Folder is slow to open", Navigation, Bar, "{0} is taking a while", "\\\\nas\\music"),
            Live("nav.offline", "Network folder unreachable", Navigation, Loud, "{0} is not responding", "\\\\nas\\music"),
            N("nav.history.back", "Went back", Navigation, Off, "Back to {0}", "Documents"),
            N("nav.history.forward", "Went forward", Navigation, Off, "Forward to {0}", "Downloads"),
            Live("nav.refreshed", "Folder refreshed", Navigation, Off, "Refreshed, {0} items", 42),
            Live("nav.changed", "Folder changed underneath", Navigation, Off, "{0} changed", "Downloads"),
            Live("nav.tab", "Switched pane", Navigation, Say, "{0}", "Right pane"),
            Live("nav.sorted", "Sort order changed", Navigation, Bar, "Sorted by {0}", "date modified"),

            // Searching. The count is spoken and the rest is not: a results list
            // cannot be checked by ear — the rows were never read as they were
            // gathered — so how many there are is the one fact nothing else can
            // tell you, exactly as it is for a selection. "Searching" is said
            // because the answer can be seconds away on Drive and a key that
            // appears to have done nothing is a key somebody presses again.
            Live("search.started", "Search started", Navigation, Say, "Searching for {0}", "love"),
            Live("search.results", "Search finished", Navigation, Say, "{0} results for {1}", 12, "love"),
            Live("search.truncated", "Too many results to show", Navigation, Say,
                 "{0} results for {1}, and there are more", 10000, "love"),
            Live("search.none", "Search found nothing", Navigation, Say,
                 "Nothing matching {0} in {1}", "love", "music"),
            Live("search.left", "Left the results", Navigation, Off, "Left the results for {0}", "love"),
            Live("search.cancelled", "Search stopped", Navigation, Say, "Search stopped"),
            Live("search.nowhere", "Nothing to search", Navigation, Say,
                 "There is nothing to search in the drive list"),
            Live("search.failed", "Search failed", Navigation, Say, "Could not search: {0}", "the network is down"),

            // ---- Clipboard and selection -------------------------------------------
            Live("clip.copied", "Copied to the clipboard", Clipboard, Say, "Copied {0} items", 3),
            Live("clip.cut", "Cut to the clipboard", Clipboard, Say, "Cut {0} items", 3),
            N("clip.pasted", "Pasted", Clipboard, Say, "Pasted {0} items", 3),
            Live("clip.empty", "Nothing to paste", Clipboard, Say, "The clipboard is empty"),
            Live("clip.path", "Path copied", Clipboard, Say, "Copied the path"),
            Live("clip.nothing", "Nothing selected", Clipboard, Say, "Nothing selected"),
            Live("select.all", "Everything selected", Clipboard, Say, "Selected all {0} items", 42),
            N("select.none", "Selection cleared", Clipboard, Say, "Selection cleared"),
            N("select.invert", "Selection inverted", Clipboard, Say, "Inverted, {0} selected", 18),
            // On, unlike the rest of this group, and for a measured reason:
            // Shift+arrow and Ctrl+Space say nothing at all by themselves. The
            // control reports those as a change to a range rather than a change
            // of focus, which is not something a screen reader announces, so
            // building a selection by keyboard was silent from first key to last.
            // This is the only account of it there is.
            Live("select.count", "Selection changed", Clipboard, Say,
                "{0} selected, {1} selected", "song.flac", 3),

            // ---- Searching -----------------------------------------------------------
            Live("find.typed", "Jumped to a name", Searching, Off, "{0}", "Program Files"),
            Live("find.nomatch", "Nothing matches", Searching, Say, "Nothing starts with {0}", "zz"),
            Live("find.wrapped", "Wrapped to the top", Searching, Off, "Wrapped to the top"),
            N("find.cleared", "Search text cleared", Searching, Off, "Search cleared"),
            Live("size.calculating", "Working out a folder size", Searching, Bar, "Measuring {0}", "Program Files"),
            Live("size.done", "Folder size", Searching, Say, "{0} is {1}", "Program Files", "14.2 GB"),

            // ---- Application ----------------------------------------------------------
            Live("app.started", "Application started", Application, Off, "Explorer Native ready"),
            Live("app.minimised", "Minimised to the tray", Application, Bar, "Minimised to the notification area"),
            Live("app.restored", "Window restored", Application, Off, "Explorer Native"),
            Live("app.quitting", "Closing", Application, Off, "Closing"),
            Live("settings.saved", "Preferences saved", Application, Say, "Preferences saved"),
            Live("settings.reset", "Preferences reset", Application, Loud, "Preferences reset to their defaults"),
            Live("settings.failed", "Preferences could not be saved", Application, Loud, "Could not save preferences: {0}", "access denied"),
            Live("hotkey.registered", "Shortcut registered", Application, Off, "{0} registered", "Ctrl+Alt+P"),
            Live("hotkey.failed", "Shortcut refused", Application, Loud, "{0} is already used by another application", "Ctrl+Alt+P"),
            Live("hotkey.duplicate", "Shortcut already used here", Application, Say, "{0} is already used by {1}", "Ctrl+Alt+P", "Play or pause"),
            N("hotkey.silent", "Shortcut may be intercepted", Application, Bar, "{0} registered, but a screen reader may take it first", "Ctrl+Alt+Right"),
            Live("install.done", "Installed", Application, Pop, "Installed to {0}", "%LOCALAPPDATA%\\Programs\\ExplorerNative"),
            Live("install.failed", "Install failed", Application, Loud, "Could not install: {0}", "access denied"),
            Live("shell.default.on", "Now opens folders", Application, Pop, "Explorer Native now opens folders"),
            Live("shell.default.off", "No longer opens folders", Application, Pop, "Windows Explorer opens folders again"),
            // Refused rather than waited on: two shell calls per selected item,
            // measured at 8ms each, so a few thousand files is half a minute of
            // the window not responding before anything appears.
            Live("shell.menu.slow", "Windows menu refused for a large selection", Navigation, Loud,
                "Windows would take too long to build a menu for {0} items. " +
                "Copy, Cut, Paste, Delete and Rename in this application have no such limit.", 12000),
            Live("shell.menu.on", "Context menu entry added", Application, Bar, "\"Open in Explorer Native\" added"),
            Live("shell.menu.off", "Context menu entry removed", Application, Bar, "\"Open in Explorer Native\" removed"),
            Live("startup.on", "Starts with Windows", Application, Bar, "Explorer Native will start with Windows"),
            Live("startup.off", "No longer starts with Windows", Application, Bar, "Explorer Native will not start with Windows"),
            Live("instance.handoff", "Folder opened in the running window", Application, Off, "Opened {0}", "Downloads"),
            Live("speech.unavailable", "Screen reader not found", Application, Bar, "NVDA is not running; nothing will be spoken"),
            Live("speech.restored", "Screen reader found", Application, Bar, "NVDA found"),
            Live("error.generic", "Something went wrong", Application, Loud, "{0}", "Something went wrong"),
        };

        /// <summary>
        /// The catalogue by id, built once.
        ///
        /// This is looked up on the keystroke path, which is what makes it worth
        /// having: every announcement asks for an id and then asks again for its
        /// channels, and type-ahead raises one per letter typed. A walk of a
        /// hundred and sixty-eight entries with a case-insensitive compare each,
        /// twice per keystroke, is not free — and it grows every time a message
        /// is added, which is the wrong direction for a list that exists to be
        /// added to.
        ///
        /// TryAdd rather than an indexer, so a duplicate id keeps the first
        /// entry, exactly as the FirstOrDefault this replaces did. Whether there
        /// are duplicates at all is a different question, and a test already asks
        /// it.
        /// </summary>
        private static readonly Dictionary<string, NotificationInfo> Index = BuildIndex();

        private static Dictionary<string, NotificationInfo> BuildIndex()
        {
            var map = new Dictionary<string, NotificationInfo>(All.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var info in All) map.TryAdd(info.Id, info);
            return map;
        }

        public static NotificationInfo? ById(string? id) =>
            id != null && Index.TryGetValue(id, out var info) ? info : null;

        /// <summary>
        /// Categories in the order they should appear.
        ///
        /// Built once. It was a LINQ chain on every read — a walk of the whole
        /// catalogue, a hash set and a fresh list — and the dialog reads it
        /// inside a loop over itself and again on every arrow press to find out
        /// which page to show.
        /// </summary>
        public static IReadOnlyList<string> Categories { get; } =
            All.Select(n => n.Category).Distinct().ToList();

        /// <summary>
        /// The channels a notification should actually use, taking the saved
        /// overrides into account.
        /// </summary>
        public static NotificationChannel ChannelsFor(string id, Settings settings)
        {
            var info = ById(id);
            if (info == null) return NotificationChannel.None;

            var overrides = CachedOverrides(settings.SpeechOverrides);
            return overrides.TryGetValue(info.Id, out var channel) ? channel : info.Default;
        }

        private static readonly object OverrideGate = new();
        private static string? _parsedText;
        private static Dictionary<string, NotificationChannel>? _parsed;

        /// <summary>
        /// The overrides for a settings string, parsed once per distinct string.
        ///
        /// Same reason as the index above, only worse: this ran on every single
        /// announcement and re-split the text, allocated a dictionary and looked
        /// every id in it back up through the catalogue to decide whether to keep
        /// it. The text changes when somebody presses OK in Preferences, which is
        /// not often enough to pay for that on a keystroke.
        ///
        /// The result is shared, so it must not be handed to anyone who might
        /// write to it — <see cref="ParseOverrides"/> stays public and keeps
        /// returning a fresh one for the dialog, which does.
        /// </summary>
        private static Dictionary<string, NotificationChannel> CachedOverrides(string? text)
        {
            lock (OverrideGate)
            {
                if (_parsed != null && string.Equals(_parsedText, text, StringComparison.Ordinal))
                    return _parsed;

                _parsed = ParseOverrides(text);
                _parsedText = text;
                return _parsed;
            }
        }

        /// <summary>
        /// The saved overrides, as "id=channels" separated by semicolons.
        ///
        /// Only what differs from the default is stored. A hundred and fifty
        /// entries written out in full would be a settings file nobody could read
        /// and a migration problem every time one is added.
        /// </summary>
        public static Dictionary<string, NotificationChannel> ParseOverrides(string? text)
        {
            var result = new Dictionary<string, NotificationChannel>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return result;

            foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = part.IndexOf('=');
                if (equals <= 0) continue;

                var id = part[..equals].Trim();
                if (!int.TryParse(part[(equals + 1)..].Trim(), out int value)) continue;

                // An id that no longer exists is dropped rather than kept: a
                // notification that was removed should not leave a setting behind
                // that nothing will ever read again.
                if (ById(id) == null) continue;

                // One bit, because there is one question. A file written by an
                // older build stored three, and it is read under a different
                // property name — see Settings.SpeechOverrides — so nothing here
                // has to guess which numbering a value came from.
                result[id] = (NotificationChannel)(value & 1);
            }
            return result;
        }

        public static string FormatOverrides(IReadOnlyDictionary<string, NotificationChannel> overrides)
        {
            var parts = overrides
                .Where(kv => ById(kv.Key) is { } info && info.Default != kv.Value)
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key}={(int)kv.Value}");

            return string.Join(";", parts);
        }

        /// <summary>Plain English for a setting, for the list.</summary>
        public static string Describe(NotificationChannel channel) =>
            channel == NotificationChannel.Speech ? "Speak" : "Off";

        /// <summary>
        /// What the chooser offers: speak it, or do not.
        ///
        /// Eight combinations became two. Six of the eight named a tray balloon
        /// that no longer exists, and the two that did not — "status bar" and
        /// "off" — were the same thing to anybody listening, since the status
        /// bar is silent. A list of eight where six do nothing and two are
        /// identical is not a choice, it is a way of appearing to offer one.
        /// </summary>
        public static readonly IReadOnlyList<NotificationChannel> Choices = new[]
        {
            NotificationChannel.None,
            NotificationChannel.Speech,
        };
    }
}
