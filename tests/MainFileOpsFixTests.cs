using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// The 2.4.0 sweep of the main window and the file operations: Replace over
    /// a read-only file by a same-volume move and by a converted file, a file
    /// and a folder never "replaced" by each other, the clipboard not freezing
    /// the window for five seconds, converting into OneDrive-style subfolders,
    /// "1 item" in the progress window, and the source rules for the timers,
    /// Go to path and the audio editor's save.
    /// </summary>
    internal static class MainFileOpsFixTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Main window and file operation fixes:");
            var root = Path.Combine(Path.GetTempPath(), "en-mainfileops-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            try
            {
                await MoveOverReadOnly(root);
                await FileAndFolderNeverReplaced(root);
                ConvertedFileReplacesReadOnly(root);
                GatherWalksPlaceholderFolders(root);
            }
            finally
            {
                try { ClearReadOnly(root); Directory.Delete(root, recursive: true); } catch { }
            }

            ProgressSaysOneItem();
            ClipboardDoesNotFreeze();
            SourceRules();
            Console.WriteLine();
        }

        private static void ClearReadOnly(string root)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }

        private static string Fresh(string root, string name)
        {
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>(a) A same-volume move is a rename, and Replace over a read-only file must still replace it.</summary>
        private static async Task MoveOverReadOnly(string root)
        {
            var from = Fresh(root, "move-from");
            var to = Fresh(root, "move-to");
            var source = Path.Combine(from, "song.txt");
            var target = Path.Combine(to, "song.txt");
            File.WriteAllText(source, "new");
            File.WriteAllText(target, "old");
            File.SetAttributes(target, FileAttributes.ReadOnly);

            var result = await FileOperations.RunAsync(new[] { source }, to, move: true,
                PasteConflictPolicy.Overwrite, 2, 64, null, null, CancellationToken.None);

            Equal("a move over a read-only file fails nothing", "0", result.Failed.ToString());
            Equal("and the new file is the one there", "new", File.Exists(target) ? File.ReadAllText(target) : "(missing)");
            Check("and the original has gone, as a move", !File.Exists(source));
            Equal("and it is said as replaced", "1", result.Collisions.OverwrittenCount.ToString());

            // The swap the copy path already did, now shared: still replaces.
            var copyFrom = Fresh(root, "copy-from");
            var copySource = Path.Combine(copyFrom, "song.txt");
            File.WriteAllText(copySource, "copied");
            File.SetAttributes(target, FileAttributes.ReadOnly);
            var copied = await FileOperations.RunAsync(new[] { copySource }, to, move: false,
                PasteConflictPolicy.Overwrite, 2, 64, null, null, CancellationToken.None);
            Equal("a copy over a read-only file still replaces it", "copied|0",
                (File.Exists(target) ? File.ReadAllText(target) : "(missing)") + "|" + copied.Failed);
        }

        /// <summary>(b) Replace never swaps a file and a folder, and never calls that a replacement.</summary>
        private static async Task FileAndFolderNeverReplaced(string root)
        {
            var from = Fresh(root, "kind-from");
            var to = Fresh(root, "kind-to");

            // A file arriving where a folder of its name is.
            var file = Path.Combine(from, "notes");
            File.WriteAllText(file, "a file");
            var folderThere = Fresh(to, "notes");
            File.WriteAllText(Path.Combine(folderThere, "inside.txt"), "kept");

            var result = await FileOperations.RunAsync(new[] { file }, to, move: false,
                PasteConflictPolicy.Overwrite, 2, 64, null, null, CancellationToken.None);
            Equal("a file over a folder is not counted as replaced", "0", result.Collisions.OverwrittenCount.ToString());
            Equal("it is a failure", "1", result.Failed.ToString());
            Check("which says a folder is there",
                result.Errors.Any(e => e.Contains("folder of that name is already there")), string.Join(" | ", result.Errors));
            Check("and the folder and what is in it are untouched",
                File.Exists(Path.Combine(folderThere, "inside.txt")));

            // A folder arriving where a file of its name is.
            var folder = Fresh(from, "photos");
            File.WriteAllText(Path.Combine(folder, "a.jpg"), "jpg");
            var fileThere = Path.Combine(to, "photos");
            File.WriteAllText(fileThere, "a file called photos");

            var reverse = await FileOperations.RunAsync(new[] { folder }, to, move: true,
                PasteConflictPolicy.Overwrite, 2, 64, null, null, CancellationToken.None);
            Equal("a folder over a file is not counted as replaced", "0", reverse.Collisions.OverwrittenCount.ToString());
            Equal("it is one failure", "1", reverse.Failed.ToString());
            Equal("the file there is untouched", "a file called photos", File.ReadAllText(fileThere));
            Check("and the move left the source folder where it was", File.Exists(Path.Combine(folder, "a.jpg")));

            // Asked, and answered Replace: the same.
            var asked = await FileOperations.RunAsync(new[] { file }, to, move: false,
                PasteConflictPolicy.Ask, 2, 64, null, _ => FileOperations.ConflictChoice.Overwrite, CancellationToken.None);
            Equal("Replace chosen in the question is refused the same way", "0|1",
                asked.Collisions.OverwrittenCount + "|" + asked.Failed);

            // A file over a file is still a replacement.
            var plain = Path.Combine(from, "plain.txt");
            File.WriteAllText(plain, "new");
            File.WriteAllText(Path.Combine(to, "plain.txt"), "old");
            var same = await FileOperations.RunAsync(new[] { plain }, to, move: false,
                PasteConflictPolicy.Overwrite, 2, 64, null, null, CancellationToken.None);
            Equal("a file over a file is still replaced", "1|0|new",
                same.Collisions.OverwrittenCount + "|" + same.Failed + "|" + File.ReadAllText(Path.Combine(to, "plain.txt")));
        }

        /// <summary>A converted file chosen to replace a read-only one replaces it.</summary>
        private static void ConvertedFileReplacesReadOnly(string root)
        {
            var dir = Fresh(root, "convert-place");
            var target = Path.Combine(dir, "song.mp3");
            File.WriteAllText(target, "old");
            File.SetAttributes(target, FileAttributes.ReadOnly);
            var temp = AudioConverter.TempPathBeside(target);
            File.WriteAllText(temp, "converted");

            string placed = "", failure = "";
            try { placed = AudioConverter.Place(temp, target, replace: true); }
            catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }

            Check("Replace puts a converted file over a read-only one", failure.Length == 0, failure);
            Equal("under the name asked for", target, placed);
            Equal("with the new contents", "converted", File.ReadAllText(target));
            Check("and the temporary file has gone", !File.Exists(temp));
        }

        /// <summary>Converting a folder goes into subfolders that are not links, reparse point or not.</summary>
        private static void GatherWalksPlaceholderFolders(string root)
        {
            var top = Fresh(root, "gather");
            var sub = Fresh(top, "Disc 1");
            File.WriteAllBytes(Path.Combine(sub, "track.wav"), new byte[16]);

            var elsewhere = Fresh(root, "gather-elsewhere");
            File.WriteAllBytes(Path.Combine(elsewhere, "linked.wav"), new byte[16]);
            bool junction = true;
            try { ReparseLinks.CreateJunction(Path.Combine(top, "link"), elsewhere); }
            catch { junction = false; }

            var found = AudioConverter.Gather(new[] { top }, subfolders: true, null, CancellationToken.None)
                .Select(Path.GetFileName).ToList();
            Check("a subfolder's audio is gathered", found.Contains("track.wav"), string.Join(", ", found));
            if (junction)
                Check("a junction is still not followed", !found.Contains("linked.wav"), string.Join(", ", found));

            // A cloud placeholder (OneDrive, Files On-Demand) is a reparse point
            // that is not a link, and cannot be made in a test: the rule is read.
            var source = Source("AudioConvert.cs");
            var gather = source == null ? null : Between(source, "public static List<string> Gather(", "public static string TargetFor(");
            Check("Gather skips links, not every reparse point",
                gather != null && gather.Contains("NameRules.IsLink(") && !gather.Contains("FileAttributes.ReparsePoint) == 0"));
        }

        private static void ProgressSaysOneItem()
        {
            string text = "", error = "";
            var thread = new Thread(() =>
            {
                try
                {
                    using var cts = new CancellationTokenSource();
                    using var window = new ProgressForm("Copying", new Settings(), cts);
                    _ = window.Handle;
                    window.Update(new TransferProgress(0, 0, 1, 0, "a.txt", 0, TimeSpan.Zero));
                    var box = AllControls(window).OfType<TextBox>().FirstOrDefault(b => b.AccessibleName == "Progress");
                    text = box?.Text ?? "(no progress line)";
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(30))) error = "timed out";

            Check("the progress window could be built", error.Length == 0, error);
            Equal("one item with no total is \"1 item\", not \"1 items\"", "1 item so far, total not yet known", text);
        }

        private static System.Collections.Generic.IEnumerable<Control> AllControls(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (var inner in AllControls(child)) yield return inner;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        /// <summary>
        /// Copy path while another program holds the clipboard gives up in well
        /// under a second, rather than five nested retry loops of one second
        /// each on the UI thread. The clipboard's contents are never changed: it
        /// is held for the whole call, so nothing can be written to it.
        /// </summary>
        private static void ClipboardDoesNotFreeze()
        {
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            bool holding = false;
            var holder = new Thread(() =>
            {
                for (int attempt = 0; attempt < 20 && !holding; attempt++)
                {
                    holding = OpenClipboard(IntPtr.Zero);
                    if (!holding) Thread.Sleep(50);
                }
                held.Set();
                if (!holding) return;
                release.Wait(TimeSpan.FromSeconds(20));
                CloseClipboard();
            }) { IsBackground = true };
            holder.Start();
            held.Wait(TimeSpan.FromSeconds(5));

            if (!holding)
            {
                Check("the clipboard could be held for the test (skipped: another program has it)", true);
                release.Set();
                return;
            }

            bool result = true;
            long elapsed = 0;
            string error = "";
            var caller = new Thread(() =>
            {
                try
                {
                    var clock = Stopwatch.StartNew();
                    result = ClipboardInterop.SetText("explorer native clipboard test");
                    elapsed = clock.ElapsedMilliseconds;
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            }) { IsBackground = true };
            caller.SetApartmentState(ApartmentState.STA);
            caller.Start();
            bool finished = caller.Join(TimeSpan.FromSeconds(15));
            release.Set();
            holder.Join(TimeSpan.FromSeconds(5));

            Check("setting text on a held clipboard returns", finished && error.Length == 0, error);
            Check("and says it could not", !result);
            Check("within two and a half seconds, not five", elapsed < 2500, $"{elapsed}ms");

            var source = Source("ClipboardInterop.cs");
            var clear = source == null ? null : Between(source, "public static bool ClearIfStillOurs(", "public static bool SetText(");
            Check("clearing a spent cut asks Clipboard.Clear once, which retries by itself",
                clear != null && Regex.Matches(clear, @"Clipboard\.Clear\(\)").Count == 1 &&
                !clear.Contains("Sleep("));
        }

        private static void SourceRules()
        {
            var main = Source("MainForm.cs");
            if (main == null) { Check("MainForm.cs can be read", false); return; }

            // Every WinForms timer the window makes is let go of with it.
            var dispose = Between(main, "protected override void Dispose(bool disposing)", "base.Dispose(disposing);");
            var timers = Regex.Matches(main, @"private\s+System\.Windows\.Forms\.Timer\??\s+(_\w+)")
                .Select(m => m.Groups[1].Value).Distinct().ToList();
            var undisposed = timers.Where(t => dispose == null ||
                !Regex.IsMatch(dispose, Regex.Escape(t) + @"\??\.Dispose\(\)")).ToList();
            Check("the window has timers to look at", timers.Count >= 5, string.Join(", ", timers));
            Check("every timer the window makes is disposed with it", undisposed.Count == 0,
                "not disposed: " + string.Join(", ", undisposed));

            // Go to path is async void from Ctrl+L: guarded like every other command.
            var goTo = Between(main, "private async void GoToPath()", "private async Task GoToPathAsync()")
                       ?? Between(main, "private async void GoToPath()", "private async void Refresh_()");
            Check("Go to path catches what would otherwise take the window down",
                goTo != null && goTo.Contains("catch (Exception"));

            // The audio editor stays busy through the whole save, not just the render.
            var editor = Source("AudioEditorForm.cs");
            if (editor == null) { Check("AudioEditorForm.cs can be read", false); return; }
            var busy = Between(editor, "private async Task BusyWhile(", "private async Task SaveAsNewCore(");
            Check("replacing the original runs busy from start to end",
                Regex.IsMatch(editor, @"void\s+ReplaceOriginal\(\)\s*=>\s*await\s+BusyWhile\(") &&
                Regex.IsMatch(editor, @"void\s+SaveAsNew\(\)\s*=>\s*await\s+BusyWhile\(") &&
                busy != null && busy.Contains("_busy = true") && busy.Contains("finally"));
            var render = Between(editor, "private async Task<string?> Render(", "// ---------------------------------------------------------- closing");
            Check("and the render puts busy back as it found it rather than clearing it",
                render != null && !Regex.IsMatch(render, @"_busy\s*=\s*false"));

            // A file that would not open says so on every key, not "still opening".
            var keys = Between(editor, "protected override bool ProcessCmdKey(", "private static bool IsEditorKey(");
            Check("a failed open is what the keys say afterwards",
                keys != null && keys.Contains("_openFailure") && editor.Contains("_openFailure = _lastMessage"));
        }

        private static string? Between(string text, string start, string end)
        {
            int a = text.IndexOf(start, StringComparison.Ordinal);
            if (a < 0) return null;
            int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
            return b < 0 ? null : text[a..b];
        }

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
