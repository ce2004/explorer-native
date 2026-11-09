using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// An internet stream as the endless IStream the decoders read (5.4.0):
    /// Shoutcast and Icecast radio (with its song titles taken out of the
    /// sound), plain audio over HTTP, and HLS, whose segments are fetched in
    /// turn and joined, with the sound taken out of MPEG-TS and AES-128 undone.
    ///
    /// One thread does the network, synchronously, and fills a buffer the
    /// decoder drains. The buffer holds at most <see cref="MaxAhead"/> unread,
    /// so a paused stream stops downloading (the socket just stalls) rather than
    /// growing. A dropped connection is made again; after
    /// <see cref="GiveUpAfter"/> with no sound at all the stream ends, with
    /// <see cref="Problem"/> saying why.
    /// </summary>
    internal sealed class RadioStream : IStream, IDisposable
    {
        public enum SoundKind { Unknown, Mp3, Aac, Ogg, Flac, Mp4, Unsupported }

        /// <summary>What the decoders are told the length is: big enough never to be reached, and never read near.</summary>
        internal const long FakeLength = 1L << 40;

        internal static long MaxAhead = 4 * 1024 * 1024;
        private const long KeepBehind = 512 * 1024;
        private const long HeadKeep = 1024 * 1024;
        internal static TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);
        internal static int StallMilliseconds = 20_000;

        private readonly object _gate = new();
        private readonly List<(long Offset, byte[] Data)> _chunks = new();
        private long _end, _position;
        private bool _ended, _disposed, _finished;
        private readonly CancellationTokenSource _cancel = new();
        private readonly ManualResetEventSlim _kindKnown = new(false);
        private Socket? _socket;
        private readonly Thread _worker;

        private readonly List<StreamPlaylists.Entry> _entries;
        private readonly string? _hlsText;
        private readonly string _fileName;
        private string? _playlistTitle, _icyName, _song;
        private volatile SoundKind _kind;

        private RadioStream(List<StreamPlaylists.Entry> entries, string? hlsText, string fileName)
        {
            _entries = entries;
            _hlsText = hlsText;
            _fileName = fileName;
            _playlistTitle = entries.Select(e => e.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            _worker = new Thread(Run) { IsBackground = true, Name = "ExplorerNative live stream" };
            _worker.Start();
        }

        /// <summary>
        /// The stream a playlist file points at, or null with
        /// <paramref name="problem"/> empty when it has no web addresses (it is
        /// a list of files, for its own app), or saying why it cannot be read.
        /// </summary>
        public static RadioStream? FromPlaylistFile(string path, out string problem)
        {
            problem = "";
            string text;
            try { text = StreamPlaylists.ReadFile(path); }
            catch (Exception ex) { problem = "it could not be read: " + ex.Message; return null; }

            string name = Path.GetFileNameWithoutExtension(path);
            if (StreamPlaylists.IsHls(text))
                return new RadioStream(new List<StreamPlaylists.Entry>(), text, name);

            var web = StreamPlaylists.WebEntries(StreamPlaylists.Parse(text, null));
            if (web.Count == 0) return null;
            return new RadioStream(web, null, name);
        }

        /// <summary>A stream at a web address, for the tests and the probe.</summary>
        public static RadioStream ForUrl(string url, string? name = null) =>
            new(new List<StreamPlaylists.Entry> { new(url, null) }, null, name ?? url);

        /// <summary>The station: the playlist's title for it (short, and the one chosen), else its own name, else the file's name.</summary>
        public string Name
        {
            get
            {
                lock (_gate)
                    return !string.IsNullOrWhiteSpace(_playlistTitle) ? _playlistTitle!
                        : !string.IsNullOrWhiteSpace(_icyName) ? _icyName! : _fileName;
            }
        }

        /// <summary>The song the station says is on, or null.</summary>
        public string? Song { get { lock (_gate) return _song; } }

        /// <summary>The song changed. Raised on the network thread.</summary>
        public event Action<RadioStream>? SongChanged;

        public SoundKind Kind => _kind;

        /// <summary>The decoder is still opening: reads may come back short then, never after.</summary>
        public volatile bool Opening = true;

        /// <summary>Why it stopped or would not start, in words for a person.</summary>
        public string Problem { get; private set; } = "";

        /// <summary>It answered, but with sound this player can't decode: its own app may do better.</summary>
        public bool WrongFormat { get; private set; }

        private StreamGone Unplayable(string message)
        {
            WrongFormat = true;
            return new StreamGone(message);
        }

        /// <summary>Waits until the kind of sound is known, or the stream has given up. True when known.</summary>
        public bool WaitForKind(TimeSpan timeout)
        {
            try { _kindKnown.Wait(timeout, _cancel.Token); } catch (OperationCanceledException) { }
            return _kind != SoundKind.Unknown && _kind != SoundKind.Unsupported;
        }

        /// <summary>Bytes held and not yet read.</summary>
        public long Ahead { get { lock (_gate) return _end - _position; } }

        public bool Ended { get { lock (_gate) return _ended; } }

        // ---------- The buffer ----------

        private void Push(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return;
            lock (_gate)
            {
                while (!_disposed && _end - _position > MaxAhead) Monitor.Wait(_gate, 1000);
                if (_disposed) return;
                _chunks.Add((_end, data.ToArray()));
                _end += data.Length;
                Monitor.PulseAll(_gate);
            }
        }

        private void WaitForRoom()
        {
            lock (_gate)
                while (!_disposed && _end - _position > MaxAhead) Monitor.Wait(_gate, 1000);
        }

        private void Trim()
        {
            long keepFrom = _position < HeadKeep ? 0 : _position - KeepBehind;
            int drop = 0;
            while (drop < _chunks.Count && _chunks[drop].Offset + _chunks[drop].Data.Length <= keepFrom) drop++;
            if (drop > 0) _chunks.RemoveRange(0, drop);
        }

        public void Read(byte[] buffer, int count, IntPtr bytesReadOut)
        {
            int read = 0;
            try
            {
                lock (_gate)
                {
                    long waitedSince = Environment.TickCount64;
                    while (!_disposed)
                    {
                        // A look at the far end (an ID3v1 tag, an index): nothing
                        // there, and nothing will be.
                        if (_position > _end + 16 * 1024 * 1024) break;
                        if (_end - _position >= count || _ended) break;
                        // What has come, rather than waiting for a whole block: a
                        // decoder asking for 64KB of a 64kbps stream waited 8s to start.
                        // Not for MP3 once it plays: Media Foundation's MP3 reader takes a
                        // short read as the end (181.fm, sent at playing speed, stopped after
                        // 5s). Its AAC reader does not, and full reads held a 64kbps AAC
                        // station silent for 20s.
                        if ((Opening || _kind != SoundKind.Mp3) && _end > _position && Environment.TickCount64 - waitedSince >= 150) break;
                        if (Environment.TickCount64 - waitedSince > StallMilliseconds + GiveUpAfter.TotalMilliseconds) break;
                        Monitor.Wait(_gate, 250);
                    }

                    if (!_disposed && _position <= _end)
                    {
                        if (_chunks.Count > 0 && _position < _chunks[0].Offset) _position = _chunks[0].Offset;
                        int want = (int)Math.Min(count, Math.Min(buffer.Length, _end - _position));
                        int i = 0;
                        while (i < _chunks.Count && _chunks[i].Offset + _chunks[i].Data.Length <= _position) i++;
                        while (read < want && i < _chunks.Count)
                        {
                            var (offset, data) = _chunks[i];
                            int from = (int)(_position + read - offset);
                            int n = Math.Min(want - read, data.Length - from);
                            Buffer.BlockCopy(data, from, buffer, read, n);
                            read += n;
                            i++;
                        }
                        _position += read;
                        Trim();
                        Monitor.PulseAll(_gate);
                    }
                }
            }
            catch { }
            if (bytesReadOut != IntPtr.Zero) Marshal.WriteInt32(bytesReadOut, read);
        }

        public void Seek(long move, int origin, IntPtr newPositionOut)
        {
            lock (_gate)
            {
                long from = origin switch { 1 => _position, 2 => FakeLength, _ => 0 };
                long to = Math.Clamp(from + move, 0, FakeLength);
                // Behind what is still held: the earliest held byte is as far back as it goes.
                if (to < _end && _chunks.Count > 0 && to < _chunks[0].Offset) to = _chunks[0].Offset;
                _position = to;
                Monitor.PulseAll(_gate);
                if (newPositionOut != IntPtr.Zero) Marshal.WriteInt64(newPositionOut, _position);
            }
        }

        public void Stat(out STATSTG stat, int flags) =>
            stat = new STATSTG { type = 2, cbSize = FakeLength, grfMode = 0 };

        public void Dispose()
        {
            Socket? socket;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _ended = true;
                socket = _socket;
                Monitor.PulseAll(_gate);
            }
            try { _cancel.Cancel(); } catch { }
            try { socket?.Dispose(); } catch { }
            _kindKnown.Set();
        }

        public void Clone(out IStream copy) => throw new NotSupportedException();
        public void Commit(int flags) => throw new NotSupportedException();
        public void CopyTo(IStream target, long count, IntPtr read, IntPtr written) => throw new NotSupportedException();
        public void LockRegion(long offset, long count, int lockType) => throw new NotSupportedException();
        public void Revert() => throw new NotSupportedException();
        public void SetSize(long newSize) => throw new NotSupportedException();
        public void UnlockRegion(long offset, long count, int lockType) => throw new NotSupportedException();
        public void Write(byte[] buffer, int count, IntPtr written) => throw new NotSupportedException();

        private bool Disposed { get { lock (_gate) return _disposed; } }
        private long End { get { lock (_gate) return _end; } }

        private void SetKind(SoundKind kind)
        {
            if (_kind == SoundKind.Unknown) _kind = kind;
            _kindKnown.Set();
        }

        // ---------- The network ----------

        /// <summary>A failure that trying again will not mend.</summary>
        private sealed class StreamGone : Exception { public StreamGone(string message) : base(message) { } }

        private void Run()
        {
            long lastSound = Environment.TickCount64;
            int attempt = 0;
            while (!Disposed)
            {
                long before = End;
                string? fatal = null;
                try
                {
                    if (_hlsText != null) RunHls(null, _hlsText);
                    else TryEntries(_entries, 0);
                }
                catch (StreamGone ex) { fatal = ex.Message; }
                catch (Exception ex) when (!Disposed) { Problem = Plain(ex); }
                catch { }

                if (Disposed || _finished) break;
                if (End > before) { lastSound = Environment.TickCount64; attempt = 0; }
                if (fatal != null) { Problem = fatal; break; }
                if (Environment.TickCount64 - lastSound > GiveUpAfter.TotalMilliseconds) break;

                int wait = Math.Min(5000, 500 << Math.Min(attempt, 4));
                attempt++;
                if (_cancel.Token.WaitHandle.WaitOne(wait)) break;
                WaitForRoom();
            }

            if (Problem.Length == 0 && !_finished && !Disposed) Problem = "it stopped sending sound";
            if (_kind == SoundKind.Unknown) _kind = SoundKind.Unsupported;
            lock (_gate) { _ended = true; Monitor.PulseAll(_gate); }
            _kindKnown.Set();
        }

        private void TryEntries(List<StreamPlaylists.Entry> entries, int depth)
        {
            if (entries.Count == 0) throw new StreamGone("there are no streams in it");
            Exception? last = null;
            foreach (var entry in entries)
            {
                if (Disposed) return;
                long before = End;
                try
                {
                    if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri)) throw new StreamGone("its address is not a web address");
                    PlayUrl(General(uri), depth);
                    return;
                }
                catch (Exception ex) when (!Disposed)
                {
                    // It was playing: try the same list again, from the top.
                    if (End > before) throw;
                    last = ex;
                }
            }
            if (last != null) throw last;
        }

        /// <summary>
        /// An address saved pointing at one server of a big radio network
        /// (StreamTheWorld's 12345.live.streamtheworld.com, Amperwave's
        /// prod-1-2-3-4.amperwave.net) goes stale when that server is moved
        /// or retired: 403, or no answer at all. The network's own address for
        /// the station sends us to whichever server is live now.
        /// </summary>
        internal static Uri General(Uri uri)
        {
            var host = uri.Host.ToLowerInvariant();
            var path = uri.AbsolutePath.TrimStart('/');
            if (Regex.IsMatch(host, @"^\d+\.live\.streamtheworld\.com$") && Regex.IsMatch(path, @"^[A-Za-z0-9_]+(\.(aac|mp3))?$"))
            {
                var mount = path.EndsWith("_SC", StringComparison.OrdinalIgnoreCase) ? path[..^3] + ".mp3"
                    : path.Contains('.') ? path : path + ".mp3";
                return new Uri("https://playerservices.streamtheworld.com/api/livestream-redirect/" + mount);
            }
            if (Regex.IsMatch(host, @"^prod-[0-9-]+\.amperwave\.net$") && path.Length > 0 && !path.Contains('/'))
                return new Uri("https://live.amperwave.net/" + (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "manifest/" : "direct/") + path);
            return uri;
        }

        private void PlayUrl(Uri uri, int depth)
        {
            if (depth > 5) throw new StreamGone("its playlists point at each other without end");
            if (uri.Scheme == "icy") uri = new UriBuilder(uri) { Scheme = "http" }.Uri;

            using var response = HttpGet(uri, icy: true);
            var type = response.Header("content-type");
            var head = response.Peek(1024);

            bool playlistByName = StreamPlaylists.IsPlaylistName(response.Url.AbsolutePath);
            if (StreamPlaylists.IsPlaylistType(type) || StreamPlaylists.LooksLikePlaylist(head) ||
                (playlistByName && type?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) != true))
            {
                var text = StreamPlaylists.Decode(response.ReadAll(2 * 1024 * 1024));
                // The playlist has been read: let its connection go now, not
                // when the station it names stops hours later.
                var from = response.Url;
                response.Dispose();
                if (StreamPlaylists.IsHls(text)) { RunHls(from, text); return; }
                var inner = StreamPlaylists.WebEntries(StreamPlaylists.Parse(text, from));
                if (inner.Count == 0) throw new StreamGone("the playlist on the web has no streams in it");
                lock (_gate) _playlistTitle ??= inner.Select(e => e.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                TryEntries(inner, depth + 1);
                return;
            }

            var name = response.Header("icy-name");
            if (!string.IsNullOrWhiteSpace(name)) lock (_gate) _icyName = name.Trim();

            bool ts = IsTs(head) || type?.Contains("mp2t", StringComparison.OrdinalIgnoreCase) == true;
            TsAudio? demux = null;
            if (ts) demux = new TsAudio();
            else
            {
                var kind = KindOf(type, head);
                if (kind == SoundKind.Unsupported) throw Unplayable("it isn't a kind of sound this player knows");
                SetKind(kind);
            }

            int metaint = int.TryParse(response.Header("icy-metaint"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mi) ? mi : 0;
            var icy = new IcyStripper(metaint, OnMetadata);
            long? length = response.ContentLength;
            long got = 0;
            var buffer = new byte[16 * 1024];
            var sound = new MemoryStream();
            while (!Disposed)
            {
                int n = response.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                got += n;
                sound.SetLength(0);
                icy.Feed(buffer.AsSpan(0, n), sound);
                var bytes = sound.GetBuffer().AsSpan(0, (int)sound.Length);
                if (demux != null) FeedTs(demux, bytes);
                else Push(bytes);
            }
            // A file, not a station: it is over once all of it has come.
            if (length is long whole && got >= whole) _finished = true;
        }

        private void FeedTs(TsAudio demux, ReadOnlySpan<byte> bytes)
        {
            var output = new MemoryStream();
            demux.Feed(bytes, output);
            if (demux.Kind == SoundKind.Unsupported) throw Unplayable("its sound is in a format this player can't decode");
            if (demux.Kind != SoundKind.Unknown) SetKind(demux.Kind);
            Push(output.GetBuffer().AsSpan(0, (int)output.Length));
        }

        private void OnMetadata(string text)
        {
            var m = Regex.Match(text, @"StreamTitle='(.*?)';", RegexOptions.Singleline);
            if (!m.Success) return;
            var song = m.Groups[1].Value.Trim();
            if (song.Length == 0 || song == "-") song = "";
            bool changed;
            lock (_gate)
            {
                // An empty title (adverts, news, talk) takes the last song's name away.
                changed = song.Length > 0 ? song != _song : _song != null;
                if (changed) _song = song.Length > 0 ? song : null;
            }
            if (changed) try { SongChanged?.Invoke(this); } catch { }
        }

        internal static bool IsTs(ReadOnlySpan<byte> head) =>
            head.Length >= 189 && head[0] == 0x47 && head[188] == 0x47;

        internal static SoundKind KindOf(string? type, ReadOnlySpan<byte> head)
        {
            var t = (type ?? "").ToLowerInvariant();
            if (t.Contains("ogg") || t.Contains("opus") || t.Contains("vorbis")) return SoundKind.Ogg;
            if (t.Contains("flac")) return SoundKind.Flac;
            if (t.Contains("aac")) return SoundKind.Aac;
            if (t.Contains("mp4") || t.Contains("m4a")) return SoundKind.Mp4;
            if (t.Contains("mpeg") || t.Contains("mp3")) return SoundKind.Mp3;
            return Sniff(head) is var s && s != SoundKind.Unknown ? s
                : t.StartsWith("text/") || t.StartsWith("video/") || t.Contains("html") ? SoundKind.Unsupported
                : SoundKind.Mp3;
        }

        internal static SoundKind Sniff(ReadOnlySpan<byte> head)
        {
            if (head.Length < 4) return SoundKind.Unknown;
            if (head.StartsWith("OggS"u8)) return SoundKind.Ogg;
            if (head.StartsWith("fLaC"u8)) return SoundKind.Flac;
            if (head.Length >= 8 && head.Slice(4, 4).SequenceEqual("ftyp"u8)) return SoundKind.Mp4;
            if (head.StartsWith("ID3"u8)) return SoundKind.Mp3;
            if (head[0] == 0xFF && (head[1] & 0xF6) == 0xF0) return SoundKind.Aac;
            if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return SoundKind.Mp3;
            return SoundKind.Unknown;
        }

        private static string Plain(Exception ex) => ex switch
        {
            StreamGone g => g.Message,
            HttpStatusException h => h.Message,
            SocketException s when s.SocketErrorCode == SocketError.HostNotFound => "the station's address could not be found",
            SocketException => "the station could not be reached",
            IOException { InnerException: SocketException { SocketErrorCode: SocketError.TimedOut } } => "the station stopped answering",
            IOException => "the connection to the station was lost",
            AuthenticationException => "the station's secure connection could not be trusted",
            OperationCanceledException or TimeoutException => "the station stopped answering",
            HttpRequestException hr => hr.Message,
            _ => ex.Message,
        };

        private sealed class HttpStatusException : Exception
        {
            public HttpStatusException(int status, string reason)
                : base(status == 404 ? "the station isn't there any more (404)"
                     : status == 403 ? "the station refused the connection (403)"
                     : $"the station answered {status} {reason}".TrimEnd()) { Status = status; }
            public int Status { get; }
        }

        // ---------- Plain HTTP, over our own socket ----------

        /// <summary>
        /// One GET, over a socket of our own rather than HttpClient: Shoutcast
        /// answers "ICY 200 OK", which HttpClient refuses, and a station is one
        /// long response anyway. Redirects followed.
        /// </summary>
        private Response HttpGet(Uri uri, bool icy)
        {
            for (int hop = 0; hop < 8; hop++)
            {
                if (Disposed) throw new OperationCanceledException();
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, ReceiveTimeout = StallMilliseconds, SendTimeout = 15_000 };
                lock (_gate) _socket = socket;
                Stream stream;
                try
                {
                    using (var connect = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token))
                    {
                        connect.CancelAfter(15_000);
                        socket.ConnectAsync(uri.DnsSafeHost, uri.Port, connect.Token).AsTask().GetAwaiter().GetResult();
                    }
                    stream = new NetworkStream(socket, ownsSocket: true) { ReadTimeout = StallMilliseconds };
                    if (uri.Scheme == Uri.UriSchemeHttps)
                    {
                        var ssl = new SslStream(stream, false) { ReadTimeout = StallMilliseconds };
                        ssl.AuthenticateAsClient(uri.IdnHost);
                        stream = ssl;
                    }
                }
                catch { socket.Dispose(); throw; }

                var host = uri.IsDefaultPort ? uri.IdnHost : uri.IdnHost + ":" + uri.Port;
                var request = $"GET {uri.PathAndQuery} HTTP/1.0\r\nHost: {host}\r\nUser-Agent: {UserAgent}\r\nAccept: */*\r\n" +
                              (icy ? "Icy-MetaData: 1\r\n" : "") + "Connection: close\r\n\r\n";
                var response = new Response(stream, uri);
                try
                {
                    var bytes = Encoding.ASCII.GetBytes(request);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                    response.ReadHeaders();
                }
                catch { response.Dispose(); throw; }

                if (response.Status is 301 or 302 or 303 or 307 or 308 && response.Header("location") is { } location)
                {
                    response.Dispose();
                    uri = new Uri(uri, location);
                    continue;
                }
                if (response.Status is not (200 or 206))
                {
                    var status = response.Status;
                    var reason = response.Reason;
                    response.Dispose();
                    if (status is 404 or 403 or 401 or 410) throw new StreamGone(new HttpStatusException(status, reason).Message);
                    throw new HttpStatusException(status, reason);
                }
                return response;
            }
            throw new StreamGone("the station kept sending it somewhere else");
        }

        internal static string UserAgent => "ExplorerNative/" + (typeof(RadioStream).Assembly.GetName().Version?.ToString(3) ?? "5") + " (Windows)";

        /// <summary>A response being read: headers, then the body, chunked or not.</summary>
        private sealed class Response : IDisposable
        {
            private readonly Stream _stream;
            private readonly byte[] _buffer = new byte[16 * 1024];
            private int _start, _count;
            private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);
            private bool _chunked;
            private long _chunkLeft = -1;
            private byte[] _peeked = Array.Empty<byte>();
            private int _peekedAt;

            public Response(Stream stream, Uri url) { _stream = stream; Url = url; }
            public Uri Url { get; }
            public int Status { get; private set; }
            public string Reason { get; private set; } = "";
            public string? Header(string name) => _headers.TryGetValue(name, out var v) ? v : null;
            public long? ContentLength => !_chunked && long.TryParse(Header("content-length"), out var n) ? n : null;

            private bool Fill()
            {
                if (_count > 0) return true;
                _start = 0;
                _count = _stream.Read(_buffer, 0, _buffer.Length);
                return _count > 0;
            }

            private string ReadLine()
            {
                var line = new StringBuilder();
                while (true)
                {
                    if (!Fill()) return line.ToString();
                    byte b = _buffer[_start++]; _count--;
                    if (b == '\n') return line.ToString().TrimEnd('\r');
                    if (line.Length > 16 * 1024) throw new IOException("a header line was too long");
                    line.Append((char)b);
                }
            }

            public void ReadHeaders()
            {
                var status = ReadLine();
                var parts = status.Split(' ', 3);
                if (parts.Length < 2 || !int.TryParse(parts[1], out var code))
                    throw new StreamGone("the station's answer made no sense");
                Status = code;
                Reason = parts.Length > 2 ? parts[2] : "";
                for (int i = 0; i < 200; i++)
                {
                    var line = ReadLine();
                    if (line.Length == 0) break;
                    int colon = line.IndexOf(':');
                    if (colon > 0) _headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                _chunked = Header("transfer-encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true;
            }

            private int ReadRaw(byte[] into, int offset, int count)
            {
                if (!Fill()) return 0;
                int n = Math.Min(count, _count);
                Buffer.BlockCopy(_buffer, _start, into, offset, n);
                _start += n; _count -= n;
                return n;
            }

            private int ReadBody(byte[] into, int offset, int count)
            {
                if (!_chunked) return ReadRaw(into, offset, count);
                if (_chunkLeft == 0) { ReadLine(); _chunkLeft = -1; }
                if (_chunkLeft < 0)
                {
                    var size = ReadLine().Split(';')[0].Trim();
                    if (!long.TryParse(size, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _chunkLeft) || _chunkLeft == 0)
                        return 0;
                }
                int n = ReadRaw(into, offset, (int)Math.Min(count, _chunkLeft));
                _chunkLeft -= n;
                return n;
            }

            public int Read(byte[] into, int offset, int count)
            {
                if (_peekedAt < _peeked.Length)
                {
                    int n = Math.Min(count, _peeked.Length - _peekedAt);
                    Buffer.BlockCopy(_peeked, _peekedAt, into, offset, n);
                    _peekedAt += n;
                    return n;
                }
                return ReadBody(into, offset, count);
            }

            /// <summary>The first bytes of the body, which are still read again afterwards.</summary>
            public byte[] Peek(int bytes)
            {
                var held = new byte[bytes];
                int got = 0;
                while (got < bytes)
                {
                    int n = ReadBody(held, got, bytes - got);
                    if (n <= 0) break;
                    got += n;
                }
                _peeked = held.AsSpan(0, got).ToArray();
                _peekedAt = 0;
                return _peeked;
            }

            public byte[] ReadAll(int max)
            {
                var all = new MemoryStream();
                var chunk = new byte[16 * 1024];
                while (all.Length < max)
                {
                    int n = Read(chunk, 0, chunk.Length);
                    if (n <= 0) break;
                    all.Write(chunk, 0, n);
                }
                return all.ToArray();
            }

            public void Dispose() { try { _stream.Dispose(); } catch { } }
        }

        /// <summary>Takes Shoutcast's song titles out from between the sound.</summary>
        internal sealed class IcyStripper
        {
            private readonly int _every;
            private readonly Action<string> _onMeta;
            private int _untilMeta;
            private int _metaLeft = -1;
            private byte[] _meta = Array.Empty<byte>();
            private int _metaGot;

            public IcyStripper(int every, Action<string> onMeta)
            {
                _every = every;
                _untilMeta = every;
                _onMeta = onMeta;
            }

            public void Feed(ReadOnlySpan<byte> data, Stream sound)
            {
                if (_every <= 0) { sound.Write(data); return; }
                int i = 0;
                while (i < data.Length)
                {
                    if (_metaLeft < 0 && _untilMeta > 0)
                    {
                        int n = Math.Min(_untilMeta, data.Length - i);
                        sound.Write(data.Slice(i, n));
                        _untilMeta -= n;
                        i += n;
                    }
                    else if (_metaLeft < 0)
                    {
                        _metaLeft = data[i++] * 16;
                        _meta = new byte[_metaLeft];
                        _metaGot = 0;
                        if (_metaLeft == 0) { _metaLeft = -1; _untilMeta = _every; }
                    }
                    else
                    {
                        int n = Math.Min(_metaLeft, data.Length - i);
                        data.Slice(i, n).CopyTo(_meta.AsSpan(_metaGot));
                        _metaGot += n;
                        _metaLeft -= n;
                        i += n;
                        if (_metaLeft == 0)
                        {
                            _metaLeft = -1;
                            _untilMeta = _every;
                            try { _onMeta(StreamPlaylists.Decode(_meta).TrimEnd('\0')); } catch { }
                        }
                    }
                }
            }
        }

        // ---------- HLS ----------

        private static readonly HttpClient Http = new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        })
        { Timeout = TimeSpan.FromSeconds(30) };

        private byte[] Get(Uri uri, out Uri final, (long Length, long Offset)? range = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            if (range is { } r) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(r.Offset, r.Offset + r.Length - 1);
            using var response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead, _cancel.Token);
            int status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                if (status is 404 or 403 or 401 or 410) throw new StreamGone(new HttpStatusException(status, response.ReasonPhrase ?? "").Message);
                throw new HttpStatusException(status, response.ReasonPhrase ?? "");
            }
            final = response.RequestMessage?.RequestUri ?? uri;
            using var body = response.Content.ReadAsStream(_cancel.Token);
            var all = new MemoryStream();
            // The client's timeout ends with the headers. A body that stops
            // arriving must not hold the network thread for good: each read
            // gets StallMilliseconds, and closing the station ends it at once.
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var stall = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
                stall.CancelAfter(StallMilliseconds);
                int n;
                try { n = body.ReadAsync(buffer, stall.Token).AsTask().GetAwaiter().GetResult(); }
                catch (OperationCanceledException) when (!_cancel.IsCancellationRequested)
                {
                    throw new IOException("the station stopped sending part way through");
                }
                finally { stall.CancelAfter(Timeout.Infinite); }
                if (n <= 0) break;
                all.Write(buffer, 0, n);
            }
            return all.ToArray();
        }

        private string GetText(Uri uri, out Uri final) => StreamPlaylists.Decode(Get(uri, out final));

        internal sealed record Variant(long Bandwidth, string? Codecs, string? Audio, bool HasVideo, Uri Uri);
        internal sealed record Rendition(string Group, bool Default, Uri Uri);
        internal sealed record Segment(long Sequence, Uri Uri, string? KeyMethod, Uri? KeyUri, byte[]? Iv, Uri? Map, (long Length, long Offset)? Range);
        internal sealed record MediaPlaylist(double TargetDuration, bool EndList, List<Segment> Segments);

        internal static Dictionary<string, string> Attributes(string line)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int colon = line.IndexOf(':');
            if (colon < 0) return result;
            foreach (Match m in Regex.Matches(line[(colon + 1)..], @"([A-Z0-9-]+)=(""[^""]*""|[^,]*)", RegexOptions.IgnoreCase))
                result[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
            return result;
        }

        private static Uri Resolve(Uri? baseUri, string text)
        {
            if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && absolute.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return absolute;
            if (baseUri == null) throw new StreamGone("the HLS playlist uses addresses that only work from where it was downloaded");
            return new Uri(baseUri, text);
        }

        /// <summary>
        /// The playlist to follow from a master playlist: an audio-only variant
        /// (the best of them) when there is one, else the default audio
        /// rendition, else the smallest variant, whose sound is taken out of the video.
        /// </summary>
        internal static Uri ChooseVariant(string text, Uri? baseUri)
        {
            var variants = new List<Variant>();
            var renditions = new List<Rendition>();
            var lines = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase))
                {
                    var a = Attributes(line);
                    if (a.TryGetValue("TYPE", out var type) && type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase) &&
                        a.TryGetValue("URI", out var u) && u.Length > 0)
                        renditions.Add(new Rendition(a.GetValueOrDefault("GROUP-ID", ""),
                            a.GetValueOrDefault("DEFAULT", "").Equals("YES", StringComparison.OrdinalIgnoreCase), Resolve(baseUri, u)));
                }
                else if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                {
                    var a = Attributes(line);
                    int j = i + 1;
                    while (j < lines.Length && (lines[j].Trim().Length == 0 || lines[j].TrimStart().StartsWith('#'))) j++;
                    if (j >= lines.Length) break;
                    long.TryParse(a.GetValueOrDefault("BANDWIDTH", "0"), out var bandwidth);
                    var codecs = a.GetValueOrDefault("CODECS");
                    bool video = a.ContainsKey("RESOLUTION") ||
                                 (codecs != null && Regex.IsMatch(codecs, @"avc|hvc|hev|vp0|vp9|av01|dvh", RegexOptions.IgnoreCase));
                    variants.Add(new Variant(bandwidth, codecs, a.GetValueOrDefault("AUDIO"), video, Resolve(baseUri, lines[j].Trim())));
                    i = j;
                }
            }
            if (variants.Count == 0) throw new StreamGone("the HLS playlist lists no streams");

            var audioOnly = variants.Where(v => !v.HasVideo).ToList();
            if (audioOnly.Count > 0) return audioOnly.OrderByDescending(v => v.Bandwidth).First().Uri;

            var smallest = variants.OrderBy(v => v.Bandwidth).First();
            var group = renditions.Where(r => smallest.Audio == null || r.Group == smallest.Audio).ToList();
            if (group.Count > 0) return (group.FirstOrDefault(r => r.Default) ?? group[0]).Uri;
            return smallest.Uri;
        }

        internal static MediaPlaylist ParseMedia(string text, Uri? baseUri)
        {
            double target = 6;
            long sequence = 0;
            bool end = false;
            string? keyMethod = null;
            Uri? keyUri = null, map = null;
            byte[]? iv = null;
            (long, long)? range = null;
            long nextRangeOffset = 0;
            var segments = new List<Segment>();
            int index = 0;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(line[22..], NumberStyles.Float, CultureInfo.InvariantCulture, out target);
                else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
                    long.TryParse(line[22..], out sequence);
                else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase)) end = true;
                else if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
                {
                    var a = Attributes(line);
                    keyMethod = a.GetValueOrDefault("METHOD", "NONE").ToUpperInvariant();
                    if (keyMethod == "NONE") { keyMethod = null; keyUri = null; iv = null; }
                    else
                    {
                        keyUri = a.TryGetValue("URI", out var u) ? Resolve(baseUri, u) : null;
                        iv = a.TryGetValue("IV", out var hex) ? ParseIv(hex) : null;
                    }
                }
                else if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
                {
                    var a = Attributes(line);
                    map = a.TryGetValue("URI", out var u) ? Resolve(baseUri, u) : null;
                }
                else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line[17..].Split('@');
                    if (long.TryParse(parts[0], out var length))
                    {
                        long offset = parts.Length > 1 && long.TryParse(parts[1], out var o) ? o : nextRangeOffset;
                        range = (length, offset);
                        nextRangeOffset = offset + length;
                    }
                }
                else if (!line.StartsWith('#'))
                {
                    segments.Add(new Segment(sequence + index, Resolve(baseUri, line), keyMethod, keyUri, iv, map, range));
                    index++;
                    range = null;
                }
            }
            return new MediaPlaylist(target > 0 ? target : 6, end, segments);
        }

        private static byte[]? ParseIv(string hex)
        {
            hex = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
            try { var bytes = Convert.FromHexString(hex.PadLeft(32, '0')); return bytes.Length == 16 ? bytes : null; }
            catch { return null; }
        }

        private long _hlsLast = -1;
        private string _hlsPlace = "";
        private const int GoneSegmentsMost = 3;

        private void RunHls(Uri? baseUri, string text)
        {
            if (text.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                var media = ChooseVariant(text, baseUri);
                text = GetText(media, out var final);
                baseUri = final;
            }
            var playlistUri = baseUri;

            // Where this station had got to survives a reconnect, so a dropped
            // connection does not play the last three segments over again.
            string place = playlistUri == null ? "" : playlistUri.Host + playlistUri.AbsolutePath;
            if (place != _hlsPlace) { _hlsPlace = place; _hlsLast = -1; }
            long last = _hlsLast;
            bool first = last < 0;
            int gone = 0;
            var keys = new Dictionary<Uri, byte[]>();
            Uri? mapSent = null;
            var demux = new TsAudio();
            Fmp4Audio? fmp4 = null;
            while (!Disposed)
            {
                var list = ParseMedia(text, baseUri);
                if (list.Segments.Count == 0 && list.EndList) { _finished = true; return; }

                int from = 0;
                if (first)
                {
                    from = list.EndList ? 0 : Math.Max(0, list.Segments.Count - 3);
                    first = false;
                }
                // Numbered again from the start (a station restarted): carry on with the new numbers.
                if (list.Segments.Count > 0 && list.Segments[^1].Sequence < last - list.Segments.Count) last = _hlsLast = list.Segments[0].Sequence - 1;

                bool fresh = false;
                for (int i = from; i < list.Segments.Count && !Disposed; i++)
                {
                    var segment = list.Segments[i];
                    if (segment.Sequence <= last) continue;
                    long waitFrom = Environment.TickCount64;
                    WaitForRoom();
                    // Paused a good while on a live station: this list is old
                    // and its segments may be gone. Ask for the list again.
                    if (!list.EndList && Environment.TickCount64 - waitFrom > Math.Max(list.TargetDuration, 6) * 3000) break;
                    if (segment.KeyMethod != null && (segment.KeyMethod != "AES-128" || segment.KeyUri == null))
                        throw new StreamGone("the stream is copy protected, so it can't be played here");
                    byte[] bytes;
                    byte[]? key = null;
                    try
                    {
                        bytes = Get(segment.Uri, out _, segment.Range);
                        if (segment.KeyMethod != null && segment.KeyUri != null && !keys.TryGetValue(segment.KeyUri, out key))
                            keys[segment.KeyUri] = key = Get(segment.KeyUri, out _);
                        gone = 0;
                    }
                    catch (StreamGone) when (!list.EndList && ++gone <= GoneSegmentsMost)
                    {
                        // One segment the server no longer has is not the
                        // station gone: step over it. Several in a row is.
                        last = _hlsLast = segment.Sequence;
                        continue;
                    }
                    if (segment.KeyMethod != null && key != null)
                    {
                        var iv = segment.Iv ?? SequenceIv(segment.Sequence);
                        using var aes = Aes.Create();
                        aes.Key = key;
                        bytes = aes.DecryptCbc(bytes, iv, PaddingMode.PKCS7);
                    }
                    if (segment.Map != null && segment.Map != mapSent)
                    {
                        fmp4 = new Fmp4Audio(Get(segment.Map, out _));
                        if (fmp4.Kind is not (SoundKind.Aac or SoundKind.Mp3))
                            throw Unplayable("its sound is in a format this player can't decode");
                        SetKind(fmp4.Kind);
                        mapSent = segment.Map;
                    }
                    if (fmp4 != null)
                    {
                        var sound = new MemoryStream();
                        fmp4.Feed(bytes, sound);
                        Push(sound.GetBuffer().AsSpan(0, (int)sound.Length));
                    }
                    else FeedSegment(demux, bytes);
                    last = _hlsLast = segment.Sequence;
                    fresh = true;
                }

                if (list.EndList) { _finished = true; return; }
                double wait = fresh ? list.TargetDuration : list.TargetDuration / 2;
                if (_cancel.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Clamp(wait, 1, 30)))) return;
                if (playlistUri == null) throw new StreamGone("the HLS playlist uses addresses that only work from where it was downloaded");
                text = GetText(playlistUri, out var again);
                baseUri = again;
            }
        }

        private static byte[] SequenceIv(long sequence)
        {
            var iv = new byte[16];
            for (int i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i));
            return iv;
        }

        private void FeedSegment(TsAudio demux, byte[] bytes)
        {
            if (IsTs(bytes)) { FeedTs(demux, bytes); return; }

            // Packed audio: an ID3 tag (the timestamp) in front of ADTS or MP3.
            int at = SkipId3(bytes);
            var sound = bytes.AsSpan(at);
            if (_kind != SoundKind.Unknown) { Push(sound); return; }
            var kind = Sniff(sound);
            if (kind == SoundKind.Unknown) throw Unplayable("its segments aren't a kind of sound this player knows");
            SetKind(kind);
            Push(sound);
        }

        internal static int SkipId3(ReadOnlySpan<byte> bytes)
        {
            int at = 0;
            while (bytes.Length - at >= 10 && bytes[at] == 'I' && bytes[at + 1] == 'D' && bytes[at + 2] == '3')
            {
                int size = ((bytes[at + 6] & 0x7F) << 21) | ((bytes[at + 7] & 0x7F) << 14) | ((bytes[at + 8] & 0x7F) << 7) | (bytes[at + 9] & 0x7F);
                bool footer = (bytes[at + 5] & 0x10) != 0;
                at += 10 + size + (footer ? 10 : 0);
            }
            return Math.Min(at, bytes.Length);
        }

        /// <summary>The first sound track out of an MPEG transport stream, as plain ADTS or MP3.</summary>
        internal sealed class TsAudio
        {
            private readonly byte[] _partial = new byte[188];
            private int _partialCount;
            private int _pmtPid = -1, _audioPid = -1;
            private bool _inPes;
            public SoundKind Kind { get; private set; }

            public void Feed(ReadOnlySpan<byte> data, Stream output)
            {
                int i = 0;
                if (_partialCount > 0)
                {
                    int n = Math.Min(188 - _partialCount, data.Length);
                    data[..n].CopyTo(_partial.AsSpan(_partialCount));
                    _partialCount += n;
                    i = n;
                    if (_partialCount < 188) return;
                    Packet(_partial, output);
                    _partialCount = 0;
                }
                while (i < data.Length)
                {
                    if (data[i] != 0x47) { i++; continue; }   // lost: find the next packet
                    if (data.Length - i < 188)
                    {
                        data[i..].CopyTo(_partial);
                        _partialCount = data.Length - i;
                        return;
                    }
                    Packet(data.Slice(i, 188), output);
                    i += 188;
                }
            }

            private void Packet(ReadOnlySpan<byte> p, Stream output)
            {
                int pid = ((p[1] & 0x1F) << 8) | p[2];
                bool start = (p[1] & 0x40) != 0;
                int control = (p[3] >> 4) & 3;
                if (control is 0 or 2) return;
                int at = 4;
                if (control == 3) at += 1 + p[4];
                if (at >= 188) return;

                if (pid == 0 && start) { ReadPat(p[at..]); return; }
                if (pid == _pmtPid && start) { ReadPmt(p[at..]); return; }
                if (pid != _audioPid || _audioPid < 0) return;

                if (start)
                {
                    var pes = p[at..];
                    if (pes.Length < 9 || pes[0] != 0 || pes[1] != 0 || pes[2] != 1) { _inPes = false; return; }
                    int headerLength = pes[8];
                    at += 9 + headerLength;
                    _inPes = true;
                    if (at >= 188) return;
                }
                if (_inPes) output.Write(p[at..]);
            }

            private void ReadPat(ReadOnlySpan<byte> s)
            {
                if (s.Length < 1 || 1 + s[0] > s.Length) return;
                s = s[(1 + s[0])..];
                if (s.Length < 8) return;
                int length = ((s[1] & 0x0F) << 8) | s[2];
                int end = Math.Min(s.Length, 3 + length - 4);
                for (int i = 8; i + 4 <= end; i += 4)
                {
                    int program = (s[i] << 8) | s[i + 1];
                    if (program == 0) continue;
                    _pmtPid = ((s[i + 2] & 0x1F) << 8) | s[i + 3];
                    return;
                }
            }

            private void ReadPmt(ReadOnlySpan<byte> s)
            {
                if (s.Length < 1 || 1 + s[0] > s.Length) return;
                s = s[(1 + s[0])..];
                if (s.Length < 12) return;
                int length = ((s[1] & 0x0F) << 8) | s[2];
                int end = Math.Min(s.Length, 3 + length - 4);
                int infoLength = ((s[10] & 0x0F) << 8) | s[11];
                bool sawUndecodable = false;
                for (int i = 12 + infoLength; i + 5 <= end;)
                {
                    int type = s[i];
                    int pid = ((s[i + 1] & 0x1F) << 8) | s[i + 2];
                    int esInfo = ((s[i + 3] & 0x0F) << 8) | s[i + 4];
                    var kind = type switch { 0x0F => SoundKind.Aac, 0x03 or 0x04 => SoundKind.Mp3, _ => SoundKind.Unknown };
                    if (kind != SoundKind.Unknown)
                    {
                        if (_audioPid != pid) _inPes = false;
                        _audioPid = pid;
                        Kind = kind;
                        return;
                    }
                    if (type is 0x81 or 0x87 or 0x11) sawUndecodable = true;
                    i += 5 + esInfo;
                }
                if (_audioPid < 0 && sawUndecodable) Kind = SoundKind.Unsupported;
            }
        }
    }

    /// <summary>
    /// The sound out of fragmented MP4 (HLS with #EXT-X-MAP): the init segment
    /// says which track is sound and how it is coded; each media segment's
    /// samples come out as ADTS (AAC) or plain frames (MP3), which Media
    /// Foundation reads from an endless stream where it will not read MP4.
    /// </summary>
    internal sealed class Fmp4Audio
    {
        private uint _track;
        private int _profile = 1, _rateIndex = 4, _channels = 2;
        private uint _defaultSize;
        public RadioStream.SoundKind Kind { get; private set; } = RadioStream.SoundKind.Unknown;

        public Fmp4Audio(byte[] init)
        {
            try { ReadInit(init); }
            catch { Kind = RadioStream.SoundKind.Unknown; }
        }

        private static uint U32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

        /// <summary>The boxes between <paramref name="start"/> and <paramref name="end"/>: type, where it starts, where its body starts, where it ends.</summary>
        internal static List<(string Type, int Start, int Body, int End)> Boxes(byte[] b, int start, int end)
        {
            var list = new List<(string, int, int, int)>();
            int at = start;
            while (at + 8 <= end)
            {
                long size = U32(b, at);
                string type = Encoding.ASCII.GetString(b, at + 4, 4);
                int header = 8;
                if (size == 1 && at + 16 <= end) { size = (long)((ulong)U32(b, at + 8) << 32 | U32(b, at + 12)); header = 16; }
                else if (size == 0) size = end - at;
                if (size < header || at + size > end) break;
                list.Add((type, at, at + header, (int)(at + size)));
                at += (int)size;
            }
            return list;
        }

        private static (string Type, int Start, int Body, int End)? Child(byte[] b, (string Type, int Start, int Body, int End) box, string type, int skip = 0)
        {
            foreach (var c in Boxes(b, box.Body + skip, box.End)) if (c.Type == type) return c;
            return null;
        }

        private void ReadInit(byte[] b)
        {
            var moov = Boxes(b, 0, b.Length).FirstOrDefault(x => x.Type == "moov");
            if (moov.Type == null) return;
            bool sawSound = false;
            foreach (var trak in Boxes(b, moov.Body, moov.End).Where(x => x.Type == "trak"))
            {
                var mdia = Child(b, trak, "mdia");
                var hdlr = mdia is { } m ? Child(b, m, "hdlr") : null;
                if (hdlr is not { } h || Encoding.ASCII.GetString(b, h.Body + 8, 4) != "soun") continue;
                sawSound = true;
                var tkhd = Child(b, trak, "tkhd");
                if (tkhd is not { } t) continue;
                uint track = U32(b, t.Body + (b[t.Body] == 1 ? 20 : 12));

                var stsd = Child(b, mdia!.Value, "minf") is { } minf && Child(b, minf, "stbl") is { } stbl ? Child(b, stbl, "stsd") : null;
                if (stsd is not { } sd) continue;
                var entry = Boxes(b, sd.Body + 8, sd.End).FirstOrDefault();
                if (entry.Type != "mp4a") continue;   // AC-3, E-AC-3, Opus or FLAC in MP4: not taken out here

                // A version 0 sound entry: 28 bytes, then its boxes.
                var esds = Boxes(b, entry.Body + 28, entry.End).FirstOrDefault(x => x.Type == "esds");
                if (esds.Type == null) continue;
                if (!ReadEsds(b, esds.Body + 4, esds.End)) continue;
                _track = track;

                var mvex = Child(b, moov, "mvex");
                if (mvex is { } mv)
                    foreach (var trex in Boxes(b, mv.Body, mv.End).Where(x => x.Type == "trex"))
                        if (U32(b, trex.Body + 4) == track) _defaultSize = U32(b, trex.Body + 16);
                return;
            }
            if (sawSound) Kind = RadioStream.SoundKind.Unsupported;
        }

        private static int Length(byte[] b, ref int at)
        {
            int length = 0;
            for (int i = 0; i < 4; i++)
            {
                byte x = b[at++];
                length = length << 7 | (x & 0x7F);
                if ((x & 0x80) == 0) break;
            }
            return length;
        }

        private bool ReadEsds(byte[] b, int at, int end)
        {
            if (at >= end || b[at++] != 0x03) return false;
            Length(b, ref at);
            at += 2;
            byte flags = b[at++];
            if ((flags & 0x80) != 0) at += 2;
            if ((flags & 0x40) != 0) at += 1 + b[at];
            if ((flags & 0x20) != 0) at += 2;
            if (at >= end || b[at++] != 0x04) return false;
            Length(b, ref at);
            byte objectType = b[at];
            at += 13;
            if (objectType is 0x6B or 0x69) { Kind = RadioStream.SoundKind.Mp3; return true; }
            if (objectType is not (0x40 or 0x66 or 0x67 or 0x68)) { Kind = RadioStream.SoundKind.Unsupported; return false; }
            if (at < end && b[at] == 0x05)
            {
                at++;
                Length(b, ref at);
                int aot = b[at] >> 3;
                _rateIndex = ((b[at] & 7) << 1) | (b[at + 1] >> 7);
                _channels = (b[at + 1] >> 3) & 0xF;
                // HE-AAC is sent as its AAC core; the decoder finds the rest itself.
                _profile = aot is 5 or 29 ? 1 : Math.Clamp(aot - 1, 0, 3);
                if (_rateIndex > 12) _rateIndex = 4;
            }
            Kind = RadioStream.SoundKind.Aac;
            return true;
        }

        public void Feed(byte[] b, Stream output)
        {
            if (Kind is not (RadioStream.SoundKind.Aac or RadioStream.SoundKind.Mp3)) return;
            int moofStart = -1;
            foreach (var box in Boxes(b, 0, b.Length))
            {
                if (box.Type != "moof") continue;
                moofStart = box.Start;
                foreach (var traf in Boxes(b, box.Body, box.End).Where(x => x.Type == "traf"))
                {
                    var tfhd = Child(b, traf, "tfhd");
                    if (tfhd is not { } th) continue;
                    uint flags = U32(b, th.Body) & 0xFFFFFF;
                    if (U32(b, th.Body + 4) != _track) continue;
                    int at = th.Body + 8;
                    long baseOffset = moofStart;
                    if ((flags & 0x1) != 0) { baseOffset = (long)((ulong)U32(b, at) << 32 | U32(b, at + 4)); at += 8; }
                    if ((flags & 0x2) != 0) at += 4;
                    if ((flags & 0x8) != 0) at += 4;
                    uint defaultSize = _defaultSize;
                    if ((flags & 0x10) != 0) defaultSize = U32(b, at);

                    long next = baseOffset;
                    foreach (var trun in Boxes(b, traf.Body, traf.End).Where(x => x.Type == "trun"))
                    {
                        uint rf = U32(b, trun.Body) & 0xFFFFFF;
                        uint count = U32(b, trun.Body + 4);
                        int p = trun.Body + 8;
                        long data = next;
                        if ((rf & 0x1) != 0) { data = baseOffset + (int)U32(b, p); p += 4; }
                        if ((rf & 0x4) != 0) p += 4;
                        for (uint i = 0; i < count && p <= trun.End; i++)
                        {
                            if ((rf & 0x100) != 0) p += 4;
                            uint size = defaultSize;
                            if ((rf & 0x200) != 0) { size = U32(b, p); p += 4; }
                            if ((rf & 0x400) != 0) p += 4;
                            if ((rf & 0x800) != 0) p += 4;
                            if (size == 0 || data + size > b.Length) break;
                            if (Kind == RadioStream.SoundKind.Aac) WriteAdtsHeader(output, (int)size);
                            output.Write(b, (int)data, (int)size);
                            data += size;
                        }
                        next = data;
                    }
                }
            }
        }

        private void WriteAdtsHeader(Stream output, int size)
        {
            int length = size + 7;
            Span<byte> h = stackalloc byte[7];
            h[0] = 0xFF;
            h[1] = 0xF1;
            h[2] = (byte)((_profile & 3) << 6 | (_rateIndex & 0xF) << 2 | (_channels >> 2) & 1);
            h[3] = (byte)((_channels & 3) << 6 | (length >> 11) & 3);
            h[4] = (byte)(length >> 3);
            h[5] = (byte)((length & 7) << 5 | 0x1F);
            h[6] = 0xFC;
            output.Write(h);
        }
    }

    /// <summary>
    /// A live stream's decoder: whatever reads the sound, with no length and
    /// no seeking, owning the stream so the two go together.
    /// </summary>
    internal sealed class LiveDecoder : ITrackDecoder
    {
        private readonly ITrackDecoder _inner;
        public RadioStream Radio { get; }
        public LiveDecoder(ITrackDecoder inner, RadioStream radio) { _inner = inner; Radio = radio; }
        public int SampleRate => _inner.SampleRate;
        public int Channels => _inner.Channels;
        public double Duration => 0;
        public double Position => _inner.Position;
        public int Read(float[] destination, int frames) => _inner.Read(destination, frames);
        public bool SeekTo(double seconds) => false;
        public bool Reopen(int sampleRate, int channels) => _inner.Reopen(sampleRate, channels);
        public void Dispose()
        {
            try { _inner.Dispose(); } catch { }
            try { Radio.Dispose(); } catch { }
        }

        /// <summary>Opens the right decoder for what the station is sending.</summary>
        public static ITrackDecoder? Open(RadioStream radio, string path, int sampleRate, int channels, out string why)
        {
            if (!radio.WaitForKind(TimeSpan.FromSeconds(30)))
            {
                why = radio.Problem.Length > 0 ? radio.Problem
                    : radio.Kind == RadioStream.SoundKind.Unsupported ? "it isn't a kind of sound this player knows"
                    : "the station did not answer in time";
                return null;
            }

            ITrackDecoder? decoder = null;
            try
            {
                decoder = OpenFor(radio, path, sampleRate, channels, out why);
            }
            finally { radio.Opening = false; }
            if (decoder == null) return null;
            why = "ready";
            return new LiveDecoder(decoder, radio);
        }

        private static ITrackDecoder? OpenFor(RadioStream radio, string path, int sampleRate, int channels, out string why)
        {
            ITrackDecoder? decoder = null;
            if (radio.Kind == RadioStream.SoundKind.Ogg)
            {
                var ogg = new OggDecoder();
                if (ogg.Open(path, radio, sampleRate, channels)) decoder = ogg;
                else { why = ogg.Diagnostic; ogg.Dispose(); return null; }
            }
            else
            {
                var media = new AudioDecoder();
                if (media.Open(path, radio, sampleRate, channels)) decoder = media;
                else
                {
                    why = radio.Problem.Length > 0 ? radio.Problem : media.Diagnostic;
                    media.Dispose();
                    return null;
                }
            }
            why = "ready";
            return decoder;
        }
    }
}
