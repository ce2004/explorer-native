using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// 6.1.0: every action by name from the command line (ExplorerNative --do
    /// name, --commands). The registry is pure; the window and the tray feed
    /// it, and those are checked from their source, as MainForm always is.
    /// </summary>
    internal static class CommandsTests
    {
        public static Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            Names(check, equal);
            Pipe(check, equal);
            CommandLine(check, equal);
            Registry(check, equal);
            Wiring(check, equal);
            return Task.CompletedTask;
        }

        private static void Names(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            equal("commands: a menu's text is its command", "new-folder", Commands.IdFor("New &folder"));
            equal("commands: dots and keys after a tab are left off", "go-to-path", Commands.IdFor("&Go to path…\tCtrl+L"));
            equal("commands: three plain dots too", "web-app", Commands.IdFor("We&b app..."));
            equal("commands: an audio action is its name in words", "play-pause", Commands.IdFor(AudioAction.PlayPause));
            equal("commands: and a longer one", "what-is-playing", Commands.IdFor(AudioAction.WhatIsPlaying));
            equal("commands: typed with spaces or capitals it is the same name", "play-pause", Commands.IdFor("Play Pause"));
            equal("commands: what it does is the menu text without its access key", "New folder", Commands.Plain("New &folder…"));

            var ids = AudioActions.All.Select(a => Commands.IdFor(a.Action)).ToList();
            check("commands: every audio action has a name of its own", ids.Distinct().Count() == ids.Count && ids.All(i => i.Length > 0),
                string.Join(",", ids));
        }

        private static void Pipe(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var reply = Commands.NewReplyPath();
            var line = Commands.Encode("convert-audio", new[] { @"G:\Recordings\a b.flac", "second" }, reply);
            check("commands: on the pipe it is one line", !line.Contains('\n') && Commands.Is(line), line);
            check("commands: and it comes back apart", Commands.TryDecode(line, out var name, out var given, out var where) &&
                name == "convert-audio" && given.Length == 2 && given[0] == @"G:\Recordings\a b.flac" && where == reply, null);
            check("commands: a folder path is not a command", !Commands.Is(@"C:\Users") && !Commands.TryDecode(@"C:\Users", out _, out _, out _), null);
            check("commands: a command with no name is not one", !Commands.TryDecode(Commands.Prefix, out _, out _, out _), null);

            check("commands: the answer goes only in our own file in the temporary folder", Commands.IsReplyPath(reply), reply);
            check("commands: never anywhere else", !Commands.IsReplyPath(@"C:\Windows\win.ini") &&
                !Commands.IsReplyPath(Path.Combine(Path.GetTempPath(), "something.txt")) &&
                !Commands.IsReplyPath(Path.Combine(Path.GetTempPath(), "sub", Path.GetFileName(reply))) &&
                !Commands.IsReplyPath(null), null);

            Commands.WriteReply(reply, "ok");
            try
            {
                equal("commands: the answer is written whole", "ok", File.Exists(reply) ? File.ReadAllText(reply) : "(none)");
                check("commands: and never over a file that is already there", !Commands.IsReplyPath(reply), null);
            }
            finally { try { File.Delete(reply); } catch { } }
        }

        private static void CommandLine(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var one = Commands.FromArguments(new[] { "--do", "play-pause" });
            equal("commands: --do name", "play-pause 0", one == null ? "none" : $"{one.Value.Name} {one.Value.Arguments.Length}");
            var two = Commands.FromArguments(new[] { "--quiet", "--do", "convert-audio", @"C:\a.flac", @"C:\b.flac" });
            equal("commands: what follows the name goes with it", @"convert-audio C:\a.flac|C:\b.flac",
                two == null ? "none" : $"{two.Value.Name} {string.Join("|", two.Value.Arguments)}");
            equal("commands: --commands is the list", Commands.ListName, Commands.FromArguments(new[] { "--commands" })?.Name ?? "none");
            equal("commands: --do alone is the list too", Commands.ListName, Commands.FromArguments(new[] { "--do" })?.Name ?? "none");
            check("commands: an ordinary launch is not a command", Commands.FromArguments(new[] { @"C:\Users" }) == null, null);

            bool Nothing(string _) => false;
            check("commands: --do and --commands are options the app knows",
                LaunchArguments.UnknownOption(new[] { "--commands" }, Nothing) == null &&
                LaunchArguments.UnknownOption(new[] { "--do", "play-pause" }, Nothing) == null, null);
            check("commands: what follows --do is never taken for a mistyped option",
                LaunchArguments.UnknownOption(new[] { "--do", "volume", "-5" }, Nothing) == null, null);
            equal("commands: a mistyped option before --do is still caught", "--unistall",
                LaunchArguments.UnknownOption(new[] { "--unistall", "--do", "quit" }, Nothing) ?? "none");
            var help = LaunchArguments.HelpText();
            check("commands: both are in --help", help.Contains("--commands") && help.Contains("--do <command>"), null);
        }

        private static void Registry(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            int ran = 0;
            string[] got = Array.Empty<string>();
            check("commands: a new command is taken", Commands.Register("zz-test-one", "A test", g => { ran++; got = g; }, window: false), null);
            check("commands: the first to take a name keeps it", !Commands.Register("zz-test-one", "Another", _ => ran += 100, window: false), null);
            check("commands: nothing may call itself the list", !Commands.Register(Commands.ListName, "No", _ => { }, window: false), null);

            var found = Commands.Find("ZZ Test One");
            check("commands: found however it is typed", found != null, null);
            found?.Run(new[] { "x" });
            equal("commands: and it runs with what it was given", "1 x", $"{ran} {string.Join(",", got)}");
            check("commands: a name nobody has is not found", Commands.Find("zz-nothing-like-it") == null, null);
            check("commands: names close to a wrong one are offered", Commands.Near("test-won").Contains("zz-test-one"), string.Join(",", Commands.Near("test-won")));

            Commands.RegisterMenu("ZZ &Window thing…", () => ran += 10);
            check("commands: a menu item is a command that works on the selection in the window",
                Commands.Find("zz-window-thing") is { NeedsWindow: true, ActsOnItems: true }, null);
            var list = Commands.List();
            check("commands: the list names each and says what it does",
                list.Contains("zz-test-one") && list.Contains("A test") && list.Contains("zz-window-thing") && list.Contains("ZZ Window thing"), null);
            Commands.ForgetWindow();
            check("commands: the window's are made again with the window, the rest stay",
                Commands.Find("zz-window-thing") == null && Commands.Find("zz-test-one") != null, null);
        }

        /// <summary>The window and the tray feed the registry; neither can be built here, so their source is read.</summary>
        private static void Wiring(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var main = Source("MainForm.cs");
            var tray = Source("TrayApplicationContext.cs");
            var program = Source("Program.cs");

            var item = Regex.Match(main, @"private static ToolStripMenuItem Item\(string text, Keys shortcut, Action action\)\s*\{(.*?)\n        \}", RegexOptions.Singleline);
            check("commands: every menu item made with Item is a command, with nothing else to do",
                item.Success && item.Groups[1].Value.Contains("Commands.RegisterMenu(text, action)"), null);
            check("commands: the menus are walked for items made any other way",
                main.Contains("Walk(menu.DropDownItems, \"\")") && main.Contains("Walk(_contextMenu.Items, \"\")"), null);
            check("commands: the window's are registered once its menus are built",
                Regex.IsMatch(main, @"BuildMenu\(\);\s*BuildContextMenu\(\);\s*RegisterCommands\(\);"), null);
            check("commands: keys with no menu item have one too",
                new[] { "\"switch-tab\"", "\"context-menu\"", "\"open-in-its-own-app\"", "\"toggle-selection\"", "\"escape\"", "favourite-{number}", "\"go\"", "\"select\"", "\"menu\"", "\"add-to-playlist\"" }
                    .All(main.Contains), null);

            check("commands: every audio action is a command, from the one list",
                Regex.IsMatch(tray, @"foreach \(var info in AudioActions\.All\)\s*\{\s*var action = info\.Action;\s*Commands\.Register\(Commands\.IdFor\(action\)"), null);
            check("commands: the tray's own are there",
                new[] { "\"show-window\"", "\"toggle-window\"", "\"sync-all\"", "\"pause-all-syncing\"", "\"resume-all-syncing\"", "\"quit\"", "\"play\"", "\"repeat\"" }
                    .All(tray.Contains), null);
            check("commands: one from another launch is run, never opened as a folder",
                tray.Contains("!Commands.Is(request);") && tray.Contains("RunCommand(request);"), null);
            check("commands: never underneath a dialog", tray.Contains("if (open.Modal) return"), null);
            check("commands: while quitting it is answered, not kept for the next copy",
                tray.Contains("Explorer Native is closing. Try again in a moment."), null);
            check("commands: a launch that asks for one sends it and stops",
                program.Contains("Commands.FromArguments(args) is { } wanted") && program.Contains("SendCommand(wanted.Name, wanted.Arguments)"), null);

            var notes = SourceBeside("CLAUDE.md");
            check("commands: the project notes say a feature's command comes with it",
                notes.Contains("its command comes with it"), null);
        }

        private static string Source(string name) => SourceBeside(Path.Combine("src", name));

        private static string SourceBeside(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            try { return dir == null ? "" : File.ReadAllText(Path.Combine(dir.FullName, relative)); }
            catch { return ""; }
        }
    }
}
