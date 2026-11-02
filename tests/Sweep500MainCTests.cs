using System;
using System.IO;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of the window: keyboard and actions.
    /// </summary>
    internal static class Sweep500MainCTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the window: keyboard and actions:");
            var main = Source("MainForm.cs");
            if (main == null) { Check("MainForm.cs can be read", false); return; }

            EscapeDuringQuietRerunLeavesResults(main);
            await Task.CompletedTask;
        }

        // Bug 1: a quiet re-run after a rename or delete in results is not a
        // search anybody started; Escape must leave the results, not stop it.
        private static void EscapeDuringQuietRerunLeavesResults(string main)
        {
            var run = Body(main, "private async Task RunSearchAsync(");
            Check("a quiet re-run marks itself as quiet while it runs",
                run.Contains("pane.SearchQuietly = quiet", StringComparison.Ordinal));
            var keys = Body(main, "private void List_KeyDown(");
            int stop = keys.IndexOf("case Keys.Escape when pane.Searching", StringComparison.Ordinal);
            int stopEnd = stop < 0 ? -1 : keys.IndexOf(':', stop);
            string guard = stop < 0 || stopEnd < 0 ? "" : keys.Substring(stop, stopEnd - stop);
            Check("Escape stops only a search somebody started, so a quiet re-run's Escape leaves the results",
                guard.Contains("!pane.SearchQuietly", StringComparison.Ordinal), guard);
        }

        /// <summary>The text of a member from its signature to the next member at the same indentation.</summary>
        internal static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0) return "";
            int next = source.IndexOf("\n        private ", at + signature.Length, StringComparison.Ordinal);
            int next2 = source.IndexOf("\n        internal ", at + signature.Length, StringComparison.Ordinal);
            int next3 = source.IndexOf("\n        public ", at + signature.Length, StringComparison.Ordinal);
            int end = source.Length;
            foreach (var n in new[] { next, next2, next3 }) if (n > 0 && n < end) end = n;
            return source.Substring(at, end - at);
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            try { return File.ReadAllText(Path.Combine(dir.FullName, "src", name)); } catch { return null; }
        }
    }
}
