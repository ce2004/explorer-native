using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of archives opened as folders and the tar reader.
    /// </summary>
    internal static class Sweep500Archive2Tests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, archives opened as folders and the tar reader:");
            TreeWithWriteFailuresIsPartialNotKept();
            IncrementalTarFoldersAreFolders();
            ReadFailureIsNotDamage("tar");
            ReadFailureIsNotDamage("tar.gz");
            await Task.CompletedTask;
        }

        /// <summary>Eight 300KB files, a 12MB one (past the inline limit, so it goes through a pipe), then four more.</summary>
        private static void WriteMixedTar(string path, bool gzip)
        {
            var rng = new Random(500);
            using var file = File.Create(path);
            using Stream outer = gzip ? new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest) : file;
            using var writer = new TarWriter(outer, TarEntryFormat.Pax, leaveOpen: true);
            void Add(string name, int size)
            {
                var data = new byte[size];
                rng.NextBytes(data);
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
            }
            for (int i = 0; i < 8; i++) Add($"a{i}.bin", 300 * 1024);
            Add("big.bin", 12 * 1024 * 1024);
            for (int i = 0; i < 4; i++) Add($"z{i}.bin", 300 * 1024);
        }

        // Bug 3: a share that went away (here, a range another handle locked)
        // was said as "This archive is damaged or incomplete".
        private static void ReadFailureIsNotDamage(string id)
        {
            var dir = NewTemp("readfail");
            try
            {
                var path = Path.Combine(dir, "backup." + id);
                WriteMixedTar(path, gzip: id == "tar.gz");
                long len = new FileInfo(path).Length;
                using (var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    holder.Lock(len / 2, len - len / 2);

                    var dest = Path.Combine(dir, "out");
                    Directory.CreateDirectory(dest);
                    var errors = new List<string>();
                    int done = 0;
                    try
                    {
                        done = TarEngine.Extract(path, Format(id), ExtractionRules.Selecting(dest, n => n), 2,
                            new ArchiveEngine.Reporter(null), errors, CancellationToken.None);
                    }
                    catch (Exception ex) { errors.Add("threw " + ex.GetType().Name + ": " + ex.Message); }
                    var said = string.Join(" | ", errors);
                    Check($"archives2 read failure ({id}): extracting says the archive could not be read",
                        errors.Any(e => e.Contains("could not be read", StringComparison.OrdinalIgnoreCase) &&
                                        e.Contains("locked", StringComparison.OrdinalIgnoreCase)), said);
                    Check($"archives2 read failure ({id}): the reason does not repeat the archive's path",
                        !said.Contains(dir, StringComparison.OrdinalIgnoreCase), said);
                    Check($"archives2 read failure ({id}): extracting does not call it damaged",
                        !said.Contains("damaged", StringComparison.OrdinalIgnoreCase), said);
                    Check($"archives2 read failure ({id}): what came before was saved", done > 0 && File.Exists(Path.Combine(dest, "a0.bin")),
                        done.ToString());

                    string opened;
                    try
                    {
                        var listing = ArchiveBrowser.Open(path, null, CancellationToken.None);
                        opened = listing.Damaged ? "listed as damaged" : "listed whole";
                    }
                    catch (InvalidDataException ex) { opened = "damaged: " + ex.Message; }
                    catch (IOException ex) { opened = "io: " + ex.Message; }
                    Check($"archives2 read failure ({id}): browsing says the read failed, not damage",
                        opened.StartsWith("io: ", StringComparison.Ordinal) && opened.Contains("locked", StringComparison.OrdinalIgnoreCase),
                        opened);

                    holder.Unlock(len / 2, len - len / 2);
                }

                // The control: a truncated copy is still damage.
                var cut = Path.Combine(dir, "cut." + id);
                var bytes = File.ReadAllBytes(path);
                File.WriteAllBytes(cut, bytes.AsSpan(0, bytes.Length / 2).ToArray());
                var dest2 = Path.Combine(dir, "out2");
                Directory.CreateDirectory(dest2);
                var errors2 = new List<string>();
                TarEngine.Extract(cut, Format(id), ExtractionRules.Selecting(dest2, n => n), 2,
                    new ArchiveEngine.Reporter(null), errors2, CancellationToken.None);
                Check($"archives2 read failure ({id}): a truncated archive is still damaged",
                    errors2.Any(e => e.StartsWith("This archive is damaged or incomplete", StringComparison.Ordinal)),
                    string.Join(" | ", errors2));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static ArchiveFormat Format(string id) => ArchiveFormats.All.First(f => f.Id == id);

        private static string NewTemp(string what)
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-sweep500a2-" + what + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Changes one header's type flag and puts its checksum right, for types TarWriter will not write.</summary>
        private static void Retype(byte[] tar, string name, char type)
        {
            for (int at = 0; at + 512 <= tar.Length; at += 512)
            {
                int len = Array.IndexOf(tar, (byte)0, at, 100) - at;
                if (len <= 0 || System.Text.Encoding.ASCII.GetString(tar, at, len) != name) continue;
                tar[at + 156] = (byte)type;
                for (int i = 148; i < 156; i++) tar[at + i] = (byte)' ';
                int sum = 0;
                for (int i = 0; i < 512; i++) sum += tar[at + i];
                var octal = System.Text.Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0'));
                Array.Copy(octal, 0, tar, at + 148, 6);
                tar[at + 154] = 0;
                tar[at + 155] = (byte)' ';
                return;
            }
            throw new InvalidOperationException("no header " + name);
        }

        // Bug 2: GNU incremental tar's dumpdir folders ('D') were files that hid
        // everything under them, and a volume label ('V') was a file that failed.
        private static void IncrementalTarFoldersAreFolders()
        {
            var ms = new MemoryStream();
            using (var writer = new TarWriter(ms, TarEntryFormat.Gnu, leaveOpen: true))
            {
                writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "label"));
                writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "home/"));
                writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "home/empty/"));
                writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "home/conner/notes.txt")
                    { DataStream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("hello notes")) });
            }
            var bytes = ms.ToArray();
            Retype(bytes, "label", 'V');
            Retype(bytes, "home/", 'D');
            Retype(bytes, "home/empty/", 'D');

            var index = TarEngine.ReadIndex(new MemoryStream(bytes), CancellationToken.None);
            Check("archives2 dumpdir: a 'D' entry is listed as a folder",
                index.Any(e => e.Name == "home/" && e.IsDirectory),
                string.Join(", ", index.Select(e => $"{e.Name}={e.IsDirectory}")));
            Check("archives2 dumpdir: a volume label is not listed", index.All(e => e.Name != "label"));

            var dir = NewTemp("dumpdir");
            try
            {
                var path = Path.Combine(dir, "home.tar");
                File.WriteAllBytes(path, bytes);
                var listing = ArchiveListing.Build(path, Format("tar"), null, path, index, CancellationToken.None);
                Check("archives2 dumpdir: the browsed folder is a folder", listing.IsFolder("home"));
                Check("archives2 dumpdir: files under it are shown", listing.Find("home/conner/notes.txt") != null);
                Equal("archives2 dumpdir: nothing is hidden as unsafe", "0", listing.Unsafe.ToString());

                var dest = Path.Combine(dir, "out");
                Directory.CreateDirectory(dest);
                var errors = new List<string>();
                TarEngine.Extract(path, Format("tar"), ExtractionRules.Selecting(dest, n => n), 2,
                    new ArchiveEngine.Reporter(null), errors, CancellationToken.None);
                Equal("archives2 dumpdir: extracting reports no failures", "", string.Join(" | ", errors));
                Check("archives2 dumpdir: an empty dumpdir folder is made", Directory.Exists(Path.Combine(dest, "home", "empty")));
                Check("archives2 dumpdir: the file arrives", File.Exists(Path.Combine(dest, "home", "conner", "notes.txt")));
                Check("archives2 dumpdir: no file is made for the volume label", !File.Exists(Path.Combine(dest, "label")));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static string SourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("src not found above the test exe");
        }

        // Bug 1: files that could not be written into the browse cache left an
        // incomplete tree kept as whole, or a full disk under 7-Zip kept as damaged.
        private static void TreeWithWriteFailuresIsPartialNotKept()
        {
            var write = ArchiveBrowser.JudgeTree(new[] { "big.bin: Write failed", "other.bin: Write failed" });
            Check("archives2 tree: a failed write makes the tree partial", write.Lost != null && !write.Damaged,
                $"{write.Damaged} {write.Lost}");
            Check("archives2 tree: the partial reason counts the files", write.Lost?.Contains("2 items") == true,
                write.Lost);

            var space = ArchiveBrowser.JudgeTree(new[]
            {
                "a.bin: the archive is damaged at this point",
                "There isn't enough space on the disk",
            });
            Check("archives2 tree: 7-Zip short of space is not damage", !space.Damaged);
            Equal("archives2 tree: 7-Zip short of space says so", "There isn't enough space on the disk", space.Lost ?? "");

            var named = ArchiveBrowser.JudgeTree(new[] { "a.bin: the archive is damaged at this point", "Cannot open output file : Access is denied." });
            Check("archives2 tree: a file 7-Zip named without damage is lost, not damage", !named.Damaged && named.Lost != null);

            foreach (var note in new[]
                     {
                         "link: a symbolic link was not extracted",
                         "2 items in the archive repeated a name already used (names that differ only in capitals are one name here); the first of each was kept",
                         "Damaged photos/link: a symbolic link was not extracted",
                     })
            {
                var j = ArchiveBrowser.JudgeTree(new[] { note });
                Check($"archives2 tree: a note keeps the tree whole and undamaged ({note[..12]})", !j.Damaged && j.Lost == null,
                    $"{j.Damaged} {j.Lost}");
            }

            var real = ArchiveBrowser.JudgeTree(new[] { "f: the archive is damaged at this point", ArchiveEngine.DamagedMessage(3) });
            Check("archives2 tree: real damage is still damage", real.Damaged && real.Lost == null);
            Check("archives2 tree: damage with nothing extracted is damage",
                ArchiveBrowser.JudgeTree(new[] { ArchiveEngine.DamagedMessage(0) }).Damaged);

            var source = File.ReadAllText(Path.Combine(SourceRoot(), "src", "ArchiveBrowser.cs"));
            Check("archives2 tree: the cache marker is written only for a whole tree",
                source.Contains("if (partial == null) File.WriteAllText(finished", StringComparison.Ordinal));
            Check("archives2 tree: a lost file makes the browse partial",
                source.Contains("judged.Lost", StringComparison.Ordinal));
        }
    }
}
