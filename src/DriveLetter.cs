using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ExplorerNative
{
    /// <summary>
    /// Choosing the letter Google Drive goes on: the one Preferences names, or
    /// the next free one after it, said when it had to be another.
    /// </summary>
    internal static class DriveLetter
    {
        /// <summary>
        /// Mounts <paramref name="host"/> on <paramref name="preferred"/> or the
        /// next free letter, and returns the letter ("G:").
        ///
        /// <paramref name="notify"/> is how the two outcomes worth telling
        /// somebody about get out of here: the letter they chose was not free,
        /// and there was no letter at all. A drive that comes up on H: without
        /// explanation reads as the setting having been ignored.
        /// </summary>
        public static string Take(WinFsp.Host host, char preferred = 'G', Action<string, string>? notify = null)
        {
            preferred = char.ToUpperInvariant(preferred);
            foreach (char c in Candidates(preferred))
            {
                if (InUse(c)) continue;
                string letter = c + ":";
                if (!host.Mount(letter, out _)) continue;

                if (c != preferred)
                    Tell(notify, "drive.letter.taken", $"{preferred}: was taken, so Google Drive is using {letter}");
                return letter;
            }

            Tell(notify, "drive.letter.none", "No drive letter is free for Google Drive");
            throw new InvalidOperationException("no free drive letter");
        }

        /// <summary>The letter <see cref="Take"/> would use now, without taking it; null when none is free.</summary>
        public static string? FirstFree(char preferred = 'G')
        {
            foreach (char c in Candidates(char.ToUpperInvariant(preferred)))
                if (!InUse(c)) return c + ":";
            return null;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetLogicalDrives();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint QueryDosDeviceW(string? deviceName, char[] targetPath, uint max);

        /// <summary>
        /// Whether anything owns <paramref name="c"/>, in any state.
        ///
        /// Asking whether the root exists answered "free" for a card reader with
        /// no card in it, a DVD drive with no disc, and a mapped network drive
        /// that had not reconnected yet — and a drive put over one of those
        /// hides the real device for the rest of the session. A letter is free
        /// only when Windows lists no drive for it, the object manager has no
        /// name for it, and no network mapping is remembered against it.
        /// </summary>
        internal static bool InUse(char c)
        {
            c = char.ToUpperInvariant(c);
            if (c < 'A' || c > 'Z') return true;

            try
            {
                if ((GetLogicalDrives() & (1u << (c - 'A'))) != 0) return true;
            }
            catch { return true; }

            try
            {
                var buffer = new char[1024];
                if (QueryDosDeviceW(c + ":", buffer, (uint)buffer.Length) != 0) return true;
            }
            catch { return true; }

            try
            {
                using var mapping = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Network\" + c);
                if (mapping != null) return true;
            }
            catch { }

            return false;
        }

        private static void Tell(Action<string, string>? notify, string id, string message)
        {
            // A message must never be the reason a mount fails.
            try { notify?.Invoke(id, message); } catch { }
        }

        private static IEnumerable<char> Candidates(char preferred)
        {
            yield return preferred;
            for (char c = 'D'; c <= 'Z'; c++)
                if (c != preferred) yield return c;
        }
    }
}
