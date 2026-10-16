using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Second bug sweep, Drive mount half: what a removed folder left behind in
    /// the maps, a failed read starting a whole-file download, a moved file
    /// recorded under its cleaned-up name as Drive's, one folder that would not
    /// list failing a whole search, listing rows lost to a race, and a folder
    /// size index stuck on a changes token Drive had refused.
    /// </summary>
    internal static class Sweep309MountTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        private static readonly DateTime When = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            Console.WriteLine("Drive mount sweep, second pass:");

            RemoveOneForgetsTheWholeSubtree();
            RemoveOneRefusesTheRoot();
            FailedReadDoesNotWarm();
            DriveNameAtIsDrivesName();
            MoveKeepsDrivesName();
            RemovalsAndPlacementsDoNotLoseRows();
            SearchSurvivesOneFolderThatWillNotList();
            await RefusedChangesTokenRebuildsSizes();

            Console.WriteLine();
        }

        private static T Field<T>(DriveMount mount, string name) =>
            (T)typeof(DriveMount).GetField(name, Private)!.GetValue(mount)!;

        private static string? SourceFile(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private static string Scratch(string tag) =>
            Path.Combine(Path.GetTempPath(), $"en-sw309{tag}-" + Guid.NewGuid().ToString("N")[..8]);

        /// <summary>A mount over a plain temporary folder, never registered: only its maps are used.</summary>
        private static DriveMount Unregistered(string root, DriveClient? client = null)
        {
            Directory.CreateDirectory(root);
            return new DriveMount(client ?? new DriveClient(new FakeDrive(), 0.0005), root, 64L * 1024 * 1024, _ => { });
        }

        // ---- RemoveOne ------------------------------------------------------

        private static void RemoveOneForgetsTheWholeSubtree()
        {
            var root = Scratch("r");
            try
            {
                var mount = Unregistered(root);
                Directory.CreateDirectory(Path.Combine(root, "Music", "Sub"));
                Directory.CreateDirectory(Path.Combine(root, "Other"));

                var ids = Field<ConcurrentDictionary<string, string>>(mount, "_ids");
                var folders = Field<ConcurrentDictionary<string, string>>(mount, "_folders");
                var driveIds = Field<ConcurrentDictionary<string, string>>(mount, "_driveIds");
                var listedAt = Field<ConcurrentDictionary<string, long>>(mount, "_listedAt");
                var markedFull = Field<ConcurrentDictionary<string, long>>(mount, "_markedFull");
                var offline = Field<ConcurrentDictionary<string, bool>>(mount, "_answeredOffline");
                var whereIs = Field<ConcurrentDictionary<string, (string Folder, string Name)>>(mount, "_whereIs");
                var listings = Field<ConcurrentDictionary<string, DrivePlaced[]>>(mount, "_listings");

                foreach (var key in new[] { "music", "music\\sub", "other" })
                {
                    ids[key] = "id-" + key;
                    folders[key] = "id-" + key;
                    driveIds[key] = "drive-1";
                    listedAt[key] = Environment.TickCount64;
                    markedFull[key] = Environment.TickCount64;
                    offline[key] = true;
                }
                whereIs["id-music"] = ("", "Music");
                whereIs["id-music\\sub"] = ("music", "Sub");
                whereIs["track"] = ("music\\sub", "t.flac");
                whereIs["id-other"] = ("", "Other");
                listings[""] = new[]
                {
                    new DrivePlaced("Music", true, -1, When, "Music"),
                    new DrivePlaced("Other", true, -1, When, "Other"),
                };

                bool removed = mount.RemoveOne(Path.Combine(root, "Music"));

                bool Gone<T>(ConcurrentDictionary<string, T> map) =>
                    !map.ContainsKey("music") && !map.ContainsKey("music\\sub") && map.ContainsKey("other");

                Check("removing a folder drops its stamps of when it was listed", removed && Gone(listedAt),
                    string.Join(",", listedAt.Keys));
                Check("and its marks for the hand-back timer", Gone(markedFull), string.Join(",", markedFull.Keys));
                Check("and its offline answers", Gone(offline), string.Join(",", offline.Keys));
                Check("and its shared-drive ids", Gone(driveIds), string.Join(",", driveIds.Keys));
                Check("and where everything in it was placed",
                    !whereIs.ContainsKey("id-music") && !whereIs.ContainsKey("id-music\\sub") &&
                    !whereIs.ContainsKey("track") && whereIs.ContainsKey("id-other"),
                    string.Join(",", whereIs.Keys));
                Check("while a sibling folder keeps everything (control)",
                    Gone(ids) && Gone(folders) && listings[""].Length == 1 && listings[""][0].Name == "Other",
                    string.Join(",", listings[""].Select(p => p.Name)));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void RemoveOneRefusesTheRoot()
        {
            var root = Scratch("t");
            try
            {
                var mount = Unregistered(root);
                File.WriteAllBytes(Path.Combine(root, "keep.txt"), new byte[] { 1 });
                bool removed = mount.RemoveOne(root);
                Check("removing the mount's own root is refused, and nothing is deleted",
                    !removed && File.Exists(Path.Combine(root, "keep.txt")), $"removed {removed}");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        // ---- a failed read is not a warm -----------------------------------

        private static void FailedReadDoesNotWarm()
        {
            var root = Scratch("w");
            var identity = Marshal.StringToHGlobalUni("file-1");
            try
            {
                var mount = Unregistered(root);

                // Asked for a range wholly past the end of what the cache holds:
                // failed by name before anything is read.
                var info = new CfApi.CF_CALLBACK_INFO
                {
                    StructSize = (uint)Marshal.SizeOf<CfApi.CF_CALLBACK_INFO>(),
                    FileIdentity = identity,
                    FileIdentityLength = 14,
                    FileSize = 64 * 1024,
                };
                var parameters = new CfApi.CF_CALLBACK_PARAMETERS_FETCH_DATA
                {
                    ParamSize = (uint)Marshal.SizeOf<CfApi.CF_CALLBACK_PARAMETERS_FETCH_DATA>(),
                    RequiredFileOffset = 1024 * 1024,
                    RequiredLength = 4096,
                };
                typeof(DriveMount).GetMethod("OnFetchData", Private)!.Invoke(mount, new object[] { info, parameters });

                var caches = Field<ConcurrentDictionary<string, DriveFileCache>>(mount, "_caches");
                bool opened = caches.TryGetValue("file-1", out var cache);
                Check("a read that fails before reading anything starts no whole-file download",
                    opened && !cache!.Pumping, opened ? "pumping" : "no cache was opened");

                // Control: warming, which is a lease let go unread on purpose, does.
                var path = Path.Combine(root, "warm.flac");
                File.WriteAllBytes(path, new byte[64 * 1024]);
                Field<ConcurrentDictionary<string, string>>(mount, "_ids")["warm.flac"] = "file-2";
                mount.Warm(new[] { path });
                Check("while warming a file still starts its download (control)",
                    caches.TryGetValue("file-2", out var warmed) && warmed.Pumping);
                mount.Dispose();
            }
            finally
            {
                Marshal.FreeHGlobal(identity);
                try { Directory.Delete(root, true); } catch { }
            }
        }

        // ---- Drive's name, not the one on disk -------------------------------

        private static void DriveNameAtIsDrivesName()
        {
            var root = Scratch("n");
            try
            {
                var mount = Unregistered(root);
                Field<ConcurrentDictionary<string, DrivePlaced[]>>(mount, "_listings")[""] = new[]
                {
                    new DrivePlaced("AC_DC.flac", false, 10, When, "AC:DC.flac"),
                    new DrivePlaced("plain.flac", false, 10, When),
                };
                Check("what Drive calls a cleaned-up file is read from its folder's listing",
                    mount.DriveNameAt(Path.Combine(root, "AC_DC.flac")) == "AC:DC.flac",
                    mount.DriveNameAt(Path.Combine(root, "AC_DC.flac")) ?? "(null)");
                Check("and a row with no Drive name, or no row, says nothing",
                    mount.DriveNameAt(Path.Combine(root, "plain.flac")) == null &&
                    mount.DriveNameAt(Path.Combine(root, "nope.flac")) == null &&
                    mount.DriveNameAt(root) == null);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void MoveKeepsDrivesName()
        {
            var source = SourceFile("GoogleDrive.cs") ?? "";
            int start = source.IndexOf("public async Task<bool> MoveOnDrive(", StringComparison.Ordinal);
            int end = start < 0 ? -1 : source.IndexOf("public async Task<int> CopyOnDrive(", start, StringComparison.Ordinal);
            var body = start < 0 || end < 0 ? "" : source[start..end];
            Check("a move with no new name records Drive's own name, not the one on disk",
                body.Contains("mount.DriveNameAt(fromPath)", StringComparison.Ordinal) &&
                !body.Contains("newName ?? Path.GetFileName(fromPath)", StringComparison.Ordinal),
                body.Length == 0 ? "MoveOnDrive not found" : null);
        }

        // ---- rows lost to a race ---------------------------------------------

        private static void RemovalsAndPlacementsDoNotLoseRows()
        {
            var root = Scratch("c");
            try
            {
                var mount = Unregistered(root);
                var listings = Field<ConcurrentDictionary<string, DrivePlaced[]>>(mount, "_listings");
                var ids = Field<ConcurrentDictionary<string, string>>(mount, "_ids");
                const int Each = 300;

                var start = new List<DrivePlaced>();
                for (int i = 0; i < Each; i++)
                {
                    var name = $"old{i}.flac";
                    File.WriteAllBytes(Path.Combine(root, name), new byte[] { 1 });
                    ids[name] = "old-" + i;
                    start.Add(new DrivePlaced(name, false, 1, When, name));
                }
                listings[""] = start.ToArray();

                // Removals on two threads; rows arriving on two more, merged the
                // way an upload's PlaceOne merges its row.
                var work = new List<Task>();
                for (int lane = 0; lane < 2; lane++)
                {
                    int l = lane;
                    work.Add(Task.Run(() =>
                    {
                        for (int i = l; i < Each; i += 2) mount.RemoveOne(Path.Combine(root, $"old{i}.flac"));
                    }));
                    work.Add(Task.Run(() =>
                    {
                        for (int i = l; i < Each; i += 2)
                        {
                            var row = new[] { new DrivePlaced($"new{i}.flac", false, 1, When) };
                            listings.AddOrUpdate("", row, (_, existing) => existing.Concat(row).ToArray());
                        }
                    }));
                }
                Task.WaitAll(work.ToArray(), 60_000);

                var names = listings[""].Select(p => p.Name).ToList();
                int olds = names.Count(n => n.StartsWith("old", StringComparison.Ordinal));
                int news = names.Count(n => n.StartsWith("new", StringComparison.Ordinal));
                Check("removals and new rows in one folder at once lose neither",
                    olds == 0 && news == Each, $"{olds} removed rows left, {news} of {Each} new rows kept");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        // ---- a search with one folder that will not list ---------------------

        private static void SearchSurvivesOneFolderThatWillNotList()
        {
            // root -> A (lists) -> hit; root -> B (will not list) -> hit.
            var placed = new Dictionary<(string Folder, string Id), string>
            {
                [("R", "a")] = "R\\A",
                [("R", "b")] = "R\\B",
            };
            int bTries = 0;
            void Populate(string folder)
            {
                if (folder == "R\\B")
                {
                    Interlocked.Increment(ref bTries);
                    throw new TimeoutException("the listing timed out");
                }
            }
            string? Child(string folder, string id) => placed.TryGetValue((folder, id), out var p) ? p : null;
            var chains = new List<IReadOnlyList<string>> { new[] { "a" }, new[] { "b" } };

            bool rawFailed = false;
            try
            {
                DriveSearch.PopulateAhead("R", chains, Populate, Child,
                    new Dictionary<string, string?>(), 4, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch { rawFailed = true; }
            Check("one folder that will not list fails the raw walk (control)", rawFailed);

            var failed = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tolerant = DriveSearch.NotingFailures(Populate, failed, ex => ex.Message);
            var resolved = new Dictionary<string, string?>();
            bTries = 0;
            Exception? thrown = null;
            string? aPath = null, bPath = null;
            try
            {
                DriveSearch.PopulateAhead("R", chains, tolerant, Child, resolved, 4, CancellationToken.None)
                    .GetAwaiter().GetResult();
                aPath = DriveSearch.PathForChain("R", chains[0], tolerant, Child, resolved);
                bPath = DriveSearch.PathForChain("R", chains[1], tolerant, Child, resolved);
            }
            catch (Exception ex) { thrown = ex; }

            Check("a search keeps the hits in folders that did list", thrown == null && aPath == "R\\A",
                thrown?.Message ?? aPath ?? "(null)");
            Check("and knows which folder would not, and why",
                bPath == "R\\B" && failed.TryGetValue("R\\B", out var why) && why == "the listing timed out",
                string.Join(",", failed.Keys));
            Check("trying that folder once, not once per hit", bTries == 1, $"{bTries} tries");

            var stop = DriveSearch.NotingFailures(_ => throw new OperationCanceledException(), failed, _ => null);
            bool stopped = false;
            try { stop("R\\C"); } catch (OperationCanceledException) { stopped = true; }
            Check("a search that was stopped still stops", stopped);
            var source = SourceFile("GoogleDrive.cs") ?? "";

            int vc = source.IndexOf("public async Task<bool?> VerifyCopied(", StringComparison.Ordinal);
            int vcEnd = vc < 0 ? -1 : source.IndexOf("public async Task<bool> HasDocumentsIn(", vc, StringComparison.Ordinal);
            var verify = vc < 0 || vcEnd < 0 ? "" : source[vc..vcEnd];
            Check("checking a move's copy on the PC reads folders ending in a dot exactly",
                verify.Contains("Directory.Exists(NameRules.ExactPath(copy))", StringComparison.Ordinal) &&
                verify.Contains("new FileInfo(NameRules.ExactPath(arrived))", StringComparison.Ordinal),
                verify.Length == 0 ? "VerifyCopied not found" : null);

            int from = source.IndexOf("internal async Task<SearchResult?> SearchAsync(", StringComparison.Ordinal);
            int to = from < 0 ? -1 : source.IndexOf("private static readonly TimeSpan FolderGraphLife", from, StringComparison.Ordinal);
            var body = from < 0 || to < 0 ? "" : source[from..to];
            Check("the Drive search walks through the tolerant listing, both stages",
                body.Contains("DriveSearch.NotingFailures(", StringComparison.Ordinal) &&
                System.Text.RegularExpressions.Regex.Matches(body, @"\bpopulateOrNote\b").Count >= 3,
                body.Length == 0 ? "SearchAsync not found" : null);
        }

        // ---- the size index and a refused changes token ----------------------

        private static async Task RefusedChangesTokenRebuildsSizes()
        {
            var fake = new FakeDrive();
            var client = new DriveClient(fake, 0.0005);
            var music = fake.AddFolder("Music");
            fake.AddFile("a.flac", new byte[100], When, music);

            var index = new DriveSizeIndex(null);
            await index.Refresh(client, CancellationToken.None);
            var before = index.SizeOf(music);
            Check("the size index is built (control)", before is { Bytes: 100, Files: 1 }, before?.ToString());

            fake.AddFile("b.flac", new byte[50], When, music);
            fake.AddFault(new FakeDrive.Fault
            {
                Kind = "changes", Status = 410, Times = 1,
                Body = FakeDrive.ErrorBody(410, "invalid", "Invalid page token"),
            });
            index.Expire();
            Exception? thrown = null;
            try { await index.Refresh(client, CancellationToken.None); }
            catch (Exception ex) { thrown = ex; }

            var after = index.SizeOf(music);
            Check("a changes token Drive refuses is replaced by listing the drive again",
                thrown == null && after is { Bytes: 150, Files: 2 },
                (thrown?.Message ?? "") + " " + after);
        }
    }
}
