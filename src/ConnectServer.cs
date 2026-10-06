using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>One row of a folder, as the web app sees it.</summary>
    public sealed record ConnectEntry(string Name, bool Folder, long Size, DateTime Modified);

    /// <summary>A folder's size, and whether the walk finished inside its budget.</summary>
    public sealed record ConnectSize(string Path, long Bytes, int Files, int Folders, bool Complete);

    /// <summary>One file or folder's details. <c>Tags</c> only for audio that has any.</summary>
    public sealed record ConnectStat(string Path, string Name, bool Folder, long Size, DateTime Modified,
        DateTime Created, bool ReadOnly, bool OnDrive,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SongSummary? Tags = null);

    public sealed record ConnectFailure(string Path, string Error);

    public sealed record ConnectDeleted(int Deleted, IReadOnlyList<ConnectFailure> Failed);

    /// <summary>
    /// A refusal with the HTTP status it deserves: 404 gone, 409 name taken, 403 not allowed, 503 Drive away.
    /// Anything else thrown out of an action is a 500.
    /// </summary>
    public sealed class ConnectException : Exception
    {
        public ConnectException(int status, string message, long? received = null) : base(message)
        {
            Status = status;
            Received = received;
        }

        public int Status { get; }

        /// <summary>For a resumable upload: how many bytes are held, so the web app can carry on from there.</summary>
        public long? Received { get; }
    }

    /// <summary>
    /// What the web app may do to files, as the application does it. The server parses, validates and routes;
    /// this does the work, through the same machinery the window uses (Drive trash and rename, the Recycle Bin,
    /// robocopy, DriveUpload). An interface so the server can be tested with a fake and no Drive at all.
    /// </summary>
    public interface IConnectFiles
    {
        /// <summary>Why a path on the Drive letter cannot be answered now, or null. The server says 503.</summary>
        string? Unavailable(string path);

        /// <summary>The Drive account's bytes used and limit (0 = no limit), or null when not mounted.</summary>
        (long Used, long Limit)? DriveQuota();

        Task<ConnectSize> SizeAsync(string folder, CancellationToken token);
        Task<ConnectStat> StatAsync(string path, CancellationToken token);

        /// <summary>Returns the new full path.</summary>
        Task<string> RenameAsync(string path, string newName, CancellationToken token);

        /// <summary>Returns the new folder's full path. A name already taken is a 409.</summary>
        Task<string> CreateFolderAsync(string parent, string name, CancellationToken token);

        Task<ConnectDeleted> DeleteAsync(IReadOnlyList<string> paths, CancellationToken token);

        /// <summary>Refusals cheap enough to give before a job exists: throws a <see cref="ConnectException"/>.</summary>
        void CheckTransfer(IReadOnlyList<string> paths, string destination, bool move);

        /// <summary>Runs a copy or move, reporting into <paramref name="job"/> and stopping on its token.</summary>
        Task TransferAsync(ConnectJob job, IReadOnlyList<string> paths, string destination, bool move,
            PasteConflictPolicy conflict);

        /// <summary>Writes <paramref name="body"/> as a file in <paramref name="folder"/>; returns its full path.
        /// May return without reading the body (a skipped clash).</summary>
        Task<string> UploadAsync(string folder, string name, PasteConflictPolicy conflict, Stream body,
            CancellationToken token);

        /// <summary>Whether a path is on the Drive letter.</summary>
        bool OnDrive(string path);

        /// <summary>
        /// Puts a finished resumable upload in place under the conflict policy: moved into a local folder, or sent
        /// up to Drive with <paramref name="progress"/> in bytes. <paramref name="file"/> is taken (moved or deleted
        /// by the caller afterwards). Returns the full path it landed at.
        /// </summary>
        Task<string> PlaceFileAsync(string file, string folder, string name, PasteConflictPolicy conflict,
            Action<long, long>? progress, CancellationToken token);

        /// <summary>The application's own audio extension list, as in settings.</summary>
        IReadOnlyList<string> AudioExtensions();

        /// <summary>
        /// Everything else worth knowing about a path, as <c>/api/stat</c>'s sections (file, folder, text, media,
        /// image, archive), inside the budget <paramref name="token"/> carries; <c>partial</c> when it ran out.
        /// </summary>
        Task<Dictionary<string, object?>> DetailsAsync(string path, ConnectStat stat, bool hash, CancellationToken token);
    }

    /// <summary>A copy or move the web app started, polled with <c>/api/job</c>.</summary>
    public sealed class ConnectJob
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly List<ConnectFailure> _failed = new();
        private string _state = "running";
        private int _items, _itemsDone;
        private long _bytes, _bytesDone;
        private string _current = "";
        private string _message = "";

        public ConnectJob(string id, string kind) { Id = id; Kind = kind; }

        public string Id { get; }
        public string Kind { get; }
        public CancellationToken Token => _cts.Token;
        public DateTime FinishedAt { get; private set; } = DateTime.MaxValue;

        public string State { get { lock (_gate) return _state; } }
        public int ItemsDone { get { lock (_gate) return _itemsDone; } }
        public IReadOnlyList<ConnectFailure> Failed { get { lock (_gate) return _failed.ToArray(); } }

        public void Report(int itemsDone, int items, long bytesDone, long bytes, string? current)
        {
            lock (_gate)
            {
                _itemsDone = Math.Max(0, itemsDone);
                _items = Math.Max(Math.Max(0, items), _itemsDone);
                _bytesDone = Math.Max(0, bytesDone);
                _bytes = Math.Max(Math.Max(0, bytes), _bytesDone);
                if (current != null) _current = current;
            }
        }

        public void Current(string current) { lock (_gate) _current = current; }

        private string? _path;

        /// <summary>Where an upload job's file landed; carried in the job as <c>path</c> once known.</summary>
        public void Landed(string path) { lock (_gate) _path = path; }

        public void Fail(string path, string error) { lock (_gate) _failed.Add(new ConnectFailure(path, error)); }

        private (int Succeeded, int Failed)? _counted;

        /// <summary>
        /// What the engine itself says arrived and what did not, in files. Its progress is no guide: robocopy
        /// counts a file it gave up on (ERROR 32, in use) as one it got through, so a copy of a locked file read
        /// as done with one item copied.
        /// </summary>
        public void Counted(int succeeded, int failed) { lock (_gate) _counted = (Math.Max(0, succeeded), Math.Max(0, failed)); }

        /// <summary>The engine's own count, when it gave one.</summary>
        public (int Succeeded, int Failed)? Counts { get { lock (_gate) return _counted; } }

        /// <summary>A sentence about the job as a whole; the last one said wins.</summary>
        public void Say(string message) { lock (_gate) _message = message; }

        public void Cancel() { try { _cts.Cancel(); } catch { } }

        public void Finish(string state, string? message = null)
        {
            lock (_gate)
            {
                _state = state;
                if (message != null) _message = message;
                // Done with nothing failed is all of it, whatever the engine last got round to reporting: a copy
                // under keep both goes by another route in robocopy and says nothing on the way.
                if (state == "done" && _failed.Count == 0 && (_counted == null || _counted.Value.Failed == 0))
                {
                    _itemsDone = _items;
                    _bytesDone = _bytes;
                }
                else if (_counted is { } counted)
                {
                    // What arrived, not what the progress got round to: a file that failed is not done.
                    _itemsDone = counted.Succeeded;
                    _items = Math.Max(_items, counted.Succeeded + counted.Failed);
                }
                _current = "";
                FinishedAt = DateTime.UtcNow;
            }
        }

        public object Snapshot()
        {
            lock (_gate)
                return new
                {
                    id = Id, kind = Kind, state = _state, items = _items, itemsDone = _itemsDone,
                    bytes = _bytes, bytesDone = _bytesDone, current = _current, message = _message,
                    failed = _failed.ToArray(), path = _path,
                };
        }
    }

    /// <summary>
    /// Remote files for the web app, one open download per path shared by every request for it.
    ///
    /// A phone scrubbing through a track sends a new Range request per drag, and AVPlayer keeps two
    /// connections to one file. Opened per request, each would start its own download from Google and throw
    /// it away a moment later. Instead each path gets one <see cref="StreamingSource"/> (the player's own
    /// window: head first, 4MB pieces following the reader, direct reads for what is not there yet), leased
    /// to requests and kept for <see cref="IdleFor"/> after the last one lets go.
    /// </summary>
    public sealed class ConnectStreams : IDisposable
    {
        public static readonly TimeSpan IdleFor = TimeSpan.FromSeconds(60);

        /// <summary>Open at once. The pool of the mount holds three files; so does this.</summary>
        public const int MostOpen = 3;

        private readonly Func<string, IRangeSource?> _open;
        private readonly long _budget;
        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _streams = new(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _sweep;

        private sealed class Entry
        {
            public required StreamingSource Stream;
            public int Leases;
            public DateTime LastUsed;
        }

        /// <param name="open">The remote source for a path, or null when it is not remote.</param>
        /// <param name="budget">Memory per file; a file within it is downloaded whole.</param>
        public ConnectStreams(Func<string, IRangeSource?> open, long budget = 256L << 20)
        {
            _open = open;
            _budget = budget;
            // Disarmed until a stream is opened, and disarmed again when the last one goes: a timer waking every
            // ten seconds to find nothing, for as long as the application runs, is battery spent on nothing.
            _sweep = new Timer(_ => Sweep(DateTime.UtcNow), null, Timeout.Infinite, Timeout.Infinite);
        }

        private bool _sweeping, _disposed;

        /// <summary>Whether the sweep timer is running, for tests. It runs only while a stream is held.</summary>
        public bool Sweeping { get { lock (_gate) return _sweeping; } }

        /// <summary>Starts or stops the sweep to match whether anything is held. Called under <see cref="_gate"/>.</summary>
        private void ArmSweep()
        {
            bool want = _streams.Count > 0 && !_disposed;
            if (want == _sweeping) return;
            _sweeping = want;
            try
            {
                if (want) _sweep.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                else _sweep.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch (ObjectDisposedException) { _sweeping = false; }
        }

        /// <summary>How many files are held open, for tests.</summary>
        public int Count { get { lock (_gate) return _streams.Count; } }

        /// <summary>A lease on the shared download for <paramref name="path"/>, or null when it is not remote.
        /// Disposing the lease does not stop the download.</summary>
        public IRangeSource? Lease(string path)
        {
            lock (_gate)
            {
                if (_streams.TryGetValue(path, out var held))
                {
                    held.Leases++;
                    held.LastUsed = DateTime.UtcNow;
                    return new Leased(this, path, held.Stream);
                }
            }

            var source = _open(path);
            if (source == null) return null;
            var stream = new StreamingSource(source, _budget);

            StreamingSource? loser = null, evicted = null;
            Leased lease;
            lock (_gate)
            {
                // Somebody else opened it while this one was being made: use theirs.
                if (_streams.TryGetValue(path, out var raced)) { loser = stream; stream = raced.Stream; raced.Leases++; raced.LastUsed = DateTime.UtcNow; }
                else
                {
                    if (_streams.Count >= MostOpen)
                    {
                        var idle = _streams.Where(e => e.Value.Leases == 0).OrderBy(e => e.Value.LastUsed).FirstOrDefault();
                        if (idle.Value != null) { evicted = idle.Value.Stream; _streams.Remove(idle.Key); }
                    }
                    _streams[path] = new Entry { Stream = stream, Leases = 1, LastUsed = DateTime.UtcNow };
                    ArmSweep();
                }
                lease = new Leased(this, path, stream);
            }
            loser?.Dispose();
            evicted?.Dispose();
            return lease;
        }

        private void Return(string path, StreamingSource stream)
        {
            lock (_gate)
                if (_streams.TryGetValue(path, out var e) && ReferenceEquals(e.Stream, stream))
                {
                    e.Leases = Math.Max(0, e.Leases - 1);
                    e.LastUsed = DateTime.UtcNow;
                }
        }

        /// <summary>Lets go of anything nobody has read for <see cref="IdleFor"/>.</summary>
        public void Sweep(DateTime now)
        {
            List<StreamingSource> done;
            lock (_gate)
            {
                var idle = _streams.Where(e => e.Value.Leases == 0 && now - e.Value.LastUsed >= IdleFor).ToList();
                foreach (var e in idle) _streams.Remove(e.Key);
                done = idle.Select(e => e.Value.Stream).ToList();
                ArmSweep();
            }
            foreach (var s in done) s.Dispose();
        }

        public void Dispose()
        {
            List<StreamingSource> all;
            lock (_gate)
            {
                _disposed = true;
                all = _streams.Values.Select(e => e.Stream).ToList();
                _streams.Clear();
                ArmSweep();
            }
            _sweep.Dispose();
            foreach (var s in all) s.Dispose();
        }

        private sealed class Leased : IRangeSource
        {
            private readonly ConnectStreams _owner;
            private readonly string _path;
            private readonly StreamingSource _stream;
            private int _returned;

            public Leased(ConnectStreams owner, string path, StreamingSource stream)
            {
                _owner = owner;
                _path = path;
                _stream = stream;
            }

            public long Length => _stream.Length;
            public int Streams => 1;

            /// <summary>
            /// <see cref="StreamingSource.ReadAt"/> blocks, so it runs on a worker; the caller stops waiting the
            /// moment its token fires, and the read, if it was going to Google, lands in the window for the next.
            /// </summary>
            public Task<int> ReadAsync(long offset, Memory<byte> into, CancellationToken token)
            {
                if (token.IsCancellationRequested) return Task.FromCanceled<int>(token);
                var stream = _stream;
                return Task.Run(() => stream.ReadAt(offset, into.Span), CancellationToken.None).WaitAsync(token);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _returned, 1) == 0) _owner.Return(_path, _stream);
            }
        }
    }

    /// <summary>
    /// Explorer Native Connect: the drives, folders and files of this machine, for the web app.
    ///
    /// Plain HTTP on 127.0.0.1 only (port 47810 unless Preferences says otherwise). Nothing on any network
    /// reaches it directly: the web app arrives through Tailscale Serve, which connects from this machine and
    /// names the tailnet user. The PC's owner needs no code; anyone else on the tailnet sends the pairing code.
    /// The web app in web\ is the only client, and what app.js calls is the contract: reading (<c>info</c>,
    /// <c>drives</c>, <c>list</c>, <c>file</c> with byte ranges, <c>size</c>, <c>details</c>) and writing
    /// (<c>rename</c>, <c>delete</c>, <c>mkdir</c>, <c>copy</c>/<c>move</c> as polled jobs, <c>upload</c>). The
    /// writing is done by an <see cref="IConnectFiles"/>; this class is HTTP, validation, the size cache and
    /// the job table.
    ///
    /// It is its own small HTTP server rather than HttpListener because HttpListener on any address but
    /// localhost needs an administrator to reserve the URL first, and rather than ASP.NET because this
    /// codebase ships no packages.
    ///
    /// Everything here runs on thread-pool workers. The listing hooks are how Drive stays fast: a Drive folder
    /// is answered from the listing the mount already holds instead of walking placeholders at fifteen
    /// milliseconds each.
    /// </summary>
    public sealed class ConnectServer : IDisposable
    {
        public const int Port = 47810;

        private readonly Func<string, IReadOnlyList<ConnectEntry>?> _tryListing;
        private readonly Func<string, IRangeSource> _open;
        private readonly Func<string> _code;
        private readonly Func<string, bool>? _isDrive;
        private readonly IConnectFiles? _files;
        private readonly Func<string, IRangeSource?>? _remote;
        private readonly ConnectUploads _uploads;
        private readonly IConnectClipboard? _clipboard;
        private readonly string _clipboardSends;

        /// <summary>Files sent from the web app to the clipboard are kept this long, then swept on start.</summary>
        public static readonly TimeSpan ClipboardSendsKeptFor = TimeSpan.FromDays(7);

        public static string DefaultClipboardSends => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExplorerNative", "connect-clipboard");
        private readonly Action<string>? _log;

        /// <summary>What the web app plays straight from <c>/api/file</c>. Everything else in the audio list goes
        /// through <c>/api/audio</c>; video containers always do, so only sound crosses the network.</summary>
        public static readonly string[] NativeFormats = { ".mp3", ".m4a", ".aac", ".flac", ".wav", ".aif", ".aiff", ".caf", ".alac" };

        /// <summary>Resumable uploads nobody has touched for this long are swept when the server starts.</summary>
        public static readonly TimeSpan UploadsKeptFor = TimeSpan.FromHours(24);

        /// <summary>How long a chunk's body may go without a byte before it is given up on; tests shorten it.</summary>
        internal TimeSpan UploadStallAfter
        {
            get => _uploads.StallAfter;
            set => _uploads.StallAfter = value;
        }
        private readonly CancellationTokenSource _stop = new();
        private TcpListener? _listener;
        private readonly object _gate = new();

        /// <summary>How long a folder size is remembered, and the local walk's budget.</summary>
        public static readonly TimeSpan SizeCacheFor = TimeSpan.FromSeconds(60);

        /// <summary>A JSON body bigger than this is not a request this API makes.</summary>
        private const int MaxJsonBody = 1 << 20;

        /// <summary>An upload refused before its body was read is drained, up to this, so the connection
        /// stays usable and the web app hears the answer instead of a reset.</summary>
        private const long MaxDrain = 256L << 20;

        private readonly ConcurrentDictionary<string, (Task<ConnectSize> Task, DateTime At)> _sizes =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ConnectJob> _jobs = new(StringComparer.Ordinal);

        /// <param name="tryListing">A folder's entries without touching the disk, when something already holds
        /// them (Drive); null to list it from the file system.</param>
        /// <param name="open">Bytes of a file at any offset.</param>
        /// <param name="code">The pairing code requests must carry.</param>
        /// <param name="loopbackOnly">Tests: do not run tailscale.exe for this PC's login.</param>
        /// <param name="isDrive">Whether a drive root is Google Drive. Windows reports the Drive letter as a
        /// "Local Disk" with C:'s space, because it is a subst of a folder there.</param>
        /// <param name="files">The file actions and Drive's quota; without it those routes answer 503.</param>
        /// <param name="remote">A shared download for a path that is not a local file (Drive), or null: what
        /// <c>/api/audio</c> decodes from instead of the letter.</param>
        /// <param name="uploads">Where resumable uploads are kept; <see cref="ConnectUploads.DefaultRoot"/> if null.</param>
        public ConnectServer(Func<string, IReadOnlyList<ConnectEntry>?> tryListing, Func<string, IRangeSource> open,
            Func<string> code, Action<string>? log = null, bool loopbackOnly = false, int port = Port,
            Func<string, bool>? isDrive = null, IConnectFiles? files = null,
            Func<string, IRangeSource?>? remote = null, string? uploads = null,
            IConnectClipboard? clipboard = null, string? clipboardSends = null, Func<string?>? tailscaleStatus = null)
        {
            _isDrive = isDrive;
            _files = files;
            _remote = remote;
            _uploads = new ConnectUploads(uploads ?? ConnectUploads.DefaultRoot);
            _clipboard = clipboard;
            _tailscaleStatus = tailscaleStatus ?? (loopbackOnly ? () => null : RunTailscaleStatus);
            _clipboardSends = clipboardSends ?? DefaultClipboardSends;
            _tryListing = tryListing;
            _open = open;
            _code = code;
            _log = log;
            ListenPort = port;
        }

        /// <summary>The port listened on, on 127.0.0.1. Changed by <see cref="Rebind"/>.</summary>
        public int ListenPort { get; private set; }

        /// <summary>Why the last attempt to listen failed, or null while listening.</summary>
        public string? ListenProblem { get; private set; }

        public void Start()
        {
            RefreshOwnLogin();
            _ = Task.Run(() =>
            {
                try
                {
                    int swept = _uploads.Sweep(UploadsKeptFor);
                    swept += SweepClipboardSends(ClipboardSendsKeptFor);
                    if (swept > 0) _log?.Invoke($"Connect: swept {swept} abandoned upload(s)");
                }
                catch (Exception e) { _log?.Invoke("Connect: sweep: " + e.Message); }
            });
            Rebind(ListenPort);
        }

        /// <summary>
        /// Listens on 127.0.0.1:<paramref name="port"/>, and only there. Nothing on any
        /// network reaches this server directly: the web app arrives through Tailscale
        /// Serve, which connects from this machine. Moving to another port keeps every
        /// job and upload. Returns false, with <see cref="ListenProblem"/> saying why,
        /// when the port cannot be had; the old listener is then kept.
        /// </summary>
        public bool Rebind(int port)
        {
            lock (_gate)
            {
                if (_stop.IsCancellationRequested) return false;
                if (_listener != null && port == ListenPort) return true;

                var next = new TcpListener(IPAddress.Loopback, port);
                try { next.Start(); }
                catch (SocketException e)
                {
                    ListenProblem = PortProblem(port, e);
                    _log?.Invoke("Connect: " + ListenProblem);
                    return false;
                }

                var old = _listener;
                _listener = next;
                ListenPort = port;
                ListenProblem = null;
                try { old?.Stop(); } catch { }
                _ = AcceptLoop(next);
                return true;
            }
        }

        /// <summary>A port that cannot be listened on, as a sentence.</summary>
        public static string PortProblem(int port, SocketException e) =>
            e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied
                ? $"Port {port} is already in use by another program."
                : $"Port {port} could not be used: {e.Message}";

        /// <summary>The ports a user may pick for the web app.</summary>
        public const int LowestPort = 1024, HighestPort = 65535;

        /// <summary>
        /// Whether <paramref name="port"/> can be used, as a sentence when it cannot.
        /// <paramref name="current"/> is the port in use now, which is ours and so free.
        /// Asks Windows which ports have listeners, then tries to bind one briefly.
        /// </summary>
        public static string? CheckPort(int port, int current)
        {
            if (port < LowestPort || port > HighestPort)
                return $"Choose a port from {LowestPort} to {HighestPort}.";
            if (port == current) return null;
            try
            {
                foreach (var l in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                    if (l.Port == port) return $"Port {port} is already in use by another program.";
            }
            catch { /* the bind below still answers */ }
            var probe = new TcpListener(IPAddress.Loopback, port);
            try { probe.Start(); return null; }
            catch (SocketException e) { return PortProblem(port, e); }
            finally { try { probe.Stop(); } catch { } }
        }

        /// <summary>A new pairing code: eight digits, shown as two groups of four.</summary>
        public static string NewCode()
        {
            Span<byte> b = stackalloc byte[4];
            RandomNumberGenerator.Fill(b);
            uint n = BitConverter.ToUInt32(b) % 100_000_000;
            return n.ToString("00000000");
        }

        private async Task AcceptLoop(TcpListener listener)
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                // Listening on loopback means only this machine connects; checked anyway.
                if (client.Client.RemoteEndPoint is not IPEndPoint peer || !IPAddress.IsLoopback(peer.Address.IsIPv4MappedToIPv6 ? peer.Address.MapToIPv4() : peer.Address))
                {
                    _log?.Invoke($"Connect: refused {client.Client.RemoteEndPoint}");
                    client.Dispose();
                    continue;
                }
                client.NoDelay = true;
                _ = Serve(client);
            }
        }

        // MARK: HTTP

        private sealed record Request(string Method, string Path, Dictionary<string, string> Query,
            Dictionary<string, string> Headers)
        {
            /// <summary>The body, which a GET does not have: empty unless Content-Length or chunked says so.</summary>
            public RequestBody Body { get; set; } = null!;

            /// <summary>The connection's socket, to notice the client hanging up mid-response.</summary>
            public Socket? Socket { get; set; }
        }

        private async Task Serve(TcpClient client)
        {
            using var _ = client;
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var stream = client.GetStream();
            var reader = new HeaderReader(stream);
            try
            {
                while (!connection.IsCancellationRequested)
                {
                    Request? request;
                    try { request = await reader.ReadAsync(HeadTimeout, IdleTimeout, connection.Token); }
                    catch (HeadRefusedException e)
                    {
                        // Too big, too slow or not HTTP: said, in JSON like every other answer, before closing.
                        await Refuse(client, stream, e, connection.Token);
                        return;
                    }
                    if (request == null) return;
                    bool keepAlive = !(request.Headers.TryGetValue("connection", out var c) && c.Equals("close", StringComparison.OrdinalIgnoreCase));
                    request.Body = RequestBody.For(request.Headers, reader, stream);
                    request.Socket = client.Client;
                    if (request.Body.Invalid)
                    {
                        await Error(stream, 400, "The request's length could not be understood.", false, connection.Token);
                        return;
                    }
                    await Handle(request, stream, connection.Token);

                    // Whatever of the body the route did not want has to be off the wire: before the next request
                    // head can be read, and before closing too, because closing a socket with unread bytes in it
                    // is a reset, and a reset can reach the client before the answer it was sent after does.
                    // Too much to drain, or a client still waiting for 100 Continue, and it is simply closed.
                    if (!request.Body.Finished)
                    {
                        if (!request.Body.Started && request.Body.ExpectsContinue) return;
                        if (!await request.Body.DrainAsync(MaxDrain, _uploads.StallAfter, connection.Token)) return;
                    }
                    if (!keepAlive)
                    {
                        try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
                        return;
                    }
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The web app went away mid-response: nothing to report.
            }
            catch (Exception e)
            {
                // Anything else would end up as an unobserved task nobody ever hears about.
                _log?.Invoke("Connect: " + e.Message);
            }
        }

        private async Task Handle(Request r, NetworkStream s, CancellationToken token)
        {
            // A HEAD answer carries the GET answer's headers and no body; sending the body anyway would be read
            // as the start of the next response on a kept-alive connection.
            bool head = r.Method == "HEAD";
            bool post = r.Method == "POST", put = r.Method == "PUT";
            if (r.Method != "GET" && !head && !post && !put) { await Error(s, 405, "Only GET, HEAD, POST and PUT.", false, token); return; }

            // Only under a name this server has, and anything that changes something only from its own page:
            // a site that points its own name at 127.0.0.1 is otherwise this server's own origin to a browser.
            var host = r.Headers.GetValueOrDefault("host");
            if (!HostAllowed(host, ListenPort, Volatile.Read(ref _ownDnsName)))
            {
                await Error(s, 421, "This server only answers to this PC's own names.", head, token);
                return;
            }
            if ((post || put) && !OriginAllowed(r.Headers.GetValueOrDefault("origin"), host))
            {
                await Error(s, 403, "That request came from another site, so it was refused.", false, token);
                return;
            }

            // The web app's own files: its shell, no data, so no code needed.
            if ((r.Method == "GET" || head) && WebAssets.TryGetAsset(r.Path, out var asset))
            {
                await Shell(s, r, asset, head, token);
                return;
            }

            // Wrong codes cost time: five free, then a wait that doubles to a minute. Before whoami too, which
            // checks a code and would otherwise be a way to try them all.
            if (await CodeThrottled(r, s, head, token)) return;

            // Who is asking, before any code: tells the web app whether to ask for one.
            if (r.Path == "/api/whoami")
            {
                bool trusted = TrustedTailscaleUser(r);
                await Json(s, 200, new
                {
                    needsCode = !trusted,
                    codeOk = CodeMatches(r),
                    user = r.Headers.TryGetValue("tailscale-user-name", out var un) ? un : null,
                    computer = Environment.MachineName,
                    apiVersion = 4,
                }, head, token);
                return;
            }

            if (!Authorised(r)) { await Error(s, 401, "Wrong or missing pairing code.", head, token); return; }
            string path = r.Query.TryGetValue("path", out var p) ? p : "";

            string wants = MethodFor(r.Path);
            if (wants.Length > 0 && !wants.Split('|').Contains(head ? "GET" : r.Method))
            {
                await Error(s, 405, $"That needs a {wants}.", head, token);
                return;
            }

            try
            {
                switch (r.Path)
                {
                    case "/api/info":
                        await Json(s, 200, new { name = Environment.MachineName, app = "Explorer Native", version = 1, apiVersion = 4 }, head, token);
                        break;
                    case "/api/drives":
                        await Json(s, 200, await Drives(), head, token, r);
                        break;
                    case "/api/list":
                        var entries = await List(path);
                        if (entries == null) await Error(s, 404, "That folder can't be opened.", head, token);
                        else await Json(s, 200, entries, head, token, r);
                        break;
                    case "/api/file":
                        await File(r, s, path, token);
                        break;
                    case "/api/size":
                        await Json(s, 200, await Size(path, token), head, token);
                        break;
                    // Not "/api/stat": "/stat?" is a common blocking rule, and a
                    // blocked request never reaches the PC.
                    case "/api/details":
                        await Json(s, 200, await Stat(path, r.Query.TryGetValue("hash", out var hv) && hv == "1", token), head, token, r);
                        break;
                    case "/api/job":
                        var job = FindJob(r.Query.TryGetValue("id", out var id) ? id : "");
                        await Json(s, 200, job.Snapshot(), head, token);
                        break;
                    case "/api/rename":
                        await Json(s, 200, await Rename(await ReadJson(r, token), token), false, token);
                        break;
                    case "/api/delete":
                        await Json(s, 200, await Delete(await ReadJson(r, token), token), false, token);
                        break;
                    case "/api/mkdir":
                        await Json(s, 200, await Mkdir(await ReadJson(r, token), token), false, token);
                        break;
                    case "/api/copy":
                    case "/api/move":
                        await Json(s, 202, StartTransfer(await ReadJson(r, token), r.Path == "/api/move"), false, token);
                        break;
                    case "/api/job/cancel":
                        await Json(s, 200, CancelJob(await ReadJson(r, token)), false, token);
                        break;
                    case "/api/upload":
                        await Json(s, 200, await Upload(r, token), false, token);
                        break;
                    case "/api/upload/start":
                        await Json(s, 200, await UploadStart(await ReadJson(r, token), token), false, token);
                        break;
                    case "/api/upload/chunk":
                        await Json(s, 200, await UploadChunk(r, token), false, token);
                        break;
                    case "/api/upload/status":
                        await Json(s, 200, UploadStatus(r.Query.TryGetValue("id", out var uid) ? uid : ""), head, token);
                        break;
                    case "/api/upload/finish":
                        var finishBody = await ReadJson(r, token);
                        var finishId = Str(finishBody, "id");
                        var (finishStatus, finished) = await Once(string.IsNullOrEmpty(finishId) ? null : "finish-" + finishId,
                            () => UploadFinish(finishBody, token), token);
                        await Json(s, finishStatus, finished, false, token);
                        break;
                    case "/api/upload/cancel":
                        await Json(s, 200, UploadCancel(await ReadJson(r, token)), false, token);
                        break;
                    case "/api/clipboard":
                        if (post) await Json(s, 200, new { ok = true, seq = await Clip.SetTextAsync(Str(await ReadJson(r, token), "text") ?? throw new ConnectException(400, "The body needs text.")) }, false, token);
                        else await Json(s, 200, Clip.Current, head, token, r);
                        break;
                    case "/api/clipboard/image":
                        if (post) await Json(s, 200, new { ok = true, seq = await Clip.SetImageAsync(await ReadAll(r, MaxClipboardImage, token)) }, false, token);
                        else await Png(s, Clip.Png() ?? throw new ConnectException(404, "The clipboard holds no image."), head, token);
                        break;
                    case "/api/clipboard/files":
                        await Json(s, 200, await ClipFiles(await ReadJson(r, token), token), false, token);
                        break;
                    case "/api/clipboard/send":
                        await Json(s, 200, await ClipSend(r, token), false, token);
                        break;
                    case "/api/clipboard/send/commit":
                        await Json(s, 200, await ClipCommit(await ReadJson(r, token), token), false, token);
                        break;
                    case "/api/formats":
                        await Json(s, 200, new { audio = Files.AudioExtensions(), native = NativeFormats }, head, token, r);
                        break;
                    case "/api/audio":
                        await Audio(r, s, path, token);
                        break;
                    default:
                        await Error(s, 404, "Not found.", head, token);
                        break;
                }
            }
            catch (ResponseStartedException)
            {
                // Headers are out: an error now cannot be an answer, only a closed connection.
                throw;
            }
            catch (ConnectException e)
            {
                if (e.Received is long received)
                    await Json(s, e.Status, new { error = e.Message, received }, head, token);
                else
                    await Error(s, e.Status, e.Message, head, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (InUse(e) && !token.IsCancellationRequested)
            {
                await Error(s, 423, InUseMessage, head, token);
            }
            catch (Exception e) when (e is not IOException || !token.IsCancellationRequested)
            {
                _log?.Invoke($"Connect: {r.Method} {r.Path} failed: {e.Message}");
                await Error(s, 500, Sentence(e.Message), head, token);
            }
        }

        /// <summary>A request head refused before it could be routed: answered with its status, then closed.</summary>
        private sealed class HeadRefusedException : Exception
        {
            public HeadRefusedException(int status, string message) : base(message) => Status = status;
            public int Status { get; }
        }

        /// <summary>How long a request head may take once its first byte is in. A head trickled a byte at a
        /// time otherwise held its connection for ever.</summary>
        internal TimeSpan HeadTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How long a kept-alive connection may sit with no request starting. Longer than the 90 s a Go client
        /// such as Serve keeps an idle connection, so it is the client that lets go first and never sends a
        /// request into a connection this end is closing.
        /// </summary>
        internal TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(120);

        /// <summary>
        /// Answers a refused head, then closes: the send side first, and what the client still sends is read
        /// and thrown away for a moment, because closing with unread bytes is a reset that can overtake the answer.
        /// </summary>
        private static async Task Refuse(TcpClient client, NetworkStream stream, HeadRefusedException e, CancellationToken token)
        {
            try
            {
                await Error(stream, e.Status, e.Message, false, token);
                client.Client.Shutdown(SocketShutdown.Send);
                using var linger = CancellationTokenSource.CreateLinkedTokenSource(token);
                linger.CancelAfter(1000);
                var scratch = new byte[16 * 1024];
                long drained = 0;
                int n;
                while (drained < (1 << 20) && (n = await stream.ReadAsync(scratch, linger.Token)) > 0) drained += n;
            }
            catch { /* closing anyway */ }
        }

        /// <summary>A failure after a response's headers were sent, which can only end the connection.</summary>
        private sealed class ResponseStartedException : IOException
        {
            public ResponseStartedException(Exception inner) : base(inner.Message, inner) { }
        }

        /// <summary>The one method a route answers (HEAD goes with GET), or "" for no such route.</summary>
        private static string MethodFor(string path) => path switch
        {
            "/api/info" or "/api/drives" or "/api/list" or "/api/file" or "/api/size" or "/api/details" or "/api/job"
                or "/api/upload/status" or "/api/formats" or "/api/audio" => "GET",
            "/api/rename" or "/api/delete" or "/api/mkdir" or "/api/copy" or "/api/move" or "/api/job/cancel"
                or "/api/upload" or "/api/upload/start" or "/api/upload/finish" or "/api/upload/cancel" => "POST",
            "/api/upload/chunk" => "PUT",
            "/api/clipboard/files" or "/api/clipboard/send" or "/api/clipboard/send/commit" => "POST",
            "/api/clipboard" or "/api/clipboard/image" => "GET|POST",
            _ => "",
        };

        /// <summary>An exception's message as something a person can read: capitalised, full stop.</summary>
        internal static string Sentence(string? message)
        {
            var m = (message ?? "").Trim();
            if (m.Length == 0) return "That didn't work.";
            if (m.Length > 400) m = m[..400] + "…";
            m = char.ToUpperInvariant(m[0]) + m[1..];
            return m.EndsWith('.') || m.EndsWith('?') || m.EndsWith('!') || m.EndsWith('…') ? m : m + ".";
        }

        // MARK: Actions

        /// <summary>A path the web app sent, checked for shape and for Drive being there to answer it.</summary>
        private string CheckPath(string? path, string what = "path")
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ConnectException(400, $"No {what} was given.");
            if (!Path.IsPathFullyQualified(path)) throw new ConnectException(400, $"The {what} has to be a full path, such as C:\\Music.");
            if (IsNetworkPath(path)) throw new ConnectException(403, NetworkPathRefusal);
            if (_files == null) throw new ConnectException(503, "This computer can't do that yet.");
            var away = _files.Unavailable(path);
            if (away != null) throw new ConnectException(503, Sentence(away));
            return path;
        }

        /// <summary>
        /// A share or device path: \\server\share, //server/share, \\?\UNC\..., \\.\... Opening one makes this PC
        /// connect to that server and offer the signed-in user's Windows credentials (NTLM) to it, so a path
        /// typed or crafted on a phone could hand them to any machine. The web app only ever browses drive
        /// letters, and a mapped network drive is one of those.
        /// </summary>
        public static bool IsNetworkPath(string? path) =>
            path != null && path.Length >= 2 && (path[0] is '\\' or '/') && (path[1] is '\\' or '/');

        internal const string NetworkPathRefusal =
            "Network paths can't be opened from the web app. Map the share to a drive letter on the PC and open that.";

        private static void CheckName(string? name)
        {
            var bad = NameRules.DescribeBadName(name);
            if (bad != null) throw new ConnectException(400, Sentence(bad));
        }

        private static bool IsRoot(string path) =>
            string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path) ?? ""),
                StringComparison.OrdinalIgnoreCase);

        private IConnectFiles Files => _files ?? throw new ConnectException(503, "This computer can't do that yet.");

        /// <summary>Anything written may have changed a size somebody has cached; the cache is cheap to lose.</summary>
        private void Changed() => _sizes.Clear();

        private async Task<ConnectSize> Size(string path, CancellationToken token)
        {
            CheckPath(path);
            var now = DateTime.UtcNow;
            foreach (var stale in _sizes.Where(e => now - e.Value.At > SizeCacheFor).Select(e => e.Key).ToList())
                _sizes.TryRemove(stale, out _);

            // One walk per folder however many ask at once: the entry is the task, stored before it finishes.
            var key = Path.TrimEndingDirectorySeparator(path);
            if (IsRoot(path)) key = path;
            var entry = _sizes.GetOrAdd(key, _ => (Files.SizeAsync(path, _stop.Token), now));
            try
            {
                return await entry.Task.WaitAsync(token);
            }
            catch
            {
                // A failure is not an answer worth keeping for a minute.
                _sizes.TryRemove(new KeyValuePair<string, (Task<ConnectSize>, DateTime)>(key, entry));
                throw;
            }
        }

        /// <summary>How long a whole /api/stat may take; what is known by then goes back with partial.</summary>
        public static readonly TimeSpan StatBudget = TimeSpan.FromSeconds(15);

        /// <summary>
        /// The v2 fields as they always were, then every section the files can give inside the budget
        /// (<see cref="IConnectFiles.DetailsAsync"/>). A section that fails is simply absent.
        /// </summary>
        private async Task<Dictionary<string, object?>> Stat(string path, bool hash, CancellationToken token)
        {
            CheckPath(path);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(StatBudget);
            var stat = await Files.StatAsync(path, budget.Token);
            var result = new Dictionary<string, object?>
            {
                ["path"] = stat.Path, ["name"] = stat.Name, ["folder"] = stat.Folder, ["size"] = stat.Size,
                ["modified"] = stat.Modified, ["created"] = stat.Created, ["readOnly"] = stat.ReadOnly, ["onDrive"] = stat.OnDrive,
            };
            Dictionary<string, object?> details;
            try { details = await Files.DetailsAsync(stat.Path, stat, hash, budget.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { details = new() { ["partial"] = true }; }
            catch (Exception e) { _log?.Invoke($"Connect: details of {path} failed: {e.Message}"); details = new(); }
            foreach (var (key, value) in details) result[key] = value;

            // The v2 tags object, for a client that reads it, from the media section when the basics had none.
            var tags = stat.Tags;
            if (tags == null && details.TryGetValue("media", out var m) && m is Dictionary<string, object?> media &&
                media.TryGetValue("tags", out var t) && t is List<NameValue> list)
            {
                string? Get(string n) => list.FirstOrDefault(x => x.Name == n)?.Value;
                int? Number(string n) => int.TryParse((Get(n) ?? "").Split(' ')[0], out var v) ? v : null;
                tags = new SongSummary(Get("Title"), Get("Artist"), Get("Album"), Number("Year"), Number("Track"),
                    media.TryGetValue("durationSeconds", out var d) && d is double seconds ? seconds : null);
            }
            if (tags != null) result["tags"] = tags;
            return result;
        }

        // MARK: Ping

        private readonly Func<string?> _tailscaleStatus;
        private DateTime _loginAt = DateTime.MinValue;
        private int _refreshingLogin;

        /// <summary>How long this PC's Tailscale login is believed before it is asked again.</summary>
        public static readonly TimeSpan LoginFor = TimeSpan.FromSeconds(30);

        /// <summary>For tests: waits for a login refresh already under way.</summary>
        internal async Task LoginSettled()
        {
            for (int i = 0; i < 100 && Volatile.Read(ref _refreshingLogin) == 1; i++) await Task.Delay(20);
        }

        /// <summary>
        /// Reads this PC's own Tailscale login behind the request that wanted it, at
        /// most every <see cref="LoginFor"/>. Tailscale missing or stopped leaves the
        /// login unknown, which trusts nobody without the code.
        /// </summary>
        private void RefreshOwnLogin()
        {
            if (Interlocked.Exchange(ref _refreshingLogin, 1) == 1) return;
            _ = Task.Run(() =>
            {
                try
                {
                    var json = _tailscaleStatus();
                    string? login = null, dns = null;
                    if (json != null)
                    {
                        var status = TailscaleWeb.ParseStatus(json);
                        if (status.State == TailscaleWeb.State.Running) login = status.Login;
                        dns = status.DnsName;
                    }
                    Volatile.Write(ref _ownLogin, login);
                    // Kept once known: Tailscale stopping for a moment does not change this PC's name.
                    if (!string.IsNullOrEmpty(dns)) Volatile.Write(ref _ownDnsName, dns.TrimEnd('.'));
                }
                catch (Exception e)
                {
                    Volatile.Write(ref _ownLogin, null);
                    _log?.Invoke("Connect: tailscale status: " + e.Message);
                }
                finally
                {
                    _loginAt = DateTime.UtcNow;
                    Volatile.Write(ref _refreshingLogin, 0);
                }
            });
        }

        /// <summary><c>tailscale status --json</c>, or null. Five seconds at most; nothing inherited, no window.</summary>
        public static string? RunTailscaleStatus()
        {
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
            if (!System.IO.File.Exists(exe)) exe = "tailscale.exe";
            var start = new System.Diagnostics.ProcessStartInfo(exe, "status --json")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8,
            };
            using var process = System.Diagnostics.Process.Start(start);
            if (process == null) return null;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000)) { try { process.Kill(); } catch { } return null; }
            return output.Wait(1000) ? output.Result : null;
        }

        private async Task<object> Rename(JsonElement body, CancellationToken token)
        {
            var path = CheckPath(Str(body, "path"));
            var newName = Str(body, "newName");
            CheckName(newName);
            if (IsRoot(path)) throw new ConnectException(400, "A drive can't be renamed.");
            var renamed = await Files.RenameAsync(path, newName!, token);
            Changed();
            return new { ok = true, path = renamed };
        }

        private async Task<object> Delete(JsonElement body, CancellationToken token)
        {
            var paths = Strs(body, "paths");
            if (paths.Count == 0) throw new ConnectException(400, "No paths were given.");
            foreach (var path in paths)
            {
                CheckPath(path);
                if (IsRoot(path)) throw new ConnectException(400, "A drive can't be deleted.");
            }
            var result = await Files.DeleteAsync(paths, token);
            Changed();
            return new { ok = true, deleted = result.Deleted, failed = result.Failed };
        }

        private async Task<object> Mkdir(JsonElement body, CancellationToken token)
        {
            var parent = CheckPath(Str(body, "parent"), "parent folder");
            var name = Str(body, "name");
            CheckName(name);
            var made = await Files.CreateFolderAsync(parent, name!, token);
            Changed();
            return new { ok = true, path = made };
        }

        private static PasteConflictPolicy Conflict(string? given) => (given ?? "rename").ToLowerInvariant() switch
        {
            "" or "rename" => PasteConflictPolicy.AutoRename,
            "skip" => PasteConflictPolicy.Skip,
            "overwrite" => PasteConflictPolicy.Overwrite,
            _ => throw new ConnectException(400, "Conflict has to be rename, skip or overwrite."),
        };

        private object StartTransfer(JsonElement body, bool move)
        {
            var paths = Strs(body, "paths");
            if (paths.Count == 0) throw new ConnectException(400, "No paths were given.");
            var destination = CheckPath(Str(body, "destination"), "destination");
            var conflict = Conflict(Str(body, "conflict"));
            foreach (var path in paths)
            {
                CheckPath(path);
                if (IsRoot(path)) throw new ConnectException(400, $"A whole drive can't be {(move ? "moved" : "copied")}; open it and pick what is in it.");
            }
            Files.CheckTransfer(paths, destination, move);

            var job = NewJob(move ? "move" : "copy");
            job.Report(0, paths.Count, 0, 0, "");
            _ = Task.Run(() => RunJob(job, paths, destination, move, conflict));
            return new { ok = true, job = job.Id };
        }

        private ConnectJob NewJob(string kind)
        {
            // Finished jobs are kept an hour, for a phone that was asleep when one ended.
            foreach (var old in _jobs.Values.Where(j => DateTime.UtcNow - j.FinishedAt > TimeSpan.FromHours(1)).ToList())
                _jobs.TryRemove(old.Id, out _);
            var job = new ConnectJob(Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(), kind);
            _jobs[job.Id] = job;
            return job;
        }

        private async Task RunJob(ConnectJob job, IReadOnlyList<string> paths, string destination, bool move,
            PasteConflictPolicy conflict)
        {
            try
            {
                await Files.TransferAsync(job, paths, destination, move, conflict);
                if (job.Token.IsCancellationRequested) job.Finish("cancelled", "Cancelled.");
                else
                {
                    var (state, message) = Outcome(job, move);
                    job.Finish(state, message);
                }
            }
            catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
            {
                job.Finish("cancelled", "Cancelled.");
            }
            catch (Exception e)
            {
                _log?.Invoke($"Connect: {job.Kind} job failed: {e.Message}");
                job.Finish("failed", Sentence(e.Message));
            }
            finally
            {
                Changed();
            }
        }

        /// <summary>
        /// How a copy or move that ran to the end came out, from the engine's own counts when it gave them:
        /// nothing arrived and something failed is failed; some of each is done, saying what did not go; nothing
        /// failed is done, with whatever the engine said (a skip under Skip is a message, never a failure).
        /// Without counts (Drive, which reports per item) the progress is all there is to go on.
        /// </summary>
        internal static (string State, string? Message) Outcome(ConnectJob job, bool move)
        {
            var failures = job.Failed;
            var counts = job.Counts;
            int failed = Math.Max(failures.Count, counts?.Failed ?? 0);
            if (failed == 0) return ("done", null);
            bool nothing = counts is { } c ? c.Succeeded == 0 : job.ItemsDone == 0;
            // Filed as sentences already; Sentence again would capitalise a file name at the front of one.
            string first = failures.Count > 0 && failures[0].Error.Trim().Length > 0
                ? (failures[0].Error.TrimEnd().EndsWith('.') ? failures[0].Error.Trim() : Sentence(failures[0].Error))
                : move ? "It could not be moved." : "It could not be copied.";
            if (nothing) return ("failed", first);
            int done = counts?.Succeeded ?? job.ItemsDone;
            return ("done", $"{(move ? "Moved" : "Copied")} {NameRules.Items(done, "file")}; " +
                            $"{NameRules.Items(failed, "file")} could not be {(move ? "moved" : "copied")}. {first}");
        }

        private ConnectJob FindJob(string id) =>
            !string.IsNullOrEmpty(id) && _jobs.TryGetValue(id, out var job)
                ? job
                : throw new ConnectException(404, "There is no job with that id.");

        private object CancelJob(JsonElement body)
        {
            var job = FindJob(Str(body, "id") ?? "");
            job.Cancel();
            return new { ok = true };
        }

        /// <summary>
        /// Uploads that have finished, by the id the web app gave them, with the answer they got. The answer to
        /// a finished upload can be lost on the way back, and the web app then sends it again: without this the
        /// file arrived twice, the second as "name (2)". Kept an hour.
        /// </summary>
        private readonly ConcurrentDictionary<string, (int Status, object Body, DateTime At)> _finished = new(StringComparer.Ordinal);

        private bool AlreadyFinished(string? key, out (int Status, object Body, DateTime At) answer)
        {
            foreach (var old in _finished.Where(e => DateTime.UtcNow - e.Value.At > TimeSpan.FromHours(1)).Select(e => e.Key).ToList())
                _finished.TryRemove(old, out _);
            answer = default;
            return key != null && _finished.TryGetValue(key, out answer);
        }

        private void Finished(string? key, int status, object body)
        {
            if (key != null) _finished[key] = (status, body, DateTime.UtcNow);
        }

        private async Task<object> Upload(Request r, CancellationToken token)
        {
            var folder = CheckPath(r.Query.TryGetValue("folder", out var f) ? f : "", "folder");
            var name = r.Query.TryGetValue("name", out var n) ? n : "";
            CheckName(name);
            var conflict = Conflict(r.Query.TryGetValue("conflict", out var c) ? c : null);
            string? key = r.Query.TryGetValue("uploadId", out var u) && BatchId(u) ? "upload-" + u : null;
            var (_, body) = await Once(key, async () =>
            {
                // No body is an empty file, which is a file like any other. A body that stops arriving is given
                // up on, and the connection with it: the rest of it could never be told from a next request.
                string path;
                try { path = await Files.UploadAsync(folder, name, conflict, new StallGuard(r.Body, _uploads.StallAfter), token); }
                catch (TimeoutException e) { throw new ResponseStartedException(e); }
                Changed();
                var answer = (200, (object)new { ok = true, path });
                Finished(key, answer.Item1, answer.Item2);
                return answer;
            }, token);
            return body;
        }

        /// <summary>Requests carrying an id that are being answered now, so a second with the same id waits.</summary>
        private readonly ConcurrentDictionary<string, Task<(int Status, object Body)>> _inFlight = new(StringComparer.Ordinal);

        /// <summary>
        /// Runs <paramref name="run"/> once for <paramref name="key"/>: a request already answered gets that answer,
        /// and one arriving while the first is still being answered waits for it rather than racing it. Four
        /// uploads with one id at once made three files. If the first fails, a waiting one tries for itself.
        /// </summary>
        private async Task<(int Status, object Body)> Once(string? key, Func<Task<(int Status, object Body)>> run,
            CancellationToken token)
        {
            if (key == null) return await run();
            while (true)
            {
                if (AlreadyFinished(key, out var done)) return (done.Status, done.Body);
                var mine = new TaskCompletionSource<(int Status, object Body)>(TaskCreationOptions.RunContinuationsAsynchronously);
                var held = _inFlight.GetOrAdd(key, mine.Task);
                if (held != mine.Task)
                {
                    try { return await held.WaitAsync(token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch { continue; }
                }
                try
                {
                    // The one before may have finished, and let go of its claim, between the look above and this
                    // claim: its answer is the answer, or the upload would be written a second time.
                    if (AlreadyFinished(key, out var late))
                    {
                        var earlier = (late.Status, late.Body);
                        mine.SetResult(earlier);
                        return earlier;
                    }
                    // run() records its own answer with Finished, before this lets go of the claim.
                    var answer = await run();
                    mine.SetResult(answer);
                    return answer;
                }
                catch (Exception e)
                {
                    mine.SetException(e);
                    _ = mine.Task.Exception;   // observed: nobody may be waiting
                    throw;
                }
                finally { _inFlight.TryRemove(new KeyValuePair<string, Task<(int Status, object Body)>>(key, mine.Task)); }
            }
        }

        // MARK: Clipboard

        private const int MaxClipboardImage = 64 << 20;

        private IConnectClipboard Clip => _clipboard ?? throw new ConnectException(503, "The clipboard isn't available on this computer.");

        private static async Task Png(NetworkStream s, byte[] png, bool head, CancellationToken token)
        {
            await Write(s, $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: {FileCsp}\r\n\r\n", token);
            if (!head) await s.WriteAsync(png, token);
        }

        private async Task<byte[]> ReadAll(Request r, int most, CancellationToken token)
        {
            if (!r.Body.HasBody) throw new ConnectException(400, "The image goes in the body.");
            if (r.Body.Length > most) throw new ConnectException(400, "That image is too big.");
            return (await ReadBody(r, most, "That image is too big.", token)).ToArray();
        }

        private async Task<object> ClipFiles(JsonElement body, CancellationToken token)
        {
            var paths = Strs(body, "paths");
            if (paths.Count == 0) throw new ConnectException(400, "No paths were given.");
            foreach (var path in paths) CheckPath(path);
            var missing = await Task.Run(() => paths.FirstOrDefault(p => !System.IO.File.Exists(ConnectFiles.Io(p)) && !Directory.Exists(ConnectFiles.Io(p))), token);
            if (missing != null) throw new ConnectException(404, $"{Path.GetFileName(missing)} is not there any more.");
            return new { ok = true, seq = await Clip.SetFilesAsync(paths) };
        }

        /// <summary>A batch id the web app chose: letters, digits and dashes, and nothing that can walk a path.</summary>
        private static bool BatchId(string? id) =>
            id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

        /// <summary>
        /// A phone file onto the clipboard: streamed to <c>connect-clipboard\&lt;folder&gt;\&lt;name&gt;</c>, then put
        /// on as a copied file. With a batch it waits in the batch's folder for the commit.
        /// </summary>
        private async Task<object> ClipSend(Request r, CancellationToken token)
        {
            var clip = Clip;
            var name = r.Query.TryGetValue("name", out var n) ? n : "";
            CheckName(name);
            string? batch = r.Query.TryGetValue("batch", out var b) && b.Length > 0 ? b : null;
            if (batch != null && !BatchId(batch)) throw new ConnectException(400, "A batch id is letters, digits and dashes.");

            // Named after the seq the clipboard is at when it arrives, and made unique: two sends can arrive
            // before the clipboard has moved.
            var folder = Path.Combine(_clipboardSends, batch != null ? "batch-" + batch
                : $"{clip.Current.Seq + 1}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}");
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, name);
            // Its own short name, so a long file name still has room; and a body that stops arriving is given
            // up on after the same stall a chunk is allowed, with the partial taken away.
            var partial = Path.Combine(folder, ConnectFiles.PartialName());
            try
            {
                await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                    await new StallGuard(r.Body, _uploads.StallAfter).CopyToAsync(file, 1 << 20, token);
                System.IO.File.Move(partial, target, overwrite: true);
            }
            catch (Exception e)
            {
                try { System.IO.File.Delete(partial); } catch { }
                if (batch == null) try { Directory.Delete(folder, true); } catch { }
                if (e is TimeoutException) throw new ResponseStartedException(e);
                throw;
            }
            if (batch != null) return new { ok = true, path = target };
            return new { ok = true, seq = await clip.SetFilesAsync(new[] { target }), path = target };
        }

        private async Task<object> ClipCommit(JsonElement body, CancellationToken token)
        {
            var batch = Str(body, "batch");
            if (!BatchId(batch)) throw new ConnectException(400, "A batch id is letters, digits and dashes.");
            var folder = Path.Combine(_clipboardSends, "batch-" + batch);
            var files = await Task.Run(() => Directory.Exists(folder)
                ? Directory.GetFiles(folder).Where(f => !f.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>(), token);
            if (files.Length == 0) throw new ConnectException(404, "Nothing has been sent in that batch.");
            return new { ok = true, seq = await Clip.SetFilesAsync(files), files };
        }

        /// <summary>Folders of sent files older than <paramref name="olderThan"/>; one level, ours only.</summary>
        internal int SweepClipboardSends(TimeSpan olderThan)
        {
            int swept = 0;
            try
            {
                if (!Directory.Exists(_clipboardSends)) return 0;
                foreach (var dir in Directory.EnumerateDirectories(_clipboardSends))
                {
                    try
                    {
                        if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) < olderThan) continue;
                        Directory.Delete(dir, recursive: true);
                        swept++;
                    }
                    catch { }
                }
            }
            catch { }
            return swept;
        }

        // MARK: Resumable uploads

        private async Task<object> UploadStart(JsonElement body, CancellationToken token)
        {
            var folder = CheckPath(Str(body, "folder"), "folder");
            var name = Str(body, "name");
            CheckName(name);
            var conflict = Str(body, "conflict") ?? "rename";
            Conflict(conflict);
            if (!body.TryGetProperty("size", out var sizeValue) || !sizeValue.TryGetInt64(out long size) || size < 0)
                throw new ConnectException(400, "The upload has to say its size in bytes.");

            var stat = await Files.StatAsync(folder, token);
            if (!stat.Folder) throw new ConnectException(400, "That is a file, not a folder.");

            var id = await Task.Run(() => _uploads.Start(new ConnectUploads.Meta(folder, name!, size, conflict.ToLowerInvariant(), DateTime.UtcNow)), token);
            return new { ok = true, id };
        }

        private ConnectUploads.Meta FindUpload(string? id) =>
            _uploads.Find(id) ?? throw new ConnectException(404, "There is no upload with that id.");

        private async Task<object> UploadChunk(Request r, CancellationToken token)
        {
            var id = r.Query.TryGetValue("id", out var i) ? i : "";
            FindUpload(id);
            if (!r.Query.TryGetValue("offset", out var o) || !long.TryParse(o, out long offset) || offset < 0)
                throw new ConnectException(400, "The chunk has to say its offset.");
            long received;
            try { received = await _uploads.AppendAsync(id, offset, r.Body, token); }
            catch (TimeoutException e)
            {
                // The body stopped arriving. What landed is kept and the upload is free for the next chunk; the
                // connection is closed, because the rest of a stalled body can never be told from a next request.
                throw new ResponseStartedException(e);
            }
            return new { ok = true, received };
        }

        private object UploadStatus(string id)
        {
            var meta = FindUpload(id);
            return new { received = _uploads.Received(id), size = meta.Size };
        }

        /// <summary>
        /// A local folder: moved into place now, 200 with the path. A Drive folder: 202 with a job that sends it
        /// up, in bytes, and throws the partial away when done.
        /// </summary>
        private async Task<(int Status, object Body)> UploadFinish(JsonElement body, CancellationToken token)
        {
            var id = Str(body, "id") ?? "";
            // Asked again because the answer was lost: the same answer, not a second file or a second job.
            if (AlreadyFinished("finish-" + id, out var again)) return (again.Status, again.Body);
            var meta = FindUpload(id);
            long received = _uploads.Received(id);
            if (received != meta.Size)
                throw new ConnectException(409, $"Only {received} of {meta.Size} bytes have arrived.", received);
            CheckPath(meta.Folder, "folder");
            var conflict = Conflict(meta.Conflict);
            var data = _uploads.DataPath(id);

            if (!Files.OnDrive(meta.Folder))
            {
                var placed = await Files.PlaceFileAsync(data, meta.Folder, meta.Name, conflict, null, token);
                _uploads.Delete(id);
                Changed();
                var answer = new { ok = true, path = placed };
                Finished("finish-" + id, 200, answer);
                return (200, answer);
            }

            var job = NewJob("upload");
            Finished("finish-" + id, 202, new { ok = true, job = job.Id });
            job.Report(0, 1, 0, meta.Size, meta.Name);
            _ = Task.Run(async () =>
            {
                try
                {
                    var placed = await Files.PlaceFileAsync(data, meta.Folder, meta.Name, conflict,
                        (sent, total) => job.Report(0, 1, sent, total, meta.Name), job.Token);
                    job.Landed(placed);
                    job.Report(1, 1, meta.Size, meta.Size, "");
                    _uploads.Delete(id);
                    job.Finish("done");
                }
                catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
                {
                    _finished.TryRemove("finish-" + id, out _);
                    job.Finish("cancelled", "Cancelled.");
                }
                catch (Exception e)
                {
                    // The partial is kept: the bytes are all here, and finishing again is one request.
                    _finished.TryRemove("finish-" + id, out _);
                    _log?.Invoke($"Connect: upload to Drive failed: {e.Message}");
                    job.Fail(Path.Combine(meta.Folder, meta.Name), Sentence(e.Message));
                    job.Finish("failed", Sentence(e.Message));
                }
                finally { Changed(); }
            });
            return (202, new { ok = true, job = job.Id });
        }

        private object UploadCancel(JsonElement body)
        {
            var id = Str(body, "id") ?? "";
            FindUpload(id);
            _uploads.Delete(id);
            return new { ok = true };
        }

        // MARK: Audio

        /// <summary>
        /// Any file the application plays, decoded to 24-bit WAV (<see cref="ConnectAudio"/>), with ranges mapped
        /// onto sample frames. The decoder runs on a worker and the bytes go out as they are made.
        /// </summary>
        private async Task Audio(Request r, NetworkStream s, string path, CancellationToken token)
        {
            bool headOnly = r.Method == "HEAD";
            CheckPath(path);

            var remote = await Task.Run(() => _remote?.Invoke(path), token);
            var io = ConnectFiles.Io(path);
            string why = "";
            var audio = await Task.Run(() =>
            {
                // A name ending in a dot or a space, anywhere in the path: by its plain name Windows opens a
                // different file. AIFF is read by its own reader, which opens the literal path itself; everything
                // else reaches the decoder as bytes, because Media Foundation does not take the literal form.
                var openAs = path;
                if (remote == null && !ReferenceEquals(io, path))
                {
                    if (AudioDecoder.IsAiffName(Path.GetExtension(path))) openAs = io;
                    else try { remote = new FileRangeSource(io); } catch { }
                }
                long bytes = remote?.Length ?? 0;
                if (remote == null)
                {
                    try { bytes = new FileInfo(io).Length; }
                    catch { bytes = 0; }
                }
                return ConnectAudio.Open(openAs, remote, bytes, out why);
            }, token);
            if (audio == null)
            {
                bool exists = remote != null || await Task.Run(() => System.IO.File.Exists(io), token);
                // A file another program holds is not one Windows cannot decode: it is in use, and says so.
                if (exists && remote == null && await Task.Run(() => Unreadable(io), token) is { } held && InUse(held))
                    throw new ConnectException(423, ConnectFiles.InUseSentence(path));
                throw new ConnectException(exists ? 500 : 404, exists
                    ? Sentence($"Explorer Native can't decode that: {why}")
                    : "No such file.");
            }

            using var _ = audio;
            long length = audio.Plan.TotalBytes;
            long from = 0, to = length - 1;
            int status = RangeStatus(r.Headers.GetValueOrDefault("range"), length, out from, out to);
            if (status == 416)
            {
                await Write(s, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{length}\r\nContent-Length: 0\r\n\r\n", token);
                return;
            }

            var head = new StringBuilder();
            head.Append(status == 206 ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            head.Append("Content-Type: audio/wav\r\nAccept-Ranges: bytes\r\nX-Content-Type-Options: nosniff\r\n");
            head.Append("Content-Security-Policy: ").Append(FileCsp).Append("\r\n");
            head.Append("Content-Length: ").Append(to - from + 1).Append("\r\n");
            if (status == 206) head.Append($"Content-Range: bytes {from}-{to}/{length}\r\n");
            head.Append("\r\n");
            await Write(s, head.ToString(), token);
            if (headOnly) return;

            using var gone = CancellationTokenSource.CreateLinkedTokenSource(token);
            var watch = WatchForHangUp(r.Socket, gone);
            try
            {
                await Task.Run(() => audio.WriteRange(from, to, s, gone.Token), gone.Token);
            }
            catch (Exception e) { throw new ResponseStartedException(e); }
            finally
            {
                gone.Cancel();
                try { await watch; } catch { }
            }
        }

        /// <summary>Why a local file cannot be read, opened as the player opens it and one byte read; null if it can.</summary>
        private static Exception? Unreadable(string io)
        {
            try
            {
                using var f = new FileStream(io, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
                f.ReadByte();
                return null;
            }
            catch (Exception e) { return e; }
        }

        /// <summary>
        /// A request body read whole into memory, given up on when no byte has come for the upload stall: a body
        /// trickled a byte at a time otherwise held the connection, and the memory, for as long as it liked. A
        /// stall closes the connection, because the rest of a stalled body could never be told from a next request.
        /// </summary>
        private async Task<MemoryStream> ReadBody(Request r, int most, string tooBig, CancellationToken token)
        {
            var body = new StallGuard(r.Body, _uploads.StallAfter);
            var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int got;
            try
            {
                while ((got = await body.ReadAsync(chunk, token)) > 0)
                {
                    buffer.Write(chunk, 0, got);
                    if (buffer.Length > most) throw new ConnectException(400, tooBig);
                }
            }
            catch (TimeoutException e) { throw new ResponseStartedException(e); }
            return buffer;
        }

        /// <summary>The request's JSON body. Anything else is a 400.</summary>
        private async Task<JsonElement> ReadJson(Request r, CancellationToken token)
        {
            if (r.Body.Length > MaxJsonBody) throw new ConnectException(400, "That request is too big.");
            var buffer = await ReadBody(r, MaxJsonBody, "That request is too big.", token);
            if (buffer.Length == 0) throw new ConnectException(400, "The request needs a JSON body.");
            try
            {
                using var doc = JsonDocument.Parse(buffer.ToArray());
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ConnectException(400, "The body has to be a JSON object.");
                return doc.RootElement.Clone();
            }
            catch (JsonException) { throw new ConnectException(400, "The body is not JSON."); }
        }

        private static string? Str(JsonElement body, string name) =>
            body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static List<string> Strs(JsonElement body, string name)
        {
            if (!body.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
                throw new ConnectException(400, $"{name} has to be a list of paths.");
            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    throw new ConnectException(400, $"{name} has to be a list of paths.");
                list.Add(item.GetString()!);
            }
            return list;
        }

        private bool Authorised(Request r) => CodeMatches(r) || TrustedTailscaleUser(r);

        private bool CodeMatches(Request r)
        {
            var a = Encoding.UTF8.GetBytes(GivenCode(r).Replace(" ", "").Replace("-", ""));
            var b = Encoding.UTF8.GetBytes(_code());
            return b.Length > 0 && CryptographicOperations.FixedTimeEquals(a, b);
        }

        /// <summary>
        /// A request Tailscale Serve proxied for the person who owns this PC. Serve
        /// connects from loopback and names the tailnet user in Tailscale-User-Login.
        /// </summary>
        private bool TrustedTailscaleUser(Request r)
        {
            if (DateTime.UtcNow - _loginAt > LoginFor) RefreshOwnLogin();
            var peer = (r.Socket?.RemoteEndPoint as IPEndPoint)?.Address;
            string? login = r.Headers.TryGetValue("tailscale-user-login", out var l) ? l : null;
            return TrustsServeUser(peer, login, Volatile.Read(ref _ownLogin));
        }

        /// <summary>
        /// The web app's rule, pure for the tests. Only a loopback connection can be
        /// Tailscale Serve, so only there is the header believed: a tailnet peer
        /// connecting straight to the port could send any header it likes. And only
        /// for this PC's own Tailscale login; anybody else on the tailnet enters the code.
        /// </summary>
        public static bool TrustsServeUser(IPAddress? peer, string? headerLogin, string? ownLogin)
        {
            if (peer == null || string.IsNullOrEmpty(headerLogin) || string.IsNullOrEmpty(ownLogin)) return false;
            if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
            return IPAddress.IsLoopback(peer) && string.Equals(headerLogin, ownLogin, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>This PC's Tailscale login, from <c>tailscale status --json</c>; null until read.</summary>
        private string? _ownLogin;

        /// <summary>This PC's ts.net name, which Serve forwards as the Host; null until read.</summary>
        private string? _ownDnsName;

        // MARK: Who may ask

        /// <summary>
        /// Whether a request's Host is this server under a name it really has: 127.0.0.1, localhost or [::1] on
        /// the port it listens on, or this PC's ts.net name, which is what Tailscale Serve forwards. Anything else
        /// is a page somewhere that has pointed its own name at 127.0.0.1 (DNS rebinding) and would otherwise be
        /// talking to this server as itself. Before the ts.net name is known, any ts.net name will do: nobody
        /// outside Tailscale can make one of those resolve here. No Host at all is not a browser.
        /// </summary>
        public static bool HostAllowed(string? host, int port, string? ownDnsName)
        {
            if (host == null) return true;
            if (!SplitHost(host, out var name, out var given)) return false;
            if (name is "127.0.0.1" or "localhost" or "::1")
                return given == null || given == port;
            if (!string.IsNullOrEmpty(ownDnsName))
                return name.Equals(ownDnsName.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) && (given == null || given == 443);
            return name.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) && (given == null || given == 443);
        }

        /// <summary>
        /// Whether a request that changes something came from the web app's own page: no Origin (not a browser,
        /// or a browser that did not say) or one naming the same host and port as the Host header. A page on any
        /// other origin is refused, whatever it managed to put in the headers.
        /// </summary>
        public static bool OriginAllowed(string? origin, string? host)
        {
            if (origin == null) return true;
            if (host == null || !Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var o) || o.Scheme is not ("http" or "https"))
                return false;
            if (!SplitHost(host, out var name, out var given)) return false;
            int hostPort = given ?? (o.Scheme == "https" ? 443 : 80);
            var originName = o.Host.Trim('[', ']').TrimEnd('.');
            return originName.Equals(name, StringComparison.OrdinalIgnoreCase) && o.Port == hostPort;
        }

        /// <summary>A Host header as a name (lower case, no trailing dot, IPv6 without brackets) and a port if given.</summary>
        private static bool SplitHost(string host, out string name, out int? port)
        {
            name = ""; port = null;
            host = host.Trim();
            if (host.Length == 0) return false;
            string portText = "";
            if (host[0] == '[')
            {
                int close = host.IndexOf(']');
                if (close < 0) return false;
                name = host[1..close];
                var rest = host[(close + 1)..];
                if (rest.Length > 0) { if (rest[0] != ':') return false; portText = rest[1..]; }
            }
            else
            {
                int colon = host.IndexOf(':');
                if (colon != host.LastIndexOf(':')) return false;
                name = colon < 0 ? host : host[..colon];
                if (colon >= 0) portText = host[(colon + 1)..];
            }
            if (portText.Length > 0)
            {
                if (!int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535) return false;
                port = p;
            }
            name = name.TrimEnd('.').ToLowerInvariant();
            return name.Length > 0;
        }

        /// <summary>Wrong codes a client may give before it is made to wait.</summary>
        public const int FreeCodeTries = 5;

        /// <summary>The longest wait for another try, reached by doubling from a second.</summary>
        public static readonly TimeSpan LongestCodeWait = TimeSpan.FromSeconds(60);

        /// <summary>Wrong codes are forgotten after this long without another.</summary>
        public static readonly TimeSpan CodeStrikesKeptFor = TimeSpan.FromMinutes(15);

        private sealed class Strikes
        {
            public int Count;
            public DateTime Until = DateTime.MinValue;
            public DateTime Last;
            public bool Said;
            /// <summary>The wrong codes already counted, so one stale code sent again and again counts once.</summary>
            public readonly HashSet<string> Codes = new(StringComparer.Ordinal);
        }

        /// <summary>Distinct wrong codes remembered per client; past this every wrong code counts.</summary>
        private const int MostCodesRemembered = 256;

        private readonly Dictionary<string, Strikes> _strikes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Raised once each time somebody starts guessing the pairing code (the first wait), with who: their
        /// Tailscale login, or their address.
        /// </summary>
        public event Action<string>? CodeGuessing;

        /// <summary>Who a code attempt is counted against: the tailnet user Serve names, or else the address.</summary>
        private static string ClientKey(Request r) =>
            r.Headers.TryGetValue("tailscale-user-login", out var l) && l.Length > 0 ? "user:" + l
                : "addr:" + ((r.Socket?.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?");

        /// <summary>How long <paramref name="key"/> must wait before trying a code again; zero for not at all.</summary>
        internal TimeSpan CodeWait(string key, DateTime now)
        {
            lock (_strikes)
            {
                foreach (var old in _strikes.Where(e => now - e.Value.Last > CodeStrikesKeptFor).Select(e => e.Key).ToList())
                    _strikes.Remove(old);
                return _strikes.TryGetValue(key, out var s) && s.Until > now ? s.Until - now : TimeSpan.Zero;
            }
        }

        /// <summary>
        /// A code tried by <paramref name="key"/>: a right one clears its record, a wrong one counts — once per
        /// distinct <paramref name="code"/>. A page polling with a code that has since changed sends the same
        /// wrong code every second; that is one stale code, not somebody guessing, and must not set off the
        /// spoken warning. Null counts every time.
        /// </summary>
        internal void CodeTried(string key, bool right, DateTime now, string? code = null)
        {
            bool say = false;
            lock (_strikes)
            {
                if (right) { _strikes.Remove(key); return; }
                if (!_strikes.TryGetValue(key, out var s)) _strikes[key] = s = new Strikes();
                if (code != null && s.Codes.Contains(code)) return;
                if (code != null && s.Codes.Count < MostCodesRemembered) s.Codes.Add(code);
                s.Count++;
                s.Last = now;
                if (s.Count >= FreeCodeTries)
                {
                    // One second after the fifth, then doubling: 2, 4, ... and never more than a minute.
                    double seconds = Math.Min(LongestCodeWait.TotalSeconds, Math.Pow(2, Math.Min(30, s.Count - FreeCodeTries)));
                    s.Until = now + TimeSpan.FromSeconds(seconds);
                    if (!s.Said) { s.Said = say = true; }
                }
            }
            if (say)
            {
                var who = key[5..];   // past "user:" or "addr:"
                _log?.Invoke($"Connect: {FreeCodeTries} wrong pairing codes from {who}; each try now waits");
                try { CodeGuessing?.Invoke(who); } catch { }
            }
        }

        /// <summary>The code a request carries, as typed: header first, then the query.</summary>
        private static string GivenCode(Request r) =>
            r.Headers.TryGetValue("x-connect-code", out var h) ? h
                : r.Query.TryGetValue("code", out var q) ? q : "";

        /// <summary>
        /// Refuses a client still waiting out its wrong codes (429, with Retry-After), and counts the code this
        /// request carries. The owner, who comes in by Serve's identity and not by a code, is never counted and
        /// never made to wait; nor is a request with no code, which is only asking whether one is needed.
        /// </summary>
        private async Task<bool> CodeThrottled(Request r, NetworkStream s, bool head, CancellationToken token)
        {
            if (TrustedTailscaleUser(r)) return false;
            if (GivenCode(r).Length == 0) return false;
            var key = ClientKey(r);
            var now = DateTime.UtcNow;
            var wait = CodeWait(key, now);
            if (wait > TimeSpan.Zero)
            {
                int seconds = (int)Math.Ceiling(wait.TotalSeconds);
                var body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    error = $"Too many wrong pairing codes. Wait {NameRules.Items(seconds, "second")}, then try again.",
                    retryAfter = seconds,
                }, JsonOptions);
                await Write(s, $"HTTP/1.1 429 Too Many Requests\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
                               $"Retry-After: {seconds}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n\r\n", token);
                if (!head) await s.WriteAsync(body, token);
                return true;
            }
            CodeTried(key, CodeMatches(r), now, GivenCode(r));
            return false;
        }

        // MARK: Drives and folders

        /// <summary>Every drive that answers within two seconds. A network drive whose server is asleep is left
        /// out rather than holding up the list.</summary>
        private async Task<List<object>> Drives()
        {
            var probes = DriveInfo.GetDrives().Select(d => (Drive: d, Task: Task.Run(() =>
            {
                try
                {
                    // The Drive letter is a subst of a folder on C:, so what Windows says about its space is C:'s.
                    // The account's quota is the truth; no limit reads as unlimited, with what is used still said.
                    if (_isDrive?.Invoke(d.Name) == true)
                    {
                        var (used, limit) = _files?.DriveQuota() ?? (0, 0);
                        return limit > 0
                            ? (object)new { name = d.Name, label = "Google Drive", kind = "GoogleDrive", size = limit, free = Math.Max(0, limit - used), used = Math.Min(used, limit), unlimited = false }
                            : new { name = d.Name, label = "Google Drive", kind = "GoogleDrive", size = 0L, free = 0L, used, unlimited = true };
                    }
                    if (!d.IsReady) return null;
                    long size = d.TotalSize, free = d.AvailableFreeSpace;
                    return new { name = d.Name, label = d.VolumeLabel, kind = d.DriveType.ToString(), size, free, used = Math.Max(0, size - free), unlimited = false };
                }
                catch { return null; }
            }))).ToList();
            await Task.WhenAny(Task.WhenAll(probes.Select(p => p.Task)), Task.Delay(2000));
            return probes.Where(p => p.Task.IsCompletedSuccessfully && p.Task.Result != null).Select(p => p.Task.Result!).ToList();
        }

        /// <summary>A folder's entries, folders first then by name, or null if it can't be read in ten seconds.</summary>
        public async Task<List<ConnectEntry>?> List(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || IsNetworkPath(path)) return null;
            var listing = Task.Run(() =>
            {
                var held = _tryListing(path);
                if (held != null) return held.ToList();
                var dir = new DirectoryInfo(ConnectFiles.Io(path));
                if (!dir.Exists) return null;
                var list = new List<ConnectEntry>();
                foreach (var info in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden }))
                {
                    bool folder = info is DirectoryInfo;
                    list.Add(new ConnectEntry(info.Name, folder, folder ? 0 : ((FileInfo)info).Length, info.LastWriteTimeUtc));
                }
                return list;
            });
            if (await Task.WhenAny(listing, Task.Delay(45_000)) != listing || !listing.IsCompletedSuccessfully) return null;
            var entries = listing.Result;
            entries?.Sort((a, b) => a.Folder != b.Folder ? (a.Folder ? -1 : 1) : NameRules.CompareNames(a.Name, a.Name, b.Name, b.Name));
            return entries;
        }

        // MARK: Files

        private async Task File(Request r, NetworkStream s, string path, CancellationToken token)
        {
            bool headOnly = r.Method == "HEAD";
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) { await Error(s, 404, "No such file.", headOnly, token); return; }
            if (IsNetworkPath(path)) { await Error(s, 403, NetworkPathRefusal, headOnly, token); return; }
            var away = _files?.Unavailable(path);
            if (away != null) { await Error(s, 503, Sentence(away), headOnly, token); return; }
            IRangeSource source;
            // On a worker: opening a Drive file can be a listing of its folder. A local name ending in a dot or a
            // space is opened by its literal path, or Windows opens a different file; Drive's names never do.
            var open = _files?.OnDrive(path) == true ? path : ConnectFiles.Io(path);
            try { source = await Task.Run(() => _open(open), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (InUse(e)) { await Error(s, 423, InUseMessage, headOnly, token); return; }
            catch { await Error(s, 404, "That file can't be opened.", headOnly, token); return; }
            using var _ = source;

            long length = source.Length;
            long from = 0, to = length - 1;
            int status = length > 0 ? RangeStatus(r.Headers.GetValueOrDefault("range"), length, out from, out to) : 200;
            if (status == 416)
            {
                await Write(s, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{length}\r\nContent-Length: 0\r\n\r\n", token);
                return;
            }
            long count = length == 0 ? 0 : to - from + 1;
            var head = new StringBuilder();
            head.Append(status == 206 ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            head.Append(FileHeaders(path));
            head.Append("Accept-Ranges: bytes\r\n");
            head.Append("Content-Length: ").Append(count).Append("\r\n");
            if (status == 206) head.Append($"Content-Range: bytes {from}-{to}/{length}\r\n");
            head.Append("\r\n");
            await Write(s, head.ToString(), token);
            if (headOnly || count == 0) return;

            // A phone scrubbing through a track drops a request the moment the finger moves on. Noticing only
            // when the next write fails would leave a Drive read running for nothing, so the socket is watched
            // while the body goes out and the reads are called off as soon as the other end has gone.
            using var gone = CancellationTokenSource.CreateLinkedTokenSource(token);
            var watch = WatchForHangUp(r.Socket, gone);

            try
            {
                // The first piece small, so the web app has bytes to decode as soon as there are any; then big.
                var buffer = new byte[256 * 1024];
                long offset = from, remaining = count;
                int piece = 64 * 1024;
                while (remaining > 0)
                {
                    int want = (int)Math.Min(piece, remaining);
                    int got = await source.ReadAsync(offset, buffer.AsMemory(0, want), gone.Token);
                    if (got <= 0) throw new IOException("the file ended early");
                    await s.WriteAsync(buffer.AsMemory(0, got), gone.Token);
                    offset += got;
                    remaining -= got;
                    piece = buffer.Length;
                }
            }
            catch (Exception e) { throw new ResponseStartedException(e); }
            finally
            {
                gone.Cancel();
                try { await watch; } catch { }
            }
        }

        /// <summary>Cancels <paramref name="gone"/> when the client closes its end, checking five times a second.</summary>
        private static async Task WatchForHangUp(Socket? socket, CancellationTokenSource gone)
        {
            if (socket == null) return;
            try
            {
                while (!gone.IsCancellationRequested)
                {
                    await Task.Delay(200, gone.Token);
                    // Readable with nothing to read is how a closed connection looks from this side.
                    if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0) { gone.Cancel(); return; }
                }
            }
            catch { }
        }

        /// <summary>One byte range: "bytes=a-b", "bytes=a-" or "bytes=-n".</summary>
        public static bool TryParseRange(string header, long length, out long from, out long to) =>
            RangeStatus(header, length, out from, out to) == 206;

        /// <summary>
        /// What a Range header asks of a file this long: 206 with the bytes, 416 when it is a range that cannot
        /// fit (a start at or past the end, a suffix of nothing), and 200 for the whole file when it is not a
        /// range this server reads at all (another unit, a malformed one, an end before its start), which is
        /// what RFC 9110 says to do with one. An end too big for a number is the end of the file.
        /// </summary>
        public static int RangeStatus(string? header, long length, out long from, out long to)
        {
            from = 0; to = length - 1;
            if (string.IsNullOrWhiteSpace(header) || !header.TrimStart().StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return 200;
            var spec = header.TrimStart()[6..].Split(',')[0].Trim();
            int dash = spec.IndexOf('-');
            if (dash < 0) return 200;
            string a = spec[..dash].Trim(), b = spec[(dash + 1)..].Trim();
            static bool Digits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);
            if (a.Length == 0)
            {
                if (!Digits(b)) return 200;
                // A suffix longer than any number is longer than the file: all of it.
                long suffix = long.TryParse(b, out var n) ? n : long.MaxValue;
                if (suffix == 0 || length <= 0) return 416;
                from = Math.Max(0, length - suffix);
                return 206;
            }
            if (!Digits(a) || (b.Length > 0 && !Digits(b))) return 200;
            if (!long.TryParse(a, out from) || from >= length) { from = 0; return 416; }
            if (b.Length > 0)
            {
                long end = long.TryParse(b, out var e) ? e : long.MaxValue;
                if (end < from) { from = 0; return 200; }
                to = Math.Min(end, length - 1);
            }
            return 206;
        }

        /// <summary>
        /// What every response carrying a file's own bytes is sent with (<c>/api/file</c>, <c>/api/audio</c>). The
        /// bytes are somebody's file, served on the web app's own origin: an HTML page opened from the PC ran as
        /// the app, with the owner's access to everything. So the browser is told never to guess a type
        /// (nosniff, which also stops anything not typed as a script from loading as one), and the file is a
        /// sandbox with no origin and no scripts. Images and media still show when opened on their own.
        /// </summary>
        public const string FileCsp = "sandbox; default-src 'none'; img-src 'self' data: blob:; media-src 'self' blob:; style-src 'unsafe-inline'";

        /// <summary>Kinds a browser would run as a page or a script. They download, typed as plain text, never shown.</summary>
        public static readonly IReadOnlySet<string> ActiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".html", ".htm", ".xhtml", ".xht", ".shtml", ".mht", ".mhtml", ".hta", ".svg", ".svgz", ".xml", ".xsl", ".xslt",
            ".js", ".mjs", ".cjs",
        };

        /// <summary>Content-Type, Content-Disposition, nosniff and the sandbox, each line ending in CRLF.</summary>
        internal static string FileHeaders(string path)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            bool active = ActiveExtensions.Contains(Path.GetExtension(name));
            var type = active ? "text/plain; charset=utf-8" : ContentType(path);
            // The plain name for old readers, letters and digits only, and the real one encoded beside it.
            var plain = new string(name.Select(c => c is >= ' ' and < (char)127 and not '"' and not '\\' and not ';' ? c : '_').ToArray());
            return $"Content-Type: {type}\r\n" +
                   $"Content-Disposition: {(active ? "attachment" : "inline")}; filename=\"{plain}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}\r\n" +
                   "X-Content-Type-Options: nosniff\r\n" +
                   $"Content-Security-Policy: {FileCsp}\r\n";
        }

        /// <summary>A file another program holds open so that nothing else may read or move it.</summary>
        internal static bool InUse(Exception e) =>
            e is IOException && (e.HResult & 0xFFFF) is 32 or 33;   // sharing violation, lock violation

        internal const string InUseMessage = "That file is in use by another program on the PC. Close it there, then try again.";

        public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".m4a" or ".m4b" or ".aac" or ".alac" => "audio/mp4",
            ".flac" => "audio/flac",
            ".wav" => "audio/wav",
            ".aif" or ".aiff" or ".aifc" => "audio/aiff",
            ".ogg" or ".oga" => "audio/ogg",
            ".opus" => "audio/ogg",
            ".caf" => "audio/x-caf",
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".heic" => "image/heic",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".txt" or ".log" or ".md" or ".csv" or ".ini" or ".json" or ".xml" => "text/plain; charset=utf-8",
            ".htm" or ".html" => "text/html; charset=utf-8",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => "application/octet-stream",
        };

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        /// <summary>
        /// JSON smaller than this goes as it is: under a packet, compressing it saves nothing on the wire.
        /// </summary>
        internal const int CompressAbove = 1400;

        /// <summary>
        /// Brotli's quality for JSON made per request. Measured on a 10,000-entry listing (1.16 MB): quality 3 is
        /// 22 KB in 1.3 ms, 1 is 24 KB in 0.6 ms, 9 saves another 3 KB for 23 ms, and gzip at its fastest is 42 KB.
        /// </summary>
        internal const int JsonBrotliQuality = 3;

        /// <summary>
        /// Writes a JSON answer. Given the request, a body worth it is compressed the way the request allows
        /// (brotli, else gzip): a folder of thousands of files is a megabyte of names that crosses the phone's
        /// link as a few tens of kilobytes.
        /// </summary>
        private static async Task Json(NetworkStream s, int status, object value, bool head, CancellationToken token, Request? r = null)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            string coding = "";
            if (r != null && body.Length >= CompressAbove)
            {
                coding = WebAssets.PickEncoding(r.Headers.GetValueOrDefault("accept-encoding"));
                if (coding == "br") body = WebAssets.Brotli(body, JsonBrotliQuality);
                else if (coding == "gzip") body = WebAssets.Gzip(body, System.IO.Compression.CompressionLevel.Fastest);
            }
            var headText = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
                           (coding.Length > 0 ? $"Content-Encoding: {coding}\r\n" : "") + (r != null ? "Vary: Accept-Encoding\r\n" : "") +
                           "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n\r\n";
            await WriteAll(s, Encoding.ASCII.GetBytes(headText), head ? null : body, token);
        }

        /// <summary>A head and a body in one write when the body is small: one packet, not two, for every answer.</summary>
        private static async Task WriteAll(NetworkStream s, byte[] head, byte[]? body, CancellationToken token)
        {
            if (body == null || body.Length == 0) { await s.WriteAsync(head, token); return; }
            if (body.Length > 64 * 1024)
            {
                await s.WriteAsync(head, token);
                await s.WriteAsync(body, token);
                return;
            }
            var both = new byte[head.Length + body.Length];
            head.CopyTo(both, 0);
            body.CopyTo(both, head.Length);
            await s.WriteAsync(both, token);
        }

        /// <summary>
        /// The web app's own files. Each carries a tag (ETag), so a phone that has the file is told so in a few
        /// bytes (304) rather than sent it again; app.js and app.css asked for under the tag index.html names them
        /// by are kept by the phone for good, because that URL can never mean other bytes. Text goes compressed
        /// when the browser takes it, from copies compressed once.
        /// </summary>
        private static async Task Shell(NetworkStream s, Request r, WebAssets.Asset asset, bool head, CancellationToken token)
        {
            var coding = WebAssets.PickEncoding(r.Headers.GetValueOrDefault("accept-encoding"));
            byte[] body = coding == "br" && asset.Brotli != null ? asset.Brotli
                : coding == "gzip" && asset.Gzip != null ? asset.Gzip
                : asset.Bytes;
            if (ReferenceEquals(body, asset.Bytes)) coding = "";
            // A tag per coding: the same tag on different bytes would let a cache splice one into the other.
            string etag = coding.Length == 0 ? $"\"{asset.Tag}\"" : $"\"{asset.Tag}-{coding}\"";
            bool versioned = r.Query.TryGetValue("v", out var v) && v == asset.Tag;
            var common = $"Content-Type: {asset.Type}\r\nETag: {etag}\r\nVary: Accept-Encoding\r\n" +
                         (versioned ? "Cache-Control: max-age=31536000, immutable\r\n" : "Cache-Control: no-cache\r\n") +
                         "X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n" +
                         "Content-Security-Policy: default-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; " +
                         "connect-src 'self'; style-src 'self'; script-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'\r\n";
            if (TagMatches(r.Headers.GetValueOrDefault("if-none-match"), etag))
            {
                await Write(s, "HTTP/1.1 304 Not Modified\r\n" + common + "\r\n", token);
                return;
            }
            var headText = "HTTP/1.1 200 OK\r\n" + common + (coding.Length > 0 ? $"Content-Encoding: {coding}\r\n" : "") +
                           $"Content-Length: {body.Length}\r\n\r\n";
            await WriteAll(s, Encoding.ASCII.GetBytes(headText), head ? null : body, token);
        }

        /// <summary>Whether an If-None-Match header names <paramref name="etag"/> (weak or strong) or is "*".</summary>
        internal static bool TagMatches(string? ifNoneMatch, string etag)
        {
            if (string.IsNullOrWhiteSpace(ifNoneMatch)) return false;
            foreach (var part in ifNoneMatch.Split(','))
            {
                var t = part.Trim();
                if (t == "*") return true;
                if (t.StartsWith("W/", StringComparison.Ordinal)) t = t[2..];
                if (t == etag) return true;
            }
            return false;
        }

        /// <summary>Every error is <c>{ "error": "a sentence" }</c> with a real status.</summary>
        private static Task Error(NetworkStream s, int status, string message, bool head, CancellationToken token) =>
            Json(s, status, new { error = message }, head, token);

        private static string Reason(int status) => status switch
        {
            200 => "OK", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
            404 => "Not Found", 405 => "Method Not Allowed", 408 => "Request Timeout", 409 => "Conflict", 421 => "Misdirected Request",
            423 => "Locked", 429 => "Too Many Requests", 431 => "Request Header Fields Too Large", 503 => "Service Unavailable",
            _ => "Internal Server Error",
        };

        private static Task Write(NetworkStream s, string text, CancellationToken token) =>
            s.WriteAsync(Encoding.ASCII.GetBytes(text), token).AsTask();

        /// <summary>
        /// Reads request heads off a keep-alive connection, and the raw bytes after them for a body: whatever
        /// arrived with the head is in the buffer and has to be handed out before the socket is read again.
        /// </summary>
        private sealed class HeaderReader
        {
            private readonly NetworkStream _stream;
            private readonly byte[] _buffer = new byte[16 * 1024];
            private int _count;

            public HeaderReader(NetworkStream stream) => _stream = stream;

            /// <summary>Body bytes: the buffered ones first, then the socket. 0 when the connection closed.</summary>
            public async ValueTask<int> ReadRawAsync(Memory<byte> into, CancellationToken token)
            {
                if (into.Length == 0) return 0;
                if (_count > 0)
                {
                    int take = Math.Min(_count, into.Length);
                    _buffer.AsSpan(0, take).CopyTo(into.Span);
                    Buffer.BlockCopy(_buffer, take, _buffer, 0, _count - take);
                    _count -= take;
                    return take;
                }
                return await _stream.ReadAsync(into, token);
            }

            /// <summary>One CRLF-terminated line, for chunk sizes. Null when the connection closed.</summary>
            public async Task<string?> ReadLineAsync(CancellationToken token)
            {
                while (true)
                {
                    for (int i = 1; i < _count; i++)
                        if (_buffer[i - 1] == '\r' && _buffer[i] == '\n')
                        {
                            string line = Encoding.ASCII.GetString(_buffer, 0, i - 1);
                            Buffer.BlockCopy(_buffer, i + 1, _buffer, 0, _count - i - 1);
                            _count -= i + 1;
                            return line;
                        }
                    if (_count == _buffer.Length) return null;
                    int n = await _stream.ReadAsync(_buffer.AsMemory(_count), token);
                    if (n <= 0) return null;
                    _count += n;
                }
            }

            /// <summary>
            /// The next request head. Null when the connection closed, or sat idle with nothing for
            /// <paramref name="idle"/>; a head that started and has not finished within <paramref name="whole"/>
            /// is a 408, one too big a 431 and one that is not HTTP a 400, as a <see cref="HeadRefusedException"/>.
            /// </summary>
            public async Task<Request?> ReadAsync(TimeSpan whole, TimeSpan idle, CancellationToken token)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                TimeSpan? started = _count > 0 ? TimeSpan.Zero : null;
                while (true)
                {
                    int end = IndexOfHeadEnd();
                    if (end >= 0)
                    {
                        string head = Encoding.ASCII.GetString(_buffer, 0, end);
                        int consumed = end + 4;
                        Buffer.BlockCopy(_buffer, consumed, _buffer, 0, _count - consumed);
                        _count -= consumed;
                        return Parse(head) ?? throw new HeadRefusedException(400, "That request could not be understood.");
                    }
                    if (_count == _buffer.Length) throw new HeadRefusedException(431, "The request's headers are too big.");
                    var left = (started is { } at ? at + whole : idle) - clock.Elapsed;
                    if (left <= TimeSpan.Zero)
                    {
                        if (started == null) return null;
                        throw new HeadRefusedException(408, "The request's headers took too long to arrive.");
                    }
                    int n;
                    using (var timer = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        timer.CancelAfter(left);
                        try { n = await _stream.ReadAsync(_buffer.AsMemory(_count), timer.Token); }
                        catch (Exception e) when (e is OperationCanceledException or IOException &&
                                                  timer.IsCancellationRequested && !token.IsCancellationRequested)
                        {
                            if (started == null) return null;
                            throw new HeadRefusedException(408, "The request's headers took too long to arrive.");
                        }
                    }
                    if (n <= 0) return null;
                    started ??= clock.Elapsed;
                    _count += n;
                }
            }

            private int IndexOfHeadEnd()
            {
                for (int i = 3; i < _count; i++)
                    if (_buffer[i - 3] == '\r' && _buffer[i - 2] == '\n' && _buffer[i - 1] == '\r' && _buffer[i] == '\n') return i - 3;
                return -1;
            }

            private static Request? Parse(string head)
            {
                var lines = head.Split("\r\n");
                var first = lines[0].Split(' ');
                // METHOD target HTTP/1.x, nothing else: a request line that is not one is not a request.
                if (first.Length != 3 || first[0].Length == 0 || !first[0].All(char.IsAsciiLetter) ||
                    first[1].Length == 0 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
                string target = first[1];
                int q = target.IndexOf('?');
                string path = q < 0 ? target : target[..q];
                var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (q >= 0)
                {
                    foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        int eq = pair.IndexOf('=');
                        // No '+' to space: file names contain plus signs, and the web app encodes spaces as %20.
                        string k = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
                        string v = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
                        query[k] = v;
                    }
                }
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
                }
                return new Request(first[0].ToUpperInvariant(), Uri.UnescapeDataString(path), query, headers);
            }
        }

        /// <summary>
        /// A request's body as a read-only stream: <c>Content-Length</c> bytes, or chunked, or nothing. Sends
        /// <c>100 Continue</c> on the first read when the client asked for it, so a refusal before reading costs
        /// the web app nothing.
        /// </summary>
        private sealed class RequestBody : Stream
        {
            private readonly HeaderReader _reader;
            private readonly NetworkStream _socket;
            private readonly bool _chunked;
            private long _remaining;     // fixed length: what is left; chunked: what is left of this chunk
            private bool _chunkStarted;

            private RequestBody(HeaderReader reader, NetworkStream socket, long length, bool chunked, bool expects)
            {
                _reader = reader;
                _socket = socket;
                _chunked = chunked;
                _remaining = chunked ? 0 : length;
                Length = chunked ? -1 : length;
                ExpectsContinue = expects;
                Finished = !chunked && length == 0;
            }

            public static RequestBody For(Dictionary<string, string> headers, HeaderReader reader, NetworkStream socket)
            {
                bool expects = headers.TryGetValue("expect", out var e) && e.Equals("100-continue", StringComparison.OrdinalIgnoreCase);
                if (headers.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                    return new RequestBody(reader, socket, -1, true, expects);
                if (!headers.TryGetValue("content-length", out var cl)) return new RequestBody(reader, socket, 0, false, false);
                return long.TryParse(cl, out long n) && n >= 0
                    ? new RequestBody(reader, socket, n, false, expects)
                    : new RequestBody(reader, socket, 0, false, false) { Invalid = true };
            }

            public bool Invalid { get; private init; }
            public bool ExpectsContinue { get; }
            public bool Started { get; private set; }
            public bool Finished { get; private set; }
            public bool HasBody => _chunked || Length > 0;

            /// <summary>The declared length, or -1 when chunked.</summary>
            public override long Length { get; }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                if (Finished || buffer.Length == 0) return 0;
                if (!Started)
                {
                    Started = true;
                    if (ExpectsContinue) await ConnectServer.Write(_socket, "HTTP/1.1 100 Continue\r\n\r\n", token);
                }

                if (_chunked && _remaining == 0)
                {
                    // Each chunk after the first follows the CRLF that ended the one before.
                    if (_chunkStarted && await _reader.ReadLineAsync(token) == null) throw new IOException("the upload ended mid-chunk");
                    var line = await _reader.ReadLineAsync(token) ?? throw new IOException("the upload ended mid-chunk");
                    int semi = line.IndexOf(';');
                    if (!long.TryParse(semi < 0 ? line.Trim() : line[..semi].Trim(), System.Globalization.NumberStyles.HexNumber, null, out long size) || size < 0)
                        throw new ConnectException(400, "The upload's chunks could not be read.");
                    _chunkStarted = true;
                    if (size == 0)
                    {
                        // Trailers, then the blank line.
                        while (!string.IsNullOrEmpty(await _reader.ReadLineAsync(token))) { }
                        Finished = true;
                        return 0;
                    }
                    _remaining = size;
                }

                int want = (int)Math.Min(buffer.Length, _remaining);
                int got = await _reader.ReadRawAsync(buffer[..want], token);
                if (got <= 0) throw new IOException("the upload ended early");
                _remaining -= got;
                if (!_chunked && _remaining == 0) Finished = true;
                return got;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            /// <summary>
            /// Reads and discards the rest, if there is no more than <paramref name="most"/> of it and it keeps
            /// coming: no byte for <paramref name="stall"/> and it is given up on, or a trickled body after a 401
            /// held the connection for ever.
            /// </summary>
            public async Task<bool> DrainAsync(long most, TimeSpan stall, CancellationToken token)
            {
                if (!_chunked && _remaining > most) return false;
                var scratch = new byte[64 * 1024];
                var guarded = new StallGuard(this, stall);
                long drained = 0;
                try
                {
                    int got;
                    while ((got = await guarded.ReadAsync(scratch, token)) > 0)
                        if ((drained += got) > most) return false;
                    return true;
                }
                catch { return false; }
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>
        /// A request body that gives up when no byte has come for <c>after</c>: a <see cref="TimeoutException"/>
        /// instead of a read that waits until the dead socket is noticed, over a minute later, with a hidden
        /// partial file sitting in somebody's folder all that time. The same rule a resumable chunk has.
        /// </summary>
        internal sealed class StallGuard : Stream
        {
            private readonly Stream _inner;
            private readonly TimeSpan _after;

            public StallGuard(Stream inner, TimeSpan after) { _inner = inner; _after = after; }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
                stall.CancelAfter(_after);
                try { return await _inner.ReadAsync(buffer, stall.Token); }
                catch (Exception e) when (e is OperationCanceledException or IOException &&
                                          stall.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    throw new TimeoutException("the upload stopped arriving");
                }
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        public void Dispose()
        {
            _stop.Cancel();
            // A copy still running is stopped with the server: robocopy is stood down from Drive's reads first.
            foreach (var job in _jobs.Values) job.Cancel();
            lock (_gate)
            {
                try { _listener?.Stop(); } catch { }
                _listener = null;
            }
        }
    }
}
