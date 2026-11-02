using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rig = ExplorerNative.DriveMountTests.Rig;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of the Google Drive volume.
    /// </summary>
    internal static class Sweep500MountTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        private static readonly DateTime When = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the Google Drive volume:");
            if (!WinFsp.Installed)
            {
                Check("WinFsp is installed, to mount the drive", false);
                return;
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
                Run("a folder that only looks empty", FolderHoldingOnlyDocsIsNotRemoved, drive =>
                {
                    var only = drive.AddFolder("OnlyDocs");
                    drive.AddFile("Budget", Array.Empty<byte>(), When, only, "application/vnd.google-apps.spreadsheet");
                    drive.AddFolder("Later");
                    drive.AddFolder("Empty");
                });
                Run("one slow change does not hold the whole drive", SlowChangesHoldOnlyTheirOwnName, drive =>
                {
                    var docs = drive.Find(FakeDrive.Root, "Docs")!;
                    drive.AddFile("old.txt", Encoding.UTF8.GetBytes("old"), When, docs);
                    drive.AddFile("new.txt", Encoding.UTF8.GetBytes("new"), When, docs);
                    drive.AddFolder("Race");
                    drive.AddFolder("Doomed");
                });
            }
            finally
            {
                DriveMount.Settle = settle;
                DriveMount.MissRelistAfter = miss;
                DriveMount.Probe.Every = every;
                DriveMount.Probe.WhileOffline = offline;
            }
            await Task.CompletedTask;
        }

        /// <summary>
        /// One test on a fresh mount. <paramref name="setup"/> adds to the fake
        /// Drive before the mount starts, so the first listing already has it.
        /// </summary>
        private static void Run(string what, Action<Rig> tests, Action<FakeDrive>? setup = null)
        {
            Rig? rig = null;
            try
            {
                rig = new Rig(start: false);
                setup?.Invoke(rig.Drive);
                rig.Start();
                tests(rig);
            }
            catch (Exception ex)
            {
                Check($"{what}: ran without throwing", false, ex.GetType().Name + ": " + ex.Message + " | " + rig?.LogText());
            }
            finally
            {
                // Bounded: a mount stuck in a callback must not hold the rest of the suite.
                var r = rig;
                if (r != null && !Task.Run(r.Dispose).Wait(TimeSpan.FromSeconds(60)))
                    Check($"{what}: the test mount came down within a minute", false);
            }
        }

        /// <summary>Whether a task ended (well or not) within the time, without throwing its error.</summary>
        private static bool Ended(this Task task, int milliseconds)
        {
            try { task.Wait(milliseconds); } catch (AggregateException) { }
            return task.IsCompleted;
        }

        /// <summary>Runs <paramref name="action"/> on a worker; false when it did not finish in time.</summary>
        private static bool Within(int milliseconds, Action action, out Exception? error)
        {
            Exception? caught = null;
            var task = Task.Run(() =>
            {
                try { action(); }
                catch (Exception ex) { caught = ex; }
            });
            bool done = task.Wait(milliseconds);
            error = caught;
            return done;
        }

        // ---- 1. a folder holding only Google Docs, or files the letter has not seen ----

        private static void FolderHoldingOnlyDocsIsNotRemoved(Rig rig)
        {
            var only = rig.Drive.Find(FakeDrive.Root, "OnlyDocs")!;
            var later = rig.Drive.Find(FakeDrive.Root, "Later")!;
            var empty = rig.Drive.Find(FakeDrive.Root, "Empty")!;

            // Only a spreadsheet inside: nothing on the letter shows it.
            Equal("a folder holding only a Google Sheet looks empty on the letter", "0",
                Directory.GetFileSystemEntries(rig.P("OnlyDocs")).Length.ToString());
            bool finished = Within(30000, () => Directory.Delete(rig.P("OnlyDocs")), out var error);
            Check("removing a folder that holds only Google Docs is refused as not empty",
                finished && error is IOException, error?.Message);
            Check("and the folder, with the document in it, is not sent to the Drive trash",
                !rig.Drive.Get(only)!.Trashed, rig.LogText());
            Check("it is still on the letter", Directory.Exists(rig.P("OnlyDocs")));

            // Listed empty through the letter, then given a file on the web.
            Equal("a folder listed while empty", "0",
                Directory.GetFileSystemEntries(rig.P("Later")).Length.ToString());
            rig.Drive.AddFile("new.txt", DriveMountTests.Bytes(10, 1), When, later);
            finished = Within(30000, () => Directory.Delete(rig.P("Later")), out error);
            Check("removing a folder whose listing is older than a file added on the web is refused",
                finished && error is IOException, error?.Message);
            Check("and nothing in it is sent to the trash", !rig.Drive.Get(later)!.Trashed, rig.LogText());

            // Controls: an empty folder still goes, and so does a folder emptied
            // through the letter a moment ago (its files just trashed).
            Equal("an empty folder", "0", Directory.GetFileSystemEntries(rig.P("Empty")).Length.ToString());
            finished = Within(30000, () => Directory.Delete(rig.P("Empty")), out error);
            Check("an empty folder is still removed bare", finished && error == null && rig.Drive.Get(empty)!.Trashed,
                error?.Message + " | " + rig.LogText());
            finished = Within(60000, () => Directory.Delete(rig.P("Music"), recursive: true), out error);
            Check("a folder removed with everything in it still goes",
                finished && error == null && rig.Drive.Get(rig.Music)!.Trashed && rig.Drive.Get(rig.Track)!.Trashed,
                error?.Message + " | " + rig.LogText());
        }

        // ---- 2. a delete, folder create or rename waiting on Google holds only its own name ----

        private static void SlowChangesHoldOnlyTheirOwnName(Rig rig)
        {
            var race = rig.Drive.Find(FakeDrive.Root, "Race")!;
            var doomed = rig.Drive.Find(FakeDrive.Root, "Doomed")!;
            var oldId = rig.Drive.Find(rig.Docs, "old.txt")!;
            var newId = rig.Drive.Find(rig.Docs, "new.txt")!;

            // Requests of one kind held at the fake Drive until let go. The hook
            // only waits (with a limit); it never touches the letter.
            var gate = new ManualResetEventSlim(true);
            string holdKind = "", holdSubject = "";
            int held = 0;
            rig.Drive.BeforeAnswer = (kind, subject) =>
            {
                if (gate.IsSet || kind != holdKind || (holdSubject.Length > 0 && subject != holdSubject)) return;
                Interlocked.Increment(ref held);
                gate.Wait(TimeSpan.FromSeconds(20));
            };
            void Hold(string kind, string subject = "") { holdKind = kind; holdSubject = subject; held = 0; gate.Reset(); }

            bool OthersAnswer(string what)
            {
                bool listed = Within(2000, () => Directory.GetFiles(rig.P("Music")), out var e1);
                bool opened = Within(2000, () => { using (File.OpenRead(rig.P("Music", "song.flac"))) { } }, out var e2);
                bool sized = Within(2000, () => _ = new DriveInfo(rig.Letter).AvailableFreeSpace, out var e3);
                Check($"while {what}, a listing of another folder answers at once", listed && e1 == null, e1?.Message);
                Check($"while {what}, another file opens at once", opened && e2 == null, e2?.Message);
                Check($"while {what}, the drive's free space answers at once", sized && e3 == null, e3?.Message);
                return listed && opened && sized;
            }

            try
            {
                // A delete waiting on Google's trash.
                Hold("trash");
                var deleting = Task.Run(() => File.Delete(rig.P("Docs", "notes.txt")));
                Check("a delete's trash request is being held", DriveMountTests.Eventually(() => held > 0, 10000));
                OthersAnswer("a delete waits on Google");
                gate.Set();
                Check("the held delete finishes once Google answers", deleting.Ended(30000) && deleting.Exception == null);
                Check("and the file is in the Drive trash and gone from the letter",
                    rig.Drive.Get(rig.Notes)!.Trashed && !File.Exists(rig.P("Docs", "notes.txt")), rig.LogText());

                // A new folder waiting on Google.
                Hold("create", "Held");
                var making = Task.Run(() => Directory.CreateDirectory(rig.P("Held")));
                Check("a new folder's request is being held", DriveMountTests.Eventually(() => held > 0, 10000));
                OthersAnswer("a new folder waits on Google");
                gate.Set();
                Check("the held new folder is made once Google answers", making.Ended(30000) && making.Exception == null &&
                    rig.Drive.Find(FakeDrive.Root, "Held") != null && Directory.Exists(rig.P("Held")), rig.LogText());

                // A rename that replaces a file: the replaced one is trashed
                // behind it, so the rename (which Windows holds every open on
                // the drive for) answers at once.
                Hold("trash");
                bool moved = Within(2000, () => File.Move(rig.P("Docs", "new.txt"), rig.P("Docs", "old.txt"), overwrite: true),
                    out var moveError);
                gate.Set();
                Check("a move over another file answers while the replaced file's trash waits on Google",
                    moved && moveError == null, moveError?.Message);
                Check("and the replaced file still ends up in the Drive trash",
                    DriveMountTests.Eventually(() => rig.Drive.Get(oldId)!.Trashed, 30000), rig.LogText());
                bool read = Within(10000, () =>
                {
                    if (File.ReadAllText(rig.P("Docs", "old.txt")) != "new") throw new IOException("the old contents");
                }, out var readError);
                Check("with the moved file under its new name",
                    rig.Drive.Get(newId)!.Name == "old.txt" && !rig.Drive.Get(newId)!.Trashed && read && readError == null,
                    readError?.Message);

                // Controls that the name gates keep what WinFsp's guard gave.
                // Two new folders of one name at once: one folder on Drive.
                Hold("create", "Twin");
                var first = Task.Run(() => Directory.CreateDirectory(rig.P("Twin")));
                Check("one new folder's request is being held", DriveMountTests.Eventually(() => held > 0, 10000));
                var second = Task.Run(() => Directory.CreateDirectory(rig.P("Twin")));
                Thread.Sleep(500);
                gate.Set();
                Check("two programs making the same new folder at once both succeed",
                    first.Ended(30000) && second.Ended(30000) && first.Exception == null && second.Exception == null,
                    (first.Exception ?? second.Exception)?.GetBaseException().Message);
                Equal("and Drive has the folder once", "1", rig.Drive.Count(FakeDrive.Root, "Twin").ToString());

                // Two opens-or-creates of one new file, both past the "is it
                // there" question before either made it: one file on Drive.
                var listGate = new ManualResetEventSlim(false);
                int listHeld = 0;
                rig.Drive.OnList = async (folder, ct) =>
                {
                    if (folder != race || listGate.IsSet) return;
                    Interlocked.Increment(ref listHeld);
                    await Task.Run(() => listGate.Wait(TimeSpan.FromSeconds(20), ct), ct);
                };
                FileStream? a = null, b = null;
                try
                {
                    var openA = Task.Run(() => a = new FileStream(rig.P("Race", "same.txt"), FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.ReadWrite, 1));
                    Check("the new file's folder listing is being held", DriveMountTests.Eventually(() => listHeld > 0, 10000));
                    var openB = Task.Run(() => b = new FileStream(rig.P("Race", "same.txt"), FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.ReadWrite, 1));
                    Thread.Sleep(500);
                    listGate.Set();
                    bool bothOpen = openA.Ended(30000) && openB.Ended(30000) && openA.Exception == null && openB.Exception == null;
                    Check("two programs opening-or-creating the same new file at once both get it", bothOpen,
                        (openA.Exception ?? openB.Exception)?.GetBaseException().Message);
                    if (bothOpen)
                    {
                        a!.Write(Encoding.UTF8.GetBytes("A"));
                        a.Flush();
                        b!.Position = 1;
                        b.Write(Encoding.UTF8.GetBytes("B"));
                        b.Flush();
                    }
                }
                finally
                {
                    listGate.Set();
                    rig.Drive.OnList = null;
                    a?.Dispose();
                    b?.Dispose();
                }
                Check("and Drive has the file once, with both writes in it",
                    rig.Uploaded(20000) && DriveMountTests.Eventually(() => rig.Drive.Count(race, "same.txt") == 1, 10000) &&
                    rig.Drive.Count(race, "same.txt") == 1 &&
                    Encoding.UTF8.GetString(rig.Drive.Get(rig.Drive.Find(race, "same.txt")!)!.Data) == "AB",
                    $"{rig.Drive.Count(race, "same.txt")} copies | {rig.LogText()}");

                // A file made in a folder while the folder's delete waits on
                // Google waits for it, then fails: nothing goes into a trashed folder.
                Equal("an empty folder to remove", "0", Directory.GetFileSystemEntries(rig.P("Doomed")).Length.ToString());
                Hold("trash", "");
                var removing = Task.Run(() => Directory.Delete(rig.P("Doomed")));
                Check("the folder's trash request is being held", DriveMountTests.Eventually(() => held > 0, 10000));
                var late = Task.Run(() => File.WriteAllText(rig.P("Doomed", "late.txt"), "too late"));
                Thread.Sleep(700);
                bool waited = !late.IsCompleted;
                gate.Set();
                bool removed = removing.Ended(30000) && removing.Exception == null;
                bool lateEnded = late.Ended(30000);
                Check("a file made in a folder being removed waits for the removal", waited);
                Check("then fails, since the folder is gone", removed && lateEnded && late.Exception != null,
                    late.Exception?.GetBaseException().Message);
                Thread.Sleep(800);
                Check("and nothing is uploaded into the trashed folder",
                    rig.Drive.Get(doomed)!.Trashed && rig.Drive.Find(doomed, "late.txt") == null && rig.Uploaded(), rig.LogText());
            }
            finally
            {
                gate.Set();
                rig.Drive.BeforeAnswer = null;
            }
        }
    }
}
