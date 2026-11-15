using System;
using System.Collections.Generic;
using System.Linq;

namespace ExplorerNative
{
    /// <summary>
    /// The main window's logic that lives in plain classes (ListSorting,
    /// TransferRules, SelectionSpeech, FolderFiltering), tested as code: these
    /// used to be in MainForm, which needs a message loop, and were checked by
    /// reading its source. Group mainformlogic.
    /// </summary>
    internal static class MainFormLogicTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check("mainformlogic: " + what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal("mainformlogic: " + what, expected, actual);

        private static readonly DateTime Then = new(2024, 5, 1, 12, 0, 0);

        private static ListEntry File(string name, long size, int minutes = 0) =>
            new(@"C:\x\" + name, name, false, size, Then.AddMinutes(minutes));

        private static ListEntry Folder(string name, int minutes = 0) =>
            new(@"C:\x\" + name, name, true, -1, Then.AddMinutes(minutes));

        private static string Names(IEnumerable<ListEntry> rows) => string.Join(",", rows.Select(r => r.Name));

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;
            Console.WriteLine("MainForm's logic, in plain classes:");

            Sorting();
            FolderOwnOrder();
            Merging();
            Transfers();
            Speech();
            Filtering();
        }

        private static void Sorting()
        {
            var rows = new List<ListEntry> { File("b10.txt", 5), Folder("Zeta"), File("b9.txt", 5), File("a.txt", 50), Folder("alpha") };

            var byName = ListSorting.SortLikeAFolder(new List<ListEntry>(rows), ListSorting.ListOrder(SortColumn.Name, true, foldersFirst: true));
            Equal("folders first, then names in Explorer's order (9 before 10)", "alpha,Zeta,a.txt,b9.txt,b10.txt", Names(byName));
            Check("the sort keys are dropped once the list is in order", byName.All(r => r.NameKey == null));

            var mixed = ListSorting.SortLikeAFolder(new List<ListEntry>(rows), ListSorting.ListOrder(SortColumn.Name, true, foldersFirst: false));
            Equal("without folders first, folders sort among the files", "a.txt,alpha,b9.txt,b10.txt,Zeta", Names(mixed));

            var down = ListSorting.SortLikeAFolder(new List<ListEntry>(rows), ListSorting.ListOrder(SortColumn.Name, false, foldersFirst: true));
            Equal("descending turns the names round but folders stay first", "Zeta,alpha,b10.txt,b9.txt,a.txt", Names(down));

            var bySize = ListSorting.SortLikeAFolder(new List<ListEntry>(rows), ListSorting.ListOrder(SortColumn.Size, true, foldersFirst: true));
            Equal("equal sizes fall back to the name, so the order is total", "alpha,Zeta,b9.txt,b10.txt,a.txt", Names(bySize));

            var dated = new List<ListEntry> { File("old.txt", 1, 0), File("new.txt", 1, 30), File("mid.txt", 1, 10) };
            var byDate = ListSorting.SortLikeAFolder(dated, ListSorting.ListOrder(SortColumn.Modified, false, foldersFirst: true));
            Equal("date modified, newest first", "new.txt,mid.txt,old.txt", Names(byDate));

            Check("Type sorts as name (SortsAs)", ListSorting.ComparerFor(SortColumn.Type)(File("a.txt", 9), File("b.txt", 1)) < 0);

            var keyed = new List<ListEntry> { File("x.txt", 1), File("y.txt", 1) };
            ListSorting.PrepareSortKeys(keyed);
            Check("a listing is given its sort keys once", keyed.All(r => r.NameKey != null));

            Equal("sort names are what is said", "name|size|date modified|name",
                string.Join("|", new[] { SortColumn.Name, SortColumn.Size, SortColumn.Modified, SortColumn.Type }.Select(ListSorting.SortName)));
        }

        private static void FolderOwnOrder()
        {
            Equal("a folder's key keeps no trailing slash", @"C:\Music", ListSorting.FolderSortKey(@"C:\Music\"));
            Equal("but a root keeps its slash (C: alone is the current folder on C)", @"C:\", ListSorting.FolderSortKey("C:"));

            var sorts = new Dictionary<string, string> { [@"C:\Music\"] = "Size descending", [@"D:\"] = "modified", [@"E:\Bad"] = "Nonsense ascending" };
            Check("a folder's own order is found whatever its trailing slash",
                ListSorting.FolderSortOf(sorts, @"c:\music") is { By: SortColumn.Size, Ascending: false });
            Check("a direction left out is ascending", ListSorting.FolderSortOf(sorts, "D:") is { By: SortColumn.Modified, Ascending: true });
            Check("an order that will not parse is no order", ListSorting.FolderSortOf(sorts, @"E:\Bad") == null);
            Check("a folder with none has none", ListSorting.FolderSortOf(sorts, @"C:\Other") == null);
            Check("nor does the drives list or a missing table",
                ListSorting.FolderSortOf(sorts, "") == null && ListSorting.FolderSortOf(null, @"C:\Music") == null);
        }

        private static void Merging()
        {
            var order = ListSorting.ListOrder(SortColumn.Name, true, foldersFirst: true);
            var shown = ListSorting.SortLikeAFolder(new List<ListEntry> { Folder("Docs"), File("a.txt", 1), File("c.txt", 1), File("e.txt", 1) }, order);
            var batch = ListSorting.SortLikeAFolder(new List<ListEntry> { File("b.txt", 1), File("f.txt", 1), Folder("Art") }, order);
            var merged = ListSorting.MergeSorted(shown, batch, order);
            Equal("a sorted batch merges into a sorted list in order", "Art,Docs,a.txt,b.txt,c.txt,e.txt,f.txt", Names(merged));
            Check("the merge leaves both lists as they were", shown.Count == 4 && batch.Count == 3);

            // Against a plain sort of everything, over many random batches.
            var random = new Random(7);
            bool same = true;
            for (int round = 0; round < 50 && same; round++)
            {
                var all = Enumerable.Range(0, 200).Select(i => File($"f{random.Next(1000)} {i}.txt", random.Next(5))).ToList();
                var size = ListSorting.ListOrder(SortColumn.Size, round % 2 == 0, foldersFirst: true);
                var a = ListSorting.SortLikeAFolder(all.Take(150).ToList(), size);
                var b = ListSorting.SortLikeAFolder(all.Skip(150).ToList(), size);
                same = Names(ListSorting.MergeSorted(a, b, size)) == Names(ListSorting.SortLikeAFolder(new List<ListEntry>(all), size));
            }
            Check("merging matches sorting the whole list", same);
        }

        private static void Transfers()
        {
            var dest = @"C:\Music";
            var kept = TransferRules.NotAlreadyIn(new[] { @"C:\Music\a.flac", @"C:\Other\b.flac", @"c:\music\C.flac" }, dest);
            Equal("a cut pasted where it already is leaves those items out", @"C:\Other\b.flac", string.Join("|", kept));
            Equal("one item already there is just said", "Already in this folder", TransferRules.AlreadyHereSaid(1));
            Equal("more say there is nothing to move", "Already in this folder; nothing to move", TransferRules.AlreadyHereSaid(3));

            Func<string, bool> onG = p => p.StartsWith(@"G:\", StringComparison.OrdinalIgnoreCase);
            Check("a move mixing Drive and the PC is refused", TransferRules.MixesDrive(new[] { @"G:\a", @"C:\b" }, onG));
            Check("all on Drive is not mixed", !TransferRules.MixesDrive(new[] { @"G:\a", @"G:\b" }, onG));
            Check("all on the PC is not mixed", !TransferRules.MixesDrive(new[] { @"C:\a", @"D:\b" }, onG));
            Equal("and the refusal says why", "Some of these are on Google Drive and some are not; move them separately", TransferRules.MixedMoveSaid);

            Check("a folder moved into itself is refused", TransferRules.IntoItself(@"G:\Music", @"G:\Music"));
            Check("and into its own subfolder", TransferRules.IntoItself(@"G:\Music\", @"g:\music\Albums"));
            Check("but a sibling whose name starts the same is fine", !TransferRules.IntoItself(@"G:\Music", @"G:\Music2"));

            Check("an item directly in the folder is in it", TransferRules.ParentIs(@"G:\a.txt", @"G:\"));
            Check("a root matches with or without its slash", TransferRules.ParentIs(@"G:\a.txt", "G:\\") && TransferRules.ParentIs(@"G:\Sub\a.txt", @"g:\sub\"));
            Check("a deeper item is not directly in it", !TransferRules.ParentIs(@"G:\Sub\a.txt", @"G:\"));
            Check("a broken path is no", !TransferRules.ParentIs("\0", @"G:\"));

            Equal("the conflict answers map to policies", "Overwrite|Skip|AutoRename|FillGaps|",
                string.Join("|", new[]
                {
                    FileOperations.ConflictChoice.Overwrite, FileOperations.ConflictChoice.Skip,
                    FileOperations.ConflictChoice.Rename, FileOperations.ConflictChoice.FillGaps, FileOperations.ConflictChoice.Cancel,
                }.Select(c => TransferRules.PolicyFor(c)?.ToString() ?? "")));

            Equal("copying off Drive goes one file at a time", "1", TransferRules.ThreadsFor(new[] { @"C:\a", @"G:\b" }, onG).ToString());
            Equal("anything else as many as the machine can", FileOperations.RecommendedThreads.ToString(),
                TransferRules.ThreadsFor(new[] { @"C:\a" }, onG).ToString());
            Equal("and with Drive off", FileOperations.RecommendedThreads.ToString(), TransferRules.ThreadsFor(new[] { @"G:\a" }, null).ToString());
        }

        private static void Speech()
        {
            Equal("one row picked out is just its name", "Selected a.flac", SelectionSpeech.RowSelectionSaid("a.flac", true, 1, false));
            Equal("more adds the total", "Selected a.flac, 1,200 selected in all", SelectionSpeech.RowSelectionSaid("a.flac", true, 1200, false));
            Equal("let go of with others still picked", "Not selected a.flac, 3 selected in all", SelectionSpeech.RowSelectionSaid("a.flac", false, 3, false));
            Equal("let go of, the last one", "Not selected a.flac", SelectionSpeech.RowSelectionSaid("a.flac", false, 0, false));
            Equal("with Ctrl held the total waits for Ctrl to be let go", "Selected a.flac", SelectionSpeech.RowSelectionSaid("a.flac", true, 5, true));
            Equal("letting go of Ctrl says the total", "5 items selected", SelectionSpeech.TotalSelectedSaid(5));
            Equal("one item reads naturally", "1 item selected", SelectionSpeech.TotalSelectedSaid(1));
            Equal("and none", "Nothing selected", SelectionSpeech.TotalSelectedSaid(0));
            Equal("Ctrl+A", "Selected 12 items", SelectionSpeech.SelectAllSaid(12));
            Equal("a folder arrived at", "Tab 2, Music, 1 item", SelectionSpeech.FolderSaid("Tab 2, ", "Music", 1));

            var rows = new List<ListEntry> { File("a", 100), Folder("b"), File("c", 0), File("d", 23) };
            Equal("the selection's size counts files only (folders are -1, not measured)", "123",
                SelectionSpeech.SelectedBytes(rows, new[] { 0, 1, 2, 3, 9, -1 }).ToString());
            Check("sizes are added up for an ordinary selection", SelectionSpeech.SumsSizes(10, @"C:\x"));
            Check("but not past the limit", !SelectionSpeech.SumsSizes(SelectionSpeech.MaxSelectionToSum + 1, @"C:\x"));
            Check("nor in Apple Music", !SelectionSpeech.SumsSizes(2, AppleMusic.Root + "\\Library"));
            Equal("the status bar with a size", "12 items, 2 selected (1.5 KB)", SelectionSpeech.StatusSaid("12 items", 2, "1.5 KB"));
            Equal("and without", "12 items, 9000 selected", SelectionSpeech.StatusSaid("12 items", 9000));

            Equal("an empty folder", "This folder is empty", SelectionSpeech.EmptyWords(@"C:\x", null));
            Equal("an empty search", "No results", SelectionSpeech.EmptyWords(@"C:\x", "song"));
            Equal("an empty Apple Music list is a list", "This list is empty", SelectionSpeech.EmptyWords(AppleMusic.Root + "\\Library", null));
            Equal("and Apple Music's search folder says how to search", "No searches yet. Press F1 to search Apple Music",
                SelectionSpeech.EmptyWords(AppleMusic.SearchFolder, null));

            Equal("a folder's size with counts that agree", "Music is 14.2 GB, 3,120 files in 12 folders",
                SelectionSpeech.SizeSentence("Music", "14.2 GB", 3120, 12, true));
            Equal("no 'in 0 folders', and one file is one", "Notes is 3 bytes, 1 file", SelectionSpeech.SizeSentence("Notes", "3 bytes", 1, 0, true));
            Equal("a walk that stopped is a lower bound", "Music is at least 2 GB; measuring stopped before the end",
                SelectionSpeech.SizeSentence("Music", "2 GB", 10, 1, false));
        }

        private static void Filtering()
        {
            Equal("a filter belongs to a folder and the search in it", "C:\\x\nsong", FolderFiltering.KeyFor(@"C:\x", "song"));
            Check("so the same folder without the search is another list", FolderFiltering.KeyFor(@"C:\x", null) != FolderFiltering.KeyFor(@"C:\x", "song"));

            var rows = new List<ListEntry> { File("live.flac", 1), File("notes.txt", 1), Folder("Live") };
            var key = FolderFiltering.KeyFor(@"C:\x", null);
            FolderFilter<ListEntry>? filter = FolderFiltering.For(key, rows);
            filter.SetText("live");
            Equal("it matches what the row shows, folders too", "2", filter.Apply(filter.All).Count.ToString());

            // A file being written changes in place while the list is filtered.
            var shown = new List<ListEntry> { new(@"C:\x\live.flac", "live.flac", false, 999, Then) };
            FolderFiltering.TakeInPlaceChanges(shown, filter);
            Equal("rows changed in place are carried into the whole list", "999", filter.All[0].Size.ToString());
            Equal("and the others are kept", "3", filter.All.Count.ToString());

            FolderFilter<ListEntry>? none = null;
            Check("with no filter rows come through untouched",
                ReferenceEquals(FolderFiltering.Incoming(ref none, key, rows), rows) && none == null);
            var arriving = new List<ListEntry>(rows) { File("live 2.flac", 1) };
            var passed = FolderFiltering.Incoming(ref filter, key, arriving);
            Check("rows arriving for the list are filtered and kept whole", passed.Count == 3 && filter != null && filter.All == arriving);
            Equal("the status bar says how many show", "Showing 3 of 4, filtered, Escape shows all", FolderFiltering.CountSaid(filter, key, 3));
            Check("rows for another list end the filter", FolderFiltering.Incoming(ref filter, FolderFiltering.KeyFor(@"C:\y", null), arriving) == arriving && filter == null);
            Equal("and the count is the plain one again", "4 items", FolderFiltering.CountSaid(filter, key, 4));
        }
    }
}
