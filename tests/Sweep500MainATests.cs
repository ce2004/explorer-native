using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ExplorerNative
{
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
            Check("the tab behind loads quietly into its own pane",
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
                                   "_libraryItem", "_appleShuffleItem", "_appleRepeatItem", "_appleUpNextItem" };
            string[] trashOnly = { "_restoreItem", "_deleteForeverItem", "_emptyTrashItem" };
            Distinct("the Shift+F10 menu's access keys are unique for files, archives and audio",
                Labels(context, appleOnly.Concat(trashOnly).ToArray()));

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
            return p == null ? "" : File.ReadAllText(p);
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
}
