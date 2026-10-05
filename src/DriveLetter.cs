using System;
using System.IO;
using static ExplorerNative.CfApi;

namespace ExplorerNative
{
    /// <summary>
    /// A drive letter pointing at a folder — what <c>subst</c> does, done through
    /// the API it is built on so there is no process to launch and no output to
    /// parse.
    ///
    /// A sync root is a directory, not a volume, so this is the only way to give
    /// it a letter. The alias is resolved before the filesystem is reached, which
    /// means everything underneath — the placeholders, the filter driver, the
    /// hydration callbacks — works exactly as it does through the long path. That
    /// is the claim, anyway; <see cref="DriveMount"/>'s harness reads a file
    /// through the letter rather than trusting it.
    ///
    /// Two things to know. The alias belongs to this logon session, so it is not
    /// visible to a service or to an elevated process, and it does not survive a
    /// reboot — which makes it something the application sets up on every start,
    /// not something installed. And it must be removed on the way out, or the
    /// letter is left pointing at a directory that no longer hydrates anything.
    /// </summary>
    internal sealed class DriveLetter : IDisposable
    {
        private readonly string _target;

        public string Letter { get; }

        private DriveLetter(string letter, string target)
        {
            Letter = letter;
            _target = target;
        }

        /// <summary>
        /// Points <paramref name="preferred"/> — or the next free letter after it
        /// — at <paramref name="directory"/>.
        ///
        /// <paramref name="notify"/> is how the two outcomes worth telling
        /// somebody about get out of here: the letter they chose in Preferences
        /// was not free, and there was no letter at all. Both were already known
        /// at this point and neither was said; a drive that comes up on H:
        /// without explanation reads as the setting having been ignored.
        /// </summary>
        public static DriveLetter Assign(
            string directory, char preferred = 'G', Action<string, string>? notify = null)
        {
            directory = Path.GetFullPath(directory).TrimEnd('\\');

            foreach (char c in Candidates(preferred))
            {
                string letter = c + ":";
                if (InUse(c)) continue;

                // The raw target path is how subst does it: the object manager
                // name of the directory, not a DOS path. Without RAW_TARGET_PATH
                // the letter is created as a device alias that does not behave
                // like a drive.
                if (!DefineDosDeviceW(DosDeviceFlags.RAW_TARGET_PATH, letter, @"\??\" + directory))
                    continue;

                if (c != preferred)
                    Tell(notify, "drive.letter.taken", $"{preferred}: was taken, using {letter}");
                return new DriveLetter(letter, directory);
            }

            Tell(notify, "drive.letter.none", "No drive letter is free for Google Drive");
            throw new InvalidOperationException("no free drive letter");
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetLogicalDrives();

        /// <summary>
        /// Whether anything owns <paramref name="c"/>, in any state.
        ///
        /// Asking whether the root exists answered "free" for a card reader with
        /// no card in it, a DVD drive with no disc, and a mapped network drive
        /// that had not reconnected yet — and the alias defined over one of those
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
            // A message must never be the reason a mount fails, so the mount does
            // not depend on whoever is listening surviving being told.
            try { notify?.Invoke(id, message); } catch { }
        }

        private static System.Collections.Generic.IEnumerable<char> Candidates(char preferred)
        {
            yield return preferred;
            for (char c = 'D'; c <= 'Z'; c++)
                if (c != preferred) yield return c;
        }

        public void Dispose()
        {
            try
            {
                DefineDosDeviceW(
                    DosDeviceFlags.REMOVE_DEFINITION |
                    DosDeviceFlags.RAW_TARGET_PATH |
                    DosDeviceFlags.EXACT_MATCH_ON_REMOVE,
                    Letter, @"\??\" + _target);
            }
            catch
            {
                // A letter left behind is untidy but harmless — it disappears
                // with the logon session.
            }
        }
    }
}
