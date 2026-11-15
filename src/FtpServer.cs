using System;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// A small FTP server over one folder, for one account. Passive mode only,
    /// UTF-8 names, and FTP over TLS (AUTH TLS) when it is given a certificate.
    /// Every connection has its own thread; nothing here touches the UI thread.
    /// </summary>
    internal sealed class FtpServer : IDisposable
    {
        private const int MaxConnections = 64;
        private const int MaxLineBytes = 8 * 1024;
        private const int IdleTimeoutMs = 5 * 60 * 1000;    // a signed-in person thinking
        private const int IoTimeoutMs = 60 * 1000;          // anything else without a byte
        private const int MaxWrongTriesPerConnection = 3;
        private const int CopyBufferBytes = 64 * 1024;

        private readonly string _root;
        private readonly int _port;
        private readonly string _user;
        private readonly string _password;
        private readonly bool _readOnly;
        private readonly X509Certificate2? _tls;

        private readonly ConcurrentDictionary<Socket, byte> _sockets = new();
        private readonly ConcurrentDictionary<Socket, byte> _passiveListeners = new();
        private TcpListener? _listener;
        private int _connections;
        private volatile bool _disposed;

        /// <summary>The port really listened on, which matters when 0 was asked for.</summary>
        internal int Port { get; private set; }

        /// <summary>How long a passive port waits for its one connection.</summary>
        internal int PassiveWaitMs { get; set; } = 30_000;

        internal SignInThrottle Throttle { get; } = new();

        /// <param name="passivePorts">
        /// The ports transfers come in on, or null for any free port. Fixed
        /// ports are what let Tailscale forward them and a firewall name them.
        /// </param>
        /// <param name="requireTls">
        /// Nobody signs in until the connection is inside TLS, and files only
        /// move inside it. Needs <paramref name="tls"/>.
        /// </param>
        public FtpServer(string folder, int port, string user, string password, bool readOnly, X509Certificate2? tls = null,
            int[]? passivePorts = null, bool requireTls = false)
        {
            _root = folder ?? "";
            _port = port;
            _user = user ?? "";
            _password = password ?? "";
            _readOnly = readOnly;
            _tls = tls;
            _passivePorts = passivePorts is { Length: > 0 } ? (int[])passivePorts.Clone() : null;
            _requireTls = requireTls;
        }

        private readonly int[]? _passivePorts;
        private readonly bool _requireTls;

        /// <summary>Connections one address may have at once. This PC itself (and so Tailscale, which arrives from it) is not held to it.</summary>
        internal const int MaxPerAddress = 16;

        /// <summary>How long a connection has to sign in, and how many commands it may send first.</summary>
        internal int SignInWithinMs { get; set; } = 60_000;
        private const int CommandsBeforeSignIn = 20;

        private readonly ConcurrentDictionary<IPAddress, int> _perAddress = new();

        public void Start()
        {
            if (_disposed) throw new InvalidOperationException("The FTP server has been stopped.");
            if (_listener != null) throw new InvalidOperationException("The FTP server is already running.");
            if (_port < 0 || _port > 65535) throw new InvalidOperationException($"{_port} is not a port number. Pick one between 1 and 65535.");
            if (!Directory.Exists(_root)) throw new InvalidOperationException($"The folder {_root} is not there.");
            if (_tls != null && !_tls.HasPrivateKey) throw new InvalidOperationException("The certificate for secure FTP has no private key.");
            if (_requireTls && _tls == null) throw new InvalidOperationException("Encrypted connections only was asked for, but the FTP server has no certificate.");

            TcpListener listener;
            try
            {
                try
                {
                    listener = Listen(IPAddress.IPv6Any);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported
                    or SocketError.ProtocolNotSupported or SocketError.AddressNotAvailable)
                {
                    listener = Listen(IPAddress.Any);       // a PC with IPv6 turned off
                }
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException(ex.SocketErrorCode switch
                {
                    SocketError.AddressAlreadyInUse => $"Port {_port} is already in use by another program.",
                    SocketError.AccessDenied => $"Windows would not let the FTP server use port {_port}. Another program may already be using it.",
                    _ => $"The FTP server could not start on port {_port}. {ex.Message}",
                }, ex);
            }

            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            new Thread(() => AcceptLoop(listener)) { IsBackground = true, Name = "FTP listener" }.Start();
        }

        private TcpListener Listen(IPAddress address)
        {
            var listener = new TcpListener(address, _port);
            try
            {
                if (address.AddressFamily == AddressFamily.InterNetworkV6) listener.Server.DualMode = true;
                // Without this another program could bind the same port and take our callers.
                listener.ExclusiveAddressUse = true;
                listener.Start(64);
                return listener;
            }
            catch
            {
                try { listener.Stop(); } catch { }
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _listener?.Stop(); } catch { }
            foreach (Socket passive in _passiveListeners.Keys)
            {
                try { passive.Close(0); } catch { }
            }
            // Closing the sockets is what ends the threads: each is blocked in a
            // read or a write on one of them.
            foreach (Socket socket in _sockets.Keys)
            {
                try { socket.Close(0); } catch { }
            }
            _passiveListeners.Clear();
            _sockets.Clear();
        }

        private void AcceptLoop(TcpListener listener)
        {
            while (!_disposed)
            {
                Socket socket;
                try { socket = listener.AcceptSocket(); }
                catch (Exception ex)
                {
                    if (_disposed) return;
                    // A caller who hung up in the queue is routine; anything else is the listener in trouble.
                    if (ex is not SocketException) Diag.Note(ex);
                    Thread.Sleep(50);                       // a caller who hung up while still in the queue
                    continue;
                }

                try
                {
                    socket.NoDelay = true;
                    socket.SendTimeout = IoTimeoutMs;
                    socket.ReceiveTimeout = IoTimeoutMs;
                    _sockets[socket] = 0;
                    if (_disposed) { Drop(socket); return; }

                    // Counted here, before a thread is spent: a caller over
                    // either limit gets one line and no thread. They used to
                    // be counted on a thread of their own, and uncounted
                    // again while that thread was still alive.
                    IPAddress from = ServedFolder.Plain(((IPEndPoint)socket.RemoteEndPoint!).Address);
                    bool tooMany = Interlocked.Increment(ref _connections) > MaxConnections;
                    bool tooManyFromOne = _perAddress.AddOrUpdate(from, 1, (_, n) => n + 1) > MaxPerAddress && !IPAddress.IsLoopback(from);
                    if (tooMany || tooManyFromOne)
                    {
                        try
                        {
                            socket.SendTimeout = 2000;
                            socket.Send(Encoding.ASCII.GetBytes("421 Too many connections at the moment. Try again shortly.\r\n"));
                        }
                        catch { }
                        Uncount(from);
                        Drop(socket);
                        continue;
                    }

                    var session = new Session(socket);
                    try
                    {
                        new Thread(() => Serve(session)) { IsBackground = true, Name = "FTP connection" }.Start();
                    }
                    catch
                    {
                        Uncount(from);
                        throw;
                    }
                }
                catch
                {
                    Drop(socket);
                }
            }
        }

        /// <summary>One connection fewer, overall and for its address.</summary>
        private void Uncount(IPAddress from)
        {
            Interlocked.Decrement(ref _connections);
            // Removed at zero, so the table is only ever the addresses connected now.
            while (true)
            {
                if (!_perAddress.TryGetValue(from, out int n)) return;
                if (n <= 1)
                {
                    if (_perAddress.TryRemove(new KeyValuePair<IPAddress, int>(from, n))) return;
                }
                else if (_perAddress.TryUpdate(from, n - 1, n)) return;
            }
        }

        private void Drop(Socket socket)
        {
            _sockets.TryRemove(socket, out _);
            try { socket.Close(); } catch { }
        }

        // ---- one connection ----------------------------------------------

        private sealed class Session
        {
            public readonly Socket Control;
            public Stream Stream;
            public readonly IPAddress Remote;
            public readonly IPAddress Local;

            public bool Secure;             // the control connection is inside TLS
            public bool ProtectData;        // PROT P: data connections are too
            public string? UserName;
            public bool SignedIn;
            public int WrongTries;

            /// <summary>When it connected (or signed out), and what it has sent since without signing in.</summary>
            public long Began = Environment.TickCount64;
            public int Unsigned;

            public List<string> Folder = new();     // where CWD has taken them, below the root
            public long RestartAt;
            public string? RenameFrom;

            public Socket? Passive;
            public Timer? PassiveTimer;
            public long PassiveDeadline;

            private readonly byte[] _buffer = new byte[4096];
            private int _at, _filled;

            public Session(Socket control)
            {
                Control = control;
                Stream = new NetworkStream(control, ownsSocket: false);
                Remote = ServedFolder.Plain(((IPEndPoint)control.RemoteEndPoint!).Address);
                Local = ((IPEndPoint)control.LocalEndPoint!).Address;
                if (Local.IsIPv4MappedToIPv6) Local = Local.MapToIPv4();
            }

            /// <summary>
            /// Anything typed ahead of the TLS handshake was sent in the clear and
            /// could have been put there by someone in the middle, so it is dropped.
            /// </summary>
            public void ForgetBuffered() { _at = 0; _filled = 0; }

            /// <summary>One command line, or null when the other end has hung up.</summary>
            public string? ReadLine(out bool tooLong)
            {
                tooLong = false;
                var line = new List<byte>(128);
                while (true)
                {
                    if (_at == _filled)
                    {
                        _at = 0;
                        _filled = Stream.Read(_buffer, 0, _buffer.Length);
                        if (_filled <= 0) { _filled = 0; return null; }
                    }

                    byte b = _buffer[_at++];
                    if (b == (byte)'\n') break;
                    if (line.Count >= MaxLineBytes) { tooLong = true; return null; }
                    line.Add(b);
                }

                if (line.Count > 0 && line[line.Count - 1] == (byte)'\r') line.RemoveAt(line.Count - 1);
                byte[] bytes = line.ToArray();
                try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
                catch (ArgumentException) { return Encoding.Latin1.GetString(bytes); }     // an old client that never asked for UTF-8
            }
        }

        /// <summary>Thrown to end a connection after its last reply has been sent.</summary>
        private sealed class HangUp : Exception { }

        private void Serve(Session s)
        {
            try
            {
                if (Throttle.IsBlocked(s.Remote))
                {
                    Reply(s, 421, "Too many wrong passwords from your address. Try again in a few minutes.");
                    return;
                }

                Reply(s, 220, "Explorer Native file sharing is ready.");

                while (!_disposed)
                {
                    // Signing in has a deadline of its own. Each read used to
                    // have a minute to itself for ever, so sixty-four
                    // connections sending NOOP now and then, with no
                    // password, held every place the server has.
                    if (!s.SignedIn)
                    {
                        long left = SignInWithinMs - (Environment.TickCount64 - s.Began);
                        if (left <= 0 || s.Unsigned > CommandsBeforeSignIn)
                        {
                            Reply(s, 421, "Sign in sooner next time.");
                            return;
                        }
                        s.Control.ReceiveTimeout = (int)Math.Clamp(left, 1, IoTimeoutMs);
                    }

                    string? line = s.ReadLine(out bool tooLong);
                    if (tooLong) { Reply(s, 500, "That command is too long."); return; }
                    if (line == null) return;
                    if (line.Length == 0) continue;
                    if (!s.SignedIn) s.Unsigned++;

                    int space = line.IndexOf(' ');
                    string verb = (space < 0 ? line : line.Substring(0, space)).ToUpperInvariant();
                    string argument = space < 0 ? "" : line.Substring(space + 1);

                    try
                    {
                        Run(s, verb, argument);
                    }
                    catch (HangUp) { return; }
                    catch (Exception ex) when (ex is not IOException && ex is not SocketException
                        && ex is not ObjectDisposedException && ex is not AuthenticationException)
                    {
                        // A surprise in one command is that command's problem, not the server's.
                        ReplyProblem(s, ex);
                    }
                }
            }
            catch
            {
                // The other end went away, or the server is stopping.
            }
            finally
            {
                ClosePassive(s);
                HangUpGently(s.Control);
                try { s.Stream.Dispose(); } catch { }
                Drop(s.Control);
                // Last: the place is given up only once the thread is done with it.
                Uncount(s.Remote);
            }
        }

        /// <summary>
        /// Closing a socket that still has unread bytes makes Windows send a
        /// reset, and a reset can wipe out our last reply before the client has
        /// read it. So: say we are done, give the client a moment to finish
        /// talking, then close.
        /// </summary>
        private void HangUpGently(Socket socket)
        {
            if (_disposed) return;
            try
            {
                socket.Shutdown(SocketShutdown.Send);
                // Two seconds in all, not half a second a byte: a caller
                // sending one byte now and then kept this thread and its
                // socket for a day and a half, off the books.
                var clock = Stopwatch.StartNew();
                byte[] scrap = new byte[4096];
                int total = 0;
                while (total < 64 * 1024 && clock.ElapsedMilliseconds < 2000)
                {
                    socket.ReceiveTimeout = (int)Math.Max(1, 2000 - clock.ElapsedMilliseconds);
                    int read = socket.Receive(scrap);
                    if (read <= 0) break;
                    total += read;
                }
            }
            catch
            {
                // Already gone, which is all that was wanted.
            }
        }

        private void Reply(Session s, int code, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(code.ToString(CultureInfo.InvariantCulture) + " " + text + "\r\n");
            s.Stream.Write(bytes, 0, bytes.Length);
            s.Stream.Flush();
        }

        private void ReplyLines(Session s, int code, string first, IEnumerable<string> middle, string last)
        {
            var text = new StringBuilder();
            text.Append(code).Append('-').Append(first).Append("\r\n");
            foreach (string line in middle) text.Append(' ').Append(line).Append("\r\n");
            text.Append(code).Append(' ').Append(last).Append("\r\n");
            byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
            s.Stream.Write(bytes, 0, bytes.Length);
            s.Stream.Flush();
        }

        private void ReplyProblem(Session s, Exception error)
        {
            switch (ServedFolder.Classify(error))
            {
                case ServedProblem.NotFound: Reply(s, 550, "That file or folder is not there."); break;
                case ServedProblem.Denied: Reply(s, 550, "Windows would not allow that."); break;
                case ServedProblem.InUse: Reply(s, 450, "That file is in use by another program. Try again in a moment."); break;
                case ServedProblem.DiskFull: Reply(s, 552, "The disk is full."); break;
                default: Reply(s, 451, "That did not work because of a problem on this PC."); break;
            }
        }

        private void Run(Session s, string verb, string argument)
        {
            // REST and RNFR only mean something to the next real command. Asking
            // for a passive port or a transfer type in between does not use them up.
            long restartAt = s.RestartAt;
            string? renameFrom = s.RenameFrom;
            if (verb is not ("PASV" or "EPSV" or "TYPE" or "MODE" or "STRU" or "NOOP" or "ALLO" or "PBSZ" or "PROT" or "OPTS" or "FEAT" or "SYST"))
            {
                s.RestartAt = 0;
                s.RenameFrom = null;
            }

            switch (verb)
            {
                case "QUIT":
                    Reply(s, 221, "Goodbye.");
                    throw new HangUp();
                case "NOOP":
                    Reply(s, 200, "Still here.");
                    return;
                case "FEAT":
                    Features(s);
                    return;
                case "OPTS":
                    if (argument.StartsWith("UTF8", StringComparison.OrdinalIgnoreCase)) Reply(s, 200, "Names are always sent as UTF-8.");
                    else if (argument.StartsWith("MLST", StringComparison.OrdinalIgnoreCase)) Reply(s, 200, "MLST OPTS type;size;modify;");
                    else Reply(s, 501, "That option is not one this server has.");
                    return;
                case "SYST":
                    Reply(s, 215, "UNIX Type: L8");
                    return;
                case "AUTH":
                    StartTls(s, argument);
                    return;
                case "PBSZ":
                    if (!s.Secure) Reply(s, 503, "Send AUTH TLS first.");
                    else Reply(s, 200, "PBSZ=0");
                    return;
                case "PROT":
                    Protect(s, argument);
                    return;
                case "USER":
                    if (s.SignedIn)
                    {
                        // Signed out: the sign-in clock and the short timeout start again.
                        s.SignedIn = false;
                        s.Began = Environment.TickCount64;
                        s.Unsigned = 0;
                        s.Control.ReceiveTimeout = IoTimeoutMs;
                    }
                    if (_requireTls && !s.Secure)
                    {
                        Reply(s, 530, "This server only takes encrypted connections. Turn on explicit FTP over TLS and try again.");
                        return;
                    }
                    s.UserName = argument;
                    Reply(s, 331, "Now send the password.");
                    return;
                case "PASS":
                    if (_requireTls && !s.Secure)
                    {
                        Reply(s, 530, "This server only takes encrypted connections. Turn on explicit FTP over TLS and try again.");
                        return;
                    }
                    SignIn(s, argument);
                    return;
            }

            if (!s.SignedIn)
            {
                Reply(s, 530, "Sign in first.");
                return;
            }

            switch (verb)
            {
                case "PWD":
                case "XPWD":
                    Reply(s, 257, Quoted(VirtualPath(s.Folder)) + " is the current folder.");
                    break;
                case "CWD":
                case "XCWD":
                    ChangeFolder(s, argument);
                    break;
                case "CDUP":
                case "XCUP":
                    ChangeFolder(s, "..");
                    break;
                case "TYPE":
                    // Text mode would mean rewriting line endings, which only ever
                    // damages files. Both are accepted and both are sent as they are.
                    if (argument.StartsWith("A", StringComparison.OrdinalIgnoreCase)
                        || argument.StartsWith("I", StringComparison.OrdinalIgnoreCase)
                        || argument.StartsWith("L", StringComparison.OrdinalIgnoreCase))
                        Reply(s, 200, "Files are sent exactly as they are.");
                    else Reply(s, 504, "Only types A and I are known here.");
                    break;
                case "MODE":
                    if (argument.Equals("S", StringComparison.OrdinalIgnoreCase)) Reply(s, 200, "Stream mode.");
                    else Reply(s, 504, "Only stream mode is known here.");
                    break;
                case "STRU":
                    if (argument.Equals("F", StringComparison.OrdinalIgnoreCase)) Reply(s, 200, "File structure.");
                    else Reply(s, 504, "Only file structure is known here.");
                    break;
                case "ALLO":
                    Reply(s, 202, "No need to reserve space.");
                    break;
                case "PASV":
                    Passive(s, extended: false, argument);
                    break;
                case "EPSV":
                    Passive(s, extended: true, argument);
                    break;
                case "PORT":
                case "EPRT":
                    Reply(s, 502, "This server only does passive mode. Switch your FTP program to passive mode.");
                    break;
                case "LIST":
                    SendListing(s, argument, ListingKind.Unix);
                    break;
                case "NLST":
                    SendListing(s, argument, ListingKind.Names);
                    break;
                case "MLSD":
                    SendListing(s, argument, ListingKind.Facts);
                    break;
                case "MLST":
                    FactsForOne(s, argument);
                    break;
                case "SIZE":
                    SizeOf(s, argument);
                    break;
                case "MDTM":
                    ModifiedOf(s, argument);
                    break;
                case "REST":
                    if (long.TryParse(argument.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long at))
                    {
                        s.RestartAt = at;
                        Reply(s, 350, "The next transfer starts at byte " + at.ToString(CultureInfo.InvariantCulture) + ".");
                    }
                    else Reply(s, 501, "REST needs a number of bytes.");
                    break;
                case "RETR":
                    Send(s, argument, restartAt);
                    break;
                case "STOR":
                    Receive(s, argument, restartAt, append: false);
                    break;
                case "APPE":
                    Receive(s, argument, 0, append: true);
                    break;
                case "DELE":
                    DeleteFile(s, argument);
                    break;
                case "RMD":
                case "XRMD":
                    RemoveFolder(s, argument);
                    break;
                case "MKD":
                case "XMKD":
                    MakeFolder(s, argument);
                    break;
                case "RNFR":
                    RenameFrom(s, argument);
                    break;
                case "RNTO":
                    RenameTo(s, renameFrom, argument);
                    break;
                case "ABOR":
                    // Transfers run to their end on this thread, so by the time
                    // this is read there is never one left to stop.
                    Reply(s, 226, "Nothing is being transferred.");
                    break;
                default:
                    Reply(s, 502, "This server does not know the command " + Printable(verb) + ".");
                    break;
            }
        }

        // ---- signing in and TLS -------------------------------------------

        private void Features(Session s)
        {
            var features = new List<string> { "UTF8", "EPSV", "PASV", "MLST type*;size*;modify*;", "REST STREAM", "SIZE", "MDTM" };
            if (_tls != null) features.AddRange(new[] { "AUTH TLS", "PBSZ", "PROT" });
            ReplyLines(s, 211, "This server can do:", features, "End");
        }

        private void SignIn(Session s, string password)
        {
            if (s.UserName == null)
            {
                Reply(s, 503, "Send the user name first.");
                return;
            }
            if (Throttle.IsBlocked(s.Remote))
            {
                Reply(s, 421, "Too many wrong passwords from your address. Try again in a few minutes.");
                throw new HangUp();
            }

            // Both halves are always compared, so the time taken does not say
            // whether the name alone was right.
            bool nameRight = ServedFolder.SameSecret(s.UserName, _user);
            bool passwordRight = ServedFolder.SameSecret(password, _password);
            s.UserName = null;
            // Passwords being guessed from many addresses at once: every
            // answer waits, right or wrong alike.
            Throttle.Slow();

            if (nameRight & passwordRight)
            {
                Throttle.Succeeded(s.Remote);
                s.SignedIn = true;
                s.Folder = new List<string>();
                s.Control.ReceiveTimeout = IdleTimeoutMs;
                Reply(s, 230, "Signed in.");
                return;
            }

            bool shutOut = Throttle.Failed(s.Remote);
            s.WrongTries++;
            if (shutOut)
            {
                Reply(s, 421, "Too many wrong passwords from your address. Try again in a few minutes.");
                throw new HangUp();
            }
            if (s.WrongTries >= MaxWrongTriesPerConnection)
            {
                Reply(s, 421, "The user name or password is wrong. Connect again to have another go.");
                throw new HangUp();
            }
            Reply(s, 530, "The user name or password is wrong.");
        }

        private SslServerAuthenticationOptions TlsOptions(bool forData = false) => new()
        {
            ServerCertificate = _tls,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            // Upload programs write and close without reading. With resumption on,
            // TLS 1.3 sends them a session ticket they never read, and closing
            // with unread bytes becomes a reset that can cut the end off the upload.
            AllowTlsResume = !forData,
        };

        private void StartTls(Session s, string argument)
        {
            if (_tls == null)
            {
                Reply(s, 502, "This server is not set up for secure FTP.");
                return;
            }
            string kind = argument.Trim().ToUpperInvariant();
            if (kind != "TLS" && kind != "TLS-C" && kind != "SSL")
            {
                Reply(s, 504, "Only AUTH TLS is known here.");
                return;
            }
            if (s.Secure)
            {
                Reply(s, 503, "This connection is already secure.");
                return;
            }

            Reply(s, 234, "Starting TLS.");
            s.ForgetBuffered();
            var tls = new SslStream(s.Stream, leaveInnerStreamOpen: false);
            tls.AuthenticateAsServer(TlsOptions());        // a failed handshake ends the connection
            s.Stream = tls;
            s.Secure = true;
            // Whatever was said before the connection was private does not count.
            s.SignedIn = false;
            s.UserName = null;
            s.Control.ReceiveTimeout = IoTimeoutMs;
        }

        private void Protect(Session s, string argument)
        {
            if (!s.Secure)
            {
                Reply(s, 503, "Send AUTH TLS first.");
                return;
            }
            switch (argument.Trim().ToUpperInvariant())
            {
                case "P":
                    s.ProtectData = true;
                    Reply(s, 200, "Files and listings will be sent inside TLS.");
                    break;
                case "C":
                    if (_requireTls)
                    {
                        Reply(s, 534, "This server only sends files inside TLS.");
                        break;
                    }
                    s.ProtectData = false;
                    Reply(s, 200, "Files and listings will be sent without TLS.");
                    break;
                default:
                    Reply(s, 504, "Only PROT P and PROT C are known here.");
                    break;
            }
        }

        // ---- paths ----------------------------------------------------------

        /// <summary>
        /// Works out where a path the client sent points: the folders below
        /// the root, and the place on this PC. ".." is walked here, where it
        /// can only ever go up as far as the shared folder, and what is left
        /// goes through the shared name checks. False means refuse.
        /// </summary>
        private bool Locate(Session s, string argument, out List<string> parts, out string local)
        {
            parts = argument.StartsWith('/') ? new List<string>() : new List<string>(s.Folder);
            local = "";

            foreach (string piece in argument.Split('/'))
            {
                if (piece.Length == 0 || piece == ".") continue;
                if (piece == "..")
                {
                    if (parts.Count == 0) return false;     // above the shared folder
                    parts.RemoveAt(parts.Count - 1);
                    continue;
                }
                parts.Add(piece);
            }

            string? found = ServedFolder.Resolve(_root, string.Join('/', parts));
            if (found == null) return false;
            local = found;
            return true;
        }

        private bool LocateOrRefuse(Session s, string argument, out List<string> parts, out string local)
        {
            if (Locate(s, argument, out parts, out local)) return true;
            Reply(s, 550, "That name is not allowed here.");
            return false;
        }

        private bool RefuseWrite(Session s)
        {
            if (!_readOnly) return false;
            Reply(s, 550, "This folder is shared for reading only.");
            return true;
        }

        private static string VirtualPath(List<string> parts) => "/" + string.Join('/', parts);

        private static string Quoted(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";

        private static string Printable(string text)
        {
            var clean = new StringBuilder();
            foreach (char c in text)
            {
                if (clean.Length >= 16) break;
                clean.Append(c >= 32 && c < 127 ? c : '?');
            }
            return clean.ToString();
        }

        private void ChangeFolder(Session s, string argument)
        {
            if (!Locate(s, argument, out List<string> parts, out string local))
            {
                Reply(s, 550, "There is no folder by that name.");
                return;
            }
            if (!Directory.Exists(local))
            {
                Reply(s, 550, "There is no folder by that name.");
                return;
            }
            s.Folder = parts;
            Reply(s, 250, "Now in " + VirtualPath(parts) + ".");
        }

        // ---- data connections -------------------------------------------

        private void ClosePassive(Session s)
        {
            Socket? listener = s.Passive;
            s.Passive = null;
            try { s.PassiveTimer?.Dispose(); } catch { }
            s.PassiveTimer = null;
            if (listener == null) return;
            _passiveListeners.TryRemove(listener, out _);
            try { listener.Close(0); } catch { }
        }

        private static readonly int[] AnyPort = { 0 };

        private void Passive(Session s, bool extended, string argument)
        {
            if (extended && argument.Trim().Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                Reply(s, 200, "Passive mode it is.");
                return;
            }
            if (!extended && s.Local.AddressFamily != AddressFamily.InterNetwork)
            {
                Reply(s, 522, "PASV cannot carry an IPv6 address. Use EPSV.");
                return;
            }

            ClosePassive(s);

            // The port is opened on the address this client already reached us
            // on: that is the one address we know it can get to.
            Socket? listener = null;
            foreach (int wanted in _passivePorts ?? AnyPort)
            {
                var trial = new Socket(s.Local.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    trial.ExclusiveAddressUse = true;
                    trial.Bind(new IPEndPoint(s.Local, wanted));
                    trial.Listen(1);
                    listener = trial;
                    break;
                }
                catch
                {
                    // In use by another transfer (or another program): the next one.
                    try { trial.Close(0); } catch { }
                }
            }
            if (listener == null)
            {
                Reply(s, 425, _passivePorts == null
                    ? "No port could be opened for the transfer."
                    : "Every transfer port is busy at the moment. Try again shortly.");
                return;
            }

            _passiveListeners[listener] = 0;
            if (_disposed) { try { listener.Close(0); } catch { } throw new HangUp(); }

            int wait = Math.Max(1, PassiveWaitMs);
            s.Passive = listener;
            s.PassiveDeadline = Environment.TickCount64 + wait;
            // The port must not stay open for whoever finds it if the client never comes.
            s.PassiveTimer = new Timer(state =>
            {
                _passiveListeners.TryRemove(listener, out _);
                try { listener.Close(0); } catch { }
            }, null, wait, Timeout.Infinite);

            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            if (extended)
            {
                Reply(s, 229, "Entering Extended Passive Mode (|||" + port.ToString(CultureInfo.InvariantCulture) + "|)");
            }
            else
            {
                byte[] a = s.Local.GetAddressBytes();
                Reply(s, 227, $"Entering Passive Mode ({a[0]},{a[1]},{a[2]},{a[3]},{port >> 8},{port & 255}).");
            }
        }

        /// <summary>
        /// Says the transfer is starting, then takes the one data connection.
        /// Null (with the client already told) when none came in time.
        /// </summary>
        private Stream? OpenData(Session s)
        {
            Socket? listener = s.Passive;
            if (listener == null)
            {
                Reply(s, 425, "Send PASV or EPSV before asking for a transfer.");
                return null;
            }
            if (_requireTls && !s.ProtectData)
            {
                ClosePassive(s);
                Reply(s, 521, "This server only sends files inside TLS. Send PROT P first.");
                return null;
            }

            Reply(s, 150, "Here it comes.");

            Socket? data = null;
            try
            {
                while (!_disposed && Environment.TickCount64 < s.PassiveDeadline)
                {
                    if (!listener.Poll(100_000, SelectMode.SelectRead)) continue;

                    Socket caller = listener.Accept();
                    // Anyone can find an open port. Only the PC that asked for it
                    // may use it, or a stranger could collect somebody's file.
                    if (ServedFolder.Plain(((IPEndPoint)caller.RemoteEndPoint!).Address).Equals(s.Remote))
                    {
                        data = caller;
                        break;
                    }
                    try { caller.Close(0); } catch { }
                }
            }
            catch
            {
                // The timer closed the port, or the server is stopping.
            }
            ClosePassive(s);

            if (data == null)
            {
                Reply(s, 425, "No data connection arrived in time.");
                return null;
            }

            _sockets[data] = 0;
            if (_disposed) { Drop(data); throw new HangUp(); }

            try
            {
                data.SendTimeout = IoTimeoutMs;
                data.ReceiveTimeout = IoTimeoutMs;
                Stream stream = new DataStream(this, data);
                if (!s.ProtectData) return stream;

                var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                try
                {
                    tls.AuthenticateAsServer(TlsOptions(forData: true));
                    return tls;
                }
                catch
                {
                    tls.Dispose();
                    throw;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is AuthenticationException || ex is SocketException || ex is ObjectDisposedException)
            {
                Drop(data);
                Reply(s, 425, "The data connection could not be made secure.");
                return null;
            }
        }

        /// <summary>A data socket that takes itself off the server's list when it closes.</summary>
        private sealed class DataStream : NetworkStream
        {
            private readonly FtpServer _server;
            private readonly Socket _socket;

            public DataStream(FtpServer server, Socket socket) : base(socket, ownsSocket: true)
            {
                _server = server;
                _socket = socket;
            }

            protected override void Dispose(bool disposing)
            {
                _server._sockets.TryRemove(_socket, out _);
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// Ends a data connection so the other end sees a clean finish: over
        /// TLS that is a closing message, not just the line going dead.
        /// </summary>
        private static bool CloseData(Stream data)
        {
            bool clean = true;
            try
            {
                data.Flush();
                if (data is SslStream tls) clean = tls.ShutdownAsync().Wait(5000);
            }
            catch { clean = false; }
            try { data.Dispose(); } catch { clean = false; }
            return clean;
        }

        // ---- listings -----------------------------------------------------

        private enum ListingKind { Unix, Names, Facts }

        private void SendListing(Session s, string argument, ListingKind kind)
        {
            // "LIST -la" is what many programs send; the switches mean nothing here.
            if (kind != ListingKind.Facts)
            {
                while (argument.StartsWith('-'))
                {
                    int space = argument.IndexOf(' ');
                    argument = space < 0 ? "" : argument.Substring(space + 1);
                }
            }

            if (!LocateOrRefuse(s, argument, out _, out string local)) return;

            IEnumerable<ServedEntry> rows;
            try
            {
                if (Directory.Exists(local))
                {
                    rows = ServedFolder.List(_root, local);
                }
                else if (kind != ListingKind.Facts && ServedFolder.Describe(local) is ServedEntry one)
                {
                    rows = new[] { one };
                }
                else
                {
                    Reply(s, 550, "There is no folder by that name.");
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ReplyProblem(s, ex);
                return;
            }

            Stream? data = OpenData(s);
            if (data == null) return;

            bool sent = true;
            try
            {
              try
              {
                using (var writer = new StreamWriter(data, new UTF8Encoding(false), 16 * 1024, leaveOpen: true))
                {
                    writer.NewLine = "\r\n";
                    DateTime now = DateTime.Now;
                    foreach (ServedEntry row in rows)
                    {
                        switch (kind)
                        {
                            case ListingKind.Unix: writer.WriteLine(UnixLine(row, now)); break;
                            case ListingKind.Names: writer.WriteLine(row.Name); break;
                            default: writer.WriteLine(Facts(row) + " " + row.Name); break;
                        }
                    }
                }
            }
              catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ObjectDisposedException)
              {
                sent = false;
              }

              if (!CloseData(data)) sent = false;
            }
            finally
            {
                // Whatever else went wrong, the connection does not outlive the command.
                try { data.Dispose(); } catch { }
            }
            if (sent) Reply(s, 226, "That is the whole list.");
            else Reply(s, 426, "The connection closed before the whole list was sent.");
        }

        private string UnixLine(ServedEntry row, DateTime now)
        {
            string rights = (row.IsFolder ? "d" : "-") + (_readOnly
                ? (row.IsFolder ? "r-xr-xr-x" : "r--r--r--")
                : (row.IsFolder ? "rwxr-xr-x" : "rw-r--r--"));

            // The form ls uses: the time for anything recent, the year otherwise.
            DateTime when = row.ModifiedUtc.ToLocalTime();
            string date = when > now.AddDays(-180) && when <= now.AddDays(1)
                ? when.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture)
                : when.ToString("MMM dd  yyyy", CultureInfo.InvariantCulture);

            return rights + " 1 owner group " + row.Length.ToString(CultureInfo.InvariantCulture).PadLeft(12) + " " + date + " " + row.Name;
        }

        private static string Stamp(DateTime utc) => utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        private static string Facts(ServedEntry row) => row.IsFolder
            ? "type=dir;modify=" + Stamp(row.ModifiedUtc) + ";"
            : "type=file;size=" + row.Length.ToString(CultureInfo.InvariantCulture) + ";modify=" + Stamp(row.ModifiedUtc) + ";";

        private void FactsForOne(Session s, string argument)
        {
            if (!LocateOrRefuse(s, argument, out List<string> parts, out string local)) return;
            if (ServedFolder.Describe(local) is not ServedEntry row)
            {
                Reply(s, 550, "That file or folder is not there.");
                return;
            }
            ReplyLines(s, 250, "Here are the details:", new[] { Facts(row) + " " + VirtualPath(parts) }, "End");
        }

        private void SizeOf(Session s, string argument)
        {
            if (!LocateOrRefuse(s, argument, out _, out string local)) return;
            if (ServedFolder.Describe(local) is ServedEntry row && !row.IsFolder)
                Reply(s, 213, row.Length.ToString(CultureInfo.InvariantCulture));
            else Reply(s, 550, "There is no file by that name.");
        }

        private void ModifiedOf(Session s, string argument)
        {
            if (!LocateOrRefuse(s, argument, out _, out string local)) return;
            if (ServedFolder.Describe(local) is ServedEntry row) Reply(s, 213, Stamp(row.ModifiedUtc));
            else Reply(s, 550, "That file or folder is not there.");
        }

        // ---- files --------------------------------------------------------

        private void Send(Session s, string argument, long startAt)
        {
            if (!LocateOrRefuse(s, argument, out _, out string local)) return;
            if (Directory.Exists(local))
            {
                Reply(s, 550, "That is a folder, not a file.");
                return;
            }

            FileStream file;
            try
            {
                // Shared as widely as Windows allows, so a file open in another
                // program can still be fetched.
                file = new FileStream(local, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, CopyBufferBytes, FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ReplyProblem(s, ex);
                return;
            }

            using (file)
            {
                if (startAt > file.Length)
                {
                    Reply(s, 550, "The file is shorter than the place you asked to start from.");
                    return;
                }
                file.Position = startAt;

                Stream? data = OpenData(s);
                if (data == null) return;

                bool sent = true;
                Exception? diskTrouble = null;
                try
                {
                    byte[] buffer = new byte[CopyBufferBytes];
                    while (true)
                    {
                        int read;
                        try { read = file.Read(buffer, 0, buffer.Length); }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { diskTrouble = ex; break; }
                        if (read <= 0) break;

                        try { data.Write(buffer, 0, read); }
                        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { sent = false; break; }
                    }

                    // Disk trouble: cut the line rather than end it cleanly, or the
                    // client would take the part it has for the whole file.
                    if (diskTrouble == null && !CloseData(data)) sent = false;
                }
                finally
                {
                    // Whatever else went wrong, the connection does not outlive the command.
                    try { data.Dispose(); } catch { }
                }

                if (diskTrouble != null)
                {
                    Reply(s, 451, "The file could not be read to the end.");
                    return;
                }
                if (sent) Reply(s, 226, "The whole file was sent.");
                else Reply(s, 426, "The connection closed before the whole file was sent.");
            }
        }

        private void Receive(Session s, string argument, long startAt, bool append)
        {
            if (RefuseWrite(s)) return;
            if (!LocateOrRefuse(s, argument, out List<string> parts, out string local)) return;
            if (parts.Count == 0 || Directory.Exists(local))
            {
                Reply(s, 550, "That is a folder, not a file.");
                return;
            }
            if (!Directory.Exists(Path.GetDirectoryName(local)))
            {
                Reply(s, 550, "The folder to put that in is not there.");
                return;
            }

            // A fresh upload lands beside the target under another name and takes
            // its place only when all of it is here. Carrying on a broken upload
            // (REST) or adding to a file (APPE) has to write to the file itself.
            // Before any file is made: with no port asked for there is no
            // transfer, and APPE used to leave an empty file behind saying so.
            if (s.Passive == null)
            {
                Reply(s, 425, "Send PASV or EPSV before asking for a transfer.");
                return;
            }

            string? temp = null;
            string? madeForAppend = null;
            FileStream file;
            try
            {
                if (append)
                {
                    bool wasThere = File.Exists(local);
                    file = new FileStream(local, FileMode.Append, FileAccess.Write, FileShare.Read, CopyBufferBytes);
                    if (!wasThere) madeForAppend = local;
                }
                else if (startAt > 0)
                {
                    // Carrying on needs something to carry on from: OpenOrCreate
                    // made an empty file and then refused it for being too short.
                    file = new FileStream(local, FileMode.Open, FileAccess.Write, FileShare.Read, CopyBufferBytes);
                    if (startAt > file.Length)
                    {
                        file.Dispose();
                        Reply(s, 550, "The file is shorter than the place you asked to carry on from.");
                        return;
                    }
                    file.Position = startAt;
                }
                else
                {
                    temp = ServedFolder.TempNameFor(local);
                    file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ReplyProblem(s, ex);
                return;
            }

            bool cut = false;
            bool finished = false;
            long arrived = 0;
            Exception? diskTrouble = null;
            Stream? data = null;
            try
            {
                data = OpenData(s);
                if (data == null) { cut = true; return; }

                byte[] buffer = new byte[CopyBufferBytes];
                while (true)
                {
                    int read;
                    try { read = data.Read(buffer, 0, buffer.Length); }
                    catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { cut = true; break; }
                    if (read <= 0) break;

                    try { file.Write(buffer, 0, read); arrived += read; }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { diskTrouble = ex; break; }
                }

                if (!cut && diskTrouble == null)
                {
                    try
                    {
                        if (startAt > 0) file.SetLength(file.Position);
                        file.Flush();
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { diskTrouble = ex; }
                }
                finished = !cut && diskTrouble == null;
            }
            finally
            {
                try { file.Dispose(); } catch (IOException ex) { diskTrouble ??= ex; finished = false; }
                if (data != null) { try { data.Dispose(); } catch { } }
                // Anything but a finished upload: the control connection
                // dropping first threw straight past here and left the
                // half-made file for good, hidden from every listing.
                if (!finished) ServedFolder.DeleteQuietly(temp);
                if (!finished && arrived == 0) ServedFolder.DeleteQuietly(madeForAppend);
            }

            if (diskTrouble != null)
            {
                ReplyProblem(s, diskTrouble);
                return;
            }
            if (cut)
            {
                Reply(s, 426, temp != null
                    ? "The connection broke before the whole file arrived, so nothing was changed."
                    : "The connection broke before the whole file arrived.");
                return;
            }

            if (temp != null)
            {
                try
                {
                    ServedFolder.MoveIntoPlace(temp, local);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    ServedFolder.DeleteQuietly(temp);
                    ReplyProblem(s, ex);
                    return;
                }
            }
            Reply(s, 226, "The whole file arrived.");
        }

        private void DeleteFile(Session s, string argument)
        {
            if (RefuseWrite(s)) return;
            if (!LocateOrRefuse(s, argument, out _, out string local)) return;
            if (!File.Exists(local))
            {
                Reply(s, 550, "There is no file by that name.");
                return;
            }
            try
            {
                File.Delete(local);
                Reply(s, 250, "Deleted.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ReplyProblem(s, ex);
            }
        }

        private void RemoveFolder(Session s, string argument)
        {
            if (RefuseWrite(s)) return;
            if (!LocateOrRefuse(s, argument, out List<string> parts, out string local)) return;
            if (parts.Count == 0)
            {
                Reply(s, 550, "The shared folder itself cannot be removed.");
                return;
            }
            if (!Directory.Exists(local))
            {
                Reply(s, 550, "There is no folder by that name.");
                return;
            }
            try
            {
                // As FTP has always had it: only an empty folder goes.
                Directory.Delete(local, recursive: false);
                Reply(s, 250, "Folder removed.");
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 145)
            {
                Reply(s, 550, "That folder is not empty.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ReplyProblem(s, ex);
            }
        }

        private void MakeFolder(Session s, string argument)
        {
            if (RefuseWrite(s)) return;
            if (!LocateOrRefuse(s, argument, out List<string> parts, out string local)) return;
            if (Directory.Exists(local) || File.Exists(local))
            {
                Reply(s, 550, "Something by that name is already there.");
                return;
            }
            if (!Directory.Exists(Path.GetDirectoryName(local)))
            {
                Reply(s, 550, "The folder to make that in is not there.");
                return;
            }
            try
            {
                Directory.CreateDirectory(local);
                Reply(s, 257, Quoted(VirtualPath(parts)) + " was made.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ReplyProblem(s, ex);
            }
        }

        private void RenameFrom(Session s, string argument)
        {
            if (RefuseWrite(s)) return;
            if (!LocateOrRefuse(s, argument, out List<string> parts, out string local)) return;
            if (parts.Count == 0)
            {
                Reply(s, 550, "The shared folder itself cannot be renamed.");
                return;
            }
            if (!File.Exists(local) && !Directory.Exists(local))
            {
                Reply(s, 550, "That file or folder is not there.");
                return;
            }
            s.RenameFrom = local;
            Reply(s, 350, "Now send the new name.");
        }

        private void RenameTo(Session s, string? from, string argument)
        {
            if (RefuseWrite(s)) return;
            if (from == null)
            {
                Reply(s, 503, "Send RNFR with the old name first.");
                return;
            }
            if (!LocateOrRefuse(s, argument, out List<string> parts, out string to)) return;
            if (parts.Count == 0)
            {
                Reply(s, 550, "That name is not allowed here.");
                return;
            }

            try
            {
                bool sameButForCase = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
                if (Directory.Exists(from))
                {
                    if (!sameButForCase && (Directory.Exists(to) || File.Exists(to)))
                    {
                        Reply(s, 550, "Something by that name is already there.");
                        return;
                    }
                    if (to.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        Reply(s, 550, "A folder cannot be moved inside itself.");
                        return;
                    }
                    if (!string.Equals(from, to, StringComparison.Ordinal)) Directory.Move(from, to);
                }
                else
                {
                    if (Directory.Exists(to))
                    {
                        Reply(s, 550, "A folder by that name is already there.");
                        return;
                    }
                    // Programs that upload under a spare name and rename at the end
                    // count on the rename replacing the old file.
                    File.Move(from, to, overwrite: true);
                }
                Reply(s, 250, "Renamed.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ReplyProblem(s, ex);
            }
        }
    }
}
