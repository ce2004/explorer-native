using System;

namespace ExplorerNative
{
    /// <summary>
    /// What the player is doing right now, for the Audio preferences page to
    /// print — and the only thing that page needed the entry point for.
    ///
    /// It read <c>Program.AudioOutputDiagnostic</c> and two neighbours directly,
    /// which sounds harmless and was not: <see cref="Program"/> owns
    /// <c>Main</c>, so a project that compiles it has an entry point of its own,
    /// and the self-test therefore could not include <see cref="SettingsForm"/>
    /// at all. The largest hand-built window in the application — nine pages of
    /// controls, every one of them constructed in code — had no test that it so
    /// much as builds, while the two small dialogs beside it did.
    ///
    /// Three delegates and a fallback is the whole of the fix. Program fills
    /// them in once the tray context exists; anything that has not filled them
    /// in gets a sentence saying so, which is exactly what the page showed
    /// before the player had started anyway.
    /// </summary>
    public static class AudioReport
    {
        /// <summary>Where the sound is leaving, and at what rate.</summary>
        public static Func<string>? Output { get; set; }

        /// <summary>Why a track would not start, when one would not.</summary>
        public static Func<string>? Problem { get; set; }

        /// <summary>Whether the limiter is on, and how much is clipping.</summary>
        public static Func<string>? Level { get; set; }

        public static string OutputText => Ask(Output, "the player has not started");
        public static string ProblemText => Ask(Problem, "not started");
        public static string LevelText => Ask(Level, "nothing is playing");

        /// <summary>
        /// Never throws and never comes back empty. This is read while building
        /// a dialog, and the player's state is owned by other threads — a
        /// diagnostic that took the Preferences window down with it would be the
        /// least useful failure in the application.
        /// </summary>
        private static string Ask(Func<string>? source, string fallback)
        {
            if (source == null) return fallback;

            try
            {
                var text = source();
                return string.IsNullOrWhiteSpace(text) ? fallback : text;
            }
            catch
            {
                return fallback;
            }
        }
    }
}
