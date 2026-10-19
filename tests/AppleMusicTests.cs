using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Apple Music as a place in the panes: the folder tree, the paths, the
    /// cookies.txt sign-in, the smart Sort menu (which orders a folder offers
    /// and what each one does), and play-on from one song to the next. All of
    /// it offline: the network calls (library, search, stations) are proven
    /// against the real account by hand, not here.
    /// </summary>
    internal static class AppleMusicTests
    {
        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            Console.WriteLine("Apple Music:");
            AppleMusic.ResetForTests();
            AppleMusic.ConfigureForTests();

            Paths(check, equal);
            await Folders(check, equal);
            Cookies(check, equal);
            Sorting(check, equal);
            Naming(check, equal);
            PlayOn(check, equal);

            AppleMusic.ResetForTests();
        }

        private static AppleMusicItem Song(string name, string id, string artist = "", string album = "",
            long ms = 0, string release = "", string added = "", int track = 0, int addedRank = -1) =>
            new()
            {
                Kind = "song", Name = name, Id = id, CatalogId = id, Subtitle = artist, AlbumName = album,
                DurationMs = ms, ReleaseDate = release, DateAdded = added, TrackNumber = track, AddedRank = addedRank,
            };

        private static void Paths(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            check("root is Apple Music", AppleMusic.Is("::applemusic"), null);
            check("a song path is Apple Music", AppleMusic.Is("::applemusic\\Library\\Songs\\A.m4a"), null);
            check("a drive is not", !AppleMusic.Is("C:\\Users"), null);
            check("null is not", !AppleMusic.Is(null), null);
            equal("display path", AppleMusic.DisplayPath("::applemusic\\Library\\Albums"), "Apple Music\\Library\\Albums");
            check("search path is under Search", AppleMusic.SearchPath("hello").StartsWith("::applemusic\\Search\\"), null);
            check("search path keeps the words", AppleMusic.SearchPath("daft punk").EndsWith("daft punk"), null);
            check("a slash in a search term can't make a sub-folder",
                !AppleMusic.SearchPath("ac/dc").Substring("::applemusic\\Search\\".Length).Contains('\\'), null);
        }

        private static async Task Folders(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var top = await AppleMusic.ListAsync("::applemusic", fresh: true, CancellationToken.None);
            var names = top.Select(r => r.Name).ToList();
            check("top has Search", names.Contains("Search"), string.Join(", ", names));
            check("top has Library", names.Contains("Library"), null);
            check("top has Stations", names.Contains("Stations"), null);
            check("top rows are folders", top.All(r => r.IsFolder), null);

            var lib = await AppleMusic.ListAsync("::applemusic\\Library", fresh: true, CancellationToken.None);
            var libNames = lib.Select(r => r.Name).ToList();
            check("library has Recently added", libNames.Contains("Recently added"), string.Join(", ", libNames));
            check("library has Playlists, Albums, Artists, Songs",
                new[] { "Playlists", "Albums", "Artists", "Songs" }.All(libNames.Contains), null);
        }

        private static void Cookies(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            // A Netscape cookies.txt line, tab-separated, like browser extensions export.
            var line = "#HttpOnly_.music.apple.com\tTRUE\t/\tTRUE\t0\tmedia-user-token\tAbCdEf123456789secretvalue";
            equal("token read from cookies.txt", AppleMusic.TokenFromCookiesText(line) ?? "(null)", "AbCdEf123456789secretvalue");

            var withOthers = "#\tcomment\n.music.apple.com\tTRUE\t/\tFALSE\t0\tgeo\tus\n" +
                             ".music.apple.com\tTRUE\t/\tTRUE\t0\tmedia-user-token\tTOKEN_HERE_long_enough_value";
            equal("token found among other cookies", AppleMusic.TokenFromCookiesText(withOthers) ?? "(null)", "TOKEN_HERE_long_enough_value");

            check("no token in an unrelated file is null", AppleMusic.TokenFromCookiesText("just some text\nno cookies here") == null, null);
            check("a pasted bare token is taken",
                AppleMusic.TokenFromCookiesText("ArDf09averyLONGtokenvaluewithnospacesorsuch1234567890") == "ArDf09averyLONGtokenvaluewithnospacesorsuch1234567890", null);
        }

        private static void Sorting(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            const string folder = "::applemusic\\Library\\Songs";
            var items = new List<AppleMusicItem>
            {
                Song("Bravo", "2", artist: "Zeta", album: "Mango", ms: 200000, release: "2020-01-01", addedRank: 1),
                Song("alpha", "1", artist: "Alpha", album: "Nectar", ms: 100000, release: "2024-06-01", addedRank: 0),
                Song("Charlie", "3", artist: "Mike", album: "Apple", ms: 300000, release: "2010-03-03", addedRank: 2),
            };
            var rows = AppleMusic.SeedForTests(folder, items);

            var sorts = AppleMusic.SortsFor(folder);
            check("songs offer a name sort", sorts.Contains(AppleMusic.Sort.Name), string.Join(",", sorts));
            check("songs offer recently added", sorts.Contains(AppleMusic.Sort.RecentlyAdded), null);
            check("songs offer artist", sorts.Contains(AppleMusic.Sort.Artist), null);
            check("songs offer release date", sorts.Contains(AppleMusic.Sort.ReleaseDate), null);
            check("songs offer length", sorts.Contains(AppleMusic.Sort.Length), null);

            string[] Order(AppleMusic.Sort by, bool asc) =>
                AppleMusic.Ordered(folder, rows, by, asc).Select(r => AppleMusic.ItemAt(r.Path)!.Name).ToArray();


            equal("by name is case-insensitive A to Z", string.Join(",", Order(AppleMusic.Sort.Name, true)), "alpha,Bravo,Charlie");
            // Recently added defaults to descending (StartsAscending is false), which is newest first.
            check("recently added starts newest first", !AppleMusic.StartsAscending(AppleMusic.Sort.RecentlyAdded), null);
            equal("recently added, newest first", string.Join(",", Order(AppleMusic.Sort.RecentlyAdded, false)), "alpha,Bravo,Charlie");
            equal("by artist A to Z", string.Join(",", Order(AppleMusic.Sort.Artist, true)), "alpha,Charlie,Bravo");
            equal("by release date, oldest first", string.Join(",", Order(AppleMusic.Sort.ReleaseDate, true)), "Charlie,Bravo,alpha");
            equal("by length, shortest first", string.Join(",", Order(AppleMusic.Sort.Length, true)), "alpha,Bravo,Charlie");
            equal("by length, longest first", string.Join(",", Order(AppleMusic.Sort.Length, false)), "Charlie,Bravo,alpha");

            // A fixed folder (Library) offers no sort and keeps its order.
            AppleMusic.SeedForTests("::applemusic\\Library", new List<AppleMusicItem>());
            check("the Library folder itself offers no sort", AppleMusic.SortsFor("::applemusic\\Library").Count == 0, null);

            // An album sorts by track order (Apple's), and that is its default.
            const string album = "::applemusic\\Search\\x\\Albums\\Some Album";
            AppleMusic.SetContainerForTests(album, new AppleMusicItem { Kind = "album", Name = "Some Album", Id = "a1" });
            AppleMusic.SeedForTests(album, new List<AppleMusicItem>
            {
                Song("Track C", "c", album: "Some Album", track: 3),
                Song("Track A", "a", album: "Some Album", track: 1),
                Song("Track B", "b", album: "Some Album", track: 2),
            }, numbered: true);
            equal("an album defaults to Apple's track order", AppleMusic.DefaultSort(album).By.ToString(), "Apple");
            check("an album offers a track-order name", AppleMusic.AppleOrderName(album) == "Track order", AppleMusic.AppleOrderName(album));
        }

        private static void Naming(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            // In an album, a song shows its title alone; elsewhere, title and artist.
            const string album = "::applemusic\\Library\\Albums\\Record";
            AppleMusic.SetContainerForTests(album, new AppleMusicItem { Kind = "album", Name = "Record", Id = "r" });
            var inAlbum = AppleMusic.SeedForTests(album, new List<AppleMusicItem> { Song("The Song", "s1", artist: "The Band", album: "Record", track: 1) }, numbered: true);
            equal("a song in an album is just its title", inAlbum[0].Name, "The Song");

            const string songs = "::applemusic\\Library\\Songs";
            var loose = AppleMusic.SeedForTests(songs, new List<AppleMusicItem> { Song("The Song", "s1", artist: "The Band") });
            equal("a song in a list says its artist too", loose[0].Name, "The Song, The Band");
        }

        private static void PlayOn(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            const string folder = "::applemusic\\Library\\Albums\\Disc";
            AppleMusic.SetContainerForTests(folder, new AppleMusicItem { Kind = "album", Name = "Disc", Id = "d" });
            var rows = AppleMusic.SeedForTests(folder, new List<AppleMusicItem>
            {
                Song("One", "1", album: "Disc", track: 1),
                Song("Two", "2", album: "Disc", track: 2),
                Song("Three", "3", album: "Disc", track: 3),
            }, numbered: true);
            // Lay out the shown order (what Ordered records), then walk it.
            AppleMusic.Ordered(folder, rows, AppleMusic.Sort.Apple, true);

            var first = rows[0].Path;
            var second = AppleMusic.NextSongAfter(first);
            equal("next after the first is the second", AppleMusic.ItemAt(second ?? "")?.Name ?? "(none)", "Two");
            var last = rows[2].Path;
            check("nothing after the last", AppleMusic.NextSongAfter(last) == null, null);
            equal("before the last is the middle", AppleMusic.ItemAt(AppleMusic.PreviousSongBefore(last) ?? "")?.Name ?? "(none)", "Two");
            check("nothing before the first", AppleMusic.PreviousSongBefore(first) == null, null);
        }
    }
}
