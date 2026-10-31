using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// A bad internet connection, for testing only. Off unless
    /// EXPLORERNATIVE_BADNET is set, for example "rate=200,drop=3000000,stall=0.5":
    /// every connection to Google shares rate KB a second, and each one carries
    /// a random amount around drop bytes before it breaks, either cut (a reset)
    /// or, stall of the time, gone silent with no data and no error. That is what
    /// a weak Wi-Fi or a phone hotspot does, and what copies out of Drive must
    /// survive.
    /// </summary>
    internal static class BadNet
    {
        internal sealed record Options(int RateKilobytes, long DropBytes, double Stall);

        private static readonly string? Setting = Environment.GetEnvironmentVariable("EXPLORERNATIVE_BADNET");

        /// <summary>
        /// "file:path" reads the settings from that file, again every second, so a
        /// test can let Drive connect at full speed and then spoil the line.
        /// An empty or missing file is a good connection.
        /// </summary>
        private static readonly string? SettingFile =
            Setting != null && Setting.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? Setting[5..] : null;

        private static Options? _fromFile;
        private static long _fromFileAt;

        internal static Options? Active
        {
            get
            {
                if (SettingFile == null) return Fixed;
                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _fromFileAt) > 1000)
                {
                    try
                    {
                        var read = File.Exists(SettingFile) ? Parse(File.ReadAllText(SettingFile)) : null;
                        if (read != _fromFile) _fromFile = read;   // same settings, same object
                    }
                    catch { }
                    Interlocked.Exchange(ref _fromFileAt, now);
                }
                return _fromFile;
            }
        }

        private static readonly Options? Fixed = SettingFile == null ? Parse(Setting) : null;

        internal static Options? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            int rate = 0; long drop = 0; double stall = 0;
            foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kv.Length != 2) continue;
                switch (kv[0].ToLowerInvariant())
                {
                    case "rate": int.TryParse(kv[1], out rate); break;
                    case "drop": long.TryParse(kv[1], out drop); break;
                    case "stall": double.TryParse(kv[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out stall); break;
                }
            }
            return rate <= 0 && drop <= 0 ? null : new Options(rate, drop, Math.Clamp(stall, 0, 1));
        }

        internal static Stream Wrap(Stream inner) =>
            Setting == null ? inner : SettingFile != null ? new Link(inner, null) : Active == null ? inner : new Link(inner, Active);

        // One shared allowance for every connection, like one slow line.
        private static readonly object Gate = new();
        private static double _allowance;
        private static long _lastTick = Stopwatch.GetTimestamp();

        private static async Task Take(int bytes, int rateKb, CancellationToken token)
        {
            if (rateKb <= 0) return;
            double rate = rateKb * 1024.0;
            while (true)
            {
                double wait;
                lock (Gate)
                {
                    long now = Stopwatch.GetTimestamp();
                    _allowance = Math.Min(rate, _allowance + (now - _lastTick) * rate / Stopwatch.Frequency);
                    _lastTick = now;
                    if (_allowance >= bytes) { _allowance -= bytes; return; }
                    wait = (bytes - _allowance) / rate;
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.005, wait)), token).ConfigureAwait(false);
            }
        }

        private sealed class Link : Stream
        {
            private readonly Stream _inner;
            private readonly Options? _fixed;
            private Options? _options;
            private long _breakAfter = long.MaxValue;
            private bool _stalls;
            private readonly CancellationTokenSource _closed = new();
            private long _carried;

            /// <param name="options">Null: the settings file decides, read by read.</param>
            public Link(Stream inner, Options? options)
            {
                _inner = inner;
                _fixed = options;
            }

            /// <summary>The settings now; a connection takes its breaking point from the first it sees.</summary>
            private Options? Current()
            {
                var o = _fixed ?? Active;
                if (o != null && !ReferenceEquals(o, _options))
                {
                    _options = o;
                    // Exponential around the mean, so some connections die at once and some go far.
                    _breakAfter = o.DropBytes <= 0 ? long.MaxValue
                        : _carried + (long)(-Math.Log(1 - Random.Shared.NextDouble()) * o.DropBytes) + 16 * 1024;
                    _stalls = Random.Shared.NextDouble() < o.Stall;
                }
                return o;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                // A zero-byte read is the TLS layer waiting for data to arrive.
                if (buffer.Length == 0) return await _inner.ReadAsync(buffer, token).ConfigureAwait(false);
                var options = Current();
                if (options == null) return await _inner.ReadAsync(buffer, token).ConfigureAwait(false);
                using var both = CancellationTokenSource.CreateLinkedTokenSource(token, _closed.Token);
                if (_carried >= _breakAfter)
                {
                    if (_stalls) { await Task.Delay(Timeout.Infinite, both.Token).ConfigureAwait(false); }
                    throw new IOException("BadNet: the connection was reset");
                }
                // Never more in one read than a second's allowance, or a rate under
                // 16KB a second could never pay for a read and the line went dead.
                int most = options.RateKilobytes > 0 ? Math.Min(16 * 1024, options.RateKilobytes * 1024) : 16 * 1024;
                int ask = (int)Math.Min(Math.Min(buffer.Length, most), _breakAfter - _carried);
                int n = await _inner.ReadAsync(buffer[..ask], both.Token).ConfigureAwait(false);
                if (n > 0)
                {
                    await Take(n, options.RateKilobytes, both.Token).ConfigureAwait(false);
                    _carried += n;
                }
                return n;
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
                _inner.WriteAsync(buffer, token);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                _inner.WriteAsync(buffer, offset, count, token);
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
            public override Task FlushAsync(CancellationToken token) => _inner.FlushAsync(token);
            public override void Flush() => _inner.Flush();

            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _closed.Cancel(); } catch { }
                    _inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
