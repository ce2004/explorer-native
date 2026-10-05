using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Progress for a running transfer.
    ///
    /// Deliberately has no progress bar: a bar carries nothing a screen reader
    /// can use. Instead every figure is its own labelled line, so Tab walks
    /// through status, item, progress, size, speed, elapsed and remaining, and
    /// each one reads out with its label.
    ///
    /// Each line is a text box that reports itself as static text. It has to be a
    /// real control to be reachable with Tab at all — a Label cannot take focus —
    /// but nothing here is editable, and a read-only edit announces "edit, read
    /// only" before it says the number anybody came for.
    ///
    /// It is not modal, and that is the whole point of it now.
    ///
    /// It used to be. A paste put this window up with ShowDialog and the file
    /// manager was gone until the copy finished — which for forty gigabytes to
    /// Google Drive is most of an afternoon of not being able to look at a
    /// folder, play a track, or start a second copy. Nothing about the transfer
    /// ever needed that: it runs on its own thread and reports through a
    /// callback, and the only thing ShowDialog was contributing was a disabled
    /// main window.
    ///
    /// So it is a top-level window of its own, in the taskbar and in Alt+Tab,
    /// one per transfer. Unowned deliberately — an owned form is kept out of
    /// Alt+Tab by Windows, which would make a window you can leave but not come
    /// back to. Cascaded, because several of these opening in the same place is
    /// one window as far as anybody can tell.
    /// </summary>
    public sealed class ProgressForm : Form
    {
        /// <summary>
        /// How far each new window is offset from the last, so a second and a
        /// third transfer do not stack exactly on top of the first.
        /// </summary>
        private const int CascadeStep = 32;

        /// <summary>How many are on screen, for the cascade. UI thread only.</summary>
        private static int _open;
        private readonly CancellationTokenSource _cts;
        private readonly Settings _settings;

        private readonly TextBox _status;
        private readonly TextBox _item;
        private readonly TextBox _progress;
        private readonly TextBox _size;
        private readonly TextBox _speed;
        private readonly TextBox _elapsed;
        private readonly TextBox _remaining;

        private int _lastSpokenTenth = -1;

        /// <summary>
        /// Set once Cancel has been pressed, so the ticks still arriving behind it
        /// do not put "Working" back.
        ///
        /// Cancelling is not instant — an upload has to finish or abandon whatever
        /// requests are in the air — and every one of those reported its progress
        /// afterwards, overwriting the one word that said the button had been
        /// heard. Pressing Cancel and watching the dialog go straight back to
        /// "Working" reads as a button that did nothing, which is the moment
        /// somebody presses it again or starts closing things by hand.
        /// </summary>
        private bool _cancelling;

        /// <summary>
        /// A report that arrived before there was a window to put it in.
        ///
        /// The transfer starts before ShowDialog does — deliberately, so nothing
        /// waits on a window — and its first report is the one carrying the totals
        /// the whole dialog is about. Dropped, the dialog opens reading "Starting"
        /// and "calculating" until the second one arrives, which for an upload is
        /// a Drive round trip away.
        /// </summary>
        private TransferProgress? _pending;

        /// <summary>Kept so the form can be sized to fit them. See OnLoad.</summary>
        private readonly TableLayoutPanel _layout;
        private readonly Button _cancel;

        public ProgressForm(string title, Settings settings, CancellationTokenSource cts)
        {
            _cts = cts;
            _settings = settings;

            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;

            // In the taskbar and in Alt+Tab, because it is now something you
            // leave and come back to rather than something in your way.
            ShowInTaskbar = true;

            // And it can be got out of the way without stopping anything. There
            // is no close box: the only two ways out of this window are Cancel,
            // which stops the transfer, and walking away from it, which does
            // not. A close box would be a third that reads like the second and
            // would have to do the first.
            MinimizeBox = true;
            ControlBox = true;
            Width = 560;
            Height = 340;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(10),
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _status = AddField(layout, "Status", "Starting");
            _item = AddField(layout, "Current item", "");
            _progress = AddField(layout, "Progress", "calculating");
            _size = AddField(layout, "Size", "calculating");
            _speed = AddField(layout, "Speed", "calculating");
            _elapsed = AddField(layout, "Elapsed", "0 seconds");
            _remaining = AddField(layout, "Time remaining", "calculating");

            var cancel = new Button { Text = "&Cancel", Dock = DockStyle.Bottom, Height = 36 };
            cancel.Click += (_, _) => RequestCancel();

            Controls.Add(layout);
            Controls.Add(cancel);
            CancelButton = cancel;

            // CancelButton stamps DialogResult.Cancel onto the button it is
            // given, and this window is not modal — so a press of Escape would
            // be setting a dialog result nothing is waiting for. Escape means
            // "stop the transfer" here, which the click handler above does, and
            // the window then stays up saying "Cancelling" until it really has.
            cancel.DialogResult = DialogResult.None;

            _layout = layout;
            _cancel = cancel;

            // Cascaded from the middle of the screen. Several transfers open
            // several of these, and CenterParent would put every one of them in
            // exactly the same place — which looks, and reads through a taskbar,
            // like one window.
            StartPosition = FormStartPosition.Manual;
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
            int step = (_open++ % 6) * CascadeStep;
            Location = new Point(
                area.Left + Math.Max(0, (area.Width - Width) / 2) + step,
                area.Top + Math.Max(0, (area.Height - Height) / 3) + step);

            FormClosed += (_, _) => { if (_open > 0) _open--; };
        }

        /// <summary>
        /// Asks the transfer to stop, and says so.
        ///
        /// The button is disabled on the way past, because cancelling is not
        /// instant and a button that still looks pressable reads as one that did
        /// nothing — which is the moment somebody presses it again.
        /// </summary>
        private void RequestCancel()
        {
            if (_cancelling) return;

            _cancel.Enabled = false;
            _cancelling = true;
            _status.Text = "Cancelling";
            Speak("Cancelling");
            try { _cts.Cancel(); } catch { }
        }

        /// <summary>
        /// Closing the window is cancelling the transfer, and it does not close
        /// until the transfer has actually stopped.
        ///
        /// A window that vanishes the instant the X is clicked, while robocopy
        /// is still finishing a forty gigabyte file and the cleanup has not
        /// started, is a window that lied about what it did. Whoever asked for
        /// it to stop gets to watch it stop; the transfer closes this itself
        /// when it is over.
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !_finished)
            {
                e.Cancel = true;
                RequestCancel();
                return;
            }

            base.OnFormClosing(e);
        }

        private bool _finished;

        /// <summary>
        /// Closes the window because the transfer is over, rather than because
        /// anybody asked. This is the one route past the guard above.
        /// </summary>
        public void Finish()
        {
            _finished = true;
            if (!IsDisposed) Close();
        }

        /// <summary>
        /// Grows the window until every field is actually on it.
        ///
        /// The height was a fixed 340 pixels. That fits seven rows and a button
        /// at 100% scaling and does not at 150%, where the last row — "Time
        /// remaining" — was covered by the Cancel button. It was still built,
        /// still updated on every tick and still reachable with Tab, so a screen
        /// reader read it out perfectly while nobody looking at the window could
        /// see it: the one combination that makes a missing control hard to
        /// notice from either direction.
        ///
        /// Measured rather than made taller by a guessed number, because the
        /// figure that was wrong here was a guessed number. PreferredSize is only
        /// meaningful once the fonts are resolved, which is why this is in OnLoad
        /// and not the constructor.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            int wanted = _layout.PreferredSize.Height + _cancel.Height + Padding.Vertical + 12;
            if (ClientSize.Height < wanted)
                ClientSize = new Size(ClientSize.Width, wanted);
        }

        /// <summary>
        /// Draws whatever arrived while there was no window to draw it on.
        /// </summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            var waiting = _pending;
            if (waiting == null) return;
            _pending = null;
            Update(waiting);
        }

        private static TextBox AddField(TableLayoutPanel layout, string label, string initial)
        {
            var lbl = new Label
            {
                Text = label + ":",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 8, 3, 3),
            };

            var box = new TextBox
            {
                Text = initial,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                TabStop = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                Margin = new Padding(3, 4, 3, 4),
            };

            // The accessible name is what a screen reader speaks on Tab; without
            // it the box would read only its value with no idea what it is.
            box.AccessibleName = label;

            // And it is announced as text, not as a control you might type in.
            //
            // A read-only TextBox reports itself as an edit field, so every one of
            // these read as "Progress, edit, read only, 40 percent" — three words
            // of furniture before the number, on a dialog whose whole purpose is
            // to be listened to while something else is happening. Nothing here
            // is editable and none of it should claim to be.
            box.AccessibleRole = AccessibleRole.StaticText;

            layout.Controls.Add(lbl);
            layout.Controls.Add(box);
            return box;
        }

        public void Update(TransferProgress p)
        {
            // Kept, gated on EXPLORERNATIVE_TRANSFERLOG like the rest of the
            // transfer trace. This is the line that settled whether the window
            // was frozen or the harness reading it was: it showed Update running
            // on the UI thread every 250ms with the byte count climbing, while a
            // cross-process GetWindowText insisted the fields still said
            // "calculating". They did not.
            RoboCopyEngine.Trace($"Update on thread {Environment.CurrentManagedThreadId}: " +
                                 $"done={p.BytesDone} total={p.BytesTotal} known={p.TotalKnown} " +
                                 $"disposed={IsDisposed} handle={IsHandleCreated}");

            if (IsDisposed) return;

            // Kept rather than dropped. The transfer is already running when the
            // window is being built, and the report it made on the way in is the
            // one that knows how many items there are and how big they add up to.
            if (!IsHandleCreated) { _pending = p; return; }

            if (!_cancelling) _status.Text = "Working";
            _item.Text = string.IsNullOrEmpty(p.CurrentItem) ? "(preparing)" : p.CurrentItem;

            var done = SizeFormatter.Format(p.BytesDone, _settings.SizeUnits);

            if (p.TotalKnown)
            {
                var total = SizeFormatter.Format(p.BytesTotal, _settings.SizeUnits);
                _progress.Text = $"{p.Percent:0} percent, {p.ItemsDone} of {NameRules.Items(p.ItemsTotal)}";
                _size.Text = $"{done} of {total}";
                _remaining.Text = p.Remaining is { } left
                    ? RoboCopyEngine.FormatDuration(left)
                    : "calculating";
            }
            else if (p.ItemsKnown && p.ItemsTotal > 0)
            {
                // Counted, with no sizes to go on: a move inside Drive, or a
                // folder of Google documents.
                _progress.Text = $"{p.ItemsDone} of {NameRules.Items(p.ItemsTotal)}";
                _size.Text = done;
                _remaining.Text = "calculating";
            }
            else
            {
                _progress.Text = $"{p.ItemsDone} items so far, total not yet known";
                _size.Text = done;
                _remaining.Text = "calculating";
            }

            _speed.Text = RoboCopyEngine.FormatSpeed(p.BytesPerSecond, _settings.SizeUnits);
            _elapsed.Text = RoboCopyEngine.FormatDuration(p.Elapsed);

            // Spoken only at ten percent steps; anything finer is unusable noise.
            if (_settings.SpeakProgress && _settings.SpeakEnabled && p.TotalKnown)
            {
                int tenth = (int)(p.Percent / 10);
                if (tenth != _lastSpokenTenth && tenth is > 0 and < 10)
                {
                    _lastSpokenTenth = tenth;
                    Speech.Speak($"{tenth * 10} percent");
                }
            }
        }

        private void Speak(string message)
        {
            if (_settings.SpeakEnabled) Speech.Speak(message, _settings.InterruptSpeech);
        }
    }
}

