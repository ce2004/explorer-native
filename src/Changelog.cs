using System;
using System.Collections.Generic;
using System.IO;

namespace ExplorerNative
{
    /// <summary>
    /// The changelog, read from CHANGES.txt built into the executable.
    ///
    /// It used to be fetched from GitHub every time it was opened, so a PC that
    /// could not reach GitHub had no changelog at all, and opening it cost a
    /// network round trip per hundred releases. The file the releases are made
    /// from is already in the repository; shipping it inside the exe means the
    /// changelog is the one for the build you are running, and it opens at once.
    /// The shape is Kova's: one line per change, with a heading line per version.
    /// </summary>
    internal static class Changelog
    {
        public const string ResourceName = "CHANGES.txt";
        public const string HeadingPrefix = "Version ";

        /// <summary>The embedded CHANGES.txt, or empty when the build has none.</summary>
        public static string Embedded()
        {
            using var stream = typeof(Changelog).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null) return "";
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// One list line per line of the file. A version alone on a line becomes
        /// "Version 2.3.3", with ", this one" on the version that is running;
        /// blank lines are dropped.
        /// </summary>
        public static List<string> Lines(string text, string current)
        {
            var lines = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                lines.Add(IsVersion(line)
                    ? HeadingPrefix + line + (SameVersion(line, current) ? ", this one" : "")
                    : line);
            }
            return lines;
        }

        /// <summary>
        /// Whether two version numbers name one release, as numbers: "3.1" in
        /// CHANGES.txt is the running "3.1.0". Compared as text, the running
        /// version was never marked whenever the file left the zero off.
        /// </summary>
        internal static bool SameVersion(string a, string b)
        {
            static Version? Parse(string text) =>
                Version.TryParse(text.Trim().TrimStart('v', 'V'), out var v)
                    ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0))
                    : null;
            var x = Parse(a);
            return x != null && x == Parse(b);
        }

        /// <summary>
        /// A line this made from a version. Not every line starting "Version "
        /// is one: CHANGES.txt has "Version 2.0 is the largest release...".
        /// </summary>
        public static bool IsHeading(string line)
        {
            if (!line.StartsWith(HeadingPrefix, StringComparison.Ordinal)) return false;
            var rest = line[HeadingPrefix.Length..];
            int comma = rest.IndexOf(',');
            var number = comma < 0 ? rest : rest[..comma];
            return number.Length > 0 && IsVersion(number) &&
                   (comma < 0 || rest[comma..] == ", this one");
        }

        /// <summary>"2.3.3": digits and dots only, as the release workflow expects.</summary>
        private static bool IsVersion(string line)
        {
            if (!char.IsDigit(line[0]) || !char.IsDigit(line[^1])) return false;
            foreach (var c in line)
                if (!char.IsDigit(c) && c != '.') return false;
            return Version.TryParse(line, out _);
        }
    }
}
