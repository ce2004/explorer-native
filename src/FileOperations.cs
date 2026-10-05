using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    public sealed record FileOpProgress(
        long BytesDone,
        long BytesTotal,
        int ItemsDone,
        int ItemsTotal,
        string CurrentItem);

    /// <summary>
    /// What the conflict policy actually did, by name.
    ///
    /// Counts alone cannot say "Saved as song (2).flac", and that sentence is
    /// the entire value of announcing a rename: a number tells you something
    /// happened to a file you can no longer find, a name tells you where it
    /// went. The lists are capped because the interesting case is one or two
    /// collisions; a paste of ten thousand files over ten thousand files does
    /// not need ten thousand strings kept to say "9,998 more".
    /// </summary>
    public sealed record ConflictOutcomes(
        IReadOnlyList<string> Renamed,
        IReadOnlyList<string> Skipped,
        IReadOnlyList<string> Overwritten,
        int RenamedCount,
        int SkippedCount,
        int OverwrittenCount)
    {
        public const int MaxRemembered = 32;

        public static readonly ConflictOutcomes None = new(
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), 0, 0, 0);

        public int Total => RenamedCount + SkippedCount + OverwrittenCount;

        // Plus() merged two of these and nothing ever called it: the collecting
        // is done by ConflictLog while the plan is built, so there are never two
        // sets to add together.
    }

    /// <summary>Collects conflict outcomes while a plan is being built.</summary>
    internal sealed class ConflictLog
    {
        private readonly List<string> _renamed = new();
        private readonly List<string> _skipped = new();
        private readonly List<string> _overwritten = new();

        public int RenamedCount, SkippedCount, OverwrittenCount;

        public void Renamed(string path) { RenamedCount++; Remember(_renamed, path); }
        public void Skipped(string path) { SkippedCount++; Remember(_skipped, path); }
        public void Overwritten(string path) { OverwrittenCount++; Remember(_overwritten, path); }

        private static void Remember(List<string> into, string path)
        {
            if (into.Count >= ConflictOutcomes.MaxRemembered) return;
            into.Add(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        }

        public ConflictOutcomes Snapshot() => new(
            _renamed.ToArray(), _skipped.ToArray(), _overwritten.ToArray(),
            RenamedCount, SkippedCount, OverwrittenCount);
    }

    public sealed record FileOpResult(
        int Succeeded,
        int Skipped,
        int Failed,
        bool Cancelled,
        IReadOnlyList<string> Errors,
        ConflictOutcomes? Conflicts = null)
    {
        /// <summary>Never null, so a caller can read it without a guard.</summary>
        public ConflictOutcomes Collisions => Conflicts ?? ConflictOutcomes.None;
    }

    /// <summary>
    /// Batch copy/move that runs off the UI thread.
    ///
    /// Files are processed with bounded parallelism, which matters a lot on a
    /// high-latency network share: serialised copies there spend most of their
    /// time waiting on round trips, so several in flight is dramatically faster
    /// than one at a time. Progress is throttled before it reaches the UI so a
    /// batch of thousands of small files doesn't drown the message pump.
    /// </summary>
    public static class FileOperations
    {
        /// <summary>
        /// How many files to keep in flight at once, worked out from the machine
        /// rather than asked of the user.
        ///
        /// Copying is bound by the disk or the network, not by the processor, so
        /// the aim is enough requests outstanding to keep the device busy. The
        /// core count is a good proxy for how much a machine can usefully have in
        /// flight, and the floor and ceiling are what stop it being silly: fewer
        /// than four leaves a high-latency share idle between round trips, and
        /// more than sixteen only adds contention — on a spinning disk it makes
        /// things actively worse by turning one sequential read into a seek
        /// storm. Robocopy's own default of eight sits inside this range.
        /// </summary>
        public static int RecommendedThreads => Math.Clamp(Environment.ProcessorCount, 4, 16);

        private sealed record PlannedCopy(string Source, string Destination, long Size);

        /// <summary>
        /// Folders the plan must create even though no file lands in them.
        ///
        /// The plan is a list of files, and directories were only ever created as
        /// a side effect of writing one. A folder that is empty — or whose whole
        /// subtree is empty — therefore vanished from the copy without a word: no
        /// error, no failed count, just a structure that quietly came out
        /// different from the one that went in. Robocopy keeps them (that is what
        /// /E means), so a paste that happened to hit a name collision, and was
        /// routed through here to be renamed, silently lost folders that the very
        /// same paste would have kept a moment earlier.
        /// </summary>
        private sealed record PlannedDirectory(string Destination);

        public static async Task<FileOpResult> RunAsync(
            IReadOnlyList<string> sources,
            string destinationDir,
            bool move,
            PasteConflictPolicy conflictPolicy,
            int threads,
            int bufferKilobytes,
            IProgress<FileOpProgress>? progress,
            Func<string, ConflictChoice>? askConflict,
            CancellationToken token,
            IEnumerable<string>? reserved = null)
        {
            var errors = new ConcurrentBag<string>();
            int succeeded = 0, skipped = 0, failed = 0;

            // ---- Plan first, so total bytes/items are known up front ----
            // This walks whole trees, so it must not run on the UI thread: doing
            // the planning inline would freeze the window before the progress
            // dialog even appeared.
            List<PlannedCopy> plan;
            List<PlannedDirectory> plannedDirectories = new();

            // Filled in as the plan is built, and read afterwards even when the
            // plan throws: a paste cancelled at the fourth collision has still
            // skipped three, and reporting nothing there is how a partly-done
            // operation looks like one that never started.
            var conflicts = new ConflictLog();
            var unreadable = new List<string>();
            try
            {
                var outcome = await Task.Run(() =>
                {
                    var dirs = new List<PlannedDirectory>();
                    var built = BuildPlan(sources, destinationDir, conflictPolicy, askConflict, conflicts, dirs, token,
                        reserved, unreadable);
                    return (Files: built, Directories: dirs);

                // Not back to the caller's thread: what follows creates every
                // planned folder and, for a move, walks and deletes the sources —
                // filesystem work that froze the window on a slow share. Nothing
                // below touches the UI; progress marshals itself.
                }, token).ConfigureAwait(false);
                plan = outcome.Files;
                plannedDirectories = outcome.Directories;
                skipped += conflicts.SkippedCount;

                // A folder inside the tree that could not be listed. It was
                // created empty at the destination and the copy reported
                // success, and a move then quietly left the source behind.
                foreach (var dir in unreadable)
                {
                    failed++;
                    errors.Add($"{dir}: could not be read, so nothing in it was copied");
                }
            }
            catch (OperationCanceledException)
            {
                return new FileOpResult(0, skipped + conflicts.SkippedCount, 0, true,
                    Array.Empty<string>(), conflicts.Snapshot());
            }
            catch (Exception ex)
            {
                // A refused plan (copying a folder into itself, an unreadable
                // source) is a failed operation to report, not an exception to
                // throw at the caller mid-await.
                return new FileOpResult(0, skipped + conflicts.SkippedCount, 1, false,
                    new[] { ex.Message }, conflicts.Snapshot());
            }

            long totalBytes = 0;
            foreach (var p in plan) totalBytes += p.Size;
            int totalItems = plan.Count;

            long bytesDone = 0;
            int itemsDone = 0;
            long lastReportTicks = 0;

            void Report(string current)
            {
                if (progress == null) return;
                // Throttle to ~10 updates/second; the UI cannot use more than that.
                long now = Environment.TickCount64;
                long last = Interlocked.Read(ref lastReportTicks);
                if (now - last < 100) return;
                Interlocked.Exchange(ref lastReportTicks, now);
                progress.Report(new FileOpProgress(
                    Interlocked.Read(ref bytesDone), totalBytes,
                    Volatile.Read(ref itemsDone), totalItems, current));
            }

            int parallelism = Math.Clamp(threads, 1, 32);
            int bufferSize = Math.Clamp(bufferKilobytes, 4, 16384) * 1024;

            // Created before the files, so an empty folder survives the copy and
            // so a folder that only contains empty folders does too.
            foreach (var directory in plannedDirectories)
            {
                if (token.IsCancellationRequested) break;
                try { Directory.CreateDirectory(directory.Destination); }
                catch (Exception ex)
                {
                    // Counted, not only recorded. A folder that files land in
                    // fails those files as well, so the count came out right by
                    // accident — but an empty one, which is the whole reason
                    // directories are planned separately, failed silently: an
                    // error string nobody reads, because the caller only shows
                    // them when Failed is non-zero, and a copy reporting complete
                    // success with a folder missing from it.
                    Interlocked.Increment(ref failed);
                    errors.Add($"{directory.Destination}: {ex.Message}");
                }
            }

            try
            {
                await Parallel.ForEachAsync(
                    plan,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = token },
                    async (item, ct) =>
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);

                            if (move && SameVolume(item.Source, item.Destination))
                            {
                                // Same-volume move is a rename: no bytes travel.
                                File.Move(item.Source, item.Destination, overwrite: true);
                            }
                            else
                            {
                                await CopyFileAsync(item.Source, item.Destination, bufferSize,
                                    copied => { Interlocked.Add(ref bytesDone, copied); Report(item.Source); }, ct);

                                if (move)
                                {
                                    // A move that left the original behind is not a
                                    // move, and the error only reaches anybody when
                                    // something has been counted as failed.
                                    try { File.Delete(item.Source); }
                                    catch (Exception ex)
                                    {
                                        Interlocked.Increment(ref failed);
                                        errors.Add($"{item.Source}: copied but the original could not be removed ({ex.Message})");
                                        return;
                                    }
                                }
                            }

                            Interlocked.Increment(ref succeeded);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref failed);
                            errors.Add($"{item.Source}: {ex.Message}");
                        }
                        finally
                        {
                            Interlocked.Increment(ref itemsDone);
                            Report(item.Source);
                        }
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new FileOpResult(succeeded, skipped, failed, true, new List<string>(errors), conflicts.Snapshot());
            }

            // A move leaves the source directory skeletons behind; clear the empties.
            if (move)
            {
                foreach (var src in sources)
                {
                    try
                    {
                        if (Directory.Exists(src) && IsEffectivelyEmpty(src))
                            Directory.Delete(src, recursive: true);
                    }
                    catch (Exception ex) { errors.Add($"{src}: {ex.Message}"); }
                }
            }

            progress?.Report(new FileOpProgress(totalBytes, totalBytes, totalItems, totalItems, ""));
            return new FileOpResult(succeeded, skipped, failed, false, new List<string>(errors), conflicts.Snapshot());
        }

        public enum ConflictChoice { Overwrite, Skip, Rename, Cancel, FillGaps }

        private static List<PlannedCopy> BuildPlan(
            IReadOnlyList<string> sources,
            string destinationDir,
            PasteConflictPolicy policy,
            Func<string, ConflictChoice>? askConflict,
            ConflictLog conflicts,
            List<PlannedDirectory> directories,
            CancellationToken token,
            IEnumerable<string>? reserved = null,
            List<string>? unreadable = null)
        {
            var plan = new List<PlannedCopy>();

            // Every top-level destination this paste has already given out, and
            // any the caller is about to use. Two sources with one name — two
            // "photos" picked out of search results — were each checked against
            // the disk alone, found the same free name, and were copied on top
            // of each other; on a move, the first was then deleted.
            var claimed = new HashSet<string>(reserved ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();

                var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) continue;

                if (Directory.Exists(source))
                {
                    var destRoot = Path.Combine(destinationDir, name);

                    // The conflict is resolved *before* the recursion check, and the
                    // order is the whole point.
                    //
                    // Pasting a folder back into the folder it lives in is how you
                    // duplicate one, and it is an entirely ordinary thing to do. But
                    // the desired destination is then the source itself, so checking
                    // for recursion first saw a folder being copied into itself and
                    // refused — the paste reported a failure and produced nothing.
                    // Auto-rename turns the destination into "name (2)" first, which
                    // is not inside the source at all, and the copy is fine.
                    destRoot = ResolveConflict(destRoot, policy, askConflict, conflicts, claimed, true, out bool skip);
                    if (skip) continue;
                    claimed.Add(destRoot);

                    // Still refused once renaming has had its say: copying a folder
                    // into its own subtree really would recurse forever, and asking
                    // to overwrite a folder with itself is not a copy at all.
                    if (IsWithin(destRoot, source))
                        throw new IOException($"Cannot copy \"{name}\" into itself.");

                    // The root itself, so copying an entirely empty folder still
                    // produces a folder rather than nothing at all.
                    directories.Add(new PlannedDirectory(destRoot));

                    foreach (var directory in SafeEnumerateDirectories(source))
                    {
                        token.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(source, directory);
                        directories.Add(new PlannedDirectory(Path.Combine(destRoot, relative)));
                    }

                    foreach (var file in SafeEnumerateFiles(source, unreadable))
                    {
                        token.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(source, file);
                        var dest = Path.Combine(destRoot, relative);
                        long size = 0;
                        try { size = new FileInfo(file).Length; } catch { }
                        plan.Add(new PlannedCopy(file, dest, size));
                    }
                }
                else if (File.Exists(source))
                {
                    var dest = Path.Combine(destinationDir, name);
                    dest = ResolveConflict(dest, policy, askConflict, conflicts, claimed, false, out bool skip);
                    if (skip) continue;
                    claimed.Add(dest);

                    long size = 0;
                    try { size = new FileInfo(source).Length; } catch { }
                    plan.Add(new PlannedCopy(source, dest, size));
                }
            }

            return plan;
        }

        private static string ResolveConflict(
            string desired,
            PasteConflictPolicy policy,
            Func<string, ConflictChoice>? askConflict,
            ConflictLog conflicts,
            HashSet<string> claimed,
            bool folder,
            out bool skip)
        {
            skip = false;

            // Another item of this same paste is going there. Whatever the policy
            // says about what was already on disk, the answer for two things
            // arriving together is to keep both.
            if (claimed.Contains(desired))
            {
                var numbered = UniqueName(desired, claimed, folder);
                conflicts.Renamed(numbered);
                return numbered;
            }

            if (!File.Exists(desired) && !Directory.Exists(desired)) return desired;

            var effective = policy;
            if (policy == PasteConflictPolicy.Ask)
            {
                var choice = askConflict?.Invoke(desired) ?? ConflictChoice.Rename;
                effective = choice switch
                {
                    ConflictChoice.Overwrite => PasteConflictPolicy.Overwrite,
                    ConflictChoice.Skip => PasteConflictPolicy.Skip,
                    ConflictChoice.Cancel => throw new OperationCanceledException(),
                    _ => PasteConflictPolicy.AutoRename,
                };
            }

            switch (effective)
            {
                case PasteConflictPolicy.Overwrite:
                    conflicts.Overwritten(desired);
                    return desired;
                case PasteConflictPolicy.Skip:
                    skip = true;
                    conflicts.Skipped(desired);
                    return desired;
                default:
                    // The *new* name is what is worth saying — "Saved as
                    // song (2).flac" — not the one that was already taken.
                    var unique = UniqueName(desired, claimed, folder);
                    conflicts.Renamed(unique);
                    return unique;
            }
        }

        /// <summary>
        /// A path nothing is using, by numbering the name.
        ///
        /// The numbering itself lives in <c>NameRules.UniqueAmong</c>, because the
        /// Drive letter needs exactly the same rule against a different question —
        /// there "already taken" is the folder's listing, not the filesystem — and
        /// two copies of "a folder has no extension whatever its dots say" is two
        /// copies to get wrong.
        /// </summary>
        public static string UniqueName(string desired, ISet<string>? claimed = null, bool? folder = null)
        {
            bool Taken(string path) =>
                (claimed != null && claimed.Contains(path)) || File.Exists(path) || Directory.Exists(path);

            if (!Taken(desired)) return desired;

            var dir = Path.GetDirectoryName(desired)!;

            // Whether what is *arriving* is a folder, when the caller knows: a new
            // notes.txt beside a folder called notes.txt is a file, and is
            // numbered as one.
            bool isFolder = folder ?? Directory.Exists(desired);

            var unique = NameRules.UniqueAmong(
                Path.GetFileName(desired),
                name => Taken(Path.Combine(dir, name)),
                isFolder);

            return Path.Combine(dir, unique);
        }

        private static async Task CopyFileAsync(
            string source, string destination, int bufferSize,
            Action<long> onChunk, CancellationToken token)
        {
            await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[bufferSize];
            int read;
            bool complete = false;

            try
            {
                while ((read = await src.ReadAsync(buffer.AsMemory(0, bufferSize), token)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), token);
                    onChunk(read);
                }

                // Written out before it is called complete: the last piece sits in
                // the buffer, and a full disk refusing it at close used to leave a
                // short file that the cleanup below had already been told to keep.
                await dst.FlushAsync(token);

                // Through the writing handle, and after the flush. Set by path
                // while the handle was still open, the time was overwritten when
                // the buffered tail went out at close — every copy dated "now".
                try { File.SetLastWriteTimeUtc(dst.SafeFileHandle, File.GetLastWriteTimeUtc(src.SafeFileHandle)); }
                catch { }

                complete = true;
            }
            finally
            {
                // A cancelled copy takes its own half-written file with it.
                //
                // The destination is opened FileMode.Create, so it exists and is
                // being filled from the first chunk. Cancelling left it there —
                // under a name nothing flags, usually "song (2).flac", since this
                // engine is the one that handles collisions by renaming — with
                // the copy reported as cancelled and no cleanup anywhere. The
                // robocopy path has removed its half-written files since the
                // 20GB incident; this one never did.
                if (!complete)
                {
                    try { await dst.DisposeAsync(); } catch { }
                    try { File.Delete(destination); } catch { }
                }
            }
        }

        /// <summary>Every directory beneath the root, junctions not followed.</summary>
        private static IEnumerable<string> SafeEnumerateDirectories(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var dir = stack.Pop();

                string[] subDirs;
                try { subDirs = Directory.GetDirectories(dir); }
                catch { continue; }

                foreach (var sub in subDirs)
                {
                    try
                    {
                        // A junction is not copied, so its destination is not
                        // created either — the same rule the file walk uses.
                        if (NameRules.IsLink(new DirectoryInfo(sub))) continue;
                    }
                    catch { continue; }

                    stack.Push(sub);
                    yield return sub;
                }
            }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string root, List<string>? unreadable = null)
        {
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var dir = stack.Pop();

                string[] subDirs;
                try { subDirs = Directory.GetDirectories(dir); }
                catch { unreadable?.Add(dir); continue; }

                foreach (var sub in subDirs)
                {
                    try
                    {
                        // Don't follow junctions: they lead out of the tree or back into it.
                        if (NameRules.IsLink(new DirectoryInfo(sub))) continue;
                    }
                    catch { continue; }
                    stack.Push(sub);
                }

                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { unreadable?.Add(dir); continue; }

                foreach (var f in files) yield return f;
            }
        }

        private static bool IsEffectivelyEmpty(string dir)
        {
            try
            {
                foreach (var _ in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    return false;
                return true;
            }
            catch { return false; }
        }

        private static bool IsWithin(string candidate, string ancestor)
        {
            try
            {
                var a = Path.GetFullPath(ancestor).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return c.StartsWith(a, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool SameVolume(string a, string b)
        {
            try
            {
                var ra = Path.GetPathRoot(Path.GetFullPath(a));
                var rb = Path.GetPathRoot(Path.GetFullPath(b));
                return string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
