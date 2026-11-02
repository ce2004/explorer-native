using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of copying, moving and deleting.
    /// </summary>
    internal static class Sweep500FilesTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, copying, moving and deleting:");
            await SystemToolsByFullPath();
            DefaultAppChangesAreSeen();
            RiskyTypesGoThroughTheShell();
        }

        private static string? FindSrc()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var src = Path.Combine(dir, "src");
                if (File.Exists(Path.Combine(src, "RoboCopyEngine.cs"))) return src;
                dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
            }
            return null;
        }

        /// <summary>
        /// robocopy and msiexec are started from System32 by full path, so a
        /// robocopy.exe or msiexec.exe in Downloads (beside a portable copy, or
        /// the current folder) never runs in their place.
        /// </summary>
        private static async Task SystemToolsByFullPath()
        {
            Check("robocopy is started by its full System32 path",
                Path.IsPathFullyQualified(RoboCopyEngine.RobocopyExecutable)
                && File.Exists(RoboCopyEngine.RobocopyExecutable)
                && RoboCopyEngine.RobocopyExecutable.StartsWith(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase),
                RoboCopyEngine.RobocopyExecutable);

            var src = FindSrc();
            if (src != null)
            {
                var bare = new Regex(@"(ProcessStartInfo\(\s*""|FileName\s*=\s*"")([^""\\:/]+\.exe)""", RegexOptions.IgnoreCase);
                var found = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
                    .SelectMany(f => bare.Matches(File.ReadAllText(f)).Select(m => Path.GetFileName(f) + ": " + m.Groups[2].Value))
                    .ToList();
                Check("no program in src is started by a bare exe name", found.Count == 0, string.Join(", ", found));
            }
            else Check("the source folder was found for the bare-name check", false);

            // Behaviour: a planted robocopy.exe in the current folder is not the one that runs.
            var root = Path.Combine(Path.GetTempPath(), "en500-planted-" + Guid.NewGuid().ToString("N"));
            var plant = Path.Combine(root, "plant");
            var from = Path.Combine(root, "from");
            var to = Path.Combine(root, "to");
            Directory.CreateDirectory(plant);
            Directory.CreateDirectory(from);
            Directory.CreateDirectory(to);
            var oldDir = Environment.CurrentDirectory;
            var oldVar = Environment.GetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath");
            try
            {
                File.Copy(Path.Combine(Environment.SystemDirectory, "hostname.exe"), Path.Combine(plant, "robocopy.exe"));
                File.WriteAllText(Path.Combine(from, "note.txt"), "hello");
                Environment.CurrentDirectory = plant;
                Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", null);
                var result = await RoboCopyEngine.RunAsync(new[] { Path.Combine(from, "note.txt") }, to, move: false, 1,
                    PasteConflictPolicy.FillGaps, null, CancellationToken.None);
                Check("a robocopy.exe in the current folder does not run in place of System32's",
                    File.Exists(Path.Combine(to, "note.txt")) && result.Failed == 0,
                    $"copied {result.Copied}, failed {result.Failed}: {string.Join("; ", result.Errors)}");
            }
            finally
            {
                Environment.CurrentDirectory = oldDir;
                Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", oldVar);
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>
        /// A default app changed while the app runs is used within half a
        /// minute, and the Open With picker is never remembered.
        /// </summary>
        private static void DefaultAppChangesAreSeen()
        {
            long clock = 0;
            string answer = @"C:\A\a.exe";
            FileLauncher.ForgetAll();
            FileLauncher.ClockForTests = () => clock;
            FileLauncher.ResolverForTests = _ => (answer, "\"%1\"");
            try
            {
                Equal("the first open uses the handler Windows names", @"C:\A\a.exe", FileLauncher.ResolveHandlerPath(".zzztest") ?? "(null)");
                answer = @"C:\B\b.exe";
                clock = 1_000;
                Equal("a moment later the handler is still the remembered one", @"C:\A\a.exe", FileLauncher.ResolveHandlerPath(".zzztest") ?? "(null)");
                clock = 31_000;
                Equal("half a minute after the default app changed, the new app opens the file", @"C:\B\b.exe", FileLauncher.ResolveHandlerPath(".zzztest") ?? "(null)");

                FileLauncher.ForgetAll();
                answer = @"C:\Windows\system32\OpenWith.exe";
                Equal("an unknown type goes to the Open With picker", answer, FileLauncher.ResolveHandlerPath(".zzzpick") ?? "(null)");
                answer = @"C:\B\b.exe";
                Equal("after choosing an app with Always, the next open goes to that app at once", @"C:\B\b.exe", FileLauncher.ResolveHandlerPath(".zzzpick") ?? "(null)");
            }
            finally
            {
                FileLauncher.ResolverForTests = null;
                FileLauncher.ClockForTests = null;
                FileLauncher.ForgetAll();
            }
        }

        /// <summary>
        /// Scripts and installers go through the shell, so Windows can show its
        /// security warning for a downloaded one.
        /// </summary>
        private static void RiskyTypesGoThroughTheShell()
        {
            FileLauncher.ForgetAll();
            foreach (var ext in new[] { ".js", ".vbs", ".msi", ".msc", ".cpl", ".reg", ".hta", ".wsf" })
            {
                Check($"{ext} is opened through the shell's security check",
                    FileLauncher.ResolveHandlerPath(ext) == null && FileLauncher.DescribeCommandLine(ext, @"C:\x\a" + ext) == null,
                    FileLauncher.ResolveHandlerPath(ext) ?? "(null)");
            }
            Check("the risky-type rule ignores case", FileLauncher.MustGoThroughShell(".JS"));
            Check("music and text still open the fast way",
                !FileLauncher.MustGoThroughShell(".flac") && !FileLauncher.MustGoThroughShell(".txt"));

            var src = FindSrc();
            var text = src == null ? "" : File.ReadAllText(Path.Combine(src, "FileLauncher.cs"));
            Check("the shell fallback lets the shell show its prompts for risky types",
                Regex.IsMatch(text, @"ErrorDialog\s*=\s*MustGoThroughShell"));
        }
    }
}
