using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// Searching: the match rule, what a result row says, the local walk against
    /// a real tree on disk, and the arithmetic that decides which Drive hits are
    /// under the folder somebody is standing in.
    ///
    /// The Drive half is testable at all because the part that is hard — scoping
    /// a search to a subtree, which Drive's query language cannot express — is
    /// kept as a pure function over a folder graph. A graph built by hand here is
    /// the same shape as the one two `files.list` requests produce, so the
    /// awkward cases (a hit outside the root, a chain that loops, a parent nobody
    /// listed) can be asked about without an account.
    /// </summary>
    internal static class SearchTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Searching:");

            MatchTests();
            LabelTests();
            LocalWalkTests();
            DriveScopeTests();
            PathMaterialisationTests();
            QueryEscapingTests();
            SourceShapeTests();
        }

        /// <summary>
        /// One rule, against the whole name. "you can search anything" is the
        /// requirement, and the extension being part of the name is what makes
        /// ".flac" a search rather than a second field in the dialog.
        /// </summary>
        private static void MatchTests()
        {
            Check("a name containing the term matches",
                FileSearch.Matches("Lovesick.flac", "love"));
            Check("case does not matter either way",
                FileSearch.Matches("LOVESICK.FLAC", "love") && FileSearch.Matches("lovesick.flac", "LOVE"));
            Check("the middle of a name counts, not only the start",
                FileSearch.Matches("Just Cause I Love You.flac", "cause i love"));
            Check("an extension is part of the name, so it is searchable",
                FileSearch.Matches("track.flac", ".flac"));
            Check("and so are digits in it",
                FileSearch.Matches("2019 mix.wav", "19"));
            Check("something absent does not match",
                !FileSearch.Matches("Lovesick.flac", "banjo"));

            // An empty search is every file rather than none. It is reachable —
            // the dialog refuses a blank, but F5 re-runs whatever was searched
            // for — and "everything" is the answer that cannot lose a file.
            Check("an empty term matches everything", FileSearch.Matches("anything.txt", ""));
        }

        /// <summary>
        /// What a row says. The name has to come first or type-ahead stops
        /// working, and the folder has to be there at all or three hits of the
        /// same name are indistinguishable.
        /// </summary>
        private static void LabelTests()
        {
            Equal("a hit in the searched folder is just its name",
                "Lovesick.flac", FileSearch.Label(@"G:\music", @"G:\music\Lovesick.flac"));

            Equal("a hit below it says where it is, after the name",
                "Lovesick.flac, in 2019", FileSearch.Label(@"G:\music", @"G:\music\2019\Lovesick.flac"));

            Equal("and says the whole way down, not just the last folder",
                "demo.wav, in live\\old", FileSearch.Label(@"G:\music", @"G:\music\live\old\demo.wav"));

            Equal("a trailing separator on the root changes nothing",
                "a.txt, in sub", FileSearch.Label(@"C:\root\", @"C:\root\sub\a.txt"));

            Equal("the root itself is matched case-insensitively",
                "a.txt", FileSearch.Label(@"c:\ROOT", @"C:\root\a.txt"));

            // A sibling folder that merely starts with the same letters is not
            // underneath the root, and turning its path into a relative one would
            // be a row claiming to be somewhere it is not.
            Check("a path outside the root is not made to look relative",
                FileSearch.Label(@"C:\root", @"C:\rootother\a.txt").Contains(@"C:\rootother", StringComparison.Ordinal),
                FileSearch.Label(@"C:\root", @"C:\rootother\a.txt"));
        }

        /// <summary>
        /// The local walk, against a real tree. Recursion is the whole feature —
        /// a search that only looked in the folder it was started in would pass
        /// every test above.
        /// </summary>
        private static void LocalWalkTests()
        {
            var root = Path.Combine(Path.GetTempPath(), "en-search-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "2019"));
                Directory.CreateDirectory(Path.Combine(root, "live", "old"));
                Directory.CreateDirectory(Path.Combine(root, "lovely folder"));

                File.WriteAllText(Path.Combine(root, "Lovesick.flac"), "x");
                File.WriteAllText(Path.Combine(root, "2019", "I Love You.flac"), "xx");
                File.WriteAllText(Path.Combine(root, "live", "old", "love demo.wav"), "xxx");
                File.WriteAllText(Path.Combine(root, "banjo.txt"), "x");

                var all = FileSearch.Local(root, "love", true, true, 1000, CancellationToken.None);

                Equal("the walk finds hits at every depth", "4", all.Hits.Count.ToString());
                Check("including the folder whose name matches",
                    all.Hits.Exists(h => h.IsDir && h.Name == "lovely folder"));
                Check("and the one three levels down",
                    all.Hits.Exists(h => h.Name == "love demo.wav"));
                Check("and not the one that does not match",
                    !all.Hits.Exists(h => h.Name == "banjo.txt"));
                Check("a complete answer does not claim to be cut short", !all.Truncated);

                Check("a file's size comes back with it",
                    all.Hits.Exists(h => h.Name == "love demo.wav" && h.Size == 3));
                Check("and a folder is marked as one rather than sized",
                    all.Hits.Exists(h => h.Name == "lovely folder" && h.IsDir && h.Size < 0));

                // The cap, and the honesty about it. A list that stopped early
                // looks exactly like a complete one from inside.
                var capped = FileSearch.Local(root, "love", true, true, 2, CancellationToken.None);
                Equal("the cap is obeyed", "2", capped.Hits.Count.ToString());
                Check("and a capped search says so", capped.Truncated);

                // Hidden files are the pane's setting, and search has to obey the
                // same one or it shows things the folder will not.
                var hidden = Path.Combine(root, "love hidden.txt");
                File.WriteAllText(hidden, "x");
                File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

                var without = FileSearch.Local(root, "love", false, false, 1000, CancellationToken.None);
                Check("a hidden file is left out when the pane would leave it out",
                    !without.Hits.Exists(h => h.Name == "love hidden.txt"));

                var with = FileSearch.Local(root, "love", true, true, 1000, CancellationToken.None);
                Check("and included when it would be shown",
                    with.Hits.Exists(h => h.Name == "love hidden.txt"));

                // Cancelling has to be heard inside a folder, not only between
                // folders: a search of a disk is minutes of walking.
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                bool threw = false;
                try { FileSearch.Local(root, "love", true, true, 1000, cancelled.Token); }
                catch (OperationCanceledException) { threw = true; }
                Check("a cancelled search stops rather than finishing", threw);
            }
            finally
            {
                try
                {
                    foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(root, recursive: true);
                }
                catch { }
            }
        }

        /// <summary>
        /// Which Drive hits count as "under here". Drive cannot answer this — its
        /// query language has no recursive form — so it is answered from the
        /// folder graph, and this is that arithmetic.
        /// </summary>
        private static void DriveScopeTests()
        {
            // root
            //  +- music          (M)
            //  |   +- 2019       (Y)
            //  |   |   +- takes  (T)
            //  |   +- live       (L)
            //  +- video          (V)
            var graph = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["M"] = "root",
                ["Y"] = "M",
                ["T"] = "Y",
                ["L"] = "M",
                ["V"] = "root",
            };

            Equal("a hit sitting in the searched folder has an empty chain",
                "", Chain("M", "M", graph));

            Equal("one folder down names that folder",
                "Y", Chain("M", "Y", graph));

            Equal("deeper names the whole way, outermost first",
                "Y,T", Chain("M", "T", graph));

            Check("a hit in a sibling folder is not under the root",
                DriveSearch.ChainUnder("M", "V", graph) == null);

            Check("nor is one in the account root, above it",
                DriveSearch.ChainUnder("M", "root", graph) == null);

            Equal("searching the account root reaches everything",
                "M,Y,T", Chain("root", "T", graph));

            // A file with no parent at all — shared into the account rather than
            // in it — is not somewhere this search can claim it is.
            Check("a hit with no parent is not under anything",
                DriveSearch.ChainUnder("M", null, graph) == null);

            // A folder whose own parent nobody listed is in somebody else's
            // drive. Walking off the end of the graph is an answer, not a hang.
            var orphan = new Dictionary<string, string?>(StringComparer.Ordinal) { ["X"] = "elsewhere" };
            Check("a chain that leaves the account stops rather than guessing",
                DriveSearch.ChainUnder("M", "X", orphan) == null);

            // A cycle should not be possible. "Should not be possible" is not a
            // reason to let the search dialog hang.
            var loop = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["A"] = "B",
                ["B"] = "A",
            };
            Check("a graph that loops is refused rather than followed for ever",
                DriveSearch.ChainUnder("M", "A", loop) == null);

            var built = DriveSearch.ParentGraph(new[]
            {
                new DriveFound("M", "music", "application/vnd.google-apps.folder", -1, default, "root"),
                new DriveFound("Y", "2019", "application/vnd.google-apps.folder", -1, default, "M"),
            });
            Equal("the graph is built from what the folder listing returned",
                "M", Chain("M", "Y", built) == "Y" ? "M" : "wrong");
        }

        /// <summary>
        /// Turning a chain of folder ids into a real path on the letter.
        ///
        /// **This is where the feature was broken and nothing caught it.** The
        /// live probe resolved all 214 of its hits and looked perfectly healthy,
        /// because resolving a chain and *materialising* a path are two different
        /// steps and only the first was being exercised. The mount can only
        /// answer "what is in this folder with that id" for a folder it has
        /// populated, and the walk populated each folder in order to find the
        /// next one — leaving the folder at the bottom, the one the hit actually
        /// lives in, never populated. Every result below the top level was
        /// dropped on the floor.
        ///
        /// The fake below is the part that makes it visible: it refuses to answer
        /// for a folder nobody populated, exactly as the real mount does.
        /// </summary>
        private static void PathMaterialisationTests()
        {
            const string Music = @"G:\music";
            const string Year = @"G:\music\2019";
            const string Takes = @"G:\music\2019\takes";

            var children = new Dictionary<(string, string), string>
            {
                [(Music, "Y")] = Year,
                [(Year, "T")] = Takes,
            };

            var populated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var asked = new List<string>();

            void Populate(string folder) { asked.Add(folder); populated.Add(folder); }

            // Exactly what the real mount does: a folder nobody has listed cannot
            // answer what is inside it.
            string? ChildPath(string folder, string id) =>
                populated.Contains(folder) && children.TryGetValue((folder, id), out var p) ? p : null;

            string? Run(string root, string[] chain, Dictionary<string, string?>? carried = null)
            {
                populated.Clear();
                asked.Clear();
                return DriveSearch.PathForChain(root, chain, Populate, ChildPath,
                    carried ?? new Dictionary<string, string?>(StringComparer.Ordinal));
            }

            Equal("a hit in the searched folder resolves to that folder",
                Music, Run(Music, Array.Empty<string>()) ?? "<null>");
            Check("and the folder itself is listed, or the hit cannot be found in it",
                populated.Contains(Music));

            Equal("one level down resolves to the subfolder",
                Year, Run(Music, new[] { "Y" }) ?? "<null>");
            Check("and THAT folder is listed too, not merely its parent",
                populated.Contains(Year),
                "the folder the hit lives in was left unlisted — every result below the top level is dropped");

            Equal("two levels down resolves the whole way",
                Takes, Run(Music, new[] { "Y", "T" }) ?? "<null>");
            Check("and the deepest folder is the one that ends up listed",
                populated.Contains(Takes));

            Equal("every folder on the way down is listed, and none of them twice",
                $"{Music},{Year},{Takes}", string.Join(",", asked));

            // A folder that cannot be resolved is an answer, not a crash, and it
            // is remembered so the next hit inside it does not ask again.
            var carried = new Dictionary<string, string?>(StringComparer.Ordinal);
            Check("an unknown folder gives back nothing rather than a wrong path",
                Run(Music, new[] { "MISSING" }, carried) == null);
            Check("and that is remembered for the next hit in the same place",
                carried.ContainsKey("MISSING") && carried["MISSING"] == null);

            // The shortcut has to skip the walk, or a folder holding fifty hits
            // is fifty listings of everything above it.
            var warm = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Y"] = Year };
            Equal("a folder already resolved is not walked to a second time",
                Year, Run(Music, new[] { "Y" }, warm) ?? "<null>");
            Equal("and only the folder the hit is in is listed",
                Year, string.Join(",", asked));
        }

        private static string Chain(string root, string? parent, IReadOnlyDictionary<string, string?> graph)
        {
            var chain = DriveSearch.ChainUnder(root, parent, graph);
            return chain == null ? "<null>" : string.Join(",", chain);
        }

        /// <summary>
        /// The term goes into a query string Google parses, and two characters
        /// that are ordinary in a file name end that string early.
        /// </summary>
        private static void QueryEscapingTests()
        {
            Equal("an ordinary term is untouched", "love", DriveClient.EscapeQuery("love"));

            // "DXSTINY - DON'T GO.flac" is on this very Drive.
            Equal("an apostrophe is escaped rather than closing the literal",
                @"don\'t go", DriveClient.EscapeQuery("don't go"));

            Equal("a backslash is escaped first, so it cannot eat the next escape",
                @"a\\b", DriveClient.EscapeQuery(@"a\b"));

            Equal("and both together come out in the right order",
                @"a\\\'b", DriveClient.EscapeQuery(@"a\'b"));
        }

        /// <summary>
        /// MainForm needs a message loop and is not compiled into the suite, so
        /// the wiring is checked by reading it — the same way the Drive branches
        /// of New folder, New file and Rename are.
        /// </summary>
        private static void SourceShapeTests()
        {
            var main = Source("MainForm.cs");
            if (main == null) { Check("MainForm.cs can be read", false); return; }

            Check("F1 opens the search",
                main.Contains("Item(\"&Search here…\", Keys.F1, SearchHere)", StringComparison.Ordinal));

            Check("Escape leaves the results",
                main.Contains("case Keys.Escape when pane.SearchTerm != null:", StringComparison.Ordinal));

            // Enter on a result goes to where the file lives. Without this it
            // would play the track, which is the other reasonable answer and not
            // the one that was asked for.
            Check("Enter on a result goes to the folder it lives in",
                main.Contains("if (Active.SearchTerm != null)", StringComparison.Ordinal) &&
                main.Contains("_ = NavigateAsync(folder, preferPath: entry.Path);",
                    StringComparison.Ordinal));

            // The results are not a folder, and two things would quietly replace
            // them with one: the folder watcher, and F5.
            Check("the folder watcher does not overwrite a set of results",
                main.Contains("if (pane.SearchTerm != null) return;", StringComparison.Ordinal));
            // Deliberately loose: it pins the decision — refresh asks whether
            // results are showing before it reloads anything — and not the
            // punctuation around it. The tight version broke the moment the call
            // gained a try/catch, which is a test failing for a change that fixed
            // a bug. The guard itself is checked below, by count.
            Check("and refresh runs the search again rather than reloading the folder",
                main.Contains("if (Active.SearchTerm is { } term)", StringComparison.Ordinal));

            // Leaving has to clear the flag, or the watcher stays off and Escape
            // goes on claiming there is somewhere to go back to.
            Check("navigating anywhere clears the search",
                main.Contains("pane.SearchTerm = null;", StringComparison.Ordinal));

            // The position to come back to is the one the search started from.
            // Recording it from inside the results would overwrite it with a row
            // belonging to some other folder entirely.
            Check("a position is never remembered from inside the results",
                main.Contains("if (!wasSearching) RememberPosition(pane);", StringComparison.Ordinal));

            var drive = Source("GoogleDrive.cs");
            Check("a Drive search asks Google rather than walking placeholders",
                drive != null &&
                drive.Contains("client.FindByName(term, token, most, driveId)", StringComparison.Ordinal) &&
                drive.Contains("client.AllFolders(token, driveId: driveId)", StringComparison.Ordinal),
                "a recursive walk of the letter is one listing per folder");

            Check("and a hit's real path comes from its id, not from the name Drive returned",
                drive != null &&
                drive.Contains("mount.ChildPathForId(parentPath, match.Id)", StringComparison.Ordinal),
                "Sanitise and Unique mean the Drive name is often not the name on disk");

            // Populate is a *synchronous* Drive listing at about 1.3 seconds, and
            // it runs after these awaits. Without ConfigureAwait every one of
            // those continuations comes back to the UI thread, so a search across
            // five folders is six seconds of frozen window — the one rule this
            // codebase does not bend, and the reason LoadEntriesAsync wraps its
            // own Populate in a Task.Run.
            Check("a Drive search never resumes on the thread that asked for it",
                drive != null &&
                drive.Contains("client.FindByName(term, token, most, driveId).ConfigureAwait(false)", StringComparison.Ordinal) &&
                drive.Contains("FolderGraph(client, driveId, token).ConfigureAwait(false)", StringComparison.Ordinal) &&
                drive.Contains("client.RootId(token).ConfigureAwait(false)", StringComparison.Ordinal),
                "a synchronous folder listing would run on the UI thread");

            // Escape has two meanings and the order between them matters: while a
            // search is still gathering there is nothing showing to go back to,
            // and a walk of a whole disk that cannot be abandoned is a window
            // somebody has to wait out.
            Check("Escape stops a search that is still running",
                main.Contains("case Keys.Escape when pane.Searching:", StringComparison.Ordinal));
            Check("and the running flag is given back in a finally",
                main.Contains("pane.Searching = false;", StringComparison.Ordinal) &&
                main.Contains("finally\n            {\n                // In a finally", StringComparison.Ordinal),
                "a flag left standing is Escape cancelling a search that ended minutes ago");

            // The path bar is set from CurrentPath in two places that know
            // nothing about search, and a pane showing results has a CurrentPath
            // that is the folder they came from.
            Check("the path bar asks the pane rather than reading CurrentPath directly",
                !main.Contains("_pathLabel.Text = IsDrivesView ? \"Drives\" : Active.CurrentPath;",
                    StringComparison.Ordinal),
                "switching tabs relabels a set of results as an ordinary folder");

            // Results are a list of files and folders like any other, and
            // somebody with "folders first" set means it here too.
            Check("results are sorted the way every other list is",
                main.Contains("entries = SortLikeAFolder(entries);", StringComparison.Ordinal));

            // SearchHere and Refresh_ are both `async void`, and only the search
            // itself is inside the try — building the rows, naming the pane and
            // announcing the count are not. Anything escaping goes to
            // Application.ThreadException and puts the crash dialog up.
            Check("an async void search cannot take the window down with it",
                System.Text.RegularExpressions.Regex.Matches(
                    main, @"try \{ await RunSearchAsync\(term\); \}").Count is var guarded && guarded >= 2 &&
                guarded == System.Text.RegularExpressions.Regex.Matches(main, @"await RunSearchAsync\(").Count,
                "every caller of RunSearchAsync must guard it");

            // A second search cancels the first, and the loser must not put the
            // winner's flag down on its way out — that leaves a search running
            // that Escape no longer stops.
            Check("a superseded search does not clear the running flag",
                main.Contains("if (ReferenceEquals(pane.EnumCts, mine)) pane.Searching = false;",
                    StringComparison.Ordinal));

            // Written twice and read nowhere is the shape this codebase already
            // caught once, in DriveFileCache.FetchedAhead.
            Check("a truncated result set is reported where it can still be read",
                main.Contains("pane.SearchTruncated ? \", and there are more\" : \"\"",
                    StringComparison.Ordinal),
                "SearchTruncated is set and never looked at");
        }

        private static string? Source(string name)
        {
            var here = new DirectoryInfo(AppContext.BaseDirectory);

            for (int up = 0; up < 8 && here != null; up++, here = here.Parent)
            {
                var candidate = Path.Combine(here.FullName, "src", name);
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
            }

            return null;
        }
    }
}
