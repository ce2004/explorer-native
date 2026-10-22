using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>One thing a search found, as a path and the facts a row needs.</summary>
    internal sealed record SearchHit(string Path, string Name, bool IsDir, long Size, DateTime Modified);

    /// <summary>
    /// What a search came back with, and whether it is the whole answer.
    ///
    /// <c>Truncated</c> is not a detail: a list that stopped at the cap looks
    /// exactly like a complete one, and somebody who cannot see the scrollbar has
    /// no other way to find out that the file they wanted may be past the end.
    /// </summary>
    internal sealed record SearchResult(List<SearchHit> Hits, bool Truncated);

    /// <summary>
    /// Finding files by name, under a folder and everything below it.
    ///
    /// The local half. Drive is searched through Google rather than by walking —
    /// see <see cref="GoogleDrive.SearchAsync"/> — because walking a cloud folder
    /// is one network listing per directory at about 1.3 seconds each, and a
    /// music library is hundreds of them.
    /// </summary>
    internal static class FileSearch
    {
        /// <summary>
        /// Where a search gives up and says so.
        ///
        /// Ten thousand rows is already far more than anybody is going to arrow
        /// through, and the cap is what stops a search started at the root of a
        /// disk from building a list of every file on the machine before the
        /// window can show any of it.
        /// </summary>
        public const int MostResults = 10_000;

        /// <summary>
        /// Whether a name answers the search, on its own (no folders around it).
        /// The rule is <see cref="SearchQuery"/>'s.
        /// </summary>
        public static bool Matches(string name, string term) => SearchQuery.Parse(term).Matches(name, "");

        /// <summary>
        /// What a result's row says: the name, and where it lives when that is
        /// not simply the folder that was searched.
        ///
        /// The name comes first and the location after it, which is the way round
        /// that keeps type-ahead working — a row beginning with its folder means
        /// pressing L no longer jumps to the L files. It also reads as a sentence
        /// rather than a path: "Lovesick.flac, in 2019".
        /// </summary>
        public static string Label(string root, string fullPath)
        {
            var name = Path.GetFileName(fullPath);
            var folder = Path.GetDirectoryName(fullPath);

            if (folder == null || name.Length == 0) return fullPath;

            var relative = Relative(root, folder);
            return relative.Length == 0 ? name : $"{name}, in {relative}";
        }

        /// <summary>
        /// <paramref name="folder"/> as a path relative to <paramref name="root"/>,
        /// or empty when it is the root itself. Anything not actually under the
        /// root is given back whole rather than turned into a relative path that
        /// would be a lie.
        /// </summary>
        public static string Relative(string root, string folder)
        {
            root = root.TrimEnd('\\');
            folder = folder.TrimEnd('\\');

            if (string.Equals(root, folder, StringComparison.OrdinalIgnoreCase)) return "";

            if (folder.Length > root.Length + 1 &&
                folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                folder[root.Length] == '\\')
                return folder[(root.Length + 1)..];

            return folder;
        }

        /// <summary>
        /// Everything under <paramref name="root"/> whose name matches.
        ///
        /// Runs on a worker and is cancellable at every entry, not merely at
        /// every folder: a search started at the root of a disk is minutes of
        /// walking, and a token only looked at between directories is a cancel
        /// that is not heard until the current one finishes.
        /// </summary>
        public static SearchResult Local(
            string root, string term, bool showHidden, bool showSystem,
            int most, CancellationToken token, Func<string, bool>? skip = null)
        {
            // skip: folders not to go into (still listed as hits if they match).
            // The window passes the Drive sync root, which sits under AppData and
            // costs a Drive listing per folder.
            var hits = new List<SearchHit>();
            var query = SearchQuery.Parse(term);

            // An explicit stack rather than recursion. A deep tree is a stack
            // overflow, which takes the process with it and cannot be caught —
            // and this walks whatever folder somebody happened to be standing in.
            // Each folder carries its path below the root, already folded, so a
            // word can be found in the folders a file sits in ("tate greedy").
            var pending = new Stack<(string Path, string Below)>();
            pending.Push((root, ""));

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();

                var (folder, below) = pending.Pop();

                // Each entry is looked at where the scan left it, and only a hit
                // or a folder to go into is turned into strings. The walk used to
                // make a FileInfo — an object and two strings — for every file in
                // the tree in order to read its name and throw it away.
                //
                // One folder at a time, deliberately. Reading the next few ahead
                // on other threads was tried: through a share it saved a quarter,
                // on a disk it cost half again, because a thread hop per folder
                // costs about what reading a small folder does. And walking them
                // out of order would change which hits a search that stops at
                // the cap keeps.
                IEnumerator<Found> walk;
                try
                {
                    // Exact: a folder called "Backup." read plainly is "Backup",
                    // so the search listed the sibling's files under the wrong
                    // folder, or nothing at all. Paths come back plain (Look).
                    walk = new System.IO.Enumeration.FileSystemEnumerable<Found>(NameRules.ExactPath(folder),
                        (ref System.IO.Enumeration.FileSystemEntry entry) => Look(ref entry, query, below), WalkOptions)
                    {
                        ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) =>
                            (showHidden || (entry.Attributes & FileAttributes.Hidden) == 0) &&
                            (showSystem || (entry.Attributes & FileAttributes.System) == 0),
                    }.GetEnumerator();
                }
                catch { continue; }   // unreadable is skipped, never fatal

                using (walk)
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();

                        // MoveNext is where a folder that went unreadable between
                        // the open and the walk throws, and one of those must not
                        // end a search over everything else.
                        try { if (!walk.MoveNext()) break; }
                        catch { break; }

                        var found = walk.Current;

                        if (found.Hit != null)
                        {
                            // Cut short only once there is one more than the cap:
                            // exactly the cap is the whole answer, said as such.
                            if (hits.Count >= most) return new SearchResult(hits, true);
                            hits.Add(found.Hit);
                        }

                        if (found.Folder != null && (skip == null || !skip(found.Folder)))
                        {
                            var name = SearchQuery.FoldName(Path.GetFileName(found.Folder));
                            pending.Push((found.Folder, below.Length == 0 ? name : below + "\\" + name));
                        }
                    }
                }
            }

            return new SearchResult(hits, false);
        }

        /// <summary>
        /// What one entry contributes: a hit, a folder to walk into, both, or
        /// (for nearly every file) neither.
        /// </summary>
        private readonly record struct Found(SearchHit? Hit, string? Folder);

        /// <summary>
        /// Everything, nothing skipped, an unreadable folder an exception: what
        /// <c>DirectoryInfo.EnumerateFileSystemInfos()</c> asks for.
        /// </summary>
        private static readonly EnumerationOptions WalkOptions = new()
        {
            MatchType = MatchType.Win32,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
        };

        private static Found Look(ref System.IO.Enumeration.FileSystemEntry entry, SearchQuery query, string below)
        {
            bool isDir = entry.IsDirectory;
            bool hit = query.MatchesFolded(entry.FileName, below);
            if (!hit && !isDir) return default;

            string path = NameRules.PlainPath(entry.ToFullPath());
            SearchHit? found = null;
            if (hit)
            {
                DateTime modified;
                try { modified = entry.LastWriteTimeUtc.UtcDateTime.ToLocalTime(); }
                catch { modified = default; }
                found = new SearchHit(path, entry.FileName.ToString(), isDir, isDir ? -1 : entry.Length, modified);
            }

            // A junction is somebody else's tree wearing this one's name:
            // following one can circle for ever, and at best it searches a whole
            // disk out of a folder of six files. DriveUpload.Survey already
            // learned this.
            string? folder = null;
            if (isDir && ((entry.Attributes & FileAttributes.ReparsePoint) == 0 || !NameRules.IsLink(entry.ToFileSystemInfo())))
                folder = path;

            return new Found(found, folder);
        }
    }

    /// <summary>
    /// What was typed into Search, taken apart once and asked of every name.
    ///
    /// - Words, in any order: "greedy tate" finds "Tate McRae - greedy.flac".
    ///   Case and accents do not matter ("beyonce" finds "Beyoncé"), and the
    ///   extension is part of the name, so ".flac" still finds every track.
    /// - A word may be found in the folders between the searched folder and the
    ///   file instead ("tate greedy" finds Tate McRae\greedy.flac), but at
    ///   least one word has to be in the name itself, or "tate" would list
    ///   every file in the Tate folder.
    /// - "quoted words" stay together as one phrase.
    /// - A word with * or ? is a pattern for the whole name: "*.flac",
    ///   "t??.mp3" (in quotes when it has a space: "track ??.mp3").
    /// - -word leaves out anything with that word in its name or its folders.
    ///
    /// Nothing at all typed (F5 re-runs a search) matches everything.
    /// </summary>
    internal sealed class SearchQuery
    {
        private readonly string[] _words;      // folded; each must be found
        private readonly string[] _patterns;   // folded; each must fit the whole name
        private readonly string[] _without;    // folded; none may be found

        /// <summary>
        /// What to ask Google for (any one of them, as "name contains"): every
        /// item the query can match holds at least one of these in its name. Empty
        /// means Google cannot narrow it and everything is asked for.
        /// </summary>
        public IReadOnlyList<string> DriveHints { get; }

        private SearchQuery(string[] words, string[] patterns, string[] without, string[] hints)
        {
            _words = words;
            _patterns = patterns;
            _without = without;
            DriveHints = hints;
        }

        public static SearchQuery Parse(string? text)
        {
            var words = new List<string>();
            var typedWords = new List<string>();
            var patterns = new List<string>();
            var without = new List<string>();
            string patternPiece = "";

            foreach (var (token, quoted, dashOutside) in Tokens(text ?? ""))
            {
                if (dashOutside && token.Length > 1)
                {
                    without.Add(FoldName(token[1..]));
                    continue;
                }
                // Even quoted: no Windows name holds * or ?, so they can only
                // be meant as a pattern ("track ??.mp3" needs the quotes).
                if (token.AsSpan().IndexOfAny('*', '?') >= 0)
                {
                    patterns.Add(FoldName(token));
                    foreach (var piece in token.Split('*', '?'))
                    {
                        var plain = piece.Trim(' ', '.');
                        if (plain.Length > patternPiece.Length) patternPiece = plain;
                    }
                    continue;
                }
                words.Add(FoldName(token));
                typedWords.Add(token);
            }

            // A name that matches fits every pattern, so it holds the longest
            // plain piece of one; failing that it holds at least one word. A
            // pattern with no plain piece ("*") gives Google nothing to go on.
            string[] hints = patterns.Count > 0
                ? (patternPiece.Length > 0 ? [patternPiece] : [])
                : typedWords.ToArray();

            return new SearchQuery(words.ToArray(), patterns.ToArray(), without.ToArray(), hints);
        }

        /// <summary>Split on spaces, keeping "quoted words" as one.</summary>
        private static IEnumerable<(string Token, bool Quoted, bool DashOutside)> Tokens(string text)
        {
            var current = new System.Text.StringBuilder();
            bool inQuote = false, quoted = false, dashOutside = false;

            foreach (char c in text)
            {
                if (c == '"') { inQuote = !inQuote; quoted = true; continue; }
                if (!inQuote && char.IsWhiteSpace(c))
                {
                    if (current.Length > 0) yield return (current.ToString(), quoted, dashOutside);
                    current.Clear();
                    quoted = dashOutside = false;
                    continue;
                }
                if (current.Length == 0) dashOutside = c == '-' && !inQuote;
                current.Append(c);
            }
            if (current.Length > 0) yield return (current.ToString(), quoted, dashOutside);
        }

        /// <summary>A name or folder path with accents taken off, as the query's words are.</summary>
        public static string FoldName(string text) => TypeAhead.Fold(text);

        /// <summary>
        /// Whether an item answers, given its name and the folders between the
        /// searched folder and it ("2019\live", or empty when it sits in the
        /// searched folder itself).
        /// </summary>
        public bool Matches(string name, string folders) => MatchesFolded(name, FoldName(folders));

        /// <summary>
        /// The same, with the folders already through <see cref="FoldName"/>: the
        /// walk folds each folder once, not once per file in it.
        /// </summary>
        public bool MatchesFolded(ReadOnlySpan<char> name, string folders)
        {
            // Plain ASCII, nearly every name, is compared where it lies.
            if (name.ContainsAnyExceptInRange((char)0, (char)0x7F))
                name = FoldName(name.ToString());

            foreach (var pattern in _patterns)
                if (!System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
                    return false;

            foreach (var word in _without)
                if (name.Contains(word, StringComparison.OrdinalIgnoreCase) ||
                    folders.Contains(word, StringComparison.OrdinalIgnoreCase))
                    return false;

            bool inName = false;
            foreach (var word in _words)
            {
                if (name.Contains(word, StringComparison.OrdinalIgnoreCase)) inName = true;
                else if (!folders.Contains(word, StringComparison.OrdinalIgnoreCase)) return false;
            }

            // A pattern already pins the name down; words alone need one in it.
            return inName || _words.Length == 0 || _patterns.Length > 0;
        }
    }
}
