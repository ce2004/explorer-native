using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Archives opened as folders: listing a zip and a .tar.gz made here,
    /// nested folders, the paths used to go in and come out, taking one member
    /// out, refusing names that would land outside, the cache sweep, and the
    /// commands that would change an archive refusing inside one.
    /// </summary>
    internal static class ArchiveBrowserTests
    {
        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            Console.WriteLine("Archives as folders:");

            var work = Path.Combine(Path.GetTempPath(), "en-browse-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);
            var madeInCache = new List<string>();
            try
            {
                Names(check, equal);
                Zip(check, equal, work, madeInCache);
                TarGz(check, equal, work, madeInCache);
                Selection(check, equal, work);
                Damaged(check, work);
                Sweep(check, work);
                await SevenZip(check, equal, work, madeInCache);
                SourceShape(check);
            }
            finally
            {
                foreach (var folder in madeInCache)
                {
                    try { if (Directory.Exists(folder)) ArchiveBrowser.DeleteTree(folder); } catch { }
                    try { if (File.Exists(folder + ".done")) File.Delete(folder + ".done"); } catch { }
                }
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        private static void Names(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            check("a zip opens as a folder", ArchiveBrowser.CanBrowse(@"C:\Music\album.zip"), null);
            check("so does a .tar.gz", ArchiveBrowser.CanBrowse(@"C:\Music\album.tar.gz"), null);
            check("and a .tgz", ArchiveBrowser.CanBrowse(@"C:\Music\album.tgz"), null);
            check("and a 7z", ArchiveBrowser.CanBrowse(@"C:\Music\album.7z"), null);
            check("a bare .gz does not: it holds one file", !ArchiveBrowser.CanBrowse(@"C:\x\song.flac.gz"), null);
            check("an .iso does not: Windows mounts it", !ArchiveBrowser.CanBrowse(@"C:\x\disc.iso"), null);
            check("a text file does not", !ArchiveBrowser.CanBrowse(@"C:\x\notes.txt"), null);

            const string archive = @"C:\Music\album.zip";
            equal("the top of an archive is the archive's own path", archive, ArchiveBrowser.VirtualPath(archive, ""));
            equal("a folder inside reads like a folder", @"C:\Music\album.zip\Disc 1\Sub",
                ArchiveBrowser.VirtualPath(archive, "Disc 1/Sub"));
            equal("and is found again from that path", "Disc 1/Sub",
                ArchiveBrowser.InnerPathOf(archive, @"C:\Music\album.zip\Disc 1\Sub") ?? "(null)");
            equal("the archive itself is its top", "",
                ArchiveBrowser.InnerPathOf(archive, @"C:\MUSIC\album.zip\") ?? "(null)");
            check("the folder it is in is outside it", ArchiveBrowser.InnerPathOf(archive, @"C:\Music") == null, null);
            check("so is a file that only starts with its name",
                ArchiveBrowser.InnerPathOf(archive, @"C:\Music\album.zip2\x") == null, null);

            // Going up: one level inside, and out of the top onto the archive.
            equal("up from a nested folder is its parent", "Disc 1", ArchiveBrowser.ParentOf("Disc 1/Sub"));
            equal("up from a top-level folder is the top", "", ArchiveBrowser.ParentOf("Disc 1"));
            equal("stepping up inside lands on the folder left",
                @"C:\Music\album.zip\Disc 1\Sub",
                NavigationHistory.ChildOnPathTo(@"C:\Music\album.zip\Disc 1", @"C:\Music\album.zip\Disc 1\Sub") ?? "(null)");
            equal("stepping out of the top lands on the archive file", archive,
                NavigationHistory.ChildOnPathTo(@"C:\Music", archive) ?? "(null)");
        }

        private static void Zip(Action<string, bool, string?> check, Action<string, string, string> equal,
            string work, List<string> madeInCache)
        {
            var path = Path.Combine(work, "album.zip");
            var track = Encoding.UTF8.GetBytes(new string('a', 5000));
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Write(zip, "Disc 1/01 one.flac", track);
                Write(zip, "Disc 1/Sub/deep.txt", Encoding.UTF8.GetBytes("deep"));
                Write(zip, "top.txt", Encoding.UTF8.GetBytes("top"));
                zip.CreateEntry("Empty/");
                Write(zip, "../evil.txt", Encoding.UTF8.GetBytes("evil"));
                Write(zip, "C:/abs.txt", Encoding.UTF8.GetBytes("abs"));
            }

            var listing = ArchiveBrowser.Open(path, null, CancellationToken.None);
            equal("a zip is read as a zip", "zip", listing.Format.Id);
            check("and read directly, with nothing extracted", listing.Tree == null, null);

            equal("the top level, folders made from file paths",
                "Disc 1|Empty|top.txt", Names(listing.Children("")));
            equal("a folder inside", "01 one.flac|Sub", Names(listing.Children("Disc 1")));
            equal("a folder inside that", "deep.txt", Names(listing.Children("Disc 1/Sub")));
            check("an empty folder the zip names is there", listing.IsFolder("Empty") && listing.Children("Empty").Count == 0, null);
            check("a file is not a folder", !listing.IsFolder("top.txt"), null);
            equal("a file's size is its uncompressed size", "5000", listing.Find("Disc 1/01 one.flac")?.Size.ToString() ?? "(null)");
            equal("a folder's size is what it holds", "5004", listing.Find("Disc 1")?.Size.ToString() ?? "(null)");
            equal("measured the same way", "5004 2 1", Format(listing.Measure("Disc 1")));

            equal("names that would land outside are not shown, and counted", "2", listing.Unsafe.ToString());
            check("and said", listing.Warning?.Contains("not shown") == true, listing.Warning);

            // One member out, on its own.
            var one = ArchiveBrowser.ExtractOne(listing, "Disc 1/01 one.flac", null, CancellationToken.None);
            madeInCache.Add(Path.GetDirectoryName(one)!);
            equal("one member comes out under its own name", "01 one.flac", Path.GetFileName(one));
            check("into the cache", one.StartsWith(ArchiveBrowser.CacheRoot, StringComparison.OrdinalIgnoreCase), one);
            check("with its contents", File.ReadAllBytes(one).SequenceEqual(track), null);
            check("and nothing else beside it", Directory.GetFileSystemEntries(Path.GetDirectoryName(one)!).Length == 1, null);
            equal("asked for again, it is not taken out twice", one,
                ArchiveBrowser.ExtractOne(listing, "Disc 1/01 one.flac", null, CancellationToken.None));

            // A selection: a folder with what is in it, and a file.
            var staging = Path.Combine(work, "copied");
            var errors = new List<string>();
            var arrived = ArchiveBrowser.ExtractSelection(listing, new[] { "Disc 1/Sub", "top.txt" }, staging,
                null, errors, CancellationToken.None);
            equal("a copy brings each chosen item", "2", arrived.Count.ToString());
            check("a folder with what it holds", File.ReadAllText(Path.Combine(staging, "Sub", "deep.txt")) == "deep", null);
            check("lands relative to where it was", !Directory.Exists(Path.Combine(staging, "Disc 1")), null);
            check("a file", File.ReadAllText(Path.Combine(staging, "top.txt")) == "top", null);
            check("and only what was chosen", !File.Exists(Path.Combine(staging, "01 one.flac")), null);
            check("without complaint", errors.Count == 0, string.Join("; ", errors));
        }

        private static void TarGz(Action<string, bool, string?> check, Action<string, string, string> equal,
            string work, List<string> madeInCache)
        {
            var path = Path.Combine(work, "album.tar.gz");
            var notes = Encoding.UTF8.GetBytes("liner notes");
            using (var file = File.Create(path))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "album/art/"));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "album/notes.txt")
                    { DataStream = new MemoryStream(notes) });
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "album/disc 2/track 1.wav")
                    { DataStream = new MemoryStream(new byte[777]) });
            }

            var listing = ArchiveBrowser.Open(path, null, CancellationToken.None);
            equal("a .tar.gz is read as one", "tar.gz", listing.Format.Id);
            equal("its top level", "album", Names(listing.Children("")));
            equal("its folders, named or not", "art|disc 2|notes.txt", Names(listing.Children("album")));
            equal("a nested folder", "track 1.wav", Names(listing.Children("album/disc 2")));
            check("nothing hidden, nothing damaged", listing.Unsafe == 0 && !listing.Damaged, listing.Warning);

            var one = ArchiveBrowser.ExtractOne(listing, "album/notes.txt", null, CancellationToken.None);
            madeInCache.Add(Path.GetDirectoryName(one)!);
            check("one member of a .tar.gz comes out", File.ReadAllBytes(one).SequenceEqual(notes), one);
            check("and only it", Directory.GetFileSystemEntries(Path.GetDirectoryName(one)!).Length == 1, null);
        }

        /// <summary>Zip slip, through the selection map and through a real archive.</summary>
        private static void Selection(Action<string, bool, string?> check, Action<string, string, string> equal, string work)
        {
            var map = ArchiveBrowser.SelectionMap(new[] { "Disc 1/Sub" });
            equal("a chosen folder's contents land under its name", "Sub/a.txt", map("Disc 1/Sub/a.txt") ?? "(null)");
            check("a sibling that only starts with its name is not chosen", map("Disc 1/Subway/a.txt") == null, null);
            check("nor anything else", map("Disc 1/other.txt") == null, null);

            var destination = Path.Combine(work, "rules");
            var rules = ExtractionRules.Selecting(destination, ArchiveBrowser.SelectionMap(new[] { "Disc 1" }));
            equal("a chosen entry is written inside", Path.Combine(destination, "Disc 1", "a.txt"),
                rules.TargetFor("Disc 1/a.txt", false, out _) ?? "(null)");
            check("one not chosen is passed over without a word",
                rules.TargetFor("other/x.txt", false, out var quiet) == null && quiet == null, null);
            check("one that climbs out of the folder is refused, and said",
                rules.TargetFor("Disc 1/../../evil.txt", false, out var climbing) == null && climbing != null, null);
            check("and so is a network path", rules.TargetFor("//server/share/Disc 1/x", false, out var unc) == null, null);

            var path = Path.Combine(work, "slip.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Write(zip, "Disc 1/fine.txt", Encoding.UTF8.GetBytes("fine"));
                Write(zip, "Disc 1/../../escaped.txt", Encoding.UTF8.GetBytes("escaped"));
            }

            var listing = ArchiveBrowser.Open(path, null, CancellationToken.None);
            equal("the climbing name is not listed", "fine.txt", Names(listing.Children("Disc 1")));

            var staging = Path.Combine(work, "slip-out", "inner");
            var errors = new List<string>();
            ArchiveBrowser.ExtractSelection(listing, new[] { "Disc 1" }, staging, null, errors, CancellationToken.None);
            check("copying its folder brings what is safe", File.Exists(Path.Combine(staging, "Disc 1", "fine.txt")), null);
            check("and writes nothing outside",
                !File.Exists(Path.Combine(work, "slip-out", "escaped.txt")) &&
                !File.Exists(Path.Combine(work, "escaped.txt")) &&
                !File.Exists(Path.Combine(staging, "escaped.txt")), null);
            check("and says why", errors.Any(e => e.Contains("outside", StringComparison.OrdinalIgnoreCase)),
                string.Join("; ", errors));
        }

        private static void Damaged(Action<string, bool, string?> check, string work)
        {
            var path = Path.Combine(work, "broken.zip");
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("PK\x03\x04 this is not really a zip at all, just its first four bytes"));

            string? said = null;
            try { ArchiveBrowser.Open(path, null, CancellationToken.None); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { said = ex.Message; }
            check("a damaged zip is said in a sentence", said != null && said.Contains("damaged"), said);

            var gz = Path.Combine(work, "broken.tar.gz");
            File.WriteAllBytes(gz, new byte[] { 0x1F, 0x8B, 8, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7 });
            said = null;
            try { ArchiveBrowser.Open(gz, null, CancellationToken.None); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { said = ex.Message; }
            check("so is a damaged .tar.gz", said != null && said.Contains("damaged"), said);

            var text = Path.Combine(work, "notes.zip");
            File.WriteAllText(text, "just some text with the wrong name");
            said = null;
            try { ArchiveBrowser.Open(text, null, CancellationToken.None); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { said = ex.Message; }
            check("and a file that is not an archive at all", said != null, said);
        }

        private static void Sweep(Action<string, bool, string?> check, string work)
        {
            var root = Path.Combine(work, "cache");
            var now = DateTime.Now;
            var oldOpen = Directory.CreateDirectory(Path.Combine(root, "open-old")).FullName;
            var newOpen = Directory.CreateDirectory(Path.Combine(root, "open-new")).FullName;
            var oldCopy = Directory.CreateDirectory(Path.Combine(root, "copy-old")).FullName;
            var newCopy = Directory.CreateDirectory(Path.Combine(root, "copy-new")).FullName;
            File.WriteAllText(Path.Combine(oldOpen, "track.flac"), "x");
            File.SetAttributes(Path.Combine(oldOpen, "track.flac"), FileAttributes.ReadOnly);

            Directory.SetCreationTime(oldOpen, now.AddHours(-1));
            Directory.SetCreationTime(newOpen, now.AddMinutes(5));
            Directory.SetLastWriteTime(oldCopy, now.AddDays(-2));

            ArchiveBrowser.SweepCache(root, startedAt: now, now: now);
            check("files opened by an earlier run are swept, read-only ones too", !Directory.Exists(oldOpen), null);
            check("this run's are kept", Directory.Exists(newOpen), null);
            check("a copy older than a day is swept", !Directory.Exists(oldCopy), null);
            check("a recent copy stays for its paste", Directory.Exists(newCopy), null);
        }

        /// <summary>A 7z goes through Windows' tar into the cache, and is listed from the disk.</summary>
        private static async Task SevenZip(Action<string, bool, string?> check, Action<string, string, string> equal,
            string work, List<string> madeInCache)
        {
            if (!BsdTar.Available)
            {
                Console.WriteLine("  (no Windows tar here; the 7z case is skipped)");
                return;
            }

            var source = Path.Combine(work, "seven", "album");
            Directory.CreateDirectory(Path.Combine(source, "disc 1"));
            File.WriteAllText(Path.Combine(source, "disc 1", "track.txt"), "seven");
            File.WriteAllText(Path.Combine(source, "cover.txt"), "cover");

            var archive = Path.Combine(work, "album.7z");
            var made = await ArchiveEngine.CompressAsync(new[] { source }, archive, ArchiveFormats.SevenZip,
                ArchiveLevel.Fastest, 1, null, CancellationToken.None);
            if (made.Failed > 0 || !File.Exists(archive))
            {
                check("a 7z could be made to test with", false, string.Join("; ", made.Errors));
                return;
            }

            var listing = ArchiveBrowser.Open(archive, null, CancellationToken.None);
            if (listing.Tree != null) madeInCache.Add(listing.Tree);
            check("a 7z is read through a folder on disk", listing.Tree != null, null);
            equal("its top level", "album", Names(listing.Children("")));
            equal("and inside", "cover.txt|disc 1", Names(listing.Children("album")));

            var one = ArchiveBrowser.ExtractOne(listing, "album/disc 1/track.txt", null, CancellationToken.None);
            check("one member of it is already on disk", File.ReadAllText(one) == "seven", one);

            var again = ArchiveBrowser.Open(archive, null, CancellationToken.None);
            equal("opened again, the same folder is used", listing.Tree ?? "", again.Tree ?? "");
        }

        /// <summary>
        /// Inside an archive, everything that would change it refuses. Read from
        /// MainForm, which the suite cannot run: each command asks before it acts.
        /// </summary>
        private static void SourceShape(Action<string, bool, string?> check)
        {
            var main = Source("MainForm.cs");
            if (main == null) { check("MainForm.cs can be read", false, null); return; }

            foreach (var command in new[]
                     {
                         "CreateNewFile", "CreateNewFolder", "RenameSelected", "DeleteSelected", "PasteClipboard",
                         "CreateShortcut", "CompressSelection", "ExtractFocused",
                     })
            {
                var body = Body(main, $"void {command}(");
                check($"{command} refuses inside an archive", body.Contains("RefuseInArchive()"), command);
            }

            var copy = Body(main, "void CopyToClipboard(");
            check("Cut refuses inside an archive", copy.Contains("cut && RefuseInArchive()"), null);
            check("and Copy takes the members out instead", copy.Contains("CopyOutOfArchive("), null);

            var refuse = Body(main, "bool RefuseInArchive(");
            check("the refusal says archives are read-only", refuse.Contains("ArchiveBrowser.ReadOnlyMessage"), null);
            check("in the catalogue's words", ArchiveBrowser.ReadOnlyMessage == "Archives are read-only here", null);

            // Menu entries that could only refuse are hidden there, not disabled.
            foreach (var label in new[] { "\"Cu&t\"", "\"&Paste\"", "\"&Rename\"", "\"&Delete\"", "\"&Compress…\"" })
                check($"the context menu's {label} is hidden inside an archive",
                    main.Contains($"ArchiveHidden(Item({label}", StringComparison.Ordinal), label);

            var enter = Body(main, "void ActivateFocused(bool openExternally)");
            check("Enter opens an archive as a folder", enter.Contains("ArchiveBrowser.CanBrowse("), null);
            check("but not one on the Google Drive letter", enter.Contains("Drive.Owns(path)"), null);
        }

        // ---------- helpers ----------

        private static void Write(ZipArchive zip, string name, byte[] data)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
            using var stream = entry.Open();
            stream.Write(data);
        }

        private static string Names(IEnumerable<ArchiveNode> nodes) =>
            string.Join("|", nodes.Select(n => n.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        private static string Format((long Bytes, int Files, int Folders) m) => $"{m.Bytes} {m.Files} {m.Folders}";

        /// <summary>From the method's signature to the start of the next member.</summary>
        private static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0) return "";
            int next = source.Length;
            foreach (var marker in new[] { "\n        private ", "\n        public ", "\n        protected ", "\n        internal ", "\n        /// <summary>" })
            {
                int found = source.IndexOf(marker, at + signature.Length, StringComparison.Ordinal);
                if (found >= 0 && found < next) next = found;
            }
            return source[at..next];
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            try { return File.ReadAllText(path); } catch { return null; }
        }
    }
}
