using System;

namespace ExplorerNative
{
    /// <summary>
    /// Renders a timestamp either exactly or as a relative phrase.
    ///
    /// The relative form exists because "14/03/2024 09:41" is a mouthful to hear
    /// on every row when all you wanted to know was whether a file is recent.
    /// </summary>
    public static class TimeFormatter
    {
        /// <summary>
        /// Short date and time, in whatever order this machine writes them.
        /// Not configurable: a format string is a poor thing to ask anyone for,
        /// and Windows already knows the answer.
        /// </summary>
        public const string DefaultFormat = "g";

        public static string Format(DateTime when, bool verbose) =>
            Format(when, verbose, DefaultFormat);

        public static string Format(DateTime when, bool verbose, string exactFormat)
        {
            if (verbose)
            {
                try { return when.ToString(exactFormat); }
                catch (FormatException) { return when.ToString("g"); }
            }
            return Relative(when, DateTime.Now);
        }

        /// <summary>Split out from Format so it can be tested against a fixed "now".</summary>
        public static string Relative(DateTime when, DateTime now)
        {
            if (when > now)
            {
                // Clock skew, or a file genuinely stamped ahead of us.
                return "in the future";
            }

            var whenDay = when.Date;
            var today = now.Date;

            if (whenDay == today) return "today";
            if (whenDay == today.AddDays(-1)) return "yesterday";

            int days = (int)(today - whenDay).TotalDays;

            if (days < 7) return $"{days} days ago";
            if (days < 14) return "one week ago";
            if (days < 30) return $"{days / 7} weeks ago";

            if (days < 365)
            {
                int months = days / 30;
                return months == 1 ? "one month ago" : $"{months} months ago";
            }

            int years = days / 365;
            if (years > 10) return "a long time ago";
            return years == 1 ? "one year ago" : $"{years} years ago";
        }
    }
}
