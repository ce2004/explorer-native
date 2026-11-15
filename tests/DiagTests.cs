using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Diag: the one line in errors.log for an exception caught and let go.
    /// The line names the type and the method, cancellation is never written,
    /// one place and type is written once a minute, the whole log is capped,
    /// the file is encrypted and reads back, and it rolls over past MaxBytes.
    /// Group: diag.
    /// </summary>
    internal static class DiagTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;
            Console.WriteLine("Diag, the errors log:");

            var previousDir = Settings.OverrideAppDataDir;
            var previousEnabled = Diag.Enabled;
            var dir = Path.Combine(Path.GetTempPath(), "en-diag-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            Settings.OverrideAppDataDir = dir;
            Diag.Enabled = true;
            try
            {
                LineFormat();
                Fresh();
                WrittenAndEncrypted(dir);
                Fresh();
                CancellationIsNotWritten();
                Fresh();
                OncePerPlace();
                Fresh();
                Capped();
                Fresh();
                RollsOver(dir);
            }
            finally
            {
                Diag.Flush();
                Diag.ResetForTests();
                Diag.Enabled = previousEnabled;
                Settings.OverrideAppDataDir = previousDir;
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
            return Task.CompletedTask;
        }

        private static void Fresh()
        {
            Diag.Flush();
            Diag.ResetForTests();
            try { File.Delete(Diag.LogPath); } catch { }
        }

        private static string[] Lines() =>
            File.Exists(Diag.LogPath) ? ProtectedFile.ReadLog(Diag.LogPath).Where(l => l.Length > 0).ToArray() : Array.Empty<string>();

        private static void LineFormat()
        {
            var when = new DateTime(2026, 11, 15, 9, 30, 5);
            var line = Diag.Format(new IOException("the disk said no\r\nsecond line"), "SaveTheThing",
                @"C:\code\src\Widget.cs", when);
            Check("diag line has the file and method", line.Contains("Widget.SaveTheThing"), line);
            Check("diag line has the exception type", line.Contains("IOException"), line);
            Check("diag line has the message on one line", line.Contains("the disk said no  second line") && !line.Contains('\n'), line);
            Check("diag line starts with the time", line.StartsWith("2026-11-15 09:30:05"), line);

            var longMessage = Diag.Format(new InvalidOperationException(new string('x', 500)), "M", "F.cs", when);
            Check("diag cuts a long message", longMessage.Length < 300 && longMessage.EndsWith("..."), longMessage.Length.ToString());
        }

        private static void WrittenAndEncrypted(string dir)
        {
            Diag.Note(new InvalidDataException("a secret-looking message"));
            Diag.Flush();
            var lines = Lines();
            Equal("diag writes one line", "1", lines.Length.ToString());
            var only = lines.FirstOrDefault() ?? "";
            Check("diag line read back names this method", only.Contains("DiagTests.WrittenAndEncrypted"), only);
            Check("diag line read back names the type", only.Contains("InvalidDataException"), only);
            Check("errors.log is in the app data folder", Diag.LogPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase), Diag.LogPath);
            var raw = Encoding.UTF8.GetString(File.ReadAllBytes(Diag.LogPath));
            Check("errors.log is encrypted on disk", !raw.Contains("secret-looking") && !raw.Contains("InvalidDataException"), raw.Length.ToString());
        }

        private static void CancellationIsNotWritten()
        {
            Diag.Note(new OperationCanceledException());
            Diag.Note(new TaskCanceledException());
            Diag.Note(null);
            Diag.Flush();
            Equal("cancellation and null write nothing", "0", Lines().Length.ToString());
        }

        private static void OncePerPlace()
        {
            for (int i = 0; i < 5; i++) Diag.Note(new IOException("again " + i));
            Diag.Note(new UnauthorizedAccessException("another type"));
            Diag.Flush();
            var lines = Lines();
            Equal("one place and type is written once, another type too", "2", lines.Length.ToString());

            var now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Diag.ResetForTests();
            Check("first note at a place is admitted", Diag.Admit("k", now));
            Check("the same place is quiet within the minute", !Diag.Admit("k", now.AddSeconds(30)));
            Check("another place is admitted", Diag.Admit("other", now.AddSeconds(30)));
            Check("the same place is admitted after a minute", Diag.Admit("k", now + Diag.Quiet + TimeSpan.FromSeconds(1)));
        }

        private static void Capped()
        {
            var now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            int admitted = 0;
            for (int i = 0; i < Diag.LinesPerMinute + 30; i++)
                if (Diag.Admit("place" + i, now.AddMilliseconds(i))) admitted++;
            Equal("at most LinesPerMinute lines a minute", Diag.LinesPerMinute.ToString(), admitted.ToString());
            Check("lines are admitted again the next minute", Diag.Admit("later", now.AddMinutes(1).AddSeconds(1)));
        }

        private static void RollsOver(string dir)
        {
            var old = Path.Combine(dir, "errors.old.log");
            File.WriteAllBytes(Diag.LogPath, new byte[Diag.MaxBytes + 10]);
            Diag.Note(new IOException("after the roll"));
            Diag.Flush();
            Check("past MaxBytes the log becomes errors.old.log", File.Exists(old) && new FileInfo(old).Length == Diag.MaxBytes + 10);
            var lines = Lines();
            Check("the new log holds only the new line", lines.Length == 1 && lines[0].Contains("after the roll"), string.Join(" / ", lines));
        }
    }
}
