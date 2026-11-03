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
            await PauseGateHolds();
            TransferLines();
            RunningListFollowsWindows();
            await ManagedCopyWaitsWhilePaused();
            await RobocopyWaitsWhilePaused();
            ProcessPauserSuspends();
        }

        // A robocopy in flight is suspended where it is while paused, and let go on Resume.
        private static void ProcessPauserSuspends()
        {
            var ping = System.IO.Path.Combine(Environment.SystemDirectory, "PING.EXE");
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ping, "-n 30 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            try
            {
                bool AllSuspended()
                {
                    process.Refresh();
                    foreach (System.Diagnostics.ProcessThread t in process.Threads)
                        if (t.ThreadState != System.Diagnostics.ThreadState.Wait ||
                            t.WaitReason != System.Diagnostics.ThreadWaitReason.Suspended) return false;
                    return true;
                }

                var pause = new TransferPause();
                using var pauser = new ProcessPauser(pause);
                pauser.Set(process);
                Check("a running process is not suspended", !AllSuspended());
                pause.Pause();
                Check("Pause suspends it", AllSuspended());
                pause.Pause();
                pause.Resume();
                Check("Resume lets it run again, once however often Pause was pressed", !AllSuspended());
                pause.Pause();
                pauser.Set(null);
                Check("letting go of it resumes it", !AllSuspended());
            }
            finally
            {
                try { process.Kill(); } catch { }
            }
        }

        // Feature 8: the gate holds new work while paused and lets it go on Resume.
        private static async Task PauseGateHolds()
        {
            var pause = new TransferPause();
            Check("a running gate is open", pause.WaitAsync(default).IsCompleted);
            var said = new System.Collections.Generic.List<bool>();
            pause.Changed += said.Add;
            pause.Pause();
            var waiting = pause.WaitAsync(default);
            await Task.Delay(50);
            Check("a paused gate holds", !waiting.IsCompleted);
            using (var piece = pause.Until(default))
                Check("a piece started while paused stops at once, as paused", piece.Token.IsCancellationRequested && piece.PausedIt);
            pause.Resume();
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Check("Resume lets it go", waiting.IsCompletedSuccessfully);
            Check("Changed says paused then resumed", said.Count == 2 && said[0] && !said[1]);

            using var cts = new System.Threading.CancellationTokenSource();
            pause.Pause();
            var held = pause.WaitAsync(cts.Token);
            cts.Cancel();
            try { await held; Check("cancel ends a paused wait", false); }
            catch (OperationCanceledException) { Check("cancel ends a paused wait", true); }
            pause.Resume();
        }

        private static void TransferLines()
        {
            var p = new TransferProgress(400, 1000, 4, 12, "a.flac", 5, TimeSpan.FromSeconds(10));
            Equal("a copy's line", "Copying 12 files to Music, 40 percent, about 2 minutes left",
                TransferText.Line("Copying to Music", p, false));
            Equal("a paused line", "Moving 12 files to Music, 40 percent, paused",
                TransferText.Line("Moving to Music", p, true));
            Equal("a line before any progress", "Extracting to Downloads, starting",
                TransferText.Line("Extracting to Downloads", null, false));
            Equal("nothing running", "Nothing is being copied or moved right now.",
                TransferText.Overall(Array.Empty<(string, TransferProgress?, bool)>()));
            var q = new TransferProgress(600, 1000, 1, 2, "b", 5, TimeSpan.FromSeconds(10));
            Equal("two at once", "2 transfers are running, 50 percent done in all, 1 paused.",
                TransferText.Overall(new (string, TransferProgress?, bool)[] { ("Copying to A", p, false), ("Copying to B", q, true) }));
            Equal("hours", "about 2 hours and 5 minutes left", TransferText.Left(TimeSpan.FromMinutes(125)));
        }

        // A transfer window is listed from when it is made until its transfer is over.
        private static void RunningListFollowsWindows()
        {
            // A form would install WinForms' context here, and the awaits after
            // it would wait for a message loop the suite does not run.
            var context = System.Threading.SynchronizationContext.Current;
            try { RunningListCore(); }
            finally { System.Threading.SynchronizationContext.SetSynchronizationContext(context); }
        }

        private static void RunningListCore()
        {
            using var cts = new System.Threading.CancellationTokenSource();
            using var window = new ProgressForm("Copying to Music", new Settings(), cts);
            Check("a new transfer window is listed", System.Linq.Enumerable.Contains(ProgressForm.Running, window));
            window.Update(new TransferProgress(50, 100, 1, 3, "x", 0, TimeSpan.Zero));
            Equal("it describes itself", "Copying 3 files to Music, 50 percent", window.Describe());
            window.Pause = new TransferPause();
            window.TogglePause();
            Check("Pause from the list pauses its transfer", window.IsPaused);
            window.TogglePause();
            Check("and Resume resumes it", !window.IsPaused);
            window.Finish();
            Check("a finished transfer leaves the list", !System.Linq.Enumerable.Contains(ProgressForm.Running, window));
        }

        private static string NewFolder(string tag)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"en510{tag}-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }

        // The managed engine waits at the gate: nothing lands while paused.
        private static async Task ManagedCopyWaitsWhilePaused()
        {
            var src = NewFolder("src");
            var dst = NewFolder("dst");
            try
            {
                var file = System.IO.Path.Combine(src, "song.flac");
                System.IO.File.WriteAllBytes(file, new byte[300_000]);
                var pause = new TransferPause();
                pause.Pause();
                var run = FileOperations.RunAsync(new[] { file }, dst, false, PasteConflictPolicy.AutoRename,
                    2, 64, null, null, default, pause: pause);
                await Task.Delay(400);
                Check("a paused managed copy writes nothing", !run.IsCompleted &&
                    !System.IO.File.Exists(System.IO.Path.Combine(dst, "song.flac")));
                pause.Resume();
                var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
                Check("resumed, it finishes", result.Succeeded == 1 &&
                    new System.IO.FileInfo(System.IO.Path.Combine(dst, "song.flac")).Length == 300_000);
            }
            finally
            {
                try { System.IO.Directory.Delete(src, true); } catch { }
                try { System.IO.Directory.Delete(dst, true); } catch { }
            }
        }

        // A robocopy paste waits at the gate before its job, then copies on Resume.
        private static async Task RobocopyWaitsWhilePaused()
        {
            var src = NewFolder("src");
            var dst = NewFolder("dst");
            try
            {
                var file = System.IO.Path.Combine(src, "track.wav");
                System.IO.File.WriteAllBytes(file, new byte[200_000]);
                var pause = new TransferPause();
                pause.Pause();
                var run = RoboCopyEngine.RunAsync(new[] { file }, dst, false, 2, PasteConflictPolicy.Overwrite,
                    null, default, pause: pause);
                await Task.Delay(1500);
                Check("a paused robocopy paste copies nothing", !run.IsCompleted &&
                    !System.IO.File.Exists(System.IO.Path.Combine(dst, "track.wav")));
                pause.Resume();
                var result = await run.WaitAsync(TimeSpan.FromSeconds(60));
                Check("resumed, robocopy copies it", !result.Cancelled && result.Failed == 0 &&
                    System.IO.File.Exists(System.IO.Path.Combine(dst, "track.wav")), string.Join("; ", result.Errors));
            }
            finally
            {
                try { System.IO.Directory.Delete(src, true); } catch { }
                try { System.IO.Directory.Delete(dst, true); } catch { }
            }
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
