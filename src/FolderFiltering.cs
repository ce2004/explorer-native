using System;
using System.Collections.Generic;
using Entry = ExplorerNative.ListEntry;

namespace ExplorerNative
{
    /// <summary>
    /// Filter this folder (Ctrl+F), the part that needs no window: which list a
    /// filter belongs to, what happens to rows arriving while one is on, and
    /// what the status bar says. MainForm.Filter.cs is the box and the wiring.
    /// The rule is SearchQuery's; the rows are the pane's own, never a new listing.
    /// </summary>
    internal static class FolderFiltering
    {
        /// <summary>Which list a filter belongs to: the folder, and the search shown in it.</summary>
        internal static string KeyFor(string currentPath, string? searchTerm) => currentPath + "\n" + (searchTerm ?? "");

        /// <summary>A filter over these rows, matching on what the row shows.</summary>
        internal static FolderFilter<Entry> For(string key, List<Entry> rows) =>
            new FolderFilter<Entry>(key, rows, e => (e.Name, e.IsDir, e.Size, e.Modified));

        /// <summary>
        /// Rows on their way into a pane. With a filter on they are kept whole
        /// in the filter and only the matches shown, so a reload or a watcher's
        /// new file follows the filter; rows for another list end it.
        /// </summary>
        internal static List<Entry> Incoming(ref FolderFilter<Entry>? filter, string key, List<Entry> entries)
        {
            if (filter == null) return entries;
            if (filter.Key != key) { filter = null; return entries; }
            filter.All = entries;
            return filter.Apply(entries);
        }

        /// <summary>Rows changed in place while filtered (a file still being written) carried into the whole list.</summary>
        internal static void TakeInPlaceChanges(List<Entry> shown, FolderFilter<Entry> filter)
        {
            if (ReferenceEquals(shown, filter.All)) return;
            var now = new Dictionary<string, Entry>(shown.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var row in shown) now[row.Path] = row;
            var all = new List<Entry>(filter.All.Count);
            foreach (var row in filter.All) all.Add(now.TryGetValue(row.Path, out var newer) ? newer : row);
            filter.All = all;
        }

        /// <summary>The count part of the status bar: "Showing 12 of 5,000, filtered" while a filter is on.</summary>
        internal static string CountSaid(FolderFilter<Entry>? filter, string key, int total) =>
            filter is { Narrows: true } on && on.Key == key
                ? FolderFilterWords.Showing(total, on.All.Count) + ", filtered, Escape shows all"
                : NameRules.Items(total);
    }
}
