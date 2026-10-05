using System;

namespace ExplorerNative
{
    public enum SizeUnitStyle
    {
        /// <summary>"1.5 megabytes" — spoken cleanly by a screen reader.</summary>
        FullWords,
        /// <summary>"1.5 MB" — compact, but read as letters.</summary>
        Abbreviated
    }

    public static class SizeFormatter
    {
        /// <summary>
        /// One decimal place. Enough to tell 1.4 from 1.9 gigabytes, and few
        /// enough that the number is still a phrase rather than a recitation when
        /// it is read aloud on every row.
        /// </summary>
        public const int DefaultDecimals = 1;

        /// <summary>Formats at the standard precision.</summary>
        public static string Format(long bytes, SizeUnitStyle style) =>
            Format(bytes, style, DefaultDecimals);

        private static readonly string[] FullSingular =
            { "byte", "kilobyte", "megabyte", "gigabyte", "terabyte", "petabyte" };
        private static readonly string[] FullPlural =
            { "bytes", "kilobytes", "megabytes", "gigabytes", "terabytes", "petabytes" };
        private static readonly string[] Abbrev =
            { "B", "KB", "MB", "GB", "TB", "PB" };

        public static string Format(long bytes, SizeUnitStyle style, int decimals)
        {
            if (bytes < 0) return "";

            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < FullPlural.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            // Bytes are always whole; larger units honour the configured precision.
            int places = unit == 0 ? 0 : Math.Clamp(decimals, 0, 3);
            double rounded = Math.Round(size, places);

            // Rounding can reach the unit above the one it was measured in.
            // 1,048,570 bytes is 1023.99 kilobytes, and to one decimal place that
            // is "1,024 kilobytes" — the ladder reading out its own overflow, on
            // a row a screen reader speaks. Step up once and round again; the
            // loop above already left size below 1024, so this cannot cascade.
            if (rounded >= 1024 && unit < FullPlural.Length - 1)
            {
                size /= 1024;
                unit++;
                places = Math.Clamp(decimals, 0, 3);
                rounded = Math.Round(size, places);
            }

            // "#" not "0" after the point, so trailing zeros are dropped: spoken
            // aloud, "1.0 kilobyte" becomes "one point zero kilobyte", which is
            // strictly worse than "one kilobyte".
            string numberFormat = places == 0 ? "#,0" : "#,0." + new string('#', places);
            string number = rounded.ToString(numberFormat);

            if (style == SizeUnitStyle.Abbreviated)
                return number + " " + Abbrev[unit];

            // Exactly one of anything is singular ("1 byte", "1 megabyte").
            bool singular = Math.Abs(rounded - 1.0) < 0.0000001;
            string word = singular ? FullSingular[unit] : FullPlural[unit];
            return number + " " + word;
        }
    }
}
