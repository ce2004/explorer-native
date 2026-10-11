using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ExplorerNative
{
    /// <summary>The tags the tag editor can change, in the order it shows them.</summary>
    public enum TagField { Title, Artist, AlbumArtist, Album, Composer, Genre, Year, Track }

    public static class TagFields
    {
        public static readonly TagField[] All =
        {
            TagField.Title, TagField.Artist, TagField.AlbumArtist, TagField.Album,
            TagField.Composer, TagField.Genre, TagField.Year, TagField.Track,
        };

        public static string Label(TagField field) => field switch
        {
            TagField.Title => "Title",
            TagField.Artist => "Artist",
            TagField.AlbumArtist => "Album artist",
            TagField.Album => "Album",
            TagField.Composer => "Composer",
            TagField.Genre => "Genre",
            TagField.Year => "Year",
            _ => "Track number",
        };

        public static bool IsNumber(TagField field) => field is TagField.Year or TagField.Track;

        /// <summary>
        /// Stored by Windows as lists of strings. Typed with semicolons between
        /// them, which is also how Windows shows them.
        /// </summary>
        public static bool IsList(TagField field) => field is TagField.Artist or TagField.Composer or TagField.Genre;

        public static string[] SplitList(string text) =>
            text.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();

        /// <summary>A year up to 9999, a track number up to 99999. Blank is allowed elsewhere, not here.</summary>
        public static bool TryParseNumber(TagField field, string text, out uint number)
        {
            if (!uint.TryParse(text.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out number)) return false;
            return field == TagField.Year ? number <= 9999 : number <= 99999;
        }
    }

    /// <summary>What one field shows: the value all files agree on, or that they differ.</summary>
    public sealed record TagFieldState(string Value, bool Differs);

    /// <summary>One file's outcome: null error means its tags were saved.</summary>
    public sealed record TagResult(string Path, string? Error);

    /// <summary>
    /// The tag editor's work, without its window: what to show for several
    /// files at once, which files to refuse by name, writing every file on the
    /// worker it is called on, and the one sentence that reports it.
    /// </summary>
    public static class TagEditing
    {
        public const string DiffersText = "different values, left unchanged";

        public const string PlayingRefusal = "It is playing; stop it and try again.";
        public const string DriveRefusal = "It is on Google Drive, and tags cannot be edited there yet.";
        public const string OggRefusal =
            "Windows cannot write tags to Ogg or Opus files, and Explorer Native does not write them itself yet.";

        /// <summary>
        /// Ogg Vorbis, Ogg Opus and their relatives. Windows reads their tags at
        /// best and has no handler that writes them.
        /// </summary>
        public static bool IsOggFamily(string path)
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".opus", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".oga", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".spx", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Why a file will not be written, decided from its path and the player
        /// alone, with no read. Null when it is worth trying.
        /// </summary>
        public static string? RefusalFor(string path, Func<string, bool>? onDrive, Func<string, bool>? isPlaying)
        {
            if (onDrive != null && onDrive(path)) return DriveRefusal;
            if (IsOggFamily(path)) return OggRefusal;
            if (isPlaying != null && isPlaying(path)) return PlayingRefusal;
            return null;
        }

        /// <summary>
        /// The fields as the editor shows them for these files: a value where
        /// every file agrees, and Differs where they do not. Files that could
        /// not be read (null) are left out of the vote.
        /// </summary>
        public static Dictionary<TagField, TagFieldState> Merge(IEnumerable<IReadOnlyDictionary<TagField, string>?> files)
        {
            var read = files.Where(f => f != null).Select(f => f!).ToList();
            var merged = new Dictionary<TagField, TagFieldState>();
            foreach (var field in TagFields.All)
            {
                string? first = null;
                bool differs = false;
                foreach (var file in read)
                {
                    var value = file.TryGetValue(field, out var v) ? v ?? "" : "";
                    if (first == null) first = value;
                    else if (!string.Equals(first, value, StringComparison.Ordinal)) { differs = true; break; }
                }
                merged[field] = differs ? new TagFieldState("", true) : new TagFieldState(first ?? "", false);
            }
            return merged;
        }

        /// <summary>
        /// Writes <paramref name="changes"/> (only the fields the user edited) to
        /// every file, in order. With <paramref name="numberInOrder"/>, each file
        /// also gets its place in the list as its track number. Blocking: call
        /// it on a worker.
        /// </summary>
        public static List<TagResult> Apply(IReadOnlyList<string> paths, IReadOnlyDictionary<TagField, string> changes,
            bool numberInOrder, Func<string, bool>? onDrive, Func<string, bool>? isPlaying)
        {
            var results = new List<TagResult>(paths.Count);
            for (int i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                var refusal = RefusalFor(path, onDrive, isPlaying);
                if (refusal != null) { results.Add(new TagResult(path, refusal)); continue; }

                var values = new Dictionary<TagField, string>(changes);
                if (numberInOrder) values[TagField.Track] = (i + 1).ToString();

                string? error;
                try { error = AudioTags.WriteFields(path, values); }
                catch (Exception ex) { error = ex.Message; }
                results.Add(new TagResult(path, error));
            }
            return results;
        }

        /// <summary>
        /// The one message for the whole run: "Tags saved for 12 files", or
        /// "Saved for 10 files, 2 failed: name, reason".
        /// </summary>
        public static string Summary(IReadOnlyList<TagResult> results)
        {
            int saved = results.Count(r => r.Error == null);
            var failed = results.Where(r => r.Error != null).ToList();

            if (failed.Count == 0)
                return results.Count == 1 ? "Tags saved" : $"Tags saved for {results.Count} files";

            var reasons = string.Join(" ", failed.Select(r => $"{SafeName(r.Path)}, {r.Error}"));

            if (results.Count == 1) return "Tags not saved: " + reasons;
            if (saved == 0) return $"Tags not saved for any of the {results.Count} files: " + reasons;
            return $"Saved for {Files(saved)}, {failed.Count} failed: " + reasons;
        }

        private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

        private static string SafeName(string path)
        {
            try { return Path.GetFileName(path); } catch { return path; }
        }
    }
}
