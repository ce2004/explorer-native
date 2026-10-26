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

        /// <summary>
        /// Replace asked for where a file would go over a folder of its name, or
        /// a folder over a file. Neither engine will put one over the other, so
        /// these are failures, not replacements: counted as "replaced" they were
        /// announced as destroyed and then failed.
        /// </summary>
        public readonly List<(string Path, bool FolderThere)> KindClashes = new();

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
        /// <param name="OnClash">
        /// What to do if, by the time the file is written, something has
        /// appeared at its destination (another transfer running at the same
        /// time, say). Only a plan that chose Replace may replace; keep both
        /// takes a fresh number, and Fill gaps or Skip leave what is there.
        /// </param>
        private sealed record PlannedCopy(string Source, string Destination, long Size, bool Rename = false,
            ClashRule OnClash = ClashRule.Rename);

        /// <summary>What a plan item does when its destination is taken at write time.</summary>
        private enum ClashRule { Rename, Skip, Overwrite }

        private static ClashRule ClashRuleFor(PasteConflictPolicy policy) => policy switch
        {
            PasteConflictPolicy.Overwrite => ClashRule.Overwrite,
            PasteConflictPolicy.Skip or PasteConflictPolicy.FillGaps => ClashRule.Skip,
            _ => ClashRule.Rename,
        };

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
                skipped += conflicts.SkippedCount + extras.AlreadyThere;

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

                foreach (var refusal in extras.Refused)
                {
                    failed++;
                    errors.Add(refusal);
                }

                foreach (var (clash, folderThere) in conflicts.KindClashes)
                {
                    failed++;
                    errors.Add($"{Path.GetFileName(clash)}: a {(folderThere ? "folder" : "file")} of that name is already there");
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

            // Folders moved whole, by one rename each, before anything is
            // copied. What was planned under each is then done: counted, and
            // taken out of the plan. One that Windows refuses is left to the
            // plan, file by file, exactly as before.
            if (extras.FolderRenames.Count > 0 && !token.IsCancellationRequested)
            {
                var renamedRoots = await Task.Run(() =>
                {
                    var done = new List<string>();
                    foreach (var (source, root) in extras.FolderRenames)
                    {
                        if (token.IsCancellationRequested) break;
                        if (RoboCopyEngine.TryRename(source, root)) done.Add(root);
                    }
                    return done;
                }).ConfigureAwait(false);

                if (renamedRoots.Count > 0)
                {
                    bool Under(string path)
                    {
                        foreach (var root in renamedRoots)
                            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                                (path.Length == root.Length || path[root.Length] == Path.DirectorySeparatorChar))
                                return true;
                        return false;
                    }

                    var rest = new List<PlannedCopy>(plan.Count);
                    foreach (var item in plan)
                    {
                        if (!Under(item.Destination)) { rest.Add(item); continue; }
                        succeeded++;
                        itemsDone++;
                        bytesDone += item.Size;
                    }
                    plan = rest;
                    plannedDirectories = plannedDirectories.FindAll(d => !Under(d.Destination));
                    extras.NestedLinks.RemoveAll(l => Under(l.Destination));
                    Report(renamedRoots[^1]);
                }
            }

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
            // Folders already made, so a file going into one does not ask for it
            // again: CreateDirectory is a request to the disk every time, even
            // for a folder that is there, and it was made once per file.
            var made = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

            foreach (var directory in plannedDirectories)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    Directory.CreateDirectory(NameRules.ExactPath(directory.Destination));
                    made.TryAdd(directory.Destination, 0);
                }
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

            // In runs of neighbouring files from one folder rather than file by
            // file: the workers taking the next file in the plan were all
            // creating files in the same folder at once, which Windows does one at
            // a time. See RunsByFolder.
            var runs = RunsByFolder(plan);

            try
            {
                await Parallel.ForEachAsync(
                    runs,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = token },
                    async (run, ct) =>
                    {
                        for (int i = run.Start; i < run.End; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            await CopyOne(plan[i], ct);
                        }
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new FileOpResult(succeeded, skipped, failed, true, new List<string>(errors), conflicts.Snapshot());
            }

            async ValueTask CopyOne(PlannedCopy item, CancellationToken ct)
            {
                try
                {
                    var parent = Path.GetDirectoryName(item.Destination)!;
                    if (!made.ContainsKey(parent))
                    {
                        Directory.CreateDirectory(NameRules.ExactPath(parent));
                        made.TryAdd(parent, 0);
                    }

                    string? landed;
                    if (item.Rename)
                    {
                        // Same-volume move is a rename: no bytes travel. Over a
                        // read-only file too, as the copy path does: Replace
                        // means replace, and File.Move alone refuses one.
                        // Only when Replace was chosen, though: anything else
                        // never moves over a file that appeared since the plan.
                        if (item.OnClash == ClashRule.Overwrite)
                        {
                            MoveReplacing(NameRules.ExactPath(item.Source), NameRules.ExactPath(item.Destination));
                            landed = item.Destination;
                        }
                        else landed = MoveWithoutReplacing(item.Source, item.Destination, item.OnClash);
                    }
                    else
                    {
                        landed = await CopyFileAsync(item.Source, item.Destination, bufferSize, item.Size,
                            copied => { Interlocked.Add(ref bytesDone, copied); Report(item.Source); }, ct,
                            item.OnClash);
                    }

                    // Something arrived at the destination after the plan, and
                    // Fill gaps or Skip leaves it alone; a move keeps its original.
                    if (landed == null)
                    {
                        Interlocked.Increment(ref skipped);
                        lock (conflicts) conflicts.Skipped(item.Destination);
                        return;
                    }

                    if (!string.Equals(landed, item.Destination, StringComparison.OrdinalIgnoreCase))
                        lock (conflicts) conflicts.Renamed(landed);

                    if (!item.Rename)
                    {
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
            }

            // Links inside the folders moved, once the folders they go into exist.
            // Each one that cannot be moved is said and counted.
            if (move && extras.NestedLinks.Count > 0)
            {
                await Task.Run(() =>
                {
                    foreach (var (link, to) in extras.NestedLinks)
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

        private static List<(int Start, int End)> RunsByFolder(List<PlannedCopy> plan) =>
            RunsByFolder(plan.Count, i => Path.GetDirectoryName(plan[i].Destination) ?? "", i => plan[i].Size);

        /// <summary>
        /// Cuts a list of files into runs of neighbours that land in one folder,
        /// for workers to take a run at a time rather than a file at a time.
        ///
        /// Workers that each take the next file are all creating files in the
        /// same folder at once, and Windows does that one at a time. Measured,
        /// ten thousand small files in a hundred folders took 2.8 seconds that
        /// way and 1.3 with each worker in a folder of its own. A run ends where
        /// the folder changes, at 64 files, or once it holds a megabyte, so a
        /// folder of big files is still shared out file by file and no worker is
        /// left with a long queue while the others stop.
        /// </summary>
        internal static List<(int Start, int End)> RunsByFolder(int count, Func<int, string> folderOf, Func<int, long> sizeOf)
        {
            const int RunFiles = 64;
            const long RunBytes = 1 << 20;

            var runs = new List<(int, int)>();
            int start = 0;
            long bytes = 0;
            string? folder = null;

            for (int i = 0; i < count; i++)
            {
                var here = folderOf(i);
                if (i > start && (i - start >= RunFiles || bytes >= RunBytes ||
                                  !string.Equals(here, folder, StringComparison.OrdinalIgnoreCase)))
                {
                    runs.Add((start, i));
                    start = i;
                    bytes = 0;
                }
                if (i == start) folder = here;
                bytes += Math.Max(0, sizeOf(i));
            }

            if (count > 0) runs.Add((start, count));
            return runs;
        }

        public enum ConflictChoice { Overwrite, Skip, Rename, Cancel, FillGaps }

        /// <summary>What the plan decided beyond the files themselves.</summary>
        private sealed class PlanExtras
        {
            /// <summary>Sources that are neither a file nor a folder any more.</summary>
            public readonly List<string> Missing = new();

            /// <summary>
            /// Sources refused on their own (a folder pasted into itself), said
            /// as failures while the rest of the paste goes ahead.
            /// </summary>
            public readonly List<string> Refused = new();

            /// <summary>Selected links being moved, and where each goes.</summary>
            public readonly List<(string Source, string Destination)> Links = new();

            /// <summary>Folders whose tree was planned, the only ones a move may tidy away.</summary>
            public readonly List<string> MovedFolders = new();

            /// <summary>
            /// Folders being moved on one volume to a name nothing has, which
            /// can be renamed whole rather than file by file.
            /// </summary>
            public readonly List<(string Source, string Destination)> FolderRenames = new();

            /// <summary>Links found inside folders being moved, and where each goes.</summary>
            public readonly List<(string Source, string Destination)> NestedLinks = new();

            /// <summary>Files Fill gaps left alone because a merged folder already had them.</summary>
            public int AlreadyThere;
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
                    destRoot = ResolveConflict(destRoot, policy, askConflict, conflicts, claimed, true,
                        out bool skip, out bool merge, out ClashRule folderRule);
                    if (skip) continue;
                    claimed.Add(destRoot);

                    // Still refused once renaming has had its say: copying a folder
                    // into its own subtree really would recurse forever, and asking
                    // to overwrite a folder with itself is not a copy at all.
                    // Refused for this source alone: thrown, it failed the whole
                    // paste and the other items in it were never copied.
                    if (IsWithin(destRoot, source))
                    {
                        if (extras == null) throw new IOException($"Cannot copy \"{name}\" into itself.");
                        extras.Refused.Add($"{name}: cannot copy a folder into itself");
                        continue;
                    }

                    // The root itself, so copying an entirely empty folder still
                    // produces a folder rather than nothing at all.
                    directories.Add(new PlannedDirectory(destRoot));
                    extras?.MovedFolders.Add(source);

                    // One walk for the folders and the files, with each file's
                    // size from the listing that found it. It was two walks and a
                    // FileInfo per file: three requests for every entry, which on
                    // a share is three round trips.
                    var foundDirectories = new List<string>();
                    var foundFiles = new List<(string Path, long Length)>();
                    var foundLinks = new List<string>();
                    int unreadableBefore = unreadable?.Count ?? 0;
                    Walk(source, foundDirectories, foundFiles, unreadable, token, foundLinks);

                    // Links inside a folder being moved go with it, as links.
                    // Skipped by the walk and by nothing after it, a move to
                    // another disk left them in the source folder — which then
                    // was not empty, so stayed — and said "Move complete".
                    if (move && extras != null)
                        foreach (var link in foundLinks)
                            extras.NestedLinks.Add((link, Path.Combine(destRoot, Path.GetRelativePath(source, link))));

                    foreach (var directory in foundDirectories)
                    {
                        var relative = Path.GetRelativePath(source, directory);
                        directories.Add(new PlannedDirectory(Path.Combine(destRoot, relative)));
                    }

                    bool rename = move && SameVolume(source, destRoot);

                    // The whole folder in one rename, when nothing is at the
                    // destination to merge into and every folder in it could be
                    // read (an unreadable one is already counted as failed, and
                    // stays behind). File by file, a move of ten thousand files
                    // within one disk took fifty seconds; whole, a few
                    // milliseconds. Tried first when the move runs, and the plan
                    // below is the fallback if Windows refuses it — a file in
                    // the tree held open by another program, say.
                    if (rename && extras != null && (unreadable?.Count ?? 0) == unreadableBefore)
                    {
                        var exactRoot = NameRules.ExactPath(destRoot);
                        bool taken;
                        try { taken = File.Exists(exactRoot) || Directory.Exists(exactRoot); }
                        catch { taken = true; }
                        if (!taken) extras.FolderRenames.Add((source, destRoot));
                    }

                    foreach (var (file, size) in foundFiles)
                    {
                        var relative = Path.GetRelativePath(source, file);
                        var target = Path.Combine(destRoot, relative);

                        // Merging under Fill gaps: what the destination already
                        // has stays as it is, and so does the original of a move.
                        if (merge)
                        {
                            var exactTarget = NameRules.ExactPath(target);
                            bool there;
                            try { there = File.Exists(exactTarget) || Directory.Exists(exactTarget); }
                            catch { there = true; }
                            if (there)
                            {
                                if (extras != null) extras.AlreadyThere++;
                                continue;
                            }
                        }

                        plan.Add(new PlannedCopy(file, target, size, rename, folderRule));
                    }
                }
                else if (File.Exists(exact))
                {
                    var dest = Path.Combine(destinationDir, name);
                    dest = ResolveConflict(dest, policy, askConflict, conflicts, claimed, false,
                        out bool skip, out _, out ClashRule fileRule);
                    if (skip) continue;
                    claimed.Add(dest);

                    long size = 0;
                    try { size = new FileInfo(exact).Length; } catch { }
                    plan.Add(new PlannedCopy(source, dest, size, move && SameVolume(source, dest), fileRule));
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
            => ResolveConflict(desired, policy, askConflict, conflicts, claimed, folder, out skip, out _, out _);

        /// <param name="merge">
        /// A folder arriving on a folder under Fill gaps: the two are merged, and
        /// every file the destination already has is left alone.
        /// </param>
        /// <param name="rule">
        /// What the copy may do if the destination is taken by the time it is
        /// written. Replace only when Replace was what this decided.
        /// </param>
        private static string ResolveConflict(
            string desired,
            PasteConflictPolicy policy,
            Func<string, ConflictChoice>? askConflict,
            ConflictLog conflicts,
            HashSet<string> claimed,
            bool folder,
            out bool skip,
            out bool merge,
            out ClashRule rule)
        {
            skip = false;
            merge = false;
            rule = ClashRule.Rename;

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
            if (!File.Exists(exactDesired) && !Directory.Exists(exactDesired))
            {
                // Nothing there now: the standing policy says what to do if
                // something arrives first. Ask cannot ask mid-copy, so keeps both.
                rule = ClashRuleFor(policy);
                return desired;
            }

            var effective = policy;
            if (policy == PasteConflictPolicy.Ask)
            {
                var choice = askConflict?.Invoke(desired) ?? ConflictChoice.Rename;
                effective = choice switch
                {
                    ConflictChoice.Overwrite => PasteConflictPolicy.Overwrite,
                    ConflictChoice.Skip => PasteConflictPolicy.Skip,
                    ConflictChoice.FillGaps => PasteConflictPolicy.FillGaps,
                    ConflictChoice.Cancel => throw new OperationCanceledException(),
                    _ => PasteConflictPolicy.AutoRename,
                };
            }

            switch (effective)
            {
                // Fill gaps fills in what is missing and touches nothing that is
                // there. It fell into the rename below, so resuming a copy made
                // "Photos (2)" with everything in it a second time.
                case PasteConflictPolicy.FillGaps:
                    if (folder && Directory.Exists(exactDesired))
                    {
                        merge = true;
                        rule = ClashRule.Skip;
                        return desired;
                    }
                    skip = true;
                    conflicts.Skipped(desired);
                    return desired;
                case PasteConflictPolicy.Overwrite:
                    // A file and a folder are never swapped for each other, as
                    // the robocopy path and Drive both refuse: said as a failure
                    // rather than recorded as replaced and then failing.
                    bool folderThere = Directory.Exists(exactDesired);
                    if (folderThere != folder)
                    {
                        skip = true;
                        conflicts.KindClashes.Add((desired, folderThere));
                        return desired;
                    }
                    conflicts.Overwritten(desired);
                    rule = ClashRule.Overwrite;
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

        /// <summary>
        /// A same-volume move that never replaces anything. A name taken since
        /// the plan is numbered afresh (keep both) or left alone (Fill gaps,
        /// Skip). Returns where the file went, or null when it was left.
        /// </summary>
        private static string? MoveWithoutReplacing(string source, string destination, ClashRule onClash)
        {
            var from = NameRules.ExactPath(source);
            string final = destination;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(from, NameRules.ExactPath(final), overwrite: false);
                    return final;
                }
                catch (IOException ex) when ((ex.HResult & 0xFFFF) is 80 or 183 && attempt < 5)
                {
                    if (onClash == ClashRule.Skip) return null;
                    final = UniqueName(destination, null, false);
                }
            }
        }

        /// <returns>
        /// Where the copy landed: the destination, or a fresh number when keep
        /// both found the name taken since the plan. Null when Fill gaps or
        /// Skip found it taken, and nothing was written.
        /// </returns>
        private static async Task<string?> CopyFileAsync(
            string source, string destination, int bufferSize, long sizeHint,
            Action<long> onChunk, CancellationToken token, ClashRule onClash)
        {
            // Exact, so "report." is read and written as itself.
            var from = NameRules.ExactPath(source);
            string final = destination;
            var to = NameRules.ExactPath(final);

            // Unbuffered, because the reads and writes are already whole chunks:
            // a buffered stream kept a buffer of its own the size of the setting
            // on each end of every file, a megabyte at the default setting, however small the file.
            await using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read,
                0, FileOptions.Asynchronous | FileOptions.SequentialScan);

            // A file being replaced is never written over in place. Opened
            // FileMode.Create, the original was emptied before the first byte
            // arrived, so a cancel, a full disk or a source that failed half way
            // destroyed the very file the overwrite was meant to replace, and the
            // cleanup below then deleted what was left of it. The new copy is
            // written beside it under a temporary name and moved over it only
            // once it is whole. A new name — nearly every file — costs nothing:
            // CreateNew answers whether something is there in the same request.
            //
            // Swapping over what is there happens only when the plan chose
            // Replace. A file that appeared since the plan (another transfer
            // running at the same time) was swapped over whatever the policy
            // said, so under keep both one transfer's copy replaced another's,
            // and a move then deleted the only original.
            string written = to;
            FileStream dst;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    dst = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        0, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    written = to;
                    break;
                }
                catch (IOException ex) when ((ex.HResult & 0xFFFF) is 80 or 183)
                {
                    if (onClash == ClashRule.Overwrite)
                    {
                        written = NameRules.ExactPath(Path.Combine(
                            Path.GetDirectoryName(NameRules.PlainPath(to))!,
                            ".copying-" + Guid.NewGuid().ToString("N")[..12] + ".tmp"));
                        dst = new FileStream(written, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                            0, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        break;
                    }
                    if (onClash == ClashRule.Skip) return null;

                    // Keep both: a fresh number now, tried again, because the
                    // race can repeat.
                    if (attempt >= 5) throw;
                    final = UniqueName(destination, null, false);
                    to = NameRules.ExactPath(final);
                }
            }
            await using var _ = dst;

            // No bigger than the file, which the plan already knows: the size
            // is only the chunk, so a file that grew since is still copied whole.
            int chunk = (int)Math.Clamp(sizeHint, 4096, bufferSize);
            var pool = System.Buffers.ArrayPool<byte>.Shared;
            var current = pool.Rent(chunk);
            byte[]? spare = null;
            int read;
            bool complete = false;

            try
            {
                // The next chunk is read while this one is written, so the disk
                // is never waiting on one end for the other. Measured on a 4GB
                // file: 4.0 seconds one after the other, 2.8 overlapped.
                read = await src.ReadAsync(current.AsMemory(0, chunk), token);
                while (read > 0)
                {
                    spare ??= pool.Rent(chunk);
                    var next = src.ReadAsync(spare.AsMemory(0, chunk), token).AsTask();
                    try { await dst.WriteAsync(current.AsMemory(0, read), token); }
                    catch
                    {
                        // Not returned to the pool while a read may still be
                        // filling it.
                        try { await next; } catch { }
                        throw;
                    }
                    int wrote = read;
                    read = await next;
                    (current, spare) = (spare, current);
                    onChunk(wrote);
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
                CopyStreams(from, written);

                // Only now does the original go, in one step. Replace means
                // replace, a read-only file included.
                if (written != to) MoveReplacing(written, to);
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
                // Only what this copy wrote: under a temporary name when it was
                // replacing something, and the original is still there untouched.
                if (!complete)
                {
                    try { await dst.DisposeAsync(); } catch { }
                    try { File.Delete(written); } catch { }
                }

                // Nothing is reading into either by now: every read started
                // has been waited for on the way here.
                pool.Return(current);
                if (spare != null) pool.Return(spare);
            }

            // Dates after the streams, because writing a stream moves the file's
            // own last-write time; attributes last, because read-only would
            // refuse the dates. Set by path once every handle that writes is
            // closed: set through a handle with buffered data still to go out,
            // the time was overwritten at close and every copy was dated "now".
            CopyDatesAndAttributes(from, to);
            return final;
        }

        /// <summary>
        /// Moves <paramref name="from"/> over <paramref name="to"/>, replacing it
        /// even when it is read-only. File.Move with overwrite refuses a read-only
        /// destination; the flag is taken off it only for the move, and put back
        /// if the move still fails, so the file left there is still the original
        /// as it was. Shared by every Replace that ends in a move: the copy's swap,
        /// a same-volume move, and a converted file taking its place.
        /// </summary>
        internal static void MoveReplacing(string from, string to)
        {
            try { File.Move(from, to, overwrite: true); }
            catch (UnauthorizedAccessException denied)
            {
                // Windows says "access denied" for a file another program has open;
                // that is "in use", which says what to do about it.
                try { using var probe = new FileStream(to, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                catch (IOException held) when ((held.HResult & 0xFFFF) is 32 or 33)
                {
                    throw new IOException("it is in use by another program. Close it there, then try again.", held.HResult);
                }
                catch { }

                // Nothing there to be read-only (a move out of a folder that
                // refuses the delete): the real error is the access denied,
                // not "could not find" the destination.
                FileAttributes attributes;
                try { attributes = File.GetAttributes(to); }
                catch
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(denied).Throw();
                    throw;
                }
                if ((attributes & FileAttributes.ReadOnly) == 0 || (attributes & FileAttributes.Directory) != 0) throw;
                File.SetAttributes(to, attributes & ~FileAttributes.ReadOnly);
                try { File.Move(from, to, overwrite: true); }
                catch
                {
                    try { File.SetAttributes(to, attributes); } catch { }
                    throw;
                }
            }
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

        /// <summary>
        /// Every folder and file beneath the root, junctions not followed, in
        /// the order the separate folder and file walks produced them, with each
        /// file's size. One listing per folder answers all of it: whether an
        /// entry is a folder, its size, and whether it is a reparse point — the
        /// only kind of folder that can be a link, so the only one asked. A
        /// folder that cannot be listed is said in <paramref name="unreadable"/>.
        /// Paths are listed through the exact form and handed back in the
        /// ordinary one.
        /// </summary>
        /// <param name="links">Where to list the folder links found and not followed, if anywhere.</param>
        private static void Walk(string root, List<string> directories, List<(string Path, long Length)> files,
            List<string>? unreadable, CancellationToken token, List<string>? links = null)
        {
            var options = new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
            };
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var dir = stack.Pop();

                List<(string Path, long Length, bool Folder, bool Reparse)> entries;
                try
                {
                    entries = new List<(string, long, bool, bool)>(
                        new System.IO.Enumeration.FileSystemEnumerable<(string, long, bool, bool)>(
                            NameRules.ExactPath(dir),
                            (ref System.IO.Enumeration.FileSystemEntry e) => (e.ToFullPath(), e.Length, e.IsDirectory,
                                (e.Attributes & FileAttributes.ReparsePoint) != 0),
                            options));
                }
                catch { unreadable?.Add(dir); continue; }

                foreach (var (sub, _, folder, reparse) in entries)
                {
                    if (!folder) continue;

                    // Don't follow junctions: they lead out of the tree or back
                    // into it, and a junction's destination is not created either.
                    if (reparse)
                    {
                        bool link;
                        try { link = NameRules.IsLink(new DirectoryInfo(sub)); }
                        catch { link = true; }
                        if (link)
                        {
                            links?.Add(NameRules.PlainPath(sub));
                            continue;
                        }
                    }

                    var plain = NameRules.PlainPath(sub);
                    stack.Push(plain);
                    directories.Add(plain);
                }

                foreach (var (file, length, folder, _) in entries)
                    if (!folder) files.Add((NameRules.PlainPath(file), length));
            }
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
