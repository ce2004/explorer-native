using System.Collections.Generic;

namespace ExplorerNative
{
    /// <summary>
    /// Help, Keyboard shortcuts (Ctrl+F1): the keys built into the window, which
    /// cannot be changed. The audio player's keys are not here; they are the
    /// person's own, on the Shortcuts page of Preferences.
    ///
    /// A test checks that every key on the main menu is in this list, so a new
    /// menu item with a key cannot be left out of it.
    /// </summary>
    internal static class BuiltInShortcuts
    {
        /// <summary>One line: the keys, then what they do.</summary>
        internal sealed record Line(string Keys, string Does)
        {
            public override string ToString() => $"{Keys}: {Does}";
        }

        /// <summary>The sections, in the order they are listed.</summary>
        public static readonly (string Heading, Line[] Lines)[] Sections =
        {
            ("Moving around", new Line[]
            {
                new("Up and Down arrows", "move through the list"),
                new("Home and End", "first or last item"),
                new("Letters", "jump to the item whose name starts with what you type"),
                new("Enter", "open the folder or file, or play an audio file"),
                new("Shift+Enter", "open the file in its own app instead of the built-in player"),
                new("Backspace or Alt+Up", "up one level"),
                new("Alt+D", "the list of drives"),
                new("Alt+Home", "your home folder"),
                new("Ctrl+L", "go to a folder by typing its path"),
                new("Tab or Ctrl+Tab", "switch to the other tab"),
                new("F5", "refresh the folder"),
                new("Ctrl+Shift+A", "say which folder you are in"),
            }),
            ("Selecting", new Line[]
            {
                new("Space", "select or unselect the item you are on, leaving the rest as they are"),
                new("Ctrl+Up and Ctrl+Down", "move without changing what is selected"),
                new("Ctrl+Space", "select or unselect the item you are on; letting go of Ctrl says how many are selected"),
                new("Shift+arrows, Shift+Home, Shift+End", "select everything from here to there"),
                new("Ctrl+A", "select all"),
            }),
            ("Files and folders", new Line[]
            {
                new("F7", "new file"),
                new("F8 or Ctrl+Shift+N", "new folder"),
                new("F2", "rename"),
                new("Delete", "move to the Recycle Bin (Google Drive: to its trash)"),
                new("Shift+Delete", "delete permanently"),
                new("Ctrl+C", "copy"),
                new("Ctrl+X", "cut"),
                new("Ctrl+V", "paste"),
                new("Alt+C", "copy the path"),
                new("Alt+L", "copy the Google Drive link"),
                new("Alt+Shift+L", "copy a public Google Drive link"),
                new("Alt+Enter", "properties"),
                new("Ctrl+Shift+S", "work out the size of a folder"),
                new("Shift+F10 or the Applications key", "the menu for the item you are on: compress, extract, convert audio and more"),
                new("Ctrl+Shift+Y", "sync all Google Drive folders now"),
            }),
            ("Searching", new Line[]
            {
                new("F1", "search this folder and every folder in it (in Apple Music, searches Apple Music)"),
                new("Escape", "stop a search that is still running, or leave the results and go back"),
                new("Enter on a result", "go to the folder it is in, landing on it"),
                new("Words", "type words in any order: greedy tate finds Tate McRae - greedy.flac"),
                new("Folder names", "a word can also be in the folders a file is in: tate greedy finds greedy.flac in a Tate McRae folder"),
                new("Quotes", "keep words together: \"i love\" finds I Love You, not Love I"),
                new("Star and question mark", "patterns: *.flac finds every FLAC file, t??.mp3 finds t07.mp3"),
                new("Minus", "leave things out: love -remix skips anything with remix in its name or folder"),
            }),
            ("The app", new Line[]
            {
                new("Ctrl+P", "Preferences, where the audio player's keys can be changed"),
                new("Ctrl+R", "restart the app"),
                new("Shift+F1", "what's new in each version"),
                new("Ctrl+F1", "this list of keyboard shortcuts"),
                new("Alt or F10", "the menu bar"),
            }),
            ("In this list", new Line[]
            {
                new("Ctrl+Down and Ctrl+Up", "next or previous section"),
                new("Ctrl+C", "copy the line you are on"),
                new("Escape or Enter", "close"),
            }),
        };

        /// <summary>The list as read: each heading on a line of its own, then its keys.</summary>
        public static List<string> Lines()
        {
            var lines = new List<string>();
            foreach (var (heading, rows) in Sections)
            {
                lines.Add(heading);
                foreach (var row in rows) lines.Add(row.ToString());
            }
            return lines;
        }

        /// <summary>Whether a line is a section heading (it has no colon).</summary>
        public static bool IsHeading(string line)
        {
            foreach (var (heading, _) in Sections)
                if (line == heading) return true;
            return false;
        }
    }
}
