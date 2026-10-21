using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace ExplorerNative
{
    /// <summary>
    /// Plays Apple Music the sanctioned way: Apple's own MusicKit JS player,
    /// hosted in a hidden WebView2, decrypting and playing through the system's
    /// own browser engine with the person's subscription. The app never touches
    /// the protected audio; it only tells the player what to play and relays the
    /// keyboard commands (play, pause, stop, seek, volume, mute).
    ///
    /// Because Apple's player keeps the audio sealed, the processing chain
    /// (speed, pitch, EQ, limiter, silence removal) does not apply here. The
    /// output device is the one chosen in the Output device list, matched by
    /// its Windows name (setSinkId). Everything else about the player's
    /// controls is mirrored so it feels like the built-in player.
    ///
    /// Nothing is kept between runs: the WebView2 profile is a throwaway folder
    /// under %TEMP%, and the sign-in is injected afresh each time.
    ///
    /// Every play carries a number. Pressing Enter on five songs in a second
    /// starts five plays; only the newest may report an error, an end, or start
    /// the next song, so an older play called off by a newer one is never taken
    /// for the newer one failing or finishing.
    ///
    /// EXPLORERNATIVE_APPLEMUSICLOG names a file to log what the page reports
    /// (state, ends, errors; never a token), for testing playback by hand.
    /// </summary>
    internal static class AppleMusicPlayer
    {
        /// <summary>Where the player page is served from (see StartHostAsync): the origin Apple's web player token works from.</summary>
        private const string PageHost = "music.apple.com";
        private const string PageName = "explorer-native-player.html";

        /// <summary>How long the player may take to load before a play is given up on, with a sentence.</summary>
        private const int StartTimeoutMs = 25_000;

        /// <summary>A seek counts on from the last one aimed for this long, because the page's clock lags a seek.</summary>
        private const int SeekSettleMs = 1500;

        private static Form? _host;
        private static CoreWebView2Controller? _controller;
        private static int _browserProcessId;
        private static TaskCompletionSource<bool>? _ready;
        private static SynchronizationContext? _ui;
        private static readonly object Gate = new();

        // What is playing now.
        private static string _currentPath = "";
        private static string _currentName = "";
        private static bool _playing;
        private static double _positionSeconds;
        private static double _durationSeconds;
        private static int _volumePercent = 100;
        private static int _unmutedPercent = -1;
        private static int _generation;
        private static double _seekTarget = -1;
        private static long _seekAt;
        private static int _seekQueued;

        // The song queued in Apple's player after the current one (play-on).
        private static string? _queuedPath, _queuedId, _queuedName;

        // Play next: songs chosen to come after this one, first to play first,
        // and the list song the list goes on from once they have played.
        private static readonly List<string> _upNext = new();
        private static string? _anchor;

        // The rest of the list a song was chosen from, in the order it will play
        // (shuffled or not), written out when the song is chosen (4.1.6), so
        // shuffle, repeat and Up next have one queue to work on. Null for a
        // station, which grows as it plays and goes on through its folder.
        private static List<string>? _listQueue;
        private static string? _listFolder;

        /// <summary>The list songs played this time through, oldest first: what Previous track goes back to.</summary>
        private static readonly List<string> _history = new();

        public enum RepeatMode { Off, All, One }

        /// <summary>Shuffle and repeat, from settings (Preferences keeps them between runs).</summary>
        private static bool _shuffle;
        private static RepeatMode _repeat;

        public static bool Shuffle { get { lock (Gate) return _shuffle; } }
        public static RepeatMode Repeat { get { lock (Gate) return _repeat; } }

        /// <summary>Set at start from settings, without saying anything.</summary>
        public static void SetModes(bool shuffle, RepeatMode repeat)
        {
            lock (Gate) { _shuffle = shuffle; _repeat = repeat; }
        }

        /// <summary>What was queued before Play next took its place, in case Apple's player was already moving on to it.</summary>
        private static string? _displacedPath, _displacedId;

        /// <summary>Playing or paused as last asked (Enter, Space), which Space goes by; _playing is what the page last said.</summary>
        private static bool _wantPlaying;

        /// <summary>The list (or station) ran out after the song shown as current.</summary>
        private static bool _ended;

        /// <summary>The newest play that has made sound (or been paused on purpose), for the start watch.</summary>
        private static int _startedGen;

        /// <summary>The output device by its Windows name; empty follows the system default.</summary>
        private static string _deviceName = "";

        /// <summary>Recent crashes of the player, so a page that keeps failing is not restarted for ever.</summary>
        private static int _crashes;
        private static long _lastCrashAt;

        private static readonly string? LogPath = Environment.GetEnvironmentVariable("EXPLORERNATIVE_APPLEMUSICLOG");

        /// <summary>How a message to speak gets out, set once by the tray.</summary>
        public static Action<string, string>? Say { get; set; }

        /// <summary>True while a song is loaded in the Apple player (playing or paused).</summary>
        public static bool IsActive
        {
            get { lock (Gate) return _currentPath.Length > 0; }
        }

        public static string CurrentPath
        {
            get { lock (Gate) return _currentPath; }
        }

        /// <summary>"Title by Artist" of the song loaded, for the Audio menu.</summary>
        public static string CurrentName
        {
            get { lock (Gate) return _currentName; }
        }

        public static bool IsPlaying
        {
            get { lock (Gate) return _playing; }
        }

        /// <summary>Must be called once on the UI thread (the tray does this) so the host can live there.</summary>
        public static void BindUiThread() => _ui = SynchronizationContext.Current;

        // ---- Commands (any thread; marshalled to the UI) ----

        /// <summary>
        /// Start a song. <paramref name="path"/> is the ::applemusic row path (for
        /// play-on and "what is playing"); <paramref name="catalogId"/> is Apple's
        /// id; <paramref name="spokenName"/> is "Title by Artist".
        /// <paramref name="volumePercent"/>, when given, is the level to start at.
        /// </summary>
        /// <summary>Enter on a song (or anything else the person chose): the list's place moves to it.</summary>
        public static void Play(string path, string catalogId, string spokenName, int volumePercent = -1) =>
            Start(path, catalogId, spokenName, volumePercent, startAt: 0, retried: false, how: StartKind.Chosen);

        /// <summary>Why a song is being started, which decides what happens to the list's place and Play next.</summary>
        private enum StartKind
        {
            /// <summary>Enter: this song becomes the list's place, and comes off Play next if it was on it.</summary>
            Chosen,
            /// <summary>Playing on by itself: off Play next if it was first there, otherwise the list's place.</summary>
            PlayedOn,
            /// <summary>The same song again after a crash or a stall: nothing about the list changes.</summary>
            Restarted,
            /// <summary>Previous track, or a song played from Up next: the queue was set up by the caller.</summary>
            Back,
        }

        /// <summary>
        /// Starts a song, with the one after it in its list queued in Apple's
        /// player too, so MusicKit fetches it ahead and goes straight on to it
        /// (a quarter of a second between songs, where starting each afresh took
        /// up to two and a half). <paramref name="paused"/> loads it without
        /// sound (a crash while paused), to go on from <paramref name="startAt"/>.
        /// </summary>
        private static void Start(string path, string catalogId, string spokenName, int volumePercent, double startAt, bool retried,
            StartKind how, bool paused = false)
        {
            int mine;
            lock (Gate)
            {
                if (how == StartKind.Chosen)
                {
                    _upNext.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                    _anchor = path;
                    BuildListQueue(path);
                }
                else if (how == StartKind.PlayedOn) BecameCurrent(path);
            }
            // Repeat one plays this song again when it ends, so nothing is queued after it.
            var (nextPath, nextId, nextName) = Repeat == RepeatMode.One ? (null, null, null) : NextFor(path);
            lock (Gate)
            {
                mine = ++_generation;
                _currentPath = path; _currentName = spokenName;
                _queuedPath = nextPath; _queuedId = nextId; _queuedName = nextName;
                _displacedPath = null; _displacedId = null;
                _playing = false; _wantPlaying = !paused; _ended = false;
                _positionSeconds = startAt; _durationSeconds = 0; _seekTarget = -1;
                if (volumePercent >= 0) { _volumePercent = Math.Clamp(volumePercent, 0, 100); _unmutedPercent = -1; }
            }
            if (paused) Volatile.Write(ref _startedGen, mine);   // no sound is wanted, so none is waited for
            Interlocked.Increment(ref _idleTicket);   // something is loaded: no closing for idleness
            Log($"play #{mine} {catalogId} next {nextId ?? "none"}" + (paused ? " paused" : ""));
            OnUi(async () =>
            {
                try
                {
                    if (!await ReadyAsync().ConfigureAwait(true))
                    {
                        Reset();
                        Fail(mine, path, "Apple Music's player didn't start. Check the internet connection and try again.");
                        return;
                    }
                    if (Volatile.Read(ref _generation) != mine) return;   // a newer Enter already
                    // Read now, not when Start began: a pause or a Play next chosen
                    // while the player was still starting has to go with the song.
                    int volume; string device; string? queued; bool hold;
                    lock (Gate) { volume = _volumePercent; device = _deviceName; queued = _queuedId; hold = !_wantPlaying; }
                    await CallAsync("volume", Gain(volume)).ConfigureAwait(true);
                    await CallAsync("device", device).ConfigureAwait(true);
                    await CallAsync("play", catalogId, mine, queued, startAt, hold).ConfigureAwait(true);
                    WatchStart(mine, path, catalogId, spokenName, retried);
                }
                catch (AppleMusicException ex) { Reset(); Fail(mine, path, ex.Message); }
                catch (Exception ex) { Fail(mine, path, "Apple Music couldn't start the song: " + ex.Message); }
            });
        }

        /// <summary>
        /// The song to come after this one, ready to queue, or nulls: the first
        /// one chosen with Play next, otherwise the one after the list song the
        /// list goes on from.
        /// </summary>
        private static (string? Path, string? Id, string? Name) NextFor(string path)
        {
            try
            {
                string? next;
                bool listed;
                lock (Gate)
                {
                    next = _upNext.Count > 0 ? _upNext[0] : null;
                    listed = _listQueue != null;
                    if (next == null && _listQueue != null)
                    {
                        // Repeat all: an ended list starts over (shuffled afresh if shuffle is on).
                        if (_listQueue.Count == 0 && _repeat == RepeatMode.All && _listFolder != null && !AppleMusic.InStation(path))
                            Refill();
                        next = _listQueue.Count > 0 ? _listQueue[0] : null;
                    }
                }
                if (!listed) next ??= AppleMusic.NextSongAfter(ListPlace(path));
                if (next == null) return (null, null, null);
                return (next, AppleMusic.CatalogIdForPlaying(next), AppleMusic.SpokenName(next) ?? Path.GetFileName(next));
            }
            catch (AppleMusicException) { return (null, null, null); }
        }

        /// <summary>Where the list goes on from: the last list song played (a Play next song is not one).</summary>
        private static string ListPlace(string current)
        {
            lock (Gate) return _anchor ?? current;
        }

        /// <summary>A song is now the one playing: taken off Play next if it was first there, otherwise the list's place. Under Gate.</summary>
        private static void BecameCurrent(string path)
        {
            if (_upNext.Count > 0 && string.Equals(_upNext[0], path, StringComparison.OrdinalIgnoreCase))
            {
                _upNext.RemoveAt(0);
                if (_listQueue != null)
                {
                    // A Play next song that is in the list too: played now, not again later.
                    int i = _listQueue.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0) _listQueue.RemoveAt(i);
                }
                // A Play next song that was the list's own next song anyway: the
                // list goes on past it, rather than playing it a second time.
                else if (_anchor != null && string.Equals(AppleMusic.NextSongAfter(_anchor), path, StringComparison.OrdinalIgnoreCase))
                    _anchor = path;
                return;
            }
            _anchor = path;
            if (_listQueue != null)
            {
                int at = _listQueue.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) _listQueue.RemoveAt(at);
                _history.Add(path);
            }
        }

        /// <summary>
        /// Enter on a song: the rest of its list becomes the queue, in the order
        /// shown, or every other song of it shuffled. A station keeps no queue: it
        /// grows as it plays. Under Gate.
        /// </summary>
        private static void BuildListQueue(string path)
        {
            _history.Clear();
            var folder = Path.GetDirectoryName(path);
            var songs = folder == null || AppleMusic.InStation(path) ? null : AppleMusic.SongsShown(folder);
            int at = songs?.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) ?? -1;
            if (songs == null || at < 0) { _listQueue = null; _listFolder = null; return; }
            _listFolder = folder;
            _history.Add(path);
            if (_shuffle)
            {
                songs.RemoveAt(at);
                Shuffled(songs);
                _listQueue = songs;
            }
            else _listQueue = songs.Skip(at + 1).ToList();
        }

        /// <summary>Repeat all, at the end of the list: all of it again. Under Gate.</summary>
        private static void Refill()
        {
            var songs = AppleMusic.SongsShown(_listFolder!);
            if (songs == null) return;
            if (_shuffle) Shuffled(songs);
            _listQueue = songs;
            _history.Clear();
        }

        private static void Shuffled(List<string> songs)
        {
            for (int i = songs.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (songs[i], songs[j]) = (songs[j], songs[i]);
            }
        }

        /// <summary>
        /// Play next on a song: it plays after the one playing now, ahead of any
        /// chosen before it, and the list carries on afterwards. Returns what to
        /// say, or null when nothing is playing (the caller plays it now).
        /// </summary>
        public static string? PlayAfterCurrent(string path)
        {
            string id, name; int gen;
            try
            {
                id = AppleMusic.CatalogIdForPlaying(path);
                name = AppleMusic.SpokenName(path) ?? Path.GetFileName(path);
            }
            catch (AppleMusicException ex) { return ex.Message; }
            lock (Gate)
            {
                // Nothing playing, or the list has ended: it plays now instead.
                if (_currentPath.Length == 0 || _ended) return null;
                // Once in Play next is enough; choosing it again moves it to the front.
                _upNext.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                _upNext.Insert(0, path);
                // What was queued may be what Apple's player is moving on to at
                // this very moment; kept, so that move is still recognised.
                if (_queuedPath != null && _queuedId != id) { _displacedPath = _queuedPath; _displacedId = _queuedId; }
                _queuedPath = path; _queuedId = id; _queuedName = name;
                gen = _generation;
            }
            Log("play next " + id);
            OnUi(() => _ = CallAsync("requeue", id, gen));
            return name + " plays next";
        }

        /// <summary>
        /// Next track: the song that would play on after this one (a Play next
        /// choice first), now. Returns what to say: the song's name, or why not.
        /// </summary>
        public static string SkipToNext()
        {
            string path; lock (Gate) path = _currentPath;
            if (path.Length == 0) return "Nothing is playing";
            var (next, id, name) = NextFor(path);
            if (next == null || id == null)
            {
                var place = ListPlace(path);
                if (AppleMusic.InStation(place)) { PlayNextFromStation(place); return ""; }   // says the song it finds
                PlayAutoplay(path);   // says the song it finds, or "End of the list"
                return "";
            }
            Start(next, id, name ?? next, -1, startAt: 0, retried: false, how: StartKind.PlayedOn);
            Volatile.Write(ref _autoGen, Volatile.Read(ref _generation));   // an unavailable one is skipped too
            return name ?? next;
        }

        /// <summary>
        /// Previous track: the list song before this one, now. From a Play next
        /// song, back to the list song it came after. Returns what to say.
        /// </summary>
        public static string SkipToPrevious()
        {
            string path, place;
            string? previous = null;
            var how = StartKind.Chosen;
            lock (Gate)
            {
                path = _currentPath; place = _anchor ?? _currentPath;
                if (path.Length == 0) return "Nothing is playing";
                if (!string.Equals(path, place, StringComparison.OrdinalIgnoreCase))
                {
                    // From a Play next song, back to the list song it came after.
                    previous = place; how = StartKind.Back;
                }
                else if (_listQueue != null && _history.Count >= 2 &&
                         string.Equals(_history[^1], path, StringComparison.OrdinalIgnoreCase))
                {
                    // Back through what played, which under shuffle is not the list's order:
                    // this song goes back to the front of what is to come.
                    previous = _history[^2];
                    _history.RemoveAt(_history.Count - 1);
                    _listQueue.Insert(0, path);
                    _anchor = previous;
                    how = StartKind.Back;
                }
                else if (_shuffle && _listQueue != null) return "That's the first song played";
            }
            // Started part way down a list in order: the song above it, and the list goes on from there.
            previous ??= AppleMusic.PreviousSongBefore(place);
            if (previous == null) return "That's the first song in the list";
            try
            {
                var id = AppleMusic.CatalogIdForPlaying(previous);
                var name = AppleMusic.SpokenName(previous) ?? Path.GetFileName(previous);
                Start(previous, id, name, -1, startAt: 0, retried: false, how: how);
                return name;
            }
            catch (AppleMusicException ex) { return ex.Message; }
        }

        // ---- Shuffle, repeat and Up next (Conner, 4.1.6) ----

        /// <summary>
        /// Shuffle on or off. On, what is left of the list is shuffled; off, the
        /// list goes on in its own order from the song playing. Returns what to say.
        /// </summary>
        public static string ToggleShuffle()
        {
            bool on;
            lock (Gate)
            {
                on = _shuffle = !_shuffle;
                if (_listQueue != null && _listFolder != null)
                {
                    if (on) Shuffled(_listQueue);
                    else
                    {
                        var songs = AppleMusic.SongsShown(_listFolder);
                        int at = songs?.FindIndex(p => string.Equals(p, _anchor, StringComparison.OrdinalIgnoreCase)) ?? -1;
                        if (songs != null) _listQueue = songs.Skip(at + 1).ToList();
                    }
                }
            }
            Requeue();
            return on ? "Shuffle on" : "Shuffle off";
        }

        /// <summary>Repeat off, then all, then one, then off again. Returns what to say.</summary>
        public static string CycleRepeat()
        {
            RepeatMode now;
            lock (Gate) now = _repeat = _repeat switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off };
            Requeue();
            return RepeatWords(now);
        }

        public static string RepeatWords(RepeatMode mode) => mode switch
        {
            RepeatMode.All => "Repeat all",
            RepeatMode.One => "Repeat this song",
            _ => "Repeat off",
        };

        /// <summary>
        /// After the queue changed: the song Apple's player has lined up next is
        /// replaced with the one now due, or taken away when nothing is.
        /// </summary>
        private static void Requeue()
        {
            string path; int gen;
            lock (Gate)
            {
                if (_currentPath.Length == 0 || _ended) return;
                path = _currentPath; gen = _generation;
            }
            var (next, id, name) = Repeat == RepeatMode.One ? (null, null, null) : NextFor(path);
            lock (Gate)
            {
                if (gen != _generation || id == _queuedId) return;
                // What was queued may be what Apple's player is moving on to at
                // this very moment; kept, so that move is still recognised.
                if (_queuedPath != null) { _displacedPath = _queuedPath; _displacedId = _queuedId; }
                _queuedPath = next; _queuedId = id; _queuedName = name;
            }
            Log("requeue " + (id ?? "nothing"));
            OnUi(() => _ = CallAsync(id != null ? "requeue" : "clearnext", id, gen));
        }

        /// <summary>One song in Up next: its row path, what is said for it, and whether it was chosen with Play next.</summary>
        public readonly record struct UpNextSong(string Path, string Name, bool Chosen);

        /// <summary>
        /// Up next: songs chosen with Play next, then the rest of the list. A
        /// station shows the songs it already has; changing them keeps them as a
        /// list, and the station adds more after.
        /// </summary>
        public static List<UpNextSong> UpNext()
        {
            List<string> chosen, listed;
            lock (Gate)
            {
                if (_currentPath.Length == 0 || _ended) return new List<UpNextSong>();
                chosen = new List<string>(_upNext);
                listed = _listQueue != null ? new List<string>(_listQueue) : StationAhead(_anchor ?? _currentPath);
            }
            return chosen.Select(p => new UpNextSong(p, NameOf(p), true))
                .Concat(listed.Select(p => new UpNextSong(p, NameOf(p), false))).ToList();
        }

        private static string NameOf(string path) => AppleMusic.SpokenName(path) ?? Path.GetFileName(path);

        /// <summary>The songs after this one in its (station's) folder, at most a couple of hundred. Under Gate.</summary>
        private static List<string> StationAhead(string from)
        {
            var ahead = new List<string>();
            for (var p = AppleMusic.NextSongAfter(from); p != null && ahead.Count < 200; p = AppleMusic.NextSongAfter(p)) ahead.Add(p);
            return ahead;
        }

        /// <summary>The whole of Up next as one list, to change and put back. Under Gate.</summary>
        private static List<(string Path, bool Chosen)> Combined()
        {
            var all = _upNext.Select(p => (p, true)).ToList();
            _listQueue ??= StationAhead(_anchor ?? _currentPath);   // a station's songs, kept as a list from now on
            all.AddRange(_listQueue.Select(p => (p, false)));
            return all;
        }

        /// <summary>Puts a changed Up next back: the chosen part stays in front, so a song takes the kind of the place it lands in. Under Gate.</summary>
        private static void PutBack(List<(string Path, bool Chosen)> all, int chosenCount)
        {
            _upNext.Clear();
            _upNext.AddRange(all.Take(chosenCount).Select(s => s.Path));
            _listQueue = all.Skip(chosenCount).Select(s => s.Path).ToList();
        }

        /// <summary>Moves the song at <paramref name="index"/> one place up (-1) or down (+1). Returns its new place, or -1.</summary>
        public static int MoveUpNext(int index, int delta)
        {
            lock (Gate)
            {
                if (_currentPath.Length == 0 || _ended) return -1;
                var all = Combined();
                int to = index + delta;
                if (index < 0 || index >= all.Count || to < 0 || to >= all.Count) return -1;
                (all[index], all[to]) = (all[to], all[index]);
                PutBack(all, _upNext.Count);
            }
            Requeue();
            return index + delta;
        }

        /// <summary>Takes the song at <paramref name="index"/> out of Up next.</summary>
        public static bool RemoveUpNext(int index)
        {
            lock (Gate)
            {
                if (_currentPath.Length == 0 || _ended) return false;
                var all = Combined();
                if (index < 0 || index >= all.Count) return false;
                int chosen = _upNext.Count - (all[index].Chosen ? 1 : 0);
                all.RemoveAt(index);
                PutBack(all, chosen);
            }
            Requeue();
            return true;
        }

        /// <summary>Empties Up next: the song playing is the last, then Autoplay (or a station's next handful).</summary>
        public static void ClearUpNext()
        {
            lock (Gate)
            {
                if (_currentPath.Length == 0 || _ended) return;
                _upNext.Clear();
                _listQueue = new List<string>();
            }
            Requeue();
        }

        /// <summary>Plays the song at <paramref name="index"/> now; the ones before it are passed over. Returns what to say.</summary>
        public static string PlayUpNext(int index)
        {
            string path;
            lock (Gate)
            {
                if (_currentPath.Length == 0 || _ended) return "Nothing is playing";
                var all = Combined();
                if (index < 0 || index >= all.Count) return "";
                path = all[index].Path;
                bool wasChosen = all[index].Chosen;
                int chosen = all.Take(index + 1).Count(s => s.Chosen);
                all.RemoveRange(0, index + 1);
                PutBack(all, _upNext.Count - chosen);
                // A list song is the list's place now; a Play next song is not.
                if (!wasChosen) { _anchor = path; _history.Add(path); }
            }
            try
            {
                var name = NameOf(path);
                Start(path, AppleMusic.CatalogIdForPlaying(path), name, -1, 0, false, StartKind.Back);
                Volatile.Write(ref _autoGen, Volatile.Read(ref _generation));
                return name;
            }
            catch (AppleMusicException ex) { return ex.Message; }
        }

        /// <summary>
        /// A song that has not started after this long is started again, once,
        /// in a fresh player: a stalled page used to leave it silent for good.
        /// </summary>
        private const int StartWatchMs = 15_000;

        private static async void WatchStart(int generation, string path, string catalogId, string name, bool retried)
        {
            try
            {
                await Task.Delay(StartWatchMs).ConfigureAwait(true);
                if (Volatile.Read(ref _generation) != generation || Volatile.Read(ref _startedGen) == generation) return;
                lock (Gate) if (!string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase)) return;
                Log($"no sound from #{generation} after {StartWatchMs}ms" + (retried ? ", giving up" : ", starting again"));
                if (retried)
                {
                    Fail(generation, path, "Apple Music didn't start the song. Press Enter on it to try again.");
                    return;
                }
                Reset();
                Start(path, catalogId, name, -1, startAt: 0, retried: true, how: StartKind.Restarted);
            }
            catch (Exception ex) { Log("watch: " + ex.Message); }
        }

        /// <summary>
        /// Pauses or plays, going by what was last asked for rather than what the
        /// page last reported: right after Enter the page has not said "playing"
        /// yet, and Space then said "Playing" and lost the pause.
        /// </summary>
        public static string TogglePlayPause()
        {
            bool pausing; string name; int gen;
            lock (Gate)
            {
                pausing = _wantPlaying;
                _wantPlaying = !pausing; _playing = !pausing;
                name = _currentName; gen = _generation;
            }
            // A pause before the first sound is a choice, not a stall.
            if (pausing) Volatile.Write(ref _startedGen, gen);
            OnUi(() => _ = CallAsync(pausing ? "pause" : "resume", null, gen));
            return pausing ? "Paused" : "Playing " + name;
        }

        public static string Stop()
        {
            lock (Gate)
            {
                _currentPath = ""; _currentName = ""; _playing = false; _wantPlaying = false; _generation++;
                _queuedPath = null; _queuedId = null; _displacedPath = null; _displacedId = null;
                _upNext.Clear(); _anchor = null; _listQueue = null; _listFolder = null; _history.Clear();
            }
            OnUi(() => _ = CallAsync("stop"));
            CloseWhenIdle();
            return "Stopped";
        }

        /// <summary>
        /// The Windows name of the output device chosen in the Output device
        /// list ("" for the system default), which Apple's player then plays to.
        /// </summary>
        public static void SetOutputDevice(string name)
        {
            name ??= "";
            lock (Gate)
            {
                if (_deviceName == name) return;
                _deviceName = name;
            }
            Log("device " + (name.Length > 0 ? name : "default"));
            OnUi(() => _ = CallAsync("device", name));
        }

        /// <summary>
        /// Gets the player ready in the background, called on entering Apple
        /// Music, so the first Enter on a song does not also wait for a browser
        /// to start. Closed again if nothing is played (CloseWhenIdle).
        /// </summary>
        public static void Warm()
        {
            if (!AppleMusic.IsSetUp) return;
            OnUi(() =>
            {
                try
                {
                    if (_ready == null) Log("warming");
                    _ = EnsureReadyAsync();
                    if (!IsActive) CloseWhenIdle();
                }
                catch (Exception ex) { Log("warm: " + ex.Message); }
            });
        }

        /// <summary>With nothing loaded for this long, the hidden browser is closed, so it holds no memory or processes.</summary>
        private const int IdleCloseMs = 5 * 60_000;
        private static int _idleTicket;

        private static void CloseWhenIdle()
        {
            int ticket = Interlocked.Increment(ref _idleTicket);
            OnUi(async () =>
            {
                try
                {
                    await Task.Delay(IdleCloseMs).ConfigureAwait(true);
                    if (ticket != Volatile.Read(ref _idleTicket) || IsActive || _ready == null) return;
                    Log("closing the idle player");
                    Reset();
                }
                catch (Exception ex) { Log("idle: " + ex.Message); }
            });
        }

        /// <summary>
        /// Moves by a step and says where that lands, at once, like the built-in
        /// player. Counted from where the last press was aiming while the page's
        /// clock catches up, or five quick presses would all land in one place.
        /// </summary>
        public static string Seek(int deltaSeconds)
        {
            double target;
            lock (Gate)
            {
                if (_currentPath.Length == 0) return "Nothing is playing";
                bool settling = _seekTarget >= 0 && Environment.TickCount64 - _seekAt < SeekSettleMs;
                double from = settling ? _seekTarget : _positionSeconds;
                target = Math.Max(0, from + deltaSeconds);
                if (_durationSeconds > 0) target = Math.Min(target, Math.Max(0, _durationSeconds - 1));
                _seekTarget = target; _seekAt = Environment.TickCount64; _positionSeconds = target;
            }
            // One send at a time, carrying the newest target when it goes: a held
            // key runs this fifty times a second, and only where it ends matters.
            if (Interlocked.Exchange(ref _seekQueued, 1) == 0)
                OnUi(() =>
                {
                    Volatile.Write(ref _seekQueued, 0);
                    double latest; lock (Gate) latest = _seekTarget;
                    if (latest >= 0) _ = CallAsync("seekto", latest.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                });
            return AudioPlayer.SpokenTime(target);
        }

        public static string SeekToStart() => Seek(-int.MaxValue / 2);

        public static string SetVolumePercent(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            lock (Gate) { _volumePercent = percent; if (percent > 0) _unmutedPercent = -1; }
            OnUi(() => _ = CallAsync("volume", Gain(percent)));
            return percent == 100 ? "Volume 100 percent, as loud as Apple Music goes" : $"Volume {percent} percent";
        }

        /// <summary>Mute goes back to the level it came from, not to full.</summary>
        public static string ToggleMute()
        {
            int target;
            lock (Gate)
            {
                if (_unmutedPercent >= 0) { target = _unmutedPercent; _unmutedPercent = -1; }
                else { _unmutedPercent = Math.Max(1, _volumePercent); target = 0; }
                _volumePercent = target;
            }
            OnUi(() => _ = CallAsync("volume", Gain(target)));
            return target == 0 ? "Muted" : $"Unmuted, volume {target} percent";
        }

        public static int VolumePercent { get { lock (Gate) return _volumePercent; } }

        /// <summary>
        /// The level a volume step counts from: while muted, the level before
        /// mute. Counting from 0, Volume down while muted saved 0 as the volume,
        /// and every song and file after started silent.
        /// </summary>
        public static int VolumeToStepFrom { get { lock (Gate) return _unmutedPercent >= 0 ? _unmutedPercent : _volumePercent; } }

        private static string Gain(int percent) =>
            (percent / 100.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>"Title by Artist, 1 minute 20 of 3 minutes 5", like the built-in player's.</summary>
        public static string Describe()
        {
            lock (Gate)
            {
                if (_currentPath.Length == 0) return "Nothing is playing";
                var where = _durationSeconds > 0
                    ? $", {AudioPlayer.SpokenTime(_positionSeconds)} of {AudioPlayer.SpokenTime(_durationSeconds)}"
                    : "";
                return (_playing ? "" : "Paused, ") + _currentName + where;
            }
        }

        public static string DescribeElapsed()
        {
            lock (Gate) return _currentPath.Length == 0 ? "Nothing is playing" : AudioPlayer.SpokenTime(_positionSeconds) + " played";
        }

        public static string DescribeRemaining()
        {
            lock (Gate) return _currentPath.Length == 0 || _durationSeconds <= 0 ? "Nothing is playing"
                : AudioPlayer.SpokenTime(Math.Max(0, _durationSeconds - _positionSeconds)) + " left";
        }

        public static string DescribeTotal()
        {
            lock (Gate) return _currentPath.Length == 0 || _durationSeconds <= 0 ? "Nothing is playing"
                : AudioPlayer.SpokenTime(_durationSeconds) + " in all";
        }

        // ---- Play-on ----

        /// <summary>A song finished: the next one in its list as shown, more from a station, or "End of the list".</summary>
        private static void PlayNext()
        {
            string path; lock (Gate) path = _currentPath;
            if (path.Length == 0) return;
            // Only a song that played: one Apple won't play is skipped, not tried for ever.
            if (Repeat == RepeatMode.One && Volatile.Read(ref _startedGen) == Volatile.Read(ref _generation))
            {
                // The same song again, quietly: nothing about the list moves.
                try { Start(path, AppleMusic.CatalogIdForPlaying(path), AppleMusic.SpokenName(path) ?? Path.GetFileName(path), -1, 0, false, StartKind.Restarted); }
                catch (AppleMusicException ex) { EndOfList(); Say?.Invoke("applemusic.failed", ex.Message); }
                return;
            }
            var next = NextFor(path).Path;
            var place = ListPlace(path);
            if (next == null && AppleMusic.InStation(place))
            {
                PlayNextFromStation(place);
                return;
            }
            if (next == null)
            {
                PlayAutoplay(path);
                return;
            }
            StartPath(next);
        }

        /// <summary>
        /// Autoplay: a list has ended, so songs like its last one play on, from
        /// a station Apple makes of it. "End of the list" only when there is none
        /// (an upload, or Apple has nothing).
        /// </summary>
        private static async void PlayAutoplay(string finished)
        {
            int mine = Volatile.Read(ref _generation);
            if (AppleMusic.Autoplay)   // Preferences, Apple Music
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var rows = await AppleMusic.StartAutoplayAsync(finished, cts.Token).ConfigureAwait(true);
                if (Volatile.Read(ref _generation) != mine) return;   // something else was played meanwhile
                foreach (var row in rows)
                    if (!row.IsFolder && !string.Equals(AppleMusic.CatalogIdOrNull(row.Path), AppleMusic.CatalogIdOrNull(finished), StringComparison.Ordinal))
                    {
                        // The finished list's queue goes: Autoplay goes on like a station.
                        lock (Gate) { _listQueue = null; _listFolder = null; _history.Clear(); }
                        StartPath(row.Path);
                        return;
                    }
            }
            catch (Exception) { }
            if (Volatile.Read(ref _generation) != mine) return;
            EndOfList();
            Say?.Invoke("applemusic.endoflist", "End of the list");
        }

        /// <summary>
        /// Nothing more to play: like Stop, but quietly, so Play next or Enter
        /// starts a song at once (Play next said "plays next" and nothing came),
        /// and the idle player can close.
        /// </summary>
        private static void EndOfList()
        {
            lock (Gate)
            {
                _currentPath = ""; _currentName = ""; _playing = false; _wantPlaying = false; _ended = true;
                _queuedPath = null; _queuedId = null; _displacedPath = null; _displacedId = null;
                _upNext.Clear(); _anchor = null; _listQueue = null; _listFolder = null; _history.Clear();
            }
            CloseWhenIdle();
        }

        private static async void PlayNextFromStation(string finished)
        {
            int mine = Volatile.Read(ref _generation);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var folder = Path.GetDirectoryName(finished)!;
                var more = await AppleMusic.MoreFromStationAsync(folder, cts.Token).ConfigureAwait(true);
                if (Volatile.Read(ref _generation) != mine) return;   // something else was played meanwhile
                // A station changed in Up next keeps a list of its own: the new songs join it.
                lock (Gate) _listQueue?.AddRange(more.Where(r => !r.IsFolder).Select(r => r.Path));
                foreach (var row in more)
                    if (!row.IsFolder) { StartPath(row.Path); return; }
                EndOfList();
                Say?.Invoke("applemusic.endoflist", "The station has no more songs");
            }
            catch (Exception)
            {
                if (Volatile.Read(ref _generation) != mine) return;
                EndOfList();
                Say?.Invoke("applemusic.endoflist", "The station has no more songs");
            }
        }

        /// <summary>Enter on a song row: start it in Apple's player. Returns false only if it isn't a song.</summary>
        public static bool PlayRow(string path, int volumePercent = -1)
        {
            try
            {
                var id = AppleMusic.CatalogIdForPlaying(path);
                var name = AppleMusic.SpokenName(path) ?? Path.GetFileName(path);
                Play(path, id, name, volumePercent);
                return true;
            }
            catch (AppleMusicException ex) { Say?.Invoke("applemusic.failed", ex.Message); return false; }
        }

        /// <summary>Resolve a row path to its song and play it, saying its name, or say why not.</summary>
        private static void StartPath(string path)
        {
            try
            {
                var id = AppleMusic.CatalogIdForPlaying(path);
                var name = AppleMusic.SpokenName(path) ?? Path.GetFileName(path);
                Start(path, id, name, -1, startAt: 0, retried: false, how: StartKind.PlayedOn);
                Volatile.Write(ref _autoGen, Volatile.Read(ref _generation));
                Say?.Invoke("audio.play", name);
            }
            catch (AppleMusicException ex)
            {
                EndOfList();
                Say?.Invoke("applemusic.failed", ex.Message);
            }
        }

        // ---- The host and the bridge ----

        private static void OnUi(Action action)
        {
            var ui = _ui;
            if (ui == null) { action(); return; }
            ui.Post(_ => action(), null);
        }

        private static Task<bool> EnsureReadyAsync()
        {
            if (_ready != null) return _ready.Task;
            _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = StartHostAsync();
            return _ready.Task;
        }

        /// <summary>True once the page has MusicKit configured; false if it took too long. Throws on a failure with a sentence.</summary>
        private static async Task<bool> ReadyAsync()
        {
            var ready = EnsureReadyAsync();
            if (await Task.WhenAny(ready, Task.Delay(StartTimeoutMs)).ConfigureAwait(true) != ready) return false;
            return await ready.ConfigureAwait(true);
        }

        /// <summary>Lets go of a player that failed to start, so the next play builds a fresh one.</summary>
        private static void Reset()
        {
            Log("reset");
            int pid = _browserProcessId;
            var folder = _dataFolder;
            CloseBrowser();
            _ready = null; _pendingConfig = null;
            // The profile goes once the browser has let go of it, off this thread.
            _ = Task.Run(() =>
            {
                WaitForExit(pid);
                WipeFolder(folder);
            });
        }

        private static void WaitForExit(int pid)
        {
            if (pid <= 0) return;
            try
            {
                using var browser = System.Diagnostics.Process.GetProcessById(pid);
                browser.WaitForExit(3000);
            }
            catch (ArgumentException) { }          // already gone
            catch (InvalidOperationException) { }
        }

        /// <summary>
        /// The page or browser died (a crash, or Windows ended it). The song that
        /// was playing is started again where it was, in a fresh player; after a
        /// third crash inside a minute it is given up on, with a sentence.
        /// </summary>
        private static void OnProcessFailed(CoreWebView2ProcessFailedKind kind)
        {
            Log("process failed: " + kind);
            if (kind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive or CoreWebView2ProcessFailedKind.FrameRenderProcessExited))
                return;
            OnUi(() =>
            {
                string path, name; double at; bool wanted; int gen;
                lock (Gate) { path = _currentPath; name = _currentName; at = _positionSeconds; wanted = _wantPlaying; gen = _generation; }
                var now = Environment.TickCount64;
                _crashes = now - _lastCrashAt < 60_000 ? _crashes + 1 : 1;
                _lastCrashAt = now;
                Reset();
                if (path.Length == 0) return;   // the next play builds a fresh one
                if (_crashes > 2)
                {
                    Fail(gen, path, "Apple Music's player keeps stopping. Press Enter on the song to try again.");
                    return;
                }
                // Paused stays paused, at the same place: left as it was, every key
                // went to a player that was gone and said nothing.
                try { Start(path, AppleMusic.CatalogIdForPlaying(path), name, -1, startAt: at, retried: false, how: StartKind.Restarted, paused: !wanted); }
                catch (AppleMusicException ex) { Fail(gen, path, ex.Message); }
            });
        }

        /// <summary>
        /// On quit, from the UI thread. Closes the browser and waits a moment for
        /// its processes to let go of the profile, then deletes it: deleting at
        /// process exit while they still held it left 78 files in %TEMP% until
        /// the next start swept them, and Conner's rule is that nothing stays.
        /// </summary>
        public static void Shutdown()
        {
            if (_controller == null && _dataFolder == null) return;
            lock (Gate) { _currentPath = ""; _playing = false; _generation++; }
            int pid = _browserProcessId;
            CloseBrowser();
            if (pid > 0)
            {
                try
                {
                    using var browser = System.Diagnostics.Process.GetProcessById(pid);
                    browser.WaitForExit(3000);
                }
                catch (ArgumentException) { }          // already gone
                catch (InvalidOperationException) { }
            }
            Wipe();
        }

        private static void CloseBrowser()
        {
            var controller = _controller; var host = _host;
            _controller = null; _host = null; _browserProcessId = 0;
            try { controller?.Close(); } catch { }
            try { host?.Dispose(); } catch { }
        }

        private static async Task StartHostAsync()
        {
            var ready = _ready;
            try
            {
                // A window that is never shown, only there to own the browser.
                // Its handle is made without showing it.
                _host = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-4000, -4000),
                    Size = new System.Drawing.Size(320, 240),
                };
                var parent = _host.Handle;

                // Nothing is kept: the sign-in is injected afresh each time, so
                // the player's profile is a throwaway folder under %TEMP%. Older
                // ones from past runs (or a crash) are swept first, and this one
                // goes when the app exits (Shutdown). So the player leaves nothing behind.
                //
                // Nobody ever clicks this page, so without the autoplay switch the
                // engine may refuse to start sound at all.
                var dataFolder = NewThrowawayProfile();
                var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
                var host = _host;
                var env = await CoreWebView2Environment.CreateAsync(null, dataFolder, options).ConfigureAwait(true);
                // A start given up on (Reset) while it waited must not take over
                // from the one started after it: it closes what it made and goes.
                if (_ready != ready) { if (_host != host) host.Dispose(); return; }
                var controller = await env.CreateCoreWebView2ControllerAsync(parent).ConfigureAwait(true);
                if (_ready != ready)
                {
                    try { controller.Close(); } catch { }
                    if (_host != host) host.Dispose();
                    return;
                }
                _controller = controller;
                _controller.Bounds = new System.Drawing.Rectangle(0, 0, 320, 240);

                // Visible to the page, though no window is on screen. Hosted in
                // the WinForms control inside a form that is never shown, the page
                // said document.hidden, and Chromium holds back a hidden page's
                // media: the licence came and then not one byte of sound loaded.
                _controller.IsVisible = true;

                var core = _controller.CoreWebView2;
                _browserProcessId = (int)core.BrowserProcessId;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.WebMessageReceived += OnWebMessage;
                core.ProcessFailed += (_, e) => OnProcessFailed(e.ProcessFailedKind);

                // Choosing a speaker (HTMLMediaElement.setSinkId) needs the
                // page to know the devices, which Chromium allows only with the
                // microphone permission. It is granted to this one page, which
                // never listens (its getUserMedia is removed); anything else a
                // page might ask for is refused.
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                try
                {
                    await core.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone,
                        "https://" + PageHost, CoreWebView2PermissionState.Allow).ConfigureAwait(true);
                }
                catch (Exception ex) { Log("no device choice: " + ex.Message); }   // an older runtime: the default device only

                // The page is our own file, served by WebView2 itself under the
                // web player's origin: the developer token is the web player's, and
                // Apple answers its catalogue requests only from that origin (from
                // any other, setQueue failed with "Unable to read body as JSON").
                // The browsing client sends the same origin. Nothing is fetched
                // from music.apple.com: this host maps to the profile folder.
                File.WriteAllText(Path.Combine(dataFolder, PageName), PlayerHtml, new UTF8Encoding(false));
                core.SetVirtualHostNameToFolderMapping(PageHost, dataFolder,
                    CoreWebView2HostResourceAccessKind.Allow);

                var token = await AppleMusic.DeveloperTokenAsync(CancellationToken.None).ConfigureAwait(true);
                if (_ready != ready) return;   // Reset closed this one meanwhile
                var userToken = AppleMusic.UserToken;
                _pendingConfig = JsonSerializer.Serialize(new { developerToken = token, userToken });

                core.NavigationCompleted += (_, e) =>
                {
                    Log("page loaded " + e.IsSuccess);
                    if (!e.IsSuccess) { ready?.TrySetException(new AppleMusicException("Apple Music's player couldn't load.")); return; }
                    if (_pendingConfig != null)
                        _ = core.ExecuteScriptAsync("window.amConfigure(" + _pendingConfig + ")");
                };
                core.Navigate("https://" + PageHost + "/" + PageName);
            }
            catch (AppleMusicException ex)
            {
                ready?.TrySetException(ex);
            }
            catch (Exception ex)
            {
                ready?.TrySetException(new AppleMusicException("Apple Music's player couldn't start: " + ex.Message));
            }
        }

        private static string? _dataFolder;
        private static bool _wipeOnExit;

        /// <summary>A fresh profile folder under %TEMP%, after wiping any left by past runs.</summary>
        private static string NewThrowawayProfile()
        {
            var root = Path.Combine(Path.GetTempPath(), "ExplorerNative-AppleMusic");
            try
            {
                if (Directory.Exists(root))
                    foreach (var old in Directory.GetDirectories(root))
                        try { Directory.Delete(old, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var mine = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mine);
            _dataFolder = mine;
            if (!_wipeOnExit)
            {
                _wipeOnExit = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Wipe();
            }
            return mine;
        }

        /// <summary>Deletes this run's profile. Runs on quit; a locked file is left for the next run's sweep.</summary>
        private static void Wipe() => WipeFolder(_dataFolder);

        private static void WipeFolder(string? folder)
        {
            if (folder == null) return;
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static string? _pendingConfig;

        private static async Task CallAsync(string command, string? arg = null, int generation = 0, string? next = null, double at = 0,
            bool paused = false)
        {
            if (_ready == null || !_ready.Task.IsCompletedSuccessfully) return;   // nothing loaded to tell
            var core = _controller?.CoreWebView2;
            if (core == null) return;
            var payload = JsonSerializer.Serialize(new { command, arg, gen = generation, next, at, paused });
            Log("send " + command + " " + arg + (next != null ? " then " + next : ""));
            try { await core.ExecuteScriptAsync("window.amCommand(" + payload + ")").ConfigureAwait(true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                Log("send failed: " + ex.Message);   // the browser went away; ProcessFailed deals with it
            }
        }

        /// <summary>State the page reports: ready, state, ended, error.</summary>
        private static void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // Only the page in use now: one being closed may still be talking.
            if (!ReferenceEquals(sender, _controller?.CoreWebView2)) return;
            string json;
            try { json = e.TryGetWebMessageAsString(); }
            catch { return; }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string kind = root.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                int gen = root.TryGetProperty("gen", out var g) && g.TryGetInt32(out var gi) ? gi : 0;
                switch (kind)
                {
                    case "ready":
                        Log("ready " + json);
                        _ready?.TrySetResult(true);
                        break;
                    case "state":
                        lock (Gate)
                        {
                            if (gen != 0 && gen != _generation) break;
                            _playing = root.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.True;
                            if (_playing && gen != 0) { _startedGen = gen; _skips = 0; }
                            bool settling = _seekTarget >= 0 && Environment.TickCount64 - _seekAt < SeekSettleMs;
                            if (!settling && root.TryGetProperty("position", out var pos) && pos.TryGetDouble(out var ps)) _positionSeconds = ps;
                            if (root.TryGetProperty("duration", out var dur) && dur.TryGetDouble(out var ds) && ds > 0) _durationSeconds = ds;
                        }
                        if (LogPath != null) Log("state " + json);
                        break;
                    case "ended":
                        Log("ended #" + gen);
                        if (gen == Volatile.Read(ref _generation)) PlayNext();
                        break;
                    case "advanced":
                        OnAdvanced(gen, root.TryGetProperty("id", out var a) ? a.GetString() ?? "" : "");
                        break;
                    case "log":
                        Log("page: " + (root.TryGetProperty("message", out var lm) ? lm.GetString() : ""));
                        break;
                    case "error":
                    {
                        string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                        string code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                        Log("error #" + gen + " " + code + " " + msg);
                        // Before the page is ready, the player itself failed to start.
                        if (_ready != null && !_ready.Task.IsCompleted)
                        {
                            _ready.TrySetException(new AppleMusicException(
                                msg.Length > 0 ? "Apple Music's player couldn't start: " + msg : "Apple Music's player couldn't start."));
                            break;
                        }
                        string path, name; lock (Gate) { path = _currentPath; name = _currentName; }
                        int failed = gen == 0 ? Volatile.Read(ref _generation) : gen;
                        var sentence = Sentence(code, msg);
                        // Playing on by itself into a song Apple won't play: say so
                        // and go on to the next, rather than stopping the music.
                        // A song you pressed Enter on just says why.
                        if (failed == Volatile.Read(ref _generation) && sentence == Unavailable && _skips < MostSkips && path.Length > 0)
                        {
                            if (failed == _autoGen)
                            {
                                _skips++;
                                Log("skipping unavailable #" + failed);
                                Say?.Invoke("applemusic.failed", name + " isn't available, so it was skipped");
                                PlayNext();
                                break;
                            }
                            if (Volatile.Read(ref _startedGen) == failed)
                            {
                                // This play already made sound, so it is a later song
                                // in the chain Apple won't play. While this one still
                                // plays, its end deals with it (ended, then skipped);
                                // stuck on it, it is skipped now.
                                bool playingNow; string? queuedPath, queuedName;
                                lock (Gate) { playingNow = _playing; queuedPath = _queuedPath; queuedName = _queuedName; }
                                if (playingNow) { Log("a later song is unavailable; its turn will skip it"); break; }
                                if (queuedPath != null)
                                    lock (Gate)
                                    {
                                        BecameCurrent(queuedPath);
                                        _currentPath = queuedPath; _currentName = queuedName ?? ""; name = _currentName;
                                        _queuedPath = null; _queuedId = null; _queuedName = null;
                                    }
                                _skips++;
                                Log("skipping unavailable (played on) #" + failed);
                                Say?.Invoke("applemusic.failed", name + " isn't available, so it was skipped");
                                PlayNext();
                                break;
                            }
                        }
                        Fail(failed, path, sentence);
                        break;
                    }
                }
            }
            catch (JsonException) { }
        }

        /// <summary>
        /// Apple's player went on to the song queued after the last one: that is
        /// now the one playing, it is said like any song starting, and the one
        /// after it is queued in turn.
        /// </summary>
        private static void OnAdvanced(int gen, string id)
        {
            string name, now;
            lock (Gate)
            {
                if (gen != _generation) { Log("advance ignored " + id); return; }
                if (_displacedPath != null && id == _displacedId && id != _queuedId)
                {
                    // Apple's player had already moved on to the song Play next was
                    // replacing. That one is playing; the Play next choice, already
                    // sent, stays queued after it.
                    BecameCurrent(_displacedPath);
                    _currentPath = _displacedPath;
                    _currentName = AppleMusic.SpokenName(_displacedPath) ?? Path.GetFileName(_displacedPath);
                    _displacedPath = null; _displacedId = null;
                    _positionSeconds = 0; _durationSeconds = 0; _seekTarget = -1; _startedGen = gen;
                    name = _currentName;
                    Log("advanced to the displaced " + id);
                    Say?.Invoke("audio.play", name);
                    return;
                }
                if (_queuedPath == null || id != _queuedId) { Log("advance ignored " + id); return; }
                BecameCurrent(_queuedPath);
                _currentPath = _queuedPath; _currentName = _queuedName ?? "";
                _queuedPath = null; _queuedId = null; _queuedName = null; _displacedPath = null; _displacedId = null;
                _positionSeconds = 0; _durationSeconds = 0; _seekTarget = -1; _startedGen = gen;
                name = _currentName; now = _currentPath;
            }
            Log("advanced to " + id);
            Say?.Invoke("audio.play", name);
            var (nextPath, nextId, nextName) = NextFor(now);
            if (nextPath == null) return;   // the end, or a station: "ended" deals with it
            lock (Gate)
            {
                if (gen != _generation) return;
                _queuedPath = nextPath; _queuedId = nextId; _queuedName = nextName;
            }
            _ = CallAsync("append", nextId, gen);
        }

        /// <summary>
        /// What is said when a song will not play. MusicKit's own messages are
        /// written for developers (one is a URL), so only its code is used to
        /// choose the sentence; the message itself goes to the log.
        /// </summary>
        private const string Unavailable = "That song isn't available to play on Apple Music.";

        /// <summary>The newest play started by playing on (not by Enter), and how many unavailable songs in a row were skipped.</summary>
        private static int _autoGen, _skips;
        private const int MostSkips = 10;

        internal static string Sentence(string code, string message)
        {
            string both = (code + " " + message).ToUpperInvariant();
            if (both.Contains("SUBSCRIPTION", StringComparison.Ordinal))
                return "Apple Music says there is no active subscription on this account.";
            if (both.Contains("AUTHORIZ", StringComparison.Ordinal) || both.Contains("UNAUTHENTICATED", StringComparison.Ordinal) ||
                both.Contains("TOKEN", StringComparison.Ordinal))
                return "Apple Music didn't accept your sign-in. Load a new cookies.txt in Preferences, Apple Music.";
            if (both.Contains("CONTENT_UNAVAILABLE", StringComparison.Ordinal) || both.Contains("CONTENT_RESTRICTED", StringComparison.Ordinal) ||
                both.Contains("NOT_FOUND", StringComparison.Ordinal))
                return Unavailable;
            if (both.Contains("NETWORK", StringComparison.Ordinal) || both.Contains("FAILED TO FETCH", StringComparison.Ordinal))
                return "Apple Music couldn't be reached. Check the internet connection.";
            if (both.Contains("KEY", StringComparison.Ordinal) || both.Contains("DRM", StringComparison.Ordinal) ||
                both.Contains("LICENSE", StringComparison.Ordinal) || both.Contains("MEDIA_", StringComparison.Ordinal))
                return "Windows couldn't play Apple Music's protected audio on this PC.";
            return "Apple Music couldn't play that song.";
        }

        /// <summary>Says why a play failed, unless a newer play has taken over since: then it is nobody's news.</summary>
        private static void Fail(int generation, string path, string message)
        {
            bool cleared = false;
            lock (Gate)
            {
                if (generation != _generation) { Log("stale error ignored"); return; }
                if (string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _currentPath = ""; _currentName = ""; _playing = false; _wantPlaying = false;
                    _queuedPath = null; _queuedId = null; _displacedPath = null; _displacedId = null;
                    _generation++;
                    cleared = true;
                }
            }
            // The page is told to stop too: a song given up on here that started
            // late anyway played on with no key able to reach it.
            if (cleared)
            {
                OnUi(() => _ = CallAsync("stop"));
                CloseWhenIdle();
            }
            Say?.Invoke("applemusic.failed", message);
        }

        private static void Log(string line)
        {
            var path = LogPath;
            if (string.IsNullOrEmpty(path)) return;
            try { File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine); }
            catch { }
        }

        /// <summary>
        /// The player page. Loads Apple's MusicKit JS v3, configures it with the
        /// developer token, seeds the music user token so no second sign-in is
        /// needed, and turns window messages into play/pause/seek/volume calls.
        /// It reports state back so the app can speak "what is playing".
        ///
        /// "current" is the number of the newest play. While a play is switching
        /// songs nothing is reported as ended, an end is reported once per play
        /// (MusicKit says "ended" and then "completed" for the same song), and an
        /// error from a play that a newer one called off is dropped.
        /// </summary>
        private const string PlayerHtml = """
<!doctype html>
<html>
<head><meta charset="utf-8"><title>Apple Music</title>
<!-- The hosts every first play talks to, connected while the page waits, so the first song does not pay for them. -->
<link rel="preconnect" href="https://api.music.apple.com" crossorigin>
<link rel="preconnect" href="https://play.itunes.apple.com" crossorigin>
<link rel="preconnect" href="https://aod-ssl.itunes.apple.com" crossorigin>
<link rel="preconnect" href="https://universal-activity-service.itunes.apple.com" crossorigin>
</head>
<body>
<script>
// The page may name speakers (that needs the microphone permission), but it
// never listens: nothing here may open a microphone.
try {
  if (navigator.mediaDevices) navigator.mediaDevices.getUserMedia = () => Promise.reject(new DOMException("Not allowed", "NotAllowedError"));
  navigator.getUserMedia = navigator.webkitGetUserMedia = undefined;
} catch (e) {}
let music = null, current = 0, switching = false, endedFor = -1, wantVolume = null, wantSink = null;
let queuedNext = null, lastState = "", seekWant = null, seeking = false, advances = 0;
let holdPaused = false, pendingRequeue = null, pendingClear = false, resumeAt = 0;
// MusicKit can reset the volume when it builds the audio for a new song (it
// started at 0, silent), so the level asked for is kept and put back.
function applyVolume() { if (music && wantVolume !== null && music.volume !== wantVolume) music.volume = wantVolume; }
function post(obj) { try { window.chrome.webview.postMessage(JSON.stringify(obj)); } catch (e) {} }
function audio() { return document.querySelector("audio"); }
function sendState() {
  // Not while a new song is being put in: what MusicKit says then is the old one's.
  if (!music || switching) return;
  const position = music.currentPlaybackTime || 0, duration = music.currentPlaybackDuration || 0;
  // Only what the app can use: a change of state, or of the whole second.
  // MusicKit reports the time several times a second, and every report
  // crossing to the app cost both processes for nothing.
  const key = music.isPlaying + "|" + music.playbackState + "|" + Math.floor(position) + "|" + Math.floor(duration);
  if (key === lastState) return;
  lastState = key;
  post({ kind: "state", gen: current, playing: music.isPlaying, state: music.playbackState, position, duration });
}
// A song as MusicKit's queue takes it. Given whole, MusicKit does not look the
// song up in Apple's catalogue first, which cost 0.4 of a second per song.
function item(id) {
  const library = String(id).startsWith("i.");
  return { id: String(id), type: library ? "library-songs" : "songs",
           attributes: { playParams: library ? { id: String(id), kind: "song", isLibrary: true } : { id: String(id), kind: "song" } } };
}
// The output device the app has chosen, by its Windows name. Empty is the
// system default, which Chromium follows by itself.
async function applySink() {
  const a = audio();
  if (!a || wantSink === null || !a.setSinkId) return;
  let id = "";
  if (wantSink) {
    try {
      const all = await navigator.mediaDevices.enumerateDevices();
      const d = all.find(x => x.kind === "audiooutput" && x.deviceId !== "default" && x.deviceId !== "communications" && x.label === wantSink);
      if (d) id = d.deviceId;
      else post({ kind: "log", message: "no output named " + wantSink + "; using the default" });
    } catch (e) { post({ kind: "log", message: "devices: " + e }); }
  }
  if (a.sinkId !== id) {
    try { await a.setSinkId(id); post({ kind: "log", message: "sink " + (id ? wantSink : "default") }); }
    catch (e) { post({ kind: "log", message: "setSinkId: " + e }); }
  }
}
function inBuffer(a, t) {
  for (let i = 0; i < a.buffered.length; i++)
    if (t >= a.buffered.start(i) && t < a.buffered.end(i) - 0.5) return true;
  return false;
}
// A skip inside what is already downloaded moves the audio itself, at once.
// Anything further goes through MusicKit, one at a time, newest target only:
// each of its seeks downloads again, and a held key used to queue dozens.
async function seekTo(t) {
  const a = audio();
  if (a && inBuffer(a, t)) { seekWant = null; a.currentTime = t; return; }
  seekWant = t;
  if (seeking) return;
  seeking = true;
  try {
    while (seekWant !== null) {
      const target = seekWant; seekWant = null;
      const b = audio();
      if (b && inBuffer(b, target)) b.currentTime = target;
      else await music.seekToTime(target);
    }
  } finally { seeking = false; }
}
function failedToLoad() { post({ kind: "error", message: "MusicKit could not be loaded from Apple." }); }
function codeOf(e) { return String((e && (e.errorCode || e.code || e.name)) || ""); }
function warmDecryption() {
  // The protected-audio module loads on first use, which the first song waited for.
  try {
    navigator.requestMediaKeySystemAccess("com.widevine.alpha", [{ initDataTypes: ["cenc"],
      audioCapabilities: [{ contentType: 'audio/mp4; codecs="mp4a.40.2"' }] }])
      .then(a => a.createMediaKeys()).catch(() => {});
  } catch (e) {}
}
window.amConfigure = async function (cfg) {
  try {
    warmDecryption();
    await new Promise(res => {
      if (window.MusicKit && window.MusicKit.configure) return res();
      document.addEventListener("musickitloaded", res, { once: true });
    });
    music = await window.MusicKit.configure({
      developerToken: cfg.developerToken,
      app: { name: "Explorer Native", build: "1.0" },
    });
    // Seed the music user token (same value as the media-user-token cookie),
    // so playback is authorised without a second interactive sign-in.
    if (cfg.userToken) { try { music.musicUserToken = cfg.userToken; } catch (e) {} }
    const E = window.MusicKit.Events;
    music.addEventListener(E.playbackStateDidChange, sendState);
    music.addEventListener(E.playbackTimeDidChange, sendState);
    music.addEventListener(E.playbackDurationDidChange, sendState);
    music.addEventListener(E.nowPlayingItemDidChange, function () {
      applyVolume(); applySink();
      // MusicKit moved on to the song queued after this one by itself: the app
      // takes it as the one playing and queues the next.
      const now = music.nowPlayingItem;
      if (!switching && queuedNext !== null && now && String(now.id) === queuedNext) {
        queuedNext = null; advances++;
        post({ kind: "advanced", gen: current, id: String(now.id) });
      }
      sendState();
    });
    music.addEventListener(E.mediaPlaybackError, function (e) {
      if (switching) return;
      post({ kind: "error", gen: current, code: codeOf(e), message: (e && (e.description || e.message)) || "Playback error" });
    });
    music.addEventListener(E.playbackStateDidChange, function () {
      // 5 is "ended", 10 is "completed" in MusicKit's playback states. With a
      // song queued after, MusicKit says "ended" and then goes on to it by
      // itself, so the end is only told if it has not moved on shortly after
      // (the queued song would not play); taken at once, the app started that
      // song again from nothing.
      const s = music.playbackState;
      if ((s !== 5 && s !== 10) || switching || endedFor === current) return;
      const mine = current, moves = advances;
      const tell = () => {
        if (mine !== current || moves !== advances || endedFor === current || music.isPlaying) return;
        endedFor = current;
        post({ kind: "ended", gen: current });
      };
      if (queuedNext !== null) setTimeout(tell, 2500); else tell();
    });
    post({ kind: "ready", authorized: !!music.isAuthorized });
  } catch (e) { post({ kind: "error", message: String(e && e.message || e) }); }
};
window.amCommand = async function (c) {
  if (!music) { post({ kind: "error", gen: c.gen || current, message: "The player isn't ready." }); return; }
  if (c.command === "play") {
    const mine = c.gen;
    current = mine; switching = true; queuedNext = null; seekWant = null;
    holdPaused = !!c.paused; pendingRequeue = null; pendingClear = false; resumeAt = 0;
    try {
      const items = [item(c.arg)];
      if (c.next) items.push(item(c.next));
      try { await music.setQueue({ items }); }
      catch (e) { if (mine !== current) return; await music.setQueue({ song: c.arg }); items.length = 1; }
      if (mine !== current) return;
      if (items.length > 1) queuedNext = String(c.next);
      // Up next changed while this song was being put in: nothing after it now.
      if (pendingClear) { music.queue.clearAfterCurrent(); queuedNext = null; pendingClear = false; }
      // A Play next chosen while this song was being put in.
      if (pendingRequeue !== null) {
        music.queue.clearAfterCurrent();
        await music.playLater({ items: [item(pendingRequeue)] });
        queuedNext = pendingRequeue; pendingRequeue = null;
      }
      applyVolume();
      await applySink();
      if (holdPaused) {
        // Paused before it started (Space right after Enter, or a crash while
        // paused): loaded, silent, and going on from the right place on resume.
        resumeAt = c.at > 1 ? c.at : 0;
      } else {
        await music.play();
        applyVolume();
        if (c.at > 1 && mine === current) await music.seekToTime(c.at);
        if (holdPaused && mine === current) await music.pause();   // paused while play() was starting
      }
    } catch (e) {
      if (mine === current) post({ kind: "error", gen: mine, code: codeOf(e), message: String(e && e.message || e) });
    } finally {
      if (mine === current) { switching = false; lastState = ""; sendState(); }
    }
    return;
  }
  // While a song is being put in, a pause, a resume or a Play next waits for it.
  if (switching && c.gen === current) {
    if (c.command === "pause") { holdPaused = true; return; }
    if (c.command === "resume") { holdPaused = false; return; }
    if (c.command === "requeue") { pendingRequeue = String(c.arg); pendingClear = false; return; }
    if (c.command === "clearnext") { pendingRequeue = null; pendingClear = true; return; }
  }
  try {
    switch (c.command) {
      case "pause": holdPaused = true; await music.pause(); break;
      case "resume":
        holdPaused = false;
        await music.play();
        applyVolume();
        if (resumeAt > 1) { const t = resumeAt; resumeAt = 0; await music.seekToTime(t); }
        break;
      case "stop": current++; switching = false; queuedNext = null; seekWant = null; await music.stop(); break;
      case "seekto": await seekTo(parseFloat(c.arg || "0")); break;
      case "volume": wantVolume = parseFloat(c.arg || "1"); music.volume = wantVolume; break;
      case "device": wantSink = c.arg || ""; await applySink(); break;
      case "append":
        if (c.gen !== current) break;
        await music.playLater({ items: [item(c.arg)] });
        queuedNext = String(c.arg);
        break;
      case "requeue":
        // Play next: whatever was queued after this song gives way to the new choice.
        if (c.gen !== current) break;
        music.queue.clearAfterCurrent();
        await music.playLater({ items: [item(c.arg)] });
        queuedNext = String(c.arg);
        break;
      case "clearnext":
        // Nothing comes next (Up next cleared, repeat one): the song's end is told as an end.
        if (c.gen !== current) break;
        music.queue.clearAfterCurrent();
        queuedNext = null;
        break;
    }
    sendState();
  } catch (e) {
    // Only a song failing to play is a failure (MusicKit's playback error and
    // "play" above). A pause, a skip or a queue change that went wrong is
    // logged: told as a failure, it emptied the app's player while the music
    // went on, out of reach of every key.
    post({ kind: "log", message: c.command + " failed: " + codeOf(e) + " " + String(e && e.message || e) });
  }
};
</script>
<!-- Just the player API, no web-component UI: nothing here is ever shown. -->
<script src="https://js-cdn.music.apple.com/musickit/v3/musickit.js" async onerror="failedToLoad()"></script>
</body>
</html>
""";
    }
}
