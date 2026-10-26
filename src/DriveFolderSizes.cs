using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>One item as the size index needs it: where it is, and how big.</summary>
    internal readonly record struct DriveSizeItem(string Id, string? Parent, long Size, bool IsFolder, bool Counted);

    /// <summary>One entry from Drive's changes feed. Null Item means it went away.</summary>
    internal readonly record struct DriveSizeChange(string Id, DriveSizeItem? Item);

    internal sealed partial class DriveClient
    {
        private const string Changes = "https://www.googleapis.com/drive/v3/changes";

        private const string SizeFields = "id,parents,size,mimeType";

        /// <summary>How many listings run at once while the index is built.</summary>
        internal const int SizeLanes = 8;

        /// <summary>
        /// Every item in My Drive (null) or one shared drive, with its parent and size.
        ///
        /// Drive cannot say how big a folder is, and walking the letter to find
        /// out is one listing per folder. This is the whole account in pages of a
        /// thousand, and pages cannot be fetched in parallel through one page
        /// token, so a full first page splits the account by modified time and
        /// lists the slices side by side, splitting again any slice that is still
        /// over a page. Items modified while this runs can land in two slices or
        /// none; duplicates are merged by id here, and the changes feed (started
        /// before the listing) catches the rest.
        /// </summary>
        internal async Task<List<DriveSizeItem>> ListForSizes(string? driveId, CancellationToken token)
        {
            var found = new Dictionary<string, DriveSizeItem>(StringComparer.Ordinal);
            var gate = new object();
            void Keep(List<DriveSizeItem> items) { lock (gate) foreach (var i in items) found[i.Id] = i; }

            var (first, more) = await SizePage(null, null, null, driveId, token).ConfigureAwait(false);
            Keep(first);
            if (more == null) return found.Values.ToList();

            // Years, because most accounts are lopsided towards recent ones and a
            // slice that is still too big splits again by itself.
            var slices = new List<(DateTime From, DateTime To)>();
            var start = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            slices.Add((DateTime.MinValue, start));
            int lastYear = DateTime.UtcNow.Year + 1;
            for (int y = 2000; y < lastYear; y++)
                slices.Add((new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(y + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            slices.Add((new DateTime(lastYear, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.MaxValue));

            using var lanes = new SemaphoreSlim(SizeLanes);

            async Task Slice(DateTime from, DateTime to)
            {
                string? page = null;
                bool firstPage = true;
                do
                {
                    List<DriveSizeItem> items;
                    await lanes.WaitAsync(token).ConfigureAwait(false);
                    try { (items, page) = await SizePage(from, to, page, driveId, token).ConfigureAwait(false); }
                    finally { lanes.Release(); }
                    Keep(items);

                    // Over a page and wide enough to cut: eight narrower slices,
                    // side by side, rather than walking this one page by page.
                    if (firstPage && page != null && to != DateTime.MaxValue && from != DateTime.MinValue &&
                        (to - from) > TimeSpan.FromHours(1))
                    {
                        long step = (to - from).Ticks / 8;
                        var parts = new List<Task>();
                        for (int i = 0; i < 8; i++)
                        {
                            var a = from.AddTicks(step * i);
                            var b = i == 7 ? to : from.AddTicks(step * (i + 1));
                            parts.Add(Slice(a, b));
                        }
                        await Task.WhenAll(parts).ConfigureAwait(false);
                        return;
                    }
                    firstPage = false;
                }
                while (page != null);
            }

            await Task.WhenAll(slices.Select(s => Slice(s.From, s.To))).ConfigureAwait(false);
            return found.Values.ToList();
        }

        private async Task<(List<DriveSizeItem> Items, string? Next)> SizePage(
            DateTime? from, DateTime? to, string? page, string? driveId, CancellationToken token)
        {
            string q = "trashed = false";
            if (from is { } f && f != DateTime.MinValue) q += $" and modifiedTime >= '{RfcTime(f)}'";
            if (to is { } t && t != DateTime.MaxValue) q += $" and modifiedTime < '{RfcTime(t)}'";

            string scope = string.IsNullOrEmpty(driveId)
                ? ""
                : $"&corpora=drive&driveId={Uri.EscapeDataString(driveId)}&includeItemsFromAllDrives=true";
            string url = $"{Files}?q={Uri.EscapeDataString(q)}" +
                         $"&fields={Uri.EscapeDataString($"nextPageToken,files({SizeFields})")}&pageSize=1000" + scope +
                         (page != null ? $"&pageToken={Uri.EscapeDataString(page)}" : "");

            using var response = await SendWithRetry(
                () => Request(HttpMethod.Get, url, token), HttpCompletionOption.ResponseContentRead, token)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new DriveStatusException((int)response.StatusCode, body,
                    $"size listing failed {(int)response.StatusCode}: " + Explain((int)response.StatusCode, body));

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var items = new List<DriveSizeItem>();
            if (root.TryGetProperty("files", out var files))
                foreach (var file in files.EnumerateArray())
                    if (SizeItem(file) is { } item) items.Add(item);
            return (items, root.TryGetProperty("nextPageToken", out var p) ? p.GetString() : null);
        }

        private static DriveSizeItem? SizeItem(JsonElement file)
        {
            var id = file.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (string.IsNullOrEmpty(id)) return null;
            string mime = file.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "";
            string? parent = file.TryGetProperty("parents", out var ps) && ps.ValueKind == JsonValueKind.Array &&
                             ps.GetArrayLength() > 0 ? ps[0].GetString() : null;
            long size = file.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var n) ? n : 0;
            bool folder = mime == FolderMimeType;

            // What the letter shows is what is counted: Google's own documents and
            // shortcuts are not placed there, so they are not in its sizes either.
            bool counted = folder || !mime.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal);
            return new DriveSizeItem(id, parent, size, folder, counted);
        }

        /// <summary>Where the changes feed starts for My Drive (null) or a shared drive.</summary>
        internal async Task<string> ChangesStartToken(string? driveId, CancellationToken token)
        {
            string url = $"{Changes}/startPageToken" +
                         (string.IsNullOrEmpty(driveId) ? "" : $"?driveId={Uri.EscapeDataString(driveId)}");
            using var response = await SendWithRetry(
                () => Request(HttpMethod.Get, url, token), HttpCompletionOption.ResponseContentRead, token)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new DriveStatusException((int)response.StatusCode, body,
                    $"changes start failed {(int)response.StatusCode}: " + Explain((int)response.StatusCode, body));
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("startPageToken").GetString() ?? "";
        }

        /// <summary>One change from the feed, for the folder sync: what it is, its name and where it is now.</summary>
        internal sealed record DriveSyncChange(string Id, bool Gone, string? Name, string? Parent, bool Folder, bool Counted);

        /// <summary>
        /// The folder sync's view of the changes feed: like <see cref="ChangesSince"/>,
        /// with names, and a trashed file reported as gone. Throws on any refusal,
        /// including an expired token, so the caller lists everything instead.
        /// </summary>
        internal async Task<(List<DriveSyncChange> Changes, string Next)> SyncChangesSince(
            string pageToken, string? driveId, CancellationToken token)
        {
            var changes = new List<DriveSyncChange>();
            string scope = string.IsNullOrEmpty(driveId)
                ? ""
                : $"&driveId={Uri.EscapeDataString(driveId)}&includeItemsFromAllDrives=true";
            string fields = Uri.EscapeDataString(
                "nextPageToken,newStartPageToken,changes(fileId,removed,file(id,name,mimeType,parents,trashed))");

            string page = pageToken;
            while (true)
            {
                string url = $"{Changes}?pageToken={Uri.EscapeDataString(page)}&pageSize=1000&includeRemoved=true" +
                             $"&fields={fields}" + scope;
                using var response = await SendWithRetry(
                    () => Request(HttpMethod.Get, url, token), HttpCompletionOption.ResponseContentRead, token)
                    .ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new DriveStatusException((int)response.StatusCode, body,
                        $"changes failed {(int)response.StatusCode}: " + Explain((int)response.StatusCode, body));

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("changes", out var list))
                    foreach (var c in list.EnumerateArray())
                    {
                        var id = c.TryGetProperty("fileId", out var f) ? f.GetString() : null;
                        if (string.IsNullOrEmpty(id)) continue;
                        bool gone = c.TryGetProperty("removed", out var r) && r.ValueKind == JsonValueKind.True;
                        string? name = null, parent = null, mime = null;
                        if (!gone && c.TryGetProperty("file", out var file))
                        {
                            if (file.TryGetProperty("trashed", out var t) && t.ValueKind == JsonValueKind.True) gone = true;
                            name = file.TryGetProperty("name", out var n) ? n.GetString() : null;
                            mime = file.TryGetProperty("mimeType", out var m) ? m.GetString() : null;
                            if (file.TryGetProperty("parents", out var ps) && ps.ValueKind == JsonValueKind.Array &&
                                ps.GetArrayLength() > 0) parent = ps[0].GetString();
                        }
                        bool folder = mime == FolderMimeType;
                        bool counted = folder || mime == null || !mime.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal);
                        changes.Add(new DriveSyncChange(id, gone, name, parent, folder, counted));
                    }

                if (root.TryGetProperty("newStartPageToken", out var done) && done.GetString() is { Length: > 0 } next)
                    return (changes, next);
                if (!(root.TryGetProperty("nextPageToken", out var more) && more.GetString() is { Length: > 0 } np))
                    throw new InvalidOperationException("the changes feed ended without saying where to carry on");
                page = np;
            }
        }

        /// <summary>
        /// Everything that changed since <paramref name="pageToken"/>, and the
        /// token to ask from next time. One request when nothing has.
        /// </summary>
        internal async Task<(List<DriveSizeChange> Changes, string Next)> ChangesSince(
            string pageToken, string? driveId, CancellationToken token)
        {
            var changes = new List<DriveSizeChange>();
            string scope = string.IsNullOrEmpty(driveId)
                ? ""
                : $"&driveId={Uri.EscapeDataString(driveId)}&includeItemsFromAllDrives=true";
            string fields = Uri.EscapeDataString(
                $"nextPageToken,newStartPageToken,changes(fileId,removed,file({SizeFields},trashed))");

            string page = pageToken;
            while (true)
            {
                string url = $"{Changes}?pageToken={Uri.EscapeDataString(page)}&pageSize=1000&includeRemoved=true" +
                             $"&fields={fields}" + scope;
                using var response = await SendWithRetry(
                    () => Request(HttpMethod.Get, url, token), HttpCompletionOption.ResponseContentRead, token)
                    .ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new DriveStatusException((int)response.StatusCode, body,
                        $"changes failed {(int)response.StatusCode}: " + Explain((int)response.StatusCode, body));

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("changes", out var list))
                    foreach (var c in list.EnumerateArray())
                    {
                        var id = c.TryGetProperty("fileId", out var f) ? f.GetString() : null;
                        if (string.IsNullOrEmpty(id)) continue;
                        bool gone = c.TryGetProperty("removed", out var r) && r.ValueKind == JsonValueKind.True;
                        DriveSizeItem? item = null;
                        if (!gone && c.TryGetProperty("file", out var file))
                        {
                            bool trashed = file.TryGetProperty("trashed", out var t) && t.ValueKind == JsonValueKind.True;
                            if (!trashed) item = SizeItem(file);
                        }
                        changes.Add(new DriveSizeChange(id, item));
                    }

                if (root.TryGetProperty("newStartPageToken", out var done) && done.GetString() is { Length: > 0 } next)
                    return (changes, next);
                if (!(root.TryGetProperty("nextPageToken", out var more) && more.GetString() is { Length: > 0 } m))
                    return (changes, page);
                page = m;
            }
        }
    }

    /// <summary>
    /// Every folder's size in one Drive (My Drive, or one shared drive), worked
    /// out from a single listing of the whole account and kept current with the
    /// changes feed. A folder's size is then a dictionary lookup, which is what
    /// lets the Size column fill in on the Drive letter without walking
    /// placeholders — walking them is one Drive listing per folder.
    /// </summary>
    internal sealed class DriveSizeIndex
    {
        /// <summary>How long the index is trusted before the changes feed is asked again.</summary>
        public static TimeSpan Fresh { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>After a failed build or refresh, how long before trying again.</summary>
        public static TimeSpan BackOff { get; set; } = TimeSpan.FromSeconds(30);

        private readonly string? _driveId;
        private readonly object _gate = new();
        private readonly Dictionary<string, DriveSizeItem> _items = new(StringComparer.Ordinal);
        private Dictionary<string, FolderSizeResult>? _totals;
        private string? _changes;
        private string? _rootId;
        private bool _built;
        private long _checkedAt = long.MinValue / 2, _failedAt = long.MinValue / 2;
        private Task? _refreshing;

        /// <param name="storePath">Where the index is kept between runs (encrypted), or null to keep it in memory only.</param>
        public DriveSizeIndex(string? driveId, string? storePath = null) { _driveId = driveId; _store = storePath; }

        private readonly string? _store;
        private bool _triedLoad;
        private long _savedAt = long.MinValue / 2;
        private bool _unsaved;

        /// <summary>How often changes from the feed are written to the saved copy. Unmount saves the rest.</summary>
        public static TimeSpan SaveEvery { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>Where the index says what it did: the Drive log.</summary>
        public static Action<string>? Log { get; set; }

        private static void Say(string line) { try { Log?.Invoke(line); } catch { } }

        private string DriveName => _driveId ?? "My Drive";

        /// <summary>
        /// The saved copy, read in place of a first listing. The bookmark saved
        /// with it is then given to the changes feed, which catches up on
        /// whatever happened since; if the feed refuses it, the drive is listed
        /// whole as before. A copy that cannot be read is deleted.
        /// </summary>
        private void TryLoad()
        {
            lock (_gate)
            {
                if (_triedLoad || _built || _store == null) return;
                _triedLoad = true;
            }
            if (!System.IO.File.Exists(_store)) return;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var (rootId, changes, items) = Decode(ProtectedFile.ReadAllBytes(_store), _driveId);
                lock (_gate)
                {
                    if (_built) return;
                    _items.Clear();
                    foreach (var item in items) _items[item.Id] = item;
                    _rootId = rootId;
                    _changes = changes;
                    _totals = null;
                    _built = true;
                    _checkedAt = long.MinValue / 2;
                    _savedAt = Environment.TickCount64;
                }
                Say($"sizes: {DriveName} loaded {items.Count} saved items in {clock.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                Say($"sizes: {DriveName} saved copy unreadable ({ex.Message}); listing again");
                try { System.IO.File.Delete(_store); } catch { }
            }
        }

        /// <summary>Writes the saved copy if anything changed since the last one. For unmount.</summary>
        public void SaveNow()
        {
            lock (_gate) if (!_unsaved) return;
            Save();
        }

        private void SaveIfDue(bool now)
        {
            if (_store == null) return;
            lock (_gate)
            {
                if (!_unsaved) return;
                if (!now && Environment.TickCount64 - _savedAt < (long)SaveEvery.TotalMilliseconds) return;
            }
            Save();
        }

        private readonly object _saving = new();

        /// <summary>Asked before every save; false once signed out.</summary>
        public static Func<bool>? MaySave { get; set; }

        private void Save()
        {
            if (_store == null) return;
            try { if (MaySave != null && !MaySave()) return; } catch { return; }
            lock (_saving)
            {
                DriveSizeItem[] items;
                string? rootId, changes;
                lock (_gate)
                {
                    if (!_built || _changes == null) return;
                    items = _items.Values.ToArray();
                    rootId = _rootId;
                    changes = _changes;
                    _unsaved = false;
                    _savedAt = Environment.TickCount64;
                }

                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_store)!);
                    var data = Encode(_driveId, rootId, changes, items);
                    ProtectedFile.WriteAllBytes(_store, data);
                    Say($"sizes: {DriveName} saved {items.Length} items ({data.Length / 1024} KB) in {clock.ElapsedMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    lock (_gate) _unsaved = true;
                    Say($"sizes: {DriveName} could not be saved: {ex.Message}");
                }
            }
        }

        private const int FormatVersion = 1;
        private static readonly byte[] FormatMagic = { (byte)'E', (byte)'N', (byte)'S', (byte)'Z' };

        /// <summary>
        /// The saved form, before encryption: each id once, then each item's
        /// parent as a number pointing into that list (an id is about 33
        /// characters, and most parents are in the list), its size and its
        /// flags, the whole Brotli-compressed.
        /// </summary>
        internal static byte[] Encode(string? driveId, string? rootId, string changes, IReadOnlyList<DriveSizeItem> items)
        {
            var at = new Dictionary<string, int>(items.Count, StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++) at[items[i].Id] = i;

            using var output = new System.IO.MemoryStream();
            output.Write(FormatMagic);
            using (var packed = new System.IO.Compression.BrotliStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            using (var w = new System.IO.BinaryWriter(packed, System.Text.Encoding.UTF8))
            {
                w.Write(FormatVersion);
                w.Write(driveId ?? "");
                w.Write(rootId ?? "");
                w.Write(changes);
                w.Write(items.Count);
                foreach (var item in items) w.Write(item.Id);
                foreach (var item in items)
                {
                    // 0: no parent; 1: a parent outside the list, by name; n + 2: item n.
                    if (item.Parent == null) w.Write7BitEncodedInt(0);
                    else if (at.TryGetValue(item.Parent, out var p)) w.Write7BitEncodedInt(p + 2);
                    else { w.Write7BitEncodedInt(1); w.Write(item.Parent); }
                    w.Write7BitEncodedInt64(item.Size);
                    w.Write((byte)((item.IsFolder ? 1 : 0) | (item.Counted ? 2 : 0)));
                }
            }
            return output.ToArray();
        }

        internal static (string? RootId, string Changes, List<DriveSizeItem> Items) Decode(byte[] data, string? driveId)
        {
            if (data.Length < FormatMagic.Length || !data.AsSpan(0, FormatMagic.Length).SequenceEqual(FormatMagic))
                throw new System.IO.InvalidDataException("not a saved size index");
            using var input = new System.IO.MemoryStream(data, FormatMagic.Length, data.Length - FormatMagic.Length);
            using var packed = new System.IO.Compression.BrotliStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var r = new System.IO.BinaryReader(packed, System.Text.Encoding.UTF8);

            if (r.ReadInt32() != FormatVersion) throw new System.IO.InvalidDataException("saved by another version");
            if (r.ReadString() != (driveId ?? "")) throw new System.IO.InvalidDataException("saved for another drive");
            var rootId = r.ReadString();
            var changes = r.ReadString();
            if (changes.Length == 0) throw new System.IO.InvalidDataException("no changes bookmark");
            int count = r.ReadInt32();
            if (count < 0 || count > 50_000_000) throw new System.IO.InvalidDataException("bad item count");

            var ids = new string[count];
            for (int i = 0; i < count; i++) ids[i] = r.ReadString();
            var items = new List<DriveSizeItem>(count);
            for (int i = 0; i < count; i++)
            {
                int p = r.Read7BitEncodedInt();
                string? parent = p switch
                {
                    0 => null,
                    1 => r.ReadString(),
                    _ when p - 2 < count => ids[p - 2],
                    _ => throw new System.IO.InvalidDataException("bad parent"),
                };
                long size = r.Read7BitEncodedInt64();
                byte flags = r.ReadByte();
                items.Add(new DriveSizeItem(ids[i], parent, size, (flags & 1) != 0, (flags & 2) != 0));
            }
            return (rootId.Length == 0 ? null : rootId, changes, items);
        }

        /// <summary>Whether the whole drive has been listed once, so a size can be answered from memory.</summary>
        public bool Built { get { lock (_gate) return _built; } }

        /// <summary>
        /// How long a question waits for the changes feed once the index is built.
        /// Past this the sizes held are the answer and the feed carries on behind
        /// it: waiting the feed out (two minutes a try on a slow line) left the
        /// Size column empty whenever the folder was left first, which on a
        /// sluggish connection was half the folders opened.
        /// </summary>
        public static TimeSpan AnswerWithin { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>The next question asks the changes feed first, whatever the clock says.</summary>
        public void Expire()
        {
            lock (_gate)
            {
                _checkedAt = long.MinValue / 2;
                _failedAt = long.MinValue / 2;
                // A refresh already on its way asked Drive before this, so it must
                // not stamp the index fresh, and the next question starts another.
                _generation++;
                // A changes check is cheap and idempotent, so one may overlap; a
                // first build of the whole account is never started twice, and
                // nor is a rebuild after the feed refused its bookmark: while one
                // runs, _changes is null until the new listing sets it.
                if (_built && _changes != null) _refreshing = null;
            }
        }

        private int _generation;

        /// <summary>
        /// <see cref="Expire"/>, unless a refresh is already on its way: the live
        /// size check asks every few seconds, and on a slow line each ask would
        /// otherwise start another feed request beside the last.
        /// </summary>
        public void ExpireIfIdle()
        {
            lock (_gate) if (_refreshing != null) return;
            Expire();
        }

        /// <summary>
        /// Brings the index up to date if it is due, sharing one refresh among
        /// everyone asking. The refresh runs on <paramref name="lifetime"/>, not
        /// the caller's token: a navigation that gives up on a size should not
        /// throw away a half-built listing of the whole account.
        /// </summary>
        public Task Refresh(DriveClient client, CancellationToken lifetime)
        {
            lock (_gate)
            {
                if (_refreshing != null) return _refreshing;
                long now = Environment.TickCount64;
                if (_built && now - _checkedAt < (long)Fresh.TotalMilliseconds) return Task.CompletedTask;
                if (now - _failedAt < (long)BackOff.TotalMilliseconds) return Task.CompletedTask;
                var task = Task.Run(() => RefreshCore(client, lifetime), lifetime);
                _refreshing = task;
                task.ContinueWith(_ => { lock (_gate) if (_refreshing == task) _refreshing = null; },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return task;
            }
        }

        private async Task RefreshCore(DriveClient client, CancellationToken token, bool afterRebuild = false)
        {
            try
            {
                if (!afterRebuild) TryLoad();

                string? from;
                int generation;
                lock (_gate) { from = _built ? _changes : null; generation = _generation; }

                if (from != null)
                {
                    List<DriveSizeChange> changes;
                    string next;
                    try
                    {
                        (changes, next) = await client.ChangesSince(from, _driveId, token).ConfigureAwait(false);
                    }
                    catch (DriveClient.DriveStatusException refused) when (!afterRebuild && refused.Status is 400 or 404 or 410)
                    {
                        // The feed will not go on from that token (expired, or
                        // never valid again). Asking it again every thirty
                        // seconds failed for the rest of the session, and every
                        // folder size stayed as it was the first time. Listed
                        // whole instead, the way the first build is, with the
                        // sizes held meanwhile still answering.
                        lock (_gate)
                        {
                            if (_changes != from) return;
                            _changes = null;
                        }
                        await RebuildAsync(client, token).ConfigureAwait(false);
                        return;
                    }
                    lock (_gate)
                    {
                        // Another refresh got further first: its token is newer.
                        if (_changes != from) return;
                        foreach (var change in changes)
                        {
                            if (change.Item is { } item) _items[change.Id] = item;
                            else _items.Remove(change.Id);
                        }
                        if (changes.Count > 0) _totals = null;
                        if (changes.Count > 0 || next != from) _unsaved = true;
                        _changes = next;
                        if (generation == _generation) _checkedAt = Environment.TickCount64;
                    }
                    if (changes.Count > 0) Say($"sizes: {DriveName} took {changes.Count} changes from the feed");
                    SaveIfDue(now: false);
                    return;
                }

                await RebuildAsync(client, token).ConfigureAwait(false);
            }
            catch
            {
                lock (_gate) _failedAt = Environment.TickCount64;
                throw;
            }
        }

        /// <summary>The whole drive listed afresh, then the feed from where that listing began.</summary>
        private async Task RebuildAsync(DriveClient client, CancellationToken token)
        {
            // The feed's start is taken before the listing, so nothing that
            // changes while the listing runs can be missed.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long gzipBefore = DriveClient.GzipAnswers;
            var start = await client.ChangesStartToken(_driveId, token).ConfigureAwait(false);
            string? rootId = _driveId == null ? await client.RootId(token).ConfigureAwait(false) : _driveId;
            var all = await client.ListForSizes(_driveId, token).ConfigureAwait(false);
            Say($"sizes: {DriveName} listed whole, {all.Count} items in {clock.Elapsed.TotalSeconds:0.0} s " +
                $"({DriveClient.GzipAnswers - gzipBefore} answers gzip)");
            lock (_gate)
            {
                _items.Clear();
                foreach (var item in all) _items[item.Id] = item;
                _rootId = rootId;
                _changes = start;
                _totals = null;
                _built = true;
                _unsaved = true;
                _checkedAt = Environment.TickCount64;
            }

            // Anything that changed during the listing, at once rather than
            // on the next refresh.
            lock (_gate) _checkedAt = long.MinValue / 2;
            await RefreshCore(client, token, afterRebuild: true).ConfigureAwait(false);

            // Saved at once: this listing is the expensive part a restart should not repeat.
            SaveIfDue(now: true);
        }

        /// <summary>A folder's size, or null when the index has not been built or does not know it.</summary>
        public FolderSizeResult? SizeOf(string folderId)
        {
            lock (_gate)
            {
                if (!_built) return null;
                if (folderId == "root" && _rootId != null) folderId = _rootId;
                _totals ??= Totals(_items);
                if (_totals.TryGetValue(folderId, out var total)) return total;

                // A folder with nothing counted in it has no row of its own.
                return _items.ContainsKey(folderId) || folderId == _rootId
                    ? new FolderSizeResult(0, 0, 0, true)
                    : null;
            }
        }

        /// <summary>
        /// Every folder's total: each item adds itself to every folder above it.
        /// The depth limit stops a parent loop, which Drive should never have but
        /// which would otherwise never end.
        /// </summary>
        internal static Dictionary<string, FolderSizeResult> Totals(IReadOnlyDictionary<string, DriveSizeItem> items)
        {
            var bytes = new Dictionary<string, long>(StringComparer.Ordinal);
            var files = new Dictionary<string, int>(StringComparer.Ordinal);
            var folders = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var item in items.Values)
            {
                if (!item.Counted) continue;
                var parent = item.Parent;
                for (int depth = 0; parent != null && depth < 128; depth++)
                {
                    if (item.IsFolder) folders[parent] = folders.GetValueOrDefault(parent) + 1;
                    else
                    {
                        files[parent] = files.GetValueOrDefault(parent) + 1;
                        bytes[parent] = bytes.GetValueOrDefault(parent) + item.Size;
                    }
                    parent = items.TryGetValue(parent, out var up) ? up.Parent : null;
                }
            }

            var totals = new Dictionary<string, FolderSizeResult>(StringComparer.Ordinal);
            foreach (var id in bytes.Keys.Concat(files.Keys).Concat(folders.Keys).Distinct())
                totals[id] = new FolderSizeResult(bytes.GetValueOrDefault(id), files.GetValueOrDefault(id),
                    folders.GetValueOrDefault(id), true);
            return totals;
        }
    }
}
