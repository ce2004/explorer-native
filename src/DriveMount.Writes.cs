using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// What other programs write on the drive letter, on its way to Google,
    /// with nothing kept on the PC's disk (Conner, 4.3.0: "uploading should not
    /// use the PC's space but other apps should still be able to write to it").
    ///
    /// A file being written is a <see cref="Pending"/>: what Google already
    /// has of it (its base), and what has been written here since, by offset,
    /// in memory (<see cref="ByteOverlay"/>). A small file goes up in one
    /// request <see cref="Settle"/> after its last handle closes, so scratch
    /// files programs make and delete while saving never reach Google. A big
    /// new one goes up while it is written: a resumable session opened with no
    /// length, sent a piece (<see cref="ChunkBytes"/>) at a time as the
    /// writing gets ahead. A file left open but not written to for
    /// <see cref="IdleCommit"/> goes up too (Word keeps a saved document open).
    ///
    /// Two locks per file. <c>Gate</c> guards its state and is never held
    /// across the network, so listing its folder, opening it and asking its
    /// size never wait on Google. <c>Send</c> lets one upload at a time touch
    /// its session, and is what is held across the network. Bytes under a
    /// piece being (or already) sent, below <c>FrozenBelow</c>, are not
    /// rewritten: a write there is a late patch, sent by the pass after the
    /// upload finishes, which reads everything else back from Drive. A cut
    /// below what has gone settles the upload first.
    ///
    /// Memory is bounded by what the buffers hold (<see cref="ByteOverlay.Allocated"/>):
    /// past <see cref="HoldMost"/> across files, a writer that cannot make room
    /// itself waits while the others send what they hold.
    ///
    /// Lock order: a hold gate, then <c>_treeGate</c>, then a file's Gate.
    /// </summary>
    internal sealed partial class DriveMount
    {
        /// <summary>Where an upload put a file: its name, folder and date as sent.</summary>
        internal readonly record struct SentAs(string Name, string? ParentId, DateTime Modified);

        internal sealed class Pending
        {
            /// <summary>State. Never held across a network call.</summary>
            public readonly object Gate = new();
            /// <summary>One upload at a time: what is held across the network.</summary>
            public readonly SemaphoreSlim Send = new(1, 1);
            /// <summary>Cancels an upload under way, for a cut that drops its session or a delete.</summary>
            public CancellationTokenSource Abort = new();

            public readonly string Identity = Guid.NewGuid().ToString("N");
            public string Key = "";
            public string Name = "";
            public string? Id;
            public string? ParentId;
            public bool HasBase;
            public long BaseSize;
            public long DriveSize;
            public long Size;
            public DateTime ModifiedUtc = DateTime.UtcNow;
            public ByteOverlay Data = new();

            public string? Session;
            internal SentAs SessionAs;
            public long Sent;
            /// <summary>Held bytes below this went to the session and are no longer here.</summary>
            public long LetGo;
            /// <summary>Writes below this go to Late: they would land under a piece being (or already) sent.</summary>
            public long FrozenBelow;
            /// <summary>The total the last piece named, once it has been sent: the session can only end there.</summary>
            public long FinalTotal;
            /// <summary>A piece failed: Google may hold more than Sent, so it is asked before more is sent.</summary>
            public bool Unsure;
            public ByteOverlay? Late;

            public int Handles;
            /// <summary>Writes under way, its own pieces included: a file being written is never taken for idle.</summary>
            public int Writing;
            /// <summary>Chosen to be sent as the mount goes: it was not being written then.</summary>
            public bool Drain;
            /// <summary>What writes are told once Google has refused the file for good.</summary>
            public int RefusedStatus;
            /// <summary>
            /// A new file's one-request create, kept across tries: the tag it is
            /// made with, the folder it was sent to, and whether a try went out.
            /// A try whose answer was lost is found by its tag, never sent twice.
            /// </summary>
            public string? CreateTag;
            public string? CreateParent;
            public bool CreateTried;
            public bool Retired;
            public volatile bool Deleted;
            public bool Dirty;
            public volatile bool Uploading;
            public long DueAt = long.MaxValue;
            public long NotBefore;
            public long LastWrite = Environment.TickCount64;
            public int Failures;

            public long Held => Data.Allocated + (Late?.Allocated ?? 0);
        }

        /// <summary>Where a file Google refuses is kept for its owner, when its bytes can still be put together.</summary>
        public string UnsavedFolder { get; init; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Explorer Native", "Not saved to Google Drive");

        /// <summary>How long after its last handle closes a small file waits. Settable for tests.</summary>
        internal static TimeSpan Settle = TimeSpan.FromSeconds(3);

        /// <summary>
        /// How long a file left open with nothing written goes before it goes up
        /// anyway: Word and Excel keep a document open after saving it, and the
        /// save must not wait for the document to be closed. Settable for tests.
        /// </summary>
        internal static TimeSpan IdleCommit = TimeSpan.FromSeconds(30);

        internal const int MemoryFileBytes = 4 * 1024 * 1024;
        internal const int ChunkBytes = 8 * 1024 * 1024;

        /// <summary>Held for one file past which it sends what it has, gaps as zeros (patched later). Settable for tests.</summary>
        internal static long FileMost = 128L * 1024 * 1024;

        /// <summary>Held across every file past which writers that cannot make room wait. Settable for tests.</summary>
        internal static long HoldMost = 512L * 1024 * 1024;

        /// <summary>How long a write waits for room, or a cut for an upload to settle, before it fails.</summary>
        private static readonly TimeSpan WriteDeadline = TimeSpan.FromMinutes(2);

        /// <summary>
        /// The most one read of a pending file may take, all its waits together:
        /// under WinFsp's IrpTimeout (5 min), past which Windows refuses the file
        /// until the app restarts. A write's waits add up to the same (room, then
        /// waiting for its upload, then its pieces).
        /// </summary>
        private static readonly TimeSpan CallbackMost = TimeSpan.FromMinutes(4);

        /// <summary>How long pieces sent from inside a write may take before the write gives up on them (and keeps the bytes).</summary>
        internal static TimeSpan PieceDeadline = TimeSpan.FromSeconds(60);

        /// <summary>The most pieces one write sends: the rest go with the next writes, or after the close.</summary>
        private const int PiecesPerWrite = 2;

        private const int UploadLanes = 4;
        private const long SayFromBytes = 16L * 1024 * 1024;

        private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _uploadedAt = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, object> _holdGates = new(StringComparer.Ordinal);

        /// <summary>Keys change (moves, renames, removals) and uploads land one at a time, so neither lands under the other's old name.</summary>
        private readonly object _treeGate = new();

        private readonly SemaphoreSlim _uploadWake = new(0, int.MaxValue);
        private readonly CancellationTokenSource _uploadStop = new();
        private readonly object _roomGate = new();
        private Task? _uploadLoop;
        private int _savedSinceSaid;
        private string? _lastSavedName;
        private volatile bool _stopping;
        private volatile bool _draining;
        /// <summary>Until when a writer is waiting for room: open files idle for a moment are sent too.</summary>
        private long _squeezedUntil;

        public int UploadsPending => _pending.Values.Count(p => !p.Deleted && (p.Dirty || p.Uploading));

        public long BytesHeld => _pending.Values.Sum(p => p.Held);

        /// <summary>The names of files still on their way to Google, for the question before quitting.</summary>
        public IReadOnlyList<string> UploadNames =>
            _pending.Values.Where(p => !p.Deleted && (p.Dirty || p.Uploading)).Select(p => p.Name).ToList();

        // ---- what is pending where ------------------------------------------------

        internal Pending? PendingAt(string key) =>
            _pending.TryGetValue(key, out var p) && !p.Deleted && !p.Retired ? p : null;

        private bool HasPending(string key) => PendingAt(key) != null;

        /// <summary>
        /// A file saved on the letter that Drive does not have yet (no id: it is
        /// still on its way up), and whether a program still has it open.
        /// </summary>
        internal bool IsNewNotLanded(string fullPath, out bool open)
        {
            open = false;
            var key = KeyFor(fullPath);
            if (key == null || PendingAt(key) is not { } p) return false;
            lock (p.Gate) { open = p.Handles > 0; return p.Id == null; }
        }

        private IEnumerable<Pending> PendingIn(string relative)
        {
            foreach (var p in _pending.Values)
                if (!p.Deleted && !p.Retired && (Parent(p.Key) ?? "") == relative) yield return p;
        }

        private bool UploadedRecently(string id) =>
            _uploadedAt.TryGetValue(id, out var at) && Environment.TickCount64 - at < 60_000;

        private DrivePlaced[] Overlay(string relative, DrivePlaced[] rows)
        {
            if (_pending.IsEmpty) return rows;
            DrivePlaced[]? changed = null;
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                if (row.IsFolder || PendingAt(ChildKey(relative, row.Name)) is not { } p) continue;
                long size;
                DateTime modified;
                lock (p.Gate) { size = p.Size; modified = p.ModifiedUtc.ToLocalTime(); }
                if (row.Size == size && row.Modified == modified) continue;
                changed ??= (DrivePlaced[])rows.Clone();
                changed[i] = row with { Size = size, Modified = modified };
            }
            return changed ?? rows;
        }

        private string? ParentIdOf(Pending p) => FolderIdAt(Parent(p.Key) ?? "") ?? p.ParentId;

        // ---- making and keeping pending files ----------------------------------------

        internal Pending? NewPending(string parent, string name, string parentId)
        {
            var p = new Pending { Key = ChildKey(parent, name), Name = name, ParentId = parentId, Dirty = true };
            lock (_treeGate)
            {
                _pending[p.Key] = p;
                var row = new DrivePlaced(name, false, 0, DateTime.Now, name);
                _listings.AddOrUpdate(parent, new[] { row }, (_, now) => now
                    .Where(r => !r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Append(row).ToArray());
            }
            StartUploads();
            return p;
        }

        /// <summary>
        /// An open Drive file made pending, ready to be written. Nothing is
        /// downloaded. Its size and id come from what the mount knows now, not
        /// what this handle saw when it opened: another handle may have grown
        /// the file and sent it since.
        /// </summary>
        private int Hold(Opened opened, bool keepContents)
        {
            for (int round = 0; round < 6; round++)
            {
                // Attached: a cut is done here, outside the hold gate, since it
                // can wait for an upload.
                if (opened.Local != null) return keepContents ? WinFsp.Success : SetLengthPending(opened.Local, 0);

                var key = opened.Key;
                var gate = _holdGates.GetOrAdd(key, new object());
                lock (gate)
                {
                    // One being let go this moment refuses the handle; the next round looks again.
                    if (PendingAt(key) is { } existing) { TryAttach(opened, existing); continue; }

                    var row = RowAt(key);
                    var id = _ids.TryGetValue(key, out var bound) ? bound : opened.Id;
                    if (id == null) return WinFsp.ObjectNameNotFound;
                    long size = row is { IsFolder: false } ? Math.Max(0, row.Size) : opened.Size;
                    var modified = row?.Modified ?? opened.Modified;
                    var p = new Pending
                    {
                        Key = key,
                        Name = row?.Name ?? LeafOf(key),
                        Id = id,
                        ParentId = FolderIdAt(Parent(key) ?? ""),
                        HasBase = keepContents,
                        BaseSize = keepContents ? size : 0,
                        DriveSize = size,
                        Size = keepContents ? size : 0,
                        Dirty = !keepContents,
                        ModifiedUtc = modified != default ? modified.ToUniversalTime() : DateTime.UtcNow,
                    };
                    lock (_treeGate)
                    {
                        // Renamed while this was being made: looked at again under its new name.
                        if (opened.Key != key || PendingAt(key) != null) continue;
                        _pending[key] = p;
                    }
                    if (!TryAttach(opened, p)) continue;
                    opened.Id = id;
                }
                StartUploads();
                return WinFsp.Success;
            }
            return WinFsp.UnexpectedIoError;
        }

        /// <summary>A handle on a pending file. False when it has just been let go, and the caller looks again.</summary>
        private static bool TryAttach(Opened opened, Pending p)
        {
            lock (p.Gate)
            {
                if (p.Retired) return false;
                p.Handles++;
            }
            opened.Local = p;
            return true;
        }

        private void Detach(Opened opened)
        {
            var p = opened.Local;
            opened.Local = null;
            if (p == null) return;

            bool retire = false;
            lock (p.Gate)
            {
                p.Handles = Math.Max(0, p.Handles - 1);
                if (p.Handles > 0) return;
                if (_stopping) return;
                if (p.Deleted || !p.Dirty) retire = true;
                else
                {
                    bool now = p.Session != null || p.Size > MemoryFileBytes || BytesHeld > HoldMost / 2;
                    p.DueAt = Environment.TickCount64 + (now ? 0 : (long)Settle.TotalMilliseconds);
                }
            }
            if (retire) Retire(p);
            else KickUploads();
        }

        /// <summary>Lets a pending file go once nothing holds it and nothing is left to send, marked so no handle attaches as it goes.</summary>
        private void Retire(Pending p)
        {
            lock (p.Gate)
            {
                if (p.Retired || p.Handles > 0 || p.Uploading || (p.Dirty && !p.Deleted)) return;
                if (p.Send.CurrentCount == 0) return;
                p.Retired = true;
            }
            lock (_treeGate) _pending.TryRemove(new KeyValuePair<string, Pending>(p.Key, p));
            _holdGates.TryRemove(p.Key, out _);
            Roomier();
        }

        // ---- reading and writing ----------------------------------------------------------

        /// <summary>
        /// What a pending file holds at an offset: late patches, then what was
        /// written, then its base on Drive, then zeros. Bytes that went to an
        /// upload and are no longer held are read back from Drive once that
        /// upload is finished, which this waits for (or does).
        /// </summary>
        private unsafe int ReadPending(Pending p, IntPtr buffer, ulong offset, uint length, out uint transferred)
        {
            transferred = 0;
            using var limit = Deadline(CallbackMost);
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    long start = (long)offset;
                    int count;
                    bool gone;
                    List<(long Start, long End)>? gaps = null;
                    string? baseId = null;
                    long driveSize = 0;
                    lock (p.Gate)
                    {
                        if (start >= p.Size) return WinFsp.EndOfFile;
                        count = (int)Math.Min(length, (ulong)(p.Size - start));
                        long end = start + count;
                        gone = p.Session != null && start < p.LetGo &&
                               (p.Late == null || !p.Late.Covers(start, Math.Min(end, p.LetGo)));
                        if (!gone)
                        {
                            var into = new Span<byte>((void*)buffer, count);
                            into.Clear();
                            p.Data.CopyTo(start, into);
                            p.Late?.CopyTo(start, into);
                            if (p.HasBase && p.Id != null && start < p.BaseSize)
                            {
                                gaps = p.Data.Gaps(start, Math.Min(end, p.BaseSize)).ToList();
                                if (p.Late != null) gaps = gaps.SelectMany(g => p.Late.Gaps(g.Start, g.End)).ToList();
                                baseId = p.Id;
                                driveSize = p.DriveSize;
                            }
                        }
                    }

                    if (gone)
                    {
                        // What went is in Google's hands and not in ours: the upload
                        // so far is finished, then it is read back from there.
                        if (!p.Send.Wait(CallbackMost, limit.Token)) return WinFsp.IoTimeout;
                        try
                        {
                            bool still;
                            lock (p.Gate) still = p.Session != null && start < p.LetGo;
                            if (still) CommitHeld(p, limit.Token, passes: 1);
                        }
                        finally { p.Send.Release(); }
                        Roomier();
                        continue;
                    }

                    if (gaps is { Count: > 0 } && baseId != null)
                    {
                        var into = new Span<byte>((void*)buffer, count);
                        foreach (var (gs, ge) in gaps)
                            ReadBase(baseId, driveSize, p.Name, gs, into.Slice((int)(gs - start), (int)(ge - gs)), limit.Token);
                    }
                    transferred = (uint)count;
                    return WinFsp.Success;
                }
                return WinFsp.IoTimeout;
            }
            catch (Exception ex)
            {
                _log($"  read of pending {p.Name} failed: {ex.Message}");
                return StatusOf(ex);
            }
        }

        private unsafe int WritePending(Pending p, IntPtr buffer, ulong offset, uint length, bool toEnd,
            bool constrained, out uint transferred)
        {
            transferred = 0;
            lock (p.Gate)
            {
                // Refused by Google: the program is told, rather than its writes
                // going into a file nothing will send. Deleted: like a file
                // unlinked under an open handle, the write goes nowhere.
                if (p.RefusedStatus != 0) return p.RefusedStatus;
                if (p.Deleted) { transferred = length; return WinFsp.Success; }
                p.Writing++;
            }
            try
            {
                if (_drive.Health.IsOffline && p.Held > FileMost) return WinFsp.NetworkUnreachable;
                // Over its own hard limit (another upload of it holding the
                // session, say): it waits for that, sends, and only then fails.
                if (p.Held > FileMost * 2)
                {
                    StreamSome(p, wait: true);
                    if (p.Held > FileMost * 2) return WinFsp.DiskFull;
                }
                if (!WaitForRoom(p)) return _drive.Health.IsOffline ? WinFsp.NetworkUnreachable : WinFsp.DiskFull;
                bool stream, must;
                try
                {
                    lock (p.Gate)
                    {
                        long at = toEnd ? p.Size : (long)offset;
                        long count = length;
                        if (constrained)
                        {
                            if (at >= p.Size) return WinFsp.Success;
                            count = Math.Min(count, p.Size - at);
                        }
                        Put(p, at, new ReadOnlySpan<byte>((void*)buffer, (int)count));
                        p.Size = Math.Max(p.Size, at + count);
                        p.Dirty = true;
                        p.ModifiedUtc = DateTime.UtcNow;
                        p.LastWrite = Environment.TickCount64;
                        transferred = (uint)count;
                        stream = WantsToStream(p);
                        must = p.Held > FileMost || (BytesHeld > HoldMost && p.Held > MemoryFileBytes);
                    }
                }
                catch (Exception ex)
                {
                    _log($"  write to {p.Name} failed: {ex.Message}");
                    return StatusOf(ex);
                }
                return stream ? StreamSome(p, wait: must) : WinFsp.Success;
            }
            finally
            {
                lock (p.Gate)
                {
                    p.Writing--;
                    p.LastWrite = Environment.TickCount64;
                }
            }
        }

        /// <summary>Into a pending file: under a piece being or already sent, as a late patch; else into what is held.</summary>
        private static void Put(Pending p, long at, ReadOnlySpan<byte> data)
        {
            long frozen = p.FrozenBelow;
            if (at < frozen)
            {
                int below = (int)Math.Min(data.Length, frozen - at);
                (p.Late ??= new ByteOverlay()).Write(at, data[..below]);
                if (below < data.Length) p.Data.Write(frozen, data[below..]);
            }
            else p.Data.Write(at, data);
        }

        /// <summary>
        /// A size set or cut. A cut below what has gone settles the upload
        /// first: when nothing it sent has been let go (an edit), or the file
        /// is cut to nothing, its session is simply dropped; otherwise it is
        /// finished. Then the cut applies to a file with no upload open.
        /// </summary>
        private int SetLengthPending(Pending p, long size)
        {
            bool cancelled = false;
            lock (p.Gate)
            {
                if (p.RefusedStatus != 0) return p.RefusedStatus;
                if (p.Deleted || size == p.Size) return WinFsp.Success;
                bool settle = size < p.Size && p.FrozenBelow > 0 && size < p.FrozenBelow;
                if (!settle) { Cut(p, size); return WinFsp.Success; }
                // An upload that is to be dropped is stopped now, unless it may be
                // the one that makes the file (its last piece, or a small file's
                // one request): stopped then, Google could keep a file nobody
                // knows of, and the next save would make a second.
                if (p.Session != null && (p.LetGo == 0 || size == 0) && !MayFinish(p))
                {
                    try { p.Abort.Cancel(); } catch { }
                    cancelled = true;
                }
            }

            bool took = false;
            using var limit = Deadline(WriteDeadline);
            try
            {
                try { took = p.Send.Wait(WriteDeadline, limit.Token); }
                catch (OperationCanceledException) { }
                if (!took) return WinFsp.IoTimeout;

                bool finish = false;
                lock (p.Gate)
                {
                    if (p.Session != null)
                    {
                        // A last piece already sent is settled with Google first
                        // (finished, or sent to its end), so the file it may have
                        // made is this one's from now on.
                        if ((p.LetGo == 0 || size == 0) && p.FinalTotal == 0) DropSession(p);
                        else finish = true;
                    }
                }
                if (finish) CommitHeld(p, limit.Token, passes: 1);
                lock (p.Gate) Cut(p, size);
                return WinFsp.Success;
            }
            catch (Exception ex)
            {
                _log($"  cutting {p.Name} failed: {ex.Message}");
                return StatusOf(ex);
            }
            finally
            {
                // Always, even when the cut gave up: a cancel left standing would
                // stop every later upload of the file at once.
                if (cancelled)
                    lock (p.Gate)
                        if (p.Abort.IsCancellationRequested && !p.Deleted) p.Abort = new CancellationTokenSource();
                if (took) p.Send.Release();
                Roomier();
            }
        }

        /// <summary>Under Gate: whether a request now on its way may be the one that makes the file on Google.</summary>
        private static bool MayFinish(Pending p) => p.FrozenBelow == long.MaxValue || p.FinalTotal > 0;

        /// <summary>Under Gate, with no upload open below the cut: the size changed.</summary>
        private static void Cut(Pending p, long size)
        {
            if (size < p.Size)
            {
                p.Data.Trim(size);
                p.Late?.Trim(size);
                p.BaseSize = Math.Min(p.BaseSize, size);
            }
            p.Size = size;
            p.Dirty = true;
            p.ModifiedUtc = DateTime.UtcNow;
            p.LastWrite = Environment.TickCount64;
        }

        /// <summary>
        /// Under Gate: an upload given up. Everything it sent is still held
        /// (nothing was let go), so the session is simply forgotten, with the
        /// late patches laid over the rest.
        /// </summary>
        private static void DropSession(Pending p)
        {
            if (p.Late != null) p.Data.Absorb(p.Late);
            p.Late = null;
            p.Session = null;
            p.Sent = 0;
            p.LetGo = 0;
            p.FrozenBelow = 0;
            p.FinalTotal = 0;
            p.Unsure = false;
        }

        /// <summary>Under Gate: whether this write leaves enough held that some of it should go now.</summary>
        private bool WantsToStream(Pending p)
        {
            if (_drive.Health.IsOffline || p.Deleted) return false;
            // An upload whose last piece broke is finished before anything more goes.
            if (p.FinalTotal > 0) return true;
            if (LateTooBig(p)) return true;
            if (p.Data.Allocated <= MemoryFileBytes) return false;
            return SendableNow(p) > 0;
        }

        private bool Pressed(Pending p) => p.Data.Allocated > FileMost || BytesHeld > HoldMost;

        /// <summary>
        /// Under Gate: whether the late patches hold enough that the upload
        /// should be finished now, so they can be sent: past
        /// <see cref="FileMost"/>, or a few megabytes while memory is short.
        /// </summary>
        private bool LateTooBig(Pending p) =>
            p.Late != null && (p.Late.Allocated > FileMost ||
                               (p.Late.Allocated > MemoryFileBytes && BytesHeld > HoldMost));

        /// <summary>
        /// Under Gate: how much from Sent could go now, whole pieces only, the
        /// last byte always kept back for the piece that names the total. A new
        /// file sends what is written straight on from Sent; an edit waits for
        /// its close, unless memory runs short, when anything sends what it has.
        /// </summary>
        private long SendableNow(Pending p)
        {
            long target;
            if (Pressed(p)) target = Math.Max(p.Data.End, p.Sent);
            else if (p.HasBase) target = p.Sent;
            else target = p.Data.ContiguousEnd(p.Sent);
            return Math.Max(0, (target - p.Sent - 1) / ChunkBytes * ChunkBytes);
        }

        /// <summary>
        /// A few pieces of a big file sent from the write that made them ready.
        /// Never waits for another upload of the same file, and a piece that
        /// will not go now is kept for the next write; only a file Google
        /// refuses, or one over its limits, fails the write.
        /// </summary>
        private int StreamSome(Pending p, bool wait = false)
        {
            if (!p.Send.Wait(0))
            {
                // Another upload of it has the session. A file holding more than
                // it should waits its turn (bounded), so its memory cannot grow
                // while that upload runs; any other just carries on.
                if (!wait) return WinFsp.Success;
                try
                {
                    using var turn = Deadline(PieceDeadline, p.Abort.Token);
                    if (!p.Send.Wait(PieceDeadline, turn.Token)) return WinFsp.Success;
                }
                catch (OperationCanceledException) { return WinFsp.Success; }
            }
            try
            {
                using var limit = Deadline(PieceDeadline, p.Abort.Token);
                for (int piece = 0; piece < PiecesPerWrite; piece++)
                {
                    bool finish;
                    lock (p.Gate) finish = p.FinalTotal > 0 || LateTooBig(p);
                    if (finish) { CommitHeld(p, limit.Token, passes: 1); break; }
                    if (!SendPieces(p, final: false, limit.Token, out _, most: 1)) break;
                }
                return WinFsp.Success;
            }
            catch (Exception ex)
            {
                _log($"  {p.Name}: a piece could not go yet: {ex.Message}");
                bool over;
                lock (p.Gate) over = p.Held > FileMost * 2;
                return ex is DriveQuotaException || Refused(ex) || over ? StatusOf(ex) : WinFsp.Success;
            }
            finally
            {
                p.Send.Release();
                Roomier();
            }
        }

        private CancellationTokenSource Deadline(TimeSpan most, CancellationToken also = default)
        {
            var limit = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token, also);
            limit.CancelAfter(most);
            return limit;
        }

        /// <summary>
        /// Waits while more is held than <see cref="HoldMost"/>. A file that can
        /// make room itself (it holds enough to send) is let through; the rest
        /// wait while closed files and open ones idle for a moment are sent, so
        /// room always comes. Offline nothing can be sent, so it fails at once.
        /// </summary>
        private bool WaitForRoom(Pending p)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            lock (_roomGate)
            {
                while (BytesHeld > HoldMost && p.Held <= MemoryFileBytes)
                {
                    if (_drive.Health.IsOffline) return false;
                    if (_closing.IsCancellationRequested || clock.Elapsed > WriteDeadline) return false;
                    Volatile.Write(ref _squeezedUntil, Environment.TickCount64 + 1000);
                    foreach (var other in _pending.Values)
                        lock (other.Gate)
                            if (other.Dirty && other.Handles == 0) other.DueAt = 0;
                    KickUploads();
                    Monitor.Wait(_roomGate, 250);
                }
            }
            return true;
        }

        private void Roomier()
        {
            lock (_roomGate) Monitor.PulseAll(_roomGate);
        }

        /// <summary>A pending file's base on Drive, through the same chunk cache the letter's reads use.</summary>
        private void ReadBase(string id, long driveSize, string name, long at, Span<byte> into, CancellationToken token)
        {
            var cache = CacheOpenedFor(id, driveSize);
            try
            {
                long end = at + into.Length;
                long done = at;
                while (done < end)
                {
                    long pieceEnd = Math.Min(end, Math.Min(
                        (done / DriveCachePool.ChunkBytes + 1) * DriveCachePool.ChunkBytes, done + PieceBytes));
                    int want = (int)(pieceEnd - done);
                    var (bytes, _) = cache.Read(done, want, token).GetAwaiter().GetResult();
                    if (bytes.Length < want)
                        throw new IOException($"Google sent {bytes.Length:N0} of {want:N0} bytes of {name}");
                    bytes.AsSpan(0, want).CopyTo(into.Slice((int)(done - at), want));
                    done = pieceEnd;
                }
            }
            finally { cache.EndLease(warm: false); }
        }

        /// <summary>
        /// Sends pieces from Sent: up to <paramref name="most"/> of what is
        /// ready, or with <paramref name="final"/> everything up to the size,
        /// the last piece naming the total, and the file made through
        /// <paramref name="done"/>. Caller holds Send. Returns whether a piece went.
        /// </summary>
        private bool SendPieces(Pending p, bool final, CancellationToken token, out DriveClient.DriveUploaded? done,
            int most = int.MaxValue)
        {
            done = null;
            string? session;
            bool unsure;
            string name;
            lock (p.Gate) { session = p.Session; unsure = p.Unsure; name = p.Name; }

            try
            {
                if (session == null)
                {
                    string parentId;
                    DateTime modified;
                    string? replace;
                    lock (p.Gate)
                    {
                        parentId = ParentIdOf(p) ?? throw new IOException("the folder it was in is gone");
                        modified = p.ModifiedUtc;
                        replace = p.Id;
                    }
                    var opened = _drive.BeginStream(name, parentId, modified, replace, token).GetAwaiter().GetResult();
                    bool say;
                    lock (p.Gate)
                    {
                        p.Session = opened;
                        p.SessionAs = new SentAs(name, parentId, modified);
                        p.Sent = 0;
                        p.LetGo = 0;
                        p.FrozenBelow = 0;
                        p.FinalTotal = 0;
                        p.Unsure = false;
                        session = opened;
                        say = p.Size >= SayFromBytes;
                    }
                    if (say) Say("drive.saving", $"Saving {name} to Google Drive");
                }
                else if (unsure)
                {
                    // A piece broke: Google may hold more than Sent, and ignores
                    // bytes it already has, so its count is where to go on from.
                    long? named;
                    lock (p.Gate) named = p.FinalTotal > 0 ? p.FinalTotal : null;
                    var (finished, answer, held) = _drive.AskHeld(session, named, token).GetAwaiter().GetResult();
                    lock (p.Gate)
                    {
                        if (finished)
                        {
                            if (!final || answer == null) throw new IOException($"Google finished {name} early");
                            p.Sent = p.FinalTotal;
                            p.Unsure = false;
                            done = answer;
                            return true;
                        }
                        if (held < p.Sent) throw new DriveClient.StreamLostException($"Google lost part of {name}");
                        p.Sent = Math.Min(held, p.FrozenBelow);
                        if (!p.HasBase || p.LetGo > 0) { p.Data.DropBelow(p.Sent); p.LetGo = p.Sent; }
                        p.Unsure = false;
                    }
                }

                long total = -1;
                if (final)
                    lock (p.Gate) total = p.FinalTotal > 0 ? p.FinalTotal : Math.Max(p.Size, p.Sent + 1);

                int sent = 0;
                while (sent < most)
                {
                    long from, n;
                    byte[] piece;
                    List<(long Start, long End)>? gaps = null;
                    string? baseId = null;
                    long driveSize = 0;
                    bool last;
                    lock (p.Gate)
                    {
                        from = p.Sent;
                        if (final) n = Math.Min(ChunkBytes, total - from);
                        else
                        {
                            if (SendableNow(p) <= 0) return sent > 0;
                            n = ChunkBytes;
                        }
                        piece = new byte[n];
                        p.Data.CopyTo(from, piece);
                        if (p.HasBase && p.Id != null && from < p.BaseSize)
                        {
                            gaps = p.Data.Gaps(from, Math.Min(from + n, p.BaseSize)).ToList();
                            baseId = p.Id;
                            driveSize = p.DriveSize;
                        }
                        p.FrozenBelow = Math.Max(p.FrozenBelow, from + n);
                        last = final && from + n >= total;
                        if (last) p.FinalTotal = total;
                    }

                    if (gaps != null)
                        foreach (var (gs, ge) in gaps)
                            ReadBase(baseId!, driveSize, name, gs, piece.AsSpan((int)(gs - from), (int)(ge - gs)), token);

                    DriveClient.DriveUploaded? answer;
                    try
                    {
                        answer = _drive.SendPiece(session!, from, piece, (int)n, last ? total : null, name, token)
                            .GetAwaiter().GetResult();
                    }
                    catch (Exception ex) when (ex is not DriveClient.UploadRefusedException { SessionGone: true })
                    {
                        lock (p.Gate) p.Unsure = true;
                        throw;
                    }

                    lock (p.Gate)
                    {
                        p.Sent = from + n;
                        // An edit keeps what it wrote until it lands, so a cut can drop
                        // its session and lose nothing, unless memory runs short; a new
                        // file lets each piece go.
                        if (!p.HasBase || p.LetGo > 0 || Pressed(p)) { p.Data.DropBelow(p.Sent); p.LetGo = p.Sent; }
                    }
                    // Writers waiting for room are woken as each piece frees it.
                    Roomier();
                    sent++;
                    if (last)
                    {
                        done = answer ?? throw new IOException("Google did not finish the file");
                        return true;
                    }
                }
                return true;
            }
            catch (DriveClient.UploadRefusedException refused) when (refused.SessionGone)
            {
                // Google ended the session. When everything it was sent is
                // still here, a new one starts on the next try.
                lock (p.Gate)
                {
                    if (p.LetGo > 0) throw;
                    DropSession(p);
                }
                throw new IOException($"Google ended the upload of {name}; it starts again", refused);
            }
        }

        /// <summary>
        /// The pending file's contents onto Drive now. Caller holds Send. Passes
        /// until nothing is left to send (or <paramref name="passes"/> runs out):
        /// writes that landed under a piece while it went are what the next pass
        /// sends, reading the rest back from Drive.
        /// </summary>
        private void CommitHeld(Pending p, CancellationToken token, int passes = 4)
        {
            for (int pass = 0; pass < passes; pass++)
            {
                bool streamed;
                long total;
                lock (p.Gate)
                {
                    if (p.Deleted || (!p.Dirty && p.Session == null)) return;
                    total = Math.Max(p.Size, p.Sent);
                    streamed = p.Session != null || total > MemoryFileBytes;
                    // Nothing more is laid over what this pass sends: the writes
                    // from here go to Late, and are the next pass.
                    if (!streamed) p.FrozenBelow = long.MaxValue;
                }

                DriveClient.DriveUploaded done;
                SentAs sentAs;
                if (!streamed)
                {
                    byte[] bytes;
                    List<(long Start, long End)> gaps;
                    string? baseId, replace, tag = null, triedIn = null;
                    long driveSize;
                    string parentId, name;
                    lock (p.Gate)
                    {
                        bytes = new byte[total];
                        p.Data.CopyTo(0, bytes);
                        gaps = p.HasBase && p.Id != null
                            ? p.Data.Gaps(0, Math.Min(total, p.BaseSize)).ToList()
                            : new List<(long, long)>();
                        baseId = p.Id;
                        replace = p.Id;
                        driveSize = p.DriveSize;
                        name = p.Name;
                        parentId = ParentIdOf(p) ?? "";
                        sentAs = new SentAs(name, parentId, p.ModifiedUtc);
                        if (p.Id == null)
                        {
                            tag = p.CreateTag ??= DriveClient.NewOperationTag();
                            if (p.CreateTried) triedIn = p.CreateParent;
                        }
                    }
                    try
                    {
                        if (parentId.Length == 0) throw new IOException("the folder it was in is gone");
                        if (baseId != null)
                            foreach (var (gs, ge) in gaps)
                                ReadBase(baseId, driveSize, name, gs, bytes.AsSpan((int)gs, (int)(ge - gs)), token);

                        // An earlier try whose answer was lost may have made the
                        // file: found by its tag, this one replaces its contents.
                        // Not looked for is not sent (a failed look throws, and
                        // the next try looks again).
                        if (triedIn != null && tag != null &&
                            _drive.MadeWithTag(tag, triedIn, "id,name", token).GetAwaiter().GetResult() is { } found)
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(found);
                            replace = doc.RootElement.GetProperty("id").GetString();
                            var madeAs = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? name : name;
                            sentAs = new SentAs(madeAs, triedIn, sentAs.Modified);
                        }
                        if (replace == null)
                            lock (p.Gate) { p.CreateTried = true; p.CreateParent = parentId; }
                        done = _drive.UploadFromMemory(bytes, (int)total, name, parentId, sentAs.Modified, replace, token,
                            replace == null ? tag : null).GetAwaiter().GetResult();
                    }
                    catch
                    {
                        lock (p.Gate)
                        {
                            p.FrozenBelow = 0;
                            if (p.Late != null) { p.Data.Absorb(p.Late); p.Late = null; }
                        }
                        throw;
                    }
                }
                else
                {
                    SendPieces(p, final: true, token, out var finished);
                    done = finished ?? throw new IOException("Google did not finish the file");
                    lock (p.Gate) { total = p.Sent; sentAs = p.SessionAs; }
                }

                Landed(p, done, total, sentAs, token);
            }
        }

        /// <summary>
        /// A pass on Drive: the upload's answer made the file's base, what was
        /// written under its pieces (and past its end) what is left, and the
        /// letter told. A new file renamed or moved while it went up is put
        /// right on Drive, and a date set since it started is sent.
        /// </summary>
        private void Landed(Pending p, DriveClient.DriveUploaded done, long total, SentAs sentAs, CancellationToken token)
        {
            string name, key;
            string? parentNow;
            bool wasNew, dateLater, deleted;
            DateTime modified;
            // The old contents' chunks go before the bytes over them are let go:
            // a read in between would otherwise be served the old ones.
            ReleaseFile(done.Id);
            lock (_treeGate)
            {
                long size;
                lock (p.Gate)
                {
                    wasNew = p.Id == null;
                    // Read with the id set, under Gate: a delete either saw the id
                    // (and trashed it) or is seen here (and it is trashed below).
                    deleted = p.Deleted;
                    p.CreateTag = null;
                    p.CreateParent = null;
                    p.CreateTried = false;
                    name = p.Name;
                    key = p.Key;
                    parentNow = ParentIdOf(p);
                    modified = p.ModifiedUtc;
                    dateLater = Math.Abs((p.ModifiedUtc - sentAs.Modified).TotalMilliseconds) >= 1;

                    p.Id = done.Id;
                    p.DriveSize = done.Size ?? total;
                    p.HasBase = true;
                    p.BaseSize = Math.Min(p.DriveSize, p.Size);
                    p.Session = null;
                    p.Sent = 0;
                    p.LetGo = 0;
                    p.FrozenBelow = 0;
                    p.FinalTotal = 0;
                    p.Unsure = false;
                    // What is left: written past what this pass sent, with what was
                    // written under its pieces laid over it.
                    p.Data.DropBelow(total);
                    if (p.Late != null) p.Data.Absorb(p.Late);
                    p.Late = null;
                    p.Dirty = !p.Data.IsEmpty || p.Size != p.DriveSize;
                    size = p.Size;
                }

                if (!deleted)
                {
                    var parent = Parent(key) ?? "";
                    var entry = new DriveEntry(done.Id, name, "application/octet-stream", done.Size ?? total,
                        done.ModifiedUtc ?? modified, _driveIds.TryGetValue(parent, out var drive) ? drive : null);
                    Bind(parent, name, entry);
                    var row = RowFor(name, entry) with { Size = size, Modified = modified.ToLocalTime() };
                    _listings.AddOrUpdate(parent, new[] { row }, (_, now) => now
                        .Where(r => !r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        .Append(row).ToArray());
                }
            }

            _uploadedAt[done.Id] = Environment.TickCount64;
            Roomier();
            if (deleted)
            {
                // Deleted (or replaced) while its upload was on the way: what that
                // upload made goes to the trash, not left behind as a stray copy.
                if (wasNew)
                {
                    try
                    {
                        _drive.Trash(done.Id, token, retry: true).GetAwaiter().GetResult();
                        ForgetTrashed(done.Id);
                    }
                    catch (Exception ex) { _log($"  could not trash {name} after a delete during its upload: {ex.Message}"); }
                }
                return;
            }

            bool movedAway = parentNow != null && parentNow != sentAs.ParentId;
            if (wasNew && (name != sentAs.Name || movedAway))
            {
                try
                {
                    _drive.MoveOrRename(done.Id, name, movedAway ? sentAs.ParentId : null, movedAway ? parentNow : null,
                        token, retry: true).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    // The letter shows what Drive has once it is listed again.
                    _log($"  could not put {name} where it was moved during its upload: {ex.Message}");
                    var parent = Parent(key) ?? "";
                    ForgetKey(parent);
                    RefreshBehind(parent);
                }
            }
            if (dateLater)
            {
                try { _drive.SetModifiedTime(done.Id, modified, token).GetAwaiter().GetResult(); }
                catch (Exception ex) { _log($"  could not set the date of {name}: {ex.Message}"); }
            }
        }

        private void SetModified(Opened opened, DateTime utc)
        {
            string? patch = null;
            var p = opened.Local ?? PendingAt(opened.Key);
            if (p != null)
            {
                lock (p.Gate)
                {
                    p.ModifiedUtc = utc;
                    if (!p.Dirty && !p.Uploading) patch = p.Id;
                }
            }
            else patch = opened.Id;

            opened.Modified = utc.ToLocalTime();
            var parent = Parent(opened.Key) ?? "";
            var leaf = LeafOf(opened.Key);
            EditListing(parent, rows => rows
                .Select(r => r.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase) && !r.IsFolder
                    ? r with { Modified = utc.ToLocalTime() } : r)
                .ToArray());

            if (patch == null || ReadOnlyAt(opened.Key)) return;
            var id = patch;
            _ = Task.Run(async () =>
            {
                try { await _drive.SetModifiedTime(id, utc, _uploadStop.Token).ConfigureAwait(false); }
                catch (Exception ex) { _log($"  could not set the date of {leaf}: {ex.Message}"); }
            });
        }

        // ---- deleting and renaming ------------------------------------------------------

        private void DeleteOpened(Opened opened)
        {
            var key = opened.Key;
            if (Fixed(key)) return;
            if (opened.Folder && _listings.TryGetValue(key, out var rows) && rows.Length > 0) return;

            var p = opened.Folder ? null : PendingAt(key);
            string? id;
            if (opened.Folder) id = FolderIdAt(key);
            else if (p != null)
            {
                lock (p.Gate)
                {
                    id = p.Id;
                    // Not on Drive yet: gone at once. An upload of it already on
                    // the way that makes the file is trashed when it lands (Landed
                    // reads Deleted with the id under this same lock).
                    if (id == null) MarkDeleted(p, stopAnyway: false);
                }
            }
            else id = _ids.TryGetValue(key, out var bound) ? bound : opened.Id;
            var parent = Parent(key) ?? "";
            var name = RowAt(key)?.Name ?? p?.Name ?? LeafOf(key);

            if (id != null && id != DriveClient.SharedDrivesId && id != DriveClient.TrashId)
            {
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
                    limit.CancelAfter(TimeSpan.FromSeconds(90));
                    _drive.Trash(id, limit.Token, retry: true).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    // Still on Drive, so what was written to it still goes up.
                    _log($"  could not trash {name}: {ex.Message}");
                    Say("drive.trash.failed", $"Could not delete {name}: {GoogleNet.Reason(ex)}");
                    ForgetKey(parent);
                    return;
                }
                if (p != null) lock (p.Gate) MarkDeleted(p, stopAnyway: true);
            }

            if (p != null) TrashLostCreate(p);
            RemoveKey(key);
        }

        /// <summary>
        /// Under Gate: a pending file deleted. Its upload is stopped unless it
        /// may be the request that makes the file, which is let land (and is
        /// trashed then); one replacing a file already trashed can always stop.
        /// </summary>
        private static void MarkDeleted(Pending p, bool stopAnyway)
        {
            p.Deleted = true;
            if (stopAnyway || !MayFinish(p)) { try { p.Abort.Cancel(); } catch { } }
        }

        /// <summary>
        /// A deleted new file whose create went out and was never answered: it
        /// may be on Drive under its tag, so it is looked for and trashed.
        /// </summary>
        private void TrashLostCreate(Pending p)
        {
            string tag, folder;
            lock (p.Gate)
            {
                if (!p.Deleted || p.Id != null || !p.CreateTried || p.Uploading ||
                    p.CreateTag == null || p.CreateParent == null) return;
                tag = p.CreateTag;
                folder = p.CreateParent;
                p.CreateTried = false;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    if (await _drive.MadeWithTag(tag, folder, "id", _uploadStop.Token).ConfigureAwait(false) is not { } found) return;
                    using var doc = System.Text.Json.JsonDocument.Parse(found);
                    if (doc.RootElement.GetProperty("id").GetString() is { } id)
                    {
                        await _drive.Trash(id, _uploadStop.Token, retry: true).ConfigureAwait(false);
                        ForgetTrashed(id);
                    }
                }
                catch (Exception ex) { _log($"  could not look for a deleted file's lost upload: {ex.Message}"); }
            });
        }

        /// <summary>
        /// A file trashed some time after it was deleted: a listing taken while
        /// it was still on Drive may show it, so it comes off the letter, unless
        /// its name is now a file being written (a save made over it).
        /// </summary>
        private void ForgetTrashed(string id)
        {
            lock (_treeGate)
                foreach (var (key, bound) in _ids.ToArray())
                    if (bound == id && PendingAt(key) == null) RemoveKeyHeld(key);
        }

        private void DropPendingUnder(string key)
        {
            var below = key + "\\";
            foreach (var p in _pending.Values.ToArray())
            {
                if (p.Key != key && !p.Key.StartsWith(below, StringComparison.Ordinal)) continue;
                lock (p.Gate) MarkDeleted(p, stopAnyway: p.Id != null);
                TrashLostCreate(p);
                Retire(p);
            }
            Roomier();
        }

        /// <summary>Pending files and open handles following a move. Under _treeGate (MoveKey).</summary>
        private void MovePending(string fromKey, string toKey, string name)
        {
            var below = fromKey + "\\";
            foreach (var p in _pending.Values.ToArray())
            {
                if (p.Key != fromKey && !p.Key.StartsWith(below, StringComparison.Ordinal)) continue;
                if (!_pending.TryRemove(new KeyValuePair<string, Pending>(p.Key, p))) continue;
                lock (p.Gate)
                {
                    bool itself = p.Key == fromKey;
                    p.Key = toKey + p.Key[fromKey.Length..];
                    if (itself) p.Name = name;
                    p.ParentId = FolderIdAt(Parent(p.Key) ?? "") ?? p.ParentId;
                }
                _pending[p.Key] = p;
            }
            RekeyOpened(fromKey, toKey);
        }

        private int RenameOpened(Opened opened, string fromKey, string toKey, string newName, bool replace)
        {
            if (Fixed(fromKey)) return WinFsp.AccessDenied;
            var fromParent = Parent(fromKey) ?? "";
            var toParent = Parent(toKey) ?? "";

            int status = EnsureListed(toParent, out _);
            if (status != WinFsp.Success) return status == WinFsp.ObjectNameNotFound ? WinFsp.ObjectPathNotFound : status;
            if (ReadOnlyAt(toParent)) return WinFsp.AccessDenied;
            if (opened.Folder && toKey.StartsWith(fromKey + "\\", StringComparison.Ordinal)) return WinFsp.InvalidParameter;

            bool sameKey = toKey == fromKey;
            bool replacing = false;
            if (!sameKey && Find(toKey, out var target) == WinFsp.Success)
            {
                if (!replace) return WinFsp.ObjectNameCollision;
                if (target.Folder || opened.Folder) return WinFsp.AccessDenied;
                replacing = true;
            }

            string? fromId = null, toId = null;
            if (fromParent != toParent)
            {
                fromId = FolderIdAt(fromParent);
                toId = FolderIdAt(toParent);
                if (toId == null) return WinFsp.ObjectPathNotFound;
            }

            var p = PendingAt(fromKey);
            string? id;
            if (opened.Folder) id = FolderIdAt(fromKey);
            else if (p != null) lock (p.Gate) id = p.Id;
            else id = _ids.TryGetValue(fromKey, out var bound) ? bound : null;

            if (id != null)
            {
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
                    limit.CancelAfter(TimeSpan.FromSeconds(90));
                    _drive.MoveOrRename(id, newName, fromId, toId, limit.Token, retry: true, interactive: true)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _log($"  could not move {LeafOf(fromKey)} to {newName}: {ex.Message}");
                    return StatusOf(ex);
                }
            }

            if (replacing) ReplaceAway(toKey);

            _driveIds.TryGetValue(fromParent, out var fromDrive);
            var toDrive = DriveOf(toParent);

            // A new file not on Drive yet is only re-keyed: its upload puts it
            // where it is now. One whose upload landed just before the re-key
            // went up under the old name, so it is moved here.
            string? landedMeanwhile = null;
            lock (_treeGate)
            {
                MoveKey(fromKey, toParent, newName, newName);
                if (id == null && p != null) lock (p.Gate) landedMeanwhile = p.Id;
            }
            if (landedMeanwhile != null)
            {
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
                    limit.CancelAfter(TimeSpan.FromSeconds(90));
                    _drive.MoveOrRename(landedMeanwhile, newName, fromId, toId, limit.Token, retry: true)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _log($"  could not move {LeafOf(fromKey)} to {newName} after its upload: {ex.Message}");
                    ForgetKey(toParent);
                }
            }

            if (opened.Folder && !string.Equals(fromDrive, toDrive, StringComparison.Ordinal))
            {
                var below = toKey + "\\";
                foreach (var key in _folders.Keys.ToArray())
                {
                    if (key != toKey && !key.StartsWith(below, StringComparison.Ordinal)) continue;
                    if (toDrive == null) _driveIds.TryRemove(key, out _);
                    else _driveIds[key] = toDrive;
                    ForgetKey(key);
                }
            }
            return WinFsp.Success;
        }

        private string? DriveOf(string key)
        {
            if (_driveIds.TryGetValue(key, out var drive)) return drive;
            var parent = Parent(key);
            if (key.Length > 0 && parent != null && FolderIdAt(parent) == DriveClient.SharedDrivesId) return FolderIdAt(key);
            return null;
        }

        private void ReplaceAway(string key)
        {
            var p = PendingAt(key);
            string? id;
            // The id read and the file marked in one step, so an upload landing
            // in between is either trashed here or by Landed.
            if (p != null) lock (p.Gate) { id = p.Id; if (id == null) MarkDeleted(p, stopAnyway: false); }
            else id = _ids.TryGetValue(key, out var bound) ? bound : null;
            if (id != null)
            {
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
                    limit.CancelAfter(TimeSpan.FromSeconds(90));
                    _drive.Trash(id, limit.Token, retry: true).GetAwaiter().GetResult();
                }
                catch (Exception ex) { _log($"  could not trash the replaced {LeafOf(key)}: {ex.Message}"); }
            }
            RemoveKey(key);
        }

        // ---- going up ---------------------------------------------------------------------

        private void StartUploads()
        {
            if (_uploadLoop != null) return;
            lock (_uploadWake)
                _uploadLoop ??= Task.Run(UploadLoopAsync);
        }

        private void KickUploads()
        {
            try { if (_uploadWake.CurrentCount < 8) _uploadWake.Release(); } catch { }
        }

        /// <summary>
        /// As the mount starts to go, before the letter does: every file still
        /// only in memory that is not being written this moment (closed, or
        /// open with nothing written for a couple of seconds, like a document
        /// Word still has open) is chosen to be sent. One being written now is
        /// unfinished, and nothing half-made is put on Google.
        /// </summary>
        private void ChooseWhatDrains()
        {
            foreach (var p in _pending.Values)
                lock (p.Gate)
                    if (p.Dirty && !p.Deleted && p.Writing == 0 &&
                        (p.Handles == 0 || Environment.TickCount64 - p.LastWrite >= 2000))
                    {
                        p.Drain = true;
                        p.DueAt = 0;
                        p.NotBefore = 0;
                    }
            _draining = true;
            KickUploads();
        }

        /// <summary>
        /// Sends what <see cref="ChooseWhatDrains"/> chose, for up to
        /// <paramref name="most"/>. Whatever is still only in memory after
        /// that is said, by name.
        /// </summary>
        private void DrainUploads(TimeSpan most)
        {
            if (_pending.IsEmpty) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < most && !_drive.Health.IsOffline &&
                   _pending.Values.Any(p => !p.Deleted && p.Drain && (p.Uploading || p.Dirty)))
                Thread.Sleep(100);

            var left = UploadNames;
            if (left.Count == 0) return;
            _log($"{left.Count} file(s) were not on Google Drive when it went: {string.Join(", ", left)}");
            Say("drive.save.failed", left.Count == 1
                ? $"{left[0]} was not saved to Google Drive before it disconnected"
                : $"{left.Count} files were not saved to Google Drive before it disconnected: {string.Join(", ", left.Take(5))}");
        }

        private void StopUploads(TimeSpan most)
        {
            try { _uploadStop.Cancel(); } catch { }
            foreach (var p in _pending.Values) { try { p.Abort.Cancel(); } catch { } }
            KickUploads();
            Roomier();
            try { _uploadLoop?.Wait(most); } catch { }
        }

        private async Task UploadLoopAsync()
        {
            var token = _uploadStop.Token;
            var running = new List<Task>();
            long lastPrune = Environment.TickCount64;
            while (!token.IsCancellationRequested)
            {
                running.RemoveAll(t => t.IsCompleted);
                long now = Environment.TickCount64;
                long next = long.MaxValue;
                long idleMs = (long)IdleCommit.TotalMilliseconds;
                bool squeezed = now < Volatile.Read(ref _squeezedUntil);

                if (!_drive.Health.IsOffline)
                {
                    foreach (var p in _pending.Values)
                    {
                        if (running.Count >= UploadLanes) break;
                        bool go;
                        long when;
                        lock (p.Gate)
                        {
                            if (_draining) when = p.Drain ? p.NotBefore : long.MaxValue;
                            else if (p.Handles == 0) when = Math.Max(p.DueAt, p.NotBefore);
                            // Open: never while a write is under way, and a stream is
                            // never ended for a pause (its own writes send it, and it
                            // goes when closed). Otherwise, left alone, it is a
                            // document its program keeps open; when memory is short,
                            // a moment alone is enough.
                            else if (p.Writing > 0 || p.Session != null) when = long.MaxValue;
                            else when = Math.Max(p.LastWrite + (squeezed ? 2000 : idleMs), p.NotBefore);
                            go = !p.Deleted && !p.Retired && !p.Uploading && p.Dirty && now >= when;
                            if (go) p.Uploading = true;
                        }
                        if (go) running.Add(Task.Run(() => FinishOne(p, token)));
                        else if (when > now && when != long.MaxValue) next = Math.Min(next, when);
                    }
                }

                if (running.Count == 0 &&
                    !_pending.Values.Any(p => !p.Deleted && (p.Uploading || (p.Dirty && p.Handles == 0))))
                    SayWhatWasSaved();

                if (now - lastPrune > 120_000)
                {
                    lastPrune = now;
                    foreach (var (id, at) in _uploadedAt.ToArray())
                        if (now - at > 120_000) _uploadedAt.TryRemove(new KeyValuePair<string, long>(id, at));
                }

                int wait = next == long.MaxValue ? 5000 : (int)Math.Clamp(next - now, 50, 5000);
                var waiting = new List<Task>(running) { _uploadWake.WaitAsync(wait, token) };
                try { await Task.WhenAny(waiting).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); } catch { }
        }

        private void SayWhatWasSaved()
        {
            int saved = Interlocked.Exchange(ref _savedSinceSaid, 0);
            if (saved == 0) return;
            Say("drive.saved", saved == 1
                ? $"Saved {_lastSavedName} to Google Drive"
                : $"Saved {saved:N0} files to Google Drive");
        }

        private void FinishOne(Pending p, CancellationToken token)
        {
            string name;
            lock (p.Gate) name = p.Name;
            bool stopped = false;
            bool took = false;
            try
            {
                using var both = CancellationTokenSource.CreateLinkedTokenSource(token, p.Abort.Token);
                p.Send.Wait(both.Token);
                took = true;
                // A file still open gets one pass: its program may go on writing,
                // and that is the next commit, not this one sent again and again.
                int passes;
                lock (p.Gate) passes = p.Handles > 0 ? 1 : 4;
                CommitHeld(p, both.Token, passes);
                lock (p.Gate) name = p.Name;

                // Deleted meanwhile: Landed trashed what the upload made.
                if (!p.Deleted)
                {
                    _lastSavedName = name;
                    Interlocked.Increment(ref _savedSinceSaid);
                    _log($"  saved {name} to Drive");
                }
                lock (p.Gate) p.Failures = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || p.Abort.IsCancellationRequested)
            {
                stopped = token.IsCancellationRequested;
                // A cut that dropped the upload: it is due again once the cut is done.
                lock (p.Gate) p.NotBefore = Environment.TickCount64 + 500;
            }
            catch (Exception ex) when (Refused(ex) || ex is DriveClient.StreamLostException ||
                                       ex is DriveClient.UploadRefusedException { SessionGone: true })
            {
                Refuse(p, GoogleNet.Reason(ex), IsQuota(ex) ? WinFsp.DiskFull : WinFsp.AccessDenied);
            }
            catch (Exception ex)
            {
                int failures;
                lock (p.Gate)
                {
                    failures = ++p.Failures;
                    long wait = ex is GoogleSignInRequiredException ? 60_000
                        : Math.Min(300_000, 5_000L << Math.Min(6, failures - 1));
                    p.NotBefore = Environment.TickCount64 + wait;
                }
                _log($"  could not save {name} yet (try {failures}): {ex.Message}");
                if (failures == 3) Say("drive.save.waiting", $"Still trying to save {name} to Google Drive");
            }
            finally
            {
                if (took) p.Send.Release();
                p.Uploading = false;
                // Deleted while a create of it went unanswered: looked for on Drive.
                if (p.Deleted) TrashLostCreate(p);
                if (!stopped) Retire(p);
                Roomier();
                KickUploads();
            }
        }

        private static bool IsQuota(Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
                if (e is DriveQuotaException) return true;
            return false;
        }

        private static bool Refused(Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
            {
                if (e is DriveQuotaException) return true;
                if (e is DriveClient.DriveStatusException s)
                    return s.Status is 400 or 403 or 404 && !DriveClient.IsRateLimit(s.Status, s.Body);
            }
            return false;
        }

        /// <summary>
        /// A file Google will not take. Kept for its owner when its bytes can
        /// still be put together (nothing sent was let go), in
        /// <see cref="UnsavedFolder"/>; said either way.
        /// </summary>
        private void Refuse(Pending p, string reason, int status)
        {
            string name;
            string? kept = null;
            bool whole;
            lock (p.Gate)
            {
                name = p.Name;
                whole = p.LetGo == 0;
                // Writes from here are refused too, so the program knows the
                // save failed, rather than being told yes and losing them.
                p.RefusedStatus = status;
                p.Deleted = true;
            }
            if (whole)
            {
                string? target = null;
                try
                {
                    Directory.CreateDirectory(UnsavedFolder);
                    var clean = NameRules.IsUsableName(Sanitise(name)) ? Sanitise(name) : "Unsaved file";
                    target = Path.Combine(UnsavedFolder,
                        NameRules.UniqueAmong(clean, n => File.Exists(Path.Combine(UnsavedFolder, n)), false));
                    using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                    {
                        long size;
                        lock (p.Gate) size = p.Size;
                        var block = new byte[Math.Min(Math.Max(size, 1), ChunkBytes)];
                        for (long at = 0; at < size; at += block.Length)
                        {
                            int n = (int)Math.Min(block.Length, size - at);
                            List<(long Start, long End)> gaps;
                            string? baseId;
                            long driveSize;
                            lock (p.Gate)
                            {
                                Array.Clear(block, 0, n);
                                p.Data.CopyTo(at, block.AsSpan(0, n));
                                p.Late?.CopyTo(at, block.AsSpan(0, n));
                                gaps = p.HasBase && p.Id != null && at < p.BaseSize
                                    ? p.Data.Gaps(at, Math.Min(at + n, p.BaseSize)).ToList() : new List<(long, long)>();
                                if (p.Late != null) gaps = gaps.SelectMany(g => p.Late.Gaps(g.Start, g.End)).ToList();
                                baseId = p.Id;
                                driveSize = p.DriveSize;
                            }
                            if (baseId != null)
                                foreach (var (gs, ge) in gaps)
                                    ReadBase(baseId, driveSize, name, gs, block.AsSpan((int)(gs - at), (int)(ge - gs)), _uploadStop.Token);
                            file.Write(block, 0, n);
                        }
                    }
                    kept = target;
                }
                catch (Exception ex)
                {
                    _log($"  could not keep {name}: {ex.Message}");
                    if (target != null) { try { File.Delete(target); } catch { } }
                }
            }

            string? id;
            string key;
            lock (p.Gate) { id = p.Id; key = p.Key; }
            if (id == null) RemoveKey(key);
            else
            {
                ReleaseFile(id);
                var parent = Parent(key) ?? "";
                Retire(p);
                ForgetKey(parent);
                RefreshBehind(parent);
            }

            _log($"  {name} was refused: {reason}");
            Say("drive.save.failed", kept != null
                ? $"Could not save {name} to Google Drive: {reason}. A copy is in {UnsavedFolder}"
                : $"Could not save {name} to Google Drive: {reason}");
        }
    }

    /// <summary>
    /// Bytes written to a file and not yet on Drive, by offset: sparse, in
    /// memory, in sorted runs that grow in place as a program writes along and
    /// join up when a gap between two is filled. <see cref="Allocated"/> is
    /// what the buffers take, which is what the memory limits count.
    /// </summary>
    internal sealed class ByteOverlay
    {
        private sealed class Run
        {
            public long Start;
            public byte[] Buf = Array.Empty<byte>();
            public int Len;
            public long End => Start + Len;
        }

        private const int RunMost = 16 * 1024 * 1024;
        private const int Grain = 4096;

        private readonly List<Run> _runs = new();

        public long Bytes { get; private set; }
        public long Allocated { get; private set; }
        public bool IsEmpty => _runs.Count == 0;
        public long End => _runs.Count == 0 ? 0 : _runs[^1].End;
        internal int RunCount => _runs.Count;

        private static int Rounded(long n) => (int)Math.Min(int.MaxValue, (n + Grain - 1) / Grain * Grain);

        private void SetBuffer(Run run, int capacity)
        {
            Allocated += capacity - run.Buf.Length;
            var grown = new byte[capacity];
            Array.Copy(run.Buf, grown, Math.Min(run.Buf.Length, Math.Min(run.Len, capacity)));
            run.Buf = grown;
        }

        public void Write(long at, ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return;
            long end = at + data.Length;

            if (_runs.Count > 0 && _runs[^1].End == at && _runs[^1].Len + data.Length <= RunMost)
            {
                var last = _runs[^1];
                if (last.Len + data.Length > last.Buf.Length)
                    SetBuffer(last, (int)Math.Min(RunMost, Math.Max(Rounded(last.Len + data.Length), (long)last.Buf.Length * 2)));
                data.CopyTo(last.Buf.AsSpan(last.Len));
                last.Len += data.Length;
                Bytes += data.Length;
                return;
            }

            int inside = IndexOfRunHolding(at);
            if (inside >= 0 && _runs[inside].End >= end)
            {
                var run = _runs[inside];
                data.CopyTo(run.Buf.AsSpan((int)(at - run.Start)));
                return;
            }

            Remove(at, end);
            var fresh = new Run { Start = at, Len = data.Length };
            SetBuffer(fresh, Rounded(data.Length));
            data.CopyTo(fresh.Buf);
            int position = _runs.FindIndex(r => r.Start > at);
            if (position < 0) { _runs.Add(fresh); position = _runs.Count - 1; }
            else _runs.Insert(position, fresh);
            Bytes += data.Length;
            Join(position);
        }

        /// <summary>Joins the run at <paramref name="index"/> with the ones either side when they touch.</summary>
        private void Join(int index)
        {
            if (index + 1 < _runs.Count) JoinPair(index);
            if (index > 0) JoinPair(index - 1);
        }

        private void JoinPair(int left)
        {
            var a = _runs[left];
            var b = _runs[left + 1];
            if (a.End != b.Start || (long)a.Len + b.Len > RunMost) return;
            if (a.Len + b.Len > a.Buf.Length) SetBuffer(a, Rounded(a.Len + b.Len));
            Array.Copy(b.Buf, 0, a.Buf, a.Len, b.Len);
            a.Len += b.Len;
            Allocated -= b.Buf.Length;
            _runs.RemoveAt(left + 1);
        }

        private int IndexOfRunHolding(long at)
        {
            for (int i = _runs.Count - 1; i >= 0; i--)
            {
                var run = _runs[i];
                if (run.Start <= at && at < run.End) return i;
                if (run.End <= at) return -1;
            }
            return -1;
        }

        public void Remove(long from, long to)
        {
            if (to <= from) return;
            for (int i = _runs.Count - 1; i >= 0; i--)
            {
                var run = _runs[i];
                if (run.End <= from || run.Start >= to) continue;
                long keepLeft = Math.Max(0, from - run.Start);
                long keepRight = Math.Max(0, run.End - to);
                Bytes -= run.Len - keepLeft - keepRight;
                if (keepRight > 0)
                {
                    var right = new Run { Start = to, Len = (int)keepRight };
                    right.Buf = new byte[Rounded(keepRight)];
                    Allocated += right.Buf.Length;
                    Array.Copy(run.Buf, (int)(to - run.Start), right.Buf, 0, (int)keepRight);
                    _runs.Insert(i + 1, right);
                }
                if (keepLeft > 0)
                {
                    run.Len = (int)keepLeft;
                    // A run cut well short gives most of its buffer back.
                    if (Rounded(keepLeft) < run.Buf.Length / 2) SetBuffer(run, Rounded(keepLeft));
                }
                else
                {
                    Allocated -= run.Buf.Length;
                    _runs.RemoveAt(i);
                }
            }
        }

        public void Trim(long size) => Remove(size, long.MaxValue);

        public void DropBelow(long at) => Remove(long.MinValue / 2, at);

        /// <summary>Another overlay's bytes written over this one's (they are newer).</summary>
        public void Absorb(ByteOverlay newer)
        {
            foreach (var run in newer._runs) Write(run.Start, run.Buf.AsSpan(0, run.Len));
        }

        public void CopyTo(long at, Span<byte> into)
        {
            long end = at + into.Length;
            foreach (var run in _runs)
            {
                if (run.End <= at) continue;
                if (run.Start >= end) break;
                long from = Math.Max(at, run.Start);
                long to = Math.Min(end, run.End);
                run.Buf.AsSpan((int)(from - run.Start), (int)(to - from)).CopyTo(into.Slice((int)(from - at)));
            }
        }

        public IEnumerable<(long Start, long End)> Gaps(long from, long to)
        {
            long at = from;
            var gaps = new List<(long, long)>();
            foreach (var run in _runs)
            {
                if (run.End <= at) continue;
                if (run.Start >= to) break;
                if (run.Start > at) gaps.Add((at, run.Start));
                at = Math.Max(at, run.End);
                if (at >= to) break;
            }
            if (at < to) gaps.Add((at, to));
            return gaps;
        }

        public bool Covers(long from, long to)
        {
            foreach (var _ in Gaps(from, to)) return false;
            return true;
        }

        public long ContiguousEnd(long from)
        {
            long at = from;
            foreach (var run in _runs)
            {
                if (run.End <= at) continue;
                if (run.Start > at) break;
                at = run.End;
            }
            return at;
        }
    }
}
