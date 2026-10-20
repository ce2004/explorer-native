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
        ///
        /// Answered from a copy kept in memory (4.1.5). Drive can only be asked
        /// for everything trashed, which includes every file inside a trashed
        /// folder: Conner's 64 items took 68 seconds to list, and Windows gives a
        /// folder 45, so Trash failed with "The operation was canceled" every
        /// time. The listing now runs on its own, patiently, started when Drive
        /// connects (<see cref="WarmTrash"/>); our own trash, restore and delete
        /// edit the copy at once, and a copy older than <see cref="TrashFresh"/>
        /// is listed again behind the answer, for changes made elsewhere.
        public async Task<List<DriveEntry>> ListTrash(CancellationToken token, bool interactive = false)
        {
            Task<List<DriveEntry>> listing;
            lock (_trashGate)
            {
                if (_trashHeld != null)
                {
                    if (_trashListing == null && DateTime.UtcNow - _trashHeldAt > TrashFresh)
                        _trashListing = StartTrashListing();
                    return HeldTrashCopy();
                }
                listing = _trashListing ??= StartTrashListing();
            }

            try { await listing.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!listing.IsCompleted)
            {
                throw new TimeoutException("Google Drive is still gathering what's in it. Open it again in a minute");
            }
            lock (_trashGate) return _trashHeld != null ? HeldTrashCopy() : new List<DriveEntry>(listing.Result);
        }

        /// <summary>Starts the trash listing if nothing is held yet, so Trash opens at once later.</summary>
        public void WarmTrash()
        {
            lock (_trashGate)
                if (_trashHeld == null && _trashListing == null)
                    _trashListing = StartTrashListing();
        }

        /// <summary>How old the kept trash listing may be before an open lists it again behind the answer.</summary>
        private static readonly TimeSpan TrashFresh = TimeSpan.FromMinutes(5);

        private readonly object _trashGate = new();
        private List<DriveEntry>? _trashHeld;
        private DateTime _trashHeldAt;
        private Task<List<DriveEntry>>? _trashListing;

        /// <summary>Our own changes made while a listing runs, applied to it when it lands.</summary>
        private List<Action<List<DriveEntry>>>? _trashEditsDuring;

        private List<DriveEntry> HeldTrashCopy()
        {
            var copy = new List<DriveEntry>(_trashHeld!);
            copy.Sort((a, b) => b.Modified.CompareTo(a.Modified));
            return copy;
        }

        /// <summary>Under <see cref="_trashGate"/>.</summary>
        private Task<List<DriveEntry>> StartTrashListing()
        {
            _trashEditsDuring = new List<Action<List<DriveEntry>>>();
            return Task.Run(async () =>
            {
                try
                {
                    using var most = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                    var fresh = await ReadTrash(most.Token, interactive: false).ConfigureAwait(false);
                    lock (_trashGate)
                    {
                        foreach (var edit in _trashEditsDuring ?? new()) edit(fresh);
                        _trashEditsDuring = null;
                        _trashHeld = fresh;
                        _trashHeldAt = DateTime.UtcNow;
                        _trashListing = null;
                    }
                    return fresh;
                }
                catch
                {
                    lock (_trashGate) { _trashListing = null; _trashEditsDuring = null; }
                    throw;
                }
            });
        }

        /// <summary>Our own change to the trash, made to the kept listing too.</summary>
        private void EditHeldTrash(Action<List<DriveEntry>> edit)
        {
            lock (_trashGate)
            {
                if (_trashHeld != null) edit(_trashHeld);
                _trashEditsDuring?.Add(edit);
            }
        }

        /// <summary>
        /// Something we just trashed, from the answer to the trash request, put
        /// at the top of the kept listing. My Drive only, like the listing.
        /// </summary>
        private void NoteTrashed(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var f = doc.RootElement;
                if (!f.TryGetProperty("name", out var n) || !f.TryGetProperty("mimeType", out var mt)) return;
                if (f.TryGetProperty("driveId", out var d) && !string.IsNullOrEmpty(d.GetString())) return;
                string id = f.GetProperty("id").GetString() ?? "";
                long size = f.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var sv) ? sv : -1;
                var when = f.TryGetProperty("trashedTime", out var tt) ? tt.GetDateTime() : DateTime.UtcNow;
                var entry = new DriveEntry(id, n.GetString() ?? "", mt.GetString() ?? "", size, when, null,
                    f.TryGetProperty("md5Checksum", out var md5) ? md5.GetString() : null);
                EditHeldTrash(l => { l.RemoveAll(e => e.Id == id); l.Insert(0, entry); });
            }
            catch (Exception) { }   // a listing later will show it
        }

        private async Task<List<DriveEntry>> ReadTrash(CancellationToken token, bool interactive)
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
            EditHeldTrash(l => l.RemoveAll(e => e.Id == fileId));
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
            if (response.IsSuccessStatusCode || (int)response.StatusCode == 404)
            {
                EditHeldTrash(l => l.RemoveAll(e => e.Id == fileId));
                return;
            }
            var body = await response.Content.ReadAsStringAsync(token);
            throw new DriveStatusException((int)response.StatusCode, body,
                $"delete failed {(int)response.StatusCode}: " + Explain((int)response.StatusCode, body));
        }

        /// <summary>Deletes everything in My Drive's trash for good. Asked about first, by the caller.</summary>
        public async Task EmptyTrash(CancellationToken token)
        {
            using var request = await Request(HttpMethod.Delete, $"{Files}/trash", token);
            using var response = await Send(request, HttpCompletionOption.ResponseContentRead, token);
            if (response.IsSuccessStatusCode)
            {
                EditHeldTrash(l => l.Clear());
                return;
            }
            var body = await response.Content.ReadAsStringAsync(token);
            throw new DriveStatusException((int)response.StatusCode, body,
                $"emptying the trash failed {(int)response.StatusCode}: " + Explain((int)response.StatusCode, body));
        }
    }
}
