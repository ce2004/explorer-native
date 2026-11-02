using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of the Google Drive client.
    /// </summary>
    internal static class Sweep500ClientTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the Google Drive client:");
            await Guard("disconnect", DisconnectDoesNotWaitForUploads);
        }

        private static async Task Guard(string name, Func<Task> test)
        {
            try { await test(); }
            catch (Exception ex) { Check(name + " ran to the end", false, ex.GetType().Name + ": " + ex.Message); }
        }

        private static readonly DateTime When = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        /// <summary>A mount over the fake Drive at "root", no letter, handed to a GoogleDrive.</summary>
        private sealed class Rig : IDisposable
        {
            public readonly FakeDrive Fake = new();
            public readonly DriveClient Client;
            public readonly GoogleDrive Drive = new();
            public readonly DriveMount Mount;
            public readonly string Root;
            public readonly List<string> Said = new();

            public Rig()
            {
                Client = new DriveClient(Fake, 0.0005);
                Root = Path.Combine(Path.GetTempPath(), "en-s500c-" + Guid.NewGuid().ToString("N")[..8]);
                Drive.Notification += (id, message) => { lock (Said) Said.Add(id + ": " + message); };
                Mount = new DriveMount(Client, Root, 16L * 1024 * 1024, _ => { });
                Mount.Start("root", assignLetter: false);
                Drive.UseForTests(Mount, Client);
            }

            public string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

            public IReadOnlyList<DrivePlaced>? Listed(string path)
            {
                Mount.Populate(path);
                return Mount.TryListing(path, out var entries) ? entries : null;
            }

            public string SaidText() { lock (Said) return string.Join(" | ", Said); }

            public void Dispose()
            {
                // The GoogleDrive takes down the mount it was handed (or waits for the one it let go).
                try { Drive.Dispose(); } catch { }
                try { Directory.Delete(Root, true); } catch { }
            }
        }

        private static string Names(IReadOnlyList<DrivePlaced>? placed) =>
            placed == null ? "(not listed)" : string.Join(",", placed.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        private static string SourceText(string name)
        {
            var at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at != null && !Directory.Exists(Path.Combine(at.FullName, "src"))) at = at.Parent;
            if (at == null) return "";
            var path = Path.Combine(at.FullName, "src", name);
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch { return ""; }
        }

        /// <summary>The body of a method in a source file, from its name to the next method at the same depth (roughly).</summary>
        private static string MethodText(string source, string signature, int length = 12000)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            return at < 0 ? "" : source.Substring(at, Math.Min(length, source.Length - at));
        }

        // ---- 1. Disconnect and switching Drive off return at once while uploads drain ----

        private static async Task DisconnectDoesNotWaitForUploads()
        {
            var drainWas = DriveMount.DrainFor;
            DriveMount.DrainFor = TimeSpan.FromSeconds(8);
            using var rig = new Rig();
            try
            {
                rig.Fake.AnswerDelayMs = 3000;
                rig.Mount.NewPending("", "late.txt", "root");
                await Task.Delay(100);

                var clock = Stopwatch.StartNew();
                rig.Drive.Unmount();
                long took = clock.ElapsedMilliseconds;
                var names = rig.Drive.UploadNames;
                Check("Disconnect returns at once while a saved file is still going up", took < 1000, took + " ms");
                Check("and the question before quitting still sees the file it is sending",
                    names.Contains("late.txt"), string.Join(",", names));

                var finished = rig.Drive.TeardownForTests;
                bool done = await Task.WhenAny(finished, Task.Delay(30_000)) == finished;
                Check("the disconnected mount finishes on a worker", done);
                Check("and the file still reaches Google Drive", rig.Fake.Find(FakeDrive.Root, "late.txt") != null,
                    rig.SaidText());
                Check("and Google Drive disconnected is said once it is gone",
                    rig.SaidText().Contains("drive.unmounted"), rig.SaidText());

                var fresh = MethodText(SourceText("GoogleDrive.cs"), "private async Task<bool> MountFresh(");
                int wait = fresh.IndexOf("Teardown.WaitAsync(", StringComparison.Ordinal);
                int start = fresh.IndexOf("mount.Start(", StringComparison.Ordinal);
                Check("a new connect waits for a disconnected mount to go before standing its volume up",
                    wait > 0 && start > wait);
            }
            finally { DriveMount.DrainFor = drainWas; }
        }
    }
}
