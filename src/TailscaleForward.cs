using System;
using System.Globalization;
using System.Text.Json;

namespace ExplorerNative
{
    /// <summary>
    /// A file server's port, reachable from the person's Tailscale network
    /// with nothing to set up (7.0.1, Conner: "it should work on tailscale by
    /// default on its own"). Tailscale itself takes connections to this PC's
    /// Tailscale address on that port and hands them to the server on
    /// 127.0.0.1 (tailscale serve --tcp), so Windows Firewall is not in the
    /// way and no administrator is asked.
    ///
    /// Only ever our own forward: a port Tailscale already serves some other
    /// way (the web app's 443, anything the person set up) is left exactly as
    /// it is, and only a forward pointing at this PC's own port of the same
    /// number is ever taken away. Never Funnel: nothing here is reachable from
    /// the internet.
    ///
    /// Every call runs the tailscale program and can take a second: workers only.
    /// </summary>
    internal static class TailscaleForward
    {
        private static string Ours(int port) => "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Pure: what <c>tailscale serve status --json</c> says a port does.
        /// Null: nothing. Otherwise where raw TCP is forwarded to, or "other"
        /// for anything else (a web page, TLS).
        /// </summary>
        internal static string? ForwardOf(string json, int port)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
                if (!document.RootElement.TryGetProperty("TCP", out var tcp) || tcp.ValueKind != JsonValueKind.Object) return null;
                if (!tcp.TryGetProperty(port.ToString(CultureInfo.InvariantCulture), out var entry)) return null;
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("TCPForward", out var to) &&
                    to.ValueKind == JsonValueKind.String && to.GetString() is { Length: > 0 } target)
                    return target;
                return "other";
            }
            catch (JsonException) { return null; }
        }

        /// <summary>Pure: whether a port's forward is the one this makes.</summary>
        internal static bool IsOurs(string json, int port) => ForwardOf(json, port) == Ours(port);

        private static string? Status(string exe)
        {
            var (code, output) = TailscaleWeb.Run(exe, "serve status --json", TimeSpan.FromSeconds(6));
            return code == 0 ? output : null;
        }

        /// <summary>
        /// Forwards the port. True: reachable over Tailscale now. False:
        /// Tailscale is there but would not (signed out, stopped, or the port
        /// is already used for something else there). Null: no Tailscale.
        /// </summary>
        public static bool? Start(int port)
        {
            var exe = TailscaleWeb.FindExe();
            if (exe == null) return null;
            var status = Status(exe);
            if (status == null) return false;
            var now = ForwardOf(status, port);
            if (now == Ours(port)) return true;
            if (now != null) return false;      // somebody else's: left alone
            var p = port.ToString(CultureInfo.InvariantCulture);
            var (code, _) = TailscaleWeb.Run(exe, $"serve --bg --tcp {p} tcp://127.0.0.1:{p}", TimeSpan.FromSeconds(12));
            return code == 0;
        }

        /// <summary>Takes the forward away, if it is ours.</summary>
        public static void Stop(int port)
        {
            var exe = TailscaleWeb.FindExe();
            if (exe == null) return;
            var status = Status(exe);
            if (status == null || !IsOurs(status, port)) return;
            TailscaleWeb.Run(exe, $"serve --tcp={port.ToString(CultureInfo.InvariantCulture)} off", TimeSpan.FromSeconds(10));
        }

        /// <summary>This PC's name on the Tailscale network ("pc.tailnet.ts.net"), or null.</summary>
        public static string? Name()
        {
            try { return TailscaleWeb.Read().DnsName?.TrimEnd('.'); }
            catch { return null; }
        }
    }
}
