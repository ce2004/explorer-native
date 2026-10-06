using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
            await ChangelogPagingTests();
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
                Check("an answer of the wrong shape is reported plainly",
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

                fake.Answer = _ => Json("{\"tag_name\":\"v0.0.1\",\"assets\":[]}");
                Check("an older one is not offered", await Updater.CheckAsync() == null);

                fake.Answer = _ => Json("{\"tag_name\":\"v99.0.0\",\"assets\":[{\"name\":\"other.exe\",\"size\":1}]}");
                var noAsset = await Fails(() => Updater.CheckAsync());
                Check("a release with nothing for this computer still says so",
                    noAsset is Updater.UpdateException && noAsset.Message.Contains(asset, StringComparison.Ordinal),
                    noAsset?.Message);

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

        private static string ReleasesPage(int from, int count) =>
            "[" + string.Join(",", Enumerable.Range(from, count).Select(i =>
                $"{{\"tag_name\":\"v1.{i}.0\",\"draft\":false,\"prerelease\":false," +
                $"\"published_at\":\"2026-01-01T00:00:00Z\",\"body\":\"- change {i}\"}}")) + "]";

        private static async Task ChangelogPagingTests()
        {
            var fake = new FakeGitHub();
            Updater.UseHandlerForTests(fake);
            try
            {
                // Three pages, linked the way GitHub links them.
                fake.Answer = request =>
                {
                    var url = request.RequestUri!.AbsoluteUri;
                    int page = url.Contains("page=3") ? 3 : url.Contains("page=2") ? 2 : 1;
                    var r = Json(ReleasesPage((page - 1) * 100, page == 3 ? 5 : 100));
                    if (page < 3)
                        r.Headers.TryAddWithoutValidation("Link",
                            $"<https://api.github.com/repositories/1/releases?per_page=100&page={page + 1}>; rel=\"next\", " +
                            "<https://api.github.com/repositories/1/releases?per_page=100&page=3>; rel=\"last\"");
                    return r;
                };
                var all = await Updater.AllReleasesAsync();
                Equal("the changelog reads past the first hundred releases", "205", all.Count.ToString());
                Check("newest first across the pages",
                    all.Count > 0 && all[0].Version == new Version(1, 204, 0) && all[^1].Version == new Version(1, 0, 0));

                // A Link header that points at itself stops at the bound.
                fake.Asked.Clear();
                fake.Answer = request =>
                {
                    var r = Json(ReleasesPage(0, 1));
                    r.Headers.TryAddWithoutValidation("Link",
                        "<https://api.github.com/repositories/1/releases?page=2>; rel=\"next\"");
                    return r;
                };
                await Updater.AllReleasesAsync();
                Equal("a page that never ends is read a bounded number of times",
                    Updater.MaxReleasePages.ToString(), fake.Asked.Count.ToString());

                // Only GitHub's API is followed.
                fake.Asked.Clear();
                fake.Answer = request =>
                {
                    var r = Json(ReleasesPage(0, 1));
                    r.Headers.TryAddWithoutValidation("Link", "<https://example.com/releases?page=2>; rel=\"next\"");
                    return r;
                };
                await Updater.AllReleasesAsync();
                Equal("a next page somewhere else is not followed", "1", fake.Asked.Count.ToString());

                fake.Answer = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
                var limited = await Fails(() => Updater.AllReleasesAsync());
                Check("the changelog says when GitHub is limiting checks",
                    limited is Updater.UpdateException { Problem: Updater.Problem.RateLimited });
            }
            finally
            {
                Updater.UseHandlerForTests(null);
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
}
