using System;
using System.IO;

namespace ExplorerNative
{
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

            var ends = Body(src, "Task DeleteOtherSyncEnds(");
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
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }
}
