using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// The picture describer's window (Enter on a picture). It asks for a
    /// description at once and reads it out; the edit box ("Talk to Claude")
    /// has the focus the whole time, so a question is just typing and Enter.
    /// Shift+Tab is the conversation, a line at a time; Ctrl+C copies a line,
    /// Ctrl+A then Ctrl+C the whole transcript. Tab past the edit box says what
    /// the conversation has cost. Escape closes it and stops any answer on its way.
    /// Nothing in here may throw out of a handler: every failure becomes a line
    /// in the conversation and is said.
    /// </summary>
    internal sealed class AiDescriberForm : Form
    {
        private readonly Settings _settings;
        private readonly AiProvider _provider;
        private readonly string _key;
        private string _model;
        private readonly string _fileName;
        private readonly Func<CancellationToken, Task<byte[]>> _read;
        private readonly string? _localPath;
        private readonly string _speaker;

        private readonly ConversationList _list = new();
        private readonly TextBox _input = new();
        private readonly TextBox _cost = new();
        private readonly PictureBox _preview = new();
        private readonly Label _state = new();
        private Image? _shownPicture;

        private readonly List<AiTurn> _turns = new();
        private readonly List<(string Who, string Text)> _transcript = new();
        private AiPicture? _picture;
        private string? _retry;
        private bool _busy;
        private readonly CancellationTokenSource _closing = new();
        private Task<AiPrice?>? _price;
        private decimal _spent;
        private bool _priceUnknown;
        private int _answers;

        /// <summary>The conversation's turns as sent, for the suite.</summary>
        internal IReadOnlyList<AiTurn> Turns => _turns;

        /// <summary>The list's lines, for the suite.</summary>
        internal IEnumerable<string> Lines => _list.Items.Cast<object>().Select(o => o.ToString() ?? "");

        internal TextBox Input => _input;
        internal TextBox CostBox => _cost;
        internal ListBox Conversation => _list;
        internal bool Busy => _busy;

        /// <summary>Everything said, for the suite (id, text).</summary>
        internal readonly List<(string Id, string Text)> Said = new();

        public AiDescriberForm(Settings settings, AiProvider provider, string key, string model, string fileName,
            Func<CancellationToken, Task<byte[]>> read, string? localPath)
        {
            _settings = settings;
            _provider = provider;
            _key = key;
            _model = model ?? "";
            _fileName = fileName;
            _read = read;
            _localPath = localPath;
            _speaker = AiDescriber.Speaker(provider, _model);

            Text = fileName + " - " + _speaker;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 10.5f);
            ClientSize = new Size(980, 640);
            MinimumSize = new Size(560, 400);

            bool contrast = SystemInformation.HighContrast;
            var accent = provider switch
            {
                AiProvider.Claude => Color.FromArgb(217, 119, 87),
                AiProvider.Gemini => Color.FromArgb(66, 133, 244),
                AiProvider.OpenAI => Color.FromArgb(16, 163, 127),
                _ => Color.FromArgb(124, 112, 255),
            };
            var back = Color.FromArgb(22, 22, 30);
            var surface = Color.FromArgb(33, 33, 45);
            var ink = Color.FromArgb(238, 238, 244);
            var muted = Color.FromArgb(160, 162, 182);

            // ---- header: who and what ----
            var header = new Panel { Dock = DockStyle.Top, Height = 64, Padding = new Padding(18, 10, 18, 6) };
            var strip = new Panel { Dock = DockStyle.Left, Width = 6 };
            var title = new Label
            {
                Text = _speaker + " is describing " + fileName,
                AutoSize = false, Dock = DockStyle.Top, Height = 28, UseMnemonic = false, AutoEllipsis = true,
                Font = new Font("Segoe UI Semibold", 14f), Padding = new Padding(10, 0, 0, 0),
            };
            var subtitle = new Label
            {
                Text = AiDescriber.Name(provider) + (_model.Length > 0 ? ", " + _model : ""),
                AutoSize = false, Dock = DockStyle.Top, Height = 20, UseMnemonic = false, Padding = new Padding(10, 0, 0, 0),
            };
            header.Controls.Add(subtitle);
            header.Controls.Add(title);
            header.Controls.Add(strip);

            // ---- the picture ----
            var pictureSide = new Panel { Dock = DockStyle.Left, Width = 320, Padding = new Padding(18, 8, 8, 12) };
            _preview.Dock = DockStyle.Fill;
            _preview.SizeMode = PictureBoxSizeMode.Zoom;
            _preview.AccessibleName = "The picture";
            _preview.AccessibleRole = AccessibleRole.Graphic;
            pictureSide.Controls.Add(_preview);

            // ---- the conversation ----
            var middle = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 8, 18, 8) };
            _list.Dock = DockStyle.Fill;
            _list.AccessibleName = "Conversation";
            _list.IntegralHeight = false;
            _list.SelectionMode = SelectionMode.MultiExtended;
            _list.BorderStyle = BorderStyle.None;
            _list.HorizontalScrollbar = false;
            _list.TabIndex = 0;
            _list.KeyDown += (_, e) => Guard(() => ListKeyDown(e));
            middle.Controls.Add(_list);

            // ---- talking, and what it cost ----
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 132, Padding = new Padding(18, 6, 18, 10), TabIndex = 1 };
            var talk = new Label
            {
                Text = "Talk to " + _speaker + ":", AutoSize = false, Dock = DockStyle.Top, Height = 24, UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 10.5f),
            };
            _input.Dock = DockStyle.Top;
            _input.MaxLength = 4000;
            _input.Font = new Font("Segoe UI", 12f);
            _input.AccessibleName = "Talk to " + _speaker;
            _input.PlaceholderText = "Ask anything about the picture, then press Enter";
            _input.TabIndex = 0;
            _input.KeyDown += (_, e) => Guard(() => InputKeyDown(e));
            var gap = new Panel { Dock = DockStyle.Top, Height = 8 };
            _cost.Dock = DockStyle.Top;
            _cost.ReadOnly = true;
            _cost.TabStop = true;
            _cost.BorderStyle = BorderStyle.None;
            _cost.AccessibleName = "Cost so far";
            _cost.TabIndex = 1;
            _cost.Text = AiDescriber.CostSentence(0, false, 0);
            _state.AutoSize = false;
            _state.Dock = DockStyle.Top;
            _state.Height = 24;
            _state.UseMnemonic = false;
            _state.AutoEllipsis = true;
            bottom.Controls.Add(_state);
            bottom.Controls.Add(_cost);
            bottom.Controls.Add(gap);
            bottom.Controls.Add(_input);
            bottom.Controls.Add(talk);

            Controls.Add(middle);
            Controls.Add(pictureSide);
            Controls.Add(bottom);
            Controls.Add(header);

            if (!contrast)
            {
                BackColor = back;
                ForeColor = ink;
                header.BackColor = back;
                strip.BackColor = accent;
                title.ForeColor = ink;
                subtitle.ForeColor = muted;
                pictureSide.BackColor = back;
                _preview.BackColor = surface;
                middle.BackColor = back;
                _list.BackColor = surface;
                _list.ForeColor = ink;
                bottom.BackColor = back;
                talk.ForeColor = accent;
                _input.BackColor = surface;
                _input.ForeColor = ink;
                _cost.BackColor = back;
                _cost.ForeColor = muted;
                _state.ForeColor = muted;
            }
            _list.Accent = contrast ? SystemColors.Highlight : accent;
            _list.Muted = contrast ? SystemColors.GrayText : muted;
            _list.Plain = contrast;

            Shown += (_, _) =>
            {
                _input.Focus();
                _ = StartAsync();
            };
            FormClosing += (_, _) => _closing.Cancel();
            FormClosed += (_, _) =>
            {
                _preview.Image = null;
                _shownPicture?.Dispose();
            };
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        /// <summary>A handler's work, never letting anything out (an escaping exception would end the application).</summary>
        private void Guard(Action work)
        {
            try { work(); }
            catch (Exception ex) { Problem("Something went wrong: " + AiDescriber.Short(ex.Message)); }
        }

        // ---------- starting ----------

        private async Task StartAsync()
        {
            try
            {
                _price = Task.Run(async () =>
                {
                    try { return await AiDescriber.PriceAsync(_provider, _model, _closing.Token); }
                    catch { return null; }
                });
                if (!await LoadPictureAsync()) { _retry = AiDescriber.FirstQuestion(_settings.AiBriefFirst); return; }
                await AskAsync(AiDescriber.FirstQuestion(_settings.AiBriefFirst), fromPerson: false);
            }
            catch (Exception ex) { if (!IsDisposed) Problem("Something went wrong: " + AiDescriber.Short(ex.Message)); }
        }

        /// <summary>Reads and prepares the picture once. False (and said) when it cannot be.</summary>
        private async Task<bool> LoadPictureAsync()
        {
            if (_picture != null) return true;
            _busy = true;
            try
            {
                Say("ai.looking", _speaker + " is looking at the picture", interrupt: false);
                ShowPending(_speaker + " is looking at the picture…");
                var token = _closing.Token;
                var picture = await Task.Run(async () =>
                {
                    var bytes = await _read(token).ConfigureAwait(false);
                    return AiDescriber.PreparePicture(bytes, _localPath);
                }, token);
                if (IsDisposed) return false;
                _picture = picture;
                ShowPreview(picture);
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (AiException ex) { if (!IsDisposed) Problem(ex.Message); return false; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!IsDisposed) Problem("The picture couldn't be read: " + AiDescriber.Short(ex.Message));
                return false;
            }
            catch (Exception ex) { if (!IsDisposed) Problem("The picture couldn't be opened: " + AiDescriber.Short(ex.Message)); return false; }
            finally
            {
                _busy = false;
                if (!IsDisposed) HidePending();
            }
        }

        private void ShowPreview(AiPicture picture)
        {
            try
            {
                using var stream = new MemoryStream(picture.Bytes, writable: false);
                using var image = Image.FromStream(stream);
                _shownPicture = new Bitmap(image);
                _preview.Image = _shownPicture;
            }
            catch { }
        }

        // ---------- talking ----------

        private void InputKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter || e.Shift || e.Control || e.Alt) return;
            e.SuppressKeyPress = true;
            e.Handled = true;

            if (_busy) { Say("ai.looking", "Still waiting for " + _speaker + "'s answer.", interrupt: true); return; }

            var text = _input.Text.Trim();
            if (text.Length == 0)
            {
                if (_retry != null)
                {
                    var again = _retry;
                    _ = AskAsync(again, fromPerson: _turns.Count > 0, retrying: true);
                }
                else Say("ai.failed", "Type a question about the picture first, then press Enter.", interrupt: true);
                return;
            }

            _input.Clear();
            Add("You", text);
            _ = AskAsync(text, fromPerson: true);
        }

        /// <summary>
        /// Sends the conversation with one more question. The question joins the
        /// conversation only once it is answered, so a failure leaves nothing
        /// half-asked and Enter on the empty box simply asks it again.
        /// </summary>
        private async Task AskAsync(string question, bool fromPerson, bool retrying = false)
        {
            if (_busy || IsDisposed) return;
            try
            {
                if (_picture == null && !await LoadPictureAsync()) { _retry = question; return; }
                _busy = true;
                _retry = null;
                Say("ai.looking", _turns.Count == 0 && !fromPerson ? _speaker + " is looking at the picture" : _speaker + " is thinking",
                    interrupt: retrying || fromPerson);
                ShowPending(_speaker + " is thinking…");

                if (string.IsNullOrWhiteSpace(_model))
                {
                    var models = await AiDescriber.CheckKeyAsync(_provider, _key, _closing.Token);
                    _model = AiDescriber.Recommended(_provider, models);
                }

                var turns = new List<AiTurn>(_turns) { new AiTurn(true, question) };
                var reply = await AiDescriber.AskAsync(_provider, _key, _model, _picture!, _fileName, turns, _closing.Token);
                if (IsDisposed) return;

                _turns.Add(new AiTurn(true, question));
                _turns.Add(new AiTurn(false, reply.Text));
                _answers++;
                var price = _price == null ? null : await _price;
                var cost = AiDescriber.CostOf(reply, price ?? AiDescriber.PriceNow(_provider, _model));
                if (cost is { } c) _spent += c; else _priceUnknown = true;
                _cost.Text = AiDescriber.CostSentence(_spent, _priceUnknown, _answers);

                HidePending();
                var text = reply.CutShort ? reply.Text + "\n\n(The answer was cut short.)" : reply.Text;
                int at = Add(_speaker, text);
                SelectLine(at);
                Say("ai.reply", text, interrupt: true);
            }
            catch (OperationCanceledException) { }
            catch (AiException ex)
            {
                if (IsDisposed) return;
                _retry = question;
                Problem(ex.KeyRefused || ex.OutOfCredit ? ex.Message : ex.Message.TrimEnd() + (ex.Message.Contains("Press Enter") ? "" : " Press Enter to try again."));
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                _retry = question;
                Problem("Something went wrong: " + AiDescriber.Short(ex.Message) + " Press Enter to try again.");
            }
            finally
            {
                _busy = false;
                if (!IsDisposed) HidePending();
            }
        }

        // ---------- the conversation list ----------

        private sealed class Line
        {
            public string Who = "";
            public string Text = "";
            public bool First;
            public bool Pending;
            public override string ToString() => First ? Who + ": " + Text : Text;
        }

        /// <summary>Adds a message, a line per paragraph; returns the index of its first line.</summary>
        private int Add(string who, string text)
        {
            HidePending();
            _transcript.Add((who, text));
            var paragraphs = text.Replace("\r\n", "\n").Split('\n')
                .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (paragraphs.Count == 0) paragraphs.Add(text.Trim());
            int first = _list.Items.Count;
            _list.BeginUpdate();
            try
            {
                for (int i = 0; i < paragraphs.Count; i++)
                    _list.Items.Add(new Line { Who = who, Text = paragraphs[i], First = i == 0 });
            }
            finally { _list.EndUpdate(); }
            _list.TopIndex = Math.Max(0, first);
            return first;
        }

        private void SelectLine(int index)
        {
            if (index < 0 || index >= _list.Items.Count) return;
            _list.ClearSelected();
            _list.SetSelected(index, true);
            _list.TopIndex = index;
        }

        private void ShowPending(string text)
        {
            HidePending();
            _state.Text = text;
            _list.Items.Add(new Line { Who = _speaker, Text = text, Pending = true });
            _list.TopIndex = _list.Items.Count - 1;
        }

        private void HidePending()
        {
            for (int i = _list.Items.Count - 1; i >= 0; i--)
                if (_list.Items[i] is Line { Pending: true }) _list.Items.RemoveAt(i);
            _state.Text = "";
        }

        private void Problem(string text)
        {
            HidePending();
            int at = Add("Problem", text);
            SelectLine(at);
            Say("ai.failed", text, interrupt: true);
        }

        private void ListKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (_list.Items.Count == 0) { Say("ai.copied", "There's nothing in the conversation yet.", interrupt: true); return; }
                _list.BeginUpdate();
                try { for (int i = 0; i < _list.Items.Count; i++) _list.SetSelected(i, true); }
                finally { _list.EndUpdate(); }
                Say("ai.copied", "Selected the whole conversation. Press Control C to copy it.", interrupt: true);
                return;
            }
            if (e.Control && (e.KeyCode == Keys.C || e.KeyCode == Keys.Insert))
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                Copy();
            }
        }

        /// <summary>The whole conversation as text, with who said what.</summary>
        internal string Transcript()
        {
            var sb = new StringBuilder();
            sb.Append("Conversation with ").Append(_speaker);
            if (_model.Length > 0) sb.Append(" (").Append(_model).Append(')');
            sb.Append(" about ").Append(_fileName).Append("\r\n\r\n");
            foreach (var (who, text) in _transcript)
                sb.Append(who).Append(": ").Append(text.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n\r\n");
            sb.Append(AiDescriber.CostSentence(_spent, _priceUnknown, _answers)).Append("\r\n");
            return sb.ToString();
        }

        private void Copy()
        {
            var chosen = _list.SelectedIndices.Cast<int>().Where(i => _list.Items[i] is Line { Pending: false }).ToList();
            if (chosen.Count == 0)
            {
                Say("ai.copied", _list.Items.Count == 0 ? "There's nothing in the conversation yet." : "Choose a line to copy first.", interrupt: true);
                return;
            }
            bool everything = chosen.Count == _list.Items.Cast<object>().Count(o => o is Line { Pending: false });
            string text = everything ? Transcript()
                : chosen.Count == 1 ? ((Line)_list.Items[chosen[0]]).Text
                : string.Join("\r\n\r\n", chosen.Select(i => _list.Items[i].ToString()));
            try
            {
                Clipboard.SetDataObject(text, copy: true, retryTimes: 10, retryDelay: 100);
                Say("ai.copied", everything ? "Copied the whole conversation" : chosen.Count == 1 ? "Copied" : $"Copied {chosen.Count} lines", interrupt: true);
            }
            catch (Exception ex) { Problem("Couldn't copy: " + AiDescriber.Short(ex.Message)); }
        }

        // ---------- speech ----------

        private void Say(string id, string text, bool interrupt)
        {
            Said.Add((id, text));
            try
            {
                WindowsToasts.Raise(id, text.Length > 250 ? text[..250] + "…" : text, _settings);
                if (!_settings.SpeakEnabled) return;
                if (Notifications.ChannelsFor(id, _settings) != NotificationChannel.Speech) return;
                Speech.Speak(text, interrupt);
            }
            catch { }
        }

        /// <summary>
        /// A list whose lines wrap, so a long answer is readable on screen too.
        /// The items are plain strings to a screen reader.
        /// </summary>
        private sealed class ConversationList : ListBox
        {
            public Color Accent = SystemColors.Highlight;
            public Color Muted = SystemColors.GrayText;
            public bool Plain;
            private int _measuredWidth;
            private const int LB_SETITEMHEIGHT = 0x01A0;

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

            public ConversationList()
            {
                DrawMode = DrawMode.OwnerDrawVariable;
                DoubleBuffered = true;
            }

            /// <summary>
            /// .NET 10's ListBox, being destroyed after a screen reader has read
            /// its lines, walks its line accessibility objects while releasing
            /// them (ListBoxAccessibleObject.ResetListItemAccessibleObjects) and
            /// throws "Collection was modified" out of WM_DESTROY: the unhandled
            /// exception dialog on closing the window. The window is going
            /// anyway, so that one throw is swallowed. Re-check after a .NET upgrade.
            /// </summary>
            protected override void WndProc(ref Message m)
            {
                const int WM_DESTROY = 0x0002;
                if (m.Msg != WM_DESTROY) { base.WndProc(ref m); return; }
                try { base.WndProc(ref m); }
                catch (InvalidOperationException) { }
            }

            private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

            protected override void OnMeasureItem(MeasureItemEventArgs e)
            {
                var width = Math.Max(80, ClientSize.Width - 24);
                if (e.Index < 0 || e.Index >= Items.Count || Items[e.Index] is not Line line) { e.ItemHeight = Font.Height + 12; return; }
                var size = TextRenderer.MeasureText(e.Graphics, line.Text, Font, new Size(width, int.MaxValue), Wrap);
                int label = line.First && !line.Pending ? Font.Height + 2 : 0;
                e.ItemHeight = Math.Min(255, size.Height + label + 12);
            }

            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= Items.Count || Items[e.Index] is not Line line) return;
                bool selected = (e.State & DrawItemState.Selected) != 0;
                var bounds = e.Bounds;
                var back = !selected ? BackColor
                    : Plain ? SystemColors.Highlight
                    : Color.FromArgb(Math.Min(255, BackColor.R + 28), Math.Min(255, BackColor.G + 28), Math.Min(255, BackColor.B + 34));
                using (var brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, bounds);
                if (selected && !Plain) using (var bar = new SolidBrush(Accent)) e.Graphics.FillRectangle(bar, bounds.X, bounds.Y, 3, bounds.Height);

                var fore = Plain && selected ? SystemColors.HighlightText : ForeColor;
                var area = new Rectangle(bounds.X + 10, bounds.Y + 6, bounds.Width - 20, bounds.Height - 8);
                if (line.Pending)
                {
                    using var italic = new Font(Font, FontStyle.Italic);
                    TextRenderer.DrawText(e.Graphics, line.Text, italic, area, Plain ? fore : Muted, Wrap | TextFormatFlags.EndEllipsis);
                    return;
                }
                if (line.First)
                {
                    using var bold = new Font(Font, FontStyle.Bold);
                    var labelColour = Plain ? fore : line.Who == "You" ? Muted : line.Who == "Problem" ? Color.FromArgb(240, 120, 120) : Accent;
                    TextRenderer.DrawText(e.Graphics, line.Who, bold, new Rectangle(area.X, area.Y, area.Width, Font.Height + 2), labelColour,
                        TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    area = new Rectangle(area.X, area.Y + Font.Height + 2, area.Width, Math.Max(1, area.Height - Font.Height - 2));
                }
                TextRenderer.DrawText(e.Graphics, line.Text, Font, area, fore, Wrap | TextFormatFlags.EndEllipsis);
                if ((e.State & DrawItemState.Focus) != 0 && Plain) e.DrawFocusRectangle();
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                // Lines are measured when added; a new width measures them again.
                if (!IsHandleCreated || Math.Abs(ClientSize.Width - _measuredWidth) < 12) return;
                _measuredWidth = ClientSize.Width;
                if (Items.Count == 0) return;
                // Each line gets its new height in place. Clearing and refilling
                // the list did the same, but threw away every line's
                // accessibility object while a screen reader might hold one.
                BeginUpdate();
                try
                {
                    using var g = CreateGraphics();
                    for (int i = 0; i < Items.Count; i++)
                    {
                        var args = new MeasureItemEventArgs(g, i, ItemHeight);
                        OnMeasureItem(args);
                        SendMessage(Handle, LB_SETITEMHEIGHT, (IntPtr)i, (IntPtr)args.ItemHeight);
                    }
                }
                catch { }
                finally { EndUpdate(); Invalidate(); }
            }
        }
    }
}
