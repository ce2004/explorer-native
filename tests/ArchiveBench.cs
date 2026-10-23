using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Not a test: times every format the app makes and unpacks, through the
    /// real engine, on the folder in EXPLORERNATIVE_BENCH_DIR.
    /// EXPLORERNATIVE_SELFTEST_ONLY=archivebench.
    /// </summary>
    internal static class ArchiveBench
    {
        public static async Task Run()
        {
            var source = Environment.GetEnvironmentVariable("EXPLORERNATIVE_BENCH_DIR");
            if (string.IsNullOrEmpty(source) || !Directory.Exists(source)) { Console.WriteLine("set EXPLORERNATIVE_BENCH_DIR"); return; }
            var formats = (Environment.GetEnvironmentVariable("EXPLORERNATIVE_BENCH_FORMATS") ?? "zip,tar,tar.gz,7z,tar.xz,tar.zst,tar.bz2").Split(',');
            var work = Path.Combine(Path.GetTempPath(), "EN-bench-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(work);
            Console.WriteLine($"threads {ArchiveEngine.RecommendedThreads}, 7-Zip {BsdTar.SevenZipExecutable != null}");
            try
            {
                foreach (var id in formats)
                {
                    var format = ArchiveFormats.ById(id);
                    if (format == null) { Console.WriteLine($"{id}: unknown"); continue; }
                    var archive = Path.Combine(work, "b" + format.Extension);
                    var cpu = Process.GetCurrentProcess().TotalProcessorTime;
                    var clock = Stopwatch.StartNew();
                    var made = await ArchiveEngine.CompressAsync(new[] { source }, archive, format, ArchiveLevel.Balanced,
                        ArchiveEngine.RecommendedThreads, null, CancellationToken.None);
                    var makeTime = clock.Elapsed;
                    var makeCpu = Process.GetCurrentProcess().TotalProcessorTime - cpu;
                    long size = File.Exists(archive) ? new FileInfo(archive).Length : 0;

                    var into = Path.Combine(work, "x-" + id);
                    cpu = Process.GetCurrentProcess().TotalProcessorTime;
                    clock.Restart();
                    var back = await ArchiveEngine.ExtractAsync(archive, into, PasteConflictPolicy.AutoRename, null,
                        ArchiveEngine.RecommendedThreads, null, CancellationToken.None);
                    var unpackTime = clock.Elapsed;
                    var unpackCpu = Process.GetCurrentProcess().TotalProcessorTime - cpu;

                    Console.WriteLine($"{id,-8} make {makeTime.TotalSeconds,6:0.0}s (in-process cpu {makeCpu.TotalSeconds,5:0.0}s) " +
                                      $"{size / 1048576.0,6:0} MB   unpack {unpackTime.TotalSeconds,5:0.0}s (cpu {unpackCpu.TotalSeconds,5:0.0}s)" +
                                      $"  failed {made.Failed}/{back.Failed}");
                    try { Directory.Delete(into, true); File.Delete(archive); } catch { }
                }
            }
            finally { try { Directory.Delete(work, true); } catch { } }
        }
    }
}
