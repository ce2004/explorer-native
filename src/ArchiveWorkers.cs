using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// How hard compressing and extracting may push the machine, and the one
    /// way their work is started.
    ///
    /// Conner, 4.2.5: "make it use 100 percent of what's available but don't
    /// freeze the PC". Both halves are answered by priority, not by holding
    /// cores back. Every core is given work, and every thread doing archive
    /// work runs below normal priority (7-Zip and Windows' tar run in the
    /// below-normal priority class). Windows always runs a ready
    /// normal-priority thread first, so the window, NVDA and anything else
    /// in use take what they need the moment they need it, and the archive
    /// gets everything left over. Leaving a core idle, as 4.2.1 did, wasted
    /// an eighth of the machine whenever nothing else wanted it, and still
    /// protected nothing when something wanted two.
    ///
    /// Memory works the other way round: priority does nothing for memory,
    /// so what is held is sized from what is free now, leaving the larger
    /// of 2GB or 15 percent of the machine for everything else.
    /// </summary>
    public static class ArchiveWorkers
    {
        /// <summary>Every core. See the class summary for why none is left idle.</summary>
        public static int Threads => Math.Clamp(Environment.ProcessorCount, 1, 64);

        /// <summary>
        /// Runs <paramref name="work"/> on the pool at below-normal priority,
        /// putting the thread's own priority back afterwards (pool threads are
        /// shared with the rest of the app).
        /// </summary>
        public static Task<T> Run<T>(Func<T> work, CancellationToken token = default) =>
            Task.Run(() => Lowered(work), token);

        public static Task Run(Action work, CancellationToken token = default) =>
            Task.Run(() => Lowered(() => { work(); return 0; }), token);

        /// <summary>
        /// For work that waits for long stretches — a writer waiting on a
        /// queue, the thread that drives a whole archive — on a thread of its
        /// own, below normal. Waiting on a pool thread, eight tar writers held
        /// all eight of the pool's threads, and the gzip pieces they were
        /// waiting for queued behind them until the pool slowly grew: a
        /// .tar.gz unpacked no faster on eight cores than on one.
        /// </summary>
        public static Task<T> RunLong<T>(Func<T> work, CancellationToken token = default) =>
            Task.Factory.StartNew(() => Lowered(work), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        public static Task RunLong(Action work, CancellationToken token = default) =>
            Task.Factory.StartNew(() => Lowered(() => { work(); return 0; }), token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);

        /// <summary>Runs <paramref name="work"/> on this thread at below-normal priority, then puts it back.</summary>
        public static T Lowered<T>(Func<T> work)
        {
            var thread = Thread.CurrentThread;
            ThreadPriority before;
            try { before = thread.Priority; }
            catch { return work(); }

            bool lowered = false;
            if (before > ThreadPriority.BelowNormal)
            {
                try { thread.Priority = ThreadPriority.BelowNormal; lowered = true; }
                catch { }
            }

            try { return work(); }
            finally
            {
                if (lowered)
                {
                    try { thread.Priority = before; }
                    catch { }
                }
            }
        }

        /// <summary>A dedicated thread for archive work: a background thread, below normal.</summary>
        public static Thread NewThread(ThreadStart body, string name) => new(body)
        {
            IsBackground = true,
            Name = name,
            Priority = ThreadPriority.BelowNormal,
        };

        // ---------- Memory ----------

        /// <summary>The least that is always left free for everything else: 2GB or 15 percent, whichever is more.</summary>
        public static long Headroom(long totalPhysical) =>
            Math.Max(2L << 30, totalPhysical / 100 * 15);

        /// <summary>
        /// How much memory archive work may take right now: what is free,
        /// less <see cref="Headroom"/>. Zero when the machine is already short.
        /// </summary>
        public static long Spare()
        {
            var (total, available) = Memory();
            if (total <= 0) return 0;
            return Math.Max(0, available - Headroom(total));
        }

        /// <summary>
        /// The most uncompressed data the zip writer may hold in memory, waiting
        /// to be written: 64MB a worker, within a quarter of <see cref="Spare"/>,
        /// never under 128MB (a small budget only slows the writer down, it
        /// cannot make an entry fail) and never over 2GB.
        /// </summary>
        public static long ZipBudget(int lanes) => ZipBudget(lanes, Spare());

        internal static long ZipBudget(int lanes, long spare) =>
            Math.Clamp(Math.Min(Math.Max(1, lanes) * (64L << 20), spare / 4), 128L << 20, 2L << 30);

        /// <summary>
        /// The memory 7-Zip may use, in megabytes, for its -mmemuse switch:
        /// <see cref="Spare"/>, at least 256MB. 7-Zip uses fewer threads or a
        /// smaller window rather than go past it.
        /// </summary>
        public static long SevenZipMegabytes() => SevenZipMegabytes(Spare());

        internal static long SevenZipMegabytes(long spare) => Math.Max(256, spare >> 20);

        /// <summary>The machine's physical memory and how much of it is free now, in bytes; zeros if Windows will not say.</summary>
        public static (long Total, long Available) Memory()
        {
            try
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (GlobalMemoryStatusEx(ref status))
                    return ((long)Math.Min(status.TotalPhysical, long.MaxValue),
                        (long)Math.Min(status.AvailablePhysical, long.MaxValue));
            }
            catch { }
            return (0, 0);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    }
}
