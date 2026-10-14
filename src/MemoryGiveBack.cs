using System;
using System.Runtime;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// Hands memory the app has finished with back to Windows.
    ///
    /// The Drive cache holds files in 4MB blocks and the player holds a
    /// streamed track whole; both are big arrays, which .NET keeps on its large
    /// object heap and does not give back until a full, compacting collection
    /// that an idle app may not have for hours. So a cache that dropped 600MB
    /// still showed as 600MB in Task Manager. Whoever lets go of a lot says how
    /// much here; once 64MB or more has been let go, one compacting collection
    /// runs two seconds later (so a burst of releases is one collection).
    /// </summary>
    internal static class MemoryGiveBack
    {
        /// <summary>Let go of at least this much before a collection is worth it.</summary>
        internal static long AboveBytes { get; set; } = 64L * 1024 * 1024;

        /// <summary>How long after the release that crossed the line the collection runs.</summary>
        internal static TimeSpan After { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Collections run so far. For tests.</summary>
        internal static int Collections => Volatile.Read(ref _collections);

        private static readonly object Gate = new();
        private static long _freed;
        private static int _collections;
        private static Timer? _timer;

        /// <summary>Says that <paramref name="bytes"/> of big arrays are no longer referenced.</summary>
        public static void Freed(long bytes)
        {
            if (bytes <= 0) return;
            if (Interlocked.Add(ref _freed, bytes) < AboveBytes) return;
            lock (Gate)
            {
                _timer ??= new Timer(_ => Run());
                _timer.Change(After, Timeout.InfiniteTimeSpan);
            }
        }

        private static void Run()
        {
            if (Interlocked.Exchange(ref _freed, 0) < AboveBytes) return;
            Collect();
        }

        /// <summary>One compacting collection that returns what is free to Windows, now.</summary>
        public static void Collect()
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                Interlocked.Increment(ref _collections);
            }
            catch { }
        }
    }
}
