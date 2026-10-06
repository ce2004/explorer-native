using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// OAuth for an installed application: the browser gets the consent, a
    /// loopback listener catches the code, and the refresh token is kept so the
    /// consent is asked for as rarely as Google allows.
    ///
    /// Two things about this flow are worth knowing before changing it.
    ///
    /// The redirect goes to <c>127.0.0.1</c> on a port picked at run time, not to
    /// a fixed one. Google allows any port on the loopback address for a desktop
    /// client precisely so that an application does not have to claim one, and
    /// claiming one means failing whenever something else got there first.
    ///
    /// The client secret is not a secret. Google says so for installed
    /// applications: it ships inside the executable, where anybody can read it,
    /// and the security of the flow rests on PKCE and on the redirect being
    /// loopback — not on hiding this string. The *refresh token* is the thing
    /// worth protecting, and that one is encrypted to the user account.
    /// </summary>
    internal sealed class GoogleAuth
    {
        private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

        /// <summary>
        /// Full Drive access: read the tree, read the bytes, and change them.
        ///
        /// This was deliberately read-only for a long time, and the reason it is
        /// not any more is that copy, move and delete were asked for. The trade
        /// is real and worth naming: a bug in this application can now damage the
        /// account, where before the worst it could do was fail to show
        /// something. Deleting therefore goes to the Drive trash rather than
        /// removing anything, which is the same bargain the recycle bin makes.
        /// </summary>
        public const string Scope = "https://www.googleapis.com/auth/drive";

        /// <summary>
        /// Bumped whenever <see cref="Scope"/> changes.
        ///
        /// A refresh token is only good for the scope it was granted under, and a
        /// token saved under the old read-only scope will keep on refreshing
        /// happily and then fail every write with "insufficient permissions" —
        /// which reads like a bug in the upload rather than a stale sign-in. A
        /// new file name means the old token is simply not found, and consent is
        /// asked for once.
        /// </summary>
        public const int ScopeVersion = 2;

        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _tokenPath;
        // Fifteen seconds, for the same reason DriveClient uses it: a token
        // refresh sits on exactly the same path as a ranged read — inside a
        // filesystem callback, holding up whoever is listing a folder. This was
        // left on HttpClient's default of a hundred seconds, so a token endpoint
        // that stopped answering could freeze a directory listing for over a
        // minute while every other Drive timeout was carefully set to fifteen.
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        /// <summary>
        /// Whether a failed refresh may open a browser and ask for consent.
        ///
        /// False everywhere except a deliberate connect. A refresh token expires
        /// after seven days while the app is in Testing, and that expiry surfaces
        /// on whichever call happened to need a token next — which, for a cloud
        /// provider, is a filesystem callback on a Cloud Files worker thread.
        /// Opening a consent page from there hangs the directory listing that
        /// asked, for as long as the person takes to sign in. Better to fail the
        /// listing and say so.
        /// </summary>
        public bool AllowConsent { get; set; } = true;

        /// <summary>
        /// Consent for one piece of work and whatever it awaits, and nothing
        /// else. Switching <see cref="AllowConsent"/> on for a reconnect let a
        /// filesystem callback that happened to need a token in that moment open
        /// a browser of its own.
        /// </summary>
        private static readonly AsyncLocal<bool> ConsentHere = new();

        public static IDisposable ConsentScope()
        {
            ConsentHere.Value = true;
            return new EndConsent();
        }

        private sealed class EndConsent : IDisposable
        {
            public void Dispose() => ConsentHere.Value = false;
        }

        private bool MayConsent => AllowConsent || ConsentHere.Value;

        /// <summary>
        /// Raised when a request needed a sign-in it could not have: whatever
        /// asked — a search, an upload, a track — rather than only a folder
        /// listing, which was the one place that noticed.
        /// </summary>
        public event Action? SignInLapsed;

        /// <summary>
        /// Where a user-facing message goes, as (notification id, sentence).
        /// Null when nobody is listening, which is the ordinary case — this class
        /// has no window and is not gaining one.
        /// </summary>
        public Action<string, string>? Notify { get; set; }

        /// <summary>
        /// Whether the browser was actually used this session.
        ///
        /// The difference matters to whoever is being told: a mount that refreshed
        /// a stored token silently is not a sign-in, and announcing one every time
        /// the application starts would be an echo of nothing.
        /// </summary>
        public bool ConsentGranted { get; private set; }

        /// <summary>
        /// The access token and when it stops being good, as one value.
        ///
        /// Two fields read without the lock could be read half-updated — the new
        /// expiry beside the old token — which is a request sent with a token
        /// Google has already let go of, once in a long while, and a 401 nothing
        /// here expected.
        /// </summary>
        private sealed record Access(string Token, DateTime Expires)
        {
            public bool Good => Token.Length > 0 && DateTime.UtcNow < Expires;
        }

        private Access _access = new("", DateTime.MinValue);
        private string _refreshToken = "";

        /// <summary>
        /// Forgets <paramref name="token"/> because Google has refused it, so the
        /// next request refreshes rather than sending it again. Only that token:
        /// one that has already been replaced by a concurrent refresh is left
        /// alone.
        /// </summary>
        public void Invalidate(string token)
        {
            var current = _access;
            if (current.Token == token)
                Interlocked.CompareExchange(ref _access, new Access("", DateTime.MinValue), current);
        }

        /// <summary>
        /// One refresh at a time.
        ///
        /// Every Drive request asks for a token, and Drive requests run several
        /// at once by design — two ranged reads per track, plus whatever
        /// callbacks Windows has in flight. When the hour was up they all saw an
        /// expired token in the same instant and all went to refresh it: several
        /// simultaneous POSTs to Google for the same thing, and if the refresh
        /// token had expired too, several simultaneous browser windows and
        /// loopback listeners racing to claim the same redirect.
        /// </summary>
        private readonly SemaphoreSlim _tokenGate = new(1, 1);

        /// <summary>
        /// How long a caller that may not open a browser waits for one that can.
        ///
        /// Consent waits for a person to click through a sign-in page, which can
        /// be minutes. The callers with AllowConsent false are filesystem
        /// callbacks holding up somebody's directory listing — that is the entire
        /// reason the flag exists — so they must not queue behind it. They wait
        /// long enough for an ordinary refresh to finish and then report that a
        /// sign-in is needed, which is what they would have done anyway.
        /// </summary>
        private const int GateWaitMilliseconds = 15_000;

        public GoogleAuth(string clientId, string clientSecret, string tokenPath)
        {
            _clientId = clientId;
            _clientSecret = clientSecret;
            _tokenPath = tokenPath;
        }

        /// <summary>
        /// The credentials to sign in with: the client file chosen in
        /// Preferences, Google Drive. Nothing is compiled into the application —
        /// the published build ships without any Google client, and each person
        /// brings the desktop-client JSON from their own Cloud project.
        /// </summary>
        public static (string ClientId, string ClientSecret)? Credentials(string directory) =>
            ReadClientJson(directory);

        /// <summary>The name an imported client file is saved under.</summary>
        public const string ClientFileName = "client_secret.json";

        /// <summary>
        /// Asks Google whether a client ID and secret are real, without a
        /// browser: the token endpoint is sent a made-up authorization code.
        /// Google checks the client first, so a wrong ID or secret comes back
        /// as invalid_client, and a right one gets as far as complaining about
        /// the code (invalid_grant). Returns null when Google accepts the pair,
        /// otherwise what to tell the person.
        /// </summary>
        public static async Task<string?> CheckClientAsync(string clientId, string clientSecret, CancellationToken token)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = "explorer-native-client-check",
                ["redirect_uri"] = "http://127.0.0.1",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            };

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), token)
                    .ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

                string error = "";
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                        error = e.GetString() ?? "";
                }
                catch (JsonException) { }

                return error switch
                {
                    "invalid_grant" => null,
                    "invalid_client" => "Google does not accept that client ID and secret. Check both, " +
                                        "and that the client is a Desktop app.",
                    "unauthorized_client" => "Google knows that client but will not let it sign in. Make sure " +
                                             "it is a Desktop app client.",
                    _ => $"Google gave an unexpected answer ({(int)response.StatusCode} {error}).",
                };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Somebody called it off. That is not Google being unreachable,
                // and saying so would send them to check a connection that works.
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return "Google could not be reached to check them. Check the internet connection and press OK again.";
            }
        }

        /// <summary>
        /// Saves a client ID and secret into <paramref name="directory"/> as
        /// <see cref="ClientFileName"/>, encrypted, replacing any client file
        /// already there. Returns null on success, otherwise what went wrong.
        /// <paramref name="clientChanged"/> is true when it names a different
        /// client from the one before, which means a saved sign-in belongs to
        /// the old one and has to be made again.
        /// </summary>
        public static string? SaveClient(string clientId, string clientSecret, string directory, out bool clientChanged)
        {
            clientChanged = false;
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                return "Enter both the client ID and the client secret.";

            // The same shape the Cloud console downloads, so ReadClientJson
            // reads it like any other.
            var text = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["installed"] = new Dictionary<string, object>
                {
                    ["client_id"] = clientId.Trim(),
                    ["client_secret"] = clientSecret.Trim(),
                    ["auth_uri"] = "https://accounts.google.com/o/oauth2/auth",
                    ["token_uri"] = TokenEndpoint,
                    ["redirect_uris"] = new[] { "http://localhost" },
                },
            });

            try
            {
                Directory.CreateDirectory(directory);
                var before = ReadClientJson(directory);

                // Every other client file goes, so there is never a question of
                // which of two the folder means.
                foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                {
                    if (Path.GetFileName(path).Equals(ClientFileName, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        using var other = JsonDocument.Parse(ProtectedFile.ReadAllText(path));
                        if (other.RootElement.TryGetProperty("installed", out _)) File.Delete(path);
                    }
                    catch { }
                }

                // Encrypted for this Windows account, like everything else saved here.
                ProtectedFile.WriteAllText(Path.Combine(directory, ClientFileName), text);

                var after = ReadClientJson(directory);
                clientChanged = before == null || after == null || before.Value.ClientId != after.Value.ClientId;
                return after == null ? "The file was copied but could not be read back." : null;
            }
            catch (Exception ex)
            {
                return "The file could not be saved: " + ex.Message;
            }
        }

        /// <summary>
        /// The credentials exactly as the Cloud console hands them out, so there
        /// is never a moment where a secret is copied by hand into a source file.
        /// Returns null when no such file is there yet.
        /// </summary>
        public static (string ClientId, string ClientSecret)? ReadClientJson(string directory)
        {
            // A fresh machine has no settings folder yet; that is "no file".
            if (!Directory.Exists(directory)) return null;

            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    using var doc = JsonDocument.Parse(ProtectedFile.Decode(bytes));
                    // Google writes the payload under "installed" for a desktop
                    // client and "web" for the other kind. Only the first is ours.
                    if (!doc.RootElement.TryGetProperty("installed", out var node)) continue;
                    if (!node.TryGetProperty("client_id", out var id)) continue;
                    if (!node.TryGetProperty("client_secret", out var secret)) continue;
                    // One an older build left in the clear is encrypted now.
                    if (!ProtectedFile.IsProtected(bytes))
                        try { ProtectedFile.WriteAllText(path, ProtectedFile.Decode(bytes)); } catch { }
                    return (id.GetString() ?? "", secret.GetString() ?? "");
                }
                catch
                {
                    // Some other json in the folder. Not an error worth reporting;
                    // the caller's complaint is "no client file", not "this one
                    // was malformed".
                }
            }
            return null;
        }

        /// <summary>
        /// A token good for the next few minutes, refreshing or asking for
        /// consent as needed. Everything else here exists to serve this.
        /// </summary>
        public async Task<string> AccessToken(CancellationToken token)
        {
            // The common case, and it must stay lock-free: this is on the path of
            // every ranged read, and a token that is still good is the answer
            // almost every time.
            var current = _access;
            if (current.Good) return current.Token;

            // A caller that may not consent and has waited this long is behind a
            // refresh that is not coming back, or behind somebody signing in —
            // neither of which is the sign-in having expired, which is what
            // saying so used to announce while the person was in the browser.
            if (!await _tokenGate.WaitAsync(
                    MayConsent ? Timeout.Infinite : GateWaitMilliseconds, token))
                throw new TimeoutException("Google's sign-in did not answer in time");

            try
            {
                // Re-checked inside. Everything that queued up behind one refresh
                // is now holding an answer from before it happened, and refreshing
                // again would throw away the token that just arrived.
                current = _access;
                if (current.Good) return current.Token;

                if (_refreshToken.Length == 0) LoadRefreshToken();

                if (_refreshToken.Length > 0 && await TryRefresh(token)) return _access.Token;

                if (!MayConsent)
                {
                    try { SignInLapsed?.Invoke(); } catch { }
                    throw new GoogleSignInRequiredException();
                }

                await Consent(token);
                return _access.Token;
            }
            finally { _tokenGate.Release(); }
        }

        private async Task<bool> TryRefresh(CancellationToken token)
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["refresh_token"] = _refreshToken,
                ["grant_type"] = "refresh_token",
            };

            using var response = await _http.PostAsync(
                TokenEndpoint, new FormUrlEncodedContent(form), token);
            var body = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                // Told apart, because one of these is permanent and the other is
                // Google having a bad minute — and the saved sign-in was thrown
                // away for both. A 500 or a 503 from the token endpoint deleted
                // the DPAPI-protected refresh token, and since consent is only
                // ever asked for on a deliberate connect, what the user saw was
                // "Sign-in expired" and a browser they had to go through again,
                // for a server error that would have been over by the next
                // request.
                //
                // invalid_grant is the real one: the token has been revoked, or
                // expired, and no amount of retrying will make it work.
                //
                // unauthorized_client is as final: a token issued to another
                // client — the built-in one, before a client_secret.json was
                // dropped in. Thrown as a network failure, it failed every mount
                // and every deliberate Connect with "answered 401", and nothing
                // offered the browser that would have fixed it.
                bool dead =
                    (int)response.StatusCode is 400 or 401 &&
                    (body.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) ||
                     body.Contains("unauthorized_client", StringComparison.OrdinalIgnoreCase));

                // invalid_client is not the token's fault: the client itself was
                // refused — a mistyped or deleted client_secret.json. The token is
                // kept, so taking the bad file away brings the sign-in back, and
                // the refusal is said as what it is.
                if ((int)response.StatusCode is 400 or 401 &&
                    body.Contains("invalid_client", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Google refused this application's client id — check client_secret.json");

                if (dead)
                {
                    _refreshToken = "";
                    try { if (File.Exists(_tokenPath)) File.Delete(_tokenPath); } catch { }
                    return false;
                }

                // Anything else is Google having a bad minute, and saying "false"
                // here reported it as the sign-in having expired — to everybody
                // listening, with a browser opened on a deliberate connect. It is a
                // failed request, and it is reported as one.
                throw new HttpRequestException(
                    $"Google's sign-in service answered {(int)response.StatusCode}",
                    null, response.StatusCode);
            }

            ReadTokens(body);
            return _access.Token.Length > 0;
        }

        /// <summary>
        /// The browser half. Opens the consent page and waits on a loopback
        /// listener for the redirect that carries the code.
        /// </summary>
        private async Task Consent(CancellationToken token)
        {
            // PKCE. The verifier never leaves this process until the exchange, so
            // a code intercepted on its way back through the browser is useless
            // to whoever caught it.
            string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string state = Base64Url(RandomNumberGenerator.GetBytes(16));

            int port = FreePort();
            string redirect = $"http://127.0.0.1:{port}/";

            using var listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            listener.Start();

            var url =
                $"{AuthEndpoint}?client_id={Uri.EscapeDataString(_clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
                $"&response_type=code" +
                $"&scope={Uri.EscapeDataString(Scope)}" +
                $"&code_challenge={challenge}&code_challenge_method=S256" +
                $"&state={state}" +
                // offline is what asks for a refresh token at all, and consent
                // is what makes Google issue a fresh one rather than assuming
                // the last still works.
                $"&access_type=offline&prompt=consent";

            OpenBrowser(url);

            // Said here rather than by the caller, because this is the moment the
            // application stops being the thing you are looking at and starts
            // waiting on a browser tab somewhere else — for up to the thirty
            // minutes below, with nothing else to explain the pause.
            try { Notify?.Invoke("drive.signin.opened", "Waiting for you to allow access in the browser"); }
            catch { }

            // Thirty minutes, not three. A first sign-in on a new PC is account
            // choice, two-step verification, the "unverified app" warning and
            // the permission page, all through a screen reader; three minutes
            // ran out first, the listener closed, and Google's redirect landed
            // on "127.0.0.1 refused to connect" with nothing said here at all.
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(30), token);
            }
            catch (TimeoutException)
            {
                try { Notify?.Invoke("drive.signin.timeout", "Google sign-in timed out. Press Connect Google Drive again."); }
                catch { }
                throw;
            }
            var query = context.Request.QueryString;

            // Everything that can make this a failure, decided before the page
            // says otherwise: a request without the state or the code is not the
            // redirect, and telling the browser "Signed in" for it left the real
            // one arriving at a listener that had already gone.
            bool failed = query["error"] != null || query["state"] != state ||
                          string.IsNullOrEmpty(query["code"]);
            string heading = failed ? "Sign-in failed" : "Signed in";

            // Escaped, because this is somebody else's text going into a page.
            // The listener answers whatever arrives on the loopback port, so the
            // query string is not necessarily the redirect Google made — anything
            // that can get the browser to open the address supplies it, and
            // dropping it into the markup raw makes this page run it.
            string detail = query["error"] is { } said
                ? $"Google said: {WebUtility.HtmlEncode(said)}"
                : failed
                    ? "This was not the answer Explorer Native was waiting for. Try connecting again."
                    : "You can close this tab and go back to Explorer Native.";

            // A real heading and a real title, because the person who lands here
            // is as likely to be listening to this page as looking at it.
            var bytes = Encoding.UTF8.GetBytes(
                "<!doctype html><html lang=en><meta charset=utf-8>" +
                $"<title>{heading} — Explorer Native</title>" +
                "<body style='font:16px/1.5 system-ui;padding:3rem;max-width:34rem'>" +
                $"<h1>{heading}</h1><p>{detail}</p></body></html>");

            // ContentLength64 rather than letting it chunk, and the stream closed
            // before the listener is: without both, the browser is left holding a
            // response it cannot tell has finished, and sits on a blank page
            // while the sign-in has in fact already succeeded.
            context.Response.StatusCode = failed ? 400 : 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            using (var output = context.Response.OutputStream)
            {
                output.Write(bytes, 0, bytes.Length);
                output.Flush();
            }
            context.Response.Close();

            if (query["error"] is { } refusal) throw new InvalidOperationException($"consent refused: {refusal}");
            if (query["state"] != state) throw new InvalidOperationException("state did not match");

            string code = query["code"] ?? throw new InvalidOperationException("no code in the redirect");

            var form = new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirect,
            };

            using var response = await _http.PostAsync(
                TokenEndpoint, new FormUrlEncodedContent(form), token);
            var payload = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"token exchange failed: {payload}");

            ReadTokens(payload);
            SaveRefreshToken();
            ConsentGranted = true;
        }

        private void ReadTokens(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var fresh = root.TryGetProperty("access_token", out var a) ? a.GetString() ?? "" : "";

            // Refreshing does not return a refresh token; keep the one we have.
            if (root.TryGetProperty("refresh_token", out var r))
                _refreshToken = r.GetString() ?? _refreshToken;

            int seconds = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;

            // A minute of margin. A token that expires between the check and the
            // request it was checked for is a failure that reproduces once a
            // month and never in a test.
            _access = new Access(fresh, DateTime.UtcNow.AddSeconds(seconds - 60));
        }

        // ---- refresh token at rest ----------------------------------------

        private void SaveRefreshToken()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
                File.WriteAllBytes(_tokenPath, Protect(Encoding.UTF8.GetBytes(_refreshToken)));
            }
            catch
            {
                // Losing the stored token costs a sign-in, not the session.
            }
        }

        private void LoadRefreshToken()
        {
            try
            {
                if (!File.Exists(_tokenPath)) return;
                _refreshToken = Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(_tokenPath)));
            }
            catch
            {
                _refreshToken = "";
            }
        }

        // DPAPI through crypt32 rather than a package, which is both the house
        // style and the only in-box route on .NET 8. The token is encrypted to
        // this user account, so a copy of the file is worth nothing on another
        // machine or to another user on this one.
        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB { public uint cbData; public IntPtr pbData; }

        private const uint CryptProtectUiForbidden = 0x1;

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private static byte[] Protect(byte[] data) => Crypt(data, protect: true);
        private static byte[] Unprotect(byte[] data) => Crypt(data, protect: false);

        private static byte[] Crypt(byte[] data, bool protect)
        {
            var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var input = new DATA_BLOB { cbData = (uint)data.Length, pbData = pin.AddrOfPinnedObject() };
                bool ok = protect
                    ? CryptProtectData(ref input, "ExplorerNative Drive", IntPtr.Zero,
                        IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero,
                        IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);

                if (!ok) throw new InvalidOperationException(
                    $"DPAPI failed: {Marshal.GetLastWin32Error()}");

                try
                {
                    var result = new byte[output.cbData];
                    Marshal.Copy(output.pbData, result, 0, result.Length);
                    return result;
                }
                finally
                {
                    LocalFree(output.pbData);
                }
            }
            finally
            {
                pin.Free();
            }
        }

        // ---- small helpers -------------------------------------------------

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>
        /// A port the operating system says is free. Asking for port 0 and
        /// reading back what was bound is the only way to get one without a race
        /// against every other program on the machine.
        /// </summary>
        private static int FreePort()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            int port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            return port;
        }

        private static void OpenBrowser(string url)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
    }
}

