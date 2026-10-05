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
        public static bool Matches(string name, string term) =>
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

                IEnumerator<FileSystemInfo> walk;
                try { walk = new DirectoryInfo(folder).EnumerateFileSystemInfos().GetEnumerator(); }
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

                        var info = walk.Current;

                        FileAttributes attrs;
                        try { attrs = info.Attributes; }
                        catch { continue; }

                        if (!showHidden && (attrs & FileAttributes.Hidden) != 0) continue;
                        if (!showSystem && (attrs & FileAttributes.System) != 0) continue;

                        bool isDir = (attrs & FileAttributes.Directory) != 0;

                        if (Matches(info.Name, term))
                        {
                            hits.Add(new SearchHit(
                                info.FullName, info.Name, isDir,
                                isDir ? -1 : (info is FileInfo f ? SafeLength(f) : 0),
                                SafeWriteTime(info)));

                            if (hits.Count >= most) return new SearchResult(hits, true);
                        }

                        // A junction is somebody else's tree wearing this one's
                        // name: following one can circle for ever, and at best it
                        // searches a whole disk out of a folder of six files.
                        // DriveUpload.Survey already learned this.
                        if (isDir && ((attrs & FileAttributes.ReparsePoint) == 0 || !NameRules.IsLink(info)))
                            pending.Push(info.FullName);
                    }
                }
            }

            return new SearchResult(hits, false);
        }

        private static long SafeLength(FileInfo f)
        {
            try { return f.Length; } catch { return 0; }
        }

        private static DateTime SafeWriteTime(FileSystemInfo info)
        {
            try { return info.LastWriteTime; } catch { return default; }
        }
    }
}
