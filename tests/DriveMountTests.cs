using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Google Drive as a real drive (4.3.0): the WinFsp volume mounted on a
    /// free letter over <see cref="FakeDrive"/>, and used the way other
    /// programs use it — .NET file calls, cmd, robocopy and PowerShell in
    /// processes of their own. Every write is checked on the Drive side, which
    /// is the side that matters: the letter showing the right thing while
    /// Google holds something else is exactly the bug this replaced.
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
            Console.WriteLine("Google Drive as a real drive, against a fake Drive:");

            NameTests();
            OverlayTests();
            if (!WinFsp.Installed)
            {
                Check("WinFsp is installed, to mount the drive", false);
                return Task.CompletedTask;
            }

            var settle = DriveMount.Settle;
            var miss = DriveMount.MissRelistAfter;
            var every = DriveMount.Probe.Every;
            var offline = DriveMount.Probe.WhileOffline;
            DriveMount.Settle = TimeSpan.FromMilliseconds(300);
            DriveMount.MissRelistAfter = TimeSpan.Zero;
            DriveMount.Probe.Every = TimeSpan.FromHours(1);
            DriveMount.Probe.WhileOffline = TimeSpan.FromHours(1);
            try
            {
                Run("reading", ReadingTests);
                Run("writing", WritingTests);
                Run("renaming and moving", MovingTests);
                Run("deleting", DeletingTests);
                Run("saving the way programs save", SavePatternTests);
                Run("places nothing can be written", ReadOnlyTests);
                Run("other processes", OtherProcessTests);
                Run("the file manager's own changes", ApiPathTests);
                Run("many at once", ManyAtOnceTests);
                Run("refusals and retries", FailureTests);
                Run("big files, sent while they are written", StreamingTests);
                Run("cuts, races and limits", HardeningTests);
                Run("quitting with files still to go", QuitTests);
                Run("going offline", OfflineTests);
            }
            finally
            {
                DriveMount.Settle = settle;
                DriveMount.MissRelistAfter = miss;
                DriveMount.Probe.Every = every;
                DriveMount.Probe.WhileOffline = offline;
            }
            return Task.CompletedTask;
        }

        private static void Run(string what, Action<Rig> tests)
        {
            Rig? rig = null;
            try
            {
                rig = new Rig();
                tests(rig);
            }
            catch (Exception ex)
            {
                Check($"{what}: ran without throwing", false, ex.GetType().Name + ": " + ex.Message + " | " + rig?.LogText());
            }
            finally
            {
                rig?.Dispose();
            }
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
            Check("a case change alone keeps the name",
                DriveMount.StillNamed("Song.flac", "song.flac", false));
            Check("a renamed file is not its old name",
                !DriveMount.StillNamed("old.flac", "new.flac", false));
            Check("nor is a name that only looks numbered",
                !DriveMount.StillNamed("song (x).flac", "song.flac", false));
            Check("nor one numbered from zero",
                !DriveMount.StillNamed("song (0).flac", "song.flac", false));
            Check("the volume's name is the account's, cut to a share name",
                GoogleDrive.VolumeName("conner.erwin+x@gmail.com") == @"\\GoogleDrive\conner.erwinx");
        }

        // ---- the rig ---------------------------------------------------------------

        internal sealed class Rig : IDisposable
        {
            private static int _count;
            public readonly FakeDrive Drive = new();
            public readonly DriveClient Client;
            public readonly string Unsaved;
            public readonly List<string> Log = new();
            public readonly List<(string Id, string Message)> Said = new();
            public readonly string Share;
            public DriveMount Mount = null!;
            public string Letter = "";

            public string Music = "", Docs = "";
            public string Track = "", Notes = "";

            public Rig(bool start = true)
            {
                Client = new DriveClient(Drive, 0.0005);
                Share = @"\\GoogleDrive\t" + Environment.ProcessId + "x" + Interlocked.Increment(ref _count);
                Unsaved = Path.Combine(Path.GetTempPath(), "en-unsaved-" + Guid.NewGuid().ToString("N")[..8]);

                Music = Drive.AddFolder("Music");
                Docs = Drive.AddFolder("Docs");
                Track = Drive.AddFile("track.flac", Bytes(5 * 1024 * 1024 + 123, 7), When, Music);
                Notes = Drive.AddFile("notes.txt", Encoding.UTF8.GetBytes("first line\r\n"), When, Docs);
                Drive.AddFile("song.flac", Bytes(1000, 1), When, Music);
                Drive.AddFile("song.flac", Bytes(2000, 2), When.AddMinutes(1), Music);
                Drive.AddFile("AC:DC.flac", Bytes(300, 3), When, Music);
                Drive.AddFile("Plan", Array.Empty<byte>(), When, Docs, "application/vnd.google-apps.document");

                if (start) Start();
            }

            public void Start()
            {
                Mount = new DriveMount(Client, Share, 64L * 1024 * 1024, line => { lock (Log) Log.Add(line); })
                {
                    UnsavedFolder = Unsaved,
                    QuotaUsed = 1000,
                    QuotaLimit = 1L << 40,
                    Notify = (id, message) => { lock (Said) Said.Add((id, message)); },
                };
                // "root", as the app mounts: Drive's alias for My Drive, which is
                // what makes the mount's root the account's, with Shared drives in it.
                char preferred = WinFspTests.FreeLetter()?[0] ?? 'X';
                Mount.Start("root", assignLetter: true, preferred: preferred);
                Letter = Mount.Letter!;
            }

            public string P(params string[] parts) => Path.Combine(new[] { Letter + "\\" }.Concat(parts).ToArray());

            public string LogText() { lock (Log) return string.Join(" | ", Log.TakeLast(14)); }

            public bool SaidLike(string id) { lock (Said) return Said.Any(s => s.Id == id); }

            /// <summary>Waits for every held file to have gone up.</summary>
            public bool Uploaded(int ms = 15000) =>
                Eventually(() => Mount.UploadsPending == 0, ms);

            public void Dispose()
            {
                try { Mount?.Dispose(); } catch { }
                try { Directory.Delete(Unsaved, true); } catch { }
            }
        }

        internal static byte[] Bytes(int count, int seed)
        {
            var data = new byte[count];
            new Random(seed).NextBytes(data);
            return data;
        }

        internal static bool Eventually(Func<bool> condition, int milliseconds = 10000)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                try { if (condition()) return true; } catch { }
                Thread.Sleep(50);
            }
            try { return condition(); } catch { return false; }
        }

        private static bool Same(byte[]? a, byte[]? b) => a != null && b != null && a.AsSpan().SequenceEqual(b);

        private static string Names(string folder) =>
            string.Join(",", Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        // ---- reading ---------------------------------------------------------------

        private static void ReadingTests(Rig rig)
        {
            var root = Names(rig.Letter + "\\");
            Check("the root lists Drive's folders and Shared drives", root.Contains("Docs") && root.Contains("Music") &&
                root.Contains("Shared drives"), root);

            var music = Names(rig.P("Music"));
            Equal("a folder lists every file, a duplicate numbered and a bad name cleaned",
                "AC_DC.flac,song (2).flac,song.flac,track.flac", music);
            Check("a Google document has no file to show", !File.Exists(rig.P("Docs", "Plan")), Names(rig.P("Docs")));

            var expected = rig.Drive.Get(rig.Track)!.Data;
            var read = File.ReadAllBytes(rig.P("Music", "track.flac"));
            Check("a 5 MB file reads back byte for byte across chunk boundaries", Same(read, expected),
                $"{read.Length:N0} of {expected.Length:N0}");

            using (var fs = new FileStream(rig.P("Music", "track.flac"), FileMode.Open, FileAccess.Read, FileShare.Read, 1))
            {
                var piece = new byte[1000];
                fs.Seek(4 * 1024 * 1024 - 300, SeekOrigin.Begin);
                int n = fs.Read(piece, 0, piece.Length);
                Check("a read from the middle, across a chunk edge, is the right bytes",
                    n == 1000 && piece.AsSpan().SequenceEqual(expected.AsSpan(4 * 1024 * 1024 - 300, 1000)));
            }

            var a = File.ReadAllBytes(rig.P("Music", "song.flac"));
            var b = File.ReadAllBytes(rig.P("Music", "song (2).flac"));
            Check("two files Drive gave one name are two files here", a.Length != b.Length && (a.Length + b.Length) == 3000,
                $"{a.Length} and {b.Length}");

            var info = new FileInfo(rig.P("Music", "track.flac"));
            Check("the size is Drive's", info.Length == expected.Length);
            Check("and so is the date", Math.Abs((info.LastWriteTimeUtc - When).TotalSeconds) < 2, info.LastWriteTimeUtc.ToString("o"));

            var drive = new DriveInfo(rig.Letter);
            Check("the drive reports Google's storage, not this disk's", drive.TotalSize == 1L << 40,
                drive.TotalSize.ToString("N0"));
            Check("and it is a network drive", drive.DriveType == DriveType.Network, drive.DriveType.ToString());

            // A file made on the web is found by its path, without a refresh.
            rig.Drive.AddFile("fresh.txt", Encoding.UTF8.GetBytes("made on the web"), DateTime.UtcNow, rig.Docs);
            Check("a file made on the web opens by path straight away",
                File.Exists(rig.P("Docs", "fresh.txt")) && File.ReadAllText(rig.P("Docs", "fresh.txt")) == "made on the web");

            // Contents changed on the web: the next listing lets the old download go.
            rig.Drive.Edit(rig.Notes, Encoding.UTF8.GetBytes("changed on the web\r\n"), DateTime.UtcNow);
            rig.Mount.Forget(rig.P("Docs"));
            rig.Mount.Populate(rig.P("Docs"));
            Check("a file changed on the web reads its new contents after a refresh",
                Eventually(() => File.ReadAllText(rig.P("Docs", "notes.txt")) == "changed on the web\r\n"),
                File.ReadAllText(rig.P("Docs", "notes.txt")));

            Check("nothing was written to Drive by reading", rig.Drive.Uploads == 0 && rig.Drive.Trashes == 0 && rig.Drive.Renames == 0);
        }

        // ---- writing ---------------------------------------------------------------

        private static void WritingTests(Rig rig)
        {
            var data = Bytes(70_000, 11);
            File.WriteAllBytes(rig.P("Docs", "new.bin"), data);
            Check("a new file shows on the letter at once", File.Exists(rig.P("Docs", "new.bin")));
            Check("and reads back before it has gone up", Same(File.ReadAllBytes(rig.P("Docs", "new.bin")), data));
            Check("it goes up to Google", rig.Uploaded(), rig.LogText());
            var landed = rig.Drive.Find(rig.Docs, "new.bin");
            Check("into the right folder, under its name, with the same bytes",
                landed != null && Same(rig.Drive.Get(landed)!.Data, data));
            Check("and nothing is held for it any more", rig.Mount.BytesHeld == 0, rig.Mount.BytesHeld.ToString("N0"));
            Check("it still reads back once it is only on Google", Same(File.ReadAllBytes(rig.P("Docs", "new.bin")), data));

            // Replacing an existing file's contents: the same file on Drive, new contents.
            int updatesBefore = rig.Drive.Updates;
            File.WriteAllText(rig.P("Docs", "notes.txt"), "replaced\r\n");
            Check("replacing a file goes up", rig.Uploaded());
            Check("as the same Drive file (an update in place, keeping its revisions)",
                rig.Drive.Updates == updatesBefore + 1 && Encoding.UTF8.GetString(rig.Drive.Get(rig.Notes)!.Data) == "replaced\r\n",
                $"updates {rig.Drive.Updates - updatesBefore}");

            // An edit in the middle of a file Drive has: the rest of it is kept.
            using (var fs = new FileStream(rig.P("Music", "track.flac"), FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Seek(1_000_000, SeekOrigin.Begin);
                fs.Write(new byte[] { 1, 2, 3, 4 });
            }
            Check("an edit in the middle of a big file goes up", rig.Uploaded(20000));
            var edited = rig.Drive.Get(rig.Track)!.Data;
            var original = Bytes(5 * 1024 * 1024 + 123, 7);
            original[1_000_000] = 1; original[1_000_001] = 2; original[1_000_002] = 3; original[1_000_003] = 4;
            Check("with the rest of the file as it was", Same(edited, original), $"{edited.Length:N0} bytes");

            // Appending.
            File.AppendAllText(rig.P("Docs", "notes.txt"), "second\r\n");
            Check("appending goes up", rig.Uploaded());
            Equal("with what was there kept", "replaced\r\nsecond\r\n", Encoding.UTF8.GetString(rig.Drive.Get(rig.Notes)!.Data));

            // An empty file, and folders.
            File.WriteAllBytes(rig.P("Docs", "empty.txt"), Array.Empty<byte>());
            Directory.CreateDirectory(rig.P("Docs", "Sub", "Deeper"));
            Check("a new folder is on Drive at once", rig.Drive.Find(rig.Docs, "Sub/Deeper") != null);
            File.WriteAllText(rig.P("Docs", "Sub", "Deeper", "inside.txt"), "deep");
            Check("an empty file and one in a new folder go up", rig.Uploaded());
            Check("both where they were put",
                rig.Drive.Find(rig.Docs, "empty.txt") != null &&
                rig.Drive.Find(rig.Docs, "Sub/Deeper/inside.txt") is { } deep &&
                Encoding.UTF8.GetString(rig.Drive.Get(deep)!.Data) == "deep");

            // A date set by itself goes as a date, not as the file again.
            int uploads = rig.Drive.Uploads + rig.Drive.Updates;
            var date = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(rig.P("Music", "AC_DC.flac"), date);
            var acdc = rig.Drive.Find(rig.Music, "AC:DC.flac")!;
            Check("setting a date changes the date on Drive",
                Eventually(() => Math.Abs((rig.Drive.Get(acdc)!.Modified - date).TotalSeconds) < 1),
                rig.Drive.Get(acdc)!.Modified.ToString("o"));
            Check("without sending the file again", rig.Drive.Uploads + rig.Drive.Updates == uploads);

            // A big file copied in: a resumable upload, bytes intact, the date kept.
            var local = Path.Combine(Path.GetTempPath(), "en-big-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
            var big = Bytes(12 * 1024 * 1024 + 7, 21);
            try
            {
                File.WriteAllBytes(local, big);
                File.SetLastWriteTimeUtc(local, date);
                File.Copy(local, rig.P("Music", "big.bin"));
                Check("a 12 MB copy in goes up", rig.Uploaded(30000), rig.LogText());
                var bigId = rig.Drive.Find(rig.Music, "big.bin");
                Check("byte for byte", bigId != null && Same(rig.Drive.Get(bigId)!.Data, big));
                Check("with the date it had here", bigId != null &&
                    Math.Abs((rig.Drive.Get(bigId)!.Modified - date).TotalSeconds) < 2,
                    bigId == null ? "" : rig.Drive.Get(bigId)!.Modified.ToString("o"));
            }
            finally { try { File.Delete(local); } catch { } }
        }

        // ---- renaming and moving ------------------------------------------------------

        private static void MovingTests(Rig rig)
        {
            int creates = rig.Drive.Uploads;
            File.Move(rig.P("Docs", "notes.txt"), rig.P("Docs", "renamed.txt"));
            Check("a rename shows at once", File.Exists(rig.P("Docs", "renamed.txt")) && !File.Exists(rig.P("Docs", "notes.txt")));
            Check("and is the same file on Drive under the new name",
                rig.Drive.Get(rig.Notes)!.Name == "renamed.txt" && !rig.Drive.Get(rig.Notes)!.Trashed);

            File.Move(rig.P("Docs", "renamed.txt"), rig.P("Music", "renamed.txt"));
            Check("a move to another folder is one change of folder on Drive",
                rig.Drive.Get(rig.Notes)!.Parent == rig.Music && rig.Drive.Moves >= 1);
            Check("and it reads in its new place", File.ReadAllText(rig.P("Music", "renamed.txt")) == "first line\r\n");

            File.Move(rig.P("Music", "renamed.txt"), rig.P("Music", "Renamed.txt"));
            Check("a change of case alone is a rename too", rig.Drive.Get(rig.Notes)!.Name == "Renamed.txt",
                rig.Drive.Get(rig.Notes)!.Name);

            Directory.Move(rig.P("Music"), rig.P("Audio"));
            Check("a folder renamed keeps everything in it", File.Exists(rig.P("Audio", "track.flac")) &&
                Same(File.ReadAllBytes(rig.P("Audio", "track.flac")), rig.Drive.Get(rig.Track)!.Data));
            Check("and is the same folder on Drive", rig.Drive.Get(rig.Music)!.Name == "Audio");

            Directory.CreateDirectory(rig.P("Archive"));
            Directory.Move(rig.P("Audio"), rig.P("Archive", "Audio"));
            Check("a folder moved into another lands whole",
                rig.Drive.Get(rig.Music)!.Parent == rig.Drive.Find(FakeDrive.Root, "Archive") &&
                Names(rig.P("Archive", "Audio")).Contains("track.flac"), Names(rig.P("Archive", "Audio")));

            // A file still held here, renamed before it goes up: it goes up under the new name.
            File.WriteAllText(rig.P("Docs", "draft.txt"), "draft");
            File.Move(rig.P("Docs", "draft.txt"), rig.P("Docs", "final.txt"));
            Check("a file renamed before it went up goes up under its new name", rig.Uploaded() &&
                rig.Drive.Find(rig.Docs, "final.txt") != null && rig.Drive.Find(rig.Docs, "draft.txt") == null);

            Check("no move sent any file's contents again", rig.Drive.Uploads == creates + 1,
                $"{rig.Drive.Uploads - creates} uploads");

            // A rename onto a file that is there: refused unless replacing, and then the old one is trashed.
            File.WriteAllText(rig.P("Docs", "a.txt"), "a");
            File.WriteAllText(rig.P("Docs", "b.txt"), "b");
            rig.Uploaded();
            bool refused = false;
            try { File.Move(rig.P("Docs", "a.txt"), rig.P("Docs", "b.txt")); } catch (IOException) { refused = true; }
            Check("moving onto a file that is there is refused", refused);
            var oldB = rig.Drive.Find(rig.Docs, "b.txt")!;
            File.Move(rig.P("Docs", "a.txt"), rig.P("Docs", "b.txt"), overwrite: true);
            // Trashed just behind the rename (5.0.0), which Windows holds every open on the drive for.
            Check("unless asked to replace it, and then the one replaced is in the Drive trash",
                Eventually(() => rig.Drive.Get(oldB)!.Trashed, 10000) && File.ReadAllText(rig.P("Docs", "b.txt")) == "a" &&
                Encoding.UTF8.GetString(rig.Drive.Get(rig.Drive.Find(rig.Docs, "b.txt")!)!.Data) == "a");
        }

        // ---- deleting -------------------------------------------------------------------

        private static void DeletingTests(Rig rig)
        {
            File.Delete(rig.P("Docs", "notes.txt"));
            Check("a delete takes it off the letter", !File.Exists(rig.P("Docs", "notes.txt")));
            var notes = rig.Drive.Get(rig.Notes);
            Check("into the Drive trash, not deleted for good", notes != null && notes.Trashed && rig.Drive.PermanentDeletes == 0);

            bool refused = false;
            try { Directory.Delete(rig.P("Music")); } catch (IOException) { refused = true; }
            Check("a folder with things in it is not removed bare", refused && !rig.Drive.Get(rig.Music)!.Trashed);

            Directory.Delete(rig.P("Music"), recursive: true);
            Check("removed with everything in it, it goes", !Directory.Exists(rig.P("Music")));
            Check("all to the trash on Drive",
                rig.Drive.Get(rig.Music)!.Trashed && rig.Drive.Get(rig.Track)!.Trashed && rig.Drive.PermanentDeletes == 0);

            // A file made and deleted before it went up never reaches Google.
            int uploads = rig.Drive.Uploads;
            File.WriteAllText(rig.P("Docs", "~scratch.tmp"), "temporary");
            File.Delete(rig.P("Docs", "~scratch.tmp"));
            Thread.Sleep(800);
            Check("a scratch file deleted before it went up never reaches Google",
                rig.Drive.Uploads == uploads && rig.Drive.Find(rig.Docs, "~scratch.tmp") == null && rig.Uploaded());

            // Deleted while open with delete-on-close, the Windows way some programs do it.
            File.WriteAllText(rig.P("Docs", "doomed.txt"), "x");
            rig.Uploaded();
            var doomed = rig.Drive.Find(rig.Docs, "doomed.txt")!;
            using (new FileStream(rig.P("Docs", "doomed.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                       1, FileOptions.DeleteOnClose)) { }
            Check("delete-on-close trashes it too", Eventually(() => rig.Drive.Get(doomed)!.Trashed) &&
                !File.Exists(rig.P("Docs", "doomed.txt")));
        }

        // ---- how real programs save ----------------------------------------------------------

        private static void SavePatternTests(Rig rig)
        {
            // Word's way: write a temporary file, rename the original aside,
            // rename the new one into place, delete the old.
            var docs = rig.P("Docs");
            File.WriteAllText(Path.Combine(docs, "~WRD0001.tmp"), "new contents");
            File.Move(Path.Combine(docs, "notes.txt"), Path.Combine(docs, "~WRL0001.tmp"));
            File.Move(Path.Combine(docs, "~WRD0001.tmp"), Path.Combine(docs, "notes.txt"));
            File.Delete(Path.Combine(docs, "~WRL0001.tmp"));
            Check("a save through a temporary file goes up", rig.Uploaded());
            var live = rig.Drive.Tree(rig.Docs);
            Check("leaving one file of that name with the new contents, and no temporary files",
                live.TryGetValue("notes.txt", out var saved) && Encoding.UTF8.GetString(saved) == "new contents" &&
                !live.Keys.Any(k => k.StartsWith("~", StringComparison.Ordinal)),
                string.Join(",", live.Keys));
            Check("the old version is in the Drive trash, not lost", rig.Drive.Get(rig.Notes)!.Trashed);

            // The .NET way: File.Replace.
            File.WriteAllText(Path.Combine(docs, "list.txt"), "one");
            rig.Uploaded();
            File.WriteAllText(Path.Combine(docs, "list.new"), "two");
            File.Replace(Path.Combine(docs, "list.new"), Path.Combine(docs, "list.txt"), null);
            Check("File.Replace goes up", rig.Uploaded());
            Equal("with the replacement's contents", "two",
                Encoding.UTF8.GetString(rig.Drive.Get(rig.Drive.Find(rig.Docs, "list.txt")!)!.Data));
            Check("and no stray file", rig.Drive.Find(rig.Docs, "list.new") == null);

            // Written in many small pieces with the file kept open: sent once, when closed.
            int uploads = rig.Drive.Uploads;
            using (var fs = new FileStream(Path.Combine(docs, "log.txt"), FileMode.Create, FileAccess.Write, FileShare.Read, 1))
                for (int i = 0; i < 200; i++) { fs.Write(Encoding.ASCII.GetBytes($"line {i}\r\n")); fs.Flush(); }
            Check("a file written in two hundred pieces goes up once", rig.Uploaded() && rig.Drive.Uploads == uploads + 1,
                $"{rig.Drive.Uploads - uploads} uploads");
        }

        // ---- places nothing can be written -----------------------------------------------------

        private static void ReadOnlyTests(Rig rig)
        {
            bool refused = false;
            try { File.WriteAllText(rig.P("Shared drives", "x.txt"), "x"); } catch (UnauthorizedAccessException) { refused = true; } catch (IOException) { refused = true; }
            Check("nothing can be put straight into Shared drives", refused);

            refused = false;
            try { Directory.Delete(rig.P("Shared drives")); } catch (UnauthorizedAccessException) { refused = true; } catch (IOException) { refused = true; }
            Check("and Shared drives itself cannot be removed", refused && Directory.Exists(rig.P("Shared drives")));

            refused = false;
            try { Directory.Move(rig.P("Shared drives"), rig.P("Other")); } catch (UnauthorizedAccessException) { refused = true; } catch (IOException) { refused = true; }
            Check("or renamed", refused);

            Check("nothing was sent to Drive for any of it", rig.Drive.Uploads == 0 && rig.Drive.Creates == 0 && rig.Drive.Trashes == 0);
        }

        // ---- other processes ------------------------------------------------------------------

        private static (int Code, string Output) RunProcess(string file, string arguments, int ms = 120000)
        {
            var output = Path.Combine(Path.GetTempPath(), "en-out-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /s /c \"\"{file}\" {arguments} > \"{output}\" 2>&1\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })!;
                if (!process.WaitForExit(ms)) return (-1, "timed out");
                return (process.ExitCode, File.Exists(output) ? File.ReadAllText(output) : "");
            }
            finally { try { File.Delete(output); } catch { } }
        }

        private static void OtherProcessTests(Rig rig)
        {
            var (code, listing) = RunProcess("cmd.exe", $"/d /c dir /b /a \"{rig.P("Music")}\"");
            Check("cmd in another process lists a folder", code == 0 && listing.Contains("track.flac") &&
                listing.Contains("song (2).flac"), listing);

            // robocopy a tree in, from a process of its own.
            var source = Path.Combine(Path.GetTempPath(), "en-rcin-" + Guid.NewGuid().ToString("N")[..8]);
            var back = Path.Combine(Path.GetTempPath(), "en-rcout-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(Path.Combine(source, "inner"));
                for (int i = 0; i < 12; i++) File.WriteAllBytes(Path.Combine(source, $"f{i}.bin"), Bytes(20_000 + i, 100 + i));
                File.WriteAllBytes(Path.Combine(source, "inner", "deep.bin"), Bytes(300_000, 99));

                var (rc, text) = RunProcess("robocopy.exe", $"\"{source}\" \"{rig.P("Docs", "Copied")}\" /E /R:1 /W:1 /NP");
                Check("robocopy copies a folder tree onto the letter", rc is >= 0 and < 8, $"robocopy {rc}: {text[^Math.Min(400, text.Length)..]}");
                Check("and all of it goes up", rig.Uploaded(30000), rig.LogText());
                var tree = rig.Drive.Tree(rig.Drive.Find(rig.Docs, "Copied") ?? "none");
                Check("thirteen files in two folders, byte for byte",
                    tree.Count == 13 && Same(tree.GetValueOrDefault("inner/deep.bin"), Bytes(300_000, 99)) &&
                    Enumerable.Range(0, 12).All(i => Same(tree.GetValueOrDefault($"f{i}.bin"), Bytes(20_000 + i, 100 + i))),
                    string.Join(",", tree.Keys));

                (rc, text) = RunProcess("robocopy.exe", $"\"{rig.P("Docs", "Copied")}\" \"{back}\" /E /R:1 /W:1 /NP");
                Check("robocopy copies it back off the letter", rc is >= 0 and < 8 &&
                    Same(File.ReadAllBytes(Path.Combine(back, "inner", "deep.bin")), Bytes(300_000, 99)),
                    $"robocopy {rc}");
            }
            finally
            {
                try { Directory.Delete(source, true); } catch { }
                try { Directory.Delete(back, true); } catch { }
            }

            // Morgan's move (4.2.10): a big folder moved in PowerShell. On Google,
            // one move, with everything in it; on the letter, all of it there.
            var album = rig.Drive.AddFolder("Big", rig.Music);
            for (int i = 0; i < 300; i++) rig.Drive.AddFile($"t{i:000}.wav", Bytes(100 + i, i), When, album);
            Directory.CreateDirectory(rig.P("Software"));
            var (pc, pout) = RunProcess("powershell.exe",
                $"-NoProfile -Command \"Move-Item -LiteralPath '{rig.P("Music", "Big")}' -Destination '{rig.P("Software", "Big")}'\"");
            Check("PowerShell's Move-Item of a 300-file folder works", pc == 0, pout);
            Check("on Google it is one move of the folder, with all 300 still in it",
                rig.Drive.Get(album)!.Parent == rig.Drive.Find(FakeDrive.Root, "Software") &&
                rig.Drive.Tree(album).Count == 300);
            Check("and the letter shows all 300 in the new place", Directory.GetFiles(rig.P("Software", "Big")).Length == 300 &&
                !Directory.Exists(rig.P("Music", "Big")));
        }

        // ---- the file manager's own changes, through the Drive API --------------------------

        private static void ApiPathTests(Rig rig)
        {
            var entry = new DriveEntry(rig.Drive.AddFile("uploaded.flac", Bytes(500, 5), When, rig.Music),
                "uploaded.flac", "audio/flac", 500, When);
            var name = rig.Mount.PlaceOne(rig.P("Music"), entry);
            Check("a file the file manager uploaded shows on the letter at once",
                name == "uploaded.flac" && File.Exists(rig.P("Music", "uploaded.flac")) &&
                Same(File.ReadAllBytes(rig.P("Music", "uploaded.flac")), Bytes(500, 5)));

            rig.Drive.MoveTo(entry.Id, rig.Docs);
            var moved = rig.Mount.MoveLocal(rig.P("Music", "uploaded.flac"), rig.P("Docs"), "uploaded.flac");
            Check("its move shows on the letter", moved == "uploaded.flac" && File.Exists(rig.P("Docs", "uploaded.flac")) &&
                !File.Exists(rig.P("Music", "uploaded.flac")));

            // Removed from the letter once Drive has trashed it, as the file
            // manager does: left on Drive, any listing after it brought it back.
            rig.Drive.Trash(entry.Id);
            Check("and its removal", rig.Mount.RemoveOne(rig.P("Docs", "uploaded.flac")) && !File.Exists(rig.P("Docs", "uploaded.flac")));
            Check("the file manager's own listing agrees with the letter",
                rig.Mount.TryListing(rig.P("Docs"), out var rows) &&
                string.Join(",", rows.Select(r => r.Name).OrderBy(n => n)) == Names(rig.P("Docs")),
                string.Join(",", rows.Select(r => r.Name)));
        }

        // ---- many at once ----------------------------------------------------------------------

        private static void ManyAtOnceTests(Rig rig)
        {
            Directory.CreateDirectory(rig.P("Docs", "Batch"));
            Parallel.For(0, 120, new ParallelOptions { MaxDegreeOfParallelism = 12 }, i =>
                File.WriteAllBytes(rig.P("Docs", "Batch", $"n{i:000}.txt"), Bytes(1000 + i, 500 + i)));
            Check("a hundred and twenty files written at once all go up", rig.Uploaded(60000), rig.LogText());
            var tree = rig.Drive.Tree(rig.Drive.Find(rig.Docs, "Batch")!);
            Check("each once, each with its own bytes",
                tree.Count == 120 && Enumerable.Range(0, 120).All(i => Same(tree.GetValueOrDefault($"n{i:000}.txt"), Bytes(1000 + i, 500 + i))) &&
                rig.Drive.Duplicates(rig.Docs).Count == 0, $"{tree.Count} files");
            Check("and saying so is one message, not a hundred and twenty",
                Eventually(() => rig.Said.Count(s => s.Id == "drive.saved") is >= 1 and <= 3, 8000),
                string.Join(" | ", rig.Said.Where(s => s.Id == "drive.saved").Select(s => s.Message)));

            // A big folder lists whole and fast.
            var crowd = rig.Drive.AddFolder("Crowd");
            for (int i = 0; i < 2500; i++) rig.Drive.AddFile($"c{i:0000}.mp3", Bytes(10, i), When, crowd);
            var clock = Stopwatch.StartNew();
            int first = Directory.GetFiles(rig.P("Crowd")).Length;
            long firstMs = clock.ElapsedMilliseconds;
            clock.Restart();
            int second = Directory.GetFiles(rig.P("Crowd")).Length;
            Check("a folder of 2,500 lists whole", first == 2500 && second == 2500, $"{first}, {second}");
            Check("and listing it again is served from memory", clock.ElapsedMilliseconds < Math.Max(1500, firstMs),
                $"{firstMs} ms then {clock.ElapsedMilliseconds} ms");
        }

        // ---- refusals and retries ----------------------------------------------------------------

        private static void FailureTests(Rig rig)
        {
            // Google says no for good: the file is set aside where its owner can find it.
            rig.Drive.AddFault(new FakeDrive.Fault
            {
                Kind = "multipart", Times = 6, Status = 403,
                Body = FakeDrive.ErrorBody(403, "insufficientFilePermissions", "The user does not have sufficient permissions for this file."),
            });
            File.WriteAllText(rig.P("Docs", "refused.txt"), "keep me");
            Check("a file Google refuses is set aside, not lost",
                Eventually(() => File.Exists(Path.Combine(rig.Unsaved, "refused.txt"))) &&
                File.ReadAllText(Path.Combine(rig.Unsaved, "refused.txt")) == "keep me", rig.LogText());
            Check("said out loud", Eventually(() => rig.SaidLike("drive.save.failed")));
            Check("and off the letter, since it is not on Google", Eventually(() => !File.Exists(rig.P("Docs", "refused.txt"))));

            // Busy for a moment: tried again until it lands.
            var fresh = new Rig();
            try
            {
                fresh.Drive.AddFault(new FakeDrive.Fault { Kind = "multipart", Times = 2, Status = 503,
                    Body = FakeDrive.ErrorBody(503, "backendError", "Backend Error") });
                File.WriteAllText(fresh.P("Docs", "patient.txt"), "worth the wait");
                Check("a file Google is too busy for goes up when it is not", fresh.Uploaded(40000) &&
                    fresh.Drive.Find(fresh.Docs, "patient.txt") is { } id &&
                    Encoding.UTF8.GetString(fresh.Drive.Get(id)!.Data) == "worth the wait", fresh.LogText());
            }
            finally { fresh.Dispose(); }
        }

        // ---- quitting with files still to go -------------------------------------------------------

        private static void QuitTests(Rig rig)
        {
            // Saved and closed, and Explorer Native quitting before it went up:
            // with nothing kept on disk, it goes up on the way out.
            DriveMount.Settle = TimeSpan.FromMinutes(5);
            FileStream? open = null;
            try
            {
                File.WriteAllText(rig.P("Docs", "pending.txt"), "survived a quit");
                Check("a small saved file waits its settle in memory", rig.Mount.UploadsPending == 1 &&
                    rig.Drive.Find(rig.Docs, "pending.txt") == null);

                // One still open: an unfinished save.
                open = new FileStream(rig.P("Docs", "unfinished.txt"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                open.Write(Encoding.UTF8.GetBytes("half"));
                open.Flush();
                Thread.Sleep(300);
                rig.Mount.Dispose();
            }
            finally
            {
                DriveMount.Settle = TimeSpan.FromMilliseconds(300);
                try { open?.Dispose(); } catch { }
            }

            Check("quitting sends a saved file before the drive goes",
                rig.Drive.Find(rig.Docs, "pending.txt") is { } id &&
                Encoding.UTF8.GetString(rig.Drive.Get(id)!.Data) == "survived a quit", rig.LogText());
            Check("and a file still open leaves nothing half-made on Google",
                rig.Drive.Find(rig.Docs, "unfinished.txt") == null);
            bool named;
            lock (rig.Said) named = rig.Said.Any(s => s.Id == "drive.save.failed" && s.Message.Contains("unfinished.txt"));
            Check("but says by name that it was not saved", named, string.Join(" | ", rig.Said.Select(s => s.Message)));

            rig.Mount = new DriveMount(rig.Client, rig.Share, 64L * 1024 * 1024, line => { lock (rig.Log) rig.Log.Add(line); })
            {
                UnsavedFolder = rig.Unsaved,
                Notify = (id, message) => { lock (rig.Said) rig.Said.Add((id, message)); },
            };
            rig.Mount.Start("root", assignLetter: true, preferred: WinFspTests.FreeLetter()?[0] ?? 'X');
            rig.Letter = rig.Mount.Letter!;
            Check("started again, the saved file is there", File.ReadAllText(rig.P("Docs", "pending.txt")) == "survived a quit");
        }

        // ---- big files, sent while they are written ---------------------------------------

        private static void StreamingTests(Rig rig)
        {
            const int Mb = 1024 * 1024;

            // A 40 MB file written a megabyte at a time: it goes up while it is
            // being written, holding a few megabytes, never the whole file.
            var big = Bytes(40 * Mb + 333, 71);
            long mostHeld = 0;
            int putsWhileOpen;
            using (var fs = new FileStream(rig.P("Docs", "stream.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1))
            {
                for (int at = 0; at < big.Length; at += Mb)
                {
                    fs.Write(big, at, Math.Min(Mb, big.Length - at));
                    fs.Flush();
                    mostHeld = Math.Max(mostHeld, rig.Mount.BytesHeld);
                }
                putsWhileOpen = rig.Drive.PutOffsets.Count;
            }
            Check("a big file goes up while it is still being written", rig.Drive.StreamSessions >= 1 && putsWhileOpen >= 3,
                $"{rig.Drive.StreamSessions} sessions, {putsWhileOpen} pieces before it closed");
            Check("holding only a few megabytes at a time, not the file",
                mostHeld <= DriveMount.MemoryFileBytes + 2L * DriveMount.ChunkBytes, $"{mostHeld:N0} bytes at most");
            Check("and it lands whole", rig.Uploaded(30000) &&
                Same(rig.Drive.Get(rig.Drive.Find(rig.Docs, "stream.bin") ?? "none")?.Data, big), rig.LogText());
            Check("nothing is held once it has gone", rig.Mount.BytesHeld == 0, rig.Mount.BytesHeld.ToString("N0"));

            // A recorder's way: data first, then back to the start for the header.
            var wav = Bytes(20 * Mb, 72);
            var header = Encoding.ASCII.GetBytes("RIFF....WAVEfmt header filled in at the end");
            using (var fs = new FileStream(rig.P("Docs", "take.wav"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1))
            {
                fs.Write(new byte[header.Length]);
                for (int at = header.Length; at < wav.Length; at += Mb)
                    fs.Write(wav, at, Math.Min(Mb, wav.Length - at));
                fs.Seek(0, SeekOrigin.Begin);
                fs.Write(header);
            }
            header.CopyTo(wav, 0);
            Check("a header written back over what had already gone lands in the file", rig.Uploaded(40000) &&
                Same(rig.Drive.Get(rig.Drive.Find(rig.Docs, "take.wav") ?? "none")?.Data, wav), rig.LogText());

            // Reading back what has already gone, while still writing.
            var mixed = Bytes(24 * Mb, 73);
            using (var fs = new FileStream(rig.P("Docs", "readback.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1))
            {
                fs.Write(mixed, 0, 18 * Mb);
                fs.Seek(1000, SeekOrigin.Begin);
                var back = new byte[4096];
                int n = fs.Read(back, 0, back.Length);
                Check("a program reads back what it wrote, though it had gone to Google",
                    n == back.Length && back.AsSpan().SequenceEqual(mixed.AsSpan(1000, 4096)));
                fs.Seek(18 * Mb, SeekOrigin.Begin);
                fs.Write(mixed, 18 * Mb, 6 * Mb);
            }
            Check("and carries on writing, and the whole lands", rig.Uploaded(40000) &&
                Same(rig.Drive.Get(rig.Drive.Find(rig.Docs, "readback.bin") ?? "none")?.Data, mixed), rig.LogText());

            // Pieces written out of order, as a download manager writes them.
            var scattered = Bytes(12 * Mb, 74);
            var order = Enumerable.Range(0, 12).OrderBy(i => (i * 7) % 12).ToArray();
            using (var fs = new FileStream(rig.P("Docs", "scattered.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1))
            {
                fs.SetLength(scattered.Length);
                foreach (var i in order) { fs.Seek(i * Mb, SeekOrigin.Begin); fs.Write(scattered, i * Mb, Mb); }
            }
            Check("a file written in pieces out of order lands in order", rig.Uploaded(40000) &&
                Same(rig.Drive.Get(rig.Drive.Find(rig.Docs, "scattered.bin") ?? "none")?.Data, scattered), rig.LogText());

            // Renamed while it was still being written and going up.
            var named = Bytes(14 * Mb, 75);
            using (var fs = new FileStream(rig.P("Docs", "download.part"), FileMode.CreateNew, FileAccess.Write,
                       FileShare.Read | FileShare.Delete, 1))
            {
                fs.Write(named, 0, 12 * Mb);
                File.Move(rig.P("Docs", "download.part"), rig.P("Docs", "download.mkv"));
                fs.Write(named, 12 * Mb, 2 * Mb);
            }
            Check("a file renamed while it was going up lands under its new name", rig.Uploaded(40000) &&
                rig.Drive.Find(rig.Docs, "download.part") == null &&
                Same(rig.Drive.Get(rig.Drive.Find(rig.Docs, "download.mkv") ?? "none")?.Data, named), rig.LogText());

            // Deleted part way: nothing of it is left on Google.
            int uploads = rig.Drive.Uploads;
            using (var fs = new FileStream(rig.P("Docs", "abandoned.bin"), FileMode.CreateNew, FileAccess.Write,
                       FileShare.Read | FileShare.Delete, 1))
            {
                fs.Write(Bytes(12 * Mb, 76));
                File.Delete(rig.P("Docs", "abandoned.bin"));
            }
            Thread.Sleep(800);
            Check("a file deleted while it was going up leaves nothing on Google", rig.Uploaded() &&
                rig.Drive.Uploads == uploads && rig.Drive.Find(rig.Docs, "abandoned.bin") == null &&
                !File.Exists(rig.P("Docs", "abandoned.bin")));

            // Cut short: the end of a big file Drive has, gone.
            var track = rig.Drive.Get(rig.Track)!.Data;
            using (var fs = new FileStream(rig.P("Music", "track.flac"), FileMode.Open, FileAccess.ReadWrite))
                fs.SetLength(1_000_000);
            Check("a file cut short is cut short on Drive", rig.Uploaded() &&
                Same(rig.Drive.Get(rig.Track)!.Data, track.AsSpan(0, 1_000_000).ToArray()),
                $"{rig.Drive.Get(rig.Track)!.Data.Length:N0} bytes");

            // Three big files at once, with little memory to go round: they
            // wait for Google rather than holding more.
            var keep = DriveMount.HoldMost;
            DriveMount.HoldMost = 24L * Mb;
            try
            {
                var files = Enumerable.Range(0, 3).Select(i => Bytes(30 * Mb, 80 + i)).ToArray();
                long peak = 0;
                var watcher = Task.Run(() =>
                {
                    var clock = Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < 60000 && (rig.Mount.UploadsPending > 0 || clock.ElapsedMilliseconds < 2000))
                    {
                        peak = Math.Max(peak, rig.Mount.BytesHeld);
                        Thread.Sleep(5);
                    }
                });
                Parallel.For(0, 3, i =>
                {
                    using var fs = new FileStream(rig.P("Docs", $"parallel{i}.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1);
                    for (int at = 0; at < files[i].Length; at += Mb) fs.Write(files[i], at, Mb);
                });
                watcher.Wait();
                Check("three big files written at once all land", rig.Uploaded(60000) &&
                    Enumerable.Range(0, 3).All(i => Same(rig.Drive.Get(rig.Drive.Find(rig.Docs, $"parallel{i}.bin") ?? "none")?.Data, files[i])),
                    rig.LogText());
                Check("without holding much more than the limit", peak <= DriveMount.HoldMost + 3L * (DriveMount.ChunkBytes + Mb),
                    $"{peak:N0} bytes at the most");
            }
            finally { DriveMount.HoldMost = keep; }
        }

        private static byte[]? DataOf(Rig rig, string folder, string name) =>
            rig.Drive.Find(folder, name) is { } id ? rig.Drive.Get(id)?.Data : null;

        // ---- what is held in memory -------------------------------------------------------------

        private static void OverlayTests()
        {
            var sparse = new ByteOverlay();
            for (int i = 0; i < 64; i++) sparse.Write(i * 65536L, new byte[] { 1 });
            Check("memory held counts the buffers, not just the bytes",
                sparse.Bytes == 64 && sparse.Allocated >= 64 * 4096, $"{sparse.Bytes} bytes in {sparse.Allocated:N0}");

            var block = Bytes(4096, 5);
            var joined = new ByteOverlay();
            foreach (var k in new[] { 3, 1, 0, 2, 5, 4 }) joined.Write(k * 4096L, block);
            Check("pieces written out of order that come to touch are held as one", joined.RunCount == 1 &&
                joined.Bytes == 6 * 4096, $"{joined.RunCount} runs");
            var all = new byte[6 * 4096];
            joined.CopyTo(0, all);
            Check("with every byte in its place", Enumerable.Range(0, 6).All(k => all.AsSpan(k * 4096, 4096).SequenceEqual(block)));

            joined.Trim(4096 + 100);
            Check("cut short, it gives most of its memory back", joined.Bytes == 4196 && joined.Allocated <= 3 * 4096,
                $"{joined.Allocated:N0}");
            joined.DropBelow(4096);
            Check("and what has gone below a point is let go", joined.Bytes == 100 &&
                joined.Gaps(0, 4196).First() == (0L, 4096L) && joined.Allocated == 4096, $"{joined.Allocated:N0}");

            var late = new ByteOverlay();
            late.Write(4096 + 10, new byte[] { 9, 9 });
            joined.Absorb(late);
            var after = new byte[100];
            joined.CopyTo(4096, after);
            Check("newer bytes laid over older ones win", after[10] == 9 && after[11] == 9 && after[9] == block[9] && after[12] == block[12]);
        }

        // ---- cuts, races and limits (4.3.0 review) ----------------------------------------------

        private static void HardeningTests(Rig rig)
        {
            const int Mb = 1024 * 1024;

            // Cut below what had already gone up: Drive must not keep the old,
            // longer file, nor zeros where bytes were.
            var cutData = Bytes(20 * Mb, 91);
            using (var fs = new FileStream(rig.P("Docs", "cut.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1))
            {
                for (int at = 0; at < cutData.Length; at += Mb) fs.Write(cutData, at, Mb);
                fs.SetLength(Mb);
            }
            Check("a big file cut below what had gone up is cut on Drive", rig.Uploaded(40000) &&
                Same(DataOf(rig, rig.Docs, "cut.bin"), cutData.AsSpan(0, Mb).ToArray()),
                $"{DataOf(rig, rig.Docs, "cut.bin")?.Length:N0} bytes | {rig.LogText()}");

            var middle = Bytes(20 * Mb, 92);
            using (var fs = new FileStream(rig.P("Docs", "cut10.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1))
            {
                for (int at = 0; at < middle.Length; at += Mb) fs.Write(middle, at, Mb);
                fs.SetLength(10 * Mb);
                fs.Seek(0, SeekOrigin.End);
                fs.Write(middle, 0, Mb);
            }
            var middleExpected = middle.AsSpan(0, 10 * Mb).ToArray().Concat(middle.AsSpan(0, Mb).ToArray()).ToArray();
            Check("cut in the middle and written on, it lands as written", rig.Uploaded(40000) &&
                Same(DataOf(rig, rig.Docs, "cut10.bin"), middleExpected),
                $"{DataOf(rig, rig.Docs, "cut10.bin")?.Length:N0} bytes | {rig.LogText()}");

            // Emptied part way and written again, the way a program starts a save over.
            int sessions = rig.Drive.StreamSessions;
            var again = Bytes(40 * Mb, 93);
            using (var fs = new FileStream(rig.P("Docs", "again.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1))
            {
                for (int at = 0; at < 20 * Mb; at += Mb) fs.Write(Bytes(Mb, 900 + at / Mb));
                fs.SetLength(0);
                fs.Seek(0, SeekOrigin.Begin);
                for (int at = 0; at < again.Length; at += Mb) fs.Write(again, at, Mb);
            }
            Check("emptied part way and written again, only the second writing lands", rig.Uploaded(40000) &&
                Same(DataOf(rig, rig.Docs, "again.bin"), again), $"{DataOf(rig, rig.Docs, "again.bin")?.Length:N0} bytes | {rig.LogText()}");
            Check("and the first is dropped, not finished on Drive", rig.Drive.StreamSessions - sessions == 2,
                $"{rig.Drive.StreamSessions - sessions} sessions");

            // Opened again the moment it closes, over and over, some of the
            // times just as it goes up: every append is kept.
            var lines = new StringBuilder();
            for (int i = 0; i < 40; i++)
            {
                var line = $"line {i}\r\n";
                File.AppendAllText(rig.P("Docs", "appended.txt"), line);
                lines.Append(line);
                if (i % 7 == 0) Thread.Sleep(320);
            }
            Check("a file opened again as it goes up keeps every append", rig.Uploaded() &&
                DataOf(rig, rig.Docs, "appended.txt") is { } appended && Encoding.UTF8.GetString(appended) == lines.ToString(),
                Encoding.UTF8.GetString(DataOf(rig, rig.Docs, "appended.txt") ?? Array.Empty<byte>()).Replace("\r\n", "/"));

            // Two handles on one Drive file: one grows it and closes, and the
            // other, opened before that, writes after it went up.
            var tail = Encoding.UTF8.GetBytes("second line\r\n");
            using (var early = new FileStream(rig.P("Docs", "notes.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1))
            {
                using (var later = new FileStream(rig.P("Docs", "notes.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1))
                {
                    later.Seek(0, SeekOrigin.End);
                    later.Write(tail);
                }
                Check("one handle's growth goes up", rig.Uploaded() &&
                    DataOf(rig, rig.Docs, "notes.txt")?.Length == "first line\r\n".Length + tail.Length);
                early.Seek(0, SeekOrigin.Begin);
                early.Write(Encoding.UTF8.GetBytes("FIRST"));
            }
            Check("a handle opened before another grew the file keeps that growth", rig.Uploaded() &&
                Encoding.UTF8.GetString(DataOf(rig, rig.Docs, "notes.txt") ?? Array.Empty<byte>()) == "FIRST line\r\nsecond line\r\n",
                Encoding.UTF8.GetString(DataOf(rig, rig.Docs, "notes.txt") ?? Array.Empty<byte>()).Replace("\r\n", "/"));

            // A document its program keeps open after saving it.
            var idle = DriveMount.IdleCommit;
            DriveMount.IdleCommit = TimeSpan.FromSeconds(1);
            try
            {
                using var doc = new FileStream(rig.P("Docs", "kept open.docx"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1);
                doc.Write(Encoding.UTF8.GetBytes("saved but still open"));
                doc.Flush();
                Check("a file its program keeps open goes up once it is left alone",
                    Eventually(() => DataOf(rig, rig.Docs, "kept open.docx") is { } d &&
                                     Encoding.UTF8.GetString(d) == "saved but still open", 15000), rig.LogText());
                doc.Write(Encoding.UTF8.GetBytes(", then saved again"));
                doc.Flush();
                Check("and again after its next save",
                    Eventually(() => DataOf(rig, rig.Docs, "kept open.docx") is { } d &&
                                     Encoding.UTF8.GetString(d) == "saved but still open, then saved again", 15000), rig.LogText());
            }
            finally { DriveMount.IdleCommit = idle; }
            Check("and closed, nothing is left to send", rig.Uploaded());

            // Four files written out of order at once with little memory to go
            // round: nobody waits for room only it is holding.
            var hold = DriveMount.HoldMost;
            DriveMount.HoldMost = 16L * Mb;
            try
            {
                var files = Enumerable.Range(0, 4).Select(i => Bytes(12 * Mb, 100 + i)).ToArray();
                var clock = Stopwatch.StartNew();
                Parallel.For(0, 4, i =>
                {
                    using var fs = new FileStream(rig.P("Docs", $"shuffled{i}.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1);
                    fs.SetLength(files[i].Length);
                    foreach (var piece in Enumerable.Range(0, 12).OrderBy(k => (k * 5 + i) % 12))
                    {
                        fs.Seek(piece * Mb, SeekOrigin.Begin);
                        fs.Write(files[i], piece * Mb, Mb);
                    }
                });
                var took = clock.Elapsed;
                Check("files written out of order with little memory all finish writing", took < TimeSpan.FromSeconds(90),
                    $"{took.TotalSeconds:N0}s");
                Check("and all land whole", rig.Uploaded(90000) &&
                    Enumerable.Range(0, 4).All(i => Same(DataOf(rig, rig.Docs, $"shuffled{i}.bin"), files[i])), rig.LogText());
            }
            finally { DriveMount.HoldMost = hold; }

            // Google has more of a broken piece than was confirmed, and the
            // program rewrote some of those bytes meanwhile.
            var deadline = DriveMount.PieceDeadline;
            DriveMount.PieceDeadline = TimeSpan.FromSeconds(1);
            var unsure = Bytes(20 * Mb, 140);
            var patch = Bytes(4096, 141);
            int fired = 0;
            Task? patching = null;
            rig.Drive.OnPutBytes = async (got, ct) =>
            {
                if (got < 6 * Mb || Interlocked.Exchange(ref fired, 1) != 0) return;
                // Not awaited: Windows lets one write into a file at a time, and
                // the write sending this piece is still under way. It lands once
                // that write gives up on the piece.
                patching = Task.Run(() =>
                {
                    using var w = new FileStream(rig.P("Docs", "unsure.bin"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1);
                    w.Seek(4 * Mb, SeekOrigin.Begin);
                    w.Write(patch);
                });
                await Task.Delay(10000, ct);
            };
            try
            {
                using (var fs = new FileStream(rig.P("Docs", "unsure.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, 1))
                {
                    for (int at = 0; at < unsure.Length; at += Mb)
                    {
                        fs.Write(unsure, at, Mb);
                        if (at == 14 * Mb) patching?.Wait(30000);
                    }
                    patching?.Wait(30000);
                }
            }
            finally
            {
                rig.Drive.OnPutBytes = null;
                DriveMount.PieceDeadline = deadline;
            }
            patch.CopyTo(unsure, 4 * Mb);
            Check("a piece that broke after Google kept part of it", fired == 1);
            Check("still lands with what the program wrote over it meanwhile", rig.Uploaded(40000) &&
                Same(DataOf(rig, rig.Docs, "unsure.bin"), unsure), rig.LogText());

            // A folder renamed, and a file renamed, while a new file in it goes up.
            Directory.CreateDirectory(rig.P("Docs", "Album"));
            var albumId = rig.Drive.Find(rig.Docs, "Album") ?? "none";
            var side = Bytes(10 * Mb, 130);
            var gate = new ManualResetEventSlim(true);
            int blocked = 0;
            rig.Drive.OnPutBytes = (got, ct) =>
            {
                if (!gate.IsSet) Interlocked.Increment(ref blocked);
                gate.Wait(TimeSpan.FromSeconds(30), ct);
                return Task.CompletedTask;
            };
            try
            {
                using (var fs = new FileStream(rig.P("Docs", "Album", "side.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1))
                {
                    for (int at = 0; at < side.Length; at += Mb) fs.Write(side, at, Mb);
                    gate.Reset();
                }
                Check("its upload is under way when the folder is renamed", Eventually(() => blocked > 0, 10000));
                Directory.Move(rig.P("Docs", "Album"), rig.P("Docs", "Album2"));
                gate.Set();
                Check("a file going up when its folder was renamed is in the renamed folder", rig.Uploaded(30000) &&
                    Same(DataOf(rig, albumId, "side.bin"), side) &&
                    File.Exists(rig.P("Docs", "Album2", "side.bin")) &&
                    new FileInfo(rig.P("Docs", "Album2", "side.bin")).Length == side.Length &&
                    !Directory.Exists(rig.P("Docs", "Album")), rig.LogText());

                blocked = 0;
                var early = Bytes(6 * Mb, 131);
                gate.Reset();
                File.WriteAllBytes(rig.P("Docs", "Album2", "early name.bin"), early);
                Check("a new file's upload is under way when it is renamed", Eventually(() => blocked > 0, 10000));
                File.Move(rig.P("Docs", "Album2", "early name.bin"), rig.P("Docs", "Album2", "late name.bin"));
                gate.Set();
                Check("a new file renamed while it went up has its new name on Drive", rig.Uploaded(30000) &&
                    Eventually(() => rig.Drive.Find(albumId, "early name.bin") == null, 5000) &&
                    Same(DataOf(rig, albumId, "late name.bin"), early) &&
                    File.Exists(rig.P("Docs", "Album2", "late name.bin")), rig.LogText());
            }
            finally
            {
                gate.Set();
                rig.Drive.OnPutBytes = null;
            }

            // Deleted, or saved over, while its upload is on the way: Google has
            // made the file but not yet said so. Nothing stray is left on Drive.
            rig.Drive.AnswerDelayMs = 1500;
            try
            {
                File.WriteAllText(rig.P("Docs", "brief.txt"), "here for a moment");
                Check("a small file's upload is on the way", Eventually(() => rig.Drive.Find(rig.Docs, "brief.txt") != null, 10000));
                File.Delete(rig.P("Docs", "brief.txt"));

                File.WriteAllText(rig.P("Docs", "saved.txt"), "first save");
                Check("and another's", Eventually(() => rig.Drive.Find(rig.Docs, "saved.txt") != null, 10000));
                File.WriteAllText(rig.P("Docs", "saved.txt.tmp"), "second save");
                File.Move(rig.P("Docs", "saved.txt.tmp"), rig.P("Docs", "saved.txt"), overwrite: true);
            }
            finally { rig.Drive.AnswerDelayMs = 0; }
            Check("a new file deleted while its upload was on the way is not left on Drive",
                Eventually(() => rig.Drive.Find(rig.Docs, "brief.txt") == null, 15000) && !File.Exists(rig.P("Docs", "brief.txt")),
                rig.LogText());
            Check("a new file saved over while its upload was on the way is on Drive once, as saved last",
                rig.Uploaded(20000) && Eventually(() => rig.Drive.Count(rig.Docs, "saved.txt") == 1, 15000) &&
                Encoding.UTF8.GetString(DataOf(rig, rig.Docs, "saved.txt") ?? Array.Empty<byte>()) == "second save",
                $"{rig.Drive.Count(rig.Docs, "saved.txt")} copies | {rig.LogText()}");

            // Emptied while the last piece of a big new file is on its way.
            var gateLast = new ManualResetEventSlim(true);
            int lastHeld = 0;
            rig.Drive.OnPutBytes = (got, ct) =>
            {
                if (!gateLast.IsSet) Interlocked.Increment(ref lastHeld);
                gateLast.Wait(TimeSpan.FromSeconds(30), ct);
                return Task.CompletedTask;
            };
            try
            {
                var firstSave = Bytes(6 * Mb, 150);
                var secondSave = Bytes(5 * Mb, 151);
                gateLast.Reset();
                File.WriteAllBytes(rig.P("Docs", "resaved.bin"), firstSave);
                Check("a big file's one piece is on the way", Eventually(() => lastHeld > 0, 10000));
                var cut = Task.Run(() => File.WriteAllBytes(rig.P("Docs", "resaved.bin"), secondSave));
                Thread.Sleep(500);
                gateLast.Set();
                cut.Wait(60000);
                Check("saved again over a big new file whose last piece was on the way, it is on Drive once",
                    rig.Uploaded(30000) && rig.Drive.Count(rig.Docs, "resaved.bin") == 1 &&
                    Same(DataOf(rig, rig.Docs, "resaved.bin"), secondSave),
                    $"{rig.Drive.Count(rig.Docs, "resaved.bin")} copies | {rig.LogText()}");
            }
            finally
            {
                gateLast.Set();
                rig.Drive.OnPutBytes = null;
            }

            // Offline, a write that would hold too much fails at once rather
            // than waiting minutes for room that cannot come.
            var most = DriveMount.FileMost;
            DriveMount.FileMost = 2L * Mb;
            var network = new HttpRequestException("no route to host");
            bool refused = false;
            TimeSpan waited = TimeSpan.Zero;
            try
            {
                using var fs = new FileStream(rig.P("Docs", "offline.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1);
                fs.Write(Bytes(Mb, 120));
                for (int i = 0; i < DriveHealth.FailuresBeforeOffline; i++) rig.Client.Health.Failed(network);
                Eventually(() => !rig.Mount.LetterShown, 5000);
                var clock = Stopwatch.StartNew();
                try { for (int i = 0; i < 8; i++) fs.Write(Bytes(Mb, 121 + i)); }
                catch (IOException) { refused = true; }
                waited = clock.Elapsed;
                rig.Client.Health.Succeeded();
                Eventually(() => rig.Mount.LetterShown, 5000);
            }
            catch (IOException) { }
            finally { DriveMount.FileMost = most; }
            Check("offline, a write that would hold too much is refused at once", refused && waited < TimeSpan.FromSeconds(10),
                $"{waited.TotalSeconds:N1}s, refused {refused}");
            Check("and back online, what it took goes up", rig.Uploaded(30000) && DataOf(rig, rig.Docs, "offline.bin") != null,
                rig.LogText());
        }

        // ---- going offline ---------------------------------------------------------------------------

        private static void OfflineTests(Rig rig)
        {
            Check("the letter is there to start with", Directory.Exists(rig.Letter + "\\") && rig.Mount.LetterShown);
            var network = new HttpRequestException("no route to host");
            for (int i = 0; i < DriveHealth.FailuresBeforeOffline; i++) rig.Client.Health.Failed(network);
            Check("when Google cannot be reached, the letter goes away",
                Eventually(() => !Directory.Exists(rig.Letter + "\\") && !rig.Mount.LetterShown, 5000));
            Check("but it is still Drive's letter to the file manager", rig.Mount.Letter == rig.Letter);

            rig.Client.Health.Succeeded();
            Check("when Google answers again, it comes back",
                Eventually(() => Directory.Exists(rig.Letter + "\\") && rig.Mount.LetterShown, 5000));
            Check("with everything readable", Same(File.ReadAllBytes(rig.P("Docs", "notes.txt")), Encoding.UTF8.GetBytes("first line\r\n")));

            // The probe brings it back by itself when nothing else asks.
            DriveMount.Probe.WhileOffline = TimeSpan.FromMilliseconds(500);
            try
            {
                for (int i = 0; i < DriveHealth.FailuresBeforeOffline; i++) rig.Client.Health.Failed(network);
                Check("gone again", Eventually(() => !rig.Mount.LetterShown, 5000));
                Check("and back by itself once Google answers the probe", Eventually(() => rig.Mount.LetterShown, 10000));
            }
            finally { DriveMount.Probe.WhileOffline = TimeSpan.FromHours(1); }
        }
    }
}
