using System;
using System.IO;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of the window: UI, list, tabs and navigation.
    /// </summary>
    internal static class Sweep500MainATests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the window: UI, list, tabs and navigation:");
            RestartAndUpdateAskAboutDriveUploads();
            await Task.CompletedTask;
        }

        private static string? FindSrc(string name)
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var p = Path.Combine(dir, "src", name);
                if (File.Exists(p)) return p;
                dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            }
            return null;
        }

        private static string Source(string name)
        {
            var p = FindSrc(name);
            Check($"source file {name} found", p != null);
            return p == null ? "" : File.ReadAllText(p);
        }

        /// <summary>The text of a member, from its signature to the next blank-line-free method end (brace matched).</summary>
        private static string Body(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return "";
            int open = src.IndexOf('{', start);
            if (open < 0) return "";
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return src.Substring(start, i - start + 1);
            }
            return src.Substring(start);
        }

        // Bug 1 (and 4, 5): Ctrl+R and Check for updates quit without the tray Exit's
        // "still on its way to Google Drive" question, so in-memory saves were lost.
        private static void RestartAndUpdateAskAboutDriveUploads()
        {
            var main = Source("MainForm.cs");
            var restart = Body(main, "private void RestartApp()");
            int ask = restart.IndexOf("ConfirmUnsavedDrive(", StringComparison.Ordinal);
            int launch = restart.IndexOf("Process.Start(", StringComparison.Ordinal);
            Check("restart asks about Drive uploads before launching the new copy", ask >= 0 && launch > ask);

            var update = Body(main, "private async void CheckForUpdates()");
            int askU = update.IndexOf("ConfirmUnsavedDrive(", StringComparison.Ordinal);
            int install = update.IndexOf("Updater.StartInstall(", StringComparison.Ordinal);
            Check("update asks about Drive uploads before the installer starts", askU >= 0 && install > askU);

            var tray = Source("TrayApplicationContext.cs");
            var confirm = Body(tray, "bool ConfirmUnsavedDrive(");
            Check("the Drive question names the action being taken, not always Quit",
                confirm.Contains("verb", StringComparison.Ordinal) && !confirm.Contains("Quit anyway?", StringComparison.Ordinal));
        }
    }
}
