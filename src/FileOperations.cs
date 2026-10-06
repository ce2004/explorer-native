using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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

        /// <param name="Rename">
        /// A move that can be a rename, decided once for the top-level item the
        /// file belongs to. It was asked per file, at about 0.8ms a call, which
        /// for a folder of thirty thousand files was most of half a minute of
        /// asking Windows the same question.
        /// </param>
        private sealed record PlannedCopy(string Source, string Destination, long Size, bool Rename = false);

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
            var extras = new PlanExtras();
            try
            {
                var outcome = await Task.Run(() =>
                {
                    var dirs = new List<PlannedDirectory>();
                    var built = BuildPlan(sources, destinationDir, conflictPolicy, askConflict, conflicts, dirs, token,
                        reserved, unreadable, move, extras);
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

                foreach (var gone in extras.Missing)
                {
                    failed++;
                    errors.Add($"{gone}: not found");
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

            // Selected links, moved as links. On a worker: recreating one on
            // another volume is filesystem work like the rest.
            if (extras.Links.Count > 0)
            {
                await Task.Run(() =>
                {
                    foreach (var (link, to) in extras.Links)
                    {
                        if (token.IsCancellationRequested) break;
                        try { MoveLink(link, to); succeeded++; }
                        catch (Exception ex)
                        {
                            failed++;
                            errors.Add($"{link}: the link could not be moved ({ex.Message})");
                        }
                    }
                }).ConfigureAwait(false);
            }

            // Created before the files, so an empty folder survives the copy and
            // so a folder that only contains empty folders does too.
            foreach (var directory in plannedDirectories)
            {
                if (token.IsCancellationRequested) break;
                try { Directory.CreateDirectory(NameRules.ExactPath(directory.Destination)); }
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
                            Directory.CreateDirectory(NameRules.ExactPath(Path.GetDirectoryName(item.Destination)!));

                            if (item.Rename)
                            {
                                // Same-volume move is a rename: no bytes travel.
                                File.Move(NameRules.ExactPath(item.Source), NameRules.ExactPath(item.Destination), overwrite: true);
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
                                    try
                                    {
                                        // Read-only is copied now, and a read-only
                                        // original refuses the delete that ends a move.
                                        var original = NameRules.ExactPath(item.Source);
                                        var attributes = File.GetAttributes(original);
                                        if (attributes.HasFlag(FileAttributes.ReadOnly))
                                            File.SetAttributes(original, attributes & ~FileAttributes.ReadOnly);
                                        File.Delete(original);
                                    }
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
            //
            // Only folders whose tree this move planned. Every source used to be
            // tried, and an empty folder that was skipped, or already where it
            // was being moved to, was deleted for being empty.
            if (move)
            {
                foreach (var src in extras.MovedFolders)
                {
                    try
                    {
                        var exact = NameRules.ExactPath(src);
                        if (Directory.Exists(exact) && !IsLinkAt(exact) && IsEffectivelyEmpty(exact))
                            Directory.Delete(exact, recursive: true);
                    }
                    catch (Exception ex) { errors.Add($"{src}: {ex.Message}"); }
                }
            }

            progress?.Report(new FileOpProgress(totalBytes, totalBytes, totalItems, totalItems, ""));
            return new FileOpResult(succeeded, skipped, failed, false, new List<string>(errors), conflicts.Snapshot());
        }

        public enum ConflictChoice { Overwrite, Skip, Rename, Cancel, FillGaps }

        /// <summary>What the plan decided beyond the files themselves.</summary>
        private sealed class PlanExtras
        {
            /// <summary>Sources that are neither a file nor a folder any more.</summary>
            public readonly List<string> Missing = new();

            /// <summary>Selected links being moved, and where each goes.</summary>
            public readonly List<(string Source, string Destination)> Links = new();

            /// <summary>Folders whose tree was planned, the only ones a move may tidy away.</summary>
            public readonly List<string> MovedFolders = new();
        }

        private static List<PlannedCopy> BuildPlan(
            IReadOnlyList<string> sources,
            string destinationDir,
            PasteConflictPolicy policy,
            Func<string, ConflictChoice>? askConflict,
            ConflictLog conflicts,
            List<PlannedDirectory> directories,
            CancellationToken token,
            IEnumerable<string>? reserved = null,
            List<string>? unreadable = null,
            bool move = false,
            PlanExtras? extras = null)
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

                // A move into the folder it is already in is nothing to do. Under
                // keep both it used to be a collision with itself, and cut and
                // paste in one folder renamed everything to "a (2)".
                if (move && SamePath(Path.Combine(destinationDir, name), source)) continue;

                // Exact, because "report." and "report" are two files and an
                // ordinary path cannot tell them apart.
                var exact = NameRules.ExactPath(source);

                // A selected link is moved as itself. Planned as a folder, the
                // walk went through it and the move deleted what it pointed at.
                if (move && extras != null && IsLinkAt(exact))
                {
                    var linkDest = ResolveConflict(Path.Combine(destinationDir, name), policy, askConflict,
                        conflicts, claimed, Directory.Exists(exact), out bool skipLink);
                    if (skipLink) continue;
                    claimed.Add(linkDest);
                    extras.Links.Add((source, linkDest));
                    continue;
                }

                if (Directory.Exists(exact))
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
                    extras?.MovedFolders.Add(source);

                    foreach (var directory in SafeEnumerateDirectories(source))
                    {
                        token.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(source, directory);
                        directories.Add(new PlannedDirectory(Path.Combine(destRoot, relative)));
                    }

                    bool rename = move && SameVolume(source, destRoot);
                    foreach (var file in SafeEnumerateFiles(source, unreadable))
                    {
                        token.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(source, file);
                        var dest = Path.Combine(destRoot, relative);
                        long size = 0;
                        try { size = new FileInfo(NameRules.ExactPath(file)).Length; } catch { }
                        plan.Add(new PlannedCopy(file, dest, size, rename));
                    }
                }
                else if (File.Exists(exact))
                {
                    var dest = Path.Combine(destinationDir, name);
                    dest = ResolveConflict(dest, policy, askConflict, conflicts, claimed, false, out bool skip);
                    if (skip) continue;
                    claimed.Add(dest);

                    long size = 0;
                    try { size = new FileInfo(exact).Length; } catch { }
                    plan.Add(new PlannedCopy(source, dest, size, move && SameVolume(source, dest)));
                }

                // Neither a file nor a folder: gone since it was picked. This had
                // no branch at all, and a paste of nothing said "Copy complete".
                else extras?.Missing.Add(source);
            }

            return plan;
        }

        internal static bool IsLinkAt(string exactPath)
        {
            try
            {
                FileSystemInfo entry = Directory.Exists(exactPath)
                    ? new DirectoryInfo(exactPath)
                    : new FileInfo(exactPath);
                return entry.Exists && NameRules.IsLink(entry);
            }
            catch { return false; }
        }

        /// <summary>Whether two paths name the same place, as written.</summary>
        internal static bool SamePath(string a, string b)
        {
            static string Norm(string p) => NameRules.PlainPath(p)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Moves a junction or symbolic link itself, never what it points at.
        ///
        /// A rename when both ends are on one volume. Otherwise the link is made
        /// again at the destination, pointing at the same place, and only then
        /// is the original link removed — removing a link does not touch its
        /// target, which is the whole point.
        /// </summary>
        public static void MoveLink(string link, string destination)
        {
            var from = NameRules.ExactPath(link);
            var to = NameRules.ExactPath(destination);

            if (RoboCopyEngine.TryRename(from, to)) return;

            bool folder = Directory.Exists(from);
            RecreateLink(link, destination);

            if (folder) Directory.Delete(from, recursive: false);
            else File.Delete(from);
        }

        /// <summary>
        /// Makes <paramref name="destination"/> a link to whatever
        /// <paramref name="link"/> points at: a junction for a junction, a
        /// symbolic link for a symbolic link. A relative target is resolved
        /// against the original's folder, because it would mean something else
        /// from anywhere else.
        /// </summary>
        internal static void RecreateLink(string link, string destination)
        {
            var from = NameRules.ExactPath(link);
            var to = NameRules.ExactPath(destination);
            bool folder = Directory.Exists(from);

            FileSystemInfo entry = folder ? new DirectoryInfo(from) : new FileInfo(from);
            var target = entry.LinkTarget ?? throw new IOException($"{Path.GetFileName(link)} is not a link");

            if (!Path.IsPathFullyQualified(target))
                target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(NameRules.PlainPath(from))!, target));

            if (folder && ReparseLinks.IsJunction(from)) ReparseLinks.CreateJunction(to, target);
            else if (folder) Directory.CreateSymbolicLink(to, target);
            else File.CreateSymbolicLink(to, target);
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

            var exactDesired = NameRules.ExactPath(desired);
            if (!File.Exists(exactDesired) && !Directory.Exists(exactDesired)) return desired;

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
                (claimed != null && claimed.Contains(path)) ||
                File.Exists(NameRules.ExactPath(path)) || Directory.Exists(NameRules.ExactPath(path));

            if (!Taken(desired)) return desired;

            var dir = Path.GetDirectoryName(desired)!;

            // Whether what is *arriving* is a folder, when the caller knows: a new
            // notes.txt beside a folder called notes.txt is a file, and is
            // numbered as one.
            bool isFolder = folder ?? Directory.Exists(NameRules.ExactPath(desired));

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
            // Exact, so "report." is read and written as itself.
            var from = NameRules.ExactPath(source);
            var to = NameRules.ExactPath(destination);

            await using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None,
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
                await dst.DisposeAsync();

                // The rest of what robocopy keeps, which this engine — the one
                // that makes every " (2)" copy — used to drop: the alternate
                // streams, both dates and the attributes. The streams are part of
                // the file, so a copy without them is incomplete and goes the way
                // of any other incomplete copy.
                CopyStreams(from, to);
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
                    try { File.Delete(to); } catch { }
                }
            }

            // Dates after the streams, because writing a stream moves the file's
            // own last-write time; attributes last, because read-only would
            // refuse the dates. Set by path once every handle that writes is
            // closed: set through a handle with buffered data still to go out,
            // the time was overwritten at close and every copy was dated "now".
            CopyDatesAndAttributes(from, to);
        }

        /// <summary>Attributes a copy carries over, as robocopy's /COPY:DAT does.</summary>
        private const FileAttributes KeptAttributes =
            FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System |
            FileAttributes.Archive | FileAttributes.NotContentIndexed;

        private static void CopyDatesAndAttributes(string from, string to)
        {
            try
            {
                File.SetCreationTimeUtc(to, File.GetCreationTimeUtc(from));
                File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
            }
            catch { }

            try
            {
                var current = File.GetAttributes(to);
                var wanted = (current & ~KeptAttributes) | (File.GetAttributes(from) & KeptAttributes);
                if (wanted != current) File.SetAttributes(to, wanted);
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindStreamData
        {
            public long StreamSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
            public string StreamName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstStreamW(string fileName, int infoLevel, out FindStreamData data, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindNextStreamW(IntPtr find, out FindStreamData data);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr find);

        /// <summary>
        /// Copies every named data stream — a download's Zone.Identifier, a tag
        /// some program keeps beside the file — onto the copy. A filesystem with
        /// no streams to list has nothing to copy.
        ///
        /// Best effort, every step of it. FAT, exFAT and plenty of NAS shares have
        /// no streams at all, and refuse one being written; a stream another
        /// program holds cannot be read. Either used to throw out of here and
        /// fail — and delete — a keep-both copy whose file had arrived whole.
        /// The file is the copy; a stream that cannot come with it is left behind.
        /// </summary>
        internal static void CopyStreams(string from, string to)
        {
            var names = new List<string>();
            try
            {
                var find = FindFirstStreamW(from, 0, out var data, 0);
                if (find == new IntPtr(-1)) return;
                try
                {
                    do
                    {
                        if (!string.Equals(data.StreamName, "::$DATA", StringComparison.OrdinalIgnoreCase))
                            names.Add(data.StreamName);
                    }
                    while (FindNextStreamW(find, out data));
                }
                finally { FindClose(find); }
            }
            catch { return; }

            foreach (var name in names)
            {
                try
                {
                    using var input = new FileStream(from + name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var output = new FileStream(to + name, FileMode.Create, FileAccess.Write, FileShare.None);
                    input.CopyTo(output);
                }
                catch
                {
                    // Half a stream is worse than none.
                    try { File.Delete(to + name); } catch { }
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
                try { subDirs = Plain(Directory.GetDirectories(NameRules.ExactPath(dir))); }
                catch { continue; }

                foreach (var sub in subDirs)
                {
                    try
                    {
                        // A junction is not copied, so its destination is not
                        // created either — the same rule the file walk uses.
                        if (NameRules.IsLink(new DirectoryInfo(NameRules.ExactPath(sub)))) continue;
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
                try { subDirs = Plain(Directory.GetDirectories(NameRules.ExactPath(dir))); }
                catch { unreadable?.Add(dir); continue; }

                foreach (var sub in subDirs)
                {
                    try
                    {
                        // Don't follow junctions: they lead out of the tree or back into it.
                        if (NameRules.IsLink(new DirectoryInfo(NameRules.ExactPath(sub)))) continue;
                    }
                    catch { continue; }
                    stack.Push(sub);
                }

                string[] files;
                try { files = Plain(Directory.GetFiles(NameRules.ExactPath(dir))); }
                catch { unreadable?.Add(dir); continue; }

                foreach (var f in files) yield return f;
            }
        }

        /// <summary>Listed through the exact form, handed back in the ordinary one.</summary>
        private static string[] Plain(string[] paths)
        {
            for (int i = 0; i < paths.Length; i++) paths[i] = NameRules.PlainPath(paths[i]);
            return paths;
        }

        private static bool IsEffectivelyEmpty(string dir)
        {
            // Walked by hand, never through a link: AllDirectories went through
            // junctions, and one pointing back up the tree recursed until the
            // path was too long. A link is something in the folder, so a folder
            // holding one is not empty.
            try
            {
                var stack = new Stack<string>();
                stack.Push(dir);
                while (stack.Count > 0)
                {
                    foreach (var entry in new DirectoryInfo(stack.Pop()).EnumerateFileSystemInfos())
                    {
                        if (entry is not DirectoryInfo sub || NameRules.IsLink(sub)) return false;
                        stack.Push(sub.FullName);
                    }
                }
                return true;
            }
            catch { return false; }
        }

        private static bool IsWithin(string candidate, string ancestor)
        {
            try
            {
                // Not through GetFullPath when a name ends in a dot, which it
                // would strip: "a." would then contain everything in "a".
                static string Full(string p) =>
                    (NameRules.ExactPath(p) != p ? NameRules.PlainPath(p) : Path.GetFullPath(p))
                        .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (Full(candidate).StartsWith(Full(ancestor), StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }

            // By where both really are, too: a junction in the destination that
            // leads back inside the source made the copy walk into its own output.
            return ReparseLinks.ResolvedWithin(candidate, ancestor);
        }

        // By the volume, not the drive letter: a subst drive or a folder that
        // is a mount point puts one letter's paths on another volume.
        private static bool SameVolume(string a, string b) => RoboCopyEngine.SameVolume(a, b);
    }
}
