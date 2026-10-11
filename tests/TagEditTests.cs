using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// The tag editor: tags written through the Windows property system to
    /// real generated audio, read back through AudioTags; several files at once
    /// with only the edited fields written; and the refusals.
    /// </summary>
    internal static class TagEditTests
    {
        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            Console.WriteLine("Tag editor:");

            PureTests(check, equal);

            // The Drive letter is never read from this process: the editor splits
            // Drive files off before it reads anybody's tags.
            var main = SourceText("MainForm.cs");
            int start = main.IndexOf("private async Task<bool> EditTagsAsync", StringComparison.Ordinal);
            int split = start < 0 ? -1 : main.IndexOf("IsOnDrive", start, StringComparison.Ordinal);
            int read = start < 0 ? -1 : main.IndexOf("AudioTags.ReadFields", start, StringComparison.Ordinal);
            check("Edit tags sets Drive files aside before reading tags", start >= 0 && split > start && read > split, null);

            FormShapeTests(check);

            // Every audio harness gets a watchdog: a property handler that hangs
            // must end the run with a reason, not a stall.
            var done = new ManualResetEventSlim();
            var worker = new Thread(() =>
            {
                try { FileTests(check, equal); }
                catch (Exception ex) { check("tag file tests ran", false, ex.ToString()); }
                finally { done.Set(); }
            }) { IsBackground = true, Name = "tag edit tests" };
            worker.Start();
            if (!done.Wait(TimeSpan.FromSeconds(60))) check("tag file tests finished within 60s", false, null);
        }

        private static IEnumerable<System.Windows.Forms.Control> Walk(System.Windows.Forms.Control parent)
        {
            foreach (System.Windows.Forms.Control child in parent.Controls)
            {
                yield return child;
                foreach (var inner in Walk(child)) yield return inner;
            }
        }

        /// <summary>
        /// The window built but not shown: one box per field, a differing field
        /// named as such where NVDA reads it, unique access keys, and the
        /// numbering box only for several files.
        /// </summary>
        private static void FormShapeTests(Action<string, bool, string?> check)
        {
            var fields = TagFields.All.ToDictionary(f => f,
                f => f == TagField.Title ? new TagFieldState("", true) : new TagFieldState("x", false));

            using (var many = new TagEditorForm("Edit tags — 3 files", fields, 3))
            {
                var controls = Walk(many).ToList();
                var boxes = controls.OfType<System.Windows.Forms.TextBox>().ToList();
                check("one box per field", boxes.Count == TagFields.All.Length, boxes.Count.ToString());
                check("a differing field is named as left unchanged",
                    boxes.Any(b => b.AccessibleName == "Title (" + TagEditing.DiffersText + ")" && b.Text == ""), null);
                check("an agreeing field is named plainly",
                    boxes.Any(b => b.AccessibleName == "Album" && b.Text == "x"), null);
                check("several files offer numbering in list order",
                    controls.OfType<System.Windows.Forms.CheckBox>().Any(c => c.Text.Contains("list order")), null);

                var keys = controls.Where(c => c is System.Windows.Forms.Label or System.Windows.Forms.ButtonBase)
                    .Select(c => c.Text).Where(t => t.Contains('&'))
                    .Select(t => char.ToUpperInvariant(t[t.IndexOf('&') + 1])).ToList();
                check("the tag editor's access keys are unique", keys.Count == keys.Distinct().Count(),
                    string.Join("", keys));
            }

            using (var one = new TagEditorForm("Edit tags — a.mp3", fields, 1))
                check("one file has no numbering box",
                    !Walk(one).OfType<System.Windows.Forms.CheckBox>().Any(), null);
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

        private static void PureTests(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var a = new Dictionary<TagField, string> { [TagField.Album] = "One", [TagField.Title] = "A", [TagField.Year] = "1999" };
            var b = new Dictionary<TagField, string> { [TagField.Album] = "One", [TagField.Title] = "B", [TagField.Year] = "1999" };
            var merged = TagEditing.Merge(new IReadOnlyDictionary<TagField, string>?[] { a, b, null });
            check("files that agree show the value", merged[TagField.Album] == new TagFieldState("One", false), merged[TagField.Album].ToString());
            check("files that differ show empty and say so", merged[TagField.Title] == new TagFieldState("", true), merged[TagField.Title].ToString());
            check("a field none of them has is empty, not different", merged[TagField.Genre] == new TagFieldState("", false), null);
            check("an unreadable file does not make a field differ", !merged[TagField.Year].Differs, null);

            check("year 2024 parses", TagFields.TryParseNumber(TagField.Year, "2024", out var y) && y == 2024, null);
            check("year 12345 is refused", !TagFields.TryParseNumber(TagField.Year, "12345", out _), null);
            check("a track number with letters is refused", !TagFields.TryParseNumber(TagField.Track, "3a", out _), null);
            check("a negative track is refused", !TagFields.TryParseNumber(TagField.Track, "-1", out _), null);
            equal("a list splits on semicolons and trims", "A|B", string.Join("|", TagFields.SplitList(" A ; ;B ")));

            check("Ogg is refused by name", TagEditing.RefusalFor(@"C:\x\song.ogg", null, null) == TagEditing.OggRefusal, null);
            check("Opus is refused by name", TagEditing.RefusalFor(@"C:\x\song.OPUS", null, null) == TagEditing.OggRefusal, null);
            check("Drive is refused first", TagEditing.RefusalFor(@"G:\song.mp3", p => p.StartsWith("G:"), null) == TagEditing.DriveRefusal, null);
            check("the playing file is refused", TagEditing.RefusalFor(@"C:\x\song.mp3", null, _ => true) == TagEditing.PlayingRefusal, null);
            check("an MP3 is worth trying", TagEditing.RefusalFor(@"C:\x\song.mp3", _ => false, _ => false) == null, null);

            equal("one file saved", "Tags saved", TagEditing.Summary(new[] { new TagResult(@"C:\a.mp3", null) }));
            equal("many files saved", "Tags saved for 3 files", TagEditing.Summary(new[]
                { new TagResult(@"C:\a.mp3", null), new TagResult(@"C:\b.mp3", null), new TagResult(@"C:\c.mp3", null) }));
            equal("some failed", "Saved for 2 files, 1 failed: c.ogg, no.", TagEditing.Summary(new[]
                { new TagResult(@"C:\a.mp3", null), new TagResult(@"C:\b.mp3", null), new TagResult(@"C:\c.ogg", "no.") }));
            equal("one saved, one failed", "Saved for 1 file, 1 failed: c.ogg, no.", TagEditing.Summary(new[]
                { new TagResult(@"C:\a.mp3", null), new TagResult(@"C:\c.ogg", "no.") }));
            equal("one file failed", "Tags not saved: c.ogg, no.", TagEditing.Summary(new[] { new TagResult(@"C:\c.ogg", "no.") }));
            equal("all failed", "Tags not saved for any of the 2 files: a.ogg, no. b.ogg, no.", TagEditing.Summary(new[]
                { new TagResult(@"C:\a.ogg", "no."), new TagResult(@"C:\b.ogg", "no.") }));
        }

        private static short[] Tone(int rate, double seconds)
        {
            int frames = (int)(rate * seconds);
            var pcm = new short[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                var v = (short)(0.4 * short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / rate));
                pcm[i * 2] = v;
                pcm[i * 2 + 1] = v;
            }
            return pcm;
        }

        private static readonly Dictionary<TagField, string> Everything = new()
        {
            [TagField.Title] = "Tagged Title",
            [TagField.Artist] = "First Artist; Second Artist",
            [TagField.AlbumArtist] = "The Album Artist",
            [TagField.Album] = "Tagged Album",
            [TagField.Composer] = "A Composer",
            [TagField.Genre] = "Jazz",
            [TagField.Year] = "1987",
            [TagField.Track] = "7",
        };

        private static void FileTests(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-tags-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var pcm = Tone(44100, 2);
                var made = new List<(string Name, string Path)>();

                var mp3 = Path.Combine(dir, "tone.mp3");
                if (ExactSeekTests.MfEncoder.Write(mp3, pcm, 44100, ExactSeekTests.MfEncoder.Mp3, out var why)) made.Add(("MP3", mp3));
                else Console.WriteLine($"  (MP3 could not be encoded here — {why})");

                var m4a = Path.Combine(dir, "tone.m4a");
                if (ExactSeekTests.MfEncoder.Write(m4a, pcm, 44100, ExactSeekTests.MfEncoder.Aac, out why)) made.Add(("M4A", m4a));
                else Console.WriteLine($"  (M4A could not be encoded here — {why})");

                var flac = Path.Combine(dir, "tone.flac");
                ExactSeekTests.WriteFlac(flac, pcm, 44100);
                made.Add(("FLAC", flac));

                var writable = new List<string>();
                foreach (var (name, path) in made)
                {
                    var error = AudioTags.WriteFields(path, Everything);
                    if (error != null)
                    {
                        Console.WriteLine($"  ({name}: Windows would not write tags here — {error})");
                        continue;
                    }
                    writable.Add(name);

                    var back = AudioTags.ReadFields(path);
                    check($"{name}: tags read back", back != null, null);
                    if (back == null) continue;
                    foreach (var field in TagFields.All)
                        equal($"{name}: {TagFields.Label(field)} round-trips", Everything[field], back[field]);

                    // A blank field removes that tag and leaves the rest.
                    error = AudioTags.WriteFields(path, new Dictionary<TagField, string> { [TagField.Genre] = "" });
                    check($"{name}: clearing a tag works", error == null, error);
                    back = AudioTags.ReadFields(path);
                    equal($"{name}: the cleared tag is gone", "", back?[TagField.Genre] ?? "(unreadable)");
                    equal($"{name}: the others stay", "Tagged Album", back?[TagField.Album] ?? "(unreadable)");

                    check($"{name}: the file still names itself", File.Exists(path), null);
                }
                Console.WriteLine("  (writable here: " + (writable.Count == 0 ? "none" : string.Join(", ", writable)) + ")");
                check("at least one format can be tagged on this machine", writable.Count > 0, null);

                // Several files: only the fields that were edited are written.
                var kind = made.FirstOrDefault(m => writable.Contains(m.Name));
                if (kind.Path != null)
                {
                    var ext = Path.GetExtension(kind.Path);
                    var first = Path.Combine(dir, "first" + ext);
                    var second = Path.Combine(dir, "second" + ext);
                    File.Copy(kind.Path, first);
                    File.Copy(kind.Path, second);
                    AudioTags.WriteFields(first, new Dictionary<TagField, string> { [TagField.Title] = "Song One", [TagField.Track] = "9" });
                    AudioTags.WriteFields(second, new Dictionary<TagField, string> { [TagField.Title] = "Song Two", [TagField.Track] = "4" });

                    var shown = TagEditing.Merge(new[] { AudioTags.ReadFields(first), AudioTags.ReadFields(second) });
                    check($"{kind.Name}: two titles show as different", shown[TagField.Title].Differs, null);
                    equal($"{kind.Name}: a shared album shows its value", "Tagged Album", shown[TagField.Album].Value);

                    var results = TagEditing.Apply(new[] { first, second },
                        new Dictionary<TagField, string> { [TagField.Album] = "New Album" },
                        numberInOrder: true, onDrive: null, isPlaying: null);
                    equal($"{kind.Name}: both saved", "Tags saved for 2 files", TagEditing.Summary(results));

                    var one = AudioTags.ReadFields(first);
                    var two = AudioTags.ReadFields(second);
                    equal($"{kind.Name}: the edited album reached the first", "New Album", one?[TagField.Album] ?? "");
                    equal($"{kind.Name}: the edited album reached the second", "New Album", two?[TagField.Album] ?? "");
                    equal($"{kind.Name}: an unedited title is left alone (first)", "Song One", one?[TagField.Title] ?? "");
                    equal($"{kind.Name}: an unedited title is left alone (second)", "Song Two", two?[TagField.Title] ?? "");
                    equal($"{kind.Name}: an unedited artist is left alone", "First Artist; Second Artist", two?[TagField.Artist] ?? "");
                    equal($"{kind.Name}: numbered in list order (first)", "1", one?[TagField.Track] ?? "");
                    equal($"{kind.Name}: numbered in list order (second)", "2", two?[TagField.Track] ?? "");

                    // The playing file is refused and not touched; the rest go ahead.
                    results = TagEditing.Apply(new[] { first, second },
                        new Dictionary<TagField, string> { [TagField.Genre] = "Rock" },
                        numberInOrder: false, onDrive: null,
                        isPlaying: p => string.Equals(p, second, StringComparison.OrdinalIgnoreCase));
                    equal($"{kind.Name}: the playing file is reported",
                        $"Saved for 1 file, 1 failed: second{ext}, {TagEditing.PlayingRefusal}", TagEditing.Summary(results));
                    equal($"{kind.Name}: the playing file was not written", "", AudioTags.ReadFields(second)?[TagField.Genre] ?? "");
                }

                // Ogg Opus: refused in a sentence, and the file is not changed.
                var opus = Path.Combine(dir, "tone.opus");
                if (ExactSeekTests.WriteOpus(opus, Tone(48000, 1)))
                {
                    var before = File.ReadAllBytes(opus);
                    var results = TagEditing.Apply(new[] { opus }, new Dictionary<TagField, string> { [TagField.Title] = "x" },
                        false, null, null);
                    equal("Opus is refused in a sentence", "Tags not saved: tone.opus, " + TagEditing.OggRefusal,
                        TagEditing.Summary(results));
                    check("the Opus file is untouched", before.AsSpan().SequenceEqual(File.ReadAllBytes(opus)), null);
                }

                // A file type Windows has no tag writer for answers in words, not silence.
                var text = Path.Combine(dir, "notes.xyz");
                File.WriteAllText(text, "not audio");
                var refused = AudioTags.WriteFields(text, new Dictionary<TagField, string> { [TagField.Title] = "x" });
                check("an unwritable type is refused in a sentence", refused != null && refused.EndsWith("."), refused ?? "(it said it worked)");
                Console.WriteLine($"  (an unknown type says: {refused})");

                // A bad number is a sentence, and nothing is written.
                if (kind.Path != null)
                {
                    var bad = AudioTags.WriteFields(kind.Path, new Dictionary<TagField, string>
                        { [TagField.Title] = "Should Not Land", [TagField.Year] = "next year" });
                    equal("a year that is not a number is refused", "Year must be a number.", bad ?? "(it said it worked)");
                    check("and the title with it was not written",
                        AudioTags.ReadFields(kind.Path)?[TagField.Title] != "Should Not Land", null);
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
