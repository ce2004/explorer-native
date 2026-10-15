using System;
using System.Collections.Generic;
using System.Linq;

namespace ExplorerNative
{
    /// <summary>
    /// Windows notifications for the messages in <see cref="Notifications.All"/>
    /// that are turned on in Preferences, Notifications (Settings.WindowsNotifications).
    /// All are off until turned on. Shown through the tray icon, which Windows
    /// turns into an ordinary notification in the corner and in the
    /// notification centre, and which needs no package and no registration.
    /// The message is still shown and spoken as it always was; this is as well.
    /// </summary>
    internal static class WindowsToasts
    {
        /// <summary>Shows one notification (title, text). Set by the tray once its icon exists.</summary>
        internal static Action<string, string>? Show { get; set; }

        private static readonly object Gate = new();
        private static string? _parsedText;
        private static HashSet<string> _parsed = new(StringComparer.OrdinalIgnoreCase);

        public static HashSet<string> Parse(string? text) =>
            new((text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase);

        public static string Format(IEnumerable<string> ids) =>
            string.Join(";", ids.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

        /// <summary>Whether a message is turned on, parsed once per distinct settings text.</summary>
        public static bool IsOn(string id, Settings settings)
        {
            var text = settings.WindowsNotifications ?? "";
            if (text.Length == 0) return false;
            lock (Gate)
            {
                if (!ReferenceEquals(text, _parsedText) && text != _parsedText)
                {
                    _parsed = Parse(text);
                    _parsedText = text;
                }
                return _parsed.Contains(id);
            }
        }

        /// <summary>A message that has just happened: a notification if it is turned on.</summary>
        public static void Raise(string id, string message, Settings settings)
        {
            try
            {
                if (Show == null || string.IsNullOrWhiteSpace(message) || !IsOn(id, settings)) return;
                Show(Notifications.ById(id)?.Name ?? "Explorer Native", message);
            }
            catch { }
        }
    }
}
