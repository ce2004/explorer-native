using System;
using System.Collections.Generic;
using System.Linq;

namespace ExplorerNative
{
    /// <summary>
    /// Favourite folders (5.3.0, Conner): up to nine, kept in order in
    /// <see cref="Settings.FavouriteFolders"/>. Ctrl+D adds the folder you are
    /// in, Ctrl+Shift+1 to 9 goes to one, the Favourites menu lists them and
    /// opens Organise favourites. Pure list rules here; MainForm does the rest.
    /// </summary>
    internal static class Favourites
    {
        /// <summary>One for each of Ctrl+Shift+1 to Ctrl+Shift+9.</summary>
        public const int Most = 9;

        /// <summary>A folder's path as compared: no trailing backslash except on a drive root.</summary>
        public static string Key(string path)
        {
            var trimmed = (path ?? "").Trim();
            if (trimmed.Length > 3) trimmed = trimmed.TrimEnd('\\');
            return trimmed;
        }

        /// <summary>Where <paramref name="folder"/> is in the list, from 0, or -1.</summary>
        public static int IndexOf(IReadOnlyList<string> list, string folder)
        {
            var key = Key(folder);
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(Key(list[i]), key, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>The list with <paramref name="folder"/> added at the end, or null when it is full. Already there: unchanged.</summary>
        public static List<string>? Add(IReadOnlyList<string> list, string folder)
        {
            var copy = Clean(list);
            if (IndexOf(copy, folder) >= 0) return copy;
            if (copy.Count >= Most) return null;
            copy.Add(Key(folder));
            return copy;
        }

        public static List<string> RemoveAt(IReadOnlyList<string> list, int index)
        {
            var copy = Clean(list);
            if (index >= 0 && index < copy.Count) copy.RemoveAt(index);
            return copy;
        }

        /// <summary>The list with one item moved up (-1) or down (+1); unchanged at either end.</summary>
        public static List<string> Move(IReadOnlyList<string> list, int index, int by)
        {
            var copy = Clean(list);
            int to = index + by;
            if (index < 0 || index >= copy.Count || to < 0 || to >= copy.Count) return copy;
            (copy[index], copy[to]) = (copy[to], copy[index]);
            return copy;
        }

        /// <summary>
        /// What a favourite is called: its folder name, or the whole path when
        /// another favourite has the same name (two "Music" folders), and the
        /// drive for a drive root.
        /// </summary>
        public static string Label(IReadOnlyList<string> list, int index)
        {
            var path = Key(list[index]);
            var name = Leaf(path);
            bool twin = list.Where((p, i) => i != index &&
                string.Equals(Leaf(Key(p)), name, StringComparison.OrdinalIgnoreCase)).Any();
            return twin ? path : name;
        }

        private static string Leaf(string path)
        {
            if (AppleMusic.Is(path)) return AppleMusic.DisplayPath(path);
            var name = System.IO.Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? path : name;
        }

        /// <summary>No blanks or repeats, at most <see cref="Most"/>: whatever was in the file.</summary>
        public static List<string> Clean(IReadOnlyList<string>? list)
        {
            var kept = new List<string>();
            foreach (var p in list ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(p) || IndexOf(kept, p) >= 0) continue;
                kept.Add(Key(p));
                if (kept.Count == Most) break;
            }
            return kept;
        }
    }
}
