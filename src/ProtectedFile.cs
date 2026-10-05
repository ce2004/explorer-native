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

        public static void WriteAllText(string path, string text) => File.WriteAllBytes(path, Encode(text));

        /// <summary>Appends one encrypted line. Newlines inside it are kept, since the line is opaque.</summary>
        public static void AppendLine(string path, string line) =>
            File.AppendAllText(path,
                LinePrefix + Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(line))) + Environment.NewLine);

        /// <summary>
        /// Encrypts the plain lines an older build left in a log. Best effort;
        /// a log in use is done on a later launch.
        /// </summary>
        public static void EncryptLogInPlace(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var lines = File.ReadAllLines(path);
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
                var temp = path + ".enc-tmp";
                File.WriteAllText(temp, sb.ToString());
                File.Move(temp, path, overwrite: true);
            }
            catch { }
        }

        /// <summary>A log's lines in the clear; older plain lines come through as they are.</summary>
        public static IEnumerable<string> ReadLog(string path)
        {
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
