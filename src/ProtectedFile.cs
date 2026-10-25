using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ExplorerNative
{
    /// <summary>
    /// Everything the application saves under AppData — settings (which hold
    /// the Connect pairing code), the Google client file, the logs — is
    /// encrypted with DPAPI for the current Windows account. A copy of the
    /// folder is unreadable to another user or on another machine, and nothing
    /// in it can be read by opening it in Notepad.
    ///
    /// A whole file is <see cref="Magic"/> followed by the DPAPI blob. A log is
    /// appended a line at a time, each line <see cref="LinePrefix"/> plus the
    /// base64 of its own blob; <c>ExplorerNative.exe --read-log file</c> prints
    /// one back. A file without the marker is read as the plain text an older
    /// build wrote, and is encrypted the next time it is saved.
    /// </summary>
    internal static class ProtectedFile
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ENDPAPI1");
        public const string LinePrefix = "enc:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ExplorerNative");

        public static byte[] Protect(byte[] data) =>
            ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

        public static byte[] Unprotect(byte[] data) =>
            ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

        public static byte[] Encode(string text)
        {
            var blob = Protect(Encoding.UTF8.GetBytes(text));
            var result = new byte[Magic.Length + blob.Length];
            Magic.CopyTo(result, 0);
            blob.CopyTo(result, Magic.Length);
            return result;
        }

        /// <summary>The text of a file's bytes, decrypting when they are ours.</summary>
        public static string Decode(byte[] bytes)
        {
            if (!IsProtected(bytes)) return new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
            return Encoding.UTF8.GetString(Unprotect(bytes.AsSpan(Magic.Length).ToArray()));
        }

        public static bool IsProtected(byte[] bytes) => bytes.AsSpan().StartsWith(Magic);

        public static string ReadAllText(string path) => Decode(File.ReadAllBytes(path));

        /// <summary>
        /// A file's text as UTF-8 bytes, decrypting when they are ours: for JSON
        /// read straight from bytes. A large file read as a string held it twice
        /// over (two bytes a character) on top of the bytes it came from.
        /// </summary>
        public static byte[] ReadAllUtf8(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (IsProtected(bytes)) return Unprotect(bytes.AsSpan(Magic.Length).ToArray());
            return bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? bytes[3..] : bytes;
        }

        /// <summary>UTF-8 text written encrypted, as <see cref="WriteAllText"/> does, without making a string of it first.</summary>
        public static void WriteAllUtf8(string path, byte[] utf8)
        {
            var blob = Protect(utf8);
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            file.Write(Magic, 0, Magic.Length);
            file.Write(blob, 0, blob.Length);
        }

        /// <summary>
        /// Binary data saved encrypted, written beside the real file and moved
        /// over it, so a crash part way leaves the old file rather than half a new one.
        /// </summary>
        public static void WriteAllBytes(string path, byte[] data)
        {
            var blob = Protect(data);
            var temp = path + ".tmp";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(Magic, 0, Magic.Length);
                file.Write(blob, 0, blob.Length);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }

        /// <summary>
        /// Binary data written by <see cref="WriteAllBytes"/>. Throws on a file
        /// that is not ours, since binary data has no plain older version.
        /// </summary>
        public static byte[] ReadAllBytes(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (!IsProtected(bytes)) throw new InvalidDataException("not an encrypted file");
            return Unprotect(bytes.AsSpan(Magic.Length).ToArray());
        }

        /// <param name="durable">Flush to the disk itself before returning, for a file about to be renamed over
        /// the real one: otherwise a power cut can leave the rename on disk and the bytes not, an empty file.</param>
        public static void WriteAllText(string path, string text, bool durable = false)
        {
            var bytes = Encode(text);
            if (!durable) { File.WriteAllBytes(path, bytes); return; }
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            file.Write(bytes, 0, bytes.Length);
            file.Flush(flushToDisk: true);
        }

        /// <summary>
        /// Appends one encrypted line. Newlines inside it are kept, since the line is opaque.
        ///
        /// One write through a handle nobody else may write through, retried
        /// for a moment when another writer — or <see cref="EncryptLogInPlace"/>
        /// — has the file: two processes logging at once is normal, and a line
        /// dropped for that is a line missing from the one record that outlives
        /// the run.
        /// </summary>
        public static void AppendLine(string path, string line)
        {
            var bytes = Encoding.UTF8.GetBytes(
                LinePrefix + Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(line))) + Environment.NewLine);

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    file.Write(bytes, 0, bytes.Length);
                    return;
                }
                catch (IOException ex) when (IsSharingViolation(ex) && attempt < ShareRetries)
                {
                    System.Threading.Thread.Sleep(ShareRetryMilliseconds);
                }
            }
        }

        /// <summary>About two seconds of waiting for a file another writer has.</summary>
        private const int ShareRetries = 100;
        private const int ShareRetryMilliseconds = 20;

        private static bool IsSharingViolation(IOException ex) =>
            (ex.HResult & 0xFFFF) is 32 or 33; // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION

        /// <summary>
        /// Encrypts the plain lines an older build left in a log. Best effort;
        /// a log in use is done on a later launch.
        ///
        /// Read and rewritten inside one handle that shares nothing, so a line
        /// appended meanwhile waits for it rather than being lost. It used to
        /// read the file, write a copy and move the copy over the log, and
        /// anything appended in between was gone: a stress run kept 804 lines
        /// of 2,195.
        /// </summary>
        public static void EncryptLogInPlace(string path)
        {
            try
            {
                if (!File.Exists(path)) return;

                FileStream? file = null;
                for (int attempt = 0; file == null; attempt++)
                {
                    try { file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException ex) when (IsSharingViolation(ex) && attempt < ShareRetries)
                    {
                        System.Threading.Thread.Sleep(ShareRetryMilliseconds);
                    }
                }

                using (file)
                {
                    var bytes = new byte[file.Length];
                    file.ReadExactly(bytes);

                    var text = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
                    var lines = text.Replace("\r", "").Split('\n');

                    bool plain = false;
                    foreach (var l in lines)
                        if (l.Length > 0 && !l.StartsWith(LinePrefix, StringComparison.Ordinal)) { plain = true; break; }
                    if (!plain) return;

                    var sb = new StringBuilder();
                    foreach (var l in lines)
                    {
                        if (l.Length == 0) continue;
                        sb.Append(l.StartsWith(LinePrefix, StringComparison.Ordinal)
                            ? l
                            : LinePrefix + Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(l))));
                        sb.Append(Environment.NewLine);
                    }

                    var rewritten = Encoding.UTF8.GetBytes(sb.ToString());
                    file.Position = 0;
                    file.Write(rewritten, 0, rewritten.Length);
                    file.SetLength(rewritten.Length);
                    file.Flush(flushToDisk: true);
                }
            }
            catch { }
        }

        /// <summary>A log's lines in the clear; older plain lines come through as they are.</summary>
        public static IEnumerable<string> ReadLog(string path)
        {
            // A whole encrypted file — settings.json, or a copy of a damaged one
            // kept beside it — rather than a log of encrypted lines.
            var bytes = File.ReadAllBytes(path);
            if (IsProtected(bytes))
            {
                foreach (var line in Decode(bytes).Replace("\r", "").Split('\n')) yield return line;
                yield break;
            }

            foreach (var line in File.ReadLines(path))
            {
                if (!line.StartsWith(LinePrefix, StringComparison.Ordinal))
                {
                    yield return line;
                    continue;
                }

                string text;
                try { text = Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(line[LinePrefix.Length..]))); }
                catch { text = "(a line this Windows account cannot decrypt)"; }
                yield return text;
            }
        }
    }
}
