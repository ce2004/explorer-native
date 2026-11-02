using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of the web app's page. There is no JS runner, so these read web/app.js and pin the decisions.
    /// </summary>
    internal static class Sweep500WebJsTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the web app's page:");
            var js = WebAssets.TryGet("/app/app.js", out var bytes, out _) ? Encoding.UTF8.GetString(bytes) : "";
            Check("app.js is embedded", js.Length > 1000);

            SlowDeleteRefreshesTests(js);
            MoveHereKeepsFocusTests(js);
            UploadServerErrorEndsTests(js);
            StopAllStopsTheBatchTests(js);
            UnplayableTrackIsSkippedTests(js);
            StopWhileWaitingStaysStoppedTests(js);
            ClipboardSendScalesWithSizeTests(js);
            await Task.CompletedTask;
        }

        /// <summary>A function of app.js, from its declaration to the next top-level one.</summary>
        private static string Function(string js, string name)
        {
            var m = Regex.Match(js, @"^(async )?function " + Regex.Escape(name) + @"\(", RegexOptions.Multiline);
            if (!m.Success) return "";
            var next = Regex.Match(js[(m.Index + m.Length)..], @"^(async )?function |^const |^let |^// -", RegexOptions.Multiline);
            return next.Success ? js.Substring(m.Index, m.Length + next.Index) : js[m.Index..];
        }

        /// <summary>Text from the first occurrence of <paramref name="from"/> on, or empty.</summary>
        private static string After(string text, string from)
        {
            int at = text.IndexOf(from, StringComparison.Ordinal);
            return at < 0 ? "" : text[at..];
        }

        // Bug 1: a big Drive delete outlasted the fixed two-minute wait, then said the PC could not be reached
        // and left the deleted rows showing.
        private static void SlowDeleteRefreshesTests(string js)
        {
            var body = Function(js, "remove");
            var line = Regex.Match(body, @"api\('/api/delete'[^\n]*").Value;
            Check("a delete's wait grows with how many items it deletes", Regex.IsMatch(line, @"timeout:[^}\n]*paths\.length"), line);
            var caught = After(body, "catch (err)");
            Check("a delete with no answer is told apart from a refusal", Regex.IsMatch(caught, @"status\s*!==?\s*0|status\s*===?\s*0"), caught);
            Check("a delete with no answer refreshes the folder", caught.Contains("openFolder("), caught);
            Check("and leaves select mode", caught.Contains("selectMode = false"), caught);
        }

        // Bug 2: Move here hid the button that had focus, and focus fell to the page.
        private static void MoveHereKeepsFocusTests(string js)
        {
            var after = After(Function(js, "paste"), "state.clip = null");
            Check("after Move here, focus is put on something still shown",
                after.Contains(".focus()") && (after.Contains("btn-select") || after.Contains("files-title")), after);
        }

        // Bug 3: a chunk the PC kept refusing with a 500 (its disk full) was retried for ever as "waiting for the PC".
        private static void UploadServerErrorEndsTests(string js)
        {
            var chunk = After(Function(js, "upload"), "/api/upload/chunk");
            int wait = chunk.IndexOf("waitForNetwork(", StringComparison.Ordinal);
            var handling = wait < 0 ? "" : chunk[..wait];
            Check("a chunk refused by the PC ends the upload after a few tries",
                Regex.IsMatch(handling, @"\w+\s*>=?\s*\d") && handling.Contains("throw err"), handling);
            Check("only no answer at all is waited out without a limit", !Regex.IsMatch(handling, @"status\s*<\s*500\)\s*throw err"), handling);
            Check("an upload that cannot go on is called off on the PC", handling.Contains("/api/upload/cancel"), handling);
        }

        // Bug 4: Stop all stopped only the file going up; the rest of a multi-file upload went on.
        private static void StopAllStopsTheBatchTests(string js)
        {
            var many = Function(js, "uploadFiles");
            var loop = After(many, "for (const f of files)");
            int up = loop.IndexOf("await upload(", StringComparison.Ordinal);
            var beforeEach = up < 0 ? "" : loop[..up];
            var flag = Regex.Match(beforeEach, @"if \((\w+)\.stopped\)").Groups[1].Value;
            Check("a multi-file upload looks for Stop before each file", flag.Length > 0, beforeEach);
            var stop = After(js, "$('tr-stop').addEventListener");
            stop = stop.Length > 400 ? stop[..400] : stop;
            Check("Stop all marks the multi-file uploads stopped", Regex.IsMatch(stop, @"\.stopped = true"), stop);
        }

        // Bug 5: a track the PC refused or the browser could not decode was retried for half a minute, blamed on the
        // connection, and stopped the whole queue.
        private static void UnplayableTrackIsSkippedTests(string js)
        {
            var from = After(js, "a.addEventListener('error'");
            int end = from.IndexOf("a.addEventListener('playing'", StringComparison.Ordinal);
            var handler = end < 0 ? from : from[..end];
            // The skip helper is defined just above the handler, in the same setup.
            int start = js.IndexOf("a.addEventListener('error'", StringComparison.Ordinal);
            var setup = start < 0 ? "" : js[Math.Max(0, start - 600)..start] + handler;
            Check("a player error reads the audio element's error", handler.Contains("a.error"), handler);
            Check("a player error asks the PC what it says about the track", Regex.IsMatch(handler, @"\b(timedFetch|fetch)\(src"), handler);
            Check("a track that won't play is skipped and the queue goes on", setup.Contains("step(1)") && handler.Contains("skip("), setup);
            Check("a queue where nothing plays stops rather than loops", Regex.IsMatch(setup, @"\+\+\w+\s*>=?"), setup);
            var src = Function(js, "srcFor");
            Check("a track this browser can't play goes through the PC's decoder", src.Contains("viaPc") && src.Contains("/api/audio"), src);
        }

        // Bug 6: Stop pressed while a transfer waited for the network was undone when the network came back.
        private static void StopWhileWaitingStaysStoppedTests(string js)
        {
            foreach (var name in new[] { "downloadToPhone", "uploadSmall", "upload" })
            {
                var body = Function(js, name);
                int waits = 0, unchecked_ = 0;
                foreach (Match m in Regex.Matches(body, @"await waitForNetwork\("))
                {
                    waits++;
                    var rest = body[m.Index..];
                    int running = rest.IndexOf("t.state = 'running'", StringComparison.Ordinal);
                    if (running >= 0 && !rest[..running].Contains("if (stopped)")) unchecked_++;
                }
                Check($"{name}: Stop pressed while waiting for the network is looked at before it carries on",
                    waits > 0 && unchecked_ == 0, $"{waits} waits, {unchecked_} not checked");
            }
            var download = Function(js, "downloadToPhone");
            int safari = download.IndexOf("handToSafari(url", StringComparison.Ordinal);
            int fetched = download.IndexOf("await fetch(", StringComparison.Ordinal);
            Check("a big download stopped while it was asked for is not handed to Safari",
                safari > fetched && fetched >= 0 && download[fetched..safari].Contains("if (stopped)"), download);
        }

        // Bug 7: sending a file to the PC clipboard gave up after five minutes whatever its size, with no Stop.
        private static void ClipboardSendScalesWithSizeTests(string js)
        {
            var body = Function(js, "sendFiles");
            var call = After(body, "/api/clipboard/send?");
            int end = call.IndexOf("});", StringComparison.Ordinal);
            call = end < 0 ? call : call[..end];
            Check("a clipboard send's time limit grows with the file's size", Regex.IsMatch(call, @"timeout:[^,}]*\.size"), call);
            Check("a clipboard send can be stopped", call.Contains("signal:"), call);
            Check("a clipboard send shows in Transfers", body.Contains("addTransfer("), body);
        }
    }
}
