using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// 7z, xz, bzip2, zstandard, rar, cab and the rest — through the bsdtar
    /// Windows already ships.
    ///
    /// <c>%SystemRoot%\System32\tar.exe</c> has been in Windows since 1803. It is
    /// bsdtar over libarchive — 3.8.4 on this machine — built with zlib, liblzma,
    /// bz2lib and libzstd, which between them are every compressor this
    /// application does not have and is not going to write. Driving it is the
    /// same bargain RoboCopyEngine makes with robocopy.exe: the operating system
    /// ships a good implementation of the hard part, and the alternative is a
    /// worse one of our own carrying a decade of correctness risk.
    ///
    /// The shape of this file is decided entirely by one thing, and it was found
    /// by measurement rather than reasoned out. **libarchive on Windows loses
    /// non-ASCII names wherever it has to render one as text.** A file called
    /// "日本語 🎵.txt" survives some routes through it and not others, and the
    /// difference is whether the name ever passes through the process locale —
    /// which on this machine, and on nearly every Windows machine, is not UTF-8.
    /// Measured, all six of these, each one either the whole name or "???":
    ///
    /// | route | name survives |
    /// | --- | --- |
    /// | name as a command-line argument, bsdtar walks the disk itself | **yes** |
    /// | the same name on standard input via `--null -T -` | no |
    /// | our own tar piped in with `@-`, repacked to .7z or .tar.xz | no |
    /// | `tar -c -f - @thing.tar.xz`, converting an archive to a tar | no |
    /// | `tar -tf thing.7z`, listing to standard output | no |
    /// | `tar -x -f thing.7z -C somewhere`, extracting to disk | **yes** |
    ///
    /// The two that work are the two where the name goes between the archive and
    /// the Windows API as wide characters and is never a string in between. So
    /// those are the only two routes used:
    ///
    /// - **To create**, the names are command-line arguments and bsdtar
    ///   enumerates the folders itself.
    /// - **To extract**, bsdtar writes the files to disk itself, into a staging
    ///   folder inside the destination — and this application then moves them
    ///   into place under its own conflict rules, which is a rename per file on
    ///   the same volume and costs nothing.
    ///
    /// The staging folder is not a workaround for the encoding; it is what keeps
    /// the rest of the application's behaviour. bsdtar overwrites whatever is in
    /// its way without asking, and Skip, Rename and Fill gaps are answers this
    /// file manager gives everywhere else. Staging means the question is asked
    /// once, with every name known and correct, before anything already on disk
    /// is touched.
    ///
    /// The one thing that is *not* attempted anywhere here is reading a name out
    /// of bsdtar's output as text. The listing below reads sizes and ignores the
    /// names in the same lines, deliberately, and says so: a listing that was
    /// right for ASCII and quietly wrong for everybody else would have been wrong
    /// in the collision check, and so would have extracted over a file it had
    /// decided was not there.
    /// </summary>
    public static class BsdTar
    {
        /// <summary>System32's tar, by full path: a bare "tar.exe" is resolved against PATH.</summary>
        public static string SystemExecutable { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");

        private static readonly Lazy<string> _executable = new(ChooseExecutable);

        /// <summary>
        /// The tar to run: System32's, unless its libarchive cannot handle a
        /// non-ASCII name, in which case a private copy beside an earlier
        /// libarchive that can.
        ///
        /// Windows' update of 2026-09-27 moved System32\archiveint.dll to
        /// libarchive 3.8.8 (10.0.28000.3086), and that build crashes with an
        /// access violation, or reports "unreadable filename", on any name that is
        /// not ASCII — create, list and extract alike, whatever the locale or
        /// hdrcharset. The 3.8.4 build it replaced is still in WinSxS, and a copy
        /// of tar.exe with that DLL beside it (the application directory is
        /// searched before System32) handles every route this file uses.
        ///
        /// So the choice is measured, not assumed: one create and one extract of
        /// a non-ASCII name, System32 first, then each WinSxS build newest first.
        /// It is remembered against System32's DLL version, so the next Windows
        /// update is measured again, and a fixed System32 wins on its own.
        /// </summary>
        public static string Executable => _executable.Value;

        public static bool Available => File.Exists(Executable);

        private static string PrivateRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExplorerNative", "tar");

        private static string ChooseExecutable()
        {
            try
            {
                if (!File.Exists(SystemExecutable)) return SystemExecutable;
                var systemDll = Path.Combine(Path.GetDirectoryName(SystemExecutable)!, "archiveint.dll");
                if (!File.Exists(systemDll)) return SystemExecutable;
                var key = FileVersionInfo.GetVersionInfo(systemDll).FileVersion + "|" + new FileInfo(SystemExecutable).Length;

                var root = PrivateRoot;
                var choiceFile = Path.Combine(root, "choice.txt");
                try
                {
                    var saved = File.ReadAllLines(choiceFile);
                    if (saved.Length == 2 && saved[0] == key && (saved[1] == "system" || File.Exists(saved[1])))
                        return saved[1] == "system" ? SystemExecutable : saved[1];
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                string chosen = "system";
                if (!HandlesNonAscii(SystemExecutable))
                {
                    foreach (var dll in WinSxSCandidates(systemDll))
                    {
                        var dir = Path.Combine(root, FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "unknown");
                        Directory.CreateDirectory(dir);
                        var exe = Path.Combine(dir, "tar.exe");
                        File.Copy(SystemExecutable, exe, overwrite: true);
                        File.Copy(dll, Path.Combine(dir, "archiveint.dll"), overwrite: true);
                        if (HandlesNonAscii(exe)) { chosen = exe; break; }
                    }
                }
                Directory.CreateDirectory(root);
                File.WriteAllLines(choiceFile, new[] { key, chosen });
                return chosen == "system" ? SystemExecutable : chosen;
            }
            catch (Exception)
            {
                return SystemExecutable;
            }
        }

        /// <summary>
        /// Every archiveint.dll in WinSxS for this architecture other than
        /// System32's own, newest first.
        /// </summary>
        private static IEnumerable<string> WinSxSCandidates(string systemDll)
        {
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "amd64",
                Architecture.X86 => "x86",
                _ => null,
            };
            if (arch is null) yield break;
            var sxs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS");
            var systemVersion = FileVersionInfo.GetVersionInfo(systemDll).FileVersion;
            var found = new List<(Version version, string path)>();
            foreach (var dir in Directory.EnumerateDirectories(sxs, arch + "_libarchive-internal_*"))
            {
                var dll = Path.Combine(dir, "archiveint.dll");
                if (!File.Exists(dll)) continue;
                var text = FileVersionInfo.GetVersionInfo(dll).FileVersion;
                if (text == systemVersion) continue;
                if (Version.TryParse(text?.Split(' ')[0], out var v)) found.Add((v, dll));
            }
            foreach (var (_, path) in found.OrderByDescending(f => f.version)) yield return path;
        }

        /// <summary>
        /// Whether this tar can create a .7z from a non-ASCII name and extract it
        /// back under the same name.
        /// </summary>
        private static bool HandlesNonAscii(string exe)
        {
            var work = Path.Combine(Path.GetTempPath(), "ExplorerNative-tarprobe-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var source = Path.Combine(work, "in");
                var output = Path.Combine(work, "out");
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(output);
                const string name = "日本語 é.txt";
                File.WriteAllText(Path.Combine(source, name), "probe");
                var archive = Path.Combine(work, "probe.7z");
                if (!Probe(exe, "--format", "7zip", "-c", "-f", archive, "-C", source, name)) return false;
                if (!Probe(exe, "-x", "-f", archive, "-C", output)) return false;
                return File.Exists(Path.Combine(output, name));
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch (Exception) { }
            }
        }

        private static bool Probe(string exe, params string[] args)
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(); } catch (Exception) { }
                return false;
            }
            return p.ExitCode == 0;
        }

        /// <summary>
        /// How long a command line may get before this refuses to build one.
        ///
        /// Windows stops at 32,767 characters. Only the *selected* names go on it
        /// — bsdtar walks the folders itself — so this is thousands of items
        /// before it matters, and the formats that do not need a command line at
        /// all are the ones the chooser offers first.
        /// </summary>
        private const int CommandLineBudget = 30_000;

        private static void RequireAvailable()
        {
            if (!Available)
                throw new NotSupportedException(
                    $"This format needs the tar that Windows ships, and {Executable} is not there.");
        }

        // ---------- Creating ----------

        public static int Create(
            IReadOnlyList<ArchiveItem> plan,
            string archivePath,
            ArchiveFormat format,
            ArchiveLevel level,
            int threads,
            ArchiveEngine.Reporter reporter,
            CancellationToken token)
        {
            RequireAvailable();

            // Only the items that were actually selected go on the command line;
            // everything under a selected folder is bsdtar's business.
            var tops = plan.Where(p => !p.Name.Contains('/')).ToList();
            if (tops.Count == 0) return 0;

            var start = NewStartInfo();
            start.ArgumentList.Add("-a");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add(archivePath);

            // One line per entry on standard error, which is the only progress
            // there is. The names in it are the mangled ones; they are counted,
            // and matched against the plan where they happen to be ASCII, which
            // is what nearly every archive is.
            start.ArgumentList.Add("-v");

            // The owner and group of a Windows file are not a unix user name, and
            // libarchive says so once per entry if left to work it out.
            start.ArgumentList.Add("--uname");
            start.ArgumentList.Add("");
            start.ArgumentList.Add("--gname");
            start.ArgumentList.Add("");

            if (Options(format, level, threads) is { } options)
            {
                start.ArgumentList.Add("--options");
                start.ArgumentList.Add(options);
            }

            int budget = CommandLineBudget - archivePath.Length - 120;

            // bsdtar names entries after what is on disk, so two chosen items with
            // one name — a selection across two folders — become two entries with
            // one name, and extracting keeps only one. Zip and .tar.gz rename the
            // second; this cannot, so it says so rather than making that archive.
            var twice = tops
                .GroupBy(p => Path.GetFileName(p.Source), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (twice != null)
                throw new NotSupportedException(
                    $"Two of the chosen items are called {twice.Key}, and a {format.Extension} " +
                    "would hold both under that one name. Zip and .tar.gz keep both.");

            foreach (var group in tops.GroupBy(
                         p => Path.GetDirectoryName(p.Source) ?? "", StringComparer.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add("-C");
                start.ArgumentList.Add(group.Key);
                budget -= group.Key.Length + 6;

                foreach (var item in group)
                {
                    // The name **on disk**, not the plan's. Two sources that
                    // share a leaf name — one selection spanning two folders,
                    // which the second pane makes easy — are deduplicated by the
                    // plan into "x (2).txt", and bsdtar walks the disk itself: it
                    // was handed a name no file has, said it could not stat it,
                    // exited non-zero, and the whole compress came back as an
                    // error with nothing made. The zip and tar engines open
                    // item.Source and never saw this.
                    var onDisk = Path.GetFileName(item.Source);
                    if (onDisk.Length == 0) onDisk = item.Name;

                    // A name bsdtar would read as something else: "@x" means
                    // "the entries of archive x", and "-x" an option. Given as
                    // "./x" it is a file — Synology's "@eaDir" folders failed the
                    // whole compress before this.
                    if (onDisk[0] is '@' or '-') onDisk = "./" + onDisk;

                    budget -= onDisk.Length + 3;
                    if (budget <= 0)
                        throw new NotSupportedException(
                            $"Too many items were chosen for a {format.Extension}. " +
                            "Zip and .tar.gz have no such limit.");

                    start.ArgumentList.Add(onDisk);
                }
            }

            // Each entry is charged the average rather than its own size, because
            // the name on the line it arrived with cannot be trusted to identify
            // it. The figures are honest at both ends and smooth in between,
            // which is all a progress window is for.
            long totalBytes = plan.Sum(p => p.Size);
            long perItem = totalBytes / Math.Max(1, plan.Count);

            var byName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in plan) byName[item.Name] = item.Size;

            int counted = 0;

            RoboCopyEngine.Trace($"bsdtar create: {string.Join(" ", start.ArgumentList)}");

            using var process = Start(start, (line, _) =>
            {
                if (!line.StartsWith("a ", StringComparison.Ordinal)) return;

                var name = line[2..].Replace('\\', '/').TrimStart('.', '/');
                reporter.Starting(name);
                reporter.Finished(byName.TryGetValue(name, out var size) ? size : perItem);
                Interlocked.Increment(ref counted);
            });

            using var kill = token.Register(() => Stop(process));

            Finish(process, token);
            return counted;
        }

        // ---------- How big it is ----------

        /// <summary>
        /// The number of entries and the number of bytes inside an archive.
        ///
        /// Read from `tar -tvf`, whose lines look like a directory listing:
        /// permissions, links, owner, group, size, date, name. The size is taken
        /// and **the name is thrown away**, which is the whole point — see the
        /// table at the top of this file. Nothing that decides where a file goes
        /// may come from here.
        /// </summary>
        public static (int Items, long Bytes) Totals(string archivePath, CancellationToken token)
        {
            RequireAvailable();

            var start = NewStartInfo();
            start.ArgumentList.Add("-tvf");
            start.ArgumentList.Add(archivePath);
            start.RedirectStandardOutput = true;

            int items = 0;
            long bytes = 0;

            using var process = Start(start, null);
            using var kill = token.Register(() => Stop(process));

            string? line;
            while ((line = process.Process.StandardOutput.ReadLine()) != null)
            {
                token.ThrowIfCancellationRequested();

                var match = ListingLine.Match(line);
                if (!match.Success) continue;

                items++;

                // A leading 'd' is a directory and its size field is zero anyway;
                // the check is there so a format that reports something else for
                // one cannot inflate the total.
                if (line.Length > 0 && line[0] == 'd') continue;

                if (long.TryParse(match.Groups["size"].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var size))
                    bytes += size;
            }

            // A listing that stops part way — a truncated archive — still says
            // what it found. Thrown, it ended the extraction before a single good
            // file was kept, which is the case the staging exists for.
            try { Finish(process, token); }
            catch (IOException) when (!token.IsCancellationRequested) { }
            return (items, bytes);
        }

        /// <summary>
        /// The shape of one `tar -tvf` line, up to but not including the name.
        ///
        /// Anchored on the permission block at the front and the size in the
        /// middle, and it stops before the name on purpose: the name is the part
        /// that cannot be trusted, and a pattern that did not capture it cannot
        /// have it read out of it by mistake later.
        /// </summary>
        private static readonly Regex ListingLine = new(
            @"^[-dlbcps][rwxsStT-]{9}\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // ---------- Extracting ----------

        /// <summary>
        /// Extracts through a staging folder, then moves what came out into place
        /// under this application's own conflict rules.
        ///
        /// The staging folder lives *inside* the destination, which is not
        /// arbitrary: a move is a rename when both ends are on the same volume and
        /// a copy when they are not, and the difference for a forty gigabyte
        /// archive is a second against ten minutes and forty gigabytes of writes
        /// that did not need to happen.
        ///
        /// The conflict question is asked after the decompression rather than
        /// before it, which is the one way this differs from the zip and tar
        /// paths. It reads as backwards and is better: every name is known and
        /// correct by then, nothing already on disk has been touched, and
        /// answering Cancel costs the decompression rather than a folder that is
        /// neither the old contents nor the new.
        /// </summary>
        public static (int Done, ConflictOutcomes Conflicts) Extract(
            string archivePath,
            string destination,
            PasteConflictPolicy policy,
            Func<IReadOnlyList<string>, FileOperations.ConflictChoice>? ask,
            ArchiveEngine.Reporter reporter,
            List<string> errors,
            CancellationToken token)
        {
            RequireAvailable();

            Directory.CreateDirectory(destination);

            var staging = Path.Combine(destination, ".extracting-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(staging);

            try
            {
                var (items, bytes) = Totals(archivePath, token);
                reporter.SetTotals(bytes, items);
                long perItem = bytes / Math.Max(1, items);

                var start = NewStartInfo();
                start.ArgumentList.Add("-x");
                start.ArgumentList.Add("-v");
                start.ArgumentList.Add("-f");
                start.ArgumentList.Add(archivePath);
                start.ArgumentList.Add("-C");
                start.ArgumentList.Add(staging);

                RoboCopyEngine.Trace($"bsdtar extract: {string.Join(" ", start.ArgumentList)}");

                // The entry bsdtar named last, which is the one it was writing if
                // it stopped part way.
                string? lastNamed = null;
                var failedEntries = new List<string>();
                bool incomplete = false;

                using (var process = Start(start, (line, failed) =>
                       {
                           if (!line.StartsWith("x ", StringComparison.Ordinal)) return;
                           lastNamed = line[2..].Trim();

                           // The entry bsdtar complained about, which is the one
                           // not to trust — not whichever it happened to name last.
                           if (failed) lock (failedEntries) failedEntries.Add(lastNamed);
                           reporter.Finished(perItem);
                       }))
                {
                    using var kill = token.Register(() => Stop(process));

                    // One entry it could not write — a symbolic link without the
                    // privilege to make one, a name Windows refuses — ends the run
                    // with a failure code after everything else has been
                    // extracted. That used to throw the whole extraction away,
                    // good files included; now the rest is kept and the complaint
                    // is reported alongside it.
                    try { Finish(process, token); }
                    catch (IOException ex) when (!token.IsCancellationRequested)
                    {
                        errors.Add(ex.Message);
                        incomplete = true;
                    }
                }

                token.ThrowIfCancellationRequested();

                // Every entry it complained about, and the one it named last:
                // a run that failed later — a truncated archive, the stall timer
                // — was part way through that one, whatever came before.
                List<string>? distrusted = null;
                if (incomplete)
                {
                    lock (failedEntries) distrusted = new List<string>(failedEntries);
                    if (lastNamed != null) distrusted.Add(lastNamed);
                }

                return Rehome(staging, destination, policy, ask, reporter, errors, token, distrusted);
            }
            finally
            {
                RemoveStaging(staging, errors);
            }
        }

        /// <summary>
        /// Deletes the staging folder, read-only files included — an archive
        /// made on Linux carries r--r--r-- as a read-only attribute, and a plain
        /// recursive delete refuses those and left ".extracting-…" in the
        /// person's folder.
        /// </summary>
        private static void RemoveStaging(string staging, List<string> errors)
        {
            try
            {
                if (!Directory.Exists(staging)) return;
                foreach (var file in Directory.EnumerateFiles(staging, "*", new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.ReparsePoint,
                         }))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex)
            {
                RoboCopyEngine.Trace($"bsdtar: staging left behind: {ex.Message}");
                errors.Add($"A temporary folder could not be removed: {staging}");
            }
        }

        /// <summary>
        /// Moves a staged tree into the destination, applying the conflict rules
        /// to it exactly as the zip and tar readers apply them to entries.
        ///
        /// <paramref name="stoppedAt"/> is non-null when bsdtar failed: the entry
        /// it named last may be half written and is not moved, and nothing that
        /// is already in the destination is replaced — a truncated archive under
        /// Replace otherwise put its broken last file over a good one.
        /// </summary>
        private static (int Done, ConflictOutcomes Conflicts) Rehome(
            string staging,
            string destination,
            PasteConflictPolicy policy,
            Func<IReadOnlyList<string>, FileOperations.ConflictChoice>? ask,
            ArchiveEngine.Reporter reporter,
            List<string> errors,
            CancellationToken token,
            IReadOnlyCollection<string>? stoppedAt = null)
        {
            var entries = new List<ArchiveEntryInfo>();

            // Links are never followed, or moved. Where bsdtar can create a
            // symbolic link, an archive holding one that pointed at a folder
            // elsewhere on the disk had that folder's files enumerated here and
            // moved into the destination. A link is left in staging and deleted
            // with it.
            var walk = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            };

            foreach (var folder in Directory.EnumerateDirectories(staging, "*", walk))
                entries.Add(new ArchiveEntryInfo(Relative(staging, folder), 0, Directory.GetLastWriteTime(folder), true));

            foreach (var file in Directory.EnumerateFiles(staging, "*", walk))
            {
                var info = new FileInfo(file);
                entries.Add(new ArchiveEntryInfo(Relative(staging, file), info.Length, info.LastWriteTime, false));
            }

            var rules = ExtractionRules.Build(entries, destination, policy, ask, token);
            if (rules == null) throw new OperationCanceledException(token);

            int done = 0;

            // Folders first and shallowest first, so a file never arrives before
            // the folder it goes in.
            foreach (var entry in entries.Where(e => e.IsDirectory).OrderBy(e => e.Name.Length))
            {
                token.ThrowIfCancellationRequested();

                var target = rules.TargetFor(entry.Name, true, out var problem);
                if (target == null)
                {
                    if (problem != null) errors.Add(problem);
                    continue;
                }

                try { Directory.CreateDirectory(target); done++; }
                catch (Exception ex) { errors.Add($"{entry.Name}: {ex.Message}"); }
            }

            // A name bsdtar complained about that matches nothing staged may be
            // one it could not print: anything outside the process's code page
            // comes out on standard error as question marks. Such a name is
            // matched against the staged names with every non-ASCII character
            // standing for one of those. A name that is simply absent — a link
            // it had no privilege to make — matches nothing and holds back
            // nothing.
            if (stoppedAt != null)
            {
                var staged = entries.Where(e => !e.IsDirectory)
                    .Select(e => ExtractionRules.Normalise(e.Name)).ToList();
                var exact = new HashSet<string>(staged, StringComparer.OrdinalIgnoreCase);
                var more = new List<string>();

                foreach (var printed in stoppedAt.Select(ExtractionRules.Normalise))
                {
                    if (exact.Contains(printed)) continue;

                    // Decoded here as UTF-8 from a console code page, a character
                    // it could not show can also arrive as a replacement
                    // character — and one of those can stand for several. So each
                    // run of either stands for one or more non-ASCII characters.
                    // Windows names cannot hold a question mark, so none is real.
                    // And code-page bytes can decode as other, valid characters,
                    // so anything outside ASCII is treated the same way. A name
                    // printed intact has already matched above.
                    if (printed.All(c => c < 128 && c != '?')) continue;
                    var pattern = MangledPattern(printed);
                    more.AddRange(staged.Where(name => pattern.IsMatch(name)));
                }

                if (more.Count > 0) stoppedAt = stoppedAt.Concat(more).ToList();
            }

            foreach (var entry in entries.Where(e => !e.IsDirectory))
            {
                token.ThrowIfCancellationRequested();

                var target = rules.TargetFor(entry.Name, false, out var problem);
                if (target == null)
                {
                    if (problem != null) errors.Add(problem);
                    continue;
                }

                var from = Path.Combine(staging, entry.Name.Replace('/', Path.DirectorySeparatorChar));

                if (stoppedAt != null)
                {
                    // Both sides as the rules see a name: bsdtar prints "./dir/f"
                    // where the staging folder gives "dir/f".
                    var name = ExtractionRules.Normalise(entry.Name);
                    if (stoppedAt.Any(s => string.Equals(ExtractionRules.Normalise(s), name, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add($"{entry.Name}: not extracted, because the archive stopped while it was being written");
                        rules.Withdraw(target);
                        continue;
                    }
                    if (File.Exists(target))
                    {
                        errors.Add($"{entry.Name}: left as it was, because the archive did not extract cleanly");
                        rules.Withdraw(target);
                        continue;
                    }
                }

                try
                {
                    var folder = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                    reporter.Starting(entry.Name);
                    ArchiveEngine.ClearReadOnly(target);
                    File.Move(from, target, overwrite: true);
                    done++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{entry.Name}: {ex.Message}");
                    rules.Withdraw(target);
                }
            }

            return (done, rules.Outcomes());
        }

        /// <summary>
        /// The names a mangled one could have been: every run of question marks
        /// or characters outside ASCII is one or more characters outside ASCII,
        /// and the rest is itself.
        /// </summary>
        internal static Regex MangledPattern(string printed)
        {
            var sb = new StringBuilder("^");
            bool inRun = false;
            foreach (char c in printed)
            {
                if (c == '?' || c >= 128)
                {
                    if (!inRun) sb.Append(@"[^\u0000-\u007F]+");
                    inRun = true;
                    continue;
                }
                inRun = false;
                sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string Relative(string root, string path) =>
            Path.GetRelativePath(root, path).Replace('\\', '/');

        // ---------- Running it ----------

        private static ProcessStartInfo NewStartInfo() => new()
        {
            FileName = Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        private sealed class Run : IDisposable
        {
            public required Process Process { get; init; }
            public required StringBuilder Errors { get; init; }

            public void Dispose() => Process.Dispose();
        }

        /// <param name="onLine">
        /// Each "a name" or "x name" line, with the name alone, and whether
        /// bsdtar put an error on the end of it.
        /// </param>
        private static Run Start(ProcessStartInfo start, Action<string, bool>? onLine)
        {
            var collected = new StringBuilder();
            var process = new Process { StartInfo = start };

            // Read asynchronously, always. A child that fills the error pipe while
            // nobody is reading it blocks in write and never exits, and the parent
            // waits for an exit that cannot happen — with an archive half written
            // and a progress window that has stopped moving.
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;

                if (onLine != null &&
                    (e.Data.StartsWith("a ", StringComparison.Ordinal) ||
                     e.Data.StartsWith("x ", StringComparison.Ordinal)))
                {
                    // bsdtar finishes the line it started for an entry with that
                    // entry's error: "x dir/f.bin: Truncated input file". Taken
                    // whole as a name, the error was lost — the message said only
                    // "Error exit delayed from previous errors" — and the name
                    // matched nothing, so a half-written file was kept.
                    int colon = e.Data.IndexOf(": ", 2, StringComparison.Ordinal);
                    bool failed = colon > 0;
                    try { onLine(failed ? e.Data[..colon] : e.Data, failed); } catch { }
                    if (!failed) return;
                }

                lock (collected)
                    if (collected.Length < 64 * 1024) collected.AppendLine(e.Data);
            };

            process.Start();
            process.BeginErrorReadLine();

            return new Run { Process = process, Errors = collected };
        }

        private static void Stop(Run run)
        {
            try { if (!run.Process.HasExited) run.Process.Kill(entireProcessTree: true); }
            catch { }
        }

        /// <summary>
        /// How long the system tar may make no progress at all before it is
        /// taken to be stuck.
        ///
        /// This replaces a flat thirty-second cap on the *whole run*, which is
        /// not a stall detector at all — it is a promise that nothing large will
        /// ever be compressed. A 2GB .tar.bz2 is minutes of perfectly healthy
        /// work (bzip2 has no parallel encoder), and at thirty seconds it was
        /// killed mid-write, the half-written archive deleted, and the user told
        /// the system tar "did not finish". Extraction had the same ceiling and
        /// the same ending, with the staging folder deleted underneath it.
        ///
        /// Processor time is the signal: a process that is compressing is
        /// spending it, and one that has wedged is not. The token is the other
        /// way out, and it is the one a person uses — Cancel on the progress
        /// window reaches here.
        /// </summary>
        private static readonly TimeSpan StallLimit = TimeSpan.FromMinutes(5);

        private static void Finish(Run run, CancellationToken token)
        {
            var process = run.Process;

            var lastUsed = TimeSpan.Zero;
            var quietSince = DateTime.UtcNow;

            while (!process.WaitForExit(250))
            {
                if (token.IsCancellationRequested) break;

                TimeSpan used;
                try { used = process.TotalProcessorTime; }
                catch { break; }      // it has gone; WaitForExit below settles it

                if (used > lastUsed)
                {
                    lastUsed = used;
                    quietSince = DateTime.UtcNow;
                    continue;
                }

                if (DateTime.UtcNow - quietSince < StallLimit) continue;

                Stop(run);
                throw new IOException(
                    $"The system tar stopped making progress for {StallLimit.TotalMinutes:0} minutes " +
                    "and was stopped.");
            }

            // Lets the asynchronous error reader deliver whatever it still has.
            process.WaitForExit();

            token.ThrowIfCancellationRequested();

            if (process.ExitCode == 0) return;

            string message;
            lock (run.Errors) message = run.Errors.ToString().Trim();

            RoboCopyEngine.Trace($"bsdtar: exit {process.ExitCode}: {message}");

            // Its own first line is the useful sentence; the rest is usually the
            // same complaint once per entry.
            var first = message.Length == 0
                ? $"the system tar stopped with code {process.ExitCode}"
                : message.Split('\n')[0].Trim();

            // It prefixes everything with its own name, which in a message this
            // application puts on screen is furniture.
            if (first.StartsWith("tar.exe: ", StringComparison.OrdinalIgnoreCase))
                first = first["tar.exe: ".Length..];

            throw new IOException(first);
        }

        // ---------- Compression options ----------

        /// <summary>
        /// What to pass through to libarchive for this format and level.
        ///
        /// Every name here was checked against the tar on this machine rather than
        /// taken from documentation, because bsdtar refuses an option it does not
        /// know — "Undefined option: `7zip:nonsense'", exit code 1 — so a name that
        /// has drifted is a compress that fails outright rather than one that
        /// quietly ignores what was asked for. That strictness is worth having and
        /// it is why this list is short.
        /// </summary>
        internal static string? Options(ArchiveFormat format, ArchiveLevel level, int threads)
        {
            var parts = new List<string>();

            switch (format.Id)
            {
                case "tar.xz":
                {
                    int compression = level switch
                    {
                        ArchiveLevel.Fastest => 1,
                        ArchiveLevel.Smallest => 9,
                        _ => 6,
                    };
                    parts.Add($"xz:compression-level={compression}");
                    parts.Add($"xz:threads={XzThreads(compression, threads)}");
                    break;
                }

                case "tar.zst":
                    parts.Add("zstd:compression-level=" + level switch
                    {
                        ArchiveLevel.Fastest => 1,
                        ArchiveLevel.Smallest => 19,
                        _ => 3,
                    });
                    parts.Add($"zstd:threads={threads}");
                    break;

                case "tar.bz2":
                    // bz2lib has no multithreaded encoder and there is no option
                    // to ask for one. bzip2 is the slow entry in the list, and the
                    // chooser is ordered so that is visible before it is chosen.
                    parts.Add("bzip2:compression-level=" + level switch
                    {
                        ArchiveLevel.Fastest => 1,
                        _ => 9,
                    });
                    break;

                case "7z":
                    parts.Add("7zip:compression-level=" + level switch
                    {
                        ArchiveLevel.Fastest => 1,
                        ArchiveLevel.Smallest => 9,
                        _ => 5,
                    });
                    parts.Add($"7zip:threads={threads}");
                    break;

                default:
                    return null;
            }

            return parts.Count == 0 ? null : string.Join(",", parts);
        }

        /// <summary>
        /// How many threads xz may have, which is a memory question rather than a
        /// core-count one.
        ///
        /// liblzma's multithreaded encoder gives each thread its own dictionary
        /// and its own match finder, and the dictionary is what the preset picks:
        /// one megabyte at level 1, eight at level 6, sixty-four at level 9,
        /// costing roughly ten times that per thread while encoding. Ten threads
        /// at level 9 is therefore about seven gigabytes of working set, asked for
        /// by somebody who chose "smallest" from a list of three and has no reason
        /// to expect their machine to start swapping.
        ///
        /// So the thread count is capped at a quarter of the physical memory. A
        /// quarter rather than a half because this is a background job on a
        /// machine somebody is still using — the whole point of the progress
        /// window not being modal is that the file manager stays usable, and that
        /// is not true if the archive has taken the memory.
        /// </summary>
        internal static int XzThreads(int compressionLevel, int threads)
        {
            long perThread = compressionLevel switch
            {
                <= 1 => 12L << 20,
                <= 3 => 40L << 20,
                <= 6 => 100L << 20,
                <= 8 => 400L << 20,
                _ => 700L << 20,
            };

            long budget = PhysicalMemoryBytes() / 4;
            if (budget <= 0) return Math.Clamp(threads, 1, 4);

            int affordable = (int)Math.Min(int.MaxValue, budget / perThread);
            return Math.Clamp(affordable, 1, Math.Max(1, threads));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        private static long _physicalMemory = -1;

        /// <summary>
        /// How much memory the machine has, cached.
        ///
        /// Not GC.GetGCMemoryInfo, which answers about this process's heap limit
        /// rather than about the machine — and the memory being budgeted here is
        /// spent by another process entirely.
        /// </summary>
        internal static long PhysicalMemoryBytes()
        {
            long known = Interlocked.Read(ref _physicalMemory);
            if (known >= 0) return known;

            long total = 0;
            try
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (GlobalMemoryStatusEx(ref status))
                    total = (long)Math.Min(status.TotalPhysical, long.MaxValue);
            }
            catch { total = 0; }

            Interlocked.Exchange(ref _physicalMemory, total);
            return total;
        }
    }
}
