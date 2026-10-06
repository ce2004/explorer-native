using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// Where each entry in an archive is allowed to land, decided once before
    /// anything is written.
    ///
    /// Deciding up front rather than file by file is the whole design, and it
    /// comes straight from what a paste already does. An extraction that asked
    /// about each collision as it reached it would put a modal question in front
    /// of somebody eleven minutes into a ten thousand file archive, with half of
    /// it already on disk and no useful answer available: saying "skip" then does
    /// not undo what has landed, and saying "cancel" leaves a folder that is
    /// neither the old contents nor the new. The archive index is read first
    /// anyway — the progress window needs the totals — so the names are all known
    /// before a byte is written, and the one question can be asked at the one
    /// moment when every answer to it is still true.
    ///
    /// The question is asked about top-level names only, exactly as a paste asks
    /// it. An archive containing "photos/2019/a.jpg" being extracted where a
    /// "photos" folder exists is one collision called "photos", not four thousand
    /// called "a.jpg" — and renaming makes "photos (2)" and puts the whole tree
    /// inside it, so the structure survives. The exception is Fill gaps, which
    /// exists precisely to go into what is already there and answer the question
    /// per file underneath.
    /// </summary>
    public sealed class ExtractionRules
    {
        private readonly string _root;
        private readonly Dictionary<string, string> _renames;
        private readonly HashSet<string> _skippedTops;
        private readonly bool _fillGaps;
        private readonly bool _overwrite;

        private readonly object _gate = new();
        private readonly List<string> _renamedNames = new();
        private readonly List<string> _skippedNames = new();
        private readonly List<string> _overwrittenNames = new();
        private int _renamedCount, _skippedCount, _overwrittenCount;

        private ExtractionRules(string root, Dictionary<string, string> renames,
            HashSet<string> skippedTops, bool fillGaps, bool overwrite)
        {
            _root = root;
            _renames = renames;
            _skippedTops = skippedTops;
            _fillGaps = fillGaps;
            _overwrite = overwrite;
        }

        /// <summary>
        /// Works out the rules, asking the one question if there is one to ask.
        ///
        /// Returns null when the answer was to not extract at all, which the
        /// caller reports as a cancellation rather than as a failure — somebody
        /// pressing Cancel on a question they were asked has not had anything go
        /// wrong.
        /// </summary>
        public static ExtractionRules? Build(
            IReadOnlyList<ArchiveEntryInfo> entries,
            string destination,
            PasteConflictPolicy policy,
            Func<IReadOnlyList<string>, FileOperations.ConflictChoice>? ask,
            CancellationToken token)
        {
            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var tops = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();

                // A network path is refused by TargetFor; its server's name is
                // not a name here to ask about or rename.
                if (entry.Name.Replace('\\', '/').StartsWith("//", StringComparison.Ordinal)) continue;

                var top = TopSegment(entry.Name);

                // ".." or "C:" is not a name to clash with or rename: renamed to
                // ".. (2)" it slipped past the refusal that exists for it and was
                // announced as saved. Left out here, SafeTarget refuses it.
                if (top == null || top == ".." || NameRules.DescribeBadName(top) != null) continue;
                if (seen.Add(top)) tops.Add(top);
            }

            var clashing = new List<string>();
            foreach (var top in tops)
            {
                var at = Path.Combine(destination, top);
                bool there;
                try { there = File.Exists(at) || Directory.Exists(at); }
                catch { there = false; }
                if (there) clashing.Add(top);
            }

            if (clashing.Count == 0)
                return new ExtractionRules(destination, renames, skipped, fillGaps: false, overwrite: false);

            var effective = policy;
            if (policy == PasteConflictPolicy.Ask)
            {
                var choice = ask?.Invoke(clashing) ?? FileOperations.ConflictChoice.Rename;
                effective = choice switch
                {
                    FileOperations.ConflictChoice.Overwrite => PasteConflictPolicy.Overwrite,
                    FileOperations.ConflictChoice.Skip => PasteConflictPolicy.Skip,
                    FileOperations.ConflictChoice.FillGaps => PasteConflictPolicy.FillGaps,
                    FileOperations.ConflictChoice.Rename => PasteConflictPolicy.AutoRename,
                    _ => PasteConflictPolicy.Ask,
                };

                // Ask still meaning Ask is the one answer that is not a policy:
                // it is Cancel.
                if (effective == PasteConflictPolicy.Ask) return null;
            }

            var rules = effective switch
            {
                PasteConflictPolicy.Skip => Skipping(destination, clashing),
                PasteConflictPolicy.Overwrite => new ExtractionRules(destination, renames, skipped, false, true),
                PasteConflictPolicy.FillGaps => new ExtractionRules(destination, renames, skipped, true, false),
                _ => Renaming(destination, clashing, entries),
            };

            return rules;
        }

        private static ExtractionRules Skipping(string destination, List<string> clashing)
        {
            var skipped = new HashSet<string>(clashing, StringComparer.OrdinalIgnoreCase);
            return new ExtractionRules(destination,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), skipped, false, false);
        }

        private static ExtractionRules Renaming(string destination, List<string> clashing,
            IReadOnlyList<ArchiveEntryInfo> entries)
        {
            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Whether the top-level name is a folder decides how it is renumbered:
            // a folder becomes "photos (2)" and a file becomes "notes (2).txt".
            // The archive knows which it is, and asking the filesystem instead
            // would answer about the thing already there rather than the thing
            // arriving.
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var top = TopSegment(entry.Name);
                if (top == null) continue;
                if (entry.IsDirectory || Normalise(entry.Name).Contains('/'))
                    folders.Add(top);
            }

            // Every top-level name the archive itself brings, and every name
            // handed out so far. A new name has to clear all three — what is on
            // disk, what is arriving, and what has already been chosen — or two
            // things end up at one path.
            //
            // Against the filesystem alone it did not. Extracting an archive
            // holding both "a" and "a (2)" into a folder that already has "a"
            // renamed the arriving "a" to "a (2)", because nothing called "a (2)"
            // was on disk yet — and the archive's own "a (2)" was not renamed at
            // all, since it did not clash with anything. The two trees were
            // written into one folder and same-named files inside them
            // overwrote each other, reported as a clean extraction.
            var arriving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var top = TopSegment(entry.Name);
                if (top != null) arriving.Add(top);
            }

            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var top in clashing)
            {
                bool Taken(string candidate)
                {
                    if (claimed.Contains(candidate)) return true;

                    // The name being renamed is the one arriving; any *other*
                    // name the archive brings is a real obstacle.
                    if (!string.Equals(candidate, top, StringComparison.OrdinalIgnoreCase) &&
                        arriving.Contains(candidate)) return true;

                    var at = Path.Combine(destination, candidate);
                    try { return File.Exists(at) || Directory.Exists(at); }
                    catch { return false; }
                }

                var name = NameRules.UniqueAmong(top, Taken, folders.Contains(top));

                claimed.Add(name);
                renames[top] = name;
            }

            return new ExtractionRules(destination, renames,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);
        }

        private static string? TopSegment(string entryName)
        {
            var cleaned = Normalise(entryName);
            if (cleaned.Length == 0) return null;
            int slash = cleaned.IndexOf('/');
            return slash < 0 ? cleaned : cleaned[..slash];
        }

        /// <summary>
        /// An entry name with forward slashes, and without empty or "." segments.
        ///
        /// "./" is how `tar -czf x.tgz .` names every entry, and the "." segment
        /// was refused as pointing outside the folder — so a tarball made that
        /// way extracted nothing, and its top-level name was the destination
        /// itself, which always clashed. ".." is left for SafeTarget to refuse.
        /// </summary>
        internal static string Normalise(string entryName)
        {
            var parts = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            return string.Join('/', parts.Where(p => p != "."));
        }

        /// <summary>
        /// Where this entry goes, or null if it is not to be written.
        ///
        /// <paramref name="problem"/> is set only when the entry was refused
        /// rather than merely skipped — a name that would land outside the
        /// destination, or one Windows cannot store. Those are reported; a skip
        /// is an answer somebody chose and is counted instead.
        ///
        /// Called from every worker at once, so the bookkeeping is under a lock.
        /// It is a handful of list appends against a file being written, which is
        /// not a contention anybody will measure.
        /// </summary>
        public string? TargetFor(string entryName, bool isDirectory, out string? problem)
        {
            problem = null;

            // Before the name is cleaned up, which drops empty segments and so
            // turns a network path — two slashes, a server, a share — into an
            // ordinary relative one that extracted quietly into a folder named
            // after the server, rather than being refused as the rule says.
            var raw = entryName.Replace('\\', '/');
            if (raw.StartsWith("//", StringComparison.Ordinal))
            {
                problem = $"{entryName} names a network path and was not extracted";
                return null;
            }

            var cleaned = Normalise(entryName);
            if (cleaned.Length == 0) return null;

            var top = TopSegment(cleaned);
            if (top == null) return null;

            if (_skippedTops.Contains(top))
            {
                // Once per folder, not once per entry inside it: skipping a
                // folder of four thousand files is one skip, as a paste counts it.
                bool first;
                lock (_gate) first = _skippedTopsCounted.Add(top);
                if (first) Record(_skippedNames, ref _skippedCount, top);
                return null;
            }

            if (_renames.TryGetValue(top, out var replacement))
            {
                cleaned = replacement + cleaned[top.Length..];
                Record(_renamedNames, ref _renamedCount, replacement);
            }

            var target = ArchiveEngine.SafeTarget(_root, cleaned, out var refusal);
            if (target == null)
            {
                problem = refusal ?? $"{entryName} cannot be written here";
                return null;
            }

            // One file for one name. An archive can hold the same name twice — a
            // tar appended to, or a Linux tree with xt_TCPMSS.c and xt_tcpmss.c —
            // and the workers writing them in parallel each replaced the other,
            // with whichever finished last winning and both counted as done.
            if (!isDirectory)
            {
                lock (_gate)
                {
                    if (!_given.Add(target))
                    {
                        problem = $"{entryName}: the archive holds this name more than once " +
                                  "(names that differ only in capitals are one name here); the first was kept";
                        return null;
                    }
                }
            }

            bool exists;
            try { exists = isDirectory ? Directory.Exists(target) : File.Exists(target); }
            catch { exists = false; }

            if (exists && !isDirectory)
            {
                if (_fillGaps)
                {
                    Record(_skippedNames, ref _skippedCount, Path.GetFileName(target));
                    return null;
                }

                if (_overwrite)
                {
                    Record(_overwrittenNames, ref _overwrittenCount, Path.GetFileName(target));
                    lock (_gate) _overwrittenTargets.Add(target);
                }
            }

            return target;
        }

        private readonly HashSet<string> _given = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _skippedTopsCounted = new(StringComparer.OrdinalIgnoreCase);

        private void Record(List<string> into, ref int count, string name)
        {
            lock (_gate)
            {
                count++;

                // Only the first handful of names are kept. The count is what the
                // announcement uses; the names are for the sentence that lists a
                // few of them, and remembering forty thousand to print three is a
                // megabyte of strings nobody reads.
                if (into.Count < ConflictOutcomes.MaxRemembered && !into.Contains(name)) into.Add(name);
            }
        }

        /// <summary>
        /// Takes back a replacement that did not happen: the entry was chosen to
        /// go over an existing file, and then was not written.
        /// </summary>
        public void Withdraw(string target)
        {
            lock (_gate)
            {
                // Not written, so not holding its name either: a later entry of
                // the same name was refused as "the first was kept" when nothing
                // had been.
                _given.Remove(target);
                if (!_overwrite) return;

                // Only a target that was recorded as replaced, by its whole path:
                // by name alone, a failed "b/readme.txt" took back the
                // replacement of "a/readme.txt".
                if (!_overwrittenTargets.Remove(target)) return;
                _overwrittenCount--;

                var name = Path.GetFileName(target);
                if (!_overwrittenTargets.Any(t => string.Equals(Path.GetFileName(t), name, StringComparison.OrdinalIgnoreCase)))
                    _overwrittenNames.Remove(name);
            }
        }

        private readonly HashSet<string> _overwrittenTargets = new(StringComparer.OrdinalIgnoreCase);

        public ConflictOutcomes Outcomes()
        {
            lock (_gate)
            {
                if (_renamedCount == 0 && _skippedCount == 0 && _overwrittenCount == 0)
                    return ConflictOutcomes.None;

                // Renames are counted per entry as the tree goes past, and a
                // renamed folder is one rename however many files are inside it.
                return new ConflictOutcomes(
                    _renamedNames.ToArray(),
                    _skippedNames.ToArray(),
                    _overwrittenNames.ToArray(),
                    _renamedNames.Count,
                    _skippedCount,
                    _overwrittenCount);
            }
        }

    }
}
