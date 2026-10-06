using System;
using System.Diagnostics;
using System.IO;

namespace ExplorerNative
{
    /// <summary>
    /// Deleting: the permanent delete that replaced the VB helper, and the shape
    /// of the batched Recycle Bin delete.
    ///
    /// Nothing here sends anything to the real Recycle Bin. The permanent half is
    /// tested against real files, because what it promises (read-only goes, a
    /// junction is never followed, odd names and long paths work) is about the
    /// filesystem and nothing else.
    /// </summary>
    internal static class DeleteTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            Console.WriteLine("Deleting:");

            var top = Path.Combine(Path.GetTempPath(), "en-delete-tests-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(top);
            try
            {
                ReadOnly(top);
                Links(top);
                LongPaths(top);
                DotsAndSpaces(top);
                Failures(top);
            }
            catch (Exception ex) { Check("the delete tests ran to the end", false, ex.ToString()); }
            finally
            {
                try { ShellDelete.DeletePermanently(top); } catch { }
            }

            Batching();
            Console.WriteLine();
        }

        private static string L(string path) => @"\\?\" + path;

        private static bool Gone(string path) => !File.Exists(L(path)) && !Directory.Exists(L(path));

        private static void Try(string what, string path)
        {
            try { ShellDelete.DeletePermanently(path); Check(what, Gone(path)); }
            catch (Exception ex) { Check(what, false, ex.GetType().Name + ": " + ex.Message); }
        }

        private static void ReadOnly(string top)
        {
            var tree = Path.Combine(top, "ro");
            Directory.CreateDirectory(Path.Combine(tree, "sub"));
            File.WriteAllText(Path.Combine(tree, "a.txt"), "x");
            File.WriteAllText(Path.Combine(tree, "sub", "b.txt"), "x");
            File.SetAttributes(Path.Combine(tree, "a.txt"), FileAttributes.ReadOnly);
            File.SetAttributes(Path.Combine(tree, "sub", "b.txt"), FileAttributes.ReadOnly | FileAttributes.Hidden);
            new DirectoryInfo(Path.Combine(tree, "sub")).Attributes |= FileAttributes.ReadOnly;
            new DirectoryInfo(tree).Attributes |= FileAttributes.ReadOnly;
            Try("a folder of read-only and hidden files, itself read-only, is deleted", tree);

            var file = Path.Combine(top, "lonely.txt");
            File.WriteAllText(file, "x");
            File.SetAttributes(file, FileAttributes.ReadOnly);
            Try("a read-only file on its own is deleted", file);
        }

        private static void Junction(string link, string target)
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
        }

        private static void Links(string top)
        {
            var outside = Path.Combine(top, "outside");
            Directory.CreateDirectory(outside);
            var keep = Path.Combine(outside, "keep.txt");
            File.WriteAllText(keep, "precious");
            File.SetAttributes(keep, FileAttributes.ReadOnly);
            bool Intact() => File.Exists(keep) && File.ReadAllText(keep) == "precious" &&
                             (File.GetAttributes(keep) & FileAttributes.ReadOnly) != 0;

            // Directory.Delete meeting a junction asks for it to be unmounted
            // first, which unelevated is refused: the delete has to cope with that.
            var tree = Path.Combine(top, "withlink");
            Directory.CreateDirectory(Path.Combine(tree, "inner"));
            File.WriteAllText(Path.Combine(tree, "inner", "x.txt"), "x");
            Junction(Path.Combine(tree, "inner", "j"), outside);
            Check("(a junction to a folder outside the tree was made)", File.Exists(Path.Combine(tree, "inner", "j", "keep.txt")));
            Try("a tree with a junction in it is deleted", tree);
            Check("and what the junction pointed at is untouched", Intact());

            var both = Path.Combine(top, "withlink-ro");
            Directory.CreateDirectory(both);
            File.WriteAllText(Path.Combine(both, "r.txt"), "x");
            File.SetAttributes(Path.Combine(both, "r.txt"), FileAttributes.ReadOnly);
            Junction(Path.Combine(both, "j"), outside);
            Try("a tree with a junction and a read-only file is deleted", both);
            Check("and the clearing of read-only did not reach through the junction", Intact());

            var lone = Path.Combine(top, "lonejunction");
            Junction(lone, outside);
            Try("a junction chosen on its own is removed", lone);
            Check("and its target is untouched", Intact());
        }

        private static void LongPaths(string top)
        {
            var root = Path.Combine(top, "long");
            var deep = root;
            for (int i = 0; i < 8; i++) deep = Path.Combine(deep, new string((char)('a' + i), 40));
            Directory.CreateDirectory(L(deep));
            File.WriteAllText(L(Path.Combine(deep, "deep.txt")), "x");
            File.SetAttributes(L(Path.Combine(deep, "deep.txt")), FileAttributes.ReadOnly);
            Check("(the path is past 260 characters)", Path.Combine(deep, "deep.txt").Length > 300);
            Try("a tree with a path past 260 characters is deleted", root);

            Directory.CreateDirectory(L(deep));
            File.WriteAllText(L(Path.Combine(deep, "deep.txt")), "x");
            Try("a file at the end of such a path, chosen by itself, is deleted", Path.Combine(deep, "deep.txt"));
            try { ShellDelete.DeletePermanently(root); } catch { }
        }

        private static void DotsAndSpaces(string top)
        {
            var dots = Path.Combine(top, "dots");
            Directory.CreateDirectory(dots);
            Directory.CreateDirectory(L(Path.Combine(dots, "x.", "inner ")));
            File.WriteAllText(L(Path.Combine(dots, "x.", "in.txt")), "x");
            File.WriteAllText(L(Path.Combine(dots, "x.", "inner ", "y.")), "x");
            Directory.CreateDirectory(Path.Combine(dots, "x"));
            File.WriteAllText(Path.Combine(dots, "x", "sibling.txt"), "keep");
            Try("a folder called \"x.\" holding names ending in dots and spaces is deleted", Path.Combine(dots, "x."));
            Check("and the folder \"x\" beside it is not", File.Exists(Path.Combine(dots, "x", "sibling.txt")));

            File.WriteAllText(L(Path.Combine(dots, "report.")), "dot");
            File.WriteAllText(Path.Combine(dots, "report"), "plain");
            Try("a file called \"report.\" is deleted", Path.Combine(dots, "report."));
            Check("and \"report\" beside it is not",
                File.Exists(Path.Combine(dots, "report")) && File.ReadAllText(Path.Combine(dots, "report")) == "plain");

            // Inside a folder whose own name ends in a dot, every path below it
            // needs the exact form too, not only the last name.
            Directory.CreateDirectory(L(Path.Combine(dots, "fold.", "child")));
            File.WriteAllText(L(Path.Combine(dots, "fold.", "child", "z.txt")), "x");
            Directory.CreateDirectory(Path.Combine(dots, "fold", "child"));
            File.WriteAllText(Path.Combine(dots, "fold", "child", "z.txt"), "keep");
            Try("a folder inside \"fold.\" is deleted", Path.Combine(dots, "fold.", "child"));
            Check("and the one inside \"fold\" is not", File.Exists(Path.Combine(dots, "fold", "child", "z.txt")));
        }

        private static void Failures(string top)
        {
            var locked = Path.Combine(top, "locked");
            Directory.CreateDirectory(locked);
            File.WriteAllText(Path.Combine(locked, "open.txt"), "x");
            using (new FileStream(Path.Combine(locked, "open.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { ShellDelete.DeletePermanently(locked); Check("a file held open stops the delete", false); }
                catch (Exception ex) { Check("a file held open stops the delete, with its reason", ex is IOException && ex.Message.Length > 0, ex.Message); }
            }
            Try("and once it is closed the delete goes through", locked);

            try { ShellDelete.DeletePermanently(Path.Combine(top, "nothing-here")); Check("something not there is an error", false); }
            catch (Exception ex) { Check("something not there is an error", ex is FileNotFoundException || ex is DirectoryNotFoundException, ex.Message); }
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private static void Batching()
        {
            var empty = ShellDelete.RecycleMany(Array.Empty<string>(), IntPtr.Zero);
            Check("recycling nothing asks the shell nothing", !empty.Declined && empty.Error == 0);

            var main = Source("MainForm.cs");
            var shell = Source("ShellDelete.cs");
            if (main == null || shell == null) { Check("the delete source can be read", false); return; }

            int start = main.IndexOf("private async void DeleteSelected(", StringComparison.Ordinal);
            int end = start < 0 ? -1 : main.IndexOf("private ", start + 30, StringComparison.Ordinal);
            var delete = start < 0 || end < 0 ? "" : main[start..end];
            Check("the window's delete queues recycles and sends them in one go",
                delete.Contains("toRecycle.Add(path);", StringComparison.Ordinal) &&
                delete.Contains("RecycleAll(toRecycle", StringComparison.Ordinal) &&
                !delete.Contains("ShellDelete.Recycle(", StringComparison.Ordinal));
            Check("and deletes for good through Directory.Delete, not the VB helper",
                delete.Contains("ShellDelete.DeletePermanently(path)", StringComparison.Ordinal) &&
                !main.Contains("FileSystem.DeleteDirectory", StringComparison.Ordinal) &&
                !main.Contains("FileSystem.DeleteFile", StringComparison.Ordinal));
            Check("the dot and space refusal is still there",
                delete.Contains("Windows cannot put a name ending in a dot", StringComparison.Ordinal));

            int batch = shell.IndexOf("public static BatchOutcome RecycleMany", StringComparison.Ordinal);
            int after = batch < 0 ? -1 : shell.IndexOf("public static void DeletePermanently", batch, StringComparison.Ordinal);
            var many = batch < 0 || after < 0 ? "" : shell[batch..after];
            Check("the batch still asks before anything is deleted for good",
                many.Contains("FOF_WANTNUKEWARNING", StringComparison.Ordinal) &&
                many.Contains("FOF_ALLOWUNDO", StringComparison.Ordinal));

            int all = main.IndexOf("private static int RecycleAll(", StringComparison.Ordinal);
            var recycleAll = all < 0 ? "" : main.Substring(all, Math.Min(2000, main.Length - all));
            Check("what the shell kept is found by looking, and a No keeps it",
                recycleAll.Contains("if (!Directory.Exists(path) && !File.Exists(path)) { done++; continue; }", StringComparison.Ordinal) &&
                recycleAll.Contains("batch.Declined", StringComparison.Ordinal));
        }
    }
}
