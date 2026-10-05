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
    /// One file to send, and which folder under the destination it belongs in.
    /// <c>RelativeFolder</c> is "" for something dropped straight into the
    /// destination, and uses backslashes for anything deeper.
    /// </summary>
    public sealed record UploadFile(string LocalPath, string RelativeFolder, long Size);

    /// <summary>
    /// The shape of what is about to be sent, worked out before anything is.
    ///
    /// Folders are listed separately from the files in them, and deliberately:
    /// an empty folder is part of the shape too. `RoboCopyEngine` learned this the
    /// hard way for the local path — a folder whose whole subtree was empty
    /// vanished with no error and no failed count — and an upload has exactly the
    /// same hole if it only walks files.
    /// </summary>
    public sealed record UploadWork(
        IReadOnlyList<UploadFile> Files,
        IReadOnlyList<string> Folders,
        IReadOnlyList<string> SourceFolders,
        long TotalBytes)
    {
        public bool IsEmpty => Files.Count == 0 && Folders.Count == 0;
    }

    /// <summary>
    /// How far an upload has got, in the terms the transfer dialog wants.
    /// </summary>
    public sealed record UploadTally(
        long BytesDone, long BytesTotal, int FilesDone, int FilesTotal, string CurrentItem);

    /// <summary>
    /// Sending a set of local paths — files, folders, or both — to a folder on the
    /// mounted Drive.
    ///
    /// Folders used to be refused rather than half-copied, and the reasoning was
    /// sound as far as it went: uploading a tree means creating every folder in it
    /// and tracking which id each one got, and a tree that fails halfway leaves
    /// something nobody asked for. What was missing was the id tracking, which is
    /// this class — <see cref="EnsureFolder"/> is the whole of it.
    ///
    /// Three things decide how long an upload takes, and none of them is the
    /// connection:
    ///
    /// - **A Drive request costs about 700ms whatever is in it.** So a folder of
    ///   small files is round trips, not bytes, and the way to make it quick is to
    ///   have several in the air at once. <see cref="Streams"/>.
    /// - **A resumable session is an extra round trip before a byte moves.** Files
    ///   under <c>DriveClient.SmallUploadBytes</c> skip it entirely.
    /// - **Chunking a large file is a round trip per chunk.** It does not chunk any
    ///   more; the file is streamed into one request, out of the disk and into the
    ///   socket, with nothing held anywhere.
    /// </summary>
    public sealed class DriveUpload
    {
        /// <summary>
        /// How many files go up at once.
        ///
        /// The limit being worked around is latency, not bandwidth: a request
        /// costs about 700ms before it carries anything, so one file at a time
        /// spends most of a small-file upload waiting. Four is the same answer
        /// `DriveFileCache` reached from the other direction — it fetches with two
        /// streams because more bought nothing once the link was saturated — and
        /// uploads are the case where the link is *not* saturated, because the
        /// files are small and the waiting is the cost.
        ///
        /// Eight, raised from four, and the headroom was measured rather than
        /// guessed at. Google's quota model since May 2026 is weighted units: an
        /// upload is an edit at 50 units, and the tightest ceiling is 325,000
        /// units per minute per user — about 6,500 uploads a minute, or a
        /// hundred a second. Four streams against a 700ms round trip is roughly
        /// six a second. Eight is twelve, which is still an order of magnitude
        /// inside the limit, so the thing that stops it going faster is the
        /// round trip and not Google.
        ///
        /// Not much higher than that, and the reason is not the quota. Each
        /// stream in flight is a file whose bytes have been counted as sent and
        /// might yet not arrive, so the number of streams is also the size of
        /// the lie the progress bar can be telling when something goes wrong —
        /// and past a point more streams stop shortening the wait and start
        /// competing for the same upstream bandwidth.
        /// </summary>
        public const int Streams = 8;

        /// <summary>
        /// How many folders are created at once. Higher than <see cref="Streams"/>
        /// because these carry no bytes at all — a folder is one small metadata
        /// request, and the only thing being overlapped is the wait.
        /// </summary>
        public const int FolderStreams = 8;

        /// <summary>
        /// The most often the caller is told anything.
        ///
        /// The dialog is a window on the UI thread and the progress arrives from
        /// however many upload streams are running, each reporting every quarter
        /// megabyte it reads. Unthrottled that is thousands of cross-thread posts
        /// a second, all of them to draw a number that changes ten times faster
        /// than anybody can read it — and the thread they queue on is the one that
        /// has to draw the result.
        /// </summary>
        public const long ReportMilliseconds = 100;

        /// <summary>
        /// A single file's own announcements are worth having up to this many.
        /// Past it, the sentence worth hearing is the dialog's, once per tenth of
        /// the whole job.
        /// </summary>
        private const int ChattyBelow = 5;

        private readonly GoogleDrive _drive;
        private readonly string _destination;
        private readonly bool _move;
        private readonly PasteConflictPolicy _conflicts;
        private readonly Action<UploadTally>? _report;

        /// <summary>
        /// What each destination folder already holds, fetched once each.
        ///
        /// Only consulted when the policy is one that needs it. Under "keep both"
        /// — the default, and what happens when there is nothing to decide — no
        /// folder is ever asked about, so the common upload costs exactly what it
        /// did before this existed.
        ///
        /// A Lazy for the same reason the folder chain is one: several streams
        /// reach the same folder at once, and GetOrAdd's factory overload may run
        /// the factory twice and keep one result. Here that is a wasted Drive
        /// listing rather than a wasted folder, but the rule is the rule.
        /// </summary>
        private readonly ConcurrentDictionary<string, Lazy<HashSet<string>?>> _taken =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ConflictLog _log = new();

        // Relative folder path -> the local path of its placeholder, or null when
        // it could not be created. A Lazy rather than a bare Task because
        // ConcurrentDictionary's factory overload may run the factory more than
        // once for one key and keep a single result — which here would be a second
        // folder created in Drive and then abandoned, exactly the mistake
        // DriveFileCache made with downloads. The value overload with a Lazy runs
        // the loser's factory never.
        private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _folders =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly List<string> _errors = new();

        private long _bytesSent;
        private int _filesDone;
        private long _lastReport;
        private string _current = "";

        private long _totalBytes;
        private int _totalFiles;
        private bool _announceEach;

        /// <param name="conflicts">
        /// What to do about a file whose name is already in the folder it is going
        /// into. Never <c>Ask</c>: that is resolved once, before any of this
        /// starts, because a question per file is a question per file.
        /// </param>
        public DriveUpload(GoogleDrive drive, string destination, bool move,
                           PasteConflictPolicy conflicts, Action<UploadTally>? report)
        {
            _drive = drive;
            _destination = destination;
            _move = move;
            _conflicts = conflicts;
            _report = report;
        }

        /// <summary>Everything that went wrong, one line each.</summary>
        public IReadOnlyList<string> Errors { get { lock (_errors) return _errors.ToArray(); } }

        /// <summary>How many files actually landed in Drive.</summary>
        public int FilesUploaded => Volatile.Read(ref _filesDone);

        /// <summary>
        /// What the conflict policy actually did, in the same shape the local
        /// engines report it — so a paste into Drive and a paste into a folder say
        /// the same sentences about the same events.
        /// </summary>
        public ConflictOutcomes Collisions { get { lock (_log) return _log.Snapshot(); } }

        // ---- working out what to send ---------------------------------------

        /// <summary>
        /// Walks the sources and says what the upload consists of.
        ///
        /// Run this on a worker. These paths can be on a share that has gone away,
        /// and the sources of a paste into Drive are exactly the paths most likely
        /// to be somewhere slow — which is also why the walk uses
        /// <c>EnumerateFileSystemInfos</c>: it carries the attributes and the
        /// length out of the scan that found the entry, rather than asking three
        /// times for what one call already returned.
        /// </summary>
        public static UploadWork Survey(IEnumerable<string> sources)
        {
            var files = new List<UploadFile>();
            var folders = new List<string>();
            var roots = new List<string>();
            long total = 0;

            foreach (var source in sources)
            {
                if (string.IsNullOrWhiteSpace(source)) continue;

                bool isFolder;
                try { isFolder = Directory.Exists(source); }
                catch { continue; }

                if (isFolder)
                {
                    var name = Path.GetFileName(source.TrimEnd('\\', '/'));

                    // A drive root has no name to give the folder it would become,
                    // and "upload D: into Drive" is not a thing anybody asked for.
                    if (name.Length == 0 || name.EndsWith(":", StringComparison.Ordinal)) continue;

                    roots.Add(source);
                    Walk(source, name, files, folders, ref total);
                    continue;
                }

                long size;
                try
                {
                    var info = new FileInfo(source);
                    if (!info.Exists) continue;
                    size = info.Length;
                }
                catch { continue; }

                files.Add(new UploadFile(source, "", size));
                total += size;
            }

            return new UploadWork(files, folders, roots, total);
        }

        /// <summary>
        /// The tree under one source folder, breadth first and without recursion.
        ///
        /// Not recursive because the depth is somebody else's — a path can nest as
        /// far as the filesystem allows, and a stack overflow is the one failure
        /// this application cannot catch or report.
        /// </summary>
        private static void Walk(
            string root, string rootName, List<UploadFile> files, List<string> folders,
            ref long total)
        {
            var queue = new Queue<(string Path, string Relative)>();
            queue.Enqueue((root, rootName));

            while (queue.Count > 0)
            {
                var (path, relative) = queue.Dequeue();
                folders.Add(relative);

                FileSystemInfo[] entries;
                try { entries = new DirectoryInfo(path).GetFileSystemInfos(); }
                catch { continue; }

                foreach (var entry in entries)
                {
                    FileAttributes attributes;
                    try { attributes = entry.Attributes; }
                    catch { continue; }

                    // A junction or a symbolic link is somebody else's tree wearing
                    // this one's name. Following one can walk in a circle for ever,
                    // and at best it copies a whole disk into a folder that looked
                    // like it held six files.
                    if ((attributes & FileAttributes.ReparsePoint) != 0 && NameRules.IsLink(entry)) continue;

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        queue.Enqueue((entry.FullName, relative + "\\" + entry.Name));
                        continue;
                    }

                    long size = 0;
                    try { size = ((FileInfo)entry).Length; } catch { }

                    files.Add(new UploadFile(entry.FullName, relative, size));
                    total += size;
                }
            }
        }

        // ---- sending it ------------------------------------------------------

        /// <summary>
        /// Creates the folders and sends the files. Throws
        /// <c>OperationCanceledException</c> if the token fires; everything else
        /// is collected into <see cref="Errors"/>, because one file that will not
        /// go is not a reason to abandon the other nine hundred.
        /// </summary>
        public async Task Run(UploadWork work, CancellationToken token)
        {
            work = await SettleTopFolders(work, token);

            _totalBytes = work.TotalBytes;
            _totalFiles = work.Files.Count;
            _announceEach = work.Files.Count <= ChattyBelow && work.Folders.Count == 0;

            // Said before anything is sent, so the dialog opens with the real
            // numbers in it rather than "calculating" for the seven hundred
            // milliseconds of the first round trip. Every field on it is already
            // knowable at this point except the speed.
            Report("", force: true);

            // The folders first, and all of them, whether or not anything goes in
            // them. A file waiting on a folder that is being created at the same
            // moment is fine — EnsureFolder hands out the same task — but an empty
            // folder has nothing to ask for it, and would otherwise be the one
            // part of the tree that silently did not arrive.
            if (work.Folders.Count > 0)
                await Parallel.ForEachAsync(
                    work.Folders,
                    new ParallelOptions { MaxDegreeOfParallelism = FolderStreams, CancellationToken = token },
                    async (folder, inner) => { await EnsureFolder(folder, inner); });

            if (work.Files.Count > 0)
                await Parallel.ForEachAsync(
                    work.Files,
                    new ParallelOptions { MaxDegreeOfParallelism = Streams, CancellationToken = token },
                    UploadOne);

            // A move takes the source directories away once their contents have
            // gone, deepest first so a parent is only ever asked about after its
            // children. Only when they are empty: anything left behind is
            // something that did not upload, and deleting it would destroy the one
            // copy there is.
            if (_move) RemoveEmptySources(work.SourceFolders);

            Report(_current, force: true);
        }

        /// <summary>
        /// The local path of the Drive folder for a relative path, creating it and
        /// everything above it if need be.
        ///
        /// The chain is what makes a tree work: `a\b\c` waits on `a\b`, which waits
        /// on `a`, which waits on the destination — and each one is created once
        /// however many files are queued behind it.
        /// </summary>
        private Task<string?> EnsureFolder(string relative, CancellationToken token)
        {
            if (relative.Length == 0) return Task.FromResult<string?>(_destination);

            return _folders.GetOrAdd(relative, new Lazy<Task<string?>>(
                () => CreateChain(relative, token),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        /// <summary>
        /// The folders being sent, against what the destination already holds,
        /// under the conflict setting — decided before anything is sent, as a
        /// paste does. Every folder used to be merged into one of the same name
        /// whatever the setting said: Skip filled it in, and "keep both" put
        /// "(2)" files inside it instead of making "Folder (2)".
        /// </summary>
        private async Task<UploadWork> SettleTopFolders(UploadWork work, CancellationToken token)
        {
            var tops = work.Folders.Where(f => !f.Contains('\\')).ToList();
            if (tops.Count == 0) return work;

            IReadOnlyList<DrivePlaced>? there = null;
            await Task.Run(() =>
            {
                _drive.Populate(_destination);
                if (_drive.TryListing(_destination, out var listed)) there = listed;
            }, token);

            var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (there == null)
            {
                // Nothing known about what is there: only keeping both is safe.
                if (_conflicts != PasteConflictPolicy.AutoRename)
                    foreach (var top in tops)
                    {
                        Fail(top, "Google Drive would not say what is already in that folder, so it was not sent");
                        dropped.Add(top);
                    }
            }
            else
            {
                var existing = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in there) existing.TryAdd(item.Name, item.IsFolder);
                var used = new HashSet<string>(existing.Keys, StringComparer.OrdinalIgnoreCase);

                foreach (var top in tops)
                {
                    if (!existing.TryGetValue(top, out bool isFolder)) continue;

                    if (_conflicts == PasteConflictPolicy.AutoRename)
                    {
                        var fresh = NameRules.UniqueAmong(top, used.Contains, folder: true);
                        used.Add(fresh);
                        _topRenames[top] = fresh;
                        lock (_log) _log.Renamed(fresh);
                        continue;
                    }

                    if (!isFolder)
                    {
                        Fail(top, "a file of that name is already there");
                        dropped.Add(top);
                        continue;
                    }

                    if (_conflicts == PasteConflictPolicy.Skip)
                    {
                        dropped.Add(top);
                        lock (_log) _log.Skipped(top);
                    }

                    // Overwrite and Fill gaps go into what is there.
                }
            }

            if (dropped.Count == 0) return work;

            static string Top(string relative)
            {
                int cut = relative.IndexOf('\\');
                return cut < 0 ? relative : relative[..cut];
            }

            var files = work.Files.Where(f => f.RelativeFolder.Length == 0 || !dropped.Contains(Top(f.RelativeFolder))).ToList();
            var folders = work.Folders.Where(f => !dropped.Contains(Top(f))).ToList();
            // And out of the folders a move tidies away afterwards: a tree that was
            // not sent had its empty subfolders deleted all the same.
            var sources = work.SourceFolders
                .Where(r => !dropped.Contains(Path.GetFileName(Path.TrimEndingDirectorySeparator(r))))
                .ToList();

            return work with
            {
                Files = files,
                Folders = folders,
                SourceFolders = sources,
                TotalBytes = files.Sum(f => f.Size),
            };
        }

        /// <summary>Top-level folders sent under another name; see SettleTopFolders.</summary>
        private readonly ConcurrentDictionary<string, string> _topRenames = new(StringComparer.OrdinalIgnoreCase);

        private async Task<string?> CreateChain(string relative, CancellationToken token)
        {
            int cut = relative.LastIndexOf('\\');
            string parentRelative = cut < 0 ? "" : relative[..cut];
            string name = cut < 0 ? relative : relative[(cut + 1)..];
            if (cut < 0 && _topRenames.TryGetValue(name, out var renamed)) name = renamed;

            var parent = await EnsureFolder(parentRelative, token);
            if (parent == null) return null;

            // Only the top of a tree is worth a sentence. Below that, "Created
            // disc 2 in Google Drive" said forty times is the upload narrating its
            // own directory listing.
            var placed = await _drive.CreateFolderIn(parent, name, announce: cut < 0, token);

            if (placed == null) Fail(relative, "the folder could not be created in Google Drive");
            return placed;
        }

        private async ValueTask UploadOne(UploadFile file, CancellationToken token)
        {
            var folder = await EnsureFolder(file.RelativeFolder, token);
            var name = Path.GetFileName(file.LocalPath);

            // The folder failed, and CreateChain has already said so — once, for
            // the folder. Recording it again per file would bury the one line that
            // explains it under five hundred copies of its consequence, and the
            // count of what did land already says how much did not.
            if (folder == null) return;

            // Whether something of this name is already in the folder, and what
            // the person asked should happen about that. Only asked under a policy
            // that needs an answer — under "keep both" no folder is ever listed,
            // so the ordinary upload costs exactly what it did before.
            //
            // Checked and reserved in one step. Two files of one name in one
            // upload — two covers from two albums — both saw the name free before
            // either landed, and both went up under it.
            var (state, reserved) = Reserve(folder, name);

            // A folder that would not list is a folder nobody can say anything
            // about, and "nothing is there" is the one answer that is known to be
            // unsafe: under Skip or Overwrite it uploaded a duplicate of every
            // file already in it.
            if (state == NameState.Unknown && _conflicts != PasteConflictPolicy.AutoRename)
            {
                Interlocked.Add(ref _totalBytes, -file.Size);
                Interlocked.Decrement(ref _totalFiles);
                Fail(name, "Google Drive would not say what is already in that folder, so it was not sent");
                Report(name);
                return;
            }

            bool clashes = state is NameState.Present or NameState.ThisUpload;

            // FillGaps and Skip do the same thing to a *file* — leave the one
            // that is there and do not send this one. The difference between
            // them is above this line: Skip drops a colliding folder before the
            // upload starts, and FillGaps walks into it, so this is the point
            // where the four hundred missing files get asked about one at a
            // time. Drive needs no size check to decide: an upload either lands
            // whole or does not land, so a name that is present is a file that
            // is complete.
            if (clashes && _conflicts is PasteConflictPolicy.Skip or PasteConflictPolicy.FillGaps)
            {
                // Out of the job entirely rather than counted as done: it is not
                // going, so its bytes are not part of what there is to send and
                // the percentage must not be measured against them.
                Interlocked.Add(ref _totalBytes, -file.Size);
                Interlocked.Decrement(ref _totalFiles);
                lock (_log) _log.Skipped(name);
                Report(name);
                return;
            }

            // Overwrite replaces what was there before, never a file this same
            // upload has just sent under the name: which of two arrives first is
            // chance, and the answer that loses nothing is to keep both.
            bool replace = state == NameState.Present && _conflicts == PasteConflictPolicy.Overwrite;

            // Bytes read off the disk for this file, which is what the progress
            // callback counts. Touched only by the one task uploading this file,
            // so it needs no interlock of its own — the shared total does.
            long counted = 0;

            void Chunk(UploadProgress p)
            {
                long delta = p.BytesSent - counted;
                counted = p.BytesSent;
                if (delta != 0) Interlocked.Add(ref _bytesSent, delta);
                Report(name);
            }

            try
            {
                var landed = await _drive.UploadInto(
                    file.LocalPath, folder, Chunk, _announceEach, replace, token);

                if (landed == null)
                {
                    if (reserved) Release(folder, name);
                    Unsend(counted);
                    Fail(name, "Google Drive would not take it");
                    return;
                }

                // Recorded only once it has actually happened. A collision logged
                // when it was noticed would report replacing a file that the
                // upload then failed to replace.
                //
                // The rename records the name it *landed* under, not the one it
                // was given, because "Saved as song (2).flac" is the whole point
                // of the message and the old name says nothing new.
                // A replace that could not replace — a folder has the name —
                // keeps both, and is said as that.
                if (clashes)
                    lock (_log)
                    {
                        if (replace && string.Equals(landed, name, StringComparison.OrdinalIgnoreCase))
                            _log.Overwritten(name);
                        else _log.Renamed(landed);
                    }

                // A folder we have already listed is now one file longer, and the
                // next file with the same name has to see that or two files that
                // collide with each other both read as clean.
                Claim(folder, landed);

                // Trued up to the file's surveyed size, because the two can
                // disagree: the survey measured the file and the upload sent
                // whatever was there at the time. The total is the survey's, so
                // the count has to be as well or the last percent never arrives.
                Interlocked.Add(ref _bytesSent, file.Size - counted);
                Interlocked.Increment(ref _filesDone);
                Report(name, force: true);

                // A move only removes the original once Google has the copy. The
                // other order loses the file if the upload failed at the end.
                if (_move) { try { File.Delete(file.LocalPath); } catch { } }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (reserved) Release(folder, name);
                Unsend(counted);
                throw;
            }
            catch (DriveQuotaException)
            {
                // Let past deliberately, and it is the only failure that is.
                // The account is full or done for the day; the other nine
                // hundred files would each fail identically, burying the one
                // sentence that explains all of them. Stopping here means the
                // window says it once.
                Unsend(counted);
                throw;
            }
            catch (Exception ex)
            {
                // The guard on the clause above is the point of it. Without it
                // this caught *any* OperationCanceledException and rethrew —
                // including the TaskCanceledException HttpClient throws for its
                // own timeout, with nobody's token cancelled. That escaped
                // Parallel.ForEachAsync, which then abandoned every other file,
                // and came out as "Cancelled after 216 of 1134".
                //
                // DriveClient.Send now converts a timeout into a TimeoutException
                // so it cannot be confused in the first place; this guard is the
                // second lock on the same door, because the cost of being wrong
                // here is the whole transfer rather than one file.
                if (reserved) Release(folder, name);
                Unsend(counted);
                Fail(name, ex.Message);
            }
        }

        /// <summary>
        /// Whether the policy has anything to decide. On Drive it always has.
        ///
        /// This used to exclude "keep both", on the reasoning that the answer to
        /// a collision is the same as the answer to no collision so there is
        /// nothing to find out. That is true of a filesystem and **false of
        /// Google Drive**, which is the whole of the bug: Drive permits two
        /// files with the same name in the same folder. A name is not a key
        /// there, an id is.
        ///
        /// So "keep both" on Drive did not keep both under different names the
        /// way it does locally — it made a second file called exactly what the
        /// first one is called, with nothing on screen or in the listing to tell
        /// them apart, and reported it as an ordinary upload. Uploading a folder
        /// twice produced two of everything, silently.
        ///
        /// It is also what made "ask" appear not to ask: a destination that
        /// could not be listed makes the question resolve to "keep both" before
        /// the upload starts, and "keep both" then checked nothing at all.
        ///
        /// The cost is one Drive listing per destination folder, taken once and
        /// shared, and it is the same listing navigating into that folder would
        /// pay for.
        /// </summary>
        private bool NeedsConflictCheck => true;

        /// <summary>
        /// What a destination folder already holds, listed once and then shared.
        ///
        /// Locked on use rather than only on creation: it is read by every stream
        /// that writes into that folder and added to as each file lands, and a
        /// HashSet being read while another thread adds to it is not merely a
        /// stale answer, it is undefined behaviour.
        /// </summary>
        private HashSet<string>? Taken(string folder)
        {
            if (!NeedsConflictCheck) return null;

            return _taken.GetOrAdd(folder, new Lazy<HashSet<string>?>(
                () => _drive.NamesIn(folder),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        private enum NameState { Free, Present, ThisUpload, Unknown }

        /// <summary>Names this upload has reserved, per folder, so they can be told from ones already there.</summary>
        private readonly ConcurrentDictionary<string, HashSet<string>> _reserved =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether <paramref name="name"/> is free in <paramref name="folder"/>,
        /// and reserves it when it is — in one step, under the folder's lock.
        /// </summary>
        private (NameState State, bool Reserved) Reserve(string folder, string name)
        {
            if (!NeedsConflictCheck) return (NameState.Free, false);

            var taken = Taken(folder);
            if (taken == null)
            {
                // Asked again for the next file rather than remembered as
                // unanswerable for the rest of the upload.
                _taken.TryRemove(folder, out _);
                return (NameState.Unknown, false);
            }

            var mine = _reserved.GetOrAdd(folder, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            lock (taken)
            {
                if (taken.Contains(name))
                    return (mine.Contains(name) ? NameState.ThisUpload : NameState.Present, false);

                taken.Add(name);
                mine.Add(name);
                return (NameState.Free, true);
            }
        }

        /// <summary>Gives back a name reserved for a file that did not arrive.</summary>
        private void Release(string folder, string name)
        {
            var taken = Taken(folder);
            if (taken == null) return;
            lock (taken)
            {
                taken.Remove(name);
                if (_reserved.TryGetValue(folder, out var mine)) mine.Remove(name);
            }
        }

        private void Claim(string folder, string name)
        {
            var taken = Taken(folder);
            if (taken == null) return;
            lock (taken)
            {
                taken.Add(name);
                _reserved.GetOrAdd(folder, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(name);
            }
        }

        /// <summary>
        /// Takes back the bytes of a file that did not arrive.
        ///
        /// They were counted as they were read off the disk, which is the only
        /// moment anything knows about them — and for a file that then failed,
        /// they are bytes the destination does not have. Leaving them in makes the
        /// percentage a claim about work that was undone.
        /// </summary>
        private void Unsend(long counted)
        {
            if (counted != 0) Interlocked.Add(ref _bytesSent, -counted);
        }

        private void Fail(string what, string why)
        {
            lock (_errors)
            {
                // Capped, because a destination that has gone away fails every
                // file in the tree and the dialog that shows these can only show
                // twenty. The count is the story past that point.
                if (_errors.Count < 200) _errors.Add($"{what}: {why}");
            }
        }

        /// <summary>
        /// Deletes the source folders of a move, deepest first, and only the ones
        /// that are empty.
        /// </summary>
        private void RemoveEmptySources(IReadOnlyList<string> roots)
        {
            foreach (var root in roots)
            {
                List<string> directories;
                try
                {
                    directories = Directory
                        .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                        .ToList();
                }
                catch { continue; }

                directories.Add(root);

                // Deepest first: a parent only becomes empty once its children
                // have gone, and one pass in that order is enough.
                foreach (var directory in directories.OrderByDescending(d => d.Length))
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(directory).Any())
                            Directory.Delete(directory);
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Hands the caller the running total, at most ten times a second.
        ///
        /// Forced at the start, at the end, and as each file lands — the moments
        /// where the number changing is the whole message and a hundred
        /// milliseconds of staleness would show the wrong file's name against a
        /// finished count.
        /// </summary>
        private void Report(string item, bool force = false)
        {
            if (item.Length > 0) _current = item;

            var report = _report;
            if (report == null) return;

            long now = Environment.TickCount64;
            if (!force)
            {
                long last = Interlocked.Read(ref _lastReport);
                if (now - last < ReportMilliseconds) return;

                // Only whoever wins the exchange sends one. Several streams reach
                // this in the same millisecond by design.
                if (Interlocked.CompareExchange(ref _lastReport, now, last) != last) return;
            }
            else Interlocked.Exchange(ref _lastReport, now);

            try
            {
                // The totals as well as the counts, because a skipped file leaves
                // the job smaller than the survey said: its bytes are never sent
                // and never will be, so measuring the percentage against them
                // would stop the bar ever reaching the end.
                report(new UploadTally(
                    Interlocked.Read(ref _bytesSent), Interlocked.Read(ref _totalBytes),
                    Volatile.Read(ref _filesDone), Volatile.Read(ref _totalFiles), _current));
            }
            catch { }
        }
    }
}
