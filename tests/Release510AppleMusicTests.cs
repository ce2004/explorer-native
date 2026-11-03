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
