using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The file servers of Preferences (7.0.0, Conner: "add samba or
    /// something... you choose the port, the drive or folder to host... add
    /// other types too"): SMB, FTP and SFTP, each sharing one folder or drive
    /// to one user name and password, on a port of the person's choosing.
    ///
    /// This is the part that decides which are running. Each server is its own
    /// class (SmbFileServer, FtpServer, SftpServer); they open files the way
    /// any program on this PC does, so the Google Drive letter is a folder
    /// like any other to them. Nothing here runs on the UI thread: a folder
    /// on a drive that has stopped answering can take a long time to say so.
    /// </summary>
    internal sealed partial class FileServers : IDisposable
    {
        // Not the protocols' own ports: Windows holds 445 on every PC, and
        // ports under 1024 are where another server is most likely to be.
        public const int DefaultSmbPort = 4450;
        public const int DefaultFtpPort = 2121;
        public const int DefaultSftpPort = 2222;

        public static bool IsPort(int port) => port is >= 1 and <= 65535;

        public enum Kind { Smb, Ftp, Sftp }

        public static readonly Kind[] All = { Kind.Smb, Kind.Ftp, Kind.Sftp };

        public static string NameOf(Kind kind) => kind switch
        {
            Kind.Smb => "SMB",
            Kind.Ftp => "FTP",
            _ => "SFTP",
        };

        /// <summary>One server as the settings ask for it. Compared whole: any difference is a restart.</summary>
        public sealed record Config(Kind Kind, bool Enabled, string Folder, int Port, string Share, string User, string Password,
            bool ReadOnly, string Key, bool EncryptedOnly = false);

        public static Config Read(Settings s, Kind kind) => kind switch
        {
            Kind.Smb => new Config(kind, s.SmbServerEnabled, s.SmbServerFolder.Trim(), s.SmbServerPort, s.SmbServerShare.Trim(),
                s.SmbServerUser.Trim(), s.SmbServerPassword, s.SmbServerReadOnly, ""),
            Kind.Ftp => new Config(kind, s.FtpServerEnabled, s.FtpServerFolder.Trim(), s.FtpServerPort, "",
                s.FtpServerUser.Trim(), s.FtpServerPassword, s.FtpServerReadOnly, s.FtpServerCertificate, s.FtpServerEncryptedOnly),
            _ => new Config(kind, s.SftpServerEnabled, s.SftpServerFolder.Trim(), s.SftpServerPort, "",
                s.SftpServerUser.Trim(), s.SftpServerPassword, s.SftpServerReadOnly, s.SftpServerHostKey),
        };

        /// <summary>The shortest password a server will start with.</summary>
        public const int ShortestPassword = 8;

        /// <summary>
        /// Why these settings cannot be a server, as a sentence, or null when
        /// they can. Asked by Preferences before OK and again before starting.
        /// Whether the folder is there is not asked here: that reads the disk.
        /// </summary>
        public static string? WhyNot(Config c, IEnumerable<Config>? others = null)
        {
            var name = NameOf(c.Kind);
            if (c.Folder.Length == 0) return $"Choose the folder or drive to share for the {name} server.";
            if (!Path.IsPathFullyQualified(c.Folder)) return $"The {name} server needs a whole path, such as D:\\Music or G:\\.";
            if (AppleMusic.Is(c.Folder)) return "Apple Music is not a folder that can be shared.";
            if (!IsPort(c.Port)) return $"The {name} server's port has to be a number from 1 to 65535.";
            if (c.User.Length == 0) return $"Give the {name} server a user name.";
            if (c.User.IndexOfAny(new[] { ' ', '\\', '/', ':', '@' }) >= 0)
                return $"The {name} server's user name cannot have a space, a slash, a colon or an at sign in it.";
            if (c.Password.Length < ShortestPassword)
                return $"Give the {name} server a password of at least {ShortestPassword} characters. It is what stands between your files and anyone who can reach this PC.";
            if (c.Kind == Kind.Smb)
            {
                if (c.Share.Length == 0) return "Give the SMB share a name.";
                if (c.Share.Length > 80 || c.Share.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                    return "The SMB share's name cannot have a slash, a colon or a wildcard in it.";
            }
            if (others != null)
                foreach (var other in others)
                {
                    if (other.Kind == c.Kind || !other.Enabled) continue;
                    if (other.Port == c.Port)
                        return $"The {name} server and the {NameOf(other.Kind)} server cannot both use port {c.Port}.";
                    // FTP's transfers use the eight ports after its own.
                    if (Array.IndexOf(PortsOf(other), c.Port) >= 0 || Array.IndexOf(PortsOf(c), other.Port) >= 0)
                    {
                        var ftp = c.Kind == Kind.Ftp ? c : other;
                        var passive = FtpPassivePorts(ftp.Port);
                        return $"The FTP server on port {ftp.Port} also uses ports {passive[0]} to {passive[^1]} for its transfers, " +
                               $"so the {NameOf((c.Kind == Kind.Ftp ? other : c).Kind)} server needs a port outside those.";
                    }
                }
            return null;
        }

        /// <summary>
        /// Makes the keys the encrypted servers need and have not got. True
        /// when the settings changed and want saving. Cheap, so it can run
        /// wherever the settings are about to be applied.
        /// </summary>
        public static bool EnsureKeys(Settings s)
        {
            bool changed = false;
            if (s.FtpServerEnabled && s.FtpServerCertificate.Length == 0)
            {
                try
                {
                    s.FtpServerCertificate = Convert.ToBase64String(NewCertificate());
                    changed = true;
                }
                catch { }   // FTP still works, without TLS on offer
            }
            changed |= EnsureSftpKey(s);
            return changed;
        }

        /// <summary>A self-signed certificate for FTP over TLS, as a PFX with no password. Ten years.</summary>
        internal static byte[] NewCertificate()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Explorer Native FTP server", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            return made.Export(X509ContentType.Pfx);
        }

        /// <summary>The one firewall rule, for this program whatever its servers and ports.</summary>
        internal const string FirewallRule = "Explorer Native file servers";

        /// <summary>
        /// Lets other devices reach this program through Windows Firewall: one
        /// rule for the executable (so FTP's data connections, which come in
        /// on ports of their own, are covered). Asks for an administrator,
        /// unless the rule is already there. Null when done, else why not, as
        /// a sentence. Blocks while Windows asks: call it from a worker.
        /// </summary>
        /// <remarks>
        /// netsh is run directly, by its full path, with the executable's
        /// path as one quoted argument. It went through cmd at first (7.0.0),
        /// which reads a percent sign or an ampersand in a path its own way.
        /// </remarks>
        public static string? AllowThroughFirewall()
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || exe.Contains('"')) return "Explorer Native could not tell where it is running from.";
            if (FirewallAllows(exe)) return null;
            return RunNetsh(elevated: true,
                $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow program=\"{exe}\" protocol=TCP enable=yes profile=any",
                out _) switch
            {
                0 => null,
                -1223 => "The administrator question was answered No.",
                -1 => "Windows did not finish the change.",
                _ => "Windows Firewall refused the new rule.",
            };
        }

        /// <summary>Takes the rule away again. Null when it is gone (or was never there).</summary>
        public static string? RemoveFromFirewall()
        {
            var exe = Environment.ProcessPath ?? "";
            if (!FirewallAllows(exe, anyProgram: true)) return null;
            return RunNetsh(elevated: true, $"advfirewall firewall delete rule name=\"{FirewallRule}\"", out _) switch
            {
                0 => null,
                -1223 => "The administrator question was answered No.",
                -1 => "Windows did not finish the change.",
                _ => "Windows Firewall would not remove the rule.",
            };
        }

        /// <summary>Whether our rule is there for this executable. Asked without an administrator.</summary>
        public static bool FirewallAllows(string exe, bool anyProgram = false)
        {
            if (RunNetsh(elevated: false, $"advfirewall firewall show rule name=\"{FirewallRule}\" verbose", out var said) != 0) return false;
            return anyProgram || said.Contains(exe, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>netsh's exit code; -1223 when the administrator question was refused, -1 for anything else that stopped it.</summary>
        private static int RunNetsh(bool elevated, string arguments, out string output)
        {
            output = "";
            var netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo(netsh, arguments);
                if (elevated)
                {
                    start.UseShellExecute = true;
                    start.Verb = "runas";
                    start.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                }
                else
                {
                    start.UseShellExecute = false;
                    start.CreateNoWindow = true;
                    start.RedirectStandardOutput = true;
                }
                using var process = System.Diagnostics.Process.Start(start);
                if (process == null) return -1;
                if (!elevated) output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(60000)) return -1;
                return process.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return -1223; }
            catch { return -1; }
        }

        // ---------------- running ----------------

        private readonly object _gate = new();
        private readonly Dictionary<Kind, (Config Config, IDisposable Server)> _running = new();
        private readonly Dictionary<Kind, string> _problems = new();
        private readonly HashSet<Kind> _waitingForFolder = new();
        private readonly Dictionary<Kind, DateTime> _waitingSince = new();
        private readonly HashSet<Kind> _saidWaiting = new();

        /// <summary>How long a server waits quietly for its folder at start-up before saying so.</summary>
        internal static TimeSpan SayWaitingAfter { get; set; } = TimeSpan.FromMinutes(2);
        private Settings? _wanted;
        private Task _work = Task.CompletedTask;
        private Timer? _retry;
        private bool _disposed;

        /// <summary>How often a server whose folder is not there yet looks again (the Drive letter arrives after start).</summary>
        internal static TimeSpan LookAgainEvery { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// What happened, for the person: the notification id (server.started,
        /// server.stopped, server.failed) and a sentence. Raised on a worker.
        /// </summary>
        public Action<string, string>? Said;

        /// <summary>Raised on a worker whenever what is running changed.</summary>
        public Action? Changed;

        /// <summary>
        /// Brings what is running into line with the settings: stops what was
        /// turned off or changed, starts what is on. Returns at once; the work
        /// is queued behind any earlier call.
        /// </summary>
        /// <param name="announce">
        /// Somebody just pressed OK, so starting and stopping are said. At
        /// start-up only failures are.
        /// </param>
        public Task Apply(Settings settings, bool announce)
        {
            lock (_gate)
            {
                if (_disposed) return Task.CompletedTask;
                _wanted = settings;
                var configs = new List<Config>();
                foreach (var kind in All) configs.Add(Read(settings, kind));
                return _work = _work.ContinueWith(_ => Reconcile(configs, announce), TaskScheduler.Default);
            }
        }

        private void Reconcile(List<Config> configs, bool announce)
        {
            bool changed = false;
            foreach (var config in configs)
            {
                try { changed |= ReconcileOne(config, configs, announce); }
                catch (Exception ex)
                {
                    // Nothing a server does may take the others, or the app, with it.
                    SetProblem(config.Kind, $"The {NameOf(config.Kind)} server could not start: {ex.Message}");
                    changed = true;
                }
            }

            lock (_gate)
            {
                if (_disposed) return;
                if (_waitingForFolder.Count > 0)
                {
                    _retry ??= new Timer(_ => LookAgain());
                    _retry.Change(LookAgainEvery, Timeout.InfiniteTimeSpan);
                }
            }
            if (changed) { try { Changed?.Invoke(); } catch { } }
        }

        private void LookAgain()
        {
            Settings? wanted;
            lock (_gate)
            {
                if (_disposed || _waitingForFolder.Count == 0) return;
                wanted = _wanted;
            }
            if (wanted != null) Apply(wanted, announce: false);
        }

        private bool ReconcileOne(Config config, List<Config> all, bool announce)
        {
            var kind = config.Kind;
            var name = NameOf(kind);
            (Config Config, IDisposable Server) was;
            bool wasRunning;
            lock (_gate) wasRunning = _running.TryGetValue(kind, out was);

            if (wasRunning && (!config.Enabled || was.Config != config))
            {
                try { was.Server.Dispose(); } catch { }
                lock (_gate) { _running.Remove(kind); _tailnet.Remove(kind); }
                wasRunning = false;
                // Off, or on other ports now: Tailscale stops forwarding the old ones.
                if (UseTailscale)
                {
                    var still = config.Enabled ? PortsOf(config) : Array.Empty<int>();
                    foreach (int port in PortsOf(was.Config))
                        if (Array.IndexOf(still, port) < 0) { try { StopForward(port); } catch { } }
                }
                if (!config.Enabled)
                {
                    lock (_gate) { _problems.Remove(kind); NotWaiting(kind); }
                    if (announce) Say("server.stopped", $"The {name} server is off");
                    return true;
                }
            }

            if (!config.Enabled)
            {
                bool had;
                lock (_gate) { had = _problems.Remove(kind); NotWaiting(kind); }
                return had;
            }
            if (wasRunning) return false;   // on, and as asked

            if (WhyNot(config, all) is { } refusal)
            {
                lock (_gate) NotWaiting(kind);
                return Fail(kind, $"The {name} server is not on. {refusal}", announce: true);
            }

            bool there;
            try { there = Directory.Exists(config.Folder); }
            catch { there = false; }
            if (!there)
            {
                // Often the Google Drive letter, which arrives a while after
                // the app starts: looked for again quietly. Said at once when
                // somebody has just pressed OK, otherwise only if it is still
                // not there after a while, and only once.
                var waiting = $"The {name} server is waiting for {config.Folder}, which is not there right now. " +
                              "It starts by itself once it is.";
                bool sayNow;
                lock (_gate)
                {
                    _waitingForFolder.Add(kind);
                    if (!_waitingSince.TryGetValue(kind, out var since)) _waitingSince[kind] = since = DateTime.UtcNow;
                    sayNow = (announce || DateTime.UtcNow - since >= SayWaitingAfter) && _saidWaiting.Add(kind);
                }
                bool isNew = SetProblem(kind, waiting);
                if (sayNow) Say("server.failed", waiting);
                return isNew;
            }
            lock (_gate) NotWaiting(kind);

            IDisposable server;
            try { server = Make(config); }
            catch (Exception ex)
            {
                var why = ex is InvalidOperationException ? ex.Message : $"{ex.Message}";
                return Fail(kind, $"The {name} server could not start. {why}", announce: true);
            }

            bool late;
            lock (_gate)
            {
                late = _disposed;
                if (!late)
                {
                    _running[kind] = (config, server);
                    _problems.Remove(kind);
                }
            }
            if (late) { try { server.Dispose(); } catch { } return false; }

            // Over Tailscale with nothing to set up (see TailscaleForward).
            // After it is listening, and never a reason for it not to be on.
            if (UseTailscale)
            {
                bool? reachable = true;
                try
                {
                    foreach (int port in PortsOf(config))
                    {
                        var one = StartForward(port);
                        if (one == null) { reachable = null; break; }
                        if (one == false) reachable = false;
                    }
                }
                catch { reachable = false; }
                string? onTailnet = null;
                if (reachable == true) { try { onTailnet = TailnetName(); } catch { } }
                lock (_gate) { _tailnet[kind] = reachable; if (onTailnet != null) _tailnetName = onTailnet; }
            }

            if (announce) Say("server.started", Sentence(config));
            return true;
        }

        /// <summary>Set by the tray: servers are also forwarded from the Tailscale network. Off in the tests.</summary>
        public bool UseTailscale { get; set; }

        /// <summary>The real Tailscale, unless a test says otherwise.</summary>
        internal Func<int, bool?> StartForward { get; set; } = TailscaleForward.Start;
        internal Action<int> StopForward { get; set; } = TailscaleForward.Stop;
        internal Func<string?> TailnetName { get; set; } = TailscaleForward.Name;

        /// <summary>Per running server: true forwarded, false Tailscale would not, null no Tailscale here.</summary>
        private readonly Dictionary<Kind, bool?> _tailnet = new();
        private string? _tailnetName;

        /// <summary>
        /// Every port a server listens on: its own, and for FTP the few its
        /// file transfers come in on (<see cref="FtpPassivePorts"/>).
        /// </summary>
        internal static int[] PortsOf(Config c)
        {
            if (c.Kind != Kind.Ftp) return new[] { c.Port };
            var passive = FtpPassivePorts(c.Port);
            var all = new int[passive.Length + 1];
            all[0] = c.Port;
            passive.CopyTo(all, 1);
            return all;
        }

        /// <summary>How many files FTP can move at once: each transfer has a port to itself while it runs.</summary>
        public const int FtpTransfersAtOnce = 8;

        /// <summary>
        /// The ports FTP's transfers use: the eight after its own port (before
        /// it, at the very top of the range). Fixed, where they used to be
        /// any free port, so that Tailscale can forward them and a firewall
        /// rule can name them.
        /// </summary>
        public static int[] FtpPassivePorts(int port)
        {
            var ports = new int[FtpTransfersAtOnce];
            int first = port + FtpTransfersAtOnce <= 65535 ? port + 1 : port - FtpTransfersAtOnce;
            for (int i = 0; i < ports.Length; i++) ports[i] = first + i;
            return ports;
        }

        /// <summary>The Tailscale sentence for a running server, or empty.</summary>
        private string TailnetSentence(Config c)
        {
            if (!_tailnet.TryGetValue(c.Kind, out var reachable)) return "";
            return reachable switch
            {
                true => _tailnetName != null
                    ? $" Devices on your Tailscale network reach it at {_tailnetName}, port {c.Port}, with nothing more to set up."
                    : " Devices on your Tailscale network reach it with nothing more to set up.",
                false => " Tailscale is installed but did not take it: check that Tailscale is running and signed in.",
                null => "",
            };
        }

        /// <summary>Under the gate: the server is no longer waiting for its folder.</summary>
        private void NotWaiting(Kind kind)
        {
            _waitingForFolder.Remove(kind);
            _waitingSince.Remove(kind);
            _saidWaiting.Remove(kind);
        }

        /// <summary>"The SMB server is on, sharing D:\Music on port 4450".</summary>
        internal static string Sentence(Config c) =>
            $"The {NameOf(c.Kind)} server is on, sharing {c.Folder} on port {c.Port}" + (c.ReadOnly ? ", read only" : "");

        private bool Fail(Kind kind, string sentence, bool announce)
        {
            bool isNew = SetProblem(kind, sentence);
            if (announce && isNew) Say("server.failed", sentence);
            return isNew;
        }

        /// <summary>True when this is not the problem already on record.</summary>
        private bool SetProblem(Kind kind, string sentence)
        {
            lock (_gate)
            {
                if (_problems.TryGetValue(kind, out var was) && was == sentence) return false;
                _problems[kind] = sentence;
                return true;
            }
        }

        private void Say(string id, string sentence)
        {
            try { Said?.Invoke(id, sentence); } catch { }
        }

        private static IDisposable Make(Config c)
        {
            switch (c.Kind)
            {
                case Kind.Smb:
                {
                    var smb = new SmbFileServer(c.Folder, c.Port, c.Share, c.User, c.Password, c.ReadOnly);
                    smb.Start();
                    return smb;
                }
                case Kind.Ftp:
                {
                    X509Certificate2? tls = null;
                    try
                    {
                        if (c.Key.Length > 0)
                            tls = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(c.Key), null);
                    }
                    catch { tls = null; }   // a damaged certificate only costs TLS
                    if (tls == null && c.EncryptedOnly)
                        throw new InvalidOperationException("It is set to encrypted connections only, and its certificate could not be read. " +
                                                            "Turn it off and on again in Preferences to make a new one.");
                    var ftp = new FtpServer(c.Folder, c.Port, c.User, c.Password, c.ReadOnly, tls, FtpPassivePorts(c.Port), c.EncryptedOnly);
                    try { ftp.Start(); }
                    catch { tls?.Dispose(); throw; }
                    return new WithCertificate(ftp, tls);
                }
                default:
                {
                    IDisposable? sftp = null;
                    MakeSftp(c, ref sftp);
                    return sftp ?? throw new InvalidOperationException("This build has no SFTP server.");
                }
            }
        }

        /// <summary>In FileServers.Sftp.cs.</summary>
        static partial void MakeSftp(Config c, ref IDisposable? made);

        /// <summary>In FileServers.Sftp.cs: makes the SFTP host key when SFTP is on and has none.</summary>
        private static bool EnsureSftpKey(Settings s)
        {
            bool changed = false;
            NewSftpKey(s, ref changed);
            return changed;
        }

        static partial void NewSftpKey(Settings s, ref bool changed);

        /// <summary>The SFTP server's key fingerprint as OpenSSH shows it, or empty when it has no key yet.</summary>
        public static string FingerprintOf(Settings s)
        {
            string print = "";
            try { SftpFingerprint(s, ref print); } catch { print = ""; }
            return print;
        }

        static partial void SftpFingerprint(Settings s, ref string print);

        /// <summary>The certificate lives as long as the server using it.</summary>
        private sealed class WithCertificate : IDisposable
        {
            private readonly IDisposable _server;
            private readonly X509Certificate2? _certificate;
            public WithCertificate(IDisposable server, X509Certificate2? certificate) { _server = server; _certificate = certificate; }
            public void Dispose()
            {
                try { _server.Dispose(); } catch { }
                try { _certificate?.Dispose(); } catch { }
            }
        }

        /// <summary>Whether a server is listening right now.</summary>
        public bool IsRunning(Kind kind)
        {
            lock (_gate) return _running.ContainsKey(kind);
        }

        /// <summary>One line for Preferences: running where, or why not.</summary>
        public string StatusOf(Kind kind)
        {
            lock (_gate)
            {
                if (_running.TryGetValue(kind, out var on)) return Sentence(on.Config) + "." + TailnetSentence(on.Config);
                if (_problems.TryGetValue(kind, out var problem)) return problem;
            }
            return $"The {NameOf(kind)} server is off.";
        }

        /// <summary>Stops everything. Returns once the servers have let go of their ports.</summary>
        public void Dispose()
        {
            List<IDisposable> stopping = new();
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var (_, server) in _running.Values) stopping.Add(server);
                _running.Clear();
                _retry?.Dispose();
                _retry = null;
            }
            foreach (var server in stopping) { try { server.Dispose(); } catch { } }
        }
    }
}
