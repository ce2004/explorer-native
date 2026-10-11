using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The Cloud Files provider itself, stood up over a scratch sync root with
    /// <see cref="FakeDrive"/> behind it: no drive letter, no account, nothing
    /// leaves the process. Folders are enumerated from another process, which
    /// is what raises FETCH_PLACEHOLDERS, with a time limit, so a provider that
    /// fails to answer is a FAIL, not a hang.
    /// </summary>
    internal static class DriveMountTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        private static readonly DateTime When = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        public static Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;
            Console.WriteLine("Google Drive provider, against a fake Drive:");

            NameTests();
            MountTests();
            return Task.CompletedTask;
        }

        private static void NameTests()
        {
            Check("a name on disk that is Drive's name is still its name",
                DriveMount.StillNamed("song.flac", "song.flac", false));
            Check("so is the numbered one a duplicate got",
                DriveMount.StillNamed("song (2).flac", "song.flac", false));
            Check("and the cleaned-up one",
                DriveMount.StillNamed("AC_DC.flac", "AC:DC.flac", false));
            Check("and a folder numbered whole",
                DriveMount.StillNamed("Album.2024 (3)", "Album.2024", true));
            Check("a case change alone keeps the placeholder",
                DriveMount.StillNamed("Song.flac", "song.flac", false));
            Check("a renamed file is not its old name",
                !DriveMount.StillNamed("old.flac", "new.flac", false));
            Check("nor is a name that only looks numbered",
                !DriveMount.StillNamed("song (x).flac", "song.flac", false));
            Check("nor one numbered from zero",
                !DriveMount.StillNamed("song (0).flac", "song.flac", false));
        }

        private sealed class Rig : IDisposable
        {
            public readonly FakeDrive Drive = new();
            public readonly DriveClient Client;
            public readonly string Root;
            public readonly List<string> Log = new();
            public readonly ConcurrentDictionary<string, int> Lists = new();
            public DriveMount Mount = null!;

            public Rig()
            {
                Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "en-mount-" + Guid.NewGuid().ToString("N")[..8]);
                Client = new DriveClient(Drive, 0.0005);
                Drive.OnList = (folder, _) =>
                {
                    Lists.AddOrUpdate(folder, 1, (_, n) => n + 1);
                    return Task.CompletedTask;
                };
            }

            public void Start()
            {
                Mount = new DriveMount(Client, Root, 64L * 1024 * 1024, line => { lock (Log) Log.Add(line); });
                Mount.Start(FakeDrive.Root, assignLetter: false);
            }

            public int ListsOf(string id) => Lists.TryGetValue(id, out var n) ? n : 0;

            public string Path(params string[] parts) => System.IO.Path.Combine(new[] { Root }.Concat(parts).ToArray());

            public string LogText() { lock (Log) return string.Join(" | ", Log.TakeLast(12)); }

            public void Dispose()
            {
                try { Mount?.Dispose(); } catch { }
                try { Directory.Delete(Root, true); } catch { }
            }
        }

        /// <summary>
        /// What a directory holds as another process sees it — the way File
        /// Explorer would — through cmd's dir into a file. Null when it took
        /// over 40s; never killed, since a process ended mid-callback can be
        /// left unable to exit.
        /// </summary>
        private static string[]? EnumerateElsewhere(string directory)
        {
            var output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "en-dir-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /c dir /b /a \"{directory}\" > \"{output}\" 2>&1",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })!;
                if (!process.WaitForExit(40000)) return null;
                return File.Exists(output)
                    ? File.ReadAllLines(output).Where(l => l.Length > 0).ToArray()
                    : Array.Empty<string>();
            }
            finally
            {
                try { File.Delete(output); } catch { }
            }
        }

        private static bool Eventually(Func<bool> condition, int milliseconds = 5000)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                if (condition()) return true;
                Thread.Sleep(50);
            }
            return condition();
        }

        private static string Names(IReadOnlyList<DrivePlaced> placed) =>
            string.Join(",", placed.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        private static void MountTests()
        {
            using var rig = new Rig();
            var drive = rig.Drive;

            var music = drive.AddFolder("Music");
            var a = drive.AddFile("a.flac", new byte[] { 1, 2, 3 }, When, music);
            var b = drive.AddFile("b.flac", new byte[] { 4, 5 }, When, music);
            var song1 = drive.AddFile("song.flac", new byte[] { 6 }, When, music);
            var song2 = drive.AddFile("song.flac", new byte[] { 7, 7 }, When.AddMinutes(1), music);

            var docs = drive.AddFolder("Docs");
            var sub = drive.AddFolder("Sub", docs);
            drive.AddFile("inner.txt", new byte[] { 8 }, When, sub);
            var other = drive.AddFolder("Other", docs);
            drive.AddFile("unseen.txt", new byte[] { 9 }, When, other);

            var album = drive.AddFolder("Album");
            var track = drive.AddFile("01 track.flac", new byte[] { 1 }, When, album);
            var big = new byte[9 * 1024 * 1024 + 123];
            new Random(7).NextBytes(big);
            var bigId = drive.AddFile("big.bin", big, When, album);

            var slow = drive.AddFolder("Slow");
            drive.AddFile("s1.txt", new byte[] { 1 }, When, slow);

            var blip = drive.AddFolder("Blip");
            drive.AddFile("b1.txt", new byte[] { 1 }, When, blip);
            var fresh = drive.AddFolder("Fresh");
            drive.AddFile("f1.txt", new byte[] { 1 }, When, fresh);
            var stale = drive.AddFolder("Stale");
            drive.AddFile("t1.txt", new byte[] { 1 }, When, stale);
            var busy = drive.AddFolder("Busy");
            drive.AddFile("u1.txt", new byte[] { 1 }, When, busy);

            try { rig.Start(); }
            catch (Exception ex)
            {
                Check("a provider stands up over a scratch folder with a fake Drive behind it", false, ex.Message);
                return;
            }

            var musicPath = rig.Path("Music");

            // ---- 5: a folder the application listed is not listed again ----
            rig.Mount.Populate(musicPath);
            Check("populating lists the folder once", rig.ListsOf(music) == 1, $"{rig.ListsOf(music)} listings");
            var seen = EnumerateElsewhere(musicPath);
            Check("enumerating a populated folder from outside answers",
                seen != null && seen.Length == 4, seen == null ? "timed out" : string.Join(",", seen));
            Check("and pays Drive for no second listing", rig.ListsOf(music) == 1,
                $"{rig.ListsOf(music)} listings; {rig.LogText()}");

            // ---- 6: the id to path lookup ----
            Equal("a search hit's path comes from its id", rig.Path("Music", "b.flac"),
                rig.Mount.ChildPathForId(musicPath, b) ?? "(null)");
            var second = rig.Mount.ChildPathForId(musicPath, song2) ?? "(null)";
            var first = rig.Mount.ChildPathForId(musicPath, song1) ?? "(null)";
            Check("and two files sharing a name come back as the two names on disk",
                new[] { first, second }.Select(System.IO.Path.GetFileName).OrderBy(n => n).SequenceEqual(new[] { "song (2).flac", "song.flac" }),
                first + " / " + second);
            Check("an id that is not in the folder is not found there",
                rig.Mount.ChildPathForId(musicPath, "no-such-id") == null);

            // ---- 3: a file renamed on the web follows on refresh ----
            drive.Get(a)!.Name = "renamed.flac";
            rig.Mount.Forget(musicPath);
            rig.Mount.Populate(musicPath);
            rig.Mount.TryListing(musicPath, out var afterRename);
            Equal("a file renamed on the web is listed under its new name",
                "b.flac,renamed.flac,song (2).flac,song.flac", Names(afterRename));
            Equal("and its id answers for the new name", a, rig.Mount.IdFor(rig.Path("Music", "renamed.flac")) ?? "(null)");
            Check("and nothing answers for the old one", rig.Mount.IdFor(rig.Path("Music", "a.flac")) == null);
            Check("the old placeholder goes from disk", Eventually(() => !File.Exists(rig.Path("Music", "a.flac"))));
            Check("and the new one is there", File.Exists(rig.Path("Music", "renamed.flac")));
            Check("a numbered duplicate keeps its number across a refresh",
                rig.Mount.IdFor(rig.Path("Music", "song (2).flac")) is { } numbered && (numbered == song1 || numbered == song2));

            // ---- 4: a file deleted on the web leaves the disk too ----
            drive.Trash(b);
            rig.Mount.Forget(musicPath);
            rig.Mount.Populate(musicPath);
            rig.Mount.TryListing(musicPath, out var afterTrash);
            Check("a file trashed on the web leaves the listing", !afterTrash.Any(p => p.Name == "b.flac"), Names(afterTrash));
            Check("and its placeholder leaves the disk", Eventually(() => !File.Exists(rig.Path("Music", "b.flac"))),
                rig.LogText());

            var docsPath = rig.Path("Docs");
            rig.Mount.Populate(docsPath);
            rig.Mount.Populate(rig.Path("Docs", "Sub"));
            drive.Trash(sub);
            drive.Trash(other);
            rig.Mount.Forget(docsPath);
            rig.Mount.Populate(docsPath);
            Check("a populated folder trashed on the web leaves the disk, contents and all",
                Eventually(() => !Directory.Exists(rig.Path("Docs", "Sub"))), rig.LogText());
            Check("and so does one nobody opened", Eventually(() => !Directory.Exists(rig.Path("Docs", "Other"))),
                rig.LogText());
            Check("without listing the folder that went", rig.ListsOf(other) == 0, $"{rig.ListsOf(other)} listings");

            // ---- 2: offline, a folder listed before is answered from what is held ----
            for (int i = 0; i < DriveHealth.FailuresBeforeOffline; i++)
                rig.Client.Health.Failed(new HttpRequestException("fake: no network"));
            Check("the fake can take Drive offline", rig.Client.Health.IsOffline);
            int before = rig.ListsOf(music);
            rig.Mount.Forget(musicPath);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var shown = new List<DrivePlaced>();
            rig.Mount.Populate(musicPath, page => shown.AddRange(page));
            Check("offline, a folder listed before opens at once", clock.ElapsedMilliseconds < 2000, $"{clock.ElapsedMilliseconds}ms");
            Check("without asking Drive", rig.ListsOf(music) == before, $"{rig.ListsOf(music) - before} listings");
            Check("and shows what it held", rig.Mount.TryListing(musicPath, out var offline) && offline.Count == 3 && shown.Count == 3,
                Names(offline) + " / " + shown.Count);
            rig.Client.Health.Succeeded();
            Check("back online, a folder answered offline is listed again rather than trusted",
                !rig.Mount.TryListing(musicPath, out _));
            rig.Mount.Populate(musicPath);
            Check("and the next visit asks Drive", rig.ListsOf(music) == before + 1, $"{rig.ListsOf(music) - before} listings");

            // ---- 10: a patient listing waits out a blip; an interactive one does not ----
            var blipPath = rig.Path("Blip");
            drive.AddFault(new FakeDrive.Fault { Kind = "list", Times = 2, Status = 503, Subject = s => s == blip });
            try { rig.Mount.Populate(blipPath); } catch { }
            Check("an interactive listing gives up on a blip", !rig.Mount.TryListing(blipPath, out _));
            drive.AddFault(new FakeDrive.Fault { Kind = "list", Times = 2, Status = 503, Subject = s => s == blip });
            try { rig.Mount.Populate(blipPath, interactive: false); }
            catch (Exception ex) { Check("a patient listing does not throw on a blip", false, ex.Message); }
            Check("a patient listing waits it out and lists the folder",
                rig.Mount.TryListing(blipPath, out var blipListed) && blipListed.Any(p => p.Name == "b1.txt"),
                rig.LogText());

            // ---- 9: a row added while a listing runs survives it ----
            var slowPath = rig.Path("Slow");
            var gate = new ManualResetEventSlim(false);
            var entered = new ManualResetEventSlim(false);
            drive.OnList = async (folder, ct) =>
            {
                rig.Lists.AddOrUpdate(folder, 1, (_, n) => n + 1);
                if (folder == slow) { entered.Set(); await Task.Run(() => gate.Wait(10000)); }
            };
            var populating = Task.Run(() => rig.Mount.Populate(slowPath));
            Check("the slow listing starts", entered.Wait(10000));

            // 5 again: Explorer opening the folder while the application is listing it.
            var outside = Task.Run(() => EnumerateElsewhere(slowPath));
            Thread.Sleep(300);
            var placed = rig.Mount.PlaceOne(slowPath, new DriveEntry("upload-1", "upload.txt", "text/plain", 3, When));
            Check("an upload is placed while the listing runs", placed == "upload.txt", placed ?? "(null)");
            gate.Set();
            Check("the listing finishes", populating.Wait(20000));
            rig.Mount.TryListing(slowPath, out var slowListed);
            Check("and the upload is still in the folder's listing", slowListed.Any(p => p.Name == "upload.txt"), Names(slowListed));
            Check("beside what the listing found", slowListed.Any(p => p.Name == "s1.txt"), Names(slowListed));
            var outsideSaw = outside.Wait(30000) ? outside.Result : null;
            Check("Explorer opening a folder mid-listing is answered", outsideSaw != null && outsideSaw.Contains("s1.txt"),
                (outsideSaw == null ? "timed out" : string.Join(",", outsideSaw)) + "; " + rig.LogText());
            Check("by that listing, not a second one", rig.ListsOf(slow) == 1, $"{rig.ListsOf(slow)} listings; {rig.LogText()}");
            lock (rig.Log)
                Check("every folder populated was marked so with Windows",
                    !rig.Log.Any(l => l.Contains("could not mark", StringComparison.Ordinal)),
                    string.Join(" | ", rig.Log.Where(l => l.Contains("could not mark", StringComparison.Ordinal))));
            drive.OnList = (folder, _) => { rig.Lists.AddOrUpdate(folder, 1, (_, n) => n + 1); return Task.CompletedTask; };

            // ---- 8: a read across several chunks arrives whole ----
            var albumPath = rig.Path("Album");
            rig.Mount.Populate(albumPath);
            var read = Task.Run(() =>
            {
                using var stream = new FileStream(rig.Path("Album", "big.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
                var got = new byte[big.Length];
                int total = 0;
                while (total < got.Length)
                {
                    int n = stream.Read(got, total, got.Length - total);
                    if (n <= 0) break;
                    total += n;
                }
                return (total, got);
            });
            if (read.Wait(TimeSpan.FromSeconds(60)))
            {
                var (total, got) = read.Result;
                Check("a placeholder read across chunks arrives whole", total == big.Length, $"{total:N0} of {big.Length:N0}");
                Check("with the right bytes", got.AsSpan().SequenceEqual(big));
            }
            else Check("a placeholder read across chunks arrives", false, "over 60s");

            // ---- 7: a folder renamed in place keeps its tree ----
            int albumLists = rig.ListsOf(album);
            var moved = rig.Mount.MoveLocal(albumPath, docsPath, "Album Renamed");
            Equal("a folder moves in place under its new name", "Album Renamed", moved ?? "(null)");
            var movedPath = rig.Path("Docs", "Album Renamed");
            Check("it is there on disk", Directory.Exists(movedPath));
            Check("and gone from where it was", !Directory.Exists(albumPath));
            Equal("its files keep their ids", track, rig.Mount.IdFor(System.IO.Path.Combine(movedPath, "01 track.flac")) ?? "(null)");
            Check("its listing came with it", rig.Mount.TryListing(movedPath, out var movedListing) && movedListing.Count == 2,
                Names(movedListing));
            Check("the folder it left no longer lists it",
                rig.Mount.TryListing(rig.Root, out var top) && !top.Any(p => p.Name == "Album"), Names(top));
            Check("the folder it joined does",
                rig.Mount.TryListing(docsPath, out var joined) && joined.Any(p => p.Name == "Album Renamed"), Names(joined));
            var movedSeen = EnumerateElsewhere(movedPath);
            Check("and it enumerates without asking Drive again",
                movedSeen != null && movedSeen.Length == 2 && rig.ListsOf(album) == albumLists,
                (movedSeen == null ? "timed out" : string.Join(",", movedSeen)) + $"; {rig.ListsOf(album) - albumLists} listings");

            var fileMoved = rig.Mount.MoveLocal(rig.Path("Music", "renamed.flac"), musicPath, "Renamed.FLAC");
            Equal("a rename that only changes case is not numbered against itself", "Renamed.FLAC", fileMoved ?? "(null)");
            Equal("and the id follows it", a, rig.Mount.IdFor(rig.Path("Music", "Renamed.FLAC")) ?? "(null)");
            Check("nothing is moved into itself", rig.Mount.MoveLocal(movedPath, movedPath, "x") == null);

            // ---- 11: File Explorer sees web changes once a listing is old or forgotten ----
            var freshPath = rig.Path("Fresh");
            rig.Mount.Populate(freshPath);
            drive.AddFile("f2.txt", new byte[] { 2 }, When, fresh);
            int freshLists = rig.ListsOf(fresh);
            var freshSeen = EnumerateElsewhere(freshPath);
            Check("a folder just listed is answered from disk", freshSeen != null && rig.ListsOf(fresh) == freshLists,
                $"{rig.ListsOf(fresh) - freshLists} listings");
            rig.Mount.Forget(freshPath);
            string[]? afterForget = null;
            Check("after a refresh, Explorer opening it asks Drive and sees what was added on the web",
                Eventually(() => (afterForget = EnumerateElsewhere(freshPath)) is { } s && s.Contains("f2.txt"), 10000),
                (afterForget == null ? "timed out" : string.Join(",", afterForget)) + "; " + rig.LogText());

            var stalePath = rig.Path("Stale");
            var keepFresh = DriveMount.FreshFor;
            try
            {
                DriveMount.FreshFor = TimeSpan.FromMilliseconds(200);
                rig.Mount.Populate(stalePath);
                drive.AddFile("t2.txt", new byte[] { 2 }, When, stale);
                Thread.Sleep(400);
                Check("an old fully-populated mark is handed back to Windows", rig.Mount.ReopenStale() >= 1, rig.LogText());
                var staleSeen = EnumerateElsewhere(stalePath);
                Check("so Explorer opening it later lists it again and sees web changes",
                    staleSeen != null && staleSeen.Contains("t2.txt"),
                    (staleSeen == null ? "timed out" : string.Join(",", staleSeen)) + "; " + rig.LogText());
            }
            finally { DriveMount.FreshFor = keepFresh; }

            // ---- 12: a callback listing holds off a move in place ----
            var busyPath = rig.Path("Busy");
            var busyGate = new ManualResetEventSlim(false);
            var busyEntered = new ManualResetEventSlim(false);
            drive.OnList = async (folder, ct) =>
            {
                rig.Lists.AddOrUpdate(folder, 1, (_, n) => n + 1);
                if (folder == busy) { busyEntered.Set(); await Task.Run(() => busyGate.Wait(10000)); }
            };
            var busyOutside = Task.Run(() => EnumerateElsewhere(busyPath));
            Check("Explorer's listing of a folder starts", busyEntered.Wait(15000), rig.LogText());
            Check("a move in place is refused while that listing runs",
                rig.Mount.MoveLocal(busyPath, docsPath, "Busy Moved") == null);
            busyGate.Set();
            busyOutside.Wait(30000);
            drive.OnList = (folder, _) => { rig.Lists.AddOrUpdate(folder, 1, (_, n) => n + 1); return Task.CompletedTask; };
            Check("and the folder is still where it was", Directory.Exists(busyPath));

            // ---- 1 (shape): a refused transfer still completes the request ----
            var source = Source("DriveMount.cs") ?? "";
            int refused = source.IndexOf("TRANSFER_PLACEHOLDERS 0x{hr:X8}", StringComparison.Ordinal);
            int nextElse = refused < 0 ? -1 : source.IndexOf("else", refused, StringComparison.Ordinal);
            Check("a listing Windows refuses is failed, not left for Explorer to wait out",
                refused > 0 && nextElse > 0 &&
                source.IndexOf("FailPlaceholders(ref opInfo)", refused, nextElse - refused, StringComparison.Ordinal) > 0);
        }

        private static string? Source(string name)
        {
            var here = new DirectoryInfo(AppContext.BaseDirectory);
            for (int up = 0; up < 8 && here != null; up++, here = here.Parent)
            {
                var candidate = System.IO.Path.Combine(here.FullName, "src", name);
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
            }
            return null;
        }
    }
}
