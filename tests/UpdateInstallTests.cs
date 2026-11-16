using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace ExplorerNative
{
    /// <summary>
    /// The updater, the command line, the encrypted logs and the single-instance
    /// handoff: the regressions from the review of the update and startup code.
    /// GitHub is never asked; a fake answers instead.
    /// </summary>
    internal static class UpdateFixTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Updates and the command line:");

            ArgumentTests();
            ReleaseNoteTests();
            await CheckTests();
            MarkerTests();
            LogRaceTests();
            await HandoffBurstTests();

            Console.WriteLine();
        }

        private static void ArgumentTests()
        {
            Equal("an unknown switch is caught", "--bogus", LaunchArguments.UnknownOption(new[] { "--bogus" }) ?? "(none)");
            Equal("and so is a typo of a real one", "--unistall",
                LaunchArguments.UnknownOption(new[] { "--unistall" }) ?? "(none)");
            Check("every real switch is known, in any case",
                LaunchArguments.CommandLineSwitches.All(s => LaunchArguments.UnknownOption(new[] { s.ToUpperInvariant() }) == null));
            Check("--quiet and --restart are known",
                LaunchArguments.UnknownOption(new[] { "--restart", "--quiet" }) == null);
            Check("a folder is not an option",
                LaunchArguments.UnknownOption(new[] { @"C:\Users", "D:\"" }) == null);

            Equal("--read-log --quiet reads install.log", "install.log",
                LaunchArguments.ReadLogName(new[] { "--read-log", "--quiet" }));
            Equal("a name after a switch is still found", "crash.log",
                LaunchArguments.ReadLogName(new[] { "--read-log", "--quiet", "crash.log" }));
            Equal("and a name straight after it", "crash.log",
                LaunchArguments.ReadLogName(new[] { "--quiet", "--READ-LOG", "crash.log" }));
        }

        private static void ReleaseNoteTests()
        {
            string One(string markdown) => string.Join("|", UpdateForm.Changes(markdown));

            Equal("a link with parentheses in its address", "x", One("[x](u/(y))"));
            Equal("a link inside a sentence", "see the notes here", One("see [the notes](https://a/b_(c)) here"));
            Equal("__init__.py keeps its underscores", "fixed __init__.py", One("- fixed __init__.py"));
            Equal("snake__case keeps them too", "a snake__case__name", One("a snake__case__name"));
            Equal("but __bold__ words lose them", "a bold word.", One("a __bold__ word."));
            Equal("escaped asterisks are asterisks", "*e*", One(@"\*e\*"));
            Equal("an escaped bracket is a bracket", "[not a link]", One(@"\[not a link\]"));
            Equal("an empty task box goes", "task", One("- [ ] task"));
            Equal("and a ticked one", "done", One("* [x] done"));
            Equal("code keeps what is inside it", @"a\*b and **c**", One(@"`a\*b` and `**c**`"));
            Equal("the old cases still hold",
                "Faster C# build, fixes track #3|code stays readable",
                One("## What's Changed\n* **Faster** C# build, fixes track #3\n- `code` stays readable\n" +
                    "**Full Changelog**: https://example/compare\n"));
        }

        /// <summary>Answers each request with whatever the test says.</summary>
        private sealed class FakeGitHub : HttpMessageHandler
        {
            public Func<HttpRequestMessage, HttpResponseMessage> Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
            public readonly List<string> Asked = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
            {
                lock (Asked) Asked.Add(request.RequestUri!.AbsoluteUri);
                return Task.FromResult(Answer(request));
            }
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static async Task<Exception?> Fails(Func<Task> what)
        {
            try { await what(); return null; }
            catch (Exception ex) { return ex; }
        }

        private static async Task CheckTests()
        {
            var fake = new FakeGitHub();
            Updater.UseHandlerForTests(fake);
            try
            {
                var asset = Updater.AssetName;

                // Rate limited: a 403 with the limit spent, and a 429.
                fake.Answer = _ =>
                {
                    var r = Json("{\"message\":\"API rate limit exceeded\"}", HttpStatusCode.Forbidden);
                    r.Headers.Add("x-ratelimit-remaining", "0");
                    return r;
                };
                var limited = await Fails(() => Updater.CheckAsync());
                Check("a 403 from the rate limit says so",
                    limited is Updater.UpdateException { Problem: Updater.Problem.RateLimited } &&
                    limited.Message == Updater.RateLimitedMessage, limited?.Message);
                Check("and is not reported as GitHub being unreachable", limited is not HttpRequestException);

                fake.Answer = _ => new HttpResponseMessage((HttpStatusCode)429);
                var tooMany = await Fails(() => Updater.CheckAsync());
                Check("so does a 429", tooMany is Updater.UpdateException { Problem: Updater.Problem.RateLimited });

                // A 404 is not "you have the latest version".
                fake.Answer = _ => Json("{\"message\":\"Not Found\"}", HttpStatusCode.NotFound);
                var missing = await Fails(() => Updater.CheckAsync());
                Check("a 404 says the release list could not be found",
                    missing is Updater.UpdateException { Problem: Updater.Problem.NotFound } &&
                    missing.Message.Contains("list of Explorer Native releases", StringComparison.Ordinal),
                    missing?.Message ?? "(returned instead of throwing)");

                // A release without a size.
                fake.Answer = _ => Json(
                    "{\"tag_name\":\"v99.0.0\",\"body\":\"\",\"assets\":[{\"name\":\"" + asset +
                    "\",\"browser_download_url\":\"https://example/x.exe\"}]}");
                var noSize = await Fails(() => Updater.CheckAsync());
                Check("a release with no size gives a sentence, not a dictionary error",
                    noSize is Updater.UpdateException { Problem: Updater.Problem.BadAnswer } &&
                    !noSize.Message.Contains("given key", StringComparison.Ordinal) &&
                    noSize.Message.Contains("99.0.0", StringComparison.Ordinal), noSize?.Message);

                // An answer that is not the shape of a release at all.
                fake.Answer = _ => Json("{\"unexpected\":true}");
                var shapeless = await Fails(() => Updater.CheckAsync());
                Check("an answer of the wrong shape is reported clearly",
                    shapeless is Updater.UpdateException { Problem: Updater.Problem.BadAnswer } &&
                    !shapeless.Message.Contains("given key", StringComparison.Ordinal), shapeless?.Message);

                fake.Answer = _ => Json("not json");
                Check("and so is one that is not JSON",
                    await Fails(() => Updater.CheckAsync()) is Updater.UpdateException { Problem: Updater.Problem.BadAnswer });

                // The ordinary answers still work.
                fake.Answer = _ => Json(
                    "{\"tag_name\":\"v99.0.0\",\"body\":\"* one\",\"assets\":[{\"name\":\"" + asset +
                    "\",\"browser_download_url\":\"https://example/x.exe\",\"size\":1234,\"digest\":\"sha256:ab\"}]}");
                var newer = await Updater.CheckAsync();
                Check("a newer release is found",
                    newer != null && newer.Size == 1234 && newer.Sha256 == "ab" && newer.DownloadUrl == "https://example/x.exe");

                Check("with nothing else known, it is one version behind",
                    newer != null && newer.Behind.Count == 1 && newer.Behind[0].Version == newer.Version &&
                    UpdateForm.Sentence(newer).StartsWith("You're 1 version out of date. ", StringComparison.Ordinal),
                    newer == null ? null : UpdateForm.Sentence(newer));

                // Several versions behind (6.2.1): counted, and each one's
                // changes listed under its own heading, newest first.
                var latestJson =
                    "{\"tag_name\":\"v99.0.0\",\"body\":\"* one\",\"assets\":[{\"name\":\"" + asset +
                    "\",\"browser_download_url\":\"https://example/x.exe\",\"size\":1234,\"digest\":\"sha256:ab\"}]}";
                fake.Answer = r => r.RequestUri!.AbsoluteUri.Contains("/releases?", StringComparison.Ordinal)
                    ? Json("[{\"tag_name\":\"v98.5.0\",\"body\":\"New: middle\"}," +
                           "{\"tag_name\":\"v99.0.0\",\"body\":\"* one\"}," +
                           "{\"tag_name\":\"v98.0.0\",\"body\":\"Bug fixed: oldest\\nNew: second line\"}," +
                           "{\"tag_name\":\"v98.9.0\",\"body\":\"draft\",\"draft\":true}," +
                           "{\"tag_name\":\"v0.0.1\",\"body\":\"long ago\"}," +
                           "{\"tag_name\":\"nightly\",\"body\":\"not a version\"}]")
                    : Json(latestJson);
                var far = await Updater.CheckAsync();
                Check("every version missed is counted, and only those",
                    far != null && string.Join(",", far.Behind.Select(b => Updater.Text(b.Version))) == "99.0.0,98.5.0,98.0.0",
                    far == null ? null : string.Join(",", far.Behind.Select(b => Updater.Text(b.Version))));
                if (far != null)
                {
                    Check("the question says how many",
                        UpdateForm.Sentence(far).StartsWith("You're 3 versions out of date. Version 99.0.0 is available.", StringComparison.Ordinal),
                        UpdateForm.Sentence(far));
                    Equal("each version's changes sit under its own heading",
                        "Version 99.0.0|one|Version 98.5.0|New: middle|Version 98.0.0|Bug fixed: oldest|New: second line",
                        string.Join("|", UpdateForm.Rows(far)));
                    Check("the headings are ones Ctrl+arrows stop on",
                        UpdateForm.Rows(far).Count(Changelog.IsHeading) == 3);
                }

                // The list of releases failing takes nothing away from the update.
                fake.Answer = r => r.RequestUri!.AbsoluteUri.Contains("/releases?", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                    : Json(latestJson);
                var alone = await Updater.CheckAsync();
                Check("an unreadable release list still offers the update", alone != null && alone.Behind.Count == 1);

                fake.Answer = _ => Json("{\"tag_name\":\"v0.0.1\",\"assets\":[]}");
                Check("an older one is not offered", await Updater.CheckAsync() == null);

                fake.Answer = _ => Json("{\"tag_name\":\"v99.0.0\",\"assets\":[{\"name\":\"other.exe\",\"size\":1}]}");
                var noAsset = await Fails(() => Updater.CheckAsync());
                Check("a release with nothing for this computer still says so",
                    noAsset is Updater.UpdateException && noAsset.Message.Contains(asset, StringComparison.Ordinal),
                    noAsset?.Message);

                await CompressedTests(fake, asset);

                fake.Answer = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                var down = await Fails(() => Updater.CheckAsync());
                Check("a server error names its status",
                    down is Updater.UpdateException { Problem: Updater.Problem.Refused } &&
                    down.Message.Contains("503", StringComparison.Ordinal), down?.Message);
            }
            finally
            {
                Updater.UseHandlerForTests(null);
            }
        }

        /// <summary>
        /// The gzip copy (4.2.3): found beside the executable, downloaded instead
        /// of it, unpacked and checked against the executable's own size and
        /// hash; a broken or missing one falls back to the full download.
        /// </summary>
        private static async Task CompressedTests(FakeGitHub fake, string asset)
        {
            var exe = new byte[300_000];
            new Random(7).NextBytes(exe.AsSpan(0, 1000));
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(exe));
            byte[] packed;
            using (var ms = new MemoryStream())
            {
                using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
                    gz.Write(exe);
                packed = ms.ToArray();
            }

            string Listing(long packedSize) =>
                "{\"tag_name\":\"v99.0.0\",\"body\":\"\",\"assets\":[" +
                "{\"name\":\"" + asset + "\",\"browser_download_url\":\"https://example/x.exe\",\"size\":" + exe.Length +
                ",\"digest\":\"sha256:" + sha + "\"}," +
                "{\"name\":\"" + asset + ".gz\",\"browser_download_url\":\"https://example/x.exe.gz\",\"size\":" + packedSize + "}]}";

            var dir = Path.Combine(Path.GetTempPath(), "en-update-gz-" + Guid.NewGuid().ToString("N"));
            var before = Updater.DirectoryOverride;
            Updater.DirectoryOverride = dir;
            try
            {
                byte[] servedGz = packed;
                fake.Answer = r => r.RequestUri!.AbsoluteUri switch
                {
                    "https://example/x.exe.gz" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(servedGz) },
                    "https://example/x.exe" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(exe) },
                    _ => Json(Listing(packed.Length)),
                };

                var release = await Updater.CheckAsync();
                Check("the compressed copy is found beside the executable",
                    release?.CompressedUrl == "https://example/x.exe.gz" && release.CompressedSize == packed.Length,
                    release?.CompressedUrl);
                if (release == null) return;

                lock (fake.Asked) fake.Asked.Clear();
                var path = await Updater.DownloadAsync(release);
                List<string> asked;
                lock (fake.Asked) asked = fake.Asked.ToList();
                Check("an update downloads the compressed copy, not the full executable",
                    asked.Contains("https://example/x.exe.gz") && !asked.Contains("https://example/x.exe"), string.Join(", ", asked));
                Check("and unpacks it to exactly the release's executable",
                    File.ReadAllBytes(path).AsSpan().SequenceEqual(exe), null);
                Check("leaving no compressed file behind",
                    !Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.gz*").Any(), null);

                // Damaged: unpacks to something else. The hash catches it and the
                // full executable is downloaded instead.
                var wrong = (byte[])exe.Clone();
                wrong[10] ^= 0xFF;
                using (var ms = new MemoryStream())
                {
                    using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                        gz.Write(wrong);
                    servedGz = ms.ToArray();
                }
                lock (fake.Asked) fake.Asked.Clear();
                var fallback = await Updater.DownloadAsync(release with { CompressedSize = servedGz.Length });
                lock (fake.Asked) asked = fake.Asked.ToList();
                Check("a compressed copy that unpacks wrong falls back to the full download",
                    asked.Contains("https://example/x.exe") && File.ReadAllBytes(fallback).AsSpan().SequenceEqual(exe),
                    string.Join(", ", asked));

                // Not gzip at all.
                servedGz = Encoding.UTF8.GetBytes("<html>not here</html>");
                var notGz = await Updater.DownloadAsync(release with { CompressedSize = servedGz.Length });
                Check("so does one that is not gzip at all", File.ReadAllBytes(notGz).AsSpan().SequenceEqual(exe), null);

                // A release without one downloads the executable as before.
                var plain = await Updater.DownloadAsync(release with { CompressedUrl = null, CompressedSize = -1 });
                Check("a release without a compressed copy downloads the executable", File.ReadAllBytes(plain).AsSpan().SequenceEqual(exe), null);
            }
            finally
            {
                Updater.DirectoryOverride = before;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void MarkerTests()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var previous = Updater.DirectoryOverride;
            Updater.DirectoryOverride = dir;
            try
            {
                var marker = Path.Combine(dir, "updating-to.txt");

                // Our marker bytes, then nothing that will decrypt.
                File.WriteAllBytes(marker, Encoding.ASCII.GetBytes("ENDPAPI1this is not a DPAPI blob"));
                Check("a corrupt marker says nothing", Updater.TakeJustUpdatedMessage() == null);
                Check("and is removed rather than read again on every launch", !File.Exists(marker));

                ProtectedFile.WriteAllText(marker, "0.0.0");
                Check("a good marker is announced", Updater.TakeJustUpdatedMessage() != null);
                Check("and removed", !File.Exists(marker));
            }
            finally
            {
                Updater.DirectoryOverride = previous;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// Encrypting a log in place while other writers append to it must not
        /// lose a line. It read, wrote a copy and moved it over the log, and a
        /// stress run kept 804 lines of 2,195.
        /// </summary>
        private static void LogRaceTests()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-lograce-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var log = Path.Combine(dir, "install.log");
                File.WriteAllText(log, string.Join(Environment.NewLine, Enumerable.Range(0, 50).Select(i => "old " + i)) + Environment.NewLine);

                const int encrypted = 400, plain = 150;
                int failures = 0;
                var stop = false;

                var appender = new Thread(() =>
                {
                    for (int i = 0; i < encrypted; i++)
                    {
                        try { ProtectedFile.AppendLine(log, "new " + i); }
                        catch { Interlocked.Increment(ref failures); }
                    }
                });

                // An older build writing in the clear, so every pass has
                // something to rewrite. It waits its turn the way any writer
                // sharing the file does.
                var older = new Thread(() =>
                {
                    for (int i = 0; i < plain; i++)
                    {
                        for (int attempt = 0; ; attempt++)
                        {
                            try
                            {
                                using var f = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.Read);
                                var b = Encoding.UTF8.GetBytes("plain " + i + Environment.NewLine);
                                f.Write(b, 0, b.Length);
                                break;
                            }
                            catch (IOException) when (attempt < 1000) { Thread.Sleep(5); }
                            catch { Interlocked.Increment(ref failures); break; }
                        }
                    }
                });

                var rewriter = new Thread(() =>
                {
                    while (!Volatile.Read(ref stop))
                    {
                        ProtectedFile.EncryptLogInPlace(log);
                        Thread.Sleep(1);
                    }
                });

                rewriter.Start();
                appender.Start();
                older.Start();
                appender.Join(TimeSpan.FromSeconds(60));
                older.Join(TimeSpan.FromSeconds(60));
                Volatile.Write(ref stop, true);
                rewriter.Join(TimeSpan.FromSeconds(10));
                ProtectedFile.EncryptLogInPlace(log);

                var lines = ProtectedFile.ReadLog(log).Where(l => l.Length > 0).ToList();
                Equal("no append failed while the log was being rewritten", "0", failures.ToString());
                Equal("every line survived the rewrites", (50 + encrypted + plain).ToString(), lines.Count.ToString());
                Check("each of them once",
                    lines.Distinct().Count() == lines.Count &&
                    Enumerable.Range(0, encrypted).All(i => lines.Contains("new " + i)) &&
                    Enumerable.Range(0, plain).All(i => lines.Contains("plain " + i)));
                Check("and the log is encrypted at the end",
                    File.ReadAllLines(log).Where(l => l.Length > 0).All(l => l.StartsWith(ProtectedFile.LinePrefix, StringComparison.Ordinal)));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// Twenty launches at once from pool threads. The listener's pipe used
        /// to complete on the thread pool, so a pool full of senders starved it
        /// and half the launches timed out.
        /// </summary>
        private static async Task HandoffBurstTests()
        {
            SingleInstance.NameSuffix = "_burst_" + Guid.NewGuid().ToString("N")[..8];
            int received = 0;
            SingleInstance.StartServer(_ => Interlocked.Increment(ref received));
            try
            {
                await Task.Delay(300);
                const int n = 20;
                var sent = await Task.WhenAll(Enumerable.Range(0, n).Select(i =>
                    Task.Run(() => SingleInstance.SendToRunningInstance($@"C:\burst\{i}"))));
                for (int i = 0; i < 50 && Volatile.Read(ref received) < n; i++) await Task.Delay(100);

                Equal("twenty launches at once from pool threads are all delivered",
                    n.ToString(), sent.Count(x => x).ToString());
                Equal("and all arrive", n.ToString(), Volatile.Read(ref received).ToString());
            }
            finally
            {
                SingleInstance.StopServer();
                await Task.Delay(200);
                SingleInstance.NameSuffix = "";
            }
        }
    }

    // ---- merged from InstallTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// Replacing a binary that something is running from.
    ///
    /// This is the one part of installing that cannot be reasoned about from the
    /// code: Windows refuses to overwrite a mapped executable and allows the same
    /// file to be renamed, and nothing in the type system says so. It is also the
    /// part that failed in the field, an install that depended on the
    /// application closing first, against four instances that would not answer
    /// the pipe asking them to, so it is exercised here against a real running
    /// process rather than against a file with a handle open on it.
    ///
    /// The difference matters and is the reason this test starts a process at
    /// all. A FileStream opened with FileShare.Read also refuses an overwrite,
    /// so it looks like a faithful stand-in; but it refuses the *rename* too,
    /// while a running image allows it. A test built on the cheap imitation would
    /// have asserted the opposite of the behaviour being relied on.
    /// </summary>
    internal static class InstallTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Replacing the installed binary:");

            PlainReplace();
            ReplaceWhileRunning();
            SweepTests();
            WhatIsInstalledTests();
            ShapeTests();
        }

        /// <summary>
        /// Only the application goes, whatever else shares its folder.
        /// </summary>
        private static void WhatIsInstalledTests()
        {
            var dir = NewDir();
            try
            {
                foreach (var name in new[] { "ExplorerNative.exe", "holiday.iso", "notes.txt", "ExplorerNative.pdb" })
                    File.WriteAllText(Path.Combine(dir, name), name);

                var single = AppInstall.FilesToInstall(dir).Select(Path.GetFileName).ToList();
                Equal("a published build installs its executable and nothing beside it",
                    "ExplorerNative.exe", string.Join(", ", single));

                foreach (var name in new[] { "ExplorerNative.dll", "Concentus.dll", "ExplorerNative.deps.json" })
                    File.WriteAllText(Path.Combine(dir, name), name);
                File.WriteAllText(Path.Combine(dir, "ExplorerNative.exe" + AppInstall.RetiredMarker + "1"), "old");

                var multi = AppInstall.FilesToInstall(dir).Select(Path.GetFileName)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                Equal("a build that is not single-file installs the program's files and not the rest",
                    "Concentus.dll, ExplorerNative.deps.json, ExplorerNative.dll, ExplorerNative.exe, ExplorerNative.pdb",
                    string.Join(", ", multi));
            }
            finally { Cleanup(dir); }
        }

        /// <summary>Nothing is holding it: one copy, nothing left behind.</summary>
        private static void PlainReplace()
        {
            var dir = NewDir();
            try
            {
                var source = Path.Combine(dir, "new.bin");
                var target = Path.Combine(dir, "live.bin");

                File.WriteAllText(source, "the new build");
                File.WriteAllText(target, "the old build");

                bool renamed = AppInstall.ReplaceFile(source, target);

                Check("a file nothing is using is replaced without renaming anything", !renamed);
                Equal("and it holds the new bytes", "the new build", File.ReadAllText(target));
                Check("with nothing left beside it",
                    Directory.GetFiles(dir).Length == 2,
                    string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// The case the whole design exists for.
        /// </summary>
        private static void ReplaceWhileRunning()
        {
            var dir = NewDir();
            Process? victim = null;

            try
            {
                // A real executable, really running. PowerShell is used because it
                // is on every Windows machine and will sit still when told to.
                var host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");

                if (!File.Exists(host))
                {
                    Check("a host executable to run from is available", false, host);
                    return;
                }

                var target = Path.Combine(dir, "live.exe");
                File.Copy(host, target);

                victim = Process.Start(new ProcessStartInfo(target)
                {
                    Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                Check("the stand-in process started", victim != null);
                if (victim == null) return;

                // Give it a moment to map its own image.
                for (int i = 0; i < 50 && !IsLocked(target); i++) Thread.Sleep(100);

                Check("and Windows will not let its executable be overwritten", IsLocked(target),
                    "the premise of the whole design does not hold on this machine");

                var source = Path.Combine(dir, "new.bin");
                File.WriteAllText(source, "the new build");

                bool renamed = AppInstall.ReplaceFile(source, target);

                Check("the install still happens, by renaming the old one aside", renamed);
                Equal("and the path now holds the new bytes", "the new build", File.ReadAllText(target));

                Check("the process that was running has not been disturbed", !victim.HasExited);

                var retired = Directory.GetFiles(dir)
                    .Where(f => Path.GetFileName(f).Contains(AppInstall.RetiredMarker, StringComparison.Ordinal))
                    .ToList();

                Check("the old binary is beside it under a retired name", retired.Count == 1,
                    string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));

                // Not ".exe" any more, so nothing offers to run it and nobody
                // starts the wrong one out of a folder listing.
                Check("and that name is not something anybody can double-click into a second copy",
                    retired.Count == 1 &&
                    !Path.GetFileName(retired[0]).EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                    retired.Count == 1 ? Path.GetFileName(retired[0]) : "");

                Check("nothing half-written is left over",
                    !Directory.GetFiles(dir).Any(f =>
                        Path.GetFileName(f).Contains(".installing-", StringComparison.Ordinal)));

                // While it is still running, its own file cannot go, which is
                // why the sweep is best-effort and runs again at startup.
                Equal("the retired binary cannot be swept while it is still mapped",
                    "0", AppInstall.SweepRetired(dir).ToString());

                victim.Kill();
                victim.WaitForExit(10_000);

                // Windows can take a moment to release the section after exit.
                int swept = 0;
                for (int i = 0; i < 50 && swept == 0; i++)
                {
                    swept = AppInstall.SweepRetired(dir);
                    if (swept == 0) Thread.Sleep(100);
                }

                Equal("and it is swept once the process it belonged to has gone", "1", swept.ToString());
            }
            finally
            {
                try { if (victim is { HasExited: false }) victim.Kill(); } catch { }
                victim?.Dispose();
                Cleanup(dir);
            }
        }

        private static void SweepTests()
        {
            var dir = NewDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "ExplorerNative.exe"), "keep me");
                File.WriteAllText(Path.Combine(dir, "ExplorerNative.exe" + AppInstall.RetiredMarker + "1"), "old");
                File.WriteAllText(Path.Combine(dir, "ExplorerNative.exe" + AppInstall.RetiredMarker + "2"), "older");
                File.WriteAllText(Path.Combine(dir, "ExplorerNative.exe.installing-3"), "half written");
                File.WriteAllText(Path.Combine(dir, "settings-of-some-kind.json"), "not ours to delete");

                Equal("every retired and half-written file is swept", "3", AppInstall.SweepRetired(dir).ToString());

                Check("the live executable is untouched", File.Exists(Path.Combine(dir, "ExplorerNative.exe")));
                Check("and so is anything that is not one of ours",
                    File.Exists(Path.Combine(dir, "settings-of-some-kind.json")));

                Equal("sweeping again finds nothing", "0", AppInstall.SweepRetired(dir).ToString());
                Equal("and a folder that does not exist is not an error",
                    "0", AppInstall.SweepRetired(Path.Combine(dir, "nowhere")).ToString());

                // Older builds' unpacked files under .net\ExplorerNative go;
                // this build's, and other apps', stay.
                var net = Path.Combine(dir, ".net");
                var mine = Directory.CreateDirectory(Path.Combine(net, "ExplorerNative", "current")).FullName;
                File.WriteAllText(Path.Combine(net, "ExplorerNative", "current", "nvda.dll"), "x");
                Directory.CreateDirectory(Path.Combine(net, "ExplorerNative", "old1"));
                File.WriteAllText(Path.Combine(net, "ExplorerNative", "old1", "nvda.dll"), "x");
                Directory.CreateDirectory(Path.Combine(net, "ExplorerNative", "old2"));
                Directory.CreateDirectory(Path.Combine(net, "Kova", "theirs"));
                Equal("older builds' unpacked folders are swept", "2",
                    AppInstall.SweepOldExtractions(new[] { Path.Combine(dir, "elsewhere"), mine + Path.DirectorySeparatorChar }).ToString());
                Check("this build's own unpacked files stay", File.Exists(Path.Combine(mine, "nvda.dll")));
                Check("and another app's are not touched", Directory.Exists(Path.Combine(net, "Kova", "theirs")));
                Equal("with no unpacked folder of its own, nothing is touched", "0",
                    AppInstall.SweepOldExtractions(new[] { Path.Combine(dir, "elsewhere") }).ToString());
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// The rules the install path lives by, read out of the source.
        ///
        /// Program.cs cannot be compiled into this suite, it owns Main, so the
        /// things that must stay true about it are checked as text, the same way
        /// the pane-naming rule is.
        /// </summary>
        private static void ShapeTests()
        {
            var program = Source("Program.cs");
            var install = Source("AppInstall.cs");

            if (program == null || install == null)
            {
                Check("Program.cs and AppInstall.cs can be read", false);
                return;
            }

            // The failure that cost three minutes and told nobody anything.
            Check("nothing on a command-line path puts up a MessageBox any more",
                !program.Contains("MessageBox.Show(message", StringComparison.Ordinal));

            Check("a notice from a command-line run closes itself",
                program.Contains("NoticeMilliseconds", StringComparison.Ordinal) &&
                program.Contains("timer.Stop(); form.Close();", StringComparison.Ordinal));

            Check("every outcome is written to a log that outlives the window",
                program.Contains("WriteLog(title, message);", StringComparison.Ordinal));

            Check("a failure sets an exit code a script can read",
                program.Contains("Environment.ExitCode = 1;", StringComparison.Ordinal));

            Check("the console is used when there is one to use",
                program.Contains("AttachConsole(AttachParentProcess)", StringComparison.Ordinal));

            // The dependency that broke.
            Check("installing no longer waits for the application to close first",
                !program.Contains("bool steppedAside = AskRunningInstanceToExit();", StringComparison.Ordinal));

            Check("and the message that blamed the user for a window that had already gone is gone with it",
                !install.Contains("Close Explorer Native and try again", StringComparison.Ordinal));

            // "Has been restarted", said about a copy still running beside the new
            // one: the stand-down was judged by whether the installed path could
            // be opened, and after a rename the old process holds a different file.
            var standDown = Between(program, "private static StandDown AskRunningInstanceToExit()",
                "private static bool HasExited(");
            Check("a stand-down is judged by the processes, not by the installed file",
                standDown != null &&
                standDown.Contains("others.All(HasExited)", StringComparison.Ordinal) &&
                !standDown.Contains("AppInstall.ExePath,", StringComparison.Ordinal));
            Check("and the new copy is started only once the single-instance lock is free",
                program.Contains("old != StandDown.StillRunning", StringComparison.Ordinal) &&
                standDown != null && standDown.Contains("PrimaryLockIsFree()", StringComparison.Ordinal));
            Check("and the lock it tests is the one Main takes",
                program.Contains("new Mutex(false, SingleInstanceMutexName)", StringComparison.Ordinal) &&
                !program.Contains("new Mutex(false, \"ExplorerNative_SingleInstance\")", StringComparison.Ordinal));

            // The proof the old procedure asked a person to do by hand.
            Check("the installer hashes what it wrote and compares it",
                install.Contains("var wanted = HashOf(", StringComparison.Ordinal) &&
                install.Contains("var landed = HashOf(ExePath);", StringComparison.Ordinal));

            Check("a mismatch is a failure rather than a success with a warning",
                install.Contains("does not match the one that was installed", StringComparison.Ordinal));

            // The rollback. Losing the executable is worse than a failed install:
            // it is the registered handler for every folder on the machine.
            Check("a failed swap puts the old binary back",
                install.Contains("try { File.Move(retired, target); } catch", StringComparison.Ordinal));
        }

        private static bool IsLocked(string path)
        {
            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException) { return true; }
            catch { return false; }
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

        /// <summary>The text from <paramref name="start"/> up to <paramref name="end"/>, or null.</summary>
        private static string? Between(string text, string start, string end)
        {
            int from = text.IndexOf(start, StringComparison.Ordinal);
            if (from < 0) return null;
            int to = text.IndexOf(end, from, StringComparison.Ordinal);
            return to < 0 ? null : text[from..to];
        }

        private static string NewDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-install-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
