using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The file servers, driven the way a real client drives them: each one is
    /// started on this PC over a temporary folder and spoken to through real
    /// sockets. Beside the shared folder sits a file no client may ever reach.
    /// </summary>
    internal static class FileServerTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        private const string User = "conner";
        private const string Password = "pa ss w\u00F6rd";
        private const string OddName = "h\u00E9llo w\u00F6rld \u2603.txt";
        private const string Hello = "Hello, world! 0123456789";

        private static volatile string _stage = "starting";
        private static readonly List<IDisposable> _servers = new();

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("File servers:");

            string home = Path.Combine(Path.GetTempPath(), "en-fileservers-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Task run = Task.Run(() => Everything(home));
                // A server that stops answering must fail the run, not hang it.
                if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60))) != run)
                {
                    Check("the file server tests finished inside a minute", false, "stuck at: " + _stage);
                }
                else
                {
                    try { await run; }
                    catch (Exception ex) { Check("the file server tests ran to the end", false, _stage + ": " + ex); }
                }
            }
            finally
            {
                lock (_servers)
                {
                    foreach (IDisposable server in _servers)
                    {
                        try { server.Dispose(); } catch { }
                    }
                    _servers.Clear();
                }
                for (int attempt = 0; attempt < 20 && Directory.Exists(home); attempt++)
                {
                    try { Directory.Delete(home, recursive: true); }
                    catch { Thread.Sleep(100); }
                }
            }

            Console.WriteLine();
        }

        private static T Track<T>(T server) where T : IDisposable
        {
            lock (_servers) _servers.Add(server);
            return server;
        }

        private static void Everything(string home)
        {
            string share = Path.Combine(home, "share");
            Directory.CreateDirectory(Path.Combine(share, "sub"));
            File.WriteAllText(Path.Combine(home, "secret.txt"), "secret");
            File.WriteAllText(Path.Combine(share, "hello.txt"), Hello);
            File.WriteAllText(Path.Combine(share, "sub", "inner.txt"), "inner");
            File.WriteAllText(Path.Combine(share, OddName), "odd");
            byte[] big = RandomNumberGenerator.GetBytes(300_000);
            File.WriteAllBytes(Path.Combine(share, "big.bin"), big);

            _stage = "path rules"; ResolveTests(home, share);
            _stage = "throttle"; ThrottleTests();
            _stage = "ftp sign-in"; FtpSignInTests(share);
            _stage = "ftp files"; FtpFileTests(home, share, big);
            _stage = "ftp escapes"; FtpEscapeTests(home, share);
            _stage = "ftp data connections"; FtpDataConnectionTests(share);
            _stage = "ftp read-only"; FtpReadOnlyTests(share);
            _stage = "ftp throttle"; FtpThrottleTests(share);
            _stage = "ftp limits"; FtpLimitTests(share);
            _stage = "ftp tls"; FtpTlsTests(share, big);
            _stage = "ftp stopping"; FtpStopTests(share);
            _stage = "sftp"; SftpTests(home, share, big);
        }

        // ---- the shared path rules ------------------------------------------

        private static void ResolveTests(string home, string share)
        {
            Equal("an empty path is the shared folder", share, ServedFolder.Resolve(share, "") ?? "(refused)");
            Equal("a slash alone is too", share, ServedFolder.Resolve(share + "\\", "/") ?? "(refused)");
            Equal("a path is put under the folder", Path.Combine(share, "sub", "inner.txt"),
                ServedFolder.Resolve(share, "sub/inner.txt") ?? "(refused)");
            Equal("doubled and trailing slashes do not matter", Path.Combine(share, "sub", "inner.txt"),
                ServedFolder.Resolve(share, "//sub//inner.txt/") ?? "(refused)");
            Equal("a whole drive can be shared", @"G:\music\a.mp3", ServedFolder.Resolve(@"G:\", "music/a.mp3") ?? "(refused)");
            Equal("and its root is itself", @"G:\", ServedFolder.Resolve(@"G:\", "") ?? "(refused)");

            foreach (string fine in new[] { OddName, ".hidden", "COM10", "console.txt", "a.b.c", " leading space", "nul2", "sub/" + OddName })
            {
                Check("allowed: " + fine, ServedFolder.Resolve(share, fine) != null);
            }

            string[] refused =
            {
                "..", "../secret.txt", "sub/../../secret.txt", "sub/..", "..\\secret.txt", "sub\\inner.txt", "\\secret.txt",
                "C:", "C:/Windows/win.ini", "C:\\Windows\\win.ini", "hello.txt:stream", "hello.txt::$DATA",
                "CON", "con", "con.txt", "NUL", "nul.tar.gz", "aux", "PRN", "COM1", "com9.log", "LPT1", "lpt9.x", "CONIN$", "NUL .txt",
                "hello.txt.", "hello.txt ", "sub./inner.txt", ".", "sub/./inner.txt", "...",
                "a<b", "a>b", "a|b", "a?b", "a*b", "a\"b", "a\u0001b", "a\0b", "a\nb", "\\\\server\\share\\x", "//./C:/x",
                new string('a', 300),
            };
            foreach (string bad in refused)
            {
                string? got = ServedFolder.Resolve(share, bad);
                Check("refused: " + Show(bad), got == null, got);
            }

            // A junction inside the folder that points out of it.
            string link = Path.Combine(share, "way-out");
            bool linked = false;
            try
            {
                var mklink = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + home + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                });
                if (mklink != null)
                {
                    mklink.WaitForExit(5000);
                    linked = Directory.Exists(link) && File.Exists(Path.Combine(link, "secret.txt"));
                }
            }
            catch { }

            if (linked)
            {
                Check("a junction that leaves the folder is not followed", ServedFolder.Resolve(share, "way-out/secret.txt") == null);
                Check("nor opened itself", ServedFolder.Resolve(share, "way-out") == null);
                Check("nor listed", !ServedFolder.List(share, share).Any(row => row.Name == "way-out"));

                string inside = Path.Combine(share, "way-in");
                try
                {
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + inside + "\" \"" + Path.Combine(share, "sub") + "\"")
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    })?.WaitForExit(5000);
                    if (Directory.Exists(inside))
                        Check("a junction that stays inside is followed", ServedFolder.Resolve(share, "way-in/inner.txt") != null);
                    Directory.Delete(inside);
                }
                catch { }
                try { Directory.Delete(link); } catch { }
            }
            else
            {
                Console.WriteLine("  NOTE  no junction could be made here, so links leaving the folder were not tested");
            }

            Check("the names in a list are the real ones", ServedFolder.List(share, share).Any(row => row.Name == OddName && !row.IsFolder && row.Length == 3));
            Check("folders are marked as folders", ServedFolder.List(share, share).Any(row => row.Name == "sub" && row.IsFolder));
            string temp = ServedFolder.TempNameFor(Path.Combine(share, "x.bin"));
            File.WriteAllText(temp, "half");
            Check("an upload still arriving is not listed", !ServedFolder.List(share, share).Any(row => ServedFolder.IsTempName(row.Name)));
            Equal("and lands beside its target", share, Path.GetDirectoryName(temp) ?? "");
            File.Delete(temp);

            Check("the same secret matches", ServedFolder.SameSecret("abc", "abc"));
            Check("a different one does not", !ServedFolder.SameSecret("abc", "abd") && !ServedFolder.SameSecret("abc", "abcd") && !ServedFolder.SameSecret("", "abc"));
        }

        private static string Show(string text)
        {
            var shown = new StringBuilder();
            foreach (char c in text.Length > 40 ? text.Substring(0, 40) + "..." : text)
            {
                if (c < 32) shown.Append("\\x").Append(((int)c).ToString("x2"));
                else shown.Append(c);
            }
            return shown.ToString();
        }

        private static void ThrottleTests()
        {
            DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var throttle = new SignInThrottle(() => now);
            IPAddress them = IPAddress.Parse("203.0.113.9");
            IPAddress other = IPAddress.Parse("203.0.113.10");

            for (int i = 0; i < 9; i++) throttle.Failed(them);
            Check("nine wrong passwords are forgiven", !throttle.IsBlocked(them));
            Check("the tenth shuts the address out", throttle.Failed(them) && throttle.IsBlocked(them));
            Check("and only that address", !throttle.IsBlocked(other));
            Check("as IPv4 inside IPv6 it is the same address", throttle.IsBlocked(them.MapToIPv6()));

            now = now.AddMinutes(4.9);
            Check("still shut out just under five minutes later", throttle.IsBlocked(them));
            now = now.AddMinutes(0.2);
            Check("let back in after five minutes", !throttle.IsBlocked(them));

            for (int i = 0; i < 9; i++) throttle.Failed(other);
            now = now.AddMinutes(11);
            Check("wrong passwords over ten minutes old no longer count", !throttle.Failed(other) && !throttle.IsBlocked(other));

            for (int i = 0; i < 9; i++) throttle.Failed(them);
            throttle.Succeeded(them);
            Check("a right password wipes the slate", !throttle.Failed(them));

            IPAddress six = IPAddress.Parse("2001:db8:1:2::1");
            for (int i = 0; i < 10; i++) throttle.Failed(IPAddress.Parse("2001:db8:1:2::" + (i + 1).ToString("x")));
            Check("one IPv6 network counts as one caller", throttle.IsBlocked(six));
            Check("another network is not caught up in it", !throttle.IsBlocked(IPAddress.Parse("2001:db8:1:3::1")));

            var crowd = new SignInThrottle(() => now);
            for (int i = 0; i < 5000; i++) crowd.Failed(new IPAddress(new byte[] { 10, (byte)(i >> 16), (byte)(i >> 8), (byte)i }));
            Check("its notes stay a bounded size", crowd.TrackedAddresses <= 2048, crowd.TrackedAddresses.ToString());

            var busy = new SignInThrottle();
            Parallel.For(0, 2000, i =>
            {
                IPAddress a = new IPAddress(new byte[] { 10, 9, (byte)(i % 7), 1 });
                busy.Failed(a);
                busy.IsBlocked(a);
                if (i % 5 == 0) busy.Succeeded(a);
            });
            Check("and it survives many threads at once", busy.TrackedAddresses <= 7);
        }

        // ---- a small FTP client -------------------------------------------------

        private sealed class Ftp : IDisposable
        {
            public readonly TcpClient Tcp = new();
            public Stream Stream;
            public bool ProtectData;
            private readonly int _port;

            public Ftp(int port)
            {
                _port = port;
                Tcp.Connect(IPAddress.Loopback, port);
                Tcp.ReceiveTimeout = 10_000;
                Tcp.SendTimeout = 10_000;
                Tcp.NoDelay = true;
                Stream = Tcp.GetStream();
            }

            /// <summary>The next whole reply: its code and every line of it. Code 0 means the line went dead.</summary>
            public (int Code, string Text) Read()
            {
                var all = new StringBuilder();
                while (true)
                {
                    var line = new List<byte>();
                    try
                    {
                        while (true)
                        {
                            int b = Stream.ReadByte();
                            if (b < 0) return (0, all.ToString());
                            if (b == '\n') break;
                            line.Add((byte)b);
                        }
                    }
                    catch (IOException) { return (0, all.ToString()); }
                    catch (ObjectDisposedException) { return (0, all.ToString()); }

                    string text = Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                    all.Append(text).Append('\n');
                    if (text.Length >= 4 && char.IsDigit(text[0]) && char.IsDigit(text[1]) && char.IsDigit(text[2]) && text[3] == ' ')
                        return (int.Parse(text.Substring(0, 3)), all.ToString());
                }
            }

            public void SendLine(string line)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line + "\r\n");
                Stream.Write(bytes, 0, bytes.Length);
                Stream.Flush();
            }

            public (int Code, string Text) Cmd(string line)
            {
                try { SendLine(line); }
                catch (IOException) { return (0, ""); }
                return Read();
            }

            public int Code(string line) => Cmd(line).Code;

            public bool Login(string user = User, string password = Password)
            {
                if (Read().Code != 220) return false;
                return SignIn(user, password);
            }

            public bool SignIn(string user = User, string password = Password) =>
                Code("USER " + user) == 331 && Code("PASS " + password) == 230;

            public void StartTls()
            {
                var tls = new SslStream(Stream, false, (_, _, _, _) => true);
                tls.AuthenticateAsClient(new SslClientAuthenticationOptions { TargetHost = "localhost" });
                Stream = tls;
            }

            /// <summary>Asks for a passive port and connects to it.</summary>
            public TcpClient OpenData(bool extended = false) => ConnectData(PassivePort(extended));

            public int PassivePort(bool extended = false)
            {
                (int code, string text) = Cmd(extended ? "EPSV" : "PASV");
                if (extended)
                {
                    if (code != 229) throw new InvalidOperationException("EPSV answered " + text);
                    int open = text.IndexOf("(|||", StringComparison.Ordinal);
                    int close = text.IndexOf("|)", open, StringComparison.Ordinal);
                    return int.Parse(text.Substring(open + 4, close - open - 4));
                }

                if (code != 227) throw new InvalidOperationException("PASV answered " + text);
                int from = text.IndexOf('(');
                string[] n = text.Substring(from + 1, text.IndexOf(')') - from - 1).Split(',');
                if (n[0] + "." + n[1] + "." + n[2] + "." + n[3] != "127.0.0.1")
                    throw new InvalidOperationException("PASV gave the address " + text);
                return int.Parse(n[4]) * 256 + int.Parse(n[5]);
            }

            public static TcpClient ConnectData(int port)
            {
                var data = new TcpClient();
                data.Connect(IPAddress.Loopback, port);
                data.ReceiveTimeout = 10_000;
                data.SendTimeout = 10_000;
                return data;
            }

            private Stream Wrap(TcpClient data)
            {
                if (!ProtectData) return data.GetStream();
                var tls = new SslStream(data.GetStream(), false, (_, _, _, _) => true);
                tls.AuthenticateAsClient(new SslClientAuthenticationOptions { TargetHost = "localhost" });
                return tls;
            }

            /// <summary>Runs a command that sends data back (RETR, LIST...) and collects it.</summary>
            public (int Code, byte[] Data) Download(string command, bool extended = false)
            {
                using TcpClient data = OpenData(extended);
                (int code, string _) = Cmd(command);
                if (code != 150) return (code, Array.Empty<byte>());

                var all = new MemoryStream();
                using (Stream stream = Wrap(data))
                {
                    stream.CopyTo(all);
                }
                return (Read().Code, all.ToArray());
            }

            public (int Code, string Text) List(string command, bool extended = false)
            {
                (int code, byte[] data) = Download(command, extended);
                return (code, Encoding.UTF8.GetString(data));
            }

            /// <summary>Runs a command that takes data (STOR, APPE). With cutAfter the line is broken part way.</summary>
            public int Upload(string command, byte[] content, int cutAfter = -1)
            {
                TcpClient data = OpenData();
                try
                {
                    (int code, string _) = Cmd(command);
                    if (code != 150) return code;

                    Stream stream = Wrap(data);
                    if (cutAfter >= 0)
                    {
                        stream.Write(content, 0, cutAfter);
                        stream.Flush();
                        Thread.Sleep(150);                      // let the server take what was sent
                        data.Client.LingerState = new LingerOption(true, 0);
                        data.Client.Close();                    // a reset, as when the network drops
                        return Read().Code;
                    }

                    stream.Write(content, 0, content.Length);
                    stream.Flush();
                    if (stream is SslStream tls) tls.ShutdownAsync().Wait(5000);
                    stream.Dispose();
                    return Read().Code;
                }
                finally
                {
                    data.Dispose();
                }
            }

            public void Dispose()
            {
                try { Stream.Dispose(); } catch { }
                Tcp.Dispose();
            }
        }

        private static FtpServer StartFtp(string share, bool readOnly = false, X509Certificate2? tls = null)
        {
            var server = Track(new FtpServer(share, 0, User, Password, readOnly, tls));
            server.Start();
            return server;
        }

        private static bool NoTempFiles(string folder) =>
            !Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(f => ServedFolder.IsTempName(Path.GetFileName(f)));

        private static bool CanConnect(int port)
        {
            try
            {
                using var probe = new TcpClient();
                probe.Connect(IPAddress.Loopback, port);
                return true;
            }
            catch (SocketException) { return false; }
        }

        // ---- FTP ---------------------------------------------------------------

        private static void FtpSignInTests(string share)
        {
            using FtpServer server = StartFtp(share);

            using (var ftp = new Ftp(server.Port))
            {
                Equal("FTP greets a caller", "220", ftp.Read().Code.ToString());
                Equal("nothing is listed before signing in", "530", ftp.Code("LIST").ToString());
                Equal("nor fetched", "530", ftp.Code("RETR hello.txt").ToString());
                Equal("nor is a passive port opened", "530", ftp.Code("PASV").ToString());
                Equal("a password with no name is out of order", "503", ftp.Code("PASS " + Password).ToString());
                Equal("any name is asked for its password", "331", ftp.Code("USER nobody").ToString());
                Equal("the right password under the wrong name is refused", "530", ftp.Code("PASS " + Password).ToString());
                ftp.Code("USER " + User);
                Equal("a wrong password is refused", "530", ftp.Code("PASS wrong").ToString());
                ftp.Code("USER " + User);
                Equal("the third wrong try ends the connection", "421", ftp.Code("PASS").ToString());
                Equal("and the line really is closed", "0", ftp.Code("NOOP").ToString());
            }

            using (var ftp = new Ftp(server.Port))
            {
                ftp.Read();
                ftp.Code("USER anonymous");
                Equal("there is no anonymous sign-in", "530", ftp.Code("PASS someone@example.com").ToString());
                Check("the right name and password get in", ftp.SignIn());
                Equal("the system type is the usual one", "215", ftp.Code("SYST").ToString());
                (int code, string text) = ftp.Cmd("FEAT");
                Check("FEAT lists UTF8, passive modes and MLST", code == 211 && text.Contains(" UTF8\n") && text.Contains(" EPSV\n")
                    && text.Contains(" MLST type*;size*;modify*;\n") && text.Contains(" REST STREAM\n"), text);
                Check("and no TLS when there is no certificate", !text.Contains("AUTH"));
                Equal("AUTH TLS is refused with no certificate", "502", ftp.Code("AUTH TLS").ToString());
                Equal("OPTS UTF8 ON is accepted", "200", ftp.Code("OPTS UTF8 ON").ToString());
                Equal("TYPE I is accepted", "200", ftp.Code("TYPE I").ToString());
                Equal("TYPE A is accepted", "200", ftp.Code("TYPE A").ToString());
                (code, text) = ftp.Cmd("PWD");
                Check("the shared folder is /", code == 257 && text.StartsWith("257 \"/\" "), text);
                (code, text) = ftp.Cmd("PORT 127,0,0,1,10,10");
                Check("PORT is refused with a sentence", code == 502 && text.Contains("passive mode"), text);
                Equal("so is EPRT", "502", ftp.Code("EPRT |1|127.0.0.1|2570|").ToString());
                Equal("an unknown command is answered, not fatal", "502", ftp.Code("XYZZY").ToString());
                Equal("NOOP answers", "200", ftp.Code("NOOP").ToString());
                Equal("QUIT says goodbye", "221", ftp.Code("QUIT").ToString());
                Equal("and hangs up", "0", ftp.Read().Code.ToString());
            }
        }

        private static void FtpFileTests(string home, string share, byte[] big)
        {
            using FtpServer server = StartFtp(share);
            using var ftp = new Ftp(server.Port);
            Check("FTP sign-in", ftp.Login());

            // Listings.
            (int code, string list) = ftp.List("LIST");
            string[] lines = list.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Check("LIST over PASV ends well", code == 226, code.ToString());
            Check("LIST shows a file with its size", lines.Any(l => l.StartsWith("-rw-r--r--") && l.Contains(" " + Hello.Length + " ") && l.EndsWith(" hello.txt")), list);
            Check("LIST shows a folder", lines.Any(l => l.StartsWith("drwxr-xr-x") && l.EndsWith(" sub")), list);
            Check("LIST shows a name with accents, spaces and a snowman", lines.Any(l => l.EndsWith(" " + OddName)), list);
            Check("LIST -la is the same list", ftp.List("LIST -la").Text == list);

            (code, list) = ftp.List("MLSD", extended: true);
            lines = list.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            string stamp = File.GetLastWriteTimeUtc(Path.Combine(share, "hello.txt")).ToString("yyyyMMddHHmmss");
            Check("MLSD over EPSV ends well", code == 226, code.ToString());
            Check("MLSD gives type, size and time", lines.Contains("type=file;size=" + Hello.Length + ";modify=" + stamp + "; hello.txt"), list);
            Check("MLSD marks folders", lines.Any(l => l.StartsWith("type=dir;modify=") && l.EndsWith("; sub")), list);
            Check("MLSD has the odd name", lines.Any(l => l.EndsWith("; " + OddName)), list);

            (code, list) = ftp.List("NLST");
            Check("NLST is bare names", code == 226 && list.Split("\r\n").Contains("hello.txt") && list.Split("\r\n").Contains(OddName), list);
            (code, list) = ftp.List("LIST sub");
            Check("LIST of a named folder", code == 226 && list.Contains(" inner.txt") && !list.Contains("hello.txt"), list);
            (code, list) = ftp.List("LIST hello.txt");
            Check("LIST of a file is that one line", code == 226 && list.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length == 1, list);
            Equal("LIST of a missing folder", "550", ftp.Code("LIST nowhere").ToString());

            (code, string text) = ftp.Cmd("MLST hello.txt");
            Check("MLST answers on the control connection", code == 250 && text.Contains(" type=file;size=" + Hello.Length + ";modify=" + stamp + "; /hello.txt\n"), text);
            (code, text) = ftp.Cmd("SIZE hello.txt");
            Check("SIZE", code == 213 && text.Trim() == "213 " + Hello.Length, text);
            (code, text) = ftp.Cmd("MDTM hello.txt");
            Check("MDTM", code == 213 && text.Trim() == "213 " + stamp, text);
            Equal("SIZE of a missing file", "550", ftp.Code("SIZE nothing.txt").ToString());

            // Downloads.
            (code, byte[] got) = ftp.Download("RETR hello.txt");
            Check("RETR gives the file", code == 226 && Encoding.UTF8.GetString(got) == Hello);
            (code, got) = ftp.Download("RETR big.bin", extended: true);
            Check("a larger file comes back byte for byte", code == 226 && got.AsSpan().SequenceEqual(big));
            Equal("REST is accepted", "350", ftp.Code("REST 7").ToString());
            (code, got) = ftp.Download("RETR hello.txt");
            Check("RETR after REST starts part way in", code == 226 && Encoding.UTF8.GetString(got) == Hello.Substring(7), Encoding.UTF8.GetString(got));
            (code, got) = ftp.Download("RETR hello.txt");
            Check("and REST only counts once", code == 226 && got.Length == Hello.Length);
            (code, got) = ftp.Download("RETR " + OddName);
            Check("a file with an odd name is fetched", code == 226 && Encoding.UTF8.GetString(got) == "odd");
            (code, got) = ftp.Download("RETR /sub/inner.txt");
            Check("a file in a folder is fetched", code == 226 && Encoding.UTF8.GetString(got) == "inner");
            Equal("a missing file", "550", ftp.Download("RETR nothing.txt").Code.ToString());
            Equal("a folder is not a file", "550", ftp.Download("RETR sub").Code.ToString());
            ftp.Code("REST 999999");
            Equal("starting past the end is refused", "550", ftp.Download("RETR hello.txt").Code.ToString());

            using (new FileStream(Path.Combine(share, "hello.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                (code, got) = ftp.Download("RETR hello.txt");
                Check("a file open in another program can still be fetched", code == 226 && got.Length == Hello.Length);
            }

            // Uploads.
            byte[] upload = RandomNumberGenerator.GetBytes(250_000);
            Equal("STOR takes a file", "226", ftp.Upload("STOR up.bin", upload).ToString());
            Check("and it is there byte for byte", File.ReadAllBytes(Path.Combine(share, "up.bin")).AsSpan().SequenceEqual(upload));
            Check("with no spare file left behind", NoTempFiles(share));

            byte[] second = RandomNumberGenerator.GetBytes(1000);
            Equal("STOR over a file", "226", ftp.Upload("STOR up.bin", second).ToString());
            Check("replaces it", File.ReadAllBytes(Path.Combine(share, "up.bin")).AsSpan().SequenceEqual(second));

            Equal("an upload cut off part way is reported", "426", ftp.Upload("STOR up.bin", upload, cutAfter: 50_000).ToString());
            Check("and the file that was there is untouched", File.ReadAllBytes(Path.Combine(share, "up.bin")).AsSpan().SequenceEqual(second));
            Check("with nothing left behind", NoTempFiles(share));
            Equal("a new upload cut off is reported too", "426", ftp.Upload("STOR never.bin", upload, cutAfter: 10_000).ToString());
            Check("and leaves no file at all", !File.Exists(Path.Combine(share, "never.bin")) && NoTempFiles(share));
            Equal("the connection still works afterwards", "200", ftp.Code("NOOP").ToString());

            Equal("APPE adds to a file", "226", ftp.Upload("APPE up.bin", new byte[] { 1, 2, 3 }).ToString());
            byte[] appended = File.ReadAllBytes(Path.Combine(share, "up.bin"));
            Check("at its end", appended.Length == 1003 && appended[1000] == 1 && appended[1002] == 3 && appended.AsSpan(0, 1000).SequenceEqual(second));
            Equal("APPE makes a file that is not there", "226", ftp.Upload("APPE fresh.txt", Encoding.UTF8.GetBytes("abc")).ToString());
            Equal("with what was sent", "abc", File.ReadAllText(Path.Combine(share, "fresh.txt")));

            ftp.Code("REST 2");
            Equal("STOR after REST carries on", "226", ftp.Upload("STOR fresh.txt", Encoding.UTF8.GetBytes("XYZ")).ToString());
            Equal("from that byte", "abXYZ", File.ReadAllText(Path.Combine(share, "fresh.txt")));

            Equal("STOR with an odd name", "226", ftp.Upload("STOR sub/caf\u00E9 \u00FCber.txt", Encoding.UTF8.GetBytes("ok")).ToString());
            Equal("lands under that name", "ok", File.ReadAllText(Path.Combine(share, "sub", "caf\u00E9 \u00FCber.txt")));
            Equal("STOR into a missing folder", "550", ftp.Upload("STOR nowhere/x.txt", second).ToString());
            Equal("STOR over a folder", "550", ftp.Upload("STOR sub", second).ToString());

            using (new FileStream(Path.Combine(share, "fresh.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Equal("replacing a file another program holds says it is in use", "450", ftp.Upload("STOR fresh.txt", second).ToString());
            }
            Check("and leaves nothing behind", NoTempFiles(share) && File.ReadAllText(Path.Combine(share, "fresh.txt")) == "abXYZ");

            // Renaming, deleting, folders.
            Equal("RNFR finds the file", "350", ftp.Code("RNFR fresh.txt").ToString());
            Equal("RNTO renames it", "250", ftp.Code("RNTO new name \u00E9.txt").ToString());
            Check("on disk too", File.Exists(Path.Combine(share, "new name \u00E9.txt")) && !File.Exists(Path.Combine(share, "fresh.txt")));
            Equal("RNTO alone is out of order", "503", ftp.Code("RNTO other.txt").ToString());
            Equal("RNFR of a missing file", "550", ftp.Code("RNFR fresh.txt").ToString());
            ftp.Code("RNFR new name \u00E9.txt");
            Equal("a rename can move into a folder", "250", ftp.Code("RNTO sub/moved.txt").ToString());
            Check("and did", File.Exists(Path.Combine(share, "sub", "moved.txt")));
            ftp.Code("RNFR sub/moved.txt");
            Equal("a rename onto a file replaces it", "250", ftp.Code("RNTO up.bin").ToString());
            Equal("with the renamed one", "abXYZ", File.ReadAllText(Path.Combine(share, "up.bin")));

            Equal("DELE deletes", "250", ftp.Code("DELE up.bin").ToString());
            Check("the file is gone", !File.Exists(Path.Combine(share, "up.bin")));
            Equal("DELE of a missing file", "550", ftp.Code("DELE up.bin").ToString());
            Equal("DELE is not for folders", "550", ftp.Code("DELE sub").ToString());

            (code, text) = ftp.Cmd("MKD new folder");
            Check("MKD makes a folder", code == 257 && text.StartsWith("257 \"/new folder\" ") && Directory.Exists(Path.Combine(share, "new folder")), text);
            Equal("MKD twice", "550", ftp.Code("MKD new folder").ToString());
            Equal("MKD under a missing folder", "550", ftp.Code("MKD a/b/c").ToString());
            Equal("CWD goes in", "250", ftp.Code("CWD new folder").ToString());
            (code, text) = ftp.Cmd("PWD");
            Check("PWD says where", text.StartsWith("257 \"/new folder\" "), text);
            Equal("a relative upload lands there", "226", ftp.Upload("STOR in.txt", Encoding.UTF8.GetBytes("in")).ToString());
            Check("in the folder", File.Exists(Path.Combine(share, "new folder", "in.txt")));
            (code, got) = ftp.Download("RETR ../hello.txt");
            Check(".. from inside a folder reaches its parent, still in the share", code == 226 && got.Length == Hello.Length);
            Equal("CDUP goes back", "250", ftp.Code("CDUP").ToString());
            Check("to the top", ftp.Cmd("PWD").Text.StartsWith("257 \"/\" "));
            Equal("CWD to a file is refused", "550", ftp.Code("CWD hello.txt").ToString());
            Equal("CWD to nowhere is refused", "550", ftp.Code("CWD nowhere").ToString());
            (code, text) = ftp.Cmd("RMD new folder");
            Check("RMD will not take a folder with files in it", code == 550 && text.Contains("not empty"), text);
            ftp.Code("DELE new folder/in.txt");
            Equal("RMD removes an empty one", "250", ftp.Code("RMD new folder").ToString());
            Check("it is gone", !Directory.Exists(Path.Combine(share, "new folder")));
            Equal("RMD of the shared folder itself is refused", "550", ftp.Code("RMD /").ToString());
            ftp.Code("MKD old");
            ftp.Code("RNFR old");
            Equal("a folder can be renamed", "250", ftp.Code("RNTO renamed").ToString());
            Check("and was", Directory.Exists(Path.Combine(share, "renamed")));
            ftp.Code("RMD renamed");

            File.Delete(Path.Combine(share, "sub", "caf\u00E9 \u00FCber.txt"));
            Check("the secret beside the share was never touched", File.ReadAllText(Path.Combine(home, "secret.txt")) == "secret");
        }

        private static void FtpEscapeTests(string home, string share)
        {
            using FtpServer server = StartFtp(share);
            using var ftp = new Ftp(server.Port);
            ftp.Login();

            string[] attempts =
            {
                "RETR ../secret.txt", "RETR /../secret.txt", "RETR sub/../../secret.txt", "RETR ..\\secret.txt", "RETR sub\\inner.txt",
                "RETR /sub/..\\..\\secret.txt", "RETR C:/Windows/win.ini", "RETR C:\\Windows\\win.ini", "RETR /C:/Windows/win.ini",
                "RETR hello.txt:stream", "RETR hello.txt::$DATA", "RETR CON", "RETR nul", "RETR hello.txt.", "RETR hello.txt ",
                "RETR %2e%2e/secret.txt",
                "CWD ..", "CWD /..", "CWD sub/../..", "CWD C:", "CWD C:\\", "CWD \\",
                "STOR ../evil.txt", "STOR nul.txt", "STOR com1", "STOR evil.txt:hidden", "STOR evil.txt.", "STOR ..\\evil.txt", "STOR C:\\evil.txt",
                "APPE ../secret.txt", "MKD ../evil", "MKD ..\\evil", "MKD aux", "RMD ..", "RMD ../share", "DELE ../secret.txt",
                "RNFR ../secret.txt", "SIZE ../secret.txt", "MDTM ../secret.txt", "MLST ../secret.txt",
                "LIST ..", "LIST ../", "LIST C:\\", "NLST ..", "MLSD ..", "MLSD C:/",
            };
            foreach (string attempt in attempts)
            {
                Equal("refused: " + attempt, "550", ftp.Code(attempt).ToString());
            }

            ftp.Code("RNFR hello.txt");
            Equal("refused: a rename out of the share", "550", ftp.Code("RNTO ../stolen.txt").ToString());
            ftp.Code("RNFR hello.txt");
            Equal("refused: a rename to a drive path", "550", ftp.Code("RNTO C:\\stolen.txt").ToString());
            ftp.Code("RNFR hello.txt");
            Equal("refused: a rename onto a stream", "550", ftp.Code("RNTO sub/inner.txt:x").ToString());
            Check("hello.txt stayed where it was", File.Exists(Path.Combine(share, "hello.txt")));

            Check("still at the top after all that", ftp.Cmd("PWD").Text.StartsWith("257 \"/\" "));
            Check("the secret is as it was", File.ReadAllText(Path.Combine(home, "secret.txt")) == "secret");
            Check("nothing new appeared outside the share",
                Directory.GetFileSystemEntries(home).Select(Path.GetFileName).OrderBy(n => n).SequenceEqual(new[] { "secret.txt", "share" }),
                string.Join(", ", Directory.GetFileSystemEntries(home)));
            Check("and no stream was added to a file", new FileInfo(Path.Combine(share, "hello.txt")).Length == Hello.Length);

            (int code, byte[] got) = ftp.Download("RETR sub/../hello.txt");
            Check(".. that stays inside the share is just a path", code == 226 && got.Length == Hello.Length);
        }

        private static void FtpDataConnectionTests(string share)
        {
            using FtpServer server = StartFtp(share);
            server.PassiveWaitMs = 700;
            using var ftp = new Ftp(server.Port);
            ftp.Login();

            (int code, string text) = ftp.Cmd("LIST");
            Check("a transfer with no PASV first is refused", code == 425, text);

            // Nobody connects.
            int port = ftp.PassivePort();
            var watch = Stopwatch.StartNew();
            Equal("with no data connection the transfer still starts", "150", ftp.Code("LIST").ToString());
            (code, text) = ftp.Read();
            Check("then gives up with a sentence", code == 425 && text.Contains("in time"), text);
            Check("after the wait and no longer", watch.ElapsedMilliseconds >= 500 && watch.ElapsedMilliseconds < 5000, watch.ElapsedMilliseconds + " ms");
            Check("the passive port is closed again", !CanConnect(port));
            Equal("and the connection carries on", "200", ftp.Code("NOOP").ToString());
            Check("the next transfer works", ftp.List("LIST").Code == 226);

            // A port asked for and never used.
            port = ftp.PassivePort(extended: true);
            Check("an unused passive port is open at first", CanConnect(port));
            port = ftp.PassivePort();
            Thread.Sleep(1200);
            Check("and closes by itself", !CanConnect(port));

            // One connection only.
            port = ftp.PassivePort();
            using (TcpClient first = Ftp.ConnectData(port))
            {
                Equal("the transfer starts", "150", ftp.Code("NLST").ToString());
                var all = new MemoryStream();
                first.GetStream().CopyTo(all);
                Equal("and ends", "226", ftp.Read().Code.ToString());
                Check("and its port takes no second caller", !CanConnect(port));
            }

            // Somebody else's PC connects to the port.
            port = ftp.PassivePort();
            try
            {
                using var stranger = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                stranger.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
                stranger.Connect(IPAddress.Loopback, port);
                stranger.ReceiveTimeout = 5000;
                Equal("with a stranger on the data port the transfer starts", "150", ftp.Code("RETR hello.txt").ToString());

                int got;
                try { got = stranger.Receive(new byte[100]); }
                catch (SocketException) { got = 0; }
                Check("but the stranger is sent nothing", got == 0, got + " bytes");
                Equal("and the transfer is given up", "425", ftp.Read().Code.ToString());
            }
            catch (SocketException ex)
            {
                Console.WriteLine("  NOTE  could not call from a second address (" + ex.SocketErrorCode + "), so that was not tested");
                ftp.Code("NOOP");
            }
        }

        private static void FtpReadOnlyTests(string share)
        {
            using FtpServer server = StartFtp(share, readOnly: true);
            using var ftp = new Ftp(server.Port);
            ftp.Login();

            foreach (string write in new[] { "STOR new.txt", "STOR hello.txt", "APPE hello.txt", "DELE hello.txt", "MKD made", "RMD sub", "RNFR hello.txt", "RNTO other.txt" })
            {
                (int code, string text) = ftp.Cmd(write);
                Check("read-only refuses " + write, code == 550 && text.Contains("reading only"), text);
            }
            ftp.Code("REST 3");
            Equal("and STOR after REST", "550", ftp.Code("STOR hello.txt").ToString());

            (int listCode, string list) = ftp.List("LIST");
            Check("read-only still lists", listCode == 226 && list.Contains("-r--r--r--") && list.Contains(" hello.txt"), list);
            (int getCode, byte[] got) = ftp.Download("RETR hello.txt");
            Check("and still sends files", getCode == 226 && Encoding.UTF8.GetString(got) == Hello);
            Check("nothing changed", File.ReadAllText(Path.Combine(share, "hello.txt")) == Hello && !File.Exists(Path.Combine(share, "new.txt"))
                && !Directory.Exists(Path.Combine(share, "made")) && Directory.Exists(Path.Combine(share, "sub")));
        }

        private static void FtpThrottleTests(string share)
        {
            using FtpServer server = StartFtp(share);

            int wrong = 0, last = 0;
            for (int connection = 0; connection < 4 && wrong < 10; connection++)
            {
                using var ftp = new Ftp(server.Port);
                ftp.Read();
                for (int attempt = 0; attempt < 3 && wrong < 10; attempt++)
                {
                    ftp.Code("USER " + User);
                    last = ftp.Code("PASS guess" + wrong);
                    wrong++;
                }
            }
            Equal("the tenth wrong password is told it is shut out", "421", last.ToString());

            using (var ftp = new Ftp(server.Port))
            {
                (int code, string text) = ftp.Read();
                Check("after ten wrong passwords the address is turned away at the door", code == 421 && text.Contains("Too many wrong passwords"), text);
                Equal("and hung up on", "0", ftp.Code("USER " + User).ToString());
            }
            Check("the throttle has the address", server.Throttle.IsBlocked(IPAddress.Loopback));
        }

        private static void FtpLimitTests(string share)
        {
            using FtpServer server = StartFtp(share);

            using (var ftp = new Ftp(server.Port))
            {
                ftp.Login();
                (int code, string _) = ftp.Cmd("STOR " + new string('a', 9000));
                Equal("a line over 8 KB is refused", "500", code.ToString());
                Equal("and the connection ended", "0", ftp.Code("NOOP").ToString());
            }

            using (var ftp = new Ftp(server.Port))
            {
                ftp.Login();
                ftp.Stream.Write(Encoding.ASCII.GetBytes("NOOP\r\nPWD\r\nNOOP\r\n"));
                Check("commands sent in one go are answered in turn",
                    ftp.Read().Code == 200 && ftp.Read().Code == 257 && ftp.Read().Code == 200);
                ftp.Stream.Write(new byte[] { (byte)'S', (byte)'I', (byte)'Z', (byte)'E', (byte)' ', 0xFF, 0xFE, 13, 10 });
                Equal("bytes that are not UTF-8 do no harm", "550", ftp.Read().Code.ToString());
            }

            Thread.Sleep(300);      // let the server notice the two above have gone
            var crowd = new List<Ftp>();
            try
            {
                bool allGreeted = true;
                for (int i = 0; i < 64; i++)
                {
                    var one = new Ftp(server.Port);
                    crowd.Add(one);
                    if (one.Read().Code != 220) allGreeted = false;
                }
                Check("sixty-four callers at once are all greeted", allGreeted);

                using (var extra = new Ftp(server.Port))
                {
                    (int code, string text) = extra.Read();
                    Check("the sixty-fifth is asked to come back", code == 421 && text.Contains("Too many connections"), text);
                }

                crowd[0].Dispose();
                crowd.RemoveAt(0);
                bool roomAgain = false;
                for (int attempt = 0; attempt < 20 && !roomAgain; attempt++)
                {
                    Thread.Sleep(50);
                    using var next = new Ftp(server.Port);
                    roomAgain = next.Read().Code == 220;
                }
                Check("and gets in once somebody leaves", roomAgain);
            }
            finally
            {
                foreach (Ftp one in crowd) one.Dispose();
            }
        }

        private static X509Certificate2 SelfSigned()
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
            using X509Certificate2 made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            // Windows' TLS only uses a private key that lives in a key store,
            // which is what loading it back from a PFX gives it.
            return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx, "x"), "x");
        }

        private static void FtpTlsTests(string share, byte[] big)
        {
            using X509Certificate2 certificate = SelfSigned();
            using FtpServer server = StartFtp(share, tls: certificate);

            using (var plain = new Ftp(server.Port))
            {
                Check("plain FTP still works beside TLS", plain.Login() && plain.List("LIST").Code == 226);
                (int code, string text) = plain.Cmd("FEAT");
                Check("FEAT offers AUTH TLS, PBSZ and PROT", text.Contains(" AUTH TLS\n") && text.Contains(" PBSZ\n") && text.Contains(" PROT\n"), text);
                Equal("PBSZ before AUTH is out of order", "503", plain.Code("PBSZ 0").ToString());
                Equal("so is PROT", "503", plain.Code("PROT P").ToString());
                Equal("AUTH with something else is refused", "504", plain.Code("AUTH KERBEROS").ToString());
            }

            using var ftp = new Ftp(server.Port);
            ftp.Read();
            ftp.Code("USER " + User);
            Equal("AUTH TLS is accepted", "234", ftp.Code("AUTH TLS").ToString());
            ftp.StartTls();
            var secure = (SslStream)ftp.Stream;
            Check("the control connection is TLS 1.2 or 1.3", secure.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13, secure.SslProtocol.ToString());
            Console.WriteLine("  NOTE  FTP over TLS negotiated " + secure.SslProtocol);
            Equal("a name given before TLS does not carry over", "503", ftp.Code("PASS " + Password).ToString());
            Check("signing in inside TLS", ftp.SignIn());
            Equal("AUTH twice is refused", "503", ftp.Code("AUTH TLS").ToString());
            Equal("PBSZ 0", "200", ftp.Code("PBSZ 0").ToString());
            Equal("PROT P", "200", ftp.Code("PROT P").ToString());
            Equal("PROT E is not known", "504", ftp.Code("PROT E").ToString());
            ftp.ProtectData = true;

            (int listCode, string list) = ftp.List("LIST");
            Check("a listing inside TLS", listCode == 226 && list.Contains(" hello.txt") && list.Contains(" " + OddName), list);
            (int getCode, byte[] got) = ftp.Download("RETR big.bin", extended: true);
            Check("a download inside TLS, byte for byte", getCode == 226 && got.AsSpan().SequenceEqual(big));
            byte[] upload = RandomNumberGenerator.GetBytes(200_000);
            Equal("an upload inside TLS", "226", ftp.Upload("STOR tls.bin", upload).ToString());
            Check("arrives byte for byte", File.ReadAllBytes(Path.Combine(share, "tls.bin")).AsSpan().SequenceEqual(upload));
            // Upload programs write and close without ever reading. Anything the
            // server had sent them unasked would then turn the close into a reset.
            int arrived = 0;
            for (int i = 0; i < 25; i++)
            {
                if (ftp.Upload("STOR tls-many.bin", upload.AsSpan(0, upload.Length - i).ToArray()) == 226) arrived++;
            }
            Equal("twenty-five uploads inside TLS in a row all arrive", "25", arrived.ToString());
            File.Delete(Path.Combine(share, "tls-many.bin"));
            Equal("an upload inside TLS cut off part way is reported", "426", ftp.Upload("STOR tls.bin", big, cutAfter: 40_000).ToString());
            Check("and changes nothing", File.ReadAllBytes(Path.Combine(share, "tls.bin")).AsSpan().SequenceEqual(upload) && NoTempFiles(share));

            // A client that asked for private data but then talks in the clear.
            using (TcpClient data = ftp.OpenData())
            {
                Equal("with PROT P the transfer starts", "150", ftp.Code("RETR hello.txt").ToString());
                data.GetStream().Write(Encoding.ASCII.GetBytes("this is not a TLS hello, not at all\r\n"));
                var all = new MemoryStream();
                try { data.GetStream().CopyTo(all); } catch (IOException) { }
                Check("but a data connection that is not TLS gets no file", !Encoding.ASCII.GetString(all.ToArray()).Contains("Hello"));
                Equal("and is reported", "425", ftp.Read().Code.ToString());
            }

            Equal("PROT C", "200", ftp.Code("PROT C").ToString());
            ftp.ProtectData = false;
            (getCode, got) = ftp.Download("RETR hello.txt");
            Check("after PROT C data goes in the clear again", getCode == 226 && Encoding.UTF8.GetString(got) == Hello);
            File.Delete(Path.Combine(share, "tls.bin"));
        }

        private static void FtpStopTests(string share)
        {
            using (FtpServer first = StartFtp(share))
            {
                bool refused = false;
                string message = "";
                try
                {
                    using var second = new FtpServer(share, first.Port, User, Password, false);
                    second.Start();
                }
                catch (InvalidOperationException ex) { refused = true; message = ex.Message; }
                Check("a port already in use is said in a sentence", refused && message.Contains("ort " + first.Port) && message.EndsWith("."), message);
            }

            bool missing = false;
            try
            {
                using var nowhere = new FtpServer(Path.Combine(share, "not-here"), 0, User, Password, false);
                nowhere.Start();
            }
            catch (InvalidOperationException ex) { missing = ex.Message.Contains("is not there"); }
            Check("a folder that is not there is said in a sentence", missing);

            FtpServer server = StartFtp(share);
            int port = server.Port;
            using var idle = new Ftp(port);
            idle.Login();
            using var midTransfer = new Ftp(port);
            midTransfer.Login();
            using TcpClient data = midTransfer.OpenData();
            midTransfer.Code("STOR stopping.bin");
            data.GetStream().Write(new byte[1000]);
            using var fresh = new Ftp(port);
            fresh.Read();
            Thread.Sleep(100);

            var watch = Stopwatch.StartNew();
            server.Dispose();
            Check("Dispose comes back quickly", watch.ElapsedMilliseconds < 2000, watch.ElapsedMilliseconds + " ms");

            idle.Tcp.ReceiveTimeout = 3000;
            midTransfer.Tcp.ReceiveTimeout = 3000;
            fresh.Tcp.ReceiveTimeout = 3000;
            watch.Restart();
            Check("a signed-in connection is ended", idle.Read().Code == 0);
            Check("so is one in the middle of an upload", midTransfer.Read().Code == 0);
            Check("and one that had not signed in", fresh.Read().Code == 0);
            Check("all without waiting for a timeout", watch.ElapsedMilliseconds < 2500, watch.ElapsedMilliseconds + " ms");

            bool dataEnded;
            try
            {
                data.ReceiveTimeout = 3000;
                dataEnded = data.GetStream().Read(new byte[10], 0, 10) == 0;
            }
            catch (IOException) { dataEnded = true; }
            Check("the data connection is ended too", dataEnded);
            Check("nobody new can connect", !CanConnect(port));

            bool settled = false;
            for (int attempt = 0; attempt < 30 && !settled; attempt++)
            {
                settled = NoTempFiles(share) && !File.Exists(Path.Combine(share, "stopping.bin"));
                if (!settled) Thread.Sleep(100);
            }
            Check("the upload it interrupted left nothing behind", settled);
            server.Dispose();   // twice is harmless

            bool restartRefused = false;
            try { server.Start(); } catch (InvalidOperationException) { restartRefused = true; }
            Check("a stopped server does not start again by accident", restartRefused);
        }

        // ---- SFTP ----------------------------------------------------------------

        private const string SftpPassword = "Sftp-pass-123";

        private static readonly string OpenSshFolder = Path.Combine(Environment.SystemDirectory, "OpenSSH");

        /// <summary>
        /// Runs Windows' own sftp.exe against a server with a list of commands.
        /// The password reaches it the only way a program can hand ssh one: a
        /// little script that ssh runs to "ask" for it.
        /// </summary>
        private static (int Exit, string Output) RunSftp(string home, string tag, int port, string password, string commands, params string[] extra)
        {
            string askpass = Path.Combine(home, "askpass-" + tag + ".cmd");
            File.WriteAllText(askpass, "@echo off\r\necho " + password + "\r\n", Encoding.ASCII);
            string batch = Path.Combine(home, "batch-" + tag + ".txt");
            File.WriteAllText(batch, commands.Replace("\r\n", "\n"), Encoding.ASCII);
            string config = Path.Combine(home, "ssh-config-" + tag);
            File.WriteAllText(config, "");
            string local = Path.Combine(home, "local");
            Directory.CreateDirectory(local);

            var start = new ProcessStartInfo(Path.Combine(OpenSshFolder, "sftp.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = local,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            // BatchMode=no has to come before -b, which would otherwise switch password prompts off.
            foreach (string argument in new[]
            {
                "-v", "-F", config, "-o", "BatchMode=no", "-b", batch, "-P", port.ToString(),
                "-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=NUL", "-o", "GlobalKnownHostsFile=NUL",
                "-o", "PreferredAuthentications=password", "-o", "PubkeyAuthentication=no",
                "-o", "NumberOfPasswordPrompts=1", "-o", "ConnectTimeout=10",
            }) start.ArgumentList.Add(argument);
            foreach (string argument in extra) start.ArgumentList.Add(argument);
            start.ArgumentList.Add(User + "@127.0.0.1");
            start.Environment["SSH_ASKPASS"] = askpass;
            start.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            start.Environment["DISPLAY"] = "none";

            using Process process = Process.Start(start)!;
            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(25_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-999, "sftp.exe did not finish");
            }
            process.WaitForExit();
            return (process.ExitCode, output.Result + errors.Result);
        }

        private static string Tail(string text) => text.Length <= 1500 ? text : text.Substring(text.Length - 1500);

        private static int Count(string text, string part)
        {
            int count = 0;
            for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
            return count;
        }

        private static string ReadToEnd(TcpClient client)
        {
            var all = new MemoryStream();
            try { client.GetStream().CopyTo(all); } catch (IOException) { }
            return Encoding.Latin1.GetString(all.ToArray());
        }

        private static string ReadOrEmpty(string path) => File.Exists(path) ? File.ReadAllText(path) : "(no file)";

        private static void SftpTests(string home, string share, byte[] big)
        {
            byte[] hostKey = SftpServer.NewHostKey();
            string fingerprint = SftpServer.Fingerprint(hostKey);
            Check("a host key's fingerprint looks like OpenSSH's", fingerprint.StartsWith("SHA256:") && fingerprint.Length == 50 && !fingerprint.EndsWith("="), fingerprint);
            Equal("and is the same every time", fingerprint, SftpServer.Fingerprint(hostKey));
            Check("another key has another", SftpServer.Fingerprint(SftpServer.NewHostKey()) != fingerprint);

            bool badKey = false;
            try
            {
                using var broken = new SftpServer(share, 0, User, SftpPassword, false, new byte[] { 1, 2, 3 });
                broken.Start();
            }
            catch (InvalidOperationException ex) { badKey = ex.Message.EndsWith("."); }
            Check("a key that cannot be read is said in a sentence", badKey);

            using SftpServer server = Track(new SftpServer(share, 0, User, SftpPassword, false, hostKey));
            server.Start();

            bool inUse = false;
            string message = "";
            try
            {
                using var second = new SftpServer(share, server.Port, User, SftpPassword, false, hostKey);
                second.Start();
            }
            catch (InvalidOperationException ex) { inUse = true; message = ex.Message; }
            Check("SFTP says a port is in use in a sentence", inUse && message.Contains("ort " + server.Port) && message.EndsWith("."), message);

            // Without a real client: what any caller sees first, and how nonsense is taken.
            using (var raw = new TcpClient())
            {
                raw.Connect(IPAddress.Loopback, server.Port);
                raw.ReceiveTimeout = 5000;
                var line = new StringBuilder();
                for (int b = raw.GetStream().ReadByte(); b >= 0 && b != '\n'; b = raw.GetStream().ReadByte()) line.Append((char)b);
                Check("SFTP announces itself as SSH-2", line.ToString().StartsWith("SSH-2.0-"), line.ToString());
                raw.GetStream().Write(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n"));
                var watch = Stopwatch.StartNew();
                ReadToEnd(raw);
                Check("a caller that is not SSH is hung up on", watch.ElapsedMilliseconds < 3000, watch.ElapsedMilliseconds + " ms");
            }
            using (var raw = new TcpClient())
            {
                raw.Connect(IPAddress.Loopback, server.Port);
                raw.ReceiveTimeout = 5000;
                byte[] hello = new byte[40_005];
                Encoding.ASCII.GetBytes("SSH-2.0-test\r\n").CopyTo(hello, 0);
                hello[14] = 0xFF; hello[15] = 0xFF; hello[16] = 0xFF; hello[17] = 0xFF;    // a packet of four billion bytes
                try { raw.GetStream().Write(hello); } catch (IOException) { }
                var watch = Stopwatch.StartNew();
                ReadToEnd(raw);
                Check("a packet of an impossible size ends the connection at once", watch.ElapsedMilliseconds < 3000, watch.ElapsedMilliseconds + " ms");
            }

            if (!File.Exists(Path.Combine(OpenSshFolder, "sftp.exe")))
            {
                Console.WriteLine("  NOTE  " + Path.Combine(OpenSshFolder, "sftp.exe") + " is not here, so SFTP was not tested with a real client");
            }
            else
            {
                SftpClientTests(home, share, big, server, fingerprint, hostKey);
            }

            // Shut out by the throttle: told so in the clear, before any key exchange.
            for (int i = 0; i < SignInThrottle.MaxFailures; i++) server.Throttle.Failed(IPAddress.Loopback);
            using (var raw = new TcpClient())
            {
                raw.Connect(IPAddress.Loopback, server.Port);
                raw.ReceiveTimeout = 5000;
                raw.GetStream().Write(Encoding.ASCII.GetBytes("SSH-2.0-test\r\n"));
                string said = ReadToEnd(raw);
                Check("an address shut out for wrong passwords is told so and hung up on", said.Contains("Too many wrong passwords"), said);
            }

            using var idle = new TcpClient();
            idle.Connect(IPAddress.Loopback, server.Port);
            idle.ReceiveTimeout = 3000;
            Thread.Sleep(100);
            int port = server.Port;
            var stop = Stopwatch.StartNew();
            server.Dispose();
            Check("SFTP Dispose comes back quickly", stop.ElapsedMilliseconds < 2000, stop.ElapsedMilliseconds + " ms");
            stop.Restart();
            ReadToEnd(idle);
            Check("and ends an open connection", stop.ElapsedMilliseconds < 2500, stop.ElapsedMilliseconds + " ms");
            Check("and takes no new ones", !CanConnect(port));
        }

        private static void SftpClientTests(string home, string share, byte[] big, SftpServer server, string fingerprint, byte[] hostKey)
        {
            string local = Path.Combine(home, "local");
            Directory.CreateDirectory(local);
            byte[] upload = RandomNumberGenerator.GetBytes(3_000_000);
            File.WriteAllBytes(Path.Combine(local, "big-up.bin"), upload);
            File.WriteAllText(Path.Combine(local, "small.txt"), "small");
            File.WriteAllText(Path.Combine(share, "name with space.txt"), "spaced");
            DateTime stamp = new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(local, "small.txt"), stamp);

            using SftpServer readOnly = Track(new SftpServer(share, 0, User, SftpPassword, true, hostKey));
            readOnly.Start();

            // The side runs go at the same time as the main one; each is a fresh sftp.exe.
            var wrong = Task.Run(() => RunSftp(home, "wrong", server.Port, "not-the-password", "ls\n"));
            var algorithms = new[]
            {
                ("aes256-ctr", "hmac-sha2-512"), ("aes256-ctr", "hmac-sha2-512-etm@openssh.com"),
                ("aes128-ctr", "hmac-sha2-256"), ("aes128-ctr", "hmac-sha2-256-etm@openssh.com"),
            };
            var algorithmRuns = new Task<(int Exit, string Output)>[algorithms.Length];
            for (int i = 0; i < algorithms.Length; i++)
            {
                int n = i;
                algorithmRuns[n] = Task.Run(() => RunSftp(home, "alg" + n, server.Port, SftpPassword,
                    "get big.bin alg" + n + ".bin\nput small.txt alg" + n + ".txt\n", "-c", algorithms[n].Item1, "-o", "MACs=" + algorithms[n].Item2));
            }
            var readOnlyRun = Task.Run(() => RunSftp(home, "ro", readOnly.Port, SftpPassword,
                "ls -l\nget hello.txt ro-hello.txt\n-put small.txt ro-put.txt\n-rm hello.txt\n-mkdir ro-made\n-rmdir sub\n-rename hello.txt ro-renamed.txt\n"));

            // RekeyLimit makes the client ask for fresh keys several times inside the big transfers.
            (int exit, string output) = RunSftp(home, "main", server.Port, SftpPassword, string.Join("\n", new[]
            {
                "pwd",
                "ls -l",
                "get hello.txt",
                "get \"name with space.txt\" spaced.txt",
                "put big-up.bin",
                "get big-up.bin big-back.bin",
                "rename big-up.bin renamed.bin",
                "mkdir made",
                "put -p small.txt made/small.txt",
                "ls made",
                "get made/small.txt small-back.txt",
                "rm made/small.txt",
                "rmdir made",
                "mkdir \"dir with space\"",
                "rmdir \"dir with space\"",
                "cd sub",
                "pwd",
                "get inner.txt",
                "cd ..",
                "cd ..",
                "pwd",
                "put -p small.txt kept-time.txt",
                "-get ../secret.txt stolen1.txt",
                "-get /../secret.txt stolen2.txt",
                "-get sub/../../secret.txt stolen3.txt",
                "-get C:/Windows/win.ini stolen4.txt",
                "-get hello.txt:stream stolen5.txt",
                "-put small.txt ../evil.txt",
                "-put small.txt nul",
                "-put small.txt evil.txt:stream",
                "-mkdir ../evil-folder",
                "-rename hello.txt ../stolen.txt",
                "-ls ..",
                "-symlink hello.txt link.txt",
                "put small.txt last.txt",
                "",
            }), "-o", "RekeyLimit=1M");

            Check("OpenSSH's sftp ran the whole batch", exit == 0, "exit " + exit + "\n" + Tail(output));
            if (exit == -999) return;

            Check("the client was shown this server's fingerprint", output.Contains("Server host key: ecdsa-sha2-nistp256 " + fingerprint), fingerprint);
            Check("signed in with the password", output.Contains("Authenticated to 127.0.0.1"));
            Check("the keys were changed mid-transfer when the client asked", Count(output, "SSH2_MSG_KEXINIT sent") >= 3, Count(output, "SSH2_MSG_KEXINIT sent") + " key exchanges");
            Check("the shared folder is /", output.Contains("Remote working directory: /\n") || output.Contains("Remote working directory: /\r"));
            Check("ls lists files and folders", output.Contains("hello.txt") && output.Contains("big.bin") && output.Contains(" sub"), Tail(output));
            Check("cd goes into a folder", output.Contains("Remote working directory: /sub"));
            Equal("get fetches a file", Hello, ReadOrEmpty(Path.Combine(local, "hello.txt")));
            Equal("a name with a space", "spaced", ReadOrEmpty(Path.Combine(local, "spaced.txt")));
            Equal("a file from a folder", "inner", ReadOrEmpty(Path.Combine(local, "inner.txt")));
            Check("put sends three megabytes byte for byte",
                File.Exists(Path.Combine(share, "renamed.bin")) && File.ReadAllBytes(Path.Combine(share, "renamed.bin")).AsSpan().SequenceEqual(upload));
            Check("and get brings them back byte for byte",
                File.Exists(Path.Combine(local, "big-back.bin")) && File.ReadAllBytes(Path.Combine(local, "big-back.bin")).AsSpan().SequenceEqual(upload));
            Check("rename moved the file", !File.Exists(Path.Combine(share, "big-up.bin")));
            Equal("a file put into a new folder came back", "small", ReadOrEmpty(Path.Combine(local, "small-back.txt")));
            Check("rm and rmdir cleared up", !Directory.Exists(Path.Combine(share, "made")) && !Directory.Exists(Path.Combine(share, "dir with space")));
            Equal("put -p keeps the file's time", stamp.ToString("u"),
                File.Exists(Path.Combine(share, "kept-time.txt")) ? File.GetLastWriteTimeUtc(Path.Combine(share, "kept-time.txt")).ToString("u") : "(no file)");
            Equal("the batch reached its last line", "small", ReadOrEmpty(Path.Combine(share, "last.txt")));

            Check("nothing outside the share was fetched", Directory.GetFiles(local, "stolen*").Length == 0, string.Join(", ", Directory.GetFiles(local, "stolen*")));
            Check("nothing was written outside the share",
                Directory.GetFileSystemEntries(home).All(e => Path.GetFileName(e) is "secret.txt" or "share" or "local" || Path.GetFileName(e).Contains('-')),
                string.Join(", ", Directory.GetFileSystemEntries(home)));
            Check("the secret is as it was", File.ReadAllText(Path.Combine(home, "secret.txt")) == "secret");
            Check("hello.txt stayed, with no stream added", File.Exists(Path.Combine(share, "hello.txt")) && new FileInfo(Path.Combine(share, "hello.txt")).Length == Hello.Length);
            Check("no device name or link was made", !File.Exists(Path.Combine(share, "link.txt")) && !Directory.EnumerateFileSystemEntries(share).Any(e => Path.GetFileName(e) == "nul"));

            (int wrongExit, string wrongOutput) = wrong.Result;
            Check("a wrong password is refused", wrongExit != 0 && wrongExit != -999 && wrongOutput.Contains("Permission denied") && !wrongOutput.Contains("Authenticated to"), "exit " + wrongExit + "\n" + Tail(wrongOutput));

            for (int i = 0; i < algorithms.Length; i++)
            {
                (int algExit, string algOutput) = algorithmRuns[i].Result;
                string wanted = "cipher: " + algorithms[i].Item1 + " MAC: " + algorithms[i].Item2;
                string got = Path.Combine(local, "alg" + i + ".bin");
                Check("works with " + algorithms[i].Item1 + " and " + algorithms[i].Item2,
                    algExit == 0 && Count(algOutput, wanted) == 2 && File.Exists(got) && File.ReadAllBytes(got).AsSpan().SequenceEqual(big)
                    && ReadOrEmpty(Path.Combine(share, "alg" + i + ".txt")) == "small",
                    "exit " + algExit + "\n" + Tail(algOutput));
                ServedFolder.DeleteQuietly(Path.Combine(share, "alg" + i + ".txt"));
            }

            (int roExit, string roOutput) = readOnlyRun.Result;
            Check("read-only: the batch ran", roExit == 0, "exit " + roExit + "\n" + Tail(roOutput));
            Check("read-only lists without write permission", roOutput.Contains("-r--r--r--") && roOutput.Contains("dr-xr-xr-x"), Tail(roOutput));
            Equal("read-only still sends files", Hello, ReadOrEmpty(Path.Combine(local, "ro-hello.txt")));
            Check("read-only refuses put, rm, mkdir, rmdir and rename",
                !File.Exists(Path.Combine(share, "ro-put.txt")) && File.Exists(Path.Combine(share, "hello.txt")) && !Directory.Exists(Path.Combine(share, "ro-made"))
                && Directory.Exists(Path.Combine(share, "sub")) && !File.Exists(Path.Combine(share, "ro-renamed.txt")));
            Check("and says permission denied each time", Count(roOutput, "Permission denied") >= 5, Count(roOutput, "Permission denied") + " refusals\n" + Tail(roOutput));

            foreach (string made in new[] { "renamed.bin", "last.txt", "kept-time.txt", "name with space.txt" }) ServedFolder.DeleteQuietly(Path.Combine(share, made));
        }
    }
}
