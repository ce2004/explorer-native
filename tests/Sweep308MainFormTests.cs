using System;
using System.IO;

namespace ExplorerNative
{
    /// <summary>
    /// Main window sweep after 3.0.7. Folders whose name ends in a dot open now,
    /// and everything else the window does to the folder on screen has to reach
    /// that folder too: the idle poll, Backspace, the fallback walk, the watcher,
    /// and New folder or New file inside it. Plus the drive list read again
    /// quietly (Preferences saved) no longer reading its row over the message.
    ///
    /// MainForm is not in the suite, so the decisions are pinned from its
    /// source; the reason behind them is shown on a real folder.
    /// </summary>
    internal static class Sweep308MainFormTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        public static void RunAll(Action<string, bool, string?> check)
        {
            _check = check;
            Console.WriteLine("Main window sweep (3.0.8):");
            TrailingDotFolderIsOnlyReachedExactly();
            SourceRules();
            Console.WriteLine();
        }

        /// <summary>Why the fixes are needed: plain calls lose the dot, exact ones keep it.</summary>
        private static void TrailingDotFolderIsOnlyReachedExactly()
        {
            var root = Path.Combine(Path.GetTempPath(), "ExplorerNativeTests_sweep308_" + Guid.NewGuid().ToString("N")[..8]);
            var dotted = Path.Combine(root, "Backup.");
            try
            {
                Directory.CreateDirectory(NameRules.ExactPath(Path.Combine(dotted, "Disc 1")));

                Check("a folder ending in a dot is not found by a plain Directory.Exists",
                    !Directory.Exists(dotted));
                Check("and is found through NameRules.ExactPath",
                    Directory.Exists(NameRules.ExactPath(dotted)));

                var inner = Path.Combine(dotted, "Disc 1");
                Check("Path.GetDirectoryName keeps the dot (what Backspace now uses)",
                    string.Equals(Path.GetDirectoryName(inner), dotted, StringComparison.OrdinalIgnoreCase),
                    Path.GetDirectoryName(inner));
                Check("Directory.GetParent loses it (what Backspace used to use)",
                    !string.Equals(Directory.GetParent(inner)?.FullName, dotted, StringComparison.OrdinalIgnoreCase),
                    Directory.GetParent(inner)?.FullName);

                using (var watcher = new FileSystemWatcher(NameRules.ExactPath(dotted)))
                {
                    var reported = Path.Combine(watcher.Path, "x.txt");
                    Check("a watcher's exact path made plain matches the listing's rows",
                        string.Equals(NameRules.PlainPath(reported), Path.Combine(dotted, "x.txt"),
                            StringComparison.OrdinalIgnoreCase), reported);
                }

                var made = Path.Combine(dotted, "New folder");
                Directory.CreateDirectory(NameRules.ExactPath(made));
                Check("New folder made exactly lands inside the dotted folder",
                    Directory.Exists(NameRules.ExactPath(made)) &&
                    !Directory.Exists(Path.Combine(root, "Backup", "New folder")));
            }
            catch (Exception ex)
            {
                Check("trailing-dot folder checks ran", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(NameRules.ExactPath(root), recursive: true); } catch { }
            }
        }

        private static void SourceRules()
        {
            var src = Source("MainForm.cs");
            if (src == null) { Check("MainForm.cs found", false); return; }

            var probe = Body(src, "Task<FolderProbe> ProbeDirectoryAsync(");
            Check("the folder probe asks about the exact path",
                probe.Contains("NameRules.ExactPath(", StringComparison.Ordinal), Excerpt(probe));

            Check("nothing in the window takes a parent through Directory.GetParent",
                !src.Contains("Directory.GetParent(", StringComparison.Ordinal));

            var goUp = Body(src, "void GoUp(");
            Check("Backspace takes the parent by text",
                goUp.Contains("SafeParent(", StringComparison.Ordinal), Excerpt(goUp));

            var ancestor = Body(src, "Task<string> NearestExistingAncestorAsync(");
            Check("the fallback walk goes up by text",
                ancestor.Contains("SafeParent(", StringComparison.Ordinal), Excerpt(ancestor));

            var build = Body(src, "RemovalWatch?) BuildWatcher(");
            Check("the watcher is built on the exact path",
                build.Contains("new FileSystemWatcher(NameRules.ExactPath(", StringComparison.Ordinal), Excerpt(build));
            Check("and the rows it reports are made plain before they are matched",
                build.Contains("NameRules.PlainPath(e.FullPath)", StringComparison.Ordinal), Excerpt(build));

            var arm = Body(src, "void ArmWatcher(");
            Check("re-arming compares the watcher's path made plain, so a refresh keeps it",
                arm.Contains("NameRules.PlainPath(pane.Watcher.Path)", StringComparison.Ordinal), Excerpt(arm));

            var register = Body(src, "static RemovalWatch? TryRegister(");
            Check("the eject registration opens the exact folder",
                register.Contains("CreateFileW(NameRules.ExactPath(", StringComparison.Ordinal), Excerpt(register));

            var newFolder = Body(src, "async void CreateNewFolder(");
            Check("New folder creates through the exact path",
                newFolder.Contains("Directory.CreateDirectory(NameRules.ExactPath(", StringComparison.Ordinal),
                Excerpt(newFolder));

            var newFile = Body(src, "async void CreateNewFile(");
            Check("New file creates through the exact path",
                newFile.Contains("new FileStream(NameRules.ExactPath(", StringComparison.Ordinal), Excerpt(newFile));

            var drives = Body(src, "void LoadDrivesInto(");
            Check("the drive list knows when its reload is quiet",
                drives.Contains("bool quiet", StringComparison.Ordinal), Excerpt(drives));
            Check("and a quiet reload on the same drive does not read the row",
                drives.Contains("readRowAloud: !(quiet && sameRow)", StringComparison.Ordinal), Excerpt(drives));
            var navigate = Body(src, "Task<bool> NavigateAsync(");
            int call = navigate.IndexOf("LoadDrivesInto(", StringComparison.Ordinal);
            int callEnd = call < 0 ? -1 : navigate.IndexOf(';', call);
            var callText = callEnd < 0 ? "" : navigate[call..callEnd];
            Check("NavigateAsync hands its quiet on to the drive list",
                callText.Contains("quiet", StringComparison.Ordinal), callText);
        }

        /// <summary>A class-level member's text, from its signature to its closing brace.</summary>
        private static string Body(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return "";
            int end = src.IndexOf("\n        }", start, StringComparison.Ordinal);
            return end < 0 ? src[start..] : src[start..end];
        }

        private static string Excerpt(string text) => text.Length <= 160 ? text : text[..160] + "…";

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }
}
