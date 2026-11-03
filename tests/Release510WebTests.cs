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
