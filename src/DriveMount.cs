using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static ExplorerNative.CfApi;

namespace ExplorerNative
{
    /// <summary>
    /// One entry as it was actually placed on disk: the name after Sanitise and
    /// Unique have had it, not the name Drive returned.
    ///
    /// The distinction is the whole reason this is a separate type from
    /// <c>DriveEntry</c>. Drive allows two files in one folder to share a name
    /// and allows characters NTFS will not take, so the name on the drive letter
    /// is frequently not the name in the account — and a listing that handed
    /// back Drive's version would produce paths that do not open.
    ///
    /// <c>Size</c> is -1 for a folder, matching what the pane means by "not
    /// measured".
    /// </summary>
    /// <param name="DriveName">
    /// What Drive calls it, when that is known: <paramref name="Name"/> is the
    /// name on disk, cleaned up and numbered.
    /// </param>
    public sealed record DrivePlaced(string Name, bool IsFolder, long Size, DateTime Modified,
        string? DriveName = null);

    /// <summary>
    /// Google Drive as real files on a real drive letter, with no kernel driver,
    /// no rclone and no Google desktop application.
    ///
    /// The files are placeholders: no disk, correct length, contents fetched when
    /// something reads them. Every Win32 API sees ordinary files, which is the
    /// entire reason for building it this way — enumeration, tags, the shell
    /// context menu and the media engine all keep working without knowing Drive
    /// is there.
    ///
    /// Directories populate on demand. Placing the whole tree at mount time would
    /// be one Drive listing per folder for a library nobody has opened yet; with
    /// a PARTIAL population policy Windows asks as each folder is entered, which
    /// costs one listing at the moment somebody actually looks.
    /// </summary>
    internal sealed class DriveMount : IDisposable
    {
        private const int ChunkAlignment = 4096;
        private const uint FileAttributeDirectory = 0x10;
        private const uint FileAttributeNormal = 0x80;

        private readonly DriveClient _drive;
        private readonly DriveCachePool _pool;
        private readonly Action<string> _log;

        private readonly ConcurrentDictionary<string, DriveFileCache> _caches = new();

        // Relative path (lower case, "" for the root) to the Drive folder it came
        // from. Grown as directories are populated, which is the only time a new
        // path can appear.
        private readonly ConcurrentDictionary<string, string> _folders = new();

        // The same for everything, files included. Writing needs it: to trash a
        // file or move it, Drive wants an id, and all the caller has is a path.
        // The id is on the placeholder as its file identity, but reading it back
        // means opening the file — which for a placeholder means hydrating it,
        // and hydrating a gigabyte to find out what to call it is absurd.
        private readonly ConcurrentDictionary<string, string> _ids = new();

        // Which shared drive a folder is in, for folders that are in one. A
        // listing of a shared drive's folder has to name the drive or it comes
        // back empty.
        private readonly ConcurrentDictionary<string, string> _driveIds = new();

        /// <summary>The shared drive a directory on this mount is in, or null.</summary>
        public string? DriveIdFor(string? fullPath)
        {
            var key = KeyFor(fullPath);
            return key != null && _driveIds.TryGetValue(key, out var id) ? id : null;
        }

        /// <summary>Whether this path is the folder that lists the shared drives.</summary>
        public bool IsSharedDrivesFolder(string? fullPath) =>
            FolderIdFor(fullPath) == DriveClient.SharedDrivesId;

        /// <summary>
        /// Whether this path is one of the shared drives themselves. Those are
        /// drives, not folders: Drive will not trash, rename or move one through
        /// the files API.
        /// </summary>
        public bool IsSharedDriveRoot(string? fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            string? parent;
            try { parent = Path.GetDirectoryName(fullPath.TrimEnd(Path.DirectorySeparatorChar)); }
            catch { return false; }
            return IsSharedDrivesFolder(parent);
        }

        /// <summary>
        /// What each populated directory contains, as it was placed.
        ///
        /// This is the listing that has already been paid for. Populate fetches
        /// every name, size and timestamp from Drive and then writes them into
        /// placeholders — and throwing the list away at that point is what made
        /// browsing the drive slow, because the only way back to the same facts
        /// was to enumerate the placeholders again.
        ///
        /// Measured on a folder of 2118 tracks: enumerating the placeholders
        /// takes **33 seconds**, every single time, and never gets cached. The
        /// same folder as ordinary files takes 310ms for twice as many entries.
        /// It is about fifteen milliseconds per placeholder, paid on every visit,
        /// and it is the whole of "why does loading Drive folders lag".
        ///
        /// The names here are the names on disk, after Sanitise and Unique have
        /// had them — not the names Drive returned. A cache that disagreed with
        /// the filesystem about what a file is called would hand back paths that
        /// do not open.
        /// </summary>
        private readonly ConcurrentDictionary<string, DrivePlaced[]> _listings = new();

        /// <summary>When each folder's whole listing last came from Drive.</summary>
        private readonly ConcurrentDictionary<string, long> _listedAt = new();

        /// <summary>
        /// What Relative answers when a path is not on this mount at all.
        /// A sentinel rather than a guess: a manufactured key that happens to
        /// collide with a real one is indistinguishable from a correct answer.
        /// </summary>
        private const string NotOnThisMount = "\u0000not-on-this-mount";

        /// <summary>
        /// A path as this mount's key for it, or null when it is not one.
        ///
        /// Path.GetFullPath throws on an empty string, and every lookup here used
        /// to call it unguarded — so asking about the parent of a root-level item,
        /// which is how a move works out where something came from, threw
        /// ArgumentException rather than answering "I do not know".
        /// </summary>
        private string? KeyFor(string? fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return null;
            try
            {
                var key = Relative(Path.GetFullPath(fullPath));
                return key == NotOnThisMount ? null : key;
            }
            catch { return null; }
        }

        /// <summary>The Drive id behind a path on this mount, or null.</summary>
        public string? IdFor(string? fullPath)
        {
            var key = KeyFor(fullPath);
            return key != null && _ids.TryGetValue(key, out var id) ? id : null;
        }

        /// <summary>
        /// The path of whatever inside <paramref name="folderFullPath"/> has this
        /// Drive id, or null when that folder has not been populated or holds no
        /// such thing.
        ///
        /// This is the other direction from <see cref="IdFor"/>, and search needs
        /// it because **the name in Drive is frequently not the name on disk**.
        /// Drive allows two files in one folder to share a name and allows
        /// characters NTFS will not take, so <c>Sanitise</c> and <c>Unique</c>
        /// have both had a go at everything placed here: a hit Drive calls
        /// <c>AC:DC.flac</c> is <c>AC_DC.flac</c> on the letter, and the second
        /// <c>song.flac</c> in a folder is <c>song (2).flac</c>. Building a path
        /// out of the name Drive returned would produce results that do not open
        /// — which is the same trap <c>DrivePlaced</c> exists to keep the pane
        /// out of.
        ///
        /// Matching on the id instead is exact. It costs a walk of the folder's
        /// own listing, which is already in memory, and is done once per hit.
        /// </summary>
        public string? ChildPathForId(string folderFullPath, string driveId)
        {
            var key = KeyFor(folderFullPath);
            if (key == null || string.IsNullOrEmpty(driveId)) return null;
            if (!_listings.TryGetValue(key, out var placed)) return null;

            foreach (var item in placed)
            {
                var childKey = (key.Length == 0 ? item.Name : key + "\\" + item.Name).ToLowerInvariant();
                if (_ids.TryGetValue(childKey, out var id) && id == driveId)
                    return Path.Combine(folderFullPath, item.Name);
            }

            return null;
        }

        /// <summary>The Drive folder id for a directory on this mount, or null.</summary>
        public string? FolderIdFor(string? fullPath)
        {
            var key = KeyFor(fullPath);
            return key != null && _folders.TryGetValue(key, out var id) ? id : null;
        }

        /// <summary>
        /// Adds one entry that has just appeared in Drive, so an upload shows up
        /// without re-listing the folder it went into. Returns the name it was
        /// actually given on disk, or null when it could not be placed.
        ///
        /// The name matters to the caller and did not used to be handed back.
        /// Drive lets two files in one folder share a name and NTFS does not, so
        /// uploading a second "song.flac" into a folder that already has one is a
        /// perfectly ordinary thing to do — and the placeholder for it has to be
        /// called something else. Whoever asked for the upload then needs to be
        /// told what it ended up as, and a tree upload needs it to know where to
        /// put the next level down.
        ///
        /// <paramref name="populated"/> is for a folder this application has just
        /// *created*, where "what is in it" is known without asking: nothing.
        /// Marking it saves a Drive listing on the first visit whose only possible
        /// answer is the empty one we already have.
        /// </summary>
        public string? PlaceOne(string parentFullPath, DriveEntry entry, bool populated = false)
        {
            var pins = new List<IntPtr>();
            var names = new List<string>(1);
            try
            {
                string? relative = KeyFor(parentFullPath);
                if (relative == null) return null;

                // Something new inside a shared drive is in that drive, and a folder
                // that does not know so lists as empty.
                if (string.IsNullOrEmpty(entry.DriveId) && _driveIds.TryGetValue(relative, out var inDrive))
                    entry = entry with { DriveId = inDrive };

                // Seeded with what is already there, which is the difference
                // between a duplicate name being renamed and a duplicate name
                // quietly stealing the map entry of the file it collided with.
                //
                // Unique is given a fresh set on the populate path because that
                // path places the whole folder at once. Here one entry arrives
                // into a folder somebody is looking at, and without the seed the
                // set is empty, the name is taken as free, CfCreatePlaceholders
                // returns "already exists" — and _ids has by then been rewritten
                // to point the *old* placeholder's path at the *new* Drive file.
                // The next delete on that path trashes the wrong one.
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_listings.TryGetValue(relative, out var siblings))
                    foreach (var s in siblings) used.Add(s.Name);

                string target = relative.Length == 0 ? Root : Path.Combine(Root, relative);
                string? name = null;

                // "Already exists" means something on disk holds the name that no
                // listing here knows about — and BuildPlaceholders has by then
                // pointed that name at this entry's id. Accepted as it stood, the
                // map named the wrong Drive object for a file on screen, which is
                // what a later delete or move acts on. So the claim is undone and
                // the next free name tried, unless what holds the name is this very
                // entry, placed already.
                for (int attempt = 0; attempt < 8 && name == null; attempt++)
                {
                    // What the listing held before this attempt, to put back if
                    // the claim fails: the attempt merges its row in, and a
                    // failed one left that row behind, showing this file's size
                    // over a name that opens another.
                    _listings.TryGetValue(relative, out var listedBefore);

                    var trial = new HashSet<string>(used, StringComparer.OrdinalIgnoreCase);
                    var wanted = Unique(Sanitise(entry.Name), trial, entry.IsFolder);
                    var key = (relative.Length == 0 ? wanted : relative + "\\" + wanted).ToLowerInvariant();
                    _ids.TryGetValue(key, out var previousId);
                    _folders.TryGetValue(key, out var previousFolder);

                    names.Clear();
                    var array = BuildPlaceholders(relative, new List<DriveEntry> { entry }, pins, used, names);
                    if (array.Length == 0 || names.Count == 0) return null;

                    int hr = CfCreatePlaceholders(target, array, (uint)array.Length,
                        CF_PLACEHOLDER_CREATE_FLAGS.NONE, out _);

                    if (hr == 0 || (IsAlreadyThere(hr) && previousId == entry.Id))
                    {
                        name = names[0];
                        if (hr != 0) RefreshNow(target, ExistingToCheck(array, null));
                        break;
                    }

                    if (!IsAlreadyThere(hr)) return null;

                    // Held by a placeholder nothing here is bound to any more —
                    // a file since trashed, forgotten by the listing that noticed.
                    // It is taken over for this entry rather than left serving the
                    // trashed file under the plain name while this one is "(2)".
                    if (previousId == null && !entry.IsFolder &&
                        RefreshOne(target, names[0], entry.Id, Math.Max(0, entry.Size), StampOf(entry)))
                    {
                        name = names[0];
                        break;
                    }

                    // Put back what the failed claim overwrote: this name's binding
                    // and this name's row, and nothing else. Putting back the whole
                    // listing wiped rows other uploads had added meanwhile.
                    if (previousId != null) _ids[key] = previousId; else _ids.TryRemove(key, out _);
                    if (previousFolder != null) _folders[key] = previousFolder; else _folders.TryRemove(key, out _);

                    var clash = names[0];
                    var oldRow = listedBefore?.FirstOrDefault(p => p.Name.Equals(clash, StringComparison.OrdinalIgnoreCase));
                    if (_listings.TryGetValue(relative, out var listedNow))
                    {
                        var rows = listedNow.Where(p => !p.Name.Equals(clash, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (oldRow != null) rows.Add(oldRow);
                        _listings[relative] = rows.ToArray();
                    }
                    // used already holds the clashing name, so the next build moves on.
                }

                if (name == null) return null;

                if (populated)
                {
                    var child = (relative.Length == 0 ? name : relative + "\\" + name).ToLowerInvariant();
                    _populated[child] = true;
                    _listings.TryAdd(child, Array.Empty<DrivePlaced>());
                }

                return name;
            }
            catch { return null; }
            finally { foreach (var p in pins) Marshal.FreeHGlobal(p); }
        }

        /// <summary>
        /// The Drive id of a folder already known to be inside this one, or null.
        ///
        /// What makes it worth having is that uploading a folder into a folder
        /// that already contains one of that name should *merge*, the way pasting
        /// does everywhere else — not make a second folder with the same name,
        /// which Drive would happily allow and which NTFS could then only show as
        /// "album (2)".
        ///
        /// The name is sanitised on the way in because the map is keyed by the
        /// name as placed, not the name Drive returned.
        /// </summary>
        public string? ChildFolderId(string parentFullPath, string name)
        {
            var key = KeyFor(parentFullPath);
            if (key == null) return null;

            var child = (key.Length == 0 ? Sanitise(name) : key + "\\" + Sanitise(name)).ToLowerInvariant();
            return _folders.TryGetValue(child, out var id) ? id : null;
        }

        /// <summary>
        /// Removes the placeholder for something that is no longer in Drive.
        /// Works because the provider is connected; nothing can delete a
        /// placeholder otherwise.
        /// </summary>
        public bool RemoveOne(string fullPath)
        {
            try
            {
                var key = KeyFor(fullPath);
                if (key == null) return false;

                // Everything underneath as well, not just this key.
                //
                // Trashing a folder takes its whole placeholder tree, and leaving
                // the children in the map leaves live Drive ids addressed by paths
                // that no longer exist — so a later lookup could hand one to Trash
                // or MoveOrRename. CLAUDE.md is explicit that the path-to-id half
                // is where a mistake deletes the wrong thing, and this was that
                // mistake waiting for a folder to be deleted.
                var prefix = key.Length == 0 ? "" : key + "\\";

                foreach (var map in new[] { _ids, _folders })
                    foreach (var entry in map.Keys)
                        if (entry == key || (prefix.Length > 0 &&
                            entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                            map.TryRemove(entry, out _);

                foreach (var entry in _populated.Keys)
                    if (entry == key || (prefix.Length > 0 &&
                        entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        _populated.TryRemove(entry, out _);

                foreach (var entry in _listings.Keys)
                    if (entry == key || (prefix.Length > 0 &&
                        entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        _listings.TryRemove(entry, out _);

                // And out of the parent's listing, which is what the pane is
                // built from. Dropping the maps for the thing itself is not
                // enough: the folder above still has it in the list it was
                // populated with, so a deleted file would go on being shown
                // until something re-fetched that folder.
                int cut = key.LastIndexOf('\\');
                string parent = cut < 0 ? "" : key[..cut];
                string leaf = cut < 0 ? key : key[(cut + 1)..];

                if (_listings.TryGetValue(parent, out var siblings))
                {
                    var kept = siblings
                        .Where(s => !string.Equals(s.Name, leaf, StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    if (kept.Length != siblings.Length) _listings[parent] = kept;
                }

                if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
                else if (File.Exists(fullPath)) File.Delete(fullPath);
                return true;
            }
            catch { return false; }
        }

        // Rooted for the life of the mount. A delegate handed to native code and
        // then collected is a crash on a thread with no stack of ours.
        private CF_CALLBACK? _fetchData;
        private CF_CALLBACK_PLACEHOLDERS? _fetchPlaceholders;
        private CF_CALLBACK_CANCEL? _cancelFetch;

        private long _connection;
        private bool _registered;
        private DriveLetter? _letter;

        public string Root { get; }
        public string? Letter => _letter?.Letter;

        /// <summary>
        /// Puts the mounted drive on another letter without unmounting. The new
        /// letter is defined on the same folder before the old one is removed,
        /// so Drive is never off a letter in between and nothing is re-listed.
        /// Does nothing when it is already on <paramref name="preferred"/>.
        /// Falls back to the next free letter, as a first mount does.
        /// </summary>
        public void MoveLetter(char preferred)
        {
            var current = _letter;
            if (current == null) return;
            if (char.ToUpperInvariant(current.Letter[0]) == char.ToUpperInvariant(preferred)) return;

            var next = DriveLetter.Assign(Root, preferred, Notify);
            _letter = next;
            current.Dispose();
            _log($"drive letter {current.Letter} -> {next.Letter}");
        }

        public ConcurrentQueue<(long Offset, long Length, long Milliseconds, bool FromMemory)> Fetches { get; } = new();

        /// <summary>How many of those to keep. Enough for a whole track, not a whole day.</summary>
        private const int MostRememberedFetches = 4096;
        public int Listings;
        public int CancelledTransfers;

        public DriveMount(DriveClient drive, string root, long budgetBytes, Action<string> log)
        {
            _drive = drive;
            Root = root;
            _pool = new DriveCachePool(budgetBytes);
            _log = log;
            _sweep = new Timer(_ => SweepIdle(), null, SweepInterval, SweepInterval);
        }

        // ---- warming, and letting go again ----------------------------------

        /// <summary>
        /// How long a file may sit unread before its download and its chunks are
        /// let go. Zero never releases, which is the old behaviour.
        ///
        /// Set from Preferences, and read on every sweep rather than captured, so
        /// changing it takes effect without remounting.
        /// </summary>
        public TimeSpan IdleRelease { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// How often the sweep looks. Deliberately much shorter than any idle
        /// setting worth having: the cost is a dictionary walk over at most
        /// <see cref="MaxOpenFiles"/> entries, and a coarse tick would round a
        /// two-minute setting up to something the user did not ask for.
        /// </summary>
        private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

        private readonly Timer _sweep;

        /// <summary>
        /// How far into a selection <see cref="Warm"/> will look. Enough that a
        /// file too big to hold does not cost a place, small enough that the cost
        /// on the copying thread is a handful of metadata reads — measured at
        /// 0.15 to 0.42ms each — rather than one per selected file.
        /// </summary>
        private const int MostWarmCandidates = MaxOpenFiles * 4;

        /// <summary>
        /// Starts pulling whole files down before anybody asks for them.
        ///
        /// This exists for one measured gap. Copying a Drive file to the
        /// clipboard and pasting it into another application — a DAW importing a
        /// track — puts a placeholder path in that application's hands and
        /// nothing else, so the *first byte it reads* is the first byte fetched
        /// from Google. It reads synchronously on the thread that pumps its
        /// window, so the whole download happens with that window frozen: 3.9
        /// seconds for a 16.8MB track, and linear from there — a minute for the
        /// 232MB one. Windows paints "(Not Responding)" over the lot.
        ///
        /// The same file once this has had it: 111ms. The bytes are not slow, the
        /// timing is — so the fix is to spend the seconds somebody takes to
        /// switch windows on the download instead of spending them inside the
        /// reader. <see cref="CacheOpenedFor"/> starts the four-stream pump in
        /// the cache's constructor, so opening one is the whole of the work.
        ///
        /// Returns how many files were taken on, which is not always how many
        /// were asked for — see the two bounds below.
        /// </summary>
        public int Warm(IEnumerable<string> paths)
        {
            if (_disposed) return 0;
            var wanted = new List<(string Id, long Size, string Path)>();

            foreach (var path in paths)
            {
                // Bounded before anything is measured, because this runs on the
                // thread that took the copy. A selection is not small — this
                // application has already had a thirteen-second window freeze out
                // of doing a per-file lookup for three thousand of them — and
                // only the first few can be warmed anyway. A few more than that
                // are looked at so that one file too big for the pool does not
                // use up a place; see WarmPlan.
                if (wanted.Count >= MostWarmCandidates) break;

                var id = IdFor(path);
                if (id == null) continue;

                // Already open is already warming, and re-opening would only
                // refresh its place in the queue.
                if (_caches.ContainsKey(id)) continue;

                long size;
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) continue;

                    // Length on a placeholder is metadata and does not hydrate
                    // anything: the size was written into the placeholder when it
                    // was placed. This is the one filesystem call here, and it is
                    // the reason a folder is not warmed by accident.
                    size = info.Length;
                }
                catch { continue; }

                if (size <= 0) continue;
                wanted.Add((id, size, path));
            }

            if (wanted.Count == 0) return 0;

            int started = 0;

            foreach (int index in WarmPlan(
                         wanted.ConvertAll(item => item.Size), MaxOpenFiles, _pool.Budget))
            {
                var (id, size, path) = wanted[index];
                try
                {
                    CacheOpenedFor(id, size).EndLease();
                    started++;
                }
                catch (Exception ex)
                {
                    _log($"  warm failed for {Path.GetFileName(path)}: {ex.Message}");
                }
            }

            if (started > 0) _log($"warming {started} file(s) ahead of a read");
            return started;
        }

        /// <summary>
        /// Which of a set of candidate files are worth warming, as indexes into
        /// <paramref name="sizes"/>, in the order they were asked for.
        ///
        /// Separated out and pure because both bounds are easy to get subtly
        /// wrong and neither is visible from outside once it is right.
        ///
        /// **The count bound.** Never take on more files than may be open at
        /// once. Warming a fourth calls <see cref="KeepRecent"/> and disposes the
        /// first, so warming ten would start ten downloads, cancel seven and
        /// discard what they had fetched — the link spent to no purpose, and the
        /// three survivors being the ones asked for *last* rather than the ones
        /// anybody is about to read.
        ///
        /// **The budget bound.** A file bigger than the whole pool streams
        /// perfectly well — that is what the chunking is for — but it cannot be
        /// *held*, so warming it would evict everything else warmed alongside it
        /// and still arrive at the reader cold. It is skipped rather than allowed
        /// to end the plan, because a 4GB video first in a selection should not
        /// stop the three tracks behind it being warmed.
        /// </summary>
        internal static List<int> WarmPlan(IReadOnlyList<long> sizes, int maxOpen, long budget)
        {
            var plan = new List<int>();
            long room = budget;

            for (int i = 0; i < sizes.Count && plan.Count < maxOpen; i++)
            {
                long size = sizes[i];
                if (size <= 0 || size > room) continue;

                room -= size;
                plan.Add(i);
            }

            return plan;
        }

        /// <summary>
        /// Lets go of anything nothing has read for <see cref="IdleRelease"/>.
        ///
        /// The pool has a byte budget already, but a budget only gives memory
        /// back when something else wants it: warm a file, walk away, and its
        /// chunks sit there for as long as the mount is up. This is the other
        /// half — time rather than size — and it is what makes warming on copy
        /// safe to leave switched on.
        /// </summary>
        private void SweepIdle()
        {
            var idle = IdleRelease;
            if (idle <= TimeSpan.Zero) return;

            // Never underneath a reader. Releasing a cache cancels its downloads,
            // so a sweep landing in the middle of a fetch callback would fail the
            // read that callback is answering — a copy that stops dead for no
            // reason anybody can see. A file being read has a fresh LastUsed and
            // would survive the cut below anyway; this is the belt to that
            // braces, and it costs one volatile read.
            if (Volatile.Read(ref _callbacksInFlight) > 0) return;

            long cutoff = Environment.TickCount64 - (long)idle.TotalMilliseconds;

            foreach (var (id, cache) in _caches.ToArray())
            {
                if (cache.LastUsed > cutoff) continue;
                if (!Retire(id, cache)) continue;

                _fetchStarted.TryRemove(id, out _);
                _fetchFinished.TryRemove(id, out _);
                _log($"  released {id} after {idle.TotalSeconds:0}s idle");
            }
        }

        /// <summary>
        /// Where a user-facing message goes, as (notification id, sentence).
        ///
        /// Separate from <c>_log</c>, which is diagnostics for a file nobody
        /// reads unless something has gone wrong. Null when nobody is listening,
        /// and everything below checks that before building a string: the fetch
        /// callback runs 891 times for one 232MB track, on a filter-driver worker
        /// thread that is holding up whoever is reading the file.
        /// </summary>
        public Action<string, string>? Notify { get; set; }

        private void Say(string id, string message)
        {
            var notify = Notify;
            if (notify == null) return;

            // Never on the caller's account. A handler that throws inside a Cloud
            // Files callback would fail the read that happened to be underway.
            try { notify(id, message); } catch { }
        }

        // Which files have had their first and their last byte served. Only used
        // to say each thing once; the ids are dropped when the cache is.
        private readonly ConcurrentDictionary<string, bool> _fetchStarted = new();

        /// <summary>
        /// Transfers that want to know what is being downloaded for them.
        ///
        /// A copy out of the Drive letter is robocopy reading placeholders, and
        /// every byte it gets comes through <see cref="OnFetchData"/> in this
        /// process first. Robocopy itself cannot say how far along a file is — it
        /// writes nothing until Drive has delivered, and its "New File" line comes
        /// through a block-buffered pipe after the file is done — so the progress
        /// window sat at zero, named nothing, and then jumped a whole file at a
        /// time, one name behind. Measured on six tracks: four and a half seconds
        /// at 0% before the first number moved. This is the honest source.
        /// </summary>
        private readonly List<FetchWatch> _watches = new();
        private int _watchCount;

        /// <summary>
        /// Callbacks being answered right now, so the provider is never taken
        /// down underneath one. See <see cref="WaitForCallbacks"/>.
        /// </summary>
        private int _callbacksInFlight;

        /// <summary>
        /// Waits for the provider to go quiet, and says whether it did.
        ///
        /// A read of a placeholder is answered by this process, so disconnecting
        /// while one is in flight leaves the reader waiting for an answer that
        /// can never arrive — and if the reader is in this process, this process
        /// can never finish exiting. That is a machine with something in it that
        /// cannot be killed and a user who has to reboot to be rid of it.
        /// </summary>
        internal bool WaitForCallbacks(TimeSpan limit)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            while (Volatile.Read(ref _callbacksInFlight) > 0)
            {
                if (clock.Elapsed >= limit)
                {
                    _log($"  disconnecting with {Volatile.Read(ref _callbacksInFlight)} callback(s) " +
                         $"still in flight after {limit.TotalSeconds:0.#}s");
                    return false;
                }

                Thread.Sleep(10);
            }

            return true;
        }

        /// <summary>
        /// What has been downloaded for a set of paths while this is alive.
        /// </summary>
        public sealed class FetchWatch : IDisposable
        {
            private readonly DriveMount? _owner;
            private readonly string[] _prefixes;
            private readonly ConcurrentDictionary<string, Ranges> _served = new(StringComparer.OrdinalIgnoreCase);
            private string? _current;

            internal FetchWatch(DriveMount? owner, string[] prefixes)
            {
                _owner = owner;
                _prefixes = prefixes;
            }

            /// <summary>
            /// Distinct bytes handed to readers, summed over every file — each byte
            /// counted once however often it was asked for, and nothing counted
            /// that was not actually served.
            /// </summary>
            public long BytesServed => _served.Values.Sum(r => r.Total);

            /// <summary>The name of the file most recently served, or null before any.</summary>
            public string? Current => Volatile.Read(ref _current);

            /// <summary>
            /// Records a served range, [<paramref name="start"/>, <paramref name="end"/>)
            /// of a file under the sync root.
            ///
            /// The union of ranges, not the furthest byte, and the difference is
            /// the progress window lying. It kept the furthest byte reached, on the
            /// reasoning that a file is read front to back — and it is not: Windows
            /// reads ahead, a virus scanner looks at the end of a new file, a WAV
            /// reader wants the trailing chunks. One read of the last four
            /// megabytes and a 65MB file counted as finished, so the window said
            /// "100 percent, less than a second" for a minute while robocopy was
            /// still pulling the file down.
            /// </summary>
            internal void Note(string relative, long start, long end, long size)
            {
                if (!_prefixes.Any(p => Covers(p, relative))) return;

                start = Math.Max(0, start);
                end = Math.Min(end, size);
                if (end <= start) return;

                _served.GetOrAdd(relative, _ => new Ranges()).Add(start, end);
                Volatile.Write(ref _current, Path.GetFileName(relative));
            }

            /// <summary>A set of byte ranges, merged as they arrive.</summary>
            internal sealed class Ranges
            {
                private readonly List<(long Start, long End)> _merged = new();
                private long _total;

                public long Total { get { lock (_merged) return _total; } }

                public void Add(long start, long end)
                {
                    lock (_merged)
                    {
                        // Swallow every range this one touches, then put back the
                        // one range that covers them all. A file is a few dozen
                        // four-megabyte chunks, so a list scan is cheaper than any
                        // structure clever enough to avoid it.
                        for (int i = _merged.Count - 1; i >= 0; i--)
                        {
                            var (s0, e0) = _merged[i];
                            if (e0 < start || s0 > end) continue;
                            start = Math.Min(start, s0);
                            end = Math.Max(end, e0);
                            _total -= e0 - s0;
                            _merged.RemoveAt(i);
                        }

                        _merged.Add((start, end));
                        _total += end - start;
                    }
                }
            }

            /// <summary>
            /// Whether <paramref name="relative"/> is <paramref name="prefix"/> or
            /// somewhere under it. On a path boundary, so "music\a" does not claim
            /// "music\ab". The empty prefix is the whole drive.
            /// </summary>
            internal static bool Covers(string prefix, string relative)
            {
                if (prefix.Length == 0) return true;
                if (!relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
                return relative.Length == prefix.Length || relative[prefix.Length] == '\\';
            }

            /// <summary>For the suite, which has no sync root to mount.</summary>
            internal static FetchWatch Detached(params string[] prefixes) => new(null, prefixes);

            public void Dispose() => _owner?.Unwatch(this);
        }

        /// <summary>
        /// Starts watching downloads for <paramref name="paths"/>, which may be
        /// on the drive letter or under the sync root.
        /// </summary>
        public FetchWatch Watch(IEnumerable<string> paths)
        {
            var prefixes = new List<string>();
            foreach (var path in paths)
            {
                var relative = RelativeToMount(path);
                if (relative != null) prefixes.Add(relative);
            }

            var watch = new FetchWatch(this, prefixes.ToArray());
            lock (_watches)
            {
                _watches.Add(watch);
                _watchCount = _watches.Count;
            }
            return watch;
        }

        private void Unwatch(FetchWatch watch)
        {
            lock (_watches)
            {
                _watches.Remove(watch);
                _watchCount = _watches.Count;
            }
        }

        /// <summary>A path on the letter or under the root, as a path under the root.</summary>
        private string? RelativeToMount(string path)
        {
            try
            {
                var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                if (Letter != null && IsAtOrUnder(full, Letter))
                    return full.Length <= 2 ? "" : full[2..].Trim(Path.DirectorySeparatorChar);
                if (IsAtOrUnder(full, Root))
                    return full[Root.TrimEnd(Path.DirectorySeparatorChar).Length..].Trim(Path.DirectorySeparatorChar);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Called from the fetch callback, on a filter-driver worker, so it is
        /// cheap: nothing happens at all unless a transfer is watching.
        /// </summary>
        private void NoteFetch(string? normalizedPath, long start, long end, long size)
        {
            if (string.IsNullOrEmpty(normalizedPath)) return;

            // The callback's path starts at the root of the volume with no drive
            // letter — "\Users\...\GoogleDrive\music\x.flac" — so the root is
            // compared without its own letter.
            //
            // Or, depending on the connect flags and the Windows build, with the
            // letter: both are accepted, anchored on a separator either way.
            var rootFull = Root.TrimEnd(Path.DirectorySeparatorChar);
            var rootTail = rootFull.Length > 2 ? rootFull[2..] : rootFull;
            string relative;
            if (normalizedPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) &&
                EndsOnABoundary(normalizedPath, rootFull.Length))
                relative = normalizedPath[rootFull.Length..];
            else if (normalizedPath.StartsWith(rootTail, StringComparison.OrdinalIgnoreCase) &&
                     EndsOnABoundary(normalizedPath, rootTail.Length))
                relative = normalizedPath[rootTail.Length..];
            else return;
            relative = relative.Trim(Path.DirectorySeparatorChar);

            FetchWatch[] watching;
            lock (_watches) watching = _watches.ToArray();
            foreach (var watch in watching) watch.Note(relative, start, end, size);
        }
        private readonly ConcurrentDictionary<string, bool> _fetchFinished = new();

        private readonly ConcurrentDictionary<string, bool> _populated = new();

        /// <summary>
        /// One Drive listing, with what is happening to it said around it.
        ///
        /// Shared by all three routes into a listing — the root at mount time,
        /// the application asking ahead of navigation, and the filter driver
        /// asking on somebody else's behalf — so none of them can drift on what
        /// is said or on the count it is said about.
        /// </summary>
        /// <summary>
        /// How long a callback from the filter driver may wait on Google before it
        /// gives up and fails the request.
        ///
        /// There was no limit, and a callback is the one place that must have
        /// one. The listing goes through the retry loop, which waits for the
        /// internet to come back when Drive looks offline; the data read goes
        /// through a keep-alive that tells Windows "still working" every half
        /// second, which is exactly what stops Windows timing it out. Either way a
        /// callback could wait indefinitely — and every read of that file, and of
        /// others queued behind it, waited with it. Found with the player silent
        /// for good: its reads sat in ReadFile with the provider's only live
        /// callback parked on a folder listing.
        ///
        /// A failed callback is an ordinary read error, which robocopy retries,
        /// the player reports, and Explorer shows. A callback that never returns
        /// is none of those things. Placeholders get less than Windows' own sixty
        /// seconds so that ours is the failure that happens; data gets longer,
        /// because a slow link legitimately takes a while over four megabytes.
        /// </summary>
        private static readonly TimeSpan PlaceholderDeadline = TimeSpan.FromSeconds(45);

        private static readonly TimeSpan DataDeadline = TimeSpan.FromSeconds(90);

        private List<DriveEntry> ListFolder(string relative, string folderId, CancellationToken token,
            Action<List<DriveEntry>>? onPage = null)
        {
            string? name = Notify == null
                ? null
                : (relative.Length == 0 ? "Google Drive" : Path.GetFileName(relative));

            if (name != null) Say("drive.folder.loading", $"Fetching {name} from Google Drive");

            bool accountRoot = relative.Length == 0 && folderId == "root";
            var sharedDrives = new DriveEntry(DriveClient.SharedDrivesId, DriveClient.SharedDrivesName,
                DriveClient.FolderMimeType, 0, DateTime.UtcNow);

            // Ahead of everything else, for the reason given below.
            if (accountRoot) onPage?.Invoke(new List<DriveEntry> { sharedDrives });

            _driveIds.TryGetValue(relative, out var driveId);
            var entries = _drive.ListChildren(folderId, token, label: name, driveId: driveId, onPage: onPage)
                                .GetAwaiter().GetResult();
            Interlocked.Increment(ref Listings);

            // The shared drives, as a folder beside My Drive's own, the way
            // drive.google.com shows them — in every listing of the account root,
            // not only the first: a refresh of the letter that left it out took
            // the folder away, and gave its name to a real folder of the same
            // name, which Delete then sent to the Drive trash. First, so it keeps
            // its name: a My Drive folder called the same is "Shared drives (2)".
            if (accountRoot) entries.Insert(0, sharedDrives);

            if (name != null)
            {
                // Empty and populated are different answers to "why is there
                // nothing here", and a placeholder tree that has not been filled
                // in yet looks exactly like the first one.
                if (entries.Count == 0) Say("drive.folder.empty", $"{name} is empty");
                else Say("drive.folder.loaded", $"{name}: {NameRules.Items(entries.Count)}");
            }

            return entries;
        }

        /// <summary>
        /// Fills in a directory, once, from Drive.
        ///
        /// Windows will not ask for this, and the measurements say why. Under a
        /// FULL population policy the sync root refuses CfCreatePlaceholders
        /// entirely; under PARTIAL the root can be seeded but FETCH_PLACEHOLDERS
        /// is never raised for anything below it — every subdirectory enumerates
        /// as empty in under a millisecond with no error anywhere. The sync root
        /// also cannot be converted into a placeholder, which is what would let
        /// it be marked populated, so leaving on-demand population on simply
        /// re-asks the same question every 220ms forever.
        ///
        /// So population is driven from the application instead: this is called
        /// as a folder is navigated into, which costs one Drive listing at the
        /// moment somebody actually looks — the behaviour that was wanted, just
        /// not arranged by the filter driver.
        ///
        /// The cost of doing it this way is that only a caller who knows to call
        /// this sees a populated tree. Our own file manager does, because it owns
        /// its navigation. Explorer does not.
        /// </summary>
        public bool Populate(string directory, Action<IReadOnlyList<DrivePlaced>>? onPlaced = null)
        {
            string? relative = KeyFor(directory);
            if (relative == null) return false;
            // Claimed first, so two navigations into one folder do not both pay
            // for the listing — and **given back on every way out that did not
            // actually populate it**, which it was not. A folder whose listing
            // threw (a blip, an expired sign-in, the deadline) stayed marked, so
            // this path never looked at it again for the rest of the session: the
            // application's own navigation stopped paying the listing up front
            // and left the folder to the on-demand callback, one round trip
            // later, every single time.
            //
            // And a second caller while the first is still listing waits for it.
            // It used to return at once, and carry on as though the folder had
            // been listed: an upload making two sibling folders looked for the
            // second in a parent whose listing had not arrived, did not find it,
            // and made another in Drive.
            var mine = new ManualResetEventSlim(false);
            var running = _populating.GetOrAdd(relative, mine);
            if (!ReferenceEquals(running, mine))
            {
                try { running.Wait(PlaceholderDeadline); } catch { }
                return false;
            }

            if (!_populated.TryAdd(relative, true))
            {
                _populating.TryRemove(new KeyValuePair<string, ManualResetEventSlim>(relative, mine));
                mine.Set();
                return false;
            }

            bool populated = false;
            try
            {
            if (!_folders.TryGetValue(relative, out var folderId))
            {
                _log($"  no Drive folder known for '{relative}'");
                return false;
            }

            // Timed in two halves, because they are two completely different
            // costs and only one of them can be avoided. The listing is a
            // network round trip to Google and has to happen however the
            // placeholders are managed; creating the placeholders is local work
            // that a previous run has already done, if anything of it survived.
            // Knowing which dominates is what decides whether keeping them
            // between runs is worth the risk.
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // The same deadline the callback has. Without one this waited out an
            // offline spell for as long as it lasted — holding a navigation, a
            // paste's conflict check or a search, with nothing able to call it off.
            using var deadline = new CancellationTokenSource(PlaceholderDeadline);

            // Written into the real path, not the drive letter: the letter is
            // an alias this process made, and the API wants the directory the
            // sync root actually is.
            string target = relative.Length == 0 ? Root : Path.Combine(Root, relative);

            // A page at a time, placed and handed on as it arrives. A folder of a
            // few thousand is several pages at more than a second each, and the
            // window showed nothing until the last of them had been listed and
            // every placeholder made; now the first thousand rows are on screen
            // while the rest are still coming.
            var prior = PriorBindings(relative);
            var session = new PlacementSession(this, relative);
            int placedCount = 0;
            bool failed = false;
            long placeMs = 0;

            void Place(List<DriveEntry> page)
            {
                if (failed) return;
                var pins = new List<IntPtr>();
                try
                {
                    var placedPage = new List<DrivePlaced>(page.Count);
                    var array = BuildPlaceholders(relative, page, pins, session: session, placedPage: placedPage);
                    if (array.Length > 0)
                    {
                        var placing = System.Diagnostics.Stopwatch.StartNew();
                        int hr = CfCreatePlaceholders(target, array, (uint)array.Length,
                            CF_PLACEHOLDER_CREATE_FLAGS.NONE, out uint processed);
                        placeMs += placing.ElapsedMilliseconds;

                        // "Already exists" is a placeholder a previous listing made,
                        // which is a placed entry like any other.
                        if (hr != 0 && !IsAlreadyThere(hr))
                        {
                            _log($"  CfCreatePlaceholders 0x{hr:X8} for '{relative}'");
                            failed = true;
                            return;
                        }
                        if (hr != 0) RefreshLater(target, ExistingToCheck(array, prior));
                        placedCount += array.Length;
                    }

                    if (placedPage.Count > 0 && onPlaced != null)
                    {
                        try { onPlaced(placedPage); } catch { }
                    }
                }
                finally
                {
                    foreach (var p in pins) Marshal.FreeHGlobal(p);
                }
            }

            ListFolder(relative, folderId, deadline.Token, Place);
            long listMs = clock.ElapsedMilliseconds - placeMs;

            _log($"  populate '{relative}': {placedCount} entries, " +
                 $"listing {listMs}ms, placing {placeMs}ms{(failed ? ", failed" : "")}");

            if (failed) return false;

            // The whole listing, now that it is whole, replaces what was there.
            _listings[relative] = session.Placed.ToArray();
            _listedAt[relative] = Environment.TickCount64;
            ForgetGone(relative, session);

            // Listed, and the placeholders are there — whether this call made them
            // or found them already made. An empty folder is a listed folder too.
            populated = true;
            return placedCount > 0;
            }
            finally
            {
                if (!populated) _populated.TryRemove(relative, out _);
                _populating.TryRemove(new KeyValuePair<string, ManualResetEventSlim>(relative, mine));
                mine.Set();
            }
        }

        /// <summary>Populations in progress, for anyone who arrives while one is running.</summary>
        private readonly ConcurrentDictionary<string, ManualResetEventSlim> _populating = new();

        public void Start(string rootFolderId = "root", bool assignLetter = true, char preferred = 'G',
            CancellationToken token = default)
        {
            Directory.CreateDirectory(Root);
            _folders[""] = rootFolderId;

            Register();
            Connect();

            // Cleared only now, with the provider connected and serving.
            //
            // Deleting a placeholder needs somebody to answer for it, and after
            // the last run exited there is nobody: the delete fails with "the
            // cloud file provider exited unexpectedly", which is caught, ignored,
            // and leaves the tree half there. The next run then finds a plain
            // directory where a placeholder should be, cannot replace it, and
            // mounts a drive that is quietly missing folders.
            ClearContents();

            PopulateRoot(rootFolderId, token);

            if (assignLetter)
            {
                _letter = DriveLetter.Assign(Root, preferred, Notify);
                _log($"drive letter {_letter.Letter} -> {Root}");
            }
        }

        /// <summary>
        /// Empties a sync root left behind by a run that was killed, and says
        /// whether it can be used again.
        ///
        /// A stale placeholder cannot be deleted by anybody while no provider
        /// answers for it — not by rmdir, not by stripping the reparse point;
        /// every route says access denied. But it *can* be deleted by a provider
        /// that is connected, which is what Start already relies on and what
        /// Dispose does on the way out. Nothing was ever pointing that machinery
        /// at the directory that actually needed it: PickRoot only asked whether
        /// a candidate was already empty, and the code that empties one runs on
        /// the root PickRoot has already accepted — which by definition was
        /// empty. So the one directory that needed clearing was the one thing
        /// that could never be cleared, and every launch from then on stepped
        /// past it to GoogleDrive-2 for ever.
        ///
        /// Registering and connecting here is cheap: no listing, no population,
        /// no drive letter. Dispose clears while still connected and then takes
        /// the registration away again. Deleting a placeholder is local — there
        /// is no provider to relay it — so nothing can reach the Drive account.
        /// </summary>
        public static bool TryReclaim(DriveClient drive, string root, long budget, Action<string> log)
        {
            if (!Directory.Exists(root)) return true;

            try { if (!Directory.EnumerateFileSystemEntries(root).Any()) return true; }
            catch { return false; }

            var mount = new DriveMount(drive, root, budget, log);
            try
            {
                mount.Register();
                mount.Connect();
            }
            catch (Exception ex)
            {
                log($"reclaim: could not connect to {Path.GetFileName(root)}: {ex.Message}");
            }
            finally
            {
                // Clears while connected, then disconnects and unregisters.
                try { mount.Dispose(); } catch { }
            }

            try { return !Directory.EnumerateFileSystemEntries(root).Any(); }
            catch { return false; }
        }

        /// <summary>
        /// Empties the sync root, with the provider live so the deletes can be
        /// serviced. Failures are per-entry and survivable: one file that will
        /// not go is a stale row, not a reason to abandon the mount.
        /// </summary>
        private void ClearContents()
        {
            int removed = 0, failed = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(Root))
                {
                    try
                    {
                        if (Directory.Exists(entry)) Directory.Delete(entry, true);
                        else File.Delete(entry);
                        removed++;
                    }
                    catch { failed++; }
                }
            }
            catch { }

            if (removed > 0 || failed > 0)
                _log($"cleared {removed} stale entries ({failed} would not go) " +
                     $"in {clock.ElapsedMilliseconds}ms");
        }

        private void Register()
        {
            var name = Marshal.StringToHGlobalUni("Explorer Native Drive");
            var version = Marshal.StringToHGlobalUni("1.0");
            const string identity = "google-drive";
            var identityPtr = Marshal.StringToHGlobalUni(identity);

            var registration = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)Marshal.SizeOf<CF_SYNC_REGISTRATION>(),
                ProviderName = name,
                ProviderVersion = version,
                SyncRootIdentity = identityPtr,
                SyncRootIdentityLength = (uint)((identity.Length + 1) * 2),
                ProviderId = new Guid("6a4d8f2e-11c3-4f6b-9a70-5d2e8c1b40ab"),
            };

            var policies = new CF_SYNC_POLICIES
            {
                StructSize = (uint)Marshal.SizeOf<CF_SYNC_POLICIES>(),
                Hydration = new CF_HYDRATION_POLICY
                {
                    Primary = CF_HYDRATION_POLICY_PRIMARY.PARTIAL,
                    Modifier = CF_HYDRATION_POLICY_MODIFIER.STREAMING_ALLOWED,
                },
                // PARTIAL, and it must stay that way. This comment used to say
                // "FULL, not PARTIAL" directly above the line that sets PARTIAL —
                // a note left over from trying FULL, kept after it was reverted,
                // and pointing the next reader straight back at the thing that
                // does not work: under FULL the sync root refuses
                // CfCreatePlaceholders outright, whatever the register flags, so
                // the drive mounts completely empty.
                //
                // What PARTIAL costs is that FETCH_PLACEHOLDERS is not raised for
                // anything below the root either, which is why Populate drives it
                // from the application instead. Both halves are measured; see
                // Populate and CLAUDE.md.
                Population = new CF_POPULATION_POLICY { Primary = CF_POPULATION_POLICY_PRIMARY.PARTIAL },
            };

            // Populating is driven by us, not by Windows — see Populate. The two
            // root flags are what stop Windows asking about a directory it can
            // never record as answered: without them the root is re-asked every
            // 220ms for as long as the mount is up.
            int hr = CfRegisterSyncRoot(Root, ref registration, ref policies,
                CF_REGISTER_FLAGS.DISABLE_ON_DEMAND_POPULATION_ON_ROOT |
                CF_REGISTER_FLAGS.MARK_IN_SYNC_ON_ROOT);

            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(version);
            Marshal.FreeHGlobal(identityPtr);

            if (hr != 0) throw new InvalidOperationException($"CfRegisterSyncRoot 0x{hr:X8}");
            _registered = true;
        }

        private void Connect()
        {
            _fetchData = OnFetchData;
            _fetchPlaceholders = OnFetchPlaceholders;
            _cancelFetch = OnCancelFetch;

            var table = new[]
            {
                new CF_CALLBACK_REGISTRATION
                {
                    Type = CF_CALLBACK_TYPE.FETCH_DATA,
                    Callback = Marshal.GetFunctionPointerForDelegate(_fetchData),
                },
                new CF_CALLBACK_REGISTRATION
                {
                    Type = CF_CALLBACK_TYPE.FETCH_PLACEHOLDERS,
                    Callback = Marshal.GetFunctionPointerForDelegate(_fetchPlaceholders),
                },
                new CF_CALLBACK_REGISTRATION
                {
                    Type = CF_CALLBACK_TYPE.CANCEL_FETCH_DATA,
                    Callback = Marshal.GetFunctionPointerForDelegate(_cancelFetch),
                },
                new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.NONE, Callback = IntPtr.Zero },
            };

            int hr = CfConnectSyncRoot(Root, table, IntPtr.Zero,
                CF_CONNECT_FLAGS.REQUIRE_FULL_FILE_PATH | CF_CONNECT_FLAGS.REQUIRE_PROCESS_INFO, out _connection);
            if (hr != 0) throw new InvalidOperationException($"CfConnectSyncRoot 0x{hr:X8}");
        }

        /// <summary>
        /// The top level, placed up front.
        ///
        /// The sync root directory cannot be turned into a placeholder — the
        /// convert returns "the cloud operation is invalid" — so it never becomes
        /// a directory Windows thinks anything is missing from, and nothing is
        /// ever asked for. Registering alone gives a drive that mounts, opens,
        /// and is simply empty, with no error anywhere to say why.
        ///
        /// So the root costs one Drive listing at mount time. Everything under it
        /// is a placeholder directory created without
        /// DISABLE_ON_DEMAND_POPULATION, which does raise the callback, so the
        /// tree below stays lazy — one listing per folder somebody actually opens.
        /// </summary>
        private void PopulateRoot(string folderId, CancellationToken token)
        {
            // Claimed before it is filled, so a caller that navigates to the root
            // does not place the same nineteen entries a second time and collect
            // ERROR_ALREADY_EXISTS for its trouble.
            _populated[""] = true;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Called off with the mount, and not waited on for ever: an outage
            // here otherwise held the mount — and the registration it has
            // already made — for as long as the outage lasted.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var entries = ListFolder("", folderId, deadline.Token);
            long listMs = clock.ElapsedMilliseconds;

            var pins = new List<IntPtr>();
            try
            {
                var array = BuildPlaceholders("", entries, pins);
                if (array.Length == 0) { _log($"root is empty (listing {listMs}ms)"); return; }

                clock.Restart();
                int hr = CfCreatePlaceholders(Root, array, (uint)array.Length,
                    CF_PLACEHOLDER_CREATE_FLAGS.NONE, out uint processed);
                _log($"root: listing {listMs}ms, placing {clock.ElapsedMilliseconds}ms");

                // Already there is fine — see ErrorAlreadyExists. Anything else
                // is worth failing the mount for, because a root with nothing in
                // it is a drive that opens empty.
                if (hr != 0 && !IsAlreadyThere(hr))
                    throw new InvalidOperationException($"CfCreatePlaceholders 0x{hr:X8}");
                if (hr != 0) RefreshLater(Root, ExistingToCheck(array, null));
                _log($"root: placed {processed} of {array.Length} entries");
            }
            finally
            {
                foreach (var p in pins) Marshal.FreeHGlobal(p);
            }
        }

        // ---- populating a directory ---------------------------------------

        private void OnFetchPlaceholders(
            ref CF_CALLBACK_INFO info,
            ref CF_CALLBACK_PARAMETERS_FETCH_PLACEHOLDERS parameters)
        {
            var opInfo = Operation(ref info, CF_OPERATION_TYPE.TRANSFER_PLACEHOLDERS);
            string relative = "";

            Interlocked.Increment(ref _callbacksInFlight);
            try
            {
                var raw = Marshal.PtrToStringUni(info.NormalizedPath) ?? "";
                relative = Relative(raw);
                _log($"  FETCH_PLACEHOLDERS raw='{raw}' -> relative='{relative}'");

                if (!_folders.TryGetValue(relative, out var folderId))
                {
                    _log($"  no Drive folder known for '{relative}'; known: " +
                         string.Join(", ", _folders.Keys.Take(6)));
                    Say("drive.folder.failed",
                        $"Could not read {Path.GetFileName(relative)}: it is not part of the mounted Drive");
                    FailPlaceholders(ref opInfo);
                    return;
                }

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
                deadline.CancelAfter(PlaceholderDeadline);
                var entries = ListFolder(relative, folderId, deadline.Token);
                _log($"  populate '{(relative.Length == 0 ? "\\" : relative)}' -> {entries.Count} entries");

                TransferPlaceholders(ref opInfo, relative, entries);
            }
            catch (Exception ex)
            {
                _log($"  populate '{relative}' threw: {ex.Message}");

                // Whoever asked for this folder is a program somewhere else —
                // File Explorer, a command prompt — and all it will see is an
                // empty directory. This is the only place that knows better.
                Say("drive.folder.failed", $"Could not read {Path.GetFileName(relative)}: {ex.Message}");
                try { FailPlaceholders(ref opInfo); } catch { }
            }
            finally
            {
                // Here, not in the helper below, and never on the success path
                // alone. Two ways out of this callback skip the transfer entirely
                // — a folder that is not part of the mount, and anything thrown,
                // which includes the listing deadline — so a decrement anywhere
                // but a finally on *this* method leaks the count. A leaked count
                // makes the shutdown guard useless in exactly the direction that
                // matters: it can no longer tell a real read in flight from a
                // number left behind, so it waits out its five seconds and
                // disconnects anyway.
                Interlocked.Decrement(ref _callbacksInFlight);
            }
        }

        private void TransferPlaceholders(
            ref CF_OPERATION_INFO opInfo, string relative, List<DriveEntry> entries)
        {
            var pins = new List<IntPtr>();
            int one = Marshal.SizeOf<CF_PLACEHOLDER_CREATE_INFO>();
            IntPtr block = IntPtr.Zero;

            try
            {
                // The same naming as Populate's: every name the last listing
                // bound to a file stays reserved, since its placeholder is still
                // on disk under it. Keeping only the names of files still present
                // handed a trashed file's name to a new one, whose reads were then
                // served by the trashed file's placeholder.
                var prior = PriorBindings(relative);
                var session = new PlacementSession(this, relative);
                var array = BuildPlaceholders(relative, entries, pins, session: session);
                if (array.Length > 0)
                {
                    block = Marshal.AllocHGlobal(one * array.Length);
                    for (int i = 0; i < array.Length; i++)
                        Marshal.StructureToPtr(array[i], block + i * one, false);
                }

                var opParams = new CF_OPERATION_PARAMETERS_TRANSFER_PLACEHOLDERS
                {
                    ParamSize = (uint)Marshal.SizeOf<CF_OPERATION_PARAMETERS_TRANSFER_PLACEHOLDERS>(),
                    // The whole listing is being handed over, which is what this
                    // flag is for: it marks the directory fully populated, so the
                    // platform does not come back to ask again — each asking being
                    // a Drive listing of a second and more. Refresh still works:
                    // it goes through Forget and Populate, not through this.
                    Flags = (uint)CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.DISABLE_ON_DEMAND_POPULATION,
                    CompletionStatus = 0,
                    PlaceholderTotalCount = array.Length,
                    PlaceholderArray = block,
                    PlaceholderCount = (uint)array.Length,
                    EntriesProcessed = 0,
                };

                int hr = CfExecute(ref opInfo, ref opParams);
                if (hr != 0)
                    _log($"  TRANSFER_PLACEHOLDERS 0x{hr:X8} for '{relative}' ({array.Length} entries)");
                else
                {
                    // Listed and placed, so the window's own navigation can use
                    // the listing rather than asking Drive a second time.
                    _listings[relative] = session.Placed.ToArray();
                    _listedAt[relative] = Environment.TickCount64;
                    ForgetGone(relative, session);
                    _populated[relative] = true;

                    // Anything that was already there may describe other
                    // contents. Checked off this thread: the callback is holding
                    // up whoever listed the folder.
                    var results = new CF_PLACEHOLDER_CREATE_INFO[array.Length];
                    for (int i = 0; i < array.Length; i++)
                        results[i] = Marshal.PtrToStructure<CF_PLACEHOLDER_CREATE_INFO>(block + i * one);

                    RefreshLater(relative.Length == 0 ? Root : Path.Combine(Root, relative),
                        ExistingToCheck(results, prior));
                }
            }
            finally
            {
                if (block != IntPtr.Zero) Marshal.FreeHGlobal(block);
                foreach (var p in pins) Marshal.FreeHGlobal(p);
            }
        }

        /// <summary>
        /// Drive entries as placeholder descriptions. Shared by the eager path
        /// for the root and the on-demand path for everything under it, so the
        /// two cannot drift on naming, sizes or which entries are skipped.
        /// </summary>
        /// <summary>
        /// One listing of a folder being placed a page at a time: the names
        /// already given out, the names kept from the listing before, and
        /// everything placed so far.
        /// </summary>
        private sealed class PlacementSession
        {
            public readonly HashSet<string> Used = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Kept = new(StringComparer.Ordinal);
            public readonly List<DrivePlaced> Placed = new();

            /// <summary>Every id this listing returned, Google documents included.</summary>
            public readonly HashSet<string> Seen = new(StringComparer.Ordinal);

            public PlacementSession(DriveMount mount, string relative)
            {
                // Every name the last listing bound to a file is kept for that
                // file, whether or not it turns up again — its placeholder is
                // still on disk under that name — so a page cannot hand the name
                // to something else before the file it belongs to arrives.
                if (!mount._listings.TryGetValue(relative, out var before)) return;
                foreach (var item in before)
                {
                    var key = (relative.Length == 0 ? item.Name : relative + "\\" + item.Name).ToLowerInvariant();
                    if (mount._ids.TryGetValue(key, out var boundId) &&
                        !Kept.ContainsKey(boundId) && Used.Add(item.Name))
                        Kept[boundId] = item.Name;
                }
            }
        }

        private CF_PLACEHOLDER_CREATE_INFO[] BuildPlaceholders(
            string relative, List<DriveEntry> entries, List<IntPtr> pins,
            HashSet<string>? claimed = null, List<string>? placedNames = null,
            PlacementSession? session = null, List<DrivePlaced>? placedPage = null)
        {
            var array = new List<CF_PLACEHOLDER_CREATE_INFO>();
            var used = session?.Used ?? claimed ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // A whole folder listed again — a refresh — keeps every name that is
            // still bound to the same Drive file. Names were handed out afresh in
            // whatever order Drive listed them, so two files sharing a name could
            // swap: the map then said "song.flac" was the file whose placeholder
            // is "song (2).flac", and deleting one trashed the other.
            Dictionary<string, string>? keptNames = session?.Kept;
            if (session == null && claimed == null && _listings.TryGetValue(relative, out var before))
            {
                var present = new HashSet<string>(entries.Select(e => e.Id), StringComparer.Ordinal);
                keptNames = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var item in before)
                {
                    var key = (relative.Length == 0 ? item.Name : relative + "\\" + item.Name).ToLowerInvariant();
                    if (_ids.TryGetValue(key, out var boundId) && present.Contains(boundId) &&
                        !keptNames.ContainsKey(boundId) && used.Add(item.Name))
                        keptNames[boundId] = item.Name;
                }
            }

            // Kept as they are built, so a later listing of this folder is a
            // dictionary lookup rather than 33 seconds of walking placeholders.
            // See _listings.
            var placed = new List<DrivePlaced>(entries.Count);

            foreach (var entry in entries)
            {
                session?.Seen.Add(entry.Id);

                // A Google-native document has no bytes anywhere — not here and
                // not in Drive for desktop. Skipped rather than shown as a file
                // that cannot be opened.
                //
                // Skipped *before* a name is claimed for it, which is the order
                // that matters. Claiming first put the name of something that was
                // then never placed into the set of names already taken, so a real
                // file sharing it — which Drive allows, and which is the only
                // reason this set exists — was renamed to "name (2)" to avoid a
                // collision with a placeholder that does not exist.
                if (entry.IsGoogleDocument) continue;

                // Drive lets two files in one folder share a name and NTFS does
                // not, so a duplicate has to be renamed or it silently replaces
                // the first and the folder comes up short.
                string name = keptNames != null && keptNames.TryGetValue(entry.Id, out var kept)
                    ? kept
                    : Unique(Sanitise(entry.Name), used, entry.IsFolder);

                long size = entry.IsFolder ? 0 : Math.Max(0, entry.Size);
                long stamp = entry.Modified == default
                    ? DateTime.UtcNow.ToFileTimeUtc()
                    : entry.Modified.ToFileTimeUtc();

                string child = (relative.Length == 0 ? name : relative + "\\" + name).ToLowerInvariant();
                _ids[child] = entry.Id;
                if (entry.IsFolder) _folders[child] = entry.Id;
                if (entry.IsFolder && !string.IsNullOrEmpty(entry.DriveId)) _driveIds[child] = entry.DriveId;

                placed.Add(new DrivePlaced(name, entry.IsFolder, entry.IsFolder ? -1 : size,
                    entry.Modified == default ? DateTime.Now : entry.Modified.ToLocalTime(), entry.Name));
                placedNames?.Add(name);

                var namePtr = Marshal.StringToHGlobalUni(name);
                var idPtr = Marshal.StringToHGlobalUni(entry.Id);
                pins.Add(namePtr);
                pins.Add(idPtr);

                array.Add(new CF_PLACEHOLDER_CREATE_INFO
                {
                    RelativeFileName = namePtr,
                    FsMetadata = new CF_FS_METADATA
                    {
                        FileSize = size,
                        BasicInfo = new FILE_BASIC_INFO
                        {
                            CreationTime = stamp,
                            LastAccessTime = stamp,
                            LastWriteTime = stamp,
                            ChangeTime = stamp,
                            FileAttributes = entry.IsFolder ? FileAttributeDirectory : FileAttributeNormal,
                        },
                    },
                    FileIdentity = idPtr,
                    FileIdentityLength = (uint)((entry.Id.Length + 1) * 2),

                    // MARK_IN_SYNC on a directory says it is fully in sync, which
                    // Windows reads as fully populated — the same trap as
                    // MARK_IN_SYNC_ON_ROOT, one level down. Files want it; folders
                    // must not have it or nothing below them is ever asked for.
                    Flags = entry.IsFolder
                        ? CF_PLACEHOLDER_CREATE_FLAGS.NONE
                        : CF_PLACEHOLDER_CREATE_FLAGS.MARK_IN_SYNC,
                });
            }

            // Merged rather than replaced. PlaceOne builds a single entry for
            // something that has just been uploaded, and overwriting the folder's
            // whole listing with that one entry would make the other two thousand
            // files disappear from the pane until the application was restarted.
            //
            // A whole listing replaces what was there — it is the answer, and a
            // file deleted elsewhere should leave the pane when it is refreshed.
            if (session != null)
            {
                session.Placed.AddRange(placed);
                placedPage?.AddRange(placed);
                return array.ToArray();
            }

            if (claimed == null)
            {
                _listings[relative] = placed.ToArray();
                return array.ToArray();
            }

            _listings.AddOrUpdate(relative, placed.ToArray(), (_, existing) =>
            {
                var merged = new List<DrivePlaced>(existing.Length + placed.Count);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in placed) if (names.Add(item.Name)) merged.Add(item);
                foreach (var item in existing) if (names.Add(item.Name)) merged.Add(item);

                return merged.ToArray();
            });

            return array.ToArray();
        }

        /// <summary>
        /// What a Drive directory contains, from the listing already fetched for
        /// it — no filesystem, no network.
        ///
        /// False for a directory nobody has populated yet, which is the caller's
        /// signal to go the ordinary way.
        /// </summary>
        public bool TryListing(string fullPath, out IReadOnlyList<DrivePlaced> entries, TimeSpan? newerThan = null)
        {
            entries = Array.Empty<DrivePlaced>();

            var key = KeyFor(fullPath);
            if (key == null) return false;
            if (!_populated.ContainsKey(key)) return false;
            if (!_listings.TryGetValue(key, out var placed)) return false;
            if (newerThan is { } most &&
                (!_listedAt.TryGetValue(key, out var at) || Environment.TickCount64 - at > most.TotalMilliseconds))
                return false;

            entries = placed;
            return true;
        }

        /// <summary>
        /// Forgets a directory's listing so the next visit fetches it again.
        /// What "refresh" means on a drive whose contents live somewhere else.
        /// </summary>
        public void Forget(string fullPath)
        {
            var key = KeyFor(fullPath);
            if (key == null) return;

            // Only marked unlisted. The old listing stays, unserved — TryListing
            // answers for populated folders alone — because the next listing
            // needs it to keep each name bound to the file it named.
            _populated.TryRemove(key, out _);
        }

        /// <summary>
        /// After a whole listing: what the map still says about files the
        /// listing no longer holds. Their ids kept answering for their paths, so
        /// a folder trashed on the web and then uploaded again by name was
        /// "merged into" — the upload went into the trashed folder.
        /// </summary>
        private void ForgetGone(string relative, PlacementSession session)
        {
            foreach (var (id, name) in session.Kept)
            {
                if (session.Seen.Contains(id)) continue;

                var key = (relative.Length == 0 ? name : relative + "\\" + name).ToLowerInvariant();
                _ids.TryRemove(new KeyValuePair<string, string>(key, id));
                if (_folders.TryRemove(new KeyValuePair<string, string>(key, id)))
                {
                    _driveIds.TryRemove(key, out _);
                    _listings.TryRemove(key, out _);
                    _populated.TryRemove(key, out _);

                    var below = key + "\\";
                    foreach (var k in _ids.Keys) if (k.StartsWith(below, StringComparison.Ordinal)) _ids.TryRemove(k, out _);
                    foreach (var k in _folders.Keys) if (k.StartsWith(below, StringComparison.Ordinal)) _folders.TryRemove(k, out _);
                    foreach (var k in _driveIds.Keys) if (k.StartsWith(below, StringComparison.Ordinal)) _driveIds.TryRemove(k, out _);
                    foreach (var k in _listings.Keys) if (k.StartsWith(below, StringComparison.Ordinal)) _listings.TryRemove(k, out _);
                    foreach (var k in _populated.Keys) if (k.StartsWith(below, StringComparison.Ordinal)) _populated.TryRemove(k, out _);
                }
            }
        }

        private readonly record struct Existing(string Name, string Id, long Size, long Stamp);

        private static long StampOf(DriveEntry entry) =>
            (entry.Modified == default ? DateTime.UtcNow : entry.Modified).ToFileTimeUtc();

        /// <summary>
        /// What this mount last said each file in a folder was — its id, size and
        /// date — by name, before a listing rewrites it.
        /// </summary>
        private Dictionary<string, (string Id, long Size, long Stamp)> PriorBindings(string relative)
        {
            var prior = new Dictionary<string, (string, long, long)>(StringComparer.OrdinalIgnoreCase);
            if (!_listings.TryGetValue(relative, out var placed)) return prior;
            string folder = relative.Length == 0 ? Root : Path.Combine(Root, relative);

            foreach (var item in placed)
            {
                if (item.IsFolder) continue;

                // One whose update failed is looked at again, whatever the
                // listing now says it is.
                if (_recheck.ContainsKey(Path.Combine(folder, item.Name).ToLowerInvariant())) continue;
                var key = (relative.Length == 0 ? item.Name : relative + "\\" + item.Name).ToLowerInvariant();
                if (_ids.TryGetValue(key, out var id))
                    prior[item.Name] = (id, item.Size, item.Modified.ToUniversalTime().ToFileTimeUtc());
            }
            return prior;
        }

        /// <summary>
        /// The files in a batch that were already on disk and may now describe
        /// something else, copied out of the batch while its strings are pinned.
        ///
        /// Only those whose id, size or date differ from what this mount last
        /// said: checking every one opened every placeholder in the folder on
        /// every refresh — two thousand for the big folder, before the first
        /// page showed.
        /// </summary>
        private static List<Existing> ExistingToCheck(CF_PLACEHOLDER_CREATE_INFO[] array,
            Dictionary<string, (string Id, long Size, long Stamp)>? prior)
        {
            var list = new List<Existing>();
            foreach (var item in array)
            {
                if (!IsAlreadyThere(item.Result)) continue;
                if ((item.FsMetadata.BasicInfo.FileAttributes & FileAttributeDirectory) != 0) continue;

                var name = Marshal.PtrToStringUni(item.RelativeFileName) ?? "";
                var id = Marshal.PtrToStringUni(item.FileIdentity) ?? "";
                long size = item.FsMetadata.FileSize;
                long stamp = item.FsMetadata.BasicInfo.LastWriteTime;

                if (prior != null && prior.TryGetValue(name, out var was) &&
                    was.Id == id && was.Size == size && Math.Abs(was.Stamp - stamp) < TimeSpan.TicksPerSecond)
                    continue;

                list.Add(new Existing(name, id, size, stamp));
            }
            return list;
        }

        /// <summary>
        /// Brought up to date off the calling thread, which is a listing somebody
        /// is waiting on.
        /// </summary>
        private void RefreshLater(string folder, List<Existing> list)
        {
            if (list.Count == 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (var item in list)
                {
                    if (_disposed) return;
                    RefreshOne(folder, item.Name, item.Id, item.Size, item.Stamp);
                }
            });
        }

        /// <summary>Placeholders whose update failed, by full path, to be tried again.</summary>
        private readonly ConcurrentDictionary<string, byte> _recheck = new();

        private void RefreshNow(string folder, List<Existing> list)
        {
            foreach (var item in list) RefreshOne(folder, item.Name, item.Id, item.Size, item.Stamp);
        }

        /// <summary>
        /// A placeholder that was already there when its folder was listed
        /// again, brought up to date when it describes something else.
        ///
        /// "Already exists" was taken as placed, full stop — so a file given new
        /// contents on the web kept its old size here, and a copy out of the
        /// letter delivered the new bytes cut off at the old length. And a name
        /// whose placeholder belonged to a file since trashed went on serving
        /// that file under the new one's name. Both are one question: does the
        /// placeholder on disk carry this id, size and date? If not, it is
        /// replaced.
        ///
        /// True when the placeholder describes the entry afterwards, whether or
        /// not anything had to change.
        /// </summary>
        private bool RefreshOne(string folder, string name, string id, long size, long stamp)
        {
            if (name.Length == 0 || id.Length == 0) return false;
            string path = Path.Combine(folder, name);

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;

                string? onDisk = PlaceholderIdentity(path);
                if (onDisk == null) return false;   // not a placeholder: somebody's own file

                // The date as well: a file revised on the web to the same length
                // keeps its id and its size, and the download already held for it
                // is of the old revision.
                bool contentsChanged = onDisk != id || info.Length != size ||
                                       Math.Abs(info.LastWriteTimeUtc.ToFileTimeUtc() - stamp) >= TimeSpan.TicksPerSecond;
                if (!contentsChanged)
                {
                    _recheck.TryRemove(path.ToLowerInvariant(), out _);
                    return true;
                }

                var metadata = new CF_FS_METADATA
                {
                    FileSize = size,
                    BasicInfo = new FILE_BASIC_INFO
                    {
                        CreationTime = stamp,
                        LastAccessTime = stamp,
                        LastWriteTime = stamp,
                        ChangeTime = stamp,
                        FileAttributes = FileAttributeNormal,
                    },
                };

                var idPtr = Marshal.StringToHGlobalUni(id);
                try
                {
                    using var handle = CreateFileW(path, FileReadAttributes,
                        FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
                        FileFlagOpenReparsePoint, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        _log($"  could not open {name} to update it: {Marshal.GetLastWin32Error()}");
                        return false;
                    }

                    int hr = CfUpdatePlaceholder(handle, ref metadata, idPtr, (uint)((id.Length + 1) * 2),
                        IntPtr.Zero, 0, CF_UPDATE_FLAGS.MARK_IN_SYNC | CF_UPDATE_FLAGS.DEHYDRATE,
                        IntPtr.Zero, IntPtr.Zero);

                    // Dehydrating wants nobody else to have the file open; the
                    // new description matters more than the empty space.
                    if (hr != 0)
                        hr = CfUpdatePlaceholder(handle, ref metadata, idPtr, (uint)((id.Length + 1) * 2),
                            IntPtr.Zero, 0, CF_UPDATE_FLAGS.MARK_IN_SYNC, IntPtr.Zero, IntPtr.Zero);

                    if (hr != 0)
                    {
                        _log($"  could not update {name}: 0x{hr:X8}");
                        _recheck[path.ToLowerInvariant()] = 0;
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(idPtr);
                }

                // Contents that are not the ones downloaded: the downloads go.
                if (onDisk != id) ReleaseFile(onDisk);
                ReleaseFile(id);
                _recheck.TryRemove(path.ToLowerInvariant(), out _);
                _log($"  updated {name}: now {id}, {size:N0} bytes");
                return true;
            }
            catch (Exception ex)
            {
                _log($"  could not check {name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// The Drive id a placeholder on disk was made for, read without
        /// touching its contents; null when it cannot be read.
        /// </summary>
        internal static string? PlaceholderIdentity(string path)
        {
            using var handle = CreateFileW(path, FileReadAttributes,
                FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) return null;

            var buffer = new byte[4096];
            int hr = CfGetPlaceholderInfo(handle, PlaceholderInfoBasic, buffer, (uint)buffer.Length, out uint got);
            if (hr != 0 || got < BasicInfoIdentityLengthOffset + 4) return null;

            int length = BitConverter.ToInt32(buffer, BasicInfoIdentityLengthOffset);
            int start = BasicInfoIdentityLengthOffset + 4;
            if (length <= 0 || start + length > got) return null;

            return System.Text.Encoding.Unicode.GetString(buffer, start, length).TrimEnd('\0');
        }

        /// <summary>
        /// For the suite: a placeholder made for one file, then listed again as
        /// a different file of different size under the same name, must come
        /// out describing the second. Stands a provider up over
        /// <paramref name="root"/>, which must be empty, and takes it down.
        /// Null when it holds; otherwise what went wrong.
        /// </summary>
        internal static string? ReplacedContentsCheck(DriveClient drive, string root, Action<string> log)
        {
            Directory.CreateDirectory(root);
            var mount = new DriveMount(drive, root, 16L * 1024 * 1024, log);
            try
            {
                mount.Register();
                mount.Connect();

                var when = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                var first = new DriveEntry("first-id", "song.flac", "audio/flac", 10, when);
                var second = new DriveEntry("second-id", "song.flac", "audio/flac", 20, when.AddDays(1));

                foreach (var entry in new[] { first, second })
                {
                    var pins = new List<IntPtr>();
                    try
                    {
                        var session = new PlacementSession(mount, "");
                        var array = mount.BuildPlaceholders("", new List<DriveEntry> { entry }, pins, session: session);
                        int hr = CfCreatePlaceholders(root, array, (uint)array.Length,
                            CF_PLACEHOLDER_CREATE_FLAGS.NONE, out _);
                        if (hr != 0 && hr != ErrorAlreadyExists) return $"placing failed 0x{hr:X8}";
                        if (hr != 0) mount.RefreshNow(root, ExistingToCheck(array, null));
                    }
                    finally { foreach (var p in pins) Marshal.FreeHGlobal(p); }

                    // A different id in the map for the same name is what a
                    // listing after a delete and a new upload looks like.
                    mount._listings.TryRemove("", out _);
                }

                var path = Path.Combine(root, "song.flac");
                var info = new FileInfo(path);
                var identity = PlaceholderIdentity(path);
                if (info.Length != 20 || identity != "second-id")
                    return $"the placeholder says {info.Length} bytes and id {identity ?? "(unreadable)"}";
                return null;
            }
            finally
            {
                try { mount.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// For a probe with a real sign-in: a process reading
        /// <paramref name="entry"/> through a provider over
        /// <paramref name="root"/> is stood down and ended mid-read, and must
        /// then be gone rather than stuck exiting. Null when it is.
        /// </summary>
        internal static string? StandDownCheck(DriveClient drive, string root, DriveEntry entry,
            Func<string, System.Diagnostics.Process> startReader, Action<string> log)
        {
            Directory.CreateDirectory(root);
            var mount = new DriveMount(drive, root, 256L * 1024 * 1024, log);
            try
            {
                mount.Register();
                mount.Connect();

                var pins = new List<IntPtr>();
                try
                {
                    var session = new PlacementSession(mount, "");
                    var array = mount.BuildPlaceholders("", new List<DriveEntry> { entry }, pins, session: session);
                    int hr = CfCreatePlaceholders(root, array, (uint)array.Length, CF_PLACEHOLDER_CREATE_FLAGS.NONE, out _);
                    if (hr != 0) return $"placing failed 0x{hr:X8}";
                }
                finally { foreach (var p in pins) Marshal.FreeHGlobal(p); }

                var target = Path.Combine(root, DriveMount.Sanitise(entry.Name));
                var placed = new FileInfo(target);
                log($"placed {target}: exists={placed.Exists} length={(placed.Exists ? placed.Length : -1)} " +
                    $"attributes={(placed.Exists ? placed.Attributes.ToString() : "")}");
                try
                {
                    var self = System.Threading.Tasks.Task.Run(() =>
                    {
                        using var fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
                        var b = new byte[65536];
                        return fs.Read(b, 0, b.Length);
                    });
                    log(self.Wait(30000) ? $"a read from this process got {self.Result} bytes" : "a read from this process took over 30s");
                }
                catch (Exception ex) { log("a read from this process failed: " + ex.GetBaseException().Message); }
                var reader = startReader(target);
                int id = reader.Id;
                Thread.Sleep(2500);

                int busy;
                string all;
                lock (mount._readersGate)
                {
                    busy = mount._readers.TryGetValue(id, out var r) ? r.InFlight : -1;
                    all = string.Join(", ", mount._readers.Select(pair => $"{pair.Key}:{pair.Value.InFlight}"));
                }
                log($"reader {id}: {busy} read(s) in flight before standing down; all readers [{all}]; " +
                    $"callbacks in flight {Volatile.Read(ref mount._callbacksInFlight)}; exited={reader.HasExited}");

                var clock = System.Diagnostics.Stopwatch.StartNew();
                mount.StandDownReads(id, TimeSpan.FromSeconds(10));
                log($"stood down in {clock.ElapsedMilliseconds}ms");

                try { reader.Kill(); } catch (Exception ex) { log("kill: " + ex.Message); }
                reader.WaitForExit(15000);
                Thread.Sleep(3000);
                mount.ForgetReader(id);

                try
                {
                    using var still = System.Diagnostics.Process.GetProcessById(id);
                    return $"reader {id} is still there with {still.Threads.Count} thread(s): stuck exiting";
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }
            finally
            {
                try { mount.Dispose(); } catch { }
            }
        }

        private void FailPlaceholders(ref CF_OPERATION_INFO opInfo)
        {
            var opParams = new CF_OPERATION_PARAMETERS_TRANSFER_PLACEHOLDERS
            {
                ParamSize = (uint)Marshal.SizeOf<CF_OPERATION_PARAMETERS_TRANSFER_PLACEHOLDERS>(),
                CompletionStatus = unchecked((int)0xC0000001), // STATUS_UNSUCCESSFUL
                PlaceholderArray = IntPtr.Zero,
            };
            CfExecute(ref opInfo, ref opParams);
        }

        // ---- serving bytes --------------------------------------------------

        /// <summary>
        /// How often a fetch that is taking a while tells Windows it is still
        /// alive. Half a second is well inside any timeout worth worrying about
        /// and costs one call each time on the only path that is slow anyway.
        /// </summary>
        private const int KeepAliveMilliseconds = 500;

        /// <summary>The reads one process has in flight here, and whether it is still served.</summary>
        private sealed class Reader
        {
            public int InFlight;
            public bool Refused;
            public readonly CancellationTokenSource Stop = new();
        }

        private readonly Dictionary<int, Reader> _readers = new();

        /// <summary>
        /// Cancelled when the mount is going: every read still being answered
        /// fails at once rather than running out its deadline, so nothing is
        /// left pending when the provider disconnects.
        /// </summary>
        private readonly CancellationTokenSource _closing = new();
        private readonly object _readersGate = new();

        /// <summary>
        /// Fails every read <paramref name="processId"/> has here, now and from
        /// now on, and returns once none has been in flight for a moment — so
        /// the process can be ended without a read of a placeholder pending.
        ///
        /// A process ended in the middle of such a read never finishes exiting:
        /// one thread stays in a kernel wait, nothing can end it, and only a
        /// restart of Windows clears it. Measured with this provider alive and
        /// answering. Cancelling a copy used to kill robocopy exactly like that.
        /// </summary>
        public void StandDownReads(int processId, TimeSpan most)
        {
            if (processId == 0) return;
            Reader reader;
            lock (_readersGate)
            {
                if (!_readers.TryGetValue(processId, out reader!)) _readers[processId] = reader = new Reader();
                reader.Refused = true;
            }
            try { reader.Stop.Cancel(); } catch { }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            long quietSince = -1;
            while (clock.Elapsed < most)
            {
                int busy;
                lock (_readersGate) busy = reader.InFlight;
                if (busy > 0) quietSince = -1;
                else if (quietSince < 0) quietSince = clock.ElapsedMilliseconds;
                else if (clock.ElapsedMilliseconds - quietSince >= 300) return;
                Thread.Sleep(25);
            }
        }

        /// <summary>After the process has gone: its id may be given to another.</summary>
        public void ForgetReader(int processId)
        {
            lock (_readersGate)
                if (_readers.TryGetValue(processId, out var reader) && reader.InFlight == 0)
                    _readers.Remove(processId);
        }

        private void OnFetchData(
            ref CF_CALLBACK_INFO info,
            ref CF_CALLBACK_PARAMETERS_FETCH_DATA parameters)
        {
            long offset = parameters.RequiredFileOffset;
            long length = parameters.RequiredLength;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            DriveFileCache? leased = null;

            Interlocked.Increment(ref _callbacksInFlight);

            int processId = info.ProcessInfo == IntPtr.Zero
                ? 0 : Marshal.ReadInt32(info.ProcessInfo, ProcessInfoProcessIdOffset);
            Reader? reader = null;
            bool refused = false;
            if (processId != 0)
            {
                lock (_readersGate)
                {
                    if (!_readers.TryGetValue(processId, out reader)) _readers[processId] = reader = new Reader();
                    reader.InFlight++;
                    refused = reader.Refused;
                }
            }

            try
            {
                if (refused || _closing.IsCancellationRequested) { Fail(ref info, offset, length); return; }

                string fileId = info.FileIdentity == IntPtr.Zero
                    ? "" : Marshal.PtrToStringUni(info.FileIdentity) ?? "";
                if (fileId.Length == 0) { Fail(ref info, offset, length); return; }

                // A ref parameter cannot be captured by the lambda, and the size
                // is the one thing the cache needs that it cannot ask Drive for
                // cheaply.
                long size = info.FileSize;
                leased = CacheOpenedFor(fileId, size);
                var cache = leased;

                // The first callback for a file is the moment its bytes start
                // coming down. Said before the read rather than after it, because
                // the read is the second or so this exists to explain — and the
                // path is only marshalled out of the callback when there is
                // somebody to say it to.
                if (Notify != null && _fetchStarted.TryAdd(fileId, true))
                    Say("drive.download.start", $"Fetching {CallbackName(ref info)}");

                // Transfers must be aligned except where they run to the end of
                // the file. An unaligned chunk fails the whole read with an error
                // that says nothing about alignment.
                long start = offset / ChunkAlignment * ChunkAlignment;
                long end = offset + length;
                if (end % ChunkAlignment != 0)
                    end = Math.Min(cache.Size, (end / ChunkAlignment + 1) * ChunkAlignment);

                // Windows times the callback, and a read it decides has taken too
                // long fails with 0x8007018D — "the cloud operation was not
                // completed before the time-out period expired" — which is a file
                // that will not copy and no explanation anywhere. A chunk served
                // from memory returns in microseconds and this never fires; one
                // that has to be fetched takes seconds on a link measuring about
                // a megabyte a second, and saying so is the documented way to be
                // given them. The keys are copied out because a ref parameter
                // cannot be captured.
                long connection = info.ConnectionKey;
                long transfer = info.TransferKey;
                long wanted = end - start;

                byte[] bytes;
                bool fromMemory;

                // The keep-alive below is what stops Windows timing this out, so
                // it is also what would let it wait for ever. DataDeadline is the
                // ceiling it no longer had; see PlaceholderDeadline. And a
                // process being stood down ends it at once.
                var deadline = reader == null
                    ? CancellationTokenSource.CreateLinkedTokenSource(_closing.Token)
                    : CancellationTokenSource.CreateLinkedTokenSource(reader.Stop.Token, _closing.Token);
                deadline.CancelAfter(DataDeadline);
                using (deadline)
                using (new Timer(
                           _ => { try { CfReportProviderProgress(connection, transfer, wanted, 0); } catch { } },
                           null, KeepAliveMilliseconds, KeepAliveMilliseconds))
                {
                    (bytes, fromMemory) = cache
                        .Read(start, (int)wanted, deadline.Token)
                        .GetAwaiter().GetResult();
                }
                // Short is a failure too, and this is the difference between a
                // copy that retries and one that stops dead.
                //
                // `end` is already clamped to the file's size, so anything less
                // than the whole range is bytes the reader asked for and did not
                // get — a truncated response from Drive, or a chunk that came
                // back partly. Transferring them with a success status tells
                // Windows the request is satisfied while part of the required
                // range was never sent, so the reader waits for the remainder
                // until Windows times the whole read out with 0x8007018D: a copy
                // that fails for no visible reason, where an honest failure here
                // is retried by robocopy and reported by the player.
                if (bytes.LongLength < wanted)
                {
                    _log($"  short read [{start:N0} +{bytes.LongLength:N0}] of {wanted:N0} wanted");
                    Fail(ref info, offset, length);
                    return;
                }

                var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try
                {
                    var opInfo = Operation(ref info, CF_OPERATION_TYPE.TRANSFER_DATA);
                    var opParams = new CF_OPERATION_PARAMETERS_TRANSFER_DATA
                    {
                        ParamSize = (uint)Marshal.SizeOf<CF_OPERATION_PARAMETERS_TRANSFER_DATA>(),
                        Buffer = pin.AddrOfPinnedObject(),
                        Offset = start,
                        Length = bytes.LongLength,
                        CompletionStatus = 0,
                    };

                    int hr = CfExecute(ref opInfo, ref opParams);

                    // The reader let go while we were fetching. Windows reads
                    // ahead speculatively and cancels what it turns out not to
                    // need, so this is ordinary traffic and not a fault.
                    if (hr == ErrorCloudFileCancelled) Interlocked.Increment(ref CancelledTransfers);
                    else if (hr != 0) _log($"  CfExecute 0x{hr:X8} at [{start:N0} +{bytes.LongLength:N0}]");

                    // Only a transfer that was actually handed to the reader counts
                    // as progress, and the path is marshalled out of the callback
                    // only when somebody is watching.
                    if (hr == 0 && Volatile.Read(ref _watchCount) > 0)
                        NoteFetch(Marshal.PtrToStringUni(info.NormalizedPath), start, start + bytes.LongLength, cache.Size);
                }
                finally
                {
                    pin.Free();
                }

                // The transfer that reaches the last byte is the whole file
                // served. Nothing else here can say that: Windows asks for a
                // quarter megabyte at a time and never says it has finished, so
                // the end of the file is the only end there is.
                if (Notify != null && cache.Size > 0 && end >= cache.Size &&
                    _fetchFinished.TryAdd(fileId, true))
                    Say("drive.download.done", $"{CallbackName(ref info)} ready");

                // Bounded, because nothing drains it. It is a measurement aid —
                // 891 entries for one 232MB track — and an unbounded one on a
                // mount that stays up all day is a list of hundreds of thousands
                // of tuples nobody will ever read.
                Fetches.Enqueue((offset, length, clock.ElapsedMilliseconds, fromMemory));
                while (Fetches.Count > MostRememberedFetches) Fetches.TryDequeue(out _);
            }
            catch (Exception ex)
            {
                _log("  fetch callback threw: " + ex.Message);
                try { Fail(ref info, offset, length); } catch { }
            }
            finally
            {
                leased?.EndLease();
                if (reader != null)
                {
                    lock (_readersGate)
                    {
                        reader.InFlight--;
                        if (reader.InFlight == 0 && !reader.Refused) _readers.Remove(processId);
                    }
                }
                Interlocked.Decrement(ref _callbacksInFlight);
            }
        }

        private void OnCancelFetch(
            ref CF_CALLBACK_INFO info,
            ref CF_CALLBACK_PARAMETERS_CANCEL parameters)
        {
            // Nothing to undo — a fetch here is a memory copy or one HTTP request
            // that will finish on its own. Registered so the transfer that
            // follows is recognised as cancelled rather than reported as broken.
            Interlocked.Increment(ref CancelledTransfers);
        }

        // ---- plumbing --------------------------------------------------------

        /// <summary>
        /// The file a callback is about, by name.
        ///
        /// Only called when a message is actually going out: pulling the path out
        /// of the callback allocates a string, and this runs on the thread the
        /// reader is waiting on.
        /// </summary>
        private static string CallbackName(ref CF_CALLBACK_INFO info) =>
            Path.GetFileName(Marshal.PtrToStringUni(info.NormalizedPath) ?? "");

        private CF_OPERATION_INFO Operation(ref CF_CALLBACK_INFO info, CF_OPERATION_TYPE type) => new()
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = type,
            ConnectionKey = info.ConnectionKey,
            TransferKey = info.TransferKey,
            CorrelationVector = info.CorrelationVector,
            RequestKey = info.RequestKey,
        };

        /// <summary>
        /// Tells Windows the read cannot be served, so the reader gets an error.
        ///
        /// It has to name the range Windows asked for. This used to say offset
        /// and nothing — a zero length at an unaligned offset — which Windows
        /// refuses, and a refused failure is no answer at all: the reader waited
        /// for ever. Every read that failed here, a Drive request timing out on a
        /// busy link included, left its reader parked; a player's download
        /// thread piled up behind each one, and any process ended while so
        /// parked could never finish exiting until Windows was restarted.
        /// </summary>
        private void Fail(ref CF_CALLBACK_INFO info, long offset, long length)
        {
            var opInfo = Operation(ref info, CF_OPERATION_TYPE.TRANSFER_DATA);
            var opParams = new CF_OPERATION_PARAMETERS_TRANSFER_DATA
            {
                ParamSize = (uint)Marshal.SizeOf<CF_OPERATION_PARAMETERS_TRANSFER_DATA>(),
                Buffer = IntPtr.Zero,
                Offset = offset,
                Length = length,
                CompletionStatus = unchecked((int)0xC0000001),
            };
            int hr = CfExecute(ref opInfo, ref opParams);
            if (hr != 0 && hr != ErrorCloudFileCancelled)
                _log($"  failing a read at [{offset:N0} +{length:N0}] was refused: 0x{hr:X8}");
            else
                Interlocked.Increment(ref FailedReads);
        }

        /// <summary>Reads answered with a failure that Windows accepted.</summary>
        public long FailedReads;

        /// <summary>
        /// The callback's path, as a path relative to the sync root. Windows
        /// reports the real directory even when the caller went through the drive
        /// letter, because the letter is resolved before the filesystem is
        /// reached.
        /// </summary>
        private string Relative(string fullPath)
        {
            if (fullPath.Length == 0) return "";

            // A caller who went through the drive letter hands us "F:\music".
            // Windows itself reports the real directory, because the letter is an
            // alias resolved before the filesystem is reached — so both forms
            // arrive here and both have to map to the same relative path.
            if (_letter != null &&
                fullPath.StartsWith(_letter.Letter, StringComparison.OrdinalIgnoreCase))
                return fullPath[_letter.Letter.Length..].Trim('\\').ToLowerInvariant();

            // Ends on a separator or ends the string, never merely starts with.
            //
            // PickRoot steps aside to GoogleDrive-2 when GoogleDrive is stuck, so
            // the two live side by side in the same folder — and a plain prefix
            // match let the GoogleDrive mount claim every path under
            // GoogleDrive-2 and hand back a key of "-2\something". That is the
            // manufactured key the sentinel below exists to refuse, arriving
            // through the one branch that never checked.
            int at = fullPath.IndexOf(Root, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && EndsOnABoundary(fullPath, at + Root.Length))
                return fullPath[(at + Root.Length)..].Trim('\\').ToLowerInvariant();

            // A normalized path can arrive without the volume, as "\dir\file".
            // Anchored to a path separator, not searched for anywhere in the
            // string: a bare IndexOf on the folder name matches "GoogleDrive"
            // inside "MyGoogleDriveBackup" and hands back a plausible-looking key
            // for a path on the wrong tree entirely. This map is what turns a
            // path into a Drive id, so a wrong answer here deletes the wrong file.
            var name = Path.GetFileName(Root);
            var anchored = "\\" + name + "\\";
            at = fullPath.IndexOf(anchored, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return fullPath[(at + anchored.Length)..].Trim('\\').ToLowerInvariant();

            if (fullPath.EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase) ||
                fullPath.Equals(name, StringComparison.OrdinalIgnoreCase))
                return "";

            // Not on this mount. Returning the whole path lower-cased was a
            // guess, and a guess that happens to collide with a real key is
            // indistinguishable from an answer.
            return NotOnThisMount;
        }

        /// <summary>
        /// Whether a prefix match ended where a path segment ends, rather than in
        /// the middle of a name. "GoogleDrive" is a prefix of "GoogleDrive-2" and
        /// they are different mounts.
        /// </summary>
        internal static bool EndsOnABoundary(string path, int index) =>
            index >= path.Length || path[index] == '\\' || path[index] == '/';

        /// <summary>
        /// Whether <paramref name="fullPath"/> is at or below
        /// <paramref name="root"/>. Shared with <see cref="GoogleDrive"/>, which
        /// asks the same question about the same two roots and answered it with a
        /// bare StartsWith — so it said yes for GoogleDrive-2 as well, and a
        /// leftover directory could not be deleted through the application
        /// because every delete was routed to Drive and refused there.
        /// </summary>
        internal static bool IsAtOrUnder(string fullPath, string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            var trimmed = root.TrimEnd('\\', '/');
            return fullPath.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase) &&
                   EndsOnABoundary(fullPath, trimmed.Length);
        }

        internal static string Sanitise(string name)
        {
            foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');

            // Trimmed until neither is left: stripping the dots of "notes.txt ."
            // exposes a space in front of them, and Windows strips that too.
            var cleaned = name.Trim();
            while (cleaned.Length > 0 && (cleaned[^1] == '.' || cleaned[^1] == ' '))
                cleaned = cleaned[..^1];

            // Trimming can take the whole name. Drive allows a file called "..",
            // or "...", or three spaces; NTFS allows none of them, so trimming
            // correctly leaves nothing at all — and a placeholder with an empty
            // relative name is a file that silently never appears, which is the
            // one outcome this function exists to prevent. Substituted rather
            // than trimmed, which is the answer the forbidden characters above
            // already get, and it keeps the length so two of them stay distinct.
            if (cleaned.Length == 0) cleaned = name.Length == 0 ? "_" : new string('_', name.Length);

            cleaned = Shorten(cleaned);

            // And a name Windows reserves for a device cannot be created either,
            // however many characters it has. "CON" off somebody's Drive is a
            // legitimate file name there and an unwritable one here.
            //
            // The underscore goes on the *stem*, not the end. A device name is
            // reserved with any extension at all — "CON.flac" is as unwritable as
            // "CON" — and the rule splits at the first dot, so "CON.flac_" is
            // still called CON and still cannot be created. "CON_.flac" can.
            if (!NameRules.IsUsableName(cleaned))
            {
                int dot = cleaned.IndexOf('.');
                cleaned = dot > 0 ? cleaned[..dot] + "_" + cleaned[dot..] : cleaned + "_";
            }

            return cleaned;
        }

        /// <summary>
        /// The most characters one path component may have here.
        ///
        /// NTFS stops at 255 and Drive has no such limit, so a long name is
        /// another way for a file to silently never appear. Five short of the
        /// real ceiling, which leaves room for the underscore a reserved name
        /// picks up above without having to trim again afterwards.
        /// </summary>
        private const int MaxNameLength = 250;

        /// <summary>
        /// A name short enough that " (2)" and the rest survive
        /// <see cref="Sanitise"/>. Numbered at full length, the number was cut
        /// off again and every candidate came back as the name already taken.
        /// </summary>
        internal static string RoomForNumber(string name) => Shorten(name, MaxNameLength - 10);

        private static string Shorten(string name, int most = MaxNameLength)
        {
            if (name.Length <= most) return name;

            // The extension is worth keeping — it is what decides whether the
            // file opens — but only if it looks like one rather than a dot that
            // happens to be near the end of a very long name.
            var extension = Path.GetExtension(name);
            if (extension.Length > 32) extension = "";

            int cut = most - extension.Length;

            // Never between the two halves of a surrogate pair: half an emoji is
            // a name that does not survive being written anywhere as UTF-8.
            if (cut > 0 && char.IsHighSurrogate(name[cut - 1])) cut--;

            var stem = name[..cut];
            var shortened = (stem.TrimEnd('.', ' ') + extension).TrimEnd('.', ' ');

            // A name of dots and spaces trims to nothing; cut, it is still a name.
            return shortened.Length > 0 ? shortened : name[..cut];
        }

        /// <summary>
        /// A name not yet in <paramref name="used"/>, claimed. The numbering is
        /// <see cref="NameRules.UniqueAmong"/>'s, so a folder is numbered whole
        /// and ".env" becomes ".env (2)" rather than " (2).env".
        /// </summary>
        internal static string Unique(string name, HashSet<string> used, bool folder = false)
        {
            var chosen = NameRules.UniqueAmong(name, used.Contains, folder);
            used.Add(chosen);
            return chosen;
        }

        /// <summary>
        /// The download for a file, made if this is the first anyone has asked.
        ///
        /// Two things the obvious <c>GetOrAdd(id, factory)</c> got wrong, and both
        /// only under the concurrency this is guaranteed to see — these callbacks
        /// arrive on filter-driver workers, several at once.
        ///
        /// The factory overload may run the factory more than once for one key
        /// and keep only one result. A DriveFileCache starts downloading *in its
        /// constructor*, so every discarded one carried on pulling the file down
        /// for ever, holding a cancellation source nobody would ever cancel and
        /// evicting the chunks of the file that was actually being played. The
        /// value overload never runs a factory, so each racing thread can dispose
        /// the one it lost with.
        ///
        /// And nothing released the winners either. "One track at a time" is what
        /// <see cref="ReleaseAllBut"/> is for and nothing called it, so reading a
        /// byte of a file — a tag, a thumbnail, a folder listing touching it —
        /// started a download of the whole thing that ran to completion, and
        /// every file ever touched kept one. Memory stayed inside the pool's
        /// budget, which is what made it invisible; the network did not.
        /// </summary>
        private DriveFileCache CacheOpenedFor(string fileId, long size)
        {
            // Leased before it is handed out, so eviction cannot stop its
            // download between here and the read; the caller ends the lease.
            // A cache let go in that moment is simply passed over.
            for (int attempt = 0; ; attempt++)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(DriveMount));

                if (_caches.TryGetValue(fileId, out var existing))
                {
                    // A cache sized for other contents is for a version of the
                    // file that is no longer the one on disk.
                    if (existing.Size != size && attempt < 8)
                    {
                        ReleaseFile(fileId);
                        continue;
                    }

                    if (existing.TryLease())
                    {
                        existing.LastUsed = Environment.TickCount64;
                        return existing;
                    }
                    _caches.TryRemove(new KeyValuePair<string, DriveFileCache>(fileId, existing));
                    continue;
                }

                var fresh = new DriveFileCache(_drive, _pool, fileId, size);
                fresh.TryLease();
                var kept = _caches.GetOrAdd(fileId, fresh);

                if (!ReferenceEquals(kept, fresh))
                {
                    // Somebody else got there first. Ours is already downloading;
                    // it is stopped without dropping the chunks, which belong to
                    // the file and so to the cache that was kept.
                    fresh.EndLease();
                    fresh.Abandon();
                    continue;
                }

                KeepRecent();
                return kept;
            }
        }

        private volatile bool _disposed;

        /// <summary>
        /// How many files may have a download open at once.
        ///
        /// The rule used to be exactly one, and starting a second stopped the
        /// first. For a player that is right — there is one track, and the file
        /// somebody has just skipped to is the only one worth spending the link
        /// on. For a *copy* it is a disaster, and the copy is what this drive is
        /// mostly used for.
        ///
        /// Robocopy runs eight to sixteen files at a time. Under "one at a time"
        /// every file it opened tore down the download of the file it had opened
        /// a moment earlier, so fifteen of sixteen readers were being served with
        /// no cache at all — one Drive request per 256KB callback, at a measured
        /// 0.21MB/s each — while their downloads were started, cancelled and
        /// started again. Long enough at that rate and Windows gives up on the
        /// callback: "the cloud operation was not completed before the time-out
        /// period expired", which is a file that fails to copy for no reason
        /// anybody can see.
        ///
        /// Three, rather than one or sixteen. It is enough that a copy and a
        /// track playing alongside it do not evict each other, and small enough
        /// that the link is not split so many ways that every single file is slow
        /// — which is the same failure by a different route, since a callback
        /// that waits too long fails whatever it is waiting for.
        /// </summary>
        private const int MaxOpenFiles = 3;

        /// <summary>
        /// Keeps the few most recently read files and lets the rest go.
        ///
        /// Least-recently-used, and by *read* rather than by when the download
        /// started: a file being copied is read continuously, so its download
        /// survives however many other files are touched around it, and a file
        /// nothing has looked at since it was opened is the one that goes.
        /// </summary>
        private void KeepRecent()
        {
            var open = _caches.ToArray();
            if (open.Length <= MaxOpenFiles) return;

            foreach (var (id, cache) in open
                         .OrderByDescending(pair => pair.Value.LastUsed)
                         .Skip(MaxOpenFiles))
            {
                // A file in the middle of being read is not the one to let go of,
                // whatever its age: a copy warming three files evicted the track
                // that was playing, and failed the read that was answering it.
                if (!Retire(id, cache)) continue;

                _fetchStarted.TryRemove(id, out _);
                _fetchFinished.TryRemove(id, out _);
            }
        }

        /// <summary>
        /// Lets one cache go, if nobody is reading it, and takes it out of the
        /// table by instance. By key alone, a newer cache for the same file
        /// could be the one removed while this one was disposed: its download
        /// then ran on untracked and uncancelled.
        /// </summary>
        private bool Retire(string id, DriveFileCache cache)
        {
            if (!cache.TryRetire()) return false;
            _caches.TryRemove(new KeyValuePair<string, DriveFileCache>(id, cache));
            return true;
        }

        /// <summary>
        /// Everything this mount holds for one file, for a file whose contents
        /// have changed in Drive: a cache sized for the old contents would serve
        /// them under the new placeholder.
        /// </summary>
        private void ReleaseFile(string id)
        {
            if (!_caches.TryGetValue(id, out var cache)) return;
            if (!cache.TryRetire())
            {
                // Being read. Taken out of the table so the next open starts
                // afresh, and stopped once the reader is done with it.
                _caches.TryRemove(new KeyValuePair<string, DriveFileCache>(id, cache));
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 200 && !cache.TryRetire(); i++) await Task.Delay(500);

                    // Stopped regardless after that: a reader that has held it
                    // for over a minute and a half is past its own deadline.
                    cache.Dispose();
                });
                return;
            }
            _caches.TryRemove(new KeyValuePair<string, DriveFileCache>(id, cache));
            _fetchStarted.TryRemove(id, out _);
            _fetchFinished.TryRemove(id, out _);
        }

        /// <summary>
        /// Everything, or everything but one. The unmount path, and the way to
        /// give the whole link back to a single file when that is what is wanted.
        /// </summary>
        public void ReleaseAllBut(string? keepFileId)
        {
            foreach (var (id, cache) in _caches.ToArray())
            {
                if (id == keepFileId) continue;
                if (!_caches.TryRemove(new KeyValuePair<string, DriveFileCache>(id, cache))) continue;

                cache.Dispose();

                // Dropped with the cache, so a track that is opened again after
                // its memory went back says it is being fetched again — which it
                // is, from the beginning.
                _fetchStarted.TryRemove(id, out _);
                _fetchFinished.TryRemove(id, out _);
            }
        }

        /// <summary>
        /// A sync root outlives the process that registered it, and so does a
        /// drive letter, so this runs on every exit path. Leaving either behind
        /// is a folder of placeholders nobody is left to hydrate.
        /// </summary>
        public void Dispose()
        {
            // Nothing new is opened from here on: a warm arriving from a copy
            // made a moment ago would start downloads nobody would ever stop.
            _disposed = true;

            // Before the callback wait, so a sweep cannot start one more teardown
            // while that wait is counting.
            try { _sweep.Dispose(); } catch { }

            _letter?.Dispose();
            _letter = null;

            // Nothing may be halfway through a read when the provider goes.
            //
            // This is what made a process impossible to kill and a reboot the
            // only way out. A read of a placeholder is answered by this process;
            // disconnect while one is in flight and the reader waits for an
            // answer that can never come — and a reader inside *this* process
            // then cannot finish exiting either, because Windows will not let a
            // process go while it has I/O outstanding. Measured twice: exit code
            // set, runtime gone, threads parked in the kernel, taskkill refused,
            // gone only after a restart.
            //
            // Five seconds is a ceiling, not a wait: the callbacks have their own
            // deadlines and are normally finished the moment nothing is reading.
            // Whatever is still going after that is worse than what this costs.
            // Failed rather than waited for: a read left to run out its deadline
            // is a reader still pending when the provider disconnects.
            try { _closing.Cancel(); } catch { }
            WaitForCallbacks(TimeSpan.FromSeconds(5));

            ReleaseAllBut(null);

            // Before disconnecting, not after: once the provider is gone nothing
            // can service a delete and the placeholders are stuck until the next
            // run connects again.
            try { ClearContents(); } catch { }

            try { if (_connection != 0) { CfDisconnectSyncRoot(_connection); _connection = 0; } } catch { }
            try { if (_registered) { CfUnregisterSyncRoot(Root); _registered = false; } } catch { }
        }
    }
}












