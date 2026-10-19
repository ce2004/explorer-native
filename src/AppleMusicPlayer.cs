using System;
using System.IO;
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
    /// (speed, pitch, EQ, limiter, silence removal) does not apply here, and the
    /// audio goes to the system default output. Everything else about the
    /// player's controls is mirrored so it feels like the built-in player.
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
        public static void Play(string path, string catalogId, string spokenName, int volumePercent = -1)
        {
            int mine;
            lock (Gate)
            {
                mine = ++_generation;
                _currentPath = path; _currentName = spokenName;
                _playing = false; _positionSeconds = 0; _durationSeconds = 0; _seekTarget = -1;
                if (volumePercent >= 0) { _volumePercent = Math.Clamp(volumePercent, 0, 100); _unmutedPercent = -1; }
            }
            Log($"play #{mine} {catalogId}");
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
                    int volume; lock (Gate) volume = _volumePercent;
                    await CallAsync("volume", Gain(volume)).ConfigureAwait(true);
                    await CallAsync("play", catalogId, mine).ConfigureAwait(true);
                }
                catch (AppleMusicException ex) { Reset(); Fail(mine, path, ex.Message); }
                catch (Exception ex) { Fail(mine, path, "Apple Music couldn't start the song: " + ex.Message); }
            });
        }

        public static string TogglePlayPause()
        {
            bool playing; string name;
            lock (Gate) { playing = _playing; name = _currentName; _playing = !playing; }
            OnUi(() => _ = CallAsync(playing ? "pause" : "resume"));
            return playing ? "Paused" : "Playing " + name;
        }

        public static string Stop()
        {
            lock (Gate) { _currentPath = ""; _currentName = ""; _playing = false; _generation++; }
            OnUi(() => _ = CallAsync("stop"));
            return "Stopped";
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
            OnUi(() => _ = CallAsync("seekto", target.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
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
            var next = AppleMusic.NextSongAfter(path);
            if (next == null && AppleMusic.InStation(path))
            {
                PlayNextFromStation(path);
                return;
            }
            if (next == null)
            {
                lock (Gate) _playing = false;
                Say?.Invoke("applemusic.endoflist", "End of the list");
                return;
            }
            StartPath(next);
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
                foreach (var row in more)
                    if (!row.IsFolder) { StartPath(row.Path); return; }
                Say?.Invoke("applemusic.endoflist", "The station has no more songs");
            }
            catch (Exception)
            {
                if (Volatile.Read(ref _generation) == mine) Say?.Invoke("applemusic.endoflist", "The station has no more songs");
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
                Play(path, id, name);
                Say?.Invoke("audio.play", name);
            }
            catch (AppleMusicException ex)
            {
                lock (Gate) { _currentPath = ""; _playing = false; }
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
            CloseBrowser();
            _ready = null; _pendingConfig = null;
            Wipe();
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
                var env = await CoreWebView2Environment.CreateAsync(null, dataFolder, options).ConfigureAwait(true);
                _controller = await env.CreateCoreWebView2ControllerAsync(parent).ConfigureAwait(true);
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
        private static void Wipe()
        {
            var folder = _dataFolder;
            if (folder == null) return;
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static string? _pendingConfig;

        private static async Task CallAsync(string command, string? arg = null, int generation = 0)
        {
            if (_ready == null || !_ready.Task.IsCompletedSuccessfully) return;   // nothing loaded to tell
            var core = _controller?.CoreWebView2;
            if (core == null) return;
            var payload = JsonSerializer.Serialize(new { command, arg, gen = generation });
            Log("send " + command + " " + arg);
            await core.ExecuteScriptAsync("window.amCommand(" + payload + ")").ConfigureAwait(true);
        }

        /// <summary>State the page reports: ready, state, ended, error.</summary>
        private static void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
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
                        string path; lock (Gate) path = _currentPath;
                        Fail(gen == 0 ? Volatile.Read(ref _generation) : gen, path, Sentence(code, msg));
                        break;
                    }
                }
            }
            catch (JsonException) { }
        }

        /// <summary>
        /// What is said when a song will not play. MusicKit's own messages are
        /// written for developers (one is a URL), so only its code is used to
        /// choose the sentence; the message itself goes to the log.
        /// </summary>
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
                return "That song isn't available to play on Apple Music.";
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
            lock (Gate)
            {
                if (generation != _generation) { Log("stale error ignored"); return; }
                if (string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase)) { _currentPath = ""; _playing = false; }
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
<head><meta charset="utf-8"><title>Apple Music</title></head>
<body>
<script>
let music = null, current = 0, switching = false, endedFor = -1, wantVolume = null;
// MusicKit can reset the volume when it builds the audio for a new song (it
// started at 0, silent), so the level asked for is kept and put back.
function applyVolume() { if (music && wantVolume !== null && music.volume !== wantVolume) music.volume = wantVolume; }
function post(obj) { try { window.chrome.webview.postMessage(JSON.stringify(obj)); } catch (e) {} }
function sendState() {
  // Not while a new song is being put in: what MusicKit says then is the old one's.
  if (!music || switching) return;
  post({ kind: "state", gen: current, playing: music.isPlaying, state: music.playbackState,
         position: music.currentPlaybackTime || 0, duration: music.currentPlaybackDuration || 0 });
}
function failedToLoad() { post({ kind: "error", message: "MusicKit could not be loaded from Apple." }); }
function codeOf(e) { return String((e && (e.errorCode || e.code || e.name)) || ""); }
window.amConfigure = async function (cfg) {
  try {
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
    music.addEventListener(E.nowPlayingItemDidChange, function () { applyVolume(); sendState(); });
    music.addEventListener(E.mediaPlaybackError, function (e) {
      if (switching) return;
      post({ kind: "error", gen: current, code: codeOf(e), message: (e && (e.description || e.message)) || "Playback error" });
    });
    music.addEventListener(E.playbackStateDidChange, function () {
      // 5 is "ended", 10 is "completed" in MusicKit's playback states.
      const s = music.playbackState;
      if ((s === 5 || s === 10) && !switching && endedFor !== current) {
        endedFor = current;
        post({ kind: "ended", gen: current });
      }
    });
    post({ kind: "ready", authorized: !!music.isAuthorized });
  } catch (e) { post({ kind: "error", message: String(e && e.message || e) }); }
};
window.amCommand = async function (c) {
  if (!music) { post({ kind: "error", gen: c.gen || current, message: "The player isn't ready." }); return; }
  if (c.command === "play") {
    const mine = c.gen;
    current = mine; switching = true;
    try {
      await music.setQueue({ song: c.arg });
      if (mine !== current) return;
      applyVolume();
      await music.play();
      applyVolume();
    } catch (e) {
      if (mine === current) post({ kind: "error", gen: mine, code: codeOf(e), message: String(e && e.message || e) });
    } finally {
      if (mine === current) { switching = false; sendState(); }
    }
    return;
  }
  try {
    switch (c.command) {
      case "pause": await music.pause(); break;
      case "resume": await music.play(); break;
      case "stop": current++; switching = false; await music.stop(); break;
      case "seekto": await music.seekToTime(parseFloat(c.arg || "0")); break;
      case "volume": wantVolume = parseFloat(c.arg || "1"); music.volume = wantVolume; break;
    }
    sendState();
  } catch (e) { post({ kind: "error", gen: current, code: codeOf(e), message: String(e && e.message || e) }); }
};
</script>
<!-- Just the player API, no web-component UI: nothing here is ever shown. -->
<script src="https://js-cdn.music.apple.com/musickit/v3/musickit.js" async onerror="failedToLoad()"></script>
</body>
</html>
""";
    }
}
