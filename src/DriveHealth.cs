using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>Whether Drive is answering.</summary>
    public enum DriveState
    {
        /// <summary>Nothing has been tried yet.</summary>
        Unknown,

        Online,

        /// <summary>
        /// Requests are failing in a way that looks like the network rather than
        /// the account: no route, no DNS, a refused connection, a timeout.
        /// </summary>
        Offline,
    }

    /// <summary>
    /// Watches whether Google Drive is reachable, and says so once when the
    /// answer changes.
    ///
    /// A cloud drive that goes offline is not an error, it is a Tuesday — a lid
    /// closed, a train tunnel, a router rebooting. What matters is that the
    /// application does not pretend otherwise: a folder that will not list and a
    /// track that will not play should say "Drive is offline" rather than
    /// "access denied", and should say it *once* rather than once per file.
    ///
    /// Deliberately not a poller. Asking Google every thirty seconds whether the
    /// internet exists is traffic spent on a question that only matters when
    /// something is actually being read — so the state is inferred from the
    /// requests the application was making anyway, and only a recovery check
    /// costs a request of its own.
    /// </summary>
    public sealed class DriveHealth
    {
        /// <summary>
        /// How many failures in a row before believing it. One timeout is a
        /// hiccup; three in a row while several reads are in flight is a network
        /// that has gone away.
        /// </summary>
        public const int FailuresBeforeOffline = 3;

        private readonly object _gate = new();
        private int _consecutiveFailures;
        private DriveState _state = DriveState.Unknown;

        /// <summary>Raised on a change, never on a repeat.</summary>
        public event Action<DriveState>? Changed;

        public DriveState State { get { lock (_gate) return _state; } }

        public bool IsOffline => State == DriveState.Offline;

        /// <summary>What the last failure actually was, for the status line.</summary>
        public string LastProblem { get; private set; } = "";

        /// <summary>A request came back. Anything at all counts as reachable.</summary>
        public void Succeeded()
        {
            DriveState changed;
            lock (_gate)
            {
                _consecutiveFailures = 0;
                if (_state == DriveState.Online) return;
                _state = DriveState.Online;
                changed = _state;
            }
            Raise(changed);
        }

        /// <summary>
        /// A request threw. Only network-shaped failures count towards going
        /// offline — a 403 or a bad token means Drive answered, and calling that
        /// "offline" would send somebody looking at their router when the problem
        /// is their sign-in.
        /// </summary>
        public void Failed(Exception error)
        {
            if (!LooksLikeNetwork(error)) return;

            DriveState changed;
            lock (_gate)
            {
                LastProblem = Describe(error);
                if (++_consecutiveFailures < FailuresBeforeOffline) return;
                if (_state == DriveState.Offline) return;
                _state = DriveState.Offline;
                changed = _state;
            }
            Raise(changed);
        }

        private void Raise(DriveState state)
        {
            try { Changed?.Invoke(state); } catch { }
        }

        /// <summary>
        /// Whether this is the network rather than the account.
        ///
        /// The distinction matters more than it looks: everything Google returns
        /// for an expired token or a revoked scope arrives as a perfectly healthy
        /// HTTP response, and treating those as "offline" hides the one problem
        /// the person can actually fix.
        /// </summary>
        public static bool LooksLikeNetwork(Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
            {
                // Note what is *not* here: a bare TaskCanceledException.
                //
                // It used to be, on the reasoning that HttpClient reports its own
                // timeout that way — which it does, but wrapped around a
                // TimeoutException, and the loop below finds that on the next
                // step. What the bare one really means is that somebody passed a
                // token and then cancelled it, which is us: disposing a
                // DriveFileCache cancels the two ranged reads its download keeps
                // in flight, so switching tracks twice in quick succession
                // produced four "failures" in a row with no request in between to
                // clear them, and the application announced that Google Drive was
                // offline while it was answering perfectly.
                if (e is TimeoutException or SocketException) return true;
                if (e is HttpRequestException http)
                {
                    // A request that never got a status code never reached
                    // Google. One that did is Google's answer, not the network's.
                    if (http.StatusCode == null) return true;
                }
            }
            return false;
        }

        private static string Describe(Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
            {
                if (e is SocketException socket) return socket.SocketErrorCode.ToString();
                if (e is TaskCanceledException or TimeoutException) return "timed out";
            }
            return error.GetType().Name;
        }

        public string Sentence => State switch
        {
            DriveState.Offline => LastProblem.Length > 0
                ? $"Google Drive is offline ({LastProblem})."
                : "Google Drive is offline.",
            DriveState.Online => "Google Drive is online.",
            _ => "Google Drive has not been reached yet.",
        };
    }
}

