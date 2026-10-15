using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// Folders whose name ends in a dot, away from the main window. Windows
    /// strips the dot from an ordinary path, so "Backup." asked about plainly is
    /// "Backup": a shortcut made inside it landed in the sibling (or nowhere),
    /// an archive with no telling extension in it was not recognised, Compress
    /// and Extract could not write into it, and its size came out as nothing.
    ///
    /// Every check runs beside a sibling "Backup" holding something else, so a
    /// call that loses the dot is caught reaching the wrong folder rather than
    /// only failing.
    /// </summary>
    internal static class Sweep308DotPathTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        public static void RunAll(Action<string, bool, string?> check)
        {
            _check = check;
            Console.WriteLine("Folders ending in a dot, outside the main window (3.0.8):");

            var root = Path.Combine(Path.GetTempPath(), "ExplorerNativeTests_dotpath_" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var dotted = Path.Combine(root, "Backup.");
                var sibling = Path.Combine(root, "Backup");
                Directory.CreateDirectory(NameRules.ExactPath(dotted));
                Directory.CreateDirectory(sibling);
                File.WriteAllText(NameRules.ExactPath(Path.Combine(dotted, "real.txt")), "the dotted folder's file");
                File.WriteAllText(Path.Combine(sibling, "decoy.txt"), "the sibling's file, which is longer than the other");

                Run("shortcut", () => Shortcuts(root, dotted, sibling));
                Run("identify", () => Identify(dotted));
                Run("compress", () => Compress(root, dotted, sibling));
                Run("extract", () => Extract(root, dotted, sibling));
                Run("folder size", () => FolderSize(root, dotted));
                Run("suggested name", () => SuggestedName(root));
                SourceRules();
            }
            catch (Exception ex)
            {
                Check("dot-path fixtures were made", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(NameRules.ExactPath(root), recursive: true); } catch { }
            }

            Console.WriteLine();
        }

        private static void Run(string what, Action body)
        {
            try { body(); }
            catch (Exception ex) { Check($"{what} checks ran", false, ex.GetType().Name + ": " + ex.Message); }
        }

        private static bool ExistsExactly(string path) =>
            File.Exists(NameRules.ExactPath(path)) || Directory.Exists(NameRules.ExactPath(path));

        /// <summary>
        /// Create shortcut. The .lnk itself is written into the dotted folder
        /// exactly; a target under a dotted name is refused, because the shell
        /// can only store it without the dot (it refuses the literal path).
        /// </summary>
        private static void Shortcuts(string root, string dotted, string sibling)
        {
            var plainTarget = Path.Combine(sibling, "decoy.txt");
            var inside = Path.Combine(dotted, ShellLink.NameFor(plainTarget));
            ShellLink.CreateAsync(plainTarget, inside).GetAwaiter().GetResult();

            Check("a shortcut saved into a folder ending in a dot is written there",
                File.Exists(NameRules.ExactPath(inside)));
            Check("and not into the sibling without the dot",
                !File.Exists(Path.Combine(sibling, ShellLink.NameFor(plainTarget))));
            var pointsAt = ShellLink.TargetOfAsync(inside).GetAwaiter().GetResult();
            Check("and it reads back from there with its target",
                string.Equals(pointsAt, plainTarget, StringComparison.OrdinalIgnoreCase), pointsAt);

            foreach (var target in new[] { Path.Combine(dotted, "real.txt"), dotted })
            {
                var link = Path.Combine(root, ShellLink.NameFor(target));
                string? refusal = null;
                try { ShellLink.CreateAsync(target, link).GetAwaiter().GetResult(); }
                catch (NotSupportedException ex) { refusal = ex.Message; }

                Check($"a shortcut to {Path.GetFileName(target)} under a dotted name is refused in words, not pointed at the sibling",
                    refusal != null && refusal.Contains("dot") && !File.Exists(NameRules.ExactPath(link)),
                    refusal ?? (File.Exists(NameRules.ExactPath(link)) ? ShellLink.TargetOfAsync(link).GetAwaiter().GetResult() : "nothing thrown"));
            }
        }

        /// <summary>An archive whose name says nothing is known by its bytes, read through the exact path.</summary>
        private static void Identify(string dotted)
        {
            var zip = Path.Combine(dotted, "named.zip");
            var made = ArchiveEngine.CompressAsync(new[] { Path.Combine(dotted, "real.txt") }, zip,
                ArchiveFormats.Zip, ArchiveLevel.Fastest, 1, null, CancellationToken.None).GetAwaiter().GetResult();
            Check("a zip is written into a folder ending in a dot", ExistsExactly(zip) && made.Failed == 0,
                string.Join("; ", made.Errors));

            var blob = Path.Combine(dotted, "blob");
            File.Copy(NameRules.ExactPath(zip), NameRules.ExactPath(blob));
            Check("an archive with no extension in a dotted folder is still recognised by its bytes",
                ArchiveFormats.Identify(blob) == ArchiveFormats.Zip,
                ArchiveFormats.Identify(blob)?.Id ?? "null");
        }

        /// <summary>Compress from the dotted folder, and into it, in each engine.</summary>
        private static void Compress(string root, string dotted, string sibling)
        {
            foreach (var format in Formats())
            {
                var outside = Path.Combine(root, "from" + format.Extension);
                bool ours = format.Engine != ArchiveEngineKind.BsdTar;
                if (ours)
                {
                    var from = ArchiveEngine.CompressAsync(new[] { dotted }, outside, format,
                        ArchiveLevel.Fastest, 1, null, CancellationToken.None).GetAwaiter().GetResult();
                    Check($"{format.Extension}: the dotted folder compresses with its own file",
                        from.Failed == 0 && File.Exists(outside) &&
                        Listed(outside).Any(n => n.EndsWith("real.txt")) && !Listed(outside).Any(n => n.EndsWith("decoy.txt")),
                        string.Join("; ", from.Errors.Concat(File.Exists(outside) ? Listed(outside) : Array.Empty<string>())));
                }
                else
                {
                    // bsdtar is handed the folder as a junction, and the file by name.
                    var from = ArchiveEngine.CompressAsync(new[] { Path.Combine(dotted, "real.txt") }, outside, format,
                        ArchiveLevel.Fastest, 1, null, CancellationToken.None).GetAwaiter().GetResult();
                    Check($"{format.Extension}: a file inside the dotted folder compresses",
                        from.Failed == 0 && File.Exists(outside), string.Join("; ", from.Errors));

                    // The dotted folder itself it can only be given by name, which
                    // it reads without the dot: refused, in words.
                    string? refusal = null;
                    try
                    {
                        ArchiveEngine.CompressAsync(new[] { dotted }, Path.Combine(root, "whole" + format.Extension), format,
                            ArchiveLevel.Fastest, 1, null, CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (NotSupportedException ex) { refusal = ex.Message; }
                    Check($"{format.Extension}: the dotted folder itself is refused in words, not read as the sibling",
                        refusal != null && refusal.Contains("dot") && !File.Exists(Path.Combine(root, "whole" + format.Extension)),
                        refusal ?? "nothing thrown");
                }

                var into = Path.Combine(dotted, "into" + format.Extension);
                var made = ArchiveEngine.CompressAsync(new[] { Path.Combine(sibling, "decoy.txt") }, into, format,
                    ArchiveLevel.Fastest, 1, null, CancellationToken.None).GetAwaiter().GetResult();
                Check($"{format.Extension}: an archive is written into the dotted folder",
                    made.Failed == 0 && ExistsExactly(into) && !File.Exists(Path.Combine(sibling, "into" + format.Extension)),
                    string.Join("; ", made.Errors));

                // Made a second time over the first: the replace path, staged
                // beside it and moved into place.
                var again = ArchiveEngine.CompressAsync(new[] { Path.Combine(sibling, "decoy.txt") }, into, format,
                    ArchiveLevel.Fastest, 1, null, CancellationToken.None).GetAwaiter().GetResult();
                Check($"{format.Extension}: and replaced there",
                    again.Failed == 0 && !again.Cancelled && ExistsExactly(into),
                    string.Join("; ", again.Errors));
            }
        }

        /// <summary>Extract an archive that sits in the dotted folder, into the dotted folder.</summary>
        private static void Extract(string root, string dotted, string sibling)
        {
            foreach (var format in Formats())
            {
                var source = Path.Combine(root, "x" + format.Extension);
                ArchiveEngine.CompressAsync(new[] { Path.Combine(sibling, "decoy.txt") }, source, format,
                    ArchiveLevel.Fastest, 1, null, CancellationToken.None).GetAwaiter().GetResult();
                var inDotted = Path.Combine(dotted, "x" + format.Extension);
                File.Copy(source, NameRules.ExactPath(inDotted), overwrite: true);

                // Enter on it: browsed as a folder, and one file taken out to open.
                try
                {
                    var listing = ArchiveBrowser.Open(inDotted, null, CancellationToken.None);
                    var opened = ArchiveBrowser.ExtractOne(listing, "decoy.txt", null, CancellationToken.None);
                    Check($"{format.Extension}: an archive in a dotted folder opens as a folder, and a file comes out of it",
                        listing.Find("decoy.txt") != null && File.ReadAllText(opened).StartsWith("the sibling's file"), opened);
                }
                catch (Exception ex)
                {
                    Check($"{format.Extension}: an archive in a dotted folder opens as a folder, and a file comes out of it",
                        false, ex.GetType().Name + ": " + ex.Message);
                }

                var destination = Path.Combine(dotted, "out" + format.Id);
                var result = ArchiveEngine.ExtractAsync(inDotted, destination, PasteConflictPolicy.AutoRename, null,
                    1, null, CancellationToken.None).GetAwaiter().GetResult();
                Check($"{format.Extension}: an archive in a dotted folder extracts into it",
                    result.Failed == 0 && !result.Cancelled && File.Exists(NameRules.ExactPath(Path.Combine(destination, "decoy.txt"))) &&
                    !Directory.Exists(Path.Combine(sibling, "out" + format.Id)),
                    string.Join("; ", result.Errors));

                // Straight into the dotted folder, where a file of that name is
                // already waiting: the clash has to be seen there, not in the sibling.
                File.WriteAllText(NameRules.ExactPath(Path.Combine(dotted, "decoy.txt")), "already here");
                var clash = ArchiveEngine.ExtractAsync(source, dotted, PasteConflictPolicy.AutoRename, null,
                    1, null, CancellationToken.None).GetAwaiter().GetResult();
                Check($"{format.Extension}: extracting into the dotted folder keeps what was there and renames the arrival",
                    clash.Failed == 0 &&
                    File.ReadAllText(NameRules.ExactPath(Path.Combine(dotted, "decoy.txt"))) == "already here" &&
                    File.Exists(NameRules.ExactPath(Path.Combine(dotted, "decoy (2).txt"))),
                    string.Join("; ", clash.Errors));
                try { File.Delete(NameRules.ExactPath(Path.Combine(dotted, "decoy.txt"))); } catch { }
                try { File.Delete(NameRules.ExactPath(Path.Combine(dotted, "decoy (2).txt"))); } catch { }
            }
        }

        /// <summary>The dotted folder's size is its own, and a dotted subfolder is walked into.</summary>
        private static void FolderSize(string root, string dotted)
        {
            var sized = Path.Combine(root, "sized.");
            Directory.CreateDirectory(NameRules.ExactPath(Path.Combine(sized, "inner.")));
            File.WriteAllBytes(NameRules.ExactPath(Path.Combine(sized, "a.bin")), new byte[100]);
            File.WriteAllBytes(NameRules.ExactPath(Path.Combine(sized, "inner.", "b.bin")), new byte[23]);
            Directory.CreateDirectory(Path.Combine(root, "sized", "inner"));
            File.WriteAllBytes(Path.Combine(root, "sized", "decoy.bin"), new byte[5000]);

            using var sizes = new FolderSizeCalculator();
            var result = sizes.CalculateAsync(sized).GetAwaiter().GetResult();
            Check("a folder ending in a dot is measured, dotted subfolder included",
                result.Complete && result.Bytes == 123 && result.Files == 2 && result.Folders == 1,
                $"{result.Bytes} bytes, {result.Files} files, {result.Folders} folders, complete {result.Complete}");

            var again = sizes.CalculateAsync(sized).GetAwaiter().GetResult();
            Check("and measured the same from the cache", again.Bytes == 123, again.Bytes.ToString());
        }

        /// <summary>A folder has no extension, so "Backup." compressed is "Backup..zip", not "Backup.zip".</summary>
        private static void SuggestedName(string root)
        {
            // No sibling "Solo": asked plainly, the folder is not there and was
            // taken for a file, whose "extension" (the dot) was stripped.
            var solo = Path.Combine(root, "Solo.");
            Directory.CreateDirectory(NameRules.ExactPath(solo));
            var name = ArchiveFormats.SuggestedName(new[] { solo }, ArchiveFormats.Zip);
            Check("the suggested name keeps a dotted folder's whole name", name == "Solo..zip", name);
        }

        private static void SourceRules()
        {
            var form = Source("ArchiveForm.cs");
            Check("Compress's \"already here\" question asks about the exact path",
                form != null && form.Contains("NameRules.ExactPath(full)"));

            // Opening "Backup." from the shell verb or a command line: checked
            // exactly, and never through GetFullPath, which drops the dot.
            var program = Source("Program.cs");
            int at = program?.IndexOf("FirstPathArgument(string[] args)", StringComparison.Ordinal) ?? -1;
            var body = at < 0 ? "" : program![at..Math.Min(program.Length, at + 1500)];
            Check("a launch path is checked exactly and kept as given when it needs that",
                body.Contains("NameRules.ExactPath(candidate)") && body.Contains("exact != candidate ? candidate"));
        }

        /// <summary>Our own zip and tar.gz writers, and one the system tar does when it is here.</summary>
        private static ArchiveFormat[] Formats() => BsdTar.Available
            ? new[] { ArchiveFormats.Zip, ArchiveFormats.TarGz, ArchiveFormats.TarXz }
            : new[] { ArchiveFormats.Zip, ArchiveFormats.TarGz };

        private static string[] Listed(string archive)
        {
            try
            {
                var entries = archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? ZipEngine.List(archive, CancellationToken.None)
                    : TarEngine.List(archive, ArchiveFormats.TarGz, CancellationToken.None);
                return entries.Select(e => e.Name).ToArray();
            }
            catch (Exception ex) { return new[] { "listing failed: " + ex.Message }; }
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }
}
