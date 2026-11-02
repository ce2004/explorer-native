using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of the window: audio, archives, status and lifetime.
    /// </summary>
    internal static class Sweep500MainDTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the window: audio, archives, status and lifetime:");
            await ExtractsIntoOneFolderTakeTurns();
            DriveSideDeleteKeepsPcFolderUnlessDriveSaysGone();
            OtherSideOfSyncedDeleteIsSaidLast();
        }

        /// <summary>
        /// Bugs 1-3: two Extract here into one folder at once. Each worked out its
        /// clashes before the other wrote anything, so one silently replaced the
        /// other's files. Now extracts into one folder take turns.
        /// </summary>
        private static async Task ExtractsIntoOneFolderTakeTurns()
        {
            var root = Path.Combine(Path.GetTempPath(), "ExplorerNativeTests_sweep500maind_" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                // The order: the second extract does not start while the first runs.
                var dest = Path.Combine(root, "dest");
                Directory.CreateDirectory(dest);
                var firstRunning = new TaskCompletionSource();
                var letFirstEnd = new TaskCompletionSource();
                bool secondStartedEarly = false;
                var first = ExtractTurns.InTurn(dest, async () =>
                {
                    firstRunning.SetResult();
                    await letFirstEnd.Task;
                    return new ArchiveResult(1, 0, false, Array.Empty<string>(), 0, 0);
                }, CancellationToken.None);
                await firstRunning.Task;
                var second = ExtractTurns.InTurn(dest + "\\", () =>
                {
                    secondStartedEarly = !letFirstEnd.Task.IsCompleted;
                    return Task.FromResult(new ArchiveResult(1, 0, false, Array.Empty<string>(), 0, 0));
                }, CancellationToken.None);
                // A different folder is not held up.
                var elsewhere = ExtractTurns.InTurn(Path.Combine(root, "other"),
                    () => Task.FromResult(new ArchiveResult(1, 0, false, Array.Empty<string>(), 0, 0)), CancellationToken.None);
                bool otherFolderRan = await Task.WhenAny(elsewhere, Task.Delay(5000)) == elsewhere;
                await Task.Delay(100);
                Check("an extract into a folder another extract is writing waits its turn", !second.IsCompleted);
                Check("an extract into a different folder is not held up", otherFolderRan);
                var cts = new CancellationTokenSource();
                var cancelled = ExtractTurns.InTurn(dest, () => throw new InvalidOperationException("ran"), cts.Token);
                cts.Cancel();
                var cancelledResult = await cancelled;
                Check("an extract cancelled while waiting its turn ends cancelled without running", cancelledResult.Cancelled);
                letFirstEnd.SetResult();
                await Task.WhenAll(first, second);
                Check("the waiting extract runs once the first has finished", !secondStartedEarly);

                // The real thing: two zips holding the same names, extracted at once.
                var a = MakeZip(root, "a", 'A');
                var b = MakeZip(root, "b", 'B');
                var into = Path.Combine(root, "into");
                Directory.CreateDirectory(into);
                File.WriteAllText(Path.Combine(into, "already.txt"), "here before");
                Task<ArchiveResult> Start(string zip) => ExtractTurns.InTurn(into,
                    () => ArchiveEngine.ExtractAsync(zip, into, PasteConflictPolicy.AutoRename, null, 4, null, CancellationToken.None),
                    CancellationToken.None);
                var results = await Task.WhenAll(Start(a), Start(b));
                var bins = Directory.GetFiles(into, "same*.bin").Select(File.ReadAllBytes).ToList();
                Check("both archives' same-named big file survive extracting together",
                    bins.Count == 2 && bins.Select(x => x[0]).Distinct().Count() == 2, $"{bins.Count} files");
                Check("both archives' same-named small files survive extracting together",
                    Directory.GetFiles(into, "f000*.txt").Length == 2);
                Check("the rename of the second archive's clashing names is reported",
                    results.Sum(r => r.Collisions.RenamedCount) > 0);

                var src = Source("MainForm.cs") ?? "";
                Check("the window's extracts take their turn per destination folder",
                    Body(src, "private async Task ExtractOneAsync(").Contains("ExtractTurns.InTurn(destination", StringComparison.Ordinal));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static string MakeZip(string root, string name, char fill)
        {
            var src = Path.Combine(root, "src-" + name);
            Directory.CreateDirectory(src);
            File.WriteAllBytes(Path.Combine(src, "same.bin"), Enumerable.Repeat((byte)fill, 24 * 1024 * 1024).ToArray());
            for (int i = 0; i < 200; i++) File.WriteAllText(Path.Combine(src, $"f{i:000}.txt"), $"from {name}");
            var zip = Path.Combine(root, name + ".zip");
            ZipFile.CreateFromDirectory(src, zip, CompressionLevel.NoCompression, includeBaseDirectory: false);
            return zip;
        }

        /// <summary>
        /// Bug 4: Delete anyway on a pair's Drive folder recycled the PC folder
        /// even when Drive had refused to trash its own. Now the PC folder goes
        /// only once Google Drive says the Drive folder has gone.
        /// </summary>
        private static void DriveSideDeleteKeepsPcFolderUnlessDriveSaysGone()
        {
            var src = Source("MainForm.cs");
            if (src == null) { Check("MainForm.cs found", false); return; }
            var ends = Body(src, "DeleteOtherSyncEnds(List<");
            int driveSide = ends.IndexOf("if (driveSide)", StringComparison.Ordinal);
            int asked = ends.IndexOf("FileNow(pair.DriveFolderId", StringComparison.Ordinal);
            int recycled = ends.IndexOf("ShellDelete.Recycle(pair.LocalFolder", StringComparison.Ordinal);
            Check("the PC end of a pair is recycled only after Drive is asked whether its folder went",
                driveSide >= 0 && asked > driveSide && recycled > asked);
        }

        /// <summary>
        /// Bug 5: the other side's failure was spoken and then cut off at once by
        /// "Deleted" after the refresh. Now one closing sentence covers both.
        /// </summary>
        private static void OtherSideOfSyncedDeleteIsSaidLast()
        {
            var src = Source("MainForm.cs");
            if (src == null) { Check("MainForm.cs found", false); return; }
            var ends = Body(src, "DeleteOtherSyncEnds(List<");
            Check("the other side of a synced delete says nothing itself, before the refresh",
                ends.Length > 0 && !ends.Contains("Announce", StringComparison.Ordinal));
            var del = Body(src, "private async void DeleteSelected(");
            if (del.Length == 0) del = Body(src, "async Task DeleteSelected(");
            int refresh = del.IndexOf("RefreshAsync(landOn", StringComparison.Ordinal);
            int both = del.IndexOf("\"delete.both\"", StringComparison.Ordinal);
            Check("deleted on both sides is said after the refresh, as the last word",
                refresh >= 0 && both > refresh);
            Check("the other side's failures reach the error list",
                del.Contains("ShowErrors(otherSide", StringComparison.Ordinal) ||
                del.Contains("Concat(otherSide", StringComparison.Ordinal));
        }

        private static string Body(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return "";
            int end = src.IndexOf("\n        }", start, StringComparison.Ordinal);
            return end < 0 ? src[start..] : src[start..end];
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
