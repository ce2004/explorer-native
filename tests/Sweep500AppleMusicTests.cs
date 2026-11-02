using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of Apple Music.
    /// </summary>
    internal static class Sweep500AppleMusicTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, Apple Music:");
            StationPreviousAfterMoreSongs();
            await StationsOfflineSaySo();
            SeekWhileStartingIsKept();
            OutsidePauseBecomesTheWish();
        }

        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            return dir?.FullName ?? "";
        }

        private static AppleMusicItem Song(string name, string id) =>
            new AppleMusicItem { Kind = "song", Name = name, Id = id, CatalogId = id };

        private static string NameAt(string? path) => AppleMusic.ItemAt(path ?? "")?.Name ?? "(none)";

        /// <summary>Bug 1: a station's next handful joins the shown order, so Previous and play-on keep walking it.</summary>
        private static void StationPreviousAfterMoreSongs()
        {
            AppleMusic.ResetForTests();
            try
            {
                const string station = "::applemusic\\Stations\\Sweep500";
                AppleMusic.SetContainerForTests(station, new AppleMusicItem { Kind = "station", Id = "ra.x", Name = "S" });
                var rows = AppleMusic.SeedForTests(station, new List<AppleMusicItem> { Song("A", "s1"), Song("B", "s2"), Song("C", "s3") });
                // What PlayAppleStation does: records the first handful as shown.
                AppleMusic.Ordered(station, rows, AppleMusic.Sort.Apple, true);
                var added = AppleMusic.AppendToStation(station, new List<AppleMusicItem> { Song("D", "s4"), Song("E", "s5") });
                Equal("a station's next handful is added", "2", added.Count.ToString());
                if (added.Count < 2) return;
                Equal("Previous on the first fetched song goes back to the last of the first handful", "C", NameAt(AppleMusic.PreviousSongBefore(added[0].Path)));
                Equal("Previous on the second fetched song goes back to the first fetched", "D", NameAt(AppleMusic.PreviousSongBefore(added[1].Path)));
                Equal("play-on from the last of the first handful goes to the fetched songs, not another fetch", "D", NameAt(AppleMusic.NextSongAfter(rows[2].Path)));
            }
            finally { AppleMusic.ResetForTests(); }
        }

        /// <summary>Bug 2: Stations with every source failing says why instead of an empty list (and caches nothing).</summary>
        private static async Task StationsOfflineSaySo()
        {
            const string down = "Apple Music couldn't be reached. Check the internet connection.";
            Task<List<AppleMusicItem>> Fails() => throw new AppleMusicException(down);
            Task<List<AppleMusicItem>> One(string id) => Task.FromResult(new List<AppleMusicItem> { new AppleMusicItem { Kind = "station", Id = id, Name = id } });
            Task<List<AppleMusicItem>> None() => throw new AppleMusicException("Apple Music says that isn't there any more.") { NotFound = true };
            Task<List<AppleMusicItem>> Empty() => Task.FromResult(new List<AppleMusicItem>());

            string? thrown = null;
            try { await AppleMusicClient.GatherStationsAsync(new Func<Task<List<AppleMusicItem>>>[] { Fails, Fails, Fails }); }
            catch (AppleMusicException ex) { thrown = ex.Message; }
            Equal("stations with no source answering say Apple couldn't be reached, not empty", down, thrown ?? "(no failure, an empty list)");

            var partly = await AppleMusicClient.GatherStationsAsync(new Func<Task<List<AppleMusicItem>>>[] { Fails, () => One("a"), () => One("b") });
            Equal("one source failing still lists the others' stations", "2", partly.Count.ToString());

            string? emptyThrew = null;
            try
            {
                var none = await AppleMusicClient.GatherStationsAsync(new Func<Task<List<AppleMusicItem>>>[] { None, Empty, Empty });
                Equal("an account that answers with no stations is truly empty", "0", none.Count.ToString());
            }
            catch (AppleMusicException ex) { emptyThrew = ex.Message; }
            Check("Apple's 'none' counts as an answer, not a failure", emptyThrew == null, emptyThrew);

            string? allNone = null;
            try { await AppleMusicClient.GatherStationsAsync(new Func<Task<List<AppleMusicItem>>>[] { None, None, None }); }
            catch (AppleMusicException ex) { allNone = ex.Message; }
            Check("every source answering 'none' is an empty list, not a failure", allNone == null, allNone);

            var src = File.ReadAllText(Path.Combine(Root(), "src", "AppleMusicClient.cs"));
            int at = src.IndexOf("Task<List<AppleMusicItem>> StationsAsync(", StringComparison.Ordinal);
            int end = at < 0 ? -1 : src.IndexOf("\n        }", at, StringComparison.Ordinal);
            Check("StationsAsync gathers through the helper that reports a total failure",
                at >= 0 && end > at && src.Substring(at, end - at).Contains("GatherStationsAsync"), null);
        }

        private static string PlayerSource() =>
            File.ReadAllText(Path.Combine(Root(), "src", "AppleMusicPlayer.cs"));

        private static string MethodBody(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0) return "";
            int end = src.IndexOf("\n        }", at, StringComparison.Ordinal);
            return end < 0 ? src.Substring(at) : src.Substring(at, end - at);
        }

        /// <summary>Bug 3: a seek pressed while the player is starting is where the song starts.</summary>
        private static void SeekWhileStartingIsKept()
        {
            var start = MethodBody(PlayerSource(), "private static void Start(");
            int ready = start.IndexOf("ReadyAsync", StringComparison.Ordinal);
            int play = start.IndexOf("CallAsync(\"play\"", StringComparison.Ordinal);
            Check("Start sends play after the page is ready", ready >= 0 && play > ready, null);
            if (ready < 0 || play < 0) return;
            int seekRead = start.IndexOf("_seekTarget", ready, StringComparison.Ordinal);
            Check("Start reads the seek target after the page is ready and before sending play", seekRead > ready && seekRead < play, null);
            int close = start.IndexOf(')', play);
            var args = start.Substring(play, Math.Max(0, close - play)).Split(',').Select(a => a.Trim()).ToArray();
            Check("play is not sent with the place Start began with alone (a seek while starting would be lost)",
                args.Length >= 5 && args[4] != "startAt", string.Join(", ", args));
        }

        /// <summary>Bug 4: a pause or resume from outside the app becomes what Play/Pause goes by.</summary>
        private static void OutsidePauseBecomesTheWish()
        {
            Check("an outside pause after the song made sound becomes the wish (next press resumes)",
                AppleMusicPlayState.WishFromPage(3, started: true, 5000) == false, null);
            Check("an outside resume becomes the wish (next press pauses)",
                AppleMusicPlayState.WishFromPage(2, started: true, 5000) == true, null);
            Check("right after Enter, before any sound, what was asked for decides",
                AppleMusicPlayState.WishFromPage(3, started: false, 5000) == null, null);
            Check("right after our own press the page's report may predate it and is not trusted",
                AppleMusicPlayState.WishFromPage(2, started: true, 200) == null, null);
            Check("ended, waiting and seeking change nothing",
                AppleMusicPlayState.WishFromPage(5, true, 5000) == null && AppleMusicPlayState.WishFromPage(8, true, 5000) == null
                && AppleMusicPlayState.WishFromPage(6, true, 5000) == null, null);

            var src = PlayerSource();
            int stateCase = src.IndexOf("case \"state\":", StringComparison.Ordinal);
            int next = stateCase < 0 ? -1 : src.IndexOf("case \"ended\":", stateCase, StringComparison.Ordinal);
            Check("the page's state report goes through WishFromPage",
                stateCase >= 0 && next > stateCase && src.Substring(stateCase, next - stateCase).Contains("WishFromPage"), null);
            Check("Play/Pause notes when it was pressed",
                MethodBody(src, "public static string TogglePlayPause(").Contains("_toggledAt"), null);
        }
    }
}
