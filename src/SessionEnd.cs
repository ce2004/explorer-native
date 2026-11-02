using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Windows signing out, restarting or shutting down.
    ///
    /// Files other programs saved on the Drive letter live only in this
    /// process's memory until they reach Google (4.3.0 keeps nothing on the
    /// disk), and only Quit sends what is left. Nothing listened for the end of
    /// the session, so Windows ended the process and a document saved a few
    /// seconds before a restart never reached Drive, without a word.
    ///
    /// A hidden top-level window of its own: the tray's other windows are
    /// message-only and are never asked, and the main window may never have
    /// been made. WM_QUERYENDSESSION agrees (never vetoes) and, while files
    /// are still going up, gives Windows a reason to show on its "apps still
    /// need to close" screen, which NVDA reads, with "Shut down anyway" there.
    /// WM_ENDSESSION then quits before it returns, because Windows may end the
    /// process the moment it does.
    /// </summary>
    internal sealed class SessionEndWindow : NativeWindow, IDisposable
    {
        internal const int WM_QUERYENDSESSION = 0x0011;
        internal const int WM_ENDSESSION = 0x0016;

        private readonly Func<IReadOnlyList<string>> _pendingNames;
        private readonly Action _endNow;
        private bool _blocking;
        private bool _ended;

        /// <param name="pendingNames">The files still on their way to Google; asked on the window's own thread.</param>
        /// <param name="endNow">Quits, synchronously, with no questions.</param>
        public SessionEndWindow(Func<IReadOnlyList<string>> pendingNames, Action endNow)
        {
            _pendingNames = pendingNames;
            _endNow = endNow;
            CreateHandle(new CreateParams
            {
                Caption = "Explorer Native",
                Style = unchecked((int)0x80000000), // WS_POPUP, never shown
                ExStyle = 0x80,                       // WS_EX_TOOLWINDOW, never on the taskbar
            });
        }

        /// <summary>What Windows shows while it waits.</summary>
        internal static string ReasonFor(IReadOnlyList<string> names) =>
            names.Count == 1
                ? $"Saving {names[0]} to Google Drive"
                : $"Saving {names.Count} files to Google Drive";

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_QUERYENDSESSION)
            {
                try
                {
                    var names = _pendingNames();
                    if (names.Count > 0 && !_blocking) _blocking = ShutdownBlockReasonCreate(Handle, ReasonFor(names));
                }
                catch { }
                m.Result = (IntPtr)1;
                return;
            }

            if (m.Msg == WM_ENDSESSION)
            {
                try
                {
                    // Zero: somebody cancelled the shutdown, and nothing ends.
                    if (m.WParam != IntPtr.Zero && !_ended)
                    {
                        _ended = true;
                        _endNow();
                    }
                }
                catch { }
                finally { Unblock(); }
                m.Result = IntPtr.Zero;
                return;
            }

            base.WndProc(ref m);
        }

        private void Unblock()
        {
            if (!_blocking) return;
            _blocking = false;
            try { ShutdownBlockReasonDestroy(Handle); } catch { }
        }

        /// <summary>The reason Windows holds for <paramref name="window"/>, or null. For the tests.</summary>
        internal static string? BlockReasonOf(IntPtr window)
        {
            uint length = 0;
            if (!ShutdownBlockReasonQuery(window, null, ref length) || length == 0) return null;
            var text = new StringBuilder((int)length);
            return ShutdownBlockReasonQuery(window, text, ref length) ? text.ToString() : null;
        }

        public void Dispose()
        {
            Unblock();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszReason);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ShutdownBlockReasonQuery(IntPtr hWnd, StringBuilder? pwszBuff, ref uint pcchBuff);
    }
}
