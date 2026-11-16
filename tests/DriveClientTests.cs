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
    /// The Drive client's interactive listings, the chunk cache's download
    /// (when it starts, how far ahead it runs, what it does about a failure),
    /// and the search's level-at-a-time folder listing.
    /// </summary>
    internal static class DriveClientCacheTests
    {
        private const int Chunk = DriveCachePool.ChunkBytes;

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            await InteractiveListings(check);
            await ColdReads(check);
            await DroppedMemoryGoesBack(check);
            await SequentialReadsStartTheDownload(check);
            await WarmingStartsTheDownload(check);
            await FailedChunksAreRetried(check);
            await SearchListsFoldersTogether(check);
            await RateLimitsAreWaitedOut(check);
        }

        /// <summary>
        /// Google's "slow down" (403 rateLimitExceeded) on a copy to Drive is
        /// waited out, however many times it comes, and never spends the six
        /// attempts: a big copy used to end in a dialog with the files not
        /// copied. Twenty in a row on each kind of request; six used to fail.
        /// </summary>
        private static async Task RateLimitsAreWaitedOut(Action<string, bool, string?> check)
        {
            var limited = FakeDrive.ErrorBody(403, "rateLimitExceeded", "Rate Limit Exceeded");
            var dir = Path.Combine(Path.GetTempPath(), "en-ratelimit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var small = Path.Combine(dir, "small.txt");
                File.WriteAllText(small, "hello");
                var big = Path.Combine(dir, "big.bin");
                File.WriteAllBytes(big, Pattern((int)DriveClient.SmallUploadBytes + 1024));

                foreach (var kind in new[] { "multipart", "create", "begin", "put" })
                {
                    var fake = new FakeDrive();
                    var client = new DriveClient(fake, 0.0005);
                    var folder = fake.AddFolder("Up");
                    fake.AddFault(new FakeDrive.Fault { Kind = kind, Times = 20, Status = 403, Body = limited });
                    string? problem = null;
                    try
                    {
                        if (kind == "create") await client.CreateFolder("Disc", folder, CancellationToken.None);
                        else await client.UploadDetailed(kind == "multipart" ? small : big, "f", folder, null, CancellationToken.None);
                    }
                    catch (Exception ex) { problem = ex.Message; }
                    check($"Drive copy: twenty rate limits in a row on {kind} are waited out",
                        problem == null && fake.FaultsLeft == 0, $"{problem}; {fake.FaultsLeft} left");
                }

                // Not for ever: an hour of waits, then the file fails.
                var endless = new FakeDrive();
                var patient = new DriveClient(endless, 0.0005);
                var into = endless.AddFolder("Up");
                endless.AddFault(new FakeDrive.Fault { Kind = "multipart", Times = 100_000, Status = 403, Body = limited });
                bool failed = false;
                try { await patient.UploadDetailed(small, "f", into, null, CancellationToken.None); }
                catch (Exception) { failed = true; }
                check("Drive copy: a rate limit that never lifts still ends, after about an hour of waits",
                    failed && endless.FaultsServed is > 20 and < 200, $"failed {failed}, {endless.FaultsServed} tries");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static byte[] Pattern(int length)
        {
            var data = new byte[length];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + (i >> 12));
            return data;
        }

        /// <summary>Waits until <paramref name="done"/> is true, or the limit passes.</summary>
        private static async Task<bool> Eventually(Func<bool> done, int milliseconds = 10_000)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                if (done()) return true;
                await Task.Delay(20);
            }
            return done();
        }

        /// <summary>Waits until the fake has stopped serving reads for a moment.</summary>
        private static async Task Settle(FakeDrive fake)
        {
            int last = -1;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 10_000)
            {
                int now = Volatile.Read(ref fake.MediaReads);
                if (now == last) return;
                last = now;
                await Task.Delay(300);
            }
        }

        private static async Task InteractiveListings(Action<string, bool, string?> check)
        {
            var fake = new FakeDrive();
            var client = new DriveClient(fake, 0.0005);
            var folder = fake.AddFolder("Music");
            fake.AddFile("a.flac", new byte[10], DateTime.UtcNow, folder);

            // Two dropped connections: more than a listing somebody is waiting on
            // sits through, well inside what a transfer's listing retries.
            fake.AddFault(new FakeDrive.Fault { Kind = "list", Status = 0, Times = 2 });
            bool failed = false;
            try { await client.ListChildren(folder, CancellationToken.None, interactive: true); }
            catch (HttpRequestException) { failed = true; }
            check("Drive listing: one somebody is waiting on gives up after two tries", failed, null);

            fake.AddFault(new FakeDrive.Fault { Kind = "list", Status = 0, Times = 2 });
            var patient = await client.ListChildren(folder, CancellationToken.None);
            check("Drive listing: the upload and monitor callers' default still retries its way through",
                patient.Count == 1, $"{patient.Count} entries");

            // The mount's listing announces itself with a label: that alone makes
            // it interactive, without the mount having to say so.
            fake.AddFault(new FakeDrive.Fault { Kind = "list", Status = 0, Times = 2 });
            failed = false;
            try { await client.ListChildren(folder, CancellationToken.None, label: "Music"); }
            catch (HttpRequestException) { failed = true; }
            check("Drive listing: a labelled listing is treated as interactive", failed, null);

            // Known to be offline: no second try and no wait for the network.
            for (int i = 0; i < 3; i++) client.Health.Failed(new HttpRequestException("gone"));
            check("Drive listing: the fake is offline for the next check", client.Health.IsOffline, null);
            int before = fake.FaultsServed;
            fake.AddFault(new FakeDrive.Fault { Kind = "list", Status = 0, Times = 5 });
            var clock = Stopwatch.StartNew();
            failed = false;
            try { await client.ListChildren(folder, CancellationToken.None, interactive: true); }
            catch (HttpRequestException) { failed = true; }
            check("Drive listing: offline, an interactive listing fails at once",
                failed && fake.FaultsServed - before == 1 && clock.ElapsedMilliseconds < 5000,
                $"failed {failed}, {fake.FaultsServed - before} attempts, {clock.ElapsedMilliseconds}ms");
        }

        private static async Task ColdReads(Action<string, bool, string?> check)
        {
            var fake = new FakeDrive();
            var client = new DriveClient(fake, 0.0005);
            var data = Pattern(5 * Chunk);
            var id = fake.AddFile("song.flac", data, DateTime.UtcNow, FakeDrive.Root);
            var pool = new DriveCachePool(1024L * 1024 * 1024);
            var cache = new DriveFileCache(client, pool, id, data.Length);

            try
            {
                await Task.Delay(200);
                check("Drive cache: opening a file downloads nothing", fake.MediaReads == 0, $"{fake.MediaReads} reads");

                var (first, _) = await cache.Read(0, 64 * 1024, CancellationToken.None);
                await Task.Delay(300);
                check("Drive cache: a small cold read is one small request, not four chunks",
                    fake.MediaReads == 1 && !cache.Pumping, $"{fake.MediaReads} reads, pumping {cache.Pumping}");
                check("Drive cache: and it reads the right bytes", first.AsSpan().SequenceEqual(data.AsSpan(0, 64 * 1024)), null);

                var (next, memory) = await cache.Read(64 * 1024, 64 * 1024, CancellationToken.None);
                check("Drive cache: the read after it comes from what that request brought back",
                    memory && fake.MediaReads == 1 && next.AsSpan().SequenceEqual(data.AsSpan(64 * 1024, 64 * 1024)),
                    $"from memory {memory}, {fake.MediaReads} reads");

                // The tail, as a tag reader looks at it.
                int tailAt = data.Length - 4096;
                var (tail, _) = await cache.Read(tailAt, 4096, CancellationToken.None);
                check("Drive cache: a cold read at the end is right too",
                    tail.AsSpan().SequenceEqual(data.AsSpan(tailAt, 4096)) && !cache.Pumping, null);
            }
            finally { cache.Dispose(); }
        }

        /// <summary>
        /// A file the cache lets go of is given back to Windows within seconds,
        /// not just back to .NET, which kept it committed until some later
        /// full collection: 400MB dropped still showed as 400MB.
        /// </summary>
        private static async Task DroppedMemoryGoesBack(Action<string, bool, string?> check)
        {
            var after = MemoryGiveBack.After;
            MemoryGiveBack.After = TimeSpan.FromMilliseconds(200);
            try
            {
                var pool = new DriveCachePool(1024L * 1024 * 1024);
                for (int i = 0; i < 100; i++)
                {
                    var bytes = new byte[Chunk];
                    for (int at = 0; at < bytes.Length; at += 4096) bytes[at] = 1;
                    pool.Put("song", i, bytes);
                }
                MemoryGiveBack.Collect();
                long held = GC.GetGCMemoryInfo().TotalCommittedBytes;
                int before = MemoryGiveBack.Collections;
                var paused = GC.GetTotalPauseDuration();

                pool.DropFile("song");
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (MemoryGiveBack.Collections == before && clock.ElapsedMilliseconds < 5000) await Task.Delay(50);
                long left = GC.GetGCMemoryInfo().TotalCommittedBytes;
                var pause = GC.GetTotalPauseDuration() - paused;

                check($"Drive cache: 400MB dropped goes back to Windows within seconds " +
                      $"({held / 1048576}MB committed before, {left / 1048576}MB after, {pause.TotalMilliseconds:0}ms pause)",
                    held - left > 300L * 1024 * 1024,
                    $"committed {held / 1048576}MB before, {left / 1048576}MB after, collection pause {pause.TotalMilliseconds:0}ms");
                check("Drive cache: and the pause it costs is short", pause < TimeSpan.FromMilliseconds(250),
                    $"{pause.TotalMilliseconds:0}ms");

                int now = MemoryGiveBack.Collections;
                MemoryGiveBack.Freed(4L * 1024 * 1024);
                await Task.Delay(500);
                check("Drive cache: a small release costs no collection", MemoryGiveBack.Collections == now, null);
            }
            finally { MemoryGiveBack.After = after; }
        }

        private static async Task SequentialReadsStartTheDownload(Action<string, bool, string?> check)
        {
            var fake = new FakeDrive();
            var client = new DriveClient(fake, 0.0005);
            var data = Pattern(24 * Chunk);
            var id = fake.AddFile("long.flac", data, DateTime.UtcNow, FakeDrive.Root);

            // Sixteen chunks of pool: a quarter is four, so the window is the
            // floor of eight chunks past the furthest read.
            var pool = new DriveCachePool(16L * Chunk);
            var cache = new DriveFileCache(client, pool, id, data.Length);

            try
            {
                const int step = 256 * 1024;
                bool right = true;
                for (long at = 0; at < 1280 * 1024; at += step)
                {
                    var (bytes, _) = await cache.Read(at, step, CancellationToken.None);
                    right &= bytes.AsSpan().SequenceEqual(data.AsSpan((int)at, step));
                }
                check("Drive cache: reading straight through the first megabyte starts the download",
                    cache.Pumping && right, $"pumping {cache.Pumping}, right {right}");

                await Settle(fake);
                long furthest;
                lock (fake.MediaOffsets) furthest = fake.MediaOffsets.Count == 0 ? -1 : fake.MediaOffsets.Max();
                check("Drive cache: the download stays within its window of the reader",
                    furthest >= 0 && furthest < 9L * Chunk,
                    $"furthest request at chunk {furthest / Chunk}");

                // Moving on moves the window.
                await cache.Read(6L * Chunk, step, CancellationToken.None);
                await Settle(fake);
                lock (fake.MediaOffsets) furthest = fake.MediaOffsets.Max();
                check("Drive cache: and the window follows the reader",
                    furthest >= 9L * Chunk && furthest < 15L * Chunk, $"furthest request at chunk {furthest / Chunk}");

                var (later, memory) = await cache.Read(8L * Chunk, step, CancellationToken.None);
                check("Drive cache: what the download fetched is served from memory",
                    memory && later.AsSpan().SequenceEqual(data.AsSpan(8 * Chunk, step)), $"from memory {memory}");
            }
            finally { cache.Dispose(); }
        }

        private static async Task WarmingStartsTheDownload(Action<string, bool, string?> check)
        {
            var fake = new FakeDrive();
            var client = new DriveClient(fake, 0.0005);
            var data = Pattern(3 * Chunk + 1000);
            var id = fake.AddFile("warm.flac", data, DateTime.UtcNow, FakeDrive.Root);
            var pool = new DriveCachePool(1024L * 1024 * 1024);
            var cache = new DriveFileCache(client, pool, id, data.Length);

            try
            {
                // What DriveMount.Warm does: open it, hand it straight back.
                cache.TryLease();
                cache.EndLease();
                check("Drive cache: a warmed file starts downloading", cache.Pumping, null);

                bool all = await Eventually(() => fake.MediaReads >= 4);
                var (bytes, memory) = await cache.Read(0, data.Length, CancellationToken.None);
                check("Drive cache: and has the whole file before anybody reads it",
                    all && memory && bytes.AsSpan().SequenceEqual(data), $"{fake.MediaReads} reads, from memory {memory}");
            }
            finally { cache.Dispose(); }
        }

        private static async Task FailedChunksAreRetried(Action<string, bool, string?> check)
        {
            var fake = new FakeDrive();
            var client = new DriveClient(fake, 0.0005);
            var data = Pattern(3 * Chunk);
            var id = fake.AddFile("flaky.flac", data, DateTime.UtcNow, FakeDrive.Root);
            var pool = new DriveCachePool(1024L * 1024 * 1024);
            var cache = new DriveFileCache(client, pool, id, data.Length);

            try
            {
                // A 404 is not retried by the ranged read itself, so this is the
                // download's own retry or nothing. It used to end the download.
                fake.AddFault(new FakeDrive.Fault { Kind = "media", Status = 404, Times = 1,
                    Body = FakeDrive.ErrorBody(404, "notFound", "File not found") });
                cache.TryLease();
                cache.EndLease();

                bool all = await Eventually(() => fake.MediaReads >= 3);
                var (bytes, memory) = await cache.Read(0, data.Length, CancellationToken.None);
                check("Drive cache: a chunk that failed once is fetched again, and the rest still come",
                    all && fake.FaultsServed == 1 && memory && bytes.AsSpan().SequenceEqual(data),
                    $"{fake.MediaReads} reads, {fake.FaultsServed} faults, from memory {memory}");
            }
            finally { cache.Dispose(); }
        }

        private static async Task SearchListsFoldersTogether(Action<string, bool, string?> check)
        {
            // root -> a -> {a1, a2}, root -> b -> b1, root -> c
            var chains = new List<IReadOnlyList<string>>
            {
                new[] { "a", "a1" }, new[] { "a", "a2" }, new[] { "b", "b1" }, new[] { "c" }, new string[0],
            };
            var names = new Dictionary<string, string>
            {
                ["a"] = "A", ["a1"] = "A1", ["a2"] = "A2", ["b"] = "B", ["b1"] = "B1", ["c"] = "C",
            };

            var listed = new List<string>();
            var populated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int running = 0, most = 0;

            void Populate(string path)
            {
                int now = Interlocked.Increment(ref running);
                lock (listed)
                {
                    most = Math.Max(most, now);
                    listed.Add(path);
                }
                Thread.Sleep(80);
                lock (listed) populated.Add(path);
                Interlocked.Decrement(ref running);
            }

            string? ChildPathForId(string parent, string id)
            {
                lock (listed)
                    if (!populated.Contains(parent)) return null;   // looked up before its parent was listed
                return names.TryGetValue(id, out var name) ? parent + "\\" + name : null;
            }

            var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
            // Enough pool threads to run four at once even late in a busy suite:
            // starved, the pool started them one at a time and this read as "1 at once".
            ThreadPool.GetMinThreads(out int workers, out int ports);
            ThreadPool.SetMinThreads(Math.Max(workers, 32), ports);
            try { await DriveSearch.PopulateAhead("G:", chains, Populate, ChildPathForId, resolved, 4, CancellationToken.None); }
            finally { ThreadPool.SetMinThreads(workers, ports); }

            check("Drive search: every folder on the way to a hit is listed once",
                listed.Count == 7 && listed.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 7,
                string.Join(", ", listed));
            check("Drive search: and folders at one level are listed together, at most four at a time",
                most > 1 && most <= 4, $"{most} at once");

            // Walking each hit afterwards needs nothing more than what is known.
            int before = listed.Count;
            var path = DriveSearch.PathForChain("G:", chains[1], _ => { }, ChildPathForId, resolved);
            check("Drive search: the hits then resolve from what was listed",
                path == "G:\\A\\A2" && listed.Count == before, path);
        }
    }

    // ---- merged from Sweep308ClientTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// Bug sweep, Drive client half: a small upload whose token was refused
    /// once, and a folder-sync download whose request timed out before any
    /// bytes arrived. Both used to fail the file outright.
    /// </summary>
    internal static class Sweep308ClientTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Drive client sweep:");

            await SmallUploadRetriesARefusedToken();
            await DownloadRetriesATimeout();

            Console.WriteLine();
        }

        /// <summary>Answers each request with whatever the test says, counting them.</summary>
        private sealed class Scripted : HttpMessageHandler
        {
            private readonly Func<int, HttpRequestMessage, HttpResponseMessage> _answer;
            public int Calls;

            public Scripted(Func<int, HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                int call = Interlocked.Increment(ref Calls);
                return Task.FromResult(_answer(call, request));
            }
        }

        private static async Task SmallUploadRetriesARefusedToken()
        {
            var file = Path.Combine(Path.GetTempPath(), "en-sweep308-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
            File.WriteAllText(file, "abc");
            try
            {
                var handler = new Scripted((call, _) => call == 1
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("{\"error\":{\"code\":401,\"message\":\"Invalid Credentials\"}}"),
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":\"landed\",\"size\":\"3\"}", Encoding.UTF8, "application/json"),
                    });
                var client = new DriveClient(handler, 0.001);

                string id = "";
                string problem = "";
                try
                {
                    var done = await client.UploadDetailed(file, "a.txt", "parent", null, CancellationToken.None);
                    id = done.Id;
                }
                catch (Exception ex) { problem = ex.GetType().Name + ": " + ex.Message; }

                Equal("Drive upload: a small file whose token was refused once is sent again and lands",
                    "landed", id.Length > 0 ? id : problem);
                Equal("and it took exactly one more request", "2", handler.Calls.ToString());
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        private static async Task DownloadRetriesATimeout()
        {
            var data = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7)).ToArray();
            var handler = new Scripted((call, request) =>
            {
                if (call == 1) throw new TimeoutException("Google Drive did not answer within 15 seconds");
                long from = request.Headers.Range?.Ranges.First().From ?? 0;
                var body = new ByteArrayContent(data, (int)from, data.Length - (int)from);
                body.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, data.Length - 1, data.Length);
                return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = body };
            });
            var client = new DriveClient(handler, 0.001);

            using var into = new MemoryStream();
            string problem = "";
            try
            {
                await client.DownloadTo("file", 0, data.Length, into, _ => { }, CancellationToken.None);
            }
            catch (Exception ex) { problem = ex.GetType().Name + ": " + ex.Message; }

            Check("Drive download: a request that timed out is asked again rather than failing the file",
                problem.Length == 0, problem);
            Check("and what arrived is the whole file", into.ToArray().SequenceEqual(data),
                $"{into.Length} of {data.Length} bytes");
        }
    }

    // ---- merged from Sweep309ClientTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

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

    // ---- merged from Sweep500ClientTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

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
                System.Text.RegularExpressions.Regex.IsMatch(tray, @"_drive\.Mount\(_settings, silently: true, mayConsent: false\)"));
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

    // ---- merged from Release510DriveTests.cs (7.1.0: tests are filed by feature, not by fix round) ----

    /// <summary>
    /// 5.1.0: Google Drive: the mounted letter, the client, the folder sync and the tray.
    /// These read the source, because the code they pin needs a live Drive, WinFsp or a window.
    /// </summary>
    internal static class Release510DriveTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        private static string Src(string name)
        {
            var at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at != null && !Directory.Exists(Path.Combine(at.FullName, "src"))) at = at.Parent;
            if (at == null) return "";
            var path = Path.Combine(at.FullName, "src", name);
            try { return File.Exists(path) ? SourceFiles.Read(path) : ""; }
            catch { return ""; }
        }

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("5.1.0, Google Drive: the mounted letter, the client, the folder sync and the tray:");

            var monitor = Src("DriveMonitor.cs");
            var driveOnly = Src("DriveMonitor.DriveOnly.cs");
            var form = Src("DriveMonitorForm.cs");
            var mount = Src("DriveMount.cs");
            var volume = Src("DriveMount.Volume.cs");
            var tray = Src("TrayApplicationContext.cs");
            var drive = Src("GoogleDrive.cs");
            Check("sources were found", monitor.Length > 0 && form.Length > 0 && tray.Length > 0);

            Check("1: coming back on another letter tells the app",
                volume.Contains("OnLetterMoved?.Invoke") && drive.Contains("LetterMoved?.Invoke") && tray.Contains("_drive.LetterMoved +="));
            Check("2/3: a folder listing is linked to the unmount",
                mount.Contains("CreateLinkedTokenSource(_closing.Token);\n            deadline.CancelAfter") ||
                mount.Contains("CreateLinkedTokenSource(_closing.Token);\r\n            deadline.CancelAfter"));
            Check("4: the WinFsp offer stays set until the install ends",
                tray.Contains("if (!installing) _offeringWinFsp = false;"));
            Check("5: Reserve takes out only its own failed listing",
                Src("DriveUpload.cs").Contains("new KeyValuePair<string, Lazy<HashSet<string>?>>(folder, lazy)"));
            Check("6: a timed-out folder is not 'operation was canceled'",
                drive.Contains("Google Drive took too long to answer"));
            Check("7: the shared look checks the folder the drive was read for", monitor.Contains("_driveOfFolder"));
            Check("8: one second is not 'seconds'", monitor.Contains("s == 1 ? \"1 second\""));
            Check("9: unreadable files keep their folder in DirtyFolders", monitor.Contains("unread?.Add(path)") &&
                monitor.Contains("unread.Select(p => new SyncAction"));
            Check("10: a refused move is counted as failed", monitor.Contains("SyncHistory.Add(pair.Id, \"Could not sync\", move.To, ex.Message)"));
            Check("11: a stopped pair is not swallowed by the feed fallbacks",
                monitor.Split("ex is not PairStoppedException").Length >= 3);
            Check("12: Keep on Drive only checks the folder off the UI thread",
                !driveOnly.Contains("if (!Directory.Exists(pcFolder))") && driveOnly.Contains("Task.Run(() => Directory.Exists(pcFolder))"));
            Check("13: Put back refuses in an upload-only pair",
                driveOnly.Contains("pair.Mode == SyncMode.UploadOnly\n                return new(false, $\"{DisplayName(pair)} only uploads, so {Leaf(entry)}") ||
                driveOnly.Contains("cannot be put back on this PC"));
            Check("14: Delete in the sync window asks first", form.Contains("Stop syncing {DriveMonitor.DisplayName(_pairs[at])}?"));
            Check("15: the sync window, picker and help lists respect the speech switch",
                form.Contains("if (!MaySpeak) return;") && Src("ChangelogForm.cs").Contains("if (!MaySpeak) return;") &&
                Src("KeyboardShortcutsForm.cs").Contains("if (!MaySpeak) return;") && Src("MainForm.cs").Contains("MaySpeak = _settings.SpeakEnabled"));
            Check("16: putting back a staged folder brings back the ones it absorbed", form.Contains("_absorbed.Remove(gone.Relative"));
            Check("17: the picker names Google Drive at the top", form.Contains("(AtTop ? \"Google Drive\" : FolderPath) + \": \""));
            Check("18: a space-stopped full listing merges the folder map",
                monitor.Contains("if (full && tally.SpaceProblem != null)"));
            Check("19: the fast lanes hand folder failures to the careful loop",
                monitor.Contains("TryMakeFoldersAsync(client, pair, state, folders, batch.Select") &&
                !monitor.Contains("await MakeFoldersAsync(client, pair, state, folders, batch.Select"));
            Check("20: fast lane counts start from the pair's own", monitor.Contains("Uploaded = upBefore + count") &&
                monitor.Contains("Downloaded = downBefore + count"));
            Check("21: the updated message respects the speech switch", Src("Program.cs").Contains("if (mayAnnounce) Speech.Speak(updated);"));
            Check("22: WinFsp installed but not loading is not offered an install",
                drive.Contains("NeedsWinFsp = !winFspLoads && !WinFsp.Installed;"));
            Check("23: the output device is saved on OK", tray.Contains("if (outcome == DialogResult.OK) _settings.Save();"));

            await Task.CompletedTask;
        }
    }
}
