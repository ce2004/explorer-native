using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The Drive monitor must never make a second copy of anything, on Drive
    /// or on the PC, in the case it was asked for: a D: drive of about two
    /// million files that Google Drive for desktop's mirror option already put
    /// into an ordinary My Drive folder of the same tree, and a sync pair made
    /// over the two. Nothing may be uploaded unless Google was asked and the
    /// file is really not there; what is already there is adopted.
    ///
    /// A: the pure planner at scale and on awkward names. B: plan, apply,
    /// plan again until nothing is left, with random edits in between. C: the
    /// real engine and DriveClient against FakeDrive, with lost answers,
    /// crashes, renames and broken listings. D: overlapping pairs.
    ///
    /// EXPLORERNATIVE_SYNCDUP_PART=B|names|lost runs one section;
    /// EXPLORERNATIVE_SYNCDUP_HUGE=1 adds the two-million-file planner run and
    /// EXPLORERNATIVE_SYNCDUP_HEAVY=1 the 100,050-file engine pass.
    /// </summary>
    internal static class SyncDuplicateTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;
        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        private const double Fast = 0.0005;
        private static readonly DateTime T0 = new(2024, 5, 1, 9, 0, 0, DateTimeKind.Utc);

        /// <summary>When the desktop app uploaded: Drive's modifiedTime in the variant where it is not the PC's.</summary>
        private static readonly DateTime UploadedAt = new(2025, 6, 10, 18, 30, 0, DateTimeKind.Utc);

        private static void Finding(string text) => Console.WriteLine("  FINDING  " + text);

        // ---------------- running just these ----------------

        /// <summary>
        /// Runs before Main. Only when asked by the environment: the suite's
        /// Main is not touched for a subset run.
        /// </summary>
        [ModuleInitializer]
        internal static void OnlyThese()
        {
            var only = Environment.GetEnvironmentVariable("EXPLORERNATIVE_SELFTEST_ONLY");
            if (only == "syncdup-nfc")
            {
                Console.WriteLine(NameProbe());
                Console.Out.Flush();
                Environment.Exit(0);
            }
            // Only this synchronous probe runs here: async work started from a
            // module initializer deadlocks (pool threads wait for the module).
        }

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;
            Console.WriteLine("Drive monitor never duplicates (mirror of D: already on Drive):");

            var previous = Settings.OverrideAppDataDir;
            var sandbox = Path.Combine(Path.GetTempPath(), "en-syncdup-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            Settings.OverrideAppDataDir = Path.Combine(sandbox, "appdata");
            try
            {
                var part = Environment.GetEnvironmentVariable("EXPLORERNATIVE_SYNCDUP_PART");
                if (part == "B") { Idempotence(); return; }
                if (part == "names")
                {
                    await HostileNames(sandbox, "mirrored");
                    await HostileNames(sandbox, "PC only");
                    await HostileNames(sandbox, "Drive only");
                    await DriveSideClashes(sandbox);
                    return;
                }
                if (part == "lost") { await LostAnswers(sandbox); return; }

                // A
                PlannerScale(200_000);
                if (Environment.GetEnvironmentVariable("EXPLORERNATIVE_SYNCDUP_HUGE") == "1") PlannerScale(2_000_000);
                StateSaveCost(Environment.GetEnvironmentVariable("EXPLORERNATIVE_SYNCDUP_HUGE") == "1" ? 2_000_000 : 200_000);
                PlannerModes();
                PlannerTimesAndChecksums();
                PlannerNames();
                InvariantGlobalisationProbe();

                // B
                Idempotence();

                // C
                await FirstSyncOverMirror(sandbox, driveTimeIsUploadTime: false);
                await FirstSyncOverMirror(sandbox, driveTimeIsUploadTime: true);
                await NothingUploadedBeforeListingIsWhole(sandbox);
                await LostAnswers(sandbox);
                await CrashMidPass(sandbox);
                await CrashDuringFirstSync(sandbox);
                await FolderRename(sandbox);
                await BrokenListing(sandbox);
                await DuplicatesAlreadyOnDrive(sandbox);
                await AccentedNamesOnDrive(sandbox);
                await HostileNames(sandbox, "mirrored");
                await HostileNames(sandbox, "PC only");
                await HostileNames(sandbox, "Drive only");
                await DriveSideClashes(sandbox);
                await HugeFolderListing(sandbox);
                if (Environment.GetEnvironmentVariable("EXPLORERNATIVE_SYNCDUP_HEAVY") == "1") await HugeFolderPass(sandbox);

                // D
                await OverlappingPairs(sandbox);
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(sandbox, true); } catch { }
            }
        }

        // =====================================================================
        // A. The planner, pure
        // =====================================================================

        private static string PathOf(int i) => $"Photos/{i / 10000:D3}/{(i / 100) % 100:D2}/IMG_{i:D7}.JPG";
        private static string HashOf(int i) => i.ToString("x32");
        private static long SizeOf(int i) => 1000 + (i * 7919L) % 5_000_000;

        private static readonly Dictionary<string, SyncBase> NoBase = new(SyncNameComparer.Instance);

        private static Dictionary<SyncActionKind, int> Tally(List<SyncAction> plan)
        {
            var t = new Dictionary<SyncActionKind, int>();
            foreach (var a in plan) t[a.Kind] = t.TryGetValue(a.Kind, out var n) ? n + 1 : 1;
            return t;
        }

        private static string Show(Dictionary<SyncActionKind, int> t) =>
            t.Count == 0 ? "nothing" : string.Join(", ", t.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value:N0}"));

        private static int Moves(Dictionary<SyncActionKind, int> t) =>
            t.Where(kv => kv.Key is not (SyncActionKind.Record or SyncActionKind.Forget)).Sum(kv => kv.Value);

        /// <summary>
        /// N files on the PC, the same N in Drive: names, sizes, times, MD5s
        /// as the mirror leaves them. Drive's spelling is a separate string,
        /// as the scans make it. Measured.
        /// </summary>
        private static void PlannerScale(int n)
        {
            Console.WriteLine($"  -- planner at {n:N0} files --");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long before = GC.GetTotalMemory(true);
            var clock = Stopwatch.StartNew();

            var local = new Dictionary<string, SyncFile>(n, SyncNameComparer.Instance);
            var remote = new Dictionary<string, SyncFile>(n, SyncNameComparer.Instance);
            var remoteLater = new Dictionary<string, SyncFile>(n, SyncNameComparer.Instance);
            var md5 = new Dictionary<string, string>(n, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < n; i++)
            {
                var path = PathOf(i);
                var drivePath = new string(path.AsSpan());
                var when = T0.AddSeconds(i).AddMilliseconds(i % 1000);
                local[path] = new SyncFile(SizeOf(i), when);
                remote[drivePath] = new SyncFile(SizeOf(i), when, "id" + i, HashOf(i));
                remoteLater[drivePath] = new SyncFile(SizeOf(i), UploadedAt.AddSeconds(i / 50), "id" + i, HashOf(i));
                md5[path] = HashOf(i);
            }
            long built = GC.GetTotalMemory(false);
            var buildMs = clock.ElapsedMilliseconds;

            var rules = new SyncRules(SyncMode.TwoWay, CopyDeletes: true);

            clock.Restart();
            var need = SyncPlanner.NeedingHash(rules, local, remote, NoBase);
            var needMs = clock.ElapsedMilliseconds;
            Check($"{n:N0}: every PC file of a matching size is asked to be hashed (Drive gives MD5s)", need.Count == n,
                $"{need.Count:N0}");

            clock.Restart();
            var plan = SyncPlanner.Ordered(SyncPlanner.Plan(rules, local, remote, NoBase, md5, new SyncSkips()), local, remote);
            var planMs = clock.ElapsedMilliseconds;
            long planned = GC.GetTotalMemory(false);
            var t = Tally(plan);
            Check($"{n:N0} mirrored files, first sync, same times and MD5s: all adopted, nothing copied, renamed or deleted",
                t.Count == 1 && t.GetValueOrDefault(SyncActionKind.Record) == n, Show(t));

            // Applied as the engine does: Record writes the base.
            clock.Restart();
            var last = new Dictionary<string, SyncBase>(n, SyncNameComparer.Instance);
            foreach (var a in plan)
            {
                var l = local[a.Path];
                var r = remote[a.Path];
                last[a.Path] = new SyncBase(l.Size, l.ModifiedUtc, r.Size, r.ModifiedUtc, r.Id!, r.Md5);
            }
            var again = SyncPlanner.Plan(rules, local, remote, last, null, new SyncSkips());
            var againMs = clock.ElapsedMilliseconds;
            Check($"{n:N0}: the second plan is empty", again.Count == 0, Show(Tally(again)));
            Check($"{n:N0}: and the second pass hashes nothing", SyncPlanner.NeedingHash(rules, local, remote, last).Count == 0);

            // Drive's time is when the desktop app uploaded it, days later.
            clock.Restart();
            var later = SyncPlanner.Plan(rules, local, remoteLater, NoBase, md5, new SyncSkips());
            var laterMs = clock.ElapsedMilliseconds;
            t = Tally(later);
            Check($"{n:N0}: Drive's times are the upload time (a year off), MD5s match: still all adopted",
                t.Count == 1 && t.GetValueOrDefault(SyncActionKind.Record) == n, Show(t));

            // Before the PC's MD5s are known: nothing at all happens.
            var waiting = SyncPlanner.Plan(rules, local, remote, NoBase, new Dictionary<string, string>(), new SyncSkips());
            Check($"{n:N0}: while the PC's MD5s are not yet known nothing is planned (no upload on a guess)",
                waiting.Count == 0, Show(Tally(waiting)));

            // The control: the same run with Drive's MD5s different must upload, or this test proves nothing.
            var otherMd5 = new Dictionary<string, string>(md5.Count, StringComparer.OrdinalIgnoreCase);
            int k = 0;
            foreach (var (p, h) in md5) otherMd5[p] = (k++ % 1000 == 0) ? "ffff" + h[4..] : h;
            var control = Tally(SyncPlanner.Plan(rules, local, remote, NoBase, otherMd5, new SyncSkips()));
            Check($"{n:N0} (control): one file in a thousand with other contents is not adopted, and is a conflict",
                control.GetValueOrDefault(SyncActionKind.Record) == n - (n + 999) / 1000 &&
                control.GetValueOrDefault(SyncActionKind.KeepBothLocalWins) + control.GetValueOrDefault(SyncActionKind.KeepBothRemoteWins)
                == (n + 999) / 1000, Show(control));

            GC.KeepAlive(plan);
            long peak = Process.GetCurrentProcess().PeakWorkingSet64;
            Console.WriteLine($"  MEASURE  {n:N0} files: build {buildMs} ms, NeedingHash {needMs} ms, Plan+Ordered {planMs} ms, " +
                              $"apply+replan {againMs} ms, plan (later times) {laterMs} ms; managed heap " +
                              $"{(built - before) / 1048576} MB for the three tables, +{(planned - built) / 1048576} MB for the plan; " +
                              $"peak working set {peak / 1048576} MB");
        }

        /// <summary>
        /// What one state save costs with N records: the whole state is
        /// serialised and encrypted on every save (DriveMonitor.SaveState).
        /// </summary>
        private static void StateSaveCost(int n)
        {
            var files = new Dictionary<string, SyncBase>(n, SyncNameComparer.Instance);
            var hashes = new Dictionary<string, SyncHashEntry>(n, SyncNameComparer.Instance);
            for (int i = 0; i < n; i++)
            {
                var when = T0.AddSeconds(i);
                files[PathOf(i)] = new SyncBase(SizeOf(i), when, SizeOf(i), when, "1AbCdEfGhIjKlMnOpQrStUvWxYz" + i, HashOf(i));
                hashes[PathOf(i)] = new SyncHashEntry(SizeOf(i), when, HashOf(i));
            }
            var clock = Stopwatch.StartNew();
            var json = JsonSerializer.Serialize(new { Files = files, Hashes = hashes });
            var serialise = clock.ElapsedMilliseconds;
            var file = Path.Combine(Path.GetTempPath(), "en-syncdup-state-" + Guid.NewGuid().ToString("N")[..8] + ".json");
            clock.Restart();
            try { ProtectedFile.WriteAllText(file, json); }
            finally { try { File.Delete(file); } catch { } }
            var write = clock.ElapsedMilliseconds;
            Console.WriteLine($"  MEASURE  one state save with {n:N0} records + {n:N0} hashes: serialise {serialise} ms, " +
                              $"encrypt+write {write} ms, {json.Length / 1048576} MB of JSON");
            long cost = serialise + write, gap = DriveMonitor.SaveGap(cost);
            Console.WriteLine($"  MEASURE  so saves come every {gap} ms, {100.0 * cost / (gap + cost):F1}% of a pass spent saving");
            Check($"state saves at {n:N0} records take at most 5% of a pass (adaptive gap), not one save per action",
                cost * 19 <= gap, $"cost {cost} ms, gap {gap} ms");
        }

        /// <summary>The same first sync under every mode and conflict choice: nothing moves.</summary>
        private static void PlannerModes()
        {
            const int n = 20_000;
            var local = new Dictionary<string, SyncFile>(SyncNameComparer.Instance);
            var remote = new Dictionary<string, SyncFile>(SyncNameComparer.Instance);
            var md5 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < n; i++)
            {
                local[PathOf(i)] = new SyncFile(SizeOf(i), T0.AddSeconds(i));
                remote[PathOf(i)] = new SyncFile(SizeOf(i), UploadedAt, "id" + i, HashOf(i));
                md5[PathOf(i)] = HashOf(i);
            }
            foreach (var mode in new[] { SyncMode.TwoWay, SyncMode.UploadOnly, SyncMode.DownloadOnly })
            foreach (var deletes in new[] { false, true })
            foreach (var choice in new[] { ConflictChoice.KeepBoth, ConflictChoice.PcWins, ConflictChoice.DriveWins })
            {
                var t = Tally(SyncPlanner.Plan(new SyncRules(mode, deletes, choice), local, remote, NoBase, md5, new SyncSkips()));
                Check($"first sync, {mode}, deletes {(deletes ? "on" : "off")}, {choice}: {n:N0} adopted, nothing moves",
                    Moves(t) == 0 && t.GetValueOrDefault(SyncActionKind.Record) == n, Show(t));
            }
        }

        private static List<SyncAction> PlanOne(SyncRules rules, string localName, SyncFile l, string driveName, SyncFile r,
            string? localMd5)
        {
            var local = new Dictionary<string, SyncFile>(SyncNameComparer.Instance) { [localName] = l };
            var remote = new Dictionary<string, SyncFile>(SyncNameComparer.Instance) { [driveName] = r };
            var md5 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in SyncPlanner.NeedingHash(rules, local, remote, NoBase))
                if (localMd5 != null) md5[p] = localMd5;
            return SyncPlanner.Plan(rules, local, remote, NoBase, md5, new SyncSkips());
        }

        private static string Kinds(List<SyncAction> plan) => plan.Count == 0 ? "nothing" : string.Join(",", plan.Select(a => a.Kind));

        private static void PlannerTimesAndChecksums()
        {
            var two = new SyncRules(SyncMode.TwoWay, false);
            var up = new SyncRules(SyncMode.UploadOnly, false);
            var down = new SyncRules(SyncMode.DownloadOnly, false);
            const string h = "0123456789abcdef0123456789abcdef";

            Equal("same size and MD5, times a year apart: adopted", "Record",
                Kinds(PlanOne(two, "a.bin", new SyncFile(10, T0), "a.bin", new SyncFile(10, UploadedAt, "x", h), h)));
            Equal("MD5 written in capitals by one side: adopted", "Record",
                Kinds(PlanOne(two, "a.bin", new SyncFile(10, T0), "a.bin", new SyncFile(10, T0, "x", h.ToUpperInvariant()), h)));
            Equal("same size, other MD5, same time: a conflict, never adopted (the control)", "KeepBothLocalWins",
                Kinds(PlanOne(two, "a.bin", new SyncFile(10, T0), "a.bin", new SyncFile(10, T0, "x", h), "ff" + h[2..])));
            Equal("same MD5 claimed but other size: not adopted", "KeepBothLocalWins",
                Kinds(PlanOne(two, "a.bin", new SyncFile(11, T0), "a.bin", new SyncFile(10, T0, "x", h), h)));

            // No MD5 on Drive (Drive gives none for some files): size plus time within two seconds.
            Equal("no MD5, same size, times 2.000 s apart: adopted", "Record",
                Kinds(PlanOne(two, "a.bin", new SyncFile(10, T0), "a.bin", new SyncFile(10, T0.AddSeconds(2), "x"), null)));
            Equal("no MD5, times 1.999 s apart the other way: adopted", "Record",
                Kinds(PlanOne(two, "a.bin", new SyncFile(10, T0.AddMilliseconds(1999)), "a.bin", new SyncFile(10, T0, "x"), null)));
            var off = PlanOne(two, "a.bin", new SyncFile(10, T0), "a.bin", new SyncFile(10, UploadedAt, "x"), null);
            var offUp = PlanOne(up, "a.bin", new SyncFile(10, T0), "a.bin", new SyncFile(10, UploadedAt, "x"), null);
            var offDown = PlanOne(down, "a.bin", new SyncFile(10, T0), "a.bin", new SyncFile(10, UploadedAt, "x"), null);
            Check("no MD5, same size, times far apart: never silently adopted (cannot know they are the same)",
                off.All(a => a.Kind != SyncActionKind.Record));
            Finding($"no MD5 on Drive + same size + time off by more than 2 s plans: two-way {Kinds(off)} (a renamed conflict " +
                    $"copy, i.e. a second copy on both sides), upload-only {Kinds(offUp)} (in place, same id), download-only " +
                    $"{Kinds(offDown)} (PC copy recycled first). Drive gives MD5 for every binary file in My Drive, so a desktop " +
                    "mirror does not hit this; files without MD5 and with an upload-time modifiedTime would.");

            // Google documents are not files: DriveMonitor's scan leaves them out, so a PC .docx of the same
            // name is a new file. Shown at the planner as the scan hands it over (no remote entry).
            var docx = SyncPlanner.Plan(two, new Dictionary<string, SyncFile>(SyncNameComparer.Instance) { ["Notes.docx"] = new(5, T0) },
                new Dictionary<string, SyncFile>(SyncNameComparer.Instance), NoBase);
            Equal("a PC file beside nothing on Drive goes up (the only way an upload is planned on a first sync)", "Upload", Kinds(docx));
        }

        private static void PlannerNames()
        {
            var two = new SyncRules(SyncMode.TwoWay, true);
            const string h = "0123456789abcdef0123456789abcdef";

            // Capitals only.
            var caps = PlanOne(two, "Music/ABBA/Waterloo.MP3", new SyncFile(10, T0), "music/abba/waterloo.mp3",
                new SyncFile(10, T0, "x", h), h);
            Check("names differing only in capitals are one file: adopted, under the PC's spelling",
                Kinds(caps) == "Record" && caps[0].Path == "Music/ABBA/Waterloo.MP3", Kinds(caps) + " " + caps.FirstOrDefault()?.Path);

            // NFC on the PC, NFD on Drive (a Mac uploaded it), and the other way.
            string nfc = "Café/Björk - Jóga.flac", nfd = "Café/Björk - Jóga.flac";
            Equal("accents as one character on the PC, two on Drive: adopted", "Record",
                Kinds(PlanOne(two, nfc, new SyncFile(10, T0), nfd, new SyncFile(10, T0, "x", h), h)));
            Equal("and the other way round", "Record",
                Kinds(PlanOne(two, nfd, new SyncFile(10, T0), nfc, new SyncFile(10, T0, "x", h), h)));

            // A thousand of both kinds at once, mixed with plain names.
            var local = new Dictionary<string, SyncFile>(SyncNameComparer.Instance);
            var remote = new Dictionary<string, SyncFile>(SyncNameComparer.Instance);
            var md5 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 1000; i++)
            {
                string pc = (i % 3) switch { 0 => $"Café {i}.mp3", 1 => $"TRACK {i}.MP3", _ => $"plain {i}.mp3" };
                string drive = (i % 3) switch { 0 => $"Café {i}.mp3", 1 => $"track {i}.mp3", _ => $"plain {i}.mp3" };
                local[pc] = new SyncFile(i + 1, T0);
                remote[drive] = new SyncFile(i + 1, T0, "id" + i, HashOf(i));
                md5[pc] = HashOf(i);
            }
            var t = Tally(SyncPlanner.Plan(two, local, remote, NoBase, md5, new SyncSkips()));
            Check("1,000 names, a third NFD on Drive and a third in other capitals: 1,000 adopted, nothing moves",
                Moves(t) == 0 && t.GetValueOrDefault(SyncActionKind.Record) == 1000, Show(t));

            // Names Windows cannot hold: the scan leaves Drive's out (DriveMonitor.ScanRemoteAsync, unusable), so
            // the PC's look-alike under a cleaned name meets nothing.
            var unusable = new[] { ("a:b.txt", "a_b.txt"), ("trail.", "trail"), ("CON.txt", "CON_.txt"), ("q?.mp3", "q_.mp3") };
            foreach (var (driveName, pcName) in unusable)
                Check($"Drive's \"{driveName}\" is not a usable Windows name (left out of the scan, never matched to \"{pcName}\")",
                    !NameRules.IsUsableName(driveName));
            Finding("a PC file whose name is a cleaned version of an unusable Drive name (\"a_b.txt\" beside Drive's \"a:b.txt\") " +
                    "is planned as an Upload: a second copy under the clean name. A desktop mirror of D: never makes such names " +
                    "(every name came from Windows), so the mirror case is unaffected.");

            // Skipped by name on both sides: never uploaded.
            var junk = new Dictionary<string, SyncFile>(SyncNameComparer.Instance)
            {
                ["desktop.ini"] = new(10, T0), ["Thumbs.db"] = new(10, T0), ["~$report.docx"] = new(10, T0),
                ["x.tmp"] = new(10, T0), ["film.mkv.partial"] = new(10, T0),
            };
            Equal("desktop.ini, Thumbs.db, Office locks, .tmp and .partial are never uploaded", "nothing",
                Kinds(SyncPlanner.Plan(two, junk, new Dictionary<string, SyncFile>(SyncNameComparer.Instance), NoBase)));
        }

        /// <summary>
        /// The app is built with InvariantGlobalization; the suite is not. Whether
        /// NFC and NFD names still compare equal in the shipped configuration is
        /// asked of a child process run in invariant mode.
        /// </summary>
        private static void InvariantGlobalisationProbe()
        {
            var here = NameProbe();
            Check("in the suite's own process, NFC and NFD names are one name", here.Contains("nfc-nfd=same"), here);

            var exe = Environment.ProcessPath;
            if (exe == null || !exe.EndsWith("SelfTest.exe", StringComparison.OrdinalIgnoreCase))
            {
                Check("invariant-mode probe could run (needs SelfTest.exe)", false, exe);
                return;
            }
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            start.Environment["EXPLORERNATIVE_SELFTEST_ONLY"] = "syncdup-nfc";
            start.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            bool done = child.WaitForExit(60_000);
            var said = done ? output.Result.Trim() : "(timed out)";
            Console.WriteLine("  PROBE  invariant mode: " + said);
            Check("the probe ran in invariant mode, as the app does", said.Contains("invariant=True"), said);
            Check("in invariant mode (the shipped app) NFC and NFD names are still one name, so an NFD Drive name is adopted",
                said.Contains("nfc-nfd=same") && said.Contains("plan=Record"), said);
            Check("and capitals beyond ASCII still compare equal", said.Contains("case=same"), said);
        }

        private static string NameProbe()
        {
            string nfc = "Café Ångström.mp3", nfd = "Café Ångström.mp3";
            string Ask(Func<bool> q) { try { return q() ? "same" : "different"; } catch (Exception ex) { return "threw " + ex.GetType().Name; } }
            var same = Ask(() => SyncNameComparer.Instance.Equals(nfc, nfd));
            var caps = Ask(() => SyncNameComparer.Instance.Equals("ÄRGER.txt", "ärger.txt"));
            string plan;
            try
            {
                const string h = "0123456789abcdef0123456789abcdef";
                plan = Kinds(PlanOne(new SyncRules(SyncMode.TwoWay, false), nfc, new SyncFile(10, T0), nfd,
                    new SyncFile(10, T0, "x", h), h));
            }
            catch (Exception ex) { plan = "threw " + ex.GetType().Name; }
            object? invariant = Type.GetType("System.Globalization.GlobalizationMode")
                ?.GetProperty("Invariant", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            return $"nfc-nfd={same}; case={caps}; plan={plan}; invariant={invariant}";
        }

        // =====================================================================
        // B. Plan, apply, plan again
        // =====================================================================

        private sealed class SimFile
        {
            public long Size;
            public DateTime Modified;
            public string Md5 = "";
            public string Id = "";
            public SimFile Clone() => (SimFile)MemberwiseClone();
        }

        /// <summary>Both sides and the state, changed exactly as DriveMonitor changes them for each action.</summary>
        private sealed class Sim
        {
            public readonly Dictionary<string, SimFile> L = new(SyncNameComparer.Instance);
            public readonly Dictionary<string, SimFile> R = new(SyncNameComparer.Instance);
            public readonly Dictionary<string, SyncBase> B = new(SyncNameComparer.Instance);
            public SyncRules Rules;
            private int _ids;
            public int SameBytesInPlace, NewOnDrive, InPlace, Downloads, Conflicts, Trashed, Recycled, Pointless;
            public readonly List<string> Why = new();
            private void Waste(SyncAction a)
            {
                Pointless++;
                B.TryGetValue(a.Path, out var b);
                L.TryGetValue(a.Path, out var l);
                R.TryGetValue(a.Path, out var r);
                Why.Add($"{a.Kind} {a.Path}: L {l?.Size}/{l?.Modified:O}/{l?.Md5?[..6]} R {r?.Size}/{r?.Modified:O}/{r?.Md5?[..6]} " +
                        $"B {b?.LocalSize}/{b?.LocalModifiedUtc:O} {b?.RemoteSize}/{b?.RemoteModifiedUtc:O}/{b?.RemoteMd5?[..6]}");
            }
            private static readonly DateTime Day = new(2026, 10, 12);

            public Sim(SyncRules rules) => Rules = rules;
            public string NewId() => "n" + (++_ids);

            public List<SyncAction> Plan()
            {
                var local = new Dictionary<string, SyncFile>(SyncNameComparer.Instance);
                foreach (var (p, f) in L) local[p] = new SyncFile(f.Size, f.Modified);
                var remote = new Dictionary<string, SyncFile>(SyncNameComparer.Instance);
                foreach (var (p, f) in R) remote[p] = new SyncFile(f.Size, f.Modified, f.Id, f.Md5);
                var md5 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in SyncPlanner.NeedingHash(Rules, local, remote, B)) md5[p] = L[p].Md5;
                return SyncPlanner.Ordered(SyncPlanner.Plan(Rules, local, remote, B, md5), local, remote);
            }

            private SyncBase Base(SimFile l, SimFile r) => new(l.Size, l.Modified, r.Size, r.Modified, r.Id, r.Md5);

            public void Apply(List<SyncAction> plan)
            {
                foreach (var a in plan)
                {
                    var p = a.Path;
                    switch (a.Kind)
                    {
                        case SyncActionKind.Upload:
                        {
                            var l = L[p];
                            if (R.TryGetValue(p, out var r))
                            {
                                if (r.Md5 == l.Md5 && r.Size == l.Size) SameBytesInPlace++;
                                r.Size = l.Size; r.Modified = l.Modified; r.Md5 = l.Md5;
                                InPlace++;
                            }
                            else
                            {
                                R[p] = r = new SimFile { Size = l.Size, Modified = l.Modified, Md5 = l.Md5, Id = NewId() };
                                NewOnDrive++;
                            }
                            B[p] = Base(l, r);
                            break;
                        }
                        case SyncActionKind.Download:
                        {
                            var r = R[p];
                            if (L.TryGetValue(p, out var had) && had.Md5 == r.Md5) Waste(a);
                            var l = new SimFile { Size = r.Size, Modified = r.Modified, Md5 = r.Md5 };
                            if (had != null) L.Remove(p);
                            L[p] = l;
                            B[p] = Base(l, r);
                            Downloads++;
                            break;
                        }
                        case SyncActionKind.DeleteRemote: R.Remove(p); B.Remove(p); Trashed++; break;
                        case SyncActionKind.DeleteLocal: L.Remove(p); B.Remove(p); Recycled++; break;
                        case SyncActionKind.KeepBothLocalWins:
                        {
                            if (L[p].Md5 == R[p].Md5) Waste(a);
                            var aside = SyncPlanner.ConflictName(p, Day, x => R.ContainsKey(x) || L.ContainsKey(x));
                            var old = R[p];
                            R.Remove(p);
                            R[aside] = old;
                            var l = L[p];
                            var r = new SimFile { Size = l.Size, Modified = l.Modified, Md5 = l.Md5, Id = NewId() };
                            R[p] = r;
                            B[p] = Base(l, r);
                            Conflicts++;
                            break;
                        }
                        case SyncActionKind.KeepBothRemoteWins:
                        {
                            if (L[p].Md5 == R[p].Md5) Waste(a);
                            var aside = SyncPlanner.ConflictName(p, Day, x => R.ContainsKey(x) || L.ContainsKey(x));
                            var mine = L[p];
                            L.Remove(p);
                            L[aside] = mine;
                            var r = R[p];
                            var l = new SimFile { Size = r.Size, Modified = r.Modified, Md5 = r.Md5 };
                            L[p] = l;
                            B[p] = Base(l, r);
                            Conflicts++;
                            break;
                        }
                        case SyncActionKind.Record: B[p] = Base(L[p], R[p]); break;
                        case SyncActionKind.Forget: B.Remove(p); break;
                    }
                }
            }

            /// <summary>Plans and applies until nothing is left: the number of rounds that did something.</summary>
            public int Settle(int max = 5)
            {
                for (int round = 0; round < max; round++)
                {
                    var plan = Plan();
                    if (plan.Count == 0) return round;
                    Apply(plan);
                }
                return max;
            }
        }

        private static void Idempotence()
        {
            foreach (var (rules, label) in new[]
                     {
                         (new SyncRules(SyncMode.TwoWay, false), "two-way"),
                         (new SyncRules(SyncMode.TwoWay, true), "two-way with deletes"),
                         (new SyncRules(SyncMode.UploadOnly, false), "upload only"),
                         (new SyncRules(SyncMode.UploadOnly, true), "upload only with deletes"),
                     })
            {
                for (int seed = 1; seed <= 3; seed++)
                {
                    var sim = new Sim(rules);
                    var random = new Random(seed * 7919);
                    var clock = T0;
                    string Fresh() => Guid.NewGuid().ToString("N");
                    for (int i = 0; i < 2000; i++)
                    {
                        var f = new SimFile { Size = SizeOf(i), Modified = T0.AddSeconds(i), Md5 = HashOf(i) };
                        sim.L[PathOf(i)] = f;
                        var g = f.Clone();
                        g.Id = sim.NewId();
                        g.Modified = random.Next(2) == 0 ? f.Modified : UploadedAt;
                        sim.R[PathOf(i)] = g;
                    }
                    var first = sim.Plan();
                    var t = Tally(first);
                    bool firstOk = t.Count == 1 && t.GetValueOrDefault(SyncActionKind.Record) == 2000;
                    sim.Apply(first);
                    bool secondEmpty = sim.Plan().Count == 0;

                    int worstRounds = 0, newNames = 0;
                    var sameEdits = new List<string>();
                    for (int round = 0; round < 40; round++)
                    {
                        for (int op = 0; op < 25; op++)
                        {
                            clock = clock.AddMinutes(1);
                            var keys = sim.L.Keys.Concat(sim.R.Keys).Distinct(SyncNameComparer.Instance).ToList();
                            var p = keys[random.Next(keys.Count)];
                            switch (random.Next(10))
                            {
                                case 0:
                                    if (sim.L.TryGetValue(p, out var le)) { le.Md5 = Fresh(); le.Size = random.Next(1, 999999); le.Modified = clock; }
                                    break;
                                case 1:
                                    if (sim.R.TryGetValue(p, out var re)) { re.Md5 = Fresh(); re.Size = random.Next(1, 999999); re.Modified = clock; }
                                    break;
                                case 2:
                                    sim.L[$"new/pc {round}-{op}.bin"] = new SimFile { Size = 5, Modified = clock, Md5 = Fresh() };
                                    newNames++;
                                    break;
                                case 3:
                                    sim.R[$"new/drive {round}-{op}.bin"] = new SimFile { Size = 6, Modified = clock, Md5 = Fresh(), Id = sim.NewId() };
                                    newNames++;
                                    break;
                                case 4: sim.L.Remove(p); break;
                                case 5: sim.R.Remove(p); break;
                                case 6:
                                    // Edited to the very same bytes on both sides (the desktop app still running for a bit).
                                    if (sim.L.TryGetValue(p, out var a) && sim.R.TryGetValue(p, out var b))
                                    {
                                        a.Md5 = b.Md5 = Fresh(); a.Size = b.Size = random.Next(1, 999999);
                                        a.Modified = clock; b.Modified = clock.AddMinutes(5);
                                        sameEdits.Add(p);
                                    }
                                    break;
                                case 7:
                                {
                                    // Made on both sides independently, the same bytes: a mirror copy that landed meanwhile.
                                    var name = $"both/made {round}-{op}.bin";
                                    var md5 = Fresh();
                                    sim.L[name] = new SimFile { Size = 77, Modified = clock, Md5 = md5 };
                                    sim.R[name] = new SimFile { Size = 77, Modified = UploadedAt, Md5 = md5, Id = sim.NewId() };
                                    break;
                                }
                                case 8:
                                    // A rename on the PC: a delete and an add of the same bytes.
                                    if (sim.L.Remove(p, out var moved)) sim.L[p + ".renamed"] = moved;
                                    break;
                                case 9:
                                    // Capitals changed on the PC only.
                                    if (sim.L.Remove(p, out var recased)) sim.L[p.ToUpperInvariant()] = recased;
                                    break;
                            }
                        }
                        worstRounds = Math.Max(worstRounds, sim.Settle());
                    }

                    bool converged = sim.Plan().Count == 0;
                    bool equalSides = rules.Mode != SyncMode.TwoWay ||
                                      (sim.L.Count == sim.R.Count && sim.L.All(kv => sim.R.TryGetValue(kv.Key, out var r) && r.Md5 == kv.Value.Md5));
                    string detail = $"rounds {worstRounds}, new on Drive {sim.NewOnDrive}, in place {sim.InPlace}, downloads {sim.Downloads}, " +
                                    $"conflicts {sim.Conflicts}, pointless {sim.Pointless}, same bytes re-sent in place {sim.SameBytesInPlace}, L {sim.L.Count}, R {sim.R.Count}; " + string.Join(" || ", sim.Why.Take(3));
                    Check($"B {label} seed {seed}: 2,000 mirrored files adopted on the first plan, second plan empty",
                        firstOk && secondEmpty, Show(t));
                    Check($"B {label} seed {seed}: after 1,000 random edits every change settles within two passes and stays settled",
                        converged && worstRounds <= 2, detail);
                    Check($"B {label} seed {seed}: no conflict copy or download was ever of bytes the other side already held (no needless second copy)",
                        sim.Pointless == 0, detail);
                    Check($"B {label} seed {seed}: two-way ends with both sides the same", equalSides, detail);
                    if (sim.SameBytesInPlace > 0)
                        Finding($"B {label} seed {seed}: {sim.SameBytesInPlace} upload(s) re-sent identical bytes in place (same id, new revision) " +
                                "because only the PC file's modified time changed; no second copy. " + string.Join(" || ", sim.Why.Take(1)));
                }
            }
        }

        // =====================================================================
        // C. The engine against the fake Drive
        // =====================================================================

        /// <summary>Lets a test see, change or drop any request and answer on the wire.</summary>
        private sealed class Wire : DelegatingHandler
        {
            public Func<HttpRequestMessage, HttpResponseMessage?>? Before;
            public Func<HttpRequestMessage, HttpResponseMessage, HttpResponseMessage>? After;

            public Wire(HttpMessageHandler inner) : base(inner) { }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (Before?.Invoke(request) is { } early) return early;
                var answer = await base.SendAsync(request, ct);
                return After == null ? answer : After(request, answer);
            }
        }

        private static bool IsNewUpload(HttpRequestMessage r) =>
            r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/upload/drive/v3/files";

        private static bool IsFolderCreate(HttpRequestMessage r) =>
            r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/drive/v3/files";

        private static (string Parent, int Page)? ListingOf(HttpRequestMessage r)
        {
            if (r.Method != HttpMethod.Get || r.RequestUri!.AbsolutePath != "/drive/v3/files") return null;
            var q = System.Web.HttpUtility.ParseQueryString(r.RequestUri.Query);
            var m = System.Text.RegularExpressions.Regex.Match(q["q"] ?? "", "'([^']*)' in parents");
            if (!m.Success || (q["q"] ?? "").Contains("appProperties")) return null;
            return (m.Groups[1].Value, int.TryParse(q["pageToken"], out var p) ? p : 0);
        }

        private static readonly string[] WriteKinds = { "multipart", "begin", "put", "create", "trash", "patch", "copy" };

        private sealed class Mirror : IDisposable
        {
            public readonly FakeDrive Drive = new();
            public readonly Wire Net;
            public readonly string Local;
            public readonly string FolderId;
            public readonly DriveSyncPair Pair;
            public readonly List<string> Said = new();
            public DriveClient Client;
            public DriveMonitor Monitor;
            private readonly Dictionary<string, string> _folders = new(StringComparer.Ordinal);

            public Mirror(string sandbox, SyncMode mode = SyncMode.TwoWay, bool deletes = false)
            {
                var top = Path.Combine(sandbox, Guid.NewGuid().ToString("N")[..8]);
                Local = Path.Combine(top, "D");
                Directory.CreateDirectory(Local);
                FolderId = Drive.AddFolder("D (mirrored)");          // an ordinary folder in My Drive
                _folders[""] = FolderId;
                Net = new Wire(Drive);
                Pair = new DriveSyncPair
                {
                    Name = "D", LocalFolder = Local, DriveFolderId = FolderId, DriveFolderPath = "My Drive/D (mirrored)",
                    Mode = mode, CopyDeletes = deletes, CheckMinutes = 0,
                };
                Client = new DriveClient(Net, Fast);
                Monitor = NewMonitor();
            }

            public DriveMonitor NewMonitor() =>
                new(new GoogleDrive(), (id, message) => { lock (Said) Said.Add(id + ": " + message); }, _ => { })
                {
                    WaitScale = Fast,
                    DiskOfRoot = _ => (1L << 42, 1L << 43, "T:"),
                    RecycleLocal = path => { File.Delete(path); return null; },
                    PatienceLimit = TimeSpan.FromSeconds(20),
                };

            public void Restart()
            {
                Monitor.Dispose();
                Client = new DriveClient(Net, Fast);
                Monitor = NewMonitor();
            }

            public string StatePath => Path.Combine(DriveMonitor.StateDirectory, Pair.Id + ".json");

            public async Task<string?> Pass(CancellationToken token = default)
            {
                await Monitor.SyncOneAsync(Client, Pair, token);
                return Monitor.StatusOf(Pair.Id).Problem;
            }

            public void WriteLocal(string relative, byte[] data, DateTime modifiedUtc)
            {
                var path = Path.Combine(Local, relative.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, data);
                File.SetLastWriteTimeUtc(path, modifiedUtc);
            }

            public string DriveFolder(string relative)
            {
                if (_folders.TryGetValue(relative, out var id)) return id;
                int slash = relative.LastIndexOf('/');
                var parent = DriveFolder(slash < 0 ? "" : relative[..slash]);
                return _folders[relative] = Drive.AddFolder(relative[(slash + 1)..], parent);
            }

            public string PutDrive(string relative, byte[] data, DateTime modifiedUtc)
            {
                int slash = relative.LastIndexOf('/');
                return Drive.AddFile(relative[(slash + 1)..], data, modifiedUtc, DriveFolder(slash < 0 ? "" : relative[..slash]));
            }

            /// <summary>What the desktop app's mirror leaves: the same file on both sides.</summary>
            public void Mirrored(string relative, byte[] data, DateTime pcTime, DateTime driveTime)
            {
                WriteLocal(relative, data, pcTime);
                PutDrive(relative, data, driveTime);
            }

            public List<string> LocalFiles()
            {
                if (!Directory.Exists(Local)) return new List<string>();
                return Directory.EnumerateFiles(Local, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(Local, f).Replace('\\', '/')).ToList();
            }

            /// <summary>Every live file under the pair's Drive folder, and every name held twice in one folder.</summary>
            public (int Files, List<string> Twice, List<string> Paths) DriveFiles()
            {
                var items = ((System.Collections.IDictionary)typeof(FakeDrive)
                        .GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Drive)!)
                    .Values.Cast<FakeDrive.Item>().ToList();
                var children = items.Where(i => !i.Trashed).ToLookup(i => i.Parent);
                var twice = new List<string>();
                var paths = new List<string>();
                var queue = new Queue<(string Id, string Prefix)>();
                queue.Enqueue((FolderId, ""));
                while (queue.Count > 0)
                {
                    var (id, prefix) = queue.Dequeue();
                    var here = children[id].ToList();
                    twice.AddRange(here.GroupBy(c => c.Name, SyncNameComparer.Instance).Where(g => g.Count() > 1)
                        .Select(g => $"{prefix}{g.Key} x{g.Count()}"));
                    foreach (var c in here)
                    {
                        if (c.IsFolder) queue.Enqueue((c.Id, prefix + c.Name + "/"));
                        else if (!c.IsDocument) paths.Add(prefix + c.Name);
                    }
                }
                return (paths.Count, twice, paths);
            }

            public int Writes()
            {
                lock (Drive.Kinds) return Drive.Kinds.Count(k => WriteKinds.Contains(k));
            }

            public string Counts() =>
                $"uploads {Drive.Uploads}, sessions {Drive.SessionsBegun}, folder creates {Drive.Creates}, trashes {Drive.Trashes}, " +
                $"renames {Drive.Renames}, media reads {Drive.MediaReads}, in-place updates {Drive.Updates}";

            public void Dispose()
            {
                Monitor.Dispose();
                try { Directory.Delete(Path.GetDirectoryName(Local)!, true); } catch { }
            }
        }

        private static byte[] Bytes(int i, int length)
        {
            var b = new byte[length];
            new Random(i * 31 + 7).NextBytes(b);
            return b;
        }

        private static string TreePath(int i) => $"Music/Artist {i / 250:D2}/Album {(i / 50) % 5}/track {i:D4}.mp3";

        private static void FillMirror(Mirror m, int n, bool driveTimeIsUploadTime, Func<int, string>? pathOf = null)
        {
            pathOf ??= TreePath;
            for (int i = 0; i < n; i++)
            {
                var pcTime = T0.AddSeconds(i).AddMilliseconds(i % 1000);
                m.Mirrored(pathOf(i), Bytes(i, 64 + i % 300), pcTime, driveTimeIsUploadTime ? UploadedAt.AddSeconds(i / 10) : pcTime);
            }
        }

        private static async Task FirstSyncOverMirror(string sandbox, bool driveTimeIsUploadTime)
        {
            const int n = 5000;
            string label = driveTimeIsUploadTime ? "Drive's times are the upload time" : "Drive's times are the PC's";
            using var m = new Mirror(sandbox);
            FillMirror(m, n, driveTimeIsUploadTime);
            // Folders Google keeps that the PC also has, empty: nothing to do with either.
            Directory.CreateDirectory(Path.Combine(m.Local, "Empty on both"));
            m.DriveFolder("Empty on both");

            var clock = Stopwatch.StartNew();
            var problem = await m.Pass();
            var ms = clock.ElapsedMilliseconds;
            var drive = m.DriveFiles();
            Console.WriteLine($"  MEASURE  first sync over {n:N0} mirrored files ({label}): {ms} ms; {m.Counts()}; requests {m.Drive.Requests}");
            Check($"C first sync, {n:N0} mirrored files, {label}: no upload, no session, no folder made",
                m.Drive.Uploads == 0 && m.Drive.SessionsBegun == 0 && m.Drive.Creates == 0, m.Counts());
            Check($"C {label}: nothing renamed, trashed or downloaded, no write request of any kind",
                m.Drive.Trashes == 0 && m.Drive.Renames == 0 && m.Drive.MediaReads == 0 && m.Writes() == 0, m.Counts());
            Check($"C {label}: Drive still holds exactly {n:N0} files, none twice", drive.Files == n && drive.Twice.Count == 0,
                $"{drive.Files} files; twice: {string.Join(", ", drive.Twice.Take(5))}");
            var local = m.LocalFiles();
            Check($"C {label}: the PC still holds exactly {n:N0} files, no conflict copies, no .partial",
                local.Count == n && !local.Any(f => f.Contains("(conflict") || f.EndsWith(".partial")), $"{local.Count} files");
            Check($"C {label}: with no problem", problem == null, problem);

            int writes = m.Writes(), uploads = m.Drive.Uploads;
            problem = await m.Pass();
            Check($"C {label}: the second pass changes nothing either", m.Writes() == writes && m.Drive.Uploads == uploads &&
                                                                      m.Drive.MediaReads == 0 && problem == null, m.Counts());

            // The control: one PC file whose bytes differ from Drive's must not be adopted.
            if (!driveTimeIsUploadTime)
            {
                using var c = new Mirror(sandbox);
                FillMirror(c, 200, false);
                var path = Path.Combine(c.Local, TreePath(7).Replace('/', '\\'));
                var when = File.GetLastWriteTimeUtc(path);
                var bytes = File.ReadAllBytes(path);
                bytes[0] ^= 0xFF;
                File.WriteAllBytes(path, bytes);
                File.SetLastWriteTimeUtc(path, when);
                await c.Pass();
                Check("C (control) the same first sync with one PC file's bytes changed does not adopt it: one conflict copy goes up",
                    c.Drive.Uploads == 1 && c.Drive.Renames == 1, c.Counts());
            }
        }

        private static async Task NothingUploadedBeforeListingIsWhole(string sandbox)
        {
            using var m = new Mirror(sandbox);
            FillMirror(m, 1000, true);
            for (int i = 0; i < 20; i++) m.WriteLocal(TreePath(i * 37).Replace("track", "new track"), Bytes(90_000 + i, 100), T0);
            for (int i = 0; i < 5; i++) m.WriteLocal($"Brand new folder/song {i}.mp3", Bytes(91_000 + i, 100), T0);

            var order = new List<string>();
            int listings = 0;
            m.Net.Before = r =>
            {
                lock (order)
                {
                    if (ListingOf(r) != null) { order.Add("list"); listings++; }
                    else if (IsNewUpload(r) || IsFolderCreate(r) || r.Method == HttpMethod.Put || r.Method == HttpMethod.Patch)
                        order.Add("write");
                }
                return null;
            };
            var problem = await m.Pass();
            int lastList = order.LastIndexOf("list"), firstWrite = order.IndexOf("write");
            var drive = m.DriveFiles();
            // A batch at a time, each batch's folders are listed before anything is
            // written into them, not the whole tree before the first write; the
            // outcome check below is what holds either way.
            if (DriveMonitor.LargePairFiles > 0)
                Check("C every Drive folder of the pair is listed before the first upload or folder create starts",
                    firstWrite > lastList && lastList >= 0, $"last listing at {lastList}, first write at {firstWrite}, {listings} listings");
            else
                Check("C a batch's folders are listed before anything is written", lastList >= 0 && firstWrite > order.IndexOf("list"),
                    $"first listing at {order.IndexOf("list")}, first write at {firstWrite}");
            Check("C only the 25 files really not on Drive go up, into one new folder",
                m.Drive.Uploads == 25 && m.Drive.Creates == 1 && drive.Files == 1025 && drive.Twice.Count == 0 && problem == null,
                m.Counts() + " | " + problem);
        }

        // ---------------- answers lost after Google stored the file ----------------

        private static async Task LostAnswers(string sandbox)
        {
            // (a) a small upload stored, then the connection drops before the answer.
            foreach (var (how, lose) in new (string, Func<HttpResponseMessage, HttpResponseMessage>)[]
                     {
                         ("the connection drops", _ => throw new HttpRequestException("wire: reset after Google stored it")),
                         ("Google answers 503", _ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                         {
                             Content = new StringContent(FakeDrive.ErrorBody(503, "backendError", "Backend Error")),
                         }),
                     })
            {
                using var m = new Mirror(sandbox);
                FillMirror(m, 50, false);
                m.WriteLocal("Music/new.mp3", Bytes(5, 3000), T0);
                int lost = 0;
                m.Net.After = (r, answer) => IsNewUpload(r) && Interlocked.Increment(ref lost) == 1 ? lose(answer) : answer;
                var problem = await m.Pass();
                var after = await m.Pass();
                var drive = m.DriveFiles();
                Check($"C small upload stored, then {how}: Drive holds new.mp3 once",
                    drive.Paths.Count(p => p == "Music/new.mp3") == 1 && drive.Twice.Count == 0 && lost >= 1,
                    $"{m.Counts()}; twice: {string.Join(",", drive.Twice)}; problems: {problem} / {after}");
                Check($"C ({how}) and the next pass sends nothing more", m.Drive.Uploads == 1, m.Counts());
            }

            // (b) answer lost, and Google cannot be asked whether it landed (the tag lookup fails too).
            {
                using var m = new Mirror(sandbox);
                FillMirror(m, 50, false);
                m.WriteLocal("Music/blind.mp3", Bytes(6, 3000), T0);
                bool lookupDown = true;
                m.Net.After = (r, answer) => IsNewUpload(r) ? throw new HttpRequestException("wire: reset after store") : answer;
                m.Net.Before = r =>
                    lookupDown && r.Method == HttpMethod.Get && (r.RequestUri!.Query.Contains("appProperties"))
                        ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                        {
                            Content = new StringContent(FakeDrive.ErrorBody(500, "backendError", "Backend Error")),
                        }
                        : null;
                var p1 = await m.Pass();
                var midway = m.DriveFiles();
                m.Net.After = null;
                lookupDown = false;
                var p2 = await m.Pass();
                var p3 = await m.Pass();
                var drive = m.DriveFiles();
                int copies = drive.Paths.Count(p => p == "Music/blind.mp3");
                Check("C every answer lost and the landed-check failing too: never more than one copy on Drive",
                    copies == 1 && drive.Twice.Count == 0,
                    $"{copies} copies (after pass 1: {midway.Paths.Count(p => p == "Music/blind.mp3")}); {m.Counts()}; problems: {p1} / {p2} / {p3}");
            }

            // (c) a big file (resumable): the last chunk is stored and its answer lost.
            {
                using var m = new Mirror(sandbox);
                FillMirror(m, 20, false);
                m.WriteLocal("Video/big.mkv", Bytes(8, 6 * 1024 * 1024 + 123), T0);
                int dropped = 0;
                m.Net.After = (r, answer) =>
                    r.Method == HttpMethod.Put && (int)answer.StatusCode == 200 && Interlocked.Increment(ref dropped) == 1
                        ? throw new HttpRequestException("wire: reset after the last chunk")
                        : answer;
                var problem = await m.Pass();
                await m.Pass();
                var drive = m.DriveFiles();
                Check("C a resumable upload whose final answer is lost: Drive holds big.mkv once",
                    dropped >= 1 && drive.Paths.Count(p => p == "Video/big.mkv") == 1 && drive.Twice.Count == 0,
                    $"{m.Counts()}; dropped {dropped}; {problem}");
            }

            // (d) a folder made, its answer lost.
            {
                using var m = new Mirror(sandbox);
                FillMirror(m, 20, false);
                for (int i = 0; i < 3; i++) m.WriteLocal($"New Album/{i}.mp3", Bytes(100 + i, 500), T0);
                int dropped = 0;
                m.Net.After = (r, answer) =>
                    IsFolderCreate(r) && Interlocked.Increment(ref dropped) == 1 ? throw new HttpRequestException("wire: reset") : answer;
                var problem = await m.Pass();
                await m.Pass();
                var drive = m.DriveFiles();
                Check("C a folder create whose answer is lost: one \"New Album\" on Drive, holding the 3 files once each",
                    dropped == 1 && drive.Twice.Count == 0 && drive.Paths.Count(p => p.StartsWith("New Album/")) == 3,
                    $"{m.Counts()}; twice: {string.Join(",", drive.Twice)}; {problem}");
            }
        }

        // ---------------- crashes ----------------

        private static async Task CrashMidPass(string sandbox)
        {
            const int mirrored = 2000, fresh = 300;
            foreach (var crash in new[] { true, false })
            {
                using var m = new Mirror(sandbox);
                FillMirror(m, mirrored, true);
                await m.Pass();
                int baseUploads = m.Drive.Uploads;
                for (int i = 0; i < fresh; i++) m.WriteLocal($"Fresh/{i / 50}/clip {i:D3}.wav", Bytes(50_000 + i, 200 + i), T0.AddDays(1));

                byte[]? snapshot = null;
                using var cancel = new CancellationTokenSource();
                int seen = 0;
                if (crash)
                {
                    m.Monitor.KillPoint = what =>
                    {
                        if (!what.StartsWith("uploaded ", StringComparison.Ordinal) || ++seen != fresh / 2 || snapshot != null) return;
                        snapshot = File.Exists(m.StatePath) ? File.ReadAllBytes(m.StatePath) : Array.Empty<byte>();
                        throw new OperationCanceledException("a crash, here");
                    };
                }
                else
                {
                    m.Net.Before = r =>
                    {
                        if (IsNewUpload(r) && Interlocked.Increment(ref seen) == fresh / 2) cancel.Cancel();
                        return null;
                    };
                }
                try { await m.Pass(cancel.Token); }
                catch (OperationCanceledException) { }
                m.Net.Before = null;
                int atCrash = m.Drive.Uploads - baseUploads;

                if (crash)
                {
                    if (snapshot == null) { Check("C crash point reached", false); continue; }
                    if (snapshot.Length == 0) File.Delete(m.StatePath); else File.WriteAllBytes(m.StatePath, snapshot);
                }
                m.Restart();
                var problem = await m.Pass();
                await m.Pass();
                var drive = m.DriveFiles();
                string kind = crash ? "a crash (state file as it was on disk)" : "the pass cancelled";
                Check($"C {kind} half way through {fresh} uploads, then a restart: every file on Drive once",
                    drive.Files == mirrored + fresh && drive.Twice.Count == 0,
                    $"{drive.Files} files; twice: {string.Join(", ", drive.Twice.Take(5))}; {problem}");
                Check($"C ({kind}) nothing that had landed was sent again: {fresh} uploads in all",
                    m.Drive.Uploads - baseUploads == fresh, $"{atCrash} before, {m.Drive.Uploads - baseUploads} in all; {m.Counts()}");
            }
        }

        private static async Task CrashDuringFirstSync(string sandbox)
        {
            const int n = 3000;
            foreach (var after in new[] { 40, 250, 900 })
            {
                using var m = new Mirror(sandbox);
                FillMirror(m, n, true);
                for (int i = 0; i < 30; i++) m.WriteLocal($"Only on PC/{i}.txt", Bytes(70_000 + i, 50), T0);

                byte[]? snapshot = null;
                using var cancel = new CancellationTokenSource();
                cancel.Token.Register(() =>
                {
                    try { snapshot = File.Exists(m.StatePath) ? File.ReadAllBytes(m.StatePath) : Array.Empty<byte>(); }
                    catch { snapshot = Array.Empty<byte>(); }
                });
                cancel.CancelAfter(after);
                try { await m.Pass(cancel.Token); }
                catch (OperationCanceledException) { }
                if (snapshot != null)
                {
                    if (snapshot.Length == 0) File.Delete(m.StatePath); else File.WriteAllBytes(m.StatePath, snapshot);
                }
                m.Restart();
                await m.Pass();
                await m.Pass();
                var drive = m.DriveFiles();
                Check($"C a crash {after} ms into the first sync over {n:N0} mirrored files: only the 30 PC-only files ever go up",
                    m.Drive.Uploads == 30 && drive.Files == n + 30 && drive.Twice.Count == 0 && m.Drive.MediaReads == 0,
                    $"{m.Counts()}; Drive {drive.Files}; twice {drive.Twice.Count}");
            }
        }

        // ---------------- a folder renamed on the PC ----------------

        private static async Task FolderRename(string sandbox)
        {
            foreach (var deletes in new[] { false, true })
            {
                using var m = new Mirror(sandbox, SyncMode.TwoWay, deletes);
                FillMirror(m, 1000, false, i => $"Holiday/{i / 100}/pic {i:D4}.jpg");
                FillMirror(m, 100, false, i => $"Other/{i}.jpg");
                await m.Pass();
                var start = (m.Drive.Uploads, m.Drive.Creates, m.Drive.Trashes, m.Drive.MediaReads);
                Directory.Move(Path.Combine(m.Local, "Holiday"), Path.Combine(m.Local, "Holiday 2024"));

                var problem = await m.Pass();
                var mid = m.DriveFiles();
                await m.Pass();
                var drive = m.DriveFiles();
                var local = m.LocalFiles();
                int up = m.Drive.Uploads - start.Uploads, made = m.Drive.Creates - start.Creates,
                    trashed = m.Drive.Trashes - start.Trashes, down = m.Drive.MediaReads - start.MediaReads;
                Check($"C folder of 1,000 renamed on the PC (deletes {(deletes ? "on" : "off")}): never a name twice in one Drive folder",
                    drive.Twice.Count == 0 && mid.Twice.Count == 0, string.Join(", ", drive.Twice.Take(3)));
                Finding($"folder rename of 1,000 files, deletes {(deletes ? "on" : "off")}: {up} uploads, {made} folder creates, " +
                        $"{trashed} trashes, {down} media reads; Drive files {drive.Files} (was 1,100), PC files {local.Count}. " +
                        (deletes
                            ? "The renamed folder is re-uploaded whole and the old copies trashed (no rename on Drive): 1,000 duplicates exist between the upload and the trash; the old Drive folder stays, empty."
                            : "Permanent: the renamed folder is uploaded as new AND the old folder is downloaded back to the PC, so both sides end with both folders (2,000 photos where there were 1,000)."));
                Check($"C folder rename (deletes {(deletes ? "on" : "off")}) is settled after two passes", problem == null, problem);
            }
        }

        // ---------------- a listing that breaks part way ----------------

        private static async Task BrokenListing(string sandbox)
        {
            var faults = new (string Label, Func<HttpResponseMessage?> Answer)[]
            {
                ("500 on page 3, every time", () => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                    { Content = new StringContent(FakeDrive.ErrorBody(500, "backendError", "Backend Error")) }),
                ("403 forbidden on page 3", () => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
                    { Content = new StringContent(FakeDrive.ErrorBody(403, "forbidden", "No")) }),
                ("400 invalid page token on page 3", () => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
                    { Content = new StringContent(FakeDrive.ErrorBody(400, "invalid", "Invalid Value")) }),
                ("connection dropped on page 3", () => throw new HttpRequestException("wire: reset during listing")),
            };
            foreach (var (label, answer) in faults)
            {
                using var m = new Mirror(sandbox);
                FillMirror(m, 3500, true, i => $"Camera/IMG_{i:D5}.JPG");      // one folder, four pages
                FillMirror(m, 50, true, i => $"Docs/{i}.pdf");
                string camera = m.DriveFolder("Camera");
                bool broken = true;
                int hits = 0;
                m.Net.Before = r =>
                {
                    if (broken && ListingOf(r) is { } l && l.Parent == camera && l.Page == 2000)
                    {
                        Interlocked.Increment(ref hits);
                        return answer();
                    }
                    return null;
                };
                string? problem;
                try { problem = await m.Pass(); }
                catch (Exception ex) { problem = "threw " + ex.GetType().Name + ": " + ex.Message; }
                Check($"C Drive listing fails ({label}): nothing uploaded, no folder made, nothing downloaded or trashed",
                    hits > 0 && m.Drive.Uploads == 0 && m.Drive.Creates == 0 && m.Drive.MediaReads == 0 && m.Drive.Trashes == 0,
                    $"{m.Counts()}; page 3 refused {hits} times; problem: {problem}");
                broken = false;
                problem = await m.Pass();
                var drive = m.DriveFiles();
                Check($"C ({label}) once the listing works again, all 3,550 are adopted, still no upload",
                    m.Drive.Uploads == 0 && drive.Files == 3550 && drive.Twice.Count == 0 && problem == null, $"{m.Counts()}; {problem}");
            }
        }

        // ---------------- Drive already holds a name twice ----------------

        private static async Task DuplicatesAlreadyOnDrive(string sandbox)
        {
            // The desktop app uploaded one file twice (it happens): two identical files of one name.
            {
                using var m = new Mirror(sandbox);
                FillMirror(m, 30, false);
                var bytes = Bytes(4242, 900);
                m.WriteLocal("Music/twice.mp3", bytes, T0);
                m.PutDrive("Music/twice.mp3", bytes, T0);
                m.PutDrive("Music/twice.mp3", bytes, UploadedAt);
                await m.Pass();
                await m.Pass();
                var drive = m.DriveFiles();
                Check("C Drive holds one file twice (same bytes): adopted, nothing uploaded, no third copy",
                    m.Drive.Uploads == 0 && drive.Paths.Count(p => p == "Music/twice.mp3") == 2 && m.LocalFiles().Count == 31,
                    m.Counts());
                Finding("the pre-existing same-name pair on Drive is left as two files (nothing trashes the spare on a first sync).");
            }

            // Two files of one name, the PC's matching the older one.
            {
                using var m = new Mirror(sandbox);
                FillMirror(m, 30, false);
                var mine = Bytes(4343, 900);
                m.WriteLocal("Music/clash.mp3", mine, T0);
                m.PutDrive("Music/clash.mp3", mine, T0);
                m.PutDrive("Music/clash.mp3", Bytes(4444, 950), T0.AddDays(3));
                var problem = await m.Pass();
                await m.Pass();
                var drive = m.DriveFiles();
                var local = m.LocalFiles();
                bool clean = m.Drive.Uploads == 0 && local.Count == 31;
                Check("C Drive holds a name twice and the PC's file is the older of the two: no new copy anywhere", clean,
                    $"{m.Counts()}; Drive copies of clash: {drive.Paths.Count(p => p.Contains("clash"))}; PC: " +
                    string.Join(", ", local.Where(f => f.Contains("clash"))) + $"; {problem}");
                if (!clean)
                    Finding("two same-name Drive files where the PC matches the older: the planner sees only the newer " +
                            "(DriveMonitor.ScanRemoteAsync keeps the newer, older goes to 'shadowed'), calls it a conflict and makes " +
                            "conflict copies, although the PC's file is already on Drive.");
            }
        }

        // ---------------- accents written the Mac way on Drive ----------------

        private static async Task AccentedNamesOnDrive(string sandbox)
        {
            using var m = new Mirror(sandbox);
            for (int i = 0; i < 200; i++)
            {
                var b = Bytes(8000 + i, 120);
                m.WriteLocal($"Café del Mar/Björk {i}.mp3", b, T0);
                m.PutDrive($"Café del Mar/Björk {i}.mp3", b, UploadedAt);
            }
            var problem = await m.Pass();
            var drive = m.DriveFiles();
            Check("C 200 files whose folder and names are NFD on Drive and NFC on the PC: adopted, nothing copied either way",
                m.Drive.Uploads == 0 && m.Drive.Creates == 0 && m.Drive.MediaReads == 0 && drive.Files == 200 &&
                m.LocalFiles().Count == 200, $"{m.Counts()}; {problem}");
        }

        // ---------------- hostile names ----------------

        private static List<(string Label, string Path, DateTime When, int Length, bool ReadOnly)> HostileList()
        {
            var t = T0;
            var list = new List<(string, string, DateTime, int, bool)>();
            void Add(string label, string path, DateTime? when = null, int length = 40, bool ro = false) =>
                list.Add((label, path, when ?? t, length, ro));

            Add("Arabic", "عربي/ملف تجريبي.txt");
            Add("Hebrew with RLM U+200F", "עברית‏/קובץ‏.txt");
            Add("Thai", "ไทย/ไฟล์ทดสอบ.txt");
            Add("Devanagari", "हिन्दी/परीक्षण फ़ाइल.txt");
            Add("Korean", "한국어/테스트 파일.txt");
            Add("Cyrillic", "Русский/Файл.txt");
            Add("Greek", "Ελληνικά/Αρχείο.txt");
            Add("CJK", "中文/测试文件.txt");
            Add("Japanese", "日本語/テスト.txt");
            Add("emoji", "emoji/\U0001F600 grin.txt");
            Add("emoji ZWJ family", "emoji/\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466.txt");
            Add("emoji skin tone", "emoji/\U0001F44D\U0001F3FD.txt");
            Add("emoji flag", "emoji/\U0001F1F3\U0001F1FF.txt");
            Add("combining marks (zalgo)", "marks/Z͑̀a̶l͒g҉o.txt");
            Add("same name NFC in one folder", "nfc/Café.txt");
            Add("same name NFD in another", "nfd/Café.txt");
            Add("zero-width space", "zw/a​b.txt");
            Add("zero-width joiner", "zw/a‍b.txt");
            Add("plain beside zero-width ones", "zw/ab.txt");
            Add("non-breaking space", "space/a b.txt");
            Add("fullwidth", "full/ＡＢＣ１２３.txt");
            Add("ASCII beside fullwidth", "full/ABC123.txt");
            Add("leading spaces", "space/  lead.txt");
            Add("double spaces", "space/a  b.txt");
            Add("starts with dash", "odd/-dash.txt");
            Add("starts with dot", "odd/.dotfile");
            Add("folder starting with dot", ".config/settings.txt");
            Add("no extension", "odd/README");
            Add("many dots", "odd/a.b.c.d.e.f.txt");
            Add("punctuation", "punct/# % & ' [ ] { } ~ ` ^ , ; = + @ ! $ ( ).txt");
            Add("255-char name", "long/" + new string('x', 251) + ".txt");
            Add("255-char CJK name", "long/" + new string('長', 251) + ".txt");
            Add("path over 260 chars", string.Join("/", Enumerable.Range(0, 6).Select(i => $"segment {i} " + new string('p', 50))) + "/far.txt");
            Add("40 levels deep", string.Join("/", Enumerable.Range(1, 40).Select(i => $"d{i}")) + "/deep.txt");
            Add("0-byte file", "sizes/zero.bin", length: 0);
            Add("read-only file", "attrs/readonly.txt", ro: true);
            Add("mtime 1980", "times/old.txt", new DateTime(1980, 1, 1, 0, 0, 2, DateTimeKind.Utc));
            Add("mtime in the future (2099)", "times/future.txt", new DateTime(2099, 12, 31, 23, 59, 58, DateTimeKind.Utc));
            return list;
        }

        /// <summary>Drive names Windows cannot hold: left out, never uploaded over, never deleted, never fetched.</summary>
        private static readonly string[] Unholdable =
        {
            "trail.", "trail ", "a<b.txt", "a>b.txt", "a:b.txt", "a\"b.txt", "a|b.txt", "a?b.txt", "a*b.txt",
            "CON", "NUL.txt", "COM1", "lpt1.mp3", new string('y', 256) + ".txt",
        };

        private static async Task HostileNames(string sandbox, string variant)
        {
            var names = HostileList();
            using var m = new Mirror(sandbox, SyncMode.TwoWay, deletes: true);
            var uploadsOf = new Dictionary<string, int>(StringComparer.Ordinal);
            m.Drive.BeforeAnswer = (kind, subject) =>
            {
                if (kind is "multipart" or "begin") lock (uploadsOf) uploadsOf[subject] = uploadsOf.GetValueOrDefault(subject) + 1;
            };

            var unwritable = new List<string>();
            int i = 0;
            foreach (var (label, path, when, length, ro) in names)
            {
                var data = Bytes(20_000 + i++, length);
                try
                {
                    if (variant != "Drive only")
                    {
                        m.WriteLocal(path, data, when);
                        if (ro) File.SetAttributes(Path.Combine(m.Local, path.Replace('/', '\\')), FileAttributes.ReadOnly);
                    }
                }
                catch (Exception ex) { unwritable.Add($"{label}: {ex.GetType().Name}"); continue; }
                if (variant != "PC only") m.PutDrive(path, data, variant == "mirrored" ? UploadedAt : when);
            }
            foreach (var bad in Unholdable) m.PutDrive("unholdable/" + bad, Bytes(bad.Length, 30), T0);
            var badFolder = m.DriveFolder("unholdable/bad:folder");
            m.Drive.AddFile("inside.txt", Bytes(1, 10), T0, badFolder);
            Check($"hostile names ({variant}): every name could be written on the PC", unwritable.Count == 0, string.Join("; ", unwritable));

            try
            {
                var p1 = await m.Pass();
                var after1 = (m.Drive.Uploads, m.Drive.MediaReads, m.Drive.Trashes);
                var p2 = await m.Pass();
                var after2 = (m.Drive.Uploads, m.Drive.MediaReads, m.Drive.Trashes);

                // A brand-new pair over the same two folders: no records, first-sync rules again.
                var fresh = m.Pair.Copy();
                fresh.Id = Guid.NewGuid().ToString("N");
                await m.Monitor.SyncOneAsync(m.Client, fresh, CancellationToken.None);
                var after3 = (m.Drive.Uploads, m.Drive.MediaReads, m.Drive.Trashes);

                var drive = m.DriveFiles();
                var local = m.LocalFiles();
                int expectUp = variant == "PC only" ? names.Count : 0, expectDown = variant == "Drive only" ? names.Count(n => n.Length > 0) : 0;   // an empty file needs no media read
                foreach (var (label, path, _, _, _) in names)
                {
                    var leaf = path[(path.LastIndexOf('/') + 1)..];
                    int onDrive = drive.Paths.Count(p => p == path);
                    int onPc = local.Count(p => p == path);
                    int sent = uploadsOf.GetValueOrDefault(leaf);
                    Check($"hostile names ({variant}): {label}: once on Drive, once on the PC, sent {(variant == "PC only" ? "once" : "never")}",
                        onDrive == 1 && onPc == 1 && sent == (variant == "PC only" ? 1 : 0),
                        $"Drive {onDrive}, PC {onPc}, uploads {sent}");
                }
                Check($"hostile names ({variant}): first pass moved exactly what was missing",
                    after1.Uploads == expectUp && after1.MediaReads >= expectDown && after1.Trashes == 0,
                    $"{m.Counts()}; {p1}");
                Check($"hostile names ({variant}): the second pass and a brand-new pair move nothing",
                    after2 == after1 && after3 == after1, $"after1 {after1}, after2 {after2}, new pair {after3}; {p2}");
                Check($"hostile names ({variant}): no name twice in any Drive folder, no conflict copies on the PC",
                    drive.Twice.Count == 0 && !local.Any(f => f.Contains("(conflict")),
                    string.Join(", ", drive.Twice.Take(5)) + " | " + string.Join(", ", local.Where(f => f.Contains("(conflict")).Take(5)));
                int unholdableLeft = drive.Paths.Count(p => p.StartsWith("unholdable/"));
                Check($"hostile names ({variant}): {Unholdable.Length + 1} Drive names Windows cannot hold are left alone: not fetched, not trashed",
                    unholdableLeft == Unholdable.Length + 1 && !local.Any(f => f.StartsWith("unholdable/")) && m.Drive.Trashes == 0,
                    $"Drive keeps {unholdableLeft}; PC has {local.Count(f => f.StartsWith("unholdable/"))}; problem: {p1}");
                Check($"hostile names ({variant}): and the pair says they were left out",
                    p1 != null && p1.Contains("left out"), p1);
            }
            finally
            {
                foreach (var f in Directory.Exists(m.Local) ? Directory.EnumerateFiles(m.Local, "*", SearchOption.AllDirectories) : Enumerable.Empty<string>())
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
        }

        /// <summary>Drive holding what Windows cannot: a file and a folder of one name, names differing only in capitals.</summary>
        private static async Task DriveSideClashes(string sandbox)
        {
            using var m = new Mirror(sandbox, SyncMode.TwoWay, deletes: true);
            var a = Bytes(1, 50);
            m.WriteLocal("clash/a.txt", a, T0);
            m.PutDrive("clash/a.txt", a, T0);
            m.PutDrive("clash/A.txt", Bytes(2, 60), T0);
            var inner = Bytes(3, 70);
            m.WriteLocal("clash/x/inner.txt", inner, T0);
            m.PutDrive("clash/x/inner.txt", inner, T0);
            m.PutDrive("clash/x", Bytes(4, 80), T0);              // a file called x beside the folder x
            m.PutDrive("clash/same.txt", Bytes(5, 90), T0);
            m.PutDrive("clash/same.txt", Bytes(6, 91), T0.AddHours(1));
            var problem = await m.Pass();
            await m.Pass();
            var fresh = m.Pair.Copy();
            fresh.Id = Guid.NewGuid().ToString("N");
            await m.Monitor.SyncOneAsync(m.Client, fresh, CancellationToken.None);
            var drive = m.DriveFiles();
            var local = m.LocalFiles();
            Check("Drive clashes (A.txt + a.txt, file x + folder x, same.txt twice): nothing uploaded or trashed, on any pass or a new pair",
                m.Drive.Uploads == 0 && m.Drive.Trashes == 0 && m.Drive.Renames == 0, m.Counts() + " | " + problem);
            Check("Drive clashes: the PC's a.txt is not sent beside Drive's two spellings, x/inner.txt adopted",
                drive.Paths.Count(p => SyncNameComparer.Instance.Equals(p, "clash/a.txt")) == 2 &&
                drive.Paths.Count(p => p == "clash/x/inner.txt") == 1, string.Join(", ", drive.Paths));
            Console.WriteLine($"  NOTE  Drive clashes: PC ends with {string.Join(", ", local)}; problem: {problem}");
        }

        // ---------------- a Drive folder of more than 100,000 files ----------------

        private static async Task HugeFolderListing(string sandbox)
        {
            const int n = 100_050;
            var drive = new FakeDrive();
            var folder = drive.AddFolder("D (mirrored)");
            var camera = drive.AddFolder("Camera", folder);
            for (int i = 0; i < n; i++) drive.AddFile($"IMG_{i:D6}.JPG", Array.Empty<byte>(), T0, camera);
            var client = new DriveClient(drive, Fast);
            var clock = Stopwatch.StartNew();
            var (files, _) = await DriveMonitor.ScanRemoteAsync(client, folder, true, CancellationToken.None);
            Console.WriteLine($"  MEASURE  listing one Drive folder of {n:N0} files: {clock.ElapsedMilliseconds} ms");
            Check($"C a Drive folder holding {n:N0} files is listed whole by the monitor's scan", files.Count == n,
                $"the scan saw {files.Count:N0}: DriveClient.ListChildren stops at its default max of 100,000 " +
                "(DriveClient.cs ListChildren 'int max = 100_000', returned at 'if (results.Count >= max)') and " +
                "DriveMonitor.ListOneFolder calls it without a max");

            // What the planner then does with the PC's same 100,050 files.
            var local = new Dictionary<string, SyncFile>(SyncNameComparer.Instance);
            var md5 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var empty = Convert.ToHexString(MD5.HashData(Array.Empty<byte>())).ToLowerInvariant();
            for (int i = 0; i < n; i++)
            {
                local[$"Camera/IMG_{i:D6}.JPG"] = new SyncFile(0, T0);
                md5[$"Camera/IMG_{i:D6}.JPG"] = empty;
            }
            var t = Tally(SyncPlanner.Plan(new SyncRules(SyncMode.TwoWay, false), local, files, NoBase, md5, new SyncSkips()));
            Check($"C so the first sync over that folder uploads nothing", t.GetValueOrDefault(SyncActionKind.Upload) == 0,
                $"{Show(t)}: each Upload is a second copy of a file Drive already holds");
        }

        /// <summary>The same, through a whole pass, with the files on disk. Slow; on request only.</summary>
        private static async Task HugeFolderPass(string sandbox)
        {
            const int n = 100_050;
            using var m = new Mirror(sandbox);
            var clock = Stopwatch.StartNew();
            var data = Array.Empty<byte>();
            string camera = m.DriveFolder("Camera");
            Directory.CreateDirectory(Path.Combine(m.Local, "Camera"));
            for (int i = 0; i < n; i++)
            {
                var name = $"IMG_{i:D6}.JPG";
                var path = Path.Combine(m.Local, "Camera", name);
                File.WriteAllBytes(path, data);
                File.SetLastWriteTimeUtc(path, T0);
                m.Drive.AddFile(name, data, T0, camera);
            }
            var built = clock.ElapsedMilliseconds;
            clock.Restart();
            var problem = await m.Pass();
            var drive = m.DriveFiles();
            Console.WriteLine($"  MEASURE  heavy: {n:N0} files written in {built} ms, first pass {clock.ElapsedMilliseconds} ms; {m.Counts()}");
            Check($"C heavy: a real first pass over one folder of {n:N0} mirrored files uploads nothing",
                m.Drive.Uploads == 0 && drive.Twice.Count == 0,
                $"{m.Counts()}; Drive now {drive.Files:N0} files, {drive.Twice.Count:N0} names twice; {problem}");
        }

        // =====================================================================
        // D. Overlapping pairs
        // =====================================================================

        private static async Task OverlappingPairs(string sandbox)
        {
            // Pair A: D <-> mirror. Pair B: D\Music\Artist 00 <-> mirror/Music/Artist 00. One after the other, as the
            // monitor runs them (one pair at a time).
            {
                using var m = new Mirror(sandbox);
                FillMirror(m, 600, false);
                for (int i = 0; i < 40; i++) m.WriteLocal($"Music/Artist 00/New/{i}.mp3", Bytes(60_000 + i, 300), T0);
                var inner = new DriveSyncPair
                {
                    Name = "inner", LocalFolder = Path.Combine(m.Local, "Music", "Artist 00"),
                    DriveFolderId = m.DriveFolder("Music/Artist 00"), Mode = SyncMode.TwoWay, CheckMinutes = 0,
                };
                await m.Monitor.SyncOneAsync(m.Client, inner, CancellationToken.None);
                await m.Pass();
                await m.Monitor.SyncOneAsync(m.Client, inner, CancellationToken.None);
                await m.Pass();
                var drive = m.DriveFiles();
                Check("D two pairs over nested folders, run one after the other: each new file goes up once, no name twice",
                    m.Drive.Uploads == 40 && m.Drive.Creates == 1 && drive.Files == 640 && drive.Twice.Count == 0,
                    $"{m.Counts()}; {drive.Files} files; twice: {string.Join(", ", drive.Twice.Take(3))}");

                // Edited on the PC, then each pair passes: updated in place by the first, adopted by the second.
                var edit = Path.Combine(m.Local, "Music", "Artist 00", "New", "3.mp3");
                File.WriteAllBytes(edit, Bytes(1, 444));
                File.SetLastWriteTimeUtc(edit, T0.AddDays(2));
                await m.Pass();
                await m.Monitor.SyncOneAsync(m.Client, inner, CancellationToken.None);
                await m.Pass();
                drive = m.DriveFiles();
                Check("D an edit under both pairs is sent once, in place, and the other pair adopts it",
                    m.Drive.Uploads == 41 && m.Drive.Updates == 1 && m.Drive.Renames == 0 && drive.Files == 640 &&
                    !m.LocalFiles().Any(f => f.Contains("(conflict")), m.Counts());
            }

            // At the same moment: two monitors (as two app instances would be, which the single-instance lock prevents).
            foreach (var samePair in new[] { false, true })
            {
                using var m = new Mirror(sandbox);
                FillMirror(m, 300, false);
                for (int i = 0; i < 40; i++) m.WriteLocal($"Music/Artist 00/New/{i}.mp3", Bytes(61_000 + i, 300), T0);
                var other = samePair
                    ? m.Pair.Copy()
                    : new DriveSyncPair
                    {
                        Name = "inner", LocalFolder = Path.Combine(m.Local, "Music", "Artist 00"),
                        DriveFolderId = m.DriveFolder("Music/Artist 00"), Mode = SyncMode.TwoWay, CheckMinutes = 0,
                    };
                var second = m.NewMonitor();
                try
                {
                    var client2 = new DriveClient(m.Net, Fast);
                    await Task.WhenAll(m.Pass(), second.SyncOneAsync(client2, other, CancellationToken.None));
                }
                finally { second.Dispose(); }
                var drive = m.DriveFiles();
                string what = samePair ? "the same pair passing twice at once" : "two nested pairs passing at once";
                Finding($"{what} (two monitors; the app runs one pass at a time in one instance): {m.Drive.Uploads} uploads for 40 new " +
                        $"files, {m.Drive.Creates} folder creates, names twice: {drive.Twice.Count} ({string.Join(", ", drive.Twice.Take(3))})");
            }

            Check("D the app itself never runs two passes at once: one loop, one pass at a time (DriveMonitor.LoopAsync/PassAsync)",
                SourceHas("DriveMonitor.cs", "private async Task LoopAsync()") &&
                SourceHas("DriveMonitor.cs", "if (!await SyncOneAsync(client, pair, token).ConfigureAwait(false)) return;"));
        }

        private static bool SourceHas(string file, string text)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "src", file);
                if (File.Exists(candidate)) return File.ReadAllText(candidate).Contains(text, StringComparison.Ordinal);
            }
            return false;
        }
    }
}
