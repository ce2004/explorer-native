using System;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// 5.1.0: files: running transfers, pause and resume, and search by size, date and kind.
    /// </summary>
    internal static class Release510FilesTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("5.1.0, files: running transfers, pause and resume, and search by size, date and kind:");
            OpenRefusesDottedNames();
            MoveCleanupKeepsNewFiles();
            await PropertiesRefusesDottedNames();
        }

        // Bug 1: "notes.txt." or a file under "Backup." would open its sibling.
        private static void OpenRefusesDottedNames()
        {
            var launched = false;
            FileLauncher.ResolverForTests = _ => { launched = true; return ("notepad.exe", "\"%1\""); };
            try
            {
                foreach (var path in new[] { @"C:\x\notes.txt.", @"C:\x\Backup.\a.txt", @"C:\x\name \a.txt" })
                {
                    string? why = null;
                    try { FileLauncher.Open(path); } catch (System.IO.IOException ex) { why = ex.Message; }
                    Check($"Open refuses {path}", why == FileLauncher.MisreadMessage, why);
                }
                Check("Open launches nothing for a dotted name", !launched);
            }
            finally
            {
                FileLauncher.ResolverForTests = null;
                FileLauncher.ForgetAll();
            }
        }

        // Bug 2: a file written into a moved folder after the emptiness walk survives.
        private static void MoveCleanupKeepsNewFiles()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "en510files-" + Guid.NewGuid().ToString("N"));
            var sub = System.IO.Path.Combine(root, "sub");
            var deeper = System.IO.Path.Combine(sub, "deeper");
            var other = System.IO.Path.Combine(root, "other");
            try
            {
                System.IO.Directory.CreateDirectory(deeper);
                System.IO.Directory.CreateDirectory(other);
                // Parents first, as the walk lists them; then a download lands.
                var walked = new System.Collections.Generic.List<string> { root, sub, deeper, other };
                var late = System.IO.Path.Combine(sub, "arrived.part");
                System.IO.File.WriteAllText(late, "late");
                FileOperations.DeleteDeepestFirst(walked);
                Check("the file written after the walk survives", System.IO.File.Exists(late));
                Check("empty folders beside it are cleared", !System.IO.Directory.Exists(other) && !System.IO.Directory.Exists(deeper));
                System.IO.File.Delete(late);
                FileOperations.DeleteDeepestFirst(walked);
                Check("an empty tree is cleared", !System.IO.Directory.Exists(root));
            }
            finally
            {
                try { System.IO.Directory.Delete(root, true); } catch { }
            }
        }

        // Bug 3: Properties on "report." would show and edit "report".
        private static async Task PropertiesRefusesDottedNames()
        {
            string? why = null;
            try { await ShellContextMenu.ShowPropertiesAsync(IntPtr.Zero, @"C:\x\report."); }
            catch (System.IO.IOException ex) { why = ex.Message; }
            Check("Properties refuses a name ending in a dot", why == ShellContextMenu.PropertiesMisreadMessage, why);
        }
    }
}
