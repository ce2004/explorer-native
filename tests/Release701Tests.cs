using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
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
