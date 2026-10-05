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

    public sealed record TransferResult(
        int Copied,
        int Failed,
        bool Cancelled,
        IReadOnlyList<string> Errors,
        ConflictOutcomes? Conflicts = null)
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
            bool cloudSource = false)
        {
            // cloudSource: copying *out of* the Google Drive letter, where every
            // source file is a placeholder this same process downloads while
            // robocopy reads it. How many to read at once is the caller's decision
            // (MainForm.TransferThreads, which says one); what changes here is the
            // attributes — see where the switches are added.

            var errors = new List<string>();
            int copied = 0, failed = 0;

            // Robocopy overwrites unconditionally, so the conflict policy has to
            // be applied before it ever runs. Anything that would collide is
            // split off: skipped, or copied under a fresh name by the managed
            // engine. Everything non-colliding still goes through robocopy at
            // full speed, which is the overwhelmingly common case.
            var colliding = new List<string>();
            var clean = new List<string>();
            var overwritten = new List<string>();

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
                bool collides;
                try { collides = File.Exists(target) || Directory.Exists(target); }
                catch { collides = false; }

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
                        sourceIsFolder = Directory.Exists(source);
                        targetIsFolder = Directory.Exists(target);
                    }
                    catch { sourceIsFolder = targetIsFolder = false; }

                    if (sourceIsFolder != targetIsFolder)
                    {
                        failed++;
                        errors.Add($"{name}: a {(targetIsFolder ? "folder" : "file")} of that name is already there");
                        continue;
                    }
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
                else if (collides && conflictPolicy == PasteConflictPolicy.FillGaps && File.Exists(target))
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
                        overwritten.Add(name);
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

            // Built from whichever counts are filled in by the time it is called,
            // so every exit reports what actually happened rather than only the
            // exits that happen to run to the end.
            ConflictOutcomes Collisions() => new(
                renamedNames.Take(ConflictOutcomes.MaxRemembered).ToArray(),
                skippedNames.Take(ConflictOutcomes.MaxRemembered).ToArray(),
                overwritten.Take(ConflictOutcomes.MaxRemembered).ToArray(),
                renamed, skipped, overwritten.Count);

            if (clean.Count == 0)
                return new TransferResult(managedFiles, failed, false, errors, Collisions());

            // Each refusal is a failure: "cannot copy a folder into itself" for
            // one of two folders went into the list and the other copied, and
            // with nothing counted the paste said "Copy complete" and the list
            // was never shown.
            int beforeRefusals = errors.Count;
            int jobsFailed = 0;
            var jobs = BuildJobs(clean, destinationDir, errors);
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
                        if (Directory.Exists(source))
                        {
                            foreach (var f in SafeEnumerateFiles(source))
                            {
                                try
                                {
                                    var len = new FileInfo(f).Length;
                                    Interlocked.Add(ref bytesTotal, len);
                                    Interlocked.Increment(ref itemsTotal);
                                    lock (sizeByName) sizeByName[Path.GetFileName(f)] = len;
                                }
                                catch { }
                            }
                        }
                        else if (File.Exists(source))
                        {
                            var len = new FileInfo(source).Length;
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
            // wins because neither is right on its own: a same-volume move
            // renames rather than copies and writes almost nothing, where the
            // parsed lines are correct.
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
                    ItemsFinished(),
                    Volatile.Read(ref itemsTotal),
                    Volatile.Read(ref current),
                    speed,
                    started.Elapsed));
            }

            using var landings = WatchLandings(destinationDir, name =>
            {
                Volatile.Write(ref current, name);
                Interlocked.Increment(ref filesStarted);
                ReportSoon();
            });

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
                    var a = new FileInfo(source);
                    var b = new FileInfo(landing);
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
                    using var fa = new FileStream(source, FileMode.Open, FileAccess.Read, Share);
                    using var fb = new FileStream(landing, FileMode.Open, FileAccess.Read, Share);
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
            void RemoveHalfWritten()
            {
                // From what actually landed, not from the plan. The planning
                // walk stops when the transfer is cancelled — and a cancel early
                // enough lands before it has started at all — so a plan-driven
                // cleanup found nothing to check and left the half-written file
                // it exists to remove. What is in a new destination is bounded by
                // what robocopy got to, which is small exactly when this matters.
                var planned = new List<(string Source, string Landing)>();
                foreach (var (root, origin) in newRootSources)
                {
                    try
                    {
                        if (File.Exists(root)) { planned.Add((origin, root)); continue; }
                        if (!Directory.Exists(root)) continue;

                        foreach (var landed in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                                 {
                                     RecurseSubdirectories = true,
                                     IgnoreInaccessible = true,
                                     AttributesToSkip = FileAttributes.ReparsePoint,
                                 }))
                            planned.Add((Path.Combine(origin, Path.GetRelativePath(root, landed)), landed));
                    }
                    catch { }
                }

                int removed = 0, refused = 0, kept = 0;
                foreach (var (source, landing) in planned)
                {
                    if (!File.Exists(landing)) continue;

                    // The source is gone, so this is a move that already finished
                    // this file and deleted the original. Removing the
                    // destination now would destroy the only copy there is — the
                    // one outcome worse than leaving a half-written file.
                    if (!File.Exists(source)) { kept++; continue; }

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
                        try { File.Delete(landing); gone = true; }
                        catch { Thread.Sleep(150); }
                    }

                    if (gone) removed++;
                    else
                    {
                        refused++;
                        errors.Add($"could not remove the half-copied \"{Path.GetFileName(landing)}\" " +
                                   "after cancelling — it is incomplete");
                    }
                }

                Trace($"cancelled: removed {removed}, kept {kept}, refused {refused}, " +
                      $"of {planned.Count} planned");
            }

            Trace($"starting: jobs={jobs.Count} sources={clean.Count} " +
                  $"progress={(progress == null ? "null" : "set")}");

            // A ticker keeps speed and elapsed moving even while a single large
            // file is in flight and robocopy has printed nothing yet.
            using var ticker = new Timer(_ => Report(), null, 250, 250);

            // Copied counts FILES, not robocopy invocations. The old code added
            // the managed engine's file count to a per-job tally, so "3 succeeded"
            // could mean three files or three folders of a thousand each — the one
            // number the user reads after a partial failure meant nothing.
            foreach (var job in jobs)
            {
                if (token.IsCancellationRequested)
                {
                    try { await planning; } catch { }
                    await Task.Run(RemoveHalfWritten);
                    return new TransferResult(managedFiles + Volatile.Read(ref itemsDone), failed, true, errors, Collisions());
                }

                var (ok, jobErrors, jobBytes) = await RunOneAsync(
                    job, move, threads, conflictPolicy == PasteConflictPolicy.FillGaps, cloudSource, line =>
                {
                    var name = MatchCopiedFile(line, sizeByName, out long size);
                    if (name == null) return;
                    current = name;
                    Interlocked.Add(ref bytesDone, size);
                    Interlocked.Increment(ref itemsDone);
                    ReportSoon();
                }, p => Volatile.Write(ref liveJob, p), token);

                // Carried over before the next job starts from zero, so a
                // multi-job transfer accumulates rather than restarting.
                Interlocked.Add(ref finishedJobBytes, jobBytes);

                if (!ok) { failed++; jobsFailed++; errors.AddRange(jobErrors); }
            }

            try { await planning; } catch { }
            ticker.Change(Timeout.Infinite, Timeout.Infinite);

            // Before the final report, or a late event lands a name on top of it.
            landings?.Dispose();

            // Taken from the plan when every job succeeded, because that is the
            // number that is actually true. The per-line count only rises for
            // output we managed to match against a name, and the planning walk
            // runs alongside the copy — so files copied before their names were
            // known were never counted, and a fast copy of a small folder
            // reported having transferred nothing at all.
            // By the jobs, not by failures in general: a refused source did not
            // make the others' files any less copied.
            copied = managedFiles + (jobsFailed == 0
                ? Math.Max(Volatile.Read(ref itemsTotal), Volatile.Read(ref itemsDone))
                : Volatile.Read(ref itemsDone));

            if (token.IsCancellationRequested)
            {
                // On a worker. Deleting a part-written 20GB file, and waiting out
                // a handle that has not been released yet, is filesystem work —
                // and this continuation resumes on the UI thread, which measured
                // 294ms of the window not answering at the exact moment somebody
                // has just asked for something to stop.
                await Task.Run(RemoveHalfWritten);
                return new TransferResult(copied, failed, true, errors, Collisions());
            }

            // Final report with the real totals now that planning has finished.
            if (progress != null)
            {
                double seconds = Math.Max(0.001, started.Elapsed.TotalSeconds);
                long done = Math.Max(BytesLanded(), 0);
                progress.Report(new TransferProgress(
                    done, Interlocked.Read(ref bytesTotal),
                    Volatile.Read(ref itemsDone), Volatile.Read(ref itemsTotal),
                    Volatile.Read(ref current), done / seconds, started.Elapsed));
            }

            return new TransferResult(copied, failed, false, errors, Collisions());
        }

        /// <summary>
        /// Calls <paramref name="started"/> with the name of each file created
        /// under <paramref name="destinationDir"/>, or returns null when the
        /// folder cannot be watched — the transfer does not depend on it, it only
        /// tells the window sooner.
        ///
        /// Created, not Changed: every write to a large file is a Changed event,
        /// and each one would be a cross-thread report to draw a name that has
        /// not changed. A file robocopy overwrites rather than creates is named
        /// by its own line instead, later, as before.
        /// </summary>
        private static FileSystemWatcher? WatchLandings(string destinationDir, Action<string> started)
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
                        var name = Path.GetFileName(e.Name);
                        if (!string.IsNullOrEmpty(name)) started(name);
                    }
                    catch { }
                };

                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch
            {
                return null;
            }
        }

        private static List<Job> BuildJobs(IReadOnlyList<string> sources, string destinationDir, List<string> errors)
        {
            var jobs = new List<Job>();
            var looseFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in sources)
            {
                try
                {
                    if (Directory.Exists(source))
                    {
                        var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                        // A drive root has no name, so Path.Combine would return
                        // the destination itself and tip the whole drive loose
                        // into it instead of into a folder. Refuse rather than
                        // guess at a name the user did not choose.
                        if (string.IsNullOrEmpty(name))
                        {
                            errors.Add($"{source}: copying a whole drive is not supported; open it and copy its contents");
                            continue;
                        }

                        var dest = Path.Combine(destinationDir, name);

                        if (IsWithin(dest, source))
                        {
                            errors.Add($"{name}: cannot copy a folder into itself");
                            continue;
                        }
                        jobs.Add(new Job(source, dest, new List<string>(), WholeTree: true));
                    }
                    else if (File.Exists(source))
                    {
                        var dir = Path.GetDirectoryName(source)!;
                        if (!looseFiles.TryGetValue(dir, out var list))
                            looseFiles[dir] = list = new List<string>();
                        list.Add(Path.GetFileName(source));
                    }
                    else errors.Add($"{source}: not found");
                }
                catch (Exception ex) { errors.Add($"{source}: {ex.Message}"); }
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
        /// Runs one robocopy job. <paramref name="onProcess"/> is handed the live
        /// process while it runs and null the moment it stops being safe to touch,
        /// so the progress ticker can ask the operating system how many bytes it
        /// has written — see <see cref="BytesWritten"/>. The count comes back with
        /// the result because the next job starts its own counter at zero.
        /// </summary>
        private static async Task<(bool Ok, List<string> Errors, long BytesWritten)> RunOneAsync(
            Job job, bool move, int threads, bool fillGaps, bool cloudSource, Action<string> onLine,
            Action<Process?> onProcess, CancellationToken token)
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
            // until the paths were too long.
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
            // Retries, and enough of them to sit out a share that blinked.
            //
            // /R:1 /W:1 was "one retry, not a million" — aimed at robocopy's
            // default of a million retries thirty seconds apart, which is a copy
            // that never returns. But one retry one second later is the other
            // extreme, and it is the wrong one for the case this application is
            // actually used for: a NAS that drops for a few seconds, a laptop
            // changing access point, a drive that spins up slowly. Every file in
            // flight fails, and a folder of thousands ends up mostly errors from
            // an interruption that lasted less than a minute.
            //
            // Twenty retries five seconds apart is up to a hundred seconds per
            // file — long enough for anything that fixes itself, and still
            // bounded, so a genuinely missing server ends the file rather than
            // the decade. Cancelling is unaffected: the process is killed
            // outright, and never waits out its own retries.
            args.Add("/R:20");
            args.Add("/W:5");
            args.Add("/BYTES");
            args.Add("/NJH");                                   // no job header
            args.Add("/NJS");                                   // no job summary
            args.Add("/NDL");                                   // no directory list
            args.Add("/NP");                                    // no per-file percentage spam

            var psi = new ProcessStartInfo("robocopy.exe")
            {
                Arguments = string.Join(" ", args),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            // Robocopy reports a failed file on standard output, as a dated
            // ERROR line naming it with the reason on the line after. Passed on
            // as progress, the file was counted as copied and the reason was
            // thrown away, leaving "exit code 8". Collected here and used only
            // if the job fails — a file that failed once and then copied on a
            // retry is not a failure.
            //
            // Kept by file, and dropped when that file is later copied: a file
            // that failed once and then went across on a retry was otherwise
            // listed as a failure beside the one that really failed. The reason
            // is joined to its own message, not to whatever the list ends with.
            var fileErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? reasonFor = null;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var line = e.Data;

                var error = RobocopyError.Match(line);
                if (error.Success)
                {
                    var message = line[error.Groups[1].Index..].Trim();
                    lock (fileErrors) fileErrors[FailedFile(message)] = message;
                    reasonFor = FailedFile(message);
                    return;
                }

                if (reasonFor != null)
                {
                    var file = reasonFor;
                    reasonFor = null;
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        lock (fileErrors)
                            if (fileErrors.TryGetValue(file, out var said))
                                fileErrors[file] = said + ": " + line.Trim();
                    }
                    return;
                }

                var copiedTail = line.TrimEnd();
                int tab = copiedTail.LastIndexOf('\t');
                copiedTail = (tab >= 0 ? copiedTail[(tab + 1)..] : copiedTail).Trim();
                // Robocopy names a copied file by its full path or by its name
                // alone, depending on the mode, so it is matched by name.
                var copiedName = copiedTail.Length > 0 ? Path.GetFileName(copiedTail) : "";
                if (copiedName.Length > 0)
                {
                    lock (fileErrors)
                    {
                        // By the whole path when the line gives one: by name
                        // alone, cover.jpg copying in the next album cleared the
                        // failure of this one's.
                        bool fullPath = Path.IsPathFullyQualified(copiedTail);
                        if (fileErrors.Count > 0)
                            foreach (var key in fileErrors.Keys.ToList())
                                if (fullPath
                                        ? string.Equals(key, copiedTail, StringComparison.OrdinalIgnoreCase)
                                        : string.Equals(Path.GetFileName(key), copiedName, StringComparison.OrdinalIgnoreCase))
                                    fileErrors.Remove(key);
                    }
                }

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
                if (!process.Start())
                    return (false, new List<string> { "Could not start robocopy" }, 0);
            }
            catch (Exception ex)
            {
                return (false, new List<string> { "robocopy failed to start: " + ex.Message }, 0);
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

                    lock (errors) return (false, new List<string>(errors), BytesWritten(process));
                }

                int code = process.ExitCode;
                if (code >= FirstFailureCode)
                {
                    lock (fileErrors)
                        lock (errors) errors.AddRange(fileErrors.Values);
                    lock (errors)
                        errors.Add($"robocopy reported failures copying \"{job.SourceDir}\" (exit code {code})");
                }

                lock (errors)
                    return (code < FirstFailureCode, new List<string>(errors), BytesWritten(process));
            }
            finally { onProcess(null); }
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
        /// <paramref name="parsed"/> wins when *it* is larger, which is a move
        /// within one volume: robocopy renames instead of copying and writes
        /// almost nothing, so the kernel counter stays near zero while files are
        /// genuinely being moved.
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
                try { subDirs = Directory.GetDirectories(dir); }
                catch { continue; }

                foreach (var sub in subDirs)
                {
                    try { if (NameRules.IsLink(new DirectoryInfo(sub))) continue; }
                    catch { continue; }
                    stack.Push(sub);
                }

                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { continue; }

                foreach (var f in files) yield return f;
            }
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
}
