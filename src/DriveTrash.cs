using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Google Drive's trash, as a folder on the letter beside My Drive and
    /// Shared drives (Conner, 3.0.16): what is in it, putting something back,
    /// deleting something for good, and emptying it.
    ///
    /// My Drive's trash only. A shared drive keeps a trash of its own, which
    /// drive.google.com shows inside that drive; listing every one of them here
    /// would be a request per drive on every visit, for files most people never
    /// put there.
    /// </summary>
    internal sealed partial class DriveClient
    {
        /// <summary>
        /// The id of the folder that lists the trash. Not a Drive object (Drive has
        /// no folder holding its trash), so every write refuses it by name, as for
        /// <see cref="SharedDrivesId"/>.
        /// </summary>
        public const string TrashId = "::trash";

        public const string TrashName = "Trash";

        /// <summary>
        /// What is in the trash: only what somebody put there. The files inside a
        /// trashed folder are trashed too, but they belong to that folder, and
        /// listing them loose beside it would show every one of them twice.
        /// Newest first, the way drive.google.com orders it, with the time it was
        /// trashed in place of the time it was changed.
        /// </summary>
        public async Task<List<DriveEntry>> ListTrash(CancellationToken token, bool interactive = false)
        {
            var results = new List<DriveEntry>();
            string? page = null;
            do
            {
                string query = Uri.EscapeDataString("trashed = true");
                string fields = Uri.EscapeDataString(
                    "nextPageToken,files(id,name,mimeType,size,modifiedTime,trashedTime,explicitlyTrashed,md5Checksum)");
                string url = $"{Files}?q={query}&fields={fields}&pageSize=1000&corpora=user" +
                             (page != null ? $"&pageToken={Uri.EscapeDataString(page)}" : "");

                using var response = await SendWithRetry(
                    () => Request(HttpMethod.Get, url, token),
                    HttpCompletionOption.ResponseContentRead, token, interactive: interactive);
                var body = await response.Content.ReadAsStringAsync(token);
                if (!response.IsSuccessStatusCode)
                    throw new DriveStatusException((int)response.StatusCode, body,
                        $"trash listing failed {(int)response.StatusCode}: " + Explain((int)response.StatusCode, body));

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                foreach (var f in root.GetProperty("files").EnumerateArray())
                {
                    if (f.TryGetProperty("explicitlyTrashed", out var e) && e.ValueKind == JsonValueKind.False) continue;
                    long size = f.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var sv) ? sv : -1;
                    var when = f.TryGetProperty("trashedTime", out var tt) && tt.GetDateTime() is var trashed ? trashed
                        : f.TryGetProperty("modifiedTime", out var m) && m.GetDateTime() is var changed ? changed : default;
                    results.Add(new DriveEntry(
                        f.GetProperty("id").GetString() ?? "",
                        f.GetProperty("name").GetString() ?? "",
                        f.GetProperty("mimeType").GetString() ?? "",
                        size, when, null,
                        f.TryGetProperty("md5Checksum", out var md5) ? md5.GetString() : null));
                }
                page = root.TryGetProperty("nextPageToken", out var p) ? p.GetString() : null;
            }
            while (page != null);

            results.Sort((a, b) => b.Modified.CompareTo(a.Modified));
            return results;
        }

        /// <summary>Takes something out of the trash, back to the folder it was in.</summary>
        public async Task Untrash(string fileId, CancellationToken token)
        {
            using var request = await Request(HttpMethod.Patch, $"{Files}/{Uri.EscapeDataString(fileId)}?fields=id", token);
            request.Content = new StringContent("{\"trashed\":false}", Encoding.UTF8, "application/json");
            using var response = await Send(request, HttpCompletionOption.ResponseContentRead, token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(token);
                throw new DriveStatusException((int)response.StatusCode, body,
                    $"restore failed {(int)response.StatusCode}: " + Explain((int)response.StatusCode, body));
            }
        }

        /// <summary>
        /// Deletes one item for good: no trash, no undo. Only for something that
        /// is already in the trash, which the caller makes sure of and asks about.
        /// A 404 is it being gone already, which is what was wanted.
        /// </summary>
        public async Task DeleteForever(string fileId, CancellationToken token)
        {
            using var request = await Request(HttpMethod.Delete, $"{Files}/{Uri.EscapeDataString(fileId)}", token);
            using var response = await Send(request, HttpCompletionOption.ResponseContentRead, token);
            if (response.IsSuccessStatusCode || (int)response.StatusCode == 404) return;
            var body = await response.Content.ReadAsStringAsync(token);
            throw new DriveStatusException((int)response.StatusCode, body,
                $"delete failed {(int)response.StatusCode}: " + Explain((int)response.StatusCode, body));
        }

        /// <summary>Deletes everything in My Drive's trash for good. Asked about first, by the caller.</summary>
        public async Task EmptyTrash(CancellationToken token)
        {
            using var request = await Request(HttpMethod.Delete, $"{Files}/trash", token);
            using var response = await Send(request, HttpCompletionOption.ResponseContentRead, token);
            if (response.IsSuccessStatusCode) return;
            var body = await response.Content.ReadAsStringAsync(token);
            throw new DriveStatusException((int)response.StatusCode, body,
                $"emptying the trash failed {(int)response.StatusCode}: " + Explain((int)response.StatusCode, body));
        }
    }
}
