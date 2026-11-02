using System;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 4.3.1 sweep of the tray, settings, install and updates.
    /// </summary>
    internal static class Sweep431AppTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 4.3.1, the tray, settings, install and updates:");
            await Task.CompletedTask;
        }
    }
}
