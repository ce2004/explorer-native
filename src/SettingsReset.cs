namespace ExplorerNative
{
    /// <summary>
    /// What "Reset to defaults" in Preferences builds.
    ///
    /// Every preference goes back to its default. What is kept is not a
    /// preference: it is state this computer and the people using it depend
    /// on, and resetting it is a surprise nobody asked for.
    ///
    /// - Where you are: the folders each tab is in, which tab is in front, the
    ///   row each tab was on, the last track played, the starting folder, and
    ///   the window's size and place.
    /// - The web app's pairing code, whether the web app is on, and its port:
    ///   a new code or a closed port signs out every phone and browser that
    ///   was paired.
    /// - The Google Drive sync pairs, and Google Drive staying on: the
    ///   configurator is where pairs are removed, and switching Drive off
    ///   stops every sync with no word said.
    /// - How this PC opens folders: whether Explorer Native is the default
    ///   file explorer and whether it is on the Windows context menu. A reset
    ///   of preferences must never change what double-clicking a folder does
    ///   on this machine.
    /// - Where the shell registration points and when folders were last taken,
    ///   which are records of what is in the registry, not choices.
    /// </summary>
    public static class SettingsReset
    {
        /// <summary>Said in the reset question, so nobody has to guess.</summary>
        public const string KeptSentence =
            "Kept: the folders each tab is in, the window's size and place, " +
            "Google Drive and its sync pairs, the web app with its pairing code and port, " +
            "and whether Explorer Native opens your folders and is on the Windows context menu. " +
            "Everything else goes back to how it was when Explorer Native was first installed, " +
            "including the audio player, the shortcuts and what is spoken.";

        /// <summary>
        /// Why a reset cannot happen now, or null when it can.
        ///
        /// While settings.json cannot be read, what this session holds are
        /// defaults standing in for the file — no pairing code, no sync pairs.
        /// A reset built from those and saved wrote them over the real file,
        /// wiping the very things the question promised to keep; and since a
        /// reset carries no "unreadable" mark, the retry that would have merged
        /// the file back in skipped it.
        /// </summary>
        public static string? Refusal(Settings live) =>
            live.UnreadableOnDisk
                ? "Preferences were not reset, because the settings file cannot be read right now. " +
                  "Nothing was changed. Try again in a moment."
                : null;

        public static Settings Defaults(Settings old, string tab1, string tab2) => new()
        {
            LastFolder = tab1,
            LastFolderTab2 = tab2,
            ActiveTab = old.ActiveTab,
            FocusedPathTab1 = old.FocusedPathTab1,
            FocusedPathTab2 = old.FocusedPathTab2,
            AudioLastPath = old.AudioLastPath,
            StartPath = old.StartPath,
            WindowX = old.WindowX,
            WindowY = old.WindowY,
            WindowWidth = old.WindowWidth,
            WindowHeight = old.WindowHeight,
            ConnectCode = old.ConnectCode,
            WebAppEnabled = old.WebAppEnabled,
            WebAppPort = old.WebAppPort,
            GoogleDriveEnabled = old.GoogleDriveEnabled,
            DriveSyncPairs = old.DriveSyncPairs,
            SetAsDefaultFileExplorer = old.SetAsDefaultFileExplorer,
            RegisterContextMenu = old.RegisterContextMenu,
            RegisteredExePath = old.RegisteredExePath,
            FoldersTakenAt = old.FoldersTakenAt,
        };
    }
}
