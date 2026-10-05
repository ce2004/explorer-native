using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// Replacing a binary that something is running from.
    ///
    /// This is the one part of installing that cannot be reasoned about from the
    /// code: Windows refuses to overwrite a mapped executable and allows the same
    /// file to be renamed, and nothing in the type system says so. It is also the
    /// part that failed in the field — an install that depended on the
    /// application closing first, against four instances that would not answer
    /// the pipe asking them to — so it is exercised here against a real running
    /// process rather than against a file with a handle open on it.
    ///
    /// The difference matters and is the reason this test starts a process at
    /// all. A FileStream opened with FileShare.Read also refuses an overwrite,
    /// so it looks like a faithful stand-in; but it refuses the *rename* too,
    /// while a running image allows it. A test built on the cheap imitation would
    /// have asserted the opposite of the behaviour being relied on.
    /// </summary>
    internal static class InstallTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Replacing the installed binary:");

            PlainReplace();
            ReplaceWhileRunning();
            SweepTests();
            WhatIsInstalledTests();
            ShapeTests();
        }

        /// <summary>
        /// Only the application goes, whatever else shares its folder.
        /// </summary>
        private static void WhatIsInstalledTests()
        {
            var dir = NewDir();
            try
            {
                foreach (var name in new[] { "ExplorerNative.exe", "holiday.iso", "notes.txt", "ExplorerNative.pdb" })
                    File.WriteAllText(Path.Combine(dir, name), name);

                var single = AppInstall.FilesToInstall(dir).Select(Path.GetFileName).ToList();
                Equal("a published build installs its executable and nothing beside it",
                    "ExplorerNative.exe", string.Join(", ", single));

                foreach (var name in new[] { "ExplorerNative.dll", "Concentus.dll", "ExplorerNative.deps.json" })
                    File.WriteAllText(Path.Combine(dir, name), name);
                File.WriteAllText(Path.Combine(dir, "ExplorerNative.exe" + AppInstall.RetiredMarker + "1"), "old");

                var multi = AppInstall.FilesToInstall(dir).Select(Path.GetFileName)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                Equal("a build that is not single-file installs the program's files and not the rest",
                    "Concentus.dll, ExplorerNative.deps.json, ExplorerNative.dll, ExplorerNative.exe, ExplorerNative.pdb",
                    string.Join(", ", multi));
            }
            finally { Cleanup(dir); }
        }

        /// <summary>Nothing is holding it: one copy, nothing left behind.</summary>
        private static void PlainReplace()
        {
            var dir = NewDir();
            try
            {
                var source = Path.Combine(dir, "new.bin");
                var target = Path.Combine(dir, "live.bin");

                File.WriteAllText(source, "the new build");
                File.WriteAllText(target, "the old build");

                bool renamed = AppInstall.ReplaceFile(source, target);

                Check("a file nothing is using is replaced without renaming anything", !renamed);
                Equal("and it holds the new bytes", "the new build", File.ReadAllText(target));
                Check("with nothing left beside it",
                    Directory.GetFiles(dir).Length == 2,
                    string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// The case the whole design exists for.
        /// </summary>
        private static void ReplaceWhileRunning()
        {
            var dir = NewDir();
            Process? victim = null;

            try
            {
                // A real executable, really running. PowerShell is used because it
                // is on every Windows machine and will sit still when told to.
                var host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");

                if (!File.Exists(host))
                {
                    Check("a host executable to run from is available", false, host);
                    return;
                }

                var target = Path.Combine(dir, "live.exe");
                File.Copy(host, target);

                victim = Process.Start(new ProcessStartInfo(target)
                {
                    Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                Check("the stand-in process started", victim != null);
                if (victim == null) return;

                // Give it a moment to map its own image.
                for (int i = 0; i < 50 && !IsLocked(target); i++) Thread.Sleep(100);

                Check("and Windows will not let its executable be overwritten", IsLocked(target),
                    "the premise of the whole design does not hold on this machine");

                var source = Path.Combine(dir, "new.bin");
                File.WriteAllText(source, "the new build");

                bool renamed = AppInstall.ReplaceFile(source, target);

                Check("the install still happens, by renaming the old one aside", renamed);
                Equal("and the path now holds the new bytes", "the new build", File.ReadAllText(target));

                Check("the process that was running has not been disturbed", !victim.HasExited);

                var retired = Directory.GetFiles(dir)
                    .Where(f => Path.GetFileName(f).Contains(AppInstall.RetiredMarker, StringComparison.Ordinal))
                    .ToList();

                Check("the old binary is beside it under a retired name", retired.Count == 1,
                    string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));

                // Not ".exe" any more, so nothing offers to run it and nobody
                // starts the wrong one out of a folder listing.
                Check("and that name is not something anybody can double-click into a second copy",
                    retired.Count == 1 &&
                    !Path.GetFileName(retired[0]).EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                    retired.Count == 1 ? Path.GetFileName(retired[0]) : "");

                Check("nothing half-written is left over",
                    !Directory.GetFiles(dir).Any(f =>
                        Path.GetFileName(f).Contains(".installing-", StringComparison.Ordinal)));

                // While it is still running, its own file cannot go — which is
                // why the sweep is best-effort and runs again at startup.
                Equal("the retired binary cannot be swept while it is still mapped",
                    "0", AppInstall.SweepRetired(dir).ToString());

                victim.Kill();
                victim.WaitForExit(10_000);

                // Windows can take a moment to release the section after exit.
                int swept = 0;
                for (int i = 0; i < 50 && swept == 0; i++)
                {
                    swept = AppInstall.SweepRetired(dir);
                    if (swept == 0) Thread.Sleep(100);
                }

                Equal("and it is swept once the process it belonged to has gone", "1", swept.ToString());
            }
            finally
            {
                try { if (victim is { HasExited: false }) victim.Kill(); } catch { }
                victim?.Dispose();
                Cleanup(dir);
            }
        }

        private static void SweepTests()
        {
            var dir = NewDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "ExplorerNative.exe"), "keep me");
                File.WriteAllText(Path.Combine(dir, "ExplorerNative.exe" + AppInstall.RetiredMarker + "1"), "old");
                File.WriteAllText(Path.Combine(dir, "ExplorerNative.exe" + AppInstall.RetiredMarker + "2"), "older");
                File.WriteAllText(Path.Combine(dir, "ExplorerNative.exe.installing-3"), "half written");
                File.WriteAllText(Path.Combine(dir, "settings-of-some-kind.json"), "not ours to delete");

                Equal("every retired and half-written file is swept", "3", AppInstall.SweepRetired(dir).ToString());

                Check("the live executable is untouched", File.Exists(Path.Combine(dir, "ExplorerNative.exe")));
                Check("and so is anything that is not one of ours",
                    File.Exists(Path.Combine(dir, "settings-of-some-kind.json")));

                Equal("sweeping again finds nothing", "0", AppInstall.SweepRetired(dir).ToString());
                Equal("and a folder that does not exist is not an error",
                    "0", AppInstall.SweepRetired(Path.Combine(dir, "nowhere")).ToString());
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// The rules the install path lives by, read out of the source.
        ///
        /// Program.cs cannot be compiled into this suite — it owns Main — so the
        /// things that must stay true about it are checked as text, the same way
        /// the pane-naming rule is.
        /// </summary>
        private static void ShapeTests()
        {
            var program = Source("Program.cs");
            var install = Source("AppInstall.cs");

            if (program == null || install == null)
            {
                Check("Program.cs and AppInstall.cs can be read", false);
                return;
            }

            // The failure that cost three minutes and told nobody anything.
            Check("nothing on a command-line path puts up a MessageBox any more",
                !program.Contains("MessageBox.Show(message", StringComparison.Ordinal));

            Check("a notice from a command-line run closes itself",
                program.Contains("NoticeMilliseconds", StringComparison.Ordinal) &&
                program.Contains("timer.Stop(); form.Close();", StringComparison.Ordinal));

            Check("every outcome is written to a log that outlives the window",
                program.Contains("WriteLog(title, message);", StringComparison.Ordinal));

            Check("a failure sets an exit code a script can read",
                program.Contains("Environment.ExitCode = 1;", StringComparison.Ordinal));

            Check("the console is used when there is one to use",
                program.Contains("AttachConsole(AttachParentProcess)", StringComparison.Ordinal));

            // The dependency that broke.
            Check("installing no longer waits for the application to close first",
                !program.Contains("bool steppedAside = AskRunningInstanceToExit();", StringComparison.Ordinal));

            Check("and the message that blamed the user for a window that had already gone is gone with it",
                !install.Contains("Close Explorer Native and try again", StringComparison.Ordinal));

            // "Has been restarted", said about a copy still running beside the new
            // one: the stand-down was judged by whether the installed path could
            // be opened, and after a rename the old process holds a different file.
            var standDown = Between(program, "private static StandDown AskRunningInstanceToExit()",
                "private static bool HasExited(");
            Check("a stand-down is judged by the processes, not by the installed file",
                standDown != null &&
                standDown.Contains("others.All(HasExited)", StringComparison.Ordinal) &&
                !standDown.Contains("AppInstall.ExePath,", StringComparison.Ordinal));
            Check("and the new copy is started only once the single-instance lock is free",
                program.Contains("old != StandDown.StillRunning", StringComparison.Ordinal) &&
                standDown != null && standDown.Contains("PrimaryLockIsFree()", StringComparison.Ordinal));
            Check("and the lock it tests is the one Main takes",
                program.Contains("new Mutex(false, SingleInstanceMutexName)", StringComparison.Ordinal) &&
                !program.Contains("new Mutex(false, \"ExplorerNative_SingleInstance\")", StringComparison.Ordinal));

            // The proof the old procedure asked a person to do by hand.
            Check("the installer hashes what it wrote and compares it",
                install.Contains("var wanted = HashOf(", StringComparison.Ordinal) &&
                install.Contains("var landed = HashOf(ExePath);", StringComparison.Ordinal));

            Check("a mismatch is a failure rather than a success with a caveat",
                install.Contains("does not match the one that was installed", StringComparison.Ordinal));

            // The rollback. Losing the executable is worse than a failed install:
            // it is the registered handler for every folder on the machine.
            Check("a failed swap puts the old binary back",
                install.Contains("try { File.Move(retired, target); } catch { }", StringComparison.Ordinal));
        }

        private static bool IsLocked(string path)
        {
            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException) { return true; }
            catch { return false; }
        }

        private static string? Source(string name)
        {
            var here = new DirectoryInfo(AppContext.BaseDirectory);

            for (int up = 0; up < 8 && here != null; up++, here = here.Parent)
            {
                var candidate = Path.Combine(here.FullName, "src", name);
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
            }

            return null;
        }

        /// <summary>The text from <paramref name="start"/> up to <paramref name="end"/>, or null.</summary>
        private static string? Between(string text, string start, string end)
        {
            int from = text.IndexOf(start, StringComparison.Ordinal);
            if (from < 0) return null;
            int to = text.IndexOf(end, from, StringComparison.Ordinal);
            return to < 0 ? null : text[from..to];
        }

        private static string NewDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-install-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
