using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Three fixes from a review of what people meet first: Google Drive setup
    /// failures said as what is wrong and how to fix it (GoogleSetupErrors),
    /// an expired Apple Music sign-in said plainly and asked about once per
    /// sign-in, and speech through UI Automation when NVDA is not answering.
    /// Offline and quick. Group: userfixes.
    /// </summary>
    internal static class UserFixesTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;
            Console.WriteLine("Google setup errors, Apple Music sign-in expiry, speech without NVDA:");

            bool diagWas = Diag.Enabled;
            Diag.Enabled = false;   // nothing here may write the real errors.log
            try
            {
                GoogleTokenEndpointErrors();
                GoogleRedirectErrors();
                GoogleApiErrors();
                GoogleLeavesOthersAlone();
                GoogleWiring();
                AppleJudge();
                AppleOncePerSignIn();
                await AppleExpiredSaysSo();
                SpeechRoutes();
                SpeechUiaHost();
                SpeechWiring();
            }
            finally
            {
                Diag.Enabled = diagWas;
                AppleMusicClient.FakeForTests = null;
                AppleMusic.ResetForTests();
                Speech.UseUiaHost(null, null);
            }
            Console.WriteLine();
        }

        private static string Source(string name)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, "src", name);
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            return "";
        }

        /// <summary>A sentence fit to be read out: no raw code, no JSON, ends like a sentence.</summary>
        private static bool Plain(string? said) =>
            said != null && said.Length > 20 && said.EndsWith(".") && !said.Contains('{') && !said.Contains('_');

        // ---------------- Google: the token endpoint ----------------

        private static void GoogleTokenEndpointErrors()
        {
            // Bodies as oauth2.googleapis.com/token really sends them.
            var notFound = GoogleSetupErrors.FromBody(
                "{\n  \"error\": \"invalid_client\",\n  \"error_description\": \"The OAuth client was not found.\"\n}");
            Check("an unknown client ID is said as the ID being wrong", notFound?.Contains("can't find that client ID") == true, notFound);
            Check("and says where to copy it from", notFound?.Contains("APIs and services, Credentials") == true, notFound);

            var badSecret = GoogleSetupErrors.FromBody(
                "{\n  \"error\": \"invalid_client\",\n  \"error_description\": \"Unauthorized\"\n}");
            Check("a real ID with the wrong secret is said as the secret being wrong",
                badSecret?.Contains("secret doesn't match") == true, badSecret);

            var deleted = GoogleSetupErrors.FromBody(
                "{\n  \"error\": \"deleted_client\",\n  \"error_description\": \"The OAuth client was deleted.\"\n}");
            Check("a deleted client says to make a new Desktop app one",
                deleted?.Contains("deleted") == true && deleted.Contains("Desktop app"), deleted);

            var disabled = GoogleSetupErrors.FromBody("{\"error\":\"disabled_client\",\"error_description\":\"The OAuth client was disabled.\"}");
            Check("a client switched off says to turn it back on", disabled?.Contains("turned off") == true, disabled);

            var unauthorized = GoogleSetupErrors.FromBody(
                "{\n  \"error\": \"unauthorized_client\",\n  \"error_description\": \"Unauthorized\"\n}");
            Check("unauthorized_client says it has to be a Desktop app client",
                unauthorized?.Contains("type Desktop app") == true, unauthorized);

            var webClient = GoogleSetupErrors.FromBody(
                "{\n  \"error\": \"redirect_uri_mismatch\",\n  \"error_description\": \"Bad Request\"\n}");
            Check("a Web application client is said as the wrong type",
                webClient?.Contains("wrong type") == true && webClient.Contains("not Web application"), webClient);

            var grant = GoogleSetupErrors.FromBody("{\"error\":\"invalid_grant\",\"error_description\":\"Bad Request\"}");
            Check("an exchanged code that ran out says to connect again", grant?.Contains("Connect again") == true, grant);

            // The check before saving refuses only what proves the pair wrong.
            Check("the client check refuses invalid_client", GoogleSetupErrors.RefusesClient("invalid_client"));
            Check("and deleted_client", GoogleSetupErrors.RefusesClient("deleted_client"));
            Check("and redirect_uri_mismatch (a Web client)", GoogleSetupErrors.RefusesClient("redirect_uri_mismatch"));
            Check("but not invalid_grant, the right answer to a made-up code", !GoogleSetupErrors.RefusesClient("invalid_grant"));
            Check("nor nothing at all", !GoogleSetupErrors.RefusesClient(""));

            foreach (var said in new[] { notFound, badSecret, deleted, disabled, unauthorized, webClient, grant })
                Check("read out as a plain sentence: " + said, Plain(said), said);
        }

        // ---------------- Google: the browser's redirect ----------------

        private static void GoogleRedirectErrors()
        {
            // Google's wording on the "Access blocked" page for an account that
            // is not a test user, which comes as access_denied.
            var testUser = GoogleSetupErrors.ForSignIn("access_denied",
                "Explorer Native has not completed the Google verification process. The app is currently being " +
                "tested, and can only be accessed by developer-approved testers.");
            Check("not a test user is said as that", testUser?.Contains("isn't one of its test users") == true, testUser);
            Check("and says where to add one", testUser?.Contains("Google Auth platform, Audience") == true, testUser);

            var cancelled = GoogleSetupErrors.ForSignIn("access_denied");
            Check("a bare access_denied covers Cancel and the test-user case",
                cancelled?.Contains("pressed Cancel") == true && cancelled.Contains("test user"), cancelled);

            var org = GoogleSetupErrors.ForSignIn("org_internal", "This client is restricted to users within its organization.");
            Check("an Internal app says to make it External", org?.Contains("External") == true, org);

            var admin = GoogleSetupErrors.ForSignIn("admin_policy_enforced");
            Check("an administrator's block is said as one", admin?.Contains("administrator") == true, admin);

            Check("an unknown code is left to the caller", GoogleSetupErrors.ForSignIn("server_error") == null);

            foreach (var said in new[] { testUser, cancelled, org, admin })
                Check("read out as a plain sentence: " + said, Plain(said), said);
        }

        // ---------------- Google: the Drive API ----------------

        private static void GoogleApiErrors()
        {
            // drive/v3/about from a project whose Drive API was never switched on.
            const string apiOff = """
                {
                  "error": {
                    "code": 403,
                    "message": "Google Drive API has not been used in project 123456789012 before or it is disabled. Enable it by visiting https://console.developers.google.com/apis/api/drive.googleapis.com/overview?project=123456789012 then retry. If you enabled this API recently, wait a few minutes for the action to propagate to our systems and retry.",
                    "errors": [
                      {
                        "message": "Google Drive API has not been used in project 123456789012 before or it is disabled.",
                        "domain": "usageLimits",
                        "reason": "accessNotConfigured",
                        "extendedHelp": "https://console.developers.google.com"
                      }
                    ],
                    "status": "PERMISSION_DENIED",
                    "details": [
                      {
                        "@type": "type.googleapis.com/google.rpc.ErrorInfo",
                        "reason": "SERVICE_DISABLED",
                        "domain": "googleapis.com",
                        "metadata": { "service": "drive.googleapis.com", "consumer": "projects/123456789012" }
                      }
                    ]
                  }
                }
                """;
            Equal("the Drive API switched off says how to switch it on", GoogleSetupErrors.DriveApiOff,
                GoogleSetupErrors.FromApiBody(apiOff) ?? "(nothing)");

            // The newer shape alone: no errors array, the reason only in details.
            const string detailsOnly =
                "{\"error\":{\"code\":403,\"message\":\"Drive API is disabled.\",\"status\":\"PERMISSION_DENIED\"," +
                "\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"SERVICE_DISABLED\"}]}}";
            Equal("and so does the newer shape, with SERVICE_DISABLED only in details", GoogleSetupErrors.DriveApiOff,
                GoogleSetupErrors.FromApiBody(detailsOnly) ?? "(nothing)");

            Equal("DriveClient.Explain says the same sentence, from the one place", GoogleSetupErrors.DriveApiOff,
                DriveClient.Explain(403, apiOff));
            Check("still telling you to press Enable", DriveClient.Explain(403,
                "{\"error\":{\"errors\":[{\"reason\":\"accessNotConfigured\"}]}}").Contains("press Enable"));

            // Drive unticked on Google's granular permission page.
            const string scope = """
                {
                  "error": {
                    "code": 403,
                    "message": "Request had insufficient authentication scopes.",
                    "errors": [ { "message": "Insufficient Permission", "domain": "global", "reason": "insufficientPermissions" } ],
                    "status": "PERMISSION_DENIED",
                    "details": [ { "@type": "type.googleapis.com/google.rpc.ErrorInfo", "reason": "ACCESS_TOKEN_SCOPE_INSUFFICIENT",
                                   "domain": "googleapis.com", "metadata": { "service": "drive.googleapis.com", "method": "google.apps.drive.v3.DriveAbout.Get" } } ]
                  }
                }
                """;
            Equal("Drive unticked on the permission page says to tick it", GoogleSetupErrors.NotEnoughAccess,
                GoogleSetupErrors.FromApiBody(scope) ?? "(nothing)");

            Check("both read as plain sentences", Plain(GoogleSetupErrors.DriveApiOff) && Plain(GoogleSetupErrors.NotEnoughAccess));
        }

        private static void GoogleLeavesOthersAlone()
        {
            // Everything that is not a setup problem keeps its old wording.
            Check("a file's own permissions are not a setup problem", GoogleSetupErrors.FromApiBody(
                "{\"error\":{\"code\":403,\"errors\":[{\"reason\":\"insufficientFilePermissions\"}]}}") == null);
            Check("nor a rate limit", GoogleSetupErrors.FromApiBody(
                "{\"error\":{\"code\":403,\"errors\":[{\"reason\":\"userRateLimitExceeded\"}]}}") == null);
            Check("nor a full account", GoogleSetupErrors.FromApiBody(
                "{\"error\":{\"code\":403,\"errors\":[{\"reason\":\"storageQuotaExceeded\"}]}}") == null);
            Check("nor an error page", GoogleSetupErrors.FromBody("<!DOCTYPE html><html>502</html>") == null);
            Check("nor nothing", GoogleSetupErrors.FromBody(null) == null && GoogleSetupErrors.FromBody("") == null);
            Check("an API answer never reads a token endpoint's code",
                GoogleSetupErrors.FromApiBody("{\"error\":\"invalid_grant\"}") == null);
            Check("Explain keeps its rate-limit sentence",
                DriveClient.Explain(403, "{\"error\":{\"errors\":[{\"reason\":\"rateLimitExceeded\"}]}}").Contains("limiting"));
        }

        private static void GoogleWiring()
        {
            var setup = new GoogleSetupException("That client is the wrong type.");
            Equal("a setup problem is found inside whatever wrapped it", "That client is the wrong type.",
                GoogleDrive.SetupProblem(new InvalidOperationException("outer", setup)) ?? "(none)");
            Check("and nothing is found in anything else", GoogleDrive.SetupProblem(new TimeoutException()) == null);
            Check("a setup problem is never retried as a network blip",
                !GoogleDrive.RetryableConnectFailure(setup, cancelled: false, consentStarted: false, consentGranted: false));

            var auth = Source("GoogleAuth.cs");
            Check("the client check refuses through the mapping",
                auth.Contains("GoogleSetupErrors.RefusesClient(error) ? GoogleSetupErrors.ForSignIn(error, description)"));
            Check("the browser's refusal goes through the mapping",
                auth.Contains("GoogleSetupErrors.ForSignIn(code0, query[\"error_description\"])"));
            Check("so does the code exchange", auth.Contains("GoogleSetupErrors.FromBody(payload)"));

            var drive = Source("GoogleDrive.cs");
            int setupAt = drive.IndexOf("SetupProblem(ex) is { } setup", StringComparison.Ordinal);
            int unfinishedAt = drive.IndexOf("\"Google sign-in was not finished.\"", StringComparison.Ordinal);
            Check("a refusal from an unfinished sign-in is said as what it is, not just \"not finished\"",
                setupAt > 0 && unfinishedAt > setupAt);
        }

        // ---------------- Apple Music ----------------

        private static void AppleJudge()
        {
            Check("401 is a refused sign-in", AppleMusicClient.Judge(401) == AppleMusicClient.Answer.SignInRejected);
            Check("403 is a refused sign-in", AppleMusicClient.Judge(403) == AppleMusicClient.Answer.SignInRejected);
            Check("404 is not there", AppleMusicClient.Judge(404) == AppleMusicClient.Answer.NotFound);
            Check("200 and 204 are fine", AppleMusicClient.Judge(200) == AppleMusicClient.Answer.Ok &&
                                          AppleMusicClient.Judge(204) == AppleMusicClient.Answer.Ok);
            Check("a 500 or a 429 is not the sign-in", AppleMusicClient.Judge(500) == AppleMusicClient.Answer.Failed &&
                                                       AppleMusicClient.Judge(429) == AppleMusicClient.Answer.Failed);
            Equal("the expiry is said plainly, with where to fix it",
                "Your Apple Music sign-in has expired. Load a new cookies file in Preferences, Apple Music.",
                AppleMusicClient.ExpiredSentence);
        }

        private static void AppleOncePerSignIn()
        {
            var once = new OncePerSignIn();
            Check("the first refusal of a sign-in is asked about", once.First("token-a"));
            Check("the next refusals of it are not", !once.First("token-a") && !once.First("token-a"));
            Check("a new cookies file is a new sign-in, asked about when it expires too", once.First("token-b"));
            Check("no sign-in at all is never asked about", !once.First("") && !once.First(null));

            // Sixteen pages failing at once ask once.
            var many = new OncePerSignIn();
            int yes = 0;
            Parallel.For(0, 16, _ => { if (many.First("token-c")) Interlocked.Increment(ref yes); });
            Equal("a list failing sixteen pages at once asks once", "1", yes.ToString());
        }

        private static async Task AppleExpiredSaysSo()
        {
            AppleMusic.ResetForTests();
            AppleMusic.ConfigureForTests("expired-token");
            var reported = new List<string>();
            void Heard(string token) { lock (reported) reported.Add(token); }
            AppleMusicClient.SignInRejected += Heard;
            try
            {
                // Apple's answer to an expired media-user-token.
                AppleMusicClient.FakeForTests = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"errors\":[{\"id\":\"X\",\"title\":\"Unauthorized\",\"status\":\"401\",\"code\":\"40100\"}]}"),
                };
                string said = "(nothing)";
                bool signIn = false;
                try { await AppleMusic.ListAsync(AppleMusic.Root + "\\Library\\Songs", fresh: true, CancellationToken.None); }
                catch (AppleMusicException ex) { said = ex.Message; signIn = ex.SignIn; }
                Equal("an expired sign-in says so, and where to fix it", AppleMusicClient.ExpiredSentence, said);
                Check("and is marked as a sign-in problem", signIn);
                Check("and is reported with the sign-in it was about",
                    reported.Count >= 1 && reported.All(t => t == "expired-token"), string.Join(",", reported));

                // A song that is not there is not a sign-in problem.
                reported.Clear();
                AppleMusicClient.FakeForTests = req => req.RequestUri!.AbsolutePath.Contains("/me/account")
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent("{\"meta\":{\"subscription\":{\"active\":true,\"storefront\":\"us\"}}}") }
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
                AppleMusic.ResetForTests();
                AppleMusic.ConfigureForTests("good-token");
                try { await AppleMusic.ListAsync(AppleMusic.Root + "\\Library\\Songs", fresh: true, CancellationToken.None); }
                catch (AppleMusicException) { }
                Check("a 404 is never taken for an expired sign-in", reported.Count == 0, string.Join(",", reported));
            }
            finally
            {
                AppleMusicClient.SignInRejected -= Heard;
                AppleMusicClient.FakeForTests = null;
                AppleMusic.ResetForTests();
            }

            var tray = Source("TrayApplicationContext.cs");
            Check("the tray listens for a refused sign-in", tray.Contains("AppleMusicClient.SignInRejected += OnAppleSignInRejected"));
            Check("asks once per sign-in", tray.Contains("_appleExpiryAsked.First(userToken)"));
            Check("as a Yes/No question", tray.Contains("\"Open Preferences, Apple Music, now?\"") &&
                                           tray.Contains("MessageBoxButtons.YesNo, MessageBoxIcon.Question"));
            Check("and opens the Apple Music page on Yes", tray.Contains("OpenPreferencesExternally(\"Apple Music\")"));
            Check("the player's own refusal is reported too",
                Source("AppleMusicPlayer.cs").Contains("AppleMusicClient.ReportSignInRejected(AppleMusic.SignInToken)"));
        }

        // ---------------- Speech ----------------

        private static void SpeechRoutes()
        {
            foreach (bool host in new[] { false, true })
                foreach (bool listening in new[] { false, true })
                    Check($"NVDA answering always speaks through NVDA (host {host}, UIA listening {listening})",
                        Speech.ChooseRoute(true, host, listening) == Speech.SpeechRoute.Nvda);
            Check("NVDA not answering, a window and a listener: UI Automation",
                Speech.ChooseRoute(false, true, true) == Speech.SpeechRoute.Uia);
            Check("no window to raise it from: silent", Speech.ChooseRoute(false, false, true) == Speech.SpeechRoute.Silent);
            Check("nobody listening: silent", Speech.ChooseRoute(false, true, false) == Speech.SpeechRoute.Silent);
            Check("NVDA's message when it goes away no longer says nothing will be spoken",
                !Speech.UnavailableSentence.Contains("nothing") &&
                Notifications.All.Any(n => n.Id == "speech.unavailable" && n.Template == Speech.UnavailableSentence));
        }

        /// <summary>Runs posted work at once, recording that it was posted.</summary>
        private sealed class InlineContext : SynchronizationContext
        {
            public int Posts;
            public override void Post(SendOrPostCallback d, object? state) { Posts++; d(state); }
        }

        private static void SpeechUiaHost()
        {
            var ui = new InlineContext();
            int asked = 0;
            Speech.UseUiaHost(ui, () => { asked++; return null; });
            Speech.RaiseUia("hello", interrupt: false);
            Check("a UIA message is posted to the UI thread, not raised on the pump", ui.Posts == 1, ui.Posts.ToString());
            Check("and asks there which control to raise it from", asked == 1, asked.ToString());

            // A control with no window yet is skipped, not thrown on.
            using var noHandle = new System.Windows.Forms.Control();
            Speech.UseUiaHost(ui, () => noHandle);
            Speech.RaiseUia("hello", interrupt: true);
            Check("a control with no window is skipped", !noHandle.IsHandleCreated);

            Speech.UseUiaHost(null, null);
            Speech.RaiseUia("hello", interrupt: false);
            Check("with no host nothing is posted", ui.Posts == 2, ui.Posts.ToString());
        }

        private static void SpeechWiring()
        {
            var speech = Source("Speech.cs");
            int route = speech.IndexOf("var route = ChooseRoute(", StringComparison.Ordinal);
            int nvdaOnly = speech.IndexOf("if (route != SpeechRoute.Nvda) continue;", StringComparison.Ordinal);
            int cancel = speech.IndexOf("if (next.Interrupt) CancelNow();", StringComparison.Ordinal);
            int speak = speech.IndexOf("speak(p);", StringComparison.Ordinal);
            Check("the pump decides the route before anything reaches NVDA",
                route > 0 && nvdaOnly > route && cancel > nvdaOnly && speak > cancel);
            Check("UIA is only asked about when NVDA is not answering", speech.Contains("!nvda && UiaClientsListening()"));
            Check("speech is queued even without the NVDA client",
                !speech.Contains("if (_speakText == null) return; // nothing to talk to"));
            Check("the tray gives speech a UI thread and a control",
                Source("TrayApplicationContext.cs").Contains("Speech.UseUiaHost(_ui, UiaSpeechTarget)"));
        }
    }
}
