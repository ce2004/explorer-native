using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// How hard to squeeze.
    ///
    /// Three, not a number from one to nine. Nine levels is a scale where nobody
    /// can say what six means, and this application already has a rule about
    /// offering a value somebody would actually pick — see SettingChoices and the
    /// combo boxes that used to step by a hundred. What the three mean is a
    /// measured fact about the trade, not a preference: on the corpus in
    /// CLAUDE.md, zipped, Fastest is five times quicker than Smallest and 8.4%
    /// bigger, and Balanced is two and a half times quicker for a file within a
    /// tenth of a percent of the smallest one.
    /// </summary>
    public enum ArchiveLevel
    {
        Fastest,
        Balanced,
        Smallest,
    }

    /// <summary>One entry inside an archive, as read out of its index.</summary>
    public sealed record ArchiveEntryInfo(
        string Name,
        long Size,
        DateTime Modified,
        bool IsDirectory);

    /// <summary>
    /// What a compress or an extract did.
    ///
    /// Shaped like <see cref="TransferResult"/> on purpose: the window reporting
    /// it is the same window, the sentences it produces are the same sentences,
    /// and a caller that already knows how to say "12 files, 1 failed" should not
    /// have to learn a second vocabulary for the same three numbers.
    /// </summary>
    public sealed record ArchiveResult(
        int Items,
        int Failed,
        bool Cancelled,
        IReadOnlyList<string> Errors,
        long BytesIn,
        long BytesOut,
        ConflictOutcomes? Conflicts = null,
        int LeftOut = 0)
    {
        public ConflictOutcomes Collisions => Conflicts ?? ConflictOutcomes.None;

        /// <summary>
        /// How much smaller it came out, as a percentage, or null when the
        /// question does not apply — an empty archive, or an extraction, where
        /// there is no ratio to report.
        /// </summary>
        public double? Saved =>
            BytesIn > 0 && BytesOut > 0 ? Math.Max(0, 100.0 - BytesOut * 100.0 / BytesIn) : null;
    }

    /// <summary>One thing on its way into an archive.</summary>
    public sealed record ArchiveItem(
        string Source,
        string Name,
        long Size,
        bool IsDirectory,
        DateTime Modified,
        FileAttributes Attributes);

    /// <summary>
    /// Compressing and extracting, and the one place that decides which engine
    /// does it.
    ///
    /// The shape follows the transfer code next door, because the two are the
    /// same kind of thing: something long, running off the UI thread, reporting
    /// through <see cref="TransferProgress"/> into a non-modal
    /// <see cref="ProgressForm"/>, cancellable, and cleaning up what it half
    /// wrote if it is stopped.
    /// </summary>
    public static class ArchiveEngine
    {
        /// <summary>
        /// How many workers to compress with.
        ///
        /// Deliberately not <see cref="FileOperations.RecommendedThreads"/>, and
        /// the difference is the point. That number is clamped to sixteen and
        /// floored at four because copying is bound by the disk or the network:
        /// the aim there is enough requests outstanding to keep a device busy,
        /// and a seventeenth only adds seek contention. Compression is bound by
        /// the processor. The right number of deflate workers is the number of
        /// cores, exactly, and floor of four on a two-core machine would mean two
        /// of them waiting for a core rather than working.
        ///
        /// A ceiling is kept only to stop something absurd on a machine with
        /// hundreds of cores, where the writer stitching the output together
        /// becomes the limit long before the workers do.
        /// </summary>
        public static int RecommendedThreads => Math.Clamp(Environment.ProcessorCount, 1, 64);

        /// <summary>
        /// How often progress may reach the UI thread.
        ///
        /// The same hundred milliseconds, and for the same reason, as
        /// DriveUpload.ReportMilliseconds: ten workers each finishing a small
        /// file every few milliseconds is thousands of cross-thread posts a
        /// second, all to redraw a number that changes faster than anybody can
        /// read it, queued onto the one thread that has to draw it.
        /// </summary>
        public const int ReportMilliseconds = 100;

        public static CompressionLevel ToCompressionLevel(ArchiveLevel level) => level switch
        {
            ArchiveLevel.Fastest => CompressionLevel.Fastest,
            ArchiveLevel.Smallest => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };

        // ---------- Planning ----------

        /// <summary>
        /// Walks the selection into the flat, ordered list of entries an archive
        /// is made of.
        ///
        /// Folders are listed as entries in their own right, not merely implied by
        /// the files inside them. That is the same lesson FileOperations learned:
        /// a plan that is only files loses every empty folder without a word, so a
        /// tree goes in and a different tree comes out, silently. An archive is
        /// worse than a copy for this, because the evidence is inside a file
        /// nobody will open until later.
        ///
        /// Names are relative to the folder the selection lives in and always use
        /// forward slashes, which is what zip and tar both require and what makes
        /// the result readable on something that is not Windows.
        /// </summary>
        /// <param name="left">Where to say what was deliberately left out: links.</param>
        /// <param name="unreadable">Where to say what could not be looked at.</param>
        public static List<ArchiveItem> Plan(IReadOnlyList<string> sources, CancellationToken token,
            List<string>? left = null, List<string>? unreadable = null)
        {
            var plan = new List<ArchiveItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();

                var trimmed = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var name = Path.GetFileName(trimmed);
                if (string.IsNullOrEmpty(name)) continue;

                // Two selected items with the same name cannot both be at the root
                // of one archive. It takes a pair of sources from different
                // folders to arrange, which the second pane makes easy.
                var unique = name;
                for (int n = 2; !seen.Add(unique); n++)
                    unique = $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";

                try
                {
                    if (Directory.Exists(trimmed))
                    {
                        var info = new DirectoryInfo(trimmed);
                        plan.Add(new ArchiveItem(trimmed, unique, 0, true, info.LastWriteTime, info.Attributes));
                        AddTree(plan, info, unique, token, left, unreadable);
                    }
                    else if (File.Exists(trimmed))
                    {
                        var info = new FileInfo(trimmed);
                        plan.Add(new ArchiveItem(trimmed, unique, info.Length, false, info.LastWriteTime, info.Attributes));
                    }
                    else
                    {
                        // Gone between being chosen and being compressed — moved,
                        // deleted, a share that dropped. Silently skipped, it made
                        // "Created x.7z" with no file and an empty zip.
                        unreadable?.Add($"{name}: it is no longer there");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unreadable?.Add($"{name}: {ex.Message}");
                    // A source that cannot be looked at is reported by the engine
                    // that tries to read it, with the name attached. Dropping it
                    // here would make it vanish from the item count as well, and
                    // then nothing would be missing from the totals to notice.
                }
            }

            return plan;
        }

        private static void AddTree(List<ArchiveItem> plan, DirectoryInfo folder, string prefix,
            CancellationToken token, List<string>? left, List<string>? unreadable)
        {
            token.ThrowIfCancellationRequested();

            // Materialised here, so a folder that cannot be listed says so
            // rather than going into the archive empty with nothing reported.
            List<FileSystemInfo> children;
            try { children = folder.EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                unreadable?.Add($"{prefix}: could not be read, so nothing in it was added ({ex.Message})");
                return;
            }

            foreach (var child in children)
            {
                token.ThrowIfCancellationRequested();

                // One scan, not three. EnumerateFileSystemInfos already carries
                // attributes, length and timestamp out of the call that found the
                // entry — asking again per file is three round trips on a share.
                var name = prefix + "/" + child.Name;

                // A link is followed by nothing here. A junction into a parent
                // folder is an archive that never finishes, and a symlink to
                // somewhere else on the machine is data leaving the tree somebody
                // thought they were compressing. Said, because a silent drop is a
                // file somebody does not know is missing.
                //
                // Only a link. Every reparse point used to be skipped, and a cloud
                // placeholder or a deduplicated file is one — so a folder on the
                // Drive letter compressed to an archive of empty folders.
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    string? linkTo;
                    try { linkTo = child.LinkTarget; }
                    catch { linkTo = ""; }

                    if (linkTo != null)
                    {
                        left?.Add($"{name}: a link, which was left out");
                        continue;
                    }
                }

                if (child is DirectoryInfo sub)
                {
                    plan.Add(new ArchiveItem(sub.FullName, name, 0, true, sub.LastWriteTime, sub.Attributes));
                    AddTree(plan, sub, name, token, left, unreadable);
                }
                else if (child is FileInfo file)
                {
                    plan.Add(new ArchiveItem(file.FullName, name, file.Length, false,
                        file.LastWriteTime, file.Attributes));
                }
            }
        }

        // ---------- Names that came out of an archive ----------

        /// <summary>
        /// Where an entry called <paramref name="entryName"/> is allowed to land
        /// under <paramref name="root"/>, or null if it is not allowed to land
        /// anywhere.
        ///
        /// This is the check that stops an archive writing outside the folder it
        /// is being extracted into. The attack is old enough to have a name — zip
        /// slip — and it is one line of archive to mount: an entry called
        /// "../../../../Windows/System32/whatever", or "C:\Windows\whatever", or
        /// a name with a drive-relative "C:file" in it. Every one of those is a
        /// legal string in both zip and tar, and neither format has any opinion
        /// about it; the extractor is the only thing standing there.
        ///
        /// It is done by resolving the combined path and then checking the result
        /// is genuinely underneath the root, rather than by looking for ".." in
        /// the name. Searching for the dangerous string is how this is usually got
        /// wrong: "a/..%2f", "a/.../b" on some readers, a name where the ".." only
        /// appears after a symlink is followed. Resolving first and comparing
        /// after asks the question nobody can dress up.
        /// </summary>
        public static string? SafeTarget(string root, string entryName, out string? reason)
        {
            reason = null;

            if (string.IsNullOrWhiteSpace(entryName)) return null;

            var slashed = entryName.Replace('\\', '/');

            // Asked before the leading slashes are trimmed, which is the whole
            // subtlety: "\\\\server\\share\\evil.txt" becomes "//server/share/..."
            // and trimming first turns it into the perfectly innocent-looking
            // "server/share/evil.txt". The check has to see the two slashes.
            if (slashed.StartsWith("//", StringComparison.Ordinal))
            {
                reason = $"\"{entryName}\" names a network path and was not extracted";
                return null;
            }

            // A single leading slash is dropped rather than refused, because that
            // is what every tar implementation does with one — "Removing leading
            // '/' from member names" — and the result lands inside the destination
            // like any other relative name.
            var cleaned = slashed.Trim('/');
            if (cleaned.Length == 0) return null;

            // A drive letter inside an entry name is never anything but an escape
            // attempt; "C:evil.txt" is a path relative to another drive's current
            // directory and there is no archive that legitimately needs one.
            if (cleaned.Length >= 2 && cleaned[1] == ':')
            {
                reason = $"\"{entryName}\" names an absolute path and was not extracted";
                return null;
            }

            foreach (var segment in cleaned.Split('/'))
            {
                if (segment.Length == 0) continue;

                if (segment == "." || segment == "..")
                {
                    reason = $"\"{entryName}\" points outside the folder and was not extracted";
                    return null;
                }

                // A name made on Linux can hold characters Windows will not store,
                // and a reserved device name or a trailing dot produces a file
                // that cannot afterwards be opened or deleted. Refused with the
                // name attached rather than quietly renamed: a silent rename is a
                // file somebody later cannot find, and a silent drop is a file
                // they do not know is missing.
                if (NameRules.DescribeBadName(segment) is { } complaint)
                {
                    reason = $"\"{entryName}\" cannot be stored on Windows: {char.ToLowerInvariant(complaint[0])}{complaint[1..]}";
                    return null;
                }
            }

            string full, rooted;
            try
            {
                rooted = Path.GetFullPath(root);
                full = Path.GetFullPath(Path.Combine(rooted, cleaned.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch
            {
                reason = $"\"{entryName}\" is not a usable name";
                return null;
            }

            var fence = rooted.EndsWith(Path.DirectorySeparatorChar)
                ? rooted
                : rooted + Path.DirectorySeparatorChar;

            if (full.StartsWith(fence, StringComparison.OrdinalIgnoreCase)) return full;

            reason = $"\"{entryName}\" points outside the folder and was not extracted";
            return null;
        }

        // ---------- Progress ----------

        /// <summary>
        /// The running totals, throttled on the way to the window.
        ///
        /// Every worker reports into this and exactly one of those reports every
        /// hundred milliseconds becomes a cross-thread post. See
        /// <see cref="ReportMilliseconds"/>: this is the same rule DriveUpload
        /// learned with four upload streams, and it arrives here with ten.
        /// </summary>
        public sealed class Reporter
        {
            private readonly IProgress<TransferProgress>? _sink;
            private readonly Stopwatch _clock = Stopwatch.StartNew();

            private long _bytesDone;
            private long _bytesTotal;
            private int _itemsDone;
            private int _itemsTotal;
            private string _current = "";
            private long _lastReportMs = -1;

            public Reporter(IProgress<TransferProgress>? sink) => _sink = sink;

            public long BytesDone => Interlocked.Read(ref _bytesDone);
            public int ItemsDone => Volatile.Read(ref _itemsDone);

            public void SetTotals(long bytes, int items)
            {
                Interlocked.Exchange(ref _bytesTotal, bytes);
                Interlocked.Exchange(ref _itemsTotal, items);
                Report(force: true);
            }

            // How far through the archive file the reading has got, for an
            // extraction that does not list the archive first.
            private Func<long>? _position;
            private long _positionTotal;
            private volatile bool _over;

            /// <summary>
            /// Measures progress through the compressed file rather than through
            /// what comes out of it.
            ///
            /// Knowing the uncompressed totals means listing the archive first,
            /// and for a compressed tar that is a whole decompression — measured,
            /// about half the time of the extraction. So an extraction with
            /// nothing to collide with skips it and reports how much of the
            /// archive has been read, which is honest at both ends and smooth in
            /// between. The item total is worked out from the same proportion
            /// and is exact by the end.
            /// </summary>
            public void Estimate(long archiveBytes, Func<long> position)
            {
                Interlocked.Exchange(ref _positionTotal, Math.Max(1, archiveBytes));
                _position = position;
                Report(force: true);
            }

            /// <summary>
            /// Names the thing being worked on.
            ///
            /// Forced, because the name is the one field where being a tenth of a
            /// second stale shows the wrong file against a finished count — the
            /// same reason DriveUpload forces a report as each file lands.
            /// </summary>
            public void Starting(string name)
            {
                Volatile.Write(ref _current, name);
                Report(force: false);
            }

            public void Advance(long bytes)
            {
                Interlocked.Add(ref _bytesDone, bytes);
                Report(force: false);
            }

            public void Finished(long bytes)
            {
                Interlocked.Add(ref _bytesDone, bytes);
                Interlocked.Increment(ref _itemsDone);
                Report(force: false);
            }

            /// <param name="finished">False for a cancellation, which did not get to the end.</param>
            public void Done(bool finished = true)
            {
                if (finished) _over = true;
                Report(force: true);
            }

            private void Report(bool force)
            {
                if (_sink == null) return;

                long now = _clock.ElapsedMilliseconds;
                if (!force)
                {
                    long last = Interlocked.Read(ref _lastReportMs);
                    if (now - last < ReportMilliseconds) return;
                    if (Interlocked.CompareExchange(ref _lastReportMs, now, last) != last) return;
                }
                else Interlocked.Exchange(ref _lastReportMs, now);

                var elapsed = TimeSpan.FromMilliseconds(now);
                long done = Interlocked.Read(ref _bytesDone);
                long total = Interlocked.Read(ref _bytesTotal);
                int items = Volatile.Read(ref _itemsDone);
                int itemsTotal = Volatile.Read(ref _itemsTotal);

                if (_position is { } position)
                {
                    total = Interlocked.Read(ref _positionTotal);
                    long at;
                    try { at = position(); }
                    catch { at = 0; }
                    done = _over ? total : Math.Clamp(at, 0, total);

                    // Extrapolated from how far through the file the entries so
                    // far have come, and never fewer than have been counted.
                    itemsTotal = _over || done <= 0
                        ? items
                        : (int)Math.Min(int.MaxValue, Math.Max(items, (long)Math.Round(items * (double)total / done)));
                }

                // Never past the end. An entry the system tar adds that was not in
                // the plan — a link it stored, a name that could not be matched —
                // took the bar to 154 percent.
                if (total > 0) done = Math.Min(done, total);
                if (itemsTotal > 0) items = Math.Min(items, itemsTotal);

                double speed = elapsed.TotalSeconds > 0.001 ? done / elapsed.TotalSeconds : 0;

                _sink.Report(new TransferProgress(
                    done,
                    total,
                    items,
                    itemsTotal,
                    Volatile.Read(ref _current),
                    speed,
                    elapsed));
            }
        }

        // ---------- Compressing ----------

        public static Task<ArchiveResult> CompressAsync(
            IReadOnlyList<string> sources,
            string archivePath,
            ArchiveFormat format,
            ArchiveLevel level,
            int threads,
            IProgress<TransferProgress>? progress,
            CancellationToken token)
        {
            return Task.Run(() => Compress(sources, archivePath, format, level, threads, progress, token), token);
        }

        private static ArchiveResult Compress(
            IReadOnlyList<string> sources,
            string archivePath,
            ArchiveFormat format,
            ArchiveLevel level,
            int threads,
            IProgress<TransferProgress>? progress,
            CancellationToken token)
        {
            if (!format.CanCreate)
                throw new NotSupportedException($"{format.Name} archives can be opened but not created.");

            var reporter = new Reporter(progress);
            var errors = new List<string>();

            RoboCopyEngine.Trace($"archive: creating {archivePath} as {format.Id} " +
                                 $"level={level} threads={threads} sources={sources.Count}");

            // Nothing is written until the plan exists, so an archive is never
            // left behind for a selection that could not even be walked.
            // A cancel while the selection is still being walked is a cancel, not
            // "Could not compress: A task was canceled".
            List<ArchiveItem> plan;
            // Links are left out by the engines that use the plan; the system
            // tar walks the disk itself and stores them, so it is not told.
            var leftOut = new List<string>();
            try { plan = Plan(sources, token, format.Engine is ArchiveEngineKind.Zip or ArchiveEngineKind.Tar ? leftOut : null, errors); }
            catch (OperationCanceledException)
            {
                return new ArchiveResult(0, 0, true, errors, 0, 0);
            }
            // Nothing left to put in it is a failure, not an empty archive: the
            // system tar made no file at all and reported success, and the zip
            // writer made one with nothing in it.
            if (plan.Count == 0)
                throw new IOException(errors.Count == 1
                    ? errors[0]
                    : errors.Count > 1
                        ? $"none of the {errors.Count} chosen items could be read"
                        : "there was nothing to compress");

            long totalBytes = plan.Sum(p => p.Size);
            reporter.SetTotals(totalBytes, plan.Count);

            if (format.SingleFile)
            {
                var files = plan.Where(p => !p.IsDirectory).ToList();
                if (files.Count != 1)
                    throw new NotSupportedException(
                        $"{format.Name} holds exactly one file, and {files.Count} were chosen.");
            }

            bool existed = File.Exists(archivePath);
            bool finished = false;

            // Replacing an archive is built beside it and moved into place, and
            // that is the difference between a cancelled second attempt costing
            // nothing and costing the archive.
            //
            // Every engine opens its output FileMode.Create, which truncates
            // what is already there before a single entry is written — so
            // "Replace it?" answered yes, then Escape two seconds later, left a
            // few megabytes of headers under the old name with no central
            // directory. The cleanup below could not help: it deliberately keeps
            // a file that was already there, on the grounds that it is somebody
            // else's, and by then somebody else's file was already gone.
            //
            // The extension stays last: bsdtar chooses the format from the name,
            // and "x.7z.creating-1234" chose none it knew — replacing any .7z,
            // .tar.xz, .tar.zst or .tar.bz2 failed with "Unknown module name".
            string working = existed ? StagingName(archivePath, format) : archivePath;

            try
            {
                var result = format.Engine switch
                {
                    ArchiveEngineKind.Zip => ZipEngine.Create(plan, working, level, threads, reporter, errors, token),
                    ArchiveEngineKind.Tar => TarEngine.Create(plan, working, format, level, threads, reporter, errors, token),
                    _ => BsdTar.Create(plan, working, format, level, threads, reporter, errors, token),
                };

                // Only now does the old one go. A move onto it is the last step
                // rather than the first.
                if (working != archivePath) File.Move(working, archivePath, overwrite: true);

                finished = true;
                reporter.Done();

                long produced = 0;
                try { produced = new FileInfo(archivePath).Length; } catch { }

                RoboCopyEngine.Trace($"archive: created {archivePath} items={result} " +
                                     $"in={totalBytes} out={produced} failed={errors.Count}");

                // Left out is not failed: a link is not a file that could not be
                // read. Listed after the failures, and counted on its own.
                int failures = errors.Count;
                errors.AddRange(leftOut);
                return new ArchiveResult(result, failures, false, errors, totalBytes, produced,
                    LeftOut: leftOut.Count);
            }
            catch (OperationCanceledException)
            {
                reporter.Done(finished: false);
                return new ArchiveResult(reporter.ItemsDone, errors.Count, true, errors, totalBytes, 0);
            }
            finally
            {
                // A half-written archive is worse than no archive: it has the
                // right name and the right extension and it opens to an error
                // somewhere in the middle, at whatever later moment somebody
                // needs what is in it. It is only removed if this call is what
                // created it — an archive that was already there before a
                // cancelled second attempt is somebody else's file.
                if (!finished)
                {
                    // The staged copy always goes; the named one only when this
                    // call is what created it.
                    try { if (File.Exists(working)) File.Delete(working); }
                    catch (Exception ex) { RoboCopyEngine.Trace($"archive: could not remove partial: {ex.Message}"); }
                }
            }
        }

        // ---------- Extracting ----------

        /// <summary>
        /// Everything inside an archive, without extracting any of it.
        ///
        /// Wanted twice over: for the totals a progress window is about, and to
        /// find out which names would collide before a single byte is written —
        /// which is what lets the same conflict question a paste asks be asked
        /// here, once, at the start, rather than file by file halfway through.
        /// </summary>
        /// <summary>
        /// The format of something being opened, or a sentence saying why it
        /// cannot be.
        ///
        /// Two different noes, and they deserve different sentences. Not an
        /// archive at all is one thing; a format that is recognised and still
        /// cannot be opened is another, and somebody handed a bare .xz needs to
        /// be told that rather than that their file is unrecognisable.
        /// </summary>
        private static ArchiveFormat Openable(string archivePath)
        {
            var format = ArchiveFormats.Identify(archivePath);

            if (format == null)
                throw new NotSupportedException(
                    $"{Path.GetFileName(archivePath)} is not an archive this can open.");

            if (!format.CanExtract)
                throw new NotSupportedException(
                    $"A bare {format.Extension} holds one compressed stream with no name in it, and " +
                    $"Windows ships no decoder this can reach it with. A .tar{format.Extension} opens normally.");

            return format;
        }

        public static Task<ArchiveResult> ExtractAsync(
            string archivePath,
            string destinationDir,
            PasteConflictPolicy policy,
            Func<IReadOnlyList<string>, FileOperations.ConflictChoice>? ask,
            int threads,
            IProgress<TransferProgress>? progress,
            CancellationToken token)
        {
            return Task.Run(() => Extract(archivePath, destinationDir, policy, ask, threads, progress, token), token);
        }

        private static ArchiveResult Extract(
            string archivePath,
            string destinationDir,
            PasteConflictPolicy policy,
            Func<IReadOnlyList<string>, FileOperations.ConflictChoice>? ask,
            int threads,
            IProgress<TransferProgress>? progress,
            CancellationToken token)
        {
            var format = Openable(archivePath);

            var reporter = new Reporter(progress);
            var errors = new List<string>();

            RoboCopyEngine.Trace($"archive: extracting {archivePath} ({format.Id}) to {destinationDir}");

            // The formats Windows holds the codecs for go a different way round,
            // and the reason is written out in BsdTar: the only names that survive
            // libarchive on a machine whose code page is not UTF-8 are the ones it
            // writes to disk itself. So it extracts into a staging folder inside
            // the destination and the conflict rules are applied to the result,
            // which means the question is asked after the decompression rather
            // than before it — and every name in it is correct.
            if (format.Engine == ArchiveEngineKind.BsdTar)
            {
                try
                {
                    var (moved, collisions) = BsdTar.Extract(
                        archivePath, destinationDir, policy, ask, reporter, errors, token);

                    reporter.Done();
                    RoboCopyEngine.Trace($"archive: extracted {moved} entries, {errors.Count} failed");
                    return new ArchiveResult(moved, errors.Count, false, errors, 0, reporter.BytesDone, collisions);
                }
                catch (OperationCanceledException)
                {
                    reporter.Done(finished: false);
                    return new ArchiveResult(reporter.ItemsDone, errors.Count, true, errors, 0, reporter.BytesDone);
                }
            }

            // The listing is for two things: the totals, and the names that
            // might collide with what is already there. Into a folder that does
            // not exist yet — which is what Extract makes — nothing can collide,
            // and for a tar the listing is a whole decompression of its own,
            // about half the time the extraction takes. So it is not done, and
            // the progress is measured through the archive file instead.
            //
            // A zip's listing is its central directory, which costs a read of
            // the end of the file and gives exact totals, so it is always read.
            bool listFirst = format.Engine == ArchiveEngineKind.Zip || !IsEmptyFolder(destinationDir);

            IReadOnlyList<ArchiveEntryInfo> entries;
            ExtractionRules? rules;
            try
            {
                if (format.Engine == ArchiveEngineKind.Zip)
                {
                    entries = ZipEngine.List(archivePath, token, out int files, out int locked);

                    // Said once, before any question about collisions is asked
                    // and before any folder is made for it. Each entry used to
                    // fail with "unsupported compression method", which reads as
                    // a fault in the file rather than a password.
                    if (locked > 0 && locked == files)
                        throw new NotSupportedException(ZipEngine.ProtectedMessage);
                }
                else if (listFirst)
                {
                    entries = TarEngine.List(archivePath, format, token);
                }
                else
                {
                    entries = Array.Empty<ArchiveEntryInfo>();
                }

                if (listFirst) reporter.SetTotals(entries.Sum(e => e.Size), entries.Count);
                rules = ExtractionRules.Build(entries, destinationDir, policy, ask, token);
            }
            catch (OperationCanceledException)
            {
                return new ArchiveResult(0, 0, true, errors, 0, 0);
            }

            if (rules == null)
                return new ArchiveResult(0, 0, true, errors, 0, 0);

            Directory.CreateDirectory(destinationDir);

            try
            {
                int done = format.Engine == ArchiveEngineKind.Zip
                    ? ZipEngine.Extract(archivePath, rules, threads, reporter, errors, token)
                    : TarEngine.Extract(archivePath, format, rules, threads, reporter, errors, token,
                        estimate: !listFirst);

                reporter.Done();
                RoboCopyEngine.Trace($"archive: extracted {done} entries, {errors.Count} failed");

                return new ArchiveResult(done, errors.Count, false, errors,
                    0, reporter.BytesDone, rules.Outcomes());
            }
            catch (OperationCanceledException)
            {
                reporter.Done(finished: false);
                return new ArchiveResult(reporter.ItemsDone, errors.Count, true, errors, 0, reporter.BytesDone,
                    rules.Outcomes());
            }
        }

        /// <summary>
        /// Whether nothing extracted into <paramref name="folder"/> can land on
        /// something already there: it does not exist, or it is empty.
        /// </summary>
        internal static bool IsEmptyFolder(string folder)
        {
            try { return !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any(); }
            catch { return false; }
        }

        /// <summary>
        /// The sentence for an archive that stopped making sense part way.
        ///
        /// Every reader produced its own: "Unable to read beyond the end of the
        /// stream", "unsupported compression method" for a damaged gzip, and
        /// "(null)" from the system tar for a truncated 7z. None of them says the
        /// one thing worth knowing, which is that the file is damaged and how
        /// much of it was saved.
        /// </summary>
        internal static string DamagedMessage(int extracted) => extracted > 0
            ? $"This archive is damaged or incomplete. {NameRules.Items(extracted)} {(extracted == 1 ? "was" : "were")} extracted before the damage."
            : "This archive is damaged or incomplete, and nothing in it could be extracted.";

        /// <summary>
        /// Writes an extracted file under a temporary name beside
        /// <paramref name="target"/> and moves it into place only once
        /// <paramref name="write"/> has finished without throwing.
        ///
        /// Every extractor used to open the target itself. A cancel, a read
        /// error or a truncated archive then left a file at the real name that
        /// looked complete and was not — and under Replace, the file it was
        /// replacing had already been emptied. Now the name is only ever the old
        /// file or the whole new one.
        /// </summary>
        internal static void WriteCommitted(string target, Action<FileStream> write, int bufferSize = 1 << 20)
        {
            var folder = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            // Short, and not built from the name: a name of 240 characters is
            // legal and "." + it + ".extracting-12345678" is not.
            var temporary = Path.Combine(folder ?? "",
                ".extracting-" + Guid.NewGuid().ToString("N")[..12] + ".tmp");

            try
            {
                // Readable as well, so a writer can check what it wrote before it
                // is given its name — the bare .gz reads back its last member.
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                           FileShare.None, bufferSize))
                    write(output);

                // Replace means replace, a read-only file included; the move is
                // refused for one otherwise, after "Replaced" has been decided.
                ClearReadOnly(target);
                File.Move(temporary, target, overwrite: true);
            }
            catch
            {
                try { File.Delete(temporary); } catch { }
                throw;
            }
        }

        internal static void ClearReadOnly(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
            catch { }
        }

        private static string StagingName(string archivePath, ArchiveFormat format)
        {
            var tag = ".creating-" + Guid.NewGuid().ToString("N")[..8];

            // Whichever of the format's extensions the name actually ends with —
            // ".txz" as well as ".tar.xz" — or bsdtar cannot tell the format.
            var extension = format.Extensions
                .Where(e => archivePath.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Length)
                .FirstOrDefault();

            return extension != null
                ? archivePath[..^extension.Length] + tag + extension
                : archivePath + tag;
        }
    }
}
