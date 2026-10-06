using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace ExplorerNative
{
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
            var two = Sorted(names.Reverse());
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
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private static void SourceRules()
        {
            var main = Source("MainForm.cs");
            var shell = Source("ShellContextMenu.cs");
            if (main == null || shell == null) { Check("the window's source can be read", false); return; }

            Check("closing the Windows menu without choosing is its own outcome",
                shell.Contains("if (selected == 0) return MenuOutcome.Dismissed;", StringComparison.Ordinal));
            int menu = main.IndexOf("private void ShowShellMenuForSelection()", StringComparison.Ordinal);
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

            Check("a drive being removed lets go of its watcher",
                main.Contains("WM_DEVICECHANGE", StringComparison.Ordinal) &&
                main.Contains("case DBT_DEVICEQUERYREMOVE:", StringComparison.Ordinal) &&
                main.Contains("RegisterDeviceNotificationW", StringComparison.Ordinal));

            Check("a progressive load merges sorted batches instead of re-sorting on the UI thread",
                main.Contains("_shown = MergeSorted(_shown, fresh, _order);", StringComparison.Ordinal) &&
                !main.Contains("_form.SortLikeAFolder(new List<Entry>(_shown))", StringComparison.Ordinal));

            Check("sort by Type goes through SortsAs",
                main.Contains("ComparerFor(SortColumn column) => NameRules.SortsAs(column) switch", StringComparison.Ordinal));

            Check("a cancelled extract does not announce what it replaced",
                main.Contains("if (!result.Cancelled) AnnounceConflicts(result.Collisions);", StringComparison.Ordinal));
        }
    }
}
