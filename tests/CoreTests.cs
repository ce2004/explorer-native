using System.Collections.Generic;
using System.Formats.Tar;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System;

namespace ExplorerNative
{
    /// <summary>
    /// The 3.0.8 sweep of the settings and core files: a settings file that
    /// would not decrypt being merged as defaults, a stray temp file left by the
    /// re-encryption of a plain file, device names with a space before the
    /// extension, and Preferences speaking with spoken announcements turned off.
    /// </summary>
    internal static class Sweep308CoreTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 3.0.8, settings and core:");

            var previous = Settings.OverrideAppDataDir;
            try
            {
                UndecryptableFileIsNotMergedAsDefaults();
                ReEncryptionLeavesNoTempFile();
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
            }

            DeviceNamesWithASpaceBeforeTheExtension();
            PreferencesHonoursTheMasterSpeechSwitch();

            Console.WriteLine();
        }

        private static string NewSettingsDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-sweep308-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Settings.OverrideAppDataDir = dir;
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        /// <summary>
        /// If settings.json stopped decrypting while the application ran, a reload
        /// (a command-line flag, or the last save on the way out) read it as the
        /// defaults and merged that over the running copy: every setting went back
        /// to its default and was then saved.
        /// </summary>
        private static void UndecryptableFileIsNotMergedAsDefaults()
        {
            var dir = NewSettingsDir();
            try
            {
                var path = Path.Combine(dir, "settings.json");
                new Settings { FontSize = 14, SetAsDefaultFileExplorer = true, WindowTitle = "Mine" }.Save();
                var live = Settings.Load();
                Check("undecryptable: the file loads as saved", live.FontSize == 14 && live.SetAsDefaultFileExplorer);

                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("ENDPAPI1this is not a DPAPI blob"));
                var merged = Settings.ReloadOnto(live);
                Check("undecryptable: a reload keeps the running settings rather than the defaults",
                    merged.FontSize == 14 && merged.SetAsDefaultFileExplorer && merged.WindowTitle == "Mine",
                    $"FontSize {merged.FontSize}, takeover {merged.SetAsDefaultFileExplorer}, title {merged.WindowTitle}");

                // Unchanged: the next save may still replace it, and a copy is kept.
                var loaded = Settings.Load(out bool failed);
                Check("undecryptable: Load still lets the next save replace it",
                    !failed && !loaded.UnreadableOnDisk);
                Check("undecryptable: and a copy is kept beside it",
                    Directory.GetFiles(dir).Any(f => Path.GetFileName(f).Contains(".undecryptable-")));
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// A plain settings.json left by an older build is encrypted when it is
        /// read. If another program held it at that moment the swap failed and
        /// settings.json.&lt;pid&gt;.enc.tmp stayed in AppData for good.
        /// </summary>
        private static void ReEncryptionLeavesNoTempFile()
        {
            var dir = NewSettingsDir();
            try
            {
                var path = Path.Combine(dir, "settings.json");
                File.WriteAllText(path, "{\"FontSize\": 12}");

                Settings loaded;
                // Read-shared, not delete-shared: the read works, the swap cannot.
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    loaded = Settings.Load();

                Equal("re-encrypt: the plain file still reads", "12", loaded.FontSize.ToString());
                var strays = Directory.GetFiles(dir).Where(f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)).ToList();
                Check("re-encrypt: a failed swap leaves no temp file behind", strays.Count == 0,
                    string.Join(", ", strays.Select(Path.GetFileName)));
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// Windows drops spaces at the end of a name's stem before it looks for a
        /// device, so "CON .txt" was accepted as a name and then wrote to the
        /// console rather than to a file.
        /// </summary>
        private static void DeviceNamesWithASpaceBeforeTheExtension()
        {
            Check("device name: CON .txt is refused", NameRules.DescribeBadName("CON .txt") != null);
            Check("device name: nul  .tar.gz is refused", NameRules.DescribeBadName("nul  .tar.gz") != null);
            Check("device name: CON.txt is still refused", NameRules.DescribeBadName("CON.txt") != null);
            Check("device name: Console .txt is fine", NameRules.DescribeBadName("Console .txt") == null);
            Check("device name: a name with an inner space is fine", NameRules.DescribeBadName("COM 1.txt") == null);
        }

        /// <summary>
        /// With "Enable spoken announcements" off, the Windows notifications and
        /// web app buttons in Preferences still spoke, because they called the
        /// speech queue directly. Everything Preferences says goes through its one
        /// Say, which asks the master switch.
        /// </summary>
        private static void PreferencesHonoursTheMasterSpeechSwitch()
        {
            var source = SourceFile("SettingsForm.cs");
            if (source == null)
            {
                Check("speech switch: SettingsForm.cs found", false);
                return;
            }

            int speaks = 0;
            for (int at = source.IndexOf("Speech.Speak(", StringComparison.Ordinal); at >= 0;
                 at = source.IndexOf("Speech.Speak(", at + 1, StringComparison.Ordinal))
                speaks++;

            int say = source.IndexOf("private void Say(string message)", StringComparison.Ordinal);
            var sayBody = say >= 0 ? source.Substring(say, Math.Min(200, source.Length - say)) : "";
            Check("speech switch: Preferences speaks only through Say",
                speaks == 1 && sayBody.Contains("Speech.Speak(") && sayBody.Contains("SpeakEnabled"),
                speaks + " direct calls");
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

    // ---- merged from Sweep309CoreTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

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

    // ---- merged from Fix418Tests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// 4.1.8: four bugs a review of 4.1.7 found. ArchiveBrowser, the Drive-only
    /// check and AppleMusicPlayer are pinned by their source (the first two run
    /// tar or a Drive rig; the player is not in the suite), the shuffle swap by
    /// behaviour.
    /// </summary>
    internal static class Fix418Tests
    {
        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            Console.WriteLine("4.1.8 fixes:");

            var browser = Source("ArchiveBrowser.cs") ?? "";
            check("an archive is partial only when tar stopped short, not because of a note about one entry",
                browser.Contains("if (!damaged && stoppedShort) partial", StringComparison.Ordinal) &&
                !browser.Contains("if (!damaged && reason != null) partial = reason;", StringComparison.Ordinal),
                "a symlink or repeated name marked a whole 7z partial and it was extracted again on every open");

            var tar = Source("BsdTar.cs") ?? "";
            check("bsdtar says when it did not get to the end",
                tar.Contains("if (unfinished) stoppedShort?.Invoke();", StringComparison.Ordinal), null);

            var driveOnly = Source("DriveMonitor.DriveOnly.cs") ?? "";
            check("a file inside a hidden folder counts as never synced",
                driveOnly.Contains("if (folderLeftOut || (file.Attributes & leftOut) != 0) neverSynced.Add(relative);", StringComparison.Ordinal),
                "Keep on Drive only waited five minutes for files the sync skips");
            check("Keep on Drive only says which files were left on the PC",
                driveOnly.Contains("{ leftOnPc.Add(relative); continue; }", StringComparison.Ordinal) &&
                driveOnly.Contains("left on this PC because", StringComparison.Ordinal),
                "it said \"cleared off this PC\" with temporary files still there");

            var player = Source("AppleMusicPlayer.cs") ?? "";
            check("a new shuffled round is checked against the song that is starting",
                player.Contains("Refill(path);", StringComparison.Ordinal) &&
                !player.Contains("string.Equals(songs[0], _currentPath", StringComparison.Ordinal),
                "the last song of a round could play twice in a row");

            var songs = new List<string> { "a", "b", "c" };
            AppleMusicPlayerProbe(songs, "a");
            equal("the song just heard goes to the end of the new round", "c,b,a", string.Join(",", songs));
            songs = new List<string> { "b", "a", "c" };
            AppleMusicPlayerProbe(songs, "a");
            equal("and a round that does not start with it is left alone", "b,a,c", string.Join(",", songs));
        }

        // AppleMusicPlayer.cs is not compiled into the suite (WebView2), so the
        // swap is the same two lines, kept beside the source check above.
        private static void AppleMusicPlayerProbe(List<string> songs, string last)
        {
            if (songs.Count > 1 && string.Equals(songs[0], last, StringComparison.OrdinalIgnoreCase))
                (songs[0], songs[^1]) = (songs[^1], songs[0]);
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            try { return File.ReadAllText(Path.Combine(dir.FullName, "src", name)); } catch { return null; }
        }
    }

    // ---- merged from Round2FixTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// Round-two fixes: a busy port written over the saved one, an ampersand in
    /// an information label taken as an access key, help and one-dash switches
    /// starting the application, the single-instance handoff topping out at
    /// about sixty launches a second, and a window title with a NUL cut short
    /// by OK.
    /// </summary>
    internal static class Round2FixTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Round two fixes:");

            var previous = Settings.OverrideAppDataDir;
            var dir = NewSettingsDir();
            try
            {
                PortIsNeverWrittenBackOnReloadTests();
                InfoLabelTests();
                OptionTests();
                WindowTitleTests();
                await HandoffThroughputTests();
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(dir, recursive: true); } catch { }
            }

            Console.WriteLine();
        }

        private static string NewSettingsDir()
        {
            const string scratch =
                @"C:\Users\Conner\AppData\Local\Temp\claude\C--Users-Conner\210da054-69e3-4f58-81af-73a744528057\scratchpad";
            var root = Directory.Exists(scratch)
                ? Path.Combine(scratch, "fix-r2-settings")
                : Path.Combine(Path.GetTempPath(), "ExplorerNativeTests_r2settings");
            var dir = Path.Combine(root, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            Settings.OverrideAppDataDir = dir;
            return dir;
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private static string OnSta(Action work, int seconds = 60)
        {
            string error = "";
            var thread = new Thread(() =>
            {
                try { work(); }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(seconds))) return "timed out";
            return error;
        }

        private static List<T> FindAll<T>(Control root) where T : Control
        {
            var found = new List<T>();
            foreach (Control child in root.Controls)
            {
                if (child is T match) found.Add(match);
                found.AddRange(FindAll<T>(child));
            }
            return found;
        }

        /// <summary>
        /// The tray is not compiled into the suite (it needs the whole
        /// application), so the rule is read from its source: a port that could
        /// not be had is never written back on a reload, and on OK it is the
        /// previously saved port that goes back, not the one listening.
        /// </summary>
        private static void PortIsNeverWrittenBackOnReloadTests()
        {
            var tray = Source("TrayApplicationContext.cs");
            Check("the tray's source is there to read", tray != null);
            if (tray == null) return;

            int start = tray.IndexOf("public void ApplySettings(Settings settings, bool reload", StringComparison.Ordinal);
            int end = start < 0 ? -1 : tray.IndexOf("ReconcileWebApp(portWas)", start, StringComparison.Ordinal);
            Check("ApplySettings and its port block are found", start >= 0 && end > start);
            if (start < 0 || end <= start) return;
            var block = tray.Substring(start, end - start);

            Check("the listening port is never saved as the setting",
                !block.Contains("settings.WebAppPort = portWas", StringComparison.Ordinal));

            int reloadBranch = block.IndexOf("else if (reload)", StringComparison.Ordinal);
            int okBranch = reloadBranch < 0 ? -1 : block.IndexOf("else", reloadBranch + 4, StringComparison.Ordinal);
            Check("a failed move on a reload has a branch of its own", reloadBranch >= 0 && okBranch > reloadBranch);
            if (reloadBranch < 0 || okBranch <= reloadBranch) return;

            var onReload = block.Substring(reloadBranch, okBranch - reloadBranch);
            Check("which saves nothing", !onReload.Contains(".Save(", StringComparison.Ordinal));
            Check("and changes no setting", !onReload.Contains("WebAppPort =", StringComparison.Ordinal));

            var onOk = block.Substring(okBranch);
            Check("OK that could not move puts back the port saved before it",
                onOk.Contains("settings.WebAppPort = savedPortWas", StringComparison.Ordinal));
            Check("which is what was saved, read before the new settings replace it",
                block.IndexOf("int savedPortWas = _settings.WebAppPort", StringComparison.Ordinal) >= 0 &&
                block.IndexOf("int savedPortWas = _settings.WebAppPort", StringComparison.Ordinal) <
                block.IndexOf("_settings = settings;", StringComparison.Ordinal));
        }

        /// <summary>
        /// Information labels show data — a device name, an account, a link — so
        /// an ampersand in one is a character, not an access key.
        /// </summary>
        private static void InfoLabelTests()
        {
            int infos = 0, mnemonic = 0;
            bool tomCatKeepsItsAmpersand = false;
            var error = OnSta(() =>
            {
                using var window = new SettingsForm(new Settings { SpeakEnabled = false }, null);
                window.BuildAllPages();
                var labels = FindAll<Label>(window).Where(l => l.GetType().Name == "InfoLabel").ToList();
                infos = labels.Count;
                mnemonic = labels.Count(l => l.UseMnemonic);
                if (labels.Count > 0)
                {
                    labels[0].Text = "Tom&Cat";
                    tomCatKeepsItsAmpersand = !labels[0].UseMnemonic && labels[0].Text == "Tom&Cat";
                }
            });
            Equal("Preferences builds for the label check", "", error);
            Check($"Preferences has information labels ({infos})", infos > 0);
            Equal("no information label takes & as an access key", "0", mnemonic.ToString());
            Check("a device called Tom&Cat is shown and read as Tom&Cat", tomCatKeepsItsAmpersand);
        }

        /// <summary>
        /// --help, -h and /? are help; anything else starting with - or / that
        /// is not an existing path is an unknown option.
        /// </summary>
        private static void OptionTests()
        {
            Func<string, bool> nothingExists = _ => false;

            foreach (var help in new[] { "--help", "-h", "/?", "-H", "--HELP" })
            {
                Check($"{help} asks for help", LaunchArguments.IsHelp(help));
                Check($"{help} is not an unknown option",
                    LaunchArguments.UnknownOption(new[] { help }, nothingExists) == null);
            }
            Check("help is found among other arguments",
                LaunchArguments.WantsHelp(new[] { @"C:\Users", "/?" }));
            Check("a folder alone is not a request for help",
                !LaunchArguments.WantsHelp(new[] { @"C:\Users" }));

            foreach (var bad in new[] { "-?", "/help", "-install", "/install", "-x", "/", "--bogus" })
                Equal($"{bad} is an unknown option", bad,
                    LaunchArguments.UnknownOption(new[] { bad }, nothingExists) ?? "(none)");

            Check("a folder whose name starts with a dash is a folder",
                LaunchArguments.UnknownOption(new[] { "-drafts" }, a => a == "-drafts") == null);
            Check("the real check finds the current drive's root as a path",
                LaunchArguments.UnknownOption(new[] { "/" }) == null);
            Check("ordinary paths are not options",
                LaunchArguments.UnknownOption(new[] { @"C:\Users", "D:\"" }, nothingExists) == null);
            Check("the log named after --read-log is not an option",
                LaunchArguments.UnknownOption(new[] { "--read-log", "-old.log" }, nothingExists) == null);
            Check("but a double-dash after it still is",
                LaunchArguments.UnknownOption(new[] { "--read-log", "--bogus" }, nothingExists) == "--bogus");
            Check("the known switches and modifiers are still known",
                LaunchArguments.UnknownOption(
                    LaunchArguments.CommandLineSwitches.Concat(LaunchArguments.Modifiers), nothingExists) == null);

            var text = LaunchArguments.HelpText();
            var missing = LaunchArguments.CommandLineSwitches
                .Where(s => s != "--license" && !text.Contains(s + " ", StringComparison.Ordinal)).ToList();
            Check("the help lists every option", missing.Count == 0, string.Join(", ", missing));
            Check("and says how to ask for it", text.Contains("--help, -h, /?", StringComparison.Ordinal));

            // Program is not in the suite; the order in Main is read from it.
            var program = Source("Program.cs");
            int helpAt = program?.IndexOf("LaunchArguments.WantsHelp(args)", StringComparison.Ordinal) ?? -1;
            int unknownAt = program?.IndexOf("LaunchArguments.UnknownOption(args)", StringComparison.Ordinal) ?? -1;
            int mutexAt = program?.IndexOf("new Mutex(false, SingleInstanceMutexName)", StringComparison.Ordinal) ?? -1;
            Check("help is answered before anything else in Main", helpAt > 0 && helpAt < unknownAt && unknownAt < mutexAt);
            if (helpAt > 0 && unknownAt > helpAt)
            {
                var helpBlock = program!.Substring(helpAt, unknownAt - helpAt);
                Check("help exits with 0", helpBlock.Contains("Environment.ExitCode = 0", StringComparison.Ordinal));
                Check("help prints to the console, or the notice that closes itself",
                    helpBlock.Contains("Console.Out.WriteLine", StringComparison.Ordinal) &&
                    helpBlock.Contains("ShowNotice(", StringComparison.Ordinal));
                var unknownBlock = program.Substring(unknownAt, Math.Max(0, mutexAt - unknownAt));
                Check("an unknown option exits with 2",
                    unknownBlock.Contains("Environment.ExitCode = 2", StringComparison.Ordinal));
            }
        }

        /// <summary>
        /// An edit box stops at a NUL, so a title holding one reads back shorter
        /// once the box has a window. OK must not write that back; typing still
        /// does.
        /// </summary>
        private static void WindowTitleTests()
        {
            const string withNul = "Explorer\0Native";
            string? afterOk = null, afterTyping = null, shown = null;
            var error = OnSta(() =>
            {
                using (var window = new SettingsForm(new Settings { SpeakEnabled = false, WindowTitle = withNul }, null))
                {
                    window.BuildAllPages();
                    var box = FindAll<TextBox>(window).First(t => t.AccessibleName == "Window title");
                    _ = box.Handle; // the window an edit box truncates in
                    shown = box.Text;
                    window.AcceptAsync().GetAwaiter().GetResult();
                    afterOk = window.Result.WindowTitle;
                }

                using (var window = new SettingsForm(new Settings { SpeakEnabled = false, WindowTitle = withNul }, null))
                {
                    window.BuildAllPages();
                    var box = FindAll<TextBox>(window).First(t => t.AccessibleName == "Window title");
                    _ = box.Handle;
                    box.Text = "My files";
                    window.AcceptAsync().GetAwaiter().GetResult();
                    afterTyping = window.Result.WindowTitle;
                }
            });
            Equal("Preferences builds for the title check", "", error);
            Check("the box really does show less than the saved title (the premise)",
                shown != null && shown != withNul, shown?.Replace("\0", "\\0"));
            Equal("OK without touching the title keeps it whole", withNul.Replace("\0", "\\0"),
                (afterOk ?? "(null)").Replace("\0", "\\0"));
            Equal("a title typed in is still saved", "My files", afterTyping ?? "(null)");
        }

        /// <summary>
        /// Two hundred launches at once, each on a thread of its own and started
        /// together, all handed over. It managed about sixty a second: a 15.6ms
        /// timer tick per connection, and a moment after each one with no pipe.
        /// </summary>
        private static async Task HandoffThroughputTests()
        {
            SingleInstance.NameSuffix = "_r2burst_" + Guid.NewGuid().ToString("N")[..8];
            var received = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
            SingleInstance.StartServer(r => received[r] = true);
            try
            {
                await Task.Delay(300);
                const int n = 200;
                var sent = new bool[n];
                using var go = new ManualResetEventSlim(false);
                var threads = Enumerable.Range(0, n).Select(i => new Thread(() =>
                {
                    go.Wait();
                    sent[i] = SingleInstance.SendToRunningInstance($@"C:\r2burst\{i}");
                })
                { IsBackground = true }).ToList();
                foreach (var t in threads) t.Start();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                go.Set();
                foreach (var t in threads) t.Join(TimeSpan.FromSeconds(30));
                clock.Stop();
                for (int i = 0; i < 50 && received.Count < n; i++) await Task.Delay(100);

                Equal($"two hundred simultaneous launches are all delivered ({clock.ElapsedMilliseconds} ms)",
                    n.ToString(), sent.Count(x => x).ToString());
                Equal("and every one arrives", n.ToString(),
                    Enumerable.Range(0, n).Count(i => received.ContainsKey($@"C:\r2burst\{i}")).ToString());
            }
            finally
            {
                SingleInstance.StopServer();
                await Task.Delay(200);
                SingleInstance.NameSuffix = "";
            }
        }
    }

    // ---- merged from AuditFixTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// The findings of the audio and archive audits (2026-10-13), each pinned:
    /// a finished track goes quiet and lets its download go, a hard-linked tar
    /// member opens, a name nested thousands deep cannot end the app, and the
    /// superscript device names are refused in words.
    /// </summary>
    internal static class AuditFixTests
    {
        public static void Run(Action<string, bool, string> check)
        {
            Console.WriteLine("Audio and archive audit fixes:");
            void Check(string what, bool ok, string detail = "") => check(what, ok, detail);

            var playback = Tests_Source("WasapiPlayback.cs") ?? "";
            Check("a finished track's pump waits long, as a paused one does",
                playback.Contains("_drained.WaitOne(_paused || _resting ? 5000 : 250)", StringComparison.Ordinal));
            var player = Tests_Source("AudioPlayer.cs") ?? "";
            int finished = player.IndexOf("private void OnDirectFinished()", StringComparison.Ordinal);
            Check("and a finished track starts the clock on letting its download go",
                finished >= 0 && player.IndexOf("ArmRelease();", finished, StringComparison.Ordinal) is var arm &&
                arm > finished && arm < player.IndexOf("Finished?.Invoke(path);", finished, StringComparison.Ordinal));

            Check("COM¹ is refused as a name, in words",
                NameRules.DescribeBadName("COM¹.txt") != null);
            Check("and LPT³ too", NameRules.DescribeBadName("LPT³") != null);

            var dir = Path.Combine(Path.GetTempPath(), "en-audit-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                // A file and a second name for it, as GNU tar stores one.
                var linked = Path.Combine(dir, "linked.tar");
                var bytes = Encoding.UTF8.GetBytes("the one file under two names");
                using (var file = File.Create(linked))
                using (var writer = new TarWriter(file, TarEntryFormat.Pax))
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "a.txt") { DataStream = new MemoryStream(bytes) });
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "b.txt") { LinkName = "a.txt" });
                }
                var listing = ArchiveBrowser.Open(linked, null, CancellationToken.None);
                string? got = null, why = null;
                try { got = ArchiveBrowser.ExtractOne(listing, "b.txt", null, CancellationToken.None); }
                catch (Exception ex) { why = ex.Message; }
                Check("a hard-linked tar member opens on its own", got != null && File.Exists(got), why ?? "");
                if (got != null && File.Exists(got))
                    Check("with the bytes of the file it names", File.ReadAllBytes(got).SequenceEqual(bytes));

                // Nested ten thousand deep, on a small stack: listed, not fatal.
                var deep = Path.Combine(dir, "deep.tar");
                using (var file = File.Create(deep))
                using (var writer = new TarWriter(file, TarEntryFormat.Pax))
                {
                    var name = string.Join('/', Enumerable.Repeat("a", 10_000)) + "/f.txt";
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(bytes) });
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "ok.txt") { DataStream = new MemoryStream(bytes) });
                }
                ArchiveListing? opened = null;
                Exception? failed = null;
                var thread = new Thread(() =>
                {
                    try { opened = ArchiveBrowser.Open(deep, null, CancellationToken.None); }
                    catch (Exception ex) { failed = ex; }
                }, 256 * 1024);
                thread.Start();
                bool ended = thread.Join(TimeSpan.FromSeconds(60));
                Check("an archive with a name nested 10,000 deep opens on a small stack",
                    ended && opened != null, failed?.Message ?? (ended ? "" : "did not finish"));
                if (opened != null)
                {
                    Check("the impossible name is counted as unsafe, not listed", opened.Unsafe == 1, opened.Unsafe.ToString());
                    Check("and the ordinary one is still there", opened.Find("ok.txt") != null);
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            Console.WriteLine();
        }

        private static string? Tests_Source(string name)
        {
            var at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at != null && !Directory.Exists(Path.Combine(at.FullName, "src"))) at = at.Parent;
            var path = at == null ? null : Path.Combine(at.FullName, "src", name);
            return path != null && File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }

    // ---- merged from EdgeCaseTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// The odd cases a review went looking for: names and archives that are
    /// legal and unusual, and what used to happen to them.
    /// </summary>
    internal static class OddCaseTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Odd cases:");

            NameTests();
            StretchReachTests();
            await SameNamePasteTests();
            await ArchiveEntryTests();
            await ArchiveWritingTests();
            ReloadOfUnreadableFileTests();
            LinkTests();
            MangledNameTests();

            Console.WriteLine();
        }

        /// <summary>
        /// A reload from the command line that finds the file unreadable keeps
        /// what the running copy has, rather than taking the defaults as though
        /// the file had asked for them.
        /// </summary>
        private static void ReloadOfUnreadableFileTests()
        {
            var dir = NewDir();
            var previous = Settings.OverrideAppDataDir;
            try
            {
                Settings.OverrideAppDataDir = dir;
                var live = new Settings { AudioVolumePercent = 37, ShowHiddenFiles = !new Settings().ShowHiddenFiles };
                live.Save();
                _ = Settings.Load();

                File.WriteAllText(Path.Combine(dir, "settings.json"), "{{{ not json at all");
                var reloaded = Settings.ReloadOnto(live);

                Check("a reload of an unreadable file keeps the live settings",
                    reloaded.AudioVolumePercent == 37 && reloaded.ShowHiddenFiles == live.ShowHiddenFiles,
                    $"volume {reloaded.AudioVolumePercent}");
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                Cleanup(dir);
            }
        }

        /// <summary>
        /// A name the system tar printed through a code page that could not
        /// show it still finds the file it was about, and nothing else.
        /// </summary>
        private static void MangledNameTests()
        {
            var one = BsdTar.MangledPattern("Caf?-Bar.txt");
            var run = BsdTar.MangledPattern("Caf�Bar.txt");
            Check("a question mark stands for a character the console could not show",
                one.IsMatch("Café-Bar.txt"));
            Check("and a replacement character for a run of them",
                run.IsMatch("Café–Bar.txt"));
            Check("but not for plain letters", !one.IsMatch("Cafe-Bar.txt"));
            Check("nor for a different name", !run.IsMatch("CaféBaz.txt"));

            // Two code-page bytes that happen to decode as a real character.
            var decoded = BsdTar.MangledPattern("CafÃ©.txt");
            Check("a character decoded from the wrong code page still finds its file",
                decoded.IsMatch("Café.txt"));
            Check("and not a file named in plain letters", !decoded.IsMatch("Cafe.txt"));
        }

        /// <summary>
        /// Only a link is a link. A plain file and folder are not, and a
        /// junction is — the walks that skip links must not skip anything else.
        /// </summary>
        private static void LinkTests()
        {
            var dir = NewDir();
            try
            {
                var file = Path.Combine(dir, "plain.txt");
                File.WriteAllText(file, "x");
                var folder = Directory.CreateDirectory(Path.Combine(dir, "plain"));
                Check("a plain file is not a link", !NameRules.IsLink(new FileInfo(file)));
                Check("a plain folder is not a link", !NameRules.IsLink(folder));

                var junction = Path.Combine(dir, "junction");
                var made = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "cmd.exe", $"/c mklink /J \"{junction}\" \"{folder.FullName}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                });
                made?.WaitForExit(10_000);
                if (Directory.Exists(junction))
                    Check("a junction is a link", NameRules.IsLink(new DirectoryInfo(junction)));
            }
            finally
            {
                // The junction first, so the delete does not follow it.
                try { Directory.Delete(Path.Combine(dir, "junction")); } catch { }
                Cleanup(dir);
            }
        }

        private static string NewDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-edge-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Directory.Delete(dir, true);
            }
            catch { }
        }

        // ---------- names ----------

        private static void NameTests()
        {
            var sanitised = DriveMount.Sanitise("notes.txt .");
            Check("a Drive name ending in a space before a dot ends in neither",
                !sanitised.EndsWith(' ') && !sanitised.EndsWith('.') && NameRules.IsUsableName(sanitised),
                $"\"{sanitised}\"");

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DriveMount.Unique(".env", used);
            Equal("a second dotfile on Drive is numbered at its end", ".env (2)", DriveMount.Unique(".env", used));

            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DriveMount.Unique("Album.2024", folders, folder: true);
            Equal("a second folder with a dot in its name is numbered whole", "Album.2024 (2)",
                DriveMount.Unique("Album.2024", folders, folder: true));

            // An emoji straddling the 250-character cut.
            var longName = new string('a', 249) + "😀" + new string('b', 20);
            var cut = DriveMount.Sanitise(longName);
            Check("shortening a long Drive name never leaves half an emoji",
                cut.Length > 0 && !char.IsHighSurrogate(cut[^1]) && !cut.Any(c => char.IsLowSurrogate(c)
                    && cut.IndexOf(c) > 0 && !char.IsHighSurrogate(cut[cut.IndexOf(c) - 1])),
                $"{cut.Length} characters");

            Check("a name ending in a dot needs the literal path", NameRules.NeedsLiteralPath(@"C:\x\report."));
            Check("and one ending in a space", NameRules.NeedsLiteralPath(@"C:\x\report "));
            Check("an ordinary one does not", !NameRules.NeedsLiteralPath(@"C:\x\report.txt"));
            Equal("a drive path is prefixed", @"\\?\C:\x\report.", NameRules.LiteralPath(@"C:\x\report."));
            Equal("a share path goes through UNC", @"\\?\UNC\nas\music\a.", NameRules.LiteralPath(@"\\nas\music\a."));

            // The premise: the literal path reaches "report." and leaves "report".
            var dir = NewDir();
            try
            {
                var plain = Path.Combine(dir, "report");
                var dotted = Path.Combine(dir, "report.");
                File.WriteAllText(plain, "plain");
                File.WriteAllText(NameRules.LiteralPath(dotted), "dotted");

                File.Delete(NameRules.LiteralPath(dotted));
                Check("deleting the literal path removes the dotted file only",
                    File.Exists(plain) && !File.Exists(NameRules.LiteralPath(dotted)));

                // A new file beside a folder of the same name is numbered as a file.
                Directory.CreateDirectory(Path.Combine(dir, "notes.txt"));
                Equal("a file beside a folder of its name is numbered as a file",
                    Path.Combine(dir, "notes (2).txt"),
                    FileOperations.UniqueName(Path.Combine(dir, "notes.txt"), folder: false));
            }
            finally { Cleanup(dir); }
        }

        // ---------- speed over pitch ----------

        private static void StretchReachTests()
        {
            // Four times the speed two octaves down is a stretch of sixteen; the
            // stretcher stopped at four and played it at normal speed.
            const int channels = 1, rate = 48000;
            long consumed = 0;
            var stretch = new TimeStretch(channels)
            {
                Rate = 16.0,
                Source = (into, frames) =>
                {
                    for (int i = 0; i < frames; i++)
                        into[i] = (float)Math.Sin(2 * Math.PI * 440 * (consumed + i) / rate) * 0.5f;
                    consumed += frames;
                    return frames;
                },
            };

            var output = new float[4096];
            long produced = 0;
            for (int i = 0; i < 200; i++) produced += stretch.Read(output, output.Length);

            double ratio = consumed / (double)Math.Max(1, produced);
            Check($"a stretch of sixteen consumes sixteen times what it plays ({ratio:0.0})",
                ratio > 14 && ratio < 18);
        }

        // ---------- two sources with one name ----------

        private static async Task SameNamePasteTests()
        {
            foreach (bool move in new[] { false, true })
            {
                var dir = NewDir();
                try
                {
                    var a = Path.Combine(dir, "a");
                    var b = Path.Combine(dir, "b");
                    var dest = Path.Combine(dir, "dest");
                    Directory.CreateDirectory(a);
                    Directory.CreateDirectory(b);
                    Directory.CreateDirectory(dest);
                    File.WriteAllText(Path.Combine(a, "song.txt"), "from a");
                    File.WriteAllText(Path.Combine(b, "song.txt"), "from b, longer");

                    var result = await RoboCopyEngine.RunAsync(
                        new[] { Path.Combine(a, "song.txt"), Path.Combine(b, "song.txt") },
                        dest, move, 4, PasteConflictPolicy.AutoRename, null, CancellationToken.None);

                    var landed = Directory.GetFiles(dest).Select(File.ReadAllText).OrderBy(x => x).ToList();
                    Equal($"two sources with one name both arrive ({(move ? "move" : "copy")})",
                        "from a|from b, longer", string.Join("|", landed));
                    if (move)
                        Check("and a move removes both originals only once both have landed",
                            !File.Exists(Path.Combine(a, "song.txt")) && !File.Exists(Path.Combine(b, "song.txt")));
                    Equal("and nothing failed", "0", result.Failed.ToString());
                }
                finally { Cleanup(dir); }
            }

            // Folders too, under the policy that replaces what is already there:
            // the second folder is not what was already there.
            var folders = NewDir();
            try
            {
                var a = Path.Combine(folders, "a", "photos");
                var b = Path.Combine(folders, "b", "photos");
                var dest = Path.Combine(folders, "dest");
                Directory.CreateDirectory(a);
                Directory.CreateDirectory(b);
                Directory.CreateDirectory(dest);
                File.WriteAllText(Path.Combine(a, "one.txt"), "a");
                File.WriteAllText(Path.Combine(b, "one.txt"), "b");

                await RoboCopyEngine.RunAsync(new[] { a, b }, dest, false, 4,
                    PasteConflictPolicy.Overwrite, null, CancellationToken.None);

                var made = Directory.GetDirectories(dest).Select(Path.GetFileName).OrderBy(x => x).ToList();
                Equal("two folders with one name stay two folders", "photos|photos (2)", string.Join("|", made));
            }
            finally { Cleanup(folders); }
        }

        // ---------- what an archive may hold ----------

        private static string MakeZip(string path, params (string Name, string Text)[] entries)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (name, text) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(text);
            }
            return path;
        }

        private static async Task ArchiveEntryTests()
        {
            var dir = NewDir();
            try
            {
                // ".." is refused, never renamed into ".. (2)".
                var escaping = MakeZip(Path.Combine(dir, "escape.zip"),
                    ("../evil.txt", "evil"), ("good.txt", "good"));
                var into = Path.Combine(dir, "into");
                Directory.CreateDirectory(into);
                var result = await ArchiveEngine.ExtractAsync(escaping, into,
                    PasteConflictPolicy.AutoRename, null, 2, null, CancellationToken.None);
                Check("an entry climbing out of the folder is refused",
                    !File.Exists(Path.Combine(dir, "evil.txt")) &&
                    !Directory.EnumerateFileSystemEntries(into).Any(e => Path.GetFileName(e).StartsWith("..")),
                    string.Join(", ", Directory.EnumerateFileSystemEntries(into).Select(Path.GetFileName)));
                Check("and said so, while the rest came out",
                    result.Failed >= 1 && File.Exists(Path.Combine(into, "good.txt")),
                    string.Join("; ", result.Errors));

                // Two names that are one name on Windows.
                var twice = MakeZip(Path.Combine(dir, "twice.zip"), ("Read.txt", "first"), ("read.txt", "second"));
                var twiceOut = Path.Combine(dir, "twice-out");
                var both = await ArchiveEngine.ExtractAsync(twice, twiceOut,
                    PasteConflictPolicy.AutoRename, null, 4, null, CancellationToken.None);
                var files = Directory.GetFiles(twiceOut);
                Check("names that differ only in capitals give one file and a complaint",
                    files.Length == 1 && both.Failed == 1 && File.ReadAllText(files[0]) == "first",
                    $"{files.Length} files, {both.Failed} failed");

                // A name close to the limit, which the temporary name used to push past it.
                var longName = new string('n', 236) + ".txt";
                var longZip = MakeZip(Path.Combine(dir, "long.zip"), (longName, "long"));
                var longOut = Path.Combine(dir, "l");
                var longResult = await ArchiveEngine.ExtractAsync(longZip, longOut,
                    PasteConflictPolicy.AutoRename, null, 2, null, CancellationToken.None);
                Check("a 240-character name extracts",
                    longResult.Failed == 0 && File.Exists(Path.Combine(longOut, longName)),
                    string.Join("; ", longResult.Errors));

                // Replace means replace, a read-only file included.
                var replaceOut = Path.Combine(dir, "replace");
                Directory.CreateDirectory(replaceOut);
                var old = Path.Combine(replaceOut, "x.txt");
                File.WriteAllText(old, "old");
                File.SetAttributes(old, FileAttributes.ReadOnly);
                var fresh = MakeZip(Path.Combine(dir, "fresh.zip"), ("x.txt", "new"));
                var replaced = await ArchiveEngine.ExtractAsync(fresh, replaceOut,
                    PasteConflictPolicy.Overwrite, null, 2, null, CancellationToken.None);
                Equal("replacing a read-only file replaces it", "new", File.ReadAllText(old));
                Equal("without a failure", "0", replaced.Failed.ToString());
            }
            finally { Cleanup(dir); }
        }

        private static async Task ArchiveWritingTests()
        {
            var dir = NewDir();
            try
            {
                // An empty file is still a gzip file.
                var empty = Path.Combine(dir, "empty.txt");
                File.WriteAllBytes(empty, Array.Empty<byte>());
                var gz = Path.Combine(dir, "empty.txt.gz");
                await ArchiveEngine.CompressAsync(new[] { empty }, gz, ArchiveFormats.Gz,
                    ArchiveLevel.Balanced, 2, null, CancellationToken.None);
                bool readable;
                try
                {
                    using var input = new GZipStream(File.OpenRead(gz), CompressionMode.Decompress);
                    readable = input.ReadByte() == -1 && new FileInfo(gz).Length > 0;
                }
                catch { readable = false; }
                Check("an empty file compresses to a gzip file that opens", readable,
                    new FileInfo(gz).Length + " bytes");

                // A file dated before 1970 is still put in a tar.
                var old = Path.Combine(dir, "old.txt");
                File.WriteAllText(old, "from a camera with a dead clock");
                File.SetLastWriteTime(old, new DateTime(1960, 1, 1));
                var tgz = Path.Combine(dir, "old.tar.gz");
                var made = await ArchiveEngine.CompressAsync(new[] { old }, tgz, ArchiveFormats.TarGz,
                    ArchiveLevel.Balanced, 2, null, CancellationToken.None);
                Check("a file older than 1970 goes into a tar",
                    made.Failed == 0 && TarEngine.List(tgz, ArchiveFormats.TarGz, CancellationToken.None).Count == 1,
                    string.Join("; ", made.Errors));

                if (!BsdTar.HandlesNames) return;

                // Replacing a .txz, which the staging name used to hide from bsdtar.
                var txz = Path.Combine(dir, "again.txz");
                await ArchiveEngine.CompressAsync(new[] { old }, txz, ArchiveFormats.TarXz,
                    ArchiveLevel.Fastest, 2, null, CancellationToken.None);
                var second = await ArchiveEngine.CompressAsync(new[] { old }, txz, ArchiveFormats.TarXz,
                    ArchiveLevel.Fastest, 2, null, CancellationToken.None);
                Check("an existing .txz can be replaced", second.Failed == 0 && File.Exists(txz),
                    string.Join("; ", second.Errors));

                // Two chosen items with one name cannot both go into a .tar.xz.
                var x1 = Path.Combine(dir, "one");
                var x2 = Path.Combine(dir, "two");
                Directory.CreateDirectory(x1);
                Directory.CreateDirectory(x2);
                File.WriteAllText(Path.Combine(x1, "x.txt"), "1");
                File.WriteAllText(Path.Combine(x2, "x.txt"), "2");
                string? said = null;
                try
                {
                    await ArchiveEngine.CompressAsync(new[] { Path.Combine(x1, "x.txt"), Path.Combine(x2, "x.txt") },
                        Path.Combine(dir, "clash.tar.xz"), ArchiveFormats.TarXz, ArchiveLevel.Fastest, 2, null,
                        CancellationToken.None);
                }
                catch (NotSupportedException ex) { said = ex.Message; }
                Check("two items with one name are refused for a .tar.xz, with the way round it",
                    said != null && said.Contains("x.txt") && said.Contains("Zip"), said);
            }
            finally { Cleanup(dir); }
        }
    }
}
