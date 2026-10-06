using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// What the web app may do to files, done the way the window does it: the Drive trash, rename, folder and
    /// copy through <see cref="GoogleDrive"/>, uploads through <see cref="DriveUpload"/>, local copies through
    /// robocopy, deletes to the Recycle Bin, names checked by <see cref="NameRules"/>.
    ///
    /// The routing follows <c>MainForm.RunTransfer</c> and <c>DeleteSelected</c> rule for rule, including
    /// what they refuse: a move or delete that mixes Drive and local items, and a move out of Drive of
    /// something the account may not remove. <see cref="DriveToDrive"/> and <see cref="FinishMoveOutOfDrive"/>
    /// are MainForm's <c>OnDrive</c> and <c>FinishMoveOutOfDrive</c> without the window; a change to how
    /// either behaves belongs in both.
    ///
    /// Everything here runs on workers. Nothing walks the Drive letter: Drive folders are measured and
    /// copied from Drive's own listings.
    /// </summary>
    public sealed class ConnectFiles : IConnectFiles
    {
        private readonly GoogleDrive? _drive;
        private readonly Settings _settings;

        /// <summary>The local walk's budget for a folder size.</summary>
        public const int SizeBudgetSeconds = 20;

        /// <summary>How long a Drive folder may take to be counted from Drive's listings.</summary>
        public static readonly TimeSpan DriveSizeBudget = TimeSpan.FromSeconds(60);

        public ConnectFiles(GoogleDrive? drive, Settings settings)
        {
            _drive = drive;
            _settings = settings;
        }

        public bool OnDrive(string path) => _drive != null && _drive.Owns(path);

        /// <summary>Settings' list, as AudioFiles.IsAudio reads it: separated by semicolons, commas or spaces.</summary>
        public IReadOnlyList<string> AudioExtensions()
        {
            var configured = string.IsNullOrWhiteSpace(_settings.AudioExtensions) ? AudioFiles.DefaultExtensions : _settings.AudioExtensions;
            return configured.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim().ToLowerInvariant())
                .Where(e => e.Length > 0 && e != "*" && e != ".*")
                .Select(e => e.StartsWith('.') ? e : "." + e.TrimStart('*'))
                .Distinct()
                .ToList();
        }

        private GoogleDrive Drive => _drive ?? throw new ConnectException(503, "Google Drive is not connected on this computer.");

        public string? Unavailable(string path)
        {
            if (_drive == null || !_settings.GoogleDriveEnabled) return null;
            bool owned = _drive.Owns(path);
            if (!owned)
            {
                // Not mounted: the letter it would be on is still recognisably Drive's.
                if (_drive.Mounted) return null;
                var letter = (_settings.GoogleDriveLetter ?? "").Trim().TrimEnd(':');
                var root = Path.GetPathRoot(path) ?? "";
                if (letter.Length != 1 || root.Length < 2 || root[1] != ':' ||
                    char.ToUpperInvariant(root[0]) != char.ToUpperInvariant(letter[0])) return null;
                return "Google Drive is not mounted on this computer";
            }
            return _drive.NeedsSignIn ? "Google Drive needs signing in again on this computer" : null;
        }

        public (long Used, long Limit)? DriveQuota() =>
            _drive is { Mounted: true } d ? (d.QuotaUsed, d.QuotaLimit) : null;

        /// <summary>A Drive folder's listing, fetching it if nobody has looked in it yet.</summary>
        private IReadOnlyList<DrivePlaced>? Listing(string folder)
        {
            var drive = Drive;
            if (!drive.TryListing(folder, out var entries)) { drive.Populate(folder); if (!drive.TryListing(folder, out entries)) return null; }
            return entries;
        }

        /// <summary>
        /// A local path as the file system is to be asked about it. A name ending in a dot or a space, anywhere
        /// in the path, is quietly trimmed by Windows, so "report." would be taken as "report" and a different
        /// file read, renamed or found missing; those paths go in the literal form. Every other path is
        /// returned as the same string, so <c>ReferenceEquals</c> says whether it changed.
        /// </summary>
        public static string Io(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            foreach (var segment in path.Split('\\', '/'))
            {
                if (segment.Length == 0 || segment == "." || segment == "..") continue;
                if (segment[^1] == '.' || segment[^1] == ' ') return NameRules.LiteralPath(path);
            }
            return path;
        }

        private static string Leaf(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

        private static string? Parent(string path) => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));

        /// <summary>Whether a local path is this application's own data, where the Drive sync root lives.</summary>
        private static bool IsOurs(string path)
        {
            var ours = Path.TrimEndingDirectorySeparator(Settings.AppDataDir);
            var p = Path.TrimEndingDirectorySeparator(path);
            return p.Equals(ours, StringComparison.OrdinalIgnoreCase) ||
                   p.StartsWith(ours + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A Drive failure as the status it deserves. The messages carry Google's status code.</summary>
        internal static ConnectException FromDrive(Exception e, string doing)
        {
            if (e is ConnectException c) return c;
            var m = e.Message;
            int status = m.Contains("403") || m.Contains("permission", StringComparison.OrdinalIgnoreCase) ? 403
                : m.Contains("404") || m.Contains("does not have", StringComparison.OrdinalIgnoreCase) ? 404
                : e is GoogleSignInRequiredException ? 503
                : 500;
            return new ConnectException(status, $"Google Drive would not {doing}: {m}");
        }

        // MARK: Sizes and details

        public async Task<ConnectSize> SizeAsync(string folder, CancellationToken token)
        {
            if (OnDrive(folder))
            {
                var drive = Drive;
                var parent = Parent(folder);
                if (parent == null)
                {
                    // The whole account: that is the quota's number, not four minutes of listings.
                    return new ConnectSize(folder, drive.QuotaUsed, 0, 0, false);
                }
                var (isFolder, exists) = await Task.Run(() => DriveKind(folder), token);
                if (!exists) throw new ConnectException(404, "That folder is not there any more.");
                if (!isFolder) throw new ConnectException(400, "That is a file; ask for its details instead.");

                // Every folder MeasureOnDrive lists is announced through this, the first being the one asked about.
                int listed = 0;
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                budget.CancelAfter(DriveSizeBudget);
                try
                {
                    var (files, bytes) = await drive.MeasureOnDrive(new[] { folder }, _ => Interlocked.Increment(ref listed), budget.Token);
                    return new ConnectSize(folder, bytes, files, Math.Max(0, listed - 1), true);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new ConnectException(500, "Google Drive took too long to count what is in that folder.");
                }
                catch (Exception e) when (e is not OperationCanceledException) { throw FromDrive(e, "count that folder"); }
            }

            if (IsOurs(folder)) throw new ConnectException(403, "That is Explorer Native's own folder, which holds the Google Drive sync folder; it is not measured.");
            var io = Io(folder);
            var kind = await Task.Run(() => Directory.Exists(io) ? 1 : File.Exists(io) ? 2 : 0, token);
            if (kind == 0) throw new ConnectException(404, "That folder is not there any more.");
            if (kind == 2) throw new ConnectException(400, "That is a file; ask for its details instead.");

            // The window's own walk: bounded, iterative, links not followed. Never into the Drive letter or the
            // sync root under AppData, which would be one Drive listing per folder.
            using var calculator = new FolderSizeCalculator
            {
                TimeoutSeconds = SizeBudgetSeconds,
                Skip = p =>
                {
                    var plain = p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
                    return IsOurs(plain) || OnDrive(plain);
                },
            };
            var r = await calculator.CalculateAsync(io, token);
            token.ThrowIfCancellationRequested();
            return new ConnectSize(folder, r.Bytes, r.Files, r.Folders, r.Complete);
        }

        /// <summary>Whether a Drive path is a folder, from its folder's listing. Not on the UI thread.</summary>
        private (bool Folder, bool Exists) DriveKind(string path)
        {
            var parent = Parent(path);
            if (parent == null) return (true, true);
            var entries = Listing(parent) ?? throw new ConnectException(500, "Google Drive would not say what is in that folder.");
            var leaf = Leaf(path);
            var entry = entries.FirstOrDefault(e => e.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase));
            return entry == null ? (false, false) : (entry.IsFolder, true);
        }

        public async Task<ConnectStat> StatAsync(string path, CancellationToken token)
        {
            if (OnDrive(path))
            {
                var parent = Parent(path);
                if (parent == null)
                    return new ConnectStat(path, "Google Drive", true, 0, DateTime.UtcNow, DateTime.UtcNow, false, true);

                var entry = await Task.Run(() =>
                {
                    var entries = Listing(parent) ?? throw new ConnectException(500, "Google Drive would not say what is in that folder.");
                    var leaf = Leaf(path);
                    return entries.FirstOrDefault(e => e.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase));
                }, token) ?? throw new ConnectException(404, "That is not there any more.");

                var full = Path.Combine(parent, entry.Name);
                // Never through the placeholder: a Drive file's tags come from /api/stat's details, read by range.
                SongSummary? tags = null;
                // Drive's listing carries one time; it stands for both.
                var modified = entry.Modified.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(entry.Modified, DateTimeKind.Utc)
                    : entry.Modified.ToUniversalTime();
                return new ConnectStat(full, entry.Name, entry.IsFolder, entry.IsFolder ? 0 : entry.Size,
                    modified, modified, false, true, tags);
            }

            var stat = await Task.Run<ConnectStat?>(() =>
            {
                var io = Io(path);
                FileSystemInfo info = Directory.Exists(io) ? new DirectoryInfo(io) : new FileInfo(io);
                if (!info.Exists) return null;
                bool folder = info is DirectoryInfo;
                // The path as the web app knows it, never the literal form.
                var full = ReferenceEquals(io, path) ? info.FullName : path;
                return new ConnectStat(full, info.Name.Length > 0 ? info.Name : full, folder,
                    folder ? 0 : ((FileInfo)info).Length, info.LastWriteTimeUtc, info.CreationTimeUtc,
                    (info.Attributes & FileAttributes.ReadOnly) != 0, false);
            }, token) ?? throw new ConnectException(404, "That is not there any more.");

            return stat.Folder ? stat : stat with { Tags = await TagsOf(path, token) };
        }

        // MARK: Full details

        private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff" };

        /// <summary>
        /// The sections of <c>/api/stat</c> beyond the basics: file, folder, text, media, image, archive. Each is
        /// its own try; one that fails is left out, and when <paramref name="token"/> (the call's budget) runs out
        /// what is known is returned with <c>partial</c>. A Drive file's bytes come through its Drive range source
        /// (a few ranged requests for a header and a tail), never through its placeholder.
        /// </summary>
        public async Task<Dictionary<string, object?>> DetailsAsync(string path, ConnectStat stat, bool hash, CancellationToken token)
        {
            var result = new Dictionary<string, object?>();
            bool partial = false;
            bool onDrive = OnDrive(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();

            IRangeSource? raw = null;
            CachedBytes? bytes = null;
            CachedBytes Bytes()
            {
                if (bytes != null) return bytes;
                raw = onDrive ? Drive.OpenRange(path) ?? throw new IOException("Google Drive would not open it") : new FileRangeSource(Io(path));
                return bytes = new CachedBytes(raw, token);
            }
            Stream OpenStream() => onDrive ? new CachedStream(Bytes()) : new FileStream(Io(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

            async Task Section(string name, Func<object?> build)
            {
                if (token.IsCancellationRequested) { partial = true; return; }
                try
                {
                    var value = await Task.Run(build, token).WaitAsync(token);
                    if (value != null) result[name] = value;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { partial = true; }
                catch { }
            }

            try
            {
                await Section("file", () => FileSection(path, stat, onDrive, hash, OpenStream, token));
                if (onDrive && result.TryGetValue("file", out var f) && f is Dictionary<string, object?> file)
                {
                    try
                    {
                        var link = await Drive.LinkFor(path, makePublic: false, token).WaitAsync(token);
                        if (link != null) file["driveWebLink"] = link;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { partial = true; }
                    catch { }
                }

                if (stat.Folder)
                {
                    await Section("folder", () =>
                    {
                        int files = 0, folders = 0;
                        if (onDrive)
                        {
                            var entries = Listing(path);
                            if (entries == null) return null;
                            foreach (var e in entries) if (e.IsFolder) folders++; else files++;
                        }
                        else
                        {
                            foreach (var e in new DirectoryInfo(Io(path)).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden }))
                            {
                                token.ThrowIfCancellationRequested();
                                if (e is DirectoryInfo) folders++; else files++;
                            }
                        }
                        return new Dictionary<string, object?> { ["items"] = files + folders, ["files"] = files, ["folders"] = folders };
                    });
                }
                else
                {
                    bool media = AudioExtensions().Contains(ext) || ConnectDetails.VideoExtensions.Contains(ext);
                    bool image = ImageExtensions.Contains(ext);
                    var archive = ArchiveFormats.FromName(path);
                    bool textish = !media && !image && archive == null && stat.Size <= 50L << 20 &&
                                   (ConnectDetails.TextExtensions.Contains(ext) ||
                                    (stat.Size > 0 && (onDrive ? stat.Size <= 4L << 20 : true) && ConnectDetails.LooksLikeText(Bytes().Get(0, 4096))));

                    if (textish) await Section("text", () => ConnectDetails.TextStats(OpenStream, ext, token));
                    if (media)
                    {
                        await Section("media", () =>
                        {
                            var m = ConnectDetails.Media(Bytes(), path, token) ?? new Dictionary<string, object?>();
                            // The property system for a local file whose own header said nothing about tags.
                            if (!onDrive && !m.ContainsKey("tags"))
                            {
                                var s = AudioTags.ReadSummary(path);
                                if (s != null)
                                {
                                    var list = new TagList();
                                    list.Add("Title", s.Title); list.Add("Artist", s.Artist); list.Add("Album", s.Album);
                                    list.Add("Year", s.Year?.ToString()); list.Add("Track", s.Track?.ToString());
                                    if (list.Count > 0) m["tags"] = list.ToList();
                                    if (!m.ContainsKey("durationSeconds") && s.DurationSeconds is > 0) m["durationSeconds"] = s.DurationSeconds;
                                }
                            }
                            return m.Count > 0 ? m : null;
                        });
                    }
                    if (image) await Section("image", () => ImageSection(path, onDrive, Bytes()));
                    if (archive != null) await Section("archive", () => ArchiveSection(path, archive, onDrive, Bytes(), token));
                }
            }
            finally { try { raw?.Dispose(); } catch { } }

            if (partial || token.IsCancellationRequested) result["partial"] = true;
            return result;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetCompressedFileSizeW(string path, out uint high);

        /// <summary>The shell's name for a kind of file ("FLAC audio"), from the extension alone: no disk.</summary>
        internal static string? FriendlyType(string path, bool folder)
        {
            try
            {
                var info = new SHFILEINFO();
                const uint USEFILEATTRIBUTES = 0x10, TYPENAME = 0x400;
                SHGetFileInfoW(folder ? "folder" : Path.GetFileName(path), folder ? 0x10u : 0x80u, ref info,
                    (uint)Marshal.SizeOf<SHFILEINFO>(), USEFILEATTRIBUTES | TYPENAME);
                return string.IsNullOrWhiteSpace(info.szTypeName) ? null : info.szTypeName;
            }
            catch { return null; }
        }

        private static Dictionary<string, object?> FileSection(string path, ConnectStat stat, bool onDrive, bool hash,
            Func<Stream> open, CancellationToken token)
        {
            var f = new Dictionary<string, object?>
            {
                ["name"] = stat.Name,
                ["folder"] = Parent(path) ?? "",
                ["extension"] = stat.Folder ? "" : Path.GetExtension(path),
                ["kind"] = FriendlyType(path, stat.Folder),
                ["mime"] = stat.Folder ? null : ConnectServer.ContentType(path).Split(';')[0],
                ["size"] = stat.Size,
                ["created"] = stat.Created,
                ["modified"] = stat.Modified,
                ["onDrive"] = onDrive,
            };
            try
            {
                // On the Drive letter these are the placeholder's own metadata: attributes, never its bytes.
                var io = Io(path);
                FileSystemInfo info = stat.Folder ? new DirectoryInfo(io) : new FileInfo(io);
                if (info.Exists)
                {
                    if (!onDrive)
                    {
                        f["created"] = info.CreationTimeUtc;
                        f["accessed"] = info.LastAccessTimeUtc;
                    }
                    var a = info.Attributes;
                    var list = new List<string>();
                    if ((a & FileAttributes.Hidden) != 0) list.Add("hidden");
                    if ((a & FileAttributes.ReadOnly) != 0) list.Add("readOnly");
                    if ((a & FileAttributes.System) != 0) list.Add("system");
                    if ((a & FileAttributes.Archive) != 0) list.Add("archive");
                    if ((a & FileAttributes.Compressed) != 0) list.Add("compressed");
                    if ((a & FileAttributes.Encrypted) != 0) list.Add("encrypted");
                    if ((a & FileAttributes.Offline) != 0) list.Add("offline");
                    if ((a & FileAttributes.Temporary) != 0) list.Add("temporary");
                    if ((a & FileAttributes.SparseFile) != 0) list.Add("sparse");
                    if ((a & FileAttributes.ReparsePoint) != 0) list.Add(onDrive || ((int)a & 0x400000) != 0 ? "cloud" : "reparsePoint");
                    else if (((int)a & 0x400000) != 0) list.Add("cloud");      // RECALL_ON_DATA_ACCESS
                    f["attributes"] = list;
                }
                if (!stat.Folder)
                {
                    uint low = GetCompressedFileSizeW(NameRules.LiteralPath(path), out uint high);
                    if (low != 0xFFFFFFFF || Marshal.GetLastWin32Error() == 0) f["sizeOnDisk"] = (long)high << 32 | low;
                }
                if (!onDrive)
                {
                    try
                    {
                        var owner = stat.Folder
                            ? new DirectoryInfo(io).GetAccessControl().GetOwner(typeof(System.Security.Principal.NTAccount))
                            : new FileInfo(io).GetAccessControl().GetOwner(typeof(System.Security.Principal.NTAccount));
                        if (owner != null) f["owner"] = owner.Value;
                    }
                    catch { }
                }
            }
            catch { }

            if (hash && !stat.Folder && stat.Size < 2L << 30)
            {
                using var stream = open();
                using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 20];
                int got;
                while ((got = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    sha.AppendData(buffer, 0, got);
                }
                f["sha256"] = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }
            return f;
        }

        private static Dictionary<string, object?>? ImageSection(string path, bool onDrive, CachedBytes bytes)
        {
            var i = new Dictionary<string, object?>();
            var (w, h, format) = ConnectDetails.ImageSize(bytes.Get(0, 256 * 1024));
            if (w > 0) { i["width"] = w; i["height"] = h; }
            if (format != null) i["format"] = format;
            if (!onDrive)
            {
                var p = AudioTags.ReadNamed(path, new[]
                {
                    "System.Image.HorizontalSize", "System.Image.VerticalSize", "System.Image.BitDepth", "System.Image.HorizontalResolution",
                    "System.Photo.CameraManufacturer", "System.Photo.CameraModel", "System.Photo.DateTaken",
                    "System.GPS.Latitude", "System.GPS.LatitudeRef", "System.GPS.Longitude", "System.GPS.LongitudeRef",
                });
                if (!i.ContainsKey("width") && int.TryParse(p.GetValueOrDefault("System.Image.HorizontalSize"), out var pw)) i["width"] = pw;
                if (!i.ContainsKey("height") && int.TryParse(p.GetValueOrDefault("System.Image.VerticalSize"), out var ph)) i["height"] = ph;
                if (int.TryParse(p.GetValueOrDefault("System.Image.BitDepth"), out var depth)) i["bitDepth"] = depth;
                if (double.TryParse(p.GetValueOrDefault("System.Image.HorizontalResolution"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var dpi)) i["dpi"] = Math.Round(dpi);
                var camera = string.Join(" ", new[] { p.GetValueOrDefault("System.Photo.CameraManufacturer"), p.GetValueOrDefault("System.Photo.CameraModel") }.Where(x => !string.IsNullOrEmpty(x)));
                if (camera.Length > 0) i["camera"] = camera;
                if (p.TryGetValue("System.Photo.DateTaken", out var taken)) i["taken"] = taken;
                if (p.TryGetValue("System.GPS.Latitude", out var lat) && p.TryGetValue("System.GPS.Longitude", out var lon))
                    i["gps"] = $"{lat} {p.GetValueOrDefault("System.GPS.LatitudeRef")}, {lon} {p.GetValueOrDefault("System.GPS.LongitudeRef")}".Trim();
            }
            return i.Count > 0 ? i : null;
        }

        private static Dictionary<string, object?>? ArchiveSection(string path, ArchiveFormat format, bool onDrive, CachedBytes bytes, CancellationToken token)
        {
            if (format.Engine == ArchiveEngineKind.Zip) return ConnectDetails.Zip(bytes);
            if (onDrive || format.Engine != ArchiveEngineKind.Tar)
                return new Dictionary<string, object?> { ["format"] = format.Id };
            var entries = TarEngine.List(path, format, token);
            return new Dictionary<string, object?>
            {
                ["format"] = format.Id,
                ["entries"] = entries.Count,
                ["files"] = entries.Count(e => !e.IsDirectory),
                ["uncompressedSize"] = entries.Sum(e => e.Size),
            };
        }

        /// <summary>
        /// A track's tags through the Windows property system, as the properties sheet reads them. A Drive
        /// file is read through its placeholder, which this process serves; fifteen seconds and the details
        /// go without them.
        /// </summary>
        private static async Task<SongSummary?> TagsOf(string path, CancellationToken token)
        {
            if (!ConnectServer.ContentType(path).StartsWith("audio/", StringComparison.Ordinal)) return null;
            try { return await Task.Run(() => AudioTags.ReadSummary(path), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        // MARK: Rename, new folder, delete

        public async Task<string> RenameAsync(string path, string newName, CancellationToken token)
        {
            var parent = Parent(path) ?? throw new ConnectException(400, "A drive can't be renamed.");
            var name = Leaf(path);
            if (name == newName) return path;
            bool caseOnly = name.Equals(newName, StringComparison.OrdinalIgnoreCase);

            if (OnDrive(path))
            {
                var drive = Drive;
                var names = await Task.Run(() => drive.NamesIn(parent), token)
                            ?? throw new ConnectException(500, "Google Drive would not say what is in that folder.");
                if (!names.Contains(name)) throw new ConnectException(404, "That is not there any more.");
                if (!caseOnly && names.Contains(DriveMount.Sanitise(newName)))
                    throw new ConnectException(409, $"Something called {newName} is already there.");
                string? placed;
                try { placed = await drive.RenameOnDrive(path, newName, token); }
                catch (Exception e) when (e is not OperationCanceledException) { throw FromDrive(e, "rename it"); }
                return placed == null
                    ? throw new ConnectException(403, "Google Drive would not rename it. A shared drive is renamed on drive.google.com.")
                    : Path.Combine(parent, placed);
            }

            return await Task.Run(() =>
            {
                var target = Path.Combine(parent, newName);
                string from = Io(path), to = Io(target);
                bool folder = Directory.Exists(from);
                if (!folder && !File.Exists(from)) throw new ConnectException(404, "That is not there any more.");
                if (!caseOnly && (File.Exists(to) || Directory.Exists(to)))
                    throw new ConnectException(409, $"Something called {newName} is already there.");
                try
                {
                    if (folder) Directory.Move(from, to);
                    else File.Move(from, to);
                }
                catch (UnauthorizedAccessException e) { throw new ConnectException(403, ConnectServer.Sentence(e.Message)); }
                return target;
            }, token);
        }

        public async Task<string> CreateFolderAsync(string parent, string name, CancellationToken token)
        {
            if (OnDrive(parent))
            {
                var drive = Drive;
                var (isFolder, exists) = await Task.Run(() => DriveKind(parent), token);
                if (!exists || !isFolder) throw new ConnectException(404, "That folder is not there any more.");
                var names = await Task.Run(() => drive.NamesIn(parent), token)
                            ?? throw new ConnectException(500, "Google Drive would not say what is in that folder.");
                // CreateFolderIn merges into a folder already there, which is right for a paste; here it is a 409.
                if (names.Contains(DriveMount.Sanitise(name)))
                    throw new ConnectException(409, $"Something called {name} is already there.");
                string? made;
                try { made = await drive.CreateFolderIn(parent, name, announce: false, token); }
                catch (Exception e) when (e is not OperationCanceledException) { throw FromDrive(e, "make the folder"); }
                return made ?? throw new ConnectException(403, "Google Drive would not make the folder there.");
            }

            return await Task.Run(() =>
            {
                if (!Directory.Exists(Io(parent))) throw new ConnectException(404, "That folder is not there any more.");
                var target = Path.Combine(parent, name);
                var io = Io(target);
                if (Directory.Exists(io) || File.Exists(io))
                    throw new ConnectException(409, $"Something called {name} is already there.");
                try { Directory.CreateDirectory(io); }
                catch (UnauthorizedAccessException e) { throw new ConnectException(403, ConnectServer.Sentence(e.Message)); }
                return target;
            }, token);
        }

        public async Task<ConnectDeleted> DeleteAsync(IReadOnlyList<string> paths, CancellationToken token)
        {
            int onDrive = paths.Count(OnDrive);

            // MainForm refuses the mixture: one route or the other went down for all of it, and the Drive half
            // of a local delete removes placeholders and leaves the files in Drive.
            if (onDrive > 0 && onDrive < paths.Count)
                throw new ConnectException(400, "Some of these are on Google Drive and some are not; delete them separately.");

            var failed = new List<ConnectFailure>();
            int deleted = 0;

            if (onDrive > 0)
            {
                var drive = Drive;
                foreach (var path in paths)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (await drive.TrashPath(path, token)) deleted++;
                        else failed.Add(new ConnectFailure(path, "Google Drive would not move it to the trash: it is gone already, or it is a shared drive."));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception e) { failed.Add(new ConnectFailure(path, ConnectServer.Sentence(e.Message))); }
                }
                return new ConnectDeleted(deleted, failed);
            }

            await Task.Run(() =>
            {
                foreach (var path in paths)
                {
                    try
                    {
                        var io = Io(path);
                        if (!Directory.Exists(io) && !File.Exists(io)) { failed.Add(new ConnectFailure(path, "It is not there any more.")); continue; }
                        // The Recycle Bin is reached only by the plain name, which here is a different file; and
                        // the web app never deletes for good. So it is refused, saying why, as the window does.
                        if (!ReferenceEquals(io, path))
                        {
                            failed.Add(new ConnectFailure(path, "Windows can't put a name ending in a dot or a space in the Recycle Bin, so it is kept. Delete it on the computer with Shift+Delete."));
                            continue;
                        }
                        // The shell deletes for good, with a question on the computer's screen, whatever has no
                        // Recycle Bin: a share, most USB sticks. From a phone that is a refusal.
                        if (!HasRecycleBin(path))
                        {
                            failed.Add(new ConnectFailure(path, "That drive has no Recycle Bin, so it would be deleted for good; do that on the computer."));
                            continue;
                        }
                        if (ShellDelete.Recycle(path, IntPtr.Zero)) deleted++;
                        else failed.Add(new ConnectFailure(path, "Kept, because it could only have been deleted for good."));
                    }
                    catch (Exception e) { failed.Add(new ConnectFailure(path, ConnectServer.Sentence(e.Message))); }
                }
            }, token);
            return new ConnectDeleted(deleted, failed);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBinW(string rootPath, ref SHQUERYRBINFO info);

        /// <summary>Whether the volume under a path keeps a Recycle Bin. Network paths never do.</summary>
        internal static bool HasRecycleBin(string path)
        {
            try
            {
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
                if (new DriveInfo(root).DriveType is DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory) return false;
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                return SHQueryRecycleBinW(root, ref info) >= 0;
            }
            catch { return false; }
        }

        // MARK: Copy and move

        public void CheckTransfer(IReadOnlyList<string> paths, string destination, bool move)
        {
            if (!move || _drive == null) return;
            int onDrive = paths.Count(OnDrive);
            if (onDrive > 0 && onDrive < paths.Count)
                throw new ConnectException(400, "Some of these are on Google Drive and some are not; move them separately.");
        }

        public async Task TransferAsync(ConnectJob job, IReadOnlyList<string> sources, string destination, bool move,
            PasteConflictPolicy conflict)
        {
            var token = job.Token;
            var paths = sources.ToArray();
            bool destinationOnDrive = OnDrive(destination);

            bool there = destinationOnDrive
                ? await Task.Run(() => DriveKind(destination), token) is (true, true)
                : await Task.Run(() => Directory.Exists(Io(destination)), token);
            if (!there) throw new ConnectException(404, "The destination folder is not there any more.");

            // Moving something into the folder it is already in is nothing to do, whatever the conflict setting:
            // it was renamed to "name (2)" under keep both, refused under replace and failed under skip.
            if (move)
            {
                var already = paths.Where(p => ParentIs(p, destination)).ToArray();
                if (already.Length > 0)
                {
                    paths = paths.Where(p => !ParentIs(p, destination)).ToArray();
                    job.Say(already.Length == 1 && paths.Length == 0 ? "It is already in this folder."
                        : already.Length == 1 ? $"{Leaf(already[0])} is already in this folder."
                        : $"{already.Length} items are already in this folder.");
                    if (paths.Length == 0) return;
                }
            }

            if (destinationOnDrive)
            {
                // Both ends on Drive: Drive does it, a request per item, no bytes through here.
                if (paths.All(OnDrive)) { await DriveToDrive(job, paths, destination, conflict, move); return; }

                // Otherwise the bytes go up. A copy that mixes Drive and local items uploads all of it, as the
                // window's paste does; a mixed move was refused before the job began.
                await UploadTo(job, paths, destination, move, conflict);
                return;
            }

            bool fromDrive = paths.Any(OnDrive);

            // Out of Drive, a move ends by trashing the originals. What the account may not remove is refused
            // before anything is copied, as the window refuses it.
            if (move && fromDrive)
            {
                var refused = await Drive.WhyNotRemovable(paths, token);
                if (refused.Count > 0)
                    throw new ConnectException(403, "Not moved: you do not have permission to remove these from Google Drive. " +
                                                    ConnectServer.Sentence(refused[0]));
            }

            await Robocopy(job, paths, destination, move, fromDrive, conflict);
        }

        /// <summary>Progress reported straight into the job, on whatever thread the engine reports from.</summary>
        private sealed class Inline<T> : IProgress<T>
        {
            private readonly Action<T> _report;
            public Inline(Action<T> report) => _report = report;
            public void Report(T value) { try { _report(value); } catch { } }
        }

        /// <summary>An engine's "name: what went wrong" line as a failure against the source it names.</summary>
        internal static ConnectFailure FailureFor(string error, IReadOnlyList<string> sources)
        {
            int colon = error.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0)
            {
                var named = error[..colon];
                var source = sources.FirstOrDefault(s =>
                    Leaf(s).Equals(named, StringComparison.OrdinalIgnoreCase) || s.Equals(named, StringComparison.OrdinalIgnoreCase));
                if (source != null) return new ConnectFailure(source, ConnectServer.Sentence(error[(colon + 2)..]));
            }
            return new ConnectFailure("", ConnectServer.Sentence(error));
        }

        private async Task Robocopy(ConnectJob job, string[] paths, string destination, bool move, bool fromDrive,
            PasteConflictPolicy conflict)
        {
            var token = job.Token;

            // Out of the Drive letter the job is told what Drive has handed over, rather than waiting for
            // robocopy's late whole-file lines — RoboCopyEngine.WithDownloads, as the window does.
            using var fetches = fromDrive ? Drive.WatchFetches(paths) : null;
            long shown = 0;
            var rate = new RecentRate();
            var progress = new Inline<TransferProgress>(p =>
            {
                if (fetches != null)
                {
                    p = RoboCopyEngine.WithDownloads(p, fetches.BytesServed, fetches.Current, Interlocked.Read(ref shown), rate);
                    Interlocked.Exchange(ref shown, p.BytesDone);
                }
                job.Report(p.ItemsDone, p.ItemsTotal, p.BytesDone, p.BytesTotal, p.CurrentItem);
            });

            // What already held a name decides which originals can be checked under it afterwards.
            HashSet<string>? renamedAway = null;
            if (move && fromDrive)
            {
                var alreadyThere = await Task.Run(() => paths.Select(Leaf)
                    .Where(n => File.Exists(Path.Combine(destination, n)) || Directory.Exists(Path.Combine(destination, n)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase), token);
                renamedAway = conflict == PasteConflictPolicy.AutoRename ? alreadyThere : null;
                var twice = paths.GroupBy(Leaf, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (twice.Count > 0) (renamedAway ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).UnionWith(twice);
            }

            // One file at a time out of Drive, which is what keeps the mount's three caches from evicting each
            // other; and never robocopy's /MOV there, which deletes a placeholder and leaves the file in Drive.
            var result = await RoboCopyEngine.RunAsync(
                paths, destination, move && !fromDrive, fromDrive ? 1 : FileOperations.RecommendedThreads,
                conflict, progress, token, _settings.CopyBufferKilobytes, cloudSource: fromDrive);

            foreach (var error in result.Errors) { var f = FailureFor(error, paths); job.Fail(f.Path, f.Error); }
            if (result.Cancelled) throw new OperationCanceledException(token);

            if (move && fromDrive && result.Failed == 0)
            {
                job.Current("Checking the copies before removing the originals");
                await FinishMoveOutOfDrive(job, paths, destination, result.Collisions, renamedAway, token);
            }
            if (result.Collisions.Total > 0)
                job.Say($"{result.Collisions.RenamedCount} renamed, {result.Collisions.SkippedCount} skipped, {result.Collisions.OverwrittenCount} replaced.");
        }

        /// <summary>
        /// MainForm.FinishMoveOutOfDrive: the originals go to the Drive trash only once each is shown to have
        /// arrived, never one the conflict policy skipped. Whatever stays is a failure with the reason.
        /// </summary>
        private async Task FinishMoveOutOfDrive(ConnectJob job, string[] paths, string destination,
            ConflictOutcomes collisions, HashSet<string>? renamedAway, CancellationToken token)
        {
            var drive = Drive;
            if (collisions.SkippedCount > collisions.Skipped.Count)
            {
                job.Say("Copied out of Google Drive. Some items were skipped, so nothing was removed from Drive.");
                return;
            }

            var skipped = new HashSet<string>(collisions.Skipped, StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                var name = Leaf(path);
                if (renamedAway != null && renamedAway.Contains(name))
                {
                    job.Fail(path, "Copied, and kept in Google Drive, because the copy was saved under another name and could not be checked.");
                    continue;
                }
                if (skipped.Contains(name)) continue;

                var arrivedAt = Path.Combine(destination, name);
                bool? confirmed;
                try
                {
                    bool isFolder = await Task.Run(() => Directory.Exists(path), token);
                    confirmed = isFolder
                        ? await drive.VerifyCopied(path, arrivedAt, token)
                        : await Task.Run(() =>
                        {
                            var copy = new FileInfo(arrivedAt);
                            return copy.Exists && copy.Length == new FileInfo(path).Length;
                        }, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { confirmed = false; }

                if (confirmed == null) { job.Fail(path, "Copied, and kept in Google Drive, because there is too much in it to check the copy."); continue; }
                if (confirmed == false) { job.Fail(path, "Copied, and kept in Google Drive, because the copy could not be shown to hold everything."); continue; }

                bool trashed;
                try { trashed = await drive.TrashPath(path, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { trashed = false; }
                if (!trashed) job.Fail(path, "Copied, but it could not be removed from Google Drive; the original is still there.");
            }
        }

        /// <summary>Local items (or a copy mixing both) into a Drive folder: DriveUpload, as the window pastes.</summary>
        private async Task UploadTo(ConnectJob job, string[] paths, string destination, bool move, PasteConflictPolicy conflict)
        {
            var token = job.Token;
            var work = await Task.Run(() => DriveUpload.Survey(paths), token);
            if (work.IsEmpty) throw new ConnectException(404, "There is nothing there to upload.");
            job.Report(0, work.Files.Count, 0, work.TotalBytes, "");

            var upload = new DriveUpload(Drive, destination, move, conflict,
                t => job.Report(t.FilesDone, t.FilesTotal, t.BytesDone, t.BytesTotal, t.CurrentItem));
            try { await upload.Run(work, token); }
            finally
            {
                foreach (var error in upload.Errors) { var f = FailureFor(error, paths); job.Fail(f.Path, f.Error); }
            }
            var c = upload.Collisions;
            if (c.Total > 0) job.Say($"{c.RenamedCount} renamed, {c.SkippedCount} skipped, {c.OverwrittenCount} replaced.");
        }

        /// <summary>A copy or move with both ends on Drive, reported as Google answers.</summary>
        private async Task DriveToDrive(ConnectJob job, string[] paths, string destination, PasteConflictPolicy conflict, bool move)
        {
            var drive = Drive;
            var token = job.Token;
            int filesDone = 0, filesTotal = paths.Length, handled = 0;
            long bytesDone = 0, bytesTotal = 0;

            void Show(string current) => job.Report(move ? handled : filesDone, move ? paths.Length : filesTotal,
                Interlocked.Read(ref bytesDone), bytesTotal, current);

            if (!move)
            {
                Show("Counting what to copy");
                var (files, bytes) = await drive.MeasureOnDrive(paths, name => Show($"Counting: {name}"), token);
                filesTotal = Math.Max(files, 1);
                bytesTotal = bytes;
                Show("");
            }

            var report = new DriveCopyReport
            {
                OnStarting = Show,
                OnCopied = (name, bytes) =>
                {
                    Interlocked.Increment(ref filesDone);
                    Interlocked.Add(ref bytesDone, bytes);
                    Show(name);
                },
            };

            var log = new ConflictLog();
            await OnDrive(job, paths, destination, conflict, move, log, token,
                name => { if (move) handled++; Show(name); }, report: report);
            if (move) { handled = paths.Length; Show(""); }

            var c = log.Snapshot();
            if (c.Total > 0) job.Say($"{c.RenamedCount} renamed, {c.SkippedCount} skipped, {c.OverwrittenCount} replaced.");
        }

        private static bool ParentIs(string path, string folder)
        {
            try
            {
                var parent = Parent(path);
                return parent != null && string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string[]? ChildrenOnDrive(GoogleDrive drive, string folder)
        {
            drive.Forget(folder);
            drive.Populate(folder);
            if (!drive.TryListing(folder, out var entries)) return null;
            return entries.Select(e => Path.Combine(folder, e.Name)).ToArray();
        }

        /// <summary>
        /// MainForm.OnDrive, one level with merges recursing: a free name goes across as it is; Skip leaves a
        /// clash alone; Overwrite replaces a file with a file (the copy made under a free name first, the old one
        /// trashed after) and merges a folder into a folder; a file and a folder are never swapped; keep both
        /// numbers it. A destination Drive would not list fails every item rather than being taken as empty.
        /// </summary>
        private async Task<int> OnDrive(ConnectJob job, string[] paths, string destination, PasteConflictPolicy conflicts,
            bool move, ConflictLog log, CancellationToken token, Action<string>? progress = null,
            IReadOnlyCollection<string>? selection = null, HashSet<string>? relisted = null,
            DriveCopyReport? report = null)
        {
            selection ??= paths;
            relisted ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var drive = Drive;

            var taken = await Task.Run(() =>
            {
                drive.Forget(destination);
                return drive.NamesIn(destination);
            }, token);
            if (taken == null)
            {
                foreach (var path in paths) job.Fail(path, "Google Drive would not say what is already in the folder.");
                return 0;
            }

            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in selection)
                if (ParentIs(path, destination)) claimed.Add(Leaf(path));

            int done = 0;
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                var wanted = Leaf(path);
                var target = Path.Combine(destination, wanted);
                progress?.Invoke(wanted);

                try
                {
                    bool sameFolder = ParentIs(path, destination);
                    bool folder = await Task.Run(() => Directory.Exists(path), token);

                    var source = Parent(path);
                    if (!string.IsNullOrEmpty(source) && relisted.Add(source))
                        await Task.Run(() => drive.RelistUnlessFresh(source), token);
                    token.ThrowIfCancellationRequested();

                    var driveName = await drive.DriveNameOf(path, token)
                        ?? throw new IOException("Google Drive would not say what it is called");
                    var landing = DriveMount.Sanitise(driveName);
                    target = Path.Combine(destination, landing);
                    bool clash = taken.Contains(landing) || (sameFolder && taken.Contains(wanted));
                    bool ownClash = !sameFolder && claimed.Contains(landing);

                    if (ownClash)
                    {
                        // Another item of this request has the name: kept both.
                    }
                    else if (clash && sameFolder)
                    {
                        if (move) continue;
                        if (conflicts != PasteConflictPolicy.AutoRename)
                        {
                            job.Fail(path, "It is already in this folder, and only keeping both can copy it here.");
                            continue;
                        }
                    }
                    else if (clash)
                    {
                        bool existingFolder = await Task.Run(() => Directory.Exists(target), token);
                        bool merge = folder && existingFolder && conflicts == PasteConflictPolicy.Overwrite;

                        if (conflicts == PasteConflictPolicy.Skip)
                        {
                            log.Skipped(target);
                            continue;
                        }

                        if (conflicts == PasteConflictPolicy.Overwrite && folder != existingFolder)
                        {
                            job.Fail(path, $"A {(existingFolder ? "folder" : "file")} of that name is already there.");
                            continue;
                        }

                        if (merge)
                        {
                            var children = await Task.Run(() => ChildrenOnDrive(drive, path), token);
                            if (children == null)
                            {
                                job.Fail(path, "Google Drive would not list it.");
                                continue;
                            }
                            claimed.Add(landing);

                            int failedBefore = job.Failed.Count, skippedBefore = log.SkippedCount;
                            done += await OnDrive(job, children, target, conflicts, move, log, token,
                                selection: selection, relisted: relisted, report: report);

                            if (move && job.Failed.Count == failedBefore && log.SkippedCount == skippedBefore)
                            {
                                if (!await drive.IsEmptyOnDrive(path, token))
                                    job.Fail(path, "What is not a file here (Google documents, shortcuts) was left in it, so it stays.");
                                else if (!await drive.TrashPath(path, token))
                                    job.Fail(path, "Everything in it moved, and the empty folder could not be removed.");
                            }
                            else if (!move && await drive.HasDocumentsIn(path, token))
                            {
                                job.Fail(path, "Google documents and shortcuts in it were not copied into the folder already there.");
                            }
                            continue;
                        }
                    }

                    bool numbered = clash || ownClash;
                    var name = numbered
                        ? NameRules.UniqueAmong(DriveMount.RoomForNumber(driveName), n =>
                        {
                            var shown = DriveMount.Sanitise(n);
                            return taken.Contains(shown) || claimed.Contains(shown);
                        }, folder)
                        : driveName;
                    bool replacing = clash && !ownClash && !sameFolder && conflicts == PasteConflictPolicy.Overwrite;

                    if (move)
                    {
                        if (!await drive.MoveOnDrive(path, destination, name, token))
                        {
                            job.Fail(path, "Google Drive would not move it.");
                            continue;
                        }
                        done++;
                    }
                    else
                    {
                        int made = await drive.CopyOnDrive(path, destination, name, token, report);
                        if (made <= 0)
                        {
                            job.Fail(path, "Google Drive would not copy it.");
                            continue;
                        }
                        done += made;
                    }

                    var arrived = DriveMount.Sanitise(name);
                    taken.Add(arrived);
                    claimed.Add(arrived);

                    if (replacing)
                    {
                        await ReplaceOnDrive(drive, destination, landing, arrived, token);
                        taken.Remove(arrived);
                        claimed.Remove(arrived);
                        claimed.Add(landing);
                        log.Overwritten(target);
                    }
                    else if (numbered)
                    {
                        log.Renamed(Path.Combine(destination, arrived));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    job.Fail(path, ConnectServer.Sentence(ex.Message));
                }
            }

            return done;
        }

        /// <summary>MainForm.ReplaceOnDrive: the old one to the Drive bin, then the new one takes its name.</summary>
        private static async Task ReplaceOnDrive(GoogleDrive drive, string folder, string wanted, string temporary,
            CancellationToken token)
        {
            var driveName = await drive.DriveNameOf(Path.Combine(folder, wanted), token)
                ?? throw new IOException($"the one already there could not be replaced; the new one is {temporary}");

            if (!await drive.TrashPath(Path.Combine(folder, wanted), token))
                throw new IOException($"the one already there could not be replaced; the new one is {temporary}");

            if (await drive.RenameOnDrive(Path.Combine(folder, temporary), driveName, CancellationToken.None) == null)
                throw new IOException($"the old one was replaced, and the new one is still called {temporary}");
        }

        // MARK: Upload

        public async Task<string> UploadAsync(string folder, string name, PasteConflictPolicy conflict, Stream body,
            CancellationToken token)
        {
            if (OnDrive(folder))
            {
                var drive = Drive;
                var (isFolder, exists) = await Task.Run(() => DriveKind(folder), token);
                if (!exists || !isFolder) throw new ConnectException(404, "That folder is not there any more.");
                var names = await Task.Run(() => { drive.Forget(folder); return drive.NamesIn(folder); }, token)
                            ?? throw new ConnectException(500, "Google Drive would not say what is in that folder.");

                // Skipped without reading a byte of the body.
                var shown = DriveMount.Sanitise(name);
                if (names.Contains(shown) && conflict == PasteConflictPolicy.Skip) return Path.Combine(folder, shown);

                var staging = Path.Combine(Path.GetTempPath(), "en-connect-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try
                {
                    var temp = Path.Combine(staging, "body");
                    await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                        await body.CopyToAsync(file, 1 << 20, token);
                    return await ToDrive(temp, folder, name, conflict, null, token);
                }
                finally
                {
                    try { Directory.Delete(staging, recursive: true); } catch { }
                }
            }

            if (!await Task.Run(() => Directory.Exists(Io(folder)), token))
                throw new ConnectException(404, "That folder is not there any more.");

            // Skipped, or refused, before a byte of the body is read.
            var wanted = Path.Combine(folder, name);
            if (conflict == PasteConflictPolicy.Skip && await Task.Run(() => Exists(wanted), token)) return wanted;
            if (conflict == PasteConflictPolicy.Overwrite && await Task.Run(() => Directory.Exists(Io(wanted)), token))
                throw new ConnectException(409, $"A folder called {name} is already there.");

            // Written beside its destination under a hidden name, and renamed into place only once every byte
            // has arrived: a phone that goes away mid-upload leaves nothing that looks like the file.
            var partial = Path.Combine(folder, $".{name}.{Guid.NewGuid():N}.partial");
            try
            {
                await using (var file = new FileStream(Io(partial), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                {
                    try { File.SetAttributes(Io(partial), FileAttributes.Hidden); } catch { }
                    await body.CopyToAsync(file, 1 << 20, token);
                }
                return await Task.Run(() =>
                {
                    File.SetAttributes(Io(partial), FileAttributes.Normal);
                    return MoveIntoPlace(partial, folder, name, conflict);
                }, CancellationToken.None);
            }
            catch (UnauthorizedAccessException e) { throw new ConnectException(403, ConnectServer.Sentence(e.Message)); }
            finally
            {
                try { if (File.Exists(Io(partial))) File.Delete(Io(partial)); } catch { }
            }
        }

        private static bool Exists(string path)
        {
            var io = Io(path);
            return File.Exists(io) || Directory.Exists(io);
        }

        private static bool AlreadyExists(Exception e) =>
            e is IOException && (e.HResult & 0xFFFF) is 80 or 183;   // ERROR_FILE_EXISTS, ERROR_ALREADY_EXISTS

        /// <summary>
        /// Moves a finished file to <paramref name="name"/> in a local folder under the conflict policy, and gives
        /// the path it landed at. The free name is chosen and then taken, and something else can take it in
        /// between: eight uploads of one name at once made two files and six errors. A name taken in that moment
        /// is simply the next one tried. Under skip a name taken in between is a skip.
        /// </summary>
        internal static string MoveIntoPlace(string file, string folder, string name, PasteConflictPolicy conflict)
        {
            for (int attempt = 0; ; attempt++)
            {
                var target = Path.Combine(folder, name);
                if (Exists(target))
                {
                    if (conflict == PasteConflictPolicy.Skip) return target;
                    if (conflict == PasteConflictPolicy.AutoRename)
                        target = Path.Combine(folder, NameRules.UniqueAmong(name, n => Exists(Path.Combine(folder, n)), folder: false));
                    else if (Directory.Exists(Io(target)))
                        throw new ConnectException(409, $"A folder called {name} is already there.");
                }
                try
                {
                    File.Move(Io(file), Io(target), overwrite: conflict == PasteConflictPolicy.Overwrite);
                    return target;
                }
                catch (Exception e) when (attempt < 100 && conflict != PasteConflictPolicy.Overwrite &&
                                          (AlreadyExists(e) || (e is UnauthorizedAccessException && Exists(target))))
                {
                    // Taken since it was looked at: look again.
                }
            }
        }

        /// <summary>
        /// Sends a file already on disk into a Drive folder under the conflict policy, and gives the path it
        /// landed at. <see cref="GoogleDrive.UploadInto"/> sends a file by its own name, so the file is first
        /// renamed, beside itself, to the name it is to have.
        /// </summary>
        private async Task<string> ToDrive(string file, string folder, string name, PasteConflictPolicy conflict,
            Action<long, long>? progress, CancellationToken token)
        {
            var drive = Drive;
            var names = await Task.Run(() => { drive.Forget(folder); return drive.NamesIn(folder); }, token)
                        ?? throw new ConnectException(500, "Google Drive would not say what is in that folder.");
            var shown = DriveMount.Sanitise(name);
            bool clash = names.Contains(shown);
            if (clash && conflict == PasteConflictPolicy.Skip) return Path.Combine(folder, shown);
            var sendAs = clash && conflict == PasteConflictPolicy.AutoRename
                ? NameRules.UniqueAmong(DriveMount.RoomForNumber(name), n => names.Contains(DriveMount.Sanitise(n)), folder: false)
                : name;

            var staging = Path.Combine(Path.GetDirectoryName(file)!, "send-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var named = Path.Combine(staging, sendAs);
            try
            {
                File.Move(file, named);
                string? placed;
                try
                {
                    placed = await drive.UploadInto(named, folder,
                        progress == null ? null : p => progress(p.BytesSent, p.BytesTotal),
                        announce: false, replace: clash && conflict == PasteConflictPolicy.Overwrite, token: token);
                }
                catch (Exception e) when (e is not OperationCanceledException) { throw FromDrive(e, "take the upload"); }
                return placed == null
                    ? throw new ConnectException(500, "Google Drive would not take the upload.")
                    : Path.Combine(folder, placed);
            }
            finally
            {
                // Back where the caller left it, so the caller's own clean-up finds it whatever happened.
                try { if (File.Exists(named)) File.Move(named, file, overwrite: true); } catch { }
                try { Directory.Delete(staging, recursive: true); } catch { }
            }
        }

        public async Task<string> PlaceFileAsync(string file, string folder, string name, PasteConflictPolicy conflict,
            Action<long, long>? progress, CancellationToken token)
        {
            if (OnDrive(folder))
            {
                var (isFolder, exists) = await Task.Run(() => DriveKind(folder), token);
                if (!exists || !isFolder) throw new ConnectException(404, "That folder is not there any more.");
                return await ToDrive(file, folder, name, conflict, progress, token);
            }

            return await Task.Run(() =>
            {
                if (!Directory.Exists(Io(folder))) throw new ConnectException(404, "That folder is not there any more.");
                string target;
                try
                {
                    // A rename when the partial is on the same volume; a copy and delete when it is not.
                    target = MoveIntoPlace(file, folder, name, conflict);
                    // Only when it was moved: a skip leaves somebody's own file, attributes and all.
                    if (!File.Exists(Io(file)))
                        try { File.SetAttributes(Io(target), File.GetAttributes(Io(target)) & ~FileAttributes.Hidden); } catch { }
                }
                catch (UnauthorizedAccessException e) { throw new ConnectException(403, ConnectServer.Sentence(e.Message)); }
                return target;
            }, token);
        }
    }
}
