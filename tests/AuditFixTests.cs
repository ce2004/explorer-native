using System;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// The findings of the audio and archive audits (2026-10-13), each pinned:
    /// a finished track goes quiet and lets its download go, a hard-linked tar
    /// member opens, a name nested thousands deep cannot end the app, and the
    /// superscript device names are refused in words.
    /// </summary>
    internal static class AuditFixTests
    {
        public static void Run(Action<string, bool, string> check)
        {
            Console.WriteLine("Audio and archive audit fixes:");
            void Check(string what, bool ok, string detail = "") => check(what, ok, detail);

            var playback = Tests_Source("WasapiPlayback.cs") ?? "";
            Check("a finished track's pump waits long, as a paused one does",
                playback.Contains("_drained.WaitOne(_paused || _resting ? 5000 : 250)", StringComparison.Ordinal));
            var player = Tests_Source("AudioPlayer.cs") ?? "";
            int finished = player.IndexOf("private void OnDirectFinished()", StringComparison.Ordinal);
            Check("and a finished track starts the clock on letting its download go",
                finished >= 0 && player.IndexOf("ArmRelease();", finished, StringComparison.Ordinal) is var arm &&
                arm > finished && arm < player.IndexOf("Finished?.Invoke(path);", finished, StringComparison.Ordinal));

            Check("COM¹ is refused as a name, in words",
                NameRules.DescribeBadName("COM¹.txt") != null);
            Check("and LPT³ too", NameRules.DescribeBadName("LPT³") != null);

            var dir = Path.Combine(Path.GetTempPath(), "en-audit-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                // A file and a second name for it, as GNU tar stores one.
                var linked = Path.Combine(dir, "linked.tar");
                var bytes = Encoding.UTF8.GetBytes("the one file under two names");
                using (var file = File.Create(linked))
                using (var writer = new TarWriter(file, TarEntryFormat.Pax))
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "a.txt") { DataStream = new MemoryStream(bytes) });
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "b.txt") { LinkName = "a.txt" });
                }
                var listing = ArchiveBrowser.Open(linked, null, CancellationToken.None);
                string? got = null, why = null;
                try { got = ArchiveBrowser.ExtractOne(listing, "b.txt", null, CancellationToken.None); }
                catch (Exception ex) { why = ex.Message; }
                Check("a hard-linked tar member opens on its own", got != null && File.Exists(got), why ?? "");
                if (got != null && File.Exists(got))
                    Check("with the bytes of the file it names", File.ReadAllBytes(got).SequenceEqual(bytes));

                // Nested ten thousand deep, on a small stack: listed, not fatal.
                var deep = Path.Combine(dir, "deep.tar");
                using (var file = File.Create(deep))
                using (var writer = new TarWriter(file, TarEntryFormat.Pax))
                {
                    var name = string.Join('/', Enumerable.Repeat("a", 10_000)) + "/f.txt";
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(bytes) });
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "ok.txt") { DataStream = new MemoryStream(bytes) });
                }
                ArchiveListing? opened = null;
                Exception? failed = null;
                var thread = new Thread(() =>
                {
                    try { opened = ArchiveBrowser.Open(deep, null, CancellationToken.None); }
                    catch (Exception ex) { failed = ex; }
                }, 256 * 1024);
                thread.Start();
                bool ended = thread.Join(TimeSpan.FromSeconds(60));
                Check("an archive with a name nested 10,000 deep opens on a small stack",
                    ended && opened != null, failed?.Message ?? (ended ? "" : "did not finish"));
                if (opened != null)
                {
                    Check("the impossible name is counted as unsafe, not listed", opened.Unsafe == 1, opened.Unsafe.ToString());
                    Check("and the ordinary one is still there", opened.Find("ok.txt") != null);
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            Console.WriteLine();
        }

        private static string? Tests_Source(string name)
        {
            var at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at != null && !Directory.Exists(Path.Combine(at.FullName, "src"))) at = at.Parent;
            var path = at == null ? null : Path.Combine(at.FullName, "src", name);
            return path != null && File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }
}
