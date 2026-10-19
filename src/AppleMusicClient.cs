using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>One row in an Apple Music listing: a song to play, or something to open.</summary>
    internal sealed class AppleMusicItem
    {
        /// <summary>song, album, playlist, artist, station, or a folder of ours ("node").</summary>
        public string Kind = "";
        public string Id = "";
        public string Name = "";
        /// <summary>Artist for songs and albums, curator for playlists. Blank when there is none.</summary>
        public string Subtitle = "";
        public bool IsLibrary;
        /// <summary>The catalogue id of a library song, which is what plays it. Blank for uploads.</summary>
        public string CatalogId = "";
        public long DurationMs;
        /// <summary>For our own folders: the API path that lists them.</summary>
        public string ListPath = "";
        public bool IsLive;
        public string AlbumName = "";
        /// <summary>yyyy-mm-dd, or blank. Compared as text.</summary>
        public string ReleaseDate = "";
        /// <summary>ISO time, or blank (library songs don't carry one; see AddedRank).</summary>
        public string DateAdded = "";
        public string LastModified = "";
        /// <summary>Place in a library list Apple gave newest-added first, or -1.</summary>
        public int AddedRank = -1;
        public int TrackNumber;
        public int DiscNumber;

        public bool IsSong => Kind == "song";
    }

    /// <summary>
    /// Apple Music's web API, signed in with the media-user-token cookie that
    /// music.apple.com sets (the one gamdl reads from cookies.txt). The
    /// developer token comes from the web player's own script, as gamdl does.
    /// Everything goes through GoogleNet's connection pool, like the rest of
    /// the app's online work.
    /// </summary>
    internal sealed class AppleMusicClient
    {
        private const string AmpApi = "https://amp-api.music.apple.com";
        private const string Home = "https://music.apple.com";

        private static readonly HttpClient Http = new(GoogleNet.Handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        private readonly string _userToken;
        private string? _developerToken;
        private string? _storefront;
        private readonly SemaphoreSlim _startGate = new(1, 1);

        public AppleMusicClient(string mediaUserToken) => _userToken = mediaUserToken.Trim();

        public string Storefront => _storefront ?? "us";

        /// <summary>
        /// The media-user-token in a Netscape cookies.txt (what browser
        /// extensions export and gamdl reads), or null when it has none.
        /// A pasted bare token is returned as it is.
        /// </summary>
        public static string? TokenFromCookies(string text)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal)) line = line.Substring(10);
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split('\t');
                if (parts.Length >= 7 && parts[5] == "media-user-token"
                    && parts[0].EndsWith("music.apple.com", StringComparison.OrdinalIgnoreCase)
                    && parts[6].Trim().Length > 0)
                    return parts[6].Trim();
            }
            var bare = text.Trim();
            if (bare.Length > 40 && !bare.Contains('\t') && !bare.Contains('\n') && !bare.Contains(' '))
                return bare;
            return null;
        }

        /// <summary>Signs in: developer token, then the account (which says the storefront). Throws AppleMusicException.</summary>
        public async Task StartAsync(CancellationToken token)
        {
            await _startGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_developerToken != null && _storefront != null) return;
                _developerToken ??= await DeveloperTokenAsync(token).ConfigureAwait(false);
                using var doc = await GetAsync("/v1/me/account?meta=subscription", token).ConfigureAwait(false);
                var sub = doc.RootElement.TryGetProperty("meta", out var meta) && meta.TryGetProperty("subscription", out var s) ? s : default;
                if (sub.ValueKind == JsonValueKind.Object)
                {
                    if (sub.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.False)
                        throw new AppleMusicException("Your Apple Music subscription isn't active.");
                    if (sub.TryGetProperty("storefront", out var sf) && sf.ValueKind == JsonValueKind.String)
                        _storefront = sf.GetString();
                }
                _storefront ??= "us";
            }
            finally { _startGate.Release(); }
        }

        private static async Task<string> DeveloperTokenAsync(CancellationToken token)
        {
            string page;
            try { page = await Http.GetStringAsync(Home, token).ConfigureAwait(false); }
            catch (HttpRequestException ex) { throw new AppleMusicException("Apple Music couldn't be reached. " + GoogleNet.Reason(ex)); }
            var script = Regex.Match(page, "/(assets/index[~-][^/\"]+\\.js)");
            if (!script.Success) throw new AppleMusicException("Apple Music's web player has changed, and the app can't sign in to it.");
            string js;
            try { js = await Http.GetStringAsync(Home + "/" + script.Groups[1].Value, token).ConfigureAwait(false); }
            catch (HttpRequestException ex) { throw new AppleMusicException("Apple Music couldn't be reached. " + GoogleNet.Reason(ex)); }
            var jwt = Regex.Match(js, "\"(eyJ[A-Za-z0-9\\-_]+\\.eyJ[A-Za-z0-9\\-_]+\\.[A-Za-z0-9\\-_]+)\"");
            if (!jwt.Success) throw new AppleMusicException("Apple Music's web player has changed, and the app can't sign in to it.");
            return jwt.Groups[1].Value;
        }

        private HttpRequestMessage Request(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _developerToken);
            req.Headers.TryAddWithoutValidation("Origin", Home);
            req.Headers.TryAddWithoutValidation("Cookie", "media-user-token=" + _userToken);
            req.Headers.TryAddWithoutValidation("media-user-token", _userToken);
            return req;
        }

        private async Task<JsonDocument> GetAsync(string path, CancellationToken token)
        {
            using var req = Request(HttpMethod.Get, AmpApi + path);
            return await SendAsync(req, token).ConfigureAwait(false);
        }

        private static async Task<JsonDocument> SendAsync(HttpRequestMessage req, CancellationToken token)
        {
            HttpResponseMessage resp;
            try { resp = await Http.SendAsync(req, token).ConfigureAwait(false); }
            catch (HttpRequestException ex) { throw new AppleMusicException("Apple Music couldn't be reached. " + GoogleNet.Reason(ex)); }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { throw new AppleMusicException("Apple Music took too long to answer."); }
            using (resp)
            {
                var body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new AppleMusicException("Apple Music didn't accept your sign-in. Set it up again in Preferences, Apple Music.", signIn: true);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    throw new AppleMusicException("Apple Music says that isn't there any more.");
                if (!resp.IsSuccessStatusCode)
                    throw new AppleMusicException($"Apple Music answered with an error ({(int)resp.StatusCode}).");
                try { return JsonDocument.Parse(body); }
                catch (JsonException) { throw new AppleMusicException("Apple Music sent back something the app can't read."); }
            }
        }

        // ---- Listings ----

        /// <summary>Every item at an API path, following "next" up to <paramref name="most"/>.</summary>
        public async Task<List<AppleMusicItem>> ListAsync(string path, CancellationToken token, int most = 2000)
        {
            await StartAsync(token).ConfigureAwait(false);
            var items = new List<AppleMusicItem>();
            string? next = path.Replace("{sf}", Storefront);
            while (next != null && items.Count < most)
            {
                using var doc = await GetAsync(next, token).ConfigureAwait(false);
                next = null;
                var root = doc.RootElement;
                if (root.TryGetProperty("results", out var results))
                {
                    // Search: one block per type, in the order asked for.
                    foreach (var block in results.EnumerateObject())
                        if (block.Value.TryGetProperty("data", out var data))
                            foreach (var d in data.EnumerateArray()) Add(items, d);
                }
                else if (root.TryGetProperty("data", out var data))
                {
                    foreach (var d in data.EnumerateArray())
                    {
                        // A view of an artist or a chart wraps its items.
                        if (d.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Array)
                            foreach (var x in inner.EnumerateArray()) Add(items, x);
                        else Add(items, d);
                    }
                }
                if (root.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    // Apple's "next" drops the page size that was asked for.
                    var s = n.GetString()!;
                    var limit = Regex.Match(path, "[?&](limit=\\d+)");
                    next = limit.Success && !s.Contains("limit=") ? s + (s.Contains('?') ? "&" : "?") + limit.Groups[1].Value : s;
                }
            }
            return items;
        }

        /// <summary>
        /// Everything at a library or catalogue path. The first page says how
        /// many there are; the rest are then asked for eight at a time, so a
        /// library of ten thousand songs takes seconds rather than a minute.
        /// </summary>
        public async Task<List<AppleMusicItem>> ListAllAsync(string path, CancellationToken token, int most = 50000)
        {
            await StartAsync(token).ConfigureAwait(false);
            path = path.Replace("{sf}", Storefront);
            var first = await PageAsync(path, token).ConfigureAwait(false);
            if (first.Next == null) return first.Items;
            if (first.Total <= first.Items.Count || first.Items.Count == 0)
                return await ListAsync(path, token, most).ConfigureAwait(false);

            int pageSize = first.Items.Count;
            int total = Math.Min(first.Total, most);
            var offsets = new List<int>();
            for (int o = pageSize; o < total; o += pageSize) offsets.Add(o);
            var pages = new List<AppleMusicItem>[offsets.Count];
            using var gate = new SemaphoreSlim(8);
            string sep = path.Contains('?') ? "&" : "?";
            await Task.WhenAll(offsets.Select(async (offset, index) =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try { pages[index] = (await PageAsync($"{path}{sep}offset={offset}", token).ConfigureAwait(false)).Items; }
                finally { gate.Release(); }
            })).ConfigureAwait(false);
            var all = new List<AppleMusicItem>(total);
            all.AddRange(first.Items);
            foreach (var page in pages) all.AddRange(page);
            return all;
        }

        private async Task<(List<AppleMusicItem> Items, string? Next, int Total)> PageAsync(string path, CancellationToken token)
        {
            using var doc = await GetAsync(path, token).ConfigureAwait(false);
            var items = new List<AppleMusicItem>();
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data))
                foreach (var d in data.EnumerateArray()) Add(items, d);
            string? next = root.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            int total = root.TryGetProperty("meta", out var meta) && meta.TryGetProperty("total", out var t) && t.TryGetInt32(out var tv) ? tv : -1;
            return (items, next, total);
        }

        /// <summary>Your own station first, then the stations you played lately, then Apple's live radio.</summary>
        public async Task<List<AppleMusicItem>> StationsAsync(CancellationToken token)
        {
            await StartAsync(token).ConfigureAwait(false);
            var all = new List<AppleMusicItem>();
            async Task Try(string path)
            {
                try { all.AddRange(await ListAsync(path, token, most: 30).ConfigureAwait(false)); }
                catch (AppleMusicException ex) when (!ex.SignIn) { }
            }
            await Try("/v1/catalog/{sf}/stations?filter[identity]=personal");
            await Try("/v1/me/recent/radio-stations?limit=10");
            await Try("/v1/catalog/{sf}/stations?filter[featured]=apple-music-live-radio");
            var seen = new HashSet<string>();
            return all.Where(s => s.Kind == "station" && seen.Add(s.Id)).ToList();
        }

        private static void Add(List<AppleMusicItem> items, JsonElement d)
        {
            var item = Parse(d);
            if (item != null) items.Add(item);
        }

        internal static AppleMusicItem? Parse(JsonElement d)
        {
            string type = d.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            string id = d.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            if (!d.TryGetProperty("attributes", out var a) || id.Length == 0) return null;
            string Str(string name) => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

            var item = new AppleMusicItem
            {
                Id = id, Name = Str("name"), IsLibrary = type.StartsWith("library-", StringComparison.Ordinal),
                AlbumName = Str("albumName"), ReleaseDate = Str("releaseDate"),
                DateAdded = Str("dateAdded"), LastModified = Str("lastModifiedDate"),
            };
            if (a.TryGetProperty("trackNumber", out var tn) && tn.TryGetInt32(out var tnv)) item.TrackNumber = tnv;
            if (a.TryGetProperty("discNumber", out var dn) && dn.TryGetInt32(out var dnv)) item.DiscNumber = dnv;
            string kind = item.IsLibrary ? type.Substring(8) : type;
            switch (kind)
            {
                case "songs":
                    item.Kind = "song";
                    item.Subtitle = Str("artistName");
                    if (a.TryGetProperty("durationInMillis", out var ms) && ms.TryGetInt64(out var dur)) item.DurationMs = dur;
                    if (a.TryGetProperty("playParams", out var pp))
                    {
                        if (pp.TryGetProperty("catalogId", out var c) && c.ValueKind == JsonValueKind.String) item.CatalogId = c.GetString() ?? "";
                        else if (pp.TryGetProperty("purchasedId", out var p) && p.ValueKind == JsonValueKind.String) item.CatalogId = p.GetString() ?? "";
                    }
                    else if (!item.IsLibrary) { /* not playable here */ }
                    if (!item.IsLibrary) item.CatalogId = id;
                    break;
                case "albums": item.Kind = "album"; item.Subtitle = Str("artistName"); break;
                case "playlists": item.Kind = "playlist"; item.Subtitle = Str("curatorName"); break;
                case "artists": item.Kind = "artist"; break;
                case "stations":
                    item.Kind = "station";
                    item.IsLive = a.TryGetProperty("isLive", out var live) && live.ValueKind == JsonValueKind.True;
                    break;
                default: return null;
            }
            if (item.Name.Length == 0) return null;
            return item;
        }

        /// <summary>The API path that lists what is inside an album, playlist, artist or station.</summary>
        public string ChildrenPath(AppleMusicItem item)
        {
            string lib = item.IsLibrary ? "/v1/me/library" : "/v1/catalog/{sf}";
            return item.Kind switch
            {
                "album" => $"{lib}/albums/{Uri.EscapeDataString(item.Id)}/tracks?limit=300",
                "playlist" => $"{lib}/playlists/{Uri.EscapeDataString(item.Id)}/tracks?limit=100",
                "artist" => item.IsLibrary
                    ? $"/v1/me/library/artists/{Uri.EscapeDataString(item.Id)}/albums?limit=100"
                    : $"/v1/catalog/{{sf}}/artists/{Uri.EscapeDataString(item.Id)}/albums?limit=100",
                _ => "",
            };
        }

        /// <summary>The next songs a station plays. Each call gives the next handful.</summary>
        public async Task<List<AppleMusicItem>> StationTracksAsync(AppleMusicItem station, CancellationToken token)
        {
            await StartAsync(token).ConfigureAwait(false);
            if (station.IsLive) throw new AppleMusicException("Live radio can't be played yet. Stations based on songs and artists can.");
            using var req = Request(HttpMethod.Post, $"{AmpApi}/v1/me/stations/next-tracks/{Uri.EscapeDataString(station.Id)}?limit=10");
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var doc = await SendAsync(req, token).ConfigureAwait(false);
            var items = new List<AppleMusicItem>();
            if (doc.RootElement.TryGetProperty("data", out var data))
                foreach (var d in data.EnumerateArray()) Add(items, d);
            return items;
        }

        // ---- Playing ----

        /// <summary>
        /// The developer token for the MusicKit player in the WebView2 host. The
        /// same token this client already scraped to talk to the API, so a sign-in
        /// and a first browse mean it is ready without another round trip.
        /// </summary>
        public async Task<string> DeveloperTokenForPlayerAsync(CancellationToken token)
        {
            await StartAsync(token).ConfigureAwait(false);
            return _developerToken!;
        }
    }

    internal sealed class AppleMusicException : Exception
    {
        /// <summary>True when setting up again in Preferences is the fix.</summary>
        public bool SignIn { get; }
        public AppleMusicException(string message, bool signIn = false) : base(message) => SignIn = signIn;
    }
}
