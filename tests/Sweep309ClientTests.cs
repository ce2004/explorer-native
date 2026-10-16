using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Second bug sweep, Drive client half: a file that changes size while it
    /// goes up, a full account met while asking a session how far it got, an
    /// answer that takes longer than the stall limit after the last byte, a
    /// refused read's error page, two source folders of one name, the IPv4
    /// fallback after IPv6 fails at once, and the changelog's "this one" mark.
    /// Everything runs against scripted handlers; nothing talks to Google.
    /// </summary>
    internal static class Sweep309ClientTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Drive client sweep, second pass:");

            await ResumableUploadThatGrowsStopsAtOnce();
            await ResumableUploadResizedBeforeSendingStopsAtOnce();
            await SmallUploadThatGrowsStopsAtOnce();
            await FullAccountWhileAskingASessionStopsTheTransfer();
            await SlowAnswerAfterTheLastByteIsNotAStall();
            await DeadAnswerAfterTheLastByteStillEnds();
            await RefusedReadsAreSentencesWithTheirStatus();
            await Ipv4StartsAsSoonAsIpv6Fails();
            SameNamedSourceFoldersStayApart();
            ChangelogMarksThisOneAsANumber();

            Console.WriteLine();
        }

        /// <summary>Answers each request with whatever the test says, counting them.</summary>
        private sealed class Scripted : HttpMessageHandler
        {
            private readonly Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;
            public int Calls;

            public Scripted(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
                _answer(Interlocked.Increment(ref Calls), request, token);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage KeepGoing() => new((HttpStatusCode)308) { Content = new ByteArrayContent(Array.Empty<byte>()) };

        /// <summary>
        /// Reads a request's file bytes as the real handler would, and breaks as
        /// it does when they do not match the length that was declared.
        /// </summary>
        private static async Task ReadBodyLikeTheSocket(HttpRequestMessage request, CancellationToken token)
        {
            var parts = request.Content is MultipartContent multi
                ? multi.ToList()
                : new List<HttpContent> { request.Content! };
            foreach (var part in parts)
            {
                if (part is not StreamContent) { await part.ReadAsByteArrayAsync(token); continue; }
                long? declared = part.Headers.ContentLength;
                var bytes = await part.ReadAsByteArrayAsync(token);
                if (declared is { } d && bytes.Length != d)
                    throw new HttpRequestException(
                        $"Unable to write content to request stream; content would exceed Content-Length ({bytes.Length} of {d})");
            }
        }

        private static string TempFile(int bytes)
        {
            var path = Path.Combine(Path.GetTempPath(), "en-sweep309-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
            File.WriteAllBytes(path, new byte[bytes]);
            return path;
        }

        private static readonly int BigFile = (int)DriveClient.SmallUploadBytes + 4096;

        private static bool IsQuery(HttpRequestMessage request) =>
            request.Method == HttpMethod.Put && request.Content is not StreamContent;

        private static async Task ResumableUploadThatGrowsStopsAtOnce()
        {
            var file = TempFile(BigFile);
            try
            {
                var handler = new Scripted(async (call, request, token) =>
                {
                    if (IsQuery(request)) return KeepGoing();

                    // Somebody saves over the file while it is going up.
                    File.AppendAllText(file, "more");
                    await ReadBodyLikeTheSocket(request, token);
                    return KeepGoing();
                });
                var client = new DriveClient(handler, 0.001);

                string problem = "";
                Exception? caught = null;
                try
                {
                    await client.UploadDetailed(file, "big.bin", "parent", null, CancellationToken.None,
                        resumeSession: "https://upload.test/session");
                }
                catch (Exception ex) { caught = ex; problem = ex.GetType().Name + ": " + ex.Message; }

                Check("Drive upload: a big file that grows while it goes up fails with a sentence saying so",
                    caught is DriveClient.ChangedWhileSendingException &&
                    problem.Contains("changed size while it was being sent", StringComparison.Ordinal), problem);
                Check("and at once, not after five resumes of the same failure",
                    handler.Calls <= 3, $"{handler.Calls} requests");
            }
            finally { try { File.Delete(file); } catch { } }
        }

        private static async Task ResumableUploadResizedBeforeSendingStopsAtOnce()
        {
            var file = TempFile(BigFile);
            try
            {
                int sends = 0;
                var handler = new Scripted((call, request, token) =>
                {
                    if (IsQuery(request))
                    {
                        // Shrunk between the session opening and the bytes going.
                        if (call == 1) using (var f = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) f.SetLength(BigFile - 100);
                        return Task.FromResult(KeepGoing());
                    }
                    Interlocked.Increment(ref sends);
                    return Task.FromResult(KeepGoing());
                });
                var client = new DriveClient(handler, 0.001);

                Exception? caught = null;
                try
                {
                    await client.UploadDetailed(file, "big.bin", "parent", null, CancellationToken.None,
                        resumeSession: "https://upload.test/session");
                }
                catch (Exception ex) { caught = ex; }

                Check("Drive upload: a big file that shrank before its bytes went is not sent at all",
                    caught is DriveClient.ChangedWhileSendingException && sends == 0,
                    $"{caught?.GetType().Name}: {caught?.Message}; {sends} sends");
            }
            finally { try { File.Delete(file); } catch { } }
        }

        private static async Task SmallUploadThatGrowsStopsAtOnce()
        {
            var file = TempFile(3);
            try
            {
                var handler = new Scripted(async (call, request, token) =>
                {
                    File.AppendAllText(file, "more");
                    await ReadBodyLikeTheSocket(request, token);
                    return Json(HttpStatusCode.OK, "{\"id\":\"landed\",\"size\":\"3\"}");
                });
                var client = new DriveClient(handler, 0.001);

                Exception? caught = null;
                try { await client.UploadDetailed(file, "a.txt", "parent", null, CancellationToken.None); }
                catch (Exception ex) { caught = ex; }

                Check("Drive upload: a small file that grows while it goes up fails at once with a sentence",
                    caught is DriveClient.ChangedWhileSendingException && handler.Calls == 1,
                    $"{caught?.GetType().Name}: {caught?.Message}; {handler.Calls} requests");
            }
            finally { try { File.Delete(file); } catch { } }
        }

        private static async Task FullAccountWhileAskingASessionStopsTheTransfer()
        {
            var file = TempFile(BigFile);
            try
            {
                const string full = "{\"error\":{\"errors\":[{\"domain\":\"usageLimits\",\"reason\":\"storageQuotaExceeded\"}]," +
                                    "\"code\":403,\"message\":\"The user's Drive storage quota has been exceeded.\"}}";
                var handler = new Scripted((call, request, token) =>
                    Task.FromResult(Json(HttpStatusCode.Forbidden, full)));
                var client = new DriveClient(handler, 0.001);

                Exception? caught = null;
                try
                {
                    await client.UploadDetailed(file, "big.bin", "parent", null, CancellationToken.None,
                        resumeSession: "https://upload.test/session");
                }
                catch (Exception ex) { caught = ex; }

                Check("Drive upload: a full account met while asking a session how far it got stops the whole transfer",
                    caught is DriveQuotaException { ResetsOnItsOwn: false },
                    $"{caught?.GetType().Name}: {caught?.Message}");
            }
            finally { try { File.Delete(file); } catch { } }
        }

        private static async Task SlowAnswerAfterTheLastByteIsNotAStall()
        {
            var file = TempFile(3);
            try
            {
                var handler = new Scripted(async (call, request, token) =>
                {
                    await ReadBodyLikeTheSocket(request, token);
                    // Every byte is in; Google takes its time putting the file
                    // together, four times the stall limit.
                    await Task.Delay(1200, token);
                    return Json(HttpStatusCode.OK, "{\"id\":\"landed\",\"size\":\"3\"}");
                });
                var client = new DriveClient(handler, 0.001)
                {
                    UploadStallLimit = TimeSpan.FromMilliseconds(300),
                    UploadAnswerLimit = TimeSpan.FromSeconds(20),
                };

                string id = "", problem = "";
                try { id = (await client.UploadDetailed(file, "a.txt", "parent", null, CancellationToken.None)).Id; }
                catch (Exception ex) { problem = ex.GetType().Name + ": " + ex.Message; }

                Equal("Drive upload: Google answering slowly after the last byte is not taken for a dead connection",
                    "landed", id.Length > 0 ? id : problem);
                Equal("and it was sent once", "1", handler.Calls.ToString());
            }
            finally { try { File.Delete(file); } catch { } }
        }

        private static async Task DeadAnswerAfterTheLastByteStillEnds()
        {
            var file = TempFile(3);
            try
            {
                var handler = new Scripted(async (call, request, token) =>
                {
                    // The tag lookups a retried create makes first: nothing there.
                    if (request.Method == HttpMethod.Get)
                        return Json(HttpStatusCode.OK, request.RequestUri!.Query.Contains("driveId", StringComparison.Ordinal) &&
                                                       !request.RequestUri.Query.Contains("appProperties", StringComparison.Ordinal)
                            ? "{}" : "{\"files\":[]}");
                    await ReadBodyLikeTheSocket(request, token);
                    await Task.Delay(Timeout.Infinite, token);
                    return Json(HttpStatusCode.OK, "{}");
                });
                var client = new DriveClient(handler, 0.001)
                {
                    UploadStallLimit = TimeSpan.FromMilliseconds(100),
                    UploadAnswerLimit = TimeSpan.FromMilliseconds(250),
                };

                var clock = Stopwatch.StartNew();
                string problem = "";
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await client.UploadDetailed(file, "a.txt", "parent", null, limit.Token); }
                catch (Exception ex) { problem = ex.GetType().Name + ": " + ex.Message; }

                Check("Drive upload: an answer that never comes after the last byte still ends, and says what happened",
                    problem.Contains("did not answer within", StringComparison.Ordinal) && !limit.IsCancellationRequested,
                    $"{problem} after {clock.ElapsedMilliseconds}ms");
            }
            finally { try { File.Delete(file); } catch { } }
        }

        private static async Task RefusedReadsAreSentencesWithTheirStatus()
        {
            const string page = "<!DOCTYPE html><html lang=en><title>Error 502 (Server Error)!!1</title>" +
                                "<p><b>502.</b> <ins>That's an error.</ins>";
            var busy = new Scripted((call, request, token) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent(page, Encoding.UTF8, "text/html"),
                }));
            var client = new DriveClient(busy, 0.001);

            string problem = "";
            Exception? caught = null;
            using (var into = new MemoryStream())
            {
                try { await client.DownloadTo("file", 0, 100, into, _ => { }, CancellationToken.None); }
                catch (Exception ex) { caught = ex; problem = ex.Message; }
            }
            Check("Drive download: Google's error page becomes a sentence, not markup",
                problem.Length > 0 && !problem.Contains('<') &&
                problem.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase), problem);
            Check("and still reads as \"not now\" to the folder sync",
                caught != null && SyncBackoff.IsTransient(caught), caught?.GetType().Name ?? "nothing thrown");

            var gone = new Scripted((call, request, token) =>
                Task.FromResult(Json(HttpStatusCode.NotFound,
                    "{\"error\":{\"code\":404,\"message\":\"File not found: file.\"}}")));
            client = new DriveClient(gone, 0.001);
            caught = null;
            try { await client.ReadRange("file", 0, 10, CancellationToken.None); }
            catch (Exception ex) { caught = ex; }
            Check("Drive read: a file that is gone is refused with its status kept",
                caught is DriveClient.DriveStatusException { Status: 404 } && gone.Calls == 1,
                $"{caught?.GetType().Name}: {caught?.Message}; {gone.Calls} requests");
        }

        private sealed class Token : IDisposable
        {
            public readonly string Name;
            public bool Disposed;
            public Token(string name) => Name = name;
            public void Dispose() => Disposed = true;
        }

        private static async Task Ipv4StartsAsSoonAsIpv6Fails()
        {
            var clock = Stopwatch.StartNew();
            long fallbackAt = -1;
            var won = await GoogleNet.RaceAsync<Token>(
                async t => { await Task.Delay(10, t); throw new System.Net.Sockets.SocketException(10051); },
                t => { fallbackAt = clock.ElapsedMilliseconds; return Task.FromResult(new Token("v4")); },
                headStartMilliseconds: 1000, CancellationToken.None);
            Check("Connecting: IPv4 starts the moment IPv6 fails, not after the head start",
                won.Name == "v4" && fallbackAt >= 0 && fallbackAt < 500, $"started at {fallbackAt}ms");

            // The control: IPv6 still gets its head start when it is only slow.
            clock.Restart();
            fallbackAt = -1;
            var slowV6 = new TaskCompletionSource<Token>();
            won = await GoogleNet.RaceAsync<Token>(
                t => { t.Register(() => slowV6.TrySetCanceled()); return slowV6.Task; },
                t => { fallbackAt = clock.ElapsedMilliseconds; return Task.FromResult(new Token("v4")); },
                headStartMilliseconds: 300, CancellationToken.None);
            Check("and a slow IPv6 still gets its head start before IPv4 joins",
                won.Name == "v4" && fallbackAt >= 250, $"started at {fallbackAt}ms");

            // And an IPv6 that answers wins without IPv4 being tried at all.
            bool tried = false;
            won = await GoogleNet.RaceAsync<Token>(
                t => Task.FromResult(new Token("v6")),
                t => { tried = true; return Task.FromResult(new Token("v4")); },
                headStartMilliseconds: 200, CancellationToken.None);
            await Task.Delay(300);
            Check("and an IPv6 that answers wins with no IPv4 connection made", won.Name == "v6" && !tried);
        }

        private static void SameNamedSourceFoldersStayApart()
        {
            var root = Path.Combine(Path.GetTempPath(), "en-sweep309-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var a = Path.Combine(root, "a", "Music");
                var b = Path.Combine(root, "b", "Music");
                Directory.CreateDirectory(Path.Combine(a, "Disc"));
                Directory.CreateDirectory(b);
                File.WriteAllText(Path.Combine(a, "Disc", "one.txt"), "1");
                File.WriteAllText(Path.Combine(b, "two.txt"), "2");

                var work = DriveUpload.Survey(new[] { a, b });
                Equal("Drive upload: two source folders of one name go up as two folders",
                    "Music|Music (2)", string.Join("|", work.Folders.Where(f => !f.Contains('\\'))));
                Equal("with each one's files in its own folder",
                    "one.txt=Music\\Disc|two.txt=Music (2)",
                    string.Join("|", work.Files.OrderBy(f => f.LocalPath)
                        .Select(f => Path.GetFileName(f.LocalPath) + "=" + f.RelativeFolder)));
                Equal("and the second is known by the name it goes up as",
                    "Music|Music (2)", string.Join("|", work.SourceNames ?? Array.Empty<string>()));
            }
            finally { try { Directory.Delete(root, recursive: true); } catch { } }
        }

        private static void ChangelogMarksThisOneAsANumber()
        {
            var lines = Changelog.Lines("3.1\nNew: something\n\n3.0.10\nBug fixed: another\n", "3.1.0");
            Check("Changelog: a version written 3.1 is marked as the running 3.1.0",
                lines.Contains("Version 3.1, this one"), string.Join(" | ", lines));
            Check("and the mark still reads as a heading", Changelog.IsHeading("Version 3.1, this one"));
            Check("and 3.0.10 is not 3.0.1",
                !Changelog.SameVersion("3.0.10", "3.0.1") && Changelog.SameVersion("3.0.10", "v3.0.10.0"));
        }
    }
}
