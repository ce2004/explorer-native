using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>One entry in a Drive folder, as much of it as we care about.</summary>
    /// <summary>
    /// An item's capabilities in Drive. <c>DriveId</c> is its shared drive, or
    /// null in My Drive.
    /// </summary>
    internal sealed record DriveRights(string? DriveId, bool CanCopy, bool CanTrash, bool CanRename,
        bool CanMoveWithinDrive, bool CanMoveOutOfDrive, bool CanAddChildren);

    internal sealed record DriveEntry(
        string Id, string Name, string MimeType, long Size, DateTime Modified, string? DriveId = null)
    {
        public bool IsFolder => MimeType == "application/vnd.google-apps.folder";

        /// <summary>
        /// A Doc, Sheet or Slide. These have no byte size anywhere — not here and
        /// not in Drive for desktop, which is where "it reports the size wrong"
        /// comes from. They are not files, they are documents that only exist
        /// inside Google, and the only honest size for them is none.
        /// </summary>
        public bool IsGoogleDocument =>
            MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) && !IsFolder;
    }

    /// <summary>
    /// The Drive half: list a folder, and read a range of bytes out of a file.
    ///
    /// That really is the whole surface a read-only provider needs. The Cloud
    /// Files callback asks for an offset and a length; this turns that into one
    /// HTTP request with a Range header. Nothing else in the API is on the path
    /// between a key press and audio.
    /// </summary>
    internal sealed partial class DriveClient
    {
        private const string Files = "https://www.googleapis.com/drive/v3/files";
        private const string About = "https://www.googleapis.com/drive/v3/about";

        private readonly GoogleAuth _auth;

        // Fifteen seconds, not thirty. This timeout is how long a filesystem
        // callback can sit holding up whoever is listing a folder or reading a
        // track, and a minute of a frozen window is a worse answer than "Drive
        // is offline".
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        /// <summary>
        /// The client the *upload* path's metadata goes out on: two minutes.
        ///
        /// Nothing here is holding a window up. Creating a folder and listing a
        /// destination happen inside a transfer that already has its own window,
        /// its own progress and its own Cancel button, so the fifteen-second
        /// ceiling above buys nothing and costs a great deal: a folder holding
        /// several thousand files is several pages of listing, and one of them
        /// running long used to end the entire upload.
        ///
        /// Two minutes rather than none, because a request that really has died
        /// still has to be given up on — and the retry around it, which is the
        /// actual answer, needs something to be triggered by.
        /// </summary>
        private readonly HttpClient _slowMetadata = new() { Timeout = TimeSpan.FromMinutes(2) };

        /// <summary>
        /// Sends a metadata request that belongs to a transfer, with a longer
        /// ceiling and a retry on anything temporary.
        ///
        /// Creating a folder and listing one are small requests, and for a long
        /// time they were the only part of an upload with no retry at all — so a
        /// single 502 while making a folder failed that folder *and* everything
        /// underneath it, and a slow listing ended the transfer outright. Both
        /// are the same shape as the file uploads next door and both get the
        /// same treatment: <see cref="WorthRetrying(int)"/> to decide, and
        /// <see cref="RetryDelay(int, Random)"/> to space it out.
        ///
        /// <paramref name="build"/> rather than a request, because an
        /// HttpRequestMessage cannot be sent twice — a retry that reuses one
        /// fails on the reuse rather than on the thing it was retrying.
        /// </summary>
        /// <param name="landed">
        /// For a request that makes something — a folder, an empty file, a copy.
        /// A request that timed out or came back 5xx may have been carried out
        /// all the same, with only the answer lost, and sending it again makes
        /// a second one: two folders of one name with the tree split between
        /// them. So before a resend, and before giving up, this is asked whether
        /// the thing now exists — found by the tag the request carried, see
        /// <see cref="MadeWithTag"/> — and its answer, the file as JSON, is
        /// returned as though the request had succeeded.
        /// </param>
        private async Task<HttpResponseMessage> SendWithRetry(
            Func<Task<HttpRequestMessage>> build, HttpCompletionOption completion, CancellationToken token,
            Func<CancellationToken, Task<string?>>? landed = null)
        {
            var random = new Random();
            Exception? last = null;
            TimeSpan? asked = null;
            bool refusedOnce = false;
            bool maybeDone = false;

            async Task<HttpResponseMessage?> AlreadyDone()
            {
                if (!maybeDone || landed == null) return null;

                string? found = await AskWhetherLanded(landed, random, token);

                return found == null
                    ? null
                    : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent(found, System.Text.Encoding.UTF8, "application/json"),
                    };
            }

            for (int attempt = 0; attempt < MaxSmallUploadAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (attempt > 0) await Task.Delay(RetryDelay(attempt - 1, random, asked), token);

                if (await AlreadyDone() is { } done) return done;

                HttpResponseMessage response;
                try
                {
                    using var request = await build();
                    response = await Send(request, completion, token, _slowMetadata);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is GoogleSignInRequiredException or InvalidOperationException
                                               or ArgumentException or UnauthorizedAccessException)
                {
                    // About the request or the account, not the moment: the
                    // weekly sign-in expiry, or a refusal made before anything
                    // was sent. Six attempts and forty seconds of backoff only
                    // hold up the folder listing that is waiting on it.
                    throw;
                }
                catch (Exception ex)
                {
                    // A timeout or a dropped connection. Both are "not now", and
                    // both used to be the end of the transfer.
                    last = ex;
                    asked = null;
                    maybeDone = true;

                    // An outage is waited out rather than charged to the budget,
                    // exactly as it is for a file. A folder that cannot be
                    // created takes everything underneath it with it, so this is
                    // the one that matters most.
                    if (await WaitOutNetwork(ex, "asking Google Drive", token))
                    {
                        attempt--;
                        continue;
                    }

                    if (attempt == MaxSmallUploadAttempts - 1)
                    {
                        if (await AlreadyDone() is { } made) return made;
                        throw;
                    }
                    continue;
                }

                if (response.IsSuccessStatusCode) return response;

                // A refused token has already been forgotten by Send; one more go
                // builds the request again with a fresh one. Once: a second 401 is
                // the sign-in really being gone.
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && !refusedOnce)
                {
                    refusedOnce = true;
                    response.Dispose();
                    attempt--;
                    continue;
                }

                // The body has to be read before the retry decision, because a
                // 403 only says whether it means "slow down" or "you may not"
                // in its payload.
                string body;
                try { body = await response.Content.ReadAsStringAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    response.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    // The answer broke off part way. Whatever it was, the
                    // request may have been carried out.
                    response.Dispose();
                    last = ex;
                    asked = null;
                    maybeDone = true;
                    continue;
                }

                if (!WorthRetrying((int)response.StatusCode, body)) return response;

                int code = (int)response.StatusCode;
                if (code == 408 || code >= 500) maybeDone = true;

                asked = RetryAfter(response);
                last = new InvalidOperationException(
                    $"{(int)response.StatusCode}: " + Explain((int)response.StatusCode, body));
                response.Dispose();
            }

            // The last answer may have been lost as well: asked once more before
            // reporting a failure for something that happened.
            if (await AlreadyDone() is { } finished) return finished;
            throw last ?? new InvalidOperationException("Google Drive would not answer");
        }

        /// <summary>
        /// Whether a create whose answer was lost happened: the file as JSON, or
        /// null when Drive says it is not there. When Drive will not say, it is
        /// asked again; still unanswered, this throws — resending on "could not
        /// ask" is how a folder came to be made twice.
        /// </summary>
        internal async Task<string?> AskWhetherLanded(
            Func<CancellationToken, Task<string?>> landed, Random random, CancellationToken token)
        {
            for (int ask = 0; ; ask++)
            {
                try { return await landed(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (GoogleSignInRequiredException) { throw; }
                catch (Exception ex)
                {
                    // The network going away is waited out, as for the request
                    // itself — a fifteen-second drop otherwise failed the create,
                    // and every file under a folder with it.
                    if (await WaitOutNetwork(ex, "checking what Google Drive already has", token))
                    {
                        ask--;
                        continue;
                    }

                    if (ask >= 3)
                        throw new InvalidOperationException(
                            "Google Drive would not say whether this had already been done, " +
                            "so it was not sent again: " + ex.Message, ex);
                    await Task.Delay(RetryDelay(ask, random), token);
                }
            }
        }

        /// <summary>Whether Drive is answering, and who to tell when that changes.</summary>
        public DriveHealth Health { get; } = new();

        /// <summary>
        /// Where a user-facing message goes, as (notification id, sentence).
        ///
        /// Null when nobody is listening, and checked before anything is built:
        /// every method here can be reached from a Cloud Files worker thread that
        /// is holding up somebody's directory listing.
        /// </summary>
        public Action<string, string>? Notify { get; set; }

        /// <summary>
        /// How to spell a byte count in a message. Set from the preference so a
        /// resumed upload is read out the same way every other size in the
        /// application is.
        /// </summary>
        public SizeUnitStyle Units { get; set; } = SizeUnitStyle.FullWords;

        private void Say(string id, string message)
        {
            var notify = Notify;
            if (notify == null) return;
            try { notify(id, message); } catch { }
        }

        /// <summary>
        /// When the last throttling was mentioned. Google throttles a burst, not
        /// one request, so the interesting fact arrives dozens of times in a row
        /// and is worth saying once.
        /// </summary>
        private long _rateLimitedAt;

        private const long RateLimitQuietMs = 60_000;

        /// <summary>
        /// Makes every request fail as though the network had gone, for testing
        /// what the application does about it. Never set outside a harness.
        /// </summary>
        public static bool SimulateOffline { get; set; }

        public DriveClient(GoogleAuth auth) => _auth = auth;

        private static readonly HttpRequestOptionsKey<bool> Retried = new("ExplorerNative.Retried401");

        /// <summary>
        /// One request, with the health of the connection recorded either way.
        ///
        /// Every call goes through here so that "is Drive reachable" is answered
        /// by the traffic the application was making anyway, rather than by
        /// polling Google to ask whether the internet still exists.
        /// </summary>
        /// <param name="client">
        /// Which client to send on, for the one caller that cannot use the
        /// fifteen-second one: an upload streams a whole file in a single
        /// request, and a gigabyte at Drive's measured 13.9MB/s is seventy
        /// seconds of one perfectly healthy PUT. See <c>Uploads</c>.
        /// </param>
        private async Task<HttpResponseMessage> Send(
            HttpRequestMessage request, HttpCompletionOption completion, CancellationToken token,
            HttpClient? client = null)
        {
            if (SimulateOffline)
            {
                var pretend = new HttpRequestException("simulated network failure");
                Health.Failed(pretend);
                throw pretend;
            }

            try
            {
                var response = await (client ?? _http).SendAsync(request, completion, token);
                Health.Succeeded();

                // Refused a token that looked good — revoked, or let go early.
                // Forgotten, so the next request refreshes; and a request with no
                // body is simply sent again, once, with the new one. One that
                // carries a body cannot be (its stream has been read), and its
                // caller's own retry builds a fresh request with a fresh token.
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
                    request.Headers.Authorization?.Parameter is { } refused)
                {
                    _auth.Invalidate(refused);

                    if (request.Content == null && !request.Options.TryGetValue(Retried, out _))
                    {
                        using var again = new HttpRequestMessage(request.Method, request.RequestUri);
                        foreach (var header in request.Headers)
                            if (!header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                                again.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        again.Options.Set(Retried, true);

                        // The refused answer is let go whatever happens next, a
                        // failed refresh included.
                        response.Dispose();
                        again.Headers.Authorization =
                            new AuthenticationHeaderValue("Bearer", await _auth.AccessToken(token));
                        response = await (client ?? _http).SendAsync(again, completion, token);
                    }
                }

                // 429 is the one throttling answer that needs nothing read out of
                // the body — which matters, because the body of a ranged read is a
                // stream the caller is about to consume. (Google also spells this
                // as a 403 with reason "rateLimitExceeded"; that one is only
                // visible from the payload, so it is deliberately not claimed
                // here rather than guessed at from the status code alone.)
                if ((int)response.StatusCode == 429) NoteThrottling(response);

                return response;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // We asked for this. A cancelled request says nothing about
                // whether Google is reachable, and counting it towards going
                // offline means the application's own tidying up — abandoning a
                // download because the track changed — reads as the network
                // failing. Rethrown unchanged; only the health report is skipped.
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // A *timeout*, wearing cancellation's clothes — and this line is
                // the whole of "the upload cancelled itself ten percent in".
                //
                // HttpClient reports its own Timeout by throwing
                // TaskCanceledException, which is an OperationCanceledException,
                // with nobody's token cancelled. Every layer above here treats
                // that type as "the person pressed Cancel": UploadOne rethrows
                // it, Parallel.ForEachAsync abandons the other nine hundred
                // files, and UploadToDrive reports "Cancelled after 216 of
                // 1134". Nothing had been cancelled. A folder listing took
                // sixteen seconds.
                //
                // It gets worse the bigger the job, which is exactly backwards:
                // more files means more folders to create and list, so more
                // chances for one request to run long, and any *one* of them
                // ends the whole upload. That is the reported bug — thousands of
                // files, cancels part way, every time.
                //
                // Given a different type it becomes an ordinary failure: the one
                // file or folder is retried and then recorded, and the other
                // nine hundred carry on. Health.Failed is still told, because a
                // request that ran out of time is evidence about the network in
                // a way a real cancellation is not.
                Health.Failed(ex);
                throw new TimeoutException(
                    $"Google Drive did not answer within {(client ?? _http).Timeout.TotalSeconds:0} seconds", ex);
            }
            catch (Exception ex)
            {
                Health.Failed(ex);
                throw;
            }
        }

        /// <summary>
        /// Says that Google is throttling, at most once a minute.
        ///
        /// Nothing here slows down in response — the retry that follows is
        /// whatever the caller does — so the sentence says what happened and not
        /// what is being done about it.
        /// </summary>
        private void NoteThrottling(HttpResponseMessage response)
        {
            if (Notify == null) return;

            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _rateLimitedAt) < RateLimitQuietMs) return;
            Interlocked.Exchange(ref _rateLimitedAt, now);

            var wait = response.Headers.RetryAfter?.Delta;
            Say("drive.ratelimited", wait == null
                ? "Google is rate limiting Drive requests"
                : $"Google is rate limiting Drive requests; retry after {(int)wait.Value.TotalSeconds} seconds");
        }

        /// <summary>
        /// The id of the folder that lists the account's shared drives.
        ///
        /// Not a Drive object — Drive has no folder that contains its shared
        /// drives; they are a separate list (`drives.list`). It stands in for
        /// one so the letter can show them the way drive.google.com does, beside
        /// My Drive's own folders, and every write refuses it by name.
        /// </summary>
        public const string SharedDrivesId = "::shared-drives";

        public const string SharedDrivesName = "Shared drives";

        /// <summary>
        /// A request URL with the one parameter every call now needs.
        ///
        /// Without `supportsAllDrives`, Drive answers 404 for anything that lives
        /// in a shared drive — a read, a trash, a rename — as though the file did
        /// not exist. It costs nothing for My Drive, so it goes on everything.
        /// </summary>
        internal static string AllDrives(string url)
        {
            if (!url.StartsWith("https://www.googleapis.com/", StringComparison.Ordinal)) return url;
            if (url.Contains("supportsAllDrives=", StringComparison.Ordinal)) return url;
            return url + (url.Contains('?') ? "&" : "?") + "supportsAllDrives=true";
        }

        private async Task<HttpRequestMessage> Request(HttpMethod method, string url, CancellationToken token)
        {
            if (url.Contains(Uri.EscapeDataString(SharedDrivesId), StringComparison.Ordinal) ||
                url.Contains(SharedDrivesId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "\"Shared drives\" is the list of shared drives, not a folder; open one of them first");

            url = AllDrives(url);
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", await _auth.AccessToken(token));
            return request;
        }

        /// <summary>Who we signed in as, and the quota. Proves the token works.</summary>
        public async Task<(string User, long Used, long Limit)> Whoami(CancellationToken token)
        {
            using var request = await Request(HttpMethod.Get,
                $"{About}?fields=user(emailAddress),storageQuota(usage,limit)", token);
            using var response = await Send(request, HttpCompletionOption.ResponseContentRead, token);
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"about failed {(int)response.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string user = root.GetProperty("user").GetProperty("emailAddress").GetString() ?? "";
            var quota = root.GetProperty("storageQuota");

            // Both come back as strings, because they do not fit in a JSON
            // number that every client can read.
            long used = quota.TryGetProperty("usage", out var u) && long.TryParse(u.GetString(), out var uv) ? uv : 0;
            long limit = quota.TryGetProperty("limit", out var l) && long.TryParse(l.GetString(), out var lv) ? lv : 0;
            return (user, used, limit);
        }

        /// <summary>
        /// Everything directly inside a folder. Paged, because a music library is
        /// not going to arrive in one response and a listing that silently stops
        /// at the first page is worse than one that fails.
        ///
        /// The default limit is deliberately far above any real folder. It was
        /// 1000 — one page — and an artists folder came back holding exactly a
        /// thousand names ending at "JZAC", with everything from K to Z simply
        /// absent. Nothing reported a problem: the folder listed, it just was not
        /// all there, and it looked for all the world like type-ahead failing to
        /// find anything under T.
        ///
        /// <paramref name="driveId"/> is the shared drive the folder is in, or
        /// null for My Drive. A folder inside a shared drive lists as empty unless
        /// the query is scoped to that drive, which is not an error anywhere.
        ///
        /// <paramref name="onPage"/> is handed each page as it arrives, so a
        /// folder of several thousand can be shown a thousand at a time rather
        /// than after the last page.
        /// </summary>
        public async Task<List<DriveEntry>> ListChildren(
            string folderId, CancellationToken token, int max = 100_000, string? label = null,
            string? driveId = null, Action<List<DriveEntry>>? onPage = null)
        {
            if (folderId == SharedDrivesId)
            {
                var drives = await ListSharedDrives(token);
                onPage?.Invoke(drives);
                return drives;
            }

            var results = new List<DriveEntry>();
            string? page = null;
            bool saidSlow = false;

            string scope = string.IsNullOrEmpty(driveId)
                ? "&includeItemsFromAllDrives=true"
                : $"&includeItemsFromAllDrives=true&corpora=drive&driveId={Uri.EscapeDataString(driveId)}";

            do
            {
                string query = Uri.EscapeDataString($"'{folderId}' in parents and trashed = false");
                string fields = Uri.EscapeDataString(
                    "nextPageToken,files(id,name,mimeType,size,modifiedTime,driveId)");
                // The first page small when somebody is waiting to see it: Drive
                // takes seconds over a thousand sorted rows, and the first screenful
                // is what makes a folder feel open.
                int pageSize = onPage != null && page == null ? FirstPageSize : 1000;
                string url = $"{Files}?q={query}&fields={fields}&pageSize={pageSize}" +
                             "&orderBy=folder,name" + scope +
                             (page != null ? $"&pageToken={Uri.EscapeDataString(page)}" : "");

                // Retried, on the long client. A listing is the other half of
                // what an upload spends its metadata time on — the destination
                // has to be listed before anything can be said about what is
                // already in it — and a folder holding several thousand files is
                // several of these pages. One of them running past fifteen
                // seconds used to end the entire transfer, reported as a
                // cancellation nobody asked for.
                using var response = await SendWithRetry(
                    () => Request(HttpMethod.Get, url, token),
                    HttpCompletionOption.ResponseContentRead, token);

                var body = await response.Content.ReadAsStringAsync(token);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"list failed {(int)response.StatusCode}: " +
                        Explain((int)response.StatusCode, body));

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                int pageStart = results.Count;

                foreach (var f in root.GetProperty("files").EnumerateArray())
                {
                    // size is absent for folders and for Google's own document
                    // types, and absent is not zero — see DriveEntry.
                    long size = f.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var sv)
                        ? sv : -1;

                    results.Add(new DriveEntry(
                        f.GetProperty("id").GetString() ?? "",
                        f.GetProperty("name").GetString() ?? "",
                        f.GetProperty("mimeType").GetString() ?? "",
                        size,
                        f.TryGetProperty("modifiedTime", out var m) && m.GetDateTime() is var d ? d : default,
                        f.TryGetProperty("driveId", out var dr) ? dr.GetString() : driveId));

                    if (results.Count >= max)
                    {
                        onPage?.Invoke(results.GetRange(pageStart, results.Count - pageStart));
                        return results;
                    }
                }

                if (results.Count > pageStart)
                    onPage?.Invoke(results.GetRange(pageStart, results.Count - pageStart));

                page = root.TryGetProperty("nextPageToken", out var p) ? p.GetString() : null;

                // A second page means over a thousand entries, and it is the only
                // thing this layer knows for certain about how much longer the
                // rest will take. It is also known *during* the listing, which is
                // the only time saying "still fetching" is any use — measured, a
                // folder nobody has opened takes about 1.3s per page, and the
                // artists folder here is three of them.
                if (page != null && label != null && !saidSlow)
                {
                    saidSlow = true;
                    Say("drive.folder.slow", $"{label} is large; still fetching");
                }
            }
            while (page != null);

            return results;
        }

        /// <summary>
        /// The shared drives this account can see, each as a folder whose id is
        /// the drive's — a shared drive's top folder has the same id as the drive.
        /// </summary>
        public async Task<List<DriveEntry>> ListSharedDrives(CancellationToken token)
        {
            var results = new List<DriveEntry>();
            string? page = null;

            do
            {
                string fields = Uri.EscapeDataString("nextPageToken,drives(id,name,createdTime)");
                string url = $"{DrivesEndpoint}?pageSize=100&fields={fields}" +
                             (page != null ? $"&pageToken={Uri.EscapeDataString(page)}" : "");

                using var response = await SendWithRetry(
                    () => Request(HttpMethod.Get, url, token),
                    HttpCompletionOption.ResponseContentRead, token);

                var body = await response.Content.ReadAsStringAsync(token);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"shared drives failed {(int)response.StatusCode}: " +
                        Explain((int)response.StatusCode, body));

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("drives", out var drives))
                {
                    foreach (var d in drives.EnumerateArray())
                    {
                        var id = d.GetProperty("id").GetString() ?? "";
                        if (id.Length == 0) continue;
                        results.Add(new DriveEntry(
                            id,
                            d.GetProperty("name").GetString() ?? "",
                            FolderMimeType,
                            0,
                            d.TryGetProperty("createdTime", out var c) && c.GetDateTime() is var t ? t : default,
                            id));
                    }
                }

                page = root.TryGetProperty("nextPageToken", out var p) ? p.GetString() : null;
            }
            while (page != null);

            return results.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>How many rows the first page of a watched listing asks for.</summary>
        internal static int FirstPageSize { get; set; } = 200;

        private const string DrivesEndpoint = "https://www.googleapis.com/drive/v3/drives";

        /// <summary>
        /// The id of the account's own root folder.
        ///
        /// Asked rather than worked out. "root" is an alias Drive accepts in a
        /// query and never the id it puts in a `parents` array, so the two have
        /// to be joined up before a chain of parents can be recognised as
        /// reaching the top — and the obvious way to infer it, looking for a
        /// parent that is not itself a folder in the listing, picks the wrong
        /// answer for any account with a folder shared into it, whose parent is
        /// equally absent. One request, and no guessing.
        /// </summary>
        public async Task<string> RootId(CancellationToken token)
        {
            using var request = await Request(HttpMethod.Get, $"{Files}/root?fields=id", token);
            using var response = await Send(request, HttpCompletionOption.ResponseContentRead, token);
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"root lookup failed {(int)response.StatusCode}: " +
                    Explain((int)response.StatusCode, body));

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("id").GetString() ?? "";
        }

        /// <summary>
        /// Files anywhere in the account whose name contains
        /// <paramref name="term"/>.
        ///
        /// One request, however large the Drive — which is why a search of the
        /// letter goes to Google rather than walking placeholders. It is not
        /// scoped to a folder, because Drive's query language cannot express
        /// "or anything below it"; see <see cref="DriveSearch"/> for the half
        /// that narrows it down.
        /// </summary>
        public Task<List<DriveFound>> FindByName(string term, CancellationToken token, int max = 10_000,
            string? driveId = null) =>
            Query($"name contains '{EscapeQuery(term)}' and trashed = false", token, max, driveId);

        /// <summary>
        /// Every folder in the account.
        ///
        /// This is the request that makes a scoped search cheap. Resolving where
        /// each hit lives by asking Drive about its parents is one round trip per
        /// folder at about 700ms; one listing of *all* folders is a few hundred
        /// rows for a library of thousands of files, and every chain then
        /// resolves out of memory for nothing.
        /// </summary>
        public Task<List<DriveFound>> AllFolders(CancellationToken token, int max = 50_000,
            string? driveId = null) =>
            Query("mimeType = 'application/vnd.google-apps.folder' and trashed = false", token, max, driveId);

        /// <summary>
        /// A term as Drive's query language will take it.
        ///
        /// Both of these are ordinary in a file name and both end the string
        /// early if they go through unescaped — an apostrophe closes the literal,
        /// and a backslash escapes whatever follows it. A name with one in it is
        /// not an error case to reject, it is Tuesday: "don't go.flac" is on this
        /// very Drive.
        /// </summary>
        internal static string EscapeQuery(string term) =>
            term.Replace("\\", "\\\\").Replace("'", "\\'");

        /// <summary>
        /// The app property a create carries so that a retry can find it: a
        /// fresh value per operation.
        ///
        /// Found by name and time instead, the lookup adopted whatever of that
        /// name had been made in the last minute — the previous upload of the
        /// same take, which an overwrite then trashed, reporting success for an
        /// upload that never happened. App properties are private to this
        /// client and cost nothing to carry.
        /// </summary>
        internal const string OperationProperty = "explorerNativeOperation";

        internal static string NewOperationTag() => Guid.NewGuid().ToString("N");

        /// <summary>The shared drive a folder is in, or null for My Drive. Throws when it cannot be told.</summary>
        private async Task<string?> DriveOf(string folderId, CancellationToken token)
        {
            using var request = await Request(HttpMethod.Get,
                $"{Files}/{Uri.EscapeDataString(folderId)}?fields=driveId", token);
            using var response = await Send(request, HttpCompletionOption.ResponseContentRead, token, _slowMetadata);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Google Drive answered {(int)response.StatusCode} when asked",
                    null, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            return doc.RootElement.TryGetProperty("driveId", out var id) ? id.GetString() : null;
        }

        /// <summary>
        /// What the signed-in account may do with an item, as Drive says —
        /// the owner's sharing settings and a shared drive's roles included.
        /// </summary>
        internal async Task<DriveRights> RightsOf(string fileId, CancellationToken token)
        {
            // The list of shared drives is ours, not an item Drive can answer for.
            if (fileId == SharedDrivesId) return new DriveRights(null, false, false, false, false, false, false);

            const string fields = "driveId,capabilities(canCopy,canTrash,canRename," +
                                  "canMoveItemWithinDrive,canMoveItemOutOfDrive,canAddChildren)";
            using var request = await Request(HttpMethod.Get,
                $"{Files}/{Uri.EscapeDataString(fileId)}?fields={Uri.EscapeDataString(fields)}", token);
            using var response = await Send(request, HttpCompletionOption.ResponseContentRead, token, _slowMetadata);
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"{(int)response.StatusCode}: " + Explain((int)response.StatusCode, body));

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? driveId = root.TryGetProperty("driveId", out var d) ? d.GetString() : null;
            bool Can(string name) =>
                root.TryGetProperty("capabilities", out var caps) &&
                caps.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

            return new DriveRights(driveId,
                Can("canCopy"), Can("canTrash"), Can("canRename"),
                Can("canMoveItemWithinDrive"), Can("canMoveItemOutOfDrive"), Can("canAddChildren"));
        }

        /// <summary>A file's own name in Drive, which the letter may show cleaned up.</summary>
        internal async Task<string> NameOf(string fileId, CancellationToken token)
        {
            using var request = await Request(HttpMethod.Get,
                $"{Files}/{Uri.EscapeDataString(fileId)}?fields=name", token);
            using var response = await Send(request, HttpCompletionOption.ResponseContentRead, token, _slowMetadata);
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"{(int)response.StatusCode}: " + Explain((int)response.StatusCode, body));

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("name").GetString() ?? "";
        }

        /// <summary>
        /// The file in <paramref name="parentId"/> made by the operation tagged
        /// <paramref name="tag"/>, as JSON with the fields asked for, or null when
        /// Drive says there is none. Throws when it cannot say — a refused query,
        /// or a search Drive reports as incomplete — because "not found" is the
        /// answer that sends the request again.
        /// </summary>
        internal async Task<string?> MadeWithTag(string tag, string parentId, string fields, CancellationToken token)
        {
            string query = $"'{EscapeQuery(parentId)}' in parents and trashed = false" +
                           $" and appProperties has {{ key='{OperationProperty}' and value='{EscapeQuery(tag)}' }}";

            // Scoped to the drive the folder is in. Across every drive the account
            // can see — sixty-four here — Drive reports the search incomplete,
            // and an incomplete "not found" cannot be acted on.
            string? driveId = await DriveOf(parentId, token);
            string scope = driveId == null
                ? ""
                : $"&corpora=drive&driveId={Uri.EscapeDataString(driveId)}&includeItemsFromAllDrives=true";

            string url = $"{Files}?q={Uri.EscapeDataString(query)}" +
                         $"&fields={Uri.EscapeDataString($"incompleteSearch,files({fields})")}" +
                         "&pageSize=10" + scope;

            using var request = await Request(HttpMethod.Get, url, token);
            using var response = await Send(request, HttpCompletionOption.ResponseContentRead, token, _slowMetadata);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Google Drive answered {(int)response.StatusCode} when asked",
                    null, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            foreach (var file in doc.RootElement.GetProperty("files").EnumerateArray())
                return file.GetRawText();

            if (doc.RootElement.TryGetProperty("incompleteSearch", out var incomplete) &&
                incomplete.ValueKind == JsonValueKind.True)
                throw new InvalidOperationException("Google Drive could not search everywhere");
            return null;
        }

        /// <summary>
        /// A `files.list` query, followed through every page.
        ///
        /// Separate from <see cref="ListChildren"/> rather than shared with it:
        /// that one orders by folder and name and says "still fetching" for a
        /// folder somebody is waiting to see, neither of which a search wants,
        /// and it does not ask for parents.
        ///
        /// My Drive, or one shared drive — never all of them at once. Measured
        /// on an account with 64 shared drives: every folder across all drives
        /// was more than 50,000 rows and 158 seconds, against three seconds for My
        /// Drive's own. Scoped to the drive being searched, it costs what that
        /// drive holds.
        /// </summary>
        private async Task<List<DriveFound>> Query(string query, CancellationToken token, int max,
            string? driveId = null)
        {
            string scope = string.IsNullOrEmpty(driveId)
                ? ""
                : $"&corpora=drive&driveId={Uri.EscapeDataString(driveId)}&includeItemsFromAllDrives=true";

            var results = new List<DriveFound>();
            string? page = null;

            do
            {
                string fields = Uri.EscapeDataString(
                    "nextPageToken,files(id,name,mimeType,size,modifiedTime,parents)");
                string url = $"{Files}?q={Uri.EscapeDataString(query)}&fields={fields}&pageSize=1000" +
                             scope +
                             (page != null ? $"&pageToken={Uri.EscapeDataString(page)}" : "");

                using var response = await SendWithRetry(
                    () => Request(HttpMethod.Get, url, token),
                    HttpCompletionOption.ResponseContentRead, token);

                var body = await response.Content.ReadAsStringAsync(token);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"search failed {(int)response.StatusCode}: " +
                        Explain((int)response.StatusCode, body));

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                foreach (var f in root.GetProperty("files").EnumerateArray())
                {
                    long size = f.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var sv)
                        ? sv : -1;

                    string? parent = null;
                    if (f.TryGetProperty("parents", out var ps) && ps.GetArrayLength() > 0)
                        parent = ps[0].GetString();

                    results.Add(new DriveFound(
                        f.GetProperty("id").GetString() ?? "",
                        f.GetProperty("name").GetString() ?? "",
                        f.GetProperty("mimeType").GetString() ?? "",
                        size,
                        f.TryGetProperty("modifiedTime", out var m) && m.GetDateTime() is var d ? d : default,
                        parent));

                    if (results.Count >= max) return results;
                }

                page = root.TryGetProperty("nextPageToken", out var p) ? p.GetString() : null;
            }
            while (page != null);

            return results;
        }

        /// <summary>
        /// Bytes <paramref name="offset"/> through <paramref name="offset"/> +
        /// <paramref name="length"/> of a file. This is the one call that sits
        /// inside the Cloud Files callback, so what it costs is what a read of an
        /// un-hydrated placeholder costs.
        ///
        /// **And it was the one Drive call with no retry at all.** Every write
        /// has had `WorthRetrying` and `RetryDelay` around it since the upload
        /// path was written — the argument being that a service which is right
        /// almost all of the time still says 503 sometimes, and one request per
        /// file means one chance each. A hydration is a request per four
        /// megabytes of a file somebody is copying, which is far more chances
        /// than an upload gets, and a single one of them coming back 500 failed
        /// the read: robocopy reported the file, and the copy of a folder came
        /// out with holes in it.
        ///
        /// **Bounded by a clock, not only by a count, and that is the whole
        /// difference between this and the retry the writes get.** An upload is
        /// allowed six attempts spaced by exponential backoff because nothing is
        /// waiting on it but the transfer's own window. This runs *inside a
        /// filesystem callback*: whoever is reading the file is blocked in a
        /// `ReadFile` for as long as it takes, Windows has its own clock running
        /// against the callback, and the shutdown path cannot disconnect the sync
        /// root until every callback in flight has come back.
        ///
        /// Written first as three attempts with the writes' delays, it made a
        /// single read worth up to fifty seconds — three fifteen-second timeouts
        /// with backoff between them — which is worse than the failure it was
        /// added to prevent, and turns a Drive that has gone away into an
        /// application that will not close.
        ///
        /// So: a total budget, checked before each attempt, and delays measured
        /// in the few hundred milliseconds a blip actually lasts. Past the budget
        /// the honest answer is that the read failed.
        /// </summary>
        public async Task<byte[]> ReadRange(
            string fileId, long offset, int length, CancellationToken token)
        {
            var spent = Stopwatch.StartNew();
            Exception? last = null;

            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();

                if (attempt > 0)
                {
                    // Room for the attempt as well as the wait. Starting a
                    // fifteen-second request with two seconds of budget left is
                    // how a bounded retry stops being bounded.
                    var wait = ReadRetryDelays[Math.Min(attempt - 1, ReadRetryDelays.Length - 1)];
                    if (spent.Elapsed + wait >= ReadRetryBudget) break;
                    await Task.Delay(wait, token);
                }

                try { return await ReadRangeOnce(fileId, offset, length, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (RetryableReadException ex) { last = ex.InnerException ?? ex; }
                catch (TimeoutException ex) { last = ex; }
                catch (HttpRequestException ex) { last = ex; }
                // A connection reset part way through the body arrives as an
                // IOException from the stream, not as anything HTTP-shaped, and
                // it is the most ordinary blip there is.
                catch (IOException ex) { last = ex; }

                if (spent.Elapsed >= ReadRetryBudget) break;
            }

            throw last ?? new InvalidOperationException("range read failed");
        }

        /// <summary>
        /// The longest a single ranged read may take, retries included.
        ///
        /// Twenty seconds: comfortably more than one fifteen-second request, so a
        /// blip on the first attempt is still retried, and comfortably less than
        /// the point at which a blocked callback becomes everybody's problem.
        /// </summary>
        private static readonly TimeSpan ReadRetryBudget = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Short, and not the writes' exponential backoff. What is being waited
        /// out here is a single request that failed while the connection is
        /// otherwise healthy — a 503 from one of Google's front ends, a socket
        /// that closed. That resolves in milliseconds or not at all, and the
        /// thirty-second ceiling the uploads use has nowhere to fit.
        /// </summary>
        private static readonly TimeSpan[] ReadRetryDelays =
        {
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(750),
        };

        /// <summary>
        /// A range read that failed for a reason worth coming back for, wrapped
        /// so the retry above can tell it from a 404 on a file that has been
        /// deleted — which no amount of asking again will fix.
        /// </summary>
        private sealed class RetryableReadException : Exception
        {
            public RetryableReadException(string message, Exception? inner = null)
                : base(message, inner) { }
        }

        /// <summary>
        /// How long one attempt may go without a byte arriving.
        ///
        /// The client's own timeout stops at the headers when the body is
        /// streamed, so a response that started and then stalled — a connection
        /// that went quiet part way through a four-megabyte chunk — waited on the
        /// body for ever, holding a filesystem callback and everything reading
        /// behind it. This clock covers the body too.
        ///
        /// It is reset by every block that arrives. It was fifteen seconds for
        /// the whole request, which on a slow link failed four megabytes that
        /// were arriving perfectly well, over and over.
        /// </summary>
        private static readonly TimeSpan ReadAttemptLimit = TimeSpan.FromSeconds(15);

        private async Task<byte[]> ReadRangeOnce(
            string fileId, long offset, int length, CancellationToken token)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            attempt.CancelAfter(ReadAttemptLimit);

            try
            {
                return await ReadRangeWithin(fileId, offset, length, attempt.Token,
                    () => { try { attempt.CancelAfter(ReadAttemptLimit); } catch { } });
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Google Drive stopped sending [{offset:N0} +{length:N0}] part way through");
            }
        }

        private async Task<byte[]> ReadRangeWithin(
            string fileId, long offset, int length, CancellationToken token, Action arrived)
        {
            using var request = await Request(HttpMethod.Get, $"{Files}/{fileId}?alt=media", token);
            request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);

            using var response = await Send(request, HttpCompletionOption.ResponseHeadersRead, token);

            if (response.StatusCode != HttpStatusCode.PartialContent &&
                response.StatusCode != HttpStatusCode.OK)
            {
                var body = await response.Content.ReadAsStringAsync(token);
                var message = $"range read failed {(int)response.StatusCode}: {body}";

                throw WorthRetrying((int)response.StatusCode, body)
                    ? new RetryableReadException(message)
                    : new InvalidOperationException(message);
            }

            // A 200 rather than a 206 means the server ignored the range and is
            // sending the whole file. Worth knowing about loudly: it is the
            // difference between streaming and downloading.
            RangeHonoured = response.StatusCode == HttpStatusCode.PartialContent;

            // And a 200 to a request that asked for the *middle* of a file is
            // refused rather than read. The body then begins at byte zero, and
            // reading `length` bytes out of it hands back the head of the file as
            // though it were the contents at `offset` — which is cached under
            // that chunk index, served to Windows at that position, and written
            // into the middle of whatever is being copied. Silent, and wrong in
            // the one way nothing downstream can detect. At offset zero a 200 is
            // exactly what was asked for.
            if (!RangeHonoured && offset > 0)
                throw new RetryableReadException(
                    $"the range [{offset:N0} +{length:N0}] was ignored and the whole file was sent");

            // How much this response promised. Fewer bytes than that is a
            // connection that ended early, not the end of the file, and handing
            // back the short buffer made the cache believe the file stopped there.
            long promised = response.Content.Headers.ContentLength ?? -1;
            if (RangeHonoured && response.Content.Headers.ContentRange is { From: { } from, To: { } to })
                promised = to - from + 1;

            using var stream = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[length];
            int total = 0;
            while (total < length)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(total, length - total), token);
                if (n <= 0) break;
                total += n;
                arrived();
            }

            if (total < length && promised >= 0 && total < Math.Min(promised, length))
                throw new RetryableReadException(
                    $"the range [{offset:N0} +{length:N0}] ended after {total:N0} of {promised:N0} bytes");

            return total == length ? buffer : buffer[..total];
        }

        /// <summary>
        /// Whether the most recent range read came back as 206 Partial Content.
        ///
        /// Diagnostics only, and deliberately not to be trusted under load: two
        /// ranged reads run at once by design, so "most recent" means whichever
        /// returned last. The harness that uses it issues one read at a time.
        /// </summary>
        public volatile bool RangeHonoured;
    }
}


