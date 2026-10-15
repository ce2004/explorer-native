using System;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Regression tests for the 3.0.8 sweep of the web app (app.js and ConnectServer). The app.js checks read the
    /// embedded script, as the phone gets it, and pin the decision: rows rebuilt under focus give it back, the
    /// phone waits at least as long as the PC's own budgets, Back to the drives lands on the drive.
    /// </summary>
    internal static class Sweep308WebTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        public static Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            Console.WriteLine("Web app sweep (3.0.8):");

            var js = WebAssets.TryGet("/app/app.js", out var bytes, out _) ? Encoding.UTF8.GetString(bytes) : "";
            Check("app.js is embedded", js.Length > 1000);

            TransfersKeepFocusTests(js);
            FilesKeepFocusTests(js);
            QueueKeepsFocusTests(js);
            TimeoutsCoverServerBudgetsTests(js);
            BackToDrivesLandsOnDriveTests(js);
            GatewayTimeoutReasonTests();
            return Task.CompletedTask;
        }

        /// <summary>A function of app.js, from its declaration to the next top-level one.</summary>
        private static string Function(string js, string name)
        {
            var m = Regex.Match(js, @"^(async )?function " + Regex.Escape(name) + @"\(", RegexOptions.Multiline);
            if (!m.Success) return "";
            var next = Regex.Match(js[(m.Index + m.Length)..], @"^(async )?function |^const |^let |^// -", RegexOptions.Multiline);
            return next.Success ? js.Substring(m.Index, m.Length + next.Index) : js[m.Index..];
        }

        /// <summary>
        /// The Transfers list was rebuilt from nothing every second while anything moved, so a Stop button that
        /// had focus was thrown away under the screen reader and could not be pressed. Rows are now kept by
        /// transfer and changed in place.
        /// </summary>
        private static void TransfersKeepFocusTests(string js)
        {
            var body = Function(js, "renderTransfers");
            Check("renderTransfers is there", body.Length > 0);
            Check("transfer rows are not rebuilt wholesale", !body.Contains("replaceChildren(frag)"), body.Length > 0 ? null : "missing");
            Check("transfer rows are kept by their transfer", body.Contains("dataset.key") && body.Contains("t.key"));
            Check("a row's text changes only when it differs", body.Contains("setText("));
        }

        /// <summary>
        /// Copy, Move and Get size from a row's More dialog, and a finished upload's refresh, rebuilt the file
        /// list and dropped focus to the top of the page. The same row's same control gets it back.
        /// </summary>
        private static void FilesKeepFocusTests(string js)
        {
            var body = Function(js, "renderFiles");
            Check("renderFiles notes what had focus before rebuilding", body.Contains("document.activeElement") &&
                  body.IndexOf("document.activeElement", StringComparison.Ordinal) < body.IndexOf("replaceChildren", StringComparison.Ordinal));
            Check("and gives it back after", Regex.IsMatch(body, @"replaceChildren\(frag\);[\s\S]*\.focus\(\)"));
            Check("only within the same folder", js.Contains("filesShownFor"));
        }

        /// <summary>Tapping a track in the queue rebuilt the queue and threw away the button that was tapped.</summary>
        private static void QueueKeepsFocusTests(string js)
        {
            var body = Function(js, "renderQueue");
            Check("renderQueue gives focus back after rebuilding",
                  body.Contains("document.activeElement") && Regex.IsMatch(body, @"replaceChildren\(frag\);[\s\S]*\.focus\(\)"));
        }

        /// <summary>The timeout an api() call on <paramref name="route"/> passes, or 20000 (the default) when none.</summary>
        private static int TimeoutFor(string js, string route)
        {
            int at = js.IndexOf("'" + route + "'", StringComparison.Ordinal);
            if (at < 0) return -1;
            int end = js.IndexOf('\n', at);
            var line = js[at..(end < 0 ? js.Length : end)];
            var m = Regex.Match(line, @"timeout:\s*(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : 20000;
        }

        /// <summary>
        /// The phone gave up after 20 seconds on requests the PC is allowed longer for (a folder size: 20 seconds
        /// locally, a minute on Drive; a slow folder listing: 45 seconds; trashing many Drive items), and then
        /// said the PC could not be reached, or showed the offline screen, while the PC was fine.
        /// </summary>
        private static void TimeoutsCoverServerBudgetsTests(string js)
        {
            int size = TimeoutFor(js, "/api/size");
            Check("Get size waits longer than Drive's minute", size > (int)ConnectFiles.DriveSizeBudget.TotalMilliseconds, size.ToString());
            Check("and longer than the local walk", size > ConnectFiles.SizeBudgetSeconds * 1000, size.ToString());
            int list = TimeoutFor(js, "/api/list");
            Check("opening a folder waits longer than the PC's 45 seconds", list > 45000, list.ToString());
            int delete = TimeoutFor(js, "/api/delete");
            Check("a delete waits longer than the usual 20 seconds", delete > 20000, delete.ToString());
        }

        /// <summary>
        /// The phone's own Back gesture from a drive's top folder looked for a row called "C:" in the drive list,
        /// whose rows are called "C:\", so focus went to the heading instead of the drive, unlike the Back button.
        /// </summary>
        private static void BackToDrivesLandsOnDriveTests(string js)
        {
            int at = js.IndexOf("'popstate'", StringComparison.Ordinal);
            var handler = at < 0 ? "" : js.Substring(at, Math.Min(600, js.Length - at));
            Check("Back to the drives names the drive with its backslash",
                  handler.Contains("e.state.path == null") && handler.Contains(@"[A-Za-z]:\\"), handler.Length > 0 ? null : "no popstate handler");
        }

        /// <summary>The details route answers 504 when even the basics take too long; its status line said "Internal Server Error".</summary>
        private static void GatewayTimeoutReasonTests()
        {
            var reason = typeof(ConnectServer).GetMethod("Reason", BindingFlags.NonPublic | BindingFlags.Static);
            var said = reason?.Invoke(null, new object[] { 504 }) as string;
            Check("a 504 is called Gateway Timeout", said == "Gateway Timeout", said ?? "no Reason method");
            var ok = reason?.Invoke(null, new object[] { 200 }) as string;
            Check("and a 200 is still OK", ok == "OK", ok);
        }
    }
}
