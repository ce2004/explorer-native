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
            Refreshing(check, equal);
            Wiring(check);

            AppleMusic.ResetForTests();
        }

        /// <summary>
        /// 4.0.0 shipped with song rows ending ".m4a", and the file player took
        /// them for files: "Cannot play Apathy.m4a, opening it instead".
        /// </summary>
        private static void Refreshing(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            const string songs = "::applemusic\\Library\\Songs";
            var rows = AppleMusic.SeedForTests(songs, new List<AppleMusicItem> { Song("Apathy", "ap1", artist: "Someone") });
            check("a song's path has no extension", System.IO.Path.GetExtension(rows[0].Path).Length == 0, rows[0].Path);
            check("a song is not an audio file to the file player",
                !AudioFiles.IsAudio(rows[0].Path, AudioFiles.DefaultExtensions), rows[0].Path);
            check("a song with a dot in its title has no extension either",
                AppleMusic.SeedForTests(songs, new List<AppleMusicItem> { Song("Mr. Brightside", "mb") })[0].Path
                    .EndsWith("\\Mr. Brightside", StringComparison.Ordinal), null);

            check("a seeded list counts as in memory", AppleMusic.HasCached(songs), null);
            check("a library list is refreshed behind its cached view", AppleMusic.CanRefresh(songs), null);
            check("the top is never refreshed", !AppleMusic.CanRefresh("::applemusic"), null);
            check("Library itself is never refreshed", !AppleMusic.CanRefresh("::applemusic\\Library"), null);
            check("Search is never refreshed", !AppleMusic.CanRefresh(AppleMusic.SearchFolder), null);
            check("a search's results are never refreshed", !AppleMusic.CanRefresh(AppleMusic.SearchPath("x") + "\\Songs"), null);

            const string station = "::applemusic\\Stations\\Chill";
            AppleMusic.SetContainerForTests(station, new AppleMusicItem { Kind = "station", Name = "Chill", Id = "st" });
            check("a station is never refreshed (it would swap the songs)", !AppleMusic.CanRefresh(station), null);

            AppleMusic.Forget(songs);
            check("F5's Forget drops the list from memory", !AppleMusic.HasCached(songs), null);
        }

        /// <summary>
        /// The 4.0.0 bugs were all in MainForm wiring that the model tests could
        /// not see: Enter never reached the Apple Music handler, the four-second
        /// "is this folder still there" probe took every Apple Music list for a
        /// missing folder and went back to the drives, F5 and F1 treated the list
        /// as a folder on disk, and the Sort submenu had no items until it opened,
        /// so it had no arrow and could not be opened.
        /// </summary>
        private static void Wiring(Action<string, bool, string?> check)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            string? main = null;
            try { if (dir != null) main = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "src", "MainForm.cs")); }
            catch { }
            if (main == null) { check("MainForm.cs can be read", false, null); return; }

            string Body(string signature, int length = 4000)
            {
                int at = main.IndexOf(signature, StringComparison.Ordinal);
                return at < 0 ? "" : main.Substring(at, Math.Min(length, main.Length - at));
            }

            var activate = Body("private void ActivateFocused(bool openExternally)");
            int apple = activate.IndexOf("AppleMusic.Is(entry.Path)", StringComparison.Ordinal);
            int player = activate.IndexOf("Program.PlayAudioFile", StringComparison.Ordinal);
            check("Enter sends an Apple Music row to its own handler", apple >= 0 &&
                activate.IndexOf("OpenAppleMusicRow(entry)", StringComparison.Ordinal) > apple, null);
            check("...before the file player could take it", apple >= 0 && player > apple, null);

            var probe = Body("private async void CheckCurrentPathStillExists()", 2500);
            int guard = probe.IndexOf("AppleMusic.Is(path)", StringComparison.Ordinal);
            int exists = probe.IndexOf("DirectoryExistsAsync(path)", StringComparison.Ordinal);
            check("the folder-still-there probe leaves Apple Music alone", guard >= 0 && exists > guard, null);

            check("F5 in Apple Music asks Apple again",
                Body("private async void Refresh_()").Contains("AppleMusic.Forget(", StringComparison.Ordinal), null);
            check("F1 in Apple Music searches Apple",
                Body("private async void SearchHere()").Contains("AskAppleMusicSearch()", StringComparison.Ordinal), null);

            var sort = Body("private ToolStripMenuItem BuildSortMenu()");
            int added = sort.IndexOf("menu.DropDownItems.AddRange(fileItems);", StringComparison.Ordinal);
            int opening = sort.IndexOf("menu.DropDownOpening +=", StringComparison.Ordinal);
            check("the Sort submenu has its items before it is first opened (so it has an arrow)",
                added >= 0 && opening > added, null);
            check("in Apple Music the item says Sort by", main.Contains("\"Sort &by\"", StringComparison.Ordinal), null);
            check("the empty-search hint names a key that exists",
                !main.Contains("or Ctrl+F, to search Apple Music", StringComparison.Ordinal), null);
            check("opening a menu applies the list's held-back name (or NVDA says a folder left long ago)",
                main.Contains("menu.MenuActivate += (_, _) => { _menuOpen = true; foreach (var p in _panes) ApplyPendingName(p); };", StringComparison.Ordinal), null);
            check("a folder changed from a menu names the list at once (NVDA said the folder just left)",
                main.Contains("if (pane.List.Focused && !menuMoment)", StringComparison.Ordinal) &&
                main.Contains("menu.MenuDeactivate += (_, _) => { _menuOpen = false;", StringComparison.Ordinal), null);
            check("an Apple song's details are said after the menu closes, not cut off by it",
                Body("private void ShowSongProperties()", 2500).Contains("AnnounceAfterMenu(\"applemusic.details\"", StringComparison.Ordinal), null);

            string? Other(string name)
            {
                try { return System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "src", name)); }
                catch { return null; }
            }
            var tray = Other("TrayApplicationContext.cs") ?? "";
            var page = Other("AppleMusicPlayer.cs") ?? "";
            check("Enter on a song does not stop an empty file player (it said \"Nothing is loaded\")",
                tray.Contains("if (_audio.HasTrack) _audio.Stop();", StringComparison.Ordinal), null);
            check("quitting closes Apple's player and deletes its profile",
                tray.Contains("AppleMusicPlayer.Shutdown()", StringComparison.Ordinal), null);
            check("the player page is visible to Chromium (hidden, the media never loaded)",
                page.Contains("_controller.IsVisible = true;", StringComparison.Ordinal), null);
            check("the player page is served under the origin the web player's token works from",
                page.Contains("PageHost = \"music.apple.com\"", StringComparison.Ordinal), null);
            check("the player may start sound without a click",
                page.Contains("--autoplay-policy=no-user-gesture-required", StringComparison.Ordinal), null);
            check("an end is reported once per play, never while switching songs",
                page.Contains("(s !== 5 && s !== 10) || switching || endedFor === current", StringComparison.Ordinal), null);
            check("with a song queued, an end waits to see whether Apple's player went on to it",
                page.Contains("if (queuedNext !== null) setTimeout(tell, 2500); else tell();", StringComparison.Ordinal) &&
                page.Contains("moves !== advances", StringComparison.Ordinal), null);

            // 4.0.2: Conner, "it says last to first, it should be oldest added, and then most recently added".
            check("added orders say oldest added first and most recently added first, oldest listed first",
                main.Contains("ascending ? \"most recently added first\" : \"oldest added first\"", StringComparison.Ordinal) &&
                main.Contains("AppleMusic.Sort.RecentlyAdded => ascending ? \"oldest added first\" : \"most recently added first\"", StringComparison.Ordinal) &&
                main.Contains("menu.DropDownItems.Add(newestUp ? down : up);", StringComparison.Ordinal), null);

            // 4.1.0: Shift+F10 on a song.
            var menu = Body("private void ShowOurContextMenu()", 4000);
            check("Shift+F10 on a song offers Play next",
                menu.Contains("_playNextItem.Visible = appleSong;", StringComparison.Ordinal), null);
            check("Favorite or Unfavorite shows only once known, saying what it will do",
                menu.Contains("_favoriteItem.Visible = favorite != null;", StringComparison.Ordinal) &&
                menu.Contains("favorite == true ? \"Unfa&vorite\" : \"Fa&vorite\"", StringComparison.Ordinal), null);
            check("Add to playlist lists your playlists, and hides when there are none",
                menu.Contains("_addToPlaylistItem.Visible = playlists is { Count: > 0 };", StringComparison.Ordinal), null);
            check("Play next replaces what Apple's player had queued",
                page.Contains("music.queue.clearAfterCurrent();", StringComparison.Ordinal), null);

            // 4.0.2: Conner, "the apple music doesn't follow the selected audio device".
            check("the Output device list works while Apple Music plays",
                tray.Contains("AudioAction.DeviceDialog => ShowDeviceDialog(),", StringComparison.Ordinal) &&
                !System.Text.RegularExpressions.Regex.IsMatch(tray, @"or AudioAction\.DeviceDialog or AudioAction\.LimiterDialog\s*\r?\n\s*or AudioAction\.EqualiserDialog[^;]*isn't available"), null);
            check("choosing a device moves Apple's player too",
                tray.Contains("AppleMusicPlayer.SetOutputDevice(NameOf(id));", StringComparison.Ordinal), null);
            check("the player page plays to the chosen device", page.Contains("a.setSinkId(id)", StringComparison.Ordinal), null);
            check("the page can name devices but never listens",
                page.Contains("CoreWebView2PermissionKind.Microphone", StringComparison.Ordinal) &&
                page.Contains("getUserMedia = () => Promise.reject", StringComparison.Ordinal) &&
                page.Contains("e.State = CoreWebView2PermissionState.Deny", StringComparison.Ordinal), null);

            // 4.0.2: "the music takes forever to play", "skipping lags".
            check("entering Apple Music starts the player in the background",
                Body("private async Task<bool> ShowAppleMusicFolderAsync(", 1500).Contains("AppleMusicPlayer.Warm();", StringComparison.Ordinal), null);
            check("the next song is queued in Apple's player so it goes straight on",
                page.Contains("if (c.next) items.push(item(c.next));", StringComparison.Ordinal) &&
                page.Contains("kind: \"advanced\"", StringComparison.Ordinal), null);
            check("a skip inside the downloaded audio moves it directly",
                page.Contains("if (a && inBuffer(a, t)) { seekWant = null; a.currentTime = t; return; }", StringComparison.Ordinal), null);
            check("a held skip sends one seek at a time, the newest",
                page.Contains("Interlocked.Exchange(ref _seekQueued, 1) == 0", StringComparison.Ordinal), null);
            check("the page reports only whole-second changes (CPU)",
                page.Contains("if (key === lastState) return;", StringComparison.Ordinal), null);
            check("a crashed player is restarted where the song was",
                page.Contains("core.ProcessFailed += (_, e) => OnProcessFailed(e.ProcessFailedKind);", StringComparison.Ordinal), null);
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
            // Artist first, so typing an artist's first letters moves by artist (Conner, 4.0.2).
            equal("a song in a list says its artist first", loose[0].Name, "The Band, The Song");
            equal("a songs list starts in artist order", AppleMusic.DefaultSort(songs).By.ToString(), "Artist");

            const string albums = "::applemusic\\Library\\Albums";
            var albumRows = AppleMusic.SeedForTests(albums, new List<AppleMusicItem>
            {
                new AppleMusicItem { Kind = "album", Name = "Record", Subtitle = "The Band", Id = "al1" },
            });
            equal("an album in a list says its artist first", albumRows[0].Name, "The Band, Record");

            // 4.1.0: "it should say favorited at the end of a title if it's favorited".
            check("a song's favorite is unknown until Apple has said", AppleMusic.IsFavorite(loose[0].Path) == null, null);
            AppleMusic.SetFavoritesForTests(new[] { "s1" }, Array.Empty<string>());
            var renamed = AppleMusic.ListAsync(songs, fresh: false, CancellationToken.None).GetAwaiter().GetResult();
            equal("a favorited song says so at the end", renamed[0].Name, "The Band, The Song, favorited");
            check("and is known to be one", AppleMusic.IsFavorite(loose[0].Path) == true, null);
            var albumNow = AppleMusic.ListAsync(album, fresh: false, CancellationToken.None).GetAwaiter().GetResult();
            equal("in an album too, after the title", albumNow[0].Name, "The Song, favorited");
            AppleMusic.SetFavoritesForTests(Array.Empty<string>(), new[] { "s1" });
            var unrenamed = AppleMusic.ListAsync(songs, fresh: false, CancellationToken.None).GetAwaiter().GetResult();
            equal("unfavorited, the word goes", unrenamed[0].Name, "The Band, The Song");
            check("and is known not to be one", AppleMusic.IsFavorite(loose[0].Path) == false, null);

            // 4.1.1: "adding a song to a playlist many times... it shouldn't let you".
            var mine = new AppleMusicItem { Kind = "song", Name = "X", Id = "i.abc", CatalogId = "123", IsLibrary = true };
            var found = new AppleMusicItem { Kind = "song", Name = "X", Id = "123", CatalogId = "123" };
            var inList = new AppleMusicItem { Kind = "song", Name = "X", Id = "i.abc", IsLibrary = true };
            var other = new AppleMusicItem { Kind = "song", Name = "X", Id = "999", CatalogId = "999" };
            check("a library song and the same song found in search are one song",
                AppleMusic.SongKeys(mine).Overlaps(AppleMusic.SongKeys(found)), null);
            check("a playlist entry naming only the library id is the same song",
                AppleMusic.SongKeys(mine).Overlaps(AppleMusic.SongKeys(inList)), null);
            check("a different song with the same title is not",
                !AppleMusic.SongKeys(mine).Overlaps(AppleMusic.SongKeys(other)), null);

            // 4.1.1: a refresh must not move a path to another song of the same name.
            const string mix = "::applemusic\\Library\\Playlists\\Mix";
            var firstRows = AppleMusic.SeedForTests(mix, new List<AppleMusicItem> { Song("Intro", "A"), Song("Intro", "B") });
            string pathOf(List<AppleMusic.Row> rows, string id) => rows.First(r => AppleMusic.ItemAt(r.Path)!.Id == id).Path;
            string aBefore = pathOf(firstRows, "A"), bBefore = pathOf(firstRows, "B");
            var secondRows = AppleMusic.SeedForTests(mix, new List<AppleMusicItem> { Song("Intro", "C"), Song("Intro", "A"), Song("Intro", "B") });
            equal("a song keeps its path when another of the same name arrives", pathOf(secondRows, "A"), aBefore);
            equal("and so does the other one", pathOf(secondRows, "B"), bBefore);
            check("the new one gets a path of its own",
                pathOf(secondRows, "C") != aBefore && pathOf(secondRows, "C") != bBefore, pathOf(secondRows, "C"));
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
