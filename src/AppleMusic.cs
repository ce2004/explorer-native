using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Apple Music as a place in the panes: a row under the drives, folders for
    /// the library, search, stations and recently played, and songs that play
    /// in the app's own player straight from Apple, unlocked in memory and
    /// never written to disk.
    ///
    /// Every row has a path under "::applemusic", so the panes' ordinary
    /// navigation (Enter, Backspace, landing on the row you came from) works on
    /// it. What each path stands for is kept here, in memory, for the session.
    /// Songs' paths end in ".m4a" so the player takes them as audio.
    /// </summary>
    internal static class AppleMusic
    {
        public const string Root = "::applemusic";
        public const string RootName = "Apple Music";
        public const string SearchFolder = Root + "\\Search";

        private static readonly object Gate = new();
        private static AppleMusicClient? _client;
        private static string _token = "";

        /// <summary>What each path stands for.</summary>
        private static readonly Dictionary<string, AppleMusicItem> Items = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Each folder's rows, as last listed, in order.</summary>
        private static readonly Dictionary<string, List<Row>> Listings = new(StringComparer.OrdinalIgnoreCase);

        internal readonly record struct Row(string Path, string Name, bool IsFolder, string Detail);

        public static bool Is(string? path) =>
            path != null && (path.Equals(Root, StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase));

        /// <summary>The path as shown and said: "Apple Music\Library\Albums".</summary>
        public static string DisplayPath(string path) =>
            Is(path) ? RootName + path.Substring(Root.Length) : path;

        /// <summary>True once a sign-in has been set up in Preferences.</summary>
        public static bool IsSetUp
        {
            get { lock (Gate) return _token.Length > 0; }
        }

        /// <summary>From settings, at start and whenever Preferences are saved.</summary>
        public static void Configure(string userToken)
        {
            lock (Gate)
            {
                userToken ??= "";
                if (userToken == _token) return;
                _token = userToken;
                _client = userToken.Length > 0 ? new AppleMusicClient(userToken) : null;
                Items.Clear();
                Listings.Clear();
                Displayed.Clear();
            }
        }

        private static AppleMusicClient Client()
        {
            lock (Gate)
                return _client ?? throw new AppleMusicException(
                    "Apple Music isn't set up. Add your sign-in in Preferences, Apple Music.", signIn: true);
        }

        /// <summary>The song or container a path stands for, or null.</summary>
        public static AppleMusicItem? ItemAt(string path)
        {
            lock (Gate) return Items.TryGetValue(path, out var item) ? item : null;
        }

        /// <summary>"Title by Artist", what is said for a song that is playing.</summary>
        public static string? SpokenName(string path)
        {
            var item = ItemAt(path);
            if (item == null) return null;
            return item.Subtitle.Length > 0 ? $"{item.Name} by {item.Subtitle}" : item.Name;
        }

        /// <summary>Forget a folder's listing, so the next visit asks Apple again (F5).</summary>
        public static void Forget(string folder)
        {
            lock (Gate) Listings.Remove(folder);
        }

        // ---- Folders ----

        private static readonly (string Name, string Path)[] TopFolders =
        {
            ("Search", "/search"),
            ("Library", ""),
            ("Recently played", "/v1/me/recent/played?limit=10"),
            ("Stations", "/stations"),
        };

        /// <summary>
        /// Songs and albums are asked for newest-added first: songs carry no
        /// date of their own, so that order is the only record of it.
        /// </summary>
        private static readonly (string Name, string Path)[] LibraryFolders =
        {
            ("Recently added", "/v1/me/library/recently-added?limit=10"),
            ("Playlists", "/v1/me/library/playlists?limit=100"),
            ("Albums", "/v1/me/library/albums?limit=100&sort=-dateAdded"),
            ("Artists", "/v1/me/library/artists?limit=100"),
            ("Songs", "/v1/me/library/songs?limit=100&sort=-dateAdded"),
        };

        private static readonly string[] SearchKinds = { "Songs", "Albums", "Artists", "Playlists", "Stations" };

        /// <summary>The path for a search. Opening it asks Apple.</summary>
        public static string SearchPath(string term) => SearchFolder + "\\" + Safe(term.Trim());

        /// <summary>
        /// The rows of a folder. From memory when it has been listed this session
        /// (unless <paramref name="fresh"/>), otherwise from Apple. Throws
        /// AppleMusicException with a sentence to say.
        /// </summary>
        public static async Task<List<Row>> ListAsync(string folder, bool fresh, CancellationToken token)
        {
            folder = folder.TrimEnd('\\');
            if (!fresh)
                lock (Gate)
                    if (Listings.TryGetValue(folder, out var known)) return known;

            var rows = await BuildAsync(folder, token).ConfigureAwait(false);
            lock (Gate) Listings[folder] = rows;
            return rows;
        }

        private static async Task<List<Row>> BuildAsync(string folder, CancellationToken token)
        {
            if (folder.Equals(Root, StringComparison.OrdinalIgnoreCase))
                return TopFolders.Select(f => new Row(Root + "\\" + f.Name, f.Name, true, "")).ToList();

            var rel = folder.Substring(Root.Length + 1);
            var client = Client();

            if (rel.Equals("Library", StringComparison.OrdinalIgnoreCase))
                return LibraryFolders.Select(f => new Row(folder + "\\" + f.Name, f.Name, true, "")).ToList();

            if (rel.Equals("Search", StringComparison.OrdinalIgnoreCase))
            {
                // Earlier searches this session, newest first.
                lock (Gate)
                    return Listings.Keys
                        .Where(k => k.StartsWith(SearchFolder + "\\", StringComparison.OrdinalIgnoreCase) &&
                                    k.IndexOf('\\', SearchFolder.Length + 1) < 0)
                        .Reverse()
                        .Select(k => new Row(k, k.Substring(SearchFolder.Length + 1), true, ""))
                        .ToList();
            }

            var parts = rel.Split('\\');

            // A search: one folder per kind of result that has any.
            if (parts[0].Equals("Search", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
            {
                var term = parts[1];
                var found = await client.ListAsync(
                    "/v1/catalog/{sf}/search?term=" + Uri.EscapeDataString(term) +
                    "&types=songs,albums,artists,playlists,stations&limit=25", token, most: 125).ConfigureAwait(false);
                var rows = new List<Row>();
                foreach (var kind in SearchKinds)
                {
                    string k = Singular(kind);
                    var these = found.Where(i => i.Kind == k).ToList();
                    if (these.Count == 0) continue;
                    var sub = folder + "\\" + kind;
                    lock (Gate) Listings[sub] = RowsFor(sub, these, sort: false);
                    rows.Add(new Row(sub, kind, true, Count(these.Count, k)));
                }
                return rows;
            }

            if (parts[0].Equals("Library", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
            {
                var top = LibraryFolders.FirstOrDefault(f => f.Name.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
                if (top.Path != null)
                {
                    var items = top.Path.Contains("recently-added", StringComparison.Ordinal)
                        ? await client.ListAsync(top.Path, token, most: 100).ConfigureAwait(false)
                        : await client.ListAllAsync(top.Path, token).ConfigureAwait(false);
                    if (top.Path.Contains("dateAdded", StringComparison.Ordinal))
                        for (int i = 0; i < items.Count; i++) items[i].AddedRank = i;
                    return RowsFor(folder, items, sort: false);
                }
            }

            if (parts.Length == 1)
            {
                var top = TopFolders.FirstOrDefault(f => f.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
                if (top.Name != null && top.Path == "/stations")
                    return RowsFor(folder, await client.StationsAsync(token).ConfigureAwait(false), sort: false);
                if (top.Name != null && top.Path.StartsWith("/v1/", StringComparison.Ordinal))
                    return RowsFor(folder, await client.ListAsync(top.Path, token, most: 50).ConfigureAwait(false), sort: false);
            }

            // An album, playlist, artist or station.
            var item = ItemAt(folder) ?? throw new AppleMusicException("That folder isn't there any more. Go back and open it again.");
            if (item.Kind == "station")
                return RowsFor(folder, await client.StationTracksAsync(item, token).ConfigureAwait(false), sort: false, numbered: false);
            var path = client.ChildrenPath(item);
            if (path.Length == 0) throw new AppleMusicException("That can't be opened.");
            var children = await client.ListAllAsync(path, token).ConfigureAwait(false);
            return RowsFor(folder, children, sort: false, numbered: item.Kind == "album");
        }

        /// <summary>A station's next handful, added to the end of its folder. For playing on past the end.</summary>
        public static async Task<List<Row>> MoreFromStationAsync(string folder, CancellationToken token)
        {
            var item = ItemAt(folder);
            if (item == null || item.Kind != "station") return new List<Row>();
            var more = await Client().StationTracksAsync(item, token).ConfigureAwait(false);
            lock (Gate)
            {
                var have = Listings.TryGetValue(folder, out var known) ? known : new List<Row>();
                var added = RowsFor(folder, more, sort: false, numbered: false, startAt: have.Count, taken: have);
                Listings[folder] = have.Concat(added).ToList();
                return added;
            }
        }

        private static List<Row> RowsFor(string folder, List<AppleMusicItem> items, bool sort, bool numbered = false,
            int startAt = 0, List<Row>? taken = null)
        {
            if (sort) items = items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (taken != null) foreach (var r in taken) used.Add(r.Path);
            var rows = new List<Row>(items.Count);
            lock (Gate)
            {
                foreach (var item in items)
                {
                    string name = item.IsSong ? SongName(item, folder)
                        : item.Kind == "album" && item.Subtitle.Length > 0 && ItemAt(folder)?.Kind != "artist"
                            ? item.Name + ", " + item.Subtitle
                            : item.Name;
                    if (item.Kind == "station" && item.IsLive) name += ", live";
                    string leaf = Safe(item.IsSong ? item.Name : item.Name);
                    string path = folder + "\\" + leaf + (item.IsSong ? ".m4a" : "");
                    for (int n = 2; used.Contains(path); n++)
                        path = folder + "\\" + leaf + $" ({n})" + (item.IsSong ? ".m4a" : "");
                    used.Add(path);
                    Items[path] = item;
                    string detail = item.IsSong && item.DurationMs > 0 ? Duration(item.DurationMs)
                        : item.Kind is "album" or "playlist" && item.Subtitle.Length > 0 ? item.Subtitle : "";
                    rows.Add(new Row(path, name, !item.IsSong, detail));
                }
            }
            return rows;
        }

        /// <summary>
        /// A song as a row: just the title inside an album (the artist is the
        /// album's), title and artist everywhere else.
        /// </summary>
        private static string SongName(AppleMusicItem song, string folder)
        {
            var parent = ItemAt(folder);
            if (parent?.Kind == "album" || song.Subtitle.Length == 0) return song.Name;
            return song.Name + ", " + song.Subtitle;
        }

        internal static string Duration(long ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        private static string Singular(string kind) => kind.TrimEnd('s').ToLowerInvariant();

        private static string Count(int n, string kind) => n == 1 ? "1 " + kind : $"{n} {kind}s";

        /// <summary>A name that is safe as one part of a path.</summary>
        internal static string Safe(string name)
        {
            var s = name.Replace('\\', '-').Replace('/', '-').Trim();
            return s.Length == 0 ? "Untitled" : s;
        }

        // ---- Sorting ----

        /// <summary>
        /// The orders an Apple Music folder can be put in, from File, Sort this
        /// folder by. Which ones a folder offers depends on what is in it.
        /// </summary>
        internal enum Sort { Apple, Name, RecentlyAdded, RecentlyUpdated, Artist, Album, ReleaseDate, Length }

        /// <summary>The words for an order, as the menu and the announcement say them.</summary>
        internal static string SortName(Sort sort) => sort switch
        {
            Sort.Name => "name",
            Sort.RecentlyAdded => "recently added",
            Sort.RecentlyUpdated => "recently updated",
            Sort.Artist => "artist",
            Sort.Album => "album",
            Sort.ReleaseDate => "release date",
            Sort.Length => "length",
            _ => "Apple's order",
        };

        /// <summary>Dates start newest first, everything else A to Z or shortest first.</summary>
        internal static bool StartsAscending(Sort sort) => sort is not (Sort.RecentlyAdded or Sort.RecentlyUpdated or Sort.ReleaseDate);

        /// <summary>
        /// What Apple's own order is called in this folder ("Track order" in an
        /// album), or null when the folder's own order is by name (the library's
        /// lists, which Apple gives in no useful order of their own).
        /// </summary>
        internal static string? AppleOrderName(string folder)
        {
            var kind = ItemAt(folder)?.Kind;
            if (kind == "album") return "Track order";
            if (kind == "playlist") return "Playlist order";
            if (kind == "station") return "Station order";
            if (kind == "artist") return "Apple's order";
            var rel = folder.Length > Root.Length ? folder.Substring(Root.Length + 1) : "";
            if (rel.StartsWith("Search\\", StringComparison.OrdinalIgnoreCase)) return "Best match";
            if (rel.Equals("Library\\Recently added", StringComparison.OrdinalIgnoreCase)) return "Recently added";
            if (rel.StartsWith("Library\\", StringComparison.OrdinalIgnoreCase)) return null;
            return "Apple's order";
        }

        /// <summary>
        /// The orders this folder offers, for its Sort menu. Empty for folders of
        /// folders that have nothing to sort (the top, Library, Search).
        /// </summary>
        internal static List<Sort> SortsFor(string folder)
        {
            var sorts = new List<Sort>();
            List<Row>? rows;
            lock (Gate) rows = Listings.TryGetValue(folder.TrimEnd('\\'), out var r) ? r : null;
            if (rows == null || rows.Count == 0 || IsFixedFolder(folder)) return sorts;

            var items = rows.Select(row => ItemAt(row.Path)).Where(i => i != null).Select(i => i!).ToList();
            if (items.Count == 0) return sorts;
            bool songs = items.Any(i => i.IsSong);
            bool albums = items.Any(i => i.Kind == "album");
            bool playlists = items.Any(i => i.Kind == "playlist");

            if (AppleOrderName(folder) != null) sorts.Add(Sort.Apple);
            sorts.Add(Sort.Name);
            if (items.Any(i => i.AddedRank >= 0 || i.DateAdded.Length > 0) &&
                !string.Equals(AppleOrderName(folder), "Recently added", StringComparison.Ordinal))
                sorts.Add(Sort.RecentlyAdded);
            if (playlists && items.Any(i => i.LastModified.Length > 0)) sorts.Add(Sort.RecentlyUpdated);
            if ((songs || albums) && items.Any(i => i.Subtitle.Length > 0) && ItemAt(folder)?.Kind != "album")
                sorts.Add(Sort.Artist);
            if (songs && items.Any(i => i.AlbumName.Length > 0) && ItemAt(folder)?.Kind != "album")
                sorts.Add(Sort.Album);
            if ((songs || albums) && items.Any(i => i.ReleaseDate.Length > 0) && ItemAt(folder)?.Kind != "album")
                sorts.Add(Sort.ReleaseDate);
            if (songs && items.Any(i => i.DurationMs > 0)) sorts.Add(Sort.Length);
            return sorts.Count > 1 ? sorts : new List<Sort>();
        }

        /// <summary>The top, Library and Search hold only our own folders, in our own order.</summary>
        private static bool IsFixedFolder(string folder)
        {
            folder = folder.TrimEnd('\\');
            return folder.Equals(Root, StringComparison.OrdinalIgnoreCase) ||
                   folder.Equals(Root + "\\Library", StringComparison.OrdinalIgnoreCase) ||
                   folder.Equals(SearchFolder, StringComparison.OrdinalIgnoreCase) ||
                   (folder.StartsWith(SearchFolder + "\\", StringComparison.OrdinalIgnoreCase) &&
                    folder.IndexOf('\\', SearchFolder.Length + 1) < 0);
        }

        /// <summary>The order a folder starts in when nothing has been chosen for it.</summary>
        internal static (Sort By, bool Ascending) DefaultSort(string folder) =>
            AppleOrderName(folder) != null || IsFixedFolder(folder) ? (Sort.Apple, true) : (Sort.Name, true);

        /// <summary>
        /// The folder's rows in the order given, and remembered as the order on
        /// screen, which is the order an album or playlist plays on in.
        /// </summary>
        internal static List<Row> Ordered(string folder, List<Row> rows, Sort by, bool ascending)
        {
            folder = folder.TrimEnd('\\');
            List<Row> result;
            if (by == Sort.Apple || IsFixedFolder(folder))
            {
                result = new List<Row>(rows);
                if (!ascending && by == Sort.Apple && !IsFixedFolder(folder)) result.Reverse();
            }
            else
            {
                var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < rows.Count; i++) index[rows[i].Path] = i;
                Comparison<Row> compare = (a, b) =>
                {
                    var x = ItemAt(a.Path);
                    var y = ItemAt(b.Path);
                    int c = x == null || y == null ? 0 : Compare(by, x, y);
                    if (!ascending) c = -c;
                    if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                    if (c == 0) c = index[a.Path].CompareTo(index[b.Path]);
                    return c;
                };
                result = new List<Row>(rows);
                result.Sort(compare);
            }
            lock (Gate) Displayed[folder] = result.Select(r => r.Path).ToList();
            return result;
        }

        /// <summary>Ascending meaning: A to Z, oldest first, shortest first.</summary>
        private static int Compare(Sort by, AppleMusicItem x, AppleMusicItem y)
        {
            int Text(string a, string b) =>
                a.Length == 0 && b.Length > 0 ? 1 : b.Length == 0 && a.Length > 0 ? -1
                    : string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
            switch (by)
            {
                case Sort.Name: return Text(x.Name, y.Name);
                case Sort.Artist:
                    int c = Text(x.Subtitle, y.Subtitle);
                    if (c == 0) c = Text(x.AlbumName, y.AlbumName);
                    if (c == 0) c = (x.DiscNumber * 1000 + x.TrackNumber).CompareTo(y.DiscNumber * 1000 + y.TrackNumber);
                    return c;
                case Sort.Album:
                    int d = Text(x.AlbumName, y.AlbumName);
                    if (d == 0) d = (x.DiscNumber * 1000 + x.TrackNumber).CompareTo(y.DiscNumber * 1000 + y.TrackNumber);
                    return d;
                case Sort.ReleaseDate: return string.CompareOrdinal(x.ReleaseDate, y.ReleaseDate);
                case Sort.Length: return x.DurationMs.CompareTo(y.DurationMs);
                case Sort.RecentlyUpdated: return string.CompareOrdinal(x.LastModified, y.LastModified);
                case Sort.RecentlyAdded:
                    // A rank counts down from newest; a date counts up.
                    if (x.AddedRank >= 0 && y.AddedRank >= 0) return y.AddedRank.CompareTo(x.AddedRank);
                    return string.CompareOrdinal(x.DateAdded, y.DateAdded);
                default: return 0;
            }
        }

        /// <summary>Each folder's rows in the order last shown.</summary>
        private static readonly Dictionary<string, List<string>> Displayed = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The song after this one in its folder, as shown, for playing on. Null at the end.</summary>
        public static string? NextSongAfter(string path)
        {
            var folder = Path.GetDirectoryName(path);
            if (folder == null) return null;
            lock (Gate)
            {
                if (Displayed.TryGetValue(folder, out var shown))
                {
                    int i = shown.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                    for (int j = i + 1; i >= 0 && j < shown.Count; j++)
                        if (Items.TryGetValue(shown[j], out var next) && next.IsSong) return shown[j];
                    if (i >= 0) return null;
                }
                if (!Listings.TryGetValue(folder, out var rows)) return null;
                int at = rows.FindIndex(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
                for (int i = at + 1; at >= 0 && i < rows.Count; i++)
                    if (!rows[i].IsFolder) return rows[i].Path;
            }
            return null;
        }

        /// <summary>The song before this one in its folder, as shown. Null at the start.</summary>
        public static string? PreviousSongBefore(string path)
        {
            var folder = Path.GetDirectoryName(path);
            if (folder == null) return null;
            lock (Gate)
            {
                var order = Displayed.TryGetValue(folder, out var shown) ? shown
                    : Listings.TryGetValue(folder, out var rows) ? rows.Select(r => r.Path).ToList() : null;
                if (order == null) return null;
                int i = order.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                for (int j = i - 1; i > 0 && j >= 0; j--)
                    if (Items.TryGetValue(order[j], out var prev) && prev.IsSong) return order[j];
            }
            return null;
        }

        /// <summary>True when the song's folder is a station, which always has more.</summary>
        public static bool InStation(string path)
        {
            var folder = Path.GetDirectoryName(path);
            return folder != null && ItemAt(folder)?.Kind == "station";
        }

        // ---- Playing ----

        /// <summary>
        /// Apple's own catalogue id for a song, which is what plays it in the
        /// MusicKit player (AppleMusicPlayer). Throws AppleMusicException with a
        /// sentence to say when the row is not a playable song.
        /// </summary>
        public static string CatalogIdForPlaying(string path)
        {
            var song = ItemAt(path) ?? throw new AppleMusicException("That song isn't there any more. Open its folder again.");
            if (!song.IsSong) throw new AppleMusicException("That isn't a song.");
            var id = song.CatalogId.Length > 0 ? song.CatalogId : song.Id;
            if (id.Length == 0) throw new AppleMusicException("Apple Music can't play that song.");
            return id;
        }

        /// <summary>The developer token for the MusicKit player, scraped and cached like the browsing one.</summary>
        public static Task<string> DeveloperTokenAsync(CancellationToken token) => Client().DeveloperTokenForPlayerAsync(token);

        /// <summary>The media-user-token set in Preferences, which is also MusicKit's music user token.</summary>
        public static string UserToken { get { lock (Gate) return _token; } }

        // ---- Test seams (no network) ----

        internal static void ResetForTests()
        {
            lock (Gate) { _token = ""; _client = null; Items.Clear(); Listings.Clear(); Displayed.Clear(); }
        }

        /// <summary>Signs in with a fake token so the offline folders (top, Library) list.</summary>
        internal static void ConfigureForTests(string token = "test-token") => Configure(token);

        /// <summary>Puts a folder's rows in memory as if Apple had listed them, for sort and play-on tests.</summary>
        internal static List<Row> SeedForTests(string folder, List<AppleMusicItem> items, bool numbered = false)
        {
            var rows = RowsFor(folder.TrimEnd('\\'), items, sort: false, numbered: numbered);
            lock (Gate) Listings[folder.TrimEnd('\\')] = rows;
            return rows;
        }

        /// <summary>Marks what a folder itself is (an album, an artist), for the row-naming and sort rules.</summary>
        internal static void SetContainerForTests(string folder, AppleMusicItem item)
        {
            lock (Gate) Items[folder.TrimEnd('\\')] = item;
        }

        /// <summary>
        /// The media-user-token in a cookies.txt (or a pasted bare token), or null.
        /// Lets Preferences take the file the person exported for gamdl as-is.
        /// </summary>
        public static string? TokenFromCookiesText(string text) => AppleMusicClient.TokenFromCookies(text);

        /// <summary>
        /// Checks a token with Apple: that it signs in and the subscription is
        /// active. Says the storefront on success. Throws AppleMusicException.
        /// </summary>
        public static async Task<string> CheckSignInAsync(string token, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(token)) throw new AppleMusicException("There's no sign-in to check yet.");
            var client = new AppleMusicClient(token);
            await client.StartAsync(ct).ConfigureAwait(false);
            return $"Signed in to Apple Music ({client.Storefront.ToUpperInvariant()} store).";
        }
    }
}
