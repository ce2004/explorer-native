using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Entry = ExplorerNative.ListEntry;

namespace ExplorerNative
{
    // Filter this folder (Ctrl+F): the list narrowed in place as you type.
    // The box and the wiring; the rules are in FolderFiltering.
    public sealed partial class MainForm
    {
        /// <summary>Set while the filter itself puts rows in, so they are not filtered twice.</summary>
        private bool _applyingFilter;

        /// <summary>Which list a filter belongs to: the folder, and the search shown in it.</summary>
        private static string FilterKey(Pane pane) => FolderFiltering.KeyFor(pane.CurrentPath, pane.SearchTerm);

        /// <summary>Rows on their way into a pane, through its filter (<see cref="FolderFiltering.Incoming"/>).</summary>
        private List<Entry> FilterIncoming(Pane pane, List<Entry> entries)
        {
            if (_applyingFilter || pane.Filter == null) return entries;
            return FolderFiltering.Incoming(ref pane.Filter, FilterKey(pane), entries);
        }

        /// <summary>Rows changed in place while filtered (a file still being written) carried into the whole list.</summary>
        private static void TakeInPlaceChanges(Pane pane, FolderFilter<Entry> filter) =>
            FolderFiltering.TakeInPlaceChanges(pane.Entries, filter);

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
                filter = FolderFiltering.For(FilterKey(pane), pane.Entries);
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
            FolderFiltering.CountSaid(pane.Filter, FilterKey(pane), total);
    }
}
