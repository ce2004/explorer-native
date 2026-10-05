using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The odd cases a review went looking for: names and archives that are
    /// legal and unusual, and what used to happen to them.
    /// </summary>
    internal static class OddCaseTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Odd cases:");

            NameTests();
            StretchReachTests();
            await SameNamePasteTests();
            await ArchiveEntryTests();
            await ArchiveWritingTests();
            ReloadOfUnreadableFileTests();
            LinkTests();
            MangledNameTests();

            Console.WriteLine();
        }

        /// <summary>
        /// A reload from the command line that finds the file unreadable keeps
        /// what the running copy has, rather than taking the defaults as though
        /// the file had asked for them.
        /// </summary>
        private static void ReloadOfUnreadableFileTests()
        {
            var dir = NewDir();
            var previous = Settings.OverrideAppDataDir;
            try
            {
                Settings.OverrideAppDataDir = dir;
                var live = new Settings { AudioVolumePercent = 37, ShowHiddenFiles = !new Settings().ShowHiddenFiles };
                live.Save();
                _ = Settings.Load();

                File.WriteAllText(Path.Combine(dir, "settings.json"), "{{{ not json at all");
                var reloaded = Settings.ReloadOnto(live);

                Check("a reload of an unreadable file keeps the live settings",
                    reloaded.AudioVolumePercent == 37 && reloaded.ShowHiddenFiles == live.ShowHiddenFiles,
                    $"volume {reloaded.AudioVolumePercent}");
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                Cleanup(dir);
            }
        }

        /// <summary>
        /// A name the system tar printed through a code page that could not
        /// show it still finds the file it was about, and nothing else.
        /// </summary>
        private static void MangledNameTests()
        {
            var one = BsdTar.MangledPattern("Caf?-Bar.txt");
            var run = BsdTar.MangledPattern("Caf�Bar.txt");
            Check("a question mark stands for a character the console could not show",
                one.IsMatch("Café-Bar.txt"));
            Check("and a replacement character for a run of them",
                run.IsMatch("Café–Bar.txt"));
            Check("but not for plain letters", !one.IsMatch("Cafe-Bar.txt"));
            Check("nor for a different name", !run.IsMatch("CaféBaz.txt"));

            // Two code-page bytes that happen to decode as a real character.
            var decoded = BsdTar.MangledPattern("CafÃ©.txt");
            Check("a character decoded from the wrong code page still finds its file",
                decoded.IsMatch("Café.txt"));
            Check("and not a file named in plain letters", !decoded.IsMatch("Cafe.txt"));
        }

        /// <summary>
        /// Only a link is a link. A plain file and folder are not, and a
        /// junction is — the walks that skip links must not skip anything else.
        /// </summary>
        private static void LinkTests()
        {
            var dir = NewDir();
            try
            {
                var file = Path.Combine(dir, "plain.txt");
                File.WriteAllText(file, "x");
                var folder = Directory.CreateDirectory(Path.Combine(dir, "plain"));
                Check("a plain file is not a link", !NameRules.IsLink(new FileInfo(file)));
                Check("a plain folder is not a link", !NameRules.IsLink(folder));

                var junction = Path.Combine(dir, "junction");
                var made = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "cmd.exe", $"/c mklink /J \"{junction}\" \"{folder.FullName}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                });
                made?.WaitForExit(10_000);
                if (Directory.Exists(junction))
                    Check("a junction is a link", NameRules.IsLink(new DirectoryInfo(junction)));
            }
            finally
            {
                // The junction first, so the delete does not follow it.
                try { Directory.Delete(Path.Combine(dir, "junction")); } catch { }
                Cleanup(dir);
            }
        }

        private static string NewDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-edge-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Directory.Delete(dir, true);
            }
            catch { }
        }

        // ---------- names ----------

        private static void NameTests()
        {
            var sanitised = DriveMount.Sanitise("notes.txt .");
            Check("a Drive name ending in a space before a dot ends in neither",
                !sanitised.EndsWith(' ') && !sanitised.EndsWith('.') && NameRules.IsUsableName(sanitised),
                $"\"{sanitised}\"");

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DriveMount.Unique(".env", used);
            Equal("a second dotfile on Drive is numbered at its end", ".env (2)", DriveMount.Unique(".env", used));

            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DriveMount.Unique("Album.2024", folders, folder: true);
            Equal("a second folder with a dot in its name is numbered whole", "Album.2024 (2)",
                DriveMount.Unique("Album.2024", folders, folder: true));

            // An emoji straddling the 250-character cut.
            var longName = new string('a', 249) + "😀" + new string('b', 20);
            var cut = DriveMount.Sanitise(longName);
            Check("shortening a long Drive name never leaves half an emoji",
                cut.Length > 0 && !char.IsHighSurrogate(cut[^1]) && !cut.Any(c => char.IsLowSurrogate(c)
                    && cut.IndexOf(c) > 0 && !char.IsHighSurrogate(cut[cut.IndexOf(c) - 1])),
                $"{cut.Length} characters");

            Check("a name ending in a dot needs the literal path", NameRules.NeedsLiteralPath(@"C:\x\report."));
            Check("and one ending in a space", NameRules.NeedsLiteralPath(@"C:\x\report "));
            Check("an ordinary one does not", !NameRules.NeedsLiteralPath(@"C:\x\report.txt"));
            Equal("a drive path is prefixed", @"\\?\C:\x\report.", NameRules.LiteralPath(@"C:\x\report."));
            Equal("a share path goes through UNC", @"\\?\UNC\nas\music\a.", NameRules.LiteralPath(@"\\nas\music\a."));

            // The premise: the literal path reaches "report." and leaves "report".
            var dir = NewDir();
            try
            {
                var plain = Path.Combine(dir, "report");
                var dotted = Path.Combine(dir, "report.");
                File.WriteAllText(plain, "plain");
                File.WriteAllText(NameRules.LiteralPath(dotted), "dotted");

                File.Delete(NameRules.LiteralPath(dotted));
                Check("deleting the literal path removes the dotted file only",
                    File.Exists(plain) && !File.Exists(NameRules.LiteralPath(dotted)));

                // A new file beside a folder of the same name is numbered as a file.
                Directory.CreateDirectory(Path.Combine(dir, "notes.txt"));
                Equal("a file beside a folder of its name is numbered as a file",
                    Path.Combine(dir, "notes (2).txt"),
                    FileOperations.UniqueName(Path.Combine(dir, "notes.txt"), folder: false));
            }
            finally { Cleanup(dir); }
        }

        // ---------- speed over pitch ----------

        private static void StretchReachTests()
        {
            // Four times the speed two octaves down is a stretch of sixteen; the
            // stretcher stopped at four and played it at normal speed.
            const int channels = 1, rate = 48000;
            long consumed = 0;
            var stretch = new TimeStretch(channels)
            {
                Rate = 16.0,
                Source = (into, frames) =>
                {
                    for (int i = 0; i < frames; i++)
                        into[i] = (float)Math.Sin(2 * Math.PI * 440 * (consumed + i) / rate) * 0.5f;
                    consumed += frames;
                    return frames;
                },
            };

            var output = new float[4096];
            long produced = 0;
            for (int i = 0; i < 200; i++) produced += stretch.Read(output, output.Length);

            double ratio = consumed / (double)Math.Max(1, produced);
            Check($"a stretch of sixteen consumes sixteen times what it plays ({ratio:0.0})",
                ratio > 14 && ratio < 18);
        }

        // ---------- two sources with one name ----------

        private static async Task SameNamePasteTests()
        {
            foreach (bool move in new[] { false, true })
            {
                var dir = NewDir();
                try
                {
                    var a = Path.Combine(dir, "a");
                    var b = Path.Combine(dir, "b");
                    var dest = Path.Combine(dir, "dest");
                    Directory.CreateDirectory(a);
                    Directory.CreateDirectory(b);
                    Directory.CreateDirectory(dest);
                    File.WriteAllText(Path.Combine(a, "song.txt"), "from a");
                    File.WriteAllText(Path.Combine(b, "song.txt"), "from b, longer");

                    var result = await RoboCopyEngine.RunAsync(
                        new[] { Path.Combine(a, "song.txt"), Path.Combine(b, "song.txt") },
                        dest, move, 4, PasteConflictPolicy.AutoRename, null, CancellationToken.None);

                    var landed = Directory.GetFiles(dest).Select(File.ReadAllText).OrderBy(x => x).ToList();
                    Equal($"two sources with one name both arrive ({(move ? "move" : "copy")})",
                        "from a|from b, longer", string.Join("|", landed));
                    if (move)
                        Check("and a move removes both originals only once both have landed",
                            !File.Exists(Path.Combine(a, "song.txt")) && !File.Exists(Path.Combine(b, "song.txt")));
                    Equal("and nothing failed", "0", result.Failed.ToString());
                }
                finally { Cleanup(dir); }
            }

            // Folders too, under the policy that replaces what is already there:
            // the second folder is not what was already there.
            var folders = NewDir();
            try
            {
                var a = Path.Combine(folders, "a", "photos");
                var b = Path.Combine(folders, "b", "photos");
                var dest = Path.Combine(folders, "dest");
                Directory.CreateDirectory(a);
                Directory.CreateDirectory(b);
                Directory.CreateDirectory(dest);
                File.WriteAllText(Path.Combine(a, "one.txt"), "a");
                File.WriteAllText(Path.Combine(b, "one.txt"), "b");

                await RoboCopyEngine.RunAsync(new[] { a, b }, dest, false, 4,
                    PasteConflictPolicy.Overwrite, null, CancellationToken.None);

                var made = Directory.GetDirectories(dest).Select(Path.GetFileName).OrderBy(x => x).ToList();
                Equal("two folders with one name stay two folders", "photos|photos (2)", string.Join("|", made));
            }
            finally { Cleanup(folders); }
        }

        // ---------- what an archive may hold ----------

        private static string MakeZip(string path, params (string Name, string Text)[] entries)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (name, text) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(text);
            }
            return path;
        }

        private static async Task ArchiveEntryTests()
        {
            var dir = NewDir();
            try
            {
                // ".." is refused, never renamed into ".. (2)".
                var escaping = MakeZip(Path.Combine(dir, "escape.zip"),
                    ("../evil.txt", "evil"), ("good.txt", "good"));
                var into = Path.Combine(dir, "into");
                Directory.CreateDirectory(into);
                var result = await ArchiveEngine.ExtractAsync(escaping, into,
                    PasteConflictPolicy.AutoRename, null, 2, null, CancellationToken.None);
                Check("an entry climbing out of the folder is refused",
                    !File.Exists(Path.Combine(dir, "evil.txt")) &&
                    !Directory.EnumerateFileSystemEntries(into).Any(e => Path.GetFileName(e).StartsWith("..")),
                    string.Join(", ", Directory.EnumerateFileSystemEntries(into).Select(Path.GetFileName)));
                Check("and said so, while the rest came out",
                    result.Failed >= 1 && File.Exists(Path.Combine(into, "good.txt")),
                    string.Join("; ", result.Errors));

                // Two names that are one name on Windows.
                var twice = MakeZip(Path.Combine(dir, "twice.zip"), ("Read.txt", "first"), ("read.txt", "second"));
                var twiceOut = Path.Combine(dir, "twice-out");
                var both = await ArchiveEngine.ExtractAsync(twice, twiceOut,
                    PasteConflictPolicy.AutoRename, null, 4, null, CancellationToken.None);
                var files = Directory.GetFiles(twiceOut);
                Check("names that differ only in capitals give one file and a complaint",
                    files.Length == 1 && both.Failed == 1 && File.ReadAllText(files[0]) == "first",
                    $"{files.Length} files, {both.Failed} failed");

                // A name close to the limit, which the temporary name used to push past it.
                var longName = new string('n', 236) + ".txt";
                var longZip = MakeZip(Path.Combine(dir, "long.zip"), (longName, "long"));
                var longOut = Path.Combine(dir, "l");
                var longResult = await ArchiveEngine.ExtractAsync(longZip, longOut,
                    PasteConflictPolicy.AutoRename, null, 2, null, CancellationToken.None);
                Check("a 240-character name extracts",
                    longResult.Failed == 0 && File.Exists(Path.Combine(longOut, longName)),
                    string.Join("; ", longResult.Errors));

                // Replace means replace, a read-only file included.
                var replaceOut = Path.Combine(dir, "replace");
                Directory.CreateDirectory(replaceOut);
                var old = Path.Combine(replaceOut, "x.txt");
                File.WriteAllText(old, "old");
                File.SetAttributes(old, FileAttributes.ReadOnly);
                var fresh = MakeZip(Path.Combine(dir, "fresh.zip"), ("x.txt", "new"));
                var replaced = await ArchiveEngine.ExtractAsync(fresh, replaceOut,
                    PasteConflictPolicy.Overwrite, null, 2, null, CancellationToken.None);
                Equal("replacing a read-only file replaces it", "new", File.ReadAllText(old));
                Equal("without a failure", "0", replaced.Failed.ToString());
            }
            finally { Cleanup(dir); }
        }

        private static async Task ArchiveWritingTests()
        {
            var dir = NewDir();
            try
            {
                // An empty file is still a gzip file.
                var empty = Path.Combine(dir, "empty.txt");
                File.WriteAllBytes(empty, Array.Empty<byte>());
                var gz = Path.Combine(dir, "empty.txt.gz");
                await ArchiveEngine.CompressAsync(new[] { empty }, gz, ArchiveFormats.Gz,
                    ArchiveLevel.Balanced, 2, null, CancellationToken.None);
                bool readable;
                try
                {
                    using var input = new GZipStream(File.OpenRead(gz), CompressionMode.Decompress);
                    readable = input.ReadByte() == -1 && new FileInfo(gz).Length > 0;
                }
                catch { readable = false; }
                Check("an empty file compresses to a gzip file that opens", readable,
                    new FileInfo(gz).Length + " bytes");

                // A file dated before 1970 is still put in a tar.
                var old = Path.Combine(dir, "old.txt");
                File.WriteAllText(old, "from a camera with a dead clock");
                File.SetLastWriteTime(old, new DateTime(1960, 1, 1));
                var tgz = Path.Combine(dir, "old.tar.gz");
                var made = await ArchiveEngine.CompressAsync(new[] { old }, tgz, ArchiveFormats.TarGz,
                    ArchiveLevel.Balanced, 2, null, CancellationToken.None);
                Check("a file older than 1970 goes into a tar",
                    made.Failed == 0 && TarEngine.List(tgz, ArchiveFormats.TarGz, CancellationToken.None).Count == 1,
                    string.Join("; ", made.Errors));

                if (!BsdTar.Available) return;

                // Replacing a .txz, which the staging name used to hide from bsdtar.
                var txz = Path.Combine(dir, "again.txz");
                await ArchiveEngine.CompressAsync(new[] { old }, txz, ArchiveFormats.TarXz,
                    ArchiveLevel.Fastest, 2, null, CancellationToken.None);
                var second = await ArchiveEngine.CompressAsync(new[] { old }, txz, ArchiveFormats.TarXz,
                    ArchiveLevel.Fastest, 2, null, CancellationToken.None);
                Check("an existing .txz can be replaced", second.Failed == 0 && File.Exists(txz),
                    string.Join("; ", second.Errors));

                // Two chosen items with one name cannot both go into a .tar.xz.
                var x1 = Path.Combine(dir, "one");
                var x2 = Path.Combine(dir, "two");
                Directory.CreateDirectory(x1);
                Directory.CreateDirectory(x2);
                File.WriteAllText(Path.Combine(x1, "x.txt"), "1");
                File.WriteAllText(Path.Combine(x2, "x.txt"), "2");
                string? said = null;
                try
                {
                    await ArchiveEngine.CompressAsync(new[] { Path.Combine(x1, "x.txt"), Path.Combine(x2, "x.txt") },
                        Path.Combine(dir, "clash.tar.xz"), ArchiveFormats.TarXz, ArchiveLevel.Fastest, 2, null,
                        CancellationToken.None);
                }
                catch (NotSupportedException ex) { said = ex.Message; }
                Check("two items with one name are refused for a .tar.xz, with the way round it",
                    said != null && said.Contains("x.txt") && said.Contains("Zip"), said);
            }
            finally { Cleanup(dir); }
        }
    }
}
