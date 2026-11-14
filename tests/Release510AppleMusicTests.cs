using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// 5.1.0: Apple Music: albums from their row, lyrics and stations.
    /// </summary>
    internal static class Release510AppleMusicTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("5.1.0, Apple Music: albums from their row, lyrics and stations:");
            try
            {
                await PlaylistGoneMakesNewList();
                LoadingSongIsNotPaused();
                LateErrorIsQuiet();
                await PlayAllFromRow();
                await StationFromSong();
                await Lyrics();
                await LongListShowsItsFirstPageFirst();
                EffectsReachApplesPlayer();
            }
            finally
            {
                AppleMusicClient.FakeForTests = null;
                AppleMusic.ResetForTests();
            }
        }

        private static string SourceText(string name)
        {
            var at = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (at != null && !System.IO.Directory.Exists(System.IO.Path.Combine(at.FullName, "src"))) at = at.Parent;
            var path = at == null ? "" : System.IO.Path.Combine(at.FullName, "src", name);
            return path.Length > 0 && System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";
        }

        private static HttpResponseMessage Answer(HttpStatusCode code, string body = "{}") =>
            new(code) { Content = new StringContent(body) };

        private static AppleMusicItem Song(string name, string id, string artist = "Someone") =>
            new() { Kind = "song", Id = id, CatalogId = id, Name = name, Subtitle = artist };

        private static string SongsJson(string type, params (string Id, string Name)[] songs) =>
            "{\"data\":[" + string.Join(",", songs.Select(s =>
                $"{{\"id\":\"{s.Id}\",\"type\":\"{type}\",\"attributes\":{{\"name\":\"{s.Name}\",\"artistName\":\"The Beatles\",\"playParams\":{{\"catalogId\":\"c{s.Id}\"}}}}}}")) + "]}";

        /// <summary>A fake Apple: the account, then whatever <paramref name="answer"/> gives for a path ("{}" otherwise).</summary>
        private static void FakeApple(Func<string, string?> answer, List<string>? asked = null) =>
            AppleMusicClient.FakeForTests = req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                lock (asked ?? new List<string>()) asked?.Add(req.Method + " " + path);
                if (path.Contains("/me/account")) return Answer(HttpStatusCode.OK, "{\"meta\":{\"subscription\":{\"active\":true,\"storefront\":\"us\"}}}");
                return Answer(HttpStatusCode.OK, answer(path) ?? "{}");
            };

        /// <summary>Feature A: an album, playlist or artist's songs from its row, without opening it.</summary>
        private static async Task PlayAllFromRow()
        {
            AppleMusic.ResetForTests();
            AppleMusic.ConfigureForTests();
            FakeApple(path => path switch
            {
                _ when path.EndsWith("/albums/l.ab/tracks") => SongsJson("library-songs", ("i.1", "Come Together"), ("i.2", "Something"), ("i.3", "Oh! Darling")),
                _ when path.EndsWith("/artists/r.1/albums") =>
                    "{\"data\":[{\"id\":\"l.a1\",\"type\":\"library-albums\",\"attributes\":{\"name\":\"Help!\",\"artistName\":\"The Beatles\"}}," +
                    "{\"id\":\"l.a2\",\"type\":\"library-albums\",\"attributes\":{\"name\":\"Revolver\",\"artistName\":\"The Beatles\"}}]}",
                _ when path.EndsWith("/albums/l.a1/tracks") => SongsJson("library-songs", ("i.4", "Help"), ("i.5", "Yesterday")),
                _ when path.EndsWith("/albums/l.a2/tracks") => SongsJson("library-songs", ("i.6", "Taxman")),
                _ => null,
            });
            var albums = AppleMusic.SeedForTests("::applemusic\\Library\\Albums", new List<AppleMusicItem>
                { new() { Kind = "album", Id = "l.ab", Name = "Abbey Road", Subtitle = "The Beatles", IsLibrary = true } });
            var artists = AppleMusic.SeedForTests("::applemusic\\Library\\Artists", new List<AppleMusicItem>
                { new() { Kind = "artist", Id = "r.1", Name = "The Beatles", IsLibrary = true } });
            var songRow = AppleMusic.SeedForTests("::applemusic\\Library\\Songs", new List<AppleMusicItem> { Song("Yesterday", "y1") })[0].Path;

            Check("an album row can be played whole", AppleMusic.CanPlayAll(albums[0].Path), albums[0].Path);
            Check("an artist row can be played whole", AppleMusic.CanPlayAll(artists[0].Path), null);
            Check("a song row is not offered Play all", !AppleMusic.CanPlayAll(songRow), null);

            var (name, songs, folder) = await AppleMusic.SongsOfAsync(albums[0].Path, CancellationToken.None);
            Equal("an album's name is said", "Abbey Road", name);
            Equal("an album's songs come in its order", "Come Together, Something, Oh! Darling",
                string.Join(", ", songs.Select(s => AppleMusic.ItemAt(s)!.Name)));
            Equal("an album's songs refill from the album", albums[0].Path, folder ?? "(none)");

            var artist = await AppleMusic.SongsOfAsync(artists[0].Path, CancellationToken.None);
            Equal("an artist's songs are every album's, album by album", "Help, Yesterday, Taxman",
                string.Join(", ", artist.Songs.Select(s => AppleMusic.ItemAt(s)!.Name)));
            Check("an artist's songs have no one folder to refill from", artist.Folder == null, artist.Folder);

            Equal("Play all says what is playing", "Playing Abbey Road, 17 songs", AppleMusic.PlayAllSaid("play", "Abbey Road", 17));
            Equal("Shuffle all says so", "Shuffling Abbey Road, 1 song", AppleMusic.PlayAllSaid("shuffle", "Abbey Road", 1));
            Equal("Play all next says so", "Abbey Road plays next, 3 songs", AppleMusic.PlayAllSaid("next", "Abbey Road", 3));

            // Menu access keys stay unique among what an album row and a song row show in Apple Music.
            var main = SourceText("MainForm.cs");
            Check("the menu has Play all, Shuffle all and Play all next",
                main.Contains("\"&Play all\"") && main.Contains("\"S&huffle all\"") && main.Contains("\"Play all ne&xt\""), null);
        }

        /// <summary>
        /// 6.2.0: a long playlist is shown from its first page, the whole of it
        /// follows, the first page's songs keep their paths, and anything that
        /// needs every song (Play all) gets every song.
        /// </summary>
        private static async Task LongListShowsItsFirstPageFirst()
        {
            AppleMusic.ResetForTests();
            AppleMusic.ConfigureForTests();
            const int total = 250;
            var asked = new List<string>();
            AppleMusicClient.FakeForTests = req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.Contains("/me/account")) return Answer(HttpStatusCode.OK, "{\"meta\":{\"subscription\":{\"active\":true,\"storefront\":\"us\"}}}");
                if (!path.EndsWith("/playlists/p.big/tracks")) return Answer(HttpStatusCode.OK);
                var offsetAt = System.Text.RegularExpressions.Regex.Match(req.RequestUri.Query, "offset=(\\d+)");
                int offset = offsetAt.Success ? int.Parse(offsetAt.Groups[1].Value) : 0;
                lock (asked) asked.Add("offset " + offset);
                var page = Enumerable.Range(offset, Math.Min(100, total - offset)).Select(i => ("i." + i, "Song " + i)).ToArray();
                var body = SongsJson("library-songs", page);
                string next = offset + 100 < total ? $",\"next\":\"/v1/me/library/playlists/p.big/tracks?offset={offset + 100}\"" : "";
                return Answer(HttpStatusCode.OK, body.Substring(0, body.Length - 1) + next + $",\"meta\":{{\"total\":{total}}}}}");
            };
            var folder = AppleMusic.SeedForTests("::applemusic\\Library\\Playlists", new List<AppleMusicItem>
                { new() { Kind = "playlist", Id = "p.big", Name = "Big", IsLibrary = true } })[0].Path;

            var grew = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnGrew(string f) { if (string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)) grew.TrySetResult(f); }
            AppleMusic.Grew += OnGrew;
            try
            {
                var first = await AppleMusic.ListAsync(folder, fresh: false, CancellationToken.None, firstPageFirst: true);
                Equal("a long playlist is shown from its first page", "100", first.Count.ToString());
                Check("the whole playlist follows, and panes showing it are told",
                    await Task.WhenAny(grew.Task, Task.Delay(5000)) == grew.Task, null);

                var whole = await AppleMusic.ListAsync(folder, fresh: false, CancellationToken.None, firstPageFirst: true);
                Equal("then the folder is the whole playlist", total.ToString(), whole.Count.ToString());
                Check("the songs of the first page keep their paths",
                    first.Select(r => r.Path).SequenceEqual(whole.Take(100).Select(r => r.Path)), null);
                Equal("in Apple's order to the end", "Song 249", AppleMusic.ItemAt(whole[^1].Path)?.Name ?? "(none)");
                Equal("every page was asked for once", "offset 0; offset 100; offset 200",
                    string.Join("; ", asked.OrderBy(a => a.Length).ThenBy(a => a)));
            }
            finally { AppleMusic.Grew -= OnGrew; }

            // Play all on a row never opened: every song, not a first page.
            AppleMusic.ResetForTests();
            AppleMusic.ConfigureForTests();
            folder = AppleMusic.SeedForTests("::applemusic\\Library\\Playlists", new List<AppleMusicItem>
                { new() { Kind = "playlist", Id = "p.big", Name = "Big", IsLibrary = true } })[0].Path;
            var all = await AppleMusic.SongsOfAsync(folder, CancellationToken.None);
            Equal("Play all plays the whole playlist", total.ToString(), all.Songs.Count.ToString());
        }

        /// <summary>6.2.0: the built-in player's effects are handed to Apple's player, and its page does them.</summary>
        private static void EffectsReachApplesPlayer()
        {
            var tray = SourceText("TrayApplicationContext.cs");
            var player = SourceText("AppleMusicPlayer.cs");
            Check("nothing says an effect is not available for Apple Music any more",
                !tray.Contains("That isn't available for Apple Music"), null);
            foreach (var dialog in new[] { "ShowSpeedDialog()", "ShowLimiterDialog()", "ShowEqualiserDialog()", "ShowPitchDialog()", "ShowSilenceDialog()" })
            {
                int apple = tray.IndexOf("if (AppleMusicPlayer.IsActive)\r\n            {\r\n                if (action == AudioAction.ToggleRepeat)", StringComparison.Ordinal);
                if (apple < 0) apple = tray.IndexOf("if (AppleMusicPlayer.IsActive)\n            {\n                if (action == AudioAction.ToggleRepeat)", StringComparison.Ordinal);
                int end = apple < 0 ? -1 : tray.IndexOf("string message = action switch", apple, StringComparison.Ordinal);
                Check("Apple Music opens " + dialog, apple >= 0 && end > apple && tray.Substring(apple, end - apple).Contains(dialog), null);
            }
            Check("every effect change is handed to Apple's player",
                System.Text.RegularExpressions.Regex.Matches(tray, "PushAppleEffects\\(\\);").Count >= 8, null);
            Check("the page has the effects worklet and takes the effects command",
                player.Contains("registerProcessor(\"fx\", Fx)") && player.Contains("case \"effects\":"), null);
            Check("the effects run at interactive latency",
                player.Contains("new AudioContext({ latencyHint: \"interactive\" })") && !player.Contains("new AudioContext({ latencyHint: \"playback\" })"), null);
            Check("MusicKit's seek no longer throws the downloaded song away",
                player.Contains("SourceBuffer.prototype.remove = function (from, to)"), null);
            Check("pause and play are settled one at a time",
                player.Contains("case \"pause\": holdPaused = true; await settle(); break;") &&
                player.Contains("case \"resume\": holdPaused = false; await settle(); break;"), null);

            // The worklet's equaliser is the built-in player's: same centres, same Q.
            Check("the worklet's bands are the equaliser's bands",
                player.Contains("const FREQ = [" + string.Join(", ", Equaliser.Frequencies) + "];"), null);
        }

        /// <summary>Feature C: Start station on a song plays Apple's "ra." station made from it.</summary>
        private static async Task StationFromSong()
        {
            AppleMusic.ResetForTests();
            AppleMusic.ConfigureForTests();
            var asked = new List<string>();
            FakeApple(path => path.Contains("/stations/next-tracks/")
                ? SongsJson("songs", ("s1", "Let It Be"), ("s2", "Hey Jude")) : null, asked);
            var song = AppleMusic.SeedForTests("::applemusic\\Library\\Songs", new List<AppleMusicItem> { Song("Yesterday", "y1") })[0].Path;
            var rows = await AppleMusic.StartSongStationAsync(song, CancellationToken.None);
            Check("the station is made from the song's catalogue id", asked.Any(a => a.Contains("/stations/next-tracks/ra.y1")), string.Join("; ", asked));
            Equal("the station's first songs are ready to play", "2", rows.Count.ToString());
            Check("its songs play on as a station", AppleMusic.InStation(rows[0].Path), rows[0].Path);
            Check("a station is never refreshed under the player", !AppleMusic.CanRefresh(AppleMusic.SongStationFolder), null);
        }

        private const string SampleTtml =
            "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" itunes:timing=\"Word\">" +
            "<head><metadata><ttm:agent type=\"person\" xml:id=\"v1\"/><iTunesMetadata><songwriters><songwriter>Lennon</songwriter></songwriters></iTunesMetadata></metadata></head>" +
            "<body dur=\"2:05\"><div begin=\"0.5\" end=\"20\" itunes:songPart=\"Verse\">" +
            "<p begin=\"0.5\" end=\"4\" ttm:agent=\"v1\"><span begin=\"0.5\" end=\"1\">Yesterday,</span> <span begin=\"1\" end=\"2\">all</span> <span begin=\"2\" end=\"3\">my</span> <span begin=\"3\" end=\"4\">troubles</span></p>" +
            "<p begin=\"4\" end=\"8\">Seemed so far &amp; away<br/>Now it looks</p>" +
            "</div><div begin=\"20\" end=\"30\"><p begin=\"20\" end=\"25\">Suddenly</p></div></body></tt>";

        /// <summary>Feature B: lyrics from Apple's TTML as plain lines, and "none" when Apple has none.</summary>
        private static async Task Lyrics()
        {
            var lines = AppleMusic.LyricsLines(SampleTtml);
            Equal("TTML becomes plain lines, a blank line between verses",
                "Yesterday, all my troubles|Seemed so far & away|Now it looks||Suddenly", string.Join("|", lines));
            Check("the head's credits are not lyrics", !lines.Any(l => l.Contains("Lennon")), null);
            Equal("broken TTML still gives its lines", "One|Two",
                string.Join("|", AppleMusic.LyricsLines("<tt><body><p>One<br>Two</p></body>")));

            AppleMusic.ResetForTests();
            AppleMusic.ConfigureForTests();
            var song = AppleMusic.SeedForTests("::applemusic\\Library\\Songs", new List<AppleMusicItem> { Song("Yesterday", "y1") })[0].Path;
            var asked = new List<string>();
            FakeApple(path => path.EndsWith("/songs/y1/lyrics")
                ? "{\"data\":[{\"id\":\"y1\",\"type\":\"lyrics\",\"attributes\":{\"ttml\":" + System.Text.Json.JsonSerializer.Serialize(SampleTtml) + "}}]}"
                : null, asked);
            var got = await AppleMusic.LyricsAsync(song, CancellationToken.None);
            Equal("a song's lyrics come from the catalogue's lyrics endpoint", "5", (got?.Count ?? 0).ToString());
            Check("asked of the catalogue song", asked.Any(a => a == "GET /v1/catalog/us/songs/y1/lyrics"), string.Join("; ", asked));

            AppleMusicClient.FakeForTests = req => req.RequestUri!.AbsolutePath.Contains("/me/account")
                ? Answer(HttpStatusCode.OK, "{\"meta\":{\"subscription\":{\"active\":true,\"storefront\":\"us\"}}}")
                : Answer(HttpStatusCode.NotFound);
            Check("a song with no lyrics gives none (said as such)", await AppleMusic.LyricsAsync(song, CancellationToken.None) == null, null);

            var tray = SourceText("TrayApplicationContext.cs");
            Check("no lyrics is said in plain words", tray.Contains("\"Apple Music has no lyrics for this song.\""), null);
            Check("lyrics with nothing playing says so", tray.Contains("\"Nothing from Apple Music is playing\""), null);
            Check("Lyrics comes after every older action, so saved shortcuts keep their meaning",
                (int)AudioAction.Lyrics > (int)AudioAction.UpNext, null);
            Check("Lyrics has no key until one is chosen", new Settings().AudioLyricsShortcut.Length == 0, null);
        }

        /// <summary>Bug 3: after Stop or a failure, a late page error with no play number was still spoken.</summary>
        private static void LateErrorIsQuiet()
        {
            Check("an error with nothing loaded is not said", !AppleMusicPlayState.ErrorMatters(""), null);
            Check("an error while a song is loaded is said", AppleMusicPlayState.ErrorMatters("::applemusic\\Library\\Songs\\x"), null);
            // The player is not in the suite (WebView2), so its wiring is read.
            var src = SourceText("AppleMusicPlayer.cs");
            int errorCase = src.IndexOf("case \"error\":", StringComparison.Ordinal);
            int guard = src.IndexOf("AppleMusicPlayState.ErrorMatters(", errorCase, StringComparison.Ordinal);
            int fail = src.IndexOf("Fail(failed,", errorCase, StringComparison.Ordinal);
            Check("the page's error report asks whether a song is loaded before speaking", errorCase > 0 && guard > errorCase && guard < fail, null);
        }

        /// <summary>Bug 2: a song still loading was said as "Paused" with no length.</summary>
        private static void LoadingSongIsNotPaused()
        {
            AppleMusic.ResetForTests();
            AppleMusic.ConfigureForTests();
            var song = Song("Yesterday", "y1", "The Beatles");
            song.DurationMs = 125_000;
            var path = AppleMusic.SeedForTests("::applemusic\\Library\\Songs", new List<AppleMusicItem> { song })[0].Path;
            var said = AppleMusicPlayState.DescribeNow(path, "Yesterday by The Beatles", 0, 0, playing: true);
            Check("a loading song asked to play is not said as paused", !said.StartsWith("Paused", StringComparison.Ordinal), said);
            Check("a loading song's length comes from the catalogue", said.Contains(" of "), said);
            Equal("the catalogue length is used until the page says", "125", AppleMusicPlayState.LengthOf(path, 0).ToString());
            Equal("the page's own length wins once known", "130", AppleMusicPlayState.LengthOf(path, 130).ToString());
            Check("a real pause still says Paused",
                AppleMusicPlayState.DescribeNow(path, "Yesterday", 10, 125, playing: false).StartsWith("Paused, ", StringComparison.Ordinal), null);
        }

        /// <summary>Bug 1: a playlist deleted elsewhere left the shared list changed in place while the menu read it.</summary>
        private static async Task PlaylistGoneMakesNewList()
        {
            AppleMusic.ResetForTests();
            AppleMusic.ConfigureForTests();
            AppleMusicClient.FakeForTests = req => req.RequestUri!.AbsolutePath.Contains("/me/account")
                ? Answer(HttpStatusCode.OK, "{\"meta\":{\"subscription\":{\"active\":true,\"storefront\":\"us\"}}}")
                : Answer(HttpStatusCode.NotFound);
            var gone = new AppleMusicItem { Kind = "playlist", Id = "p.gone", Name = "Gone", CanEdit = true };
            var kept = new AppleMusicItem { Kind = "playlist", Id = "p.kept", Name = "Kept", CanEdit = true };
            AppleMusic.SetPlaylistsForTests(new List<AppleMusicItem> { gone, kept });
            var before = AppleMusic.EditablePlaylists!;
            var rows = AppleMusic.SeedForTests("::applemusic\\Library\\Songs", new List<AppleMusicItem> { Song("Yesterday", "y1") });

            string? said = null;
            try { await AppleMusic.AddToPlaylistAsync(rows[0].Path, gone, CancellationToken.None); }
            catch (AppleMusicException ex) { said = ex.Message; }
            Check("adding to a deleted playlist says it is gone", said?.Contains("isn't in your library") == true, said);
            Equal("the list the menu was reading is left as it was", "2", before.Count.ToString());
            Equal("the deleted playlist is gone from the new list", "Kept",
                string.Join(",", AppleMusic.EditablePlaylists!.Select(p => p.Name)));
        }
    }
}
