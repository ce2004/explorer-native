using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ExplorerNative
{
    /// <summary>
    /// Copies and moves still running when an update or a restart closed the
    /// app, to be finished by the next start (Conner, 5.6.1: "can it attempt to
    /// retry the operation if conditions let it? it should ask that question").
    /// Written encrypted to AppData on the way out and taken once by the next
    /// start; an old note (a quit that never restarted) is ignored. The next start
    /// pastes each again with Fill in what is missing, which is how a copy that
    /// stopped part way is finished.
    /// </summary>
    internal static class CarryOnTransfers
    {
        internal enum Kind { Copy, Move, EmptyTrash }

        /// <summary>A paste (Copy or Move of Paths into Destination), or emptying the Google Drive trash.</summary>
        internal sealed record Job(Kind What, string[] Paths, string Destination)
        {
            public bool Move => What == Kind.Move;
        }

        /// <summary>Longer than a quit can take, short enough that nobody is surprised by it.</summary>
        internal static readonly TimeSpan GoodFor = TimeSpan.FromMinutes(30);

        /// <summary>How long the next start waits for the folders to be there (a share, a USB drive, the Drive letter).</summary>
        internal static readonly TimeSpan WaitForFolders = TimeSpan.FromMinutes(5);

        internal static string NotePath => Path.Combine(Settings.AppDataDir, "carry-on-transfers");

        public static void Save(IReadOnlyList<Job> jobs) => Save(jobs, DateTime.UtcNow);

        internal static void Save(IReadOnlyList<Job> jobs, DateTime atUtc)
        {
            try
            {
                if (jobs.Count == 0) { File.Delete(NotePath); return; }
                ProtectedFile.WriteAllBytes(NotePath, Encoding.UTF8.GetBytes(Encode(jobs, atUtc)));
            }
            catch { }
        }

        /// <summary>The jobs a fresh note holds; the note is deleted either way.</summary>
        public static List<Job> Take() => Take(DateTime.UtcNow);

        internal static List<Job> Take(DateTime nowUtc)
        {
            try
            {
                if (!File.Exists(NotePath)) return new List<Job>();
                string text;
                try { text = Encoding.UTF8.GetString(ProtectedFile.ReadAllBytes(NotePath)); }
                finally { try { File.Delete(NotePath); } catch { } }
                return Decode(text, nowUtc);
            }
            catch { return new List<Job>(); }
        }

        /// <summary>Whether a note is waiting, without taking it: the window opens for it.</summary>
        public static bool Waiting()
        {
            try { return File.Exists(NotePath); } catch { return false; }
        }

        // One line with the time, then per job "J", move flag and destination,
        // then "P" and one source per line. A tab can't be in a Windows path.
        internal static string Encode(IReadOnlyList<Job> jobs, DateTime atUtc)
        {
            var text = new StringBuilder();
            text.Append(atUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var job in jobs)
            {
                char kind = job.What switch { Kind.Move => '1', Kind.EmptyTrash => 'T', _ => '0' };
                text.Append("J\t").Append(kind).Append('\t').Append(job.Destination).Append('\n');
                foreach (var path in job.Paths) text.Append("P\t").Append(path).Append('\n');
            }
            return text.ToString();
        }

        internal static List<Job> Decode(string text, DateTime nowUtc)
        {
            var jobs = new List<Job>();
            var lines = text.Split('\n');
            if (lines.Length == 0 ||
                !long.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) ||
                ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return jobs;
            var age = nowUtc - new DateTime(ticks, DateTimeKind.Utc);
            if (age < TimeSpan.FromMinutes(-1) || age >= GoodFor) return jobs;

            string? destination = null;
            Kind kind = Kind.Copy;
            bool open = false;
            var paths = new List<string>();
            void Finish()
            {
                if (open && kind == Kind.EmptyTrash) jobs.Add(new Job(kind, Array.Empty<string>(), destination ?? ""));
                else if (open && destination != null && paths.Count > 0) jobs.Add(new Job(kind, paths.ToArray(), destination));
                paths.Clear();
                open = false;
            }
            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split('\t');
                if (parts.Length == 3 && parts[0] == "J")
                {
                    Finish();
                    kind = parts[1] switch { "1" => Kind.Move, "T" => Kind.EmptyTrash, _ => Kind.Copy };
                    destination = parts[2].Length > 0 ? parts[2] : null;
                    open = parts[1] is "0" or "1" or "T";
                }
                else if (parts.Length == 2 && parts[0] == "P" && parts[1].Length > 0) paths.Add(parts[1]);
            }
            Finish();
            return jobs;
        }
    }
}
