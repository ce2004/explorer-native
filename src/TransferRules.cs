using System;
using System.IO;
using System.Linq;

namespace ExplorerNative
{
    /// <summary>
    /// The decisions a paste makes before anything is copied or moved: what is
    /// already where it is going, a selection that mixes Drive and the PC, a
    /// folder moved into itself, the answer to the conflict question, and how
    /// many files go at once. Text only: nothing here asks the disk or needs a
    /// window. MainForm runs the transfer; these say what it may do.
    /// </summary>
    internal static class TransferRules
    {
        /// <summary>Said when a paste would move a selection into a mix of Drive and the PC.</summary>
        internal const string MixedMoveSaid = "Some of these are on Google Drive and some are not; move them separately";

        /// <summary>
        /// The items of a cut that are not already in the destination.
        ///
        /// Cut and paste into the folder the items are already in is nothing
        /// to do. It was a collision of each item with itself, so under keep
        /// both everything came back renamed "a (2)" and the clipboard was
        /// cleared. A string comparison, so it costs nothing on a share; the
        /// engines refuse it as well. The clipboard keeps the cut, as File
        /// Explorer's does.
        /// </summary>
        internal static string[] NotAlreadyIn(string[] paths, string destination) =>
            paths.Where(p =>
            {
                var name = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return string.IsNullOrEmpty(name) || !FileOperations.SamePath(Path.Combine(destination, name), p);
            }).ToArray();

        /// <summary>Said when everything cut is already in the folder it was pasted into.</summary>
        internal static string AlreadyHereSaid(int count) =>
            count == 1
                ? "Already in this folder"
                : "Already in this folder; nothing to move";

        /// <summary>
        /// Whether a move's selection holds both Drive items and others.
        ///
        /// A move is finished on Drive by trashing and elsewhere by robocopy,
        /// and a selection holding both went down one path or the other for
        /// all of it: the local half copied rather than moved, was then counted
        /// as "could not remove from Google Drive", and "Move complete" was
        /// said with the originals still in place. Search results are where
        /// such a selection comes from.
        /// </summary>
        internal static bool MixesDrive(string[] paths, Func<string, bool> onDrive)
        {
            int count = paths.Count(p => onDrive(p));
            return count > 0 && count < paths.Length;
        }

        /// <summary>
        /// Into itself or one of its own subfolders: refused before Drive is
        /// asked, not left for Google to answer.
        /// </summary>
        internal static bool IntoItself(string path, string destination)
        {
            var into = destination.TrimEnd('\\') + "\\";
            return into.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether <paramref name="path"/> sits directly in
        /// <paramref name="folder"/>. Compared as full paths with the root's
        /// separator kept: trimmed by hand, "G:\" and "G:" never matched, and an
        /// item pasted onto itself at the top of the letter was replaced — or,
        /// merged, trashed. Text only; nothing is asked of the disk.
        /// </summary>
        internal static bool ParentIs(string path, string folder)
        {
            try
            {
                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
                return parent != null && string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>The paste's conflict policy for the answer to the question; null when it was cancelled.</summary>
        internal static PasteConflictPolicy? PolicyFor(FileOperations.ConflictChoice choice) => choice switch
        {
            FileOperations.ConflictChoice.Overwrite => PasteConflictPolicy.Overwrite,
            FileOperations.ConflictChoice.Skip => PasteConflictPolicy.Skip,
            FileOperations.ConflictChoice.Rename => PasteConflictPolicy.AutoRename,
            FileOperations.ConflictChoice.FillGaps => PasteConflictPolicy.FillGaps,
            _ => null,
        };

        /// <summary>
        /// How many files to copy at once, which is a different question when the
        /// files are coming off Google Drive.
        ///
        /// Everywhere else the answer is "as many as the machine can usefully
        /// have in flight" — a NAS spends its time waiting on round trips and
        /// eight or sixteen requests outstanding is the difference between usable
        /// and not. Reading a placeholder is round trips too, so the same
        /// reasoning looks like it should apply, and it is exactly backwards.
        ///
        /// A Drive read is not waiting on latency, it is *using the link*, and
        /// the link is about three megabytes a second however many ways it is
        /// split. Sixteen at once does not make the copy faster; it makes every
        /// one of the sixteen sixteen times slower, and a read slow enough is a
        /// read Windows abandons — "the cloud operation was not completed before
        /// the time-out period expired". Sixteen files failing one at a time is
        /// what a folder copied off the drive actually looked like.
        ///
        /// One, then. The whole link goes to the file being copied, that file
        /// finishes at the speed of the link, and the next one starts. The total
        /// is the same and nothing times out.
        /// </summary>
        /// <param name="onDrive">Whether a path is on the Drive letter; null when Drive is not connected.</param>
        internal static int ThreadsFor(string[] paths, Func<string, bool>? onDrive)
        {
            bool fromDrive = onDrive != null && paths.Any(p => onDrive(p));
            return fromDrive ? 1 : FileOperations.RecommendedThreads;
        }
    }
}
