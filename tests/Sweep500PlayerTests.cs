using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of the audio player.
    /// </summary>
    internal static class Sweep500PlayerTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the audio player:");

            var dir = Path.Combine(Path.GetTempPath(), "en-sweep500player-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                await WithWatchdog("a WAV over 4GB played through a download", () => BigWavThroughDownloadTests(dir));
                await WithWatchdog("a share or Drive that stops answering", () => NotAnsweringTests(dir));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static async Task WithWatchdog(string what, Action body)
        {
            var run = Task.Run(body);
            if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60))) != run)
            {
                Check($"{what} within 60 seconds", false, "the watchdog fired");
                return;
            }
            if (run.Exception != null)
                Check($"{what} ran to the end", false, run.Exception.InnerException?.ToString() ?? run.Exception.ToString());
        }

        // ---------- A share or Drive that stops answering ----------

        /// <summary>
        /// A track on a share that stopped answering, or on Drive while it is
        /// offline, was said as "is no longer there" (File.Exists answers false
        /// for a share it cannot reach) and handed to another app.
        /// </summary>
        private static void NotAnsweringTests(string dir)
        {
            var here = Path.Combine(dir, "here.wav");
            File.WriteAllBytes(here, new byte[16]);
            var gone = Path.Combine(dir, "gone.flac");
            var goneFolder = Path.Combine(dir, "no-such-folder", "gone.flac");
            var deadShare = @"\\localhost\no-such-share-" + Guid.NewGuid().ToString("N") + @"\Music\song.flac";

            Equal("a file that is there is There", "There", AudioPlayer.WhereIs(here).ToString());
            Equal("a file deleted from a folder that answers is Gone", "Gone", AudioPlayer.WhereIs(gone).ToString());
            Equal("a file whose folder went from a drive that answers is Gone", "Gone", AudioPlayer.WhereIs(goneFolder).ToString());
            Equal("a file on a share that does not answer is NotAnswering, not Gone", "NotAnswering", AudioPlayer.WhereIs(deadShare).ToString());

            var was = AudioPlayer.DriveUnreachable;
            try
            {
                AudioPlayer.DriveUnreachable = p => p.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
                Equal("a file on Drive while Drive is offline is NotAnswering", "NotAnswering", AudioPlayer.WhereIs(gone).ToString());
                AudioPlayer.DriveUnreachable = null;

                // End to end: what is said, and whether it is handed to another app.
                using var player = new AudioPlayer();
                player.VolumePercent = 0;
                var said = new System.Collections.Generic.List<(string id, string text)>();
                bool handedOver = false;
                player.Notify = (id, text) => { lock (said) said.Add((id, text)); };
                player.Failed += _ => handedOver = true;
                var answer = player.Play(deadShare);
                string all;
                lock (said) all = string.Join(" | ", said.ConvertAll(x => x.id + ": " + x.text));
                if (all.Contains("No audio output"))
                {
                    Console.WriteLine("  (no output device; the end-to-end share check is skipped)");
                    return;
                }
                Check("a track on a share that does not answer does not start", answer == null, answer);
                Check("a track on a share that does not answer is not said to be gone", !all.Contains("audiofile.missing"), all);
                Check("a track on a share that does not answer says the share isn't answering",
                    all.Contains("audio.failed") && all.Contains("isn't answering"), all);
                Check("a track on a share that does not answer is not handed to another app", !handedOver, all);
            }
            finally
            {
                AudioPlayer.DriveUnreachable = was;
            }
        }

        // ---------- A WAV over 4GB on a share or Drive ----------

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize,
            IntPtr output, int outputSize, out int returned, IntPtr overlapped);

        private const uint FSCTL_SET_SPARSE = 0x000900C4;
        private const int Rate = 44100;

        /// <summary>
        /// Every remote track is decoded through a download (a StreamingSource),
        /// and the 3.0.9 big-WAV reader was only tried without one, so a 5GB
        /// recording on the NAS stopped at its wrapped size.
        /// </summary>
        private static void BigWavThroughDownloadTests(string dir)
        {
            var path = Path.Combine(dir, "long.wav");
            const long length = (1L << 32) + 4 * 1024 * 1024;
            long dataBytes = length - 44;
            try
            {
                using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
                if (!DeviceIoControl(file.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                {
                    Console.WriteLine("  (this volume cannot make sparse files; the 4GB WAV test is skipped)");
                    return;
                }
                using var w = new BinaryWriter(file, Encoding.ASCII, leaveOpen: true);
                w.Write("RIFF"u8); w.Write((uint)((length - 8) & 0xFFFFFFFF)); w.Write("WAVE"u8);
                w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate);
                w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
                w.Write("data"u8); w.Write((uint)(dataBytes & 0xFFFFFFFF));
                file.SetLength(length);
                file.Position = length - Rate * 4L;
                for (int i = 0; i < Rate; i++)
                {
                    short v = (short)Math.Round(10000 * Math.Sin(2 * Math.PI * 500 * i / Rate));
                    w.Write(v); w.Write(v);
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine("  (no room for the sparse 4GB WAV: " + ex.Message + ")");
                return;
            }

            double expected = dataBytes / 4 / (double)Rate;
            using (var source = new StreamingSource(path, 16L << 20))
            {
                var decoder = TrackDecoder.Open(path, source, 0, 0, out string why);
                Check("a WAV over 4GB opens through a download", decoder != null, why);
                if (decoder != null)
                {
                    using (decoder)
                    {
                        Check($"a WAV over 4GB played through a download is as long as the file ({decoder.Duration:0.0}s, expected {expected:0.0}s)",
                            Math.Abs(decoder.Duration - expected) < 0.01, decoder.GetType().Name);
                    }
                }
            }
            try { File.Delete(path); } catch { }
        }
    }
}
