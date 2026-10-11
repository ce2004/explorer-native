using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

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
            await SequentialReadsStartTheDownload(check);
            await WarmingStartsTheDownload(check);
            await FailedChunksAreRetried(check);
            await SearchListsFoldersTogether(check);
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
            await DriveSearch.PopulateAhead("G:", chains, Populate, ChildPathForId, resolved, 4, CancellationToken.None);

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
}
