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
        /// <remarks>
        /// Policy is what was chosen for names already there: a
        /// PasteConflictPolicy's name, <see cref="NotChosen"/> when it was
        /// stopped before that was settled, or empty for a note written by 5.6
        /// (finished with Fill gaps, as 5.6 did). Clashed is the sources whose
        /// name the destination already had when it began; null when unknown.
        /// </remarks>
        internal sealed record Job(Kind What, string[] Paths, string Destination, string Policy = "", string[]? Clashed = null)
        {
            public bool Move => What == Kind.Move;
        }

        internal const string NotChosen = "none";

        /// <summary>One paste of the finishing: these sources, with this answer for names already there (null: decide as any paste does).</summary>
        internal sealed record Run(string[] Paths, PasteConflictPolicy? Forced);

        /// <summary>
        /// How a paste that stopped part way is finished, given the sources
        /// still there. What was new at the destination is ours, so only what is
        /// missing is filled in. What clashed keeps the answer it was given:
        /// Replace replaces, Skip stays skipped. A second copy ("X (2)") cannot
        /// be found again by name, so those are left out and said (6.0.0: every
        /// finish ran as Fill gaps, which merged them into the folder already
        /// there and left Replace's files unreplaced).
        /// </summary>
        internal static (List<Run> Runs, string[] LeftOut) Plan(Job job, IReadOnlyList<string> present)
        {
            var runs = new List<Run>();
            if (present.Count == 0) return (runs, Array.Empty<string>());
            if (job.Policy == NotChosen) { runs.Add(new Run(present.ToArray(), null)); return (runs, Array.Empty<string>()); }
            if (!Enum.TryParse<PasteConflictPolicy>(job.Policy, out var policy) || policy == PasteConflictPolicy.FillGaps)
            {
                runs.Add(new Run(present.ToArray(), PasteConflictPolicy.FillGaps));
                return (runs, Array.Empty<string>());
            }

            var clashedSet = job.Clashed == null ? null : new HashSet<string>(job.Clashed, StringComparer.OrdinalIgnoreCase);
            var fresh = clashedSet == null ? Array.Empty<string>() : present.Where(p => !clashedSet.Contains(p)).ToArray();
            var clashed = clashedSet == null ? present.ToArray() : present.Where(clashedSet.Contains).ToArray();
            if (fresh.Length > 0) runs.Add(new Run(fresh, PasteConflictPolicy.FillGaps));
            var leftOut = Array.Empty<string>();
            if (clashed.Length > 0)
            {
                if (policy == PasteConflictPolicy.Overwrite) runs.Add(new Run(clashed, PasteConflictPolicy.Overwrite));
                else if (policy == PasteConflictPolicy.Skip && clashedSet != null) { /* skipped then, skipped now */ }
                else leftOut = clashed;
            }
            return (runs, leftOut);
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
            try
            {
                var note = new FileInfo(NotePath);
                if (!note.Exists) return false;
                // A note nobody took in time (an update that failed, then a
                // quit) must not open the window on some later start.
                if (DateTime.UtcNow - note.LastWriteTimeUtc < GoodFor) return true;
                note.Delete();
                return false;
            }
            catch { return false; }
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
                if (job.Policy.Length > 0)
                    text.Append("O\t").Append(job.Policy).Append('\t').Append(job.Clashed == null ? '0' : '1').Append('\n');
                foreach (var path in job.Paths) text.Append("P\t").Append(path).Append('\n');
                foreach (var path in job.Clashed ?? Array.Empty<string>()) text.Append("C\t").Append(path).Append('\n');
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
            var clashed = new List<string>();
            string policy = "";
            bool clashKnown = false;
            void Finish()
            {
                if (open && kind == Kind.EmptyTrash) jobs.Add(new Job(kind, Array.Empty<string>(), destination ?? ""));
                else if (open && destination != null && paths.Count > 0)
                    jobs.Add(new Job(kind, paths.ToArray(), destination, policy, clashKnown ? clashed.ToArray() : null));
                paths.Clear();
                clashed.Clear();
                policy = "";
                clashKnown = false;
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
                else if (parts.Length == 3 && parts[0] == "O") { policy = parts[1]; clashKnown = parts[2] == "1"; }
                else if (parts.Length == 2 && parts[0] == "P" && parts[1].Length > 0) paths.Add(parts[1]);
                else if (parts.Length == 2 && parts[0] == "C" && parts[1].Length > 0) clashed.Add(parts[1]);
            }
            Finish();
            return jobs;
        }
    }
}
