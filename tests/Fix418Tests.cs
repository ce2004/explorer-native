using System;
using System.Collections.Generic;
using System.IO;

namespace ExplorerNative
{
    /// <summary>
    /// 4.1.8: four bugs a review of 4.1.7 found. ArchiveBrowser, the Drive-only
    /// check and AppleMusicPlayer are pinned by their source (the first two run
    /// tar or a Drive rig; the player is not in the suite), the shuffle swap by
    /// behaviour.
    /// </summary>
    internal static class Fix418Tests
    {
        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            Console.WriteLine("4.1.8 fixes:");

            var browser = Source("ArchiveBrowser.cs") ?? "";
            check("an archive is partial only when tar stopped short, not because of a note about one entry",
                browser.Contains("if (!damaged && stoppedShort) partial", StringComparison.Ordinal) &&
                !browser.Contains("if (!damaged && reason != null) partial = reason;", StringComparison.Ordinal),
                "a symlink or repeated name marked a whole 7z partial and it was extracted again on every open");

            var tar = Source("BsdTar.cs") ?? "";
            check("bsdtar says when it did not get to the end",
                tar.Contains("if (unfinished) stoppedShort?.Invoke();", StringComparison.Ordinal), null);

            var driveOnly = Source("DriveMonitor.DriveOnly.cs") ?? "";
            check("a file inside a hidden folder counts as never synced",
                driveOnly.Contains("if (folderLeftOut || (file.Attributes & leftOut) != 0) neverSynced.Add(relative);", StringComparison.Ordinal),
                "Keep on Drive only waited five minutes for files the sync skips");
            check("Keep on Drive only says which files were left on the PC",
                driveOnly.Contains("{ leftOnPc.Add(relative); continue; }", StringComparison.Ordinal) &&
                driveOnly.Contains("left on this PC because", StringComparison.Ordinal),
                "it said \"cleared off this PC\" with temporary files still there");

            var player = Source("AppleMusicPlayer.cs") ?? "";
            check("a new shuffled round is checked against the song that is starting",
                player.Contains("Refill(path);", StringComparison.Ordinal) &&
                !player.Contains("string.Equals(songs[0], _currentPath", StringComparison.Ordinal),
                "the last song of a round could play twice in a row");

            var songs = new List<string> { "a", "b", "c" };
            AppleMusicPlayerProbe(songs, "a");
            equal("the song just heard goes to the end of the new round", "c,b,a", string.Join(",", songs));
            songs = new List<string> { "b", "a", "c" };
            AppleMusicPlayerProbe(songs, "a");
            equal("and a round that does not start with it is left alone", "b,a,c", string.Join(",", songs));
        }

        // AppleMusicPlayer.cs is not compiled into the suite (WebView2), so the
        // swap is the same two lines, kept beside the source check above.
        private static void AppleMusicPlayerProbe(List<string> songs, string last)
        {
            if (songs.Count > 1 && string.Equals(songs[0], last, StringComparison.OrdinalIgnoreCase))
                (songs[0], songs[^1]) = (songs[^1], songs[0]);
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            try { return File.ReadAllText(Path.Combine(dir.FullName, "src", name)); } catch { return null; }
        }
    }
}
