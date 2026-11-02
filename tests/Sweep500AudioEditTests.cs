using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of converting, editing and tags.
    /// </summary>
    internal static class Sweep500AudioEditTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, converting, editing and tags:");
            EditorWaitsForTheMp3FrameWalk();
            EditedDecoderReadsTheLastPartToTheRealEnd();
            ConverterAsksTheLengthAgainAtTheEnd();
            DiscAudioIsNotConvertedOrEditedInPlace();
            EditorLeavesTheLineUnderTheKeyboardAlone();
            await Task.CompletedTask;
        }

        // ---------- 4: every arrow said twice ----------

        /// <summary>
        /// The editor's state list rewrote the line under the keyboard after
        /// every key. A list box changes a line by taking it out and putting it
        /// back, which NVDA hears as landing on it again, so Right arrow said
        /// "11 seconds" and then "Position 11.000 seconds of 3 minutes 45".
        /// </summary>
        private static void EditorLeavesTheLineUnderTheKeyboardAlone()
        {
            // The mechanism: only the selected line is taken out and put back.
            int otherLines = -1, selectedLine = -1, selectedAfter = -1;
            var thread = new Thread(() =>
            {
                using var form = new System.Windows.Forms.Form();
                var box = new System.Windows.Forms.ListBox();
                form.Controls.Add(box);
                box.Items.AddRange(new object[] { "a", "b", "c", "d" });
                _ = box.Handle;
                box.SelectedIndex = 2;
                int count = 0;
                box.SelectedIndexChanged += (_, _) => count++;
                box.Items[0] = "a2";
                box.Items[3] = "d2";
                otherLines = count;
                box.Items[2] = "c2";
                selectedLine = count - otherLines;
                selectedAfter = box.SelectedIndex;
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            Check($"rewriting list box lines that are not selected leaves the selection alone ({otherLines} changes)",
                otherLines == 0 && selectedAfter == 2);
            Check($"rewriting the selected line takes it out and puts it back ({selectedLine} selection changes)", selectedLine > 0);

            // The decision: the editor leaves the focused line alone and brings
            // it up to date once it is left.
            var editor = SourceText("AudioEditorForm.cs");
            var refresh = Between(editor, "private void Refresh_(", "private void Say(");
            Check("the editor's list holds the line under the keyboard while it has focus",
                refresh.Contains("_info.Focused") && refresh.Contains("hold"));
            Check("and brings it up to date when the keyboard leaves it",
                Between(editor, "public AudioEditorForm(", "// ------").Contains("SelectedIndexChanged"));
            Check("the first fill replaces the Loading line under the keyboard",
                Between(editor, "protected override async void OnShown", "private string? _openFailure").Contains("Refresh_(all: true)"));
            Check("a spoken message does not rewrite the line under the keyboard",
                !Between(editor, "private void Say(", "// ------").Contains("all: true"));
        }

        // ---------- 3: CD tracks and discs ----------

        /// <summary>
        /// Convert audio and Edit audio write beside the source, and nothing can
        /// be written on a CD or DVD, so every file failed with "access was
        /// refused". CD tracks cannot even be copied off (the .cda is a stub),
        /// so the commands are hidden for them by name, and audio on any disc is
        /// refused with a sentence, as audio on Google Drive is.
        /// </summary>
        private static void DiscAudioIsNotConvertedOrEditedInPlace()
        {
            Check("a file on a CD drive is on a disc", AudioConverter.IsOnDisc(@"X:\Track01.cda", _ => DriveType.CDRom));
            Check("a file on a fixed drive is not", !AudioConverter.IsOnDisc(@"C:\Music\a.mp3", _ => DriveType.Fixed));
            Check("a share is not, and its type is not asked",
                !AudioConverter.IsOnDisc(@"\\server\music\a.mp3", _ => throw new InvalidOperationException()));

            var main = SourceText("MainForm.cs");
            var gate = Between(main, "bool focusedAudio", "ShowDriveLinkItems");
            Check("the menu hides Edit audio and Convert audio on CD tracks, by name",
                gate.Contains("IsCdaName") && gate.Contains("_editAudioItem.Visible") && gate.Contains("_convertAudioItem.Visible"));
            Check("Convert audio refuses audio on a disc",
                Between(main, "void ConvertAudio()", "AudioConvertForm.Ask").Contains("AudioConverter.IsOnDisc"));
            Check("Edit audio refuses audio on a disc",
                Between(main, "void EditAudio()", "new AudioEditorForm").Contains("AudioConverter.IsOnDisc"));
        }

        // ---------- helpers ----------

        private static string NewDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-sweep500ae-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        private static float[] TakeAll(ITrackDecoder decoder, long most = long.MaxValue)
        {
            int channels = Math.Max(1, decoder.Channels);
            var all = new List<float>();
            var block = new float[16384 * channels];
            int empties = 0;
            long have = 0;
            while (have < most)
            {
                int got = decoder.Read(block, (int)Math.Min(16384, most - have));
                if (got <= 0) { if (++empties >= 3) break; continue; }
                empties = 0;
                all.AddRange(new ArraySegment<float>(block, 0, got * channels));
                have += got;
            }
            return all.ToArray();
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

        private static string Between(string text, string from, string to)
        {
            int start = text.IndexOf(from, StringComparison.Ordinal);
            if (start < 0) return "";
            int end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
            return end < 0 ? text[start..] : text[start..end];
        }

        /// <summary>Lets a held MP3 frame walk go a moment after the caller has started waiting for it.</summary>
        private static void ReleaseSoon(ManualResetEventSlim hold) =>
            Task.Run(async () => { await Task.Delay(300); hold.Set(); });

        // ---------- 1 and 2: the editor's MP3 length and seeks ----------

        /// <summary>
        /// A joined VBR audiobook (the fixture twelve times, each copy with its
        /// own tag and Xing frame). The editor read its length and seeked the
        /// moment it opened, before the frame walk had got anywhere, so it took
        /// Media Foundation's guesses: the length of one copy, and a seek that
        /// lands wherever. The walk is held back here so that is certain.
        /// </summary>
        private static void EditorWaitsForTheMp3FrameWalk()
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "clicks-vbr.mp3");
            if (!File.Exists(fixture)) { Check("the VBR MP3 fixture is there", false, fixture); return; }

            var dir = NewDir();
            try
            {
                var joined = Path.Combine(dir, "joined.mp3");
                var one = File.ReadAllBytes(fixture);
                using (var output = File.Create(joined))
                    for (int i = 0; i < 12; i++) output.Write(one);

                float[] whole;
                int rate, channels;
                using (var straight = TrackDecoder.Open(joined, null, 0, 0, out string why))
                {
                    if (straight == null) { Check("the joined MP3 opens", false, why); return; }
                    rate = straight.SampleRate;
                    channels = Math.Max(1, straight.Channels);
                    whole = TakeAll(straight);
                }
                long truth = whole.Length / channels;

                // What the editor used to read: the length straight after opening.
                var hold = new ManualResetEventSlim(false);
                double early;
                try
                {
                    Mp3Seek.HoldWalkForTests = hold;
                    using (var quick = TrackDecoder.Open(joined, null, 0, 0, out _)) early = quick?.Duration ?? 0;
                    Check($"the joined MP3 shows the problem: Media Foundation's guess ({early:0.###}s) is not its length ({truth / (double)rate:0.###}s)",
                        Math.Abs(early * rate - truth) > rate);

                    // The editor's open waits for the walk, held here until it has started waiting.
                    ReleaseSoon(hold);
                    var (measuredRate, frames, _, _) = EditSource.Measure(joined, EditSource.Settle);
                    Check($"the editor's length of a joined MP3 is the decoded length ({frames} frames against {truth})",
                        measuredRate == rate && Math.Abs(frames - truth) <= 2);
                }
                finally
                {
                    hold.Set();
                    Mp3Seek.HoldWalkForTests = null;
                }

                // A cut start: the saved file starts where the cut ends, not seconds
                // before it. Inside Media Foundation's guess and well past it.
                foreach (var (cutTo, what) in new[] { (8L * rate, "the first 8 seconds"), (truth / 2, "the first half") })
                {
                    var edits = new EditList(truth, rate).Cut(0, cutTo);
                    var hold2 = new ManualResetEventSlim(false);
                    float[] got;
                    try
                    {
                        // Held until the seek has been made, unless the open waits.
                        Mp3Seek.HoldWalkForTests = hold2;
                        ReleaseSoon(hold2);
                        var inner = EditSource.Open(joined, 0, 0, EditSource.Settle, out _);
                        if (inner == null) { Check("the joined MP3 opens for editing", false); return; }
                        using var edited = new EditedDecoder(inner, edits);
                        got = TakeAll(edited, rate / 2);
                    }
                    finally
                    {
                        hold2.Set();
                        Mp3Seek.HoldWalkForTests = null;
                    }
                    int compare = Math.Min(got.Length, (rate / 2) * channels);
                    double worst = got.Length == (rate / 2) * channels ? 0 : 1;
                    for (int i = 0; i < compare; i++)
                        worst = Math.Max(worst, Math.Abs(got[i] - whole[cutTo * channels + i]));
                    Check($"cutting {what} of a joined VBR MP3 starts on the sample after the cut (worst difference {worst:0.######})",
                        worst < 1e-4);
                }
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// The second line of defence: when the length the editor was given is
        /// short (an estimate, or a walk that gave up), the part that runs to
        /// the end is read to the real end rather than dropped.
        /// </summary>
        private static void EditedDecoderReadsTheLastPartToTheRealEnd()
        {
            var dir = NewDir();
            try
            {
                var source = Path.Combine(dir, "tone.wav");
                Tests.WriteTone(source, 4, 0.5);

                // Told three seconds of four; the first second trimmed off.
                var trimmed = new EditList(3 * 48000, 48000).Cut(0, 48000);
                var inner = TrackDecoder.Open(source, null, 0, 0, out string why);
                if (inner == null) { Check("the tone opens", false, why); return; }
                long frames;
                using (var decoder = new EditedDecoder(inner, trimmed)) frames = TakeAll(decoder).Length / 2;
                Equal("a short length estimate does not lose the end of a trimmed file (frames)", (3 * 48000).ToString(), frames.ToString());

                // A cut that takes the end off still takes it off.
                var cutEnd = new EditList(3 * 48000, 48000).Cut(2 * 48000, 3 * 48000);
                inner = TrackDecoder.Open(source, null, 0, 0, out _)!;
                using (var decoder = new EditedDecoder(inner, cutEnd)) frames = TakeAll(decoder).Length / 2;
                Equal("but cutting the end off still ends where the cut starts (frames)", (2 * 48000).ToString(), frames.ToString());
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// A converted MP3 whose length Media Foundation guessed too long at the
        /// start (128k for three seconds, then 320k for three, so the guess from
        /// the first frames is ten and a half seconds) was thrown away as "only 6
        /// seconds of 10 could be read", though every frame was converted.
        /// </summary>
        private static void ConverterAsksTheLengthAgainAtTheEnd()
        {
            var dir = NewDir();
            try
            {
                var tone = Path.Combine(dir, "tone.wav");
                Tests.WriteTone(tone, 3, 0.5, 44100);
                var parts = new List<string>();
                foreach (var id in new[] { "mp3-128", "mp3-320" })
                {
                    var part = Path.Combine(dir, id + ".mp3");
                    try
                    {
                        using var decoder = TrackDecoder.Open(tone, null, 0, 0, out _)!;
                        AudioConverter.Encode(decoder, AudioFormats.ById(id), part, 16, null, null, CancellationToken.None, new List<string>());
                        parts.Add(part);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  (no MP3 encoder here, the converter's length check is not tested: {ex.Message})");
                        return;
                    }
                }

                var joined = Path.Combine(dir, "joined.mp3");
                using (var output = File.Create(joined))
                    foreach (var part in parts) output.Write(File.ReadAllBytes(part));

                double early;
                using (var quick = TrackDecoder.Open(joined, null, 0, 0, out _)) early = quick?.Duration ?? 0;
                if (early < 6 / 0.9 + 1.5)
                    Console.WriteLine($"  (Media Foundation's guess at the joined MP3 was {early:0.###}s, not too long; the check below proves little here)");

                var output2 = Path.Combine(dir, "joined.wav");
                string? failed = null;
                try
                {
                    using var decoder = TrackDecoder.Open(joined, null, 0, 0, out string why)
                                        ?? throw new IOException(why);
                    AudioConverter.Encode(decoder, AudioFormats.ById("wav-16"), output2, 16, null, null, CancellationToken.None, new List<string>());
                }
                catch (Exception ex) { failed = ex.Message; }
                Check($"converting an MP3 whose length was first guessed at {early:0.###}s is not refused as cut off", failed == null, failed);
            }
            finally { Cleanup(dir); }
        }
    }
}
