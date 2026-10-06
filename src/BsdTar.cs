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
                // The version string alone carries no Windows build number —
                // "3.8.8 (WinBuild.160101.0800)" for every build of 3.8.8 — so a
                // serviced DLL under the same string would never be measured
                // again. Its size and date change whenever its bytes do.
                var dllInfo = new FileInfo(systemDll);
                var key = FileVersionInfo.GetVersionInfo(systemDll).FileVersion + "|" +
                          new FileInfo(SystemExecutable).Length + "|" +
                          dllInfo.Length + "|" + dllInfo.LastWriteTimeUtc.Ticks;

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
            // The first tar run in this process clears what a killed one left,
            // whether or not this run needs a junction of its own.
            if (Interlocked.Exchange(ref _swept, 1) == 0) SweepLinks();

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
            List<string> errors,
            CancellationToken token)
        {
            RequireAvailable();

            // Only the items that were actually selected go on the command line;
            // everything under a selected folder is bsdtar's business.
            var tops = plan.Where(p => !p.Name.Contains('/')).ToList();
            if (tops.Count == 0) return 0;

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

            var aliases = new List<Alias>();
            try
            {
                int budget = CommandLineBudget - archivePath.Length - 120;

                // What each top-level item is called on the command line, which is
                // also how bsdtar names everything under it.
                var argFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var groups = new List<(string Folder, List<string> Args)>();

                foreach (var group in tops.GroupBy(
                             p => Path.GetDirectoryName(p.Source) ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    // A folder past MAX_PATH cannot be bsdtar's current directory —
                    // "could not chdir" — and this volume has no 8.3 names to
                    // shorten it with. A junction at a short path can be.
                    var folder = Reachable(group.Key, aliases);
                    budget -= folder.Length + 6;
                    var args = new List<string>();

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

                        args.Add(onDisk);
                        argFor[item.Name] = onDisk;
                    }

                    groups.Add((folder, args));
                }

                // Progress. A line's name identifies its entry only where it is
                // ASCII, so an entry that matches nothing is charged a share of
                // whatever is still unaccounted for — never more. Charging it the
                // average of everything took the bar to 154 percent when the
                // archive held entries the plan did not: a link bsdtar stored, a
                // name it printed in another code page.
                var gate = new object();
                var unmatched = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in plan) unmatched[item.Name] = item.Size;
                long unaccounted = plan.Sum(p => p.Size);
                int unaccountedItems = plan.Count;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Items that could not be opened, left out on the next attempt.
                var excluded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var unexplained = new List<string>();

                for (int attempt = 0; ; attempt++)
                {
                    var start = NewStartInfo();
                    foreach (var flag in FormatFlags(format)) start.ArgumentList.Add(flag);
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

                    foreach (var name in excluded.Keys)
                    {
                        start.ArgumentList.Add("--exclude");
                        start.ArgumentList.Add(Pattern(ArchivePathOf(name, argFor)));
                    }

                    foreach (var (folder, args) in groups)
                    {
                        start.ArgumentList.Add("-C");
                        start.ArgumentList.Add(folder);
                        foreach (var a in args) start.ArgumentList.Add(a);
                    }

                    RoboCopyEngine.Trace($"bsdtar create: {string.Join(" ", start.ArgumentList)}");

                    Outcome outcome;
                    using (var process = Start(start, (line, failed, _) =>
                           {
                               if (failed || !line.StartsWith("a ", StringComparison.Ordinal)) return;

                               var name = EntryName(line[2..]);
                               long charge;
                               lock (gate)
                               {
                                   // A second attempt names everything again.
                                   if (!seen.Add(name)) return;

                                   if (unmatched.Remove(name, out var size))
                                   {
                                       charge = Math.Min(size, unaccounted);
                                   }
                                   else
                                   {
                                       charge = unaccountedItems > 0 ? unaccounted / unaccountedItems : 0;
                                   }
                                   unaccounted = Math.Max(0, unaccounted - charge);
                                   unaccountedItems = Math.Max(0, unaccountedItems - 1);
                               }

                               reporter.Starting(name);
                               reporter.Finished(charge);
                           }))
                    {
                        using var kill = token.Register(() => Stop(process));
                        outcome = Wait(process, token);
                    }

                    if (outcome.ExitCode == 0) break;

                    // It gave up on the whole thing rather than on an entry.
                    if (!outcome.Delayed) throw new IOException(outcome.First);

                    // An entry it could not open — a file locked by another
                    // program — is reported, and bsdtar finishes with an error
                    // code. That used to throw the whole archive away. Worse,
                    // measured: bsdtar abandons the rest of that folder after it,
                    // so keeping the archive as it stands would silently lose
                    // the files that came after. So the archive is made again
                    // without the file that could not be opened.
                    unexplained.Clear();
                    bool added = false;
                    foreach (var message in outcome.Messages)
                    {
                        const string prefix = "Couldn't open ";
                        int at = message.IndexOf(prefix, StringComparison.Ordinal);
                        int colon = message.LastIndexOf(": ", StringComparison.Ordinal);
                        if (at >= 0 && colon > at + prefix.Length &&
                            MatchPlan(EntryName(message[(at + prefix.Length)..colon]), plan) is { } item)
                        {
                            if (excluded.TryAdd(item.Name, message[(colon + 2)..].Trim())) added = true;
                            continue;
                        }

                        if (!message.Contains("Error exit delayed", StringComparison.Ordinal))
                            unexplained.Add(Plain(message));
                    }

                    if (added && attempt < 4) continue;
                    break;
                }

                foreach (var (name, reason) in excluded)
                    errors.Add($"{name}: it could not be opened ({reason}), so it was left out");
                errors.AddRange(unexplained);

                // Anything with a name that can be read back and that never went
                // in, after a run that complained: said, not left to be found.
                if (unexplained.Count > 0)
                    foreach (var name in unmatched.Keys.Where(n => !excluded.ContainsKey(n) && n.All(c => c < 128)))
                        errors.Add($"{name}: it was not added");

                return seen.Count;
            }
            finally
            {
                foreach (var alias in aliases) alias.Dispose();
            }
        }

        /// <summary>
        /// The format, said outright rather than left to "-a".
        ///
        /// "-a" picks the format from the archive's name and matches the
        /// extension case-sensitively, so "BACKUP.7Z" and "x.Tar.Xz" got no
        /// format at all — and then the compression options failed with
        /// "Unknown module name: 7zip".
        /// </summary>
        internal static string[] FormatFlags(ArchiveFormat format) => format.Id switch
        {
            "7z" => new[] { "--format", "7zip" },
            "tar.xz" => new[] { "-J" },
            "tar.bz2" => new[] { "-j" },
            "tar.zst" => new[] { "--zstd" },
            _ => new[] { "-a" },
        };

        /// <summary>An entry name as bsdtar prints it, without "./" or a leading slash.</summary>
        private static string EntryName(string printed)
        {
            var name = printed.Trim().Replace('\\', '/');
            while (true)
            {
                if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
                else if (name.StartsWith('/')) name = name[1..];
                else return name;
            }
        }

        /// <summary>The plan item a printed name stands for: by name, or by the mangled pattern.</summary>
        private static ArchiveItem? MatchPlan(string printed, IReadOnlyList<ArchiveItem> plan)
        {
            var exact = plan.FirstOrDefault(p => string.Equals(p.Name, printed, StringComparison.OrdinalIgnoreCase));
            if (exact != null || printed.All(c => c < 128 && c != '?')) return exact;
            var pattern = MangledPattern(printed);
            return plan.FirstOrDefault(p => pattern.IsMatch(p.Name));
        }

        /// <summary>A plan name as bsdtar sees it: the top segment as it was given on the command line.</summary>
        private static string ArchivePathOf(string planName, Dictionary<string, string> argFor)
        {
            int slash = planName.IndexOf('/');
            var top = slash < 0 ? planName : planName[..slash];
            var arg = argFor.TryGetValue(top, out var given) ? given : top;
            return slash < 0 ? arg : arg + planName[slash..];
        }

        /// <summary>A name as an --exclude pattern that matches only itself.</summary>
        internal static string Pattern(string name)
        {
            var sb = new StringBuilder(name.Length + 8);
            foreach (var c in name)
            {
                if (c is '*' or '?' or '[') sb.Append('[').Append(c).Append(']');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>One of bsdtar's own lines, without its name on the front.</summary>
        private static string Plain(string message) =>
            message.StartsWith("tar.exe: ", StringComparison.OrdinalIgnoreCase) ? message["tar.exe: ".Length..] : message;

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

            var aliases = new List<Alias>();
            try
            {
                // No listing first. It used to run `tar -tvf` for the totals,
                // which for a .tar.xz is a whole decompression before the real
                // one — about half the time of the extraction, measured. The
                // progress is read from how much of the archive bsdtar has read,
                // and the entries are counted from its own lines as it goes.
                var start = NewStartInfo();
                start.ArgumentList.Add("-x");
                start.ArgumentList.Add("-v");

                // Keep the first of two entries with one name, as the zip and tar
                // readers do; without it the last one silently won. Into a staging
                // folder this application has just made, nothing else can be
                // "already there".
                start.ArgumentList.Add("-k");

                start.ArgumentList.Add("-f");
                start.ArgumentList.Add(archivePath);
                start.ArgumentList.Add("-C");
                start.ArgumentList.Add(Reachable(staging, aliases));

                RoboCopyEngine.Trace($"bsdtar extract: {string.Join(" ", start.ArgumentList)}");

                // Every "x" line, in order: the name as printed, and the complaint
                // on the end of it if there was one.
                var printed = new List<(string Name, string? Failure)>();
                Outcome? outcome = null;
                bool unfinished = false;

                long archiveBytes = 0;
                try { archiveBytes = new FileInfo(archivePath).Length; } catch { }

                using (var process = Start(start, (line, failed, reason) =>
                       {
                           if (!line.StartsWith("x ", StringComparison.Ordinal)) return;
                           lock (printed) printed.Add((line[2..].Trim(), failed ? reason ?? "" : null));
                           reporter.Finished(0);
                       }))
                {
                    long lastRead = 0;
                    reporter.Estimate(archiveBytes, () =>
                    {
                        try { if (GetProcessIoCounters(process.Process.Handle, out var io)) lastRead = (long)io.ReadTransferCount; }
                        catch { }
                        return lastRead;
                    });

                    using var kill = token.Register(() => Stop(process));

                    try { outcome = Wait(process, token); }
                    catch (IOException ex) when (!token.IsCancellationRequested)
                    {
                        // The stall timer: bsdtar did not get to the end.
                        errors.Add(ex.Message);
                        unfinished = true;
                    }
                }

                token.ThrowIfCancellationRequested();

                List<(string Name, string? Failure)> lines;
                lock (printed) lines = new List<(string, string?)>(printed);

                bool damaged = false;
                if (outcome is { ExitCode: not 0 })
                {
                    // "Error exit delayed from previous errors" is bsdtar saying it
                    // carried on to the end and is reporting failures it met on the
                    // way. Without it, it stopped where it was.
                    if (!outcome.Delayed) unfinished = true;

                    damaged = lines.Any(l => l.Failure != null && LooksDamaged(l.Failure)) ||
                              outcome.Messages.Any(LooksDamaged);

                    // Each entry it complained about, named. One it could not
                    // write — a link without the privilege to make one — used to
                    // throw the whole extraction away, good files included.
                    foreach (var (name, failure) in lines)
                        if (failure != null)
                            errors.Add(LooksDamaged(failure)
                                ? $"{name}: the archive is damaged at this point"
                                : $"{name}: {PlainReason(failure)}");

                    if (unfinished && !damaged) errors.Add(outcome.First);
                }

                // Every entry it complained about is not trusted. The one it named
                // last is distrusted only when it did not get to the end — a
                // stall, a crash — because only then can that one be half
                // written. Distrusted always, one link in a .tar.xz withheld the
                // archive's last file as "stopped while it was being written",
                // and under Replace left every existing file as it was.
                var distrusted = lines.Where(l => l.Failure != null).Select(l => l.Name).ToList();
                if (unfinished && lines.Count > 0) distrusted.Add(lines[^1].Name);

                bool stripped = outcome != null &&
                                outcome.Messages.Any(m => m.Contains("Removing leading", StringComparison.Ordinal));

                var (done, conflicts, stagedFiles) = Rehome(staging, destination, policy, ask, reporter, errors, token,
                    distrusted.Count > 0 || unfinished ? distrusted : null, unfinished,
                    staged => Withheld(lines, staged, destination, stripped ? archivePath : null, token));

                // Two entries with one name — or two that differ only in capitals,
                // which are one name here — are one file in the staging folder.
                // The zip and tar readers say so; this said nothing and kept the
                // last. Now the first is kept and the difference is said.
                int arrived = lines.Count(l => l.Failure == null && !l.Name.EndsWith('/'));
                if (!unfinished && arrived > stagedFiles)
                {
                    int lost = arrived - stagedFiles;
                    errors.Add($"{NameRules.Items(lost)} in the archive repeated a name already used " +
                               "(names that differ only in capitals are one name here); the first of each was kept");
                }

                if (damaged) errors.Add(ArchiveEngine.DamagedMessage(done));

                return (done, conflicts);
            }
            finally
            {
                foreach (var alias in aliases) alias.Dispose();
                RemoveStaging(staging, errors);
            }
        }

        /// <summary>Whether one of bsdtar's complaints is about the archive rather than about one entry.</summary>
        private static bool LooksDamaged(string message) =>
            message.Contains("truncated", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("damaged", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("corrupt", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Decompression failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Unrecognized archive format", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Error opening archive", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("(null)", StringComparison.Ordinal);

        /// <summary>
        /// A complaint about one entry, without the staging path it quotes: a
        /// link said "Can't create '\\?\C:\…\.extracting-1a2b3c4d\link': Invalid
        /// argument", which names a folder that is gone by the time it is read.
        /// </summary>
        private static string PlainReason(string reason)
        {
            var plain = Regex.Replace(reason, @"'[^']*'", "it").Trim();
            return plain.Length == 0 ? "it could not be extracted" : plain;
        }

        /// <summary>
        /// Staged entries that came from a name Windows cannot store, and so must
        /// not be moved into place under the name bsdtar gave them instead.
        ///
        /// bsdtar does not refuse those names; it changes them, silently: "q?"
        /// becomes "q_", "x*y" "x_y", "trail." "trail", "a:b" "b", and
        /// "//host/share/f" "host/share/f". Each is refused here with the name it
        /// had, as the zip and tar readers refuse it.
        ///
        /// The names come from bsdtar's own output, which is not trusted for
        /// anything outside ASCII — see the table at the top of this file — and
        /// they are used only to refuse, never to decide where a file goes. A
        /// printed name with a question mark in it may be a non-ASCII name the
        /// console could not show; if a staged name fits it that way, it is one.
        /// </summary>
        private static Dictionary<string, string> Withheld(
            List<(string Name, string? Failure)> lines,
            IReadOnlyCollection<string> staged,
            string destination,
            string? listAgain,
            CancellationToken token)
        {
            var withheld = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var present = new HashSet<string>(staged, StringComparer.OrdinalIgnoreCase);

            foreach (var (raw, failure) in lines)
            {
                if (failure != null) continue;
                var name = raw.TrimEnd('/');
                if (name.Length == 0 || name.Any(c => c >= 128)) continue;
                if (ArchiveEngine.SafeTarget(destination, name, out var reason) != null || reason == null) continue;

                if (name.Contains('?'))
                {
                    var pattern = MangledPattern(name);
                    if (staged.Any(s => pattern.IsMatch(s))) continue;
                }

                var cleaned = ExtractionRules.Normalise(CleanLikeTar(name));
                if (present.Contains(cleaned)) withheld.TryAdd(cleaned, reason);
            }

            // A drive letter or a network path has already been taken off the
            // printed name, so the only record of it is the archive's own
            // listing — read only when bsdtar said it removed one.
            if (listAgain != null)
            {
                foreach (var original in ListNames(listAgain, token))
                {
                    var name = original.TrimEnd('/');
                    if (name.Length == 0 || name.Any(c => c >= 128)) continue;
                    if (ArchiveEngine.SafeTarget(destination, name, out var reason) != null || reason == null) continue;

                    var rest = StripRoot(name);
                    if (rest == name) continue;
                    var cleaned = ExtractionRules.Normalise(CleanLikeTar(rest));
                    if (present.Contains(cleaned)) withheld.TryAdd(cleaned, reason);
                }
            }

            return withheld;
        }

        /// <summary>What bsdtar does on Windows to a name it cannot store: the forbidden characters become underscores and a trailing dot or space goes.</summary>
        internal static string CleanLikeTar(string name) => string.Join('/', name.Replace('\\', '/').Split('/').Select(segment =>
        {
            var chars = segment.Select(c => c < 32 || ":*?\"<>|".IndexOf(c) >= 0 ? '_' : c).ToArray();
            return new string(chars).TrimEnd('.', ' ');
        }));

        /// <summary>A name without the drive letters and leading slashes bsdtar removes.</summary>
        private static string StripRoot(string name)
        {
            var rest = name.Replace('\\', '/');
            while (true)
            {
                var before = rest;
                rest = rest.TrimStart('/');
                if (rest.Length >= 2 && rest[1] == ':' && char.IsAsciiLetter(rest[0])) rest = rest[2..];
                if (rest == before) return rest;
            }
        }

        /// <summary>The names in an archive as bsdtar prints them, for <see cref="Withheld"/> only.</summary>
        private static List<string> ListNames(string archivePath, CancellationToken token)
        {
            var names = new List<string>();
            var start = NewStartInfo();
            start.ArgumentList.Add("-tf");
            start.ArgumentList.Add(archivePath);
            start.RedirectStandardOutput = true;

            using var process = Start(start, null);
            using var kill = token.Register(() => Stop(process));
            string? line;
            while ((line = process.Process.StandardOutput.ReadLine()) != null) names.Add(line);
            try { Wait(process, token); } catch (IOException) { }
            return names;
        }

        /// <summary>
        /// Deletes the staging folder, read-only files included — an archive
        /// made on Linux carries r--r--r-- as a read-only attribute, and a plain
        /// recursive delete refuses those and left ".extracting-…" in the
        /// person's folder.
        ///
        /// Through the "\\?\" form of every path, because the staging folder can
        /// hold names Windows' ordinary path rules cannot reach: bsdtar writes
        /// "CON" as a file called CON, and a trailing dot as itself.
        /// </summary>
        private static void RemoveStaging(string staging, List<string> errors)
        {
            try
            {
                if (!Directory.Exists(staging)) return;
                DeleteTree(Verbatim(staging));
            }
            catch (Exception ex)
            {
                RoboCopyEngine.Trace($"bsdtar: staging left behind: {ex.Message}");
                errors.Add($"A temporary folder could not be removed: {staging}");
            }
        }

        private static string Verbatim(string path)
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
            return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        }

        private static void DeleteTree(string folder)
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                // A link is removed, never followed.
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (entry is DirectoryInfo) Directory.Delete(entry.FullName, recursive: false);
                    else File.Delete(entry.FullName);
                    continue;
                }

                if (entry is DirectoryInfo) { DeleteTree(entry.FullName); continue; }

                try { File.SetAttributes(entry.FullName, FileAttributes.Normal); } catch { }
                File.Delete(entry.FullName);
            }

            try { File.SetAttributes(folder, FileAttributes.Directory); } catch { }
            Directory.Delete(folder, recursive: false);
        }

        /// <summary>
        /// Moves a staged tree into the destination, applying the conflict rules
        /// to it exactly as the zip and tar readers apply them to entries.
        ///
        /// <paramref name="stoppedAt"/> holds the entries bsdtar complained
        /// about, which may be half written and are not moved. When
        /// <paramref name="unfinished"/> — bsdtar never got to the end — nothing
        /// that is already in the destination is replaced either: a truncated
        /// archive under Replace otherwise put its broken last file over a good
        /// one.
        /// </summary>
        /// <param name="withhold">Given every staged name, the ones not to move and why.</param>
        private static (int Done, ConflictOutcomes Conflicts, int StagedFiles) Rehome(
            string staging,
            string destination,
            PasteConflictPolicy policy,
            Func<IReadOnlyList<string>, FileOperations.ConflictChoice>? ask,
            ArchiveEngine.Reporter reporter,
            List<string> errors,
            CancellationToken token,
            IReadOnlyCollection<string>? stoppedAt,
            bool unfinished,
            Func<IReadOnlyCollection<string>, Dictionary<string, string>> withhold)
        {
            var entries = new List<ArchiveEntryInfo>();
            var folders = new List<ArchiveEntryInfo>();
            var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var links = new List<string>();

            // One scan, carrying sizes and dates out of the call that found each
            // entry. Asking again by path — new FileInfo(file).Length — failed
            // for a name Windows' path rules rewrite: a trailing dot or space is
            // dropped, the file "does not exist", and that one name took the
            // whole extraction with it, leaving an empty folder behind.
            //
            // Links are never followed, or moved. Where bsdtar can create a
            // symbolic link, an archive holding one that pointed at a folder
            // elsewhere on the disk had that folder's files enumerated here and
            // moved into the destination. A link is said, left in staging, and
            // deleted with it.
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(Verbatim(staging)));
            var root = Verbatim(staging);

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var folder = pending.Pop();
                bool any = false;

                foreach (var item in folder.EnumerateFileSystemInfos())
                {
                    any = true;
                    var name = Relative(root, item.FullName);

                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) { links.Add(name); continue; }

                    if (item is DirectoryInfo sub)
                    {
                        folders.Add(new ArchiveEntryInfo(name, 0, sub.LastWriteTime, true));
                        pending.Push(sub);
                    }
                    else if (item is FileInfo file)
                    {
                        entries.Add(new ArchiveEntryInfo(name, file.Length, file.LastWriteTime, false));
                    }
                }

                if (!any && folder.FullName.Length > root.Length) empty.Add(Relative(root, folder.FullName));
            }

            foreach (var link in links) errors.Add($"{link}: a symbolic link was not extracted");

            var withheld = withhold(entries.Concat(folders).Select(e => ExtractionRules.Normalise(e.Name)).ToList());

            // A folder is made when something good goes in it, or when it was
            // empty in the archive. One that only ever held refused names is
            // not left behind empty.
            var needed = new HashSet<string>(empty, StringComparer.OrdinalIgnoreCase);
            foreach (var file in entries)
            {
                var name = ExtractionRules.Normalise(file.Name);
                if (withheld.ContainsKey(name)) continue;
                for (int slash = name.LastIndexOf('/'); slash > 0; slash = name.LastIndexOf('/', slash - 1))
                    needed.Add(name[..slash]);
            }

            var all = entries.Concat(folders.Where(f =>
            {
                var name = ExtractionRules.Normalise(f.Name);
                return needed.Contains(name) && !withheld.ContainsKey(name);
            })).ToList();

            var said = new HashSet<string>();
            foreach (var (name, reason) in withheld)
                if (entries.Any(e => string.Equals(ExtractionRules.Normalise(e.Name), name, StringComparison.OrdinalIgnoreCase)) &&
                    said.Add(reason))
                    errors.Add(reason);

            var rules = ExtractionRules.Build(all.Where(e => !withheld.ContainsKey(ExtractionRules.Normalise(e.Name))).ToList(),
                destination, policy, ask, token);
            if (rules == null) throw new OperationCanceledException(token);

            int done = 0;

            // Folders first and shallowest first, so a file never arrives before
            // the folder it goes in.
            foreach (var entry in all.Where(e => e.IsDirectory).OrderBy(e => e.Name.Length))
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
                var stagedNames = entries.Select(e => ExtractionRules.Normalise(e.Name)).ToList();
                var exact = new HashSet<string>(stagedNames, StringComparer.OrdinalIgnoreCase);
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
                    more.AddRange(stagedNames.Where(name => pattern.IsMatch(name)));
                }

                if (more.Count > 0) stoppedAt = stoppedAt.Concat(more).ToList();
            }

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (withheld.ContainsKey(ExtractionRules.Normalise(entry.Name))) continue;

                var target = rules.TargetFor(entry.Name, false, out var problem);
                if (target == null)
                {
                    if (problem != null) errors.Add(problem);
                    continue;
                }

                var from = Path.Combine(root, entry.Name.Replace('/', Path.DirectorySeparatorChar));

                if (stoppedAt != null)
                {
                    // Both sides as the rules see a name: bsdtar prints "./dir/f"
                    // where the staging folder gives "dir/f".
                    var name = ExtractionRules.Normalise(entry.Name);
                    if (stoppedAt.Any(s => string.Equals(ExtractionRules.Normalise(s), name, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (unfinished)
                            errors.Add($"{entry.Name}: not extracted, because the archive stopped while it was being written");
                        rules.Withdraw(target);
                        continue;
                    }
                    if (unfinished && File.Exists(target))
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
                    reporter.Advance(entry.Size);
                    done++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{entry.Name}: {ex.Message}");
                    rules.Withdraw(target);
                }
            }

            return (done, rules.Outcomes(), entries.Count + links.Count);
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

        // ---------- A short name for a long folder ----------

        /// <summary>
        /// A junction at a short path, standing in for a folder too long to be
        /// bsdtar's current directory.
        ///
        /// bsdtar changes into the folder given with -C, and a current directory
        /// is limited to MAX_PATH whatever else is long-path aware: a 7z or
        /// .tar.xz of anything past 260 characters failed in both directions
        /// with "could not chdir". Its file access is not limited — libarchive
        /// opens through "\\?\" names — so only the folder it starts in has to be
        /// short. This volume has 8.3 names switched off, so shortening is not
        /// available; a junction needs no privilege and works, measured on a
        /// 447-character folder both ways.
        ///
        /// Kept in %LOCALAPPDATA%\ExplorerNative\tarlinks rather than in Temp,
        /// where something cleaning up might follow it. It is removed as a link
        /// — never recursively — and any left by a crash are swept.
        /// </summary>
        private sealed class Alias : IDisposable
        {
            public required string Link { get; init; }

            public void Dispose()
            {
                try { Directory.Delete(Link, recursive: false); }
                catch (Exception ex) { RoboCopyEngine.Trace($"bsdtar: junction left behind: {ex.Message}"); }
            }
        }

        private const int LongFolder = 240;
        private static int _swept;

        private static string LinkRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExplorerNative", "tarlinks");

        /// <summary>The folder itself when it is short enough, otherwise a junction to it.</summary>
        private static string Reachable(string folder, List<Alias> aliases)
        {
            if (folder.Length < LongFolder) return folder;

            var full = Path.GetFullPath(folder);
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) full = full[4..];
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return folder;   // a share cannot be a junction's target

            try
            {
                Directory.CreateDirectory(LinkRoot);
                if (Interlocked.Exchange(ref _swept, 1) == 0) SweepLinks();

                // Named after this process, so a sweep can tell a link whose
                // owner is gone from one still in use.
                var link = Path.Combine(LinkRoot, $"{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}");
                CreateJunction(link, full);
                aliases.Add(new Alias { Link = link });
                return link;
            }
            catch (Exception ex)
            {
                RoboCopyEngine.Trace($"bsdtar: no junction for a long folder: {ex.Message}");
                return folder;
            }
        }

        /// <summary>Removes junctions a crashed run left, as links and nothing else.</summary>
        internal static void SweepLinks()
        {
            try
            {
                if (!Directory.Exists(LinkRoot)) return;
                foreach (var old in new DirectoryInfo(LinkRoot).EnumerateDirectories())
                {
                    if ((old.Attributes & FileAttributes.ReparsePoint) == 0) continue;
                    if (!Abandoned(old)) continue;
                    try { Directory.Delete(old.FullName, recursive: false); } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Whether nothing can still be using a link: the process named in it
        /// has gone, or the number now belongs to a process started after the
        /// link was made. A name from before links carried one waits an hour.
        /// </summary>
        private static bool Abandoned(DirectoryInfo link)
        {
            int dash = link.Name.IndexOf('-');
            if (dash <= 0 || !int.TryParse(link.Name.AsSpan(0, dash), out int pid))
                return DateTime.UtcNow - link.CreationTimeUtc >= TimeSpan.FromHours(1);

            if (pid == Environment.ProcessId) return false;
            try
            {
                using var owner = Process.GetProcessById(pid);
                return owner.StartTime.ToUniversalTime() > link.CreationTimeUtc.AddSeconds(1);
            }
            catch (ArgumentException) { return true; }        // no such process
            catch { return false; }                            // there, and not ours to read
        }

        private static void CreateJunction(string link, string target)
        {
            Directory.CreateDirectory(link);
            try
            {
                var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
                var print = Encoding.Unicode.GetBytes(target);
                int pathBuffer = substitute.Length + 2 + print.Length + 2;
                if (8 + pathBuffer > ushort.MaxValue) throw new PathTooLongException();

                var buffer = new byte[16 + pathBuffer];
                BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);               // IO_REPARSE_TAG_MOUNT_POINT
                BitConverter.GetBytes((ushort)(8 + pathBuffer)).CopyTo(buffer, 4);
                BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
                BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
                BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
                BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
                substitute.CopyTo(buffer, 16);
                print.CopyTo(buffer, 16 + substitute.Length + 2);

                using var handle = CreateFileW(link, 0x40000000, 0, IntPtr.Zero, 3,
                    0x02000000 | 0x00200000, IntPtr.Zero);                          // BACKUP_SEMANTICS | OPEN_REPARSE_POINT
                if (handle.IsInvalid) throw new IOException($"error {Marshal.GetLastWin32Error()} opening the link");
                if (!DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    throw new IOException($"error {Marshal.GetLastWin32Error()} making the link");
            }
            catch
            {
                try { Directory.Delete(link, recursive: false); } catch { }
                throw;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access,
            uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code,
            byte[] inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
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
        private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

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
        /// Each "a name" or "x name" line, with the name alone, whether bsdtar
        /// put an error on the end of it, and that error.
        /// </param>
        private static Run Start(ProcessStartInfo start, Action<string, bool, string?>? onLine)
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
                    try { onLine(failed ? e.Data[..colon] : e.Data, failed, failed ? e.Data[(colon + 2)..] : null); } catch { }
                    if (!failed) return;
                }

                lock (collected)
                    if (collected.Length < 64 * 1024) collected.AppendLine(e.Data);
            };

            process.Start();
            KeepWithUs(process);
            process.BeginErrorReadLine();

            return new Run { Process = process, Errors = collected };
        }

        // ---------- Dying with the app ----------

        /// <summary>
        /// One job for every tar this process starts, closed only by Windows
        /// when this process ends — however it ends.
        ///
        /// Kill on close is the point: a crash, Task Manager, or a log-off took
        /// the app away and left tar.exe compressing on its own for as long as
        /// the archive took, holding the half-written file and, for a long
        /// folder, the junction it was started in. Cancel and Stop still kill
        /// it as before; this is for when nobody is left to.
        ///
        /// Windows 8 and later allow nested jobs, so it works when the app is
        /// itself in one (a terminal, a scheduler). If the assignment is
        /// refused anyway, the tar runs as it always did.
        /// </summary>
        private static readonly Lazy<IntPtr> TarJob = new(CreateTarJob);

        private static IntPtr CreateTarJob()
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                RoboCopyEngine.Trace($"bsdtar: no job object: error {Marshal.GetLastWin32Error()}");
                return IntPtr.Zero;
            }

            var limits = new JobExtendedLimits();
            limits.Basic.LimitFlags = JobObjectLimitKillOnJobClose;
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits,
                    Marshal.SizeOf<JobExtendedLimits>()))
            {
                RoboCopyEngine.Trace($"bsdtar: job limits refused: error {Marshal.GetLastWin32Error()}");
                CloseHandle(job);
                return IntPtr.Zero;
            }

            // Never closed: the handle closing as this process exits is what
            // ends the tars.
            return job;
        }

        private static void KeepWithUs(Process process)
        {
            try
            {
                var job = TarJob.Value;
                if (job == IntPtr.Zero) return;
                if (!AssignProcessToJobObject(job, process.Handle))
                    RoboCopyEngine.Trace($"bsdtar: not in the job: error {Marshal.GetLastWin32Error()}");
            }
            catch (Exception ex) { RoboCopyEngine.Trace($"bsdtar: not in the job: {ex.Message}"); }
        }

        /// <summary>Whether a tar this process started would end with it. For the tests.</summary>
        internal static bool InJob(Process process)
        {
            var job = TarJob.Value;
            return job != IntPtr.Zero && IsProcessInJob(process.Handle, job, out bool inside) && inside;
        }

        private const uint JobObjectLimitKillOnJobClose = 0x2000;
        private const int JobObjectExtendedLimitInformation = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobBasicLimits
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobExtendedLimits
        {
            public JobBasicLimits Basic;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass,
            ref JobExtendedLimits info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsProcessInJob(IntPtr process, IntPtr job,
            [MarshalAs(UnmanagedType.Bool)] out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

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

        /// <summary>How a run ended.</summary>
        /// <param name="First">Its first complaint worth reading, or a sentence standing in for one.</param>
        /// <param name="Delayed">
        /// bsdtar said "Error exit delayed from previous errors": it went on to
        /// the end and is reporting entries that failed on the way.
        /// </param>
        /// <param name="Messages">Every line it wrote that was not an entry.</param>
        private sealed record Outcome(int ExitCode, string First, bool Delayed, IReadOnlyList<string> Messages);

        private static void Finish(Run run, CancellationToken token)
        {
            var outcome = Wait(run, token);
            if (outcome.ExitCode != 0) throw new IOException(outcome.First);
        }

        private static Outcome Wait(Run run, CancellationToken token)
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

            string text;
            lock (run.Errors) text = run.Errors.ToString();
            var messages = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

            if (process.ExitCode == 0) return new Outcome(0, "", false, messages);

            RoboCopyEngine.Trace($"bsdtar: exit {process.ExitCode}: {text.Trim()}");

            bool delayed = messages.Any(m => m.Contains("Error exit delayed from previous errors", StringComparison.Ordinal));

            // Its own first complaint is the useful sentence; the rest is usually
            // the same complaint once per entry. A warning about a name it
            // changed is not the complaint, and nor is its sign-off.
            var first = messages
                .Select(Plain)
                .FirstOrDefault(m => !m.StartsWith("Removing leading", StringComparison.Ordinal) &&
                                     !m.StartsWith("Error exit delayed", StringComparison.Ordinal));

            // A truncated 7z says "(null)", and nothing else.
            if (string.IsNullOrEmpty(first) || first == "(null)")
                first = first == "(null)"
                    ? "This archive is damaged or incomplete."
                    : $"the system tar stopped with code {process.ExitCode}";

            return new Outcome(process.ExitCode, first, delayed, messages);
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
