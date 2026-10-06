using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Preferences and its shortcut windows: access keys that pressed the wrong
    /// button, Shift-only global shortcuts, OK changing values nobody touched,
    /// startup rewriting the pairing code and the port, and the Google check
    /// acting before OK had finished.
    /// </summary>
    internal static class SettingsFixTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Preferences fixes:");

            var previous = Settings.OverrideAppDataDir;
            try
            {
                AccessKeyTests();
                ShiftOnlyTests();
                UntouchedOkTests();
                UntouchedDialogTests();
                LockedFileKeepsPairingCodeTests();
                BusyPortIsNotReplacedTests();
                GoogleCheckWaitsForOkTests();
                GeneratedReaderTests();
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
            }

            Console.WriteLine();
        }

        /// <summary>
        /// settings.json is read and written through metadata generated at build
        /// time (<see cref="SettingsJson"/>) instead of reflection, because
        /// reflection was the slowest thing in front of the window. The file has
        /// to come out exactly as reflection wrote it and read back exactly as
        /// reflection read it, or an update would quietly change somebody's
        /// settings. Every persisted property is moved off its default first,
        /// because a comparison of two default objects proves nothing.
        /// </summary>
        private static void GeneratedReaderTests()
        {
            var reflectionWrite = new JsonSerializerOptions(Settings.JsonFormat)
            {
                TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            };
            var reflectionRead = new JsonSerializerOptions(Settings.ReadFormat)
            {
                TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            };

            Check("the generated context covers Settings",
                SettingsJson.Default.GetTypeInfo(typeof(Settings)) != null);

            var odd = new Settings();
            int moved = 0;
            foreach (var property in typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite) continue;
                if (property.GetIndexParameters().Length > 0) continue;
                if (Attribute.IsDefined(property, typeof(System.Text.Json.Serialization.JsonIgnoreAttribute))) continue;

                var type = property.PropertyType;
                object? value =
                    type == typeof(bool) ? !(bool)property.GetValue(odd)! :
                    type == typeof(int) ? (int)property.GetValue(odd)! + 7 :
                    type == typeof(long) ? (long)property.GetValue(odd)! + 7 :
                    type == typeof(uint) ? (uint)property.GetValue(odd)! + 7 :
                    type == typeof(string) ? "emoji 🎵, 日本語, \"quotes\" and \\back\\slashes\\ " + property.Name :
                    type == typeof(int[]) ? new[] { 3, -2, 0, 24 } :
                    type.IsEnum ? Enum.GetValues(type).Cast<object>().Last() :
                    type == typeof(List<DriveSyncPair>) ? new List<DriveSyncPair>
                    {
                        new()
                        {
                            Name = "Music é", LocalFolder = @"D:\Music", DriveFolderId = "abc123",
                            DriveFolderPath = "My Drive/Music", Mode = SyncMode.UploadOnly, CopyDeletes = true,
                            Paused = true, IncludeSubfolders = false, SkipExtensions = ".tmp;.part",
                            MaxFileMegabytes = 12, CheckMinutes = 9,
                        },
                    } :
                    type == typeof(Dictionary<string, JsonElement>) ? new Dictionary<string, JsonElement>
                    {
                        ["FromANewerBuild"] = JsonDocument.Parse("{\"a\":[1,2,{\"b\":null}]}").RootElement.Clone(),
                    } :
                    null;
                if (value == null)
                {
                    Check($"the test knows how to move {property.Name} ({type.Name})", false);
                    continue;
                }
                property.SetValue(odd, value);
                moved++;
            }
            Check("every persisted setting was moved off its default", moved > 100, moved.ToString());

            var generated = JsonSerializer.Serialize(odd, Settings.JsonFormat);
            var reflected = JsonSerializer.Serialize(odd, reflectionWrite);
            Check("the file is written exactly as reflection wrote it", generated == reflected,
                generated.Length + " against " + reflected.Length + " characters");

            var readGenerated = JsonSerializer.Deserialize<Settings>(generated, Settings.ReadFormat)!;
            var readReflected = JsonSerializer.Deserialize<Settings>(generated, reflectionRead)!;
            Check("and reads back exactly as reflection read it",
                JsonSerializer.Serialize(readGenerated, reflectionWrite) == JsonSerializer.Serialize(readReflected, reflectionWrite));
            Check("which is what was written",
                JsonSerializer.Serialize(readGenerated, reflectionWrite) == reflected);

            // What people leave in a file they edit by hand reads the same way.
            var edited = "{\n  // a comment\n  \"FontSize\": 13,\n  \"WindowTitle\": \"Mine\",\n}";
            var a = JsonSerializer.Deserialize<Settings>(edited, Settings.ReadFormat)!;
            var b = JsonSerializer.Deserialize<Settings>(edited, reflectionRead)!;
            Check("comments and a trailing comma read the same",
                a.FontSize == 13 && a.WindowTitle == "Mine" &&
                JsonSerializer.Serialize(a, reflectionWrite) == JsonSerializer.Serialize(b, reflectionWrite));

            // And a value of the wrong type is refused the same way, which is what
            // sends Load to read the file a property at a time.
            bool generatedThrew = false, reflectionThrew = false;
            try { JsonSerializer.Deserialize<Settings>("{\"FontSize\":\"big\"}", Settings.ReadFormat); }
            catch (JsonException) { generatedThrew = true; }
            try { JsonSerializer.Deserialize<Settings>("{\"FontSize\":\"big\"}", reflectionRead); }
            catch (JsonException) { reflectionThrew = true; }
            Check("a value of the wrong type is refused by both", generatedThrew && reflectionThrew);

            // Through the real file, encryption and all.
            var dir = NewSettingsDir();
            try
            {
                odd.Save();
                var loaded = Settings.Load(out bool failed);
                Check("the real file round-trips through the generated reader", !failed &&
                    loaded.WindowTitle == odd.WindowTitle.Trim() && loaded.DriveSyncPairs.Count == 1 &&
                    loaded.DriveSyncPairs[0].Name == "Music é" && loaded.DriveSyncPairs[0].Mode == SyncMode.UploadOnly);
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// Where these tests keep settings: never the real settings.json.
        /// </summary>
        private static string NewSettingsDir()
        {
            const string scratch =
                @"C:\Users\Conner\AppData\Local\Temp\claude\C--Users-Conner\210da054-69e3-4f58-81af-73a744528057\scratchpad";
            var root = Directory.Exists(scratch)
                ? Path.Combine(scratch, "fix-settings")
                : Path.Combine(Path.GetTempPath(), "ExplorerNativeTests_settings");
            var dir = Path.Combine(root, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            Settings.OverrideAppDataDir = dir;
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        /// <summary>Runs on an STA thread of its own, and says what it threw.</summary>
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

        /// <summary>The access key a control answers to, or null.</summary>
        private static char? AccessKey(Control control)
        {
            bool mnemonic = control is ButtonBase b ? b.UseMnemonic : control is Label l && l.UseMnemonic;
            if (!mnemonic) return null;
            var text = control.Text ?? "";
            for (int i = 0; i < text.Length - 1; i++)
            {
                if (text[i] != '&') continue;
                if (text[i + 1] == '&') { i++; continue; }
                return char.ToUpperInvariant(text[i + 1]);
            }
            return null;
        }

        /// <summary>Every access key used more than once among these controls, said as a sentence.</summary>
        private static List<string> Clashes(IEnumerable<Control> controls)
        {
            var byKey = new Dictionary<char, List<string>>();
            foreach (var c in controls)
            {
                if (AccessKey(c) is not { } key) continue;
                if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<string>();
                list.Add(c.Text.Replace("&", ""));
            }
            return byKey.Where(p => p.Value.Count > 1)
                .Select(p => $"Alt+{p.Key}: {string.Join(" and ", p.Value)}").ToList();
        }

        /// <summary>
        /// No page in Preferences shares an access key with another control on
        /// it or with OK, Cancel and Reset, in any state of the account button
        /// and with the web app on or off. Every control on a page counts,
        /// shown or not, so the web app's buttons that come and go are covered
        /// in every combination at once.
        /// </summary>
        private static void AccessKeyTests()
        {
            var hook = SettingsForm.AccountState;
            var dir = NewSettingsDir();
            try
            {
                foreach (var state in new[] { DriveAccountState.NotConnected, DriveAccountState.Connected, DriveAccountState.NeedsSignIn })
                foreach (var webOn in new[] { false, true })
                {
                    SettingsForm.AccountState = () => state;
                    var problems = new List<string>();
                    var dialogKeys = new List<char>();
                    int pages = 0;
                    string accountButton = "";
                    var error = OnSta(() =>
                    {
                        using var window = new SettingsForm(new Settings { WebAppEnabled = webOn, SpeakEnabled = false }, null);
                        window.BuildAllPages();
                        pages = window.Pages.Count;

                        var all = FindAll<Control>(window);
                        var outside = all.Where(c => !window.Pages.Any(p => p == c || p.Contains(c))).ToList();
                        dialogKeys.AddRange(outside.Select(AccessKey).Where(k => k != null).Select(k => k!.Value));
                        problems.AddRange(Clashes(outside).Select(c => "the dialog's own buttons: " + c));

                        foreach (var page in window.Pages)
                            problems.AddRange(Clashes(outside.Concat(FindAll<Control>(page)))
                                .Select(c => (page.AccessibleName ?? "a page") + ": " + c));

                        accountButton = FindAll<Button>(window)
                            .Select(b => b.Text).FirstOrDefault(t => t.Contains("Google Drive", StringComparison.Ordinal)) ?? "";
                    });

                    string name = $"Drive {state}, web app {(webOn ? "on" : "off")}";
                    Equal($"Preferences builds ({name})", "", error);
                    Check($"every page was looked at ({name}, {pages})", pages >= 9, pages.ToString());
                    Check($"OK, Cancel and Reset keep O, C and R ({name})",
                        dialogKeys.OrderBy(k => k).SequenceEqual(new[] { 'C', 'O', 'R' }),
                        string.Join(",", dialogKeys));
                    Equal($"no access key is shared ({name})", "", string.Join("; ", problems));
                    Check($"the account button uses neither C nor R ({name}: {accountButton})",
                        !accountButton.Contains("&C") && !accountButton.Contains("&R") && accountButton.Contains('&'));
                }

                // The two windows Preferences opens from its pages.
                var more = new List<string>();
                var moreError = OnSta(() =>
                {
                    using var speech = new SpeechForm(new Settings());
                    more.AddRange(Clashes(FindAll<Control>(speech)).Select(c => "What is spoken: " + c));
                    using var keys = new ShortcutsForm(new Settings());
                    more.AddRange(Clashes(FindAll<Control>(keys)).Select(c => "Audio keyboard shortcuts: " + c));
                });
                Equal("the speech and shortcut windows build", "", moreError);
                Equal("and share no access keys either", "", string.Join("; ", more));
            }
            finally
            {
                SettingsForm.AccountState = hook;
                Cleanup(dir);
            }
        }

        /// <summary>
        /// Shift is not enough to make a global shortcut: Shift and a letter is
        /// a capital, Shift and an arrow is selecting text. Only a function key
        /// or a media key may go with Shift alone.
        /// </summary>
        private static void ShiftOnlyTests()
        {
            foreach (var text in new[] { "Shift+P", "Shift+1", "Shift+Space", "Shift+Left", "Shift+Right", "Shift+Enter", "Shift+Back" })
            {
                var s = Shortcut.Parse(text);
                Check($"{text} is read", s.IsAssigned, text);
                Check($"but refused as a global shortcut", !s.CanRegister, text);
            }

            foreach (var text in new[] { "Shift+F6", "Shift+MediaPlayPause", "Ctrl+Shift+P", "Alt+Shift+1", "Win+Shift+P", "Ctrl+Alt+M" })
                Check($"{text} can still be a global shortcut", Shortcut.Parse(text).CanRegister, text);

            // A stray bit from a hand-edited file is not a modifier.
            Check("a modifier Windows has no name for does not count",
                !new Shortcut(0x4000, (uint)Keys.P).CanRegister && !new Shortcut(0x4004, (uint)Keys.P).CanRegister);

            // The box in the shortcut window: pressed, refused, and the box
            // still empty. Then an obscure combination that is taken. Nothing
            // is registered with Windows here; the box only records keys.
            string refusedValue = "", acceptedValue = "", said = "";
            bool refusedChanged = true, acceptedChanged = false;
            var dir = NewSettingsDir();
            try
            {
                var error = OnSta(() =>
                {
                    using var form = new ShortcutsForm(new Settings { SpeakEnabled = false });
                    var box = FindAll<ShortcutBox>(form).First();
                    box.Captured += m => said = m;
                    var press = typeof(ShortcutBox).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!;

                    press.Invoke(box, new object[] { new Message(), Keys.Shift | Keys.P });
                    refusedValue = box.Value.ToString();
                    refusedChanged = box.Changed;

                    press.Invoke(box, new object[] { new Message(), Keys.Control | Keys.Alt | Keys.Shift | Keys.F11 });
                    acceptedValue = box.Value.ToString();
                    acceptedChanged = box.Changed;
                });
                Equal("the shortcut window builds", "", error);
                Equal("Shift+P is refused by the box", "", refusedValue);
                Check("and the box is not counted as changed", !refusedChanged);
                Equal("Ctrl+Alt+Shift+F11 is taken", "Ctrl+Alt+Shift+F11", acceptedValue);
                Check("and the box is counted as changed", acceptedChanged, said);

                // The Hotkey page: Shift and P ticked and chosen, then OK.
                uint mods = 99, key = 99;
                bool accepted = false;
                error = OnSta(() =>
                {
                    using var window = new SettingsForm(new Settings { SpeakEnabled = false, GlobalHotkeyEnabled = true }, null);
                    window.BuildAllPages();
                    var shift = FindAll<CheckBox>(window).First(c => c.Text == "Shift");
                    var keyBox = FindAll<ComboBox>(window).First(c => c.AccessibleName == "Hotkey letter or key");
                    shift.Checked = true;
                    keyBox.SelectedIndex = keyBox.Items.IndexOf("P");
                    accepted = window.AcceptAsync().GetAwaiter().GetResult();
                    mods = window.Result.HotkeyModifiers;
                    key = window.Result.HotkeyKey;
                });
                Equal("Preferences builds for the hotkey check", "", error);
                Check("OK still closes", accepted);
                Equal("Shift+P is not made the window shortcut", "0 0", $"{mods} {key}");
            }
            finally { Cleanup(dir); }
        }

        /// <summary>Every property whose value differs, by name.</summary>
        private static List<string> Differences(Settings a, Settings b)
        {
            var differ = new List<string>();
            foreach (var p in typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                var x = JsonSerializer.Serialize(p.GetValue(a));
                var y = JsonSerializer.Serialize(p.GetValue(b));
                if (x != y) differ.Add($"{p.Name} {x} became {y}");
            }
            return differ;
        }

        private static Settings Unusual(string letter, int readAhead, SortColumn sort) => new()
        {
            SpeakEnabled = false,
            AudioPrefetchKilobytes = readAhead,
            GoogleDriveLetter = letter,
            SortBy = sort,
            Delete = (DeleteMode)7,
            PasteConflict = (PasteConflictPolicy)42,
            SizeUnits = (SizeUnitStyle)17,
            FolderSizes = (FolderSizeMode)0,
            HotkeyModifiers = 0x4003,
            HotkeyKey = (uint)Keys.D7,
            GlobalHotkeyEnabled = true,
            AudioPlayPauseShortcut = "ctrl+alt+m",
            AudioStopShortcut = "Ctrl+Wibble",
            SpeechOverrides = "copy.done=0;from.a.newer.build=1",
            FontSize = 30,
            TypeAheadMilliseconds = 1234,
            AudioReleaseAfterMinutes = 37,
            WebAppPort = 51234,
            WindowTitle = "Something Else",
        };

        /// <summary>
        /// OK with nothing touched leaves every setting exactly as it was, for a
        /// settings object full of values no list offers: read-ahead sizes from
        /// old builds, drive letters in odd forms, enum numbers out of range,
        /// stray modifier bits, a lowercase shortcut and speech ids from a newer
        /// build. Every page is built first, as the window does in the
        /// background shortly after opening.
        /// </summary>
        private static void UntouchedOkTests()
        {
            var dir = NewSettingsDir();
            try
            {
                var cases = new List<(string Name, Settings Settings)>
                {
                    ("read-ahead 1536, drive letter q", Unusual("q", 1536, (SortColumn)9)),
                    ("read-ahead 512, drive letter G:", Unusual("G:", 512, SortColumn.Type)),
                    ("read-ahead 3000, drive letter rubbish", Unusual("rubbish", 3000, SortColumn.Size)),
                    ("an empty drive letter", Unusual("", 1536, SortColumn.Name)),
                    ("drive letter C", Unusual("C", 2048, SortColumn.Modified)),
                };

                foreach (var (name, settings) in cases)
                {
                    var before = settings.Clone();
                    Settings? after = null;
                    bool accepted = false;
                    var error = OnSta(() =>
                    {
                        using var window = new SettingsForm(settings, null);
                        window.BuildAllPages();
                        accepted = window.AcceptAsync().GetAwaiter().GetResult();
                        after = window.Result;
                    });

                    Equal($"Preferences builds and accepts ({name})", "", error);
                    Check($"OK goes through ({name})", accepted);
                    if (after != null)
                        Equal($"OK on untouched pages changes nothing ({name})", "",
                            string.Join("; ", Differences(before, after)));
                }

                // And a list that is touched still writes what was picked.
                Settings? picked = null;
                var pickError = OnSta(() =>
                {
                    using var window = new SettingsForm(Unusual("q", 1536, SortColumn.Type), null);
                    window.BuildAllPages();
                    var sort = FindAll<ComboBox>(window).First(c => c.AccessibleName == "Sort by");
                    var read = FindAll<ComboBox>(window).First(c => c.AccessibleName == "Read ahead from the selected file");
                    sort.SelectedIndex = sort.Items.IndexOf("Modified");
                    read.SelectedIndex = read.Items.IndexOf("4 megabytes");
                    window.AcceptAsync().GetAwaiter().GetResult();
                    picked = window.Result;
                });
                Equal("Preferences builds for a change", "", pickError);
                Check("a picked sort order is written", picked?.SortBy == SortColumn.Modified, picked?.SortBy.ToString());
                Check("and a picked read-ahead size", picked?.AudioPrefetchKilobytes == 4096, picked?.AudioPrefetchKilobytes.ToString());

                // Sort by no longer offers Type, and a saved Type shows as Name.
                var sortItems = new List<string>();
                string sortShown = "", readShown = "", letterShown = "";
                var listError = OnSta(() =>
                {
                    using var window = new SettingsForm(Unusual("rubbish", 1536, SortColumn.Type), null);
                    window.BuildAllPages();
                    var sort = FindAll<ComboBox>(window).First(c => c.AccessibleName == "Sort by");
                    foreach (var item in sort.Items) sortItems.Add(item?.ToString() ?? "");
                    sortShown = sort.Text;
                    readShown = FindAll<ComboBox>(window).First(c => c.AccessibleName == "Read ahead from the selected file").Text;
                    letterShown = FindAll<ComboBox>(window).First(c => c.AccessibleName == "Google Drive letter").Text;
                });
                Equal("Preferences builds for the lists", "", listError);
                Equal("Sort by offers Name, Size and Modified", "Name|Size|Modified", string.Join("|", sortItems));
                Equal("a saved Type shows as Name", "Name", sortShown);
                Equal("1536 kilobytes is shown as it is", "1.5 megabytes (custom)", readShown);
                Equal("and a drive letter that is not one", "rubbish (custom)", letterShown);

                // The Audio page counts the actions rather than saying eighteen.
                var source = SourceFileText("SettingsForm.cs");
                Check("the Audio page counts its actions",
                    !source.Contains("Eighteen actions") && source.Contains("{AudioActions.All.Count} actions"));
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// OK in the speech and shortcut windows writes only what was changed:
        /// an id from a newer build survives, and so do a lowercase shortcut and
        /// one this build cannot read.
        /// </summary>
        private static void UntouchedDialogTests()
        {
            var dir = NewSettingsDir();
            try
            {
                var settings = Unusual("G", 2048, SortColumn.Name);
                string untouched = "", changed = "", play = "", stop = "";
                var error = OnSta(() =>
                {
                    using (var speech = new SpeechForm(settings))
                    {
                        speech.Apply();
                        untouched = settings.SpeechOverrides;

                        var list = FindAll<CheckedListBox>(speech).First();
                        list.SetItemChecked(0, !list.GetItemChecked(0));
                        speech.Apply();
                        changed = settings.SpeechOverrides;
                    }

                    using var keys = new ShortcutsForm(settings);
                    keys.Apply();
                    play = settings.AudioPlayPauseShortcut;
                    stop = settings.AudioStopShortcut;
                });

                Equal("the windows build", "", error);
                Equal("speech OK with nothing changed leaves the text alone",
                    "copy.done=0;from.a.newer.build=1", untouched);
                Check("a change keeps the id a newer build wrote", changed.Contains("from.a.newer.build=1"), changed);
                Check("and records the change", changed != untouched, changed);
                Equal("shortcuts OK keeps a lowercase shortcut as written", "ctrl+alt+m", play);
                Equal("and one this build cannot read", "Ctrl+Wibble", stop);
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// settings.json held by another program at startup: no pairing code is
        /// made over the defaults, and once the file opens its own code is the
        /// one kept.
        /// </summary>
        private static void LockedFileKeepsPairingCodeTests()
        {
            var dir = NewSettingsDir();
            try
            {
                new Settings { ConnectCode = "11112222" }.Save();
                var path = Path.Combine(dir, "settings.json");

                Settings live;
                bool failed;
                bool made;
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    live = Settings.Load(out failed);
                    made = live.EnsureConnectCode();
                }

                Check("a held settings file is unreadable at startup", failed && live.UnreadableOnDisk);
                Check("and no pairing code is made over the defaults", !made && live.ConnectCode == "", live.ConnectCode);

                // What RetrySettings does once the file opens.
                var fresh = Settings.Load();
                var merged = Settings.WithChanges(fresh, new Settings(), live);
                merged.EnsureConnectCode();
                Equal("the file's own pairing code is kept", "11112222", merged.ConnectCode);

                // A file with no code at all does get one, once it can be read.
                new Settings { ConnectCode = "" }.Save();
                var empty = Settings.Load();
                Check("a readable file with no code gets one",
                    empty.EnsureConnectCode() && empty.ConnectCode.Length == 8, empty.ConnectCode);

                var tray = SourceFileText("TrayApplicationContext.cs");
                Check("startup asks for a code only through EnsureConnectCode",
                    tray.Contains("if (_settings.EnsureConnectCode()) _settings.Save();") &&
                    !tray.Contains("_settings.ConnectCode = ConnectServer.NewCode()"));
                Check("and the retry keeps the file's code", tray.Contains("merged.EnsureConnectCode();"));
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// A custom port another program holds is not replaced by 47810: not by
        /// startup, which falls back for the session only, and not by OK in
        /// Preferences, which leaves an untouched port alone.
        /// </summary>
        private static void BusyPortIsNotReplacedTests()
        {
            var dir = NewSettingsDir();
            var hook = SettingsForm.CurrentWebPort;
            var holder = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            try
            {
                holder.Start();
                int busy = ((System.Net.IPEndPoint)holder.LocalEndpoint).Port;
                SettingsForm.CurrentWebPort = () => ConnectServer.Port;

                int saved = 0;
                bool accepted = false;
                var error = OnSta(() =>
                {
                    using var window = new SettingsForm(new Settings { WebAppPort = busy, SpeakEnabled = false }, null);
                    window.BuildAllPages();
                    accepted = window.AcceptAsync().GetAwaiter().GetResult();
                    saved = window.Result.WebAppPort;
                }, seconds: 30);

                Equal("Preferences builds with a busy port", "", error);
                Check("OK is not refused over a port nobody touched", accepted);
                Equal("and the busy port is still the setting", busy.ToString(), saved.ToString());

                var tray = SourceFileText("TrayApplicationContext.cs");
                int from = tray.IndexOf("A saved port another program has taken since", StringComparison.Ordinal);
                int to = from < 0 ? -1 : tray.IndexOf("ReconcileWebApp(_connect.ListenPort);", from, StringComparison.Ordinal);
                var fallback = from >= 0 && to > from ? tray[from..to] : "";
                Check("startup's fallback is found", fallback.Length > 0);
                Check("and it does not save 47810 over the setting",
                    !fallback.Contains("_settings.WebAppPort = ConnectServer.Port") && !fallback.Contains("Save()"), fallback);
                Check("and OK only rebinds a port setting that changed", tray.Contains("portSettingChanged &&"));
            }
            finally
            {
                try { holder.Stop(); } catch { }
                SettingsForm.CurrentWebPort = hook;
                Cleanup(dir);
            }
        }

        /// <summary>
        /// The Google client is saved when OK finishes, not when Google says yes:
        /// a later check that refuses, followed by Cancel, must leave no client
        /// saved. Google is a fake here.
        /// </summary>
        private static void GoogleCheckWaitsForOkTests()
        {
            var dir = NewSettingsDir();
            var real = SettingsForm.CheckClient;
            int asked = 0;
            SettingsForm.CheckClient = (_, _, _) => { asked++; return Task.FromResult<string?>(null); };
            try
            {
                bool first = true, second = false, driveOn = false;
                (string, string)? afterRefusal = null, afterOk = null;
                var error = OnSta(() =>
                {
                    using (var window = new SettingsForm(new Settings { SpeakEnabled = false }, null))
                    {
                        window.BuildAllPages();
                        FindAll<TextBox>(window).First(t => t.AccessibleName == "Google client ID").Text = "id.apps.example";
                        FindAll<TextBox>(window).First(t => t.AccessibleName == "Google client secret").Text = "secret";
                        window.AddCheckForTests(() => Task.FromResult(false));
                        first = window.AcceptAsync().GetAwaiter().GetResult();
                        afterRefusal = GoogleAuth.Credentials(dir);
                    }

                    using (var window = new SettingsForm(new Settings { SpeakEnabled = false }, null))
                    {
                        window.BuildAllPages();
                        FindAll<TextBox>(window).First(t => t.AccessibleName == "Google client ID").Text = "id.apps.example";
                        FindAll<TextBox>(window).First(t => t.AccessibleName == "Google client secret").Text = "secret";
                        second = window.AcceptAsync().GetAwaiter().GetResult();
                        afterOk = GoogleAuth.Credentials(dir);
                        driveOn = window.Result.GoogleDriveEnabled;
                    }
                }, seconds: 30);

                Equal("Preferences builds for the Google check", "", error);
                Check("Google was asked both times", asked == 2, asked.ToString());
                Check("a later check refusing keeps the dialog open", !first);
                Check("and nothing was saved before it refused", afterRefusal == null);
                Check("with every check passed OK goes through", second);
                Check("and the client is saved then", afterOk == ("id.apps.example", "secret"), afterOk?.ToString());
                Check("and Drive is switched on", driveOn);
            }
            finally
            {
                SettingsForm.CheckClient = real;
                Cleanup(dir);
            }
        }

        private static string SourceFileText(string name)
        {
            var at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at != null && !Directory.Exists(Path.Combine(at.FullName, "src"))) at = at.Parent;
            if (at == null) return "";
            var path = Path.Combine(at.FullName, "src", name);
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch { return ""; }
        }
    }
}
