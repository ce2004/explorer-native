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
    }
}
