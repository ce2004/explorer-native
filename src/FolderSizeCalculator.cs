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
        /// Folders the walk must not go into, however they are reached. The phone
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

            long bytes = 0;
            int files = 0, folders = 0;
            bool complete = true;

            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, TimeoutSeconds));

            // Iterative walk: a recursive one blows the stack on pathological trees.
            var stack = new Stack<(string Path, int Depth)>();
            stack.Push((root, 0));

            // Checking the clock is a syscall, and on a folder of a hundred
            // thousand files doing it per entry costs more than the walk. Once
            // every few thousand entries is still well inside a second.
            const int DeadlineCheckInterval = 4096;
            int sinceClockCheck = 0;

            while (stack.Count > 0)
            {
                if (token.IsCancellationRequested) return new FolderSizeResult(bytes, files, folders, false);
                if (DateTime.UtcNow > deadline) { complete = false; break; }

                var (current, depth) = stack.Pop();

                // EnumerateFileSystemInfos carries the attributes and the length
                // straight out of the directory scan that found the entry. The
                // old shape asked the filesystem again for each of those — three
                // round trips per file where one will do, which is the whole cost
                // of this walk on a network share.
                IEnumerable<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(current).EnumerateFileSystemInfos(); }
                catch { complete = false; continue; } // unreadable subtree, keep going

                IEnumerator<FileSystemInfo> walker;
                try { walker = entries.GetEnumerator(); }
                catch { complete = false; continue; }

                using (walker)
                {
                    while (true)
                    {
                        // A directory can become unreadable part-way through, and
                        // MoveNext is where that surfaces. Losing the rest of one
                        // folder is not a reason to abandon the whole tree.
                        try { if (!walker.MoveNext()) break; }
                        catch { complete = false; break; }

                        if (token.IsCancellationRequested)
                            return new FolderSizeResult(bytes, files, folders, false);

                        if (++sinceClockCheck >= DeadlineCheckInterval)
                        {
                            sinceClockCheck = 0;
                            if (DateTime.UtcNow > deadline) { complete = false; goto done; }
                        }

                        var entry = walker.Current;

                        FileAttributes attrs;
                        try { attrs = entry.Attributes; }
                        catch { complete = false; continue; }

                        // Junctions and symlinks would otherwise double-count or loop.
                        // Only those: a cloud placeholder is a reparse point too, and
                        // skipping them measured a Drive or OneDrive folder as empty.
                        if ((attrs & FileAttributes.ReparsePoint) != 0 && NameRules.IsLink(entry)) continue;

                        if ((attrs & FileAttributes.Directory) != 0)
                        {
                            folders++;
                            if (Skip != null && Skip(entry.FullName)) continue;
                            // MaxDepth counts levels actually walked: 1 means the
                            // root's own files only, 2 adds its immediate subfolders.
                            if (depth + 1 < MaxDepth) stack.Push((entry.FullName, depth + 1));
                            else complete = false;
                        }
                        else if (entry is FileInfo file)
                        {
                            try
                            {
                                bytes += file.Length;
                                files++;
                            }
                            catch { complete = false; }
                        }
                    }
                }
            }
        done:

            // Only a complete walk is worth remembering: a partial answer cached
            // would be handed back for ever as though it were the real size.
            if (complete)
                _cache[root] = new CacheEntry(bytes, files, folders, stamp, Environment.TickCount64);

            return new FolderSizeResult(bytes, files, folders, complete);
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
