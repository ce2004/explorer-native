namespace ExplorerNative
{
    /// <summary>
    /// What "Reset to defaults" in Preferences builds.
    ///
    /// Every preference goes back to its default. What is kept is not a
    /// preference: it is state this computer and the people using it depend
    /// on, and resetting it is a surprise nobody asked for.
    ///
    /// - The folders each tab is in, and the window's size and place.
    /// - The web app's pairing code: a new one signs out every phone and
    ///   browser that was paired.
    /// - The Google Drive sync pairs: the configurator is where those are
    ///   removed, and losing them stops a sync with no word said.
    /// - Where the shell registration points and when folders were last taken,
    ///   which are records of what is in the registry, not choices.
    /// </summary>
    public static class SettingsReset
    {
        /// <summary>Said in the reset question, so nobody has to guess.</summary>
        public const string KeptSentence =
            "Kept: the folders each tab is in, the window's size and place, " +
            "the web app's pairing code and the Google Drive sync pairs.";

        public static Settings Defaults(Settings old, string tab1, string tab2) => new()
        {
            LastFolder = tab1,
            LastFolderTab2 = tab2,
            WindowX = old.WindowX,
            WindowY = old.WindowY,
            WindowWidth = old.WindowWidth,
            WindowHeight = old.WindowHeight,
            ConnectCode = old.ConnectCode,
            DriveSyncPairs = old.DriveSyncPairs,
            RegisteredExePath = old.RegisteredExePath,
            FoldersTakenAt = old.FoldersTakenAt,
        };
    }
}
