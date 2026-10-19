using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ExplorerNative
{
    /// <summary>
    /// Plays Apple Music the sanctioned way: Apple's own MusicKit JS player,
    /// hosted in a hidden WebView2, decrypting and playing through the system's
    /// own browser engine with the person's subscription. The app never touches
    /// the protected audio; it only tells the player what to play and relays the
    /// keyboard commands (play, pause, stop, seek, volume, next, previous).
    ///
    /// Because Apple's player keeps the audio sealed, the processing chain
    /// (speed, pitch, EQ, limiter, silence removal) does not apply here, and the
    /// audio goes to the system default output. Everything else about the
    /// player's controls is mirrored so it feels like the built-in player.
    ///
    /// The WebView2 keeps its own profile folder, so the one-time Apple sign-in
    /// for playback persists between runs.
    /// </summary>
    internal static class AppleMusicPlayer
    {
        private const string Origin = "https://explorer-native.applemusic";

        private static Form? _host;
        private static WebView2? _view;
        private static TaskCompletionSource<bool>? _ready;
        private static SynchronizationContext? _ui;
        private static readonly object Gate = new();

        // What is playing now, on the UI thread.
        private static string _currentPath = "";
        private static string _currentName = "";
        private static bool _playing;
        private static double _positionSeconds;
        private static double _durationSeconds;
        private static int _volumePercent = 100;

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

        /// <summary>Must be called once on the UI thread (the tray does this) so the host can live there.</summary>
        public static void BindUiThread() => _ui = SynchronizationContext.Current;

        // ---- Commands (any thread; marshalled to the UI) ----

        /// <summary>
        /// Start a song. <paramref name="path"/> is the ::applemusic row path (for
        /// play-on and "what is playing"); <paramref name="catalogId"/> is Apple's
        /// id; <paramref name="spokenName"/> is "Title by Artist".
        /// </summary>
        public static void Play(string path, string catalogId, string spokenName)
        {
            lock (Gate) { _currentPath = path; _currentName = spokenName; _playing = false; _positionSeconds = 0; _durationSeconds = 0; }
            OnUi(async () =>
            {
                try
                {
                    await EnsureReadyAsync().ConfigureAwait(true);
                    await CallAsync("play", catalogId).ConfigureAwait(true);
                }
                catch (AppleMusicException ex) { Fail(path, ex.Message); }
                catch (Exception ex) { Fail(path, "Apple Music couldn't start the song: " + ex.Message); }
            });
        }

        public static string TogglePlayPause() { OnUi(() => _ = CallAsync("toggle")); return ""; }
        public static string Pause() { OnUi(() => _ = CallAsync("pause")); return ""; }
        public static string Resume() { OnUi(() => _ = CallAsync("resume")); return ""; }

        public static string Stop()
        {
            lock (Gate) { _currentPath = ""; _currentName = ""; _playing = false; }
            OnUi(() => _ = CallAsync("stop"));
            return "Stopped";
        }

        public static string Seek(int deltaSeconds)
        {
            OnUi(() => _ = CallAsync("seek", deltaSeconds.ToString()));
            return "";
        }

        public static string SeekToStart() { OnUi(() => _ = CallAsync("seekto", "0")); return ""; }

        public static string Next() { OnUi(PlayNext); return ""; }

        public static string Previous()
        {
            // Within the first few seconds, Previous restarts; otherwise the song before.
            double pos; lock (Gate) pos = _positionSeconds;
            if (pos > 3) { SeekToStart(); return ""; }
            OnUi(PlayPrevious);
            return "";
        }

        public static string SetVolumePercent(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            lock (Gate) _volumePercent = percent;
            OnUi(() => _ = CallAsync("volume", (percent / 100.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
            return $"Volume {percent} percent";
        }

        public static int VolumePercent { get { lock (Gate) return _volumePercent; } }

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
            if (next == null) { Say?.Invoke("applemusic.endoflist", "End of the list"); return; }
            StartPath(next);
        }

        private static void PlayPrevious()
        {
            string path; lock (Gate) path = _currentPath;
            var prev = path.Length == 0 ? null : AppleMusic.PreviousSongBefore(path);
            if (prev == null) { SeekToStart(); return; }
            StartPath(prev);
        }

        private static async void PlayNextFromStation(string finished)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var folder = Path.GetDirectoryName(finished)!;
                var more = await AppleMusic.MoreFromStationAsync(folder, cts.Token).ConfigureAwait(true);
                foreach (var row in more)
                    if (!row.IsFolder) { StartPath(row.Path); return; }
                Say?.Invoke("applemusic.endoflist", "The station has no more songs");
            }
            catch (Exception) { Say?.Invoke("applemusic.endoflist", "The station has no more songs"); }
        }

        /// <summary>Enter on a song row: start it in Apple's player, saying its name. Returns false only if it isn't a song.</summary>
        public static bool PlayRow(string path)
        {
            try
            {
                var id = AppleMusic.CatalogIdForPlaying(path);
                var name = AppleMusic.SpokenName(path) ?? Path.GetFileNameWithoutExtension(path);
                Play(path, id, name);
                return true;
            }
            catch (AppleMusicException ex) { Fail(path, ex.Message); return true; }
        }

        /// <summary>Resolve a row path to its song and play it, or say why not.</summary>
        private static void StartPath(string path)
        {
            try
            {
                var id = AppleMusic.CatalogIdForPlaying(path);
                var name = AppleMusic.SpokenName(path) ?? Path.GetFileNameWithoutExtension(path);
                Play(path, id, name);
                Say?.Invoke("audio.play", name);
            }
            catch (AppleMusicException ex) { Fail(path, ex.Message); }
        }

        // ---- The host and the bridge ----

        private static void OnUi(Action action)
        {
            var ui = _ui;
            if (ui == null) { action(); return; }
            ui.Post(_ => action(), null);
        }

        private static Task EnsureReadyAsync()
        {
            if (_ready != null) return _ready.Task;
            _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = StartHostAsync();
            return _ready.Task;
        }

        private static async Task StartHostAsync()
        {
            try
            {
                _host = new Form
                {
                    FormBorderStyle = FormBorderStyle.FixedToolWindow,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-4000, -4000),
                    Size = new System.Drawing.Size(320, 240),
                    Opacity = 0,
                };
                _host.CreateControl();
                _view = new WebView2 { Dock = DockStyle.Fill };
                _host.Controls.Add(_view);

                // Nothing is kept: the sign-in is injected afresh each time, so
                // the player's profile is a throwaway folder under %TEMP%. Older
                // ones from past runs (or a crash) are swept first, and this one
                // goes when the app exits. So the player leaves nothing behind.
                var dataFolder = NewThrowawayProfile();
                var env = await CoreWebView2Environment.CreateAsync(null, dataFolder).ConfigureAwait(true);
                await _view.EnsureCoreWebView2Async(env).ConfigureAwait(true);

                var core = _view.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.WebMessageReceived += OnWebMessage;

                // The page is served from a made-up https origin, which MusicKit
                // needs; the files live in the profile folder.
                File.WriteAllText(Path.Combine(dataFolder, "player.html"), PlayerHtml, new UTF8Encoding(false));
                core.SetVirtualHostNameToFolderMapping("explorer-native.applemusic", dataFolder,
                    CoreWebView2HostResourceAccessKind.Allow);

                var token = await AppleMusic.DeveloperTokenAsync(CancellationToken.None).ConfigureAwait(true);
                var userToken = AppleMusic.UserToken;
                _pendingConfig = JsonSerializer.Serialize(new { developerToken = token, userToken });

                core.NavigationCompleted += (_, e) =>
                {
                    if (e.IsSuccess && _pendingConfig != null)
                        _ = core.ExecuteScriptAsync("window.amConfigure(" + _pendingConfig + ")");
                };
                core.Navigate(Origin + "/player.html");
            }
            catch (Exception ex)
            {
                _ready?.TrySetException(new AppleMusicException("Apple Music's player couldn't start: " + ex.Message));
            }
        }

        private static string? _dataFolder;

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
            var mine = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mine);
            _dataFolder = mine;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Wipe();
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

        private static async Task CallAsync(string command, string? arg = null)
        {
            await EnsureReadyAsync().ConfigureAwait(true);
            var view = _view;
            if (view?.CoreWebView2 == null) return;
            var payload = JsonSerializer.Serialize(new { command, arg });
            await view.CoreWebView2.ExecuteScriptAsync("window.amCommand(" + payload + ")").ConfigureAwait(true);
        }

        /// <summary>State the page reports: ready, playing, paused, position, ended, error.</summary>
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
                switch (kind)
                {
                    case "ready":
                        _ready?.TrySetResult(true);
                        break;
                    case "state":
                        lock (Gate)
                        {
                            _playing = root.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.True;
                            if (root.TryGetProperty("position", out var pos) && pos.TryGetDouble(out var ps)) _positionSeconds = ps;
                            if (root.TryGetProperty("duration", out var dur) && dur.TryGetDouble(out var ds)) _durationSeconds = ds;
                        }
                        break;
                    case "ended":
                        PlayNext();
                        break;
                    case "error":
                        string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                        string path; lock (Gate) path = _currentPath;
                        Fail(path, msg.Length > 0 ? "Apple Music: " + msg : "Apple Music couldn't play that song.");
                        break;
                }
            }
            catch (JsonException) { }
        }

        private static void Fail(string path, string message)
        {
            lock (Gate) if (string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase)) _currentPath = "";
            _ready?.TrySetResult(true); // so later commands don't hang on a dead sign-in
            Say?.Invoke("applemusic.failed", message);
        }

        /// <summary>
        /// The player page. Loads Apple's MusicKit JS v3, configures it with the
        /// developer token, seeds the music user token so no second sign-in is
        /// needed, and turns window messages into play/pause/seek/volume calls.
        /// It reports state back so the app can speak "what is playing".
        /// </summary>
        private const string PlayerHtml = """
<!doctype html>
<html>
<head><meta charset="utf-8"><title>Apple Music</title></head>
<body>
<!-- Just the player API, no web-component UI: nothing here is ever shown. -->
<script src="https://js-cdn.music.apple.com/musickit/v3/musickit.js" async></script>
<script>
let music = null;
function post(obj) { try { window.chrome.webview.postMessage(JSON.stringify(obj)); } catch (e) {} }
function sendState() {
  if (!music) return;
  post({ kind: "state", playing: music.isPlaying,
         position: music.currentPlaybackTime || 0, duration: music.currentPlaybackDuration || 0 });
}
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
    music.addEventListener(E.nowPlayingItemDidChange, sendState);
    music.addEventListener(E.mediaPlaybackError, function (e) {
      post({ kind: "error", message: (e && (e.description || e.message)) || "Playback error" });
    });
    music.addEventListener(E.playbackStateDidChange, function () {
      // 5 is "ended", 10 is "completed" in MusicKit's playback states.
      if (music.playbackState === 5 || music.playbackState === 10) post({ kind: "ended" });
    });
    post({ kind: "ready" });
  } catch (e) { post({ kind: "error", message: String(e && e.message || e) }); }
};
window.amCommand = async function (c) {
  if (!music) { post({ kind: "error", message: "The player isn't ready." }); return; }
  try {
    switch (c.command) {
      case "play":
        await music.setQueue({ song: c.arg });
        await music.play();
        break;
      case "toggle":
        if (music.isPlaying) await music.pause(); else await music.play();
        break;
      case "pause": await music.pause(); break;
      case "resume": await music.play(); break;
      case "stop": await music.stop(); break;
      case "seek": {
        let t = Math.max(0, (music.currentPlaybackTime || 0) + parseFloat(c.arg || "0"));
        await music.seekToTime(t);
        break;
      }
      case "seekto": await music.seekToTime(parseFloat(c.arg || "0")); break;
      case "volume": music.volume = parseFloat(c.arg || "1"); break;
    }
    sendState();
  } catch (e) { post({ kind: "error", message: String(e && e.message || e) }); }
};
</script>
</body>
</html>
""";
    }
}
