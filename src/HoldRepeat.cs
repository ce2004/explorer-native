using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Keeps firing an action for as long as its shortcut is held down.
    ///
    /// Windows will do this itself for a hotkey registered without MOD_NOREPEAT,
    /// but only at the keyboard's own repeat rate, which is set for typing and is
    /// both slow to start and slow to run. Every audio hotkey is therefore
    /// registered with MOD_NOREPEAT — one press, one message — and the repeating
    /// is done here, at whatever rate the preferences ask for.
    ///
    /// The key is asked about directly rather than tracked, because these are
    /// global hotkeys: the release happens in whatever application has the
    /// keyboard, and no key-up ever arrives here.
    /// </summary>
    public sealed class HoldRepeat : IDisposable
    {
        private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        /// <summary>
        /// A key that never appears to come up — because the release was
        /// swallowed somewhere, or the machine was locked mid-press — must not
        /// leave this running for ever.
        /// </summary>
        private const int SafetyStopMilliseconds = 30_000;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>
        /// A repeat never uses more than half the thread it runs on.
        ///
        /// The engine lives on the UI thread, so a repeated action runs there
        /// too. Play and pause on a network source can take a hundred
        /// milliseconds or more, and asking for one every sixty leaves the thread
        /// with nothing left over — the window stops painting and Windows calls
        /// it not responding. Which is exactly what holding play/pause did.
        ///
        /// So the next repeat is scheduled from how long the last one actually
        /// took. A cheap action keeps the configured rate; an expensive one slows
        /// itself down until the thread is half idle again.
        /// </summary>
        private const int DutyCycleDivisor = 2;

        /// <summary>However slow the action is, it is still a repeat.</summary>
        private const int SlowestRepeatMilliseconds = 1000;

        /// <summary>
        /// The floor. Windows will not schedule a timer below about ten
        /// milliseconds — USER_TIMER_MINIMUM — so asking for less buys nothing.
        /// A hundred a second is as fast as a held key can be made to go here,
        /// and every action the player has costs a fraction of a millisecond, so
        /// that is not the limit anyway: the step size is.
        /// </summary>
        public const int FastestRepeatMilliseconds = 10;

        /// <summary>
        /// How long a key must be down before it starts repeating.
        ///
        /// This is what separates a press from a hold, and without it there is
        /// no such thing as a press: a finger is on a key for about a tenth of a
        /// second, which at fifty repeats a second is five more of whatever the
        /// key does. Pressing play once played, paused, played and paused again,
        /// landing wherever it happened to stop.
        ///
        /// Two hundred, not the four hundred this shipped with. Four tenths of a
        /// second reads as the key ignoring you before it gives in, and Windows'
        /// own fastest keyboard setting is 250. Two tenths is still twice as
        /// long as a deliberate tap, which is the only thing the delay has to
        /// beat. It was a preference and is not one now: there is one right
        /// answer and it was measured, not chosen.
        /// </summary>
        public const int Delay = 200;

        /// <summary>
        /// How often a held key fires. Ten is the floor Windows will schedule a
        /// timer at, so twenty is fifty times a second and about as fast as this
        /// can be driven. Every action the player has costs a fraction of a
        /// millisecond, so the rate was never what made a hold feel slow — the
        /// step size was, which is what <see cref="AudioActions.Accelerated"/>
        /// is for.
        /// </summary>
        public const int Interval = 20;

        private readonly Timer _timer = new();
        private Shortcut _shortcut;
        private Action<int>? _action;
        private long _startedAt;
        private int _interval = 20;
        private int _repeat;
        private bool _inAction;

        /// <summary>How the key is asked about. Replaced in tests.</summary>
        public Func<int, bool> IsKeyDown { get; set; } =
            vk => (GetAsyncKeyState(vk) & 0x8000) != 0;

        public HoldRepeat() => _timer.Tick += (_, _) => Tick();

        /// <summary>
        /// Begins repeating <paramref name="action"/> while
        /// <paramref name="shortcut"/> stays down. The caller has already run it
        /// once — this is only the repeat.
        ///
        /// The action is told which repeat it is, counting from one, so that
        /// something like the volume can accelerate the longer it is held rather
        /// than crawling along one step at a time.
        /// </summary>
        /// <param name="delayMilliseconds">
        /// How long the key must be down before repeating begins.
        ///
        /// This is the difference between a press and a hold, and without it
        /// there is no such thing as a press: a finger is on a key for something
        /// like a tenth of a second, which at fifty repeats a second is five more
        /// of whatever it does. Pressing play once played, paused, played, paused
        /// and stopped wherever it happened to land — "it plays for a second and
        /// then pauses it". Every keyboard in the world waits before repeating;
        /// so does this.
        /// </param>
        /// <summary>
        /// Moved on by every Start and Stop, so a tick whose action was running
        /// when another key took over does not re-arm the new key's timer with
        /// its own repeat interval — which cut the new key's press delay to
        /// nothing and made a tap repeat.
        /// </summary>
        private int _generation;

        public void Start(Shortcut shortcut, int delayMilliseconds, int intervalMilliseconds, Action<int> action)
        {
            Stop();
            if (!shortcut.IsAssigned) return;

            // A key that is already up by the time we look was a tap, not a hold.
            if (!StillHeld(shortcut, IsKeyDown)) return;

            _shortcut = shortcut;
            _action = action;
            _repeat = 0;
            _startedAt = Environment.TickCount64;

            _interval = Math.Clamp(intervalMilliseconds, FastestRepeatMilliseconds, 2000);
            _timer.Interval = Math.Clamp(delayMilliseconds, FastestRepeatMilliseconds, 5000);
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
            _action = null;
            _generation++;
        }

        /// <summary>
        /// How long to wait before repeating again, given how long the last one
        /// took. Pure, because the thing worth testing about the pacing is the
        /// arithmetic — that a slow action cannot saturate the thread.
        /// </summary>
        public static int NextInterval(int configured, long tookMilliseconds)
        {
            long paced = tookMilliseconds * DutyCycleDivisor;
            long wanted = Math.Max(configured, paced);
            return (int)Math.Clamp(wanted, FastestRepeatMilliseconds, SlowestRepeatMilliseconds);
        }

        private void Tick()
        {
            // One shot, rescheduled at the end: the gap between repeats has to be
            // measured from when the last one finished, not from when it started.
            _timer.Stop();

            var action = _action;
            if (action == null) return;

            // A tick that arrives while the previous action is still running has
            // nothing to add and would only deepen the hole — but skipping one
            // repeat must not end the repeating.
            //
            // The timer is stopped at the top of every tick and started again at
            // the bottom, so returning here without re-arming it is how a held
            // key stops repeating for the rest of the hold: the schedule has been
            // thrown away and the run still in progress reschedules from its own
            // path, which this one has just pre-empted. Re-arm, and let the run
            // that is already going finish.
            //
            // Re-entrancy is not hypothetical. The action runs on the thread that
            // owns the media engine, and a COM call that blocks pumps messages
            // while it waits — which is exactly where a WM_TIMER gets dispatched
            // into the middle of an action that has not returned.
            if (_inAction) { _timer.Start(); return; }

            if (Environment.TickCount64 - _startedAt > SafetyStopMilliseconds) { Stop(); return; }
            if (!StillHeld(_shortcut, IsKeyDown)) { Stop(); return; }

            long before = Environment.TickCount64;
            int generation = _generation;
            _inAction = true;
            try { action(++_repeat); }
            catch { }
            finally { _inAction = false; }

            // Stopped from inside the action, or while it ran — or stopped and
            // started again for another key, which has its own timing.
            if (_action == null || generation != _generation) return;

            _timer.Interval = NextInterval(_interval, Environment.TickCount64 - before);
            _timer.Start();
        }

        /// <summary>
        /// Whether the whole combination is still down — the key and every
        /// modifier it needs.
        ///
        /// The modifiers matter: letting go of Control while keeping the letter
        /// down has ended the shortcut, and carrying on would be a bare letter
        /// repeating into whatever application the user just went back to.
        /// </summary>
        public static bool StillHeld(Shortcut shortcut, Func<int, bool> isDown)
        {
            if (!shortcut.IsAssigned) return false;
            if (!isDown((int)shortcut.Key)) return false;

            if ((shortcut.Modifiers & HotkeyManager.MOD_CONTROL) != 0 && !isDown(VK_CONTROL)) return false;
            if ((shortcut.Modifiers & HotkeyManager.MOD_ALT) != 0 && !isDown(VK_MENU)) return false;
            if ((shortcut.Modifiers & HotkeyManager.MOD_SHIFT) != 0 && !isDown(VK_SHIFT)) return false;
            if ((shortcut.Modifiers & HotkeyManager.MOD_WIN) != 0 &&
                !isDown(VK_LWIN) && !isDown(VK_RWIN)) return false;

            return true;
        }

        public void Dispose()
        {
            Stop();
            _timer.Dispose();
        }
    }
}
