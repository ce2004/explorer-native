using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Hands a folder from a newly launched copy to the one already running.
    ///
    /// This matters far more once the app is the default handler for folders:
    /// from then on every double-click on a folder anywhere in Windows starts
    /// this executable with a path. Without a handoff each of those either opens
    /// a second window or, worse, starts, finds the single-instance lock taken,
    /// and exits — so the folder the user asked for simply never appears.
    ///
    /// A named pipe rather than a window message, because the running instance
    /// may have no window at all: closing to the tray hides it, and starting
    /// minimised never creates one. FindWindow has nothing to find in either
    /// case, whereas the pipe is up for the whole life of the process.
    /// </summary>
    internal static class SingleInstance
    {
        /// <summary>
        /// Appended to the pipe name so the self-test can exercise the handoff
        /// without talking to the copy the user has running. Left empty in the
        /// application; a shared name would mean the suite's "nobody is
        /// listening" case silently reached the real app instead.
        /// </summary>
        public static string NameSuffix { get; set; } = "";

        /// <summary>
        /// Per-user: two people signed in at once must not cross wires.
        /// Internal rather than private so the suite can impersonate a client —
        /// the one that connects and says nothing has to be testable.
        ///
        /// And per session. The single-instance mutex is a session-local name, so
        /// the same person signed in twice — a console and a remote desktop —
        /// runs a copy in each; a pipe name shared between them let only one of
        /// those listen, and sent the other session's folders to a window on a
        /// screen nobody in that session could see.
        /// </summary>
        internal static string PipeName =>
            "ExplorerNative_ipc_" + Environment.UserName.ToLowerInvariant() + SessionPart + NameSuffix;

        private static readonly string SessionPart = CurrentSession();

        private static string CurrentSession()
        {
            try
            {
                int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                // Session one is the ordinary console on a single-user machine;
                // it keeps the name scripts and the notes already use.
                return session <= 1 ? "" : "_s" + session;
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Only this user may serve or reach the pipe. Without it anyone on the
        /// machine could create the name first — the listener then never starts,
        /// and every folder this user opens is handed to a stranger's process.
        /// </summary>
        private const PipeOptions Restricted = PipeOptions.CurrentUserOnly;

        private const int ConnectTimeoutMs = 2500;

        /// <summary>Sent when a second copy is launched with no folder — just show the window.</summary>
        public const string ShowCommand = "<show>";

        /// <summary>
        /// Sent after a --register-default or --unregister-default run has
        /// changed settings.json behind a live instance's back.
        ///
        /// Without it the running copy still holds the settings it loaded at
        /// startup, writes them out on exit, and silently undoes the change — and
        /// worse, reconciles the registry against them on the next launch, so the
        /// takeover the user had just asked for removed itself.
        /// </summary>
        public const string ReloadSettingsCommand = "<reload-settings>";

        /// <summary>
        /// Asks a running instance to shut down.
        ///
        /// Needed by --install: the installed copy holds its own files open, so
        /// they cannot be replaced while it runs. Asking it to leave is far
        /// better than refusing the install or killing the process from under it,
        /// since it saves its state on the way out.
        /// </summary>
        public const string ExitCommand = "<exit>";

        /// <summary>
        /// The recovery flags' changes, carried in the request rather than read
        /// back out of settings.json: a file that cannot be read left the running
        /// copy with its own values, and its next reconcile took folders straight
        /// back. <c>ReleaseFolders</c> is --unregister-default; <c>ReleaseAll</c>
        /// also gives up the context menu and the startup entry.
        /// </summary>
        public const string ReleaseFoldersCommand = "<release-folders>";
        public const string ReleaseAllCommand = "<release-all>";

        private static CancellationTokenSource? _serverCts;

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PeekNamedPipe(
            Microsoft.Win32.SafeHandles.SafePipeHandle pipe, IntPtr buffer, uint size,
            IntPtr bytesRead, out uint totalAvailable, IntPtr bytesLeftThisMessage);

        private const int ASFW_ANY = -1;

        // ---------- Server side (the instance that owns the lock) ----------

        /// <summary>
        /// Starts listening. <paramref name="onRequest"/> is invoked on a
        /// background thread, so the handler is responsible for marshalling to
        /// the UI.
        /// </summary>
        public static void StartServer(Action<string> onRequest)
        {
            _serverCts?.Cancel();
            _serverCts = new CancellationTokenSource();
            var token = _serverCts.Token;

            // Fixed for this listener's life: the name can change under it (the
            // suite changes it), and a listener that followed would serve a pipe
            // somebody else owns.
            var pipe = PipeName;
            _serverPipe = pipe;

            var thread = new Thread(() => Listen(onRequest, pipe, token))
            {
                IsBackground = true,
                Name = "ExplorerNative single-instance listener",
            };
            _serverThread = thread;
            thread.Start();
        }

        private static Thread? _serverThread;
        private static string? _serverPipe;

        /// <summary>
        /// Waits for the listener to finish with a client it had already
        /// accepted when it was stopped — up to the read deadline and a little —
        /// so what that client asked for is handed on before quitting takes
        /// stock of what is waiting.
        /// </summary>
        public static void WaitForServer()
        {
            try { _serverThread?.Join(ReadTimeoutMs + 1000); } catch { }
        }

        public static void StopServer()
        {
            try { _serverCts?.Cancel(); } catch { }

            // The listener waits for a connection without the token, so a
            // throwaway connection is what ends the wait. Tried again while the
            // listener is still there: between two connections there is a moment
            // with no pipe to connect to.
            var listener = _serverThread;
            for (int attempt = 0; attempt < 5 && listener is { IsAlive: true }; attempt++)
            {
                try
                {
                    using var poke = new NamedPipeClientStream(".", _serverPipe ?? PipeName, PipeDirection.Out, Restricted);
                    poke.Connect(200);
                    break;
                }
                catch { }
            }
        }

        private static void Listen(Action<string> onRequest, string pipe, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Rebuilt per connection. A single reused instance would have
                    // to be disconnected and re-armed between clients anyway, and
                    // getting that wrong wedges the pipe for the whole session.
                    //
                    // Synchronous, on this thread of its own. An asynchronous pipe
                    // completes on the thread pool, so a busy pool starved the
                    // handoff: twenty launches at once from a process whose pool
                    // was full lost half of them to the connect timeout, every
                    // one a folder double-clicked that opened nothing.
                    using var server = new NamedPipeServerStream(
                        pipe, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, Restricted);

                    // Not cancelled by the token. A cancelled wait leaves the pipe
                    // listening until it is disposed, and a launcher that connected
                    // in that moment took its folder as delivered while nothing
                    // read it. StopServer connects instead, and whoever connected
                    // is read before the token is looked at.
                    server.WaitForConnection();

                    // A client that connected is read and handed on even when
                    // stopping has begun: its launcher takes the connection as
                    // the folder taken and exits, and the application routes a
                    // folder that arrives while quitting to the copy after it.
                    // The read keeps its own deadline, not the server's token.
                    var line = ReadRequest(server);

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        try { onRequest(line.Trim()); }
                        catch { /* a bad request must not stop us listening */ }
                    }

                    if (token.IsCancellationRequested) return;
                }
                catch (OperationCanceledException) { return; }
                catch
                {
                    if (token.IsCancellationRequested) return;
                    // Something transient — a client that died mid-write, a pipe
                    // in a bad state. Pause briefly rather than spin the CPU
                    // rebuilding a pipe that is going to fail again immediately.
                    try { Task.Delay(250, token).GetAwaiter().GetResult(); }
                    catch { return; }
                }
            }
        }

        /// <summary>
        /// How long a connected client has to actually say something.
        ///
        /// Must stay below <see cref="ConnectTimeoutMs"/>, and that is the whole
        /// design. Only one server instance exists at a time, so while a silent
        /// client is being waited on nobody else can connect — a real launch
        /// arriving in that window sits in Connect. Keeping this the shorter of
        /// the two means the stall clears first and the waiting launch still gets
        /// in, so one misbehaving client costs a delay rather than a folder that
        /// never opens. Raise this above the connect timeout and that stops being
        /// true.
        /// </summary>
        private const int ReadTimeoutMs = 2000;

        /// <summary>A request longer than this is not a path, it is a client misbehaving.</summary>
        private const int MaxRequestBytes = 64 * 1024;

        /// <summary>
        /// One line from a connected client, or null.
        ///
        /// A blocking StreamReader.ReadLine here was a single point of failure
        /// for the entire handoff. One client that connects and then never
        /// writes — killed between the two calls, or simply not us — parks this
        /// thread for ever, and from that moment every folder double-click on the
        /// machine opens nothing, because the listener that would have answered
        /// it is still waiting on the last one. Nothing recovers that except
        /// restarting the application, and nothing reports it either.
        ///
        /// The deadline comes from asking the pipe what it holds
        /// (<c>PeekNamedPipe</c>) and reading only that much, so no read ever
        /// blocks and nothing waits on the thread pool. The bytes are decoded
        /// once at the end rather than per chunk: a path can carry characters
        /// whose UTF-8 runs across a read boundary, and decoding each chunk on
        /// its own splits them into replacement characters — a folder that
        /// opens for most people and not for the one with an accent in their
        /// user name.
        /// </summary>
        private static string? ReadRequest(NamedPipeServerStream server)
        {
            long deadline = Environment.TickCount64 + ReadTimeoutMs;

            using var collected = new MemoryStream();
            var buffer = new byte[512];
            bool complete = false;

            try
            {
                while (!complete && collected.Length < MaxRequestBytes)
                {
                    // False once the client has gone and everything it wrote
                    // has been read.
                    if (!PeekNamedPipe(server.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero,
                            out uint available, IntPtr.Zero))
                        break;

                    if (available == 0)
                    {
                        if (Environment.TickCount64 >= deadline) return null;
                        Thread.Sleep(2);
                        continue;
                    }

                    int n = server.Read(buffer, 0, (int)Math.Min(available, (uint)buffer.Length));
                    if (n <= 0) break;

                    for (int i = 0; i < n; i++)
                    {
                        if (buffer[i] == (byte)'\n') { complete = true; break; }
                        if (buffer[i] != (byte)'\r') collected.WriteByte(buffer[i]);
                    }
                }
            }
            catch
            {
                // A client that died mid-write, or one that said nothing at all.
                // Either way the next connection is what matters.
                return null;
            }

            return collected.Length == 0 ? null : Encoding.UTF8.GetString(collected.ToArray());
        }

        // ---------- Client side (the copy that has to step aside) ----------

        /// <summary>
        /// Passes the request to the running instance. Returns false if nobody is
        /// listening, in which case the caller should carry on and start normally.
        /// </summary>
        public static bool SendToRunningInstance(string request)
        {
            // Only the name of this session. The name without a session part —
            // what a copy from before sessions were in the name listened on — is
            // also session 1's name now, so from any other session it reached
            // another session's copy.
            return Send(PipeName, request, ConnectTimeoutMs);
        }

        private static bool Send(string pipe, string request, int timeoutMs)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, Restricted);
                client.Connect(timeoutMs);

                // We were started by the user's own click, so we hold the right to
                // set the foreground window and the running instance does not.
                // Handing that right over is the only way its window can actually
                // come to the front instead of just flashing in the taskbar.
                try { AllowSetForegroundWindow(ASFW_ANY); } catch { }

                using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine(request);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// What a launch's arguments mean, kept apart from Program so the suite
    /// can check it: Program owns Main and cannot be compiled into it.
    /// </summary>
    internal static class LaunchArguments
    {
        /// <summary>
        /// The switches that do their work and exit without a window — the only
        /// runs that are allowed to borrow a console.
        /// </summary>
        public static readonly string[] CommandLineSwitches =
        {
            "--licence", "--license", "--install", "--install-only", "--update", "--uninstall", "--read-log",
            "--register-default", "--unregister-default", "--unregister-all",
        };

        /// <summary>
        /// Switches that change how an ordinary launch behaves: --quiet silences
        /// a command-line run's notice, --restart is a copy taking over from one
        /// that is leaving.
        /// </summary>
        public static readonly string[] Modifiers = { "--quiet", "--restart" };

        public static bool IsKnown(string arg) =>
            CommandLineSwitches.Contains(arg.Trim(), StringComparer.OrdinalIgnoreCase) ||
            Modifiers.Contains(arg.Trim(), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The first argument that looks like a switch and is not one, or null.
        /// A typo such as --unistall used to fall through to an ordinary launch,
        /// which brought the running copy to the front or started a new one.
        /// </summary>
        public static string? UnknownOption(IEnumerable<string> args) =>
            args.Select(a => a.Trim())
                .FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal) && !IsKnown(a));

        /// <summary>
        /// The log --read-log was asked for: the first argument after it that is
        /// not a switch, so "--read-log --quiet" reads install.log rather than a
        /// file called "--quiet".
        /// </summary>
        public static string ReadLogName(IEnumerable<string> args) =>
            args.SkipWhile(a => !a.Trim().Equals("--read-log", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault(a => !a.Trim().StartsWith("--", StringComparison.Ordinal) && a.Trim().Length > 0)
            ?? "install.log";
    }
}
