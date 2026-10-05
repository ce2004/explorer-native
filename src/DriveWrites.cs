using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// How far an upload has got, for the transfer dialog.
    /// </summary>
    public sealed record UploadProgress(long BytesSent, long BytesTotal)
    {
        public double Percent => BytesTotal <= 0 ? 0 : BytesSent * 100.0 / BytesTotal;
    }

    /// <summary>
    /// The half of Drive that changes things: upload, make a folder, move,
    /// rename, and put in the trash.
    ///
    /// Kept apart from the reading half deliberately. Everything here can lose
    /// somebody's data if it is wrong, and that is worth being able to see in one
    /// file rather than mixed in among the listings.
    /// </summary>
    internal sealed partial class DriveClient
    {
        private const string UploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files";

        /// <summary>
        /// Files at or under this size go up in one request rather than through a
        /// resumable session.
        ///
        /// What this buys is a request, not a round trip's worth of time, and the
        /// difference is worth writing down because the obvious argument is wrong.
        /// Measured, eight 64KB files sent one after another: **17.2 seconds
        /// through multipart and 17.5 through a resumable session**, medians 1345ms
        /// and 1372ms — the same, twice, within the noise of a link whose per-file
        /// times ranged from 1.1 to 2.0 seconds either way. Google's session
        /// creation is cheap and the connection is already open, so the extra
        /// request is nearly free in wall clock.
        ///
        /// It is not free in *requests*, and that is the reason to keep it: a
        /// folder of two thousand small files is two thousand requests rather than
        /// four thousand, against a per-user quota that is counted per minute and
        /// against four streams all spending it at once.
        ///
        /// Above this the session earns its request outright, because what it buys
        /// — picking a broken transfer up where it stopped rather than sending a
        /// gigabyte again — starts to be worth far more than one round trip.
        /// </summary>
        public const long SmallUploadBytes = 5 * 1024 * 1024;

        /// <summary>
        /// How much is moved from the file to the socket at a time.
        ///
        /// Nothing is held: this is the copy buffer between a FileStream and the
        /// request body, not a chunk of the upload. The whole file goes up in one
        /// streamed request, so the only memory an upload of any size costs is
        /// this buffer — a gigabyte and a kilobyte are the same footprint.
        /// </summary>
        public const int UploadBufferBytes = 256 * 1024;

        /// <summary>
        /// How long a request may move no bytes at all before it is treated as a
        /// dead connection.
        ///
        /// The upload client has no timeout of its own, because a legitimate
        /// upload is legitimately long — so this is what stands in for one, and it
        /// is the better question anyway: "has anything happened lately" rather
        /// than "has this taken a while".
        /// </summary>
        private const int UploadStallSeconds = 60;

        /// <summary>
        /// How many times a broken upload is picked up again before it is
        /// reported as a failure. Each attempt asks Google what it actually holds
        /// and carries on from there, so five is five *resumes*, not five
        /// re-uploads.
        /// </summary>
        private const int MaxUploadAttempts = 5;

        /// <summary>
        /// How many times a small upload is sent again after Google answers with
        /// something temporary.
        ///
        /// The resumable path above has had this from the start, and the small
        /// path had nothing at all — which was the wrong way round, because the
        /// small path is the one that runs a million times. A million tiny files
        /// is a million requests, and a service that is right 99.99% of the time
        /// still refuses a hundred of them; every one of those was a file
        /// reported as failed, in a list nobody can read to the end.
        ///
        /// Six, which with the backoff below is about two minutes of trying
        /// before a file is genuinely given up on.
        /// </summary>
        private const int MaxSmallUploadAttempts = 6;

        /// <summary>
        /// Whether an answer from Google is worth sending again.
        ///
        /// Only the ones that mean "not now". 429 is the rate limit, 408 is a
        /// timeout, and 500, 502, 503 and 504 are Google's own transient
        /// failures — the 502 page says "please try again in 30 seconds" in as
        /// many words. Everything else is about the request rather than the
        /// moment: a 403 for quota, a 404 for a parent that has been deleted, a
        /// 401 for a token that has expired. Retrying those is a way of turning
        /// one clear failure into six identical ones and a longer wait.
        /// </summary>
        internal static bool WorthRetrying(int status) =>
            status is 408 or 429 or 500 or 502 or 503 or 504;

        /// <summary>
        /// The same question, with the body in hand — which is the only way to
        /// get 403 right.
        ///
        /// **Google spells rate limiting two ways.** 429 is one of them. The
        /// other is a 403 whose payload carries a `reason` of
        /// `rateLimitExceeded` or `userRateLimitExceeded`, and it is
        /// indistinguishable by status code from the 403 that means "you may not
        /// do that" — which is exactly why the status-only test above refuses
        /// all of them. That refusal is right for a permission error and wrong
        /// for the throttling case, where the correct response is to back off
        /// and come back: told to slow down, the upload instead failed the file
        /// and moved on to hammer Google with the next one.
        ///
        /// Two reasons are deliberately *not* retried even though they arrive
        /// the same way:
        ///
        /// - `dailyLimitExceeded` — the window is a day. Six retries over two
        ///   minutes is not going to reach the other side of it, and pretending
        ///   otherwise turns a clear answer into a slow one.
        /// - `storageQuotaExceeded` / `quotaExceeded` — the account is full.
        ///   That is not a moment, it is a state, and it needs a person.
        /// </summary>
        internal static bool WorthRetrying(int status, string? body)
        {
            if (WorthRetrying(status)) return true;
            if (status != 403 || string.IsNullOrEmpty(body)) return false;

            var reason = ErrorReason(body);
            return reason is "rateLimitExceeded" or "userRateLimitExceeded";
        }

        /// <summary>
        /// The machine-readable <c>reason</c> out of a Google error payload, or
        /// an empty string.
        ///
        /// The human message is no good for deciding anything — it is prose, it
        /// is localised, and it changes. The reason is the part that is a
        /// contract.
        /// </summary>
        internal static string ErrorReason(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";
            if (!body.TrimStart().StartsWith("{", StringComparison.Ordinal)) return "";

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("error", out var error)) return "";

                // The newer shape puts one status at the top; the classic shape
                // has an array of them. Both are still served.
                if (error.TryGetProperty("errors", out var list) &&
                    list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var one in list.EnumerateArray())
                        if (one.TryGetProperty("reason", out var r) &&
                            r.GetString() is { Length: > 0 } said)
                            return said;
                }

                if (error.TryGetProperty("status", out var status) &&
                    status.GetString() is { Length: > 0 } code)
                    return code;
            }
            catch
            {
                // A body that is not the JSON it claimed to be says nothing, and
                // this is already on the path that is reporting a failure.
            }

            return "";
        }

        /// <summary>How long between asks while the network is away.</summary>
        private const int NetworkWaitMilliseconds = 10_000;

        /// <summary>
        /// The longest a single file will sit waiting for the internet before it
        /// is finally called a failure. Two hours, in ten-second asks.
        ///
        /// Not unbounded, and not small. A transfer that gives up after two
        /// minutes of a router rebooting throws away an hour of work for
        /// something that fixed itself; one that waits for ever is a progress
        /// window that says "Working" until somebody notices next week. Two
        /// hours covers every outage that resolves itself and none of the ones
        /// that need a person.
        /// </summary>
        private const int MostNetworkWaits = 720;

        /// <summary>
        /// Waits out a network that has gone away, rather than spending the
        /// file's retries on it. True when it waited, which makes the attempt
        /// free.
        ///
        /// This is the difference between an outage costing a transfer and an
        /// outage costing some time. The retry budget above is sized for a
        /// server that refused once — six tries over about two minutes — and a
        /// dropped connection eats the whole of it in one go. So a thousand-file
        /// upload that met a five-minute outage did not pause: every file still
        /// in flight burned six attempts against a network that was not there,
        /// failed, and the ones behind them did the same, until the run finished
        /// with nine hundred errors and nothing to show for the wait.
        ///
        /// Only once <see cref="DriveHealth"/> agrees, which takes three failures
        /// in a row — one timeout is a hiccup and pausing a transfer for it would
        /// be its own fault.
        ///
        /// The probe is the point of the loop. Health only clears on a request
        /// that *worked*, and while the transfer is stopped here nothing else is
        /// making one — so the wait has to ask, or it would never learn that the
        /// network came back.
        /// </summary>
        private async Task<bool> WaitOutNetwork(Exception error, string what, CancellationToken token)
        {
            if (!DriveHealth.LooksLikeNetwork(error) || !Health.IsOffline) return false;

            Say("drive.offline.waiting",
                $"Waiting for the internet to come back before {what}");

            for (int waited = 0; waited < MostNetworkWaits && Health.IsOffline; waited++)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(NetworkWaitMilliseconds, token);

                // Cheap, and the only thing that can put Health back online.
                try { await Whoami(token); }
                catch { /* still away; the loop asks again */ }
            }

            if (!Health.IsOffline)
            {
                Say("drive.offline.back", "Back online; carrying on");
                return true;
            }

            // Still away after the whole allowance, and saying "true" here is
            // what made that allowance meaningless: both callers read true as
            // "it came back" and un-spend the attempt, so the request was tried
            // again, failed again, and waited another two hours — for ever, on
            // one file, with a progress window that never moved and no way out
            // but Cancel. The documented ceiling has to be able to end.
            return false;
        }

        /// <summary>
        /// How long Google asked us to wait, when it said.
        ///
        /// A 429 or a 503 may carry <c>Retry-After</c>, either as seconds or as
        /// an HTTP date, and that figure is worth more than any backoff worked
        /// out here: it is the server saying when it will be ready, rather than
        /// this end guessing. It was already being read — to put a number in a
        /// sentence — and then ignored when deciding when to actually come back.
        ///
        /// Clamped at five minutes. An honest Retry-After is seconds; a very
        /// large one is either a mistake or an outage, and neither is a reason
        /// for a transfer to sit silent for an hour with a progress window up.
        /// </summary>
        internal static TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            var header = response.Headers.RetryAfter;
            if (header == null) return null;

            TimeSpan? asked = header.Delta;
            if (asked == null && header.Date is { } when)
                asked = when - DateTimeOffset.UtcNow;

            if (asked == null || asked <= TimeSpan.Zero) return null;
            return asked > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : asked;
        }

        /// <summary>
        /// How long to wait before attempt number <paramref name="attempt"/>,
        /// counting from zero.
        ///
        /// Exponential, with a random fraction added. The jitter is not
        /// decoration: an upload runs four streams at once and a folder of small
        /// files puts them all through the same moment of trouble, so a fixed
        /// backoff has all four coming back at the same instant, failing
        /// together, and doing it again — which is the shape that turns a blip
        /// into an outage. Capped, because past half a minute this stops being a
        /// retry and starts being a hang.
        /// </summary>
        internal static TimeSpan RetryDelay(int attempt, Random random) =>
            RetryDelay(attempt, random, null);

        /// <summary>
        /// The same, but honouring what the server asked for when it asked.
        ///
        /// <paramref name="asked"/> wins outright rather than being averaged in
        /// or treated as a floor. It is the one party that knows when it will be
        /// ready, and second-guessing it is how a client that is already being
        /// throttled arranges to be throttled again. A little jitter is still
        /// added on top, because four upload streams all told to wait thirty
        /// seconds would otherwise return in the same instant.
        /// </summary>
        internal static TimeSpan RetryDelay(int attempt, Random random, TimeSpan? asked)
        {
            if (asked is { } wait)
                return wait + TimeSpan.FromMilliseconds(random.NextDouble() * 1000);

            double seconds = Math.Min(30.0, Math.Pow(2, attempt));
            return TimeSpan.FromSeconds(seconds + random.NextDouble() * 0.5 * seconds);
        }

        /// <summary>
        /// A server's error body, cut down to something worth putting in front of
        /// somebody.
        ///
        /// Google answers a 502 with a full HTML page — doctype, stylesheet, a
        /// link to a picture of a robot, about seven hundred characters of it —
        /// and that went into the failure list whole. On screen it pushed every
        /// other failure out of the dialog; read aloud it is a minute of markup
        /// before the one sentence that says what happened.
        ///
        /// A JSON error carries a real message and that is what comes back. HTML
        /// carries nothing worth extracting, so it is named rather than quoted.
        /// </summary>
        internal static string Explain(int status, string body)
        {
            body = (body ?? "").Trim();

            // The three that are worth a sentence of our own, because Google's
            // is either a bare code or prose about "quota" that does not say
            // which quota — and these are the ones where knowing which decides
            // what somebody should do next.
            //
            // They are all 403s that look identical from the outside, and all
            // three used to arrive as "upload failed 403: ..." at the end of a
            // list of nine hundred other failures, which is the worst possible
            // moment to be vague. A daily limit means stop and come back
            // tomorrow; a full account means buy storage or delete something;
            // per-minute throttling means the application is already handling it
            // and there is nothing to do.
            switch (ErrorReason(body))
            {
                case "dailyLimitExceeded":
                    return "Google Drive's daily limit for this account has been reached. " +
                           "It resets about 24 hours after it was hit — nothing is lost, and " +
                           "pasting again then will fill in whatever did not arrive.";

                case "storageQuotaExceeded":
                case "quotaExceeded":
                    return "The Google Drive account is out of storage. Nothing further will " +
                           "upload until space is freed or more is bought.";

                case "rateLimitExceeded":
                case "userRateLimitExceeded":
                    return "Google is limiting how fast this account may upload; waiting and " +
                           "trying again.";
            }

            if (body.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("error", out var error) &&
                        error.TryGetProperty("message", out var message) &&
                        message.GetString() is { Length: > 0 } said)
                        return said;
                }
                catch
                {
                    // Not the JSON it looked like. Fall through to the cap below.
                }
            }

            // An HTML page says nothing a person needs and a great deal they do
            // not. The status code already carries the meaning.
            if (body.StartsWith("<", StringComparison.Ordinal) ||
                body.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
                return status switch
                {
                    502 or 503 => "Google Drive is temporarily unavailable",
                    504 => "Google Drive did not answer in time",
                    _ => "Google Drive returned an error page",
                };

            const int most = 200;
            return body.Length <= most ? body : body[..most] + "…";
        }

        /// <summary>
        /// The client uploads go out on, with no timeout.
        ///
        /// <c>_http</c> has fifteen seconds, deliberately, because it is on the
        /// path of a filesystem callback that is holding somebody up. An upload is
        /// the opposite case: a single streamed request carries the whole file, so
        /// a gigabyte at the measured 13.9MB/s is seventy seconds of a request
        /// that is working perfectly, and a fifteen-second ceiling would fail
        /// every file over about 200MB. What replaces the timeout is
        /// <see cref="UploadStallSeconds"/>, which asks whether bytes are still
        /// moving rather than how long they have been moving for.
        /// </summary>
        private readonly HttpClient _uploads = new() { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>
        /// Moves a file or folder to the Drive trash.
        ///
        /// Not files.delete, which is permanent and immediate. The recycle bin
        /// exists because people change their minds, and a file manager that
        /// deletes irrecoverably from a keystroke is a file manager that will one
        /// day be the reason something is gone.
        /// </summary>
        public async Task Trash(string fileId, CancellationToken token)
        {
            using var request = await Request(HttpMethod.Patch,
                $"{Files}/{fileId}?fields=id", token);
            request.Content = new StringContent("{\"trashed\":true}", Encoding.UTF8, "application/json");

            using var response = await Send(request, HttpCompletionOption.ResponseContentRead, token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"trash failed {(int)response.StatusCode}: " +
                    Explain((int)response.StatusCode, await response.Content.ReadAsStringAsync(token)));
        }

        /// <summary>
        /// The link to a file or folder, and — when asked — the sharing that
        /// makes the link work for somebody who is not signed in as us.
        ///
        /// Two different things, which is why one method with a flag rather than
        /// two commands that look alike:
        ///
        /// - The **link** is `webViewLink`, and it always exists. It is the
        ///   address of the file. Handing it to somebody who has not been given
        ///   access gets them a request-access page, which is exactly right for
        ///   pasting into a message to somebody who already shares the folder.
        /// - **Public** adds a permission — `{"role":"reader","type":"anyone"}` —
        ///   and *that* is what makes the same link open for anyone who has it.
        ///   It is a real change to the account, not a way of writing the URL,
        ///   and it stays until somebody takes it away again.
        ///
        /// The scope this application holds is full Drive, so both are allowed.
        /// A read-only scope would have permitted the first and refused the
        /// second, which is worth knowing if the scope is ever narrowed again.
        ///
        /// `webContentLink` is deliberately not returned. It exists only for
        /// binary files, is absent for anything Google-native, and it is a
        /// download rather than a page — so a "copy link" that sometimes gave one
        /// and sometimes the other would be a command whose result you had to
        /// inspect to know what you had.
        /// </summary>
        public async Task<string> ShareLink(string fileId, bool makePublic, CancellationToken token)
        {
            if (makePublic)
            {
                using var permission = await Request(HttpMethod.Post,
                    $"{Files}/{fileId}/permissions?fields=id", token);
                permission.Content = new StringContent(
                    "{\"role\":\"reader\",\"type\":\"anyone\"}", Encoding.UTF8, "application/json");

                using var granted = await Send(permission, HttpCompletionOption.ResponseContentRead, token);

                // A file that is already shared this way answers 200 with a
                // second permission, not an error, so there is nothing to
                // special-case for "again".
                if (!granted.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"sharing failed {(int)granted.StatusCode}: " +
                        Explain((int)granted.StatusCode, await granted.Content.ReadAsStringAsync(token)));
            }

            using var request = await Request(HttpMethod.Get,
                $"{Files}/{fileId}?fields=webViewLink", token);

            using var response = await Send(request, HttpCompletionOption.ResponseContentRead, token);
            var body = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"link lookup failed {(int)response.StatusCode}: " +
                    Explain((int)response.StatusCode, body));

            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("webViewLink", out var link) &&
                link.GetString() is { Length: > 0 } url)
                return url;

            // Drive gives every file a webViewLink, so this is the shape of an
            // answer changing rather than a file without one. The address is
            // derivable from the id, and a link that works is a better answer
            // than an error about a field name.
            throw new InvalidOperationException("Google Drive did not return a link for this item");
        }

        /// <summary>
        /// Makes a folder, and gives back its id.
        ///
        /// This was written once, called by nothing, and deleted with a comment
        /// saying that uploading a tree means creating every folder in it and
        /// tracking which id each got — which is exactly right, and is now what
        /// <see cref="DriveUpload"/> does. A folder in Drive is a file with a
        /// particular mime type and no content, so this is the metadata half of
        /// an upload with the bytes left out.
        /// </summary>
        public Task<string> CreateFolder(string name, string parentId, CancellationToken token) =>
            CreateEmpty(name, parentId, FolderMimeType, token);

        /// <summary>
        /// Makes an empty file, and gives back its id.
        ///
        /// The same request as a folder with a different mime type, and no body at
        /// all — a file created this way has no content, which is exactly what
        /// "New file" means. Uploading zero bytes would work too and would cost a
        /// second round trip to say nothing.
        /// </summary>
        public Task<string> CreateEmptyFile(string name, string parentId, CancellationToken token,
            DateTime? modified = null) =>
            CreateEmpty(name, parentId, "application/octet-stream", token, modified);

        /// <summary>A time as Drive's metadata takes it, to the millisecond, in UTC.</summary>
        internal static string RfcTime(DateTime when) =>
            when.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// The time an upload is stamped with in Drive and on its placeholder:
        /// the file's own, to the millisecond, which both ends then agree on.
        /// </summary>
        internal static DateTime UploadStamp(string localPath)
        {
            DateTime when;
            try { when = File.GetLastWriteTimeUtc(localPath); }
            catch { when = DateTime.UtcNow; }
            return new DateTime(when.Ticks - when.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        }

        private async Task<string> CreateEmpty(
            string name, string parentId, string mimeType, CancellationToken token, DateTime? modified = null)
        {
            var tag = NewOperationTag();
            var fields = new Dictionary<string, object>
            {
                ["name"] = name,
                ["mimeType"] = mimeType,
                ["parents"] = new[] { RealFolder(parentId) },
                ["appProperties"] = new Dictionary<string, string> { [OperationProperty] = tag },
            };
            if (modified is { } when) fields["modifiedTime"] = RfcTime(when);
            var metadata = JsonSerializer.Serialize(fields);

            // Through the retrying sender, on the two-minute client. Creating a
            // folder is where an upload of a deep tree spends its first minute,
            // and it had no retry and a fifteen-second ceiling — so one slow or
            // temporarily-refused folder took every file underneath it with it,
            // and a timeout ended the whole transfer as a "cancellation".
            using var response = await SendWithRetry(
                async () =>
                {
                    var request = await Request(HttpMethod.Post, $"{Files}?fields=id", token);
                    request.Content = new StringContent(metadata, Encoding.UTF8, "application/json");
                    return request;
                },
                HttpCompletionOption.ResponseContentRead, token,
                t => MadeWithTag(tag, parentId, "id", t));

            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                // The same two that stop a file stop a folder, and rather more
                // urgently: everything underneath it is waiting on this.
                if (DriveQuotaException.From((int)response.StatusCode, body) is { } stop) throw stop;

                throw new InvalidOperationException(
                    $"create failed {(int)response.StatusCode}: " +
                    Explain((int)response.StatusCode, body));
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("id").GetString() ?? "";
        }

        public const string FolderMimeType = "application/vnd.google-apps.folder";

        /// <summary>
        /// Copies a file that is already on the drive, and gives back the new one.
        ///
        /// **No bytes cross this machine.** Drive copies it server-side, so a
        /// four gigabyte video duplicates in about as long as a four kilobyte
        /// note — one request, and the answer is the new file. The application
        /// used to refuse this outright, on a comment saying that "copying inside
        /// Drive means downloading and uploading the same bytes". That was simply
        /// wrong about the API, and the cost of being wrong was a file manager
        /// that told somebody to copy a file out of Drive and back in — several
        /// gigabytes over the link, both ways, to avoid a request that moves
        /// none.
        ///
        /// **It does not copy folders**, and that is Drive's rule rather than a
        /// shortcut taken here: `files.copy` refuses a folder. A folder is copied
        /// by making one and copying what is in it, which is
        /// <see cref="GoogleDrive.CopyOnDrive"/>'s job because it needs the
        /// listing as well.
        /// </summary>
        /// <param name="name">
        /// What to call the copy, or null to keep the original's own name — the
        /// one Drive has, not the one the letter shows cleaned up.
        /// </param>
        public async Task<DriveEntry> Copy(
            string fileId, string? name, string parentId, CancellationToken token)
        {
            var tag = NewOperationTag();
            var fields = new Dictionary<string, object>
            {
                ["parents"] = new[] { RealFolder(parentId) },
                ["appProperties"] = new Dictionary<string, string> { [OperationProperty] = tag },
            };
            if (name != null) fields["name"] = name;
            var metadata = JsonSerializer.Serialize(fields);

            // Through the retrying sender, like every other write that is not a
            // stream of bytes: a copy is cheap for us and expensive for Drive, so
            // it is exactly the request most likely to come back 429 or 503 in a
            // folder being duplicated a file at a time.
            using var response = await SendWithRetry(
                async () =>
                {
                    var request = await Request(HttpMethod.Post,
                        $"{Files}/{fileId}/copy?fields=id,name,mimeType,size,modifiedTime", token);
                    request.Content = new StringContent(metadata, Encoding.UTF8, "application/json");
                    return request;
                },
                HttpCompletionOption.ResponseContentRead, token,
                t => MadeWithTag(tag, parentId, "id,name,mimeType,size,modifiedTime", t));

            var body = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                if (DriveQuotaException.From((int)response.StatusCode, body) is { } stop) throw stop;

                throw new InvalidOperationException(
                    $"copy failed {(int)response.StatusCode}: " +
                    Explain((int)response.StatusCode, body));
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            long size = 0;
            if (root.TryGetProperty("size", out var bytes) &&
                long.TryParse(bytes.GetString(), out long parsed)) size = parsed;

            var modified = root.TryGetProperty("modifiedTime", out var when) &&
                           when.GetString() is { } stamp &&
                           DateTime.TryParse(stamp, null,
                               System.Globalization.DateTimeStyles.AdjustToUniversal, out var at)
                ? at
                : DateTime.UtcNow;

            return new DriveEntry(
                root.GetProperty("id").GetString() ?? "",
                root.TryGetProperty("name", out var named) ? named.GetString() ?? name ?? "" : name ?? "",
                root.TryGetProperty("mimeType", out var mime)
                    ? mime.GetString() ?? "application/octet-stream"
                    : "application/octet-stream",
                size,
                modified);
        }

        /// <summary>
        /// Renames a file, moves it between folders, or both.
        ///
        /// Drive has no move: a file's parents are a property, so moving is
        /// adding one and removing the other in the same request. Doing it as two
        /// requests leaves a window where the file is in both places or neither.
        /// </summary>
        public async Task MoveOrRename(
            string fileId, string? newName, string? fromParentId, string? toParentId,
            CancellationToken token)
        {
            var url = $"{Files}/{fileId}?fields=id";
            if (!string.IsNullOrEmpty(toParentId)) url += $"&addParents={toParentId}";
            if (!string.IsNullOrEmpty(fromParentId)) url += $"&removeParents={fromParentId}";

            using var request = await Request(HttpMethod.Patch, url, token);
            request.Content = new StringContent(
                newName == null
                    ? "{}"
                    : JsonSerializer.Serialize(new Dictionary<string, object> { ["name"] = newName }),
                Encoding.UTF8, "application/json");

            using var response = await Send(request, HttpCompletionOption.ResponseContentRead, token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"move failed {(int)response.StatusCode}: " +
                    Explain((int)response.StatusCode, await response.Content.ReadAsStringAsync(token)));
        }

        /// <summary>
        /// Uploads a local file and gives back the new file id.
        ///
        /// **Nothing is buffered.** The file is opened and handed to the request
        /// body, and the bytes go from the disk to the socket through
        /// <see cref="UploadBufferBytes"/> and nothing else — no temporary copy,
        /// no chunk held in memory, no whole-file array. A gigabyte and a
        /// kilobyte cost the same amount of memory to send.
        ///
        /// It used to go up in 8MB chunks, each read into an array first and each
        /// its own request. That was written for resumability and it paid for it
        /// twice over: an 8MB allocation per upload, and a full round trip's
        /// latency every 8MB — 128 of them for a gigabyte, and the link idle for
        /// most of each one while Google answered. One streamed request has the
        /// socket busy from the first byte to the last, which is as fast as the
        /// connection goes.
        ///
        /// Measured end to end against the real account, 400MB of known bytes,
        /// verified at three offsets including one 1KB from the end:
        /// **21.5 seconds at 18.6MB/s, with a peak working set of 59MB and 3MB of
        /// managed allocation for the whole run.** The chunked version managed
        /// 13.9MB/s and allocated eight megabytes before it started. Sixty
        /// megabytes of process is the runtime; the upload itself is a quarter of
        /// a megabyte of buffer whatever the file weighs.
        ///
        /// Resumability is not given up for it, only moved: if the request breaks
        /// the session is asked how much it actually holds and the stream picks up
        /// from there — see <see cref="UploadResumable"/>. What was a cost on
        /// every upload is now a cost on the ones that go wrong.
        /// </summary>
        public async Task<string> Upload(
            string localPath, string name, string parentId,
            Action<UploadProgress>? progress, CancellationToken token,
            string? resumeSession = null, Action<string>? sessionStarted = null)
        {
            long total;
            try { total = new FileInfo(localPath).Length; }
            catch { total = 0; }

            // Small enough that a resumable session would spend more time asking
            // for one than sending the file. A zero-length file lands here too,
            // and goes up as one request with an empty body.
            if (total <= SmallUploadBytes)
                return await UploadWhole(localPath, name, parentId, total, progress, token);

            return await UploadResumable(localPath, name, parentId, total, progress, token,
                resumeSession, sessionStarted);
        }

        /// <summary>
        /// A parent id that is a real Drive folder, or the refusal that says why
        /// not. Nothing can be made directly inside the list of shared drives.
        /// </summary>
        private static string RealFolder(string parentId) =>
            parentId == SharedDrivesId
                ? throw new InvalidOperationException(
                    "Nothing can be put directly into \"Shared drives\"; open one of the shared drives first")
                : parentId;

        /// <summary>The metadata half of an upload: what to call it and where.</summary>
        private static string MetadataJson(string name, string parentId, string? tag = null,
            DateTime? modified = null)
        {
            var metadata = new Dictionary<string, object>
            {
                ["name"] = name,
                ["parents"] = new[] { RealFolder(parentId) },
            };

            // Set rather than left to Drive: the placeholder is placed with the
            // same time, so the next listing does not read a newly sent file as
            // changed contents — and the file keeps the date it had here.
            if (modified is { } when)
                metadata["modifiedTime"] = RfcTime(when);
            if (tag != null)
                metadata["appProperties"] = new Dictionary<string, string> { [OperationProperty] = tag };
            return JsonSerializer.Serialize(metadata);
        }

        /// <summary>
        /// Opened for streaming: sequential, asynchronous, and shared, because
        /// the file being read is somebody's and may well be open elsewhere.
        /// </summary>
        private static FileStream OpenRead(string localPath) =>
            new(localPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, UploadBufferBytes,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

        /// <summary>
        /// One request, metadata and bytes together, for a file small enough that
        /// a second round trip would be most of the cost of sending it.
        /// </summary>
        private async Task<string> UploadWhole(
            string localPath, string name, string parentId, long total,
            Action<UploadProgress>? progress, CancellationToken token)
        {
            // Sent again when Google says "not now", which it does — this is one
            // request per file and a folder of small files is a great many of
            // them. There was no retry here at all, and a real upload of a
            // million tiny files lost one to a 502 whose own body reads "please
            // try again in 30 seconds": a file reported as permanently failed on
            // the strength of an answer that said the opposite.
            //
            // Whole-request retry rather than resumption, and it is right here
            // for the reason the resumable path exists at all: five megabytes is
            // a second or two of sending, so starting again costs less than
            // asking Google how much it kept.
            var random = new Random();
            string lastProblem = "";

            // What Google asked us to wait, when it said. Carried from the
            // answer that refused us to the top of the next attempt.
            TimeSpan? asked = null;

            // Whether an attempt may have landed with its answer lost — a
            // timeout, a reset, a 5xx. Sending again then makes a second file
            // of the same name, so Drive is asked first, by the tag every
            // attempt carries. See SendWithRetry.
            var tag = NewOperationTag();
            bool maybeDone = false;

            async Task<string?> AlreadyUploaded()
            {
                if (!maybeDone) return null;

                var found = await AskWhetherLanded(t => MadeWithTag(tag, parentId, "id,size", t), random, token);
                if (found == null) return null;

                using var doc = JsonDocument.Parse(found);
                var file = doc.RootElement;
                bool whole = !file.TryGetProperty("size", out var size) ||
                             (long.TryParse(size.GetString(), out long bytes) && bytes == total);
                if (!whole) return null;

                progress?.Invoke(new UploadProgress(total, total));
                return file.GetProperty("id").GetString() ?? "";
            }

            for (int attempt = 0; attempt < MaxSmallUploadAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();

                if (attempt > 0)
                    await Task.Delay(RetryDelay(attempt - 1, random, asked), token);

                if (await AlreadyUploaded() is { } uploaded) return uploaded;

                // Opened inside the loop. A stream that has already been walked
                // to the end cannot be sent again, and neither can an
                // HttpRequestMessage that has been sent once — reusing either is
                // a retry that fails for a reason that has nothing to do with
                // the failure it is retrying.
                using var source = OpenRead(localPath);

                using var request = await Request(HttpMethod.Post,
                    $"{UploadEndpoint}?uploadType=multipart&fields=id", token);

                var metadata = new StringContent(
                    MetadataJson(name, parentId, tag, UploadStamp(localPath)), Encoding.UTF8, "application/json");

                var counter = new ProgressStream(source, 0, total, progress);
                var body = new StreamContent(counter, UploadBufferBytes);
                body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                body.Headers.ContentLength = total;

                var multipart = new MultipartContent("related") { metadata, body };
                request.Content = multipart;

                int status;
                string text;
                try
                {
                    using var response = await SendWatched(request, counter, token);
                    status = (int)response.StatusCode;
                    text = await response.Content.ReadAsStringAsync(token);

                    if (response.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(text);
                        var id = doc.RootElement.GetProperty("id").GetString() ?? "";
                        progress?.Invoke(new UploadProgress(total, total));
                        return id;
                    }

                    // Read before the response is disposed, and only useful on
                    // the answers that carry it — which are exactly the ones we
                    // are about to wait on.
                    asked = RetryAfter(response);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    asked = null;
                    lastProblem = ex.Message;
                    maybeDone = true;

                    // The network went away. Waited out rather than charged to
                    // this file's six attempts — otherwise a five-minute outage
                    // fails every file that happened to be in flight, and then
                    // every file behind them.
                    if (await WaitOutNetwork(ex, $"sending {name}", token))
                    {
                        attempt--;
                        continue;
                    }

                    // A stalled request or a refusal, which is the same kind of
                    // "not now" as a 502 and is worth the same answer. The last
                    // attempt rethrows rather than swallowing it, so a genuine
                    // failure still says what it was.
                    if (attempt == MaxSmallUploadAttempts - 1)
                    {
                        if (await AlreadyUploaded() is { } landedAfterAll) return landedAfterAll;
                        throw;
                    }

                    Say("drive.upload.retrying",
                        $"Retrying {name}: {lastProblem}");
                    continue;
                }

                lastProblem = Explain(status, text);

                // Out of storage, or done for the day. Neither is going to
                // change while nine hundred more files ask the same question, so
                // this one stops the transfer instead of joining a list.
                if (DriveQuotaException.From(status, text) is { } stop) throw stop;

                // The body, not just the status. A 403 whose reason is
                // rateLimitExceeded is Google saying "slow down" in its other
                // voice, and refusing to retry it meant the upload answered a
                // request to slow down by failing the file and immediately
                // asking for the next one.
                if (!WorthRetrying(status, text))
                    throw new InvalidOperationException($"upload failed {status}: {lastProblem}");

                if (status == 408 || status >= 500) maybeDone = true;

                if (attempt == MaxSmallUploadAttempts - 1) break;

                // Said, because otherwise a retry is invisible: the file simply
                // takes a minute longer than it should and nothing accounts for
                // it. Off by default, like everything else in this group.
                Say("drive.upload.retrying", $"Retrying {name}: {lastProblem}");
            }

            if (await AlreadyUploaded() is { } landedLast) return landedLast;
            throw new InvalidOperationException(
                $"upload of {name} failed after {MaxSmallUploadAttempts} attempts: {lastProblem}");
        }

        /// <summary>
        /// A resumable session, streamed in one request, and picked back up from
        /// Google's own byte count whenever that request breaks.
        ///
        /// The loop is the resume, not the sending. On the ordinary path it runs
        /// exactly once: open the session, stream the file, done. Every further
        /// pass has already asked Google how much it holds and is starting from
        /// there — which is the one number that may be trusted, and it may
        /// legitimately point *backwards* when a request was half received.
        /// </summary>
        private async Task<string> UploadResumable(
            string localPath, string name, string parentId, long total,
            Action<UploadProgress>? progress, CancellationToken token,
            string? resumeSession = null, Action<string>? sessionStarted = null)
        {
            // A session saved by the folder monitor before the connection went
            // (or the app closed) is picked up where Google says it stopped;
            // every new session is handed back so it can be saved the same way.
            var session = resumeSession ?? await BeginUpload(name, parentId, total, UploadStamp(localPath), token);
            if (resumeSession == null) sessionStarted?.Invoke(session);

            using var source = OpenRead(localPath);
            long sent = 0;
            string? lastProblem = null;
            var random = new Random();

            // Whether Google's count has to be asked for before anything more is
            // sent: after a request broke and the question itself could not be
            // answered, carrying on from our own figure is the one thing not to do.
            bool askFirst = resumeSession != null;

            for (int attempt = 0; attempt < MaxUploadAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();

                // Spaced out. Going straight back into a connection that has just
                // broken is how five resumes are spent in a second.
                if (attempt > 0) await Task.Delay(RetryDelay(attempt - 1, random), token);

                if (askFirst)
                {
                    try
                    {
                        var (held, heldId, heldCount) = await QuerySession(session, total, token);
                        if (held)
                        {
                            progress?.Invoke(new UploadProgress(total, total));
                            return heldId;
                        }
                        sent = ConfirmedOffset(sent, heldCount, total);
                        askFirst = false;

                        // The resume actually happening, which is invisible
                        // otherwise.
                        Say("drive.upload.resumed",
                            $"Resuming {name} from {SizeFormatter.Format(sent, Units)}");
                        progress?.Invoke(new UploadProgress(sent, total));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (UploadRefusedException refused) when (refused.SessionGone)
                    {
                        session = await BeginUpload(name, parentId, total, UploadStamp(localPath), token); sessionStarted?.Invoke(session);
                        sent = 0;
                        askFirst = false;
                        lastProblem = "the upload session expired and was started again";
                    }
                    catch (UploadRefusedException refused) when (!refused.Retryable)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastProblem = ex.Message;
                        continue;
                    }
                }

                // Every byte is accounted for and Google still has not said so,
                // which happens when the answer to the last request was lost on
                // the way home. There is nothing left to send — a zero-length
                // range is not a request Drive has a meaning for — so the session
                // is asked outright.
                if (sent >= total)
                {
                    try
                    {
                        var (done, doneId, _) = await QuerySession(session, total, token);
                        if (done)
                        {
                            progress?.Invoke(new UploadProgress(total, total));
                            return doneId;
                        }

                        // It holds everything and calls the upload unfinished.
                        // Nothing further can be sent, so start the bytes again
                        // rather than loop sending nothing.
                        sent = 0;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (UploadRefusedException refused) when (refused.SessionGone)
                    {
                        session = await BeginUpload(name, parentId, total, UploadStamp(localPath), token); sessionStarted?.Invoke(session);
                        sent = 0;
                        lastProblem = "the upload session expired and was started again";
                    }
                    catch (UploadRefusedException refused) when (!refused.Retryable)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // The question failed, not the upload: asked again on the
                        // next pass, which is what the attempts are for.
                        lastProblem = ex.Message;
                        continue;
                    }
                }

                // The file has to be rewound to match Google's count, not ours.
                //
                // Trusting the count alone fixes the *label* on the next request
                // and leaves the *source* where it was: the reader has already
                // walked past the bytes Google says it never received, so the
                // rest of the file would land at an offset nothing checks. A file
                // of exactly the right length, with a hole in it, reported as a
                // successful upload — silent, and only on a connection that drops
                // mid-request, which is the one case this path exists for.
                if (source.Position != sent) source.Position = sent;

                long before = sent;
                try
                {
                    var (finished, id, confirmed) =
                        await SendRange(session, source, sent, total, progress, token);

                    if (finished)
                    {
                        progress?.Invoke(new UploadProgress(total, total));
                        return id;
                    }

                    sent = ConfirmedOffset(before, confirmed, total);
                    if (sent <= before)
                        lastProblem = "Google confirmed no further bytes";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (UploadRefusedException refused) when (refused.SessionGone)
                {
                    // Sessions last about a week and can be dropped sooner. The
                    // bytes Google had are gone with it; a new session starts from
                    // nothing, which is the only offset it can agree to.
                    lastProblem = refused.Message;
                    session = await BeginUpload(name, parentId, total, UploadStamp(localPath), token); sessionStarted?.Invoke(session);
                    sent = 0;
                    continue;
                }
                catch (UploadRefusedException refused) when (!refused.Retryable)
                {
                    // A full account, a daily limit, a parent that has gone — not a
                    // moment but a state. Sending the rest of the file again five
                    // times cannot change it and costs five times the bandwidth.
                    // The account-wide ones stop the whole upload, as they do for
                    // a small file, rather than failing every file after it.
                    if (DriveQuotaException.From(refused.Status, refused.Body) is { } stop) throw stop;
                    throw;
                }
                catch (Exception ex)
                {
                    lastProblem = ex.Message;

                    // How much of it actually arrived, asked of the only party
                    // that knows — on the next pass, after the pause, so that a
                    // connection that is down for a moment does not fail the
                    // question as well as the request.
                    sent = before;
                    askFirst = true;
                    continue;
                }

                // The resume actually happening, which is invisible otherwise —
                // the upload simply takes longer than the byte count says it
                // should.
                if (sent < total)
                    Say("drive.upload.resumed",
                        $"Resuming {name} from {SizeFormatter.Format(sent, Units)}");

                progress?.Invoke(new UploadProgress(sent, total));
            }

            throw new InvalidOperationException(
                $"upload stopped at {SizeFormatter.Format(sent, Units)} of " +
                $"{SizeFormatter.Format(total, Units)} after {MaxUploadAttempts} attempts" +
                (lastProblem == null ? "" : $": {lastProblem}"));
        }

        /// <summary>
        /// Where the next chunk starts, given what Google says it is holding.
        ///
        /// Its figure is normally the one to trust — that is what makes the
        /// upload resumable, and it may legitimately point *backwards* when a
        /// chunk was only half received. What it may not do is send the walk
        /// somewhere impossible, so two answers are refused: a negative offset
        /// keeps our own place, and one past the end of the file is the end of
        /// the file. Neither should ever arrive; both would otherwise be written
        /// into somebody's file at an offset nothing checks.
        /// </summary>
        internal static long ConfirmedOffset(long sentBefore, long confirmed, long total)
        {
            if (confirmed < 0) return sentBefore;
            return confirmed > total ? total : confirmed;
        }

        /// <summary>
        /// Starts a resumable session and returns the URI to send bytes to.
        /// </summary>
        private async Task<string> BeginUpload(string name, string parentId, long size, DateTime modified,
            CancellationToken token)
        {
            // Through the retrying sender. Opening a session makes nothing, so a
            // resend is safe; without it one 429 or dropped connection failed
            // every file over five megabytes, which the small-file path had
            // stopped doing long ago.
            var metadata = MetadataJson(name, parentId, modified: modified);
            using var response = await SendWithRetry(
                async () =>
                {
                    var request = await Request(HttpMethod.Post,
                        $"{UploadEndpoint}?uploadType=resumable&fields=id", token);
                    request.Content = new StringContent(metadata, Encoding.UTF8, "application/json");
                    request.Headers.Add("X-Upload-Content-Length",
                        size.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return request;
                },
                HttpCompletionOption.ResponseContentRead, token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(token);
                if (DriveQuotaException.From((int)response.StatusCode, body) is { } stop) throw stop;
                throw new InvalidOperationException(
                    $"upload session failed {(int)response.StatusCode}: " +
                    Explain((int)response.StatusCode, body));
            }

            var location = response.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(location) &&
                response.Headers.TryGetValues("Location", out var values))
                foreach (var v in values) { location = v; break; }

            if (string.IsNullOrEmpty(location))
                throw new InvalidOperationException("upload session gave no Location");

            return location;
        }

        /// <summary>
        /// Everything from <paramref name="offset"/> to the end of the file, in
        /// one streamed request. Returns whether the upload is complete, the file
        /// id when it is, and how many bytes Google confirms it now holds.
        /// </summary>
        private async Task<(bool Finished, string Id, long Confirmed)> SendRange(
            string session, Stream source, long offset, long total,
            Action<UploadProgress>? progress, CancellationToken token)
        {
            long length = total - offset;

            using var request = new HttpRequestMessage(HttpMethod.Put, session);

            var counter = new ProgressStream(source, offset, total, progress);
            var content = new StreamContent(counter, UploadBufferBytes);

            // The session URI carries its own authorisation; a bearer token is
            // neither needed nor wanted on the body.
            content.Headers.ContentLength = length;
            content.Headers.ContentRange = new ContentRangeHeaderValue(offset, total - 1, total);
            request.Content = content;

            using var response = await SendWatched(request, counter, token);
            return await ReadUploadAnswer(response, token);
        }

        /// <summary>
        /// Asks a session how much it holds, without sending anything.
        ///
        /// "Content-Range: bytes */total" with an empty body is Google's spelling
        /// of the question, and its answer is the only trustworthy figure there
        /// is after a request has broken. It may also come back as a completed
        /// upload, which is the case worth catching: the last request can be cut
        /// off on the way home, after every byte arrived.
        /// </summary>
        private async Task<(bool Finished, string Id, long Confirmed)> QuerySession(
            string session, long total, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, session);
            var content = new ByteArrayContent(Array.Empty<byte>());
            content.Headers.ContentLength = 0;
            content.Headers.ContentRange = new ContentRangeHeaderValue(total);
            request.Content = content;

            // On the ordinary client, with its fifteen seconds, because this
            // carries no bytes and is reached on the path where something has
            // already gone wrong. The upload client's lack of a timeout is for
            // requests that are legitimately long; a question with an empty body
            // that has not been answered in fifteen seconds is a dead connection,
            // and waiting on it for ever is how a failed upload becomes a hang.
            using var response = await Send(request, HttpCompletionOption.ResponseContentRead, token);
            return await ReadUploadAnswer(response, token);
        }

        /// <summary>
        /// What a session said, whichever request it was answering.
        ///
        /// The byte count is Google's and only Google's. Ours is what was read off
        /// the disk, which after a broken request is exactly the number that is
        /// wrong — and carrying on from it writes the rest of the file at an
        /// offset nothing checks.
        /// </summary>
        private static async Task<(bool Finished, string Id, long Confirmed)> ReadUploadAnswer(
            HttpResponseMessage response, CancellationToken token)
        {
            // 308 is "keep going", and is the normal answer to anything but a
            // request that completed the file. It is not an error however much it
            // looks like one.
            if ((int)response.StatusCode == 308)
            {
                // No Range header means it is holding nothing, which is what
                // Google's own documentation says and is also the safe reading:
                // starting again from zero costs bandwidth, and starting from a
                // number it never confirmed costs the file.
                long confirmed = 0;
                var range = response.Headers.TryGetValues("Range", out var r) ? FirstOf(r) : null;

                // "bytes=0-8388607" — the last byte held, so the next offset is
                // one past it.
                if (range != null)
                {
                    int dash = range.LastIndexOf('-');
                    if (dash > 0 && long.TryParse(range[(dash + 1)..], out long last))
                        confirmed = last + 1;
                }

                return (false, "", confirmed);
            }

            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new UploadRefusedException((int)response.StatusCode, body);

            using var doc = JsonDocument.Parse(body);
            return (true, doc.RootElement.GetProperty("id").GetString() ?? "", -1);
        }

        /// <summary>
        /// A resumable session's answer that was not a success, kept with its
        /// status so the upload can tell "try again" from "stop" from "the
        /// session has gone".
        /// </summary>
        internal sealed class UploadRefusedException : InvalidOperationException
        {
            public int Status { get; }
            public string Body { get; }

            public UploadRefusedException(int status, string body)
                : base($"upload failed {status}: " + Explain(status, body))
            {
                Status = status;
                Body = body;
            }

            /// <summary>404 or 410: the session URI no longer means anything.</summary>
            public bool SessionGone => Status is 404 or 410;

            public bool Retryable => WorthRetrying(Status, Body);
        }

        /// <summary>
        /// Sends an upload request with a watchdog on it instead of a timeout.
        ///
        /// The upload client has none, because a legitimate upload is legitimately
        /// long — so what stands in for one is the question a timeout cannot ask:
        /// are bytes still moving? The stream counts them, and a connection that
        /// has stopped feeding for <see cref="UploadStallSeconds"/> is abandoned
        /// as a dead one, with a sentence saying so rather than the
        /// indistinguishable "the operation was canceled" a token would produce.
        /// </summary>
        private async Task<HttpResponseMessage> SendWatched(
            HttpRequestMessage request, ProgressStream counter, CancellationToken token)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);

            counter.Touch();
            using var watchdog = new Timer(_ =>
            {
                if (Environment.TickCount64 - counter.LastActivity < UploadStallSeconds * 1000L) return;
                try { stall.Cancel(); } catch { }
            }, null, 5000, 5000);

            try
            {
                return await Send(request, HttpCompletionOption.ResponseContentRead, stall.Token, _uploads);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Ours, not theirs. Reported as what it is, because "canceled" on
                // a screen belonging to somebody who cancelled nothing is the
                // least useful sentence available.
                throw new IOException(
                    $"the connection sent nothing for {UploadStallSeconds} seconds");
            }
        }

        private static string? FirstOf(IEnumerable<string> values)
        {
            foreach (var v in values) return v;
            return null;
        }

        /// <summary>
        /// A file being read into a request body, counting what goes past.
        ///
        /// This is the whole of "stream it": the request's content is this, the
        /// content length is known up front, and HttpClient pulls from it straight
        /// into the socket. Nothing accumulates — the only memory an upload of any
        /// size costs is whatever buffer the copy uses.
        ///
        /// It also carries the clock the stall watchdog reads. The right place for
        /// it is here because this is the only part of an upload that knows a byte
        /// moved; everything above it only knows a request has not returned yet.
        /// </summary>
        private sealed class ProgressStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _base;
            private readonly long _total;
            private readonly Action<UploadProgress>? _progress;
            private long _read;

            /// <summary>When a byte last moved, as <c>Environment.TickCount64</c>.</summary>
            public long LastActivity;

            public ProgressStream(Stream inner, long start, long total, Action<UploadProgress>? progress)
            {
                _inner = inner;
                _base = start;
                _total = total;
                _progress = progress;
                LastActivity = Environment.TickCount64;
            }

            public void Touch() => LastActivity = Environment.TickCount64;

            private void Advance(int n)
            {
                if (n <= 0) return;
                _read += n;
                LastActivity = Environment.TickCount64;
                _progress?.Invoke(new UploadProgress(_base + _read, _total));
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken token = default)
            {
                int n = await _inner.ReadAsync(buffer, token);
                Advance(n);
                return n;
            }

            public override Task<int> ReadAsync(
                byte[] buffer, int offset, int count, CancellationToken token) =>
                ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = _inner.Read(buffer, offset, count);
                Advance(n);
                return n;
            }

            public override bool CanRead => true;

            // Not seekable on purpose. HttpClient rewinds a seekable body to retry
            // it, and a rewind here would send bytes this has already counted —
            // a progress bar that goes backwards over a request nobody was told
            // about. Retrying is the resumable session's job, from Google's own
            // byte count.
            public override bool CanSeek => false;
            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => _read;
                set => throw new NotSupportedException();
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            // The file belongs to the caller, which opened it and will close it —
            // frequently to send the *next* range through a fresh wrapper.
            // Disposing it here would close the file underneath the resume.
            protected override void Dispose(bool disposing) { }
        }
    }
}
