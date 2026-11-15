using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// A small SFTP server over one folder, for one account: just enough of
    /// SSH-2 to carry the "sftp" subsystem, written on what .NET ships with.
    /// One key exchange (ECDH over P-256), one kind of host key (ECDSA P-256),
    /// AES in counter mode, HMAC-SHA2, password sign-in. No shell, no
    /// commands, no forwarding. Every connection has its own thread.
    /// </summary>
    internal sealed class SftpServer : IDisposable
    {
        private const int MaxConnections = 64;
        private const int MaxPacketBytes = 35_000;          // the size RFC 4253 asks everyone to cope with
        private const int IdleTimeoutMs = 5 * 60 * 1000;
        private const int IoTimeoutMs = 60 * 1000;
        private const int SignInGraceMs = 2 * 60 * 1000;
        private const int MaxWrongTriesPerConnection = 3;
        private const int MaxSignInRequests = 20;
        private const int MaxChannels = 4;
        private const int MaxHandles = 64;
        private const int WindowBytes = 2 * 1024 * 1024;
        private const int MaxDataPerPacket = 32 * 1024;
        private const int MaxSftpMessageBytes = 300_000;
        private const int MaxReadBytes = 64 * 1024;
        private const int PendingOutputLimit = 1024 * 1024;

        private const string ServerVersion = "SSH-2.0-ExplorerNative";
        private const string KexName = "ecdh-sha2-nistp256";
        private const string HostKeyName = "ecdsa-sha2-nistp256";
        private static readonly string[] Ciphers = { "aes256-ctr", "aes128-ctr" };
        private static readonly string[] Macs =
        {
            "hmac-sha2-256-etm@openssh.com", "hmac-sha2-512-etm@openssh.com", "hmac-sha2-256", "hmac-sha2-512",
        };

        private readonly string _root;
        private readonly int _port;
        private readonly string _user;
        private readonly string _password;
        private readonly bool _readOnly;
        private readonly byte[] _hostKeyPkcs8;

        private ECDsa? _hostKey;
        private byte[] _hostKeyBlob = Array.Empty<byte>();
        private readonly ConcurrentDictionary<Socket, byte> _sockets = new();
        private TcpListener? _listener;
        private int _connections;

        /// <summary>Connections one address may have at once. This PC itself (and so Tailscale, which arrives from it) is not held to it.</summary>
        internal const int MaxPerAddress = 16;

        /// <summary>
        /// Everything before sign-in, start to finish: the version line, the
        /// key exchange and the password. Each read used to have a minute to
        /// itself, so a connection dripping a byte now and then, or sending
        /// "ignore" messages, held its place for hours without a password.
        /// </summary>
        internal int BeforeSignInMs { get; set; } = 60_000;

        private readonly ConcurrentDictionary<IPAddress, int> _perAddress = new();

        /// <summary>One connection fewer, overall and for its address.</summary>
        private void Uncount(IPAddress from)
        {
            Interlocked.Decrement(ref _connections);
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
        private volatile bool _disposed;

        /// <summary>The port really listened on, which matters when 0 was asked for.</summary>
        internal int Port { get; private set; }

        internal SignInThrottle Throttle { get; } = new();

        public SftpServer(string folder, int port, string user, string password, bool readOnly, byte[] hostKeyPkcs8)
        {
            _root = folder ?? "";
            _port = port;
            _user = user ?? "";
            _password = password ?? "";
            _readOnly = readOnly;
            _hostKeyPkcs8 = hostKeyPkcs8 ?? Array.Empty<byte>();
        }

        /// <summary>A new host key: an ECDSA P-256 private key as PKCS#8, to be kept and reused.</summary>
        public static byte[] NewHostKey()
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return key.ExportPkcs8PrivateKey();
        }

        /// <summary>The key's fingerprint as OpenSSH prints it, "SHA256:" and base64 with no padding.</summary>
        public static string Fingerprint(byte[] hostKeyPkcs8)
        {
            using ECDsa key = LoadHostKey(hostKeyPkcs8);
            return "SHA256:" + Convert.ToBase64String(SHA256.HashData(PublicBlob(key))).TrimEnd('=');
        }

        private static ECDsa LoadHostKey(byte[] pkcs8)
        {
            ECDsa key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(pkcs8, out _);
                if (key.KeySize != 256) throw new CryptographicException("Not a P-256 key.");
                return key;
            }
            catch
            {
                key.Dispose();
                throw;
            }
        }

        /// <summary>The public half in SSH's own form, which is also what the fingerprint is taken over.</summary>
        private static byte[] PublicBlob(ECDsa key)
        {
            ECParameters p = key.ExportParameters(false);
            return new Wire().Str(HostKeyName).Str("nistp256").Str(PointBytes(p.Q)).ToArray();
        }

        private static byte[] PointBytes(ECPoint q)
        {
            byte[] point = new byte[65];
            point[0] = 4;                                   // "uncompressed": X then Y follow
            q.X!.CopyTo(point, 1 + 32 - q.X.Length);
            q.Y!.CopyTo(point, 33 + 32 - q.Y.Length);
            return point;
        }

        public void Start()
        {
            if (_disposed) throw new InvalidOperationException("The SFTP server has been stopped.");
            if (_listener != null) throw new InvalidOperationException("The SFTP server is already running.");
            if (_port < 0 || _port > 65535) throw new InvalidOperationException($"{_port} is not a port number. Pick one between 1 and 65535.");
            if (!Directory.Exists(_root)) throw new InvalidOperationException($"The folder {_root} is not there.");

            try
            {
                _hostKey = LoadHostKey(_hostKeyPkcs8);
                _hostKeyBlob = PublicBlob(_hostKey);
            }
            catch (Exception ex) when (ex is CryptographicException || ex is ArgumentException)
            {
                throw new InvalidOperationException("The SFTP server's own key could not be read.", ex);
            }

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
                    SocketError.AccessDenied => $"Windows would not let the SFTP server use port {_port}. Another program may already be using it.",
                    _ => $"The SFTP server could not start on port {_port}. {ex.Message}",
                }, ex);
            }

            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            new Thread(() => AcceptLoop(listener)) { IsBackground = true, Name = "SFTP listener" }.Start();
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
            // Closing the sockets is what ends the threads: each is blocked in a
            // read or a write on one of them.
            foreach (Socket socket in _sockets.Keys)
            {
                try { socket.Close(0); } catch { }
            }
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
                    Thread.Sleep(50);
                    continue;
                }

                try
                {
                    socket.NoDelay = true;
                    socket.SendTimeout = IoTimeoutMs;
                    socket.ReceiveTimeout = IoTimeoutMs;
                    _sockets[socket] = 0;
                    if (_disposed) { Drop(socket); return; }

                    // Counted here, before a thread is spent. The limit used to
                    // be looked at on a thread of the connection's own, after
                    // its first line had arrived: 1,500 silent connections were
                    // 1,500 threads against a limit of 64.
                    IPAddress from = ServedFolder.Plain(((IPEndPoint)socket.RemoteEndPoint!).Address);
                    bool tooMany = Interlocked.Increment(ref _connections) > MaxConnections;
                    bool tooManyFromOne = _perAddress.AddOrUpdate(from, 1, (_, n) => n + 1) > MaxPerAddress && !IPAddress.IsLoopback(from);
                    if (tooMany || tooManyFromOne)
                    {
                        Uncount(from);
                        Drop(socket);
                        continue;
                    }

                    var link = new Link(this, socket);
                    try
                    {
                        new Thread(link.Serve) { IsBackground = true, Name = "SFTP connection" }.Start();
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

        private void Drop(Socket socket)
        {
            _sockets.TryRemove(socket, out _);
            try { socket.Close(); } catch { }
        }

        // ---- bytes on the wire ---------------------------------------------

        /// <summary>The other end sent something that is not SSH or not SFTP. The connection ends.</summary>
        private sealed class Malformed : Exception
        {
            public Malformed(string what) : base(what) { }
        }

        /// <summary>The connection is over and anything owed has been said.</summary>
        private sealed class HangUp : Exception { }

        /// <summary>Builds a message in SSH's encoding: big-endian numbers, length-prefixed strings.</summary>
        private sealed class Wire
        {
            private byte[] _bytes = new byte[128];
            private int _length;

            public int Length => _length;

            private Span<byte> Room(int count)
            {
                if (_length + count > _bytes.Length) Array.Resize(ref _bytes, Math.Max(_bytes.Length * 2, _length + count));
                Span<byte> room = _bytes.AsSpan(_length, count);
                _length += count;
                return room;
            }

            public Wire U8(byte value) { Room(1)[0] = value; return this; }
            public Wire Bool(bool value) => U8(value ? (byte)1 : (byte)0);
            public Wire U32(uint value) { BinaryPrimitives.WriteUInt32BigEndian(Room(4), value); return this; }
            public Wire U64(ulong value) { BinaryPrimitives.WriteUInt64BigEndian(Room(8), value); return this; }
            public Wire Raw(ReadOnlySpan<byte> bytes) { bytes.CopyTo(Room(bytes.Length)); return this; }
            public Wire Str(ReadOnlySpan<byte> bytes) { U32((uint)bytes.Length); return Raw(bytes); }
            public Wire Str(string text) => Str(Encoding.UTF8.GetBytes(text));

            /// <summary>A whole number from its big-endian bytes, in SSH's signed form.</summary>
            public Wire MpInt(ReadOnlySpan<byte> magnitude)
            {
                while (magnitude.Length > 0 && magnitude[0] == 0) magnitude = magnitude.Slice(1);
                bool pad = magnitude.Length > 0 && (magnitude[0] & 0x80) != 0;     // or it would read as negative
                U32((uint)(magnitude.Length + (pad ? 1 : 0)));
                if (pad) U8(0);
                return Raw(magnitude);
            }

            public byte[] ToArray() => _bytes.AsSpan(0, _length).ToArray();
        }

        /// <summary>Reads a message back out. Anything that runs past the end is malformed, never a crash.</summary>
        private sealed class Reader
        {
            private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);
            private readonly byte[] _bytes;
            private int _at;
            private readonly int _end;

            public Reader(byte[] bytes, int start, int count)
            {
                _bytes = bytes;
                _at = start;
                _end = start + count;
            }

            public Reader(byte[] bytes) : this(bytes, 0, bytes.Length) { }

            public int Left => _end - _at;

            private int Take(int count)
            {
                if (count < 0 || count > _end - _at) throw new Malformed("A message was shorter than it said.");
                int at = _at;
                _at += count;
                return at;
            }

            public byte U8() => _bytes[Take(1)];
            public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(Take(4), 4));
            public ulong U64() => BinaryPrimitives.ReadUInt64BigEndian(_bytes.AsSpan(Take(8), 8));
            public void Skip(int count) => Take(count);

            public ReadOnlySpan<byte> Bytes()
            {
                uint length = U32();
                if (length > int.MaxValue) throw new Malformed("A message was shorter than it said.");
                return _bytes.AsSpan(Take((int)length), (int)length);
            }

            public string Text()
            {
                try { return StrictUtf8.GetString(Bytes()); }
                catch (ArgumentException) { throw new Malformed("Text that is not UTF-8."); }
            }

            /// <summary>Text that may be wrong without the connection being wrong: a file name. Null when it is not UTF-8.</summary>
            public string? Name()
            {
                ReadOnlySpan<byte> bytes = Bytes();
                try { return StrictUtf8.GetString(bytes); }
                catch (ArgumentException) { return null; }
            }
        }

        /// <summary>
        /// AES in counter mode, made from plain AES blocks: each block of
        /// output is the data XORed with the encrypted counter, and the counter
        /// goes up by one. SSH only ever hands it whole blocks.
        /// </summary>
        private sealed class CounterMode : IDisposable
        {
            private readonly Aes _aes;
            private readonly byte[] _counter;
            private byte[] _counters = new byte[1024];
            private byte[] _stream = new byte[1024];

            public CounterMode(byte[] key, byte[] counter)
            {
                _aes = Aes.Create();
                _aes.Key = key;
                _counter = counter;
            }

            public void Apply(Span<byte> data)
            {
                if (data.Length % 16 != 0) throw new Malformed("A packet that is not whole cipher blocks.");
                if (_counters.Length < data.Length)
                {
                    _counters = new byte[data.Length];
                    _stream = new byte[data.Length];
                }

                for (int at = 0; at < data.Length; at += 16)
                {
                    _counter.CopyTo(_counters, at);
                    for (int i = 15; i >= 0; i--)
                    {
                        if (++_counter[i] != 0) break;
                    }
                }

                _aes.EncryptEcb(_counters.AsSpan(0, data.Length), _stream.AsSpan(0, data.Length), PaddingMode.None);
                for (int i = 0; i < data.Length; i++) data[i] ^= _stream[i];
            }

            public void Dispose() => _aes.Dispose();
        }

        /// <summary>One direction's keys: the cipher, and the check that nothing was altered.</summary>
        private sealed class Keys : IDisposable
        {
            public readonly CounterMode Cipher;
            public readonly IncrementalHash Mac;
            public readonly int MacBytes;
            /// <summary>Check the bytes as sent, before decrypting. The length then travels in the clear.</summary>
            public readonly bool MacFirst;

            public Keys(string cipher, string mac, Func<char, int, byte[]> derive, char ivLetter, char keyLetter, char macLetter)
            {
                Cipher = new CounterMode(derive(keyLetter, cipher == "aes256-ctr" ? 32 : 16), derive(ivLetter, 16));
                bool big = mac.StartsWith("hmac-sha2-512", StringComparison.Ordinal);
                MacBytes = big ? 64 : 32;
                MacFirst = mac.EndsWith("-etm@openssh.com", StringComparison.Ordinal);
                Mac = IncrementalHash.CreateHMAC(big ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256, derive(macLetter, MacBytes));
            }

            public byte[] Sign(uint sequence, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
            {
                Span<byte> number = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(number, sequence);
                Mac.AppendData(number);
                Mac.AppendData(first);
                Mac.AppendData(second);
                return Mac.GetHashAndReset();
            }

            public void Dispose()
            {
                Cipher.Dispose();
                Mac.Dispose();
            }
        }

        // ---- one connection ------------------------------------------------

        private sealed class Link
        {
            private readonly SftpServer _server;
            private readonly Socket _socket;
            private readonly IPAddress _remote;

            private readonly byte[] _received = new byte[16 * 1024];
            private int _receivedAt, _receivedEnd;

            private Keys? _in, _out;
            private uint _inSequence, _outSequence, _lastInSequence;
            private string _clientVersion = "";
            private byte[]? _sessionId;
            private bool _strict;               // both ends know the "strict key exchange" fix for the Terrapin attack

            private readonly Dictionary<uint, Channel> _channels = new();
            private uint _nextChannel;

            public Link(SftpServer server, Socket socket)
            {
                _server = server;
                _socket = socket;
                _remote = ServedFolder.Plain(((IPEndPoint)socket.RemoteEndPoint!).Address);
            }

            /// <summary>When everything before sign-in must be done by (tick count), 0 once signed in.</summary>
            private long _deadline;

            public void Serve()
            {
                try
                {
                    _deadline = Environment.TickCount64 + _server.BeforeSignInMs;

                    SendRaw(Encoding.ASCII.GetBytes(ServerVersion + "\r\n"));
                    _clientVersion = ReadVersion();

                    if (_server.Throttle.IsBlocked(_remote)) Disconnect(11, "Too many wrong passwords from your address. Try again in a few minutes.");

                    byte[] ours = OurKexInit();
                    Send(ours);
                    byte[] theirs = ReadPacket();
                    if (theirs[0] != 20) throw new Malformed("The first message was not a key exchange.");
                    ExchangeKeys(theirs, ours, initial: true);

                    SignIn();
                    _deadline = 0;
                    _socket.ReceiveTimeout = IdleTimeoutMs;

                    while (!_server._disposed)
                    {
                        Handle(Next());
                        foreach (Channel channel in new List<Channel>(_channels.Values)) Pump(channel);
                    }
                }
                catch
                {
                    // Hung up, timed out, sent nonsense, or the server is stopping.
                    // Whatever it was, it is this connection's alone.
                }
                finally
                {
                    foreach (Channel channel in _channels.Values) channel.CloseHandles();
                    _channels.Clear();
                    HangUpGently();
                    _in?.Dispose();
                    _out?.Dispose();
                    _server.Drop(_socket);
                    // Last: the place is given up only once the thread is done with it.
                    _server.Uncount(_remote);
                }
            }

            /// <summary>
            /// Closing with unread bytes waiting makes Windows send a reset, which
            /// can wipe out our last message before it is read. So: say we are
            /// done, let the other end finish, then close.
            /// </summary>
            private void HangUpGently()
            {
                if (_server._disposed) return;
                try
                {
                    _socket.Shutdown(SocketShutdown.Send);
                    // Two seconds in all, not half a second a byte: a caller
                    // sending a byte now and then kept this thread for a day.
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    byte[] scrap = new byte[4096];
                    int total = 0;
                    while (total < 64 * 1024 && clock.ElapsedMilliseconds < 2000)
                    {
                        _socket.ReceiveTimeout = (int)Math.Max(1, 2000 - clock.ElapsedMilliseconds);
                        int read = _socket.Receive(scrap);
                        if (read <= 0) break;
                        total += read;
                    }
                }
                catch { }
            }

            // ---- packets -----------------------------------------------------

            private void SendRaw(ReadOnlySpan<byte> bytes)
            {
                while (bytes.Length > 0)
                {
                    int sent = _socket.Send(bytes);
                    if (sent <= 0) throw new IOException("The connection closed.");
                    bytes = bytes.Slice(sent);
                }
            }

            private void ReadExact(Span<byte> into)
            {
                while (into.Length > 0)
                {
                    if (_receivedAt == _receivedEnd)
                    {
                        if (_deadline != 0)
                        {
                            long left = _deadline - Environment.TickCount64;
                            if (left <= 0) throw new HangUp();
                            _socket.ReceiveTimeout = (int)Math.Min(left, IoTimeoutMs);
                        }
                        _receivedAt = 0;
                        _receivedEnd = _socket.Receive(_received);
                        if (_receivedEnd <= 0) { _receivedEnd = 0; throw new HangUp(); }
                    }
                    int take = Math.Min(into.Length, _receivedEnd - _receivedAt);
                    _received.AsSpan(_receivedAt, take).CopyTo(into);
                    _receivedAt += take;
                    into = into.Slice(take);
                }
            }

            private string ReadVersion()
            {
                // Only a server may say anything before its version. A client's
                // first line is its version, or it is not an SSH client.
                Span<byte> one = stackalloc byte[1];
                var line = new StringBuilder();
                while (true)
                {
                    ReadExact(one);
                    if (one[0] == (byte)'\n') break;
                    if (line.Length >= 255) throw new Malformed("A version line that never ends.");
                    line.Append((char)one[0]);
                }
                string text = line.ToString().TrimEnd('\r');
                if (text.StartsWith("SSH-2.0-", StringComparison.Ordinal) || text.StartsWith("SSH-1.99-", StringComparison.Ordinal)) return text;
                throw new Malformed("Not an SSH-2 client.");
            }

            private byte[] ReadPacket()
            {
                Span<byte> head = stackalloc byte[16];
                byte[] packet;
                int length;

                if (_in == null)
                {
                    ReadExact(head.Slice(0, 4));
                    length = CheckedLength(head, 8, lengthCounts: true);
                    packet = new byte[length];
                    ReadExact(packet);
                }
                else if (_in.MacFirst)
                {
                    ReadExact(head.Slice(0, 4));
                    length = CheckedLength(head, 16, lengthCounts: false);
                    packet = new byte[length];
                    ReadExact(packet);
                    byte[] sent = new byte[_in.MacBytes];
                    ReadExact(sent);
                    if (!CryptographicOperations.FixedTimeEquals(sent, _in.Sign(_inSequence, head.Slice(0, 4), packet)))
                        throw new Malformed("A packet failed its integrity check.");
                    _in.Cipher.Apply(packet);
                }
                else
                {
                    ReadExact(head);
                    _in.Cipher.Apply(head);
                    length = CheckedLength(head, 16, lengthCounts: true);
                    packet = new byte[length];
                    head.Slice(4).CopyTo(packet);
                    Span<byte> rest = packet.AsSpan(12);
                    ReadExact(rest);
                    _in.Cipher.Apply(rest);
                    byte[] sent = new byte[_in.MacBytes];
                    ReadExact(sent);
                    if (!CryptographicOperations.FixedTimeEquals(sent, _in.Sign(_inSequence, head.Slice(0, 4), packet)))
                        throw new Malformed("A packet failed its integrity check.");
                }

                _lastInSequence = _inSequence;
                _inSequence++;

                int padding = packet[0];
                int payload = length - 1 - padding;
                if (padding < 4 || payload < 1) throw new Malformed("A packet with impossible padding.");
                return packet.AsSpan(1, payload).ToArray();
            }

            private static int CheckedLength(ReadOnlySpan<byte> head, int block, bool lengthCounts)
            {
                uint length = BinaryPrimitives.ReadUInt32BigEndian(head);
                if (length < 5 || length > MaxPacketBytes) throw new Malformed("A packet of an impossible size.");
                if ((length + (lengthCounts ? 4u : 0u)) % block != 0) throw new Malformed("A packet that is not whole blocks.");
                return (int)length;
            }

            private void Send(Wire message) => Send(message.ToArray());

            private void Send(ReadOnlySpan<byte> payload)
            {
                int block = _out == null ? 8 : 16;
                bool macFirst = _out != null && _out.MacFirst;
                int counted = payload.Length + 1 + (macFirst ? 0 : 4);
                int padding = block - counted % block;
                if (padding < 4) padding += block;

                int length = 1 + payload.Length + padding;
                byte[] packet = new byte[4 + length + (_out?.MacBytes ?? 0)];
                BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)length);
                packet[4] = (byte)padding;
                payload.CopyTo(packet.AsSpan(5));

                if (_out != null)
                {
                    RandomNumberGenerator.Fill(packet.AsSpan(5 + payload.Length, padding));
                    if (macFirst)
                    {
                        _out.Cipher.Apply(packet.AsSpan(4, length));
                        _out.Sign(_outSequence, packet.AsSpan(0, 4), packet.AsSpan(4, length)).CopyTo(packet, 4 + length);
                    }
                    else
                    {
                        byte[] mac = _out.Sign(_outSequence, packet.AsSpan(0, 4), packet.AsSpan(4, length));
                        _out.Cipher.Apply(packet.AsSpan(0, 4 + length));
                        mac.CopyTo(packet, 4 + length);
                    }
                }

                _outSequence++;
                SendRaw(packet);
            }

            /// <summary>Says why, then ends the connection.</summary>
            private void Disconnect(uint reason, string sentence)
            {
                try { Send(new Wire().U8(1).U32(reason).Str(sentence).Str("")); } catch { }
                throw new HangUp();
            }

            /// <summary>
            /// The next message that is somebody's business. Messages every
            /// SSH end must swallow are swallowed here, and a client asking for
            /// fresh keys gets them here, so nothing above has to know.
            /// </summary>
            private byte[] Next()
            {
                while (true)
                {
                    byte[] packet = ReadPacket();
                    switch (packet[0])
                    {
                        case 1: throw new HangUp();         // disconnect
                        case 2:                             // ignore
                        case 3:                             // unimplemented
                        case 4:                             // debug
                            continue;
                        case 20:
                            // Fresh keys are for somebody who has signed in:
                            // before that it is a way to make this PC do
                            // public-key sums for nothing, over and over.
                            if (_deadline != 0) throw new Malformed("A second key exchange before signing in.");
                            byte[] ours = OurKexInit();
                            Send(ours);
                            ExchangeKeys(packet, ours, initial: false);
                            continue;
                        default:
                            return packet;
                    }
                }
            }

            private void Unimplemented() => Send(new Wire().U8(3).U32(_lastInSequence));

            // ---- key exchange -----------------------------------------------

            private static byte[] OurKexInit()
            {
                string ciphers = string.Join(',', Ciphers);
                string macs = string.Join(',', Macs);
                var message = new Wire().U8(20).Raw(RandomNumberGenerator.GetBytes(16));
                message.Str(KexName + ",kex-strict-s-v00@openssh.com").Str(HostKeyName);
                message.Str(ciphers).Str(ciphers).Str(macs).Str(macs).Str("none").Str("none").Str("").Str("");
                return message.Bool(false).U32(0).ToArray();
            }

            private static string? Pick(string[] theirs, params string[] ours)
            {
                // The client's order of preference decides, as the standard says.
                foreach (string name in theirs)
                {
                    if (Array.IndexOf(ours, name) >= 0) return name;
                }
                return null;
            }

            private void ExchangeKeys(byte[] theirInit, byte[] ourInit, bool initial)
            {
                var init = new Reader(theirInit);
                init.Skip(17);
                var lists = new string[10][];
                for (int i = 0; i < 10; i++) lists[i] = init.Text().Split(',');
                bool guessFollows = init.U8() != 0;

                if (initial) _strict = Array.IndexOf(lists[0], "kex-strict-c-v00@openssh.com") >= 0;

                string? kex = Pick(lists[0], KexName);
                string? hostKey = Pick(lists[1], HostKeyName);
                string? cipherIn = Pick(lists[2], Ciphers), cipherOut = Pick(lists[3], Ciphers);
                string? macIn = Pick(lists[4], Macs), macOut = Pick(lists[5], Macs);
                if (kex == null || hostKey == null || cipherIn == null || cipherOut == null || macIn == null || macOut == null
                    || Pick(lists[6], "none") == null || Pick(lists[7], "none") == null)
                {
                    Disconnect(3, "This server and your SFTP program have no way of encrypting in common.");
                    return;
                }

                // A client may send its first exchange message on a guess. A wrong guess is thrown away.
                if (guessFollows && (lists[0][0] != kex || lists[1][0] != hostKey)) ReadPacket();

                byte[] start = ReadKexPacket(initial);
                if (start[0] != 30) throw new Malformed("Expected the client's half of the key exchange.");
                byte[] theirPoint = new Reader(start, 1, start.Length - 1).Bytes().ToArray();
                if (theirPoint.Length != 65 || theirPoint[0] != 4) throw new Malformed("Not a P-256 point.");

                byte[] secret, ourPoint;
                try
                {
                    using ECDiffieHellman mine = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                    // Importing checks the point really is on the curve.
                    using ECDiffieHellman theirs = ECDiffieHellman.Create(new ECParameters
                    {
                        Curve = ECCurve.NamedCurves.nistP256,
                        Q = new ECPoint { X = theirPoint.AsSpan(1, 32).ToArray(), Y = theirPoint.AsSpan(33, 32).ToArray() },
                    });
                    secret = mine.DeriveRawSecretAgreement(theirs.PublicKey);
                    ourPoint = PointBytes(mine.ExportParameters(false).Q);
                }
                catch (CryptographicException)
                {
                    throw new Malformed("The client's key exchange value was not usable.");
                }

                // Both ends hash everything said so far. Our signature over that
                // hash is what proves to the client that it reached this PC.
                byte[] hash = SHA256.HashData(new Wire()
                    .Str(Encoding.Latin1.GetBytes(_clientVersion)).Str(ServerVersion).Str(theirInit).Str(ourInit)
                    .Str(_server._hostKeyBlob).Str(theirPoint).Str(ourPoint).MpInt(secret).ToArray());
                _sessionId ??= hash;

                byte[] rs;
                ECDsa signer = _server._hostKey!;
                lock (signer)
                {
                    rs = signer.SignData(hash, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                }
                byte[] signature = new Wire().Str(HostKeyName)
                    .Str(new Wire().MpInt(rs.AsSpan(0, rs.Length / 2)).MpInt(rs.AsSpan(rs.Length / 2)).ToArray()).ToArray();

                Send(new Wire().U8(31).Str(_server._hostKeyBlob).Str(ourPoint).Str(signature));
                Send(new Wire().U8(21));

                byte[] sharedAsNumber = new Wire().MpInt(secret).ToArray();
                byte[] sessionId = _sessionId;
                byte[] Derive(char letter, int count)
                {
                    byte[] made = SHA256.HashData(new Wire().Raw(sharedAsNumber).Raw(hash).U8((byte)letter).Raw(sessionId).ToArray());
                    while (made.Length < count)
                    {
                        byte[] more = SHA256.HashData(new Wire().Raw(sharedAsNumber).Raw(hash).Raw(made).ToArray());
                        byte[] joined = new byte[made.Length + more.Length];
                        made.CopyTo(joined, 0);
                        more.CopyTo(joined, made.Length);
                        made = joined;
                    }
                    return made.AsSpan(0, count).ToArray();
                }

                _out?.Dispose();
                _out = new Keys(cipherOut, macOut, Derive, 'B', 'D', 'F');
                if (_strict) _outSequence = 0;

                if (ReadKexPacket(initial)[0] != 21) throw new Malformed("Expected the client to switch to the new keys.");
                _in?.Dispose();
                _in = new Keys(cipherIn, macIn, Derive, 'A', 'C', 'E');
                if (_strict) _inSequence = 0;

                CryptographicOperations.ZeroMemory(secret);
                CryptographicOperations.ZeroMemory(sharedAsNumber);
            }

            private byte[] ReadKexPacket(bool initial)
            {
                while (true)
                {
                    byte[] packet = ReadPacket();
                    if (packet[0] == 1) throw new HangUp();
                    // In a strict first exchange nothing else may be slipped in:
                    // that is the whole of the fix.
                    if (packet[0] is 2 or 3 or 4 && !(initial && _strict)) continue;
                    return packet;
                }
            }

            // ---- signing in --------------------------------------------------

            private void SignIn()
            {
                long deadline = Environment.TickCount64 + SignInGraceMs;
                int wrong = 0;

                for (int requests = 0; requests < MaxSignInRequests; requests++)
                {
                    byte[] packet = Next();
                    if (Environment.TickCount64 > deadline) break;

                    var message = new Reader(packet, 1, packet.Length - 1);
                    if (packet[0] == 5)
                    {
                        string service = message.Text();
                        if (service != "ssh-userauth") Disconnect(7, "This server only does SFTP.");
                        Send(new Wire().U8(6).Str(service));
                        continue;
                    }
                    if (packet[0] != 50)
                    {
                        Unimplemented();
                        continue;
                    }

                    string user = message.Text();
                    string wanted = message.Text();
                    string method = message.Text();

                    if (method == "password" && wanted == "ssh-connection" && message.U8() == 0)
                    {
                        string password = message.Text();
                        if (_server.Throttle.IsBlocked(_remote))
                            Disconnect(14, "Too many wrong passwords from your address. Try again in a few minutes.");

                        // Both halves are always compared, so the time taken does
                        // not say whether the name alone was right.
                        bool nameRight = ServedFolder.SameSecret(user, _server._user);
                        bool passwordRight = ServedFolder.SameSecret(password, _server._password);
                        // Passwords being guessed from many addresses at once:
                        // every answer waits, right or wrong alike.
                        _server.Throttle.Slow();
                        if (nameRight & passwordRight)
                        {
                            _server.Throttle.Succeeded(_remote);
                            Send(new Wire().U8(52));
                            return;
                        }

                        wrong++;
                        if (_server.Throttle.Failed(_remote))
                            Disconnect(14, "Too many wrong passwords from your address. Try again in a few minutes.");
                        if (wrong >= MaxWrongTriesPerConnection)
                            Disconnect(14, "The user name or password is wrong.");
                    }

                    // "Not that way, or not that password. Passwords are all there is."
                    Send(new Wire().U8(51).Str("password").Bool(false));
                }

                Disconnect(14, "Signing in took too long.");
            }

            // ---- channels ----------------------------------------------------

            private sealed class Channel
            {
                public uint Local, Remote;
                public long RemoteWindow;
                public uint RemoteMaxPacket;
                public int LocalWindow = WindowBytes;
                public int Consumed;
                public bool Sftp, EndOfInput, CloseSent;

                // What the client has sent and we have not yet acted on.
                public byte[] Input = new byte[64 * 1024];
                public int InputAt, InputEnd;

                // Replies waiting for the client to have room for them.
                public readonly Queue<byte[]> Output = new();
                public int OutputAt;
                public long OutputBytes;

                public readonly Dictionary<uint, OpenHandle> Handles = new();
                public uint NextHandle;

                public void CloseHandles()
                {
                    foreach (OpenHandle handle in Handles.Values) handle.Dispose();
                    Handles.Clear();
                }
            }

            private sealed class OpenHandle : IDisposable
            {
                public string Path = "";
                public FileStream? File;
                public bool Append;
                public IEnumerator<ServedEntry>? Rows;
                public bool RowsDone;

                public void Dispose()
                {
                    try { File?.Dispose(); } catch { }
                    try { Rows?.Dispose(); } catch { }
                    File = null;
                    Rows = null;
                }
            }

            private void Handle(byte[] packet)
            {
                var message = new Reader(packet, 1, packet.Length - 1);
                switch (packet[0])
                {
                    case 80:    // a request about the whole connection: forwarding, keep-alives
                    {
                        message.Text();
                        if (message.U8() != 0) Send(new Wire().U8(82));     // none are granted; a keep-alive only wants an answer
                        break;
                    }
                    case 90:
                        OpenChannel(message);
                        break;
                    case 93:
                    {
                        Channel channel = ChannelFor(message);
                        channel.RemoteWindow += message.U32();
                        if (channel.RemoteWindow > uint.MaxValue) throw new Malformed("A window larger than SSH allows.");
                        break;
                    }
                    case 94:
                    case 95:
                    {
                        Channel channel = ChannelFor(message);
                        if (packet[0] == 95) message.U32();
                        ReadOnlySpan<byte> data = message.Bytes();
                        if (data.Length > channel.LocalWindow) throw new Malformed("More data than the window allowed.");
                        channel.LocalWindow -= data.Length;

                        if (packet[0] == 94 && channel.Sftp && !channel.CloseSent && !channel.EndOfInput)
                        {
                            if (channel.InputEnd + data.Length > channel.Input.Length)
                            {
                                int waiting = channel.InputEnd - channel.InputAt;
                                byte[] roomier = waiting + data.Length > channel.Input.Length
                                    ? new byte[Math.Max(channel.Input.Length * 2, waiting + data.Length)]
                                    : channel.Input;
                                Buffer.BlockCopy(channel.Input, channel.InputAt, roomier, 0, waiting);
                                channel.Input = roomier;
                                channel.InputAt = 0;
                                channel.InputEnd = waiting;
                            }
                            data.CopyTo(channel.Input.AsSpan(channel.InputEnd));
                            channel.InputEnd += data.Length;
                        }
                        else
                        {
                            channel.Consumed += data.Length;    // nobody is listening; just give the room back
                        }
                        break;
                    }
                    case 96:
                        ChannelFor(message).EndOfInput = true;
                        break;
                    case 97:
                    {
                        Channel channel = ChannelFor(message);
                        if (!channel.CloseSent) Send(new Wire().U8(97).U32(channel.Remote));
                        channel.CloseHandles();
                        _channels.Remove(channel.Local);
                        break;
                    }
                    case 98:
                    {
                        Channel channel = ChannelFor(message);
                        string kind = message.Text();
                        bool wantsReply = message.U8() != 0;
                        // The one thing a channel here can become. A shell, a
                        // command, a terminal, an agent, X11: all refused.
                        bool granted = kind == "subsystem" && !channel.Sftp && !channel.CloseSent && message.Text() == "sftp";
                        if (granted) channel.Sftp = true;
                        if (wantsReply) Send(new Wire().U8(granted ? (byte)99 : (byte)100).U32(channel.Remote));
                        break;
                    }
                    case 99:
                    case 100:
                        break;      // answers to requests; we make none that want one
                    default:
                        Unimplemented();
                        break;
                }
            }

            private Channel ChannelFor(Reader message)
            {
                if (!_channels.TryGetValue(message.U32(), out Channel? channel)) throw new Malformed("A message for a channel that is not open.");
                return channel;
            }

            private void OpenChannel(Reader message)
            {
                string kind = message.Text();
                uint theirs = message.U32();
                uint window = message.U32();
                uint maxPacket = message.U32();

                if (kind != "session")
                {
                    // Forwarded ports, X11 and the rest.
                    Send(new Wire().U8(92).U32(theirs).U32(1).Str("This server only does SFTP.").Str(""));
                    return;
                }
                if (_channels.Count >= MaxChannels)
                {
                    Send(new Wire().U8(92).U32(theirs).U32(4).Str("Too many sessions on one connection.").Str(""));
                    return;
                }

                var channel = new Channel { Local = _nextChannel++, Remote = theirs, RemoteWindow = window, RemoteMaxPacket = maxPacket };
                _channels[channel.Local] = channel;
                Send(new Wire().U8(91).U32(theirs).U32(channel.Local).U32(WindowBytes).U32(MaxDataPerPacket));
            }

            /// <summary>
            /// Moves a channel along as far as it can go: sends what the client
            /// has room for, and answers requests only while the pile of unsent
            /// answers is small. That keeps memory bounded when a client asks
            /// faster than it reads, and honours its window and ours.
            /// </summary>
            private void Pump(Channel channel)
            {
                if (!_channels.ContainsKey(channel.Local)) return;

                bool moved = true;
                while (moved && !channel.CloseSent)
                {
                    moved = false;

                    while (channel.OutputBytes > 0 && channel.RemoteWindow > 0)
                    {
                        byte[] first = channel.Output.Peek();
                        int count = (int)Math.Min(Math.Min(first.Length - channel.OutputAt, channel.RemoteWindow),
                            Math.Min(MaxDataPerPacket, Math.Max(1u, channel.RemoteMaxPacket)));
                        Send(new Wire().U8(94).U32(channel.Remote).Str(first.AsSpan(channel.OutputAt, count)));
                        channel.OutputAt += count;
                        channel.OutputBytes -= count;
                        channel.RemoteWindow -= count;
                        if (channel.OutputAt == first.Length)
                        {
                            channel.Output.Dequeue();
                            channel.OutputAt = 0;
                        }
                        moved = true;
                    }

                    if (channel.OutputBytes < PendingOutputLimit && channel.InputEnd - channel.InputAt >= 4)
                    {
                        uint length = BinaryPrimitives.ReadUInt32BigEndian(channel.Input.AsSpan(channel.InputAt));
                        if (length < 1 || length > MaxSftpMessageBytes)
                        {
                            CloseChannel(channel, 1);
                            return;
                        }
                        if (channel.InputEnd - channel.InputAt - 4 >= length)
                        {
                            byte[] reply = Sftp(channel, new Reader(channel.Input, channel.InputAt + 4, (int)length));
                            channel.InputAt += 4 + (int)length;
                            channel.Consumed += 4 + (int)length;
                            if (channel.InputAt == channel.InputEnd) channel.InputAt = channel.InputEnd = 0;

                            byte[] framed = new byte[4 + reply.Length];
                            BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)reply.Length);
                            reply.CopyTo(framed, 4);
                            channel.Output.Enqueue(framed);
                            channel.OutputBytes += framed.Length;
                            moved = true;
                        }
                    }
                }

                if (channel.CloseSent) return;

                if (channel.Consumed >= WindowBytes / 4)
                {
                    Send(new Wire().U8(93).U32(channel.Remote).U32((uint)channel.Consumed));
                    channel.LocalWindow += channel.Consumed;
                    channel.Consumed = 0;
                }

                // The client has said all it will, and has all its answers.
                if (channel.EndOfInput && channel.OutputBytes == 0) CloseChannel(channel, 0);
            }

            private void CloseChannel(Channel channel, uint exitStatus)
            {
                channel.CloseHandles();
                Send(new Wire().U8(98).U32(channel.Remote).Str("exit-status").Bool(false).U32(exitStatus));
                Send(new Wire().U8(96).U32(channel.Remote));
                Send(new Wire().U8(97).U32(channel.Remote));
                channel.CloseSent = true;
                channel.Output.Clear();
                channel.OutputBytes = 0;
            }

            // ---- SFTP, version 3 -------------------------------------------

            private const uint Ok = 0, EndOfFile = 1, NoSuchFile = 2, PermissionDenied = 3, Failure = 4, BadMessage = 5, Unsupported = 8;

            private static byte[] Status(uint id, uint code, string sentence) =>
                new Wire().U8(101).U32(id).U32(code).Str(sentence).Str("").ToArray();

            private static byte[] Problem(uint id, Exception error) => ServedFolder.Classify(error) switch
            {
                ServedProblem.NotFound => Status(id, NoSuchFile, "That file or folder is not there."),
                ServedProblem.Denied => Status(id, PermissionDenied, "Windows would not allow that."),
                ServedProblem.InUse => Status(id, Failure, "That file is in use by another program. Try again in a moment."),
                ServedProblem.DiskFull => Status(id, Failure, "The disk is full."),
                _ => Status(id, Failure, "That did not work because of a problem on this PC."),
            };

            private static byte[] NotAllowed(uint id) => Status(id, PermissionDenied, "That name is not allowed here.");

            private static byte[] ReadOnly(uint id) => Status(id, PermissionDenied, "This folder is shared for reading only.");

            /// <summary>
            /// Works out where a client's path points. "/" is the shared folder
            /// and a path with no slash in front is taken from there. ".." is
            /// walked here, where it can never rise above the shared folder,
            /// and what is left goes through the shared name checks.
            /// </summary>
            private bool Locate(string? path, out List<string> parts, out string local, bool stopAtTop = false)
            {
                parts = new List<string>();
                local = "";
                if (path == null) return false;

                foreach (string piece in path.Split('/'))
                {
                    if (piece.Length == 0 || piece == ".") continue;
                    if (piece == "..")
                    {
                        if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                        else if (!stopAtTop) return false;
                        continue;
                    }
                    parts.Add(piece);
                }

                string? found = ServedFolder.Resolve(_server._root, string.Join('/', parts));
                if (found == null) return false;
                local = found;
                return true;
            }

            private byte[] Sftp(Channel channel, Reader message)
            {
                uint id = 0;
                try
                {
                    byte kind = message.U8();
                    if (kind == 1)
                    {
                        // Version 3 is the one every client speaks. The extension
                        // is the rename that replaces, which OpenSSH prefers.
                        return new Wire().U8(2).U32(3).Str("posix-rename@openssh.com").Str("1").ToArray();
                    }

                    id = message.U32();
                    return SftpRequest(channel, kind, id, message);
                }
                catch (Malformed)
                {
                    return Status(id, BadMessage, "That request could not be understood.");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException
                    || ex is ArgumentException || ex is InvalidOperationException || ex is System.Security.SecurityException)
                {
                    return Problem(id, ex);
                }
            }

            private byte[] SftpRequest(Channel channel, byte kind, uint id, Reader message)
            {
                bool readOnly = _server._readOnly;

                switch (kind)
                {
                    case 3:     // open
                    {
                        string? path = message.Name();
                        uint flags = message.U32();
                        ReadAttributes(message);
                        const uint Read = 1, Write = 2, Append = 4, Create = 8, Truncate = 16, Exclusive = 32;

                        bool writes = (flags & (Write | Append | Create | Truncate)) != 0;
                        if (writes && readOnly) return ReadOnly(id);
                        if (!Locate(path, out List<string> parts, out string local)) return NotAllowed(id);
                        if (parts.Count == 0 || Directory.Exists(local)) return Status(id, Failure, "That is a folder, not a file.");
                        if (channel.Handles.Count >= MaxHandles) return Status(id, Failure, "Too many files are open on this connection.");

                        FileMode mode =
                            (flags & Create) != 0 && (flags & Exclusive) != 0 ? FileMode.CreateNew
                            : (flags & Create) != 0 && (flags & Truncate) != 0 ? FileMode.Create
                            : (flags & Create) != 0 ? FileMode.OpenOrCreate
                            : (flags & Truncate) != 0 ? FileMode.Truncate
                            : FileMode.Open;
                        FileAccess access = !writes ? FileAccess.Read : (flags & Read) != 0 ? FileAccess.ReadWrite : FileAccess.Write;
                        // A file being read is shared as widely as Windows allows, so
                        // one open in another program can still be fetched.
                        FileShare share = writes ? FileShare.Read | FileShare.Delete : FileShare.ReadWrite | FileShare.Delete;

                        var handle = new OpenHandle
                        {
                            Path = local,
                            Append = (flags & Append) != 0,
                            File = new FileStream(local, mode, access, share, 64 * 1024),
                        };
                        return NewHandle(channel, id, handle);
                    }
                    case 4:     // close
                    {
                        uint key = HandleKey(message);
                        if (!channel.Handles.Remove(key, out OpenHandle? handle)) return Status(id, Failure, "That file is not open.");
                        FileStream? file = handle.File;
                        handle.File = null;
                        handle.Dispose();
                        file?.Dispose();        // outside the quiet Dispose: a failed last write must be reported
                        return Status(id, Ok, "Closed.");
                    }
                    case 5:     // read
                    {
                        OpenHandle? handle = FileHandle(channel, message);
                        ulong offset = message.U64();
                        int count = (int)Math.Min(message.U32(), MaxReadBytes);
                        if (handle?.File == null || !handle.File.CanRead) return Status(id, Failure, "That file is not open for reading.");
                        if (offset > long.MaxValue) return Status(id, EndOfFile, "That is the end of the file.");

                        byte[] data = new byte[count];
                        int got = 0;
                        handle.File.Position = (long)offset;
                        while (got < count)
                        {
                            int read = handle.File.Read(data, got, count - got);
                            if (read <= 0) break;
                            got += read;
                        }
                        if (got == 0 && count > 0) return Status(id, EndOfFile, "That is the end of the file.");
                        return new Wire().U8(103).U32(id).Str(data.AsSpan(0, got)).ToArray();
                    }
                    case 6:     // write
                    {
                        OpenHandle? handle = FileHandle(channel, message);
                        ulong offset = message.U64();
                        ReadOnlySpan<byte> data = message.Bytes();
                        if (readOnly) return ReadOnly(id);
                        if (handle?.File == null || !handle.File.CanWrite) return Status(id, Failure, "That file is not open for writing.");
                        if (offset > long.MaxValue - (ulong)data.Length) return Status(id, Failure, "That is past the largest file there can be.");

                        handle.File.Position = handle.Append ? handle.File.Length : (long)offset;
                        handle.File.Write(data);
                        return Status(id, Ok, "Written.");
                    }
                    case 7:     // lstat
                    case 17:    // stat
                    {
                        if (!Locate(message.Name(), out _, out string local)) return NotAllowed(id);
                        if (ServedFolder.Describe(local) is not ServedEntry entry) return Status(id, NoSuchFile, "That file or folder is not there.");
                        return Attributes(new Wire().U8(105).U32(id), entry).ToArray();
                    }
                    case 8:     // fstat
                    {
                        OpenHandle? handle = AnyHandle(channel, message);
                        if (handle == null) return Status(id, Failure, "That file is not open.");
                        handle.File?.Flush();
                        if (ServedFolder.Describe(handle.Path) is not ServedEntry entry) return Status(id, NoSuchFile, "That file or folder is not there.");
                        if (handle.File != null) entry = entry with { Length = handle.File.Length };
                        return Attributes(new Wire().U8(105).U32(id), entry).ToArray();
                    }
                    case 9:     // setstat
                    {
                        string? path = message.Name();
                        ClientAttributes wanted = ReadAttributes(message);
                        if (readOnly) return ReadOnly(id);
                        if (!Locate(path, out _, out string local)) return NotAllowed(id);
                        if (!File.Exists(local) && !Directory.Exists(local)) return Status(id, NoSuchFile, "That file or folder is not there.");

                        if (wanted.Size is ulong size)
                        {
                            if (size > long.MaxValue) return Status(id, Failure, "That is past the largest file there can be.");
                            using var file = new FileStream(local, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                            file.SetLength((long)size);
                        }
                        if (wanted.Modified is DateTime modified)
                        {
                            if (Directory.Exists(local)) Directory.SetLastWriteTimeUtc(local, modified);
                            else File.SetLastWriteTimeUtc(local, modified);
                        }
                        return Status(id, Ok, "Done.");     // permissions, owner and group mean nothing on Windows and are let pass
                    }
                    case 10:    // fsetstat
                    {
                        OpenHandle? handle = AnyHandle(channel, message);
                        ClientAttributes wanted = ReadAttributes(message);
                        if (readOnly) return ReadOnly(id);
                        if (handle == null) return Status(id, Failure, "That file is not open.");

                        if (handle.File != null)
                        {
                            if (wanted.Size is ulong size)
                            {
                                if (size > long.MaxValue || !handle.File.CanWrite) return Status(id, Failure, "The size of that file cannot be set.");
                                handle.File.SetLength((long)size);
                            }
                            if (wanted.Modified is DateTime modified)
                            {
                                // Flushed first, and set through the open handle: a
                                // time set by name would be overwritten by "now"
                                // when the last buffered bytes are written at close.
                                handle.File.Flush();
                                try { File.SetLastWriteTimeUtc(handle.File.SafeFileHandle, modified); }
                                catch (UnauthorizedAccessException) { File.SetLastWriteTimeUtc(handle.Path, modified); }
                            }
                        }
                        else if (wanted.Modified is DateTime modified)
                        {
                            Directory.SetLastWriteTimeUtc(handle.Path, modified);
                        }
                        return Status(id, Ok, "Done.");
                    }
                    case 11:    // opendir
                    {
                        if (!Locate(message.Name(), out _, out string local)) return NotAllowed(id);
                        if (!Directory.Exists(local)) return Status(id, NoSuchFile, "There is no folder by that name.");
                        if (channel.Handles.Count >= MaxHandles) return Status(id, Failure, "Too many files are open on this connection.");

                        IEnumerator<ServedEntry> rows = ServedFolder.List(_server._root, local).GetEnumerator();
                        return NewHandle(channel, id, new OpenHandle { Path = local, Rows = rows });
                    }
                    case 12:    // readdir
                    {
                        OpenHandle? handle = AnyHandle(channel, message);
                        if (handle?.Rows == null) return Status(id, Failure, "That folder is not open.");
                        if (handle.RowsDone) return Status(id, EndOfFile, "That is the whole list.");

                        var names = new Wire();
                        uint count = 0;
                        DateTime now = DateTime.Now;
                        while (count < 100 && names.Length < 16 * 1024)
                        {
                            if (!handle.Rows.MoveNext()) { handle.RowsDone = true; break; }
                            ServedEntry row = handle.Rows.Current;
                            names.Str(row.Name).Str(LongName(row, now));
                            Attributes(names, row);
                            count++;
                        }
                        if (count == 0) return Status(id, EndOfFile, "That is the whole list.");
                        return new Wire().U8(104).U32(id).U32(count).Raw(names.ToArray()).ToArray();
                    }
                    case 13:    // remove
                    {
                        string? path = message.Name();
                        if (readOnly) return ReadOnly(id);
                        if (!Locate(path, out _, out string local)) return NotAllowed(id);
                        if (Directory.Exists(local)) return Status(id, Failure, "That is a folder, not a file.");
                        if (!File.Exists(local)) return Status(id, NoSuchFile, "There is no file by that name.");
                        File.Delete(local);
                        return Status(id, Ok, "Deleted.");
                    }
                    case 14:    // mkdir
                    {
                        string? path = message.Name();
                        ReadAttributes(message);
                        if (readOnly) return ReadOnly(id);
                        if (!Locate(path, out List<string> parts, out string local)) return NotAllowed(id);
                        if (parts.Count == 0 || Directory.Exists(local) || File.Exists(local)) return Status(id, Failure, "Something by that name is already there.");
                        if (!Directory.Exists(Path.GetDirectoryName(local))) return Status(id, NoSuchFile, "The folder to make that in is not there.");
                        Directory.CreateDirectory(local);
                        return Status(id, Ok, "Folder made.");
                    }
                    case 15:    // rmdir
                    {
                        string? path = message.Name();
                        if (readOnly) return ReadOnly(id);
                        if (!Locate(path, out List<string> parts, out string local)) return NotAllowed(id);
                        if (parts.Count == 0) return Status(id, PermissionDenied, "The shared folder itself cannot be removed.");
                        if (!Directory.Exists(local)) return Status(id, NoSuchFile, "There is no folder by that name.");
                        try
                        {
                            Directory.Delete(local, recursive: false);     // only an empty folder goes, as with rmdir everywhere
                        }
                        catch (IOException ex) when ((ex.HResult & 0xFFFF) == 145)
                        {
                            return Status(id, Failure, "That folder is not empty.");
                        }
                        return Status(id, Ok, "Folder removed.");
                    }
                    case 16:    // realpath
                    {
                        // Only a name is being tidied here, nothing is opened, so
                        // ".." at the top simply stays at the top, as "cd .." does.
                        if (!Locate(message.Name(), out List<string> parts, out _, stopAtTop: true)) return NotAllowed(id);
                        string tidy = "/" + string.Join('/', parts);
                        return new Wire().U8(104).U32(id).U32(1).Str(tidy).Str(tidy).U32(0).ToArray();
                    }
                    case 18:    // rename: refuses to replace
                    {
                        string? from = message.Name();
                        string? to = message.Name();
                        return Rename(id, from, to, replace: false);
                    }
                    case 19:    // readlink
                    case 20:    // symlink
                        return Status(id, Unsupported, "Links are not available on this server.");
                    case 200:   // extended
                    {
                        string name = message.Text();
                        if (name == "posix-rename@openssh.com")
                        {
                            string? from = message.Name();
                            string? to = message.Name();
                            return Rename(id, from, to, replace: true);
                        }
                        return Status(id, Unsupported, "This server does not know that request.");
                    }
                    default:
                        return Status(id, Unsupported, "This server does not know that request.");
                }
            }

            private byte[] Rename(uint id, string? from, string? to, bool replace)
            {
                if (_server._readOnly) return ReadOnly(id);
                if (!Locate(from, out List<string> fromParts, out string source)) return NotAllowed(id);
                if (!Locate(to, out List<string> toParts, out string target)) return NotAllowed(id);
                if (fromParts.Count == 0 || toParts.Count == 0) return Status(id, PermissionDenied, "The shared folder itself cannot be renamed.");

                bool sameButForCase = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
                if (Directory.Exists(source))
                {
                    if (!sameButForCase && (Directory.Exists(target) || File.Exists(target))) return Status(id, Failure, "Something by that name is already there.");
                    if (target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        return Status(id, Failure, "A folder cannot be moved inside itself.");
                    if (!string.Equals(source, target, StringComparison.Ordinal)) Directory.Move(source, target);
                    return Status(id, Ok, "Renamed.");
                }

                if (!File.Exists(source)) return Status(id, NoSuchFile, "That file or folder is not there.");
                if (Directory.Exists(target)) return Status(id, Failure, "A folder by that name is already there.");
                if (!replace && !sameButForCase && File.Exists(target)) return Status(id, Failure, "Something by that name is already there.");

                if (replace) ServedFolder.MoveIntoPlace(source, target);
                else File.Move(source, target);
                return Status(id, Ok, "Renamed.");
            }

            private static byte[] NewHandle(Channel channel, uint id, OpenHandle handle)
            {
                uint key = channel.NextHandle++;
                channel.Handles[key] = handle;
                Span<byte> name = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(name, key);
                return new Wire().U8(102).U32(id).Str(name).ToArray();
            }

            private static uint HandleKey(Reader message)
            {
                ReadOnlySpan<byte> name = message.Bytes();
                return name.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(name) : uint.MaxValue;
            }

            private static OpenHandle? AnyHandle(Channel channel, Reader message) =>
                channel.Handles.TryGetValue(HandleKey(message), out OpenHandle? handle) ? handle : null;

            private static OpenHandle? FileHandle(Channel channel, Reader message)
            {
                OpenHandle? handle = AnyHandle(channel, message);
                return handle?.File == null ? null : handle;
            }

            private readonly record struct ClientAttributes(ulong? Size, DateTime? Modified);

            private static ClientAttributes ReadAttributes(Reader message)
            {
                uint flags = message.U32();
                ulong? size = null;
                DateTime? modified = null;

                if ((flags & 1) != 0) size = message.U64();
                if ((flags & 2) != 0) { message.U32(); message.U32(); }     // owner and group
                if ((flags & 4) != 0) message.U32();                        // permissions
                if ((flags & 8) != 0)
                {
                    message.U32();                                          // last read; Windows keeps its own
                    modified = DateTime.UnixEpoch.AddSeconds(message.U32());
                }
                if ((flags & 0x80000000) != 0)
                {
                    uint count = message.U32();
                    for (uint i = 0; i < count; i++) { message.Bytes(); message.Bytes(); }
                }
                return new ClientAttributes(size, modified);
            }

            private Wire Attributes(Wire into, ServedEntry entry)
            {
                bool readOnly = _server._readOnly;
                uint rights = entry.IsFolder
                    ? 0x4000u | (readOnly ? 0x16Du : 0x1EDu)        // drwxr-xr-x, or dr-xr-xr-x
                    : 0x8000u | (readOnly ? 0x124u : 0x1A4u);       // -rw-r--r--, or -r--r--r--
                double seconds = (entry.ModifiedUtc - DateTime.UnixEpoch).TotalSeconds;
                uint when = (uint)Math.Clamp(seconds, 0, uint.MaxValue);

                // Size, owner and group, permissions, times.
                return into.U32(1 | 2 | 4 | 8).U64((ulong)Math.Max(0, entry.Length)).U32(0).U32(0).U32(rights).U32(when).U32(when);
            }

            /// <summary>The line "ls -l" shows, which SFTP version 3 has the server write.</summary>
            private string LongName(ServedEntry row, DateTime now)
            {
                string rights = (row.IsFolder ? "d" : "-") + (_server._readOnly
                    ? (row.IsFolder ? "r-xr-xr-x" : "r--r--r--")
                    : (row.IsFolder ? "rwxr-xr-x" : "rw-r--r--"));
                DateTime when = row.ModifiedUtc.ToLocalTime();
                string date = when > now.AddDays(-180) && when <= now.AddDays(1)
                    ? when.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture)
                    : when.ToString("MMM dd  yyyy", CultureInfo.InvariantCulture);
                return rights + "    1 owner    group    " + row.Length.ToString(CultureInfo.InvariantCulture).PadLeft(12) + " " + date + " " + row.Name;
            }
        }
    }
}
