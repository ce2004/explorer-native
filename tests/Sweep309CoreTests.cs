using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ExplorerNative
{
    /// <summary>
    /// The 3.0.9 sweep of the settings and core files: the startup entry
    /// announced when nothing changed, the tray tooltip cut through an emoji,
    /// merged settings sharing lists with the dialog's copy, shortcut text naming
    /// no key reported as taken, a newer build's speech choices lost on the way
    /// through an older file, the console's device names, search and stepping up
    /// through a folder whose name ends in a dot, Reset moving the Drive letter,
    /// and a reload after a salvaged file taking the file wholesale.
    /// </summary>
    internal static class Sweep309CoreTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 3.0.9, settings and core:");

            StartupAnnouncesOnlyWhatTheKeyHolds();
            TooltipCutsBetweenWholeCharacters();
            MergedSettingsOwnTheirLists();
            ShortcutTextNamingNoKeyIsInvalid();
            ConsoleDeviceNames();
            SearchReadsAFolderEndingInADot();
            SteppingUpOutOfAFolderEndingInADot();
            ResetKeepsTheDriveLetter();
            TurnAllOffKeepsANewerBuildsChoices();

            var previous = Settings.OverrideAppDataDir;
            try
            {
                RetiredSwitchesKeepANewerBuildsIds();
                ReloadAfterASalvagedFileKeepsTheSession();
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
            }

            Console.WriteLine();
        }

        private static string NewSettingsDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-sweep309-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Settings.OverrideAppDataDir = dir;
            return dir;
        }

        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-sweep309-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            // Literal, so folders whose names end in a dot go too.
            try { Directory.Delete(@"\\?\" + dir, recursive: true); } catch { }
        }

        /// <summary>
        /// With no installed copy, asking for startup while a Run value was
        /// already there declined the write and still announced "will start with
        /// Windows", because the change was judged against what was asked for.
        /// Now it is judged against what the key holds before and after.
        /// Snapshots and restores the real Run value.
        /// </summary>
        private static void StartupAnnouncesOnlyWhatTheKeyHolds()
        {
            const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
            object? saved;
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(run))
                saved = key?.GetValue("ExplorerNative");

            var said = new List<string>();
            void Heard(string id, string message) => said.Add(id);
            ShellRegistration.Changed += Heard;
            try
            {
                // Already there: asking again is no change, whether the request
                // is granted (installed) or declined (no installed copy).
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(run))
                    key.SetValue("ExplorerNative", "\"C:\\nowhere\\ExplorerNative.exe\"");
                ShellRegistration.SetStartup(true);
                Check("startup: asking for it while it is already there announces nothing",
                    said.Count == 0, string.Join(", ", said));

                // Taken away: one "off", and what it says matches the key.
                said.Clear();
                ShellRegistration.SetStartup(false);
                Check("startup: turning it off says off",
                    said.SequenceEqual(new[] { "startup.off" }) && !ShellRegistration.IsStartupRegistered(),
                    string.Join(", ", said));

                // Asked for with nothing there: "on" only if the value was written.
                said.Clear();
                ShellRegistration.SetStartup(true);
                bool on = ShellRegistration.IsStartupRegistered();
                Check("startup: an announcement matches what the key now holds",
                    on ? said.SequenceEqual(new[] { "startup.on" }) : said.Count == 0,
                    $"registered {on}, said {string.Join(", ", said)}");
            }
            finally
            {
                ShellRegistration.Changed -= Heard;
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(run);
                    if (saved != null) key.SetValue("ExplorerNative", saved);
                    else key.DeleteValue("ExplorerNative", throwOnMissingValue: false);
                }
                catch { }
            }
        }

        /// <summary>
        /// The tray tooltip is capped at 63 characters, and the cap cut by UTF-16
        /// unit: a title with an emoji across the 63rd unit ended in half of it.
        /// </summary>
        private static void TooltipCutsBetweenWholeCharacters()
        {
            var emoji = new string('a', 62) + "\U0001F600" + "tail";
            var cut = Settings.CutAtCharacter(emoji, 63);
            Check("tooltip: an emoji across the cap is left out whole",
                cut == new string('a', 62) && !char.IsSurrogate(cut[^1]), $"{cut.Length} units");

            var accent = new string('b', 62) + "e\u0301" + "x";
            Equal("tooltip: a letter and its accent stay together", new string('b', 62),
                Settings.CutAtCharacter(accent, 63));

            Equal("tooltip: a short title is untouched", "Explorer Native", Settings.CutAtCharacter("Explorer Native", 63));
            Equal("tooltip: plain text is cut at the cap", new string('c', 63), Settings.CutAtCharacter(new string('c', 80), 63));

            var tray = SourceFile("TrayApplicationContext.cs") ?? "";
            Check("tooltip: the tray uses the whole-character cut",
                tray.Contains("CutAtCharacter(") && !tray.Contains("title[..63]"));
        }

        /// <summary>
        /// WithChanges put the "after" copy's own sync-pair list and folder-sort
        /// table into the merged settings, so a later edit of either changed the
        /// other.
        /// </summary>
        private static void MergedSettingsOwnTheirLists()
        {
            var live = new Settings();
            var before = new Settings();
            var after = new Settings
            {
                DriveSyncPairs = new List<DriveSyncPair> { new() { Name = "Music", CheckMinutes = 0 } },
                FolderSorts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [@"C:\x"] = "Modified descending" },
            };
            after.AudioEqualiserBands[3] = 5;

            var merged = Settings.WithChanges(live, before, after);
            Check("merge: the pairs came across", merged.DriveSyncPairs.Count == 1 && merged.DriveSyncPairs[0].Name == "Music");
            Check("merge: a list of its own", !ReferenceEquals(merged.DriveSyncPairs, after.DriveSyncPairs));
            Check("merge: pairs of its own", !ReferenceEquals(merged.DriveSyncPairs[0], after.DriveSyncPairs[0]));
            Check("merge: a pair's unsaved fields come across too", merged.DriveSyncPairs[0].CheckMinutes == 0);
            Check("merge: a sort table of its own", !ReferenceEquals(merged.FolderSorts, after.FolderSorts));
            Check("merge: the sort table keeps ignoring case", merged.FolderSorts.ContainsKey(@"c:\X"));

            after.DriveSyncPairs[0].Paused = true;
            after.FolderSorts[@"C:\y"] = "Size";
            after.AudioEqualiserBands[3] = 9;
            Check("merge: editing the dialog's copy afterwards leaves the merged one alone",
                !merged.DriveSyncPairs[0].Paused && merged.FolderSorts.Count == 1 && merged.AudioEqualiserBands[3] == 5);
        }

        /// <summary>
        /// "Ctrl+Alt+KeyCode", a mouse button or a modifier on its own parsed as
        /// a shortcut, went to Windows, was refused, and was reported as taken by
        /// another application. They name no key and are refused when read, and
        /// the tray reports such text in its one settings-mistake dialog.
        /// </summary>
        private static void ShortcutTextNamingNoKeyIsInvalid()
        {
            foreach (var text in new[] { "Ctrl+Alt+KeyCode", "Ctrl+Alt+Modifiers", "Ctrl+LButton", "Ctrl+Alt+RButton",
                                         "Ctrl+XButton1", "Ctrl+ShiftKey", "Alt+LWin", "Ctrl+Alt+ControlKey" })
                Check($"shortcut: \"{text}\" is not a shortcut", !Shortcut.Parse(text).IsAssigned,
                    Shortcut.Parse(text).ToString());

            Equal("shortcut: an ordinary one still reads", "Ctrl+Alt+P", Shortcut.Parse("ctrl+alt+p").ToString());
            Equal("shortcut: a media key still reads", "MediaPlayPause", Shortcut.Parse("MediaPlayPause").ToString());
            Equal("shortcut: a function key still reads", "Ctrl+F5", Shortcut.Parse("Ctrl+F5").ToString());
            Equal("shortcut: a digit still reads", "Ctrl+Alt+1", Shortcut.Parse("Ctrl+Alt+1").ToString());

            var tray = SourceFile("TrayApplicationContext.cs") ?? "";
            int register = tray.IndexOf("private void RegisterHotkeys(", StringComparison.Ordinal);
            int dialog = tray.IndexOf("private void ShowShortcutDialog(", StringComparison.Ordinal);
            var body = register >= 0 && dialog > register ? tray[register..dialog] : "";
            Check("shortcut: text that names no key is reported, not skipped as unset",
                body.Contains("IsNullOrWhiteSpace(text)") && body.Contains("unreadable.Add("));
            Check("shortcut: and it goes into the one dialog",
                body.Contains("ShowShortcutDialog(reserved, duplicates, unusable, unreadable)"));
        }

        /// <summary>
        /// Windows' own path parser turns a bare CONIN$ or CONOUT$ into the
        /// console (\\.\CONIN$), so such names are refused. COM0 and LPT0 are
        /// ordinary names to Windows and stay allowed.
        /// </summary>
        private static void ConsoleDeviceNames()
        {
            Check("device name: CONIN$ is refused", NameRules.DescribeBadName("CONIN$") != null);
            Check("device name: conout$ is refused", NameRules.DescribeBadName("conout$") != null);
            Check("device name: CONIN$.txt is an ordinary name", NameRules.DescribeBadName("CONIN$.txt") == null);
            Check("device name: COM0 is an ordinary name", NameRules.DescribeBadName("COM0") == null);
            Check("device name: LPT0.txt is an ordinary name", NameRules.DescribeBadName("LPT0.txt") == null);
            Check("device name: COM1 is still refused", NameRules.DescribeBadName("COM1") != null);
        }

        /// <summary>
        /// Searching walked "Backup." through its ordinary path, which Windows
        /// reads as "Backup": the sibling's files were listed under the wrong
        /// folder and the folder's own were never found.
        /// </summary>
        private static void SearchReadsAFolderEndingInADot()
        {
            var root = NewTempDir();
            try
            {
                var dotted = Path.Combine(root, "Backup.");
                Directory.CreateDirectory(@"\\?\" + dotted);
                File.WriteAllText(@"\\?\" + Path.Combine(dotted, "inside.txt"), "x");
                var sibling = Path.Combine(root, "Backup");
                Directory.CreateDirectory(sibling);
                File.WriteAllText(Path.Combine(sibling, "other.txt"), "y");

                var inside = FileSearch.Local(root, "inside", true, true, 100, default);
                Check("search: a file inside a folder ending in a dot is found",
                    inside.Hits.Count == 1 && inside.Hits[0].Path == Path.Combine(dotted, "inside.txt"),
                    string.Join(", ", inside.Hits.Select(h => h.Path)));

                var other = FileSearch.Local(root, "other", true, true, 100, default);
                Check("search: the sibling's file is found once, under the sibling",
                    other.Hits.Count == 1 && other.Hits[0].Path == Path.Combine(sibling, "other.txt"),
                    string.Join(", ", other.Hits.Select(h => h.Path)));
            }
            finally { Cleanup(root); }
        }

        /// <summary>
        /// Stepping up out of "Backup." resolved it through GetFullPath, which
        /// drops the dot, so the cursor looked for "Backup" — the sibling, or
        /// nothing — instead of the folder just left.
        /// </summary>
        private static void SteppingUpOutOfAFolderEndingInADot()
        {
            Equal("step up: lands on a folder ending in a dot", @"C:\x\Backup.",
                NavigationHistory.ChildOnPathTo(@"C:\x", @"C:\x\Backup.") ?? "(null)");
            Equal("step up: and from inside it", @"C:\x\Backup.",
                NavigationHistory.ChildOnPathTo(@"C:\x", @"C:\x\Backup.\Sub") ?? "(null)");
            Equal("step up: a name ending in a space too", @"C:\x\Notes ",
                NavigationHistory.ChildOnPathTo(@"C:\x", @"C:\x\Notes \a.txt") ?? "(null)");
            Equal("step up: ordinary names unchanged", @"C:\Users\Conner",
                NavigationHistory.ChildOnPathTo(@"C:\Users", @"C:\Users\Conner\Documents") ?? "(null)");
            Equal("step up: a bare drive still means its root", @"C:\Windows",
                NavigationHistory.ChildOnPathTo(@"C:", @"C:\Windows") ?? "(null)");
        }

        /// <summary>
        /// Reset keeps Google Drive, but not its letter: a drive mounted on X:
        /// went back to G: under every open folder that named X:.
        /// </summary>
        private static void ResetKeepsTheDriveLetter()
        {
            var old = new Settings { GoogleDriveEnabled = true, GoogleDriveLetter = "X" };
            var reset = SettingsReset.Defaults(old, @"C:\one", @"D:\two");
            Equal("reset: keeps the Google Drive letter", "X", reset.GoogleDriveLetter);
        }

        /// <summary>
        /// "Turn all off" on the Windows notifications page emptied the setting,
        /// ids a newer build had turned on included, where the chooser keeps them.
        /// </summary>
        private static void TurnAllOffKeepsANewerBuildsChoices()
        {
            var form = SourceFile("SettingsForm.cs") ?? "";
            Check("toasts: turning all off keeps the ids this build does not know",
                form.Length > 0 && !form.Contains("_working.WindowsNotifications = \"\";"));
        }

        /// <summary>
        /// An older file's retired speech switches are folded into
        /// SpeechOverrides; writing it back dropped every id this build has no
        /// message for, which is a newer build's choices on a trip back and forth.
        /// </summary>
        private static void RetiredSwitchesKeepANewerBuildsIds()
        {
            var kept = Notifications.FormatOverridesKeeping(
                new Dictionary<string, NotificationChannel> { ["error.generic"] = NotificationChannel.None },
                "zz.from.a.newer.build=1;error.generic=1");
            Check("overrides: a newer build's id is kept when written",
                kept.Contains("zz.from.a.newer.build=1") && kept.Contains("error.generic=0"), kept);

            var dir = NewSettingsDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "settings.json"),
                    "{\"SettingsVersion\": 13, \"SpeakErrors\": false, " +
                    "\"SpeechOverrides\": \"zz.from.a.newer.build=1\"}");
                var loaded = Settings.Load();
                Check("overrides: an old box is still carried into the list",
                    loaded.SpeechOverrides.Contains("error.generic=0"), loaded.SpeechOverrides);
                Check("overrides: and the newer build's id survives it",
                    loaded.SpeechOverrides.Contains("zz.from.a.newer.build=1"), loaded.SpeechOverrides);
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// A file that was damaged when this process read it was salvaged, but a
        /// later reload could not read that copy again to see what had changed,
        /// so it took the file wholesale: the volume and everything else changed
        /// this session went back to the file's.
        /// </summary>
        private static void ReloadAfterASalvagedFileKeepsTheSession()
        {
            var dir = NewSettingsDir();
            try
            {
                var path = Path.Combine(dir, "settings.json");
                File.WriteAllText(path,
                    "{\"FontSize\": \"big\", \"WindowTitle\": \"Mine\", \"AudioVolumePercent\": 50}");
                var live = Settings.Load();
                Check("salvaged: the readable values came through",
                    live.WindowTitle == "Mine" && live.AudioVolumePercent == 50);

                // Changed this session, not saved.
                live.AudioVolumePercent = 33;

                // Somebody else writes the file (a command-line flag).
                var outside = new Settings { WindowTitle = "Mine", AudioVolumePercent = 50, SetAsDefaultFileExplorer = true };
                ProtectedFile.WriteAllText(path, JsonSerializer.Serialize(outside, Settings.JsonFormat));

                var merged = Settings.ReloadOnto(live);
                Check("salvaged: a reload takes what the file changed",
                    merged.SetAsDefaultFileExplorer);
                Check("salvaged: and keeps what this session changed",
                    merged.AudioVolumePercent == 33, "volume " + merged.AudioVolumePercent);
            }
            finally { Cleanup(dir); }
        }

        private static string? SourceFile(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }
}
