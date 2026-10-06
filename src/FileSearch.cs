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
        /// Whether a name answers the search.
        ///
        /// Case-insensitive, anywhere in the name, and the name includes the
        /// extension — so "love" finds <c>Lovesick.flac</c>, ".flac" finds every
        /// track, and "19" finds <c>2019 mix.wav</c>. One rule rather than a
        /// field per kind of match, because a dialog with a name box and an
        /// extension box is two things to tab past to do the one thing anybody
        /// wants.
        /// </summary>
        public static bool Matches(string name, string term) => Matches(name.AsSpan(), term);

        /// <summary>The same rule, over a name the walk has not made a string of.</summary>
        public static bool Matches(ReadOnlySpan<char> name, string term) =>
            term.Length == 0 || name.Contains(term, StringComparison.OrdinalIgnoreCase);

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
            int most, CancellationToken token)
        {
            var hits = new List<SearchHit>();

            // An explicit stack rather than recursion. A deep tree is a stack
            // overflow, which takes the process with it and cannot be caught —
            // and this walks whatever folder somebody happened to be standing in.
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();

                var folder = pending.Pop();

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
                    walk = new System.IO.Enumeration.FileSystemEnumerable<Found>(folder,
                        (ref System.IO.Enumeration.FileSystemEntry entry) => Look(ref entry, term), WalkOptions)
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
                            hits.Add(found.Hit);
                            if (hits.Count >= most) return new SearchResult(hits, true);
                        }

                        if (found.Folder != null) pending.Push(found.Folder);
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

        private static Found Look(ref System.IO.Enumeration.FileSystemEntry entry, string term)
        {
            bool isDir = entry.IsDirectory;
            bool hit = Matches(entry.FileName, term);
            if (!hit && !isDir) return default;

            string path = entry.ToFullPath();
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
}
