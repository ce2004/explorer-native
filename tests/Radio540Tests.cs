using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// 5.4.0: internet streams in playlist files (.pls, .m3u, .m3u8, .asx, .xspf),
    /// Shoutcast and Icecast radio, and HLS. Offline: a pretend station and a
    /// pretend HLS server on 127.0.0.1 send real MP3. The real-station probe is
    /// its own group, radioprobe.
    /// </summary>
    internal static class Radio540Tests
    {
        public static Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            Playlists(check, equal);
            Icy(check, equal);
            Hls(check, equal);
            Ts(check, equal);
            Wiring(check);
            EndToEnd(check, equal);
            return Task.CompletedTask;
        }

        private static void Playlists(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            check("radio: .pls, .m3u, .m3u8 are playlists by name", StreamPlaylists.IsPlaylistName(@"C:\a\Radio.PLS") &&
                StreamPlaylists.IsPlaylistName("x.m3u") && StreamPlaylists.IsPlaylistName("x.m3u8") && !StreamPlaylists.IsPlaylistName("x.mp3"), null);

            var pls = StreamPlaylists.Parse("[playlist]\r\nNumberOfEntries=2\r\nFile2=http://b.example/stream\r\nTitle2=Backup\r\nFile1=http://a.example:8000/live\r\nTitle1=Groove Salad\r\nLength1=-1\r\n", null);
            equal("radio: pls entries in number order", "http://a.example:8000/live|http://b.example/stream", string.Join("|", pls.Select(e => e.Url)));
            equal("radio: pls titles", "Groove Salad", pls[0].Title ?? "");

            var m3u = StreamPlaylists.Parse("#EXTM3U\n#EXTINF:-1,Station One\nhttp://one.example/a.mp3\n\n# comment\nhttps://two.example/b\nC:\\Music\\song.mp3\n", null);
            equal("radio: m3u entries", "http://one.example/a.mp3|https://two.example/b|C:\\Music\\song.mp3", string.Join("|", m3u.Select(e => e.Url)));
            equal("radio: m3u EXTINF title", "Station One", m3u[0].Title ?? "");
            equal("radio: only web entries are streams", "2", StreamPlaylists.WebEntries(m3u).Count.ToString());

            var relative = StreamPlaylists.Parse("#EXTM3U\nlive/stream.aac\n", new Uri("http://host.example/radio/list.m3u"));
            equal("radio: relative entries against the playlist's address", "http://host.example/radio/live/stream.aac", relative[0].Url);

            var asx = StreamPlaylists.Parse("<asx version=\"3.0\"><title>My Station</title><entry><ref href=\"http://asx.example/s?a=1&amp;b=2\"/></entry></asx>", null);
            equal("radio: asx", "http://asx.example/s?a=1&b=2", asx[0].Url);
            equal("radio: asx title", "My Station", asx[0].Title ?? "");
            var xspf = StreamPlaylists.Parse("<?xml version=\"1.0\"?><playlist><trackList><track><location>https://x.example/live</location></track></trackList></playlist>", null);
            equal("radio: xspf", "https://x.example/live", xspf[0].Url);

            check("radio: an HLS playlist is told apart from a station list", StreamPlaylists.IsHls("#EXTM3U\n#EXT-X-TARGETDURATION:6\nseg1.ts") &&
                !StreamPlaylists.IsHls("#EXTM3U\n#EXTINF:-1,x\nhttp://a/b"), null);
            check("radio: playlist content is recognised whatever it is served as", StreamPlaylists.LooksLikePlaylist(Encoding.UTF8.GetBytes("\uFEFF[playlist]\nFile1=")) &&
                !StreamPlaylists.LooksLikePlaylist(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }), null);
            check("radio: playlist content types", StreamPlaylists.IsPlaylistType("audio/x-scpls") && StreamPlaylists.IsPlaylistType("application/vnd.apple.mpegurl") &&
                !StreamPlaylists.IsPlaylistType("audio/mpeg"), null);

            var dir = Path.Combine(Path.GetTempPath(), "en-radio-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var files = Path.Combine(dir, "songs.m3u");
                File.WriteAllText(files, "#EXTM3U\nC:\\Music\\a.mp3\nb.flac\n");
                var none = RadioStream.FromPlaylistFile(files, out var problem);
                check("radio: a playlist of files has no stream (it opens in its own app)", none == null && problem == "", problem);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static void Icy(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var titles = new List<string>();
            var stripper = new RadioStream.IcyStripper(4, titles.Add);
            var meta = Encoding.ASCII.GetBytes("StreamTitle='Artist - Song';");
            var block = new byte[16 * ((meta.Length + 15) / 16)];
            meta.CopyTo(block, 0);
            var input = new List<byte>();
            input.AddRange("abcd"u8.ToArray());
            input.Add((byte)(block.Length / 16));
            input.AddRange(block);
            input.AddRange("efgh"u8.ToArray());
            input.Add(0);
            input.AddRange("ij"u8.ToArray());
            var output = new MemoryStream();
            // In small pieces, as a network delivers it.
            foreach (var piece in input.Chunk(3)) stripper.Feed(piece, output);
            equal("radio: Shoutcast titles are taken out of the sound", "abcdefghij", Encoding.ASCII.GetString(output.ToArray()));
            equal("radio: and read", "StreamTitle='Artist - Song';", titles.FirstOrDefault() ?? "");

            check("radio: sniffs MP3, ADTS, Ogg", RadioStream.Sniff(new byte[] { 0xFF, 0xFB, 0x90, 0 }) == RadioStream.SoundKind.Mp3 &&
                RadioStream.Sniff(new byte[] { 0xFF, 0xF1, 0x50, 0x80 }) == RadioStream.SoundKind.Aac &&
                RadioStream.Sniff("OggS"u8) == RadioStream.SoundKind.Ogg, null);
            check("radio: by content type first", RadioStream.KindOf("audio/aacp", new byte[] { 0, 0, 0, 0 }) == RadioStream.SoundKind.Aac &&
                RadioStream.KindOf("text/html", new byte[] { (byte)'<', (byte)'h', 0, 0 }) == RadioStream.SoundKind.Unsupported, null);
        }

        private static void Hls(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var b = new Uri("https://cdn.example/live/master.m3u8");
            var audioOnly = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=64000,CODECS=\"mp4a.40.5\"\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=320000,CODECS=\"mp4a.40.2\"\nhigh.m3u8\n";
            equal("hls: the best audio-only variant", "https://cdn.example/live/high.m3u8", RadioStream.ChooseVariant(audioOnly, b).ToString());
            var video = "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English\",DEFAULT=YES,URI=\"audio/en.m3u8\"\n" +
                        "#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,CODECS=\"avc1.640028,mp4a.40.2\",AUDIO=\"aud\"\nhd.m3u8\n" +
                        "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS=\"avc1.4d401e,mp4a.40.2\",AUDIO=\"aud\"\nsd.m3u8\n";
            equal("hls: video with a separate audio track takes the audio", "https://cdn.example/live/audio/en.m3u8", RadioStream.ChooseVariant(video, b).ToString());
            var muxed = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080\nhd.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\nsd.m3u8\n";
            equal("hls: video only: the smallest, for its sound", "https://cdn.example/live/sd.m3u8", RadioStream.ChooseVariant(muxed, b).ToString());

            var media = RadioStream.ParseMedia("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:100\n" +
                "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x0000000000000000000000000000002A\n#EXTINF:4.0,\nseg100.ts\n" +
                "#EXT-X-KEY:METHOD=NONE\n#EXTINF:4.0,\n#EXT-X-BYTERANGE:1000@500\nall.ts\n#EXT-X-ENDLIST\n", new Uri("https://cdn.example/live/media.m3u8"));
            check("hls: media playlist", media.TargetDuration == 4 && media.EndList && media.Segments.Count == 2 &&
                media.Segments[0].Sequence == 100 && media.Segments[1].Sequence == 101, null);
            check("hls: key and IV", media.Segments[0].KeyMethod == "AES-128" && media.Segments[0].KeyUri?.ToString() == "https://cdn.example/live/key.bin" &&
                media.Segments[0].Iv?[15] == 42 && media.Segments[1].KeyMethod == null, null);
            check("hls: byte ranges", media.Segments[1].Range is { Length: 1000, Offset: 500 }, null);

            var id3 = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 3, 1, 2, 3, 0xFF, 0xF1 };
            equal("hls: the ID3 timestamp in front of packed audio is skipped", "13", RadioStream.SkipId3(id3).ToString());
        }

        private static void Ts(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var sound = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7)).ToArray();
            var ts = Mux(sound, 0x0F);
            var demux = new RadioStream.TsAudio();
            var output = new MemoryStream();
            foreach (var piece in ts.Chunk(100)) demux.Feed(piece, output);
            check("ts: the sound comes out of the transport stream whole", output.ToArray().SequenceEqual(sound), $"{output.Length} bytes");
            check("ts: and is known to be AAC", demux.Kind == RadioStream.SoundKind.Aac, null);
            var ac3 = new RadioStream.TsAudio();
            ac3.Feed(Mux(sound, 0x81), new MemoryStream());
            check("ts: Dolby only is said to be undecodable", ac3.Kind == RadioStream.SoundKind.Unsupported, null);

            // Fragmented MP4: a video track first, then AAC LC 44.1kHz stereo.
            static byte[] Box(string type, params byte[][] parts)
            {
                var body = parts.SelectMany(p => p).ToArray();
                int size = body.Length + 8;
                return new[] { (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size }
                    .Concat(Encoding.ASCII.GetBytes(type)).Concat(body).ToArray();
            }
            static byte[] U32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
            static byte[] Track(uint id, string handler, byte[] entry) => Box("trak",
                Box("tkhd", new byte[12], U32(id), new byte[60]),
                Box("mdia", Box("hdlr", new byte[8], Encoding.ASCII.GetBytes(handler), new byte[13]),
                    Box("minf", Box("stbl", Box("stsd", new byte[4], U32(1), entry)))));
            var esds = Box("esds", new byte[4], new byte[] { 0x03, 25, 0, 1, 0, 0x04, 17, 0x40, 0x15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x05, 2, 0x12, 0x10 });
            var init = Box("moov",
                Track(1, "vide", Box("avc1", new byte[78])),
                Track(2, "soun", Box("mp4a", new byte[28], esds)),
                Box("mvex", Box("trex", new byte[4], U32(2), U32(1), U32(1024), U32(0), U32(0))));
            var samples = new byte[][] { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6, 7 } };
            byte[] Segment(int dataOffset) => Box("moof", Box("traf",
                Box("tfhd", U32(0x020000), U32(2)),
                Box("trun", U32(0x201), U32(2), U32((uint)dataOffset), U32(3), U32(4))));
            int moofSize = Segment(0).Length;
            var segment = Segment(moofSize + 8).Concat(Box("mdat", samples[0], samples[1])).ToArray();
            var fmp4 = new Fmp4Audio(init);
            check("fmp4: finds the AAC track past the video", fmp4.Kind == RadioStream.SoundKind.Aac, fmp4.Kind.ToString());
            var adts = new MemoryStream();
            fmp4.Feed(segment, adts);
            var a = adts.ToArray();
            equal("fmp4: each sample becomes an ADTS frame", "FF-F1-50-80-01-5F-FC-01-02-03-FF-F1-50-80-01-7F-FC-04-05-06-07", BitConverter.ToString(a));
            var dolby = Box("moov", Track(1, "soun", Box("ec-3", new byte[28])));
            check("fmp4: Dolby sound is said to be undecodable", new Fmp4Audio(dolby).Kind == RadioStream.SoundKind.Unsupported, null);
        }

        private static string Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            try { return dir == null ? "" : File.ReadAllText(Path.Combine(dir.FullName, "src", name)); }
            catch { return ""; }
        }

        private static void Wiring(Action<string, bool, string?> check)
        {
            var main = Source("MainForm.cs");
            check("radio: Enter plays a playlist file's stream", main.Contains("StreamPlaylists.IsPlaylistName(path)", StringComparison.Ordinal), null);
            var decoder = Source("TrackDecoder.cs");
            check("radio: a live stream gets its own decoder", decoder.Contains("stream is RadioStream radio", StringComparison.Ordinal), null);
            var playback = Source("WasapiPlayback.cs");
            check("radio: a live stream keeps the deep buffer", playback.Contains("_cushioning || _live ||", StringComparison.Ordinal), null);
        }

        // ---------- End to end, against pretend servers ----------

        private static void EndToEnd(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var mp3 = MakeMp3(out var why);
            if (mp3 == null) { Console.WriteLine("  (radio end to end skipped: " + why + ")"); return; }

            using var server = new PretendServer(mp3);
            string? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    // Shoutcast, reached through a .pls on the web, with a song title.
                    using (var radio = RadioStream.ForUrl(server.Base + "station.pls", "file name"))
                    {
                        var (seconds, level) = Listen(radio, 3);
                        check("radio e2e: a Shoutcast station through a web .pls plays sound", seconds >= 2.9 && level > 0.05, $"{seconds:0.0}s at {level:0.000}");
                        equal("radio e2e: named by the playlist's title", "From the pls", radio.Name);
                        equal("radio e2e: the song it says is on", "Artist - Song", radio.Song ?? "");
                        check("radio e2e: Shoutcast's ICY answer is understood", server.Served.Contains("/radio"), string.Join(",", server.Served));
                    }

                    // Dropped by the server: made again, and the sound carries on.
                    server.DropRadioAfter = 60_000;
                    using (var radio = RadioStream.ForUrl(server.Base + "radio"))
                    {
                        var (seconds, level) = Listen(radio, 8);
                        equal("radio e2e: with no title, the station's own name", "Test Station", radio.Name);
                        check("radio e2e: a dropped connection is made again", seconds >= 7.9 && level > 0.05 && server.RadioConnections >= 2,
                            $"{seconds:0.0}s, {server.RadioConnections} connections");
                    }
                    server.DropRadioAfter = 0;

                    // HLS: master, encrypted TS segments with the sound inside.
                    using (var radio = RadioStream.ForUrl(server.Base + "hls/master.m3u8"))
                    {
                        var (seconds, level) = Listen(radio, 3);
                        check("hls e2e: encrypted transport-stream segments play sound", seconds >= 2.9 && level > 0.05, $"{seconds:0.0}s at {level:0.000} {radio.Problem}");
                        check("hls e2e: through the key", server.Served.Contains("/hls/key"), null);
                    }

                    // HLS from a playlist file on disk, with absolute addresses.
                    var dir = Path.Combine(Path.GetTempPath(), "en-radio-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    try
                    {
                        var file = Path.Combine(dir, "Live.m3u8");
                        File.WriteAllText(file, server.MediaPlaylist(absolute: true));
                        using var radio = RadioStream.FromPlaylistFile(file, out var problem)!;
                        var (seconds, level) = Listen(radio, 2);
                        check("hls e2e: a saved .m3u8 plays", seconds >= 1.9 && level > 0.05, $"{seconds:0.0}s {problem} {radio.Problem}");
                        equal("hls e2e: named after the file", "Live", radio.Name);
                    }
                    finally { try { Directory.Delete(dir, true); } catch { } }

                    // A station that isn't there: ends with a reason, quickly.
                    var saved = RadioStream.GiveUpAfter;
                    RadioStream.GiveUpAfter = TimeSpan.FromSeconds(2);
                    try
                    {
                        using var radio = RadioStream.ForUrl(server.Base + "missing");
                        var watch = Stopwatch.StartNew();
                        var decoder = TrackDecoder.Open("x.pls", radio, 48000, 2, out var said);
                        check("radio e2e: a missing station fails with a reason", decoder == null && said.Contains("404"), said);
                        check("radio e2e: and fails fast", watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
                        decoder?.Dispose();
                    }
                    finally { RadioStream.GiveUpAfter = saved; }
                }
                catch (Exception ex) { error = ex.ToString(); }
            });
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(90))) error = "timed out";
            equal("radio end to end runs", "", error ?? "");
        }

        /// <summary>Decodes about <paramref name="seconds"/> of the stream; how much came, and how loud.</summary>
        private static (double Seconds, double Level) Listen(RadioStream radio, double seconds)
        {
            var decoder = TrackDecoder.Open("probe.pls", radio, 48000, 2, out var why);
            if (decoder == null) { Console.WriteLine("    open failed: " + why); return (0, 0); }
            try
            {
                var buffer = new float[4800 * decoder.Channels];
                long frames = 0;
                double sum = 0; long count = 0;
                while (frames < seconds * decoder.SampleRate)
                {
                    int n = decoder.Read(buffer, 4800);
                    if (n <= 0) break;
                    frames += n;
                    for (int i = 0; i < n * decoder.Channels; i++) { sum += buffer[i] * buffer[i]; count++; }
                }
                return (frames / (double)decoder.SampleRate, count == 0 ? 0 : Math.Sqrt(sum / count));
            }
            finally { decoder.Dispose(); }
        }

        /// <summary>Ten seconds of a 440Hz tone as MP3, by Windows' own encoder.</summary>
        private static byte[]? MakeMp3(out string why)
        {
            why = AudioEncoders.WhyUnavailable(AudioFormatKind.Mp3) ?? "";
            if (why.Length > 0) return null;
            var path = Path.Combine(Path.GetTempPath(), "en-radio-" + Guid.NewGuid().ToString("N") + ".mp3");
            try
            {
                using (var encoder = new MediaFoundationEncoder(path, AudioFormatKind.Mp3, 44100, 2, 128, 16))
                {
                    var block = new float[4410 * 2];
                    for (int b = 0; b < 100; b++)
                    {
                        for (int i = 0; i < 4410; i++)
                        {
                            float v = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * (b * 4410 + i) / 44100.0));
                            block[2 * i] = block[2 * i + 1] = v;
                        }
                        encoder.Write(block, 4410);
                    }
                    encoder.Finish();
                }
                return File.ReadAllBytes(path);
            }
            catch (Exception ex) { why = ex.Message; return null; }
            finally { try { File.Delete(path); } catch { } }
        }

        /// <summary>An MPEG transport stream of one PES carrying <paramref name="sound"/>.</summary>
        internal static byte[] Mux(byte[] sound, byte streamType)
        {
            var output = new MemoryStream();
            void Packet(int pid, bool start, byte[] payload, int cc)
            {
                var p = new byte[188];
                p[0] = 0x47;
                p[1] = (byte)((start ? 0x40 : 0) | (pid >> 8));
                p[2] = (byte)pid;
                int room = 184;
                if (payload.Length < room)
                {
                    p[3] = (byte)(0x30 | (cc & 0xF));
                    int adaptation = 184 - payload.Length - 1;
                    p[4] = (byte)adaptation;
                    if (adaptation > 0) { p[5] = 0; for (int i = 6; i < 5 + adaptation; i++) p[i] = 0xFF; }
                    payload.CopyTo(p, 5 + adaptation);
                }
                else
                {
                    p[3] = (byte)(0x10 | (cc & 0xF));
                    payload.CopyTo(p, 4);
                }
                output.Write(p);
            }
            byte[] Section(byte[] body)
            {
                var s = new byte[1 + body.Length + 4];
                body.CopyTo(s, 1);
                return s.Concat(Enumerable.Repeat((byte)0xFF, 0)).ToArray();
            }
            Packet(0, true, Section(new byte[] { 0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01, 0xE1, 0x00 }), 0);
            Packet(0x100, true, Section(new byte[] { 0x02, 0xB0, 0x12, 0x00, 0x01, 0xC1, 0x00, 0x00, 0xE1, 0x01, 0xF0, 0x00, streamType, 0xE1, 0x01, 0xF0, 0x00 }), 0);
            var pes = new byte[] { 0, 0, 1, 0xC0, 0, 0, 0x80, 0x00, 0x00 }.Concat(sound).ToArray();
            int cc = 0;
            for (int at = 0; at < pes.Length; at += 184)
                Packet(0x101, at == 0, pes.Skip(at).Take(184).ToArray(), cc++);
            return output.ToArray();
        }

        /// <summary>A Shoutcast station and an HLS server, on 127.0.0.1.</summary>
        private sealed class PretendServer : IDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly byte[] _mp3;
            private readonly byte[] _key = RandomNumberGenerator.GetBytes(16);
            private readonly List<byte[]> _segments = new();
            private volatile bool _stopped;
            public readonly List<string> Served = new();
            public volatile int RadioConnections;
            public volatile int DropRadioAfter;

            public PretendServer(byte[] mp3)
            {
                _mp3 = mp3;
                int size = (mp3.Length + 3) / 4;
                for (int i = 0; i < 4; i++)
                {
                    var piece = mp3.Skip(i * size).Take(size).ToArray();
                    using var aes = Aes.Create();
                    aes.Key = _key;
                    var iv = new byte[16]; iv[15] = (byte)i;
                    _segments.Add(aes.EncryptCbc(Mux(piece, 0x03), iv, PaddingMode.PKCS7));
                }
                _listener.Start();
                new Thread(Accept) { IsBackground = true }.Start();
            }

            public string Base => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

            public string MediaPlaylist(bool absolute)
            {
                var prefix = absolute ? Base + "hls/" : "";
                var sb = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXT-X-MEDIA-SEQUENCE:0\n");
                sb.Append($"#EXT-X-KEY:METHOD=AES-128,URI=\"{prefix}key\"\n");
                for (int i = 0; i < _segments.Count; i++) sb.Append($"#EXTINF:2.5,\n{prefix}seg{i}.ts\n");
                sb.Append("#EXT-X-ENDLIST\n");
                return sb.ToString();
            }

            private void Accept()
            {
                while (!_stopped)
                {
                    TcpClient client;
                    try { client = _listener.AcceptTcpClient(); } catch { return; }
                    new Thread(() => Serve(client)) { IsBackground = true }.Start();
                }
            }

            private void Serve(TcpClient client)
            {
                using var _ = client;
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var first = reader.ReadLine() ?? "";
                    while (!string.IsNullOrEmpty(reader.ReadLine())) { }
                    var path = first.Split(' ').ElementAtOrDefault(1) ?? "/";
                    lock (Served) Served.Add(path);

                    void Send(string type, byte[] body, int status = 200)
                    {
                        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                        stream.Write(head);
                        stream.Write(body);
                    }

                    switch (path)
                    {
                        case "/station.pls":
                            Send("audio/x-scpls", Encoding.ASCII.GetBytes($"[playlist]\nFile1={Base}radio\nTitle1=From the pls\nNumberOfEntries=1\n"));
                            break;
                        case "/radio":
                            Radio(stream);
                            break;
                        case "/hls/master.m3u8":
                            Send("application/vnd.apple.mpegurl", Encoding.ASCII.GetBytes("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS=\"mp4a.40.34\"\nmedia.m3u8\n"));
                            break;
                        case "/hls/media.m3u8":
                            Send("application/vnd.apple.mpegurl", Encoding.ASCII.GetBytes(MediaPlaylist(false)));
                            break;
                        case "/hls/key":
                            Send("application/octet-stream", _key);
                            break;
                        default:
                            if (path.StartsWith("/hls/seg") && int.TryParse(path[8..^3], out var n) && n < _segments.Count)
                                Send("video/mp2t", _segments[n]);
                            else Send("text/plain", Encoding.ASCII.GetBytes("no"), 404);
                            break;
                    }
                }
                catch { }
            }

            /// <summary>Shoutcast: an ICY answer, the MP3 round and round, a title every 8192 bytes.</summary>
            private void Radio(NetworkStream stream)
            {
                Interlocked.Increment(ref RadioConnections);
                stream.Write(Encoding.ASCII.GetBytes("ICY 200 OK\r\nicy-name: Test Station\r\ncontent-type: audio/mpeg\r\nicy-metaint: 8192\r\n\r\n"));
                var meta = Encoding.ASCII.GetBytes("StreamTitle='Artist - Song';");
                var block = new byte[1 + 16 * ((meta.Length + 15) / 16)];
                block[0] = (byte)((block.Length - 1) / 16);
                meta.CopyTo(block, 1);
                int at = 0;
                long sent = 0;
                int drop = DropRadioAfter;
                while (!_stopped)
                {
                    var audio = new byte[8192];
                    for (int i = 0; i < audio.Length; i++) { audio[i] = _mp3[at]; at = (at + 1) % _mp3.Length; }
                    stream.Write(audio);
                    stream.Write(block);
                    sent += audio.Length;
                    if (drop > 0 && sent >= drop) return;
                }
            }

            public void Dispose()
            {
                _stopped = true;
                try { _listener.Stop(); } catch { }
            }
        }

        // ---------- Real stations (radioprobe) ----------

        public static Task Probe(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var urls = (Environment.GetEnvironmentVariable("EXPLORERNATIVE_RADIOPROBE_URLS") ??
                "https://ice1.somafm.com/groovesalad-128-mp3;https://ice1.somafm.com/groovesalad-128-aac;https://somafm.com/groovesalad.pls;" +
                "https://stream.radioparadise.com/aac-320;" +
                "http://as-hls-ww-live.akamaized.net/pool_904/live/ww/bbc_radio_one/bbc_radio_one.isml/bbc_radio_one-audio%3d96000.norewind.m3u8;" +
                "https://devstreaming-cdn.apple.com/videos/streaming/examples/bipbop_4x3/bipbop_4x3_variant.m3u8;" +
                "https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_fmp4/master.m3u8")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var url in urls)
            {
                string? said = null;
                var thread = new Thread(() =>
                {
                    try
                    {
                        using var radio = RadioStream.ForUrl(url);
                        var watch = Stopwatch.StartNew();
                        var decoder = TrackDecoder.Open("probe.pls", radio, 48000, 2, out var why);
                        if (decoder == null) { said = $"FAILED to open in {watch.ElapsedMilliseconds}ms: {why}"; return; }
                        long opened = watch.ElapsedMilliseconds;
                        var buffer = new float[4800 * decoder.Channels];
                        long frames = 0; double sum = 0; long count = 0; long firstSound = -1;
                        while (frames < 8 * decoder.SampleRate && watch.Elapsed < TimeSpan.FromSeconds(40))
                        {
                            int n = decoder.Read(buffer, 4800);
                            if (n <= 0) break;
                            frames += n;
                            for (int i = 0; i < n * decoder.Channels; i++)
                            {
                                sum += buffer[i] * buffer[i]; count++;
                                if (firstSound < 0 && Math.Abs(buffer[i]) > 0.01) firstSound = watch.ElapsedMilliseconds;
                            }
                        }
                        double level = count == 0 ? 0 : Math.Sqrt(sum / count);
                        said = $"{radio.Kind}, opened {opened}ms, sound at {firstSound}ms, {frames / (double)decoder.SampleRate:0.0}s level {level:0.000}, " +
                               $"name '{radio.Name}', song '{radio.Song}' {radio.Problem}";
                        check("radioprobe: " + url, frames >= 7 * decoder.SampleRate && level > 0.005, said);
                        decoder.Dispose();
                    }
                    catch (Exception ex) { said = "threw " + ex; }
                });
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();
                if (!thread.Join(TimeSpan.FromSeconds(60))) said = "timed out";
                Console.WriteLine($"  {url}\n    {said}");
            }
            return Task.CompletedTask;
        }
    }
}
