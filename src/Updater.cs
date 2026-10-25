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
            string? Sha256,
            string? CompressedUrl = null,
            long CompressedSize = -1);

        /// <summary>
        /// The gzip copy of each executable the release carries beside it, about
        /// 40 percent of the size (measured on 4.2.2: 138 MB to 55 MB). Unpacked
        /// here and checked against the executable's own size and hash, so it can
        /// never install something other than the release.
        /// </summary>
        public static string CompressedAssetName => AssetName + ".gz";

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
                ? new HttpClient(GoogleNet.Handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) }
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

        /// <summary>How long a download may go without a byte arriving.</summary>
        private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(60);

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

            // A hotel or café Wi-Fi sign-in page answers 200 with a web page.
            if (response.Content.Headers.ContentType?.MediaType is { } type &&
                type.Contains("html", StringComparison.OrdinalIgnoreCase))
                throw new UpdateException(Problem.BadAnswer,
                    "A web page answered instead of GitHub. If this network has a sign-in page, sign in in the browser first.");

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

                    string? packedUrl = null;
                    long packedSize = -1;
                    foreach (var asset in root.GetProperty("assets").EnumerateArray())
                    {
                        if (!string.Equals(asset.GetProperty("name").GetString(), CompressedAssetName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (asset.TryGetProperty("browser_download_url", out var pu) && pu.ValueKind == JsonValueKind.String &&
                            asset.TryGetProperty("size", out var ps) && ps.ValueKind == JsonValueKind.Number &&
                            ps.TryGetInt64(out var pn) && pn > 0)
                        {
                            packedUrl = pu.GetString();
                            packedSize = pn;
                        }
                    }

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

                        return new Release(latest, notes, url, size, sha,
                            string.IsNullOrEmpty(packedUrl) ? null : packedUrl, packedSize);
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

            // The compressed copy first: under half the bytes. Anything wrong
            // with it (missing, cut short, will not unpack, wrong hash) falls
            // back to the plain executable, so it can only ever make it faster.
            if (release.CompressedUrl != null && release.CompressedSize > 0)
            {
                var packed = target + ".gz.download";
                try
                {
                    await FetchAsync(release.CompressedUrl, packed, release, cancel).ConfigureAwait(false);
                    if (new FileInfo(packed).Length != release.CompressedSize)
                        throw new InvalidDataException("The compressed download was cut short.");
                    await Task.Run(() =>
                    {
                        using var input = new FileStream(packed, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                        using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                        using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                        gzip.CopyTo(output, 1 << 20);
                    }, cancel).ConfigureAwait(false);
                    TryDelete(packed);
                    if (Verified(partial, release))
                    {
                        File.Move(partial, target, overwrite: true);
                        return target;
                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { TryDelete(packed); throw; }
                catch { }
                TryDelete(packed);
                TryDelete(partial);
            }

            await FetchAsync(release.DownloadUrl, partial, release, cancel).ConfigureAwait(false);

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

        /// <summary>Whether an unpacked executable is exactly the release's: its size, and its hash when GitHub gave one.</summary>
        private static bool Verified(string path, Release release) =>
            new FileInfo(path).Length == release.Size &&
            (release.Sha256 == null || string.Equals(AppInstall.HashOf(path), release.Sha256, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Downloads <paramref name="url"/> into <paramref name="path"/>.
        /// HttpClient's own timeout stops at the headers once the body is
        /// streamed, so a stalled download would otherwise never end. Given up
        /// when nothing arrives for a minute (Kova's rule), not after a fixed
        /// five minutes, which a slow line could never beat.
        /// </summary>
        private static async Task FetchAsync(string url, string path, Release release, CancellationToken cancel)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(StallLimit);

            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, limit.Token)
                .ConfigureAwait(false);
            ThrowIfRefused(response, $"The download for version {Text(release.Version)} is no longer on GitHub.");
            await using var source = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            var buffer = new byte[1 << 20];
            int read;
            try
            {
                while ((read = await source.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), limit.Token).ConfigureAwait(false);
                    limit.CancelAfter(StallLimit);
                }
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new UpdateException(Problem.Refused,
                    "The download stopped: nothing arrived from GitHub for a minute. Try again.");
            }
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
