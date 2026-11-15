using System;
using System.Text.Json;

namespace ExplorerNative
{
    /// <summary>
    /// A sign-in or setup problem that only the person can fix in their own
    /// Google Cloud project: the wrong kind of OAuth client, a deleted one, the
    /// Drive API switched off, an account that is not a test user. Its message
    /// is the whole sentence to say, fix included. Never a network failure, so
    /// nothing retries it.
    /// </summary>
    internal sealed class GoogleSetupException : InvalidOperationException
    {
        public GoogleSetupException(string message) : base(message) { }
    }

    /// <summary>
    /// Google's answers to a sign-in or a first Drive request, turned into what
    /// is wrong and what to do about it.
    ///
    /// Everybody brings their own OAuth client from the Google Cloud console,
    /// and most of what can go wrong there arrives as a bare code
    /// (invalid_client, redirect_uri_mismatch, access_denied, a 403 with
    /// accessNotConfigured). Pure: no network, no state, so every mapping is
    /// tested against the bodies Google really sends.
    /// </summary>
    internal static class GoogleSetupErrors
    {
        /// <summary>Where the OAuth client lives, said the same way everywhere.</summary>
        private const string Credentials = "in the Google Cloud console, under APIs and services, Credentials";

        /// <summary>Where test users are added (Google renamed "OAuth consent screen" to this in 2025).</summary>
        private const string Audience = "in the Google Cloud console, under Google Auth platform, Audience";

        /// <summary>
        /// The sentence for an OAuth error code (from the token endpoint, or the
        /// error on the browser's redirect), or null when the code is not one
        /// the person can fix in their project.
        /// </summary>
        public static string? ForSignIn(string? error, string? description = null)
        {
            error = (error ?? "").Trim();
            var said = (description ?? "").Trim();
            switch (error)
            {
                case "invalid_client":
                    // Google says "The OAuth client was not found." for an ID it
                    // has never issued, and "Unauthorized" for a real ID with the
                    // wrong secret.
                    if (said.Contains("not found", StringComparison.OrdinalIgnoreCase))
                        return "Google can't find that client ID. Copy it again, all of it, from your OAuth client " +
                               Credentials + ".";
                    if (said.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
                        said.Contains("secret", StringComparison.OrdinalIgnoreCase))
                        return "Google knows that client ID, but the client secret doesn't match it. Copy the secret " +
                               "again from the same OAuth client " + Credentials + ".";
                    return "Google doesn't accept that client ID and secret. Copy both again from your OAuth client " +
                           Credentials + ".";

                case "deleted_client":
                    return "That OAuth client has been deleted in the Google Cloud console. Make a new one of type " +
                           "Desktop app " + Credentials + ", and enter its ID and secret here.";

                case "disabled_client":
                    return "That OAuth client is turned off in the Google Cloud console. Turn it back on " +
                           Credentials + ", or make a new one of type Desktop app.";

                case "unauthorized_client":
                    return "Google won't let that client sign in this way. It has to be an OAuth client of type " +
                           "Desktop app. Make one " + Credentials + ", Create credentials, OAuth client ID, and " +
                           "enter its ID and secret here.";

                case "redirect_uri_mismatch":
                case "invalid_request" when said.Contains("redirect_uri", StringComparison.OrdinalIgnoreCase):
                    return "That client is the wrong type. Explorer Native needs an OAuth client of type Desktop " +
                           "app, not Web application. Make a Desktop app client " + Credentials + ", and enter its " +
                           "ID and secret here.";

                case "access_denied":
                    if (said.Contains("verification", StringComparison.OrdinalIgnoreCase) ||
                        said.Contains("test user", StringComparison.OrdinalIgnoreCase) ||
                        said.Contains("developer-approved", StringComparison.OrdinalIgnoreCase))
                        return TestUser;
                    return "Google wasn't given permission. If you pressed Cancel, connect again and choose " +
                           "Continue on each page. If Google said the app hasn't completed verification, " +
                           TestUserFix;

                case "org_internal":
                    return "That client only lets accounts in its own organisation sign in. " + Audience +
                           ", make the app External and add your account as a test user, then connect again.";

                case "admin_policy_enforced":
                    return "Your organisation's Google administrator doesn't allow this app. Ask them to allow it, " +
                           "or connect with a personal Google account.";

                case "invalid_scope":
                    return "Google wouldn't give access to Drive for that client. Check that the Google Drive API " +
                           "is enabled for its project, then connect again.";

                case "invalid_grant":
                    return "Google's sign-in code had run out or was already used. Connect again.";
            }
            return null;
        }

        /// <summary>The access_denied that means the app is in testing and this account is not a test user.</summary>
        private const string TestUser =
            "Your Google Cloud app is still in testing, and this Google account isn't one of its test users. " +
            TestUserFix;

        private const string TestUserFix =
            "Add your account as a test user " + Audience + ", then connect again.";

        /// <summary>
        /// The client errors that prove the ID and secret are wrong for us, out
        /// of everything the token endpoint can answer a made-up code with.
        /// invalid_grant is the right answer to a made-up code, so it is not one.
        /// </summary>
        public static bool RefusesClient(string? error) => error is
            "invalid_client" or "unauthorized_client" or "deleted_client" or "disabled_client" or
            "redirect_uri_mismatch";

        /// <summary>
        /// The sentence for any Google answer body this knows: a token endpoint
        /// error ({"error":"invalid_client","error_description":...}) or an API
        /// error ({"error":{"code":403,"errors":[...],"details":[...]}}).
        /// Null for anything else, so the caller says what it said before.
        /// </summary>
        public static string? FromBody(string? body) => FromBody(body, signInToo: true);

        /// <summary>The same, for an API answer only: a token endpoint's codes are left alone.</summary>
        public static string? FromApiBody(string? body) => FromBody(body, signInToo: false);

        private static string? FromBody(string? body, bool signInToo)
        {
            body = (body ?? "").Trim();
            if (!body.StartsWith("{", StringComparison.Ordinal)) return null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error)) return null;

                if (error.ValueKind == JsonValueKind.String)
                    return !signInToo ? null : ForSignIn(error.GetString(),
                        root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String
                            ? d.GetString() : null);

                if (error.ValueKind == JsonValueKind.Object) return ForApi(error);
            }
            catch (JsonException) { }
            return null;
        }

        /// <summary>The Drive API is switched off for the project the client belongs to.</summary>
        public const string DriveApiOff =
            "The Google Drive API isn't turned on for your Google Cloud project. In the Google Cloud console, go " +
            "to APIs and services, Library, find Google Drive API and press Enable. Wait a minute, then connect again.";

        /// <summary>The person unticked Drive on Google's permission page.</summary>
        public const string NotEnoughAccess =
            "Google gave Explorer Native less access than it needs. Connect again, and on Google's permission " +
            "page tick the box to see, edit, create and delete your Google Drive files.";

        private static string? ForApi(JsonElement error)
        {
            // Every reason Google gives, old shape (errors[].reason) and new
            // (details[].reason, status), because the same refusal arrives in
            // either depending on the API's age.
            bool Has(string reason)
            {
                if (error.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var one in list.EnumerateArray())
                        if (one.ValueKind == JsonValueKind.Object && one.TryGetProperty("reason", out var r) &&
                            r.ValueKind == JsonValueKind.String && r.GetString() == reason)
                            return true;
                if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                    foreach (var one in details.EnumerateArray())
                        if (one.ValueKind == JsonValueKind.Object && one.TryGetProperty("reason", out var r) &&
                            r.ValueKind == JsonValueKind.String && r.GetString() == reason)
                            return true;
                return error.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String &&
                       s.GetString() == reason;
            }

            string message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? "" : "";

            if (Has("accessNotConfigured") || Has("SERVICE_DISABLED") ||
                message.Contains("has not been used in project", StringComparison.OrdinalIgnoreCase))
                return DriveApiOff;

            if (Has("insufficientPermissions") || Has("ACCESS_TOKEN_SCOPE_INSUFFICIENT"))
                return NotEnoughAccess;

            return null;
        }
    }
}
