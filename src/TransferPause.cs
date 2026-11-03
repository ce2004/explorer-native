using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Pause and resume for one transfer. The engines wait at <see cref="WaitAsync"/>
    /// before new work; the transfer window and the running transfers list
    /// press <see cref="Pause"/> and <see cref="Resume"/>.
    /// </summary>
    public sealed class TransferPause
    {
        private readonly object _gate = new();
        private TaskCompletionSource _open = Opened();
        private bool _paused;

        /// <summary>Raised after every change, with true for paused. Any thread.</summary>
        public event Action<bool>? Changed;

        /// <summary>
        /// The pause of the Drive upload running on this flow, read deep inside
        /// the upload so a piece in flight can stop. Null everywhere else.
        /// </summary>
        internal static readonly AsyncLocal<TransferPause?> Ambient = new();

        private static TaskCompletionSource Opened()
        {
            var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            open.SetResult();
            return open;
        }

        public bool IsPaused { get { lock (_gate) return _paused; } }

        public void Pause()
        {
            lock (_gate)
            {
                if (_paused) return;
                _paused = true;
                _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            Raise(true);
        }

        public void Resume()
        {
            TaskCompletionSource open;
            lock (_gate)
            {
                if (!_paused) return;
                _paused = false;
                open = _open;
            }
            open.TrySetResult();
            Raise(false);
        }

        /// <summary>Pauses a running transfer, or resumes a paused one. Returns true if now paused.</summary>
        public bool Toggle()
        {
            if (IsPaused) { Resume(); return false; }
            Pause();
            return true;
        }

        private void Raise(bool paused)
        {
            var handlers = Changed;
            if (handlers == null) return;
            foreach (Action<bool> one in handlers.GetInvocationList())
            {
                try { one(paused); } catch { }
            }
        }

        /// <summary>Returns at once while running; while paused, waits for Resume or the token.</summary>
        public Task WaitAsync(CancellationToken token)
        {
            Task open;
            lock (_gate)
            {
                if (!_paused) return Task.CompletedTask;
                open = _open.Task;
            }
            return open.WaitAsync(token);
        }

        /// <summary>
        /// A token that is cancelled by the outer one, or by a press of Pause, so
        /// a request in flight can stop where it is. Dispose it once the request ends.
        /// </summary>
        public UntilPaused Until(CancellationToken outer) => new(this, outer);

        public sealed class UntilPaused : IDisposable
        {
            private readonly TransferPause _pause;
            private readonly CancellationTokenSource _cts;
            private readonly Action<bool> _handler;
            private int _pausedIt;

            internal UntilPaused(TransferPause pause, CancellationToken outer)
            {
                _pause = pause;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
                _handler = paused =>
                {
                    if (!paused) return;
                    Interlocked.Exchange(ref _pausedIt, 1);
                    try { _cts.Cancel(); } catch { }
                };
                pause.Changed += _handler;
                if (pause.IsPaused) _handler(true);
            }

            public CancellationToken Token => _cts.Token;

            /// <summary>Whether Pause, not the outer token, stopped it.</summary>
            public bool PausedIt => Volatile.Read(ref _pausedIt) == 1;

            public void Dispose()
            {
                _pause.Changed -= _handler;
                _cts.Dispose();
            }
        }
    }

    /// <summary>
    /// Holds a robocopy process still while its transfer is paused: suspended
    /// where it is, so nothing is read again and nothing half-written is left
    /// for Fill gaps to keep. Robocopy's next job waits at the gate instead.
    /// </summary>
    internal sealed class ProcessPauser : IDisposable
    {
        private readonly TransferPause? _pause;
        private readonly object _gate = new();
        private Process? _process;
        private bool _suspended;

        public ProcessPauser(TransferPause? pause)
        {
            _pause = pause;
            if (pause == null) return;
            pause.Changed += Follow;
            // Never leave a suspended robocopy behind when the app goes.
            AppDomain.CurrentDomain.ProcessExit += Released;
        }

        /// <summary>The process now running, or null once it has ended.</summary>
        public void Set(Process? process)
        {
            if (_pause == null) return;
            lock (_gate)
            {
                if (_process != null && _suspended) Apply(_process, false);
                _suspended = false;
                _process = process;
                Sync();
            }
        }

        private void Follow(bool paused)
        {
            lock (_gate) Sync();
        }

        private void Sync()
        {
            if (_process == null || _pause == null) return;
            bool want = _pause.IsPaused;
            if (want == _suspended) return;
            if (Apply(_process, want)) _suspended = want;
        }

        private void Released(object? sender, EventArgs e) => Set(null);

        public void Dispose()
        {
            if (_pause == null) return;
            _pause.Changed -= Follow;
            AppDomain.CurrentDomain.ProcessExit -= Released;
            Set(null);
        }

        private static bool Apply(Process process, bool suspend)
        {
            try
            {
                if (process.HasExited) return false;
                var handle = process.Handle;
                return (suspend ? NtSuspendProcess(handle) : NtResumeProcess(handle)) >= 0;
            }
            catch { return false; }
        }

        [DllImport("ntdll.dll")]
        private static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        private static extern int NtResumeProcess(IntPtr processHandle);
    }

    /// <summary>The sentences the running transfers list and its key say.</summary>
    internal static class TransferText
    {
        public const string NothingRunning = "Nothing is being copied or moved right now.";

        /// <summary>
        /// One transfer as one line: "Copying 12 files to Music, 40 percent,
        /// about 2 minutes left". The window's title is the start of it.
        /// </summary>
        public static string Line(string title, TransferProgress? p, bool paused)
        {
            var text = WithCount(title, p);
            if (p != null && p.TotalKnown)
            {
                text += $", {p.Percent:0} percent";
                if (paused) text += ", paused";
                else if (p.Remaining is { } left) text += ", " + Left(left);
            }
            else if (paused) text += ", paused";
            else if (p == null) text += ", starting";
            return text;
        }

        /// <summary>"Copying to Music" with the count put in: "Copying 12 files to Music".</summary>
        private static string WithCount(string title, TransferProgress? p)
        {
            if (p == null || p.ItemsTotal <= 0) return title;
            foreach (var verb in new[] { "Copying", "Moving" })
            {
                if (title == verb) return $"{verb} {NameRules.Items(p.ItemsTotal, "file")}";
                if (title.StartsWith(verb + " to ", StringComparison.Ordinal))
                    return $"{verb} {NameRules.Items(p.ItemsTotal, "file")}{title[verb.Length..]}";
            }
            return title;
        }

        /// <summary>Roughly, as somebody would say it: "about 2 minutes left".</summary>
        public static string Left(TimeSpan left)
        {
            if (left.TotalSeconds < 60) return "less than a minute left";
            if (left.TotalMinutes < 60)
                return $"about {NameRules.Items((int)Math.Round(left.TotalMinutes), "minute")} left";
            int hours = (int)left.TotalHours, minutes = left.Minutes;
            return minutes == 0
                ? $"about {NameRules.Items(hours, "hour")} left"
                : $"about {NameRules.Items(hours, "hour")} and {NameRules.Items(minutes, "minute")} left";
        }

        /// <summary>Everything running, in one sentence.</summary>
        public static string Overall(IReadOnlyList<(string Title, TransferProgress? Progress, bool Paused)> running)
        {
            if (running.Count == 0) return NothingRunning;
            if (running.Count == 1)
            {
                var (title, p, paused) = running[0];
                return Line(title, p, paused) + ".";
            }

            long done = 0, total = 0;
            TimeSpan? longest = TimeSpan.Zero;
            int pausedCount = 0;
            foreach (var (_, p, paused) in running)
            {
                if (paused) pausedCount++;
                if (p == null || p.BytesTotal <= 0) { longest = null; continue; }
                done += p.BytesDone;
                total += p.BytesTotal;
                if (paused || p.Remaining is not { } left) longest = null;
                else if (longest != null && left > longest) longest = left;
            }

            var text = $"{running.Count} transfers are running";
            if (total > 0) text += $", {Math.Clamp(done * 100.0 / total, 0, 100):0} percent done in all";
            if (pausedCount == running.Count) text += ", all paused";
            else if (pausedCount > 0) text += $", {pausedCount} paused";
            else if (longest is { } all && all > TimeSpan.Zero) text += ", " + Left(all);
            return text + ".";
        }
    }
}
