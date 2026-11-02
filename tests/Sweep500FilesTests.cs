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
    }
}
