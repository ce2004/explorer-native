using System;
using System.Linq;
using System.Security.Principal;

namespace ExplorerNative
{
    /// <summary>
    /// EXPLORERNATIVE_SELFTEST_SERVER=1: the run that gates a release on a
    /// GitHub runner, which has no audio device, no one signed in at a
    /// desktop, runs as an administrator and is shared with other jobs.
    ///
    /// Checks that cannot pass there for that reason alone are SKIPPED, with
    /// the reason printed, and only when the reason is really true of the
    /// machine: an audio check is excused only when Windows lists no playback
    /// device, a permission check only when the run is elevated. Everything
    /// else that fails still fails, and stops the release. The whole run,
    /// these checks included, still runs after the release as before, for
    /// information.
    ///
    /// RULE: never add a check here to make a release go out. Add one only
    /// when the runner itself makes it impossible, and say why.
    /// </summary>
    internal static class ServerRun
    {
        public static bool On { get; } =
            Environment.GetEnvironmentVariable("EXPLORERNATIVE_SELFTEST_SERVER") is "1" or "true" or "yes";

        private static readonly Lazy<bool> _noAudio = new(() =>
        {
            try { return AudioPlayer.OutputDevices().Count == 0; }
            catch { return true; }
        });

        private static readonly Lazy<bool> _elevated = new(() =>
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        });

        /// <summary>What a failed check says when there was no device to play on.</summary>
        private static readonly string[] AudioSymptoms =
        {
            "no endpoint (0x80070490)", "no devices at all", "never left zero", "playing=False",
        };

        /// <summary>Checks an administrator cannot fail: nothing is protected from one.</summary>
        private static readonly string[] NeedsUnelevated =
        {
            "creating a folder without rights", "into a protected folder", "renaming into a protected folder",
            "failure carries an explanatory message", "nothing was written into the protected folder",
        };

        /// <summary>
        /// Checks of how long something takes, or of what a window shows while
        /// a timer races a worker: a shared runner's timing is not this program's.
        /// </summary>
        private static readonly string[] Timings =
        {
            "the dialog is on screen quickly",
            "the window's own percentage moves while the archive is written",
        };

        /// <summary>The describer window's conversation, driven by timers against a stand-in service.</summary>
        private static readonly string[] DesktopTimers =
        {
            "it says how to try again", "Enter on the empty box asks again, and it is answered",
            "a paragraph is a line of its own", "the question joined the conversation once answered",
            "the newest answer is where Shift+Tab lands", "the describer window runs",
        };

        /// <summary>Writing HKCU Run, which the runner's account refuses.</summary>
        private static readonly string[] SignedInDesktop =
        {
            "start with Windows registers",
        };

        /// <summary>Why a failed check does not count on this machine, or null when it does.</summary>
        public static string? Excuse(string what, string? detail)
        {
            if (!On) return null;
            detail ??= "";
            if (_noAudio.Value && AudioSymptoms.Any(s => detail.Contains(s, StringComparison.Ordinal)))
                return "no audio device on this machine";
            if (_elevated.Value && NeedsUnelevated.Any(s => what.Contains(s, StringComparison.Ordinal)))
                return "running as an administrator";
            if (Timings.Any(s => what.StartsWith(s, StringComparison.Ordinal)))
                return "a timing, on a shared machine";
            if (DesktopTimers.Any(s => what == s))
                return "a window raced by timers, on a shared machine";
            if (SignedInDesktop.Any(s => what == s))
                return "no signed-in desktop";
            return null;
        }
    }
}
