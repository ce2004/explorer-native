using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of the web app.
    /// </summary>
    internal static class Sweep500WebTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the web app:");
            WebDeleteNeverAsksOnThePc();
            AudioThatStopsArrivingFails();
            LongAudioIsNotCut();
            AacDetailsAreNotMp3();
            DeepMatroskaDoesNotCrash();
            DetailsNeverSpeakOnThePc();
            MoveOutOfDriveThatFailedSaysCopied();
            MoveInsideDriveCountsWhatMoved();
            OwnerTrustNeedsTheTailscaleService();
            EveryLoopbackAddressSharesOneCount();
            await Task.CompletedTask;
        }

        // ---------------- 10. owner trust only from the Tailscale service, not anything named tailscaled ----------------

        private static void OwnerTrustNeedsTheTailscaleService()
        {
            const int service = 3844;
            var svc = new ProcessFacts(service, 700, 0, 1000, "tailscaled.exe");
            var child = new ProcessFacts(5532, service, 0, 2000, "tailscaled.exe");
            Check("owner trust: the Tailscale service's own process is Tailscale", ConnectServer.IsTailscaleService(svc, null, service));
            Check("owner trust: the tailscaled the service started is Tailscale", ConnectServer.IsTailscaleService(child, svc, service));
            var copy = new ProcessFacts(9100, 4200, 0, 3000, "tailscaled.exe");
            var copier = new ProcessFacts(4200, 700, 0, 2500, "svchost.exe");
            Check("owner trust: a tailscaled a service account started in session 0 is not, so it needs the pairing code",
                !ConnectServer.IsTailscaleService(copy, copier, service));
            var reused = new ProcessFacts(service, 700, 0, 5000, "tailscaled.exe");
            Check("owner trust: a child whose parent id was reused by a newer service process is not",
                !ConnectServer.IsTailscaleService(child with { CreateTime = 4000 }, reused, service));
            Check("owner trust: with no Tailscale service running, nothing is", !ConnectServer.IsTailscaleService(child, svc, 0));
            Check("owner trust: something else the service started is not", !ConnectServer.IsTailscaleService(child with { Name = "cmd.exe" }, svc, service));

            // Live, on this PC: the snapshot reads, the suite itself is not Tailscale, and if Tailscale runs here
            // one of its processes passes (so the owner is still let in without the code).
            var table = ProcessTable.Snapshot();
            int me = Environment.ProcessId;
            Check("owner trust: the process snapshot finds this process with its parent and start time",
                table.TryGetValue(me, out var self) && self.CreateTime > 0 && self.Name.Length > 0, table.Count.ToString());
            Check("owner trust: this suite is not taken for Tailscale",
                !ConnectServer.IsTailscaleService(self, table.TryGetValue(self.ParentPid, out var sp) ? sp : null, TailscaleServiceProcess.Pid()));
            int live = TailscaleServiceProcess.Pid();
            if (live > 0)
            {
                bool any = false;
                foreach (var f in table.Values)
                    if (ConnectServer.IsTailscaleService(f, table.TryGetValue(f.ParentPid, out var pf) ? pf : null, live)) any = true;
                Check("owner trust: the Tailscale service running on this PC passes", any, $"service {live}");
            }
            else Console.WriteLine("  (no Tailscale service running here; the live check is skipped)");
        }

        // ---------------- 11. every loopback address shares one count of wrong codes ----------------

        private static void EveryLoopbackAddressSharesOneCount()
        {
            var one = ConnectServer.AddressKey(System.Net.IPAddress.Loopback);
            Check("pairing code: 127.0.0.2, 127.45.6.7 and ::ffff:127.0.0.3 count as 127.0.0.1, so new addresses get no new guesses",
                ConnectServer.AddressKey(System.Net.IPAddress.Parse("127.0.0.2")) == one &&
                ConnectServer.AddressKey(System.Net.IPAddress.Parse("127.45.6.7")) == one &&
                ConnectServer.AddressKey(System.Net.IPAddress.Parse("::ffff:127.0.0.3")) == one &&
                ConnectServer.AddressKey(System.Net.IPAddress.IPv6Loopback) == one, one);
            Check("pairing code: another address keeps a count of its own",
                ConnectServer.AddressKey(System.Net.IPAddress.Parse("100.64.0.9")) == "addr:100.64.0.9");
        }

        // ---------------- 8. a move out of Drive with a failure says nothing left Drive ----------------

        private static void MoveOutOfDriveThatFailedSaysCopied()
        {
            var job = new ConnectJob("d", "move");
            job.Counted(9, 1);
            job.Fail(@"G:\a\y.txt", "It is open in another program.");
            job.KeepOriginals();
            var (state, message) = ConnectServer.Outcome(job, move: true);
            Check("move out of Drive with one failure: says Copied 9 files, and that the originals are all still on Drive, never Moved",
                state == "done" && message != null && message.StartsWith("Copied 9 files; 1 file could not be copied.", StringComparison.Ordinal) &&
                message.Contains("Google Drive", StringComparison.Ordinal) && !message.Contains("Moved", StringComparison.Ordinal), $"{state}: {message}");

            var local = new ConnectJob("l", "move");
            local.Counted(2, 1);
            local.Fail(@"C:\a\y.txt", "It is open in another program.");
            var (_, localSaid) = ConnectServer.Outcome(local, move: true);
            Check("a local move with one failure still says Moved", localSaid?.StartsWith("Moved 2 files; 1 file could not be moved.", StringComparison.Ordinal) == true, localSaid);

            var log = new ConflictLog();
            for (int i = 0; i < 33; i++) log.Skipped($"name {i}");
            var said = ConnectFiles.MoveOutSentence("Copied out of Google Drive. Some items were skipped, so nothing was removed from Drive.", log.Snapshot(), 0, true);
            Check("move out of Drive with over 32 skipped: says both that nothing was removed and what was skipped",
                said != null && said.Contains("nothing was removed from Drive", StringComparison.Ordinal) && said.Contains("33", StringComparison.Ordinal), said);

            Check("the window says the same: a failed move out of Drive is a copy with nothing removed from Google Drive",
                NameRules.PartialTransferSentence(2, 1, move: true, fromDrive: true).Contains("nothing was removed from Google Drive", StringComparison.Ordinal));
        }

        // ---------------- 9. a move inside Drive counts what moved, not what was tried ----------------

        private static void MoveInsideDriveCountsWhatMoved()
        {
            var body = Slice(Source(Path.Combine("src", "ConnectFiles.cs")), "async Task DriveToDrive(", "private async Task<int> OnDrive(");
            Check("web move inside Drive: the source of DriveToDrive was found", body.Length > 0);
            Check("web move inside Drive gives the job OnDrive's count of what really moved",
                body.Contains("= await OnDrive(", StringComparison.Ordinal) && body.Contains("job.Counted(", StringComparison.Ordinal));

            // What DriveToDrive now leaves behind when all three moves are refused.
            var job = new ConnectJob("m", "move");
            job.Report(3, 3, 0, 0, "");
            for (int i = 0; i < 3; i++) job.Fail($@"G:\a\{i}.txt", "You do not have permission to add anything to that folder.");
            job.Counted(0, 3);
            var (state, _) = ConnectServer.Outcome(job, move: true);
            Equal("a move inside Drive where every item was refused is failed, not done", "failed", state);
        }

        // ---------------- 6. web details never ask Drive for a link, whose failure the PC would say ----------------

        private static void DetailsNeverSpeakOnThePc()
        {
            var body = Slice(Source(Path.Combine("src", "ConnectFiles.cs")), "public async Task<Dictionary<string, object?>> DetailsAsync(", "public async Task<string> RenameAsync(");
            Check("web details: the source of DetailsAsync was found", body.Length > 0);
            Check("web details ask Drive for no share link, so Details on Shared drives or Trash from the phone says nothing on the PC",
                !body.Contains("LinkFor(", StringComparison.Ordinal));
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-sweep500web-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static System.Collections.Generic.Dictionary<string, object?>? DetailsOf(string path)
        {
            using var source = new FileRangeSource(path);
            var bytes = new CachedBytes(source, CancellationToken.None);
            return ConnectDetails.Media(bytes, path, CancellationToken.None);
        }

        private static string AudioValue(System.Collections.Generic.Dictionary<string, object?>? media, string key)
        {
            if (media == null || !media.TryGetValue("audio", out var a) || a is not System.Collections.IEnumerable list) return "";
            foreach (var s in list)
                if (s is System.Collections.Generic.IDictionary<string, object?> d && d.TryGetValue(key, out var v)) return v?.ToString() ?? "";
            return "";
        }

        // ---------------- 4. an .aac file's details are not read as MP3 ----------------

        private static void AacDetailsAreNotMp3()
        {
            var dir = TempDir();
            try
            {
                // ADTS as encoders write it: buffer fullness 0x7FF, and frames whose length ends in 7 bits set, so
                // the header's bytes 5 and 6 are FF FC, a perfect MPEG-1 layer II header; stereo payloads start 0x21.
                var rng = new Random(5);
                var file = new MemoryStream();
                for (int k = 0; k < 400; k++)
                {
                    int len = 7 + 8 * (40 + rng.Next(20)) + (k % 8 == 0 ? 7 : rng.Next(8));
                    var frame = new byte[len];
                    rng.NextBytes(frame);
                    frame[0] = 0xFF; frame[1] = 0xF1; frame[2] = 0x4C;
                    frame[3] = (byte)(0x80 | (len >> 11));
                    frame[4] = (byte)(len >> 3);
                    frame[5] = (byte)(((len & 7) << 5) | 0x1F);
                    frame[6] = 0xFC;
                    frame[7] = 0x21;
                    file.Write(frame);
                }
                var aac = Path.Combine(dir, "song.aac");
                File.WriteAllBytes(aac, file.ToArray());
                var media = DetailsOf(aac);
                string container = media != null && media.TryGetValue("container", out var c) ? c?.ToString() ?? "" : "";
                string codec = AudioValue(media, "codec");
                Check("details: an ADTS .aac file is not called MPEG audio, MP3 or MPEG layer 2",
                    container != "MPEG audio" && codec != "MP3" && !codec.StartsWith("MPEG layer", StringComparison.Ordinal), $"[{container}] [{codec}]");

                var mp3 = Path.Combine(AppContext.BaseDirectory, "Fixtures", "clicks-vbr.mp3");
                var real = DetailsOf(mp3);
                Check("details: a real MP3 is still read as MP3 by its own header",
                    real != null && real.TryGetValue("container", out var rc) && rc?.ToString() == "MPEG audio" && AudioValue(real, "codec") == "MP3",
                    real == null ? "null" : $"{(real.TryGetValue("container", out var x) ? x : "")} {AudioValue(real, "codec")}");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------------- 5. a crafted Matroska file nested deep does not overflow the stack ----------------

        private static void DeepMatroskaDoesNotCrash()
        {
            var dir = TempDir();
            try
            {
                var f = new MemoryStream();
                void W(params byte[] b) => f.Write(b);
                void Sized(byte[] id, byte[] body)
                {
                    W(id);
                    long n = body.Length;
                    W(0x01, 0, 0, 0, 0, (byte)(n >> 16), (byte)(n >> 8), (byte)n);
                    W(body);
                }
                byte[] Repeat(byte a, byte b, int times)
                {
                    var r = new byte[times * 3];
                    for (int i = 0; i < times; i++) { r[3 * i] = a; r[3 * i + 1] = b; r[3 * i + 2] = 0xFF; }
                    return r;
                }
                W(0x1A, 0x45, 0xDF, 0xA3, 0x8B, 0x42, 0x82, 0x88);
                W(System.Text.Encoding.ASCII.GetBytes("matroska"));
                W(0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
                Sized(new byte[] { 0x10, 0x43, 0xA7, 0x70 }, Repeat(0x45, 0xB9, 200_000));
                Sized(new byte[] { 0x12, 0x54, 0xC3, 0x67 }, Repeat(0x73, 0x73, 200_000));
                var mkv = Path.Combine(dir, "deep.mkv");
                File.WriteAllBytes(mkv, f.ToArray());

                System.Collections.Generic.Dictionary<string, object?>? media = null;
                Exception? failed = null;
                var thread = new Thread(() => { try { media = DetailsOf(mkv); } catch (Exception e) { failed = e; } }, 256 * 1024);
                thread.Start();
                bool ended = thread.Join(TimeSpan.FromSeconds(60));
                Check("details: a Matroska file with chapters and tags nested 200,000 deep is read on a small stack without crashing the app",
                    ended && failed == null, failed?.Message);
                Check("details: and the nesting gives no chapters", media == null || !media.ContainsKey("chapters"));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------------- helpers ----------------

        private static string? Source(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            try { return File.ReadAllText(Path.Combine(dir.FullName, relative)); } catch { return null; }
        }

        /// <summary>The text from <paramref name="start"/> up to <paramref name="end"/>, or empty.</summary>
        private static string Slice(string? text, string start, string end)
        {
            if (text == null) return "";
            int a = text.IndexOf(start, StringComparison.Ordinal);
            if (a < 0) return "";
            int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
            return b < 0 ? text[a..] : text[a..b];
        }

        // ---------------- 1. web delete never puts a question on the PC ----------------

        private static void WebDeleteNeverAsksOnThePc()
        {
            var body = Slice(Source(Path.Combine("src", "ConnectFiles.cs")), "public async Task<ConnectDeleted> DeleteAsync(", "[StructLayout");
            Check("web delete: the source of DeleteAsync was found", body.Length > 0);
            Check("web delete recycles without any window, so a file too big for the bin is kept rather than asked about on the PC",
                body.Contains("ShellDelete.RecycleWithoutUi(", StringComparison.Ordinal) &&
                !body.Contains("ShellDelete.Recycle(", StringComparison.Ordinal));
        }

        // ---------------- 2. /api/audio fails, rather than going silent, when the bytes stop ----------------

        /// <summary>Serves a file's bytes up to a cut, then fails: no bytes, or an exception.</summary>
        private sealed class CutSource : IRangeSource
        {
            private readonly byte[] _bytes;
            private readonly bool _throw;
            /// <summary>Where the bytes stop coming; set after opening, as Google stalls part way through playing.</summary>
            public long Cut = long.MaxValue;
            public int Refused;
            public CutSource(byte[] bytes, bool throws) { _bytes = bytes; _throw = throws; }
            public long Length => _bytes.Length;
            public int Streams => 1;
            public Task<int> ReadAsync(long offset, Memory<byte> into, CancellationToken token)
            {
                if (offset >= Cut)
                {
                    Refused++;
                    if (_throw) throw new IOException("Google stopped answering");
                    return Task.FromResult(0);
                }
                int n = (int)Math.Min(into.Length, Math.Min(Cut, _bytes.Length) - offset);
                _bytes.AsSpan((int)offset, n).CopyTo(into.Span);
                return Task.FromResult(n);
            }
            public void Dispose() { }
        }

        private static byte[]? OpusTone(int seconds)
        {
            try
            {
                const int rate = 48000;
                var pcm = new short[rate * seconds * 2];
                for (int i = 0; i < rate * seconds; i++)
                {
                    short v = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000);
                    pcm[2 * i] = v;
                    pcm[2 * i + 1] = v;
                }
                using var file = new MemoryStream();
                var encoder = Concentus.OpusCodecFactory.CreateEncoder(48000, 2, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO, null);
                var writer = new Concentus.Oggfile.OpusOggWriteStream(encoder, file, null, 48000, 2, false);
                writer.WriteSamples(pcm, 0, pcm.Length);
                writer.Finish();
                return file.ToArray();
            }
            catch (Exception e) { Check("an Opus tone could be made", false, e.Message); return null; }
        }

        private static void AudioThatStopsArrivingFails()
        {
            var bytes = OpusTone(20);
            if (bytes == null) return;

            using (var whole = ConnectAudio.Open("tone.opus", new CutSource(bytes, false), bytes.Length, out var why))
            {
                Check("audio: a Drive track that arrives whole opens", whole != null, why);
                if (whole != null)
                {
                    var ms = new MemoryStream();
                    string? failed = null;
                    try { whole.WriteRange(0, whole.Plan.TotalBytes - 1, ms, CancellationToken.None); }
                    catch (Exception e) { failed = e.Message; }
                    Check("audio: a track that arrives whole is served whole, with no error", failed == null && ms.Length == whole.Plan.TotalBytes,
                        $"{failed} {ms.Length} vs {whole.Plan.TotalBytes}");
                }
            }

            foreach (var throws in new[] { false, true })
            {
                string how = throws ? "with an error" : "with no bytes";
                var source = new CutSource(bytes, throws);
                using var audio = ConnectAudio.Open("tone.opus", source, bytes.Length, out var why);
                source.Cut = bytes.Length * 2 / 5;
                Check($"audio: a track that stops arriving {how} still opens", audio != null, why);
                if (audio == null) continue;
                var ms = new MemoryStream();
                Exception? caught = null;
                try { audio.WriteRange(0, audio.Plan.TotalBytes - 1, ms, CancellationToken.None); }
                catch (Exception e) { caught = e; }
                Check($"audio: a track that stops arriving {how} fails the response, so the phone retries, instead of playing silence to the end",
                    caught is IOException && ms.Length < audio.Plan.TotalBytes, $"{caught?.GetType().Name} {ms.Length} of {audio.Plan.TotalBytes}, {source.Refused} reads refused");
            }
        }

        // ---------------- 3. a long track is not cut at the 4 GB WAV limit ----------------

        private static void LongAudioIsNotCut()
        {
            const long most = uint.MaxValue - 36L;
            var three = ConnectAudio.PlanFor(48000, 2, 3L * 3600 * 48000);
            Check("audio: a three-hour track stays 24-bit stereo at its own rate",
                three.Bits == 24 && three.Channels == 2 && three.Rate == 48000 && three.Frames == 3L * 3600 * 48000, three.ToString());

            var six = ConnectAudio.PlanFor(48000, 2, 6L * 3600 * 48000);
            Check("audio: a six-hour Opus audiobook is served whole, as 16-bit, under the WAV limit",
                six.Frames / (double)six.Rate >= 6 * 3600 - 1 && six.DataBytes <= most && six.Bits == 16, six.ToString());

            var twenty = ConnectAudio.PlanFor(48000, 2, 20L * 3600 * 48000);
            Check("audio: a twenty-hour track is served whole, mono at a lower rate, under the WAV limit",
                twenty.Frames / (double)twenty.Rate >= 20 * 3600 - 1 && twenty.DataBytes <= most && twenty.Channels == 1 && twenty.Rate < 48000,
                twenty.ToString());

            var h = ConnectAudio.Header(six);
            Check("audio: a 16-bit plan's header says 16 bits, with the block and byte rate to match",
                BitConverter.ToUInt16(h, 34) == 16 && BitConverter.ToUInt16(h, 32) == six.Channels * 2 &&
                BitConverter.ToUInt32(h, 28) == six.Rate * six.Channels * 2 && BitConverter.ToUInt32(h, 40) == six.DataBytes);
        }
    }
}
