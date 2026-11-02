using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Extracts into the same folder take turns (5.0.0). Each extract works out
    /// its name clashes from what is on disk when it starts, and places files
    /// with overwrite, so two at once into one folder never saw each other's
    /// names: the last to finish silently replaced the other's files, and the
    /// conflict setting was never asked. Extracts into different folders (Extract
    /// into new folders, or other folders) still run side by side.
    /// </summary>
    internal static class ExtractTurns
    {
        // Never disposed: a waiting extract may still hold one. Value overload,
        // never a factory (CLAUDE.md).
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
            new(StringComparer.OrdinalIgnoreCase);

        private static string Key(string destination)
        {
            string full;
            try { full = Path.GetFullPath(destination); }
            catch { full = destination; }
            return full.TrimEnd('\\', '/');
        }

        /// <summary>
        /// Runs the extract once every earlier extract into the same folder has
        /// finished. Cancelled while waiting, it reports a cancelled result.
        /// </summary>
        internal static async Task<ArchiveResult> InTurn(
            string destination, Func<Task<ArchiveResult>> extract, CancellationToken token)
        {
            var gate = Gates.GetOrAdd(Key(destination), new SemaphoreSlim(1, 1));
            try { await gate.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                return new ArchiveResult(0, 0, true, Array.Empty<string>(), 0, 0);
            }
            try { return await extract().ConfigureAwait(false); }
            finally { gate.Release(); }
        }
    }
}
