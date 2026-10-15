using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Bug sweep, Drive client half: a small upload whose token was refused
    /// once, and a folder-sync download whose request timed out before any
    /// bytes arrived. Both used to fail the file outright.
    /// </summary>
    internal static class Sweep308ClientTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Drive client sweep:");

            await SmallUploadRetriesARefusedToken();
            await DownloadRetriesATimeout();

            Console.WriteLine();
        }

        /// <summary>Answers each request with whatever the test says, counting them.</summary>
        private sealed class Scripted : HttpMessageHandler
        {
            private readonly Func<int, HttpRequestMessage, HttpResponseMessage> _answer;
            public int Calls;

            public Scripted(Func<int, HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                int call = Interlocked.Increment(ref Calls);
                return Task.FromResult(_answer(call, request));
            }
        }

        private static async Task SmallUploadRetriesARefusedToken()
        {
            var file = Path.Combine(Path.GetTempPath(), "en-sweep308-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
            File.WriteAllText(file, "abc");
            try
            {
                var handler = new Scripted((call, _) => call == 1
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("{\"error\":{\"code\":401,\"message\":\"Invalid Credentials\"}}"),
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":\"landed\",\"size\":\"3\"}", Encoding.UTF8, "application/json"),
                    });
                var client = new DriveClient(handler, 0.001);

                string id = "";
                string problem = "";
                try
                {
                    var done = await client.UploadDetailed(file, "a.txt", "parent", null, CancellationToken.None);
                    id = done.Id;
                }
                catch (Exception ex) { problem = ex.GetType().Name + ": " + ex.Message; }

                Equal("Drive upload: a small file whose token was refused once is sent again and lands",
                    "landed", id.Length > 0 ? id : problem);
                Equal("and it took exactly one more request", "2", handler.Calls.ToString());
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        private static async Task DownloadRetriesATimeout()
        {
            var data = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7)).ToArray();
            var handler = new Scripted((call, request) =>
            {
                if (call == 1) throw new TimeoutException("Google Drive did not answer within 15 seconds");
                long from = request.Headers.Range?.Ranges.First().From ?? 0;
                var body = new ByteArrayContent(data, (int)from, data.Length - (int)from);
                body.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, data.Length - 1, data.Length);
                return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = body };
            });
            var client = new DriveClient(handler, 0.001);

            using var into = new MemoryStream();
            string problem = "";
            try
            {
                await client.DownloadTo("file", 0, data.Length, into, _ => { }, CancellationToken.None);
            }
            catch (Exception ex) { problem = ex.GetType().Name + ": " + ex.Message; }

            Check("Drive download: a request that timed out is asked again rather than failing the file",
                problem.Length == 0, problem);
            Check("and what arrived is the whole file", into.ToArray().SequenceEqual(data),
                $"{into.Length} of {data.Length} bytes");
        }
    }
}
