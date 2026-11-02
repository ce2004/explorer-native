using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of the folder sync: Keep on Drive only and the sync window.
    /// </summary>
    internal static class Sweep500Sync2Tests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        private const double Fast = 0.0005;
        private static readonly DateTime T0 = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the folder sync: Keep on Drive only and the sync window:");
            var previous = Settings.OverrideAppDataDir;
            var sandbox = Path.Combine(Path.GetTempPath(), "en-sync2-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            Settings.OverrideAppDataDir = Path.Combine(sandbox, "appdata");
            try
            {
                await KeepGivesThePassProblem(sandbox);
                await KeepInDownloadOnlySaysWhyNot(sandbox);
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(sandbox, true); } catch { }
            }
        }

        /// <summary>A pair over a fake Drive, its monitor with no loop: passes run when the test says.</summary>
        private sealed class Rig : IDisposable
        {
            public readonly FakeDrive Drive = new();
            public readonly string Local;
            public readonly string FolderId;
            public readonly DriveSyncPair Pair;
            public readonly DriveClient Client;
            public readonly DriveMonitor Monitor;

            public Rig(string sandbox, SyncMode mode)
            {
                Local = Path.Combine(sandbox, Guid.NewGuid().ToString("N")[..8], "local");
                Directory.CreateDirectory(Local);
                FolderId = Drive.AddFolder("Sync");
                Pair = new DriveSyncPair
                {
                    Name = "Test", LocalFolder = Local, DriveFolderId = FolderId, DriveFolderPath = "My Drive/Sync",
                    Mode = mode, CopyDeletes = false, CheckMinutes = 0,
                };
                Client = new DriveClient(Drive, Fast);
                Monitor = new DriveMonitor(new GoogleDrive(), (_, _) => { }, _ => { })
                {
                    WaitScale = Fast,
                    DiskOfRoot = _ => (1L << 42, 1L << 43, "T:"),
                    RecycleLocal = path => { File.Delete(path); return null; },
                };
            }

            public async Task Pass() => await Monitor.SyncOneAsync(Client, Pair, default);

            public string PathOf(string relative) => Path.Combine(Local, relative.Replace('/', '\\'));

            public void Write(string relative, string text, DateTime modifiedUtc)
            {
                var path = PathOf(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
                File.SetLastWriteTimeUtc(path, modifiedUtc);
            }

            public void Put(string relative, string text, DateTime modifiedUtc)
            {
                var parent = FolderId;
                var parts = relative.Split('/');
                foreach (var folder in parts[..^1])
                    parent = Drive.Find(parent, folder) ?? Drive.AddFolder(folder, parent);
                Drive.AddFile(parts[^1], Encoding.UTF8.GetBytes(text), modifiedUtc, parent);
            }

            public List<string> LiveDriveOnly() => Monitor.Pairs.First(p => p.Id == Pair.Id).DriveOnlyFolders;

            public void Dispose()
            {
                Monitor.Dispose();
                try { Directory.Delete(Path.GetDirectoryName(Local)!, true); } catch { }
            }
        }

        /// <summary>
        /// Bug 1: a pass that went through but could not send a file (Google
        /// refused it) gives that reason, not "try again once it has synced".
        /// </summary>
        private static async Task KeepGivesThePassProblem(string sandbox)
        {
            const string refusal = "The user does not have sufficient permissions for this file.";
            foreach (bool refused in new[] { true, false })
            {
                using var rig = new Rig(sandbox, SyncMode.TwoWay);
                rig.Write("School/one.txt", "one", T0);
                await rig.Pass();
                rig.Monitor.Reload(new[] { rig.Pair });
                rig.Write("School/bad.txt", "refused by Google", T0.AddMinutes(1));
                if (refused)
                    rig.Drive.AddFault(new FakeDrive.Fault
                    {
                        Kind = "multipart", Status = 403, Times = 1000, Subject = s => s.Contains("bad.txt"),
                        Body = "{\"error\":{\"code\":403,\"message\":\"" + refusal + "\",\"errors\":[{\"reason\":\"insufficientFilePermissions\",\"message\":\"" + refusal + "\"}]}}",
                    });
                rig.Monitor.DriveOnlySyncWait = TimeSpan.FromSeconds(20);
                var ask = rig.Monitor.KeepOnDriveOnlyAsync(rig.Pair.Id, "School", default, rig.Client);
                await Task.Delay(300);
                await rig.Pass();
                var outcome = await ask;
                if (refused)
                {
                    Check("keep on Drive only: a file Google refused is not kept", !outcome.Done, outcome.Message);
                    Check("and the message names the file", outcome.Message.Contains("bad.txt"), outcome.Message);
                    Check("and gives Google's reason, from the pass that went through", outcome.Message.Contains("sufficient permissions"), outcome.Message);
                    Check("and does not say try again once it has synced, which never helps", !outcome.Message.Contains("Try again once it has synced"), outcome.Message);
                    Check("and nothing was removed", File.Exists(rig.PathOf("School/bad.txt")) && File.Exists(rig.PathOf("School/one.txt")));
                }
                else
                    Check("control: with nothing refused the same pass sends it and the folder is kept", outcome.Done, outcome.Message);
            }
        }

        /// <summary>
        /// Bug 2: in a download-only pair, a file only on the PC never goes up,
        /// so Keep on Drive only says so at once instead of "try again once it
        /// has synced"; a PC edit still differing after the pass is said too.
        /// </summary>
        private static async Task KeepInDownloadOnlySaysWhyNot(string sandbox)
        {
            using (var rig = new Rig(sandbox, SyncMode.DownloadOnly))
            {
                rig.Put("School/one.txt", "one", T0);
                await rig.Pass();
                Check("download only rig: Drive's file came down", File.Exists(rig.PathOf("School/one.txt")));
                rig.Monitor.Reload(new[] { rig.Pair });
                rig.Monitor.DriveOnlySyncWait = TimeSpan.FromSeconds(20);
                rig.Write("School/notes.txt", "pc only", T0.AddMinutes(1));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var outcome = await rig.Monitor.KeepOnDriveOnlyAsync(rig.Pair.Id, "School", default, rig.Client);
                Check("download only: a file only on the PC is not kept on Drive only", !outcome.Done, outcome.Message);
                Check("and the message names it and says the pair only copies from Drive",
                    outcome.Message.Contains("notes.txt") && outcome.Message.Contains("only copies from Google Drive"), outcome.Message);
                Check("and does not say try again once it has synced", !outcome.Message.Contains("Try again once it has synced"), outcome.Message);
                Check("and it is said at once, without waiting on a pass that can never send it", clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
                Check("and nothing was removed", File.Exists(rig.PathOf("School/one.txt")) && File.Exists(rig.PathOf("School/notes.txt")));
                Check("and the folder is not on the list", rig.LiveDriveOnly().Count == 0);

                Directory.CreateDirectory(rig.PathOf("Empty"));
                var empty = await rig.Monitor.KeepOnDriveOnlyAsync(rig.Pair.Id, "Empty", default, rig.Client);
                Check("download only: an empty folder Drive lacks is said to never go up",
                    !empty.Done && empty.Message.Contains("only copies from Google Drive") && !empty.Message.Contains("Try again"), empty.Message);

                // A PC edit of a file Drive has: the pass runs (it could bring a
                // newer copy down), then the edit is said to never go up.
                File.Delete(rig.PathOf("School/notes.txt"));
                rig.Write("School/one.txt", "ONE", T0.AddMinutes(2));
                var ask = rig.Monitor.KeepOnDriveOnlyAsync(rig.Pair.Id, "School", default, rig.Client);
                await Task.Delay(300);
                await rig.Pass();
                var edited = await ask;
                Check("download only: a PC edit still differing after the pass is said as never going up",
                    !edited.Done && edited.Message.Contains("one.txt") && edited.Message.Contains("different from Google Drive's copy")
                    && !edited.Message.Contains("Try again"), edited.Message);
                Check("and the edit is still on the PC", File.ReadAllText(rig.PathOf("School/one.txt")) == "ONE");
            }
        }
    }
}
