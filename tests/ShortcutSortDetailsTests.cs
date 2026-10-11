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
