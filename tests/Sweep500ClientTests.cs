using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of the Google Drive client.
    /// </summary>
    internal static class Sweep500ClientTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the Google Drive client:");
            await Guard("disconnect", DisconnectDoesNotWaitForUploads);
            await Guard("token offline", TokenRefreshFailuresCountAsOffline);
            await Guard("acknowledgeAbuse", RateLimitKeepsAcknowledgeAbuse);
            await Guard("offline said once", OfflineWaitSaidOncePerOutage);
            await Guard("sign-in only when asked", SignInOnlyWhenAsked);
            await Guard("other account", OtherAccountKeepsOldSavesOnPc);
            await Guard("search part of a word", SearchFindsPartOfAWord);
            await Guard("cut listing", CutListingIsNotWhole);
            await Guard("still uploading", StillUploadingFile);
            await Guard("trash current", TrashAndRestoreShowAtOnce);
            await Guard("trash size", TrashSizeCountsTrashedFolders);
        }

        private static async Task Guard(string name, Func<Task> test)
        {
            try { await test(); }
            catch (Exception ex) { Check(name + " ran to the end", false, ex.GetType().Name + ": " + ex.Message); }
        }

        private static readonly DateTime When = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        /// <summary>A mount over the fake Drive at "root", no letter, handed to a GoogleDrive.</summary>
        private sealed class Rig : IDisposable
        {
            public readonly FakeDrive Fake = new();
            public readonly DriveClient Client;
            public readonly GoogleDrive Drive = new();
            public readonly DriveMount Mount;
            public readonly string Root;
            public readonly List<string> Said = new();

            public Rig()
            {
                Client = new DriveClient(Fake, 0.0005);
                Root = Path.Combine(Path.GetTempPath(), "en-s500c-" + Guid.NewGuid().ToString("N")[..8]);
                Drive.Notification += (id, message) => { lock (Said) Said.Add(id + ": " + message); };
                Mount = new DriveMount(Client, Root, 16L * 1024 * 1024, _ => { }) { UnsavedFolder = Root + "-unsaved" };
                Mount.Start("root", assignLetter: false);
                Drive.UseForTests(Mount, Client);
            }

            public string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

            public IReadOnlyList<DrivePlaced>? Listed(string path)
            {
                Mount.Populate(path);
                return Mount.TryListing(path, out var entries) ? entries : null;
            }

            public string SaidText() { lock (Said) return string.Join(" | ", Said); }

            public void Dispose()
            {
                // The GoogleDrive takes down the mount it was handed (or waits for the one it let go).
                try { Drive.Dispose(); } catch { }
                try { Directory.Delete(Root, true); } catch { }
                try { Directory.Delete(Root + "-unsaved", true); } catch { }
            }
        }

        private static string Names(IReadOnlyList<DrivePlaced>? placed) =>
            placed == null ? "(not listed)" : string.Join(",", placed.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        private static string SourceText(string name)
        {
            var at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at != null && !Directory.Exists(Path.Combine(at.FullName, "src"))) at = at.Parent;
            if (at == null) return "";
            var path = Path.Combine(at.FullName, "src", name);
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch { return ""; }
        }

        /// <summary>The body of a method in a source file, from its name to the next method at the same depth (roughly).</summary>
        private static string MethodText(string source, string signature, int length = 12000)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            return at < 0 ? "" : source.Substring(at, Math.Min(length, source.Length - at));
        }

        // ---- 1. Disconnect and switching Drive off return at once while uploads drain ----

        private static async Task DisconnectDoesNotWaitForUploads()
        {
            var drainWas = DriveMount.DrainFor;
            DriveMount.DrainFor = TimeSpan.FromSeconds(8);
            using var rig = new Rig();
            try
            {
                rig.Fake.AnswerDelayMs = 3000;
                rig.Mount.NewPending("", "late.txt", "root");
                await Task.Delay(100);

                var clock = Stopwatch.StartNew();
                rig.Drive.Unmount();
                long took = clock.ElapsedMilliseconds;
                var names = rig.Drive.UploadNames;
                Check("Disconnect returns at once while a saved file is still going up", took < 1000, took + " ms");
                Check("and the question before quitting still sees the file it is sending",
                    names.Contains("late.txt"), string.Join(",", names));

                var finished = rig.Drive.TeardownForTests;
                bool done = await Task.WhenAny(finished, Task.Delay(30_000)) == finished;
                Check("the disconnected mount finishes on a worker", done);
                Check("and the file still reaches Google Drive", rig.Fake.Find(FakeDrive.Root, "late.txt") != null,
                    rig.SaidText());
                Check("and Google Drive disconnected is said once it is gone",
                    rig.SaidText().Contains("drive.unmounted"), rig.SaidText());

                var fresh = MethodText(SourceText("GoogleDrive.cs"), "private async Task<bool> MountFresh(");
                int wait = fresh.IndexOf("Teardown.WaitAsync(", StringComparison.Ordinal);
                int start = fresh.IndexOf("mount.Start(", StringComparison.Ordinal);
                Check("a new connect waits for a disconnected mount to go before standing its volume up",
                    wait > 0 && start > wait);
            }
            finally { DriveMount.DrainFor = drainWas; }
        }

        // ---- 2. A token refresh that cannot reach Google counts toward offline ----

        private static Func<CancellationToken, Task<string>> TokenFailingFirst(int failures)
        {
            int calls = 0;
            return _ =>
            {
                if (Interlocked.Increment(ref calls) <= failures)
                    throw new System.Net.Http.HttpRequestException("No such host is known",
                        new System.Net.Sockets.SocketException(11001));
                return Task.FromResult("fake-drive-token");
            };
        }

        private static async Task TokenRefreshFailuresCountAsOffline()
        {
            var fake = new FakeDrive();
            var down = new DriveClient(fake, TokenFailingFirst(int.MaxValue), 0.0005);
            for (int i = 0; i < 3; i++)
                try { await down.Whoami(CancellationToken.None); } catch { }
            Check("three token refreshes that cannot reach Google make Drive read as offline",
                down.Health.IsOffline, down.Health.State.ToString());

            var busy = new DriveClient(fake, _ => throw new GoogleAuth.SignInBusyException(), 0.0005);
            for (int i = 0; i < 3; i++)
                try { await busy.Whoami(CancellationToken.None); } catch { }
            Check("but waiting behind somebody signing in is not the network", !busy.Health.IsOffline,
                busy.Health.State.ToString());

            // A wake with no network: the folder create and the small upload wait
            // for it instead of asking whether something never sent had landed.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var folders = new DriveClient(fake, TokenFailingFirst(6), 0.0005);
            int creates = fake.Creates;
            string? made = null, error = null;
            try { made = await folders.CreateFolder("Woken", "root", cts.Token); }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            Check("a folder made while the token cannot be refreshed waits for the network and is made once",
                made != null && fake.Creates == creates + 1 && fake.Count(FakeDrive.Root, "Woken") == 1,
                error ?? $"{fake.Creates - creates} creates");
            Check("and Drive is back online afterwards", folders.Health.State == DriveState.Online,
                folders.Health.State.ToString());

            var uploads = new DriveClient(fake, TokenFailingFirst(6), 0.0005);
            var bytes = new byte[] { 1, 2, 3 };
            error = null;
            DriveClient.DriveUploaded? up = null;
            try { up = await uploads.UploadFromMemory(bytes, 3, "woken.txt", "root", When, null, cts.Token); }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            Check("a small file sent while the token cannot be refreshed waits and lands once",
                up != null && fake.Count(FakeDrive.Root, "woken.txt") == 1, error);
        }

        // ---- 3. A rate limit on a read does not take acknowledgeAbuse away ----

        private static async Task RateLimitKeepsAcknowledgeAbuse()
        {
            foreach (var reason in new[] { "userRateLimitExceeded", "rateLimitExceeded", "downloadQuotaExceeded" })
            {
                var id = "s500-ack-" + reason;
                var body = FakeDrive.ErrorBody(403, reason, "Slow down.");
                Check($"a 403 {reason} is not Google refusing acknowledgeAbuse",
                    !DriveClient.RefusedAcknowledge(id, 403, DriveClient.MediaUrl(id), body));
                Check($"and the file is still asked with the flag after a {reason}",
                    DriveClient.MediaUrl(id).EndsWith("&acknowledgeAbuse=true", StringComparison.Ordinal),
                    DriveClient.MediaUrl(id));
            }

            var fake = new FakeDrive();
            var client = new DriveClient(fake, 0.0005);
            var data = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
            var file = fake.AddFile("big.7z", data, When, FakeDrive.Root);
            fake.AddFault(new FakeDrive.Fault
            {
                Kind = "media", Times = 1, Status = 403,
                Body = FakeDrive.ErrorBody(403, "userRateLimitExceeded", "User rate limit exceeded."),
                Subject = s => s == file,
            });
            byte[]? got = null;
            string? error = null;
            try { got = await client.ReadRange(file, 8, 16, CancellationToken.None); }
            catch (Exception ex) { error = ex.Message; }
            Check("a read refused once for a rate limit arrives on the retry",
                got != null && got.SequenceEqual(data.Skip(8).Take(16)), error);
            Check("and the file keeps acknowledgeAbuse afterwards",
                DriveClient.MediaUrl(file).EndsWith("&acknowledgeAbuse=true", StringComparison.Ordinal),
                DriveClient.MediaUrl(file));
        }

        // ---- 5, 7, 8. Google's sign-in page opens only when somebody asked to connect ----

        /// <summary>A scratch settings folder with a client file and no saved sign-in; the browser is never opened.</summary>
        private static async Task WithNoSignIn(Func<GoogleDrive, System.Collections.Concurrent.ConcurrentQueue<string>,
            List<string>, Task> body)
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-s500-signin-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, GoogleAuth.ClientFileName),
                "{\"installed\":{\"client_id\":\"s500.apps.googleusercontent.com\",\"client_secret\":\"s500-secret\"}}");
            var opened = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var was = Settings.OverrideAppDataDir;
            Settings.OverrideAppDataDir = dir;
            GoogleAuth.OpenBrowserForTests = url => opened.Enqueue(url);
            var drive = new GoogleDrive();
            var said = new List<string>();
            drive.Notification += (id, message) => { lock (said) said.Add(id); };
            try { await body(drive, opened, said); }
            finally
            {
                try { drive.Dispose(); } catch { }
                GoogleAuth.OpenBrowserForTests = null;
                Settings.OverrideAppDataDir = was;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static async Task<bool> Until(Func<bool> done, int ms)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < ms) await Task.Delay(20);
            return done();
        }

        private static async Task SignInOnlyWhenAsked()
        {
            // 8: an unfinished browser sign-in is never retried on its own.
            Check("an unanswered browser sign-in is not retried",
                !GoogleDrive.RetryableConnectFailure(new TimeoutException(), false, true, false));
            Check("nor a code exchange that timed out after the browser",
                !GoogleDrive.RetryableConnectFailure(new TaskCanceledException("t", new TimeoutException()), false, true, false));
            Check("a network failure with no browser opened is retried",
                GoogleDrive.RetryableConnectFailure(new System.Net.Http.HttpRequestException("x"), false, false, false));
            Check("and so is one after a finished sign-in",
                GoogleDrive.RetryableConnectFailure(new TimeoutException(), false, true, true));
            var drive = SourceText("GoogleDrive.cs");
            var fresh = MethodText(drive, "private async Task<bool> MountFresh(");
            Check("a failed connect decides on retrying with whether the browser sign-in was left unfinished",
                fresh.Contains("RetryableConnectFailure(", StringComparison.Ordinal) &&
                fresh.Contains("ConsentStarted", StringComparison.Ordinal));

            // 7: start-up and retries never open the browser.
            var tray = SourceText("TrayApplicationContext.cs");
            Check("the start-up mount may not open Google's sign-in page",
                tray.Contains("if (_settings.GoogleDriveEnabled) MountGoogleDrive(mayConsent: false);", StringComparison.Ordinal));
            Check("nor may a retry after a network failure",
                System.Text.RegularExpressions.Regex.IsMatch(tray, @"_drive\.Mount\(_settings, quietly: true, mayConsent: false\)"));
            var form = SourceText("SettingsForm.cs");
            var signOut = MethodText(form, "// Switched off and saved now too, so Cancel or Escape cannot undo", 600);
            Check("Sign out switches Drive off and saves it at once, so Cancel cannot leave it on",
                signOut.Contains("DisconnectDrive?.Invoke(", StringComparison.Ordinal));

            if (!WinFsp.TryLoad(out var problem))
            {
                Console.WriteLine("  (sign-in mounts skipped: WinFsp is not here: " + problem + ")");
                return;
            }

            await WithNoSignIn(async (gd, opened, said) =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var clock = Stopwatch.StartNew();
                bool ok = await gd.Mount(new Settings(), cts.Token, mayConsent: false);
                Check("a start-up mount with no saved sign-in gives up at once without a browser",
                    !ok && opened.IsEmpty && clock.ElapsedMilliseconds < 15_000, $"{opened.Count} opened, {clock.ElapsedMilliseconds} ms");
                Check("and says the sign-in is needed, not that it keeps trying", gd.NeedsSignIn && !gd.FailedOnNetwork);
                lock (said) Check("and never says it is waiting for the browser", !said.Contains("drive.signin.opened"),
                    string.Join(",", said));
            });

            // 5: Connect pressed again while the first sign-in waits in the browser.
            await WithNoSignIn(async (gd, opened, said) =>
            {
                using var first = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var second = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var one = Task.Run(() => gd.Mount(new Settings(), first.Token));
                bool firstOpened = await Until(() => opened.Count >= 1, 10_000);
                Check("a connect with no sign-in opens Google's page (in the test, the hook)", firstOpened);
                var two = Task.Run(() => gd.Mount(new Settings(), second.Token));
                bool secondOpened = await Until(() => opened.Count >= 2, 5_000);
                Check("pressing Connect again opens a fresh sign-in at once instead of queueing behind the first",
                    secondOpened, opened.Count + " opened");
                bool oneDone = await Task.WhenAny(one, Task.Delay(5_000)) == one;
                Check("and the first connect is called off", oneDone && !one.Result);
                second.Cancel();
                first.Cancel();
                try { await Task.WhenAny(two, Task.Delay(10_000)); } catch { }
            });
        }

        // ---- 6. Signing in again as another account never sends the old account's saves ----

        private static async Task OtherAccountKeepsOldSavesOnPc()
        {
            // A retired sign-in never reads the token file, which may hold the new account's sign-in.
            var tokenPath = Path.Combine(Path.GetTempPath(), "en-s500-retired-" + Guid.NewGuid().ToString("N")[..8] + ".dat");
            File.WriteAllText(tokenPath, "not to be read");
            try
            {
                var auth = new GoogleAuth("id.apps.googleusercontent.com", "secret", tokenPath) { AllowConsent = false };
                bool lapsed = false;
                auth.SignInLapsed += () => lapsed = true;
                auth.Retire();
                Exception? thrown = null;
                try { await auth.AccessToken(CancellationToken.None); }
                catch (Exception ex) { thrown = ex; }
                Check("a retired sign-in refuses every request as not signed in",
                    thrown is GoogleSignInRequiredException, thrown?.GetType().Name);
                Check("and does not say the sign-in expired", !lapsed);
            }
            finally { try { File.Delete(tokenPath); } catch { } }

            using var rig = new Rig();
            rig.Fake.AddFault(new FakeDrive.Fault { Kind = "multipart", Times = 100, Status = 503 });
            rig.Mount.NewPending("", "note.txt", "root");
            int uploads = rig.Fake.Uploads, requests = rig.Fake.Requests;
            rig.Mount.AccountGone = true;
            var clock = Stopwatch.StartNew();
            rig.Drive.Unmount();
            var finished = rig.Drive.TeardownForTests;
            bool done = await Task.WhenAny(finished, Task.Delay(20_000)) == finished;
            Check("a mount whose account was replaced goes without draining", done && clock.ElapsedMilliseconds < 15_000,
                clock.ElapsedMilliseconds + " ms");
            Check("and sends nothing it held", rig.Fake.Uploads == uploads && rig.Fake.Find(FakeDrive.Root, "note.txt") == null,
                $"{rig.Fake.Uploads - uploads} uploads, {rig.Fake.Requests - requests} requests");
            Check("its unsent file is kept on this PC", File.Exists(Path.Combine(rig.Root + "-unsaved", "note.txt")));
            Check("and that is said", rig.SaidText().Contains("different account", StringComparison.Ordinal), rig.SaidText());

            var drive = SourceText("GoogleDrive.cs");
            var mount = MethodText(drive, "public async Task<bool> Mount(", 6000);
            int retire = mount.IndexOf(".Retire()", StringComparison.Ordinal);
            int gone = mount.IndexOf("AccountGone = true", StringComparison.Ordinal);
            int tear = mount.IndexOf("TearDown();", StringComparison.Ordinal);
            Check("another account: the old sign-in is retired and the mount told before it is taken down",
                retire > 0 && gone > 0 && tear > retire && tear > gone);
            var renew = MethodText(drive, "RenewSignIn(CancellationToken token,", 4000);
            Check("a renewed sign-in goes through a scratch sign-in, not the live client",
                renew.Contains("new DriveClient(scratch)", StringComparison.Ordinal) &&
                !renew.Contains("_client", StringComparison.Ordinal));
        }

        // ---- 9. A Drive search finds part of a word, as a PC folder's does ----

        private static async Task SearchFindsPartOfAWord()
        {
            using var rig = new Rig();
            rig.Fake.NameSearchLikeGoogle = true;
            var music = rig.Fake.AddFolder("Music");
            rig.Fake.AddFile("Song (Remix).flac", new byte[] { 1 }, When, music);
            rig.Fake.AddFile("HelloWorld.txt", new byte[] { 2 }, When, FakeDrive.Root);
            rig.Fake.AddFile("Beatles.flac", new byte[] { 3 }, When, music);
            rig.Mount.Forget(rig.Root);
            rig.Listed(rig.Root);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            async Task<string> Search(string term)
            {
                var result = await rig.Drive.SearchAsync(rig.Root, term, 100, cts.Token);
                return result == null ? "(no search)" : string.Join(",", result.Hits.Select(h => h.Name).OrderBy(n => n));
            }

            Equal("before the size index is built, Google's own search still finds the start of a word",
                "Song (Remix).flac", await Search("rem"));

            var size = await rig.Drive.FolderSizeAsync(rig.Root, cts.Token);
            Check("the size index is built for the drive", size != null);
            Equal("\"mix\" finds Song (Remix).flac", "Song (Remix).flac", await Search("mix"));
            Equal("\"World\" finds HelloWorld.txt", "HelloWorld.txt", await Search("World"));
            Equal("\"tles\" finds Beatles.flac", "Beatles.flac", await Search("tles"));

            // The saved index carries the names, so a search after a restart is as good.
            var items = new[] { new DriveSizeItem("a", "root", 5, false, true, "Song (Remix).flac", When.Ticks) };
            var back = DriveSizeIndex.Decode(DriveSizeIndex.Encode(null, "root", "7", items), null).Items;
            Check("a saved size index keeps names and dates",
                back.Count == 1 && back[0].Name == "Song (Remix).flac" && back[0].ModifiedTicks == When.Ticks);
        }

        // ---- 10. A listing cut at its limit is never taken for the whole folder ----

        private static async Task CutListingIsNotWhole()
        {
            var fake = new FakeDrive();
            var client = new DriveClient(fake, 0.0005);
            var folder = fake.AddFolder("Big");
            for (int i = 0; i < 12; i++) fake.AddFile($"f{i:00}.bin", new byte[] { 1 }, When, folder);

            Exception? thrown = null;
            try { await client.ListChildren(folder, CancellationToken.None, max: 10); }
            catch (Exception ex) { thrown = ex; }
            Check("a folder holding more than the limit fails rather than coming back short",
                thrown is DriveClient.ListingCutException, thrown?.GetType().Name ?? "returned");
            var part = await client.ListChildren(folder, CancellationToken.None, max: 10, partial: true);
            Check("a caller that asks for part of it gets part of it", part.Count == 10, part.Count.ToString());
            var exact = await client.ListChildren(folder, CancellationToken.None, max: 12);
            Check("a folder holding exactly the limit lists whole", exact.Count == 12, exact.Count.ToString());

            // Everything that copies, measures or checks a whole folder asks for all of it.
            var drive = SourceText("GoogleDrive.cs");
            var calls = System.Text.RegularExpressions.Regex.Matches(drive, @"ListChildren\([^;]*;")
                .Select(m => m.Value).ToList();
            var capped = calls.Where(c => !c.Contains("max:", StringComparison.Ordinal)).ToList();
            Check("every folder listing in GoogleDrive says how much of the folder it needs",
                calls.Count >= 6 && capped.Count == 0, string.Join(" | ", capped));
        }

        // ---- 11. A file still on its way up can be deleted, and rename and move say why they wait ----

        private static async Task StillUploadingFile()
        {
            using var rig = new Rig();
            var music = rig.Fake.AddFolder("Music");
            rig.Mount.Forget(rig.Root);
            rig.Listed(rig.Root);
            rig.Listed(rig.P("Music"));
            var take = rig.P("Music", "take.wav");
            rig.Mount.NewPending("music", "take.wav", music);
            Check("a file saved a moment ago is on its way up", rig.Mount.IsNewNotLanded(take, out _));

            var renamed = await rig.Drive.RenameOnDrive(take, "other.wav");
            Check("rename says it is still being saved, not that Drive has no such file",
                renamed == null && rig.SaidText().Contains("still being saved", StringComparison.Ordinal) &&
                !rig.SaidText().Contains("does not have a file", StringComparison.Ordinal), rig.SaidText());
            string? moveRefusal = null;
            try { await rig.Drive.MoveOnDrive(take, rig.Root); }
            catch (InvalidOperationException ex) { moveRefusal = ex.Message; }
            Check("so does a move", moveRefusal?.Contains("still being saved", StringComparison.Ordinal) == true, moveRefusal);

            bool deleted = await rig.Drive.TrashPath(take);
            Check("Delete on a file still going up deletes it", deleted, rig.SaidText());
            Check("and it leaves the folder", !Names(rig.Mount.TryListing(rig.P("Music"), out var rows) ? rows : null)
                .Split(',').Contains("take.wav"));
            Check("and it is no longer waiting to be sent", rig.Mount.PendingAt("music\\take.wav") == null);
            await Task.Delay(DriveMount.Settle + TimeSpan.FromSeconds(1));
            Check("and it never lands on Google Drive", rig.Fake.Find(music, "take.wav") == null);

            var open = rig.Mount.NewPending("music", "open.wav", music)!;
            lock (open.Gate) open.Handles = 1;
            bool refused = !await rig.Drive.TrashPath(rig.P("Music", "open.wav"));
            Check("a file another program still has open is not deleted, and that is said",
                refused && rig.SaidText().Contains("another program is still saving it", StringComparison.Ordinal) &&
                rig.Mount.PendingAt("music\\open.wav") != null, rig.SaidText());
            lock (open.Gate) open.Handles = 0;
        }

        // ---- 12. Trash and the folder a file goes back to are current without F5 ----

        private static async Task TrashAndRestoreShowAtOnce()
        {
            using var rig = new Rig();
            var music = rig.Fake.AddFolder("Music");
            var song = rig.Fake.AddFile("song.flac", new byte[] { 3, 4, 5 }, When, music);
            var keep = rig.Fake.AddFile("keep.flac", new byte[] { 1, 2 }, When, music);
            rig.Fake.Trash(song);
            rig.Mount.Forget(rig.Root);
            rig.Listed(rig.Root);
            var trash = rig.P("Trash");
            Equal("Trash lists what was put in it", "song.flac", Names(rig.Listed(trash)));
            Equal("and Music what is left", "keep.flac", Names(rig.Listed(rig.P("Music"))));

            bool deleted = await rig.Drive.TrashPath(rig.P("Music", "keep.flac"));
            Check("a file is deleted from Music", deleted && rig.Fake.Get(keep) is { Trashed: true });
            Equal("and Trash, already listed, shows it at once", "keep.flac,song.flac", Names(rig.Listed(trash)));

            bool restored = await rig.Drive.RestoreFromTrash(rig.P("Trash", "song.flac"));
            Check("a file is restored", restored && rig.Fake.Get(song) is { Trashed: false });
            Equal("and Music, already listed, shows it back at once", "song.flac", Names(rig.Listed(rig.P("Music"))));
        }

        // ---- 13. The size of Trash, or of a folder in it, counts what is inside trashed folders ----

        private static async Task TrashSizeCountsTrashedFolders()
        {
            using var rig = new Rig();
            var photos = rig.Fake.AddFolder("Old Photos");
            rig.Fake.AddFile("a.jpg", new byte[3], When, photos);
            rig.Fake.AddFile("b.jpg", new byte[5], When, photos);
            var loose = rig.Fake.AddFile("loose.txt", new byte[7], When, FakeDrive.Root);
            rig.Fake.Trash(photos);
            rig.Fake.Trash(loose);
            rig.Mount.Forget(rig.Root);
            rig.Listed(rig.Root);
            rig.Listed(rig.P("Trash"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var folder = await rig.Drive.MeasureOnDrive(new[] { rig.P("Trash", "Old Photos") }, null, cts.Token);
            Equal("a trashed folder measures what is in it", "2 files, 8 bytes", $"{folder.Files} files, {folder.Bytes} bytes");
            var all = await rig.Drive.MeasureOnDrive(new[] { rig.P("Trash") }, null, cts.Token);
            Equal("and Trash measures everything in it", "3 files, 15 bytes", $"{all.Files} files, {all.Bytes} bytes");
        }

        // ---- 4. Waiting for the internet is said once per outage ----

        private static async Task OfflineWaitSaidOncePerOutage()
        {
            var fake = new FakeDrive();
            bool down = false;
            var client = new DriveClient(fake, _ => Volatile.Read(ref down)
                ? throw new System.Net.Http.HttpRequestException("down", new System.Net.Sockets.SocketException(10051))
                : Task.FromResult("fake-drive-token"), 0.0005);
            var said = new System.Collections.Concurrent.ConcurrentQueue<string>();
            client.Notify = (id, _) => said.Enqueue(id);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            async Task Outage(int files, string prefix)
            {
                Volatile.Write(ref down, true);
                for (int i = 0; i < 3; i++) client.Health.Failed(new System.Net.Sockets.SocketException(10051));
                var all = Enumerable.Range(0, files)
                    .Select(i => client.CreateFolder($"{prefix}{i}", "root", cts.Token)).ToArray();
                await Task.Delay(150);
                Volatile.Write(ref down, false);
                try { await Task.WhenAll(all); } catch { }
            }

            await Outage(8, "first");
            int waiting = said.Count(s => s == "drive.offline.waiting"), back = said.Count(s => s == "drive.offline.back");
            Check("eight requests waiting out one outage say they are waiting once", waiting == 1, waiting + " times");
            Check("and say they are back once", back == 1, back + " times");
            Check("and every one of them still goes through",
                Enumerable.Range(0, 8).All(i => fake.Count(FakeDrive.Root, "first" + i) == 1));

            await Outage(4, "second");
            waiting = said.Count(s => s == "drive.offline.waiting");
            back = said.Count(s => s == "drive.offline.back");
            Check("a second outage is said again", waiting == 2 && back == 2, $"{waiting} waiting, {back} back");

            var example = Notifications.All.FirstOrDefault(n => n.Id == "drive.offline.waiting");
            Check("the catalogue's example names no one file",
                example != null && example.Preview() ==
                "Waiting for the internet to come back before sending anything more to Google Drive",
                example?.Preview());
        }
    }
}
