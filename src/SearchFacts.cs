using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ExplorerNative
{
    /// <summary>The kinds a search can ask for with kind:word.</summary>
    internal enum SearchKind { Folder, File, Audio, Video, Image, Document, Archive }

    /// <summary>
    /// The size, date and kind words of a search (size&gt;100mb, modified:today,
    /// kind:audio), taken apart, and the test of an item against them.
    /// A word that does not parse is not one of these: it stays an ordinary
    /// search word.
    /// </summary>
    internal sealed class SearchFacts
    {
        /// <summary>Bigger than this many bytes, or -1.</summary>
        public long Above { get; private set; } = -1;

        /// <summary>Smaller than this many bytes, or -1.</summary>
        public long Below { get; private set; } = -1;

        /// <summary>Modified at or after this local time (MinValue: no limit).</summary>
        public DateTime From { get; private set; } = DateTime.MinValue;

        /// <summary>Modified before this local time (MaxValue: no limit).</summary>
        public DateTime Until { get; private set; } = DateTime.MaxValue;

        private readonly List<SearchKind> _kinds = new();

        /// <summary>The kinds asked for; an item may be any one of them.</summary>
        public IReadOnlyList<SearchKind> Kinds => _kinds;

        public bool Any => Above >= 0 || Below >= 0 || From != DateTime.MinValue || Until != DateTime.MaxValue || _kinds.Count > 0;

        /// <summary>
        /// Takes in one typed word if it is a size, date or kind word, and says
        /// whether it was. <paramref name="now"/> is local time.
        /// </summary>
        public bool TryTake(string word, DateTime now)
        {
            if (word.StartsWith("size", StringComparison.OrdinalIgnoreCase) && word.Length > 5)
                return TakeSize(word.AsSpan(4));
            if (word.StartsWith("modified:", StringComparison.OrdinalIgnoreCase))
                return TakeDate(word.Substring("modified:".Length).ToLowerInvariant(), now);
            if (word.StartsWith("kind:", StringComparison.OrdinalIgnoreCase))
            {
                var kind = KindNamed(word.Substring("kind:".Length));
                if (kind == null) return false;
                if (!_kinds.Contains(kind.Value)) _kinds.Add(kind.Value);
                return true;
            }
            return false;
        }

        private bool TakeSize(ReadOnlySpan<char> rest)
        {
            char op = rest[0];
            if (op != '>' && op != '<') return false;
            rest = rest[1..];

            int digits = 0;
            while (digits < rest.Length && (char.IsAsciiDigit(rest[digits]) || rest[digits] == '.')) digits++;
            if (digits == 0) return false;
            if (!double.TryParse(rest[..digits], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                return false;

            long unit = rest[digits..].ToString().ToLowerInvariant() switch
            {
                "kb" or "k" => 1024L,
                "mb" or "m" => 1024L * 1024,
                "gb" or "g" => 1024L * 1024 * 1024,
                "tb" or "t" => 1024L * 1024 * 1024 * 1024,
                "b" => 1,
                _ => 0,
            };
            if (unit == 0) return false;

            double bytes = number * unit;
            if (bytes >= long.MaxValue) return false;
            long value = (long)bytes;

            if (op == '>') Above = Math.Max(Above, value);
            else Below = Below < 0 ? value : Math.Min(Below, value);
            return true;
        }

        private bool TakeDate(string what, DateTime now)
        {
            var today = now.Date;
            DateTime from, until = DateTime.MaxValue;

            switch (what)
            {
                case "today": from = today; until = today.AddDays(1); break;
                case "yesterday": from = today.AddDays(-1); until = today; break;
                case "thisweek":
                    int back = ((int)today.DayOfWeek - (int)FirstDayOfWeek + 7) % 7;
                    from = today.AddDays(-back);
                    break;
                case "thismonth": from = new DateTime(today.Year, today.Month, 1); break;
                case "thisyear": from = new DateTime(today.Year, 1, 1); break;
                default:
                    if (what.Length == 4 && int.TryParse(what, NumberStyles.None, CultureInfo.InvariantCulture, out int year) &&
                        year >= 1900 && year <= 9998)
                    {
                        from = new DateTime(year, 1, 1);
                        until = from.AddYears(1);
                        break;
                    }
                    if (what.Length > 1 && what[^1] == 'd' &&
                        int.TryParse(what.AsSpan(0, what.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int days) &&
                        days >= 1 && days <= 36_500)
                    {
                        from = now.AddDays(-days);
                        break;
                    }
                    return false;
            }

            if (from > From) From = from;
            if (until < Until) Until = until;
            return true;
        }

        private static SearchKind? KindNamed(string word) => word.ToLowerInvariant() switch
        {
            "folder" or "folders" => SearchKind.Folder,
            "file" or "files" => SearchKind.File,
            "audio" or "music" => SearchKind.Audio,
            "video" or "videos" => SearchKind.Video,
            "image" or "images" or "picture" or "pictures" or "photo" or "photos" => SearchKind.Image,
            "document" or "documents" or "doc" or "docs" => SearchKind.Document,
            "archive" or "archives" => SearchKind.Archive,
            _ => null,
        };

        /// <summary>
        /// Whether an item fits. Size words leave folders out (a folder has no
        /// size of its own here), and an unknown date never fits a date word.
        /// <paramref name="modified"/> is local time.
        /// </summary>
        public bool Fits(ReadOnlySpan<char> name, bool isDir, long size, DateTime modified)
        {
            if (Above >= 0 || Below >= 0)
            {
                if (isDir || size < 0) return false;
                if (Above >= 0 && size <= Above) return false;
                if (Below >= 0 && size >= Below) return false;
            }

            if (From != DateTime.MinValue || Until != DateTime.MaxValue)
            {
                if (modified == default) return false;
                if (modified < From || modified >= Until) return false;
            }

            if (_kinds.Count > 0)
            {
                bool any = false;
                foreach (var kind in _kinds)
                    if (IsKind(kind, name, isDir)) { any = true; break; }
                if (!any) return false;
            }
            return true;
        }

        /// <summary>Whether a name is of a kind, by its extension alone.</summary>
        public static bool IsKind(SearchKind kind, ReadOnlySpan<char> name, bool isDir)
        {
            if (kind == SearchKind.Folder) return isDir;
            if (isDir) return false;
            if (kind == SearchKind.File) return true;

            var extension = Path.GetExtension(name).ToString();
            if (extension.Length == 0) return false;

            return kind switch
            {
                // The player's own list, less the video containers it also plays.
                SearchKind.Audio => !VideoExtensions.Contains(extension) && AudioFiles.IsAudio(name.ToString(), null),
                SearchKind.Video => VideoExtensions.Contains(extension),
                SearchKind.Image => ImageExtensions.Contains(extension),
                SearchKind.Document => DocumentExtensions.Contains(extension),
                SearchKind.Archive => ArchiveFormats.FromName(name.ToString()) != null,
                _ => false,
            };
        }

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".webm", ".3gp", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts", ".flv", ".vob",
        };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff", ".ico", ".svg",
            ".avif", ".raw", ".dng", ".cr2", ".nef", ".arw",
        };

        private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".rtf", ".pdf", ".doc", ".docx", ".odt", ".xls", ".xlsx", ".xlsm", ".ods", ".csv",
            ".ppt", ".pptx", ".odp", ".epub", ".pages", ".numbers", ".key", ".htm", ".html",
        };

        /// <summary>The first day of the week as Windows is set (Control Panel, Region), Monday if unreadable.</summary>
        public static DayOfWeek FirstDayOfWeek => _firstDay ??= ReadFirstDay();
        private static DayOfWeek? _firstDay;

        private static DayOfWeek ReadFirstDay()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\International");
                // 0 is Monday through 6 Sunday.
                if (key?.GetValue("iFirstDayOfWeek") is string text && int.TryParse(text, out int n) && n is >= 0 and <= 6)
                    return (DayOfWeek)((n + 1) % 7);
            }
            catch { }
            return DayOfWeek.Monday;
        }

        /// <summary>
        /// What Google's query language can do of this (folder or not, the
        /// modified range), as clauses to add with "and"; empty when nothing.
        /// Sizes and the other kinds are judged here after Google answers.
        /// </summary>
        public string DriveClauses()
        {
            var clauses = new List<string>();

            bool folders = _kinds.Contains(SearchKind.Folder);
            bool files = _kinds.Exists(k => k != SearchKind.Folder);
            if (Above >= 0 || Below >= 0) { files = true; folders = false; }
            if (folders && !files) clauses.Add($"mimeType = '{DriveClient.FolderMimeType}'");
            else if (files && !folders) clauses.Add($"mimeType != '{DriveClient.FolderMimeType}'");

            if (From != DateTime.MinValue) clauses.Add($"modifiedTime >= '{Rfc3339(From)}'");
            if (Until != DateTime.MaxValue) clauses.Add($"modifiedTime < '{Rfc3339(Until)}'");

            return string.Join(" and ", clauses);
        }

        private static string Rfc3339(DateTime local) =>
            DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}
