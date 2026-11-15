using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SMBLibrary;
using SMBLibrary.Client;

namespace ExplorerNative
{
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
                }
                client.Disconnect();

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
}
