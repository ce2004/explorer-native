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

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExplorerNative", "Updates");

        /// <summary>
        /// Written just before the install starts, read by the copy it starts, so
        /// the new build can say that it is the new build.
        /// </summary>
        private static string MarkerPath => Path.Combine(DownloadsDirectory, "updating-to.txt");

        /// <summary>
        /// The latest release if it is newer than this build, otherwise null.
        /// Throws when GitHub cannot be reached or the release has nothing for
        /// this architecture, so the caller can say why.
        /// </summary>
        public static async Task<Release?> CheckAsync(CancellationToken cancel = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            using var response = await Http.GetAsync(
                $"https://api.github.com/repos/{Repository}/releases/latest", timeout.Token).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null; // No release published yet.

            response.EnsureSuccessStatusCode();

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
            var root = json.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryParseVersion(tag, out var latest))
                throw new InvalidOperationException($"The latest release is tagged \"{tag}\", which is not a version number.");

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

                return new Release(
                    latest,
                    notes,
                    asset.GetProperty("browser_download_url").GetString()!,
                    asset.GetProperty("size").GetInt64(),
                    sha);
            }

            throw new InvalidOperationException(
                $"Version {Text(latest)} is out, but it has no {AssetName} for this computer yet.");
        }

        /// <summary>One published version and its notes, for Help, Changelog.</summary>
        public sealed record ReleaseNotes(Version Version, DateTime? Published, string Notes);

        /// <summary>
        /// Every published release, newest first. Drafts, pre-releases and tags
        /// that are not version numbers are left out.
        /// </summary>
        public static async Task<List<ReleaseNotes>> AllReleasesAsync(CancellationToken cancel = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            using var response = await Http.GetAsync(
                $"https://api.github.com/repos/{Repository}/releases?per_page=100", timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);

            var result = new List<ReleaseNotes>();
            foreach (var release in json.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
                if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) continue;
                if (!TryParseVersion(release.GetProperty("tag_name").GetString() ?? "", out var version)) continue;

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

            return result.OrderByDescending(r => r.Version).ToList();
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
                response.EnsureSuccessStatusCode();
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
                File.Delete(MarkerPath);
                return TryParseVersion(text, out var wanted) && Current >= wanted
                    ? $"Explorer Native updated to version {CurrentText}"
                    : null;
            }
            catch { return null; }
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
