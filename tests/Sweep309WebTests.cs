using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Regression tests for the second web app sweep (after 3.0.8): the clipboard image that leaked when the
    /// clipboard thread never started, Forward before a track's length is known, messages said twice on the
    /// Clipboard tab and the code screen, a Drive stream left leased at shutdown, jobs that stayed "waiting" for
    /// ever, paths naming a file's hidden streams, and the copy check of a move out of Drive into a dotted folder.
    /// </summary>
    internal static class Sweep309WebTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        private const string Code = "24681357";

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            Console.WriteLine("Web app sweep (3.0.9):");

            var js = WebAssets.TryGet("/app/app.js", out var bytes, out _) ? Encoding.UTF8.GetString(bytes) : "";
            var html = WebAssets.TryGet("/", out var page, out _) ? Encoding.UTF8.GetString(page) : "";
            Check("app.js and index.html are embedded", js.Length > 1000 && html.Length > 500);

            ClipboardImageWithoutWindowTests();
            SkipBeforeLengthKnownTests(js);
            SaidOnceTests(js, html);
            JobsWaitingArePolledTests(js);
            UploadStoppedWhileStartingTests(js);
            AudioLeaseReachesDecoderTests();
            StreamsAfterDisposeTests();
            ListWaitTests(js);
            ContentTypeTests();
            FindExeIgnoresRelativePathTests();
            MoveOutOfDriveChecksLiteralPathTests();
            await StreamPathTests();
        }

        /// <summary>A function of app.js, from its declaration to the next top-level one.</summary>
        private static string Function(string js, string name)
        {
            var m = Regex.Match(js, @"^(async )?function " + Regex.Escape(name) + @"\(", RegexOptions.Multiline);
            if (!m.Success) return "";
            var next = Regex.Match(js[(m.Index + m.Length)..], @"^(async )?function |^const |^let |^// -", RegexOptions.Multiline);
            return next.Success ? js.Substring(m.Index, m.Length + next.Index) : js[m.Index..];
        }

        /// <summary>A C# method of a source file, from its signature to the next member at the same indent.</summary>
        private static string Method(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0) return "";
            var next = Regex.Match(source[(at + signature.Length)..], @"\n        (public|private|internal) ");
            return next.Success ? source.Substring(at, signature.Length + next.Index) : source[at..];
        }

        private static string? Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        /// <summary>
        /// With the clipboard thread's window not there, the image was decoded into a bitmap whose only Dispose was
        /// in work that never ran: one image left behind per request. It is now refused before anything is decoded.
        /// </summary>
        private static void ClipboardImageWithoutWindowTests()
        {
            byte[] png;
            using (var bitmap = new Bitmap(4, 4))
            using (var output = new MemoryStream())
            {
                bitmap.Save(output, ImageFormat.Png);
                png = output.ToArray();
            }
            var clipboard = new ConnectClipboard();   // never started: no window
            Task<long>? task = null;
            Exception? thrown = null;
            try { task = clipboard.SetImageAsync(png); }
            catch (Exception e) { thrown = e; }
            Check("an image for a clipboard with no window does not throw on the spot", thrown == null, thrown?.Message);
            var error = task?.Exception?.InnerException as ConnectException;
            Check("it is refused as the clipboard being unusable (503)", task is { IsFaulted: true } && error?.Status == 503,
                  task == null ? "no task" : $"{task.Status} {error?.Status}");

            var body = Method(Source("ConnectClipboard.cs") ?? "", "public Task<long> SetImageAsync(");
            int window = body.IndexOf("_window == null", StringComparison.Ordinal);
            int decode = body.IndexOf("Image.FromStream", StringComparison.Ordinal);
            Check("the window is asked about before the image is decoded", window >= 0 && decode > window, $"{window} {decode}");
        }

        /// <summary>Forward or Back before the track's length was known took the end as 0 and went to the start.</summary>
        private static void SkipBeforeLengthKnownTests(string js)
        {
            var body = Function(js, "skip");
            Check("skip is there", body.Length > 0);
            Check("an unknown length is not taken as zero", !body.Contains("(a.duration || 0)"), body);
            Check("an unknown length leaves no end to stop short of", body.Contains("isFinite(a.duration)") && body.Contains("Infinity"));
        }

        /// <summary>The Clipboard tab's status line was a live region, and announce() said the same words too.</summary>
        private static void SaidOnceTests(string js, string html)
        {
            var line = Regex.Match(html, "<p id=\"clip-status\"[^>]*>").Value;
            Check("the clipboard status line is there", line.Length > 0);
            Check("and it is not a live region of its own", !line.Contains("role=") && !line.Contains("aria-live"), line);
            var send = Function(js, "sendText");
            Check("sending text is still announced", send.Contains("announce('Sent to the PC clipboard')"));

            int at = js.IndexOf("'code-form'", StringComparison.Ordinal);
            var submit = at < 0 ? "" : js.Substring(at, Math.Min(1400, js.Length - at));
            Check("the code screen's unreachable line is said by its alert only",
                  submit.Contains("$('code-error').textContent = UNREACHABLE") && !submit.Contains("announce(UNREACHABLE"), at < 0 ? "no handler" : null);
            Check("and the alert is emptied before each try, so the same sentence is said again",
                  Regex.IsMatch(submit, @"\$\('code-error'\)\.textContent = '';[\s\S]*timedFetch"));

            Check("the select count is rewritten only when it changes", Function(js, "updateSelectCount").Contains("setText("));
        }

        /// <summary>A job whose poll once missed the network was marked waiting and never polled again.</summary>
        private static void JobsWaitingArePolledTests(string js)
        {
            var body = Function(js, "pollJobs");
            Check("waiting jobs are polled too", Regex.IsMatch(body, @"filter\([^\n]*'waiting'"), body.Length > 0 ? null : "no pollJobs");
            Check("an answer puts a waiting job back to running", body.Contains("t.state = 'running'"));
        }

        /// <summary>Stop pressed while the PC was starting an upload left that upload on the PC for a day.</summary>
        private static void UploadStoppedWhileStartingTests(string js)
        {
            var body = Function(js, "upload");
            Check("an upload stopped while starting is called off on the PC",
                  Regex.IsMatch(body, @"id = s\.id;\s*(//[^\n]*\n\s*)*if \(stopped\) \{ api\('/api/upload/cancel'"));
        }

        /// <summary>
        /// /api/audio's lease went to the decoder in a second step that the request's token could stop from ever
        /// starting, leaving one Drive download leased until the app closed.
        /// </summary>
        private static void AudioLeaseReachesDecoderTests()
        {
            var body = Method(Source("ConnectServer.cs") ?? "", "private async Task Audio(");
            Check("the audio route is there", body.Length > 0);
            Check("the step that hands the lease to the decoder is not cancellable",
                  Regex.IsMatch(body, @"ConnectAudio\.Open\([^;]*;\s*\}, CancellationToken\.None\)"));
        }

        private sealed class FakeSource : IRangeSource
        {
            public int Disposed;
            public long Length => 1000;
            public int Streams => 1;
            public Task<int> ReadAsync(long offset, Memory<byte> into, CancellationToken token)
            {
                int n = (int)Math.Min(into.Length, Math.Max(0, Length - offset));
                into.Span[..n].Clear();
                return Task.FromResult(n);
            }
            public void Dispose() => Interlocked.Increment(ref Disposed);
        }

        /// <summary>
        /// A lease asked for while the server was being disposed added a download that nothing would ever stop.
        /// After Dispose a lease is refused (never null, which callers would read as "not on Drive").
        /// </summary>
        private static void StreamsAfterDisposeTests()
        {
            int opened = 0;
            var after = new ConnectStreams(_ => { opened++; return new FakeSource(); });
            after.Dispose();
            ConnectException? refused = null;
            try { after.Lease(@"G:\a.flac"); } catch (ConnectException e) { refused = e; }
            Check("a lease after Dispose is refused with a 503", refused?.Status == 503, refused?.Message);
            Check("and nothing is opened for it", opened == 0, opened.ToString());

            ConnectStreams? racing = null;
            var source = new FakeSource();
            racing = new ConnectStreams(_ => { racing!.Dispose(); return source; });
            refused = null;
            try { racing.Lease(@"G:\b.flac"); } catch (ConnectException e) { refused = e; }
            Check("disposed while a lease was being opened, the lease is refused", refused?.Status == 503, refused?.Message);
            Check("and nothing is left held", racing.Count == 0, racing.Count.ToString());
            Check("and the download opened for it is let go", Volatile.Read(ref source.Disposed) > 0);
            Check("and the sweep is not running", !racing.Sweeping);
        }

        /// <summary>The List comment said ten seconds while the code waited 45.</summary>
        private static void ListWaitTests(string js)
        {
            Check("the PC gives a folder 45 seconds", ConnectServer.ListWait == TimeSpan.FromSeconds(45));
            var server = Source("ConnectServer.cs") ?? "";
            var doc = Regex.Match(server, @"(///[^\n]*\n\s*)+public async Task<List<ConnectEntry>\?> List\(").Value;
            Check("the comment on List says what it waits", doc.Contains("ListWait") && !doc.Contains("ten seconds"), doc);
            var m = Regex.Match(js, @"'/api/list'[^\n]*timeout:\s*(\d+)");
            Check("and the phone waits longer than that",
                  m.Success && int.Parse(m.Groups[1].Value) > ConnectServer.ListWait.TotalMilliseconds, m.Value);
        }

        /// <summary>Raw .aac went as MP4 and .m4r as octet-stream, both straight to the phone's player.</summary>
        private static void ContentTypeTests()
        {
            Check(".aac is typed as ADTS AAC", ConnectServer.ContentType(@"C:\a.aac") == "audio/aac", ConnectServer.ContentType(@"C:\a.aac"));
            Check(".m4r is typed as MP4 audio", ConnectServer.ContentType(@"C:\a.m4r") == "audio/mp4");
            Check(".m4a still is", ConnectServer.ContentType(@"C:\a.m4a") == "audio/mp4");
        }

        /// <summary>A relative PATH entry was the current folder's, so a tailscale.exe planted there could be run.</summary>
        private static void FindExeIgnoresRelativePathTests()
        {
            var body = Method(Source("TailscaleWeb.cs") ?? "", "public static string? FindExe(");
            Check("FindExe skips PATH entries that are not full paths", body.Contains("IsPathFullyQualified"), body.Length > 0 ? null : "no FindExe");
        }

        /// <summary>
        /// A move out of Drive into a folder whose name ends in a dot checked the copy by its plain path, which is
        /// the namesake's, so every original was kept as "could not be shown to hold everything".
        /// </summary>
        private static void MoveOutOfDriveChecksLiteralPathTests()
        {
            var body = Method(Source("ConnectFiles.cs") ?? "", "private async Task FinishMoveOutOfDrive(");
            Check("FinishMoveOutOfDrive is there", body.Length > 0);
            Check("the copy of a file is looked at by its literal path", body.Contains("new FileInfo(Io(arrivedAt))"));
            Check("and a folder's copy is checked by it too", body.Contains("VerifyCopied(path, Io(arrivedAt)"));
            Check("no plain FileInfo of where it arrived is left", !body.Contains("new FileInfo(arrivedAt)"));
        }

        private sealed class Files : IConnectFiles
        {
            public readonly List<string> Calls = new();
            public string? Unavailable(string path) => null;
            public (long Used, long Limit)? DriveQuota() => null;
            public Task<ConnectSize> SizeAsync(string folder, CancellationToken token) { Calls.Add("size"); return Task.FromResult(new ConnectSize(folder, 0, 0, 0, true)); }
            public Task<ConnectStat> StatAsync(string path, CancellationToken token) { Calls.Add("stat"); return Task.FromResult(new ConnectStat(path, "x", true, 0, DateTime.UtcNow, DateTime.UtcNow, false, false)); }
            public Task<string> RenameAsync(string path, string newName, CancellationToken token) { Calls.Add("rename"); return Task.FromResult(path); }
            public Task<string> CreateFolderAsync(string parent, string name, CancellationToken token) { Calls.Add("mkdir"); return Task.FromResult(parent); }
            public Task<ConnectDeleted> DeleteAsync(IReadOnlyList<string> paths, CancellationToken token) { Calls.Add("delete"); return Task.FromResult(new ConnectDeleted(paths.Count, Array.Empty<ConnectFailure>())); }
            public void CheckTransfer(IReadOnlyList<string> paths, string destination, bool move) { }
            public Task TransferAsync(ConnectJob job, IReadOnlyList<string> paths, string destination, bool move, PasteConflictPolicy conflict) { Calls.Add("transfer"); return Task.CompletedTask; }
            public Task<string> UploadAsync(string folder, string name, PasteConflictPolicy conflict, Stream body, CancellationToken token) { Calls.Add("upload"); return Task.FromResult(folder); }
            public bool OnDrive(string path) => false;
            public Task<string> PlaceFileAsync(string file, string folder, string name, PasteConflictPolicy conflict, Action<long, long>? progress, CancellationToken token) => Task.FromResult(folder);
            public IReadOnlyList<string> AudioExtensions() => new[] { ".flac" };
            public Task<Dictionary<string, object?>> DetailsAsync(string path, ConnectStat stat, bool hash, CancellationToken token) => Task.FromResult(new Dictionary<string, object?>());
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        /// <summary>
        /// "C:\x\a.txt:secret" is one of a file's alternate data streams. Every route took it as a path: opened, it
        /// was read; deleted, the Recycle Bin was handed a stream. Refused now before anything reaches the files.
        /// </summary>
        private static async Task StreamPathTests()
        {
            Check("a colon past the drive's is a stream path", ConnectServer.IsStreamPath(@"C:\x\a.txt:secret"));
            Check("a plain path is not", !ConnectServer.IsStreamPath(@"C:\x\a.txt") && !ConnectServer.IsStreamPath(@"C:\"));
            Check("nor a bare drive or nothing", !ConnectServer.IsStreamPath("C:") && !ConnectServer.IsStreamPath(null));

            var dir = Path.Combine(Path.GetTempPath(), "en-sweep309-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "a.txt");
            File.WriteAllText(file, "main");
            try { File.WriteAllText(file + ":secret", "hidden"); } catch { }

            int port = FreePort();
            var files = new Files();
            var server = new ConnectServer(_ => null, p => new FileRangeSource(p), () => Code, loopbackOnly: true, port: port, files: files,
                uploads: Path.Combine(dir, "uploads"), clipboardSends: Path.Combine(dir, "sends"));
            try
            {
                server.Start();
                bool listening = false;
                for (int i = 0; i < 50 && !listening; i++)
                {
                    try { using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, port); listening = true; }
                    catch { await Task.Delay(50); }
                }
                if (!listening) { Check("the server listens", false); return; }

                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(15) };
                async Task<(int Status, string Body)> Send(HttpMethod method, string url, string? json = null)
                {
                    var request = new HttpRequestMessage(method, url);
                    request.Headers.Add("X-Connect-Code", Code);
                    if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = await http.SendAsync(request);
                    return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
                }

                var stream = Uri.EscapeDataString(file + ":secret");
                var (fileStatus, fileBody) = await Send(HttpMethod.Get, "/api/file?path=" + stream);
                Check("a stream is not served as a file", fileStatus == 400 && !fileBody.Contains("hidden"), $"{fileStatus} {fileBody}");
                var (plainStatus, plainBody) = await Send(HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(file));
                Check("the file itself still is", plainStatus == 200 && plainBody == "main", $"{plainStatus} {plainBody}");

                var (listStatus, _) = await Send(HttpMethod.Get, "/api/list?path=" + Uri.EscapeDataString(dir + ":x"));
                Check("a stream is not listed as a folder", listStatus == 404, listStatus.ToString());

                var paths = System.Text.Json.JsonSerializer.Serialize(new { paths = new[] { file + ":secret" } });
                var (deleteStatus, deleteBody) = await Send(HttpMethod.Post, "/api/delete", paths);
                Check("a stream is refused for delete with a sentence", deleteStatus == 400 && deleteBody.Contains("part of a file"), $"{deleteStatus} {deleteBody}");
                var rename = System.Text.Json.JsonSerializer.Serialize(new { path = file + ":secret", newName = "b.txt" });
                var (renameStatus, _) = await Send(HttpMethod.Post, "/api/rename", rename);
                Check("and for rename", renameStatus == 400, renameStatus.ToString());
                Check("none of it reached the files", !files.Calls.Contains("delete") && !files.Calls.Contains("rename"), string.Join(",", files.Calls));
            }
            finally
            {
                server.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
