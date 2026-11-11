using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Concentus;

namespace ExplorerNative
{
    /// <summary>One file to convert, and where it goes.</summary>
    /// <param name="Replace">The person chose to replace a file already at <paramref name="Target"/>.</param>
    internal sealed record ConvertJob(string Source, string Target, long Bytes, bool Replace);

    /// <summary>What the batch would do before anything is written, and which targets are already taken.</summary>
    internal sealed class ConvertPlan
    {
        public List<ConvertJob> Jobs { get; } = new();

        /// <summary>Jobs whose target name is already in use by a file the person has.</summary>
        public List<ConvertJob> Clashing { get; } = new();

        /// <summary>Every name the plan hands out, so "keep both" numbers past all of them.</summary>
        internal HashSet<string> Claimed { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record ConvertFailure(string Name, string Reason);

    /// <summary>Google Drive, not the file, was the trouble: worth another try.</summary>
    internal sealed class ConvertRetryException : Exception
    {
        public ConvertRetryException(string message) : base(message) { }
    }

    internal sealed class ConvertResult
    {
        public int Converted;
        public int Skipped;

        /// <summary>Files left out because they are already the format asked for.</summary>
        public int AlreadyFormat;
        public bool Cancelled;
        public List<ConvertFailure> Failures { get; } = new();
        public List<string> Notes { get; } = new();
        public List<string> Written { get; } = new();

        /// <summary>Originals removed after their converted file was in place (Recycle Bin, or the Drive trash).</summary>
        public int OriginalsDeleted;

        /// <summary>Originals that were converted and then could not be removed, and why.</summary>
        public List<ConvertFailure> OriginalsKept { get; } = new();

        /// <summary>
        /// The one sentence said at the end: "Converted 12 files, 1 failed:
        /// name, reason." Failures name the file first so a long reason can be
        /// cut from the end.
        /// </summary>
        public string Summary(AudioFormatChoice choice)
        {
            if (Cancelled)
                return Converted == 0
                    ? "Converting cancelled"
                    : $"Converting cancelled after {NameRules.Items(Converted, "file")}";

            var text = Converted == 0 && Failures.Count == 0 && Skipped == 0 && AlreadyFormat == 0
                ? "Nothing to convert"
                : $"Converted {NameRules.Items(Converted, "file")} to {choice.Label}";

            if (AlreadyFormat > 0) text += $", {AlreadyFormat} already {choice.FormatName} and left alone";
            if (Skipped > 0) text += $", {Skipped} skipped because the name was taken";

            if (OriginalsDeleted > 0)
                text += OriginalsDeleted == 1 && Converted == 1 ? " and deleted the original"
                    : OriginalsDeleted == Converted ? " and deleted the originals"
                    : $" and deleted {OriginalsDeleted} of the originals";
            if (OriginalsKept.Count > 0)
            {
                var kept = OriginalsKept.Take(3).Select(k => $"{k.Name}, {k.Reason}");
                text += $", {NameRules.Items(OriginalsKept.Count, "original")} could not be deleted: {string.Join("; ", kept)}";
                if (OriginalsKept.Count > 3) text += $"; and {OriginalsKept.Count - 3} more";
            }

            if (Failures.Count > 0)
            {
                var named = Failures.Take(3).Select(f => $"{f.Name}, {f.Reason}");
                text += $", {Failures.Count} failed: {string.Join("; ", named)}";
                if (Failures.Count > 3) text += $"; and {Failures.Count - 3} more";
            }

            text += ".";
            foreach (var note in Notes) text += " " + char.ToUpperInvariant(note[0]) + note[1..] + ".";
            return text;
        }
    }

    /// <summary>
    /// Converts audio files between MP3, M4A, Opus, FLAC and WAV.
    ///
    /// The source is decoded by the player's own decoders and never written
    /// to. Output goes beside it under a temporary name with the target's
    /// extension (Media Foundation picks the container from the extension)
    /// and is moved to its real name only when it is complete, so a cancelled
    /// or failed file leaves nothing behind. A name already in use is replaced
    /// only when the person said so.
    /// </summary>
    internal static class AudioConverter
    {
        /// <summary>
        /// Files converted at once: one a core (Conner, 6.1.0: "make sure it
        /// uses all cores"). Each runs below normal priority, so the PC stays
        /// usable while every core is busy.
        /// </summary>
        public static int Parallel => Math.Max(1, Environment.ProcessorCount);

        private const int BlockFrames = 4096;

        /// <summary>The temporary names start with this, so a folder walk never converts one.</summary>
        internal const string TempMarker = ".converting-";

        /// <summary>
        /// The audio files in a selection: files as they are, folders opened (and
        /// their subfolders when asked). One scan per folder; links to folders are
        /// not followed. Runs on a worker.
        /// </summary>
        public static List<string> Gather(IEnumerable<string> paths, bool subfolders, string? audioExtensions,
            CancellationToken token, GoogleDrive? drive = null)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string file)
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith(TempMarker, StringComparison.Ordinal)) return;
                if (!AudioFiles.IsAudio(file, audioExtensions)) return;
                if (seen.Add(file)) found.Add(file);
            }

            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();

                if (OnDrive(drive, path))
                {
                    GatherOnDrive(path, subfolders, drive!, Add, token);
                    continue;
                }

                // Exact throughout: in a folder named "Backup." the ordinary path
                // is "Backup", and the conversion read (and wrote beside) the
                // sibling folder's files. The names kept are the ordinary form.
                if (File.Exists(NameRules.ExactPath(path))) { Add(path); continue; }
                if (!Directory.Exists(NameRules.ExactPath(path))) continue;

                var stack = new Stack<string>();
                stack.Push(path);
                while (stack.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var folder = stack.Pop();
                    var files = new List<string>();
                    try
                    {
                        foreach (var info in new DirectoryInfo(NameRules.ExactPath(folder)).EnumerateFileSystemInfos())
                        {
                            if (info is DirectoryInfo dir)
                            {
                                // Links are skipped, not every reparse point: a
                                // OneDrive folder is one too, and its subfolders
                                // were left out of the conversion without a word.
                                if (subfolders && !NameRules.IsLink(dir))
                                    stack.Push(NameRules.PlainPath(dir.FullName));
                            }
                            else files.Add(NameRules.PlainPath(info.FullName));
                        }
                    }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }

                    files.Sort(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in files) Add(file);
                }
            }

            return found;
        }

        /// <summary>
        /// The same walk on Google Drive, from the mount's listings and never
        /// the letter: one Drive listing for each folder nobody has opened yet,
        /// nothing for the rest.
        /// </summary>
        private static void GatherOnDrive(string path, bool subfolders, GoogleDrive drive, Action<string> add,
            CancellationToken token)
        {
            var known = drive.Known(path);
            if (known == null)
            {
                // Its folder was never listed (a search result): list it, ask again.
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent)) drive.Populate(parent, interactive: false);
                known = drive.Known(path);
                if (known == null) return;
            }
            if (!known.Value.Folder) { add(path); return; }

            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var folder = stack.Pop();
                drive.Populate(folder, interactive: false);
                if (!drive.TryListing(folder, out var entries)) continue;

                var files = new List<string>();
                foreach (var entry in entries)
                {
                    if (entry.IsFolder)
                    {
                        if (subfolders) stack.Push(Path.Combine(folder, entry.Name));
                    }
                    else files.Add(Path.Combine(folder, entry.Name));
                }

                files.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files) add(file);
            }
        }

        /// <summary>Whether this path is on the mounted Google Drive.</summary>
        private static bool OnDrive(GoogleDrive? drive, string path) => drive != null && drive.Owns(path);

        /// <summary>A free name for a target: numbered past the claimed names and what is already there.</summary>
        private static string Unique(string target, ISet<string>? claimed, GoogleDrive? drive)
        {
            if (!OnDrive(drive, target)) return FileOperations.UniqueName(target, claimed, folder: false);

            bool Used(string candidate) =>
                (claimed != null && claimed.Contains(candidate)) || drive!.Known(candidate) != null;
            if (!Used(target)) return target;

            var folder = Path.GetDirectoryName(target) ?? "";
            return Path.Combine(folder,
                NameRules.UniqueAmong(Path.GetFileName(target), name => Used(Path.Combine(folder, name)), false));
        }

        /// <summary>
        /// The sources still worth converting: a file that already has the
        /// format's extension is left out and counted (Conner, 6.1.0: a folder
        /// of FLACs and Opus files made into Opus converts only the FLACs).
        /// By name only, so nothing is read.
        /// </summary>
        public static List<string> NotAlready(IReadOnlyList<string> sources, AudioFormatChoice choice, out int already)
        {
            var left = new List<string>(sources.Count);
            already = 0;
            foreach (var source in sources)
            {
                // FLAC to FLAC and WAV to WAV are still made (Conner): the
                // same audio packed again, or at another bit depth.
                if (choice.Kind is not (AudioFormatKind.Flac or AudioFormatKind.Wav) &&
                    string.Equals(Path.GetExtension(source), choice.Extension, StringComparison.OrdinalIgnoreCase)) already++;
                else left.Add(source);
            }
            return left;
        }

        /// <summary>The file a source becomes: beside it, same name, the format's extension.</summary>
        public static string TargetFor(string source, AudioFormatChoice choice) =>
            Path.Combine(Path.GetDirectoryName(source) ?? "", Path.GetFileNameWithoutExtension(source) + choice.Extension);

        /// <summary>
        /// Works out every target and which ones are already taken. A source that
        /// would become itself (an MP3 converted to MP3) always gets a new name,
        /// and so does a target that is another source in the batch or a second
        /// source landing on the first one's target: none of those is something
        /// to ask about, because replacing could only ever destroy a file being
        /// read. Runs on a worker.
        /// </summary>
        public static ConvertPlan Plan(IReadOnlyList<string> sources, AudioFormatChoice choice,
            GoogleDrive? drive = null)
        {
            var plan = new ConvertPlan();
            foreach (var source in sources) plan.Claimed.Add(source);

            foreach (var source in sources)
            {
                long bytes = 0;
                if (OnDrive(drive, source)) bytes = drive!.Known(source)?.Size ?? 0;
                else
                {
                    try { bytes = new FileInfo(NameRules.ExactPath(source)).Length; } catch { }
                }

                var target = TargetFor(source, choice);

                // Itself, another source in this batch, or another job's target.
                if (plan.Claimed.Contains(target))
                {
                    target = Unique(target, plan.Claimed, drive);
                    plan.Claimed.Add(target);
                    plan.Jobs.Add(new ConvertJob(source, target, bytes, false));
                    continue;
                }

                var job = new ConvertJob(source, target, bytes, false);
                plan.Claimed.Add(target);
                plan.Jobs.Add(job);
                if (Taken(target, drive)) plan.Clashing.Add(job);
            }

            return plan;
        }

        /// <summary>
        /// The jobs to run once the person has answered for the clashing names:
        /// skip them, replace them, or keep both with a numbered name. Runs on a
        /// worker (numbering checks the disk).
        /// </summary>
        public static List<ConvertJob> Resolve(ConvertPlan plan, FileOperations.ConflictChoice choice, out int skipped,
            GoogleDrive? drive = null)
        {
            skipped = 0;
            var clashing = new HashSet<ConvertJob>(plan.Clashing);
            var jobs = new List<ConvertJob>();

            foreach (var job in plan.Jobs)
            {
                if (!clashing.Contains(job)) { jobs.Add(job); continue; }

                switch (choice)
                {
                    case FileOperations.ConflictChoice.Skip:
                    case FileOperations.ConflictChoice.FillGaps:
                        skipped++;
                        break;

                    case FileOperations.ConflictChoice.Overwrite:
                        // Never a folder: a folder in the way is numbered past.
                        if (OnDrive(drive, job.Target)
                                ? drive!.Known(job.Target)?.Folder == true
                                : Directory.Exists(NameRules.ExactPath(job.Target))) goto default;
                        jobs.Add(job with { Replace = true });
                        break;

                    case FileOperations.ConflictChoice.Cancel:
                        return new List<ConvertJob>();

                    default:
                        var fresh = Unique(job.Target, plan.Claimed, drive);
                        plan.Claimed.Add(fresh);
                        jobs.Add(job with { Target = fresh });
                        break;
                }
            }

            return jobs;
        }

        /// <summary>
        /// What is left of a batch an update or a restart stopped (6.1.0). A
        /// file is only ever put in place whole, so a target that is there (and
        /// was not being replaced) is a file already converted; a source that
        /// has gone was converted and deleted. Everything else is still to do,
        /// a file being replaced included: converting it twice costs time and
        /// nothing else. Sizes are read again. Throws when a Google Drive folder
        /// will not list, rather than take its files for gone. On a worker.
        /// </summary>
        public static (List<ConvertJob> Left, List<ConvertJob> Converted) Remaining(IReadOnlyList<ConvertJob> jobs,
            GoogleDrive? drive)
        {
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var job in jobs)
            {
                foreach (var path in new[] { job.Source, job.Target })
                {
                    if (!OnDrive(drive, path)) continue;
                    var folder = Path.GetDirectoryName(path) ?? "";
                    if (!listed.Add(folder)) continue;
                    drive!.Populate(folder, interactive: false);
                    if (!drive.TryListing(folder, out _))
                        throw new IOException($"Google Drive would not list {Path.GetFileName(folder)}");
                }
            }

            var left = new List<ConvertJob>();
            var converted = new List<ConvertJob>();
            foreach (var job in jobs)
            {
                long bytes;
                if (OnDrive(drive, job.Source))
                {
                    if (drive!.Known(job.Source) is not { Folder: false } known) continue;
                    bytes = known.Size;
                }
                else
                {
                    var info = new FileInfo(NameRules.ExactPath(job.Source));
                    if (!info.Exists) continue;
                    bytes = info.Length;
                }

                if (!job.Replace && Taken(job.Target, drive)) converted.Add(job with { Bytes = bytes });
                else left.Add(job with { Bytes = bytes });
            }
            return (left, converted);
        }

        /// <summary>
        /// Converts the jobs, a few at a time, reporting progress like a copy
        /// (bytes are the sources' sizes, counted as each one is decoded).
        /// </summary>
        public static async Task<ConvertResult> RunAsync(IReadOnlyList<ConvertJob> jobs, AudioFormatChoice choice,
            IProgress<TransferProgress>? progress, CancellationToken token, int parallel = 0,
            GoogleDrive? drive = null, bool deleteOriginals = false)
        {
            var result = new ConvertResult();
            if (parallel <= 0) parallel = Parallel;

            // Never the Drive letter: its files are converted in the PC's own
            // temporary folder, and looking would cost a listing per folder.
            SweepStaleTemps(jobs.Where(j => !OnDrive(drive, j.Target))
                .Select(j => Path.GetDirectoryName(j.Target)).Distinct());
            if (jobs.Any(j => OnDrive(drive, j.Target))) SweepStaleWork();

            long total = jobs.Sum(j => Math.Max(1, j.Bytes));
            var fractions = new double[jobs.Count];
            var stages = new string?[jobs.Count];       // null: not started, or over
            int done = 0;
            string current = jobs.Count > 0 ? Path.GetFileName(jobs[0].Source) : "";
            var clock = Stopwatch.StartNew();
            long lastReport = -1000;
            var reportGate = new object();

            void Report(bool force)
            {
                if (progress == null) return;
                lock (reportGate)
                {
                    long now = clock.ElapsedMilliseconds;
                    if (!force && now - lastReport < 250) return;
                    lastReport = now;

                    double bytes = 0;
                    for (int i = 0; i < jobs.Count; i++) bytes += Math.Max(1, jobs[i].Bytes) * Math.Clamp(fractions[i], 0, 1);
                    double seconds = Math.Max(0.001, clock.Elapsed.TotalSeconds);
                    // Every file being worked on and what is happening to it,
                    // then what has failed so far: the window's full status.
                    var detail = new StringBuilder();
                    int working = 0;
                    for (int i = 0; i < jobs.Count; i++)
                    {
                        if (stages[i] is not { } stage) continue;
                        // Started, but only standing in line: that is waiting.
                        if (!stage.StartsWith("Waiting for", StringComparison.Ordinal)) working++;
                        detail.Append(Path.GetFileName(jobs[i].Source)).Append(": ").Append(stage);
                        if (fractions[i] > 0 && fractions[i] < 1)
                            detail.Append(", ").Append((int)(fractions[i] * 100)).Append(" percent");
                        detail.Append("\r\n");
                    }
                    int converted, failed;
                    lock (result)
                    {
                        converted = result.Converted;
                        failed = result.Failures.Count;
                        foreach (var failure in result.Failures)
                            detail.Append("Failed: ").Append(failure.Name).Append(", ").Append(failure.Reason).Append("\r\n");
                    }
                    int waiting = Math.Max(0, jobs.Count - Volatile.Read(ref done) - working);
                    string tally = $"{converted} converted, {working} in progress, {waiting} waiting" +
                                   (failed > 0 ? $", {failed} failed" : "");

                    progress.Report(new TransferProgress((long)bytes, total, Volatile.Read(ref done), jobs.Count,
                        current, bytes / seconds, clock.Elapsed, ItemsKnown: true,
                        Tally: tally, Detail: detail.ToString().TrimEnd()));
                }
            }

            Report(true);

            using var gate = new SemaphoreSlim(parallel);
            var tasks = jobs.Select(async (job, index) =>
            {
                try { await gate.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                try
                {
                    if (token.IsCancellationRequested) return;
                    lock (reportGate) { current = Path.GetFileName(job.Source); stages[index] = "Starting"; }
                    Report(true);

                    // Below normal priority, on its own thread: every core is
                    // in use and the PC must still answer.
                    var written = await ArchiveWorkers.RunLong(() => ConvertFile(job, choice, f =>
                    {
                        fractions[index] = f;
                        Report(false);
                    }, token, result.Notes, drive, said =>
                    {
                        lock (reportGate) stages[index] = said;
                        Report(true);
                    }), token).ConfigureAwait(false);

                    // Only now, with the new file whole and in place, and never
                    // once cancelled: the original is the only copy until then.
                    string? kept = null;
                    bool removed = false;
                    if (deleteOriginals && !token.IsCancellationRequested)
                    {
                        lock (reportGate) stages[index] = "Deleting the original";
                        kept = await Task.Run(() => RemoveOriginal(job, written, drive)).ConfigureAwait(false);
                        removed = kept == null;
                    }

                    fractions[index] = 1;
                    lock (result)
                    {
                        result.Converted++;
                        result.Written.Add(written);
                        if (removed) result.OriginalsDeleted++;
                        else if (kept != null)
                            result.OriginalsKept.Add(new ConvertFailure(Path.GetFileName(job.Source), kept));
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    fractions[index] = 1;
                    lock (result) result.Failures.Add(new ConvertFailure(Path.GetFileName(job.Source), Reason(ex)));
                }
                finally
                {
                    lock (reportGate) stages[index] = null;
                    Interlocked.Increment(ref done);
                    gate.Release();
                    Report(true);
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);
            result.Cancelled = token.IsCancellationRequested;
            return result;
        }

        /// <summary>An exception as the end of a sentence about a file.</summary>
        internal static string Reason(Exception ex) => ex switch
        {
            AudioEncodeException => ex.Message,
            UnauthorizedAccessException => "access was refused",
            IOException io when io.HResult == unchecked((int)0x80070070) => "the disk is full",
            ConvertRetryException => ex.Message,
            DriveQuotaException => "Google Drive is full",
            System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException => GoogleNet.Reason(ex).TrimEnd('.'),
            _ => ex.Message.TrimEnd('.'),
        };

        /// <summary>
        /// One file, start to finish, on the calling thread: every COM object the
        /// encoder makes is made and used here. Returns the path written. On a
        /// failure or a cancel the temporary file is deleted and the source is
        /// as it was.
        /// </summary>
        public static string ConvertFile(ConvertJob job, AudioFormatChoice choice, Action<double>? fraction,
            CancellationToken token, ICollection<string> notes, GoogleDrive? drive = null, Action<string>? stage = null)
        {
            token.ThrowIfCancellationRequested();
            if (OnDrive(drive, job.Target)) return ConvertOnDrive(job, choice, fraction, token, notes, drive!, stage);

            var temp = TempPathBeside(job.Target);
            try
            {
                stage?.Invoke("Converting");
                var tags = AudioTags.ReadTagSet(job.Source);

                var decoder = TrackDecoder.Open(job.Source, null, 0, 0, out string why)
                              ?? throw new AudioEncodeException("it could not be read: " + why);
                using (decoder)
                {
                    int bits = NativeBitsOf(decoder);
                    Encode(decoder, choice, temp, bits, tags, fraction, token, notes);
                }

                token.ThrowIfCancellationRequested();
                return Place(temp, job.Target, job.Replace);
            }
            catch
            {
                AudioEncoders.TryDelete(temp);
                throw;
            }
        }

        /// <summary>
        /// Removes an original whose converted file is in place: to the Recycle
        /// Bin on this PC, to the trash on Google Drive, so either can be put
        /// back. Never deleted for good, and kept whenever the new file cannot
        /// be seen whole. Null when it went, otherwise why it was kept.
        /// </summary>
        internal static string? RemoveOriginal(ConvertJob job, string written, GoogleDrive? drive)
        {
            try
            {
                if (string.Equals(job.Source, written, StringComparison.OrdinalIgnoreCase))
                    return "the converted file has its name";

                if (OnDrive(drive, job.Source))
                {
                    // ConvertOnDrive has already had Google confirm the new file.
                    if (drive!.Known(written) is not { Folder: false, Size: > 0 })
                        return "the converted file could not be seen on Google Drive";
                    // Quietly: one sentence a file would talk over the whole
                    // batch, and the summary says how many went.
                    string? said = null;
                    bool went;
                    using (GoogleDrive.SayingTo((_, message) => said = message))
                        went = drive.TrashPath(job.Source, CancellationToken.None).GetAwaiter().GetResult();
                    if (went) return null;
                    int colon = said?.IndexOf(": ", StringComparison.Ordinal) ?? -1;
                    return colon >= 0 ? said![(colon + 2)..].TrimEnd('.') : "Google Drive would not move it to the trash";
                }

                var made = new FileInfo(NameRules.ExactPath(written));
                if (!made.Exists || made.Length == 0) return "the converted file could not be checked";
                return ShellDelete.RecycleWithoutUi(job.Source);
            }
            catch (Exception ex) { return Reason(ex); }
        }

        // ------------------------------------------------------ Google Drive

        /// <summary>Where Google Drive files are converted: this PC's temporary folder, one folder a file.</summary>
        internal static string WorkRoot => Path.Combine(Path.GetTempPath(), "ExplorerNative-Convert");

        /// <summary>
        /// The most disk the Drive conversions running at once may take between
        /// them (Conner: "no more than a gigabyte at a time"): originals being
        /// downloaded plus the files being made. A file that needs more by
        /// itself runs alone, and its original is read from Drive as it is
        /// decoded rather than downloaded.
        /// </summary>
        internal static readonly long WorkBudget = BudgetFromEnvironment() ?? 1024L * 1024 * 1024;

        /// <summary>EXPLORERNATIVE_CONVERTBUDGET_MB, for testing the too-big-to-download path with small files.</summary>
        private static long? BudgetFromEnvironment() =>
            long.TryParse(Environment.GetEnvironmentVariable("EXPLORERNATIVE_CONVERTBUDGET_MB"), out var mb) && mb > 0
                ? mb * 1024 * 1024
                : null;

        /// <summary>Room always left free on the temporary folder's disk.</summary>
        internal const long WorkHeadroom = 512L * 1024 * 1024;

        /// <summary>How often one Drive file is tried when Drive, not the file, was the trouble.</summary>
        internal const int DriveTries = 3;

        private static readonly object _workGate = new();
        private static long _workHeld;

        /// <summary>The wait before a second try; the third waits twice as long.</summary>
        internal static TimeSpan RetryPause = TimeSpan.FromSeconds(5);

        internal static long WorkHeld { get { lock (_workGate) return _workHeld; } }

        /// <summary>Waits for room in the budget. Something alone always goes, however big.</summary>
        internal static void AcquireWork(long bytes, CancellationToken token)
        {
            lock (_workGate)
            {
                while (_workHeld > 0 && _workHeld + bytes > WorkBudget)
                {
                    Monitor.Wait(_workGate, 500);
                    token.ThrowIfCancellationRequested();
                }
                _workHeld += bytes;
            }
        }

        internal static void ReleaseWork(long bytes)
        {
            lock (_workGate)
            {
                _workHeld = Math.Max(0, _workHeld - bytes);
                Monitor.PulseAll(_workGate);
            }
        }

        private static long WorkFree()
        {
            try
            {
                return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(WorkRoot))!).AvailableFreeSpace;
            }
            catch { return long.MaxValue; }
        }

        private static readonly HashSet<string> LosslessExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".flac", ".wav", ".aif", ".aiff", ".aifc", ".ape", ".wv", ".tta", ".caf", ".au", ".snd", ".w64", ".cda",
        };

        /// <summary>
        /// About how big the converted file will be, from the original's size
        /// and kind alone (nothing has been read yet). Only for sharing the
        /// budget out, so roughly right is right.
        /// </summary>
        internal static long EstimateOutput(long sourceBytes, string? sourceExtension, AudioFormatChoice choice)
        {
            bool lossless = LosslessExtensions.Contains(sourceExtension ?? "");
            double factor = choice.Kind switch
            {
                AudioFormatKind.Flac => lossless ? 1.0 : 8.0,
                AudioFormatKind.Wav => lossless ? 2.0 : 12.0,
                // A lossless original runs near 700 kilobits; a lossy one is taken as 96.
                _ => lossless ? Math.Min(1.0, choice.Kbps / 700.0) : Math.Max(0.25, choice.Kbps / 96.0),
            };
            return (long)Math.Min(long.MaxValue / 2, Math.Max(1, sourceBytes) * factor);
        }

        /// <summary>
        /// One file on Google Drive. Nothing can be encoded on the letter (a
        /// file being written there is held in memory, and an encoder goes back
        /// to finish its header), so the original is downloaded straight from
        /// Google into this PC's temporary folder and checked, converted there,
        /// and the new file uploaded beside the original and confirmed with
        /// Google by size and checksum; then the temporary folder goes. The
        /// original on Drive is only ever read. Trouble that is Drive's (the
        /// connection, not the file) is tried again. Returns the path on the
        /// letter the new file was given.
        /// </summary>
        private static string ConvertOnDrive(ConvertJob job, AudioFormatChoice choice, Action<double>? fraction,
            CancellationToken token, ICollection<string> notes, GoogleDrive drive, Action<string>? stage = null)
        {
            long estimate = EstimateOutput(job.Bytes, Path.GetExtension(job.Source), choice);
            bool fits = job.Bytes + estimate <= WorkBudget;
            long held = job.Bytes + estimate;

            // On disk when there is room in the budget right now. Otherwise
            // straight through (nothing on disk) when one of those turns is
            // free, rather than a core standing idle; a file too big for the
            // budget only ever goes straight through.
            bool download = fits && TryAcquireWork(held);
            bool streaming = false;
            try
            {
                if (!download)
                {
                    if (_streamers.Wait(0, token)) streaming = true;
                    else if (fits)
                    {
                        stage?.Invoke("Waiting for room on this PC's disk");
                        AcquireWork(held, token);
                        download = true;
                    }
                    else
                    {
                        stage?.Invoke("Waiting for its turn");
                        _streamers.Wait(token);
                        streaming = true;
                    }
                }
                GoogleDrive.Trace($"convert {Path.GetFileName(job.Source)}: {(download ? "downloaded first" : "straight through, nothing on disk")}, {job.Bytes:N0} bytes, about {estimate:N0} to make");

                string last = "";
                for (int attempt = 0; attempt < DriveTries; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    if (attempt > 0)
                    {
                        fraction?.Invoke(0);
                        stage?.Invoke($"Waiting to try again, try {attempt + 1} of {DriveTries}: {last}");
                        if (token.WaitHandle.WaitOne(RetryPause * attempt)) token.ThrowIfCancellationRequested();
                    }

                    try { return ConvertOnDriveOnce(job, choice, fraction, token, notes, drive, download, estimate, stage); }
                    catch (ConvertRetryException ex) { last = ex.Message; }
                }
                throw new AudioEncodeException(last);
            }
            finally
            {
                if (download) ReleaseWork(held);
                if (streaming) _streamers.Release();
            }
        }

        /// <summary>
        /// Files converted straight through at once, whatever the budget:
        /// each reads its original through the letter (the mount keeps three
        /// files' caches) and holds a few upload pieces in memory.
        /// </summary>
        private static readonly SemaphoreSlim _streamers = new(2);

        /// <summary>Takes room in the budget if there is some now.</summary>
        internal static bool TryAcquireWork(long bytes)
        {
            lock (_workGate)
            {
                if (_workHeld + bytes > WorkBudget) return false;
                _workHeld += bytes;
                return true;
            }
        }

        /// <summary>
        /// One Drive file with nothing on this PC's disk (6.1.0, Conner: "no
        /// more than 1 gb should be in disk at a time. if the file is 100 gigs
        /// alone it should split the file"): the original is read through the
        /// letter as it is decoded, and what the encoder writes goes to Google
        /// in pieces as it is made (<see cref="GoogleDrive.StreamInto"/>).
        /// </summary>
        private static string ConvertStreaming(ConvertJob job, AudioFormatChoice choice, Action<double>? fraction,
            CancellationToken token, ICollection<string> notes, GoogleDrive drive, Action<string>? stage)
        {
            var folder = Path.GetDirectoryName(job.Target) ?? "";
            stage?.Invoke("Converting and sending to Google Drive");
            try
            {
                var tags = AudioTags.ReadTagSet(job.Source);
                var placed = drive.StreamInto(folder, Path.GetFileName(job.Target), job.Replace, output =>
                {
                    var decoder = TrackDecoder.Open(job.Source, null, 0, 0, out string why)
                                  ?? throw new ConvertRetryException("it could not be read from Google Drive: " + why);
                    using (decoder)
                        Encode(decoder, choice, job.Target, NativeBitsOf(decoder), tags,
                            f => fraction?.Invoke(0.98 * f), token, notes, output);
                }, stage, null, token);
                return Path.Combine(folder, placed);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (AudioEncodeException) { throw; }
            catch (ConvertRetryException) { throw; }
            catch (DriveQuotaException) { throw new AudioEncodeException("the converted file could not be uploaded: Google Drive is full"); }
            catch (NotSupportedException ex) { throw new AudioEncodeException(ex.Message); }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("nothing can be put", StringComparison.Ordinal))
            {
                throw new AudioEncodeException(ex.Message);
            }
            catch (Exception ex)
            {
                throw new ConvertRetryException("Google Drive stopped answering while it was converted: " + Reason(ex));
            }
        }

        private static string ConvertOnDriveOnce(ConvertJob job, AudioFormatChoice choice, Action<double>? fraction,
            CancellationToken token, ICollection<string> notes, GoogleDrive drive, bool download, long estimate,
            Action<string>? stage = null)
        {
            var folder = Path.GetDirectoryName(job.Target) ?? "";
            var name = Path.GetFileName(job.Source);

            if (!drive.LetterShown) throw new ConvertRetryException("Google Drive is offline");

            // A disk too full for it is no reason to fail: straight through needs none.
            if (!download || WorkFree() < job.Bytes + estimate + WorkHeadroom)
                return ConvertStreaming(job, choice, fraction, token, notes, drive, stage);

            var work = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N")[..12]);
            try
            {
                Directory.CreateDirectory(Path.Combine(work, "in"));
                Directory.CreateDirectory(Path.Combine(work, "out"));

                // Download, convert, upload: the bar's three parts.
                const double Fetched = 0.3, Encoded = 0.8;
                string readFrom = job.Source;
                double encodeFrom = 0;

                if (download)
                {
                    stage?.Invoke("Downloading from Google Drive");
                    var local = Path.Combine(work, "in", name);
                    long size = Math.Max(1, job.Bytes);
                    string? whyNot;
                    try
                    {
                        whyNot = drive.DownloadOutAsync(job.Source, local,
                                moved => fraction?.Invoke(Fetched * Math.Min(1.0, moved / (double)size)), token)
                            .GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (IOException ex) when (ex.HResult == unchecked((int)0x80070070)) { throw; }
                    catch (Exception ex) { whyNot = GoogleNet.Reason(ex); }

                    token.ThrowIfCancellationRequested();
                    if (whyNot != null)
                    {
                        var said = "it could not be downloaded from Google Drive: " + whyNot.TrimEnd('.');
                        // Drive's own answer about the file is final; anything else may pass.
                        if (whyNot.StartsWith("it is ", StringComparison.Ordinal)) throw new AudioEncodeException(said);
                        throw new ConvertRetryException(said);
                    }
                    readFrom = local;
                    encodeFrom = Fetched;
                }

                var output = Path.Combine(work, "out", Path.GetFileName(job.Target));
                try
                {
                    stage?.Invoke("Converting");
                    var tags = AudioTags.ReadTagSet(readFrom);

                    var decoder = TrackDecoder.Open(readFrom, null, 0, 0, out string why);
                    if (decoder == null)
                    {
                        // Read through the letter, "could not open" may be the connection.
                        if (!download) throw new ConvertRetryException("it could not be read from Google Drive: " + why);
                        throw new AudioEncodeException("it could not be read: " + why);
                    }
                    using (decoder)
                    {
                        int bits = NativeBitsOf(decoder);
                        Encode(decoder, choice, output, bits, tags,
                            f => fraction?.Invoke(encodeFrom + (Encoded - encodeFrom) * f), token, notes);
                    }
                }
                catch (IOException ex) when (!download && ex.HResult != unchecked((int)0x80070070))
                {
                    throw new ConvertRetryException("Google Drive stopped answering while it was read: " + Reason(ex));
                }

                token.ThrowIfCancellationRequested();
                long made = new FileInfo(output).Length;
                if (made <= 0) throw new AudioEncodeException("nothing was written for it");

                stage?.Invoke("Uploading to Google Drive");
                var placed = UploadConverted(job, drive, output, folder, made,
                    p => fraction?.Invoke(Encoded + (1 - Encoded) * 0.95 * Math.Clamp(p, 0, 1)), token);
                var path = Path.Combine(folder, placed);

                // Google's own word that the file is there and whole, before
                // anybody is told it was converted or an original is removed.
                stage?.Invoke("Checking it with Google Drive");
                string? unconfirmed = null;
                for (int attempt = 0; attempt < DriveTries; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    if (attempt > 0 && token.WaitHandle.WaitOne(RetryPause * attempt)) token.ThrowIfCancellationRequested();
                    try { unconfirmed = drive.WhyNotUploaded(path, output, token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { unconfirmed = GoogleNet.Reason(ex).TrimEnd('.'); continue; }
                    break;
                }
                if (unconfirmed != null)
                    throw new AudioEncodeException("the converted file could not be confirmed on Google Drive: " + unconfirmed);

                return path;
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// Uploads the converted file beside the original, trying again when
        /// the connection was the trouble. Before a second try it looks for the
        /// first: Google can finish an upload whose answer was lost, and sending
        /// it again would leave two.
        /// </summary>
        private static string UploadConverted(ConvertJob job, GoogleDrive drive, string output, string folder, long made,
            Action<double> sent, CancellationToken token)
        {
            string last = "Google Drive did not take it";
            for (int attempt = 0; attempt < DriveTries; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (attempt > 0)
                {
                    if (token.WaitHandle.WaitOne(RetryPause * attempt)) token.ThrowIfCancellationRequested();

                    if (!job.Replace)
                    {
                        try
                        {
                            var at = Path.Combine(folder, Path.GetFileName(output));
                            drive.Forget(folder);
                            drive.Populate(folder, interactive: false);
                            if (drive.Known(at) is { Folder: false } there && there.Size == made &&
                                drive.WhyNotUploaded(at, output, token).GetAwaiter().GetResult() == null)
                                return Path.GetFileName(at);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch { }
                    }
                }

                try
                {
                    var placed = drive.UploadInto(output, folder, p => sent(p.Percent / 100.0),
                            announce: false, replace: job.Replace, token: token)
                        .GetAwaiter().GetResult();
                    if (placed != null) return placed;
                    last = drive.LetterShown ? "Google Drive did not take it" : "Google Drive is offline";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (DriveQuotaException)
                {
                    throw new AudioEncodeException("the converted file could not be uploaded: Google Drive is full");
                }
                catch (Exception ex) { last = DriveUpload.Reason(ex).TrimEnd('.'); }
            }

            throw new AudioEncodeException("the converted file could not be uploaded to Google Drive: " + last);
        }

        /// <summary>Deletes work folders a crash or a quit left behind, once they are two days old.</summary>
        internal static void SweepStaleWork(TimeSpan? olderThan = null)
        {
            var limit = DateTime.UtcNow - (olderThan ?? TimeSpan.FromDays(2));
            try
            {
                if (!Directory.Exists(WorkRoot)) return;
                foreach (var folder in Directory.EnumerateDirectories(WorkRoot))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(folder) < limit) Directory.Delete(folder, recursive: true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// The bits per sample the source stores, whichever decoder opened it;
        /// zero when it does not say. Asked of Media Foundation's decoder alone,
        /// a 24-bit file Windows will not open itself (TTA, CAF, AU, 64-bit
        /// float WAV, FLAC in Ogg) was written as 16-bit FLAC or WAV.
        /// </summary>
        internal static int NativeBitsOf(ITrackDecoder decoder) => decoder switch
        {
            AudioDecoder media => media.NativeBits,
            WavDecoder wav => wav.NativeBits,
            TtaDecoder tta => tta.NativeBits,
            OwningDecoder owning => NativeBitsOf(owning.Inner),
            _ => 0,
        };

        /// <summary>
        /// Whether the path is on a CD or DVD drive, where nothing can be written
        /// beside it, so a conversion or an edit's save could only fail. Asks the
        /// drive's type alone, which does not touch the disc. Never true for a share.
        /// </summary>
        internal static bool IsOnDisc(string path, Func<string, DriveType>? typeOf = null)
        {
            try
            {
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
                return (typeOf ?? (r => new DriveInfo(r).DriveType))(root) == DriveType.CDRom;
            }
            catch { return false; }
        }

        /// <summary>A hidden-looking temporary name in the target's folder, keeping the target's extension.</summary>
        /// <summary>
        /// Deletes temp files a crash or a quit left in these folders. Only ones
        /// untouched for a while, so a conversion running in another window keeps its own.
        /// </summary>
        internal static void SweepStaleTemps(IEnumerable<string?> folders, TimeSpan? olderThan = null)
        {
            var limit = DateTime.UtcNow - (olderThan ?? TimeSpan.FromHours(6));
            foreach (var folder in folders)
            {
                if (string.IsNullOrEmpty(folder)) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder, TempMarker + "*"))
                    {
                        try
                        {
                            if (File.GetLastWriteTimeUtc(file) < limit) File.Delete(file);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        internal static string TempPathBeside(string target)
        {
            var folder = Path.GetDirectoryName(target) ?? "";
            var token = Guid.NewGuid().ToString("N")[..8];
            return Path.Combine(folder, $"{TempMarker}{token}{Path.GetExtension(target)}");
        }

        /// <summary>
        /// Moves the finished temporary file to its name. Replaces only when the
        /// person chose to; if the name was taken while converting, the new file
        /// is numbered instead of overwriting anything.
        /// </summary>
        internal static string Place(string temp, string target, bool replace)
        {
            if (replace)
            {
                // Replace means replace, a read-only file included, as a paste's does.
                FileOperations.MoveReplacing(NameRules.ExactPath(temp), NameRules.ExactPath(target));
                return target;
            }

            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (Taken(target))
                    target = FileOperations.UniqueName(target, null, folder: false);
                try
                {
                    File.Move(NameRules.ExactPath(temp), NameRules.ExactPath(target), overwrite: false);
                    return target;
                }
                catch (IOException) when (Taken(target)) { }
            }

            throw new IOException("no free name could be found beside it");
        }

        /// <summary>Whether a file or folder is at this exact path (a dot or space at the end of a folder's name included).</summary>
        private static bool Taken(string path, GoogleDrive? drive = null)
        {
            if (OnDrive(drive, path)) return drive!.Known(path) != null;
            var exact = NameRules.ExactPath(path);
            return File.Exists(exact) || Directory.Exists(exact);
        }

        /// <summary>
        /// Decodes everything <paramref name="decoder"/> gives and encodes it into
        /// <paramref name="path"/>, converting rate and channels to what the
        /// format takes. Shared by the converter and the editor's save. Returns
        /// the frames written.
        /// </summary>
        public static long Encode(ITrackDecoder decoder, AudioFormatChoice choice, string path, int sourceBits,
            AudioTagSet? tags, Action<double>? fraction, CancellationToken token, ICollection<string> notes,
            Stream? output = null)
        {
            int sourceRate = decoder.SampleRate, sourceChannels = Math.Max(1, decoder.Channels);
            var (rate, channels) = AudioEncoders.ShapeFor(choice.Kind, sourceRate, sourceChannels);

            // Windows' own resampler and channel mixer first, inside the decode.
            if (rate != sourceRate || channels != sourceChannels) decoder.Reopen(rate, channels);
            int decodedRate = decoder.SampleRate, decodedChannels = Math.Max(1, decoder.Channels);

            // And ours for anything it would not do.
            IResampler? resampler = decodedRate != rate
                ? ResamplerFactory.CreateResampler(channels, decodedRate, rate, 8, null)
                : null;

            double duration = decoder.Duration;
            var block = new float[BlockFrames * Math.Max(decodedChannels, channels)];
            var mapped = decodedChannels != channels ? new float[BlockFrames * channels] : null;
            var resampled = resampler != null
                ? new float[((int)((long)BlockFrames * rate / decodedRate) + 512) * channels]
                : null;

            long decodedFrames = 0, writtenFrames = 0;
            // How long it will be, for a WAV header right from the start.
            long expectedFrames = resampler == null && duration > 0 ? (long)Math.Round(duration * rate) : 0;
            var encoder = AudioEncoders.Create(choice, path, rate, channels, sourceBits, tags, notes, output, expectedFrames);
            try
            {
                int empties = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    int got = decoder.Read(block, BlockFrames);
                    if (got <= 0)
                    {
                        // Bounded: three empty reads in a row is the end.
                        if (++empties >= 3) break;
                        continue;
                    }
                    empties = 0;
                    decodedFrames += got;

                    float[] samples = block;
                    if (mapped != null)
                    {
                        MapChannels(block, decodedChannels, mapped, channels, got);
                        samples = mapped;
                    }

                    if (resampler != null)
                        writtenFrames += Resample(resampler, samples, got, channels, resampled!, encoder, long.MaxValue);
                    else
                    {
                        encoder.Write(samples, got);
                        writtenFrames += got;
                    }

                    if (duration > 0)
                        fraction?.Invoke(Math.Min(1, decodedFrames / (double)decodedRate / duration));
                }

                // The resampler holds back a few milliseconds; fed silence until
                // the output is as long as the input was.
                if (resampler != null)
                {
                    long expected = (long)Math.Round(decodedFrames * (double)rate / decodedRate);
                    Array.Clear(block);
                    for (int round = 0; round < 64 && writtenFrames < expected; round++)
                        writtenFrames += Resample(resampler, block, 1024, channels, resampled!, encoder,
                            expected - writtenFrames);
                }

                // A file that stopped decoding well short of its own length is
                // not converted, it is cut off, and saying "converted" would hide that.
                // The length asked again: at the start an MP3's was Media
                // Foundation's estimate (its frame walk had only just begun),
                // which on a VBR file with no header was minutes too long, and a
                // whole, good conversion was thrown away as cut off.
                duration = decoder.Duration;
                double decodedSeconds = decodedFrames / (double)decodedRate;
                if (duration > 2 && decodedSeconds < duration * 0.9 - 1)
                    throw new AudioEncodeException(
                        $"only {AudioPlayer.SpokenTime(decodedSeconds)} of {AudioPlayer.SpokenTime(duration)} could be read");
                if (decodedFrames == 0)
                    throw new AudioEncodeException("no audio could be read from it");

                token.ThrowIfCancellationRequested();
                encoder.Finish();
            }
            finally { encoder.Dispose(); }

            if (encoder.TagsAfterwards && tags != null && !tags.IsEmpty &&
                (output != null || !AudioTags.WriteTagSet(path, tags)))
            {
                lock (notes)
                {
                    var note = $"Windows would not write the tags into the {choice.FormatName} file";
                    if (!notes.Contains(note)) notes.Add(note);
                }
            }

            fraction?.Invoke(1);
            return writtenFrames;
        }

        private static long Resample(IResampler resampler, float[] input, int frames, int channels, float[] output,
            IAudioEncoder encoder, long limit)
        {
            long written = 0;
            int consumed = 0;
            int capacity = output.Length / channels;

            while (consumed < frames)
            {
                int inLength = frames - consumed;
                int outLength = capacity;
                resampler.ProcessInterleaved(
                    input.AsSpan(consumed * channels, inLength * channels), ref inLength,
                    output.AsSpan(0, capacity * channels), ref outLength);

                int keep = (int)Math.Min(outLength, limit - written);
                if (keep > 0)
                {
                    encoder.Write(output, keep);
                    written += keep;
                }

                if (inLength <= 0 && outLength <= 0) break;
                consumed += inLength;
            }

            return written;
        }

        /// <summary>
        /// Channels changed by hand, for the rare decoder that would not do it:
        /// to mono by averaging, from mono by copying, otherwise the first
        /// channels kept and any extra filled from the last one. Down to
        /// stereo from 3 to 8 channels uses a fixed ITU-style mix in WAVE
        /// order (LFE dropped), so the centre and surrounds are kept; any
        /// other down-mix folds channel c into output c % to. Each mix is
        /// divided by its fixed total weight, so it cannot clip.
        /// </summary>
        internal static void MapChannels(float[] input, int from, float[] output, int to, int frames)
        {
            const float g = 0.7071f;
            if (to == 2 && from >= 3 && from <= 8)
            {
                float norm = from switch
                {
                    3 or 4 => 1f / (1f + g),
                    5 or 6 => 1f / (1f + 2f * g),
                    7 => 1f / (1.5f + 2f * g),
                    _ => 1f / (1f + 3f * g),
                };
                for (int i = 0; i < frames; i++)
                {
                    int s = i * from, d = i * 2;
                    float fl = input[s], fr = input[s + 1], l, r;
                    switch (from)
                    {
                        case 3:
                            l = fl + g * input[s + 2];
                            r = fr + g * input[s + 2];
                            break;
                        case 4:
                            l = fl + g * input[s + 2];
                            r = fr + g * input[s + 3];
                            break;
                        case 5:
                            l = fl + g * input[s + 2] + g * input[s + 3];
                            r = fr + g * input[s + 2] + g * input[s + 4];
                            break;
                        case 6:
                            l = fl + g * input[s + 2] + g * input[s + 4];
                            r = fr + g * input[s + 2] + g * input[s + 5];
                            break;
                        case 7:
                            l = fl + g * input[s + 2] + g * input[s + 5] + 0.5f * input[s + 4];
                            r = fr + g * input[s + 2] + g * input[s + 6] + 0.5f * input[s + 4];
                            break;
                        default:
                            l = fl + g * input[s + 2] + g * input[s + 4] + g * input[s + 6];
                            r = fr + g * input[s + 2] + g * input[s + 5] + g * input[s + 7];
                            break;
                    }
                    output[d] = l * norm;
                    output[d + 1] = r * norm;
                }
                return;
            }
            if (from > to && to > 1)
            {
                var total = new float[to];
                for (int c = 0; c < from; c++) total[c % to] += c < to ? 1f : g;
                for (int i = 0; i < frames; i++)
                {
                    int s = i * from, d = i * to;
                    for (int o = 0; o < to; o++) output[d + o] = 0f;
                    for (int c = 0; c < from; c++)
                        output[d + c % to] += (c < to ? 1f : g) * input[s + c];
                    for (int o = 0; o < to; o++) output[d + o] /= total[o];
                }
                return;
            }
            for (int i = 0; i < frames; i++)
            {
                int src = i * from, dst = i * to;
                if (to == 1)
                {
                    float sum = 0;
                    for (int c = 0; c < from; c++) sum += input[src + c];
                    output[dst] = sum / from;
                    continue;
                }
                for (int c = 0; c < to; c++) output[dst + c] = input[src + Math.Min(c, from - 1)];
            }
        }
    }
}
