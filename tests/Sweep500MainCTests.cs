using System;
using System.IO;
using System.Threading.Tasks;

namespace ExplorerNative
{
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
            await Task.CompletedTask;
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
            var words = Body(main, "private static string EmptyWords(");
            Check("an empty Apple Music list is a list, as navigation says",
                say.Contains("EmptyWords(", StringComparison.Ordinal) &&
                words.Contains("AppleMusic.Is", StringComparison.Ordinal) && words.Contains("This list is empty", StringComparison.Ordinal));
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
            try { return File.ReadAllText(Path.Combine(dir.FullName, "src", name)); } catch { return null; }
        }
    }
}
