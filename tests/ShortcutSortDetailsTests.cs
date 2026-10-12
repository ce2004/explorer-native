using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Global or not per audio shortcut, per-folder sort orders surviving
    /// Preferences OK, and the details said after a pause on a file.
    /// </summary>
    internal static class ShortcutSortDetailsTests
    {
        public static Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            Console.WriteLine("Global shortcuts, folder sorts, file details:");

            // Every shortcut is global until somebody says otherwise.
            var s = new Settings();
            check("a new shortcut is global", AudioActions.IsGlobal(s, AudioAction.PlayPause), null);

            AudioActions.SetGlobal(s, AudioAction.PlayPause, false);
            AudioActions.SetGlobal(s, AudioAction.VolumeUp, false);
            check("unticked shortcuts are not global",
                !AudioActions.IsGlobal(s, AudioAction.PlayPause) && !AudioActions.IsGlobal(s, AudioAction.VolumeUp), null);
            check("the others stay global", AudioActions.IsGlobal(s, AudioAction.Stop), null);

            AudioActions.SetGlobal(s, AudioAction.PlayPause, true);
            equal("ticking one back leaves only the other", "VolumeUp", s.AudioLocalShortcuts);
            AudioActions.SetGlobal(s, AudioAction.VolumeUp, false);
            equal("setting it twice does not list it twice", "VolumeUp", s.AudioLocalShortcuts);

            // A folder's own order set while Preferences was open is not undone by OK.
            var live = new Settings();
            var before = new Settings();
            var after = new Settings { ShowHiddenFiles = true };
            live.FolderSorts = new Dictionary<string, string> { [@"C:\Music"] = "Modified descending" };
            var merged = Settings.WithChanges(live, before, after);
            check("OK in Preferences keeps a folder's own order",
                merged.FolderSorts.TryGetValue(@"C:\Music", out var kept) && kept == "Modified descending", null);

            // A Drive upload's progress counts files as well as bytes: a few big
            // files sent and thousands of small ones left is not 100 percent.
            var bigDone = new TransferProgress(1000L * 1024 * 1024, 1024L * 1024 * 1024, 20, 3000, "",
                5_000_000, TimeSpan.FromSeconds(60), PerItemWork: DriveUpload.PerFileWork);
            check("big files done and small ones left is not 100 percent", bigDone.Percent < 50, bigDone.Percent.ToString("F0"));
            var smallDone = new TransferProgress(1024 * 1024, 1024L * 1024 * 1024, 500, 3000, "",
                10_000, TimeSpan.FromSeconds(60), PerItemWork: DriveUpload.PerFileWork);
            check("500 of 3000 small files done is not 0 percent", smallDone.Percent > 5, smallDone.Percent.ToString("F0"));
            check("and the time left follows the files, not the byte rate",
                smallDone.Remaining is { } left && left.TotalMinutes < 30, smallDone.Remaining?.ToString() ?? "none");
            var plain = new TransferProgress(50, 100, 1, 2, "", 10, TimeSpan.FromSeconds(5));
            equal("a copy without a per-file cost still counts bytes", "50", plain.Percent.ToString("F0"));
            check("uploads send small files sixteen at a time and big ones four",
                DriveUpload.Streams == 16 && DriveUpload.BigStreams == 4, null);

            // Words are runs of anything that is not white space.
            equal("text counts", "3 words, 14 characters", FileDetails.CountText("one two\n three"));
            equal("one word is singular", "1 word, 5 characters", FileDetails.CountText("hello"));
            equal("empty text", "0 words, 0 characters", FileDetails.CountText(""));

            var dir = Path.Combine(Path.GetTempPath(), "en-details-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var text = Path.Combine(dir, "notes.txt");
                File.WriteAllText(text, "a b c");
                equal("a text file is described", "3 words, 5 characters", FileDetails.Describe(text, null) ?? "(null)");

                var other = Path.Combine(dir, "picture.bin");
                File.WriteAllBytes(other, new byte[] { 1, 2, 3 });
                check("a file with nothing to say gives nothing", FileDetails.Describe(other, null) == null, null);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }

            return Task.CompletedTask;
        }
    }
}
