using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    public sealed record TransferProgress(
        long BytesDone,
        long BytesTotal,
        int ItemsDone,
        int ItemsTotal,
        string CurrentItem,
        double BytesPerSecond,
        TimeSpan Elapsed,
        bool ItemsKnown = false)
    {
        public bool TotalKnown => BytesTotal > 0;
        public double Percent => TotalKnown ? Math.Clamp(BytesDone * 100.0 / BytesTotal, 0, 100) : 0;
        public TimeSpan? Remaining =>
            TotalKnown && BytesPerSecond > 1
                ? TimeSpan.FromSeconds(Math.Max(0, (BytesTotal - BytesDone) / BytesPerSecond))
                : null;
    }

    /// <param name="AlreadyThere">
    /// Files inside a merged folder that the destination already had, so they
    /// were left alone — and on a move, whose originals therefore stayed where
    /// they were. Not copied, not failed, and said so rather than counted as
    /// either.
    /// </param>
    public sealed record TransferResult(
        int Copied,
        int Failed,
        bool Cancelled,
        IReadOnlyList<string> Errors,
        ConflictOutcomes? Conflicts = null,
        int AlreadyThere = 0)
    {
        /// <summary>Never null, so a caller can read it without a guard.</summary>
        public ConflictOutcomes Collisions => Conflicts ?? ConflictOutcomes.None;
    }

    /// <summary>
    /// How fast the transfer is going *now*, rather than how fast it has gone on
    /// average since it started.
    ///
    /// The difference is small on a local copy and enormous on one coming off
    /// Google Drive, where the first few megabytes are already in memory and the
    /// rest are not. Measured on a 68.8MB file: 17.8MB in the first nine seconds
    /// and the remaining 51MB over the following ninety-three. The average at
    /// the nine-second mark said two megabytes a second and twenty-five seconds
    /// remaining. The real answer was ninety-three, and the number on screen went
    /// on being wrong in the same direction for the whole copy — the estimate
    /// climbing steadily instead of counting down, which is the one thing a time
    /// remaining is for.
    ///
    /// A window, so the figure is about the part of the transfer happening now.
    /// Ten seconds because it has to be long enough to ride out robocopy's
    /// block-buffered output — which arrives in bursts of fifty lines — without
    /// being so long that the opening burst is still colouring the answer a
    /// minute later.
    /// </summary>
    internal sealed class RecentRate
    {
        private const double WindowSeconds = 10.0;

        private readonly object _gate = new();
        private readonly Queue<(double At, long Bytes)> _samples = new();

        /// <summary>
        /// Records where the transfer has got to and returns bytes per second
        /// over the window. Called from the ticker and from robocopy's output
        /// handler, which are different threads.
        /// </summary>
        public double Observe(long bytesDone, TimeSpan elapsed)
        {
            double now = elapsed.TotalSeconds;

            lock (_gate)
            {
                _samples.Enqueue((now, bytesDone));

                // One sample older than the window is kept deliberately, so the
                // span never collapses to nothing at the moment the oldest is
                // dropped — which would divide by very nearly zero and report a
                // speed of several gigabytes a second.
                while (_samples.Count > 2 && _samples.Peek().At < now - WindowSeconds)
                    _samples.Dequeue();

                var first = _samples.Peek();
                double span = now - first.At;

                // Nothing to measure against yet. The average since the start is
                // the honest answer for the first second or so, and it is what
                // this used to be for the whole transfer.
                if (span < 0.5) return now > 0.001 ? bytesDone / now : 0;

                return Math.Max(0, (bytesDone - first.Bytes) / span);
            }
        }
    }

    /// <summary>
    /// Copy/move driven by robocopy.exe.
    ///
    /// Robocopy is used rather than a hand-rolled loop because it already
    /// handles the hard parts: multithreaded copying (/MT), retry on transient
    /// network failure, long paths, and preserving timestamps. On a high-latency
    /// share its threading is the difference between usable and unusable.
    ///
    /// Progress is derived by matching robocopy's per-file output against a plan
    /// computed in the background, so a percentage appears as soon as the total
    /// is known without delaying the start of the copy.
    /// </summary>
    public static class RoboCopyEngine
    {
        /// <summary>
        /// A robocopy report, corrected by what the Drive mount has actually handed
        /// over, for a copy out of the Drive letter.
        ///
        /// The larger of the two byte counts, because each is late in its own way —
        /// robocopy's by a whole file, the mount's not at all for bytes it served
        /// but blind to a file that was already downloaded and never asked for —
        /// and never less than what is already on screen, so the window does not go
        /// backwards when robocopy's late line for one file lands while the mount
        /// has moved on to the next. Capped at the total, which neither source may
        /// exceed. The name is the file being downloaded now rather than the one
        /// robocopy last finished telling us about.
        ///
        /// Pure, and here rather than in the window, because the arithmetic is the
        /// part that can be wrong and the window is the part nobody tests.
        /// </summary>
        /// <param name="rate">
        /// The window the speed is measured over. Without one the speed is the
        /// average since the start — which is what brought back, for Drive, the
        /// "time remaining keeps climbing" that <see cref="RecentRate"/> fixed.
        /// </param>
        internal static TransferProgress WithDownloads(
            TransferProgress reported, long served, string? downloading, long alreadyShown,
            RecentRate? rate = null)
        {
            long bytes = Math.Max(Math.Max(reported.BytesDone, served), alreadyShown);
            if (reported.TotalKnown) bytes = Math.Min(bytes, reported.BytesTotal);

            double seconds = reported.Elapsed.TotalSeconds;
            double speed = rate != null ? rate.Observe(bytes, reported.Elapsed)
                : seconds > 0.25 ? bytes / seconds
                : reported.BytesPerSecond;

            return reported with
            {
                BytesDone = bytes,
                CurrentItem = string.IsNullOrEmpty(downloading) ? reported.CurrentItem : downloading,
                BytesPerSecond = speed,
            };
        }

        /// <summary>Robocopy uses its exit code as a bitmask; 8 and above are real failures.</summary>
        private const int FirstFailureCode = 8;

        private sealed record Job(string SourceDir, string DestDir, List<string> Files, bool WholeTree);

        /// <summary>
        /// How many characters of file names one robocopy call is given. The
        /// limit is 32,767 for the whole command line; the two folders and the
        /// switches take the rest.
        /// </summary>
        private const int CommandLineBudget = 24_000;

        /// <summary>
        /// Called with robocopy's process id before a cancelled copy ends it,
        /// and after. The Drive provider fails that process's reads in between;
        /// see <see cref="DriveMount.StandDownReads"/>.
        /// </summary>
        public static Action<int>? BeforeKill;
        public static Action<int>? AfterKill;

        public static async Task<TransferResult> RunAsync(
            IReadOnlyList<string> sources,
            string destinationDir,
            bool move,
            int threads,
            PasteConflictPolicy conflictPolicy,
            IProgress<TransferProgress>? progress,
            CancellationToken token,
            int bufferKilobytes = 1024,
            bool cloudSource = false,
            Func<string, bool>? onCloudLetter = null)
        {
            // cloudSource: copying *out of* the Google Drive letter, where every
            // source file is a placeholder this same process downloads while
            // robocopy reads it. How many to read at once is the caller's decision
            // (MainForm.TransferThreads, which says one); what changes here is the
            // attributes — see where the switches are added.
            //
            // onCloudLetter: whether a path is on the Drive letter, where nothing
            // may be renamed into or out of — a rename there is not a move in
            // the account. Null when Drive is not mounted.

            var errors = new List<string>();
            int copied = 0, failed = 0;

            // Robocopy overwrites unconditionally, so the conflict policy has to
            // be applied before it ever runs. Anything that would collide is
            // split off: skipped, or copied under a fresh name by the managed
            // engine. Everything non-colliding still goes through robocopy at
            // full speed, which is the overwhelmingly common case.
            var colliding = new List<string>();
            var clean = new List<string>();
            var overwritten = new List<(string Name, string Source)>();

            // Selected links being moved, and top-level folders whose names end in
            // a dot or a space. Neither can be handed to robocopy: it follows a
            // link at the root of a job (/XJ only covers links inside one), and
            // given "fold." it quietly copies the sibling "fold" instead.
            var linkMoves = new List<(string Source, string Target)>();
            var literalFolders = new List<string>();

            // Sources robocopy was never given, because they were refused first.
            var refusedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Replaced by the managed engine, which robocopy's list cannot know of.
            int managedReplaced = 0;
            var managedReplacedNames = new List<string>();

            // Destinations that did not exist before this transfer started, so
            // everything that appears under one of them was put there by us.
            //
            // This is what makes cleaning up after a cancellation safe. Anything
            // we did not create is never touched, however incomplete it looks —
            // once a destination already existed there is no way to tell our
            // half-written bytes from somebody else's file, and guessing wrong
            // there deletes data nobody asked us to touch.
            var newRootSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var transferStartedUtc = DateTime.UtcNow;

            // Targets this paste has already given to a source. A second source
            // with the same name — two "photos" out of search results — would be
            // a second robocopy job into the same folder, merging the two and, on
            // a move, deleting both originals.
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sameName = new List<string>();

            // A file that is already there, under Fill gaps: left alone, which
            // robocopy would do anyway — but said, so nothing downstream takes it
            // for copied. A move out of Drive trashed such a file's original.
            var gapSkipped = new List<string>();

            foreach (var source in sources)
            {
                var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) { clean.Add(source); continue; }

                var target = Path.Combine(destinationDir, name);
                if (!targets.Add(target)) { sameName.Add(source); continue; }

                // Exact, because "report." and "report" are different files and an
                // ordinary path says "report" for both.
                var exactSource = NameRules.ExactPath(source);
                var exactTarget = NameRules.ExactPath(target);

                // Pasted where it already is.
                if (FileOperations.SamePath(target, source))
                {
                    // A move there is nothing to do. Under keep both it was a
                    // collision with itself, and cut and paste within one folder
                    // renamed everything to "a (2)".
                    if (move) continue;

                    // Replacing a thing with itself is not a copy. Refused here,
                    // before it is recorded as replaced: it was announced as
                    // "overwritten" and then refused as the same folder.
                    if (conflictPolicy == PasteConflictPolicy.Overwrite)
                    {
                        failed++;
                        errors.Add($"{name}: source and destination are the same folder, so it cannot replace itself");
                        continue;
                    }

                    // Every file it has is already there, which is all Fill gaps asks.
                    if (conflictPolicy == PasteConflictPolicy.FillGaps)
                    {
                        gapSkipped.Add(name);
                        continue;
                    }
                }

                bool collides;
                try { collides = File.Exists(exactTarget) || Directory.Exists(exactTarget); }
                catch { collides = false; }

                // A selected link, moved: as itself, never through robocopy, which
                // follows a link at the root of a job and moved — so deleted —
                // whatever it pointed at.
                if (move && FileOperations.IsLinkAt(exactSource))
                {
                    if (!collides) linkMoves.Add((source, target));

                    // The managed engine moves links as links. Overwrite and Fill
                    // gaps have no meaning for one — neither replaces a folder with
                    // a link nor merges into one — so it is kept both.
                    else if (conflictPolicy is PasteConflictPolicy.Overwrite or PasteConflictPolicy.FillGaps)
                        sameName.Add(source);
                    else colliding.Add(source);
                    continue;
                }

                // A file where a folder of that name is, or the other way round.
                // Robocopy will not put one over the other and says so only as
                // exit code 4, which counts as success — a move out of Drive then
                // trashed an original that was never copied. Keeping both is the
                // one answer that can go ahead.
                if (collides && conflictPolicy is PasteConflictPolicy.Overwrite or PasteConflictPolicy.FillGaps)
                {
                    bool sourceIsFolder, targetIsFolder;
                    try
                    {
                        sourceIsFolder = Directory.Exists(exactSource);
                        targetIsFolder = Directory.Exists(exactTarget);
                    }
                    catch { sourceIsFolder = targetIsFolder = false; }

                    if (sourceIsFolder != targetIsFolder)
                    {
                        failed++;
                        errors.Add($"{name}: a {(targetIsFolder ? "folder" : "file")} of that name is already there");
                        continue;
                    }
                }

                // A folder robocopy would be handed by a name ending in a dot or a
                // space — the folder itself, the one it is in, or the destination.
                // Robocopy strips the dot and copies the sibling of that name, and
                // does not take the literal form at all. The managed engine does.
                bool isFolder;
                try { isFolder = Directory.Exists(exactSource); } catch { isFolder = false; }
                var parent = Path.GetDirectoryName(source.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
                if (NameRules.ExactPath(destinationDir) != destinationDir ||
                    NameRules.ExactPath(parent) != parent ||
                    (isFolder && exactSource != source))
                {
                    if (collides && conflictPolicy is not (PasteConflictPolicy.Overwrite or PasteConflictPolicy.FillGaps))
                        colliding.Add(source);
                    else literalFolders.Add(source);
                    continue;
                }

                // FillGaps never splits anything off. Its whole job is to go
                // *into* what is already there, which is precisely what the
                // managed rename engine below cannot do and what robocopy does
                // natively — merge the tree, and copy a file only where the
                // destination has none. Splitting a colliding folder off here
                // would hand the one case this policy exists for to the one
                // engine that answers it by making "folder (2)".
                if (collides && conflictPolicy != PasteConflictPolicy.Overwrite &&
                    conflictPolicy != PasteConflictPolicy.FillGaps)
                {
                    colliding.Add(source);
                }
                else if (collides && conflictPolicy == PasteConflictPolicy.FillGaps && File.Exists(exactTarget))
                {
                    gapSkipped.Add(name);
                }
                else
                {
                    // Robocopy overwrites without saying so, and that silence is
                    // the whole reason to record it here: this is the one policy
                    // that destroys something, and the only place that knows a
                    // file was replaced rather than merely written.
                    //
                    // Not for FillGaps, which reaches the same branch and
                    // destroys nothing — it merges into what is there and writes
                    // only where there was no file. Calling that "replaced"
                    // would announce a file as destroyed for the one policy
                    // chosen specifically because it does not do that.
                    if (collides && conflictPolicy == PasteConflictPolicy.Overwrite)
                        overwritten.Add((name, source));
                    else if (!collides) newRootSources[target] = source;

                    clean.Add(source);
                }
            }

            int renamed = 0, skipped = gapSkipped.Count;
            var renamedNames = new List<string>();
            var skippedNames = new List<string>(gapSkipped);

            // Files the managed engine actually copied, as against the number of
            // *collisions* it resolved.
            //
            // These are different units and the second was being reported as the
            // first. A folder pasted back beside itself is one collision and one
            // rename — and a thousand files, so "1 file transferred" was the
            // answer for a thousand in the one place a partial failure gets
            // explained. That is the same mistake the comment further down
            // records having already fixed once, in its other form.
            int managedFiles = 0;

            if (colliding.Count > 0)
            {
                if (conflictPolicy == PasteConflictPolicy.Skip)
                {
                    skipped = colliding.Count;
                    foreach (var source in colliding.Take(ConflictOutcomes.MaxRemembered))
                        skippedNames.Add(Path.GetFileName(
                            source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

                    // Reported here, not at the end: when everything collides
                    // there is no robocopy phase to fall through to.
                    errors.Add($"{skipped} item{(skipped == 1 ? "" : "s")} already existed and " +
                               $"{(skipped == 1 ? "was" : "were")} skipped");
                }
                else
                {
                    // AutoRename (and Ask, which the caller resolves before us):
                    // the managed engine is the only one that can rename mid-copy.
                    // The configured buffer, not a hard-coded one. CopyBufferKilobytes
                    // was offered in the settings file, clamped on load and covered by
                    // a test, and then never reached a copy: this was the only call
                    // that could have used it and it passed a literal instead.
                    // Against every name this paste is about to write, too: "a"
                    // renamed to "a (2)" beside a source called "a (2)" was then
                    // written over by robocopy's copy of it.
                    var managed = await FileOperations.RunAsync(
                        colliding, destinationDir, move, PasteConflictPolicy.AutoRename,
                        threads, bufferKilobytes, null, null, token, reserved: targets);

                    renamed = managed.Collisions.RenamedCount;
                    managedFiles = managed.Succeeded;
                    renamedNames.AddRange(managed.Collisions.Renamed);
                    skipped += managed.Collisions.SkippedCount;
                    skippedNames.AddRange(managed.Collisions.Skipped);

                    failed += managed.Failed;
                    errors.AddRange(managed.Errors);
                    if (managed.Cancelled)
                        return new TransferResult(managed.Succeeded, failed, true, errors, Collisions());
                }
            }

            // The second of two sources with one name, numbered against everything
            // this paste is about to write as well as what is already there.
            if (sameName.Count > 0)
            {
                var numbered = await FileOperations.RunAsync(
                    sameName, destinationDir, move, PasteConflictPolicy.AutoRename,
                    threads, bufferKilobytes, null, null, token, reserved: targets);

                renamed += numbered.Collisions.RenamedCount;
                managedFiles += numbered.Succeeded;
                renamedNames.AddRange(numbered.Collisions.Renamed);
                failed += numbered.Failed;
                errors.AddRange(numbered.Errors);
                if (numbered.Cancelled)
                    return new TransferResult(managedFiles, failed, true, errors, Collisions());
            }

            // What robocopy cannot be handed by name, under the policy asked for.
            // Not reserved against their own names, or every one of them would
            // be numbered as though it collided with itself.
            if (literalFolders.Count > 0)
            {
                var others = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
                foreach (var source in literalFolders)
                    others.Remove(Path.Combine(destinationDir, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar))));

                var exact = await FileOperations.RunAsync(
                    literalFolders, destinationDir, move,
                    conflictPolicy == PasteConflictPolicy.Ask ? PasteConflictPolicy.AutoRename : conflictPolicy,
                    threads, bufferKilobytes, null, null, token, reserved: others);

                renamed += exact.Collisions.RenamedCount;
                renamedNames.AddRange(exact.Collisions.Renamed);
                skipped += exact.Collisions.SkippedCount;
                skippedNames.AddRange(exact.Collisions.Skipped);
                managedReplaced += exact.Collisions.OverwrittenCount;
                managedReplacedNames.AddRange(exact.Collisions.Overwritten);
                managedFiles += exact.Succeeded;
                failed += exact.Failed;
                errors.AddRange(exact.Errors);
                if (exact.Cancelled)
                    return new TransferResult(managedFiles, failed, true, errors, Collisions());
            }

            // A move that can be a rename is one. Robocopy's /MOVE copies every
            // byte and then deletes the original — a gigabyte moved within C: took
            // as long as copying it and came out a different file. A rename is
            // instant, and is exactly what the user asked for.
            //
            // Only where nothing collides (the rest needs the conflict policy),
            // where both ends are on one volume, and never to or from the Drive
            // letter: a rename there is local, and the account never hears of it.
            // The rename itself refuses to copy across volumes, so a wrong answer
            // about the volume costs a refused call, never a silent copy.
            //
            // Selected links are moved here too, as themselves.
            // Items already moved by renaming, carried into the copy's count so
            // the window never goes backwards — "1 of 3" to "0 of 2" — when
            // what could not be renamed falls back to copying.
            int renamedItems = 0;

            if (move && (linkMoves.Count > 0 || clean.Count > 0))
            {
                bool OnCloud(string path)
                {
                    try { return onCloudLetter?.Invoke(path) == true; }
                    catch { return true; }
                }

                var clock = Stopwatch.StartNew();
                bool renaming = !cloudSource && !OnCloud(destinationDir);

                var (moved, movedFiles, problems) = await Task.Run(() =>
                {
                    // On the worker: whether a target is inside its source asks
                    // Windows where each really is, which on a sleeping share is
                    // not instant.
                    var candidates = renaming
                        ? clean.Where(s =>
                        {
                            var target = Path.Combine(destinationDir,
                                Path.GetFileName(s.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                            return newRootSources.ContainsKey(target) && !OnCloud(s) && !IsWithin(target, s);
                        }).ToList()
                        : new List<string>();
                    int planned = candidates.Count + linkMoves.Count;

                    int done = 0, files = 0;
                    var trouble = new List<string>();

                    void Moved(string name)
                    {
                        done++;
                        progress?.Report(new TransferProgress(0, 0, done, planned, name, 0, clock.Elapsed,
                            ItemsKnown: true));
                    }

                    foreach (var (source, target) in linkMoves)
                    {
                        if (token.IsCancellationRequested) break;
                        try
                        {
                            FileOperations.MoveLink(source, target);
                            files++;
                            Moved(Path.GetFileName(target));
                        }
                        catch (Exception ex) { trouble.Add($"{source}: the link could not be moved ({ex.Message})"); }
                    }

                    foreach (var source in candidates)
                    {
                        if (token.IsCancellationRequested) break;
                        var target = Path.Combine(destinationDir,
                            Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                        if (!SameVolume(source, destinationDir) || !TryRename(source, target)) continue;

                        lock (clean) clean.Remove(source);
                        newRootSources.Remove(target);

                        // Counted in files, like everything else this reports.
                        if (Directory.Exists(target))
                            foreach (var _ in SafeEnumerateFiles(target)) files++;
                        else files++;
                        Moved(Path.GetFileName(target));
                    }

                    return (done, files, trouble);
                });

                managedFiles += movedFiles;
                renamedItems = moved;
                failed += problems.Count;
                errors.AddRange(problems);
                Trace($"renamed {moved} in {clock.ElapsedMilliseconds}ms");

                if (token.IsCancellationRequested)
                    return new TransferResult(managedFiles, failed, true, errors, Collisions());
            }

            // Built from whichever counts are filled in by the time it is called,
            // so every exit reports what actually happened rather than only the
            // exits that happen to run to the end.
            ConflictOutcomes Collisions()
            {
                // Not what was refused afterwards: overwriting a thing with itself
                // was announced as "replaced" and then refused.
                var replaced = overwritten
                    .Where(o => !refusedSources.Contains(o.Source))
                    .Select(o => o.Name)
                    .Concat(managedReplacedNames)
                    .ToList();
                int replacedCount = overwritten.Count(o => !refusedSources.Contains(o.Source)) + managedReplaced;

                return new(
                    renamedNames.Take(ConflictOutcomes.MaxRemembered).ToArray(),
                    skippedNames.Take(ConflictOutcomes.MaxRemembered).ToArray(),
                    replaced.Take(ConflictOutcomes.MaxRemembered).ToArray(),
                    renamed, skipped, replacedCount);
            }

            if (clean.Count == 0)
                return new TransferResult(managedFiles, failed, false, errors, Collisions());

            // Each refusal is a failure: "cannot copy a folder into itself" for
            // one of two folders went into the list and the other copied, and
            // with nothing counted the paste said "Copy complete" and the list
            // was never shown.
            int beforeRefusals = errors.Count;
            int jobsFailed = 0;
            // On a worker: the "into itself" check asks Windows where each end
            // really is (see ReparseLinks.Resolved).
            var jobs = await Task.Run(() => BuildJobs(clean, destinationDir, errors, refusedSources));
            failed += errors.Count - beforeRefusals;
            if (jobs.Count == 0)
                return new TransferResult(managedFiles, failed, false, errors, Collisions());

            // Totals are computed alongside the copy rather than before it, so a
            // huge tree does not sit silent while it is measured.
            long bytesTotal = 0;
            int itemsTotal = 0;
            var sizeByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            // Only what the jobs will copy. Walking every clean source counted a
            // refused folder — copied into itself, or a whole drive — into the
            // total, so the percentage never reached a hundred, and waited for
            // that walk at the end.
            var accepted = jobs
                .SelectMany(j => j.WholeTree
                    ? new[] { j.SourceDir }
                    : j.Files.Select(f => Path.Combine(j.SourceDir, f)))
                .ToList();

            var planning = Task.Run(() =>
            {
                foreach (var source in accepted)
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        if (Directory.Exists(NameRules.ExactPath(source)))
                        {
                            foreach (var f in SafeEnumerateFiles(source))
                            {
                                try
                                {
                                    var len = new FileInfo(NameRules.ExactPath(f)).Length;
                                    Interlocked.Add(ref bytesTotal, len);
                                    Interlocked.Increment(ref itemsTotal);
                                    lock (sizeByName) sizeByName[Path.GetFileName(f)] = len;
                                }
                                catch { }
                            }
                        }
                        else if (File.Exists(NameRules.ExactPath(source)))
                        {
                            var len = new FileInfo(NameRules.ExactPath(source)).Length;
                            Interlocked.Add(ref bytesTotal, len);
                            Interlocked.Increment(ref itemsTotal);
                            lock (sizeByName) sizeByName[Path.GetFileName(source)] = len;
                        }
                    }
                    catch { }
                }
            }, token);

            long bytesDone = 0;
            int itemsDone = 0;
            var started = Stopwatch.StartNew();
            string current = "";

            // Bytes written by robocopy jobs that have already finished, and the
            // one running now — see BytesWritten for why the operating system is
            // asked rather than robocopy.
            long finishedJobBytes = 0;
            Process? liveJob = null;

            // Never allowed to fall. Two sources feed this and either can drop
            // for an instant — a job ending between the ticker reading the
            // process and the total being added to finishedJobBytes — and a
            // progress figure that goes backwards is its own bug.
            long shown = 0;

            // How many bytes have really landed.
            //
            // Not what robocopy has said so far, which is the whole point. Its
            // per-file lines are the only thing that used to move this, and a
            // child process writing to a redirected pipe does not line-buffer —
            // it fills a block and flushes, so the lines arrive in bursts of
            // roughly fifty and mostly at the end. Measured on a 40-file copy
            // with these exact arguments: one line at 37ms, the other 39 in a
            // burst at 45-48ms.
            //
            // What that produced was not a slightly stale number, it was a
            // meaningless one. A real 42GB copy running at 36MB/s showed
            // "12.4 megabytes of 41.5 gigabytes", "59.3 kilobytes per second"
            // and "203 hours, 34 minutes" remaining — one file's worth of lines
            // having escaped the buffer while fifty had actually been copied.
            // The true answer was about eight minutes.
            //
            // The operating system knows, so it is asked. The larger of the two
            // wins because neither is right on its own: the kernel counter reads
            // zero for a process whose handle cannot be asked, where the parsed
            // lines are still correct.
            long BytesLanded()
            {
                long matched = Interlocked.Read(ref bytesDone);

                // Until the walk has finished there is no total to measure
                // against, and inflating the percentage against a total that is
                // still growing is its own kind of wrong. The parsed count is the
                // honest one to show for the second or two that takes.
                if (!planning.IsCompleted) return matched;

                long measured = Interlocked.Read(ref finishedJobBytes) + BytesWritten(Volatile.Read(ref liveJob));

                long best = ProgressBytes(matched, measured,
                    Interlocked.Read(ref bytesTotal), Interlocked.Read(ref shown));

                Interlocked.Exchange(ref shown, best);
                return best;
            }

            var rate = new RecentRate();

            // Files robocopy has *started*, seen by watching the destination.
            //
            // The name and the item count had the same problem as the bytes and
            // never got the same fix: both came only from robocopy's own lines,
            // which arrive in bursts and mostly at the end, so for nearly the
            // whole of a copy the window said "(preparing)" and "0 of 6 items".
            // Robocopy creates each file at the destination the moment it starts
            // it, under its final name, and Windows says so at once — so the
            // newest one is the file being copied now. At most `threads` are in
            // flight, which makes "started less threads" a count of finished
            // files that can only be too low, never too high.
            //
            // The line is late for one file as much as for many: /MT is always
            // passed, and in that mode robocopy prints a file as it *finishes*.
            // One track copied out of Drive showed "(preparing)" from start to end.
            int filesStarted = 0;
            int lanes = Math.Clamp(threads, 1, 128);

            int ItemsFinished()
            {
                int parsed = Volatile.Read(ref itemsDone);
                int seen = Volatile.Read(ref filesStarted) - lanes;
                return Math.Min(Math.Max(parsed, seen), Math.Max(parsed, Volatile.Read(ref itemsTotal)));
            }

            // Per file, a report is at most one every 100ms — the rule the
            // archive engine keeps, and for the same reason: fifty thousand small
            // files were fifty thousand posts to the window, each rewriting
            // fields a screen reader announces. The ticker reports regardless.
            // Starting a tenth of a second in the past, so the first report is
            // never the one held back: a short copy is over before 100ms.
            long lastReport = -100;
            void ReportSoon()
            {
                long now = started.ElapsedMilliseconds;
                long last = Interlocked.Read(ref lastReport);
                if (now - last < 100) return;
                if (Interlocked.CompareExchange(ref lastReport, now, last) != last) return;
                Report();
            }

            void Report()
            {
                if (progress == null) { Trace("report skipped: no progress sink"); return; }
                long done = BytesLanded();
                double speed = rate.Observe(done, started.Elapsed);
                Trace($"report done={done} total={Interlocked.Read(ref bytesTotal)} " +
                      $"planned={planning.IsCompleted} items={Volatile.Read(ref itemsDone)}");
                progress.Report(new TransferProgress(
                    done,
                    Interlocked.Read(ref bytesTotal),
                    renamedItems + ItemsFinished(),
                    renamedItems + Volatile.Read(ref itemsTotal),
                    Volatile.Read(ref current),
                    speed,
                    started.Elapsed));
            }

            // Where each planned file lands, so that a file appearing in the
            // destination can be told apart from anything else writing there.
            //
            // The watcher covered the whole destination tree and believed every
            // file in it: while a 3GB file copied, a browser writing its cookies
            // in a subfolder had the window naming "Cookies-journal" and counting
            // it as an item. And the cleanup below needs the same answer the
            // other way round — which files this transfer made.
            var treeJobs = jobs.Where(j => j.WholeTree)
                .Select(j => (Dest: Prefix(j.DestDir), Source: Prefix(j.SourceDir)))
                .ToList();
            var looseLandings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var j in jobs.Where(j => !j.WholeTree))
                foreach (var f in j.Files)
                    looseLandings[Path.Combine(j.DestDir, f)] = Path.Combine(j.SourceDir, f);
            var looseSources = looseLandings.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

            static string Prefix(string folder) => folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            string? SourceFor(string landing)
            {
                if (looseLandings.TryGetValue(landing, out var source)) return source;
                foreach (var (dest, from) in treeJobs)
                    if (landing.StartsWith(dest, StringComparison.OrdinalIgnoreCase))
                        return from + landing[dest.Length..];
                return null;
            }

            string? LandingFor(string source)
            {
                if (looseSources.TryGetValue(source, out var landing)) return landing;
                foreach (var (dest, from) in treeJobs)
                    if (source.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                        return dest + source[from.Length..];
                return null;
            }

            // Files this transfer created, as Windows announced them. Every one
            // is a file robocopy started, whether or not the destination existed
            // before — which is what the cleanup needs and the old "new roots
            // only" rule could not see.
            var created = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            int landingsLost = 0;

            using var landings = WatchLandings(destinationDir, landing =>
            {
                // Only what the plan copies, and — inside a folder being merged
                // into, where anything else may be writing — only what has a
                // source. Not on a move, where robocopy may already have deleted
                // the source of a small file by the time this is heard.
                var source = SourceFor(landing);
                if (source == null) return;
                if (!move)
                {
                    try { if (!File.Exists(NameRules.ExactPath(source))) return; }
                    catch { return; }
                }

                created[landing] = 0;
                Volatile.Write(ref current, Path.GetFileName(landing));
                Interlocked.Increment(ref filesStarted);
                ReportSoon();
            }, () => Interlocked.Exchange(ref landingsLost, 1));
            if (landings == null) landingsLost = 1;

            // Whether the file at the destination is really the whole file.
            //
            // Not "did robocopy print its name", which was the first attempt and
            // is wrong: its stdout is block-buffered through the redirected pipe,
            // so the line for a file it has only *started* arrives when the
            // buffer is flushed — which killing it does. Every half-written file
            // therefore announced itself as finished, and the cleanup skipped
            // exactly the ones it existed to remove.
            //
            // Not size either, which is the second wrong answer: robocopy
            // preallocates, so a file that is one percent written already has its
            // final length. That is the whole reason a cancelled 20GB copy left
            // something indistinguishable from a finished one.
            //
            // Not the timestamp either, which was the third wrong answer.
            // Robocopy stamps the source's last-write time onto the destination
            // as it closes the file, so an unclosed one keeps the time it was
            // created — which is *now*, and so is the source's whenever somebody
            // copies a file they have just made. Two files written seconds apart
            // match on any tolerance loose enough to be worth having.
            //
            // The contents are the honest signal. The tail alone was the fourth
            // wrong answer: robocopy does not always write front to back, and a
            // file killed part way has been found with its last 64KB written and
            // the two megabytes before them still zeros. So a file up to 64MB is
            // compared whole, and a bigger one at the tail and at blocks spaced
            // by doubling distances back from it — an unwritten stretch runs
            // from wherever the writing stopped up towards the end, so one of
            // those falls inside it whatever its size, for a few dozen reads.
            static bool LooksComplete(string source, string landing, DateTime startedUtc)
            {
                const int BlockBytes = 64 * 1024;
                const long WholeUpTo = 64L * 1024 * 1024;

                try
                {
                    var a = new FileInfo(NameRules.ExactPath(source));
                    var b = new FileInfo(NameRules.ExactPath(landing));
                    if (!a.Exists || !b.Exists) return false;
                    if (a.Length != b.Length) return false;
                    if (a.Length == 0) return true;

                    // Stamped with the source's time, which robocopy does as it
                    // closes the file, from a source older than this transfer —
                    // so not the "made a moment ago" coincidence above. Finished,
                    // without reading it: a late cancel of a big copy otherwise
                    // read back everything already copied.
                    if (a.LastWriteTimeUtc < startedUtc.AddSeconds(-2) &&
                        b.LastWriteTimeUtc == a.LastWriteTimeUtc)
                        return true;

                    const FileShare Share = FileShare.ReadWrite | FileShare.Delete;
                    using var fa = new FileStream(NameRules.ExactPath(source), FileMode.Open, FileAccess.Read, Share);
                    using var fb = new FileStream(NameRules.ExactPath(landing), FileMode.Open, FileAccess.Read, Share);
                    var x = new byte[BlockBytes];
                    var y = new byte[BlockBytes];

                    bool Same(long at, int count)
                    {
                        fa.Position = at;
                        fb.Position = at;
                        fa.ReadExactly(x, 0, count);
                        fb.ReadExactly(y, 0, count);
                        return x.AsSpan(0, count).SequenceEqual(y.AsSpan(0, count));
                    }

                    // A source that itself ends in zeros — a WAV with a silent
                    // tail, a disk image, a preallocated database — matches a
                    // copy that stopped anywhere near its end. Unverifiable, so
                    // not kept: the source is still there to copy again.
                    int tail = (int)Math.Min(BlockBytes, a.Length);
                    if (!Same(a.Length - tail, tail)) return false;

                    // Compared whole, zeros and all, which proves it either way.
                    if (a.Length <= WholeUpTo)
                    {
                        for (long at = 0; at < a.Length - tail; at += BlockBytes)
                            if (!Same(at, (int)Math.Min(BlockBytes, a.Length - tail - at))) return false;
                        return true;
                    }

                    // Sampled, a zero tail proves nothing.
                    if (x.AsSpan(0, tail).IndexOfAnyExcept((byte)0) < 0) return false;

                    for (long back = 2L * BlockBytes; back <= a.Length; back *= 2)
                        if (!Same(a.Length - back, BlockBytes)) return false;
                    return Same(0, BlockBytes);
                }
                catch
                {
                    // Unreadable means unverifiable, and an unverifiable file is
                    // not one to keep on the strength of a guess.
                    return false;
                }
            }

            // Takes back what a cancelled transfer half wrote.
            //
            // Robocopy is killed where it stands, and it preallocates: cancelling
            // a 20GB copy five seconds in left a 20GB file at the destination
            // whose first six gigabytes were the real thing and whose remaining
            // fourteen were zeros. Nothing about it looked wrong — same name,
            // same size as the source, sitting right there in the folder — so the
            // only way to find out was to read it, and the worst case is somebody
            // deleting the original because the copy "obviously worked".
            //
            // Deliberately narrow. Only files under a destination that did not
            // exist before this transfer, and only ones robocopy never reported
            // as finished. Anything else is left exactly where it is: an
            // over-eager cleanup here deletes somebody's data, which is a great
            // deal worse than the thing it is fixing.
            //
            // A plain comment, not ///. A doc comment on a local function is
            // CS1587 and the compiler drops it.
            void RemoveHalfWritten(IReadOnlyCollection<string>? failedSources = null)
            {
                var planned = new List<(string Source, string Landing)>();
                var considered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void Consider(string source, string landing)
                {
                    if (considered.Add(landing)) planned.Add((source, landing));
                }

                // Written during this transfer: a file robocopy still has open
                // carries the time it was created, and only a finished one has
                // the source's date stamped back on. A few seconds of slack for a
                // share whose clock is not quite ours.
                bool WrittenDuring(string landing)
                {
                    try { return File.GetLastWriteTimeUtc(NameRules.ExactPath(landing)) >= transferStartedUtc.AddSeconds(-5); }
                    catch { return false; }
                }

                if (failedSources != null)
                {
                    // After a failure rather than a cancel: only the destinations
                    // of the files robocopy itself said it could not copy, and only
                    // if this transfer wrote them. Left there, a part-written file
                    // dated "now" is "newer" to every later Fill gaps, which then
                    // never copies it again and reports success.
                    foreach (var source in failedSources)
                        if (LandingFor(source) is { } landing &&
                            (created.ContainsKey(landing) || WrittenDuring(landing)))
                            Consider(source, landing);
                }
                else
                {
                    // From what actually landed, not from the plan. The planning
                    // walk stops when the transfer is cancelled — and a cancel
                    // early enough lands before it has started at all — so a
                    // plan-driven cleanup found nothing to check.
                    //
                    // Everything under a destination that did not exist before.
                    foreach (var (root, origin) in newRootSources)
                    {
                        try
                        {
                            if (File.Exists(NameRules.ExactPath(root))) { Consider(origin, root); continue; }
                            if (!Directory.Exists(NameRules.ExactPath(root))) continue;

                            var prefix = Prefix(root);
                            foreach (var landed in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                                     {
                                         RecurseSubdirectories = true,
                                         IgnoreInaccessible = true,
                                         AttributesToSkip = FileAttributes.ReparsePoint,
                                     }))
                                if (landed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                    Consider(Prefix(origin) + landed[prefix.Length..], landed);
                        }
                        catch { }
                    }

                    // Every file this transfer created, wherever it is. That was
                    // the missing half: Fill gaps resumed into a folder that was
                    // already there, was cancelled again, and left its part-written
                    // file behind, dated now — which every later Fill gaps then
                    // excluded as "newer" and reported success over.
                    foreach (var landing in created.Keys)
                        if (SourceFor(landing) is { } source) Consider(source, landing);

                    // And when the watcher cannot be believed — it overflowed, or
                    // could not watch at all — or files were written over rather
                    // than created, which raises no event: whatever in the
                    // destinations was written during this transfer.
                    if (Volatile.Read(ref landingsLost) != 0 || conflictPolicy == PasteConflictPolicy.Overwrite)
                    {
                        foreach (var (landing, source) in looseLandings)
                            if (WrittenDuring(landing)) Consider(source, landing);

                        foreach (var (dest, from) in treeJobs)
                        {
                            try
                            {
                                var tree = new DirectoryInfo(dest);
                                if (!tree.Exists) continue;
                                foreach (var file in tree.EnumerateFiles("*", new EnumerationOptions
                                         {
                                             RecurseSubdirectories = true,
                                             IgnoreInaccessible = true,
                                             AttributesToSkip = FileAttributes.ReparsePoint,
                                         }))
                                {
                                    if (file.LastWriteTimeUtc < transferStartedUtc.AddSeconds(-5)) continue;
                                    if (!file.FullName.StartsWith(dest, StringComparison.OrdinalIgnoreCase)) continue;
                                    Consider(from + file.FullName[dest.Length..], file.FullName);
                                }
                            }
                            catch { }
                        }
                    }
                }

                int removed = 0, refused = 0, kept = 0;
                foreach (var (source, landing) in planned)
                {
                    var exactLanding = NameRules.ExactPath(landing);
                    if (!File.Exists(exactLanding)) continue;

                    // The source is gone, so this is a move that already finished
                    // this file and deleted the original. Removing the
                    // destination now would destroy the only copy there is — the
                    // one outcome worse than leaving a half-written file.
                    if (!File.Exists(NameRules.ExactPath(source))) { kept++; continue; }

                    if (LooksComplete(source, landing, transferStartedUtc)) { kept++; continue; }

                    // Retried, because the loser of the race is the file that
                    // stays. The process has been waited on, but a filter driver
                    // or an indexer can hold a handle for a moment longer, and
                    // giving up on the first refusal leaves exactly the file this
                    // is here to remove.
                    bool gone = false;
                    // Three seconds in all: a killed robocopy, an indexer or an
                    // antivirus scan can each hold the file for longer than the
                    // half second this used to allow, and the file left behind is
                    // the one this cleanup exists to remove.
                    for (int attempt = 0; attempt < 20 && !gone; attempt++)
                    {
                        try
                        {
                            var attributes = File.GetAttributes(exactLanding);
                            if (attributes.HasFlag(FileAttributes.ReadOnly))
                                File.SetAttributes(exactLanding, attributes & ~FileAttributes.ReadOnly);
                            File.Delete(exactLanding);
                            gone = true;
                        }
                        catch { Thread.Sleep(150); }
                    }

                    if (gone) removed++;
                    else
                    {
                        refused++;
                        errors.Add($"could not remove the half-copied \"{Path.GetFileName(landing)}\" " +
                                   (failedSources == null ? "after cancelling" : "after it failed") +
                                   " — it is incomplete");
                    }
                }

                Trace($"{(failedSources == null ? "cancelled" : "failed")}: removed {removed}, kept {kept}, " +
                      $"refused {refused}, of {planned.Count} planned");
            }

            Trace($"starting: jobs={jobs.Count} sources={clean.Count} " +
                  $"progress={(progress == null ? "null" : "set")}");

            // A ticker keeps speed and elapsed moving even while a single large
            // file is in flight and robocopy has printed nothing yet.
            using var ticker = new Timer(_ => Report(), null, 250, 250);

            // How long robocopy waits on a file it cannot open — held by another
            // program, or gone since the plan. See RetriesFor.
            // On a worker: it asks Windows about volumes, which on a mapped
            // drive whose server is asleep is not instant.
            var (retries, retryWait) = await Task.Run(() => RetriesFor(clean, destinationDir, cloudSource));
            bool fillGaps = conflictPolicy == PasteConflictPolicy.FillGaps;

            // Copied counts FILES, not robocopy invocations, and now from what
            // robocopy says it copied rather than from the plan: a file that
            // failed, one it would not put over a folder, and one Fill gaps left
            // alone were all counted as copied, and "Failed" counted runs.
            int jobCopied = 0, alreadyThere = 0;
            bool allCounted = true;
            var failedSources = new List<string>();

            foreach (var job in jobs)
            {
                if (token.IsCancellationRequested)
                {
                    try { await planning; } catch { }
                    await Task.Run(() => RemoveHalfWritten());
                    return new TransferResult(managedFiles + jobCopied + Volatile.Read(ref itemsDone), failed, true,
                        errors, Collisions(), alreadyThere);
                }

                var outcome = await RunOneAsync(
                    job, move, threads, fillGaps, cloudSource, retries, retryWait, line =>
                {
                    var name = MatchCopiedFile(line, sizeByName, out long size);
                    if (name == null) return;
                    current = name;
                    Interlocked.Add(ref bytesDone, size);
                    Interlocked.Increment(ref itemsDone);
                    ReportSoon();
                },
                (file, attempt) =>
                {
                    // Said, rather than sitting at a percentage that does not move.
                    Volatile.Write(ref current, $"Waiting for {RetryName(file)} (retry {attempt} of {retries})");
                    Report();
                },
                p => Volatile.Write(ref liveJob, p), token);

                // Carried over before the next job starts from zero, so a
                // multi-job transfer accumulates rather than restarting.
                Interlocked.Add(ref finishedJobBytes, outcome.BytesWritten);
                failedSources.AddRange(outcome.FailedSources);

                if (outcome.Counts is { } c)
                {
                    int problems = c.Failed + c.Mismatch + c.DirMismatch + c.DirFailed;

                    if (move)
                    {
                        // What is still in the source after a move is what did not
                        // move: the failures, and the rest — files the destination
                        // already had, which Fill gaps leaves and robocopy excludes.
                        // A Fill gaps move said "Move complete" over them.
                        int left = token.IsCancellationRequested ? 0 : await Task.Run(() => FilesLeftIn(job));
                        int stayed = Math.Max(0, left - c.Failed - c.Mismatch);
                        jobCopied += c.Copied + Math.Max(0, c.Skipped - stayed);
                        alreadyThere += stayed;
                    }
                    else if (fillGaps)
                    {
                        // Skipped is exactly "already there", which is the policy.
                        jobCopied += c.Copied;
                        alreadyThere += c.Skipped;
                    }
                    else
                    {
                        // Skipped as identical: already the file that was asked for.
                        jobCopied += c.Copied + c.Skipped;
                    }

                    failed += problems;
                    if (!outcome.Ok && problems == 0) failed++;
                }
                else
                {
                    allCounted = false;
                    if (!outcome.Ok) failed++;
                }

                if (!outcome.Ok) { jobsFailed++; errors.AddRange(outcome.Errors); }
            }

            try { await planning; } catch { }
            ticker.Change(Timeout.Infinite, Timeout.Infinite);

            // Before the final report, or a late event lands a name on top of it.
            landings?.Dispose();

            // From robocopy's own count wherever it gave one. Otherwise, as
            // before, from the plan when every job succeeded: the per-line count
            // only rises for output matched against a name, and the planning
            // walk runs alongside the copy, so a fast copy of a small folder
            // reported having transferred nothing at all.
            copied = managedFiles + (allCounted
                ? jobCopied
                : jobsFailed == 0
                    ? Math.Max(Volatile.Read(ref itemsTotal), Volatile.Read(ref itemsDone))
                    : Volatile.Read(ref itemsDone));

            if (token.IsCancellationRequested)
            {
                // On a worker. Deleting a part-written 20GB file, and waiting out
                // a handle that has not been released yet, is filesystem work —
                // and this continuation resumes on the UI thread, which measured
                // 294ms of the window not answering at the exact moment somebody
                // has just asked for something to stop.
                await Task.Run(() => RemoveHalfWritten());
                return new TransferResult(copied, failed, true, errors, Collisions(), alreadyThere);
            }

            // A file robocopy gave up on may have been part written.
            if (failedSources.Count > 0)
                await Task.Run(() => RemoveHalfWritten(failedSources));

            // Final report with the real totals now that planning has finished.
            if (progress != null)
            {
                double seconds = Math.Max(0.001, started.Elapsed.TotalSeconds);
                long done = Math.Max(BytesLanded(), 0);
                progress.Report(new TransferProgress(
                    done, Interlocked.Read(ref bytesTotal),
                    renamedItems + Volatile.Read(ref itemsDone), renamedItems + Volatile.Read(ref itemsTotal),
                    Volatile.Read(ref current), done / seconds, started.Elapsed));
            }

            return new TransferResult(copied, failed, false, errors, Collisions(), alreadyThere);
        }

        /// <summary>Files still in a moved job's source: what did not move.</summary>
        private static int FilesLeftIn(Job job)
        {
            try
            {
                if (job.WholeTree)
                    return Directory.Exists(NameRules.ExactPath(job.SourceDir)) ? SafeEnumerateFiles(job.SourceDir).Count() : 0;
                return job.Files.Count(f => File.Exists(NameRules.ExactPath(Path.Combine(job.SourceDir, f))));
            }
            catch { return 0; }
        }

        /// <summary>
        /// The name of a file robocopy is retrying, in its own letters. Robocopy
        /// writes its output in the console code page, so anything outside it
        /// arrives as "?" — which is a wildcard for exactly one character, so the
        /// folder is asked which file that was.
        ///
        /// The output is now read in the OEM code page it is written in
        /// (<see cref="RobocopyOutputEncoding"/>), so an accent the code page has
        /// comes through as itself. What it has not — Japanese, most of
        /// everything else — is still "?", and a name read in the wrong page
        /// is not the file's at all; either way, any "?" or non-ASCII letter is
        /// taken as one unknown character and the folder is asked.
        /// </summary>
        internal static string RetryName(string path)
        {
            var name = Path.GetFileName(path);
            if (name.All(c => c < 0x80 && c != '?')) return name;
            try
            {
                var folder = Path.GetDirectoryName(path);
                if (folder == null || folder.Contains('?')) return name;
                var exactFolder = NameRules.ExactPath(folder);
                if (File.Exists(Path.Combine(exactFolder, name))) return name;

                var pattern = new string(name.Select(c => c >= 0x80 || c == '?' ? '?' : c).ToArray());
                var found = Directory.EnumerateFiles(exactFolder, pattern).Take(2).ToList();
                if (found.Count == 1) return Path.GetFileName(found[0]);
            }
            catch { }
            return name;
        }

        /// <summary>
        /// The code page robocopy writes its standard output in: the console's,
        /// which for the hidden console it is started with is the OEM page —
        /// 437 or 850 here, not the ANSI 1252 it was being read as, which turned
        /// "café" into "caf‚".
        /// </summary>
        internal static readonly Lazy<System.Text.Encoding> RobocopyOutputEncoding = new(() =>
        {
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                int page = 437;
                try { page = (int)GetOEMCP(); } catch { }
                try { return System.Text.Encoding.GetEncoding(page); }
                catch { return System.Text.Encoding.GetEncoding(437); }
            }
            catch { return System.Text.Encoding.Latin1; }
        });

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        /// <summary>
        /// Calls <paramref name="started"/> with the full path of each file
        /// created under <paramref name="destinationDir"/>, or returns null when
        /// the folder cannot be watched. The caller decides which of them belong
        /// to the transfer; anything else may be writing there too.
        ///
        /// Created, not Changed: every write to a large file is a Changed event,
        /// and each one would be a cross-thread report to draw a name that has
        /// not changed. A file robocopy overwrites rather than creates is named
        /// by its own line instead, later, as before.
        ///
        /// <paramref name="lost"/> is called when Windows dropped events — the
        /// buffer overflowed — so the caller knows the list of created files is
        /// no longer the whole of it.
        /// </summary>
        private static FileSystemWatcher? WatchLandings(string destinationDir, Action<string> started, Action lost)
        {
            try
            {
                var watcher = new FileSystemWatcher(destinationDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName,
                    InternalBufferSize = 64 * 1024,
                };

                watcher.Created += (_, e) =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(e.FullPath)) started(e.FullPath);
                    }
                    catch { }
                };
                watcher.Error += (_, _) =>
                {
                    try { lost(); } catch { }
                };

                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch
            {
                return null;
            }
        }

        private static List<Job> BuildJobs(IReadOnlyList<string> sources, string destinationDir, List<string> errors,
            ISet<string>? refused = null)
        {
            var jobs = new List<Job>();
            var looseFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in sources)
            {
                try
                {
                    var exact = NameRules.ExactPath(source);
                    if (Directory.Exists(exact))
                    {
                        var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                        // A drive root has no name, so Path.Combine would return
                        // the destination itself and tip the whole drive loose
                        // into it instead of into a folder. Refuse rather than
                        // guess at a name the user did not choose.
                        if (string.IsNullOrEmpty(name))
                        {
                            errors.Add($"{source}: copying a whole drive is not supported; open it and copy its contents");
                            refused?.Add(source);
                            continue;
                        }

                        var dest = Path.Combine(destinationDir, name);

                        if (IsWithin(dest, source))
                        {
                            errors.Add($"{name}: cannot copy a folder into itself");
                            refused?.Add(source);
                            continue;
                        }
                        jobs.Add(new Job(source, dest, new List<string>(), WholeTree: true));
                    }
                    else if (File.Exists(exact))
                    {
                        var dir = Path.GetDirectoryName(source)!;
                        if (!looseFiles.TryGetValue(dir, out var list))
                            looseFiles[dir] = list = new List<string>();
                        list.Add(Path.GetFileName(source));
                    }
                    else { errors.Add($"{source}: not found"); refused?.Add(source); }
                }
                catch (Exception ex) { errors.Add($"{source}: {ex.Message}"); refused?.Add(source); }
            }

            // Files sharing a parent go in one robocopy call, which is what makes
            // /MT worth anything on a slow share.
            foreach (var (dir, files) in looseFiles)
            {
                // Robocopy given the same source and destination would treat the
                // files as already in place and silently do nothing, which reads
                // as a hang. Say so instead.
                if (string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar),
                                  Path.GetFullPath(destinationDir).TrimEnd(Path.DirectorySeparatorChar),
                                  StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("Source and destination are the same folder");
                    foreach (var file in files) refused?.Add(Path.Combine(dir, file));
                    continue;
                }
                // In batches that fit on a command line. All of a folder's files
                // went on one, and a thousand selected files with ordinary names
                // passed Windows' 32,767-character limit: robocopy never started.
                var batch = new List<string>();
                int length = 0;
                foreach (var file in files)
                {
                    if (batch.Count > 0 && length + file.Length + 3 > CommandLineBudget)
                    {
                        jobs.Add(new Job(dir, destinationDir, batch, WholeTree: false));
                        batch = new List<string>();
                        length = 0;
                    }
                    batch.Add(file);
                    length += file.Length + 3;
                }
                if (batch.Count > 0) jobs.Add(new Job(dir, destinationDir, batch, WholeTree: false));
            }

            return jobs;
        }

        /// <summary>
        /// What robocopy's own summary says one job did, per file — the number
        /// of files it copied, skipped (already there, or excluded), would not
        /// put over a folder of the same name, and failed; and the same for
        /// folders where it matters.
        /// </summary>
        internal sealed record JobCounts(int Copied, int Skipped, int Mismatch, int Failed, int DirMismatch, int DirFailed);

        private sealed record JobOutcome(
            bool Ok, List<string> Errors, long BytesWritten, JobCounts? Counts, List<string> FailedSources);

        /// <summary>What a robocopy log says, read after the job.</summary>
        internal sealed class RobocopyLog
        {
            /// <summary>Files that failed and stayed failed, by path, with the reason.</summary>
            public readonly Dictionary<string, string> FileErrors = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>Files robocopy would not put over a folder of the same name.</summary>
            public readonly List<string> Mismatched = new();

            public JobCounts? Counts;
        }

        private static readonly Regex SummaryRow =
            new(@"^\s*[^:\d\t]+:\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Reads a robocopy log: the failures that were never retried into
        /// success, the files it refused to put over folders, and the summary
        /// table. Pure, so the parsing is tested on its own.
        ///
        /// A failed file is reported as a dated ERROR line naming it, with the
        /// reason on the line after. It is kept by file and dropped when that
        /// file is later copied: a file that failed once and then went across
        /// on a retry is not a failure. By the whole path when the line gives
        /// one: by name alone, cover.jpg copying in the next album cleared the
        /// failure of this one's.
        /// </summary>
        internal static RobocopyLog ParseLog(IEnumerable<string> lines)
        {
            var log = new RobocopyLog();
            string? reasonFor = null;
            bool inSummary = false;
            var rows = new List<long[]>();

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r', '\n');

                var error = RobocopyError.Match(line);
                if (error.Success)
                {
                    var message = line[error.Groups[1].Index..].Trim();
                    var file = FailedFile(message);
                    log.FileErrors[file] = message;
                    reasonFor = file;
                    continue;
                }

                if (reasonFor != null)
                {
                    var file = reasonFor;
                    reasonFor = null;
                    if (!string.IsNullOrWhiteSpace(line) && log.FileErrors.TryGetValue(file, out var said))
                        log.FileErrors[file] = said + ": " + line.Trim();
                    continue;
                }

                if (line.TrimStart().StartsWith("-----", StringComparison.Ordinal)) { inSummary = true; continue; }

                if (inSummary)
                {
                    // As long, and never thrown: the Bytes row of anything over
                    // 2GB does not fit an int, and parsing it as one threw out of the
                    // whole transfer after it had finished — "Copy failed", and
                    // a multi-item move left half done. A number too big even
                    // for a long is held at the top rather than failing either.
                    var row = SummaryRow.Match(line);
                    if (row.Success)
                        rows.Add(Enumerable.Range(1, 6).Select(i =>
                            long.TryParse(row.Groups[i].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                                ? n : long.MaxValue).ToArray());
                    continue;
                }

                var tail = line.TrimEnd();
                int tab = tail.LastIndexOf('\t');
                tail = (tab >= 0 ? tail[(tab + 1)..] : tail).Trim();
                if (tail.Length == 0) continue;

                if (line.Contains("*MISMATCH", StringComparison.OrdinalIgnoreCase))
                {
                    log.Mismatched.Add(tail);
                    continue;
                }

                bool fullPath = Path.IsPathFullyQualified(tail);
                var name = Path.GetFileName(tail);
                if (log.FileErrors.Count > 0)
                    foreach (var key in log.FileErrors.Keys.ToList())
                        if (fullPath
                                ? string.Equals(key, tail, StringComparison.OrdinalIgnoreCase)
                                : string.Equals(Path.GetFileName(key), name, StringComparison.OrdinalIgnoreCase))
                            log.FileErrors.Remove(key);
            }

            // Dirs, Files, Bytes, in that order, whatever the labels are called
            // in this language: Total, Copied, Skipped, Mismatch, FAILED, Extras.
            if (rows.Count >= 2)
                log.Counts = new JobCounts(
                    Copied: Count(rows[1][1]), Skipped: Count(rows[1][2]), Mismatch: Count(rows[1][3]),
                    Failed: Count(rows[1][4]), DirMismatch: Count(rows[0][3]), DirFailed: Count(rows[0][4]));

            static int Count(long n) => (int)Math.Min(n, int.MaxValue);

            return log;
        }

        /// <summary>
        /// Runs one robocopy job. <paramref name="onProcess"/> is handed the live
        /// process while it runs and null the moment it stops being safe to touch,
        /// so the progress ticker can ask the operating system how many bytes it
        /// has written — see <see cref="BytesWritten"/>. The count comes back with
        /// the result because the next job starts its own counter at zero.
        /// <paramref name="onRetry"/> hears each file robocopy is about to try
        /// again, and which attempt it will be.
        /// </summary>
        private static async Task<JobOutcome> RunOneAsync(
            Job job, bool move, int threads, bool fillGaps, bool cloudSource, int retries, int retryWait,
            Action<string> onLine, Action<string, int> onRetry, Action<Process?> onProcess, CancellationToken token)
        {
            var errors = new List<string>();

            var args = new List<string>
            {
                QuoteDir(job.SourceDir),
                QuoteDir(job.DestDir),
            };
            args.AddRange(job.Files.Select(Quote));

            if (job.WholeTree) args.Add("/E");                 // include subdirectories, even empty

            // Junctions are not followed. The planner already skips them, and
            // robocopy by default does not: moving a folder that held a junction
            // moved — and so deleted — whatever the junction pointed at, anywhere
            // on the disk. A self-referencing one ("Application Data") recursed
            // until the paths were too long. A *selected* junction never gets
            // here — /XJ does not cover the root of a job — see RunAsync.
            args.Add("/XJ");
            if (move) args.Add(job.WholeTree ? "/MOVE" : "/MOV");

            // "Only what is missing", which robocopy expresses by exclusion.
            //
            // It decides what to copy by comparing size and timestamp, and
            // classifies each file as lonely (only at the source), same, newer,
            // older or changed. Excluding the last three leaves exactly the
            // lonely ones — a file the destination does not have — and "same" is
            // skipped anyway. So a folder that copied six hundred of a thousand
            // files and stopped copies the four hundred and re-reads nothing.
            //
            // Deliberately *not* /XN alone, which would still recopy anything
            // whose timestamp drifted. Between two filesystems that happens for
            // reasons that have nothing to do with the contents — and recopying
            // forty gigabytes because a clock disagrees is the failure this
            // policy exists to avoid.
            //
            // Which is also why nothing this transfer part-wrote may be left
            // behind: a file robocopy had open is dated "now", so it is "newer",
            // and this exclusion keeps it for ever. RunAsync's cleanup is what
            // makes the exclusion safe.
            if (fillGaps)
            {
                args.Add("/XC");    // exclude changed
                args.Add("/XN");    // exclude newer
                args.Add("/XO");    // exclude older
            }
            args.Add("/MT:" + Math.Clamp(threads, 1, 128));

            // Robocopy copies attributes, and a placeholder carries Offline — so the file that landed in Downloads had the
            // right bytes, the right size and the Offline flag, which Explorer
            // draws as unavailable and plenty of programs refuse to open. It
            // looked exactly like the copy had not happened. /A-:O takes it off
            // every file written; /DCOPY:T keeps a folder's timestamps and none
            // of its placeholder attributes.
            if (cloudSource)
            {
                args.Add("/A-:O");
                args.Add("/DCOPY:T");
            }

            // Retries: long for a share, short for a local disk (RetriesFor).
            //
            // Twenty retries five seconds apart is right for a NAS that drops
            // for a few seconds or a laptop changing access point — one retry a
            // second later fails every file in flight over an interruption that
            // lasted less than a minute. It is wrong for a local file that is
            // simply held open by another program, or deleted since the plan:
            // that never fixes itself, and it was a hundred seconds per file of
            // a window at 100 percent saying nothing. Locally it is two retries
            // a second apart, and either way the window now says what it is
            // waiting for. Cancelling is unaffected: the process is killed
            // outright, and never waits out its own retries.
            args.Add("/R:" + retries);
            args.Add("/W:" + retryWait);
            args.Add("/BYTES");
            args.Add("/NJH");                                   // no job header
            args.Add("/NDL");                                   // no directory list
            args.Add("/NP");                                    // no per-file percentage spam
            args.Add("/XX");                                    // nothing about files only the destination has

            // The job summary is kept, because it is the only place robocopy
            // says per file what it did; and so is a log of everything, in
            // UTF-16. Its standard output is in the console code page, so a name
            // outside it — any Japanese, most accents — arrived as "???" in the
            // very error that had to say which file failed. /TEE keeps standard
            // output as well, which the progress reads; without it everything
            // goes to the log and the window sees nothing until the end.
            var logPath = Path.Combine(Path.GetTempPath(), $"explorernative-robocopy-{Guid.NewGuid():N}.log");
            var logName = Path.GetFileName(logPath);
            args.Add("/UNILOG:" + Quote(logPath));
            args.Add("/TEE");

            var psi = new ProcessStartInfo("robocopy.exe")
            {
                Arguments = string.Join(" ", args),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = RobocopyOutputEncoding.Value,
                StandardErrorEncoding = RobocopyOutputEncoding.Value,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            // The live view of the output: progress, and the retries. Failures
            // are read afterwards from the log, which has the names right; what
            // is collected here is only for when the log cannot be read.
            var liveLines = new List<string>();
            var attempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            bool summaryStarted = false;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var line = e.Data;
                lock (liveLines) liveLines.Add(line);

                // "Log File : …", which /TEE prints first.
                if (line.Contains(logName, StringComparison.OrdinalIgnoreCase)) return;

                var error = RobocopyError.Match(line);
                if (error.Success)
                {
                    var file = FailedFile(line[error.Groups[1].Index..].Trim());
                    int attempt;
                    lock (attempts) attempts[file] = attempt = attempts.GetValueOrDefault(file) + 1;

                    // The attempt that has just failed is followed by a wait while
                    // there are retries left — that is the moment to say so.
                    if (attempt <= retries)
                        try { onRetry(file, attempt); } catch { }
                    return;
                }

                if (line.TrimStart().StartsWith("-----", StringComparison.Ordinal)) summaryStarted = true;
                if (summaryStarted) return;

                onLine(line);
            };
            // The stderr callback runs on a threadpool thread while this method is
            // reading and returning the same list. Unsynchronised that is a torn
            // List<T> — a lost message at best, an IndexOutOfRange at worst.
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (errors) errors.Add(e.Data.Trim());
            };

            try
            {
                try
                {
                    if (!process.Start())
                        return new JobOutcome(false, new List<string> { "Could not start robocopy" }, 0, null, new List<string>());
                }
                catch (Exception ex)
                {
                    return new JobOutcome(false, new List<string> { "robocopy failed to start: " + ex.Message }, 0, null,
                        new List<string>());
                }

                // Published only once it is running, and taken back inside the finally
                // below — before `using` disposes it, so the ticker can never reach a
                // handle that has gone.
                onProcess(process);
                try
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    try
                    {
                        await process.WaitForExitAsync(token);

                        // WaitForExitAsync returns when the process object is signalled,
                        // which happens before the redirected pipes have been drained. The
                        // synchronous overload is what waits for the reader threads, so
                        // without it the last few files robocopy printed never reached the
                        // progress handler and the transfer appeared to stall at 97%.
                        process.WaitForExit();
                    }
                    catch (OperationCanceledException)
                    {
                        // Not while it is reading a Drive placeholder: a process
                        // ended in the middle of one never finishes exiting, and only
                        // a restart of Windows clears it. Its reads are failed first,
                        // on a worker — this may be the window's thread.
                        int id = 0;
                        try { id = process.Id; } catch { }
                        var standDown = BeforeKill;
                        if (standDown != null && id != 0)
                            try { await Task.Run(() => standDown(id)); } catch { }

                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }

                        // Wait for it to actually be gone before returning. Kill only
                        // asks; until the process has exited it still holds the
                        // destination file open, and the cleanup that runs next then
                        // finds every delete refused and leaves the half-written file
                        // exactly where it was — the thing the cleanup exists to
                        // prevent, defeated by a race with a dying process.
                        try { process.WaitForExit(5000); } catch { }
                        if (id != 0) try { AfterKill?.Invoke(id); } catch { }

                        lock (errors)
                            return new JobOutcome(false, new List<string>(errors), BytesWritten(process), null,
                                new List<string>());
                    }

                    int code = process.ExitCode;

                    // The log, in its own letters; the live output only if the log
                    // could not be read.
                    RobocopyLog log;
                    try { log = ParseLog(File.ReadAllLines(logPath, System.Text.Encoding.Unicode)); }
                    catch
                    {
                        // Guarded too: whatever the live output holds, a job that
                        // has finished is reported, never thrown out of RunAsync.
                        try
                        {
                            lock (liveLines) log = ParseLog(liveLines.Where(l => !l.Contains(logName, StringComparison.OrdinalIgnoreCase)).ToList());
                        }
                        catch { log = new RobocopyLog(); }
                    }

                    var counts = log.Counts;
                    int mismatched = Math.Max(counts?.Mismatch ?? 0, log.Mismatched.Count);
                    int foldersMismatched = counts?.DirMismatch ?? 0;

                    // Exit code 4 is "mismatched": a file robocopy would not put
                    // over a folder of the same name, deeper in the tree. It is
                    // below the failure threshold, so it was success.
                    bool ok = code < FirstFailureCode && mismatched == 0 && foldersMismatched == 0;

                    if (!ok)
                    {
                        var explained = new List<string>(log.FileErrors.Values);
                        foreach (var path in log.Mismatched)
                            explained.Add($"{path}: a folder of that name is already there");
                        if (mismatched > log.Mismatched.Count)
                            explained.Add($"{NameRules.Items(mismatched - log.Mismatched.Count, "file")} in \"{job.SourceDir}\" " +
                                          "could not go over a folder of the same name");
                        if (foldersMismatched > 0)
                            explained.Add($"{NameRules.Items(foldersMismatched, "folder")} in \"{job.SourceDir}\" " +
                                          "could not go over a file of the same name");

                        lock (errors)
                        {
                            errors.AddRange(explained);
                            if (explained.Count == 0)
                                errors.Add($"robocopy reported failures copying \"{job.SourceDir}\" (exit code {code})");
                        }
                    }

                    lock (errors)
                        return new JobOutcome(ok, new List<string>(errors), BytesWritten(process), counts,
                            log.FileErrors.Keys.ToList());
                }
                finally { onProcess(null); }
            }
            finally
            {
                // Gone, whatever happened. A killed robocopy may hold it a moment.
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        if (File.Exists(logPath)) File.Delete(logPath);
                        break;
                    }
                    catch { Thread.Sleep(50); }
                }
            }
        }

        /// <summary>
        /// A transfer, written down as it happens.
        ///
        /// Null unless a harness is watching, like the hotkey log. A transfer
        /// reports itself through a modal dialog and a progress callback, so when
        /// the dialog is the thing that is wrong there is nothing left to ask.
        /// Set EXPLORERNATIVE_TRANSFERLOG to a path.
        /// </summary>
        internal static void Trace(string line)
        {
            var path = Environment.GetEnvironmentVariable("EXPLORERNATIVE_TRANSFERLOG");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                File.AppendAllText(path,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
            }
            catch
            {
                // A diagnostic that cannot be written is not a reason to stop.
            }
        }

        /// <summary>
        /// How far along to say a transfer is, given both answers.
        ///
        /// Pure, because the arithmetic is the part worth testing — the rest is a
        /// kernel call and a timer. Three rules, each of which exists for a case
        /// that really happened:
        ///
        /// <paramref name="measured"/> wins when it is larger, which is nearly
        /// always: robocopy's stdout is block-buffered through a pipe, so the
        /// parsed count sits at one file's worth while fifty have been copied.
        ///
        /// <paramref name="parsed"/> wins when *it* is larger, which is when the
        /// kernel counter cannot be read for the running process. (A move within
        /// one volume used to be described here as robocopy renaming; it does
        /// not — /MOVE copies and deletes — which is why RunAsync renames such
        /// moves itself before robocopy is started.)
        ///
        /// And neither may exceed <paramref name="total"/> or fall below
        /// <paramref name="alreadyShown"/>. The kernel counts a little more than
        /// file data — metadata, its own output — so it can overshoot at the end,
        /// and a job finishing between the ticker reading the process and its
        /// total being banked can dip. A progress figure that reads 104 percent,
        /// or that counts down, is a bug whatever caused it.
        /// </summary>
        internal static long ProgressBytes(long parsed, long measured, long total, long alreadyShown)
        {
            long best = Math.Max(parsed, measured);
            if (total > 0 && best > total) best = total;
            return best < alreadyShown ? alreadyShown : best;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumePathNameW(string fileName, char[] volumePathName, int bufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, char[] volumeName, int bufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetDriveTypeW(string rootPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);

        private const uint DriveRemote = 4;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint QueryDosDeviceW(string deviceName, char[] targetPath, int max);

        /// <summary>The folder a path's volume is mounted at: "C:\", or a mount-point folder.</summary>
        private static string? VolumePathOf(string path, int depth = 0)
        {
            try
            {
                var plain = NameRules.PlainPath(path);

                // A subst letter is asked as the folder it stands for, and first.
                // GetVolumePathName answers for one perfectly happily — "S:\" —
                // and the volume name of "S:\" is then refused, so a move from a
                // subst letter to the disk under it was a copy rather than a
                // rename. "\??\C:\folder" is a subst; a real drive's device is
                // "\Device\HarddiskVolume3", and anything else is left alone.
                if (plain.Length >= 2 && plain[1] == ':' && char.IsLetter(plain[0]))
                {
                    var target = new char[1024];
                    if (QueryDosDeviceW(plain[..2], target, target.Length) != 0)
                    {
                        int stop = Array.IndexOf(target, '\0');
                        var device = new string(target, 0, stop < 0 ? target.Length : stop);
                        if (device.StartsWith(@"\??\", StringComparison.Ordinal) && device.Length >= 6 && device[5] == ':')
                        {
                            var resolved = device[4..].TrimEnd('\\') + "\\" + plain[2..].TrimStart('\\');
                            if (!string.Equals(resolved[..2], plain[..2], StringComparison.OrdinalIgnoreCase))
                                return depth < 4 ? VolumePathOf(resolved, depth + 1) : null;
                        }
                    }
                }

                var buffer = new char[1024];
                if (!GetVolumePathNameW(plain, buffer, buffer.Length)) return null;
                int end = Array.IndexOf(buffer, '\0');
                return new string(buffer, 0, end < 0 ? buffer.Length : end);
            }
            catch { return null; }
        }

        /// <summary>
        /// The volume a path is on, as Windows names it ("\\?\Volume{…}\"), or
        /// null for anything that has no such name — a network share among them.
        ///
        /// Not the drive letter. A folder that is a mount point puts paths under
        /// C: on another volume, and a subst drive puts another letter's paths on
        /// this one.
        /// </summary>
        internal static string? VolumeOf(string path)
        {
            var mount = VolumePathOf(path);
            if (mount == null) return null;
            try
            {
                if (!mount.EndsWith('\\')) mount += "\\";
                var name = new char[128];
                if (!GetVolumeNameForVolumeMountPointW(mount, name, name.Length)) return null;
                int end = Array.IndexOf(name, '\0');
                return new string(name, 0, end < 0 ? name.Length : end);
            }
            catch { return null; }
        }

        /// <summary>
        /// Whether two paths are on one volume, so that moving between them can
        /// be a rename. Unknown counts as no: the answer only decides whether a
        /// rename is tried, and the rename itself never copies (see
        /// <see cref="TryRename"/>), so a wrong yes costs one refused call and a
        /// wrong no costs a copy.
        /// </summary>
        public static bool SameVolume(string a, string b)
        {
            var va = VolumeOf(a);
            return va != null && string.Equals(va, VolumeOf(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A share, or a drive letter mapped to one.</summary>
        internal static bool IsRemote(string path)
        {
            try
            {
                var plain = NameRules.PlainPath(path);
                if (plain.StartsWith(@"\\", StringComparison.Ordinal)) return true;
                var mount = VolumePathOf(plain);
                return mount != null && GetDriveTypeW(mount.EndsWith('\\') ? mount : mount + "\\") == DriveRemote;
            }
            catch { return false; }
        }

        /// <summary>
        /// Renames, and only renames: no copy across volumes and nothing replaced.
        /// False whenever it cannot be done as a rename, so the caller can copy
        /// instead. A junction or symbolic link is renamed as itself.
        /// </summary>
        internal static bool TryRename(string from, string to)
        {
            try { return MoveFileExW(NameRules.ExactPath(from), NameRules.ExactPath(to), 0); }
            catch { return false; }
        }

        /// <summary>
        /// How many times robocopy tries a file again, and how long it waits
        /// between. See where the switches are added for why the two answers.
        /// </summary>
        internal static (int Count, int WaitSeconds) RetriesFor(
            IEnumerable<string> sources, string destination, bool cloudSource)
        {
            if (cloudSource || IsRemote(destination)) return (20, 5);
            foreach (var folder in sources.Select(s => Path.GetDirectoryName(NameRules.PlainPath(s)) ?? s)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                if (IsRemote(folder)) return (20, 5);
            return (2, 1);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

        /// <summary>
        /// Bytes a robocopy process has actually written, straight from the
        /// kernel. Zero for anything it cannot ask about — a process that has
        /// been disposed, or none at all — which is why the caller only ever
        /// takes this as the larger of two answers rather than the answer.
        ///
        /// Still correct after the process has exited: the handle stays valid
        /// until the object is disposed, and the counters keep their final
        /// values, which is what makes the total for a finished job readable.
        ///
        /// Internal so a test can prove it reads the field it means to. IO_COUNTERS
        /// is six identical unsigned longs, so naming the wrong one compiles
        /// perfectly and returns a plausible number — the same class of mistake as
        /// a COM vtable slot, and checked the same way: write a known amount and
        /// see whether this is what moved.
        /// </summary>
        internal static long BytesWritten(Process? process)
        {
            if (process == null) return 0;
            try
            {
                if (!GetProcessIoCounters(process.Handle, out var counters)) return 0;
                long written = (long)counters.WriteTransferCount;
                return written < 0 ? 0 : written;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Robocopy prints one line per file. Rather than depend on its exact
        /// column layout, the line is matched against the names we already know
        /// we are copying — which is stable across robocopy versions and locales.
        /// </summary>
        private static string? MatchCopiedFile(string line, Dictionary<string, long> sizeByName, out long size)
        {
            size = 0;
            if (string.IsNullOrWhiteSpace(line)) return null;

            var trimmed = line.TrimEnd();
            var lastTab = trimmed.LastIndexOf('\t');
            var candidate = (lastTab >= 0 ? trimmed[(lastTab + 1)..] : trimmed).Trim();
            if (candidate.Length == 0) return null;

            var name = Path.GetFileName(candidate);
            if (name.Length == 0) return null;

            lock (sizeByName)
            {
                if (sizeByName.TryGetValue(name, out size))
                {
                    // Consume it so a retry line cannot double-count.
                    sizeByName.Remove(name);
                    return name;
                }
            }

            // Unknown name, but robocopy still prints a byte count we can use.
            var m = Regex.Match(trimmed, @"^\s*(\d+)\s");
            if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out size))
                return name;

            return null;
        }

        private static IEnumerable<string> SafeEnumerateFiles(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var dir = stack.Pop();

                string[] subDirs;
                try { subDirs = Directory.GetDirectories(NameRules.ExactPath(dir)); }
                catch { continue; }

                foreach (var sub in subDirs)
                {
                    try { if (NameRules.IsLink(new DirectoryInfo(NameRules.ExactPath(sub)))) continue; }
                    catch { continue; }
                    stack.Push(NameRules.PlainPath(sub));
                }

                string[] files;
                try { files = Directory.GetFiles(NameRules.ExactPath(dir)); }
                catch { continue; }

                foreach (var f in files) yield return NameRules.PlainPath(f);
            }
        }

        private static bool IsWithin(string candidate, string ancestor)
        {
            try
            {
                var a = Path.GetFullPath(ancestor).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (c.StartsWith(a, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }

            // And by where they really are: a junction in the destination that
            // leads back inside the source is the same recursion in other letters.
            return ReparseLinks.ResolvedWithin(candidate, ancestor);
        }

        private static string Quote(string s) => "\"" + s + "\"";

        /// <summary>
        /// Quotes a directory for robocopy's command line.
        ///
        /// Two traps here. A trailing backslash immediately before the closing
        /// quote is read as an escape, so "C:\dir\" swallows the quote. And
        /// trimming it off a drive root leaves "D:", which does not mean the
        /// root of D — it means the current directory on D, so a copy would
        /// land somewhere else entirely. A root is therefore written "D:\."
        /// which has no trailing separator and still resolves to the root.
        /// </summary>
        public static string QuoteDir(string path)
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // "D:" or "\\server\share" left with nothing after the separator.
            if (trimmed.Length == 0 || trimmed.EndsWith(':'))
                return Quote(trimmed + Path.DirectorySeparatorChar + ".");

            return Quote(trimmed);
        }

        public static string FormatSpeed(double bytesPerSecond, SizeUnitStyle style) =>
            FormatSpeed(bytesPerSecond, style, SizeFormatter.DefaultDecimals);

        public static string FormatSpeed(double bytesPerSecond, SizeUnitStyle style, int decimals)
        {
            if (bytesPerSecond <= 0) return "unknown";
            return SizeFormatter.Format((long)bytesPerSecond, style, decimals) + " per second";
        }

        /// <summary>
        /// "2024/01/02 03:04:05 ERROR 32 (0x00000020) Copying File C:\a.txt",
        /// with the part from ERROR on captured.
        /// </summary>
        /// <summary>The path an ERROR line is about: everything from its drive or share on.</summary>
        private static string FailedFile(string message)
        {
            int at = message.IndexOf(":\\", StringComparison.Ordinal);
            if (at > 0) return message[(at - 1)..].Trim();
            at = message.IndexOf("\\\\", StringComparison.Ordinal);
            return at >= 0 ? message[at..].Trim() : message;
        }

        private static readonly Regex RobocopyError =
            new(@"^\s*\d{4}/\d\d/\d\d \d\d:\d\d:\d\d (ERROR \d+ \(0x[0-9A-Fa-f]+\).*)$", RegexOptions.Compiled);

        /// <summary>
        /// Read out in the progress window, so one is "1 second", not
        /// "1 seconds".
        /// </summary>
        public static string FormatDuration(TimeSpan span)
        {
            if (span.TotalSeconds < 1) return "less than a second";
            if (span.TotalMinutes < 1) return NameRules.Items((int)span.TotalSeconds, "second");
            if (span.TotalHours < 1)
                return $"{NameRules.Items((int)span.TotalMinutes, "minute")}, {NameRules.Items(span.Seconds, "second")}";
            return $"{NameRules.Items((int)span.TotalHours, "hour")}, {NameRules.Items(span.Minutes, "minute")}";
        }
    }

    /// <summary>
    /// Junctions, which .NET can read but not make. Moving one to another volume
    /// means making it again there; see <see cref="FileOperations.MoveLink"/>.
    /// </summary>
    internal static class ReparseLinks
    {
        private const uint MountPointTag = 0xA0000003;
        private const uint SetReparsePoint = 0x000900A4;
        private const uint GenericWrite = 0x40000000;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;
        private const uint OpenReparsePoint = 0x00200000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint SizeHigh, SizeLow, Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string fileName, out FindData data);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr find);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code,
            byte[] input, int inputLength, IntPtr output, int outputLength, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file,
            char[] path, uint length, uint flags);

        /// <summary>
        /// Where a path really is, every junction and symbolic link on the way
        /// to it followed: the deepest part of it that exists is asked of
        /// Windows (GetFinalPathNameByHandle) and the rest, not made yet, is put
        /// back on the end. The path as given when nothing can be asked.
        ///
        /// For the "into itself" checks. A folder moved into a junction that
        /// points inside it looked, by its letters, like a move somewhere else
        /// entirely — and the copy then walked into its own output, S\inner\S\
        /// inner\S, until the paths were too long.
        /// </summary>
        public static string Resolved(string path)
        {
            try
            {
                var plain = NameRules.PlainPath(path);
                if (!Path.IsPathFullyQualified(plain)) return path;
                plain = plain.Replace('/', '\\');

                var existing = plain.TrimEnd('\\');
                var tail = "";
                while (existing.Length > 0)
                {
                    var exact = NameRules.ExactPath(existing.Length == 2 && existing[1] == ':' ? existing + "\\" : existing);
                    if (Directory.Exists(exact) || File.Exists(exact)) break;
                    int cut = existing.LastIndexOf('\\');
                    if (cut <= 0) return path;
                    tail = existing[(cut + 1)..] + (tail.Length > 0 ? "\\" + tail : "");
                    existing = existing[..cut];
                }
                if (existing.Length == 0) return path;

                var open = NameRules.ExactPath(existing.Length == 2 && existing[1] == ':' ? existing + "\\" : existing);
                using var handle = CreateFileW(open, 0, 7 /* read, write, delete */, IntPtr.Zero, OpenExisting,
                    BackupSemantics, IntPtr.Zero);
                if (handle.IsInvalid) return path;

                var buffer = new char[1024];
                uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
                if (length > buffer.Length)
                {
                    buffer = new char[length + 1];
                    length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
                }
                if (length == 0 || length > buffer.Length) return path;

                var final = new string(buffer, 0, (int)length);
                if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) final = @"\\" + final[8..];
                else if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];
                // A volume with no letter has no path the rest of the application
                // can compare against; the letters as given are the best answer.
                if (final.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase)) return path;

                final = final.TrimEnd('\\');
                return tail.Length > 0 ? final + "\\" + tail : (final.Length == 2 ? final + "\\" : final);
            }
            catch { return path; }
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> is <paramref name="ancestor"/> or
        /// inside it, by where both really are — see <see cref="Resolved"/> —
        /// as well as by their letters.
        /// </summary>
        public static bool ResolvedWithin(string candidate, string ancestor)
        {
            static string Slashed(string p) => p.TrimEnd('\\') + "\\";
            try
            {
                return Slashed(Resolved(candidate)).StartsWith(Slashed(Resolved(ancestor)), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>A junction (a mount point to a folder), as against a symbolic link.</summary>
        public static bool IsJunction(string path)
        {
            var find = FindFirstFileW(NameRules.ExactPath(path.TrimEnd('\\')), out var data);
            if (find == new IntPtr(-1)) return false;
            FindClose(find);
            return (data.Attributes & (uint)FileAttributes.ReparsePoint) != 0 && data.Reserved0 == MountPointTag;
        }

        /// <summary>Makes <paramref name="link"/> a new junction to <paramref name="target"/>.</summary>
        public static void CreateJunction(string link, string target)
        {
            // What a junction stores is "\??\C:\target"; .NET hands back either
            // form depending on the version, so both prefixes are taken off.
            foreach (var prefix in new[] { @"\??\", @"\\?\" })
                if (target.StartsWith(prefix, StringComparison.Ordinal)) target = target[prefix.Length..];

            // A volume mount point is registered with the mount manager, which a
            // reparse buffer alone does not do. Refused rather than half made.
            if (target.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(target))
                throw new IOException("This link cannot be made again on another drive");

            var substitute = System.Text.Encoding.Unicode.GetBytes(@"\??\" + target);
            var print = System.Text.Encoding.Unicode.GetBytes(target);
            int pathBuffer = substitute.Length + 2 + print.Length + 2;
            int dataLength = 8 + pathBuffer;
            var buffer = new byte[8 + dataLength];

            BitConverter.GetBytes(MountPointTag).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)dataLength).CopyTo(buffer, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);                          // substitute offset
            BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
            BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);   // print offset
            BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
            substitute.CopyTo(buffer, 16);
            print.CopyTo(buffer, 16 + substitute.Length + 2);

            var exact = NameRules.ExactPath(link);
            Directory.CreateDirectory(exact);
            try
            {
                using var handle = CreateFileW(exact, GenericWrite, 0, IntPtr.Zero, OpenExisting,
                    BackupSemantics | OpenReparsePoint, IntPtr.Zero);
                if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (!DeviceIoControl(handle, SetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            catch
            {
                try { Directory.Delete(exact); } catch { }
                throw;
            }
        }
    }
}
