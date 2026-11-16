using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Sockets;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace ExplorerNative
{
    /// <summary>
    /// Explorer Native Connect: the HTTP server the iPhone app talks to, driven over a real socket.
    ///
    /// Loopback only, on a port nobody else holds, against a folder of temporary files, never a real drive,
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
            await DetailsTests();
            await WebAppTests();
            await Round2Tests();
            await Round3Tests();
            await ServePeerTests();
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

            Check("an empty Serve config is off", TailscaleWeb.ParseServe("{}", 47810) == TailscaleWeb.ServeState.Off);
            Check("nothing at all is off", TailscaleWeb.ParseServe("", 47810) == TailscaleWeb.ServeState.Off);
            Check("our own handler is ours", TailscaleWeb.ParseServe("""
                { "TCP": { "443": { "HTTPS": true } },
                  "Web": { "laptop.tail3d7403.ts.net:443": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:47810" } } } } }
                """, 47810) == TailscaleWeb.ServeState.Ours);
            Check("somebody else's proxy on 443 is theirs", TailscaleWeb.ParseServe("""
                { "TCP": { "443": { "HTTPS": true } },
                  "Web": { "laptop.tail3d7403.ts.net:443": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:3000" } } } } }
                """, 47810) == TailscaleWeb.ServeState.TakenByOther);
            Check("ours plus another path on 443 is left alone", TailscaleWeb.ParseServe("""
                { "Web": { "x.ts.net:443": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:47810" }, "/grafana": { "Proxy": "http://127.0.0.1:3000" } } } } }
                """, 47810) == TailscaleWeb.ServeState.TakenByOther);
            Check("a raw TCP forward on 443 is theirs", TailscaleWeb.ParseServe("""
                { "TCP": { "443": { "TCPForward": "127.0.0.1:22" } } }
                """, 47810) == TailscaleWeb.ServeState.TakenByOther);
            Check("Serve on another port does not block 443", TailscaleWeb.ParseServe("""
                { "TCP": { "8443": { "HTTPS": true } },
                  "Web": { "x.ts.net:8443": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:3000" } } } } }
                """, 47810) == TailscaleWeb.ServeState.Off);
            const string ours47810 = """
                { "TCP": { "443": { "HTTPS": true } },
                  "Web": { "x.ts.net:443": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:47810" } } } } }
                """;
            Check("our handler on the port used before is ours to move",
                TailscaleWeb.ParseServe(ours47810, 50123, 47810) == TailscaleWeb.ServeState.OursOtherPort);
            Check("but not when that port was never ours",
                TailscaleWeb.ParseServe(ours47810, 50123, 50000) == TailscaleWeb.ServeState.TakenByOther);
            Equal("the handler points at the port asked for", "http://127.0.0.1:50123", TailscaleWeb.Target(50123));

            Equal("the enable-HTTPS link is found in Tailscale's message",
                "https://login.tailscale.com/f/serve?node=nABC123",
                TailscaleWeb.EnableLink("Serve is not enabled on your tailnet.\nTo enable, visit:\n\n         https://login.tailscale.com/f/serve?node=nABC123\n") ?? "");

            var loop = IPAddress.Loopback;
            Check("Serve's header for the PC's owner is trusted from loopback",
                ConnectServer.TrustsServeUser(loop, "me@example.com", "me@example.com"));
            Check("and matched without regard to case",
                ConnectServer.TrustsServeUser(loop, "Me@Example.com", "me@example.com"));
            Check("another tailnet user is not", !ConnectServer.TrustsServeUser(loop, "friend@example.com", "me@example.com"));
            Check("the header is only believed from this machine, the only place a connection can come from",
                !ConnectServer.TrustsServeUser(IPAddress.Parse("100.100.1.1"), "me@example.com", "me@example.com"));
            Check("loopback without the header is not trusted", !ConnectServer.TrustsServeUser(loop, null, "me@example.com"));
            Check("nothing is trusted before the PC's login is known", !ConnectServer.TrustsServeUser(loop, "me@example.com", null));
            Check("an IPv4-mapped loopback counts as loopback",
                ConnectServer.TrustsServeUser(IPAddress.Parse("::ffff:127.0.0.1"), "me@example.com", "me@example.com"));

            int port = FreePort();
            var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port,
                files: new FakeFiles(), tailscaleStatus: () => status);
            server.ServePeerOverride = _ => true; // as if Tailscale Serve made every connection
            try
            {
                server.Start();
                if (!await WaitForListener(port)) { Check("the web app server listens", false); return; }
                await server.LoginSettled();
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

                // The shell is cached by tag: index.html names app.js and app.css by their tags, which the phone
                // keeps for good; everything else is asked again and answered with 304 when it has not changed.
                async Task<HttpResponseMessage> Raw(string url, string? accept = null, string? ifNoneMatch = null, string? login = null)
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (accept != null) request.Headers.TryAddWithoutValidation("Accept-Encoding", accept);
                    if (ifNoneMatch != null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
                    if (login != null) request.Headers.Add("Tailscale-User-Login", login);
                    return await http.SendAsync(request);
                }
                var jsTag = WebAssets.TagOf("app.js");
                Check("the page names its script and style sheet by their tags",
                    jsTag.Length == 16 && html.Contains($"\"/app/app.js?v={jsTag}\"") && html.Contains($"\"/app/app.css?v={WebAssets.TagOf("app.css")}\""), jsTag);
                using (var versioned = await Raw($"/app/app.js?v={jsTag}"))
                    Check("the script under its tag is kept for good", versioned.Headers.CacheControl?.MaxAge == TimeSpan.FromDays(365) &&
                        versioned.Headers.CacheControl.Extensions.Any(e => e.Name == "immutable"), versioned.Headers.CacheControl?.ToString());
                using (var stale = await Raw("/app/app.js?v=0000000000000000"))
                    Check("but not under a tag that is not its own", stale.Headers.CacheControl?.NoCache == true, stale.Headers.CacheControl?.ToString());
                using (var page = await Raw("/"))
                {
                    var tag = page.Headers.ETag?.Tag ?? "";
                    Check("the page is asked again every time, with a tag", page.Headers.CacheControl?.NoCache == true && tag.Length > 2, tag);
                    using var again = await Raw("/", ifNoneMatch: tag);
                    var body = await again.Content.ReadAsByteArrayAsync();
                    Check("and the same page is a 304 with no body", again.StatusCode == HttpStatusCode.NotModified && body.Length == 0, $"{again.StatusCode} {body.Length}");
                    Check("which still carries the security headers", again.Headers.Contains("Content-Security-Policy") || again.Content.Headers.Contains("Content-Security-Policy") ||
                        again.Headers.TryGetValues("X-Content-Type-Options", out _), again.Headers.ToString());
                    using var other = await Raw("/", ifNoneMatch: "\"something-else\"");
                    Check("a different tag gets the page", other.StatusCode == HttpStatusCode.OK, other.StatusCode.ToString());
                }
                using (var br = await Raw("/app/app.js", accept: "gzip, deflate, br"))
                {
                    var packed = await br.Content.ReadAsByteArrayAsync();
                    WebAssets.TryGet("/app/app.js", out var plainJs, out _);
                    using var unpack = new System.IO.Compression.BrotliStream(new MemoryStream(packed), System.IO.Compression.CompressionMode.Decompress);
                    using var into = new MemoryStream();
                    unpack.CopyTo(into);
                    Check("the script goes compressed to a browser that takes brotli, and unpacks to itself",
                        br.Content.Headers.ContentEncoding.Contains("br") && packed.Length < plainJs.Length / 2 && into.ToArray().AsSpan().SequenceEqual(plainJs),
                        $"{packed.Length} of {plainJs.Length}");
                }
                using (var gz = await Raw("/app/app.css", accept: "gzip"))
                    Check("and as gzip to one that takes only that", gz.Content.Headers.ContentEncoding.Contains("gzip"), string.Join(",", gz.Content.Headers.ContentEncoding));
                using (var none = await Raw("/app/app.css", accept: "br;q=0, gzip;q=0"))
                    Check("a coding refused with q=0 is not used", none.Content.Headers.ContentEncoding.Count == 0, string.Join(",", none.Content.Headers.ContentEncoding));
                using (var icon = await Raw("/app/icon-192.png", accept: "br"))
                    Check("an image is not compressed again", icon.Content.Headers.ContentEncoding.Count == 0);

                // JSON worth it goes compressed too; small answers and clients that did not ask go as they are.
                var bigFolder = Path.Combine(Path.GetTempPath(), $"en-compress-{Guid.NewGuid():N}");
                Directory.CreateDirectory(bigFolder);
                try
                {
                    for (int i = 0; i < 200; i++) File.WriteAllBytes(Path.Combine(bigFolder, $"track {i:000} with a longer name.flac"), Array.Empty<byte>());
                    var listUrl = "/api/list?path=" + Uri.EscapeDataString(bigFolder);
                    using var plain = await Raw(listUrl, login: "me@example.com");
                    var plainBody = await plain.Content.ReadAsStringAsync();
                    using var packedList = await Raw(listUrl, accept: "br", login: "me@example.com");
                    var packedBytes = await packedList.Content.ReadAsByteArrayAsync();
                    using var unpack = new System.IO.Compression.BrotliStream(new MemoryStream(packedBytes), System.IO.Compression.CompressionMode.Decompress);
                    using var reader = new StreamReader(unpack);
                    var unpacked = reader.ReadToEnd();
                    Check("a big listing goes compressed, and is the same listing",
                        packedList.Content.Headers.ContentEncoding.Contains("br") && unpacked == plainBody && packedBytes.Length * 5 < plainBody.Length,
                        $"{packedBytes.Length} of {plainBody.Length}");
                    Check("a client that did not ask gets plain JSON", plain.Content.Headers.ContentEncoding.Count == 0 && plainBody.StartsWith("["));
                    using var gzList = await Raw(listUrl, accept: "gzip", login: "me@example.com");
                    using var gunzip = new System.IO.Compression.GZipStream(await gzList.Content.ReadAsStreamAsync(), System.IO.Compression.CompressionMode.Decompress);
                    using var gzReader = new StreamReader(gunzip);
                    Check("and gzip when that is all it takes", gzList.Content.Headers.ContentEncoding.Contains("gzip") && gzReader.ReadToEnd() == plainBody);
                    using var small = await Raw("/api/info", accept: "br", login: "me@example.com");
                    Check("a small answer is not compressed", small.Content.Headers.ContentEncoding.Count == 0);
                }
                finally { try { Directory.Delete(bigFolder, true); } catch { } }

                Equal("br is preferred to gzip", "br", WebAssets.PickEncoding("gzip, deflate, br"));
                Equal("gzip when br is refused", "gzip", WebAssets.PickEncoding("br;q=0, gzip"));
                Equal("nothing for identity", "", WebAssets.PickEncoding("identity"));
                Equal("nothing for a bare star", "", WebAssets.PickEncoding("*"));
                Check("a weak tag matches its strong form", ConnectServer.TagMatches("W/\"abc\", \"def\"", "\"abc\"") && ConnectServer.TagMatches("*", "\"x\"") && !ConnectServer.TagMatches("\"abc\"", "\"abd\""));

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
                    var (code, body) = await CallAt(http, HttpMethod.Get, "/api/details?path=" + Uri.EscapeDataString(mkv));
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
            // The real class's bookkeeping, fed by hand: seq on every change, and only the current item kept.
            var real = new ConnectClipboard();
            for (int i = 0; i < 60; i++) real.Observe((new ClipState(0, "text", Text: "item " + i), null));
            real.Observe((new ClipState(0, "empty"), null));
            Check("seq goes up on every change, empty included", real.Current.Seq == 61, real.Current.Seq.ToString());
            Check("an emptied clipboard reads as empty", real.Current.Kind == "empty" && real.Current.Text == null);
            real.Observe((new ClipState(0, "text", Text: new string('x', 2_000_000)), null));
            Check("the current text is kept whole", real.Current.Text!.Length == 2_000_000);
            Check("and nothing else is kept: no history in the class",
                typeof(ConnectClipboard).GetMethod("History") == null &&
                typeof(ConnectClipboard).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .All(f => !f.Name.Contains("history", StringComparison.OrdinalIgnoreCase)));

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

                fake.Real.Observe((new ClipState(0, "text", Text: "copied on the PC"), null));
                (status, body) = await CallAt(http, HttpMethod.Get, "/api/clipboard");
                Check("GET clipboard shows what was copied on the PC", status == 200 && Prop(body, "text") == "copied on the PC", body.ToString());
                fake.Real.Observe((new ClipState(0, "empty"), null));
                (status, body) = await CallAt(http, HttpMethod.Get, "/api/clipboard");
                Check("and an empty clipboard as empty", status == 200 && Prop(body, "kind") == "empty", body.ToString());

                (status, _) = await CallAt(http, HttpMethod.Get, "/api/clipboard/image");
                Equal("no image is 404", "404", status.ToString());
                var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
                (status, body) = await CallAt(http, HttpMethod.Post, "/api/clipboard/image", content: new ByteArrayContent(png));
                Check("POST image hands the bytes over", status == 200 && fake.LastImage?.SequenceEqual(png) == true, $"{status} {body}");
                var r = await Send(http, HttpMethod.Get, "/api/clipboard/image");
                var got = await r.Content.ReadAsByteArrayAsync();
                Check("GET image is the PNG", (int)r.StatusCode == 200 && r.Content.Headers.ContentType?.MediaType == "image/png" && got.SequenceEqual(png));
                Check("and never sniffed", Header(r, "X-Content-Type-Options") == "nosniff");
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
                (status, body) = await CallAt(http, HttpMethod.Post, "/api/clipboard/send?name=empty.txt", content: new ByteArrayContent(Array.Empty<byte>()));
                var emptySent = Prop(body, "path");
                Check("an empty file can be sent too, and is put on as an empty file",
                    status == 200 && System.IO.File.Exists(emptySent) && new FileInfo(emptySent).Length == 0 && fake.FilesSet.Last().SequenceEqual(new[] { emptySent }),
                    $"{status} {body}");

                // Gone with the old phone app: no history, no long poll, no ping, no stat.
                foreach (var gone in new[] { "/api/clipboard/history", "/api/clipboard/wait?since=0", "/api/ping", "/api/stat?path=C%3A%5C" })
                {
                    (status, body) = await CallAt(http, HttpMethod.Get, gone);
                    Check($"{gone.Split('?')[0]} is gone, with a sentence", status == 404 && Prop(body, "error").Length > 5, $"{status} {body}");
                }

                foreach (var dir in Directory.GetDirectories(sends)) Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-8));
                var fresh = Directory.CreateDirectory(Path.Combine(sends, "fresh"));
                Check("sent files are swept after seven days, and newer ones kept",
                    server.SweepClipboardSends(ConnectServer.ClipboardSendsKeptFor) == 3 && Directory.GetDirectories(sends).Single() == fresh.FullName);
            }
            finally
            {
                server.Dispose();
                try { Directory.Delete(sends, true); } catch { }
            }
        }

        private static IPAddress? NetworkInterfaceAddress()
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                            return ua.Address;
                }
            }
            catch { }
            return null;
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
                    s.Write(Encoding.ASCII.GetBytes($"PUT /api/upload/chunk?id={id}&offset=300 HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Connect-Code: {Code}\r\nContent-Length: 400\r\n\r\n"));
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
                var finishedPath = Prop(body, "path");
                (status, body) = await CallAt(http, HttpMethod.Post, "/api/upload/finish", new { id });
                Check("finishing again, because the answer was lost, gives the same answer and places nothing twice",
                    status == 200 && Prop(body, "path") == finishedPath, $"{status} {body}");

                // A chunk whose body stops arriving: the upload is held only until it is given up on, what
                // landed is kept, and the stalled connection is closed.
                server.UploadStallAfter = TimeSpan.FromSeconds(1);
                (_, body) = await CallAt(http, HttpMethod.Post, "/api/upload/start", new { folder = @"C:\b", name = "stall.bin", size = 400 });
                var stallId = Prop(body, "id");
                using (var stalled = new TcpClient())
                {
                    stalled.Connect(IPAddress.Loopback, port);
                    var ss = stalled.GetStream();
                    ss.Write(Encoding.ASCII.GetBytes($"PUT /api/upload/chunk?id={stallId}&offset=0 HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Connect-Code: {Code}\r\nContent-Length: 400\r\n\r\n"));
                    ss.Write(payload, 0, 100);
                    ss.Flush();
                    await Task.Delay(200);
                    (status, body) = await CallAt(http, HttpMethod.Put, $"/api/upload/chunk?id={stallId}&offset=100", content: new ByteArrayContent(payload[100..400]));
                    Check("while the stalled chunk holds it, another is 409, still arriving", status == 409, $"{status} {body}");
                    await Task.Delay(1500);
                    (status, body) = await CallAt(http, HttpMethod.Put, $"/api/upload/chunk?id={stallId}&offset=100", content: new ByteArrayContent(payload[100..400]));
                    Check("once the stall is given up on, the upload carries on from what landed",
                        status == 200 && Prop(body, "received") == "400", $"{status} {body}");
                    stalled.ReceiveTimeout = 5000;
                    int closed;
                    try { closed = ss.Read(new byte[64], 0, 64); } catch (IOException) { closed = 0; }
                    Check("and the stalled connection was closed", closed == 0, closed.ToString());
                }
                server.UploadStallAfter = TimeSpan.FromSeconds(30);
                await CallAt(http, HttpMethod.Post, "/api/upload/cancel", new { id = stallId });

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

                    r = await Send(http, HttpMethod.Get, url, range: new RangeHeaderValue(0, 99));
                    Check("/api/audio is sandboxed and never sniffed, like /api/file",
                        Header(r, "Content-Security-Policy").StartsWith("sandbox;") && Header(r, "X-Content-Type-Options") == "nosniff",
                        $"[{Header(r, "Content-Security-Policy")}] [{Header(r, "X-Content-Type-Options")}]");

                    var dotted = Path.Combine(dir, "album.");
                    Directory.CreateDirectory(@"\\?\" + dotted);
                    System.IO.File.Copy(aiff, @"\\?\" + Path.Combine(dotted, "sweep.aiff"));
                    r = await Send(http, HttpMethod.Head, "/api/audio?path=" + Uri.EscapeDataString(Path.Combine(dotted, "sweep.aiff")));
                    Check("a track in a folder whose name ends in a dot plays", (int)r.StatusCode == 200 && r.Content.Headers.ContentLength == total,
                        $"{(int)r.StatusCode} {r.Content.Headers.ContentLength} {await r.Content.ReadAsStringAsync()}");
                    if (System.IO.File.Exists(opus))
                    {
                        System.IO.File.Copy(opus, @"\\?\" + Path.Combine(dotted, "sweep.opus"));
                        r = await Send(http, HttpMethod.Get, "/api/audio?path=" + Uri.EscapeDataString(Path.Combine(dotted, "sweep.opus")), range: new RangeHeaderValue(0, 999));
                        var said = (int)r.StatusCode == 206 ? "" : await r.Content.ReadAsStringAsync();
                        Check("and so does one read as bytes rather than by name", (int)r.StatusCode == 206, $"{(int)r.StatusCode} {said}");
                    }
                }
                finally { server.Dispose(); }

                Check("a length nobody knows is estimated generously: a minute at least",
                    ConnectAudio.EstimateFrames(1000, 48000) == 60 * 48000 && ConnectAudio.EstimateFrames(80_000_000, 44100) == 10_000L * 44100);
            }
            finally
            {
                try { Directory.Delete(@"\\?\" + dir, true); } catch { }
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

        /// <summary>
        /// The server listens on loopback only, on the port asked for, and a port is
        /// checked before it is used: range, in use by another program, ours already.
        /// </summary>
        private static void TailscaleTests()
        {
            Equal("too low a port is refused", $"Choose a port from {ConnectServer.LowestPort} to {ConnectServer.HighestPort}.",
                ConnectServer.CheckPort(80, 47810) ?? "");
            Check("too high a port is refused", ConnectServer.CheckPort(70000, 47810) != null);
            Check("the port in use now is fine", ConnectServer.CheckPort(47810, 47810) == null);

            var taken = new TcpListener(IPAddress.Loopback, 0);
            taken.Start();
            int busy = ((IPEndPoint)taken.LocalEndpoint).Port;
            try
            {
                var refusal = ConnectServer.CheckPort(busy, 47810) ?? "";
                Check("a port another program listens on is refused in plain words",
                    refusal == $"Port {busy} is already in use by another program.", refusal);
                Check("unless it is the one already ours", ConnectServer.CheckPort(busy, busy) == null);

                using var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: busy);
                server.Start();
                Check("starting on a taken port says why", server.ListenProblem == $"Port {busy} is already in use by another program.",
                    server.ListenProblem ?? "(none)");
                int free = FreePort();
                Check("and moving to a free one works", server.Rebind(free) && server.ListenPort == free && server.ListenProblem == null);
                Check("moving onto a taken one keeps the port it had", !server.Rebind(busy) && server.ListenPort == free);
                using (var c = new TcpClient())
                {
                    c.Connect(IPAddress.Loopback, free);
                    Check("it answers on loopback at the new port", c.Connected);
                }
                var outside = NetworkInterfaceAddress();
                if (outside != null)
                {
                    bool reached;
                    try { using var c = new TcpClient(); c.Connect(outside, free); reached = true; }
                    catch (SocketException) { reached = false; }
                    Check($"and nothing reaches it from this PC's network address ({outside})", !reached);
                }
            }
            finally { taken.Stop(); }

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

                // A file is somebody's bytes on the web app's own origin. An HTML page opened from the PC ran as
                // the app with the owner's access, and a .js could be loaded as one of its scripts. Every kind a
                // browser would run comes back sandboxed, never sniffed, as plain text, to be downloaded.
                foreach (var ext in ConnectServer.ActiveExtensions)
                {
                    var page = Path.Combine(dir, "probe" + ext);
                    System.IO.File.WriteAllText(page, "<html><script>fetch('/api/list?path=C%3A%5C')</script></html>");
                    r = await Send(http, HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(page));
                    var csp = Header(r, "Content-Security-Policy");
                    Check($"{ext} comes back sandboxed, nosniff, as an attachment in plain text",
                        (int)r.StatusCode == 200 && csp.StartsWith("sandbox;") && csp.Contains("default-src 'none'") && !csp.Contains("allow-") &&
                        Header(r, "X-Content-Type-Options") == "nosniff" && r.Content.Headers.ContentDisposition?.DispositionType == "attachment" &&
                        r.Content.Headers.ContentType?.MediaType == "text/plain",
                        $"{(int)r.StatusCode} [{csp}] [{Header(r, "X-Content-Type-Options")}] [{r.Content.Headers.ContentDisposition}] [{r.Content.Headers.ContentType}]");
                    r = await Send(http, HttpMethod.Head, "/api/file?path=" + Uri.EscapeDataString(page), range: null);
                    Check($"{ext}: a HEAD says the same", Header(r, "Content-Security-Policy").StartsWith("sandbox;") &&
                        r.Content.Headers.ContentDisposition?.DispositionType == "attachment");
                }
                foreach (var ext in new[] { ".htm", ".svg", ".js" })
                {
                    var page = Path.Combine(dir, "ranged" + ext);
                    System.IO.File.WriteAllText(page, "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");
                    r = await Send(http, HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(page), range: new RangeHeaderValue(0, 9));
                    Check($"{ext}: a range of it is sandboxed and an attachment too", (int)r.StatusCode == 206 &&
                        Header(r, "Content-Security-Policy").StartsWith("sandbox;") && Header(r, "X-Content-Type-Options") == "nosniff" &&
                        r.Content.Headers.ContentDisposition?.DispositionType == "attachment" && r.Content.Headers.ContentType?.MediaType == "text/plain");
                }
                r = await Send(http, HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(odd));
                Check("anything else is sandboxed and nosniff too, shown in place, under its own name",
                    Header(r, "Content-Security-Policy").StartsWith("sandbox;") && Header(r, "X-Content-Type-Options") == "nosniff" &&
                    r.Content.Headers.ContentDisposition?.DispositionType == "inline" && r.Content.Headers.ContentDisposition?.FileNameStar == "a+b 日本.txt",
                    r.Content.Headers.ContentDisposition?.ToString());
                r = await Send(http, HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(bin));
                Check("an unknown kind is bytes, never guessed at", r.Content.Headers.ContentType?.MediaType == "application/octet-stream" &&
                    Header(r, "X-Content-Type-Options") == "nosniff");
                r = await Send(http, HttpMethod.Get, "/api/list?path=" + Uri.EscapeDataString(dir));
                Equal("and JSON is never sniffed either", "nosniff", Header(r, "X-Content-Type-Options"));

                // Names ending in a dot or a space, which Windows trims from an ordinary path: listed, and opened.
                var dotted = Path.Combine(dir, "deep.");
                Directory.CreateDirectory(@"\\?\" + dotted);
                System.IO.File.WriteAllText(@"\\?\" + Path.Combine(dotted, "end."), "the dotted one");
                r = await Send(http, HttpMethod.Get, "/api/list?path=" + Uri.EscapeDataString(dotted));
                var dottedList = await r.Content.ReadAsStringAsync();
                Check("a folder whose name ends in a dot lists", (int)r.StatusCode == 200 && dottedList.Contains("\"end.\""), $"{(int)r.StatusCode} {dottedList}");
                r = await Send(http, HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(Path.Combine(dotted, "end.")));
                Equal("and a file in it whose name ends in a dot downloads", "the dotted one", await r.Content.ReadAsStringAsync());

                // A file another program holds open with no sharing says so, not that it is missing.
                var locked = Path.Combine(dir, "locked.bin");
                System.IO.File.WriteAllBytes(locked, data);
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    r = await Send(http, HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(locked));
                    var said = await r.Content.ReadAsStringAsync();
                    Check("a file in use is 423 with a sentence saying so", (int)r.StatusCode == 423 && said.Contains("in use"), $"{(int)r.StatusCode} {said}");
                }

                KeepAliveTests(port, bin, data);
            }
            finally
            {
                server.Dispose();
                // Literally, or the folder ending in a dot stops the delete.
                try { Directory.Delete(@"\\?\" + dir, true); } catch { }
            }
        }

        private static string Header(HttpResponseMessage r, string name) =>
            r.Headers.TryGetValues(name, out var v) ? string.Join(",", v)
            : r.Content.Headers.TryGetValues(name, out var c) ? string.Join(",", c) : "";

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

            public int Uploads;

            /// <summary>How long an upload takes, so requests can arrive while one is being answered.</summary>
            public int UploadDelayMs;

            public async Task<string> UploadAsync(string folder, string name, PasteConflictPolicy conflict, Stream body, CancellationToken token)
            {
                Interlocked.Increment(ref Uploads);
                if (UploadDelayMs > 0) await Task.Delay(UploadDelayMs, token);
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
                (status, body) = await Call(HttpMethod.Get, "/api/details?path=" + Uri.EscapeDataString(@"C:\a\song.flac"));
                Check("stat of a track carries its tags as values",
                    status == 200 && body.TryGetProperty("tags", out var tags) && S(tags, "title") == "Song" && S(tags, "durationSeconds") == "181.5" && S(tags, "year") == "2020",
                    body.ToString());
                (status, body) = await Call(HttpMethod.Get, "/api/details?path=" + Uri.EscapeDataString(@"C:\a\folder"));
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
                fake.LastUpload = null;
                (status, body) = await Call(HttpMethod.Post, "/api/upload?folder=C%3A%5Cb&name=d.txt", content: new ByteArrayContent(Array.Empty<byte>()));
                Check("an empty file uploads, as an empty file", status == 200 && fake.LastUpload == "" && S(body, "path") == @"C:\b\d.txt",
                    $"{status} {body} [{fake.LastUpload}]");

                // The answer to an upload can be lost after the file arrived; the web app sends it again with the
                // same id, and gets the same answer rather than a second copy.
                int uploadsBefore = fake.Uploads;
                for (int i = 0; i < 2; i++)
                    (status, body) = await Call(HttpMethod.Post, "/api/upload?folder=C%3A%5Cb&name=again.txt&uploadId=abc123",
                        content: new ByteArrayContent(Encoding.UTF8.GetBytes("once")));
                Check("an upload sent twice under one id is written once and answered the same both times",
                    status == 200 && fake.Uploads == uploadsBefore + 1 && S(body, "path") == @"C:\b\again.txt", $"{status} {body} {fake.Uploads - uploadsBefore}");
                await Call(HttpMethod.Post, "/api/upload?folder=C%3A%5Cb&name=again.txt&uploadId=def456", content: new ByteArrayContent(Encoding.UTF8.GetBytes("twice")));
                Equal("a different id is a different upload", (uploadsBefore + 2).ToString(), fake.Uploads.ToString());

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
            s.Write(Encoding.ASCII.GetBytes($"POST /api/upload?folder=C%3A%5Cb&name=e.txt&conflict=skip HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Connect-Code: {Code}\r\nContent-Length: {payload.Length}\r\n\r\n{payload}"));
            var (status, _, body) = ReadResponse(s, head: false);
            Check("a skipped upload answers without reading the body", status == 200 && Encoding.UTF8.GetString(body).Contains("e.txt"), status.ToString());
            s.Write(Encoding.ASCII.GetBytes($"GET /api/info HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Connect-Code: {Code}\r\n\r\n"));
            (status, _, body) = ReadResponse(s, head: false);
            Check("and the next request on the connection is read cleanly", status == 200 && Encoding.UTF8.GetString(body).Contains("apiVersion"), status.ToString());

            using (var closing = new TcpClient())
            {
                closing.Connect(IPAddress.Loopback, port);
                closing.ReceiveTimeout = 10_000;
                var c = closing.GetStream();
                c.Write(Encoding.ASCII.GetBytes($"POST /api/upload?folder=C%3A%5Cb&name=g.txt&conflict=skip HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Connect-Code: {Code}\r\nConnection: close\r\nContent-Length: {payload.Length}\r\n\r\n{payload}"));
                var (closedStatus, _, _) = ReadResponse(c, head: false);
                Check("an unread body on a closing connection is drained, so the answer is not lost to a reset", closedStatus == 200, closedStatus.ToString());
            }

            s.Write(Encoding.ASCII.GetBytes($"POST /api/upload?folder=C%3A%5Cb&name=f.txt HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Connect-Code: {Code}\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n"));
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

            Check("with nothing open, the sweep timer is not running", !pool.Sweeping);
            Check("a path that is not remote gets no lease", pool.Lease(@"C:\x.flac") == null);
            Check("and a path that is not remote does not start it", !pool.Sweeping);
            var a = pool.Lease(@"Q:\song.flac")!;
            var b = pool.Lease(@"q:\SONG.flac")!;
            Equal("two requests for one file share one download", "1", opened.ToString());
            Check("a stream held starts the sweep timer", pool.Sweeping);

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
            Check("and with the last one gone, the sweep timer stops", !pool.Sweeping);

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
                    s.Write(Encoding.ASCII.GetBytes($"GET /api/file?path=C%3A%5Cx.flac HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Connect-Code: {Code}\r\nRange: bytes=100-\r\n\r\n"));
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

                var busy = Path.Combine(a, "busy.txt");
                System.IO.File.WriteAllText(busy, "busy");
                using (new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Exception? caught = null;
                    try { await files.RenameAsync(busy, "free.txt", CancellationToken.None); } catch (Exception e) { caught = e; }
                    Check("renaming a file another program holds is told apart as in use, which the server answers 423",
                        caught != null && ConnectServer.InUse(caught), $"{caught?.GetType().Name}: {caught?.Message}");
                }

                // Moving into the folder it is already in is nothing to do, whatever the setting.
                foreach (var policy in new[] { PasteConflictPolicy.AutoRename, PasteConflictPolicy.Skip, PasteConflictPolicy.Overwrite })
                {
                    var stay = new ConnectJob("s-" + policy, "move");
                    await files.TransferAsync(stay, new[] { Path.Combine(b, "uno.txt"), Path.Combine(b, "inner") }, b + @"\", true, policy);
                    var snapshot = JsonSerializer.Serialize(stay.Snapshot());
                    Check($"moving into its own folder under {policy} leaves it alone and says it is already there",
                        System.IO.File.Exists(Path.Combine(b, "uno.txt")) && !System.IO.File.Exists(Path.Combine(b, "uno (3).txt")) &&
                        !Directory.Exists(Path.Combine(b, "inner (2)")) && stay.Failed.Count == 0 && snapshot.Contains("already in this folder"),
                        snapshot);
                }

                // Eight uploads of one name at once: eight files, no errors.
                var many = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
                    files.UploadAsync(b, "same.txt", PasteConflictPolicy.AutoRename, new MemoryStream(Encoding.UTF8.GetBytes("copy " + i)), CancellationToken.None))));
                Check("eight uploads of one name at once all land, each under its own name",
                    many.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 8 && many.All(System.IO.File.Exists) &&
                    Enumerable.Range(0, 8).All(i => many.Any(p => System.IO.File.ReadAllText(p) == "copy " + i)), string.Join(", ", many.Select(Path.GetFileName)));
                var placedTwice = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() =>
                {
                    var part = Path.Combine(root, $"part{i}.bin");
                    System.IO.File.WriteAllText(part, "placed " + i);
                    return files.PlaceFileAsync(part, b, "placed.txt", PasteConflictPolicy.AutoRename, null, CancellationToken.None);
                })));
                Check("and so do finished resumable uploads of one name", placedTwice.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4,
                    string.Join(", ", placedTwice.Select(Path.GetFileName)));

                var empty = await files.UploadAsync(b, "empty.txt", PasteConflictPolicy.AutoRename, new MemoryStream(), CancellationToken.None);
                Check("an empty file uploads as an empty file", System.IO.File.Exists(empty) && new FileInfo(empty).Length == 0, empty);

                // Names ending in a dot or a space: Windows trims them from an ordinary path, so each action has to
                // reach the literal one, or it acts on a different file or calls it missing.
                string odd = Path.Combine(root, "odd."), end = Path.Combine(root, "end.");
                Directory.CreateDirectory(@"\\?\" + odd);
                System.IO.File.WriteAllText(@"\\?\" + Path.Combine(odd, "in.txt"), "inside");
                System.IO.File.WriteAllText(@"\\?\" + end, "ends in a dot");
                var endStat = await files.StatAsync(end, CancellationToken.None);
                Check("stat of a name ending in a dot is that file, under its own path",
                    endStat.Name == "end." && endStat.Size == 13 && endStat.Path == end, endStat.ToString());
                var oddSize = await files.SizeAsync(odd, CancellationToken.None);
                Check("a folder ending in a dot is measured", oddSize.Files == 1 && oddSize.Bytes == 6, oddSize.ToString());
                var oddDetails = await files.DetailsAsync(end, endStat, true, CancellationToken.None);
                Check("its details are read", oddDetails.TryGetValue("file", out var oddFile) && oddFile is Dictionary<string, object?> of && of.ContainsKey("sha256"));
                Check("a folder can be made inside one",
                    await files.CreateFolderAsync(odd, "made", CancellationToken.None) == Path.Combine(odd, "made") && Directory.Exists(@"\\?\" + Path.Combine(odd, "made")));
                var intoOdd = await files.UploadAsync(odd, "up.txt", PasteConflictPolicy.AutoRename, new MemoryStream(Encoding.UTF8.GetBytes("up")), CancellationToken.None);
                Check("and a file uploaded into it", System.IO.File.ReadAllText(@"\\?\" + intoOdd) == "up", intoOdd);
                var deleted = await files.DeleteAsync(new[] { end }, CancellationToken.None);
                Check("delete says why it cannot recycle one, rather than that it is missing",
                    deleted.Deleted == 0 && deleted.Failed.Count == 1 && deleted.Failed[0].Error.Contains("Recycle Bin") && System.IO.File.Exists(@"\\?\" + end),
                    deleted.Failed.FirstOrDefault()?.Error);
                var fixedName = await files.RenameAsync(end, "fixed.txt", CancellationToken.None);
                Check("and it can be renamed to a name Windows takes", System.IO.File.ReadAllText(fixedName) == "ends in a dot" && !System.IO.File.Exists(@"\\?\" + end), fixedName);

                var f = ConnectFiles.FailureFor("two.bin: it would not go", new[] { Path.Combine(a, "two.bin") });
                Check("an engine's error is filed against the source it names", f.Path == Path.Combine(a, "two.bin") && f.Error == "It would not go.", $"{f.Path} {f.Error}");
                Check("a share has no Recycle Bin, so nothing there is deleted from the phone", !ConnectFiles.HasRecycleBin(@"\\server\share\x"));
                Check("the system drive has one", ConnectFiles.HasRecycleBin(Path.GetTempPath()));
            }
            finally
            {
                try { Directory.Delete(@"\\?\" + root, true); } catch { }
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
                s.Write(Encoding.ASCII.GetBytes($"{method} {url} HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Connect-Code: {Code}\r\n{extra}\r\n"));

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

        // MARK: Round 2

        /// <summary>
        /// The second round of web-app findings, each held as it was found: a locked file reported as copied,
        /// DNS rebinding, unlimited code guessing, long names, Skip called a failure, one upload id raced,
        /// a stalled body held, a cancelled chunk orphaned, a cache of misses, a Range header answered 416, and
        /// shares opened on a phone's say-so.
        /// </summary>
        private static async Task Round2Tests()
        {
            Console.WriteLine("Web app, round 2:");
            Round2Pure();
            await Round2LocalFiles();
            await Round2Server();
            await Round2ChunkCancelled();
        }

        private static void Round2Pure()
        {
            // 1. The outcome is the engine's count, not its progress.
            var locked = new ConnectJob("l", "copy");
            locked.Report(1, 1, 10, 10, "");   // robocopy's progress counts a file it gave up on as done
            locked.Counted(0, 1);
            locked.Fail(@"C:\a\x.txt", ConnectFiles.InUseSentence(@"C:\a\x.txt"));
            var (state, message) = ConnectServer.Outcome(locked, move: false);
            Check("a copy whose one file was in use fails, saying so, though the progress counted it done",
                state == "failed" && message != null && message.Contains("x.txt is in use"), $"{state} {message}");
            locked.Finish(state, message);
            var snap = JsonSerializer.Serialize(locked.Snapshot());
            Check("and the job shows nothing done", snap.Contains("\"itemsDone\":0"), snap);

            var some = new ConnectJob("s", "move");
            some.Report(3, 3, 0, 0, "");
            some.Counted(2, 1);
            some.Fail(@"C:\a\y.txt", ConnectFiles.InUseSentence(@"C:\a\y.txt"));
            (state, message) = ConnectServer.Outcome(some, move: true);
            Check("some moved and one not is done, saying what stayed and why",
                state == "done" && message != null && message.StartsWith("Moved 2 files; 1 file could not be moved.") && message.Contains("y.txt is in use"),
                $"{state} {message}");
            some.Finish(state, message);
            snap = JsonSerializer.Serialize(some.Snapshot());
            Check("with only what went counted as done", snap.Contains("\"itemsDone\":2") && snap.Contains("\"items\":3"), snap);

            // 7. Skip that skipped everything.
            var skipped = new ConnectJob("k", "copy");
            skipped.Counted(0, 0);
            (state, message) = ConnectServer.Outcome(skipped, move: false);
            Check("a copy under Skip where everything was there already is done, not failed", state == "done" && message == null, $"{state} {message}");
            Check("Skip's own note is recognised and not filed as a failure",
                ConnectFiles.IsSkipNote("1 item already existed and was skipped") && ConnectFiles.IsSkipNote("3 items already existed and were skipped") &&
                !ConnectFiles.IsSkipNote("x.txt: access is denied"));
            var log = new ConflictLog();
            log.Skipped(@"C:\b\x.txt");
            Equal("and what was skipped is said as a sentence", "1 item skipped, because it was already there.",
                ConnectFiles.CollisionSentence(log.Snapshot()) ?? "(nothing)");

            var f = ConnectFiles.FailureFor(@"ERROR 32 (0x00000020) Copying File C:\src\in use.txt: The process cannot access the file because it is being used by another process.",
                new[] { @"C:\src\in use.txt" });
            Check("robocopy's ERROR 32 is filed against the file and says it is in use, naming it",
                f.Path == @"C:\src\in use.txt" && f.Error == "in use.txt is in use by another program on the PC. Close it there, then try again.", $"{f.Path} | {f.Error}");
            f = ConnectFiles.FailureFor(@"ERROR 33 (0x00000021) Copying File C:\src\deep\locked.bin: The process cannot access the file because another process has locked a portion of the file.",
                new[] { @"C:\src" });
            Check("ERROR 33 is in use too, against the file deep in the folder copied",
                f.Path == @"C:\src\deep\locked.bin" && f.Error.StartsWith("locked.bin is in use"), $"{f.Path} | {f.Error}");
            f = ConnectFiles.FailureFor(@"ERROR 5 (0x00000005) Copying File C:\src\no.txt: Access is denied.", new[] { @"C:\src\no.txt" });
            Check("any other robocopy error keeps its reason", f.Path == @"C:\src\no.txt" && f.Error.EndsWith("Access is denied."), $"{f.Path} | {f.Error}");

            // 2. Host and Origin.
            const string ts = "laptop.tail3d7403.ts.net";
            Check("the loopback names on our port are this server",
                ConnectServer.HostAllowed("127.0.0.1:47810", 47810, ts) && ConnectServer.HostAllowed("localhost:47810", 47810, ts) &&
                ConnectServer.HostAllowed("[::1]:47810", 47810, ts) && ConnectServer.HostAllowed("LOCALHOST", 47810, ts));
            Check("a loopback name on another port is not", !ConnectServer.HostAllowed("127.0.0.1:8080", 47810, ts));
            Check("a name somebody pointed at 127.0.0.1 is not (DNS rebinding)", !ConnectServer.HostAllowed("attacker.example:47810", 47810, ts));
            Check("the ts.net name Serve forwards is, with or without 443",
                ConnectServer.HostAllowed(ts, 47810, ts) && ConnectServer.HostAllowed("Laptop.Tail3d7403.ts.net:443", 47810, ts) &&
                ConnectServer.HostAllowed(ts + ".", 47810, ts + "."));
            Check("another machine's ts.net name is not, once ours is known", !ConnectServer.HostAllowed("other.tail3d7403.ts.net", 47810, ts));
            Check("before ours is known a ts.net name is let through, and nothing else",
                ConnectServer.HostAllowed(ts, 47810, null) && !ConnectServer.HostAllowed("attacker.example", 47810, null));
            Check("a Host that is not a host is not",
                !ConnectServer.HostAllowed("", 47810, ts) && !ConnectServer.HostAllowed("a:b:c", 47810, ts) && !ConnectServer.HostAllowed("127.0.0.1:99999", 47810, ts));
            Check("an Origin naming the same host and port is the web app's own",
                ConnectServer.OriginAllowed("https://" + ts, ts) && ConnectServer.OriginAllowed("http://127.0.0.1:47810", "127.0.0.1:47810") &&
                ConnectServer.OriginAllowed(null, "127.0.0.1:47810"));
            Check("any other Origin is refused",
                !ConnectServer.OriginAllowed("http://attacker.example:47810", "127.0.0.1:47810") && !ConnectServer.OriginAllowed("null", "127.0.0.1:47810") &&
                !ConnectServer.OriginAllowed("http://127.0.0.1:47810", ts) && !ConnectServer.OriginAllowed("https://127.0.0.1:47811", "127.0.0.1:47810"));

            // 3. Wrong codes cost time.
            using (var counting = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: FreePort()))
            {
                int said = 0;
                counting.CodeGuessing += _ => said++;
                var t0 = DateTime.UtcNow;
                for (int i = 0; i < 4; i++) counting.CodeTried("addr:x", false, t0);
                Check("four wrong codes cost nothing", counting.CodeWait("addr:x", t0) == TimeSpan.Zero);
                counting.CodeTried("addr:x", false, t0);
                Equal("after the fifth the next try waits a second", "1", counting.CodeWait("addr:x", t0).TotalSeconds.ToString());
                counting.CodeTried("addr:x", false, t0);
                Equal("after the sixth, two", "2", counting.CodeWait("addr:x", t0).TotalSeconds.ToString());
                for (int i = 0; i < 30; i++) counting.CodeTried("addr:x", false, t0);
                Check("and never more than a minute", counting.CodeWait("addr:x", t0) == ConnectServer.LongestCodeWait, counting.CodeWait("addr:x", t0).ToString());
                Equal("said once, however many", "1", said.ToString());
                Check("nobody else waits for it", counting.CodeWait("user:friend@example.com", t0) == TimeSpan.Zero);
                Check("and it is forgotten in time", counting.CodeWait("addr:x", t0 + ConnectServer.CodeStrikesKeptFor + TimeSpan.FromMinutes(1)) == TimeSpan.Zero);
                counting.CodeTried("addr:y", false, t0);
                counting.CodeTried("addr:y", true, t0);
                for (int i = 0; i < 4; i++) counting.CodeTried("addr:y", false, t0);
                Check("a right code starts the count again", counting.CodeWait("addr:y", t0) == TimeSpan.Zero);
            }

            // 13. Ranges RFC 9110 says to ignore, and shares.
            Equal("a Range in another unit is ignored: the whole file", "200", ConnectServer.RangeStatus("items=0-5", 300, out _, out _).ToString());
            Equal("so is a malformed one", "200", ConnectServer.RangeStatus("bytes=abc", 300, out _, out _).ToString());
            Equal("and one ending before it starts", "200", ConnectServer.RangeStatus("bytes=20-10", 300, out _, out _).ToString());
            int rs = ConnectServer.RangeStatus("bytes=10-99999999999999999999999", 300, out long rf, out long rt);
            Check("an end too big for a number is the end of the file", rs == 206 && rf == 10 && rt == 299, $"{rs} {rf}-{rt}");
            Equal("a start past the end is still 416", "416", ConnectServer.RangeStatus("bytes=300-", 300, out _, out _).ToString());
            Equal("and so is one too big for a number", "416", ConnectServer.RangeStatus("bytes=99999999999999999999999-", 300, out _, out _).ToString());
            Check("shares and device paths are network paths",
                ConnectServer.IsNetworkPath(@"\\host\share\x") && ConnectServer.IsNetworkPath(@"\\?\UNC\host\share\x") &&
                ConnectServer.IsNetworkPath("//host/share") && ConnectServer.IsNetworkPath(@"\\.\pipe\x"));
            Check("a drive letter is not, mapped or not", !ConnectServer.IsNetworkPath(@"Z:\music") && !ConnectServer.IsNetworkPath(@"C:\"));
            Check("an NT path to a share is a network path too",
                ConnectServer.IsNetworkPath(@"\??\UNC\host\share\x") && ConnectServer.IsNetworkPath(@"\??\C:\x"));
            Check("FLV HE-AAC goes as AAC LC", Rewrap.Profile(5) == 2 && Rewrap.Profile(29) == 2 && Rewrap.Profile(2) == 2);

            // 5. Room for the number.
            var nearLimit = new string('n', 250) + ".flac";
            var numbered = ConnectFiles.UniqueLocalName(nearLimit, n => n == nearLimit);
            Check("a name near the limit is shortened before it is numbered, keeping its extension",
                numbered.Length <= 255 && numbered.EndsWith(".flac") && numbered != nearLimit, $"{numbered.Length}");
            Equal("a short name is numbered as always", "song (2).flac", ConnectFiles.UniqueLocalName("song.flac", n => n == "song.flac"));

            // 11. Misses are not remembered.
            int cached = WebAssets.Cached;
            for (int i = 0; i < 50; i++) WebAssets.TryGet("/app/nope-" + Guid.NewGuid().ToString("N") + ".js", out _, out _);
            Check("names that are not the web app's files are not remembered", WebAssets.Cached == cached, $"{cached} -> {WebAssets.Cached}");
            Check("a name longer than any of ours is not even looked up", !WebAssets.TryGet("/app/" + new string('a', 200) + ".js", out _, out _));
            Check("and the real files still are", WebAssets.TryGet("/app/app.js", out var appJs, out _) && appJs.Length > 1000);

            // 12. Where focus goes, read from app.js.
            var js = Encoding.UTF8.GetString(appJs);
            Check("Select puts focus on the first box to tick, not the heading",
                js.Contains("$('file-list').querySelector('input[type=\"checkbox\"]')") && js.Contains("(first || $('btn-sel-done')).focus()"));
            Check("Copy and Move from select put focus on the Paste here button", js.Contains("(paste.hidden ? $('btn-select') : paste).focus()"));
            Check("the mini player's label is written only when it changes", js.Contains("getAttribute('aria-label') !== label"));
            Check("too many wrong codes is said on the code screen", js.Contains("res.status === 429") && js.Contains("if (res.error)"));
        }

        /// <summary>The real ConnectFiles, robocopy and all, against files another program holds.</summary>
        private static async Task Round2LocalFiles()
        {
            string root = Path.Combine(Path.GetTempPath(), "en-connect-r2-" + Guid.NewGuid().ToString("N"));
            string a = Path.Combine(root, "a"), b = Path.Combine(root, "b");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            var files = new ConnectFiles(null, new Settings());
            try
            {
                // 1. A held file is not copied, not moved, and not called done.
                var held = Path.Combine(a, "held.txt");
                System.IO.File.WriteAllText(held, "held");
                using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var copy = new ConnectJob("r2c", "copy");
                    await files.TransferAsync(copy, new[] { held }, b, false, PasteConflictPolicy.AutoRename);
                    var (state, message) = ConnectServer.Outcome(copy, move: false);
                    Check("copying a file another program holds fails, and says it is in use",
                        state == "failed" && message != null && message.Contains("held.txt is in use") && !System.IO.File.Exists(Path.Combine(b, "held.txt")),
                        $"{state} | {message} | counts {copy.Counts}");

                    var move = new ConnectJob("r2m", "move");
                    await files.TransferAsync(move, new[] { held }, b, true, PasteConflictPolicy.AutoRename);
                    (state, message) = ConnectServer.Outcome(move, move: true);
                    Check("so does moving it, and it stays where it was",
                        state == "failed" && message != null && message.Contains("held.txt is in use") && System.IO.File.Exists(held),
                        $"{state} | {message} | counts {move.Counts}");
                }

                // 1. Replacing a held file is not "1 replaced".
                var newer = Path.Combine(a, "over.txt");
                var older = Path.Combine(b, "over.txt");
                System.IO.File.WriteAllText(older, "old");
                System.IO.File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));
                System.IO.File.WriteAllText(newer, "the new one");
                using (new FileStream(older, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var over = new ConnectJob("r2o", "copy");
                    await files.TransferAsync(over, new[] { newer }, b, false, PasteConflictPolicy.Overwrite);
                    var (state, message) = ConnectServer.Outcome(over, move: false);
                    over.Finish(state, message);
                    var snap = JsonSerializer.Serialize(over.Snapshot());
                    Check("replacing a file another program holds fails as in use, and does not say replaced",
                        state == "failed" && snap.Contains("is in use") && !snap.Contains("replaced"), snap);
                }

                // 7. Skip where it is all there already.
                System.IO.File.WriteAllText(Path.Combine(a, "same.txt"), "a");
                System.IO.File.WriteAllText(Path.Combine(b, "same.txt"), "b");
                var skip = new ConnectJob("r2s", "copy");
                await files.TransferAsync(skip, new[] { Path.Combine(a, "same.txt") }, b, false, PasteConflictPolicy.Skip);
                var (skipState, skipMessage) = ConnectServer.Outcome(skip, move: false);
                skip.Finish(skipState, skipMessage);
                var skipSnap = JsonSerializer.Serialize(skip.Snapshot());
                Check("a copy under Skip where it was all there is done, with the skip said and nothing failed",
                    skipState == "done" && skip.Failed.Count == 0 && skipSnap.Contains("skipped") && System.IO.File.ReadAllText(Path.Combine(b, "same.txt")) == "b",
                    skipSnap);

                // 4. A long name, small upload.
                var longName = new string('L', 220) + ".txt";
                var up = await files.UploadAsync(b, longName, PasteConflictPolicy.AutoRename, new MemoryStream(Encoding.UTF8.GetBytes("long")), CancellationToken.None);
                Check("a small upload with a 224-character name lands", System.IO.File.ReadAllText(up) == "long", Path.GetFileName(up).Length.ToString());

                // 5. Keeping both of a name at the limit.
                var nearLimit = new string('N', 250) + ".txt";
                var first = await files.UploadAsync(b, nearLimit, PasteConflictPolicy.AutoRename, new MemoryStream(Encoding.UTF8.GetBytes("1")), CancellationToken.None);
                var second = await files.UploadAsync(b, nearLimit, PasteConflictPolicy.AutoRename, new MemoryStream(Encoding.UTF8.GetBytes("2")), CancellationToken.None);
                Check("keeping both of a 254-character name fits, with its extension",
                    second != first && Path.GetFileName(second).Length <= 255 && second.EndsWith(".txt") && System.IO.File.ReadAllText(second) == "2" &&
                    System.IO.File.ReadAllText(first) == "1", Path.GetFileName(second).Length.ToString());

                // 6. Replacing a held file by upload is in use, not "access denied".
                var busy = Path.Combine(b, "busy.txt");
                System.IO.File.WriteAllText(busy, "old");
                using (new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    int status = 0;
                    try { await files.UploadAsync(b, "busy.txt", PasteConflictPolicy.Overwrite, new MemoryStream(Encoding.UTF8.GetBytes("new")), CancellationToken.None); }
                    catch (ConnectException e) { status = e.Status; }
                    catch (Exception e) when (ConnectServer.InUse(e)) { status = 423; }
                    Equal("an upload replacing a file another program holds is 423, in use", "423", status.ToString());
                }

                // 9. A body that stops arriving.
                Exception? caught = null;
                try
                {
                    await files.UploadAsync(b, "stall.txt", PasteConflictPolicy.AutoRename,
                        new ConnectServer.StallGuard(new NeverStream(), TimeSpan.FromMilliseconds(500)), CancellationToken.None);
                }
                catch (Exception e) { caught = e; }
                Check("an upload whose body stops arriving is given up on", caught is TimeoutException, caught?.GetType().Name ?? "(nothing)");
                Check("and leaves no hidden partial in the folder",
                    !Directory.EnumerateFiles(b, "*.partial", new EnumerationOptions { AttributesToSkip = 0 }).Any());
            }
            finally
            {
                try { Directory.Delete(@"\\?\" + root, true); } catch { }
            }
        }

        /// <summary>A body that never sends a byte.</summary>
        private sealed class NeverStream : Stream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>A body that sends some bytes and then waits until it is let go.</summary>
        private sealed class GatedStream : Stream
        {
            private readonly TaskCompletionSource<bool> _go = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private bool _sent;
            public void Release() => _go.TrySetResult(true);
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                if (!_sent) { _sent = true; buffer.Span[..10].Fill(7); return 10; }
                await _go.Task.WaitAsync(token);
                return 0;
            }
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>10. Cancelled while a chunk is still arriving: the folder goes when the chunk lets go.</summary>
        private static async Task Round2ChunkCancelled()
        {
            string store = Path.Combine(Path.GetTempPath(), "en-connect-r2-store-" + Guid.NewGuid().ToString("N"));
            try
            {
                var uploads = new ConnectUploads(store);
                var id = uploads.Start(new ConnectUploads.Meta(@"C:\b", "x.bin", 1000, "rename", DateTime.UtcNow));
                var body = new GatedStream();
                var append = uploads.AppendAsync(id, 0, body, CancellationToken.None);
                for (int i = 0; i < 40 && uploads.Received(id) < 10; i++) await Task.Delay(25);
                uploads.Delete(id);
                Check("cancelled mid-chunk, the upload is gone at once to anybody asking", uploads.Find(id) == null);
                body.Release();
                try { await append.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                Check("and its folder goes when the chunk lets go, not at the next start-up",
                    !Directory.Exists(Path.Combine(store, id)), string.Join(", ", Directory.Exists(Path.Combine(store, id)) ? Directory.GetFiles(Path.Combine(store, id)) : Array.Empty<string>()));
                var idle = uploads.Start(new ConnectUploads.Meta(@"C:\b", "y.bin", 10, "rename", DateTime.UtcNow));
                uploads.Delete(idle);
                Check("cancelled with nothing arriving, it goes at once", !Directory.Exists(Path.Combine(store, idle)));
            }
            finally { try { Directory.Delete(store, true); } catch { } }
        }

        /// <summary>The server end of round 2, over real sockets.</summary>
        private static async Task Round2Server()
        {
            const string status = """
                { "BackendState": "Running",
                  "Self": { "DNSName": "laptop.tail3d7403.ts.net.", "HostName": "laptop", "UserID": 1 },
                  "User": { "1": { "LoginName": "me@example.com" } },
                  "CertDomains": ["laptop.tail3d7403.ts.net"] }
                """;
            string dir = Path.Combine(Path.GetTempPath(), "en-connect-r2-srv-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string sends = Path.Combine(dir, "sends");
            var data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
            string bin = Path.Combine(dir, "data.bin");
            System.IO.File.WriteAllBytes(bin, data);
            var fake = new FakeFiles();
            var clip = new FakeClipboard();
            int port = FreePort();
            var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port,
                files: fake, clipboard: clip, clipboardSends: sends, tailscaleStatus: () => status);
            server.ServePeerOverride = _ => true; // as if Tailscale Serve made every connection
            try
            {
                server.Start();
                if (!await WaitForListener(port)) { Check("the round 2 server listens", false); return; }
                await server.LoginSettled();
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(20) };

                async Task<HttpResponseMessage> Ask(HttpMethod method, string url, string? host = null, string? login = null, string? code = null,
                    string? origin = null, string? range = null, HttpContent? content = null)
                {
                    var request = new HttpRequestMessage(method, url) { Content = content };
                    if (host != null) request.Headers.Host = host;
                    if (login != null) request.Headers.Add("Tailscale-User-Login", login);
                    if (code != null) request.Headers.Add("X-Connect-Code", code);
                    if (origin != null) request.Headers.Add("Origin", origin);
                    if (range != null) request.Headers.TryAddWithoutValidation("Range", range);
                    return await http.SendAsync(request);
                }
                static int N(HttpResponseMessage r) => (int)r.StatusCode;

                // 2. DNS rebinding.
                Equal("a rebinding page's Host is refused, owner's header and all", "421",
                    N(await Ask(HttpMethod.Get, "/api/drives", host: $"attacker.example:{port}", login: "me@example.com")).ToString());
                Equal("even for the app's own files", "421", N(await Ask(HttpMethod.Get, "/", host: $"attacker.example:{port}")).ToString());
                Equal("the ts.net name Serve forwards is answered", "200",
                    N(await Ask(HttpMethod.Get, "/api/drives", host: "laptop.tail3d7403.ts.net", login: "me@example.com")).ToString());
                Equal("and plain loopback, with the code", "200", N(await Ask(HttpMethod.Get, "/api/info", code: Code)).ToString());
                Equal("and localhost", "200", N(await Ask(HttpMethod.Get, "/api/info", host: $"localhost:{port}", code: Code)).ToString());
                var mkdirBody = () => new StringContent("""{"parent":"C:\\b","name":"made"}""", Encoding.UTF8, "application/json");
                Equal("a POST from another site's page is refused", "403",
                    N(await Ask(HttpMethod.Post, "/api/mkdir", code: Code, origin: "http://attacker.example", content: mkdirBody())).ToString());
                Equal("a POST from the app's own page goes through", "200",
                    N(await Ask(HttpMethod.Post, "/api/mkdir", code: Code, origin: $"http://127.0.0.1:{port}", content: mkdirBody())).ToString());
                Equal("and through Serve, from its ts.net page", "200",
                    N(await Ask(HttpMethod.Post, "/api/mkdir", host: "laptop.tail3d7403.ts.net", login: "me@example.com",
                        origin: "https://laptop.tail3d7403.ts.net", content: mkdirBody())).ToString());

                // 3. Wrong codes, per client.
                const string friend = "friend@example.com";
                var answers = new List<int>();
                for (int i = 0; i < 5; i++) answers.Add(N(await Ask(HttpMethod.Get, "/api/info", login: friend, code: "0000000" + i)));
                Check("five wrong codes are each refused as wrong", answers.All(s => s == 401), string.Join(",", answers));
                var limited = await Ask(HttpMethod.Get, "/api/info", login: friend, code: "00000001");
                Check("the sixth try is told to wait, with Retry-After", N(limited) == 429 && limited.Headers.RetryAfter?.Delta is { } d && d.TotalSeconds >= 1,
                    $"{N(limited)} {limited.Headers.RetryAfter}");
                Equal("the right code waits too while the wait lasts", "429", N(await Ask(HttpMethod.Get, "/api/info", login: friend, code: Code)).ToString());
                var who = await Ask(HttpMethod.Get, "/api/whoami", login: friend, code: Code);
                var whoText = await who.Content.ReadAsStringAsync();
                Check("whoami cannot be used to keep guessing", N(who) == 429 && whoText.Contains("Too many wrong pairing codes"), $"{N(who)} {whoText}");
                Equal("the owner, by Serve's identity, is never held up", "200", N(await Ask(HttpMethod.Get, "/api/info", login: "me@example.com")).ToString());
                Equal("nor is somebody else with the right code", "200", N(await Ask(HttpMethod.Get, "/api/info", login: "other@example.com", code: Code)).ToString());
                await Task.Delay(TimeSpan.FromSeconds((limited.Headers.RetryAfter?.Delta?.TotalSeconds ?? 1) + 0.3));
                Equal("after the wait the right code is let in", "200", N(await Ask(HttpMethod.Get, "/api/info", login: friend, code: Code)).ToString());
                Equal("and the count starts again", "401", N(await Ask(HttpMethod.Get, "/api/info", login: friend, code: "00000000")).ToString());

                // 6. /api/audio on a held file.
                var heldAudio = Path.Combine(dir, "held.wav");
                System.IO.File.WriteAllBytes(heldAudio, new byte[4096]);
                using (new FileStream(heldAudio, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var r = await Ask(HttpMethod.Get, "/api/audio?path=" + Uri.EscapeDataString(heldAudio), code: Code);
                    var t = await r.Content.ReadAsStringAsync();
                    Check("playing a file another program holds is 423, in use, not \"can't decode\"",
                        N(r) == 423 && t.Contains("held.wav is in use"), $"{N(r)} {t}");
                }

                // 13. Shares.
                const string share = @"\\127.0.0.2\nothing\x.mp3";
                Equal("a share is not opened", "403", N(await Ask(HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(share), code: Code)).ToString());
                Equal("nor measured", "403", N(await Ask(HttpMethod.Get, "/api/size?path=" + Uri.EscapeDataString(@"\\?\UNC\127.0.0.2\nothing"), code: Code)).ToString());
                Equal("nor listed", "404", N(await Ask(HttpMethod.Get, "/api/list?path=" + Uri.EscapeDataString(@"\\127.0.0.2\nothing"), code: Code)).ToString());

                // 13. Ranges to ignore.
                foreach (var junk in new[] { "items=0-5", "bytes=abc", "bytes=20-10" })
                {
                    var r = await Ask(HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(bin), code: Code, range: junk);
                    var got = await r.Content.ReadAsByteArrayAsync();
                    Check($"Range \"{junk}\" is ignored: 200 and the whole file", N(r) == 200 && got.SequenceEqual(data), $"{N(r)} {got.Length}");
                }
                var big = await Ask(HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(bin), code: Code, range: "bytes=290-99999999999999999999999");
                var tail = await big.Content.ReadAsByteArrayAsync();
                Check("an end too big for a number runs to the end of the file", N(big) == 206 && tail.SequenceEqual(data[290..]), $"{N(big)} {tail.Length}");

                // 8. One id, four at once.
                fake.UploadDelayMs = 400;
                int before = fake.Uploads;
                var racing = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
                {
                    var r = await Ask(HttpMethod.Post, "/api/upload?folder=C%3A%5Cb&name=race.txt&uploadId=race-1", code: Code,
                        content: new ByteArrayContent(Encoding.UTF8.GetBytes("once")));
                    return (N(r), await r.Content.ReadAsStringAsync());
                }));
                fake.UploadDelayMs = 0;
                Check("four uploads with one id at once write it once, and all four hear the same answer",
                    fake.Uploads == before + 1 && racing.All(x => x.Item1 == 200 && x.Item2 == racing[0].Item2),
                    $"{fake.Uploads - before} written; " + string.Join(" | ", racing.Select(x => $"{x.Item1} {x.Item2}")));

                // 9. Stalled bodies on the one-request upload and the clipboard send.
                server.UploadStallAfter = TimeSpan.FromSeconds(1);
                foreach (var url in new[] { "/api/upload?folder=C%3A%5Cb&name=stalled.txt", "/api/clipboard/send?name=stalled.txt" })
                {
                    using var client = new TcpClient();
                    client.Connect(IPAddress.Loopback, port);
                    client.ReceiveTimeout = 8000;
                    var s = client.GetStream();
                    s.Write(Encoding.ASCII.GetBytes($"POST {url} HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Connect-Code: {Code}\r\nContent-Length: 100\r\n\r\n"));
                    s.Write(new byte[10]);
                    s.Flush();
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    int read;
                    try { read = s.Read(new byte[256], 0, 256); } catch (IOException) { read = 0; }
                    Check($"{url.Split('?')[0]} gives up on a stalled body and closes the connection", read == 0 && clock.Elapsed < TimeSpan.FromSeconds(6),
                        $"{read} bytes after {clock.Elapsed.TotalSeconds:0.0} s");
                }
                server.UploadStallAfter = TimeSpan.FromSeconds(30);
                await Task.Delay(200);
                Check("and the clipboard send leaves no partial behind",
                    !Directory.Exists(sends) || !Directory.EnumerateFiles(sends, "*", SearchOption.AllDirectories).Any(),
                    Directory.Exists(sends) ? string.Join(", ", Directory.EnumerateFiles(sends, "*", SearchOption.AllDirectories)) : "");

                // 4. A long name sent to the clipboard.
                var longName = new string('c', 230) + ".txt";
                var sent = await Ask(HttpMethod.Post, "/api/clipboard/send?name=" + Uri.EscapeDataString(longName), code: Code,
                    content: new ByteArrayContent(Encoding.UTF8.GetBytes("long")));
                Equal("a long name can be sent to the clipboard", "200", N(sent).ToString());
            }
            finally
            {
                server.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // MARK: Round 3

        /// <summary>
        /// The third round: bodies and heads that trickle, protocol errors answered rather than reset, a stale
        /// code counted once however often it is polled with, and a stall timer that cannot fail a flowing chunk.
        /// </summary>
        private static async Task Round3Tests()
        {
            Console.WriteLine("Web app, round 3:");

            // 3. One stale code, sent every second, is one wrong code: no wait and nothing said.
            using (var counting = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: FreePort()))
            {
                int said = 0;
                counting.CodeGuessing += _ => said++;
                var t0 = DateTime.UtcNow;
                for (int i = 0; i < 30; i++) counting.CodeTried("addr:stale", false, t0, "87654321");
                Check("the same stale code thirty times costs nothing", counting.CodeWait("addr:stale", t0) == TimeSpan.Zero);
                Equal("and is not said as somebody guessing", "0", said.ToString());
                for (int i = 2; i <= 5; i++) counting.CodeTried("addr:stale", false, t0, "8765432" + i);   // 4 more, 5 distinct
                Check("five different wrong codes still make the next try wait", counting.CodeWait("addr:stale", t0) > TimeSpan.Zero);
                Equal("and that is said, once", "1", said.ToString());
            }

            // 4. A read that comes back just after the stall timer fired does not fail the chunk's next read.
            string store = Path.Combine(Path.GetTempPath(), "en-connect-r3-store-" + Guid.NewGuid().ToString("N"));
            try
            {
                var uploads = new ConnectUploads(store) { StallAfter = TimeSpan.FromMilliseconds(200) };
                var id = uploads.Start(new ConnectUploads.Meta(@"C:\b", "late.bin", 30, "rename", DateTime.UtcNow));
                long total = -1;
                Exception? failed = null;
                try { total = await uploads.AppendAsync(id, 0, new LateStream(3, 350), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception e) { failed = e; }
                Check("a chunk whose reads each land just after the stall timer fires is not failed as stalled",
                    failed == null && total == 30, failed?.GetType().Name + " " + failed?.Message + " " + total);
            }
            finally { try { Directory.Delete(store, true); } catch { } }

            // 1, 2. Over real sockets.
            string dir = Path.Combine(Path.GetTempPath(), "en-connect-r3-srv-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            int port = FreePort();
            var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port,
                files: new FakeFiles(), clipboard: new FakeClipboard(), clipboardSends: Path.Combine(dir, "sends"));
            try
            {
                server.Start();
                if (!await WaitForListener(port)) { Check("the round 3 server listens", false); return; }
                server.UploadStallAfter = TimeSpan.FromSeconds(1);
                server.HeadTimeout = TimeSpan.FromSeconds(1);
                server.IdleTimeout = TimeSpan.FromSeconds(1);

                TcpClient Open()
                {
                    var c = new TcpClient();
                    c.Connect(IPAddress.Loopback, port);
                    c.ReceiveTimeout = 8000;
                    return c;
                }
                static (int Read, TimeSpan After) UntilClosed(NetworkStream s)
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    int read;
                    try { read = s.Read(new byte[256], 0, 256); } catch (IOException) { read = 0; }
                    return (read, clock.Elapsed);
                }

                // 1. Trickled JSON and image bodies, and a body after a 401.
                foreach (var (url, code) in new[] { ("/api/mkdir", Code), ("/api/clipboard/image", Code), ("/api/mkdir", "") })
                {
                    using var client = Open();
                    var s = client.GetStream();
                    var codeLine = code.Length > 0 ? $"X-Connect-Code: {code}\r\n" : "";
                    s.Write(Encoding.ASCII.GetBytes($"POST {url} HTTP/1.1\r\nHost: 127.0.0.1\r\n{codeLine}Content-Length: 100\r\n\r\n"));
                    s.Write(Encoding.ASCII.GetBytes("{\"parent\":"));
                    s.Flush();
                    string what = code.Length > 0 ? url : "a body after a 401";
                    if (code.Length == 0)
                    {
                        int status = 0;
                        try { status = ReadResponse(s, head: false).Status; } catch (IOException) { }
                        Equal("a request with no code is refused without reading its body", "401", status.ToString());
                    }
                    var (read, after) = UntilClosed(s);
                    Check($"{what}: a trickled body is given up on and the connection closed", read == 0 && after < TimeSpan.FromSeconds(6),
                        $"{read} bytes after {after.TotalSeconds:0.0} s");
                }

                // 1. A head that never finishes, and a connection that never starts one.
                using (var client = Open())
                {
                    var s = client.GetStream();
                    s.Write(Encoding.ASCII.GetBytes("GET /api/info HTTP/1.1\r\nHost: 127.0.0.1\r\n"));
                    int status = 0;
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    try { status = ReadResponse(s, head: false).Status; } catch (IOException) { }
                    Check("a head that stops arriving is answered 408 and closed", status == 408 && clock.Elapsed < TimeSpan.FromSeconds(6),
                        $"{status} after {clock.Elapsed.TotalSeconds:0.0} s");
                }
                using (var client = Open())
                {
                    var (read, after) = UntilClosed(client.GetStream());
                    Check("a connection that never sends a request is closed, saying nothing", read == 0 && after < TimeSpan.FromSeconds(6),
                        $"{read} bytes after {after.TotalSeconds:0.0} s");
                }

                // 2. Protocol errors are answered in JSON, with nosniff, before the close.
                var cases = new (string Name, string Request, int Status)[]
                {
                    ("a head over 16 KB", "GET /api/info HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Big: " + new string('a', 17_000), 431),
                    ("a request line that is not HTTP", "hello there\r\n\r\n", 400),
                    ("a request line with no version", "GET /api/info\r\n\r\n", 400),
                };
                foreach (var (name, request, expected) in cases)
                {
                    using var client = Open();
                    var s = client.GetStream();
                    try { s.Write(Encoding.ASCII.GetBytes(request)); } catch (IOException) { }
                    int status = 0;
                    var headers = new Dictionary<string, string>();
                    string body = "";
                    try
                    {
                        var answer = ReadResponse(s, head: false);
                        status = answer.Status; headers = answer.Headers; body = Encoding.UTF8.GetString(answer.Body);
                    }
                    catch (IOException e) { body = e.Message; }
                    Check($"{name} is answered {expected}, in JSON, with nosniff",
                        status == expected && body.Contains("\"error\"") && headers.GetValueOrDefault("x-content-type-options") == "nosniff" &&
                        (headers.GetValueOrDefault("content-type") ?? "").StartsWith("application/json"),
                        $"{status} {body}");
                }

                using (var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) })
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, "/api/info");
                    request.Headers.Add("X-Connect-Code", Code);
                    Equal("and an ordinary request is still answered", "200", ((int)(await http.SendAsync(request)).StatusCode).ToString());
                }
            }
            finally
            {
                server.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }

            // 3. The web app stops polling jobs on 401 and 429.
            if (WebAssets.TryGet("/app/app.js", out var appJs, out _))
            {
                var js = Encoding.UTF8.GetString(appJs);
                int poll = js.IndexOf("function pollJobs()", StringComparison.Ordinal);
                var pollBody = poll < 0 ? "" : js.Substring(poll, Math.Min(2500, js.Length - poll));
                Check("job polling stops on a code that is not right, or a wait",
                    pollBody.Contains("err.status === 401 || err.status === 429") && pollBody.Contains("if (!$('screen-code').hidden) return;"));
            }
            else Check("app.js is served", false);
        }

        /// <summary>A body whose every read comes back with bytes a little after the caller's timer fired.</summary>
        private sealed class LateStream : Stream
        {
            private int _left;
            private readonly int _delayMs;
            public LateStream(int reads, int delayMs) { _left = reads; _delayMs = delayMs; }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested();
                if (_left-- <= 0) return 0;
                await Task.Delay(_delayMs);   // deaf to the token: the bytes were already on their way
                buffer.Span[..10].Fill(1);
                return 10;
            }
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>
        /// The owner's login header believed only from the Tailscale service, the owner kept signed in
        /// through a tailscale status that did not answer, and the clipboard read only when asked.
        /// </summary>
        private static async Task ServePeerTests()
        {
            Console.WriteLine("Serve peer:");

            // Who holds the other end of a loopback connection: this process, here.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                using var accepted = await listener.AcceptTcpClientAsync();
                var server = (IPEndPoint)accepted.Client.LocalEndPoint!;
                var pid = TcpOwner.PidOf((IPEndPoint)accepted.Client.RemoteEndPoint!, server);
                Equal("Windows names the process at the other end of a loopback connection",
                    Environment.ProcessId.ToString(), pid?.ToString() ?? "none");
                Check("and nobody for a connection that is not there", TcpOwner.PidOf(new IPEndPoint(IPAddress.Loopback, 1), server) == null);
            }
            finally { listener.Stop(); }
            Check("tailscaled running as a service is Tailscale", ConnectServer.IsTailscaleService("tailscaled", 0));
            Check("a program of that name in someone's session is not", !ConnectServer.IsTailscaleService("tailscaled", 1));
            Check("nor is any other service", !ConnectServer.IsTailscaleService("SelfTest", 0));

            const string running = """
                { "BackendState": "Running",
                  "Self": { "DNSName": "laptop.tail3d7403.ts.net.", "HostName": "laptop", "UserID": 1 },
                  "User": { "1": { "LoginName": "me@example.com" } } }
                """;
            // Signed in, then no answer at all (slow or failed), then Tailscale saying it has stopped.
            var calls = new int[1];
            string? Status() => Interlocked.Increment(ref calls[0]) switch
            {
                1 => running,
                2 => null,
                _ => """{ "BackendState": "Stopped" }""",
            };
            int port = FreePort();
            var connect = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port,
                files: new FakeFiles(), tailscaleStatus: Status);
            try
            {
                connect.Start();
                if (!await WaitForListener(port)) { Check("the serve-peer server listens", false); return; }
                await connect.LoginSettled();
                HttpClient NewClient() => new() { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
                async Task<string> Drives(HttpClient http)
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, "/api/drives");
                    request.Headers.Add("Tailscale-User-Login", "me@example.com");
                    using var response = await http.SendAsync(request);
                    return ((int)response.StatusCode).ToString();
                }
                void DueForRefresh() =>
                    typeof(ConnectServer).GetField("_loginAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                        .SetValue(connect, DateTime.MinValue);

                using (var http = NewClient())
                    Equal("the owner's header from a program that is not Tailscale needs the code", "401", await Drives(http));

                // A new connection, because the answer is kept for the connection's life.
                connect.ServePeerOverride = _ => true;
                using (var http = NewClient())
                {
                    Equal("and from Tailscale it does not", "200", await Drives(http));
                    DueForRefresh();
                    await Drives(http);
                    await connect.LoginSettled();
                    Check("tailscale status was asked again", Volatile.Read(ref calls[0]) >= 2, calls[0].ToString());
                    Equal("a tailscale status that gave no answer leaves the owner signed in", "200", await Drives(http));
                    DueForRefresh();
                    await Drives(http);
                    await connect.LoginSettled();
                    Equal("but Tailscale saying it has stopped does not", "401", await Drives(http));
                }
            }
            finally { connect.Dispose(); }

            // The clipboard: a change is counted, and nothing is read until somebody asks.
            var clip = new ConnectClipboard();
            clip.Observe((new ClipState(0, "text", Text: "kept"), null));
            clip.Changed();
            Check("a clipboard change is counted without reading the clipboard",
                clip.Current.Seq == 2 && clip.Current.Text == "kept", $"{clip.Current.Seq} {clip.Current.Text}");
            var write = clip.SetTextAsync("x");
            var finished = await Task.WhenAny(write, Task.Delay(3000));
            Check("a clipboard whose listener never started refuses a write at once, with 503",
                finished == write && write.Exception?.InnerException is ConnectException { Status: 503 },
                write.IsCompleted ? write.Exception?.InnerException?.Message ?? "no error" : "still waiting");
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

    // ---- merged from Sweep308WebTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// Regression tests for the 3.0.8 sweep of the web app (app.js and ConnectServer). The app.js checks read the
    /// embedded script, as the phone gets it, and pin the decision: rows rebuilt under focus give it back, the
    /// phone waits at least as long as the PC's own budgets, Back to the drives lands on the drive.
    /// </summary>
    internal static class Sweep308WebTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        public static Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            Console.WriteLine("Web app sweep (3.0.8):");

            var js = WebAssets.TryGet("/app/app.js", out var bytes, out _) ? Encoding.UTF8.GetString(bytes) : "";
            Check("app.js is embedded", js.Length > 1000);

            TransfersKeepFocusTests(js);
            FilesKeepFocusTests(js);
            QueueKeepsFocusTests(js);
            TimeoutsCoverServerBudgetsTests(js);
            BackToDrivesLandsOnDriveTests(js);
            GatewayTimeoutReasonTests();
            return Task.CompletedTask;
        }

        /// <summary>A function of app.js, from its declaration to the next top-level one.</summary>
        private static string Function(string js, string name)
        {
            var m = Regex.Match(js, @"^(async )?function " + Regex.Escape(name) + @"\(", RegexOptions.Multiline);
            if (!m.Success) return "";
            var next = Regex.Match(js[(m.Index + m.Length)..], @"^(async )?function |^const |^let |^// -", RegexOptions.Multiline);
            return next.Success ? js.Substring(m.Index, m.Length + next.Index) : js[m.Index..];
        }

        /// <summary>
        /// The Transfers list was rebuilt from nothing every second while anything moved, so a Stop button that
        /// had focus was thrown away under the screen reader and could not be pressed. Rows are now kept by
        /// transfer and changed in place.
        /// </summary>
        private static void TransfersKeepFocusTests(string js)
        {
            var body = Function(js, "renderTransfers");
            Check("renderTransfers is there", body.Length > 0);
            Check("transfer rows are not rebuilt wholesale", !body.Contains("replaceChildren(frag)"), body.Length > 0 ? null : "missing");
            Check("transfer rows are kept by their transfer", body.Contains("dataset.key") && body.Contains("t.key"));
            Check("a row's text changes only when it differs", body.Contains("setText("));
        }

        /// <summary>
        /// Copy, Move and Get size from a row's More dialog, and a finished upload's refresh, rebuilt the file
        /// list and dropped focus to the top of the page. The same row's same control gets it back.
        /// </summary>
        private static void FilesKeepFocusTests(string js)
        {
            var body = Function(js, "renderFiles");
            Check("renderFiles notes what had focus before rebuilding", body.Contains("document.activeElement") &&
                  body.IndexOf("document.activeElement", StringComparison.Ordinal) < body.IndexOf("replaceChildren", StringComparison.Ordinal));
            Check("and gives it back after", Regex.IsMatch(body, @"replaceChildren\(frag\);[\s\S]*\.focus\(\)"));
            Check("only within the same folder", js.Contains("filesShownFor"));
        }

        /// <summary>Tapping a track in the queue rebuilt the queue and threw away the button that was tapped.</summary>
        private static void QueueKeepsFocusTests(string js)
        {
            var body = Function(js, "renderQueue");
            Check("renderQueue gives focus back after rebuilding",
                  body.Contains("document.activeElement") && Regex.IsMatch(body, @"replaceChildren\(frag\);[\s\S]*\.focus\(\)"));
        }

        /// <summary>The timeout an api() call on <paramref name="route"/> passes, or 20000 (the default) when none.</summary>
        private static int TimeoutFor(string js, string route)
        {
            int at = js.IndexOf("'" + route + "'", StringComparison.Ordinal);
            if (at < 0) return -1;
            int end = js.IndexOf('\n', at);
            var line = js[at..(end < 0 ? js.Length : end)];
            var m = Regex.Match(line, @"timeout:\s*(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : 20000;
        }

        /// <summary>
        /// The phone gave up after 20 seconds on requests the PC is allowed longer for (a folder size: 20 seconds
        /// locally, a minute on Drive; a slow folder listing: 45 seconds; trashing many Drive items), and then
        /// said the PC could not be reached, or showed the offline screen, while the PC was fine.
        /// </summary>
        private static void TimeoutsCoverServerBudgetsTests(string js)
        {
            int size = TimeoutFor(js, "/api/size");
            Check("Get size waits longer than Drive's minute", size > (int)ConnectFiles.DriveSizeBudget.TotalMilliseconds, size.ToString());
            Check("and longer than the local walk", size > ConnectFiles.SizeBudgetSeconds * 1000, size.ToString());
            int list = TimeoutFor(js, "/api/list");
            Check("opening a folder waits longer than the PC's 45 seconds", list > 45000, list.ToString());
            int delete = TimeoutFor(js, "/api/delete");
            Check("a delete waits longer than the usual 20 seconds", delete > 20000, delete.ToString());
        }

        /// <summary>
        /// The phone's own Back gesture from a drive's top folder looked for a row called "C:" in the drive list,
        /// whose rows are called "C:\", so focus went to the heading instead of the drive, unlike the Back button.
        /// </summary>
        private static void BackToDrivesLandsOnDriveTests(string js)
        {
            int at = js.IndexOf("'popstate'", StringComparison.Ordinal);
            var handler = at < 0 ? "" : js.Substring(at, Math.Min(600, js.Length - at));
            Check("Back to the drives names the drive with its backslash",
                  handler.Contains("e.state.path == null") && handler.Contains(@"[A-Za-z]:\\"), handler.Length > 0 ? null : "no popstate handler");
        }

        /// <summary>The details route answers 504 when even the basics take too long; its status line said "Internal Server Error".</summary>
        private static void GatewayTimeoutReasonTests()
        {
            var reason = typeof(ConnectServer).GetMethod("Reason", BindingFlags.NonPublic | BindingFlags.Static);
            var said = reason?.Invoke(null, new object[] { 504 }) as string;
            Check("a 504 is called Gateway Timeout", said == "Gateway Timeout", said ?? "no Reason method");
            var ok = reason?.Invoke(null, new object[] { 200 }) as string;
            Check("and a 200 is still OK", ok == "OK", ok);
        }
    }

    // ---- merged from Sweep309WebTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// Regression tests for the second web app sweep (after 3.0.8): the clipboard image that leaked when the
    /// clipboard thread never started, Forward before a track's length is known, messages said twice on the
    /// Clipboard tab and the code screen, a Drive stream left leased at shutdown, jobs that stayed "waiting" for
    /// ever, paths naming a file's hidden streams, and the copy check of a move out of Drive into a dotted folder.
    /// </summary>
    internal static class Sweep309WebTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        private const string Code = "24681357";

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            Console.WriteLine("Web app sweep (3.0.9):");

            var js = WebAssets.TryGet("/app/app.js", out var bytes, out _) ? Encoding.UTF8.GetString(bytes) : "";
            var html = WebAssets.TryGet("/", out var page, out _) ? Encoding.UTF8.GetString(page) : "";
            Check("app.js and index.html are embedded", js.Length > 1000 && html.Length > 500);

            ClipboardImageWithoutWindowTests();
            SkipBeforeLengthKnownTests(js);
            SaidOnceTests(js, html);
            JobsWaitingArePolledTests(js);
            UploadStoppedWhileStartingTests(js);
            AudioLeaseReachesDecoderTests();
            StreamsAfterDisposeTests();
            ListWaitTests(js);
            ContentTypeTests();
            FindExeIgnoresRelativePathTests();
            MoveOutOfDriveChecksLiteralPathTests();
            await StreamPathTests();
        }

        /// <summary>A function of app.js, from its declaration to the next top-level one.</summary>
        private static string Function(string js, string name)
        {
            var m = Regex.Match(js, @"^(async )?function " + Regex.Escape(name) + @"\(", RegexOptions.Multiline);
            if (!m.Success) return "";
            var next = Regex.Match(js[(m.Index + m.Length)..], @"^(async )?function |^const |^let |^// -", RegexOptions.Multiline);
            return next.Success ? js.Substring(m.Index, m.Length + next.Index) : js[m.Index..];
        }

        /// <summary>A C# method of a source file, from its signature to the next member at the same indent.</summary>
        private static string Method(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0) return "";
            var next = Regex.Match(source[(at + signature.Length)..], @"\n        (public|private|internal) ");
            return next.Success ? source.Substring(at, signature.Length + next.Index) : source[at..];
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        /// <summary>
        /// With the clipboard thread's window not there, the image was decoded into a bitmap whose only Dispose was
        /// in work that never ran: one image left behind per request. It is now refused before anything is decoded.
        /// </summary>
        private static void ClipboardImageWithoutWindowTests()
        {
            byte[] png;
            using (var bitmap = new Bitmap(4, 4))
            using (var output = new MemoryStream())
            {
                bitmap.Save(output, ImageFormat.Png);
                png = output.ToArray();
            }
            var clipboard = new ConnectClipboard();   // never started: no window
            Task<long>? task = null;
            Exception? thrown = null;
            try { task = clipboard.SetImageAsync(png); }
            catch (Exception e) { thrown = e; }
            Check("an image for a clipboard with no window does not throw on the spot", thrown == null, thrown?.Message);
            var error = task?.Exception?.InnerException as ConnectException;
            Check("it is refused as the clipboard being unusable (503)", task is { IsFaulted: true } && error?.Status == 503,
                  task == null ? "no task" : $"{task.Status} {error?.Status}");

            var body = Method(Source("ConnectClipboard.cs") ?? "", "public Task<long> SetImageAsync(");
            int window = body.IndexOf("_window == null", StringComparison.Ordinal);
            int decode = body.IndexOf("Image.FromStream", StringComparison.Ordinal);
            Check("the window is asked about before the image is decoded", window >= 0 && decode > window, $"{window} {decode}");
        }

        /// <summary>Forward or Back before the track's length was known took the end as 0 and went to the start.</summary>
        private static void SkipBeforeLengthKnownTests(string js)
        {
            var body = Function(js, "skip");
            Check("skip is there", body.Length > 0);
            Check("an unknown length is not taken as zero", !body.Contains("(a.duration || 0)"), body);
            Check("an unknown length leaves no end to stop short of", body.Contains("isFinite(a.duration)") && body.Contains("Infinity"));
        }

        /// <summary>The Clipboard tab's status line was a live region, and announce() said the same words too.</summary>
        private static void SaidOnceTests(string js, string html)
        {
            var line = Regex.Match(html, "<p id=\"clip-status\"[^>]*>").Value;
            Check("the clipboard status line is there", line.Length > 0);
            Check("and it is not a live region of its own", !line.Contains("role=") && !line.Contains("aria-live"), line);
            var send = Function(js, "sendText");
            Check("sending text is still announced", send.Contains("announce('Sent to the PC clipboard')"));

            int at = js.IndexOf("'code-form'", StringComparison.Ordinal);
            var submit = at < 0 ? "" : js.Substring(at, Math.Min(1400, js.Length - at));
            Check("the code screen's unreachable line is said by its alert only",
                  submit.Contains("$('code-error').textContent = UNREACHABLE") && !submit.Contains("announce(UNREACHABLE"), at < 0 ? "no handler" : null);
            Check("and the alert is emptied before each try, so the same sentence is said again",
                  Regex.IsMatch(submit, @"\$\('code-error'\)\.textContent = '';[\s\S]*timedFetch"));

            Check("the select count is rewritten only when it changes", Function(js, "updateSelectCount").Contains("setText("));
        }

        /// <summary>A job whose poll once missed the network was marked waiting and never polled again.</summary>
        private static void JobsWaitingArePolledTests(string js)
        {
            var body = Function(js, "pollJobs");
            Check("waiting jobs are polled too", Regex.IsMatch(body, @"filter\([^\n]*'waiting'"), body.Length > 0 ? null : "no pollJobs");
            Check("an answer puts a waiting job back to running", body.Contains("t.state = 'running'"));
        }

        /// <summary>Stop pressed while the PC was starting an upload left that upload on the PC for a day.</summary>
        private static void UploadStoppedWhileStartingTests(string js)
        {
            var body = Function(js, "upload");
            Check("an upload stopped while starting is called off on the PC",
                  Regex.IsMatch(body, @"id = s\.id;\s*(//[^\n]*\n\s*)*if \(stopped\) \{ api\('/api/upload/cancel'"));
        }

        /// <summary>
        /// /api/audio's lease went to the decoder in a second step that the request's token could stop from ever
        /// starting, leaving one Drive download leased until the app closed.
        /// </summary>
        private static void AudioLeaseReachesDecoderTests()
        {
            var body = Method(Source("ConnectServer.cs") ?? "", "private async Task Audio(");
            Check("the audio route is there", body.Length > 0);
            Check("the step that hands the lease to the decoder is not cancellable",
                  Regex.IsMatch(body, @"ConnectAudio\.Open\([^;]*;\s*\}, CancellationToken\.None\)"));
        }

        private sealed class FakeSource : IRangeSource
        {
            public int Disposed;
            public long Length => 1000;
            public int Streams => 1;
            public Task<int> ReadAsync(long offset, Memory<byte> into, CancellationToken token)
            {
                int n = (int)Math.Min(into.Length, Math.Max(0, Length - offset));
                into.Span[..n].Clear();
                return Task.FromResult(n);
            }
            public void Dispose() => Interlocked.Increment(ref Disposed);
        }

        /// <summary>
        /// A lease asked for while the server was being disposed added a download that nothing would ever stop.
        /// After Dispose a lease is refused (never null, which callers would read as "not on Drive").
        /// </summary>
        private static void StreamsAfterDisposeTests()
        {
            int opened = 0;
            var after = new ConnectStreams(_ => { opened++; return new FakeSource(); });
            after.Dispose();
            ConnectException? refused = null;
            try { after.Lease(@"G:\a.flac"); } catch (ConnectException e) { refused = e; }
            Check("a lease after Dispose is refused with a 503", refused?.Status == 503, refused?.Message);
            Check("and nothing is opened for it", opened == 0, opened.ToString());

            ConnectStreams? racing = null;
            var source = new FakeSource();
            racing = new ConnectStreams(_ => { racing!.Dispose(); return source; });
            refused = null;
            try { racing.Lease(@"G:\b.flac"); } catch (ConnectException e) { refused = e; }
            Check("disposed while a lease was being opened, the lease is refused", refused?.Status == 503, refused?.Message);
            Check("and nothing is left held", racing.Count == 0, racing.Count.ToString());
            Check("and the download opened for it is let go", Volatile.Read(ref source.Disposed) > 0);
            Check("and the sweep is not running", !racing.Sweeping);
        }

        /// <summary>The List comment said ten seconds while the code waited 45.</summary>
        private static void ListWaitTests(string js)
        {
            Check("the PC gives a folder 45 seconds", ConnectServer.ListWait == TimeSpan.FromSeconds(45));
            var server = Source("ConnectServer.cs") ?? "";
            var doc = Regex.Match(server, @"(///[^\n]*\n\s*)+public async Task<List<ConnectEntry>\?> List\(").Value;
            Check("the comment on List says what it waits", doc.Contains("ListWait") && !doc.Contains("ten seconds"), doc);
            var m = Regex.Match(js, @"'/api/list'[^\n]*timeout:\s*(\d+)");
            Check("and the phone waits longer than that",
                  m.Success && int.Parse(m.Groups[1].Value) > ConnectServer.ListWait.TotalMilliseconds, m.Value);
        }

        /// <summary>Raw .aac went as MP4 and .m4r as octet-stream, both straight to the phone's player.</summary>
        private static void ContentTypeTests()
        {
            Check(".aac is typed as ADTS AAC", ConnectServer.ContentType(@"C:\a.aac") == "audio/aac", ConnectServer.ContentType(@"C:\a.aac"));
            Check(".m4r is typed as MP4 audio", ConnectServer.ContentType(@"C:\a.m4r") == "audio/mp4");
            Check(".m4a still is", ConnectServer.ContentType(@"C:\a.m4a") == "audio/mp4");
        }

        /// <summary>A relative PATH entry was the current folder's, so a tailscale.exe planted there could be run.</summary>
        private static void FindExeIgnoresRelativePathTests()
        {
            var body = Method(Source("TailscaleWeb.cs") ?? "", "public static string? FindExe(");
            Check("FindExe skips PATH entries that are not full paths", body.Contains("IsPathFullyQualified"), body.Length > 0 ? null : "no FindExe");
        }

        /// <summary>
        /// A move out of Drive into a folder whose name ends in a dot checked the copy by its plain path, which is
        /// the namesake's, so every original was kept as "could not be shown to hold everything".
        /// </summary>
        private static void MoveOutOfDriveChecksLiteralPathTests()
        {
            var body = Method(Source("ConnectFiles.cs") ?? "", " FinishMoveOutOfDrive(ConnectJob job,");
            Check("FinishMoveOutOfDrive is there", body.Length > 0);
            Check("the copy of a file is looked at by its literal path", body.Contains("new FileInfo(Io(arrivedAt))"));
            Check("and a folder's copy is checked by it too", body.Contains("VerifyCopied(path, Io(arrivedAt)"));
            Check("no plain FileInfo of where it arrived is left", !body.Contains("new FileInfo(arrivedAt)"));
        }

        private sealed class Files : IConnectFiles
        {
            public readonly List<string> Calls = new();
            public string? Unavailable(string path) => null;
            public (long Used, long Limit)? DriveQuota() => null;
            public Task<ConnectSize> SizeAsync(string folder, CancellationToken token) { Calls.Add("size"); return Task.FromResult(new ConnectSize(folder, 0, 0, 0, true)); }
            public Task<ConnectStat> StatAsync(string path, CancellationToken token) { Calls.Add("stat"); return Task.FromResult(new ConnectStat(path, "x", true, 0, DateTime.UtcNow, DateTime.UtcNow, false, false)); }
            public Task<string> RenameAsync(string path, string newName, CancellationToken token) { Calls.Add("rename"); return Task.FromResult(path); }
            public Task<string> CreateFolderAsync(string parent, string name, CancellationToken token) { Calls.Add("mkdir"); return Task.FromResult(parent); }
            public Task<ConnectDeleted> DeleteAsync(IReadOnlyList<string> paths, CancellationToken token) { Calls.Add("delete"); return Task.FromResult(new ConnectDeleted(paths.Count, Array.Empty<ConnectFailure>())); }
            public void CheckTransfer(IReadOnlyList<string> paths, string destination, bool move) { }
            public Task TransferAsync(ConnectJob job, IReadOnlyList<string> paths, string destination, bool move, PasteConflictPolicy conflict) { Calls.Add("transfer"); return Task.CompletedTask; }
            public Task<string> UploadAsync(string folder, string name, PasteConflictPolicy conflict, Stream body, CancellationToken token) { Calls.Add("upload"); return Task.FromResult(folder); }
            public bool OnDrive(string path) => false;
            public Task<string> PlaceFileAsync(string file, string folder, string name, PasteConflictPolicy conflict, Action<long, long>? progress, CancellationToken token) => Task.FromResult(folder);
            public IReadOnlyList<string> AudioExtensions() => new[] { ".flac" };
            public Task<Dictionary<string, object?>> DetailsAsync(string path, ConnectStat stat, bool hash, CancellationToken token) => Task.FromResult(new Dictionary<string, object?>());
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        /// <summary>
        /// "C:\x\a.txt:secret" is one of a file's alternate data streams. Every route took it as a path: opened, it
        /// was read; deleted, the Recycle Bin was handed a stream. Refused now before anything reaches the files.
        /// </summary>
        private static async Task StreamPathTests()
        {
            Check("a colon past the drive's is a stream path", ConnectServer.IsStreamPath(@"C:\x\a.txt:secret"));
            Check("a plain path is not", !ConnectServer.IsStreamPath(@"C:\x\a.txt") && !ConnectServer.IsStreamPath(@"C:\"));
            Check("nor a bare drive or nothing", !ConnectServer.IsStreamPath("C:") && !ConnectServer.IsStreamPath(null));

            var dir = Path.Combine(Path.GetTempPath(), "en-sweep309-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "a.txt");
            File.WriteAllText(file, "main");
            try { File.WriteAllText(file + ":secret", "hidden"); } catch { }

            int port = FreePort();
            var files = new Files();
            var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port, files: files,
                uploads: Path.Combine(dir, "uploads"), clipboardSends: Path.Combine(dir, "sends"));
            try
            {
                server.Start();
                bool listening = false;
                for (int i = 0; i < 50 && !listening; i++)
                {
                    try { using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, port); listening = true; }
                    catch { await Task.Delay(50); }
                }
                if (!listening) { Check("the server listens", false); return; }

                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(15) };
                async Task<(int Status, string Body)> Send(HttpMethod method, string url, string? json = null)
                {
                    var request = new HttpRequestMessage(method, url);
                    request.Headers.Add("X-Connect-Code", Code);
                    if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = await http.SendAsync(request);
                    return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
                }

                var stream = Uri.EscapeDataString(file + ":secret");
                var (fileStatus, fileBody) = await Send(HttpMethod.Get, "/api/file?path=" + stream);
                Check("a stream is not served as a file", fileStatus == 400 && !fileBody.Contains("hidden"), $"{fileStatus} {fileBody}");
                var (plainStatus, plainBody) = await Send(HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(file));
                Check("the file itself still is", plainStatus == 200 && plainBody == "main", $"{plainStatus} {plainBody}");

                var (listStatus, _) = await Send(HttpMethod.Get, "/api/list?path=" + Uri.EscapeDataString(dir + ":x"));
                Check("a stream is not listed as a folder", listStatus == 404, listStatus.ToString());

                var paths = System.Text.Json.JsonSerializer.Serialize(new { paths = new[] { file + ":secret" } });
                var (deleteStatus, deleteBody) = await Send(HttpMethod.Post, "/api/delete", paths);
                Check("a stream is refused for delete with a sentence", deleteStatus == 400 && deleteBody.Contains("part of a file"), $"{deleteStatus} {deleteBody}");
                var rename = System.Text.Json.JsonSerializer.Serialize(new { path = file + ":secret", newName = "b.txt" });
                var (renameStatus, _) = await Send(HttpMethod.Post, "/api/rename", rename);
                Check("and for rename", renameStatus == 400, renameStatus.ToString());
                Check("none of it reached the files", !files.Calls.Contains("delete") && !files.Calls.Contains("rename"), string.Join(",", files.Calls));
            }
            finally
            {
                server.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }

    // ---- merged from Sweep500WebTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// The 5.0.0 sweep of the web app.
    /// </summary>
    internal static class Sweep500WebTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the web app:");
            WebDeleteNeverAsksOnThePc();
            AudioThatStopsArrivingFails();
            LongAudioIsNotCut();
            AacDetailsAreNotMp3();
            DeepMatroskaDoesNotCrash();
            DetailsNeverSpeakOnThePc();
            MoveOutOfDriveThatFailedSaysCopied();
            MoveInsideDriveCountsWhatMoved();
            OwnerTrustNeedsTheTailscaleService();
            EveryLoopbackAddressSharesOneCount();
            await Task.CompletedTask;
        }

        // ---------------- 10. owner trust only from the Tailscale service, not anything named tailscaled ----------------

        private static void OwnerTrustNeedsTheTailscaleService()
        {
            const int service = 3844;
            var svc = new ProcessFacts(service, 700, 0, 1000, "tailscaled.exe");
            var child = new ProcessFacts(5532, service, 0, 2000, "tailscaled.exe");
            Check("owner trust: the Tailscale service's own process is Tailscale", ConnectServer.IsTailscaleService(svc, null, service));
            Check("owner trust: the tailscaled the service started is Tailscale", ConnectServer.IsTailscaleService(child, svc, service));
            var copy = new ProcessFacts(9100, 4200, 0, 3000, "tailscaled.exe");
            var copier = new ProcessFacts(4200, 700, 0, 2500, "svchost.exe");
            Check("owner trust: a tailscaled a service account started in session 0 is not, so it needs the pairing code",
                !ConnectServer.IsTailscaleService(copy, copier, service));
            var reused = new ProcessFacts(service, 700, 0, 5000, "tailscaled.exe");
            Check("owner trust: a child whose parent id was reused by a newer service process is not",
                !ConnectServer.IsTailscaleService(child with { CreateTime = 4000 }, reused, service));
            Check("owner trust: with no Tailscale service running, nothing is", !ConnectServer.IsTailscaleService(child, svc, 0));
            Check("owner trust: something else the service started is not", !ConnectServer.IsTailscaleService(child with { Name = "cmd.exe" }, svc, service));

            // Live, on this PC: the snapshot reads, the suite itself is not Tailscale, and if Tailscale runs here
            // one of its processes passes (so the owner is still let in without the code).
            var table = ProcessTable.Snapshot();
            int me = Environment.ProcessId;
            Check("owner trust: the process snapshot finds this process with its parent and start time",
                table.TryGetValue(me, out var self) && self.CreateTime > 0 && self.Name.Length > 0, table.Count.ToString());
            Check("owner trust: this suite is not taken for Tailscale",
                !ConnectServer.IsTailscaleService(self, table.TryGetValue(self.ParentPid, out var sp) ? sp : null, TailscaleServiceProcess.Pid()));
            int live = TailscaleServiceProcess.Pid();
            if (live > 0)
            {
                bool any = false;
                foreach (var f in table.Values)
                    if (ConnectServer.IsTailscaleService(f, table.TryGetValue(f.ParentPid, out var pf) ? pf : null, live)) any = true;
                Check("owner trust: the Tailscale service running on this PC passes", any, $"service {live}");
            }
            else Console.WriteLine("  (no Tailscale service running here; the live check is skipped)");
        }

        // ---------------- 11. every loopback address shares one count of wrong codes ----------------

        private static void EveryLoopbackAddressSharesOneCount()
        {
            var one = ConnectServer.AddressKey(System.Net.IPAddress.Loopback);
            Check("pairing code: 127.0.0.2, 127.45.6.7 and ::ffff:127.0.0.3 count as 127.0.0.1, so new addresses get no new guesses",
                ConnectServer.AddressKey(System.Net.IPAddress.Parse("127.0.0.2")) == one &&
                ConnectServer.AddressKey(System.Net.IPAddress.Parse("127.45.6.7")) == one &&
                ConnectServer.AddressKey(System.Net.IPAddress.Parse("::ffff:127.0.0.3")) == one &&
                ConnectServer.AddressKey(System.Net.IPAddress.IPv6Loopback) == one, one);
            Check("pairing code: another address keeps a count of its own",
                ConnectServer.AddressKey(System.Net.IPAddress.Parse("100.64.0.9")) == "addr:100.64.0.9");
        }

        // ---------------- 8. a move out of Drive with a failure says nothing left Drive ----------------

        private static void MoveOutOfDriveThatFailedSaysCopied()
        {
            var job = new ConnectJob("d", "move");
            job.Counted(9, 1);
            job.Fail(@"G:\a\y.txt", "It is open in another program.");
            job.KeepOriginals();
            var (state, message) = ConnectServer.Outcome(job, move: true);
            Check("move out of Drive with one failure: says Copied 9 files, and that the originals are all still on Drive, never Moved",
                state == "done" && message != null && message.StartsWith("Copied 9 files; 1 file could not be copied.", StringComparison.Ordinal) &&
                message.Contains("Google Drive", StringComparison.Ordinal) && !message.Contains("Moved", StringComparison.Ordinal), $"{state}: {message}");

            var local = new ConnectJob("l", "move");
            local.Counted(2, 1);
            local.Fail(@"C:\a\y.txt", "It is open in another program.");
            var (_, localSaid) = ConnectServer.Outcome(local, move: true);
            Check("a local move with one failure still says Moved", localSaid?.StartsWith("Moved 2 files; 1 file could not be moved.", StringComparison.Ordinal) == true, localSaid);

            var log = new ConflictLog();
            for (int i = 0; i < 33; i++) log.Skipped($"name {i}");
            var said = ConnectFiles.MoveOutSentence("Copied out of Google Drive. Some items were skipped, so nothing was removed from Drive.", log.Snapshot(), 0, true);
            Check("move out of Drive with over 32 skipped: says both that nothing was removed and what was skipped",
                said != null && said.Contains("nothing was removed from Drive", StringComparison.Ordinal) && said.Contains("33", StringComparison.Ordinal), said);

            Check("the window says the same: a failed move out of Drive is a copy with nothing removed from Google Drive",
                NameRules.PartialTransferSentence(2, 1, move: true, fromDrive: true).Contains("nothing was removed from Google Drive", StringComparison.Ordinal));
        }

        // ---------------- 9. a move inside Drive counts what moved, not what was tried ----------------

        private static void MoveInsideDriveCountsWhatMoved()
        {
            var body = Slice(Source(Path.Combine("src", "ConnectFiles.cs")), "async Task DriveToDrive(", "private async Task<int> OnDrive(");
            Check("web move inside Drive: the source of DriveToDrive was found", body.Length > 0);
            Check("web move inside Drive gives the job OnDrive's count of what really moved",
                body.Contains("= await OnDrive(", StringComparison.Ordinal) && body.Contains("job.Counted(", StringComparison.Ordinal));

            // What DriveToDrive now leaves behind when all three moves are refused.
            var job = new ConnectJob("m", "move");
            job.Report(3, 3, 0, 0, "");
            for (int i = 0; i < 3; i++) job.Fail($@"G:\a\{i}.txt", "You do not have permission to add anything to that folder.");
            job.Counted(0, 3);
            var (state, _) = ConnectServer.Outcome(job, move: true);
            Equal("a move inside Drive where every item was refused is failed, not done", "failed", state);
        }

        // ---------------- 6. web details never ask Drive for a link, whose failure the PC would say ----------------

        private static void DetailsNeverSpeakOnThePc()
        {
            var body = Slice(Source(Path.Combine("src", "ConnectFiles.cs")), "public async Task<Dictionary<string, object?>> DetailsAsync(", "public async Task<string> RenameAsync(");
            Check("web details: the source of DetailsAsync was found", body.Length > 0);
            Check("web details ask Drive for no share link, so Details on Shared drives or Trash from the phone says nothing on the PC",
                !body.Contains("LinkFor(", StringComparison.Ordinal));
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-sweep500web-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static System.Collections.Generic.Dictionary<string, object?>? DetailsOf(string path)
        {
            using var source = new FileRangeSource(path);
            var bytes = new CachedBytes(source, CancellationToken.None);
            return ConnectDetails.Media(bytes, path, CancellationToken.None);
        }

        private static string AudioValue(System.Collections.Generic.Dictionary<string, object?>? media, string key)
        {
            if (media == null || !media.TryGetValue("audio", out var a) || a is not System.Collections.IEnumerable list) return "";
            foreach (var s in list)
                if (s is System.Collections.Generic.IDictionary<string, object?> d && d.TryGetValue(key, out var v)) return v?.ToString() ?? "";
            return "";
        }

        // ---------------- 4. an .aac file's details are not read as MP3 ----------------

        private static void AacDetailsAreNotMp3()
        {
            var dir = TempDir();
            try
            {
                // ADTS as encoders write it: buffer fullness 0x7FF, and frames whose length ends in 7 bits set, so
                // the header's bytes 5 and 6 are FF FC, a perfect MPEG-1 layer II header; stereo payloads start 0x21.
                var rng = new Random(5);
                var file = new MemoryStream();
                for (int k = 0; k < 400; k++)
                {
                    int len = 7 + 8 * (40 + rng.Next(20)) + (k % 8 == 0 ? 7 : rng.Next(8));
                    var frame = new byte[len];
                    rng.NextBytes(frame);
                    frame[0] = 0xFF; frame[1] = 0xF1; frame[2] = 0x4C;
                    frame[3] = (byte)(0x80 | (len >> 11));
                    frame[4] = (byte)(len >> 3);
                    frame[5] = (byte)(((len & 7) << 5) | 0x1F);
                    frame[6] = 0xFC;
                    frame[7] = 0x21;
                    file.Write(frame);
                }
                var aac = Path.Combine(dir, "song.aac");
                File.WriteAllBytes(aac, file.ToArray());
                var media = DetailsOf(aac);
                string container = media != null && media.TryGetValue("container", out var c) ? c?.ToString() ?? "" : "";
                string codec = AudioValue(media, "codec");
                Check("details: an ADTS .aac file is not called MPEG audio, MP3 or MPEG layer 2",
                    container != "MPEG audio" && codec != "MP3" && !codec.StartsWith("MPEG layer", StringComparison.Ordinal), $"[{container}] [{codec}]");

                var mp3 = Path.Combine(AppContext.BaseDirectory, "Fixtures", "clicks-vbr.mp3");
                var real = DetailsOf(mp3);
                Check("details: a real MP3 is still read as MP3 by its own header",
                    real != null && real.TryGetValue("container", out var rc) && rc?.ToString() == "MPEG audio" && AudioValue(real, "codec") == "MP3",
                    real == null ? "null" : $"{(real.TryGetValue("container", out var x) ? x : "")} {AudioValue(real, "codec")}");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------------- 5. a crafted Matroska file nested deep does not overflow the stack ----------------

        private static void DeepMatroskaDoesNotCrash()
        {
            var dir = TempDir();
            try
            {
                var f = new MemoryStream();
                void W(params byte[] b) => f.Write(b);
                void Sized(byte[] id, byte[] body)
                {
                    W(id);
                    long n = body.Length;
                    W(0x01, 0, 0, 0, 0, (byte)(n >> 16), (byte)(n >> 8), (byte)n);
                    W(body);
                }
                byte[] Repeat(byte a, byte b, int times)
                {
                    var r = new byte[times * 3];
                    for (int i = 0; i < times; i++) { r[3 * i] = a; r[3 * i + 1] = b; r[3 * i + 2] = 0xFF; }
                    return r;
                }
                W(0x1A, 0x45, 0xDF, 0xA3, 0x8B, 0x42, 0x82, 0x88);
                W(System.Text.Encoding.ASCII.GetBytes("matroska"));
                W(0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
                Sized(new byte[] { 0x10, 0x43, 0xA7, 0x70 }, Repeat(0x45, 0xB9, 200_000));
                Sized(new byte[] { 0x12, 0x54, 0xC3, 0x67 }, Repeat(0x73, 0x73, 200_000));
                var mkv = Path.Combine(dir, "deep.mkv");
                File.WriteAllBytes(mkv, f.ToArray());

                System.Collections.Generic.Dictionary<string, object?>? media = null;
                Exception? failed = null;
                var thread = new Thread(() => { try { media = DetailsOf(mkv); } catch (Exception e) { failed = e; } }, 256 * 1024);
                thread.Start();
                bool ended = thread.Join(TimeSpan.FromSeconds(60));
                Check("details: a Matroska file with chapters and tags nested 200,000 deep is read on a small stack without crashing the app",
                    ended && failed == null, failed?.Message);
                Check("details: and the nesting gives no chapters", media == null || !media.ContainsKey("chapters"));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------------- helpers ----------------

        private static string? Source(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            try { return File.ReadAllText(Path.Combine(dir.FullName, relative)); } catch { return null; }
        }

        /// <summary>The text from <paramref name="start"/> up to <paramref name="end"/>, or empty.</summary>
        private static string Slice(string? text, string start, string end)
        {
            if (text == null) return "";
            int a = text.IndexOf(start, StringComparison.Ordinal);
            if (a < 0) return "";
            int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
            return b < 0 ? text[a..] : text[a..b];
        }

        // ---------------- 1. web delete never puts a question on the PC ----------------

        private static void WebDeleteNeverAsksOnThePc()
        {
            var body = Slice(Source(Path.Combine("src", "ConnectFiles.cs")), "public async Task<ConnectDeleted> DeleteAsync(", "[StructLayout");
            Check("web delete: the source of DeleteAsync was found", body.Length > 0);
            Check("web delete recycles without any window, so a file too big for the bin is kept rather than asked about on the PC",
                body.Contains("ShellDelete.RecycleWithoutUi(", StringComparison.Ordinal) &&
                !body.Contains("ShellDelete.Recycle(", StringComparison.Ordinal));
        }

        // ---------------- 2. /api/audio fails, rather than going silent, when the bytes stop ----------------

        /// <summary>Serves a file's bytes up to a cut, then fails: no bytes, or an exception.</summary>
        private sealed class CutSource : IRangeSource
        {
            private readonly byte[] _bytes;
            private readonly bool _throw;
            /// <summary>Where the bytes stop coming; set after opening, as Google stalls part way through playing.</summary>
            public long Cut = long.MaxValue;
            public int Refused;
            public CutSource(byte[] bytes, bool throws) { _bytes = bytes; _throw = throws; }
            public long Length => _bytes.Length;
            public int Streams => 1;
            public Task<int> ReadAsync(long offset, Memory<byte> into, CancellationToken token)
            {
                if (offset >= Cut)
                {
                    Refused++;
                    if (_throw) throw new IOException("Google stopped answering");
                    return Task.FromResult(0);
                }
                int n = (int)Math.Min(into.Length, Math.Min(Cut, _bytes.Length) - offset);
                _bytes.AsSpan((int)offset, n).CopyTo(into.Span);
                return Task.FromResult(n);
            }
            public void Dispose() { }
        }

        private static byte[]? OpusTone(int seconds)
        {
            try
            {
                const int rate = 48000;
                var pcm = new short[rate * seconds * 2];
                for (int i = 0; i < rate * seconds; i++)
                {
                    short v = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000);
                    pcm[2 * i] = v;
                    pcm[2 * i + 1] = v;
                }
                using var file = new MemoryStream();
                var encoder = Concentus.OpusCodecFactory.CreateEncoder(48000, 2, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO, null);
                var writer = new Concentus.Oggfile.OpusOggWriteStream(encoder, file, null, 48000, 2, false);
                writer.WriteSamples(pcm, 0, pcm.Length);
                writer.Finish();
                return file.ToArray();
            }
            catch (Exception e) { Check("an Opus tone could be made", false, e.Message); return null; }
        }

        private static void AudioThatStopsArrivingFails()
        {
            var bytes = OpusTone(20);
            if (bytes == null) return;

            using (var whole = ConnectAudio.Open("tone.opus", new CutSource(bytes, false), bytes.Length, out var why))
            {
                Check("audio: a Drive track that arrives whole opens", whole != null, why);
                if (whole != null)
                {
                    var ms = new MemoryStream();
                    string? failed = null;
                    try { whole.WriteRange(0, whole.Plan.TotalBytes - 1, ms, CancellationToken.None); }
                    catch (Exception e) { failed = e.Message; }
                    Check("audio: a track that arrives whole is served whole, with no error", failed == null && ms.Length == whole.Plan.TotalBytes,
                        $"{failed} {ms.Length} vs {whole.Plan.TotalBytes}");
                }
            }

            foreach (var throws in new[] { false, true })
            {
                string how = throws ? "with an error" : "with no bytes";
                var source = new CutSource(bytes, throws);
                using var audio = ConnectAudio.Open("tone.opus", source, bytes.Length, out var why);
                source.Cut = bytes.Length * 2 / 5;
                Check($"audio: a track that stops arriving {how} still opens", audio != null, why);
                if (audio == null) continue;
                var ms = new MemoryStream();
                Exception? caught = null;
                try { audio.WriteRange(0, audio.Plan.TotalBytes - 1, ms, CancellationToken.None); }
                catch (Exception e) { caught = e; }
                Check($"audio: a track that stops arriving {how} fails the response, so the phone retries, instead of playing silence to the end",
                    caught is IOException && ms.Length < audio.Plan.TotalBytes, $"{caught?.GetType().Name} {ms.Length} of {audio.Plan.TotalBytes}, {source.Refused} reads refused");
            }
        }

        // ---------------- 3. a long track is not cut at the 4 GB WAV limit ----------------

        private static void LongAudioIsNotCut()
        {
            const long most = uint.MaxValue - 36L;
            var three = ConnectAudio.PlanFor(48000, 2, 3L * 3600 * 48000);
            Check("audio: a three-hour track stays 24-bit stereo at its own rate",
                three.Bits == 24 && three.Channels == 2 && three.Rate == 48000 && three.Frames == 3L * 3600 * 48000, three.ToString());

            var six = ConnectAudio.PlanFor(48000, 2, 6L * 3600 * 48000);
            Check("audio: a six-hour Opus audiobook is served whole, as 16-bit, under the WAV limit",
                six.Frames / (double)six.Rate >= 6 * 3600 - 1 && six.DataBytes <= most && six.Bits == 16, six.ToString());

            var twenty = ConnectAudio.PlanFor(48000, 2, 20L * 3600 * 48000);
            Check("audio: a twenty-hour track is served whole, mono at a lower rate, under the WAV limit",
                twenty.Frames / (double)twenty.Rate >= 20 * 3600 - 1 && twenty.DataBytes <= most && twenty.Channels == 1 && twenty.Rate < 48000,
                twenty.ToString());

            var h = ConnectAudio.Header(six);
            Check("audio: a 16-bit plan's header says 16 bits, with the block and byte rate to match",
                BitConverter.ToUInt16(h, 34) == 16 && BitConverter.ToUInt16(h, 32) == six.Channels * 2 &&
                BitConverter.ToUInt32(h, 28) == six.Rate * six.Channels * 2 && BitConverter.ToUInt32(h, 40) == six.DataBytes);
        }
    }

    // ---- merged from Sweep500WebJsTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// The 5.0.0 sweep of the web app's page. There is no JS runner, so these read web/app.js and pin the decisions.
    /// </summary>
    internal static class Sweep500WebJsTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the web app's page:");
            var js = WebAssets.TryGet("/app/app.js", out var bytes, out _) ? Encoding.UTF8.GetString(bytes) : "";
            Check("app.js is embedded", js.Length > 1000);

            SlowDeleteRefreshesTests(js);
            MoveHereKeepsFocusTests(js);
            UploadServerErrorEndsTests(js);
            StopAllStopsTheBatchTests(js);
            UnplayableTrackIsSkippedTests(js);
            StopWhileWaitingStaysStoppedTests(js);
            ClipboardSendScalesWithSizeTests(js);
            await Task.CompletedTask;
        }

        /// <summary>A function of app.js, from its declaration to the next top-level one.</summary>
        private static string Function(string js, string name)
        {
            var m = Regex.Match(js, @"^(async )?function " + Regex.Escape(name) + @"\(", RegexOptions.Multiline);
            if (!m.Success) return "";
            var next = Regex.Match(js[(m.Index + m.Length)..], @"^(async )?function |^const |^let |^// -", RegexOptions.Multiline);
            return next.Success ? js.Substring(m.Index, m.Length + next.Index) : js[m.Index..];
        }

        /// <summary>Text from the first occurrence of <paramref name="from"/> on, or empty.</summary>
        private static string After(string text, string from)
        {
            int at = text.IndexOf(from, StringComparison.Ordinal);
            return at < 0 ? "" : text[at..];
        }

        // Bug 1: a big Drive delete outlasted the fixed two-minute wait, then said the PC could not be reached
        // and left the deleted rows showing.
        private static void SlowDeleteRefreshesTests(string js)
        {
            var body = Function(js, "remove");
            var line = Regex.Match(body, @"api\('/api/delete'[^\n]*").Value;
            Check("a delete's wait grows with how many items it deletes", Regex.IsMatch(line, @"timeout:[^}\n]*paths\.length"), line);
            var caught = After(body, "catch (err)");
            Check("a delete with no answer is told apart from a refusal", Regex.IsMatch(caught, @"status\s*!==?\s*0|status\s*===?\s*0"), caught);
            Check("a delete with no answer refreshes the folder", caught.Contains("openFolder("), caught);
            Check("and leaves select mode", caught.Contains("selectMode = false"), caught);
        }

        // Bug 2: Move here hid the button that had focus, and focus fell to the page.
        private static void MoveHereKeepsFocusTests(string js)
        {
            var after = After(Function(js, "paste"), "state.clip = null");
            Check("after Move here, focus is put on something still shown",
                after.Contains(".focus()") && (after.Contains("btn-select") || after.Contains("files-title")), after);
        }

        // Bug 3: a chunk the PC kept refusing with a 500 (its disk full) was retried for ever as "waiting for the PC".
        private static void UploadServerErrorEndsTests(string js)
        {
            var chunk = After(Function(js, "upload"), "/api/upload/chunk");
            int wait = chunk.IndexOf("waitForNetwork(", StringComparison.Ordinal);
            var handling = wait < 0 ? "" : chunk[..wait];
            Check("a chunk refused by the PC ends the upload after a few tries",
                Regex.IsMatch(handling, @"\w+\s*>=?\s*\d") && handling.Contains("throw err"), handling);
            Check("only no answer at all is waited out without a limit", !Regex.IsMatch(handling, @"status\s*<\s*500\)\s*throw err"), handling);
            Check("an upload that cannot go on is called off on the PC", handling.Contains("/api/upload/cancel"), handling);
        }

        // Bug 4: Stop all stopped only the file going up; the rest of a multi-file upload went on.
        private static void StopAllStopsTheBatchTests(string js)
        {
            var many = Function(js, "uploadFiles");
            var loop = After(many, "for (const f of files)");
            int up = loop.IndexOf("await upload(", StringComparison.Ordinal);
            var beforeEach = up < 0 ? "" : loop[..up];
            var flag = Regex.Match(beforeEach, @"if \((\w+)\.stopped\)").Groups[1].Value;
            Check("a multi-file upload looks for Stop before each file", flag.Length > 0, beforeEach);
            var stop = After(js, "$('tr-stop').addEventListener");
            stop = stop.Length > 400 ? stop[..400] : stop;
            Check("Stop all marks the multi-file uploads stopped", Regex.IsMatch(stop, @"\.stopped = true"), stop);
        }

        // Bug 5: a track the PC refused or the browser could not decode was retried for half a minute, blamed on the
        // connection, and stopped the whole queue.
        private static void UnplayableTrackIsSkippedTests(string js)
        {
            var from = After(js, "a.addEventListener('error'");
            int end = from.IndexOf("a.addEventListener('playing'", StringComparison.Ordinal);
            var handler = end < 0 ? from : from[..end];
            // The skip helper is defined just above the handler, in the same setup.
            int start = js.IndexOf("a.addEventListener('error'", StringComparison.Ordinal);
            var setup = start < 0 ? "" : js[Math.Max(0, start - 600)..start] + handler;
            Check("a player error reads the audio element's error", handler.Contains("a.error"), handler);
            Check("a player error asks the PC what it says about the track", Regex.IsMatch(handler, @"\b(timedFetch|fetch)\(src"), handler);
            Check("a track that won't play is skipped and the queue goes on", setup.Contains("step(1)") && handler.Contains("skip("), setup);
            Check("a queue where nothing plays stops rather than loops", Regex.IsMatch(setup, @"\+\+\w+\s*>=?"), setup);
            var src = Function(js, "srcFor");
            Check("a track this browser can't play goes through the PC's decoder", src.Contains("viaPc") && src.Contains("/api/audio"), src);
        }

        // Bug 6: Stop pressed while a transfer waited for the network was undone when the network came back.
        private static void StopWhileWaitingStaysStoppedTests(string js)
        {
            foreach (var name in new[] { "downloadToPhone", "uploadSmall", "upload" })
            {
                var body = Function(js, name);
                int waits = 0, unchecked_ = 0;
                foreach (Match m in Regex.Matches(body, @"await waitForNetwork\("))
                {
                    waits++;
                    var rest = body[m.Index..];
                    int running = rest.IndexOf("t.state = 'running'", StringComparison.Ordinal);
                    if (running >= 0 && !rest[..running].Contains("if (stopped)")) unchecked_++;
                }
                Check($"{name}: Stop pressed while waiting for the network is looked at before it carries on",
                    waits > 0 && unchecked_ == 0, $"{waits} waits, {unchecked_} not checked");
            }
            var download = Function(js, "downloadToPhone");
            int safari = download.IndexOf("handToSafari(url", StringComparison.Ordinal);
            int fetched = download.IndexOf("await fetch(", StringComparison.Ordinal);
            Check("a big download stopped while it was asked for is not handed to Safari",
                safari > fetched && fetched >= 0 && download[fetched..safari].Contains("if (stopped)"), download);
        }

        // Bug 7: sending a file to the PC clipboard gave up after five minutes whatever its size, with no Stop.
        private static void ClipboardSendScalesWithSizeTests(string js)
        {
            var body = Function(js, "sendFiles");
            var call = After(body, "/api/clipboard/send?");
            int end = call.IndexOf("});", StringComparison.Ordinal);
            call = end < 0 ? call : call[..end];
            Check("a clipboard send's time limit grows with the file's size", Regex.IsMatch(call, @"timeout:[^,}]*\.size"), call);
            Check("a clipboard send can be stopped", call.Contains("signal:"), call);
            Check("a clipboard send shows in Transfers", body.Contains("addTransfer("), body);
        }
    }

    // ---- merged from Release510WebTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

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
            Check("Delete in the trash says clearly that it deletes forever", Regex(js, @"if \(paths\.some\(underDriveTrash\)\)\s*\{\s*await dialog\(\{ title: 'Delete forever', body: 'Deleting from the Google Drive trash deletes it forever"));
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

    // ---- merged from WebArchiveAudioFixTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// Regression tests for the web app, archive and audio player review of
    /// 2026-10-13: one per finding, each watched failing before its fix.
    /// </summary>
    internal static class WebArchiveAudioFixTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;
            Console.WriteLine("Web app, archive and player fixes (2026-10-13):");

            await SpoofedLoginStillCountsWrongCodesTests();
            await LockedFileStillHasDetailsTests();
            await TrailingDotFolderArchiveDetailsTests();
            OggRunawayPacketTests();
        }

        // MARK: Audio

        /// <summary>
        /// Ogg pages whose lacing all says "continues" were appended to one packet with no limit, so a damaged
        /// or hostile file grew it until memory ran out. Past the cap the packet is dropped and reading goes on.
        /// </summary>
        private static void OggRunawayPacketTests()
        {
            const int serial = 0x1234;
            var file = new MemoryStream();
            int sequence = 0;
            void PageOf(byte flags, byte[] lacing, byte[] body, long granule)
            {
                var head = new byte[27 + lacing.Length];
                head[0] = (byte)'O'; head[1] = (byte)'g'; head[2] = (byte)'g'; head[3] = (byte)'S';
                head[5] = flags;
                BitConverter.GetBytes(granule).CopyTo(head, 6);
                BitConverter.GetBytes(serial).CopyTo(head, 14);
                BitConverter.GetBytes(sequence++).CopyTo(head, 18);
                head[26] = (byte)lacing.Length;
                lacing.CopyTo(head, 27);
                uint crc = OggCrc(OggCrc(0, head), body);
                BitConverter.GetBytes(crc).CopyTo(head, 22);
                file.Write(head);
                file.Write(body);
            }
            var full = Enumerable.Repeat((byte)255, 255).ToArray();
            var fullBody = new byte[255 * 255];
            int pages = (2 << 20) / fullBody.Length + 1;   // a little over 2 MB, all one packet
            for (int i = 0; i < pages; i++) PageOf(i == 0 ? (byte)2 : (byte)1, full, fullBody, -1);
            PageOf(1, new byte[] { 10 }, new byte[10], 960);        // the runaway packet's end
            PageOf(0, new byte[] { 7 }, new byte[7], 1920);         // and a normal packet after it

            file.Position = 0;
            var reader = new OggPages(file) { Serial = serial, MostPacketBytes = 1 << 20 };
            reader.Restart(0);
            var packets = new List<byte[]>();
            int reads = 0;
            while (reads++ < pages + 10 && reader.ReadPackets(packets, out _, out _, out _)) { }
            int longest = packets.Count == 0 ? 0 : packets.Max(p => p.Length);
            Check("an Ogg packet running on past the cap is dropped, not assembled", longest <= 1 << 20, $"longest {longest} bytes");
            Check("and the packet after it still arrives", packets.Count > 0 && packets[^1].Length == 7,
                string.Join(",", packets.Select(p => p.Length)));
        }

        private static uint OggCrc(uint crc, byte[] data)
        {
            foreach (var b in data)
            {
                uint r = ((crc >> 24) ^ b) << 24;
                for (int bit = 0; bit < 8; bit++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
                crc = (crc << 8) ^ r;
            }
            return crc;
        }

        /// <summary>
        /// A tar inside a folder whose name ends in a dot ("dir.") was listed for the web app's details by
        /// its plain path, which Windows trims to "dir": the entries of a different archive were reported.
        /// </summary>
        private static async Task TrailingDotFolderArchiveDetailsTests()
        {
            var root = Path.Combine(Path.GetTempPath(), "en-dotdir-" + Guid.NewGuid().ToString("N")[..8]);
            var dotted = Path.Combine(root, "dir.");
            var plain = Path.Combine(root, "dir");
            Directory.CreateDirectory(NameRules.LiteralPath(dotted));
            Directory.CreateDirectory(plain);
            try
            {
                static void Tar(string path, int members)
                {
                    using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
                    using var w = new System.Formats.Tar.TarWriter(file, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: false);
                    for (int i = 0; i < members; i++)
                    {
                        var e = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, $"m{i}.txt")
                        { DataStream = new MemoryStream(Encoding.ASCII.GetBytes("member " + i)) };
                        w.WriteEntry(e);
                    }
                }
                var target = Path.Combine(dotted, "b.tar");
                Tar(NameRules.LiteralPath(target), 3);
                Tar(Path.Combine(plain, "b.tar"), 1);

                var files = new ConnectFiles(null, new Settings());
                var stat = await files.StatAsync(target, CancellationToken.None);
                var details = await files.DetailsAsync(target, stat, false, CancellationToken.None);
                object? entries = details.TryGetValue("archive", out var a) && a is Dictionary<string, object?> archive
                    && archive.TryGetValue("entries", out var n) ? n : null;
                _equal("a tar in a folder named with a trailing dot is listed itself, not its trimmed namesake", "3", entries?.ToString() ?? "none");
            }
            finally
            {
                try { Directory.Delete(NameRules.LiteralPath(root), true); } catch { }
            }
        }

        /// <summary>
        /// The "does it look like text" sniff of a file with no text extension ran outside every section's
        /// try, so a file another program held shut (or one Drive would not open) threw out of DetailsAsync
        /// and the web app's details lost everything, the file section it already had included.
        /// </summary>
        private static async Task LockedFileStillHasDetailsTests()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-details-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                var p = Path.Combine(dir, "notes.bin");
                File.WriteAllText(p, "plain words, a line of them\r\n");
                var files = new ConnectFiles(null, new Settings());
                var stat = await files.StatAsync(p, CancellationToken.None);
                Dictionary<string, object?>? details = null;
                string? error = null;
                using (new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    try { details = await files.DetailsAsync(p, stat, false, CancellationToken.None); }
                    catch (Exception e) { error = e.GetType().Name + ": " + e.Message; }
                }
                Check("a file held shut by another program still has its file details", details != null && details.ContainsKey("file"),
                    error ?? string.Join(",", details?.Keys ?? Enumerable.Empty<string>()));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // MARK: Web app

        /// <summary>
        /// Wrong pairing codes were counted against the Tailscale-User-Login header whoever sent it. Any
        /// local program can write that header, so one that changed it on every try was never made to wait
        /// and could try every code. Only a login Tailscale itself vouches for may name the client.
        /// </summary>
        private static async Task SpoofedLoginStillCountsWrongCodesTests()
        {
            int port = FreePort();
            using var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => "12345678", loopbackOnly: true, port: port);
            server.ServePeerOverride = _ => false; // a local program, not Tailscale Serve
            server.Start();
            if (!await WaitForListener(port)) { Check("the web app server listens", false); return; }
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            var statuses = new List<int>();
            for (int i = 0; i < ConnectServer.FreeCodeTries + 1; i++)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/api/info");
                request.Headers.Add("Tailscale-User-Login", $"guesser{i}@example.com");
                request.Headers.Add("X-Connect-Code", (11111111 + i).ToString());
                using var response = await http.SendAsync(request);
                statuses.Add((int)response.StatusCode);
            }
            Check("a local program changing its login header each try is still made to wait after five wrong codes",
                statuses[^1] == 429, string.Join(",", statuses));
        }

        // MARK: Helpers

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
