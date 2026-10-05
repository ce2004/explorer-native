using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Copying out of the Google Drive letter, and the terminal the installer
    /// used to hold.
    ///
    /// Three faults reported as one — "I can't copy items from Google Drive, the
    /// progress bar is breaking, and it just won't go to Downloads" — and each is
    /// checked here separately, because each had a different cause and a fix for
    /// one says nothing about the others.
    /// </summary>
    internal static class DriveCopyTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Copying out of Google Drive:");

            await OfflineAttributeTests();
            ProgressCorrectionTests();
            FetchWatchTests();
            SourceShapeTests();
            CopyWithinDriveTests();
            SyncRootConnectTests();
            ReplacedContentsTests();
            LetterTests();
            SharedDrivesTests();
        }

        /// <summary>
        /// The shared drives folder: every request reaches shared drives, and
        /// nothing can be made inside the list of them.
        /// </summary>
        private static void SharedDrivesTests()
        {
            Equal("a request asks for every drive",
                "https://www.googleapis.com/drive/v3/files/abc?alt=media&supportsAllDrives=true",
                DriveClient.AllDrives("https://www.googleapis.com/drive/v3/files/abc?alt=media"));
            Equal("including one with no query yet",
                "https://www.googleapis.com/drive/v3/files/abc?supportsAllDrives=true",
                DriveClient.AllDrives("https://www.googleapis.com/drive/v3/files/abc"));
            Equal("and never twice",
                "https://www.googleapis.com/drive/v3/files?supportsAllDrives=true",
                DriveClient.AllDrives("https://www.googleapis.com/drive/v3/files?supportsAllDrives=true"));
            Equal("an address that is not Google's is left alone",
                "http://127.0.0.1:5000/", DriveClient.AllDrives("http://127.0.0.1:5000/"));

            var mount = Source("DriveMount.cs");
            Check("the top of the letter lists the shared drives as a folder",
                mount != null && mount.Contains("if (accountRoot) entries.Insert(0, sharedDrives);",
                    StringComparison.Ordinal));
            Check("and a listing inside a shared drive names the drive",
                mount != null && mount.Contains("driveId: driveId", StringComparison.Ordinal));

            var client = new DriveClient(new GoogleAuth("test", "test",
                Path.Combine(Path.GetTempPath(), "en-no-token-" + Guid.NewGuid().ToString("N") + ".dat")));
            bool refused = false;
            try { client.CreateFolder("x", DriveClient.SharedDrivesId, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidOperationException ex) { refused = ex.Message.Contains("Shared drives"); }
            catch { }
            Check("nothing can be made directly inside the list of shared drives", refused);
        }

        /// <summary>
        /// Windows accepts the registration and the connect flags as declared.
        ///
        /// The flags were wrong once — REQUIRE_FULL_FILE_PATH was declared with
        /// the value of REQUIRE_PROCESS_INFO — and nothing noticed, because both
        /// are accepted. This at least proves the ones declared now are: a
        /// provider is stood up over a scratch folder, no network, and taken down.
        /// </summary>
        private static void SyncRootConnectTests()
        {
            var root = Path.Combine(Path.GetTempPath(), "en-syncroot-" + Guid.NewGuid().ToString("N")[..8]);
            var log = new List<string>();
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "leftover.txt"), "x");

                var auth = new GoogleAuth("test", "test", Path.Combine(root + "-token.dat"));
                var client = new DriveClient(auth);
                bool cleared = DriveMount.TryReclaim(client, root, 64L * 1024 * 1024, line => { lock (log) log.Add(line); });

                Check("a sync root registers and connects with the declared flags",
                    !log.Any(l => l.Contains("could not connect", StringComparison.OrdinalIgnoreCase)),
                    string.Join(" | ", log));
                Check("and the provider clears its folder and goes away", cleared);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>
        /// A name listed again as a different file, or the same file with new
        /// contents, must leave the placeholder describing what Drive has now.
        /// "Already exists" was taken as placed, so a copy out of the letter
        /// delivered new bytes cut off at the old length. A scratch sync root,
        /// no network.
        /// </summary>
        private static void ReplacedContentsTests()
        {
            var root = Path.Combine(Path.GetTempPath(), "en-replaced-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var auth = new GoogleAuth("test", "test", root + "-token.dat");
                var problem = DriveMount.ReplacedContentsCheck(new DriveClient(auth), root, _ => { });
                Check("a placeholder already on disk is updated to what Drive now says", problem == null, problem ?? "");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>A letter that is in use is never handed to Drive.</summary>
        private static void LetterTests()
        {
            var system = Path.GetPathRoot(Environment.SystemDirectory)![0];
            Check("the system drive's letter is in use", DriveLetter.InUse(system));
            Check("so is anything that is not a letter", DriveLetter.InUse('1'));
        }

        /// <summary>
        /// "It won't go to Downloads": the file did go, with the right bytes and
        /// the placeholder's Offline flag, which Explorer draws as unavailable.
        ///
        /// The fixture sets Offline on an ordinary file, which is exactly what a
        /// placeholder presents to robocopy — so this runs the real engine and the
        /// real robocopy and needs no Drive at all. The first half proves the
        /// premise: without the switch, the flag really is carried across. A test
        /// that only checked the fixed path would pass on a machine where robocopy
        /// had never copied the attribute in the first place.
        /// </summary>
        private static async Task OfflineAttributeTests()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-offline-" + Guid.NewGuid().ToString("N")[..8]);
            var source = Path.Combine(dir, "src");
            Directory.CreateDirectory(source);

            try
            {
                var file = Path.Combine(source, "track.ogg");
                File.WriteAllBytes(file, new byte[200_000]);
                File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.Offline);

                if ((File.GetAttributes(file) & FileAttributes.Offline) == 0)
                {
                    Check("the filesystem lets the fixture be marked Offline", false);
                    return;
                }

                var plain = Path.Combine(dir, "plain");
                Directory.CreateDirectory(plain);
                await RoboCopyEngine.RunAsync(new[] { file }, plain, move: false, threads: 1,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None);

                var carried = Path.Combine(plain, "track.ogg");
                Check("robocopy on its own copies the Offline flag across (the premise)",
                    File.Exists(carried) && (File.GetAttributes(carried) & FileAttributes.Offline) != 0,
                    File.Exists(carried) ? File.GetAttributes(carried).ToString() : "not copied");

                var fixedDir = Path.Combine(dir, "fixed");
                Directory.CreateDirectory(fixedDir);
                var result = await RoboCopyEngine.RunAsync(new[] { file }, fixedDir, move: false, threads: 1,
                    PasteConflictPolicy.AutoRename, null, CancellationToken.None, cloudSource: true);

                var landed = Path.Combine(fixedDir, "track.ogg");
                Check("copied out of the cloud, it lands", result.Failed == 0 && File.Exists(landed));
                Check("and it lands as an ordinary file, not an Offline one",
                    File.Exists(landed) && (File.GetAttributes(landed) & FileAttributes.Offline) == 0,
                    File.Exists(landed) ? File.GetAttributes(landed).ToString() : "missing");
                Check("with every byte", File.Exists(landed) && new FileInfo(landed).Length == 200_000);
            }
            finally
            {
                try
                {
                    foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(dir, recursive: true);
                }
                catch { }
            }
        }

        /// <summary>
        /// "The progress bar is breaking": measured on six tracks, 0% for the first
        /// four and a half seconds, then whole-file jumps with the name one file
        /// behind. The correction takes the larger of robocopy's count and the
        /// mount's, and may never go backwards.
        /// </summary>
        private static void ProgressCorrectionTests()
        {
            var elapsed = TimeSpan.FromSeconds(4);
            TransferProgress Robocopy(long done, string item = "") =>
                new(done, 10_000_000, 0, 6, item, 0, elapsed);

            var early = RoboCopyEngine.WithDownloads(Robocopy(0), 2_500_000, "a.ogg", 0);
            Equal("while robocopy still says nothing, the bytes Drive has handed over are shown",
                "2500000", early.BytesDone.ToString());
            Equal("and the file named is the one downloading", "a.ogg", early.CurrentItem);
            Check("and the speed is worked out from them rather than left at zero", early.BytesPerSecond > 0);

            var ahead = RoboCopyEngine.WithDownloads(Robocopy(6_000_000, "b.ogg"), 5_000_000, "c.ogg", 2_500_000);
            Equal("robocopy's count wins when it is the larger — a file already downloaded is never fetched",
                "6000000", ahead.BytesDone.ToString());

            var late = RoboCopyEngine.WithDownloads(Robocopy(1_000_000), 900_000, "d.ogg", 6_000_000);
            Equal("and nothing makes the window go backwards", "6000000", late.BytesDone.ToString());

            var over = RoboCopyEngine.WithDownloads(Robocopy(0), 99_000_000, "e.ogg", 0);
            Equal("nor past the total", "10000000", over.BytesDone.ToString());

            var quiet = RoboCopyEngine.WithDownloads(Robocopy(100, "f.ogg"), 0, null, 0);
            Equal("with nothing downloading, robocopy's name stands", "f.ogg", quiet.CurrentItem);
            Equal("and so do the item counts, which only robocopy knows", "6", quiet.ItemsTotal.ToString());
        }

        /// <summary>
        /// The mount's side of it: what counts as "for this copy", and counting a
        /// file once however many times Windows asks for its bytes.
        /// </summary>
        private static void FetchWatchTests()
        {
            Check("a folder covers what is under it",
                DriveMount.FetchWatch.Covers(@"music\album", @"music\album\01.flac"));
            Check("and itself", DriveMount.FetchWatch.Covers(@"music\album", @"music\album"));
            Check("but not a sibling that merely starts with the same letters",
                !DriveMount.FetchWatch.Covers(@"music\album", @"music\album two\01.flac"));
            Check("case does not matter on this filesystem",
                DriveMount.FetchWatch.Covers(@"Music\Album", @"music\album\01.flac"));
            Check("the empty prefix is the whole drive", DriveMount.FetchWatch.Covers("", @"anything\at\all"));

            var watch = DriveMount.FetchWatch.Detached(@"music\album");
            const long mb = 1_048_576;

            watch.Note(@"music\album\01.flac", 0, 4 * mb, 20 * mb);
            watch.Note(@"music\album\01.flac", 0, 2 * mb, 20 * mb);   // a re-read of an earlier range
            Equal("a range served twice counts once", (4 * mb).ToString(), watch.BytesServed.ToString());

            // The lie, reproduced: something reads the end of the file early.
            watch.Note(@"music\album\01.flac", 16 * mb, 20 * mb, 20 * mb);
            Equal("reading the end early does not make the file look finished",
                (8 * mb).ToString(), watch.BytesServed.ToString());

            watch.Note(@"music\album\01.flac", 4 * mb, 16 * mb, 20 * mb);   // the gap, filled
            Equal("and once the gap is filled the file is exactly its size",
                (20 * mb).ToString(), watch.BytesServed.ToString());

            watch.Note(@"music\album\02.flac", 0, 12 * mb, 3 * mb);   // the last aligned chunk runs past the end
            Equal("no file counts past its own size", (23 * mb).ToString(), watch.BytesServed.ToString());
            Equal("the latest file served is the one named", "02.flac", watch.Current ?? "(none)");

            watch.Note(@"music\other\song.flac", 0, 5 * mb, 5 * mb);   // a track playing alongside
            Equal("a file outside the copy does not count towards it",
                (23 * mb).ToString(), watch.BytesServed.ToString());
            Equal("and does not take the name either", "02.flac", watch.Current ?? "(none)");
        }

        /// <summary>
        /// The parts that live in files the suite cannot compile, checked as text.
        /// </summary>

        /// <summary>
        /// Copying from the drive to the drive, which used to be refused.
        ///
        /// "Copying inside Google Drive is not supported yet. Move works, or copy
        /// out and back in." — on a comment claiming a copy "means downloading and
        /// uploading the same bytes". It does not: Drive has `files.copy`, which
        /// duplicates server-side, so a four gigabyte video copies in one request
        /// and nothing crosses the link. The advice in the refusal was to send
        /// those four gigabytes down and back up again to avoid it.
        ///
        /// Checked by reading the source, like the rest of this file: none of it
        /// can run without an account and a mounted drive, and a test that needs
        /// both is a test that does not run.
        /// </summary>
        private static void CopyWithinDriveTests()
        {
            var main = Source("MainForm.cs");
            var drive = Source("GoogleDrive.cs");
            var writes = Source("DriveWrites.cs");

            if (main == null || drive == null || writes == null)
            {
                Check("the Drive sources can be read", false);
                return;
            }

            Check("the refusal is gone",
                !main.Contains("Copying inside Google Drive is not supported", StringComparison.Ordinal));

            // A folder copied into itself must not find its own copy to copy.
            int listed = drive.IndexOf("var children = await client.ListChildren(id, token, driveId: fromDrive)", StringComparison.Ordinal);
            int created = drive.IndexOf("var folderId = await client.CreateFolder(name, intoId, token)", StringComparison.Ordinal);
            Check("a folder's contents are listed before its copy is made",
                listed >= 0 && created > listed);
            Check("and nothing the copy made is copied again",
                drive.Split("if (made.Contains(child.Id)) continue;").Length - 1 == 2);

            Check("and a copy with both ends on the drive is done on the drive",
                main.Contains("if (allFromDrive) return await CopyWithinDrive(paths, destination, conflicts);",
                    StringComparison.Ordinal));

            // Server-side, which is the whole point. A copy that went through
            // DriveUpload would be the download-and-upload the refusal described.
            Check("the copy is one request to Drive, not an upload",
                writes.Contains("$\"{Files}/{fileId}/copy?fields=id,name,mimeType,size,modifiedTime\"",
                    StringComparison.Ordinal));

            Check("and it goes through the retrying sender like every other write",
                (Between(writes, "public async Task<DriveEntry> Copy(", "public async Task MoveOrRename(") ?? "")
                    .Contains("SendWithRetry(", StringComparison.Ordinal));

            // Drive will not copy a folder, so a folder is a new folder and then
            // the same question about everything in it — and the listing walked
            // is Drive's own, so a folder nobody has opened here copies too.
            Check("a folder is copied by making one and copying what is in it",
                drive.Contains("var folderId = await client.CreateFolder(name, intoId, token)",
                    StringComparison.Ordinal) &&
                drive.Contains("await client.ListChildren(id, token, driveId: fromDrive)",
                    StringComparison.Ordinal));

            Check("and it recurses rather than stopping at the first level",
                drive.Contains("count += await CopyFolderById(client, mount, child.Id, child.Name,",
                    StringComparison.Ordinal));

            // Two files of one name in one folder is legal in Drive and
            // impossible on a drive letter: the second placeholder has nowhere
            // to go. So the name is settled before the request.
            Check("a name already in use is resolved before the copy is asked for",
                main.Contains("NameRules.UniqueAmong(DriveMount.RoomForNumber(driveName),",
                    StringComparison.Ordinal));

            Check("and the paste conflict setting decides how",
                main.Contains("conflicts == PasteConflictPolicy.Skip", StringComparison.Ordinal) &&
                main.Contains("conflicts == PasteConflictPolicy.FillGaps", StringComparison.Ordinal) &&
                main.Contains("conflicts == PasteConflictPolicy.Overwrite", StringComparison.Ordinal));

            // Overwriting is finished after the copy has landed, never before it.
            // The other order leaves a moment with the old one trashed and the
            // new one not yet there, and a copy that fails in that moment has
            // taken something away and put nothing back.
            var replace = Between(main, "private async Task ReplaceOnDrive(",
                "Turns the configured conflict policy") ?? "";

            Check("an overwrite trashes the old one only after the new one exists",
                replace.IndexOf("TrashPath", StringComparison.Ordinal) >= 0 &&
                replace.IndexOf("RenameOnDrive", StringComparison.Ordinal) >
                replace.IndexOf("TrashPath", StringComparison.Ordinal));

            Check("and it trashes rather than deletes",
                replace.Contains("TrashPath", StringComparison.Ordinal) &&
                !replace.Contains("Delete", StringComparison.Ordinal));
        }

        private static void SourceShapeTests()
        {
            var main = Source("MainForm.cs");
            var program = Source("Program.cs");
            var engine = Source("RoboCopyEngine.cs");

            if (main == null || program == null || engine == null)
            {
                Check("the sources can be read", false);
                return;
            }

            Check("a copy out of Drive tells the engine so",
                main.Contains("cloudSource: fromDrive", StringComparison.Ordinal));

            // Deleting a placeholder is not deleting the file: nothing answers the
            // delete callback, so a robocopy move out of Drive leaves the file in
            // Drive and it comes straight back on the next mount.
            Check("a move out of Drive is never handed to robocopy as a move",
                main.Contains("move && !fromDrive", StringComparison.Ordinal));
            Check("it is finished by sending the originals to the Drive trash",
                main.Contains("await FinishMoveOutOfDrive(paths, destination, result.Collisions", StringComparison.Ordinal));
            Check("and only when every file copied",
                main.Contains("if (move && fromDrive && !result.Cancelled && result.Failed == 0)", StringComparison.Ordinal));

            Check("the progress window is corrected by what Drive has handed over",
                main.Contains("RoboCopyEngine.WithDownloads(p, fetches.BytesServed, fetches.Current, shown",
                    StringComparison.Ordinal));

            Check("Drive's Offline flag is stripped from what robocopy writes",
                engine.Contains("args.Add(\"/A-:O\");", StringComparison.Ordinal));

            // The player that went silent for good. Every one of these was a wait
            // with no end, and they chained: a provider callback with no deadline,
            // a read that held a lock across it, a Dispose that needed the lock,
            // and a play gate held across the Dispose.
            var streaming = Source("StreamingSource.cs");
            var cache = Source("DriveFileCache.cs");
            var mount = Source("DriveMount.cs");

            if (streaming != null)
            {
                Check("a read ahead of the download is not made while holding the lock",
                    !streaming.Contains("read = _remote.Read(buffer, 0, wanted);", StringComparison.Ordinal) &&
                    streaming.Contains("got = _source.ReadAsync(position, bytes, _closing.Token)",
                        StringComparison.Ordinal));
            }

            if (mount != null)
            {
                Check("a placeholder callback's listing has a deadline",
                    mount.Contains("var entries = ListFolder(relative, folderId, deadline.Token);", StringComparison.Ordinal));
                Check("and so does a data callback, whose keep-alive otherwise holds Windows off for ever",
                    mount.Contains(".Read(start, (int)wanted, deadline.Token)", StringComparison.Ordinal));
            }

            if (cache != null)
            {
                Check("and the wait for a chunk actually listens to that deadline",
                    cache.Contains("chunk = await whole.WaitAsync(token);", StringComparison.Ordinal) &&
                    cache.Contains("Task.Delay(ChunkPatienceMilliseconds, token)", StringComparison.Ordinal));
            }

            // The process that could not be killed, and the reboot it cost.
            //
            // A read of a placeholder is answered by this same process, so the
            // provider must outlive every reader in it. Quit took the Drive down
            // first, while the player could still be reading a track off the
            // letter, and a read with nobody left to answer it is I/O Windows
            // will not let the process exit with. Measured twice: exit code set,
            // runtime gone, threads parked in the kernel, taskkill refused.
            var tray = Source("TrayApplicationContext.cs");
            if (tray != null)
            {
                int player = tray.IndexOf("_audio.Dispose();", StringComparison.Ordinal);
                int waits = tray.IndexOf("StreamingSource.WaitForReads(", StringComparison.Ordinal);
                int drive = tray.IndexOf("_drive.Dispose();", StringComparison.Ordinal);

                Check("quitting stops the player before it unmounts Drive",
                    player >= 0 && drive > player, $"player at {player}, drive at {drive}");
                Check("and waits for reads of the letter to come back in between",
                    waits > player && drive > waits, $"wait at {waits}");
            }

            if (mount != null)
            {
                Check("and the provider does not disconnect while it is answering a callback",
                    mount.Contains("WaitForCallbacks(TimeSpan.FromSeconds(5));", StringComparison.Ordinal) &&
                    mount.Contains("Interlocked.Increment(ref _callbacksInFlight);", StringComparison.Ordinal));

                // A count that is taken and not given back is worse than no count
                // at all: the guard then waits out its whole limit and
                // disconnects anyway, while no longer being able to tell a real
                // read from a number left behind. It shipped that way for an
                // afternoon — the decrement had landed in the *helper* the
                // callback calls, which two of its exit paths never reach.
                foreach (var (what, from, to) in new[]
                {
                    ("the data callback", "private void OnFetchData(", "private void OnCancelFetch("),
                    ("the placeholder callback", "private void OnFetchPlaceholders(", "private void TransferPlaceholders("),
                })
                {
                    var body = Between(mount, from, to);
                    bool balanced =
                        body != null &&
                        body.Contains("Interlocked.Increment(ref _callbacksInFlight);", StringComparison.Ordinal) &&
                        body.Contains("finally", StringComparison.Ordinal) &&
                        body.LastIndexOf("Interlocked.Decrement(ref _callbacksInFlight);", StringComparison.Ordinal) >
                        body.LastIndexOf("finally", StringComparison.Ordinal);

                    Check($"{what} gives that count back in its own finally", balanced,
                        body == null ? "the method could not be found" : "increment without a finally that undoes it");
                }
            }

            // ---- the freeze in whatever you paste into ----
            //
            // Copying a Drive file put a placeholder path on the clipboard and
            // started nothing, so the first byte the other application read was
            // the first byte fetched from Google — and it reads on the thread
            // that paints its own window. Measured on a 16.8MB track: 3.9 seconds
            // frozen, against 111ms once the pool already had it, and linear in
            // the file's size from there.
            var clipboardCopy = Between(
                Source("MainForm.cs") ?? "",
                "private void CopyToClipboard(bool cut)",
                "private bool IsDirectoryInView(");

            Check("copying a Drive file starts fetching it",
                clipboardCopy != null &&
                clipboardCopy.Contains("Drive?.Warm(paths)", StringComparison.Ordinal),
                "nothing warms the file the clipboard now points at");

            // A cut pasted back inside Drive is a move, which the application
            // does as one metadata request without reading a byte. Warming it
            // would download the whole file only to throw it away.
            Check("but a cut does not, because a cut inside Drive reads nothing",
                clipboardCopy != null &&
                clipboardCopy.Contains("if (!cut) Drive?.Warm(paths);",
                    StringComparison.Ordinal));

            // Both bounds on what gets warmed, for real rather than by reading
            // the source. Three files is MaxOpenFiles; a fourth would evict the
            // first, and the survivors would be the wrong three.
            const long mb = 1024 * 1024;

            Equal("warming takes on no more files than may be open at once",
                "0,1,2", Plan(new long[] { mb, mb, mb, mb, mb }, 3, 1024 * mb));

            Equal("a file too big for the pool is skipped, not held",
                "", Plan(new long[] { 4096 * mb }, 3, 1024 * mb));

            // And skipping it does not end the plan: a huge video first in a
            // selection must not stop the tracks behind it being warmed.
            Equal("and the files behind it are still warmed",
                "1,2", Plan(new long[] { 4096 * mb, mb, mb }, 3, 1024 * mb));

            Equal("what is warmed together has to fit together",
                "0,1", Plan(new long[] { 600 * mb, 300 * mb, 300 * mb }, 3, 1024 * mb));

            Equal("a folder, which has no size, is not a thing to warm",
                "1", Plan(new long[] { 0, mb }, 3, 1024 * mb));

            // Neither bound is allowed to cost anything on the thread that took
            // the copy. Looking every selected file up is what froze the window
            // for thirteen seconds over a context menu, and a selection of three
            // thousand is an ordinary thing to press Ctrl+C on.
            var driveSource = Source("GoogleDrive.cs");
            Check("warming never runs on the thread that copied",
                driveSource != null && driveSource.Contains("Task.Run(() =>", StringComparison.Ordinal) &&
                driveSource.Contains("try { mount.Warm(snapshot); }", StringComparison.Ordinal),
                "Warm is called inline from the clipboard path");
            Check("and the selection is copied before it gets there",
                driveSource != null &&
                driveSource.Contains("var snapshot = paths.Where(p => Owns(mount, p)).ToArray();",
                    StringComparison.Ordinal),
                "the worker reads the pane's live selection");

            if (mount != null)
            {
                Check("and it stops looking once it has candidates enough",
                    mount.Contains("if (wanted.Count >= MostWarmCandidates) break;", StringComparison.Ordinal),
                    "every selected path is measured, however many there are");
            }

            if (mount != null)
            {
                // The other half of warming: a byte budget only gives memory back
                // when something else wants it, so a file warmed and then walked
                // away from sits there for as long as the mount is up.
                Check("a file nothing has read is let go on a timer",
                    mount.Contains("_sweep = new Timer(_ => SweepIdle(), null, SweepInterval, SweepInterval);",
                        StringComparison.Ordinal),
                    "the idle sweep is declared but nothing drives it");

                // Releasing a cache cancels its downloads. A sweep landing inside
                // a fetch callback would fail the read that callback is
                // answering — the copy that stops dead for no visible reason that
                // this file already carries scars about.
                var sweep = Between(mount, "private void SweepIdle()", "// ---- serving bytes");
                Check("and never out from under a reader",
                    sweep != null &&
                    sweep.Contains("if (Volatile.Read(ref _callbacksInFlight) > 0) return;",
                        StringComparison.Ordinal),
                    "the sweep can cancel a download a callback is waiting on");

                // Same reasoning as the player being stopped before the unmount:
                // a timer that can still fire is a teardown that can still start
                // while the callback wait is counting.
                int stopped = mount.IndexOf("_sweep.Dispose();", StringComparison.Ordinal);
                int waited = mount.IndexOf("WaitForCallbacks(TimeSpan.FromSeconds(5));", StringComparison.Ordinal);
                Check("the sweep is stopped before the mount waits for its callbacks",
                    stopped >= 0 && waited > stopped, $"sweep at {stopped}, wait at {waited}");
            }

            // The terminal that froze. A file manager attached to the console that
            // started it holds that console for as long as it runs.
            Check("only a command-line run borrows a console",
                program.Contains("if (IsCommandLineRun(args)) AttachToParentConsole();", StringComparison.Ordinal));
            Check("and the copy an install starts inherits nothing from the terminal",
                program.Contains("Process.Start(new ProcessStartInfo(result.ExePath) { UseShellExecute = true });",
                    StringComparison.Ordinal));
        }

        /// <summary>
        /// <see cref="DriveMount.WarmPlan"/> as a string, so the expected answer
        /// in a test reads as the list of files it is: "0,1,2".
        /// </summary>
        private static string Plan(long[] sizes, int maxOpen, long budget) =>
            string.Join(",", DriveMount.WarmPlan(sizes, maxOpen, budget));

        /// <summary>The text from <paramref name="start"/> up to <paramref name="end"/>, or null.</summary>
        private static string? Between(string text, string start, string end)
        {
            int from = text.IndexOf(start, StringComparison.Ordinal);
            if (from < 0) return null;
            int to = text.IndexOf(end, from, StringComparison.Ordinal);
            return to < 0 ? null : text[from..to];
        }

        private static string? Source(string name)
        {
            var here = new DirectoryInfo(AppContext.BaseDirectory);

            for (int up = 0; up < 8 && here != null; up++, here = here.Parent)
            {
                var candidate = Path.Combine(here.FullName, "src", name);
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
            }

            return null;
        }
    }
}
