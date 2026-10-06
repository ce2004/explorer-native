using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Round-two fixes: a busy port written over the saved one, an ampersand in
    /// an information label taken as an access key, help and one-dash switches
    /// starting the application, the single-instance handoff topping out at
    /// about sixty launches a second, and a window title with a NUL cut short
    /// by OK.
    /// </summary>
    internal static class Round2FixTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Round two fixes:");

            var previous = Settings.OverrideAppDataDir;
            var dir = NewSettingsDir();
            try
            {
                PortIsNeverWrittenBackOnReloadTests();
                InfoLabelTests();
                OptionTests();
                WindowTitleTests();
                await HandoffThroughputTests();
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(dir, recursive: true); } catch { }
            }

            Console.WriteLine();
        }

        private static string NewSettingsDir()
        {
            const string scratch =
                @"C:\Users\Conner\AppData\Local\Temp\claude\C--Users-Conner\210da054-69e3-4f58-81af-73a744528057\scratchpad";
            var root = Directory.Exists(scratch)
                ? Path.Combine(scratch, "fix-r2-settings")
                : Path.Combine(Path.GetTempPath(), "ExplorerNativeTests_r2settings");
            var dir = Path.Combine(root, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            Settings.OverrideAppDataDir = dir;
            return dir;
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private static string OnSta(Action work, int seconds = 60)
        {
            string error = "";
            var thread = new Thread(() =>
            {
                try { work(); }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(seconds))) return "timed out";
            return error;
        }

        private static List<T> FindAll<T>(Control root) where T : Control
        {
            var found = new List<T>();
            foreach (Control child in root.Controls)
            {
                if (child is T match) found.Add(match);
                found.AddRange(FindAll<T>(child));
            }
            return found;
        }

        /// <summary>
        /// The tray is not compiled into the suite (it needs the whole
        /// application), so the rule is read from its source: a port that could
        /// not be had is never written back on a reload, and on OK it is the
        /// previously saved port that goes back, not the one listening.
        /// </summary>
        private static void PortIsNeverWrittenBackOnReloadTests()
        {
            var tray = Source("TrayApplicationContext.cs");
            Check("the tray's source is there to read", tray != null);
            if (tray == null) return;

            int start = tray.IndexOf("public void ApplySettings(Settings settings, bool reload", StringComparison.Ordinal);
            int end = start < 0 ? -1 : tray.IndexOf("ReconcileWebApp(portWas)", start, StringComparison.Ordinal);
            Check("ApplySettings and its port block are found", start >= 0 && end > start);
            if (start < 0 || end <= start) return;
            var block = tray.Substring(start, end - start);

            Check("the listening port is never saved as the setting",
                !block.Contains("settings.WebAppPort = portWas", StringComparison.Ordinal));

            int reloadBranch = block.IndexOf("else if (reload)", StringComparison.Ordinal);
            int okBranch = reloadBranch < 0 ? -1 : block.IndexOf("else", reloadBranch + 4, StringComparison.Ordinal);
            Check("a failed move on a reload has a branch of its own", reloadBranch >= 0 && okBranch > reloadBranch);
            if (reloadBranch < 0 || okBranch <= reloadBranch) return;

            var onReload = block.Substring(reloadBranch, okBranch - reloadBranch);
            Check("which saves nothing", !onReload.Contains(".Save(", StringComparison.Ordinal));
            Check("and changes no setting", !onReload.Contains("WebAppPort =", StringComparison.Ordinal));

            var onOk = block.Substring(okBranch);
            Check("OK that could not move puts back the port saved before it",
                onOk.Contains("settings.WebAppPort = savedPortWas", StringComparison.Ordinal));
            Check("which is what was saved, read before the new settings replace it",
                block.IndexOf("int savedPortWas = _settings.WebAppPort", StringComparison.Ordinal) >= 0 &&
                block.IndexOf("int savedPortWas = _settings.WebAppPort", StringComparison.Ordinal) <
                block.IndexOf("_settings = settings;", StringComparison.Ordinal));
        }

        /// <summary>
        /// Information labels show data — a device name, an account, a link — so
        /// an ampersand in one is a character, not an access key.
        /// </summary>
        private static void InfoLabelTests()
        {
            int infos = 0, mnemonic = 0;
            bool tomCatKeepsItsAmpersand = false;
            var error = OnSta(() =>
            {
                using var window = new SettingsForm(new Settings { SpeakEnabled = false }, null);
                window.BuildAllPages();
                var labels = FindAll<Label>(window).Where(l => l.GetType().Name == "InfoLabel").ToList();
                infos = labels.Count;
                mnemonic = labels.Count(l => l.UseMnemonic);
                if (labels.Count > 0)
                {
                    labels[0].Text = "Tom&Cat";
                    tomCatKeepsItsAmpersand = !labels[0].UseMnemonic && labels[0].Text == "Tom&Cat";
                }
            });
            Equal("Preferences builds for the label check", "", error);
            Check($"Preferences has information labels ({infos})", infos > 0);
            Equal("no information label takes & as an access key", "0", mnemonic.ToString());
            Check("a device called Tom&Cat is shown and read as Tom&Cat", tomCatKeepsItsAmpersand);
        }

        /// <summary>
        /// --help, -h and /? are help; anything else starting with - or / that
        /// is not an existing path is an unknown option.
        /// </summary>
        private static void OptionTests()
        {
            Func<string, bool> nothingExists = _ => false;

            foreach (var help in new[] { "--help", "-h", "/?", "-H", "--HELP" })
            {
                Check($"{help} asks for help", LaunchArguments.IsHelp(help));
                Check($"{help} is not an unknown option",
                    LaunchArguments.UnknownOption(new[] { help }, nothingExists) == null);
            }
            Check("help is found among other arguments",
                LaunchArguments.WantsHelp(new[] { @"C:\Users", "/?" }));
            Check("a folder alone is not a request for help",
                !LaunchArguments.WantsHelp(new[] { @"C:\Users" }));

            foreach (var bad in new[] { "-?", "/help", "-install", "/install", "-x", "/", "--bogus" })
                Equal($"{bad} is an unknown option", bad,
                    LaunchArguments.UnknownOption(new[] { bad }, nothingExists) ?? "(none)");

            Check("a folder whose name starts with a dash is a folder",
                LaunchArguments.UnknownOption(new[] { "-drafts" }, a => a == "-drafts") == null);
            Check("the real check finds the current drive's root as a path",
                LaunchArguments.UnknownOption(new[] { "/" }) == null);
            Check("ordinary paths are not options",
                LaunchArguments.UnknownOption(new[] { @"C:\Users", "D:\"" }, nothingExists) == null);
            Check("the log named after --read-log is not an option",
                LaunchArguments.UnknownOption(new[] { "--read-log", "-old.log" }, nothingExists) == null);
            Check("but a double-dash after it still is",
                LaunchArguments.UnknownOption(new[] { "--read-log", "--bogus" }, nothingExists) == "--bogus");
            Check("the known switches and modifiers are still known",
                LaunchArguments.UnknownOption(
                    LaunchArguments.CommandLineSwitches.Concat(LaunchArguments.Modifiers), nothingExists) == null);

            var text = LaunchArguments.HelpText();
            var missing = LaunchArguments.CommandLineSwitches
                .Where(s => s != "--license" && !text.Contains(s + " ", StringComparison.Ordinal)).ToList();
            Check("the help lists every option", missing.Count == 0, string.Join(", ", missing));
            Check("and says how to ask for it", text.Contains("--help, -h, /?", StringComparison.Ordinal));

            // Program is not in the suite; the order in Main is read from it.
            var program = Source("Program.cs");
            int helpAt = program?.IndexOf("LaunchArguments.WantsHelp(args)", StringComparison.Ordinal) ?? -1;
            int unknownAt = program?.IndexOf("LaunchArguments.UnknownOption(args)", StringComparison.Ordinal) ?? -1;
            int mutexAt = program?.IndexOf("new Mutex(false, SingleInstanceMutexName)", StringComparison.Ordinal) ?? -1;
            Check("help is answered before anything else in Main", helpAt > 0 && helpAt < unknownAt && unknownAt < mutexAt);
            if (helpAt > 0 && unknownAt > helpAt)
            {
                var helpBlock = program!.Substring(helpAt, unknownAt - helpAt);
                Check("help exits with 0", helpBlock.Contains("Environment.ExitCode = 0", StringComparison.Ordinal));
                Check("help prints to the console, or the notice that closes itself",
                    helpBlock.Contains("Console.Out.WriteLine", StringComparison.Ordinal) &&
                    helpBlock.Contains("ShowNotice(", StringComparison.Ordinal));
                var unknownBlock = program.Substring(unknownAt, Math.Max(0, mutexAt - unknownAt));
                Check("an unknown option exits with 2",
                    unknownBlock.Contains("Environment.ExitCode = 2", StringComparison.Ordinal));
            }
        }

        /// <summary>
        /// An edit box stops at a NUL, so a title holding one reads back shorter
        /// once the box has a window. OK must not write that back; typing still
        /// does.
        /// </summary>
        private static void WindowTitleTests()
        {
            const string withNul = "Explorer\0Native";
            string? afterOk = null, afterTyping = null, shown = null;
            var error = OnSta(() =>
            {
                using (var window = new SettingsForm(new Settings { SpeakEnabled = false, WindowTitle = withNul }, null))
                {
                    window.BuildAllPages();
                    var box = FindAll<TextBox>(window).First(t => t.AccessibleName == "Window title");
                    _ = box.Handle; // the window an edit box truncates in
                    shown = box.Text;
                    window.AcceptAsync().GetAwaiter().GetResult();
                    afterOk = window.Result.WindowTitle;
                }

                using (var window = new SettingsForm(new Settings { SpeakEnabled = false, WindowTitle = withNul }, null))
                {
                    window.BuildAllPages();
                    var box = FindAll<TextBox>(window).First(t => t.AccessibleName == "Window title");
                    _ = box.Handle;
                    box.Text = "My files";
                    window.AcceptAsync().GetAwaiter().GetResult();
                    afterTyping = window.Result.WindowTitle;
                }
            });
            Equal("Preferences builds for the title check", "", error);
            Check("the box really does show less than the saved title (the premise)",
                shown != null && shown != withNul, shown?.Replace("\0", "\\0"));
            Equal("OK without touching the title keeps it whole", withNul.Replace("\0", "\\0"),
                (afterOk ?? "(null)").Replace("\0", "\\0"));
            Equal("a title typed in is still saved", "My files", afterTyping ?? "(null)");
        }

        /// <summary>
        /// Two hundred launches at once, each on a thread of its own and started
        /// together, all handed over. It managed about sixty a second: a 15.6ms
        /// timer tick per connection, and a moment after each one with no pipe.
        /// </summary>
        private static async Task HandoffThroughputTests()
        {
            SingleInstance.NameSuffix = "_r2burst_" + Guid.NewGuid().ToString("N")[..8];
            var received = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
            SingleInstance.StartServer(r => received[r] = true);
            try
            {
                await Task.Delay(300);
                const int n = 200;
                var sent = new bool[n];
                using var go = new ManualResetEventSlim(false);
                var threads = Enumerable.Range(0, n).Select(i => new Thread(() =>
                {
                    go.Wait();
                    sent[i] = SingleInstance.SendToRunningInstance($@"C:\r2burst\{i}");
                })
                { IsBackground = true }).ToList();
                foreach (var t in threads) t.Start();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                go.Set();
                foreach (var t in threads) t.Join(TimeSpan.FromSeconds(30));
                clock.Stop();
                for (int i = 0; i < 50 && received.Count < n; i++) await Task.Delay(100);

                Equal($"two hundred simultaneous launches are all delivered ({clock.ElapsedMilliseconds} ms)",
                    n.ToString(), sent.Count(x => x).ToString());
                Equal("and every one arrives", n.ToString(),
                    Enumerable.Range(0, n).Count(i => received.ContainsKey($@"C:\r2burst\{i}")).ToString());
            }
            finally
            {
                SingleInstance.StopServer();
                await Task.Delay(200);
                SingleInstance.NameSuffix = "";
            }
        }
    }
}
