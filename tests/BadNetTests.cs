using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Copies out of Drive on a bad connection (4.2.7, Conner: nvda.7z sat at 100
    /// percent and failed). A piece whose connection broke part way used to be
    /// thrown away and asked again from its start, inside twenty seconds for the
    /// whole piece, so on a weak link no piece got through. Quick group:
    /// EXPLORERNATIVE_SELFTEST_ONLY=badnet.
    /// </summary>
    internal static class BadNetTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;
            Console.WriteLine("Bad connections:");

            await PieceSurvivesManyDrops();
            await DropsWithoutLengthStillResume();
            SettingsAreRead();
            WindowStartsAgainWithTheFile();

            Console.WriteLine();
        }

        /// <summary>Serves a file by range, breaking each response after <c>dropAfter</c> bytes.</summary>
        private sealed class Breaking : HttpMessageHandler
        {
            private readonly byte[] _data;
            private readonly int _dropAfter;
            private readonly bool _withLength;
            public readonly List<(long From, long To)> Asked = new();

            public Breaking(byte[] data, int dropAfter, bool withLength = true)
            {
                _data = data;
                _dropAfter = dropAfter;
                _withLength = withLength;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var range = request.Headers.Range!.Ranges.First();
                long from = range.From ?? 0;
                long to = Math.Min(range.To ?? _data.Length - 1, _data.Length - 1);
                lock (Asked) Asked.Add((from, to));
                var slice = _data.AsSpan((int)from, (int)(to - from + 1)).ToArray();
                var content = new StreamContent(new BreakingStream(slice, _dropAfter));
                if (_withLength) content.Headers.ContentLength = slice.Length;
                content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, _data.Length);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
            }
        }

        private sealed class BreakingStream : Stream
        {
            private readonly byte[] _data;
            private readonly int _dropAfter;
            private int _at;

            public BreakingStream(byte[] data, int dropAfter) { _data = data; _dropAfter = dropAfter; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_at >= _data.Length) return 0;
                if (_at >= _dropAfter) throw new IOException("the connection was reset");
                int n = Math.Min(Math.Min(count, 64 * 1024), Math.Min(_data.Length, _dropAfter) - _at);
                Array.Copy(_data, _at, buffer, offset, n);
                _at += n;
                return n;
            }

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

        private static byte[] Noise(int length, int seed)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        private static async Task PieceSurvivesManyDrops()
        {
            // A 4MB piece over a link that breaks every 300KB: fourteen breaks.
            var data = Noise(4 * 1024 * 1024, 5);
            var handler = new Breaking(data, 300 * 1024);
            var client = new DriveClient(handler, 0.001);

            byte[]? got = null;
            string problem = "";
            try { got = await client.ReadRange("file", 0, data.Length, CancellationToken.None); }
            catch (Exception ex) { problem = ex.GetType().Name + ": " + ex.Message; }

            Check("bad connection: a 4MB piece whose connection breaks every 300KB arrives whole",
                got != null && got.AsSpan().SequenceEqual(data), problem.Length > 0 ? problem : $"{got?.Length ?? 0:N0} bytes");

            List<(long From, long To)> asked;
            lock (handler.Asked) asked = handler.Asked.ToList();
            bool forward = asked.Count > 1 && asked.Zip(asked.Skip(1)).All(p => p.Second.From > p.First.From);
            Check("and each request starts where the last one broke, not at the start of the piece",
                forward, string.Join(", ", asked.Select(a => a.From.ToString("N0"))));
            long fetched = asked.Sum(a => Math.Min(a.To - a.From + 1, 300 * 1024));
            Check("so nothing is fetched twice", fetched <= data.Length,
                $"{fetched:N0} bytes asked for and received, of {data.Length:N0}");
        }

        private static async Task DropsWithoutLengthStillResume()
        {
            // Same, with no Content-Length on the responses (chunked bodies).
            var data = Noise(1024 * 1024, 9);
            var handler = new Breaking(data, 200 * 1024, withLength: false);
            var client = new DriveClient(handler, 0.001);

            byte[]? got = null;
            string problem = "";
            try { got = await client.ReadRange("file", 0, data.Length, CancellationToken.None); }
            catch (Exception ex) { problem = ex.GetType().Name + ": " + ex.Message; }
            Check("bad connection: with no length promised, a broken body is still resumed and arrives whole",
                got != null && got.AsSpan().SequenceEqual(data), problem.Length > 0 ? problem : $"{got?.Length ?? 0:N0} bytes");
        }

        private static void SettingsAreRead()
        {
            var o = BadNet.Parse("rate=200, drop=3000000, stall=0.5");
            Check("bad internet simulator: its settings are read",
                o != null && o.RateKilobytes == 200 && o.DropBytes == 3_000_000 && Math.Abs(o.Stall - 0.5) < 1e-9,
                o?.ToString() ?? "null");
            Check("and it is off with nothing set", BadNet.Parse(null) == null && BadNet.Parse("") == null);
            Check("and in the app it is off unless the variable is set",
                Environment.GetEnvironmentVariable("EXPLORERNATIVE_BADNET") != null || BadNet.Active == null);
        }

        private static void WindowStartsAgainWithTheFile()
        {
            const long mb = 1024 * 1024;
            using var watch = DriveMount.FetchWatch.Detached("");
            for (long at = 0; at < 72 * mb; at += 4 * mb) watch.Note(@"nvda.7z", at, at + 4 * mb, 72 * mb);
            Equal("copy window: the whole file served counts as all of it", (72 * mb).ToString(), watch.BytesServed.ToString());

            // robocopy, after a failed read, starts the file again from byte zero.
            watch.Note(@"nvda.7z", 0, 4 * mb, 72 * mb);
            Check("copy window: a file read again from the start after a failure counts again from there",
                watch.BytesServed == 4 * mb && watch.Restarts == 1, $"{watch.BytesServed:N0} served, {watch.Restarts} restarts");

            // The head read twice at the very start (a program opening the file) is not a restart.
            using var early = DriveMount.FetchWatch.Detached("");
            early.Note(@"song.flac", 0, 64 * 1024, 10 * mb);
            early.Note(@"song.flac", 0, 4 * mb, 10 * mb);
            Check("and the first bytes read twice at the start is not taken for one",
                early.Restarts == 0 && early.BytesServed == 4 * mb, $"{early.Restarts} restarts, {early.BytesServed:N0}");
        }
    }
}
