using System;
using System.Globalization;
using System.IO;

namespace ExplorerNative
{
    /// <summary>
    /// The window was open when an install or update stood the app down
    /// (5.3.0, Conner: an update put him back in the tray). A note in AppData,
    /// written on the way out and taken by the next start; the folders, tabs
    /// and focused rows come back from settings as on any start. An old note
    /// (a quit that never restarted) is ignored.
    /// </summary>
    internal static class ReopenAfterUpdate
    {
        /// <summary>Longer than a quit can take (it may send Drive files for a minute or two).</summary>
        internal static readonly TimeSpan GoodFor = TimeSpan.FromMinutes(10);

        internal static string NotePath => Path.Combine(Settings.AppDataDir, "reopen-window");

        public static void Mark() => Mark(DateTime.UtcNow);

        internal static void Mark(DateTime atUtc)
        {
            try { File.WriteAllText(NotePath, atUtc.Ticks.ToString(CultureInfo.InvariantCulture)); }
            catch { }
        }

        /// <summary>Whether a fresh note was there; it is deleted either way.</summary>
        public static bool Take()
        {
            try
            {
                if (!File.Exists(NotePath)) return false;
                var text = File.ReadAllText(NotePath);
                File.Delete(NotePath);
                if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) ||
                    ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return false;
                var age = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
                return age >= TimeSpan.FromMinutes(-1) && age < GoodFor;
            }
            catch { return false; }
        }
    }
}
