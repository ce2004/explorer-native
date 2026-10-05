using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ExplorerNative
{
    internal static class Program
    {
        public const string MainWindowTitle = "Explorer Native";

        /// <summary>Held for its whole life by the copy that is the application.</summary>
        private const string SingleInstanceMutexName = "ExplorerNative_SingleInstance";

        /// <summary>
        /// What to call the application in a dialog raised from here.
        ///
        /// The two callers below — <see cref="Deliver"/> for the command-line
        /// flags and <see cref="ReportCrash"/> — have no Settings to hand: one
        /// runs before the settings are loaded and the other runs when things
        /// have already gone wrong, possibly during startup. So the configured
        /// name is remembered as it goes past instead, and this falls back to the
        /// real one until it has. Everywhere that *does* have a Settings uses
        /// <see cref="Settings.DisplayTitle"/> directly.
        /// </summary>
        public static string DisplayTitle { get; private set; } = MainWindowTitle;

        /// <summary>Records the name to use, whenever settings are loaded or changed.</summary>
        public static void RememberTitle(Settings settings)
        {
            try { DisplayTitle = settings.DisplayTitle; } catch { }
        }

        private static TrayApplicationContext? _context;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_RESTORE = 9;

        [STAThread]
        private static void Main(string[] args)
        {
            // Before a command-line run decides what to say, work out where it can
            // say it. A run started from PowerShell has a console one process up;
            // a run started by double-clicking has none, and only that second case
            // should ever put a window on screen.
            //
            // **Only** for a command-line run, and this is not a nicety. It used
            // to happen on every launch, and a file manager that attaches itself
            // to whatever console started it then holds that console for as long
            // as it runs — which, for the copy the installer starts, is for ever.
            // The terminal it was installed from froze and had to be closed, over
            // and over, and closing it sent the console's close event to the file
            // manager as well. A GUI application has no business in a console it
            // is not writing to.
            _quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
            if (IsCommandLineRun(args)) AttachToParentConsole();

            // Registration flags do their work and exit without starting a UI, so
            // the takeover can be undone from a command line even if the app
            // itself will not start.
            if (HandleRegistrationOnlyRun(args)) return;

            // A restart launches the replacement while this process is still
            // alive, so it must wait for the lock rather than treat it as
            // "already running" and quit.
            bool isRestart = args.Any(a => a.Equals("--restart", StringComparison.OrdinalIgnoreCase));

            using var mutex = new Mutex(false, SingleInstanceMutexName);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(isRestart ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                // The previous holder died without releasing; the lock is ours.
                acquired = true;
            }

            if (!acquired)
            {
                // A restart is a copy taking over from one that is leaving; a
                // window found now belongs to that one, and raising it opens
                // nothing.
                if (HandOffToRunningInstance(args, allowWindow: !isRestart)) return;

                // Nobody answered and there was no window to raise, which means
                // the copy holding the lock is on its way out — it stops
                // answering the pipe as soon as quitting begins, and lets the
                // lock go at the very end. Waiting a few seconds for it turns
                // "the folder I double-clicked opened nothing" into "the folder
                // opened, a moment later".
                // Thirty, not five: quitting has three bounded waits of up to
                // five seconds each, and a launcher that gave up first opened
                // nothing. In slices, trying the handoff again between them: two
                // launches waiting together, one of them wins the lock and
                // becomes the application, and the other hands its folder to it
                // rather than waiting out the thirty seconds for nothing.
                var until = DateTime.UtcNow.AddSeconds(30);
                while (!acquired && DateTime.UtcNow < until)
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(500)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    // The pipe only: a window found now may belong to a copy that
                    // is closing — a transfer window stays up through teardown —
                    // and raising it is not handing the folder over.
                    if (!acquired && SingleInstance.SendToRunningInstance(
                            FirstPathArgument(args) ?? SingleInstance.ShowCommand)) return;
                }

                if (!acquired) return;
            }

            try
            {
                Run(args);
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }

        /// <summary>
        /// Gives the folder we were launched with to the copy already running, and
        /// brings it to the front.
        ///
        /// Once this app is the default handler for folders, this is the path most
        /// launches take: the shell starts a fresh copy for every folder opened.
        /// The old code only looked for a window by title, which finds nothing at
        /// all when the running instance is sitting in the tray with no window —
        /// so the folder was silently dropped and nothing happened.
        /// </summary>
        /// <summary>
        /// Hands the folder over, and says whether anybody took it. False means
        /// the caller should consider becoming the application itself.
        /// </summary>
        private static bool HandOffToRunningInstance(string[] args, bool allowWindow = true)
        {
            var folder = FirstPathArgument(args);
            if (SingleInstance.SendToRunningInstance(folder ?? SingleInstance.ShowCommand)) return true;
            if (!allowWindow) return false;

            // The pipe was not answered — an older build, an instance still
            // starting up, or one already shutting down. Fall back to raising its
            // window.
            var existing = FindRunningInstanceWindow();
            if (existing != IntPtr.Zero)
            {
                ShowWindow(existing, SW_RESTORE);
                SetForegroundWindow(existing);
                return true;
            }

            return false;
        }

        /// <summary>
        /// The main window of the copy already running, found by process rather
        /// than by window title.
        ///
        /// The title is a setting now, so looking one up by name was a fallback
        /// that stopped working the moment anybody renamed their window — which is
        /// to say, a fallback that worked only when it was least likely to be
        /// needed. The process identity does not change.
        /// </summary>
        private static IntPtr FindRunningInstanceWindow()
        {
            try
            {
                using var me = Process.GetCurrentProcess();
                foreach (var other in Process.GetProcessesByName(me.ProcessName))
                {
                    using (other)
                    {
                        if (other.Id == me.Id) continue;

                        var handle = other.MainWindowHandle;
                        if (handle != IntPtr.Zero) return handle;
                    }
                }
            }
            catch { }

            return IntPtr.Zero;
        }

        /// <summary>
        /// The switches that do their work and exit without a window — the only
        /// runs that are allowed to borrow a console.
        /// </summary>
        internal static readonly string[] CommandLineSwitches =
        {
            "--licence", "--license", "--install", "--install-only", "--update", "--uninstall",
            "--register-default", "--unregister-default", "--unregister-all",
        };

        private static bool IsCommandLineRun(string[] args) =>
            args.Any(a => CommandLineSwitches.Contains(a.Trim(), StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// Handles --register-default / --unregister-default / --unregister-all.
        /// Returns true when the process has done its job and should stop.
        /// </summary>
        private static bool HandleRegistrationOnlyRun(string[] args)
        {
            foreach (var raw in args)
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "--licence":
                    case "--license":
                    {
                        // The NVDA controller client is LGPL and its licence has to
                        // travel with it. There is no folder beside the executable
                        // to keep it in any more, so it rides inside and comes back
                        // out on request.
                        WriteLicenceBeside();
                        return true;
                    }

                    case "--install":
                        return RunInstall(register: true);

                    case "--install-only":
                        return RunInstall(register: false);

                    // Help, Check for updates: --install-only, and the copy that
                    // asked for it is restarted on the new build even when it
                    // is not the installed copy (one run from Downloads, say).
                    case "--update":
                        return RunInstall(register: false, restartRunning: true);

                    case "--uninstall":
                    {
                        var settings = LoadForChange(out bool readable);
                        settings.SetAsDefaultFileExplorer = false;
                        settings.RegisterContextMenu = false;
                        settings.StartWithWindows = false;
                        ShellRegistration.UnregisterEverything();
                        if (readable) settings.Save();

                        // Before asking it to close, for the reason above: its
                        // save-on-exit would otherwise put the takeover and the
                        // startup entry back, and the next launch would reinstall
                        // the program directory this is about to delete. Carried
                        // in the request, so it holds when the file cannot be read.
                        TellRunningInstance(SingleInstance.ReleaseAllCommand);

                        AskRunningInstanceToExit();

                        // Deregistering has already happened and cannot fail
                        // usefully; removing the files can, and silently. The
                        // two ways it does are worth telling apart, because one
                        // of them is fixed by running the command differently:
                        // uninstalling *with* the installed copy leaves it in
                        // place, and saying "done" then is a lie that leaves a
                        // program directory nobody expects to still be there.
                        if (!AppInstall.Uninstall())
                            Fail("Could not finish uninstalling",
                                AppInstall.IsRunningInstalledCopy
                                    ? "The installed copy cannot delete itself. Run --uninstall " +
                                      "from a build outside " + AppInstall.Directory + ", or delete " +
                                      "that folder by hand. Windows no longer opens folders with " +
                                      "Explorer Native either way."
                                    : "Windows no longer opens folders with Explorer Native, but " +
                                      AppInstall.Directory + " could not be removed. Something in " +
                                      "it is probably still open.");
                        return true;
                    }

                    case "--register-default":
                    {
                        // The registry names one path for every folder on the
                        // machine, and before anything is installed that path is
                        // this process — a build output the next rebuild replaces
                        // and the next clean deletes. Install first, as ticking
                        // the box in Preferences does.
                        if (!AppInstall.IsInstalled)
                        {
                            var installed = AppInstall.Install();
                            if (!installed.Ok)
                            {
                                Fail("Could not install",
                                    "Folders were not handed to this build, because nothing is installed for " +
                                    "them to open. " + (installed.Problem ?? ""));
                                return true;
                            }
                        }

                        // Not registered against a file that says otherwise: the
                        // running copy would take it back on its next reconcile.
                        var settings = LoadForChange(out bool readable);
                        if (!readable) return true;
                        settings.SetAsDefaultFileExplorer = true;
                        settings.FoldersTakenAt = DateTime.UtcNow.Ticks;
                        ShellRegistration.SetDefaultFileExplorer(true);
                        settings.RegisteredExePath = ShellRegistration.ExePath;
                        settings.Save();
                        TellRunningInstanceSettingsChanged();
                        return true;
                    }
                    case "--unregister-default":
                    {
                        // The way out when the application is unreachable, so the
                        // registry is handed back whether or not the file reads.
                        var settings = LoadForChange(out bool readable);
                        settings.SetAsDefaultFileExplorer = false;
                        ShellRegistration.SetDefaultFileExplorer(false);
                        if (readable) settings.Save();
                        TellRunningInstance(SingleInstance.ReleaseFoldersCommand);
                        return true;
                    }
                    case "--unregister-all":
                    {
                        var settings = LoadForChange(out bool readable);
                        settings.SetAsDefaultFileExplorer = false;
                        settings.RegisterContextMenu = false;
                        settings.StartWithWindows = false;
                        ShellRegistration.UnregisterEverything();
                        if (readable) settings.Save();
                        TellRunningInstance(SingleInstance.ReleaseAllCommand);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Copies this build to the install directory and, optionally, points
        /// Windows' folder handling at it.
        ///
        /// Installing is what makes the registration survive a rebuild: the
        /// registry then names a copy in the user's program directory that no
        /// build ever touches, instead of a path inside bin\ that the next
        /// publish replaces and the next clean deletes.
        /// </summary>
        private static bool RunInstall(bool register, bool restartRunning = false)
        {
            // Nothing is asked to close first any more, and that is the change.
            //
            // The install used to send <exit> down the single-instance pipe and
            // wait, because a running image cannot be overwritten. That made a
            // file copy depend on inter-process co-operation — and therefore on
            // the very thing that had already gone wrong the day it mattered:
            // four instances were running with no pipe server among them, the
            // pipe went unanswered, "nobody answered" was read as "nothing is
            // running", and the copy then failed against a file all four were
            // holding. AppInstall renames the old binary aside instead, which
            // needs no co-operation from anybody. See AppInstall.Replace.
            bool wasRunning = AppInstall.InstalledCopyIsRunning();

            var result = AppInstall.Install();

            if (!result.Ok)
            {
                Fail("Could not install", result.Problem ?? "Unknown problem.");
                return true;
            }

            // The files are installed either way; only what is recorded about
            // them waits for a settings file that can be read.
            var settings = LoadForChange(out bool readable);
            if (readable)
            {
                if (register)
                {
                    settings.SetAsDefaultFileExplorer = true;
                    settings.FoldersTakenAt = DateTime.UtcNow.Ticks;
                    ShellRegistration.SetDefaultFileExplorer(true);
                }
                settings.RegisteredExePath = ShellRegistration.ExePath;
                settings.Save();
            }

            // And tell the copy that is running, or it writes its own older
            // settings straight back over these.
            //
            // It loaded them at startup and saves them again on the way out, and
            // the way out is the very next thing this method asks it to do — so
            // `--install` registered the folder takeover, wrote
            // SetAsDefaultFileExplorer true, and then had it set back to false by
            // the instance standing down. The copy started immediately afterwards
            // read that false and deregistered every folder on the machine, a
            // second or two after reporting a successful install. Every other
            // command-line flag that edits settings already sends this.
            TellRunningInstanceSettingsChanged();

            // Said plainly, including the part that is not finished.
            //
            // An install that reports success while the application somebody is
            // looking at is still the previous build is the exact shape of "a
            // change that is only built is not shipped", one step further along:
            // it is shipped, and not running. Whoever reads this needs to know
            // which of those they have.
            var said = $"Installed to {result.ExePath}";
            if (result.Hash != null) said += $"\nSHA-256 {result.Hash}";

            var old = StandDown.Gone;

            if (result.RunningCopyIsStale || restartRunning)
            {
                // It could not be overwritten, so it was renamed aside and a
                // live process is still executing the old file. Ask that process
                // to stand down — politely, so it saves its state and unmounts
                // Google Drive — and start the new one in its place. This is the
                // *nice to have* now rather than the thing the install rests on:
                // if nobody answers, the install has still happened.
                old = AskRunningInstanceToExit();

                said += old switch
                {
                    StandDown.Gone =>
                        "\nThe copy that was running has been restarted on the new build.",
                    StandDown.LetGo =>
                        "\nThe copy that was running has stood down and the new build has been " +
                        "started. The old process has not finished exiting — Windows is still " +
                        "holding it — but it no longer holds anything the new one needs.",
                    _ =>
                        "\nA copy is still running from the previous build. It will pick this " +
                        "up the next time it starts; nothing else is outstanding.",
                };
            }

            // Start the copy that was just installed, so whoever ran this ends up
            // running the thing the registry now names rather than the build they
            // invoked. Skipped while the old copy still holds the single-instance
            // lock, because a launch then only hands its request to that copy.
            if (old != StandDown.StillRunning)
            {
                try
                {
                    // Through the shell, so the new copy inherits nothing from this
                    // one: no console, no standard handles. With UseShellExecute
                    // false it inherited both, and an installer run from a
                    // terminal left the file manager holding that terminal's
                    // output pipe open for as long as it ran — so whatever was
                    // waiting on the terminal waited for ever.
                    Process.Start(new ProcessStartInfo(result.ExePath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    said += $"\nInstalled, but it could not be started: {ex.Message}";
                }
            }

            if (!wasRunning && result.RunningCopyIsStale)
                said += "\n(Something was holding the executable that was not answering the pipe.)";

            Say("Installed", said);
            return true;
        }

        /// <summary>
        /// How long to wait for a running instance to let go of its own files.
        ///
        /// Four seconds, and it was not enough. The installed copy is a
        /// self-contained single file — around a hundred and sixty megabytes the
        /// host has memory-mapped — and on the way out it takes Google Drive
        /// down with it: clearing the sync root's placeholders, disconnecting the
        /// provider, removing the drive letter. None of that is instant, and the
        /// wait expired in the middle of it.
        ///
        /// What made it worth finding was the message that followed.
        /// <c>AppInstall</c> saw the executable still locked and said
        /// "ExplorerNative.exe is in use. Close Explorer Native and try again" —
        /// which is wrong twice over: the install had just asked it to close, and
        /// it was in the act of closing. Following that instruction does nothing,
        /// because there is nothing left to close by the time anyone reads it.
        /// </summary>
        private const int ShutdownWaitMilliseconds = 30_000;

        /// <summary>What became of the copy that was asked to stand down.</summary>
        private enum StandDown
        {
            /// <summary>Every other copy has exited, or there was none.</summary>
            Gone,

            /// <summary>
            /// The single-instance lock is free, so a new launch becomes the
            /// application — but a process is still there, not finished exiting.
            /// </summary>
            LetGo,

            /// <summary>It still holds the lock; a new launch would only hand off to it.</summary>
            StillRunning,
        }

        /// <summary>
        /// How long the lock has to have been free, with the old process still
        /// present, before that is taken as the answer rather than waited out.
        /// </summary>
        private const int LingerMilliseconds = 5_000;

        /// <summary>
        /// Asks every other running copy to shut down and watches the processes
        /// themselves, not a file.
        ///
        /// It used to wait for the installed path to be writable, which was
        /// right when the install overwrote in place and meaningless once it
        /// renamed the old binary aside: the old process holds the *retired*
        /// file, the path is the new one, and the probe succeeded on its first
        /// try. So the report said "has been restarted" about a copy that was
        /// still there — measured, the old process alive beside the new one
        /// with its runtime gone and four threads parked in the kernel — and
        /// the new launch raced a lock the old one might not yet have released,
        /// which ends with the launch handing its request to a dying process
        /// and nothing running at all.
        ///
        /// The two questions are separate now. Whether the processes have gone
        /// is what the report says; whether the single-instance lock is free is
        /// what decides if starting the new one makes it the application. A
        /// process can finish the second long before the first — Windows keeps
        /// a process that still has I/O out, and one waiting on a cloud file
        /// can have it out for a minute.
        /// </summary>
        private static StandDown AskRunningInstanceToExit()
        {
            Process[] others;
            try
            {
                others = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppInstall.ExePath))
                                .Where(p => p.Id != Environment.ProcessId)
                                .ToArray();
            }
            catch { others = Array.Empty<Process>(); }

            try
            {
                // Nobody listening is not "nothing running" — the incident this
                // installer was redesigned around was four copies with no pipe
                // server among them. Only an answered request is worth waiting on.
                bool asked;
                try { asked = SingleInstance.SendToRunningInstance(SingleInstance.ExitCommand); }
                catch { asked = false; }

                if (asked)
                {
                    long deadline = Environment.TickCount64 + ShutdownWaitMilliseconds;
                    long freeSince = -1;

                    while (Environment.TickCount64 < deadline && !others.All(HasExited))
                    {
                        Thread.Sleep(100);

                        if (!PrimaryLockIsFree()) { freeSince = -1; continue; }
                        if (freeSince < 0) freeSince = Environment.TickCount64;
                        else if (Environment.TickCount64 - freeSince >= LingerMilliseconds) break;
                    }
                }

                if (others.All(HasExited)) return StandDown.Gone;
                return PrimaryLockIsFree() ? StandDown.LetGo : StandDown.StillRunning;
            }
            finally
            {
                foreach (var p in others) p.Dispose();
            }
        }

        private static bool HasExited(Process process)
        {
            try { return process.HasExited; }
            catch { return true; }
        }

        /// <summary>
        /// Whether a launch now would become the application rather than a
        /// messenger. Taken and given straight back; the name is the one
        /// <see cref="Main"/> takes.
        /// </summary>
        private static bool PrimaryLockIsFree()
        {
            try
            {
                using var mutex = new Mutex(false, SingleInstanceMutexName);
                bool got;
                try { got = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { got = true; }

                if (got) mutex.ReleaseMutex();
                return got;
            }
            catch { return false; }
        }

        /// <summary>
        /// Everything in here that somebody else wrote, and whose licence has to
        /// travel with the binary.
        ///
        /// Three now, not one. The NVDA controller client is LGPL; Concentus and
        /// NVorbis are the managed Opus and Vorbis decoders, BSD and MIT, and
        /// they are in the executable because Windows will not decode those
        /// formats for a desktop application. All three ride inside the single
        /// file and come back out on request, because there is no folder beside
        /// the executable to keep them in.
        /// </summary>
        private static readonly string[] LicenceResources =
        {
            "NVDA-controllerClient-LICENSE.txt",
            "Concentus-LICENSE.txt",
            "NVorbis-LICENSE.txt",
        };

        private static void WriteLicenceBeside()
        {
            try
            {
                var folder = Path.GetDirectoryName(Application.ExecutablePath) ?? ".";
                var written = new List<string>();
                var missing = new List<string>();

                foreach (var name in LicenceResources)
                {
                    using var stream = typeof(Program).Assembly.GetManifestResourceStream(name);
                    if (stream == null) { missing.Add(name); continue; }

                    var target = Path.Combine(folder, name);
                    using (var file = File.Create(target)) stream.CopyTo(file);
                    written.Add(target);
                }

                // Both halves reported. A build that quietly shipped without one
                // of these is exactly the thing this switch exists to make
                // checkable, so saying only what worked would defeat it.
                var said = written.Count == 0
                    ? "No licence text is present in this build."
                    : "Licences written to:\n" + string.Join("\n", written);

                if (missing.Count > 0)
                    said += "\n\nMissing from this build: " + string.Join(", ", missing);

                Say("Licence", said);
            }
            catch (Exception ex) { Fail("Licence", ex.Message); }
        }

        /// <summary>
        /// How long a notice from a command-line run stays on screen before it
        /// closes itself.
        ///
        /// It closes itself because the alternative cost three minutes and told
        /// nobody anything. A MessageBox waits for a person; an install is
        /// overwhelmingly started by a script, and on a machine where nobody is
        /// looking at the screen — a remote session, a locked workstation, a
        /// build step — that wait is unbounded. The caller sat out its own
        /// timeout, killed the process, and had no idea whether the install had
        /// worked, because the only report was behind a button nobody could
        /// press.
        ///
        /// A minute is long enough to read a sentence and short enough that
        /// nothing is ever blocked on it for long. The message is on the console
        /// and in the log either way, so nothing is lost when it goes.
        /// </summary>
        private const int NoticeMilliseconds = 60_000;

        /// <summary>True once <see cref="AttachToParentConsole"/> has found one.</summary>
        private static bool _console;

        private static bool _quiet;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        private const int AttachParentProcess = -1;

        /// <summary>
        /// Borrows the console of whatever started this, if there was one.
        ///
        /// A WinExe has no console of its own — that is what makes it a WinExe —
        /// but when it is launched from PowerShell or cmd there is one right
        /// there belonging to the parent, and writing to it is what every other
        /// command-line tool does. .NET has already decided its standard output
        /// is going nowhere by this point, so the streams are re-opened after
        /// attaching or nothing appears.
        /// </summary>
        private static void AttachToParentConsole()
        {
            try
            {
                if (!AttachConsole(AttachParentProcess)) return;

                var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(output);

                var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                Console.SetError(error);

                _console = true;
            }
            catch { _console = false; }
        }

        /// <summary>
        /// Where every command-line run writes what it did, whether or not
        /// anybody was watching.
        ///
        /// This is the part that makes the outcome findable after the fact. A
        /// dialog is gone the moment it is dismissed and a console scrolls away;
        /// a line in a file is still there tomorrow, which is when somebody
        /// usually asks why the change they made is not in the application.
        /// </summary>
        private static string LogPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ExplorerNative", "install.log");

        private static void WriteLog(string title, string message)
        {
            try
            {
                var folder = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(folder)) System.IO.Directory.CreateDirectory(folder);

                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {title}: " +
                    message.Replace("\r", " ").Replace("\n", " ") + Environment.NewLine);
            }
            catch
            {
                // A diagnostic that cannot be written is not a reason to stop.
            }
        }

        /// <summary>Something worked, and here is what happened.</summary>
        private static void Say(string title, string message) => Deliver(title, message, failed: false);

        /// <summary>
        /// Something did not work. Sets the exit code, which is the only part of
        /// this a script can act on.
        /// </summary>
        /// <summary>
        /// The settings, for a command-line change, and whether they were read.
        /// When the file is there and could not be read — held by another
        /// program, or damaged, in which case a copy was kept beside it — that
        /// has been said, and nothing may be saved: the defaults would go over
        /// every setting in it, and the running copy would take them on too.
        /// </summary>
        private static Settings LoadForChange(out bool readable)
        {
            var settings = Settings.Load(out bool failed);
            readable = !failed;
            if (failed)
                Fail("Could not read the settings",
                    "settings.json is there and could not be read — another program may have it open, " +
                    "or it is damaged and a copy was kept beside it — so the settings were not changed.");
            return settings;
        }

        private static void Fail(string title, string message)
        {
            Environment.ExitCode = 1;
            Deliver(title, message, failed: true);
        }

        /// <summary>
        /// The log always, the console when there is one, and a dialog only when
        /// there is neither — and even then, not for ever.
        ///
        /// The order matters. The log is written first because it is the one
        /// that cannot fail to reach anybody; the dialog is last because it is
        /// the one that can stop the world.
        /// </summary>
        private static void Deliver(string title, string message, bool failed)
        {
            WriteLog(title, message);

            if (_console)
            {
                try
                {
                    var writer = failed ? Console.Error : Console.Out;
                    writer.WriteLine($"{title}: {message}");
                }
                catch { }
                return;
            }

            if (_quiet) return;

            ShowNotice(title, message);
        }

        /// <summary>
        /// A notice that closes itself, for the case where there is no console
        /// and somebody may or may not be in front of the screen.
        ///
        /// Built by hand rather than with MessageBox because MessageBox has no
        /// way to be dismissed by anything except a person, and that is the
        /// property being removed. The text is in a read-only box reporting
        /// itself as static text — the same arrangement ProgressForm uses — so a
        /// screen reader reads the sentence rather than announcing an edit
        /// field, and so it can be selected and copied.
        /// </summary>
        private static void ShowNotice(string title, string message)
        {
            try
            {
                using var form = new Form
                {
                    Text = $"{DisplayTitle} — {title}",
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false,
                    ShowInTaskbar = true,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Padding = new Padding(14),
                };

                var text = new TextBox
                {
                    Text = message,
                    ReadOnly = true,
                    Multiline = true,
                    BorderStyle = BorderStyle.None,
                    BackColor = System.Drawing.SystemColors.Control,
                    Width = 460,
                    Height = 110,
                    ScrollBars = ScrollBars.Vertical,
                    AccessibleName = title,
                    AccessibleRole = AccessibleRole.StaticText,
                };

                var ok = new Button { Text = "&OK", DialogResult = DialogResult.OK, AutoSize = true };

                var layout = new TableLayoutPanel
                {
                    ColumnCount = 1,
                    RowCount = 2,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Fill,
                };
                layout.Controls.Add(text, 0, 0);
                layout.Controls.Add(ok, 0, 1);

                form.Controls.Add(layout);
                form.AcceptButton = ok;
                form.CancelButton = ok;

                using var timer = new System.Windows.Forms.Timer { Interval = NoticeMilliseconds };
                timer.Tick += (_, _) => { timer.Stop(); form.Close(); };
                form.Shown += (_, _) => { timer.Start(); ok.Focus(); };

                form.ShowDialog();
            }
            catch
            {
                // Nothing left to try. The log has it.
            }
        }

        /// <summary>
        /// Tells a live instance that settings.json has changed underneath it.
        ///
        /// It loaded its settings at startup and writes them back on exit, so
        /// without this the change made here is overwritten the moment the user
        /// closes the window — and the next launch reconciles the registry
        /// against the stale value, undoing the registration as well.
        /// </summary>
        private static void TellRunningInstanceSettingsChanged() =>
            TellRunningInstance(SingleInstance.ReloadSettingsCommand);

        private static void TellRunningInstance(string command)
        {
            try { SingleInstance.SendToRunningInstance(command); }
            catch { /* nothing running is the normal case */ }
        }

        /// <summary>The first argument that names somewhere on disk, if any.</summary>
        private static string? FirstPathArgument(string[] args)
        {
            foreach (var arg in args)
            {
                if (arg.StartsWith("--", StringComparison.Ordinal)) continue;

                var candidate = NameRules.NormaliseLaunchPath(arg);
                if (candidate.Length == 0) continue;

                try
                {
                    // Made absolute here: the running copy that receives it has a
                    // current directory of its own, and "." there is not "." here.
                    if (Directory.Exists(candidate) || File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch { }
            }
            return null;
        }

        /// <summary>Ends this process so a replacement started by Ctrl+R can take over.</summary>
        public static void QuitForRestart() => _context?.QuitImmediately();

        /// <summary>
        /// Hands a file to the audio player, which the tray owns because it has to
        /// outlive the window — the whole point of it is that there is no window.
        /// False when the player will not take it and the file should be opened
        /// the ordinary way.
        /// </summary>
        public static bool PlayAudioFile(string path) => _context?.PlayAudioFile(path) ?? false;

        /// <summary>Runs one audio command, announcing it as the preferences ask.</summary>
        public static void RunAudioAction(AudioAction action) => _context?.RunAudioAction(action);

        /// <summary>
        /// Says which audio file the cursor has settled on, so it can be warmed
        /// before anyone presses Enter on it.
        /// </summary>
        public static void PrefetchAudioFile(string path) => _context?.PrefetchAudioFile(path);

        /// <summary>What the player is doing, for the Audio menu's labels.</summary>
        public static AudioSnapshot? AudioState => _context?.AudioState;

        // The three audio diagnostics the Preferences page prints used to be
        // static properties here, and reading them was the only thing that page
        // needed this file for — which meant the self-test could not compile
        // SettingsForm at all, because this file owns Main. They are delegates on
        // AudioReport now, filled in below, and the page has no idea the entry
        // point exists.
        private static void PublishAudioReport()
        {
            AudioReport.Output = () => _context?.AudioOutputDiagnostic ?? "";
            AudioReport.Problem = () => _context?.AudioDiagnostic ?? "";
            AudioReport.Level = () => _context?.AudioLevelDiagnostic ?? "";
        }

        private static void Run(string[] args)
        {

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Registered before anything else can throw, so the "--send" path is
            // covered too rather than only the main window.
            Application.ThreadException += (_, e) => ReportCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

            Speech.Init();
            PerfCounters.Start();

            var settings = Settings.Load();
            RememberTitle(settings);

            // Launched with a folder argument (the shell verb passes one).
            var launchPath = FirstPathArgument(args);
            settings.InitialFolderOverride =
                launchPath == null ? null
                : Directory.Exists(launchPath) ? launchPath
                : Path.GetDirectoryName(launchPath);

            PublishAudioReport();
            _context = new TrayApplicationContext(settings);

            // Now that there is something to hand requests to, start answering
            // them. Later launches send their folder here instead of starting a
            // second copy that would have nowhere to put it.
            SingleInstance.StartServer(request => _context?.HandleExternalRequest(request));

            // The binary the last install had to rename aside, if it is finally
            // free.
            //
            // On a worker, and after the window exists, because it is a directory
            // listing and a delete of a hundred and sixty megabytes and neither
            // belongs in front of somebody waiting for a folder to open. Nothing
            // depends on it: the file is harmless where it is, and this is only
            // the moment it is most likely to be deletable — whatever was running
            // from it has, by definition, been restarted.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    int swept = AppInstall.SweepRetired();
                    if (swept > 0) WriteLog("Swept", $"{swept} retired file(s) removed from {AppInstall.Directory}");
                }
                catch { }

                // The installer an update ran, now that it has finished, and the
                // one sentence saying the update took.
                Updater.SweepDownloads();
                var updated = Updater.TakeJustUpdatedMessage();
                if (updated != null)
                {
                    WriteLog("Updated", updated);
                    System.Threading.Thread.Sleep(1500);
                    Speech.Speak(updated);
                }
            });

            try
            {
                Application.Run(_context);
            }
            finally
            {
                SingleInstance.StopServer();
                Speech.Shutdown();
            }
        }

        /// <summary>
        /// Pushes edited preferences everywhere they need to take effect, and
        /// says whether the file was actually written.
        ///
        /// The settings still apply to the running session either way — a failed
        /// write costs the change on the next launch, not this one — so the
        /// return value is about what to tell somebody, not about what to do.
        /// </summary>
        public static bool ApplySettings(Settings settings)
        {
            bool saved = settings.Save();
            _context?.ApplySettings(settings);
            return saved;
        }

        private static void ReportCrash(Exception? ex)
        {
            if (ex == null) return;
            try
            {
                var path = Path.Combine(Settings.AppDataDir, "crash.log");
                Directory.CreateDirectory(Settings.AppDataDir);
                File.AppendAllText(path, $"[{DateTime.Now:u}] {ex}{Environment.NewLine}{Environment.NewLine}");
                Speech.Speak("Explorer Native hit an error. Details were written to the crash log.");
                MessageBox.Show($"{ex.Message}\n\nWritten to:\n{path}", DisplayTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
