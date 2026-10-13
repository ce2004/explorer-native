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
    /// Times the real account's write path, step by step: EXPLORERNATIVE_SELFTEST_ONLY=driveprobe.
    /// Works in a throwaway zz-probe folder in My Drive and trashes it at the end.
    /// Uses the app's own token; never consents, never touches the drive letter.
    /// </summary>
    internal static class DriveProbe
    {
        public static async Task Run()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExplorerNative");
            var creds = GoogleAuth.Credentials(dir);
            if (creds == null) { Console.WriteLine("no client credentials"); return; }

            var auth = new GoogleAuth(creds.Value.ClientId, creds.Value.ClientSecret,
                Path.Combine(dir, $"google-drive-token-v{GoogleAuth.ScopeVersion}.dat")) { AllowConsent = false };
            var client = new DriveClient(auth);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var token = cts.Token;

            async Task<T> Time<T>(string what, Func<Task<T>> step)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var result = await step();
                    Console.WriteLine($"{sw.ElapsedMilliseconds,7} ms  {what}");
                    return result;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{sw.ElapsedMilliseconds,7} ms  {what} FAILED: {ex.GetType().Name}: {ex.Message}");
                    throw;
                }
            }

            var root = await Time("root id (first request, includes token refresh)", () => client.RootId(token));
            await Time("root id again", () => client.RootId(token));
            var folder = await Time("create folder", () => client.CreateFolder("zz-probe-" + Guid.NewGuid().ToString("N")[..8], root, token));

            var local = Path.Combine(Path.GetTempPath(), "en-probe-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(local);
            var ids = new List<string>();
            try
            {
                for (int i = 0; i < 12; i++)
                    File.WriteAllText(Path.Combine(local, $"note{i}.txt"), "hello from the probe " + i);

                for (int i = 0; i < 3; i++)
                {
                    int n = i;
                    ids.Add(await Time($"upload note{n}.txt alone", () =>
                        client.Upload(Path.Combine(local, $"note{n}.txt"), $"note{n}.txt", folder, null, token)));
                }

                var all = Stopwatch.StartNew();
                // Four at a time, like DriveUpload.Streams.
                var gate = new SemaphoreSlim(4);
                var gated = Enumerable.Range(3, 8).Select(async n =>
                {
                    await gate.WaitAsync(token);
                    try
                    {
                        return await Time($"upload note{n}.txt (4 at a time)", () =>
                            client.Upload(Path.Combine(local, $"note{n}.txt"), $"note{n}.txt", folder, null, token));
                    }
                    finally { gate.Release(); }
                }).ToList();
                ids.AddRange(await Task.WhenAll(gated));
                Console.WriteLine($"{all.ElapsedMilliseconds,7} ms  all 8 parallel uploads");

                foreach (var id in ids.Take(3))
                    await Time("trash one file", async () => { await client.Trash(id, token); return 0; });

                // How many at once Google will take before it slows down: 48 small
                // files at each width, timed as a batch.
                for (int f = 0; f < 192; f++)
                    File.WriteAllText(Path.Combine(local, $"wide{f}.txt"), "width test " + f);
                foreach (int width in new[] { 16, 32, 48, 64 })
                {
                    var lanes = new SemaphoreSlim(width);
                    int retried = 0;
                    var batch = Stopwatch.StartNew();
                    var sends = Enumerable.Range(0, 192).Select(async f =>
                    {
                        await lanes.WaitAsync(token);
                        try
                        {
                            var one = Stopwatch.StartNew();
                            await client.Upload(Path.Combine(local, $"wide{f}.txt"), $"w{width}-{f}.txt", folder, null, token);
                            if (one.ElapsedMilliseconds > 4000) Interlocked.Increment(ref retried);
                        }
                        finally { lanes.Release(); }
                    }).ToList();
                    await Task.WhenAll(sends);
                    Console.WriteLine($"{batch.ElapsedMilliseconds,7} ms  192 small files, {width} at a time " +
                                      $"({192000.0 / batch.ElapsedMilliseconds:F1} files a second, {retried} took over 4s)");
                }
            }
            finally
            {
                try { await Time("trash the probe folder", async () => { await client.Trash(folder, token); return 0; }); } catch { }
                try { Directory.Delete(local, true); } catch { }
            }
        }
    }
}
