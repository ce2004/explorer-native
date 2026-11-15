using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// One line in errors.log for an exception that was caught and let go.
    ///
    /// Most catch blocks in this program are deliberate: a folder that vanished,
    /// a file another program holds, a network that dropped. Letting them go is
    /// right, but saying nothing made every "it just didn't work" report
    /// undiagnosable. <see cref="Note"/> writes the exception type, the file and
    /// the method, encrypted like every other log (read it with
    /// ExplorerNative --read-log errors.log), and costs the caller next to
    /// nothing: the line is queued and written on a worker, the same place and
    /// type is written at most once a minute, and the whole log is capped.
    /// Cancellation is never written: it is how work is stopped, not a failure.
    /// </summary>
    internal static class Diag
    {
        public const string FileName = "errors.log";

        /// <summary>When errors.log passes this it becomes errors.old.log and starts again.</summary>
        public const long MaxBytes = 1024 * 1024;

        /// <summary>The same place and exception type is written once in this long.</summary>
        public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(1);

        /// <summary>At most this many lines a minute in all, so a loop cannot fill the disk.</summary>
        public const int LinesPerMinute = 120;

        /// <summary>Off in a test that wants nothing written. On by default.</summary>
        public static bool Enabled { get; set; } = true;

        private static readonly ConcurrentQueue<string> Pending = new();
        private static readonly Dictionary<string, DateTime> LastSaid = new();
        private static readonly object Gate = new();
        private static DateTime _minuteStarted = DateTime.MinValue;
        private static int _linesThisMinute;
        private static int _writing;

        /// <summary>The path errors.log is written to (follows Settings.OverrideAppDataDir).</summary>
        public static string LogPath => Path.Combine(Settings.AppDataDir, FileName);

        /// <summary>
        /// Notes an exception that was caught and let go. Never throws and never blocks.
        /// </summary>
        public static void Note(Exception? ex,
            [CallerMemberName] string member = "",
            [CallerFilePath] string file = "")
        {
            try
            {
                if (!Enabled || ex == null) return;
                if (ex is OperationCanceledException || ex is ThreadInterruptedException) return;
                var line = Format(ex, member, file, DateTime.Now);
                if (!Admit(KeyOf(ex, member, file), DateTime.UtcNow)) return;
                Pending.Enqueue(line);
                if (Interlocked.Exchange(ref _writing, 1) == 0)
                    Task.Run(Drain);
            }
            catch
            {
                // The note about a failure must never become one.
            }
        }

        /// <summary>The line written for an exception: when, where, what type, and its message.</summary>
        internal static string Format(Exception ex, string member, string file, DateTime when)
        {
            var where = Path.GetFileNameWithoutExtension(file ?? "");
            var message = (ex.Message ?? "").Replace("\r", " ").Replace("\n", " ");
            if (message.Length > 200) message = message.Substring(0, 200) + "...";
            var hresult = ex.HResult != 0 ? $" (0x{ex.HResult:X8})" : "";
            return $"{when:yyyy-MM-dd HH:mm:ss}  {where}.{member}: {ex.GetType().Name}{hresult}: {message}";
        }

        internal static string KeyOf(Exception ex, string member, string file) =>
            file + "|" + member + "|" + ex.GetType().FullName;

        /// <summary>Whether a line for this key may be written now (once a minute per key, a cap per minute in all).</summary>
        internal static bool Admit(string key, DateTime nowUtc)
        {
            lock (Gate)
            {
                if (nowUtc - _minuteStarted >= TimeSpan.FromMinutes(1))
                {
                    _minuteStarted = nowUtc;
                    _linesThisMinute = 0;
                }
                if (_linesThisMinute >= LinesPerMinute) return false;
                if (LastSaid.TryGetValue(key, out var last) && nowUtc - last < Quiet) return false;
                if (LastSaid.Count > 4096) LastSaid.Clear();
                LastSaid[key] = nowUtc;
                _linesThisMinute++;
                return true;
            }
        }

        /// <summary>Forgets what was written, for tests.</summary>
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                LastSaid.Clear();
                _linesThisMinute = 0;
                _minuteStarted = DateTime.MinValue;
            }
            while (Pending.TryDequeue(out _)) { }
        }

        /// <summary>Writes whatever is queued now, for tests and for the bug report.</summary>
        internal static void Flush()
        {
            while (Interlocked.Exchange(ref _writing, 1) != 0) Thread.Sleep(5);
            try { WriteQueued(); }
            finally { Interlocked.Exchange(ref _writing, 0); }
        }

        private static void Drain()
        {
            try { WriteQueued(); }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _writing, 0);
                // A line queued after the last dequeue and before the flag was cleared.
                if (!Pending.IsEmpty && Interlocked.Exchange(ref _writing, 1) == 0)
                    Task.Run(Drain);
            }
        }

        private static void WriteQueued()
        {
            if (Pending.IsEmpty) return;
            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = Path.Combine(Path.GetDirectoryName(path)!, "errors.old.log");
                    File.Move(path, old, overwrite: true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            while (Pending.TryDequeue(out var line))
            {
                try { ProtectedFile.AppendLine(path, line); }
                catch { return; }
            }
        }
    }
}
