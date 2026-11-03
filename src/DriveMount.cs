using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

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
    /// Google Drive as a real drive, through WinFsp (4.3.0): a network-type
    /// volume on a letter, answered from here. Every program reads, writes,
    /// renames and deletes on it as on any drive, and every change goes to
    /// Google — reads through the chunk cache, writes through
    /// <see cref="DriveMount"/>'s write-back (DriveMount.Writes.cs).
    ///
    /// It replaced a Cloud Files sync root on a subst letter, which looked like
    /// a drive but was a folder of placeholders: another program's save, move
    /// or rename there changed only this PC (Morgan, 4.2.10: a 204 GB folder
    /// moved in PowerShell vanished from the letter and never reached Google).
    ///
    /// Directories are listed on demand, one Drive listing when somebody first
    /// looks, and kept: <see cref="_listings"/> is what the volume answers
    /// from. A listing older than <see cref="FreshFor"/> is served at once and
    /// listed again behind it, and Windows is told what changed.
    /// </summary>
    internal sealed partial class DriveMount : IDisposable
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

        /// <summary>Whether the account root shows the Trash folder (Preferences, Google Drive). Asked at each listing of the root.</summary>
        public Func<bool> ShowTrash { get; set; } = () => true;

        /// <summary>Whether this path is the Trash folder itself.</summary>
        public bool IsTrashFolder(string? fullPath) => FolderIdFor(fullPath) == DriveClient.TrashId;

        /// <summary>Whether this path is something in the Trash folder, at any depth (not the folder itself).</summary>
        public bool IsInTrash(string? fullPath)
        {
            var key = KeyFor(fullPath);
            if (string.IsNullOrEmpty(key)) return false;
            var parent = Parent(key);
            return parent != null && IsUnderTrash(parent);
        }

        /// <summary>Whether a relative folder is the Trash folder or inside it: listed as trashed.</summary>
        private bool IsUnderTrash(string relative)
        {
            for (string? at = relative; !string.IsNullOrEmpty(at); at = Parent(at))
                if (_folders.TryGetValue(at, out var id) && id == DriveClient.TrashId) return true;
            return false;
        }

        private static string? Parent(string relative)
        {
            int slash = relative.LastIndexOf('\\');
            return slash < 0 ? "" : relative[..slash];
        }

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
        /// This is the listing that has already been paid for, and it is what
        /// the volume answers Windows from: every name, size and date Drive
        /// returned, kept, so a folder opened again costs nothing.
        ///
        /// The names here are the names on the letter, after Sanitise and Unique
        /// have had them — not the names Drive returned. A listing that disagreed
        /// with the letter about what a file is called would hand back paths that
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
        /// Matching on the id instead is exact. Answered from
        /// <see cref="_whereIs"/> when that still holds, checked against
        /// <c>_ids</c>; the walk of the folder's listing is only the fallback.
        /// Walking first, lower-casing every row, made a search with many hits
        /// in one big folder quadratic.
        /// </summary>
        public string? ChildPathForId(string folderFullPath, string driveId)
        {
            var key = KeyFor(folderFullPath);
            if (key == null || string.IsNullOrEmpty(driveId)) return null;
            if (!_listings.TryGetValue(key, out var placed)) return null;

            var prefix = key.Length == 0 ? "" : key + "\\";

            // A hint, and only that: an id in two folders, a placement undone or
            // a row forgotten all leave it pointing somewhere else, so it counts
            // only when the map agrees.
            if (_whereIs.TryGetValue(driveId, out var where) && where.Folder == key &&
                _ids.TryGetValue((prefix + where.Name).ToLowerInvariant(), out var bound) && bound == driveId)
                return Path.Combine(folderFullPath, where.Name);

            foreach (var item in placed)
            {
                var childKey = (prefix + item.Name).ToLowerInvariant();
                if (_ids.TryGetValue(childKey, out var id) && id == driveId)
                    return Path.Combine(folderFullPath, item.Name);
            }

            return null;
        }

        /// <summary>
        /// Where each Drive id was last placed: its folder's key and its name on
        /// the letter, in its own case. Written beside every <c>_ids</c> entry
        /// Bind makes and never trusted without checking that.
        /// </summary>
        private readonly ConcurrentDictionary<string, (string Folder, string Name)> _whereIs = new(StringComparer.Ordinal);

        /// <summary>
        /// What Drive calls the item at a path on this mount, from its folder's
        /// listing however old, or null when no listing says. The name on disk
        /// is Sanitise's and Unique's ("AC_DC.flac", "song (2).flac"), and is
        /// not the name to describe the file to Drive by.
        /// </summary>
        public string? DriveNameAt(string? fullPath)
        {
            var key = KeyFor(fullPath);
            if (string.IsNullOrEmpty(key)) return null;
            int cut = key.LastIndexOf('\\');
            string parent = cut < 0 ? "" : key[..cut];
            string leaf = cut < 0 ? key : key[(cut + 1)..];
            if (!_listings.TryGetValue(parent, out var placed)) return null;
            foreach (var item in placed)
                if (item.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase)) return item.DriveName;
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
        /// actually given on the letter, or null when it could not be placed.
        ///
        /// The name matters to the caller. Drive lets two files in one folder
        /// share a name and Windows does not, so uploading a second "song.flac"
        /// into a folder that already has one is a perfectly ordinary thing to do
        /// — and it has to be called something else here. Whoever asked for the
        /// upload then needs to be told what it ended up as, and a tree upload
        /// needs it to know where to put the next level down.
        ///
        /// <paramref name="populated"/> is for a folder this application has just
        /// *created*, where "what is in it" is known without asking: nothing.
        /// Marking it saves a Drive listing on the first visit whose only possible
        /// answer is the empty one we already have.
        /// </summary>
        public string? PlaceOne(string parentFullPath, DriveEntry entry, bool populated = false)
        {
            var relative = KeyFor(parentFullPath);
            return relative == null ? null : PlaceRow(relative, entry, populated);
        }

        /// <summary>
        /// One entry into the listing of the folder at <paramref name="relative"/>,
        /// named against everything that folder already shows, and Windows told.
        /// An entry already placed there under some name keeps that name and has
        /// its size and date brought up to date.
        /// </summary>
        internal string? PlaceRow(string relative, DriveEntry entry, bool populated = false)
        {
            try
            {
                // A Google document has no bytes to show; see BuildEntries.
                if (entry.IsGoogleDocument) return null;

                // Something new inside a shared drive is in that drive, and a folder
                // that does not know so lists as empty.
                if (string.IsNullOrEmpty(entry.DriveId) && _driveIds.TryGetValue(relative, out var inDrive))
                    entry = entry with { DriveId = inDrive };

                string name;
                DrivePlaced row;
                bool fresh;
                lock (PlaceGate(relative))
                {
                    // Placed here already (a listing got there first, or a retry):
                    // that row, brought up to date.
                    if (_whereIs.TryGetValue(entry.Id, out var where) && where.Folder == relative &&
                        _ids.TryGetValue(ChildKey(relative, where.Name), out var bound) && bound == entry.Id)
                    {
                        name = where.Name;
                        fresh = false;
                    }
                    else
                    {
                        // Seeded with what is already there, which is the difference
                        // between a duplicate name being numbered and a duplicate
                        // name quietly stealing the map entry of the file it
                        // collided with — the next delete on that path would trash
                        // the wrong one.
                        var used = NamesShownIn(relative);
                        name = Unique(Sanitise(entry.Name), used, entry.IsFolder);
                        fresh = true;
                    }

                    Bind(relative, name, entry);
                    row = RowFor(name, entry);
                    var placedRow = row;
                    _listings.AddOrUpdate(relative, new[] { placedRow }, (_, now) => now
                        .Where(p => !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        .Append(placedRow).ToArray());

                    if (populated)
                    {
                        // Made a moment ago, so empty: whatever an older folder of
                        // this name left under the key is not its contents.
                        var child = ChildKey(relative, name);
                        lock (_treeGate)
                        {
                            ForgetUnder(child);
                            _populated[child] = true;
                            _listings[child] = Array.Empty<DrivePlaced>();
                            _listedAt[child] = Environment.TickCount64;
                        }
                    }
                }

                // Contents that may have changed under an id already held: the
                // download kept for it is of the old ones.
                if (!fresh && !entry.IsFolder) ReleaseFile(entry.Id);
                Changed(relative, name, entry.IsFolder, fresh ? WinFsp.ActionAdded : WinFsp.ActionModified);
                return name;
            }
            catch (Exception ex)
            {
                _log($"  could not place {entry.Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Under _treeGate: every map entry below the folder at <paramref name="key"/>
        /// (not the folder itself), for a folder that has only just been made.
        /// Files saved here are left to their own handling.
        /// </summary>
        private void ForgetUnder(string key)
        {
            var below = key + "\\";
            bool Under(string k) => k.StartsWith(below, StringComparison.Ordinal);
            foreach (var k in _ids.Keys) if (Under(k)) _ids.TryRemove(k, out _);
            foreach (var k in _folders.Keys) if (Under(k)) _folders.TryRemove(k, out _);
            foreach (var k in _driveIds.Keys) if (Under(k)) _driveIds.TryRemove(k, out _);
            foreach (var k in _listings.Keys) if (Under(k)) _listings.TryRemove(k, out _);
            foreach (var k in _listedAt.Keys) if (Under(k)) _listedAt.TryRemove(k, out _);
            foreach (var k in _populated.Keys) if (Under(k)) _populated.TryRemove(k, out _);
            foreach (var k in _answeredOffline.Keys) if (Under(k)) _answeredOffline.TryRemove(k, out _);
            foreach (var (id, where) in _whereIs.ToArray())
                if (where.Folder == key || Under(where.Folder))
                    _whereIs.TryRemove(new KeyValuePair<string, (string, string)>(id, where));
        }

        /// <summary>Every name the folder at <paramref name="relative"/> shows now, files kept only here included.</summary>
        private HashSet<string> NamesShownIn(string relative)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_listings.TryGetValue(relative, out var siblings))
                foreach (var s in siblings) used.Add(s.Name);
            foreach (var copy in PendingIn(relative)) used.Add(copy.Name);
            return used;
        }

        /// <summary>The map entries for one placed entry: its id, where it is, and for a folder its folder id and drive.</summary>
        private void Bind(string relative, string name, DriveEntry entry)
        {
            var key = ChildKey(relative, name);
            _ids[key] = entry.Id;
            _whereIs[entry.Id] = (relative, name);
            if (entry.IsFolder) _folders[key] = entry.Id;
            if (entry.IsFolder && !string.IsNullOrEmpty(entry.DriveId)) _driveIds[key] = entry.DriveId;
        }

        /// <summary>A Drive entry as the row its folder's listing keeps for it.</summary>
        private static DrivePlaced RowFor(string name, DriveEntry entry) =>
            new(name, entry.IsFolder, entry.IsFolder ? -1 : Math.Max(0, entry.Size),
                entry.Modified == default ? DateTime.Now : entry.Modified.ToLocalTime(), entry.Name);

        /// <summary>The key of <paramref name="name"/> inside the folder at <paramref name="relative"/>.</summary>
        internal static string ChildKey(string relative, string name) =>
            (relative.Length == 0 ? name : relative + "\\" + name).ToLowerInvariant();

        // Placements into one folder are named one at a time: two arriving
        // together both found a name free and both took it.
        private readonly object[] _placeGates = Enumerable.Range(0, 32).Select(_ => new object()).ToArray();
        private object PlaceGate(string relative) =>
            _placeGates[(StringComparer.Ordinal.GetHashCode(relative) & 0x7FFFFFFF) % _placeGates.Length];

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
        /// Takes something that is no longer in Drive off the letter: out of the
        /// maps, out of its folder's listing, and Windows told.
        /// </summary>
        public bool RemoveOne(string fullPath)
        {
            var key = KeyFor(fullPath);
            return key != null && RemoveKey(key);
        }

        internal bool RemoveKey(string key)
        {
            lock (_treeGate) return RemoveKeyHeld(key);
        }

        private bool RemoveKeyHeld(string key)
        {
            try
            {
                // Never the root.
                if (key.Length == 0) return false;
                bool wasFolder = _folders.ContainsKey(key);
                DropPendingUnder(key);

                // Everything underneath as well, not just this key.
                //
                // Trashing a folder takes everything in it, and leaving
                // the children in the map leaves live Drive ids addressed by paths
                // that no longer exist — so a later lookup could hand one to Trash
                // or MoveOrRename. CLAUDE.md is explicit that the path-to-id half
                // is where a mistake deletes the wrong thing, and this was that
                // mistake waiting for a folder to be deleted.
                var prefix = key + "\\";
                bool Under(string entry) =>
                    entry == key || entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

                foreach (var map in new[] { _ids, _folders, _driveIds })
                    foreach (var entry in map.Keys)
                        if (Under(entry)) map.TryRemove(entry, out _);

                foreach (var entry in _populated.Keys)
                    if (Under(entry)) _populated.TryRemove(entry, out _);

                foreach (var entry in _answeredOffline.Keys)
                    if (Under(entry)) _answeredOffline.TryRemove(entry, out _);

                foreach (var entry in _listings.Keys)
                    if (Under(entry)) _listings.TryRemove(entry, out _);

                // The rest of what is kept per folder. A stale stamp or mark
                // outlived the folder: a later folder of the same name was taken
                // as freshly listed, and the hand-back timer kept ticking for a
                // directory that is gone.
                foreach (var entry in _listedAt.Keys)
                    if (Under(entry)) _listedAt.TryRemove(entry, out _);

                // And out of the parent's listing, which is what the pane is
                // built from. Dropping the maps for the thing itself is not
                // enough: the folder above still has it in the list it was
                // populated with, so a deleted file would go on being shown
                // until something re-fetched that folder.
                int cut = key.LastIndexOf('\\');
                string parent = cut < 0 ? "" : key[..cut];
                string leaf = cut < 0 ? key : key[(cut + 1)..];

                // Where each id under it was placed, by the folder and name.
                foreach (var (id, where) in _whereIs.ToArray())
                    if (Under(where.Folder) ||
                        (where.Folder == parent && where.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase)))
                        _whereIs.TryRemove(new KeyValuePair<string, (string, string)>(id, where));

                // Atomically: an upload placing a row in the same folder at the
                // same moment (a replace among parallel uploads) lost its row.
                string shown = leaf;
                EditListing(parent, siblings =>
                {
                    var gone = siblings.FirstOrDefault(s => string.Equals(s.Name, leaf, StringComparison.OrdinalIgnoreCase));
                    if (gone != null) shown = gone.Name;
                    return siblings.Where(s => !ReferenceEquals(s, gone)).ToArray();
                });

                Changed(parent, shown, wasFolder, WinFsp.ActionRemoved);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Follows a move or rename Drive has already agreed to, and returns the
        /// name it was given on the letter — or null when it was not followed,
        /// and RemoveOne then PlaceOne is the way.
        ///
        /// RemoveOne then PlaceOne throws away the listing of everything under a
        /// folder, and the next visit pays Drive for all of it again: renaming a
        /// folder of two thousand tracks re-listed the lot. This re-keys the maps
        /// under it instead.
        ///
        /// Refused rather than risked: the root, a move into itself, a listing
        /// running in or around it (it would write keys under the old path), a
        /// move between shared drives (everything below would carry the wrong
        /// drive), and the virtual folders.
        /// </summary>
        public string? MoveLocal(string fromFullPath, string toFolderFullPath, string driveName)
        {
            try
            {
                var fromKey = KeyFor(fromFullPath);
                var toParent = KeyFor(toFolderFullPath);
                if (fromKey == null || fromKey.Length == 0 || toParent == null) return null;
                if (!_ids.ContainsKey(fromKey)) return null;

                bool isFolder = _folders.ContainsKey(fromKey);
                string fromParent = Parent(fromKey) ?? "";
                string fromLeaf = Path.GetFileName(fromFullPath.TrimEnd(Path.DirectorySeparatorChar));

                if (toParent == fromKey || toParent.StartsWith(fromKey + "\\", StringComparison.Ordinal)) return null;
                if (Busy(fromKey, fromParent, toParent)) return null;

                _driveIds.TryGetValue(fromParent, out var fromDrive);
                _driveIds.TryGetValue(toParent, out var toDrive);
                if (!string.Equals(fromDrive, toDrive, StringComparison.Ordinal)) return null;
                if (IsSharedDrivesFolder(toFolderFullPath) || FolderIdFor(fromFullPath) == DriveClient.SharedDrivesId ||
                    IsSharedDriveRoot(fromFullPath))
                    return null;

                // Named the way PlaceOne names it, against what the destination
                // already shows — less the thing itself, so a rename that only
                // changes case is not numbered against its own old name.
                var used = NamesShownIn(toParent);
                if (toParent == fromParent) used.Remove(fromLeaf);
                var name = Unique(Sanitise(driveName), used, isFolder);

                return MoveKey(fromKey, toParent, name, driveName) ? name : null;
            }
            catch (Exception ex)
            {
                _log($"  could not move {Path.GetFileName(fromFullPath)} in place: {ex.Message}");
                return null;
            }
        }

        /// <summary>Whether a listing is running in or around a move's two ends.</summary>
        private bool Busy(string fromKey, string fromParent, string toParent)
        {
            foreach (var busy in _populating.Keys)
                if (busy == fromKey || busy.StartsWith(fromKey + "\\", StringComparison.Ordinal) ||
                    busy == fromParent || busy == toParent)
                    return true;
            return false;
        }

        /// <summary>
        /// The maps, the two listings, files kept only here and Windows, all
        /// following one item to <paramref name="name"/> in the folder at
        /// <paramref name="toParent"/>. The name is taken as given: the caller
        /// has made sure it is free.
        /// </summary>
        internal bool MoveKey(string fromKey, string toParent, string name, string driveName)
        {
            lock (_treeGate) return MoveKeyHeld(fromKey, toParent, name, driveName);
        }

        private bool MoveKeyHeld(string fromKey, string toParent, string name, string driveName)
        {
            string fromParent = Parent(fromKey) ?? "";
            string fromLeaf = fromKey[(fromKey.LastIndexOf('\\') + 1)..];
            bool isFolder = _folders.ContainsKey(fromKey);
            var toKey = ChildKey(toParent, name);

            // Its row, from the listing it leaves, to the one it joins.
            DrivePlaced? row = null;
            EditListing(fromParent, leaving =>
            {
                row = leaving.FirstOrDefault(p => p.Name.Equals(fromLeaf, StringComparison.OrdinalIgnoreCase));
                return leaving.Where(p => !ReferenceEquals(p, row)).ToArray();
            });
            string oldName = row?.Name ?? fromLeaf;

            if (toKey != fromKey)
            {
                RekeyTree(fromKey, toKey);
                // A listing still running in or under it marked its folder
                // populated when it began, and that mark moved with the rest:
                // the folder would count as listed with nothing written. The
                // listing itself is dropped (StillListing), so it is listed again.
                foreach (var busy in _populating.Keys)
                    if (busy == fromKey || busy.StartsWith(fromKey + "\\", StringComparison.Ordinal))
                        ForgetKey(toKey + busy[fromKey.Length..]);
            }
            if (_ids.TryGetValue(toKey, out var id)) _whereIs[id] = (toParent, name);
            MovePending(fromKey, toKey, name);

            row = row == null
                ? new DrivePlaced(name, isFolder, isFolder ? -1 : 0, DateTime.Now, driveName)
                : row with { Name = name, DriveName = driveName };

            var joiningRow = row;
            _listings.AddOrUpdate(toParent, new[] { joiningRow }, (_, joining) => joining
                .Where(p => !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Append(joiningRow).ToArray());

            Changed(fromParent, oldName, isFolder, WinFsp.ActionRemoved);
            Changed(toParent, name, isFolder, WinFsp.ActionAdded);
            return true;
        }

        /// <summary>
        /// Rewrites one folder's listing as a single step against whatever it
        /// holds at that moment. A read, an edit and a plain store lost any row
        /// another thread added in between: an upload's PlaceOne landing while a
        /// replace removed the old copy left the new file off the pane.
        /// </summary>
        private void EditListing(string key, Func<DrivePlaced[], DrivePlaced[]> edit)
        {
            // Until it lands: a losing attempt means another writer's landed, so
            // this always ends, and giving up after a bound dropped the edit.
            while (true)
            {
                if (!_listings.TryGetValue(key, out var now)) return;
                var next = edit(now);
                if (ReferenceEquals(next, now) || _listings.TryUpdate(key, next, now)) return;
            }
        }

        /// <summary>
        /// Every map entry at or below <paramref name="from"/>, moved to the same
        /// place below <paramref name="to"/>. Both are keys: lower case, relative
        /// to the root.
        /// </summary>
        private void RekeyTree(string from, string to)
        {
            static void Move<T>(ConcurrentDictionary<string, T> map, string from, string to)
            {
                var below = from + "\\";
                foreach (var (key, value) in map.ToArray())
                {
                    if (key != from && !key.StartsWith(below, StringComparison.Ordinal)) continue;
                    if (map.TryRemove(new KeyValuePair<string, T>(key, value)))
                        map[to + key[from.Length..]] = value;
                }
            }

            Move(_ids, from, to);
            Move(_folders, from, to);
            Move(_driveIds, from, to);
            Move(_listings, from, to);
            Move(_listedAt, from, to);
            Move(_populated, from, to);
            Move(_answeredOffline, from, to);

            var under = from + "\\";
            foreach (var (id, where) in _whereIs.ToArray())
                if (where.Folder == from || where.Folder.StartsWith(under, StringComparison.Ordinal))
                    _whereIs.TryUpdate(id, (to + where.Folder[from.Length..], where.Name), where);
        }

        /// <summary>
        /// The volume's own name, \\GoogleDrive\share: what it answers to when
        /// it is not reached through its letter. Paths under either are on this
        /// mount.
        /// </summary>
        public string Root { get; }

        private WinFsp.Host? _host;
        private Volume? _volume;
        private readonly object _letterGate = new();

        // The letter Drive was given, kept while it is hidden for being offline,
        // so paths on it still mean Drive to everything that asks.
        private string? _letterName;

        /// <summary>The letter Drive is on ("G:"), also while it is hidden because Google cannot be reached.</summary>
        public string? Letter => _letterName;

        /// <summary>Whether the letter is there right now: false while hidden because Google cannot be reached.</summary>
        public bool LetterShown => _host?.MountPoint != null;

        /// <summary>
        /// Puts the mounted drive on another letter. Open files carry on; the
        /// listings and caches stay, so nothing is fetched again. Does nothing
        /// when it is already on <paramref name="preferred"/>. Falls back to the
        /// next free letter, as a first mount does.
        /// </summary>
        public void MoveLetter(char preferred)
        {
            lock (_letterGate)
            {
                var host = _host;
                var current = _letterName;
                if (host == null || current == null) return;
                if (char.ToUpperInvariant(current[0]) == char.ToUpperInvariant(preferred)) return;

                if (host.MountPoint == null)
                {
                    // Hidden while offline: it comes back on the new letter.
                    var free = DriveLetter.FirstFree(preferred);
                    if (free != null) _letterName = free;
                    return;
                }

                host.Unmount();
                try { _letterName = DriveLetter.Take(host, preferred, Notify); }
                catch
                {
                    host.Mount(current, out _);
                    throw;
                }
                _log($"drive letter {current} -> {_letterName}");
            }
        }
        public ConcurrentQueue<(long Offset, long Length, long Milliseconds, bool FromMemory)> Fetches { get; } = new();

        /// <summary>How many of those to keep. Enough for a whole track, not a whole day.</summary>
        private const int MostRememberedFetches = 4096;
        public int Listings;

        public DriveMount(DriveClient drive, string root, long budgetBytes, Action<string> log)
        {
            _drive = drive;
            Root = root;
            _pool = new DriveCachePool(budgetBytes);
            _log = log;
            // Both stand still until there is something for them to do: a mount
            // with nothing open and nothing marked wakes nobody up. See WorkTimer.
            _sweep = new WorkTimer(SweepIdle, () => !_caches.IsEmpty && IdleRelease > TimeSpan.Zero);
            _probe = new Probe(this);
            _drive.Health.Changed += OnHealthChanged;
            _drive.TrashEdited += OnTrashEdited;
        }

        /// <summary>
        /// Something was trashed, restored or deleted for good through this
        /// client: the Trash folder is listed again on its next visit, from the
        /// kept copy, so a file just deleted is there to restore.
        /// </summary>
        private void OnTrashEdited()
        {
            if (TrashKey() is { } key) ForgetKey(key);
        }

        /// <summary>The key of the Trash folder on the letter, or null while it has not been listed.</summary>
        internal string? TrashKey()
        {
            foreach (var pair in _folders)
                if (pair.Value == DriveClient.TrashId) return pair.Key;
            return null;
        }

        /// <summary>Forgets the listing of every folder on the letter that is the Drive folder <paramref name="id"/>.</summary>
        internal void ForgetFolderId(string id)
        {
            foreach (var pair in _folders)
                if (pair.Value == id) ForgetKey(pair.Key);
        }


        /// <summary>
        /// A timer that ticks every <see cref="SweepInterval"/> only while there
        /// is work for it, so an idle mount costs no wakeups at all (on a laptop,
        /// a tick every 15 seconds for as long as the app is open is a CPU kept
        /// out of its deepest sleep for nothing).
        ///
        /// Whoever adds work calls <see cref="Arm"/> *after* adding it. After each
        /// tick the timer stops itself if the work is gone, and it checks under the
        /// same lock <see cref="Arm"/> takes. Either an add is seen by that check,
        /// or its Arm comes after the stop and starts the timer again. Nothing is
        /// ever left waiting with the timer stopped.
        /// </summary>
        internal sealed class WorkTimer : IDisposable
        {
            private readonly object _gate = new();
            private readonly Timer _timer;
            private readonly Action _work;
            private readonly Func<bool> _hasWork;
            private bool _armed;
            private bool _disposed;

            public WorkTimer(Action work, Func<bool> hasWork)
            {
                _work = work;
                _hasWork = hasWork;
                _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
            }

            public bool Armed { get { lock (_gate) return _armed; } }

            /// <summary>Starts ticking, if it is not already. Cheap; call it on every add.</summary>
            public void Arm()
            {
                lock (_gate)
                {
                    if (_armed || _disposed) return;
                    _armed = true;
                    var every = SweepInterval;
                    _timer.Change(every, every);
                }
            }

            private void Tick()
            {
                // A throw on a timer thread ends the process.
                try { _work(); } catch { }
                Settle();
            }

            /// <summary>Stops ticking if, and only if, there is nothing left to do.</summary>
            internal void Settle()
            {
                lock (_gate)
                {
                    if (!_armed || _disposed) return;
                    bool more;
                    try { more = _hasWork(); } catch { more = true; }
                    if (more) return;
                    _armed = false;
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    _disposed = true;
                    _armed = false;
                }
                _timer.Dispose();
            }
        }

        /// <summary>Whether the idle sweep is ticking. For tests.</summary>
        internal bool SweepArmed => _sweep.Armed;


        /// <summary>How many files have a cache open. For tests.</summary>
        internal int OpenCaches => _caches.Count;



        // ---- warming, and letting go again ----------------------------------

        /// <summary>
        /// How long a file may sit unread before its download and its chunks are
        /// let go. Zero never releases, which is the old behaviour.
        ///
        /// Set from Preferences, and read on every sweep rather than captured, so
        /// changing it takes effect without remounting.
        /// </summary>
        public TimeSpan IdleRelease
        {
            get => _idleRelease;
            set
            {
                _idleRelease = value;
                if (value > TimeSpan.Zero) _sweep?.Arm();
            }
        }

        private TimeSpan _idleRelease = TimeSpan.Zero;

        /// <summary>
        /// How often the sweep looks. Deliberately much shorter than any idle
        /// setting worth having: the cost is a dictionary walk over at most
        /// <see cref="MaxOpenFiles"/> entries, and a coarse tick would round a
        /// two-minute setting up to something the user did not ask for.
        ///
        /// Only while there is work: see <see cref="WorkTimer"/>. Settable for
        /// tests; read each time a timer is started.
        /// </summary>
        internal static TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

        private readonly WorkTimer _sweep;

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
        /// track — puts a path on the letter in that application's hands and
        /// nothing else, so the *first byte it reads* is the first byte fetched
        /// from Google. It reads synchronously on the thread that pumps its
        /// window, so the whole download happens with that window frozen: 3.9
        /// seconds for a 16.8MB track, and linear from there — a minute for the
        /// 232MB one. Windows paints "(Not Responding)" over the lot.
        ///
        /// The same file once this has had it: 111ms. The bytes are not slow, the
        /// timing is — so the fix is to spend the seconds somebody takes to
        /// switch windows on the download instead of spending them inside the
        /// reader. A cache leased here and let go without a read is how the
        /// cache knows it was warmed, and that starts its four-stream download.
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

                // The size from the listing: no filesystem call, and a folder
                // (size -1) is never warmed by accident.
                var key = KeyFor(path);
                long size = key == null ? -1 : RowAt(key)?.Size ?? -1;
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
        /// and everything below checks that before building a string: a read
        /// is answered hundreds of times for one big track, on a thread that is
        /// holding up whoever is reading the file.
        /// </summary>
        public Action<string, string>? Notify { get; set; }

        /// <summary>Told (old letter, new letter) when coming back online had to use another letter.</summary>
        public Action<string, string>? OnLetterMoved { get; set; }

        private void Say(string id, string message)
        {
            var notify = Notify;
            if (notify == null) return;

            // Never on the caller's account. A handler that throws inside a
            // read the volume is answering would fail that read.
            try { notify(id, message); } catch { }
        }

        // Which files have had their first and their last byte served. Only used
        // to say each thing once; the ids are dropped when the cache is.
        private readonly ConcurrentDictionary<string, bool> _fetchStarted = new();

        /// <summary>
        /// Transfers that want to know what is being downloaded for them.
        ///
        /// A copy out of the Drive letter is robocopy reading the volume, and
        /// every byte it gets comes through <see cref="ReadFromDrive"/> in this
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
        /// Requests from Windows being answered right now, so the volume is
        /// never taken down underneath one. See <see cref="WaitForCallbacks"/>.
        /// </summary>
        private int _callbacksInFlight;

        /// <summary>
        /// Waits for the volume to go quiet, and says whether it did: requests
        /// still being answered are failed (the mount's closing token) and given
        /// a moment to say so, rather than cut off mid-answer.
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
            /// of a file on the volume.
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

                // The file read from its first byte again after more than a chunk
                // of it had gone: robocopy starting it over after a failed read
                // (4.2.7). Counted afresh, so the window shows the copy going again
                // rather than sitting at 100 percent while it does.
                if (start == 0 && _served.TryGetValue(relative, out var had) && had.Total > RestartAfterBytes)
                {
                    _served[relative] = new Ranges();
                    Interlocked.Increment(ref _restarts);
                }

                _served.GetOrAdd(relative, _ => new Ranges()).Add(start, end);
                Volatile.Write(ref _current, Path.GetFileName(relative));
            }

            /// <summary>How much of a file must have gone before a read from byte zero counts as starting over.</summary>
            internal const long RestartAfterBytes = 4L * 1024 * 1024;

            private int _restarts;

            /// <summary>How many times a file was started over; the window may go back when this moves.</summary>
            public int Restarts => Volatile.Read(ref _restarts);

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

            /// <summary>For the suite, which has no volume to mount.</summary>
            internal static FetchWatch Detached(params string[] prefixes) => new(null, prefixes);

            public void Dispose() => _owner?.Unwatch(this);
        }

        /// <summary>
        /// Starts watching downloads for <paramref name="paths"/>, which may be
        /// on the drive letter or under the volume's own name.
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
        /// Called from a read the volume is answering, so it is cheap: nothing
        /// happens at all unless a transfer is watching. The path is relative to
        /// the volume, in the case the letter shows it.
        /// </summary>
        private void NoteFetch(string relative, long start, long end, long size)
        {
            FetchWatch[] watching;
            lock (_watches) watching = _watches.ToArray();
            foreach (var watch in watching) watch.Note(relative, start, end, size);
        }
        private readonly ConcurrentDictionary<string, bool> _fetchFinished = new();

        private readonly ConcurrentDictionary<string, bool> _populated = new();

        /// <summary>
        /// How long a listing somebody is waiting on — a folder opened through
        /// the letter, or navigated to in the file manager — may wait on Google
        /// before it gives up and fails.
        ///
        /// A request that never returns is worse than one that fails: a failed
        /// listing is an ordinary error, which File Explorer shows and robocopy
        /// retries, and every request queued behind it gets its turn. Found with
        /// the player silent for good, its reads queued behind the only request
        /// being answered, which was parked on a folder listing.
        /// </summary>
        private static readonly TimeSpan ListingDeadline = TimeSpan.FromSeconds(45);

        /// <summary>How long one piece (PieceBytes) of a data callback may take.</summary>
        private static readonly TimeSpan DataDeadline = TimeSpan.FromSeconds(90);

        /// <summary>The most one transfer to Windows holds; see OnFetchData.</summary>
        private const long PieceBytes = 256 * 1024;

        /// <summary>Every data callback, start and end, in the Drive log when one is being kept.</summary>
        private static readonly bool TraceFetches =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EXPLORERNATIVE_DRIVELOG"));

        private List<DriveEntry> ListFolder(string relative, string folderId, CancellationToken token,
            Action<List<DriveEntry>>? onPage = null, bool interactive = true)
        {
            string? name = Notify == null
                ? null
                : (relative.Length == 0 ? "Google Drive" : Path.GetFileName(relative));

            if (name != null) Say("drive.folder.loading", $"Fetching {name} from Google Drive");

            bool accountRoot = relative.Length == 0 && folderId == "root";
            var sharedDrives = new DriveEntry(DriveClient.SharedDrivesId, DriveClient.SharedDrivesName,
                DriveClient.FolderMimeType, 0, DateTime.UtcNow);
            var trash = new DriveEntry(DriveClient.TrashId, DriveClient.TrashName,
                DriveClient.FolderMimeType, 0, DateTime.UtcNow);
            bool showTrash = accountRoot && ShowTrash();
            var ours = showTrash ? new List<DriveEntry> { sharedDrives, trash } : new List<DriveEntry> { sharedDrives };

            // Ahead of everything else, for the reason given below.
            if (accountRoot) onPage?.Invoke(ours);

            _driveIds.TryGetValue(relative, out var driveId);
            // Said explicitly, never left to ListChildren's guess from the label:
            // a caller deciding whether to create something (an upload's parent,
            // a conflict check) has to wait out a blip, not fail fast and carry on.
            var entries = _drive.ListChildren(folderId, token, label: name, driveId: driveId, onPage: onPage,
                    interactive: interactive, trashed: IsUnderTrash(relative))
                                .GetAwaiter().GetResult();
            Interlocked.Increment(ref Listings);

            // The shared drives, as a folder beside My Drive's own, the way
            // drive.google.com shows them — in every listing of the account root,
            // not only the first: a refresh of the letter that left it out took
            // the folder away, and gave its name to a real folder of the same
            // name, which Delete then sent to the Drive trash. First, so it keeps
            // its name: a My Drive folder called the same is "Shared drives (2)".
            // The trash the same way, right after it, while it is shown.
            if (accountRoot) entries.InsertRange(0, ours);

            if (name != null)
            {
                // Nothing is said for an empty folder: most listings are the
                // app's own (deletes, sizes, re-checks), and "Downloads is
                // empty" out of nowhere is noise (Conner, 4.2.2). Opening an
                // empty folder yourself still says "This folder is empty".
                if (entries.Count > 0) Say("drive.folder.loaded", $"{name}: {NameRules.Items(entries.Count)}");
            }

            return entries;
        }

        /// <summary>
        /// Fills in a directory, once, from Drive: one listing at the moment
        /// somebody actually looks. The volume calls it when Windows opens or
        /// lists a folder it has not seen; the file manager calls it ahead of
        /// navigating, so the listing is paid up front and shown a page at a time.
        /// </summary>
        public bool Populate(string directory, Action<IReadOnlyList<DrivePlaced>>? onPlaced = null,
            bool interactive = true)
        {
            string? relative = KeyFor(directory);
            return relative != null && PopulateKey(relative, onPlaced, interactive);
        }

        /// <summary>
        /// <see cref="Populate"/> for a key: the volume's own route in, which
        /// already has one. True when the listing put anything in it.
        /// </summary>
        internal bool PopulateKey(string relative, Action<IReadOnlyList<DrivePlaced>>? onPlaced = null,
            bool interactive = true)
        {

            // Back online since something was answered from an old listing: those
            // folders are listed again rather than trusted for the session.
            if (!_answeredOffline.IsEmpty && !_drive.Health.IsOffline) DropOfflineAnswers();

            // Somebody waiting on the screen gets the listing deadline; a
            // caller about to create or merge on the strength of the answer gets
            // long enough for the patient client to wait out a blip.
            var allowed = interactive ? ListingDeadline : PatientDeadline;
            // Claimed first, so two navigations into one folder do not both pay
            // for the listing — and **given back on every way out that did not
            // actually populate it**, which it was not. A folder whose listing
            // threw (a blip, an expired sign-in, the deadline) stayed marked, so
            // this path never looked at it again for the rest of the session: the
            // application's own navigation stopped paying the listing up front
            // and left the folder to be listed when Windows asked, one round
            // trip later, every single time.
            //
            // And a second caller while the first is still listing waits for it.
            // It used to return at once, and carry on as though the folder had
            // been listed: an upload making two sibling folders looked for the
            // second in a parent whose listing had not arrived, did not find it,
            // and made another in Drive.
            //
            // A patient caller whose wait ends with the folder still unlisted —
            // the listing it waited on was an interactive one that gave up on a
            // blip — takes the claim and lists it itself, in the time it has
            // left, rather than giving up on a listing it was meant to wait for.
            var mine = new ManualResetEventSlim(false);
            var claimClock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var running = _populating.GetOrAdd(relative, mine);
                if (ReferenceEquals(running, mine)) break;

                var waitFor = allowed - claimClock.Elapsed;
                if (waitFor > TimeSpan.Zero)
                {
                    try { running.Wait(waitFor, _closing.Token); } catch { }
                }
                if (_closing.IsCancellationRequested || interactive || _populated.ContainsKey(relative) ||
                    allowed - claimClock.Elapsed < TimeSpan.FromSeconds(5))
                    return false;
            }
            // Whatever the wait used is not given again to the listing.
            var listFor = allowed - claimClock.Elapsed;

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

            // Offline, with this folder listed before: the listing held is the
            // answer. Asking waited out the whole deadline — three quarters of a
            // minute on every folder opened — to fail and show nothing, when
            // the last listing is still held. Not
            // stamped as listed, so anything wanting a fresh listing still asks
            // once Drive is back — and remembered, so that coming back online
            // drops it (DropOfflineAnswers). Not marked fully populated either:
            // that kept a folder opened during a blip stale for the session.
            //
            // Only for somebody waiting on the screen, and only for a folder
            // once listed whole: a patient caller about to create on the
            // answer waits the blip out instead (a stale listing missed a
            // folder made on the web and made a second one), and the single
            // row PlaceOne merges into a folder never listed is not its contents.
            if (interactive && _drive.Health.IsOffline && _listedAt.ContainsKey(relative) &&
                _listings.TryGetValue(relative, out var held))
            {
                _log($"  populate '{relative}': offline, {held.Length} entries from the last listing");
                if (held.Length > 0 && onPlaced != null)
                {
                    try { onPlaced(held); } catch { }
                }
                _answeredOffline[relative] = true;
                populated = true;
                return false;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();

            // A deadline of its own. Without one this waited out an offline spell
            // for as long as it lasted — holding a navigation, a paste's conflict
            // check or a search, with nothing able to call it off.
            // Linked to the unmount, so Quit and Disconnect do not wait out a listing.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            deadline.CancelAfter(listFor < TimeSpan.Zero ? TimeSpan.Zero : listFor);

            // A page at a time, kept and handed on as it arrives. A folder of a
            // few thousand is several pages at more than a second each, and the
            // window showed nothing until the last of them had been listed; now
            // the first thousand rows are on screen while the rest are still
            // coming.
            var session = new PlacementSession(this, relative) { FolderId = folderId };
            int placedCount = 0;

            void Place(List<DriveEntry> page)
            {
                var placedPage = new List<DrivePlaced>(page.Count);
                BuildEntries(relative, page, session, placedPage);
                placedCount += placedPage.Count;
                if (placedPage.Count > 0 && onPlaced != null)
                {
                    try { onPlaced(placedPage); } catch { }
                }
            }

            ListFolder(relative, folderId, deadline.Token, Place, interactive);
            _log($"  populate '{relative}': {placedCount} entries in {clock.ElapsedMilliseconds}ms");

            // The whole listing, now that it is whole, replaces what was there.
            CommitListing(relative, session);
            if (session.Stale)
            {
                // Renamed, moved or removed meanwhile: nothing of it was kept,
                // and the folder is listed again wherever it is now.
                _log($"  populate '{relative}': the folder moved while it was listed; not kept");
                return false;
            }

            // Listed. An empty folder is a listed folder too.
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

        /// <summary>
        /// Listings in progress — the file manager's and the volume's alike —
        /// for anyone who arrives while one is running, and for MoveLocal, which
        /// must not re-key a folder a listing is still writing under.
        /// </summary>
        private readonly ConcurrentDictionary<string, ManualResetEventSlim> _populating = new();

        /// <summary>
        /// How long a patient Populate (an upload's parent, a conflict check) may
        /// take: the patient client's two minutes, so its outage wait is not cut
        /// off by the interactive deadline.
        /// </summary>
        private static readonly TimeSpan PatientDeadline = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Folders answered from an old listing while Drive was offline. Dropped
        /// from <see cref="_populated"/> once Drive answers again, so a folder
        /// opened during a blip is listed afresh rather than stale all session.
        /// </summary>
        private readonly ConcurrentDictionary<string, bool> _answeredOffline = new();


        /// <summary>How long a listing is trusted for an outside program's enumeration.</summary>
        internal static TimeSpan FreshFor = TimeSpan.FromSeconds(30);

        private void DropOfflineAnswers()
        {
            foreach (var key in _answeredOffline.Keys)
                if (_answeredOffline.TryRemove(key, out _))
                    _populated.TryRemove(key, out _);
        }

        /// <summary>
        /// A change of Drive's state. Only a wake-up: what is done follows the
        /// state as it is now, since events can arrive in the other order.
        /// </summary>
        private void OnHealthChanged(DriveState state)
        {
            if (_drive.Health.State == DriveState.Online && !_answeredOffline.IsEmpty) DropOfflineAnswers();
            LetterFollows();
        }

        /// <summary>Whether a folder's listing is recent enough to be served without asking Drive again.</summary>
        private bool ListedRecently(string relative) =>
            _listedAt.TryGetValue(relative, out var at) &&
            Environment.TickCount64 - at <= FreshFor.TotalMilliseconds;


        /// <summary>
        /// Lists the root, creates the volume and puts it on a letter.
        /// <paramref name="assignLetter"/> false leaves it on no letter at all,
        /// for the suite's tests of the maps alone.
        /// </summary>
        public void Start(string rootFolderId = "root", bool assignLetter = true, char preferred = 'G',
            CancellationToken token = default)
        {
            _folders[""] = rootFolderId;
            PopulateRoot(rootFolderId, token);

            if (!assignLetter) return;

            _volume = new Volume(this);
            var host = new WinFsp.Host(_volume);
            try
            {
                // Two Explorer Native windows signed in to one Drive (two
                // sessions on one PC) would collide on the name; the second
                // steps aside.
                // Unguarded: creates, deletes and renames wait on Google, and
                // WinFsp's guard made every other open on the drive wait with
                // them. The name gates keep the order that matters instead.
                try { host.Create(Prefix, "GoogleDrive", FileInfoTimeoutMs, DispatcherThreads, unguarded: true); }
                catch (InvalidOperationException) when (!Prefix.EndsWith("-" + Environment.ProcessId))
                {
                    host.Dispose();
                    host = new WinFsp.Host(_volume);
                    host.Create(Prefix + "-" + Environment.ProcessId, "GoogleDrive", FileInfoTimeoutMs, DispatcherThreads,
                        unguarded: true);
                }

                lock (_letterGate)
                {
                    _host = host;
                    _letterName = DriveLetter.Take(host, preferred, Notify);
                }
            }
            catch
            {
                lock (_letterGate) { _host = null; _letterName = null; }
                host.Dispose();
                _volume = null;
                throw;
            }

            _log($"drive letter {_letterName} -> {Root}");
            StartUploads();
            _probe.Start();
        }

        /// <summary>
        /// The volume's \server\share name, as WinFsp takes it: one backslash in
        /// front. Root is the same with two, as Windows writes a UNC path.
        /// </summary>
        private string Prefix => Root.StartsWith(@"\\", StringComparison.Ordinal) ? Root[1..] : Root;

        /// <summary>
        /// How long Windows may keep what it was told about a file before asking
        /// again. Short, because Drive changes from elsewhere; anything this
        /// mount learns of sooner is told to Windows directly (Changed).
        /// </summary>
        private const uint FileInfoTimeoutMs = 1000;

        /// <summary>
        /// Requests answered at once. A read waiting on Google holds one, so
        /// enough that a copy, a player and File Explorer browsing do not queue
        /// behind each other.
        /// </summary>
        private const uint DispatcherThreads = 16;

        /// <summary>
        /// The top level, listed up front: the letter opens on it, and a mount
        /// whose root cannot be listed is not worth putting on a letter.
        /// </summary>
        private void PopulateRoot(string folderId, CancellationToken token)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Called off with the mount, and not waited on for ever: an outage
            // here otherwise held the mount up for as long as it lasted.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));

            var session = new PlacementSession(this, "");
            var placedPage = new List<DrivePlaced>();
            var entries = ListFolder("", folderId, deadline.Token, interactive: false);
            BuildEntries("", entries, session, placedPage);
            CommitListing("", session);
            _populated[""] = true;
            _log($"root: {placedPage.Count} entries in {clock.ElapsedMilliseconds}ms");
        }

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

            /// <summary>
            /// Kept names given up because the file was renamed in Drive: by id,
            /// the old name, which the letter no longer shows.
            /// </summary>
            public readonly Dictionary<string, string> Abandoned = new(StringComparer.Ordinal);

            /// <summary>
            /// The rows the folder's listing held when this started, by instance:
            /// a row that is not one of these when the listing is committed was
            /// added meanwhile — an upload's PlaceOne — and is not this listing's
            /// to drop.
            /// </summary>
            public readonly HashSet<DrivePlaced> Before = new(ReferenceEqualityComparer.Instance);

            /// <summary>The Drive id of the folder being listed: what its key must still name for the listing to be kept.</summary>
            public string? FolderId;

            /// <summary>The folder was renamed, moved or removed while it was listed: nothing more of this listing is written.</summary>
            public bool Stale;

            public PlacementSession(DriveMount mount, string relative)
            {
                // A file saved here and not yet on Drive has its name: nothing the
                // listing brings may be given it.
                foreach (var copy in mount.PendingIn(relative))
                    if (copy.Id == null) Used.Add(copy.Name);

                // Every name the last listing bound to a file is kept for that
                // file, whether or not it turns up again, so a page cannot hand
                // the name to something else before the file it belongs to
                // arrives.
                if (!mount._listings.TryGetValue(relative, out var before)) return;
                foreach (var item in before) Before.Add(item);
                foreach (var item in before)
                {
                    var key = (relative.Length == 0 ? item.Name : relative + "\\" + item.Name).ToLowerInvariant();
                    if (mount._ids.TryGetValue(key, out var boundId) &&
                        !Kept.ContainsKey(boundId) && Used.Add(item.Name))
                        Kept[boundId] = item.Name;
                }
            }
        }

        /// <summary>
        /// One page of a folder's listing, named and bound: every entry given its
        /// name on the letter, its id, folder id and drive put in the maps, and
        /// its row added to the session.
        /// </summary>
        private void BuildEntries(string relative, List<DriveEntry> entries, PlacementSession session,
            List<DrivePlaced> placedPage)
        {
            // Under _treeGate, so a rename or removal of the folder (which re-keys
            // or drops its maps under the same lock) comes wholly before or after
            // a page, never in the middle of one.
            lock (_treeGate)
            {
                if (!StillListing(relative, session)) return;
                BuildEntriesHeld(relative, entries, session, placedPage);
            }
        }

        /// <summary>
        /// Under _treeGate: whether the folder a listing was started for is
        /// still at its key. A listing kept the key it started with, so a
        /// folder renamed on the letter while it was listed had its files bound
        /// under the old name; a new folder made there then showed the renamed
        /// folder's files, and deleting one trashed the real file.
        /// </summary>
        private bool StillListing(string relative, PlacementSession session)
        {
            if (session.Stale) return false;
            if (relative.Length == 0 || session.FolderId == null) return true;
            if (_folders.TryGetValue(relative, out var id) && id == session.FolderId) return true;
            session.Stale = true;
            return false;
        }

        private void BuildEntriesHeld(string relative, List<DriveEntry> entries, PlacementSession session,
            List<DrivePlaced> placedPage)
        {
            var used = session.Used;
            foreach (var entry in entries)
            {
                session.Seen.Add(entry.Id);

                // A Google-native document has no bytes anywhere — not here and
                // not in Drive for desktop. Skipped rather than shown as a file
                // that cannot be opened.
                //
                // Skipped *before* a name is claimed for it, which is the order
                // that matters. Claiming first put the name of something that was
                // then never placed into the set of names already taken, so a real
                // file sharing it — which Drive allows, and which is the only
                // reason this set exists — was renamed to "name (2)" to avoid a
                // collision with an entry that does not exist.
                if (entry.IsGoogleDocument) continue;

                // Drive lets two files in one folder share a name and Windows does
                // not, so a duplicate has to be renamed or it silently replaces
                // the first and the folder comes up short.
                //
                // A kept name is kept only while it is still this file's name,
                // cleaned up or numbered. Kept whatever Drive now called it, a
                // file renamed on the web stayed under its old name on the letter
                // for the whole session, F5 or not.
                string name;
                session.Kept.TryGetValue(entry.Id, out var kept);
                if (kept != null && StillNamed(kept, entry.Name, entry.IsFolder))
                    name = kept;
                else
                {
                    if (kept != null) session.Abandoned[entry.Id] = kept;
                    name = Unique(Sanitise(entry.Name), used, entry.IsFolder);
                }

                Bind(relative, name, entry);
                placedPage.Add(RowFor(name, entry));
            }

            session.Placed.AddRange(placedPage);
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
            if (key != null) ForgetKey(key);
        }

        internal void ForgetKey(string key)
        {
            // Only marked unlisted. The old listing stays, unserved by TryListing
            // — it answers for populated folders alone — because the next listing
            // needs it to keep each name bound to the file it named.
            _populated.TryRemove(key, out _);
            _answeredOffline.TryRemove(key, out _);
        }

        /// <summary>
        /// After a whole listing: what the map still says about files the
        /// listing no longer holds. Their ids kept answering for their paths, so
        /// a folder trashed on the web and then uploaded again by name was
        /// "merged into" — the upload went into the trashed folder.
        ///
        /// Two kinds are kept: a file saved here that is not on Drive yet, and
        /// one this mount uploaded a moment ago, which Google's listing can lag
        /// by a second or two (measured; see CLAUDE.md, Drive monitor).
        /// </summary>
        private void ForgetGone(string relative, PlacementSession session)
        {
            foreach (var (id, name) in session.Kept)
            {
                if (session.Seen.Contains(id) && !session.Abandoned.ContainsKey(id)) continue;

                var key = ChildKey(relative, name);
                if (HasPending(key) || UploadedRecently(id)) continue;

                _ids.TryRemove(new KeyValuePair<string, string>(key, id));
                bool wasFolder = _folders.TryRemove(new KeyValuePair<string, string>(key, id));
                if (wasFolder)
                {
                    _driveIds.TryRemove(key, out _);
                    _listings.TryRemove(key, out _);
                    _listedAt.TryRemove(key, out _);
                    _populated.TryRemove(key, out _);

                    var below = key + "\\";
                    foreach (var k in _ids.Keys) if (k.StartsWith(below, StringComparison.Ordinal)) _ids.TryRemove(k, out _);
                    foreach (var k in _folders.Keys) if (k.StartsWith(below, StringComparison.Ordinal)) _folders.TryRemove(k, out _);
                    foreach (var k in _driveIds.Keys) if (k.StartsWith(below, StringComparison.Ordinal)) _driveIds.TryRemove(k, out _);
                    foreach (var k in _listings.Keys) if (k.StartsWith(below, StringComparison.Ordinal)) _listings.TryRemove(k, out _);
                    foreach (var k in _listedAt.Keys) if (k.StartsWith(below, StringComparison.Ordinal)) _listedAt.TryRemove(k, out _);
                    foreach (var k in _populated.Keys) if (k.StartsWith(below, StringComparison.Ordinal)) _populated.TryRemove(k, out _);
                }
                ReleaseFile(id);
            }
        }

        /// <summary>
        /// A whole listing, now that it is whole, as the folder's listing — plus
        /// any row added while it was being fetched, and every file saved here
        /// that Drive does not have yet. An upload landing in a folder
        /// mid-listing placed its row with PlaceOne, and the listing, started
        /// before the upload finished, then overwrote it: the file was in Drive
        /// and missing from the pane.
        ///
        /// What changed since the last listing is told to Windows, and a file
        /// whose size or date changed loses the download held for its old
        /// contents.
        /// </summary>
        private void CommitListing(string relative, PlacementSession session)
        {
            DrivePlaced[]? previous = null;
            lock (_treeGate)
            {
                if (!StillListing(relative, session)) return;
                previous = CommitListingHeld(relative, session);
            }
            if (previous != null && _listings.TryGetValue(relative, out var after))
                NoteListingChanges(relative, previous, after);
        }

        /// <summary>CommitListing under _treeGate; returns the listing it replaced.</summary>
        private DrivePlaced[]? CommitListingHeld(string relative, PlacementSession session)
        {
            var placed = session.Placed.ToArray();
            DrivePlaced[]? previous = null;
            _listings.AddOrUpdate(relative, Overlay(relative, placed), (_, now) =>
            {
                previous = now;
                var names = new HashSet<string>(placed.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
                List<DrivePlaced>? merged = null;

                foreach (var row in now)
                {
                    if (names.Contains(row.Name)) continue;
                    var key = ChildKey(relative, row.Name);
                    _ids.TryGetValue(key, out var id);

                    bool keep =
                        // Saved here, and not (yet) in what Google listed.
                        HasPending(key) ||
                        // Uploaded a moment ago; the listing lags it.
                        (id != null && UploadedRecently(id) && !session.Seen.Contains(id)) ||
                        // Added while the listing was being fetched, and not
                        // returned by it under another name.
                        (!session.Before.Contains(row) && !(id != null && session.Seen.Contains(id)));
                    if (!keep) continue;

                    (merged ??= new List<DrivePlaced>(placed)).Add(row);
                    names.Add(row.Name);
                }

                return Overlay(relative, merged?.ToArray() ?? placed);
            });
            _listedAt[relative] = Environment.TickCount64;
            _answeredOffline.TryRemove(relative, out _);
            ForgetGone(relative, session);
            return previous;
        }

        /// <summary>
        /// What one folder's listing gained, lost and changed, told to Windows,
        /// with downloads of changed files let go.
        /// </summary>
        private void NoteListingChanges(string relative, DrivePlaced[] before, DrivePlaced[] after)
        {
            var was = new Dictionary<string, DrivePlaced>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in before) was.TryAdd(row.Name, row);
            var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in after)
            {
                now.Add(row.Name);
                if (!was.TryGetValue(row.Name, out var old))
                {
                    Changed(relative, row.Name, row.IsFolder, WinFsp.ActionAdded);
                    continue;
                }
                if (row.IsFolder) continue;
                if (old.Size != row.Size || Math.Abs((old.Modified - row.Modified).TotalSeconds) >= 1)
                {
                    if (_ids.TryGetValue(ChildKey(relative, row.Name), out var id) && !HasPending(ChildKey(relative, row.Name)))
                        ReleaseFile(id);
                    Changed(relative, row.Name, false, WinFsp.ActionModified);
                }
            }

            foreach (var row in before)
                if (!now.Contains(row.Name)) Changed(relative, row.Name, row.IsFolder, WinFsp.ActionRemoved);
        }

        /// <summary>
        /// Whether a name on disk is still a form of what Drive calls the file:
        /// the name as Sanitise leaves it, or that numbered by Unique. Case is
        /// not a difference worth a new name, since Windows would refuse the
        /// two side by side anyway.
        /// </summary>
        internal static bool StillNamed(string onDisk, string driveName, bool folder)
        {
            var clean = Sanitise(driveName);
            if (onDisk.Equals(clean, StringComparison.OrdinalIgnoreCase)) return true;

            // Split the way NameRules.UniqueAmong splits it.
            int dot = clean.LastIndexOf('.');
            bool whole = folder || dot <= 0;
            var stem = whole ? clean : clean[..dot];
            var extension = whole ? "" : clean[dot..];

            if (onDisk.Length <= stem.Length + extension.Length + 3) return false;
            if (!onDisk.StartsWith(stem + " (", StringComparison.OrdinalIgnoreCase)) return false;
            if (!onDisk.EndsWith(")" + extension, StringComparison.OrdinalIgnoreCase)) return false;

            var number = onDisk[(stem.Length + 2)..(onDisk.Length - extension.Length - 1)];
            return number.Length > 0 && number.All(char.IsAsciiDigit) && number[0] != '0';
        }



        /// <summary>Reads answered with a failure.</summary>
        public long FailedReads;

        /// <summary>
        /// A full path as this mount's key for it: through the letter ("G:\music",
        /// also while the letter is hidden offline) or the volume's own name
        /// ("\\GoogleDrive\share\music"), lower case and relative to the root.
        /// Anchored at the start and on a path boundary either way: this map is
        /// what turns a path into a Drive id, so a wrong answer here deletes the
        /// wrong file.
        /// </summary>
        private string Relative(string fullPath)
        {
            if (fullPath.Length == 0) return NotOnThisMount;

            var letter = _letterName;
            if (letter != null && fullPath.StartsWith(letter, StringComparison.OrdinalIgnoreCase) &&
                EndsOnABoundary(fullPath, letter.Length))
                return fullPath[letter.Length..].Trim('\\').ToLowerInvariant();

            if (IsAtOrUnder(fullPath, Root))
                return fullPath[Root.TrimEnd('\\').Length..].Trim('\\').ToLowerInvariant();

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
            // correctly leaves nothing at all — and an empty name is a file
            // that silently never appears, which is the
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
        /// only under the concurrency this is guaranteed to see — reads arrive
        /// on the volume's dispatcher threads, several at once.
        ///
        /// The factory overload may run the factory more than once for one key
        /// and keep only one result. A DriveFileCache used to start downloading
        /// in its constructor, so every discarded one carried on pulling the file down
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
                    // Somebody else got there first. Ours is stopped before its
                    // lease ends, or ending it would start a download (EndLease
                    // warms a cache nobody has read) only to cancel it.
                    fresh.Abandon();
                    fresh.EndLease();
                    continue;
                }

                // Unmounted between the check at the top and the add: Dispose's
                // ReleaseAllBut may already have been through the table, and a
                // warmed cache left in it downloads on with nothing to stop it.
                // Checked after the add, so either this sees the flag or that
                // sweep sees the cache.
                if (_disposed)
                {
                    _caches.TryRemove(new KeyValuePair<string, DriveFileCache>(fileId, fresh));
                    fresh.Dispose();
                    fresh.EndLease();
                    throw new ObjectDisposedException(nameof(DriveMount));
                }

                // After the add, so a sweep stopping itself either sees this
                // cache or is started again here. With idle release off there
                // is nothing to sweep for; switching it on arms it (IdleRelease).
                if (IdleRelease > TimeSpan.Zero) _sweep.Arm();
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
        /// them under the new name.
        /// </summary>
        internal void ReleaseFile(string id)
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
        /// Cancelled when the mount is going: every read still being answered
        /// fails at once rather than running out its deadline.
        /// </summary>
        private readonly CancellationTokenSource _closing = new();

        /// <summary>How long going away waits for files still only in memory to reach Google. Settable for tests.</summary>
        internal static TimeSpan DrainFor = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Set before <see cref="Dispose"/> when the person signed in again as a
        /// different Google account: what this mount still holds belongs to the
        /// old one, so it is kept on the PC (<see cref="UnsavedFolder"/>) instead
        /// of being sent.
        /// </summary>
        internal volatile bool AccountGone;

        /// <summary>Stops sending and sets aside every file not yet on Drive, saying so.</summary>
        private void KeepWhatCannotBeSent()
        {
            StopUploads(TimeSpan.FromSeconds(5));
            foreach (var p in _pending.Values.ToList())
            {
                bool keep;
                lock (p.Gate) keep = p.Dirty && !p.Deleted && !p.Retired;
                if (!keep) continue;
                try { Refuse(p, "you signed in to Google Drive as a different account", WinFsp.AccessDenied); }
                catch (Exception ex) { _log($"  could not set {p.Name} aside: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Takes the letter away, sends what programs saved and closed that is
        /// still only in memory, stops answering, and lets every download go.
        /// </summary>
        public void Dispose()
        {
            // The letter first, so nothing new arrives; then every closed file
            // still in memory goes up — there is no disk to keep it on.
            _stopping = true;
            bool gone = AccountGone;
            if (!gone) ChooseWhatDrains();
            WinFsp.Host? host;
            lock (_letterGate) { host = _host; _host = null; }
            try { host?.Unmount(); } catch { }
            if (gone) KeepWhatCannotBeSent();
            else DrainUploads(DrainFor);

            // Nothing new is opened from here on: a warm arriving from a copy
            // made a moment ago would start downloads nobody would ever stop.
            _disposed = true;

            try { _sweep.Dispose(); } catch { }
            try { _probe.Dispose(); } catch { }
            try { _drive.Health.Changed -= OnHealthChanged; } catch { }
            try { _drive.TrashEdited -= OnTrashEdited; } catch { }

            // Every request still being answered is failed rather than left to
            // run out its deadline.
            try { _closing.Cancel(); } catch { }
            WaitForCallbacks(TimeSpan.FromSeconds(5));

            StopUploads(TimeSpan.FromSeconds(5));
            try { host?.Dispose(); } catch { }

            ReleaseAllBut(null);
        }
    }
}
