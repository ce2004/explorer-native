using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    public readonly record struct FolderSizeResult(long Bytes, int Files, int Folders, bool Complete);

    /// <summary>
    /// Computes folder sizes off the UI thread.
    ///
    /// The reason a naive folder-size column makes a file manager feel broken is
    /// that it walks the tree synchronously while painting. Here every walk runs
    /// on a worker, is cancellable, is bounded by a timeout and a depth limit,
    /// and results are cached against the folder's last-write time so revisiting
    /// a folder is instant. Reparse points are skipped so a junction loop can't
    /// spin forever.
    /// </summary>
    public sealed class FolderSizeCalculator : IDisposable
    {
        private readonly record struct CacheEntry(long Bytes, int Files, int Folders, DateTime Stamp, long MeasuredAt);

        /// <summary>
        /// How long a cached size may be trusted, however quiet the folder looks.
        ///
        /// The stamp check below is not the staleness test it was written as. A
        /// directory's last-write time changes when its *own* entries change —
        /// a file added, removed or renamed directly inside it. It does not move
        /// when something deep in a subtree grows, so a folder measured once and
        /// then filled up two levels down answered with the old number for the
        /// rest of the session, and there was nothing to tell you it had.
        ///
        /// Re-walking to find out whether a walk is needed defeats the cache, so
        /// the two are used together: the stamp catches a direct change straight
        /// away and cheaply, and this puts a ceiling on how long a deep one can
        /// hide. Generous, because the walks this avoids are the expensive ones —
        /// arrowing around with automatic sizes on re-asks constantly.
        /// </summary>
        public const int CacheSeconds = 300;

        private readonly ConcurrentDictionary<string, CacheEntry> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource _cts = new();
        private bool _disposed;

        // Caching is unconditional. A switch to turn it off only ever offered to
        // make the same walk happen twice. What an entry is judged against is
        // two things, for the reason set out on CacheSeconds: the folder's own
        // last-write time, and how long ago it was measured.

        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// How deep a walk goes. Sixty-four, and no longer a preference.
        ///
        /// Sixty-four levels is deeper than a Windows path will normally go, so
        /// the only values that ever changed the answer were the ones that made
        /// it wrong: a folder measured to depth three reports a number that is
        /// not its size, and nothing in "14.2 GB" says so. The timeout is the
        /// honest bound — it limits the work without misreporting the result —
        /// and Complete says when one was hit.
        ///
        /// Still settable, because the walk's own bounding has to be testable
        /// with a tree three levels deep rather than sixty-five.
        /// </summary>
        public int MaxDepth { get; set; } = 64;

        /// <summary>
        /// Folders the walk must not go into, however they are reached. The web app
        /// server sets it to the Drive sync root: walking that is one Drive listing
        /// per folder, and measuring C:\Users would otherwise walk all of Drive
        /// through the back door of %APPDATA%. Counted as a folder, not as its size.
        /// </summary>
        public Func<string, bool>? Skip { get; set; }

        public bool TryGetCached(string path, out FolderSizeResult result)
        {
            result = default;
            if (!_cache.TryGetValue(path, out var entry)) return false;

            // Expired first, because it needs no syscall to decide.
            if (Environment.TickCount64 - entry.MeasuredAt > CacheSeconds * 1000L)
            {
                _cache.TryRemove(path, out _);
                return false;
            }

            try
            {
                // Catches a change to this folder's own entries. It does not
                // catch one further down — see CacheSeconds.
                if (Directory.GetLastWriteTimeUtc(path) != entry.Stamp) return false;
            }
            catch
            {
                return false;
            }

            result = new FolderSizeResult(entry.Bytes, entry.Files, entry.Folders, true);
            return true;
        }

        /// <summary>
        /// Walks a folder on a worker thread.
        ///
        /// <paramref name="extra"/> lets a caller cancel just its own walk (the
        /// pane it belongs to navigated away) without touching everyone else's,
        /// which CancelAll cannot express.
        /// </summary>
        public Task<FolderSizeResult> CalculateAsync(string path, CancellationToken extra = default)
        {
            CancellationToken token;
            try { token = Volatile.Read(ref _cts).Token; }
            catch (ObjectDisposedException) { return Task.FromResult(new FolderSizeResult(0, 0, 0, false)); }

            if (extra.CanBeCanceled)
            {
                var linked = CancellationTokenSource.CreateLinkedTokenSource(token, extra);
                return Task.Run(() => Walk(path, linked.Token), linked.Token)
                           .ContinueWith(t =>
                           {
                               linked.Dispose();
                               return t.IsCompletedSuccessfully
                                   ? t.Result
                                   : new FolderSizeResult(0, 0, 0, false);
                           }, CancellationToken.None,
                              TaskContinuationOptions.ExecuteSynchronously,
                              TaskScheduler.Default);
            }

            // A cancelled walk is an ordinary outcome here, not an error: callers
            // want a partial result to show, never an exception to catch.
            return Task.Run(() => Walk(path, token), CancellationToken.None);
        }

        private FolderSizeResult Walk(string root, CancellationToken token)
        {
            if (TryGetCached(root, out var cached)) return cached;

            DateTime stamp;
            try { stamp = Directory.GetLastWriteTimeUtc(root); }
            catch { return new FolderSizeResult(0, 0, 0, false); }

            var walk = new TreeWalk(root, MaxDepth, Skip,
                DateTime.UtcNow.AddSeconds(Math.Max(1, TimeoutSeconds)), token);
            walk.Run();

            var result = new FolderSizeResult(walk.Bytes, walk.Files, walk.Folders, walk.Complete);

            // Only a complete walk is worth remembering: a partial answer cached
            // would be handed back for ever as though it were the real size.
            if (result.Complete)
                _cache[root] = new CacheEntry(result.Bytes, result.Files, result.Folders, stamp, Environment.TickCount64);

            return result;
        }

        /// <summary>
        /// How many folders one walk reads at once.
        ///
        /// A tree is mostly folders, and a folder costs an open, a read and a
        /// close however few files are in it: 6,000 folders of ten files took
        /// 200ms on one thread and 69ms on four, and a share, where each of those
        /// is a round trip, gains more. Four rather than every core, because the
        /// automatic sizes walk while somebody is using the window.
        /// </summary>
        public const int WalkThreads = 4;

        /// <summary>
        /// One measurement, shared by up to <see cref="WalkThreads"/> threads
        /// taking folders off one stack. An explicit stack, not recursion: a
        /// recursive walk blows the stack on pathological trees. The thread that
        /// asked does the first folder, and helpers join only once there is more
        /// than one folder waiting, so the many small folders an automatic sweep
        /// measures never start a thread at all.
        /// </summary>
        private sealed class TreeWalk
        {
            private readonly object _gate = new();
            private readonly Stack<(string Path, int Depth)> _stack = new();
            private readonly int _maxDepth;
            private readonly Func<string, bool>? _skip;
            private readonly DateTime _deadline;
            private readonly CancellationToken _token;
            private readonly List<Task> _helpers = new();

            /// <summary>Folders taken off the stack and not yet finished.</summary>
            private int _busy;
            private volatile bool _stop;
            private volatile bool _complete = true;
            private Exception? _failure;

            private long _bytes;
            private int _files, _folders;

            public long Bytes => Interlocked.Read(ref _bytes);
            public int Files => Volatile.Read(ref _files);
            public int Folders => Volatile.Read(ref _folders);
            public bool Complete => _complete;

            public TreeWalk(string root, int maxDepth, Func<string, bool>? skip, DateTime deadline, CancellationToken token)
            {
                _maxDepth = maxDepth;
                _skip = skip;
                _deadline = deadline;
                _token = token;
                _stack.Push((root, 0));
            }

            public void Run()
            {
                Work();

                Task[] helpers;
                lock (_gate) helpers = _helpers.ToArray();
                try { Task.WaitAll(helpers); } catch { }

                // What the single-threaded walk would have thrown, it still throws.
                if (_failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(_failure);
            }

            private void Work()
            {
                var found = new List<(string Path, int Depth)>();
                long bytes = 0;
                int files = 0, folders = 0;

                try
                {
                    while (true)
                    {
                        (string Path, int Depth) next;
                        lock (_gate)
                        {
                            while (_stack.Count == 0 && _busy > 0 && !_stop) Monitor.Wait(_gate);
                            if (_stop || _stack.Count == 0) { Monitor.PulseAll(_gate); return; }
                            next = _stack.Pop();
                            _busy++;
                        }

                        found.Clear();
                        try
                        {
                            if (_token.IsCancellationRequested) { _complete = false; _stop = true; }
                            else if (DateTime.UtcNow > _deadline) { _complete = false; _stop = true; }
                            else Read(next.Path, next.Depth, found, ref bytes, ref files, ref folders);
                        }
                        catch (Exception ex)
                        {
                            lock (_gate) _failure ??= ex;
                            _stop = true;
                        }

                        lock (_gate)
                        {
                            _busy--;
                            foreach (var folder in found) _stack.Push(folder);
                            if (_stack.Count > 1 && _helpers.Count < WalkThreads - 1 && !_stop)
                                _helpers.Add(Task.Run(Work));
                            Monitor.PulseAll(_gate);
                        }
                    }
                }
                finally
                {
                    Interlocked.Add(ref _bytes, bytes);
                    Interlocked.Add(ref _files, files);
                    Interlocked.Add(ref _folders, folders);
                }
            }

            /// <summary>One folder: its files counted, its subfolders handed back to be walked.</summary>
            private void Read(string current, int depth, List<(string Path, int Depth)> found,
                ref long bytes, ref int files, ref int folders)
            {
                // Checking the clock is a syscall, and on a folder of a hundred
                // thousand files doing it per entry costs more than the walk. Once
                // every few thousand entries is still well inside a second.
                const int DeadlineCheckInterval = 4096;
                int sinceClockCheck = 0;

                // The attributes and the length come straight out of the
                // directory scan that found the entry. The old shape asked the
                // filesystem again for each of those — three round trips per file
                // where one will do, which is the whole cost of this walk on a
                // network share. And nothing is made of a file but its length:
                // a FileInfo per file, two strings and an object, was the rest
                // of the cost on a local disk.
                IEnumerable<Seen> entries;
                try
                {
                    entries = new System.IO.Enumeration.FileSystemEnumerable<Seen>(current,
                        static (ref System.IO.Enumeration.FileSystemEntry entry) => Look(ref entry), WalkOptions);
                }
                catch { _complete = false; return; } // unreadable subtree, keep going

                IEnumerator<Seen> walker;
                try { walker = entries.GetEnumerator(); }
                catch { _complete = false; return; }

                using (walker)
                {
                    while (true)
                    {
                        // A directory can become unreadable part-way through, and
                        // MoveNext is where that surfaces. Losing the rest of one
                        // folder is not a reason to abandon the whole tree.
                        try { if (!walker.MoveNext()) break; }
                        catch { _complete = false; break; }

                        if (_stop) return;
                        if (_token.IsCancellationRequested) { _complete = false; _stop = true; return; }

                        if (++sinceClockCheck >= DeadlineCheckInterval)
                        {
                            sinceClockCheck = 0;
                            if (DateTime.UtcNow > _deadline) { _complete = false; _stop = true; return; }
                        }

                        var entry = walker.Current;

                        // Junctions and symlinks would otherwise double-count or loop.
                        // Only those: a cloud placeholder is a reparse point too, and
                        // skipping them measured a Drive or OneDrive folder as empty.
                        // Decided in Look, where the entry still is.
                        if (entry.Link) continue;

                        if (entry.Folder != null)
                        {
                            folders++;
                            if (_skip != null && _skip(entry.Folder)) continue;
                            // MaxDepth counts levels actually walked: 1 means the
                            // root's own files only, 2 adds its immediate subfolders.
                            if (depth + 1 < _maxDepth) found.Add((entry.Folder, depth + 1));
                            else _complete = false;
                        }
                        else
                        {
                            bytes += entry.Length;
                            files++;
                        }
                    }
                }
            }
        }

        /// <summary>One entry as the walk needs it: a link to step over, a folder to go into, or a file's length.</summary>
        private readonly record struct Seen(bool Link, string? Folder, long Length);

        /// <summary>
        /// Everything, nothing skipped, an unreadable folder an exception: what
        /// <c>DirectoryInfo.EnumerateFileSystemInfos()</c> asks for.
        /// </summary>
        private static readonly EnumerationOptions WalkOptions = new()
        {
            MatchType = MatchType.Win32,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
        };

        private static Seen Look(ref System.IO.Enumeration.FileSystemEntry entry)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && NameRules.IsLink(entry.ToFileSystemInfo()))
                return new Seen(true, null, 0);
            return entry.IsDirectory
                ? new Seen(false, entry.ToFullPath(), 0)
                : new Seen(false, null, entry.Length);
        }

        public void Dispose()
        {
            Volatile.Write(ref _disposed, true);
            // Cancelled, not disposed: walks still running hold this token, and
            // disposing it out from under them is how a background thread ends up
            // throwing during shutdown. It owns no unmanaged handle to release.
            try { Volatile.Read(ref _cts).Cancel(); } catch { }
        }
    }
}
