using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Pulls a file through the operating system's cache so that the next
    /// program to read it — the media engine — finds it already local.
    ///
    /// This exists because of what a music library on a NAS actually costs.
    /// Measured against one over SMB: a track nobody has touched takes about
    /// 1.3 seconds from Enter to sound, and a seek into an uncached part of it
    /// about two seconds. The bytes are not the problem — the round trips are,
    /// and the first sixty-four kilobytes alone can take a second.
    ///
    /// Reading the first couple of megabytes ahead of time cuts the start to
    /// about 570ms, and reading the whole file while it plays makes seeking
    /// instant. Neither is extra network traffic: it is the same data playback
    /// would have read anyway, fetched earlier and in one sequential run rather
    /// than in scattered demand reads.
    ///
    /// Nothing is kept. The bytes are read into one reusable buffer and thrown
    /// away; the copy that matters is the one Windows now holds.
    /// </summary>
    public sealed class AudioPrefetch : IDisposable
    {
        private const int BufferBytes = 256 * 1024;

        private readonly object _gate = new();
        private CancellationTokenSource? _running;
        private string _path = "";
        private bool _disposed;

        /// <summary>
        /// That a file is being warmed, as a catalogue id and a sentence. Null
        /// changes nothing, and it is called on the worker doing the reading —
        /// never on the thread that asked for it, which is the UI thread.
        /// </summary>
        public Action<string, string>? Notify { get; set; }

        /// <summary>
        /// Starts pulling <paramref name="path"/>, abandoning whatever it was
        /// doing before. Returns immediately; the reading happens on a worker,
        /// because this is exactly the network I/O the rest of the application
        /// goes to such lengths to keep off the UI thread.
        /// </summary>
        public Task Begin(string path, long maxBytes, bool remoteOnly)
        {
            if (string.IsNullOrEmpty(path) || maxBytes <= 0) return Task.CompletedTask;

            CancellationTokenSource source;
            lock (_gate)
            {
                if (_disposed) return Task.CompletedTask;

                // Already reading this one. Restarting would throw away the
                // progress made so far and begin the same work again.
                if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) &&
                    _running is { IsCancellationRequested: false })
                    return Task.CompletedTask;

                CancelLocked();
                _path = path;
                source = new CancellationTokenSource();
                _running = source;
            }

            var notify = Notify;

            return Task.Run(async () =>
            {
                try { await Pull(path, maxBytes, remoteOnly, notify, source.Token); }
                finally
                {
                    lock (_gate)
                    {
                        // Only if nothing has taken over in the meantime.
                        if (ReferenceEquals(_running, source)) { _running = null; _path = ""; }
                    }
                }
            });
        }

        public void Cancel()
        {
            lock (_gate) CancelLocked();
        }

        private void CancelLocked()
        {
            try { _running?.Cancel(); } catch { }
            _running = null;
            _path = "";
        }

        /// <summary>
        /// Reads the head of the file, asynchronously and cancellably.
        ///
        /// Both of those matter, and neither is style. This reads megabytes from
        /// a share, and it did it with a synchronous <c>Read</c> on a thread-pool
        /// thread — which holds that thread for the whole of it and cannot see
        /// the token until the read it is inside of returns. Arrowing through a
        /// folder of network tracks starts one of these per row rested on, so a
        /// row a second left a second's worth of blocked threads standing on top
        /// of each other: the cancellation was cooperative in name only, and the
        /// pool grows by about one thread every half second when it runs out.
        ///
        /// Everything else the application does off the UI thread then queues
        /// behind that — the next folder's enumeration, the idle availability
        /// probe, a folder-size walk — which is felt as the window going slow
        /// while arrowing, a long way from anything to do with reading ahead.
        ///
        /// Asynchronous, the read releases the thread while the network answers
        /// and the token stops the read itself rather than the loop around it.
        /// </summary>
        private static async Task Pull(string path, long maxBytes, bool remoteOnly,
            Action<string, string>? notify, CancellationToken token)
        {
            try
            {
                // On a local disk this buys nothing worth the reading — Windows
                // is already fast there, and the file is competing with whatever
                // else wants the disk.
                if (remoteOnly && !LooksRemote(path)) return;

                // Below the check, so what is reported is a read that is really
                // about to happen rather than one the setting turned into nothing.
                if (notify != null)
                {
                    try { notify("audiofile.prefetch", "Reading ahead " + Path.GetFileName(path)); }
                    catch { }
                }

                var buffer = new byte[BufferBytes];

                // Tried more than once, and this is the whole of "never let it
                // hang on one request".
                //
                // On a cloud or network path the first read is not a disk read:
                // it is a round trip that the provider may answer slowly, refuse
                // once, or simply lose. A single attempt that stalls means the
                // warm-up silently does nothing, and the first anybody knows is
                // pressing Enter and waiting the whole cold cost anyway — the
                // exact failure this exists to prevent, and invisible, because a
                // prefetch that achieved nothing looks identical to one that was
                // never asked for.
                //
                // So each pass has its own deadline. One that produces nothing
                // within StartWithinMilliseconds is abandoned and started again
                // rather than waited on, because a read that has not begun in
                // that time is not slow, it is stuck — and the second attempt
                // usually lands immediately, since the provider has by then done
                // the work the first one triggered.
                for (int attempt = 0; attempt < Attempts && !token.IsCancellationRequested; attempt++)
                {
                    if (attempt > 0)
                    {
                        try { await Task.Delay(RetryPauseMilliseconds, token); }
                        catch (OperationCanceledException) { return; }
                    }

                    if (await PullOnce(path, buffer, maxBytes, token)) return;
                }
            }
            catch
            {
                // A prefetch that fails has cost nothing. The file may have gone,
                // the share may be down, or it may simply be locked — and in
                // every one of those cases playing it is the thing that should
                // report the problem, not warming it up.
            }
        }

        /// <summary>
        /// How many times the head of a file is asked for before giving up.
        ///
        /// Three, which is enough for the case this is for — a cloud provider
        /// that answers the second ask because the first one woke it up — and
        /// few enough that a file which genuinely cannot be read is not being
        /// hammered on behalf of somebody who has merely rested the cursor.
        /// </summary>
        private const int Attempts = 3;

        /// <summary>
        /// How long a pass may produce *nothing at all* before it is abandoned
        /// and tried again.
        ///
        /// This is a deadline on the first byte, not on the read. Once bytes are
        /// arriving the pass is left alone however long the file takes, because
        /// then it is working and the only question is bandwidth. What it
        /// catches is the pass that never starts.
        /// </summary>
        private const int StartWithinMilliseconds = 400;

        /// <summary>A moment between attempts, so a refusal is not retried instantly.</summary>
        private const int RetryPauseMilliseconds = 120;

        /// <summary>
        /// One attempt at reading the head of the file. True when it read
        /// something, which is the caller's signal to stop trying.
        /// </summary>
        private static async Task<bool> PullOnce(
            string path, byte[] buffer, long maxBytes, CancellationToken token)
        {
            // A deadline of its own, linked to the caller's so moving off the row
            // still stops everything immediately. Only the *first* read is under
            // it — see below.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(StartWithinMilliseconds);

            try
            {
                // Shared as widely as possible: the media engine has this file
                // open at the same time, and the user must still be able to move
                // or delete it.
                await using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);

                int first = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), deadline.Token);
                if (first <= 0) return true;   // an empty file is read, not stuck

                // Answered once, so the path is awake. The rest of the file runs
                // on the caller's token with no deadline at all: a large track
                // over a slow link is *supposed* to take a while, and cutting it
                // off at four hundred milliseconds would abandon exactly the
                // read that is going well.
                long total = first;
                while (total < maxBytes && !token.IsCancellationRequested)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                    if (read <= 0) break;
                    total += read;
                }

                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The cursor moved. Not a failure, and not worth another pass.
                return true;
            }
            catch
            {
                // Either the deadline fired or the open failed. Both are worth
                // one more try; the caller decides how many.
                return false;
            }
        }

        // What Windows marks a file with when its contents are not really here.
        // Cloud drives — OneDrive, Google Drive, Dropbox — mount as an ordinary
        // drive letter and report themselves as fixed disks, so the drive type
        // alone says "local" about a file that is actually across the internet.
        // These attributes are how such a file admits it.
        private const int FileAttributeOffline = 0x00001000;
        private const int FileAttributeRecallOnOpen = 0x00040000;
        private const int FileAttributeRecallOnDataAccess = 0x00400000;

        private const int NotReallyHere =
            FileAttributeOffline | FileAttributeRecallOnOpen | FileAttributeRecallOnDataAccess;

        /// <summary>
        /// Whether the file is somewhere a round trip is expensive: a UNC path, a
        /// mapped network drive, or a file whose contents live in a cloud and are
        /// fetched on demand.
        /// </summary>
        public static bool LooksRemote(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return false;
                if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;

                var root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return false;

                if (new DriveInfo(root).DriveType == DriveType.Network) return true;
            }
            catch
            {
                // An unreadable drive is not worth guessing about.
                return false;
            }

            // Only now, and only for something that claimed to be a local disk:
            // this is a real query about a real file, and doing it on a share
            // first would be a round trip spent asking whether round trips are
            // expensive.
            try
            {
                return ((int)File.GetAttributes(path) & NotReallyHere) != 0;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                CancelLocked();
            }
        }
    }
}
