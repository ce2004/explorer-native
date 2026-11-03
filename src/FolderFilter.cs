using System;
using System.Collections.Generic;
using System.Globalization;

namespace ExplorerNative
{
    /// <summary>
    /// Filter this folder (Ctrl+F): the rows on screen narrowed in place by the
    /// search rule, without going into subfolders or asking Drive anything.
    /// <typeparamref name="T"/> is the pane's row type.
    /// </summary>
    internal sealed class FolderFilter<T>
    {
        /// <summary>What was typed.</summary>
        public string Text { get; private set; } = "";

        private SearchQuery _query = SearchQuery.Parse("");

        /// <summary>Which list this filter belongs to (folder and search); another list drops it.</summary>
        public string Key { get; }

        /// <summary>Every row of the list, filtered or not.</summary>
        public List<T> All { get; set; }

        private readonly Func<T, (string Name, bool IsDir, long Size, DateTime Modified)> _facts;

        public FolderFilter(string key, List<T> all, Func<T, (string Name, bool IsDir, long Size, DateTime Modified)> facts)
        {
            Key = key;
            All = all;
            _facts = facts;
        }

        /// <summary>Whether anything is typed (an empty filter shows everything).</summary>
        public bool Narrows => Text.Trim().Length > 0;

        public void SetText(string text)
        {
            Text = text ?? "";
            _query = SearchQuery.Parse(Text);
        }

        /// <summary>The rows that pass, in their order.</summary>
        public List<T> Apply(List<T> rows)
        {
            if (!Narrows) return rows;
            var kept = new List<T>();
            foreach (var row in rows)
            {
                var (name, isDir, size, modified) = _facts(row);
                if (_query.Matches(name, "", isDir, size, modified)) kept.Add(row);
            }
            return kept;
        }
    }

    /// <summary>What a filter says.</summary>
    internal static class FolderFilterWords
    {
        public static string Number(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>"Showing 12 of 5,000".</summary>
        public static string Showing(int shown, int total) => $"Showing {Number(shown)} of {Number(total)}";

        /// <summary>"Showing all 5,000 items", or the plain count for one or none.</summary>
        public static string ShowingAll(int total) => total switch
        {
            0 => "Showing everything, the folder is empty",
            1 => "Showing the one item",
            _ => $"Showing all {Number(total)} items",
        };
    }
}
