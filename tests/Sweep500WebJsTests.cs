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
    }
}
