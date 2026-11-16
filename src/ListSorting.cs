using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Entry = ExplorerNative.ListEntry;

namespace ExplorerNative
{
    /// <summary>
    /// How the panes' rows are ordered: the comparison for a column, folders
    /// first, a folder's own order, and merging a sorted batch into a sorted
    /// list. Moved out of MainForm, which only says which folder and which
    /// settings; nothing here needs a window.
    /// </summary>
    internal static class ListSorting
    {
        /// <summary>
        /// The sort order every list in this application uses: the chosen column,
        /// the chosen direction, and folders first when that is asked for.
        ///
        /// Shared with search results rather than written twice. Results came
        /// back sorted by name and direction only, so somebody with "folders
        /// first" set got it everywhere except the one list where a folder and a
        /// file are most easily confused for each other.
        /// </summary>
        internal static List<Entry> SortLikeAFolder(List<Entry> result, Comparison<Entry> order)
        {
            PrepareSortKeys(result);
            result.Sort(order);

            // In order now, and nothing sorts this list again: a new order is a
            // new listing. The keys would only be memory.
            foreach (var row in result) row.NameKey = null;
            return result;
        }

        /// <summary>
        /// Gives each row the key its name sorts by, so the sort compares bytes
        /// instead of calling into Windows n log n times. Rows that already have
        /// one, a progressive load keyed them batch by batch, keep it. Spread
        /// over the cores for a big folder; this runs on a worker, apart from
        /// search results, which stop at ten thousand.
        /// </summary>
        internal static void PrepareSortKeys(List<Entry> rows)
        {
            if (rows.Count >= 8192)
                Parallel.For(0, rows.Count, i => rows[i].NameKey ??= NameRules.LogicalKey(rows[i].Name));
            else
                foreach (var row in rows) row.NameKey ??= NameRules.LogicalKey(row.Name);
        }

        /// <summary>
        /// A folder's own order from File, Sort this folder by, as kept in
        /// Settings.FolderSorts ("Size descending"), or null when it has none.
        /// </summary>
        internal static (SortColumn By, bool Ascending)? FolderSortOf(Dictionary<string, string>? sorts, string? folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            if (sorts == null || sorts.Count == 0) return null;

            var key = FolderSortKey(folder);
            string? value = null;
            foreach (var pair in sorts)
                if (string.Equals(FolderSortKey(pair.Key), key, StringComparison.OrdinalIgnoreCase)) { value = pair.Value; break; }
            if (value == null) return null;

            var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || !Enum.TryParse<SortColumn>(words[0], ignoreCase: true, out var by)) return null;
            bool ascending = words.Length < 2 || !words[1].Equals("descending", StringComparison.OrdinalIgnoreCase);
            return (by, ascending);
        }

        internal static string FolderSortKey(string folder)
        {
            var trimmed = Path.TrimEndingDirectorySeparator(folder);
            // "C:" alone means the current directory on C:, so a root keeps its slash.
            return trimmed.EndsWith(':') ? trimmed + "\\" : trimmed;
        }

        internal static string SortName(SortColumn column) => NameRules.SortsAs(column) switch
        {
            SortColumn.Size => "size",
            SortColumn.Modified => "date modified",
            _ => "name",
        };

        /// <summary>
        /// The whole order as one comparison: folders first when asked, then the
        /// column, then the direction. Total, because every column falls back to
        /// the name and the name to the path, which is what lets a progressive
        /// load sort each batch on the worker and merge it into what is already
        /// shown, instead of re-sorting everything on the UI thread every tick.
        /// </summary>
        internal static Comparison<Entry> ListOrder(SortColumn by, bool ascending, bool foldersFirst)
        {
            var cmp = ComparerFor(by);
            return (a, b) =>
            {
                if (foldersFirst && a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
                int c = cmp(a, b);
                return ascending ? c : -c;
            };
        }

        /// <summary>
        /// A short sorted batch merged into a long sorted list: each new entry's
        /// place is found by binary search from the last one's, and the rows in
        /// between are copied without being compared. That is m log n
        /// comparisons rather than n + m, which matters because a logical name
        /// comparison is a call into Windows, and a plain merge of 100,000 rows
        /// spent 80ms of the UI thread on one tick doing them.
        /// </summary>
        internal static List<Entry> MergeSorted(List<Entry> into, List<Entry> batch, Comparison<Entry> order)
        {
            var merged = new List<Entry>(into.Count + batch.Count);
            int from = 0;
            foreach (var item in batch)
            {
                int lo = from, hi = into.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) >>> 1;
                    if (order(into[mid], item) <= 0) lo = mid + 1;
                    else hi = mid;
                }
                for (int k = from; k < lo; k++) merged.Add(into[k]);
                merged.Add(item);
                from = lo;
            }
            for (int k = from; k < into.Count; k++) merged.Add(into[k]);
            return merged;
        }

        /// <summary>
        /// The comparison, chosen once rather than re-decided inside every
        /// comparison. Sorting is n log n calls, so a switch on a settings
        /// property in the body was evaluated a quarter of a million times on a
        /// twenty-thousand file folder for an answer that never changes.
        ///
        /// Every comparison falls back to the name, so the order is total: List
        /// .Sort is unstable, and without a tie-break equal-sized files shuffled
        /// on every refresh.
        /// </summary>
        internal static Comparison<Entry> ComparerFor(SortColumn column) => NameRules.SortsAs(column) switch
        {
            SortColumn.Size => static (a, b) =>
            {
                int c = a.Size.CompareTo(b.Size);
                return c != 0 ? c : CompareNames(a, b);
            },
            SortColumn.Modified => static (a, b) =>
            {
                int c = a.Modified.CompareTo(b.Modified);
                return c != 0 ? c : CompareNames(a, b);
            },
            _ => static (a, b) => CompareNames(a, b),
        };

        internal static int CompareNames(Entry a, Entry b) =>
            NameRules.CompareNames(a.Name, a.NameKey, a.Path, b.Name, b.NameKey, b.Path);
    }
}
