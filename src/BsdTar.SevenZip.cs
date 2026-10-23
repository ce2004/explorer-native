using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// 7z through 7-Zip's own console program (7za.exe, 7-Zip Extra 25.01, LGPL),
    /// carried inside the executable and unpacked once per version.
    ///
    /// Windows' tar makes a 7z on one core whatever it is told: measured on this
    /// 8-core machine, 490 MB of drivers took 180 seconds with threads=1 and 180
    /// with threads=8, and unpacking took 15. 7-Zip did the same 490 MB in 43
    /// seconds (13 percent smaller) and unpacked it in 5 (Conner, 4.2.1: "7z
    /// should be much faster, use all my CPU and RAM and leave enough to work
    /// with"). So it gets every core but one, at most 40 percent of the memory,
    /// and below-normal priority, so the machine stays usable while it runs.
    /// 7-Zip also has no trouble with non-ASCII names, which is where Windows'
    /// tar crashes.
    ///
    /// It goes through the same doors as the tar: the plan's refusals before
    /// anything is made, a staging folder and <see cref="Rehome"/> on the way
    /// out, so conflicts, damage and Replace behave exactly as they did.
    /// </summary>
    public static partial class BsdTar
    {
        internal const string SevenZipVersion = "25.01";

        /// <summary>For the tests: when set, 7z goes through Windows' tar as it used to.</summary>
        internal static bool SevenZipOff { get; set; }

        private static readonly Lazy<string?> _sevenZip = new(UnpackSevenZip);

        /// <summary>The unpacked 7za.exe, or null when this build carries none or it could not be written.</summary>
        internal static string? SevenZipExecutable => SevenZipOff ? null : _sevenZip.Value;

        /// <summary>Whether a 7z is made and unpacked by 7-Zip rather than Windows' tar.</summary>
        /// <remarks>
        /// tar.xz and tar.bz2 as well (4.2.1): 7-Zip writes the tar and a second
        /// 7-Zip compresses it on every core but one. Measured on 490 MB, 8 cores:
        /// tar.bz2 6.6s against Windows' tar's 44s on one core, the same size;
        /// tar.xz 41s against 37s but 12 percent smaller, and names in any
        /// language (Windows' tar crashes on them on some builds). tar.zst stays
        /// with tar: 7-Zip cannot write it, and tar takes under a second.
        /// </remarks>
        internal static bool UsesSevenZip(ArchiveFormat? format) =>
            format?.Id is "7z" or "tar.xz" or "tar.bz2" && SevenZipExecutable != null;

        /// <summary>
        /// Whether an archive is unpacked by 7-Zip: 7z, and tar.xz and tar.bz2,
        /// whose decompression 7-Zip does on several cores where Windows' tar
        /// uses one. Measured on 490 MB of drivers, 8 cores: tar.xz 11.3s with
        /// tar, 2.4s with 7-Zip; tar.bz2 21.0s against 9.4s. Making them stays
        /// with tar, which was the faster of the two at that (37.5s against
        /// 40.8s for xz, 46s against 68s for bz2); tar.zst was even (2.3s, 2.7s).
        /// </summary>
        internal static bool UnpacksWithSevenZip(ArchiveFormat? format) =>
            format?.Id is "7z" or "tar.xz" or "tar.bz2" && SevenZipExecutable != null;

        /// <summary>Every core but one, so the window and everything else keep one for themselves.</summary>
        internal static int SevenZipThreads => Math.Max(1, Environment.ProcessorCount - 1);

        /// <summary>The share of the machine's memory 7-Zip may use, in percent.</summary>
        internal const int SevenZipMemoryPercent = 40;

        /// <summary>
        /// Writes 7za.exe to %LOCALAPPDATA%\ExplorerNative\7-Zip\&lt;version&gt;-&lt;arch&gt;
        /// unless an identical copy is already there. Compared by hash, so a
        /// damaged or swapped file is replaced rather than run.
        /// </summary>
        private static string? UnpackSevenZip()
        {
            try
            {
                using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("7za.exe");
                if (resource == null) return null;
                var bytes = new byte[resource.Length];
                resource.ReadExactly(bytes);
                var want = SHA256.HashData(bytes);

                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ExplorerNative", "7-Zip",
                    $"{SevenZipVersion}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}");
                var exe = Path.Combine(folder, "7za.exe");

                try
                {
                    if (File.Exists(exe) && new FileInfo(exe).Length == bytes.Length &&
                        SHA256.HashData(File.ReadAllBytes(exe)).AsSpan().SequenceEqual(want))
                        return exe;
                }
                catch (IOException) { }

                Directory.CreateDirectory(folder);
                var temp = Path.Combine(folder, $"7za.{Guid.NewGuid():N}.tmp");
                File.WriteAllBytes(temp, bytes);
                try { File.Move(temp, exe, overwrite: true); }
                catch
                {
                    // Another copy of the app running it right now: its file is
                    // the same version, checked again before it is trusted.
                    try { File.Delete(temp); } catch { }
                    if (!(File.Exists(exe) && SHA256.HashData(File.ReadAllBytes(exe)).AsSpan().SequenceEqual(want)))
                        return null;
                }
                return exe;
            }
            catch (Exception ex)
            {
                RoboCopyEngine.Trace($"7-Zip: could not be unpacked: {ex.Message}");
                return null;
            }
        }

        private static string SevenZipLevel(ArchiveLevel level) => level switch
        {
            ArchiveLevel.Fastest => "-mx1",
            ArchiveLevel.Smallest => "-mx9",
            _ => "-mx5",
        };

        // ---------- Creating ----------

        private static int CreateWithSevenZip(
            IReadOnlyList<ArchiveItem> plan,
            List<ArchiveItem> tops,
            string archivePath,
            ArchiveFormat format,
            ArchiveLevel level,
            ArchiveEngine.Reporter reporter,
            List<string> errors,
            CancellationToken token)
        {
            var exe = SevenZipExecutable!;
            var list = Path.Combine(Path.GetTempPath(), $"ExplorerNative-7z-{Guid.NewGuid():N}.txt");
            try
            {
                // A list file rather than arguments: no command-line limit, and
                // names go across as UTF-8, whatever the code page.
                File.WriteAllLines(list, tops.Select(t => Path.GetFullPath(t.Source)), new UTF8Encoding(false));

                long total = plan.Sum(p => p.Size);
                long charged = 0;
                int counted = 0;
                var gate = new object();
                void Progress(long percent, int files, string? name)
                {
                    lock (gate)
                    {
                        if (name != null) reporter.Starting(name);
                        long due = total * percent / 100;
                        if (due > charged) { reporter.Advance(due - charged); charged = due; }
                        while (files > counted) { reporter.Finished(0); counted++; }
                    }
                }

                var start = SevenZipStart(exe);
                Feeder? tar = null;
                if (format.Id == "7z")
                {
                    foreach (var a in new[]
                             {
                                 "a", "-t7z", SevenZipLevel(level), $"-mmt{SevenZipThreads}",
                                 $"-mmemuse=p{SevenZipMemoryPercent}", "-snl", "-y",
                                 "-bso0", "-bse1", "-bsp1", "-sccUTF-8", "-scsUTF-8",
                             })
                        start.ArgumentList.Add(a);
                    start.ArgumentList.Add(Path.GetFullPath(archivePath));
                    start.ArgumentList.Add("@" + list);
                }
                else
                {
                    // One 7-Zip writes the tar to its standard output (its
                    // progress and warnings on standard error); the second
                    // compresses what it is fed into the archive.
                    tar = new Feeder(exe, new[]
                    {
                        "a", "-ttar", "-so", "-an", "-snl", "-y", "-bse2", "-bsp2", "-sccUTF-8", "-scsUTF-8", "@" + list,
                    }, Progress, token);
                    foreach (var a in new[]
                             {
                                 "a", "-si", format.Id == "tar.xz" ? "-txz" : "-tbzip2", SevenZipLevel(level),
                                 $"-mmt{SevenZipThreads}", $"-mmemuse=p{SevenZipMemoryPercent}", "-y",
                                 "-bso0", "-bse1", "-bsp0", "-sccUTF-8",
                             })
                        start.ArgumentList.Add(a);
                    start.ArgumentList.Add(Path.GetFullPath(archivePath));
                }

                RoboCopyEngine.Trace($"7-Zip create: {string.Join(" ", start.ArgumentList)}");

                int exit;
                List<string> lines;
                try
                {
                    (exit, lines) = RunSevenZip(start, token, (percent, files, name) =>
                    {
                        if (tar == null) Progress(percent, files, name);
                    }, feed: tar == null ? null : tar.CopyTo);
                }
                finally { tar?.Finish(); }
                if (tar != null)
                {
                    lines.AddRange(tar.Lines);
                    exit = Math.Max(exit, tar.ExitCode);
                }

                // Files and folders it could not open: "path : reason" under its
                // warnings summaries. A folder takes everything in it along.
                var failed = FileWarnings(lines);
                var sources = plan.Select(p => (Item: p, Full: Path.GetFullPath(p.Source))).ToList();
                var left = new HashSet<ArchiveItem>();
                foreach (var (path, reason) in failed)
                {
                    var under = sources.Where(s => string.Equals(s.Full, path, StringComparison.OrdinalIgnoreCase) ||
                                                   s.Full.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                                       .Select(s => s.Item).ToList();
                    foreach (var item in under) left.Add(item);
                    var named = under.FirstOrDefault(i => string.Equals(Path.GetFullPath(i.Source), path, StringComparison.OrdinalIgnoreCase));
                    errors.Add($"{named?.Name ?? path}: it could not be opened ({reason.TrimEnd('.')}), so it was not added");
                }

                if (exit is 0 or 1) return Math.Max(0, plan.Count - left.Count);
                throw new IOException(SevenZipProblem(exit, lines, extracting: false));
            }
            finally
            {
                try { File.Delete(list); } catch { }
            }
        }

        // ---------- Extracting ----------

        private static (int Done, ConflictOutcomes Conflicts) ExtractWithSevenZip(
            string archivePath,
            string destination,
            PasteConflictPolicy policy,
            Func<IReadOnlyList<string>, FileOperations.ConflictChoice>? ask,
            ArchiveEngine.Reporter reporter,
            List<string> errors,
            CancellationToken token,
            Action? stoppedShort,
            bool compressedTar = false)
        {
            var exe = SevenZipExecutable!;
            Directory.CreateDirectory(destination);
            var staging = Path.Combine(destination, ".extracting-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(staging);

            try
            {
                var start = SevenZipStart(exe);
                // -aos keeps the first of two entries with one name, as the other
                // readers do. A password is given so a protected archive fails
                // with "Wrong password" instead of waiting for one to be typed.
                // A tar.xz or tar.bz2 is two steps: one 7-Zip decompresses to
                // its standard output, and a second reads the tar from it.
                foreach (var a in compressedTar
                             ? new[]
                             {
                                 "x", "-si", "-ttar", "-y", "-aos", "-bso1", "-bse1", "-bsp0", "-bb1",
                                 "-sccUTF-8", "-o" + Path.GetFullPath(staging),
                             }
                             : new[]
                             {
                                 "x", "-y", "-aos", "-pExplorerNative", $"-mmt{SevenZipThreads}",
                                 $"-mmemuse=p{SevenZipMemoryPercent}", "-bso1", "-bse1", "-bsp1", "-bb1",
                                 "-sccUTF-8", "-o" + Path.GetFullPath(staging), "--",
                             })
                    start.ArgumentList.Add(a);
                if (!compressedTar) start.ArgumentList.Add(Path.GetFullPath(archivePath));

                RoboCopyEngine.Trace($"7-Zip extract: {string.Join(" ", start.ArgumentList)}");

                long archiveBytes = 0;
                try { archiveBytes = new FileInfo(archivePath).Length; } catch { }
                long percentNow = 0;
                reporter.Estimate(archiveBytes, () => Math.Max(1, archiveBytes) * Interlocked.Read(ref percentNow) / 100);

                bool stalled = false;
                int exit;
                List<string> lines;
                Feeder? feeding = compressedTar
                    ? new Feeder(exe, new[]
                    {
                        "x", "-so", "-y", $"-mmt{SevenZipThreads}", $"-mmemuse=p{SevenZipMemoryPercent}",
                        "-bse2", "-bsp2", "-sccUTF-8", "--", Path.GetFullPath(archivePath),
                    }, (p, _, _) => Interlocked.Exchange(ref percentNow, p), token)
                    : null;
                try
                {
                    (exit, lines) = RunSevenZip(start, token, (percent, _, name) =>
                    {
                        if (feeding == null) Interlocked.Exchange(ref percentNow, percent);
                        if (name != null) reporter.Starting(name);
                    }, onLine: line =>
                    {
                        if (line.StartsWith("- ", StringComparison.Ordinal)) reporter.Finished(0);
                    }, feed: feeding == null ? null : feeding.CopyTo);
                }
                catch (IOException ex) when (!token.IsCancellationRequested)
                {
                    errors.Add(ex.Message);
                    (exit, lines, stalled) = (2, new List<string>(), true);
                }
                finally { feeding?.Finish(); }

                // The decompressing half's complaints count with the tar's:
                // a damaged .xz is said by the first, not the second.
                if (feeding != null)
                {
                    lines.AddRange(feeding.Lines);
                    exit = Math.Max(exit, feeding.ExitCode);
                }

                token.ThrowIfCancellationRequested();

                // "ERROR: Data Error : dir\f.bin", "ERROR: CRC Failed : f.bin",
                // "ERROR: Data Error in encrypted file. Wrong password? : f.bin".
                var distrusted = new List<string>();
                bool damaged = false, locked = false;
                foreach (var line in lines)
                {
                    if (!line.StartsWith("ERROR: ", StringComparison.Ordinal)) continue;
                    var body = line[7..];
                    if (body.Contains("Wrong password", StringComparison.OrdinalIgnoreCase)) locked = true;
                    else if (LooksDamagedTo7Zip(body)) damaged = true;
                    int at = body.LastIndexOf(" : ", StringComparison.Ordinal);
                    if (at > 0)
                    {
                        var name = body[(at + 3)..].Trim();
                        if (name.StartsWith(staging, StringComparison.OrdinalIgnoreCase))
                            name = Path.GetRelativePath(staging, name);
                        distrusted.Add(ExtractionRules.Normalise(name.Replace('\\', '/')));
                    }
                }
                damaged |= lines.Any(LooksDamagedTo7Zip);

                // Exit 2 with nothing named: it could not go on (a damaged header,
                // a truncated file), so what came out is not trusted to be whole.
                bool unfinished = stalled || (exit >= 2 && distrusted.Count == 0);
                if (locked)
                    errors.Add("This archive is protected with a password, which Explorer Native can't open yet");
                else if (exit >= 2 && !damaged && !stalled)
                    errors.Add(SevenZipProblem(exit, lines, extracting: true));

                var (done, conflicts, _) = Rehome(staging, destination, policy, ask, reporter, errors, token,
                    distrusted.Count > 0 || unfinished ? distrusted : null, unfinished,
                    _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

                foreach (var name in distrusted)
                    if (!locked) errors.Add($"{name}: the archive is damaged at this point");
                if (damaged) errors.Add(ArchiveEngine.DamagedMessage(done));
                if (unfinished) stoppedShort?.Invoke();

                return (done, conflicts);
            }
            finally
            {
                RemoveStaging(staging, errors);
            }
        }

        private static bool LooksDamagedTo7Zip(string message) =>
            message.Contains("Data Error", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("CRC Failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Unexpected end", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Headers Error", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Cannot open the file as", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Unconfirmed start", StringComparison.OrdinalIgnoreCase);

        /// <summary>"path : reason" lines from 7-Zip's "WARNINGS for files:" summary.</summary>
        internal static List<(string Path, string Reason)> FileWarnings(IEnumerable<string> lines)
        {
            var found = new List<(string, string)>();
            bool inside = false;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                // "WARNINGS for files:" (reading) and "Scan WARNINGS for files and folders:" (walking).
                if (line.EndsWith("WARNINGS for files:", StringComparison.Ordinal) ||
                    line.EndsWith("WARNINGS for files and folders:", StringComparison.Ordinal)) { inside = true; continue; }
                if (!inside || line.Length == 0) continue;
                if (line.StartsWith("----", StringComparison.Ordinal)) { inside = false; continue; }
                int at = line.IndexOf(" : ", StringComparison.Ordinal);
                if (at > 0) found.Add((line[..at].Trim(), line[(at + 3)..].Trim()));
            }
            return found;
        }

        /// <summary>One sentence for a run that failed outright.</summary>
        internal static string SevenZipProblem(int exit, IReadOnlyList<string> lines, bool extracting)
        {
            if (exit == 8) return "There wasn't enough memory for 7-Zip. Close some programs and try again";
            var said = lines.FirstOrDefault(l => l.StartsWith("ERROR: ", StringComparison.Ordinal) &&
                                                  !l.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                       ?? lines.FirstOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal) ||
                                                    l.Contains("cannot", StringComparison.OrdinalIgnoreCase));
            if (said != null && said.Contains("not enough space", StringComparison.OrdinalIgnoreCase))
                return "There isn't enough space on the disk";
            if (said != null && said.StartsWith("ERROR: ", StringComparison.Ordinal)) said = said[7..];
            return string.IsNullOrWhiteSpace(said)
                ? $"7-Zip stopped with code {exit} while {(extracting ? "extracting" : "compressing")}"
                : said.Trim().TrimEnd('.');
        }

        /// <summary>
        /// The first half of a tar.xz or tar.bz2: 7-Zip decompressing the file
        /// to its standard output on several cores, copied into the second
        /// 7-Zip that unpacks the tar. Its progress (how far through the
        /// archive) and its complaints arrive on standard error.
        /// </summary>
        private sealed class Feeder
        {
            private readonly Process _process;
            private readonly Thread _errors;
            private readonly CancellationTokenRegistration _kill;
            public readonly List<string> Lines = new();
            public int ExitCode { get; private set; }

            public Feeder(string exe, IEnumerable<string> arguments, Action<long, int, string?> progress,
                CancellationToken token)
            {
                var start = SevenZipStart(exe);
                start.RedirectStandardError = true;
                start.StandardErrorEncoding = new UTF8Encoding(false);
                foreach (var a in arguments) start.ArgumentList.Add(a);
                RoboCopyEngine.Trace($"7-Zip feeding: {string.Join(" ", start.ArgumentList)}");

                _process = new Process { StartInfo = start };
                StartQuietly(_process);
                KeepWithUs(_process);
                try { _process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                try { _process.StandardInput.Close(); } catch { }
                _kill = token.Register(Stop);

                _errors = new Thread(() =>
                {
                    var buffer = new char[4096];
                    var current = new StringBuilder();
                    void Flush()
                    {
                        var text = current.ToString().Trim();
                        current.Clear();
                        if (text.Length == 0) return;
                        var m = SevenZipProgress.Match(text);
                        if (m.Success)
                        {
                            try
                            {
                                progress(long.Parse(m.Groups[1].Value),
                                    m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0,
                                    m.Groups[3].Success ? m.Groups[3].Value.Trim() : null);
                            }
                            catch { }
                            return;
                        }
                        lock (Lines) if (Lines.Count < 2000) Lines.Add(text);
                    }
                    try
                    {
                        int n;
                        while ((n = _process.StandardError.Read(buffer, 0, buffer.Length)) > 0)
                            for (int i = 0; i < n; i++)
                            {
                                char c = buffer[i];
                                if (c is '\b' or '\r' or '\n') Flush();
                                else current.Append(c);
                            }
                    }
                    catch { }
                    Flush();
                }) { IsBackground = true, Name = "7-Zip decompress messages" };
                _errors.Start();
            }

            /// <summary>Copies the decompressed tar into <paramref name="into"/> until the end.</summary>
            public void CopyTo(Stream into) => _process.StandardOutput.BaseStream.CopyTo(into, 1 << 20);

            private void Stop()
            {
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            }

            /// <summary>Waits for it to end (killed if the tar half stopped first) and keeps its exit code.</summary>
            public void Finish()
            {
                try
                {
                    // Nobody reads its output any more: a tar half that stopped
                    // early would otherwise leave it blocked writing for ever.
                    try { _process.StandardOutput.Close(); } catch { }
                    if (!_process.WaitForExit(10_000)) { Stop(); _process.WaitForExit(5000); }
                    ExitCode = _process.HasExited ? _process.ExitCode : 2;
                }
                catch { ExitCode = 2; }
                _errors.Join(5000);
                _kill.Dispose();
                _process.Dispose();
            }
        }

        // ---------- Running it ----------

        private static ProcessStartInfo SevenZipStart(string exe) => new()
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };

        private static readonly Regex SevenZipProgress =
            new(@"^\s*(\d{1,3})%(?:\s+(\d+))?(?:\s+[+\-U=]\s+(.*))?$", RegexOptions.Compiled);

        /// <summary>
        /// Runs 7za and gives back its exit code and every line it wrote.
        /// Progress comes as "12% 40 + name" rewritten in place with
        /// backspaces, so standard output is read raw and split on those as
        /// well as on line ends. Stopped after five minutes without using any
        /// processor time, as the tar is.
        /// </summary>
        private static (int Exit, List<string> Lines) RunSevenZip(ProcessStartInfo start, CancellationToken token,
            Action<long, int, string?> onProgress, Action<string>? onLine = null, Action<Stream>? feed = null)
        {
            var lines = new List<string>();
            using var process = new Process { StartInfo = start };
            StartQuietly(process);
            KeepWithUs(process);
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            Thread? feeder = null;
            if (feed == null)
            {
                try { process.StandardInput.Close(); } catch { }
            }
            else
            {
                feeder = new Thread(() =>
                {
                    try { feed(process.StandardInput.BaseStream); } catch { }
                    try { process.StandardInput.Close(); } catch { }
                }) { IsBackground = true, Name = "7-Zip input" };
                feeder.Start();
            }

            var reader = new Thread(() =>
            {
                var buffer = new char[4096];
                var current = new StringBuilder();
                void Flush()
                {
                    var text = current.ToString().Trim();
                    current.Clear();
                    if (text.Length == 0) return;
                    var m = SevenZipProgress.Match(text);
                    if (m.Success)
                    {
                        try
                        {
                            onProgress(long.Parse(m.Groups[1].Value),
                                m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0,
                                m.Groups[3].Success ? m.Groups[3].Value.Trim() : null);
                        }
                        catch { }
                        return;
                    }
                    lock (lines) if (lines.Count < 20_000) lines.Add(text);
                    try { onLine?.Invoke(text); } catch { }
                }

                try
                {
                    int n;
                    while ((n = process.StandardOutput.Read(buffer, 0, buffer.Length)) > 0)
                        for (int i = 0; i < n; i++)
                        {
                            char c = buffer[i];
                            if (c is '\b' or '\r' or '\n') Flush();
                            else current.Append(c);
                        }
                }
                catch { }
                Flush();
            }) { IsBackground = true, Name = "7-Zip output" };
            reader.Start();

            using (token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }))
            {
                var lastUsed = TimeSpan.Zero;
                var quietSince = DateTime.UtcNow;
                while (!process.WaitForExit(250))
                {
                    if (token.IsCancellationRequested) break;
                    TimeSpan used;
                    try { used = process.TotalProcessorTime; } catch { break; }
                    if (used > lastUsed) { lastUsed = used; quietSince = DateTime.UtcNow; continue; }
                    if (DateTime.UtcNow - quietSince < StallLimit) continue;
                    try { process.Kill(entireProcessTree: true); } catch { }
                    throw new IOException($"7-Zip stopped making progress for {StallLimit.TotalMinutes:0} minutes and was stopped.");
                }
                process.WaitForExit();
            }
            reader.Join(5000);
            feeder?.Join(5000);
            token.ThrowIfCancellationRequested();

            List<string> said;
            lock (lines) said = new List<string>(lines);
            if (process.ExitCode != 0)
                RoboCopyEngine.Trace($"7-Zip: exit {process.ExitCode}: {string.Join(" | ", said.Take(20))}");
            return (process.ExitCode, said);
        }
    }
}
