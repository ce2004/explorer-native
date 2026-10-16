using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    internal sealed class ConvertResult
    {
        public int Converted;
        public int Skipped;
        public bool Cancelled;
        public List<ConvertFailure> Failures { get; } = new();
        public List<string> Notes { get; } = new();
        public List<string> Written { get; } = new();

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

            var text = Converted == 0 && Failures.Count == 0 && Skipped == 0
                ? "Nothing to convert"
                : $"Converted {NameRules.Items(Converted, "file")} to {choice.Label}";

            if (Skipped > 0) text += $", {Skipped} skipped because the name was taken";

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
        /// <summary>Files converted at once. Encoding is CPU-bound and three keeps the machine usable.</summary>
        public static int Parallel => Math.Clamp(Environment.ProcessorCount - 1, 1, 3);

        private const int BlockFrames = 4096;

        /// <summary>The temporary names start with this, so a folder walk never converts one.</summary>
        internal const string TempMarker = ".converting-";

        /// <summary>
        /// The audio files in a selection: files as they are, folders opened (and
        /// their subfolders when asked). One scan per folder; links to folders are
        /// not followed. Runs on a worker.
        /// </summary>
        public static List<string> Gather(IEnumerable<string> paths, bool subfolders, string? audioExtensions,
            CancellationToken token)
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
        public static ConvertPlan Plan(IReadOnlyList<string> sources, AudioFormatChoice choice)
        {
            var plan = new ConvertPlan();
            foreach (var source in sources) plan.Claimed.Add(source);

            foreach (var source in sources)
            {
                long bytes = 0;
                try { bytes = new FileInfo(NameRules.ExactPath(source)).Length; } catch { }

                var target = TargetFor(source, choice);

                // Itself, another source in this batch, or another job's target.
                if (plan.Claimed.Contains(target))
                {
                    target = FileOperations.UniqueName(target, plan.Claimed, folder: false);
                    plan.Claimed.Add(target);
                    plan.Jobs.Add(new ConvertJob(source, target, bytes, false));
                    continue;
                }

                var job = new ConvertJob(source, target, bytes, false);
                plan.Claimed.Add(target);
                plan.Jobs.Add(job);
                if (Taken(target)) plan.Clashing.Add(job);
            }

            return plan;
        }

        /// <summary>
        /// The jobs to run once the person has answered for the clashing names:
        /// skip them, replace them, or keep both with a numbered name. Runs on a
        /// worker (numbering checks the disk).
        /// </summary>
        public static List<ConvertJob> Resolve(ConvertPlan plan, FileOperations.ConflictChoice choice, out int skipped)
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
                        if (Directory.Exists(NameRules.ExactPath(job.Target))) goto default;
                        jobs.Add(job with { Replace = true });
                        break;

                    case FileOperations.ConflictChoice.Cancel:
                        return new List<ConvertJob>();

                    default:
                        var fresh = FileOperations.UniqueName(job.Target, plan.Claimed, folder: false);
                        plan.Claimed.Add(fresh);
                        jobs.Add(job with { Target = fresh });
                        break;
                }
            }

            return jobs;
        }

        /// <summary>
        /// Converts the jobs, a few at a time, reporting progress like a copy
        /// (bytes are the sources' sizes, counted as each one is decoded).
        /// </summary>
        public static async Task<ConvertResult> RunAsync(IReadOnlyList<ConvertJob> jobs, AudioFormatChoice choice,
            IProgress<TransferProgress>? progress, CancellationToken token, int parallel = 0)
        {
            var result = new ConvertResult();
            if (parallel <= 0) parallel = Parallel;

            long total = jobs.Sum(j => Math.Max(1, j.Bytes));
            var fractions = new double[jobs.Count];
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
                    progress.Report(new TransferProgress((long)bytes, total, Volatile.Read(ref done), jobs.Count,
                        current, bytes / seconds, clock.Elapsed, ItemsKnown: true));
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
                    lock (reportGate) current = Path.GetFileName(job.Source);
                    Report(true);

                    var written = await Task.Run(() => ConvertFile(job, choice, f =>
                    {
                        fractions[index] = f;
                        Report(false);
                    }, token, result.Notes), token).ConfigureAwait(false);

                    fractions[index] = 1;
                    lock (result)
                    {
                        result.Converted++;
                        result.Written.Add(written);
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
            _ => ex.Message.TrimEnd('.'),
        };

        /// <summary>
        /// One file, start to finish, on the calling thread: every COM object the
        /// encoder makes is made and used here. Returns the path written. On a
        /// failure or a cancel the temporary file is deleted and the source is
        /// as it was.
        /// </summary>
        public static string ConvertFile(ConvertJob job, AudioFormatChoice choice, Action<double>? fraction,
            CancellationToken token, ICollection<string> notes)
        {
            token.ThrowIfCancellationRequested();

            var temp = TempPathBeside(job.Target);
            try
            {
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

        /// <summary>A hidden-looking temporary name in the target's folder, keeping the target's extension.</summary>
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
        private static bool Taken(string path)
        {
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
            AudioTagSet? tags, Action<double>? fraction, CancellationToken token, ICollection<string> notes)
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
            var encoder = AudioEncoders.Create(choice, path, rate, channels, sourceBits, tags, notes);
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

            if (encoder.TagsAfterwards && tags != null && !tags.IsEmpty && !AudioTags.WriteTagSet(path, tags))
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
        /// channels kept and any extra filled from the last one.
        /// </summary>
        internal static void MapChannels(float[] input, int from, float[] output, int to, int frames)
        {
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
