using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Downloaded audio held in memory, in fixed chunks, under one budget shared
    /// by every file.
    ///
    /// Chunks rather than one buffer per file, because a whole-file buffer means
    /// the largest thing you ever open decides the memory ceiling — and there is
    /// a 232MB track in this very Drive. Chunks make the ceiling a policy instead
    /// of an accident, and let a file larger than the whole budget still stream:
    /// the beginning is dropped as the end arrives, which for something being
    /// played start to finish is exactly the right thing to lose.
    /// </summary>
    internal sealed class DriveCachePool
    {
        public const int ChunkBytes = 4 * 1024 * 1024;

        private sealed class Item
        {
            public string File = "";
            public int Chunk;
            public byte[] Bytes = Array.Empty<byte>();
        }

        private readonly object _gate = new();
        private readonly Dictionary<(string, int), LinkedListNode<Item>> _map = new();

        // Most recently used at the front, so eviction takes from the back.
        private readonly LinkedList<Item> _order = new();
        private long _used;

        public long Budget { get; }
        public long Used { get { lock (_gate) return _used; } }
        public long Evicted { get; private set; }

        public DriveCachePool(long budget) => Budget = budget;

        /// <summary>
        /// Which cache was read most recently, and when — so a download nobody is
        /// reading can stand aside for the one somebody is.
        /// </summary>
        public void NoteRead(string file)
        {
            Volatile.Write(ref _lastReader, file);
            Interlocked.Exchange(ref _lastReadAt, Environment.TickCount64);
        }

        private string _lastReader = "";
        private long _lastReadAt;

        public string LastReader => Volatile.Read(ref _lastReader);
        public long LastReadAt => Interlocked.Read(ref _lastReadAt);

        public byte[]? Get(string file, int chunk)
        {
            lock (_gate)
            {
                if (!_map.TryGetValue((file, chunk), out var node)) return null;
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Bytes;
            }
        }

        public void Put(string file, int chunk, byte[] bytes)
        {
            lock (_gate)
            {
                if (_map.ContainsKey((file, chunk))) return;

                var node = _order.AddFirst(new Item { File = file, Chunk = chunk, Bytes = bytes });
                _map[(file, chunk)] = node;
                _used += bytes.Length;

                while (_used > Budget && _order.Last != null)
                {
                    var last = _order.Last;
                    _order.RemoveLast();
                    _map.Remove((last.Value.File, last.Value.Chunk));
                    _used -= last.Value.Bytes.Length;
                    Evicted += last.Value.Bytes.Length;
                }
            }
        }

        /// <summary>
        /// Everything belonging to one file. Called when a track is finished
        /// with, which is what keeps "one track at a time" true rather than
        /// hopeful.
        /// </summary>
        public void DropFile(string file)
        {
            lock (_gate)
            {
                var node = _order.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (node.Value.File == file)
                    {
                        _order.Remove(node);
                        _map.Remove((node.Value.File, node.Value.Chunk));
                        _used -= node.Value.Bytes.Length;
                    }
                    node = next;
                }
            }
        }
    }

    /// <summary>
    /// One Drive file, downloading into the pool while it is being read.
    ///
    /// This exists because of one measured fact: a Drive request costs about a
    /// second whatever is in it. Windows asks a cloud provider for roughly 256KB
    /// at a time, so answering each callback with its own request is a second per
    /// quarter megabyte — measured at 0.21MB/s, about five minutes for a 68MB
    /// file, and that is what a Drive-to-disk copy really ran at.
    ///
    /// So the callbacks never talk to Drive if they can help it. A worker pulls
    /// the file down in 4MB chunks, several requests at a time, and the callbacks
    /// are memory copies.
    ///
    /// **Two rules make that true rather than hopeful, and both were missing.**
    ///
    /// A read that misses fetches the whole *chunk* its offset lives in, not the
    /// sliver the callback asked for, and files it in the pool. One request then
    /// answers the sixteen callbacks that follow it instead of sixteen requests
    /// answering one each. This is the whole difference between 0.2MB/s and the
    /// link's real speed on the miss path — and the miss path is where a copy
    /// lives, because a reader taking bytes as fast as they arrive pins itself to
    /// the downloader's watermark within seconds and never gets ahead again.
    ///
    /// And a chunk is fetched **once**, however many readers want it. The
    /// downloader and the callbacks used to race for the same bytes down separate
    /// requests, each halving the other's share of a link that measures about
    /// 1MB/s per stream — so the reader's duplicate request made the download it
    /// was waiting for slower, which made the next read miss too. Requests in
    /// flight are shared, so a callback that arrives while the downloader is
    /// already fetching its chunk waits for that one rather than starting a
    /// second.
    ///
    /// Measured on a 68.8MB file, cold, Drive to local disk through robocopy:
    /// 102 seconds — 17.8MB at 5MB/s while the downloader's head start lasted,
    /// then 51MB at 0.6MB/s once the reader caught it. A long enough file ran out
    /// of the operating system's patience altogether and failed the read with
    /// "the cloud operation was not completed before the time-out period
    /// expired", which is the reported bug.
    /// </summary>
    internal sealed class DriveFileCache : IDisposable
    {
        /// <summary>
        /// How many chunk requests the downloader keeps in flight.
        ///
        /// Two, for a long time, on a measurement that said two streams reached
        /// 11.8MB/s against 5.1 for one. That is not what the link does any more.
        /// Measured again, same account, same file, 4MB ranges:
        ///
        /// | streams | 1 | 2 | 4 |
        /// | --- | --- | --- | --- |
        /// | MB/s | 0.9 | 1.9 | 3.2 |
        ///
        /// One stream is about 1MB/s and it scales nearly linearly to four, so
        /// the old setting was leaving two thirds of the link unused. Past four
        /// it flattens — 8MB by four measured 3.5 and then 2.9 — and every extra
        /// stream is one more request competing with the callbacks' own.
        /// </summary>
        private const int Streams = 4;

        /// <summary>How long a download may go unread before it gives way to one that is.</summary>
        private const int YieldAfterMilliseconds = 3000;

        /// <summary>
        /// The most a read that cannot wait for its chunk fetches by itself.
        /// A request costs about the same whatever is in it up to here, and a
        /// whole 4MB chunk measured four to six seconds on a busy link.
        /// </summary>
        private const int DirectBytes = 1024 * 1024;

        /// <summary>How long a read waits for its chunk before fetching its own bytes.</summary>
        private const int ChunkPatienceMilliseconds = 400;

        /// <summary>
        /// Chunks a read has already fetched around, once each. More than once
        /// and a copy reading at the download's edge makes a small request for
        /// every callback, which is the 0.2MB/s this class exists to prevent.
        /// </summary>
        private readonly ConcurrentDictionary<int, byte> _directed = new();

        private readonly DriveClient _drive;
        private readonly DriveCachePool _pool;
        private readonly CancellationTokenSource _cancel = new();

        /// <summary>
        /// Chunk requests started and not yet finished.
        ///
        /// A <see cref="Lazy{T}"/> rather than the task itself, because the value
        /// overload of GetOrAdd has to be handed something already made — and a
        /// task that is already made is a request that has already gone out. The
        /// loser of the race never calls Value, so it never sends anything. The
        /// factory overload is the other way round, and is the trap DriveMount
        /// already carries a comment about: it may run more than once for one key
        /// and keep only one result.
        /// </summary>
        private readonly ConcurrentDictionary<int, Lazy<Task<byte[]>>> _inFlight = new();

        public string FileId { get; }

        /// <summary>
        /// What this cache's chunks are filed under in the pool: the file and
        /// which cache fetched them. By the file alone, a cache for new contents
        /// read chunks an older cache of the same file was still bringing in,
        /// and the older one letting go dropped the newer one's.
        /// </summary>
        private readonly string _poolKey;
        private static long _instances;
        public long Size { get; }

        /// <summary>
        /// When this file was last read, for the mount's "only a few open at
        /// once" rule. A tick count, not a time: it is only ever compared.
        /// </summary>
        public long LastUsed;

        public DriveFileCache(DriveClient drive, DriveCachePool pool, string fileId, long size)
        {
            _drive = drive;
            _pool = pool;
            FileId = fileId;
            _poolKey = fileId + "#" + Interlocked.Increment(ref _instances);
            Size = size;
            LastUsed = Environment.TickCount64;

            // Not started here. Opening a cache used to start four 4MB requests
            // at once, so every byte anything read — Explorer looking for a tag
            // or a thumbnail, a property sheet, an antivirus peek — cost sixteen
            // megabytes of a link that gives three or four a second. The
            // download starts when it is wanted: the file was warmed, or it is
            // being read straight through. See StartPump.
            _ahead = Math.Max(Streams * 2, (int)Math.Min(int.MaxValue, pool.Budget / DriveCachePool.ChunkBytes / 4));
        }

        /// <summary>
        /// How small reads of a file nobody is reading through are answered: one
        /// ranged request of at least this much, kept for the reads that follow.
        /// A request costs the same up to here, and a quarter megabyte covers
        /// what a tag or thumbnail reader looks at in one go.
        /// </summary>
        private const int ColdBytes = 256 * 1024;

        /// <summary>
        /// How far a reader goes straight through the file before the download
        /// starts. A copy passes it in its first few callbacks; a tag reader
        /// never does.
        /// </summary>
        private const long SequentialStart = 1024 * 1024;

        /// <summary>
        /// How many chunks the download may run ahead of the furthest one read:
        /// a quarter of the pool, so the three files that may be open at once
        /// fit together, and the download never evicts the chunks just ahead of
        /// a reader to make room for ones further on. It was unbounded, and a
        /// file over the whole pool lost its own next chunks to its own tail.
        /// </summary>
        private readonly int _ahead;

        /// <summary>Most times one chunk is tried by the download before reads are left to fetch it.</summary>
        private const int MostChunkFailures = 5;

        private int _pumpStarted;
        private volatile bool _everRead;

        /// <summary>The chunk the latest read began in, and the furthest any read has reached.</summary>
        private int _lastReadChunk = -1;
        private int _highestRead = -1;

        /// <summary>The straight run the reads have made, start and end, for <see cref="SequentialStart"/>.</summary>
        private long _runStart = -1, _runEnd = -1;
        private readonly object _runGate = new();

        /// <summary>Wakes the download when a read moves on or a chunk finishes.</summary>
        private readonly SemaphoreSlim _wake = new(0, 1);

        /// <summary>Chunks the download itself has asked for and not finished with.</summary>
        private readonly ConcurrentDictionary<int, byte> _pumpFetching = new();

        /// <summary>Chunks the download failed to fetch: how often, and when it may try again.</summary>
        private readonly ConcurrentDictionary<int, (int Count, long RetryAt)> _failed = new();

        /// <summary>
        /// The last few cold reads, whole, so the reads that follow a small one
        /// are memory copies. Two: the head and the tail a decoder looks at.
        /// </summary>
        private readonly List<(long At, byte[] Bytes)> _fragments = new();
        private readonly object _fragmentGate = new();

        /// <summary>
        /// Starts the download, once. Called when the file is warmed (a lease
        /// that ends without a read is exactly what warming is) and when the
        /// reads pass <see cref="SequentialStart"/> straight through.
        /// </summary>
        internal void StartPump()
        {
            if (_cancel.IsCancellationRequested) return;
            if (Interlocked.CompareExchange(ref _pumpStarted, 1, 0) != 0) return;
            _ = Task.Run(() => Pump(_cancel.Token));
        }

        /// <summary>Whether the download has been started, for the suite.</summary>
        internal bool Pumping => Volatile.Read(ref _pumpStarted) != 0;

        private void Wake()
        {
            if (_wake.CurrentCount == 0) try { _wake.Release(); } catch (SemaphoreFullException) { }
        }

        /// <summary>
        /// Records where a read is, for the download's window and for deciding
        /// whether this is a reader going straight through.
        /// </summary>
        private void NoteRead(long offset, int length)
        {
            _everRead = true;
            if (length <= 0 || offset >= Size) return;

            long end = Math.Min(Size, offset + length);
            int first = (int)(offset / DriveCachePool.ChunkBytes);
            int last = (int)((end - 1) / DriveCachePool.ChunkBytes);
            Volatile.Write(ref _lastReadChunk, first);
            int seen;
            while ((seen = Volatile.Read(ref _highestRead)) < last &&
                   Interlocked.CompareExchange(ref _highestRead, last, seen) != seen) { }

            bool through;
            lock (_runGate)
            {
                // Overlapping or touching the run so far, within the 4KB a
                // callback is rounded to, carries it on.
                if (_runStart >= 0 && offset >= _runStart && offset <= _runEnd + 4096)
                    _runEnd = Math.Max(_runEnd, end);
                else
                {
                    _runStart = offset;
                    _runEnd = end;
                }
                through = _runEnd - _runStart >= SequentialStart;
            }

            if (through) StartPump();
            Wake();
        }

        /// <summary>How many chunks the file is, rounded up.</summary>
        private int ChunkCount =>
            (int)((Size + DriveCachePool.ChunkBytes - 1) / DriveCachePool.ChunkBytes);

        /// <summary>
        /// One whole chunk: from the pool, from a request already in flight, or
        /// from a new one — in that order.
        /// </summary>
        private async Task<byte[]> Chunk(int index)
        {
            var cached = _pool.Get(_poolKey, index);
            if (cached != null) return cached;

            var mine = new Lazy<Task<byte[]>>(
                () => Fetch(index), LazyThreadSafetyMode.ExecutionAndPublication);
            var kept = _inFlight.GetOrAdd(index, mine);

            try { return await kept.Value; }
            finally
            {
                // Only the entry we are holding, so a slow loser cannot remove a
                // request some later reader has just started.
                _inFlight.TryRemove(new KeyValuePair<int, Lazy<Task<byte[]>>>(index, kept));
            }
        }

        private async Task<byte[]> Fetch(int index)
        {
            long start = (long)index * DriveCachePool.ChunkBytes;
            int length = (int)Math.Min(DriveCachePool.ChunkBytes, Size - start);
            if (length <= 0) return Array.Empty<byte>();

            var bytes = await _drive.ReadRange(FileId, start, length, _cancel.Token);

            // Only a whole chunk is filed. A short read stored under a chunk index
            // would leave the pool holding the wrong bytes for that index with
            // nothing to report it: the file is simply wrong from there on.
            // Nor after this cache has been let go of, or the chunks outlive it.
            if (bytes.Length == length && !_cancel.IsCancellationRequested)
            {
                _pool.Put(_poolKey, index, bytes);

                // Let go of in between the check and the store: the chunk would
                // sit in the budget under a key nothing will ask for again.
                if (_cancel.IsCancellationRequested) _pool.DropFile(_poolKey);
            }
            return bytes;
        }

        /// <summary>
        /// Pulls the file down ahead of its reader, <see cref="Streams"/> chunks
        /// in flight at a time, and through the same door the callbacks use — so
        /// a chunk somebody is already waiting on is never fetched twice.
        ///
        /// A sliding window rather than batches: it was four requests, then a
        /// wait for the slowest of the four, then four more, so one slow
        /// response idled the other three streams. A failed chunk is tried again
        /// after a pause rather than ending the download — one 503 used to leave
        /// the rest of the file to the reads, a chunk at a time.
        /// </summary>
        private async Task Pump(CancellationToken token)
        {
            var slots = new SemaphoreSlim(Streams, Streams);
            try
            {
                int chunks = ChunkCount;
                while (!token.IsCancellationRequested)
                {
                    await slots.WaitAsync(token);

                    int target;
                    while (true)
                    {
                        // Standing aside while another file is the one being read
                        // and this one is not. Clicking through tracks left a
                        // whole-file download running for each, all splitting a
                        // link that gives a few megabytes a second between them —
                        // and the track somebody had just picked took long enough
                        // to open that it failed. A warmed file nothing has read
                        // yet keeps going until something else is read.
                        if (Environment.TickCount64 - LastUsed > YieldAfterMilliseconds &&
                            _pool.LastReadAt > LastUsed &&
                            !string.Equals(_pool.LastReader, _poolKey, StringComparison.Ordinal))
                        {
                            await _wake.WaitAsync(1000, token);
                            continue;
                        }

                        target = NextChunk(chunks, out bool finished);
                        if (target >= 0) break;

                        // Everything to the end of the file is held, or given up
                        // on, and nothing is still coming: done. A reader that
                        // goes back later fetches what it needs itself.
                        if (finished && slots.CurrentCount == Streams - 1)
                        {
                            slots.Release();
                            return;
                        }

                        await _wake.WaitAsync(1000, token);
                    }

                    _pumpFetching[target] = 0;
                    _ = FetchAhead(target, slots, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // A download that dies leaves the file readable: the reads fall
                // through to Drive one chunk at a time. Slower, not broken.
            }
        }

        /// <summary>
        /// The next chunk the download should fetch, or -1. The first one from
        /// where the latest read began that is not held, not on its way and not
        /// resting after a failure, no further than <see cref="_ahead"/> past
        /// the furthest read. <paramref name="finished"/> says nothing at all is
        /// left between there and the end of the file.
        /// </summary>
        private int NextChunk(int chunks, out bool finished)
        {
            finished = false;
            int from = Math.Max(0, Volatile.Read(ref _lastReadChunk));
            // The end of the current straight run, not the furthest byte ever read:
            // one tag read at the end of a big file used to lift the bound for good.
            long runEnd = Interlocked.Read(ref _runEnd);
            int highest = runEnd < 0 ? -1 : (int)Math.Min(int.MaxValue, runEnd / DriveCachePool.ChunkBytes);
            long limit = Math.Min(chunks, (long)Math.Max(from - 1, highest) + 1 + _ahead);
            long now = Environment.TickCount64;
            bool resting = false;

            for (int index = from; index < limit; index++)
            {
                if (_pumpFetching.ContainsKey(index) || _inFlight.ContainsKey(index)) { resting = true; continue; }
                if (_failed.TryGetValue(index, out var failure))
                {
                    if (failure.Count >= MostChunkFailures) continue;
                    if (failure.RetryAt > now) { resting = true; continue; }
                }
                if (_pool.Get(_poolKey, index) != null) continue;
                return index;
            }

            finished = limit >= chunks && !resting;
            return -1;
        }

        /// <summary>
        /// One chunk for the download, giving its slot back however it ends, and
        /// noting a failure so the chunk rests before it is tried again.
        /// </summary>
        private async Task FetchAhead(int index, SemaphoreSlim slots, CancellationToken token)
        {
            bool fetched = false;
            try
            {
                var bytes = await Chunk(index);

                // Short means the response ended early. Retried like a failure;
                // a file genuinely shorter than it says stops after a few.
                long want = Math.Min(DriveCachePool.ChunkBytes, Size - (long)index * DriveCachePool.ChunkBytes);
                fetched = bytes.Length >= want;
            }
            catch
            {
                // Cancelled, or Drive did not answer: handled below.
            }
            finally
            {
                if (fetched) _failed.TryRemove(index, out _);
                else if (!token.IsCancellationRequested)
                {
                    int count = _failed.TryGetValue(index, out var before) ? before.Count + 1 : 1;
                    long rest = Math.Min(15_000, 500L << Math.Min(count - 1, 5));
                    _failed[index] = (count, Environment.TickCount64 + rest);
                }

                _pumpFetching.TryRemove(index, out _);
                slots.Release();
                Wake();
            }
        }

        /// <summary>
        /// <paramref name="length"/> bytes at <paramref name="offset"/>, from
        /// memory where possible.
        ///
        /// Anything missing is fetched a whole chunk at a time — see the class
        /// comment. <c>FromMemory</c> is false when any part of the answer had to
        /// be waited for, which is what the mount's counters mean by a callback
        /// that was not served from memory.
        /// </summary>
        public async Task<(byte[] Bytes, bool FromMemory)> Read(
            long offset, int length, CancellationToken token)
        {
            LastUsed = Environment.TickCount64;
            _pool.NoteRead(_poolKey);
            NoteRead(offset, length);
            lock (_leaseGate) _readers++;
            try { return await ReadCore(offset, length, token); }
            finally
            {
                lock (_leaseGate) _readers--;
                LastUsed = Environment.TickCount64;
            }
        }

        private int _readers;
        private bool _retired;
        private readonly object _leaseGate = new();

        /// <summary>Whether a read is in progress, so the mount does not let this go under it.</summary>
        public bool Busy => Volatile.Read(ref _readers) > 0;

        /// <summary>
        /// Holds the cache open for a reader from the moment the mount hands it
        /// out. Without it there was a gap between being handed the cache and
        /// the read starting in which eviction could stop the download under
        /// the callback. False when the cache has already been let go.
        /// </summary>
        public bool TryLease()
        {
            lock (_leaseGate)
            {
                if (_retired) return false;
                _readers++;
                return true;
            }
        }

        public void EndLease()
        {
            lock (_leaseGate) _readers--;

            // A lease that ends with nothing ever read through it is the mount
            // warming the file ahead of a paste: the one case where the whole
            // download is wanted before anybody asks for a byte.
            if (!_everRead) StartPump();
        }

        /// <summary>
        /// Lets the cache go unless somebody is reading it, as one step, so a
        /// reader cannot arrive between the check and the release.
        /// </summary>
        public bool TryRetire()
        {
            lock (_leaseGate)
            {
                if (_readers > 0) return false;
                _retired = true;
            }
            Dispose();
            return true;
        }

        private async Task<(byte[] Bytes, bool FromMemory)> ReadCore(
            long offset, int length, CancellationToken token)
        {
            if (offset >= Size) return (Array.Empty<byte>(), true);
            length = (int)Math.Min(length, Size - offset);

            var result = new byte[length];
            int filled = 0;
            bool fromMemory = true;

            while (filled < length)
            {
                token.ThrowIfCancellationRequested();

                long at = offset + filled;
                int index = (int)(at / DriveCachePool.ChunkBytes);
                int within = (int)(at % DriveCachePool.ChunkBytes);

                var chunk = _pool.Get(_poolKey, index);
                if (chunk == null)
                {
                    // What an earlier cold read brought back beyond what it was
                    // asked for.
                    int kept = FromFragments(at, result.AsSpan(filled, length - filled));
                    if (kept > 0)
                    {
                        filled += kept;
                        continue;
                    }

                    fromMemory = false;

                    // A file nobody is reading straight through, and nothing on
                    // its way for this chunk: one small request, at least a
                    // quarter megabyte, kept for the reads that follow. Four
                    // whole chunks for a tag reader's 64KB was the cost of
                    // touching a file at all.
                    if (!Pumping && !_inFlight.ContainsKey(index))
                    {
                        long chunkEnd = Math.Min(Size, (long)(index + 1) * DriveCachePool.ChunkBytes);
                        int ask = (int)Math.Min(Math.Max(length - filled, ColdBytes), chunkEnd - at);
                        var cold = await _drive.ReadRange(FileId, at, ask, token);
                        if (cold.Length == 0) break;

                        int use = Math.Min(cold.Length, length - filled);
                        Buffer.BlockCopy(cold, 0, result, filled, use);
                        filled += use;
                        if (cold.Length > use) KeepFragment(at, cold);
                        continue;
                    }

                    // Cancellable, which it was not: the token was only looked at
                    // between chunks, so a caller with a deadline still waited as
                    // long as the chunk took, however long that was. Abandoning the
                    // wait leaves the download running for the next reader.
                    var whole = Chunk(index);

                    // A read that finds its chunk not there yet — the head, the
                    // tail and the middle a decoder looks at while opening — waits
                    // a moment for it and then fetches just its own bytes, once
                    // per chunk. Waiting for the whole chunk was seconds per
                    // region, and an open touches several.
                    if (!whole.IsCompleted && _directed.TryAdd(index, 0))
                    {
                        var delay = Task.Delay(ChunkPatienceMilliseconds, token);
                        if (await Task.WhenAny(whole, delay) != whole)
                        {
                            token.ThrowIfCancellationRequested();
                            int want = (int)Math.Min(Math.Min(length - filled, DirectBytes),
                                (long)(index + 1) * DriveCachePool.ChunkBytes - at);
                            var own = await _drive.ReadRange(FileId, at, want, token);
                            if (own.Length == 0) break;
                            Buffer.BlockCopy(own, 0, result, filled, own.Length);
                            filled += own.Length;
                            continue;
                        }
                    }

                    chunk = await whole.WaitAsync(token);
                }

                if (within >= chunk.Length) break;

                int take = Math.Min(length - filled, chunk.Length - within);
                Buffer.BlockCopy(chunk, within, result, filled, take);
                filled += take;
            }

            return (filled == length ? result : result[..filled], fromMemory);
        }

        /// <summary>How many fragments of cold reads are kept.</summary>
        private const int MostFragments = 2;

        /// <summary>
        /// Copies what the kept fragments hold from <paramref name="at"/> on,
        /// as much as one fragment covers. Zero when none starts at or before it.
        /// </summary>
        private int FromFragments(long at, Span<byte> into)
        {
            lock (_fragmentGate)
            {
                foreach (var (start, bytes) in _fragments)
                {
                    if (at < start || at >= start + bytes.Length) continue;
                    int take = (int)Math.Min(into.Length, start + bytes.Length - at);
                    bytes.AsSpan((int)(at - start), take).CopyTo(into);
                    return take;
                }
            }
            return 0;
        }

        private void KeepFragment(long at, byte[] bytes)
        {
            if (_cancel.IsCancellationRequested) return;
            lock (_fragmentGate)
            {
                _fragments.Insert(0, (at, bytes));
                if (_fragments.Count > MostFragments) _fragments.RemoveAt(_fragments.Count - 1);
            }
        }

        private void DropFragments()
        {
            lock (_fragmentGate) _fragments.Clear();
        }

        /// <summary>
        /// Stops the download and gives the memory back.
        ///
        /// Cancelled, not disposed — the same rule the folder-size walks and the
        /// pane work already follow. Pump is still inside an await when this
        /// runs, and its next HttpClient call registers a callback on this
        /// token; registering on a disposed source throws ObjectDisposedException
        /// on a background thread with nobody to catch it. The source holds no
        /// unmanaged handle, so letting it be collected is the correct ending.
        /// </summary>
        public void Dispose()
        {
            lock (_leaseGate) _retired = true;
            try { _cancel.Cancel(); } catch { }
            _pool.DropFile(_poolKey);
            DropFragments();
        }

        /// <summary>
        /// Stops this instance's download and leaves the pool alone — for a
        /// duplicate that lost the race to be the file's cache. What it fetched is
        /// filed under its own key, which nothing will ask for, so that goes too;
        /// the winner's chunks are under another.
        /// </summary>
        public void Abandon()
        {
            try { _cancel.Cancel(); } catch { }
            _pool.DropFile(_poolKey);
            DropFragments();
        }
    }
}
