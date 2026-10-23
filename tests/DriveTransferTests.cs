using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Copying and moving to, from and inside Google Drive in the file manager,
    /// against the fake Drive over a scratch sync root: one file Drive refuses
    /// does not stop a folder copy, moves and renames ride out "not now", a
    /// move to Drive says when it could not remove the original, unreadable
    /// folders and locked files are said plainly, the progress speed does not
    /// fall to "unknown" between Drive's four-megabyte pieces, and a slow
    /// listing gets a longer second try. Run alone with
    /// EXPLORERNATIVE_SELFTEST_ONLY=drivecopy.
    /// </summary>
    internal static class DriveTransferTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static void Check(string what, bool ok, string? detail = null) => _check("Drive transfers: " + what, ok, detail);

        private static readonly DateTime When = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        private static string HtmlPage => "<!DOCTYPE html><html><body>502. That's an error.</body></html>";

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            Console.WriteLine("Copying and moving with Google Drive, against a fake Drive:");
            RecentRateTests();
            await SlowListingTests();
            await OnTheLetter();
        }

        // ---- the speed between Drive's pieces ----

        private static void RecentRateTests()
        {
            // A copy off Drive: 4MB arrives every 13 seconds, sampled four
            // times a second as the window's ticker does.
            const long Piece = 4L * 1024 * 1024;
            var rate = new RecentRate();
            int zeroAfterStart = 0, samples = 0;
            for (double t = 0; t <= 60; t += 0.25)
            {
                long bytes = Piece * (long)(t / 13);
                double speed = rate.Observe(bytes, TimeSpan.FromSeconds(t));
                if (t > 14) { samples++; if (speed <= 0) zeroAfterStart++; }
            }
            Check("a speed is shown between Drive's pieces on a slow line, not \"unknown\"",
                zeroAfterStart == 0, $"{zeroAfterStart} of {samples} samples said nought");

            // And a copy that has truly stopped says nought once a minute has gone.
            double last = 0;
            for (double t = 60.25; t <= 130; t += 0.25) last = rate.Observe(Piece * 4, TimeSpan.FromSeconds(t));
            Check("a transfer that has not moved for over a minute shows no speed", last == 0, last.ToString("0"));

            // Steady progress still reads as the last ten seconds, not the minute.
            var steady = new RecentRate();
            double now = 0;
            for (double t = 0; t <= 30; t += 0.25)
                now = steady.Observe((long)(t < 20 ? t * 1_000_000 : 20_000_000 + (t - 20) * 100_000), TimeSpan.FromSeconds(t));
            Check("a transfer that slowed down shows the speed it goes at now",
                now > 50_000 && now < 200_000, now.ToString("0"));
        }

        // ---- a slow listing somebody is waiting for ----

        private static async Task SlowListingTests()
        {
            var fake = new FakeDrive();
            var big = fake.AddFolder("Big");
            fake.AddFile("one.flac", new byte[] { 1 }, When, big);
            var client = new DriveClient(fake, 0.0005)
            {
                QuickTimeoutForTests = TimeSpan.FromMilliseconds(400),
                InteractiveSlowAttempt = TimeSpan.FromSeconds(5),
            };

            int lists = 0;
            fake.OnList = async (_, ct) =>
            {
                if (Interlocked.Increment(ref lists) == 1) await Task.Delay(1500, ct);
            };
            List<DriveEntry>? listed = null;
            string problem = "";
            try { listed = await client.ListChildren(big, CancellationToken.None, interactive: true); }
            catch (Exception ex) { problem = ex.Message; }
            Check("a page slower than the quick client's limit gets a longer second try, and arrives",
                listed?.Count == 1 && lists == 2, $"{lists} asks, {problem}");

            // Still bounded: a listing that never answers fails with a sentence.
            fake.OnList = (_, ct) => Task.Delay(20_000, ct);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Exception? failure = null;
            try { await client.ListChildren(big, CancellationToken.None, interactive: true); }
            catch (Exception ex) { failure = ex; }
            Check("and one that never answers still fails, saying how long it waited",
                failure is TimeoutException && failure.Message.Contains("5 seconds") && clock.Elapsed < TimeSpan.FromSeconds(12),
                $"{failure?.GetType().Name}: {failure?.Message} after {clock.Elapsed.TotalSeconds:0.0}s");
            fake.OnList = null;
        }

        // ---- on the letter ----

        private static async Task OnTheLetter()
        {
            var fake = new FakeDrive();
            var client = new DriveClient(fake, 0.0005);
            var root = Path.Combine(Path.GetTempPath(), "en-xfer-" + Guid.NewGuid().ToString("N")[..8]);
            var local = Path.Combine(Path.GetTempPath(), "en-xfer-pc-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(local);

            var album = fake.AddFolder("Album");
            fake.AddFile("a.flac", new byte[] { 1 }, When, album);
            var refused = fake.AddFile("b.flac", new byte[] { 2, 2 }, When, album);
            fake.AddFile("c.flac", new byte[] { 3, 3, 3 }, When, album);
            var disc = fake.AddFolder("Disc 2", album);
            fake.AddFile("d.flac", new byte[] { 4 }, When, disc);
            var dest = fake.AddFolder("Dest");
            var from = fake.AddFolder("From");
            var song = fake.AddFile("song.flac", new byte[] { 5 }, When, from);
            var named = fake.AddFile("named.flac", new byte[] { 6 }, When, from);
            var old = fake.AddFile("old.flac", new byte[] { 7 }, When, from);
            var target = fake.AddFolder("Target");

            using var drive = new GoogleDrive();
            DriveMount? mount = null;
            try
            {
                try
                {
                    mount = new DriveMount(client, root, 16L * 1024 * 1024, _ => { });
                    mount.Start("root", assignLetter: false);
                }
                catch (Exception ex)
                {
                    Check("a provider stands up over a scratch folder", false, ex.Message);
                    return;
                }
                drive.UseForTests(mount, client);

                string P(params string[] parts) => Path.Combine(new[] { root }.Concat(parts).ToArray());
                void Listed(string path) => mount!.Populate(path);
                Listed(root);
                foreach (var f in new[] { "Album", "Dest", "From", "Target" }) Listed(P(f));

                await CopyCarriesOn(fake, drive, P, refused, dest);
                await MovesRideOutNotNow(fake, client, drive, P, song, named, old, from, target);
                await UploadsSayWhy(fake, drive, P, local, target);
            }
            finally
            {
                try { mount?.Dispose(); } catch { }
                try { Directory.Delete(root, true); } catch { }
                try
                {
                    foreach (var f in Directory.GetFiles(local, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(local, true);
                }
                catch { }
            }
        }

        /// <summary>One file whose owner turned copying off, in a folder copied inside Drive.</summary>
        private static async Task CopyCarriesOn(FakeDrive fake, GoogleDrive drive, Func<string[], string> P0,
            string refused, string dest)
        {
            string P(params string[] parts) => P0(parts);
            FakeDrive.Fault NoCopy() => new()
            {
                Kind = "copy", Times = 1, Status = 403, Subject = s => s == refused,
                Body = FakeDrive.ErrorBody(403, "cannotCopyFile", "This file cannot be copied by the authenticated user."),
            };

            // The control: with nobody collecting failures, the first one ends
            // the copy, as it did for every copy before.
            fake.AddFault(NoCopy());
            bool threw = false;
            try { await drive.CopyOnDrive(P("Album"), P("Dest"), "Old way", CancellationToken.None, new DriveCopyReport()); }
            catch (InvalidOperationException) { threw = true; }
            Check("(control) without a failure list, one refused file still ends the copy part way",
                threw && fake.Find(dest, "Old way/Disc 2/d.flac") == null);

            fake.AddFault(NoCopy());
            var failures = new List<string>();
            var report = new DriveCopyReport { OnFailed = (name, why) => { lock (failures) failures.Add($"{name}: {why}"); } };
            int made = 0;
            string problem = "";
            try { made = await drive.CopyOnDrive(P("Album"), P("Dest"), "Album copy", CancellationToken.None, report); }
            catch (Exception ex) { problem = ex.Message; }
            Check("a folder copy inside Drive carries on past a file Drive will not copy",
                problem == "" && made == 5 &&
                fake.Find(dest, "Album copy/a.flac") != null && fake.Find(dest, "Album copy/c.flac") != null &&
                fake.Find(dest, "Album copy/Disc 2/d.flac") != null && fake.Find(dest, "Album copy/b.flac") == null,
                $"made {made} {problem}");
            Check("and names the one it could not copy, with Google's reason",
                failures.Count == 1 && failures[0].StartsWith("b.flac: ") && failures[0].Contains("cannot be copied"),
                string.Join(" | ", failures));
        }

        /// <summary>Google's "not now" in the middle of moves, renames and trashing on the letter.</summary>
        private static async Task MovesRideOutNotNow(FakeDrive fake, DriveClient client, GoogleDrive drive,
            Func<string[], string> P0, string song, string named, string old, string from, string target)
        {
            string P(params string[] parts) => P0(parts);

            // The folder monitor's own moves are untouched: one try, as before.
            fake.AddFault(new FakeDrive.Fault { Kind = "patch", Times = 1, Status = 503, Body = HtmlPage, Subject = s => s == old });
            bool once;
            try { await client.MoveOrRename(old, null, from, target, CancellationToken.None); once = false; }
            catch (DriveClient.DriveStatusException ex) { once = ex.Status == 503; }
            Check("(control) a move asked once still fails on one 503, for the folder monitor which waits Google out itself",
                once && fake.Get(old)!.Parent == from);

            int served = fake.FaultsServed;
            fake.AddFault(new FakeDrive.Fault
            {
                Kind = "get", Times = 1, Status = 403, Subject = s => s == song,
                Body = FakeDrive.ErrorBody(403, "rateLimitExceeded", "Rate Limit Exceeded"),
            });
            fake.AddFault(new FakeDrive.Fault { Kind = "patch", Times = 1, Status = 503, Body = HtmlPage, Subject = s => s == song });
            bool moved = false;
            string problem = "";
            try { moved = await drive.MoveOnDrive(P("From", "song.flac"), P("Target")); }
            catch (Exception ex) { problem = ex.Message; }
            Check("a move inside Drive rides out a rate limit on its permission check and a 503 on the move",
                moved && fake.Get(song)!.Parent == target && fake.FaultsServed - served == 2,
                $"{problem} served {fake.FaultsServed - served}");

            fake.AddFault(new FakeDrive.Fault { Kind = "patch", Times = 1, Status = 502, Body = HtmlPage, Subject = s => s == named });
            string? renamed = null;
            try { renamed = await drive.RenameOnDrive(P("From", "named.flac"), "renamed.flac", interactive: true); }
            catch (Exception ex) { problem = ex.Message; }
            Check("F2 on the letter rides out one 502", renamed == "renamed.flac" && fake.Get(named)!.Name == "renamed.flac",
                renamed ?? problem);

            fake.AddFault(new FakeDrive.Fault { Kind = "trash", Times = 1, Status = 503, Body = HtmlPage, Subject = s => s == old });
            bool trashed = false;
            try { trashed = await drive.TrashPath(P("From", "old.flac")); }
            catch (Exception ex) { problem = ex.Message; }
            Check("trashing the original after a move out rides out one 503", trashed && fake.Get(old)!.Trashed, problem);
        }

        /// <summary>Copies and moves up to Drive that cannot do everything, and say so.</summary>
        private static async Task UploadsSayWhy(FakeDrive fake, GoogleDrive drive, Func<string[], string> P0,
            string local, string target)
        {
            string P(params string[] parts) => P0(parts);

            async Task<DriveUpload> Send(bool move, params string[] paths)
            {
                var upload = new DriveUpload(drive, P("Target"), move, PasteConflictPolicy.AutoRename, null);
                await upload.Run(DriveUpload.Survey(paths), CancellationToken.None);
                return upload;
            }

            // A read-only file moved to Drive: gone from the PC, as Explorer's own move does.
            var readOnly = Path.Combine(local, "read only.txt");
            File.WriteAllText(readOnly, "ro");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            var up = await Send(true, readOnly);
            Check("a read-only file moved to Drive is taken off the PC once Drive has it",
                up.Errors.Count == 0 && !File.Exists(readOnly) && fake.Find(target, "read only.txt") != null,
                string.Join(" | ", up.Errors));

            // One another program holds without sharing delete: copied, kept, and said.
            var held = Path.Combine(local, "held.txt");
            File.WriteAllText(held, "held");
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                up = await Send(true, held);
                Check("a moved file whose original cannot be removed is said, not reported as moved",
                    File.Exists(held) && fake.Find(target, "held.txt") != null &&
                    up.Errors.Any(e => e.StartsWith("held.txt: ") && e.Contains("in both places")),
                    string.Join(" | ", up.Errors));
            }

            // A file open with no sharing at all: one plain sentence, not Windows' with the path again.
            var locked = Path.Combine(local, "locked.pst");
            File.WriteAllText(locked, "pst");
            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                up = await Send(false, locked);
                Check("a file open in another program is said plainly",
                    up.Errors.Count == 1 && up.Errors[0].Contains("another program has it open") &&
                    !up.Errors[0].Contains("being used by another process"),
                    string.Join(" | ", up.Errors));
            }

            // A folder Windows will not let us look inside.
            var top = Path.Combine(local, "Top");
            var inside = Path.Combine(top, "Private");
            Directory.CreateDirectory(inside);
            File.WriteAllText(Path.Combine(top, "open.txt"), "open");
            File.WriteAllText(Path.Combine(inside, "secret.txt"), "secret");
            var me = WindowsIdentity.GetCurrent().User!;
            var deny = new FileSystemAccessRule(me, FileSystemRights.ListDirectory, AccessControlType.Deny);
            var folder = new DirectoryInfo(inside);
            bool denied = false;
            try
            {
                var rules = folder.GetAccessControl();
                rules.AddAccessRule(deny);
                folder.SetAccessControl(rules);
                try { Directory.GetFiles(inside); } catch (UnauthorizedAccessException) { denied = true; }
                if (!denied) { Check("the fixture folder can be made unreadable", false); return; }

                var work = DriveUpload.Survey(new[] { top });
                Check("the walk keeps a folder it could not look inside",
                    work.Unreadable.Count == 1 && work.Unreadable[0] == @"Top\Private", string.Join(",", work.Unreadable));
                var upload = new DriveUpload(drive, P("Target"), false, PasteConflictPolicy.AutoRename, null);
                await upload.Run(work, CancellationToken.None);
                Check("and the copy to Drive says what was not sent, rather than only that the rest was",
                    upload.FilesUploaded == 1 && upload.Errors.Any(e => e.StartsWith(@"Top\Private: ") && e.Contains("nothing in it was sent")),
                    string.Join(" | ", upload.Errors));
            }
            finally
            {
                try
                {
                    var rules = folder.GetAccessControl();
                    rules.RemoveAccessRule(deny);
                    folder.SetAccessControl(rules);
                }
                catch { }
            }
        }
    }
}
