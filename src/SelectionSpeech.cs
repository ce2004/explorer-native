using System;
using System.Collections;
using System.Collections.Generic;
using Entry = ExplorerNative.ListEntry;

namespace ExplorerNative
{
    /// <summary>
    /// What the main window says about the selection and the list: a row
    /// picked out or let go, the running total, the status bar's counts and
    /// sizes, an empty list, a folder's size. MainForm decides when; these
    /// decide the words. Nothing here needs a window.
    /// </summary>
    internal static class SelectionSpeech
    {
        /// <summary>Summing sizes across a very large selection is not worth the pause it causes.</summary>
        internal const int MaxSelectionToSum = 5000;

        /// <summary>
        /// A row picked out or let go with Space or Ctrl+Space.
        ///
        /// The count only when it adds something: "song.flac selected, 1
        /// selected" said the same thing twice. Nor while Ctrl is held: the
        /// total is said once, when it is let go (List_KeyUp).
        /// </summary>
        internal static string RowSelectionSaid(string name, bool isIn, int selected, bool ctrlHeld) =>
            (isIn && selected == 1) || ctrlHeld
                ? $"{(isIn ? "Selected" : "Not selected")} {name}"
            : selected == 0 ? $"Not selected {name}"
            : $"{(isIn ? "Selected" : "Not selected")} {name}, {selected:N0} selected in all";

        /// <summary>The total, said when Ctrl is let go after picking rows out.</summary>
        internal static string TotalSelectedSaid(int selected) =>
            selected == 0 ? "Nothing selected" : $"{NameRules.Items(selected)} selected";

        /// <summary>Ctrl+A reporting what it did.</summary>
        internal static string SelectAllSaid(int count) => $"Selected {NameRules.Items(count)}";

        /// <summary>A folder arrived at, when that is said: the tab, where, and how many.</summary>
        internal static string FolderSaid(string tab, string where, int count) =>
            $"{tab}{where}, {count} item{(count == 1 ? "" : "s")}";

        /// <summary>Whether the status bar adds up the selection's sizes. Nothing in Apple Music has a size to add up.</summary>
        internal static bool SumsSizes(int selected, string folder) =>
            !(selected > MaxSelectionToSum || AppleMusic.Is(folder));

        /// <summary>The bytes in the selected rows, files only.</summary>
        internal static long SelectedBytes(List<Entry> entries, IEnumerable selectedIndices)
        {
            long bytes = 0;
            foreach (int i in selectedIndices)
            {
                if (i < 0 || i >= entries.Count) continue;
                var entry = entries[i];
                // Folders carry -1 as "not measured"; adding it would understate
                // the total by one byte per folder in the selection.
                if (!entry.IsDir && entry.Size > 0) bytes += entry.Size;
            }
            return bytes;
        }

        /// <summary>
        /// The status bar with something selected: what is listed, how many are
        /// selected, and their size when it was added up.
        /// </summary>
        internal static string StatusSaid(string listed, int selected, string? size = null) =>
            size == null ? $"{listed}, {selected} selected" : $"{listed}, {selected} selected ({size})";

        /// <summary>What an empty list is called: the same words navigation uses.</summary>
        internal static string EmptyWords(string currentPath, string? searchTerm) =>
            searchTerm != null ? "No results"
            : string.Equals(currentPath, AppleMusic.SearchFolder, StringComparison.OrdinalIgnoreCase)
                ? "No searches yet. Press F1 to search Apple Music"
            : AppleMusic.Is(currentPath) ? "This list is empty"
            : "This folder is empty";

        /// <summary>
        /// "Music is 14.2 GB, 3,120 files in 12 folders", counts that agree
        /// with their nouns, and no "in 0 folders". A walk that ran out of time
        /// says it is a lower bound.
        /// </summary>
        internal static string SizeSentence(string name, string size, long files, long folders, bool complete)
        {
            if (!complete) return $"{name} is at least {size}; measuring stopped before the end";
            static string Of(long n, string noun) => $"{n:N0} {noun}{(n == 1 ? "" : "s")}";
            var contents = folders > 0 ? $"{Of(files, "file")} in {Of(folders, "folder")}" : Of(files, "file");
            return $"{name} is {size}, {contents}";
        }
    }
}
