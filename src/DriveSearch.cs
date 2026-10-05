using System;
using System.Collections.Generic;

namespace ExplorerNative
{
    /// <summary>
    /// A Drive object as a search answers for it: the usual facts plus who its
    /// parent is, which is the part that says where it lives.
    ///
    /// Drive models a parent as a list because a file could once be in several
    /// folders at once. That has not been creatable since 2020 and the API
    /// returns a single entry for everything made since, so only the first is
    /// used — a file genuinely in two places is found under whichever one Drive
    /// names first, which is one honest answer rather than the same file listed
    /// twice under two paths.
    /// </summary>
    internal sealed record DriveFound(
        string Id, string Name, string MimeType, long Size, DateTime Modified, string? ParentId)
    {
        public bool IsFolder => MimeType == "application/vnd.google-apps.folder";

        public bool IsGoogleDocument =>
            MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) && !IsFolder;
    }

    /// <summary>
    /// Working out which Drive hits are under the folder somebody is standing in.
    ///
    /// **Drive has no such thing as a recursive search.** Its query language can
    /// say "in this folder" and cannot say "in this folder or anything below it",
    /// so scoping a search to a subtree is work that has to happen here.
    ///
    /// The route that suggests itself — resolve each hit's parents upwards with a
    /// <c>files.get</c> per folder — costs one request per folder in the answer,
    /// at about 700ms each. Instead the whole folder graph is fetched **in one
    /// query**: every folder in the account is a single `mimeType = folder`
    /// listing, a few hundred rows for a library of thousands of tracks, and once
    /// it is in memory every chain resolves for nothing.
    ///
    /// So a search is two requests whatever the shape of the account, and this
    /// class is the arithmetic between them — pure, and therefore tested against
    /// a graph built by hand rather than against somebody's Drive.
    /// </summary>
    internal static class DriveSearch
    {
        /// <summary>
        /// How far up a parent chain to walk before deciding something is wrong.
        ///
        /// A guard rather than a limit: Drive's own folder depth is far under
        /// this, and what it is really defending against is a graph that loops.
        /// A cycle should not be possible and "should not be possible" is not a
        /// reason to hang the search dialog.
        /// </summary>
        public const int MostDepth = 128;

        /// <summary>
        /// The folders between <paramref name="rootId"/> and
        /// <paramref name="parentId"/>, outermost first, or null when
        /// <paramref name="parentId"/> is not underneath the root at all.
        ///
        /// An empty list means the hit sits directly in the root. The order is
        /// what makes it useful to the caller: each folder has to be populated
        /// before the one below it can be looked up by name, so they come back in
        /// the order they must be visited.
        /// </summary>
        public static List<string>? ChainUnder(
            string rootId, string? parentId, IReadOnlyDictionary<string, string?> parentOf)
        {
            var upwards = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var at = parentId;

            for (int step = 0; step < MostDepth; step++)
            {
                if (at == null) return null;                 // ran out of account before finding the root
                if (at == rootId) { upwards.Reverse(); return upwards; }

                // A folder reached twice is a cycle, and continuing is a loop
                // that never ends rather than an answer that is merely wrong.
                if (!seen.Add(at)) return null;

                upwards.Add(at);

                // A parent nobody listed is a folder in somebody else's drive
                // shared into this one, whose own parents are not ours to see.
                // Not under our root as far as anything here can tell.
                if (!parentOf.TryGetValue(at, out at)) return null;
            }

            return null;
        }

        /// <summary>
        /// Every folder's parent, as the graph <see cref="ChainUnder"/> walks.
        /// </summary>
        public static Dictionary<string, string?> ParentGraph(IEnumerable<DriveFound> folders)
        {
            var graph = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var folder in folders) graph[folder.Id] = folder.ParentId;
            return graph;
        }

        /// <summary>
        /// Walks a chain of folder ids down from the searched folder and gives
        /// back the path of the last one, populating each folder on the way so
        /// the next can be found inside it. Null when any step is not on the
        /// letter.
        ///
        /// <paramref name="populate"/> and <paramref name="childPathForId"/> are
        /// the mount's two operations, taken as delegates so the ordering between
        /// them can be tested — **and it is the ordering that was wrong.** The
        /// first version populated each folder before looking the *next* one up
        /// inside it, which walks down correctly and leaves the folder at the
        /// bottom, the one the hit actually lives in, never populated. Looking
        /// the hit up in it then found nothing and the hit was dropped, so every
        /// result below the top level silently disappeared unless that folder
        /// happened to have been browsed already. A live search resolved all 214
        /// of its hits and returned almost none of them.
        ///
        /// <paramref name="resolved"/> carries what is already known between
        /// calls, so a second hit in the same folder costs nothing — including
        /// when the answer was "not there", which is remembered too.
        /// </summary>
        public static string? PathForChain(
            string rootPath, IReadOnlyList<string> chain,
            Action<string> populate,
            Func<string, string, string?> childPathForId,
            Dictionary<string, string?> resolved)
        {
            var path = rootPath;

            foreach (var childId in chain)
            {
                if (resolved.TryGetValue(childId, out var known))
                {
                    if (known == null) return null;
                    path = known;
                    continue;
                }

                // One listing, and only for a folder that is actually on the way
                // to something somebody searched for.
                populate(path);

                var childPath = childPathForId(path, childId);
                resolved[childId] = childPath;
                if (childPath == null) return null;

                path = childPath;
            }

            // The folder the hit is in, which the loop above never reaches: it
            // populates the *parent* of each step so that step can be found. A
            // chain of none lands here straight away, which is the hit sitting in
            // the searched folder itself.
            populate(path);
            return path;
        }
    }
}
