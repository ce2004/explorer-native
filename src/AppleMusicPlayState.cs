namespace ExplorerNative
{
    /// <summary>
    /// When the Apple Music page's own report should become what the person wants
    /// (play or pause). Right after Enter, and right after our own Play/Pause, what
    /// was asked for decides; once the song has made sound, a settled pause or
    /// resume from outside (Windows media controls, a headset button) is the truth,
    /// so the next Play/Pause does the opposite of it rather than the same again.
    /// </summary>
    internal static class AppleMusicPlayState
    {
        /// <summary>How long after our own Play/Pause the page's reports are not trusted (they may predate it).</summary>
        internal const long OwnToggleSettleMs = 1500;

        /// <summary>
        /// True (wants playing) for MusicKit's state 2, false (wants paused) for 3,
        /// null to leave the wish alone: before the first sound, within the settle
        /// after our own press, and for every in-between state (loading, seeking,
        /// waiting, stalled, stopped, ended).
        /// </summary>
        internal static bool? WishFromPage(int pageState, bool started, long msSinceOwnToggle)
        {
            if (!started || msSinceOwnToggle < OwnToggleSettleMs) return null;
            return pageState switch
            {
                2 => true,
                3 => false,
                _ => null,
            };
        }
    }
}
