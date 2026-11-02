using System;
using System.Runtime.InteropServices;

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
                // The shipped exe runs with InvariantGlobalization, so .NET's own
                // "g" is always MM/dd/yyyy HH:mm whatever the region says. Windows
                // is asked instead, so a UK PC hears 04/03 for the 4th of March.
                if (exactFormat == DefaultFormat) return WindowsDateTime(when);
                try { return when.ToString(exactFormat); }
                catch (FormatException) { return when.ToString("g"); }
            }
            return Relative(when, DateTime.Now);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEMTIME
        {
            public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetDateFormatEx(string? locale, uint flags, ref SYSTEMTIME date,
            string? format, char[] buffer, int size, string? calendar);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetTimeFormatEx(string? locale, uint flags, ref SYSTEMTIME time,
            string? format, char[] buffer, int size);

        private const uint DATE_SHORTDATE = 0x1, DATE_LONGDATE = 0x2, TIME_NOSECONDS = 0x2;

        /// <summary>
        /// Date and time the way this PC's region writes them (the person's own
        /// overrides included), with no seconds. Long date for property pages.
        /// The time is taken as given: callers pass local time.
        /// </summary>
        public static string WindowsDateTime(DateTime when, bool longDate = false)
        {
            try
            {
                var st = new SYSTEMTIME
                {
                    Year = (ushort)when.Year, Month = (ushort)when.Month, DayOfWeek = (ushort)when.DayOfWeek,
                    Day = (ushort)when.Day, Hour = (ushort)when.Hour, Minute = (ushort)when.Minute,
                    Second = (ushort)when.Second, Milliseconds = (ushort)when.Millisecond,
                };
                var date = new char[128];
                var time = new char[128];
                int d = GetDateFormatEx(null, longDate ? DATE_LONGDATE : DATE_SHORTDATE, ref st, null, date, date.Length, null);
                int t = GetTimeFormatEx(null, TIME_NOSECONDS, ref st, null, time, time.Length);
                if (d > 1 && t > 1) return new string(date, 0, d - 1) + " " + new string(time, 0, t - 1);
            }
            catch { }
            return when.ToString(longDate ? "f" : "g");
        }

        /// <summary>Split out from Format so it can be tested against a fixed "now".</summary>
        public static string Relative(DateTime when, DateTime now)
        {
            if (when > now.AddMinutes(5))
            {
                // A file genuinely stamped well ahead of us.
                return "in the future";
            }
            // A few seconds or minutes ahead is clock skew on a share or FAT's
            // 2-second rounding: the file was just written, so treat it as now.
            if (when > now) when = now;

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
