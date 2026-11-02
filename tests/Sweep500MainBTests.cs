using System;
using System.IO;
using System.Threading.Tasks;

namespace ExplorerNative
{
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
                branch.Contains("DrivesPath", StringComparison.Ordinal) &&
                branch.Contains("NeedsRefresh", StringComparison.Ordinal) &&
                branch.Contains("ReloadQuietly", StringComparison.Ordinal), Excerpt(branch));
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
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }
}
