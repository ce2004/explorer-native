using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
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
