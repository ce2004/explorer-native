using SMBLibrary.Client;
using SMBLibrary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Net;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

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

    // ---- merged from Release700Tests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// 7.0.0: the file servers (SMB here; FTP and SFTP have their own suite in
    /// FileServerTests) and what decides which of them run. Group: release700.
    /// The SMB server is talked to over a real socket by the library's own
    /// client, on a port of the test's choosing.
    /// </summary>
    internal static class Release700Tests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;
            Console.WriteLine("7.0.0, file servers:");

            var sandbox = Path.Combine(Path.GetTempPath(), "en-700-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                Rules();
                var work = Task.Run(async () =>
                {
                    Smb(sandbox);
                    SmbWholeDrive(sandbox);
                    await WhichRun(sandbox);
                });
                if (await Task.WhenAny(work, Task.Delay(Timeout.Infinite, watchdog.Token)) != work)
                    Check("700: the file server tests finish in time", false, "still running after 90 seconds");
                else await work;
                Wiring();
            }
            finally
            {
                try { Directory.Delete(sandbox, true); } catch { }
            }
            Console.WriteLine();
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        // ---------------- what may be a server ----------------

        private static void Rules()
        {
            FileServers.Config Smb(string folder = @"D:\Music", int port = 4450, string share = "files", string user = "conner",
                string password = "long enough", bool on = true) =>
                new(FileServers.Kind.Smb, on, folder, port, share, user, password, false, "");

            Check("700 rules: a whole setup is accepted", FileServers.WhyNot(Smb()) == null, FileServers.WhyNot(Smb()));
            Check("700 rules: a drive is a folder too", FileServers.WhyNot(Smb(folder: @"G:\")) == null);
            Check("700 rules: no folder is refused, in a sentence",
                FileServers.WhyNot(Smb(folder: "")) is { } a && a.Contains("folder or drive") && a.EndsWith('.'), FileServers.WhyNot(Smb(folder: "")));
            Check("700 rules: a path that is not whole is refused", FileServers.WhyNot(Smb(folder: "Music")) != null);
            Check("700 rules: Apple Music is not a folder to share", FileServers.WhyNot(Smb(folder: "::applemusic")) != null);
            Check("700 rules: no user name is refused", FileServers.WhyNot(Smb(user: "")) != null);
            Check("700 rules: a short password is refused, and says how long",
                FileServers.WhyNot(Smb(password: "short")) is { } p && p.Contains(FileServers.ShortestPassword.ToString()),
                FileServers.WhyNot(Smb(password: "short")));
            Check("700 rules: no password at all is refused", FileServers.WhyNot(Smb(password: "")) != null);
            Check("700 rules: port 0 and 70000 are refused",
                FileServers.WhyNot(Smb(port: 0)) != null && FileServers.WhyNot(Smb(port: 70000)) != null);
            Check("700 rules: a share name with a slash is refused", FileServers.WhyNot(Smb(share: @"a\b")) != null);

            var ftp = new FileServers.Config(FileServers.Kind.Ftp, true, @"D:\Music", 4450, "", "conner", "long enough", false, "");
            Check("700 rules: two servers cannot have one port",
                FileServers.WhyNot(ftp, new[] { Smb(), ftp }) is { } clash && clash.Contains("4450") && clash.Contains("SMB"),
                FileServers.WhyNot(ftp, new[] { Smb(), ftp }));
            Check("700 rules: unless the other is off", FileServers.WhyNot(ftp, new[] { Smb(on: false), ftp }) == null);

            var s = new Settings();
            Check("700 settings: every server is off by default, on a port of its own that is not the protocol's",
                !s.SmbServerEnabled && !s.FtpServerEnabled && !s.SftpServerEnabled &&
                s.SmbServerPort == FileServers.DefaultSmbPort && s.SmbServerPort != 445 &&
                s.FtpServerPort != 21 && s.SftpServerPort != 22 &&
                new[] { s.SmbServerPort, s.FtpServerPort, s.SftpServerPort }.Distinct().Count() == 3);
            Check("700 settings: each PC keeps its own server keys",
                Settings.ThisPcOnly.Contains(nameof(Settings.SftpServerHostKey)) &&
                Settings.ThisPcOnly.Contains(nameof(Settings.FtpServerCertificate)));

            var withFtp = new Settings { FtpServerEnabled = true };
            Check("700 keys: turning FTP on makes its certificate, once",
                FileServers.EnsureKeys(withFtp) && withFtp.FtpServerCertificate.Length > 0 && !FileServers.EnsureKeys(withFtp));
            Check("700 keys: nothing is made for servers that are off",
                !FileServers.EnsureKeys(new Settings()));
            var withSftp = new Settings { SftpServerEnabled = true };
            Check("700 keys: turning SFTP on makes its key, once, and the key has a fingerprint",
                FileServers.EnsureKeys(withSftp) && withSftp.SftpServerHostKey.Length > 0 && !FileServers.EnsureKeys(withSftp) &&
                FileServers.FingerprintOf(withSftp).StartsWith("SHA256:", StringComparison.Ordinal),
                FileServers.FingerprintOf(withSftp));
            Check("700 keys: no key, no fingerprint", FileServers.FingerprintOf(new Settings()) == "");
            using (var loaded = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
                       Convert.FromBase64String(withFtp.FtpServerCertificate), null))
                Check("700 keys: the certificate has its private key and ten years", loaded.HasPrivateKey &&
                    loaded.NotAfter > DateTime.Now.AddYears(9));
        }

        // ---------------- SMB, over a real socket ----------------

        private static readonly MethodInfo? ConnectOnPort = typeof(SMB2Client).GetMethod("Connect",
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(IPAddress), typeof(SMBTransportType), typeof(int) }, null);

        /// <summary>Signs in and opens the share, or says how far it got.</summary>
        private static (SMB2Client Client, ISMBFileStore? Share, string Outcome) Open(int port, string user, string password, string share)
        {
            var client = new SMB2Client();
            if (ConnectOnPort == null || !(bool)ConnectOnPort.Invoke(client, new object[] { IPAddress.Loopback, SMBTransportType.DirectTCPTransport, port })!)
                return (client, null, "could not connect");
            var status = client.Login("", user, password);
            if (status != NTStatus.STATUS_SUCCESS) return (client, null, "login " + status);
            var store = client.TreeConnect(share, out status);
            return (client, status == NTStatus.STATUS_SUCCESS ? store : null, "share " + status);
        }

        /// <summary>Makes a junction with Windows' own mklink. False when it could not.</summary>
        private static bool Junction(string at, string target)
        {
            try
            {
                var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                using var p = Process.Start(new ProcessStartInfo(cmd, $"/c mklink /J \"{at}\" \"{target}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                });
                p?.WaitForExit(10000);
                return Directory.Exists(at) && (File.GetAttributes(at) & System.IO.FileAttributes.ReparsePoint) != 0;
            }
            catch { return false; }
        }

        private static string? ReadText(ISMBFileStore share, string path)
        {
            var status = share.CreateFile(out var handle, out _, path, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                SMBLibrary.FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            if (status != NTStatus.STATUS_SUCCESS) return null;
            share.ReadFile(out var data, handle, 0, 65536);
            share.CloseFile(handle);
            return Encoding.UTF8.GetString(data ?? Array.Empty<byte>());
        }

        private static NTStatus WriteText(ISMBFileStore share, string path, string text)
        {
            var status = share.CreateFile(out var handle, out _, path, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE,
                SMBLibrary.FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OVERWRITE_IF,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            if (status != NTStatus.STATUS_SUCCESS) return status;
            status = share.WriteFile(out _, handle, 0, Encoding.UTF8.GetBytes(text));
            share.CloseFile(handle);
            return status;
        }

        private static List<string> Names(ISMBFileStore share, string folder)
        {
            var names = new List<string>();
            var status = share.CreateFile(out var handle, out _, folder, AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
            if (status != NTStatus.STATUS_SUCCESS) return names;
            share.QueryDirectory(out var found, handle, "*", FileInformationClass.FileDirectoryInformation);
            share.CloseFile(handle);
            foreach (var entry in found ?? new())
                if (entry is FileDirectoryInformation info && info.FileName is not ("." or "..")) names.Add(info.FileName);
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        private static void Smb(string sandbox)
        {
            Check("700 smb: the library can still be started on a chosen port", SmbFileServer.StartOnPort != null);
            Check("700 smb: and its client can still be pointed at one, for these tests", ConnectOnPort != null);
            if (SmbFileServer.StartOnPort == null || ConnectOnPort == null) return;

            var folder = Path.Combine(sandbox, "shared");
            Directory.CreateDirectory(Path.Combine(folder, "Sub folder"));
            File.WriteAllText(Path.Combine(folder, "hello.txt"), "hello from the share");
            File.WriteAllText(Path.Combine(folder, "Sub folder", "ünïcode näme.txt"), "inside");

            int port = FreePort();
            using (var server = new SmbFileServer(folder, port, "files", "conner", "a long password", readOnly: false))
            {
                server.Start();

                var (bad, none, outcome) = Open(port, "conner", "wrong password", "files");
                Check("700 smb: a wrong password is refused", none == null && outcome.StartsWith("login"), outcome);
                bad.Disconnect();
                (bad, none, outcome) = Open(port, "somebody", "a long password", "files");
                Check("700 smb: and so is a user name that is not the one set", none == null, outcome);
                bad.Disconnect();

                var (client, share, said) = Open(port, "Conner", "a long password", "files");
                Check("700 smb: the right name and password open the share, whatever the name's case", share != null, said);
                if (share != null)
                {
                    Equal("700 smb: the folder is listed", "Sub folder|hello.txt", string.Join("|", Names(share, "")));
                    Equal("700 smb: a file is read", "hello from the share", ReadText(share, "hello.txt") ?? "(not opened)");
                    Equal("700 smb: a name with accents in a subfolder too", "inside",
                        ReadText(share, @"Sub folder\ünïcode näme.txt") ?? "(not opened)");
                    var wrote = WriteText(share, "new.txt", "written over SMB");
                    Check("700 smb: a file is written", wrote == NTStatus.STATUS_SUCCESS &&
                        File.ReadAllText(Path.Combine(folder, "new.txt")) == "written over SMB", wrote.ToString());
                    Check("700 smb: nothing outside the folder is reached",
                        ReadText(share, @"..\outside.txt") == null && ReadText(share, @"Sub folder\..\..\outside.txt") == null);

                    // 7.0.1, the security review. A junction inside the share
                    // that points out of it is not a way out; one that stays
                    // inside still works.
                    var secret = Path.Combine(sandbox, "secret");
                    Directory.CreateDirectory(secret);
                    File.WriteAllText(Path.Combine(secret, "secret.txt"), "not shared");
                    bool linked = Junction(Path.Combine(folder, "way out"), secret) &&
                                  Junction(Path.Combine(folder, "way round"), Path.Combine(folder, "Sub folder"));
                    if (linked)
                    {
                        Check("700 smb: a junction that leaves the share is not followed",
                            ReadText(share, @"way out\secret.txt") == null && Names(share, "way out").Count == 0,
                            ReadText(share, @"way out\secret.txt") ?? string.Join("|", Names(share, "way out")));
                        Check("700 smb: nor written through",
                            WriteText(share, @"way out\planted.txt", "x") != NTStatus.STATUS_SUCCESS &&
                            !File.Exists(Path.Combine(secret, "planted.txt")));
                        Equal("700 smb: a junction that stays inside still works", "inside",
                            ReadText(share, @"way round\ünïcode näme.txt") ?? "(not opened)");
                    }
                    else Console.WriteLine("  (junctions could not be made: the link checks were not run)");

                    Check("700 smb: a device name is not opened", ReadText(share, "NUL") == null && ReadText(share, @"Sub folder\CON.txt") == null);
                    Check("700 smb: nor a name Windows would quietly respell", ReadText(share, "hello.txt.") == null && ReadText(share, "hello.txt ") == null);
                    Check("700 smb: a file's own stream is still allowed (Windows writes one on downloaded files)",
                        WriteText(share, "new.txt:Zone.Identifier", "[ZoneTransfer]") == NTStatus.STATUS_SUCCESS);

                    var opened = share.CreateFile(out var any, out _, "hello.txt", AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                        SMBLibrary.FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN,
                        CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
                    if (opened == NTStatus.STATUS_SUCCESS)
                    {
                        // FSCTL_SET_REPARSE_POINT and friends: no control code reaches the disk.
                        var control = share.DeviceIOControl(any, 0x000900A4, new byte[16], out _, 0);
                        var acl = share.SetSecurityInformation(any, SecurityInformation.DACL_SECURITY_INFORMATION, new SecurityDescriptor());
                        share.CloseFile(any);
                        Check("700 smb: control codes are refused", control != NTStatus.STATUS_SUCCESS, control.ToString());
                        Check("700 smb: and so is changing who may open a file", acl != NTStatus.STATUS_SUCCESS, acl.ToString());
                    }
                }
                client.Disconnect();

                // Nobody gets in without the name and the password.
                foreach (var (who, word, label) in new[] { ("", "", "no name at all"), ("Guest", "", "Guest"), ("conner", "", "the right name with no password") })
                {
                    var (anon, anonShare, anonOutcome) = Open(port, who, word, "files");
                    Check($"700 smb: {label} is refused", anonShare == null, anonOutcome);
                    if (anonShare == null && anonOutcome.StartsWith("share"))
                    {
                        // Signed in as nobody: the pipe share must not list the shares either.
                        var listed = anon.ListShares(out var listStatus);
                        Check($"700 smb: and {label} cannot list the shares", listed == null || listed.Count == 0, listStatus.ToString());
                    }
                    anon.Disconnect();
                }

                var second = new SmbFileServer(folder, port, "files", "conner", "a long password", false);
                string? refused = null;
                try { second.Start(); } catch (InvalidOperationException ex) { refused = ex.Message; }
                finally { second.Dispose(); }
                Check("700 smb: a port already taken is said in a sentence",
                    refused == $"Port {port} is already in use by another program.", refused);
            }

            // Stopped: the port is free again.
            bool free;
            try { var again = new TcpListener(IPAddress.Loopback, port); again.Start(); again.Stop(); free = true; }
            catch { free = false; }
            Check("700 smb: stopping gives the port back", free);

            File.WriteAllText(Path.Combine(sandbox, "outside.txt"), "not shared");
            port = FreePort();
            using (var readOnly = new SmbFileServer(folder, port, "files", "conner", "a long password", readOnly: true))
            {
                readOnly.Start();
                var (client, share, said) = Open(port, "conner", "a long password", "files");
                Check("700 smb read only: the share opens", share != null, said);
                if (share != null)
                {
                    Equal("700 smb read only: files are read", "hello from the share", ReadText(share, "hello.txt") ?? "(not opened)");
                    var wrote = WriteText(share, "refused.txt", "no");
                    Check("700 smb read only: nothing is written", wrote != NTStatus.STATUS_SUCCESS &&
                        !File.Exists(Path.Combine(folder, "refused.txt")), wrote.ToString());

                    var forDelete = share.CreateFile(out var doomed, out _, "hello.txt", AccessMask.DELETE | AccessMask.SYNCHRONIZE,
                        SMBLibrary.FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                        CreateDisposition.FILE_OPEN, CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
                    if (forDelete == NTStatus.STATUS_SUCCESS)
                    {
                        share.SetFileInformation(doomed, new FileRenameInformationType2 { FileName = @"\renamed.txt" });
                        share.SetFileInformation(doomed, new FileDispositionInformation { DeletePending = true });
                        share.CloseFile(doomed);
                    }
                    var onClose = share.CreateFile(out var closing, out _, "hello.txt", AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                        SMBLibrary.FileAttributes.Normal, ShareAccess.Read | ShareAccess.Delete, CreateDisposition.FILE_OPEN,
                        CreateOptions.FILE_DELETE_ON_CLOSE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
                    if (onClose == NTStatus.STATUS_SUCCESS) share.CloseFile(closing);
                    var made = share.CreateFile(out var newFolder, out _, "made", AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                        SMBLibrary.FileAttributes.Directory, ShareAccess.Read, CreateDisposition.FILE_CREATE,
                        CreateOptions.FILE_DIRECTORY_FILE, null);
                    if (made == NTStatus.STATUS_SUCCESS) share.CloseFile(newFolder);
                    Check("700 smb read only: nothing is renamed, deleted or made, whichever way it is asked",
                        File.Exists(Path.Combine(folder, "hello.txt")) && !File.Exists(Path.Combine(folder, "renamed.txt")) &&
                        !Directory.Exists(Path.Combine(folder, "made")),
                        $"delete open {forDelete}, delete on close {onClose}, new folder {made}");
                }
                client.Disconnect();
            }
        }

        /// <summary>
        /// A whole drive ("G:\"): the library put two backslashes in every
        /// path, and nothing on a shared drive could be opened. Here a spare
        /// letter is pointed at a folder with Windows' subst.
        /// </summary>
        private static void SmbWholeDrive(string sandbox)
        {
            if (SmbFileServer.StartOnPort == null || ConnectOnPort == null) return;
            var folder = Path.Combine(sandbox, "drive");
            Directory.CreateDirectory(Path.Combine(folder, "Music"));
            File.WriteAllText(Path.Combine(folder, "top.txt"), "at the top");
            File.WriteAllText(Path.Combine(folder, "Music", "song.txt"), "a song");

            char letter = '\0';
            for (char c = 'R'; c >= 'K' && letter == '\0'; c--)
                if (!Directory.Exists(c + @":\")) letter = c;
            var subst = Path.Combine(Environment.SystemDirectory, "subst.exe");
            if (letter == '\0' || !File.Exists(subst))
            {
                Console.WriteLine("  (no spare drive letter or no subst: the whole-drive share was not tested)");
                return;
            }

            void Subst(string arguments)
            {
                using var p = Process.Start(new ProcessStartInfo(subst, arguments) { UseShellExecute = false, CreateNoWindow = true });
                p?.WaitForExit(10000);
            }

            Subst($"{letter}: \"{folder}\"");
            try
            {
                if (!Directory.Exists(letter + @":\"))
                {
                    Console.WriteLine("  (subst did not make the drive: the whole-drive share was not tested)");
                    return;
                }
                int port = FreePort();
                using var server = new SmbFileServer(letter + @":\", port, "drive", "conner", "a long password", false);
                server.Start();
                var (client, share, said) = Open(port, "conner", "a long password", "drive");
                Check("700 smb drive: a whole drive opens as a share", share != null, said);
                if (share != null)
                {
                    Equal("700 smb drive: its top folder is listed", "Music|top.txt", string.Join("|", Names(share, "")));
                    Equal("700 smb drive: a file at the top is read", "at the top", ReadText(share, "top.txt") ?? "(not opened)");
                    Equal("700 smb drive: and one in a folder", "a song", ReadText(share, @"Music\song.txt") ?? "(not opened)");

                    var status = share.CreateFile(out var handle, out _, "top.txt", AccessMask.DELETE | AccessMask.SYNCHRONIZE,
                        SMBLibrary.FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                        CreateDisposition.FILE_OPEN, CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
                    if (status == NTStatus.STATUS_SUCCESS)
                    {
                        status = share.SetFileInformation(handle, new FileRenameInformationType2 { FileName = @"\Music\moved.txt" });
                        share.CloseFile(handle);
                    }
                    Check("700 smb drive: a rename lands where it was sent", status == NTStatus.STATUS_SUCCESS &&
                        File.Exists(Path.Combine(folder, "Music", "moved.txt")) && !File.Exists(Path.Combine(folder, "top.txt")),
                        status.ToString());
                }
                client.Disconnect();
            }
            finally { Subst($"{letter}: /d"); }
        }

        // ---------------- which servers run ----------------

        private static async Task WhichRun(string sandbox)
        {
            if (SmbFileServer.StartOnPort == null || ConnectOnPort == null) return;
            var folder = Path.Combine(sandbox, "served");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "hello.txt"), "served");
            var later = Path.Combine(sandbox, "arrives later");

            var said = new List<string>();
            var before = FileServers.LookAgainEvery;
            var beforeSay = FileServers.SayWaitingAfter;
            FileServers.LookAgainEvery = TimeSpan.FromMilliseconds(150);
            FileServers.SayWaitingAfter = TimeSpan.FromMilliseconds(600);
            using var servers = new FileServers { Said = (id, text) => { lock (said) said.Add(id + ": " + text); } };
            try
            {
                int port = FreePort();
                var settings = new Settings
                {
                    SmbServerEnabled = true, SmbServerFolder = folder, SmbServerPort = port, SmbServerShare = "files",
                    SmbServerUser = "conner", SmbServerPassword = "a long password",
                };
                await servers.Apply(settings, announce: true);
                var (client, share, outcome) = Open(port, "conner", "a long password", "files");
                Check("700 run: a server turned on in settings is listening", servers.IsRunning(FileServers.Kind.Smb) && share != null, outcome);
                client.Disconnect();
                Check("700 run: and that is said, with the folder and the port",
                    said.Count == 1 && said[0] == $"server.started: The SMB server is on, sharing {folder} on port {port}",
                    string.Join(" | ", said));
                Equal("700 run: its status line says the same", $"The SMB server is on, sharing {folder} on port {port}.",
                    servers.StatusOf(FileServers.Kind.Smb));

                // The same settings again: nothing restarts, nothing is said.
                await servers.Apply(settings, announce: true);
                Check("700 run: applying the same settings again says nothing", said.Count == 1, string.Join(" | ", said));

                // A new port: the old one is let go.
                int moved = FreePort();
                settings.SmbServerPort = moved;
                await servers.Apply(settings, announce: true);
                var (old, oldShare, _) = Open(port, "conner", "a long password", "files");
                old.Disconnect();
                (client, share, outcome) = Open(moved, "conner", "a long password", "files");
                client.Disconnect();
                Check("700 run: a changed port moves the server", share != null && oldShare == null, outcome);

                // A short password: it stops, and says why.
                said.Clear();
                settings.SmbServerPassword = "short";
                await servers.Apply(settings, announce: true);
                Check("700 run: settings that cannot be a server stop it and say why",
                    !servers.IsRunning(FileServers.Kind.Smb) && said.Count == 1 && said[0].StartsWith("server.failed: The SMB server is not on."),
                    string.Join(" | ", said));

                // A folder that is not there yet (the Drive letter at start-up).
                said.Clear();
                settings.SmbServerPassword = "a long password";
                settings.SmbServerFolder = later;
                await servers.Apply(settings, announce: false);
                Check("700 run: at start-up a folder that is not there yet is waited for without a word",
                    !servers.IsRunning(FileServers.Kind.Smb) && said.Count == 0 &&
                    servers.StatusOf(FileServers.Kind.Smb).Contains("is waiting for " + later),
                    string.Join(" | ", said) + " / " + servers.StatusOf(FileServers.Kind.Smb));
                var saidBy = Environment.TickCount64 + 5000;
                while (said.Count == 0 && Environment.TickCount64 < saidBy) await Task.Delay(50);
                await Task.Delay(600);
                Check("700 run: still not there after a while, it is said, once",
                    said.Count == 1 && said[0].Contains("is waiting for " + later), string.Join(" | ", said));
                Directory.CreateDirectory(later);
                var until = Environment.TickCount64 + 5000;
                while (!servers.IsRunning(FileServers.Kind.Smb) && Environment.TickCount64 < until) await Task.Delay(50);
                Check("700 run: it starts by itself once the folder is there", servers.IsRunning(FileServers.Kind.Smb));

                said.Clear();
                settings.SmbServerEnabled = false;
                await servers.Apply(settings, announce: true);
                Check("700 run: turned off, it stops and says so",
                    !servers.IsRunning(FileServers.Kind.Smb) && said.SequenceEqual(new[] { "server.stopped: The SMB server is off" }),
                    string.Join(" | ", said));
                Equal("700 run: and its status says off", "The SMB server is off.", servers.StatusOf(FileServers.Kind.Smb));
            }
            finally
            {
                FileServers.LookAgainEvery = before;
                FileServers.SayWaitingAfter = beforeSay;
            }
        }

        // ---------------- wired into the app ----------------

        private static void Wiring()
        {
            string Source(string name)
            {
                var dir = AppContext.BaseDirectory;
                for (int i = 0; i < 8 && dir != null; i++, dir = Path.GetDirectoryName(dir))
                {
                    var path = Path.Combine(dir, "src", name);
                    if (File.Exists(path)) return File.ReadAllText(path);
                }
                return "";
            }

            var form = Source("SettingsForm.cs");
            var tray = Source("TrayApplicationContext.cs");
            if (form.Length == 0 || tray.Length == 0)
            {
                Console.WriteLine("  (sources not beside the tests: wiring not checked)");
                return;
            }
            Check("700 wiring: Preferences has a page called SMB server, and one for each other kind",
                form.Contains("\"SMB server\"") && form.Contains("\"FTP server\"") && form.Contains("\"SFTP server\""));
            Check("700 wiring: OK refuses a server that could not start, before saving", form.Contains("FileServers.WhyNot("));
            Check("700 wiring: the tray applies the servers with the settings, and stops them on the way out",
                tray.Contains("_fileServers.Apply(") && tray.Contains("_fileServers.Dispose()"));
            Check("700 wiring: each server has a command", tray.Contains("\"smb-server-on\"") || tray.Contains("-server-on"));
        }
    }

    // ---- merged from Release701Tests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// 7.0.1: what the security review of the file servers found and fixed
    /// (FTP here; SMB's are in Release700Tests beside the rest of its checks,
    /// SFTP's in FileServerTests), the servers over Tailscale, and a command
    /// for every setting. Group: release701.
    /// </summary>
    internal static class Release701Tests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;
            Console.WriteLine("7.0.1, the servers' security review:");

            var sandbox = Path.Combine(Path.GetTempPath(), "en-701-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                Throttle();
                Tailnet();
                Settings701();
                var work = Task.Run(async () =>
                {
                    Ftp(sandbox);
                    Sftp(sandbox);
                    await OverTailscale(sandbox);
                });
                if (await Task.WhenAny(work, Task.Delay(Timeout.Infinite, watchdog.Token)) != work)
                    Check("701: the server tests finish in time", false, "still running after 120 seconds");
                else await work;
                Wiring();
                Pages();
            }
            finally
            {
                try { Directory.Delete(sandbox, true); } catch { }
            }
            Console.WriteLine();
        }

        // ---------------- the password-guess limit ----------------

        private static void Throttle()
        {
            var now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
            var throttle = new SignInThrottle(() => now);

            // Shut one address out, then try to push its note out with thousands of others.
            var guesser = IPAddress.Parse("203.0.113.9");
            for (int i = 0; i < SignInThrottle.MaxFailures; i++) throttle.Failed(guesser);
            Check("701 throttle: ten wrong passwords shut the address out", throttle.IsBlocked(guesser));
            for (int i = 0; i < 3000; i++)
            {
                now = now.AddMilliseconds(1);
                throttle.Failed(new IPAddress(new byte[] { 198, 18, (byte)(i >> 8), (byte)i }));
            }
            Check("701 throttle: thousands of other addresses do not wipe a lockout", throttle.IsBlocked(guesser));
            Check("701 throttle: and its notes stay bounded", throttle.TrackedAddresses <= 2048, throttle.TrackedAddresses.ToString());

            // Everybody at once.
            var quiet = new SignInThrottle(() => now);
            quiet.Failed(IPAddress.Parse("192.0.2.1"));
            Check("701 throttle: a few wrong passwords slow nobody", !quiet.Braking);
            var busy = new SignInThrottle(() => now);
            for (int i = 0; i < SignInThrottle.BrakeAfter; i++) busy.Failed(new IPAddress(new byte[] { 198, 19, 0, (byte)i }));
            Check("701 throttle: guessing from many addresses at once slows every answer", busy.Braking);
            now = now + SignInThrottle.BrakeWindow + TimeSpan.FromSeconds(1);
            Check("701 throttle: and stops slowing once it stops", !busy.Braking);
        }

        // ---------------- Tailscale ----------------

        private static void Tailnet()
        {
            const string status = "{\"TCP\":{\"443\":{\"HTTPS\":true},\"4450\":{\"TCPForward\":\"127.0.0.1:4450\"}," +
                                  "\"2222\":{\"TCPForward\":\"127.0.0.1:22\"}},\"Web\":{}}";
            Equal("701 tailscale: a port forwarded to this PC is read", "127.0.0.1:4450", TailscaleForward.ForwardOf(status, 4450) ?? "(none)");
            Check("701 tailscale: and known as ours", TailscaleForward.IsOurs(status, 4450));
            Check("701 tailscale: one forwarded somewhere else is not ours", !TailscaleForward.IsOurs(status, 2222) &&
                TailscaleForward.ForwardOf(status, 2222) == "127.0.0.1:22");
            Equal("701 tailscale: a port serving a web page is somebody else's", "other", TailscaleForward.ForwardOf(status, 443) ?? "(none)");
            Check("701 tailscale: a port with nothing on it is free", TailscaleForward.ForwardOf(status, 2121) == null);
            Check("701 tailscale: nothing set up at all reads as free",
                TailscaleForward.ForwardOf("{}", 4450) == null && TailscaleForward.ForwardOf("not json", 4450) == null);

            var ports = FileServers.FtpPassivePorts(2121);
            Check("701 ftp ports: transfers use the eight ports after the FTP port",
                ports.Length == 8 && ports[0] == 2122 && ports[^1] == 2129, string.Join(",", ports));
            var top = FileServers.FtpPassivePorts(65530);
            Check("701 ftp ports: at the top of the range they come before it", top.All(p => p is > 0 and < 65530) && top.Length == 8,
                string.Join(",", top));

            var ftp = new FileServers.Config(FileServers.Kind.Ftp, true, @"D:\Music", 2121, "", "conner", "long enough", false, "");
            var sftp = new FileServers.Config(FileServers.Kind.Sftp, true, @"D:\Music", 2125, "", "conner", "long enough", false, "");
            Check("701 ftp ports: another server cannot sit on one of them",
                FileServers.WhyNot(sftp, new[] { ftp, sftp }) is { } why && why.Contains("2122") && why.Contains("2129"),
                FileServers.WhyNot(sftp, new[] { ftp, sftp }));
            Check("701 ftp ports: nor FTP be put where they would land on another",
                FileServers.WhyNot(ftp, new[] { ftp, sftp }) != null);
            Check("701 ftp ports: the defaults are clear of each other",
                FileServers.WhyNot(new FileServers.Config(FileServers.Kind.Sftp, true, @"D:\Music", FileServers.DefaultSftpPort, "", "c", "long enough", false, ""),
                    new[] { ftp with { Port = FileServers.DefaultFtpPort } }) == null);
        }

        private static async Task OverTailscale(string sandbox)
        {
            var folder = Path.Combine(sandbox, "tailnet");
            Directory.CreateDirectory(folder);
            var started = new List<int>();
            var stopped = new List<int>();
            bool? answer = true;
            using var servers = new FileServers
            {
                UseTailscale = true,
                StartForward = port => { lock (started) started.Add(port); return answer; },
                StopForward = port => { lock (stopped) stopped.Add(port); },
                TailnetName = () => "pc.example.ts.net",
            };

            int port = FreePort(9);
            var settings = new Settings
            {
                FtpServerEnabled = true, FtpServerFolder = folder, FtpServerPort = port,
                FtpServerUser = "conner", FtpServerPassword = "a long password",
            };
            await servers.Apply(settings, announce: false);
            var all = new[] { port }.Concat(FileServers.FtpPassivePorts(port)).ToArray();
            Check("701 tailscale: a server that starts is forwarded, its transfer ports too",
                servers.IsRunning(FileServers.Kind.Ftp) && started.SequenceEqual(all), string.Join(",", started));
            Check("701 tailscale: its status says where to reach it",
                servers.StatusOf(FileServers.Kind.Ftp).Contains("pc.example.ts.net, port " + port), servers.StatusOf(FileServers.Kind.Ftp));

            settings.FtpServerEnabled = false;
            await servers.Apply(settings, announce: false);
            Check("701 tailscale: turned off, every one of its ports stops being forwarded",
                stopped.OrderBy(p => p).SequenceEqual(all.OrderBy(p => p)), string.Join(",", stopped));

            // Tailscale there but refusing is never a reason for the server to be off.
            started.Clear();
            answer = false;
            settings.FtpServerEnabled = true;
            await servers.Apply(settings, announce: false);
            Check("701 tailscale: Tailscale refusing does not stop the server, and the status says so",
                servers.IsRunning(FileServers.Kind.Ftp) && servers.StatusOf(FileServers.Kind.Ftp).Contains("Tailscale is installed but did not take it"),
                servers.StatusOf(FileServers.Kind.Ftp));

            using var plain = new FileServers();
            Check("701 tailscale: nothing is forwarded unless the app asks for it (the tests never touch the real one)", !plain.UseTailscale);
        }

        private static int FreePort(int clearAfter = 0)
        {
            for (int tries = 0; tries < 50; tries++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                if (port + clearAfter <= 65535) return port;
            }
            return 40000;
        }

        // ---------------- a command for every setting ----------------

        private static void Settings701()
        {
            var live = new Settings();
            Check("701 settings: every plain setting has a command name",
                SettingCommands.All.Count > 60 && SettingCommands.All.All(p => SettingCommands.Find(SettingCommands.Dashed(p.Name)) == p),
                SettingCommands.All.Count.ToString());
            Check("701 settings: the name is found however it is typed",
                SettingCommands.Find("smb-server-port")?.Name == nameof(Settings.SmbServerPort) &&
                SettingCommands.Find("SmbServerPort")?.Name == nameof(Settings.SmbServerPort) &&
                SettingCommands.Find("smb server port")?.Name == nameof(Settings.SmbServerPort));
            Equal("701 settings: one is read back in a sentence", "smb-server-port is 4450", SettingCommands.Read(live, "smb-server-port"));

            var changed = SettingCommands.Change(live, "smb-server-port", "4455", out var refusal, out var said);
            Check("701 settings: a number is changed on a copy, and said",
                changed != null && changed.SmbServerPort == 4455 && live.SmbServerPort == 4450 && said == "smb-server-port is now 4455",
                refusal + said);
            changed = SettingCommands.Change(live, "smb-server-enabled", "yes", out refusal, out said);
            Check("701 settings: yes and no are understood", changed is { SmbServerEnabled: true } && said.EndsWith("is now yes"), refusal + said);
            Check("701 settings: nonsense for a number is refused in a sentence",
                SettingCommands.Change(live, "smb-server-port", "lots", out refusal, out _) == null && refusal == "smb-server-port is a whole number.",
                refusal);
            Check("701 settings: nonsense for yes or no too",
                SettingCommands.Change(live, "smb-server-enabled", "maybe", out refusal, out _) == null && refusal.EndsWith("is yes or no."), refusal);
            changed = SettingCommands.Change(live, "smb-server-port", "700000", out refusal, out said);
            Check("701 settings: a number out of range lands on one that is allowed",
                changed != null && FileServers.IsPort(changed.SmbServerPort), refusal + said);
            Check("701 settings: a name that is not a setting says so, with ones near it",
                SettingCommands.Change(live, "smb-server-prot", "1", out refusal, out _) == null && refusal.Contains("is not a setting") &&
                refusal.Contains("smb-server-port"), refusal);

            // Text with spaces arrives whole; a path is a path.
            changed = SettingCommands.Change(live, "smb-server-folder", @"D:\My Music", out refusal, out said);
            Check("701 settings: text is taken exactly as given", changed?.SmbServerFolder == @"D:\My Music", refusal + said);

            var reset = SettingCommands.Reset(new Settings { SmbServerFolder = @"D:\Music", SmbServerPort = 4999 }, "smb-server-folder", out refusal, out said);
            Check("701 settings: one can be put back to how a new installation has it, which is how text is emptied",
                reset is { SmbServerFolder: "", SmbServerPort: 4999 } && said == "smb-server-folder is back to empty", refusal + said);

            // Nothing secret is ever written out.
            var secret = new Settings
            {
                SmbServerPassword = "hunter2-hunter2", FtpServerPassword = "ftp-secret-1", SftpServerPassword = "sftp-secret-1",
                AppleMusicUserToken = "apple-token-value", ConnectCode = "PAIR-CODE-1", AiClaudeKey = "sk-claude-key",
                SftpServerHostKey = "host-key-bytes", FtpServerCertificate = "certificate-bytes",
            };
            var listed = SettingCommands.List(secret);
            foreach (var value in new[] { "hunter2-hunter2", "ftp-secret-1", "sftp-secret-1", "apple-token-value", "PAIR-CODE-1", "sk-claude-key",
                         "host-key-bytes", "certificate-bytes" })
                Check($"701 settings: the list never shows a secret ({value[..4]}...)", !listed.Contains(value));
            Equal("701 settings: a password reads as set", "smb-server-password is set", SettingCommands.Read(secret, "smb-server-password"));
            Equal("701 settings: or not set", "smb-server-password is not set", SettingCommands.Read(live, "smb-server-password"));
            changed = SettingCommands.Change(live, "smb-server-password", "a new password", out refusal, out said);
            Check("701 settings: a password can be changed, and is not said back",
                changed?.SmbServerPassword == "a new password" && said == "smb-server-password is now set", refusal + said);
            Check("701 settings: the servers' own keys are not offered at all",
                SettingCommands.Find("sftp-server-host-key") == null && SettingCommands.Find("ftp-server-certificate") == null);
        }

        // ---------------- FTP, over real sockets ----------------

        private sealed class Client : IDisposable
        {
            private readonly TcpClient _tcp;
            private readonly StreamReader _reader;
            private readonly NetworkStream _stream;
            public string Greeting = "";

            public Client(int port)
            {
                _tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 8000, SendTimeout = 8000 };
                _tcp.Connect(IPAddress.Loopback, port);
                _stream = _tcp.GetStream();
                _reader = new StreamReader(_stream, new UTF8Encoding(false));
                try { Greeting = Read(); } catch { Greeting = "(nothing)"; }
            }

            public string Read()
            {
                while (true)
                {
                    var line = _reader.ReadLine() ?? throw new IOException("closed");
                    if (line.Length >= 4 && char.IsDigit(line[0]) && line[3] == ' ') return line;
                }
            }

            public string Say(string command)
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(command + "\r\n");
                    _stream.Write(bytes, 0, bytes.Length);
                    return Read();
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { return "(closed)"; }
            }

            public bool SignIn() => Say("USER conner").StartsWith("331") && Say("PASS a long password").StartsWith("230");

            /// <summary>Asks for a transfer port and gives its number.</summary>
            public int Passive()
            {
                var reply = Say("EPSV");
                int open = reply.IndexOf("(|||", StringComparison.Ordinal), close = reply.IndexOf("|)", StringComparison.Ordinal);
                return open < 0 || close < 0 ? -1 : int.Parse(reply[(open + 4)..close]);
            }

            public void Raw(string text) { var b = Encoding.UTF8.GetBytes(text); _stream.Write(b, 0, b.Length); }
            public void Cut() { try { _tcp.Client.Close(0); } catch { } }
            public void Dispose() { try { _tcp.Dispose(); } catch { } }
        }

        private static void Ftp(string sandbox)
        {
            var folder = Path.Combine(sandbox, "ftp");
            Directory.CreateDirectory(Path.Combine(folder, "sub"));
            File.WriteAllText(Path.Combine(folder, "hello.txt"), "hello");

            // ---- signing in has a deadline, and a count
            using (var server = new FtpServer(folder, 0, "conner", "a long password", false) { SignInWithinMs = 900 })
            {
                server.Start();
                using var idle = new Client(server.Port);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                string last = "";
                while (clock.ElapsedMilliseconds < 5000)
                {
                    last = idle.Say("NOOP");
                    if (!last.StartsWith("200")) break;
                    Thread.Sleep(150);
                }
                Check("701 ftp: a connection that never signs in is ended, however often it says NOOP",
                    !last.StartsWith("200") && clock.ElapsedMilliseconds < 4000, $"{last} after {clock.ElapsedMilliseconds} ms");

                using var signed = new Client(server.Port);
                bool inside = signed.SignIn();
                Thread.Sleep(1300);
                Check("701 ftp: one that has signed in is not held to that deadline", inside && signed.Say("NOOP").StartsWith("200"));
            }

            using (var server = new FtpServer(folder, 0, "conner", "a long password", false))
            {
                server.Start();
                using var chatty = new Client(server.Port);
                string last = "";
                int sent = 0;
                for (; sent < 60; sent++)
                {
                    last = chatty.Say("SYST");
                    if (!last.StartsWith("215")) break;
                }
                Check("701 ftp: nor may it send commands for ever without signing in", sent < 40 && !last.StartsWith("215"), $"{sent} commands, then {last}");
            }

            // ---- a finished connection really ends
            using (var server = new FtpServer(folder, 0, "conner", "a long password", false))
            {
                server.Start();
                using var lingerer = new Client(server.Port);
                lingerer.Say("QUIT");
                var clock = System.Diagnostics.Stopwatch.StartNew();
                bool closed = false;
                while (clock.ElapsedMilliseconds < 6000 && !closed)
                {
                    try { lingerer.Raw("x"); } catch (Exception ex) when (ex is IOException or SocketException) { closed = true; }
                    Thread.Sleep(300);
                }
                Check("701 ftp: after QUIT a caller dripping bytes is let go within seconds, not a day", closed, $"{clock.ElapsedMilliseconds} ms");
            }

            // ---- uploads leave nothing behind
            using (var server = new FtpServer(folder, 0, "conner", "a long password", false) { PassiveWaitMs = 1500 })
            {
                server.Start();
                using (var dropper = new Client(server.Port))
                {
                    dropper.SignIn();
                    dropper.Say("CWD sub");
                    dropper.Passive();
                    dropper.Raw("STOR orphan.bin\r\n");
                    Thread.Sleep(300);
                    dropper.Cut();
                }
                var until = Environment.TickCount64 + 6000;
                while (Directory.EnumerateFileSystemEntries(Path.Combine(folder, "sub")).Any() && Environment.TickCount64 < until) Thread.Sleep(100);
                Check("701 ftp: an upload whose connection drops before any data leaves no hidden file",
                    !Directory.EnumerateFileSystemEntries(Path.Combine(folder, "sub")).Any(),
                    string.Join(", ", Directory.EnumerateFileSystemEntries(Path.Combine(folder, "sub")).Select(Path.GetFileName)));

                using var client = new Client(server.Port);
                client.SignIn();
                client.Passive();
                client.Say("REST 5");
                var refused = client.Say("STOR brandnew.bin");
                Check("701 ftp: carrying on a file that is not there is refused without making it",
                    refused.StartsWith("5") && !File.Exists(Path.Combine(folder, "brandnew.bin")), refused);
                using var fresh = new Client(server.Port);
                fresh.SignIn();
                var noPort = fresh.Say("APPE noport.bin");
                Check("701 ftp: adding to a file with no transfer port asked for makes no file",
                    noPort.StartsWith("425") && !File.Exists(Path.Combine(folder, "noport.bin")), noPort);
            }

            // ---- fixed transfer ports
            int basePort = FreePort(9);
            var two = new[] { basePort + 1, basePort + 2 };
            using (var server = new FtpServer(folder, basePort, "conner", "a long password", false, null, two))
            {
                server.Start();
                using var a = new Client(server.Port);
                using var b = new Client(server.Port);
                using var c = new Client(server.Port);
                a.SignIn(); b.SignIn(); c.SignIn();
                int first = a.Passive(), second = b.Passive();
                var third = c.Say("EPSV");
                Check("701 ftp: transfers come in on the ports it was given, one each",
                    two.Contains(first) && two.Contains(second) && first != second, $"{first}, {second}");
                Check("701 ftp: and with every one busy the next is asked to wait, in a sentence", third.StartsWith("425"), third);
                int again = a.Passive();
                Check("701 ftp: asking again reuses the caller's own port", two.Contains(again) && again != second, again.ToString());
            }

            // ---- encrypted connections only
            using (var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(FileServers.NewCertificate(), null))
            using (var server = new FtpServer(folder, 0, "conner", "a long password", false, certificate, null, requireTls: true))
            {
                server.Start();
                using var plain = new Client(server.Port);
                var user = plain.Say("USER conner");
                var pass = plain.Say("PASS a long password");
                var list = plain.Say("PWD");
                Check("701 ftp: with encrypted only, nobody signs in over plain FTP",
                    user.StartsWith("530") && pass.StartsWith("530") && list.StartsWith("530"), $"{user} | {pass} | {list}");
                Check("701 ftp: and the refusal says what to turn on", user.Contains("TLS"), user);
            }
            string? noCertificate = null;
            try { using var bad = new FtpServer(folder, 0, "conner", "a long password", false, null, null, requireTls: true); bad.Start(); }
            catch (InvalidOperationException ex) { noCertificate = ex.Message; }
            Check("701 ftp: encrypted only with no certificate does not start", noCertificate != null, noCertificate);

            // ---- one address cannot take every place
            using (var server = new FtpServer(folder, 0, "conner", "a long password", false))
            {
                server.Start();
                // From this PC itself there is no limit per address (Tailscale arrives that way).
                var many = new List<Client>();
                try
                {
                    for (int i = 0; i < FtpServer.MaxPerAddress + 4; i++) many.Add(new Client(server.Port));
                    Check("701 ftp: this PC's own connections are not held to the per-address limit",
                        many.All(m => m.Greeting.StartsWith("220")), many.Count(m => m.Greeting.StartsWith("220")).ToString());
                }
                finally { foreach (var m in many) m.Dispose(); }
            }
        }

        // ---------------- SFTP: nobody holds a place without signing in ----------------

        /// <summary>Sends <paramref name="first"/>, then a byte every so often, and says how long the server kept the connection.</summary>
        private static long HeldFor(int port, string first, int dripEveryMs, int giveUpMs)
        {
            using var tcp = new TcpClient { NoDelay = true };
            tcp.Connect(IPAddress.Loopback, port);
            var stream = tcp.GetStream();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var bytes = Encoding.ASCII.GetBytes(first);
                if (bytes.Length > 0) stream.Write(bytes, 0, bytes.Length);
                var one = new byte[] { (byte)'x' };
                var scrap = new byte[4096];
                while (clock.ElapsedMilliseconds < giveUpMs)
                {
                    Thread.Sleep(dripEveryMs);
                    stream.Write(one, 0, 1);
                    // A closed connection shows on the read side first.
                    if (tcp.Client.Poll(0, SelectMode.SelectRead))
                    {
                        int read = tcp.Client.Receive(scrap);
                        if (read == 0) break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException) { }
            return clock.ElapsedMilliseconds;
        }

        private static void Sftp(string sandbox)
        {
            var folder = Path.Combine(sandbox, "sftp");
            Directory.CreateDirectory(folder);
            var key = SftpServer.NewHostKey();

            using (var server = new SftpServer(folder, 0, "conner", "a long password", false, key) { BeforeSignInMs = 1000 })
            {
                server.Start();
                long silent = HeldFor(server.Port, "", 200, 8000);
                Check("701 sftp: a connection that says nothing useful is ended at the sign-in deadline, drip or no drip",
                    silent < 5000, silent + " ms");
                long version = HeldFor(server.Port, "SSH-2.0-slow", 200, 8000);
                Check("701 sftp: so is one that never finishes its first line", version < 5000, version + " ms");
                long junk = HeldFor(server.Port, "junk\n", 200, 8000);
                Check("701 sftp: and one already refused is let go within seconds, however it drips", junk < 5000, junk + " ms");
            }

            using (var server = new SftpServer(folder, 0, "conner", "a long password", false, key))
            {
                server.Start();
                // More than the server has places for, all silent. Before, each
                // was a thread of its own for a minute whatever the limit said.
                int threadsBefore = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
                var held = new List<TcpClient>();
                try
                {
                    for (int i = 0; i < 300; i++)
                    {
                        var tcp = new TcpClient();
                        tcp.Connect(IPAddress.Loopback, server.Port);
                        held.Add(tcp);
                    }
                    Thread.Sleep(500);
                    int extra = System.Diagnostics.Process.GetCurrentProcess().Threads.Count - threadsBefore;
                    Check("701 sftp: silent connections past the limit cost no threads", extra < 120, extra + " more threads for 300 connections");
                }
                finally { foreach (var tcp in held) tcp.Dispose(); }
            }
        }

        // ---------------- the Preferences pages, built off screen ----------------

        /// <summary>
        /// Whether a control is itself set to show. Control.Visible also asks
        /// every parent, and this window is never put on screen.
        /// </summary>
        private static bool Shows(System.Windows.Forms.Control control) =>
            (bool)typeof(System.Windows.Forms.Control)
                .GetMethod("GetState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(control, new object[] { 2 })!;

        private static void Pages()
        {
            Exception? failed = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var window = new SettingsForm(new Settings { SpeakEnabled = false }, "SMB server");
                    window.BuildAllPages();
                    foreach (var name in new[] { "SMB", "FTP", "SFTP" })
                    {
                        var page = window.Pages.FirstOrDefault(p => p.AccessibleName == name + " server settings");
                        Check($"701 pages: Preferences has the {name} server page", page != null);
                        if (page == null) continue;
                        var controls = page.Controls.Cast<System.Windows.Forms.Control>().ToList();
                        var box = controls.OfType<System.Windows.Forms.CheckBox>().First(c => c.Text.StartsWith("&Turn on"));
                        var showing = controls.Where(Shows).ToList();
                        Check($"701 pages: with the {name} server off, only the first paragraph and its box show",
                            showing.Count == 2 && showing.Contains(box) && showing.Contains(controls[0]), showing.Count + " showing");
                        box.Checked = true;
                        Check($"701 pages: ticking it brings the rest of the {name} page back", controls.All(Shows),
                            controls.Count(c => !Shows(c)) + " still hidden");
                        box.Checked = false;
                        Check($"701 pages: and unticking hides it again", controls.Count(Shows) == 2);

                        var buttons = controls.SelectMany(c => c is System.Windows.Forms.Button b
                            ? new[] { b } : c.Controls.OfType<System.Windows.Forms.Button>()).Select(b => b.Text).ToList();
                        Check($"701 pages: the {name} page's buttons say folder or drive to share, and Allow through Windows Firewall",
                            buttons.Contains("&Browse for the folder or drive to share...") && buttons.Contains("Allow through Windows Fire&wall..."),
                            string.Join(" | ", buttons));
                        var labels = controls.SelectMany(c => c.Controls.OfType<System.Windows.Forms.Label>()).Select(l => l.Text).ToList();
                        Check($"701 pages: its folder box is labelled Folder or drive to share", labels.Contains("&Folder or drive to share:"),
                            string.Join(" | ", labels));
                    }
                    var ftp = window.Pages.First(p => p.AccessibleName == "FTP server settings");
                    Check("701 pages: the FTP page has Encrypted connections only",
                        ftp.Controls.OfType<System.Windows.Forms.CheckBox>().Any(c => c.Text.StartsWith("&Encrypted connections only")));
                }
                catch (Exception ex) { failed = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(30))) Check("701 pages: the Preferences pages build in time", false, "still building after 30 seconds");
            Check("701 pages: the Preferences pages build without an error", failed == null, failed?.ToString());
        }

        // ---------------- wired into the app ----------------

        private static void Wiring()
        {
            string Source(string name)
            {
                var dir = AppContext.BaseDirectory;
                for (int i = 0; i < 8 && dir != null; i++, dir = Path.GetDirectoryName(dir))
                {
                    var path = Path.Combine(dir, "src", name);
                    if (File.Exists(path)) return File.ReadAllText(path);
                }
                return "";
            }

            var tray = Source("TrayApplicationContext.cs");
            var form = Source("SettingsForm.cs");
            var servers = Source("FileServers.cs");
            if (tray.Length == 0 || form.Length == 0 || servers.Length == 0)
            {
                Console.WriteLine("  (sources not beside the tests: wiring not checked)");
                return;
            }
            foreach (var command in new[] { "-server-on", "-server-off", "-server-status", "\"sftp-server-fingerprint\"", "\"firewall-status\"",
                         "\"allow-through-firewall\"", "\"remove-from-firewall\"", "\"settings\"", "\"setting\"" })
                Check($"701 wiring: the tray has the command {command.Trim('"')}", tray.Contains(command));
            Check("701 wiring: the app's servers go over Tailscale", tray.Contains("UseTailscale = true"));
            Check("701 wiring: the firewall button says Remove once it is allowed",
                form.Contains("Remove from Windows Fire&wall") && form.Contains("Allow through Windows Fire&wall"));
            Check("701 wiring: unticking a server hides its settings", form.Contains("c != enable && c != intro"));
            Check("701 wiring: the firewall rule is made without a command shell", !servers.Contains("cmd.exe"));
            Check("701 wiring: FTP is given its transfer ports", servers.Contains("FtpPassivePorts(c.Port)"));
        }
    }
}
