using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// A Google Drive that lives in memory, answering exactly the Drive v3
    /// requests DriveClient, DriveWrites and DriveMonitor make: files.list
    /// (q, pageToken, pageSize, the appProperties lookup), files.get (the
    /// folder alive check, the root, driveId), alt=media with ranges, files.create
    /// for folders, multipart and resumable uploads (session URI, Content-Range
    /// queries, 308), files.update (trash, rename, move, modifiedTime),
    /// files.copy and about.
    ///
    /// It is an HttpMessageHandler handed to DriveClient's internal constructor,
    /// so nothing leaves the process and no account is involved. Faults are
    /// queued per kind of request: a status with a body (429 with Retry-After,
    /// 403 rateLimitExceeded, 500, 502 HTML, 503), a dropped connection, a body
    /// cut off part way, or a slow body.
    /// </summary>
    internal sealed class FakeDrive : HttpMessageHandler
    {
        public const string Root = "root-id";
        private const string Host = "https://www.googleapis.com";

        public sealed class Item
        {
            public string Id = "";
            public string Name = "";
            public string Parent = "";
            public string MimeType = "application/octet-stream";
            public byte[] Data = Array.Empty<byte>();
            public DateTime Modified = DateTime.UtcNow;
            public bool Trashed;
            public Dictionary<string, string> AppProperties = new();
            public bool IsFolder => MimeType == DriveClient.FolderMimeType;
            public bool IsDocument => MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) && !IsFolder;
            public string Md5 => Convert.ToHexString(MD5.HashData(Data)).ToLowerInvariant();
        }

        private sealed class Session
        {
            public string Name = "";
            public string Parent = "";
            public DateTime? Modified;
            public long Total;
            public MemoryStream Got = new();
            public Item? Done;
        }

        /// <summary>One queued misbehaviour. Status 0 is a dropped connection: no answer at all.</summary>
        public sealed class Fault
        {
            public string Kind = "";
            public int Times = 1;
            public int Status;
            public string Body = "";
            public TimeSpan? RetryAfter;
            public Func<string, bool>? Subject;
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, Item> _items = new();
        private readonly Dictionary<string, Session> _sessions = new();
        private readonly List<Fault> _faults = new();
        private int _next;

        // What happened, for the tests to count.
        public int Uploads, MediaReads, Trashes, Renames, Creates, SessionsBegun, Requests, FaultsServed;
        public readonly List<long> MediaOffsets = new();
        public readonly List<long> PutOffsets = new();
        public readonly List<string> Kinds = new();

        // Hooks, each off unless a test sets it.
        public Func<long, CancellationToken, Task>? OnMedia;
        public Func<long, CancellationToken, Task>? OnPutBytes;
        public Action<string>? AfterMultipartBody;
        public int CutPutsAfterBytes = -1;
        public int DropMediaMidBody;
        public int DropMultipartMidBody;
        public int SlowMediaMs;

        public FakeDrive()
        {
            _items[Root] = new Item { Id = Root, Name = "My Drive", MimeType = DriveClient.FolderMimeType };
        }

        // ---------------- building and reading the Drive ----------------

        public string AddFolder(string name, string parent = Root)
        {
            lock (_gate)
            {
                var id = "f" + (++_next);
                _items[id] = new Item { Id = id, Name = name, Parent = parent, MimeType = DriveClient.FolderMimeType };
                return id;
            }
        }

        public string AddFile(string name, byte[] data, DateTime modifiedUtc, string parent,
            string mime = "application/octet-stream")
        {
            lock (_gate)
            {
                var id = "d" + (++_next);
                _items[id] = new Item
                {
                    Id = id, Name = name, Parent = parent, MimeType = mime, Data = data,
                    Modified = Ms(modifiedUtc),
                };
                return id;
            }
        }

        public void Edit(string id, byte[] data, DateTime modifiedUtc)
        {
            lock (_gate)
            {
                _items[id].Data = data;
                _items[id].Modified = Ms(modifiedUtc);
            }
        }

        public void Trash(string id) { lock (_gate) _items[id].Trashed = true; }

        public void Remove(string id) { lock (_gate) _items.Remove(id); }

        public Item? Get(string id) { lock (_gate) return _items.TryGetValue(id, out var i) ? i : null; }

        /// <summary>The id of a live item at a relative path under a folder.</summary>
        public string? Find(string folder, string relative)
        {
            lock (_gate)
            {
                var at = folder;
                foreach (var part in relative.Split('/'))
                {
                    var hit = _items.Values.Where(i => i.Parent == at && !i.Trashed && i.Name == part)
                        .OrderByDescending(i => i.Modified).FirstOrDefault();
                    if (hit == null) return null;
                    at = hit.Id;
                }
                return at;
            }
        }

        /// <summary>Every live file under a folder, by relative path. Docs are left out.</summary>
        public Dictionary<string, byte[]> Tree(string folder)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            lock (_gate) Walk(folder, "", result);
            return result;
        }

        private void Walk(string folder, string prefix, Dictionary<string, byte[]> into)
        {
            foreach (var item in _items.Values.Where(i => i.Parent == folder && !i.Trashed))
            {
                var path = prefix + item.Name;
                if (item.IsFolder) Walk(item.Id, path + "/", into);
                else if (!item.IsDocument) into[path] = item.Data;
            }
        }

        /// <summary>Names that more than one live item in one folder holds, anywhere under a folder.</summary>
        public List<string> Duplicates(string folder)
        {
            lock (_gate)
            {
                var found = new List<string>();
                var queue = new Queue<(string Id, string Prefix)>();
                queue.Enqueue((folder, ""));
                while (queue.Count > 0)
                {
                    var (id, prefix) = queue.Dequeue();
                    var children = _items.Values.Where(i => i.Parent == id && !i.Trashed).ToList();
                    found.AddRange(children.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                        .Where(g => g.Count() > 1).Select(g => prefix + g.Key));
                    foreach (var f in children.Where(c => c.IsFolder)) queue.Enqueue((f.Id, prefix + f.Name + "/"));
                }
                return found;
            }
        }

        public void AddFault(Fault fault) { lock (_gate) _faults.Add(fault); }

        public int FaultsLeft { get { lock (_gate) return _faults.Sum(f => f.Times); } }

        public static string ErrorBody(int code, string reason, string message) =>
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["error"] = new Dictionary<string, object>
                {
                    ["code"] = code,
                    ["message"] = message,
                    ["errors"] = new[] { new Dictionary<string, object> { ["reason"] = reason, ["message"] = message } },
                },
            });

        private static DateTime Ms(DateTime t)
        {
            t = t.ToUniversalTime();
            return new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        }

        // ---------------- the API ----------------

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var uri = request.RequestUri!;
            if (!uri.AbsoluteUri.StartsWith(Host + "/", StringComparison.Ordinal))
                return Error(400, "badRequest", "the fake Drive only answers googleapis.com");

            var (kind, subject, body) = await Classify(request, ct);
            lock (_gate) Kinds.Add(kind);

            Fault? fault = null;
            lock (_gate)
            {
                fault = _faults.FirstOrDefault(f => f.Kind == kind && f.Times > 0 && (f.Subject == null || f.Subject(subject)));
                if (fault != null)
                {
                    fault.Times--;
                    if (fault.Times == 0) _faults.Remove(fault);
                    FaultsServed++;
                }
            }
            if (fault != null)
            {
                if (fault.Status == 0) throw new HttpRequestException("fake Drive: the connection was reset");
                var refused = new HttpResponseMessage((HttpStatusCode)fault.Status)
                {
                    Content = new StringContent(fault.Body, Encoding.UTF8,
                        fault.Body.StartsWith("<", StringComparison.Ordinal) ? "text/html" : "application/json"),
                };
                if (fault.RetryAfter is { } wait) refused.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
                return refused;
            }

            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            switch (kind)
            {
                case "about":
                    return Json(200, new Dictionary<string, object>
                    {
                        ["user"] = new Dictionary<string, object> { ["emailAddress"] = "fake@example.invalid" },
                        ["storageQuota"] = new Dictionary<string, object> { ["usage"] = "0", ["limit"] = "0" },
                    });

                case "list":
                case "lookup":
                    return List(query, kind == "lookup");

                case "alive":
                case "get":
                {
                    var item = Get(subject == "root" ? Root : subject);
                    if (item == null) return Error(404, "notFound", "File not found: " + subject);
                    return Json(200, Describe(item));
                }

                case "media":
                    return await Media(request, subject, ct);

                case "create":
                {
                    var meta = JsonDocument.Parse(body).RootElement;
                    var item = FromMetadata(meta, Array.Empty<byte>());
                    Interlocked.Increment(ref Creates);
                    return Json(200, Describe(item));
                }

                case "trash":
                case "patch":
                    return Patch(subject, query, body);

                case "copy":
                {
                    var from = Get(subject);
                    if (from == null) return Error(404, "notFound", "File not found");
                    var meta = JsonDocument.Parse(body).RootElement;
                    var copy = FromMetadata(meta, from.Data, from.Name, from.Modified, from.MimeType);
                    return Json(200, Describe(copy));
                }

                case "multipart":
                    return await Multipart(request, ct);

                case "begin":
                {
                    var meta = JsonDocument.Parse(body).RootElement;
                    long total = request.Headers.TryGetValues("X-Upload-Content-Length", out var lengths)
                        ? long.Parse(lengths.First()) : 0;
                    string id;
                    lock (_gate)
                    {
                        id = "S" + (++_next);
                        _sessions[id] = new Session
                        {
                            Name = meta.GetProperty("name").GetString() ?? "",
                            Parent = meta.GetProperty("parents")[0].GetString() ?? "",
                            Modified = meta.TryGetProperty("modifiedTime", out var m) ? ParseTime(m.GetString()) : null,
                            Total = total,
                        };
                    }
                    Interlocked.Increment(ref SessionsBegun);
                    var started = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
                    started.Headers.Location = new Uri($"{Host}/upload/drive/v3/files?uploadType=resumable&upload_id={id}");
                    return started;
                }

                case "query":
                case "put":
                    return await Put(request, subject, kind == "query", ct);
            }

            return Error(400, "badRequest", "the fake Drive does not know " + request.Method + " " + uri.AbsolutePath);
        }

        private async Task<(string Kind, string Subject, string Body)> Classify(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            string body = "";
            if (request.Content is StringContent text) body = await text.ReadAsStringAsync(ct);

            if (path == "/drive/v3/about") return ("about", "", body);

            if (path == "/drive/v3/files")
            {
                if (request.Method == HttpMethod.Post)
                {
                    var name = JsonDocument.Parse(body).RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    return ("create", name, body);
                }
                var q = query["q"] ?? "";
                var parent = Regex.Match(q, "'([^']*)' in parents").Groups[1].Value;
                return (q.Contains("appProperties", StringComparison.Ordinal) ? "lookup" : "list", parent, body);
            }

            if (path.StartsWith("/drive/v3/files/", StringComparison.Ordinal))
            {
                var rest = Uri.UnescapeDataString(path["/drive/v3/files/".Length..]);
                if (rest.EndsWith("/copy", StringComparison.Ordinal)) return ("copy", rest[..^5], body);
                if (request.Method == HttpMethod.Patch)
                    return (body.Contains("\"trashed\"", StringComparison.Ordinal) ? "trash" : "patch", rest, body);
                if (query["alt"] == "media") return ("media", rest, body);
                if ((query["fields"] ?? "").Contains("trashed", StringComparison.Ordinal)) return ("alive", rest, body);
                return ("get", rest, body);
            }

            if (path == "/upload/drive/v3/files")
            {
                if (request.Method == HttpMethod.Post && query["uploadType"] == "multipart")
                {
                    string name = "";
                    if (request.Content is MultipartContent parts && parts.FirstOrDefault() is StringContent meta)
                    {
                        var json = await meta.ReadAsStringAsync(ct);
                        name = JsonDocument.Parse(json).RootElement.GetProperty("name").GetString() ?? "";
                    }
                    return ("multipart", name, body);
                }
                if (request.Method == HttpMethod.Post)
                {
                    var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString() ?? "";
                    return ("begin", name, body);
                }
                if (request.Method == HttpMethod.Put)
                {
                    var range = request.Content?.Headers.ContentRange;
                    bool asking = range != null && !range.HasRange;
                    return (asking ? "query" : "put", query["upload_id"] ?? "", body);
                }
            }

            return ("unknown", path, body);
        }

        private HttpResponseMessage List(System.Collections.Specialized.NameValueCollection query, bool lookup)
        {
            var q = query["q"] ?? "";
            var parent = Regex.Match(q, "'([^']*)' in parents").Groups[1].Value;
            var tag = lookup ? Regex.Match(q, "value='([^']*)'").Groups[1].Value : null;
            int size = int.TryParse(query["pageSize"], out var s) ? s : 100;
            int start = int.TryParse(query["pageToken"], out var p) ? p : 0;

            List<Dictionary<string, object>> page;
            int count;
            lock (_gate)
            {
                var rows = _items.Values
                    .Where(i => i.Parent == parent && !i.Trashed && i.Id != Root)
                    .Where(i => tag == null ||
                                (i.AppProperties.TryGetValue(DriveClient.OperationProperty, out var t) && t == tag))
                    .OrderBy(i => i.IsFolder ? 0 : 1).ThenBy(i => i.Name, StringComparer.Ordinal)
                    .ToList();
                count = rows.Count;
                page = rows.Skip(start).Take(size).Select(Describe).ToList();
            }

            var answer = new Dictionary<string, object> { ["files"] = page };
            if (lookup) answer["incompleteSearch"] = false;
            if (start + size < count) answer["nextPageToken"] = (start + size).ToString();
            return Json(200, answer);
        }

        private async Task<HttpResponseMessage> Media(HttpRequestMessage request, string id, CancellationToken ct)
        {
            var item = Get(id);
            if (item == null) return Error(404, "notFound", "File not found");

            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            long from = range?.From ?? 0;
            long to = Math.Min(range?.To ?? item.Data.Length - 1, item.Data.Length - 1);

            Interlocked.Increment(ref MediaReads);
            lock (_gate) MediaOffsets.Add(from);
            if (OnMedia != null) await OnMedia(from, ct);
            ct.ThrowIfCancellationRequested();

            var slice = item.Data.AsSpan((int)from, (int)Math.Max(0, to - from + 1)).ToArray();
            long dropAt = -1;
            if (DropMediaMidBody > 0 && slice.Length > 1)
            {
                DropMediaMidBody--;
                dropAt = slice.Length / 2;
            }

            var content = new StreamContent(new PacedStream(slice, SlowMediaMs, dropAt));
            content.Headers.ContentLength = slice.Length;
            var response = new HttpResponseMessage(range != null ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = content,
            };
            if (range != null) content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, item.Data.Length);
            return response;
        }

        private async Task<HttpResponseMessage> Multipart(HttpRequestMessage request, CancellationToken ct)
        {
            var parts = ((MultipartContent)request.Content!).ToList();
            var meta = JsonDocument.Parse(await parts[0].ReadAsStringAsync(ct)).RootElement;

            var got = new MemoryStream();
            await using (var stream = await parts[1].ReadAsStreamAsync(ct))
            {
                long promised = parts[1].Headers.ContentLength ?? -1;
                bool drop = false;
                if (DropMultipartMidBody > 0 && promised > 1)
                {
                    DropMultipartMidBody--;
                    drop = true;
                }
                var buffer = new byte[64 * 1024];
                int n;
                while ((n = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    got.Write(buffer, 0, n);
                    if (drop && got.Length >= promised / 2)
                        throw new HttpRequestException("fake Drive: the connection was reset part way through the body");
                }
            }

            var name = meta.GetProperty("name").GetString() ?? "";
            AfterMultipartBody?.Invoke(name);

            var item = FromMetadata(meta, got.ToArray());
            Interlocked.Increment(ref Uploads);
            return Json(200, Describe(item));
        }

        private async Task<HttpResponseMessage> Put(HttpRequestMessage request, string sessionId, bool asking, CancellationToken ct)
        {
            Session? session;
            lock (_gate) _sessions.TryGetValue(sessionId, out session);
            if (session == null) return Error(404, "notFound", "No such upload session");

            if (session.Done != null) return Json(200, Describe(session.Done));
            if (asking) return Incomplete(session);

            var range = request.Content!.Headers.ContentRange!;
            long from = range.From ?? 0;
            lock (_gate)
            {
                PutOffsets.Add(from);
                if (from > session.Got.Length) return Incomplete(session);
                session.Got.SetLength(from);
                session.Got.Position = from;
            }

            await using (var stream = await request.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[64 * 1024];
                int n;
                while ((n = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    lock (_gate) session.Got.Write(buffer, 0, n);
                    if (OnPutBytes != null) await OnPutBytes(session.Got.Length, ct);
                    ct.ThrowIfCancellationRequested();
                    if (CutPutsAfterBytes >= 0 && session.Got.Length >= CutPutsAfterBytes)
                    {
                        CutPutsAfterBytes = -1;
                        throw new HttpRequestException("fake Drive: the connection was reset part way through the upload");
                    }
                }
            }

            if (session.Got.Length < session.Total) return Incomplete(session);

            var item = new Item
            {
                Name = session.Name, Parent = session.Parent, Data = session.Got.ToArray(),
                Modified = Ms(session.Modified ?? DateTime.UtcNow),
            };
            lock (_gate)
            {
                item.Id = "d" + (++_next);
                _items[item.Id] = item;
                session.Done = item;
            }
            Interlocked.Increment(ref Uploads);
            return Json(200, Describe(item));
        }

        private static HttpResponseMessage Incomplete(Session session)
        {
            var answer = new HttpResponseMessage((HttpStatusCode)308) { Content = new StringContent("") };
            if (session.Got.Length > 0) answer.Headers.TryAddWithoutValidation("Range", $"bytes=0-{session.Got.Length - 1}");
            return answer;
        }

        private HttpResponseMessage Patch(string id, System.Collections.Specialized.NameValueCollection query, string body)
        {
            lock (_gate)
            {
                if (!_items.TryGetValue(id, out var item)) return Error(404, "notFound", "File not found");
                var json = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body).RootElement;
                if (json.TryGetProperty("trashed", out var t) && t.ValueKind == JsonValueKind.True)
                {
                    item.Trashed = true;
                    Trashes++;
                }
                if (json.TryGetProperty("name", out var name))
                {
                    item.Name = name.GetString() ?? item.Name;
                    Renames++;
                }
                if (json.TryGetProperty("modifiedTime", out var m)) item.Modified = ParseTime(m.GetString()) ?? item.Modified;
                if (query["addParents"] is { Length: > 0 } to) item.Parent = to;
                return Json(200, Describe(item));
            }
        }

        private Item FromMetadata(JsonElement meta, byte[] data, string? name = null, DateTime? modified = null,
            string? mime = null)
        {
            var item = new Item
            {
                Name = meta.TryGetProperty("name", out var n) ? n.GetString() ?? "" : name ?? "",
                Parent = meta.TryGetProperty("parents", out var p) ? p[0].GetString() ?? Root : Root,
                MimeType = meta.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : mime ?? "application/octet-stream",
                Data = data,
                Modified = meta.TryGetProperty("modifiedTime", out var t) ? ParseTime(t.GetString()) ?? DateTime.UtcNow
                    : modified ?? Ms(DateTime.UtcNow),
            };
            if (meta.TryGetProperty("appProperties", out var props))
                foreach (var prop in props.EnumerateObject()) item.AppProperties[prop.Name] = prop.Value.GetString() ?? "";

            lock (_gate)
            {
                item.Id = (item.IsFolder ? "f" : "d") + (++_next);
                _items[item.Id] = item;
            }
            return item;
        }

        private static DateTime? ParseTime(string? text) =>
            DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var t)
                ? Ms(DateTime.SpecifyKind(t, DateTimeKind.Utc))
                : null;

        private static Dictionary<string, object> Describe(Item item)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = item.Id,
                ["name"] = item.Name,
                ["mimeType"] = item.MimeType,
                ["modifiedTime"] = DriveClient.RfcTime(item.Modified),
                ["trashed"] = item.Trashed,
            };
            if (!item.IsFolder && !item.IsDocument)
            {
                d["size"] = item.Data.Length.ToString();
                d["md5Checksum"] = item.Md5;
            }
            return d;
        }

        private static HttpResponseMessage Json(int status, object value) =>
            new((HttpStatusCode)status)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
            };

        private static HttpResponseMessage Error(int status, string reason, string message) =>
            new((HttpStatusCode)status)
            {
                Content = new StringContent(ErrorBody(status, reason, message), Encoding.UTF8, "application/json"),
            };

        /// <summary>A body that arrives in pieces, slowly if asked, and can break off part way.</summary>
        private sealed class PacedStream : Stream
        {
            private readonly byte[] _data;
            private readonly int _delayMs;
            private readonly long _dropAt;
            private int _at;

            public PacedStream(byte[] data, int delayMs, long dropAt)
            {
                _data = data;
                _delayMs = delayMs;
                _dropAt = dropAt;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                if (_delayMs > 0) await Task.Delay(_delayMs, token);
                if (_dropAt >= 0 && _at >= _dropAt) throw new IOException("fake Drive: the connection was reset mid-body");
                int n = Math.Min(Math.Min(buffer.Length, 64 * 1024), _data.Length - _at);
                if (_dropAt >= 0) n = (int)Math.Min(n, Math.Max(1, _dropAt - _at));
                _data.AsMemory(_at, n).CopyTo(buffer);
                _at += n;
                return n;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _data.Length;
            public override long Position { get => _at; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
