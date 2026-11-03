using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
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
            try { return File.ReadAllText(Path.Combine(dir.FullName, "src", name)); } catch { return ""; }
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
}
