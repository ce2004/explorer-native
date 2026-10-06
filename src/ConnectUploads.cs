using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Uploads from the web app that survive a dropped connection: the bytes go to a partial file of their own
    /// under <c>%LOCALAPPDATA%\ExplorerNative\connect-uploads\&lt;id&gt;</c>, a chunk at a time, and the web app
    /// can always ask how much has arrived and carry on from exactly there.
    ///
    /// Nothing is held in memory: every chunk is streamed from the socket to the end of the file. How much
    /// has been received is the file's length, so a chunk cut off half way still counts for what landed.
    /// </summary>
    public sealed class ConnectUploads
    {
        public sealed record Meta(string Folder, string Name, long Size, string Conflict, DateTime Started);

        private const string DataName = "data";
        private const string MetaName = "upload.json";

        private readonly string _root;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _writing = new(StringComparer.Ordinal);

        public ConnectUploads(string root) => _root = root;

        /// <summary>How long a chunk's body may go without a byte before it is given up on.</summary>
        public TimeSpan StallAfter { get; set; } = TimeSpan.FromSeconds(30);

        public static string DefaultRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExplorerNative", "connect-uploads");

        private string Dir(string id) => Path.Combine(_root, id);

        /// <summary>The partial file itself.</summary>
        public string DataPath(string id) => Path.Combine(Dir(id), DataName);

        /// <summary>Ids are ours, sixteen hex digits; anything else never reaches a path.</summary>
        private static bool Wellformed(string? id) =>
            id is { Length: 16 } && id.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

        public string Start(Meta meta)
        {
            var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var dir = Directory.CreateDirectory(Dir(id));
            try { dir.Attributes |= FileAttributes.Hidden; } catch { }
            ProtectedFile.WriteAllText(Path.Combine(dir.FullName, MetaName), JsonSerializer.Serialize(meta));
            using (File.Create(DataPath(id))) { }
            return id;
        }

        public Meta? Find(string? id)
        {
            if (!Wellformed(id)) return null;
            try { return JsonSerializer.Deserialize<Meta>(ProtectedFile.ReadAllText(Path.Combine(Dir(id!), MetaName))); }
            catch { return null; }
        }

        public long Received(string id)
        {
            try { return new FileInfo(DataPath(id)).Length; }
            catch { return 0; }
        }

        /// <summary>
        /// Appends <paramref name="body"/> at <paramref name="offset"/>, which has to be exactly what has been
        /// received; anything else, or a second chunk while one is still arriving, is a 409 carrying the number
        /// to resume from. Returns the new total.
        /// </summary>
        public async Task<long> AppendAsync(string id, long offset, Stream body, CancellationToken token)
        {
            var meta = Find(id) ?? throw new ConnectException(404, "There is no upload with that id.");
            var gate = _writing.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0, token))
                throw new ConnectException(409, "A chunk of this upload is still arriving.", Received(id));
            try
            {
                long received = Received(id);
                if (offset != received)
                    throw new ConnectException(409, $"The upload holds {received} bytes, so the next chunk starts there.", received);

                await using var file = new FileStream(DataPath(id), FileMode.Append, FileAccess.Write, FileShare.Read,
                    1 << 20, useAsync: true);
                var buffer = new byte[1 << 20];
                // A body that stops arriving holds this upload's gate, and every retry meets "still arriving"
                // until the dead socket is noticed, which took over a minute. No byte for StallAfter and it is
                // given up on: what landed stays, and the gate is free for the next chunk.
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
                while (true)
                {
                    stall.CancelAfter(StallAfter);
                    int got;
                    try { got = await body.ReadAsync(buffer, stall.Token); }
                    catch (Exception e) when (e is OperationCanceledException or IOException &&
                                              stall.IsCancellationRequested && !token.IsCancellationRequested)
                    {
                        throw new TimeoutException("the chunk stopped arriving");
                    }
                    if (got <= 0) break;
                    if (file.Position + got > meta.Size)
                        throw new ConnectException(400, $"That is more than the {meta.Size} bytes the upload said it would be.", file.Position);
                    await file.WriteAsync(buffer.AsMemory(0, got), token);
                }
                await file.FlushAsync(token);
                return file.Position;
            }
            finally
            {
                gate.Release();
                // Cancelled while this chunk was arriving: the folder could not go then, with the file open, and
                // would have waited for the start-up sweep. It goes now that the chunk has let go of it.
                if (_cancelled.TryRemove(id, out _)) Delete(id);
            }
        }

        /// <summary>Uploads cancelled while a chunk of theirs was still arriving.</summary>
        private readonly ConcurrentDictionary<string, byte> _cancelled = new(StringComparer.Ordinal);

        /// <summary>
        /// Takes the upload away. Done under the upload's own gate; with a chunk still arriving it is marked, and
        /// the chunk removes the folder when it lets go, so nothing is left for the sweep a day later.
        /// </summary>
        public void Delete(string id)
        {
            if (!Wellformed(id)) return;
            var gate = _writing.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
            if (!gate.Wait(0))
            {
                _cancelled[id] = 1;
                // What can go now goes now, so the upload is gone to every question asked of it.
                try { File.Delete(Path.Combine(Dir(id), MetaName)); } catch { }
                // The chunk may have let go between the two looks.
                if (!gate.Wait(0)) return;
                _cancelled.TryRemove(id, out _);
            }
            try
            {
                try { Directory.Delete(Dir(id), recursive: true); } catch { }
            }
            finally
            {
                _writing.TryRemove(id, out _);
                gate.Release();
            }
        }

        /// <summary>
        /// Takes away uploads nobody has touched for <paramref name="olderThan"/>. One level only: each upload is
        /// a folder of two files, and nothing here walks further than that.
        /// </summary>
        public int Sweep(TimeSpan olderThan)
        {
            int swept = 0;
            try
            {
                if (!Directory.Exists(_root)) return 0;
                foreach (var dir in Directory.EnumerateDirectories(_root))
                {
                    var id = Path.GetFileName(dir);
                    if (!Wellformed(id)) continue;
                    DateTime touched;
                    try
                    {
                        var data = new FileInfo(Path.Combine(dir, DataName));
                        touched = data.Exists ? data.LastWriteTimeUtc : Directory.GetLastWriteTimeUtc(dir);
                    }
                    catch { continue; }
                    if (DateTime.UtcNow - touched < olderThan) continue;
                    try { Directory.Delete(dir, recursive: true); swept++; } catch { }
                }
            }
            catch { }
            return swept;
        }
    }
}
