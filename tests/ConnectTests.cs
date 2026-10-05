using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Explorer Native Connect: the HTTP server the iPhone app talks to, driven over a real socket.
    ///
    /// Loopback only, on a port nobody else holds, against a folder of temporary files — never a real drive,
    /// and never the Drive letter, where a test process reading a placeholder can fail to exit. What is worth
    /// testing here is exactly the part a phone cannot tell you about: a byte range off by one is a player that
    /// seeks to the wrong place, a HEAD with a body is a kept-alive connection that reads garbage as its next
    /// response, and a plus sign decoded as a space is a file with a plus in its name that never opens.
    /// </summary>
    internal static class ConnectTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        private const string Code = "12345678";

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Explorer Native Connect:");

            RangeTests();
            TailscaleTests();
            await ServerTests();
            await ActionTests();
            await StreamPoolTests();
            await HangUpTests();
            await LocalFilesTests();
            await ResumableUploadTests();
            await AudioTests();
            await ClipboardTests();
            await PingTests();
            await DetailsTests();
            await WebAppTests();
        }

        /// <summary>
        /// The web app: Tailscale's JSON read right, Serve never taken from anybody
        /// else, the owner let in by Serve's header and nobody else by it, and the
        /// app's own files served with the right types.
        /// </summary>
        private static async Task WebAppTests()
        {
            Console.WriteLine("Web app:");
            const string status = """
                { "BackendState": "Running",
                  "Self": { "DNSName": "laptop.tail3d7403.ts.net.", "HostName": "laptop", "UserID": 5663247441147785,
                            "TailscaleIPs": ["100.67.248.25"] },
                  "User": { "5663247441147785": { "LoginName": "me@example.com" } },
                  "CertDomains": ["laptop.tail3d7403.ts.net"] }
                """;
            var s = TailscaleWeb.ParseStatus(status);
            Check("a signed-in Tailscale reads as running", s.State == TailscaleWeb.State.Running);
            Equal("with the PC's own login", "me@example.com", s.Login ?? "");
            Equal("and its name without the trailing dot", "laptop.tail3d7403.ts.net", s.DnsName ?? "");
            Check("and HTTPS on, from CertDomains", s.HttpsEnabled);
            Equal("the link is the ts.net address", "https://laptop.tail3d7403.ts.net/", TailscaleWeb.Link(s) ?? "");
            Check("NeedsLogin reads as needing a sign-in",
                TailscaleWeb.ParseStatus("""{ "BackendState": "NeedsLogin", "Self": {} }""").State == TailscaleWeb.State.NeedsLogin);
            Check("Stopped reads as not running",
                TailscaleWeb.ParseStatus("""{ "BackendState": "Stopped" }""").State == TailscaleWeb.State.NotRunning);
            Check("no CertDomains means HTTPS is off",
                !TailscaleWeb.ParseStatus("""{ "BackendState": "Running", "Self": {} }""").HttpsEnabled);

            Check("an empty Serve config is off", TailscaleWeb.ParseServe("{}") == TailscaleWeb.ServeState.Off);
            Check("nothing at all is off", TailscaleWeb.ParseServe("") == TailscaleWeb.ServeState.Off);
            Check("our own handler is ours", TailscaleWeb.ParseServe("""
                { "TCP": { "443": { "HTTPS": true } },
                  "Web": { "laptop.tail3d7403.ts.net:443": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:47810" } } } } }
                """) == TailscaleWeb.ServeState.Ours);
            Check("somebody else's proxy on 443 is theirs", TailscaleWeb.ParseServe("""
                { "TCP": { "443": { "HTTPS": true } },
                  "Web": { "laptop.tail3d7403.ts.net:443": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:3000" } } } } }
                """) == TailscaleWeb.ServeState.TakenByOther);
            Check("ours plus another path on 443 is left alone", TailscaleWeb.ParseServe("""
                { "Web": { "x.ts.net:443": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:47810" }, "/grafana": { "Proxy": "http://127.0.0.1:3000" } } } } }
                """) == TailscaleWeb.ServeState.TakenByOther);
            Check("a raw TCP forward on 443 is theirs", TailscaleWeb.ParseServe("""
                { "TCP": { "443": { "TCPForward": "127.0.0.1:22" } } }
                """) == TailscaleWeb.ServeState.TakenByOther);
            Check("Serve on another port does not block 443", TailscaleWeb.ParseServe("""
                { "TCP": { "8443": { "HTTPS": true } },
                  "Web": { "x.ts.net:8443": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:3000" } } } } }
                """) == TailscaleWeb.ServeState.Off);
            Equal("the enable-HTTPS link is found in Tailscale's message",
                "https://login.tailscale.com/f/serve?node=nABC123",
                TailscaleWeb.EnableLink("Serve is not enabled on your tailnet.\nTo enable, visit:\n\n         https://login.tailscale.com/f/serve?node=nABC123\n") ?? "");

            var loop = IPAddress.Loopback;
            Check("Serve's header for the PC's owner is trusted from loopback",
                ConnectServer.TrustsServeUser(loop, "me@example.com", "me@example.com"));
            Check("and matched without regard to case",
                ConnectServer.TrustsServeUser(loop, "Me@Example.com", "me@example.com"));
            Check("another tailnet user is not", !ConnectServer.TrustsServeUser(loop, "friend@example.com", "me@example.com"));
            Check("the header straight from a tailnet peer is never believed",
                !ConnectServer.TrustsServeUser(IPAddress.Parse("100.100.1.1"), "me@example.com", "me@example.com"));
            Check("loopback without the header is not trusted", !ConnectServer.TrustsServeUser(loop, null, "me@example.com"));
            Check("nothing is trusted before the PC's login is known", !ConnectServer.TrustsServeUser(loop, "me@example.com", null));
            Check("an IPv4-mapped loopback counts as loopback",
                ConnectServer.TrustsServeUser(IPAddress.Parse("::ffff:127.0.0.1"), "me@example.com", "me@example.com"));

            int port = FreePort();
            var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port,
                files: new FakeFiles(), tailscaleStatus: () => status);
            try
            {
                server.Start();
                if (!await WaitForListener(port)) { Check("the web app server listens", false); return; }
                await server.PathsSettled();
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };

                async Task<(int Status, string Type, string Body)> Get(string url, string? login = null, bool code = false)
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (login != null) request.Headers.Add("Tailscale-User-Login", login);
                    if (code) request.Headers.Add("X-Connect-Code", Code);
                    var response = await http.SendAsync(request);
                    return ((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType ?? "",
                        await response.Content.ReadAsStringAsync());
                }

                foreach (var (url, type) in new[]
                         {
                             ("/", "text/html"), ("/app/app.js", "text/javascript"), ("/app/app.css", "text/css"),
                             ("/sw.js", "text/javascript"), ("/manifest.webmanifest", "application/manifest+json"),
                             ("/app/icon-192.png", "image/png"), ("/apple-touch-icon.png", "image/png"),
                         })
                {
                    var (code, got, _) = await Get(url);
                    Check($"{url} is served without a code, as {type}", code == 200 && got == type, $"{code} {got}");
                }
                var (_, _, html) = await Get("/");
                Check("the page is the Explorer Connect app", html.Contains("Explorer Connect") && html.Contains("/app/app.js"));
                var (missing, _, _) = await Get("/app/../settings.json");
                Check("a path climbing out of the app is not served", missing != 200, missing.ToString());

                Equal("the API still needs the code", "401", (await Get("/api/info")).Status.ToString());
                Equal("the PC's owner through Serve gets in without it", "200", (await Get("/api/info", "me@example.com")).Status.ToString());
                Equal("another tailnet user does not", "401", (await Get("/api/info", "friend@example.com")).Status.ToString());
                Equal("but does with the code", "200", (await Get("/api/info", "friend@example.com", code: true)).Status.ToString());
                Equal("and the iPhone app's code still works as before", "200", (await Get("/api/drives", code: true)).Status.ToString());

                // The web app asks for details under a name tracker blockers leave alone.
                var probe = Path.Combine(Path.GetTempPath(), $"en-details-{Guid.NewGuid():N}.txt");
                File.WriteAllText(probe, "hello");
                try
                {
                    var (dStatus, _, dBody) = await Get("/api/details?path=" + Uri.EscapeDataString(probe), "me@example.com");
                    Check("/api/details answers like /api/stat", dStatus == 200 && dBody.Contains(Path.GetFileName(probe)), $"{dStatus} {dBody[..Math.Min(80, dBody.Length)]}");
                }
                finally { try { File.Delete(probe); } catch { } }

                var (_, _, who) = await Get("/api/whoami", "me@example.com");
                Check("whoami tells the owner no code is needed", who.Contains("\"needsCode\":false"), who);
                (_, _, who) = await Get("/api/whoami");
                Check("and anybody else that one is", who.Contains("\"needsCode\":true") && who.Contains("\"codeOk\":false"), who);
                (_, _, who) = await Get("/api/whoami", code: true);
                Check("and checks a code without opening anything", who.Contains("\"codeOk\":true"), who);
            }
            finally
            {
                server.Dispose();
            }
        }

        private static async Task PingTests()
        {
            const string status = """
                { "Self": { "TailscaleIPs": ["100.67.248.25", "fd7a::1"] },
                  "Peer": {
                    "a": { "TailscaleIPs": ["100.100.1.1"], "CurAddr": "203.0.113.5:41641", "Relay": "ord" },
                    "b": { "TailscaleIPs": ["100.100.2.2"], "CurAddr": "", "Relay": "lhr" },
                    "c": { "TailscaleIPs": ["100.100.3.3"], "CurAddr": "", "Relay": "" } } }
                """;
            var paths = ConnectServer.ParseTailscalePaths(status);
            Check("a peer with a current address is direct", paths["100.100.1.1"] == "direct");
            Check("one without goes through its relay, named", paths["100.100.2.2"] == "relay lhr");
            Check("one with neither is unknown", paths["100.100.3.3"] == "unknown");
            Check("this machine and loopback are direct", paths["100.67.248.25"] == "direct" && paths["127.0.0.1"] == "direct");

            int asked = 0;
            int port = FreePort();
            var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port,
                files: new FakeFiles(), tailscaleStatus: () => { Interlocked.Increment(ref asked); Thread.Sleep(300); return status; });
            try
            {
                server.Start();
                if (!await WaitForListener(port)) { Check("the ping server listens", false); return; }
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var (code, body) = await CallAt(http, HttpMethod.Get, "/api/ping");
                Check("ping answers at once, without waiting for tailscale", code == 200 && clock.ElapsedMilliseconds < 250 && body.TryGetProperty("time", out _),
                    $"{code} {clock.ElapsedMilliseconds}ms");
                await server.PathsSettled();
                (_, body) = await CallAt(http, HttpMethod.Get, "/api/ping");
                Check("and says how the caller is reached", Prop(body, "path") == "direct", body.ToString());
                for (int i = 0; i < 5; i++) await CallAt(http, HttpMethod.Get, "/api/ping");
                Equal("the status is cached, not asked per ping", "1", asked.ToString());
                (_, body) = await CallAt(http, HttpMethod.Get, "/api/info");
                Equal("info says apiVersion 4", "4", Prop(body, "apiVersion"));
            }
            finally { server.Dispose(); }
        }

        // MARK: Fixtures for the details

        private static byte[] Be32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        private static byte[] Le32(int v) => BitConverter.GetBytes(v);

        /// <summary>A PNG that is only its signature and header: enough for a width and height, which is all is read.</summary>
        private static byte[] PngHeader(int w, int h)
        {
            var b = new List<byte> { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
            b.AddRange(Be32((uint)w)); b.AddRange(Be32((uint)h)); b.AddRange(new byte[] { 8, 6, 0, 0, 0, 0, 0, 0, 0 });
            return b.ToArray();
        }

        private static byte[] VorbisCommentBlock(string vendor, params string[] fields)
        {
            var c = new List<byte>();
            var v = Encoding.UTF8.GetBytes(vendor);
            c.AddRange(Le32(v.Length)); c.AddRange(v); c.AddRange(Le32(fields.Length));
            foreach (var f in fields) { var x = Encoding.UTF8.GetBytes(f); c.AddRange(Le32(x.Length)); c.AddRange(x); }
            return c.ToArray();
        }

        /// <summary>STREAMINFO with an MD5, Vorbis comments and a 600×600 PNG cover; no audio frames.</summary>
        private static void WriteTaggedFlac(string path)
        {
            var f = new List<byte>();
            f.AddRange("fLaC"u8.ToArray());
            void Block(int type, bool last, byte[] data)
            {
                f.Add((byte)((last ? 0x80 : 0) | type));
                f.Add((byte)(data.Length >> 16)); f.Add((byte)(data.Length >> 8)); f.Add((byte)data.Length);
                f.AddRange(data);
            }
            var si = new List<byte> { 0x10, 0x00, 0x10, 0x00, 0, 0, 0, 0, 0, 0 };
            ulong packed = (48000UL << 44) | (1UL << 41) | (23UL << 36) | (48000UL * 185);   // stereo, 24-bit, 185 s
            for (int s = 56; s >= 0; s -= 8) si.Add((byte)(packed >> s));
            si.AddRange(Enumerable.Range(1, 16).Select(i => (byte)i));
            Block(0, false, si.ToArray());
            Block(4, false, VorbisCommentBlock("reference libFLAC 1.4.3 20230623",
                "TITLE=Flac Song", "ARTIST=The Band", "ALBUM=The Album", "TRACKNUMBER=3", "TRACKTOTAL=12", "DATE=2021",
                "REPLAYGAIN_TRACK_GAIN=-6.50 dB", "LYRICS=" + new string('l', 3000), "MOOD=calm"));
            var pic = new List<byte>();
            var mime = Encoding.ASCII.GetBytes("image/png");
            pic.AddRange(Be32(3)); pic.AddRange(Be32((uint)mime.Length)); pic.AddRange(mime); pic.AddRange(Be32(0));
            pic.AddRange(Be32(600)); pic.AddRange(Be32(600)); pic.AddRange(Be32(32)); pic.AddRange(Be32(0));
            var png = PngHeader(600, 600);
            pic.AddRange(Be32((uint)png.Length)); pic.AddRange(png);
            Block(6, true, pic.ToArray());
            System.IO.File.WriteAllBytes(path, f.ToArray());
        }

        /// <summary>An ID3v2.3 tag in front of the suite's LAME-encoded VBR fixture.</summary>
        private static void WriteTaggedMp3(string path, byte[] mp3)
        {
            var frames = new List<byte>();
            void Frame(string id, byte[] body) { frames.AddRange(Encoding.ASCII.GetBytes(id)); frames.AddRange(Be32((uint)body.Length)); frames.Add(0); frames.Add(0); frames.AddRange(body); }
            byte[] Text(string s) => new byte[] { 0 }.Concat(Encoding.Latin1.GetBytes(s)).ToArray();
            Frame("TIT2", Text("Mp3 Song"));
            Frame("TPE1", Text("Mp3 Artist"));
            Frame("TRCK", Text("5/9"));
            Frame("TBPM", Text("128"));
            Frame("TXXX", new byte[] { 0 }.Concat(Encoding.Latin1.GetBytes("REPLAYGAIN_TRACK_GAIN\0-3.2 dB")).ToArray());
            Frame("APIC", new byte[] { 0 }.Concat(Encoding.Latin1.GetBytes("image/png\0")).Concat(new byte[] { 3, 0 }).Concat(PngHeader(300, 200)).ToArray());
            int size = frames.Count;
            var head = new List<byte> { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0, (byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F), (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F) };
            System.IO.File.WriteAllBytes(path, head.Concat(frames).Concat(mp3).ToArray());
        }

        private static byte[] Box(string type, params byte[][] parts)
        {
            int length = 8 + parts.Sum(p => p.Length);
            return Be32((uint)length).Concat(Encoding.Latin1.GetBytes(type)).Concat(parts.SelectMany(p => p)).ToArray();
        }

        /// <summary>
        /// The M4A Windows writes, made an audiobook: the brand becomes M4B, and the moov gains iTunes tags, a cover
        /// and Nero chapters. The moov is last in what the sink writer makes, so nothing after it moves.
        /// </summary>
        private static bool MakeAudiobook(string m4a, string m4b)
        {
            var d = System.IO.File.ReadAllBytes(m4a);
            int at = 0, moov = -1;
            while (at + 8 <= d.Length)
            {
                int size = (int)((uint)d[at] << 24 | (uint)d[at + 1] << 16 | (uint)d[at + 2] << 8 | d[at + 3]);
                var type = Encoding.ASCII.GetString(d, at + 4, 4);
                if (type == "ftyp") { d[at + 8] = (byte)'M'; d[at + 9] = (byte)'4'; d[at + 10] = (byte)'B'; d[at + 11] = (byte)' '; }
                if (type == "moov") { moov = at; break; }
                if (size < 8) return false;
                at += size;
            }
            if (moov < 0) return false;
            int moovSize = (int)((uint)d[moov] << 24 | (uint)d[moov + 1] << 16 | (uint)d[moov + 2] << 8 | d[moov + 3]);
            if (moov + moovSize != d.Length) return false;

            byte[] Data(int kind, byte[] payload) => Box("data", Be32((uint)kind), new byte[4], payload);
            byte[] Item(string name, int kind, byte[] payload) => Box(name, Data(kind, payload));
            var ilst = Box("ilst",
                Item("©nam", 1, Encoding.UTF8.GetBytes("Book Title")),
                Item("©ART", 1, Encoding.UTF8.GetBytes("Narrator")),
                Item("trkn", 0, new byte[] { 0, 0, 0, 2, 0, 7, 0, 0 }),
                Item("covr", 13, new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0, 17, 8, 0, 100, 0, 200, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 }));
            var hdlr = Box("hdlr", new byte[8], Encoding.ASCII.GetBytes("mdirappl"), new byte[9]);
            var meta = Box("meta", new byte[4], hdlr, ilst);
            var chpl = new List<byte> { 1, 0, 0, 0, 0, 0, 0, 0, 3 };
            foreach (var (time, title) in new[] { (0.0, "Opening"), (1.0, "Middle"), (2.5, "Ending") })
            {
                ulong ticks = (ulong)(time * 10_000_000);
                for (int s = 56; s >= 0; s -= 8) chpl.Add((byte)(ticks >> s));
                var t = Encoding.UTF8.GetBytes(title);
                chpl.Add((byte)t.Length); chpl.AddRange(t);
            }
            var udta = Box("udta", Box("chpl", chpl.ToArray()), meta);
            var grown = Be32((uint)(moovSize + udta.Length));
            Array.Copy(grown, 0, d, moov, 4);
            System.IO.File.WriteAllBytes(m4b, d.Concat(udta).ToArray());
            return true;
        }

        private static byte[] Ebml(uint id, params byte[][] parts)
        {
            var idBytes = id > 0xFFFFFF ? Be32(id) : id > 0xFFFF ? Be32(id)[1..] : id > 0xFF ? Be32(id)[2..] : new[] { (byte)id };
            long length = parts.Sum(p => (long)p.Length);
            var size = new byte[8];
            size[0] = 0x01;
            for (int i = 0; i < 7; i++) size[7 - i] = (byte)(length >> (8 * i));
            return idBytes.Concat(size).Concat(parts.SelectMany(p => p)).ToArray();
        }

        private static byte[] U(uint id, ulong v) => Ebml(id, Be32((uint)(v >> 32)).Concat(Be32((uint)v)).ToArray());
        private static byte[] S(uint id, string v) => Ebml(id, Encoding.UTF8.GetBytes(v));
        private static byte[] F(uint id, double v) => Ebml(id, Enumerable.Reverse(BitConverter.GetBytes(v)).ToArray());

        /// <summary>A Matroska header, info, two tracks, chapters and tags; no clusters, which the details never read.</summary>
        private static void WriteMkv(string path)
        {
            var header = Ebml(0x1A45DFA3, S(0x4282, "matroska"), U(0x4287, 4));
            var info = Ebml(0x1549A966, U(0x2AD7B1, 1_000_000), F(0x4489, 5000), S(0x4D80, "libebml test"), S(0x5741, "ExplorerNative suite"), S(0x7BA9, "Mkv Title"));
            var tracks = Ebml(0x1654AE6B,
                Ebml(0xAE, U(0xD7, 1), U(0x83, 2), S(0x86, "A_OPUS"), S(0x22B59C, "eng"), Ebml(0xE1, F(0xB5, 48000), U(0x9F, 6))),
                Ebml(0xAE, U(0xD7, 2), U(0x83, 1), S(0x86, "V_MPEG4/ISO/AVC"), U(0x23E383, 41_708_333), Ebml(0xE0, U(0xB0, 1920), U(0xBA, 1080))));
            var chapters = Ebml(0x1043A770, Ebml(0x45B9,
                Ebml(0xB6, U(0x73C4, 1), U(0x91, 0), Ebml(0x80, S(0x85, "Intro"))),
                Ebml(0xB6, U(0x73C4, 2), U(0x91, 2_000_000_000), Ebml(0x80, S(0x85, "Part two")))));
            var tags = Ebml(0x1254C367, Ebml(0x7373, Ebml(0x63C0), Ebml(0x67C8, S(0x45A3, "ARTIST"), S(0x4487, "Mkv Artist")), Ebml(0x67C8, S(0x45A3, "GENRE"), S(0x4487, "Ambient"))));
            var segment = Ebml(0x18538067, info, tracks, chapters, tags);
            System.IO.File.WriteAllBytes(path, header.Concat(segment).ToArray());
        }

        private static string? Tag(Dictionary<string, object?> media, string name) =>
            media.TryGetValue("tags", out var t) && t is List<NameValue> list ? list.FirstOrDefault(x => x.Name == name)?.Value : null;

        private static Dictionary<string, object?> Section(Dictionary<string, object?> d, string name) =>
            d.TryGetValue(name, out var s) && s is Dictionary<string, object?> section ? section : new Dictionary<string, object?>();

        private static object? At(Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) ? v : null;

        private static Dictionary<string, object?> Stream0(Dictionary<string, object?> media, string kind) =>
            media.TryGetValue(kind, out var s) && s is List<Dictionary<string, object?>> list && list.Count > 0 ? list[0] : new Dictionary<string, object?>();

        private static async Task DetailsTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "en-connect-details-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var files = new ConnectFiles(null, new Settings());
            async Task<Dictionary<string, object?>> Details(string path, bool hash = false)
            {
                var stat = await files.StatAsync(path, CancellationToken.None);
                using var budget = new CancellationTokenSource(ConnectServer.StatBudget);
                return await files.DetailsAsync(path, stat, hash, budget.Token);
            }
            try
            {
                // Text: UTF-16 with a BOM and CRLF, then a UTF-8 file mixing all three line endings.
                var utf16 = Path.Combine(dir, "wide.txt");
                System.IO.File.WriteAllText(utf16, "Hello world\r\nSecond line\r\n\r\nThird ünïcode line\r\n", new UnicodeEncoding(false, true));
                var t = Section(await Details(utf16), "text");
                Check("UTF-16 LE with a BOM is recognised", (string?)At(t, "encoding") == "UTF-16 LE" && At(t, "bom") is true, string.Join(", ", t));
                Check("and counted as characters, not bytes",
                    At(t, "lineEndings") as string == "CRLF" && (long)At(t, "lines")! == 4 && (long)At(t, "words")! == 7 && (long)At(t, "characters")! == 40 &&
                    (long)At(t, "blankLines")! == 1 && (long)At(t, "paragraphs")! == 2 && (long)At(t, "longestLine")! == 18 && (long)At(t, "nonAscii")! == 2,
                    string.Join(", ", t));

                var mixed = Path.Combine(dir, "mixed.cs");
                System.IO.File.WriteAllBytes(mixed, Encoding.UTF8.GetBytes("one two\nthré\r\n\tfour\r"));
                t = Section(await Details(mixed), "text");
                Check("LF, CRLF and CR in one file are mixed, UTF-8 without a BOM, in C#",
                    At(t, "lineEndings") as string == "mixed" && At(t, "encoding") as string == "UTF-8" && At(t, "bom") is false &&
                    (long)At(t, "lines")! == 3 && (long)At(t, "words")! == 4 && (long)At(t, "tabs")! == 1 && (long)At(t, "nonAscii")! == 1 && At(t, "language") as string == "C#",
                    string.Join(", ", t));

                var plain = Path.Combine(dir, "plain.log");
                System.IO.File.WriteAllText(plain, "just ascii\n");
                var d = await Details(plain, hash: true);
                Check("pure ASCII says so", Section(d, "text").GetValueOrDefault("encoding") as string == "ASCII");
                var file = Section(d, "file");
                Check("the file section: extension, kind, mime, size, attributes, owner",
                    At(file, "extension") as string == ".log" && At(file, "kind") is string && (long)At(file, "size")! == 11 &&
                    At(file, "attributes") is List<string> && At(file, "owner") is string && At(file, "onDrive") is false,
                    string.Join(", ", file));
                Check("and SHA-256 when asked",
                    At(file, "sha256") as string == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(plain))).ToLowerInvariant());
                Check("but not otherwise", !Section(await Details(plain), "file").ContainsKey("sha256"));

                var spent = new CancellationTokenSource();
                spent.Cancel();
                var cut = await files.DetailsAsync(plain, await files.StatAsync(plain, CancellationToken.None), false, spent.Token);
                Check("a budget already spent answers partial rather than failing", cut.TryGetValue("partial", out var pv) && pv is true);

                var binary = Path.Combine(dir, "blob.dat");
                System.IO.File.WriteAllBytes(binary, new byte[] { 0, 1, 2, 3, 0, 5 });
                Check("a binary file is not text", !(await Details(binary)).ContainsKey("text"));

                var folder = Section(await Details(dir), "folder");
                Check("a folder counts its direct children", At(folder, "files") is int and >= 4 && At(folder, "folders") is int f0 && f0 == 0, string.Join(", ", folder));

                // FLAC with tags and a cover.
                var flac = Path.Combine(dir, "tagged.flac");
                WriteTaggedFlac(flac);
                var m = Section(await Details(flac), "media");
                var a = Stream0(m, "audio");
                Check("FLAC: container, 24-bit stereo at 48 kHz, lossless, 185 s",
                    At(m, "container") as string == "FLAC" && At(a, "bitsPerSample") is 24 && At(a, "sampleRate") is 48000 && At(a, "channels") is 2 &&
                    At(a, "lossless") is true && At(m, "durationSeconds") is double dur && Math.Abs(dur - 185) < 0.001, string.Join(", ", m) + " | " + string.Join(", ", a));
                Check("FLAC: tags in order, track as \"3 of 12\", lyrics cut to 2000, cover described",
                    Tag(m, "Title") == "Flac Song" && Tag(m, "Track") == "3 of 12" && Tag(m, "Lyrics")?.Length == 2000 &&
                    Tag(m, "ReplayGain track gain") == "-6.50 dB" && Tag(m, "Cover art") == "yes, 600×600 PNG" && Tag(m, "Mood") == "calm" &&
                    ((List<NameValue>)m["tags"]!)[0].Name == "Title", string.Join("; ", ((List<NameValue>?)At(m, "tags"))?.Select(x => x.Name + "=" + x.Value[..Math.Min(20, x.Value.Length)]) ?? Array.Empty<string>()));
                Check("FLAC: the MD5 and the vendor are in extra",
                    At(m, "extra") is List<NameValue> fx && fx.Any(x => x.Name == "MD5" && x.Value == "0102030405060708090a0b0c0d0e0f10") && fx.Any(x => x.Name == "Vendor" && x.Value.StartsWith("reference libFLAC")));

                // MP3, VBR with a LAME header, behind an ID3v2 tag.
                var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "clicks-vbr.mp3");
                var mp3 = Path.Combine(dir, "tagged.mp3");
                WriteTaggedMp3(mp3, System.IO.File.ReadAllBytes(fixture));
                m = Section(await Details(mp3), "media");
                a = Stream0(m, "audio");
                Check("MP3: VBR from its Xing header, the LAME encoder, delay and padding",
                    At(a, "codec") as string == "MP3" && At(a, "vbr") is true && At(m, "extra") is List<NameValue> mx &&
                    mx.Any(x => x.Name == "Encoder" && x.Value.StartsWith("LAME")) && mx.Any(x => x.Name == "Encoder delay") && mx.Any(x => x.Name == "Padding"),
                    string.Join(", ", a) + " | " + string.Join("; ", ((List<NameValue>?)At(m, "extra"))?.Select(x => x.Name + "=" + x.Value) ?? Array.Empty<string>()));
                Check("MP3: the duration is about twelve seconds",
                    At(m, "durationSeconds") is double md && Math.Abs(md - 12) < 0.3, At(m, "durationSeconds")?.ToString());
                Check("MP3: ID3v2 tags, with the TXXX ReplayGain and the cover",
                    Tag(m, "Title") == "Mp3 Song" && Tag(m, "Track") == "5 of 9" && Tag(m, "BPM") == "128" && Tag(m, "ReplayGain track gain") == "-3.2 dB" &&
                    Tag(m, "Cover art") == "yes, 300×200 PNG", string.Join("; ", ((List<NameValue>?)At(m, "tags"))?.Select(x => x.Name + "=" + x.Value) ?? Array.Empty<string>()));

                // An audiobook: AAC in M4B, with Nero chapters and iTunes tags.
                var m4a = Path.Combine(dir, "tone.m4a");
                var m4b = Path.Combine(dir, "book.m4b");
                if (Tests.Mpeg4.WriteTone(m4a, 48000, 2, 3, out var why) && MakeAudiobook(m4a, m4b))
                {
                    m = Section(await Details(m4b), "media");
                    a = Stream0(m, "audio");
                    Check("M4B: an audiobook, AAC-LC, 48 kHz stereo",
                        (At(m, "container") as string ?? "").Contains("M4B") && At(a, "codec") as string == "AAC" && At(a, "codecProfile") as string == "AAC-LC" &&
                        At(a, "sampleRate") is 48000 && At(a, "channels") is 2, string.Join(", ", m) + " | " + string.Join(", ", a));
                    Check("M4B: its chapters, in order",
                        At(m, "chapters") is List<Dictionary<string, object?>> ch && ch.Count == 3 && ch[1]["title"] as string == "Middle" && (double)ch[2]["startSeconds"]! == 2.5,
                        (At(m, "chapters") as List<Dictionary<string, object?>>)?.Count.ToString());
                    Check("M4B: its tags and cover",
                        Tag(m, "Title") == "Book Title" && Tag(m, "Artist") == "Narrator" && Tag(m, "Track") == "2 of 7" && Tag(m, "Cover art") == "yes, 200×100 JPEG",
                        string.Join("; ", ((List<NameValue>?)At(m, "tags"))?.Select(x => x.Name + "=" + x.Value) ?? Array.Empty<string>()));
                    Check("M4B: about three seconds", At(m, "durationSeconds") is double bd && Math.Abs(bd - 3) < 0.2);
                }
                else Console.WriteLine($"  (no AAC encoder here: {why})");

                // Matroska.
                var mkv = Path.Combine(dir, "film.mkv");
                WriteMkv(mkv);
                m = Section(await Details(mkv), "media");
                a = Stream0(m, "audio");
                var v = Stream0(m, "video");
                Check("MKV: Matroska, 5 s, Opus 5.1 in English, H.264 1920×1080 at 23.976",
                    At(m, "container") as string == "Matroska" && At(m, "durationSeconds") is double kd && Math.Abs(kd - 5) < 0.001 &&
                    At(a, "codec") as string == "Opus" && At(a, "channelLayout") as string == "5.1" && At(a, "language") as string == "eng" &&
                    At(v, "codec") as string == "H.264" && At(v, "width") is 1920 && At(v, "height") is 1080 && At(v, "frameRate") is double fr && Math.Abs(fr - 23.976) < 0.01,
                    string.Join(", ", m) + " | " + string.Join(", ", a) + " | " + string.Join(", ", v));
                Check("MKV: chapters, tags and the writing application",
                    At(m, "chapters") is List<Dictionary<string, object?>> kc && kc.Count == 2 && kc[1]["title"] as string == "Part two" && (double)kc[1]["startSeconds"]! == 2 &&
                    Tag(m, "Title") == "Mkv Title" && Tag(m, "Artist") == "Mkv Artist" && Tag(m, "Genre") == "Ambient" &&
                    At(m, "extra") is List<NameValue> kx && kx.Any(x => x.Name == "Writing application"));

                // Opus with tags and a chapter.
                var opus = Path.Combine(dir, "tagged.opus");
                using (var stream = System.IO.File.Create(opus))
                {
                    var tagsIn = new Concentus.Oggfile.OpusTags();
                    tagsIn.Fields["TITLE"] = "Opus Song";
                    tagsIn.Fields["ARTIST"] = "Opus Artist";
                    tagsIn.Fields["CHAPTER001"] = "00:00:01.500";
                    tagsIn.Fields["CHAPTER001NAME"] = "Later";
                    var encoder = Concentus.OpusCodecFactory.CreateEncoder(48000, 2, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO, null);
                    var writer = new Concentus.Oggfile.OpusOggWriteStream(encoder, stream, tagsIn, 48000, 2, false);
                    writer.WriteSamples(new short[48000 * 2 * 2], 0, 48000 * 2 * 2);
                    writer.Finish();
                }
                m = Section(await Details(opus), "media");
                a = Stream0(m, "audio");
                Check("Opus: Ogg Opus, 48 kHz stereo, two seconds, the pre-skip",
                    At(m, "container") as string == "Ogg Opus" && At(a, "codec") as string == "Opus" && At(a, "channels") is 2 &&
                    At(m, "durationSeconds") is double od && Math.Abs(od - 2) < 0.05 && At(m, "extra") is List<NameValue> ox && ox.Any(x => x.Name == "Pre-skip"),
                    string.Join(", ", m) + " | " + string.Join(", ", a));
                Check("Opus: its comments as tags, and CHAPTER comments as chapters",
                    Tag(m, "Title") == "Opus Song" && Tag(m, "Artist") == "Opus Artist" &&
                    At(m, "chapters") is List<Dictionary<string, object?>> oc && oc.Count == 1 && oc[0]["title"] as string == "Later" && (double)oc[0]["startSeconds"]! == 1.5);

                // A zip, from its central directory alone.
                var zip = Path.Combine(dir, "bundle.zip");
                using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                {
                    var e1 = archive.CreateEntry("a.txt"); using (var w = new StreamWriter(e1.Open())) w.Write(new string('a', 1000));
                    archive.CreateEntry("folder/");
                    var e2 = archive.CreateEntry("folder/b.txt"); using (var w = new StreamWriter(e2.Open())) w.Write("bb");
                }
                var z = Section(await Details(zip), "archive");
                Check("a zip: entries and uncompressed size", At(z, "format") as string == "zip" && (long)At(z, "entries")! == 3 && (long)At(z, "uncompressedSize")! == 1002,
                    string.Join(", ", z));

                // An image, from its header.
                var png = Path.Combine(dir, "shot.png");
                using (var bmp = new System.Drawing.Bitmap(40, 30)) bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                var im = Section(await Details(png), "image");
                Check("an image: width, height and format", At(im, "width") is 40 && At(im, "height") is 30 && At(im, "format") as string == "PNG", string.Join(", ", im));

                // Over HTTP, through the server: the old fields, the sections, and the v2 tags object from the media.
                int port = FreePort();
                var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port, files: files);
                try
                {
                    server.Start();
                    if (!await WaitForListener(port)) { Check("the details server listens", false); return; }
                    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };
                    var (code, body) = await CallAt(http, HttpMethod.Get, "/api/stat?path=" + Uri.EscapeDataString(mkv));
                    Check("stat over HTTP keeps the v2 fields and adds file and media, tags as name/value",
                        code == 200 && Prop(body, "name") == "film.mkv" && body.TryGetProperty("file", out _) &&
                        body.GetProperty("media").GetProperty("tags")[0].GetProperty("name").GetString() == "Title" &&
                        body.GetProperty("media").GetProperty("audio")[0].GetProperty("channelLayout").GetString() == "5.1" &&
                        !body.TryGetProperty("partial", out _), body.ToString()[..Math.Min(400, body.ToString().Length)]);
                    Check("and the v2 tags object for a client that reads it", body.TryGetProperty("tags", out var old) && Prop(old, "title") == "Mkv Title");
                }
                finally { server.Dispose(); }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>A clipboard that is not the real one: the suite must never touch what Conner has copied.</summary>
        private sealed class FakeClipboard : IConnectClipboard
        {
            public readonly ConnectClipboard Real = new();
            public List<string[]> FilesSet = new();
            public byte[]? LastImage;

            public ClipState Current => Real.Current;
            public Task<ClipState> WaitAsync(long since, TimeSpan timeout, CancellationToken token) => Real.WaitAsync(since, timeout, token);
            public byte[]? Png() => Real.Png();
            public IReadOnlyList<ClipHistoryItem> History() => Real.History();
            public void ClearHistory() => Real.ClearHistory();

            public Task<long> SetTextAsync(string text)
            {
                Real.Observe((new ClipState(0, "text", Text: text), null));
                return Task.FromResult(Real.Current.Seq);
            }

            public Task<long> SetImageAsync(byte[] image)
            {
                LastImage = image;
                Real.Observe((new ClipState(0, "image", ImageBytes: image.Length), image));
                return Task.FromResult(Real.Current.Seq);
            }

            public Task<long> SetFilesAsync(IReadOnlyList<string> paths)
            {
                FilesSet.Add(paths.ToArray());
                Real.Observe((new ClipState(0, "files", Files: paths.ToArray()), null));
                return Task.FromResult(Real.Current.Seq);
            }
        }

        private static async Task ClipboardTests()
        {
            // The real class's bookkeeping, fed by hand: seq, history order, its cap, the text cap, empties.
            var real = new ConnectClipboard();
            for (int i = 0; i < 60; i++) real.Observe((new ClipState(0, "text", Text: "item " + i), null));
            real.Observe((new ClipState(0, "empty"), null));
            real.Observe((new ClipState(0, "text", Text: new string('x', ConnectClipboard.HistoryTextChars + 10)), null));
            var history = real.History();
            Check("seq goes up on every change, empty included", real.Current.Seq == 62, real.Current.Seq.ToString());
            Check("history is newest first and holds the last 50", history.Count == 50 && history[0].Seq == 62 && history[1].Text == "item 59");
            Check("an empty clipboard is not a history item", history.All(h => h.Kind != "empty"));
            Check("history text is capped at 1 MB", history[0].Text!.Length == ConnectClipboard.HistoryTextChars);
            Check("but the clipboard itself is not", real.Current.Text!.Length == ConnectClipboard.HistoryTextChars + 10);
            real.ClearHistory();
            Check("clear empties the history", real.History().Count == 0);

            // One copy raises several updates; copying something again moves it to the top.
            real.Observe((new ClipState(0, "text", Text: "once"), null));
            real.Observe((new ClipState(0, "text", Text: "once"), null));
            real.Observe((new ClipState(0, "text", Text: "once"), null));
            real.Observe((new ClipState(0, "files", Files: new[] { @"C:\a.txt" }), null));
            real.Observe((new ClipState(0, "files", Files: new[] { @"c:\A.txt" }), null));
            real.Observe((new ClipState(0, "text", Text: "once"), null));
            var deduped = real.History();
            Check("a repeated copy is one history item", deduped.Count == 2);
            Check("and the repeat moves to the top", deduped[0].Text == "once" && deduped[1].Kind == "files");
            real.ClearHistory();

            var fake = new FakeClipboard();
            fake.Real.Observe((new ClipState(0, "text", Text: "start"), null));
            string sends = Path.Combine(Path.GetTempPath(), "en-connect-clip-" + Guid.NewGuid().ToString("N"));
            int port = FreePort();
            var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port,
                files: new FakeFiles(), clipboard: fake, clipboardSends: sends);
            try
            {
                server.Start();
                if (!await WaitForListener(port)) { Check("the clipboard server listens", false); return; }
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(40) };

                var (status, body) = await CallAt(http, HttpMethod.Get, "/api/clipboard");
                Check("GET clipboard gives seq, kind and text, and nothing absent",
                    status == 200 && Prop(body, "kind") == "text" && Prop(body, "text") == "start" && Prop(body, "seq") == "1" &&
                    !body.TryGetProperty("files", out _) && !body.TryGetProperty("imageBytes", out _), body.ToString());

                (status, body) = await CallAt(http, HttpMethod.Post, "/api/clipboard", new { text = "from the phone" });
                Check("POST text sets it and answers the seq", status == 200 && Prop(body, "seq") == "2" && fake.Current.Text == "from the phone", $"{status} {body}");

                var clock = System.Diagnostics.Stopwatch.StartNew();
                var waiting = CallAt(http, HttpMethod.Get, "/api/clipboard/wait?since=2");
                await Task.Delay(300);
                Check("a long poll waits while nothing changes", !waiting.IsCompleted);
                fake.Real.Observe((new ClipState(0, "text", Text: "copied on the PC"), null));
                (status, body) = await waiting;
                Check("and answers as soon as it does", status == 200 && Prop(body, "seq") == "3" && Prop(body, "text") == "copied on the PC" && clock.ElapsedMilliseconds < 5000,
                    $"{body} {clock.ElapsedMilliseconds}ms");
                (status, body) = await CallAt(http, HttpMethod.Get, "/api/clipboard/wait?since=1");
                Check("a long poll already behind answers at once", Prop(body, "seq") == "3");

                (status, _) = await CallAt(http, HttpMethod.Get, "/api/clipboard/image");
                Equal("no image is 404", "404", status.ToString());
                var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
                (status, body) = await CallAt(http, HttpMethod.Post, "/api/clipboard/image", content: new ByteArrayContent(png));
                Check("POST image hands the bytes over", status == 200 && fake.LastImage?.SequenceEqual(png) == true, $"{status} {body}");
                var r = await Send(http, HttpMethod.Get, "/api/clipboard/image");
                var got = await r.Content.ReadAsByteArrayAsync();
                Check("GET image is the PNG", (int)r.StatusCode == 200 && r.Content.Headers.ContentType?.MediaType == "image/png" && got.SequenceEqual(png));
                (_, body) = await CallAt(http, HttpMethod.Get, "/api/clipboard");
                Check("and the state says image, with its size", Prop(body, "kind") == "image" && Prop(body, "imageBytes") == png.Length.ToString(), body.ToString());

                var existing = Path.Combine(Path.GetTempPath(), "en-connect-clipfile-" + Guid.NewGuid().ToString("N") + ".txt");
                System.IO.File.WriteAllText(existing, "x");
                (status, body) = await CallAt(http, HttpMethod.Post, "/api/clipboard/files", new { paths = new[] { existing } });
                Check("POST files puts the paths on", status == 200 && fake.FilesSet.Last().SequenceEqual(new[] { existing }), $"{status} {body}");
                (status, _) = await CallAt(http, HttpMethod.Post, "/api/clipboard/files", new { paths = new[] { existing + ".gone" } });
                Equal("a path that is not there is 404", "404", status.ToString());
                System.IO.File.Delete(existing);

                // Phone files: one straight on, then a batch.
                (status, body) = await CallAt(http, HttpMethod.Post, "/api/clipboard/send?name=" + Uri.EscapeDataString("photo 1.jpg"),
                    content: new ByteArrayContent(Encoding.UTF8.GetBytes("jpeg bytes")));
                var sent = Prop(body, "path");
                Check("a sent file is stored under the sends folder and put on as a file",
                    status == 200 && sent.StartsWith(sends) && System.IO.File.ReadAllText(sent) == "jpeg bytes" && fake.FilesSet.Last().SequenceEqual(new[] { sent }),
                    $"{status} {body}");
                int before = fake.FilesSet.Count;
                await CallAt(http, HttpMethod.Post, "/api/clipboard/send?name=a.txt&batch=b-1", content: new ByteArrayContent(Encoding.UTF8.GetBytes("a")));
                await CallAt(http, HttpMethod.Post, "/api/clipboard/send?name=b.txt&batch=b-1", content: new ByteArrayContent(Encoding.UTF8.GetBytes("b")));
                Check("a batch is not put on until it is committed", fake.FilesSet.Count == before);
                (status, body) = await CallAt(http, HttpMethod.Post, "/api/clipboard/send/commit", new { batch = "b-1" });
                Check("commit puts the whole batch on at once",
                    status == 200 && fake.FilesSet.Last().Select(Path.GetFileName).SequenceEqual(new[] { "a.txt", "b.txt" }), $"{status} {body}");
                (status, _) = await CallAt(http, HttpMethod.Post, "/api/clipboard/send?name=x.txt&batch=..%5Cup", content: new ByteArrayContent(new byte[1]));
                Equal("a batch id that could walk a path is 400", "400", status.ToString());
                (status, _) = await CallAt(http, HttpMethod.Post, "/api/clipboard/send?name=..%5Cx.txt", content: new ByteArrayContent(new byte[1]));
                Equal("a name that could walk a path is 400", "400", status.ToString());
                (status, _) = await CallAt(http, HttpMethod.Post, "/api/clipboard/send/commit", new { batch = "never" });
                Equal("committing a batch nothing was sent in is 404", "404", status.ToString());

                (status, body) = await CallAt(http, HttpMethod.Get, "/api/clipboard/history");
                var items = body.EnumerateArray().ToList();
                Check("history over HTTP is newest first, with kind and time",
                    status == 200 && items.Count >= 6 && Prop(items[0], "kind") == "files" && items[0].TryGetProperty("time", out _) &&
                    long.Parse(Prop(items[0], "seq")) > long.Parse(Prop(items[1], "seq")), body.ToString());
                (status, _) = await CallAt(http, HttpMethod.Post, "/api/clipboard/history/clear", new { });
                (_, body) = await CallAt(http, HttpMethod.Get, "/api/clipboard/history");
                Check("clear over HTTP empties it", status == 200 && body.GetArrayLength() == 0);

                foreach (var dir in Directory.GetDirectories(sends)) Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-8));
                var fresh = Directory.CreateDirectory(Path.Combine(sends, "fresh"));
                Check("sent files are swept after seven days, and newer ones kept",
                    server.SweepClipboardSends(ConnectServer.ClipboardSendsKeptFor) == 2 && Directory.GetDirectories(sends).Single() == fresh.FullName);
            }
            finally
            {
                server.Dispose();
                try { Directory.Delete(sends, true); } catch { }
            }
        }

        private static async Task<(int Status, JsonElement Body)> CallAt(HttpClient http, HttpMethod method, string url,
            object? json = null, HttpContent? content = null)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add("X-Connect-Code", Code);
            if (json != null) request.Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");
            if (content != null) request.Content = content;
            var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
            return ((int)response.StatusCode, doc.RootElement.Clone());
        }

        private static string Prop(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : "(missing)";

        /// <summary>Start, chunks with a gap and a cut-off one, status, finish to a local and a Drive folder, cancel, sweep.</summary>
        private static async Task ResumableUploadTests()
        {
            var fake = new FakeFiles();
            string store = Path.Combine(Path.GetTempPath(), "en-connect-uploads-" + Guid.NewGuid().ToString("N"));
            int port = FreePort();
            var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port,
                files: fake, uploads: store);
            try
            {
                server.Start();
                if (!await WaitForListener(port)) { Check("the upload server listens", false); return; }
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(15) };

                var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 1000).Select(i => (char)('a' + i % 26))));
                var (status, body) = await CallAt(http, HttpMethod.Post, "/api/upload/start", new { folder = @"C:\b", name = "big.bin", size = payload.Length, conflict = "rename" });
                string id = Prop(body, "id");
                Check("start answers an id", status == 200 && id.Length == 16, $"{status} {body}");
                Check("the partial file is under its own folder in the store", System.IO.File.Exists(Path.Combine(store, id, "data")));

                (status, body) = await CallAt(http, HttpMethod.Put, $"/api/upload/chunk?id={id}&offset=0", content: new ByteArrayContent(payload[..300]));
                Check("a chunk says the total received", status == 200 && Prop(body, "received") == "300", $"{status} {body}");
                (status, body) = await CallAt(http, HttpMethod.Put, $"/api/upload/chunk?id={id}&offset=500", content: new ByteArrayContent(payload[500..600]));
                Check("a chunk at the wrong offset is 409 with where to resume", status == 409 && Prop(body, "received") == "300", $"{status} {body}");
                (status, body) = await CallAt(http, HttpMethod.Post, "/api/upload/chunk", content: new ByteArrayContent(payload));
                Equal("a chunk is a PUT", "405", status.ToString());

                // Cut off half way: the bytes that arrived count.
                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, port);
                    var s = client.GetStream();
                    s.Write(Encoding.ASCII.GetBytes($"PUT /api/upload/chunk?id={id}&offset=300 HTTP/1.1\r\nHost: x\r\nX-Connect-Code: {Code}\r\nContent-Length: 400\r\n\r\n"));
                    s.Write(payload, 300, 100);
                    s.Flush();
                    await Task.Delay(300);
                }
                long received = 0;
                for (int i = 0; i < 40 && received != 400; i++)
                {
                    (_, body) = await CallAt(http, HttpMethod.Get, $"/api/upload/status?id={id}");
                    received = long.Parse(Prop(body, "received"));
                    if (received != 400) await Task.Delay(50);
                }
                Check("a chunk cut off keeps what arrived, and status says so", received == 400 && Prop(body, "size") == "1000", body.ToString());

                (status, body) = await CallAt(http, HttpMethod.Post, "/api/upload/finish", new { id });
                Check("finishing early is 409 with what is held", status == 409 && Prop(body, "received") == "400", $"{status} {body}");
                (status, _) = await CallAt(http, HttpMethod.Put, $"/api/upload/chunk?id={id}&offset=400", content: new ByteArrayContent(payload[400..]));
                Equal("the rest arrives from where it stopped", "200", status.ToString());
                (status, body) = await CallAt(http, HttpMethod.Post, "/api/upload/finish", new { id });
                Check("finish into a local folder puts it in place and answers the path",
                    status == 200 && Prop(body, "path") == @"C:\b\big.bin" && fake.Placed == Encoding.UTF8.GetString(payload), $"{status} {body}");
                Check("and the partial is gone", !Directory.Exists(Path.Combine(store, id)));

                (_, body) = await CallAt(http, HttpMethod.Post, "/api/upload/start", new { folder = @"C:\b", name = "x.bin", size = 5 });
                (status, body) = await CallAt(http, HttpMethod.Put, $"/api/upload/chunk?id={Prop(body, "id")}&offset=0", content: new ByteArrayContent(new byte[9]));
                Equal("more than the size said is 400", "400", status.ToString());

                // Into Drive: a job, in bytes.
                (_, body) = await CallAt(http, HttpMethod.Post, "/api/upload/start", new { folder = @"Q:\music", name = "song.flac", size = 3 });
                id = Prop(body, "id");
                await CallAt(http, HttpMethod.Put, $"/api/upload/chunk?id={id}&offset=0", content: new ByteArrayContent(new byte[] { 1, 2, 3 }));
                (status, body) = await CallAt(http, HttpMethod.Post, "/api/upload/finish", new { id });
                Check("finish into Drive is 202 with a job", status == 202 && Prop(body, "job").Length > 0, $"{status} {body}");
                JsonElement job = default;
                for (int i = 0; i < 50; i++)
                {
                    (_, job) = await CallAt(http, HttpMethod.Get, "/api/job?id=" + Prop(body, "job"));
                    if (Prop(job, "state") != "running") break;
                    await Task.Delay(50);
                }
                Check("the upload job ends done, says where it landed, and the partial is gone",
                    Prop(job, "state") == "done" && Prop(job, "kind") == "upload" && Prop(job, "path") == @"Q:\music\song.flac" &&
                    !Directory.Exists(Path.Combine(store, id)), job.ToString());

                (_, body) = await CallAt(http, HttpMethod.Post, "/api/upload/start", new { folder = @"C:\b", name = "gone.bin", size = 10 });
                id = Prop(body, "id");
                (status, _) = await CallAt(http, HttpMethod.Post, "/api/upload/cancel", new { id });
                Check("cancel deletes the partial", status == 200 && !Directory.Exists(Path.Combine(store, id)));
                (status, _) = await CallAt(http, HttpMethod.Get, $"/api/upload/status?id={id}");
                Equal("and the upload is then unknown", "404", status.ToString());
                (status, _) = await CallAt(http, HttpMethod.Get, "/api/upload/status?id=..%5C..%5Cx");
                Equal("an id that is not ours never reaches a path", "404", status.ToString());

                (status, body) = await CallAt(http, HttpMethod.Get, "/api/formats");
                Check("formats lists the audio extensions and the native ones",
                    status == 200 && body.GetProperty("audio").EnumerateArray().Any(e => e.GetString() == ".opus") &&
                    body.GetProperty("native").EnumerateArray().Any(e => e.GetString() == ".flac") &&
                    !body.GetProperty("native").EnumerateArray().Any(e => e.GetString() is ".mp4" or ".mkv" or ".mov"), body.ToString());
            }
            finally { server.Dispose(); }

            var uploads = new ConnectUploads(store);
            var old = uploads.Start(new ConnectUploads.Meta(@"C:\b", "old.bin", 1, "rename", DateTime.UtcNow));
            var fresh = uploads.Start(new ConnectUploads.Meta(@"C:\b", "new.bin", 1, "rename", DateTime.UtcNow));
            System.IO.File.SetLastWriteTimeUtc(uploads.DataPath(old), DateTime.UtcNow.AddHours(-25));
            Check("a sweep takes uploads untouched for a day and leaves the rest",
                uploads.Sweep(ConnectServer.UploadsKeptFor) == 1 && uploads.Find(old) == null && uploads.Find(fresh) != null);
            try { Directory.Delete(store, true); } catch { }
        }

        /// <summary>
        /// /api/audio against real files of four kinds the application decodes: Opus and Vorbis (the managed
        /// decoders), AIFF (read as WAV), and AAC in an MP4 video container (Media Foundation). The header has to
        /// be a valid 24-bit WAV at the file's own rate, and a range from the middle has to be the same samples
        /// a straight decode gives at that place.
        /// </summary>
        private static async Task AudioTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "en-connect-audio-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                const int rate = 48000;
                var pcm = new short[rate * 3 * 2];
                for (int i = 0; i < rate * 3; i++)
                {
                    // A sweep, so that a frame off anywhere shows as a difference.
                    double t = i / (double)rate;
                    short v = (short)(Math.Sin(2 * Math.PI * (200 + 300 * t) * t) * 12000);
                    pcm[2 * i] = v;
                    pcm[2 * i + 1] = (short)(-v / 2);
                }

                var fixtures = new List<(string Path, string What, bool Exact)>();
                var opus = Path.Combine(dir, "sweep.opus");
                try
                {
                    using var file = System.IO.File.Create(opus);
                    var encoder = Concentus.OpusCodecFactory.CreateEncoder(48000, 2, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO, null);
                    var writer = new Concentus.Oggfile.OpusOggWriteStream(encoder, file, null, 48000, 2, false);
                    writer.WriteSamples(pcm, 0, pcm.Length);
                    writer.Finish();
                    fixtures.Add((opus, "Opus", false));
                }
                catch (Exception e) { Check("an Opus fixture could be written", false, e.Message); }

                var vorbis = Path.Combine(AppContext.BaseDirectory, "Fixtures", "clicks.ogg");
                if (System.IO.File.Exists(vorbis)) fixtures.Add((vorbis, "Vorbis", false));
                else Check("the Vorbis fixture is next to the suite", false, vorbis);

                var aiff = Path.Combine(dir, "sweep.aiff");
                ExactSeekTests.WriteAiff(aiff, pcm, 44100, "AIFF");
                fixtures.Add((aiff, "AIFF", true));

                var mp4 = Path.Combine(dir, "tone.mp4");
                if (Tests.Mpeg4.WriteTone(mp4, 48000, 2, 3, out var why)) fixtures.Add((mp4, "AAC in MP4", false));
                else Console.WriteLine($"  (no AAC encoder here: {why})");

                foreach (var (path, what, exact) in fixtures)
                {
                    using var audio = ConnectAudio.Open(path, null, new FileInfo(path).Length, out var said);
                    Check($"{what}: /api/audio opens it", audio != null, said);
                    if (audio == null) continue;
                    var plan = audio.Plan;
                    int expectRate = what == "AIFF" ? 44100 : what == "Vorbis" ? plan.Rate : 48000;
                    Check($"{what}: at the file's own rate, stereo at most ({plan.Rate} Hz, {plan.Channels} ch, {plan.Frames:N0} frames)",
                        plan.Rate == expectRate && plan.Channels is 1 or 2 && plan.Frames > 0);

                    var whole = new MemoryStream();
                    audio.WriteRange(0, plan.TotalBytes - 1, whole, CancellationToken.None);
                    var all = whole.ToArray();
                    Check($"{what}: the whole WAV is exactly as long as its header says", all.Length == plan.TotalBytes, $"{all.Length} vs {plan.TotalBytes}");
                    Check($"{what}: the header is a valid 24-bit PCM WAV", ValidHeader(all, plan), BitConverter.ToString(all, 0, 44));

                    // A range from a third of the way in, starting one byte into a frame.
                    long frame = plan.Frames / 3;
                    long from = AudioPlan.HeaderBytes + frame * plan.BlockAlign + 1;
                    long to = from + plan.BlockAlign * 2000L;
                    using var again = ConnectAudio.Open(path, null, new FileInfo(path).Length, out _);
                    var part = new MemoryStream();
                    again!.WriteRange(from, to, part, CancellationToken.None);
                    var got = part.ToArray();
                    Check($"{what}: a mid-file range is as long as asked", got.Length == to - from + 1, got.Length.ToString());

                    // Compared as samples, aligned on the frame: exact for PCM, and for the lossy codecs the best
                    // alignment has to be no shift at all, and close.
                    var reference = all.AsSpan((int)(from - 1), (int)(to - from + 2)).ToArray();
                    var mine = new byte[reference.Length];
                    all.AsSpan((int)(from - 1), 1).CopyTo(mine);
                    got.CopyTo(mine, 1);
                    double at0 = Difference(reference, mine, 0, plan.BlockAlign);
                    double shifted = Math.Min(Difference(all.AsSpan((int)(from - 1 - plan.BlockAlign), reference.Length).ToArray(), mine, 0, plan.BlockAlign),
                                              Difference(all.AsSpan((int)(from - 1 + plan.BlockAlign), reference.Length).ToArray(), mine, 0, plan.BlockAlign));
                    if (exact)
                        Check($"{what}: the range is the same bytes a straight decode gives", reference.AsSpan(1).SequenceEqual(got));
                    else
                        Check($"{what}: the range lands on the same samples (mean difference {at0:0.0000} there, {shifted:0.0000} a frame off)",
                            at0 < 0.01 && at0 < shifted);
                }

                // Over HTTP, through the server: HEAD, and a range.
                int port = FreePort();
                var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port, files: new FakeFiles());
                try
                {
                    server.Start();
                    if (!await WaitForListener(port)) { Check("the audio server listens", false); return; }
                    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };
                    var url = "/api/audio?path=" + Uri.EscapeDataString(aiff);
                    var r = await Send(http, HttpMethod.Head, url);
                    using var local = ConnectAudio.Open(aiff, null, new FileInfo(aiff).Length, out _);
                    var total = local!.Plan.TotalBytes;
                    Check("HEAD on /api/audio gives the WAV's whole length, as audio/wav",
                        (int)r.StatusCode == 200 && r.Content.Headers.ContentLength == total && r.Content.Headers.ContentType?.MediaType == "audio/wav",
                        $"{(int)r.StatusCode} {r.Content.Headers.ContentLength} vs {total}");
                    var whole = new MemoryStream();
                    local.WriteRange(0, total - 1, whole, CancellationToken.None);
                    r = await Send(http, HttpMethod.Get, url, range: new RangeHeaderValue(100_000, 160_000));
                    var bytes = await r.Content.ReadAsByteArrayAsync();
                    Check("a Range on /api/audio is 206 with the same bytes",
                        (int)r.StatusCode == 206 && bytes.AsSpan().SequenceEqual(whole.ToArray().AsSpan(100_000, 60_001)) &&
                        r.Content.Headers.ContentRange?.ToString() == $"bytes 100000-160000/{total}", $"{(int)r.StatusCode} {bytes.Length}");
                    r = await Send(http, HttpMethod.Get, "/api/audio?path=" + Uri.EscapeDataString(Path.Combine(dir, "nothing.flac")));
                    Equal("audio of a file that is not there is 404", "404", ((int)r.StatusCode).ToString());
                    var junk = Path.Combine(dir, "junk.flac");
                    System.IO.File.WriteAllBytes(junk, new byte[5000]);
                    r = await Send(http, HttpMethod.Get, "/api/audio?path=" + Uri.EscapeDataString(junk));
                    Equal("audio of something no decoder takes is 500, with a sentence", "500", ((int)r.StatusCode).ToString());
                }
                finally { server.Dispose(); }

                Check("a length nobody knows is estimated generously: a minute at least",
                    ConnectAudio.EstimateFrames(1000, 48000) == 60 * 48000 && ConnectAudio.EstimateFrames(80_000_000, 44100) == 10_000L * 44100);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static bool ValidHeader(byte[] wav, AudioPlan plan)
        {
            if (wav.Length < 44) return false;
            string Tag(int at) => Encoding.ASCII.GetString(wav, at, 4);
            return Tag(0) == "RIFF" && BitConverter.ToUInt32(wav, 4) == wav.Length - 8 && Tag(8) == "WAVE" && Tag(12) == "fmt " &&
                   BitConverter.ToUInt32(wav, 16) == 16 && BitConverter.ToUInt16(wav, 20) == 1 &&
                   BitConverter.ToUInt16(wav, 22) == plan.Channels && BitConverter.ToUInt32(wav, 24) == plan.Rate &&
                   BitConverter.ToUInt32(wav, 28) == plan.Rate * plan.Channels * 3 && BitConverter.ToUInt16(wav, 32) == plan.Channels * 3 &&
                   BitConverter.ToUInt16(wav, 34) == 24 && Tag(36) == "data" && BitConverter.ToUInt32(wav, 40) == wav.Length - 44;
        }

        /// <summary>Mean absolute difference of two runs of 24-bit samples, as a fraction of full scale.</summary>
        private static double Difference(byte[] a, byte[] b, int start, int align)
        {
            int n = Math.Min(a.Length, b.Length) / 3;
            if (n == 0) return 1;
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                int x = a[3 * i] | a[3 * i + 1] << 8 | (sbyte)a[3 * i + 2] << 16;
                int y = b[3 * i] | b[3 * i + 1] << 8 | (sbyte)b[3 * i + 2] << 16;
                sum += Math.Abs(x - y);
            }
            return sum / n / 8388608.0;
        }

        /// <summary>The three shapes of a single range, and every way one can fail to fit the file.</summary>
        private static void RangeTests()
        {
            bool ok = ConnectServer.TryParseRange("bytes=10-19", 300, out long from, out long to);
            Check("a closed range parses", ok && from == 10 && to == 19, $"{ok} {from}-{to}");
            ok = ConnectServer.TryParseRange("bytes=100-", 300, out from, out to);
            Check("an open range runs to the end", ok && from == 100 && to == 299, $"{ok} {from}-{to}");
            ok = ConnectServer.TryParseRange("bytes=-5", 300, out from, out to);
            Check("a suffix range is the last n bytes", ok && from == 295 && to == 299, $"{ok} {from}-{to}");
            ok = ConnectServer.TryParseRange("bytes=-500", 300, out from, out to);
            Check("a suffix longer than the file is the whole file", ok && from == 0 && to == 299, $"{ok} {from}-{to}");
            ok = ConnectServer.TryParseRange("bytes=250-9999", 300, out from, out to);
            Check("an end past the file is clamped to it", ok && from == 250 && to == 299, $"{ok} {from}-{to}");
            Check("a start at the length does not fit", !ConnectServer.TryParseRange("bytes=300-", 300, out _, out _));
            Check("an end before the start does not parse", !ConnectServer.TryParseRange("bytes=20-10", 300, out _, out _));
            Check("a zero suffix does not parse", !ConnectServer.TryParseRange("bytes=-0", 300, out _, out _));
            Check("another unit does not parse", !ConnectServer.TryParseRange("items=0-5", 300, out _, out _));
            Check("nothing fits an empty file", !ConnectServer.TryParseRange("bytes=0-", 0, out _, out _));
            ok = ConnectServer.TryParseRange("bytes=0-0,5-9", 300, out from, out to);
            Check("of several ranges the first is served", ok && from == 0 && to == 0, $"{ok} {from}-{to}");
        }

        /// <summary>100.64.0.0/10 and nothing either side of it.</summary>
        private static void TailscaleTests()
        {
            Check("this machine's Tailscale address is Tailscale", ConnectServer.IsTailscale(IPAddress.Parse("100.67.248.25")));
            Check("the bottom of the range is", ConnectServer.IsTailscale(IPAddress.Parse("100.64.0.0")));
            Check("the top of the range is", ConnectServer.IsTailscale(IPAddress.Parse("100.127.255.255")));
            Check("just above the range is not", !ConnectServer.IsTailscale(IPAddress.Parse("100.128.0.1")));
            Check("just below the range is not", !ConnectServer.IsTailscale(IPAddress.Parse("100.63.255.255")));
            Check("a home network is not", !ConnectServer.IsTailscale(IPAddress.Parse("192.168.1.20")));
            Check("loopback is not", !ConnectServer.IsTailscale(IPAddress.Loopback));
            Check("IPv6 is not", !ConnectServer.IsTailscale(IPAddress.Parse("fd7a:115c:a1e0::1")));
            Check("a tailnet peer may connect", ConnectServer.IsAllowedPeer(IPAddress.Parse("100.108.16.12")));
            Check("a Tailscale IPv6 peer may connect", ConnectServer.IsAllowedPeer(IPAddress.Parse("fd7a:115c:a1e0::b732:f81b")));
            Check("this machine may connect", ConnectServer.IsAllowedPeer(IPAddress.Loopback) && ConnectServer.IsAllowedPeer(IPAddress.IPv6Loopback));
            Check("a mapped tailnet address may connect", ConnectServer.IsAllowedPeer(IPAddress.Parse("::ffff:100.108.16.12")));
            Check("a home network may not", !ConnectServer.IsAllowedPeer(IPAddress.Parse("192.168.1.20")));
            Check("the internet may not", !ConnectServer.IsAllowedPeer(IPAddress.Parse("8.8.8.8")));
            Check("other IPv6 may not", !ConnectServer.IsAllowedPeer(IPAddress.Parse("2001:db8::1")));

            var code = ConnectServer.NewCode();
            Check("a pairing code is eight digits", code.Length == 8 && code.All(char.IsAsciiDigit), code);
        }

        private static async Task ServerTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "en-connect-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
            string odd = Path.Combine(dir, "a+b 日本.txt");
            string bin = Path.Combine(dir, "data.bin");
            System.IO.File.WriteAllBytes(bin, data);
            System.IO.File.WriteAllText(odd, "plus, space and kanji");
            System.IO.File.WriteAllText(Path.Combine(dir, "zeta.txt"), "z");
            Directory.CreateDirectory(Path.Combine(dir, "Beta"));
            Directory.CreateDirectory(Path.Combine(dir, "alpha"));

            // A folder that is not on disk at all: only the hook can answer for it, as the Drive mount does.
            string held = Path.Combine(dir, "held elsewhere");
            var heldEntries = new List<ConnectEntry>
            {
                new("song.flac", false, 5, DateTime.UtcNow),
                new("Album", true, 0, DateTime.UtcNow),
            };

            int port = FreePort();
            var server = new ConnectServer(
                p => string.Equals(p, held, StringComparison.OrdinalIgnoreCase) ? heldEntries : null,
                p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port);
            try
            {
                server.Start();
                bool up = await WaitForListener(port);
                Check("the server listens on loopback", up);
                if (!up) return;

                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(15) };

                // The code.
                var r = await http.GetAsync("/api/info");
                Equal("no pairing code is refused", "401", ((int)r.StatusCode).ToString());
                r = await Send(http, HttpMethod.Get, "/api/info", code: "87654321");
                Equal("a wrong pairing code is refused", "401", ((int)r.StatusCode).ToString());
                r = await http.GetAsync($"/api/info?code={Code}");
                Equal("the code in the query is accepted", "200", ((int)r.StatusCode).ToString());
                r = await Send(http, HttpMethod.Get, "/api/info", code: "1234-5678");
                Equal("the code as shown, with a separator, is accepted", "200", ((int)r.StatusCode).ToString());
                r = await Send(http, HttpMethod.Get, "/api/info");
                using (var info = JsonDocument.Parse(await r.Content.ReadAsStringAsync()))
                    Equal("info names the machine", Environment.MachineName, info.RootElement.GetProperty("name").GetString() ?? "");

                // Listing.
                r = await Send(http, HttpMethod.Get, "/api/list?path=" + Uri.EscapeDataString(dir));
                Equal("a folder lists", "200", ((int)r.StatusCode).ToString());
                using (var list = JsonDocument.Parse(await r.Content.ReadAsStringAsync()))
                {
                    var names = list.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
                    Equal("folders first, then files, each by name", "alpha|Beta|a+b 日本.txt|data.bin|zeta.txt", string.Join("|", names));
                    var dataRow = list.RootElement.EnumerateArray().First(e => e.GetProperty("name").GetString() == "data.bin");
                    Check("a file row carries its size, camel-cased",
                        dataRow.GetProperty("size").GetInt64() == 300 && !dataRow.GetProperty("folder").GetBoolean());
                    Check("and when it was modified", dataRow.TryGetProperty("modified", out _));
                }
                r = await Send(http, HttpMethod.Get, "/api/list?path=" + Uri.EscapeDataString(held));
                using (var list = JsonDocument.Parse(await r.Content.ReadAsStringAsync()))
                {
                    var names = list.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
                    Equal("a folder the hook holds is answered from the hook, sorted", "Album|song.flac", string.Join("|", names));
                }
                r = await Send(http, HttpMethod.Get, "/api/list?path=" + Uri.EscapeDataString(Path.Combine(dir, "missing")));
                Equal("a folder that is not there is 404", "404", ((int)r.StatusCode).ToString());
                r = await Send(http, HttpMethod.Get, "/api/list?path=relative");
                Equal("a relative path is 404", "404", ((int)r.StatusCode).ToString());

                // Files.
                r = await Send(http, HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(bin));
                var body = await r.Content.ReadAsByteArrayAsync();
                Check("a whole file comes back byte for byte", (int)r.StatusCode == 200 && body.SequenceEqual(data), $"{(int)r.StatusCode}, {body.Length} bytes");
                Check("and says ranges are welcome", r.Headers.AcceptRanges.Contains("bytes"));

                await RangeCase(http, bin, data, new RangeHeaderValue(10, 19), 10, 19);
                await RangeCase(http, bin, data, new RangeHeaderValue(100, null), 100, 299);
                await RangeCase(http, bin, data, new RangeHeaderValue(null, 5), 295, 299);

                r = await Send(http, HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(bin), range: new RangeHeaderValue(300, 400));
                Equal("a range past the end is 416", "416", ((int)r.StatusCode).ToString());
                Equal("and says how long the file is", "bytes */300", r.Content.Headers.ContentRange?.ToString() ?? "");

                r = await Send(http, HttpMethod.Head, "/api/file?path=" + Uri.EscapeDataString(bin));
                Check("HEAD gives the length", (int)r.StatusCode == 200 && r.Content.Headers.ContentLength == 300,
                    $"{(int)r.StatusCode}, {r.Content.Headers.ContentLength}");

                r = await Send(http, HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(odd));
                Equal("a name with a plus, a space and kanji round-trips", "plus, space and kanji", await r.Content.ReadAsStringAsync());
                r = await Send(http, HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(odd).Replace("%2B", "+"));
                Equal("a plus left unencoded is a plus, not a space", "plus, space and kanji", await r.Content.ReadAsStringAsync());
                r = await Send(http, HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(Path.Combine(dir, "nope.bin")));
                Equal("a file that is not there is 404", "404", ((int)r.StatusCode).ToString());
                r = await Send(http, HttpMethod.Get, "/api/nothing");
                Equal("an unknown route is 404", "404", ((int)r.StatusCode).ToString());

                KeepAliveTests(port, bin, data);
            }
            finally
            {
                server.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// The v2 routes against a fake <see cref="IConnectFiles"/>: what the server itself owns is the shape of
        /// every answer, the status of every refusal, the size cache, the job table and the request body.
        /// </summary>
        private sealed class FakeFiles : IConnectFiles
        {
            public (long Used, long Limit)? Quota = (300, 1000);
            public string? Away;
            public int Sizes;
            public string? LastUpload;
            public List<string> Calls = new();
            public TaskCompletionSource<bool>? HoldTransfer;

            public string? Unavailable(string path) => Away != null && path.StartsWith("Q:", StringComparison.OrdinalIgnoreCase) ? Away : null;
            public (long Used, long Limit)? DriveQuota() => Quota;

            public Task<ConnectSize> SizeAsync(string folder, CancellationToken token)
            {
                Interlocked.Increment(ref Sizes);
                if (folder.EndsWith("gone", StringComparison.Ordinal)) throw new ConnectException(404, "That folder is not there any more.");
                return Task.FromResult(new ConnectSize(folder, 1234, 5, 2, true));
            }

            public Task<ConnectStat> StatAsync(string path, CancellationToken token) => Task.FromResult(
                path.EndsWith(".flac", StringComparison.Ordinal)
                    ? new ConnectStat(path, Path.GetFileName(path), false, 10, DateTime.UtcNow, DateTime.UtcNow, false, false,
                        new SongSummary("Song", "Artist", null, 2020, 3, 181.5))
                    : new ConnectStat(path, Path.GetFileName(path), true, 0, DateTime.UtcNow, DateTime.UtcNow, true, false));

            public Task<string> RenameAsync(string path, string newName, CancellationToken token)
            {
                Calls.Add($"rename {path} {newName}");
                if (newName == "taken") throw new ConnectException(409, "Something called taken is already there.");
                return Task.FromResult(Path.Combine(Path.GetDirectoryName(path)!, newName));
            }

            public Task<string> CreateFolderAsync(string parent, string name, CancellationToken token)
            {
                Calls.Add($"mkdir {parent} {name}");
                if (name == "taken") throw new ConnectException(409, "Something called taken is already there.");
                return Task.FromResult(Path.Combine(parent, name));
            }

            public Task<ConnectDeleted> DeleteAsync(IReadOnlyList<string> paths, CancellationToken token) =>
                Task.FromResult(new ConnectDeleted(paths.Count - 1, new[] { new ConnectFailure(paths[^1], "It is not there any more.") }));

            public void CheckTransfer(IReadOnlyList<string> paths, string destination, bool move)
            {
                if (move && paths.Any(p => p.StartsWith("Q:", StringComparison.Ordinal)) && paths.Any(p => !p.StartsWith("Q:", StringComparison.Ordinal)))
                    throw new ConnectException(400, "Some of these are on Google Drive and some are not; move them separately.");
            }

            public async Task TransferAsync(ConnectJob job, IReadOnlyList<string> paths, string destination, bool move, PasteConflictPolicy conflict)
            {
                Calls.Add($"{(move ? "move" : "copy")} {paths.Count} {conflict}");
                job.Report(0, paths.Count, 0, 100, paths[0]);
                if (HoldTransfer != null) await HoldTransfer.Task.WaitAsync(job.Token);
                job.Fail(paths[^1], "It is not there any more.");
                job.Report(paths.Count - 1, paths.Count, 100, 100, "");
            }

            public bool OnDrive(string path) => path.StartsWith("Q:", StringComparison.OrdinalIgnoreCase);

            public string? Placed;

            public async Task<string> PlaceFileAsync(string file, string folder, string name, PasteConflictPolicy conflict,
                Action<long, long>? progress, CancellationToken token)
            {
                Placed = System.IO.File.ReadAllText(file);
                if (OnDrive(folder)) { progress?.Invoke(5, 10); await Task.Delay(50, token); progress?.Invoke(10, 10); }
                return Path.Combine(folder, name);
            }

            public IReadOnlyList<string> AudioExtensions() => new[] { ".flac", ".opus", ".mkv" };

            public Task<Dictionary<string, object?>> DetailsAsync(string path, ConnectStat stat, bool hash, CancellationToken token) =>
                Task.FromResult(new Dictionary<string, object?>
                {
                    ["file"] = new Dictionary<string, object?> { ["name"] = stat.Name, ["hashed"] = hash },
                });

            public async Task<string> UploadAsync(string folder, string name, PasteConflictPolicy conflict, Stream body, CancellationToken token)
            {
                if (conflict == PasteConflictPolicy.Skip) return Path.Combine(folder, name);   // the body is left unread
                using var read = new MemoryStream();
                await body.CopyToAsync(read, token);
                LastUpload = Encoding.UTF8.GetString(read.ToArray());
                return Path.Combine(folder, name);
            }
        }

        private static async Task ActionTests()
        {
            var fake = new FakeFiles();
            int port = FreePort();
            string drive = DriveInfo.GetDrives().First(d => d.IsReady).Name;
            var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port,
                isDrive: root => root == drive, files: fake);
            try
            {
                server.Start();
                if (!await WaitForListener(port)) { Check("the v2 server listens", false); return; }
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(15) };

                async Task<(int Status, JsonElement Body)> Call(HttpMethod method, string url, object? json = null, HttpContent? content = null)
                {
                    var request = new HttpRequestMessage(method, url);
                    request.Headers.Add("X-Connect-Code", Code);
                    if (json != null) request.Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");
                    if (content != null) request.Content = content;
                    var response = await http.SendAsync(request);
                    var text = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
                    return ((int)response.StatusCode, doc.RootElement.Clone());
                }
                static string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : "(missing)";

                var (status, body) = await Call(HttpMethod.Get, "/api/info");
                Equal("info says apiVersion 4", "4", S(body, "apiVersion"));

                // Drives: the Drive letter from the quota; every drive carries used.
                (status, body) = await Call(HttpMethod.Get, "/api/drives");
                var rows = body.EnumerateArray().ToList();
                var g = rows.First(r => S(r, "name") == drive);
                Check("the Drive letter's size is the quota's limit, free the rest, used what is used",
                    S(g, "kind") == "GoogleDrive" && S(g, "size") == "1000" && S(g, "free") == "700" && S(g, "used") == "300" && S(g, "unlimited") == "False",
                    g.ToString());
                Check("every drive says used, as size minus free",
                    rows.All(r => r.TryGetProperty("used", out var u) &&
                                  (S(r, "kind") == "GoogleDrive" || u.GetInt64() == r.GetProperty("size").GetInt64() - r.GetProperty("free").GetInt64())));
                fake.Quota = (4242, 0);
                (_, body) = await Call(HttpMethod.Get, "/api/drives");
                g = body.EnumerateArray().First(r => S(r, "name") == drive);
                Check("no limit is unlimited, size and free zero, used still said",
                    S(g, "unlimited") == "True" && S(g, "size") == "0" && S(g, "free") == "0" && S(g, "used") == "4242", g.ToString());

                // Errors are JSON with a sentence.
                (status, body) = await Call(HttpMethod.Get, "/api/list?path=relative");
                Check("an error is JSON with an error sentence", status == 404 && S(body, "error").Length > 5, $"{status} {body}");
                (status, body) = await Call(HttpMethod.Get, "/api/rename");
                Equal("a GET on a POST route is 405", "405", status.ToString());
                (status, _) = await Call(HttpMethod.Post, "/api/size?path=C%3A%5C");
                Equal("a POST on a GET route is 405", "405", status.ToString());

                // Sizes, cached for a minute, dropped on a write.
                string folder = @"C:\Some Folder";
                (status, body) = await Call(HttpMethod.Get, "/api/size?path=" + Uri.EscapeDataString(folder));
                Check("size answers bytes, files, folders and complete",
                    status == 200 && S(body, "bytes") == "1234" && S(body, "files") == "5" && S(body, "folders") == "2" && S(body, "complete") == "True" && S(body, "path") == folder,
                    body.ToString());
                await Call(HttpMethod.Get, "/api/size?path=" + Uri.EscapeDataString(folder));
                Equal("a second size inside a minute comes from the cache", "1", fake.Sizes.ToString());
                (status, _) = await Call(HttpMethod.Get, "/api/size?path=" + Uri.EscapeDataString(@"C:\gone"));
                Equal("a refusal from the files keeps its status", "404", status.ToString());
                await Call(HttpMethod.Get, "/api/size?path=" + Uri.EscapeDataString(@"C:\gone"));
                Equal("and is not cached", "3", fake.Sizes.ToString());

                // Stat, with tags only for audio.
                (status, body) = await Call(HttpMethod.Get, "/api/stat?path=" + Uri.EscapeDataString(@"C:\a\song.flac"));
                Check("stat of a track carries its tags as values",
                    status == 200 && body.TryGetProperty("tags", out var tags) && S(tags, "title") == "Song" && S(tags, "durationSeconds") == "181.5" && S(tags, "year") == "2020",
                    body.ToString());
                (status, body) = await Call(HttpMethod.Get, "/api/stat?path=" + Uri.EscapeDataString(@"C:\a\folder"));
                Check("stat of a folder has no tags, and says readOnly and onDrive",
                    status == 200 && !body.TryGetProperty("tags", out _) && S(body, "readOnly") == "True" && S(body, "onDrive") == "False", body.ToString());

                // Rename.
                (status, body) = await Call(HttpMethod.Post, "/api/rename", new { path = @"C:\a\old.txt", newName = "new.txt" });
                Check("rename answers ok and the new path", status == 200 && S(body, "ok") == "True" && S(body, "path") == @"C:\a\new.txt", $"{status} {body}");
                await Call(HttpMethod.Get, "/api/size?path=" + Uri.EscapeDataString(folder));
                Equal("a write empties the size cache", "4", fake.Sizes.ToString());
                (status, _) = await Call(HttpMethod.Post, "/api/rename", new { path = @"C:\a\old.txt", newName = @"..\escape" });
                Equal("a name with a slash is 400, before anything is asked", "400", status.ToString());
                (status, _) = await Call(HttpMethod.Post, "/api/rename", new { path = @"C:\a\old.txt", newName = "taken" });
                Equal("a name taken is 409", "409", status.ToString());
                (status, _) = await Call(HttpMethod.Post, "/api/rename", new { path = @"C:\", newName = "x" });
                Equal("a drive cannot be renamed", "400", status.ToString());
                (status, _) = await Call(HttpMethod.Post, "/api/rename", content: new StringContent("not json"));
                Equal("a body that is not JSON is 400", "400", status.ToString());

                // Mkdir.
                (status, body) = await Call(HttpMethod.Post, "/api/mkdir", new { parent = @"C:\a", name = "New" });
                Check("mkdir answers the new path", status == 200 && S(body, "path") == @"C:\a\New", $"{status} {body}");
                (status, _) = await Call(HttpMethod.Post, "/api/mkdir", new { parent = @"C:\a", name = "taken" });
                Equal("mkdir onto a name taken is 409", "409", status.ToString());

                // Delete.
                (status, body) = await Call(HttpMethod.Post, "/api/delete", new { paths = new[] { @"C:\a\1", @"C:\a\2" } });
                Check("delete says how many and what failed, with the path",
                    status == 200 && S(body, "deleted") == "1" && body.GetProperty("failed")[0].GetProperty("path").GetString() == @"C:\a\2", $"{status} {body}");
                (status, _) = await Call(HttpMethod.Post, "/api/delete", new { paths = new[] { @"D:\" } });
                Equal("a drive cannot be deleted", "400", status.ToString());
                (status, _) = await Call(HttpMethod.Post, "/api/delete", new { paths = Array.Empty<string>() });
                Equal("nothing to delete is 400", "400", status.ToString());

                // Drive away is 503.
                fake.Away = "Google Drive needs signing in again on this computer";
                (status, body) = await Call(HttpMethod.Get, "/api/size?path=" + Uri.EscapeDataString(@"Q:\music"));
                Check("a path on a Drive that cannot answer is 503", status == 503 && S(body, "error").Contains("signing in"), $"{status} {body}");
                fake.Away = null;

                // Copy as a job.
                (status, body) = await Call(HttpMethod.Post, "/api/copy", new { paths = new[] { @"C:\a\1", @"C:\a\2" }, destination = @"C:\b", conflict = "overwrite" });
                Check("copy is 202 with a job id", status == 202 && S(body, "job").Length >= 8, $"{status} {body}");
                string id = S(body, "job");
                JsonElement job = default;
                for (int i = 0; i < 50; i++)
                {
                    (_, job) = await Call(HttpMethod.Get, "/api/job?id=" + id);
                    if (S(job, "state") != "running") break;
                    await Task.Delay(50);
                }
                Check("the job finishes done, with its counts, its kind and what failed",
                    S(job, "state") == "done" && S(job, "kind") == "copy" && S(job, "items") == "2" && S(job, "itemsDone") == "1" &&
                    S(job, "bytesDone") == "100" && job.GetProperty("failed")[0].GetProperty("path").GetString() == @"C:\a\2",
                    job.ToString());
                Check("the conflict reached the files as the policy", fake.Calls.Contains("copy 2 Overwrite"), string.Join("; ", fake.Calls));

                (status, _) = await Call(HttpMethod.Post, "/api/copy", new { paths = new[] { @"C:\a\1" }, destination = @"C:\b", conflict = "merge" });
                Equal("an unknown conflict is 400", "400", status.ToString());
                (status, body) = await Call(HttpMethod.Post, "/api/move", new { paths = new[] { @"Q:\x", @"C:\y" }, destination = @"C:\b" });
                Check("a move mixing Drive and local is refused before any job", status == 400 && S(body, "error").Contains("separately"), $"{status} {body}");
                (status, _) = await Call(HttpMethod.Get, "/api/job?id=nope");
                Equal("an unknown job is 404", "404", status.ToString());

                // Cancel.
                fake.HoldTransfer = new TaskCompletionSource<bool>();
                (_, body) = await Call(HttpMethod.Post, "/api/move", new { paths = new[] { @"C:\a\1" }, destination = @"C:\b" });
                id = S(body, "job");
                (_, job) = await Call(HttpMethod.Get, "/api/job?id=" + id);
                Equal("a held job is running", "running", S(job, "state"));
                (status, body) = await Call(HttpMethod.Post, "/api/job/cancel", new { id });
                Check("cancel answers ok", status == 200 && S(body, "ok") == "True", $"{status} {body}");
                for (int i = 0; i < 50; i++)
                {
                    (_, job) = await Call(HttpMethod.Get, "/api/job?id=" + id);
                    if (S(job, "state") != "running") break;
                    await Task.Delay(50);
                }
                Check("and the job ends cancelled, as a move", S(job, "state") == "cancelled" && S(job, "kind") == "move", job.ToString());
                fake.HoldTransfer = null;

                // Upload: plain, chunked, and a refusal left unread on a kept-alive connection.
                (status, body) = await Call(HttpMethod.Post, "/api/upload?folder=" + Uri.EscapeDataString(@"C:\b") + "&name=" + Uri.EscapeDataString("a+b.txt"),
                    content: new ByteArrayContent(Encoding.UTF8.GetBytes("hello upload")));
                Check("upload hands the body over and answers the path", status == 200 && fake.LastUpload == "hello upload" && S(body, "path") == @"C:\b\a+b.txt",
                    $"{status} {body} {fake.LastUpload}");
                var chunked = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 70_000) + "end")));
                var chunkedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/upload?folder=C%3A%5Cb&name=c.txt");
                chunkedRequest.Headers.Add("X-Connect-Code", Code);
                chunkedRequest.Headers.TransferEncodingChunked = true;
                chunkedRequest.Content = chunked;
                var chunkedResponse = await http.SendAsync(chunkedRequest);
                Check("a chunked upload arrives whole", (int)chunkedResponse.StatusCode == 200 && fake.LastUpload?.Length == 70_003 && fake.LastUpload.EndsWith("end"),
                    $"{(int)chunkedResponse.StatusCode} {fake.LastUpload?.Length}");
                (status, _) = await Call(HttpMethod.Post, "/api/upload?folder=C%3A%5Cb&name=d.txt");
                Equal("an upload with no body is 400", "400", status.ToString());

                UploadKeepAliveTests(port);
            }
            finally { server.Dispose(); }
        }

        /// <summary>An upload answered without reading its body must not leave the body to be read as the next request.</summary>
        private static void UploadKeepAliveTests(int port)
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            client.ReceiveTimeout = 10_000;
            using var s = client.GetStream();
            var payload = new string('z', 5000);
            s.Write(Encoding.ASCII.GetBytes($"POST /api/upload?folder=C%3A%5Cb&name=e.txt&conflict=skip HTTP/1.1\r\nHost: x\r\nX-Connect-Code: {Code}\r\nContent-Length: {payload.Length}\r\n\r\n{payload}"));
            var (status, _, body) = ReadResponse(s, head: false);
            Check("a skipped upload answers without reading the body", status == 200 && Encoding.UTF8.GetString(body).Contains("e.txt"), status.ToString());
            s.Write(Encoding.ASCII.GetBytes($"GET /api/info HTTP/1.1\r\nHost: x\r\nX-Connect-Code: {Code}\r\n\r\n"));
            (status, _, body) = ReadResponse(s, head: false);
            Check("and the next request on the connection is read cleanly", status == 200 && Encoding.UTF8.GetString(body).Contains("apiVersion"), status.ToString());

            using (var closing = new TcpClient())
            {
                closing.Connect(IPAddress.Loopback, port);
                closing.ReceiveTimeout = 10_000;
                var c = closing.GetStream();
                c.Write(Encoding.ASCII.GetBytes($"POST /api/upload?folder=C%3A%5Cb&name=g.txt&conflict=skip HTTP/1.1\r\nHost: x\r\nX-Connect-Code: {Code}\r\nConnection: close\r\nContent-Length: {payload.Length}\r\n\r\n{payload}"));
                var (closedStatus, _, _) = ReadResponse(c, head: false);
                Check("an unread body on a closing connection is drained, so the answer is not lost to a reset", closedStatus == 200, closedStatus.ToString());
            }

            s.Write(Encoding.ASCII.GetBytes($"POST /api/upload?folder=C%3A%5Cb&name=f.txt HTTP/1.1\r\nHost: x\r\nX-Connect-Code: {Code}\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n"));
            var (interim, _, _) = ReadResponse(s, head: true);
            Check("Expect: 100-continue is answered with 100 when the body is wanted", interim == 100, interim.ToString());
            s.Write(Encoding.ASCII.GetBytes("12345"));
            (status, _, _) = ReadResponse(s, head: false);
            Equal("and then with the answer", "200", status.ToString());
        }

        /// <summary>A range source whose reads are counted and which says when it has been let go of.</summary>
        private sealed class CountingSource : IRangeSource
        {
            public int Reads;
            public bool Disposed;
            public TaskCompletionSource<bool> Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Hang;
            public long Length { get; init; } = 3_000_000;
            public int Streams => 1;

            public async Task<int> ReadAsync(long offset, Memory<byte> into, CancellationToken token)
            {
                Interlocked.Increment(ref Reads);
                if (Hang)
                {
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) { Cancelled.TrySetResult(true); throw; }
                }
                int n = (int)Math.Min(into.Length, Length - offset);
                for (int i = 0; i < n; i++) into.Span[i] = (byte)((offset + i) % 251);
                return n;
            }

            public void Dispose() => Disposed = true;
        }

        private static async Task StreamPoolTests()
        {
            int opened = 0;
            var sources = new List<CountingSource>();
            using var pool = new ConnectStreams(p =>
            {
                if (!p.StartsWith("Q:", StringComparison.Ordinal)) return null;
                Interlocked.Increment(ref opened);
                var s = new CountingSource();
                lock (sources) sources.Add(s);
                return s;
            }, budget: 8L << 20);

            Check("a path that is not remote gets no lease", pool.Lease(@"C:\x.flac") == null);
            var a = pool.Lease(@"Q:\song.flac")!;
            var b = pool.Lease(@"q:\SONG.flac")!;
            Equal("two requests for one file share one download", "1", opened.ToString());

            var buffer = new byte[1000];
            int got = await a.ReadAsync(2_000_000, buffer, CancellationToken.None);
            Check("a read through a lease gives the file's bytes", got == 1000 && buffer[0] == (byte)(2_000_000 % 251) && buffer[999] == (byte)((2_000_000 + 999) % 251),
                $"{got} {buffer[0]}");
            a.Dispose();
            b.Dispose();
            pool.Lease(@"Q:\song.flac")!.Dispose();
            Equal("a file let go of is still held for the next seek", "1", opened.ToString());

            pool.Sweep(DateTime.UtcNow + ConnectStreams.IdleFor - TimeSpan.FromSeconds(5));
            Equal("not yet idle long enough", "1", pool.Count.ToString());
            pool.Sweep(DateTime.UtcNow + ConnectStreams.IdleFor + TimeSpan.FromSeconds(1));
            Check("idle for a minute, it is closed and its source disposed", pool.Count == 0 && sources[0].Disposed);

            var held = pool.Lease(@"Q:\held.flac")!;
            pool.Sweep(DateTime.UtcNow + TimeSpan.FromHours(1));
            Equal("a file somebody is reading is never swept", "1", pool.Count.ToString());
            held.Dispose();
        }

        /// <summary>A client that hangs up mid-response has its read called off, not left to finish.</summary>
        private static async Task HangUpTests()
        {
            var slow = new CountingSource { Hang = true };
            int port = FreePort();
            var server = new ConnectServer(_ => null, _ => slow, () => Code, loopbackOnly: true, port: port);
            try
            {
                server.Start();
                if (!await WaitForListener(port)) { Check("the hang-up server listens", false); return; }
                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, port);
                    var s = client.GetStream();
                    s.Write(Encoding.ASCII.GetBytes($"GET /api/file?path=C%3A%5Cx.flac HTTP/1.1\r\nHost: x\r\nX-Connect-Code: {Code}\r\nRange: bytes=100-\r\n\r\n"));
                    ReadResponse(s, head: true);
                    await Task.Delay(300);
                }
                var cancelled = await Task.WhenAny(slow.Cancelled.Task, Task.Delay(3000)) == slow.Cancelled.Task;
                Check("a client hanging up mid-body cancels the read within a second or so", cancelled);
            }
            finally { server.Dispose(); }
        }

        /// <summary>
        /// The real <see cref="ConnectFiles"/> with no Drive, on scratch folders in %TEMP%: the local half of every
        /// action, through the application's own engines. Delete is left out: it goes to the real Recycle Bin.
        /// </summary>
        private static async Task LocalFilesTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "en-connect-files-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var files = new ConnectFiles(null, new Settings());
            try
            {
                string a = Path.Combine(root, "a"), b = Path.Combine(root, "b");
                Equal("mkdir makes the folder", a, await files.CreateFolderAsync(root, "a", CancellationToken.None));
                await files.CreateFolderAsync(root, "b", CancellationToken.None);
                int status = 0;
                try { await files.CreateFolderAsync(root, "a", CancellationToken.None); } catch (ConnectException e) { status = e.Status; }
                Equal("mkdir onto a folder there is 409", "409", status.ToString());

                System.IO.File.WriteAllText(Path.Combine(a, "one.txt"), "one");
                System.IO.File.WriteAllBytes(Path.Combine(a, "two.bin"), new byte[1000]);
                Directory.CreateDirectory(Path.Combine(a, "inner"));
                System.IO.File.WriteAllText(Path.Combine(a, "inner", "three.txt"), "three");

                var size = await files.SizeAsync(a, CancellationToken.None);
                Check("size walks the folder", size.Bytes == 1008 && size.Files == 3 && size.Folders == 1 && size.Complete, size.ToString());
                status = 0;
                try { await files.SizeAsync(Settings.AppDataDir, CancellationToken.None); } catch (ConnectException e) { status = e.Status; }
                Equal("our own AppData folder, which holds the sync root, is never walked", "403", status.ToString());

                var stat = await files.StatAsync(Path.Combine(a, "two.bin"), CancellationToken.None);
                Check("stat of a file", !stat.Folder && stat.Size == 1000 && stat.Name == "two.bin" && !stat.OnDrive && stat.Tags == null, stat.ToString());

                var renamed = await files.RenameAsync(Path.Combine(a, "one.txt"), "uno.txt", CancellationToken.None);
                Check("rename renames", renamed == Path.Combine(a, "uno.txt") && System.IO.File.Exists(renamed));
                status = 0;
                try { await files.RenameAsync(renamed, "two.bin", CancellationToken.None); } catch (ConnectException e) { status = e.Status; }
                Equal("rename onto a name there is 409", "409", status.ToString());
                status = 0;
                try { await files.RenameAsync(Path.Combine(a, "nope.txt"), "x.txt", CancellationToken.None); } catch (ConnectException e) { status = e.Status; }
                Equal("rename of something gone is 404", "404", status.ToString());

                var up = await files.UploadAsync(b, "uno.txt", PasteConflictPolicy.AutoRename, new MemoryStream(Encoding.UTF8.GetBytes("up")), CancellationToken.None);
                var again = await files.UploadAsync(b, "uno.txt", PasteConflictPolicy.AutoRename, new MemoryStream(Encoding.UTF8.GetBytes("again")), CancellationToken.None);
                Check("upload writes, and keeps both under rename", System.IO.File.ReadAllText(up) == "up" && again == Path.Combine(b, "uno (2).txt") && System.IO.File.ReadAllText(again) == "again",
                    again);
                await files.UploadAsync(b, "uno.txt", PasteConflictPolicy.Overwrite, new MemoryStream(Encoding.UTF8.GetBytes("over")), CancellationToken.None);
                var skipped = await files.UploadAsync(b, "uno.txt", PasteConflictPolicy.Skip, new MemoryStream(Encoding.UTF8.GetBytes("skip")), CancellationToken.None);
                Check("overwrite replaces and skip leaves it", System.IO.File.ReadAllText(skipped) == "over");
                Check("no partial file is left behind", !Directory.EnumerateFiles(b, "*.partial", new EnumerationOptions { AttributesToSkip = 0 }).Any());

                var copy = new ConnectJob("c1", "copy");
                await files.TransferAsync(copy, new[] { Path.Combine(a, "inner"), Path.Combine(a, "two.bin") }, b, false, PasteConflictPolicy.AutoRename);
                Check("copy through robocopy puts the folder and the file there, and leaves the originals",
                    System.IO.File.Exists(Path.Combine(b, "inner", "three.txt")) && System.IO.File.Exists(Path.Combine(b, "two.bin")) && System.IO.File.Exists(Path.Combine(a, "two.bin")) &&
                    copy.Failed.Count == 0, string.Join("; ", copy.Failed.Select(f => f.Error)));

                var move = new ConnectJob("m1", "move");
                await files.TransferAsync(move, new[] { Path.Combine(a, "two.bin") }, b, true, PasteConflictPolicy.AutoRename);
                Check("move under keep both numbers it and takes the original away",
                    System.IO.File.Exists(Path.Combine(b, "two (2).bin")) && !System.IO.File.Exists(Path.Combine(a, "two.bin")), string.Join("; ", move.Failed.Select(f => f.Error)));

                status = 0;
                try { await files.TransferAsync(new ConnectJob("x", "copy"), new[] { Path.Combine(a, "inner") }, Path.Combine(root, "missing"), false, PasteConflictPolicy.AutoRename); }
                catch (ConnectException e) { status = e.Status; }
                Equal("a destination that is not there is 404", "404", status.ToString());

                var f = ConnectFiles.FailureFor("two.bin: it would not go", new[] { Path.Combine(a, "two.bin") });
                Check("an engine's error is filed against the source it names", f.Path == Path.Combine(a, "two.bin") && f.Error == "It would not go.", $"{f.Path} {f.Error}");
                Check("a share has no Recycle Bin, so nothing there is deleted from the phone", !ConnectFiles.HasRecycleBin(@"\\server\share\x"));
                Check("the system drive has one", ConnectFiles.HasRecycleBin(Path.GetTempPath()));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static async Task RangeCase(HttpClient http, string path, byte[] data, RangeHeaderValue range, int from, int to)
        {
            var r = await Send(http, HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(path), range: range);
            var body = await r.Content.ReadAsByteArrayAsync();
            var want = data[from..(to + 1)];
            Check($"{range} is 206 with exactly bytes {from} to {to}",
                (int)r.StatusCode == 206 && body.SequenceEqual(want), $"{(int)r.StatusCode}, {body.Length} bytes");
            Equal($"{range} names its range", $"bytes {from}-{to}/{data.Length}", r.Content.Headers.ContentRange?.ToString() ?? "");
        }

        /// <summary>
        /// Several requests down one connection, by hand, because HttpClient hides whether it reused one. A HEAD
        /// goes first: a body sent after its headers would be read here as the start of the next response.
        /// </summary>
        private static void KeepAliveTests(int port, string bin, byte[] data)
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            client.ReceiveTimeout = 10_000;
            using var s = client.GetStream();
            string target = "/api/file?path=" + Uri.EscapeDataString(bin);

            void Ask(string method, string url, string extra = "") =>
                s.Write(Encoding.ASCII.GetBytes($"{method} {url} HTTP/1.1\r\nHost: x\r\nX-Connect-Code: {Code}\r\n{extra}\r\n"));

            Ask("HEAD", "/api/info");
            var (status, headers, _) = ReadResponse(s, head: true);
            Check("HEAD on JSON gives headers only", status == 200 && headers.ContainsKey("content-length"), status.ToString());

            Ask("GET", target, "Range: bytes=0-3\r\n");
            (status, headers, var body) = ReadResponse(s, head: false);
            Check("the next request on the same connection is answered cleanly",
                status == 206 && body.SequenceEqual(data[..4]), $"{status}, {body.Length} bytes");

            Ask("HEAD", target);
            (status, headers, _) = ReadResponse(s, head: true);
            Equal("HEAD on a file gives its length", "300", headers.GetValueOrDefault("content-length") ?? "");

            Ask("GET", target, "Connection: close\r\n");
            (status, _, body) = ReadResponse(s, head: false);
            Check("and a third after it", status == 200 && body.SequenceEqual(data), $"{status}, {body.Length} bytes");
            Check("Connection: close closes it", s.Read(new byte[1], 0, 1) == 0);
        }

        private static (int Status, Dictionary<string, string> Headers, byte[] Body) ReadResponse(Stream s, bool head)
        {
            var raw = new List<byte>();
            while (raw.Count < 4 || !(raw[^4] == '\r' && raw[^3] == '\n' && raw[^2] == '\r' && raw[^1] == '\n'))
            {
                int b = s.ReadByte();
                if (b < 0) throw new IOException("the connection closed mid-head");
                raw.Add((byte)b);
            }
            var lines = Encoding.ASCII.GetString(raw.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            int status = int.Parse(lines[0].Split(' ')[1]);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
            var body = new byte[head ? 0 : int.Parse(headers.GetValueOrDefault("content-length") ?? "0")];
            int got = 0;
            while (got < body.Length)
            {
                int n = s.Read(body, got, body.Length - got);
                if (n <= 0) throw new IOException("the connection closed mid-body");
                got += n;
            }
            return (status, headers, body);
        }

        private static Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, string url,
            string code = Code, RangeHeaderValue? range = null)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add("X-Connect-Code", code);
            if (range != null) request.Headers.Range = range;
            return http.SendAsync(request);
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        /// <summary>The listener is bound on a worker, so give it a moment to be there.</summary>
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
