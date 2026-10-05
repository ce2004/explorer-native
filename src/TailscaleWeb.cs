using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The web app's way onto the phone: Tailscale on this PC, and Tailscale Serve
    /// putting the Connect server on https://&lt;this pc&gt;.&lt;tailnet&gt;.ts.net/ for the
    /// tailnet only. Never Funnel; nothing is reachable off the tailnet.
    ///
    /// Everything here runs tailscale.exe with no window, nothing inherited and a
    /// hard timeout, off the UI thread.
    /// </summary>
    internal static class TailscaleWeb
    {
        /// <summary>What the web app's Serve handler points at.</summary>
        public static string Target => $"http://127.0.0.1:{ConnectServer.Port}";

        public enum State { NotInstalled, NotRunning, NeedsLogin, Running }

        /// <summary>What <c>tailscale status --json</c> says, reduced to what the web app needs.</summary>
        public sealed record Status(
            State State,
            string? Login,
            string? DnsName,
            string? HostName,
            bool HttpsEnabled);

        /// <summary>Whether our handler is on 443, or something else has it.</summary>
        public enum ServeState { Off, Ours, TakenByOther }

        /// <summary>The result of turning Serve on.</summary>
        public sealed record EnableResult(bool Ok, string? Problem, string? EnableHttpsUrl);

        public static string? FindExe()
        {
            var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
            if (File.Exists(pf)) return pf;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "tailscale.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        public static bool Installed => FindExe() != null;

        /// <summary>The PC's Tailscale state now. Never throws.</summary>
        public static Status Read()
        {
            var exe = FindExe();
            if (exe == null) return new Status(State.NotInstalled, null, null, null, false);
            var (code, output) = Run(exe, "status --json", TimeSpan.FromSeconds(6));
            if (output.Length == 0 || !output.TrimStart().StartsWith('{'))
                return new Status(State.NotRunning, null, null, null, false);
            try { return ParseStatus(output); }
            catch { return new Status(State.NotRunning, null, null, null, false); }
        }

        /// <summary>Pure: <c>tailscale status --json</c> to a <see cref="Status"/>.</summary>
        public static Status ParseStatus(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string backend = root.TryGetProperty("BackendState", out var b) ? b.GetString() ?? "" : "";

            string? login = null, dns = null, host = null;
            if (root.TryGetProperty("Self", out var self) && self.ValueKind == JsonValueKind.Object)
            {
                dns = self.TryGetProperty("DNSName", out var d) ? (d.GetString() ?? "").TrimEnd('.') : null;
                host = self.TryGetProperty("HostName", out var h) ? h.GetString() : null;
                if (self.TryGetProperty("UserID", out var uid) &&
                    root.TryGetProperty("User", out var users) && users.ValueKind == JsonValueKind.Object)
                {
                    var key = uid.ValueKind == JsonValueKind.Number ? uid.GetRawText() : uid.GetString() ?? "";
                    if (users.TryGetProperty(key, out var user) && user.TryGetProperty("LoginName", out var ln))
                        login = ln.GetString();
                }
            }

            bool https = root.TryGetProperty("CertDomains", out var cd) &&
                         cd.ValueKind == JsonValueKind.Array && cd.GetArrayLength() > 0;

            var state = backend switch
            {
                "Running" => State.Running,
                "NeedsLogin" or "NeedsMachineAuth" or "NoState" => State.NeedsLogin,
                _ => State.NotRunning,
            };
            if (string.IsNullOrEmpty(dns)) dns = null;
            return new Status(state, login, dns, host, https);
        }

        /// <summary>Pure: what <c>tailscale serve status --json</c> says about port 443.</summary>
        public static ServeState ParseServe(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return ServeState.Off;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ServeState.Off;

            bool tcp443 = root.TryGetProperty("TCP", out var tcp) && tcp.ValueKind == JsonValueKind.Object &&
                          tcp.TryGetProperty("443", out _);

            bool anyWeb443 = false, ours = false, other = false;
            if (root.TryGetProperty("Web", out var web) && web.ValueKind == JsonValueKind.Object)
            {
                foreach (var site in web.EnumerateObject())
                {
                    if (!site.Name.EndsWith(":443", StringComparison.Ordinal)) continue;
                    anyWeb443 = true;
                    if (!site.Value.TryGetProperty("Handlers", out var handlers) || handlers.ValueKind != JsonValueKind.Object)
                        continue;
                    foreach (var handler in handlers.EnumerateObject())
                    {
                        string proxy = handler.Value.TryGetProperty("Proxy", out var p) ? p.GetString() ?? "" : "";
                        if (handler.Name == "/" && IsOurTarget(proxy)) ours = true;
                        else other = true;
                    }
                }
            }

            if (other) return ServeState.TakenByOther;
            if (ours) return ServeState.Ours;
            if (tcp443 || anyWeb443) return ServeState.TakenByOther;
            return ServeState.Off;
        }

        private static bool IsOurTarget(string proxy)
        {
            proxy = proxy.TrimEnd('/');
            return proxy.Equals(Target, StringComparison.OrdinalIgnoreCase) ||
                   proxy.Equals($"http://localhost:{ConnectServer.Port}", StringComparison.OrdinalIgnoreCase) ||
                   proxy.Equals($"127.0.0.1:{ConnectServer.Port}", StringComparison.OrdinalIgnoreCase);
        }

        public static ServeState ReadServe()
        {
            var exe = FindExe();
            if (exe == null) return ServeState.Off;
            var (_, output) = Run(exe, "serve status --json", TimeSpan.FromSeconds(6));
            try { return ParseServe(output.Trim()); }
            catch { return ServeState.Off; }
        }

        /// <summary>The admin link Tailscale prints when HTTPS (or Serve) is not enabled for the tailnet.</summary>
        public static string? EnableLink(string output)
        {
            var m = Regex.Match(output, @"https://login\.tailscale\.com/f/[A-Za-z0-9/_\-\.\?=&%]+");
            return m.Success ? m.Value : null;
        }

        /// <summary>
        /// Puts the Connect server on https://&lt;pc&gt;.&lt;tailnet&gt;.ts.net/. Leaves anybody
        /// else's Serve config alone: if 443 is already used by something else it
        /// says so and changes nothing.
        /// </summary>
        public static EnableResult Enable()
        {
            var exe = FindExe();
            if (exe == null) return new EnableResult(false, "Tailscale is not installed.", null);

            switch (ReadServe())
            {
                case ServeState.Ours: return new EnableResult(true, null, null);
                case ServeState.TakenByOther:
                    return new EnableResult(false,
                        "Something else on this PC already uses Tailscale Serve on port 443, so the web app was not turned on. " +
                        "Turn that off first, or ask for help.", null);
            }

            // When HTTPS or Serve is off for the tailnet, this prints a link and then
            // waits for it to be enabled. The link is all that is wanted; the wait is cut short.
            var (code, output) = Run(exe, $"serve --bg --https=443 {Target}", TimeSpan.FromSeconds(12));
            var link = EnableLink(output);
            if (ReadServe() == ServeState.Ours) return new EnableResult(true, null, null);
            if (link != null)
                return new EnableResult(false,
                    "HTTPS needs to be switched on for your Tailscale network, once. Press Enable HTTPS in Tailscale, " +
                    "turn it on in the page that opens, and the web app starts by itself.", link);
            var first = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            return new EnableResult(false, "Tailscale could not turn the web app on" + (first != null ? ": " + first : "."), null);
        }

        /// <summary>Removes only our handler; anything else in Serve is left as it was.</summary>
        public static void Disable()
        {
            var exe = FindExe();
            if (exe == null) return;
            if (ReadServe() != ServeState.Ours) return;
            Run(exe, "serve --https=443 off", TimeSpan.FromSeconds(10));
        }

        /// <summary>https://name.tailnet.ts.net/, or null before Tailscale is signed in.</summary>
        public static string? Link(Status s) => s.DnsName == null ? null : $"https://{s.DnsName}/";

        /// <summary>Installs Tailscale with winget when Windows has it, otherwise opens the download page.</summary>
        public static void Install()
        {
            var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");
            try
            {
                if (File.Exists(winget))
                {
                    Process.Start(new ProcessStartInfo(winget,
                        "install --id Tailscale.Tailscale -e --accept-package-agreements --accept-source-agreements")
                    { UseShellExecute = true });
                    return;
                }
            }
            catch { }
            Open("https://tailscale.com/download/windows");
        }

        /// <summary>
        /// Signs this PC in: starts the Tailscale app if it is not running, then
        /// asks tailscale up for its sign-in link and opens it in the browser.
        /// </summary>
        public static void SignIn()
        {
            var exe = FindExe();
            if (exe == null) return;
            try
            {
                var gui = Path.Combine(Path.GetDirectoryName(exe)!, "tailscale-ipn.exe");
                if (File.Exists(gui) && Process.GetProcessesByName("tailscale-ipn").Length == 0)
                    Process.Start(new ProcessStartInfo(gui) { UseShellExecute = true });
            }
            catch { }

            _ = Task.Run(() =>
            {
                // "tailscale up" prints the login link and waits for the sign-in to finish.
                var (_, output) = Run(exe, "up", TimeSpan.FromSeconds(10));
                var m = Regex.Match(output, @"https://login\.tailscale\.com/a/[A-Za-z0-9]+");
                if (m.Success) Open(m.Value);
            });
        }

        public static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        /// <summary>
        /// Runs tailscale.exe and returns what it printed (stdout then stderr). No
        /// window, no inherited handles; killed at <paramref name="limit"/>.
        /// </summary>
        internal static (int Code, string Output) Run(string exe, string args, TimeSpan limit)
        {
            try
            {
                var start = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                };
                using var process = Process.Start(start);
                if (process == null) return (-1, "");
                process.StandardInput.Close();
                var output = new StringBuilder();
                var gate = new object();
                process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (gate) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) output.AppendLine(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                bool exited = process.WaitForExit((int)limit.TotalMilliseconds);
                if (!exited) { try { process.Kill(true); } catch { } }
                else process.WaitForExit();
                lock (gate) return (exited ? process.ExitCode : -1, output.ToString());
            }
            catch (Exception e)
            {
                return (-1, e.Message);
            }
        }
    }
}
