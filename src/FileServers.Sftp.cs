using System;

namespace ExplorerNative
{
    /// <summary>The SFTP server's place in <see cref="FileServers"/>: its key, and starting it.</summary>
    internal sealed partial class FileServers
    {
        static partial void MakeSftp(Config c, ref IDisposable? made)
        {
            byte[] key;
            try { key = Convert.FromBase64String(c.Key); }
            catch { key = Array.Empty<byte>(); }
            if (key.Length == 0)
                throw new InvalidOperationException("It has no key of its own yet. Turn it off and on again in Preferences.");

            var server = new SftpServer(c.Folder, c.Port, c.User, c.Password, c.ReadOnly, key);
            try { server.Start(); }
            catch { server.Dispose(); throw; }
            made = server;
        }

        /// <summary>
        /// The key that says "this is the same server as last time". Made once,
        /// when SFTP is first turned on, and kept: a new key each start would
        /// have every program that connects warn that the server had changed.
        /// </summary>
        static partial void NewSftpKey(Settings s, ref bool changed)
        {
            if (!s.SftpServerEnabled || s.SftpServerHostKey.Length > 0) return;
            try
            {
                s.SftpServerHostKey = Convert.ToBase64String(SftpServer.NewHostKey());
                changed = true;
            }
            catch (Exception ex) { Diag.Note(ex); }   // said when it fails to start, by MakeSftp
        }

        static partial void SftpFingerprint(Settings s, ref string print)
        {
            if (s.SftpServerHostKey.Length == 0) return;
            print = SftpServer.Fingerprint(Convert.FromBase64String(s.SftpServerHostKey));
        }
    }
}
