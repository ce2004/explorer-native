using System;
using System.Collections.Generic;
using System.IO;

namespace ExplorerNative
{
    /// <summary>
    /// What is said about a file when the cursor rests on it: words and
    /// characters for text, and bitrate, sample rate, length, bit depth and
    /// channels for audio, in that order. Null when there is nothing to add.
    ///
    /// Runs on a worker. It reads the file, which is why it is only asked after
    /// the cursor has stopped, and never for a file on the Drive letter.
    /// </summary>
    public static class FileDetails
    {
        /// <summary>Bigger than this is not counted: a log of hundreds of megabytes is a long read for a sentence.</summary>
        public const long MaxTextBytes = 5 * 1024 * 1024;

        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".text", ".md", ".markdown", ".log", ".csv", ".tsv", ".ini", ".cfg", ".conf", ".json",
            ".xml", ".yaml", ".yml", ".toml", ".html", ".htm", ".css", ".js", ".ts", ".cs", ".py", ".ps1",
            ".bat", ".cmd", ".sh", ".c", ".h", ".cpp", ".hpp", ".java", ".rs", ".go", ".srt", ".vtt",
            ".lrc", ".nfo", ".cue", ".m3u", ".m3u8", ".rtf", ".sql", ".reg",
        };

        public static bool IsText(string path) => TextExtensions.Contains(Path.GetExtension(path));

        public static string? Describe(string path, string? audioExtensions)
        {
            try
            {
                if (AudioFiles.IsAudio(path, audioExtensions)) return DescribeAudio(path);
                if (IsText(path)) return DescribeText(path);
            }
            catch
            {
                // A file that will not open has no details to give, and saying
                // so on every arrow would be noise.
            }
            return null;
        }

        private static string? DescribeText(string path)
        {
            // Exact: in a folder named "Notes." the ordinary path counts the
            // words of the file of the same name in "Notes".
            var exact = NameRules.ExactPath(path);
            var info = new FileInfo(exact);
            if (!info.Exists || info.Length > MaxTextBytes) return null;
            return CountText(File.ReadAllText(exact));
        }

        /// <summary>"120 words, 640 characters". Words are runs of anything that is not white space.</summary>
        public static string CountText(string text)
        {
            int words = 0;
            bool inWord = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) inWord = false;
                else if (!inWord) { inWord = true; words++; }
            }
            return $"{words:N0} {(words == 1 ? "word" : "words")}, " +
                   $"{text.Length:N0} {(text.Length == 1 ? "character" : "characters")}";
        }

        private static string? DescribeAudio(string path)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in AudioTags.Read(path)) found[row.Name] = row.Value;

            var parts = new List<string>();
            foreach (var name in new[] { "Bitrate", "Sample rate", "Length", "Sample size", "Channels" })
                if (found.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                    parts.Add(value);

            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
    }
}
