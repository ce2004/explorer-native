using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Help, Check for updates. Asked for by a person, never on a timer: there is
    /// no automatic update and no setting for one.
    ///
    /// A release is a GitHub release of <see cref="Repository"/> carrying one
    /// single-file executable per architecture. Updating downloads the one for
    /// this process's architecture into <see cref="DownloadsDirectory"/>, checks
    /// it against the hash GitHub publishes for it, and runs it with
    /// <c>--update</c> — the same install the command line uses. That install
    /// renames the running executable aside rather than overwriting it, which is
    /// what lets it happen while this copy is open, then asks this copy to stand
    /// down (it saves its state as it would for Exit) and starts the new build
    /// in its place.
    /// </summary>
    internal static class Updater
    {
        public const string Repository = "ce2004/explorer-native";

        public sealed record Release(
            Version Version,
            string Notes,
            string DownloadUrl,
            long Size,
            string? Sha256);

        private static readonly HttpClient Shared = CreateClient(null);

        private static HttpClient? _testClient;

        private static HttpClient Http => _testClient ?? Shared;

        /// <summary>
        /// For the suite: answers every request from <paramref name="handler"/>
        /// rather than GitHub, until called again with null.
        /// </summary>
        internal static void UseHandlerForTests(HttpMessageHandler? handler)
        {
            var old = _testClient;
            _testClient = handler == null ? null : CreateClient(handler);
            old?.Dispose();
        }

        /// <summary>For the suite: where downloads and the update marker go instead.</summary>
        internal static string? DirectoryOverride { get; set; }

        private static HttpClient CreateClient(HttpMessageHandler? handler)
        {
            var client = handler == null
                ? new HttpClient { Timeout = TimeSpan.FromMinutes(5) }
                : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };
            // GitHub refuses API requests without one.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ExplorerNative/" + CurrentText);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        /// <summary>This build's version, as major.minor.patch.</summary>
        public static Version Current => Normalise(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

        public static string CurrentText => Text(Current);

        public static string Text(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

        /// <summary>The release asset this process can run.</summary>
        public static string AssetName => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "ExplorerNative-arm64.exe",
            _ => "ExplorerNative-x64.exe",
        };

        /// <summary>
        /// Local, not roaming: %APPDATA%\ExplorerNative holds the Google Drive
        /// sync root, and nothing should be walking or writing beside that.
        /// </summary>
        public static string DownloadsDirectory =>
            DirectoryOverride ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExplorerNative", "Updates");

        /// <summary>
        /// Written just before the install starts, read by the copy it starts, so
        /// the new build can say that it is the new build.
        /// </summary>
        private static string MarkerPath => Path.Combine(DownloadsDirectory, "updating-to.txt");

        /// <summary>What went wrong talking to GitHub, for a caller that wants to act on it.</summary>
        public enum Problem
        {
            /// <summary>GitHub refused for now (403 or 429): too many checks from this address.</summary>
            RateLimited,

            /// <summary>404: the release list, or the file, is not where it should be.</summary>
            NotFound,

            /// <summary>GitHub answered, but not with something that can be read.</summary>
            BadAnswer,

            /// <summary>Any other refusal or server error.</summary>
            Refused,
        }

        /// <summary>
        /// A failure whose <see cref="Exception.Message"/> is already the whole
        /// sentence to show. Deliberately not an HttpRequestException, which
        /// callers prefix with "GitHub could not be reached".
        /// </summary>
        public sealed class UpdateException : Exception
        {
            public Problem Problem { get; }

            public UpdateException(Problem problem, string message, Exception? inner = null)
                : base(message, inner) => Problem = problem;
        }

        public const string RateLimitedMessage = "GitHub is limiting checks right now. Try again in a few minutes.";

        /// <summary>
        /// Turns a refusal into a sentence. <paramref name="notFound"/> is the
        /// sentence for a 404, which depends on what was asked for.
        /// </summary>
        private static void ThrowIfRefused(HttpResponseMessage response, string notFound)
        {
            if (response.IsSuccessStatusCode) return;

            int status = (int)response.StatusCode;

            // Nothing here signs in, so a 403 from these public endpoints is the
            // rate limit (or its secondary, abuse-detection form) whatever its
            // headers say; 429 is the same thing under its own number.
            if (status is 403 or 429) throw new UpdateException(Problem.RateLimited, RateLimitedMessage);
            if (status == 404) throw new UpdateException(Problem.NotFound, notFound);

            throw new UpdateException(Problem.Refused,
                $"GitHub answered {status} ({response.ReasonPhrase ?? "an error"}). Try again later.");
        }

        private static UpdateException Unreadable(Exception inner) =>
            new(Problem.BadAnswer, "GitHub's answer about the releases could not be read. Try again later.", inner);

        /// <summary>
        /// The latest release if it is newer than this build, otherwise null.
        /// Throws when GitHub cannot be reached or the release has nothing for
        /// this architecture, so the caller can say why: an
        /// <see cref="UpdateException"/> carries the sentence to show.
        /// </summary>
        public static async Task<Release?> CheckAsync(CancellationToken cancel = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            using var response = await Http.GetAsync(
                $"https://api.github.com/repos/{Repository}/releases/latest", timeout.Token).ConfigureAwait(false);

            // A 404 is not "you are up to date": it means the list itself was
            // not there to compare against, and saying "latest" then is a guess.
            ThrowIfRefused(response, "Could not find the list of Explorer Native releases on GitHub.");

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            JsonDocument json;
            try { json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false); }
            catch (JsonException ex) { throw Unreadable(ex); }

            using (json)
            {
                try
                {
                    var root = json.RootElement;

                    var tag = root.GetProperty("tag_name").GetString() ?? "";
                    if (!TryParseVersion(tag, out var latest))
                        throw new UpdateException(Problem.BadAnswer,
                            $"The latest release is tagged \"{tag}\", which is not a version number.");

                    if (latest <= Current) return null;

                    foreach (var asset in root.GetProperty("assets").EnumerateArray())
                    {
                        if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string? sha = null;
                        if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String)
                        {
                            var d = digest.GetString() ?? "";
                            if (d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = d[7..];
                        }

                        var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                            ? (b.GetString() ?? "").Trim()
                            : "";

                        var url = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String
                            ? u.GetString()
                            : null;
                        long size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number &&
                                    s.TryGetInt64(out var n) ? n : -1;

                        if (string.IsNullOrEmpty(url) || size <= 0)
                            throw new UpdateException(Problem.BadAnswer,
                                $"Version {Text(latest)} is out, but GitHub did not say " +
                                (string.IsNullOrEmpty(url) ? "where to download it" : "how big the download is") +
                                ". Try again later.");

                        return new Release(latest, notes, url, size, sha);
                    }

                    throw new UpdateException(Problem.NotFound,
                        $"Version {Text(latest)} is out, but it has no {AssetName} for this computer yet.");
                }
                catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    throw Unreadable(ex);
                }
            }
        }

        /// <summary>One published version and its notes, for Help, Changelog.</summary>
        public sealed record ReleaseNotes(Version Version, DateTime? Published, string Notes);

        /// <summary>
        /// Every published release, newest first. Drafts, pre-releases and tags
        /// that are not version numbers are left out.
        /// </summary>
        public static async Task<List<ReleaseNotes>> AllReleasesAsync(CancellationToken cancel = default)
        {
            var result = new List<ReleaseNotes>();
            string? url = $"https://api.github.com/repos/{Repository}/releases?per_page=100";

            // GitHub hands the list out a hundred at a time and says where the
            // next hundred are in the Link header. The first page alone was the
            // whole changelog until the hundred-and-first release.
            for (int page = 0; url != null && page < MaxReleasePages; page++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));

                using var response = await Http.GetAsync(url, timeout.Token).ConfigureAwait(false);
                ThrowIfRefused(response, "Could not find the list of Explorer Native releases on GitHub.");

                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                JsonDocument json;
                try { json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false); }
                catch (JsonException ex) { throw Unreadable(ex); }

                using (json)
                {
                    if (json.RootElement.ValueKind != JsonValueKind.Array) throw Unreadable(new FormatException("not a list"));
                    foreach (var release in json.RootElement.EnumerateArray())
                    {
                        // One release GitHub describes oddly is left out rather
                        // than costing the whole changelog.
                        try
                        {
                            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
                            if (release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) continue;
                            if (!release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
                                !TryParseVersion(tag.GetString() ?? "", out var version)) continue;

                            DateTime? published = null;
                            if (release.TryGetProperty("published_at", out var at) && at.ValueKind == JsonValueKind.String &&
                                DateTime.TryParse(at.GetString(), CultureInfo.InvariantCulture,
                                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                                published = DateTime.SpecifyKind(when, DateTimeKind.Utc);

                            var notes = release.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                                ? b.GetString() ?? ""
                                : "";
                            result.Add(new ReleaseNotes(version, published, notes));
                        }
                        catch (InvalidOperationException) { }
                    }
                }

                url = NextPage(response);
            }

            return result.OrderByDescending(r => r.Version).ToList();
        }

        /// <summary>
        /// How many pages of a hundred releases the changelog reads. A bound, so
        /// a Link header that points back at itself cannot loop for ever.
        /// </summary>
        internal const int MaxReleasePages = 10;

        /// <summary>
        /// The rel="next" address from a Link header, or null. Only ever another
        /// page of GitHub's API: the header is followed without a person seeing it.
        /// </summary>
        internal static string? NextPage(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Link", out var values)) return null;

            foreach (var value in values)
            foreach (var part in value.Split(','))
            {
                var pieces = part.Split(';');
                if (pieces.Length < 2) continue;
                if (!pieces.Skip(1).Any(p => p.Trim().Replace(" ", "")
                        .Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase))) continue;

                var target = pieces[0].Trim().TrimStart('<').TrimEnd('>');
                if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                    uri.Scheme == Uri.UriSchemeHttps &&
                    uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                    return uri.AbsoluteUri;
            }
            return null;
        }

        /// <summary>
        /// Downloads the release and returns the path of the verified executable.
        /// It is named ExplorerNative.exe in a folder of its own because the
        /// install copies "ExplorerNative.exe from the folder it is running in".
        /// </summary>
        public static async Task<string> DownloadAsync(Release release, CancellationToken cancel = default)
        {
            SweepDownloads();

            var folder = Path.Combine(DownloadsDirectory, Text(release.Version));
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, AppInstall.ProductName + ".exe");
            var partial = target + ".download";

            // HttpClient's own timeout stops at the headers once the body is
            // streamed, so a stalled download would otherwise never end.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromMinutes(5));

            using (var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, limit.Token)
                       .ConfigureAwait(false))
            {
                ThrowIfRefused(response, $"The download for version {Text(release.Version)} is no longer on GitHub.");
                await using var source = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
                await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(file, limit.Token).ConfigureAwait(false);
            }

            var length = new FileInfo(partial).Length;
            if (length != release.Size)
            {
                TryDelete(partial);
                throw new InvalidDataException($"The download was {length:N0} bytes, not {release.Size:N0}.");
            }

            if (release.Sha256 != null)
            {
                var got = AppInstall.HashOf(partial);
                if (!string.Equals(got, release.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(partial);
                    throw new InvalidDataException("The download does not match the hash GitHub published for it.");
                }
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }

        /// <summary>
        /// Runs the downloaded build's installer. Through the shell, so it
        /// inherits nothing from this process. The returned process exits with 0
        /// once the new build is installed and started, 1 if the install failed —
        /// in which case this copy was never asked to close.
        /// </summary>
        public static Process StartInstall(string exe, Release release)
        {
            try { ProtectedFile.WriteAllText(MarkerPath, Text(release.Version)); } catch { }

            return Process.Start(new ProcessStartInfo(exe, "--update --quiet") { UseShellExecute = true })
                ?? throw new InvalidOperationException("The installer did not start.");
        }

        /// <summary>
        /// The sentence to say if this launch is the build an update just put
        /// in place, otherwise null. Consumes the marker either way.
        /// </summary>
        public static string? TakeJustUpdatedMessage()
        {
            try
            {
                if (!File.Exists(MarkerPath)) return null;
                var text = ProtectedFile.ReadAllText(MarkerPath).Trim();
                return TryParseVersion(text, out var wanted) && Current >= wanted
                    ? $"Explorer Native updated to version {CurrentText}"
                    : null;
            }
            catch { return null; }
            finally
            {
                // Whatever it held. A marker that could not be read was read
                // again, and failed again, on every launch after.
                TryDelete(MarkerPath);
            }
        }

        /// <summary>
        /// Deletes earlier downloads. Best effort: the installer that just ran
        /// may still be on its way out, and its folder goes on the next launch.
        /// </summary>
        public static void SweepDownloads()
        {
            try
            {
                if (!Directory.Exists(DownloadsDirectory)) return;
                foreach (var dir in Directory.GetDirectories(DownloadsDirectory))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
            catch { }
        }

        public static bool TryParseVersion(string text, out Version version)
        {
            text = text.Trim().TrimStart('v', 'V');
            if (Version.TryParse(text, out var parsed))
            {
                version = Normalise(parsed);
                return true;
            }
            version = new Version(0, 0, 0);
            return false;
        }

        /// <summary>
        /// Three parts, always. Version treats a missing part as -1, so "1.0.0"
        /// would otherwise sort below the assembly's "1.0.0.0".
        /// </summary>
        private static Version Normalise(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
