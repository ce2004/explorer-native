using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// 5.1.0: the web app: the PC's player from the phone and the Drive trash.
    /// </summary>
    internal static class Release510WebTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        private const string Code = "12345678";

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("5.1.0, the web app: the PC's player from the phone and the Drive trash:");
            AlwaysPlayedFormatsAreAudio();
            DriveMessagesGoToThePhone();
            StaleDriveDownloadIsNotReused();
            await FunnelAndRenamedHost();
            await PcPlayerRoutes();
            PcPlayerPage();
            await TrashRoutes();
            TrashPage();
        }

        // MARK: Features

        /// <summary>The PC's player from the phone: state, buttons and Play on PC, behind the code and origin checks.</summary>
        private static async Task PcPlayerRoutes()
        {
            foreach (var (name, action) in new[] { ("playpause", AudioAction.PlayPause), ("previous", AudioAction.PreviousTrack),
                         ("next", AudioAction.NextTrack), ("back", AudioAction.SeekBackward), ("forward", AudioAction.SeekForward),
                         ("volumedown", AudioAction.VolumeDown), ("volumeup", AudioAction.VolumeUp) })
                Check($"the {name} button runs the desktop's {action}", ConnectServer.PlayerAction(name) == action);
            Check("anything else is no button", ConnectServer.PlayerAction("stop") == null && ConnectServer.PlayerAction(null) == null);

            var dir = Path.Combine(Path.GetTempPath(), "en-r510web-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var song = Path.Combine(dir, "song.flac");
            File.WriteAllBytes(song, new byte[10]);
            var player = new FakePlayer();
            int port = FreePort();
            var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port,
                files: new FakeFiles(), player: player);
            try
            {
                server.Start();
                if (!await WaitForListener(port)) { Check("the player server listens", false); return; }
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(20) };

                var (status, body) = await Call(http, HttpMethod.Get, "/api/pc/player");
                Check("the PC player's state is answered", status == 200 && body.GetProperty("title").GetString() == "Song" &&
                      body.GetProperty("artist").GetString() == "Artist" && body.GetProperty("playing").GetBoolean() &&
                      body.GetProperty("length").GetDouble() == 200 && body.GetProperty("seekSeconds").GetInt32() == 10, $"{status} {body}");

                (status, body) = await Call(http, HttpMethod.Post, "/api/pc/player/action", new { action = "playpause" });
                Check("Play/Pause runs PlayPause on the PC and answers with the new state",
                      status == 200 && player.Ran.SequenceEqual(new[] { AudioAction.PlayPause }) && !body.GetProperty("playing").GetBoolean(),
                      $"{status} {string.Join(",", player.Ran)}");
                (status, _) = await Call(http, HttpMethod.Post, "/api/pc/player/action", new { action = "format" });
                Equal("an unknown button is refused", "400", status.ToString());
                (status, _) = await Call(http, HttpMethod.Get, "/api/pc/player/action");
                Equal("a button is a POST", "405", status.ToString());
                (status, _) = await Call(http, HttpMethod.Post, "/api/pc/player/action", new { action = "next" }, code: "");
                Equal("without the pairing code it is refused", "401", status.ToString());
                (status, _) = await Call(http, HttpMethod.Post, "/api/pc/player/action", new { action = "next" }, origin: "https://evil.example.com");
                Equal("from another site it is refused", "403", status.ToString());
                Check("and neither ran anything", player.Ran.Count == 1, string.Join(",", player.Ran));

                (status, _) = await Call(http, HttpMethod.Post, "/api/pc/player/play", new { path = song });
                Check("Play on PC plays that file on the PC", status == 200 && player.Played == song, $"{status} {player.Played}");
                (status, _) = await Call(http, HttpMethod.Post, "/api/pc/player/play", new { path = Path.Combine(dir, "notes.txt") });
                Equal("a file the player doesn't play is refused", "400", status.ToString());
                player.Off = true;
                (status, body) = await Call(http, HttpMethod.Post, "/api/pc/player/play", new { path = song });
                Check("with the player off it says so", status == 409 && body.GetProperty("error").GetString()!.Contains("turned off"), $"{status}");

                (status, body) = await Call(http, HttpMethod.Get, "/api/formats");
                Check("the page is told the PC player is there", status == 200 && body.GetProperty("pcPlayer").GetBoolean());
            }
            finally { server.Dispose(); try { Directory.Delete(dir, true); } catch { } }

            port = FreePort();
            var without = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port, files: new FakeFiles());
            try
            {
                without.Start();
                if (!await WaitForListener(port)) return;
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(20) };
                var (status, _) = await Call(http, HttpMethod.Get, "/api/pc/player");
                Equal("with no player the route is 503", "503", status.ToString());
            }
            finally { without.Dispose(); }
        }

        private static void PcPlayerPage()
        {
            var js = File.ReadAllText(Path.Combine(WebDir(), "app.js"));
            var html = File.ReadAllText(Path.Combine(WebDir(), "index.html"));
            foreach (var id in new[] { "pc-play", "pc-prev", "pc-next", "pc-back", "pc-fwd", "pc-vol-down", "pc-vol-up" })
                Check($"the {id} button has a label", Regex(html, "id=\"" + id + "\"[^>]*aria-label=\"[^\"]+\"[^>]*>[^<]+<"));
            Check("the section opens with a button that says whether it is open", Regex(html, "id=\"pc-toggle\" aria-expanded=\"false\" aria-controls=\"pc-player\""));
            Check("state is polled every few seconds only while open, on screen and visible",
                  Regex(js, @"const want = pc\.open && currentTab === 'player' && !document\.hidden;") && Regex(js, @"setInterval\(pcRefresh, 3000\)") &&
                  Regex(js, @"clearInterval\(pc\.timer\)"));
            Check("results go through the live region", Regex(js, @"announce\(pcSaid\(action, s\)\)") && js.Contains("s.playing ? 'Playing' : 'Paused'"));
            Check("an audio file's More has Play on PC", js.Contains("items.push(['playpc', 'Play on PC'])") && js.Contains("case 'playpc': await playOnPc(full, e.name)"));
            Check("every player fetch has a time limit", Regex(js, @"api\('/api/pc/player', \{ timeout:") && Regex(js, @"api\('/api/pc/player/action'[^\n]*timeout:") &&
                  Regex(js, @"api\('/api/pc/player/play'[^\n]*timeout:"));
        }

        /// <summary>Restore from Drive's trash, and Undo after a Drive delete.</summary>
        private static async Task TrashRoutes()
        {
            var files = new FakeFiles { TrashedIds = new[] { "id-a", "id-b" } };
            int port = FreePort();
            var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port, files: files);
            try
            {
                server.Start();
                if (!await WaitForListener(port)) { Check("the trash server listens", false); return; }
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(20) };

                var (status, body) = await Call(http, HttpMethod.Post, "/api/delete", new { paths = new[] { @"Q:\a.flac", @"Q:\b.flac" } });
                var ticket = status == 200 && body.TryGetProperty("undo", out var u) ? u.GetString() : null;
                Check("a Drive delete hands back an Undo", !string.IsNullOrEmpty(ticket), $"{status} {body}");

                (status, _) = await Call(http, HttpMethod.Post, "/api/restore", new { undo = ticket }, origin: "https://evil.example.com");
                Equal("Undo from another site is refused", "403", status.ToString());
                (status, body) = await Call(http, HttpMethod.Post, "/api/restore", new { undo = ticket });
                Check("Undo puts back exactly what that delete trashed",
                      status == 200 && body.GetProperty("restored").GetInt32() == 2 && string.Join(",", files.Untrashed) == "id-a,id-b", $"{status} {body}");
                (status, _) = await Call(http, HttpMethod.Post, "/api/restore", new { undo = ticket });
                Equal("an Undo works once", "410", status.ToString());
                (status, _) = await Call(http, HttpMethod.Post, "/api/restore", new { undo = "made-up" });
                Equal("a made-up Undo does nothing", "410", status.ToString());

                files.TrashedIds = Array.Empty<string>();
                (status, body) = await Call(http, HttpMethod.Post, "/api/delete", new { paths = new[] { @"C:\x.txt" } });
                Check("a delete with nothing trashed on Drive offers no Undo", status == 200 && body.GetProperty("undo").ValueKind == JsonValueKind.Null, $"{body}");

                (status, body) = await Call(http, HttpMethod.Post, "/api/restore", new { paths = new[] { @"Q:\Trash\a.flac" } });
                Check("Restore restores the named items", status == 200 && body.GetProperty("restored").GetInt32() == 1 &&
                      files.Restored.SequenceEqual(new[] { @"Q:\Trash\a.flac" }), $"{status} {body}");
                (status, _) = await Call(http, HttpMethod.Post, "/api/restore", new { paths = new[] { @"Q:\Trash\a.flac" } }, code: "");
                Equal("Restore needs the pairing code", "401", status.ToString());
                (status, _) = await Call(http, HttpMethod.Get, "/api/restore");
                Equal("Restore is a POST", "405", status.ToString());

                (status, body) = await Call(http, HttpMethod.Get, "/api/formats");
                Check("the page is told where Drive's trash is", status == 200 && body.GetProperty("trash")[0].GetString() == @"Q:\Trash", $"{body}");
            }
            finally { server.Dispose(); }

            Check("an Undo lasts longer than the phone offers it", ConnectServer.UndoFor >= TimeSpan.FromSeconds(10));
        }

        private static void TrashPage()
        {
            var js = File.ReadAllText(Path.Combine(WebDir(), "app.js"));
            Check("More offers Restore straight inside Drive's trash", js.Contains("if (inDriveTrash(full)) items.push(['restore', 'Restore'])"));
            Check("and calls Delete there Delete forever", js.Contains("underDriveTrash(full) ? ['delete', 'Delete forever']"));
            Check("Delete in the trash says plainly that it deletes forever", Regex(js, @"if \(paths\.some\(underDriveTrash\)\)\s*\{\s*await dialog\(\{ title: 'Delete forever', body: 'Deleting from the Google Drive trash deletes it forever"));
            Check("after a Drive delete the phone says Undo is there", js.Contains("'Moved to Google Drive trash. Undo is available.'") && js.Contains("if (r.undo) offerUndo(r.undo)"));
            Check("for about ten seconds", Regex(js, @"undoTimer = setTimeout\(hideUndo, 10000\)"));
            Check("and a hidden Undo button gives focus back", Regex(js, @"if \(had\) \$\('files-title'\)\.focus\(\)"));
            var html = File.ReadAllText(Path.Combine(WebDir(), "index.html"));
            Check("the Undo button names what it undoes", html.Contains("id=\"btn-undo\" hidden>Undo delete<"));
        }

        private sealed class FakePlayer : IConnectPlayer
        {
            public readonly List<AudioAction> Ran = new();
            public string? Played;
            public bool Off;
            private bool _playing = true;
            public ConnectPlayerState State() => new(true, _playing, "Song", "Artist", 12, 200, 50, false, false, 10);
            public Task<ConnectPlayerState> RunAsync(AudioAction action, CancellationToken token)
            {
                Ran.Add(action);
                if (action == AudioAction.PlayPause) _playing = !_playing;
                return Task.FromResult(State());
            }
            public Task<bool> PlayFileAsync(string path, CancellationToken token) { Played = path; return Task.FromResult(!Off); }
        }

        private sealed class FakeFiles : IConnectFiles
        {
            public IReadOnlyList<string> TrashedIds = Array.Empty<string>();
            public readonly List<string> Untrashed = new();
            public readonly List<string> Restored = new();
            public string? Unavailable(string path) => null;
            public (long Used, long Limit)? DriveQuota() => null;
            public Task<ConnectSize> SizeAsync(string folder, CancellationToken token) => Task.FromResult(new ConnectSize(folder, 0, 0, 0, true));
            public Task<ConnectStat> StatAsync(string path, CancellationToken token) => Task.FromResult(new ConnectStat(path, "x", true, 0, DateTime.UtcNow, DateTime.UtcNow, false, false));
            public Task<string> RenameAsync(string path, string newName, CancellationToken token) => Task.FromResult(path);
            public Task<string> CreateFolderAsync(string parent, string name, CancellationToken token) => Task.FromResult(parent);
            public Task<ConnectDeleted> DeleteAsync(IReadOnlyList<string> paths, CancellationToken token) =>
                Task.FromResult(new ConnectDeleted(paths.Count, Array.Empty<ConnectFailure>()) { Trashed = TrashedIds });
            public void CheckTransfer(IReadOnlyList<string> paths, string destination, bool move) { }
            public Task TransferAsync(ConnectJob job, IReadOnlyList<string> paths, string destination, bool move, PasteConflictPolicy conflict) => Task.CompletedTask;
            public Task<string> UploadAsync(string folder, string name, PasteConflictPolicy conflict, Stream body, CancellationToken token) => Task.FromResult(folder);
            public bool OnDrive(string path) => path.StartsWith("Q:", StringComparison.OrdinalIgnoreCase);
            public Task<string> PlaceFileAsync(string file, string folder, string name, PasteConflictPolicy conflict, Action<long, long>? progress, CancellationToken token) => Task.FromResult(folder);
            public IReadOnlyList<string> AudioExtensions() => new[] { ".flac" };
            public Task<Dictionary<string, object?>> DetailsAsync(string path, ConnectStat stat, bool hash, CancellationToken token) => Task.FromResult(new Dictionary<string, object?>());
            public IReadOnlyList<string> TrashFolders() => new[] { @"Q:\Trash" };
            public Task<ConnectDeleted> RestoreAsync(IReadOnlyList<string> paths, CancellationToken token)
            {
                Restored.AddRange(paths);
                return Task.FromResult(new ConnectDeleted(paths.Count, Array.Empty<ConnectFailure>()));
            }
            public Task<int> UndoTrashAsync(IReadOnlyList<string> ids, CancellationToken token)
            {
                Untrashed.AddRange(ids);
                return Task.FromResult(ids.Count);
            }
        }

        private static async Task<(int Status, JsonElement Body)> Call(HttpClient http, HttpMethod method, string url, object? body = null,
            string code = Code, string? origin = null)
        {
            var request = new HttpRequestMessage(method, url);
            if (code.Length > 0) request.Headers.Add("X-Connect-Code", code);
            if (origin != null) request.Headers.Add("Origin", origin);
            if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var r = await http.SendAsync(request);
            var text = await r.Content.ReadAsStringAsync();
            JsonElement parsed = default;
            try { parsed = JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement; } catch { parsed = JsonDocument.Parse("{}").RootElement; }
            return ((int)r.StatusCode, parsed);
        }

        // MARK: Bugs

        /// <summary>Bug 1: .tta, .w64, .voc and friends play on the PC but were downloads on the phone.</summary>
        private static void AlwaysPlayedFormatsAreAudio()
        {
            var files = new ConnectFiles(null, new Settings { AudioExtensions = ".mp3" });
            var audio = files.AudioExtensions();
            foreach (var ext in new[] { ".caf", ".tta", ".w64", ".voc", ".au", ".snd", ".flv", ".aifc", ".mp3" })
                Check($"the web app counts {ext} as audio", audio.Contains(ext), string.Join(" ", audio));
            Check("and lists each once", audio.Count == audio.Distinct().Count());
        }

        /// <summary>
        /// Bugs 2 and 3: Drive actions from the phone were spoken on the PC, item by item, and the phone heard a
        /// guessed reason instead of the real one.
        /// </summary>
        private static void DriveMessagesGoToThePhone()
        {
            var drive = (GoogleDrive)RuntimeHelpers.GetUninitializedObject(typeof(GoogleDrive));
            var heardOnPc = new List<string>();
            var field = typeof(GoogleDrive).GetField("_notification", BindingFlags.Instance | BindingFlags.NonPublic);
            field?.SetValue(drive, (Action<string, string>)((id, m) => heardOnPc.Add(m)));
            var say = typeof(GoogleDrive).GetMethod("Say", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null || say == null) { Check("GoogleDrive.Say is there", false); return; }

            string? toPhone = null;
            using (GoogleDrive.SayingTo((_, m) => toPhone = m))
                say.Invoke(drive, new object[] { "drive.trash.failed", "Could not move a.flac to the trash: it is already there" });
            Check("inside SayingTo, a Drive message goes to the caller", toPhone == "Could not move a.flac to the trash: it is already there", toPhone);
            Check("and is not said on the PC", heardOnPc.Count == 0, string.Join("|", heardOnPc));
            say.Invoke(drive, new object[] { "drive.trash", "b.flac moved to the Google Drive trash" });
            Check("outside it, the PC says it as before", heardOnPc.Count == 1);

            var src = File.ReadAllText(Path.Combine(SourceDir(), "ConnectFiles.cs"));
            Check("web delete hands the phone what Drive said", src.Contains("trashed = await drive.TrashPath(path, token)") &&
                  src.Contains("said != null ? ConnectServer.Sentence(said) : \"Google Drive would not move it to the trash.\""));
            Check("web rename too", src.Contains("said != null ? ConnectServer.Sentence(said) : \"Google Drive would not rename it.\""));
            Check("delete inside the trash says it would delete forever", ConnectFiles.TrashDeleteRefusal.Contains("forever"));
            Check("copies, moves and uploads are quiet on the PC",
                  Regex(src, @"Task TransferAsync\([^{]*\{\s*//[^\n]*\n\s*using var quiet = GoogleDrive\.SayingTo") &&
                  Regex(src, @"Task<string> UploadAsync\([^{]*\{\s*using var quiet = GoogleDrive\.SayingTo") &&
                  Regex(src, @"Task<string> PlaceFileAsync\([^{]*\{\s*using var quiet = GoogleDrive\.SayingTo"));
        }

        /// <summary>Bug 4: a Drive file changed in the last minute was served from the old download.</summary>
        private static void StaleDriveDownloadIsNotReused()
        {
            string stamp = "id1|10|1";
            var opened = new List<FakeSource>();
            var streams = new ConnectStreams(_ => { var s = new FakeSource(); opened.Add(s); return s; }, stamp: _ => stamp);
            try
            {
                var a = streams.Lease(@"G:\notes.txt");
                a?.Dispose();
                var b = streams.Lease(@"G:\notes.txt");
                b?.Dispose();
                Check("the same file is opened once", opened.Count == 1, opened.Count.ToString());

                stamp = "id2|12|2";
                var held = streams.Lease(@"G:\notes.txt");
                Check("a replaced file is opened again", opened.Count == 2, opened.Count.ToString());
                Check("and the old download is let go", opened[0].Disposed > 0);

                stamp = "id3|14|3";
                var fresh = streams.Lease(@"G:\notes.txt");
                Check("a change while the old one is still being read opens a new one", opened.Count == 3);
                Check("without cutting off the reader of the old one", opened[1].Disposed == 0);
                held?.Dispose();
                Check("which is let go when its reader is done", opened[1].Disposed > 0);
                fresh?.Dispose();
                Check("one file is held", streams.Count == 1, streams.Count.ToString());
            }
            finally { streams.Dispose(); }
        }

        /// <summary>
        /// Bug 5: Funnel traffic was answered. Bug 6: after the PC's Tailscale name changed every request got 421
        /// until a restart.
        /// </summary>
        private static async Task FunnelAndRenamedHost()
        {
            string name = "laptop.tail3d7403.ts.net.";
            string Status() => "{ \"BackendState\": \"Running\", \"Self\": { \"DNSName\": \"" + name + "\", \"HostName\": \"laptop\", \"UserID\": 1 }, " +
                               "\"User\": { \"1\": { \"LoginName\": \"me@example.com\" } } }";
            int port = FreePort();
            var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port,
                tailscaleStatus: Status);
            try
            {
                server.Start();
                if (!await WaitForListener(port)) { Check("the server listens", false); return; }
                await server.LoginSettled();
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(20) };

                var funnel = new HttpRequestMessage(HttpMethod.Get, "/api/info");
                funnel.Headers.Add("X-Connect-Code", Code);
                funnel.Headers.Add("Tailscale-Funnel-Request", "?1");
                var r = await http.SendAsync(funnel);
                Equal("a request through Funnel is refused", "403", ((int)r.StatusCode).ToString());

                var plain = new HttpRequestMessage(HttpMethod.Get, "/api/info");
                plain.Headers.Add("X-Connect-Code", Code);
                r = await http.SendAsync(plain);
                Equal("the same request from the tailnet is answered", "200", ((int)r.StatusCode).ToString());

                name = "laptop-1.tail3d7403.ts.net.";
                // As if the last look at Tailscale was a while ago.
                typeof(ConnectServer).GetField("_loginAt", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(server, DateTime.MinValue);
                var renamed = new HttpRequestMessage(HttpMethod.Get, "/api/info");
                renamed.Headers.Add("X-Connect-Code", Code);
                renamed.Headers.Host = "laptop-1.tail3d7403.ts.net";
                r = await http.SendAsync(renamed);
                Equal("after the PC is renamed in Tailscale, its new name is answered", "200", ((int)r.StatusCode).ToString());

                var stranger = new HttpRequestMessage(HttpMethod.Get, "/api/info");
                stranger.Headers.Add("X-Connect-Code", Code);
                stranger.Headers.Host = "evil.example.com";
                r = await http.SendAsync(stranger);
                Equal("a name that is not this PC's is still refused", "421", ((int)r.StatusCode).ToString());
            }
            finally { server.Dispose(); }
        }

        // MARK: Helpers

        private sealed class FakeSource : IRangeSource
        {
            public int Disposed;
            public long Length => 1000;
            public int Streams => 1;
            public Task<int> ReadAsync(long offset, Memory<byte> into, CancellationToken token)
            {
                int n = (int)Math.Min(into.Length, Math.Max(0, Length - offset));
                into.Span[..n].Fill(7);
                return Task.FromResult(n);
            }
            public void Dispose() => Interlocked.Increment(ref Disposed);
        }

        private static bool Regex(string text, string pattern) =>
            System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.Singleline);

        private static string SourceDir()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (Directory.Exists(Path.Combine(dir, "src")) && File.Exists(Path.Combine(dir, "src", "ConnectFiles.cs")))
                    return Path.Combine(dir, "src");
                dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
            }
            return Path.Combine(Directory.GetCurrentDirectory(), "src");
        }

        private static string WebDir() => Path.Combine(Path.GetDirectoryName(SourceDir())!, "web");

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static async Task<bool> WaitForListener(int port)
        {
            for (int i = 0; i < 50; i++)
            {
                try
                {
                    using var c = new TcpClient();
                    await c.ConnectAsync(IPAddress.Loopback, port);
                    return true;
                }
                catch (SocketException) { await Task.Delay(100); }
            }
            return false;
        }
    }
}
