using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ExplorerNative
{
    /// <summary>
    /// Cleaning up after Explorer Native 4.2 and earlier, which put Google
    /// Drive on a subst letter over a Cloud Files sync root in
    /// %APPDATA%\ExplorerNative\GoogleDrive. A clean exit of those versions
    /// emptied the folder and unregistered it; a crash left both behind. Every
    /// start sweeps once: the registration and any letter still pointing at
    /// one of those folders go, and the folders are deleted when Windows lets
    /// them be (an empty placeholder holds no data, so one that will not go is
    /// left for the next start rather than fought).
    /// </summary>
    internal static class LegacyCloudFiles
    {
        [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
        private static extern int CfUnregisterSyncRoot(string syncRootPath);

        [Flags]
        private enum DosDeviceFlags : uint
        {
            RAW_TARGET_PATH = 0x00000001,
            REMOVE_DEFINITION = 0x00000002,
            EXACT_MATCH_ON_REMOVE = 0x00000004,
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DefineDosDeviceW(DosDeviceFlags flags, string deviceName, string? targetPath);

        /// <summary>The old sync-root folders, by the names those versions gave them.</summary>
        private static string[] OldRoots()
        {
            try { return Directory.GetDirectories(Settings.AppDataDir, "GoogleDrive*"); }
            catch { return Array.Empty<string>(); }
        }

        public static void Sweep(Action<string> log)
        {
            var roots = OldRoots();
            SweepLetters(log);
            foreach (var root in roots)
            {
                try { CfUnregisterSyncRoot(root); } catch { }
                try
                {
                    Directory.Delete(root, recursive: true);
                    log($"removed the old Google Drive folder {Path.GetFileName(root)}");
                }
                catch (Exception ex)
                {
                    log($"the old Google Drive folder {Path.GetFileName(root)} is still there: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Every letter still pointing at one of the old folders, taken away. A
        /// letter is removed only when what it points at really is one: asking
        /// each letter is safer than guessing which one an old run took.
        /// </summary>
        private static void SweepLetters(Action<string> log)
        {
            string ours;
            try { ours = Path.GetFullPath(Settings.AppDataDir).TrimEnd('\\'); }
            catch { return; }

            var buffer = new char[1024];
            for (char c = 'A'; c <= 'Z'; c++)
            {
                string letter = c + ":";
                string target;
                try
                {
                    uint length = DriveLetter.QueryDosDeviceW(letter, buffer, (uint)buffer.Length);
                    if (length == 0) continue;
                    target = new string(buffer, 0, (int)length).Split('\0')[0];
                }
                catch { continue; }

                // "\??\C:\Users\...\ExplorerNative\GoogleDrive-2". A real volume
                // answers with a device path instead and never matches.
                const string rawPrefix = @"\??\";
                if (!target.StartsWith(rawPrefix, StringComparison.Ordinal)) continue;
                var directory = target[rawPrefix.Length..].TrimEnd('\\');
                if (!directory.StartsWith(ours + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Path.GetFileName(directory).StartsWith("GoogleDrive", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    DefineDosDeviceW(DosDeviceFlags.REMOVE_DEFINITION | DosDeviceFlags.RAW_TARGET_PATH |
                                     DosDeviceFlags.EXACT_MATCH_ON_REMOVE, letter, target);
                    log($"took away {letter}, left pointing at an old Google Drive folder");
                }
                catch { }
            }
        }
    }
}
