using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ExplorerNative
{
    /// <summary>
    /// What each synced folder did, file by file, for the History list in the
    /// sync window: uploaded, downloaded, deleted, moved, kept twice, failed
    /// and why. The last <see cref="Keep"/> lines per pair, newest last, in
    /// drive-monitor\&lt;id&gt;.history (encrypted like the rest). A pass's
    /// lines are held in memory and written when it ends; a pass of thousands
    /// keeps only its last <see cref="Keep"/> and says how many came before.
    /// </summary>
    internal static class SyncHistory
    {
        internal const int Keep = 1000;

        private static readonly object Gate = new();
        private static readonly Dictionary<string, Queue<string>> Pending = new();
        private static readonly Dictionary<string, int> Dropped = new();

        internal static string PathOf(string pairId) => Path.Combine(DriveMonitor.StateDirectory, pairId + ".history");

        /// <summary>One thing that happened to one file, such as ("Uploaded", "Album/song.mp3", null).</summary>
        public static void Add(string pairId, string what, string path, string? why = null, DateTime? at = null)
        {
            var line = string.Join("\t",
                (at ?? DateTime.UtcNow).ToString("O", CultureInfo.InvariantCulture),
                Clean(what), Clean(path), Clean(why ?? ""));
            lock (Gate)
            {
                if (!Pending.TryGetValue(pairId, out var queue)) Pending[pairId] = queue = new Queue<string>();
                queue.Enqueue(line);
                if (queue.Count > Keep)
                {
                    queue.Dequeue();
                    Dropped[pairId] = (Dropped.TryGetValue(pairId, out var n) ? n : 0) + 1;
                }
            }
        }

        private static string Clean(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        /// <summary>Writes a pass's lines after the ones already kept, keeping the last <see cref="Keep"/>.</summary>
        public static void Flush(string pairId)
        {
            Queue<string>? queue;
            int dropped;
            lock (Gate)
            {
                if (!Pending.Remove(pairId, out queue) || queue.Count == 0) return;
                Dropped.Remove(pairId, out dropped);
            }

            try
            {
                // A pass that dropped lines filled the whole history by itself:
                // its last Keep - 1 files, after one line saying how many more.
                var lines = dropped > 0 ? new List<string>() : ReadFile(pairId);
                lines.AddRange(queue);
                int room = dropped > 0 ? Keep - 1 : Keep;
                if (lines.Count > room)
                {
                    if (dropped > 0) dropped += lines.Count - room;
                    lines.RemoveRange(0, lines.Count - room);
                }
                if (dropped > 0)
                    lines.Insert(0, string.Join("\t", lines[0].Split('\t')[0], "And",
                        $"{dropped:N0} more files earlier in the same sync", ""));
                Directory.CreateDirectory(DriveMonitor.StateDirectory);
                ProtectedFile.WriteAllUtf8(PathOf(pairId), Encoding.UTF8.GetBytes(string.Join("\n", lines)));
            }
            catch { }
        }

        /// <summary>
        /// Lets go of a pass's lines unwritten: its pair was removed while it ran.
        /// Flushed, they wrote the history file back after Remove had deleted it,
        /// a list of file names kept for a sync that no longer exists.
        /// </summary>
        public static void Discard(string pairId)
        {
            lock (Gate)
            {
                Pending.Remove(pairId);
                Dropped.Remove(pairId);
            }
        }

        private static List<string> ReadFile(string pairId)
        {
            try
            {
                var path = PathOf(pairId);
                if (!File.Exists(path)) return new List<string>();
                return Encoding.UTF8.GetString(ProtectedFile.ReadAllUtf8(path))
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            catch { return new List<string>(); }
        }

        /// <summary>One line of history as the list reads it.</summary>
        internal sealed record Entry(DateTime Utc, string What, string Path, string Why);

        /// <summary>Everything kept for a pair, newest first, including a pass still running.</summary>
        public static List<Entry> Read(string pairId)
        {
            var lines = ReadFile(pairId);
            lock (Gate)
                if (Pending.TryGetValue(pairId, out var queue)) lines.AddRange(queue);

            var entries = new List<Entry>(lines.Count);
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var parts = lines[i].Split('\t');
                if (parts.Length < 4 ||
                    !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) continue;
                entries.Add(new Entry(at.ToUniversalTime(), parts[1], parts[2], parts[3]));
            }
            return entries;
        }

        /// <summary>
        /// "Uploaded Album/song.mp3, today at 8:31 PM" or, for a failure,
        /// "Could not sync notes.txt, yesterday at 9:02 AM: access denied".
        /// </summary>
        public static string Describe(Entry entry, DateTime nowLocal)
        {
            var local = entry.Utc.ToLocalTime();
            string day = local.Date == nowLocal.Date ? "today"
                : local.Date == nowLocal.Date.AddDays(-1) ? "yesterday"
                : local.ToString("d MMMM", CultureInfo.CurrentCulture);
            var text = $"{entry.What} {entry.Path}, {day} at {local.ToString("t", CultureInfo.CurrentCulture)}";
            return entry.Why.Length > 0 ? text + ": " + entry.Why : text;
        }
    }
}
