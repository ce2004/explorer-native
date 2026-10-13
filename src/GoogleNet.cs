using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The one connection pool for everything that goes online: Google sign-in,
    /// the client check, Drive, and the update check on GitHub.
    ///
    /// It connects the way a browser does, over IPv6 and IPv4 at once, and keeps
    /// whichever answers first. .NET tries the addresses one after another, so
    /// on a network whose IPv6 is broken (common on home routers) every request
    /// sat on a dead IPv6 address until the 15 second timeout, and the app said
    /// Google could not be reached while Chrome on the same PC worked fine.
    /// </summary>
    internal static class GoogleNet
    {
        /// <summary>How long IPv6 gets on its own before IPv4 joins in (RFC 8305 suggests 250ms).</summary>
        private const int SecondFamilyDelayMilliseconds = 250;

        public static readonly SocketsHttpHandler Handler = new()
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectAsync,
        };

        private static async ValueTask<System.IO.Stream> ConnectAsync(
            SocketsHttpConnectionContext context, CancellationToken token)
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;
            var addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);

            var v6 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
            var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (v6 == null || v4 == null)
            {
                var only = v4 ?? v6 ?? throw new SocketException((int)SocketError.HostNotFound);
                return await OpenAsync(only, port, token).ConfigureAwait(false);
            }

            using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
            var first = OpenAsync(v6, port, race.Token);
            var second = Task.Delay(SecondFamilyDelayMilliseconds, race.Token)
                .ContinueWith(_ => OpenAsync(v4, port, race.Token), race.Token,
                    TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default)
                .Unwrap();

            Exception? failure = null;
            var pending = new[] { first, second }.ToList();
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                try
                {
                    var stream = await done.ConfigureAwait(false);
                    race.Cancel();
                    // The loser may still connect; close it rather than leak it.
                    foreach (var other in pending)
                        _ = other.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
                    return stream;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    // IPv4's reason over IPv6's: a broken IPv6 says "unreachable"
                    // and would hide what actually stopped the connection.
                    if (failure == null || done == second) failure = ex;
                }
            }

            token.ThrowIfCancellationRequested();
            throw failure ?? new SocketException((int)SocketError.HostUnreachable);
        }

        private static async Task<System.IO.Stream> OpenAsync(IPAddress address, int port, CancellationToken token)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Why a request to Google never got an answer, in words: the innermost
        /// cause rather than "An error occurred while sending the request".
        /// </summary>
        public static string Reason(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                // 10013, WSAEACCES: a firewall or antivirus refused this program
                // a connection. The browser is a different program, so it works.
                if (e is SocketException { SocketErrorCode: SocketError.AccessDenied })
                    return "something on this PC, usually a firewall or antivirus, is stopping Explorer Native " +
                           "from going online. Allow ExplorerNative.exe in it and try again";
                if (e is System.Security.Authentication.AuthenticationException)
                    return "the secure connection could not be set up. Check that the PC's date and time are right, " +
                           "and that no antivirus is scanning secure connections for this program";
            }
            if (ex is TaskCanceledException or TimeoutException) return "no answer in time";

            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            var text = inner.Message.Trim().TrimEnd('.');
            return text.Length > 200 ? text[..200] : text;
        }
    }
}
