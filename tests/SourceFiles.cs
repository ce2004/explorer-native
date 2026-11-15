using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ExplorerNative
{
    /// <summary>
    /// What the source-shape tests read when they ask for a file by name.
    ///
    /// MainForm and DriveMonitor are split across several files (partial
    /// classes, and plain classes the window calls), so a test that asks for
    /// "MainForm.cs" or "DriveMonitor.cs" gets the whole of it: that file first,
    /// then its partial files, then (for MainForm) the classes its logic moved
    /// into. Any other name is read as it is.
    /// </summary>
    internal static class SourceFiles
    {
        /// <summary>The plain classes MainForm's logic moved into, read with it.</summary>
        internal static readonly string[] MainFormLogic =
        {
            "ListEntry.cs", "ListSorting.cs", "TransferRules.cs", "SelectionSpeech.cs", "FolderFiltering.cs",
        };

        /// <summary>Reads <paramref name="path"/>, or the whole class when it names MainForm.cs or DriveMonitor.cs.</summary>
        public static string Read(string path)
        {
            var name = Path.GetFileName(path);
            var folder = Path.GetDirectoryName(path);
            if (folder == null) return File.ReadAllText(path);

            if (name.Equals("MainForm.cs", StringComparison.OrdinalIgnoreCase))
                return Join(path, Partials(folder, "MainForm").Concat(MainFormLogic.Select(f => Path.Combine(folder, f))));
            if (name.Equals("DriveMonitor.cs", StringComparison.OrdinalIgnoreCase))
                return Join(path, Partials(folder, "DriveMonitor"));
            return File.ReadAllText(path);
        }

        /// <summary>"Name.*.cs" beside the file, in name order (never NameForm.cs).</summary>
        private static IEnumerable<string> Partials(string folder, string type) =>
            Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, type + ".*.cs").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                : Enumerable.Empty<string>();

        private static string Join(string first, IEnumerable<string> rest)
        {
            // The named file itself must be there, as File.ReadAllText would insist.
            var parts = new List<string> { File.ReadAllText(first) };
            foreach (var file in rest)
                if (File.Exists(file)) parts.Add(File.ReadAllText(file));
            return string.Join("\n", parts);
        }
    }
}
