using System;
using System.Collections.Generic;
using System.IO;

namespace ExplorerNative
{
    /// <summary>
    /// Remembers where you were in each folder, so coming back to one puts you
    /// back on the row you left rather than at the top of the list.
    ///
    /// This matters far more without sight than with it. Landing on row one of a
    /// folder you have already worked through means listening your way down to
    /// where you were every single time — going up one level and back should not
    /// cost that.
    /// </summary>
    public sealed class FolderPositionMemory
    {
        /// <summary>
        /// Enough to cover any realistic session of moving around, and bounded so
        /// a long-lived process cannot accumulate one entry per folder ever
        /// visited.
        /// </summary>
        public const int MaxRemembered = 256;

        private readonly Dictionary<string, string> _byFolder =
            new(StringComparer.OrdinalIgnoreCase);

        // Insertion order, for evicting the least recently added first.
        private readonly Queue<string> _order = new();

        public int Count => _byFolder.Count;

        public void Remember(string? folder, string? focusedPath)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(focusedPath)) return;

            if (!_byFolder.ContainsKey(folder))
            {
                while (_byFolder.Count >= MaxRemembered && _order.Count > 0)
                    _byFolder.Remove(_order.Dequeue());

                _order.Enqueue(folder);
            }

            _byFolder[folder] = focusedPath;
        }

        public string? Recall(string? folder) =>
            !string.IsNullOrEmpty(folder) && _byFolder.TryGetValue(folder, out var path) ? path : null;
    }

    public static class NavigationHistory
    {
        /// <summary>
        /// The immediate child of <paramref name="ancestor"/> that leads towards
        /// <paramref name="descendant"/>, or null if one is not below the other.
        ///
        /// This is what makes going up land on the folder you just came out of.
        /// From C:\Users\Conner\Documents, stepping up to C:\Users\Conner should
        /// put the cursor on "Documents" — the thing you were just in — not on
        /// whatever happens to sort first.
        /// </summary>
        public static string? ChildOnPathTo(string? ancestor, string? descendant)
        {
            if (string.IsNullOrEmpty(ancestor) || string.IsNullOrEmpty(descendant)) return null;

            string a, d;
            try
            {
                // Normalised first: a bare "C:" means the current directory on C,
                // so GetFullPath would resolve it to wherever this process
                // started rather than to the root of the drive.
                a = Path.GetFullPath(NameRules.NormaliseLaunchPath(ancestor));
                d = Path.GetFullPath(NameRules.NormaliseLaunchPath(descendant));
            }
            catch { return null; }

            a = a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            d = d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // A drive root trims to "C:", which is not a prefix anyone should be
            // matching against — put its separator back.
            if (a.EndsWith(':')) a += Path.DirectorySeparatorChar;

            var withSeparator = a.EndsWith(Path.DirectorySeparatorChar)
                ? a
                : a + Path.DirectorySeparatorChar;

            if (d.Length <= withSeparator.Length) return null;
            if (!d.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase)) return null;

            var remainder = d[withSeparator.Length..];
            int cut = remainder.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            int segmentLength = cut < 0 ? remainder.Length : cut;
            if (segmentLength == 0) return null;

            // Cut out of the descendant rather than rebuilt from the ancestor, so
            // the result carries the casing the filesystem actually uses. The
            // caller looks this up among real entries, and a path assembled from
            // however the user happened to type the parent is not one of them.
            return d[..(withSeparator.Length + segmentLength)];
        }
    }
}
