using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// Counters for the per-keystroke path, off unless asked for.
    ///
    /// Arrow-key cost is not something that can be reasoned about from the source:
    /// the list control, the accessibility layer and the screen reader all call
    /// back into this process, and the only way to know how often is to count.
    /// Set EXPLORERNATIVE_PERFLOG to a file path and the numbers are written there
    /// once a second; leave it unset and every method here is a no-op.
    /// </summary>
    internal static class PerfCounters
    {
        private static readonly string? LogPath =
            Environment.GetEnvironmentVariable("EXPLORERNATIVE_PERFLOG");

        public static bool Enabled => LogPath != null;

        private static long _retrieveCalls;
        private static long _itemsBuilt;
        private static long _cacheHits;
        private static long _selectionChanges;
        private static long _keyDowns;
        private static long _statusUpdates;
        private static long _buildTicks;

        private static Timer? _writer;

        public static void Start()
        {
            if (!Enabled || _writer != null) return;
            _writer = new Timer(_ => Write(), null, 1000, 1000);
        }

        public static void RetrieveCall() { if (Enabled) Interlocked.Increment(ref _retrieveCalls); }
        public static void CacheHit() { if (Enabled) Interlocked.Increment(ref _cacheHits); }
        public static void SelectionChange() { if (Enabled) Interlocked.Increment(ref _selectionChanges); }
        public static void KeyDown() { if (Enabled) Interlocked.Increment(ref _keyDowns); }
        public static void StatusUpdate() { if (Enabled) Interlocked.Increment(ref _statusUpdates); }

        /// <summary>
        /// A row we read out ourselves because the screen reader was going to
        /// treat the move as no move at all. Counted because "did that path run?"
        /// is otherwise only answerable by listening.
        /// </summary>
        public static void RowSpoken() { if (Enabled) Interlocked.Increment(ref _rowsSpoken); }

        private static long _rowsSpoken;

        public static void ItemBuilt(long ticks)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _itemsBuilt);
            Interlocked.Add(ref _buildTicks, ticks);
        }

        private static void Write()
        {
            try
            {
                double buildMs = Interlocked.Read(ref _buildTicks) * 1000.0 / Stopwatch.Frequency;
                File.AppendAllText(LogPath!,
                    $"keys={Interlocked.Read(ref _keyDowns)} " +
                    $"selchg={Interlocked.Read(ref _selectionChanges)} " +
                    $"retrieve={Interlocked.Read(ref _retrieveCalls)} " +
                    $"built={Interlocked.Read(ref _itemsBuilt)} " +
                    $"cachehit={Interlocked.Read(ref _cacheHits)} " +
                    $"status={Interlocked.Read(ref _statusUpdates)} " +
                    $"rowsSpoken={Interlocked.Read(ref _rowsSpoken)} " +
                    $"buildMs={buildMs:0.0}" + Environment.NewLine);
            }
            catch { }
        }
    }
}
