using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using System;

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

    // ---- merged from MainFixTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// The main window's performance and correctness fixes: Explorer's name
    /// order, type-ahead past accents and emoji, the folder watcher's debounce,
    /// what Reset keeps, the update messages, and the source rules for the
    /// context menu, the hidden window and removable drives.
    /// </summary>
    internal static class MainFixTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Main window fixes:");
            SortOrder();
            TypeAheadFolding();
            Debounce();
            ResetKeeps();
            UpdateMessages();
            SourceRules();
            Console.WriteLine();
        }

        private static int Cmp(string a, string b) => NameRules.CompareNames(a, @"C:\x\" + a, b, @"C:\x\" + b);

        private static string[] Sorted(IEnumerable<string> names)
        {
            var list = names.ToList();
            list.Sort(Cmp);
            return list.ToArray();
        }

        private static void SortOrder()
        {
            Check("Track 2 comes before Track 10", Cmp("Track 2.flac", "Track 10.flac") < 0);
            Check("file (2).txt comes before file (10).txt", Cmp("file (2).txt", "file (10).txt") < 0);
            Check("and file (9) before file (10)", Cmp("file (9).txt", "file (10).txt") < 0);
            Check("names still ignore case", Cmp("apple", "Banana") < 0 && Cmp("Banana", "apple") > 0);

            Check("Éclair sorts among the E names, before Zebra", Cmp("Éclair", "Zebra") < 0);
            Check("between Eagle and Egg", Cmp("Eagle", "Éclair") < 0 && Cmp("Éclair", "Egg") < 0);
            Check("étoile before Zebra", Cmp("étoile", "Zebra") < 0);
            Check("a name starting with a symbol comes before the letters", Cmp("~tilde", "apple") < 0);

            // Total: names the comparison calls equal still get an order, and the
            // same folder sorts the same way however it was enumerated.
            Check("case-only twins are decided", Cmp("readme", "README") != 0);
            Check("accent-only twins are decided", Cmp("resume", "résumé") != 0 || Cmp("résumé", "resume") != 0);
            var names = new[] { "Track 10", "track 2", "Track 2", "Éclair", "eclair", "Zebra", "~x", "日本語", "file (10)", "file (2)" };
            var one = Sorted(names);
            var two = Sorted(names.AsEnumerable().Reverse());
            var three = Sorted(names.OrderBy(n => n.GetHashCode()));
            Check("the order is the same whatever order the names arrive in",
                one.SequenceEqual(two) && one.SequenceEqual(three), string.Join(" | ", one));
            Check("and every pair is antisymmetric",
                names.All(a => names.All(b => Math.Sign(Cmp(a, b)) == -Math.Sign(Cmp(b, a)))));
        }

        private static int Find(string[] names, string query, int at) =>
            TypeAhead.Find(names.Length, i => names[i], query, at);

        private static void TypeAheadFolding()
        {
            Equal("folding takes the accents off", "Eclair etoile Arger", TypeAhead.Fold("Éclair étoile Ärger"));
            Check("plain ASCII comes back as the same string", ReferenceEquals("plain.txt", TypeAhead.Fold("plain.txt")));

            var names = Sorted(new[] { "apple", "Éclair", "egg", "étoile", "zebra", "🎶 one", "🎶 two", "🎵 other" });

            // The jump lands where the sort put the first E name.
            int firstE = Array.FindIndex(names, n => TypeAhead.Fold(n).StartsWith("e", StringComparison.OrdinalIgnoreCase));
            Equal("typing e lands on the first E name in the list's own order",
                firstE.ToString(), Find(names, "e", -1).ToString());
            Equal("and that name is Éclair", "Éclair", names[Find(names, "e", -1)]);
            Equal("typing \"et\" reaches étoile", "étoile", names[Find(names, "et", 0)]);
            Equal("typing the accent reaches it too", "étoile", names[Find(names, "ét", 0)]);
            Equal("typing E again cycles on to egg", "egg", names[Find(names, "ee", Find(names, "e", -1))]);

            int note = Find(names, "🎶", -1);
            Check("an emoji finds a name starting with it", note >= 0 && names[note].StartsWith("🎶"), note.ToString());
            int next = Find(names, "🎶🎶", note);
            Check("the same emoji twice cycles to the next one",
                next >= 0 && next != note && names[next].StartsWith("🎶"), next.ToString());
            Equal("and a third time comes back round", note.ToString(), Find(names, "🎶🎶🎶", next).ToString());
            Equal("a different emoji is a different prefix", "🎵 other", names[Find(names, "🎵", -1)]);
        }

        /// <summary>
        /// Plays a timeline of watcher events through the debounce the way the
        /// window does: the first event of a burst arms a one-shot timer, a timer
        /// that fires early waits out the rest, and a due timer takes the result.
        /// </summary>
        private static (int Reloads, int InPlace, int Wakes) Simulate(IEnumerable<(long At, bool Reload, string Path)> events, long until)
        {
            var d = new WatchDebounce();
            long timerAt = -1;
            int reloads = 0, inPlace = 0, wakes = 0;

            void RunTimerUpTo(long now)
            {
                while (timerAt >= 0 && timerAt <= now)
                {
                    long t = timerAt;
                    timerAt = -1;
                    wakes++;
                    int left = d.DueIn(t);
                    if (left > 0) { timerAt = t + left; continue; }
                    var (reload, changed) = d.Take();
                    if (reload) reloads++;
                    else if (changed.Count > 0) inPlace++;
                }
            }

            foreach (var (at, reload, path) in events.OrderBy(e => e.At))
            {
                RunTimerUpTo(at);
                bool started = reload ? d.Reload(at) : d.Changed(path, at);
                if (started && timerAt < 0) timerAt = at + Math.Max(1, d.DueIn(at));
            }
            RunTimerUpTo(until);
            return (reloads, inPlace, wakes);
        }

        private static void Debounce()
        {
            // A download: created once, then written every 100ms for 6.6 seconds.
            var download = new List<(long, bool, string)> { (0, true, "") };
            for (long t = 100; t <= 6600; t += 100) download.Add((t, false, @"C:\d\big.iso.part"));
            var (reloads, inPlace, _) = Simulate(download, 20_000);
            Equal("a download being written reloads the folder once", "1", reloads.ToString());
            Check($"and updates its row in place ({inPlace} times, every few seconds)", inPlace is >= 1 and <= 4, inPlace.ToString());

            // Five thousand files arriving over 15.7 seconds.
            var burst = new List<(long, bool, string)>();
            for (int i = 0; i < 5000; i++) burst.Add((i * 15_700L / 5000, true, ""));
            var (burstReloads, _, wakes) = Simulate(burst, 40_000);
            Check($"a 5,000-file burst is {burstReloads} reloads, at most one per three seconds (was 26)",
                burstReloads is >= 5 and <= 7, burstReloads.ToString());
            Check($"and the timer woke {wakes} times, not every 600ms", wakes < 40, wakes.ToString());

            // A burst that ends quickly is read once, half a second after it ends.
            var quick = new WatchDebounce();
            quick.Reload(0);
            quick.Reload(100);
            Equal("a short burst waits for quiet", "500", quick.DueIn(100).ToString());
            Equal("then is due", "0", quick.DueIn(600).ToString());

            // A ceiling, however busy the folder.
            var busy = new WatchDebounce();
            for (long t = 0; t < 3000; t += 50) busy.Changed(@"C:\d\log.txt", t);
            Equal("a folder that never goes quiet is still looked at after three seconds", "0", busy.DueIn(3000).ToString());

            var none = new WatchDebounce();
            Equal("nothing waiting is -1, and the timer stops", "-1", none.DueIn(0).ToString());

            // Too many changed files is a reload, not a list of rows.
            var many = new WatchDebounce();
            for (int i = 0; i <= WatchDebounce.MostInPlace; i++) many.Changed($@"C:\d\{i}.txt", i);
            var (manyReload, manyChanged) = many.Take();
            Check("hundreds of changed files become one reload", manyReload && manyChanged.Count == 0);

            var mixed = new WatchDebounce();
            mixed.Changed(@"C:\d\a.txt", 0);
            mixed.Reload(10);
            var (mixedReload, mixedChanged) = mixed.Take();
            Check("a reload covers the rows that changed alongside it", mixedReload && mixedChanged.Count == 0);
            Check("and taking ends the wait", !mixed.Pending && mixed.DueIn(20) == -1);
        }

        private static void ResetKeeps()
        {
            var old = new Settings
            {
                ConnectCode = "123456",
                DriveSyncPairs = new List<DriveSyncPair> { new() },
                RegisteredExePath = @"C:\Programs\ExplorerNative.exe",
                FoldersTakenAt = 638_000_000_000_000_000,
                WindowX = 111, WindowY = 222, WindowWidth = 1333, WindowHeight = 777,
                ShowHiddenFiles = !new Settings().ShowHiddenFiles,
                SortAscending = !new Settings().SortAscending,
            };

            var reset = SettingsReset.Defaults(old, @"C:\one", @"D:\two");
            Check("reset keeps the web app's pairing code", reset.ConnectCode == "123456");
            Check("and the Drive sync pairs", reset.DriveSyncPairs.Count == 1);
            Check("and where the registration points", reset.RegisteredExePath == old.RegisteredExePath);
            Check("and when folders were taken", reset.FoldersTakenAt == old.FoldersTakenAt);
            Check("and the window's size and place",
                reset.WindowX == 111 && reset.WindowY == 222 && reset.WindowWidth == 1333 && reset.WindowHeight == 777);
            Check("and the folders each tab is in", reset.LastFolder == @"C:\one" && reset.LastFolderTab2 == @"D:\two");
            Check("while the preferences go back to their defaults",
                reset.ShowHiddenFiles == new Settings().ShowHiddenFiles && reset.SortAscending == new Settings().SortAscending);
            Check("and the question says what is kept",
                SettingsReset.KeptSentence.Contains("pairing code") && SettingsReset.KeptSentence.Contains("sync pairs"));
        }

        private static void UpdateMessages()
        {
            var refusal = new Updater.UpdateException(Updater.Problem.RateLimited, "GitHub is limiting requests. Try again later.");
            Equal("the updater's own sentence is shown as it is",
                "GitHub is limiting requests. Try again later.", NameRules.PlainUpdateFailure(refusal));

            var wrapped = new Updater.UpdateException(Updater.Problem.Refused, "The download was refused.",
                new HttpRequestException("socket"));
            Equal("even with a network failure inside it", "The download was refused.", NameRules.PlainUpdateFailure(wrapped));

            Equal("a network failure says GitHub could not be reached",
                "GitHub could not be reached: no route", NameRules.PlainUpdateFailure(new HttpRequestException("no route")));
            Equal("a timeout says so", "GitHub did not answer in time.",
                NameRules.PlainUpdateFailure(new TaskCanceledException()));
            Equal("anything else is its message", "disk full", NameRules.PlainUpdateFailure(new IOException("disk full")));
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? SourceFiles.Read(path) : null;
        }

        private static void SourceRules()
        {
            var main = Source("MainForm.cs");
            var shell = Source("ShellContextMenu.cs");
            if (main == null || shell == null) { Check("the window's source can be read", false); return; }

            Check("closing the Windows menu without choosing is its own outcome",
                shell.Contains("if (selected == 0) return MenuOutcome.Dismissed;", StringComparison.Ordinal));
            int menu = main.IndexOf("private async void ShowShellMenuForSelection()", StringComparison.Ordinal);
            int menuEnd = menu < 0 ? -1 : main.IndexOf("private async void CalculateFocusedFolderSize()", menu, StringComparison.Ordinal);
            var menuBody = menu >= 0 && menuEnd > menu ? main[menu..menuEnd] : "";
            Check("and neither a dismissal nor a command refreshes or searches again",
                menuBody.Contains("MenuOutcome.Dismissed", StringComparison.Ordinal) &&
                !menuBody.Contains("Refresh_()", StringComparison.Ordinal) &&
                !menuBody.Contains("RunSearchAsync", StringComparison.Ordinal), menuBody.Length.ToString());
            Check("the menu parses each item against the parent it already bound",
                shell.Contains("parentFolder.ParseDisplayName(", StringComparison.Ordinal));

            Check("the watcher no longer polls every 600ms", !main.Contains("WatchSettleMs", StringComparison.Ordinal));
            Check("changes go through the debounce",
                main.Contains("pane.Debounce.Reload(", StringComparison.Ordinal) &&
                main.Contains("pane.Debounce.Changed(", StringComparison.Ordinal));

            int visible = main.IndexOf("protected override void OnVisibleChanged", StringComparison.Ordinal);
            var visibleBody = visible >= 0 ? main.Substring(visible, Math.Min(1500, main.Length - visible)) : "";
            Check("hidden, the window stops its availability poll, its watch timer and its watchers",
                visibleBody.Contains("_availabilityTimer?.Stop()", StringComparison.Ordinal) &&
                visibleBody.Contains("_watchTimer?.Stop()", StringComparison.Ordinal) &&
                visibleBody.Contains("DisarmWatcher(pane)", StringComparison.Ordinal));
            int ctor = main.IndexOf("public MainForm(Settings settings)", StringComparison.Ordinal);
            int ctorEnd = ctor < 0 ? -1 : main.IndexOf("private bool _initialising;", ctor, StringComparison.Ordinal);
            Check("and the poll is not started before the window is shown",
                ctor >= 0 && ctorEnd > ctor && !main[ctor..ctorEnd].Contains("_availabilityTimer.Start()", StringComparison.Ordinal));

            // The watcher is built on a worker (a sleeping share made it seconds
            // on the UI thread), armed before the listing, and the listing forgets
            // only what happened before it.
            int arm = main.IndexOf("private void ArmWatcher(", StringComparison.Ordinal);
            int armEnd = arm < 0 ? -1 : main.IndexOf("private static void DisarmWatcher(", arm, StringComparison.Ordinal);
            var armBody = arm >= 0 && armEnd > arm ? main[arm..armEnd] : "";
            Check("a folder watcher is built off the UI thread and installed only if still wanted",
                armBody.Contains("await Task.Run(() => BuildWatcher(", StringComparison.Ordinal) &&
                armBody.Contains("pane.WatchGeneration == generation", StringComparison.Ordinal));
            int nav = main.IndexOf("private async Task<bool> NavigateAsync(", StringComparison.Ordinal);
            int load = nav < 0 ? -1 : main.IndexOf("bool arrived = await LoadEntriesAsync(", nav, StringComparison.Ordinal);
            var beforeListing = nav >= 0 && load > nav ? main[nav..load] : "";
            Check("a navigation watches its folder before listing it, and forgets only earlier marks",
                beforeListing.Contains("ArmWatcher(pane, full);", StringComparison.Ordinal) &&
                beforeListing.Contains("pane.Debounce.Clear();", StringComparison.Ordinal) &&
                !armBody.Contains("Debounce.Clear()", StringComparison.Ordinal));
            Check("showing from the tray watches only the tab in front",
                visibleBody.Contains("if (pane == Active) ArmWatcher(pane, pane.CurrentPath);", StringComparison.Ordinal));
            Check("folder sizes and the local search never walk into Drive",
                main.Contains("_folderSizes.Skip = OnDriveFolder;", StringComparison.Ordinal) &&
                main.Contains("skip: OnDriveFolder", StringComparison.Ordinal));
            Check("refreshed and changed are said only when the reload landed",
                main.Contains("if (landed && !ours) AnnounceOperation(\"nav.changed\"", StringComparison.Ordinal) &&
                main.Contains("if (IsDisposed || !landed) return;", StringComparison.Ordinal));
            Check("a progressive load holds rows that would move the cursor",
                main.Contains("fresh = fresh.FindAll(e => _order(e, marked) >= 0);", StringComparison.Ordinal));

            Check("a drive being removed lets go of its watcher",
                main.Contains("WM_DEVICECHANGE", StringComparison.Ordinal) &&
                main.Contains("case DBT_DEVICEQUERYREMOVE:", StringComparison.Ordinal) &&
                main.Contains("RegisterDeviceNotificationW", StringComparison.Ordinal));

            Check("a progressive load merges sorted batches instead of re-sorting on the UI thread",
                main.Contains("_shown = ListSorting.MergeSorted(_shown, fresh, _order);", StringComparison.Ordinal) &&
                !main.Contains("_form.SortLikeAFolder(new List<Entry>(_shown))", StringComparison.Ordinal));

            Check("type-ahead searches the names folded when the folder was listed",
                main.Contains("TypeAhead.FindFolded(", StringComparison.Ordinal) &&
                !main.Contains("TypeAhead.Find(", StringComparison.Ordinal) &&
                main.Contains("entry.PrepareTypeAhead();", StringComparison.Ordinal));

            Check("a listing is sorted by keys made once per name, and drops them after",
                main.Contains("PrepareSortKeys(result);", StringComparison.Ordinal) &&
                main.Contains("PrepareSortKeys(sorted);", StringComparison.Ordinal) &&
                main.Contains("row.NameKey = null;", StringComparison.Ordinal));

            Check("sort by Type goes through SortsAs",
                main.Contains("ComparerFor(SortColumn column) => NameRules.SortsAs(column) switch", StringComparison.Ordinal));

            Check("a cancelled extract does not announce what it replaced",
                main.Contains("if (!result.Cancelled) AnnounceConflicts(result.Collisions);", StringComparison.Ordinal));
        }
    }

    // ---- merged from Sweep308MainFormTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// Main window sweep after 3.0.7. Folders whose name ends in a dot open now,
    /// and everything else the window does to the folder on screen has to reach
    /// that folder too: the idle poll, Backspace, the fallback walk, the watcher,
    /// and New folder or New file inside it. Plus the drive list read again
    /// silently (Preferences saved) no longer reading its row over the message.
    ///
    /// MainForm is not in the suite, so the decisions are pinned from its
    /// source; the reason behind them is shown on a real folder.
    /// </summary>
    internal static class Sweep308MainFormTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        public static void RunAll(Action<string, bool, string?> check)
        {
            _check = check;
            Console.WriteLine("Main window sweep (3.0.8):");
            TrailingDotFolderIsOnlyReachedExactly();
            SourceRules();
            Console.WriteLine();
        }

        /// <summary>Why the fixes are needed: plain calls lose the dot, exact ones keep it.</summary>
        private static void TrailingDotFolderIsOnlyReachedExactly()
        {
            var root = Path.Combine(Path.GetTempPath(), "ExplorerNativeTests_sweep308_" + Guid.NewGuid().ToString("N")[..8]);
            var dotted = Path.Combine(root, "Backup.");
            try
            {
                Directory.CreateDirectory(NameRules.ExactPath(Path.Combine(dotted, "Disc 1")));

                Check("a folder ending in a dot is not found by a plain Directory.Exists",
                    !Directory.Exists(dotted));
                Check("and is found through NameRules.ExactPath",
                    Directory.Exists(NameRules.ExactPath(dotted)));

                var inner = Path.Combine(dotted, "Disc 1");
                Check("Path.GetDirectoryName keeps the dot (what Backspace now uses)",
                    string.Equals(Path.GetDirectoryName(inner), dotted, StringComparison.OrdinalIgnoreCase),
                    Path.GetDirectoryName(inner));
                Check("Directory.GetParent loses it (what Backspace used to use)",
                    !string.Equals(Directory.GetParent(inner)?.FullName, dotted, StringComparison.OrdinalIgnoreCase),
                    Directory.GetParent(inner)?.FullName);

                using (var watcher = new FileSystemWatcher(NameRules.ExactPath(dotted)))
                {
                    var reported = Path.Combine(watcher.Path, "x.txt");
                    Check("a watcher's exact path made plain matches the listing's rows",
                        string.Equals(NameRules.PlainPath(reported), Path.Combine(dotted, "x.txt"),
                            StringComparison.OrdinalIgnoreCase), reported);
                }

                var made = Path.Combine(dotted, "New folder");
                Directory.CreateDirectory(NameRules.ExactPath(made));
                Check("New folder made exactly lands inside the dotted folder",
                    Directory.Exists(NameRules.ExactPath(made)) &&
                    !Directory.Exists(Path.Combine(root, "Backup", "New folder")));
            }
            catch (Exception ex)
            {
                Check("trailing-dot folder checks ran", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(NameRules.ExactPath(root), recursive: true); } catch { }
            }
        }

        private static void SourceRules()
        {
            var src = Source("MainForm.cs");
            if (src == null) { Check("MainForm.cs found", false); return; }

            var probe = Body(src, "Task<FolderProbe> ProbeDirectoryAsync(");
            Check("the folder probe asks about the exact path",
                probe.Contains("NameRules.ExactPath(", StringComparison.Ordinal), Excerpt(probe));

            Check("nothing in the window takes a parent through Directory.GetParent",
                !src.Contains("Directory.GetParent(", StringComparison.Ordinal));

            var goUp = Body(src, "void GoUp(");
            Check("Backspace takes the parent by text",
                goUp.Contains("SafeParent(", StringComparison.Ordinal), Excerpt(goUp));

            var ancestor = Body(src, "Task<string> NearestExistingAncestorAsync(");
            Check("the fallback walk goes up by text",
                ancestor.Contains("SafeParent(", StringComparison.Ordinal), Excerpt(ancestor));

            var build = Body(src, "RemovalWatch?) BuildWatcher(");
            Check("the watcher is built on the exact path",
                build.Contains("new FileSystemWatcher(NameRules.ExactPath(", StringComparison.Ordinal), Excerpt(build));
            Check("and the rows it reports are made plain before they are matched",
                build.Contains("NameRules.PlainPath(e.FullPath)", StringComparison.Ordinal), Excerpt(build));

            var arm = Body(src, "void ArmWatcher(");
            Check("re-arming compares the watcher's path made plain, so a refresh keeps it",
                arm.Contains("NameRules.PlainPath(pane.Watcher.Path)", StringComparison.Ordinal), Excerpt(arm));

            var register = Body(src, "static RemovalWatch? TryRegister(");
            Check("the eject registration opens the exact folder",
                register.Contains("CreateFileW(NameRules.ExactPath(", StringComparison.Ordinal), Excerpt(register));

            var newFolder = Body(src, "async void CreateNewFolder(");
            Check("New folder creates through the exact path",
                newFolder.Contains("Directory.CreateDirectory(NameRules.ExactPath(", StringComparison.Ordinal),
                Excerpt(newFolder));

            var newFile = Body(src, "async void CreateNewFile(");
            Check("New file creates through the exact path",
                newFile.Contains("new FileStream(NameRules.ExactPath(", StringComparison.Ordinal), Excerpt(newFile));

            var drives = Body(src, "void LoadDrivesInto(");
            Check("the drive list knows when its reload is quiet",
                drives.Contains("bool quiet", StringComparison.Ordinal), Excerpt(drives));
            Check("and a quiet reload on the same drive does not read the row",
                drives.Contains("readRowAloud: !(quiet && sameRow)", StringComparison.Ordinal), Excerpt(drives));
            var navigate = Body(src, "Task<bool> NavigateAsync(");
            int call = navigate.IndexOf("LoadDrivesInto(", StringComparison.Ordinal);
            int callEnd = call < 0 ? -1 : navigate.IndexOf(';', call);
            var callText = callEnd < 0 ? "" : navigate[call..callEnd];
            Check("NavigateAsync hands its quiet on to the drive list",
                callText.Contains("quiet", StringComparison.Ordinal), callText);
        }

        /// <summary>A class-level member's text, from its signature to its closing brace.</summary>
        private static string Body(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return "";
            int end = src.IndexOf("\n        }", start, StringComparison.Ordinal);
            return end < 0 ? src[start..] : src[start..end];
        }

        private static string Excerpt(string text) => text.Length <= 160 ? text : text[..160] + "…";

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? SourceFiles.Read(path) : null;
        }
    }

    // ---- merged from Sweep309MainFormTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// Main window sweep after 3.0.8. A multi-selection survives the list being
    /// focused again (a transfer finishing, a re-sort, a quiet reload inside an
    /// archive); an archive folder overtaken before it was shown leaves the pane
    /// where it was; a startup folder that will not open lands on a drive list
    /// that is really there; opening a big archive is not thrown away by the
    /// folder watcher; and two Drive paths reach folders ending in a dot.
    ///
    /// MainForm is not in the suite, so the decisions are pinned from its
    /// source; the reason behind the dot fixes is shown on a real folder.
    /// </summary>
    internal static class Sweep309MainFormTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;
            Console.WriteLine("Main window sweep (3.0.9):");
            FileInsideDottedFolderIsOnlyFoundExactly();
            SourceRules();
            Console.WriteLine();
        }

        /// <summary>Why the move-out check needs the exact path: a plain FileInfo under "Backup." misses the copy.</summary>
        private static void FileInsideDottedFolderIsOnlyFoundExactly()
        {
            var root = Path.Combine(Path.GetTempPath(), "ExplorerNativeTests_sweep309_" + Guid.NewGuid().ToString("N")[..8]);
            var copy = Path.Combine(root, "Backup.", "song.flac");
            try
            {
                Directory.CreateDirectory(NameRules.ExactPath(Path.Combine(root, "Backup.")));
                File.WriteAllBytes(NameRules.ExactPath(copy), new byte[1234]);

                Check("a copy inside a folder ending in a dot is not found by a plain FileInfo",
                    !new FileInfo(copy).Exists);
                var exact = new FileInfo(NameRules.ExactPath(copy));
                Check("and is found, at its real length, through NameRules.ExactPath",
                    exact.Exists && exact.Length == 1234, exact.Exists ? exact.Length.ToString() : "missing");
                _equal("an ordinary path is left as it is", Path.Combine(root, "plain", "x"),
                    NameRules.ExactPath(Path.Combine(root, "plain", "x")));
            }
            catch (Exception ex)
            {
                Check("dotted-folder file checks ran", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(NameRules.ExactPath(root), recursive: true); } catch { }
            }
        }

        private static void SourceRules()
        {
            var src = Source("MainForm.cs");
            if (src == null) { Check("MainForm.cs found", false); return; }

            // a. Focusing the list again keeps a selection of several.
            var focus = Body(src, "void FocusListAndRow(");
            Check("focusing the list again asks how many rows are selected",
                focus.Contains("SelectedCountOf(", StringComparison.Ordinal), Excerpt(focus));
            Check("and with several only re-applies the focus",
                focus.Contains("RefocusIndex(", StringComparison.Ordinal), Excerpt(focus));
            var refocus = Body(src, "void RefocusIndex(");
            Check("re-applying the focus never touches the selected state",
                refocus.Length > 0 && refocus.Contains("LVIS_FOCUSED", StringComparison.Ordinal) &&
                !refocus.Contains("LVIS_SELECTED", StringComparison.Ordinal), Excerpt(refocus));

            var resort = Body(src, "async void ResortActive(");
            Check("a re-sort puts a selection of several back",
                resort.Contains("Reselect(", StringComparison.Ordinal), Excerpt(resort));

            // b. An archive folder is committed only once it is still wanted.
            var show = Body(src, "Task<bool> ShowArchiveFolderAsync(");
            int overtaken = show.IndexOf("pane.NavigationId != mine", StringComparison.Ordinal);
            int moved = show.IndexOf("pane.CurrentPath = full", StringComparison.Ordinal);
            Check("an archive folder moves the pane only after checking it is still the navigation",
                overtaken >= 0 && moved > overtaken, $"check at {overtaken}, move at {moved}");
            int unwatched = show.IndexOf("DisarmWatcher(pane)", StringComparison.Ordinal);
            Check("and lets the old folder's watcher go only then",
                unwatched > overtaken, $"check at {overtaken}, disarm at {unwatched}");
            Check("a quiet reload inside an archive keeps a selection of several",
                show.Contains("Reselect(", StringComparison.Ordinal), Excerpt(show));

            var open = Body(src, "Task OpenArchiveAsync(");
            Check("opening an archive is marked as a navigation in progress",
                open.Contains("pane.Probing = navigation", StringComparison.Ordinal), Excerpt(open));
            Check("and one overtaken before it was shown gives the pane its archive back",
                open.Contains("pane.Archive = before", StringComparison.Ordinal), Excerpt(open));

            // c. A first navigation that fails lands on a drive list that is there.
            var navigate = Body(src, "Task<bool> NavigateAsync(");
            int failed = navigate.IndexOf("if (!arrived)", StringComparison.Ordinal);
            int landedEnd = failed < 0 ? -1 : navigate.IndexOf("return pane.NavigationId == mine &&", failed, StringComparison.Ordinal);
            var notArrived = failed < 0 || landedEnd < 0 ? "" : navigate[failed..landedEnd];
            Check("a folder that would not open with nothing before it loads the drive list",
                notArrived.Contains("string.IsNullOrEmpty(cameFrom)", StringComparison.Ordinal) &&
                notArrived.Contains("NavigateAsync(DrivesPath", StringComparison.Ordinal), Excerpt(notArrived));

            // Trailing dots on two Drive paths.
            var finish = Body(src, "Task<int> FinishMoveOutOfDrive(");
            Check("a move out of Drive checks the copy at its exact path",
                finish.Contains("new FileInfo(NameRules.ExactPath(arrivedAt))", StringComparison.Ordinal), Excerpt(finish));

            var ends = Body(src, " DeleteOtherSyncEnds(List<");
            Check("the other end of a sync pair is asked about by its exact path",
                ends.Contains("NameRules.ExactPath(pair.LocalFolder)", StringComparison.Ordinal) &&
                !ends.Contains("Directory.Exists(pair.LocalFolder)", StringComparison.Ordinal), Excerpt(ends));
        }

        /// <summary>A class-level member's text, from its signature to its closing brace.</summary>
        private static string Body(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return "";
            int end = src.IndexOf("\n        }", start, StringComparison.Ordinal);
            return end < 0 ? src[start..] : src[start..end];
        }

        private static string Excerpt(string text) => text.Length <= 160 ? text : text[..160] + "…";

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? SourceFiles.Read(path) : null;
        }
    }

    // ---- merged from Sweep500MainATests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// The 5.0.0 sweep of the window: UI, list, tabs and navigation.
    /// </summary>
    internal static class Sweep500MainATests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the window: UI, list, tabs and navigation:");
            RestartAndUpdateAskAboutDriveUploads();
            DrivesListFollowsDrivesComingAndGoing();
            TagEditorAsksAboutThePlayingSongFirst();
            MenuAccessKeysAreUnique();
            StartupLoadsTheFrontTabFirst();
            RestartComesBackToTheDriveFolder();
            await Task.CompletedTask;
        }

        // Bug 6: a restart probed the remembered Drive folder before the mount,
        // opened home, and the next quit saved home over the Drive place.
        private static void RestartComesBackToTheDriveFolder()
        {
            Check("a Drive folder before the mount is waited for",
                NavigationHistory.WaitsForDrive(@"G:\Music", "G", letterShown: false));
            Check("the letter in any case and with a colon",
                NavigationHistory.WaitsForDrive(@"g:\Music", "G:", letterShown: false));
            Check("once the letter is there it is probed like any folder",
                !NavigationHistory.WaitsForDrive(@"G:\Music", "G", letterShown: true));
            Check("with Drive off nothing is waited for",
                !NavigationHistory.WaitsForDrive(@"G:\Music", null, letterShown: false));
            Check("another drive's folder is not waited for",
                !NavigationHistory.WaitsForDrive(@"C:\Users\x", "G", letterShown: false));
            Check("a share is not waited for",
                !NavigationHistory.WaitsForDrive(@"\\nas\share", "G", letterShown: false));
            Equal("a place follows Drive to the letter it got", @"H:\Music\a.flac",
                NavigationHistory.RebaseOnLetter(@"G:\Music\a.flac", "H") ?? "");

            var main = Source("MainForm.cs");
            Check("startup keeps a Drive folder to wait for instead of probing it",
                Body(main, "FirstExistingFolderAsync(params").Contains("WaitsForDrive(", StringComparison.Ordinal));
            Check("the mount takes a waiting tab to its Drive folder",
                Body(main, "public void RefreshIfShowingDrive(").Contains("ResumeWaitingForDrive(", StringComparison.Ordinal));
            Check("the resume leaves a tab alone once somebody has moved",
                Body(main, "private void ResumeWaitingForDrive(").Contains("StillWaitingForDrive(", StringComparison.Ordinal));
            Check("a quit before the mount saves the Drive folder, not the stand-in",
                Body(main, "private void CaptureState()").Contains("StillWaitingForDrive(", StringComparison.Ordinal));
        }

        // Bug 3: startup loaded the hidden second tab first (both lists off screen
        // meanwhile), read its row aloud, and loaded it even with one tab.
        private static void StartupLoadsTheFrontTabFirst()
        {
            var main = Source("MainForm.cs");
            var init = Body(main, "private async Task InitialiseTabsAsync(");
            int shown = init.IndexOf("ShowActivePane();", StringComparison.Ordinal);
            int firstNav = init.IndexOf("NavigateAsync(", StringComparison.Ordinal);
            Check("startup shows the front tab before loading anything", shown >= 0 && firstNav > shown);
            int back = init.IndexOf("LoadTabAsync(", StringComparison.Ordinal);
            Check("the tab behind is loaded after the front one", back > firstNav && firstNav >= 0);
            int twoTabs = init.LastIndexOf("TwoTabs", back < 0 ? init.Length - 1 : back, StringComparison.Ordinal);
            Check("the tab behind is loaded only with two tabs", back >= 0 && twoTabs >= 0 && twoTabs > firstNav);

            var load = Body(main, "private async Task LoadTabAsync(");
            Check("the tab behind loads silently into its own pane",
                load.Contains("quiet: true", StringComparison.Ordinal) && load.Contains("onPane:", StringComparison.Ordinal));

            var capture = Body(main, "private void CaptureState()");
            int empty = capture.IndexOf("IsNullOrEmpty(", StringComparison.Ordinal);
            int last = capture.IndexOf("LastFolder", StringComparison.Ordinal);
            Check("a tab not loaded yet keeps its saved folder", empty >= 0 && last > empty);

            Check("switching to a tab never loaded loads it rather than re-reading nothing",
                Body(main, "private void SetActiveTab(").Contains("LoadTabAsync(", StringComparison.Ordinal));
            Check("turning two tabs on loads the second",
                Body(main, "public void ApplyNewSettings(").Contains("LoadTabAsync(", StringComparison.Ordinal));
        }

        // Bug 8: two visible items sharing an access key make the letter cycle
        // between them instead of running one (Shift+F10 then R did nothing).
        private static void MenuAccessKeysAreUnique()
        {
            var main = Source("MainForm.cs");

            static char? KeyOf(string label)
            {
                for (int i = 0; i < label.Length - 1; i++)
                {
                    if (label[i] != '&') continue;
                    if (label[i + 1] == '&') { i++; continue; }
                    return char.ToUpperInvariant(label[i + 1]);
                }
                return null;
            }

            static List<(string Label, char Key)> Labels(string slice, params string[] leaveOut)
            {
                var found = new List<(string, char)>();
                foreach (var line in slice.Split('\n'))
                {
                    if (leaveOut.Any(f => line.Contains(f, StringComparison.Ordinal))) continue;
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                                 line, "(?:Item|ToolStripMenuItem)\\(\"([^\"]*)\""))
                        if (KeyOf(m.Groups[1].Value) is char k) found.Add((m.Groups[1].Value, k));
                }
                return found;
            }

            void Distinct(string what, List<(string Label, char Key)> items)
            {
                var clashes = items.GroupBy(i => i.Key).Where(g => g.Count() > 1)
                    .Select(g => g.Key + ": " + string.Join(" / ", g.Select(i => i.Label))).ToList();
                Check(what, items.Count > 3 && clashes.Count == 0, string.Join("; ", clashes));
            }

            string Slice(string from, string to)
            {
                int a = main.IndexOf(from, StringComparison.Ordinal);
                int b = a < 0 ? -1 : main.IndexOf(to, a, StringComparison.Ordinal);
                return a < 0 || b < 0 ? "" : main.Substring(a, b - a);
            }

            var context = Body(main, "private void BuildContextMenu()");
            string[] appleOnly = { "_playNextItem", "_favoriteItem", "_addToPlaylistItem", "_goToArtistItem", "_goToAlbumItem",
                                   "_libraryItem", "_appleShuffleItem", "_appleRepeatItem", "_appleUpNextItem",
                                   "_playAllItem", "_shuffleAllItem", "_playAllNextItem", "_startStationItem",
                                   "_copySongLinkItem", "_copyLyricsItem" };
            string[] trashOnly = { "_restoreItem", "_deleteForeverItem", "_emptyTrashItem" };
            Distinct("the Shift+F10 menu's access keys are unique for files, archives and audio",
                Labels(context, appleOnly.Concat(trashOnly).Append("_testArchiveItem").ToArray()));
            // Test archive shows only on archives, Edit audio only on audio files.
            Distinct("the Shift+F10 menu's access keys are unique for archives",
                Labels(context, appleOnly.Concat(trashOnly).Append("_editAudioItem").ToArray()));
            Distinct("the Shift+F10 menu's access keys are unique for Apple Music rows",
                Labels(context, trashOnly.Concat(new[] { "_playAllItem", "_shuffleAllItem", "_playAllNextItem" }).ToArray()).Where(i => appleOnly.Any(f => context.Contains(f + " = Item(\"" + i.Label, StringComparison.Ordinal))).ToList());

            var file = Slice("new ToolStripMenuItem(\"&File\")", "menu.Items.Add(file)");
            var fileItems = Labels(file).Skip(1).ToList(); // the menu's own name is not one of its items
            var sortLabel = Labels(Body(main, "private ToolStripMenuItem BuildSortMenu()")).FirstOrDefault();
            if (sortLabel.Label != null && !file.Contains(sortLabel.Label, StringComparison.Ordinal)) fileItems.Add(sortLabel);
            Distinct("the File menu's access keys are unique", fileItems);

            Distinct("the Edit menu's access keys are unique",
                Labels(Slice("new ToolStripMenuItem(\"&Edit\")", "menu.Items.Add(edit)")).Skip(1).ToList());
        }

        // Bug 7: Edit tags on the playing song let you type everything, then
        // refused on Save and threw it all away.
        private static void TagEditorAsksAboutThePlayingSongFirst()
        {
            var among = TagEditing.LoadedAmong(new[] { @"C:\a.mp3", @"C:\B.MP3" }, @"c:\b.mp3");
            Equal("the loaded song is found among the files, ignoring case", @"C:\B.MP3", string.Join("|", among));
            Equal("nothing loaded holds nothing", "0", TagEditing.LoadedAmong(new[] { @"C:\a.mp3" }, null).Length.ToString());
            Equal("another song loaded holds nothing", "0", TagEditing.LoadedAmong(new[] { @"C:\a.mp3" }, @"C:\z.mp3").Length.ToString());

            var edit = Body(Source("MainForm.cs"), "private async Task<bool> EditTagsAsync(");
            int asked = edit.IndexOf("LoadedAmong(", StringComparison.Ordinal);
            int dialog = edit.IndexOf("new TagEditorForm", StringComparison.Ordinal);
            Check("the player is checked before the tag editor opens", asked >= 0 && dialog > asked);
            Check("the playing check after Save stays as a backstop",
                edit.IndexOf("IsPlaying", dialog < 0 ? 0 : dialog, StringComparison.Ordinal) > dialog);
        }

        // Bug 2: the Drives list was read once and never again, so Google Drive's
        // row was missing after a start on the list, and USB sticks never showed.
        private static void DrivesListFollowsDrivesComingAndGoing()
        {
            var main = Source("MainForm.cs");
            var refresh = Body(main, "void RefreshDrivesList(");
            Check("a Drives-list refresh exists and marks panes on the list",
                refresh.Contains("DrivesPath", StringComparison.Ordinal) && refresh.Contains("NeedsRefresh", StringComparison.Ordinal));
            Check("the Drives-list refresh is quiet", refresh.Contains("ReloadQuietly", StringComparison.Ordinal));
            Check("the Drives-list refresh does not navigate itself (focus would be pulled from menus)",
                !refresh.Contains("Navigate", StringComparison.Ordinal));

            Check("the Drive mount's refresh also refreshes the Drives list",
                Body(main, "public void RefreshIfShowingDrive(").Contains("RefreshDrivesList(", StringComparison.Ordinal));
            Check("a moved Drive letter refreshes the Drives list",
                Body(main, "public void DriveLetterMoved(").Contains("RefreshDrivesList(", StringComparison.Ordinal));

            var device = Body(main, "void OnDeviceChange(");
            int volume = device.IndexOf("DBT_DEVTYP_VOLUME", StringComparison.Ordinal);
            Check("a volume arriving or leaving refreshes the Drives list",
                volume >= 0 && device.IndexOf("RefreshDrivesList(", volume, StringComparison.Ordinal) > volume);
        }

        private static string? FindSrc(string name)
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var p = Path.Combine(dir, "src", name);
                if (File.Exists(p)) return p;
                dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            }
            return null;
        }

        private static string Source(string name)
        {
            var p = FindSrc(name);
            Check($"source file {name} found", p != null);
            return p == null ? "" : SourceFiles.Read(p);
        }

        /// <summary>The text of a member, from its signature to the next blank-line-free method end (brace matched).</summary>
        private static string Body(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return "";
            int open = src.IndexOf('{', start);
            if (open < 0) return "";
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return src.Substring(start, i - start + 1);
            }
            return src.Substring(start);
        }

        // Bug 1 (and 4, 5): Ctrl+R and Check for updates quit without the tray Exit's
        // "still on its way to Google Drive" question, so in-memory saves were lost.
        private static void RestartAndUpdateAskAboutDriveUploads()
        {
            var main = Source("MainForm.cs");
            var restart = Body(main, "private void RestartApp()");
            int ask = restart.IndexOf("ConfirmUnsavedDrive(", StringComparison.Ordinal);
            int launch = restart.IndexOf("Process.Start(", StringComparison.Ordinal);
            Check("restart asks about Drive uploads before launching the new copy", ask >= 0 && launch > ask);

            var update = Body(main, "private async void CheckForUpdates()");
            int askU = update.IndexOf("ConfirmUnsavedDrive(", StringComparison.Ordinal);
            int install = update.IndexOf("Updater.StartInstall(", StringComparison.Ordinal);
            Check("update asks about Drive uploads before the installer starts", askU >= 0 && install > askU);

            var tray = Source("TrayApplicationContext.cs");
            var confirm = Body(tray, "bool ConfirmUnsavedDrive(");
            Check("the Drive question names the action being taken, not always Quit",
                confirm.Contains("string before, string question", StringComparison.Ordinal) && confirm.Contains("before it {before}", StringComparison.Ordinal) && !confirm.Contains("Quit anyway?", StringComparison.Ordinal));
        }
    }

    // ---- merged from Sweep500MainBTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// The 5.0.0 sweep of the window: watching, archives as folders and search.
    /// </summary>
    internal static class Sweep500MainBTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the window: watching, archives as folders and search:");
            var src = Source("MainForm.cs");
            Check("mainb: MainForm.cs found", src != null);
            if (src == null) return;

            DeadWatcherIsRebuilt(src);
            DriveListFollowsDevices(src);
            HiddenDriveLetterKeepsPane(src);
            SelectionMarkFollowsFile(src);
            SecondOperationRerunsQuietSearch(src);
            await Task.CompletedTask;
        }

        /// <summary>A watcher .NET stopped after an error is not kept as if it still watched.</summary>
        private static void DeadWatcherIsRebuilt(string src)
        {
            var build = Body(src, "private (FileSystemWatcher?, RemovalWatch?) BuildWatcher(");
            int err = build.IndexOf("watcher.Error +=", StringComparison.Ordinal);
            var handler = err < 0 ? "" : build.Substring(err, Math.Min(400, build.Length - err));
            Check("mainb dead watcher: the list watcher's Error marks it dead unless the buffer only overflowed",
                handler.Contains("WatchDead", StringComparison.Ordinal) &&
                handler.Contains("InternalBufferOverflowException", StringComparison.Ordinal), Excerpt(handler));

            var sizes = Body(src, "private FileSystemWatcher? SizeWatcherFor(");
            int serr = sizes.IndexOf("sizes.Error +=", StringComparison.Ordinal);
            var shandler = serr < 0 ? "" : sizes.Substring(serr, Math.Min(500, sizes.Length - serr));
            Check("mainb dead watcher: the size watcher's Error marks the pair dead too",
                shandler.Contains("WatchDead", StringComparison.Ordinal), Excerpt(shandler));

            var arm = Body(src, "private void ArmWatcher(Pane pane, string path)");
            int keep = arm.IndexOf("NameRules.PlainPath(pane.Watcher", StringComparison.Ordinal);
            var guard = keep < 0 ? "" : arm[Math.Max(0, keep - 400)..keep];
            Check("mainb dead watcher: ArmWatcher keeps a same-path watcher only while it is alive",
                guard.Contains("WatchDead", StringComparison.Ordinal) || guard.Contains("EnableRaisingEvents", StringComparison.Ordinal),
                Excerpt(guard));

            var needs = Body(src, "private static bool NeedsWatcher(Pane pane)");
            Check("mainb dead watcher: NeedsWatcher counts a dead watcher as none",
                needs.Contains("WatchDead", StringComparison.Ordinal), Excerpt(needs));

            var poll = Body(src, "private async void CheckCurrentPathStillExists()");
            Check("mainb dead watcher: the availability poll re-arms a folder that is there but unwatched",
                poll.Contains("NeedsWatcher(", StringComparison.Ordinal) && poll.Contains("ArmWatcher(", StringComparison.Ordinal));
        }

        /// <summary>A drive plugged in or removed reloads a drive list on screen.</summary>
        private static void DriveListFollowsDevices(string src)
        {
            int at = src.IndexOf("type == DBT_DEVTYP_VOLUME", StringComparison.Ordinal);
            int end = at < 0 ? -1 : src.IndexOf("\n        }", at, StringComparison.Ordinal);
            var branch = at < 0 || end < 0 ? "" : src[at..end];
            Check("mainb drive list: a volume arriving or going marks a drive-list pane for a quiet reload",
                branch.Contains("RefreshDrivesList(", StringComparison.Ordinal) ||
                (branch.Contains("DrivesPath", StringComparison.Ordinal) &&
                 branch.Contains("NeedsRefresh", StringComparison.Ordinal) &&
                 branch.Contains("ReloadQuietly", StringComparison.Ordinal)), Excerpt(branch));
        }

        /// <summary>Drive offline hides its letter; panes on it stay put rather than falling back to Drives.</summary>
        private static void HiddenDriveLetterKeepsPane(string src)
        {
            var poll = Body(src, "private async void CheckCurrentPathStillExists()");
            int hidden = poll.IndexOf("LetterShown", StringComparison.Ordinal);
            int count = poll.IndexOf("++_consecutiveProbeFailures", StringComparison.Ordinal);
            Check("mainb hidden letter: the availability poll leaves a hidden Drive letter alone before counting a failure",
                hidden >= 0 && count >= 0 && hidden < count, Excerpt(poll));
        }

        /// <summary>A quiet swap of rows keeps the Shift-range anchor on its file.</summary>
        private static void SelectionMarkFollowsFile(string src)
        {
            Check("mainb selection mark: the anchor message is defined",
                src.Contains("LVM_SETSELECTIONMARK", StringComparison.Ordinal) && src.Contains("LVM_GETSELECTIONMARK", StringComparison.Ordinal));

            void Around(string what, string body)
            {
                int read = body.IndexOf("SelectionMarkPathOf(", StringComparison.Ordinal);
                int swap = body.IndexOf("ReplaceEntriesInPlace(", StringComparison.Ordinal);
                int put = body.LastIndexOf("RestoreSelectionMark(", StringComparison.Ordinal);
                Check($"mainb selection mark: {what} reads the anchor's file before the swap and puts it back after",
                    read >= 0 && swap > read && put > swap, Excerpt(body));
            }
            Around("SwapEntriesQuietly", Body(src, "private void SwapEntriesQuietly("));
            Around("ShowAppleRowsQuietly", Body(src, "private void ShowAppleRowsQuietly("));
            int later = src.IndexOf("// Later showings keep the cursor on the item it is on.", StringComparison.Ordinal);
            int laterEnd = later < 0 ? -1 : src.IndexOf("/// <summary>The row the letters still waiting", later, StringComparison.Ordinal);
            Around("ProgressiveLoad's later showings", later < 0 || laterEnd < 0 ? "" : src[later..laterEnd]);

            var swapBody = Body(src, "private void SwapEntriesQuietly(");
            int focusCheck = swapBody.IndexOf("if (focus != oldFocus)", StringComparison.Ordinal);
            int restore = swapBody.LastIndexOf("RestoreSelectionMark(", StringComparison.Ordinal);
            Check("mainb selection mark: SwapEntriesQuietly puts the anchor back even when the cursor's row did not move",
                focusCheck >= 0 && restore > focusCheck, Excerpt(swapBody));
        }

        /// <summary>A second operation in results while a quiet re-run walks starts it again.</summary>
        private static void SecondOperationRerunsQuietSearch(string src)
        {
            var refresh = Body(src, "private async Task<bool> RefreshAsync(");
            int at = refresh.IndexOf("if (pane.Searching)", StringComparison.Ordinal);
            int term = refresh.IndexOf("if (pane.SearchTerm is { } term)", StringComparison.Ordinal);
            var block = at < 0 || term < at ? "" : refresh[at..term];
            Check("mainb search rerun: a refresh while a quiet re-run walks starts the re-run again",
                block.Contains("RerunSearchAsync(", StringComparison.Ordinal) &&
                block.Contains("QuietSearchTerm", StringComparison.Ordinal), Excerpt(block));

            var run = Body(src, "private async Task RunSearchAsync(");
            Check("mainb search rerun: RunSearchAsync records whether the walk is a quiet re-run",
                run.Contains("QuietSearchTerm = quiet", StringComparison.Ordinal), null);
        }

        /// <summary>A class-level member's text, from its signature to its closing brace.</summary>
        private static string Body(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return "";
            int end = src.IndexOf("\n        }", start, StringComparison.Ordinal);
            return end < 0 ? src[start..] : src[start..end];
        }

        private static string Excerpt(string text) => text.Length <= 160 ? text : text[..160] + "…";

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? SourceFiles.Read(path) : null;
        }
    }

    // ---- merged from Sweep500MainCTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// The 5.0.0 sweep of the window: keyboard and actions.
    /// </summary>
    internal static class Sweep500MainCTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the window: keyboard and actions:");
            var main = Source("MainForm.cs");
            if (main == null) { Check("MainForm.cs can be read", false); return; }

            EscapeDuringQuietRerunLeavesResults(main);
            SpaceMidWordNeverReachesTheControl(main);
            DriveMenuHidesArchiveCommands(main);
            EmptyFolderMessageNeverCutsOffTheTab(main);
            PasteIntoDriveTrashRefusesOnce(main);
            MoveOutOfDriveWithFailuresIsSaidAsACopy(main);
            await Task.CompletedTask;
        }

        // Bug 7: a move out of Drive with any failure trashes nothing, yet it
        // said "Moved 119 files".
        private static void MoveOutOfDriveWithFailuresIsSaidAsACopy(string main)
        {
            var drive = NameRules.PartialTransferSentence(119, 1, move: true, fromDrive: true);
            Check("a move out of Google Drive with a failure is not said as moved, and says the originals stayed",
                !drive.StartsWith("Moved", StringComparison.Ordinal) &&
                drive.Contains("nothing was removed from Google Drive", StringComparison.Ordinal), drive);
            Equal("a local move with a failure still says moved",
                "Moved 119 files, but 1 item could not be moved", NameRules.PartialTransferSentence(119, 1, true, false));
            Equal("nothing moved still says so",
                "Nothing was moved: 3 items failed", NameRules.PartialTransferSentence(0, 3, true, true));
            Equal("a copy with a failure is unchanged",
                "Copied 2 files, but 1 item could not be copied", NameRules.PartialTransferSentence(2, 1, false, true));
            Check("the paste's partial message knows whether the move came out of Drive",
                main.Contains("PartialTransferSentence(result.Copied, result.Failed, move, fromDrive)", StringComparison.Ordinal));
        }

        // Bug 6: Ctrl+V in the Drive trash surveyed, opened a transfer window
        // and spoke one failure per file.
        private static void PasteIntoDriveTrashRefusesOnce(string main)
        {
            var paste = Body(main, "private async void PasteClipboard(");
            int trashAt = paste.IndexOf("IsInTrash(", StringComparison.Ordinal);
            int runAt = paste.IndexOf("RunTransfer(", StringComparison.Ordinal);
            Check("Paste refuses the Drive trash once, before anything is read or transferred",
                trashAt >= 0 && paste.Contains("IsTrashFolder(", StringComparison.Ordinal) && runAt > trashAt &&
                paste.IndexOf("TryGetFiles", StringComparison.Ordinal) > trashAt);

            var fake = new FakeDrive();
            var client = new DriveClient(fake, 0.0005);
            var root = Path.Combine(Path.GetTempPath(), "en-mc-trash-" + Guid.NewGuid().ToString("N")[..8]);
            var local = Path.Combine(Path.GetTempPath(), "en-mc-up-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
            var said = new System.Collections.Generic.List<string>();
            using var drive = new GoogleDrive();
            drive.Notification += (id, message) => { lock (said) said.Add(id); };
            DriveMount? mount = null;
            try
            {
                try
                {
                    mount = new DriveMount(client, root, 16L * 1024 * 1024, _ => { });
                    mount.Start("root", assignLetter: false);
                }
                catch (Exception ex) { Check("a fake Drive stands up for the trash upload test", false, ex.Message); return; }
                drive.UseForTests(mount, client);
                mount.Forget(root);
                mount.Populate(root);
                var trash = Path.Combine(root, "Trash");
                File.WriteAllText(local, "up");
                int uploads = fake.Uploads;
                bool allRefused = true;
                for (int i = 0; i < 3; i++)
                    allRefused &= drive.UploadInto(local, trash, announce: false).GetAwaiter().GetResult() == null;
                lock (said)
                    Check("a bulk upload into the trash is refused without a message per file",
                        allRefused && fake.Uploads == uploads && !said.Contains("drive.upload.failed"), string.Join(" | ", said));
            }
            finally
            {
                try { mount?.Dispose(); } catch { }
                try { File.Delete(local); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }
        }

        // Bugs 4 and 5: "This folder is empty" on focus interrupted the tab
        // announcement (and NVDA's list name), repeated on a held Tab, and
        // said "folder" in Apple Music.
        private static void EmptyFolderMessageNeverCutsOffTheTab(string main)
        {
            var say = Body(main, "private void SayIfEmpty(");
            Check("the empty message holds back while a spoken tab announcement is on its way",
                say.Contains("_switchingTab", StringComparison.Ordinal) && say.Contains("\"nav.tab\"", StringComparison.Ordinal));
            var tab = Body(main, "private void SetActiveTab(");
            int raise = tab.IndexOf("_switchingTab = true", StringComparison.Ordinal);
            Check("the tab switch is marked before the new list can take focus, and cleared in a finally",
                raise >= 0 && raise < tab.IndexOf("ShowActivePane();", StringComparison.Ordinal) &&
                tab.Contains("finally { _switchingTab = false; }", StringComparison.Ordinal));
            Check("the empty message never interrupts speech",
                !say.Contains("AnnounceOperation(", StringComparison.Ordinal) &&
                say.Contains("interrupt: false", StringComparison.Ordinal));
            Check("a held Tab restarts one timer instead of starting one per press",
                say.Contains("_emptyTimer.Stop();", StringComparison.Ordinal) &&
                !say.Replace("if (_emptyTimer == null)", "").Contains("var wait = new", StringComparison.Ordinal));
            // The words themselves are SelectionSpeech's, tested as code.
            Check("an empty Apple Music list is a list, as navigation says",
                say.Contains("EmptyWords(", StringComparison.Ordinal) &&
                main.Contains("private static string EmptyWords(Pane pane) => SelectionSpeech.EmptyWords(pane.CurrentPath, pane.SearchTerm);", StringComparison.Ordinal) &&
                SelectionSpeech.EmptyWords(AppleMusic.Root + "\\Library", null) == "This list is empty" &&
                SelectionSpeech.EmptyWords(@"C:\Music", null) == "This folder is empty");
        }

        // Bug 3: on the Drive letter Compress, Extract and Extract here can
        // only refuse, so the menu must not read them out.
        private static void DriveMenuHidesArchiveCommands(string main)
        {
            var menu = Body(main, "private void ShowOurContextMenu(");
            int at = menu.IndexOf("bool looksLikeArchive", StringComparison.Ordinal);
            int end = at < 0 ? -1 : menu.IndexOf(';', at);
            string looks = at < 0 || end < 0 ? "" : menu.Substring(at, end - at);
            Check("Extract is decided with the Drive letter in mind",
                looks.Contains("Drive.Owns", StringComparison.Ordinal) || looks.Contains("onDrive", StringComparison.Ordinal), looks);
            Check("on the Drive letter Compress is hidden after ShowArchiveHidden shows it",
                menu.Contains("onDrive = Drive != null && Drive.Owns(Active.CurrentPath)", StringComparison.Ordinal) &&
                menu.IndexOf("if (onDrive) _compressItem.Visible = false", StringComparison.Ordinal) >
                menu.IndexOf("ShowArchiveHidden();", StringComparison.Ordinal));
        }

        // Bug 2: Ctrl+Space mid-word toggled silently through the native
        // control and added a space to the word; a plain Space mid-word let
        // the control select that row alone.
        private static void SpaceMidWordNeverReachesTheControl(string main)
        {
            var keys = Body(main, "private void List_KeyDown(");
            int first = keys.IndexOf("case Keys.Space when", StringComparison.Ordinal);
            int firstEnd = first < 0 ? -1 : keys.IndexOf("break;", first, StringComparison.Ordinal);
            string toggle = first < 0 || firstEnd < 0 ? "" : keys.Substring(first, firstEnd - first);
            Check("Ctrl+Space toggles the row even while a name is being typed",
                toggle.Contains("e.Control ||", StringComparison.Ordinal) && toggle.Contains("ToggleFocusedRow", StringComparison.Ordinal), toggle);
            Check("choosing a row with Space ends the typed word",
                toggle.Contains("TypeAhead.Reset()", StringComparison.Ordinal));
            int second = first < 0 ? -1 : keys.IndexOf("case Keys.Space when", firstEnd, StringComparison.Ordinal);
            int secondEnd = second < 0 ? -1 : keys.IndexOf("break;", second, StringComparison.Ordinal);
            string midWord = second < 0 || secondEnd < 0 ? "" : keys.Substring(second, secondEnd - second);
            Check("a plain Space mid-word is kept from the control but still reaches the word",
                midWord.Contains("e.Handled = true", StringComparison.Ordinal) && !midWord.Contains("SuppressKeyPress", StringComparison.Ordinal), midWord);
            var press = Body(main, "private void List_KeyPress(");
            Check("a space typed with Ctrl held never extends the word",
                press.Contains("ModifierKeys & Keys.Control", StringComparison.Ordinal));
        }

        // Bug 1: a quiet re-run after a rename or delete in results is not a
        // search anybody started; Escape must leave the results, not stop it.
        private static void EscapeDuringQuietRerunLeavesResults(string main)
        {
            var run = Body(main, "private async Task RunSearchAsync(");
            Check("a quiet re-run marks itself as quiet while it runs",
                run.Contains("pane.SearchQuietly = quiet", StringComparison.Ordinal));
            var keys = Body(main, "private void List_KeyDown(");
            int stop = keys.IndexOf("case Keys.Escape when pane.Searching", StringComparison.Ordinal);
            int stopEnd = stop < 0 ? -1 : keys.IndexOf(':', stop);
            string guard = stop < 0 || stopEnd < 0 ? "" : keys.Substring(stop, stopEnd - stop);
            Check("Escape stops only a search somebody started, so a quiet re-run's Escape leaves the results",
                guard.Contains("!pane.SearchQuietly", StringComparison.Ordinal), guard);
        }

        /// <summary>The text of a member from its signature to the next member at the same indentation.</summary>
        internal static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0) return "";
            int next = source.IndexOf("\n        private ", at + signature.Length, StringComparison.Ordinal);
            int next2 = source.IndexOf("\n        internal ", at + signature.Length, StringComparison.Ordinal);
            int next3 = source.IndexOf("\n        public ", at + signature.Length, StringComparison.Ordinal);
            int end = source.Length;
            foreach (var n in new[] { next, next2, next3 }) if (n > 0 && n < end) end = n;
            return source.Substring(at, end - at);
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            try { return SourceFiles.Read(Path.Combine(dir.FullName, "src", name)); } catch { return null; }
        }
    }

    // ---- merged from Sweep500MainDTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// The 5.0.0 sweep of the window: audio, archives, status and lifetime.
    /// </summary>
    internal static class Sweep500MainDTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the window: audio, archives, status and lifetime:");
            await ExtractsIntoOneFolderTakeTurns();
            DriveSideDeleteKeepsPcFolderUnlessDriveSaysGone();
            OtherSideOfSyncedDeleteIsSaidLast();
        }

        /// <summary>
        /// Bugs 1-3: two Extract here into one folder at once. Each worked out its
        /// clashes before the other wrote anything, so one silently replaced the
        /// other's files. Now extracts into one folder take turns.
        /// </summary>
        private static async Task ExtractsIntoOneFolderTakeTurns()
        {
            var root = Path.Combine(Path.GetTempPath(), "ExplorerNativeTests_sweep500maind_" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                // The order: the second extract does not start while the first runs.
                var dest = Path.Combine(root, "dest");
                Directory.CreateDirectory(dest);
                var firstRunning = new TaskCompletionSource();
                var letFirstEnd = new TaskCompletionSource();
                bool secondStartedEarly = false;
                var first = ExtractTurns.InTurn(dest, async () =>
                {
                    firstRunning.SetResult();
                    await letFirstEnd.Task;
                    return new ArchiveResult(1, 0, false, Array.Empty<string>(), 0, 0);
                }, CancellationToken.None);
                await firstRunning.Task;
                var second = ExtractTurns.InTurn(dest + "\\", () =>
                {
                    secondStartedEarly = !letFirstEnd.Task.IsCompleted;
                    return Task.FromResult(new ArchiveResult(1, 0, false, Array.Empty<string>(), 0, 0));
                }, CancellationToken.None);
                // A different folder is not held up.
                var elsewhere = ExtractTurns.InTurn(Path.Combine(root, "other"),
                    () => Task.FromResult(new ArchiveResult(1, 0, false, Array.Empty<string>(), 0, 0)), CancellationToken.None);
                bool otherFolderRan = await Task.WhenAny(elsewhere, Task.Delay(5000)) == elsewhere;
                await Task.Delay(100);
                Check("an extract into a folder another extract is writing waits its turn", !second.IsCompleted);
                Check("an extract into a different folder is not held up", otherFolderRan);
                var cts = new CancellationTokenSource();
                var cancelled = ExtractTurns.InTurn(dest, () => throw new InvalidOperationException("ran"), cts.Token);
                cts.Cancel();
                var cancelledResult = await cancelled;
                Check("an extract cancelled while waiting its turn ends cancelled without running", cancelledResult.Cancelled);
                letFirstEnd.SetResult();
                await Task.WhenAll(first, second);
                Check("the waiting extract runs once the first has finished", !secondStartedEarly);

                // The real thing: two zips holding the same names, extracted at once.
                var a = MakeZip(root, "a", 'A');
                var b = MakeZip(root, "b", 'B');
                var into = Path.Combine(root, "into");
                Directory.CreateDirectory(into);
                File.WriteAllText(Path.Combine(into, "already.txt"), "here before");
                Task<ArchiveResult> Start(string zip) => ExtractTurns.InTurn(into,
                    () => ArchiveEngine.ExtractAsync(zip, into, PasteConflictPolicy.AutoRename, null, 4, null, CancellationToken.None),
                    CancellationToken.None);
                var results = await Task.WhenAll(Start(a), Start(b));
                var bins = Directory.GetFiles(into, "same*.bin").Select(File.ReadAllBytes).ToList();
                Check("both archives' same-named big file survive extracting together",
                    bins.Count == 2 && bins.Select(x => x[0]).Distinct().Count() == 2, $"{bins.Count} files");
                Check("both archives' same-named small files survive extracting together",
                    Directory.GetFiles(into, "f000*.txt").Length == 2);
                Check("the rename of the second archive's clashing names is reported",
                    results.Sum(r => r.Collisions.RenamedCount) > 0);

                var src = Source("MainForm.cs") ?? "";
                Check("the window's extracts take their turn per destination folder",
                    Body(src, "private async Task ExtractOneAsync(").Contains("ExtractTurns.InTurn(destination", StringComparison.Ordinal));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static string MakeZip(string root, string name, char fill)
        {
            var src = Path.Combine(root, "src-" + name);
            Directory.CreateDirectory(src);
            File.WriteAllBytes(Path.Combine(src, "same.bin"), Enumerable.Repeat((byte)fill, 24 * 1024 * 1024).ToArray());
            for (int i = 0; i < 200; i++) File.WriteAllText(Path.Combine(src, $"f{i:000}.txt"), $"from {name}");
            var zip = Path.Combine(root, name + ".zip");
            ZipFile.CreateFromDirectory(src, zip, CompressionLevel.NoCompression, includeBaseDirectory: false);
            return zip;
        }

        /// <summary>
        /// Bug 4: Delete anyway on a pair's Drive folder recycled the PC folder
        /// even when Drive had refused to trash its own. Now the PC folder goes
        /// only once Google Drive says the Drive folder has gone.
        /// </summary>
        private static void DriveSideDeleteKeepsPcFolderUnlessDriveSaysGone()
        {
            var src = Source("MainForm.cs");
            if (src == null) { Check("MainForm.cs found", false); return; }
            var ends = Body(src, "DeleteOtherSyncEnds(List<");
            int driveSide = ends.IndexOf("if (driveSide)", StringComparison.Ordinal);
            int asked = ends.IndexOf("DriveFolderStillThere(", StringComparison.Ordinal);
            int recycled = ends.IndexOf("ShellDelete.Recycle(pair.LocalFolder", StringComparison.Ordinal);
            Check("the PC end of a pair is recycled only after Drive is asked whether its folder went",
                driveSide >= 0 && asked > driveSide && recycled > asked);
            var still = Body(src, "Task<string?> DriveFolderStillThere(");
            Check("Drive is asked about the pair's own folder, and only gone or trashed lets the PC end go",
                still.Contains("FileNow(pair.DriveFolderId", StringComparison.Ordinal) &&
                still.Contains("== null", StringComparison.Ordinal));
        }

        /// <summary>
        /// Bug 5: the other side's failure was spoken and then cut off at once by
        /// "Deleted" after the refresh. Now one closing sentence covers both.
        /// </summary>
        private static void OtherSideOfSyncedDeleteIsSaidLast()
        {
            var src = Source("MainForm.cs");
            if (src == null) { Check("MainForm.cs found", false); return; }
            var ends = Body(src, "DeleteOtherSyncEnds(List<");
            Check("the other side of a synced delete says nothing itself, before the refresh",
                ends.Length > 0 && !ends.Contains("Announce", StringComparison.Ordinal));
            var del = Body(src, "private async void DeleteSelected(");
            if (del.Length == 0) del = Body(src, "async Task DeleteSelected(");
            int refresh = del.IndexOf("RefreshAsync(landOn", StringComparison.Ordinal);
            int both = del.IndexOf("\"delete.both\"", StringComparison.Ordinal);
            Check("deleted on both sides is said after the refresh, as the last word",
                refresh >= 0 && both > refresh);
            Check("the other side's failures reach the error list",
                del.Contains("ShowErrors(otherSide", StringComparison.Ordinal) ||
                del.Contains("Concat(otherSide", StringComparison.Ordinal));
        }

        private static string Body(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return "";
            int end = src.IndexOf("\n        }", start, StringComparison.Ordinal);
            return end < 0 ? src[start..] : src[start..end];
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? SourceFiles.Read(path) : null;
        }
    }

    // ---- merged from Release510MainTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// 5.1.0: the main window, its keys and navigation.
    /// </summary>
    internal static class Release510MainTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        private static string Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return "";
            try { return SourceFiles.Read(Path.Combine(dir.FullName, "src", name)); } catch { return ""; }
        }

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("5.1.0, the main window, its keys and navigation:");

            var main = Source("MainForm.cs");
            var speech = Source("Speech.cs");

            // 20: the NVDA client beside a single-file exe is not loaded first.
            Check("a loose NVDA client beside the exe is only tried in multi-file builds",
                speech.Contains("Assembly.Location") &&
                speech.IndexOf("Assembly.Location", StringComparison.Ordinal) < speech.IndexOf("candidates.Add(Path.Combine(baseDir, exact))", StringComparison.Ordinal));

            // 21: Tab or Ctrl+Tab is not listed with one tab.
            Check("shortcut list has Tab line with two tabs", BuiltInShortcuts.Lines(true).Any(l => l.StartsWith("Tab or Ctrl+Tab")));
            Check("shortcut list has no Tab line with one tab", !BuiltInShortcuts.Lines(false).Any(l => l.StartsWith("Tab or Ctrl+Tab")));

            // 23: an exclusion written as a pattern leaves out matching names.
            var q = SearchQuery.Parse("love -*.m4a");
            Check("-*.m4a excludes an m4a", !q.Matches("love song.m4a", ""));
            Check("-*.m4a keeps a flac", q.Matches("love song.flac", ""));
            var q2 = SearchQuery.Parse("*.flac -*live*");
            Check("-*live* excludes a live track", !q2.Matches("a live one.flac", ""));
            Check("-*live* keeps the others", q2.Matches("studio.flac", ""));
            Check("plain -word still works", !SearchQuery.Parse("love -live").Matches("love live.flac", ""));

            // 22: an unreadable folder searched is an error, not "nothing matching".
            var missing = Path.Combine(Path.GetTempPath(), "w510-missing-" + Guid.NewGuid().ToString("N"));
            bool threw = false;
            try { FileSearch.Local(missing, "x", true, true, 100, CancellationToken.None); }
            catch (Exception) { threw = true; }
            Check("searching a folder that cannot be read says so", threw);

            // MainForm cannot be compiled in; pin the decisions in its source.
            Check("1: failed update does not point at the encrypted log file",
                main.Contains("--read-log") && !main.Contains("\"ExplorerNative\", \"install.log\") + \".\""));
            Check("2: File, Exit quits like the tray", main.Contains("Program.QuitAsked()"));
            Check("3/8: drive list hides items that only refuse", main.Contains("_drivesHidden") &&
                main.Contains("foreach (var item in _drivesHidden) item.Visible = false;"));
            Check("4: cached rows are dropped when the day changes", main.Contains("_cachedOnDay"));
            Check("5: a hidden item that is not on screen does not reload", main.Contains("if (!known && !keep) continue;"));
            Check("6: a cancelled search gives the pane live work again",
                main.Contains("var fresh = BeginPaneWork(pane);") && main.Contains("CalculateAllFolderSizesAsync(pane, fresh)"));
            Check("7: the quick Ctrl release says no total twice", main.Contains("AnnounceSelectionChange(ctrlHeld: true)") &&
                main.Contains("list.LostFocus += (_, _) => _selectedWithCtrl = false;"));
            Check("9: only items directly in Trash go to permanent delete",
                main.Contains("can't be deleted one by one"));
            Check("10: empty trash lands on the pane that asked", main.Contains("NavigateAsync(land, onPane: pane)"));
            Check("11: a paste that skipped everything says so", main.Contains("already there and left alone"));
            Check("12: moving a Drive folder into itself is refused here", main.Contains("a folder cannot be moved into itself"));
            Check("13: Convert audio speaks while it plans", main.Contains("_convertPlanning"));
            Check("14/15: one audio editor per file", main.Contains("_openEditors"));
            Check("16: extract reads the folder before awaiting", main.Contains("var startedIn = Active.CurrentPath;"));
            Check("17: folder size tells a slow folder from a missing one", main.Contains("is not answering, so its size can't be measured now"));
            Check("18: importing a locked settings file explains it", main.Contains("locked to the PC and account that wrote it"));
            Check("19: closing over running transfers takes the process down", main.Contains("QuitForRestart()") && main.Contains("bool abandoning"));

            await Task.CompletedTask;
        }
    }

    // ---- merged from ShortcutSortDetailsTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// Global or not per audio shortcut, per-folder sort orders surviving
    /// Preferences OK, and the details said after a pause on a file.
    /// </summary>
    internal static class ShortcutSortDetailsTests
    {
        public static Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            Console.WriteLine("Global shortcuts, folder sorts, file details:");

            // Every shortcut is global until somebody says otherwise.
            var s = new Settings();
            check("a new shortcut is global", AudioActions.IsGlobal(s, AudioAction.PlayPause), null);

            AudioActions.SetGlobal(s, AudioAction.PlayPause, false);
            AudioActions.SetGlobal(s, AudioAction.VolumeUp, false);
            check("unticked shortcuts are not global",
                !AudioActions.IsGlobal(s, AudioAction.PlayPause) && !AudioActions.IsGlobal(s, AudioAction.VolumeUp), null);
            check("the others stay global", AudioActions.IsGlobal(s, AudioAction.Stop), null);

            AudioActions.SetGlobal(s, AudioAction.PlayPause, true);
            equal("ticking one back leaves only the other", "VolumeUp", s.AudioLocalShortcuts);
            AudioActions.SetGlobal(s, AudioAction.VolumeUp, false);
            equal("setting it twice does not list it twice", "VolumeUp", s.AudioLocalShortcuts);

            // A folder's own order set while Preferences was open is not undone by OK.
            var live = new Settings();
            var before = new Settings();
            var after = new Settings { ShowHiddenFiles = true };
            live.FolderSorts = new Dictionary<string, string> { [@"C:\Music"] = "Modified descending" };
            var merged = Settings.WithChanges(live, before, after);
            check("OK in Preferences keeps a folder's own order",
                merged.FolderSorts.TryGetValue(@"C:\Music", out var kept) && kept == "Modified descending", null);

            // A Drive upload's progress counts files as well as bytes: a few big
            // files sent and thousands of small ones left is not 100 percent.
            var bigDone = new TransferProgress(1000L * 1024 * 1024, 1024L * 1024 * 1024, 20, 3000, "",
                5_000_000, TimeSpan.FromSeconds(60), PerItemWork: DriveUpload.PerFileWork);
            check("big files done and small ones left is not 100 percent", bigDone.Percent < 50, bigDone.Percent.ToString("F0"));
            var smallDone = new TransferProgress(1024 * 1024, 1024L * 1024 * 1024, 500, 3000, "",
                10_000, TimeSpan.FromSeconds(60), PerItemWork: DriveUpload.PerFileWork);
            check("500 of 3000 small files done is not 0 percent", smallDone.Percent > 5, smallDone.Percent.ToString("F0"));
            check("and the time left follows the files, not the byte rate",
                smallDone.Remaining is { } left && left.TotalMinutes < 30, smallDone.Remaining?.ToString() ?? "none");
            var plain = new TransferProgress(50, 100, 1, 2, "", 10, TimeSpan.FromSeconds(5));
            equal("a copy without a per-file cost still counts bytes", "50", plain.Percent.ToString("F0"));
            check("uploads send small files sixteen at a time and big ones four",
                DriveUpload.Streams == 16 && DriveUpload.BigStreams == 4, null);

            // Words are runs of anything that is not white space.
            equal("text counts", "3 words, 14 characters", FileDetails.CountText("one two\n three"));
            equal("one word is singular", "1 word, 5 characters", FileDetails.CountText("hello"));
            equal("empty text", "0 words, 0 characters", FileDetails.CountText(""));

            var dir = Path.Combine(Path.GetTempPath(), "en-details-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var text = Path.Combine(dir, "notes.txt");
                File.WriteAllText(text, "a b c");
                equal("a text file is described", "3 words, 5 characters", FileDetails.Describe(text, null) ?? "(null)");

                var other = Path.Combine(dir, "picture.bin");
                File.WriteAllBytes(other, new byte[] { 1, 2, 3 });
                check("a file with nothing to say gives nothing", FileDetails.Describe(other, null) == null, null);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }

            return Task.CompletedTask;
        }
    }
}
