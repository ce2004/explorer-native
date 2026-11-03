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
            }
            finally
            {
                AppleMusicClient.FakeForTests = null;
                AppleMusic.ResetForTests();
            }
        }

        private static HttpResponseMessage Answer(HttpStatusCode code, string body = "{}") =>
            new(code) { Content = new StringContent(body) };

        private static AppleMusicItem Song(string name, string id, string artist = "Someone") =>
            new() { Kind = "song", Id = id, CatalogId = id, Name = name, Subtitle = artist };

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
