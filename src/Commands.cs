using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ExplorerNative
{
    /// <summary>
    /// Everything Explorer Native can do, by name, for the command line
    /// (6.1.0, Conner: "add command params to every single little action").
    ///
    ///   ExplorerNative --commands
    ///   ExplorerNative --do play-pause
    ///   ExplorerNative --do convert-audio "G:\Recordings\a.flac"
    ///
    /// A second copy sends the command down the pipe to the running one,
    /// which looks it up here and runs it. Nothing is listed by hand that can
    /// be found: every menu item registers itself as it is built
    /// (<c>MainForm.Item</c>, then a walk of the menus for anything made
    /// another way), every audio action comes from <c>AudioActions.All</c>,
    /// and the tray registers its own. So a feature added to a menu has a
    /// command the moment it exists.
    ///
    /// RULE: whenever a feature is added or changed, its command comes with
    /// it. A new menu item needs nothing. Anything that is not a menu item (a
    /// key with no menu entry, something that takes a value) is registered by
    /// hand next to where it lives, with <see cref="Register"/>, and a command
    /// whose action was renamed or removed is renamed or removed here too.
    /// </summary>
    internal static class Commands
    {
        /// <summary>What a command sent down the pipe starts with.</summary>
        public const string Prefix = "<do>";

        /// <summary>Between the parts of a command on the pipe: nothing a path or a name can hold.</summary>
        private const char Separator = '\u001F';

        /// <summary>The name of the built-in command that lists the rest.</summary>
        public const string ListName = "list";

        /// <param name="NeedsWindow">Runs in the main window, which is opened for it.</param>
        /// <param name="ActsOnItems">
        /// Works on what is selected: paths given after the command are gone to
        /// and selected first.
        /// </param>
        public sealed record Command(string Id, string Does, Action<string[]> Run, bool NeedsWindow, bool ActsOnItems);

        private static readonly Dictionary<string, Command> _all = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A menu's text as a command name: "New &amp;folder…" is new-folder.
        /// Taken when the item is made, so an item whose text changes as it is
        /// used (Pause, Play) keeps the name it started with.
        /// </summary>
        public static string IdFor(string text)
        {
            int tab = text.IndexOf('\t');
            if (tab >= 0) text = text[..tab];

            var id = new StringBuilder(text.Length);
            bool gap = false;
            foreach (char c in text)
            {
                if (c == '&') continue;
                if (char.IsLetterOrDigit(c))
                {
                    if (gap && id.Length > 0) id.Append('-');
                    id.Append(char.ToLowerInvariant(c));
                    gap = false;
                }
                else gap = true;
            }
            return id.ToString();
        }

        /// <summary>An audio action's name: PlayPause is play-pause.</summary>
        public static string IdFor(AudioAction action)
        {
            var name = action.ToString();
            var id = new StringBuilder(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i])) id.Append('-');
                id.Append(char.ToLowerInvariant(name[i]));
            }
            return id.ToString();
        }

        /// <summary>Menu text as a sentence: no access key, no dots.</summary>
        public static string Plain(string text)
        {
            int tab = text.IndexOf('\t');
            if (tab >= 0) text = text[..tab];
            return text.Replace("&", "").Replace("…", "").Replace("...", "").Trim();
        }

        /// <summary>
        /// Adds a command. The first to take a name keeps it (the same action
        /// is on several menus) unless <paramref name="replace"/>.
        /// </summary>
        public static bool Register(string id, string does, Action<string[]> run, bool window, bool items = false,
            bool replace = false)
        {
            if (string.IsNullOrEmpty(id) || id == ListName) return false;
            lock (_all)
            {
                if (!replace && _all.ContainsKey(id)) return false;
                _all[id] = new Command(id, does, run, window, items);
                return true;
            }
        }

        /// <summary>A menu item, as it is made.</summary>
        public static void RegisterMenu(string text, Action action) =>
            Register(IdFor(text), Plain(text), _ => action(), window: true, items: true);

        /// <summary>The window is being built again: its commands are made again with it.</summary>
        public static void ForgetWindow()
        {
            lock (_all)
                foreach (var id in _all.Values.Where(c => c.NeedsWindow).Select(c => c.Id).ToList())
                    _all.Remove(id);
        }

        public static bool Has(string id)
        {
            lock (_all) return _all.ContainsKey(id);
        }

        /// <summary>The command of this name, however it was typed: "Play Pause", "play_pause" and "play-pause" are one.</summary>
        public static Command? Find(string name)
        {
            lock (_all) return _all.TryGetValue(IdFor(name), out var found) ? found : null;
        }

        public static IReadOnlyList<Command> All
        {
            get { lock (_all) return _all.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToList(); }
        }

        /// <summary>Names that share a word with one that was not found, for "did you mean".</summary>
        public static IReadOnlyList<string> Near(string name)
        {
            var words = IdFor(name).Split('-', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToArray();
            return All.Where(c => words.Any(w => c.Id.Contains(w, StringComparison.Ordinal))).Select(c => c.Id).Take(8).ToList();
        }

        /// <summary>What --commands prints: every command and what it does, one to a line.</summary>
        public static string List()
        {
            var all = All;
            int width = Math.Min(40, all.Count == 0 ? 0 : all.Max(c => c.Id.Length));
            var text = new StringBuilder();
            text.AppendLine("Run one with: ExplorerNative --do <command> [file or folder ...]");
            text.AppendLine("Files or folders after a command that works on a selection are gone to and selected first.");
            text.AppendLine();
            foreach (var command in all)
                text.Append(command.Id.PadRight(width)).Append("  ").AppendLine(command.Does);
            return text.ToString().TrimEnd();
        }

        // ------------------------------------------------------ the pipe

        public static bool Is(string request) => request.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>One line for the pipe: where to write the answer, the command, what follows it.</summary>
        public static string Encode(string name, IEnumerable<string> arguments, string? replyPath) =>
            Prefix + string.Join(Separator, new[] { replyPath ?? "", name }.Concat(arguments)
                .Select(part => part.Replace(Separator, ' ').Replace('\r', ' ').Replace('\n', ' ')));

        public static bool TryDecode(string request, out string name, out string[] arguments, out string? replyPath)
        {
            name = "";
            arguments = Array.Empty<string>();
            replyPath = null;
            if (!Is(request)) return false;

            var parts = request[Prefix.Length..].Split(Separator);
            if (parts.Length < 2 || parts[1].Trim().Length == 0) return false;
            replyPath = parts[0].Length == 0 ? null : parts[0];
            name = parts[1].Trim();
            arguments = parts.Skip(2).ToArray();
            return true;
        }

        private const string ReplyStart = "ExplorerNative-reply-";

        /// <summary>A fresh file in the temporary folder for the running copy to answer in.</summary>
        public static string NewReplyPath() =>
            Path.Combine(Path.GetTempPath(), ReplyStart + Guid.NewGuid().ToString("N") + ".txt");

        /// <summary>
        /// Only ever our own answer file: the running copy writes where a
        /// command tells it to, and that must not be anywhere else.
        /// </summary>
        public static bool IsReplyPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                var full = Path.GetFullPath(path);
                var name = Path.GetFileName(full);
                return string.Equals(Path.GetDirectoryName(full)?.TrimEnd('\\'), Path.GetTempPath().TrimEnd('\\'),
                           StringComparison.OrdinalIgnoreCase) &&
                       name.StartsWith(ReplyStart, StringComparison.Ordinal) &&
                       name.EndsWith(".txt", StringComparison.Ordinal) &&
                       name.Length == ReplyStart.Length + 32 + 4 &&
                       !File.Exists(full);
            }
            catch { return false; }
        }

        /// <summary>Writes the answer whole (beside it, then renamed), so a reader never sees half.</summary>
        public static void WriteReply(string? path, string answer)
        {
            if (!IsReplyPath(path)) return;
            try
            {
                var part = path + ".part";
                File.WriteAllText(part, answer, new UTF8Encoding(false));
                File.Move(part, path!, overwrite: false);
            }
            catch { }
        }

        // ------------------------------------------------ the command line

        public const string DoSwitch = "--do";
        public const string ListSwitch = "--commands";

        /// <summary>
        /// The command a launch asks for, or null: "--commands" is the list,
        /// "--do name ..." is name with everything after it.
        /// </summary>
        public static (string Name, string[] Arguments)? FromArguments(IReadOnlyList<string> args)
        {
            for (int i = 0; i < args.Count; i++)
            {
                var a = args[i].Trim();
                if (a.Equals(ListSwitch, StringComparison.OrdinalIgnoreCase)) return (ListName, Array.Empty<string>());
                if (!a.Equals(DoSwitch, StringComparison.OrdinalIgnoreCase)) continue;

                var rest = args.Skip(i + 1).Where(r => !r.Trim().Equals("--quiet", StringComparison.OrdinalIgnoreCase)).ToArray();
                return rest.Length == 0 ? (ListName, Array.Empty<string>()) : (rest[0], rest.Skip(1).ToArray());
            }
            return null;
        }
    }
}
