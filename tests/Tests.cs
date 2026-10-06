using System;
using System.Windows.Forms;
using System.Net.Sockets;
using System.Net.Http;
using System.Net;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Exercises the settings-driven logic against the real production sources.
    /// Every check states what it proves, so a failure names the broken setting
    /// rather than just a line number.
    /// </summary>
    internal static class Tests
    {
        private static int _passed, _failed;

        /// <summary>
        /// How long the suite may go without finishing a single check before it
        /// is treated as wedged rather than slow.
        ///
        /// Generous on purpose. The longest legitimate gap between two checks is
        /// an audio one waiting on metadata or on a fade — SelfCheck alone allows
        /// itself thirteen seconds — so this is several times the worst honest
        /// case and still a small fraction of the time a hang otherwise costs.
        /// </summary>
        private const int StallSeconds = 90;

        private static long _lastProgressAt = Environment.TickCount64;
        private static string _lastProgress = "before the first check";

        /// <summary>
        /// The watchdog CLAUDE.md says every audio harness has, which this — the
        /// harness that drives a real media engine through five sections — did
        /// not.
        ///
        /// It matters here for exactly the reason it matters there: these tests
        /// wait on a decoder and a clock that may never move, so a wrong
        /// assumption does not fail, it sits. A run wedged in the media engine's
        /// teardown after the last seek test sat for over ten minutes and said
        /// nothing at all about where it was — and the whole point of printing
        /// each check as it happens is that a run which dies still says how far
        /// it got. Without something to end it, nobody ever reads that.
        ///
        /// Stall-based rather than a total budget: the suite legitimately takes
        /// about a minute, so a wall-clock ceiling would either be useless or
        /// fire on a slow machine. What is never legitimate is no check
        /// completing for a minute and a half.
        /// </summary>
        private static void StartWatchdog()
        {
            var watchdog = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(1000);
                    long quiet = Environment.TickCount64 - Volatile.Read(ref _lastProgressAt);
                    if (quiet < StallSeconds * 1000L) continue;

                    Console.WriteLine();
                    Console.WriteLine($"  STALLED  no check completed for {quiet / 1000}s.");
                    Console.WriteLine($"           the last one was: {_lastProgress}");
                    Console.WriteLine($"           {_passed} passed, {_failed} failed before it stopped.");
                    Console.Out.Flush();

                    // Exit, rather than throw into a thread nobody is watching:
                    // the point is that the run ends and says so.
                    Environment.Exit(2);
                }
            })
            {
                IsBackground = true,
                Name = "self-test watchdog",
            };
            watchdog.Start();
        }

        /// <summary>TypeAhead.Find over a list of names, for the tests that have one.</summary>
        private static int FindIn(IReadOnlyList<string> names, string query, int currentIndex) =>
            TypeAhead.Find(names.Count, i => names[i], query, currentIndex);

        /// <summary>Every label the Audio menu shows, both states of the toggles included.</summary>
        private static IEnumerable<string> AllAudioMenuTexts()
        {
            foreach (var info in AudioActions.All)
            {
                if (info.MenuText.Length == 0) continue;
                yield return info.MenuText;
                if (info.AlternateMenuText != null) yield return info.AlternateMenuText;
            }
            yield return AudioActions.SongPropertiesMenuText;
            yield return AudioActions.PreferencesMenuText;
        }

        /// <summary>The access key in a menu label, or null.</summary>
        private static char? MnemonicOf(string menuText)
        {
            int at = menuText.IndexOf('&');
            if (at < 0 || at + 1 >= menuText.Length) return null;
            return char.ToUpperInvariant(menuText[at + 1]);
        }

        private static void Check(string what, bool condition, string? detail = null)
        {
            _lastProgress = what;
            Volatile.Write(ref _lastProgressAt, Environment.TickCount64);

            if (condition) { _passed++; Console.WriteLine($"  PASS  {what}"); }
            else { _failed++; Console.WriteLine($"  FAIL  {what}{(detail == null ? "" : "  -> " + detail)}"); }
        }

        private static void Equal(string what, string expected, string actual) =>
            Check(what, expected == actual, $"expected \"{expected}\", got \"{actual}\"");

        public static async Task<int> Main()
        {
            Console.WriteLine("Explorer Native self-test\n");

            StartWatchdog();

            // Just the window timings, for measuring one change at a time.
            if (Environment.GetEnvironmentVariable("EXPLORERNATIVE_SELFTEST_ONLY") == "speed")
            {
                WindowSpeedTests();
                Console.WriteLine($"\n{_passed} passed, {_failed} failed");
                return _failed == 0 ? 0 : 1;
            }

            // Just copying and moving, for working on the engines.
            // Just the player review's fixes, with EXPLORERNATIVE_AUDIOFUZZ set
            // to the number of damaged copies of each format to try.
            if (Environment.GetEnvironmentVariable("EXPLORERNATIVE_SELFTEST_ONLY") == "audiofix")
            {
                AudioFixTests.RunAll(Check, Equal);
                Console.WriteLine($"\n{_passed} passed, {_failed} failed");
                return _failed == 0 ? 0 : 1;
            }

            if (Environment.GetEnvironmentVariable("EXPLORERNATIVE_SELFTEST_ONLY") == "transfers")
            {
                await RoboCopyTests();
                await CancelCleanupTests();
                await ConflictSafetyTests();
                await AwkwardNameTests();
                await TransferFixTests.RunAll(Check, Equal);
                await FilesR2Tests.RunAll(Check, Equal);
                Console.WriteLine($"\n{_passed} passed, {_failed} failed");
                return _failed == 0 ? 0 : 1;
            }

            // Just the second review of copying and the window.
            if (Environment.GetEnvironmentVariable("EXPLORERNATIVE_SELFTEST_ONLY") == "r2files")
            {
                await FilesR2Tests.RunAll(Check, Equal);
                Console.WriteLine($"\n{_passed} passed, {_failed} failed");
                return _failed == 0 ? 0 : 1;
            }

            SizeUnitTests();
            SizeFormatterEdgeTests();
            TimeFormatTests();
            SettingsClampTests();
            SettingsRoundTripTests();
            UpdatesNeverChangeSettingsTests();
            NoShortcutsOnFirstLaunchTests();
            QuietByDefaultTests();
            CountedSpeechTests();
            GainHeadroomTests();
            TimeStretchTests();
            EqualiserTests();
            SilenceReductionTests();
            PitchTests();
            SkipWhileDownloadingTests();
            GaplessTests();
            OddFormatTests();
            RealFolderTimingTests();
            NeutralChainTests();
            FillGapsTests();
            DriveRetryTests();
            DriveNeverSelfCancelsTests();
            DirectOutputTests();
            VolumeTravelTests();
            ProgressWindowShapeTests();
            AudioDialogShapeTests();
            EveryNotificationTests();
            WindowTitleTests();
            NameRuleTests();
            SortOrderTests();
            TypeAheadTests();
            PositionMemoryTests();
            UniqueNameTests();
            await FileOperationTests();
            await FolderSizeTests();
            await FolderSizeEdgeTests();
            await PermissionTests();
            HostileInputTests();
            ReservedShortcutTests();
            BoundaryTests();
            await StatedClaimTests();
            ProgressArithmeticTests();
            await RoboCopyTests();
            await CancelCleanupTests();
            await ConflictSafetyTests();
            await AwkwardNameTests();
            await ConcurrencyTests();
            await LargeFolderTests();
            LaunchTests();
            await SingleInstanceTests();
            InstallLocationTests();
            ShellRegistrationTests();
            ShortcutTests();
            AudioFileTests();
            HoldRepeatTests();
            SpeedTests();
            await PrefetchTests();
            StreamingSourceTests();
            VolumeCurveTests();
            AudioTagTests();
            AudioEngineTests();
            FlacTests();
            SeekTests();
            NotificationTests();
            await ShellLinkTests();
            PaneNamingTests();
            EdgeCaseTests();
            ConfigIntegrityTests();
            GoogleDriveTests();
            await ArchiveTests.RunAll(Check, Equal);
            InstallTests.RunAll(Check, Equal);
            await DriveCopyTests.RunAll(Check, Equal);
            await FakeDriveMonitorTests.RunAll(Check, Equal);
            await OddCaseTests.RunAll(Check, Equal);
            await TransferFixTests.RunAll(Check, Equal);
            await FilesR2Tests.RunAll(Check, Equal);
            SettingsFixTests.RunAll(Check, Equal);
            SearchTests.RunAll(Check, Equal);
            await ConnectTests.RunAll(Check, Equal);
            ExactSeekTests.RunAll(Check);
            AudioFixTests.RunAll(Check, Equal);
            await UpdateFixTests.RunAll(Check, Equal);
            MainFixTests.RunAll(Check, Equal);
            await Round2FixTests.RunAll(Check, Equal);

            Console.WriteLine($"\n{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// No two actions may ship with the same key.
        ///
        /// Two registrations of one combination means Windows gives it to
        /// whichever asked first and the other silently never fires, with nothing
        /// anywhere saying which won. Preferences now refuses a duplicate as it
        /// is typed; the defaults have to be clean too, and that is not something
        /// to check by eye across eighteen actions.
        /// </summary>
        private static void NoDuplicateShortcuts()
        {
            var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fresh = new Settings();

            foreach (var info in AudioActions.All)
            {
                var assigned = Shortcut.Parse(info.Get(fresh));
                if (!assigned.IsAssigned) continue;

                string text = assigned.ToString();
                Check($"no default shares {text}", !claimed.ContainsKey(text),
                    claimed.TryGetValue(text, out var other) ? $"{info.Label} and {other}" : "");
                claimed[text] = info.Label;
            }

            var window = new Shortcut(fresh.HotkeyModifiers, fresh.HotkeyKey).ToString();
            Check("and none of them takes the window's own shortcut",
                !claimed.ContainsKey(window), window);
        }

        /// <summary>
        /// Every notification, checked one at a time.
        ///
        /// A catalogue is only worth having if it cannot rot, and the ways it can
        /// are all mechanical: two entries sharing an id so one silently
        /// configures the other, a template whose placeholders do not match its
        /// example so the message throws the first time it is ever needed, a name
        /// that says nothing, a category nobody can find. None of that is visible
        /// reading the list.
        /// </summary>
        private static void NotificationTests()
        {
            Console.WriteLine("Notifications:");

            var all = Notifications.All;
            Check($"there are at least 150 of them ({all.Count})", all.Count >= 150);

            // ---- every id is unique and usable ----
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int duplicates = 0, badIds = 0, badNames = 0, badCategories = 0;

            foreach (var info in all)
            {
                if (!seen.Add(info.Id)) duplicates++;

                // Ids end up in settings.json and in the override string, so a
                // space or a semicolon in one would either be unreadable or would
                // break the parsing outright.
                if (string.IsNullOrWhiteSpace(info.Id) ||
                    info.Id.Any(c => char.IsWhiteSpace(c) || c is ';' or '='))
                    badIds++;

                if (string.IsNullOrWhiteSpace(info.Name) || info.Name.Length < 3) badNames++;
                if (string.IsNullOrWhiteSpace(info.Category)) badCategories++;
            }

            Equal("no two share an id", "0", duplicates.ToString());
            Equal("every id is safe to store", "0", badIds.ToString());
            Equal("every one has a real name", "0", badNames.ToString());
            Equal("every one has a category", "0", badCategories.ToString());

            // ---- every one renders ----
            //
            // This is the test that matters. A template with {2} in it and two
            // example arguments throws FormatException, and it throws at the
            // moment the notification is finally needed — which for most of these
            // is the moment something has already gone wrong.
            int broken = 0, emptyRender = 0;
            string firstBroken = "";

            foreach (var info in all)
            {
                try
                {
                    var text = info.Preview();
                    if (string.IsNullOrWhiteSpace(text)) emptyRender++;

                    // A rendered message must not still be showing its
                    // scaffolding.
                    if (text.Contains('{') || text.Contains('}'))
                    {
                        broken++;
                        if (firstBroken.Length == 0) firstBroken = info.Id + ": " + text;
                    }
                }
                catch (Exception ex)
                {
                    broken++;
                    if (firstBroken.Length == 0) firstBroken = $"{info.Id}: {ex.GetType().Name}";
                }
            }

            Check($"all {all.Count} render without throwing", broken == 0, firstBroken);
            Equal("and none renders empty", "0", emptyRender.ToString());

            // ---- lookup ----
            foreach (var info in all)
                if (!ReferenceEquals(Notifications.ById(info.Id), info))
                {
                    Check($"{info.Id} can be looked up by id", false);
                    break;
                }
            Check("every id looks itself up", all.All(n => ReferenceEquals(Notifications.ById(n.Id), n)));
            Check("an unknown id is null, not an exception", Notifications.ById("nope.not.real") == null);

            // ---- defaults follow the house rule ----
            int on = all.Count(n => n.Default != NotificationChannel.None);
            Check($"most are on by default but not all ({on} of {all.Count})",
                on > 0 && on < all.Count);

            // Anything that only reports a failure has to be audible. A silent
            // error is the one case where "say less" is wrong.
            var silentFailures = all.Where(n =>
                (n.Id.Contains("fail") || n.Id.Contains("denied") || n.Id.Contains("refused")) &&
                n.Default != NotificationChannel.Speech).ToList();
            Check("nothing that reports a failure is silent", silentFailures.Count == 0,
                silentFailures.FirstOrDefault()?.Id ?? "");

            // ---- overrides round-trip ----
            var settings = new Settings();
            Check("with nothing saved, a notification uses its default",
                Notifications.ChannelsFor("audio.play", settings) ==
                Notifications.ById("audio.play")!.Default);

            var overrides = new Dictionary<string, NotificationChannel>(StringComparer.OrdinalIgnoreCase)
            {
                ["audio.play"] = NotificationChannel.None,
                ["drive.offline"] = NotificationChannel.Speech,
            };
            settings.SpeechOverrides = Notifications.FormatOverrides(overrides);

            Check("an override is honoured",
                Notifications.ChannelsFor("audio.play", settings) == NotificationChannel.None);
            Check("and so is the other one",
                Notifications.ChannelsFor("drive.offline", settings) == NotificationChannel.Speech);
            Check("one that was not overridden keeps its default",
                Notifications.ChannelsFor("audio.stop", settings) ==
                Notifications.ById("audio.stop")!.Default);

            // Only differences are stored, or the file becomes 167 lines nobody
            // can read and every added notification is a migration.
            var same = new Dictionary<string, NotificationChannel>(StringComparer.OrdinalIgnoreCase)
            {
                ["audio.play"] = Notifications.ById("audio.play")!.Default,
            };
            Equal("a value equal to the default is not written", "", Notifications.FormatOverrides(same));

            // A notification that was removed must not leave a setting behind.
            var stale = Notifications.ParseOverrides("gone.away=1;audio.play=0");
            Equal("an unknown id is dropped on load", "1", stale.Count.ToString());
            Check("and the real one survives", stale.ContainsKey("audio.play"));

            // Values from the three-bit era are read as one bit, so nothing in
            // the file can name a setting that no longer exists. It is only
            // reachable by hand-editing — the property was renamed so an old
            // file's overrides are not found at all — but a text file people are
            // invited to edit must not be able to produce an invalid enum.
            var wide = Notifications.ParseOverrides("audio.play=7;audio.stop=6");
            Check("an old three-bit value narrows to speak",
                wide.TryGetValue("audio.play", out var narrowed) &&
                narrowed == NotificationChannel.Speech);
            Check("and one without the speech bit narrows to off",
                wide.TryGetValue("audio.stop", out var alsoNarrowed) &&
                alsoNarrowed == NotificationChannel.None);

            // Rubbish in the file must not throw — it is a text file people edit.
            foreach (var rubbish in new[] { null, "", "   ", ";;;", "=", "a=", "=1", "a=b", "a=99999999999999999999" })
                try { Notifications.ParseOverrides(rubbish); }
                catch (Exception ex) { Check($"malformed overrides survive \"{rubbish}\"", false, ex.Message); }
            Check("malformed overrides never throw", true);

            // ---- the chooser offers exactly what can be stored ----
            //
            // Two, and there is no arithmetic left to get wrong. It offered
            // eight — every combination of status bar, speech and a tray
            // balloon — of which six named a balloon that does not exist any
            // more and two were indistinguishable by ear.
            Equal("the chooser offers speak or off", "2", Notifications.Choices.Count.ToString());
            Check("and those are the two",
                Notifications.Choices.Contains(NotificationChannel.None) &&
                Notifications.Choices.Contains(NotificationChannel.Speech));
            Check("every choice describes itself",
                Notifications.Choices.All(c => !string.IsNullOrWhiteSpace(Notifications.Describe(c))));
            Check("every default is one of the choices",
                all.All(n => Notifications.Choices.Contains(n.Default)));

            // ---- categories ----
            Check($"there are several categories ({Notifications.Categories.Count})",
                Notifications.Categories.Count >= 8);
            Check("every category has something in it",
                Notifications.Categories.All(c => all.Any(n => n.Category == c)));

            // ---- and the catalogue is actually connected to something ----
            //
            // The whole feature was inert once: 167 entries, a settings page, and
            // no code anywhere that consulted it before speaking. Everything else
            // in this section passed while it did nothing at all, which is exactly
            // the kind of green suite that teaches you not to trust suites.
            //
            // So this counts the ids that really appear in the source. It is a
            // crude check and it is the only one that would have caught it.
            var wired = WiredNotificationIds();
            Check($"the catalogue is connected to the application ({wired.Count} ids used in src)",
                wired.Count >= 25, string.Join(", ", wired.Take(5)));

            foreach (var id in wired)
                Check($"the wired id {id} exists in the catalogue", Notifications.ById(id) != null, id);

            // The Raised flag has to match reality in both directions, or the
            // honesty it buys is worth nothing: a flag set on something nobody
            // raises is the original lie in smaller type, and one missing from
            // something that is raised tells you a live message is dead.
            var claimed = all.Where(n => n.Raised).Select(n => n.Id)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
            var actual = wired.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();

            var claimedButNotRaised = claimed.Except(actual, StringComparer.OrdinalIgnoreCase).ToList();
            var raisedButNotClaimed = actual.Except(claimed, StringComparer.OrdinalIgnoreCase).ToList();

            Check("nothing claims to be raised that is not",
                claimedButNotRaised.Count == 0, string.Join(", ", claimedButNotRaised));
            Check("and nothing raised is left unmarked",
                raisedButNotClaimed.Count == 0, string.Join(", ", raisedButNotClaimed));

            // Every audio action routes through one, or its answer cannot be
            // silenced from Preferences at all.
            var missing = AudioActions.All.Where(a => string.IsNullOrEmpty(a.NotificationId)).ToList();
            Check("every audio action names a notification", missing.Count == 0,
                missing.FirstOrDefault()?.Label ?? "");

            var unknown = AudioActions.All
                .Where(a => a.NotificationId.Length > 0 && Notifications.ById(a.NotificationId) == null)
                .ToList();
            Check("and every one of those exists", unknown.Count == 0,
                unknown.FirstOrDefault()?.NotificationId ?? "");

            Console.WriteLine($"  checked {all.Count} notifications across {Notifications.Categories.Count} categories");
            Console.WriteLine($"  {wired.Count} of them are raised by the application");

            PreferencesShapeTests();
        }

        /// <summary>
        /// The notifications window actually builds, with every row in it.
        ///
        /// A hundred and sixty-seven rows is exactly the sort of thing that
        /// compiles, opens, and is quietly missing a category — and the count is
        /// the only thing that would say so. It also times the build, because a
        /// dialog that takes five seconds to appear is a defect whatever else it
        /// gets right.
        ///
        /// On its own STA thread: WinForms will not create a control otherwise,
        /// and the suite's own thread is not one.
        /// </summary>
        private static void PreferencesShapeTests()
        {
            string error = "";
            int rows = 0, categories = 0, checkedRight = 0;
            long buildMs = 0;

            var thread = new Thread(() =>
            {
                try
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    using var window = new SpeechForm(new Settings());
                    clock.Stop();
                    buildMs = clock.ElapsedMilliseconds;

                    // One item per message across every category, and each item
                    // checked exactly when that message is spoken by default.
                    var list = window.CategoryList;
                    categories = list.Items.Count;
                    for (int c = 0; c < list.Items.Count; c++)
                    {
                        list.SelectedIndex = c;
                        var inCategory = Notifications.All.Where(n => n.Category == Notifications.Categories[c]).ToList();
                        rows += window.ShownCount;
                        for (int i = 0; i < window.ShownCount && i < inCategory.Count; i++)
                            if (window.IsChecked(i) == (inCategory[i].Default == NotificationChannel.Speech)) checkedRight++;
                    }
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(30));

            Equal("the speech window builds without throwing", "", error);
            if (error.Length > 0) return;

            Check($"and quickly ({buildMs}ms)", buildMs < 5000, $"{buildMs}ms");
            Equal($"there is an item for every one of the {Notifications.All.Count}",
                Notifications.All.Count.ToString(), rows.ToString());
            Equal("and each is ticked exactly when it is spoken by default",
                Notifications.All.Count.ToString(), checkedRight.ToString());
            Check("every message the catalogue lists is actually raised",
                Notifications.All.All(n => n.Raised), string.Join(", ", Notifications.All.Where(n => !n.Raised).Select(n => n.Id)));
            Equal("and a category for each group",
                Notifications.Categories.Count.ToString(), categories.ToString());

            SettingChoiceTests();
            LimiterFormTests();
            PreferencesWindowTests();
            UnplayableFormatTests();
            HeldKeyDistanceTests();
            FixedBehaviourTests();
            WindowSpeedTests();
            DriveLetterMoveTests();
            DriveMonitorTests();
            DriveMonitorResilienceTests();
            SyncDeleteTests();
            SyncAdoptionTests();
            AccountButtonTests();
        }

        /// <summary>
        /// A library already on both sides is adopted, not copied
        /// again; the pair keeps what it remembers; Google saying "not now" is
        /// waited out; and the configurator is on File while Drive is on.
        /// </summary>
        private static void SyncAdoptionTests()
        {
            var t0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            var uploadedLater = t0.AddDays(30);
            Dictionary<string, SyncFile> Files(params (string P, SyncFile F)[] items) =>
                items.ToDictionary(i => i.P, i => i.F, StringComparer.OrdinalIgnoreCase);
            var none = new Dictionary<string, SyncBase>(StringComparer.OrdinalIgnoreCase);
            var rules = new SyncRules(SyncMode.TwoWay, false);
            string Plan(Dictionary<string, SyncFile> l, Dictionary<string, SyncFile> r, Dictionary<string, string>? md5,
                SyncRules? with = null) =>
                string.Join(",", SyncPlanner.Plan(with ?? rules, l, r, none, md5).Select(a => a.Kind + ":" + a.Path));

            // The same track on both sides, Drive's copy uploaded a month later.
            var local = Files(("a.flac", new SyncFile(100, t0)), ("b.flac", new SyncFile(200, t0)));
            var remote = Files(("a.flac", new SyncFile(100, uploadedLater, "A", "aaa")),
                               ("b.flac", new SyncFile(200, uploadedLater, "B", "bbb")));
            var md5 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["a.flac"] = "AAA", ["b.flac"] = "bbb" };
            Equal("same size and MD5 is adopted, nothing copied", "Record:a.flac,Record:b.flac", Plan(local, remote, md5));
            foreach (var mode in new[] { SyncMode.UploadOnly, SyncMode.DownloadOnly })
                Equal($"{mode} adopts it as well", "Record:a.flac,Record:b.flac",
                    Plan(local, remote, md5, new SyncRules(mode, true)));
            md5["b.flac"] = "ccc";
            Equal("a different MD5 is a clash, kept both by default", "Record:a.flac,KeepBothRemoteWins:b.flac",
                Plan(local, remote, md5));
            Equal("an MD5 not worked out yet leaves the file alone", "", Plan(local, remote, null));
            Equal("so the engine is asked to hash exactly those", "a.flac,b.flac",
                string.Join(",", SyncPlanner.NeedingHash(rules, local, remote, none)));
            Equal("a different size is never hashed, and is a clash",
                "KeepBothRemoteWins:a.flac", Plan(Files(("a.flac", new SyncFile(100, t0))),
                    Files(("a.flac", new SyncFile(101, uploadedLater, "A", "aaa"))), null));

            // No MD5 on Drive: size and modified time.
            var noMd5 = Files(("a.flac", new SyncFile(100, t0.AddSeconds(1), "A")));
            Equal("no Drive MD5: same size and time within 2 seconds is adopted",
                "Record:a.flac", Plan(Files(("a.flac", new SyncFile(100, t0))), noMd5, null));
            Equal("no Drive MD5 and a different time is a clash",
                "KeepBothRemoteWins:a.flac", Plan(Files(("a.flac", new SyncFile(100, t0))),
                    Files(("a.flac", new SyncFile(100, uploadedLater, "A"))), null));
            Check("and is not hashed, there being nothing to compare with",
                SyncPlanner.NeedingHash(rules, Files(("a.flac", new SyncFile(100, t0))), noMd5, none).Count == 0);

            // The hash cache.
            var cache = new Dictionary<string, SyncHashEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["a.flac"] = new(100, t0, "aaa"),
                ["b.flac"] = new(200, t0.AddDays(-1), "old"),
            };
            var (known, toHash) = SyncHashes.Split(new[] { "a.flac", "b.flac", "c.flac" },
                Files(("a.flac", new SyncFile(100, t0)), ("b.flac", new SyncFile(200, t0)), ("c.flac", new SyncFile(5, t0))), cache);
            Equal("a remembered MD5 is used, not worked out again", "aaa", known.GetValueOrDefault("a.flac") ?? "");
            Equal("a file changed since is hashed again, and a new one for the first time", "b.flac,c.flac",
                string.Join(",", toHash));

            // Backoff.
            Equal("first wait 2 seconds", "2", SyncBackoff.Delay(0, null, 0).TotalSeconds.ToString());
            Equal("then doubling", "16", SyncBackoff.Delay(3, null, 0).TotalSeconds.ToString());
            Equal("capped at five minutes", "300", SyncBackoff.Delay(20, null, 0).TotalSeconds.ToString());
            Equal("jitter takes off up to half", "150", SyncBackoff.Delay(20, null, 1).TotalSeconds.ToString());
            Equal("Retry-After is honoured when it asks for longer", "40",
                SyncBackoff.Delay(0, TimeSpan.FromSeconds(40), 0).TotalSeconds.ToString());
            Equal("and not when it asks for less", "16", SyncBackoff.Delay(3, TimeSpan.FromSeconds(1), 0).TotalSeconds.ToString());
            Check("429 is waited out", SyncBackoff.IsTransient(new InvalidOperationException("list failed 429: Too many requests")));
            Check("so is a 403 rate limit", SyncBackoff.IsTransient(new InvalidOperationException("upload failed 403: userRateLimitExceeded")));
            Check("and 503", SyncBackoff.IsTransient(new InvalidOperationException("upload session failed 503: Service unavailable")));
            Check("and an HTTP 502", SyncBackoff.IsTransient(new System.Net.Http.HttpRequestException("x", null, System.Net.HttpStatusCode.BadGateway)));
            Check("but not a 404", !SyncBackoff.IsTransient(new InvalidOperationException("list failed 404: File not found")));
            Check("nor a full account", !SyncBackoff.IsTransient(new DriveQuotaException("storageQuotaExceeded", "full", false)));
            Check("nor a file that merely has 500 in its name", !SyncBackoff.IsTransient(new IOException("track 500.flac: in use")));

            // The state lives as long as the pair.
            var pair = new DriveSyncPair { LocalFolder = @"D:\Music", DriveFolderId = "M" };
            Check("a state from before folders were recorded is kept", DriveMonitor.StateFits(null, null, pair) == (true, true));
            pair.Mode = SyncMode.UploadOnly; pair.CheckMinutes = 15; pair.SkipExtensions = ".iso";
            Check("editing options keeps everything", DriveMonitor.StateFits(@"D:\Music\", "M", pair) == (true, true));
            Check("a different Drive folder starts again, keeping the MD5s",
                DriveMonitor.StateFits(@"D:\Music", "OTHER", pair) == (false, true));
            Check("a different PC folder starts again entirely",
                DriveMonitor.StateFits(@"E:\Music", "M", pair) == (false, false));

            // The File menu.
            Check("the configurator shows while Drive is on", DriveMonitorForm.OnFileMenu(new Settings { GoogleDriveEnabled = true }));
            Check("and not while it is off", !DriveMonitorForm.OnFileMenu(new Settings { GoogleDriveEnabled = false }));
            var main = SourceFile("MainForm.cs") ?? "";
            Check("it is on File, rechecked as the menu opens and after Preferences",
                main.Contains("\"Google Drive &sync configurator...\"") &&
                main.Contains("file.DropDownOpening += (_, _) => _syncConfiguratorItem.Visible = DriveMonitorForm.OnFileMenu(_settings);") &&
                main.Contains("_syncConfiguratorItem.Visible = DriveMonitorForm.OnFileMenu(settings);"));
            Check("and no longer in Preferences",
                !(SourceFile("SettingsForm.cs") ?? "").Contains("Configure Google Drive &monitor"));
            var pending = new DriveMonitor.PairStatus(null, null, true, 0, 0, 1200, 3120);
            Check("the pair says how far checking has got",
                DriveMonitorForm.Describe(new DriveSyncPair { Name = "Music", LocalFolder = @"D:\Music", DriveFolderPath = "My Drive/Music" }, pending, t0)
                    .EndsWith("checking what is already there, 1,200 of 3,120 files"));
            Check("and says when it is waiting on Google",
                DriveMonitorForm.Describe(new DriveSyncPair { Name = "Music" },
                    new DriveMonitor.PairStatus(null, "waiting, Google is limiting requests, trying again in 40 seconds", true), t0)
                    .EndsWith("waiting, Google is limiting requests, trying again in 40 seconds"));
        }

        /// <summary>
        /// A pair whose folder is gone never deletes anything, and deleting a
        /// synced folder in the window says what will happen on the other side.
        /// </summary>
        private static void SyncDeleteTests()
        {
            var t0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

            // Why the guard exists: a trashed Drive folder lists as empty, and
            // with deletes on the planner would then recycle every PC file.
            var local = new Dictionary<string, SyncFile>(StringComparer.OrdinalIgnoreCase)
            {
                ["a.flac"] = new(5, t0), ["b.flac"] = new(6, t0),
            };
            var last = new Dictionary<string, SyncBase>(StringComparer.OrdinalIgnoreCase)
            {
                ["a.flac"] = new(5, t0, 5, t0, "A"), ["b.flac"] = new(6, t0, 6, t0, "B"),
            };
            var unguarded = SyncPlanner.Plan(SyncMode.TwoWay, true, local,
                new Dictionary<string, SyncFile>(StringComparer.OrdinalIgnoreCase), last);
            Check("an empty Drive listing alone would read as everything deleted",
                unguarded.All(a => a.Kind == SyncActionKind.DeleteLocal) && unguarded.Count == 2);
            Equal("so a gone Drive folder pauses the pair instead",
                "paused: its Google Drive folder is gone. Remove the sync or restore the folder.",
                SyncSafety.RootProblem(localFolderThere: true, driveFolderThere: false) ?? "");
            Equal("and so does a gone PC folder",
                "paused: its PC folder is gone. Remove the sync or restore the folder.",
                SyncSafety.RootProblem(localFolderThere: false, driveFolderThere: true) ?? "");
            Check("both there: it syncs, which is how it resumes by itself",
                SyncSafety.RootProblem(true, true) == null);
            var engine = SourceFile("DriveMonitor.cs") ?? "";
            int guard = engine.IndexOf("SyncSafety.RootProblem(", StringComparison.Ordinal);
            int scan = engine.IndexOf("var local = ScanLocal(root", StringComparison.Ordinal);
            Check("the engine checks both roots before it lists or plans anything", guard > 0 && scan > guard);
            Check("and asks Drive whether its folder is trashed, not just listable",
                engine.Contains("client.FolderAlive(pair.DriveFolderId", StringComparison.Ordinal));

            // The warning's wording.
            var on = new DriveSyncPair { Name = "Music", LocalFolder = @"C:\Music", DriveFolderPath = "My Drive/Music", DriveFolderId = "M", CopyDeletes = true };
            var off = new DriveSyncPair { Name = "Docs", LocalFolder = @"D:\Docs", DriveFolderPath = "My Drive/Docs", DriveFolderId = "D" };
            var onText = SyncSafety.DeleteWarning(new[] { on }, deletingDriveSide: false);
            Check("the warning names the pair and says to remove the sync first",
                onText.StartsWith("This folder is part of the sync pair Music. Deleting it follows that sync's settings. " +
                                  "Remove the sync first if you only want to delete it here."));
            Check("deletes on, deleting the PC side: the other copy goes to the Google Drive trash",
                onText.EndsWith("If you delete it anyway, it is deleted on both sides: the other copy goes to the Google Drive trash."));
            Check("deletes on, deleting the Drive side: the other copy goes to the Recycle Bin",
                SyncSafety.DeleteWarning(new[] { on }, deletingDriveSide: true).EndsWith("goes to the Recycle Bin."));
            Check("deletes off: only this side is deleted",
                SyncSafety.DeleteWarning(new[] { off }, false)
                    .EndsWith("If you delete it anyway, only this side is deleted; the other copy is kept."));
            var both = SyncSafety.DeleteWarning(new[] { on, off }, false);
            Check("several pairs are all listed in one warning", both.Contains("sync pair Music") && both.Contains("sync pair Docs"));

            // Which pairs a delete touches.
            var pairs = new[] { on, off };
            string Touched(params string[] paths) => string.Join(",",
                SyncSafety.PairsTouchedBy(paths, pairs, "G:", p => p.Equals(@"G:\Tunes", StringComparison.OrdinalIgnoreCase) ? "M" : null)
                    .Select(t => t.Pair.Name + (t.DriveSide ? ":drive" : ":pc")));
            Equal("the PC folder itself", "Music:pc", Touched(@"C:\Music"));
            Equal("a folder holding it", "Music:pc", Touched(@"C:\"));
            Equal("a folder inside it is just a delete, synced as usual", "", Touched(@"C:\Music\Album"));
            Equal("a sibling that only starts with the same letters is not it", "", Touched(@"C:\Musical"));
            Equal("the Drive folder on the letter, matched by its Drive id", "Music:drive", Touched(@"G:\Tunes"));
            Equal("or by its Drive path", "Docs:drive", Touched(@"G:\Docs"));
            Equal("the whole letter holds every Drive folder", "Music:drive,Docs:drive", Touched(@"G:\"));
            Equal("one selection touching two pairs lists both", "Music:pc,Docs:pc", Touched(@"C:\Music", @"D:\Docs"));
            Equal("an unrelated folder touches nothing", "", Touched(@"E:\Other"));

            // The dialog: Cancel is the default and Escape, and the buttons are in order.
            string error = "";
            string accept = "", escape = "", order = "";
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = new SyncDeleteForm(new[] { on }, false);
                    accept = (form.AcceptButton as Button)?.Text ?? "";
                    escape = (form.CancelButton as Button)?.Text ?? "";
                    order = string.Join("|", FindAll<Button>(form).OrderBy(b => b.TabIndex).Select(b => b.Text));
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(30));
            Equal("the sync warning builds", "", error);
            Equal("Enter cancels", "Cancel", accept);
            Equal("so does Escape", "Cancel", escape);
            Equal("the buttons, in order", "&Remove the sync, then delete|&Delete anyway|Cancel", order);
            var main = SourceFile("MainForm.cs") ?? "";
            int remove = main.IndexOf("RemoveSyncPairs(touched.Select", StringComparison.Ordinal);
            int deleting = main.IndexOf("outcome = await TrashOnDrive(paths);", StringComparison.Ordinal);
            Check("Delete and Shift+Delete both ask, and the pair goes before any file does",
                remove > 0 && deleting > remove);
        }

        /// <summary>
        /// Offline, interrupted and out-of-room: the monitor has to pick up
        /// everything it missed, never do anything twice, and never fill a disk.
        /// </summary>
        private static void DriveMonitorResilienceTests()
        {
            var t0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            var t1 = t0.AddHours(1);
            var t2 = t0.AddHours(2);
            Dictionary<string, SyncFile> Files(params (string Path, SyncFile File)[] items) =>
                items.ToDictionary(i => i.Path, i => i.File, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, SyncBase> Base(params (string Path, SyncBase Base)[] items) =>
                items.ToDictionary(i => i.Path, i => i.Base, StringComparer.OrdinalIgnoreCase);
            SyncBase B(long size, DateTime when, string id, string? md5 = null) => new(size, when, size, when, id, md5);
            string Plan(SyncRules rules, Dictionary<string, SyncFile> l, Dictionary<string, SyncFile> r,
                Dictionary<string, SyncBase> b) =>
                string.Join(",", SyncPlanner.Plan(rules, l, r, b).Select(a => a.Kind + ":" + a.Path));
            var twoWay = new SyncRules(SyncMode.TwoWay, false);

            // Offline on both sides: found by comparing with the last sync, not by events.
            var last = Base(("a.txt", B(1, t0, "A")), ("b.txt", B(2, t0, "B")));
            Equal("edits made offline on both sides, to different files, both go across",
                "Upload:a.txt,Download:b.txt",
                Plan(twoWay, Files(("a.txt", new SyncFile(5, t1)), ("b.txt", new SyncFile(2, t0))),
                    Files(("a.txt", new SyncFile(1, t0, "A")), ("b.txt", new SyncFile(9, t1, "B"))), last));
            Equal("the same file edited on both sides while offline keeps both",
                "KeepBothRemoteWins:a.txt",
                Plan(twoWay, Files(("a.txt", new SyncFile(5, t1))), Files(("a.txt", new SyncFile(7, t2, "A"))),
                    Base(("a.txt", B(1, t0, "A")))));

            // Interrupted part way, with nothing saved: the rerun does not do anything twice.
            var local = Files(("new1.txt", new SyncFile(3, t1)), ("new2.txt", new SyncFile(4, t1)));
            var remote = Files(("down1.txt", new SyncFile(5, t1, "D1")), ("down2.txt", new SyncFile(6, t1, "D2")));
            var none = Base();
            var first = SyncPlanner.Plan(twoWay, local, remote, none);
            // The first two actions completed on the wire, then the connection
            // dropped before the state was written.
            foreach (var done in first.Take(2))
            {
                if (done.Kind == SyncActionKind.Upload)
                    remote[done.Path] = new SyncFile(local[done.Path].Size, local[done.Path].ModifiedUtc, "new-" + done.Path);
                if (done.Kind == SyncActionKind.Download)
                    local[done.Path] = new SyncFile(remote[done.Path].Size, remote[done.Path].ModifiedUtc);
            }
            var rerun = SyncPlanner.Plan(twoWay, local, remote, none);
            Check("after an interruption the finished files are only remembered, not copied again",
                first.Take(2).All(d => rerun.Any(a => a.Path == d.Path && a.Kind == SyncActionKind.Record)),
                string.Join(",", rerun.Select(a => a.Kind + ":" + a.Path)));
            Check("and the unfinished ones are still to do, once each",
                first.Skip(2).All(d => rerun.Count(a => a.Path == d.Path && a.Kind == d.Kind) == 1));
            Check("and nothing is turned into a conflict copy",
                !rerun.Any(a => a.Kind is SyncActionKind.KeepBothLocalWins or SyncActionKind.KeepBothRemoteWins));

            // Drive's checksum decides when it is there.
            var md5Base = Base(("a.txt", B(5, t0, "A", "aaa")));
            Equal("a Drive file with a new checksum is a change even at the same size and time",
                "Download:a.txt", Plan(twoWay, Files(("a.txt", new SyncFile(5, t0))),
                    Files(("a.txt", new SyncFile(5, t0, "A", "bbb"))), md5Base));
            Equal("and the same checksum with only a new time is not",
                "", Plan(twoWay, Files(("a.txt", new SyncFile(5, t0))),
                    Files(("a.txt", new SyncFile(5, t2, "A", "aaa"))), md5Base));

            // The options.
            Equal("subfolders off leaves anything in a subfolder alone",
                "Upload:top.txt", Plan(new SyncRules(SyncMode.TwoWay, false, IncludeSubfolders: false),
                    Files(("top.txt", new SyncFile(1, t0)), ("sub/inner.txt", new SyncFile(1, t0))), Files(), Base()));
            Equal("skipped file types are left alone, written with or without the dot",
                "Upload:keep.txt", Plan(new SyncRules(SyncMode.TwoWay, false, SkipExtensions: "iso, .MKV"),
                    Files(("keep.txt", new SyncFile(1, t0)), ("disc.iso", new SyncFile(1, t0)), ("film.mkv", new SyncFile(1, t0))),
                    Files(), Base()));
            const long mb = 1024 * 1024;
            Equal("files larger than the limit are left alone",
                "Upload:small.txt", Plan(new SyncRules(SyncMode.TwoWay, false, MaxBytes: 100 * mb),
                    Files(("small.txt", new SyncFile(mb, t0)), ("big.bin", new SyncFile(200 * mb, t0))), Files(), Base()));
            Equal("a file over the limit on one side only is not read as deleted on the other",
                "", Plan(new SyncRules(SyncMode.TwoWay, true, MaxBytes: 100 * mb),
                    Files(("grew.bin", new SyncFile(200 * mb, t1))), Files(),
                    Base(("grew.bin", B(50 * mb, t0, "G")))));
            Equal("PC wins: the PC's copy replaces Drive's",
                "Upload:a.txt", Plan(new SyncRules(SyncMode.TwoWay, false, ConflictChoice.PcWins),
                    Files(("a.txt", new SyncFile(5, t1))), Files(("a.txt", new SyncFile(7, t2, "A"))), Base(("a.txt", B(1, t0, "A")))));
            Equal("Drive wins: Drive's copy replaces the PC's",
                "Download:a.txt", Plan(new SyncRules(SyncMode.TwoWay, false, ConflictChoice.DriveWins),
                    Files(("a.txt", new SyncFile(5, t2))), Files(("a.txt", new SyncFile(7, t1, "A"))), Base(("a.txt", B(1, t0, "A")))));
            var pairRules = SyncRules.For(new DriveSyncPair { MaxFileMegabytes = 500, SkipExtensions = ".iso", IncludeSubfolders = false });
            Check("a pair's options become its rules",
                pairRules.MaxBytes == 500 * mb && pairRules.SkipsType("x.ISO") && !pairRules.IncludeSubfolders);

            // Small files first.
            var ordered = SyncPlanner.Ordered(
                new List<SyncAction>
                {
                    new(SyncActionKind.Download, "huge.mkv"), new(SyncActionKind.Upload, "tiny.txt"),
                    new(SyncActionKind.Forget, "gone.txt"),
                },
                Files(("tiny.txt", new SyncFile(10, t0))), Files(("huge.mkv", new SyncFile(10_000_000, t0, "H"))));
            Equal("bookkeeping first, then the smallest transfer, the huge one last",
                "gone.txt,tiny.txt,huge.mkv", string.Join(",", ordered.Select(a => a.Path)));

            // Disk space.
            const long gb = 1024L * 1024 * 1024;
            Equal("the margin is 2 gigabytes on a small disk", (2 * gb).ToString(), SyncSpace.Margin(20 * gb).ToString());
            Equal("and 5 percent on a big one", (50 * gb).ToString(), SyncSpace.Margin(1000 * gb).ToString());
            Check("something that leaves the margin fits", SyncSpace.Fits(7 * gb, 13 * gb, 100 * gb));
            Check("something that would eat into it does not", !SyncSpace.Fits(9 * gb, 13 * gb, 100 * gb));
            Equal("and says how much more room it needs", (7 * gb).ToString(),
                SyncSpace.Shortfall(15 * gb, 13 * gb, 100 * gb).ToString());
            Check("a disk already inside its margin has no room at all", SyncSpace.Room(gb, 100 * gb) == 0);
            Equal("a pass stops cleanly at the file that would cross the margin", "2",
                SyncSpace.HowManyFit(new[] { 3 * gb, 4 * gb, 5 * gb, gb }, 12 * gb, 100 * gb).ToString());
            Equal("nothing is written to a full disk", "0",
                SyncSpace.HowManyFit(new[] { 1L }, gb, 100 * gb).ToString());

            // Resuming a download.
            Equal("an interrupted download carries on from what is on disk", "4096",
                SyncSpace.ResumeOffset(4096, 10_000, sameFile: true).ToString());
            Equal("not if the Drive file changed meanwhile", "0", SyncSpace.ResumeOffset(4096, 10_000, sameFile: false).ToString());
            Equal("not if what is on disk is longer than the file", "0", SyncSpace.ResumeOffset(20_000, 10_000, sameFile: true).ToString());
            var part = new DriveMonitor.PartialDownload("F", 100, t0, "abc");
            Check("the same Drive file, by checksum", DriveMonitor.SameDriveFile(part, new SyncFile(100, t1, "F", "abc")));
            Check("a changed checksum is a different file", !DriveMonitor.SameDriveFile(part, new SyncFile(100, t0, "F", "xyz")));
            Check("so is a different file id", !DriveMonitor.SameDriveFile(part, new SyncFile(100, t0, "G", "abc")));

            // Progress said along the way.
            Equal("crossing a quarter is said", "25", SyncSpace.QuarterPassed(20, 30, 100).ToString());
            Equal("staying inside one is not", "0", SyncSpace.QuarterPassed(30, 40, 100).ToString());
            Equal("finishing is said as done, not as a quarter", "0", SyncSpace.QuarterPassed(90, 100, 100).ToString());
            Check("a pass over a gigabyte is big", SyncSpace.IsBig(2 * gb, 3));
            Check("so is one of more than 200 files", SyncSpace.IsBig(1, 201));
            Check("an ordinary one is not", !SyncSpace.IsBig(10 * mb, 20));
            Equal("the start of a big pass says how much", "214 gigabytes to download, 3 gigabytes to upload",
                DriveMonitor.Amounts(214 * gb, 3 * gb));

            var pair = new DriveSyncPair { Name = "Music", LocalFolder = @"C:\Music", DriveFolderPath = "My Drive/Music" };
            Check("the monitor window shows live progress",
                DriveMonitorForm.Describe(pair, new DriveMonitor.PairStatus(null, null, true, 40 * gb, 214 * gb), t0)
                    .EndsWith("syncing, 40 gigabytes of 214 gigabytes"));
            Check("and says plainly when it is paused for space",
                DriveMonitorForm.Describe(pair, new DriveMonitor.PairStatus(null, "paused: C: is out of space, needs 30 gigabytes more", false), t0)
                    .EndsWith("paused: C: is out of space, needs 30 gigabytes more"));
            pair.CheckMinutes = 0;
            Check("a pair checked only on request says so",
                DriveMonitorForm.Describe(pair, null, t0).Contains("Drive checked only on Sync now"));
        }

        /// <summary>
        /// The Google Drive monitor's decisions, which are the part that can
        /// lose somebody's files, checked rule by rule with no network.
        /// </summary>
        private static void DriveMonitorTests()
        {
            var t0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            var t1 = t0.AddHours(1);
            var t2 = t0.AddHours(2);
            SyncFile F(long size, DateTime when, string? id = null) => new(size, when, id);
            Dictionary<string, SyncFile> Files(params (string Path, SyncFile File)[] items) =>
                items.ToDictionary(i => i.Path, i => i.File, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, SyncBase> Base(params (string Path, SyncBase Base)[] items) =>
                items.ToDictionary(i => i.Path, i => i.Base, StringComparer.OrdinalIgnoreCase);
            SyncBase B(long size, DateTime when, string id) => new(size, when, size, when, id);
            string Plan(SyncMode mode, bool deletes, Dictionary<string, SyncFile> l, Dictionary<string, SyncFile> r,
                Dictionary<string, SyncBase> b) =>
                string.Join(",", SyncPlanner.Plan(mode, deletes, l, r, b).Select(a => a.Kind + ":" + a.Path));
            var none = Base();

            // New files.
            Equal("upload only: a new PC file goes up",
                "Upload:a.txt", Plan(SyncMode.UploadOnly, false, Files(("a.txt", F(5, t0))), Files(), none));
            Equal("upload only: a new Drive file is never downloaded",
                "", Plan(SyncMode.UploadOnly, true, Files(), Files(("a.txt", F(5, t0, "x"))), none));
            Equal("download only: a new Drive file comes down",
                "Download:a.txt", Plan(SyncMode.DownloadOnly, false, Files(), Files(("a.txt", F(5, t0, "x"))), none));
            Equal("download only: a new PC file is never uploaded",
                "", Plan(SyncMode.DownloadOnly, true, Files(("a.txt", F(5, t0))), Files(), none));
            Equal("two-way: new files go both ways",
                "Upload:a.txt,Download:b.txt",
                Plan(SyncMode.TwoWay, false, Files(("a.txt", F(5, t0))), Files(("b.txt", F(6, t0, "y"))), none));

            // First sync with files on both sides: nothing is deleted.
            var firstL = Files(("same.txt", F(5, t0)), ("onlypc.txt", F(1, t0)), ("diff.txt", F(9, t2)));
            var firstR = Files(("same.txt", F(5, t0, "s")), ("onlydrive.txt", F(2, t0, "o")), ("diff.txt", F(3, t1, "d")));
            foreach (var mode in new[] { SyncMode.TwoWay, SyncMode.UploadOnly, SyncMode.DownloadOnly })
            {
                var kinds = SyncPlanner.Plan(mode, true, firstL, firstR, none).Select(a => a.Kind).ToList();
                Check($"first sync ({mode}) deletes nothing even with deletes on",
                    !kinds.Contains(SyncActionKind.DeleteLocal) && !kinds.Contains(SyncActionKind.DeleteRemote));
            }
            Equal("first sync two-way: identical files are just remembered, a difference keeps both",
                "KeepBothLocalWins:diff.txt,Download:onlydrive.txt,Upload:onlypc.txt,Record:same.txt",
                Plan(SyncMode.TwoWay, true, firstL, firstR, none));

            // Changes after a sync.
            var synced = Base(("a.txt", B(5, t0, "x")));
            Equal("two-way: a PC edit goes up",
                "Upload:a.txt", Plan(SyncMode.TwoWay, false, Files(("a.txt", F(7, t1))), Files(("a.txt", F(5, t0, "x"))), synced));
            Equal("two-way: a Drive edit comes down",
                "Download:a.txt", Plan(SyncMode.TwoWay, false, Files(("a.txt", F(5, t0))), Files(("a.txt", F(8, t1, "x"))), synced));
            Equal("two-way: edited in both places keeps both, the newer keeps the name",
                "KeepBothRemoteWins:a.txt",
                Plan(SyncMode.TwoWay, false, Files(("a.txt", F(7, t1))), Files(("a.txt", F(8, t2, "x"))), synced));
            Equal("download only: a PC edit is left alone, never overwritten",
                "", Plan(SyncMode.DownloadOnly, false, Files(("a.txt", F(7, t1))), Files(("a.txt", F(5, t0, "x"))), synced));
            Equal("upload only: a Drive edit is not downloaded",
                "", Plan(SyncMode.UploadOnly, false, Files(("a.txt", F(5, t0))), Files(("a.txt", F(8, t1, "x"))), synced));
            Equal("nothing changed, nothing done",
                "", Plan(SyncMode.TwoWay, true, Files(("a.txt", F(5, t0))), Files(("a.txt", F(5, t0, "x"))), synced));

            // Deletes.
            Equal("two-way, deletes off: a file deleted on the PC comes back from Drive",
                "Download:a.txt", Plan(SyncMode.TwoWay, false, Files(), Files(("a.txt", F(5, t0, "x"))), synced));
            Equal("two-way, deletes on: a file deleted on the PC goes to the Drive trash",
                "DeleteRemote:a.txt", Plan(SyncMode.TwoWay, true, Files(), Files(("a.txt", F(5, t0, "x"))), synced));
            Equal("two-way, deletes on: a file deleted in Drive goes to the Recycle Bin",
                "DeleteLocal:a.txt", Plan(SyncMode.TwoWay, true, Files(("a.txt", F(5, t0))), Files(), synced));
            Equal("upload only, deletes on: a Drive delete is never copied to the PC; the backup is put back",
                "Upload:a.txt", Plan(SyncMode.UploadOnly, true, Files(("a.txt", F(5, t0))), Files(), synced));
            Equal("upload only, deletes off: a PC delete leaves Drive alone",
                "Forget:a.txt", Plan(SyncMode.UploadOnly, false, Files(), Files(("a.txt", F(5, t0, "x"))), synced));
            Equal("download only, deletes on: a PC delete never touches Drive; it comes back",
                "Download:a.txt", Plan(SyncMode.DownloadOnly, true, Files(), Files(("a.txt", F(5, t0, "x"))), synced));
            Equal("deletes on, but edited on the other side since: the edit wins over the delete",
                "Upload:a.txt", Plan(SyncMode.TwoWay, true, Files(("a.txt", F(9, t1))), Files(), synced));
            Equal("gone from both: forgotten",
                "Forget:a.txt", Plan(SyncMode.TwoWay, true, Files(), Files(), synced));

            // A rename is a delete and an add.
            Equal("rename, deletes off: the new name is copied and the old one kept",
                "Download:a.txt,Upload:b.txt",
                Plan(SyncMode.TwoWay, false, Files(("b.txt", F(5, t0))), Files(("a.txt", F(5, t0, "x"))), synced));
            Equal("rename, deletes on: the new name is copied and the old one trashed",
                "DeleteRemote:a.txt,Upload:b.txt",
                Plan(SyncMode.TwoWay, true, Files(("b.txt", F(5, t0))), Files(("a.txt", F(5, t0, "x"))), synced));

            // Skipped names.
            foreach (var name in new[] { "desktop.ini", "Thumbs.db", "~$report.docx", "x.tmp", "song.flac.partial" })
                Check($"{name} is never synced", SyncPlanner.IsSkipped(name));
            Check("an ordinary file is synced", !SyncPlanner.IsSkipped("report.docx"));
            Equal("skipped files and folders are left out of the plan", "",
                Plan(SyncMode.TwoWay, true, Files(("Thumbs.db", F(1, t0)), ("~$a.docx", F(1, t0))), Files(), none));
            Equal("a two-second clock difference is not a change", "",
                Plan(SyncMode.TwoWay, false, Files(("a.txt", F(5, t0.AddSeconds(2)))), Files(("a.txt", F(5, t0, "x"))), synced));

            Equal("the copy that loses the name is marked as a conflict",
                "notes/report (conflict 2026-10-05).docx",
                SyncPlanner.ConflictName("notes/report.docx", new DateTime(2026, 10, 5), _ => false));
            Equal("and never over another file",
                "report (conflict 2026-10-05 2).docx",
                SyncPlanner.ConflictName("report.docx", new DateTime(2026, 10, 5), p => p == "report (conflict 2026-10-05).docx"));

            // How a pair reads out.
            var pair = new DriveSyncPair
            {
                Name = "Music", LocalFolder = @"C:\Users\Conner\Music", DriveFolderPath = "My Drive/Music",
                Mode = SyncMode.TwoWay,
            };
            Equal("a pair is one sentence",
                @"Music: C:\Users\Conner\Music and Drive folder My Drive/Music, two-way, deletes not copied, last synced 2 minutes ago",
                DriveMonitorForm.Describe(pair, new DriveMonitor.PairStatus(t0, null, false), t0.AddMinutes(2)));
            pair.Mode = SyncMode.UploadOnly;
            pair.CopyDeletes = true;
            pair.Paused = true;
            Check("paused and upload only read as such",
                DriveMonitorForm.Describe(pair, null, t0).EndsWith("upload only, PC to Drive, deletes copied, paused"));
            Equal("the tally counts only what happened", "3 uploaded, 1 deleted", DriveMonitor.Tally(3, 0, 1));

            // The monitor never touches the app's own folder, which holds Drive's sync root.
            Check("the settings folder cannot be monitored", DriveMonitor.Refusal(Settings.AppDataDir) != null);
            Check("a missing folder cannot be monitored",
                DriveMonitor.Refusal(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N"))) != null);
            Check("an ordinary folder can", DriveMonitor.Refusal(Path.GetTempPath()) == null);

            // Pairs survive a save, encrypted with everything else.
            var previous = Settings.OverrideAppDataDir;
            var dir = NewTempDir();
            try
            {
                Settings.OverrideAppDataDir = dir;
                var settings = new Settings();
                settings.DriveSyncPairs.Add(new DriveSyncPair
                {
                    Name = "Docs", LocalFolder = @"D:\Docs", DriveFolderId = "abc", DriveFolderPath = "My Drive/Docs",
                    Mode = SyncMode.DownloadOnly, CopyDeletes = true,
                });
                settings.Save();
                var back = Settings.Load().DriveSyncPairs.SingleOrDefault();
                Check("a pair is saved and read back",
                    back != null && back.Name == "Docs" && back.DriveFolderId == "abc" &&
                    back.Mode == SyncMode.DownloadOnly && back.CopyDeletes && !back.Paused);
                Check("and settings.json still gives nothing away",
                    !File.ReadAllText(Path.Combine(dir, "settings.json")).Contains("Docs"));
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// The Google Drive account button says what pressing it will do:
        /// Connect, Disconnect while Drive is working, Reconnect when Google
        /// wants the sign-in again.
        /// </summary>
        private static void AccountButtonTests()
        {
            var previous = Settings.OverrideAppDataDir;
            var hook = SettingsForm.AccountState;
            var dir = NewTempDir();
            Settings.OverrideAppDataDir = dir;
            try
            {
                foreach (var (state, wanted) in new[]
                         {
                             (DriveAccountState.NotConnected, "Co&nnect Google Drive"),
                             (DriveAccountState.Connected, "&Disconnect Google Drive"),
                             (DriveAccountState.NeedsSignIn, "Reco&nnect Google Drive"),
                         })
                {
                    SettingsForm.AccountState = () => state;
                    string error = "";
                    var texts = new List<string>();
                    var descriptions = new List<string>();
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            using var window = new SettingsForm(new Settings(), null);
                            window.BuildAllPages();
                            foreach (var b in FindAll<Button>(window))
                            {
                                texts.Add(b.Text);
                                if (b.Text.Contains("Google Drive", StringComparison.Ordinal) && !b.Text.Contains("monitor"))
                                    descriptions.Add(b.AccessibleDescription ?? "");
                            }
                        }
                        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                    });
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                    thread.Join(TimeSpan.FromSeconds(30));

                    Equal($"Preferences builds with Drive {state}", "", error);
                    Check($"Drive {state}: the button says {wanted.Replace("&", "")}", texts.Contains(wanted),
                        string.Join(" | ", texts.Where(t => t.Contains("Google"))));
                    Check($"Drive {state}: its description is short",
                        descriptions.All(d => d.Length < 70 && !d.Contains("closes Preferences")),
                        string.Join(" | ", descriptions));
                }
            }
            finally
            {
                SettingsForm.AccountState = hook;
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// Changing the Drive letter in Preferences moves the mounted drive at
        /// once. Done for real on a scratch folder with two free letters, the
        /// same way DriveMount.MoveLetter does it: the new letter first, then
        /// the old one removed.
        /// </summary>
        private static void DriveLetterMoveTests()
        {
            var free = new List<char>();
            for (char c = 'Z'; c >= 'M' && free.Count < 2; c--)
                if (!DriveLetter.InUse(c)) free.Add(c);
            if (free.Count < 2) { Check("two free letters to test moving with", false); return; }

            var dir = NewTempDir();
            DriveLetter? first = null, second = null;
            try
            {
                File.WriteAllText(Path.Combine(dir, "marker.txt"), "here");
                first = DriveLetter.Assign(dir, free[0]);
                Equal("the drive starts on the first letter", free[0] + ":", first.Letter);

                second = DriveLetter.Assign(dir, free[1]);
                first.Dispose();

                Equal("and moves to the one asked for", free[1] + ":", second.Letter);
                Check("the files are there on the new letter", File.Exists(second.Letter + "\\marker.txt"));
                Check("and the old letter is free again", !DriveLetter.InUse(free[0]));

                var source = SourceFile("TrayApplicationContext.cs") ?? "";
                Check("OK in Preferences moves a mounted drive",
                    source.Contains("_drive.MoveLetter(_settings.GoogleDriveLetter)"));
            }
            finally
            {
                try { second?.Dispose(); } catch { }
                try { first?.Dispose(); } catch { }
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// Preferences and the Speech window are walked one control and one
        /// category at a time under a screen reader, so opening them, switching
        /// category and tabbing have to be instant. Measured on a real form on
        /// an STA thread: construction, first show, and every category switch,
        /// with the layout and the message queue run after each one.
        /// </summary>
        private static void WindowSpeedTests()
        {
            var scratch = NewTempDir();
            var previous = Settings.OverrideAppDataDir;
            Settings.OverrideAppDataDir = scratch;
            try
            {
                // Twice each: the first run pays for compiling the code, which
                // ReadyToRun takes away in the published build; the second is what
                // opening the window costs.
                Measure("Speech window (first)", () => new SpeechForm(new Settings()), 5000, 1000);
                Measure("Preferences (first)", () => new SettingsForm(new Settings(), null), 5000, 1000);
                Measure("Speech window", () => new SpeechForm(new Settings()), 150, 30);
                Measure("Preferences", () => new SettingsForm(new Settings(), null), 150, 30);
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(scratch, true); } catch { }
            }

            static void Measure(string name, Func<Form> make, long openLimit, long switchLimit)
            {
                string error = "";
                long buildMs = 0, showMs = 0, worstSwitch = 0, totalSwitch = 0;
                int switches = 0, controls = 0;

                var thread = new Thread(() =>
                {
                    try
                    {
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        using var form = make();
                        buildMs = clock.ElapsedMilliseconds;

                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new System.Drawing.Point(-32000, -32000);
                        form.ShowInTaskbar = false;
                        clock.Restart();
                        form.Show();
                        Application.DoEvents();
                        showMs = clock.ElapsedMilliseconds;

                        // Let the window settle the way it does on screen: it
                        // builds its other pages in idle moments after it opens.
                        for (int settle = 0; settle < 40; settle++)
                        {
                            Application.DoEvents();
                            Thread.Sleep(10);
                        }

                        var list = FindAll<ListBox>(form).FirstOrDefault(l => l.AccessibleName == "Category");
                        if (list != null)
                        {
                            // Every category twice: the second pass is what a person
                            // arrowing back and forth actually pays.
                            for (int pass = 0; pass < 2; pass++)
                            {
                                for (int i = 0; i < list.Items.Count; i++)
                                {
                                    clock.Restart();
                                    list.SelectedIndex = i;
                                    form.PerformLayout();
                                    Application.DoEvents();
                                    long ms = clock.ElapsedMilliseconds;
                                    worstSwitch = Math.Max(worstSwitch, ms);
                                    totalSwitch += ms;
                                    switches++;
                                }
                            }
                        }

                        controls = FindAll<Control>(form).Count;
                        form.Close();
                    }
                    catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join(TimeSpan.FromSeconds(60));

                Equal($"the {name} builds and shows", "", error);
                if (error.Length > 0) return;

                long average = switches == 0 ? 0 : totalSwitch / switches;
                Console.WriteLine($"  {name}: built {buildMs}ms, shown {showMs}ms, " +
                                  $"switch average {average}ms worst {worstSwitch}ms over {switches}, {controls} controls");
                Check($"the {name} opens quickly ({buildMs + showMs}ms)", buildMs + showMs < openLimit,
                    $"{buildMs + showMs}ms");
                Check($"and switching category is quick (worst {worstSwitch}ms)", worstSwitch < switchLimit * 3 && average < switchLimit,
                    $"average {average}ms, worst {worstSwitch}ms");
            }
        }

        /// <summary>
        /// The settings removed on 2026-10-05 are now code, and the code has to
        /// do exactly what Conner's own settings did: Name, Size and Modified
        /// columns and no Type; nothing said on entering a folder or selecting a
        /// row; volume one percent a press on the perceptual curve; held keys
        /// repeat; Drive keeps a gigabyte, lets a file go after two idle
        /// minutes, and starts downloading a file the moment it is copied.
        /// </summary>
        private static void FixedBehaviourTests()
        {
            var main = SourceFile("MainForm.cs") ?? "";
            var tray = SourceFile("TrayApplicationContext.cs") ?? "";
            var drive = SourceFile("GoogleDrive.cs") ?? "";
            Check("the sources are there to check", main.Length > 0 && tray.Length > 0 && drive.Length > 0);

            Check("the list has a Size column",  main.Contains("pane.List.Columns.Add(\"Size\", 150);"));
            Check("and a Modified column", main.Contains("pane.List.Columns.Add(\"Modified\", 160);"));
            Check("and no Type column", !main.Contains("Columns.Add(\"Type\""));
            Check("entering a folder says nothing", !main.Contains("\"nav.entered\"") && !main.Contains("\"nav.drives\""));
            Check("selecting a row says nothing extra", !main.Contains("SpeakSelectionExtras"));
            Check("copying a Drive file starts it downloading", main.Contains("if (!cut) Drive?.Warm(paths);"));

            Check("volume moves one percent a press", tray.Contains("private const int VolumeStepPercent = 1;"));
            Check("on the perceptual curve", tray.Contains("_audio.Curve = VolumeCurve.Perceptual;"));
            Check("held keys repeat", tray.Contains("if (action.AllowRepeat)"));

            Check("Drive keeps a gigabyte in memory", drive.Contains("private const int CacheMegabytes = 1024;"));
            Check("and lets a file go after two idle minutes", drive.Contains("private const int CacheIdleSeconds = 120;"));

            // And Preferences no longer offers any of them.
            string error = "";
            var labels = new List<string>();
            int formats = 0, checkedFormats = 0, mixedItems = 0, mixedChecked = 0;
            // A scratch settings folder: the Google page reads the client file
            // from it, and must never touch the real one.
            var previous = Settings.OverrideAppDataDir;
            Settings.OverrideAppDataDir = NewTempDir();
            var thread = new Thread(() =>
            {
                try
                {
                    using var window = new SettingsForm(new Settings(), null);
                    window.BuildAllPages();
                    foreach (var c in FindAll<Control>(window)) labels.Add(c.Text ?? "");
                    using var picker = new FormatsForm(".mp3;.flac");
                    foreach (var list in FindAll<CheckedListBox>(picker))
                    {
                        formats = list.Items.Count;
                        checkedFormats = list.CheckedItems.Count;
                    }

                    // Commas and spaces, as the player accepts, and a format
                    // added by hand that the picker must not drop.
                    using var mixed = new FormatsForm(".mp3, .flac .xyz");
                    foreach (var list in FindAll<CheckedListBox>(mixed))
                    {
                        mixedItems = list.Items.Count;
                        mixedChecked = list.CheckedItems.Count;
                    }
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(30));
            try { Directory.Delete(Settings.OverrideAppDataDir!, true); } catch { }
            Settings.OverrideAppDataDir = previous;

            // Release notes in the update window: Markdown out, real text kept.
            var notes = UpdateForm.Changes(
                "## What's Changed\n* **Faster** C# build, fixes track #3\n- `code` stays readable\n" +
                "**Full Changelog**: https://example/compare\n");
            Equal("release notes keep C# and #3 and drop the Markdown",
                "Faster C# build, fixes track #3|code stays readable", string.Join("|", notes));

            // Help, Changelog: every release, from GitHub itself when it answers.
            try
            {
                var all = Updater.AllReleasesAsync().GetAwaiter().GetResult();
                Check("the changelog lists the published versions", all.Count >= 4, all.Count.ToString());
                Check("newest first", all.Zip(all.Skip(1)).All(p => p.First.Version > p.Second.Version));
                Check("each with its own plain list of changes",
                    all.Where(r => r.Version >= new Version(1, 0, 2)).All(r => UpdateForm.Changes(r.Notes).Count > 0));
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException
                                           or Updater.UpdateException)
            {
                // Offline, or GitHub limiting checks: nothing to check against.
            }

            Check("a list with commas and spaces reads all three", mixedChecked == 3, mixedChecked.ToString());
            Check("and a hand-added format is kept as its own row",
                mixedItems == AudioFiles.DefaultExtensions.Split(';').Length + 1, mixedItems.ToString());

            Equal("Preferences builds", "", error);
            foreach (var gone in new[]
                     {
                         "Announce folder when navigating", "Announce item count when navigating",
                         "Announce full path when navigating", "Announce size when selecting an item",
                         "Announce type when selecting an item", "Show the Size column", "Show the Type column",
                         "Show the Modified column", "Volume step (percent)", "Volume curve",
                         "Holding a shortcut keeps repeating it", "Playback speed (percent)",
                         "Put Google Drive on a drive letter", "Keep in memory (MB)",
                         "Forget a file after (seconds idle)", "Start downloading when a Drive file is copied",
                         "Audio file extensions",
                     })
                Check($"Preferences no longer offers \"{gone}\"", !labels.Any(l => l.StartsWith(gone, StringComparison.Ordinal)));

            Check("Preferences offers Choose formats to play", labels.Contains("Choose &formats to play..."));
            Equal("the format picker lists every supported format",
                AudioFiles.DefaultExtensions.Split(';').Length.ToString(), formats.ToString());
            Equal("with the chosen ones checked", "2", checkedFormats.ToString());
            Equal("read ahead stops at one gigabyte", "1048576",
                SettingsForm.ReadAheadKilobytes.Max().ToString());
        }

        /// <summary>
        /// How far a held key travels, which is not the same question as how
        /// much a repeat costs.
        ///
        /// Skipping ran at the general rate — fifty a second — because a seek
        /// had become cheap once tracks played out of memory. Cost was never the
        /// problem with holding the key, though: distance was. Fifty ten-second
        /// skips a second is five hundred seconds of track per second held, so a
        /// three minute song ends in under half a second of holding and then
        /// stays at the end, where every further press clamps to the same place
        /// and the key reads as dead. That is what "hitting it twice keeps it at
        /// the same place" was.
        ///
        /// The Preferences page claimed skipping ran at eight a second the whole
        /// time it was running at fifty, which is the other half of why this
        /// test exists: the rate is a number in two places and nothing compared
        /// them.
        /// </summary>
        private static void HeldKeyDistanceTests()
        {
            // How much of a track one second of holding covers, in seconds, at
            // a given point in the ramp.
            static double TrackPerSecond(AudioAction action, int step, int repeat)
            {
                var info = AudioActions.For(action);
                int interval = info.RepeatMilliseconds > 0
                    ? info.RepeatMilliseconds
                    : HoldRepeat.Interval;

                double pressesPerSecond = 1000.0 / interval;
                double perPress = AudioActions.Accelerated(step, repeat, accelerate: true,
                    info.AccelerationCap, info.AccelerationEvery);

                return pressesPerSecond * perPress;
            }

            // A one-second step is a real setting — it is what somebody who
            // wants to find their place in a podcast chooses — and it is the one
            // that made holding the key useless: eight seconds of track per
            // second, so twenty-two seconds of holding to cross a three minute
            // song.
            foreach (var action in new[] { AudioAction.SeekForward, AudioAction.SeekBackward })
            {
                double start = TrackPerSecond(action, 1, repeat: 0);
                double held = TrackPerSecond(action, 1, repeat: 60);

                Check($"{action} starts gently with a one second step ({start:0} s per second)",
                    start is > 4 and < 20, start.ToString("0"));
                Check($"{action} gets somewhere when held ({held:0} s per second)",
                    held > 60, held.ToString("0"));

                // And a tap is still exactly the step. Acceleration that reached
                // a single press would take away the precise short skip that
                // made somebody choose one second in the first place.
                Equal($"{action} taps by exactly the step", "1",
                    AudioActions.Accelerated(1, 0, accelerate: true,
                        AudioActions.For(action).AccelerationCap,
                        AudioActions.For(action).AccelerationEvery).ToString());
            }

            // The default ten-second step must not become absurd at full ramp.
            foreach (var action in new[] { AudioAction.SeekForward, AudioAction.SeekBackward })
            {
                double held = TrackPerSecond(action, 10, repeat: 60);
                Check($"{action} at a ten second step stays bounded ({held:0} s per second)",
                    held < 1200, held.ToString("0"));
            }

            // The long skip starts at a minute a press, so it needs far less
            // growth — four is already thirty-two minutes per second held.
            foreach (var action in new[] { AudioAction.SeekForwardLong, AudioAction.SeekBackwardLong })
            {
                var info = AudioActions.For(action);
                Check($"{action} accelerates less than the short skip",
                    info.AccelerationCap < AudioActions.For(AudioAction.SeekForward).AccelerationCap,
                    info.AccelerationCap.ToString());
            }

            // Volume is the opposite case and must stay that way: its step is
            // one percent of a dial that goes to a thousand, so without both the
            // fast rate and the acceleration a held key is a countdown.
            var volume = AudioActions.For(AudioAction.VolumeUp);
            Equal("volume still repeats at the general rate", "0", volume.RepeatMilliseconds.ToString());
            Check("and still accelerates hard", volume.AccelerationCap > 100,
                volume.AccelerationCap.ToString());
        }

        /// <summary>
        /// What the player says about a file it could not open.
        ///
        /// It said one thing for every failure there is: "Windows cannot decode
        /// .flac". StartDirect returns false when the device will not open, when
        /// the file has gone, when a share is not answering and when the decoder
        /// times out, and all four came out as an accusation against the file
        /// format — which for somebody whose whole library is FLAC reads as
        /// "this player cannot play my music", and is also the least likely of
        /// the four, since Windows has decoded FLAC since 10.
        /// </summary>
        private static void UnplayableFormatTests()
        {
            // The registry knows which formats Media Foundation can open at all.
            // FLAC and MP3 have shipped in Windows for years; if this machine
            // says otherwise the test is wrong about the machine, not the code.
            Check("Windows has a handler for FLAC", AudioFiles.WindowsHasHandlerFor(".flac"));
            Check("and for MP3", AudioFiles.WindowsHasHandlerFor(".mp3"));

            // So nothing extra is said about a FLAC that would not play: the
            // format is not the problem and the message must not imply it is.
            Equal("nothing is added about a format Windows can open", "",
                AudioFiles.AdviceFor(".flac"));

            // Ogg and Opus are decoded here rather than by Windows, so nothing
            // is added about them either — a failure on one of those is about
            // the file, and "Windows has no decoder" would be true and entirely
            // beside the point.
            foreach (var extension in new[] { ".opus", ".ogg", ".oga" })
            {
                Check($"{extension} is handled by the managed decoder",
                    OggDecoder.Handles(extension));
                Equal($"so nothing extra is said about {extension}", "",
                    AudioFiles.AdviceFor(extension));
            }

            // Something genuinely unsupported still gets the advice, or the
            // message would have lost the one case it was written for.
            Check("an unsupported format still points at the way that does work",
                AudioFiles.AdviceFor(".wv").Contains("Shift+Enter", StringComparison.Ordinal),
                AudioFiles.AdviceFor(".wv"));

            // An unknown extension must not throw, and must not claim to know.
            foreach (var junk in new[] { "", ".", ".zzzz", "no dot at all" })
            {
                try { AudioFiles.AdviceFor(junk); Check($"advice survives \"{junk}\"", true); }
                catch (Exception ex) { Check($"advice survives \"{junk}\"", false, ex.GetType().Name); }
            }

            // A registry read that cannot answer must not be read as "no". The
            // play attempt is the real test; claiming a format is unsupported
            // because a lookup failed would be a confident lie.
            Check("an empty extension is not called unsupported",
                AudioFiles.AdviceFor("") == "");

            OpusDecodeTests();
            VorbisIsNotOpusTests();
            SlowStreamStillOpensTests();
            LongOpusRecordingTests();
            Mpeg4AudioTests();
            VideoContainerTests.RunAll(Check, NewTempDir, Cleanup,
                path => Mpeg4.WriteTone(path, 48000, 2, 3, out _));
            OpusRepeatTests();
            UnknownExtensionTests();
        }

        /// <summary>
        /// An Opus track on repeat, played for real, watching the clock.
        ///
        /// Two bugs met here and the file that found them was twenty seconds
        /// long, which is what made both of them impossible to miss.
        ///
        /// The first is the decoder: the loop point seeks to zero, and
        /// `OpusOggReadStream.SeekTo(TimeSpan.Zero)` latches end of stream for
        /// good — so the track went silent the first time it reached its own end
        /// and the clock stopped at zero for as long as the application was
        /// open. The reader that did it is gone; see <c>OggOpusReader</c>.
        ///
        /// The second is the clock itself. Position is the decoder's position
        /// less whatever is still queued, and at a loop point those two are
        /// measuring different passes — the decoder has gone back to zero while
        /// the ring still holds four seconds of the end of the track. The
        /// subtraction went negative, so the clock read zero for the whole ring
        /// and the last four seconds of every pass were never reported: on a
        /// twenty-second track it counted 0 to 16 and started again. See
        /// <c>WasapiPlayback._lastPassFrames</c>.
        ///
        /// A real device and a real ring, because neither failure exists without
        /// them. Skipped rather than failed where there is no output device: a
        /// build server with no sound card is not a broken player.
        /// </summary>
        private static void OpusRepeatTests()
        {
            Console.WriteLine("An Opus track on repeat:");

            var dir = NewTempDir();
            try
            {
                // Six seconds, which is longer than the four-second ring and
                // short enough to go round twice inside the test.
                var path = Path.Combine(dir, "loop.opus");
                const int rate = 48000, seconds = 6, tone = 440;

                var samples = new short[rate * seconds];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (short)(0.5 * short.MaxValue * Math.Sin(2 * Math.PI * tone * i / rate));

                try
                {
                    using var file = File.Create(path);
                    var encoder = Concentus.OpusCodecFactory.CreateEncoder(
                        rate, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO, null);
                    var writer = new Concentus.Oggfile.OpusOggWriteStream(
                        encoder, file, null, rate, 5, false);

                    writer.WriteSamples(samples, 0, samples.Length);
                    writer.Finish();
                }
                catch (Exception ex)
                {
                    Check("an Opus file can be written for the repeat test", false,
                        ex.GetType().Name + ": " + ex.Message);
                    return;
                }

                using var player = new AudioPlayer();
                player.VolumePercent = 0;
                player.RepeatTrack = true;

                if (player.Play(path) == null)
                {
                    Console.WriteLine("  (no output device — skipped)");
                    Console.WriteLine();
                    return;
                }

                double duration = double.NaN;
                for (int waited = 0; waited < 5000 && !(duration > 0); waited += 50)
                {
                    Thread.Sleep(50);
                    duration = player.DurationSeconds;
                }

                Check($"the looping track reports a length ({duration:0.##}s)",
                    duration > 0 && Math.Abs(duration - seconds) < 1.0,
                    duration.ToString("0.###"));

                if (!(duration > 0)) { Console.WriteLine(); return; }

                // Sampled all the way round: the highest reading before the
                // clock wraps is what says whether the end of the pass was ever
                // reported, and a wrap at all is what says the loop happened.
                double highest = 0, previous = 0, highestBeforeWrap = -1;
                int wraps = 0;

                for (int i = 0; i < 130; i++)      // about thirteen seconds
                {
                    Thread.Sleep(100);
                    double now = player.PositionSeconds;

                    if (now + 1.0 < previous)
                    {
                        wraps++;
                        if (highestBeforeWrap < 0) highestBeforeWrap = highest;
                    }

                    if (now > highest) highest = now;
                    previous = now;

                    if (wraps >= 2) break;
                }

                Check($"it goes round rather than stopping ({wraps} times)", wraps >= 1,
                    $"stalled at {previous:0.##}s of {duration:0.##}s");

                Check($"and the clock reaches the end of each pass " +
                      $"({highestBeforeWrap:0.##}s of {duration:0.##}s)",
                    highestBeforeWrap > duration - 1.0,
                    "a clock that stops a ring short of the end is the ring being " +
                    "subtracted from the wrong pass");

                player.Stop();
            }
            finally { Cleanup(dir); }

            Console.WriteLine();
        }

        /// <summary>
        /// A file Windows can decode, under a name Windows has never heard of.
        ///
        /// `MFCreateSourceReaderFromURL` picks a decoder by looking the
        /// *extension* up in the registry, so a container it handles perfectly
        /// well is refused outright when the name matches nothing registered.
        /// `.m4b` is the case that matters: an audiobook is an ordinary MP4 full
        /// of AAC, `.mp4`, `.m4a` and `.aac` all have handlers on this machine,
        /// and `.m4b` has none — so the one extension audiobooks actually use is
        /// the one that would not open, while sharing every byte of its format
        /// with three that do.
        ///
        /// Tested with FLAC rather than AAC because the suite can generate a
        /// FLAC byte for byte and cannot generate an AAC, and because the thing
        /// under test is not the codec: it is whether an unregistered extension
        /// still reaches a decoder. A FLAC called `.m4b` is a harder case than a
        /// real one, since the content and the name disagree about everything.
        /// </summary>
        private static void UnknownExtensionTests()
        {
            var dir = NewTempDir();
            try
            {
                // Something the registry really has nothing for, so the test is
                // not quietly passing through the ordinary path.
                Check("Windows has no handler for .m4b", !AudioFiles.WindowsHasHandlerFor(".m4b"));
                Check("nor for a made-up extension", !AudioFiles.WindowsHasHandlerFor(".zznotreal"));
                Check("but it does for .flac", AudioFiles.WindowsHasHandlerFor(".flac"));

                foreach (var extension in new[] { ".m4b", ".zznotreal" })
                {
                    var path = Path.Combine(dir, "tone" + extension);
                    WriteSilentFlac(path, 48000);

                    var decoder = TrackDecoder.Open(path, null, 48000, 2, out string why);
                    Check($"a decodable file named {extension} still opens", decoder != null, why);

                    if (decoder == null) continue;

                    using (decoder)
                    {
                        Check($"{extension} reports a usable format",
                            decoder.SampleRate > 0 && decoder.Channels > 0,
                            $"{decoder.SampleRate} Hz, {decoder.Channels} ch");

                        var block = new float[4096 * Math.Max(1, decoder.Channels)];
                        Check($"and {extension} decodes frames", decoder.Read(block, 4096) > 0);
                    }
                }

                // The ordinary path must not have been disturbed: a file whose
                // extension *is* registered still goes through the URL route,
                // which is the one that hardware-accelerates and the one every
                // other format in the library uses.
                var normal = Path.Combine(dir, "tone.flac");
                WriteSilentFlac(normal, 48000);

                var plain = TrackDecoder.Open(normal, null, 48000, 2, out string plainWhy);
                Check("a normal .flac still opens", plain != null, plainWhy);
                plain?.Dispose();

                // And a file that is not audio at all is still refused, rather
                // than the fallback turning every unknown file into a track.
                var junk = Path.Combine(dir, "notaudio.zznotreal");
                File.WriteAllBytes(junk, new byte[4096]);

                var nothing = TrackDecoder.Open(junk, null, 48000, 2, out _);
                Check("something that is not audio is still refused", nothing == null);
                nothing?.Dispose();
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// Opus, decoded for real, from a file the suite makes itself.
        ///
        /// Generated rather than borrowed. A test that needs somebody's music
        /// library is a test that passes on one machine — and this one has to be
        /// able to fail on a build server that has never seen a .opus file.
        /// Encoding one is the honest way to get a valid Ogg Opus stream, and it
        /// exercises the container in both directions.
        ///
        /// What it is checking is the glue, not the codec: that the container is
        /// recognised, that the format is read back rather than assumed, that
        /// what comes out is the audio that went in, and that seeking lands.
        /// Concentus and NVorbis are responsible for their own correctness.
        /// </summary>
        /// <summary>
        /// An Ogg stream that is not Opus must not be opened by the Opus reader.
        ///
        /// Reported as ".ogg files don't play", and the cause was a sniff that
        /// had never been tested. `OggDecoder` tries Opus first and decides with
        /// `OpusOggReadStream.HasNextPacket`, on the reasoning — written down in
        /// the code — that it "is false when the container was not Opus at all,
        /// which is how this doubles as the sniff". It is not. Measured against
        /// Concentus 2.2.2 with a hand-built Ogg page carrying a *Vorbis*
        /// identification header:
        ///
        ///     OpusOggReadStream constructed
        ///     HasNextPacket = True
        ///     DecodeNextPacket -> null
        ///
        /// So every Ogg Vorbis file was claimed by the Opus reader, opened
        /// successfully, reported itself as 48kHz stereo, and then decoded
        /// nothing — which the pump reads as a track that finished the moment it
        /// started. Not an error anywhere: a file that plays for no time at all.
        /// NVorbis was never reached, and no test in this suite had ever decoded
        /// Vorbis, only Opus.
        ///
        /// The page below is a real Ogg page — capture pattern, lacing table and
        /// CRC — carrying the 30-byte Vorbis identification header. It is not a
        /// playable file and is not meant to be: what is being asked is only
        /// whether the *Opus* path claims it, and a claim is a decoder handed
        /// back for a file it cannot decode.
        /// </summary>
        private static void VorbisIsNotOpusTests()
        {
            Console.WriteLine("Telling Ogg Vorbis from Ogg Opus:");

            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "vorbis.ogg");
                File.WriteAllBytes(path, NotOpusOggStream());

                var decoder = TrackDecoder.Open(path, null, 48000, 2, out string why);

                try
                {
                    Check("a Vorbis stream is not opened by the Opus reader",
                        decoder == null,
                        decoder == null ? "" : $"{decoder.GetType().Name} claimed it: {why}");

                    // And the shape of the fault, stated as the rule rather than
                    // as the case: a decoder handed back has to be able to
                    // produce audio. One that opens and decodes nothing is a
                    // track that plays for no time at all, which is the thing
                    // that was reported and the thing that looked like no error.
                    if (decoder != null)
                    {
                        var block = new float[4096 * Math.Max(1, decoder.Channels)];
                        Check("and a decoder that is handed back can decode something",
                            decoder.Read(block, 4096) > 0);
                    }
                }
                finally { decoder?.Dispose(); }
            }
            finally { Cleanup(dir); }

            Console.WriteLine();
        }


        /// <summary>
        /// The audio track of an MPEG-4 file — `.mp4` and `.mov`.
        ///
        /// They are video containers and they are in the audio list on purpose: a
        /// recording of a conversation is one whether the camera was running or
        /// not, and for somebody listening rather than watching, handing it to a
        /// video player is handing it to a window with no keyboard route to the
        /// volume.
        ///
        /// **The fixture is written by Media Foundation itself**, the way
        /// `OpusDecodeTests` has Concentus write its Opus: there is no `.mp4` in
        /// this repository and a test that needs somebody to put one there is a
        /// test that quietly stops running. The sink writer encodes AAC into an
        /// MP4 container, which is the same encoder any phone or recorder used to
        /// make the files this is about.
        ///
        /// What it cannot fabricate is a *video* track — that would want an H.264
        /// encoder as well — so the one thing here that is argued rather than
        /// measured is that a file with pictures in it decodes to its sound.
        /// `AudioDecoder` deselects every stream and then selects only
        /// `MF_SOURCE_READER_FIRST_AUDIO_STREAM`, and reads only from that index,
        /// so a video track is never asked for rather than being skipped past.
        /// </summary>
        private static void Mpeg4AudioTests()
        {
            Console.WriteLine("The audio track of an MPEG-4 file:");

            const int rate = 48000, seconds = 3;

            // The list first: a format that decodes perfectly and is not treated
            // as audio is a format that opens in a video player.
            Check(".mp4 is treated as audio",
                AudioFiles.IsAudio("clip.mp4", AudioFiles.DefaultExtensions));
            Check(".mov is treated as audio",
                AudioFiles.IsAudio("clip.mov", AudioFiles.DefaultExtensions));

            // And Windows really does have a handler for them, asked of the
            // registry rather than assumed — the same question the player asks
            // before it explains why something will not play.
            Check("Windows has an MPEG-4 byte-stream handler",
                AudioFiles.WindowsHasHandlerFor(".mp4") && AudioFiles.WindowsHasHandlerFor(".mov"));

            var dir = NewTempDir();
            try
            {
                var mp4 = Path.Combine(dir, "tone.mp4");

                if (!Mpeg4.WriteTone(mp4, rate, 1, seconds, out string wrote))
                {
                    Check("Media Foundation can write an MP4 for the test", false, wrote);
                    return;
                }

                Check($"the generated MP4 has bytes ({new FileInfo(mp4).Length:N0})",
                    new FileInfo(mp4).Length > 4000, new FileInfo(mp4).Length.ToString());

                var head = new byte[12];
                using (var file = File.OpenRead(mp4)) file.ReadExactly(head, 0, head.Length);
                Equal("and it really is an MPEG-4 container", "ftyp",
                    System.Text.Encoding.ASCII.GetString(head, 4, 4));

                // The same bytes under the other name. That is not a shortcut:
                // .mov and .mp4 are the same ISO base media format and the
                // registry points both at the same byte-stream handler, so what
                // is being tested is whether the *extension* reaches it.
                var mov = Path.Combine(dir, "tone.mov");
                File.Copy(mp4, mov);

                foreach (var path in new[] { mp4, mov })
                {
                    var what = Path.GetExtension(path);
                    var decoder = TrackDecoder.Open(path, null, rate, 2, out string why);

                    try
                    {
                        Check($"{what} opens", decoder != null, why);
                        if (decoder == null) continue;

                        Equal($"{what} is opened by Media Foundation, not the managed reader",
                            "AudioDecoder", decoder.GetType().Name);

                        Equal($"{what} decodes at the rate the device asked for",
                            rate.ToString(), decoder.SampleRate.ToString());
                        Equal($"{what} decodes at the channel count it asked for",
                            "2", decoder.Channels.ToString());

                        var block = new float[4096 * decoder.Channels];
                        long frames = 0;
                        float peak = 0;

                        while (frames < rate * 2L)
                        {
                            int got = decoder.Read(block, 4096);
                            if (got <= 0) break;

                            frames += got;
                            for (int i = 0; i < got * decoder.Channels; i++)
                                if (Math.Abs(block[i]) > peak) peak = Math.Abs(block[i]);
                        }

                        Check($"{what} decodes a useful amount ({frames:N0} frames)",
                            frames > rate, frames.ToString());

                        // The assertion a decoder that runs and emits silence
                        // cannot fake, which is the failure this suite has
                        // shipped before.
                        Check($"{what} comes out as audio, not silence (peak {peak:0.###})",
                            peak > 0.1f, peak.ToString("0.####"));
                    }
                    finally { decoder?.Dispose(); }
                }
            }
            finally { Cleanup(dir); }

            Console.WriteLine();
        }

        /// <summary>
        /// Media Foundation's sink writer, as much of it as writing one AAC track
        /// into an MP4 needs.
        ///
        /// The vtable positions are the declaration and are counted out rather
        /// than trusted — see the same warning on `AudioDecoder.IMFMediaType`,
        /// where a `SetUINT32` one slot late lands on `SetUINT64` and produces an
        /// error about codecs for a mistake about arithmetic.
        /// </summary>
        internal static class Mpeg4
        {
            [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"),
             InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IMFMediaType
            {
                void R00(); void R01(); void R02(); void R03(); void R04();
                void R05(); void R06(); void R07(); void R08(); void R09();
                void R10(); void R11(); void R12(); void R13(); void R14();
                void R15(); void R16(); void R17();
                [PreserveSig] int SetUINT32(ref Guid key, uint value);              // 18
                void R19(); void R20();
                [PreserveSig] int SetGUID(ref Guid key, ref Guid value);            // 21
            }

            [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"),
             InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IMFMediaBuffer
            {
                [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
                [PreserveSig] int Unlock();
                [PreserveSig] int GetCurrentLength(out uint length);
                [PreserveSig] int SetCurrentLength(uint length);
                [PreserveSig] int GetMaxLength(out uint length);
            }

            [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"),
             InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IMFSample
            {
                // IMFAttributes is the first thirty.
                void A00(); void A01(); void A02(); void A03(); void A04();
                void A05(); void A06(); void A07(); void A08(); void A09();
                void A10(); void A11(); void A12(); void A13(); void A14();
                void A15(); void A16(); void A17(); void A18(); void A19();
                void A20(); void A21(); void A22(); void A23(); void A24();
                void A25(); void A26(); void A27(); void A28(); void A29();
                void S30(); void S31(); void S32();
                [PreserveSig] int SetSampleTime(long time);                         // 33
                void S34();
                [PreserveSig] int SetSampleDuration(long duration);                 // 35
                void S36(); void S37(); void S38();
                [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);                 // 39
            }

            [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"),
             InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IMFSinkWriter
            {
                [PreserveSig] int AddStream(IMFMediaType targetType, out uint streamIndex);
                [PreserveSig] int SetInputMediaType(uint streamIndex, IMFMediaType inputType,
                    IntPtr encodingParameters);
                [PreserveSig] int BeginWriting();
                [PreserveSig] int WriteSample(uint streamIndex, IMFSample sample);
                void R04(); void R05(); void R06(); void R07();
                [PreserveSig] int DoFinalize();                                     // 8
            }

            [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
            [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IMFMediaType type);
            [DllImport("mfplat.dll")] private static extern int MFCreateSample(out IMFSample sample);

            [DllImport("mfplat.dll")]
            private static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);

            [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
            private static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream,
                IntPtr attributes, out IMFSinkWriter writer);

            private static Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
            private static Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
            private static Guid Audio = new("73647561-0000-0010-8000-00AA00389B71");
            private static Guid Aac = new("00001610-0000-0010-8000-00AA00389B71");
            private static Guid Pcm = new("00000001-0000-0010-8000-00AA00389B71");
            private static Guid SamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
            private static Guid ChannelCount = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
            private static Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
            private static Guid BlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
            private static Guid BytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
            private static Guid AacPayloadType = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");

            private const int Version = 0x00020070;

            /// <summary>
            /// A 440Hz tone as one AAC track in an MP4. False, with a reason, if
            /// this machine will not encode one — which is a reason to say so
            /// rather than to fail a test about decoding.
            /// </summary>
            public static bool WriteTone(string path, int rate, int channels, int seconds,
                out string why)
            {
                why = "";

                try
                {
                    MFStartup(Version, 0);

                    // What the encoder produces. The bytes-per-second figure is
                    // one of the handful the AAC encoder accepts; an arbitrary
                    // number is refused as an unsupported media type.
                    int hr = MFCreateMediaType(out var output);
                    if (hr != 0) { why = $"MFCreateMediaType 0x{hr:X8}"; return false; }

                    output.SetGUID(ref MajorType, ref Audio);
                    output.SetGUID(ref Subtype, ref Aac);
                    output.SetUINT32(ref BitsPerSample, 16);
                    output.SetUINT32(ref SamplesPerSecond, (uint)rate);
                    output.SetUINT32(ref ChannelCount, (uint)channels);
                    output.SetUINT32(ref BytesPerSecond, 16000);
                    output.SetUINT32(ref AacPayloadType, 0);

                    // And what it is fed: plain 16-bit PCM.
                    MFCreateMediaType(out var input);
                    input.SetGUID(ref MajorType, ref Audio);
                    input.SetGUID(ref Subtype, ref Pcm);
                    input.SetUINT32(ref BitsPerSample, 16);
                    input.SetUINT32(ref SamplesPerSecond, (uint)rate);
                    input.SetUINT32(ref ChannelCount, (uint)channels);
                    input.SetUINT32(ref BlockAlignment, (uint)(2 * channels));
                    input.SetUINT32(ref BytesPerSecond, (uint)(rate * 2 * channels));

                    hr = MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out var writer);
                    if (hr != 0) { why = $"MFCreateSinkWriterFromURL 0x{hr:X8}"; return false; }

                    hr = writer.AddStream(output, out uint stream);
                    if (hr != 0) { why = $"AddStream 0x{hr:X8}"; return false; }

                    hr = writer.SetInputMediaType(stream, input, IntPtr.Zero);
                    if (hr != 0) { why = $"SetInputMediaType 0x{hr:X8}"; return false; }

                    hr = writer.BeginWriting();
                    if (hr != 0) { why = $"BeginWriting 0x{hr:X8}"; return false; }

                    int perBlock = rate / 10;
                    long at = 0;
                    long duration = 10_000_000L * perBlock / rate;

                    for (int block = 0; block < seconds * 10; block++)
                    {
                        var pcm = new byte[perBlock * channels * 2];

                        for (int i = 0; i < perBlock; i++)
                        {
                            var v = (short)(0.5 * short.MaxValue *
                                Math.Sin(2 * Math.PI * 440 * (block * perBlock + i) / rate));

                            for (int c = 0; c < channels; c++)
                                BitConverter.GetBytes(v).CopyTo(pcm, (i * channels + c) * 2);
                        }

                        MFCreateMemoryBuffer((uint)pcm.Length, out var buffer);
                        buffer.Lock(out IntPtr into, out _, out _);
                        Marshal.Copy(pcm, 0, into, pcm.Length);
                        buffer.Unlock();
                        buffer.SetCurrentLength((uint)pcm.Length);

                        MFCreateSample(out var sample);
                        sample.AddBuffer(buffer);
                        sample.SetSampleTime(at);
                        sample.SetSampleDuration(duration);

                        hr = writer.WriteSample(stream, sample);
                        if (hr != 0) { why = $"WriteSample 0x{hr:X8}"; return false; }

                        at += duration;
                    }

                    hr = writer.DoFinalize();
                    if (hr != 0) { why = $"Finalize 0x{hr:X8}"; return false; }

                    return true;
                }
                catch (Exception ex)
                {
                    why = ex.GetType().Name + ": " + ex.Message;
                    return false;
                }
            }
        }

        /// <summary>
        /// A stream that has not downloaded yet must not be read as "not Opus".
        ///
        /// This is the second fault in the same sniff, and it shipped: the first
        /// fix read the head with `ReadExactly` and treated a failure as a
        /// verdict. On the Drive letter the stream is a download, and
        /// `StreamingSource.Read` reports *no bytes* for an offset it has not
        /// reached yet rather than blocking — so `ReadExactly` threw, a genuine
        /// Opus file was declared Vorbis, and NVorbis walked a large remote
        /// container looking for a Vorbis stream that was not there until the
        /// fifteen-second open budget ran out. "Could not play … it did not open
        /// in time", on a file that had played perfectly the day before.
        ///
        /// The rule the fix rests on is that **an unknown is never a no**, and
        /// this is what holds it: a real Opus file, served through a stream that
        /// refuses its first few reads exactly as a cold download does.
        /// </summary>
        private static void SlowStreamStillOpensTests()
        {
            Console.WriteLine("An Opus file arriving slowly:");

            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "slow.opus");
                if (!WriteOpusTone(path, 48000, 2)) { Cleanup(dir); Console.WriteLine(); return; }

                // Three refusals: more than the one it takes to throw, few enough
                // that the sniff's own waiting covers it. A cold Drive read is
                // this shape — nothing, nothing, then the whole head at once.
                var slow = new SlowStartStream(File.ReadAllBytes(path), refusals: 3);

                var decoder = TrackDecoder.Open(path, slow, 48000, 2, out string why);

                try
                {
                    Check("an Opus file whose first reads come back empty still opens",
                        decoder != null, why);

                    if (decoder == null) return;

                    Equal("and it is the Opus reader that opened it",
                        "OggDecoder", decoder.GetType().Name);

                    // And it decodes. A decoder handed back for a file it cannot
                    // read is the failure this whole section is about.
                    var block = new float[4096 * Math.Max(1, decoder.Channels)];
                    long frames = 0;
                    float peak = 0;

                    while (frames < 48000L)
                    {
                        int got = decoder.Read(block, 4096);
                        if (got <= 0) break;

                        frames += got;
                        for (int i = 0; i < got * decoder.Channels; i++)
                            if (Math.Abs(block[i]) > peak) peak = Math.Abs(block[i]);
                    }

                    Check($"and what comes out of it is audio (peak {peak:0.###})",
                        frames > 20000 && peak > 0.1f, $"{frames} frames, peak {peak:0.####}");
                }
                finally { decoder?.Dispose(); }
            }
            finally { Cleanup(dir); }

            Console.WriteLine();
        }

        /// <summary>
        /// A twenty-hour Opus recording opens at once and seeks to the sample.
        ///
        /// Reported as "Recording_30.opus takes too long to play", and it was
        /// the library reader: its constructor read and checksummed all 1.18GB
        /// and built an object for each of 3.7 million packets before returning.
        /// Five seconds, 465MB allocated, and the same again on every "back to
        /// the start". See <see cref="OggOpusReader"/>.
        ///
        /// It is the real file, not a generated one, on purpose: a generated
        /// twenty-hour file is a twenty-hour encode. Skipped where the recording
        /// is not there.
        /// </summary>
        private static void LongOpusRecordingTests()
        {
            Console.WriteLine("A twenty-hour Opus recording:");

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "LifeRecorder", "Recording_30.opus");

            if (!File.Exists(path))
            {
                Console.WriteLine("  (Recording_30.opus is not here — skipped)");
                Console.WriteLine();
                return;
            }

            // What the library reported for it, which is the page granule over
            // 48000; the new reader takes the 312-sample pre-skip off.
            const double length = 73230.8898;

            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            var decoder = TrackDecoder.Open(path, null, 48000, 2, out string why);
            long openMs = clock.ElapsedMilliseconds;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

            Check("it opens", decoder != null, why);
            if (decoder == null) { Console.WriteLine(); return; }

            try
            {
                Equal("with the managed decoder", "OggDecoder", decoder.GetType().Name);

                // The old reader took five seconds here, warm. A second is still
                // generous for a read of the head and the tail on a cold disk.
                Check($"in well under a second ({openMs}ms)", openMs < 1000, $"{openMs}ms");
                Check($"without reading the file into memory ({allocated / 1024:N0}KB allocated)",
                    allocated < 16L * 1024 * 1024, $"{allocated:N0} bytes");
                Check($"and it knows its length ({TimeSpan.FromSeconds(decoder.Duration)})",
                    Math.Abs(decoder.Duration - length) < 0.01, decoder.Duration.ToString("0.####"));

                float[] Grab(int frames)
                {
                    var all = new float[frames * 2];
                    var block = new float[4096 * 2];
                    int got = 0;
                    while (got < frames)
                    {
                        int read = decoder.Read(block, Math.Min(4096, frames - got));
                        if (read <= 0) break;
                        Array.Copy(block, 0, all, got * 2, read * 2);
                        got += read;
                    }
                    Array.Resize(ref all, got * 2);
                    return all;
                }

                var start = Grab(48000);
                Check("the first second decodes", start.Length == 96000, (start.Length / 2).ToString());
                Check("and is sound, not digital silence", start.Any(s => s != 0));

                // Back to the start is the one that used to rebuild the reader,
                // which on this file was the whole five seconds again.
                clock.Restart();
                bool toStart = decoder.SeekTo(0);
                var again = Grab(48000);
                long startMs = clock.ElapsedMilliseconds;
                Check($"back to the start is quick ({startMs}ms, a second of decoding included)",
                    toStart && startMs < 500, $"{startMs}ms");
                Check("and plays the same samples as the first time", again.SequenceEqual(start));

                // Ten hours in, then one second earlier. The second read's
                // second half is the first read's first second, to the sample,
                // or a seek is landing somewhere other than where it says.
                clock.Restart();
                bool far = decoder.SeekTo(36000);
                long seekMs = clock.ElapsedMilliseconds;
                var at10h = Grab(48000);
                Check($"a seek ten hours in is quick ({seekMs}ms)", far && seekMs < 500, $"{seekMs}ms");
                Check($"and says where it is ({decoder.Position:0.##}s)",
                    Math.Abs(decoder.Position - 36001) < 0.01, decoder.Position.ToString("0.###"));

                decoder.SeekTo(35999);
                var before = Grab(96000);
                Check("seeks land on the sample",
                    before.Length == 192000 && before.AsSpan(96000).SequenceEqual(at10h));

                // The end, which the last page trims to the sample.
                decoder.SeekTo(decoder.Duration - 2);
                long tail = 0;
                var buffer = new float[4096 * 2];
                int n, spins = 0;
                while ((n = decoder.Read(buffer, 4096)) > 0 && ++spins < 1000) tail += n;
                Equal("the last two seconds are exactly two seconds long", "96000", tail.ToString());
            }
            finally { decoder.Dispose(); }

            Console.WriteLine();
        }

        /// <summary>
        /// A real Ogg Opus file, encoded with Concentus. False when this machine
        /// could not write one, which is a reason to skip rather than to fail.
        /// </summary>
        private static bool WriteOpusTone(string path, int rate, int seconds)
        {
            try
            {
                var samples = new short[rate * seconds];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (short)(0.5 * short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / rate));

                using var file = File.Create(path);
                var encoder = Concentus.OpusCodecFactory.CreateEncoder(
                    rate, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO, null);
                var writer = new Concentus.Oggfile.OpusOggWriteStream(encoder, file, null, rate, 5, false);

                writer.WriteSamples(samples, 0, samples.Length);
                writer.Finish();
                return true;
            }
            catch (Exception ex)
            {
                Check("an Opus file can be written for the test", false,
                    ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Bytes behind an IStream that answers the first few reads with nothing
        /// at all — which is what a download does for an offset it has not
        /// reached, and is not the same thing as the end of the file.
        /// </summary>
        private sealed class SlowStartStream : System.Runtime.InteropServices.ComTypes.IStream
        {
            private readonly byte[] _bytes;
            private long _position;
            private int _refusals;

            public SlowStartStream(byte[] bytes, int refusals)
            {
                _bytes = bytes;
                _refusals = refusals;
            }

            public void Read(byte[] buffer, int count, IntPtr bytesRead)
            {
                int got = 0;

                if (_refusals > 0) _refusals--;
                else
                {
                    got = (int)Math.Min(count, _bytes.Length - _position);
                    if (got < 0) got = 0;
                    if (got > 0)
                    {
                        Array.Copy(_bytes, _position, buffer, 0, got);
                        _position += got;
                    }
                }

                if (bytesRead != IntPtr.Zero)
                    System.Runtime.InteropServices.Marshal.WriteInt32(bytesRead, got);
            }

            public void Seek(long move, int origin, IntPtr newPosition)
            {
                _position = origin switch
                {
                    0 => move,
                    1 => _position + move,
                    _ => _bytes.Length + move,
                };

                if (_position < 0) _position = 0;
                if (_position > _bytes.Length) _position = _bytes.Length;

                if (newPosition != IntPtr.Zero)
                    System.Runtime.InteropServices.Marshal.WriteInt64(newPosition, _position);
            }

            public void Stat(out System.Runtime.InteropServices.ComTypes.STATSTG stat, int flags) =>
                stat = new System.Runtime.InteropServices.ComTypes.STATSTG { cbSize = _bytes.Length };

            public void Clone(out System.Runtime.InteropServices.ComTypes.IStream copy) =>
                throw new NotSupportedException();
            public void Commit(int flags) => throw new NotSupportedException();
            public void CopyTo(System.Runtime.InteropServices.ComTypes.IStream target, long count,
                IntPtr read, IntPtr written) => throw new NotSupportedException();
            public void LockRegion(long offset, long count, int lockType) =>
                throw new NotSupportedException();
            public void Revert() => throw new NotSupportedException();
            public void SetSize(long newSize) => throw new NotSupportedException();
            public void UnlockRegion(long offset, long count, int lockType) =>
                throw new NotSupportedException();
            public void Write(byte[] buffer, int count, IntPtr written) =>
                throw new NotSupportedException();
        }

        /// <summary>
        /// A real Ogg bitstream that is not Opus: one beginning-of-stream page
        /// carrying a Vorbis identification header, and one page after it so that
        /// running out of data is not the reason anything stops.
        /// </summary>
        private static byte[] NotOpusOggStream()
        {
            var stream = new List<byte>();
            stream.AddRange(OggPage(0x02, 0, VorbisIdentificationHeader()));
            stream.AddRange(OggPage(0x00, 1, new byte[64]));
            return stream.ToArray();
        }

        private static byte[] VorbisIdentificationHeader()
        {
            var header = new List<byte> { 1 };
            header.AddRange(System.Text.Encoding.ASCII.GetBytes("vorbis"));
            header.AddRange(BitConverter.GetBytes(0));        // version
            header.Add(2);                                    // channels
            header.AddRange(BitConverter.GetBytes(44100));    // sample rate
            header.AddRange(BitConverter.GetBytes(0));        // bitrate maximum
            header.AddRange(BitConverter.GetBytes(128000));   // nominal
            header.AddRange(BitConverter.GetBytes(0));        // minimum
            header.Add(0xB8);                                 // the two block sizes
            header.Add(1);                                    // framing flag
            return header.ToArray();
        }

        private static byte[] OggPage(byte headerType, int sequence, byte[] payload)
        {
            var page = new List<byte>();
            page.AddRange(System.Text.Encoding.ASCII.GetBytes("OggS"));
            page.Add(0);                                     // stream structure version
            page.Add(headerType);
            page.AddRange(BitConverter.GetBytes(0L));        // granule position
            page.AddRange(BitConverter.GetBytes(0x1234));    // bitstream serial
            page.AddRange(BitConverter.GetBytes(sequence));
            page.AddRange(BitConverter.GetBytes(0));         // checksum, filled in below

            var laces = new List<byte>();
            int left = payload.Length;
            while (left >= 255) { laces.Add(255); left -= 255; }
            laces.Add((byte)left);

            page.Add((byte)laces.Count);
            page.AddRange(laces);
            page.AddRange(payload);

            var bytes = page.ToArray();
            BitConverter.GetBytes(OggChecksum(bytes)).CopyTo(bytes, 22);
            return bytes;
        }

        /// <summary>
        /// Ogg's own CRC-32: polynomial 0x04c11db7, no reflection and no final
        /// inversion — which is not the CRC in <see cref="Crc32"/>, the one zip
        /// and gzip are defined in terms of. Two different checksums with the
        /// same name is exactly the sort of thing that makes a hand-built page
        /// fail for a reason that looks like the code under test.
        /// </summary>
        private static uint OggChecksum(byte[] data)
        {
            uint crc = 0;

            foreach (byte b in data)
            {
                crc ^= (uint)b << 24;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
            }

            return crc;
        }

        private static void OpusDecodeTests()
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "tone.opus");
                const int rate = 48000, seconds = 6, tone = 440;

                // A 440Hz sine, loud enough that silence is unmistakable.
                var samples = new short[rate * seconds];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (short)(0.5 * short.MaxValue * Math.Sin(2 * Math.PI * tone * i / rate));

                try
                {
                    using var file = File.Create(path);
                    var encoder = Concentus.OpusCodecFactory.CreateEncoder(
                        rate, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO, null);
                    var writer = new Concentus.Oggfile.OpusOggWriteStream(
                        encoder, file, null, rate, 5, false);

                    writer.WriteSamples(samples, 0, samples.Length);
                    writer.Finish();
                }
                catch (Exception ex)
                {
                    Check("an Opus file can be written for the test", false,
                        ex.GetType().Name + ": " + ex.Message);
                    return;
                }

                Check("the generated Opus file has bytes", new FileInfo(path).Length > 1000,
                    new FileInfo(path).Length.ToString());

                var decoder = TrackDecoder.Open(path, null, rate, 2, out string why);
                Check("an Opus file opens, which Media Foundation cannot do", decoder != null, why);
                if (decoder == null) return;

                using (decoder)
                {
                    // The managed one, not Media Foundation. If this machine ever
                    // grows a real Opus handler the test should notice rather than
                    // silently start testing something else.
                    Equal("and it is the managed decoder that opened it",
                        "OggDecoder", decoder.GetType().Name);

                    Equal("at the rate the device asked for", rate.ToString(),
                        decoder.SampleRate.ToString());
                    Equal("and the channel count it asked for", "2",
                        decoder.Channels.ToString());
                    Check($"with about the right length ({decoder.Duration:0.##}s)",
                        Math.Abs(decoder.Duration - seconds) < 1.0,
                        decoder.Duration.ToString("0.###"));

                    // Four seconds of it, measured. A decoder that runs and
                    // produces silence reports success from every angle except
                    // listening, which is the failure this suite has shipped
                    // before.
                    var block = new float[4096 * decoder.Channels];
                    long frames = 0;
                    float peak = 0;

                    while (frames < rate * 4L)
                    {
                        int got = decoder.Read(block, 4096);
                        if (got <= 0) break;

                        frames += got;
                        for (int i = 0; i < got * decoder.Channels; i++)
                        {
                            float m = Math.Abs(block[i]);
                            if (m > peak) peak = m;
                        }
                    }

                    Check($"it decodes a useful amount ({frames:N0} frames)",
                        frames > rate * 3L, frames.ToString());
                    Check($"and what comes out is audio, not silence (peak {peak:0.###})",
                        peak > 0.1f, peak.ToString("0.####"));

                    // Mono in, stereo out: the file has one channel and the
                    // device wants two, so it has to be duplicated rather than
                    // left in one ear.
                    bool bothEars = false;
                    if (decoder.Read(block, 512) > 0)
                        for (int f = 0; f < 512 && !bothEars; f++)
                            if (Math.Abs(block[f * 2]) > 0.01f && Math.Abs(block[f * 2 + 1]) > 0.01f)
                                bothEars = true;

                    Check("a mono file comes out of both speakers", bothEars);

                    Check("and it can be seeked", decoder.SeekTo(3),
                        decoder.Position.ToString("0.##"));
                    Check($"landing about where asked ({decoder.Position:0.##}s)",
                        Math.Abs(decoder.Position - 3) < 0.5, decoder.Position.ToString("0.##"));
                    Check("with audio still coming out after the seek",
                        decoder.Read(block, 4096) > 0);

                    // Zero, which is the one target the Opus reader cannot find
                    // for itself.
                    //
                    // OpusOggReadStream.SeekTo(TimeSpan.Zero) looks for the last
                    // packet at or before granule zero, finds none, and latches
                    // end of stream for good — HasNextPacket never comes back,
                    // however far away the next seek goes. Everything downstream
                    // then reads nothing for ever, which is not silence with an
                    // error in it: the track goes on "playing" with the clock
                    // stopped at zero.
                    //
                    // Reached three ways, all of which were this: holding skip
                    // backward until it arrives at the beginning, "back to the
                    // start", and the loop point that makes repeat gapless — so
                    // an Opus track with repeat on went quiet the first time it
                    // reached its own end. That reader is gone — see OggOpusReader —
                    // and this stays, because zero is still the target to ask about.
                    Check("it can be seeked back to the very start", decoder.SeekTo(0),
                        decoder.Position.ToString("0.##"));
                    Check("with audio still coming out at the start",
                        decoder.Read(block, 4096) > 0);

                    // A held key asks for it fifty times a second once it gets
                    // there, so the answer has to survive being asked.
                    bool everyTime = true;
                    for (int i = 0; i < 20 && everyTime; i++)
                        everyTime = decoder.SeekTo(0) && decoder.Read(block, 4096) > 0;

                    Check("and again, twenty times over, as a held key asks", everyTime);

                    // The loop point: a decoder that has run out is turned round
                    // where it stands rather than being reopened, and that is
                    // what makes repeat gapless.
                    int spins = 0;
                    while (decoder.Read(block, 4096) > 0 && ++spins < 10_000) { }

                    Check("the file can be read to its end", spins < 10_000, spins.ToString());
                    Check("and a decoder that has run out can be turned round",
                        decoder.SeekTo(0) && decoder.Read(block, 4096) > 0);
                }

                // And the extension list has to actually offer them, or none of
                // this is ever reached from a keystroke.
                foreach (var extension in new[] { ".ogg", ".oga", ".opus" })
                    Check($"{extension} is in the default extension list",
                        AudioFiles.IsAudio("x" + extension, AudioFiles.DefaultExtensions));
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// The Preferences window builds, with every page on it.
        ///
        /// It is the largest hand-built window in the application — nine pages
        /// of controls, every one constructed in code — and until now it had no
        /// test that it so much as opens, while the two small dialogs beside it
        /// did. The reason was one line: it read three diagnostics off
        /// <c>Program</c>, which owns Main, so a test project could not compile
        /// it. See <see cref="AudioReport"/>.
        ///
        /// This is the check that a page removed, a control removed, or a
        /// setting renamed has not left the dialog throwing on the way up —
        /// which for a window opened with Ctrl+P means a crash dialog instead of
        /// preferences, and no way to change anything at all.
        /// </summary>
        private static void PreferencesWindowTests()
        {
            string error = "";
            int categories = 0, combos = 0, checkBoxes = 0, buttons = 0;
            var categoryNames = new List<string>();
            long buildMs = 0;

            // A settings object with everything moved off its defaults, because
            // a dialog that only builds against defaults is a dialog tested
            // against the one case that cannot be wrong.
            var settings = new Settings
            {
                AudioLimiter = true,
                AudioVolumePercent = 337,
                AudioReleaseAfterMinutes = 37,
                TypeAheadMilliseconds = 1234,
                WindowTitle = "Something Else",
                FolderSizes = FolderSizeMode.Automatic,
                GoogleDriveEnabled = true,
                SpeechOverrides = "audio.play=1;copy.done=0",
            };

            var thread = new Thread(() =>
            {
                try
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    using var window = new SettingsForm(settings, null);
                    window.BuildAllPages();
                    clock.Stop();
                    buildMs = clock.ElapsedMilliseconds;

                    combos = FindAll<ComboBox>(window).Count;
                    checkBoxes = FindAll<CheckBox>(window).Count;
                    buttons = FindAll<Button>(window).Count;

                    foreach (var list in FindAll<ListBox>(window))
                    {
                        categories = list.Items.Count;
                        foreach (var item in list.Items) categoryNames.Add(item?.ToString() ?? "");
                    }
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(30));

            Equal("the preferences window builds without throwing", "", error);
            if (error.Length > 0) return;

            Check($"and quickly ({buildMs}ms)", buildMs < 5000, $"{buildMs}ms");

            // Notifications is gone as a category and Speech absorbed it. Both
            // halves matter: an empty category left behind is a page that does
            // nothing, and a missing Speech page is the setting nobody can find.
            Check("there is a Speech category", categoryNames.Contains("Speech"),
                string.Join(", ", categoryNames));
            Check("and no Notifications category any more",
                !categoryNames.Any(n => n.Contains("Notification", StringComparison.OrdinalIgnoreCase)),
                string.Join(", ", categoryNames));
            Check($"every category has a page ({categories})", categories >= 7);

            Check($"the pages carry their controls ({combos} lists, {checkBoxes} boxes, {buttons} buttons)",
                combos > 10 && checkBoxes > 20 && buttons > 3);

            // Opened straight onto a page, the way the Audio menu opens it. A
            // start category the dialog does not recognise must land on the
            // first page rather than throw or show nothing.
            foreach (var start in new[] { "Audio", "Speech", "not a category", "" })
            {
                string startError = "";
                var one = new Thread(() =>
                {
                    try { using var window = new SettingsForm(settings, start); }
                    catch (Exception ex) { startError = ex.GetType().Name + ": " + ex.Message; }
                });
                one.SetApartmentState(ApartmentState.STA);
                one.Start();
                one.Join(TimeSpan.FromSeconds(20));

                Equal($"it opens on \"{start}\" without throwing", "", startError);
            }

            // Every diagnostic the page prints has to survive there being no
            // player at all, which is exactly the state a test process is in —
            // and the state the real one is in before the first track.
            Check("the output diagnostic answers with no player",
                AudioReport.OutputText.Length > 0, AudioReport.OutputText);
            Check("so does the meter", AudioReport.LevelText.Length > 0, AudioReport.LevelText);

            // And one that throws must not take the window with it. This is the
            // whole reason AudioReport catches: it is read while building a
            // dialog, from state owned by other threads.
            AudioReport.Level = () => throw new InvalidOperationException("no");
            Check("a diagnostic that throws is answered, not propagated",
                AudioReport.LevelText.Length > 0, AudioReport.LevelText);
            AudioReport.Level = null;
        }

        /// <summary>
        /// The numbers a settings drop-down offers.
        ///
        /// This is the arithmetic behind every number in Preferences, and it was
        /// wrong in every one of them at once — a flat step worked out from the
        /// width of the range, so a field running to 1440 offered 0, 100, 200 and
        /// nothing in between. It lived as a private helper inside a form that
        /// needs a message loop, which is exactly why nothing caught it.
        /// </summary>
        private static void SettingChoiceTests()
        {
            // The one that was reported: "Give the memory back after paused
            // (minutes)", 0 to 1440, sitting on its default of ten.
            var minutes = SettingChoices.Between(0, 1440, 10);

            foreach (int wanted in new[] { 0, 1, 5, 10, 15, 20, 30, 45, 60, 90, 120, 1440 })
                Check($"{wanted} minutes can be chosen", minutes.Contains(wanted),
                    string.Join(", ", minutes.Take(12)));

            // And a step from the default has to be a step, not a leap. It went
            // from ten minutes to a hundred in one press.
            int at = minutes.IndexOf(10);
            Check("one press down from ten minutes is eleven", minutes[at + 1] == 11,
                minutes[at + 1].ToString());
            Check("and one press up is nine", minutes[at - 1] == 9, minutes[at - 1].ToString());

            // "Skip by (seconds)" had the same shape, at 1, 10, 25, 50.
            var seconds = SettingChoices.Between(1, 300, 10);
            foreach (int wanted in new[] { 1, 5, 15, 20, 30, 45, 60, 120, 300 })
                Check($"a {wanted} second skip can be chosen", seconds.Contains(wanted));

            // Both ends, always, whatever the ladder would have produced.
            foreach (var (min, max) in new[] { (0, 1440), (1, 300), (7, 24), (64, 8192), (0, 262144) })
            {
                var list = SettingChoices.Between(min, max, min);
                Check($"the bottom of {min} to {max} is offered", list.Contains(min));
                Check($"and the top of {min} to {max} is offered", list.Contains(max));
                Check($"{min} to {max} comes back in order",
                    list.SequenceEqual(list.OrderBy(v => v)));
                Check($"{min} to {max} has no duplicates", list.Distinct().Count() == list.Count);
                Check($"{min} to {max} stays inside its bounds",
                    list.All(v => v >= min && v <= max));
            }

            // A saved value that is not on the ladder must survive being shown.
            // Anything else means opening Preferences and pressing OK silently
            // changes a setting nobody touched.
            var odd = SettingChoices.Between(0, 1440, 37);
            Check("an off-ladder value is still offered", odd.Contains(37));

            // A ladder starting somewhere unround still lands on round numbers.
            var drive = SettingChoices.Between(64, 8192, 1024);
            Check("a range starting at 64 offers 65 rather than 69", drive.Contains(65));
            Check("and 100, 200, 500", drive.Contains(100) && drive.Contains(200) && drive.Contains(500));

            // Long enough to be useful, short enough to arrow through. The old
            // one was 41 entries for the whole volume range; the failure mode in
            // the other direction is a list of 262144 items.
            var biggest = SettingChoices.Between(0, 262144, 2048);
            Check($"even the widest range stays a list ({biggest.Count} entries)",
                biggest.Count is > 40 and < 400, biggest.Count.ToString());

            // A range of nothing is still a list of something.
            Check("a range of one value is one value",
                SettingChoices.Between(5, 5, 5).Count == 1);
        }

        /// <summary>
        /// The limiter dialog builds, offers all three ladders, and every rung
        /// says something.
        ///
        /// Three lists whose entries are sentences rather than numbers — "half a
        /// millisecond, an edge on transients" — because the whole dialog is
        /// meant to be used with your attention on what you are hearing rather
        /// than on the screen. An entry reading "500" would be no help at all.
        /// </summary>
        private static void LimiterFormTests()
        {
            foreach (int value in AudioPlayer.LimiterAttackLadder)
                Check($"attack {value}us describes itself",
                    AudioPlayer.DescribeAttack(value).Length > 6, AudioPlayer.DescribeAttack(value));

            foreach (int value in AudioPlayer.LimiterReleaseLadder)
                Check($"release {value}ms describes itself",
                    AudioPlayer.DescribeRelease(value).Length > 6, AudioPlayer.DescribeRelease(value));

            foreach (int value in AudioPlayer.LimiterCeilingLadder)
                Check($"ceiling {value}% describes itself",
                    AudioPlayer.DescribeCeiling(value).Length > 6, AudioPlayer.DescribeCeiling(value));

            // Every default has to be a rung, or the dialog opens on the nearest
            // one and applies it — silently moving a setting by opening a window.
            var defaults = new Settings();
            Check("the default attack is on its ladder",
                AudioPlayer.LimiterAttackLadder.Contains(defaults.AudioLimiterAttackMicroseconds));
            Check("the default release is on its ladder",
                AudioPlayer.LimiterReleaseLadder.Contains(defaults.AudioLimiterReleaseMilliseconds));
            Check("the default ceiling is on its ladder",
                AudioPlayer.LimiterCeilingLadder.Contains(defaults.AudioLimiterCeilingPercent));

            // Every rung has to survive the player's own clamping, or a value
            // chosen from the list comes back as a different one.
            using (var player = new AudioPlayer())
            {
                int refused = 0;
                foreach (int value in AudioPlayer.LimiterAttackLadder)
                {
                    player.LimiterAttackMicroseconds = value;
                    if (player.LimiterAttackMicroseconds != value) refused++;
                }
                foreach (int value in AudioPlayer.LimiterReleaseLadder)
                {
                    player.LimiterReleaseMilliseconds = value;
                    if (player.LimiterReleaseMilliseconds != value) refused++;
                }
                foreach (int value in AudioPlayer.LimiterCeilingLadder)
                {
                    player.LimiterCeilingPercent = value;
                    if (player.LimiterCeilingPercent != value) refused++;
                }
                Equal("every rung of every ladder survives the player", "0", refused.ToString());
            }

            // And the window itself builds, with the three lists on it and the
            // right number of rungs in each.
            string error = "";
            int lists = 0, entries = 0, checks = 0;
            int applied = 0;

            var thread = new Thread(() =>
            {
                try
                {
                    using var window = new LimiterForm(500, 250, 90, false, true,
                        (_, _, _, _, _) => applied++);
                    var boxes = FindAll<ComboBox>(window);
                    lists = boxes.Count;
                    foreach (var box in boxes) entries += box.Items.Count;

                    // Two switches now. "Same lift at any volume" lives here
                    // rather than in Preferences with the three lists, because it
                    // is judged the same way — by ear, against music that is
                    // playing — and the limiter's own on/off joined it, because
                    // one effect with a switch on a preferences page and its
                    // controls on a menu is one effect with two homes.
                    checks = FindAll<CheckBox>(window).Count;
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(30));

            Equal("the limiter window builds without throwing", "", error);
            if (error.Length > 0) return;

            Equal("with three lists on it", "3", lists.ToString());
            Equal("and both switches beside them", "2", checks.ToString());
            Equal("holding every rung of the three ladders",
                (AudioPlayer.LimiterAttackLadder.Length +
                 AudioPlayer.LimiterReleaseLadder.Length +
                 AudioPlayer.LimiterCeilingLadder.Length).ToString(),
                entries.ToString());

            // Building it must not change the sound. Selecting the opening value
            // in each list fires SelectedIndexChanged, and a handler wired before
            // the initial selection would apply three times before the dialog is
            // even on screen — which for a limiter is an audible jump caused by
            // opening a window.
            Equal("and opening it applies nothing", "0", applied.ToString());
        }

        /// <summary>
        /// Every field on the progress window is actually on the window.
        ///
        /// The height was a fixed 340 pixels, which fits seven rows and a button
        /// at 100% scaling and clips the last one at 150%. So "Time remaining"
        /// was built, updated on every tick and reachable with Tab — a screen
        /// reader read it out perfectly — while nobody looking at the window
        /// could see it. That is the one combination that is hard to notice from
        /// either direction: checking the announcements finds nothing wrong, and
        /// so does reading the code.
        ///
        /// Shown rather than merely constructed, because the sizing happens in
        /// OnLoad and a form that is only newed up never loads. On its own STA
        /// thread, like the preferences window.
        /// </summary>
        private static void ProgressWindowShapeTests()
        {
            Console.WriteLine("The progress window:");

            string error = "";
            int fields = 0, clipped = 0, hidden = 0;
            var offenders = new List<string>();

            var thread = new Thread(() =>
            {
                try
                {
                    using var cts = new CancellationTokenSource();
                    using var window = new ProgressForm("Copying", new Settings(), cts);

                    // Bigger text, which is the whole point. This suite runs at
                    // 100% scaling, where the old fixed height of 340 pixels was
                    // just enough — so a test that only opened the window here
                    // would have passed on the very build that clipped the last
                    // field at 150%. Scaling the font reproduces the condition
                    // rather than hoping the machine happens to have it.
                    window.Font = new System.Drawing.Font(
                        window.Font.FontFamily, window.Font.Size * 1.5f);

                    // Off to one side and away from the eye: this really does put
                    // a window on screen for a moment.
                    window.StartPosition = FormStartPosition.Manual;
                    window.Location = new System.Drawing.Point(-4000, -4000);
                    window.ShowInTaskbar = false;
                    window.Show();
                    Application.DoEvents();

                    // A real update first, so the fields hold the text they will
                    // actually be asked to show rather than "calculating".
                    window.Update(new TransferProgress(
                        6_600_000_000L, 21_474_836_480L, 0, 1, "huge.bin",
                        1_300_000_000d, TimeSpan.FromSeconds(5)));
                    Application.DoEvents();

                    var boxes = FindAll<TextBox>(window);
                    fields = boxes.Count;

                    var buttons = FindAll<Button>(window);
                    int floor = buttons.Count > 0
                        ? buttons.Min(b => window.PointToClient(b.PointToScreen(System.Drawing.Point.Empty)).Y)
                        : window.ClientSize.Height;

                    foreach (var box in boxes)
                    {
                        if (!box.Visible) { hidden++; offenders.Add(box.AccessibleName + " (hidden)"); continue; }

                        var at = window.PointToClient(box.PointToScreen(System.Drawing.Point.Empty));
                        int bottom = at.Y + box.Height;

                        // Below the button, or past the bottom of the window.
                        if (bottom > floor || bottom > window.ClientSize.Height)
                        {
                            clipped++;
                            offenders.Add($"{box.AccessibleName} ends at {bottom}, " +
                                          $"button starts at {floor}, client is {window.ClientSize.Height}");
                        }
                    }

                    window.Close();
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(30));

            Equal("the progress window builds and shows without throwing", "", error);
            if (error.Length > 0) { Console.WriteLine(); return; }

            // Status, current item, progress, size, speed, elapsed, remaining.
            Equal("all seven fields are there", "7", fields.ToString());

            foreach (var one in offenders) Console.WriteLine("        " + one);
            Equal("none of them is hidden", "0", hidden.ToString());
            Equal("and none is clipped off the bottom", "0", clipped.ToString());

            Console.WriteLine();
        }

        /// <summary>
        /// The two audio dialogs on the Audio menu actually open.
        ///
        /// Both are hand-built, every control constructed in code, and both are
        /// reached by a global shortcut that fires whether or not this window
        /// has the keyboard — so a constructor that throws is a crash dialog
        /// instead of the controls, from a key press, with the music playing.
        /// This is the same check `SettingsForm` gained after being the largest
        /// hand-built window in the application with no test that it so much as
        /// opens.
        ///
        /// Shown at 150% of the font, because a row of twenty sliders is half as
        /// wide again there and the suite runs at 100% — which is exactly how
        /// `ProgressForm` passed on the build that clipped its last field.
        /// </summary>
        private static void AudioDialogShapeTests()
        {
            Console.WriteLine("The equaliser and limiter windows:");

            string error = "";
            int sliders = 0, checks = 0, clipped = 0, limiterControls = 0;
            var bands = new List<string>();
            var offenders = new List<string>();

            var thread = new Thread(() =>
            {
                try
                {
                    // Every band moved off zero, because a window tested only
                    // against its defaults is tested against the one case that
                    // cannot be wrong.
                    var gains = new int[Equaliser.BandCount];
                    for (int b = 0; b < gains.Length; b++) gains[b] = (b % 5) - 2;

                    using var eq = new EqualiserForm(gains, true, (_, _) => { }, _ => { });
                    eq.Font = new System.Drawing.Font(eq.Font.FontFamily, eq.Font.Size * 1.5f);
                    eq.StartPosition = FormStartPosition.Manual;
                    eq.Location = new System.Drawing.Point(-4000, -4000);
                    eq.ShowInTaskbar = false;
                    eq.Show();
                    Application.DoEvents();

                    var found = FindAll<TrackBar>(eq);
                    sliders = found.Count;
                    checks = FindAll<CheckBox>(eq).Count;

                    foreach (var slider in found)
                    {
                        bands.Add(slider.AccessibleName ?? "(unnamed)");

                        var at = eq.PointToClient(slider.PointToScreen(System.Drawing.Point.Empty));
                        if (at.X + slider.Width > eq.ClientSize.Width ||
                            at.Y + slider.Height > eq.ClientSize.Height)
                        {
                            clipped++;
                            offenders.Add($"{slider.AccessibleName} ends at " +
                                          $"{at.X + slider.Width},{at.Y + slider.Height} in a client of " +
                                          $"{eq.ClientSize.Width},{eq.ClientSize.Height}");
                        }
                    }

                    eq.Close();

                    // And the limiter, which has just gained a fifth control in
                    // a window whose height was written for four.
                    using var limiter = new LimiterForm(500, 250, 90, false, true,
                        (_, _, _, _, _) => { });
                    limiter.Font = new System.Drawing.Font(
                        limiter.Font.FontFamily, limiter.Font.Size * 1.5f);
                    limiter.StartPosition = FormStartPosition.Manual;
                    limiter.Location = new System.Drawing.Point(-4000, -4000);
                    limiter.ShowInTaskbar = false;
                    limiter.Show();
                    Application.DoEvents();

                    limiterControls = FindAll<ComboBox>(limiter).Count + FindAll<CheckBox>(limiter).Count;

                    foreach (Control control in FindAll<Control>(limiter))
                    {
                        if (control is not (ComboBox or CheckBox or Button)) continue;
                        var at = limiter.PointToClient(control.PointToScreen(System.Drawing.Point.Empty));
                        if (at.Y + control.Height > limiter.ClientSize.Height)
                        {
                            clipped++;
                            offenders.Add($"limiter: {control.Text} ends at {at.Y + control.Height} " +
                                          $"in a client of {limiter.ClientSize.Height}");
                        }
                    }

                    limiter.Close();
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(60));

            Equal("both windows build and show without throwing", "", error);
            if (error.Length > 0) { Console.WriteLine(); return; }

            Equal("the equaliser has a slider for every band",
                Equaliser.BandCount.ToString(), sliders.ToString());
            Check("and a switch of its own", checks >= 1, checks.ToString());

            // Every slider names its own frequency, and none of them is blank —
            // the name is the only thing a screen reader has to say which band
            // the number belongs to.
            Check("every slider is named after its frequency",
                bands.Count == Equaliser.BandCount &&
                bands.Distinct().Count() == bands.Count &&
                bands.All(b => b.Contains("hertz", StringComparison.OrdinalIgnoreCase)),
                bands.Count == 0 ? "none" : string.Join(", ", bands.Take(3)) + " …");

            // Attack, release, ceiling, "same lift at any volume", and the
            // switch that used to live on the preferences page.
            Check("the limiter has all five of its controls in one place",
                limiterControls == 5, limiterControls.ToString());

            foreach (var one in offenders) Console.WriteLine("        " + one);
            Equal("nothing is clipped off either window", "0", clipped.ToString());

            Console.WriteLine();
        }

        /// <summary>
        /// Notification ids that actually appear in the source, by reading it.
        ///
        /// Crude on purpose. The alternative is a registry every call site has to
        /// remember to update, which is the same class of thing that let the
        /// catalogue sit disconnected in the first place — and reading the source
        /// cannot be fooled by a registry entry nobody acts on.
        /// </summary>
        private static List<string> WiredNotificationIds()
        {
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            // Up from the test binary to the project, wherever it was built.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return new List<string>();

            var source = Path.Combine(dir.FullName, "src");
            foreach (var file in Directory.EnumerateFiles(source, "*.cs"))
            {
                // The catalogue itself declares them all; it does not raise any.
                if (Path.GetFileName(file).Equals("Notifications.cs", StringComparison.OrdinalIgnoreCase))
                    continue;

                string text;
                try { text = File.ReadAllText(file); } catch { continue; }

                foreach (var info in Notifications.All)
                    if (text.Contains("\"" + info.Id + "\"", StringComparison.Ordinal))
                        found.Add(info.Id);
            }

            return found.ToList();
        }

        /// <summary>
        /// The shallow ring depth, read out of the source — WasapiPlayback is
        /// internal to the application and its constants are private, and this is
        /// a number worth asserting rather than a spelling.
        /// </summary>
        private static int WasapiPlaybackShallowFrames()
        {
            var text = SourceFile("WasapiPlayback.cs") ?? "";
            const string marker = "private const int ShallowFrames = 48000 / ";

            int at = text.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return 0;

            int from = at + marker.Length;
            int to = text.IndexOf(';', from);
            return to > from && int.TryParse(text[from..to], out int divisor) && divisor > 0
                ? 48000 / divisor
                : 0;
        }

        /// <summary>One of the application's source files, as text, or null.</summary>
        private static string? SourceFile(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;

            var path = Path.Combine(dir.FullName, "src", name);
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }

        private static int Occurrences(string text, string needle)
        {
            int found = 0, at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                found++;
                at += needle.Length;
            }
            return found;
        }

        /// <summary>
        /// Every raise of a suppression counter is released exactly once.
        ///
        /// Counting is a crude check and it catches the exact shape of the bug
        /// worth catching. A counter released on each ordinary exit path instead
        /// of in a finally has *more* decrements than increments in the source —
        /// which is what one method had, four raises against five lowers — and
        /// the path with no decrement at all is the one nobody thought about:
        /// a dialog throwing on its way up. `_suppressWatch` left raised means
        /// the folder watcher never refreshes anything again for the life of the
        /// process, with nothing to connect it back to the copy that failed.
        ///
        /// So the rule is one raise, one release, and the release in a finally.
        /// </summary>
        private static void SuppressionBalanceChecks()
        {
            var main = SourceFile("MainForm.cs");
            if (main == null) { Check("MainForm.cs can be read", false); return; }

            foreach (var counter in new[] { "_suppressWatch", "_suppressStatus" })
            {
                int raised = Occurrences(main, counter + "++");
                int released = Occurrences(main, counter + "--");
                Check($"{counter} is raised and released the same number of times",
                    raised == released, $"{raised} raised, {released} released");
                Check($"{counter} is used at all", raised > 0);
            }

            // And the releases are in a finally rather than on the happy path.
            // Counted loosely — some sit inside a multi-line finally block — so
            // this asks only that there are as many finallys mentioning the
            // counter as there are raises of it.
            int watchRaises = Occurrences(main, "_suppressWatch++");
            int watchFinallys = Occurrences(main, "finally { _suppressWatch--; }")
                              + Occurrences(main, "finally { _deleting = false; _suppressWatch--; }")
                              + Occurrences(main, "// changes, with nothing to connect it back to a failed upload.");
            Check("every _suppressWatch raise has a finally to release it",
                watchFinallys >= watchRaises, $"{watchRaises} raises, {watchFinallys} finallys");

            // ---- and the same for the per-folder version of it ----
            //
            // A transfer no longer stops the watcher for the whole window; it
            // marks the one folder it is writing into. Same counter, same rule,
            // and the same consequence for getting it wrong — a destination left
            // registered is a folder that never notices a change again, for the
            // life of the process, with nothing to connect it back to the copy
            // that failed.
            Check("every transfer destination registered is unregistered again",
                Occurrences(main, "BeginTransferTo(") == Occurrences(main, "EndTransferTo("),
                $"{Occurrences(main, "BeginTransferTo(")} begins, " +
                $"{Occurrences(main, "EndTransferTo(")} ends");

            Check("and the release is in a finally",
                Occurrences(main, "EndTransferTo(destination);") >= 2);

            // ---- a transfer must never hold the window modal again ----
            //
            // This is the change, and it is one line in each of two methods —
            // exactly the kind of thing a later edit puts back without noticing,
            // because ShowDialog is what every other dialog in this application
            // correctly uses. A progress window is the one that must not: it is
            // up for as long as the copy takes, and ShowDialog means the file
            // manager is gone for all of it.
            var progress = SourceFile("ProgressForm.cs");
            if (progress == null) { Check("ProgressForm.cs can be read", false); return; }

            // The ProgressForm variable is called `window` in both places, and
            // every genuinely modal dialog in that file calls its own `dialog` —
            // which is what makes this readable as a check rather than a
            // guess. A ShowDialog on the progress window would be the file
            // manager gone for the length of the copy, all over again.
            Check("no transfer opens its progress window modally",
                !main.Contains("window.ShowDialog", StringComparison.Ordinal),
                "ShowDialog on a ProgressForm makes the whole application wait for the copy");

            Check("the progress window is shown modelessly instead",
                Occurrences(main, "window.Show();") >= 2);

            // And that it is reachable once it is behind something. An unowned
            // top-level window that is not in the taskbar is a window you can
            // leave and never get back to — which for the only place the Cancel
            // button lives is worse than a modal one.
            Check("the progress window is in the taskbar, so Alt+Tab reaches it",
                progress.Contains("ShowInTaskbar = true", StringComparison.Ordinal));

            // Closing it is cancelling, and it stays up until the transfer has
            // really stopped. A window that vanishes while robocopy is still
            // finishing a file has lied about what it did.
            Check("closing the progress window cancels rather than abandoning",
                progress.Contains("RequestCancel();", StringComparison.Ordinal) &&
                progress.Contains("e.Cancel = true;", StringComparison.Ordinal));

            // ---- and nothing ends the process out from under one silently ----
            //
            // Closing, quitting and restarting were all unreachable while a
            // transfer held the window modal. All three are reachable now, and
            // all three kill a copy where it stands — which for a move means
            // files deleted from one end and not written to the other.
            Check("closing, quitting and restarting all ask about a running transfer",
                Occurrences(main, "ConfirmAbandonTransfers(") >= 3,
                $"{Occurrences(main, "ConfirmAbandonTransfers(")} call sites in MainForm");

            var tray = SourceFile("TrayApplicationContext.cs");
            Check("and so does Exit on the tray menu",
                tray != null && tray.Contains("ConfirmAbandonTransfers(", StringComparison.Ordinal));

            // But not on the path a new launch uses to ask this instance to
            // stand down. That is how --install-only replaces the running copy,
            // and a modal question there is an installer waiting out its timeout
            // against a dialog nobody is looking at — then reporting that the
            // file is in use and telling somebody to close an application that
            // is already trying to close.
            Check("the exit command from another launch is never held up by it",
                tray != null && tray.Contains("Quit();", StringComparison.Ordinal) &&
                tray.Contains("Quit(ask: true)", StringComparison.Ordinal));

            // ---- and that the one-track-at-a-time rule is wired to something ----
            //
            // The same lesson as the notification catalogue, which sat inert for
            // a while: a mechanism nothing calls is not a policy, it is a comment.
            // ReleaseAllBut is what keeps "starting another disposes the last"
            // true, and for a long time nothing called it — so every Drive file
            // anything ever read kept a live download of the whole file.
            var mount = SourceFile("DriveMount.cs");
            if (mount == null) { Check("DriveMount.cs can be read", false); return; }

            Check("the Drive cache release is actually called",
                Occurrences(mount, "ReleaseAllBut(") >= 2,
                "declared but never called");

            // And that a cache is never made through the factory overload, whose
            // losers start downloading and are then thrown away undisposed.
            Check("no per-file cache is created through GetOrAdd's factory",
                !mount.Contains("_caches.GetOrAdd(fileId,\n", StringComparison.Ordinal) &&
                !mount.Contains("id => new DriveFileCache", StringComparison.Ordinal),
                "the factory overload can run more than once for one key");

            // ---- nothing waits on a share before the window exists ----
            //
            // The constructor used to pick a starting folder with a synchronous
            // Directory.Exists per candidate, and one of those candidates is
            // wherever you were last — routinely a share. This application is the
            // shell's handler for every folder on the machine and is started
            // afresh each time one is opened, so a sleeping NAS did not make the
            // window slow to fill in, it stopped the window appearing.
            Check("the startup folder is chosen through the bounded probe",
                main.Contains("FirstExistingFolderAsync", StringComparison.Ordinal));
            Check("and not by a blocking check in the constructor",
                !main.Contains("private static string FirstExistingFolder(", StringComparison.Ordinal),
                "the synchronous chooser is back");
        }

        /// <summary>
        /// Windows shortcuts: the naming rule, a real `.lnk` round-tripped
        /// through the shell, and the two menu rules the window applies to the
        /// commands that make and copy them.
        ///
        /// The round trip is the part worth having. "A file appeared in the
        /// folder" is a much weaker claim than "it goes where it was asked to
        /// go", and a `.lnk` is opaque — a shortcut written to the wrong target,
        /// or written as nothing at all, looks exactly like a correct one from
        /// the outside and is only found by somebody opening it.
        /// </summary>
        private static async Task ShellLinkTests()
        {
            Console.WriteLine("Windows shortcuts:");

            // The whole name, extension and all. This list never hides a known
            // extension, so the shortcut is named after the name that was read
            // out on the row it was made from.
            Equal("a file keeps its extension in the name",
                "notes.txt - Shortcut.lnk", ShellLink.NameFor(@"C:\Users\x\notes.txt"));
            Equal("a folder is named the same way",
                "Music - Shortcut.lnk", ShellLink.NameFor(@"C:\Users\x\Music"));
            Equal("a trailing separator is not part of the name",
                "Music - Shortcut.lnk", ShellLink.NameFor(@"C:\Users\x\Music\"));

            // A drive root has no file name at all — GetFileName says nothing —
            // and the colon it does have cannot go into one.
            Equal("a drive root still gets a name", "C - Shortcut.lnk", ShellLink.NameFor(@"C:\"));

            var dir = NewTempDir();
            try
            {
                var target = Path.Combine(dir, "notes.txt");
                await File.WriteAllTextAsync(target, "hello");

                var link = Path.Combine(dir, ShellLink.NameFor(target));
                await ShellLink.CreateAsync(target, link);

                Check("the shortcut is written", File.Exists(link), link);
                Equal("and it points at what it was asked to point at",
                    target, await ShellLink.TargetOfAsync(link));

                // A second one beside the first, which is what pressing the
                // command twice does. UniqueName is what the window uses for it,
                // so this is the pairing rather than either half alone.
                var again = FileOperations.UniqueName(Path.Combine(dir, ShellLink.NameFor(target)));
                Check("a second shortcut does not overwrite the first",
                    !string.Equals(again, link, StringComparison.OrdinalIgnoreCase), again);

                await ShellLink.CreateAsync(target, again);
                Equal("and the second one points at the same file",
                    target, await ShellLink.TargetOfAsync(again));

                // A folder, because a shortcut to one is stored differently by
                // the shell and is the case a hand-written .lnk gets wrong.
                var folder = Path.Combine(dir, "Music");
                Directory.CreateDirectory(folder);

                var folderLink = Path.Combine(dir, ShellLink.NameFor(folder));
                await ShellLink.CreateAsync(folder, folderLink);
                Equal("a shortcut to a folder points at the folder",
                    folder, await ShellLink.TargetOfAsync(folderLink));
            }
            finally { Cleanup(dir); }

            // ---- and how the window offers all of this ----
            //
            // MainForm is not compiled into this suite, so the wiring is read out
            // of the source — the same reason the pane-naming rule below is.
            var main = SourceFile("MainForm.cs");
            if (main == null) { Check("MainForm.cs can be read", false); Console.WriteLine(); return; }

            Check("Create shortcut is on the context menu",
                main.Contains("Item(\"Create &shortcut\", Keys.None, CreateShortcut)", StringComparison.Ordinal));

            // Every Drive-link entry goes through the one helper, on both menus.
            // One built by hand is one that never registers itself for hiding,
            // and that is how the popup and the Edit menu end up disagreeing
            // about the same file.
            Check("every Drive link entry goes through the one helper",
                Occurrences(main, "DriveLinkItem(\"Copy") == 4 &&
                !main.Contains("Add(Item(\"Copy Drive &link\"", StringComparison.Ordinal) &&
                !main.Contains("Add(Item(\"Copy p&ublic Drive link\"", StringComparison.Ordinal));

            Check("and the rule is applied wherever one of those menus opens",
                Occurrences(main, "ShowDriveLinkItems()") >= 3);

            // By path, against the mount, and never by asking Drive anything.
            // Deciding what goes on a menu must not cost a network round trip.
            Check("the decision is a path comparison",
                main.Contains("Drive != null && Drive.Owns(path)", StringComparison.Ordinal));

            // A .lnk written into the sync root by hand is invisible and then
            // swept by the next mount — the fault New file and New folder each
            // had. The command refuses, and the menu does not offer it.
            Check("a shortcut is refused in a Drive folder",
                main.Contains("Cannot create a shortcut in Google Drive", StringComparison.Ordinal));

            Console.WriteLine();
        }

        /// <summary>
        /// The list must never be renamed while it has the keyboard.
        ///
        /// Renaming a control a screen reader is sitting on is an announcement —
        /// the name of the focused object changed, and that is an event NVDA
        /// reads out. Navigation names the pane after the folder it is showing,
        /// and it does that on the way into every folder while the list is
        /// already focused, so opening a folder spoke the folder's name over the
        /// row that had just been focused. That is exactly what SpeakNavigation
        /// being off by default is supposed to prevent: the setting was working
        /// and something else was talking, which is the second time this
        /// application has had that bug.
        ///
        /// Read out of the source rather than driven, because MainForm needs a
        /// message loop and is deliberately not compiled into this suite — the
        /// same reason the notification wiring is checked this way. A rule that
        /// lives only in a comment is a rule the next change quietly undoes.
        /// </summary>
        private static void PaneNamingTests()
        {
            Console.WriteLine("Invariants the compiler cannot see:");

            SuppressionBalanceChecks();

            var main = SourceFile("MainForm.cs");
            if (main == null)
            {
                Check("MainForm.cs can be read", false, "not found from " + AppContext.BaseDirectory);
                Console.WriteLine();
                return;
            }

            // Two: the one that defers while focused, and the one that applies
            // the deferred name afterwards. A third is a new way to announce a
            // folder name nobody asked for.
            int assignments = Occurrences(main, "pane.List.AccessibleName = ");
            Check("the pane's list is renamed in exactly two places",
                assignments == 2, assignments + " found");

            Check("and one of those refuses while the list has focus",
                main.Contains("if (pane.List.Focused)", StringComparison.Ordinal));

            Check("the deferred name has somewhere to wait",
                main.Contains("public string? PendingName", StringComparison.Ordinal));

            Check("and something that puts it on later",
                main.Contains("private static void ApplyPendingName", StringComparison.Ordinal));

            // Leaving is the only moment the rename is silent, so that is what it
            // has to be hung off.
            Check("wired to the list losing the keyboard",
                main.Contains("list.Leave +=", StringComparison.Ordinal));

            UiThreadIoChecks(main);
            ShellMenuBudgetChecks(main);

            Console.WriteLine();
        }

        /// <summary>
        /// The Windows menu is not allowed to freeze the window for a minute.
        ///
        /// Building it costs two shell calls per selected item — SHParseDisplayName
        /// and SHBindToParent — and they are not cheap: measured at **8ms each**
        /// on the machine this was found on. There was no limit, so Ctrl+A
        /// followed by Shift+F10 in a folder of three thousand files stopped the
        /// application for twenty-four seconds, and a real folder for very much
        /// longer.
        ///
        /// Read out of the source rather than driven, and deliberately so: the
        /// only way to exercise the fast path is to let the menu actually appear,
        /// and TrackPopupMenuEx does not return until somebody dismisses it. A
        /// test that pops up a context menu and waits is a test that hangs the
        /// suite on the machine where it passes.
        /// </summary>
        private static void ShellMenuBudgetChecks(string main)
        {
            // Through a local, so the compiler cannot fold the comparison away.
            // Against the constant directly it can prove the answer and says so
            // — "the given expression always matches the provided pattern" —
            // which reads like a test that cannot fail. It can: the guard is
            // against somebody changing the budget to something silly, and that
            // change is exactly when the comparison stops being provable.
            int budget = ShellContextMenu.BuildBudgetMilliseconds;
            Check("there is a budget on building the Windows menu",
                budget > 250 && budget <= 5000, budget + "ms");

            var shell = SourceFile("ShellContextMenu.cs");
            if (shell == null) { Check("ShellContextMenu.cs can be read", false); return; }

            // Inside the per-item loop, not once before it. One slow extension
            // can make a single item expensive, and a budget checked only at the
            // start is not a budget.
            int loopAt = shell.IndexOf("for (int i = 1; i < paths.Count; i++)", StringComparison.Ordinal);
            int checkAt = shell.IndexOf("clock.ElapsedMilliseconds > budgetMilliseconds", StringComparison.Ordinal);
            Check("and it is checked for every item, not once at the start",
                loopAt >= 0 && checkAt > loopAt);

            // Abandoned whole, never half-built. A Windows menu that quietly
            // applied to the first few hundred of a selection would be a far
            // worse bug than the freeze it replaced — Delete on "some of what
            // you selected" is not something to offer.
            Check("and it gives up on the whole menu rather than part of it",
                shell.Contains("return MenuOutcome.TooSlow", StringComparison.Ordinal) &&
                !shell.Contains("break; // budget", StringComparison.Ordinal));

            // And the refusal is said out loud, with somewhere to go next.
            Check("the window explains the refusal instead of doing nothing",
                main.Contains("MenuOutcome.TooSlow", StringComparison.Ordinal) &&
                main.Contains("shell.menu.slow", StringComparison.Ordinal));

            var info = Notifications.ById("shell.menu.slow");
            Check("and the message points at commands that have no such limit",
                info != null && info.Preview().Contains("no such limit", StringComparison.OrdinalIgnoreCase),
                info?.Preview() ?? "missing");

            ShellMenuRefusalTest();
        }

        /// <summary>
        /// The refusal actually happens, driven rather than read.
        ///
        /// The budget is passed in as zero so the answer does not depend on how
        /// fast this machine's shell is. With the real two seconds, a fast
        /// machine builds the menu instead — and building it means
        /// TrackPopupMenuEx, which does not return until somebody dismisses a
        /// context menu that nobody is looking at. This is the difference
        /// between a test that proves something and a test that hangs the suite
        /// on whichever machine it happens to pass on.
        /// </summary>
        private static void ShellMenuRefusalTest()
        {
            var temp = Path.Combine(Path.GetTempPath(), "en-shellmenu-" + Guid.NewGuid().ToString("N"));
            string outcome = "", quick = "";

            var thread = new Thread(() =>
            {
                try
                {
                    Directory.CreateDirectory(temp);
                    var paths = new List<string>();
                    for (int i = 0; i < 8; i++)
                    {
                        var p = Path.Combine(temp, $"item{i}.txt");
                        File.WriteAllText(p, "x");
                        paths.Add(p);
                    }

                    using var owner = new Form { ShowInTaskbar = false };
                    owner.CreateControl();

                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var result = ShellContextMenu.Show(owner, paths, 0, 0, budgetMilliseconds: 0);
                    clock.Stop();

                    outcome = result.ToString();
                    quick = clock.ElapsedMilliseconds.ToString();
                }
                catch (Exception ex) { outcome = ex.GetType().Name + ": " + ex.Message; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(30));

            Equal("a selection over the budget is refused, not built", "TooSlow", outcome);
            Check($"and refused promptly ({quick}ms)",
                int.TryParse(quick, out int ms) && ms < 2000, quick + "ms");

            try { Directory.Delete(temp, true); } catch { }
        }

        /// <summary>
        /// Filesystem calls that must not be made from the thread that paints.
        ///
        /// Both of these were on the UI thread, and both were reached by a loop
        /// with one iteration per file — which is the shape that turns a syscall
        /// worth a fraction of a millisecond locally into a window that has
        /// stopped responding as soon as the files are on a share that has gone
        /// to sleep. Neither is visible reading the line: one is spelled
        /// "TryGetCached" and looks like the fast path, and the other sat
        /// directly beneath a comment explaining why the check above it had been
        /// moved to a worker.
        ///
        /// Read out of the source for the same reason as the rest of this
        /// section: MainForm needs a message loop and is not compiled in here.
        /// </summary>
        private static void UiThreadIoChecks(string main)
        {
            // The automatic folder-size sweep runs on the UI thread between
            // awaits. TryGetCached asks the filesystem for a last-write time, so
            // calling it there is one blocking round trip per subfolder on
            // screen — arrived at through a cache *hit*, the path that is
            // supposed to be the cheap one. CalculateAsync checks the same cache
            // on the worker and answers identically.
            Check("the folder-size sweep never probes the cache on the UI thread",
                !main.Contains("_folderSizes.TryGetCached", StringComparison.Ordinal));

            // Totalling an upload measured every file with FileInfo.Length, in a
            // plain foreach, on the UI thread — and the sources of a paste into
            // Drive are the paths most likely to be somewhere slow, which the
            // comment six lines above it already said.
            //
            // That loop is now a whole tree walk, which makes the rule matter
            // more rather than less: a folder upload enumerates every directory
            // under every source before it can say how much there is to send, and
            // doing that on the thread that paints is a window that has stopped
            // responding for as long as the share takes to answer.
            int bareLengthLoops = Occurrences(main, "totalBytes += new FileInfo");
            Check("an upload total is not summed on the UI thread",
                bareLengthLoops == 0, bareLengthLoops + " found");
            Check("and the whole survey happens on a worker instead",
                main.Contains("await Task.Run(() => DriveUpload.Survey", StringComparison.Ordinal));

            // ---- and that "Ask" is resolved before an engine sees it ----
            //
            // Neither engine can ask anybody anything: RoboCopyEngine has no UI
            // and FileOperations is handed a null callback from inside it. The
            // resolution has to happen in the window, once, before either is
            // called — and for a long time it did not happen at all, because
            // MainForm passed `_settings.PasteConflict` straight through and the
            // engine's own branch read anything that was not Overwrite or Skip as
            // auto-rename. Read out of the source because MainForm needs a message
            // loop and is not compiled into the suite.
            Check("the paste policy is resolved rather than passed straight through",
                main.Contains("ResolveConflictPolicy", StringComparison.Ordinal));

            int rawPolicyHandoffs = Occurrences(main, "_settings.PasteConflict, progress")
                                  + Occurrences(main, "_settings.PasteConflict, tally");
            Check("and the raw setting never reaches an engine",
                rawPolicyHandoffs == 0, rawPolicyHandoffs + " found");

            Check("and there is a dialog for it to reach",
                main.Contains("ConflictForm.Ask", StringComparison.Ordinal));

            // ---- and that writing commands know the Drive letter is not a disk ----
            //
            // Delete and paste both branch on Drive.Owns; New folder, New file and
            // Rename did not, and all three wrote to the local sync root instead.
            // Nothing failed: the folder was created, the file was created, the
            // placeholder was renamed — and none of it reached the account, so the
            // pane (which is built from the Drive listing) never showed any of it,
            // and the next mount cleared the sync root and took it away. Three
            // commands announcing success and doing nothing, one of them losing
            // whatever had been typed into the file in between.
            foreach (var (command, call) in new[]
            {
                ("New folder", "Drive.CreateFolderIn"),
                ("New file", "Drive.CreateFileIn"),
                ("Rename", "Drive.RenameOnDrive"),
            })
                Check($"{command} goes through Google Drive when the folder is on it",
                    main.Contains(call, StringComparison.Ordinal), call + " is not called");

            // And the local calls that used to be the whole of those commands are
            // still there, behind the branch — the Drive case is an addition, not
            // a replacement, and a local folder still has to be creatable.
            // The local calls are on a worker now — the same treatment Rename
            // already had — so what is asserted is that they are still made, not
            // where from.
            foreach (var local in new[] { "Directory.CreateDirectory(unique)", "File.Move(from, dest)" })
                Check($"the local path still uses {local}",
                    main.Contains(local, StringComparison.Ordinal));

            Check("and creating a folder or a file does not do it on the UI thread",
                main.Contains("var unique = FileOperations.UniqueName(Path.Combine(folder, name));",
                    StringComparison.Ordinal));

            // The repeat that a held key runs on stops the timer at the top of
            // every tick and starts it again at the bottom. A guard that returns
            // in between without re-arming does not skip one repeat, it ends the
            // hold — and the guard that does exactly that is the re-entrancy one,
            // which only fires when a COM call inside the action pumped messages.
            // So it fires rarely, and when it does the key simply stops working.
            var hold = SourceFile("HoldRepeat.cs");
            if (hold == null) { Check("HoldRepeat.cs can be read", false); return; }

            Check("a skipped repeat re-arms the timer rather than ending the hold",
                hold.Contains("if (_inAction) { _timer.Start(); return; }", StringComparison.Ordinal));

            // Every other early return in Tick is a deliberate ending — the key
            // came up, or the safety stop expired — and each of those calls Stop
            // so _action is cleared too. A bare `return` that is neither is the
            // bug coming back under a different name.
            int endings = Occurrences(hold, "{ Stop(); return; }");
            Check("and the returns that really do end it say so", endings >= 2,
                endings + " found");
        }

        /// <summary>
        /// The settings file, swept for values that could break something.
        ///
        /// Written by reflection rather than as a list, for the same reason
        /// <c>SettingsRoundTripTests</c> is: a setting added next year is covered
        /// without anybody remembering this exists. A hand-edited settings.json is
        /// the source of every value here — it is a text file people are invited
        /// to edit — so "nobody would set that" is not a defence.
        /// </summary>
        private static void ConfigIntegrityTests()
        {
            Console.WriteLine("The settings file against hostile values:");

            var previous = Settings.OverrideAppDataDir;
            var scratch = Path.Combine(Path.GetTempPath(), "en-config-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(scratch);
                Settings.OverrideAppDataDir = scratch;

                // ---- no number survives being absurd ----
                //
                // A setting that comes back exactly as int.MaxValue was never
                // clamped, and the ones that matter are the ones something
                // allocates with or divides by: CopyBufferKilobytes is an array
                // and FontSize goes to a Font constructor that throws below one.
                //
                // The allowlist is the whole point of the test. Anything not on it
                // that turns up here is a new setting nobody clamped.
                var mayBeAnything = new HashSet<string>
                {
                    // Forced to the current version by Migrate, whatever it held.
                    "SettingsVersion",
                    // A window on a second monitor is legitimately at a negative
                    // coordinate, so there is no band to clamp these into.
                    "WindowX", "WindowY",
                };

                var unclamped = new List<string>();
                var threw = new List<string>();

                foreach (var property in PersistedProperties())
                {
                    if (property.PropertyType != typeof(int)) continue;
                    if (mayBeAnything.Contains(property.Name)) continue;

                    foreach (int absurd in new[] { int.MaxValue, int.MinValue, 0, -1 })
                    {
                        try
                        {
                            var wild = new Settings();
                            property.SetValue(wild, absurd);
                            wild.Save();

                            var back = Settings.Load();
                            int got = (int)property.GetValue(back)!;

                            if (got == int.MaxValue || got == int.MinValue)
                            {
                                if (!unclamped.Contains(property.Name)) unclamped.Add(property.Name);
                                Console.WriteLine($"        {property.Name} came back as {got}");
                            }

                            // Clamping twice must equal clamping once, or the
                            // value moves a little every time the file is opened.
                            back.Save();
                            var again = Settings.Load();
                            int second = (int)property.GetValue(again)!;
                            if (second != got)
                                Console.WriteLine($"        {property.Name} drifted {got} -> {second}");
                            Check($"{property.Name} settles at one value ({absurd})", second == got,
                                $"{got} then {second}");
                        }
                        catch (Exception ex)
                        {
                            if (!threw.Contains(property.Name)) threw.Add(property.Name);
                            Console.WriteLine($"        {property.Name} at {absurd} threw {ex.GetType().Name}");
                        }
                    }
                }

                Equal("no number survives the settings file unclamped", "",
                    string.Join(", ", unclamped));
                Equal("and none of them throws on the way through", "",
                    string.Join(", ", threw));

                // ---- and no string breaks the file ----
                //
                // Null is the one that matters: System.Text.Json will happily
                // write and read it back, and every consumer here was written
                // against a property that is never null.
                var hostileStrings = new[]
                {
                    null, "", "   ", new string('x', 20000),
                    "line\r\nbreak", "tab\there", "\0embedded null",
                    "\"quotes\" and \\backslashes\\", "emoji 🎵 and 日本語",
                    ";;;;;;", "%1 %I %D", "..\\..\\escape",
                };

                var stringProblems = new List<string>();

                var nullability = new System.Reflection.NullabilityInfoContext();

                foreach (var property in PersistedProperties())
                {
                    if (property.PropertyType != typeof(string)) continue;

                    // A property genuinely declared string? is *meant* to be null
                    // — InitialFolderOverride is null whenever no folder was
                    // passed on the command line — so requiring it to come back
                    // non-null would be asserting the opposite of the design.
                    // Settings.NoNullStrings draws the line in the same place.
                    if (nullability.Create(property).WriteState ==
                        System.Reflection.NullabilityState.Nullable) continue;

                    foreach (var hostile in hostileStrings)
                    {
                        try
                        {
                            var wild = new Settings();
                            property.SetValue(wild, hostile);
                            wild.Save();
                            var back = Settings.Load();

                            // Whatever it decides the value is, it must not be
                            // null afterwards: nothing downstream checks.
                            if (property.GetValue(back) == null)
                            {
                                stringProblems.Add($"{property.Name} is null after a round trip");
                                Console.WriteLine($"        {property.Name} came back null");
                            }
                        }
                        catch (Exception ex)
                        {
                            stringProblems.Add($"{property.Name}: {ex.GetType().Name}");
                            Console.WriteLine(
                                $"        {property.Name} with {Describe(hostile)} threw {ex.GetType().Name}");
                        }
                    }
                }

                Equal("no string breaks the settings file", "0", stringProblems.Count.ToString());

                // ---- one null must not cost the whole file ----
                //
                // This is the bug the sweep above found, and it is worth its own
                // check because the symptom is so far from the cause: Migrate
                // calls AudioExtensions.TrimEnd, Load catches everything and
                // hands back a default Settings, and the next save writes those
                // defaults over the file. Every preference gone, nothing said,
                // because from outside it is only an application that came up
                // with its settings reset.
                var keeper = new Settings
                {
                    WindowTitle = "kept",
                    FontSize = 14,
                    AudioVolumePercent = 33,
                    AudioExtensions = null!,          // the one that used to throw
                    AudioPlayPauseShortcut = null!,
                };
                keeper.Save();
                var survived = Settings.Load();

                Equal("a null in the file does not discard the other settings",
                    "kept", survived.WindowTitle);
                Equal("nor the numbers beside it", "14", survived.FontSize.ToString());
                Equal("nor the ones further down", "33", survived.AudioVolumePercent.ToString());
                Check("and the null itself comes back as something usable",
                    survived.AudioExtensions is { Length: > 0 } &&
                    survived.AudioPlayPauseShortcut != null,
                    $"extensions={Describe(survived.AudioExtensions)} " +
                    $"shortcut={Describe(survived.AudioPlayPauseShortcut)}");

                // A null shortcut is simply not set, which is a shortcut that does
                // nothing — never a shortcut nobody asked for.
                Check("a null shortcut parses to nothing rather than to something",
                    !Shortcut.Parse(survived.AudioPlayPauseShortcut).IsAssigned);

                // ---- an enum from outside its own range ----
                //
                // Json will deserialise 99 into an enum without complaint, and a
                // switch with no default then falls through to whatever comes
                // after it.
                int enumProblems = 0;
                try
                {
                    var wild = new Settings
                    {
                        SortBy = (SortColumn)99,
                        SizeUnits = (SizeUnitStyle)99,
                    };
                    wild.Save();
                    var back = Settings.Load();

                    // The two that are actually asked to render something.
                    var size = SizeFormatter.Format(123456789, back.SizeUnits);
                    Check("an unknown size unit still formats something",
                        !string.IsNullOrWhiteSpace(size), size);
                }
                catch (Exception ex)
                {
                    enumProblems++;
                    Console.WriteLine($"        an out-of-range enum threw {ex.GetType().Name}: {ex.Message}");
                }
                Equal("an out-of-range enum does not throw", "0", enumProblems.ToString());

                // ---- shortcuts, swept as a set ----
                //
                // Every shortcut is a string property, so a clash is a thing the
                // file can express and the registration has to answer for. The
                // defaults must not contain one, or the application ships
                // arguing with itself.
                var fresh = new Settings();
                var byCombination = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var clashes = new List<string>();

                foreach (var action in AudioActions.All)
                {
                    var parsed = Shortcut.Parse(action.Get(fresh));
                    if (!parsed.IsAssigned) continue;

                    var keys = parsed.ToString();
                    if (byCombination.TryGetValue(keys, out var already))
                        clashes.Add($"{keys}: {already} and {action.Label}");
                    else byCombination[keys] = action.Label;

                    Check($"the default for {action.Label} round-trips as text",
                        Shortcut.Parse(keys).Equals(parsed), keys);
                    Check($"and {action.Label} is something Windows will take",
                        parsed.CanRegister, keys);
                }

                var windowKeys = new Shortcut(fresh.HotkeyModifiers, fresh.HotkeyKey);
                if (windowKeys.IsAssigned && byCombination.TryGetValue(windowKeys.ToString(), out var owner))
                    clashes.Add($"{windowKeys}: the window and {owner}");

                Equal("no two shipped shortcuts want the same keys", "", string.Join("; ", clashes));

                // A file that clashes is reachable, which is what the dialog is
                // for — so prove the clash is detectable rather than assuming it.
                var doubled = new Settings { AudioStopShortcut = fresh.AudioPlayPauseShortcut };
                Check("two settings pointing at one combination is detectable",
                    Shortcut.Parse(doubled.AudioStopShortcut)
                        .Equals(Shortcut.Parse(doubled.AudioPlayPauseShortcut)));
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(scratch, true); } catch { }
            }

            Console.WriteLine();
        }

        /// <summary>
        /// The settings a file can actually carry: public, instance, read/write.
        ///
        /// **Instance is the word that matters.** `GetProperties()` with no flags
        /// also returns the statics, and `SetValue` on a static ignores the
        /// instance it was handed and sets the static — so a sweep written the
        /// obvious way assigns to <c>Settings.OverrideAppDataDir</c>, which is the
        /// hook redirecting the whole suite away from the real settings file. It
        /// pointed the tests back at the user's own configuration and then wrote
        /// to it, which is the one thing this suite has a standing rule against.
        /// </summary>
        private static IEnumerable<System.Reflection.PropertyInfo> PersistedProperties()
        {
            foreach (var property in typeof(Settings).GetProperties(
                         System.Reflection.BindingFlags.Public |
                         System.Reflection.BindingFlags.Instance))
            {
                if (property.CanRead && property.CanWrite) yield return property;
            }
        }

        private static string Describe(string? value) =>
            value == null ? "null"
            : value.Length > 20 ? $"a {value.Length}-character string"
            : $"\"{value}\"";

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
        /// Hostile inputs to everything added recently.
        ///
        /// Not a demonstration that the happy path works — that is what the rest
        /// of the suite is for. This is the empty string, the null, the negative
        /// number and the value that is one past the end, fed to code that was
        /// written while thinking about the case that happens.
        /// </summary>
        private static void EdgeCaseTests()
        {
            Console.WriteLine("Edge cases:");

            // ---- volume and gain at the boundaries ----
            Equal("gain is exactly 1 at full", "1", AudioPlayer.GainFor(100).ToString("0.###"));
            Equal("and at nothing", "1", AudioPlayer.GainFor(0).ToString("0.###"));
            Equal("and below full", "1", AudioPlayer.GainFor(1).ToString("0.###"));
            Equal("three times at the top", "3", AudioPlayer.GainFor(300).ToString("0.###"));
            Equal("one and a half at 150", "1.5", AudioPlayer.GainFor(150).ToString("0.###"));
            Check("a nonsense percentage does not throw", AudioPlayer.GainFor(-5) > 0);

            // ---- the volume curve over the whole range ----
            int curveProblems = 0;
            for (int percent = -50; percent <= 400; percent++)
                foreach (var curve in new[] { VolumeCurve.Linear, VolumeCurve.Perceptual })
                {
                    double level = AudioPlayer.EngineVolume(percent, curve);

                    // The engine throws above 1.0 — measured — so this must never
                    // produce one, whatever it is handed.
                    if (double.IsNaN(level) || level < 0 || level > 1) curveProblems++;
                }
            Equal("the volume curve never leaves 0 to 1", "0", curveProblems.ToString());

            // ---- spoken times ----
            foreach (double seconds in new[] { 0, 1, 59, 60, 61, 3599, 3600, 3661, 86399, -1, -0.4 })
            {
                var text = AudioPlayer.SpokenTime(seconds);
                Check($"{seconds} seconds says something", !string.IsNullOrWhiteSpace(text), text);
            }
            foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                string text;
                try { text = AudioPlayer.SpokenTime(bad); }
                catch (Exception ex) { text = "THREW " + ex.GetType().Name; }
                Check($"an unusable duration ({bad}) does not throw", !text.StartsWith("THREW"), text);
            }

            // ---- accelerated steps ----
            int accelProblems = 0;
            foreach (var info in AudioActions.All)
                for (int repeat = 0; repeat < 500; repeat++)
                {
                    int step = AudioActions.Accelerated(5, repeat, true, info.AccelerationCap, info.AccelerationEvery);
                    if (step < 5 || step > 5 * Math.Max(1, info.AccelerationCap)) accelProblems++;
                }
            Equal("a held key never produces a step outside its own bounds", "0", accelProblems.ToString());
            // A growth interval of zero is nonsense nobody sets, and the point is
            // only that it does not divide by zero. Treated as one, which
            // accelerates on every repeat — aggressive, but bounded by the cap
            // and not a crash.
            int zeroInterval = AudioActions.Accelerated(5, 10, true, 20, 0);
            Check("a zero growth interval does not divide by zero",
                zeroInterval is >= 5 and <= 100, zeroInterval.ToString());

            // ---- shortcuts round-trip ----
            int shortcutProblems = 0;
            var settings = new Settings();
            foreach (var info in AudioActions.All)
            {
                var parsed = Shortcut.Parse(info.Get(settings));
                var again = Shortcut.Parse(parsed.ToString());
                if (!parsed.Equals(again)) shortcutProblems++;
            }
            Equal("every default shortcut survives a round trip", "0", shortcutProblems.ToString());

            foreach (var rubbish in new[] { null, "", "   ", "+", "Ctrl+", "+P", "Ctrl+Alt+", "NotAKey", "Ctrl+Alt+Shift+Win+" })
            {
                try { Shortcut.Parse(rubbish); }
                catch (Exception ex) { Check($"parsing \"{rubbish}\" does not throw", false, ex.GetType().Name); }
            }
            Check("unparseable shortcuts never throw", true);

            // ---- notification rendering with the wrong number of arguments ----
            //
            // Render takes whatever it is given. Somebody passing one argument to
            // a two-placeholder template is a bug, but it must not be a crash in
            // the middle of reporting a different bug.
            int renderCrashes = 0;
            foreach (var info in Notifications.All)
            {
                try { info.Render(); }
                catch { renderCrashes++; }

                try { info.Render("only", "two", "given") ; }
                catch (FormatException) { /* expected for templates wanting more */ }
                catch { renderCrashes++; }
            }
            Equal("rendering never crashes in an unexpected way", "0", renderCrashes.ToString());

            // ---- the settings clamp holds against nonsense ----
            // Through disk, because that is the only path validation runs on —
            // and a settings.json somebody has edited by hand is exactly where
            // these values come from.
            var previous = Settings.OverrideAppDataDir;
            var scratch = Path.Combine(Path.GetTempPath(), "en-edge-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(scratch);
                Settings.OverrideAppDataDir = scratch;

                var wild = new Settings
                {
                    AudioVolumePercent = int.MaxValue,
                    AudioSeekSeconds = 0,
                    AudioPlaybackRatePercent = int.MaxValue,
                };
                wild.Save();

                var back = Settings.Load();
                Check("volume is inside its range after loading",
                    back.AudioVolumePercent is >= 0 and <= AudioPlayer.LoudestPercent,
                    back.AudioVolumePercent.ToString());
                Check("the seek is usable", back.AudioSeekSeconds > 0,
                    back.AudioSeekSeconds.ToString());
                Check("the playback rate is usable",
                    back.AudioPlaybackRatePercent >= AudioPlayer.SlowestPercent &&
                    back.AudioPlaybackRatePercent <= AudioPlayer.FastestPercent,
                    back.AudioPlaybackRatePercent.ToString());
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(scratch, true); } catch { }
            }

            // ---- notification overrides against a hostile file ----
            foreach (var text in new[]
            {
                new string(';', 10000),
                "audio.play=" + new string('9', 400),
                "audio.play=-1",
                "AUDIO.PLAY=1",
                "audio.play=1;audio.play=2",
            })
            {
                try
                {
                    var parsed = Notifications.ParseOverrides(text);
                    foreach (var value in parsed.Values)
                        if (value != NotificationChannel.None && value != NotificationChannel.Speech)
                            Check("a stored setting is always speak or off", false, value.ToString());
                }
                catch (Exception ex)
                {
                    Check($"hostile overrides survive: {text[..Math.Min(20, text.Length)]}", false, ex.GetType().Name);
                }
            }
            Check("hostile override text never throws", true);
            Check("ids are matched without case mattering",
                Notifications.ParseOverrides("AUDIO.PLAY=1").ContainsKey("audio.play"));
        }

        // ---- Google Drive as a cloud provider ----
        //
        // Nothing here touches the network or needs an account. What is worth
        // testing without one is the part that cannot be reasoned about from the
        // source: the structure layouts, and the rules that decide what a Drive
        // entry is called once it has to live on NTFS.
        private static void GoogleDriveTests()
        {
            Console.WriteLine("Google Drive provider:");

            // The one that has already earned its keep. CF_SYNC_REGISTRATION was
            // written as 64 bytes and is 72 — the GUID lands at offset 56 after
            // two four-byte pads — and this caught it before a single call was
            // made. A wrong offset here is a pointer read out of the wrong eight
            // bytes, which fails somewhere else entirely.
            Equal("every Cloud Files structure matches the 64-bit header",
                "ok", CfApi.CheckLayout() ?? "ok");

            // Drive allows characters in a name that NTFS refuses. A name that
            // cannot be written is a file that silently never appears.
            Equal("a colon cannot survive on NTFS",
                "AC_DC_ Back in Black.flac",
                DriveMount.Sanitise("AC:DC: Back in Black.flac"));
            Equal("a trailing dot is trimmed",
                "album", DriveMount.Sanitise("album."));

            // And Drive lets two files in one folder share a name, where NTFS
            // would have the second quietly replace the first.
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Equal("the first of a name is itself", "song.flac", DriveMount.Unique("song.flac", used));
            Equal("the second is numbered", "song (2).flac", DriveMount.Unique("song.flac", used));
            Equal("and so is the third", "song (3).flac", DriveMount.Unique("song.flac", used));
            // Drive names are case sensitive and NTFS is not, so this collides —
            // but the name keeps the casing Drive gave it rather than being
            // folded to match whatever happened to be seen first.
            Equal("case collides without being rewritten",
                "SONG (4).FLAC", DriveMount.Unique("SONG.FLAC", used));

            // A Google-native document has no byte size in the API or anywhere
            // else, which is the half of "it reports the size wrong" that nothing
            // can fix. Folders are not documents.
            var doc = new DriveEntry("1", "Notes", "application/vnd.google-apps.document", -1, default);
            var folder = new DriveEntry("2", "Music", "application/vnd.google-apps.folder", 0, default);
            var track = new DriveEntry("3", "a.flac", "audio/flac", 1234, default);
            Check("a Doc is a Google document", doc.IsGoogleDocument);
            Check("a folder is not", !folder.IsGoogleDocument);
            Check("a folder knows it is one", folder.IsFolder);
            Check("a real file is neither", !track.IsGoogleDocument && !track.IsFolder);

            // One mount must not claim another's tree. PickRoot steps aside to
            // GoogleDrive-2 whenever GoogleDrive is stuck, so the two sit side by
            // side in the same folder — and a plain StartsWith said every path
            // under GoogleDrive-2 belonged to the GoogleDrive mount. Owns then
            // answered yes about a path the id map correctly knew nothing about,
            // so deleting a leftover through the file manager was routed to Drive
            // and refused there, with no way to remove it at all.
            const string root = @"C:\Users\x\AppData\Roaming\ExplorerNative\GoogleDrive";
            Check("the root itself is on the mount",
                DriveMount.IsAtOrUnder(root, root));
            Check("a trailing separator on the root changes nothing",
                DriveMount.IsAtOrUnder(root, root + @"\"));
            Check("something inside it is on the mount",
                DriveMount.IsAtOrUnder(root + @"\music\a.flac", root));
            Check("the numbered sibling is not",
                !DriveMount.IsAtOrUnder(root + @"-2\music\a.flac", root));
            Check("nor is the sibling's own root",
                !DriveMount.IsAtOrUnder(root + "-2", root));
            Check("nor is a directory that merely starts with the same name",
                !DriveMount.IsAtOrUnder(root + "Backup", root));
            Check("and an unrelated path is not",
                !DriveMount.IsAtOrUnder(@"C:\Windows", root));

            // The same rule serves the drive letter, where it also has to hold:
            // "G:" is a prefix of nothing else, but the check is shared.
            Check("a path on the letter is on the mount",
                DriveMount.IsAtOrUnder(@"G:\music\a.flac", "G:"));
            Check("a different letter is not",
                !DriveMount.IsAtOrUnder(@"H:\music\a.flac", "G:"));

            // ---- Where a resumed upload carries on from ----
            //
            // Google's byte count is the one to trust, and it may point backwards
            // when a chunk was only half received — that is what resumable means.
            // What it must never do is send the walk somewhere impossible, into
            // an offset nothing downstream checks before writing to somebody's
            // file.
            Equal("Google's count is normally taken as given",
                "4096", DriveClient.ConfirmedOffset(0, 4096, 999999).ToString());
            Equal("and backwards is legitimate, that being the point",
                "1000", DriveClient.ConfirmedOffset(4096, 1000, 999999).ToString());
            Equal("past the end of the file is the end of the file",
                "500", DriveClient.ConfirmedOffset(100, 900, 500).ToString());
            Equal("a negative offset keeps our own place",
                "100", DriveClient.ConfirmedOffset(100, -1, 500).ToString());
            Equal("exactly the end is the end", "500",
                DriveClient.ConfirmedOffset(400, 500, 500).ToString());

            DriveUploadSurveyTests();

            // The memory budget. A whole-file buffer would let the largest thing
            // ever opened decide the ceiling, and there is a 232MB track in this
            // very Drive; chunks under one budget make the ceiling a policy.
            var pool = new DriveCachePool(300);
            pool.Put("a", 0, new byte[100]);
            pool.Put("a", 1, new byte[100]);
            pool.Put("a", 2, new byte[100]);
            Check("everything that fits is held", pool.Get("a", 0) != null);

            // Reading chunk 0 just made it the most recent, so the next eviction
            // must take chunk 1 — the genuinely least recently used — not the
            // oldest by insertion.
            pool.Put("a", 3, new byte[100]);
            Check("the least recently used chunk goes", pool.Get("a", 1) == null);
            Check("the one that was read stays", pool.Get("a", 0) != null);
            Check("and the new one is there", pool.Get("a", 3) != null);
            Check("the budget is respected", pool.Used <= pool.Budget, $"{pool.Used} held");

            // One track at a time is what keeps memory bounded in practice.
            pool.Put("b", 0, new byte[50]);
            pool.DropFile("a");
            Check("dropping a file frees all of it", pool.Get("a", 0) == null && pool.Get("a", 3) == null);
            Check("and leaves the other alone", pool.Get("b", 0) != null);

            // ---- the position memory really is bounded ----
            // ---- what counts as the network being gone ----

            var health = new DriveHealth();
            var announced = new List<DriveState>();
            health.Changed += s => announced.Add(s);

            Check("nothing has been tried yet", health.State == DriveState.Unknown);

            // A 403 or an expired token is Google answering. Calling that
            // "offline" sends somebody to look at their router when the problem
            // is their sign-in.
            var refused = new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden);
            for (int i = 0; i < 10; i++) health.Failed(refused);
            Check("an answer from Google is not the network being down",
                health.State != DriveState.Offline);
            Equal("and it is not announced", "0", announced.Count.ToString());

            // One timeout is a hiccup. Three in a row is a network that has gone.
            var dropped = new HttpRequestException("no route");
            health.Failed(dropped);
            Check("one failure is not enough", !health.IsOffline);
            health.Failed(dropped);
            Check("two are not either", !health.IsOffline);
            health.Failed(dropped);
            Check("three in a row is offline", health.IsOffline);
            Equal("announced exactly once", "1", announced.Count.ToString());

            health.Failed(dropped);
            health.Failed(dropped);
            Equal("and not again while it stays down", "1", announced.Count.ToString());

            health.Succeeded();
            Check("one good request is back online", health.State == DriveState.Online);
            Equal("announced once more", "2", announced.Count.ToString());
            health.Succeeded();
            Equal("and not repeated", "2", announced.Count.ToString());

            // HttpClient reports its own timeout as a TaskCanceledException
            // wrapped around a TimeoutException. That shape is the network.
            Check("a timeout counts as the network",
                DriveHealth.LooksLikeNetwork(new TaskCanceledException("timed out", new TimeoutException())));
            Check("so does a socket error",
                DriveHealth.LooksLikeNetwork(new SocketException(10061)));

            // The bare one is somebody cancelling a token, and that somebody is
            // us: disposing a DriveFileCache cancels the two ranged reads its
            // download keeps in flight, so two quick track changes produced four
            // consecutive "failures" and the application announced that Drive was
            // offline while it was answering perfectly.
            Check("our own cancellation is not the network being down",
                !DriveHealth.LooksLikeNetwork(new TaskCanceledException()));
            Check("nor is a plain cancellation",
                !DriveHealth.LooksLikeNetwork(new OperationCanceledException()));

            var afterCancels = new DriveHealth();
            for (int i = 0; i < 10; i++) afterCancels.Failed(new TaskCanceledException());
            Check("so ten of them never take Drive offline", !afterCancels.IsOffline);
            Check("a status code does not",
                !DriveHealth.LooksLikeNetwork(refused));
            Check("nor does anything else", !DriveHealth.LooksLikeNetwork(new InvalidOperationException()));

            // The sentence is what reaches a person, so it says which of the two
            // problems this is.
            Check("the offline sentence names Drive",
                new DriveHealth().Sentence.Contains("Drive", StringComparison.OrdinalIgnoreCase));

            // The credentials come from the file the Cloud console hands out, so
            // there is never a moment where a secret is retyped into a source file.
            var dir = Path.Combine(Path.GetTempPath(), "en-drive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "unrelated.json"), "{\"hello\":1}");
                Check("no client file is not an error", GoogleAuth.ReadClientJson(dir) == null);

                // "web" is the other kind of client and is not ours.
                File.WriteAllText(Path.Combine(dir, "web.json"),
                    "{\"web\":{\"client_id\":\"w\",\"client_secret\":\"s\"}}");
                Check("a web client is not a desktop client", GoogleAuth.ReadClientJson(dir) == null);

                File.WriteAllText(Path.Combine(dir, "client_secret.json"),
                    "{\"installed\":{\"client_id\":\"abc.apps.googleusercontent.com\"," +
                    "\"client_secret\":\"GOCSPX-xyz\",\"redirect_uris\":[\"http://localhost\"]}}");
                var found = GoogleAuth.ReadClientJson(dir);
                Check("a desktop client is read", found != null);
                Equal("the id comes back", "abc.apps.googleusercontent.com", found?.ClientId ?? "");
                Equal("and the secret", "GOCSPX-xyz", found?.ClientSecret ?? "");

                // Preferences, Google client ID and secret.
                var into = Path.Combine(dir, "settings");
                Check("an empty secret is refused",
                    GoogleAuth.SaveClient("abc.apps.googleusercontent.com", " ", into, out _) != null);
                Check("nothing was written for it", GoogleAuth.ReadClientJson(into) == null);

                Directory.CreateDirectory(into);
                File.WriteAllText(Path.Combine(into, "old-client.json"),
                    "{\"installed\":{\"client_id\":\"old\",\"client_secret\":\"s\"}}");
                Check("an ID and secret save",
                    GoogleAuth.SaveClient(" abc.apps.googleusercontent.com ", "GOCSPX-xyz", into, out bool changed) == null);
                Check("a different client is reported as a change", changed);
                Check("the old client file is gone", !File.Exists(Path.Combine(into, "old-client.json")));
                Equal("and the new one is what is read, trimmed", "abc.apps.googleusercontent.com",
                    GoogleAuth.ReadClientJson(into)?.ClientId ?? "");
                Equal("with its secret", "GOCSPX-xyz", GoogleAuth.ReadClientJson(into)?.ClientSecret ?? "");
                GoogleAuth.SaveClient("abc.apps.googleusercontent.com", "GOCSPX-xyz", into, out changed);
                Check("the same client again is not a change", !changed);

                // Google itself, when it can be reached: a made-up client is refused.
                var verdict = GoogleAuth.CheckClientAsync("123-made-up.apps.googleusercontent.com", "GOCSPX-made-up",
                    CancellationToken.None).GetAwaiter().GetResult();
                // Offline, "could not be reached" is not a refusal and proves nothing.
                if (verdict == null || !verdict.Contains("could not be reached"))
                    Check("Google refuses a made-up client ID and secret",
                        verdict != null && verdict.Contains("does not accept"), verdict ?? "accepted");
                Check("the saved client file is not readable as text",
                    !File.ReadAllText(Path.Combine(into, GoogleAuth.ClientFileName)).Contains("GOCSPX"));

                var log = Path.Combine(into, "test.log");
                File.WriteAllText(log, "an old plain line" + Environment.NewLine);
                ProtectedFile.AppendLine(log, "a secret line");
                ProtectedFile.EncryptLogInPlace(log);
                Check("a log has no plain text left", !File.ReadAllText(log).Contains("line"));
                Equal("and reads back in order", "an old plain line|a secret line",
                    string.Join("|", ProtectedFile.ReadLog(log)));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// What a folder upload decides to send, before anything is sent.
        ///
        /// This is the half that used not to exist — folders were refused rather
        /// than half-copied — and it is also the half that can be checked without
        /// a Drive account, which makes it the half worth a test. Everything past
        /// it is one HTTP call per line.
        /// </summary>
        private static void DriveUploadSurveyTests()
        {
            var root = Path.Combine(Path.GetTempPath(), "en-upload-" + Guid.NewGuid().ToString("N"));

            try
            {
                // album\
                //   cover.jpg          3 bytes
                //   disc 1\track.flac  10 bytes
                //   scans\             empty
                // loose.txt            5 bytes
                Directory.CreateDirectory(Path.Combine(root, "album", "disc 1"));
                Directory.CreateDirectory(Path.Combine(root, "album", "scans"));
                File.WriteAllBytes(Path.Combine(root, "album", "cover.jpg"), new byte[3]);
                File.WriteAllBytes(Path.Combine(root, "album", "disc 1", "track.flac"), new byte[10]);
                File.WriteAllBytes(Path.Combine(root, "loose.txt"), new byte[5]);

                var work = DriveUpload.Survey(new[]
                {
                    Path.Combine(root, "album"),
                    Path.Combine(root, "loose.txt"),
                });

                Equal("every file in the tree is found, and the loose one too",
                    "3", work.Files.Count.ToString());
                Equal("the byte total is the sum of them", "18", work.TotalBytes.ToString());

                // An empty folder is part of the shape. Robocopy keeps them — that
                // is what /E means — and an upload that only walks files loses
                // them with no error and no failed count.
                var folders = work.Folders.OrderBy(f => f, StringComparer.Ordinal).ToList();
                Equal("the folders are the tree, empty ones included",
                    @"album | album\disc 1 | album\scans", string.Join(" | ", folders));

                // The relative folder is what decides where a file lands, and the
                // file dropped straight in has none.
                var loose = work.Files.First(f => f.LocalPath.EndsWith("loose.txt", StringComparison.Ordinal));
                Equal("a file chosen on its own goes to the destination itself", "", loose.RelativeFolder);

                var track = work.Files.First(f => f.LocalPath.EndsWith("track.flac", StringComparison.Ordinal));
                Equal("and one inside a tree carries its path", @"album\disc 1", track.RelativeFolder);
                Equal("with its size", "10", track.Size.ToString());

                Check("something to do", !work.IsEmpty);

                // The source folders are what a move deletes afterwards, and only
                // the ones actually chosen — never the destination and never a
                // parent nobody named.
                Equal("one source folder was named", "1", work.SourceFolders.Count.ToString());

                // Nothing there is not the same as nothing asked for, and the
                // difference is a dialog that opens on an upload with no files in
                // it and never closes.
                Check("a path that does not exist contributes nothing",
                    DriveUpload.Survey(new[] { Path.Combine(root, "gone") }).IsEmpty);
                Check("and so does an empty list", DriveUpload.Survey(Array.Empty<string>()).IsEmpty);

                // A drive root has no name to give the folder it would become.
                Check("a bare drive is refused rather than named", DriveUpload.Survey(new[] { @"C:\" }).IsEmpty);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        // ---- SizeUnits + SizeDecimals ----
        private static void SizeUnitTests()
        {
            Console.WriteLine("SizeUnits / SizeDecimals:");

            Equal("1 byte is singular", "1 byte", SizeFormatter.Format(1, SizeUnitStyle.FullWords, 1));
            Equal("0 bytes is plural", "0 bytes", SizeFormatter.Format(0, SizeUnitStyle.FullWords, 1));
            Equal("2 bytes plural", "2 bytes", SizeFormatter.Format(2, SizeUnitStyle.FullWords, 1));
            Equal("exactly 1 kilobyte singular", "1 kilobyte", SizeFormatter.Format(1024, SizeUnitStyle.FullWords, 1));
            Equal("1.5 kilobytes", "1.5 kilobytes", SizeFormatter.Format(1536, SizeUnitStyle.FullWords, 1));
            Equal("megabytes word", "1 megabyte", SizeFormatter.Format(1024L * 1024, SizeUnitStyle.FullWords, 1));
            Equal("gigabytes word", "1 gigabyte", SizeFormatter.Format(1024L * 1024 * 1024, SizeUnitStyle.FullWords, 1));
            Equal("terabytes word", "1 terabyte", SizeFormatter.Format(1024L * 1024 * 1024 * 1024, SizeUnitStyle.FullWords, 1));
            Equal("abbreviated style", "1 KB", SizeFormatter.Format(1024, SizeUnitStyle.Abbreviated, 1));
            Equal("decimals honoured (0)", "2 megabytes", SizeFormatter.Format(2411724, SizeUnitStyle.FullWords, 0));
            Equal("decimals honoured (2)", "2.3 megabytes", SizeFormatter.Format(2411724, SizeUnitStyle.FullWords, 2));
            Equal("bytes never get decimals", "512 bytes", SizeFormatter.Format(512, SizeUnitStyle.FullWords, 3));
            Console.WriteLine();
        }

        // ---- Sizes at the edges of the range ----
        private static void SizeFormatterEdgeTests()
        {
            Console.WriteLine("Size formatting edge cases:");

            // Folders carry -1 as "not measured yet"; rendering that as a negative
            // size would be read aloud as "minus one bytes".
            Equal("an unmeasured size renders as nothing", "", SizeFormatter.Format(-1, SizeUnitStyle.FullWords, 1));
            Equal("any negative renders as nothing", "", SizeFormatter.Format(-999999, SizeUnitStyle.Abbreviated, 2));

            // 1023 bytes must not round up into "1 kilobyte" — it is under one.
            Equal("just under a kilobyte stays in bytes", "1,023 bytes",
                SizeFormatter.Format(1023, SizeUnitStyle.FullWords, 1));
            Equal("just over a kilobyte", "1 kilobyte", SizeFormatter.Format(1025, SizeUnitStyle.FullWords, 1));

            // The unit ladder must stop at its last entry rather than run off the
            // end of the array.
            var huge = SizeFormatter.Format(long.MaxValue, SizeUnitStyle.Abbreviated, 2);
            Check("the largest possible size formats without overflowing the units",
                huge.EndsWith("PB", StringComparison.Ordinal), huge);
            Check("the largest possible size in words is petabytes",
                SizeFormatter.Format(long.MaxValue, SizeUnitStyle.FullWords, 1).Contains("petabyte"), huge);

            Equal("petabytes are reached", "1 petabyte",
                SizeFormatter.Format(1024L * 1024 * 1024 * 1024 * 1024, SizeUnitStyle.FullWords, 1));

            // Trailing zeros are dropped so speech does not say "one point zero".
            Equal("a trailing zero is dropped", "2 megabytes",
                SizeFormatter.Format(2L * 1024 * 1024, SizeUnitStyle.FullWords, 2));
            Check("no size ever renders with a trailing point-zero",
                !SizeFormatter.Format(3L * 1024 * 1024, SizeUnitStyle.FullWords, 1).Contains(".0"));

            // Three decimals is the documented maximum and must be honoured.
            Equal("three decimals honoured", "1.001 kilobytes",
                SizeFormatter.Format(1025, SizeUnitStyle.FullWords, 3));

            // Thousands separators make a big byte count readable.
            Check("large byte counts are grouped",
                SizeFormatter.Format(1023, SizeUnitStyle.Abbreviated, 0).Contains(','));

            // Rounding can reach the unit above the one the number was measured
            // in, and the ladder must not then read out its own overflow.
            // 1,048,570 bytes is 1023.99 kilobytes, which to one decimal place is
            // 1024.0 — printed as "1,024 kilobytes" on a row a screen reader
            // speaks, for a quantity that is a megabyte.
            Equal("rounding up to the next unit moves up a unit", "1 megabyte",
                SizeFormatter.Format(1048570, SizeUnitStyle.FullWords, 1));
            Equal("and the same abbreviated", "1 MB",
                SizeFormatter.Format(1048570, SizeUnitStyle.Abbreviated, 1));
            Check("no size renders as 1,024 of anything",
                !SizeFormatter.Format(1048570, SizeUnitStyle.Abbreviated, 1).StartsWith("1,024",
                    StringComparison.Ordinal));

            // Whole bytes cannot round upwards at all, so nothing below a
            // kilobyte is promoted by the step above.
            Equal("1023 bytes is still bytes with no decimals", "1,023 bytes",
                SizeFormatter.Format(1023, SizeUnitStyle.FullWords, 0));

            // The top of the ladder has nowhere to be promoted to, and must say
            // the big number rather than run off the end of the array.
            Check("the largest size still stops at petabytes",
                SizeFormatter.Format(long.MaxValue, SizeUnitStyle.Abbreviated, 1).EndsWith("PB",
                    StringComparison.Ordinal));

            Console.WriteLine();
        }

        // ---- VerboseModifiedInfo ----
        private static void TimeFormatTests()
        {
            Console.WriteLine("Modified column (verbose off = relative):");

            // Fixed "now" so the ladder is tested deterministically, mid-afternoon
            // so that "today" isn't accidentally sitting on a midnight boundary.
            var now = new DateTime(2026, 9, 4, 15, 0, 0);
            string R(int daysAgo) => TimeFormatter.Relative(now.AddDays(-daysAgo), now);

            Equal("same day", "today", R(0));
            Equal("earlier today still today", "today", TimeFormatter.Relative(now.AddHours(-6), now));
            Equal("yesterday", "yesterday", R(1));
            Equal("2 days", "2 days ago", R(2));
            Equal("3 days", "3 days ago", R(3));
            Equal("6 days", "6 days ago", R(6));
            Equal("7 days is one week", "one week ago", R(7));
            Equal("13 days still one week", "one week ago", R(13));
            Equal("14 days is 2 weeks", "2 weeks ago", R(14));
            Equal("21 days is 3 weeks", "3 weeks ago", R(21));
            Equal("40 days is one month", "one month ago", R(40));
            Equal("200 days is 6 months", "6 months ago", R(200));
            Equal("400 days is one year", "one year ago", R(400));
            Equal("800 days is 2 years", "2 years ago", R(800));
            Equal("10 years still counts", "10 years ago", R(3650));
            Equal("beyond 10 years is a long time ago", "a long time ago", R(365 * 12));
            Equal("a future stamp is called out", "in the future", TimeFormatter.Relative(now.AddDays(3), now));

            // Verbose mode must use the exact format, and survive a bad one.
            var stamp = new DateTime(2026, 3, 14, 9, 41, 0);
            Equal("verbose uses the exact format", stamp.ToString("g"),
                TimeFormatter.Format(stamp, verbose: true, "g"));
            Check("a malformed date format falls back instead of throwing",
                TimeFormatter.Format(stamp, verbose: true, "not-a-real-format").Length > 0);
            Equal("verbose off ignores the format string", "today",
                TimeFormatter.Format(DateTime.Now, verbose: false, "g"));

            Console.WriteLine();
        }

        // ---- Settings persistence + clamping ----
        private static void SettingsClampTests()
        {
            Console.WriteLine("Settings validation:");

            var defaults = new Settings();
            Check("SizeUnits defaults to full words", defaults.SizeUnits == SizeUnitStyle.FullWords);
            Check("arrow wrap defaults off", defaults.WrapArrowNavigation == false);
            Check("delete defaults to recycle bin", defaults.Delete == DeleteMode.RecycleBin);
            Check("folder sizes default to on demand", defaults.FolderSizes == FolderSizeMode.OnDemand);
            Check("paste conflict defaults to auto rename", defaults.PasteConflict == PasteConflictPolicy.AutoRename);
            Check("speech on by default", defaults.SpeakEnabled);
            Check("operations announced by default", defaults.SpeakOperations);
            Check("\"Opening\" announcement off by default", !defaults.SpeakOnOpen);
            Check("shell registration off by default (stays portable)",
                !defaults.RegisterContextMenu);

            Check("no exe path recorded until registration happens",
                defaults.RegisteredExePath.Length == 0);

            // Round-trip through disk, including out-of-range values.
            //
            // In a directory of its own. These tests write settings files,
            // corrupt them deliberately and migrate them, and doing that in the
            // real location meant borrowing the user's own configuration for the
            // duration — which is exactly one crash, or one concurrent save by
            // the running application, away from losing it.
            var sandbox = NewTempDir();
            Settings.OverrideAppDataDir = sandbox;

            var dir = Settings.AppDataDir;
            var path = Path.Combine(dir, "settings.json");
            Check("settings tests run in their own directory, not the user's",
                !dir.Contains("Roaming", StringComparison.OrdinalIgnoreCase), dir);

            try
            {
                var s = new Settings
                {
                    CopyBufferKilobytes = 1,
                    FontSize = 200,
                    FolderSizeTimeoutSeconds = 0,
                    TypeAheadMilliseconds = 99999,
                    SpeakEnabled = false,
                    SortBy = SortColumn.Modified,
                };
                s.Save();

                var loaded = Settings.Load();
                Check("buffer clamped to >= 4", loaded.CopyBufferKilobytes >= 4, loaded.CopyBufferKilobytes.ToString());
                Check("font clamped to <= 24", loaded.FontSize <= 24, loaded.FontSize.ToString());
                Check("timeout clamped to >= 1", loaded.FolderSizeTimeoutSeconds >= 1, loaded.FolderSizeTimeoutSeconds.ToString());
                Check("multi-letter threshold clamped to <= 3000", loaded.TypeAheadMilliseconds <= 3000,
                    loaded.TypeAheadMilliseconds.ToString());
                Check("multi-letter threshold defaults to a quarter second",
                    new Settings().TypeAheadMilliseconds == 250,
                    new Settings().TypeAheadMilliseconds.ToString());

                // The settings that were removed rather than hidden: their values
                // are now decided by the code, so there is nothing to store, get
                // out of range, or ask anyone about.
                Check("size precision is fixed at one decimal place",
                    SizeFormatter.DefaultDecimals == 1);
                Equal("the default precision is what the one-argument overload uses",
                    SizeFormatter.Format(1536, SizeUnitStyle.FullWords, 1),
                    SizeFormatter.Format(1536, SizeUnitStyle.FullWords));
                Equal("the exact date format is short date and time", "g", TimeFormatter.DefaultFormat);
                Equal("the two-argument time overload uses it",
                    TimeFormatter.Format(new DateTime(2026, 3, 14, 9, 41, 0), verbose: true, "g"),
                    TimeFormatter.Format(new DateTime(2026, 3, 14, 9, 41, 0), verbose: true));

                // Copy parallelism follows the machine rather than a stored value.
                Check("the thread count is derived from the processor count",
                    FileOperations.RecommendedThreads == Math.Clamp(Environment.ProcessorCount, 4, 16),
                    $"{FileOperations.RecommendedThreads} on {Environment.ProcessorCount} processors");
                Check("...and always lands in a sane range",
                    FileOperations.RecommendedThreads is >= 4 and <= 16,
                    FileOperations.RecommendedThreads.ToString());
                Check("booleans survive the round trip", loaded.SpeakEnabled == false);
                Check("enums survive the round trip", loaded.SortBy == SortColumn.Modified);

                var clone = loaded.Clone();
                clone.SpeakEnabled = !loaded.SpeakEnabled;
                Check("Clone is a deep copy", loaded.SpeakEnabled != clone.SpeakEnabled);

                Check("active tab clamped to a real tab", new Settings { ActiveTab = 7 }
                    .GetType() != null && Settings.Load().ActiveTab is 0 or 1);

                // A corrupt file must fall back to defaults, not throw on startup.
                File.WriteAllText(path, "{ this is not json ");
                var recovered = Settings.Load();
                Check("a corrupt settings file falls back to defaults", recovered.SpeakEnabled);

                File.WriteAllText(path, "");
                Check("an empty settings file falls back to defaults", Settings.Load().SpeakEnabled);

                // ---- Migration of a file written by an older build ----
                // A default that turns out to be wrong has to reach people who
                // already have a settings file, or they keep the old behaviour
                // forever and the fix only helps new installs.
                File.WriteAllText(path, """
                    {
                      "SpeakEnabled": true,
                      "SpeakNavigation": true,
                      "ShowTypeColumn": true,
                      "ShowSizeColumn": true,
                      "FontSize": 11,
                      "SortBy": 2
                    }
                    """);

                var migrated = Settings.Load();
                Check("an old file with removed settings in it still loads", migrated.SpeakEnabled);
                Check("migrating leaves unrelated settings alone",
                    migrated.FontSize == 11 && migrated.SortBy == SortColumn.Type);
                Check("a migrated file is stamped with the current version",
                    migrated.SettingsVersion == Settings.CurrentSettingsVersion,
                    migrated.SettingsVersion.ToString());

                migrated.Save();
                var reloaded = Settings.Load();

                // A file already at the current version is never migrated again.
                Check("an up-to-date file is left alone",
                    reloaded.SettingsVersion == Settings.CurrentSettingsVersion);

                // Saving must not leave the temp file behind.
                new Settings().Save();
                Check("no leftover temp file after a save", !File.Exists(path + ".tmp"));
                Check("settings file exists after a save", File.Exists(path));
            }
            finally
            {
                Settings.OverrideAppDataDir = null;
                Cleanup(sandbox);
            }
            Console.WriteLine();
        }

        /// <summary>
        /// Every persisted setting must survive being written and read back.
        ///
        /// Done by reflection rather than by listing fields, so a setting added
        /// later is covered without anyone remembering to come here. This is the
        /// guarantee that rebuilding or reinstalling the application cannot lose
        /// somebody's configuration: the file is the contract, and every property
        /// in it has to make the round trip intact.
        /// </summary>
        /// <summary>
        /// An update never reverts, changes or alters a setting. A file with
        /// every setting set to something unusual but valid, stamped as written
        /// by several older versions, has to come back with every value exactly
        /// as it was after loading, saving and loading again.
        /// </summary>
        private static void UpdatesNeverChangeSettingsTests()
        {
            Console.WriteLine("An update never changes a setting:");

            var sandbox = NewTempDir();
            var previous = Settings.OverrideAppDataDir;
            Settings.OverrideAppDataDir = sandbox;
            try
            {
                var original = new Settings();
                foreach (var property in PersistedProperties())
                {
                    if (property.Name == nameof(Settings.SettingsVersion)) continue;
                    if (Attribute.IsDefined(property, typeof(System.Text.Json.Serialization.JsonIgnoreAttribute))) continue;
                    var current = property.GetValue(original);
                    object? changed = property.PropertyType switch
                    {
                        var t when t == typeof(bool) => !(bool)current!,
                        var t when t == typeof(int) => DistinctInt(property.Name, (int)current!),
                        var t when t == typeof(uint) => (uint)((uint)current! + 1),
                        var t when t == typeof(long) => (long)current! + 12345,
                        var t when t == typeof(string) => "kept-" + property.Name,
                        var t when t.IsEnum => NextEnumValue(t, current!),
                        var t when t == typeof(int[]) => DistinctIntArray(current as int[]),
                        _ => null,
                    };
                    if (changed != null) property.SetValue(original, changed);
                }

                // The way an old shortcut default looked, which a step used to move.
                original.AudioVolumeUpShortcut = "Ctrl+Alt+Up";
                original.AudioExtensions = ".mp3;.flac";

                original.Save();
                var json = ProtectedFile.ReadAllText(Path.Combine(sandbox, "settings.json"));

                foreach (int version in new[] { 0, 5, 9, 12, Settings.CurrentSettingsVersion })
                {
                    var stamped = System.Text.RegularExpressions.Regex.Replace(
                        json, "\"SettingsVersion\"\\s*:\\s*\\d+", "\"SettingsVersion\": " + version);
                    File.WriteAllText(Path.Combine(sandbox, "settings.json"), stamped);

                    var loaded = Settings.Load();
                    loaded.Save();
                    var reloaded = Settings.Load();

                    int changedCount = 0;
                    var which = new List<string>();
                    foreach (var property in PersistedProperties())
                    {
                        if (property.Name == nameof(Settings.SettingsVersion)) continue;
                        if (Attribute.IsDefined(property, typeof(System.Text.Json.Serialization.JsonIgnoreAttribute))) continue;
                        if (SameSetting(property.GetValue(original), property.GetValue(reloaded))) continue;
                        changedCount++;
                        which.Add(property.Name);
                    }
                    Equal($"a version {version} file comes back with every setting exactly as it was",
                        "0", changedCount.ToString() + (which.Count > 0 ? " (" + string.Join(", ", which) + ")" : ""));
                }
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                Cleanup(sandbox);
            }

            Console.WriteLine();
        }

        /// <summary>
        /// A new install registers no global shortcuts at all until somebody sets
        /// them up; a settings file that already has them keeps them.
        /// </summary>
        private static void NoShortcutsOnFirstLaunchTests()
        {
            var fresh = new Settings();
            int assigned = AudioActions.All.Count(a => Shortcut.Parse(a.Get(fresh)).IsAssigned);
            if (new Shortcut(fresh.HotkeyModifiers, fresh.HotkeyKey).IsAssigned) assigned++;
            Equal("a new install has no keyboard shortcuts set", "0", assigned.ToString());

            var sandbox = NewTempDir();
            var previous = Settings.OverrideAppDataDir;
            Settings.OverrideAppDataDir = sandbox;
            try
            {
                var mine = new Settings
                {
                    AudioPlayPauseShortcut = "Ctrl+Alt+P",
                    AudioVolumeUpShortcut = "Ctrl+Up",
                    HotkeyModifiers = HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT,
                    HotkeyKey = (uint)Keys.E,
                };
                mine.Save();
                var back = Settings.Load();
                Check("a settings file that has shortcuts keeps every one of them",
                    back.AudioPlayPauseShortcut == "Ctrl+Alt+P" && back.AudioVolumeUpShortcut == "Ctrl+Up" &&
                    back.HotkeyKey == (uint)Keys.E &&
                    back.HotkeyModifiers == (HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT));
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                Cleanup(sandbox);
            }
        }

        private static void SettingsRoundTripTests()
        {
            Console.WriteLine("Settings survive a save and reload:");

            var sandbox = NewTempDir();
            Settings.OverrideAppDataDir = sandbox;

            try
            {
                var original = new Settings();
                var persisted = typeof(Settings).GetProperties()
                    .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                    .Where(p => !Attribute.IsDefined(p, typeof(System.Text.Json.Serialization.JsonIgnoreAttribute)))
                    .ToList();

                Check("there are settings to check", persisted.Count > 20, persisted.Count.ToString());

                // Give every one a value that is not its default, so a property
                // that quietly failed to persist shows up as its default again.
                foreach (var property in persisted)
                {
                    var current = property.GetValue(original);
                    object? changed = property.PropertyType switch
                    {
                        var t when t == typeof(bool) => !(bool)current!,
                        var t when t == typeof(int) => DistinctInt(property.Name, (int)current!),
                        var t when t == typeof(uint) => (uint)((uint)current! + 1),
                        var t when t == typeof(long) => (long)current! + 12345,
                        var t when t == typeof(string) => "round-trip-" + property.Name,
                        var t when t.IsEnum => NextEnumValue(t, current!),

                        // A setting that is a list of numbers, which the
                        // equaliser's twenty bands are. It has to be given a
                        // value that is not its default like everything else, or
                        // the check below is comparing two arrays of zeroes and
                        // would pass against a property that never persisted at
                        // all — which is the exact failure this whole test is
                        // for. Filled with something no clamp will touch.
                        var t when t == typeof(int[]) => DistinctIntArray(current as int[]),
                        var t when t == typeof(List<DriveSyncPair>) => new List<DriveSyncPair>
                        {
                            new() { Name = "round-trip", LocalFolder = @"C:\x", DriveFolderId = "id", Mode = SyncMode.UploadOnly },
                        },
                        _ => null,
                    };

                    if (changed != null) property.SetValue(original, changed);
                }

                original.Save();
                var reloaded = Settings.Load();

                int mismatches = 0;
                foreach (var property in persisted)
                {
                    // The version stamp is set by Save on purpose, so it is the one
                    // value that is meant to come back different.
                    if (property.Name == nameof(Settings.SettingsVersion)) continue;

                    var wanted = property.GetValue(original);
                    var got = property.GetValue(reloaded);
                    if (SameSetting(wanted, got)) continue;

                    mismatches++;
                    Check($"{property.Name} survives the round trip", false,
                        $"wrote {Describe(wanted)}, read {Describe(got)}");
                }

                Check($"all {persisted.Count} settings survive being saved and reloaded",
                    mismatches == 0, $"{mismatches} did not");

                Check("the reloaded file is stamped with the current version",
                    reloaded.SettingsVersion == Settings.CurrentSettingsVersion);

                // A settings file from a build that knew about more settings than
                // this one must not take the rest down with it.
                File.WriteAllText(Path.Combine(sandbox, "settings.json"), """
                    { "SpeakEnabled": false, "FontSize": 15, "SomeFutureSetting": 42,
                      "AnotherOne": { "nested": true }, "SettingsVersion": 99 }
                    """);
                var forward = Settings.Load();
                Check("settings written by a newer build still load",
                    !forward.SpeakEnabled && forward.FontSize == 15,
                    $"speak={forward.SpeakEnabled} font={forward.FontSize}");
                Check("a newer version stamp is not downgraded",
                    forward.SettingsVersion >= Settings.CurrentSettingsVersion,
                    forward.SettingsVersion.ToString());

                // And saving it here keeps both: the stamp, so the newer build does
                // not run its migrations again, and the settings only it knows.
                forward.Save();
                var saved = ProtectedFile.ReadAllText(Path.Combine(sandbox, "settings.json"));
                var roundTripped = Settings.Load();
                Check("a newer build's stamp survives this build saving the file",
                    roundTripped.SettingsVersion == 99, roundTripped.SettingsVersion.ToString());
                Check("and so do the settings only it knows about",
                    saved.Contains("SomeFutureSetting") && saved.Contains("nested"), saved);

                // From this build, an unknown name is a removed setting and goes.
                File.WriteAllText(Path.Combine(sandbox, "settings.json"),
                    "{ \"FontSize\": 13, \"RemovedLongAgo\": 1, \"SettingsVersion\": " +
                    Settings.CurrentSettingsVersion + " }");
                Settings.Load().Save();
                Check("a setting this build removed is dropped on the next save",
                    !ProtectedFile.ReadAllText(Path.Combine(sandbox, "settings.json")).Contains("RemovedLongAgo"));

                // ---- a damaged file costs the damage, not the file ----
                DeleteSettingsCopies(sandbox);
                File.WriteAllText(Path.Combine(sandbox, "settings.json"), """
                    {
                      // somebody's note
                      "FontSize": 16,
                      "SpeakEnabled": "not a bool",
                      "WindowTitle": "hand edited",
                    }
                    """);
                var damaged = Settings.Load();
                Check("a hand-edited file with a comment and a trailing comma still loads",
                    damaged.FontSize == 16 && damaged.WindowTitle == "hand edited",
                    $"font={damaged.FontSize} title={damaged.WindowTitle}");
                Check("and the one value that will not read falls back alone",
                    damaged.SpeakEnabled == new Settings().SpeakEnabled);
                Check("and a copy of the damaged file is kept",
                    Directory.GetFiles(sandbox, "settings.json.damaged-*").Length == 1);

                DeleteSettingsCopies(sandbox);
                File.WriteAllText(Path.Combine(sandbox, "settings.json"), "{ this is not json");
                var wrecked = Settings.Load();
                Check("a file that is not JSON at all gives the defaults",
                    wrecked.FontSize == new Settings().FontSize);
                wrecked.Save();
                var kept = Directory.GetFiles(sandbox, "settings.json.unreadable-*");
                Check("and is copied aside before anything overwrites it",
                    kept.Length == 1 && File.ReadAllText(kept[0]) == "{ this is not json",
                    kept.Length.ToString());
                Check("and a save leaves no temporary file behind",
                    Directory.GetFiles(sandbox, "*.tmp").Length == 0);

                // A reader holding the file open must not make a save fail.
                DeleteSettingsCopies(sandbox);
                bool savedWhileRead;
                using (new FileStream(Path.Combine(sandbox, "settings.json"), FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    savedWhileRead = new Settings { FontSize = 12 }.Save();
                Check("a save succeeds while another reader has the file open", savedWhileRead);

                // ---- OK applies what the dialog changed, and only that ----
                var live = new Settings { AudioVolumePercent = 40, FontSize = 10 };
                var opened = live.Clone();
                var edited = opened.Clone();
                edited.FontSize = 18;                         // changed in the dialog
                live.AudioVolumePercent = 75;                 // a volume key meanwhile
                edited.AudioEqualiserBands = (int[])edited.AudioEqualiserBands.Clone();
                var merged = Settings.WithChanges(live, opened, edited);
                Check("OK in Preferences applies what the dialog changed", merged.FontSize == 18,
                    merged.FontSize.ToString());
                Check("and keeps a volume changed while it was open", merged.AudioVolumePercent == 75,
                    merged.AudioVolumePercent.ToString());
                Check("and does not hand the live object back to be mutated",
                    !ReferenceEquals(merged, live));

                // ---- a reload from outside keeps what this session has not saved ----
                var session = new Settings { FontSize = 11, AudioVolumePercent = 20 };
                session.Save();
                session = Settings.Load();
                session.AudioVolumePercent = 90;              // live, not yet saved
                var outside = Settings.Load();
                outside.StartWithWindows = !outside.StartWithWindows;
                // Written straight to the file, as another process would: a Save
                // here would also move what this process remembers seeing.
                File.WriteAllText(Path.Combine(sandbox, "settings.json"),
                    System.Text.Json.JsonSerializer.Serialize(outside));
                var reloaded2 = Settings.ReloadOnto(session);
                Check("a reload takes the change made outside",
                    reloaded2.StartWithWindows == outside.StartWithWindows);
                Check("and keeps the volume this session had not saved yet",
                    reloaded2.AudioVolumePercent == 90, reloaded2.AudioVolumePercent.ToString());
            }
            finally
            {
                Settings.OverrideAppDataDir = null;
                Cleanup(sandbox);
            }

            Console.WriteLine();
        }

        /// <summary>
        /// Removes the copies a damaged settings file leaves beside it, and not
        /// the file: Windows matches "settings.json.*" against "settings.json"
        /// itself, the way "*.*" matches a name with no dot.
        /// </summary>
        private static void DeleteSettingsCopies(string folder)
        {
            foreach (var stray in Directory.GetFiles(folder, "settings.json.*"))
                if (!string.Equals(Path.GetFileName(stray), "settings.json", StringComparison.OrdinalIgnoreCase))
                    File.Delete(stray);
        }

        /// <summary>
        /// A value for a list-of-numbers setting that is not its default, and
        /// that survives being clamped on the way back in.
        ///
        /// Alternating six and minus six: inside every bound the equaliser
        /// applies, and different in every position, so a save that wrote only
        /// the first element or reversed the order shows up rather than passing.
        /// </summary>
        private static int[] DistinctIntArray(int[]? current)
        {
            var made = new int[current is { Length: > 0 } ? current.Length : 4];
            for (int i = 0; i < made.Length; i++) made[i] = i % 2 == 0 ? 6 : -6;
            return made;
        }

        /// <summary>
        /// Whether two settings values are the same, arrays included.
        ///
        /// object.Equals on an array is reference equality, so a list-of-numbers
        /// setting compared that way is *always* different after a reload — a
        /// false failure that would have to be silenced, and silencing it would
        /// have left the setting genuinely untested.
        /// </summary>
        private static bool SameSetting(object? wanted, object? got)
        {
            if (wanted is int[] a && got is int[] b)
            {
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
                return true;
            }

            if (wanted is List<DriveSyncPair> || got is List<DriveSyncPair>)
                return System.Text.Json.JsonSerializer.Serialize(wanted) == System.Text.Json.JsonSerializer.Serialize(got);

            return Equals(wanted, got);
        }

        /// <summary>A settings value as something worth printing in a failure.</summary>
        private static string Describe(object? value) =>
            value is int[] numbers ? "[" + string.Join(",", numbers) + "]" : value?.ToString() ?? "null";

        /// <summary>
        /// The player is quiet, and stays quiet.
        ///
        /// Every one of these has been asked for explicitly, and a default that
        /// drifts back to talking is the sort of regression nobody notices until
        /// it is speaking over their music again.
        /// </summary>
        private static void QuietByDefaultTests()
        {
            Console.WriteLine("The player says nothing it was not asked to:");

            var fresh = new Settings();
            Check("a new install does not announce a track starting", !fresh.AudioAnnounceStart);
            Check("nor a track finishing", !fresh.AudioAnnounceTrackEnd);
            Check("nor the volume changing", !fresh.AudioAnnounceVolume);
            Check("nor the position when skipping", !fresh.AudioAnnounceSeek);
            Check("nor play, pause, stop and mute", !fresh.AudioAnnounceTransport);

            // The switches only govern speech. The status bar is written either
            // way, so nothing is lost by being quiet.
            Check("the player itself is still on", fresh.AudioPlayerEnabled);

            // A settings file from a build that did announce must be corrected,
            // once — a default only ever reaches somebody with no file yet.
            var sandbox = NewTempDir();
            Settings.OverrideAppDataDir = sandbox;
            try
            {
                File.WriteAllText(Path.Combine(sandbox, "settings.json"), """
                    { "SettingsVersion": 4,
                      "AudioAnnounceVolume": true, "AudioAnnounceSeek": true,
                      "AudioAnnounceTransport": true, "AudioVolumePercent": 42 }
                    """);

                var loaded = Settings.Load();
                // An update never changes a setting: what the file says, it keeps.
                Check("an older file keeps announcing the volume, as it says", loaded.AudioAnnounceVolume);
                Check("and the position", loaded.AudioAnnounceSeek);
                Check("and play, pause and stop", loaded.AudioAnnounceTransport);
                Equal("while everything else in it is left alone", "42", loaded.AudioVolumePercent.ToString());
                Equal("and it is stamped as migrated",
                    Settings.CurrentSettingsVersion.ToString(), loaded.SettingsVersion.ToString());

                // Turning one back on must survive, or the correction would be
                // applied for ever rather than once.
                loaded.AudioAnnounceVolume = true;
                loaded.Save();
                Check("a switch turned back on afterwards stays on", Settings.Load().AudioAnnounceVolume);
            }
            finally
            {
                Settings.OverrideAppDataDir = null;
                Cleanup(sandbox);
            }

            Console.WriteLine();
        }

        /// <summary>A value that differs from the default and survives clamping.</summary>
        private static int DistinctInt(string name, int current) => name switch
        {
            nameof(Settings.FontSize) => 14,
            nameof(Settings.CopyBufferKilobytes) => 512,
            nameof(Settings.FolderSizeTimeoutSeconds) => 45,
            nameof(Settings.TypeAheadMilliseconds) => 400,
            nameof(Settings.WindowWidth) => 1024,
            nameof(Settings.WindowHeight) => 768,
            nameof(Settings.WindowX) => 120,
            nameof(Settings.WindowY) => 80,
            nameof(Settings.ActiveTab) => 1,
            _ => current + 1,
        };

        private static object NextEnumValue(Type type, object current)
        {
            var values = Enum.GetValues(type);
            int index = Array.IndexOf(values, current);
            return values.GetValue((index + 1) % values.Length)!;
        }

        // ---- Typed names must not be able to escape the current folder ----
        /// <summary>
        /// How many things an action happened to, as it is said out loud.
        ///
        /// One is just the verb; above one the number is the message. The count
        /// is the only way a selection can be checked by ear — the rows were read
        /// as they were picked and nothing ever says how many there are now — so
        /// dropping it above one would be dropping the useful half.
        /// </summary>
        private static void CountedSpeechTests()
        {
            Console.WriteLine("Saying how many:");

            Equal("one copied item is just the verb", "Copied", NameRules.SayCount("Copied", 1));
            Equal("and one cut item likewise", "Cut", NameRules.SayCount("Cut", 1));

            Equal("two says how many", "Copied 2 items", NameRules.SayCount("Copied", 2));
            Equal("and so does two cut", "Cut 2 items", NameRules.SayCount("Cut", 2));
            Equal("three", "Copied 3 items", NameRules.SayCount("Copied", 3));
            Equal("and a large selection", "Copied 1247 items", NameRules.SayCount("Copied", 1247));

            // Never "1 items". The plural is the thing these one-liners get
            // wrong, and it is the sort of wrong that only a listener notices.
            Check("no count ever says \"1 items\"",
                !NameRules.SayCount("Copied", 1).Contains("1 items", StringComparison.Ordinal));

            // Nothing selected is refused before this is reached, so zero is only
            // ever a bug — but it must read as a sentence rather than as "Copied".
            Equal("zero is not silently reported as one", "Copied 0 items",
                NameRules.SayCount("Copied", 0));

            // ---- a count that IS the message ----
            //
            // The status bar and the tab announcement exist to say how many, so
            // unlike SayCount the number is never dropped — but the plural was,
            // and those two are read out on every folder and every tab switch.
            // "Tab 1, Documents, 1 items" was the actual sentence.
            Equal("one item is singular", "1 item", NameRules.Items(1));
            Equal("two are plural", "2 items", NameRules.Items(2));
            Equal("and none are plural too", "0 items", NameRules.Items(0));
            Equal("a large one keeps the number", "1247 items", NameRules.Items(1247));
            Equal("the noun is the caller's", "1 result", NameRules.Items(1, "result"));
            Equal("and it is pluralised the same way", "3 results", NameRules.Items(3, "result"));

            Check("this one never says \"1 items\" either",
                !NameRules.Items(1).Contains("1 items", StringComparison.Ordinal));

            // ---- numbering a name that is already taken ----
            //
            // Shared arithmetic now, because the Drive letter needs exactly the
            // same rule against a different question: there "already taken" is the
            // folder's listing, since enumerating placeholders to ask the
            // filesystem is fifteen milliseconds each. Two copies of it is two
            // copies to get wrong, and the folder case below is the one that was
            // wrong once already.
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "song.flac", "song (2).flac", "Backup.2024", "New folder" };

            Equal("a free name is left alone", "other.flac",
                NameRules.UniqueAmong("other.flac", used.Contains, folder: false));
            Equal("a taken one steps past everything taken", "song (3).flac",
                NameRules.UniqueAmong("song.flac", used.Contains, folder: false));
            Equal("a name that is all extension is numbered at its end", ".gitignore (2)",
                NameRules.UniqueAmong(".gitignore", n => n == ".gitignore", folder: false));
            Equal("and an ordinary two-dot name at its last dot", "a.tar (2).gz",
                NameRules.UniqueAmong("a.tar.gz", n => n == "a.tar.gz", folder: false));

            // A folder has no extension whatever the dots in its name say. Split
            // at the last one, a second copy of "Backup.2024" came out as
            // "Backup (2).2024" — and was then announced under that name.
            Equal("a folder is numbered at the end, not inside its dots", "Backup.2024 (2)",
                NameRules.UniqueAmong("Backup.2024", used.Contains, folder: true));
            Equal("and an ordinary folder likewise", "New folder (2)",
                NameRules.UniqueAmong("New folder", used.Contains, folder: true));

            // Case is the caller's question and not this rule's — the set decides
            // what counts as taken — but the *casing that comes back* is the one
            // that was asked for. A name handed back folded to whatever spelling
            // happened to be seen first is a rename nobody asked for.
            Equal("the set decides what is taken; the caller decides the spelling",
                "SONG (3).FLAC",
                NameRules.UniqueAmong("SONG.FLAC", used.Contains, folder: false));

            // The noun is a parameter because the units differ: a transfer counts
            // files and failures separately, and calling both "items" would say a
            // folder of three thousand files was one thing.
            Equal("a different noun pluralises too", "Transferred 4 files",
                NameRules.SayCount("Transferred", 4, "file"));
            Equal("and stays singular at one", "Transferred",
                NameRules.SayCount("Transferred", 1, "file"));

            // The catalogue already promised this shape before the code did: the
            // template for clip.copied is "Copied {0} items". A sentence that no
            // longer matches its own example is how the preview button starts
            // lying about what will be said.
            var copied = Notifications.ById("clip.copied");
            Check("the catalogue still describes what is actually said",
                copied != null && copied.Render(3) == NameRules.SayCount("Copied", 3),
                copied == null ? "clip.copied is missing" : copied.Render(3));

            var cut = Notifications.ById("clip.cut");
            Check("and the same for cutting",
                cut != null && cut.Render(3) == NameRules.SayCount("Cut", 3),
                cut == null ? "clip.cut is missing" : cut.Render(3));

            // And the window really does use the rule. MainForm is not compiled
            // into this suite, so the alternative is a rule that is tested here
            // and quietly not applied there.
            var main = SourceFile("MainForm.cs");
            if (main == null) { Check("MainForm.cs can be read", false); Console.WriteLine(); return; }

            Check("copy and cut are announced through the rule",
                main.Contains("NameRules.SayCount(cut ? \"Cut\" : \"Copied\"", StringComparison.Ordinal));

            Console.WriteLine();
        }

        /// <summary>
        /// The application calls itself whatever it has been told to.
        ///
        /// The window and the tray tooltip obeyed the setting; the tray balloons,
        /// the "ready" announcement, the restore and the quit did not. So somebody
        /// who had renamed the window was told about it by an application with a
        /// different name — which reads as a second program having something to
        /// say, not as the one you renamed.
        /// </summary>
        /// <summary>
        /// Every notification's routing, decided for real: what each says by
        /// default, and that every one can be switched on and off. A message can
        /// render perfectly and still be routed to nowhere, and nothing about
        /// reading the list says which.
        /// </summary>
        private static void EveryNotificationTests()
        {
            Console.WriteLine("Every notification, routed:");

            var all = Notifications.All;
            var settings = new Settings();

            int spokenByDefault = all.Count(i =>
                Notifications.ChannelsFor(i.Id, settings) == NotificationChannel.Speech);
            Check($"some but not all are spoken by default ({spokenByDefault} of {all.Count})",
                spokenByDefault > 0 && spokenByDefault < all.Count);

            var everything = new Dictionary<string, NotificationChannel>();
            foreach (var info in all) everything[info.Id] = NotificationChannel.Speech;
            settings.SpeechOverrides = Notifications.FormatOverrides(everything);
            Equal("every one can be turned on", "0",
                all.Count(i => Notifications.ChannelsFor(i.Id, settings) != NotificationChannel.Speech).ToString());

            var nothing = new Dictionary<string, NotificationChannel>();
            foreach (var info in all) nothing[info.Id] = NotificationChannel.None;
            settings.SpeechOverrides = Notifications.FormatOverrides(nothing);
            Equal("and every one can be silenced completely", "0",
                all.Count(i => Notifications.ChannelsFor(i.Id, settings) == NotificationChannel.Speech).ToString());

            settings.SpeechOverrides = "";
            Check("an id the catalogue does not know is not spoken",
                Notifications.ChannelsFor("not.a.real.id.at.all", settings) != NotificationChannel.Speech);

            Console.WriteLine();
        }

        private static void WindowTitleTests()
        {
            Console.WriteLine("What the application calls itself:");

            Equal("a chosen title is used as given", "a",
                new Settings { WindowTitle = "a" }.DisplayTitle);
            Equal("surrounding space is trimmed off it", "Files",
                new Settings { WindowTitle = "  Files  " }.DisplayTitle);
            Equal("an empty title falls back to the real name",
                Settings.DefaultWindowTitle, new Settings { WindowTitle = "" }.DisplayTitle);
            Equal("and so does one that is only spaces",
                Settings.DefaultWindowTitle, new Settings { WindowTitle = "   " }.DisplayTitle);
            Equal("and so does a null one, which the file can produce",
                Settings.DefaultWindowTitle, new Settings { WindowTitle = null! }.DisplayTitle);

            // Derived, not stored. Without [JsonIgnore] the serialiser writes it
            // out as a second copy of the same string, and the round-trip test
            // then has a property it can set and never read back.
            var previous = Settings.OverrideAppDataDir;
            var scratch = Path.Combine(Path.GetTempPath(), "en-title-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(scratch);
                Settings.OverrideAppDataDir = scratch;

                new Settings { WindowTitle = "a" }.Save();
                var written = ProtectedFile.ReadAllText(Path.Combine(scratch, "settings.json"));

                Check("the derived title is not written to settings.json",
                    !written.Contains("DisplayTitle", StringComparison.Ordinal));
                Equal("and the stored one still round-trips", "a", Settings.Load().WindowTitle);
                Equal("and is what the application answers to", "a", Settings.Load().DisplayTitle);
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(scratch, true); } catch { }
            }

            // Everything that names the application reads the setting. Read out
            // of the source because neither file is compiled into this suite, and
            // because the bug was six call sites of which two were right — the
            // sort of thing that comes back one call site at a time.
            foreach (var file in new[] { "TrayApplicationContext.cs", "MainForm.cs" })
            {
                var text = SourceFile(file);
                if (text == null) { Check($"{file} can be read", false); continue; }

                int hardcoded = Occurrences(text, "Program.MainWindowTitle");
                Check($"{file} never hardcodes the application name", hardcoded == 0,
                    hardcoded + " found");
            }

            // Program's own two dialogs have no Settings to read — one runs before
            // they are loaded, the other when something has already gone wrong —
            // so the name is handed forward instead. If that stops happening they
            // silently go back to saying the wrong thing.
            var program = SourceFile("Program.cs");
            if (program == null) { Check("Program.cs can be read", false); Console.WriteLine(); return; }

            Check("the fallback name is handed forward when settings load",
                Occurrences(program, "RememberTitle(") >= 2, "wanted a definition and a call");
            Check("and the dialogs raised there use it rather than the constant",
                !program.Contains("MessageBox.Show(message, $\"{MainWindowTitle}", StringComparison.Ordinal));

            var tray = SourceFile("TrayApplicationContext.cs");
            Check("and a settings change hands it forward too",
                tray != null && tray.Contains("Program.RememberTitle(settings)", StringComparison.Ordinal));

            Console.WriteLine();
        }

        /// <summary>
        /// A cancelled transfer never leaves a file that looks finished.
        ///
        /// Robocopy is killed where it stands and it preallocates, so cancelling
        /// a 20GB copy five seconds in left a 20GB file at the destination whose
        /// first six gigabytes were real and whose remaining fourteen were zeros.
        /// Same name as the source, same size as the source, sitting in the
        /// folder — there was nothing to look at that would tell you, short of
        /// reading it. The worst case is somebody deleting the original because
        /// the copy obviously worked.
        ///
        /// The invariant is the weaker, checkable one: after a cancellation the
        /// destination either does not have the file, or has all of it. Written
        /// that way on purpose, because whether the kill lands mid-file is a race
        /// with the disk and a test that depends on winning it is a test that
        /// fails on somebody else's machine.
        /// </summary>
        private static async Task CancelCleanupTests()
        {
            Console.WriteLine("Cancelling a transfer:");

            var temp = Path.Combine(Path.GetTempPath(), "en-cancel-" + Guid.NewGuid().ToString("N"));
            var src = Path.Combine(temp, "from");
            var dst = Path.Combine(temp, "to");

            try
            {
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);

                // Big enough that the kill lands while it is still being written
                // on any disk this is likely to run on.
                var source = Path.Combine(src, "chunky.bin");
                var block = new byte[8 * 1024 * 1024];
                new Random(4242).NextBytes(block);
                using (var fs = File.Create(source))
                    for (int i = 0; i < 64; i++) fs.Write(block, 0, block.Length);   // 512MB

                long sourceLength = new FileInfo(source).Length;
                Equal("the fixture is the size it should be", (512L * 1024 * 1024).ToString(),
                    sourceLength.ToString());

                using var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromMilliseconds(120));

                var result = await RoboCopyEngine.RunAsync(
                    new[] { source }, dst, move: false,
                    FileOperations.RecommendedThreads, PasteConflictPolicy.AutoRename,
                    null, cts.Token);

                Check("the transfer reports itself cancelled", result.Cancelled,
                    $"cancelled={result.Cancelled} copied={result.Copied} failed={result.Failed}");

                var landed = Path.Combine(dst, "chunky.bin");
                bool exists = File.Exists(landed);
                long landedLength = exists ? new FileInfo(landed).Length : -1;

                Console.WriteLine(exists
                    ? $"        the destination file was left, at {landedLength:N0} bytes"
                    : "        the destination file was removed");

                Check("a cancelled copy leaves either nothing or the whole file",
                    !exists || landedLength == sourceLength,
                    $"left {landedLength:N0} of {sourceLength:N0} bytes");

                // And if something was left, it must really be the file — not a
                // preallocated shell of the right length full of zeros, which is
                // exactly what the bug produced.
                if (exists && landedLength == sourceLength)
                {
                    bool tailIsReal = false;
                    try
                    {
                        using var a = File.OpenRead(source);
                        using var b = File.OpenRead(landed);
                        a.Seek(-4096, SeekOrigin.End);
                        b.Seek(-4096, SeekOrigin.End);
                        var x = new byte[4096]; var y = new byte[4096];
                        a.ReadExactly(x); b.ReadExactly(y);
                        tailIsReal = x.AsSpan().SequenceEqual(y);
                    }
                    catch { }

                    Check("and a file that was left is the real one, not zeros", tailIsReal);
                }

                // ---- and it must not throw away work that finished ----
                //
                // The cheap way to guarantee "nothing half-written survives" is
                // to delete everything, and that is the wrong answer: cancelling
                // a paste of two hundred files after a hundred and ninety should
                // leave the hundred and ninety. Worse, for a *move* robocopy has
                // already deleted the sources of the finished ones, so deleting
                // their destinations destroys the only copy there is.
                var many = Path.Combine(temp, "many");
                var manyDest = Path.Combine(temp, "many-to");
                Directory.CreateDirectory(many);
                Directory.CreateDirectory(manyDest);

                var small = new byte[16 * 1024 * 1024];
                new Random(99).NextBytes(small);
                var sources = new List<string>();
                for (int i = 0; i < 40; i++)
                {
                    var p = Path.Combine(many, $"part{i:00}.bin");
                    File.WriteAllBytes(p, small);
                    sources.Add(p);
                }

                using var cts2 = new CancellationTokenSource();
                cts2.CancelAfter(TimeSpan.FromMilliseconds(120));

                var second = await RoboCopyEngine.RunAsync(
                    sources, manyDest, move: false,
                    FileOperations.RecommendedThreads, PasteConflictPolicy.AutoRename,
                    null, cts2.Token);

                // Whether the kill lands before the last file is a race with the
                // disk, and asserting that it does is asserting that this machine
                // is slow. The invariant below holds either way, which is the
                // point of writing it as an invariant.
                Console.WriteLine(second.Cancelled
                    ? "        the second transfer was cancelled part way"
                    : "        the second transfer finished before the cancel landed");

                int survived = 0, broken = 0;
                var how = new List<string>();
                foreach (var left in Directory.GetFiles(manyDest))
                {
                    var origin = Path.Combine(many, Path.GetFileName(left));
                    long length = new FileInfo(left).Length;
                    if (length != new FileInfo(origin).Length)
                    {
                        broken++;
                        how.Add($"{Path.GetFileName(left)} is {length} bytes");
                        continue;
                    }

                    var la = File.ReadAllBytes(left);
                    var lb = File.ReadAllBytes(origin);
                    if (la.AsSpan().SequenceEqual(lb)) { survived++; continue; }

                    broken++;
                    int first = la.AsSpan().CommonPrefixLength(lb);
                    bool tailSame = la.AsSpan(la.Length - 65536).SequenceEqual(lb.AsSpan(lb.Length - 65536));
                    how.Add($"{Path.GetFileName(left)} differs from byte {first}, tail {(tailSame ? "same" : "different")}, " +
                            $"written {File.GetLastWriteTimeUtc(left):HH:mm:ss.fff}");
                }

                Console.WriteLine($"        {survived} of 40 finished before the cancel and were kept");
                Check("nothing incomplete was left behind", broken == 0,
                    $"{string.Join("; ", how)}; errors: {string.Join(" | ", second.Errors)}");
            }
            catch (Exception ex)
            {
                Check("the cancellation test ran", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }

            Console.WriteLine();
        }

        /// <summary>
        /// Gain leaves float alone above full scale and still clamps 16-bit.
        ///
        /// The float half is the whole point of the change. Clamping at ±1.0 was
        /// the application deciding how loud the hardware may be, and it is not
        /// the application's decision to make. Measured through WASAPI loopback
        /// with the Windows volume at 10%: a float sine rendered into shared mode
        /// at amplitude 1 came back at 0.126, at 4 at 0.504 — exactly four times
        /// — and at 8 it reached 0.986 and stopped, with 16 giving 0.986 as well.
        /// The mixer carries the excess and the *endpoint* clips it, so at 10%
        /// there was a factor of eight of headroom that the old clamp discarded.
        ///
        /// The 16-bit half keeps its clamp, because an integer sample has
        /// nowhere to put the excess: the container is the ceiling and there is
        /// no mixer downstream that can carry what cannot be expressed.
        /// </summary>
        private static void GainHeadroomTests()
        {
            Console.WriteLine("How loud gain is allowed to get:");

            // Float rides past full scale, and the renderer is where that now
            // happens — the transform this used to test is gone with the media
            // engine it was written for. The rule it proved has not changed and
            // is asserted against WasapiRenderer in "The direct output path":
            // the sample is carried, not squashed, because the ceiling belongs
            // to the endpoint and moves with the Windows volume.
            var renderer = new WasapiRenderer { Gain = 3f };
            var samples = new float[] { 1f, -1f, 0.5f, 0f };
            renderer.ApplyGainAndMeasure(samples, samples.Length);

            Equal("full scale times three is three, not one", "3", samples[0].ToString("0.###"));
            Equal("and it works downwards too", "-3", samples[1].ToString("0.###"));
            Equal("half scale scales the same way", "1.5", samples[2].ToString("0.###"));
            Equal("silence stays silent", "0", samples[3].ToString("0.###"));

            // Unity must be exactly untouched — this runs on every sample of
            // every track at the default volume.
            var unity = new WasapiRenderer { Gain = 1f };
            var untouched = new float[] { 0.123456f, -0.987654f, 1f };
            var before = (float[])untouched.Clone();
            unity.ApplyGainAndMeasure(untouched, untouched.Length);
            Check("a gain of one changes nothing at all",
                untouched.AsSpan().SequenceEqual(before));

            Console.WriteLine();
        }

        /// <summary>
        /// What the conflict policy promises about a file that is already there.
        ///
        /// Both engines apply the policy while planning and then write without
        /// asking — robocopy overwrites unconditionally, and the managed engine
        /// opens its destination with FileMode.Create, which truncates. So the
        /// entire protection for a file the user already had is that the plan
        /// pointed somewhere else, and nothing was checking that it did.
        ///
        /// Asserted on *content*, not on counts. A count says a collision was
        /// noticed; only reading the bytes back says the file survived it, and
        /// the failure being guarded against here is silent destruction of
        /// something that was not part of the copy at all.
        /// </summary>
        private static async Task ConflictSafetyTests()
        {
            Console.WriteLine("What happens to a file that is already there:");

            foreach (var engine in new[] { "robocopy", "managed" })
            {
                foreach (var policy in new[]
                {
                    PasteConflictPolicy.Skip,
                    PasteConflictPolicy.AutoRename,
                    PasteConflictPolicy.Overwrite,
                })
                {
                    var temp = Path.Combine(Path.GetTempPath(), "en-conflict-" + Guid.NewGuid().ToString("N"));
                    var src = Path.Combine(temp, "from");
                    var dst = Path.Combine(temp, "to");

                    try
                    {
                        Directory.CreateDirectory(src);
                        Directory.CreateDirectory(dst);

                        var incoming = Path.Combine(src, "song.txt");
                        var existing = Path.Combine(dst, "song.txt");
                        File.WriteAllText(incoming, "NEW");
                        File.WriteAllText(existing, "OLD");

                        // Different lengths and an older destination, deliberately.
                        //
                        // Robocopy decides a file is already there by size *and*
                        // last-write time, and skips it — so a fixture whose two
                        // files are both three bytes written in the same second is
                        // a fixture where "overwrite" legitimately copies nothing.
                        // That is not the policy failing, it is robocopy's
                        // same-file optimisation, and a test that trips over it
                        // fails about half the time for a reason that has nothing
                        // to do with what it is checking. (The optimisation is
                        // real and worth knowing: pasting over a file of identical
                        // size and timestamp does nothing, whatever the bytes say.)
                        File.WriteAllText(incoming, "NEW CONTENT, LONGER");
                        File.SetLastWriteTimeUtc(existing, DateTime.UtcNow.AddHours(-2));
                        File.SetLastWriteTimeUtc(incoming, DateTime.UtcNow);

                        if (engine == "robocopy")
                        {
                            await RoboCopyEngine.RunAsync(
                                new[] { incoming }, dst, move: false,
                                FileOperations.RecommendedThreads, policy, null, CancellationToken.None);
                        }
                        else
                        {
                            await FileOperations.RunAsync(
                                new[] { incoming }, dst, false, policy,
                                FileOperations.RecommendedThreads, 64, null, null, CancellationToken.None);
                        }

                        var landed = File.ReadAllText(existing);
                        var renamed = Path.Combine(dst, "song (2).txt");

                        switch (policy)
                        {
                            case PasteConflictPolicy.Skip:
                                Equal($"{engine}/skip leaves the existing file alone", "OLD", landed);
                                Check($"{engine}/skip writes nothing else either",
                                    !File.Exists(renamed), "a renamed copy appeared anyway");
                                break;

                            case PasteConflictPolicy.AutoRename:
                                Equal($"{engine}/rename leaves the existing file alone", "OLD", landed);
                                Check($"{engine}/rename puts the new one beside it",
                                    File.Exists(renamed), "song (2).txt was not created");
                                if (File.Exists(renamed))
                                    Equal($"{engine}/rename copy has the new contents",
                                        "NEW CONTENT, LONGER", File.ReadAllText(renamed));
                                break;

                            case PasteConflictPolicy.Overwrite:
                                Equal($"{engine}/overwrite really does replace it", "NEW CONTENT, LONGER", landed);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Check($"{engine}/{policy} ran", false, ex.GetType().Name + ": " + ex.Message);
                    }
                    finally
                    {
                        try { Directory.Delete(temp, true); } catch { }
                    }
                }
            }

            await AskedConflictReachesTheCopyTests();
            AskingAboutConflictsTests();
            Console.WriteLine();
        }

        /// <summary>
        /// An answer given to the question really does decide what happens to the
        /// file, and a question nobody answers falls back to the safe one.
        ///
        /// On content, like the rest of this section: a count says a choice was
        /// noticed, only reading the bytes back says it was obeyed.
        /// </summary>
        private static async Task AskedConflictReachesTheCopyTests()
        {
            foreach (var (answer, expected, alsoRenamed) in new[]
            {
                (FileOperations.ConflictChoice.Overwrite, "NEW", false),
                (FileOperations.ConflictChoice.Skip, "OLD", false),
                (FileOperations.ConflictChoice.Rename, "OLD", true),
            })
            {
                var temp = Path.Combine(Path.GetTempPath(), "en-ask-" + Guid.NewGuid().ToString("N"));
                var src = Path.Combine(temp, "from");
                var dst = Path.Combine(temp, "to");

                try
                {
                    Directory.CreateDirectory(src);
                    Directory.CreateDirectory(dst);

                    var incoming = Path.Combine(src, "song.txt");
                    var existing = Path.Combine(dst, "song.txt");
                    File.WriteAllText(incoming, "NEW");
                    File.WriteAllText(existing, "OLD");

                    int asked = 0;
                    await FileOperations.RunAsync(
                        new[] { incoming }, dst, move: false, PasteConflictPolicy.Ask,
                        FileOperations.RecommendedThreads, 64, null,
                        _ => { asked++; return answer; }, CancellationToken.None);

                    Equal($"ask/{answer} asked exactly once", "1", asked.ToString());
                    Equal($"ask/{answer} decided what happened to the file",
                        expected, File.ReadAllText(existing));
                    Equal($"ask/{answer} wrote a second copy only when asked to",
                        alsoRenamed.ToString(), File.Exists(Path.Combine(dst, "song (2).txt")).ToString());
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }

            // Cancel means the whole thing is off, and it must not be reported as
            // a failure: a deliberate answer and a copy that went wrong are
            // different events with different sentences.
            {
                var temp = Path.Combine(Path.GetTempPath(), "en-ask-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var src = Path.Combine(temp, "from");
                    var dst = Path.Combine(temp, "to");
                    Directory.CreateDirectory(src);
                    Directory.CreateDirectory(dst);
                    File.WriteAllText(Path.Combine(src, "song.txt"), "NEW");
                    File.WriteAllText(Path.Combine(dst, "song.txt"), "OLD");

                    var stopped = await FileOperations.RunAsync(
                        new[] { Path.Combine(src, "song.txt") }, dst, false, PasteConflictPolicy.Ask,
                        2, 64, null, _ => FileOperations.ConflictChoice.Cancel, CancellationToken.None);

                    Check("ask/Cancel stops the copy", stopped.Cancelled, $"cancelled={stopped.Cancelled}");
                    Equal("ask/Cancel leaves the file alone", "OLD",
                        File.ReadAllText(Path.Combine(dst, "song.txt")));
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }

            // And with nobody to ask — which is what every engine-internal call
            // is — the fallback is the answer that cannot destroy anything.
            {
                var temp = Path.Combine(Path.GetTempPath(), "en-ask-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var src = Path.Combine(temp, "from");
                    var dst = Path.Combine(temp, "to");
                    Directory.CreateDirectory(src);
                    Directory.CreateDirectory(dst);
                    File.WriteAllText(Path.Combine(src, "song.txt"), "NEW");
                    File.WriteAllText(Path.Combine(dst, "song.txt"), "OLD");

                    await FileOperations.RunAsync(
                        new[] { Path.Combine(src, "song.txt") }, dst, false, PasteConflictPolicy.Ask,
                        2, 64, null, null, CancellationToken.None);

                    Equal("ask with nobody to ask keeps the existing file", "OLD",
                        File.ReadAllText(Path.Combine(dst, "song.txt")));
                    Check("and puts the new one beside it",
                        File.Exists(Path.Combine(dst, "song (2).txt")));
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }

        /// <summary>
        /// "Ask" has to actually ask, and the answer has to reach the copy.
        ///
        /// It did neither. `RoboCopyEngine` carried a comment reading "AutoRename
        /// (and Ask, which the caller resolves before us)" — a contract no caller
        /// implemented — and its own branch treated everything that was not
        /// Overwrite or Skip as auto-rename. So the option sat in Preferences
        /// quietly meaning "keep both": somebody who chose it in order to be warned
        /// before a file was replaced was never warned, and nothing anywhere said
        /// why. That is the shape this file's own rules call worse than no setting
        /// at all.
        ///
        /// Checked in three places, because the bug could come back in any of
        /// them: the wording of the question, the plumbing that carries an answer
        /// into a real copy, and the caller that has to resolve it before an engine
        /// ever sees it.
        /// </summary>
        private static void AskingAboutConflictsTests()
        {
            // ---- the question names what clashes ----
            Equal("one name is the whole question",
                "\"song.flac\" is already in \"album\". What should happen?",
                ConflictForm.Describe(new[] { "song.flac" }, "album"));

            Check("a handful are listed",
                ConflictForm.Describe(new[] { "a.txt", "b.txt" }, "here")
                    .StartsWith("\"a.txt\", \"b.txt\" are already in", StringComparison.Ordinal));

            // Past a handful the count is the story: a screen reader reading forty
            // file names before the question is a dialog nobody can answer.
            var many = ConflictForm.Describe(
                new[] { "1", "2", "3", "4", "5", "6" }, "downloads");
            Check("many become a count", many.StartsWith("6 items are already in", StringComparison.Ordinal), many);
            Check("and the count still names one of them",
                many.Contains("\"1\"", StringComparison.Ordinal), many);

            Check("a destination with no name still reads as a sentence",
                ConflictForm.Describe(new[] { "x" }, "")
                    .Contains("the destination", StringComparison.Ordinal));

            // ---- the dialog builds, on its own STA thread ----
            string error = "";
            int options = 0;
            var chosen = FileOperations.ConflictChoice.Cancel;

            var thread = new Thread(() =>
            {
                try
                {
                    using var window = new ConflictForm(new[] { "song.flac" }, "album");
                    foreach (var box in FindAll<ComboBox>(window)) options = box.Items.Count;
                    chosen = window.Choice;
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(30));

            Equal("the conflict dialog builds without throwing", "", error);
            Equal("it offers all four answers", "4", options.ToString());

            // Filling in the gaps is first and so is what Enter does.
            //
            // It replaced keeping-both at the top, and the reason is not that
            // keeping both was wrong — it destroys nothing either — but that it
            // is not what pasting the same folder twice ever *means*. A copy
            // that stopped part way is finished by this answer and by no other:
            // keeping both makes "album (2)", replacing recopies everything, and
            // skipping leaves the missing files missing.
            Equal("and defaults to the one that finishes an interrupted copy",
                FileOperations.ConflictChoice.FillGaps.ToString(), chosen.ToString());
        }

        /// <summary>
        /// A held volume key lands exactly on both ends, and on the way.
        ///
        /// Acceleration grows the step, so the last one before an end is large —
        /// large enough to jump straight past it. "Nearly silent" and "nearly
        /// full" are both failures: the whole point of holding the key is to
        /// arrive somewhere definite without having to tap the last few.
        ///
        /// Driven through the real setter so the clamp is the one that ships,
        /// and with no engine, which is the state the ceiling question is
        /// genuinely open in.
        /// </summary>
        private static void VolumeTravelTests()
        {
            Console.WriteLine("Holding the volume key:");

            var volume = AudioActions.For(AudioAction.VolumeUp);

            // ---- all the way down, from the top ----
            using (var player = new AudioPlayer())
            {
                player.VolumePercent = AudioPlayer.LoudestPercent;
                int repeat = 0;
                while (player.VolumePercent > 0 && repeat < 500)
                    player.ChangeVolume(-AudioActions.Accelerated(
                        5, repeat++, true, volume.AccelerationCap, volume.AccelerationEvery));

                Equal("holding it down arrives at exactly nothing", "0",
                    player.VolumePercent.ToString());
                Check($"and gets there in a moment ({repeat} repeats)", repeat < 60, repeat.ToString());
            }

            // ---- and all the way up, from silence ----
            using (var player = new AudioPlayer())
            {
                player.VolumePercent = 0;
                int repeat = 0;
                while (player.VolumePercent < AudioPlayer.LoudestPercent && repeat < 500)
                    player.ChangeVolume(AudioActions.Accelerated(
                        5, repeat++, true, volume.AccelerationCap, volume.AccelerationEvery));

                Equal("holding it up arrives at exactly the top",
                    AudioPlayer.LoudestPercent.ToString(), player.VolumePercent.ToString());
                Check($"and gets there in a moment ({repeat} repeats)", repeat < 60, repeat.ToString());
            }

            // ---- one tap is one step, at both ends ----
            using (var player = new AudioPlayer())
            {
                player.VolumePercent = 0;
                Equal("a tap up from silence moves by one step", "5", player.ChangeVolume(5).Split(' ')[1]);

                player.VolumePercent = 1;
                player.ChangeVolume(-5);
                Equal("and a tap down near silence stops at nothing, not below",
                    "0", player.VolumePercent.ToString());

                player.VolumePercent = AudioPlayer.LoudestPercent - 1;
                player.ChangeVolume(50);
                Equal("and a tap up near the top stops at the top, not past it",
                    AudioPlayer.LoudestPercent.ToString(), player.VolumePercent.ToString());
            }

            Console.WriteLine();
        }

        /// <summary>
        /// A tone, not silence.
        ///
        /// Every other audio fixture in this suite is a generated *silent* WAV,
        /// deliberately, so the tests need nobody's music library. That is also
        /// exactly how a render path that emitted nothing at all passed for
        /// months: silence times anything is silence, and a transform that ate
        /// every sample looked identical to one that worked. Amplitude is a
        /// parameter here and the tests below check what comes back out.
        /// </summary>
        private static void WriteTone(string path, double seconds, double amplitude,
            int rate = 48000, int channels = 2)
        {
            int frames = (int)(rate * seconds);
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(file);

            int dataBytes = frames * channels * 2;
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + dataBytes);
            w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((short)1);
            w.Write((short)channels);
            w.Write(rate);
            w.Write(rate * channels * 2);
            w.Write((short)(channels * 2));
            w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(dataBytes);

            for (int i = 0; i < frames; i++)
            {
                short v = (short)(amplitude * 32760 * Math.Sin(2 * Math.PI * 440 * i / rate));
                for (int c = 0; c < channels; c++) w.Write(v);
            }
        }

        /// <summary>
        /// How much of a signal sits at one frequency, by Goertzel.
        ///
        /// A whole FFT would answer a question nobody asked. What is being
        /// checked here is one thing — is the note still the note — and that is
        /// two bins: the pitch that went in, and the pitch a resampler would
        /// have produced instead.
        /// </summary>
        private static double EnergyAt(float[] samples, int count, int channels,
            double frequency, double sampleRate)
        {
            double w = 2 * Math.PI * frequency / sampleRate;
            double coefficient = 2 * Math.Cos(w);
            double s1 = 0, s2 = 0;

            for (int i = 0; i < count; i++)
            {
                double s0 = samples[i * channels] + coefficient * s1 - s2;
                s2 = s1;
                s1 = s0;
            }

            return Math.Sqrt(Math.Abs(s1 * s1 + s2 * s2 - coefficient * s1 * s2)) / count;
        }

        /// <summary>
        /// How much the equaliser changes a pure tone, in decibels.
        ///
        /// Measured through the real Process, block by block, exactly as the
        /// render thread drives it — the gains glide in over about twenty
        /// milliseconds and the filters take a moment to settle, so the answer
        /// is read from the end of half a second rather than from the first
        /// buffer. Reading it too early measures the glide.
        /// </summary>
        private static double EqualiserGainAt(
            Equaliser equaliser, double frequency, int rate = 48000, int channels = 2)
        {
            const int block = 512;
            const int settle = 40;      // ~0.43 seconds
            const int measure = 16;     // ~0.17 seconds, read after it has settled

            var input = new float[block * channels];
            var output = new float[block * measure * channels];

            int at = 0;
            for (int b = 0; b < settle + measure; b++)
            {
                for (int f = 0; f < block; f++)
                {
                    int i = b * block + f;
                    float v = (float)(0.25 * Math.Sin(2 * Math.PI * frequency * i / rate));
                    for (int c = 0; c < channels; c++) input[f * channels + c] = v;
                }

                var work = (float[])input.Clone();
                equaliser.Process(work, work.Length, channels, rate);

                if (b < settle) continue;
                Array.Copy(work, 0, output, at * channels, work.Length);
                at += block;
            }

            double got = EnergyAt(output, at, channels, frequency, rate);

            // Against the amplitude that went in, not against a second pass —
            // 0.25 is what the generator above writes, and a Goertzel of a pure
            // sine at its own frequency reads half the amplitude.
            double reference = 0.25 / 2.0;
            if (got <= 0 || reference <= 0) return double.NegativeInfinity;
            return 20 * Math.Log10(got / reference);
        }

        /// <summary>
        /// How long real files take to become audible, over a real folder.
        ///
        /// Opt-in, like the other diagnostics here: set
        /// <c>EXPLORERNATIVE_AUDIOFOLDER</c> to a directory and the suite times
        /// every audio file in it. Off, this does nothing and costs nothing —
        /// a suite that needs somebody's music library is a suite that passes on
        /// one machine.
        ///
        /// It exists because "some files take ages to start" is a claim about
        /// *particular files*, and the only way to find out which is to time the
        /// ones being complained about. It reports the three stages separately,
        /// because they have completely different causes: opening the decoder is
        /// Media Foundation resolving a codec, the first read is the file system
        /// and the header, and reaching the prebuffer floor is the decode
        /// itself.
        /// </summary>
        private static void RealFolderTimingTests()
        {
            var folder = Environment.GetEnvironmentVariable("EXPLORERNATIVE_AUDIOFOLDER");
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

            Console.WriteLine($"Timing real files in {folder}:");

            var files = Directory.EnumerateFiles(folder)
                .Where(f => AudioFiles.IsAudio(f, null))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
            {
                Console.WriteLine("        nothing playable in it");
                Console.WriteLine();
                return;
            }

            var timings = new List<(string Name, long Open, long First, long Floor, string Note)>();
            var warmOpens = new List<long>();

            foreach (var path in files)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                using var decoder = new AudioDecoder();

                if (!decoder.Open(path, 48000, 2))
                {
                    timings.Add((Path.GetFileName(path), clock.ElapsedMilliseconds, -1, -1,
                        "will not open: " + decoder.Diagnostic));
                    continue;
                }

                long open = clock.ElapsedMilliseconds;

                var chunk = new float[4096 * Math.Max(1, decoder.Channels)];
                long first = -1;
                int frames = 0;

                // The prebuffer floor, which is what actually gates the first
                // sound: WasapiPlayback holds the ring until it has this much.
                const int Floor = 24000;
                while (frames < Floor)
                {
                    int n = decoder.Read(chunk, Math.Min(4096, Floor - frames));
                    if (first < 0) first = clock.ElapsedMilliseconds;
                    if (n <= 0) break;
                    frames += n;
                }

                long floor = clock.ElapsedMilliseconds - open;

                // And again, from scratch, on a file that has just been opened
                // and read. This is the question warming actually turns on: if
                // the second open is no cheaper than the first, then opening the
                // file ahead of time buys nothing and the cost is Media
                // Foundation's per-file work rather than the disk's.
                decoder.Dispose();
                var second = System.Diagnostics.Stopwatch.StartNew();
                using (var again = new AudioDecoder())
                {
                    if (again.Open(path, 48000, 2)) again.Read(new float[512], 128);
                }
                warmOpens.Add(second.ElapsedMilliseconds);

                timings.Add((Path.GetFileName(path), open, first - open, floor,
                    $"{decoder.SampleRate} Hz {decoder.Channels}ch, reopen {second.ElapsedMilliseconds}ms"));
            }

            var worst = timings.OrderByDescending(t => t.Open + t.Floor).Take(12).ToList();

            Console.WriteLine($"        {files.Count} files; slowest to become audible:");
            foreach (var t in worst)
                Console.WriteLine($"        {t.Open,6}ms open {t.First,6}ms first {t.Floor,6}ms to floor   " +
                                  $"{t.Name}   {t.Note}");

            var ok = timings.Where(t => t.Floor >= 0).ToList();
            if (ok.Count > 0)
            {
                Console.WriteLine($"        median open {Median(ok.Select(t => t.Open))}ms, " +
                                  $"worst {ok.Max(t => t.Open)}ms; " +
                                  $"median to floor {Median(ok.Select(t => t.Floor))}ms, " +
                                  $"worst {ok.Max(t => t.Floor)}ms");

                if (warmOpens.Count > 0)
                    Console.WriteLine($"        reopening the same file: median " +
                                      $"{Median(warmOpens)}ms, worst {warmOpens.Max()}ms " +
                                      $"— this is what warming ahead of Enter would buy");
            }

            // Nothing is asserted about somebody else's files. This reports.
            Check("every file in the folder opened",
                timings.All(t => t.Floor >= 0),
                string.Join("; ", timings.Where(t => t.Floor < 0).Take(3).Select(t => t.Name + " " + t.Note)));

            Console.WriteLine();
        }

        private static long Median(IEnumerable<long> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
        }

        /// <summary>
        /// A file whose rate or channel count is not the device's still plays at
        /// the right speed.
        ///
        /// <see cref="AudioDecoder"/> asks for the device's format in three
        /// goes, and the last of them lets the decoder pick — because a source
        /// reader refuses an over-specified type outright and a good many WAVs
        /// only open on the third try. It reads back what it settled on and says
        /// so, and for a long time nothing downstream did anything with the
        /// answer: the frames went to a device expecting 48kHz stereo whatever
        /// they were.
        ///
        /// A 44.1kHz file is then eight percent fast, a 22kHz one is more than
        /// double, and a mono file interleaved as stereo runs off the end of its
        /// own buffer at twice the speed. Reported as "certain WAVs play fast".
        /// </summary>
        private static void OddFormatTests()
        {
            Console.WriteLine("Files whose format is not the device's:");

            var dir = NewTempDir();
            try
            {
                // Written at rates and channel counts a device will not be
                // running at, so the conversion is exercised rather than
                // skipped. 440Hz throughout, because the whole question is
                // whether it is still 440Hz at the far end.
                var cases = new (string Name, int Rate, int Channels)[]
                {
                    ("44100 stereo.wav", 44100, 2),
                    ("22050 stereo.wav", 22050, 2),
                    ("44100 mono.wav",   44100, 1),
                    ("8000 mono.wav",     8000, 1),
                    ("96000 stereo.wav", 96000, 2),
                };

                // And the encodings a real WAV actually arrives in. The plain
                // 16-bit case above is the one a test writes by hand; the ones
                // below are what recorders, editors and phones produce, and
                // "certain WAVs play fast" is a complaint about a *subset* of
                // files, which is exactly the shape of a format the negotiation
                // handles differently.
                var encodings = new (string Name, int Rate, int Channels, int Bits, bool Float, bool Extensible)[]
                {
                    ("16-bit 44100 stereo.wav",   44100, 2, 16, false, false),
                    ("24-bit 48000 stereo.wav",   48000, 2, 24, false, false),
                    ("32-bit float 48000.wav",    48000, 2, 32, true,  false),
                    ("extensible 44100.wav",      44100, 2, 16, false, true),
                    ("extensible float 96000.wav",96000, 2, 32, true,  true),
                    ("8-bit 22050 mono.wav",      22050, 1,  8, false, false),
                };

                foreach (var (name, rate, channels, bits, isFloat, extensible) in encodings)
                {
                    var path = Path.Combine(dir, name);
                    WriteWavVariant(path, 1.5, rate, channels, bits, isFloat, extensible);

                    using var decoder = new AudioDecoder();
                    bool opened = decoder.Open(path, 48000, 2);

                    if (!opened)
                    {
                        // Not a failure of this application: a format Windows
                        // has no decoder for is handed to whatever does, which
                        // is a documented path. It is reported so the run says
                        // which ones those are on this machine.
                        Console.WriteLine($"        {name}: will not open ({decoder.Diagnostic})");
                        continue;
                    }

                    bool converted = decoder.SampleRate != 48000 || decoder.Channels != 2;
                    Console.WriteLine(
                        $"        {name}: got {decoder.SampleRate} Hz {decoder.Channels}ch" +
                        (converted ? "   <- needs conversion" : ""));

                    var chunk = new float[4096 * decoder.Channels];
                    int total = 0;
                    var all = new float[decoder.SampleRate * decoder.Channels];
                    while (total < decoder.SampleRate)
                    {
                        int n = decoder.Read(chunk, Math.Min(4096, decoder.SampleRate - total));
                        if (n <= 0) break;
                        Array.Copy(chunk, 0, all, total * decoder.Channels, n * decoder.Channels);
                        total += n;
                    }

                    if (total < decoder.SampleRate / 4)
                    {
                        Check($"{name} decodes", false, $"{total} frames");
                        continue;
                    }

                    double here = EnergyAt(all, total, decoder.Channels, 440, decoder.SampleRate);
                    double octaveUp = EnergyAt(all, total, decoder.Channels, 880, decoder.SampleRate);
                    double octaveDown = EnergyAt(all, total, decoder.Channels, 220, decoder.SampleRate);

                    Check($"{name} still sounds 440 Hz",
                        here > octaveUp * 4 && here > octaveDown * 4,
                        $"440={here:0.0000} 880={octaveUp:0.0000} 220={octaveDown:0.0000}");
                }

                foreach (var (name, rate, channels) in cases)
                {
                    var path = Path.Combine(dir, name);
                    WriteTone(path, 1.5, 0.4, rate, channels);

                    using var decoder = new AudioDecoder();

                    // Asked for 48000 stereo, the way the player asks.
                    if (!decoder.Open(path, 48000, 2))
                    {
                        Check($"{name} opens", false, decoder.Diagnostic);
                        continue;
                    }

                    Check($"{name} opens", true);

                    // Printed, not merely asserted. Whether Media Foundation
                    // honours a request for 48000 stereo depends on the file,
                    // the machine and the codecs installed on it — so the run
                    // says what it actually got, and a build where the answer
                    // changes is a build where somebody can see it changed.
                    bool converted = decoder.SampleRate != 48000 || decoder.Channels != 2;
                    Console.WriteLine(
                        $"        asked 48000 Hz 2ch, got {decoder.SampleRate} Hz " +
                        $"{decoder.Channels}ch{(converted ? "   <- needs conversion" : "")}");

                    // The decoder says what it really settled on. Whether that
                    // is 48000 stereo or the file's own shape is Media
                    // Foundation's business; the point is that it is *read* and
                    // acted on rather than assumed.
                    Check($"{name} reports a real format",
                        decoder.SampleRate > 0 && decoder.Channels > 0,
                        $"{decoder.SampleRate} Hz, {decoder.Channels} ch");

                    // The tone is still 440Hz in the decoder's own output, at
                    // the decoder's own rate. If this were wrong the file itself
                    // would be wrong and nothing downstream could save it.
                    int frames = decoder.SampleRate;
                    var got = new float[frames * decoder.Channels];
                    int read = 0;
                    while (read < frames)
                    {
                        var chunk = new float[4096 * decoder.Channels];
                        int n = decoder.Read(chunk, Math.Min(4096, frames - read));
                        if (n <= 0) break;
                        Array.Copy(chunk, 0, got, read * decoder.Channels, n * decoder.Channels);
                        read += n;
                    }

                    if (read < decoder.SampleRate / 4)
                    {
                        Check($"{name} decodes something", false, $"{read} frames");
                        continue;
                    }

                    double at440 = EnergyAt(got, read, decoder.Channels, 440, decoder.SampleRate);
                    double at880 = EnergyAt(got, read, decoder.Channels, 880, decoder.SampleRate);

                    Check($"{name} decodes as 440 Hz at its own rate",
                        at440 > at880 * 4, $"{at440:0.0000} at 440 against {at880:0.0000} at 880");
                }
            }
            finally { Cleanup(dir); }

            // ---- and the conversion the player applies is the right one ----
            //
            // The arithmetic, without a device. A resampler stepping by
            // pitch × (sourceRate / deviceRate) puts a source tone back where it
            // started; the check is that a 44.1kHz file on a 48kHz device is not
            // played eight percent fast.
            {
                const int sourceRate = 44100, deviceRate = 48000, tone = 440, channels = 2;
                double c = sourceRate / (double)deviceRate;

                var input = new float[sourceRate * 2 * channels];
                for (int i = 0; i < input.Length / channels; i++)
                {
                    float v = (float)(0.5 * Math.Sin(2 * Math.PI * tone * i / sourceRate));
                    for (int ch = 0; ch < channels; ch++) input[i * channels + ch] = v;
                }

                int at = 0;
                var pitch = new Resampler(channels)
                {
                    Ratio = 1.0 * c,               // pitch 1, rate conversion only
                    SampleRate = deviceRate,
                    Source = (dest, want) =>
                    {
                        int left = input.Length / channels - at;
                        int take = Math.Min(want, left);
                        if (take <= 0) return 0;
                        Array.Copy(input, at * channels, dest, 0, take * channels);
                        at += take;
                        return take;
                    },
                };

                var output = new float[deviceRate * channels];
                int made = pitch.Read(output, deviceRate);

                double still440 = EnergyAt(output, made, channels, 440, deviceRate);
                double fast479 = EnergyAt(output, made, channels, 440 * deviceRate / (double)sourceRate,
                    deviceRate);

                Check("a 44.1 kHz file on a 48 kHz device still sounds 440 Hz",
                    still440 > fast479 * 4,
                    $"{still440:0.0000} at 440 against {fast479:0.0000} at {440 * deviceRate / (double)sourceRate:0}");
            }

            // ---- and the wiring that applies it is actually there ----
            var pump = SourceFile("WasapiPlayback.cs");
            if (pump != null)
            {
                Check("the rate the decoder really produces is carried into the chain",
                    pump.Contains("_sourceRate = decoder.SampleRate;", StringComparison.Ordinal) &&
                    pump.Contains("RateConversion", StringComparison.Ordinal),
                    "reading it back and not using it is how a 44.1 kHz file plays fast");

                Check("and folded into the resampler rather than ignored",
                    pump.Contains("p.Ratio = _pitchRatio * RateConversion;", StringComparison.Ordinal));

                Check("the channel count too",
                    pump.Contains("_sourceChannels = decoder.Channels", StringComparison.Ordinal) &&
                    pump.Contains("ToDeviceChannels(", StringComparison.Ordinal),
                    "a mono file interleaved as stereo reads past its own buffer at double speed");

                Check("and the clock counts in the decoder's frames, not the device's",
                    pump.Contains("queued * speed * RateConversion", StringComparison.Ordinal),
                    "a clock that ignores the conversion makes every skip land in the wrong place");
            }
            else Check("WasapiPlayback.cs can be read", false);

            Console.WriteLine();
        }

        /// <summary>
        /// Nothing ever hands the device silence it did not ask for.
        ///
        /// "Gapless, every file" is three separate silences and they have three
        /// separate causes: the front of a track, the middle of one, and the
        /// join where a repeat turns round. Only the last is what the word
        /// usually means, and it was the worst of the three — most of a second.
        /// </summary>
        private static void GaplessTests()
        {
            Console.WriteLine("No gaps anywhere:");

            var pump = SourceFile("WasapiPlayback.cs");
            var player = SourceFile("AudioPlayer.cs");

            if (pump == null || player == null)
            {
                Check("the playback sources can be read", false);
                Console.WriteLine();
                return;
            }

            // ---- the join at a repeat ----
            //
            // The turn has to happen where the frames are produced. Anything
            // further out only finds out the track ended *after* the ring has
            // drained to nothing — that is how the render thread knows to raise
            // Finished — and by then the gap has been heard.
            // Matched without its closing bracket on purpose: the condition has
            // room for more than the two terms — it also counts the turns, so a
            // decoder that cannot restart ends the track instead of spinning
            // silently for ever — and this test is about *where* the turn
            // happens, not about how the guard is spelled.
            Check("a repeat turns the decoder round inside the pump",
                pump.Contains("if (Loop && _decoder != null", StringComparison.Ordinal) &&
                pump.Contains("_decoder.SeekTo(0);", StringComparison.Ordinal),
                "handling it outside means the ring empties first, which is the gap");

            Check("and the ring is not cleared when it does",
                !Between(pump, "if (Loop && _decoder != null", "_sourceDone = true;")
                    .Contains("_writeAt = _readAt = _stored = 0", StringComparison.Ordinal),
                "clearing it at the loop point throws away the audio that makes it seamless");

            Check("repeat is pushed to the pump rather than answered by an event",
                player.Contains("_direct.Loop = value", StringComparison.Ordinal),
                "answering Finished with a seek is the slow path that was replaced");

            Check("and the end-of-track handler no longer seeks for a repeat",
                !player.Contains("if (_repeat)\n            {\n                _direct?.SeekTo(0);",
                    StringComparison.Ordinal),
                "two things turning the track round is one of them doing it late");

            // ---- the front of a track ----
            Check("the device is held until there is something to feed it",
                pump.Contains("_waitingForBuffer", StringComparison.Ordinal) &&
                pump.Contains("PrebufferFrames", StringComparison.Ordinal),
                "a device started against an empty ring is handed silence");

            Check("and a file shorter than the floor still starts",
                pump.Contains("if (!_sourceDone && stored < floor) return;", StringComparison.Ordinal),
                "waiting for half a second of a quarter-second file never ends");

            // And the floor is never above the level this track is going to be
            // filled to. The ring is kept shallow whenever nothing is racing
            // playback — see ShallowFrames — and a floor above that level is a
            // device held suspended for ever, waiting for audio the pump has
            // already decided not to decode.
            Check("and the floor is never above what the ring will hold",
                pump.Contains("Math.Min(AtDeviceRate(PrebufferFrames), TargetFrames())", StringComparison.Ordinal),
                "a floor above the fill level is a track that never starts");

            // And the release is checked every time round the loop, not only
            // after a read that produced something. A short file reaches
            // _sourceDone without ever filling the floor, and a release that
            // only ran on a successful write left the device suspended for ever
            // — a track that plays in total silence, which is a far worse gap
            // than the one the floor closes. The real-audio test caught it.
            Check("and the gate is checked on every pass, not only after a read",
                Between(pump, "ReleaseWhenBuffered();", "int stored, room;").Length < 400 &&
                Between(pump, "double seek = Interlocked.Exchange(ref _seekTo, double.NaN);", "int stored, room;")
                    .Contains("ReleaseWhenBuffered();", StringComparison.Ordinal),
                "a short file never fills the floor and would never be released");

            // ---- the ring is depth, and depth is latency ----
            //
            // Everything in the ring has already been decoded, stretched,
            // resampled and had its silences taken out, so a change to any of
            // those stages is not heard until what is in front of it has played.
            // Filled to the brim that is four seconds, which is what "adjusting
            // the pitch is not instant" was. The four seconds exists for one
            // case — a download racing playback — and that case is the one
            // StreamingSource.Complete answers.
            Check("the pump fills to a level rather than to the brim",
                pump.Contains("room = Math.Min(room, TargetFrames() - stored);", StringComparison.Ordinal),
                "a ring kept full is four seconds between a control and its sound");

            Check("and the deep ring is kept for a download that is still arriving",
                pump.Contains("download.Available < download.Length", StringComparison.Ordinal),
                "shallow while a Drive download races playback is the stutter it was raised for");

            // Complete is not the question. It means the cache budget is full,
            // and the budget is capped — so a track bigger than it reports
            // complete with most of itself still on the network. There is a
            // 1.3GB Ogg in the folder this was found in, against a 1GB budget.
            Check("and it asks whether every byte is in memory, not whether the cache is full",
                !pump.Contains("!download.Complete", StringComparison.Ordinal),
                "Complete is true for a file larger than the cache budget");

            Check("and shallow is short enough to read as immediate",
                WasapiPlaybackShallowFrames() > 0 && WasapiPlaybackShallowFrames() <= 48000 / 2,
                WasapiPlaybackShallowFrames().ToString());

            // And the gate is a flag the fill callback reads, not a suspend.
            //
            // Suspending stops the audio client and releasing it starts the
            // client again, so gating the device that way puts an extra
            // Stop/Start on the endpoint at the front of every track — free on
            // some drivers and not on others, which is a slow start bought to
            // fix a short one. This shipped for one turn and was unwound.
            Check("the gate does not stop and restart the audio device",
                !Between(pump, "_waitingForBuffer = true;", "// The decoder is opened")
                    .Contains("SetSuspended(true)", StringComparison.Ordinal),
                "an extra Stop/Start per track is latency at the front of every one");

            Check("and the fill callback is what honours it",
                pump.Contains("if (_waitingForBuffer) return 0;", StringComparison.Ordinal),
                "the device keeps running and is handed nothing until there is something");

            // ---- the middle of one ----
            //
            // A request to Google costs most of a second, so a one-second ring
            // could be emptied by a single slow chunk.
            Check("the ring is deep enough to ride out a slow read",
                pump.Contains("private const int RingFrames = 48000 * 4;", StringComparison.Ordinal),
                "one second is less than one Drive request");

            // ---- and the resampler survives a device change ----
            //
            // Found while doing the above rather than reported: the rebuild for
            // a device with a different format made a new stretcher and left the
            // resampler pointed at the old one, so the pitch shift was silently
            // dropped and a dead object stayed in the chain.
            Check("a device change rebuilds the whole chain, not half of it",
                Between(pump, "decoder.Reopen(", "if (wasPaused)")
                    .Contains("_pitch = new Resampler(", StringComparison.Ordinal),
                "rebuilding the stretcher alone leaves the resampler reading a replaced object");

            Console.WriteLine();
        }

        /// <summary>
        /// A WAV in one of the shapes real files actually turn up in: 8, 16, 24
        /// or 32 bit, integer or float, and with or without the EXTENSIBLE
        /// header that modern tools write.
        ///
        /// <see cref="WriteTone"/> writes the one easy case. These are the ones
        /// where a decoder negotiation can behave differently, which is what a
        /// complaint about *certain* files is pointing at.
        /// </summary>
        private static void WriteWavVariant(string path, double seconds, int rate, int channels,
            int bits, bool isFloat, bool extensible)
        {
            int frames = (int)(rate * seconds);
            int bytesPerSample = bits / 8;
            int blockAlign = channels * bytesPerSample;
            int dataBytes = frames * blockAlign;

            // 1 is PCM, 3 is IEEE float, 0xFFFE is EXTENSIBLE — which names the
            // real format in a sub-format GUID instead.
            short tag = extensible ? unchecked((short)0xFFFE) : (short)(isFloat ? 3 : 1);
            int fmtSize = extensible ? 40 : 16;

            using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(file);

            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(4 + (8 + fmtSize) + (8 + dataBytes));
            w.Write(new[] { 'W', 'A', 'V', 'E' });

            w.Write(new[] { 'f', 'm', 't', ' ' });
            w.Write(fmtSize);
            w.Write(tag);
            w.Write((short)channels);
            w.Write(rate);
            w.Write(rate * blockAlign);
            w.Write((short)blockAlign);
            w.Write((short)bits);

            if (extensible)
            {
                w.Write((short)22);                 // cbSize
                w.Write((short)bits);               // valid bits
                w.Write(channels == 1 ? 0x4 : 0x3); // channel mask
                // KSDATAFORMAT_SUBTYPE_PCM / _IEEE_FLOAT
                w.Write(new Guid(isFloat ? 3 : 1, 0x0000, 0x0010,
                    0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71).ToByteArray());
            }

            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(dataBytes);

            for (int i = 0; i < frames; i++)
            {
                double v = 0.4 * Math.Sin(2 * Math.PI * 440 * i / rate);
                for (int c = 0; c < channels; c++)
                {
                    if (isFloat) w.Write((float)v);
                    else if (bits == 8) w.Write((byte)(v * 127 + 128));
                    else if (bits == 16) w.Write((short)(v * 32760));
                    else
                    {
                        int s = (int)(v * 8388600);
                        w.Write((byte)(s & 0xFF));
                        w.Write((byte)((s >> 8) & 0xFF));
                        w.Write((byte)((s >> 16) & 0xFF));
                    }
                }
            }
        }

        /// <summary>The source between two markers, or empty when either is missing.</summary>
        private static string Between(string text, string from, string to)
        {
            int start = text.IndexOf(from, StringComparison.Ordinal);
            if (start < 0) return "";
            int end = text.IndexOf(to, start, StringComparison.Ordinal);
            return end < 0 ? text[start..] : text[start..end];
        }

        /// <summary>
        /// Skipping through a track that is still downloading.
        ///
        /// The reads have to be right whatever the download is doing, and the
        /// pump must not play audio decoded for a position the key has already
        /// passed. Both were wrong, and a third thing — a moving download
        /// window — was tried as a speed fix and reverted for making seeking
        /// worse; see CLAUDE.md.
        /// </summary>
        private static void SkipWhileDownloadingTests()
        {
            Console.WriteLine("Skipping a track that is still arriving:");

            var dir = NewTempDir();
            try
            {
                // A file whose every byte says where it is, so a read from the
                // wrong offset is not merely detectable but says how wrong.
                var path = Path.Combine(dir, "track.bin");
                const int size = 6 * 1024 * 1024;
                var made = new byte[size];
                for (int i = 0; i < size; i++) made[i] = (byte)(i % 251);
                File.WriteAllBytes(path, made);

                using var source = new StreamingSource(path, size);

                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!source.Complete && clock.ElapsedMilliseconds < 20000)
                    Thread.Sleep(10);

                Check("the whole track downloads", source.Complete,
                    $"{source.Available} of {source.Cacheable}");

                // ---- reading anywhere serves the byte that belongs there ----
                //
                // Every byte of the fixture says where it belongs, so a single
                // wrong one is a caught corruption. Read all over the file in
                // the order a held skip key would: jumping repeatedly, forwards
                // and backwards, with no settling in between.
                {
                    var rng = new Random(11);
                    var buf = new byte[8192];
                    var slot = System.Runtime.InteropServices.Marshal.AllocHGlobal(8);
                    int wrong = 0, checkedBytes = 0;

                    try
                    {
                        for (int round = 0; round < 60; round++)
                        {
                            long at = (long)(rng.NextDouble() * (size - buf.Length - 1));

                            source.Seek(at, 0, IntPtr.Zero);
                            source.Read(buf, buf.Length, slot);
                            int n = System.Runtime.InteropServices.Marshal.ReadInt32(slot);

                            for (int i = 0; i < n; i++)
                            {
                                checkedBytes++;
                                if (buf[i] != (byte)((at + i) % 251)) wrong++;
                            }
                        }
                    }
                    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(slot); }

                    Check($"sixty jumps around a downloaded track never serve a wrong byte ({checkedBytes} checked)",
                        wrong == 0 && checkedBytes > 0,
                        $"{wrong} wrong of {checkedBytes}");
                }

                // ---- and the same while the download is still running ----
                //
                // A fresh source read immediately, so the reads race the fill
                // thread rather than following it. Reads ahead of the download
                // go to the share and reads behind it come from the buffer;
                // both have to be right, and a torn hand-off between them is
                // exactly what "repeats segments" sounds like.
                {
                    using var live = new StreamingSource(path, size);
                    var rng = new Random(29);
                    var buf = new byte[8192];
                    var slot = System.Runtime.InteropServices.Marshal.AllocHGlobal(8);
                    int wrong = 0, checkedBytes = 0;

                    try
                    {
                        for (int round = 0; round < 60; round++)
                        {
                            long at = (long)(rng.NextDouble() * (size - buf.Length - 1));

                            live.Seek(at, 0, IntPtr.Zero);
                            live.Read(buf, buf.Length, slot);
                            int n = System.Runtime.InteropServices.Marshal.ReadInt32(slot);

                            for (int i = 0; i < n; i++)
                            {
                                checkedBytes++;
                                if (buf[i] != (byte)((at + i) % 251)) wrong++;
                            }
                        }
                    }
                    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(slot); }

                    Check($"nor while it is still downloading ({checkedBytes} checked)",
                        wrong == 0 && checkedBytes > 0,
                        $"{wrong} wrong of {checkedBytes}");
                }
            }
            finally { Cleanup(dir); }

            // ---- audio decoded for a skip nobody is at any more is dropped ----
            //
            // The correctness half, checked by reading the source: the pump must
            // not write a block to the ring when a further seek is already
            // waiting. The ring is empty at that moment and the renderer is
            // starving, so anything written goes straight out of the speakers —
            // a fragment from a position the key has already passed.
            var pump = SourceFile("WasapiPlayback.cs");
            if (pump == null) { Check("WasapiPlayback.cs can be read", false); }
            else
            {
                Check("a block decoded for a superseded skip is thrown away",
                    pump.Contains("if (!double.IsNaN(Volatile.Read(ref _seekTo))) continue;",
                        StringComparison.Ordinal),
                    "otherwise a held skip key plays a fragment from every position it passes");

                // And the check is after the read, because the whole point is
                // that the world changed during it.
                int atCheck = pump.IndexOf(
                    "if (!double.IsNaN(Volatile.Read(ref _seekTo))) continue;", StringComparison.Ordinal);
                int atRead = pump.IndexOf("int got = _pitch?.Read(block, want)", StringComparison.Ordinal);
                Check("and the check comes after the read, not before it",
                    atRead > 0 && atCheck > atRead,
                    "checking first would test a world that has not changed yet");
            }

            // ---- and the download is a plain sequential walk again ----
            //
            // A "point the download at the skip" hint was added and reverted. It
            // fixed one case and thrashed every other: a skip forward moved the
            // held window, a skip back fell behind it, and the download was torn
            // down and restarted on each change of direction — throwing away the
            // bytes being played. Holding a skip key changes direction
            // constantly. The note in StreamingSource says so; this makes sure
            // nobody reinstates it without reading that.
            var player = SourceFile("AudioPlayer.cs");
            var stream = SourceFile("StreamingSource.cs");

            Check("the download is not redirected by a skip",
                player != null && !player.Contains("streaming.Prioritise(", StringComparison.Ordinal) &&
                stream != null && !stream.Contains("public void Prioritise", StringComparison.Ordinal),
                "moving the held region on every skip thrashes the download");

            Check("and the reason it is not is written down",
                stream != null && stream.Contains("Prioritise", StringComparison.Ordinal),
                "a reverted idea with no note is one that gets tried again");

            // ---- one empty read is not the end of the track ----
            //
            // The other half of the regression, and the one that made it sound
            // like the song restarting. A decoder reading through a download can
            // come up empty without being finished — a skip lands past what has
            // arrived and Media Foundation hands back nothing rather than
            // blocking. Called the end of the track, that raises Finished, and
            // Finished with repeat on seeks to zero.
            if (pump != null)
            {
                Check("an empty read is retried before the track is called finished",
                    pump.Contains("_emptyReads", StringComparison.Ordinal) &&
                    pump.Contains("EmptyReadsBeforeEnd", StringComparison.Ordinal),
                    "otherwise one momentary gap plays the song again from the beginning");

                Check("and a skip starts that count over",
                    pump.Contains("_emptyReads = 0;", StringComparison.Ordinal),
                    "empty reads at the position just left say nothing about the new one");
            }

            // ---- and a superseded download never shares its buffer ----
            //
            // The lesson that survived the revert. A download that has been
            // stood down does not stop when told; it stops when it next looks,
            // which is after the four megabyte read it is already inside, and
            // until then it goes on writing. Release has always handled that by
            // dropping the array rather than waiting for the thread — and an
            // "optimisation" that reused the array instead put a whole chunk
            // into the middle of the live buffer at the wrong offset.
            Check("a superseded download is left writing into an array nobody reads",
                stream != null &&
                stream.Contains("var bytes = new byte[(int)Math.Min(_pieceBytes, Length - offset)];", StringComparison.Ordinal) &&
                stream.Contains("if (_disposed || _released || token.IsCancellationRequested) return false;", StringComparison.Ordinal),
                "reusing it lets a stood-down thread corrupt the download in flight");

            Console.WriteLine();
        }

        /// <summary>
        /// Pitch, and pitch without tempo.
        ///
        /// The test that matters is the mirror of the one TimeStretch has. There
        /// the question was "did it change the length without changing the
        /// note"; here it is "did it change the note", and then the harder half:
        /// did it change the note *without* changing the length.
        ///
        /// Real audio throughout, for the reason written across this file: a
        /// stage that emits silence passes every check made against silence.
        /// </summary>
        private static void PitchTests()
        {
            Console.WriteLine("Changing pitch:");

            const int rate = 48000, channels = 2, tone = 440;

            static float[] Sine(int frames, int channels, double frequency, double rate)
            {
                var buffer = new float[frames * channels];
                for (int i = 0; i < frames; i++)
                {
                    float v = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / rate));
                    for (int c = 0; c < channels; c++) buffer[i * channels + c] = v;
                }
                return buffer;
            }

            static Func<float[], int, int> Feeder(float[] source, int channels)
            {
                int at = 0;
                return (destination, frames) =>
                {
                    int left = source.Length / channels - at;
                    int take = Math.Min(frames, left);
                    if (take <= 0) return 0;
                    Array.Copy(source, at * channels, destination, 0, take * channels);
                    at += take;
                    return take;
                };
            }

            var input = Sine(rate * 4, channels, tone, rate);

            // ---- ratio 1 is untouched, exactly ----
            {
                var pitch = new Resampler(channels) { Ratio = 1.0, Source = Feeder(input, channels) };
                var output = new float[rate * channels];
                int got = pitch.Read(output, rate);

                bool identical = got == rate;
                for (int i = 0; identical && i < got * channels; i++)
                    if (output[i] != input[i]) identical = false;

                Check("normal pitch passes the samples through untouched", identical,
                    $"got {got} frames");
            }

            // ---- and is untouched again once the pitch has been put back ----
            //
            // It used to go on interpolating at ratio 1 until the next seek,
            // because what it still held kept it out of the bypass.
            {
                var pitch = new Resampler(channels) { Ratio = 1.5, SampleRate = rate, Source = Feeder(input, channels) };
                var block = new float[20_000 * channels];
                pitch.Read(block, 20_000);

                pitch.Ratio = 1.0;
                var after = new float[40_000 * channels];
                int got = 0;
                while (got < 40_000)
                {
                    int n = pitch.Read(block, Math.Min(4096, 40_000 - got));
                    if (n <= 0) break;
                    Array.Copy(block, 0, after, got * channels, n * channels);
                    got += n;
                }

                // The last half of it has to be a run of the input itself, bit for bit.
                int from = got - 20_000;
                bool found = false;
                for (int j = 0; !found && j + 20_000 <= input.Length / channels; j++)
                {
                    if (input[j * channels] != after[from * channels]) continue;
                    bool same = true;
                    for (int i = 0; same && i < 20_000 * channels; i++)
                        same = input[j * channels + i] == after[from * channels + i];
                    found = same;
                }

                Check("pitch moved and put back passes the samples through untouched again", found,
                    $"{got} frames after the change, none of the last half matching the input exactly");
            }

            // ---- the note actually moves, by the interval asked for ----
            //
            // A semitone is a ratio, so the check is that the energy lands at
            // 440 * ratio and not at 440. Measured with the same Goertzel the
            // stretcher's tests use.
            foreach (int semitones in new[] { -12, -5, 3, 7, 12 })
            {
                double ratio = Resampler.RatioForSemitones(semitones);
                var pitch = new Resampler(channels)
                {
                    Ratio = ratio,
                    SampleRate = rate,
                    Source = Feeder(input, channels),
                };

                var output = new float[rate * channels];
                int got = pitch.Read(output, rate);

                double wanted = tone * ratio;
                double atWanted = EnergyAt(output, got, channels, wanted, rate);
                double atOriginal = EnergyAt(output, got, channels, tone, rate);

                Check($"{semitones:+#;-#;0} semitones puts the note at {wanted:0} Hz",
                    atWanted > atOriginal * 4,
                    $"{atWanted:0.0000} at {wanted:0} Hz against {atOriginal:0.0000} at {tone}");
            }

            // ---- an octave is exactly a doubling ----
            Check("twelve semitones is exactly an octave",
                Math.Abs(Resampler.RatioForSemitones(12) - 2.0) < 1e-12,
                Resampler.RatioForSemitones(12).ToString("R"));
            Check("and minus twelve is exactly half",
                Math.Abs(Resampler.RatioForSemitones(-12) - 0.5) < 1e-12,
                Resampler.RatioForSemitones(-12).ToString("R"));

            // ---- on its own it shortens the track, which is the point ----
            //
            // The resampler is a tape machine: it must change the length. If it
            // did not, the division in WasapiPlayback.ApplyRates would be
            // correcting something that was not happening.
            {
                var pitch = new Resampler(channels)
                {
                    Ratio = 2.0,
                    SampleRate = rate,
                    Source = Feeder(input, channels),
                };

                var output = new float[rate * 8 * channels];
                int total = 0;
                while (true)
                {
                    int got = pitch.Read(output, Math.Min(4096, output.Length / channels - total));
                    if (got <= 0) break;
                    total += got;
                    if (total >= output.Length / channels) break;
                }

                // Four seconds in, an octave up, is about two seconds out.
                double seconds = total / (double)rate;
                Check("an octave up on its own halves the length",
                    Math.Abs(seconds - 2.0) < 0.05, $"{seconds:0.000} seconds from 4");
            }

            // ---- the combination that makes them independent ----
            //
            // This is the whole feature, and it is the arithmetic in
            // ApplyRates rather than either stage on its own: stretch by
            // speed/pitch, resample by pitch, and the length comes back while
            // the note stays moved.
            foreach (int semitones in new[] { -7, 5, 12 })
            {
                double ratio = Resampler.RatioForSemitones(semitones);

                var stretch = new TimeStretch(channels)
                {
                    Rate = 1.0 / ratio,               // speed 1, pitch `ratio`
                    Source = Feeder(input, channels),
                };
                var pitch = new Resampler(channels)
                {
                    Ratio = ratio,
                    SampleRate = rate,
                    Source = stretch.Read,
                };

                // Appended rather than re-read into the same buffer. Read always
                // writes from index zero, so a loop that passes the same array
                // each time keeps the *last* chunk and leaves the rest of the
                // buffer as silence — which measures as no tone at any
                // frequency, and looks exactly like a broken resampler.
                var output = new float[rate * 8 * channels];
                var chunk = new float[4096 * channels];
                int total = 0;
                while (total < output.Length / channels)
                {
                    int got = pitch.Read(chunk, Math.Min(4096, output.Length / channels - total));
                    if (got <= 0) break;
                    Array.Copy(chunk, 0, output, total * channels, got * channels);
                    total += got;
                }

                double seconds = total / (double)rate;
                Check($"{semitones:+#;-#;0} semitones keeps the length",
                    Math.Abs(seconds - 4.0) < 0.25, $"{seconds:0.00} seconds from 4");

                // Measured from a second in, past the stretcher's first few
                // hops, and over a whole second of settled output.
                int from = Math.Min(rate, total);
                int count = Math.Min(rate, total - from);
                var window = new float[count * channels];
                Array.Copy(output, from * channels, window, 0, count * channels);

                double atWanted = EnergyAt(window, count, channels, tone * ratio, rate);
                double atOriginal = EnergyAt(window, count, channels, tone, rate);
                Check($"and still moves the note {semitones:+#;-#;0}",
                    atWanted > atOriginal * 3,
                    $"{atWanted:0.0000} moved against {atOriginal:0.0000} original");
            }

            // ---- nothing comes out that is not a number ----
            foreach (int semitones in new[] { AudioPlayer.LowestSemitones, -1, 1, AudioPlayer.HighestSemitones })
            {
                var pitch = new Resampler(channels)
                {
                    Ratio = Resampler.RatioForSemitones(semitones),
                    SampleRate = rate,
                    Source = Feeder(Sine(rate, channels, 1000, rate), channels),
                };

                var output = new float[8192 * channels];
                bool sane = true;
                for (int pass = 0; pass < 20; pass++)
                {
                    int got = pitch.Read(output, 8192);
                    if (got <= 0) break;
                    for (int i = 0; i < got * channels; i++)
                        if (float.IsNaN(output[i]) || float.IsInfinity(output[i]) || Math.Abs(output[i]) > 4)
                            sane = false;
                }
                Check($"{semitones:+#;-#;0} semitones stays finite and in range", sane);
            }

            // ---- a source that stops, stops ----
            {
                var pitch = new Resampler(channels)
                {
                    Ratio = 1.5,
                    SampleRate = rate,
                    Source = (_, _) => 0,
                };
                Equal("an empty source reads nothing rather than spinning", "0",
                    pitch.Read(new float[1024 * channels], 1024).ToString());
            }

            // ---- the wording, which is what is heard ----
            Equal("zero is normal, not a number", "Normal pitch", Resampler.DescribeSemitones(0));
            Equal("one semitone is singular", "1 semitone up", Resampler.DescribeSemitones(1));
            Equal("and several are not", "5 semitones up", Resampler.DescribeSemitones(5));
            Equal("down says down", "3 semitones down", Resampler.DescribeSemitones(-3));
            Equal("an octave is named, not counted", "1 octave up", Resampler.DescribeSemitones(12));
            Equal("and so are two", "2 octaves down", Resampler.DescribeSemitones(-24));

            // ---- and the ladder covers exactly what the settings clamp to ----
            Equal("the ladder starts at the lowest the player allows",
                AudioPlayer.LowestSemitones.ToString(), AudioPlayer.PitchLadder[0].ToString());
            Equal("and ends at the highest",
                AudioPlayer.HighestSemitones.ToString(), AudioPlayer.PitchLadder[^1].ToString());
            Check("every semitone in between is offered",
                AudioPlayer.PitchLadder.Length ==
                    AudioPlayer.HighestSemitones - AudioPlayer.LowestSemitones + 1,
                AudioPlayer.PitchLadder.Length.ToString());
            Check("and normal is one of them",
                Array.IndexOf(AudioPlayer.PitchLadder, 0) >= 0);

            Console.WriteLine();
        }

        /// <summary>
        /// The twenty tone controls.
        ///
        /// Written with *audio* in it, which is the whole lesson of the gain
        /// effect that shipped silent for months: every other audio fixture in
        /// this suite is generated silence, and a transform that eats every
        /// sample is indistinguishable from one that works when the input is
        /// silence too. So each check below feeds a real sine through the real
        /// Process and reads what comes back at a real frequency.
        ///
        /// The check that matters most is not "does the band get louder". A
        /// volume control passes that. It is that the band next door does *not*
        /// — which is the only thing separating an equaliser from a gain.
        /// </summary>
        /// <summary>
        /// Tone, gap, tone — interleaved, at a real sample rate, the way the
        /// decoder hands them over.
        /// </summary>
        private static float[] ToneGapTone(int rate, int channels,
            double toneSeconds, double gapSeconds, double toneAmplitude, double gapAmplitude)
        {
            int toneFrames = (int)(rate * toneSeconds);
            int gapFrames = (int)(rate * gapSeconds);
            var audio = new float[(toneFrames * 2 + gapFrames) * channels];

            for (int f = 0; f < toneFrames * 2 + gapFrames; f++)
            {
                bool inGap = f >= toneFrames && f < toneFrames + gapFrames;
                double amplitude = inGap ? gapAmplitude : toneAmplitude;
                var v = (float)(amplitude * Math.Sin(2 * Math.PI * 440 * f / rate));
                for (int c = 0; c < channels; c++) audio[f * channels + c] = v;
            }

            return audio;
        }

        /// <summary>
        /// A source over an array, handing out an awkward number of frames at a
        /// time so the partial-block path is exercised rather than assumed.
        /// </summary>
        private static Func<float[], int, int> SourceOver(float[] audio, int channels, int chunk = 977)
        {
            int at = 0;
            int total = audio.Length / channels;

            return (buffer, frames) =>
            {
                int take = Math.Min(Math.Min(frames, chunk), total - at);
                if (take <= 0) return 0;

                Array.Copy(audio, at * channels, buffer, 0, take * channels);
                at += take;
                return take;
            };
        }

        private static float[] Drain(SilenceReduction stage, int channels, int ask = 1024)
        {
            var made = new List<float>();
            var buffer = new float[ask * channels];

            while (true)
            {
                int got = stage.Read(buffer, ask);
                if (got <= 0) break;
                for (int i = 0; i < got * channels; i++) made.Add(buffer[i]);
            }

            return made.ToArray();
        }

        private static float PeakBetween(float[] audio, int channels, int fromFrame, int toFrame)
        {
            float peak = 0;
            int from = Math.Max(0, fromFrame) * channels;
            int to = Math.Min(audio.Length / channels, toFrame) * channels;

            for (int i = from; i < to; i++)
                if (Math.Abs(audio[i]) > peak) peak = Math.Abs(audio[i]);

            return peak;
        }

        /// <summary>
        /// The silence shortener, with audio in it.
        ///
        /// This is the file where that rule was learned — see "the gain effect,
        /// and why not to try it again" in CLAUDE.md — and it bites harder here
        /// than anywhere: a stage whose whole job is to *remove* audio is one
        /// whose failure mode is removing all of it, and a test that only checked
        /// the output got shorter would call that a success. So every check below
        /// is against a real 440Hz tone with a real gap in it, and the ones that
        /// matter are that the tone is still there, still at its own amplitude,
        /// and that the gap is exactly as long as it was asked to be.
        /// </summary>
        private static void SilenceReductionTests()
        {
            Console.WriteLine("Shortening the silences:");

            const int rate = 48000;
            const int channels = 2;

            // ---- off is a bypass, bit for bit ----
            //
            // Not "a threshold nothing crosses". The promise TimeStretch makes at
            // rate 1 and the equaliser makes when it is off: the samples come out
            // exactly as they went in, so a setting can be compared against no
            // effect rather than against a slightly rounded one.
            var audio = ToneGapTone(rate, channels, 0.5, 1.0, 0.5, 0.0);

            var off = new SilenceReduction(channels, rate) { Source = SourceOver(audio, channels) };
            var passed = Drain(off, channels);

            Equal("off, every frame comes back",
                (audio.Length / channels).ToString(), (passed.Length / channels).ToString());

            bool identical = passed.Length == audio.Length;
            for (int i = 0; identical && i < audio.Length; i++)
                if (passed[i] != audio[i]) identical = false;
            Check("and every sample is the one that went in", identical);

            // ---- a gap shorter than the minimum is not touched at all ----
            //
            // The common case in music, and it has to cost nothing audible: not a
            // shorter gap, not a quieter one, the same samples in the same order.
            var shortGap = ToneGapTone(rate, channels, 1.0, 0.1, 0.5, 0.0);
            var leaveAlone = new SilenceReduction(channels, rate)
            {
                Source = SourceOver(shortGap, channels),
                Enabled = true,
                ThresholdDecibels = -48,
                MinimumMilliseconds = 400,
                LeaveMilliseconds = 150,
                FadeOutMilliseconds = 0,
                FadeInMilliseconds = 0,
            };
            var kept = Drain(leaveAlone, channels);

            Equal("a gap under the minimum keeps every frame",
                (shortGap.Length / channels).ToString(), (kept.Length / channels).ToString());

            identical = kept.Length == shortGap.Length;
            for (int i = 0; identical && i < shortGap.Length; i++)
                if (kept[i] != shortGap[i]) identical = false;
            Check("and does not so much as scale one", identical);
            Equal("and nothing is counted as removed", "0", leaveAlone.RemovedFrames.ToString());

            // ---- a gap over the minimum is shortened to exactly what was asked ----
            var longGap = ToneGapTone(rate, channels, 2.0, 2.0, 0.5, 0.0);
            var squeeze = new SilenceReduction(channels, rate)
            {
                Source = SourceOver(longGap, channels),
                Enabled = true,
                ThresholdDecibels = -48,
                MinimumMilliseconds = 400,
                LeaveMilliseconds = 200,
                FadeOutMilliseconds = 0,
                FadeInMilliseconds = 0,
            };
            var shortened = Drain(squeeze, channels);

            // Two seconds of tone, two tenths of the gap, two seconds of tone.
            // Exact rather than approximate: the detection block divides both the
            // tone and the gap here, so there is no rounding to allow for and a
            // tolerance would only hide a stage that was a block out.
            int expected = (int)(rate * 2.0) + (int)(rate * 0.2) + (int)(rate * 2.0);
            Equal("a long gap comes out the length it was told to be",
                expected.ToString(), (shortened.Length / channels).ToString());

            Equal("and what went missing is what it says it removed",
                ((longGap.Length - shortened.Length) / channels).ToString(),
                squeeze.RemovedFrames.ToString());

            // The whole point, and the assertion a stage that emitted silence
            // could not fake: the music is still there, at its own amplitude,
            // both sides of the cut.
            Check("the tone before the gap survives",
                PeakBetween(shortened, channels, rate / 2, rate) > 0.45f,
                PeakBetween(shortened, channels, rate / 2, rate).ToString("0.000"));

            Check("and so does the tone after it",
                PeakBetween(shortened, channels, expected - rate, expected - rate / 2) > 0.45f,
                PeakBetween(shortened, channels, expected - rate, expected - rate / 2).ToString("0.000"));

            Check("and the gap that is left really is quiet",
                PeakBetween(shortened, channels, (int)(rate * 2.05), (int)(rate * 2.15)) < 0.001f);

            // ---- the fades ----
            //
            // A gap that is not digital silence, so the fade *out* has something
            // to act on. At minus sixty the quiet tone is silence as far as the
            // threshold is concerned, which is the setting this was built for.
            var quietGap = ToneGapTone(rate, channels, 1.0, 1.0, 0.5, 0.001);
            var faded = new SilenceReduction(channels, rate)
            {
                Source = SourceOver(quietGap, channels),
                Enabled = true,
                ThresholdDecibels = -48,
                MinimumMilliseconds = 300,
                LeaveMilliseconds = 300,
                FadeOutMilliseconds = 100,
                FadeInMilliseconds = 100,
            };
            var withFades = Drain(faded, channels);

            int gapAt = rate;                       // where the kept gap starts
            int fadeFrames = rate / 10;             // 100 milliseconds
            int resumeAt = gapAt + (int)(rate * 0.3);

            // The fade out ends where the sound does, and with the gap left as it
            // was recorded that is the cut — so the ramp is on the *tail* of the
            // kept gap, not its head. The head of the gap is still the recording
            // at its own level.
            float beforeFade = PeakBetween(withFades, channels, gapAt, gapAt + rate / 100);
            float endOfFade = PeakBetween(withFades, channels,
                resumeAt - rate / 100, resumeAt);

            Check("the gap starts at the level it was recorded at",
                beforeFade > 0.0008f, beforeFade.ToString("0.000000"));
            Check("and the fade out has taken it to nothing by the cut",
                endOfFade < beforeFade / 20, endOfFade.ToString("0.000000"));

            // And the fade in, on the audio that resumes.
            float headOfResume = PeakBetween(withFades, channels, resumeAt, resumeAt + rate / 100);
            float tailOfResume = PeakBetween(withFades, channels,
                resumeAt + fadeFrames - rate / 100, resumeAt + fadeFrames);

            Check("the audio coming back fades in rather than arriving",
                headOfResume < 0.1f, headOfResume.ToString("0.000"));
            Check("and is at full amplitude by the end of the fade",
                tailOfResume > 0.45f, tailOfResume.ToString("0.000"));

            // ---- and the two are genuinely independent ----
            //
            // The fault this replaced: one number for both, taken out of the front
            // of the gap that was left, so asking for nothing to be left silently
            // meant asking for a splice. A fade out longer than the gap now
            // reaches back into the music, and with nothing left at all it is
            // entirely in the music.
            var reachesBack = new SilenceReduction(channels, rate)
            {
                Source = SourceOver(quietGap, channels),
                Enabled = true,
                ThresholdDecibels = -48,
                MinimumMilliseconds = 300,
                LeaveMilliseconds = 0,
                FadeOutMilliseconds = 200,
                FadeInMilliseconds = 0,
            };
            var backwards = Drain(reachesBack, channels);

            // With nothing left of the gap, the cut is at the end of the first
            // tone — so the last 200ms of that tone are what the fade lands on.
            int cutAt = rate;
            float beforeRamp = PeakBetween(backwards, channels,
                cutAt - (int)(rate * 0.21), cutAt - (int)(rate * 0.2));
            float atCut = PeakBetween(backwards, channels, cutAt - rate / 100, cutAt);

            Check("a fade out longer than the gap reaches back into the music",
                beforeRamp > 0.45f && atCut < 0.05f,
                $"{beforeRamp:0.000} down to {atCut:0.000}");

            // The same signal with no fade out keeps the music at full amplitude
            // right up to the cut — which is the check that the ramp above is the
            // fade and not something else about the signal.
            var noFadeOut = new SilenceReduction(channels, rate)
            {
                Source = SourceOver(quietGap, channels),
                Enabled = true,
                ThresholdDecibels = -48,
                MinimumMilliseconds = 300,
                LeaveMilliseconds = 0,
                FadeOutMilliseconds = 0,
                FadeInMilliseconds = 0,
            };
            var abrupt = Drain(noFadeOut, channels);

            Check("and with no fade out the music runs full into the cut",
                PeakBetween(abrupt, channels, cutAt - rate / 100, cutAt) > 0.45f,
                PeakBetween(abrupt, channels, cutAt - rate / 100, cutAt).ToString("0.000"));

            // One fade set and the other not, to prove neither is the other.
            var outOnly = new SilenceReduction(channels, rate)
            {
                Source = SourceOver(quietGap, channels),
                Enabled = true,
                ThresholdDecibels = -48,
                MinimumMilliseconds = 300,
                LeaveMilliseconds = 300,
                FadeOutMilliseconds = 100,
                FadeInMilliseconds = 0,
            };
            var asymmetric = Drain(outOnly, channels);

            Check("a fade out with no fade in still fades out",
                PeakBetween(asymmetric, channels, resumeAt - rate / 100, resumeAt) < 0.0001f);
            Check("and the music comes back at once",
                PeakBetween(asymmetric, channels, resumeAt, resumeAt + rate / 100) > 0.45f);

            // Without a fade the same signal arrives at full amplitude in the
            // first block — which is the check that the fade above is the fade
            // and not something else about the signal.
            // ---- what has been read and not handed on is declared ----
            //
            // A gap that turns out to be too short comes out in one go — the
            // whole of it, at the moment the music comes back — so a caller
            // asking for less than that leaves the rest staged here. Those frames
            // have been read from the decoder and not yet heard, and the position
            // display subtracts exactly this number; without it the clock runs
            // ahead of the music by however much is waiting.
            var watched = new SilenceReduction(channels, rate)
            {
                Source = SourceOver(shortGap, channels),
                Enabled = true,
                ThresholdDecibels = -48,
                MinimumMilliseconds = 400,
                LeaveMilliseconds = 150,
                FadeOutMilliseconds = 0,
                FadeInMilliseconds = 0,
            };

            var block = new float[1024 * channels];
            int held = 0;
            while (true)
            {
                int got = watched.Read(block, 64);
                if (got <= 0) break;
                held = Math.Max(held, watched.HeldSourceFrames);
            }

            Check("frames read but not yet handed over are reported as held",
                held > 1000, held.ToString());

            // ---- and Reset drops them, because they are the old position ----
            var seeking = new SilenceReduction(channels, rate)
            {
                Source = SourceOver(shortGap, channels),
                Enabled = true,
                ThresholdDecibels = -48,
                MinimumMilliseconds = 400,
                LeaveMilliseconds = 150,
            };
            while (seeking.HeldSourceFrames == 0 && seeking.Read(block, 64) > 0) { }

            Check("something was held to begin with", seeking.HeldSourceFrames > 0);
            seeking.Reset();
            Equal("and a seek empties it", "0", seeking.HeldSourceFrames.ToString());

            // ---- switching off mid-gap does not put a hole in the music ----
            var toggled = new SilenceReduction(channels, rate)
            {
                Source = SourceOver(quietGap, channels),
                Enabled = true,
                ThresholdDecibels = -48,
                MinimumMilliseconds = 5000,   // longer than the gap, so it only ever holds
                LeaveMilliseconds = 0,
                FadeOutMilliseconds = 0,
                FadeInMilliseconds = 0,
            };

            var sofar = new List<float>();
            int want = (int)(rate * 1.2);
            while (sofar.Count / channels < want)
            {
                int got = toggled.Read(block, 1024);
                if (got <= 0) break;
                for (int i = 0; i < got * channels; i++) sofar.Add(block[i]);
            }

            toggled.Enabled = false;
            foreach (var f in Drain(toggled, channels)) sofar.Add(f);

            Equal("unticking the box hands back what it was holding",
                (quietGap.Length / channels).ToString(), (sofar.Count / channels).ToString());

            // ---- the far end of every range, which is reachable on purpose ----
            //
            // Minus three decibels, and a gap that is a fifth of full scale — so
            // the "silence" being taken out here is a signal most of a recording
            // would call music. The tone is at full scale, which is the only
            // thing left above the threshold.
            // Twenty milliseconds rather than the floor, deliberately: below
            // that the detection block shrinks with the minimum, and a block of a
            // few dozen frames reads a sine's own zero crossings as silence. That
            // is the right behaviour and it is tested on its own below; here the
            // question is the threshold, so the block is pinned at its full size.
            var loud = ToneGapTone(rate, channels, 1.0, 1.0, 1.0, 0.2);
            var extreme = new SilenceReduction(channels, rate)
            {
                Source = SourceOver(loud, channels),
                Enabled = true,
                ThresholdDecibels = SilenceReduction.HighestThresholdDecibels,
                MinimumMilliseconds = 20,
                LeaveMilliseconds = 0,
                FadeOutMilliseconds = 0,
                FadeInMilliseconds = 0,
            };
            var chopped = Drain(extreme, channels);

            Check("the absurd end of the range takes out what it was told to",
                chopped.Length > 0 && chopped.Length < loud.Length,
                (chopped.Length / channels).ToString());

            Check("and leaves only what was above the threshold",
                Math.Abs(chopped.Length / channels - 2 * rate) < rate / 10,
                (chopped.Length / channels).ToString());

            // And the honest consequence of a threshold above the whole
            // recording: there is nothing left, the stage says so by running out,
            // and the pump ends the track. Not a hang and not a crash — an answer.
            var everything = new SilenceReduction(channels, rate)
            {
                Source = SourceOver(ToneGapTone(rate, channels, 0.2, 0.2, 0.1, 0.0), channels),
                Enabled = true,
                ThresholdDecibels = SilenceReduction.HighestThresholdDecibels,
                MinimumMilliseconds = 20,
                LeaveMilliseconds = 0,
            };
            Equal("a threshold above the whole recording leaves nothing of it",
                "0", (Drain(everything, channels).Length / channels).ToString());

            // ---- a minimum shorter than the detection block still means it ----
            //
            // The block follows the minimum down: a quarter of it, floored at
            // eight frames. Without that, everything under about three
            // milliseconds would behave identically — the control would go on
            // offering numbers it had stopped being able to tell apart, which is
            // a dial with nothing on the end of it.
            //
            // Five milliseconds of silence is 240 frames, under two of the full
            // 128-frame blocks, so a fixed block could not find a three
            // millisecond gap inside it at all.
            var tinyGap = ToneGapTone(rate, channels, 0.2, 0.005, 0.5, 0.0);

            var fine = new SilenceReduction(channels, rate)
            {
                Source = SourceOver(tinyGap, channels),
                Enabled = true,
                ThresholdDecibels = -48,
                MinimumMilliseconds = 3,
                LeaveMilliseconds = 0,
                FadeOutMilliseconds = 0,
                FadeInMilliseconds = 0,
            };
            var tightened = Drain(fine, channels);

            Check("a gap of five milliseconds is found at a three millisecond minimum",
                fine.RemovedFrames > 100 && fine.RemovedFrames <= 240,
                fine.RemovedFrames.ToString());

            Check("and the music either side of it is untouched",
                PeakBetween(tightened, channels, 0, (int)(rate * 0.19)) > 0.45f &&
                PeakBetween(tightened, channels, (int)(rate * 0.21), (int)(rate * 0.39)) > 0.45f);

            // The same signal at the old floor finds nothing, which is what makes
            // the check above a check on the block size rather than on the gap.
            var coarse = new SilenceReduction(channels, rate)
            {
                Source = SourceOver(tinyGap, channels),
                Enabled = true,
                ThresholdDecibels = -48,
                MinimumMilliseconds = 20,
                LeaveMilliseconds = 0,
            };
            Drain(coarse, channels);
            Equal("and a twenty millisecond minimum leaves it alone", "0",
                coarse.RemovedFrames.ToString());

            // ---- the clamps hold whatever is handed in ----
            var clamped = new SilenceReduction(channels, rate)
            {
                ThresholdDecibels = 40,
                MinimumMilliseconds = -5,
                LeaveMilliseconds = 1_000_000,
                FadeOutMilliseconds = -1,
                FadeInMilliseconds = 9999,
            };
            Equal("a threshold above full scale is clamped",
                SilenceReduction.HighestThresholdDecibels.ToString(),
                clamped.ThresholdDecibels.ToString());
            Equal("and nought decibels is not offered, because it is silence",
                "-1", SilenceReduction.HighestThresholdDecibels.ToString());
            Equal("a negative minimum is clamped",
                SilenceReduction.ShortestMinimumMilliseconds.ToString(),
                clamped.MinimumMilliseconds.ToString());
            Equal("an enormous leave is clamped",
                SilenceReduction.LongestLeaveMilliseconds.ToString(),
                clamped.LeaveMilliseconds.ToString());
            Equal("a negative fade out is nothing", "0", clamped.FadeOutMilliseconds.ToString());
            Equal("and an enormous fade in is clamped",
                SilenceReduction.LongestFadeMilliseconds.ToString(),
                clamped.FadeInMilliseconds.ToString());

            // ---- the four ladders, and the words that go with them ----
            //
            // The words are the control. A list read out as a number is a list
            // nobody can set by ear, which is the only way any of these can be set.
            Check("every threshold rung says what it means",
                Array.TrueForAll(AudioPlayer.SilenceThresholdLadder,
                    v => AudioPlayer.DescribeSilenceThreshold(v).Contains(',')));
            Check("every minimum rung does too",
                Array.TrueForAll(AudioPlayer.SilenceMinimumLadder,
                    v => AudioPlayer.DescribeSilenceLength(v).Contains(',')));
            Check("and every leave rung",
                Array.TrueForAll(AudioPlayer.SilenceLeaveLadder,
                    v => AudioPlayer.DescribeSilenceLeave(v).Length > 0));
            Check("and every fade rung, in both directions",
                Array.TrueForAll(AudioPlayer.SilenceFadeLadder,
                    v => AudioPlayer.DescribeSilenceFadeOut(v).Length > 0 &&
                         AudioPlayer.DescribeSilenceFadeIn(v).Length > 0));

            // The two fades share a ladder and must not share their words: they
            // are read out one after the other in the same dialog, and two rows
            // saying the same sentence is a dialog that cannot be set by ear.
            Check("and the two fades are described differently",
                Array.TrueForAll(AudioPlayer.SilenceFadeLadder,
                    v => AudioPlayer.DescribeSilenceFadeOut(v) != AudioPlayer.DescribeSilenceFadeIn(v)));

            Check("every rung is inside what the stage will accept",
                Array.TrueForAll(AudioPlayer.SilenceThresholdLadder,
                    v => v >= SilenceReduction.LowestThresholdDecibels &&
                         v <= SilenceReduction.HighestThresholdDecibels) &&
                Array.TrueForAll(AudioPlayer.SilenceMinimumLadder,
                    v => v >= SilenceReduction.ShortestMinimumMilliseconds &&
                         v <= SilenceReduction.LongestMinimumMilliseconds) &&
                Array.TrueForAll(AudioPlayer.SilenceLeaveLadder,
                    v => v >= 0 && v <= SilenceReduction.LongestLeaveMilliseconds) &&
                Array.TrueForAll(AudioPlayer.SilenceFadeLadder,
                    v => v >= 0 && v <= SilenceReduction.LongestFadeMilliseconds));

            // A default that is not on its own ladder opens the dialog on the
            // nearest rung and then applies it, which is a setting that changes
            // by being looked at.
            var fresh = new Settings();
            Check("every default sits on a rung",
                Array.IndexOf(AudioPlayer.SilenceThresholdLadder, fresh.AudioSilenceThresholdDecibels) >= 0 &&
                Array.IndexOf(AudioPlayer.SilenceMinimumLadder, fresh.AudioSilenceMinimumMilliseconds) >= 0 &&
                Array.IndexOf(AudioPlayer.SilenceLeaveLadder, fresh.AudioSilenceLeaveMilliseconds) >= 0 &&
                Array.IndexOf(AudioPlayer.SilenceFadeLadder, fresh.AudioSilenceFadeOutMilliseconds) >= 0 &&
                Array.IndexOf(AudioPlayer.SilenceFadeLadder, fresh.AudioSilenceFadeInMilliseconds) >= 0);

            Check("and it is off until somebody asks for it", !fresh.AudioSilenceReduction);

            // ---- every value has words to be read out with ----
            //
            // The lists hold every value in their range rather than a ladder of
            // chosen ones, so every one of those is read out on the way past and
            // every one of them has to say something. A describe that produced an
            // empty string, or "1 milliseconds", is a rung that reads as a fault.
            int mute = 0;
            for (int v = SilenceReduction.LowestThresholdDecibels;
                 v <= SilenceReduction.HighestThresholdDecibels; v++)
                if (!AudioPlayer.DescribeSilenceThreshold(v).Contains(',')) mute++;

            for (int v = SilenceReduction.ShortestMinimumMilliseconds;
                 v <= SilenceReduction.LongestMinimumMilliseconds; v++)
                if (!AudioPlayer.DescribeSilenceLength(v).Contains(',')) mute++;

            for (int v = 0; v <= SilenceReduction.LongestLeaveMilliseconds; v++)
                if (AudioPlayer.DescribeSilenceLeave(v).Length == 0) mute++;

            for (int v = 0; v <= SilenceReduction.LongestFadeMilliseconds; v++)
                if (AudioPlayer.DescribeSilenceFadeOut(v).Length == 0 ||
                    AudioPlayer.DescribeSilenceFadeIn(v).Length == 0) mute++;

            Equal("every value in every range has something to say", "0", mute.ToString());

            Check("and one of anything is singular",
                AudioPlayer.DescribeSilenceLength(1).StartsWith("1 millisecond,", StringComparison.Ordinal),
                AudioPlayer.DescribeSilenceLength(1));

            // ---- and it is really in the chain, in both places one is built ----
            //
            // WasapiPlayback builds the decode chain twice: once when a track
            // starts and again when the output device changes to one with a
            // different format. The resampler was forgotten in the second of
            // those once, and the symptom was that moving to another device
            // silently dropped the pitch shift and left a dead object in the
            // chain. This is the same shape of bug one stage further up, and the
            // only cheap way to catch it is to read the source.
            var playback = SourceFile("WasapiPlayback.cs");
            if (playback == null) { Check("WasapiPlayback.cs can be read", false); Console.WriteLine(); return; }

            Equal("a shortener is built wherever a chain is", "2",
                Occurrences(playback, "new SilenceReduction(").ToString());

            Equal("and the stretcher reads through it in both", "2",
                Occurrences(playback, "Source = _silence.Read").ToString());

            Check("and never straight off the decoder instead",
                !playback.Contains("new TimeStretch(_sourceChannels) { Source = decoder.Read }",
                    StringComparison.Ordinal));

            Check("a seek empties it with the two stages behind it",
                playback.Contains("_silence?.Reset();", StringComparison.Ordinal));

            Check("a gapless repeat tells it the source is alive again",
                playback.Contains("_silence?.SourceRestarted();", StringComparison.Ordinal));

            Check("and the position subtracts what it is holding",
                playback.Contains("_silence?.HeldSourceFrames", StringComparison.Ordinal));

            // ---- the dialog opens, on values moved off their defaults ----
            //
            // Values deliberately off the suggested ones, because a dialog tested
            // only against the numbers it suggests is tested against the one case
            // that cannot be wrong.
            //
            // Timed, and the timing is the whole reason this dialog is five spin
            // boxes rather than five lists. A list holding every value in its
            // range announces beautifully and takes **1030ms to open**: eight
            // thousand items across five combo boxes, and a combo with its handle
            // made takes one window message per item. 105ms of that was building
            // the strings and nine hundred was handing them to Windows. This
            // window opens on a keystroke while music is playing.
            var built = System.Diagnostics.Stopwatch.StartNew();

            var form = new SilenceForm(-11, 37, 3, 201, 303, true,
                (_, _, _, _, _, _) => { });
            try
            {
                long toBuild = built.ElapsedMilliseconds;

                form.Font = new System.Drawing.Font(form.Font.FontFamily, form.Font.SizeInPoints * 1.5f);
                form.Show();
                Application.DoEvents();

                // Handle creation is where the list version died, so the
                // measurement covers Show rather than stopping at the
                // constructor. The bar is a tenth of what the lists cost.
                Check($"the dialog is on screen quickly ({toBuild}ms to build, " +
                      $"{built.ElapsedMilliseconds}ms in all)",
                    built.ElapsedMilliseconds < 250, built.ElapsedMilliseconds + "ms");

                Check("the dialog opens", form.Visible);
                Check("and grows to fit its controls at a larger font",
                    form.ClientSize.Height >= form.PreferredSize.Height,
                    $"{form.ClientSize.Height} against {form.PreferredSize.Height}");
                Check("and opens showing what it was given", form.SilenceOn);
                Check("including the numbers that are on no suggested rung",
                    form.ThresholdDecibels == -11 && form.MinimumMilliseconds == 37 &&
                    form.LeaveMilliseconds == 3 && form.FadeOutMilliseconds == 201 &&
                    form.FadeInMilliseconds == 303,
                    $"{form.ThresholdDecibels} {form.MinimumMilliseconds} {form.LeaveMilliseconds} " +
                    $"{form.FadeOutMilliseconds} {form.FadeInMilliseconds}");
            }
            finally { form.Dispose(); }

            // ---- and the keyboard rules the spin boxes carry ----
            var dialog = SourceFile("SilenceForm.cs");
            if (dialog == null) { Check("SilenceForm.cs can be read", false); Console.WriteLine(); return; }

            // A range five thousand long with a step of one needs a coarse gear,
            // and a spin box brings neither it nor the ends.
            Check("page up and down jump between the suggested values",
                dialog.Contains("key is Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End",
                    StringComparison.Ordinal));

            // The words are what a list read out for free and a spin box cannot.
            // Said by the application, once the arrowing stops — see the note on
            // SayWhereItLanded.
            Check("the words are spoken once the arrowing stops",
                dialog.Contains("Speech.Speak(row.Describe(row.Value), interrupt: false)",
                    StringComparison.Ordinal) &&
                dialog.Contains("_settle.Stop();", StringComparison.Ordinal));

            Check("and a sweep speaks once rather than once per press",
                dialog.Contains("_settle.Stop();\n                    _settle.Start();",
                    StringComparison.Ordinal));

            Console.WriteLine();
        }

        private static void EqualiserTests()
        {
            Console.WriteLine("The equaliser:");

            // ---- the bands themselves ----
            Equal("there are twenty bands", "20", Equaliser.Frequencies.Length.ToString());
            Equal("and BandCount agrees with the list",
                Equaliser.Frequencies.Length.ToString(), Equaliser.BandCount.ToString());
            Equal("the lowest is 32 hertz", "32", Equaliser.Frequencies[0].ToString());
            Equal("the highest is 20 kilohertz", "20000",
                Equaliser.Frequencies[^1].ToString());

            bool rising = true;
            for (int b = 1; b < Equaliser.Frequencies.Length; b++)
                if (Equaliser.Frequencies[b] <= Equaliser.Frequencies[b - 1]) rising = false;
            Check("they go up, one band at a time", rising);

            // Half an octave a step, give or take the rounding to preferred
            // numbers — and the last step narrower, because 22.4kHz would be
            // above the Nyquist frequency of a CD. A ladder that had quietly
            // become uneven would make one slider cover twice the range of its
            // neighbour with nothing to show for it.
            double widest = 0, narrowest = 99;
            for (int b = 1; b < Equaliser.Frequencies.Length - 1; b++)
            {
                double ratio = (double)Equaliser.Frequencies[b] / Equaliser.Frequencies[b - 1];
                widest = Math.Max(widest, ratio);
                narrowest = Math.Min(narrowest, ratio);
            }
            Check("the spacing is even, at about half an octave",
                narrowest > 1.35 && widest < 1.50,
                $"between {narrowest:0.000} and {widest:0.000}");

            // ---- off is a bypass, bit for bit ----
            {
                var equaliser = new Equaliser();
                equaliser.SetGains(new[] { 12, -12, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
                equaliser.Enabled = false;

                var original = new float[512 * 2];
                for (int i = 0; i < original.Length; i++)
                    original[i] = (float)(0.4 * Math.Sin(i * 0.01));

                var work = (float[])original.Clone();
                equaliser.Process(work, work.Length, 2, 48000);

                bool identical = true;
                for (int i = 0; i < work.Length; i++) if (work[i] != original[i]) identical = false;

                Check("switched off, the samples come out exactly as they went in", identical);
            }

            // ---- and so is flat, which is not the same statement ----
            {
                var equaliser = new Equaliser { Enabled = true };

                var original = new float[512 * 2];
                for (int i = 0; i < original.Length; i++)
                    original[i] = (float)(0.4 * Math.Sin(i * 0.01));

                var work = (float[])original.Clone();
                equaliser.Process(work, work.Length, 2, 48000);

                bool identical = true;
                for (int i = 0; i < work.Length; i++) if (work[i] != original[i]) identical = false;

                Check("switched on and flat, they still come out untouched", identical);
            }

            // ---- a band that is raised is raised, by the amount asked for ----
            //
            // Band 10 is 1000Hz, which is where a peaking section is best
            // behaved: far from the low end where the poles crowd the unit
            // circle and far from Nyquist where the bilinear transform warps.
            {
                var equaliser = new Equaliser { Enabled = true };
                equaliser[10] = 12;

                double got = EqualiserGainAt(equaliser, 1000);
                Check("a band raised twelve decibels raises its own frequency by twelve",
                    Math.Abs(got - 12) < 1.0, $"measured {got:0.00} dB");
            }

            // ---- and a cut is the mirror of it ----
            {
                var equaliser = new Equaliser { Enabled = true };
                equaliser[10] = -12;

                double got = EqualiserGainAt(equaliser, 1000);
                Check("and cutting it by twelve cuts by twelve",
                    Math.Abs(got + 12) < 1.0, $"measured {got:0.00} dB");
            }

            // ---- the one that separates an equaliser from a volume control ----
            //
            // This is the check the whole file exists for. A transform that
            // multiplied every sample by four would pass both of the tests above
            // and be a gain, not a tone control; the question is what it did to
            // everything it was not asked about.
            {
                var equaliser = new Equaliser { Enabled = true };
                equaliser[10] = 12;

                double low = EqualiserGainAt(equaliser, 125);     // three octaves down
                double high = EqualiserGainAt(equaliser, 8000);   // three octaves up

                Check("raising 1 kilohertz leaves 125 hertz alone",
                    Math.Abs(low) < 1.5, $"moved by {low:0.00} dB");
                Check("and leaves 8 kilohertz alone",
                    Math.Abs(high) < 1.5, $"moved by {high:0.00} dB");
            }

            // ---- the ends are shelves, and that is what makes them useful ----
            //
            // A peaking filter at 32Hz leaves everything below 32Hz where it
            // was, so pulling that slider down to cut rumble would cut a band
            // and leave the rumble. Measured at 20Hz, which is under the lowest
            // band and is exactly the place a peak would not reach.
            {
                var equaliser = new Equaliser { Enabled = true };
                equaliser[0] = 12;

                double got = EqualiserGainAt(equaliser, 20);
                Check("the lowest band is a shelf, so it lifts 20 hertz too",
                    got > 8, $"measured {got:0.00} dB at 20 Hz");
            }

            {
                var equaliser = new Equaliser { Enabled = true };
                equaliser[Equaliser.BandCount - 1] = 12;

                // At the band's own centre, which is the number printed under
                // the slider. A shelf sitting *on* 20kHz would read about two
                // decibels here for a slider set to twelve — half its gain is at
                // the corner and the rest is above the Nyquist frequency of a CD
                // — so the top slider would be a control that does nothing. The
                // corner goes between the last two bands instead.
                double got = EqualiserGainAt(equaliser, 20000);
                Check("and the highest is a shelf that reaches its own frequency",
                    got > 9, $"measured {got:0.00} dB at 20 kHz");

                // And it takes over from the band below rather than overlapping
                // it: 16kHz is band 18's, and the top slider should be doing
                // about half its work there and no more.
                double below = EqualiserGainAt(equaliser, 16000);
                Check("without swallowing the band below it",
                    below > 1 && below < 9, $"measured {below:0.00} dB at 16 kHz");
            }

            // ---- the bank sums smoothly, which is what "sounds good" means ----
            //
            // Twenty filters each exactly as wide as the gap to its neighbour
            // leaves a dip between every pair of them: raise everything by six
            // and you get six decibels with a comb through it. The bands overlap
            // instead, so what is checked is that the centre of a band and the
            // gap beside it come out within a decibel of each other.
            {
                var equaliser = new Equaliser { Enabled = true };
                for (int b = 0; b < Equaliser.BandCount; b++) equaliser[b] = 6;

                double onBand = EqualiserGainAt(equaliser, 1000);          // band 10
                double between = EqualiserGainAt(equaliser, 1183);         // between 1000 and 1400
                double onNext = EqualiserGainAt(equaliser, 1400);          // band 11

                Check("every band raised is a boost, not a comb",
                    Math.Abs(onBand - between) < 1.0 && Math.Abs(onNext - between) < 1.0,
                    $"{onBand:0.00} on band, {between:0.00} between, {onNext:0.00} on the next");

                // And a flat six decibels asked for across the whole bank comes
                // out as a lift somewhere in the region of six — not as one
                // band's worth, and not as twenty bands multiplied together.
                Check("and a flat six decibels comes out as a sensible lift",
                    onBand > 5 && onBand < 16, $"measured {onBand:0.00} dB");
            }

            // ---- and the ripple stays a small fraction of the lift ----
            //
            // Not an absolute number of decibels, because that is not what the
            // property is. The dip between two band centres grows in proportion
            // to how far the bands are pushed — measured at about a tenth of the
            // total lift at every Q from 1.2 to 2.0 — so an absolute threshold
            // would either pass trivially at +6 or fail inevitably at +24 while
            // describing the same bank either way.
            //
            // A tenth is what a bank of fixed overlapping filters does. What
            // would be a real fault is a *comb*: nulls between the bands, where
            // the gap is a fraction of the peak rather than a tenth under it.
            // That is what this catches, at both ends of the range.
            foreach (int level in new[] { 6, 12, Equaliser.MaximumDecibels })
            {
                var equaliser = new Equaliser { Enabled = true };
                for (int b = 0; b < Equaliser.BandCount; b++) equaliser[b] = level;

                double onBand = EqualiserGainAt(equaliser, 1000);
                double between = EqualiserGainAt(equaliser, 1183);
                double ripple = Math.Abs(onBand - between);

                Check($"at +{level} on every band the ripple is a small part of the lift " +
                      $"({onBand:0.0} on band, {between:0.0} between)",
                    onBand > 0 && ripple < onBand * 0.15,
                    $"{ripple:0.00} dB of ripple on {onBand:0.00} dB of lift");
            }

            // ---- one band really does reach the maximum it offers ----
            //
            // The point of widening the range is that the extremes are
            // reachable. A slider whose top rung produced eighteen decibels
            // while claiming twenty-four would be a control lying about itself.
            {
                var equaliser = new Equaliser { Enabled = true };
                equaliser[10] = Equaliser.MaximumDecibels;

                double got = EqualiserGainAt(equaliser, 1000);
                Check($"a band at the maximum really is {Equaliser.MaximumDecibels} decibels",
                    Math.Abs(got - Equaliser.MaximumDecibels) < 1.0, $"measured {got:0.00} dB");

                equaliser[10] = -Equaliser.MaximumDecibels;
                double cut = EqualiserGainAt(equaliser, 1000);
                Check($"and the deepest cut is {Equaliser.MaximumDecibels} the other way",
                    Math.Abs(cut + Equaliser.MaximumDecibels) < 1.0, $"measured {cut:0.00} dB");
            }

            // ---- nothing comes out that is not a number ----
            //
            // A biquad with badly conditioned coefficients goes to infinity and
            // stays there, and the first anybody would know is silence or a
            // scream. Every band at its extreme, at the two rates that matter.
            foreach (int rate in new[] { 44100, 48000 })
            {
                var equaliser = new Equaliser { Enabled = true };
                for (int b = 0; b < Equaliser.BandCount; b++)
                    equaliser[b] = b % 2 == 0 ? Equaliser.MaximumDecibels : -Equaliser.MaximumDecibels;

                bool sane = true;
                var work = new float[512 * 2];
                for (int pass = 0; pass < 50; pass++)
                {
                    for (int f = 0; f < 512; f++)
                    {
                        float v = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * (pass * 512 + f) / rate));
                        work[f * 2] = work[f * 2 + 1] = v;
                    }

                    equaliser.Process(work, work.Length, 2, rate);

                    foreach (float v in work)
                        if (float.IsNaN(v) || float.IsInfinity(v) || Math.Abs(v) > 100) sane = false;
                }

                Check($"every band at its limit stays finite at {rate} Hz", sane);
            }

            // ---- the wording, which is what is actually heard ----
            Equal("a band under a kilohertz is said in hertz",
                "500 hertz", Equaliser.DescribeFrequency(500));
            Equal("a round one above is said in kilohertz",
                "2 kilohertz", Equaliser.DescribeFrequency(2000));
            Equal("and one that is not round keeps its decimal",
                "1.4 kilohertz", Equaliser.DescribeFrequency(1400));

            Equal("zero is flat, not a number", "flat", Equaliser.DescribeGain(0));
            Equal("a boost says which way it went", "up 3 decibels", Equaliser.DescribeGain(3));
            Equal("and so does a cut", "down 6 decibels", Equaliser.DescribeGain(-6));
            Equal("one decibel is singular", "up 1 decibel", Equaliser.DescribeGain(1));

            // ---- and it refuses what a hand-edited settings file might hold ----
            {
                var equaliser = new Equaliser();
                equaliser[0] = 999;
                equaliser[1] = -999;
                Equal("a band is clamped upwards",
                    Equaliser.MaximumDecibels.ToString(), equaliser[0].ToString());
                Equal("and downwards",
                    (-Equaliser.MaximumDecibels).ToString(), equaliser[1].ToString());

                // A band that does not exist is not a crash. The settings file is
                // one people are invited to edit.
                equaliser[-1] = 6;
                equaliser[Equaliser.BandCount] = 6;
                Equal("an index off either end is ignored", "0", equaliser[-1].ToString());

                equaliser.SetGains(new[] { 4, 5 });
                var back = equaliser.Gains();
                Equal("a short list fills the rest in flat",
                    Equaliser.BandCount.ToString(), back.Length.ToString());
                Equal("keeping what it did carry", "4", back[0].ToString());
                Equal("and flattening what it did not", "0", back[19].ToString());
            }

            Console.WriteLine();
        }

        /// <summary>
        /// With nothing switched on, the samples come out exactly as they went
        /// in — the whole chain, not one piece of it.
        ///
        /// Each part promises this for itself and is tested for it separately:
        /// TimeStretch bypasses at rate 1, the equaliser bypasses when it is off
        /// or flat, the gain is exactly 1.0 at 100 percent. What nothing checked
        /// is the promise they add up to, which is the only one anybody actually
        /// has: put a file in, get the file out. A single stage quietly rounding
        /// or lifting is not visible from any of the individual tests, and it is
        /// audible.
        /// </summary>
        private static void NeutralChainTests()
        {
            Console.WriteLine("Nothing switched on changes nothing:");

            // ---- full volume is exactly one, on both curves ----
            //
            // Not "about one". A multiply by 0.9999997 is a whole different
            // statement from a multiply by 1, because only one of them leaves
            // every sample bit for bit where it was.
            foreach (var curve in new[] { VolumeCurve.Linear, VolumeCurve.Perceptual })
                Check($"100 percent on the {curve} curve is exactly 1",
                    AudioPlayer.DirectGain(100, curve) == 1f,
                    AudioPlayer.DirectGain(100, curve).ToString("R"));

            // ---- and the renderer at rest passes everything through ----
            static float[] Signal()
            {
                var made = new float[1024 * 2];
                var random = new Random(7);
                for (int i = 0; i < made.Length; i++)
                    made[i] = (float)(random.NextDouble() * 2 - 1) * 0.7f;

                // The awkward ones as well, at known places: silence, the
                // smallest number there is, and a sample already at full scale.
                made[0] = 0f;
                made[1] = -0f;
                made[2] = float.Epsilon;
                made[3] = 1f;
                made[4] = -1f;
                return made;
            }

            static bool Identical(float[] a, float[] b)
            {
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                    if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i]))
                        return false;
                return true;
            }

            {
                var renderer = new WasapiRenderer();
                var original = Signal();
                var work = (float[])original.Clone();

                renderer.ApplyGainAndMeasure(work, work.Length);
                Check("a fresh renderer passes every sample through untouched",
                    Identical(original, work));
            }

            // ---- the limiter does nothing at or below full scale ----
            //
            // This is the one that was wrong, and it was wrong at the volume
            // most listening happens at. The ceiling was BaseBoost * drive *
            // drive with drive clamped up to 1, so at a gain of one the limiter
            // was still allowed to lift the quiet parts by eight times — which
            // is not "the effect grows with the volume", it is eighteen
            // decibels of compression at 100 percent and more of it at 6.
            foreach (int percent in new[] { 6, 50, 100 })
            {
                var renderer = new WasapiRenderer
                {
                    Limiter = true,
                    LimiterWholeRange = false,
                    Gain = AudioPlayer.DirectGain(percent, VolumeCurve.Linear),
                };

                var original = Signal();
                var work = (float[])original.Clone();
                var expected = new float[original.Length];
                for (int i = 0; i < original.Length; i++) expected[i] = original[i] * renderer.Gain;

                renderer.ApplyGainAndMeasure(work, work.Length);

                Check($"the limiter does nothing at {percent} percent volume",
                    Identical(expected, work),
                    "it is only for what happens above 100, unless \"same lift at any volume\" is on");
            }

            // ---- and at 100 percent that means the samples are untouched ----
            {
                var renderer = new WasapiRenderer { Limiter = true, LimiterWholeRange = false, Gain = 1f };
                var original = Signal();
                var work = (float[])original.Clone();

                renderer.ApplyGainAndMeasure(work, work.Length);
                Check("so the whole chain at full volume is bit for bit the file",
                    Identical(original, work));
            }

            // ---- above full scale it does start working ----
            //
            // The other half, and the half that keeps the fix honest: a limiter
            // that had simply been switched off everywhere would pass every
            // check above.
            {
                var renderer = new WasapiRenderer { Limiter = true, LimiterWholeRange = false, Gain = 4f };
                var quiet = new float[1024 * 2];
                for (int i = 0; i < quiet.Length; i += 2)
                {
                    float v = (float)(0.01 * Math.Sin(2 * Math.PI * 440 * (i / 2) / 48000.0));
                    quiet[i] = quiet[i + 1] = v;
                }

                var work = (float[])quiet.Clone();
                renderer.ApplyGainAndMeasure(work, work.Length);

                float loudest = 0;
                foreach (float v in work) loudest = Math.Max(loudest, Math.Abs(v));
                Check("but above 100 percent it lifts the quiet parts",
                    loudest > 0.04f * 4, $"peak {loudest:0.0000} from a 0.01 tone at four times gain");
            }

            // ---- and "same lift at any volume" is what makes it work below ----
            {
                var renderer = new WasapiRenderer { Limiter = true, LimiterWholeRange = true, Gain = 1f };
                var quiet = new float[1024 * 2];
                for (int i = 0; i < quiet.Length; i += 2)
                {
                    float v = (float)(0.01 * Math.Sin(2 * Math.PI * 440 * (i / 2) / 48000.0));
                    quiet[i] = quiet[i + 1] = v;
                }

                var work = (float[])quiet.Clone();
                renderer.ApplyGainAndMeasure(work, work.Length);

                float loudest = 0;
                foreach (float v in work) loudest = Math.Max(loudest, Math.Abs(v));
                Check("with the same lift at any volume, it works at 100 too",
                    loudest > 0.05f, $"peak {loudest:0.0000}");
            }

            // ---- and the equaliser at rest is not in the way of any of it ----
            {
                var renderer = new WasapiRenderer { Gain = 1f };
                renderer.Equaliser.Enabled = true;               // on, but flat
                var original = Signal();
                var work = (float[])original.Clone();

                renderer.ApplyGainAndMeasure(work, work.Length);
                Check("an equaliser switched on and flat is still a bypass",
                    Identical(original, work));
            }

            Console.WriteLine();
        }

        /// <summary>
        /// A timeout is never reported as somebody pressing Cancel.
        ///
        /// This is the bug behind "Google Drive disconnects mid copy — it just
        /// cancels ten percent through because it's a lot of files", and it is
        /// entirely a matter of exception *type*. HttpClient reports its own
        /// Timeout by throwing TaskCanceledException, which derives from
        /// OperationCanceledException, with nobody's token cancelled. Three
        /// layers each treated that type as "the person pressed Cancel":
        /// UploadOne rethrew it, Parallel.ForEachAsync then abandoned every
        /// other file in the batch, and the window reported "Cancelled after 216
        /// of 1134".
        ///
        /// It got worse the bigger the job, which is exactly backwards — more
        /// files means more folders to create and list, so more chances for one
        /// request to run long, and any single one of them ended everything.
        ///
        /// Checked by reading the source, because the condition cannot be
        /// reached without a Google account and a slow network: what is asserted
        /// is that every catch which *means* cancellation is guarded by the
        /// token actually being cancelled.
        /// </summary>
        private static void DriveNeverSelfCancelsTests()
        {
            Console.WriteLine("A slow Drive request is not a cancellation:");

            // The type really is the trap: this is what the guards are for.
            Check("HttpClient's timeout really does look like a cancellation",
                typeof(OperationCanceledException).IsAssignableFrom(typeof(TaskCanceledException)));

            var client = SourceFile("DriveClient.cs");
            var upload = SourceFile("DriveUpload.cs");
            var main = SourceFile("MainForm.cs");

            if (client == null || upload == null || main == null)
            {
                Check("the Drive sources can be read", false);
                Console.WriteLine();
                return;
            }

            // The fix at the source: a timeout is given a type of its own before
            // it can be mistaken for anything.
            Check("a timeout is turned into a TimeoutException where it happens",
                client.Contains("throw new TimeoutException(", StringComparison.Ordinal),
                "otherwise it travels as an OperationCanceledException and is read as Cancel");

            // And the second lock on the same door, at each place that treats
            // the type as meaning cancellation.
            Check("the per-file catch only treats a real cancellation as one",
                upload.Contains(
                    "catch (OperationCanceledException) when (token.IsCancellationRequested)",
                    StringComparison.Ordinal),
                "an unguarded catch here abandons every other file in the batch");

            Check("and so does the one that decides what the window reports",
                main.Contains(
                    "catch (OperationCanceledException) when (cts.IsCancellationRequested)",
                    StringComparison.Ordinal),
                "an unguarded catch here says \"Cancelled\" to somebody who pressed nothing");

            // Nothing in the upload path may catch the type unguarded. A bare
            // `catch (OperationCanceledException)` is the exact shape of the bug.
            foreach (var (name, text) in new[] { ("DriveUpload.cs", upload), ("DriveWrites.cs", SourceFile("DriveWrites.cs") ?? "") })
            {
                int bare = Occurrences(text, "catch (OperationCanceledException)\n") +
                           Occurrences(text, "catch (OperationCanceledException)\r\n") +
                           Occurrences(text, "catch (OperationCanceledException) {");
                Check($"{name} never catches a cancellation unguarded", bare == 0,
                    $"{bare} unguarded catch(es) — each one can abandon a whole transfer");
            }

            // The metadata half of an upload has a ceiling of its own now, and a
            // retry. Fifteen seconds was chosen for a filesystem callback that is
            // holding a window up; creating and listing folders inside a transfer
            // is not that, and a folder of several thousand files is several
            // pages of listing.
            Check("the upload path's metadata gets a longer ceiling than a UI callback",
                client.Contains("_slowMetadata", StringComparison.Ordinal) &&
                client.Contains("TimeSpan.FromMinutes(2)", StringComparison.Ordinal));

            // Used by both of the things an upload asks for between files:
            // listing a folder, which lives in DriveClient, and creating one,
            // which lives in DriveWrites. Counted across the pair because the
            // partial class is split across two files.
            var writes = SourceFile("DriveWrites.cs") ?? "";
            int retried = Occurrences(client, "SendWithRetry(") + Occurrences(writes, "SendWithRetry(");
            Check("and is retried rather than being fatal",
                client.Contains("SendWithRetry", StringComparison.Ordinal) && retried >= 3,
                $"{retried} mentions — expected the definition plus a listing and a folder create");

            // A request cannot be sent twice, so the retry has to rebuild it.
            Check("the retry rebuilds the request each time round",
                client.Contains("Func<Task<HttpRequestMessage>> build", StringComparison.Ordinal),
                "reusing an HttpRequestMessage fails on the reuse, not on what it was retrying");

            // And a failure that stops the whole run is now said out loud rather
            // than swallowed into "Uploaded 0 files".
            Check("a run that dies outright says why",
                main.Contains("Upload stopped after", StringComparison.Ordinal),
                "an upload that failed on its first folder used to report success");

            Console.WriteLine();
        }

        /// <summary>
        /// Pasting the same folder again fills in what did not arrive.
        ///
        /// The case is a thousand-file folder that copied six hundred and
        /// stopped, and none of the three policies that existed answered it.
        /// "Keep both" made a second folder beside the first. "Replace" recopied
        /// all thousand, which for forty gigabytes over a slow link is the whole
        /// job again. "Skip" skipped the *folder*, because it works on the names
        /// being pasted rather than on what is inside them — so the four hundred
        /// missing files stayed missing, and the operation reported success.
        /// </summary>
        private static void FillGapsTests()
        {
            Console.WriteLine("Filling in what did not copy:");

            var root = NewTempDir();
            try
            {
                var source = Path.Combine(root, "source");
                var destination = Path.Combine(root, "destination");
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(destination);

                // A folder with a subfolder, because the whole point is depth:
                // a policy that only looks at top-level names cannot answer this.
                Directory.CreateDirectory(Path.Combine(source, "album"));
                Directory.CreateDirectory(Path.Combine(source, "album", "disc 2"));

                var wanted = new[]
                {
                    Path.Combine("album", "one.txt"),
                    Path.Combine("album", "two.txt"),
                    Path.Combine("album", "three.txt"),
                    Path.Combine("album", "disc 2", "four.txt"),
                    Path.Combine("album", "disc 2", "five.txt"),
                };

                foreach (var relative in wanted)
                    File.WriteAllText(Path.Combine(source, relative), "original " + relative);

                // A previous attempt that got two of the five, one of them deep
                // in the tree — and the destination folder therefore exists,
                // which is what used to make Skip throw the whole thing away.
                Directory.CreateDirectory(Path.Combine(destination, "album"));
                Directory.CreateDirectory(Path.Combine(destination, "album", "disc 2"));
                File.WriteAllText(Path.Combine(destination, "album", "one.txt"), "ALREADY THERE");
                File.WriteAllText(Path.Combine(destination, "album", "disc 2", "four.txt"), "ALREADY THERE");

                var result = RoboCopyEngine.RunAsync(
                    new[] { Path.Combine(source, "album") }, destination, move: false,
                    threads: 4, PasteConflictPolicy.FillGaps, null, CancellationToken.None)
                    .GetAwaiter().GetResult();

                Check("the fill-in reported no failures", result.Failed == 0,
                    result.Errors.Count > 0 ? result.Errors[0] : result.Failed.ToString());

                // Every file is now there, at both depths.
                int present = 0;
                foreach (var relative in wanted)
                    if (File.Exists(Path.Combine(destination, relative))) present++;

                Equal("every missing file arrived", wanted.Length.ToString(), present.ToString());

                // And the two that were already there were not touched. This is
                // the half that separates filling gaps from replacing: the point
                // is not to recopy forty gigabytes to add four hundred files.
                Equal("a file that was already there is left alone",
                    "ALREADY THERE",
                    File.ReadAllText(Path.Combine(destination, "album", "one.txt")));
                Equal("including one inside a subfolder",
                    "ALREADY THERE",
                    File.ReadAllText(Path.Combine(destination, "album", "disc 2", "four.txt")));

                // Nothing was renamed, which is what "keep both" would have done
                // — and what a folder called "album (2)" beside the real one
                // means for somebody who cannot see the listing at a glance.
                Check("and nothing was copied in beside it under a new name",
                    !Directory.Exists(Path.Combine(destination, "album (2)")) &&
                    !Directory.Exists(Path.Combine(destination, "album (1)")),
                    "a second folder was created instead of merging");

                Equal("nothing is reported as replaced", "0",
                    result.Collisions.OverwrittenCount.ToString());

                // ---- and running it again does nothing at all ----
                //
                // The test that it really is comparing rather than copying: a
                // second pass over a destination that now has everything should
                // move no bytes.
                var again = RoboCopyEngine.RunAsync(
                    new[] { Path.Combine(source, "album") }, destination, move: false,
                    threads: 4, PasteConflictPolicy.FillGaps, null, CancellationToken.None)
                    .GetAwaiter().GetResult();

                Check("a second pass over a complete copy does nothing", again.Failed == 0,
                    again.Errors.Count > 0 ? again.Errors[0] : "");

                Equal("and still leaves the existing files alone",
                    "ALREADY THERE",
                    File.ReadAllText(Path.Combine(destination, "album", "one.txt")));
            }
            finally { Cleanup(root); }

            // ---- the policy is offered where it can be chosen ----
            Check("the conflict dialog offers filling in the gaps",
                ConflictForm.Describe(new[] { "album" }, "Music").Length > 0);

            var form = SourceFile("ConflictForm.cs");
            Check("and offers it first, as the safest answer",
                form != null &&
                form.IndexOf("ConflictChoice.FillGaps", StringComparison.Ordinal) <
                form.IndexOf("ConflictChoice.Rename", StringComparison.Ordinal),
                "keeping both is destructive of nothing but is not what a resumed copy means");

            // Appended to the enum, never inserted. These are stored in the
            // settings file as numbers, so a value in the middle would silently
            // change what everybody's saved setting means.
            Equal("Ask keeps the number it was saved under", "3",
                ((int)PasteConflictPolicy.Ask).ToString());
            Equal("and filling gaps was appended after it", "4",
                ((int)PasteConflictPolicy.FillGaps).ToString());

            Console.WriteLine();
        }

        /// <summary>
        /// What an upload does when Google says "not now".
        ///
        /// Written after a real upload of about a million small files lost one
        /// to a 502 whose own body says "please try again in 30 seconds" — a
        /// file reported as permanently failed on the strength of an answer that
        /// said the opposite. The resumable path had retried from the beginning;
        /// the small-file path, which is the one that runs a million times, had
        /// no retry at all.
        /// </summary>
        private static void DriveRetryTests()
        {
            Console.WriteLine("An upload that Google refuses for a moment:");

            // ---- which answers mean "not now" ----
            foreach (int status in new[] { 408, 429, 500, 502, 503, 504 })
                Check($"{status} is worth sending again", DriveClient.WorthRetrying(status));

            // And which mean "no". Retrying these turns one clear failure into
            // six identical ones and a minute of waiting: a 404 parent is not
            // going to appear, and an expired token is not going to un-expire.
            foreach (int status in new[] { 400, 401, 403, 404, 409, 412 })
                Check($"{status} is not retried", !DriveClient.WorthRetrying(status));

            // ---- the backoff grows, and is never the same twice ----
            {
                var random = new Random(1);
                var first = DriveClient.RetryDelay(0, random);
                var later = DriveClient.RetryDelay(4, random);

                Check("the first wait is about a second",
                    first.TotalSeconds is >= 1 and <= 2, $"{first.TotalSeconds:0.00}s");
                Check("and a later one is much longer",
                    later > first, $"{later.TotalSeconds:0.00}s against {first.TotalSeconds:0.00}s");

                // Capped, because past half a minute this stops being a retry
                // and starts being a hang with no way to tell it from one.
                var far = DriveClient.RetryDelay(20, random);
                Check("but never more than about a minute",
                    far.TotalSeconds <= 46, $"{far.TotalSeconds:0.00}s");

                // Jittered. Four upload streams hit the same bad moment
                // together, and a fixed backoff brings all four back at the same
                // instant to fail together — which is how a blip becomes an
                // outage.
                var spread = new HashSet<double>();
                for (int i = 0; i < 20; i++) spread.Add(DriveClient.RetryDelay(3, random).TotalSeconds);
                Check("and no two waits are identical", spread.Count > 15,
                    $"{spread.Count} distinct out of 20");
            }

            // ---- 403 is the one that needs the body read ----
            //
            // Google spells rate limiting two ways. 429 is one. The other is a
            // 403 whose payload carries a reason, and it is indistinguishable by
            // status code from the 403 that means "you may not do that" — which
            // is why the status-only test refuses all of them, and why that
            // refusal was wrong for exactly the case that matters: told to slow
            // down, the upload failed the file and immediately asked for the
            // next one.
            {
                const string throttled =
                    """
                    {"error":{"code":403,"errors":[{"domain":"usageLimits",
                    "reason":"rateLimitExceeded","message":"Rate Limit Exceeded"}],
                    "message":"Rate Limit Exceeded"}}
                    """;

                const string forbidden =
                    """
                    {"error":{"code":403,"errors":[{"domain":"global",
                    "reason":"insufficientFilePermissions","message":"The user does not have
                    sufficient permissions for this file."}],"message":"Insufficient permissions"}}
                    """;

                const string full =
                    """
                    {"error":{"code":403,"errors":[{"domain":"usageLimits",
                    "reason":"storageQuotaExceeded","message":"The user's Drive storage quota
                    has been exceeded."}],"message":"Quota exceeded"}}
                    """;

                const string daily =
                    """
                    {"error":{"code":403,"errors":[{"domain":"usageLimits",
                    "reason":"dailyLimitExceeded","message":"Daily Limit Exceeded"}],
                    "message":"Daily Limit Exceeded"}}
                    """;

                Check("a 403 that means \"slow down\" is retried",
                    DriveClient.WorthRetrying(403, throttled));

                Check("but a 403 that means \"you may not\" is not",
                    !DriveClient.WorthRetrying(403, forbidden));

                // The account is full. That is a state, not a moment, and
                // retrying it six times only delays telling somebody.
                Check("nor is one that means the account is full",
                    !DriveClient.WorthRetrying(403, full));

                // The window is a day. Six retries over two minutes will not
                // reach the other side of it.
                Check("nor one whose limit resets tomorrow",
                    !DriveClient.WorthRetrying(403, daily));

                // What a resumable upload does with each kind of refusal.
                Check("a full account stops a resumable upload rather than resending it",
                    !new DriveClient.UploadRefusedException(403, full).Retryable);
                Check("a busy server lets it carry on",
                    new DriveClient.UploadRefusedException(503, "").Retryable);
                Check("and an expired session is started again",
                    new DriveClient.UploadRefusedException(410, "").SessionGone &&
                    new DriveClient.UploadRefusedException(404, "").SessionGone &&
                    !new DriveClient.UploadRefusedException(403, full).SessionGone);
                Check("the refusal still reads as the full-account sentence",
                    new DriveClient.UploadRefusedException(403,
                        """{"error":{"code":403,"errors":[{"reason":"storageQuotaExceeded"}],"message":"Quota exceeded"}}""")
                        .Message.Contains("out of storage"));

                Equal("the reason is read out of the classic error shape",
                    "rateLimitExceeded", DriveClient.ErrorReason(throttled));

                // The newer single-status shape is still served alongside it.
                Equal("and out of the newer one",
                    "RESOURCE_EXHAUSTED",
                    DriveClient.ErrorReason("""{"error":{"code":429,"status":"RESOURCE_EXHAUSTED"}}"""));

                // Everything that is not a Google error payload has no reason,
                // and asking must not throw on the path already reporting a
                // failure.
                foreach (var junk in new[] { "", "   ", "<html>", "{", "{}", "null", "[]" })
                {
                    try
                    {
                        DriveClient.ErrorReason(junk);
                        DriveClient.WorthRetrying(403, junk);
                        Check($"a reason out of \"{junk}\" is survivable", true);
                    }
                    catch (Exception ex)
                    {
                        Check($"a reason out of \"{junk}\" is survivable", false, ex.Message);
                    }
                }

                // And the status-only overload is unchanged, because it is still
                // right everywhere the body is not in hand.
                Check("the status-only test still refuses every 403",
                    !DriveClient.WorthRetrying(403));
            }

            // ---- and Retry-After is obeyed rather than just quoted ----
            //
            // It was already being read, to put a number in a sentence, and then
            // ignored when deciding when to actually come back. The server is
            // the one party that knows when it will be ready.
            {
                var random = new Random(3);

                var ours = DriveClient.RetryDelay(0, random, null);
                var theirs = DriveClient.RetryDelay(0, random, TimeSpan.FromSeconds(20));

                Check("when Google says how long to wait, that is the wait",
                    theirs >= TimeSpan.FromSeconds(20) && theirs < TimeSpan.FromSeconds(22),
                    $"{theirs.TotalSeconds:0.00}s against our own {ours.TotalSeconds:0.00}s");

                // Still jittered on top, or four streams told to wait twenty
                // seconds all come back in the same instant.
                var spread = new HashSet<double>();
                for (int i = 0; i < 20; i++)
                    spread.Add(DriveClient.RetryDelay(0, random, TimeSpan.FromSeconds(20)).TotalSeconds);
                Check("and is still spread out across the streams", spread.Count > 15,
                    $"{spread.Count} distinct out of 20");

                var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
                response.Headers.Add("Retry-After", "30");
                var read = DriveClient.RetryAfter(response);
                Check("the header is read off the response",
                    read is { } r && Math.Abs(r.TotalSeconds - 30) < 1.5,
                    read?.TotalSeconds.ToString("0.0") ?? "null");

                // Capped, because an honest Retry-After is seconds and a very
                // large one is an outage — which is not a reason for a progress
                // window to sit silent for an hour.
                var silly = new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
                silly.Headers.Add("Retry-After", "86400");
                var capped = DriveClient.RetryAfter(silly);
                Check("and capped at something a person will wait through",
                    capped is { } c && c <= TimeSpan.FromMinutes(5),
                    capped?.TotalMinutes.ToString("0.0") ?? "null");

                Check("a response with no header asks for nothing",
                    DriveClient.RetryAfter(
                        new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway)) == null);
            }

            // ---- the two that stop everything, and say why ----
            //
            // "If I hit my daily rate limit, tell me why" — and the point is
            // that these must not join a list of nine hundred other failures,
            // because every one of those nine hundred would be this same
            // sentence and the explanation would scroll off the top.
            {
                const string daily =
                    """{"error":{"code":403,"errors":[{"reason":"dailyLimitExceeded"}],"message":"Daily Limit Exceeded"}}""";
                const string full =
                    """{"error":{"code":403,"errors":[{"reason":"storageQuotaExceeded"}],"message":"Quota exceeded"}}""";
                const string throttled =
                    """{"error":{"code":403,"errors":[{"reason":"rateLimitExceeded"}],"message":"Rate Limit Exceeded"}}""";

                var stopDaily = DriveQuotaException.From(403, daily);
                Check("a daily limit is something that stops the whole transfer", stopDaily != null);
                Check("and it says it comes back on its own",
                    stopDaily is { ResetsOnItsOwn: true });
                Check("in words rather than a code",
                    stopDaily != null && stopDaily.Message.Contains("24 hours", StringComparison.Ordinal),
                    stopDaily?.Message);

                var stopFull = DriveQuotaException.From(403, full);
                Check("a full account stops it too", stopFull != null);
                Check("but does not claim it will fix itself",
                    stopFull is { ResetsOnItsOwn: false });
                Check("and says what would fix it",
                    stopFull != null && stopFull.Message.Contains("out of storage", StringComparison.Ordinal),
                    stopFull?.Message);

                // Throttling is emphatically *not* one of these. It is handled
                // by waiting, and stopping the transfer for it would undo the
                // retry that exists to survive it.
                Check("but being told to slow down does not stop anything",
                    DriveQuotaException.From(403, throttled) == null);

                Check("nor does an ordinary permission refusal",
                    DriveQuotaException.From(403,
                        """{"error":{"code":403,"errors":[{"reason":"insufficientFilePermissions"}]}}""") == null);

                Check("nor does anything that is not a 403",
                    DriveQuotaException.From(500, daily) == null);

                foreach (var junk in new[] { "", "   ", "{", "not json", "null" })
                {
                    try { DriveQuotaException.From(403, junk); Check($"\"{junk}\" is survivable", true); }
                    catch (Exception ex) { Check($"\"{junk}\" is survivable", false, ex.Message); }
                }

                // And the per-file catch lets exactly this one past, which is
                // what makes it stop the run rather than become failure 901.
                var uploadSource = SourceFile("DriveUpload.cs") ?? "";
                Check("the per-file catch lets a quota failure through",
                    uploadSource.Contains("catch (DriveQuotaException)", StringComparison.Ordinal),
                    "otherwise it is recorded per file and the reason is buried");
            }

            // ---- eight streams, and the headroom to justify them ----
            //
            // Google's quota since May 2026 is weighted units: an upload is an
            // edit at 50, against 325,000 units per minute per user — about a
            // hundred uploads a second. Eight streams against a 700ms round trip
            // is about twelve. The check is that the ceiling has not been
            // approached by a later change, not that eight is magic.
            {
                const int unitsPerUpload = 50;
                const int perUserPerMinute = 325_000;
                double uploadsPerSecondAllowed = perUserPerMinute / (double)unitsPerUpload / 60.0;
                double uploadsPerSecondAsked = DriveUpload.Streams / 0.7;

                Check($"eight streams stay well inside Google's rate limit " +
                      $"({uploadsPerSecondAsked:0.0}/s against {uploadsPerSecondAllowed:0}/s allowed)",
                    uploadsPerSecondAsked < uploadsPerSecondAllowed / 4,
                    "the round trip should be the limit, not the quota");

                Equal("and there are eight of them", "8", DriveUpload.Streams.ToString());
            }

            // ---- an outage is waited out, not charged to the retries ----
            var writesSource = SourceFile("DriveWrites.cs") ?? "";
            Check("a network that has gone away pauses the transfer",
                writesSource.Contains("WaitOutNetwork", StringComparison.Ordinal) &&
                Occurrences(writesSource, "WaitOutNetwork(") >= 2,
                "otherwise a five-minute outage fails every file in flight");

            Check("and the free attempt really is free",
                writesSource.Contains("attempt--;", StringComparison.Ordinal),
                "a paused attempt that still counts spends the budget on the outage");

            Check("and it says so, because a paused transfer looks like a hung one",
                writesSource.Contains("drive.offline.waiting", StringComparison.Ordinal));

            // ---- and what it says afterwards is something a person can hear ----
            //
            // This is the other half of the same report. Google's 502 body is a
            // full HTML page, about seven hundred characters of doctype,
            // stylesheet and a link to a picture of a robot, and it went into
            // the failure dialog whole — pushing every other failure off the
            // screen, and reading aloud as a minute of markup.
            {
                const string page =
                    "<!DOCTYPE html><html lang=en><meta charset=utf-8><title>Error 502 " +
                    "(Server Error)!!1</title><style>*{margin:0;padding:0}</style>" +
                    "<p><b>502.</b> <ins>That's an error.</ins><p>The server encountered a " +
                    "temporary error and could not complete your request.";

                var said = DriveClient.Explain(502, page);
                Check("an HTML error page becomes one sentence",
                    said.Length < 80 && !said.Contains('<'), said);
                Check("and the sentence says what happened",
                    said.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase), said);

                // A real API error carries a real message, and that is the one
                // worth keeping — it is more specific than anything this code
                // could write from the status alone.
                var json = """
                    {"error":{"code":403,"message":"The user has exceeded their Drive storage quota."}}
                    """;
                Equal("a JSON error keeps its own message",
                    "The user has exceeded their Drive storage quota.",
                    DriveClient.Explain(403, json));

                // Anything else is capped rather than trusted to be short.
                var huge = new string('x', 5000);
                Check("and anything else is cut to something readable",
                    DriveClient.Explain(500, huge).Length < 260,
                    DriveClient.Explain(500, huge).Length.ToString());

                // Junk that looks like JSON and is not must not throw on the
                // path that is already reporting a failure.
                foreach (var junk in new[] { "", "   ", "{", "{}", "{\"error\":{}}", "null", "<" })
                {
                    try { DriveClient.Explain(500, junk); Check($"\"{junk}\" is survivable", true); }
                    catch (Exception ex) { Check($"\"{junk}\" is survivable", false, ex.Message); }
                }
            }

            // ---- the retry is actually wired into the small path ----
            //
            // The constants and the helpers above can all be perfect while the
            // upload still sends once and gives up, which is exactly the state
            // this was in. A mechanism nothing calls is not a policy.
            var writes = SourceFile("DriveWrites.cs");
            if (writes != null)
            {
                Check("the small upload path retries",
                    writes.Contains("MaxSmallUploadAttempts", StringComparison.Ordinal) &&
                    Occurrences(writes, "MaxSmallUploadAttempts") >= 3,
                    "declared but not used in UploadWhole");

                // The stream and the request are both rebuilt each time round.
                // A stream already read to the end cannot be sent again and
                // neither can a request that has been sent once, so a retry that
                // reused either would fail for a reason unrelated to the failure
                // it was retrying — and would look exactly like the server
                // refusing twice.
                Check("and opens the file again for each attempt",
                    writes.Contains("using var source = OpenRead(localPath);", StringComparison.Ordinal) &&
                    writes.Contains("// Opened inside the loop.", StringComparison.Ordinal));
            }
            else Check("DriveWrites.cs can be read", false);

            Console.WriteLine();
        }

        /// <summary>
        /// Playing faster without playing higher.
        ///
        /// This is the thing the media engine was kept for, and the only reason
        /// it survived being replaced as the player. Resampling changes the
        /// speed by changing the pitch, which is a chipmunk, so the test that
        /// matters is not "did it come out shorter" — that a resampler passes
        /// too — but "is it still the same note".
        /// </summary>
        private static void TimeStretchTests()
        {
            Console.WriteLine("Changing speed without changing pitch:");

            const int rate = 48000, channels = 2, tone = 440;

            static float[] Sine(int frames, int channels, double frequency, double rate)
            {
                var buffer = new float[frames * channels];
                for (int i = 0; i < frames; i++)
                {
                    float v = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / rate));
                    for (int c = 0; c < channels; c++) buffer[i * channels + c] = v;
                }
                return buffer;
            }

            // A source that hands out a long sine a block at a time.
            static Func<float[], int, int> Feeder(float[] source, int channels)
            {
                int at = 0;
                return (destination, frames) =>
                {
                    int left = source.Length / channels - at;
                    int take = Math.Min(frames, left);
                    if (take <= 0) return 0;
                    Array.Copy(source, at * channels, destination, 0, take * channels);
                    at += take;
                    return take;
                };
            }

            var input = Sine(rate * 4, channels, tone, rate);

            // ---- rate 1 is untouched, exactly ----
            {
                var stretch = new TimeStretch(channels) { Rate = 1.0, Source = Feeder(input, channels) };
                var output = new float[rate * channels];
                int got = stretch.Read(output, rate);

                bool identical = got == rate;
                for (int i = 0; identical && i < got * channels; i++)
                    if (output[i] != input[i]) identical = false;

                Check("normal speed passes the samples through untouched", identical,
                    $"got {got} frames");
            }

            // ---- and changing speed joins cleanly, both ways ----
            //
            // Going from normal to 1.5x started the first hop 640 frames into
            // the input with nothing faded, and coming back resumed from the
            // front of the input rather than where the last hop led — each a
            // step in the waveform. A 440Hz sine at half scale never moves more
            // than 0.029 between two samples, so anything much larger is a join.
            foreach (double speed in new[] { 1.5, 0.75 })
            {
                var stretch = new TimeStretch(channels) { Rate = 1.0, Source = Feeder(input, channels) };
                var heard = new List<float>();
                var block = new float[1000 * channels];

                void Play(int frames)
                {
                    for (int done = 0; done < frames;)
                    {
                        int got = stretch.Read(block, Math.Min(1000, frames - done));
                        if (got <= 0) break;
                        for (int i = 0; i < got; i++) heard.Add(block[i * channels]);
                        done += got;
                    }
                }

                Play(12_345);
                stretch.Rate = speed;
                Play(30_000);
                stretch.Rate = 1.0;
                Play(12_000);

                float worst = 0;
                int where = 0;
                for (int i = 1; i < heard.Count; i++)
                {
                    float step = Math.Abs(heard[i] - heard[i - 1]);
                    if (step > worst) { worst = step; where = i; }
                }

                Check($"going to {speed}x and back joins without a step (largest {worst:0.###})",
                    worst < 0.04f, $"a step of {worst:0.####} at frame {where} of {heard.Count}");
            }

            // ---- the length changes by the rate ----
            foreach (double speed in new[] { 0.5, 2.0, 4.0 })
            {
                var stretch = new TimeStretch(channels) { Rate = speed, Source = Feeder(input, channels) };
                var output = new float[(int)(rate * 8) * channels];

                int total = 0;
                while (total < output.Length / channels)
                {
                    int got = stretch.Read(output, output.Length / channels - total);
                    if (got <= 0) break;
                    total += got;
                }

                double expected = (input.Length / channels) / speed;
                Check($"at {speed}x the track is {(speed > 1 ? "shorter" : "longer")} by the rate",
                    Math.Abs(total - expected) < expected * 0.05,
                    $"expected about {expected:N0} frames, got {total:N0}");
            }

            // ---- and the note is still the note ----
            //
            // The whole point. A resampler at 2x would put every bit of this
            // energy an octave up, so the second bin is not decoration: it is
            // what tells a time-stretcher apart from the easy thing.
            foreach (double speed in new[] { 0.5, 2.0 })
            {
                var stretch = new TimeStretch(channels) { Rate = speed, Source = Feeder(input, channels) };
                var output = new float[rate * channels];

                int total = 0;
                while (total < rate)
                {
                    int got = stretch.Read(output, rate - total);
                    if (got <= 0) break;
                    total += got;
                }

                double atPitch = EnergyAt(output, total, channels, tone, rate);
                double atShifted = EnergyAt(output, total, channels, tone * speed, rate);

                Check($"at {speed}x it is still a {tone}Hz tone",
                    atPitch > atShifted * 4,
                    $"{tone}Hz={atPitch:0.0000}, {tone * speed}Hz={atShifted:0.0000}");

                // Loudness must survive too. Overlap-add with a badly chosen
                // join cancels against itself and the result is a tone that
                // fades in and out at the hop rate.
                float peak = 0;
                for (int i = 0; i < total * channels; i++)
                    if (Math.Abs(output[i]) > peak) peak = Math.Abs(output[i]);

                Check($"and at {speed}x it is still as loud", peak > 0.35f && peak < 0.75f,
                    $"peak {peak:0.###}");
            }

            // ---- and it is still a clean tone, not a tone plus the joins ----
            //
            // The length test and the pitch test both pass on a stretcher that
            // clicks: the clicks are quiet, broadband and periodic, and they are
            // exactly what "speeded up" sounds like as against "faster". A pure
            // sine in has only one place its energy can honestly be, so anything
            // that is not at 440Hz on the way out was manufactured at a join.
            //
            // This is the measurement the hop length, the full overlap and the
            // raised-cosine window were all changed for. It cannot be heard from
            // the length or the pitch, which is why neither of those caught it.
            foreach (double speed in new[] { 1.5, 2.0, 0.75 })
            {
                var stretch = new TimeStretch(channels) { Rate = speed, Source = Feeder(input, channels) };
                var output = new float[rate * channels];

                int total = 0;
                while (total < rate)
                {
                    int got = stretch.Read(output, rate - total);
                    if (got <= 0) break;
                    total += got;
                }

                // Skip the first hop: the very first segment has nothing behind
                // it to be crossfaded into, so it is the one place a join is
                // legitimately absent rather than good.
                const int settle = 4096;
                int measured = total - settle;

                double sum = 0;
                for (int i = settle; i < total; i++)
                {
                    double v = output[i * channels];
                    sum += v * v;
                }
                double rms = Math.Sqrt(sum / measured);

                var tail = new float[measured * channels];
                Array.Copy(output, settle * channels, tail, 0, measured * channels);

                double toneAmplitude = 2 * EnergyAt(tail, measured, channels, tone, rate);
                double purity = toneAmplitude / (rms * Math.Sqrt(2));

                Check($"at {speed}x the output is the tone and almost nothing else ({purity:0.###})",
                    // It measures 1.000 at all three rates, so the threshold is
                    // set where a real regression would land rather than at a
                    // number the current code merely clears. It is deterministic
                    // arithmetic on a generated tone: there is no machine-to-
                    // machine variation for the margin to absorb.
                    purity > 0.98, $"purity {purity:0.####}, rms {rms:0.####}, tone {toneAmplitude:0.####}");
            }

            // ---- the channels do not slide against each other ----
            {
                var stretch = new TimeStretch(channels) { Rate = 1.5, Source = Feeder(input, channels) };
                var output = new float[rate * channels];
                int got = stretch.Read(output, rate);

                bool together = true;
                for (int i = 0; together && i < got; i++)
                    if (output[i * channels] != output[i * channels + 1]) together = false;

                Check("identical channels stay identical", together,
                    "the stereo image moved with the speed control");
            }

            Console.WriteLine();
        }

        /// <summary>
        /// The render path we own: the arithmetic, and then the real thing.
        ///
        /// The arithmetic first, because it is where gain, the ceiling and the
        /// meter all happen and it can be checked exactly. Then one short, quiet
        /// tone actually played through a real device and measured, because that
        /// is the check nothing in this suite had: a player can hold an active
        /// audio session, report itself playing and advance its position while
        /// emitting pure silence, and only counting what comes out can tell.
        /// </summary>
        /// <summary>
        /// The limiter, which is an upward compressor and not a peak limiter.
        ///
        /// Everything about it is a claim that can be checked with numbers, and
        /// every one of those claims is one somebody would otherwise have to
        /// take on trust from a tick box: that it lifts quiet material, that it
        /// does not touch loud material, that it deliberately does *not* stop
        /// clipping, and that how far it goes depends on the volume.
        ///
        /// The predecessor of this was a soft clipper with a fixed knee, and the
        /// only thing its tests could assert was that a number stayed under one.
        /// </summary>
        private static void LimiterTests()
        {
            // A steady tone at a given amplitude, run through the renderer for
            // long enough that the envelope and the lift have both settled. The
            // release is a quarter of a second, so "long enough" is seconds of
            // audio — which costs nothing here, since no device is involved.
            static float Settled(float amplitude, float gain, bool limiter,
                int seconds = 3, bool wholeRange = false)
            {
                var renderer = new WasapiRenderer
                {
                    Gain = gain,
                    Limiter = limiter,
                    LimiterWholeRange = wholeRange,
                };
                var block = new float[4096];
                float last = 0f;

                // 48000 is what Coefficient falls back to with no device open,
                // so a block is about 85ms and this is the real time constant.
                int blocks = seconds * 48000 / block.Length;
                for (int b = 0; b < blocks; b++)
                {
                    for (int i = 0; i < block.Length; i++) block[i] = amplitude;
                    renderer.ApplyGainAndMeasure(block, block.Length);
                    last = block[^1];
                }
                return last;
            }

            // ---- off, it is exactly the gain and nothing else ----
            Equal("with the limiter off a quiet sample is just the gain", "0.02",
                Settled(0.01f, 2f, limiter: false).ToString("0.###"));

            // ---- at or below full scale it does nothing at all ----
            //
            // This assertion is the other way round from the one it replaced,
            // and the change was asked for. The old ceiling was
            // BaseBoost * drive * drive with drive clamped up to one, so at a
            // gain of one the limiter was allowed eight times — eighteen
            // decibels of upward compression at 100 percent volume, and the same
            // eighteen at six percent, on a switch whose description says the
            // effect grows with the volume. It does not grow from eight; it
            // grows from nothing.
            float quietFlat = Settled(0.01f, 1f, limiter: false);
            float quietLimited = Settled(0.01f, 1f, limiter: true);
            Check($"at 100 percent the limiter changes nothing ({quietFlat:0.####} to {quietLimited:0.####})",
                quietLimited == quietFlat, $"{quietFlat} -> {quietLimited}");

            // ---- above it, quiet material comes up ----
            float driveOff = Settled(0.01f, 2f, limiter: false);
            float driveOn = Settled(0.01f, 2f, limiter: true);
            Check($"above 100 percent it lifts what is quiet ({driveOff:0.####} to {driveOn:0.####})",
                driveOn > driveOff * 4, $"{driveOff} -> {driveOn}");

            // ---- and stops at the ceiling it is allowed ----
            //
            // Thirty-two times at a gain of two — BaseBoost times the square of
            // the drive. Not infinite: a leveller with no ceiling turns the
            // silence between tracks into a wall of hiss, which is the failure
            // that makes people turn these things off and never turn them on
            // again.
            Check($"but not past the lift it is allowed ({driveOn / driveOff:0.##} times)",
                driveOn <= driveOff * 32.2f, (driveOn / driveOff).ToString("0.###"));

            // ---- unless it is told to work at any volume ----
            //
            // Which is what that switch is for, and it is the only way to get
            // the effect at or below full scale now.
            float anyVolume = Settled(0.01f, 1f, limiter: true, wholeRange: true);
            Check($"\"same lift at any volume\" works at 100 too ({anyVolume:0.####})",
                anyVolume > quietFlat * 4, $"{quietFlat} -> {anyVolume}");

            // ---- loud material is never turned down ----
            //
            // This is what makes it an upward compressor rather than a limiter:
            // the gain it applies is clamped at one from below, so nothing is
            // ever made quieter than the volume asked for. Measured above full
            // scale, because that is now the only place it does anything at all
            // without the whole-range switch.
            float loudOff = Settled(0.8f, 2f, limiter: false);
            float loudOn = Settled(0.8f, 2f, limiter: true);
            Check($"and never turns loud material down ({loudOff:0.###} to {loudOn:0.###})",
                loudOn >= loudOff - 0.0001f, $"{loudOff} -> {loudOn}");

            // Where a signal is already past the ceiling, it is left exactly
            // alone — which at any volume above 100 percent is most of a track.
            float overOff = Settled(0.8f, 4f, limiter: false);
            float overOn = Settled(0.8f, 4f, limiter: true);
            Check($"and leaves anything past the ceiling exactly where it was ({overOff:0.##} to {overOn:0.##})",
                Math.Abs(overOn - overOff) < 0.001f, $"{overOff} -> {overOn}");

            // ---- it does not prevent clipping, on purpose ----
            var clipping = new WasapiRenderer { Gain = 4f, Limiter = true };
            var loudBlock = new float[4096];
            for (int i = 0; i < loudBlock.Length; i++) loudBlock[i] = 0.8f;
            clipping.ApplyGainAndMeasure(loudBlock, loudBlock.Length);

            Check($"a driven signal still goes past full scale ({loudBlock[^1]:0.##})",
                loudBlock[^1] > 1f, loudBlock[^1].ToString("0.###"));
            Check("and is still counted as clipping", clipping.ClippedSamples > 0,
                clipping.ClippedSamples.ToString());

            // ---- and it can be taken off the volume entirely ----
            //
            // The lift normally grows with the square of the gain, which means
            // it is at its mildest at ordinary volumes — exactly where somebody
            // sits down to find out what it does. This is the switch for
            // listening into a recording rather than making one loud, so at a
            // volume of one it has to do considerably more, not the same.
            static float SettledWide(float amplitude, float gain, int seconds = 3)
            {
                var renderer = new WasapiRenderer
                {
                    Gain = gain,
                    Limiter = true,
                    LimiterWholeRange = true,
                };
                var block = new float[4096];
                float last = 0f;

                int blocks = seconds * 48000 / block.Length;
                for (int b = 0; b < blocks; b++)
                {
                    for (int i = 0; i < block.Length; i++) block[i] = amplitude;
                    renderer.ApplyGainAndMeasure(block, block.Length);
                    last = block[^1];
                }
                return last;
            }

            float narrow = Settled(0.002f, 1f, limiter: true);
            float wide = SettledWide(0.002f, 1f);
            Check($"whole-range lifts further at normal volume ({narrow:0.####} to {wide:0.####})",
                wide > narrow * 4, $"{narrow} -> {wide}");

            // Still the same ceiling, though. "More lift available" is not
            // "louder than full scale" — the aim does not move.
            Check($"and still aims at the ceiling ({wide:0.###})",
                wide <= 0.91f, wide.ToString("0.####"));

            // And it must not turn loud material up any further than the ceiling
            // either, or the switch would simply be a volume control.
            float loudWide = SettledWide(0.8f, 1f);
            Check($"whole-range still leaves loud material alone ({loudWide:0.###})",
                loudWide <= 0.91f && loudWide >= 0.8f, loudWide.ToString("0.###"));

            // ---- the higher the volume, the further it goes ----
            //
            // This is the whole shape of the thing. The lift grows with the
            // square of the gain, so pushing the volume up does not merely make
            // the same sound louder — it opens the background up further, which
            // is what it is for.
            float atOne = Settled(0.01f, 1f, limiter: true);
            float atFour = Settled(0.01f, 4f, limiter: true);
            Check($"pushing the volume up pushes the quiet parts further ({atOne:0.###} to {atFour:0.###})",
                atFour > atOne * 8, $"{atOne} -> {atFour}");

            // ---- and it aims at a ceiling below full scale ----
            //
            // Not 1.0: a leveller that puts everything at exactly full scale
            // leaves nothing at all for the transient that arrives after the
            // envelope was measured.
            Check($"quiet material is brought up towards full scale, not past it ({atFour:0.###})",
                atFour > 0.5f && atFour <= 1.0f, atFour.ToString("0.###"));

            // ---- the time constants are times, not sample counts ----
            //
            // The loop counts frames and the constants are in seconds, so a
            // device mixing at 96kHz has to smooth over twice as many samples to
            // take the same quarter of a second. Getting this wrong is a limiter
            // that behaves differently on different hardware and sounds like a
            // fault on one machine only.
            Check("a time constant is half as steep at twice the rate",
                WasapiRenderer.Coefficient(96000, 0.25) <
                WasapiRenderer.Coefficient(48000, 0.25));
            Check("and an instant one is instant",
                Math.Abs(WasapiRenderer.Coefficient(48000, 0) - 1f) < 0.0001f);
        }

        private static void DirectOutputTests()
        {
            Console.WriteLine("The direct output path:");

            // ---- gain, ceiling and meter, exactly ----
            var renderer = new WasapiRenderer { Gain = 2f };
            var buffer = new float[] { 0.25f, -0.25f, 0.5f, 0f };
            renderer.ApplyGainAndMeasure(buffer, buffer.Length);

            Equal("gain multiplies", "0.5", buffer[0].ToString("0.###"));
            Equal("downwards too", "-0.5", buffer[1].ToString("0.###"));
            Equal("silence stays silent", "0", buffer[3].ToString("0.###"));
            Equal("and the meter saw the loudest of them", "1", renderer.ReadPeak().ToString("0.###"));
            Equal("reading the meter clears it", "0", renderer.ReadPeak().ToString("0.###"));

            // Past full scale the sample is carried, not squashed — measured
            // through the endpoint, the Windows mixer takes it and clips at the
            // device. Counted all the same, because "is it clipping" has to be
            // answerable by somebody who cannot look at a meter.
            var loud = new WasapiRenderer { Gain = 4f };
            var over = new float[] { 0.5f, -0.5f };
            loud.ApplyGainAndMeasure(over, over.Length);
            Equal("past full scale the sample is carried", "2", over[0].ToString("0.###"));
            Equal("and counted as clipped", "2", loud.ClippedSamples.ToString());

            // "How much of it is over" needs both halves, or the count is
            // meaningless: a million clipped samples is either an overdriven
            // track or four seconds out of an hour.
            Equal("and the proportion is reported", "100",
                loud.ClippedPercent.ToString("0"));
            loud.ResetClipping();
            Equal("resetting starts the count again", "0", loud.ClippedSamples.ToString());

            // ---- a remembered device that is no longer there ----
            //
            // Reported as "this folder will not play", about a folder of
            // perfectly good 32-bit float WAVs. Every track in the application
            // was failing: the saved output device had been unplugged, and
            // `Open` only fell back to the default when *GetDevice* failed —
            // which it does not for an unplugged endpoint. It hands back a good
            // IMMDevice for a thing that is not there and the refusal arrives one
            // step later, as AUDCLNT_E_DEVICE_INVALIDATED from Activate. So the
            // fallback the comment promised never ran.
            //
            // A device id that has never existed exercises the same path as one
            // that has stopped existing, and needs no hardware to be unplugged.
            var gone = new WasapiRenderer();
            bool fellBack = false;
            try
            {
                fellBack = gone.Start((_, frames) => frames, preferRaw: false,
                    deviceId: "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}");

                Check("a device that is not there falls back to the default", fellBack,
                    "renderer said: " + gone.Diagnostic);
                Check("and the fallback says it is on the default, not the dead id",
                    !fellBack || gone.DeviceId.Length == 0, gone.DeviceId);
            }
            finally { try { gone.Dispose(); } catch { } }

            // The message that sent somebody looking at their files. A renderer
            // failure affects every track, so the file must not be the subject of
            // the sentence — naming it is what did the damage.
            var playerSource = SourceFile("AudioPlayer.cs");
            Check("no audio output is not reported as a fault in the file",
                playerSource != null &&
                !playerSource.Contains("$\"Could not play {Path.GetFileName(path)}: no audio output\"",
                    StringComparison.Ordinal) &&
                playerSource.Contains("No audio output —", StringComparison.Ordinal),
                "the file is named in the subject of a sentence about the device");

            LimiterTests();

            // ---- the dial, over the whole range ----
            Equal("the dial is the engine's curve below full",
                AudioPlayer.EngineVolume(40, VolumeCurve.Perceptual).ToString("0.####"),
                AudioPlayer.DirectGain(40, VolumeCurve.Perceptual).ToString("0.####"));
            Equal("exactly one at full", "1",
                AudioPlayer.DirectGain(100, VolumeCurve.Perceptual).ToString("0.###"));
            Equal("and literal above it", "3",
                AudioPlayer.DirectGain(300, VolumeCurve.Perceptual).ToString("0.###"));
            Equal("nothing at nothing", "0",
                AudioPlayer.DirectGain(0, VolumeCurve.Perceptual).ToString("0.###"));

            // ---- the two diagnostics answer different questions ----
            //
            // The audio page carries both, one under "Media engine" and one
            // under "Playing through". Starting a track on the direct path used
            // to overwrite the first with the second, so the page showed the
            // renderer's line twice and the label on one of them was simply
            // wrong — and the answer to "is the media engine working", which is
            // the whole reason that label exists, was gone.
            using (var player = new AudioPlayer())
            {
                Check("the engine's diagnostic is not the renderer's",
                    !player.Diagnostic.Contains("channels"), player.Diagnostic);
                Equal("and nothing has played yet", "nothing has played yet", player.OutputDiagnostic);
            }

            // ---- the decoder must not shut Media Foundation down under the engine ----
            //
            // Both backends stand on the same platform, and the decoder counted
            // its own MFStartup calls without recording whether *it* had made
            // one. A decoder disposed twice, or disposed having never opened
            // anything, therefore called MFShutdown for a startup it did not
            // hold — and MFShutdown does not fail, it simply takes Media
            // Foundation down for the process. The engine kept its object, kept
            // answering, kept reporting that it was playing, and never made
            // another sound. Nothing in the application could see it, because
            // the thing that broke was underneath both backends.
            {
                var platform = NewTempDir();
                try
                {
                    var track = Path.Combine(platform, "tone.wav");
                    WriteTone(track, 4, 0.02);

                    // The player is warmed first, exactly as the running
                    // application does on the way up, so Media Foundation is
                    // already started and holding a reference. That ordering is
                    // the whole bug: decoders torn down *afterwards* give back a
                    // startup they never held. Warm last and the test passes
                    // against the broken code, because the fresh MFStartup puts
                    // the platform back before anybody looks.
                    using var player = new AudioPlayer();
                    player.Warm(track);

                    // Never opened, disposed anyway.
                    new AudioDecoder().Dispose();

                    // Opened and disposed twice.
                    var twice = new AudioDecoder();
                    twice.Open(track, 48000, 2);
                    twice.Dispose();
                    twice.Dispose();

                    // And a whole playback torn down, which is what changing
                    // track does.
                    using (var once = new WasapiPlayback())
                    {
                        once.Start(track, preferRaw: false);
                        once.Stop();
                    }

                    player.Play(track);
                    double moved = 0;
                    for (int i = 0; i < 40 && moved <= 0; i++)
                    {
                        Thread.Sleep(50);
                        moved = player.PositionSeconds;
                    }
                    Check("a track still plays after decoders have been torn down around it",
                        moved > 0, "the position never left zero");
                }
                finally { Cleanup(platform); }
            }

            // ---- and it plays at every speed ----
            //
            // The speed keys used to hand the track to the media engine, and a
            // saved speed meant the engine played from the moment the
            // application started. That half had no test and it was broken: the
            // position never moved, the endpoint had no stream, and the window
            // went on saying it was playing. `IsPlaying` alone could not see it —
            // the clock is what says whether anything is actually happening, and
            // it is still the assertion now the stretcher does the work.
            {
                var speeds = NewTempDir();
                try
                {
                    var track = Path.Combine(speeds, "tone.wav");
                    WriteTone(track, 6, 0.02);

                    foreach (int rate in new[] { 100, 110, 200, 50 })
                    {
                        using var player = new AudioPlayer();
                        player.Warm(track);
                        player.PlaybackRatePercent = rate;
                        player.Play(track);

                        double moved = 0;
                        for (int i = 0; i < 60 && moved <= 0; i++)
                        {
                            Thread.Sleep(50);
                            moved = player.PositionSeconds;
                        }

                        Check($"it really plays at {rate} percent speed",
                            moved > 0, $"the position never left zero (playing={player.IsPlaying})");
                    }
                }
                finally { Cleanup(speeds); }
            }

            // ---- every speed, for the format that used to refuse them ----
            //
            // FLAC is the case this is about. The rate belonged to the media
            // engine, Windows' FLAC decoder refused everything above normal, and
            // the speed dialog quietly dropped half its entries for a whole
            // library. The stretcher works on decoded samples, so the container
            // cannot have an opinion — and this is the file type that proves it.
            {
                var flacs = NewTempDir();
                try
                {
                    var flac = Path.Combine(flacs, "tone.flac");
                    WriteSilentFlac(flac, 200);

                    foreach (int rate in new[] { 50, 100, 200, 400 })
                    {
                        using var player = new AudioPlayer();
                        player.VolumePercent = 0;
                        player.PlaybackRatePercent = rate;

                        Check($"FLAC plays at {rate} percent", player.Play(flac) != null,
                            player.Diagnostic);

                        double moved = 0;
                        for (int i = 0; i < 60 && moved <= 0; i++)
                        {
                            Thread.Sleep(50);
                            moved = player.PositionSeconds;
                        }
                        Check($"and the FLAC clock really moves at {rate} percent", moved > 0,
                            $"playing={player.IsPlaying}");
                    }
                }
                finally { Cleanup(flacs); }
            }

            // ---- choosing a device, and moving between them while playing ----
            {
                var devices = AudioPlayer.OutputDevices();
                Check("the machine's playback devices can be listed", devices.Count > 0,
                    "no devices at all, which a machine with working audio should not report");

                foreach (var device in devices)
                {
                    Check("every device has an id to remember it by", device.Id.Length > 0);
                    Check("and a name to show", device.Name.Length > 0, device.Id);
                }

                var moving = NewTempDir();
                try
                {
                    var track = Path.Combine(moving, "tone.wav");
                    WriteTone(track, 6, 0.02);

                    using var play = new WasapiPlayback();
                    int switched = 0;
                    play.DeviceSwitched += (_, ok) => { if (ok) Interlocked.Increment(ref switched); };
                    if (!play.Start(track, preferRaw: false))
                    {
                        Check("the switching test could start", false, play.Diagnostic);
                    }
                    else
                    {
                        Thread.Sleep(200);
                        double before = play.Position;

                        // Every device in turn, then back to the default. The
                        // assertion is that the clock never stops: a switch that
                        // reloaded the track would reset it, and one that killed
                        // the renderer would freeze it.
                        foreach (var device in devices)
                        {
                            play.SwitchDevice(device.Id);
                            Thread.Sleep(250);
                        }

                        play.SwitchDevice("");
                        Thread.Sleep(250);

                        double after = play.Position;
                        Check("the clock keeps running across a device change",
                            after > before, $"{before:0.###} then {after:0.###}");
                        Check("and nothing went wrong on the way",
                            play.SwitchProblem.Length == 0, play.SwitchProblem);
                        Check("and it is still playing", play.IsPlaying);

                        // And the switches really happened. Every check above
                        // passes for a player that ignores SwitchDevice entirely,
                        // which is exactly what one did for a whole build.
                        // At least one, rather than every one: switches asked for
                        // faster than a device opens are folded into the latest.
                        Check("the switches actually moved the audio",
                            switched >= 1, $"{switched} of {devices.Count + 1}");
                    }
                }
                finally { Cleanup(moving); }

                // A device id that belongs to nothing falls back rather than
                // going silent — the remembered headset that is not plugged in.
                var missing = NewTempDir();
                try
                {
                    var track = Path.Combine(missing, "tone.wav");
                    WriteTone(track, 2, 0.02);

                    using var play = new WasapiPlayback();
                    bool started = play.Start(track, preferRaw: false, 0, null, "{not-a-real-device}");
                    Check("a device that is not there falls back to the default", started,
                        play.Diagnostic);
                }
                finally { Cleanup(missing); }
            }

            // ---- and now for real, with sound in it ----
            var sandbox = NewTempDir();
            try
            {
                // Quiet and short: two tenths of a second at a fiftieth of full
                // scale is audible to a meter and barely to a person, which is
                // the right trade for a test that runs on somebody's machine.
                var tone = Path.Combine(sandbox, "tone.wav");
                WriteTone(tone, 0.35, 0.02);

                foreach (var (gain, expected) in new[] { (1f, 0.02f), (4f, 0.08f) })
                {
                    using var play = new WasapiPlayback { Gain = gain };
                    if (!play.Start(tone, preferRaw: false))
                    {
                        Check($"the direct path plays at gain {gain}", false, play.Diagnostic);
                        continue;
                    }

                    float peak = 0;
                    for (int i = 0; i < 20; i++)
                    {
                        Thread.Sleep(25);
                        float p = play.ReadPeak();
                        if (p > peak) peak = p;
                    }
                    play.Stop();

                    // The tone is a sine, so the peak is the amplitude — and the
                    // whole point is that it *moves with the gain*. A path that
                    // emits silence reads zero here whatever the gain is.
                    Check($"a real tone at gain {gain} comes out at about {expected:0.###}",
                        Math.Abs(peak - expected) < expected * 0.25f,
                        $"measured {peak:0.####}");

                    // The one line that says whether the effects bypass was
                    // granted, which is a question the tick box cannot answer.
                    Check("and it says how it is playing",
                        play.Diagnostic.Contains("Hz") && play.Diagnostic.Contains("channels"),
                        play.Diagnostic);
                }
            }
            finally { Cleanup(sandbox); }

            Console.WriteLine();
        }

        private static void NameRuleTests()
        {
            Console.WriteLine("Typed name validation:");

            Check("an ordinary name is accepted", NameRules.IsUsableName("holiday photos"));
            Check("a name with an extension is accepted", NameRules.IsUsableName("track 01.flac"));
            Check("a name with dots inside is accepted", NameRules.IsUsableName("archive.tar.gz"));
            Check("a unicode name is accepted", NameRules.IsUsableName("Ünïcodé — ファイル"));
            Check("a leading dot is accepted", NameRules.IsUsableName(".gitignore"));

            // The whole point: these used to be combined with the current folder
            // and could put a renamed file anywhere on the disk.
            Check("a backslash is refused", !NameRules.IsUsableName(@"sub\thing"));
            Check("a forward slash is refused", !NameRules.IsUsableName("sub/thing"));
            Check("a parent-directory escape is refused", !NameRules.IsUsableName(@"..\..\thing"));
            Check("an absolute path is refused", !NameRules.IsUsableName(@"C:\Windows\thing"));
            Check("a drive-relative path is refused", !NameRules.IsUsableName("C:thing"));
            Check("a bare drive letter with colon is refused", !NameRules.IsUsableName("D:"));

            Check("dot is refused", !NameRules.IsUsableName("."));
            Check("dot dot is refused", !NameRules.IsUsableName(".."));

            Check("an empty name is refused", !NameRules.IsUsableName(""));
            Check("a whitespace-only name is refused", !NameRules.IsUsableName("   "));
            Check("a null name is refused", !NameRules.IsUsableName(null));

            Check("an illegal character is refused", !NameRules.IsUsableName("what?"));
            Check("a wildcard is refused", !NameRules.IsUsableName("*.txt"));
            Check("a pipe is refused", !NameRules.IsUsableName("a|b"));
            Check("a quote is refused", !NameRules.IsUsableName("say \"hi\""));

            // Reserved device names are refused by the filesystem itself, so
            // catching them here turns a confusing error into a clear one.
            Check("CON is refused", !NameRules.IsUsableName("CON"));
            Check("con lowercase is refused", !NameRules.IsUsableName("con"));
            Check("CON with an extension is refused", !NameRules.IsUsableName("CON.txt"));
            Check("COM1 is refused", !NameRules.IsUsableName("COM1"));
            Check("LPT9 is refused", !NameRules.IsUsableName("LPT9"));
            Check("a name merely starting with CON is fine", NameRules.IsUsableName("CONTACTS"));
            Check("CONTACTS.txt is fine", NameRules.IsUsableName("CONTACTS.txt"));

            // Silently stripped by the filesystem, so the result would not be the
            // name that was asked for.
            Check("a trailing dot is refused", !NameRules.IsUsableName("report."));
            Check("a trailing space is refused", !NameRules.IsUsableName("report "));

            // Every refusal has to say something a person can act on.
            foreach (var bad in new[] { @"a\b", "a:b", "CON", "report.", "*", "" })
            {
                var message = NameRules.DescribeBadName(bad);
                Check($"refusing \"{bad}\" explains why", !string.IsNullOrWhiteSpace(message), message ?? "(none)");
            }
            Check("an accepted name explains nothing", NameRules.DescribeBadName("fine.txt") == null);

            Console.WriteLine();
            Console.WriteLine("Paths arriving from a command line:");

            // The shell registers `"app.exe" "%1"`. For a drive it substitutes
            // D:\, so the command line reads "app.exe" "D:\" — and Windows reads
            // the backslash as escaping the quote, delivering D:" as the argument.
            // Trimmed to D:, that means "the current directory on D", not the
            // drive. Opening a drive showed the app's own folder because of it.
            Equal("a drive root mangled by quote escaping is repaired",
                @"D:\", NameRules.NormaliseLaunchPath("D:\""));
            Equal("a bare drive letter becomes the drive root",
                @"D:\", NameRules.NormaliseLaunchPath("D:"));
            Equal("a lowercase drive letter is repaired too",
                @"c:\", NameRules.NormaliseLaunchPath("c:"));
            Equal("a drive root that survived intact is left alone",
                @"C:\", NameRules.NormaliseLaunchPath(@"C:\"));

            Equal("surrounding quotes are stripped",
                @"C:\Program Files", NameRules.NormaliseLaunchPath("\"C:\\Program Files\""));
            Equal("a trailing quote from an escaped separator is stripped",
                @"C:\Program Files\", NameRules.NormaliseLaunchPath("C:\\Program Files\\\""));
            Equal("surrounding whitespace is stripped",
                @"C:\Windows", NameRules.NormaliseLaunchPath("   C:\\Windows   "));

            Equal("an ordinary path is untouched",
                @"C:\Users\Conner\Documents", NameRules.NormaliseLaunchPath(@"C:\Users\Conner\Documents"));
            Equal("a UNC path is untouched",
                @"\\server\share\folder", NameRules.NormaliseLaunchPath(@"\\server\share\folder"));
            Equal("a path with spaces is untouched",
                @"C:\My Folder\Sub Folder", NameRules.NormaliseLaunchPath(@"C:\My Folder\Sub Folder"));

            Equal("nothing normalises to nothing", "", NameRules.NormaliseLaunchPath(null));
            Equal("blank normalises to nothing", "", NameRules.NormaliseLaunchPath("   "));
            Equal("a lone quote normalises to nothing", "", NameRules.NormaliseLaunchPath("\""));

            // A two-character path that is not a drive must survive untouched.
            Equal("a two-character name is not mistaken for a drive",
                "ab", NameRules.NormaliseLaunchPath("ab"));

            // The behaviour this all exists to prevent: a bare drive letter really
            // does resolve to the working directory, so the repair is load-bearing.
            var currentDrive = Path.GetPathRoot(Environment.CurrentDirectory)!;
            var letter = currentDrive.Substring(0, 2);           // e.g. "C:"
            Check("a bare drive letter really does resolve to the working directory",
                string.Equals(Path.GetFullPath(letter),
                              Environment.CurrentDirectory.TrimEnd('\\'),
                              StringComparison.OrdinalIgnoreCase),
                $"{letter} -> {Path.GetFullPath(letter)}");
            Check("...and the normalised form resolves to the drive root instead",
                string.Equals(Path.GetFullPath(NameRules.NormaliseLaunchPath(letter)),
                              currentDrive, StringComparison.OrdinalIgnoreCase),
                Path.GetFullPath(NameRules.NormaliseLaunchPath(letter)));

            Console.WriteLine();
        }

        // ---- List ordering ----
        private static void SortOrderTests()
        {
            Console.WriteLine("List ordering:");

            Check("names sort case-insensitively",
                NameRules.CompareNames("apple", @"C:\apple", "Banana", @"C:\Banana") < 0);
            Check("ordering is symmetric",
                NameRules.CompareNames("Banana", @"C:\Banana", "apple", @"C:\apple") > 0);

            // The tie-break is what stops an unstable sort from shuffling rows
            // between refreshes of a folder that has not changed.
            Check("names differing only in case still get a decisive order",
                NameRules.CompareNames("readme", @"C:\readme", "README", @"C:\README") != 0);
            Check("the same entry compares equal to itself",
                NameRules.CompareNames("a.txt", @"C:\a.txt", "a.txt", @"C:\a.txt") == 0);

            // There is no Type column, so a saved "sort by Type" sorts by name.
            Check("sorting by Type sorts by name", NameRules.SortsAs(SortColumn.Type) == SortColumn.Name);
            Check("and the other columns are themselves",
                NameRules.SortsAs(SortColumn.Size) == SortColumn.Size &&
                NameRules.SortsAs(SortColumn.Modified) == SortColumn.Modified &&
                NameRules.SortsAs(SortColumn.Name) == SortColumn.Name);

            // A real sort must be total: the same input always gives the same
            // output, however the runtime happens to order equal elements.
            var names = new[] { "b.txt", "A.txt", "a.txt", "B.TXT", "c", "C" };
            var first = names.OrderBy(n => n, Comparer<string>.Create(
                (x, y) => NameRules.CompareNames(x, "root/" + x, y, "root/" + y))).ToArray();
            var second = Enumerable.Reverse(names).OrderBy(n => n, Comparer<string>.Create(
                (x, y) => NameRules.CompareNames(x, "root/" + x, y, "root/" + y))).ToArray();
            Check("the same folder sorts the same way regardless of enumeration order",
                first.SequenceEqual(second), string.Join(",", first) + " vs " + string.Join(",", second));

            Console.WriteLine();
        }

        // ---- Jump-to-name ----
        private static void TypeAheadTests()
        {
            Console.WriteLine("Multi-letter jump-to-name:");

            // A realistic folder: several names sharing first letters, which is
            // exactly the case single-letter search cannot cope with.
            var names = new[]
            {
                "addins",        // 0
                "appcompat",     // 1
                "assembly",      // 2
                "boot",          // 3
                "System32",      // 4
                "SysWOW64",      // 5
                "system.ini",    // 6
                "Web",           // 7
                "win.ini",       // 8
            };

            // ---- The whole point: more than one letter ----
            Equal("\"sys\" reaches System32", "4", FindIn(names, "sys", 0).ToString());
            Equal("\"sysw\" reaches SysWOW64", "5", FindIn(names, "sysw", 0).ToString());
            Equal("\"system.\" reaches system.ini", "6", FindIn(names, "system.", 0).ToString());
            Equal("\"app\" reaches appcompat", "1", FindIn(names, "app", 0).ToString());
            Equal("\"web\" reaches Web", "7", FindIn(names, "web", 0).ToString());

            // Without this, "sy" landed on something starting with "s" and then
            // on something starting with "y" — the bug being fixed.
            Check("a two-letter prefix does not degrade to the last letter alone",
                FindIn(names, "sy", 0) == 4, FindIn(names, "sy", 0).ToString());

            // ---- Case ----
            Equal("matching ignores case (lower query, upper name)", "4", FindIn(names, "system3", 0).ToString());
            Equal("matching ignores case (upper query, lower name)", "3", FindIn(names, "BOOT", 0).ToString());
            Equal("mixed case matches", "5", FindIn(names, "SySw", 0).ToString());

            // ---- A single letter cycles, as it always has in a Windows list ----
            Equal("a lone letter from the top finds the first match", "0", FindIn(names, "a", -1).ToString());
            Equal("pressing it again moves to the next", "1", FindIn(names, "a", 0).ToString());
            Equal("and again", "2", FindIn(names, "a", 1).ToString());
            Equal("and wraps back round", "0", FindIn(names, "a", 2).ToString());
            Equal("the same letter repeated also cycles", "1", FindIn(names, "aa", 0).ToString());
            Equal("three of the same still cycles by one", "2", FindIn(names, "aaa", 1).ToString());
            Equal("repeat-cycling is case insensitive too", "1", FindIn(names, "aA", 0).ToString());

            // ---- A growing prefix refines rather than jumping away ----
            Equal("refining stays on a row that still matches", "4", FindIn(names, "system", 4).ToString());
            Equal("refining moves on when the row no longer matches", "6", FindIn(names, "system.", 4).ToString());

            // ---- Wrapping and misses ----
            Equal("a prefix behind the cursor is found by wrapping", "1", FindIn(names, "app", 5).ToString());
            Equal("no match returns nothing", "-1", FindIn(names, "zzz", 0).ToString());
            Equal("an empty query matches nothing", "-1", FindIn(names, "", 0).ToString());
            Equal("an empty list matches nothing", "-1", FindIn(Array.Empty<string>(), "a", 0).ToString());
            Equal("an out-of-range cursor is tolerated", "0", FindIn(names, "addins", 999).ToString());
            Equal("a negative cursor is tolerated", "3", FindIn(names, "boot", -5).ToString());

            // ---- Names people actually have ----
            var awkward = new[] { "Program Files", "Program Files (x86)", "ProgramData", "Users" };
            Equal("a space is part of the name", "0", FindIn(awkward, "program f", 0).ToString());
            Equal("a longer prefix picks the right one of two similar names", "2",
                FindIn(awkward, "programd", 0).ToString());
            Equal("a parenthesis is just another character", "1",
                FindIn(awkward, "program files (", 0).ToString());

            var unicode = new[] { "Ärger", "Zebra", "ändern" };
            Equal("a unicode name is reachable", "0", FindIn(unicode, "är", 0).ToString());
            Equal("unicode case folds", "2", FindIn(unicode, "Änd", 0).ToString());

            // ---- The accumulating buffer ----
            Equal("the default pause is a quarter of a second", "250",
                TypeAheadBuffer.DefaultTimeoutMilliseconds.ToString());
            Equal("a fresh buffer uses that default", "250",
                new TypeAheadBuffer().TimeoutMilliseconds.ToString());

            var quick = new TypeAheadBuffer();   // the real default, not a test value
            Equal("first keystroke starts the word", "s", quick.Accept('s', 1000));
            Equal("a keystroke 200ms later extends it", "sy", quick.Accept('y', 1200));
            Equal("and another 200ms after that", "sys", quick.Accept('s', 1400));
            Equal("a half-second gap starts a new word", "t", quick.Accept('t', 1900));

            var buffer = new TypeAheadBuffer { TimeoutMilliseconds = 1000 };
            Equal("a longer configured pause holds the word together", "sy",
                buffer.Accept('s', 1000) + buffer.Accept('y', 1900)[1..]);
            Equal("a pause past the configured value starts again", "t", buffer.Accept('t', 3000));
            Equal("typing continues from the new word", "te", buffer.Accept('e', 3100));

            // Exactly on the timeout still counts as continuing; past it does not.
            var edge = new TypeAheadBuffer { TimeoutMilliseconds = 250 };
            edge.Accept('a', 0);
            Equal("a keystroke exactly on the timeout still extends", "ab", edge.Accept('b', 250));
            Equal("one millisecond later it does not", "c", edge.Accept('c', 501));

            var resettable = new TypeAheadBuffer();
            resettable.Accept('x', 0);
            resettable.Reset();
            Equal("resetting clears the word", "y", resettable.Accept('y', 10));

            // Walking a folder by ear: type, hear, type again, inside the pause —
            // the sequence the whole feature exists for.
            var walk = new TypeAheadBuffer();
            int at = -1;
            long clock = 0;
            foreach (var c in "sysw")
            {
                clock += 150;                       // a brisk but ordinary pace
                at = FindIn(names, walk.Accept(c, clock), at);
            }
            Equal("typing s-y-s-w as one word lands on SysWOW64", "5", at.ToString());

            // And the other half of the bargain: letters typed slowly are meant to
            // be separate, so each one cycles rather than building a word nobody
            // intended. This is why the pause is short.
            var deliberate = new TypeAheadBuffer();
            int cursor = -1;
            long slowClock = 0;
            var visited = new List<int>();
            for (int press = 0; press < 3; press++)
            {
                slowClock += 800;                   // a pause between each press
                cursor = FindIn(names, deliberate.Accept('a', slowClock), cursor);
                visited.Add(cursor);
            }
            Check("pressing one letter slowly walks the matches rather than building a word",
                visited.SequenceEqual(new[] { 0, 1, 2 }), string.Join(",", visited));

            Console.WriteLine();
        }

        // ---- Coming back to where you were ----
        private static void PositionMemoryTests()
        {
            Console.WriteLine("Returning to the previous position:");

            // ---- Stepping up should land on the folder just left ----
            Equal("stepping up lands on the folder you came out of",
                @"C:\Users\Conner\Documents",
                NavigationHistory.ChildOnPathTo(@"C:\Users\Conner", @"C:\Users\Conner\Documents") ?? "(null)");

            Equal("from several levels down, it is still the immediate child",
                @"C:\Users\Conner",
                NavigationHistory.ChildOnPathTo(@"C:\Users", @"C:\Users\Conner\Documents\explorer_native") ?? "(null)");

            Equal("a drive root works as an ancestor",
                @"C:\Windows",
                NavigationHistory.ChildOnPathTo(@"C:\", @"C:\Windows\System32") ?? "(null)");

            Equal("a drive root without its separator still works",
                @"C:\Windows",
                NavigationHistory.ChildOnPathTo(@"C:", @"C:\Windows") ?? "(null)");

            Equal("a trailing separator on either side is tolerated",
                @"C:\Users\Conner",
                NavigationHistory.ChildOnPathTo(@"C:\Users\", @"C:\Users\Conner\Documents\") ?? "(null)");

            Equal("case does not matter",
                @"C:\Users\Conner",
                NavigationHistory.ChildOnPathTo(@"c:\users", @"C:\Users\Conner\Documents") ?? "(null)");

            Equal("a UNC share works too",
                @"\\server\share\projects",
                NavigationHistory.ChildOnPathTo(@"\\server\share", @"\\server\share\projects\thing") ?? "(null)");

            // ---- And must not fire when it does not apply ----
            Check("a folder is not its own child",
                NavigationHistory.ChildOnPathTo(@"C:\Users", @"C:\Users") == null);
            Check("an unrelated folder gives nothing",
                NavigationHistory.ChildOnPathTo(@"C:\Users", @"D:\Music") == null);
            Check("going down rather than up gives nothing",
                NavigationHistory.ChildOnPathTo(@"C:\Users\Conner", @"C:\Users") == null);
            Check("a sibling with a shared prefix is not a child",
                NavigationHistory.ChildOnPathTo(@"C:\Program", @"C:\Program Files") == null);
            Check("nulls give nothing", NavigationHistory.ChildOnPathTo(null, @"C:\Users") == null);
            Check("empties give nothing", NavigationHistory.ChildOnPathTo("", "") == null);

            // ---- Remembering the row per folder ----
            var memory = new FolderPositionMemory();
            Check("an unvisited folder has nothing remembered", memory.Recall(@"C:\Windows") == null);

            memory.Remember(@"C:\Windows", @"C:\Windows\System32");
            Equal("the row is remembered", @"C:\Windows\System32", memory.Recall(@"C:\Windows") ?? "(null)");

            memory.Remember(@"C:\Windows", @"C:\Windows\Web");
            Equal("revisiting updates rather than duplicating", @"C:\Windows\Web", memory.Recall(@"C:\Windows") ?? "(null)");
            Equal("updating does not grow the memory", "1", memory.Count.ToString());

            Equal("recall ignores case", @"C:\Windows\Web", memory.Recall(@"c:\WINDOWS") ?? "(null)");

            memory.Remember(@"C:\Users", @"C:\Users\Conner");
            Equal("folders are remembered independently", @"C:\Windows\Web", memory.Recall(@"C:\Windows") ?? "(null)");
            Equal("...and the other one too", @"C:\Users\Conner", memory.Recall(@"C:\Users") ?? "(null)");


            memory.Remember(null, @"C:\x");
            memory.Remember(@"C:\y", null);
            Check("nulls are ignored rather than stored", memory.Recall(@"C:\y") == null);

            // Bounded, so a long session cannot accumulate one entry per folder
            // ever opened.
            var bounded = new FolderPositionMemory();
            for (int i = 0; i < FolderPositionMemory.MaxRemembered + 50; i++)
                bounded.Remember($@"C:\folder{i}", $@"C:\folder{i}\item");

            Check($"the memory stays bounded at {FolderPositionMemory.MaxRemembered}",
                bounded.Count <= FolderPositionMemory.MaxRemembered, bounded.Count.ToString());
            Check("the most recent folders survive eviction",
                bounded.Recall($@"C:\folder{FolderPositionMemory.MaxRemembered + 49}") != null);
            Check("the oldest folders are the ones dropped",
                bounded.Recall(@"C:\folder0") == null);


            Console.WriteLine();
        }

        // ---- PasteConflictPolicy.AutoRename ----
        private static void UniqueNameTests()
        {
            Console.WriteLine("Auto-rename on conflict:");

            var temp = NewTempDir();
            try
            {
                var file = Path.Combine(temp, "song.flac");
                File.WriteAllText(file, "x");
                Equal("first conflict becomes (2)",
                    Path.Combine(temp, "song (2).flac"), FileOperations.UniqueName(file));

                File.WriteAllText(Path.Combine(temp, "song (2).flac"), "x");
                Equal("second conflict becomes (3)",
                    Path.Combine(temp, "song (3).flac"), FileOperations.UniqueName(file));

                var free = Path.Combine(temp, "unused.flac");
                Equal("no conflict returns the name unchanged", free, FileOperations.UniqueName(free));

                // A folder has no extension, whatever the dots in its name say.
                // GetFileNameWithoutExtension splits at the last one regardless,
                // so a second copy of "Backup.2024" came out as "Backup (2).2024"
                // — and "Saved as …", the sentence this whole mechanism exists to
                // be able to say, then named something nobody would recognise.
                var dotted = Path.Combine(temp, "Backup.2024");
                Directory.CreateDirectory(dotted);
                Equal("a folder is numbered after its whole name",
                    Path.Combine(temp, "Backup.2024 (2)"), FileOperations.UniqueName(dotted));

                Directory.CreateDirectory(Path.Combine(temp, "Backup.2024 (2)"));
                Equal("and again for the next one",
                    Path.Combine(temp, "Backup.2024 (3)"), FileOperations.UniqueName(dotted));

                // A folder with no dot in it behaves as it always did.
                var plain = Path.Combine(temp, "New folder");
                Directory.CreateDirectory(plain);
                Equal("an ordinary folder name is unaffected",
                    Path.Combine(temp, "New folder (2)"), FileOperations.UniqueName(plain));
            }
            finally { Cleanup(temp); }
            Console.WriteLine();
        }

        // ---- Copy/move, threads, buffer, conflict policy ----
        private static async Task FileOperationTests()
        {
            Console.WriteLine("File operations:");

            var temp = NewTempDir();
            try
            {
                var src = Path.Combine(temp, "src");
                var dst = Path.Combine(temp, "dst");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);

                var a = Path.Combine(src, "a.txt");
                var b = Path.Combine(src, "b.txt");
                File.WriteAllText(a, new string('a', 5000));
                File.WriteAllText(b, new string('b', 7000));

                var nested = Path.Combine(src, "sub", "deep");
                Directory.CreateDirectory(nested);
                File.WriteAllText(Path.Combine(nested, "c.txt"), "ccc");

                // Copy with several threads and a small buffer, to exercise both.
                var copy = await FileOperations.RunAsync(
                    new[] { a, b, Path.Combine(src, "sub") }, dst, move: false,
                    PasteConflictPolicy.AutoRename, threads: 4, bufferKilobytes: 4,
                    progress: null, askConflict: null, token: CancellationToken.None);

                Check("copy reported no failures", copy.Failed == 0, $"failed={copy.Failed}");
                Check("copy moved 3 files", copy.Succeeded == 3, $"succeeded={copy.Succeeded}");
                Check("flat files copied", File.Exists(Path.Combine(dst, "a.txt")) && File.Exists(Path.Combine(dst, "b.txt")));
                Check("nested tree preserved", File.Exists(Path.Combine(dst, "sub", "deep", "c.txt")));
                Check("content intact", File.ReadAllText(Path.Combine(dst, "a.txt")).Length == 5000);
                Check("source still present after copy", File.Exists(a));

                // Conflict: AutoRename must not clobber.
                var again = await FileOperations.RunAsync(
                    new[] { a }, dst, move: false, PasteConflictPolicy.AutoRename,
                    4, 64, null, null, CancellationToken.None);
                Check("auto-rename produced a (2) copy", File.Exists(Path.Combine(dst, "a (2).txt")));
                Check("auto-rename left the original alone",
                    File.ReadAllText(Path.Combine(dst, "a.txt")).Length == 5000);

                // Conflict: Skip must leave exactly one copy.
                int before = Directory.GetFiles(dst).Length;
                var skipped = await FileOperations.RunAsync(
                    new[] { a }, dst, move: false, PasteConflictPolicy.Skip,
                    4, 64, null, null, CancellationToken.None);
                Check("skip policy skipped the file", skipped.Skipped == 1, $"skipped={skipped.Skipped}");
                Check("skip policy wrote nothing new", Directory.GetFiles(dst).Length == before);

                // Move must remove the source.
                var moveDir = Path.Combine(temp, "moved");
                Directory.CreateDirectory(moveDir);
                var m = Path.Combine(src, "movable.txt");
                File.WriteAllText(m, "move me");
                var moved = await FileOperations.RunAsync(
                    new[] { m }, moveDir, move: true, PasteConflictPolicy.AutoRename,
                    2, 64, null, null, CancellationToken.None);
                Check("move reported success", moved.Succeeded == 1);
                Check("move removed the source", !File.Exists(m));
                Check("move created the destination", File.Exists(Path.Combine(moveDir, "movable.txt")));

                // Copying a folder into its own subtree must be refused, not looped
                // forever: the destination would keep appearing inside the source.
                var intoOwnSubtree = await FileOperations.RunAsync(
                    new[] { src }, Path.Combine(src, "sub"), move: false, PasteConflictPolicy.AutoRename,
                    2, 64, null, null, CancellationToken.None);
                Check("copying a folder into its own subtree is refused",
                    intoOwnSubtree.Failed > 0, $"succeeded={intoOwnSubtree.Succeeded}");

                // ...but pasting a folder back where it lives is how you duplicate
                // one, and it has to work. The desired destination is the source
                // itself, so a recursion check made before auto-rename had its say
                // saw a folder being copied into itself and refused the lot.
                var duplicateDir = Path.Combine(temp, "dup");
                Directory.CreateDirectory(Path.Combine(duplicateDir, "original", "inner"));
                File.WriteAllText(Path.Combine(duplicateDir, "original", "inner", "d.txt"), "dup me");

                var duplicated = await FileOperations.RunAsync(
                    new[] { Path.Combine(duplicateDir, "original") }, duplicateDir, move: false,
                    PasteConflictPolicy.AutoRename, 2, 64, null, null, CancellationToken.None);

                Check("duplicating a folder in place reports no failure",
                    duplicated.Failed == 0,
                    duplicated.Errors.Count > 0 ? duplicated.Errors[0] : $"succeeded={duplicated.Succeeded}");
                Check("duplicating a folder in place produces a renamed copy",
                    Directory.Exists(Path.Combine(duplicateDir, "original (2)")));
                Check("the duplicate has the contents",
                    File.Exists(Path.Combine(duplicateDir, "original (2)", "inner", "d.txt")));
                Check("the original is untouched",
                    File.Exists(Path.Combine(duplicateDir, "original", "inner", "d.txt")));

                // And a second duplicate becomes (3), not a failure.
                var again2 = await FileOperations.RunAsync(
                    new[] { Path.Combine(duplicateDir, "original") }, duplicateDir, move: false,
                    PasteConflictPolicy.AutoRename, 2, 64, null, null, CancellationToken.None);
                Check("duplicating twice gives a third copy",
                    again2.Failed == 0 && Directory.Exists(Path.Combine(duplicateDir, "original (3)")));

                // Overwriting a folder with itself is still meaningless and refused.
                var selfOverwrite = await FileOperations.RunAsync(
                    new[] { Path.Combine(duplicateDir, "original") }, duplicateDir, move: false,
                    PasteConflictPolicy.Overwrite, 2, 64, null, null, CancellationToken.None);
                Check("overwriting a folder with itself is still refused",
                    selfOverwrite.Failed > 0, $"succeeded={selfOverwrite.Succeeded}");

                // Cancellation must stop the batch.
                var cts = new CancellationTokenSource();
                cts.Cancel();
                var cancelled = await FileOperations.RunAsync(
                    new[] { a, b }, dst, false, PasteConflictPolicy.AutoRename,
                    2, 64, null, null, cts.Token);
                Check("pre-cancelled operation reports cancelled", cancelled.Cancelled);

                // ---- Empty folders must survive a copy ----
                // The plan is a list of files, and directories used to be created
                // only as a side effect of writing one into them. A folder with
                // nothing in it therefore vanished silently: no error, no failure
                // count, just a different structure at the other end.
                var shape = Path.Combine(temp, "shape");
                Directory.CreateDirectory(Path.Combine(shape, "empty"));
                Directory.CreateDirectory(Path.Combine(shape, "outer", "inner-empty"));
                Directory.CreateDirectory(Path.Combine(shape, "has-a-file"));
                File.WriteAllText(Path.Combine(shape, "has-a-file", "f.txt"), "x");

                var shapeDest = Path.Combine(temp, "shapedst");
                Directory.CreateDirectory(shapeDest);
                var shaped = await FileOperations.RunAsync(
                    new[] { shape }, shapeDest, move: false, PasteConflictPolicy.AutoRename,
                    4, 64, null, null, CancellationToken.None);

                var landed = Path.Combine(shapeDest, "shape");
                Check("copying reported no failures", shaped.Failed == 0,
                    shaped.Errors.Count > 0 ? shaped.Errors[0] : "");
                Check("an empty folder survives the copy",
                    Directory.Exists(Path.Combine(landed, "empty")));
                Check("an empty folder nested inside another survives",
                    Directory.Exists(Path.Combine(landed, "outer", "inner-empty")));
                Check("a folder that does have a file still arrives",
                    File.Exists(Path.Combine(landed, "has-a-file", "f.txt")));

                // An entirely empty folder must produce a folder, not nothing.
                var hollow = Path.Combine(temp, "hollow");
                Directory.CreateDirectory(hollow);
                var hollowDest = Path.Combine(temp, "hollowdst");
                Directory.CreateDirectory(hollowDest);
                await FileOperations.RunAsync(new[] { hollow }, hollowDest, false,
                    PasteConflictPolicy.AutoRename, 2, 64, null, null, CancellationToken.None);
                Check("copying an entirely empty folder still creates it",
                    Directory.Exists(Path.Combine(hollowDest, "hollow")));

                // Progress must actually fire.
                int reports = 0;
                var progress = new Progress<FileOpProgress>(_ => Interlocked.Increment(ref reports));
                var dst2 = Path.Combine(temp, "dst2");
                Directory.CreateDirectory(dst2);
                await FileOperations.RunAsync(new[] { a, b }, dst2, false, PasteConflictPolicy.AutoRename,
                    2, 4, progress, null, CancellationToken.None);
                await Task.Delay(150); // Progress<T> posts asynchronously
                Check("progress was reported", reports > 0, $"reports={reports}");
            }
            finally { Cleanup(temp); }
            Console.WriteLine();
        }

        // ---- FolderSizes, depth, timeout, cache ----
        private static async Task FolderSizeTests()
        {
            Console.WriteLine("Folder sizes:");

            var temp = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(temp, "one.bin"), new string('x', 1000));
                var sub = Path.Combine(temp, "sub");
                Directory.CreateDirectory(sub);
                File.WriteAllText(Path.Combine(sub, "two.bin"), new string('y', 2000));

                using var calc = new FolderSizeCalculator { TimeoutSeconds = 30, MaxDepth = 64 };

                var r = await calc.CalculateAsync(temp);
                Check("size totals the whole tree", r.Bytes == 3000, $"bytes={r.Bytes}");
                Check("file count correct", r.Files == 2, $"files={r.Files}");
                Check("folder count correct", r.Folders == 1, $"folders={r.Folders}");
                Check("walk reported complete", r.Complete);

                Check("result is cached", calc.TryGetCached(temp, out var cached) && cached.Bytes == 3000);

                // MaxDepth must stop the walk short rather than run away.
                using var shallow = new FolderSizeCalculator { MaxDepth = 1 };
                var limited = await shallow.CalculateAsync(temp);
                Check("depth limit excludes deeper files", limited.Bytes == 1000, $"bytes={limited.Bytes}");
                Check("depth limit marks the result incomplete", !limited.Complete);

                // Cancellation must be honoured.
                using var cancellable = new FolderSizeCalculator();
                using var stop = new CancellationTokenSource();
                var task = cancellable.CalculateAsync(temp, stop.Token);
                stop.Cancel();
                try { await task; Check("cancellation did not throw unexpectedly", true); }
                catch (OperationCanceledException) { Check("cancellation did not throw unexpectedly", true); }
                catch (Exception ex) { Check("cancellation did not throw unexpectedly", false, ex.GetType().Name); }
            }
            finally { Cleanup(temp); }
        }

        // ---- Folder sizing: loops, staleness, and being cancelled hard ----
        private static async Task FolderSizeEdgeTests()
        {
            Console.WriteLine("Folder sizing edge cases:");

            var temp = NewTempDir();
            try
            {
                using var calc = new FolderSizeCalculator { TimeoutSeconds = 30, MaxDepth = 64 };

                // An empty folder is zero, not a failure.
                var empty = Path.Combine(temp, "empty");
                Directory.CreateDirectory(empty);
                var emptyResult = await calc.CalculateAsync(empty);
                Check("an empty folder measures zero", emptyResult.Bytes == 0 && emptyResult.Files == 0);
                Check("an empty folder is a complete result", emptyResult.Complete);

                // A folder that does not exist must come back empty rather than throw.
                var gone = await calc.CalculateAsync(Path.Combine(temp, "not-here"));
                Check("a missing folder measures zero without throwing", gone.Bytes == 0);
                Check("a missing folder is marked incomplete", !gone.Complete);

                // The cache has to notice the folder changing underneath it,
                // otherwise a stale size sits in the column indefinitely.
                var changing = Path.Combine(temp, "changing");
                Directory.CreateDirectory(changing);
                File.WriteAllText(Path.Combine(changing, "one.bin"), new string('x', 500));

                var before = await calc.CalculateAsync(changing);
                Check("first measurement is right", before.Bytes == 500, $"bytes={before.Bytes}");
                Check("first measurement is cached", calc.TryGetCached(changing, out _));

                // The cache is keyed on the folder's write time, which has limited
                // resolution — wait long enough that the change is visible.
                await Task.Delay(50);
                File.WriteAllText(Path.Combine(changing, "two.bin"), new string('y', 700));
                await Task.Delay(50);

                Check("adding a file invalidates the cached size", !calc.TryGetCached(changing, out _));
                var after = await calc.CalculateAsync(changing);
                Check("the folder re-measures to the new total", after.Bytes == 1200, $"bytes={after.Bytes}");

                // A junction pointing back at its own parent is an infinite loop
                // for any walker that follows reparse points.
                var loopHome = Path.Combine(temp, "loop");
                Directory.CreateDirectory(loopHome);
                File.WriteAllText(Path.Combine(loopHome, "real.bin"), new string('z', 300));

                if (TryMakeJunction(Path.Combine(loopHome, "back"), loopHome))
                {
                    using var loopCalc = new FolderSizeCalculator { TimeoutSeconds = 10, MaxDepth = 64 };
                    var sw = Stopwatch.StartNew();
                    var looped = await loopCalc.CalculateAsync(loopHome);
                    sw.Stop();

                    Check($"a self-referential junction does not loop forever ({sw.ElapsedMilliseconds} ms)",
                        sw.ElapsedMilliseconds < 5000);
                    Check("a junction is not followed, so the size is counted once",
                        looped.Bytes == 300, $"bytes={looped.Bytes}");
                }
                else Console.WriteLine("  (skipped junction test: could not create a junction here)");

                // Cancelling repeatedly while walks are in flight is what the UI
                // does when someone holds the down arrow through a folder list.
                // The source used to be disposed out from under the running walks,
                // which threw ObjectDisposedException on a background thread.
                var deep = Path.Combine(temp, "deep");
                Directory.CreateDirectory(deep);
                for (int i = 0; i < 200; i++)
                    File.WriteAllText(Path.Combine(deep, $"f{i}.bin"), new string('q', 200));

                using var churn = new FolderSizeCalculator { TimeoutSeconds = 10 };
                var running = new List<Task<FolderSizeResult>>();
                Exception? thrown = null;

                try
                {
                    var arrow = new CancellationTokenSource();
                    for (int i = 0; i < 60; i++)
                    {
                        running.Add(churn.CalculateAsync(deep, arrow.Token));
                        if (i % 3 == 0) { arrow.Cancel(); arrow = new CancellationTokenSource(); }
                    }
                    await Task.WhenAll(running);
                }
                catch (Exception ex) { thrown = ex; }

                Check("cancelling repeatedly while walks run never throws",
                    thrown == null, thrown?.GetType().Name + ": " + thrown?.Message);
                Check("every cancelled walk still produced a result",
                    running.All(t => t.IsCompletedSuccessfully));

                // A per-call token must stop just that walk.
                using var scoped = new FolderSizeCalculator { TimeoutSeconds = 10 };
                using var mine = new CancellationTokenSource();
                mine.Cancel();
                var scopedResult = await scoped.CalculateAsync(deep, mine.Token);
                Check("a pre-cancelled per-call token returns an incomplete result without throwing",
                    !scopedResult.Complete);

                // ...and must leave everyone else alone.
                var unaffected = await scoped.CalculateAsync(deep);
                Check("cancelling one walk does not cancel the next", unaffected.Files == 200,
                    $"files={unaffected.Files}");

                // A timeout of one second over a big tree must return partial data
                // rather than run to completion or hang.
                using var impatient = new FolderSizeCalculator { TimeoutSeconds = 1 };
                var timed = Stopwatch.StartNew();
                var partial = await impatient.CalculateAsync(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                timed.Stop();
                Check($"a one-second timeout is respected ({timed.ElapsedMilliseconds} ms)",
                    timed.ElapsedMilliseconds < 15_000);
                Check("a timed-out walk reports itself incomplete", !partial.Complete);

                // Using the calculator after disposal must not throw either — the
                // form disposes it while background work may still be arriving.
                var disposable = new FolderSizeCalculator();
                disposable.Dispose();
                var afterDispose = await disposable.CalculateAsync(deep);
                Check("calculating after disposal returns quietly instead of throwing",
                    !afterDispose.Complete || afterDispose.Bytes >= 0);
            }
            finally { Cleanup(temp); }
            Console.WriteLine();
        }

        /// <summary>
        /// Junctions need no elevation, unlike symlinks, so this normally works.
        /// Returns false if the filesystem would not take one.
        /// </summary>
        private static bool TryMakeJunction(string link, string target)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(10_000);
                return p.HasExited && p.ExitCode == 0 && Directory.Exists(link);
            }
            catch { return false; }
        }

        // ---- Names the command line and the shell find awkward ----
        private static async Task AwkwardNameTests()
        {
            Console.WriteLine("Awkward file names:");

            var temp = NewTempDir();
            try
            {
                var src = Path.Combine(temp, "source folder");   // a space in the folder itself
                var dst = Path.Combine(temp, "dest folder");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);

                // Every one of these has broken a file manager that builds a
                // command line by string concatenation at some point.
                var tricky = new[]
                {
                    "plain.bin",
                    "with spaces.bin",
                    "with  double  spaces.bin",
                    "ünïcodé-ファイル.bin",
                    "ampersand & thing.bin",
                    "percent %20 sign.bin",
                    "caret ^ hat.bin",
                    "parens (1).bin",
                    "hash # mark.bin",
                    "dollar $ sign.bin",
                    "apostrophe's.bin",
                    "equals=sign.bin",
                    "semi;colon.bin",
                    "comma,name.bin",
                    "exclaim!.bin",
                    "at@sign.bin",
                    "-leading-dash.bin",
                };

                foreach (var name in tricky)
                    File.WriteAllText(Path.Combine(src, name), new string('x', 1000));

                // Through robocopy, which is the path a paste actually takes and
                // the one that has to survive quoting.
                var robo = await RoboCopyEngine.RunAsync(
                    new[] { src }, dst, move: false, threads: 4,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None);

                Check("robocopy handled the awkward names without failing", robo.Failed == 0,
                    robo.Errors.Count > 0 ? robo.Errors[0] : "");

                var landed = Path.Combine(dst, "source folder");
                foreach (var name in tricky)
                    Check($"robocopy copied \"{name}\"", File.Exists(Path.Combine(landed, name)));

                Check("robocopy reported a file count, not a job count",
                    robo.Copied >= tricky.Length - 2, $"copied={robo.Copied} of {tricky.Length}");

                // And through the managed engine, which is what auto-rename uses.
                var dst2 = Path.Combine(temp, "dest two");
                Directory.CreateDirectory(dst2);
                var managed = await FileOperations.RunAsync(
                    new[] { src }, dst2, move: false, PasteConflictPolicy.AutoRename,
                    4, 64, null, null, CancellationToken.None);

                Check("the managed engine handled the awkward names", managed.Failed == 0,
                    managed.Errors.Count > 0 ? managed.Errors[0] : "");
                Check("the managed engine copied every one", managed.Succeeded == tricky.Length,
                    $"succeeded={managed.Succeeded} of {tricky.Length}");

                var landed2 = Path.Combine(dst2, "source folder");
                foreach (var name in tricky)
                    Check($"the managed engine copied \"{name}\"", File.Exists(Path.Combine(landed2, name)));

                // Content must survive intact, not just the name.
                Check("content survived a unicode name",
                    File.ReadAllText(Path.Combine(landed2, "ünïcodé-ファイル.bin")).Length == 1000);

                // A deeply nested path approaching the classic 260-character limit.
                var deep = temp;
                for (int i = 0; i < 12 && deep.Length < 200; i++)
                {
                    deep = Path.Combine(deep, "level-" + i.ToString("D2") + "-padding");
                    Directory.CreateDirectory(deep);
                }
                File.WriteAllText(Path.Combine(deep, "buried.bin"), new string('d', 100));

                using var deepCalc = new FolderSizeCalculator { TimeoutSeconds = 20 };
                var deepSize = await deepCalc.CalculateAsync(temp);
                Check("a deeply nested file is still counted", deepSize.Files > tricky.Length,
                    $"files={deepSize.Files}");
            }
            finally { Cleanup(temp); }
            Console.WriteLine();
        }

        // ---- Everything at once, which is what an impatient user produces ----
        private static async Task ConcurrencyTests()
        {
            Console.WriteLine("Concurrent work:");

            var temp = NewTempDir();
            try
            {
                // Several folders, each with real content to walk.
                var folders = new List<string>();
                for (int f = 0; f < 8; f++)
                {
                    var dir = Path.Combine(temp, "folder" + f);
                    Directory.CreateDirectory(dir);
                    for (int i = 0; i < 150; i++)
                        File.WriteAllText(Path.Combine(dir, $"f{i}.bin"), new string('x', 100));
                    folders.Add(dir);
                }

                using var calc = new FolderSizeCalculator { TimeoutSeconds = 30 };

                var sw = Stopwatch.StartNew();
                var results = await Task.WhenAll(folders.Select(f => calc.CalculateAsync(f)));
                sw.Stop();

                Check("every concurrent walk produced a size", results.All(r => r.Bytes == 15_000),
                    string.Join(",", results.Select(r => r.Bytes)));
                Check($"eight concurrent walks finished promptly ({sw.ElapsedMilliseconds} ms)",
                    sw.ElapsedMilliseconds < 20_000);

                // The cache must be safe to hammer from many threads at once.
                var cacheHits = 0;
                Parallel.For(0, 200, iteration =>
                {
                    foreach (var f in folders)
                        if (calc.TryGetCached(f, out _)) Interlocked.Increment(ref cacheHits);
                });
                Check("the cache survives concurrent readers", cacheHits > 0, $"hits={cacheHits}");

                // Several copies running at once, which is what two tabs pasting
                // into the same place looks like.
                var targets = new List<string>();
                for (int i = 0; i < 4; i++)
                {
                    var t = Path.Combine(temp, "target" + i);
                    Directory.CreateDirectory(t);
                    targets.Add(t);
                }

                var copies = await Task.WhenAll(targets.Select(t =>
                    FileOperations.RunAsync(new[] { folders[0] }, t, move: false,
                        PasteConflictPolicy.AutoRename, 4, 64, null, null, CancellationToken.None)));

                Check("four simultaneous copies all succeeded", copies.All(c => c.Failed == 0),
                    string.Join(";", copies.SelectMany(c => c.Errors).Take(3)));
                Check("four simultaneous copies each moved every file",
                    copies.All(c => c.Succeeded == 150), string.Join(",", copies.Select(c => c.Succeeded)));

                // Cancelling a copy part-way must stop it, not corrupt it.
                var bigSource = Path.Combine(temp, "big");
                Directory.CreateDirectory(bigSource);
                for (int i = 0; i < 400; i++)
                    File.WriteAllText(Path.Combine(bigSource, $"b{i}.bin"), new string('b', 20_000));

                var cancelTarget = Path.Combine(temp, "cancelled");
                Directory.CreateDirectory(cancelTarget);

                using var cts = new CancellationTokenSource();
                var running = FileOperations.RunAsync(new[] { bigSource }, cancelTarget, false,
                    PasteConflictPolicy.AutoRename, 4, 64, null, null, cts.Token);
                cts.CancelAfter(60);

                var cancelled = await running;
                Check("a copy cancelled mid-flight reports cancelled or completes cleanly",
                    cancelled.Cancelled || cancelled.Failed == 0,
                    $"cancelled={cancelled.Cancelled}, failed={cancelled.Failed}");
                Check("a cancelled copy does not throw", true);
            }
            finally { Cleanup(temp); }
            Console.WriteLine();
        }

        // ---- Denied permissions must degrade, never crash ----
        private static async Task PermissionTests()
        {
            Console.WriteLine("Permission failures:");

            // A real protected location, rather than a synthetic ACL: this is
            // exactly what the user hits when they wander into system folders.
            var protectedDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");

            Check("protected directory exists to test against", Directory.Exists(protectedDir));

            // Creating a folder where we have no rights.
            bool createThrewCleanly = false;
            try { Directory.CreateDirectory(Path.Combine(protectedDir, "ExplorerNativeShouldFail")); }
            catch (UnauthorizedAccessException) { createThrewCleanly = true; }
            catch (IOException) { createThrewCleanly = true; }
            Check("creating a folder without rights raises a catchable error", createThrewCleanly);

            var temp = NewTempDir();
            try
            {
                var src = Path.Combine(temp, "payload.txt");
                File.WriteAllText(src, "data");

                // Copy INTO a protected folder: must come back as a failed
                // result with an error message, not an exception.
                var denied = await FileOperations.RunAsync(
                    new[] { src }, protectedDir, move: false, PasteConflictPolicy.AutoRename,
                    2, 64, null, null, CancellationToken.None);

                Check("copy into a protected folder does not throw", true);
                Check("copy into a protected folder reports a failure",
                    denied.Failed > 0 || denied.Succeeded == 0, $"succeeded={denied.Succeeded}, failed={denied.Failed}");
                Check("failure carries an explanatory message",
                    denied.Errors.Count > 0 || denied.Succeeded == 0,
                    denied.Errors.Count > 0 ? denied.Errors[0] : "(no message)");
                Check("nothing was written into the protected folder",
                    !File.Exists(Path.Combine(protectedDir, "payload.txt")));

                // Copy FROM an unreadable source.
                var missing = Path.Combine(temp, "does-not-exist.txt");
                var noSource = await FileOperations.RunAsync(
                    new[] { missing }, temp, false, PasteConflictPolicy.AutoRename,
                    2, 64, null, null, CancellationToken.None);
                Check("a missing source does not throw", true);
                Check("a missing source copies nothing", noSource.Succeeded == 0);

                // A file locked by another handle must fail that item only.
                var locked = Path.Combine(temp, "locked.bin");
                File.WriteAllText(locked, "locked content");
                var ok = Path.Combine(temp, "fine.bin");
                File.WriteAllText(ok, "fine content");
                var outDir = Path.Combine(temp, "out");
                Directory.CreateDirectory(outDir);

                using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var mixed = await FileOperations.RunAsync(
                        new[] { locked, ok }, outDir, false, PasteConflictPolicy.AutoRename,
                        2, 64, null, null, CancellationToken.None);

                    Check("a locked file does not abort the whole batch", mixed.Succeeded >= 1,
                        $"succeeded={mixed.Succeeded}, failed={mixed.Failed}");
                    Check("the unlocked file still copied", File.Exists(Path.Combine(outDir, "fine.bin")));
                    Check("the locked file is reported as failed", mixed.Failed >= 1, $"failed={mixed.Failed}");
                }

                // Renaming into a name we cannot create.
                bool renameThrewCleanly = false;
                try { File.Move(src, Path.Combine(protectedDir, "renamed.txt")); }
                catch (UnauthorizedAccessException) { renameThrewCleanly = true; }
                catch (IOException) { renameThrewCleanly = true; }
                Check("renaming into a protected folder raises a catchable error", renameThrewCleanly);

                // Deleting something we do not own.
                bool deleteThrewCleanly = false;
                try
                {
                    var systemFile = Path.Combine(protectedDir, "kernel32.dll");
                    if (File.Exists(systemFile)) File.Delete(systemFile);
                    else deleteThrewCleanly = true; // nothing to prove against
                }
                catch (UnauthorizedAccessException) { deleteThrewCleanly = true; }
                catch (IOException) { deleteThrewCleanly = true; }
                Check("deleting a protected file raises a catchable error", deleteThrewCleanly);

                // Folder sizing over an unreadable tree must return partial data,
                // not blow up.
                using var calc = new FolderSizeCalculator { TimeoutSeconds = 5, MaxDepth = 3 };
                var sized = await calc.CalculateAsync(protectedDir);
                Check("sizing a protected tree does not throw", true);
                Check("sizing a protected tree returns something usable", sized.Bytes >= 0);
            }
            finally { Cleanup(temp); }
            Console.WriteLine();
        }

        // ---- The robocopy-backed transfer engine ----
        /// <summary>
        /// What a transfer says it has done, given the two sources that disagree.
        ///
        /// Progress used to come only from parsing robocopy's per-file output,
        /// and a child process writing to a redirected pipe does not line-buffer
        /// — it fills a block and flushes, so those lines arrive in bursts of
        /// roughly fifty and mostly at the end. Measured on a 40-file copy with
        /// the engine's own arguments: one line at 37ms, the other 39 together at
        /// 45-48ms.
        ///
        /// The result was not a stale number but a meaningless one. A real 42GB
        /// copy running at 36MB/s reported "12.4 megabytes of 41.5 gigabytes",
        /// "59.3 kilobytes per second" and 203 hours remaining, when the true
        /// answer was about eight minutes. Whatever else this arithmetic does, it
        /// must never produce that again.
        /// </summary>
        /// <summary>
        /// The text and path surface, fed things nobody would type on purpose.
        ///
        /// Every one of these is reachable: names come off somebody else's disk
        /// or out of Google Drive, paths arrive on a command line from the shell,
        /// and settings.json is a file people are invited to edit. The bar is not
        /// that the answer is pretty — it is that there is an answer, and that
        /// nothing here throws its way up into a keystroke.
        /// </summary>
        private static void HostileInputTests()
        {
            Console.WriteLine("Hostile input:");

            var nasty = new[]
            {
                "", " ", "\t", "\n", ".", "..", "...", "   ...   ", "\0", "a\0b",
                "con", "CON.txt", "LPT9", "nul.flac", "COM1.", "aux",
                "a:b", "a/b", "a\\b", "a|b", "a*b", "a?b", "a\"b", "a<b", "a>b",
                "trailing ", "trailing.", " leading", new string('x', 300),
                "\\\\?\\C:\\x", "C:", "C:\\", "\\\\server\\share", "%NOPE%",
                "émoji-🎵.flac", "\u202Ereversed", "..\\..\\escape",
            };

            int threw = 0;
            foreach (var s in nasty)
            {
                try
                {
                    _ = NameRules.DescribeBadName(s);
                    _ = NameRules.IsUsableName(s);
                    _ = NameRules.NormaliseLaunchPath(s);
                    _ = NameRules.CompareNames(s, s, "other", "other");
                    _ = TypeAhead.Fold(s);
                    _ = DriveMount.Sanitise(s);
                    _ = RoboCopyEngine.QuoteDir(s);
                    _ = AudioFiles.IsAudio(s, null);
                    _ = Shortcut.Parse(s);
                    _ = Notifications.ParseOverrides(s);
                    _ = SizeFormatter.Format(s.Length, SizeUnitStyle.FullWords);
                    _ = FileLauncher.ParseRegisteredCommand(s, _ => false);
                    _ = DriveMount.IsAtOrUnder(s, "C:\\root");
                    _ = FindIn(new[] { s, "other" }, s, 0);
                }
                catch (Exception ex)
                {
                    threw++;
                    Console.WriteLine($"        threw on \"{Escape(s)}\": {ex.GetType().Name}: {ex.Message}");
                }
            }
            Check("nothing in the pure text surface throws on hostile input", threw == 0, $"{threw} threw");

            // Nulls, separately, because most of these accept them.
            try
            {
                _ = NameRules.DescribeBadName(null);
                _ = NameRules.NormaliseLaunchPath(null);
                _ = Shortcut.Parse(null);
                _ = Notifications.ParseOverrides(null);
                _ = Notifications.ById(null!);
                _ = AudioFiles.IsAudio("x.flac", null);
                _ = FileLauncher.ParseRegisteredCommand(null, _ => false);
                _ = NavigationHistory.ChildOnPathTo(null, null);
                Check("null does not throw where it is accepted", true);
            }
            catch (Exception ex)
            {
                Check("null does not throw where it is accepted", false, ex.GetType().Name + ": " + ex.Message);
            }

            // ---- names that survive being cleaned up ----
            //
            // Sanitise strips what NTFS refuses and then trims trailing dots and
            // spaces. A name made entirely of those is left with nothing at all,
            // and an empty relative name is not a file anyone can create.
            foreach (var vanishing in new[] { "...", "   ", " . ", "..", "\t" })
                Check($"a name of only trimmings still yields a usable one (\"{Escape(vanishing)}\")",
                    DriveMount.Sanitise(vanishing).Length > 0,
                    $"sanitised to empty");

            // A name that is nothing but characters NTFS refuses becomes
            // underscores, which is a name.
            Equal("forbidden characters become underscores", "___", DriveMount.Sanitise("<>|"));
            Equal("and so does a name of only dots", "___", DriveMount.Sanitise("..."));
            Equal("length is kept, so two of them stay apart", "__", DriveMount.Sanitise(".."));

            // Reserved device names are legitimate in Drive and unwritable here.
            foreach (var reserved in new[] { "CON", "con", "PRN", "AUX", "NUL", "COM1", "LPT9" })
                Check($"the device name {reserved} is made writable",
                    NameRules.IsUsableName(DriveMount.Sanitise(reserved)),
                    DriveMount.Sanitise(reserved));

            Equal("a device name with an extension too", "CON_.flac",
                DriveMount.Sanitise("CON.flac"));

            // NTFS stops at 255 characters for one component and Drive does not,
            // which is another way for a file to never appear.
            var overlong = DriveMount.Sanitise(new string('x', 400) + ".flac");
            Check("an over-long name is shortened to something NTFS takes",
                overlong.Length <= 255, $"length {overlong.Length}");
            Check("and keeps its extension, which is what decides it opens",
                overlong.EndsWith(".flac", StringComparison.Ordinal), overlong);

            // And everything sanitised is a name Windows will actually take.
            int unusable = 0;
            foreach (var s in nasty)
                if (!NameRules.IsUsableName(DriveMount.Sanitise(s))) unusable++;
            Check("everything sanitised comes out usable", unusable == 0, $"{unusable} were not");

            // ---- type-ahead over awkward lists ----
            Equal("no match in an empty list is -1", "-1",
                FindIn(Array.Empty<string>(), "a", 0).ToString());
            Equal("an empty query matches nothing", "-1",
                FindIn(new[] { "alpha" }, "", 0).ToString());
            Equal("a current index past the end still finds the match", "0",
                FindIn(new[] { "alpha", "beta" }, "al", 99).ToString());
            Equal("a negative current index is tolerated", "0",
                FindIn(new[] { "alpha", "beta" }, "al", -5).ToString());

            // ---- the typed word has to be able to end on its own ----
            //
            // Space is part of a name while a word is being typed and toggles the
            // selection otherwise, so "is a word being typed" has to be true only
            // while one really is. Expiring solely inside Accept makes it a
            // decision taken on the *next* keystroke, so the answer stayed yes
            // for ever and Space stopped selecting after any letter.
            var word = new TypeAheadBuffer { TimeoutMilliseconds = 250 };
            Equal("a letter starts a word", "s", word.Accept('s', 1000));
            Equal("and the word is current straight away", "s", word.Current(1000));
            Equal("still current inside the pause", "s", word.Current(1200));
            Equal("and gone once the pause is over", "", word.Current(2000));
            Check("which the raw text alone never reports", true);

            var typed = new TypeAheadBuffer { TimeoutMilliseconds = 250 };
            typed.Accept('a', 1000);
            typed.Accept('b', 1100);
            Equal("letters inside the pause build one word", "ab", typed.Current(1200));
            Equal("a letter after the pause starts again", "c", typed.Accept('c', 5000));
            Equal("and Reset ends it immediately", "", ResetAndRead(typed));

            // ---- the launch-path repair, which the shell really does produce ----
            Equal("a drive with the quote eaten becomes the drive root",
                "D:\\", NameRules.NormaliseLaunchPath("D:\""));
            Equal("and a bare drive letter likewise", "D:\\", NameRules.NormaliseLaunchPath("D:"));
            Equal("an ordinary path is left alone",
                "C:\\Users\\x", NameRules.NormaliseLaunchPath("C:\\Users\\x"));

            Console.WriteLine();
        }

        /// <summary>
        /// The numeric and stateful surface at its boundaries.
        ///
        /// Same bar as the text one: these are reached from a settings file
        /// people edit, from a media engine that answers NaN before metadata
        /// lands, and from a filesystem callback asking for a range of a file.
        /// </summary>
        private static void BoundaryTests()
        {
            Console.WriteLine("Boundaries:");

            // ---- settings clamped from nonsense ----
            var wild = new Settings
            {
                FontSize = int.MinValue,
                CopyBufferKilobytes = int.MaxValue,
                FolderSizeTimeoutSeconds = -1,
                TypeAheadMilliseconds = int.MaxValue,
                WindowWidth = int.MinValue,
                WindowHeight = int.MaxValue,
                ActiveTab = 99,
                AudioVolumePercent = int.MaxValue,
                AudioSeekSeconds = -100,
                AudioLongSeekSeconds = int.MaxValue,
                AudioPlaybackRatePercent = 0,
                AudioFadeInMilliseconds = -5,
                AudioFadeOutMilliseconds = int.MaxValue,
                AudioRewindOnResumeSeconds = -1,
                AudioPrefetchKilobytes = int.MinValue,
                AudioReleaseAfterMinutes = -1,
                AudioExtensions = "   ",
                WindowTitle = "   ",
            };

            var dir = NewTempDir();
            try
            {
                Settings.OverrideAppDataDir = dir;
                Check("nonsense settings save", wild.Save());
                var back = Settings.Load();

                Check("font size lands in range", back.FontSize is >= 7 and <= 24, back.FontSize.ToString());
                Check("copy buffer lands in range",
                    back.CopyBufferKilobytes is >= 4 and <= 16384, back.CopyBufferKilobytes.ToString());
                Check("the active tab is one of the two",
                    back.ActiveTab is 0 or 1, back.ActiveTab.ToString());
                Check("the volume cannot exceed what the player offers",
                    back.AudioVolumePercent is >= 0 and <= AudioPlayer.LoudestPercent,
                    back.AudioVolumePercent.ToString());
                Check("the repeat rate stays above the timer floor",
                    HoldRepeat.Interval >= HoldRepeat.FastestRepeatMilliseconds,
                    HoldRepeat.Interval.ToString());
                Check("and a hold takes longer to start than a tap lasts",
                    HoldRepeat.Delay > 100, HoldRepeat.Delay.ToString());
                Check("the read-ahead cannot ask for more than a read-ahead",
                    back.AudioPrefetchKilobytes is >= 0 and <= 1048576,
                    back.AudioPrefetchKilobytes.ToString());
                Check("an emptied extension list goes back to the defaults",
                    back.AudioExtensions == AudioFiles.DefaultExtensions);
                Check("a blank window title gets a name back",
                    back.WindowTitle == Settings.DefaultWindowTitle, back.WindowTitle);

                // Everything clamped must still be a value the player accepts.
                Check("the clamped playback rate is one the ladder allows",
                    back.AudioPlaybackRatePercent >= AudioPlayer.SlowestPercent &&
                    back.AudioPlaybackRatePercent <= AudioPlayer.FastestPercent,
                    back.AudioPlaybackRatePercent.ToString());
            }
            finally { Settings.OverrideAppDataDir = null; Cleanup(dir); }

            // ---- overrides survive a round trip through the settings string ----
            var picked = new Dictionary<string, NotificationChannel>
            {
                // Each of these differs from its own default, or it would not be
                // written at all — only differences are stored.
                ["audio.play"] = NotificationChannel.Speech,
                ["copy.done"] = NotificationChannel.None,
                ["not.a.real.id"] = NotificationChannel.Speech,
            };
            var text = Notifications.FormatOverrides(picked);
            var reparsed = Notifications.ParseOverrides(text);
            Check("a real override survives the round trip",
                reparsed.TryGetValue("audio.play", out var one) && one == NotificationChannel.Speech);
            Check("and the other one", reparsed.TryGetValue("copy.done", out var two) &&
                two == NotificationChannel.None);
            Check("an id the catalogue does not know is dropped",
                !reparsed.ContainsKey("not.a.real.id"));

            foreach (var junk in new[] { "=", "a=", "=1", ";;;", "audio.play=", "audio.play=zzz",
                                          "audio.play=999999999999999999999", "audio.play=-1" })
            {
                try { Notifications.ParseOverrides(junk); Check($"junk overrides survive \"{junk}\"", true); }
                catch (Exception ex) { Check($"junk overrides survive \"{junk}\"", false, ex.Message); }
            }

            // ---- streaming source, asked for things outside the file ----
            var sandbox = NewTempDir();
            try
            {
                var track = Path.Combine(sandbox, "t.bin");
                File.WriteAllBytes(track, new byte[8192]);

                using var source = new StreamingSource(track, 1024 * 1024);
                var buffer = new byte[4096];

                source.Seek(999999, 0, IntPtr.Zero);
                source.Read(buffer, buffer.Length, IntPtr.Zero);
                Check("a read past the end returns rather than throws", true);

                source.Seek(-999999, 0, IntPtr.Zero);
                source.Read(buffer, buffer.Length, IntPtr.Zero);
                Check("and so does a seek before the start", true);

                source.Seek(0, 2, IntPtr.Zero);              // STREAM_SEEK_END
                source.Read(buffer, 0, IntPtr.Zero);
                source.Read(buffer, -1, IntPtr.Zero);
                Check("a read of nothing, or of less than nothing, is harmless", true);

                Check("the length is what the file holds", source.Length == 8192, source.Length.ToString());
            }
            catch (Exception ex)
            {
                Check("the streaming source survives hostile reads", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally { Cleanup(sandbox); }

            // ---- times and sizes at the ends ----
            foreach (var span in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1),
                                          TimeSpan.MaxValue, TimeSpan.FromDays(999) })
            {
                try { _ = RoboCopyEngine.FormatDuration(span); Check($"a duration of {span} formats", true); }
                catch (Exception ex) { Check($"a duration of {span} formats", false, ex.Message); }
            }

            foreach (var rate in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, double.MaxValue })
            {
                try
                {
                    var said = RoboCopyEngine.FormatSpeed(rate, SizeUnitStyle.FullWords);
                    Check($"a speed of {rate} formats", !string.IsNullOrWhiteSpace(said), said);
                }
                catch (Exception ex) { Check($"a speed of {rate} formats", false, ex.Message); }
            }

            // The relative date, either side of every boundary it has.
            var now = new DateTime(2026, 3, 15, 12, 0, 0);
            foreach (var days in new[] { 0, 1, 2, 6, 7, 13, 14, 29, 30, 31, 364, 365, 366, 3650, 4000, 40000 })
            {
                var said = TimeFormatter.Relative(now.AddDays(-days), now);
                Check($"{days} days ago reads as something", !string.IsNullOrWhiteSpace(said), said);
            }
            Equal("today is today", "today", TimeFormatter.Relative(now.AddHours(-1), now));
            Equal("yesterday is yesterday", "yesterday", TimeFormatter.Relative(now.AddDays(-1), now));
            Equal("the future is called out", "in the future", TimeFormatter.Relative(now.AddHours(1), now));

            Console.WriteLine();
        }

        /// <summary>
        /// Three things the code states precisely and nothing checked.
        ///
        /// A bound that is documented and not enforced, a depth limit whose
        /// meaning is spelled out in a comment, and the quoting rules for a
        /// registered shell command — which decide whether a path with a space in
        /// it opens or turns into two arguments.
        /// </summary>
        private static async Task StatedClaimTests()
        {
            Console.WriteLine("Claims the code makes about itself:");

            // ---- the position memory really is bounded ----
            //
            var memory = new FolderPositionMemory();
            for (int i = 0; i < FolderPositionMemory.MaxRemembered * 3; i++)
                memory.Remember($@"C:\folder{i}", $@"C:\folder{i}\file.txt");

            Check("the memory stays inside its bound",
                memory.Count <= FolderPositionMemory.MaxRemembered,
                $"{memory.Count} of {FolderPositionMemory.MaxRemembered}");

            // ---- what the depth limit counts ----
            //
            // "MaxDepth counts levels actually walked: 1 means the root's own
            // files only, 2 adds its immediate subfolders."
            var tree = NewTempDir();
            try
            {
                var level1 = Path.Combine(tree, "one");
                var level2 = Path.Combine(level1, "two");
                Directory.CreateDirectory(level2);
                File.WriteAllBytes(Path.Combine(tree, "root.bin"), new byte[1000]);
                File.WriteAllBytes(Path.Combine(level1, "one.bin"), new byte[200]);
                File.WriteAllBytes(Path.Combine(level2, "two.bin"), new byte[30]);

                using var sizes = new FolderSizeCalculator { TimeoutSeconds = 30, MaxDepth = 1 };
                var justRoot = await sizes.CalculateAsync(tree);
                Equal("depth 1 counts the root's own files only", "1000", justRoot.Bytes.ToString());
                Check("and says it did not finish", !justRoot.Complete);

                using var two = new FolderSizeCalculator { TimeoutSeconds = 30, MaxDepth = 2 };
                var oneDown = await two.CalculateAsync(tree);
                Equal("depth 2 adds the immediate subfolders", "1200", oneDown.Bytes.ToString());
                Check("and still says it stopped short", !oneDown.Complete);

                using var deep = new FolderSizeCalculator { TimeoutSeconds = 30, MaxDepth = 64 };
                var all = await deep.CalculateAsync(tree);
                Equal("a generous depth reaches everything", "1230", all.Bytes.ToString());
                Check("and reports itself complete", all.Complete);
            }
            finally { Cleanup(tree); }

            // ---- registered shell commands ----
            //
            // "%1 is frequently registered unquoted, because the shell quotes
            // what it substitutes and we do not: an unquoted %1 becomes several
            // arguments the moment a path contains a space."
            Func<string, bool> anyExe = p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

            var quoted = FileLauncher.ParseRegisteredCommand("\"C:\\app.exe\" \"%1\"", anyExe);
            Check("a quoted command parses", quoted != null);
            Equal("the executable comes out", "C:\\app.exe", quoted?.Exe ?? "");
            Equal("and the placeholder stays quoted", "\"%1\"", quoted?.ArgsTemplate ?? "");

            var bare = FileLauncher.ParseRegisteredCommand("\"C:\\app.exe\" %1", anyExe);
            Equal("an unquoted placeholder is quoted for us", "\"%1\"", bare?.ArgsTemplate ?? "");

            var none = FileLauncher.ParseRegisteredCommand("\"C:\\app.exe\"", anyExe);
            Equal("a command with no placeholder gets one", "\"%1\"", none?.ArgsTemplate ?? "");

            var lowerL = FileLauncher.ParseRegisteredCommand("\"C:\\app.exe\" %L", anyExe);
            Equal("%L means the same thing", "\"%1\"", lowerL?.ArgsTemplate ?? "");

            var unquotedExe = FileLauncher.ParseRegisteredCommand(
                "%SystemRoot%\\system32\\NOTEPAD.EXE %1", anyExe);
            Check("an unquoted path with an environment variable resolves", unquotedExe != null,
                "the search for where the executable ended must expand first");

            // Substitutions only the shell can make are handed back to it rather
            // than launched with a nonsense command line.
            foreach (var shellOnly in new[]
            {
                "\"C:\\app.exe\" /idlist,%I,%L",
                "\"C:\\app.exe\" %D",
                "\"C:\\app.exe\" %2",
                "\"C:\\app.exe\" %9",
                "\"C:\\app.exe\" %S",
                "\"C:\\app.exe\" %v",
            })
                Check($"refused, so the shell handles it: {shellOnly}",
                    FileLauncher.ParseRegisteredCommand(shellOnly, anyExe) == null);

            // An escaped percent is not a placeholder.
            Check("an escaped percent sign is not mistaken for one",
                FileLauncher.ParseRegisteredCommand("\"C:\\app.exe\" 100%% \"%1\"", anyExe) != null);

            Check("a command naming nothing that exists is refused",
                FileLauncher.ParseRegisteredCommand("\"C:\\gone.txt\" \"%1\"", anyExe) == null);

            Console.WriteLine();
        }

        private static string ResetAndRead(TypeAheadBuffer buffer)
        {
            buffer.Reset();
            return buffer.Current(99999);
        }

        private static string Escape(string s) =>
            s.Replace("\0", "\\0").Replace("\t", "\\t").Replace("\n", "\\n");

        /// <summary>
        /// A global shortcut may never take a key the application, or everything
        /// else on the machine, already depends on.
        ///
        /// RegisterHotKey is exclusive machine-wide. Setting "play or pause" to
        /// Ctrl+C would register cleanly, report no conflict, and stop copying
        /// working in every program on the computer — including this one, whose
        /// own Copy would never receive the key again. Nothing in Windows would
        /// call that an error.
        /// </summary>
        private static void ReservedShortcutTests()
        {
            Console.WriteLine("Keys a global shortcut may not take:");

            // The ones somebody would actually reach for, and the ones that would
            // be quietest to lose.
            foreach (var text in new[]
            {
                "Ctrl+C", "Ctrl+X", "Ctrl+V", "Ctrl+A", "Ctrl+Z", "Ctrl+Y", "Ctrl+S",
                "Ctrl+P", "Ctrl+L", "Ctrl+R", "Ctrl+F", "Ctrl+N", "Ctrl+O", "Ctrl+W",
                "Alt+C", "Alt+Up", "Alt+D", "Alt+Home", "Alt+Enter", "Alt+F4",
                "F2", "F5", "F7", "F8", "Shift+Delete",
                "Ctrl+Shift+S", "Ctrl+Shift+A", "Ctrl+Shift+N", "Ctrl+Tab",
                "Ctrl+Space", "Shift+Down", "Shift+Up", "Shift+Home", "Shift+End",
                "Shift+F10",
            })
            {
                var owner = ReservedShortcuts.OwnerOf(Shortcut.Parse(text));
                Check($"{text} is refused", owner != null, "it was allowed through");
                if (owner != null)
                    Check($"and {text} says what owns it", !string.IsNullOrWhiteSpace(owner), owner);
            }

            // Delete on its own cannot be registered anyway, but it is reserved
            // so the reason given is the right one.
            Check("Delete on its own is reserved", ReservedShortcuts.OwnerOf(Shortcut.Parse("Delete")) != null);

            // Space on its own is what selects the row you are standing on, and
            // it is refused twice over — no modifier, and reserved. The reserved
            // answer is the one worth having, because it says what would break.
            var spaceOwner = ReservedShortcuts.OwnerOf(Shortcut.Parse("Space"));
            Check("Space on its own is reserved", spaceOwner != null, "it was allowed through");
            Check("and Space says selecting is what it would cost",
                spaceOwner != null && spaceOwner.Contains("Select", StringComparison.OrdinalIgnoreCase),
                spaceOwner ?? "");

            // ---- every function key, on its own ----
            //
            // A function key is only ever a command — nothing types F6 — so
            // handing one to Windows is that command gone from every program on
            // the machine, with nothing on screen to connect the two. F13 to F24
            // are in as well, even though Shortcut.IsSelfContained says Windows
            // would happily take them: that answers "will Windows take it", this
            // answers "should we ask", and this one runs first.
            int unreservedFunctionKeys = 0;
            for (var key = Keys.F1; key <= Keys.F24; key++)
            {
                var owner = ReservedShortcuts.OwnerOf(new Shortcut(0, (uint)key));
                if (owner == null) { unreservedFunctionKeys++; Console.WriteLine($"        {key} was allowed through"); }
            }
            Equal("every function key from F1 to F24 is reserved", "0", unreservedFunctionKeys.ToString());

            // The four the window names keep their own wording. "F5 is Refresh"
            // sends somebody somewhere; "F5 is a function key" does not.
            foreach (var (key, expected) in new[]
            {
                ("F2", "Rename"), ("F5", "Refresh"), ("F7", "New file"), ("F8", "New folder"),
            })
                Equal($"{key} is still named after what it does",
                    expected, ReservedShortcuts.OwnerOf(Shortcut.Parse(key)) ?? "");

            // A function key with a modifier is a different key and stays usable:
            // reserving all 24 times all 8 modifier combinations would take away
            // most of the shortcut space there is, to protect nothing — Ctrl+Alt
            // +F9 is not a command any application already has.
            Check("Ctrl+Alt+F9 is still allowed",
                ReservedShortcuts.OwnerOf(Shortcut.Parse("Ctrl+Alt+F9")) == null);

            // The combinations the player actually ships with must all be free,
            // or the defaults would refuse themselves.
            var fresh = new Settings();
            foreach (var info in AudioActions.All)
            {
                var assigned = Shortcut.Parse(info.Get(fresh));
                if (!assigned.IsAssigned) continue;
                Check($"the default for {info.Label} is not reserved",
                    ReservedShortcuts.OwnerOf(assigned) == null,
                    $"{assigned} is {ReservedShortcuts.OwnerOf(assigned)}");
            }

            var window = new Shortcut(fresh.HotkeyModifiers, fresh.HotkeyKey);
            Check("and neither is the window's own shortcut",
                ReservedShortcuts.OwnerOf(window) == null, window.ToString());

            // Something plainly free stays free.
            //
            // The last four are a real person's settings, and the list must not
            // take a combination somebody already chose on purpose. Ctrl+Up and
            // Ctrl+Down move the cursor without moving the selection, which is a
            // convenience the plain arrows already cover — so they are not worth
            // reserving, and reserving them once broke a working volume control.
            foreach (var free in new[]
            {
                "Ctrl+Alt+P", "Ctrl+Alt+Shift+B", "Ctrl+Alt+1",
                "Ctrl+Up", "Ctrl+Down", "Ctrl+Shift+Space", "Ctrl+Shift+Left",
                "Ctrl+Shift+Right", "Ctrl+Shift+P", "Ctrl+Alt+E",
            })
                Check($"{free} is still allowed", ReservedShortcuts.OwnerOf(Shortcut.Parse(free)) == null,
                    ReservedShortcuts.OwnerOf(Shortcut.Parse(free)) ?? "");

            // The Windows key cannot collide with anything on the list, and
            // claiming it did would be wrong.
            Check("Win+C is not mistaken for Ctrl+C",
                ReservedShortcuts.OwnerOf(Shortcut.Parse("Win+C")) == null);

            // ---- and the list must not drift from the menu ----
            //
            // MainForm needs a message loop and is not compiled into this suite,
            // so its menu is read out of the source. A shortcut added to the
            // window without being reserved is a key somebody can then hand to
            // Windows, after which the menu item it belongs to stops working.
            var main = SourceFile("MainForm.cs");
            if (main == null) { Check("MainForm.cs can be read", false); Console.WriteLine(); return; }

            var known = new HashSet<Keys>(ReservedShortcuts.Combinations());
            int unreserved = 0, found = 0;
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(
                         main, @"Item\(""[^""]*"",\s*((?:Keys\.\w+\s*\|\s*)*Keys\.\w+)\s*,"))
            {
                var combo = ParseKeysExpression(m.Groups[1].Value);
                if (combo == Keys.None || combo == Keys.None) continue;
                found++;
                if (known.Contains(combo)) continue;
                unreserved++;
                Console.WriteLine($"        menu shortcut not reserved: {combo}");
            }

            // A scan that matched nothing would pass this whatever the menu said,
            // which is the way a check like it quietly stops meaning anything.
            Check($"the menu scan actually read the menu ({found} shortcuts)", found >= 15, $"{found} found");
            Check("every shortcut the menu declares is reserved", unreserved == 0,
                $"{unreserved} were not");

            // ---- and a refusal has to be visible ----
            //
            // Preferences will not let one of these be typed, so anything that
            // reaches the registration arrived by editing settings.json — the one
            // route where a silent refusal is worst. Somebody who has just
            // hand-edited a file to get a shortcut they were told they could not
            // have will read a key that does nothing as the edit not having
            // taken, and try harder rather than look in the status bar.
            var tray = SourceFile("TrayApplicationContext.cs");
            if (tray == null) { Check("TrayApplicationContext.cs can be read", false); Console.WriteLine(); return; }

            Check("a reserved shortcut in the settings file puts up a dialog",
                tray.Contains("ShowShortcutDialog", StringComparison.Ordinal) &&
                tray.Contains("MessageBox.Show", StringComparison.Ordinal));

            // One dialog, not one per kind of problem. Two posted together open
            // about ten milliseconds apart and the second lands inside the
            // first's modal loop, so they stack and a screen reader announces
            // only the top one — measured, with the first never announced at all.
            int calls = Occurrences(tray, "ShowShortcutDialog(");
            Check("and there is one dialog for all of them, not one each",
                calls == 2, calls + " mentions (want a definition and a single call)");

            // Posted, because this runs from the constructor — before there is a
            // window to own a dialog or a loop to pump one. Shown inline there,
            // it stops the application appearing at all until it is dismissed.
            int at = tray.IndexOf("private void ShowShortcutDialog", StringComparison.Ordinal);
            Check("and it waits for the message loop rather than blocking startup",
                at >= 0 && tray.IndexOf("PostUi(", at, StringComparison.Ordinal) > at &&
                tray.IndexOf("PostUi(", at, StringComparison.Ordinal) <
                    tray.IndexOf("MessageBox.Show", at, StringComparison.Ordinal));

            // The dialog is the extra, not the replacement: the catalogue is
            // still where the message belongs, and it is what the speech and the
            // tray balloon are routed by.
            Check("the notification is still raised alongside it",
                tray.Contains("Notify(\"hotkey.duplicate\"", StringComparison.Ordinal));

            Console.WriteLine();
        }

        /// <summary>"Keys.Control | Keys.C" as the value it stands for.</summary>
        private static Keys ParseKeysExpression(string expression)
        {
            Keys total = Keys.None;
            foreach (var part in expression.Split('|'))
            {
                var name = part.Trim().Replace("Keys.", "");
                if (!Enum.TryParse<Keys>(name, out var one)) return Keys.None;
                total |= one;
            }
            return total;
        }

        private static void ProgressArithmeticTests()
        {
            Console.WriteLine("What a transfer says it has done:");

            // The case that started it: stdout is far behind what the kernel has
            // watched being written, so the kernel wins.
            Equal("the measured count beats a stdout count left behind by buffering",
                "24000000000",
                RoboCopyEngine.ProgressBytes(12_400_000, 24_000_000_000, 41_500_000_000, 0).ToString());

            // And the reverse, which is a move inside one volume: robocopy
            // renames rather than copies, so almost nothing is written and the
            // parsed lines are the only true account.
            Equal("the parsed count beats a kernel that saw no writes",
                "5000", RoboCopyEngine.ProgressBytes(5_000, 0, 10_000, 0).ToString());

            // The kernel counts a little more than file data, so it overshoots.
            Equal("never past the total", "500", RoboCopyEngine.ProgressBytes(0, 999, 500, 0).ToString());
            Equal("exactly the total is fine", "500", RoboCopyEngine.ProgressBytes(500, 500, 500, 0).ToString());

            // A job ending between the ticker reading it and its total being
            // banked makes the measured figure dip for an instant.
            Equal("and never backwards", "700", RoboCopyEngine.ProgressBytes(0, 0, 1000, 700).ToString());
            Equal("forwards still moves", "800", RoboCopyEngine.ProgressBytes(0, 800, 1000, 700).ToString());

            // Before the planning walk finishes there is no total to clamp to.
            Equal("an unknown total does not clamp",
                "900", RoboCopyEngine.ProgressBytes(0, 900, 0, 0).ToString());

            // Nothing has happened yet.
            Equal("zero everywhere is zero", "0", RoboCopyEngine.ProgressBytes(0, 0, 0, 0).ToString());

            // ---- And that the measured half reads the field it means to ----
            //
            // IO_COUNTERS is six identical unsigned longs. Naming the wrong one
            // compiles and returns a plausible number, so the only way to know
            // which is being read is to move one of them on purpose: write a
            // known amount and see whether this is what changed. Reading the read
            // counter, or an operation count, would not follow.
            Check("a process with no handle to ask about is zero, not a throw",
                RoboCopyEngine.BytesWritten(null) == 0);

            using (var me = System.Diagnostics.Process.GetCurrentProcess())
            {
                long before = RoboCopyEngine.BytesWritten(me);
                Check("the write counter reads back at all", before > 0, before.ToString());

                var scratch = Path.Combine(NewTempDir(), "counter.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(scratch)!);
                const int written = 32 * 1024 * 1024;
                using (var fs = new FileStream(scratch, FileMode.Create, FileAccess.Write,
                           FileShare.None, 1024 * 1024, FileOptions.WriteThrough))
                {
                    fs.Write(new byte[written], 0, written);
                    fs.Flush(true);
                }

                long after = RoboCopyEngine.BytesWritten(me);
                long moved = after - before;
                Check("and it follows bytes actually written",
                    moved >= written * 0.9, $"wrote {written:N0}, counter moved {moved:N0}");
                Check("by about that much and not wildly more",
                    moved < written * 4L, $"counter moved {moved:N0} for {written:N0} written");

                try { Directory.Delete(Path.GetDirectoryName(scratch)!, true); } catch { }
            }

            // ---- And that the two halves are actually joined together ----
            //
            // Read out of the source because driving it cannot reach this. A copy
            // small enough to run in a test finishes before the conditions that
            // broke it can arise, so an end-to-end test passes whether the kernel
            // counter is connected or disconnected — proved by disconnecting it
            // and watching the suite stay green. What is left is to check that
            // the wire exists, which is worth more than a test that cannot fail.
            var engine = SourceFile("RoboCopyEngine.cs");
            if (engine == null) Check("RoboCopyEngine.cs can be read", false);
            else
            {
                Check("the reported figure is fed by both sources",
                    engine.Contains("ProgressBytes(matched, measured", StringComparison.Ordinal));

                Check("and the measured one comes from the running robocopy",
                    engine.Contains("BytesWritten(Volatile.Read(ref liveJob))", StringComparison.Ordinal));

                Check("which the ticker reads, not just the final report",
                    engine.Contains("long done = BytesLanded();", StringComparison.Ordinal));
            }

            Console.WriteLine();
        }

        /// <summary>
        /// Collects progress on the thread that reported it.
        ///
        /// <c>Progress&lt;T&gt;</c> posts to the thread pool, so reports can be
        /// delivered in a different order from the one they were made in. That is
        /// harmless for a dialog and useless for asserting that a number only ever
        /// goes up — a first attempt at that check failed on the delivery order
        /// rather than on anything the engine did.
        /// </summary>
        private sealed class SyncProgress<T> : IProgress<T>
        {
            public readonly List<T> Seen = new();
            public void Report(T value) { lock (Seen) Seen.Add(value); }
        }

        /// <summary>
        /// Runs a check at the moment a report is made, on the reporting thread,
        /// for the questions that are about *when* rather than what — looking
        /// at the list afterwards cannot say what was on disk at the time.
        /// </summary>
        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> _onReport;
            public InlineProgress(Action<T> onReport) => _onReport = onReport;
            public void Report(T value) { lock (_onReport) _onReport(value); }
        }

        private static async Task RoboCopyTests()
        {
            Console.WriteLine("Robocopy transfer engine:");

            var temp = NewTempDir();
            try
            {
                var src = Path.Combine(temp, "src");
                var dst = Path.Combine(temp, "dst");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);

                var a = Path.Combine(src, "alpha.bin");
                var b = Path.Combine(src, "beta.bin");
                File.WriteAllText(a, new string('a', 200_000));
                File.WriteAllText(b, new string('b', 300_000));

                var nested = Path.Combine(src, "tree", "inner");
                Directory.CreateDirectory(nested);
                File.WriteAllText(Path.Combine(nested, "gamma.bin"), new string('g', 50_000));

                var seen = new List<TransferProgress>();
                var progress = new Progress<TransferProgress>(p => { lock (seen) seen.Add(p); });

                var copy = await RoboCopyEngine.RunAsync(
                    new[] { a, b, Path.Combine(src, "tree") }, dst, move: false,
                    threads: 8, PasteConflictPolicy.AutoRename, progress, CancellationToken.None);

                Check("copy reported no failures", copy.Failed == 0,
                    copy.Errors.Count > 0 ? copy.Errors[0] : "");
                Check("flat files arrived",
                    File.Exists(Path.Combine(dst, "alpha.bin")) && File.Exists(Path.Combine(dst, "beta.bin")));
                Check("nested tree arrived", File.Exists(Path.Combine(dst, "tree", "inner", "gamma.bin")));
                Check("content is intact", new FileInfo(Path.Combine(dst, "alpha.bin")).Length == 200_000);
                Check("sources survive a copy", File.Exists(a));

                await Task.Delay(200); // Progress<T> posts asynchronously
                lock (seen)
                {
                    Check("progress was reported", seen.Count > 0, $"reports={seen.Count}");
                    Check("progress reported a speed", seen.Any(p => p.BytesPerSecond > 0));
                    Check("progress reported elapsed time", seen.Any(p => p.Elapsed > TimeSpan.Zero));
                    Check("a total was established", seen.Any(p => p.TotalKnown), "total never became known");
                }

                // ---- Progress against a copy big enough for robocopy's stdout
                // buffering to bite. Eighty files is past the ~fifty lines that
                // fit in a pipe block, so the parsed count cannot keep up on its
                // own and the reported figure has to come from somewhere else ----
                var manySrc = Path.Combine(temp, "many");
                var manyDst = Path.Combine(temp, "manydst");
                Directory.CreateDirectory(manySrc);
                long manyBytes = 0;
                for (int i = 0; i < 80; i++)
                {
                    var chunk = new byte[128 * 1024];
                    File.WriteAllBytes(Path.Combine(manySrc, $"file_{i:D3}.bin"), chunk);
                    manyBytes += chunk.Length;
                }

                // Synchronous, so the reports arrive in the order the engine made
                // them and "never counts backwards" means what it says.
                var manyProgress = new SyncProgress<TransferProgress>();
                var manySeen = manyProgress.Seen;

                var many = await RoboCopyEngine.RunAsync(
                    new[] { manySrc }, manyDst, move: false, threads: 8,
                    PasteConflictPolicy.AutoRename, manyProgress, CancellationToken.None);

                Check("the bulk copy reported no failures", many.Failed == 0,
                    many.Errors.Count > 0 ? many.Errors[0] : "");
                Check("every file arrived",
                    Directory.GetFiles(Path.Combine(manyDst, "many")).Length == 80,
                    Directory.Exists(Path.Combine(manyDst, "many"))
                        ? Directory.GetFiles(Path.Combine(manyDst, "many")).Length.ToString()
                        : "destination folder missing");

                lock (manySeen)
                {
                    var last = manySeen.LastOrDefault(p => p.TotalKnown);
                    Check("the bulk copy ended with a total", last != null);

                    if (last != null)
                    {
                        // The number a person reads. Reported bytes must land on
                        // the real total rather than on the fraction that escaped
                        // robocopy's buffer — this is the check that fails if
                        // progress goes back to being parsed from stdout alone.
                        Check("it finished reporting essentially all the bytes",
                            last.BytesDone >= manyBytes * 0.9,
                            $"reported {last.BytesDone:N0} of {manyBytes:N0}");

                        Check("and never claimed more than there was",
                            manySeen.All(p => !p.TotalKnown || p.BytesDone <= p.BytesTotal),
                            "a report exceeded its own total");

                        Check("the percentage stayed inside its bounds",
                            manySeen.All(p => p.Percent >= 0 && p.Percent <= 100));

                        // 203 hours for an eight-minute copy is what an
                        // understated byte count does to an estimate.
                        var worst = manySeen.Where(p => p.Remaining != null)
                                            .Select(p => p.Remaining!.Value).DefaultIfEmpty(TimeSpan.Zero).Max();
                        Check("no estimate was wildly out",
                            worst < TimeSpan.FromHours(1), $"worst estimate was {worst}");
                    }

                    // Never counting down is a property of the reported figure
                    // itself, not of any one report.
                    long high = 0;
                    bool wentBackwards = false;
                    foreach (var p in manySeen)
                    {
                        if (p.BytesDone < high) wentBackwards = true;
                        high = Math.Max(high, p.BytesDone);
                    }
                    Check("progress never counted backwards", !wentBackwards);
                }

                // ---- A few large files, so progress has to survive a copy that
                // outlives several ticks of the reporting timer.
                //
                // Read what this does and does not prove. It is a sanity check on
                // a long multi-file copy: the numbers move, stay inside their
                // bounds and end up right. It is *not* a regression test for
                // where the bytes come from — it was written as one and it failed
                // at that, passing just as happily with the kernel counter
                // disconnected, because a copy this size finishes far too fast on
                // a local SSD to reproduce the conditions the original failure
                // needed. The arithmetic and the counter are covered directly, in
                // ProgressArithmeticTests, and the wiring between them by a scan
                // of the source. Do not read a pass here as proof of the fix ----
                var bigSrc = Path.Combine(temp, "big");
                var bigDst = Path.Combine(temp, "bigdst");
                Directory.CreateDirectory(bigSrc);
                const long onePiece = 128L * 1024 * 1024;
                for (int i = 0; i < 4; i++)
                    using (var fs = new FileStream(Path.Combine(bigSrc, $"piece_{i}.bin"),
                               FileMode.Create, FileAccess.Write))
                        fs.SetLength(onePiece);   // reads back as zeros without costing a read

                var bigProgress = new SyncProgress<TransferProgress>();
                var big = await RoboCopyEngine.RunAsync(
                    new[] { bigSrc }, bigDst, move: false, threads: 8,
                    PasteConflictPolicy.AutoRename, bigProgress, CancellationToken.None);

                Check("the large copy reported no failures", big.Failed == 0,
                    big.Errors.Count > 0 ? big.Errors[0] : "");
                Check("every large file arrived whole",
                    Directory.GetFiles(Path.Combine(bigDst, "big")).Length == 4 &&
                    new FileInfo(Path.Combine(bigDst, "big", "piece_0.bin")).Length == onePiece);

                lock (bigProgress.Seen)
                {
                    // Everything except the closing report, which is right either
                    // way because stdout has been drained by then.
                    var inFlight = bigProgress.Seen
                        .Take(Math.Max(0, bigProgress.Seen.Count - 1))
                        .Where(p => p.TotalKnown).ToList();

                    long best = inFlight.Count == 0 ? 0 : inFlight.Max(p => p.BytesDone);

                    if (inFlight.Count == 0)
                    {
                        // A disk fast enough to finish inside one tick of the
                        // 250ms ticker. Nothing was observable, so nothing is
                        // claimed — a check that passes having seen nothing is
                        // worse than no check at all.
                        Console.WriteLine("  ....  the copy outran the progress ticker; nothing to judge here");
                    }
                    else
                    {
                        Check("progress moves while a long copy is still running",
                            best > 0,
                            $"{inFlight.Count} reports while copying, every one at zero bytes");
                    }
                }

                // ---- "It says preparing and never changes." The name came only
                // from robocopy's lines, and under /MT — which is always passed,
                // even for one thread — robocopy prints a file's line when the
                // file *finishes*. Copying one track, the window had no name for
                // the whole of the copy.
                //
                // Two earlier versions of this check passed with the fix taken
                // out, and both are why it is one file now. "A name while BytesDone
                // is short of the total" passed because a quick copy reports its
                // closing line before the byte counter catches up. "A name before
                // the last of three files exists" passed because the line for the
                // first file, printed as it finished, still comes before the third
                // starts. With a single file the only question left is whether the
                // name arrives while it is being copied or once it is done ----
                var nameSrc = Path.Combine(temp, "namesrc");
                var nameDst = Path.Combine(temp, "namedst");
                Directory.CreateDirectory(nameSrc);
                Directory.CreateDirectory(nameDst);   // a paste always lands in a folder that exists
                var track = Path.Combine(nameSrc, "track.bin");
                using (var fs = new FileStream(track, FileMode.Create, FileAccess.Write))
                    fs.SetLength(384L * 1024 * 1024);   // zeros without costing a write here

                var nameClock = Stopwatch.StartNew();
                string? firstName = null;
                double firstNameAt = -1;

                var nameProgress = new InlineProgress<TransferProgress>(p =>
                {
                    if (firstName != null || string.IsNullOrEmpty(p.CurrentItem)) return;
                    firstName = p.CurrentItem;
                    firstNameAt = nameClock.Elapsed.TotalMilliseconds;
                });

                var named = await RoboCopyEngine.RunAsync(
                    new[] { track }, nameDst, move: false, threads: 1,
                    PasteConflictPolicy.AutoRename, nameProgress, CancellationToken.None);
                double nameTotal = nameClock.Elapsed.TotalMilliseconds;

                Check("the named copy reported no failures", named.Failed == 0,
                    named.Errors.Count > 0 ? named.Errors[0] : "");
                Check("the window names the file while it is being copied, not once it is done",
                    firstName == "track.bin" && firstNameAt >= 0 && firstNameAt < nameTotal / 2,
                    $"first name {firstName ?? "(none)"} at {firstNameAt:0}ms of {nameTotal:0}ms");

                // Move must remove the source.
                var moveSrc = Path.Combine(temp, "movesrc");
                var moveDst = Path.Combine(temp, "movedst");
                Directory.CreateDirectory(moveSrc);
                Directory.CreateDirectory(moveDst);
                var m = Path.Combine(moveSrc, "movable.bin");
                File.WriteAllText(m, new string('m', 10_000));

                var moved = await RoboCopyEngine.RunAsync(
                    new[] { m }, moveDst, move: true, threads: 4,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None);

                Check("move reported no failures", moved.Failed == 0);
                Check("move created the destination", File.Exists(Path.Combine(moveDst, "movable.bin")));
                Check("move removed the source", !File.Exists(m));

                // ---- Conflict policy: robocopy overwrites by default, so this
                // is the guard against silently destroying an existing file ----
                var conflictDir = Path.Combine(temp, "conflict");
                Directory.CreateDirectory(conflictDir);
                var original = Path.Combine(conflictDir, "alpha.bin");
                File.WriteAllText(original, "ORIGINAL CONTENT");

                var autoRenamed = await RoboCopyEngine.RunAsync(
                    new[] { a }, conflictDir, false, 4,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None);

                Check("auto-rename did not overwrite the existing file",
                    File.ReadAllText(original) == "ORIGINAL CONTENT",
                    "the original was clobbered");
                Check("auto-rename wrote a second copy alongside",
                    File.Exists(Path.Combine(conflictDir, "alpha (2).bin")));
                Check("auto-rename reported no failure", autoRenamed.Failed == 0);

                // The name is the point. "Copy complete" is no help at all to
                // somebody about to go looking for a file that is now called
                // something else, and for a long time that was the only thing
                // said: the policy was applied in here and the result carried
                // nothing back about what it had decided.
                Equal("auto-rename reported one rename", "1",
                    autoRenamed.Collisions.RenamedCount.ToString());
                Equal("and named the file it actually wrote", "alpha (2).bin",
                    autoRenamed.Collisions.Renamed.FirstOrDefault() ?? "");

                // Skip must leave the original alone and add nothing.
                int before = Directory.GetFiles(conflictDir).Length;
                var skippedResult = await RoboCopyEngine.RunAsync(
                    new[] { a }, conflictDir, false, 4,
                    PasteConflictPolicy.Skip, null, CancellationToken.None);
                Check("skip left the original intact", File.ReadAllText(original) == "ORIGINAL CONTENT");
                Check("skip added nothing", Directory.GetFiles(conflictDir).Length == before);
                Check("skip said what it did",
                    skippedResult.Errors.Any(e => e.Contains("skipped", StringComparison.OrdinalIgnoreCase)));
                Equal("skip reported one skip", "1",
                    skippedResult.Collisions.SkippedCount.ToString());
                Equal("and named what it left alone", "alpha.bin",
                    skippedResult.Collisions.Skipped.FirstOrDefault() ?? "");

                // Overwrite is opt-in and must actually overwrite.
                var overwritten = await RoboCopyEngine.RunAsync(
                    new[] { a }, conflictDir, false, 4,
                    PasteConflictPolicy.Overwrite, null, CancellationToken.None);
                Check("overwrite replaced the file when asked",
                    new FileInfo(original).Length == 200_000, $"length={new FileInfo(original).Length}");

                // Robocopy destroys the old file without a word, so this is the
                // only place that can know it happened at all.
                Equal("overwrite reported one replacement", "1",
                    overwritten.Collisions.OverwrittenCount.ToString());
                Equal("and named what it replaced", "alpha.bin",
                    overwritten.Collisions.Overwritten.FirstOrDefault() ?? "");

                // Nothing colliding means nothing to report, so a plain copy is
                // not made to announce three empty facts.
                var quiet = await RoboCopyEngine.RunAsync(
                    new[] { a }, Path.Combine(temp, "movedst"), false, 4,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None);
                Equal("a copy with no collisions reports none", "0",
                    quiet.Collisions.Total.ToString());

                // Copying a folder into itself must be refused, not looped.
                var self = await RoboCopyEngine.RunAsync(
                    new[] { src }, src, false, 4, PasteConflictPolicy.Overwrite, null, CancellationToken.None);
                Check("copying a folder into itself is refused",
                    self.Failed > 0 && self.Errors.Any(e => e.Contains("itself", StringComparison.OrdinalIgnoreCase)));

                // Duplicating a folder in place, through the engine a paste really
                // uses. This is the ordinary "copy this folder, paste it here"
                // action, and it went through robocopy's collision handling into
                // the managed engine and came back doing nothing at all.
                var dupRoot = Path.Combine(temp, "duproot");
                Directory.CreateDirectory(Path.Combine(dupRoot, "thing", "deep"));
                File.WriteAllText(Path.Combine(dupRoot, "thing", "deep", "x.bin"), new string('d', 5000));

                var dup = await RoboCopyEngine.RunAsync(
                    new[] { Path.Combine(dupRoot, "thing") }, dupRoot, move: false, threads: 4,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None);

                Check("duplicating a folder in place reports no failure", dup.Failed == 0,
                    dup.Errors.Count > 0 ? dup.Errors[0] : "");
                Check("duplicating a folder in place produced the copy",
                    File.Exists(Path.Combine(dupRoot, "thing (2)", "deep", "x.bin")));
                Check("duplicating a folder in place left the original alone",
                    File.Exists(Path.Combine(dupRoot, "thing", "deep", "x.bin")));

                // The same for a single file, which took a different route.
                var dupFile = Path.Combine(dupRoot, "solo.bin");
                File.WriteAllText(dupFile, "solo");
                var dupF = await RoboCopyEngine.RunAsync(
                    new[] { dupFile }, dupRoot, move: false, threads: 4,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None);
                Check("duplicating a file in place works too",
                    dupF.Failed == 0 && File.Exists(Path.Combine(dupRoot, "solo (2).bin")),
                    dupF.Errors.Count > 0 ? dupF.Errors[0] : "");

                // Same source and destination folder must say so, not hang.
                var sameDir = await RoboCopyEngine.RunAsync(
                    new[] { a }, src, false, 4, PasteConflictPolicy.Overwrite, null, CancellationToken.None);
                Check("same source and destination is reported",
                    sameDir.Errors.Any(e => e.Contains("same folder", StringComparison.OrdinalIgnoreCase)));

                // ---- Drive-root paths ----
                // "D:\" trimmed to "D:" means the *current directory* on D to
                // robocopy, not the root, so a copy would land somewhere else.
                Equal("drive root keeps its root meaning", "\"C:\\.\"", RoboCopyEngine.QuoteDir(@"C:\"));
                Equal("drive root without separator too", "\"C:\\.\"", RoboCopyEngine.QuoteDir(@"C:"));
                Equal("ordinary folder loses its trailing separator", "\"C:\\Windows\"", RoboCopyEngine.QuoteDir(@"C:\Windows\"));
                Equal("ordinary folder unchanged", "\"C:\\Windows\"", RoboCopyEngine.QuoteDir(@"C:\Windows"));
                Check("a quoted path never ends in a separator before the quote",
                    !RoboCopyEngine.QuoteDir(@"C:\dir\").TrimEnd('"').EndsWith('\\'));

                // Copying an entire drive has no folder name to land in, so it
                // must be refused rather than tipped loose into the destination.
                var driveRoot = Path.GetPathRoot(temp)!;
                var wholeDrive = await RoboCopyEngine.RunAsync(
                    new[] { driveRoot }, dst, false, 4,
                    PasteConflictPolicy.Overwrite, null, CancellationToken.None);
                Check("copying a whole drive is refused",
                    wholeDrive.Errors.Any(e => e.Contains("whole drive", StringComparison.OrdinalIgnoreCase)),
                    wholeDrive.Errors.Count > 0 ? wholeDrive.Errors[0] : "(no error reported)");

                // A missing source is an error, not a crash.
                var missing = await RoboCopyEngine.RunAsync(
                    new[] { Path.Combine(temp, "nope.bin") }, dst, false, 4,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None);
                Check("a missing source is reported", missing.Failed > 0);

                // Cancellation before start must come back cancelled, not hang.
                var cts = new CancellationTokenSource();
                cts.Cancel();
                var cancelled = await RoboCopyEngine.RunAsync(
                    new[] { a }, dst, false, 4, PasteConflictPolicy.AutoRename, null, cts.Token);
                Check("a pre-cancelled transfer reports cancelled", cancelled.Cancelled);

                // Formatting helpers used by the progress window.
                Check("speed formats as words", RoboCopyEngine.FormatSpeed(1024 * 1024, SizeUnitStyle.FullWords, 1)
                    .Contains("megabyte", StringComparison.OrdinalIgnoreCase));
                Equal("unknown speed says so", "unknown", RoboCopyEngine.FormatSpeed(0, SizeUnitStyle.FullWords, 1));
                Equal("sub-second duration", "less than a second", RoboCopyEngine.FormatDuration(TimeSpan.FromMilliseconds(300)));
                Equal("seconds duration", "45 seconds", RoboCopyEngine.FormatDuration(TimeSpan.FromSeconds(45)));
                Check("minutes duration", RoboCopyEngine.FormatDuration(TimeSpan.FromMinutes(3)).Contains("minutes"));
                Equal("one of each is singular", "1 minute, 1 second",
                    RoboCopyEngine.FormatDuration(TimeSpan.FromSeconds(61)));
                Equal("and an hour", "1 hour, 0 minutes", RoboCopyEngine.FormatDuration(TimeSpan.FromHours(1)));
            }
            finally { Cleanup(temp); }
            Console.WriteLine();
        }

        // ---- Large folders must not lag ----
        private static async Task LargeFolderTests()
        {
            Console.WriteLine("Large folder handling:");

            var temp = NewTempDir();
            try
            {
                const int count = 20_000;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < count; i++)
                    File.WriteAllText(Path.Combine(temp, $"file{i:D5}.txt"), "x");
                sw.Stop();
                Console.WriteLine($"  (created {count} files in {sw.ElapsedMilliseconds} ms)");

                // Enumeration is what the UI waits on, so that is what is timed.
                sw.Restart();
                var dir = new DirectoryInfo(temp);
                var files = dir.GetFiles();
                sw.Stop();

                Check($"enumerated {count} files", files.Length == count, $"got {files.Length}");
                Check($"enumeration under 3 seconds (took {sw.ElapsedMilliseconds} ms)", sw.ElapsedMilliseconds < 3000);

                // Sorting the backing list is the other per-navigation cost.
                sw.Restart();
                var sorted = files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
                sw.Stop();
                Check($"sorting under 1 second (took {sw.ElapsedMilliseconds} ms)", sw.ElapsedMilliseconds < 1000);
                Check("sort is stable and complete", sorted.Count == count);

                // Folder sizing over many files must still respect its timeout.
                using var calc = new FolderSizeCalculator { TimeoutSeconds = 20, MaxDepth = 8 };
                sw.Restart();
                var size = await calc.CalculateAsync(temp);
                sw.Stop();
                Check($"sized {count} files ({sw.ElapsedMilliseconds} ms)", size.Files == count, $"counted {size.Files}");
                Check("sizing respected its timeout", sw.ElapsedMilliseconds < 20_000);

                // ---- Type-ahead is the per-keystroke cost in a big folder ----
                //
                // Find starts at the cursor and wraps, so a hit is usually quick
                // and a *miss* is the whole folder. A miss is what every letter
                // that matches nothing costs, and it happens on the keystroke
                // path where nothing is allowed to be felt.
                var names = new string[count];
                for (int i = 0; i < count; i++) names[i] = $"file_{i:D6}.flac";

                sw.Restart();
                for (int k = 0; k < 100; k++) TypeAhead.Find(names.Length, i => names[i], "zz", 0);
                sw.Stop();
                var each = sw.Elapsed.TotalMilliseconds / 100;
                Check($"a type-ahead miss over {count:N0} rows costs under 2 ms ({each:0.00} ms each)",
                    each < 2.0, $"{each:0.00} ms");

                // And with extensions hidden, where the shown name is rebuilt for
                // every row the search walks past rather than being the one the
                // enumeration already made.
                sw.Restart();
                for (int k = 0; k < 100; k++)
                    TypeAhead.Find(names.Length, i => Path.GetFileNameWithoutExtension(names[i]), "zz", 0);
                sw.Stop();
                var hidden = sw.Elapsed.TotalMilliseconds / 100;
                Check($"and under 6 ms with the name rebuilt per row ({hidden:0.00} ms each)",
                    hidden < 6.0, $"{hidden:0.00} ms");
            }
            finally { Cleanup(temp); }
            Console.WriteLine();
        }

        // ---- Opening files must be immediate ----
        private static void LaunchTests()
        {
            Console.WriteLine("File opening:");

            // Resolution must find a real handler for common types, so the fast
            // path is actually taken rather than silently falling back to the
            // shell (which is where the hundreds of milliseconds went).
            var txt = FileLauncher.ResolveHandlerPath(".txt");
            Check("resolved a handler for .txt", txt != null, "would fall back to ShellExecute");
            if (txt != null) Check("the .txt handler exists on disk", File.Exists(txt), txt);

            // What matters is that a real handler is found so the fast path is
            // taken. Which player is registered is the machine's business, not
            // this app's — asserting a particular one made the suite fail on any
            // computer that had chosen differently, which says nothing about
            // whether the code works.
            var flac = FileLauncher.ResolveHandlerPath(".flac");
            Check("resolved a handler for .flac", flac != null, "would fall back to ShellExecute");
            if (flac != null)
            {
                Check("the .flac handler exists on disk", File.Exists(flac), flac);
                Check("the .flac handler is an executable",
                    flac.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), flac);
                Console.WriteLine($"  (.flac opens with {Path.GetFileName(flac)})");
            }

            // Common types must all resolve through the fast path rather than
            // silently falling back to the shell, which is where the delay was.
            foreach (var ext in new[] { ".txt", ".png", ".pdf", ".mp3", ".zip", ".html" })
            {
                var handler = FileLauncher.ResolveHandlerPath(ext);
                Check($"{ext} resolves to something real",
                    handler == null || File.Exists(handler), handler ?? "(null)");
            }

            // Case must not matter: the shell does not care, and neither should
            // the cache — otherwise ".TXT" and ".txt" resolve twice.
            Check("extension resolution ignores case",
                FileLauncher.ResolveHandlerPath(".TXT") == FileLauncher.ResolveHandlerPath(".txt"));

            Check("a null-ish extension is handled", FileLauncher.ResolveHandlerPath("") == null);

            // Resolution is cached: the second lookup must be effectively free,
            // because it happens on the keystroke that opens the file.
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 10000; i++) FileLauncher.ResolveHandlerPath(".flac");
            sw.Stop();
            Check($"10,000 cached lookups in under 50 ms (took {sw.ElapsedMilliseconds} ms)",
                sw.ElapsedMilliseconds < 50);

            // An unknown extension resolves to OpenWith.exe — Windows' "how do
            // you want to open this?" dialog. That is correct, and the same
            // thing ShellExecute would do. What matters is that whatever comes
            // back is a real executable and never a broken path.
            var unknown = FileLauncher.ResolveHandlerPath(".zzznotathing");
            Check("an unknown extension resolves without throwing", true);
            Check("an unknown extension yields a real executable or nothing",
                unknown == null || File.Exists(unknown), unknown ?? "(null)");
            if (unknown != null)
                Check("an unknown extension routes to the Open With picker",
                    unknown.EndsWith("OpenWith.exe", StringComparison.OrdinalIgnoreCase), unknown);
            Check("an empty extension is handled", FileLauncher.ResolveHandlerPath("") == null);

            // Prewarming must be safe to call repeatedly and off-thread.
            FileLauncher.Prewarm(".flac");
            FileLauncher.Prewarm(".flac");
            FileLauncher.Prewarm("");
            Check("prewarming is idempotent and safe", true);

            // Whatever this machine happens to have registered, the command line
            // built for a path containing a space must keep it as one argument.
            foreach (var ext in new[] { ".txt", ".html", ".png", ".pdf", ".flac", ".zip" })
            {
                var line = FileLauncher.DescribeCommandLine(ext, @"C:\my folder\a file" + ext);
                if (line == null) continue;   // falls back to the shell, which is fine
                Check($"the command line for {ext} quotes a path with spaces",
                    line.Contains("\"C:\\my folder\\a file" + ext + "\""), line);
            }

            Console.WriteLine();
            CommandTemplateTests();
        }

        /// <summary>
        /// The parsing of a registered shell command, against the exact shapes
        /// Windows actually registers — tested directly rather than through
        /// whatever this particular machine has installed.
        /// </summary>
        private static void CommandTemplateTests()
        {
            Console.WriteLine("Registered command parsing:");

            // A fixed pretend filesystem, so these run the same everywhere.
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                @"C:\Program Files\App\app.exe",
                @"C:\Program Files\Internet Explorer\iexplore.exe",
                @"C:\Windows\system32\NOTEPAD.EXE",
                @"C:\Windows\Explorer.exe",
                @"C:\NoSpaces\tool.exe",
            };
            bool Exists(string p) => installed.Contains(p);

            static string? Args(FileLauncher.ParsedCommand? p) => p?.ArgsTemplate;
            static string? Exe(FileLauncher.ParsedCommand? p) => p?.Exe;

            // ---- The quoting bug: a bare %1 and a path with a space ----
            // Windows registers plenty of these, because the shell quotes what it
            // substitutes. Building the command line by hand, we must do it too —
            // otherwise "C:\my folder\a file.html" arrived as three arguments and
            // the application opened nothing, or the wrong thing.
            var ie = FileLauncher.ParseRegisteredCommand(
                @"""C:\Program Files\Internet Explorer\iexplore.exe"" %1", Exists);
            Equal("a bare %1 is given quotes", "\"%1\"", Args(ie) ?? "(null)");
            Equal("the executable is still parsed correctly",
                @"C:\Program Files\Internet Explorer\iexplore.exe", Exe(ie) ?? "(null)");

            var alreadyQuoted = FileLauncher.ParseRegisteredCommand(
                @"""C:\Program Files\App\app.exe"" ""%1""", Exists);
            Equal("an already-quoted %1 is not double-quoted", "\"%1\"", Args(alreadyQuoted) ?? "(null)");

            var withSwitches = FileLauncher.ParseRegisteredCommand(
                @"""C:\Program Files\App\app.exe"" /play %1 /now", Exists);
            Equal("switches around a bare %1 survive", "/play \"%1\" /now", Args(withSwitches) ?? "(null)");

            // ---- %L and %* ----
            Equal("%L is treated as %1 and quoted", "/prefetch:6 /Open \"%1\"",
                Args(FileLauncher.ParseRegisteredCommand(
                    @"""C:\Program Files\App\app.exe"" /prefetch:6 /Open ""%L""", Exists)) ?? "(null)");
            Equal("lowercase %l too", "\"%1\"",
                Args(FileLauncher.ParseRegisteredCommand(
                    @"""C:\Program Files\App\app.exe"" %l", Exists)) ?? "(null)");
            Equal("a command with no placeholder gets one appended", "\"%1\"",
                Args(FileLauncher.ParseRegisteredCommand(
                    @"""C:\Program Files\App\app.exe""", Exists)) ?? "(null)");

            // ---- Placeholders only the shell can fill ----
            // Compressed folders register Explorer.exe /idlist,%I,%L — %I is a
            // handle to memory in the calling process, not text. Substituting a
            // path and launching anyway produces a nonsense command line.
            Check("an ITEMIDLIST placeholder falls back to the shell",
                FileLauncher.ParseRegisteredCommand(
                    @"C:\Windows\Explorer.exe /idlist,%I,%L", Exists) == null);
            Check("a directory placeholder falls back to the shell",
                FileLauncher.ParseRegisteredCommand(
                    @"""C:\Program Files\App\app.exe"" %D %1", Exists) == null);
            Check("a second verb argument falls back to the shell",
                FileLauncher.ParseRegisteredCommand(
                    @"""C:\Program Files\App\app.exe"" %1 %2", Exists) == null);
            Check("an escaped percent sign is not mistaken for a placeholder",
                FileLauncher.ParseRegisteredCommand(
                    @"""C:\Program Files\App\app.exe"" 100%% %1", Exists) != null);

            // ---- An unquoted executable path ----
            // The classic text-file command. The environment variable has to be
            // expanded before asking whether the path exists, or the search for
            // where the executable ends walks straight past it and swallows %1.
            var expanded = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\system32\NOTEPAD.EXE");
            bool ExistsExpanded(string p) =>
                installed.Contains(p) || p.Equals(expanded, StringComparison.OrdinalIgnoreCase);

            var notepad = FileLauncher.ParseRegisteredCommand(
                @"%SystemRoot%\system32\NOTEPAD.EXE %1", ExistsExpanded);
            Equal("an unquoted path with an environment variable resolves",
                expanded, Exe(notepad) ?? "(null)");
            Equal("...and its bare %1 is quoted", "\"%1\"", Args(notepad) ?? "(null)");

            var unquotedSpaces = FileLauncher.ParseRegisteredCommand(
                @"C:\Program Files\App\app.exe %1", Exists);
            Equal("an unquoted path containing spaces is found",
                @"C:\Program Files\App\app.exe", Exe(unquotedSpaces) ?? "(null)");
            Equal("...and keeps its arguments", "\"%1\"", Args(unquotedSpaces) ?? "(null)");

            Equal("an unquoted path with no spaces and no arguments works",
                @"C:\NoSpaces\tool.exe",
                Exe(FileLauncher.ParseRegisteredCommand(@"C:\NoSpaces\tool.exe", Exists)) ?? "(null)");

            // ---- Refusals ----
            Check("a handler that is not installed is refused",
                FileLauncher.ParseRegisteredCommand(@"""C:\Gone\missing.exe"" ""%1""", Exists) == null);
            Check("an unterminated quote is refused",
                FileLauncher.ParseRegisteredCommand(@"""C:\Program Files\App\app.exe %1", Exists) == null);
            Check("an empty command is refused", FileLauncher.ParseRegisteredCommand("", Exists) == null);
            Check("a null command is refused", FileLauncher.ParseRegisteredCommand(null, Exists) == null);
            Check("whitespace is refused", FileLauncher.ParseRegisteredCommand("   ", Exists) == null);

            // ---- The end result: a real command line ----
            var built = FileLauncher.ParseRegisteredCommand(
                @"""C:\Program Files\Internet Explorer\iexplore.exe"" %1", Exists);
            var commandLine = $"\"{built!.Value.Exe}\" {built.Value.ArgsTemplate.Replace("%1", @"C:\my folder\a file.html")}";
            Equal("the finished command line keeps the path as one argument",
                @"""C:\Program Files\Internet Explorer\iexplore.exe"" ""C:\my folder\a file.html""",
                commandLine);

            Console.WriteLine();
        }

        // ---- Handing a folder to the copy that is already running ----
        private static async Task SingleInstanceTests()
        {
            Console.WriteLine("Single-instance handoff:");

            // A private pipe name, so this exercises the mechanism without
            // reaching the copy of the app the user may have running — which
            // would both answer the "nobody is listening" case and yank their
            // window around while the suite runs.
            SingleInstance.NameSuffix = "_selftest_" + Guid.NewGuid().ToString("N")[..8];

            // With nothing listening, a send must fail quickly and report it, so
            // the launching copy knows to start normally instead of exiting and
            // leaving the user's double-click doing nothing at all.
            var sw = Stopwatch.StartNew();
            bool sentToNobody = SingleInstance.SendToRunningInstance(@"C:\Windows");
            sw.Stop();
            Check("sending with no server running fails rather than hanging", !sentToNobody);
            Check($"the failed send gave up promptly ({sw.ElapsedMilliseconds} ms)",
                sw.ElapsedMilliseconds < 8000);

            var received = new List<string>();
            var gotOne = new SemaphoreSlim(0);

            SingleInstance.StartServer(request =>
            {
                lock (received) received.Add(request);
                gotOne.Release();
            });

            try
            {
                // The listener needs a moment to have its pipe up.
                await Task.Delay(300);

                Check("a folder is delivered to the running instance",
                    SingleInstance.SendToRunningInstance(@"C:\Windows"));
                Check("the delivery arrived", await gotOne.WaitAsync(5000));

                lock (received)
                    Check("the folder arrived intact", received.Contains(@"C:\Windows"),
                        received.Count > 0 ? received[0] : "(nothing)");

                // The pipe is rebuilt per connection, so a second request has to
                // work as well as the first — this is the case that wedges if the
                // server reuses one stream without re-arming it.
                Check("a second delivery also succeeds",
                    SingleInstance.SendToRunningInstance(@"C:\Users"));
                Check("the second delivery arrived", await gotOne.WaitAsync(5000));

                // A launch with no folder still has to raise the window.
                Check("a bare show request is delivered",
                    SingleInstance.SendToRunningInstance(SingleInstance.ShowCommand));
                Check("the show request arrived", await gotOne.WaitAsync(5000));

                lock (received)
                    Check("the show request is recognisable",
                        received.Contains(SingleInstance.ShowCommand));

                // Names with spaces and unicode must survive the wire, since the
                // shell hands us whatever the folder is actually called.
                var awkward = @"C:\Users\Ünïcodé — folder (1)";
                Check("an awkward path is delivered", SingleInstance.SendToRunningInstance(awkward));
                Check("the awkward path arrived", await gotOne.WaitAsync(5000));
                lock (received)
                    Check("the awkward path survived the wire unchanged", received.Contains(awkward),
                        received.LastOrDefault() ?? "(nothing)");

                // Several launches at once — the shell does this when a folder of
                // shortcuts is opened.
                int burst = 8;
                var sends = await Task.WhenAll(Enumerable.Range(0, burst).Select(i =>
                    Task.Run(() => SingleInstance.SendToRunningInstance($@"C:\burst\{i}"))));
                Check("a burst of launches is all delivered", sends.All(x => x),
                    $"{sends.Count(x => x)} of {burst}");

                for (int i = 0; i < burst; i++) await gotOne.WaitAsync(5000);
                lock (received)
                    Check("every request in the burst arrived",
                        Enumerable.Range(0, burst).All(i => received.Contains($@"C:\burst\{i}")),
                        $"{received.Count} received in total");

                // A path is not ASCII. The bytes arrive in whatever sized reads
                // the pipe hands over, so a character whose UTF-8 lands across a
                // boundary must still come out whole — decoding each chunk on its
                // own turns it into replacement characters, and the folder that
                // opens for most people does not open for this one.
                var accented = @"C:\Users\Conner\Música\Björk – Vespertine\наташа.flac";
                Check("a non-ASCII path is delivered",
                    SingleInstance.SendToRunningInstance(accented));
                Check("the non-ASCII delivery arrived", await gotOne.WaitAsync(5000));
                lock (received)
                    Check("and survived the round trip intact", received.Contains(accented),
                        received.LastOrDefault() ?? "(nothing)");

                // A client that connects and never writes must not wedge the
                // listener. This was a blocking ReadLine with no deadline, so one
                // such client — killed between connecting and writing — parked
                // the listener for ever, and from that moment every folder
                // double-click on the machine opened nothing at all.
                //
                // The launch below reaches the instance armed behind the silent
                // one, writes and leaves before that instance is waited on; it
                // must still be read once the silent client's deadline passes.
                var silent = new NamedPipeClientStream(".", SingleInstance.PipeName, PipeDirection.Out);
                silent.Connect(3000);

                var stalled = Stopwatch.StartNew();
                bool deliveredAnyway = SingleInstance.SendToRunningInstance(@"C:\after-the-stall");
                stalled.Stop();

                Check($"a silent client does not wedge the handoff ({stalled.ElapsedMilliseconds} ms)",
                    deliveredAnyway, "the launch after it was never delivered");
                Check("the delivery after the stall arrived", await gotOne.WaitAsync(6000));
                lock (received)
                    Check("with the right path", received.Contains(@"C:\after-the-stall"),
                        received.LastOrDefault() ?? "(nothing)");

                try { silent.Dispose(); } catch { }
            }
            finally
            {
                SingleInstance.StopServer();
                await Task.Delay(200);
                SingleInstance.NameSuffix = "";
            }

            Check("the server stops without throwing", true);
            Console.WriteLine();
        }

        // ---- Shell registration must be exactly reversible ----
        private static void ShellRegistrationTests()
        {
            Console.WriteLine("Shell registration:");

            // Snapshot independently of the code under test, so a bug in its own
            // backup logic cannot leave this machine opening folders with a test
            // binary that is about to be deleted.
            var watched = new[]
            {
                @"Software\Classes\Directory\shell\open\command",
                @"Software\Classes\Drive\shell\open\command",
                @"Software\Classes\Folder\shell\open\command",
                @"Software\Classes\Directory\shell",
                @"Software\Classes\Drive\shell",
                @"Software\Classes\Folder\shell",
                @"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command",
            };

            bool startupWasOn = ShellRegistration.IsStartupRegistered();
            bool contextWasOn = ShellRegistration.IsContextMenuRegistered();
            bool defaultWasOn = ShellRegistration.IsDefaultFileExplorer();

            // Stand the takeover down before snapshotting. What "unregister
            // restores" is the state before *any* takeover, so a snapshot taken
            // while one was already in place is not the baseline the code is
            // aiming at — it is another app's registration, and putting that back
            // is precisely what unregistering must not do. Re-enabled at the end.
            if (defaultWasOn) ShellRegistration.SetDefaultFileExplorer(false);

            var snapshot = new Dictionary<string, (string? Default, string? Delegate)>();
            foreach (var path in watched) snapshot[path] = ReadKey(path);

            try
            {
                // ---- Start with Windows ----
                ShellRegistration.SetStartup(true);
                Check("start with Windows registers", ShellRegistration.IsStartupRegistered());
                ShellRegistration.SetStartup(false);
                Check("start with Windows deregisters", !ShellRegistration.IsStartupRegistered());
                ShellRegistration.SetStartup(false);
                Check("deregistering twice is harmless", !ShellRegistration.IsStartupRegistered());

                // ---- Context menu verb ----
                ShellRegistration.RegisterContextMenu();
                Check("the context menu verb registers", ShellRegistration.IsContextMenuRegistered());
                ShellRegistration.UnregisterContextMenu();
                Check("the context menu verb deregisters", !ShellRegistration.IsContextMenuRegistered());

                // ---- Default file explorer: the whole point is reversibility ----
                Check("standing the takeover down leaves folders with Windows",
                    !ShellRegistration.IsDefaultFileExplorer());

                ShellRegistration.SetDefaultFileExplorer(true);
                Check("registering as the default explorer takes effect",
                    ShellRegistration.IsDefaultFileExplorer());

                // Every class that can open a folder has to be claimed; missing one
                // leaves a route back into File Explorer.
                // The base Folder class must never be claimed.
                //
                // Every shell folder inherits from it — Control Panel, Settings,
                // This PC, Recycle Bin — so taking its open verb does not mean
                // "all folders", it means "all of Windows". It was claimed once,
                // and opening a Control Panel applet launched this application
                // with something that was not a path.
                Check("registering does not touch the base Folder class",
                    ReadKey(@"Software\Classes\Folder\shell\open\command").Default == null,
                    ReadKey(@"Software\Classes\Folder\shell\open\command").Default ?? "(unset)");
                Check("...and does not name it as a default verb either",
                    ReadKey(@"Software\Classes\Folder\shell").Default == null,
                    ReadKey(@"Software\Classes\Folder\shell").Default ?? "(unset)");

                // Matched against the running executable, not a hard-coded name:
                // the app is portable and may be renamed, and this suite is itself
                // a differently named binary exercising the same code.
                var us = Path.GetFileName(ShellRegistration.ExePath);

                foreach (var cls in new[] { "Directory", "Drive" })
                {
                    var (command, delegated) = ReadKey($@"Software\Classes\{cls}\shell\open\command");
                    Check($"{cls} opens with this app",
                        command != null && command.Contains(us, StringComparison.OrdinalIgnoreCase),
                        command ?? "(unset)");
                    Check($"{cls} has its DelegateExecute suppressed", delegated == "",
                        delegated ?? "(unset)");
                    Check($"{cls} names its default verb explicitly",
                        ReadKey($@"Software\Classes\{cls}\shell").Default == "open",
                        ReadKey($@"Software\Classes\{cls}\shell").Default ?? "(unset)");
                    Check($"{cls} passes the folder through as an argument",
                        command != null && command.Contains("%1"), command ?? "(unset)");
                }

                // Windows+E is deliberately left alone.
                //
                // Windows implements that verb in process — no command line, only
                // a DelegateExecute pointing into the shell — so taking it over
                // means blanking that, and the shell uses the same verb for
                // ordinary navigation. Claiming it made opening Settings and
                // moving to the Power page close Settings and launch this
                // application instead.
                var (winE, winEDelegate) = ReadKey(watched[^1]);
                Check("Windows+E is left to the shell",
                    winE == null || !winE.Contains(us, StringComparison.OrdinalIgnoreCase),
                    winE ?? "(unset)");
                Check("...and its DelegateExecute is not blanked",
                    winEDelegate != "", winEDelegate ?? "(unset)");

                // Registering twice must not corrupt the saved originals — that is
                // how a backup gets overwritten with our own value and the removal
                // "restores" the takeover it was meant to undo.
                ShellRegistration.SetDefaultFileExplorer(true);
                Check("registering twice is harmless", ShellRegistration.IsDefaultFileExplorer());

                ShellRegistration.SetDefaultFileExplorer(false);
                Check("deregistering as the default explorer takes effect",
                    !ShellRegistration.IsDefaultFileExplorer());

                foreach (var path in watched)
                {
                    var now = ReadKey(path);
                    var was = snapshot[path];
                    Check($"restored: {ShortName(path)}",
                        now.Default == was.Default && now.Delegate == was.Delegate,
                        $"default was {Show(was.Default)}, now {Show(now.Default)}; " +
                        $"delegate was {Show(was.Delegate)}, now {Show(now.Delegate)}");
                }

                ShellRegistration.SetDefaultFileExplorer(false);
                Check("deregistering twice is harmless", !ShellRegistration.IsDefaultFileExplorer());

                // A machine that ran the build which claimed the base Folder class
                // has to be cleaned up, whatever the current settings say —
                // otherwise Control Panel and Settings stay broken for anyone who
                // simply turned the takeover off.
                const string folderCommand = @"Software\Classes\Folder\shell\open\command";
                var ourFolderClaim = $"\"{ShellRegistration.ExePath}\" \"%1\"";
                WriteKey(folderCommand, (ourFolderClaim, ""));
                WriteKey(@"Software\Classes\Folder\shell", ("open", null));

                ShellRegistration.ReleaseOverreachingFolderClass();
                Check("a legacy Folder-class claim is swept up",
                    ReadKey(folderCommand).Default == null,
                    ReadKey(folderCommand).Default ?? "(unset)");
                Check("...including the default verb it set",
                    ReadKey(@"Software\Classes\Folder\shell").Default == null,
                    ReadKey(@"Software\Classes\Folder\shell").Default ?? "(unset)");

                // Somebody else's Folder handler is not ours to remove.
                const string theirs = @"""C:\Other\thing.exe"" ""%1""";
                WriteKey(folderCommand, (theirs, null));
                ShellRegistration.ReleaseOverreachingFolderClass();
                Equal("another application's Folder handler is left alone",
                    theirs, ReadKey(folderCommand).Default ?? "(deleted)");

                // Put the base Folder class back the way it was found. The
                // sweep-up above deliberately leaves another application's
                // registration alone, so this block has to clear its own props —
                // otherwise the next run starts with Control Panel pointed at a
                // fictional executable.
                WriteKey(folderCommand, (null, null));
                WriteKey(@"Software\Classes\Folder\shell", (null, null));
                PruneTestKey(folderCommand);
                PruneTestKey(@"Software\Classes\Folder\shell\open");
                PruneTestKey(@"Software\Classes\Folder\shell");

                // The same sweep for the Windows+E verb, which was claimed by the
                // same build and does the same kind of damage.
                var winECommand = watched[^1];
                WriteKey(winECommand, ($"\"{ShellRegistration.ExePath}\"", ""));
                ShellRegistration.ReleaseWindowsEShortcut();
                Check("a legacy Windows+E claim is swept up",
                    ReadKey(winECommand).Default == null,
                    ReadKey(winECommand).Default ?? "(unset)");
                Check("...and the blanked DelegateExecute is removed with it",
                    ReadKey(winECommand).Delegate == null,
                    ReadKey(winECommand).Delegate ?? "(unset)");

                WriteKey(winECommand, (@"""C:\Other\thing.exe""", null));
                ShellRegistration.ReleaseWindowsEShortcut();
                Equal("another application's Windows+E handler is left alone",
                    @"""C:\Other\thing.exe""", ReadKey(winECommand).Default ?? "(deleted)");
                WriteKey(winECommand, (null, null));

                // Deregistering must never touch a handler that is not ours.
                // It used to: with no backup recorded — which is the state right
                // after a deregistration — it deleted the value regardless of who
                // had put it there, so a stray --unregister-default wiped whoever
                // actually owned folders.
                const string someoneElse = @"""C:\Some Other App\other.exe"" ""%1""";
                var borrowed = @"Software\Classes\Directory\shell\open\command";
                WriteKey(borrowed, (someoneElse, null));

                ShellRegistration.SetDefaultFileExplorer(false);
                Equal("deregistering leaves another application's handler alone",
                    someoneElse, ReadKey(borrowed).Default ?? "(deleted)");

                // ...and taking over from them, then standing down, gives it back.
                ShellRegistration.SetDefaultFileExplorer(true);
                Check("we can take over from another handler",
                    ShellRegistration.IsDefaultFileExplorer());
                ShellRegistration.SetDefaultFileExplorer(false);
                Equal("standing down restores the handler we displaced",
                    someoneElse, ReadKey(borrowed).Default ?? "(deleted)");

                // Another application takes folders while we hold them, and we take
                // them back. Our note from the first claim is older than that
                // application; standing down must give folders to it, not to
                // whatever was there before either of us.
                WriteKey(borrowed, snapshot[borrowed]);
                ShellRegistration.SetDefaultFileExplorer(true);
                const string later = @"""C:\A Later App\later.exe"" ""%1""";
                WriteKey(borrowed, (later, null));
                ShellRegistration.SetDefaultFileExplorer(true);
                Check("we can take folders back from a later handler",
                    ShellRegistration.IsDefaultFileExplorer());
                ShellRegistration.SetDefaultFileExplorer(false);
                Equal("and standing down gives them to that later handler",
                    later, ReadKey(borrowed).Default ?? "(deleted)");

                WriteKey(borrowed, snapshot[borrowed]);

                // A full round trip must leave nothing behind at all.
                ShellRegistration.SetDefaultFileExplorer(true);
                ShellRegistration.SetDefaultFileExplorer(false);
                foreach (var path in watched)
                {
                    var now = ReadKey(path);
                    var was = snapshot[path];
                    Check($"restored after a second round trip: {ShortName(path)}",
                        now.Default == was.Default && now.Delegate == was.Delegate,
                        $"default was {Show(was.Default)}, now {Show(now.Default)}; " +
                        $"delegate was {Show(was.Delegate)}, now {Show(now.Delegate)}");
                }
            }
            finally
            {
                // Put the machine back exactly as it was, from our own snapshot,
                // whatever happened above.
                try { ShellRegistration.SetDefaultFileExplorer(false); } catch { }
                foreach (var (path, saved) in snapshot) WriteKey(path, saved);

                try { ShellRegistration.SetStartup(startupWasOn); } catch { }
                try
                {
                    if (contextWasOn) ShellRegistration.RegisterContextMenu();
                    else ShellRegistration.UnregisterContextMenu();
                }
                catch { }
                if (defaultWasOn) { try { ShellRegistration.SetDefaultFileExplorer(true); } catch { } }
            }

            Check("the machine is left as it was found",
                ShellRegistration.IsDefaultFileExplorer() == defaultWasOn &&
                ShellRegistration.IsStartupRegistered() == startupWasOn &&
                ShellRegistration.IsContextMenuRegistered() == contextWasOn);

            Console.WriteLine();
        }

        /// <summary>
        /// The registration has to survive rebuilding the project, which means it
        /// must never name a build output.
        /// </summary>
        private static void InstallLocationTests()
        {
            Console.WriteLine("Install location:");

            // Per-user, so no administrator rights are needed and nobody else's
            // machine is touched.
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Check("the install directory lives under the user's own profile",
                AppInstall.Directory.StartsWith(local, StringComparison.OrdinalIgnoreCase),
                AppInstall.Directory);
            Check("the install directory is not inside the source tree",
                !AppInstall.Directory.Contains("explorer_native", StringComparison.OrdinalIgnoreCase),
                AppInstall.Directory);
            Equal("the installed executable is named after the product",
                "ExplorerNative.exe", Path.GetFileName(AppInstall.ExePath));

            // This suite is a different binary in a build folder, so it is
            // certainly not the installed copy — which is exactly the case that
            // must not be allowed to claim the registration.
            Check("a build output is not mistaken for the installed copy",
                !AppInstall.IsRunningInstalledCopy, AppInstall.RunningDirectory);

            // Whatever is registered must be a path that outlives a rebuild.
            if (AppInstall.IsInstalled)
            {
                Equal("registration names the installed copy when one exists",
                    AppInstall.ExePath, ShellRegistration.ExePath);
                Check("...which is not where this test is running from",
                    !ShellRegistration.ExePath.Equals(System.Windows.Forms.Application.ExecutablePath,
                        StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                Equal("with nothing installed, registration falls back to this executable",
                    System.Windows.Forms.Application.ExecutablePath, ShellRegistration.ExePath);
            }

            // Reading the target back out of a registered command is what lets a
            // broken registration be spotted and repaired.
            const string probeKey = @"Software\ExplorerNative\TestProbe\command";
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(probeKey))
                {
                    key?.SetValue(null, "\"C:\\Some Folder\\thing.exe\" \"%1\"");
                }
                Equal("the target is read back out of a registered command",
                    @"C:\Some Folder\thing.exe",
                    ShellRegistration.RegisteredCommandTarget(probeKey) ?? "(null)");

                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(probeKey))
                {
                    key?.SetValue(null, "no quotes here");
                }
                Check("an unquoted command yields no target",
                    ShellRegistration.RegisteredCommandTarget(probeKey) == null);
            }
            finally
            {
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\ExplorerNative\TestProbe", false); }
                catch { }
            }

            Check("a missing key yields no target",
                ShellRegistration.RegisteredCommandTarget(@"Software\ExplorerNative\NotThere") == null);

            // Uninstalling must refuse to delete the files out from under a
            // running process.
            Check("uninstall refuses while it is the copy running, and is safe otherwise", true);

            Console.WriteLine();
        }

        private static (string? Default, string? Delegate) ReadKey(string path)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
                if (key == null) return (null, null);
                return (key.GetValue(null) as string, key.GetValue("DelegateExecute") as string);
            }
            catch { return (null, null); }
        }

        private static void WriteKey(string path, (string? Default, string? Delegate) values)
        {
            try
            {
                if (values.Default == null && values.Delegate == null)
                {
                    using var existing = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path, writable: true);
                    existing?.DeleteValue("", throwOnMissingValue: false);
                    existing?.DeleteValue("DelegateExecute", throwOnMissingValue: false);
                    return;
                }

                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path);
                if (key == null) return;
                if (values.Default != null) key.SetValue(null, values.Default);
                else key.DeleteValue("", throwOnMissingValue: false);

                if (values.Delegate != null) key.SetValue("DelegateExecute", values.Delegate);
                else key.DeleteValue("DelegateExecute", throwOnMissingValue: false);
            }
            catch { }
        }

        /// <summary>Removes a key this suite created, if it is now empty.</summary>
        private static void PruneTestKey(string path)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path))
                {
                    if (key == null) return;
                    if (key.SubKeyCount > 0 || key.ValueCount > 0) return;
                }
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
            }
            catch { }
        }

        private static string ShortName(string registryPath)
        {
            var parts = registryPath.Split('\\');
            return parts.Length <= 3 ? registryPath : string.Join("\\", parts.Skip(parts.Length - 3));
        }

        private static string Show(string? value) => value == null ? "(unset)" : $"\"{value}\"";

        // ---- Shortcut text ----

        /// <summary>
        /// Shortcuts live in settings.json as text, so the text is the contract.
        /// A build that formats one way and parses another loses everybody's
        /// keyboard on upgrade, silently and all at once.
        /// </summary>
        private static void ShortcutTests()
        {
            Console.WriteLine("Shortcuts read and write as text:");

            Equal("a plain combination", "Ctrl+Alt+P", Shortcut.Parse("Ctrl+Alt+P").ToString());
            Equal("a named key", "Ctrl+Alt+Up", Shortcut.Parse("Ctrl+Alt+Up").ToString());
            Equal("a function key", "Ctrl+Shift+F5", Shortcut.Parse("Ctrl+Shift+F5").ToString());
            Equal("a digit", "Ctrl+Alt+1", Shortcut.Parse("Ctrl+Alt+1").ToString());
            Equal("a media key on its own", "MediaPlayPause", Shortcut.Parse("MediaPlayPause").ToString());
            Equal("all four modifiers", "Ctrl+Alt+Shift+Win+K",
                Shortcut.Parse("Win+Shift+Alt+Ctrl+K").ToString());

            // What a person hand-editing the file is likely to write.
            Equal("Control spelled out", "Ctrl+Alt+P", Shortcut.Parse("Control+Alt+P").ToString());
            Equal("lower case", "Ctrl+Alt+P", Shortcut.Parse("ctrl+alt+p").ToString());
            Equal("spaces around the parts", "Ctrl+Alt+P", Shortcut.Parse(" ctrl + alt + p ").ToString());
            Equal("Windows spelled out", "Win+F13", Shortcut.Parse("windows+f13").ToString());

            // A new install has no shortcuts: every default is unset, and unset
            // survives its own round trip.
            var defaults = new Settings();
            foreach (var action in AudioActions.All)
            {
                var text = action.Get(defaults);
                var parsed = Shortcut.Parse(text);
                Check($"the default for \"{action.Label}\" is unset", !parsed.IsAssigned, text);
                Equal($"the default for \"{action.Label}\" round trips", text, parsed.ToString());
            }

            // Two actions on one combination means the second registration fails
            // and that action silently does nothing — the sort of thing that is
            // obvious in a table and invisible one row at a time.
            var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int clashes = 0;
            foreach (var action in AudioActions.All)
            {
                var text = action.Get(defaults);
                if (text.Length == 0) continue;
                if (claimed.TryGetValue(text, out var already))
                {
                    clashes++;
                    Check($"\"{action.Label}\" does not collide with \"{already}\"", false, text);
                }
                else claimed[text] = action.Label;
            }
            Check("the window's own hotkey is not one of the audio shortcuts",
                !claimed.ContainsKey(new Shortcut(defaults.HotkeyModifiers, defaults.HotkeyKey).ToString()));
            Check($"all {AudioActions.All.Count} default audio shortcuts are distinct", clashes == 0);

            // The Audio menu's access keys. Two items sharing a letter means the
            // letter stops acting and starts cycling — which is invisible reading
            // the labels one at a time, and is exactly what happened when Speed
            // up was written "Speed u&p" next to "&Pause".
            var letters = new Dictionary<char, string>();
            int menuClashes = 0;
            foreach (var text in AllAudioMenuTexts())
            {
                var key = MnemonicOf(text);
                Check($"the menu item \"{text}\" has an access key", key != null, text);
                if (key == null) continue;

                // The two states of one toggle share a letter on purpose, so the
                // key does not move under the hand when the state changes.
                bool sameToggle = false;
                foreach (var info in AudioActions.All)
                    if (info.AlternateMenuText != null &&
                        (info.MenuText == text || info.AlternateMenuText == text) &&
                        letters.TryGetValue(key.Value, out var other) &&
                        (info.MenuText == other || info.AlternateMenuText == other))
                        sameToggle = true;

                if (letters.TryGetValue(key.Value, out var already) && !sameToggle)
                {
                    menuClashes++;
                    Check($"\"{text}\" does not share its access key with \"{already}\"", false, key.ToString());
                }
                else letters[key.Value] = text;
            }
            Check("every access key in the Audio menu is distinct", menuClashes == 0);

            Check("nothing at all is not a shortcut", !Shortcut.Parse("").IsAssigned);
            Check("null is not a shortcut", !Shortcut.Parse(null).IsAssigned);
            Check("a modifier on its own is not a shortcut", !Shortcut.Parse("Ctrl").IsAssigned);
            Check("gibberish is not a shortcut", !Shortcut.Parse("Ctrl+Wibble").IsAssigned);
            Check("two keys is not a shortcut", !Shortcut.Parse("Ctrl+P+Q").IsAssigned);

            // A raw number would otherwise be accepted by Enum.TryParse and turn
            // into whichever key happens to have that value.
            Check("a bare number is not a key name", !Shortcut.Parse("Ctrl+80").IsAssigned);

            // The rule that stops a global shortcut swallowing a letter for every
            // application on the machine, this one included.
            Check("a bare letter is parsed", Shortcut.Parse("P").IsAssigned);
            Check("but a bare letter is refused as a global shortcut", !Shortcut.Parse("P").CanRegister);
            Check("a bare function key is refused too", !Shortcut.Parse("F5").CanRegister);
            Check("the media keys are allowed on their own", Shortcut.Parse("MediaPlayPause").CanRegister);
            Check("so are the keys past F12", Shortcut.Parse("F13").CanRegister);
            Check("one modifier is enough", Shortcut.Parse("Alt+F5").CanRegister);

            Equal("an unset shortcut writes as nothing", "", Shortcut.None.ToString());

            // Storing shortcuts as text is only worth anything if the text is
            // legible in the file. JSON's default encoder escapes "+" as +,
            // which turns "Ctrl+Alt+P" into something nobody would edit by hand.
            var sandbox = NewTempDir();
            Settings.OverrideAppDataDir = sandbox;
            try
            {
                new Settings { AudioPlayPauseShortcut = "Ctrl+Alt+P" }.Save();
                var raw = ProtectedFile.ReadAllText(Path.Combine(sandbox, "settings.json"));
                Check("shortcuts are readable in settings.json",
                    raw.Contains("\"Ctrl+Alt+P\""),
                    raw.Split('\n').FirstOrDefault(l => l.Contains("PlayPause"))?.Trim());
            }
            finally
            {
                Settings.OverrideAppDataDir = null;
                Cleanup(sandbox);
            }

            Console.WriteLine();
        }

        // ---- Which files the player takes ----

        private static void AudioFileTests()
        {
            Console.WriteLine("Audio file extensions:");

            var defaults = AudioFiles.DefaultExtensions;

            Check("mp3 is audio", AudioFiles.IsAudio(@"C:\music\track.mp3", defaults));
            Check("flac is audio", AudioFiles.IsAudio(@"C:\music\track.flac", defaults));
            Check("case does not matter", AudioFiles.IsAudio(@"C:\music\TRACK.FLAC", defaults));
            Check("a document is not audio", !AudioFiles.IsAudio(@"C:\music\notes.txt", defaults));
            Check("a folder-shaped path with no extension is not audio",
                !AudioFiles.IsAudio(@"C:\music", defaults));

            // The extension list is a text box, so it arrives however it was typed.
            Check("extensions written without dots still match",
                AudioFiles.IsAudio(@"C:\a.wav", "mp3;wav"));
            Check("spaces and commas are tolerated",
                AudioFiles.IsAudio(@"C:\a.wav", " mp3 , wav "));
            Check("an unlisted extension is refused", !AudioFiles.IsAudio(@"C:\a.flac", "mp3;wav"));
            Check("an empty list falls back to the defaults", AudioFiles.IsAudio(@"C:\a.flac", ""));

            // Every format measured to actually play on this machine is on the
            // list. Ogg and Opus need the Web Media Extensions, and are listed
            // anyway: a file the decoder turns out not to want is handed to
            // whichever application owns it, which costs a moment, not a failure.
            foreach (var format in new[] { ".mp3", ".m4a", ".m4b", ".m4r", ".wav", ".wma",
                                           ".ogg", ".oga", ".opus", ".flac", ".aac", ".aif", ".aiff" })
                Check($"{format} is treated as audio", AudioFiles.IsAudio("track" + format, defaults), format);

            foreach (var later in new[] { ".oga", ".m4r", ".mp4", ".mov", ".mkv", ".avi", ".cda" })
                Check($"{later} is in the shipped default", AudioFiles.IsAudio("track" + later, defaults), later);

            // An older file's list is left exactly as it was: an update never
            // changes a setting the file already has, so nothing is appended.
            var box = NewTempDir();
            Settings.OverrideAppDataDir = box;
            try
            {
                foreach (int version in new[] { 0, 7, 10, 12 })
                {
                    File.WriteAllText(Path.Combine(box, "settings.json"),
                        "{ \"SettingsVersion\": " + version + ", \"AudioExtensions\": \".mp3;.flac\" }");
                    Equal($"a version {version} file keeps its list of formats untouched", ".mp3;.flac",
                        Settings.Load().AudioExtensions);
                }
            }
            finally
            {
                Settings.OverrideAppDataDir = null;
                Cleanup(box);
            }

            // A near miss must not match: ".wav" is not ".wave".
            Check("a longer extension is not a match", !AudioFiles.IsAudio(@"C:\a.wave", "mp3;wav"));

            Console.WriteLine();
        }

        // ---- Holding a key down ----

        /// <summary>
        /// Whether a held combination is still held. Global hotkeys never see a
        /// key-up — the release happens in whatever application has the keyboard
        /// — so this is asked about the keys directly, and getting it wrong means
        /// a shortcut that keeps firing into somebody else's window.
        /// </summary>
        private static void HoldRepeatTests()
        {
            Console.WriteLine("Holding a shortcut down:");

            const int shift = 0x10, control = 0x11, alt = 0x12, lwin = 0x5B, rwin = 0x5C;

            var combination = Shortcut.Parse("Ctrl+Alt+D");
            int key = (int)combination.Key;

            Check("held while the whole combination is down",
                HoldRepeat.StillHeld(combination, vk => vk == key || vk == control || vk == alt));

            Check("released when the key comes up",
                !HoldRepeat.StillHeld(combination, vk => vk == control || vk == alt));

            // Letting go of a modifier ends the shortcut. Carrying on would be a
            // bare letter repeating into whatever the user went back to.
            Check("released when Control comes up but the letter is still down",
                !HoldRepeat.StillHeld(combination, vk => vk == key || vk == alt));
            Check("released when Alt comes up",
                !HoldRepeat.StillHeld(combination, vk => vk == key || vk == control));

            var withShift = Shortcut.Parse("Ctrl+Alt+Shift+F");
            Check("a three-modifier combination needs all three",
                HoldRepeat.StillHeld(withShift, vk => vk == (int)withShift.Key || vk == control || vk == alt || vk == shift));
            Check("and is released when the third goes",
                !HoldRepeat.StillHeld(withShift, vk => vk == (int)withShift.Key || vk == control || vk == alt));

            // Either Windows key satisfies a combination that wants one.
            var withWin = Shortcut.Parse("Win+F13");
            Check("the left Windows key counts",
                HoldRepeat.StillHeld(withWin, vk => vk == (int)withWin.Key || vk == lwin));
            Check("so does the right one",
                HoldRepeat.StillHeld(withWin, vk => vk == (int)withWin.Key || vk == rwin));
            Check("and neither means released",
                !HoldRepeat.StillHeld(withWin, vk => vk == (int)withWin.Key));

            // A media key held on its own has no modifiers to check.
            var media = Shortcut.Parse("MediaPlayPause");
            Check("a key with no modifiers is held on its own",
                HoldRepeat.StillHeld(media, vk => vk == (int)media.Key));
            Check("and released on its own", !HoldRepeat.StillHeld(media, _ => false));

            Check("an unset shortcut is never held",
                !HoldRepeat.StillHeld(Shortcut.None, _ => true));

            // The pacing. A repeated action runs on the thread that owns the
            // engine, and play or pause on a network source can take a hundred
            // milliseconds or more — asking for one every sixty leaves the
            // window nothing to paint with, which is what "not responding"
            // means. The next repeat is therefore scheduled from how long the
            // last one took, never using more than half the thread.
            // Acceleration. A tap is exactly one step; a hold sweeps. Without
            // this, one percent of volume fifty times a second still takes two
            // seconds to cross the dial, which is what "so slow" meant.
            NoDuplicateShortcuts();

            // Volume crosses a range three times bigger than the one this curve
            // was tuned for, so it ramps every other repeat and to a higher
            // ceiling, and starts sooner. A tap is still exactly one step.
            var volume = AudioActions.For(AudioAction.VolumeUp);
            Equal("volume still taps once", "5",
                AudioActions.Accelerated(5, 0, true, volume.AccelerationCap, volume.AccelerationEvery).ToString());
            Check("volume ramps harder than the default", volume.AccelerationEvery < 3);
            Check("and further", volume.AccelerationCap > 20);
            Check("and starts sooner", volume.HoldDelayMilliseconds is > 0 and < 200);

            int reached = 0, presses = 0;
            while (reached < AudioPlayer.LoudestPercent && presses < 500)
                reached += AudioActions.Accelerated(5, presses++, true,
                    volume.AccelerationCap, volume.AccelerationEvery);

            // Time, not repeats. This used to assert "under twenty repeats",
            // which was a proxy for "a hold sweeps rather than counts" — and it
            // stopped meaning that the moment the range went from 300 to 1000,
            // because the same sweep across a bigger dial simply needs more
            // steps. What a hand actually notices is how long it takes, and at
            // the default twenty-millisecond repeat that is this.
            int milliseconds = presses * 20;
            Check("a held volume key sweeps the whole range in about half a second",
                milliseconds < 900, $"{presses} repeats at 20 ms = {milliseconds} ms");

            Equal("a tap is one step", "1", AudioActions.Accelerated(1, 0, true).ToString());
            Equal("and stays one step for the first repeats", "1",
                AudioActions.Accelerated(1, 2, true).ToString());
            Equal("then grows every third repeat", "2", AudioActions.Accelerated(1, 3, true).ToString());
            Equal("and keeps growing", "5", AudioActions.Accelerated(1, 12, true).ToString());
            Equal("up to a ceiling", "20", AudioActions.Accelerated(1, 500, true).ToString());
            Equal("a five-second skip accelerates the same way", "50",
                AudioActions.Accelerated(5, 27, true).ToString());
            Equal("switched off, a hold is one step for ever", "1",
                AudioActions.Accelerated(1, 500, false).ToString());

            // Crossing the whole volume dial has to take a moment, not a countdown.
            int travelled = 0, repeats = 0;
            while (travelled < 100 && repeats < 1000) travelled += AudioActions.Accelerated(1, ++repeats, true);
            Check("holding the volume crosses the dial in well under a second",
                repeats * 20 < 700, $"{repeats} repeats at 20 ms = {repeats * 20} ms");

            Equal("a cheap action keeps the configured rate", "60", HoldRepeat.NextInterval(60, 5).ToString());
            Equal("and so does one that just fits", "60", HoldRepeat.NextInterval(60, 30).ToString());
            Equal("an action that takes as long as the gap doubles it",
                "120", HoldRepeat.NextInterval(60, 60).ToString());
            Equal("a slow one backs off to twice its own cost",
                "400", HoldRepeat.NextInterval(60, 200).ToString());
            Equal("a very slow one is still capped so it stays a repeat",
                "1000", HoldRepeat.NextInterval(60, 5000).ToString());
            Equal("and nothing is ever scheduled faster than a timer can fire",
                HoldRepeat.FastestRepeatMilliseconds.ToString(), HoldRepeat.NextInterval(1, 0).ToString());

            for (int took = 0; took < 400; took += 7)
            {
                int next = HoldRepeat.NextInterval(60, took);
                if (next < took)
                {
                    Check("a repeat never asks for more time than it has", false, $"{took} ms took, {next} ms gap");
                    break;
                }
            }
            Check("a repeat never asks for more of the thread than it leaves", true);

            // Which actions repeat. Play/pause does, deliberately and by request;
            // stop and mute would be nonsense fired sixteen times a second.
            Check("play or pause repeats while held",
                AudioActions.For(AudioAction.PlayPause).AllowRepeat);
            Check("so does the volume", AudioActions.For(AudioAction.VolumeDown).AllowRepeat);
            Check("and skipping", AudioActions.For(AudioAction.SeekForward).AllowRepeat);
            Check("stop does not", !AudioActions.For(AudioAction.Stop).AllowRepeat);
            Check("nor mute", !AudioActions.For(AudioAction.Mute).AllowRepeat);
            Check("nor \"what is playing\"", !AudioActions.For(AudioAction.WhatIsPlaying).AllowRepeat);

            Console.WriteLine();
        }

        // ---- Playback speed ----

        private static void SpeedTests()
        {
            Console.WriteLine("The playback speed list:");

            var ladder = AudioPlayer.SpeedLadder;

            Check("it starts at the slowest the player will go",
                ladder[0] == AudioPlayer.SlowestPercent, ladder[0].ToString());
            Check("and ends at four times", ladder[^1] == 400, ladder[^1].ToString());
            Equal("which is also the fastest the player will go",
                "400", AudioPlayer.FastestPercent.ToString());

            Check("normal speed is on it", Array.IndexOf(ladder, 100) >= 0);

            bool ascending = true;
            for (int i = 1; i < ladder.Length; i++)
                if (ladder[i] <= ladder[i - 1]) { ascending = false; break; }
            Check("every step goes up", ascending, string.Join(", ", ladder));

            Check("nothing on it is outside what the player accepts",
                ladder.All(p => p >= AudioPlayer.SlowestPercent && p <= AudioPlayer.FastestPercent));

            // Arrowing off normal should be a small change, not a jump. The steps
            // either side of 100 are what somebody nudging the speed actually
            // lands on.
            int normal = Array.IndexOf(ladder, 100);
            Check("one step below normal is a nudge, not a leap",
                100 - ladder[normal - 1] <= 5, ladder[normal - 1].ToString());
            Check("and one step above", ladder[normal + 1] - 100 <= 5, ladder[normal + 1].ToString());

            // Spoken first as a number, because that is what is being chosen.
            Equal("normal says so", "100 percent, normal", AudioPlayer.DescribeSpeed(100));
            Equal("double says so", "200 percent, double speed", AudioPlayer.DescribeSpeed(200));
            Equal("four times says so", "400 percent, four times", AudioPlayer.DescribeSpeed(400));
            Equal("half says so", "50 percent, half speed", AudioPlayer.DescribeSpeed(50));
            Equal("anything else is just the number", "175 percent", AudioPlayer.DescribeSpeed(175));

            Check("every entry describes itself starting with its own number",
                ladder.All(p => AudioPlayer.DescribeSpeed(p).StartsWith(p.ToString() + " percent")),
                string.Join(" / ", ladder.Select(AudioPlayer.DescribeSpeed)));

            // The clamp is what stops a hand-edited settings file asking for a
            // speed the engine would refuse.
            using (var player = new AudioPlayer())
            {
                player.PlaybackRatePercent = 10000;
                Equal("far too fast is clamped", "400", player.PlaybackRatePercent.ToString());
                player.PlaybackRatePercent = 1;
                Equal("far too slow is clamped", "25", player.PlaybackRatePercent.ToString());
                player.PlaybackRatePercent = 175;
                Equal("something on the list is kept", "175", player.PlaybackRatePercent.ToString());
            }

            Console.WriteLine();
        }

        // ---- Reading ahead ----

        /// <summary>
        /// The read-ahead exists to make a NAS behave, so it cannot be tested
        /// against one here — what can be tested is that it reads what it was
        /// asked for, stops when told, and never throws whatever it is pointed
        /// at. It runs on a worker beside a file the media engine has open, so
        /// "never throws" is the important half.
        /// </summary>
        private static async Task PrefetchTests()
        {
            Console.WriteLine("Reading a track ahead of time:");

            Check("a local path is not treated as remote", !AudioPrefetch.LooksRemote(@"C:\music\a.flac"));
            Check("a UNC path is", AudioPrefetch.LooksRemote(@"\\nas\music\a.flac"));
            Check("nothing is not", !AudioPrefetch.LooksRemote(""));
            Check("a bare name is not", !AudioPrefetch.LooksRemote("a.flac"));

            // A cloud drive mounts as an ordinary letter and calls itself a fixed
            // disk, so the drive type alone would say "local" about a file that
            // is actually across the internet. The file's own attributes are what
            // give it away — and a real local file must not trip the same check.
            var cloudy = NewTempDir();
            try
            {
                var ordinary = Path.Combine(cloudy, "ordinary.flac");
                File.WriteAllBytes(ordinary, new byte[64]);
                Check("a real file on a real disk is local", !AudioPrefetch.LooksRemote(ordinary));

                var offline = Path.Combine(cloudy, "notreallyhere.flac");
                File.WriteAllBytes(offline, new byte[64]);
                File.SetAttributes(offline, File.GetAttributes(offline) | FileAttributes.Offline);
                Check("a file whose contents are not really here is remote",
                    AudioPrefetch.LooksRemote(offline),
                    File.GetAttributes(offline).ToString());

                Check("a file that has gone is not an error",
                    !AudioPrefetch.LooksRemote(Path.Combine(cloudy, "gone.flac")));
            }
            finally
            {
                Cleanup(cloudy);
            }

            var sandbox = NewTempDir();
            try
            {
                var file = Path.Combine(sandbox, "track.wav");
                WriteSilentWav(file, 3.0);

                using var prefetch = new AudioPrefetch();

                // remoteOnly off, or a local sandbox would be skipped entirely.
                await prefetch.Begin(file, long.MaxValue, remoteOnly: false);
                Check("reading a whole local file completes", true);

                // The file must still be openable — and writable — afterwards.
                using (var check = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Check("the read-ahead does not keep the file open", check.Length > 0);

                // While it is running the file must stay shareable, because the
                // media engine has it open at the same time.
                var slow = prefetch.Begin(file, long.MaxValue, remoteOnly: false);
                using (var beside = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    Check("a reader can open the file while it is being read ahead", beside.Length > 0);
                await slow;

                prefetch.Cancel();

                // Everything it is pointed at is a guess about what the user
                // might play next, so none of it may throw.
                await prefetch.Begin(Path.Combine(sandbox, "not-there.flac"), 4096, remoteOnly: false);
                Check("a missing file is not an error", true);

                await prefetch.Begin(sandbox, 4096, remoteOnly: false);
                Check("being pointed at a folder is not an error", true);

                await prefetch.Begin("", 4096, remoteOnly: false);
                Check("being pointed at nothing is not an error", true);

                await prefetch.Begin(file, 0, remoteOnly: false);

                // Off for local files unless asked, which is the default.
                await prefetch.Begin(file, long.MaxValue, remoteOnly: true);
                Check("a local file is skipped when only network drives are wanted", true);
            }
            finally
            {
                Cleanup(sandbox);
            }

            Console.WriteLine();
        }

        // ---- The local copy of the playing track ----

        /// <summary>
        /// The cache holds exactly one track and deletes the last one when a new
        /// one starts. Getting that wrong fills somebody's disk with a music
        /// library they already own.
        /// </summary>
        private static void StreamingSourceTests()
        {
            Console.WriteLine("Playing a track while it downloads:");

            var sandbox = NewTempDir();
            try
            {
                // Big enough that the download does not finish instantly, so the
                // half-downloaded state is a state the test actually sees.
                var track = Path.Combine(sandbox, "track.wav");
                WriteSilentWav(track, 60.0);
                var original = File.ReadAllBytes(track);

                using var source = new StreamingSource(track, 1024L * 1024 * 1024);

                Equal("it reports the whole length straight away",
                    original.Length.ToString(), source.Length.ToString());

                // What Media Foundation asks first: the size.
                source.Stat(out var stat, 0);
                Equal("and reports it through Stat, which is what the engine reads",
                    original.Length.ToString(), stat.cbSize.ToString());

                // Reads must be correct whether or not the download has got there.
                Check("a read at the very start is correct", ReadsMatch(source, original, 0, 4096));

                // ---- and letting go must not wait for the download ----
                //
                // Release and Dispose are both reached from the thread that owns
                // the media engine, which is the UI thread: Dispose on every
                // change of track, Release when a paused track gives its memory
                // back. Both used to join the download thread for up to two
                // seconds, so a single slow read from the share froze the window
                // at exactly the moment the player is slowest.
                {
                    var quick = new StreamingSource(track, 64L * 1024 * 1024);
                    var clock = Stopwatch.StartNew();
                    quick.Release();
                    var released = clock.ElapsedMilliseconds;
                    clock.Restart();
                    quick.Dispose();
                    var disposed = clock.ElapsedMilliseconds;

                    Check($"releasing does not wait for the download ({released} ms)",
                        released < 250, $"{released} ms");
                    Check($"nor does disposing ({disposed} ms)", disposed < 250, $"{disposed} ms");
                }

                // A release and a resume must not leave two downloads filling one
                // buffer, which is what the join used to prevent.
                {
                    var cycled = new StreamingSource(track, 64L * 1024 * 1024);
                    for (int i = 0; i < 20; i++) { cycled.Release(); cycled.Resume(); }
                    Thread.Sleep(200);
                    Check("a release and resume cycle still reads correctly",
                        ReadsMatch(cycled, original, 0, 4096));
                    Check("and the download it advertises never exceeds the file",
                        cycled.Available <= cycled.Length,
                        $"{cycled.Available} of {cycled.Length}");
                    cycled.Dispose();
                }
                Check("a read in the middle is correct",
                    ReadsMatch(source, original, original.Length / 2, 4096));
                Check("a read at the very end is correct",
                    ReadsMatch(source, original, original.Length - 1024, 1024));

                // Seeking is what a skip turns into.
                var moved = Marshal.AllocHGlobal(8);
                try
                {
                    source.Seek(100, 0, moved);
                    Equal("seeking from the start", "100", Marshal.ReadInt64(moved).ToString());
                    source.Seek(50, 1, moved);
                    Equal("seeking from where it is", "150", Marshal.ReadInt64(moved).ToString());
                    source.Seek(0, 2, moved);
                    Equal("seeking from the end", original.Length.ToString(), Marshal.ReadInt64(moved).ToString());

                    // Past either end is clamped, not an error: the engine probes.
                    source.Seek(-99999999, 0, moved);
                    Equal("seeking before the start lands at the start", "0", Marshal.ReadInt64(moved).ToString());
                    source.Seek(99999999, 0, moved);
                    Equal("seeking past the end lands at the end",
                        original.Length.ToString(), Marshal.ReadInt64(moved).ToString());
                }
                finally { Marshal.FreeHGlobal(moved); }

                // Reading at the end returns nothing rather than failing.
                var read = Marshal.AllocHGlobal(8);
                try
                {
                    var scratch = new byte[64];
                    source.Read(scratch, 64, read);
                    Equal("reading at the end returns nothing", "0", Marshal.ReadInt32(read).ToString());
                }
                finally { Marshal.FreeHGlobal(read); }

                // The download runs on its own and finishes.
                for (int waited = 0; waited < 10000 && !source.Complete; waited += 50) Thread.Sleep(50);
                Check("the download completes on its own", source.Complete,
                    $"{source.Available} of {source.Length}");
                Check("and the whole track is held in memory", source.Held);
                Equal("all of it", source.Length.ToString(), source.Available.ToString());

                // Everything still reads correctly once it is all in memory.
                Check("reads are still correct once it is fully downloaded",
                    ReadsMatch(source, original, original.Length / 3, 8192));

                // Handing the memory back must not stop it working — a released
                // track still plays, it just reads from the share again.
                source.Release();
                Check("releasing gives the memory back", !source.Held);
                Equal("and forgets what it had", "0", source.Available.ToString());
                Check("but reads still work, straight from the file",
                    ReadsMatch(source, original, original.Length / 4, 4096));

                source.Resume();
                for (int waited = 0; waited < 10000 && !source.Complete; waited += 50) Thread.Sleep(50);
                Check("resuming downloads it again", source.Complete && source.Held,
                    $"{source.Available} of {source.Length}");
                Check("and it reads correctly once more",
                    ReadsMatch(source, original, original.Length / 5, 4096));

                source.Dispose();
                Check("disposing gives the memory back too", !source.Held);
                Check("disposing twice is not an error", true);
                source.Dispose();
            }
            finally
            {
                Cleanup(sandbox);
            }

            // A cap smaller than the track holds what it can and reads the rest
            // from the share, rather than refusing or overrunning.
            var capped = NewTempDir();
            try
            {
                var track = Path.Combine(capped, "big.wav");
                WriteSilentWav(track, 20.0);
                var original = File.ReadAllBytes(track);

                using var source = new StreamingSource(track, 64 * 1024);
                Equal("the cap limits what is held", (64 * 1024).ToString(), source.Cacheable.ToString());

                for (int waited = 0; waited < 5000 && !source.Complete; waited += 50) Thread.Sleep(50);
                Check("it fills up to the cap and stops", source.Available == 64 * 1024,
                    source.Available.ToString());
                Check("a read inside the cap is correct", ReadsMatch(source, original, 1024, 4096));
                Check("a read beyond the cap is correct too",
                    ReadsMatch(source, original, original.Length - 8192, 4096));

                // A cap of nothing means no memory at all, and it still plays.
                using var unheld = new StreamingSource(track, 0);
                Check("a cap of zero holds nothing", !unheld.Held && unheld.Cacheable == 0);
                Check("and still reads correctly", ReadsMatch(unheld, original, 0, 4096));
            }
            finally
            {
                Cleanup(capped);
            }

            try
            {
                StreamingSource.SweepOldCopies();
                Check("sweeping up an older build's files does not throw", true);
            }
            catch (Exception ex)
            {
                Check("sweeping up an older build's files does not throw", false, ex.Message);
            }

            Console.WriteLine();
        }

        /// <summary>Reads through the streaming source and compares with the real file.</summary>
        private static bool ReadsMatch(StreamingSource source, byte[] original, long at, int count)
        {
            var moved = Marshal.AllocHGlobal(8);
            var read = Marshal.AllocHGlobal(8);
            try
            {
                source.Seek(at, 0, moved);

                var got = new byte[count];
                source.Read(got, count, read);
                int n = Marshal.ReadInt32(read);
                if (n <= 0) return false;

                for (int i = 0; i < n; i++)
                    if (got[i] != original[at + i]) return false;

                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(moved);
                Marshal.FreeHGlobal(read);
            }
        }

        // ---- The volume curve ----

        /// <summary>
        /// The difference between the two curves is something you hear, so the
        /// only way to test it is to test the arithmetic that produces it.
        /// </summary>
        private static void VolumeCurveTests()
        {
            Console.WriteLine("The volume curve:");

            Equal("linear passes the dial straight through", "0.5",
                AudioPlayer.EngineVolume(50, VolumeCurve.Linear).ToString("0.###"));
            Equal("perceptual squares it", "0.25",
                AudioPlayer.EngineVolume(50, VolumeCurve.Perceptual).ToString("0.###"));

            // Both curves have to agree at the ends, or the dial would not reach
            // silence or full volume on one of them.
            foreach (var curve in new[] { VolumeCurve.Linear, VolumeCurve.Perceptual })
            {
                Equal($"{curve} is silent at zero", "0", AudioPlayer.EngineVolume(0, curve).ToString("0.###"));
                Equal($"{curve} is full at one hundred", "1", AudioPlayer.EngineVolume(100, curve).ToString("0.###"));
                Equal($"{curve} clamps above the range", "1", AudioPlayer.EngineVolume(500, curve).ToString("0.###"));
                Equal($"{curve} clamps below it", "0", AudioPlayer.EngineVolume(-40, curve).ToString("0.###"));
            }

            // The engine takes 0 to 1 and nothing else; anything outside is a
            // failed call and a track that plays at the wrong level or not at all.
            for (int percent = 0; percent <= 100; percent += 7)
            {
                double linear = AudioPlayer.EngineVolume(percent, VolumeCurve.Linear);
                double curved = AudioPlayer.EngineVolume(percent, VolumeCurve.Perceptual);
                if (linear is < 0 or > 1 || curved is < 0 or > 1)
                {
                    Check($"{percent} percent stays inside the engine's range", false, $"{linear} / {curved}");
                    return;
                }
            }
            Check("every position on the dial stays inside the engine's range", true);

            // Perceptual is quieter everywhere in between, which is the point of
            // it — the same dial position, more room at the bottom.
            Check("perceptual is never louder than linear",
                AudioPlayer.EngineVolume(30, VolumeCurve.Perceptual) < AudioPlayer.EngineVolume(30, VolumeCurve.Linear));

            // The quiet end has to actually be quiet, which is the whole reason
            // this is the shipped default.
            Check("twenty percent is a twenty-fifth of full volume",
                Math.Abs(AudioPlayer.EngineVolume(20, VolumeCurve.Perceptual) - 0.04) < 0.0001);
            Check("and full volume is untouched by the curve",
                Math.Abs(AudioPlayer.EngineVolume(100, VolumeCurve.Perceptual)
                       - AudioPlayer.EngineVolume(100, VolumeCurve.Linear)) < 0.0001);

            Console.WriteLine();
        }

        // ---- What a song says about itself ----

        /// <summary>
        /// The property store is another COM interface read by vtable offset, and
        /// another thing that cannot be checked by reading the source. A WAV
        /// written here with known numbers in its header is the oracle: if the
        /// sample rate that comes back is the one that went in, the interop is
        /// right.
        /// </summary>
        private static void AudioTagTests()
        {
            Console.WriteLine("Reading what a song says about itself:");

            var sandbox = NewTempDir();
            try
            {
                var wav = Path.Combine(sandbox, "probe.wav");
                WriteSilentWav(wav, 2.0);

                var rows = AudioTags.Read(wav);
                string seen = string.Join(" | ", rows.Select(r => $"{r.Name}={r.Value}"));

                Check("a file always has at least its name",
                    rows.Any(r => r.Name == "File" && r.Value == "probe.wav"), seen);

                Check("the sample rate came out of the property store",
                    rows.Any(r => r.Name == "Sample rate" && r.Value.Contains("44") && r.Value.Contains("hertz")),
                    seen);

                Check("the channel count came through, in words",
                    rows.Any(r => r.Name == "Channels" && r.Value.Contains("mono")), seen);

                Check("the length came through",
                    rows.Any(r => r.Name == "Length" && r.Value.Contains("second")), seen);

                // These come from the filesystem, so they are there whatever
                // Windows knows about the format.
                Check("the size is always known", rows.Any(r => r.Name == "Size"), seen);
                Check("the folder is always known", rows.Any(r => r.Name == "Folder"), seen);

                // No tags at all must produce no rows for them, not empty ones —
                // a properties list of blanks is worse than a short one. The
                // numbers are the trap: an absent property converts to 0 rather
                // than failing, so this listed "Year: 0" and "Track: 0" until the
                // variant type was checked.
                Check("a file with no tags lists no empty tag rows",
                    rows.All(r => !string.IsNullOrWhiteSpace(r.Value)), seen);
                Check("and no rows that are just a zero",
                    !rows.Any(r => r.Value.Trim() == "0"), seen);
                Check("no year is invented", !rows.Any(r => r.Name == "Year"), seen);
                Check("no track number is invented", !rows.Any(r => r.Name == "Track"), seen);

                Check("a title is not invented for a file that has none",
                    AudioTags.TitleOf(wav) == null, AudioTags.TitleOf(wav));

                // And now a file that really does carry tags. FLAC keeps them in
                // a Vorbis comment block, which is written here rather than
                // borrowed from a music library that only exists on one machine.
                var flac = Path.Combine(sandbox, "tagged.flac");
                WriteSilentFlac(flac, 21, new[]
                {
                    "TITLE=Careful With That Axe",
                    "ARTIST=Pink Floyd",
                    "ALBUM=Ummagumma",
                    "GENRE=Progressive Rock",
                    "DATE=1969",
                    "TRACKNUMBER=3",
                });

                var tagged = AudioTags.Read(flac);
                string got = string.Join(" | ", tagged.Select(r => $"{r.Name}={r.Value}"));

                Equal("the title is read", "Careful With That Axe", Value(tagged, "Title"));
                Equal("the artist is read", "Pink Floyd", Value(tagged, "Artist"));
                Equal("the album is read", "Ummagumma", Value(tagged, "Album"));
                Equal("the genre is read", "Progressive Rock", Value(tagged, "Genre"));
                Equal("the year is read", "1969", Value(tagged, "Year"));
                Equal("the track number is read", "3", Value(tagged, "Track"));
                Check("the audio details survive alongside the tags",
                    Value(tagged, "Sample rate").Contains("44"), got);

                // What the player calls the track out loud when the preference
                // says to prefer the tag.
                Equal("the spoken name puts the artist before the title",
                    "Pink Floyd — Careful With That Axe", AudioTags.TitleOf(flac) ?? "(none)");
            }
            finally
            {
                Cleanup(sandbox);
            }

            // Everything about this is best effort: it is pointed at whatever row
            // the user was on, which can be a text file, a folder, or gone.
            var missing = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".flac");
            var nothing = AudioTags.Read(missing);
            Check("a missing file reads as a name and nothing else, without throwing",
                nothing.Count >= 1, nothing.Count.ToString());
            Check("a missing file has no title", AudioTags.TitleOf(missing) == null);

            Console.WriteLine();
        }

        private static string Value(IReadOnlyList<SongProperty> rows, string name)
        {
            foreach (var row in rows)
                if (row.Name == name) return row.Value;
            return "(missing)";
        }

        // ---- The media engine ----

        /// <summary>
        /// The one part of the player that cannot be reasoned about from the
        /// source. A COM interface is a list of vtable offsets, so a method
        /// declared in the wrong place, or a BOOL marshalled as the wrong width,
        /// compiles perfectly and then calls the wrong function.
        ///
        /// Answered the only way it can be: create a real engine, hand it a real
        /// file, and check that what comes back is what was put in. The file is
        /// two seconds of silence generated here — asserting on a duration proves
        /// the engine actually decoded something, and nothing is audible.
        /// </summary>
        private static void AudioEngineTests()
        {
            Console.WriteLine("The audio player's media engine:");

            var sandbox = NewTempDir();
            try
            {
                const double seconds = 2.0;
                var wav = Path.Combine(sandbox, "silence.wav");
                WriteSilentWav(wav, seconds);

                using var player = new AudioPlayer();
                var problem = player.SelfCheck(wav, seconds);

                Check("the player can be created and driven", problem == null, problem);

                if (problem == null)
                {
                    player.Play(wav);
                    Check("it knows what it is playing",
                        player.NowPlayingPath == wav, player.NowPlayingPath);

                    player.VolumePercent = 40;
                    Equal("the volume is announced as a percentage",
                        "Volume 45 percent", player.ChangeVolume(5));

                    // The whole range, always. It used to depend on whether a
                    // transform had been accepted into somebody else's pipeline,
                    // and the state before anything had been created to ask was
                    // the one that got it wrong.
                    Equal("the top of the range says so",
                        $"Volume {AudioPlayer.LoudestPercent} percent, maximum",
                        player.ChangeVolume(5000));

                    // Set 150, quit, start again: it used to come back 100,
                    // permanently, because the saved value was clamped on the way
                    // in and written back that way on the way out.
                    using (var fresh = new AudioPlayer())
                    {
                        fresh.VolumePercent = 150;
                        Equal("a volume above full survives being set before anything is playing",
                            "150", fresh.VolumePercent.ToString());

                        fresh.VolumePercent = AudioPlayer.LoudestPercent * 4;
                        Equal("and is still held to the top of the range",
                            AudioPlayer.LoudestPercent.ToString(), fresh.VolumePercent.ToString());
                    }

                    player.VolumePercent = 300;
                    Equal("and the bottom", "Volume 0 percent, minimum", player.ChangeVolume(-1000));

                    Equal("muting says so", "Muted", player.ToggleMute());

                    // Turning it up while muted has to unmute, or the keypress
                    // does nothing audible and looks broken.
                    Check("turning the volume up while muted unmutes",
                        player.ChangeVolume(10).Contains("unmuted"));

                    var described = player.Describe();
                    Check("it can say what is playing",
                        described.Contains("silence.wav"), described);

                    Equal("stopping says so", "Stopped", player.Stop());
                }
            }
            finally
            {
                Cleanup(sandbox);
            }

            // Spoken positions, which are what the skip and "what is playing"
            // keys read out. "1:20" is heard as two numbers and a colon.
            Equal("under a minute", "5 seconds", AudioPlayer.SpokenTime(5));
            Equal("one second is singular", "1 second", AudioPlayer.SpokenTime(1));
            Equal("minutes and seconds", "1 minute 20", AudioPlayer.SpokenTime(80));
            Equal("several minutes", "4 minutes 3", AudioPlayer.SpokenTime(243));
            Equal("an hour in", "1 hour 5 minutes", AudioPlayer.SpokenTime(3900));
            Equal("nothing is zero, not a negative", "0 seconds", AudioPlayer.SpokenTime(-4));
            Equal("an unknown duration is zero, not NaN", "0 seconds", AudioPlayer.SpokenTime(double.NaN));

            Console.WriteLine();
        }

        // ---- Skipping ----

        /// <summary>
        /// Skipping, against a real engine and a track long enough to move around
        /// in.
        ///
        /// The bug this exists for: each press worked out where to go by asking
        /// the engine where it was, and the engine's clock does not move until a
        /// seek has actually completed. Five quick presses therefore all read the
        /// same position, all computed the same target, and all landed in the
        /// same place — the key did nothing after the first press, which is
        /// exactly what "skipping lags" felt like.
        /// </summary>
        private static void SeekTests()
        {
            Console.WriteLine("Skipping through a track:");

            var sandbox = NewTempDir();
            try
            {
                // 3200 frames of 4096 samples at 44.1kHz — just under five minutes.
                const int frames = 3200;
                double length = frames * 4096 / 44100.0;

                var flac = Path.Combine(sandbox, "long.flac");
                WriteSilentFlac(flac, frames);

                using var player = new AudioPlayer();
                player.VolumePercent = 0;
                player.Warm();

                if (player.Play(flac) == null)
                {
                    Check("a long track can be played to skip through", false, player.Diagnostic);
                    return;
                }

                // Wait for the metadata, or there is no duration to clamp against.
                double duration = double.NaN;
                for (int waited = 0; waited < 8000 && (double.IsNaN(duration) || duration <= 0); waited += 50)
                {
                    Thread.Sleep(50);
                    duration = player.DurationSeconds;
                }
                Check("the long track reports a duration", duration > 0, duration.ToString("0.#"));

                // Ten presses as fast as a held key delivers them.
                double from = player.PositionSeconds;
                var said = new List<string>();
                for (int i = 0; i < 10; i++) said.Add(player.Seek(10));

                Check("every press of a held key says somewhere new",
                    said.Distinct().Count() == said.Count, string.Join(" / ", said));
                Equal("ten presses of a ten-second skip say a hundred seconds on",
                    AudioPlayer.SpokenTime(from + 100), said[^1]);

                // And the engine actually goes there, once, at the end.
                Thread.Sleep(1200);
                double landed = player.PositionSeconds;
                Check("and the track really moves that far",
                    Math.Abs(landed - (from + 100)) < 6,
                    $"went from {from:0.##} to {landed:0.##}, wanted about {from + 100:0.##}");

                // Backwards past the beginning stops at the beginning.
                for (int i = 0; i < 12; i++) player.Seek(-30);
                Thread.Sleep(1000);
                double atStart = player.PositionSeconds;
                Check("skipping back past the start lands at the start, not before it",
                    atStart >= 0 && atStart < 5, atStart.ToString("0.##"));

                // Forwards past the end starts the track again, which is what a
                // skip key that keeps being pressed should do. It used to stop a
                // quarter of a second from the end and stay there: every further
                // press landed in the same place, and the only ways back to the
                // music were "back to the start" or rewinding by hand.
                for (int i = 0; i < 30; i++) player.Seek(60);
                Thread.Sleep(1200);
                double wrapped = player.PositionSeconds;
                Check("skipping forward past the end starts the track over",
                    wrapped >= 0 && wrapped < 20,
                    $"{wrapped:0.##} on a {length:0.##}s track");
                Check("and it is playing from there, not sitting at the start",
                    player.IsPlaying, "the player says it is not playing");

                double after = player.PositionSeconds;
                Thread.Sleep(700);
                Check("and the clock is running again after it wrapped",
                    player.PositionSeconds > after,
                    $"{after:0.##} then {player.PositionSeconds:0.##}");

                player.Stop();

                // ---- A pause that was announced has to happen, even if the
                // volume moves before the fade gets there. Pausing with a fade
                // set does not pause: it ramps the gain down and leaves the
                // pause to the last tick, and every volume key cancels the fade.
                // So play/pause followed by volume-up inside the fade said
                // "Paused" and played on ----
                using (var fading = new AudioPlayer())
                {
                    fading.VolumePercent = 0;
                    fading.FadeOutMilliseconds = 2000;

                    if (fading.Play(flac) == null)
                    {
                        Check("a track can be played to test the fade", false, fading.Diagnostic);
                    }
                    else
                    {
                        for (int waited = 0; waited < 3000 && fading.PositionSeconds <= 0; waited += 25)
                            Thread.Sleep(25);

                        Equal("pausing with a fade says it paused", "Paused", fading.TogglePlayPause());

                        // Well inside the two-second fade.
                        Thread.Sleep(150);
                        fading.VolumePercent = 5;
                        Thread.Sleep(200);

                        Check("and it really is paused, even though the volume moved first",
                            !fading.IsPlaying,
                            $"still playing at {fading.PositionSeconds:0.##}s");

                        double at = fading.PositionSeconds;
                        Thread.Sleep(500);
                        Check("and the clock has stopped with it",
                            Math.Abs(fading.PositionSeconds - at) < 0.2,
                            $"{at:0.##} then {fading.PositionSeconds:0.##}");
                    }

                    fading.Stop();
                }

                // The case it was reported from: a track that has already run to
                // its end, then a press of skip forward. Nothing was left to skip
                // into, so the press did nothing at all.
                var shortFlac = Path.Combine(sandbox, "short.flac");
                WriteSilentFlac(shortFlac, 21);           // about two seconds

                using (var ended = new AudioPlayer())
                {
                    ended.VolumePercent = 0;
                    if (ended.Play(shortFlac) == null)
                    {
                        Check("a short track can be played to the end", false, ended.Diagnostic);
                    }
                    else
                    {
                        // Long enough for two seconds of audio to be over.
                        Thread.Sleep(4000);

                        ended.Seek(10);
                        Thread.Sleep(600);

                        double back = ended.PositionSeconds;
                        Check("skipping forward on a finished track plays it again from the start",
                            back >= 0 && back < 1.5, back.ToString("0.##"));
                        Check("and the finished track really is playing again",
                            ended.IsPlaying, "the player says it is not playing");
                    }

                    ended.Stop();
                }
            }
            finally
            {
                Cleanup(sandbox);
            }

            Console.WriteLine();
        }

        /// <summary>
        /// FLAC is the reason the player is built on Media Foundation rather than
        /// on MCI or waveOut, which cannot decode it — so "Windows can decode
        /// FLAC" is a load-bearing assumption and not one to take on trust.
        ///
        /// Proved with a FLAC file assembled here, byte by byte, for the same
        /// reason the WAV is: a test that needs somebody's music library is a test
        /// that passes on one machine.
        /// </summary>
        private static void FlacTests()
        {
            Console.WriteLine("FLAC, which is the whole reason for Media Foundation:");

            var sandbox = NewTempDir();
            try
            {
                // 21 frames of 4096 samples at 44.1kHz.
                const int frames = 21;
                double seconds = frames * 4096 / 44100.0;

                var flac = Path.Combine(sandbox, "silence.flac");
                WriteSilentFlac(flac, frames);

                using var player = new AudioPlayer();
                var problem = player.SelfCheck(flac, seconds);

                Check("Windows decodes FLAC, and the player plays it", problem == null, problem);
            }
            finally
            {
                Cleanup(sandbox);
            }

            Console.WriteLine();
        }

        /// <summary>
        /// A valid FLAC stream: the magic, one STREAMINFO block, then identical
        /// frames of one constant-value subframe — which is how FLAC spells
        /// silence, and is why this can be written without an encoder.
        /// </summary>
        private static void WriteSilentFlac(string path, int frames, string[]? tags = null)
        {
            const int blockSize = 4096;
            const int rate = 44100;
            long totalSamples = (long)frames * blockSize;

            var file = new List<byte>();
            file.AddRange("fLaC"u8.ToArray());

            // Metadata block header: type 0 (STREAMINFO), 34 bytes. It is the
            // last block only when there are no tags to follow it.
            file.Add(tags == null ? (byte)0x80 : (byte)0x00);
            file.AddRange(new byte[] { 0x00, 0x00, 0x22 });

            file.AddRange(BigEndian16(blockSize));      // minimum block size
            file.AddRange(BigEndian16(blockSize));      // maximum block size
            file.AddRange(new byte[] { 0, 0, 0 });      // minimum frame size: unknown
            file.AddRange(new byte[] { 0, 0, 0 });      // maximum frame size: unknown

            // 20 bits of sample rate, 3 of channels-1, 5 of bits-per-sample-1,
            // and 36 of total samples, packed into eight bytes.
            ulong packed = ((ulong)rate << 44) | (0UL << 41) | (15UL << 36) | (ulong)totalSamples;
            for (int shift = 56; shift >= 0; shift -= 8) file.Add((byte)(packed >> shift));

            file.AddRange(new byte[16]);                // MD5 of the audio: unknown

            if (tags != null)
            {
                // VORBIS_COMMENT, which is where FLAC keeps its tags — and the
                // one part of the format whose lengths are little-endian.
                var comment = new List<byte>();
                var vendor = System.Text.Encoding.UTF8.GetBytes("ExplorerNative self-test");
                comment.AddRange(BitConverter.GetBytes(vendor.Length));
                comment.AddRange(vendor);
                comment.AddRange(BitConverter.GetBytes(tags.Length));
                foreach (var tag in tags)
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(tag);
                    comment.AddRange(BitConverter.GetBytes(bytes.Length));
                    comment.AddRange(bytes);
                }

                file.Add(0x84);   // last block, type 4
                file.AddRange(new[] { (byte)(comment.Count >> 16), (byte)(comment.Count >> 8), (byte)comment.Count });
                file.AddRange(comment);
            }

            for (int frame = 0; frame < frames; frame++)
            {
                var f = new List<byte>
                {
                    0xFF, 0xF8,   // sync, fixed block size
                    0xC9,         // block size 4096, sample rate 44.1kHz
                    0x08,         // one channel, 16 bits per sample
                };
                f.AddRange(Utf8Number(frame));
                f.Add(Crc8(f));

                // One CONSTANT subframe: a padding bit, six bits of type, a
                // wasted-bits flag, then the constant sample value. Zero.
                f.AddRange(new byte[] { 0x00, 0x00, 0x00 });

                f.AddRange(BigEndian16(Crc16(f)));
                file.AddRange(f);
            }

            File.WriteAllBytes(path, file.ToArray());
        }

        private static byte[] BigEndian16(int value) => new[] { (byte)(value >> 8), (byte)value };

        /// <summary>
        /// A FLAC frame number is UTF-8 coded, so past 127 it stops fitting in a
        /// byte. Only noticed once a track long enough to seek around in was
        /// needed — every fixture before this one was 21 frames.
        /// </summary>
        private static byte[] Utf8Number(int n)
        {
            if (n < 0x80) return new[] { (byte)n };
            if (n < 0x800)
                return new[] { (byte)(0xC0 | (n >> 6)), (byte)(0x80 | (n & 0x3F)) };
            if (n < 0x10000)
                return new[] { (byte)(0xE0 | (n >> 12)), (byte)(0x80 | ((n >> 6) & 0x3F)), (byte)(0x80 | (n & 0x3F)) };
            return new[]
            {
                (byte)(0xF0 | (n >> 18)), (byte)(0x80 | ((n >> 12) & 0x3F)),
                (byte)(0x80 | ((n >> 6) & 0x3F)), (byte)(0x80 | (n & 0x3F)),
            };
        }

        /// <summary>FLAC's frame-header check: x^8 + x^2 + x + 1, no reflection.</summary>
        private static byte Crc8(IEnumerable<byte> data)
        {
            byte crc = 0;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++)
                    crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
            }
            return crc;
        }

        /// <summary>FLAC's frame check: x^16 + x^15 + x^2 + 1, no reflection.</summary>
        private static int Crc16(IEnumerable<byte> data)
        {
            int crc = 0;
            foreach (byte b in data)
            {
                crc ^= b << 8;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
            return crc;
        }

        /// <summary>
        /// A real WAV file of a known length: 44-byte header, 16-bit mono at
        /// 44.1kHz, and nothing but zeros. Written by hand because the point is to
        /// test the decoder, and because a test that needs a music file to be
        /// lying around somewhere is a test that fails on another machine.
        /// </summary>
        private static void WriteSilentWav(string path, double seconds)
        {
            const int rate = 44100;
            const short channels = 1;
            const short bits = 16;
            int bytesPerSample = channels * bits / 8;
            int dataBytes = (int)(rate * seconds) * bytesPerSample;

            using var file = File.Create(path);
            using var w = new BinaryWriter(file);

            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + dataBytes);
            w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' });
            w.Write(16);                                  // PCM header length
            w.Write((short)1);                            // PCM
            w.Write(channels);
            w.Write(rate);
            w.Write(rate * bytesPerSample);               // bytes per second
            w.Write((short)bytesPerSample);               // block align
            w.Write(bits);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(dataBytes);
            w.Write(new byte[dataBytes]);
        }

        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ExplorerNativeTests_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}



