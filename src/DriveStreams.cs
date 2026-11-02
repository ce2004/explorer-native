using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Uploads that are sent while they are still being written (4.3.0): what
    /// another program saving onto the drive letter becomes, with nothing kept
    /// on the PC's disk (Conner: "uploading should not use the PC's space").
    ///
    /// A resumable session opened without a length, then pieces as they are
    /// ready — every piece but the last a multiple of 256 KiB, as Google
    /// requires, the last one carrying the total. Each piece is held in memory
    /// only until Google confirms it; a connection that breaks part way is asked
    /// how much it kept, and the rest of the piece is sent again.
    /// </summary>
    internal sealed partial class DriveClient
    {
        /// <summary>The size every piece but the last must be a multiple of.</summary>
        internal const int PieceAlignment = 256 * 1024;

        /// <summary>
        /// A small file from memory, in one request. For files another program
        /// saved on the letter that never grew past the in-memory limit.
        /// </summary>
        /// <param name="tag">
        /// The tag a new file is made with, kept by the caller across tries: an
        /// earlier try whose answer was lost is found by it (<see cref="MadeWithTag"/>)
        /// instead of a second file being made.
        /// </param>
        internal Task<DriveUploaded> UploadFromMemory(byte[] data, int length, string name, string parentId,
            DateTime modified, string? replaceId, CancellationToken token, string? tag = null) =>
            UploadWhole("", name, parentId, length, null, token, replaceId,
                () => new MemoryStream(data, 0, length, writable: false), Truncated(modified), tag);

        private static DateTime Truncated(DateTime utc) =>
            new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

        /// <summary>
        /// Opens a session for contents whose length is not known yet: a new
        /// file in <paramref name="parentId"/>, or new contents for
        /// <paramref name="replaceId"/>, which keeps its id and revisions.
        /// </summary>
        internal Task<string> BeginStream(string name, string parentId, DateTime modified, string? replaceId,
            CancellationToken token) =>
            BeginUpload(name, parentId, -1, Truncated(modified), token, replaceId);

        /// <summary>
        /// Bytes that went to a session and are no longer anywhere: Google says
        /// it holds less than was confirmed before. Nothing can send them again.
        /// </summary>
        internal sealed class StreamLostException : IOException
        {
            public StreamLostException(string message) : base(message) { }
        }

        /// <summary>
        /// Sends <paramref name="count"/> bytes at <paramref name="offset"/> of a
        /// session, and returns once Google holds all of them — or, when
        /// <paramref name="total"/> is given (the last piece), once the file is
        /// made, with what Drive now holds. Not now (5xx, 429, a dropped line)
        /// is waited out within <paramref name="token"/>; no is thrown.
        /// </summary>
        internal async Task<DriveUploaded?> SendPiece(string session, long offset, byte[] buffer, int count,
            long? total, string name, CancellationToken token)
        {
            long end = offset + count;
            long at = offset;
            bool ask = false;
            var random = new Random();
            var slow = new SlowDown();
            var busy = new ServerBusy();
            string? lastProblem = null;
            Exception? lastFailure = null;

            for (int attempt = 0; attempt < MaxUploadAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (attempt > 0) await Pause(RetryDelay(attempt - 1, random), token);

                if (ask)
                {
                    try
                    {
                        var (finished, done, confirmed) = await AskStream(session, total, token);
                        if (finished) return done;
                        if (confirmed < offset)
                            throw new StreamLostException(
                                $"Google kept {confirmed:N0} bytes of {name} where it had confirmed {offset:N0}");
                        at = Math.Min(confirmed, end);
                        ask = false;
                        if (at >= end && total == null) return null;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (StreamLostException) { throw; }
                    catch (UploadRefusedException refused) when (refused.SessionGone || !refused.Retryable)
                    {
                        if (DriveQuotaException.From(refused.Status, refused.Body) is { } stop) throw stop;
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastProblem = ex.Message; lastFailure = ex;
                        if (await WaitOutNetwork(ex, $"sending {name}", token)) attempt--;
                        continue;
                    }
                }

                // Everything held and the last answer lost: the last byte is sent
                // again with the total, which Google takes as the end.
                if (at >= end) at = end - 1;

                int from = (int)(at - offset);
                int length = count - from;
                using var request = new HttpRequestMessage(HttpMethod.Put, session);
                var source = new MemoryStream(buffer, from, length, writable: false);
                var counter = new ProgressStream(source, at, end, null);
                var content = new StreamContent(counter, UploadBufferBytes);
                content.Headers.ContentLength = length;
                content.Headers.ContentRange = total is { } whole
                    ? new ContentRangeHeaderValue(at, end - 1, whole)
                    : new ContentRangeHeaderValue(at, end - 1);
                request.Content = content;

                try
                {
                    using var response = await SendWatched(request, counter, token);
                    var (finished, done, confirmed) = await ReadUploadAnswer(response, token);
                    if (finished) return done;
                    if (confirmed >= end && total == null) return null;
                    if (confirmed < offset)
                        throw new StreamLostException(
                            $"Google kept {confirmed:N0} bytes of {name} where it had confirmed {offset:N0}");
                    // Part of it kept: the rest goes again, which is not a failure.
                    if (confirmed > at) attempt--;
                    at = Math.Min(confirmed, end);
                    lastProblem = "Google confirmed only part of the piece";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (StreamLostException) { throw; }
                catch (UploadRefusedException refused) when (refused.SessionGone)
                {
                    throw;
                }
                catch (UploadRefusedException refused) when (IsRateLimit(refused.Status, refused.Body))
                {
                    lastProblem = refused.Message; lastFailure = refused;
                    if (!await WaitOutRateLimit(slow, random, null, token)) throw;
                    ask = true;
                    attempt--;
                }
                catch (UploadRefusedException refused) when (!refused.Retryable)
                {
                    if (DriveQuotaException.From(refused.Status, refused.Body) is { } stop) throw stop;
                    throw;
                }
                catch (Exception ex)
                {
                    lastProblem = ex.Message; lastFailure = ex;
                    if (ex is UploadRefusedException { } busyAnswer && IsServerBusy(busyAnswer.Status) &&
                        await WaitOutBusy(busy, random, null, token))
                    {
                        ask = true;
                        attempt--;
                        continue;
                    }
                    if (await WaitOutNetwork(ex, $"sending {name}", token)) attempt--;
                    ask = true;
                }
            }

            throw new InvalidOperationException(
                $"sending {name} stopped at {at:N0} bytes after {MaxUploadAttempts} attempts" +
                (lastProblem == null ? "" : $": {lastProblem}"), lastFailure);
        }

        /// <summary>
        /// How much a session holds, after a piece broke: Google ignores bytes
        /// it already has, so this is where sending goes on from. With the
        /// total once the last piece was sent, a finished session answers with
        /// the file.
        /// </summary>
        internal Task<(bool Finished, DriveUploaded? Done, long Confirmed)> AskHeld(
            string session, long? total, CancellationToken token) => AskStream(session, total, token);

        /// <summary>
        /// Asks a session how much it holds. "bytes */*" while the total is not
        /// known; "bytes */total" once it is, which also answers with the file
        /// when the upload has completed.
        /// </summary>
        private async Task<(bool Finished, DriveUploaded? Done, long Confirmed)> AskStream(
            string session, long? total, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, session);
            var content = new ByteArrayContent(Array.Empty<byte>());
            content.Headers.ContentLength = 0;
            content.Headers.TryAddWithoutValidation("Content-Range",
                total is { } whole ? $"bytes */{whole}" : "bytes */*");
            request.Content = content;
            using var response = await Send(request, HttpCompletionOption.ResponseContentRead, token);
            return await ReadUploadAnswer(response, token);
        }
    }
}
