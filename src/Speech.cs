using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// Screen-reader output through the NVDA controller client.
    ///
    /// NVDA first, by design (same rule as the ARP recorder): no SAPI fallback,
    /// because a second synthesiser talking over the top of NVDA is worse than
    /// silence. Only when the NVDA controller is not answering (NVDA not
    /// running or restarting, or the client missing) is a message raised as a
    /// UI Automation notification instead, which JAWS, Narrator and other
    /// screen readers read in their own voice (<see cref="ChooseRoute"/>).
    ///
    /// Everything is dispatched to a background thread. Both entry points into
    /// the controller client are synchronous RPC calls into NVDA's process: on a
    /// busy or wedged NVDA they block for as long as NVDA takes to answer, and
    /// Speak is called from selection changes, i.e. on every arrow press. Doing
    /// that inline froze the list. The UI now only ever enqueues.
    /// </summary>
    internal static unsafe class Speech
    {
        private static bool _init;
        private static delegate* unmanaged[Stdcall]<char*, int> _speakText;
        private static delegate* unmanaged[Stdcall]<int> _testIfRunning;
        private static delegate* unmanaged[Stdcall]<int> _cancelSpeech;

        public static string LoadDiagnostic { get; private set; } = "not initialised";

        // ---- Dispatch queue ----

        private readonly record struct Utterance(string Message, bool Interrupt);

        private static readonly Queue<Utterance> Pending = new();
        private static readonly object Gate = new();
        private static Thread? _worker;
        private static bool _shuttingDown;

        /// <summary>
        /// A backlog longer than this can only ever be stale — the user has moved
        /// on several times over. Oldest entries are dropped, not newest, so what
        /// survives is what they are actually looking at now.
        /// </summary>
        private const int MaxQueued = 16;

        public static void Init()
        {
            if (_init) return;
            _init = true;
            LoadNvda();
            StartWorker();
        }

        private static void StartWorker()
        {
            // Started even without the NVDA client: UI Automation notifications
            // are the second way out (see ChooseRoute).
            _worker = new Thread(PumpForever)
            {
                IsBackground = true,
                Name = "ExplorerNative speech",
                Priority = ThreadPriority.BelowNormal,
            };
            _worker.Start();
        }

        /// <summary>Stops the pump. Safe to call more than once.</summary>
        public static void Shutdown()
        {
            lock (Gate)
            {
                _shuttingDown = true;
                Pending.Clear();
                Monitor.PulseAll(Gate);
            }
        }

        /// <summary>An utterance taken off the queue and not yet handed to NVDA.</summary>
        private static bool _handing;

        /// <summary>
        /// Waits, at most <paramref name="most"/>, until everything queued has
        /// been handed to NVDA, which goes on speaking it after this process has
        /// gone. For the last words before quitting: <see cref="Shutdown"/>
        /// clears the queue, so anything still in it then is never said.
        /// </summary>
        public static bool WaitUntilSpoken(TimeSpan most)
        {
            if (_worker == null) return true;
            var until = DateTime.UtcNow + most;
            lock (Gate)
            {
                while ((Pending.Count > 0 || _handing) && !_shuttingDown)
                {
                    var left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) return false;
                    Monitor.Wait(Gate, left);
                }
                return true;
            }
        }

        private static void PumpForever()
        {
            while (true)
            {
                Utterance next;
                lock (Gate)
                {
                    if (_handing) { _handing = false; Monitor.PulseAll(Gate); }
                    while (Pending.Count == 0 && !_shuttingDown) Monitor.Wait(Gate);
                    if (_shuttingDown) return;
                    next = Pending.Dequeue();
                    _handing = true;
                }

                try
                {
                    // NVDA first, exactly as before. Only when its controller is
                    // not answering (not running, restarting, or the client DLL
                    // missing) does the message go out through UI Automation, for
                    // JAWS, Narrator and the rest. Never both.
                    bool nvda = NvdaRunning();
                    var route = ChooseRoute(nvda, _uiaPost != null, !nvda && UiaClientsListening());
                    if (route == SpeechRoute.Uia)
                    {
                        if (next.Message.Length > 0) RaiseUia(next.Message, next.Interrupt);
                        continue;
                    }
                    if (route != SpeechRoute.Nvda) continue;
                    if (next.Interrupt) CancelNow();

                    // Cancel() enqueues an empty interrupting utterance: the
                    // silence is the whole point, so there is nothing to speak.
                    if (next.Message.Length == 0) continue;

                    var speak = _speakText;
                    if (speak == null) continue;

                    fixed (char* p = next.Message)
                    {
                        speak(p);
                    }
                }
                catch
                {
                    // A failed announcement must never take the app down with it.
                }
            }
        }

        private static void LoadNvda()
        {
            // Must match the *calling process* architecture, not NVDA's.
            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "Arm64",
                Architecture.X64 => "64",
                Architecture.X86 => "32",
                _ => "64",
            };

            string exact = "nvdaControllerClient" + arch + ".dll";
            const string generic = "nvdaControllerClient.dll";

            var candidates = new List<string>();

            // Beside the executable, where a loose copy would sit.
            // A release is single-file (no assembly location), and a loose copy
            // beside it is never ours, so only multi-file builds look here.
            if (!string.IsNullOrEmpty(typeof(Speech).Assembly.Location))
            {
                string baseDir = AppContext.BaseDirectory;
                candidates.Add(Path.Combine(baseDir, exact));
                candidates.Add(Path.Combine(baseDir, generic));
            }

            // Bundled inside a single-file build.
            //
            // The host unpacks native libraries to a directory of its own and
            // records it here. NativeLibrary.TryLoad given a bare name goes
            // through the ordinary OS search, which does not include that
            // directory — so bundling the client produced a build that silently
            // could not speak, with the DLL sitting right there inside it.
            if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string search)
            {
                foreach (var dir in search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    candidates.Add(Path.Combine(dir, exact));
                    candidates.Add(Path.Combine(dir, generic));
                }
            }

            // Finally, let the OS look wherever it normally would.
            candidates.Add(exact);
            candidates.Add(generic);

            foreach (string c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;

                // A bare name goes through the runtime's own probe first, which
                // knows where a single-file build unpacked it. Self-contained
                // (3.1.0), NATIVE_DLL_SEARCH_DIRECTORIES is empty, and the OS
                // search alone left the bundled client unfound: no speech.
                IntPtr lib;
                bool loaded = Path.IsPathRooted(c)
                    ? NativeLibrary.TryLoad(c, out lib)
                    : NativeLibrary.TryLoad(c, typeof(Speech).Assembly, null, out lib) || NativeLibrary.TryLoad(c, out lib);
                if (!loaded) continue;
                try
                {
                    if (!NativeLibrary.TryGetExport(lib, "nvdaController_speakText", out IntPtr speak) ||
                        !NativeLibrary.TryGetExport(lib, "nvdaController_testIfRunning", out IntPtr test))
                    {
                        NativeLibrary.Free(lib);
                        continue;
                    }
                    NativeLibrary.TryGetExport(lib, "nvdaController_cancelSpeech", out IntPtr cancel);

                    _speakText = (delegate* unmanaged[Stdcall]<char*, int>)speak;
                    _testIfRunning = (delegate* unmanaged[Stdcall]<int>)test;
                    _cancelSpeech = cancel == IntPtr.Zero ? null : (delegate* unmanaged[Stdcall]<int>)cancel;
                    var path = new System.Text.StringBuilder(1024);
                    LoadedFrom = GetModuleFileNameW(lib, path, (uint)path.Capacity) > 0 ? path.ToString() : null;
                    LoadDiagnostic = "loaded " + (LoadedFrom ?? c);
                    return;
                }
                catch (Exception e)
                {
                    LoadDiagnostic = "failed binding " + c + ": " + e.Message;
                    try { NativeLibrary.Free(lib); } catch { }
                }
            }

            LoadDiagnostic = $"could not load {exact} (tried {candidates.Count} locations)";
        }

        /// <summary>Where the NVDA client was loaded from, in full; null until it is. The single-file unpack folder, in a release.</summary>
        internal static string? LoadedFrom { get; private set; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetModuleFileNameW(IntPtr module, System.Text.StringBuilder path, uint size);

        // NVDA starting or stopping mid-session is rare; asking on every single
        // announcement is an RPC round trip we pay for nothing. Re-checked
        // often enough that starting NVDA is noticed within a couple of seconds.
        private const int RunningCacheMs = 2000;
        private static long _runningCheckedAt = -RunningCacheMs * 2;
        private static bool _runningCached;

        /// <summary>
        /// Raised when NVDA turns out to have gone away, or come back, with the
        /// notification id and the sentence to say.
        ///
        /// Hung off the check the pump already makes rather than given a probe of
        /// its own. testIfRunning is a synchronous call into another process, so
        /// a watcher that asked on its own schedule would be paying NVDA's price
        /// on a timer for the whole session; this one only ever reads a number
        /// that was about to be read anyway.
        ///
        /// Raised on the speech pump thread — a subscriber that touches the UI
        /// has to marshal.
        /// </summary>
        public static event Action<string, string>? Notification;

        /// <summary>
        /// What the last probe said, so only a change is reported. Null until the
        /// first one, because the two starting states are not symmetrical: NVDA
        /// missing from the outset is worth saying, since everything queued after
        /// it is silently dropped, while NVDA present from the outset is the
        /// ordinary case and nothing has been lost.
        /// </summary>
        private static bool? _reportedRunning;

        public static bool NvdaRunning()
        {
            if (_testIfRunning == null) return false;

            long now = Environment.TickCount64;
            if (now - _runningCheckedAt < RunningCacheMs) return _runningCached;

            try { _runningCached = _testIfRunning() == 0; }
            catch { _runningCached = false; }

            _runningCheckedAt = now;

            if (_reportedRunning != _runningCached)
            {
                bool first = _reportedRunning == null;
                _reportedRunning = _runningCached;

                if (!_runningCached)
                    Notification?.Invoke("speech.unavailable", UnavailableSentence);
                else if (!first)
                    Notification?.Invoke("speech.restored", "NVDA is running again.");
            }

            return _runningCached;
        }

        /// <summary>
        /// Queues a message, optionally interrupting whatever NVDA is saying.
        /// Returns immediately; the controller client is called on the pump.
        /// </summary>
        public static void Speak(string? message, bool interrupt = false)
        {
            if (string.IsNullOrEmpty(message)) return;

            lock (Gate)
            {
                if (_shuttingDown) return;

                // An interrupting message supersedes everything still waiting —
                // that is what "interrupt" means, and it stops a fast arrow run
                // from leaving a queue of announcements nobody wants any more.
                if (interrupt) Pending.Clear();
                else while (Pending.Count >= MaxQueued) Pending.Dequeue();

                Pending.Enqueue(new Utterance(message, interrupt));
                // All, not one: WaitUntilSpoken waits on the same lock, and a
                // single pulse that woke it instead of the pump said nothing.
                Monitor.PulseAll(Gate);
            }
        }

        /// <summary>Said when NVDA goes away. Not "nothing will be spoken" any more: other screen readers hear it.</summary>
        internal const string UnavailableSentence = "NVDA isn't running. Messages will go to any other screen reader instead.";

        // ---- UI Automation, when NVDA is not answering ----

        /// <summary>Which way one message goes out.</summary>
        internal enum SpeechRoute { Nvda, Uia, Silent }

        /// <summary>
        /// The one decision: NVDA whenever its controller answers (so nothing
        /// about NVDA changes, and it never hears a message twice); otherwise a
        /// UI Automation notification when there is a window to raise it from
        /// and a UIA client (JAWS, Narrator, any screen reader) listening;
        /// otherwise nothing.
        /// </summary>
        internal static SpeechRoute ChooseRoute(bool nvdaRunning, bool uiaHost, bool uiaListening) =>
            nvdaRunning ? SpeechRoute.Nvda
            : uiaHost && uiaListening ? SpeechRoute.Uia
            : SpeechRoute.Silent;

        /// <summary>Posts a message to the UI thread to be raised there; null until the tray sets it.</summary>
        private static volatile Action<string, bool>? _uiaPost;

        /// <summary>
        /// Where UI Automation notifications come from: <paramref name="ui"/> is
        /// the UI thread's context (they are raised there, never on the pump),
        /// and <paramref name="target"/> picks the control to raise them from at
        /// that moment. Null, null stops them (quitting).
        /// </summary>
        public static void UseUiaHost(SynchronizationContext? ui, Func<System.Windows.Forms.Control?>? target)
        {
            if (ui == null || target == null) { _uiaPost = null; return; }
            _uiaPost = (text, interrupt) =>
            {
                try
                {
                    ui.Post(_ =>
                    {
                        try
                        {
                            var control = target();
                            if (control == null || control.IsDisposed || !control.IsHandleCreated) return;
                            var processing = interrupt
                                ? System.Windows.Forms.Automation.AutomationNotificationProcessing.ImportantMostRecent
                                : System.Windows.Forms.Automation.AutomationNotificationProcessing.ImportantAll;
                            bool raised = control.AccessibilityObject.RaiseAutomationNotification(
                                System.Windows.Forms.Automation.AutomationNotificationKind.Other, processing, text);
                            // A child control whose provider will not raise: its window will.
                            if (!raised && control.FindForm() is { IsDisposed: false, IsHandleCreated: true } form &&
                                form != control)
                                form.AccessibilityObject.RaiseAutomationNotification(
                                    System.Windows.Forms.Automation.AutomationNotificationKind.Other, processing, text);
                        }
                        catch (Exception ex) { Diag.Note(ex); }
                    }, null);
                }
                catch (Exception ex) { Diag.Note(ex); }
            };
        }

        internal static void RaiseUia(string text, bool interrupt) => _uiaPost?.Invoke(text, interrupt);

        [DllImport("uiautomationcore.dll")]
        private static extern bool UiaClientsAreListening();

        /// <summary>Whether any UI Automation client is listening; false when the call itself fails.</summary>
        private static bool UiaClientsListening()
        {
            try { return UiaClientsAreListening(); }
            catch (Exception ex) { Diag.Note(ex); return false; }
        }

        private static void CancelNow()
        {
            try
            {
                if (_cancelSpeech != null) _cancelSpeech();
            }
            catch
            {
            }
        }
    }
}
