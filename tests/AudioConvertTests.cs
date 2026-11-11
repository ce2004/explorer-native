using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The audio converter and the editor's edit list. Every fixture is a
    /// generated tone (silence would hide a converter that writes nothing),
    /// every conversion is decoded back and measured, and every audio run has
    /// its own 60-second watchdog.
    /// </summary>
    internal static class AudioConvertTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Audio converter:");
            Console.WriteLine("  (Windows encoders here: " + string.Join(", ",
                new[] { AudioFormatKind.Mp3, AudioFormatKind.M4a, AudioFormatKind.Flac }
                    .Select(k => $"{AudioFormats.NameOf(k)} {(MediaFoundationEncoder.HasEncoder(k) ? "yes" : "no")}")) + ")");
            FormatTableTests();
            await WithWatchdog("every format round-trips", EveryFormatTests);
            await WithWatchdog("our own FLAC encoder is lossless", OwnFlacTests);
            await WithWatchdog("tags are carried across", TagTests);
            await WithWatchdog("existing files are never overwritten unasked", NoOverwriteTests);
            await WithWatchdog("a cancelled batch leaves nothing behind", CancelTests);
            await WithWatchdog("a batch reports progress and a summary", BatchTests);
            await WithWatchdog("Opus goes to its top rate", OpusTopTests);
            await WithWatchdog("Drive conversions share one gigabyte", WorkBudgetTests);
            DeleteOriginalTests();
            await WithWatchdog("a stopped batch is finished after a restart", ResumeTests);
            Console.WriteLine();

            Console.WriteLine("Audio editor:");
            EditListTests();
            HistoryTests();
            SpokenTests();
            await WithWatchdog("a cut renders to the right length", RenderTests);
            await WithWatchdog("the editor plays through its edits", PlaybackTests);
            EditorSourceTests();
            Console.WriteLine();
        }

        /// <summary>The 60-second watchdog every audio harness has: a run that hangs fails instead.</summary>
        private static async Task WithWatchdog(string what, Func<Task> body)
        {
            var run = Task.Run(body);
            var done = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60)));
            if (done != run) { Check($"{what} within 60 seconds", false, "the watchdog fired"); return; }
            try { await run; }
            catch (Exception ex) { Check($"{what} ran to the end", false, ex.GetType().Name + ": " + ex.Message); }
        }

        private static string NewDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-convert-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        /// <summary>Decodes a file and says how long it is and how loud it gets.</summary>
        private static (double Seconds, float Peak, int Rate, string Why) Measure(string path)
        {
            var decoder = TrackDecoder.Open(path, null, 0, 0, out string why);
            if (decoder == null) return (0, 0, 0, why);
            using (decoder)
            {
                var block = new float[4096 * Math.Max(1, decoder.Channels)];
                long frames = 0;
                float peak = 0;
                int empties = 0;
                while (empties < 3)
                {
                    int got = decoder.Read(block, 4096);
                    if (got <= 0) { empties++; continue; }
                    frames += got;
                    for (int i = 0; i < got * decoder.Channels; i++) peak = Math.Max(peak, Math.Abs(block[i]));
                }
                return (frames / (double)decoder.SampleRate, peak, decoder.SampleRate, "");
            }
        }

        private static void FormatTableTests()
        {
            Check("every format choice has a distinct id",
                AudioFormats.All.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == AudioFormats.All.Count);
            Check("every format offers at least one quality",
                AudioFormats.Kinds.All(k => AudioFormats.QualitiesOf(k).Any()));
            Equal("an unknown remembered id falls back to the default", AudioFormats.DefaultId,
                AudioFormats.ById("ogg-vorbis-500").Id);
            Equal("a quality reads as words", "MP3, 192 kilobits per second", AudioFormats.ById("mp3-192").Label);
            Equal("an MP3 is saved back as MP3 at no less than its rate", "mp3-256",
                AudioFormats.SameFormatAs(".mp3", 224)?.Id ?? "(null)");
            Check("a WMA has to ask for a format", AudioFormats.SameFormatAs(".wma", 128) == null);
            Equal("the setting starts at the default", AudioFormats.DefaultId, new Settings().AudioConvertFormat);

            var s = new Settings { AudioConvertFormat = "kept-from-a-newer-build" };
            Equal("and a value this build does not know is left as it is", "kept-from-a-newer-build", s.AudioConvertFormat);
        }

        private static async Task EveryFormatTests()
        {
            var dir = NewDir();
            try
            {
                var source = Path.Combine(dir, "tone.wav");
                Tests.WriteTone(source, 3, 0.5);
                var sourceHash = Hash(source);

                foreach (var id in new[] { "mp3-192", "m4a-192", "m4a-256", "opus-128", "flac", "wav-16", "wav-keep" })
                {
                    var choice = AudioFormats.ById(id);
                    var why = AudioEncoders.WhyUnavailable(choice.Kind);
                    if (why != null)
                    {
                        Console.WriteLine($"  ({choice.Label}: {why}; skipped)");
                        continue;
                    }

                    var plan = AudioConverter.Plan(new[] { source }, choice);
                    var jobs = AudioConverter.Resolve(plan, FileOperations.ConflictChoice.Rename, out _);
                    var result = await AudioConverter.RunAsync(jobs, choice, null, CancellationToken.None);

                    Check($"{choice.Label}: converted", result.Converted == 1 && result.Failures.Count == 0,
                        result.Summary(choice));
                    if (result.Written.Count == 0) continue;

                    var written = result.Written[0];
                    Check($"{choice.Label}: written beside the source with the format's extension",
                        Path.GetDirectoryName(written) == dir &&
                        string.Equals(Path.GetExtension(written), choice.Extension, StringComparison.OrdinalIgnoreCase),
                        written);

                    var (seconds, peak, _, failed) = Measure(written);
                    bool lossless = choice.Kind is AudioFormatKind.Flac or AudioFormatKind.Wav;
                    double tolerance = lossless ? 0.0001 : 0.1;
                    Check($"{choice.Label}: decodes back to 3 seconds ({seconds:0.0000})",
                        Math.Abs(seconds - 3) <= tolerance, failed);
                    Check($"{choice.Label}: and it is the tone, not silence (peak {peak:0.###})",
                        peak > 0.3f && peak < 0.75f, peak.ToString("0.####"));

                    try { File.Delete(written); } catch { }
                }

                Check("the source was never touched", Hash(source) == sourceHash);
                Check("no temporary files are left",
                    !Directory.EnumerateFiles(dir).Any(f => Path.GetFileName(f).StartsWith(AudioConverter.TempMarker)));
            }
            finally { Cleanup(dir); }
        }

        private static async Task OwnFlacTests()
        {
            await Task.Yield();
            var dir = NewDir();
            try
            {
                foreach (int channels in new[] { 1, 2 })
                {
                    var source = Path.Combine(dir, $"tone{channels}.wav");
                    Tests.WriteTone(source, 2.5, 0.5, 44100, channels);
                    var flac = Path.Combine(dir, $"own{channels}.flac");

                    var original = ReadAll(source, out int rate);
                    using (var encoder = new FlacEncoder(flac, rate, channels, 16,
                               new AudioTagSet("Own title", "Someone", null, null, null, 2024, 3)))
                    {
                        encoder.Write(original, original.Length / channels);
                        encoder.Finish();
                    }

                    var back = ReadAll(flac, out _);
                    Check($"our FLAC encoder ({channels} channel) gives back every sample exactly",
                        back.Length == original.Length && back.AsSpan().SequenceEqual(original),
                        $"{back.Length} against {original.Length} samples");

                    long before = new FileInfo(source).Length, after = new FileInfo(flac).Length;
                    Check($"and it is smaller than the WAV ({after:N0} against {before:N0} bytes)", after < before);
                }

                // A block of digital silence takes the constant path; a last short block the odd sizes.
                var quiet = Path.Combine(dir, "quiet.flac");
                var samples = new float[(4096 + 1000) * 2];
                for (int i = 4096 * 2; i < samples.Length; i++) samples[i] = (float)Math.Sin(i * 0.01) * 0.25f;
                using (var encoder = new FlacEncoder(quiet, 48000, 2, 24, null))
                {
                    encoder.Write(samples, samples.Length / 2);
                    encoder.Finish();
                }
                var (seconds, peak, _, why) = Measure(quiet);
                Check($"a 24-bit FLAC with a silent block and a short last block decodes ({seconds * 48000:0} frames)",
                    Math.Abs(seconds * 48000 - 5096) < 0.5 && peak > 0.2f, why);
            }
            finally { Cleanup(dir); }
        }

        private static async Task TagTests()
        {
            await Task.Yield();
            var dir = NewDir();
            try
            {
                var source = Path.Combine(dir, "tone.wav");
                Tests.WriteTone(source, 2, 0.5);
                var tagged = Path.Combine(dir, "tagged.flac");
                var original = ReadAll(source, out int rate);
                var tags = new AudioTagSet("Converter test", "Explorer Native", "Tones", "Various", "Test", 2026, 7);
                using (var encoder = new FlacEncoder(tagged, rate, 2, 16, tags))
                {
                    encoder.Write(original, original.Length / 2);
                    encoder.Finish();
                }

                var read = AudioTags.ReadTagSet(tagged);
                Check("Windows reads the tags our FLAC encoder writes",
                    read?.Title == "Converter test" && read.Artist == "Explorer Native" && read.Track == 7,
                    read?.ToString() ?? "(none)");
                if (read == null) return;

                foreach (var id in new[] { "mp3-192", "m4a-192", "flac", "wav-16" })
                {
                    var choice = AudioFormats.ById(id);
                    if (AudioEncoders.WhyUnavailable(choice.Kind) != null) continue;

                    var jobs = AudioConverter.Resolve(AudioConverter.Plan(new[] { tagged }, choice),
                        FileOperations.ConflictChoice.Rename, out _);
                    var result = await AudioConverter.RunAsync(jobs, choice, null, CancellationToken.None);
                    if (result.Written.Count == 0) { Check($"{choice.Label}: tagged file converted", false, result.Summary(choice)); continue; }

                    var back = AudioTags.ReadTagSet(result.Written[0]);
                    bool across = back?.Title == "Converter test" && back.Artist == "Explorer Native" && back.Album == "Tones";
                    if (choice.Kind == AudioFormatKind.Wav && !across)
                    {
                        // Windows reads no tags from a WAV on some versions. Then
                        // the summary has to say so rather than pretend.
                        Check("WAV: when Windows will not hold the tags, the summary says so",
                            result.Summary(choice).Contains("would not write the tags"), result.Summary(choice));
                    }
                    else
                        Check($"{choice.Label}: title, artist and album come across", across, back?.ToString() ?? "(none)");
                    try { File.Delete(result.Written[0]); } catch { }
                }

                // Opus: Windows cannot read Ogg tags, so the header is read directly.
                var opus = AudioFormats.ById("opus-96");
                var opusJobs = AudioConverter.Resolve(AudioConverter.Plan(new[] { tagged }, opus),
                    FileOperations.ConflictChoice.Rename, out _);
                var opusResult = await AudioConverter.RunAsync(opusJobs, opus, null, CancellationToken.None);
                var head = opusResult.Written.Count > 0 ? File.ReadAllBytes(opusResult.Written[0]) : Array.Empty<byte>();
                var text = System.Text.Encoding.UTF8.GetString(head, 0, Math.Min(head.Length, 4096));
                Check("Opus: the tags are in its OpusTags header",
                    text.Contains("TITLE=Converter test") && text.Contains("ARTIST=Explorer Native"));
            }
            finally { Cleanup(dir); }
        }

        private static async Task NoOverwriteTests()
        {
            var dir = NewDir();
            try
            {
                var source = Path.Combine(dir, "song.wav");
                Tests.WriteTone(source, 1, 0.5);
                var sourceHash = Hash(source);
                var existing = Path.Combine(dir, "song.flac");
                File.WriteAllText(existing, "the person's own file");
                var flac = AudioFormats.ById("flac");

                var plan = AudioConverter.Plan(new[] { source }, flac);
                Equal("an existing target is found before anything is written", "song.flac",
                    string.Join(",", plan.Clashing.Select(j => Path.GetFileName(j.Target))));

                // Skip.
                var skipped = AudioConverter.Resolve(plan, FileOperations.ConflictChoice.Skip, out int skipCount);
                var result = await AudioConverter.RunAsync(skipped, flac, null, CancellationToken.None);
                Check("skip converts nothing and keeps the file",
                    skipCount == 1 && result.Converted == 0 && File.ReadAllText(existing) == "the person's own file");

                // Keep both.
                plan = AudioConverter.Plan(new[] { source }, flac);
                var both = AudioConverter.Resolve(plan, FileOperations.ConflictChoice.Rename, out _);
                result = await AudioConverter.RunAsync(both, flac, null, CancellationToken.None);
                Check("keep both writes a numbered name", result.Written.Count == 1 &&
                    Path.GetFileName(result.Written[0]) == "song (2).flac",
                    string.Join(",", result.Written.Select(Path.GetFileName)));
                Check("and leaves the existing file exactly as it was",
                    File.ReadAllText(existing) == "the person's own file");

                // Replace, chosen explicitly.
                plan = AudioConverter.Plan(new[] { source }, flac);
                var replace = AudioConverter.Resolve(plan, FileOperations.ConflictChoice.Overwrite, out _);
                result = await AudioConverter.RunAsync(replace, flac, null, CancellationToken.None);
                Check("replace, when chosen, puts the converted file in its place",
                    result.Converted == 1 && new FileInfo(existing).Length > 1000);

                // The same format as the source: never itself.
                var wav = AudioFormats.ById("wav-16");
                plan = AudioConverter.Plan(new[] { source }, wav);
                Check("converting to the source's own format is not a question", plan.Clashing.Count == 0);
                var same = AudioConverter.Resolve(plan, FileOperations.ConflictChoice.Overwrite, out _);
                result = await AudioConverter.RunAsync(same, wav, null, CancellationToken.None);
                Check("and writes a new name rather than over the source",
                    result.Written.Count == 1 && !string.Equals(result.Written[0], source, StringComparison.OrdinalIgnoreCase),
                    string.Join(",", result.Written));
                Check("the source is exactly as it was", Hash(source) == sourceHash);

                // Same base name from two formats: the second is numbered, not a clash.
                var opus = AudioFormats.ById("opus-64");
                var a = Path.Combine(dir, "same.wav");
                Tests.WriteTone(a, 1, 0.4);
                var b = Path.Combine(dir, "same.flac");
                File.Copy(existing, b);
                var sameName = AudioConverter.Plan(new[] { a, b }, opus);
                Check("two sources with one base name get two names",
                    sameName.Jobs.Count == 2 && sameName.Clashing.Count == 0 &&
                    !string.Equals(sameName.Jobs[0].Target, sameName.Jobs[1].Target, StringComparison.OrdinalIgnoreCase),
                    string.Join(",", sameName.Jobs.Select(j => Path.GetFileName(j.Target))));

                // A target that is another file in the batch is never replaced, even when asked.
                var mp3Named = Path.Combine(dir, "same.opus");
                File.Copy(a, mp3Named);
                var intoSource = AudioConverter.Plan(new[] { a, mp3Named }, opus);
                Check("a target that is another source in the batch is numbered, not offered for replacing",
                    intoSource.Clashing.Count == 0 &&
                    intoSource.Jobs.All(j => !string.Equals(j.Target, mp3Named, StringComparison.OrdinalIgnoreCase)),
                    string.Join(",", intoSource.Jobs.Select(j => Path.GetFileName(j.Target))));
            }
            finally { Cleanup(dir); }
        }

        private static async Task CancelTests()
        {
            var dir = NewDir();
            try
            {
                var source = Path.Combine(dir, "long.wav");
                Tests.WriteTone(source, 60, 0.5);
                var choice = AudioFormats.ById("flac");
                var jobs = AudioConverter.Resolve(AudioConverter.Plan(new[] { source }, choice),
                    FileOperations.ConflictChoice.Rename, out _);

                using var cts = new CancellationTokenSource();
                var progress = new SyncProgress(p => { if (p.BytesDone > 0) cts.Cancel(); });
                var result = await AudioConverter.RunAsync(jobs, choice, progress, cts.Token);

                Check("a cancel part way through is reported as cancelled", result.Cancelled && result.Converted == 0,
                    result.Summary(choice));
                var left = Directory.EnumerateFiles(dir).Select(Path.GetFileName).Where(n => n != "long.wav").ToList();
                Check("and nothing half-written is left beside the source", left.Count == 0, string.Join(",", left));

                // A file that is not audio fails in words and leaves nothing either.
                var bad = Path.Combine(dir, "broken.mp3");
                File.WriteAllBytes(bad, Enumerable.Repeat((byte)0x55, 5000).ToArray());
                var badJobs = AudioConverter.Resolve(AudioConverter.Plan(new[] { bad }, choice),
                    FileOperations.ConflictChoice.Rename, out _);
                var badResult = await AudioConverter.RunAsync(badJobs, choice, null, CancellationToken.None);
                Check("a file that cannot be decoded is a named failure",
                    badResult.Failures.Count == 1 && badResult.Failures[0].Name == "broken.mp3",
                    badResult.Summary(choice));
                Check("and the summary says which and why",
                    badResult.Summary(choice).Contains("1 failed: broken.mp3, "), badResult.Summary(choice));
                left = Directory.EnumerateFiles(dir).Select(Path.GetFileName)
                    .Where(n => n != "long.wav" && n != "broken.mp3").ToList();
                Check("with nothing left behind", left.Count == 0, string.Join(",", left));
            }
            finally { Cleanup(dir); }
        }

        private static async Task BatchTests()
        {
            var dir = NewDir();
            try
            {
                var folder = Path.Combine(dir, "album");
                var inner = Path.Combine(folder, "disc 2");
                Directory.CreateDirectory(inner);
                for (int i = 1; i <= 3; i++) Tests.WriteTone(Path.Combine(folder, $"track {i}.wav"), 1, 0.4);
                Tests.WriteTone(Path.Combine(inner, "track 4.wav"), 1, 0.4);
                File.WriteAllText(Path.Combine(folder, "notes.txt"), "not audio");

                var shallow = AudioConverter.Gather(new[] { folder }, false, null, CancellationToken.None);
                var deep = AudioConverter.Gather(new[] { folder }, true, null, CancellationToken.None);
                Equal("a folder gives its audio files", "3", shallow.Count.ToString());
                Equal("and with subfolders, theirs too", "4", deep.Count.ToString());

                var opus = AudioFormats.ById("opus-64");
                var jobs = AudioConverter.Resolve(AudioConverter.Plan(deep, opus), FileOperations.ConflictChoice.Rename, out _);
                var reports = new List<TransferProgress>();
                var progress = new SyncProgress(p => { lock (reports) reports.Add(p); });
                var result = await AudioConverter.RunAsync(jobs, opus, progress, CancellationToken.None, parallel: 3);

                Check("four files convert in parallel", result.Converted == 4, result.Summary(opus));
                TransferProgress? last;
                lock (reports) last = reports.LastOrDefault();
                Check("the last progress report is four of four, all the bytes",
                    last != null && last.ItemsDone == 4 && last.ItemsTotal == 4 && last.BytesDone == last.BytesTotal,
                    last == null ? "none" : $"{last.ItemsDone}/{last.ItemsTotal}, {last.BytesDone}/{last.BytesTotal}");
                Check("the output sits beside each source",
                    File.Exists(Path.Combine(inner, "track 4.opus")) && File.Exists(Path.Combine(folder, "track 1.opus")));
                Equal("the summary is one sentence", "Converted 4 files to Opus, 64 kilobits per second.",
                    result.Summary(opus));

                var failed = new ConvertResult { Converted = 12 };
                failed.Failures.Add(new ConvertFailure("song.wma", "it could not be read"));
                Equal("a failure is named in the summary",
                    "Converted 12 files to MP3, 192 kilobits per second, 1 failed: song.wma, it could not be read.",
                    failed.Summary(AudioFormats.ById("mp3-192")));
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// EXPLORERNATIVE_SELFTEST_ONLY=convertprobe with EXPLORERNATIVE_CONVERTPROBE_DIR:
        /// converts every audio file in a real folder to every format, each
        /// into its own subfolder of a scratch copy, and checks each result
        /// decodes to the original's length and is not silent. The folder
        /// itself is only read.
        /// </summary>
        public static async Task Probe(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;
            var from = Environment.GetEnvironmentVariable("EXPLORERNATIVE_CONVERTPROBE_DIR") ?? "";
            if (!Directory.Exists(from)) { Check("the probe folder is there", false, from); return; }

            var dir = NewDir();
            try
            {
                var originals = AudioConverter.Gather(new[] { from }, true, null, CancellationToken.None);
                Console.WriteLine($"Convert probe: {originals.Count} files from {from}");
                var lengths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var original in originals)
                {
                    var (seconds, peak, _, why) = Measure(original);
                    Check($"{Path.GetFileName(original)}: the original decodes", seconds > 0, why);
                    lengths[Path.GetFileName(original)] = seconds;
                    Console.WriteLine($"  {Path.GetFileName(original)}: {seconds:F1}s, peak {peak:F2}");
                }

                foreach (var id in new[] { "mp3-320", "m4a-256", "opus-96", "opus-510", "flac", "wav-16", "wav-keep" })
                {
                    var choice = AudioFormats.ById(id);
                    if (AudioEncoders.WhyUnavailable(choice.Kind) is { } unavailable)
                    {
                        Console.WriteLine($"  ({choice.Label}: {unavailable}; skipped)");
                        continue;
                    }

                    var into = Path.Combine(dir, id);
                    Directory.CreateDirectory(into);
                    foreach (var original in originals) File.Copy(original, Path.Combine(into, Path.GetFileName(original)));

                    var sources = AudioConverter.Gather(new[] { into }, false, null, CancellationToken.None);
                    var jobs = AudioConverter.Resolve(AudioConverter.Plan(sources, choice),
                        FileOperations.ConflictChoice.Rename, out _);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var result = await AudioConverter.RunAsync(jobs, choice, null, CancellationToken.None);
                    Check($"{choice.Label}: all {sources.Count} converted in {clock.Elapsed.TotalSeconds:F0}s",
                        result.Converted == sources.Count && result.Failures.Count == 0, result.Summary(choice));

                    for (int i = 0; i < jobs.Count; i++)
                    {
                        var written = result.Written.FirstOrDefault(w =>
                            string.Equals(Path.GetFileNameWithoutExtension(w).Replace(" (2)", ""),
                                Path.GetFileNameWithoutExtension(jobs[i].Source), StringComparison.OrdinalIgnoreCase));
                        if (written == null) continue;
                        var (seconds, peak, _, why) = Measure(written);
                        double want = lengths[Path.GetFileName(jobs[i].Source)];
                        Check($"{choice.Label}: {Path.GetFileName(written)} is the original's length and has sound",
                            Math.Abs(seconds - want) < Math.Max(0.2, want * 0.002) && peak > 0.001,
                            $"{seconds:F2}s against {want:F2}s, peak {peak:F3}, {new FileInfo(written).Length:N0} bytes {why}");
                    }
                }
            }
            finally { Cleanup(dir); }
        }

        private static async Task OpusTopTests()
        {
            // Files already in the format asked for are left alone, by name.
            var toOpus = AudioFormats.All.First(c => c.Id == "opus-96");
            var mixed = new[] { @"C:\r\a.flac", @"C:\r\b.opus", @"C:\r\c.OPUS", @"C:\r\d.mp3", @"C:\r\opus.wav" };
            var worth = AudioConverter.NotAlready(mixed, toOpus, out int alreadyOpus);
            Equal("converting to Opus leaves the Opus files out", "a.flac,d.mp3,opus.wav",
                string.Join(",", worth.Select(Path.GetFileName)));
            Equal("and counts them", "2", alreadyOpus.ToString());
            foreach (var same in new[] { "flac", "wav-16" })
            {
                var again = AudioFormats.All.First(c => c.Id == same);
                var kept = AudioConverter.NotAlready(new[] { @"C:\r\a.flac", @"C:\r\b.wav" }, again, out int leftOut);
                Equal($"FLAC to FLAC and WAV to WAV are still converted ({same})", "2 0", $"{kept.Count} {leftOut}");
            }
            Equal("a file a core is converted at once", Environment.ProcessorCount.ToString(), AudioConverter.Parallel.ToString());
            await SpoolTests();
            Check("libopus itself loads here", NativeOpus.Available, null);
            Equal("Opus works hardest where it can be heard, and faster above that", "10 10 5 5",
                $"{OpusFileEncoder.ComplexityFor(96000)} {OpusFileEncoder.ComplexityFor(160000)} {OpusFileEncoder.ComplexityFor(192000)} {OpusFileEncoder.ComplexityFor(320000)}");
            var mainSource = Source("MainForm.cs");
            Check("a conversion lets go of the track the player was readying, and readies none meanwhile",
                mainSource.Contains("_converting++;") && mainSource.Contains("Program.CancelAudioPrepare();") &&
                mainSource.Contains("&& _converting == 0)"), null);
            long nativeBefore = NativeOpus.Packets;
            OpusSectionTests("libopus");
            OpusSectionTests("libopus at 320, the faster setting", 320);
            long nativeAfter = NativeOpus.Packets;
            Check("converting to Opus uses libopus", nativeAfter > nativeBefore, $"{nativeBefore} then {nativeAfter}");
            OpusFileEncoder.ManagedOnly = true;
            try { OpusSectionTests("the managed encoder"); }
            finally { OpusFileEncoder.ManagedOnly = false; }
            Equal("and the managed encoder is still there for when it will not load", nativeAfter.ToString(), NativeOpus.Packets.ToString());
            var said = new ConvertResult { Converted = 3, AlreadyFormat = 2 }.Summary(toOpus);
            Check("the summary says how many were left alone", said.Contains("2 already Opus and left alone"), said);
            Check("MainForm leaves them out before planning",
                Source("MainForm.cs").Contains("AudioConverter.NotAlready("), null);

            Equal("the highest Opus quality is 510 kilobits", "opus-510",
                AudioFormats.QualitiesOf(AudioFormatKind.Opus).OrderBy(c => c.Kbps).Last().Id);
            Equal("and the usual answers are still there", "64,96,128,160,192,256,320,510",
                string.Join(",", AudioFormats.QualitiesOf(AudioFormatKind.Opus).Select(c => c.Kbps)));

            var dir = NewDir();
            try
            {
                var source = Path.Combine(dir, "tone.wav");
                Tests.WriteTone(source, 4, 0.5);
                long[] sizes = new long[2];
                int at = 0;
                foreach (var id in new[] { "opus-128", "opus-510" })
                {
                    var choice = AudioFormats.ById(id);
                    var jobs = AudioConverter.Resolve(AudioConverter.Plan(new[] { source }, choice),
                        FileOperations.ConflictChoice.Rename, out _);
                    var result = await AudioConverter.RunAsync(jobs, choice, null, CancellationToken.None);
                    Check($"{choice.Label}: converted", result.Converted == 1, result.Summary(choice));
                    if (result.Written.Count == 0) return;

                    var (seconds, peak, _, why) = Measure(result.Written[0]);
                    Check($"{choice.Label}: plays back whole and audible",
                        Math.Abs(seconds - 4) < 0.1 && peak > 0.2, $"{seconds:F2}s peak {peak:F2} {why}");
                    sizes[at++] = new FileInfo(result.Written[0]).Length;
                }
                Check("the top rate writes a bigger file than 128", sizes[1] > sizes[0], $"{sizes[0]} then {sizes[1]}");

                // One channel takes at most 300 kilobits; asking for 510 must not throw.
                var mono = Path.Combine(dir, "mono.opus");
                using (var encoder = new OpusFileEncoder(mono, 1, 510, null))
                {
                    var block = new float[48000];
                    for (int i = 0; i < block.Length; i++) block[i] = (float)(0.5 * Math.Sin(i * 2 * Math.PI * 440 / 48000));
                    encoder.Write(block, block.Length);
                    encoder.Finish();
                }
                var (monoSeconds, monoPeak, _, monoWhy) = Measure(mono);
                Check("one channel at the top rate plays back", Math.Abs(monoSeconds - 1) < 0.1 && monoPeak > 0.2,
                    $"{monoSeconds:F2}s peak {monoPeak:F2} {monoWhy}");
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// A file sent in pieces as it is written (a big recording converted on
        /// Google Drive with nothing on disk): what arrives, pieces put together
        /// and late writes put in, is the file an encoder would have made.
        /// </summary>
        private static async Task SpoolTests()
        {
            await Task.Yield();
            const int Piece = 1024;

            (byte[] File, List<int> Sizes, long? Total, SpoolStream Spool) Through(Action<SpoolStream> write)
            {
                var got = new MemoryStream();
                var sizes = new List<int>();
                long? total = null;
                var spool = new SpoolStream((offset, buffer, count, whole) =>
                {
                    lock (got)
                    {
                        if (offset != got.Length) throw new IOException($"piece at {offset}, expected {got.Length}");
                        got.Write(buffer, 0, count);
                        sizes.Add(count);
                        if (whole != null) total = whole;
                    }
                }, CancellationToken.None, Piece);
                write(spool);
                spool.Complete();
                var file = got.ToArray();
                foreach (var (at, bytes) in spool.Late) Array.Copy(bytes, 0, file, at, bytes.Length);
                return (file, sizes, total, spool);
            }

            var data = new byte[Piece * 5 + 300];
            new Random(7).NextBytes(data);

            var plain = Through(s =>
            {
                for (int at = 0; at < data.Length;)
                {
                    int n = Math.Min(data.Length - at, 1 + at % 700);
                    s.Write(data, at, n);
                    at += n;
                }
            });
            Check("pieces sent in order add up to what was written", plain.File.AsSpan().SequenceEqual(data), null);
            Check("every piece but the last is a whole piece",
                plain.Sizes.Take(plain.Sizes.Count - 1).All(n => n == Piece) && plain.Sizes.Count >= 4,
                string.Join(",", plain.Sizes));
            Equal("the last piece carries the total", data.Length.ToString(), plain.Total?.ToString() ?? "none");
            Equal("nothing was late", "0", plain.Spool.Late.Count.ToString());
            Equal("and the checksum is the file's",
                Convert.ToHexString(System.Security.Cryptography.MD5.HashData(data)), plain.Spool.Md5Hex);

            // An encoder going back to finish its header after the start has gone.
            var patched = (byte[])data.Clone();
            patched[4] = 1; patched[5] = 2; patched[6] = 3; patched[7] = 4;
            var late = Through(s =>
            {
                s.Write(data, 0, data.Length);
                s.Position = 4;
                s.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);
            });
            Equal("a header finished afterwards is kept as a late write", "1", late.Spool.Late.Count.ToString());
            Check("and put in, the file is right", late.File.AsSpan().SequenceEqual(patched), null);
            Equal("the length is what was written, not where the writer was left", data.Length.ToString(),
                late.Spool.Length.ToString());

            var same = Through(s =>
            {
                s.Write(data, 0, data.Length);
                s.Position = 4;
                s.Write(data, 4, 4);
            });
            Equal("the same bytes written again are not late", "0", same.Spool.Late.Count.ToString());

            // A write near the end, still held, is not late either.
            var near = Through(s =>
            {
                s.Write(data, 0, data.Length);
                s.Position = data.Length - 10;
                s.Write(new byte[] { 9 }, 0, 1);
            });
            Check("going back a little costs nothing", near.Spool.Late.Count == 0 && near.File[data.Length - 10] == 9, null);

            // A failed upload stops the writer with the reason, and never hangs it.
            string stopped = "it went on";
            var failing = new SpoolStream((_, _, _, _) => throw new IOException("the line dropped"),
                CancellationToken.None, Piece);
            var writer = Task.Run(() =>
            {
                try { for (int i = 0; i < 200; i++) failing.Write(data, 0, data.Length); failing.Complete(); }
                catch (IOException ex) { stopped = ex.Message; }
            });
            Check("a failed upload stops the writer", await Task.WhenAny(writer, Task.Delay(20000)) == writer &&
                stopped.Contains("the line dropped"), stopped);

            // Every encoder, written through a spool: the same audio as on disk.
            var dir = NewDir();
            try
            {
                var source = Path.Combine(dir, "tone.wav");
                Tests.WriteTone(source, 4, 0.5);
                foreach (var id in new[] { "opus-96", "flac", "wav-16", "mp3-192", "m4a-192" })
                {
                    var choice = AudioFormats.ById(id);
                    if (AudioEncoders.WhyUnavailable(choice.Kind) != null) continue;

                    var made = Path.Combine(dir, "made" + choice.Extension);
                    int lateWrites = 0;
                    string? failed = null;
                    try
                    {
                        var sent = Through(s =>
                        {
                            using var decoder = TrackDecoder.Open(source, null, 0, 0, out _)!;
                            AudioConverter.Encode(decoder, choice, made, 16, null, null, CancellationToken.None,
                                new List<string>(), s);
                        });
                        lateWrites = sent.Spool.Late.Count;
                        File.WriteAllBytes(made, sent.File);
                    }
                    catch (Exception ex) { failed = ex.Message; }

                    Check($"{choice.Label}: encodes through a stream ({lateWrites} late)", failed == null, failed);
                    if (failed != null) continue;
                    var (seconds, peak, _, why) = Measure(made);
                    Check($"{choice.Label}: sent in pieces it is still 4 seconds of the tone ({seconds:0.000}, peak {peak:0.##})",
                        Math.Abs(seconds - 4) <= 0.1 && peak > 0.3f, why);
                }
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// Opus encoded in sections on every core: the joins between sections
        /// must be no worse than anywhere else in the file, and the length exact.
        /// </summary>
        private static void OpusSectionTests(string which, int kbps = 128)
        {
            var dir = NewDir();
            int before = OpusFileEncoder.SectionPackets;
            try
            {
                // Twelve seconds of a chord that swells and fades: something with
                // pitch and level for an encoder starting cold to get wrong.
                const int Rate = 48000, Seconds = 12;
                var source = new float[Rate * Seconds * 2];
                for (int i = 0; i < Rate * Seconds; i++)
                {
                    double t = i / (double)Rate;
                    double level = 0.25 + 0.15 * Math.Sin(2 * Math.PI * 0.4 * t);
                    float v = (float)(level * (Math.Sin(2 * Math.PI * 220 * t) + 0.6 * Math.Sin(2 * Math.PI * 554.37 * t) +
                                               0.4 * Math.Sin(2 * Math.PI * 1318.5 * t)) / 2);
                    source[i * 2] = v;
                    source[i * 2 + 1] = v * 0.8f;
                }

                float[] Decoded(int sectionPackets, string name)
                {
                    OpusFileEncoder.SectionPackets = sectionPackets;
                    var path = Path.Combine(dir, name);
                    using (var encoder = new OpusFileEncoder(path, 2, kbps, null))
                    {
                        var block = new float[4096 * 2];
                        for (int at = 0; at < Rate * Seconds; at += 4096)
                        {
                            int n = Math.Min(4096, Rate * Seconds - at);
                            Array.Copy(source, at * 2, block, 0, n * 2);
                            encoder.Write(block, n);
                        }
                        encoder.Finish();
                    }

                    using var decoder = TrackDecoder.Open(path, null, 0, 0, out string why)
                                        ?? throw new InvalidOperationException(why);
                    var all = new List<float>();
                    var buffer = new float[4096 * 2];
                    int empties = 0;
                    while (empties < 3)
                    {
                        int got = decoder.Read(buffer, 4096);
                        if (got <= 0) { empties++; continue; }
                        empties = 0;
                        for (int i = 0; i < got * 2; i++) all.Add(buffer[i]);
                    }
                    return all.ToArray();
                }

                // One second sections: eleven joins in twelve seconds.
                var cut = Decoded(50, "cut.opus");
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var whole = Decoded(100000, "whole.opus");
                Console.WriteLine($"  ({which}: twelve seconds encoded and read back in {clock.ElapsedMilliseconds} ms on one core, libopus packets so far {NativeOpus.Packets})");

                Equal($"{which}: encoded in sections it is exactly as long as the audio", (Rate * Seconds).ToString(), (cut.Length / 2).ToString());
                Equal($"{which}: and so is one encoded in one go", (Rate * Seconds).ToString(), (whole.Length / 2).ToString());

                // Error against the original in 20 ms windows; the worst window
                // at a join against the worst window anywhere when encoded in one go.
                double[] Errors(float[] decoded)
                {
                    int windows = Math.Min(decoded.Length, source.Length) / 2 / 960;
                    var errors = new double[windows];
                    for (int w = 0; w < windows; w++)
                    {
                        double sum = 0;
                        for (int i = w * 960 * 2; i < (w + 1) * 960 * 2; i++)
                        {
                            double d = decoded[i] - source[i];
                            sum += d * d;
                        }
                        errors[w] = Math.Sqrt(sum / (960 * 2));
                    }
                    return errors;
                }

                var cutErrors = Errors(cut);
                var wholeErrors = Errors(whole);
                double worstWhole = wholeErrors.Skip(10).Max();
                double worstJoin = 0;
                for (int join = 50; join < cutErrors.Length; join += 50)
                    for (int w = join - 2; w <= join + 3 && w < cutErrors.Length; w++)
                        worstJoin = Math.Max(worstJoin, cutErrors[w]);
                Check($"{which}: the joins between sections are as clean as the rest (worst join {worstJoin:0.00000}, worst anywhere in one go {worstWhole:0.00000})",
                    worstJoin <= worstWhole * 2 + 0.0005, null);

                // And no step in the waveform at a join: the biggest jump between
                // neighbouring samples there is no bigger than the signal makes anyway.
                double Jump(float[] decoded, int from, int to)
                {
                    double most = 0;
                    for (int i = Math.Max(1, from); i < to && i < decoded.Length / 2; i++)
                        most = Math.Max(most, Math.Abs(decoded[i * 2] - decoded[(i - 1) * 2]));
                    return most;
                }
                double usual = Jump(whole, Rate, Rate * (Seconds - 1));
                double atJoins = 0;
                for (int join = 1; join < Seconds; join++)
                    atJoins = Math.Max(atJoins, Jump(cut, join * Rate - 960, join * Rate + 1920));
                Check($"{which}: and there is no click at a join (biggest step {atJoins:0.0000}, usual {usual:0.0000})",
                    atJoins <= usual * 1.25 + 0.002, null);
            }
            catch (Exception ex) { Check("Opus in sections ran to the end", false, ex.Message); }
            finally
            {
                OpusFileEncoder.SectionPackets = before;
                Cleanup(dir);
            }
        }

        private static async Task WorkBudgetTests()
        {
            const long Mb = 1024 * 1024;
            Equal("the budget is one gigabyte", (1024 * Mb).ToString(), AudioConverter.WorkBudget.ToString());

            var none = CancellationToken.None;
            AudioConverter.AcquireWork(600 * Mb, none);
            var second = Task.Run(() => AudioConverter.AcquireWork(600 * Mb, none));
            await Task.Delay(300);
            Check("a second file that would pass a gigabyte waits", !second.IsCompleted);
            var small = Task.Run(() => AudioConverter.AcquireWork(100 * Mb, none));
            Check("one that fits goes at once", await Task.WhenAny(small, Task.Delay(3000)) == small);
            AudioConverter.ReleaseWork(600 * Mb);
            Check("and the waiting one goes when room is given back",
                await Task.WhenAny(second, Task.Delay(3000)) == second);
            AudioConverter.ReleaseWork(600 * Mb);
            AudioConverter.ReleaseWork(100 * Mb);
            Equal("everything is given back", "0", AudioConverter.WorkHeld.ToString());

            var huge = Task.Run(() => AudioConverter.AcquireWork(20 * 1024 * Mb, none));
            Check("a file bigger than the budget still goes, alone",
                await Task.WhenAny(huge, Task.Delay(3000)) == huge);
            var behind = Task.Run(() => AudioConverter.AcquireWork(Mb, none));
            await Task.Delay(300);
            Check("and nothing joins it", !behind.IsCompleted);
            AudioConverter.ReleaseWork(20 * 1024 * Mb);
            await Task.WhenAny(behind, Task.Delay(3000));
            AudioConverter.ReleaseWork(Mb);

            using var stop = new CancellationTokenSource();
            AudioConverter.AcquireWork(900 * Mb, none);
            var cancelled = Task.Run(() => AudioConverter.AcquireWork(900 * Mb, stop.Token));
            stop.Cancel();
            await Task.WhenAny(cancelled, Task.Delay(3000));
            Check("a wait for room ends on cancel", cancelled.IsCanceled || cancelled.IsFaulted);
            AudioConverter.ReleaseWork(900 * Mb);
            Equal("and takes nothing", "0", AudioConverter.WorkHeld.ToString());

            var opus = AudioFormats.ById("opus-128");
            Check("a FLAC made into Opus is estimated smaller than itself",
                AudioConverter.EstimateOutput(700 * Mb, ".flac", opus) < 200 * Mb);
            Check("an Opus made into WAV is estimated much bigger",
                AudioConverter.EstimateOutput(10 * Mb, ".opus", AudioFormats.ById("wav-16")) >= 100 * Mb);
        }

        private static async Task ResumeTests()
        {
            var dir = NewDir();
            try
            {
                foreach (var name in new[] { "a", "b", "c", "d" }) Tests.WriteTone(Path.Combine(dir, name + ".wav"), 1, 0.4);
                var choice = AudioFormats.ById("opus-64");
                var sources = AudioConverter.Gather(new[] { dir }, false, null, CancellationToken.None);
                var jobs = AudioConverter.Resolve(AudioConverter.Plan(sources, choice),
                    FileOperations.ConflictChoice.Rename, out _);
                // d is being replaced: its target was there when the batch began.
                jobs[3] = jobs[3] with { Replace = true };

                var note = CarryOnTransfers.ForConvert(jobs, dir, choice.Id, deleteOriginals: true);
                var now = DateTime.UtcNow;
                var back = CarryOnTransfers.Decode(CarryOnTransfers.Encode(new[] { note }, now), now);
                Check("a conversion is written down and read back as one job",
                    back.Count == 1 && back[0].What == CarryOnTransfers.Kind.Convert && back[0].Destination == dir);
                if (back.Count != 1) return;

                var (again, id, delete) = CarryOnTransfers.AsConvert(back[0]);
                Equal("with its format", choice.Id, id);
                Check("whether the originals go", delete);
                Equal("every source and target", string.Join("|", jobs.Select(j => j.Source + ">" + j.Target + ">" + j.Replace)),
                    string.Join("|", again.Select(j => j.Source + ">" + j.Target + ">" + j.Replace)));
                var (_, plainId, plainDelete) = CarryOnTransfers.AsConvert(
                    CarryOnTransfers.ForConvert(jobs, dir, choice.Id, deleteOriginals: false));
                Check("and a batch that keeps its originals says so", plainId == choice.Id && !plainDelete);

                // a was converted, b converted and its original deleted, c never
                // started, d's target is there because it is the file being replaced.
                await AudioConverter.RunAsync(new[] { jobs[0] }, choice, null, CancellationToken.None);
                File.Delete(jobs[1].Source);
                File.WriteAllBytes(jobs[3].Target, new byte[10]);

                var (left, converted) = AudioConverter.Remaining(again, null);
                Equal("what is left is the file never started and the one being replaced", "c.wav,d.wav",
                    string.Join(",", left.Select(j => Path.GetFileName(j.Source))));
                Equal("the converted file with its original still there is known as converted", "a.wav",
                    string.Join(",", converted.Select(j => Path.GetFileName(j.Source))));
                Check("sizes are read again", left.All(j => j.Bytes > 0));

                var result = await AudioConverter.RunAsync(left, choice, null, CancellationToken.None, deleteOriginals: false);
                Check("the rest converts", result.Converted == 2 && result.Failures.Count == 0, result.Summary(choice));
                var (after, _) = AudioConverter.Remaining(again.Select(j => j with { Replace = false }).ToList(), null);
                Equal("and then nothing is left", "0", after.Count.ToString());
            }
            finally { Cleanup(dir); }

            var main = Source("MainForm.cs");
            Check("a running conversion can be finished after an update or a restart",
                main.Contains("StartCarryable(CarryOnTransfers.ForConvert(") && main.Contains("FinishConvertAsync(job)"));
        }

        private static string Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            try { return dir == null ? "" : File.ReadAllText(Path.Combine(dir.FullName, "src", name)); }
            catch { return ""; }
        }

        private static void DeleteOriginalTests()
        {
            var opus = AudioFormats.ById("opus-96");
            var all = new ConvertResult { Converted = 3, OriginalsDeleted = 3 };
            Equal("deleted originals are said in the summary",
                "Converted 3 files to Opus, 96 kilobits per second and deleted the originals.", all.Summary(opus));
            var one = new ConvertResult { Converted = 1, OriginalsDeleted = 1 };
            Equal("one file, one original",
                "Converted 1 file to Opus, 96 kilobits per second and deleted the original.", one.Summary(opus));
            var some = new ConvertResult { Converted = 3, OriginalsDeleted = 2 };
            some.OriginalsKept.Add(new ConvertFailure("a.flac", "it is in use"));
            Equal("an original that was kept is named",
                "Converted 3 files to Opus, 96 kilobits per second and deleted 2 of the originals, " +
                "1 original could not be deleted: a.flac, it is in use.", some.Summary(opus));

            var dir = NewDir();
            try
            {
                var source = Path.Combine(dir, "a.wav");
                File.WriteAllBytes(source, new byte[100]);
                var missing = Path.Combine(dir, "a.opus");
                var job = new ConvertJob(source, missing, 100, false);
                Check("an original is kept when the converted file is not there",
                    AudioConverter.RemoveOriginal(job, missing, null) != null && File.Exists(source));
                File.WriteAllBytes(missing, Array.Empty<byte>());
                Check("or is empty", AudioConverter.RemoveOriginal(job, missing, null) != null && File.Exists(source));
                Check("or has the original's own name",
                    AudioConverter.RemoveOriginal(job, source, null) != null && File.Exists(source));
            }
            finally { Cleanup(dir); }

            var run = Source("AudioConvert.cs");
            Check("an original is removed only after its file is converted, and never once cancelled",
                run.IndexOf("ConvertFile(job, choice", StringComparison.Ordinal) <
                run.IndexOf("RemoveOriginal(job, written, drive)", StringComparison.Ordinal) &&
                run.Contains("deleteOriginals && !token.IsCancellationRequested"));
            Check("originals go to the Recycle Bin or the Drive trash, never for good",
                run.Contains("ShellDelete.RecycleWithoutUi(job.Source)") && run.Contains("drive.TrashPath(job.Source") &&
                !run.Contains("DeletePermanently") && !run.Contains("DeleteForever"));
            Check("a Drive upload is confirmed with Google before it counts",
                run.Contains("drive.WhyNotUploaded(path, output, token)"));
            var form = Source("AudioConvertForm.cs");
            Check("the delete box starts unticked every time", form.Contains("Checked = false"));
        }

        // ------------------------------------------------------------ editor

        private static void EditListTests()
        {
            var e = new EditList(1000, 100);
            Check("a fresh edit list is the source unchanged", e.IsUnchanged && e.Length == 1000);

            var cut = e.Cut(200, 300);
            Equal("a cut shortens it", "900", cut.Length.ToString());
            Equal("into two runs of the source", "0-200@0,300-1000@0", Describe(cut));
            Check("and is no longer unchanged", !cut.IsUnchanged);

            var cut2 = cut.Cut(150, 250);       // edited 150-250 = source 150-200 and 300-350
            Equal("a second cut across the join takes from both sides", "0-150@0,350-1000@0", Describe(cut2));

            Equal("marks in either order mean the same", Describe(e.Cut(300, 200)), Describe(cut));
            Check("an empty selection changes nothing", ReferenceEquals(e.Cut(400, 400), e));

            var trim = e.KeepOnly(100, 900);
            Equal("trim keeps only the selection", "100-900@0", Describe(trim));
            Equal("trimming after a cut maps through it", "100-200@0,300-400@0", Describe(cut.KeepOnly(100, 300)));

            var louder = e.ChangeGain(500, 600, 6);
            Equal("a volume change splits out its region", "0-500@0,500-600@6,600-1000@0", Describe(louder));
            Equal("and stacks with an earlier one", "0-500@0,500-550@6,550-600@3,600-1000@-3",
                Describe(louder.ChangeGain(550, 1000, -3)));
            Equal("undoing a change by hand joins the runs back up", "0-1000@0",
                Describe(louder.ChangeGain(500, 600, -6)));
            Equal("the whole file at once", "0-1000@-2", Describe(e.ChangeGain(0, e.Length, -2)));

            var (index, offset) = cut.Locate(250);
            Check("an edited position maps to the right run of the source", index == 1 && offset == 50,
                $"{index}, {offset}");
            Equal("the end is past the last run", $"{cut.Segments.Count}", cut.Locate(cut.Length).Index.ToString());

            Check("a cut clamped to the end is fine", e.Cut(950, 5000).Length == 950);
            Check("cutting everything leaves nothing", e.Cut(0, 1000).Length == 0);
        }

        private static string Describe(EditList list) =>
            string.Join(",", list.Segments.Select(s => $"{s.Start}-{s.End}@{s.GainDb:0.##}"));

        private static void HistoryTests()
        {
            var start = new EditList(1000, 100);
            var history = new EditHistory(start);
            Check("nothing to undo at first", !history.CanUndo && !history.Unsaved);

            history.Apply(start.Cut(0, 100), "cut");
            history.Apply(history.Current.ChangeGain(0, 100, 3), "volume");
            Check("two edits are unsaved", history.Unsaved && history.CanUndo);

            Equal("undo names what it undid", "volume", history.Undo() ?? "(null)");
            Equal("and goes back one step", "900", history.Current.Length.ToString());
            Equal("redo puts it back", "volume", history.Redo() ?? "(null)");
            Check("with the gain in it", history.Current.Segments[0].GainDb > 2);

            history.Undo();
            history.Apply(history.Current.KeepOnly(0, 500), "trim");
            Check("a new edit after an undo clears the redo", !history.CanRedo);

            Equal("undo all the way", "cut", (history.Undo(), history.Undo()).Item2 ?? "(null)");
            Check("back to the start", ReferenceEquals(history.Current, start) && !history.Unsaved);
            Check("and no further", history.Undo() == null);
            Check("a step that changes nothing is not a step", !history.Apply(history.Current, "nothing"));

            history.Redo();
            history.MarkSaved();
            Check("saved is not unsaved", !history.Unsaved);
        }

        private static void SpokenTests()
        {
            Equal("a mark under a minute", "4.2 seconds", EditList.SpokenPrecise(4.23));
            Equal("a mark with minutes", "1 minute 4.2 seconds", EditList.SpokenPrecise(64.2));
            Equal("whole seconds have no point", "2 minutes 0 seconds", EditList.SpokenPrecise(120.01));
            Equal("and hours", "1 hour 2 minutes 3.5 seconds", EditList.SpokenPrecise(3723.5));
            Equal("one second is singular", "1 second", EditList.SpokenPrecise(1));

            Equal("decibels typed with a plus", "3", EditList.ParseDecibels("+3")?.ToString() ?? "(null)");
            Equal("and with a unit", "-6", EditList.ParseDecibels("-6 dB")?.ToString() ?? "(null)");
            Check("nonsense is refused", EditList.ParseDecibels("loud") == null);
            Check("and so is far too much", EditList.ParseDecibels("200") == null);
        }

        private static async Task RenderTests()
        {
            await Task.Yield();
            var dir = NewDir();
            try
            {
                var source = Path.Combine(dir, "speech.wav");
                Tests.WriteTone(source, 4, 0.5);
                var probe = Measure(source);
                long frames = (long)Math.Round(probe.Seconds * probe.Rate);
                Check("the fixture is four seconds", frames == 4 * 48000, frames.ToString());

                // Cut one second out of the middle, quieten the start, keep the rest.
                var edits = new EditList(frames, 48000)
                    .Cut(48000, 96000)
                    .ChangeGain(0, 24000, -6);

                foreach (var id in new[] { "wav-keep", "flac", "opus-96" })
                {
                    var choice = AudioFormats.ById(id);
                    var output = Path.Combine(dir, $"edited{choice.Extension}");
                    var inner = TrackDecoder.Open(source, null, 0, 0, out string why);
                    Check($"{choice.Label}: the source opens for rendering", inner != null, why);
                    if (inner == null) continue;

                    using (var decoder = new EditedDecoder(inner, edits))
                    {
                        Check("the edited decoder reports the edited length", Math.Abs(decoder.Duration - 3) < 1e-9,
                            decoder.Duration.ToString());
                        AudioConverter.Encode(decoder, choice, output, 16, null, null, CancellationToken.None, new List<string>());
                    }

                    var (seconds, peak, _, failed) = Measure(output);
                    double tolerance = choice.Kind == AudioFormatKind.Opus ? 0.05 : 0.0001;
                    Check($"{choice.Label}: a one-second cut of four seconds renders three ({seconds:0.0000})",
                        Math.Abs(seconds - 3) <= tolerance, failed);
                    Check($"{choice.Label}: and it is still the tone (peak {peak:0.###})", peak > 0.4f);
                }

                // Sample-exact: the frame after the cut is the source's frame after the cut.
                var original = ReadAll(source, out _);
                var rendered = ReadAll(Path.Combine(dir, "edited.wav"), out _);
                int join = 48000 * 2;   // edited frame 48000, stereo
                bool exact = rendered.Length == original.Length - 48000 * 2 &&
                             rendered.AsSpan(join, 2000).SequenceEqual(original.AsSpan(96000 * 2, 2000)) &&
                             rendered.AsSpan(30000 * 2, 2000).SequenceEqual(original.AsSpan(30000 * 2, 2000));
                Check("the cut lands on the sample and the rest is untouched", exact);

                float quietPeak = 0;
                for (int i = 0; i < 24000 * 2; i++) quietPeak = Math.Max(quietPeak, Math.Abs(rendered[i]));
                Check($"the quietened half second is 6 dB down (peak {quietPeak:0.###})",
                    Math.Abs(quietPeak - 0.5 * Math.Pow(10, -6 / 20.0)) < 0.01);

                // Seeking on the edited timeline lands after the cut.
                var again = TrackDecoder.Open(source, null, 0, 0, out _)!;
                using (var decoder = new EditedDecoder(again, edits))
                {
                    decoder.SeekTo(1.5);
                    var block = new float[200 * 2];
                    decoder.Read(block, 200);
                    Check("a seek past the cut reads the source from after it",
                        block.AsSpan().SequenceEqual(original.AsSpan((96000 + 24000) * 2, 400)));
                    Check("and the clock says where on the edited timeline",
                        Math.Abs(decoder.Position - (1.5 + 200 / 48000.0)) < 1e-6, decoder.Position.ToString());
                }
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// The editor's playback: the player's own pipeline, handed the edited
        /// decoder. Quiet (2 percent), and the proof is a moving clock on the
        /// edited timeline.
        /// </summary>
        private static async Task PlaybackTests()
        {
            await Task.Yield();
            var dir = NewDir();
            try
            {
                var source = Path.Combine(dir, "quiet.wav");
                Tests.WriteTone(source, 6, 0.02);
                var edits = new EditList(6 * 48000, 48000).Cut(48000, 4 * 48000);    // 3 seconds left

                int built = 0;
                using var playback = new WasapiPlayback
                {
                    DecoderFactory = (int rate, int channels, out string why) =>
                    {
                        Interlocked.Increment(ref built);
                        var inner = TrackDecoder.Open(source, null, rate, channels, out why);
                        return inner == null ? null : new EditedDecoder(inner, edits);
                    },
                };

                bool started = playback.Start(source, preferRaw: false, 0.5);
                Check("the editor's playback starts", started, playback.Diagnostic);
                if (!started) return;

                Check("through the edited decoder", Volatile.Read(ref built) == 1);
                Check($"which says the edited length ({playback.Duration:0.###})", Math.Abs(playback.Duration - 3) < 1e-6);

                double first = playback.Position;
                double moved = first;
                for (int i = 0; i < 30 && moved <= first + 0.2; i++)
                {
                    Thread.Sleep(50);
                    moved = playback.Position;
                }
                Check($"and its clock moves from where it started ({first:0.00} to {moved:0.00})",
                    first >= 0.45 && moved > first + 0.2);

                playback.SeekTo(2.5);
                Thread.Sleep(150);
                Check($"a seek on the edited timeline is followed ({playback.Position:0.00})",
                    playback.Position >= 2.4 && playback.Position <= 3.0);
                playback.Stop();
            }
            finally { Cleanup(dir); }
        }

        /// <summary>The window's decisions, read from its source like MainForm's are.</summary>
        private static void EditorSourceTests()
        {
            var editor = SourceFile("AudioEditorForm.cs") ?? "";
            var main = SourceFile("MainForm.cs") ?? "";

            Check("the editor saves to a new file by default", editor.Contains("(edited)"));
            Check("and replacing the original is its own key that asks first",
                editor.Contains("Keys.Control | Keys.Shift | Keys.S") && editor.Contains("MessageBoxDefaultButton.Button2"));
            Check("the editor has a help key", editor.Contains("Keys.F1"));
            Check("the editor plays at the player's volume, not full level",
                editor.Contains("Gain = PlayerGain()") && editor.Contains("Program.AudioGain"));
            Check("the context menu offers Convert audio and Edit audio",
                main.Contains("Con&vert audio") && main.Contains("Edit &audio"));
            Check("the converter's progress window is never modal",
                !System.Text.RegularExpressions.Regex.IsMatch(main, @"window\.ShowDialog"));
        }

        private static string? SourceFile(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private static float[] ReadAll(string path, out int rate)
        {
            var decoder = TrackDecoder.Open(path, null, 0, 0, out _)!;
            using (decoder)
            {
                rate = decoder.SampleRate;
                var all = new List<float>();
                var block = new float[4096 * decoder.Channels];
                int empties = 0;
                while (empties < 3)
                {
                    int got = decoder.Read(block, 4096);
                    if (got <= 0) { empties++; continue; }
                    all.AddRange(block.AsSpan(0, got * decoder.Channels).ToArray());
                }
                return all.ToArray();
            }
        }

        private static string Hash(string path)
        {
            using var md5 = MD5.Create();
            using var file = File.OpenRead(path);
            return Convert.ToHexString(md5.ComputeHash(file));
        }

        /// <summary>IProgress that calls back on the reporting thread, so a test sees every report.</summary>
        private sealed class SyncProgress : IProgress<TransferProgress>
        {
            private readonly Action<TransferProgress> _action;
            public SyncProgress(Action<TransferProgress> action) => _action = action;
            public void Report(TransferProgress value) => _action(value);
        }
    }
}
