using System;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// 5.1.0: files: running transfers, pause and resume, and search by size, date and kind.
    /// </summary>
    internal static class Release510FilesTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("5.1.0, files: running transfers, pause and resume, and search by size, date and kind:");
            await Task.CompletedTask;
        }
    }
}
