using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ExplorerNative
{
    // Filter this folder (Ctrl+F): the list narrowed in place as you type.
    // The rule is SearchQuery's; the rows are the pane's own, never a new listing.
    public sealed partial class MainForm
    {
        /// <summary>Set while the filter itself puts rows in, so they are not filtered twice.</summary>
        private bool _applyingFilter;

        /// <summary>Which list a filter belongs to: the folder, and the search shown in it.</summary>
        private static string FilterKey(Pane pane) => pane.CurrentPath + "\n" + (pane.SearchTerm ?? "");

        /// <summary>
        /// Rows on their way into a pane. With a filter on they are kept whole
        /// in the filter and only the matches shown, so a reload or a watcher's
        /// new file follows the filter; rows for another list end it.
        /// </summary>
        private List<Entry> FilterIncoming(Pane pane, List<Entry> entries)
        {
            if (_applyingFilter || pane.Filter == null) return entries;
            if (pane.Filter.Key != FilterKey(pane)) { pane.Filter = null; return entries; }
            pane.Filter.All = entries;
            return pane.Filter.Apply(entries);
        }

        /// <summary>Rows changed in place while filtered (a file still being written) carried into the whole list.</summary>
        private static void TakeInPlaceChanges(Pane pane, FolderFilter<Entry> filter)
        {
            if (ReferenceEquals(pane.Entries, filter.All)) return;
            var now = new Dictionary<string, Entry>(pane.Entries.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var row in pane.Entries) now[row.Path] = row;
            var all = new List<Entry>(filter.All.Count);
            foreach (var row in filter.All) all.Add(now.TryGetValue(row.Path, out var newer) ? newer : row);
            filter.All = all;
        }

        private void FilterThisFolder()
        {
            var pane = Active;
            if (string.IsNullOrEmpty(pane.CurrentPath)) return;
            if (pane.Searching)
            {
                Announce("Wait for the search to finish, then filter its results");
                return;
            }

            var filter = pane.Filter;
            if (filter == null || filter.Key != FilterKey(pane))
                filter = new FolderFilter<Entry>(FilterKey(pane), pane.Entries, e => (e.Name, e.IsDir, e.Size, e.Modified));
            pane.Filter = filter;

            var name = pane.SearchTerm != null ? "these results" : FolderDisplayName(pane.CurrentPath);
            DialogResult result;
            using (var box = new FilterForm(name, filter.Text, text => ShowFilter(pane, filter, text)))
                result = box.ShowDialog(this);

            // The list was replaced by another while the box was open.
            if (pane.Filter != filter) { RestoreListFocus(); return; }

            if (result == DialogResult.OK && filter.Narrows)
            {
                // Left filtered, on the first match.
                if (pane.Entries.Count > 0) SelectIndex(pane, 0);
                if (pane == Active) FocusListAndRow(pane, readRowAloud: pane.Entries.Count > 0);
                UpdateStatus();
                return;
            }

            // Escape clears; Enter on an empty box just closes it.
            ClearFilter(pane, say: result != DialogResult.OK);
            RestoreListFocus();
        }

        /// <summary>Narrows the list to what is typed, saying how many show.</summary>
        private void ShowFilter(Pane pane, FolderFilter<Entry> filter, string text)
        {
            if (IsDisposed || pane.Filter != filter) return;
            TakeInPlaceChanges(pane, filter);
            filter.SetText(text);
            var rows = filter.Apply(filter.All);

            _applyingFilter = true;
            try { SetEntries(pane, rows, preferPath: FocusedPathOf(pane)); }
            finally { _applyingFilter = false; }

            if (filter.Narrows)
                AnnounceOperation("filter.shown", FolderFilterWords.Showing(rows.Count, filter.All.Count));
            else
                AnnounceOperation("filter.cleared", FolderFilterWords.ShowingAll(filter.All.Count));
        }

        /// <summary>Puts every row back. Returns false when nothing was filtered.</summary>
        private bool ClearFilter(Pane pane, bool say)
        {
            var filter = pane.Filter;
            if (filter == null) return false;
            TakeInPlaceChanges(pane, filter);
            pane.Filter = null;
            if (filter.Key == FilterKey(pane) && !ReferenceEquals(pane.Entries, filter.All))
                SetEntries(pane, filter.All, preferPath: FocusedPathOf(pane));
            if (say) AnnounceOperation("filter.cleared", FolderFilterWords.ShowingAll(filter.All.Count));
            return true;
        }

        /// <summary>The count part of the status bar: "Showing 12 of 5,000, filtered" while a filter is on.</summary>
        private static string CountSaid(Pane pane, int total) =>
            pane.Filter is { Narrows: true } filter && filter.Key == FilterKey(pane)
                ? FolderFilterWords.Showing(total, filter.All.Count) + ", filtered, Escape shows all"
                : NameRules.Items(total);
    }
}
