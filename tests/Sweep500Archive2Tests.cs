using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of archives opened as folders and the tar reader.
    /// </summary>
    internal static class Sweep500Archive2Tests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, archives opened as folders and the tar reader:");
            TreeWithWriteFailuresIsPartialNotKept();
            await Task.CompletedTask;
        }

        private static string SourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("src not found above the test exe");
        }

        // Bug 1: files that could not be written into the browse cache left an
        // incomplete tree kept as whole, or a full disk under 7-Zip kept as damaged.
        private static void TreeWithWriteFailuresIsPartialNotKept()
        {
            var write = ArchiveBrowser.JudgeTree(new[] { "big.bin: Write failed", "other.bin: Write failed" });
            Check("archives2 tree: a failed write makes the tree partial", write.Lost != null && !write.Damaged,
                $"{write.Damaged} {write.Lost}");
            Check("archives2 tree: the partial reason counts the files", write.Lost?.Contains("2 items") == true,
                write.Lost);

            var space = ArchiveBrowser.JudgeTree(new[]
            {
                "a.bin: the archive is damaged at this point",
                "There isn't enough space on the disk",
            });
            Check("archives2 tree: 7-Zip short of space is not damage", !space.Damaged);
            Equal("archives2 tree: 7-Zip short of space says so", "There isn't enough space on the disk", space.Lost ?? "");

            var named = ArchiveBrowser.JudgeTree(new[] { "a.bin: the archive is damaged at this point", "Cannot open output file : Access is denied." });
            Check("archives2 tree: a file 7-Zip named without damage is lost, not damage", !named.Damaged && named.Lost != null);

            foreach (var note in new[]
                     {
                         "link: a symbolic link was not extracted",
                         "2 items in the archive repeated a name already used (names that differ only in capitals are one name here); the first of each was kept",
                         "Damaged photos/link: a symbolic link was not extracted",
                     })
            {
                var j = ArchiveBrowser.JudgeTree(new[] { note });
                Check($"archives2 tree: a note keeps the tree whole and undamaged ({note[..12]})", !j.Damaged && j.Lost == null,
                    $"{j.Damaged} {j.Lost}");
            }

            var real = ArchiveBrowser.JudgeTree(new[] { "f: the archive is damaged at this point", ArchiveEngine.DamagedMessage(3) });
            Check("archives2 tree: real damage is still damage", real.Damaged && real.Lost == null);
            Check("archives2 tree: damage with nothing extracted is damage",
                ArchiveBrowser.JudgeTree(new[] { ArchiveEngine.DamagedMessage(0) }).Damaged);

            var source = File.ReadAllText(Path.Combine(SourceRoot(), "src", "ArchiveBrowser.cs"));
            Check("archives2 tree: the cache marker is written only for a whole tree",
                source.Contains("if (partial == null) File.WriteAllText(finished", StringComparison.Ordinal));
            Check("archives2 tree: a lost file makes the browse partial",
                source.Contains("judged.Lost", StringComparison.Ordinal));
        }
    }
}
