using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// 5.1.0: the player, decoders and the audio editor.
    /// </summary>
    internal static class Release510PlaybackTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("5.1.0, the player, decoders and the audio editor:");
            await Task.CompletedTask;

            var dir = Path.Combine(Path.GetTempPath(), "en510-playback-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                RestartKeepsDownload(dir);
                PendingLetGo(dir);
                ReleaseWords();
                OddlyNamedFiles(dir);
                SourceShapes();
                TempFiles(dir);
                AppleLossless(dir);
                Tags();
                TypeChange();
                MonoOpus(dir);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static T Field<T>(object on, string name) =>
            (T)on.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(on)!;

        private static void SetField(object on, string name, object? value) =>
            on.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(on, value);

        private static StreamingSource FullDownload(string dir, string name)
        {
            var file = Path.Combine(dir, name);
            File.WriteAllBytes(file, new byte[200_000]);
            var source = new StreamingSource(file, 1 << 20);
            for (int i = 0; i < 100 && !source.Complete; i++) Thread.Sleep(50);
            return source;
        }

        // 1: Enter on the track already playing keeps its finished download.
        private static void RestartKeepsDownload(string dir)
        {
            using var player = new AudioPlayer();
            var download = FullDownload(dir, "a.flac");
            Check("the test download finished", download.Complete);
            SetField(player, "_streaming", download);
            SetField(player, "_path", @"\\server\share\a.flac");

            Check("the same track again keeps its finished download",
                ReferenceEquals(player.KeepableDownload(@"\\SERVER\share\a.flac"), download));
            Check("another track does not take it", player.KeepableDownload(@"\\server\share\b.flac") == null);

            download.Release();
            Check("a download that was let go is not reused", player.KeepableDownload(@"\\server\share\a.flac") == null);
            SetField(player, "_streaming", null);
            download.Dispose();
        }

        // 4: the cursor back on the playing track lets go of a download readied for another row.
        private static void PendingLetGo(string dir)
        {
            using var player = new AudioPlayer();
            var pending = FullDownload(dir, "other.flac");
            SetField(player, "_path", @"\\server\share\playing.flac");
            SetField(player, "_pending", pending);
            SetField(player, "_pendingPath", @"\\server\share\other.flac");

            player.Prepare(@"\\server\share\playing.flac");
            Check("resting on the playing track drops the readied download",
                Field<object?>(player, "_pending") == null);
        }

        // 5: the release message.
        private static void ReleaseWords()
        {
            Equal("one minute is not minutes", "Released 3 MB after 1 minute paused",
                AudioPlayer.ReleaseMessage("3 MB", 1, false));
            Equal("a finished track is not called paused", "Released 3 MB, 10 minutes after the track finished",
                AudioPlayer.ReleaseMessage("3 MB", 10, true));
        }

        // 6: a WAV saved with an Ogg name still plays.
        private static void OddlyNamedFiles(string dir)
        {
            foreach (var ext in new[] { ".ogg", ".opus" })
            {
                var path = Path.Combine(dir, "mislabelled" + ext);
                Tests.WriteTone(path, 0.5, 0.5, 44100, 2);
                var decoder = TrackDecoder.Open(path, null, 0, 0, out var why);
                Check($"a WAV named {ext} is decoded anyway", decoder != null, why);
                if (decoder != null)
                {
                    var block = new float[4096 * Math.Max(1, decoder.Channels)];
                    int got = decoder.Read(block, 4096);
                    Check($"and gives sound ({ext})", got > 0 && block.Take(got).Any(v => Math.Abs(v) > 0.01f));
                    decoder.Dispose();
                }
            }
        }

        private static string SourceText(string name)
        {
            var at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at != null && !Directory.Exists(Path.Combine(at.FullName, "src"))) at = at.Parent;
            if (at == null) return "";
            var path = Path.Combine(at.FullName, "src", name);
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch { return ""; }
        }

        // 2, 3, 7, 12, 14: form and player code that a test cannot drive without a window or a device.
        private static void SourceShapes()
        {
            var player = SourceText("AudioPlayer.cs");
            Check("a track that installs paused starts the release clock",
                player.Contains("direct.Pause();\n                ArmRelease();") ||
                player.Contains("direct.Pause();\r\n                ArmRelease();"));
            Check("a new track clears the last track's release clock",
                player.Contains("if (keep == null) StopStreaming();") && player.Contains("CancelRelease();"));
            Check("Back to the start does not need a length",
                player.Contains("unknownLength && fraction > 0"));

            var silence = SourceText("SilenceForm.cs");
            Check("Defaults stops the one-row speech", silence.Contains("_settle.Stop();\n                _spoke = null;") ||
                silence.Contains("_settle.Stop();\r\n                _spoke = null;"));

            Check("Convert audio tabs to Convert before Cancel", SourceText("AudioConvertForm.cs").Contains("ok.TabIndex = 0;"));
            Check("the shortcuts page tabs to OK first", SourceText("ShortcutsForm.cs").Contains("ok.TabIndex = 0;"));
            Check("the speech page tabs in screen order", SourceText("SpeechForm.cs").Contains("preview.TabIndex = 0;"));
            Check("the pitch page tabs to the tempo box under the list", SourceText("PitchForm.cs").Contains("tempoBox.TabIndex = 2;"));

            var editor = SourceText("AudioEditorForm.cs");
            Check("closing the editor cancels the measuring", editor.Contains("measuring.Cancel()"));
            Check("the editor tells ALAC from AAC", editor.Contains("IsAppleLossless"));
        }

        // 11: stale temp files are swept, fresh ones left, and sync skips them.
        private static void TempFiles(string dir)
        {
            var folder = Path.Combine(dir, "music");
            Directory.CreateDirectory(folder);
            var old = Path.Combine(folder, ".converting-aaaa1111.flac");
            var fresh = Path.Combine(folder, ".converting-bbbb2222.flac");
            var song = Path.Combine(folder, "song.flac");
            foreach (var f in new[] { old, fresh, song }) File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));

            AudioConverter.SweepStaleTemps(new[] { folder });
            Check("an old half-written temp file is swept", !File.Exists(old));
            Check("a recent one (another conversion may be writing it) stays", File.Exists(fresh));
            Check("a real song is never touched", File.Exists(song));

            Check("sync does not upload a temp file", SyncPlanner.IsSkipped(".converting-aaaa1111.flac"));
            Check("sync still takes a real flac", !SyncPlanner.IsSkipped("song.flac"));
        }

        // 13: Apple Lossless is told from AAC by its sample description.
        private static void AppleLossless(string dir)
        {
            byte[] Fake(string format) => System.Text.Encoding.ASCII.GetBytes("....ftypM4A ....moovstsd" + "\0\0\0\0" + "\0\0\0\u0001" + "\0\0\0\0" + format);
            var alac = Path.Combine(dir, "lossless.m4a");
            var aac = Path.Combine(dir, "lossy.m4a");
            var named = Path.Combine(dir, "named.m4a");
            File.WriteAllBytes(alac, Fake("alac"));
            File.WriteAllBytes(aac, Fake("mp4a"));
            File.WriteAllBytes(named, System.Text.Encoding.ASCII.GetBytes("....ftypM4A title=alac stsd mp4a"));

            Check("an ALAC .m4a is recognised", AudioFormats.IsAppleLossless(alac));
            Check("an AAC .m4a is not", !AudioFormats.IsAppleLossless(aac));
            Check("a title that says alac does not fool it", !AudioFormats.IsAppleLossless(named));
            Check("Apple Lossless with no bit rate is asked about, not turned into AAC",
                AudioFormats.SameFormatAs(".m4a", null, true) == null);
        }

        // 15 and 16: tags that travel, and a failure message that stays short.
        private static void Tags()
        {
            var set = new AudioTagSet("T", null, null, null, null, null, null, "Composer Name", "2/3");
            Check("composer and disc count as tags", !set.IsEmpty);
            var fields = set.VorbisFields().ToList();
            Check("composer goes to Vorbis comments", fields.Contains(("COMPOSER", "Composer Name")));
            Check("disc goes to Vorbis comments", fields.Contains(("DISCNUMBER", "2/3")));

            var back = AudioTags.FromVorbisFields(fields);
            Equal("composer comes back from Vorbis comments", "Composer Name", back?.Composer ?? "");
            Equal("disc comes back from Vorbis comments", "2/3", back?.Disc ?? "");

            var results = Enumerable.Range(0, 40)
                .Select(i => new TagResult($@"C:\m\track{i}.opus", i < 15 ? null : "Windows cannot write tags to Ogg or Opus files, and Explorer Native does not write them itself yet."))
                .ToList();
            var summary = TagEditing.Summary(results);
            Check("the failure summary names three files at most", summary.Contains("track15.opus") && summary.Contains("track17.opus") && !summary.Contains("track18.opus"), summary);
            Check("and says how many more", summary.Contains("And 22 more."), summary);
        }

        // 8: a media type change mid-stream is renegotiated, driven through a fake.
        private static void TypeChange()
        {
            Check("the type-changed flag is noticed", AudioDecoder.TypeChanged(0, 0x20));
            Check("the stream-change error is noticed", AudioDecoder.TypeChanged(unchecked((int)0xC00D6D61), 0));
            Check("an ordinary read is not a change", !AudioDecoder.TypeChanged(0, 0));

            var asked = new System.Collections.Generic.List<(int Rate, int Channels)>();
            bool ok = AudioDecoder.Renegotiate((r, c) => { asked.Add((r, c)); return 0; }, 44100, 2);
            Check("the output type is put back to the running format", ok && asked.Count == 1 && asked[0] == (44100, 2));

            asked.Clear();
            ok = AudioDecoder.Renegotiate((r, c) => { asked.Add((r, c)); return r == 0 ? 0 : -1; }, 44100, 2);
            Check("if that is refused it asks again for the channels alone",
                ok && asked.Count == 2 && asked[1] == (0, 2));

            ok = AudioDecoder.Renegotiate((r, c) => -1, 44100, 2);
            Check("and gives up only when both are refused", !ok);
        }

        // 10: mono Opus stays mono.
        private static void MonoOpus(string dir)
        {
            var path = Path.Combine(dir, "mono.opus");
            var samples = new float[48000];
            for (int i = 0; i < samples.Length; i++) samples[i] = (float)Math.Sin(i * 0.05) * 0.4f;
            using (var encoder = new OpusFileEncoder(path, 1, 64, null))
            {
                encoder.Write(samples, samples.Length);
                encoder.Finish();
            }

            var decoder = TrackDecoder.Open(path, null, 0, 0, out var why);
            Check("a mono Opus file opens", decoder != null, why);
            if (decoder == null) return;
            Check("and has one channel", decoder.Channels == 1, "channels " + decoder.Channels);
            decoder.Dispose();
        }
    }
}
