using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SMBLibrary;
using SMBLibrary.Client;

namespace ExplorerNative
{
    /// <summary>
    /// Every 7.0.1 fix to the file servers, attacked the way an outsider
    /// would: over a real socket, with a client written here (raw FTP, an SSH
    /// client of our own for SFTP, SMBLibrary's own client for SMB). Release701Tests
    /// and FileServerTests already pin the rules one by one; these try to get
    /// round them. Also: which networks each server listens on, against what
    /// the Preferences pages say. Group: serverattacks.
    ///
    /// Runs on any Windows PC without Tailscale: free ports, junctions (no
    /// administrator needed), symbolic links only where Windows allows them.
    /// The per-address limit needs a second address of this PC's own that is
    /// not 127.x (any network adapter's); without one that part says so and is skipped.
    /// </summary>
    internal static class ServerAttackTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;
        private static volatile string _stage = "starting";

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        private const string User = "conner";
        private const string Password = "a long password";
        private const string Secret = "not shared";

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;
            Console.WriteLine("Server attacks, over real sockets:");

            var sandbox = Path.Combine(Path.GetTempPath(), "en-attack-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            try
            {
                var work = Task.Run(() =>
                {
                    Stage("links", () => Links(sandbox));
                    Stage("crowds", () => Crowds(sandbox));
                    Stage("goodbyes", () => Goodbyes(sandbox));
                    Stage("guessing", () => Guessing(sandbox));
                    Stage("listening", () => Listening(sandbox));
                });
                if (await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(150))) != work)
                    Check("attack: the server attack tests finish in time", false, "still running after 150 seconds, at: " + _stage);
                else await work;
            }
            finally
            {
                // Junctions are deleted as links, never followed into.
                try { Directory.Delete(sandbox, true); } catch { }
            }
            Console.WriteLine();
        }

        private static void Stage(string name, Action run)
        {
            _stage = name;
            try { run(); }
            catch (Exception ex) { Check("attack: " + name + " ran to the end", false, ex.ToString()); }
        }

        // ================= the sandbox: a share with ways out of it =================

        private sealed class Layout
        {
            public string Share = "", Outside = "";
            /// <summary>Client paths (forward slashes) that lead out of the share, by how.</summary>
            public readonly List<(string How, string Path)> WaysOut = new();
            /// <summary>A folder reached through a link that stays inside.</summary>
            public string? WayRound;
            public string? OutFolder;       // "way out", the junction's own name
        }

        private static Layout MakeShare(string sandbox, string name)
        {
            var l = new Layout
            {
                Share = Path.Combine(sandbox, name, "share"),
                Outside = Path.Combine(sandbox, name, "outside"),
            };
            Directory.CreateDirectory(Path.Combine(l.Share, "inner"));
            Directory.CreateDirectory(l.Outside);
            File.WriteAllText(Path.Combine(l.Share, "hello.txt"), "hello");
            File.WriteAllText(Path.Combine(l.Share, "victim.txt"), "victim");
            File.WriteAllText(Path.Combine(l.Share, "inner", "inner.txt"), "inner");
            File.WriteAllText(Path.Combine(l.Outside, "secret.txt"), Secret);

            if (Junction(Path.Combine(l.Share, "way out"), l.Outside))
            {
                l.OutFolder = "way out";
                l.WaysOut.Add(("a junction", "way out/secret.txt"));
                l.WaysOut.Add(("a junction spelt in capitals", "WAY OUT/secret.txt"));
                l.WaysOut.Add(("a junction's own stream", "way out::$INDEX_ALLOCATION/secret.txt"));
                if (ShortName(Path.Combine(l.Share, "way out")) is { } shortName)
                    l.WaysOut.Add(("a junction by its short 8.3 name", shortName + "/secret.txt"));
            }
            if (Junction(Path.Combine(l.Share, "way round"), Path.Combine(l.Share, "inner"))) l.WayRound = "way round";

            try
            {
                File.CreateSymbolicLink(Path.Combine(l.Share, "file out.txt"), Path.Combine(l.Outside, "secret.txt"));
                l.WaysOut.Add(("a symbolic link to a file", "file out.txt"));
            }
            catch { }                       // needs an administrator or Developer Mode
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(l.Share, "dir out"), l.Outside);
                l.WaysOut.Add(("a symbolic link to a folder", "dir out/secret.txt"));
            }
            catch { }
            return l;
        }

        /// <summary>Makes a junction with Windows' own mklink, by full path. False when it could not.</summary>
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint size);

        /// <summary>The 8.3 name of the last part of a path, or null when the volume makes none.</summary>
        private static string? ShortName(string path)
        {
            var into = new StringBuilder(1024);
            if (GetShortPathNameW(path, into, (uint)into.Capacity) == 0) return null;
            string shortLast = Path.GetFileName(into.ToString());
            return shortLast.Length == 0 || shortLast.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) ? null : shortLast;
        }

        private static void NoteLinks(Layout l)
        {
            if (l.OutFolder == null) Console.WriteLine("  NOTE  no junction could be made here: the link attacks were not run");
            if (!l.WaysOut.Any(w => w.How.StartsWith("a symbolic", StringComparison.Ordinal)))
                Console.WriteLine("  NOTE  Windows would not make a symbolic link here (no administrator or Developer Mode): only junctions were tried");
            if (!l.WaysOut.Any(w => w.How.Contains("8.3")))
                Console.WriteLine("  NOTE  this volume makes no 8.3 names: the short-name attack was not run");
        }

        private static bool OutsideUntouched(Layout l) =>
            File.ReadAllText(Path.Combine(l.Outside, "secret.txt")) == Secret &&
            Directory.EnumerateFileSystemEntries(l.Outside).Count() == 1;

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        // ================= a link leaving the shared folder =================

        private static void Links(string sandbox)
        {
            var ftpShare = MakeShare(sandbox, "ftp");
            NoteLinks(ftpShare);
            if (ftpShare.OutFolder != null) FtpLinks(ftpShare);
            var sftpShare = MakeShare(sandbox, "sftp");
            if (sftpShare.OutFolder != null) SftpLinks(sftpShare);
            var smbShare = MakeShare(sandbox, "smb");
            if (smbShare.OutFolder != null) SmbLinks(smbShare);
        }

        private static void FtpLinks(Layout l)
        {
            using var server = new FtpServer(l.Share, 0, User, Password, false);
            server.Start();
            using var ftp = new Ftp(server.Port);
            Check("attack ftp links: the test client signs in", ftp.SignIn(User, Password).StartsWith("230"));
            var (helloReply, hello) = ftp.Transfer("RETR hello.txt");
            Check("attack ftp links: and reads a shared file (so a refusal below means something)",
                helloReply.StartsWith("226") && Encoding.UTF8.GetString(hello) == "hello", helloReply);
            if (l.WayRound != null)
            {
                var (roundReply, round) = ftp.Transfer($"RETR {l.WayRound}/inner.txt");
                Check("attack ftp links: a junction that stays inside the share still works",
                    roundReply.StartsWith("226") && Encoding.UTF8.GetString(round) == "inner", roundReply);
            }

            var leaks = new List<string>();
            foreach (var (how, path) in l.WaysOut)
            {
                var (reply, data) = ftp.Transfer("RETR " + path);
                if (reply.StartsWith("226") || Encoding.UTF8.GetString(data).Contains(Secret)) leaks.Add($"RETR through {how}: {reply}");
                var size = ftp.Say("SIZE " + path);
                if (size.StartsWith("213")) leaks.Add($"SIZE through {how}: {size}");
                var facts = ftp.Say("MLST " + path);
                if (facts.StartsWith("250")) leaks.Add($"MLST through {how}: {facts}");
            }
            Check("attack ftp links: no link out of the share is read, sized or described (" + l.WaysOut.Count + " ways)",
                leaks.Count == 0, string.Join(" | ", leaks));

            var cwd = ftp.Say("CWD " + l.OutFolder);
            var (listReply, listing) = ftp.Transfer("LIST " + l.OutFolder);
            Check("attack ftp links: the junction cannot be entered or listed",
                !cwd.StartsWith("250") && !listReply.StartsWith("226") && !Encoding.UTF8.GetString(listing).Contains("secret"),
                cwd + " | " + listReply);
            var (rootReply, root) = ftp.Transfer("NLST");
            var names = Encoding.UTF8.GetString(root);
            Check("attack ftp links: and is not even shown in the folder it sits in",
                rootReply.StartsWith("226") && names.Contains("hello.txt") && !names.Contains(l.OutFolder!), names);

            var (storReply, _) = ftp.Transfer($"STOR {l.OutFolder}/planted.txt", Encoding.UTF8.GetBytes("planted"));
            var made = ftp.Say($"MKD {l.OutFolder}/made");
            var from = ftp.Say("RNFR victim.txt");
            var to = ftp.Say($"RNTO {l.OutFolder}/moved.txt");
            var dele = ftp.Say($"DELE {l.OutFolder}/secret.txt");
            Check("attack ftp links: nothing is written, made, moved or deleted through the junction",
                !storReply.StartsWith("226") && !made.StartsWith("257") && !to.StartsWith("250") && !dele.StartsWith("250") &&
                OutsideUntouched(l) && File.Exists(Path.Combine(l.Share, "victim.txt")),
                $"{storReply} | {made} | {from} {to} | {dele}");
        }

        private static void SftpLinks(Layout l)
        {
            using var server = new SftpServer(l.Share, 0, User, Password, false, SftpServer.NewHostKey());
            server.Start();
            using var ssh = new Ssh(server.Port);
            ssh.Handshake();
            Check("attack sftp links: the test client signs in", ssh.SignIn(User, Password) == 52);
            ssh.OpenSftp();
            Equal("attack sftp links: and reads a shared file (so a refusal below means something)", "hello", ssh.ReadFile("hello.txt") ?? "(refused)");
            if (l.WayRound != null)
                Equal("attack sftp links: a junction that stays inside the share still works", "inner", ssh.ReadFile(l.WayRound + "/inner.txt") ?? "(refused)");

            var leaks = new List<string>();
            foreach (var (how, path) in l.WaysOut)
            {
                if (ssh.ReadFile(path) is { } got) leaks.Add($"read through {how}: {got}");
                if (ssh.Request(17, new W().Str(path)).Type == 105) leaks.Add($"stat through {how}");
                if (ssh.Request(7, new W().Str(path)).Type == 105) leaks.Add($"lstat through {how}");
                if (ssh.ReadFile("/" + path) is { } rooted) leaks.Add($"read from / through {how}: {rooted}");
            }
            Check("attack sftp links: no link out of the share is read or looked at (" + l.WaysOut.Count + " ways)",
                leaks.Count == 0, string.Join(" | ", leaks));

            var opened = ssh.Request(11, new W().Str(l.OutFolder!));
            Check("attack sftp links: the junction cannot be listed", opened.Type != 102, opened.ToString());
            var shown = ssh.List("/");
            Check("attack sftp links: and is not even shown in the folder it sits in",
                shown.Contains("hello.txt") && !shown.Contains(l.OutFolder!), string.Join(", ", shown));

            var write = ssh.Request(3, new W().Str(l.OutFolder + "/planted.txt").U32(2 | 8 | 16).U32(0));
            var mkdir = ssh.Request(14, new W().Str(l.OutFolder + "/made").U32(0));
            var rename = ssh.Request(18, new W().Str("victim.txt").Str(l.OutFolder + "/moved.txt"));
            var remove = ssh.Request(13, new W().Str(l.OutFolder + "/secret.txt"));
            var link = ssh.Request(20, new W().Str("link.txt").Str("../outside/secret.txt"));
            Check("attack sftp links: nothing is written, made, moved, deleted or linked through the junction",
                write.Type != 102 && !mkdir.Ok && !rename.Ok && !remove.Ok && !link.Ok &&
                OutsideUntouched(l) && File.Exists(Path.Combine(l.Share, "victim.txt")) && !File.Exists(Path.Combine(l.Share, "link.txt")),
                $"{write} | {mkdir} | {rename} | {remove} | {link}");
        }

        private static readonly MethodInfo? SmbConnectOnPort = typeof(SMB2Client).GetMethod("Connect",
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(IPAddress), typeof(SMBTransportType), typeof(int) }, null);

        private static (SMB2Client Client, ISMBFileStore? Share, string Outcome) SmbOpen(IPAddress to, int port)
        {
            var client = new SMB2Client();
            if (SmbConnectOnPort == null || !(bool)SmbConnectOnPort.Invoke(client, new object[] { to, SMBTransportType.DirectTCPTransport, port })!)
                return (client, null, "could not connect");
            var status = client.Login("", User, Password);
            if (status != NTStatus.STATUS_SUCCESS) return (client, null, "login " + status);
            var store = client.TreeConnect("files", out status);
            return (client, status == NTStatus.STATUS_SUCCESS ? store : null, "share " + status);
        }

        private static string? SmbRead(ISMBFileStore share, string path)
        {
            var status = share.CreateFile(out var handle, out _, path, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                SMBLibrary.FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            if (status != NTStatus.STATUS_SUCCESS) return null;
            share.ReadFile(out var data, handle, 0, 65536);
            share.CloseFile(handle);
            return Encoding.UTF8.GetString(data ?? Array.Empty<byte>());
        }

        private static void SmbLinks(Layout l)
        {
            if (SmbFileServer.StartOnPort == null || SmbConnectOnPort == null)
            {
                Check("attack smb links: the library can still be started and reached on a chosen port", false);
                return;
            }
            int port = FreePort();
            using var server = new SmbFileServer(l.Share, port, "files", User, Password, readOnly: false);
            server.Start();
            var (client, share, outcome) = SmbOpen(IPAddress.Loopback, port);
            Check("attack smb links: the library's client opens the share", share != null, outcome);
            if (share == null) { client.Disconnect(); return; }
            try
            {
                Equal("attack smb links: and reads a shared file (so a refusal below means something)", "hello", SmbRead(share, "hello.txt") ?? "(refused)");

                // Release700Tests has the junction read, listed and written into.
                // These are the other spellings and the other kinds of link.
                var leaks = new List<string>();
                foreach (var (how, path) in l.WaysOut)
                {
                    if (SmbRead(share, path.Replace('/', '\\')) is { } got) leaks.Add($"read through {how}: {got}");
                    if (SmbRead(share, "\\" + path.Replace('/', '\\')) is { } rooted) leaks.Add($"read from \\ through {how}: {rooted}");
                }
                Check("attack smb links: no link out of the share is read, however it is spelt (" + l.WaysOut.Count + " ways)",
                    leaks.Count == 0, string.Join(" | ", leaks));

                // The link itself, opened as a link: a way to read where it points, or re-point it.
                var asLink = share.CreateFile(out var linkHandle, out _, l.OutFolder!, AccessMask.GENERIC_READ | AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE,
                    SMBLibrary.FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN,
                    CreateOptions.FILE_OPEN_REPARSE_POINT | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
                if (asLink == NTStatus.STATUS_SUCCESS) share.CloseFile(linkHandle);
                Check("attack smb links: the junction itself cannot be opened as a link", asLink != NTStatus.STATUS_SUCCESS, asLink.ToString());

                // Moving a shared file out through the junction.
                var opened = share.CreateFile(out var victim, out _, "victim.txt", AccessMask.DELETE | AccessMask.SYNCHRONIZE,
                    SMBLibrary.FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                    CreateDisposition.FILE_OPEN, CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
                NTStatus moved = NTStatus.STATUS_ACCESS_DENIED;
                if (opened == NTStatus.STATUS_SUCCESS)
                {
                    moved = share.SetFileInformation(victim, new FileRenameInformationType2 { FileName = l.OutFolder + "\\moved.txt", ReplaceIfExists = true });
                    share.CloseFile(victim);
                }
                Check("attack smb links: a shared file cannot be moved out through the junction",
                    opened == NTStatus.STATUS_SUCCESS && moved != NTStatus.STATUS_SUCCESS &&
                    File.Exists(Path.Combine(l.Share, "victim.txt")) && OutsideUntouched(l), $"open {opened}, rename {moved}");
            }
            finally { client.Disconnect(); }
        }

        // ================= many connections that never sign in =================

        /// <summary>
        /// An address of this PC that is not 127.x, so the server sees a caller
        /// the per-address limit applies to. Tailscale's own addresses are left
        /// alone. Null when this PC has none.
        /// </summary>
        private static IPAddress? OtherAddress()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        var a = unicast.Address;
                        if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a) || IsTailscale(a)) continue;
                        if (a.GetAddressBytes()[0] == 169) continue;    // no address handed out
                        return a;
                    }
                }
            }
            catch { }
            return null;
        }

        private static bool IsTailscale(IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            if (a.AddressFamily == AddressFamily.InterNetwork) return b[0] == 100 && (b[1] & 0xC0) == 64;      // 100.64.0.0/10
            return b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;  // fd7a:115c:a1e0::/48
        }

        /// <summary>
        /// Keeps writing to every connection until the server has really let go
        /// of it (a write fails once it has closed its end), and says how long
        /// the slowest one took, or -1 if one was still held at the end.
        /// </summary>
        private static long AllLetGo(IReadOnlyList<Socket> sockets, Func<int, byte[]> drip, int giveUpMs)
        {
            var clock = Stopwatch.StartNew();
            var open = new HashSet<int>(Enumerable.Range(0, sockets.Count));
            while (open.Count > 0 && clock.ElapsedMilliseconds < giveUpMs)
            {
                foreach (int i in open.ToList())
                {
                    try
                    {
                        sockets[i].Send(drip(i));
                        // Anything the server said is read off, so its goodbye read sees no backlog from us.
                        while (sockets[i].Available > 0) sockets[i].Receive(new byte[4096]);
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { open.Remove(i); }
                }
                if (open.Count > 0) Thread.Sleep(200);
            }
            return open.Count == 0 ? clock.ElapsedMilliseconds : -1;
        }

        private static void Crowds(string sandbox)
        {
            var other = OtherAddress();
            if (other == null)
            {
                Console.WriteLine("  NOTE  this PC has no address besides 127.x: the per-address limit was not attacked");
                return;
            }
            var folder = Path.Combine(sandbox, "crowd");
            Directory.CreateDirectory(folder);

            // ---- FTP
            using (var server = new FtpServer(folder, 0, User, Password, false) { SignInWithinMs = 1500 })
            {
                server.Start();
                var crowd = new List<Ftp>();
                try
                {
                    Ftp first;
                    try { first = new Ftp(server.Port, other, other); }
                    catch (SocketException ex)
                    {
                        Console.WriteLine($"  NOTE  this PC would not connect to its own address {other} ({ex.SocketErrorCode}): the per-address limit was not attacked");
                        return;
                    }
                    crowd.Add(first);
                    for (int i = 1; i < FtpServer.MaxPerAddress; i++) crowd.Add(new Ftp(server.Port, other, other));
                    Check($"attack ftp crowd: {FtpServer.MaxPerAddress} connections from one address are all greeted",
                        crowd.All(c => c.Greeting.StartsWith("220")), string.Join(" | ", crowd.Select(c => c.Greeting).Distinct()));
                    using (var extra = new Ftp(server.Port, other, other))
                        Check("attack ftp crowd: the next one from that address is turned away in a sentence",
                            extra.Greeting.StartsWith("421") && extra.Greeting.Contains("Too many connections"), extra.Greeting);
                    using (var own = new Ftp(server.Port))
                        Check("attack ftp crowd: while this PC's own address (Tailscale's way in) still gets in", own.Greeting.StartsWith("220"), own.Greeting);

                    // None of them signs in. Each says NOOP every so often, which used to keep a place for ever.
                    long gone = AllLetGo(crowd.Select(c => c.Socket).ToList(), _ => Encoding.ASCII.GetBytes("NOOP\r\n"), 9000);
                    Check("attack ftp crowd: every one is ended at the one sign-in deadline, NOOPs or not",
                        gone > 1000 && gone < 7000, gone + " ms (deadline 1500 ms, then at most two seconds of goodbye)");
                }
                finally { foreach (var c in crowd) c.Dispose(); }

                bool back = false;
                for (var until = Environment.TickCount64 + 5000; !back && Environment.TickCount64 < until; Thread.Sleep(200))
                {
                    using var again = new Ftp(server.Port, other, other);
                    back = again.Greeting.StartsWith("220");
                }
                Check("attack ftp crowd: and their places come back: the address is greeted again", back);
            }

            // ---- SFTP
            using (var server = new SftpServer(folder, 0, User, Password, false, SftpServer.NewHostKey()) { BeforeSignInMs = 1500 })
            {
                server.Start();
                var crowd = new List<Ssh>();
                try
                {
                    for (int i = 0; i < SftpServer.MaxPerAddress; i++)
                    {
                        var c = new Ssh(server.Port, other, other);
                        crowd.Add(c);
                        c.ReadBanner();
                    }
                    Check($"attack sftp crowd: {SftpServer.MaxPerAddress} connections from one address are all answered",
                        crowd.All(c => c.Banner.StartsWith("SSH-2.0-")), crowd.Count(c => c.Banner.StartsWith("SSH-2.0-")) + " answered");
                    using (var extra = new Ssh(server.Port, other, other))
                        Check("attack sftp crowd: the next one from that address is hung up on before a word", !extra.ReadBanner(), extra.Banner);
                    using (var own = new Ssh(server.Port))
                        Check("attack sftp crowd: while this PC's own address (Tailscale's way in) still gets in", own.ReadBanner(), own.Banner);

                    // Each finishes its version line a byte at a time, never.
                    long gone = AllLetGo(crowd.Select(c => c.Socket).ToList(), _ => new[] { (byte)'x' }, 9000);
                    Check("attack sftp crowd: every one is ended at the one sign-in deadline, however it drips",
                        gone > 1000 && gone < 7000, gone + " ms (deadline 1500 ms, then at most two seconds of goodbye)");
                }
                finally { foreach (var c in crowd) c.Dispose(); }

                bool back = false;
                for (var until = Environment.TickCount64 + 5000; !back && Environment.TickCount64 < until; Thread.Sleep(200))
                {
                    using var again = new Ssh(server.Port, other, other);
                    back = again.ReadBanner();
                }
                Check("attack sftp crowd: and their places come back: the address is answered again", back);
            }

            // ---- SFTP: the one deadline covers the key exchange and after, not just the first line
            using (var server = new SftpServer(folder, 0, User, Password, false, SftpServer.NewHostKey()) { BeforeSignInMs = 1500 })
            {
                server.Start();
                using var stalling = new Ssh(server.Port);
                stalling.Handshake();
                var clock = Stopwatch.StartNew();
                bool closed = false;
                while (!closed && clock.ElapsedMilliseconds < 9000)
                {
                    try { stalling.SendIgnore(); Thread.Sleep(200); }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { closed = true; }
                }
                Check("attack sftp: keys exchanged, then only \"ignore\" messages: ended at the same sign-in deadline",
                    closed && clock.ElapsedMilliseconds < 7000, clock.ElapsedMilliseconds + " ms");

                using var rekeyer = new Ssh(server.Port);
                rekeyer.Handshake();
                var asked = Stopwatch.StartNew();
                string what;
                try { rekeyer.Exchange(); what = "the server went along with it"; }
                catch (Exception ex) when (ex is IOException or SocketException) { what = ex.Message; }
                Check("attack sftp: a second key exchange before signing in is refused at once (public-key sums for nothing)",
                    what != "the server went along with it" && asked.ElapsedMilliseconds < 1500, what + " after " + asked.ElapsedMilliseconds + " ms");
            }
            using (var server = new SftpServer(folder, 0, User, Password, false, SftpServer.NewHostKey()))
            {
                server.Start();
                File.WriteAllText(Path.Combine(folder, "after.txt"), "fresh keys");
                using var signed = new Ssh(server.Port);
                signed.Handshake();
                bool inside = signed.SignIn(User, Password) == 52;
                string read;
                try { signed.Exchange(); signed.OpenSftp(); read = signed.ReadFile("after.txt") ?? "(refused)"; }
                catch (Exception ex) { read = ex.Message; }
                Check("attack sftp: once signed in, fresh keys are given (so the refusal above is about signing in)",
                    inside && read == "fresh keys", read);
            }
        }

        // ================= a finished connection kept open =================

        private static void Goodbyes(string sandbox)
        {
            var folder = Path.Combine(sandbox, "goodbye");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "hello.txt"), "hello");

            // FTP's QUIT is in Release701Tests. These end the other ways.
            using (var server = new FtpServer(folder, 0, User, Password, false))
            {
                server.Start();
                using var guesser = new Ftp(server.Port);
                string last = "";
                for (int i = 0; i < 3; i++) last = guesser.SignIn(User, "wrong password " + i);
                long held = AllLetGo(new[] { guesser.Socket }, _ => new[] { (byte)'x' }, 9000);
                Check("attack ftp goodbye: hung up on for wrong passwords, a caller still dripping bytes is let go within seconds",
                    last.StartsWith("421") && held >= 0 && held < 4000, $"{last}; {held} ms");
            }

            using (var server = new SftpServer(folder, 0, User, Password, false, SftpServer.NewHostKey()))
            {
                server.Start();
                using var done = new Ssh(server.Port);
                done.Handshake();
                bool inside = done.SignIn(User, Password) == 52;
                done.OpenSftp();
                bool worked = done.ReadFile("hello.txt") == "hello";
                done.SendDisconnect();
                long held = AllLetGo(new[] { done.Socket }, _ => new[] { (byte)'x' }, 9000);
                Check("attack sftp goodbye: after the client says goodbye, dripping bytes keeps nothing open past two seconds",
                    inside && worked && held >= 0 && held < 4000, held + " ms");

                using var guesser = new Ssh(server.Port);
                guesser.Handshake();
                int answer = 0;
                for (int i = 0; i < 3 && answer != 1; i++) answer = guesser.SignIn(User, "wrong password " + i);
                long cut = AllLetGo(new[] { guesser.Socket }, _ => new[] { (byte)'x' }, 9000);
                Check("attack sftp goodbye: hung up on for wrong passwords, a caller still dripping bytes is let go within seconds",
                    answer == 1 && cut >= 0 && cut < 4000, $"{guesser.Goodbye}; {cut} ms");
            }
        }

        // ================= password guessing from many addresses =================

        /// <summary>127.0.0.n: every one is this PC, and each is a different address to the servers.</summary>
        private static IPAddress Loop(int n) => new(new byte[] { 127, 0, (byte)(n >> 8), (byte)n });

        private static void Guessing(string sandbox)
        {
            var folder = Path.Combine(sandbox, "guess");
            Directory.CreateDirectory(folder);

            // Windows answers on all of 127.0.0.0/8 without being told to; if it
            // will not here, there is no way to be many addresses at once.
            try { using var probe = new TcpClient(new IPEndPoint(Loop(2), 0)); }
            catch (SocketException ex)
            {
                Console.WriteLine($"  NOTE  this PC will not send from 127.0.0.2 ({ex.SocketErrorCode}): guessing from many addresses was not attacked");
                return;
            }

            // ---- FTP: the brake
            using (var server = new FtpServer(folder, 0, User, Password, false))
            {
                server.Start();
                long Answer(int from, string password, out string reply)
                {
                    using var c = new Ftp(server.Port, Loop(from));
                    c.Say("USER " + User);
                    var clock = Stopwatch.StartNew();
                    reply = c.Say("PASS " + password);
                    return clock.ElapsedMilliseconds;
                }

                long before = Answer(300, Password, out var right);
                Check("attack ftp guessing: before anyone guesses, a right password is answered at once", right.StartsWith("230") && before < 1000, $"{right} in {before} ms");

                var answers = new List<string>();
                for (int i = 0; i < SignInThrottle.BrakeAfter; i++)
                {
                    Answer(10 + i, "guess " + i, out var reply);
                    answers.Add(reply);
                }
                Check($"attack ftp guessing: {SignInThrottle.BrakeAfter} guesses, each from its own address, each told no",
                    answers.All(a => a.StartsWith("530")), string.Join(" | ", answers.Distinct()));
                using (var guesser = new Ftp(server.Port, Loop(10)))
                    Check("attack ftp guessing: none of those addresses is shut out on its own (one guess each)", guesser.Greeting.StartsWith("220"), guesser.Greeting);

                long braked = Answer(301, Password, out var slowRight);
                long wrongBraked = Answer(302, "one more guess", out var slowWrong);
                Check("attack ftp guessing: now every password answer waits, a right one included",
                    slowRight.StartsWith("230") && braked >= SignInThrottle.BrakeDelay.TotalMilliseconds - 200, $"{slowRight} in {braked} ms");
                Check("attack ftp guessing: and a wrong one waits just as long, so the wait gives nothing away",
                    slowWrong.StartsWith("530") && Math.Abs(wrongBraked - braked) < 1000, $"right {braked} ms, wrong {wrongBraked} ms");
            }

            // ---- FTP: a lockout outlives a flood of other addresses
            using (var server = new FtpServer(folder, 0, User, Password, false))
            {
                server.Start();
                string last = "";
                for (int tries = 0; tries < SignInThrottle.MaxFailures;)
                {
                    using var c = new Ftp(server.Port, Loop(77));
                    for (int i = 0; i < 3 && tries < SignInThrottle.MaxFailures; i++, tries++) last = c.SignIn(User, "guess " + tries);
                }
                Check("attack ftp lockout: ten wrong passwords over the wire shut the address out",
                    last.StartsWith("421") && last.Contains("Too many wrong passwords"), last);
                // Thousands of other addresses each getting one wrong. Real
                // connections from 4,096 addresses would take minutes behind the
                // brake; the server's own throttle takes the same notes they would.
                for (int i = 0; i < 4096; i++) server.Throttle.Failed(new IPAddress(new byte[] { 10, 1, (byte)(i >> 8), (byte)i }));
                using (var back = new Ftp(server.Port, Loop(77)))
                    Check("attack ftp lockout: after a flood of 4,096 other addresses the guesser is still turned away at the door",
                        back.Greeting.StartsWith("421") && back.Greeting.Contains("Too many wrong passwords"), back.Greeting);
                using (var other = new Ftp(server.Port, Loop(78)))
                    Check("attack ftp lockout: while an address that never guessed is greeted", other.Greeting.StartsWith("220"), other.Greeting);
            }

            // ---- SFTP: the brake
            var key = SftpServer.NewHostKey();
            using (var server = new SftpServer(folder, 0, User, Password, false, key))
            {
                server.Start();
                long Answer(int from, string password, out int reply)
                {
                    using var c = new Ssh(server.Port, Loop(from));
                    c.Handshake();
                    c.StartUserAuth();
                    var clock = Stopwatch.StartNew();
                    reply = c.SignIn(User, password);
                    return clock.ElapsedMilliseconds;
                }

                long before = Answer(300, Password, out int right);
                Check("attack sftp guessing: before anyone guesses, a right password is answered at once", right == 52 && before < 1000, $"{right} in {before} ms");
                var answers = new List<int>();
                for (int i = 0; i < SignInThrottle.BrakeAfter; i++)
                {
                    Answer(10 + i, "guess " + i, out int reply);
                    answers.Add(reply);
                }
                Check($"attack sftp guessing: {SignInThrottle.BrakeAfter} guesses, each from its own address, each told no",
                    answers.All(a => a == 51), string.Join(",", answers.Distinct()));
                long braked = Answer(301, Password, out int slowRight);
                long wrongBraked = Answer(302, "one more guess", out int slowWrong);
                Check("attack sftp guessing: now every password answer waits, a right one included",
                    slowRight == 52 && braked >= SignInThrottle.BrakeDelay.TotalMilliseconds - 200, $"{slowRight} in {braked} ms");
                Check("attack sftp guessing: and a wrong one waits just as long, so the wait gives nothing away",
                    slowWrong == 51 && Math.Abs(wrongBraked - braked) < 1000, $"right {braked} ms, wrong {wrongBraked} ms");
            }

            // ---- SFTP: a lockout outlives a flood of other addresses
            using (var server = new SftpServer(folder, 0, User, Password, false, key))
            {
                server.Start();
                int tries = 0;
                string said = "";
                while (tries < SignInThrottle.MaxFailures)
                {
                    using var c = new Ssh(server.Port, Loop(77));
                    c.Handshake();
                    while (tries < SignInThrottle.MaxFailures)
                    {
                        tries++;
                        if (c.SignIn(User, "guess " + tries) == 1) { said = c.Goodbye; break; }
                    }
                }
                Check("attack sftp lockout: ten wrong passwords over the wire shut the address out", said.Contains("Too many wrong passwords"), said);
                for (int i = 0; i < 4096; i++) server.Throttle.Failed(new IPAddress(new byte[] { 10, 2, (byte)(i >> 8), (byte)i }));
                string door;
                using (var back = new Ssh(server.Port, Loop(77)))
                {
                    try { back.Handshake(); door = "(let in to the key exchange)"; }
                    catch (IOException ex) { door = ex.Message; }
                }
                Check("attack sftp lockout: after a flood of 4,096 other addresses the guesser is still turned away before the key exchange",
                    door.Contains("Too many wrong passwords"), door);
                using (var other = new Ssh(server.Port, Loop(78)))
                {
                    other.Handshake();
                    Check("attack sftp lockout: while an address that never guessed signs in", other.SignIn(User, Password) == 52);
                }
            }
        }

        // ================= where the servers listen =================

        /// <summary>
        /// Every address of this PC a device could reach it at: the loopback
        /// addresses (Tailscale's forward arrives on 127.0.0.1) and each
        /// network adapter's own. Tailscale's addresses are left out: the
        /// forward, not the server, answers there, and the tests never touch it.
        /// </summary>
        private static List<IPAddress> AddressesOfThisPc()
        {
            var all = new List<IPAddress> { IPAddress.Loopback };
            if (Socket.OSSupportsIPv6) all.Add(IPAddress.IPv6Loopback);
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    // Tailscale's adapter, link-local address and all: its traffic is Tailscale's.
                    if (nic.GetIPProperties().UnicastAddresses.Any(u => IsTailscale(u.Address))) continue;
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        var a = unicast.Address;
                        if (IPAddress.IsLoopback(a)) continue;
                        if (a.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6) continue;
                        if (unicast.DuplicateAddressDetectionState != DuplicateAddressDetectionState.Preferred) continue;
                        all.Add(a);
                    }
                }
            }
            catch { }
            return all;
        }

        private static bool Reaches(IPAddress address, int port)
        {
            try
            {
                using var tcp = new TcpClient(address.AddressFamily);
                return tcp.ConnectAsync(address, port).Wait(3000) && tcp.Connected;
            }
            catch { return false; }
        }

        private static string Source(string name)
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++, dir = Path.GetDirectoryName(dir))
            {
                var path = Path.Combine(dir, "src", name);
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            return "";
        }

        private static IPEndPoint? Bound(object server)
        {
            var field = server.GetType().GetField("_listener", BindingFlags.NonPublic | BindingFlags.Instance);
            return (field?.GetValue(server) as TcpListener)?.LocalEndpoint as IPEndPoint;
        }

        /// <summary>Every address, of both families where this PC has IPv6 (both reached is checked by connecting).</summary>
        private static bool Everywhere(IPEndPoint? at) =>
            at != null && (Socket.OSSupportsIPv6 ? at.Address.Equals(IPAddress.IPv6Any) : at.Address.Equals(IPAddress.Any));

        private static void Listening(string sandbox)
        {
            // What the pages say. Over Tailscale: the forward hands each
            // connection to 127.0.0.1. Everyone else: "every network this PC
            // joins", once Windows Firewall lets them through. So each server
            // must answer on 127.0.0.1 and on every address of every adapter,
            // IPv4 and IPv6, and the three must agree.
            var form = Source("SettingsForm.cs");
            var forward = Source("TailscaleForward.cs");
            if (form.Length > 0 && forward.Length > 0)
            {
                Check("attack listening: the pages say the servers are reached on every network this PC joins",
                    form.Contains("every network this PC joins"));
                Check("attack listening: and that Tailscale opens nothing to the internet, not that nothing is open",
                    form.Contains("Tailscale opens nothing to the internet") && !form.Contains("and nothing is opened to the internet"));
                Check("attack listening: Tailscale's forward goes to 127.0.0.1", forward.Contains("tcp://127.0.0.1:"));
            }
            else Console.WriteLine("  (sources not beside the tests: the pages' words were not checked)");

            var folder = Path.Combine(sandbox, "listen");
            Directory.CreateDirectory(folder);
            var addresses = AddressesOfThisPc();
            Console.WriteLine("  addresses tried: " + string.Join(", ", addresses));

            using (var ftp = new FtpServer(folder, 0, User, Password, false))
            {
                ftp.Start();
                var at = Bound(ftp);
                Check("attack listening: FTP listens on every address, not one", Everywhere(at), at?.ToString());
                var missed = addresses.Where(a => !Reaches(a, ftp.Port)).ToList();
                Check("attack listening: FTP answers on 127.0.0.1 and every address of this PC", missed.Count == 0, "not on " + string.Join(", ", missed));
            }
            using (var sftp = new SftpServer(folder, 0, User, Password, false, SftpServer.NewHostKey()))
            {
                sftp.Start();
                var at = Bound(sftp);
                Check("attack listening: SFTP listens on every address, not one", Everywhere(at), at?.ToString());
                var missed = addresses.Where(a => !Reaches(a, sftp.Port)).ToList();
                Check("attack listening: SFTP answers on 127.0.0.1 and every address of this PC", missed.Count == 0, "not on " + string.Join(", ", missed));
            }
            if (SmbFileServer.StartOnPort == null) return;
            int port = FreePort();
            using (var smb = new SmbFileServer(folder, port, "files", User, Password, false))
            {
                smb.Start();
                Check("attack listening: SMB listens on IPv6 too, as FTP and SFTP do", smb.ListensOnIPv6 || !Socket.OSSupportsIPv6);
                var missed = addresses.Where(a => !Reaches(a, port)).ToList();
                Check("attack listening: SMB answers on 127.0.0.1 and every address of this PC", missed.Count == 0, "not on " + string.Join(", ", missed));
                if (Socket.OSSupportsIPv6 && SmbConnectOnPort != null)
                {
                    var (client, share, outcome) = SmbOpen(IPAddress.IPv6Loopback, port);
                    Check("attack listening: and over IPv6 it is the same share behind the same password", share != null, outcome);
                    client.Disconnect();
                    var (bad, none, refused) = (new SMB2Client(), (ISMBFileStore?)null, "");
                    if ((bool)SmbConnectOnPort.Invoke(bad, new object[] { IPAddress.IPv6Loopback, SMBTransportType.DirectTCPTransport, port })!)
                        refused = bad.Login("", User, "wrong password").ToString();
                    bad.Disconnect();
                    Check("attack listening: and a wrong password is refused there too", refused.Length > 0 && refused != nameof(NTStatus.STATUS_SUCCESS), refused);
                }
            }
        }

        // ================= a raw FTP client =================

        private sealed class Ftp : IDisposable
        {
            private readonly TcpClient _tcp;
            private readonly NetworkStream _stream;
            private readonly IPAddress _to;
            public string Greeting = "";
            public Socket Socket => _tcp.Client;

            public Ftp(int port, IPAddress? from = null, IPAddress? to = null)
            {
                _to = to ?? IPAddress.Loopback;
                _tcp = from == null ? new TcpClient(_to.AddressFamily) : new TcpClient(new IPEndPoint(from, 0));
                _tcp.NoDelay = true;
                _tcp.ReceiveTimeout = 8000;
                _tcp.SendTimeout = 8000;
                try { _tcp.Connect(_to, port); }
                catch { _tcp.Dispose(); throw; }
                _stream = _tcp.GetStream();
                try { Greeting = Read(); } catch { Greeting = "(nothing)"; }
            }

            private string Line()
            {
                var bytes = new List<byte>();
                while (true)
                {
                    int b = _stream.ReadByte();
                    if (b < 0) throw new IOException("closed");
                    if (b == '\n') break;
                    bytes.Add((byte)b);
                }
                return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
            }

            public string Read()
            {
                while (true)
                {
                    var line = Line();
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

            public string SignIn(string user, string password)
            {
                var u = Say("USER " + user);
                return u.StartsWith("331") ? Say("PASS " + password) : u;
            }

            /// <summary>One command over a transfer port: its last reply, and what came over the data connection.</summary>
            public (string Reply, byte[] Data) Transfer(string command, byte[]? upload = null)
            {
                var epsv = Say("EPSV");
                int open = epsv.IndexOf("(|||", StringComparison.Ordinal), close = epsv.IndexOf("|)", StringComparison.Ordinal);
                if (!epsv.StartsWith("229") || open < 0 || close < 0) return (epsv, Array.Empty<byte>());
                int port = int.Parse(epsv[(open + 4)..close]);

                using var data = new TcpClient(_to.AddressFamily) { ReceiveTimeout = 8000, SendTimeout = 8000 };
                data.Connect(_to, port);
                var first = Say(command);
                if (!first.StartsWith("1")) return (first, Array.Empty<byte>());
                var got = new MemoryStream();
                try
                {
                    if (upload != null)
                    {
                        data.GetStream().Write(upload, 0, upload.Length);
                        data.Client.Shutdown(SocketShutdown.Send);
                    }
                    else data.GetStream().CopyTo(got);
                }
                catch (IOException) { }
                data.Close();
                try { return (Read(), got.ToArray()); }
                catch (IOException) { return ("(closed)", got.ToArray()); }
            }

            public void Dispose() { try { _tcp.Dispose(); } catch { } }
        }

        // ================= an SSH client, just enough for SFTP =================

        /// <summary>Builds an SSH message: big-endian numbers, length-prefixed strings.</summary>
        private sealed class W
        {
            private readonly MemoryStream _bytes = new();
            public W U8(byte v) { _bytes.WriteByte(v); return this; }
            public W U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); _bytes.Write(b); return this; }
            public W U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); _bytes.Write(b); return this; }
            public W Raw(ReadOnlySpan<byte> b) { _bytes.Write(b); return this; }
            public W Str(ReadOnlySpan<byte> b) { U32((uint)b.Length); return Raw(b); }
            public W Str(string s) => Str(Encoding.UTF8.GetBytes(s));
            public W MpInt(ReadOnlySpan<byte> magnitude)
            {
                while (magnitude.Length > 0 && magnitude[0] == 0) magnitude = magnitude.Slice(1);
                bool pad = magnitude.Length > 0 && (magnitude[0] & 0x80) != 0;
                U32((uint)(magnitude.Length + (pad ? 1 : 0)));
                if (pad) U8(0);
                return Raw(magnitude);
            }
            public byte[] ToArray() => _bytes.ToArray();
        }

        private sealed class R
        {
            private readonly byte[] _b;
            private int _at;
            public R(byte[] b, int at) { _b = b; _at = at; }
            public byte U8() => _b[_at++];
            public uint U32() { uint v = BinaryPrimitives.ReadUInt32BigEndian(_b.AsSpan(_at)); _at += 4; return v; }
            public ulong U64() { ulong v = BinaryPrimitives.ReadUInt64BigEndian(_b.AsSpan(_at)); _at += 8; return v; }
            public byte[] Bytes() { int n = (int)U32(); var v = _b.AsSpan(_at, n).ToArray(); _at += n; return v; }
            public string Text() => Encoding.UTF8.GetString(Bytes());
        }

        /// <summary>One SFTP answer: its type (101 status, 102 handle, 103 data, 104 names, 105 attributes) and what it held.</summary>
        private sealed record Answer(byte Type, uint Code, byte[] Bytes, List<string> Names)
        {
            public bool Ok => Type == 101 && Code == 0;
            public override string ToString() => Type == 101 ? "status " + Code : "type " + Type;
        }

        private sealed class Ssh : IDisposable
        {
            private const string Version = "SSH-2.0-ExplorerNativeAttackTest";
            private readonly TcpClient _tcp;
            private readonly NetworkStream _s;
            public string Banner = "";
            public string Goodbye = "";
            public Socket Socket => _tcp.Client;

            private byte[]? _sessionId;
            private Aes? _aesOut, _aesIn;
            private byte[] _ctrOut = Array.Empty<byte>(), _ctrIn = Array.Empty<byte>();
            private IncrementalHash? _macOut, _macIn;
            private uint _seqOut, _seqIn;
            private bool _service;
            private uint _remote;
            private readonly MemoryStream _sftpIn = new();
            private uint _id = 1;

            public Ssh(int port, IPAddress? from = null, IPAddress? to = null)
            {
                to ??= IPAddress.Loopback;
                _tcp = from == null ? new TcpClient(to.AddressFamily) : new TcpClient(new IPEndPoint(from, 0));
                _tcp.NoDelay = true;
                _tcp.ReceiveTimeout = 8000;
                _tcp.SendTimeout = 8000;
                try { _tcp.Connect(to, port); }
                catch { _tcp.Dispose(); throw; }
                _s = _tcp.GetStream();
            }

            /// <summary>Reads the server's version line. False when it hung up without one.</summary>
            public bool ReadBanner()
            {
                try
                {
                    var line = new StringBuilder();
                    int b;
                    while ((b = _s.ReadByte()) >= 0 && b != '\n' && line.Length < 300) line.Append((char)b);
                    Banner = line.ToString().TrimEnd('\r');
                }
                catch (Exception ex) when (ex is IOException or SocketException) { }
                return Banner.StartsWith("SSH-2.0-", StringComparison.Ordinal);
            }

            public void Handshake()
            {
                if (Banner.Length == 0 && !ReadBanner()) throw new IOException("The server hung up before saying anything.");
                _s.Write(Encoding.ASCII.GetBytes(Version + "\r\n"));
                Exchange();
            }

            private static byte[] KexInit() => new W().U8(20).Raw(RandomNumberGenerator.GetBytes(16))
                .Str("ecdh-sha2-nistp256").Str("ecdsa-sha2-nistp256").Str("aes128-ctr").Str("aes128-ctr")
                .Str("hmac-sha2-256").Str("hmac-sha2-256").Str("none").Str("none").Str("").Str("").U8(0).U32(0).ToArray();

            private static byte[] Point(ECPoint q)
            {
                var p = new byte[65];
                p[0] = 4;
                q.X!.CopyTo(p, 1 + 32 - q.X.Length);
                q.Y!.CopyTo(p, 33 + 32 - q.Y.Length);
                return p;
            }

            /// <summary>A key exchange, the first or a fresh one. Throws IOException when the server refuses or hangs up.</summary>
            public void Exchange()
            {
                byte[] ours = KexInit();
                Send(ours);
                byte[] theirs = Expect(20);
                using var mine = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                byte[] q = Point(mine.ExportParameters(false).Q);
                Send(new W().U8(30).Str(q).ToArray());
                var reply = new R(Expect(31), 1);
                byte[] hostKey = reply.Bytes(), theirPoint = reply.Bytes();
                using var other = ECDiffieHellman.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = theirPoint.AsSpan(1, 32).ToArray(), Y = theirPoint.AsSpan(33, 32).ToArray() },
                });
                byte[] secret = mine.DeriveRawSecretAgreement(other.PublicKey);
                byte[] hash = SHA256.HashData(new W().Str(Encoding.ASCII.GetBytes(Version)).Str(Encoding.ASCII.GetBytes(Banner))
                    .Str(ours).Str(theirs).Str(hostKey).Str(q).Str(theirPoint).MpInt(secret).ToArray());
                _sessionId ??= hash;
                byte[] k = new W().MpInt(secret).ToArray();
                byte[] session = _sessionId;
                byte[] Derive(char letter, int count) =>
                    SHA256.HashData(new W().Raw(k).Raw(hash).U8((byte)letter).Raw(session).ToArray()).AsSpan(0, count).ToArray();

                Expect(21);
                Send(new byte[] { 21 });
                _aesOut?.Dispose(); _aesIn?.Dispose(); _macOut?.Dispose(); _macIn?.Dispose();
                _aesOut = Aes.Create(); _aesOut.Key = Derive('C', 16); _ctrOut = Derive('A', 16);
                _macOut = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Derive('E', 32));
                _aesIn = Aes.Create(); _aesIn.Key = Derive('D', 16); _ctrIn = Derive('B', 16);
                _macIn = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Derive('F', 32));
            }

            private static void Ctr(Aes aes, byte[] counter, Span<byte> data)
            {
                var counters = new byte[data.Length];
                for (int at = 0; at < data.Length; at += 16)
                {
                    counter.CopyTo(counters, at);
                    for (int i = 15; i >= 0; i--) if (++counter[i] != 0) break;
                }
                var stream = aes.EncryptEcb(counters, PaddingMode.None);
                for (int i = 0; i < data.Length; i++) data[i] ^= stream[i];
            }

            private void Send(byte[] payload)
            {
                int block = _aesOut == null ? 8 : 16;
                int padding = block - (payload.Length + 5) % block;
                if (padding < 4) padding += block;
                int length = 1 + payload.Length + padding;
                var packet = new byte[4 + length];
                BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)length);
                packet[4] = (byte)padding;
                payload.CopyTo(packet, 5);
                RandomNumberGenerator.Fill(packet.AsSpan(5 + payload.Length, padding));
                byte[] mac = Array.Empty<byte>();
                if (_aesOut != null)
                {
                    Span<byte> number = stackalloc byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(number, _seqOut);
                    _macOut!.AppendData(number);
                    _macOut.AppendData(packet);
                    mac = _macOut.GetHashAndReset();
                    Ctr(_aesOut, _ctrOut, packet);
                }
                _seqOut++;
                _s.Write(packet);
                if (mac.Length > 0) _s.Write(mac);
            }

            private void ReadExactly(Span<byte> into)
            {
                while (into.Length > 0)
                {
                    int n = _s.Read(into);
                    if (n <= 0) throw new IOException("The server hung up.");
                    into = into.Slice(n);
                }
            }

            private byte[] ReadPacket()
            {
                byte[] packet;
                if (_aesIn == null)
                {
                    var head = new byte[4];
                    ReadExactly(head);
                    int length = (int)BinaryPrimitives.ReadUInt32BigEndian(head);
                    if (length < 5 || length > 300_000) throw new IOException("A packet of an impossible size.");
                    packet = new byte[4 + length];
                    head.CopyTo(packet, 0);
                    ReadExactly(packet.AsSpan(4));
                }
                else
                {
                    var head = new byte[16];
                    ReadExactly(head);
                    Ctr(_aesIn, _ctrIn, head);
                    int length = (int)BinaryPrimitives.ReadUInt32BigEndian(head);
                    if (length < 12 || length > 300_000) throw new IOException("A packet of an impossible size.");
                    packet = new byte[4 + length];
                    head.CopyTo(packet, 0);
                    ReadExactly(packet.AsSpan(16));
                    Ctr(_aesIn, _ctrIn, packet.AsSpan(16));
                    ReadExactly(new byte[32]);        // the MAC: this end is the test, not the thing tested
                }
                _seqIn++;
                int padding = packet[4];
                return packet.AsSpan(5, packet.Length - 5 - padding).ToArray();
            }

            /// <summary>The next message that matters. A disconnect is returned (type 1) with its words in <see cref="Goodbye"/>.</summary>
            private byte[] Next()
            {
                while (true)
                {
                    byte[] p = ReadPacket();
                    if (p[0] is 2 or 4) continue;
                    if (p[0] == 1)
                    {
                        var r = new R(p, 1);
                        r.U32();
                        Goodbye = r.Text();
                    }
                    return p;
                }
            }

            private byte[] Expect(byte type)
            {
                byte[] p = Next();
                if (p[0] == 1) throw new IOException("The server said goodbye: " + Goodbye);
                if (p[0] != type) throw new IOException($"Expected message {type}, got {p[0]}.");
                return p;
            }

            public void StartUserAuth()
            {
                if (_service) return;
                Send(new W().U8(5).Str("ssh-userauth").ToArray());
                Expect(6);
                _service = true;
            }

            /// <summary>52 signed in, 51 refused, 1 hung up on (see <see cref="Goodbye"/>).</summary>
            public int SignIn(string user, string password)
            {
                try
                {
                    StartUserAuth();
                    Send(new W().U8(50).Str(user).Str("ssh-connection").Str("password").U8(0).Str(password).ToArray());
                    return Next()[0];
                }
                catch (Exception ex) when (ex is IOException or SocketException) { return 1; }
            }

            public void SendIgnore() => Send(new W().U8(2).Str("").ToArray());
            public void SendDisconnect() => Send(new W().U8(1).U32(11).Str("Bye.").Str("").ToArray());

            public void OpenSftp()
            {
                Send(new W().U8(90).Str("session").U32(0).U32(2 * 1024 * 1024).U32(32768).ToArray());
                byte[] p;
                while ((p = Next())[0] == 93) { }
                if (p[0] != 91) throw new IOException("No session: message " + p[0]);
                var r = new R(p, 1);
                r.U32();
                _remote = r.U32();
                Send(new W().U8(98).U32(_remote).Str("subsystem").U8(1).Str("sftp").ToArray());
                while ((p = Next())[0] == 93) { }
                if (p[0] != 99) throw new IOException("No sftp: message " + p[0]);
                SftpSend(new W().U8(1).U32(3).ToArray());
                if (SftpReceive()[0] != 2) throw new IOException("No sftp version.");
            }

            private void SftpSend(byte[] body) =>
                Send(new W().U8(94).U32(_remote).Str(new W().Str(body).ToArray()).ToArray());

            private byte[] SftpReceive()
            {
                while (true)
                {
                    var held = _sftpIn.ToArray();
                    if (held.Length >= 4)
                    {
                        int length = (int)BinaryPrimitives.ReadUInt32BigEndian(held);
                        if (held.Length >= 4 + length)
                        {
                            _sftpIn.SetLength(0);
                            _sftpIn.Write(held, 4 + length, held.Length - 4 - length);
                            return held.AsSpan(4, length).ToArray();
                        }
                    }
                    byte[] p = Next();
                    switch (p[0])
                    {
                        case 94:
                        {
                            var r = new R(p, 1);
                            r.U32();
                            _sftpIn.Write(r.Bytes());
                            break;
                        }
                        case 93: case 95: case 98: case 99: case 100: break;
                        default: throw new IOException("The channel ended: message " + p[0]);
                    }
                }
            }

            public Answer Request(byte kind, W rest)
            {
                uint id = _id++;
                SftpSend(new W().U8(kind).U32(id).Raw(rest.ToArray()).ToArray());
                byte[] reply = SftpReceive();
                var r = new R(reply, 1);
                r.U32();
                switch (reply[0])
                {
                    case 101: return new Answer(101, r.U32(), Array.Empty<byte>(), new());
                    case 102:
                    case 103: return new Answer(reply[0], 0, r.Bytes(), new());
                    case 104:
                    {
                        var names = new List<string>();
                        uint count = r.U32();
                        for (uint i = 0; i < count; i++)
                        {
                            names.Add(r.Text());
                            r.Text();
                            uint flags = r.U32();
                            if ((flags & 1) != 0) r.U64();
                            if ((flags & 2) != 0) { r.U32(); r.U32(); }
                            if ((flags & 4) != 0) r.U32();
                            if ((flags & 8) != 0) { r.U32(); r.U32(); }
                            if ((flags & 0x80000000) != 0) { uint n = r.U32(); for (uint j = 0; j < n; j++) { r.Bytes(); r.Bytes(); } }
                        }
                        return new Answer(104, 0, Array.Empty<byte>(), names);
                    }
                    default: return new Answer(reply[0], 0, Array.Empty<byte>(), new());
                }
            }

            /// <summary>A file's text, or null when the server would not open it.</summary>
            public string? ReadFile(string path)
            {
                var open = Request(3, new W().Str(path).U32(1).U32(0));
                if (open.Type != 102) return null;
                var data = Request(5, new W().Str(open.Bytes).U64(0).U32(65536));
                Request(4, new W().Str(open.Bytes));
                return data.Type == 103 ? Encoding.UTF8.GetString(data.Bytes) : "";
            }

            public List<string> List(string path)
            {
                var names = new List<string>();
                var open = Request(11, new W().Str(path));
                if (open.Type != 102) return names;
                for (int i = 0; i < 100; i++)
                {
                    var some = Request(12, new W().Str(open.Bytes));
                    if (some.Type != 104) break;
                    names.AddRange(some.Names);
                }
                Request(4, new W().Str(open.Bytes));
                return names;
            }

            public void Dispose()
            {
                try { _tcp.Dispose(); } catch { }
                _aesOut?.Dispose(); _aesIn?.Dispose(); _macOut?.Dispose(); _macIn?.Dispose();
            }
        }
    }
}
