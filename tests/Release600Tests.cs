using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// 6.0.0: the review of everything added since 5.1.0 (internet radio, the
    /// AI describer, finishing copies after a restart, resume). One check or
    /// more per bug fixed. Group: release600.
    /// </summary>
    internal static class Release600Tests
    {
        public static Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            Radio(check, equal);
            Ai(check, equal);
            FinishLater(check, equal);
            Resume(check);
            SyncDelete(check);
            Wiring(check);
            return Task.CompletedTask;
        }

        private static void Radio(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            const string pls = "[playlist]\nFile1=http://example.com/live\nTitle1=Ünïcode FM\n";
            var le = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(pls)).ToArray();
            var be = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(pls)).ToArray();
            var utf8 = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(pls)).ToArray();
            equal("600 radio: a UTF-16 playlist file is read", pls, StreamPlaylists.Decode(le));
            equal("600 radio: big-endian too", pls, StreamPlaylists.Decode(be));
            equal("600 radio: a UTF-8 mark is not part of the text", pls, StreamPlaylists.Decode(utf8));
            var entries = StreamPlaylists.WebEntries(StreamPlaylists.Parse(StreamPlaylists.Decode(le), null));
            check("600 radio: and its station is found", entries.Count == 1 && entries[0].Url == "http://example.com/live",
                entries.Count.ToString());

            // A table packet whose pointer runs past the packet: ignored, not thrown.
            foreach (int pid in new[] { 0, 0x100 })
            {
                var demux = new RadioStream.TsAudio();
                string threw = "";
                try
                {
                    if (pid != 0)
                    {
                        // A table saying where the programme's own table is, first.
                        var pat = new byte[188];
                        Array.Fill(pat, (byte)0xFF);
                        new byte[] { 0x47, 0x40, 0x00, 0x10, 0x00, 0x00, 0xB0, 0x0D, 0, 1, 0xC1, 0, 0, 0, 1, 0xE1, 0x00, 0, 0, 0, 0 }.CopyTo(pat, 0);
                        demux.Feed(pat, new MemoryStream());
                    }
                    var bad = new byte[188];
                    bad[0] = 0x47; bad[1] = (byte)(0x40 | (pid >> 8)); bad[2] = (byte)pid; bad[3] = 0x10; bad[4] = 0xFF;
                    demux.Feed(bad, new MemoryStream());
                }
                catch (Exception ex) { threw = ex.GetType().Name; }
                equal($"600 radio: a damaged table packet (pid {pid}) does not end the connection", "", threw);
            }

            var radio = Source("RadioStream.cs");
            check("600 radio: one missing HLS segment is stepped over", radio.Contains("catch (StreamGone) when (!list.EndList && ++gone <= GoneSegmentsMost)", StringComparison.Ordinal), null);
            check("600 radio: a reconnect goes on from the last segment played", radio.Contains("long last = _hlsLast;", StringComparison.Ordinal), null);
            check("600 radio: a stalled segment download is timed", radio.Contains("stall.CancelAfter(StallMilliseconds)", StringComparison.Ordinal), null);
            check("600 radio: an empty title takes the old song away", radio.Contains("_song = song.Length > 0 ? song : null", StringComparison.Ordinal), null);
        }

        private static void Ai(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var limit = AiDescriber.Failure(AiProvider.Gemini, "gemini-2.5-flash", 429,
                "{\"error\":{\"code\":429,\"message\":\"You exceeded your current quota, please check your plan and billing details.\",\"status\":\"RESOURCE_EXHAUSTED\"}}");
            check("600 ai: a per-minute limit is not called out of credit", !limit.OutOfCredit && limit.Message.Contains("too many requests", StringComparison.Ordinal), limit.Message);
            var broke = AiDescriber.Failure(AiProvider.OpenAI, "gpt-4o", 429,
                "{\"error\":{\"message\":\"You exceeded your current quota, please check your plan and billing details.\",\"type\":\"insufficient_quota\",\"code\":\"insufficient_quota\"}}");
            check("600 ai: an account with no credit still is", broke.OutOfCredit, broke.Message);

            var picture = new AiPicture(new byte[] { 1, 2, 3 }, "image/png", 1, 1);
            var turns = new List<AiTurn> { new AiTurn(true, "Describe it"), new AiTurn(false, "A cat"), new AiTurn(true, "What colour?") };
            var gemma = AiDescriber.GeminiBody("SYSTEM", picture, turns, "gemma-3-27b-it");
            check("600 ai: Gemma gets no system instruction", gemma["system_instruction"] == null, null);
            var firstText = gemma["contents"]?[0]?["parts"]?.AsArray().Last()?["text"]?.GetValue<string>() ?? "";
            check("600 ai: its instructions lead the first message instead", firstText.StartsWith("SYSTEM", StringComparison.Ordinal) && firstText.EndsWith("Describe it", StringComparison.Ordinal), firstText);
            var later = gemma["contents"]?[2]?["parts"]?[0]?["text"]?.GetValue<string>() ?? "";
            equal("600 ai: later messages are as typed", "What colour?", later);
            var gemini = AiDescriber.GeminiBody("SYSTEM", picture, turns, "gemini-2.5-flash");
            check("600 ai: Gemini keeps its system instruction", gemini["system_instruction"] != null, null);

            var models = AiDescriber.OpenAiModels(JsonNode.Parse(
                "{\"data\":[{\"id\":\"gpt-4o\"},{\"id\":\"o1-mini\"},{\"id\":\"o1-preview\"},{\"id\":\"o3-mini\"},{\"id\":\"o3\"}]}"));
            equal("600 ai: text-only OpenAI models are not offered", "gpt-4o,o3", string.Join(",", models.Select(m => m.Id).OrderBy(x => x, StringComparer.Ordinal)));

            try
            {
                AiDescriber.RememberOpenRouterPrices(JsonNode.Parse(
                    "{\"data\":[" +
                    "{\"id\":\"openai/gpt-4o-2024-05-13\",\"pricing\":{\"prompt\":\"0.000005\",\"completion\":\"0.000015\"}}," +
                    "{\"id\":\"openai/gpt-4o\",\"pricing\":{\"prompt\":\"0.0000025\",\"completion\":\"0.00001\"}}," +
                    "{\"id\":\"openai/gpt-4o-2024-11-20\",\"pricing\":{\"prompt\":\"0.0000025\",\"completion\":\"0.00001\"}}]}"));
                var plain = AiDescriber.PriceNow(AiProvider.OpenAI, "gpt-4o");
                var dated = AiDescriber.PriceNow(AiProvider.OpenAI, "gpt-4o-2024-05-13");
                check("600 ai: the undated model has its own price whatever came first", plain?.Input == 0.0000025m, plain?.Input.ToString());
                check("600 ai: a dated snapshot keeps its price", dated?.Input == 0.000005m, dated?.Input.ToString());
            }
            finally { AiDescriber.ForgetPricesForTests(); }

            var form = Source("AiDescriberForm.cs");
            check("600 ai: the answer does not wait for the price list", !form.Contains("await _price", StringComparison.Ordinal), null);
            check("600 ai: looking at the picture is said once", form.Contains("if (!(firstLook && _saidLooking))", StringComparison.Ordinal), null);
            var setup = Source("WebView2Setup.cs");
            check("600 webview2: Cancel as the download ends still cancels",
                setup.IndexOf("window.Token.ThrowIfCancellationRequested();", StringComparison.Ordinal) is var at && at > 0 &&
                at < setup.IndexOf("window.InstallingNow();", StringComparison.Ordinal), null);
            check("600 webview2: a stalled download is timed", setup.Contains("limit.CancelAfter(TimeSpan.FromMinutes(5))", StringComparison.Ordinal), null);
        }

        private static void FinishLater(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var now = DateTime.UtcNow;
            var jobs = new[]
            {
                new CarryOnTransfers.Job(CarryOnTransfers.Kind.Copy, new[] { @"C:\a\one", @"C:\a\two" }, @"D:\b", "Overwrite", new[] { @"C:\a\two" }),
                new CarryOnTransfers.Job(CarryOnTransfers.Kind.Move, new[] { @"C:\a\x" }, @"D:\c", "AutoRename", Array.Empty<string>()),
                new CarryOnTransfers.Job(CarryOnTransfers.Kind.Copy, new[] { @"C:\a\y" }, @"G:\d", "Skip", null),
                new CarryOnTransfers.Job(CarryOnTransfers.Kind.Copy, new[] { @"C:\a\z" }, @"D:\e", CarryOnTransfers.NotChosen),
            };
            var back = CarryOnTransfers.Decode(CarryOnTransfers.Encode(jobs, now), now);
            check("600 finish later: every job comes back", back.Count == 4, back.Count.ToString());
            if (back.Count == 4)
            {
                check("600 finish later: with its answer for names already there", back[0].Policy == "Overwrite" && back[1].Policy == "AutoRename" && back[3].Policy == CarryOnTransfers.NotChosen, null);
                check("600 finish later: and which names clashed", back[0].Clashed is { Length: 1 } c && c[0] == @"C:\a\two", null);
                check("600 finish later: nothing clashed is not the same as not known", back[1].Clashed is { Length: 0 } && back[2].Clashed == null, null);
            }
            var old = CarryOnTransfers.Decode(now.Ticks + "\nJ\t0\tD:\\b\nP\tC:\\a\\one\n", now);
            check("600 finish later: a note from 5.6 still reads", old.Count == 1 && old[0].Policy == "" && old[0].Clashed == null, null);

            static string Shape((List<CarryOnTransfers.Run> Runs, string[] LeftOut) plan) =>
                string.Join(" | ", plan.Runs.Select(r => (r.Forced?.ToString() ?? "ask") + ":" + string.Join(",", r.Paths.Select(Path.GetFileName)))) +
                (plan.LeftOut.Length > 0 ? " | left:" + string.Join(",", plan.LeftOut.Select(Path.GetFileName)) : "");
            var both = new[] { @"C:\a\one", @"C:\a\two" };
            CarryOnTransfers.Job Job(string policy, string[]? clashed) =>
                new(CarryOnTransfers.Kind.Copy, both, @"D:\b", policy, clashed);

            equal("600 finish later: Replace still replaces what clashed, and fills in the rest",
                "FillGaps:one | Overwrite:two", Shape(CarryOnTransfers.Plan(Job("Overwrite", new[] { @"C:\a\two" }), both)));
            equal("600 finish later: Skip leaves what clashed alone",
                "FillGaps:one", Shape(CarryOnTransfers.Plan(Job("Skip", new[] { @"C:\a\two" }), both)));
            equal("600 finish later: a second copy is never merged into the folder already there",
                "FillGaps:one | left:two", Shape(CarryOnTransfers.Plan(Job("AutoRename", new[] { @"C:\A\TWO" }), both)));
            equal("600 finish later: keep both with nothing clashing fills in what is missing",
                "FillGaps:one,two", Shape(CarryOnTransfers.Plan(Job("AutoRename", Array.Empty<string>()), both)));
            equal("600 finish later: fill in what is missing stays that",
                "FillGaps:one,two", Shape(CarryOnTransfers.Plan(Job("FillGaps", null), both)));
            equal("600 finish later: clashes not known, Replace replaces all",
                "Overwrite:one,two", Shape(CarryOnTransfers.Plan(Job("Overwrite", null), both)));
            equal("600 finish later: clashes not known, nothing is guessed for keep both",
                " | left:one,two", Shape(CarryOnTransfers.Plan(Job("AutoRename", null), both)));
            equal("600 finish later: stopped before it was answered, it asks as any paste does",
                "ask:one,two", Shape(CarryOnTransfers.Plan(Job(CarryOnTransfers.NotChosen, null), both)));
            equal("600 finish later: a 5.6 note finishes as 5.6 did",
                "FillGaps:one,two", Shape(CarryOnTransfers.Plan(Job("", null), both)));
            equal("600 finish later: only what is still there",
                "FillGaps:one", Shape(CarryOnTransfers.Plan(Job("Overwrite", new[] { @"C:\a\two" }), new[] { @"C:\a\one" })));

            var saved = Settings.OverrideAppDataDir;
            var dir = Path.Combine(Path.GetTempPath(), "en600-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                Settings.OverrideAppDataDir = dir;
                CarryOnTransfers.Save(jobs);
                check("600 finish later: a fresh note is waiting", CarryOnTransfers.Waiting(), null);
                File.SetLastWriteTimeUtc(CarryOnTransfers.NotePath, DateTime.UtcNow - TimeSpan.FromHours(1));
                check("600 finish later: a stale note does not open the window", !CarryOnTransfers.Waiting(), null);
                check("600 finish later: and is gone", !File.Exists(CarryOnTransfers.NotePath), null);
            }
            finally
            {
                Settings.OverrideAppDataDir = saved;
                try { Directory.Delete(dir, true); } catch { }
            }

            var main = Source("MainForm.cs");
            check("600 finish later: the finishing follows the plan", main.Contains("CarryOnTransfers.Plan(job, sources)", StringComparison.Ordinal), null);
            check("600 finish later: the answer is written down once known", main.Contains("Policy = conflicts.ToString(), Clashed = clashedSources", StringComparison.Ordinal), null);
            check("600 finish later: a share that gives no answer is waited for, not left out", main.Contains("LookForCarryOn(job)", StringComparison.Ordinal) && !main.Contains("job.Paths.Where(PathAnswers)", StringComparison.Ordinal), null);
            check("600 finish later: an update that fails finishes them here", System.Text.RegularExpressions.Regex.Matches(main, @"_ = CarryOnHereAsync\(\);").Count >= 3, null);
            check("600 finish later: what was agreed is the operations, not a count", main.Contains("_carryable.Keys.All(_carryOnAgreed.Contains)", StringComparison.Ordinal), null);
            check("600 finish later: a paste still at its question does not start under the restart", main.Contains("if (_carryStopped.Contains(carryId)) return false;", StringComparison.Ordinal), null);
        }

        private static void Resume(Action<string, bool, string?> check)
        {
            var now = DateTime.UtcNow;
            var s = new Settings { ResumeAfterRestart = true, ResumeAfterRestartAt = now.AddMinutes(-2).Ticks };
            check("600 resume: a restart two minutes ago comes back playing", s.ResumeAfterRestartStands(now), null);
            s.ResumeAfterRestartAt = now.AddDays(-3).Ticks;
            check("600 resume: one closed three days ago does not", !s.ResumeAfterRestartStands(now), null);
            s.ResumeAfterRestartAt = 0;
            check("600 resume: an update from 5.6, which wrote no time, still does", s.ResumeAfterRestartStands(now), null);
            s.ResumeAfterRestart = false;
            s.ResumeAfterRestartAt = now.Ticks;
            check("600 resume: not asked for, not done", !s.ResumeAfterRestartStands(now), null);

            var tray = Source("TrayApplicationContext.cs");
            check("600 resume: anything chosen since the start wins", System.Text.RegularExpressions.Regex.Matches(tray, @"Volatile\.Read\(ref _playRequest\) != asked").Count >= 2, null);
            check("600 radio: play on a stopped station connects off the window's thread", tray.Contains("action == AudioAction.PlayPause && _audio.LiveEnded", StringComparison.Ordinal), null);
            var player = Source("AudioPlayer.cs");
            check("600 radio: Stop lets go of a station still connecting", player.Contains("if (openingRadio) ForgetRadio();", StringComparison.Ordinal), null);
        }

        private static void SyncDelete(Action<string, bool, string?> check)
        {
            var dir = Path.Combine(Path.GetTempPath(), "en600d-" + Guid.NewGuid().ToString("N"));
            try
            {
                var folder = Path.Combine(dir, "report.txt");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "inside.txt"), "kept");
                var said = DriveMonitor.DeleteForGood(folder);
                check("600 sync: a folder that took a deleted file's name is never deleted for good", said != null && File.Exists(Path.Combine(folder, "inside.txt")), said);
                var file = Path.Combine(dir, "gone.txt");
                File.WriteAllText(file, "x");
                File.SetAttributes(file, FileAttributes.ReadOnly);
                check("600 sync: a file still is, read-only or not", DriveMonitor.DeleteForGood(file) == null && !File.Exists(file), null);
                check("600 sync: one already gone is fine", DriveMonitor.DeleteForGood(file) == null, null);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static void Wiring(Action<string, bool, string?> check)
        {
            var radio = Source("RadioStream.cs");
            int read = radio.IndexOf("StreamPlaylists.Decode(response.ReadAll(", StringComparison.Ordinal);
            int let = radio.IndexOf("response.Dispose();", StringComparison.Ordinal);
            int run = radio.IndexOf("RunHls(from, text)", StringComparison.Ordinal);
            check("600 radio: a playlist's connection is let go before its station plays", read > 0 && let > read && run > let, null);
        }

        private static string Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            try { return dir == null ? "" : File.ReadAllText(Path.Combine(dir.FullName, "src", name)); }
            catch { return ""; }
        }
    }
}
