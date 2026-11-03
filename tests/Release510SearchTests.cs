using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// 5.1.0: search by size, date and kind, and filter this folder as you type.
    /// </summary>
    internal static class Release510SearchTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("5.1.0, search: size, date and kind words, and filter as you type:");
            SizeDateKindWords();
            LocalWalkUsesFacts();
            DriveUsesFacts();
            FilterAsYouType();
            await Task.CompletedTask;
        }

        private sealed record Row(string Name, bool IsDir, long Size, DateTime Modified);

        private static void FilterAsYouType()
        {
            var rows = new List<Row>();
            for (int i = 0; i < 5000; i++) rows.Add(new Row($"track {i}.{(i % 3 == 0 ? "flac" : "txt")}", false, i, Now));
            rows.Add(new Row("Live", true, -1, Now));

            var filter = new FolderFilter<Row>("C:\\x\n", rows, r => (r.Name, r.IsDir, r.Size, r.Modified));
            Check("an empty filter shows every row", ReferenceEquals(filter.Apply(rows), rows) && !filter.Narrows);
            filter.SetText("kind:audio size>4900b");
            var shown = filter.Apply(filter.All);
            Equal("the filter uses the search rule, size and kind words too", "33", shown.Count.ToString());
            Check("and keeps the folder's order", shown[0].Name == "track 4902.flac");
            filter.SetText("live");
            Check("folders match by name", filter.Apply(filter.All) is { Count: 1 } one && one[0].IsDir);

            // A new row arriving (the watcher reloading) is filtered the same way.
            rows.Add(new Row("live set.flac", false, 10, Now));
            filter.All = rows;
            Equal("new rows respect the filter", "2", filter.Apply(filter.All).Count.ToString());

            Equal("the count is said", "Showing 12 of 5,000", FolderFilterWords.Showing(12, 5000));
            Equal("and all of it", "Showing all 5,000 items", FolderFilterWords.ShowingAll(5000));
            Equal("one item reads naturally", "Showing the one item", FolderFilterWords.ShowingAll(1));

            // MainForm needs a message loop; its wiring is checked in the source.
            var main = Source("MainForm.cs");
            var wiring = Source("MainForm.Filter.cs");
            Check("Ctrl+F is on the Go menu with its own access key",
                main != null && main.Contains("Item(\"&Filter this folder…\", Keys.Control | Keys.F, FilterThisFolder)", StringComparison.Ordinal));
            Check("every way rows reach a pane goes through the filter",
                main != null && CountOf(main, "entries = FilterIncoming(pane, entries);") == 3);
            Check("Escape in a filtered list clears it",
                main != null && main.Contains("case Keys.Escape when pane.Filter != null:", StringComparison.Ordinal));
            Check("the status bar says how many show",
                main != null && CountOf(main, "CountSaid(pane, total)") == 3);
            Check("rows for another folder end the filter",
                wiring != null && wiring.Contains("if (pane.Filter.Key != FilterKey(pane)) { pane.Filter = null; return entries; }", StringComparison.Ordinal));
            Check("Ctrl+F is in the shortcuts list",
                BuiltInShortcuts.Lines().Exists(l => l.StartsWith("Ctrl+F:", StringComparison.Ordinal)));
        }

        private static int CountOf(string text, string what)
        {
            int n = 0;
            for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            var path = dir == null ? null : Path.Combine(dir.FullName, "src", name);
            return path != null && File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private static readonly DateTime Now = new(2026, 11, 4, 15, 30, 0);   // a Wednesday

        private static void SizeDateKindWords()
        {
            const long MB = 1024 * 1024;
            var big = SearchQuery.Parse("size>100mb", Now);
            Check("size>100mb takes a 200 MB file", big.Matches("a.flac", "", false, 200 * MB, Now));
            Check("and leaves out a 50 MB one", !big.Matches("a.flac", "", false, 50 * MB, Now));
            Check("and leaves out folders", !big.Matches("a", "", true, -1, Now));
            Check("size words are not name words", big.DriveHints.Count == 0);

            var range = SearchQuery.Parse("SIZE>1KB size<1MB", Now);
            Check("size bounds combine, any case", range.Matches("x", "", false, 5000, Now) &&
                                                   !range.Matches("x", "", false, 2 * MB, Now) &&
                                                   !range.Matches("x", "", false, 100, Now));
            Check("size>1gb is gigabytes", SearchQuery.Parse("size>1gb", Now).Matches("x", "", false, 2L << 30, Now) &&
                                           !SearchQuery.Parse("size>1gb", Now).Matches("x", "", false, 900 * MB, Now));

            var bad = SearchQuery.Parse("size>abc", Now);
            Check("size>abc is an ordinary word", bad.Matches("size>abc.txt", "", false, 1, Now) &&
                                                  !bad.Matches("other.txt", "", false, 1, Now) && !bad.HasFacts);
            Check("so is modified:someday", !SearchQuery.Parse("modified:someday", Now).HasFacts);
            Check("and kind:banana", !SearchQuery.Parse("kind:banana", Now).HasFacts);
            Check("and a quoted size word", !SearchQuery.Parse("\"size>1mb\"", Now).HasFacts);

            var today = SearchQuery.Parse("modified:today", Now);
            Check("modified:today takes this morning", today.Matches("a", "", false, 1, Now.Date.AddHours(8)));
            Check("but not yesterday", !today.Matches("a", "", false, 1, Now.Date.AddHours(-1)));
            Check("an unknown date never fits a date word", !today.Matches("a", "", false, 1, default));
            var yesterday = SearchQuery.Parse("modified:yesterday", Now);
            Check("modified:yesterday", yesterday.Matches("a", "", false, 1, Now.Date.AddHours(-3)) &&
                                        !yesterday.Matches("a", "", false, 1, Now.Date.AddHours(1)));
            var week = SearchQuery.Parse("modified:thisweek", Now);
            var weekStart = Now.Date.AddDays(-(((int)Now.DayOfWeek - (int)SearchFacts.FirstDayOfWeek + 7) % 7));
            Check("modified:thisweek starts on the first day of the week",
                week.Matches("a", "", false, 1, weekStart) && !week.Matches("a", "", false, 1, weekStart.AddSeconds(-1)));
            var month = SearchQuery.Parse("modified:thismonth", Now);
            Check("modified:thismonth", month.Matches("a", "", false, 1, new DateTime(2026, 11, 1, 0, 0, 1)) &&
                                        !month.Matches("a", "", false, 1, new DateTime(2026, 10, 31, 23, 0, 0)));
            var days = SearchQuery.Parse("modified:7d", Now);
            Check("modified:7d is the last seven days", days.Matches("a", "", false, 1, Now.AddDays(-6)) &&
                                                        !days.Matches("a", "", false, 1, Now.AddDays(-8)));
            var year = SearchQuery.Parse("modified:2025", Now);
            Check("modified:2025 is that year", year.Matches("a", "", false, 1, new DateTime(2025, 6, 1)) &&
                                                !year.Matches("a", "", false, 1, new DateTime(2026, 1, 1)));

            Check("kind:audio takes flac, not mp4 or txt",
                SearchQuery.Parse("kind:audio", Now).Matches("x.FLAC", "", false, 1, Now) &&
                !SearchQuery.Parse("kind:audio", Now).Matches("x.mp4", "", false, 1, Now) &&
                !SearchQuery.Parse("kind:audio", Now).Matches("x.txt", "", false, 1, Now));
            Check("kind:video takes mp4", SearchQuery.Parse("kind:video", Now).Matches("x.mp4", "", false, 1, Now));
            Check("kind:image takes jpg", SearchQuery.Parse("kind:image", Now).Matches("x.jpg", "", false, 1, Now));
            Check("kind:document takes docx", SearchQuery.Parse("kind:document", Now).Matches("x.docx", "", false, 1, Now));
            Check("kind:archive takes tar.gz and 7z",
                SearchQuery.Parse("kind:archive", Now).Matches("x.tar.gz", "", false, 1, Now) &&
                SearchQuery.Parse("kind:archive", Now).Matches("x.7z", "", false, 1, Now));
            Check("kind:folder takes folders only",
                SearchQuery.Parse("kind:folder", Now).Matches("x", "", true, -1, Now) &&
                !SearchQuery.Parse("kind:folder", Now).Matches("x", "", false, 1, Now));
            Check("kind:file takes files only",
                !SearchQuery.Parse("kind:file", Now).Matches("x", "", true, -1, Now) &&
                SearchQuery.Parse("kind:file", Now).Matches("x", "", false, 1, Now));

            var mixed = SearchQuery.Parse("kind:audio love -remix size>1mb \"i love\"", Now);
            Check("words, quotes, exclusions and facts combine in any order",
                mixed.Matches("I love you.flac", "", false, 5 * MB, Now) &&
                !mixed.Matches("I love you remix.flac", "", false, 5 * MB, Now) &&
                !mixed.Matches("I love you.flac", "", false, 10, Now) &&
                !mixed.Matches("I love you.txt", "", false, 5 * MB, Now));
            Equal("only the words go to Google as names", "love|i love", string.Join("|", mixed.DriveHints));
        }

        private static void LocalWalkUsesFacts()
        {
            var root = Path.Combine(Path.GetTempPath(), "en-510search-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Music"));
                File.WriteAllBytes(Path.Combine(root, "Music", "big.flac"), new byte[3000]);
                File.WriteAllBytes(Path.Combine(root, "small.flac"), new byte[10]);
                File.WriteAllBytes(Path.Combine(root, "notes.txt"), new byte[3000]);
                var old = Path.Combine(root, "old.flac");
                File.WriteAllBytes(old, new byte[3000]);
                File.SetLastWriteTime(old, new DateTime(2020, 5, 1));

                string Names(string term)
                {
                    var hits = FileSearch.Local(root, term, true, true, 100, CancellationToken.None).Hits;
                    var names = hits.ConvertAll(h => h.Name);
                    names.Sort(StringComparer.OrdinalIgnoreCase);
                    return string.Join(",", names);
                }

                Equal("the walk applies size and kind", "big.flac,old.flac", Names("kind:audio size>2kb"));
                Equal("and a year", "old.flac", Names("modified:2020"));
                Equal("and kind:folder alone lists folders", "Music", Names("kind:folder"));
                Equal("and words with a date", "big.flac,small.flac", Names(".flac modified:today"));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static void DriveUsesFacts()
        {
            Equal("Google is asked for folders and dates",
                "name contains 'love' and mimeType = 'application/vnd.google-apps.folder' and modifiedTime >= '" +
                new DateTime(2025, 1, 1).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "' and modifiedTime < '" +
                new DateTime(2026, 1, 1).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "' and trashed = false",
                DriveClient.NameQuery(SearchQuery.Parse("love kind:folder modified:2025", Now).DriveHints,
                    SearchQuery.Parse("love kind:folder modified:2025", Now).DriveClauses));
            Equal("sizes ask Google for files only", "mimeType != 'application/vnd.google-apps.folder' and trashed = false",
                DriveClient.NameQuery(Array.Empty<string>(), SearchQuery.Parse("size>1mb", Now).DriveClauses));
            Equal("plain words ask as before", "name contains 'love' and trashed = false",
                DriveClient.NameQuery(SearchQuery.Parse("love", Now).DriveHints, SearchQuery.Parse("love", Now).DriveClauses));

            // Sizes and kinds Google cannot judge are judged after it answers.
            var found = new List<DriveFound>
            {
                new("1", "a.flac", "audio/flac", 5_000_000, DateTime.UtcNow, "root"),
                new("2", "b.flac", "audio/flac", 10, DateTime.UtcNow, "root"),
                new("3", "c.txt", "text/plain", 5_000_000, DateTime.UtcNow, "root"),
            };
            var kept = DriveSearch.Answering(SearchQuery.Parse("kind:audio size>1mb"), found, "root",
                new Dictionary<string, string?>(), new Dictionary<string, string>());
            Equal("Drive hits are filtered by size and kind", "a.flac", string.Join(",", kept.ConvertAll(k => k.Name)));
        }
    }
}
