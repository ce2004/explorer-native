using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ExplorerNative
{
    /// <summary>Which piece of code handles a format.</summary>
    public enum ArchiveEngineKind
    {
        /// <summary>Our own parallel zip reader and writer.</summary>
        Zip,

        /// <summary>Our own tar reader and writer, optionally through parallel gzip.</summary>
        Tar,

        /// <summary>Windows' own bsdtar, for the formats it has codecs for and we do not.</summary>
        BsdTar,
    }

    /// <summary>
    /// One archive format: what it is called, what it is called on disk, and who
    /// deals with it.
    /// </summary>
    /// <param name="Extensions">
    /// Every suffix that means this format, longest first — the order matters,
    /// because ".tar.gz" and ".gz" both end a name that is a gzipped tar and only
    /// the first is the right answer.
    /// </param>
    /// <param name="SingleFile">
    /// True for the bare compressors — .gz, .xz, .bz2, .zst — which wrap exactly
    /// one file and have nowhere to put a name, a folder, or a second entry.
    /// These exist so that "compress this one file" and "open this .gz" both
    /// work; they are not offered for a selection of more than one thing,
    /// because the format cannot express it.
    /// </param>
    public sealed record ArchiveFormat(
        string Id,
        string Name,
        string Extension,
        string[] Extensions,
        bool CanCreate,
        ArchiveEngineKind Engine,
        bool SingleFile = false,
        bool CanExtract = true)
    {

    }

    /// <summary>
    /// Every archive format the application knows, and how a path or a set of
    /// bytes is turned into one.
    ///
    /// The split between the three engines is not arbitrary and it is not
    /// aesthetic — it is where the codecs are.
    ///
    /// Zip, tar and gzip are ours, because .NET has deflate and because those
    /// three are the ones worth making fast: they are what a selection of files
    /// is overwhelmingly compressed into, and the parallel writers for them (see
    /// <see cref="ZipEngine"/> and <see cref="ParallelGZip"/>) are the whole
    /// reason this feature is quick.
    ///
    /// xz, bzip2, zstandard and 7z are Windows'. Not because they could not be
    /// written — because they should not be, twice over. Each is thousands of
    /// lines of codec that has to be exactly right or it silently produces an
    /// archive no other tool can read, and every one of them is already installed
    /// on the machine: System32\tar.exe is bsdtar 3.8.4 over libarchive 3.8.4,
    /// built with zlib, liblzma, bz2lib and libzstd, and it has been in Windows
    /// since 1803. This codebase already drives robocopy.exe for the same reason
    /// — the operating system ships a good implementation of the hard part, and
    /// the alternative is a worse one of our own. See <see cref="BsdTar"/>.
    ///
    /// The formats that can only be read are read through bsdtar too: rar, cab,
    /// iso, cpio, ar, lha. Nothing here writes them, and offering to would be
    /// offering something that cannot work.
    /// </summary>
    public static class ArchiveFormats
    {
        public static readonly ArchiveFormat Zip =
            new("zip", "Zip", ".zip", new[] { ".zip" }, true, ArchiveEngineKind.Zip);

        public static readonly ArchiveFormat TarGz =
            new("tar.gz", "Gzipped tar", ".tar.gz", new[] { ".tar.gz", ".tgz" }, true, ArchiveEngineKind.Tar);

        public static readonly ArchiveFormat Tar =
            new("tar", "Tar (no compression)", ".tar", new[] { ".tar" }, true, ArchiveEngineKind.Tar);

        public static readonly ArchiveFormat TarXz =
            new("tar.xz", "xz-compressed tar", ".tar.xz", new[] { ".tar.xz", ".txz" }, true, ArchiveEngineKind.BsdTar);

        public static readonly ArchiveFormat TarZst =
            new("tar.zst", "Zstandard-compressed tar", ".tar.zst", new[] { ".tar.zst", ".tzst" }, true, ArchiveEngineKind.BsdTar);

        public static readonly ArchiveFormat TarBz2 =
            new("tar.bz2", "bzip2-compressed tar", ".tar.bz2", new[] { ".tar.bz2", ".tbz2", ".tbz" }, true, ArchiveEngineKind.BsdTar);

        public static readonly ArchiveFormat SevenZip =
            new("7z", "7-Zip", ".7z", new[] { ".7z" }, true, ArchiveEngineKind.BsdTar);

        public static readonly ArchiveFormat Gz =
            new("gz", "Gzip (one file)", ".gz", new[] { ".gz" }, true, ArchiveEngineKind.Tar, SingleFile: true);

        // The bare compressors that are not gzip, and the one place in this table
        // where the answer is "no".
        //
        // A .xz, .bz2 or .zst holding a single raw stream is not an archive and
        // libarchive will not open it as one: "tar -c -f - @thing.xz" answers
        // "Unrecognized archive format", measured, and so does an extract. So
        // there is no codec on this machine to reach them with, and the only
        // honest thing the table can do is know what they are so the message can
        // say so. The gzip one above is different because that one is ours.
        //
        // The .tar.xz, .tar.bz2 and .tar.zst above are unaffected: those are
        // archives, and they work.
        public static readonly ArchiveFormat Xz =
            new("xz", "xz (one file)", ".xz", new[] { ".xz" }, false, ArchiveEngineKind.BsdTar,
                SingleFile: true, CanExtract: false);

        public static readonly ArchiveFormat Zst =
            new("zst", "Zstandard (one file)", ".zst", new[] { ".zst" }, false, ArchiveEngineKind.BsdTar,
                SingleFile: true, CanExtract: false);

        public static readonly ArchiveFormat Bz2 =
            new("bz2", "bzip2 (one file)", ".bz2", new[] { ".bz2" }, false, ArchiveEngineKind.BsdTar,
                SingleFile: true, CanExtract: false);

        // Read-only. Every one of these is something somebody is handed rather
        // than something anybody chooses to make, and libarchive can open all of
        // them.
        public static readonly ArchiveFormat Rar =
            new("rar", "RAR", ".rar", new[] { ".rar" }, false, ArchiveEngineKind.BsdTar);

        public static readonly ArchiveFormat Cab =
            new("cab", "Cabinet", ".cab", new[] { ".cab" }, false, ArchiveEngineKind.BsdTar);

        public static readonly ArchiveFormat Iso =
            new("iso", "ISO image", ".iso", new[] { ".iso" }, false, ArchiveEngineKind.BsdTar);

        public static readonly ArchiveFormat Cpio =
            new("cpio", "cpio", ".cpio", new[] { ".cpio" }, false, ArchiveEngineKind.BsdTar);

        public static readonly ArchiveFormat Lha =
            new("lha", "LHA", ".lzh", new[] { ".lzh", ".lha" }, false, ArchiveEngineKind.BsdTar);

        public static readonly ArchiveFormat Ar =
            new("ar", "ar archive", ".a", new[] { ".a", ".deb" }, false, ArchiveEngineKind.BsdTar);

        /// <summary>
        /// Everything, in the order the chooser offers it.
        ///
        /// Zip first because it is the one anybody else can open without
        /// installing something, then the gzipped tar because it is the fast one,
        /// then the rest by how hard they squeeze. That order is the
        /// recommendation, and a list read out one entry at a time by a screen
        /// reader is a place where the order is the advice.
        /// </summary>
        public static readonly IReadOnlyList<ArchiveFormat> All = new[]
        {
            Zip, TarGz, TarZst, TarXz, TarBz2, SevenZip, Tar,
            Gz, Zst, Xz, Bz2,
            Rar, Cab, Iso, Cpio, Lha, Ar,
        };

        /// <summary>The ones a Compress dialog may offer for a set of items.</summary>
        public static IReadOnlyList<ArchiveFormat> Creatable(bool singleFile) =>
            All.Where(f => f.CanCreate && (singleFile || !f.SingleFile)).ToArray();

        public static ArchiveFormat? ById(string? id) =>
            id == null ? null : All.FirstOrDefault(f => f.Id == id);

        /// <summary>
        /// The format a name says it is, or null.
        ///
        /// Longest suffix wins, which is the whole reason the extension lists are
        /// ordered. "album.tar.gz" ends with ".gz" and matching that would send a
        /// gzipped tar to the single-file gzip path, where it would extract to one
        /// file called "album.tar" and stop — technically true, and not what
        /// anybody pressing Extract meant.
        /// </summary>
        public static ArchiveFormat? FromName(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) return null;

            ArchiveFormat? best = null;
            int bestLength = 0;

            foreach (var format in All)
                foreach (var extension in format.Extensions)
                    if (extension.Length > bestLength &&
                        name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
                        name.Length > extension.Length)
                    {
                        best = format;
                        bestLength = extension.Length;
                    }

            return best;
        }

        /// <summary>
        /// The format the first few bytes say it is, or null.
        ///
        /// Used when the name does not know — a file called "download" that is
        /// really a zip — and for the .tar case, where there is no compressed
        /// signature to find because a tar is not compressed at all.
        ///
        /// The name is asked first, not this, because a ".tar.gz" and a ".gz"
        /// have identical bytes and only the name separates them.
        /// </summary>
        public static ArchiveFormat? FromBytes(ReadOnlySpan<byte> head)
        {
            if (Starts(head, 0x50, 0x4B, 0x03, 0x04) ||
                Starts(head, 0x50, 0x4B, 0x05, 0x06) ||
                Starts(head, 0x50, 0x4B, 0x07, 0x08)) return Zip;

            if (Starts(head, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return SevenZip;
            if (Starts(head, 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00)) return Xz;
            if (Starts(head, 0x28, 0xB5, 0x2F, 0xFD)) return Zst;
            if (Starts(head, 0x1F, 0x8B)) return Gz;
            if (Starts(head, 0x42, 0x5A, 0x68)) return Bz2;
            if (Starts(head, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07)) return Rar;
            if (Starts(head, 0x4D, 0x53, 0x43, 0x46)) return Cab;
            if (Starts(head, 0x21, 0x3C, 0x61, 0x72, 0x63, 0x68, 0x3E)) return Ar;

            // A tar has no signature at the front — it opens with a file name. The
            // magic is 257 bytes in, which is why this wants a whole header block
            // rather than the handful of bytes every check above needs.
            if (head.Length >= 262)
            {
                var magic = head.Slice(257, 5);
                if (magic[0] == (byte)'u' && magic[1] == (byte)'s' && magic[2] == (byte)'t' &&
                    magic[3] == (byte)'a' && magic[4] == (byte)'r')
                    return Tar;
            }

            return null;
        }

        private static bool Starts(ReadOnlySpan<byte> data, params byte[] signature)
        {
            if (data.Length < signature.Length) return false;
            for (int i = 0; i < signature.Length; i++)
                if (data[i] != signature[i]) return false;
            return true;
        }

        /// <summary>How many bytes <see cref="FromBytes"/> would like to be given.</summary>
        public const int SniffBytes = 512;

        /// <summary>
        /// What this path is, name first and bytes second.
        ///
        /// Reads the file, so it belongs on a worker like everything else that
        /// touches a share. Never throws: a file that cannot be opened is simply
        /// not an archive as far as this is concerned, and the caller has a better
        /// error to give than "could not read the first 512 bytes".
        /// </summary>
        public static ArchiveFormat? Identify(string path)
        {
            var byName = FromName(path);

            // The name is enough for everything except .tar, whose bytes are the
            // only confirmation there is, and the case where it says nothing.
            if (byName != null && byName != Tar) return byName;

            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                Span<byte> head = stackalloc byte[SniffBytes];
                int got = 0;
                while (got < head.Length)
                {
                    int read = file.Read(head[got..]);
                    if (read <= 0) break;
                    got += read;
                }

                var byBytes = FromBytes(head[..got]);

                // "backup.tar" that is gzip inside is a gzipped tar with the
                // wrong name, not a bare gzip — read as one, it came out as a
                // single file holding the raw tar.
                if (byName == Tar && byBytes == Gz) return TarGz;
                return byBytes ?? byName;
            }
            catch { return byName; }
        }

        /// <summary>
        /// The name without the archive suffix — "album.tar.gz" becomes "album" —
        /// which is what an extraction calls the folder it makes.
        /// </summary>
        public static string BaseName(string archivePath)
        {
            var name = Path.GetFileName(archivePath) ?? "";

            string? longest = null;
            foreach (var format in All)
                foreach (var extension in format.Extensions)
                    if (name.Length > extension.Length &&
                        name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
                        (longest == null || extension.Length > longest.Length))
                        longest = extension;

            if (longest != null) return name[..^longest.Length];

            // Not one of ours: take whatever the last dot marks off, and if there
            // is no dot keep the whole name rather than returning nothing.
            var stem = Path.GetFileNameWithoutExtension(name);
            return string.IsNullOrEmpty(stem) ? name : stem;
        }

        /// <summary>
        /// What to call the archive made from these items.
        ///
        /// One item is named after itself, which is what everybody expects and
        /// what every other file manager does. Several are named after the folder
        /// they are in, because the alternatives are all worse: the first item's
        /// name describes one twentieth of the contents, and "Archive.zip" tells
        /// you nothing at the point where the file is a year old in a Downloads
        /// folder with forty others.
        /// </summary>
        /// <param name="isFolder">
        /// Whether the one source is a folder, when the caller already knows —
        /// the dialog does, and asking the disk from its thread is a wait on a
        /// share that has gone to sleep.
        /// </param>
        public static string SuggestedName(IReadOnlyList<string> sources, ArchiveFormat format,
            bool? isFolder = null)
        {
            string stem;

            if (sources.Count == 1)
            {
                var one = sources[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var name = Path.GetFileName(one);

                bool folder;
                if (isFolder is { } known) folder = known;
                else
                {
                    try { folder = Directory.Exists(one); }
                    catch { folder = false; }
                }

                // A bare compressor keeps the whole name, extension and all:
                // "song.flac" gzipped is "song.flac.gz", and it has to be, because
                // the extension is the only record of what comes back out.
                stem = format.SingleFile || folder ? name : StripOneExtension(name);
                if (string.IsNullOrEmpty(stem)) stem = "Archive";
            }
            else
            {
                var folder = sources.Count > 0
                    ? Path.GetDirectoryName(sources[0].TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    : null;

                stem = string.IsNullOrEmpty(folder) ? "Archive" : Path.GetFileName(folder);
                if (string.IsNullOrEmpty(stem)) stem = "Archive";
            }

            return stem + format.Extension;
        }

        /// <summary>
        /// Drops a file's extension and leaves anything that only looks like one
        /// alone.
        ///
        /// Two rules, and each is here because of a name it got wrong. The caller
        /// asks the filesystem first, so a real folder keeps its whole name — a
        /// folder called "Season 1.5" has no extension, and treating the ".5" as
        /// one offers "Season 1.zip" for a folder full of episodes. But the
        /// filesystem cannot always answer: the name may be one the caller
        /// invented, or the item may be on a share that has gone away. So the
        /// second rule works from the text alone — an extension has a letter in
        /// it. "mp3" and "7z" do; "5" does not, and neither does the "1" in
        /// "chapter 8.1".
        /// </summary>
        private static string StripOneExtension(string name)
        {
            int dot = name.LastIndexOf('.');
            if (dot <= 0) return name;

            var tail = name[(dot + 1)..];
            if (tail.Length is 0 or > 8) return name;

            bool anyLetter = false;
            foreach (var c in tail)
            {
                if (!char.IsLetterOrDigit(c)) return name;
                if (char.IsLetter(c)) anyLetter = true;
            }

            return anyLetter ? name[..dot] : name;
        }
    }
}
