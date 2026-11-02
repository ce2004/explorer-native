using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ExplorerNative
{
    /// <summary>
    /// What Google Drive says while the application is quitting, kept so it
    /// still reaches somebody.
    ///
    /// Quitting drains the Drive letter's uploads for up to a minute and then
    /// names whatever did not make it (drive.save.failed). That sentence is the
    /// only record that a file is missing from Drive, and it arrives after the
    /// tray has stopped posting to its message loop, so it was dropped. The tray
    /// holds it here instead, says it before it goes, and leaves it in a file
    /// the next start reads out once. Held messages can come from an upload
    /// worker, hence the concurrent queue.
    /// </summary>
    internal sealed class QuitNotices
    {
        /// <summary>The messages worth keeping past the end: a file that did not reach Drive.</summary>
        internal static readonly string[] HeldIds = { "drive.save.failed" };

        private readonly ConcurrentQueue<string> _held = new();

        /// <summary>Keeps <paramref name="message"/> if its id is one worth keeping, and says whether it did.</summary>
        public bool Hold(string id, string message)
        {
            if (string.IsNullOrEmpty(message) || !HeldIds.Contains(id, StringComparer.Ordinal)) return false;
            _held.Enqueue(message);
            return true;
        }

        /// <summary>Everything held so far, once.</summary>
        public IReadOnlyList<string> TakeHeld()
        {
            var taken = new List<string>();
            while (_held.TryDequeue(out var message)) taken.Add(message);
            return taken;
        }

        private static string FilePath => Path.Combine(Settings.AppDataDir, "drive-unsaved.txt");

        /// <summary>
        /// Leaves the messages for the next start, which follows at once after a
        /// restart or an update, and after a sign-out is the first chance to
        /// say them to anybody. Added to whatever an earlier run left unread.
        /// </summary>
        public static void SaveForNextStart(IEnumerable<string> messages)
        {
            try
            {
                var lines = TakeFromLastRun().Concat(messages.Where(m => !string.IsNullOrWhiteSpace(m))
                    .Select(m => m.Replace("\r", " ").Replace("\n", " "))).ToList();
                if (lines.Count == 0) return;
                Directory.CreateDirectory(Settings.AppDataDir);
                ProtectedFile.WriteAllText(FilePath, string.Join("\n", lines));
            }
            catch
            {
                // The log has it, and it was said; a file that cannot be written
                // is no reason to hold up quitting.
            }
        }

        /// <summary>What the last run left, once: the file goes whatever it held.</summary>
        public static string[] TakeFromLastRun()
        {
            try
            {
                if (!File.Exists(FilePath)) return Array.Empty<string>();
                return ProtectedFile.ReadAllText(FilePath)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            catch { return Array.Empty<string>(); }
            finally
            {
                // A file that could not be read would fail again at every start.
                try { File.Delete(FilePath); } catch { }
            }
        }

        /// <summary>A message from the last run, said as one.</summary>
        public static string AsLastTime(string message)
        {
            if (message.StartsWith("Could not ", StringComparison.Ordinal)) message = "c" + message[1..];
            return "Last time Explorer Native closed, " + message;
        }
    }
}
