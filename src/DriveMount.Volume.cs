using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The volume itself: what Windows asks of the drive letter, answered from
    /// the mount's listings, its chunk cache and its write-back. Plus the two
    /// things a real drive has to do that a folder of placeholders never could
    /// — tell Windows when something changed elsewhere, and go away when Google
    /// cannot be reached.
    /// </summary>
    internal sealed partial class DriveMount
    {
        // ---- finding rows ------------------------------------------------------

        // A row looked up by name in a folder of thousands is a dictionary, made
        // once per listing (listings are replaced whole, never edited in place).
        private static readonly ConditionalWeakTable<DrivePlaced[], Dictionary<string, DrivePlaced>> RowIndex = new();

        /// <summary>The row the folder at <paramref name="parent"/> shows for <paramref name="name"/>, any case.</summary>
        internal DrivePlaced? RowIn(string parent, string name)
        {
            if (!_listings.TryGetValue(parent, out var rows)) return null;
            if (rows.Length < 24)
            {
                foreach (var row in rows)
                    if (row.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return row;
                return null;
            }
            var index = RowIndex.GetValue(rows, all =>
            {
                var byName = new Dictionary<string, DrivePlaced>(all.Length, StringComparer.OrdinalIgnoreCase);
                foreach (var row in all) byName.TryAdd(row.Name, row);
                return byName;
            });
            return index.TryGetValue(name, out var found) ? found : null;
        }

        /// <summary>
        /// What a path on the mount is, from what the mount holds: whether it is
        /// there, a folder, and its size and date (a file held here at the size
        /// it has here). Null when the mount does not know — its folder never
        /// listed — and the caller may ask the letter. The file manager asks
        /// this rather than File and Directory: those go through the volume,
        /// which is this process answering itself, and which is not there at
        /// all while the letter is hidden offline.
        /// </summary>
        internal (bool Folder, long Size, DateTime ModifiedUtc)? Describe(string? fullPath)
        {
            var key = KeyFor(fullPath);
            if (key == null) return null;
            if (key.Length == 0) return (true, 0, DateTime.UtcNow);
            if (PendingAt(key) is { } copy)
                lock (copy.Gate) return (false, copy.Size, copy.ModifiedUtc);
            if (_folders.ContainsKey(key))
                return (true, 0, RowAt(key)?.Modified.ToUniversalTime() ?? DateTime.UtcNow);
            if (RowAt(key) is { } row)
                return (row.IsFolder, Math.Max(0, row.Size), row.Modified.ToUniversalTime());
            return null;
        }

        /// <summary>The row for a key, from its folder's listing, or null.</summary>
        internal DrivePlaced? RowAt(string key)
        {
            if (key.Length == 0) return null;
            int cut = key.LastIndexOf('\\');
            return cut < 0 ? RowIn("", key) : RowIn(key[..cut], key[(cut + 1)..]);
        }

        private static string LeafOf(string key) => key[(key.LastIndexOf('\\') + 1)..];

        /// <summary>
        /// A key in the case the letter shows it ("Music\Album"), from the rows
        /// of each folder on the way down. Windows wants names this way in
        /// change notices and in the answer to an open.
        /// </summary>
        internal string DisplayPath(string key)
        {
            if (key.Length == 0) return "";
            var shown = new StringBuilder(key.Length);
            string at = "";
            foreach (var part in key.Split('\\'))
            {
                var row = RowIn(at, part);
                if (shown.Length > 0) shown.Append('\\');
                shown.Append(row?.Name ?? PendingAt(ChildKey(at, part))?.Name ?? part);
                at = at.Length == 0 ? part : at + "\\" + part;
            }
            return shown.ToString();
        }

        // ---- telling Windows ---------------------------------------------------

        private readonly BlockingCollection<(string Path, uint Filter, uint Action)> _changes = new();
        private Thread? _notifier;

        /// <summary>
        /// Something in the folder at <paramref name="relative"/> appeared, went,
        /// or changed without Windows doing it: a listing found it, or the Drive
        /// API did it for the file manager. Told to Windows off this thread, so
        /// File Explorer windows update and nothing is served from a stale cache.
        /// </summary>
        private void Changed(string relative, string name, bool folder, uint action)
        {
            if (_disposed || _host == null) return;
            var parent = DisplayPath(relative);
            var path = "\\" + (parent.Length == 0 ? name : parent + "\\" + name);
            uint filter = action == WinFsp.ActionModified
                ? WinFsp.ChangeSize | WinFsp.ChangeLastWrite
                : folder ? WinFsp.ChangeDirName : WinFsp.ChangeFileName;
            try { _changes.Add((path, filter, action)); } catch { }
            EnsureNotifier();
        }

        private void EnsureNotifier()
        {
            if (_notifier != null) return;
            lock (_changes)
            {
                if (_notifier != null) return;
                _notifier = new Thread(NotifyLoop) { IsBackground = true, Name = "Drive change notices" };
                _notifier.Start();
            }
        }

        /// <summary>How long a batch of change notices is tried before it is given up.</summary>
        private static readonly TimeSpan NoticesKeptFor = TimeSpan.FromMinutes(2);

        private void NotifyLoop()
        {
            var batch = new List<(string, uint, uint)>();
            try
            {
                while (!_closing.IsCancellationRequested)
                {
                    var first = _changes.Take(_closing.Token);
                    batch.Clear();
                    batch.Add(first);
                    // A listing that found a hundred changes is one notice, not a hundred.
                    Thread.Sleep(30);
                    while (batch.Count < 256 && _changes.TryTake(out var more)) batch.Add(more);

                    var host = _host;
                    if (host == null) continue;
                    // A rename holds notices off for as long as it waits on
                    // Google, which can be longer than a few tries: the batch is
                    // kept and tried again, less often the longer it waits.
                    var tried = System.Diagnostics.Stopwatch.StartNew();
                    for (int wait = 50; !_closing.IsCancellationRequested; wait = Math.Min(wait * 2, 1000))
                    {
                        try { if (host.Notify(batch)) break; } catch { break; }
                        if (tried.Elapsed > NoticesKeptFor) { _log($"  {batch.Count} change notice(s) dropped"); break; }
                        _closing.Token.WaitHandle.WaitOne(wait);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        // ---- going away while offline -------------------------------------------

        /// <summary>
        /// Takes the letter away when Drive goes offline and puts it back when it
        /// returns (Conner, 4.3.0: "Can it just go away when there's not a
        /// network?"). Open files keep their handles; nothing new opens through a
        /// letter that is not there, so nothing hangs waiting on Google.
        /// </summary>
        private void LetterFollows(DriveState state)
        {
            if (_disposed) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (_letterGate)
                {
                    var host = _host;
                    var letter = _letterName;
                    if (_disposed || host == null || letter == null) return;

                    if (state == DriveState.Offline && host.MountPoint != null)
                    {
                        host.Unmount();
                        _log($"offline: {letter} hidden");
                    }
                    else if (state == DriveState.Online && host.MountPoint == null)
                    {
                        if (!host.Mount(letter, out var status))
                        {
                            // Taken while it was away: the next free one, said.
                            try { _letterName = DriveLetter.Take(host, letter[0], Notify); }
                            catch (Exception ex) { _log($"online, but no letter: {ex.Message} (0x{status:X8})"); return; }
                        }
                        _log($"online: back on {_letterName}");
                    }
                }
                _probe.Pace();
                if (state == DriveState.Online) KickUploads();
            });
        }

        /// <summary>
        /// Asks Google whether it is there, now and then, so a connection that
        /// went away while nobody was using Drive is noticed (and the letter
        /// taken away), and one that came back is noticed while the letter is
        /// gone and nothing else is asking. Network changes ask at once.
        /// </summary>
        internal sealed class Probe : IDisposable
        {
            private readonly DriveMount _mount;
            private readonly Timer _timer;
            private int _busy;
            private bool _started;

            /// <summary>How often to ask while Drive is online. Settable for tests.</summary>
            internal static TimeSpan Every = TimeSpan.FromSeconds(60);

            /// <summary>How often to ask while it is offline and the letter is gone.</summary>
            internal static TimeSpan WhileOffline = TimeSpan.FromSeconds(10);

            public Probe(DriveMount mount)
            {
                _mount = mount;
                _timer = new Timer(_ => Ask(), null, Timeout.Infinite, Timeout.Infinite);
            }

            public void Start()
            {
                if (_started) return;
                _started = true;
                try
                {
                    NetworkChange.NetworkAvailabilityChanged += OnAvailability;
                    NetworkChange.NetworkAddressChanged += OnAddress;
                }
                catch { }
                Pace();
            }

            /// <summary>The next ask, at the pace the state calls for.</summary>
            public void Pace()
            {
                if (!_started || _mount._disposed) return;
                var wait = _mount._drive.Health.IsOffline ? WhileOffline : Every;
                try { _timer.Change(wait, Timeout.InfiniteTimeSpan); } catch { }
            }

            private void OnAvailability(object? sender, NetworkAvailabilityEventArgs e) => Soon(e.IsAvailable ? 1 : 3);
            private void OnAddress(object? sender, EventArgs e) => Soon(2);

            /// <summary>
            /// A few asks close together. It takes three network failures in a
            /// row for Drive to count as offline; three quick ones after the
            /// network itself says it went is the letter gone in seconds.
            /// </summary>
            private void Soon(int times)
            {
                if (_mount._disposed) return;
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < times && !_mount._disposed; i++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
                        await AskAsync().ConfigureAwait(false);
                    }
                });
            }

            private void Ask() => _ = AskAsync().ContinueWith(_ => Pace(), TaskScheduler.Default);

            private async Task AskAsync()
            {
                if (_mount._disposed || Interlocked.Exchange(ref _busy, 1) == 1) return;
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(_mount._closing.Token);
                    limit.CancelAfter(TimeSpan.FromSeconds(15));
                    var (_, used, total) = await _mount._drive.Whoami(limit.Token).ConfigureAwait(false);
                    if (total > 0) { _mount.QuotaUsed = used; _mount.QuotaLimit = total; }
                }
                catch { /* counted by the client's health; the next ask asks again */ }
                finally { Volatile.Write(ref _busy, 0); }
            }

            public void Dispose()
            {
                try
                {
                    NetworkChange.NetworkAvailabilityChanged -= OnAvailability;
                    NetworkChange.NetworkAddressChanged -= OnAddress;
                }
                catch { }
                _timer.Dispose();
            }
        }

        private readonly Probe _probe;

        /// <summary>Google's storage, used and total, for the size the volume reports. Kept up to date by the probe.</summary>
        public long QuotaUsed;
        public long QuotaLimit;

        // ---- answering Windows ---------------------------------------------------

        /// <summary>One open handle (strictly, one file object) on the volume.</summary>
        internal sealed class Opened
        {
            public string Key = "";
            public bool Folder;
            public string? Id;
            public long Size;
            public DateTime Modified;
            public Pending? Local;
            public IntPtr DirBuffer;
            /// <summary>When Drive last said this folder holds nothing (CanDelete), by TickCount64.</summary>
            public long EmptyAt = long.MinValue / 2;
        }

        // Every handle open now, so a rename or a move done any way re-keys the
        // handles under it too. A few dozen at most; a set walked on a rename.
        private readonly ConcurrentDictionary<Opened, byte> _openNow = new();

        private void RekeyOpened(string from, string to)
        {
            var below = from + "\\";
            foreach (var opened in _openNow.Keys)
            {
                if (opened.Key == from) opened.Key = to;
                else if (opened.Key.StartsWith(below, StringComparison.Ordinal))
                    opened.Key = to + opened.Key[from.Length..];
            }
        }

        /// <summary>A request being answered, so the mount is not taken down under it.</summary>
        private readonly struct InFlight : IDisposable
        {
            private readonly DriveMount _mount;
            public InFlight(DriveMount mount) { _mount = mount; Interlocked.Increment(ref mount._callbacksInFlight); }
            public void Dispose() => Interlocked.Decrement(ref _mount._callbacksInFlight);
        }

        /// <summary>What a key is, as far as Windows needs to know.</summary>
        private readonly record struct Node(bool Folder, string? Id, long Size, DateTime Modified, string Name);

        /// <summary>Whether nothing may be created, changed or removed at or under a key.</summary>
        private bool ReadOnlyAt(string key)
        {
            if (FolderIdAt(key) == DriveClient.SharedDrivesId) return true;
            for (string? at = key; !string.IsNullOrEmpty(at); at = Parent(at))
                if (_folders.TryGetValue(at, out var id) && id == DriveClient.TrashId) return true;
            return false;
        }

        /// <summary>
        /// Whether a key may not itself be renamed, moved or deleted: the root,
        /// the two folders this mount makes up (Shared drives, Trash), each
        /// shared drive (a drive, not a folder), and anything in the Trash.
        /// </summary>
        private bool Fixed(string key)
        {
            if (key.Length == 0) return true;
            var id = _folders.TryGetValue(key, out var f) ? f : null;
            if (id == DriveClient.SharedDrivesId || id == DriveClient.TrashId) return true;
            var parent = Parent(key) ?? "";
            if (_folders.TryGetValue(parent, out var p) && p == DriveClient.SharedDrivesId) return true;
            return ReadOnlyAt(parent);
        }

        private string? FolderIdAt(string key) => _folders.TryGetValue(key, out var id) ? id : null;

        /// <summary>The NTSTATUS for an exception from Google.</summary>
        private int StatusOf(Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
            {
                if (e is GoogleSignInRequiredException) return WinFsp.AccessDenied;
                if (e is DriveQuotaException) return WinFsp.DiskFull;
                if (e is DriveClient.DriveStatusException s)
                    return s.Status switch
                    {
                        401 or 403 => WinFsp.AccessDenied,
                        404 => WinFsp.ObjectNameNotFound,
                        _ => WinFsp.UnexpectedNetworkError,
                    };
            }
            if (DriveHealth.LooksLikeNetwork(error) || _drive.Health.IsOffline) return WinFsp.NetworkUnreachable;
            if (error is OperationCanceledException) return WinFsp.IoTimeout;
            return WinFsp.UnexpectedNetworkError;
        }

        /// <summary>How long a listing found missing a name is trusted before a miss asks Drive again.</summary>
        internal static TimeSpan MissRelistAfter = TimeSpan.FromSeconds(30);

        private readonly ConcurrentDictionary<string, long> _missRelisted = new();
        private readonly ConcurrentDictionary<string, byte> _refreshing = new();

        /// <summary>
        /// Makes sure the folder at <paramref name="key"/> is listed, listing it
        /// now if it never has been. An old listing will do when Drive cannot be
        /// asked. <paramref name="listedNow"/> says the listing is this moment's.
        /// </summary>
        private int EnsureListed(string key, out bool listedNow)
        {
            listedNow = false;
            if (key.Length != 0 && !_folders.ContainsKey(key))
            {
                int found = Find(key, out var node);
                if (found != WinFsp.Success) return found == WinFsp.ObjectNameNotFound ? WinFsp.ObjectPathNotFound : found;
                if (!node.Folder) return WinFsp.ObjectPathNotFound;
            }

            if (_populated.ContainsKey(key) && _listings.ContainsKey(key)) return WinFsp.Success;

            Exception? problem = null;
            try { PopulateKey(key, interactive: true); }
            catch (Exception ex) { problem = ex; }

            if (_populated.ContainsKey(key) && _listings.ContainsKey(key)) { listedNow = true; return WinFsp.Success; }
            if (_listings.ContainsKey(key) && _listedAt.ContainsKey(key)) return WinFsp.Success;
            return problem != null ? StatusOf(problem)
                : _drive.Health.IsOffline ? WinFsp.NetworkUnreachable : WinFsp.UnexpectedNetworkError;
        }

        /// <summary>
        /// What a key is. A name the parent's listing does not have is asked
        /// about once more when that listing is older than
        /// <see cref="MissRelistAfter"/>: a file made on the web a minute ago and
        /// opened by path must be found.
        /// </summary>
        private int Find(string key, out Node node, bool relistOnMiss = true)
        {
            node = default;
            if (key.Length == 0)
            {
                node = new Node(true, FolderIdAt(""), 0, DateTime.Now, "");
                return WinFsp.Success;
            }

            if (PendingAt(key) is { } copy)
            {
                lock (copy.Gate) node = new Node(false, copy.Id, copy.Size, copy.ModifiedUtc.ToLocalTime(), copy.Name);
                return WinFsp.Success;
            }

            var parent = Parent(key) ?? "";
            var leaf = LeafOf(key);
            int status = EnsureListed(parent, out bool listedNow);
            if (status != WinFsp.Success) return status == WinFsp.ObjectNameNotFound ? WinFsp.ObjectPathNotFound : status;

            var row = RowIn(parent, leaf);
            if (row == null && relistOnMiss && !listedNow && !_drive.Health.IsOffline && !ListedRecently(parent, MissRelistAfter))
            {
                long now = Environment.TickCount64;
                if (!_missRelisted.TryGetValue(parent, out var at) || now - at > MissRelistAfter.TotalMilliseconds)
                {
                    _missRelisted[parent] = now;
                    ForgetKey(parent);
                    try { PopulateKey(parent, interactive: true); } catch { }
                    row = RowIn(parent, leaf);
                }
            }
            if (row == null) return WinFsp.ObjectNameNotFound;

            node = new Node(row.IsFolder, _ids.TryGetValue(key, out var id) ? id : null,
                row.IsFolder ? 0 : Math.Max(0, row.Size), row.Modified, row.Name);
            return WinFsp.Success;
        }

        private bool ListedRecently(string key, TimeSpan within) =>
            _listedAt.TryGetValue(key, out var at) && Environment.TickCount64 - at <= within.TotalMilliseconds;

        /// <summary>
        /// Lists a folder again behind an answer already given from its old
        /// listing, when that is older than <see cref="FreshFor"/>. Windows is
        /// told what changed (CommitListing), so an open window catches up.
        /// </summary>
        private void RefreshBehind(string key)
        {
            if (_disposed || _drive.Health.IsOffline || ListedRecently(key, FreshFor)) return;
            if (!_refreshing.TryAdd(key, 0)) return;
            _ = Task.Run(() =>
            {
                try
                {
                    ForgetKey(key);
                    PopulateKey(key, interactive: true);
                }
                catch { }
                finally { _refreshing.TryRemove(key, out _); }
            });
        }

        private static ulong IndexOf(string text)
        {
            // FNV-1a: stable across runs, so a file keeps its number.
            ulong hash = 14695981039346656037;
            foreach (char c in text) { hash ^= c; hash *= 1099511628211; }
            return hash == 0 ? 1 : hash;
        }

        private WinFsp.FileInfo InfoOf(Opened opened)
        {
            if (opened.Local is { } copy)
            {
                long size;
                DateTime modified;
                lock (copy.Gate) { size = copy.Size; modified = copy.ModifiedUtc; }
                return MakeInfo(false, size, modified.ToLocalTime(), opened.Id ?? copy.Id ?? copy.Identity, false);
            }

            // A file on the letter for minutes may have been listed again since
            // it was opened; the listing's size is the one its reads are served at.
            if (!opened.Folder && RowAt(opened.Key) is { } row && !row.IsFolder)
            {
                opened.Size = Math.Max(0, row.Size);
                opened.Modified = row.Modified;
            }
            return MakeInfo(opened.Folder, opened.Size, opened.Modified, opened.Id ?? opened.Key, ReadOnlyAt(opened.Key));
        }

        private static WinFsp.FileInfo MakeInfo(bool folder, long size, DateTime modified, string identity, bool readOnly)
        {
            ulong stamp = (ulong)(modified == default ? DateTime.UtcNow : modified.ToUniversalTime()).ToFileTimeUtc();
            uint attributes = folder ? WinFsp.AttributeDirectory : WinFsp.AttributeArchive;
            if (readOnly && !folder) attributes |= WinFsp.AttributeReadOnly;
            ulong bytes = folder ? 0 : (ulong)Math.Max(0, size);
            return new WinFsp.FileInfo
            {
                FileAttributes = attributes,
                FileSize = bytes,
                AllocationSize = (bytes + 4095) / 4096 * 4096,
                CreationTime = stamp,
                LastAccessTime = stamp,
                LastWriteTime = stamp,
                ChangeTime = stamp,
                IndexNumber = IndexOf(identity),
            };
        }

        /// <summary>What a folder holds, as Windows lists it, with files held here shown as they are here.</summary>
        private IEnumerable<(string Name, WinFsp.FileInfo Info)> EntriesOf(string key)
        {
            var list = new List<(string, WinFsp.FileInfo)>();
            if (key.Length != 0)
            {
                var self = RowAt(key);
                var info = MakeInfo(true, 0, self?.Modified ?? DateTime.Now, FolderIdAt(key) ?? key, false);
                list.Add((".", info));
                list.Add(("..", info));
            }

            bool readOnly = ReadOnlyAt(key);
            if (_listings.TryGetValue(key, out var rows))
                foreach (var row in rows)
                {
                    var child = ChildKey(key, row.Name);
                    if (!row.IsFolder && PendingAt(child) is { } copy)
                    {
                        long size;
                        DateTime modified;
                        lock (copy.Gate) { size = copy.Size; modified = copy.ModifiedUtc; }
                        list.Add((row.Name, MakeInfo(false, size, modified.ToLocalTime(), copy.Id ?? copy.Identity, false)));
                        continue;
                    }
                    var id = _ids.TryGetValue(child, out var bound) ? bound : child;
                    list.Add((row.Name, MakeInfo(row.IsFolder, Math.Max(0, row.Size), row.Modified, id, readOnly)));
                }
            return list;
        }

        /// <summary>The file system WinFsp calls into. Every answer comes from the mount.</summary>
        private sealed unsafe class Volume : WinFsp.FileSystem
        {
            private readonly DriveMount _m;

            public Volume(DriveMount mount)
            {
                _m = mount;
                Security = WinFsp.UserOnlySecurity();
            }

            public override byte[] Security { get; }

            private static string KeyOf(string path) => path.Trim('\\').ToLowerInvariant();

            private static string LastName(string path)
            {
                var trimmed = path.TrimEnd('\\');
                return trimmed[(trimmed.LastIndexOf('\\') + 1)..];
            }

            private bool Closing => _m._disposed || _m._closing.IsCancellationRequested;

            public override int GetVolumeInfo(out ulong total, out ulong free, out string label)
            {
                long used = Math.Max(0, Volatile.Read(ref _m.QuotaUsed));
                long limit = Volatile.Read(ref _m.QuotaLimit);
                // No limit (some accounts): a petabyte past what is used, so
                // nothing refuses a copy for want of space.
                total = (ulong)(limit > 0 ? limit : used + (1L << 50));
                free = (ulong)Math.Max(0, (long)total - used);
                label = "Google Drive";
                return WinFsp.Success;
            }

            public override int GetAttributes(string path, out uint attributes)
            {
                attributes = 0;
                if (Closing) return WinFsp.NetworkUnreachable;
                using var busy = new InFlight(_m);
                int status = _m.Find(KeyOf(path), out var node);
                if (status != WinFsp.Success) return status;
                attributes = node.Folder ? WinFsp.AttributeDirectory : WinFsp.AttributeArchive;
                return WinFsp.Success;
            }

            public override int Open(string path, uint createOptions, uint grantedAccess,
                out object? context, out WinFsp.FileInfo info, out string? normalized)
            {
                context = null; info = default; normalized = null;
                if (Closing) return WinFsp.NetworkUnreachable;
                using var busy = new InFlight(_m);

                var key = KeyOf(path);
                int status = _m.Find(key, out var node);
                if (status != WinFsp.Success) return status;
                if ((createOptions & WinFsp.FileDirectoryFile) != 0 && !node.Folder) return WinFsp.NotADirectory;
                if ((createOptions & WinFsp.FileNonDirectoryFile) != 0 && node.Folder) return WinFsp.FileIsADirectory;

                var opened = new Opened
                {
                    Key = key, Folder = node.Folder, Id = node.Id, Size = node.Size, Modified = node.Modified,
                };
                // One let go this moment is simply not attached: reads then go to Drive.
                if (!node.Folder && _m.PendingAt(key) is { } pending) TryAttach(opened, pending);
                _m._openNow[opened] = 0;

                context = opened;
                info = _m.InfoOf(opened);
                normalized = "\\" + _m.DisplayPath(key);
                return WinFsp.Success;
            }

            /// <summary>FILE_OPEN_IF, the create disposition WinFsp passes in the top byte of the create options.</summary>
            private const uint FileOpenIf = 3;

            public override int Create(string path, uint createOptions, uint grantedAccess, uint attributes,
                out object? context, out WinFsp.FileInfo info, out string? normalized)
            {
                context = null; info = default; normalized = null;
                if (Closing) return WinFsp.NetworkUnreachable;
                using var busy = new InFlight(_m);

                var key = KeyOf(path);
                if (key.Length == 0) return WinFsp.ObjectNameCollision;
                var parent = Parent(key) ?? "";
                var name = LastName(path);

                // Two creates of one name, or a create in a folder being
                // removed, one at a time (WinFsp's guard is off; see HoldNames).
                using var names = _m.HoldNames(parent, key);

                int status = _m.EnsureListed(parent, out _);
                if (status != WinFsp.Success) return status == WinFsp.ObjectNameNotFound ? WinFsp.ObjectPathNotFound : status;
                if (_m.ReadOnlyAt(parent)) return WinFsp.AccessDenied;
                if (_m.Find(key, out _) == WinFsp.Success)
                {
                    // Made by another create while this one waited its turn:
                    // "open, or create" opens it, as it would have had it come
                    // second. Anything else that must not open an existing file
                    // is told the name is taken.
                    if (((createOptions >> 24) & 0xFF) == FileOpenIf)
                    {
                        _m._log($"  {name} was made a moment ago: opened instead");
                        return Open(path, createOptions, grantedAccess, out context, out info, out normalized);
                    }
                    return WinFsp.ObjectNameCollision;
                }

                var parentId = _m.FolderIdAt(parent);
                if (parentId == null) return WinFsp.ObjectPathNotFound;

                Opened opened;
                if ((createOptions & WinFsp.FileDirectoryFile) != 0)
                {
                    // A folder is made on Drive at once: everything put in it
                    // next needs its id.
                    string id;
                    try
                    {
                        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_m._closing.Token);
                        limit.CancelAfter(TimeSpan.FromSeconds(90));
                        id = _m._drive.CreateFolder(name, parentId, limit.Token).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _m._log($"  could not make folder {name}: {ex.Message}");
                        return _m.StatusOf(ex);
                    }

                    var entry = new DriveEntry(id, name, DriveClient.FolderMimeType, 0, DateTime.UtcNow);
                    var placed = _m.PlaceRow(parent, entry, populated: true);
                    if (placed == null) return WinFsp.UnexpectedIoError;
                    key = ChildKey(parent, placed);
                    opened = new Opened { Key = key, Folder = true, Id = id, Modified = DateTime.Now };
                }
                else
                {
                    var pending = _m.NewPending(parent, name, parentId);
                    if (pending == null) return WinFsp.InsufficientResources;
                    opened = new Opened { Key = key, Folder = false, Modified = DateTime.Now };
                    TryAttach(opened, pending);
                }

                _m._openNow[opened] = 0;
                context = opened;
                info = _m.InfoOf(opened);
                normalized = "\\" + _m.DisplayPath(key);
                return WinFsp.Success;
            }

            public override int Overwrite(object context, uint attributes, bool replaceAttributes, out WinFsp.FileInfo info)
            {
                info = default;
                var opened = (Opened)context;
                if (Closing) return WinFsp.NetworkUnreachable;
                if (opened.Folder) return WinFsp.FileIsADirectory;
                if (_m.ReadOnlyAt(opened.Key)) return WinFsp.AccessDenied;
                using var busy = new InFlight(_m);

                int status = _m.Hold(opened, keepContents: false);
                if (status != WinFsp.Success) return status;
                status = _m.SetLengthPending(opened.Local!, 0);
                info = _m.InfoOf(opened);
                return status;
            }

            public override void Cleanup(object context, string? path, uint flags)
            {
                var opened = (Opened)context;
                using var busy = new InFlight(_m);
                if ((flags & WinFsp.CleanupDelete) != 0) _m.DeleteOpened(opened);
            }

            public override void Close(object context)
            {
                var opened = (Opened)context;
                WinFsp.DirBuffer.Delete(ref opened.DirBuffer);
                _m._openNow.TryRemove(opened, out _);
                if (opened.Local != null) _m.Detach(opened);
            }

            public override int Read(object context, IntPtr buffer, ulong offset, uint length, out uint transferred)
            {
                transferred = 0;
                var opened = (Opened)context;
                if (opened.Folder) return WinFsp.InvalidDeviceRequest;
                if (Closing) return WinFsp.NetworkUnreachable;
                using var busy = new InFlight(_m);
                // Pending through any handle: another may be the one writing it.
                return (opened.Local ?? _m.PendingAt(opened.Key)) is { } pending
                    ? _m.ReadPending(pending, buffer, offset, length, out transferred)
                    : _m.ReadFromDrive(opened, buffer, offset, length, out transferred);
            }

            public override int Write(object context, IntPtr buffer, ulong offset, uint length, bool toEnd,
                bool constrained, out uint transferred, out WinFsp.FileInfo info)
            {
                transferred = 0; info = default;
                var opened = (Opened)context;
                if (opened.Folder) return WinFsp.InvalidDeviceRequest;
                if (Closing) return WinFsp.NetworkUnreachable;
                if (_m.ReadOnlyAt(opened.Key)) return WinFsp.AccessDenied;
                using var busy = new InFlight(_m);

                int status = _m.Hold(opened, keepContents: true);
                if (status != WinFsp.Success) return status;
                status = _m.WritePending(opened.Local!, buffer, offset, length, toEnd, constrained, out transferred);
                info = _m.InfoOf(opened);
                return status;
            }

            public override int Flush(object? context, out WinFsp.FileInfo info)
            {
                info = default;
                if (context is not Opened opened) return WinFsp.Success;
                // Nothing of a pending file is on this PC's disk to flush; it goes
                // to Google as it is written and when it is closed.
                info = _m.InfoOf(opened);
                return WinFsp.Success;
            }

            public override int GetFileInfo(object context, out WinFsp.FileInfo info)
            {
                info = _m.InfoOf((Opened)context);
                return WinFsp.Success;
            }

            public override int SetBasicInfo(object context, uint attributes, ulong created, ulong accessed,
                ulong written, ulong changed, out WinFsp.FileInfo info)
            {
                var opened = (Opened)context;
                if (written != 0 && written != ulong.MaxValue && !opened.Folder && !_m.ReadOnlyAt(opened.Key))
                {
                    var when = DateTime.FromFileTimeUtc((long)written);
                    _m.SetModified(opened, when);
                }
                info = _m.InfoOf(opened);
                return WinFsp.Success;
            }

            public override int SetFileSize(object context, ulong size, bool allocation, out WinFsp.FileInfo info)
            {
                info = default;
                var opened = (Opened)context;
                if (opened.Folder) return WinFsp.InvalidDeviceRequest;
                if (Closing) return WinFsp.NetworkUnreachable;
                using var busy = new InFlight(_m);

                long current = opened.Local is { } held ? held.Size : opened.Size;
                // Room asked for ahead of a write is not a change of size; only a
                // cut below the end is.
                if (allocation && (long)size >= current) { info = _m.InfoOf(opened); return WinFsp.Success; }
                if (!allocation && (long)size == current && opened.Local != null) { info = _m.InfoOf(opened); return WinFsp.Success; }
                if (_m.ReadOnlyAt(opened.Key)) return WinFsp.AccessDenied;

                int status = _m.Hold(opened, keepContents: size > 0);
                if (status != WinFsp.Success) return status;
                status = _m.SetLengthPending(opened.Local!, (long)size);
                info = _m.InfoOf(opened);
                return status;
            }

            public override int CanDelete(object context, string path)
            {
                var opened = (Opened)context;
                if (Closing) return WinFsp.NetworkUnreachable;
                using var busy = new InFlight(_m);
                if (_m.Fixed(opened.Key)) return WinFsp.AccessDenied;
                if (!opened.Folder) return WinFsp.Success;

                int status = _m.EnsureListed(opened.Key, out _);
                if (status != WinFsp.Success) return status;
                bool empty = !_m._listings.TryGetValue(opened.Key, out var rows) || rows.Length == 0;
                if (!empty) return WinFsp.DirectoryNotEmpty;
                // What the letter shows is not all a folder holds (Google Docs
                // are left out, and the listing can be old), so Drive is asked.
                status = _m.EmptyOnDrive(opened.Key);
                if (status == WinFsp.Success) opened.EmptyAt = Environment.TickCount64;
                return status;
            }

            public override int Rename(object context, string path, string newPath, bool replace)
            {
                var opened = (Opened)context;
                if (Closing) return WinFsp.NetworkUnreachable;
                using var busy = new InFlight(_m);
                return _m.RenameOpened(opened, KeyOf(path), KeyOf(newPath), LastName(newPath), replace);
            }

            public override int ReadDirectory(object context, char* pattern, char* marker, IntPtr buffer,
                uint length, out uint transferred)
            {
                transferred = 0;
                var opened = (Opened)context;
                if (!opened.Folder) return WinFsp.NotADirectory;
                if (Closing) return WinFsp.NetworkUnreachable;
                using var busy = new InFlight(_m);

                var key = opened.Key;
                if (marker == null)
                {
                    int status = _m.EnsureListed(key, out bool listedNow);
                    if (status != WinFsp.Success) return status;
                    if (!listedNow) _m.RefreshBehind(key);
                }

                uint n = 0;
                int read = WinFsp.DirBuffer.Read(ref opened.DirBuffer, marker, buffer, length, &n, () => _m.EntriesOf(key));
                transferred = n;
                return read;
            }

            public override void DispatcherStopped(bool normally)
            {
                if (!normally) _m._log("WinFsp stopped the volume by itself (driver being removed?)");
            }
        }

        // ---- reading what is only on Drive -----------------------------------------

        /// <summary>
        /// A read of a file that is only on Drive, through its chunk cache: a
        /// piece at a time into Windows' buffer, each piece with its own
        /// deadline. Never answered short — a short read before the end looks
        /// like the end to most readers — so a piece that will not come fails
        /// the whole read.
        /// </summary>
        internal unsafe int ReadFromDrive(Opened opened, IntPtr buffer, ulong offset, uint length, out uint transferred)
        {
            transferred = 0;
            var id = opened.Id;
            if (id == null) return WinFsp.ObjectNameNotFound;
            long size = opened.Size;
            if ((long)offset >= size) return WinFsp.EndOfFile;
            long start = (long)offset;
            long end = Math.Min(size, start + length);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool fromMemory = true;

            DriveFileCache? leased = null;
            try
            {
                leased = CacheOpenedFor(id, size);
                var cache = leased;

                if (Notify != null && _fetchStarted.TryAdd(id, true))
                    Say("drive.download.start", $"Fetching {RowAt(opened.Key)?.Name ?? LeafOf(opened.Key)}");

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
                deadline.CancelAfter(DataDeadline);

                long at = start;
                while (at < end)
                {
                    long pieceEnd = Math.Min(end, Math.Min(
                        (at / DriveCachePool.ChunkBytes + 1) * DriveCachePool.ChunkBytes, at + PieceBytes));
                    int want = (int)(pieceEnd - at);

                    var (bytes, memory) = cache.Read(at, want, deadline.Token).GetAwaiter().GetResult();
                    fromMemory &= memory;
                    if (bytes.Length < want)
                    {
                        _log($"  short read [{at:N0} +{bytes.Length:N0}] of {want:N0} wanted");
                        Interlocked.Increment(ref FailedReads);
                        return WinFsp.UnexpectedNetworkError;
                    }

                    fixed (byte* source = bytes)
                        Buffer.MemoryCopy(source, (byte*)buffer + (at - start), want, want);

                    if (Volatile.Read(ref _watchCount) > 0)
                        NoteFetch(DisplayPath(opened.Key), at, pieceEnd, size);

                    at = pieceEnd;
                    if (at < end) deadline.CancelAfter(DataDeadline);
                }

                transferred = (uint)(end - start);
                if (Notify != null && end >= size && _fetchFinished.TryAdd(id, true))
                    Say("drive.download.done", $"{RowAt(opened.Key)?.Name ?? LeafOf(opened.Key)} ready");

                Fetches.Enqueue((start, end - start, clock.ElapsedMilliseconds, fromMemory));
                while (Fetches.Count > MostRememberedFetches) Fetches.TryDequeue(out _);
                if (TraceFetches) _log($"  read {id[..Math.Min(8, id.Length)]} [{start:N0} +{end - start:N0}] in {clock.ElapsedMilliseconds}ms");
                return WinFsp.Success;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref FailedReads);
                if (TraceFetches || !_closing.IsCancellationRequested) _log($"  read failed [{start:N0} +{end - start:N0}]: {ex.Message}");
                return StatusOf(ex);
            }
            finally
            {
                // Never as a warm. A read that got as far as the cache has
                // marked it read already; one that failed before that is a
                // failure, and must not start the whole file downloading.
                leased?.EndLease(warm: false);
            }
        }
    }
}
