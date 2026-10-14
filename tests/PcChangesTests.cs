using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The PC side of a pass that lists only the folders that changed: the
    /// partial scan gives what a whole walk would, the change journal names the
    /// folders that changed (even across a restart), the watcher's account is
    /// trusted only after a whole walk, and a pair synced pass after pass on
    /// that basis ends with both sides the same.
    /// </summary>
    internal static class PcChangesTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private const double Fast = 0.0005;
        private static readonly DateTime T0 = new(2025, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        public static async Task RunAll(Action<string, bool, string?> check)
        {
            _check = check;
            Console.WriteLine("Only the PC folders that changed: partial scans, the change journal, the watcher:");

            var previous = Settings.OverrideAppDataDir;
            var sandbox = Path.Combine(Path.GetTempPath(), "en-pcchanges-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            Settings.OverrideAppDataDir = Path.Combine(sandbox, "appdata");
            try
            {
                PartialScanMatchesAWalk(sandbox);
                WatcherTrust();
                JournalNamesTheFolders(sandbox);
                await PassesUseTheJournal(sandbox, batched: false);
                await PassesUseTheJournal(sandbox, batched: true);
                await RandomRounds(sandbox, batched: false, seed: 11);
                await RandomRounds(sandbox, batched: true, seed: 12);
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(sandbox, true); } catch { }
            }
            Console.WriteLine();
        }

        // ---------------- the partial scan against a whole walk ----------------

        private static void Write(string root, string relative, string text)
        {
            var path = Path.Combine(root, relative.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        /// <summary>Records as a sync would leave them: each file as the PC had it.</summary>
        private static Dictionary<string, SyncBase> RecordsOf(Dictionary<string, SyncFile> scan) =>
            scan.ToDictionary(kv => kv.Key,
                kv => new SyncBase(kv.Value.Size, kv.Value.ModifiedUtc, kv.Value.Size, kv.Value.ModifiedUtc, "id-" + kv.Key),
                SyncNameComparer.Instance);

        /// <summary>What the planner would see: the files, less anything left out.</summary>
        private static string Seen(Dictionary<string, SyncFile> files, SyncSkips skips) =>
            string.Join("|", files.Where(kv => !skips.Covers(kv.Key))
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key}:{kv.Value.Size}:{kv.Value.ModifiedUtc.Ticks}"));

        private static void PartialScanMatchesAWalk(string sandbox)
        {
            var root = Path.Combine(sandbox, "scan");
            void Fresh()
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
                Write(root, "top.txt", "t");
                Write(root, "A/a1.txt", "a1");
                Write(root, "A/a2.txt", "a2");
                Write(root, "A/B/b1.txt", "b1");
                Write(root, "A/B/C/c1.txt", "c1");
                Write(root, "D/d1.txt", "d1");
            }

            void Case(string what, Action change, Action<PcMarks> mark)
            {
                Fresh();
                var records = RecordsOf(DriveMonitor.ScanLocal(root, true));
                change();
                var marks = new PcMarks();
                mark(marks);
                var walkSkips = new SyncSkips();
                var walk = DriveMonitor.ScanLocal(root, true, walkSkips);
                var partSkips = new SyncSkips();
                var part = DriveMonitor.ScanLocalPartly(root, true, records, marks, partSkips, new List<string>(), Array.Empty<string>());
                Check("partial scan: " + what, Seen(walk, walkSkips) == Seen(part, partSkips),
                    $"walk {Seen(walk, walkSkips)}\n      part {Seen(part, partSkips)}");
            }

            Case("a file edited", () => Write(root, "A/a1.txt", "a1 changed"), m => m.Folders.Add("A"));
            Case("a file added deep down", () => Write(root, "A/B/C/c2.txt", "c2"), m => m.Folders.Add("A/B/C"));
            Case("a file deleted", () => File.Delete(Path.Combine(root, @"A\a2.txt")), m => m.Folders.Add("A"));
            Case("a folder renamed, with the new name marked whole",
                () => Directory.Move(Path.Combine(root, @"A\B"), Path.Combine(root, @"A\B2")),
                m => { m.Folders.Add("A"); m.Deep.Add("A/B2"); });
            Case("a folder renamed, with only its parent marked",
                () => Directory.Move(Path.Combine(root, @"A\B"), Path.Combine(root, @"A\B2")), m => m.Folders.Add("A"));
            Case("a folder deleted, its parent marked",
                () => Directory.Delete(Path.Combine(root, @"A\B"), true), m => m.Folders.Add("A"));
            Case("a whole tree moved in, its parent marked",
                () => { Write(root, "D/New/n1.txt", "n1"); Write(root, "D/New/Deeper/n2.txt", "n2"); }, m => m.Folders.Add("D"));
            Case("a folder hidden",
                () => File.SetAttributes(Path.Combine(root, @"A\B"), FileAttributes.Directory | FileAttributes.Hidden),
                m => m.Folders.Add("A"));
            Case("a file hidden",
                () => File.SetAttributes(Path.Combine(root, @"A\a1.txt"), FileAttributes.Hidden), m => m.Folders.Add("A"));
            Case("a folder that is gone, marked itself",
                () => Directory.Delete(Path.Combine(root, "D"), true), m => m.Folders.Add("D"));
            Case("a mark under a hidden folder reaches nothing a walk would not",
                () =>
                {
                    File.SetAttributes(Path.Combine(root, @"A\B"), FileAttributes.Directory | FileAttributes.Hidden);
                    Write(root, "A/B/C/c3.txt", "c3");
                },
                m => { m.Folders.Add("A"); m.Folders.Add("A/B/C"); m.Deep.Add("A/B/C"); });

            // A change nobody reported: the scan stays as the records were. Nothing
            // reads as deleted, and the next whole walk finds it.
            Fresh();
            var before = DriveMonitor.ScanLocal(root, true);
            var recorded = RecordsOf(before);
            Write(root, "D/missed.txt", "missed");
            var unreported = DriveMonitor.ScanLocalPartly(root, true, recorded, new PcMarks(), new SyncSkips(), new List<string>(), Array.Empty<string>());
            Check("an unreported change is not seen, and nothing reads as deleted",
                unreported.Count == recorded.Count && recorded.Keys.All(unreported.ContainsKey), unreported.Count.ToString());
            Check("and a whole walk finds it", DriveMonitor.ScanLocal(root, true).ContainsKey("D/missed.txt"));

            // Names left out for clashing stay said where nothing was looked at.
            var carried = new List<string>();
            DriveMonitor.ScanLocalPartly(root, true, recorded, new PcMarks(), new SyncSkips(), carried, new[] { "D/cafe.txt" });
            Check("a clash found before is still said when its folder is not looked at", carried.SequenceEqual(new[] { "D/cafe.txt" }));
        }

        // ---------------- the watcher's account ----------------

        private static void WatcherTrust()
        {
            var watch = new PcWatchJournal();
            watch.Note("A/x.txt", WatcherChangeTypes.Changed);
            Check("a new watcher is not trusted: the first pass walks everything", watch.Take(out var epoch) == null);
            watch.Walked(epoch);
            watch.Note("A/x.txt", WatcherChangeTypes.Changed);
            watch.Note("B/New", WatcherChangeTypes.Created);
            var marks = watch.Take(out epoch);
            Check("after a whole walk it is, and names the folders",
                marks != null && marks.Folders.Contains("A") && marks.Folders.Contains("B") && marks.Deep.Contains("B/New"));
            Check("taken once, they are gone", watch.Take(out epoch) is { Count: 0 });
            watch.PutBack(marks, epoch);
            Check("a pass that did not get to plan puts them back", watch.Take(out epoch) is { } back && back.Folders.Contains("A"));
            watch.Lose();
            Check("an overflow means a whole walk again", watch.Take(out epoch) == null);
            int stale = epoch;
            watch.Lose();
            watch.Walked(stale);
            Check("a walk that something was lost during does not restore trust", watch.Take(out _) == null);
        }

        // ---------------- the change journal itself ----------------

        private static void JournalNamesTheFolders(string sandbox)
        {
            var root = Path.Combine(sandbox, "journal");
            Write(root, "A/a.txt", "a");
            Write(root, "A/B/b.txt", "b");
            Write(root, "Gone/g.txt", "g");
            var start = UsnJournal.Where(root);
            if (start == null)
            {
                Console.WriteLine("    (no change journal on this volume: the journal tests are skipped)");
                return;
            }

            Write(root, "A/B/b.txt", "b edited");
            Directory.Move(Path.Combine(root, "A", "B"), Path.Combine(root, "A", "Renamed"));
            Directory.Delete(Path.Combine(root, "Gone"), true);
            Write(Path.Combine(sandbox, "elsewhere"), "x.txt", "outside the pair");
            Directory.CreateDirectory(Path.Combine(root, "Made"));

            var now = UsnJournal.Where(root)!;
            var marks = UsnJournal.Since(root, start.JournalId, start.Next, now.Next);
            Check("the journal can be read without administrator rights", marks != null);
            if (marks == null) return;
            Check("an edited file names its folder (by where the folder is now)", marks.Folders.Contains("A/Renamed"),
                string.Join(", ", marks.Folders));
            Check("a renamed folder names its parent, and itself to be taken whole",
                marks.Folders.Contains("A") && marks.Deep.Contains("A/Renamed"), string.Join(", ", marks.Deep));
            Check("a deleted folder names its parent", marks.Folders.Contains(""));
            Check("a new folder is taken whole", marks.Deep.Contains("Made"));
            Check("nothing outside the pair's folder is named",
                marks.Folders.Concat(marks.Deep).All(f => !f.Contains("elsewhere", StringComparison.OrdinalIgnoreCase)));
            Check("another journal's place is refused", UsnJournal.Since(root, start.JournalId + 1, start.Next, now.Next) == null);
            Check("a place before the journal's start is refused", UsnJournal.Since(root, start.JournalId, now.First - 8, now.Next) == null);
        }

        // ---------------- passes ----------------

        private sealed class Rig : IDisposable
        {
            public readonly FakeDrive Drive = new();
            public readonly string Local;
            public readonly string FolderId;
            public readonly DriveSyncPair Pair;
            public DriveClient Client;
            public DriveMonitor Monitor;

            public Rig(string sandbox)
            {
                var top = Path.Combine(sandbox, Guid.NewGuid().ToString("N")[..8]);
                Local = Path.Combine(top, "P");
                Directory.CreateDirectory(Local);
                FolderId = Drive.AddFolder("P");
                Pair = new DriveSyncPair
                {
                    Name = "P", LocalFolder = Local, DriveFolderId = FolderId, DriveFolderPath = "My Drive/P",
                    Mode = SyncMode.TwoWay, CopyDeletes = true, CheckMinutes = 0,
                };
                Client = new DriveClient(Drive, Fast);
                Monitor = NewMonitor();
            }

            private DriveMonitor NewMonitor() =>
                new(new GoogleDrive(), (_, _) => { }, _ => { })
                {
                    WaitScale = Fast,
                    DiskOfRoot = _ => (1L << 42, 1L << 43, "T:"),
                    RecycleLocal = path => { File.Delete(path); return null; },
                    PatienceLimit = TimeSpan.FromSeconds(5),
                    SettleTime = TimeSpan.Zero,
                };

            public void Restart()
            {
                Monitor.Dispose();
                Client = new DriveClient(Drive, Fast);
                Monitor = NewMonitor();
            }

            public async Task<DriveMonitor.PairStatus> Pass()
            {
                await Monitor.SyncOneAsync(Client, Pair, CancellationToken.None);
                return Monitor.StatusOf(Pair.Id);
            }

            public void Write(string relative, string text, DateTime when)
            {
                var path = Path.Combine(Local, relative.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
                File.SetLastWriteTimeUtc(path, when);
            }

            public Dictionary<string, string> LocalTree() =>
                Directory.EnumerateFiles(Local, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(f => Path.GetRelativePath(Local, f).Replace('\\', '/'), File.ReadAllText, StringComparer.OrdinalIgnoreCase);

            public Dictionary<string, string> DriveTree() =>
                Drive.Tree(FolderId).ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetString(kv.Value), StringComparer.OrdinalIgnoreCase);

            public string Difference()
            {
                var pc = LocalTree();
                var drive = DriveTree();
                var onlyPc = pc.Keys.Where(k => !drive.ContainsKey(k)).ToList();
                var onlyDrive = drive.Keys.Where(k => !pc.ContainsKey(k)).ToList();
                var differ = pc.Keys.Where(k => drive.TryGetValue(k, out var d) && d != pc[k]).ToList();
                return onlyPc.Count + onlyDrive.Count + differ.Count == 0 ? ""
                    : $"only on the PC: {string.Join(", ", onlyPc)}; only on Drive: {string.Join(", ", onlyDrive)}; different: {string.Join(", ", differ)}";
            }

            public void Dispose()
            {
                Monitor.Dispose();
                try { Directory.Delete(Path.GetDirectoryName(Local)!, true); } catch { }
            }
        }

        private static async Task<T> InBatches<T>(bool batched, Func<Task<T>> run)
        {
            var (large, batch) = (DriveMonitor.LargePairFiles, DriveMonitor.BatchFiles);
            if (batched) { DriveMonitor.LargePairFiles = 0; DriveMonitor.BatchFiles = 7; }
            try { return await run(); }
            finally { DriveMonitor.LargePairFiles = large; DriveMonitor.BatchFiles = batch; }
        }

        private static async Task PassesUseTheJournal(string sandbox, bool batched)
        {
            if (UsnJournal.Where(sandbox) == null) return;
            var label = batched ? "[batched] " : "";
            await InBatches(batched, async () =>
            {
                using var rig = new Rig(sandbox);
                for (int f = 0; f < 12; f++)
                    for (int i = 0; i < 5; i++)
                        rig.Write($"F{f:D2}/s{i}.txt", $"{f}-{i}", T0);
                await rig.Pass();
                Check(label + "the first pass walks everything", rig.Monitor.LastPcSide.StartsWith("walking it whole"), rig.Monitor.LastPcSide);
                await rig.Pass();
                Check(label + "the next one goes by the journal", rig.Monitor.LastPcSide.EndsWith("folders changed"), rig.Monitor.LastPcSide);
                var settled = await rig.Pass();
                Check(label + "and still counts every file in step", settled.Matched == 60 && settled.LocalFound == 60,
                    $"matched {settled.Matched}, PC {settled.LocalFound}");
                if (batched)
                    Check(label + "visiting only a folder or two, not all of them", rig.Monitor.LastFoldersVisited <= 3,
                        rig.Monitor.LastFoldersVisited.ToString());

                rig.Write("F03/s1.txt", "edited on the PC", T0.AddHours(1));
                int uploads = rig.Drive.Uploads + rig.Drive.Updates;
                await rig.Pass();
                Check(label + "an edit on the PC goes up, found through the journal",
                    rig.DriveTree()["F03/s1.txt"] == "edited on the PC" && rig.Monitor.LastPcSide.EndsWith("folders changed"),
                    rig.Monitor.LastPcSide);

                // Closed and opened again: the journal kept what happened meanwhile.
                rig.Restart();
                rig.Write("New/one.txt", "made while the app was closed", T0.AddHours(2));
                Directory.Move(Path.Combine(rig.Local, "F05"), Path.Combine(rig.Local, "F05 renamed"));
                // (Not one alike the renamed folder's files: a lookalike gone at
                // the same time makes the move ambiguous, and it is copied instead.)
                File.Delete(Path.Combine(rig.Local, "F03", "s1.txt"));
                int renames = rig.Drive.Renames;
                uploads = rig.Drive.Uploads + rig.Drive.Updates;
                await rig.Pass();
                Check(label + "after a restart it still goes by the journal", rig.Monitor.LastPcSide.EndsWith("folders changed"),
                    rig.Monitor.LastPcSide);
                Check(label + "and both sides end the same", rig.Difference() == "", rig.Difference());
                Check(label + "the renamed folder was renamed on Drive, not copied",
                    rig.Drive.Renames > renames && rig.Drive.Uploads + rig.Drive.Updates - uploads == 1,
                    $"renames {rig.Drive.Renames - renames}, uploads {rig.Drive.Uploads + rig.Drive.Updates - uploads}");

                // A day on: the whole folder is walked again, to be sure.
                var full = DriveMonitor.FullListEvery;
                DriveMonitor.FullListEvery = TimeSpan.Zero;
                try { await rig.Pass(); }
                finally { DriveMonitor.FullListEvery = full; }
                Check(label + "once a day the whole folder is walked", rig.Monitor.LastPcSide.StartsWith("walking it whole"),
                    rig.Monitor.LastPcSide);

                // Settings that change what is synced walk it again too.
                rig.Pair.SkipExtensions = ".tmp";
                await rig.Pass();
                Check(label + "a change of settings walks it again", rig.Monitor.LastPcSide.Contains("settings"), rig.Monitor.LastPcSide);
                return 0;
            });
        }

        /// <summary>
        /// Rounds of whatever a person might do on the PC, a pass after each:
        /// by the journal, both sides must end every round the same.
        /// </summary>
        private static async Task RandomRounds(string sandbox, bool batched, int seed)
        {
            if (UsnJournal.Where(sandbox) == null) return;
            var label = batched ? "[batched] " : "";
            await InBatches(batched, async () =>
            {
                using var rig = new Rig(sandbox);
                var random = new Random(seed);
                for (int f = 0; f < 6; f++) rig.Write($"F{f}/x.txt", "start", T0);
                await rig.Pass();
                await rig.Pass();

                string? firstBad = null;
                int partialPasses = 0;
                for (int round = 0; round < 30 && firstBad == null; round++)
                {
                    var when = T0.AddMinutes(round + 1);
                    var folders = Directory.EnumerateDirectories(rig.Local, "*", SearchOption.AllDirectories).ToList();
                    var files = Directory.EnumerateFiles(rig.Local, "*", SearchOption.AllDirectories).ToList();
                    string Pick(List<string> from) => from[random.Next(from.Count)];
                    string Rel(string full) => Path.GetRelativePath(rig.Local, full).Replace('\\', '/');
                    string what;
                    switch (random.Next(7))
                    {
                        case 0 when files.Count > 0:
                            var edit = Pick(files);
                            File.WriteAllText(edit, "edit " + round);
                            File.SetLastWriteTimeUtc(edit, when);
                            what = "edited " + Rel(edit);
                            break;
                        case 1 when files.Count > 0:
                            var gone = Pick(files);
                            File.Delete(gone);
                            what = "deleted " + Rel(gone);
                            break;
                        case 2 when folders.Count > 0:
                            var from = Pick(folders);
                            var to = Path.Combine(Path.GetDirectoryName(from)!, Path.GetFileName(from) + "r" + round);
                            Directory.Move(from, to);
                            what = $"renamed {Rel(from)} to {Rel(to)}";
                            break;
                        case 3 when folders.Count > 1:
                            var moving = Pick(folders);
                            var into = Pick(folders);
                            if (into.StartsWith(moving, StringComparison.OrdinalIgnoreCase)) goto default;
                            var target = Path.Combine(into, Path.GetFileName(moving));
                            if (Directory.Exists(target)) goto default;
                            Directory.Move(moving, target);
                            what = $"moved {Rel(moving)} into {Rel(into)}";
                            break;
                        case 4 when folders.Count > 2:
                            var dropped = Pick(folders);
                            Directory.Delete(dropped, true);
                            what = "deleted folder " + Rel(dropped);
                            break;
                        case 5:
                            var tree = $"T{round}/sub";
                            rig.Write(tree + "/a.txt", "tree " + round, when);
                            rig.Write($"T{round}/b.txt", "tree b " + round, when);
                            what = "made a tree " + tree;
                            break;
                        default:
                            var made = (folders.Count > 0 ? Rel(Pick(folders)) + "/" : "") + $"n{round}.txt";
                            rig.Write(made, "new " + round, when);
                            what = "made " + made;
                            break;
                    }
                    await rig.Pass();
                    if (rig.Monitor.LastPcSide.EndsWith("folders changed")) partialPasses++;
                    var difference = rig.Difference();
                    if (difference != "") firstBad = $"round {round}, after {what}: {difference}";
                }
                Check(label + "thirty rounds of changes on the PC, each synced by the journal, both sides the same every round",
                    firstBad == null, firstBad);
                Check(label + "and they did go by the journal", partialPasses >= 25, partialPasses.ToString());
                return 0;
            });
        }
    }
}
