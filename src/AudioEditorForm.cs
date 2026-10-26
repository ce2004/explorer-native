using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// The audio editor: one file, keyboard only, every position spoken.
    ///
    /// Trim the start and end, cut a section out, change the volume of a part
    /// or the whole, undo and redo, save. Nothing is done to the file until
    /// Save: the edits are an <see cref="EditList"/> over the original, and
    /// both playing and saving read the original through it, so what is heard
    /// is what is written. Playback is the player's own pipeline
    /// (<see cref="WasapiPlayback"/>) handed an <see cref="EditedDecoder"/>.
    ///
    /// Save writes a new file beside the original ("name (edited).mp3").
    /// Replacing the original is a separate key that asks first, writes to a
    /// temporary file, sends the original to the Recycle Bin and only then
    /// moves the new one into its place.
    ///
    /// A list holds the state (position, length, selection, edits, last
    /// message) so it can be reviewed with the arrows; everything that happens
    /// is also spoken.
    /// </summary>
    internal sealed class AudioEditorForm : Form
    {
        /// <summary>Raised on the window's thread with the path of each file saved.</summary>
        public event Action<string>? Saved;

        private const string HelpText =
            "Audio editor keys.\n\n" +
            "Space: play or pause.\n" +
            "Shift+Space: play the selection.\n" +
            "Left and Right arrows: back or forward 1 second. With Control: 10 seconds.\n" +
            "Home and End: the start and the end.\n" +
            "P: say the position. L: say the length. S: say the selection.\n" +
            "Left bracket: set the selection's start mark here. Right bracket: set its end mark here.\n" +
            "Delete: cut the selection out.\n" +
            "T: trim, keeping only the selection.\n" +
            "V: change the volume of the selection, or of the whole file when nothing is selected, by a number of decibels.\n" +
            "N: normalise the selection, or the whole file, so its loudest point is 1 decibel below full scale.\n" +
            "Escape: clear the marks; with no marks, close the editor.\n" +
            "Control+Z: undo. Control+Y: redo.\n" +
            "Control+S: save the edited audio as a new file beside the original.\n" +
            "Control+Shift+S: replace the original with the edited audio (asks first; the original goes to the Recycle Bin).\n" +
            "F1: these keys.\n\n" +
            "Nothing changes on disk until you save.";

        private readonly string _path;
        private readonly Settings _settings;
        private readonly ListBox _info;

        private EditHistory? _history;
        private int _rate;
        private int _bits;
        private int? _kbps;
        private AudioTagSet? _tags;

        private double _cursor;
        private double? _markStart, _markEnd;
        private string _lastMessage = "";

        private WasapiPlayback? _playback;
        private EditList? _playbackEdits;
        private bool _playing;
        private double? _stopAt;
        private int _generation;
        private Task _stopping = Task.CompletedTask;

        private bool _busy;
        private readonly System.Windows.Forms.Timer _timer;
        private float _lastGain = float.NaN;

        public AudioEditorForm(string path, Settings settings)
        {
            _path = path;
            _settings = settings;

            Text = $"Editing {Path.GetFileName(path)}";
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 240);
            MinimizeBox = true;

            _info = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                AccessibleName = "Audio editor",
            };
            _info.Items.Add("Loading " + Path.GetFileName(path));
            _info.SelectedIndex = 0;
            Controls.Add(_info);

            _timer = new System.Windows.Forms.Timer { Interval = 100 };
            _timer.Tick += (_, _) => Tick();
        }

        // ---------------------------------------------------------- loading

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _info.Focus();

            try
            {
                var (rate, frames, bits, kbps, tags, why) = await Task.Run(() => Probe(_path));
                if (IsDisposed) return;

                if (rate <= 0 || frames <= 0)
                {
                    Say(rate <= 0 ? $"Could not open {Path.GetFileName(_path)}: {why}"
                                  : "This file does not say how long it is, so it cannot be edited");
                    _info.Items[0] = _lastMessage;
                    _openFailure = _lastMessage;
                    return;
                }

                _rate = rate;
                _bits = bits;
                _kbps = kbps;
                _tags = tags;
                _history = new EditHistory(new EditList(frames, rate));
                _cursor = 0;
                Refresh_();
                Say($"Editing {Path.GetFileName(_path)}, {AudioPlayer.SpokenTime(_history.Current.Seconds)} long. Press F1 for the keys.");
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                Say("Could not open the file: " + ex.Message);
                _openFailure = _lastMessage;
            }
        }

        /// <summary>
        /// Why the file would not open, once that is known. Every key said
        /// "Still opening the file" for ever after a failed open, which reads
        /// as a window that is stuck rather than one that has given up.
        /// </summary>
        private string? _openFailure;

        /// <summary>The file's rate, length, bit depth, bit rate and tags. On a worker.</summary>
        private static (int Rate, long Frames, int Bits, int? Kbps, AudioTagSet? Tags, string Why) Probe(string path)
        {
            var decoder = TrackDecoder.Open(path, null, 0, 0, out string why);
            if (decoder == null) return (0, 0, 0, null, null, why);

            int rate, bits;
            long frames;
            using (decoder)
            {
                rate = decoder.SampleRate;
                frames = (long)Math.Round(decoder.Duration * rate);
                bits = AudioConverter.NativeBitsOf(decoder);
            }

            int? kbps = null;
            var named = AudioTags.ReadNamed(path, new[] { "System.Audio.EncodingBitrate" });
            if (named.TryGetValue("System.Audio.EncodingBitrate", out var text) &&
                long.TryParse(new string(text.Where(char.IsDigit).ToArray()), out long bps) && bps > 0)
                kbps = (int)Math.Max(1, bps / 1000);

            return (rate, frames, bits, kbps, AudioTags.ReadTagSet(path), "");
        }

        // ---------------------------------------------------------- state

        private EditList? Edits => _history?.Current;

        /// <summary>Where we are on the edited timeline, playing or not.</summary>
        /// <remarks>
        /// While a fresh playback is still starting its position reads 0 (no
        /// decoder yet), so the cursor, which Play set to the start point, is
        /// the answer until it has.
        /// </remarks>
        private double Now => _playing && _playback != null && _playbackStart.IsCompleted
            ? Math.Max(0, _playback.Position) : _cursor;

        private (long From, long To)? Selection()
        {
            var edits = Edits;
            if (edits == null || _markStart == null || _markEnd == null) return null;
            long a = edits.FrameAt(_markStart.Value), b = edits.FrameAt(_markEnd.Value);
            if (a > b) (a, b) = (b, a);
            return b > a ? (a, b) : null;
        }

        private string Seconds(long frames) => EditList.SpokenPrecise(frames / (double)Math.Max(1, _rate));

        private string DescribeSelection()
        {
            var selection = Selection();
            if (selection is { } s)
                return $"Selection from {Seconds(s.From)} to {Seconds(s.To)}, {Seconds(s.To - s.From)} long";
            if (_markStart != null && _markEnd == null) return $"Start mark at {EditList.SpokenPrecise(_markStart.Value)}, no end mark";
            if (_markEnd != null && _markStart == null) return $"End mark at {EditList.SpokenPrecise(_markEnd.Value)}, no start mark";
            return "No selection";
        }

        /// <summary>Rewrites the list. Only lines that changed are touched, so the one under the cursor is not re-read for nothing.</summary>
        private void Refresh_()
        {
            var edits = Edits;
            if (edits == null || _history == null) return;

            var lines = new[]
            {
                $"Position {EditList.SpokenPrecise(Now)} of {AudioPlayer.SpokenTime(edits.Seconds)}",
                DescribeSelection(),
                !_history.CanUndo && !_history.CanRedo ? "No edits"
                    : _history.Unsaved ? "Edited, not saved" : "Edits saved",
                _lastMessage.Length > 0 ? _lastMessage : "Press F1 for the keys",
            };

            int keep = Math.Max(0, _info.SelectedIndex);
            _info.BeginUpdate();
            try
            {
                while (_info.Items.Count > lines.Length) _info.Items.RemoveAt(_info.Items.Count - 1);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i >= _info.Items.Count) _info.Items.Add(lines[i]);
                    else if (!Equals(_info.Items[i], lines[i])) _info.Items[i] = lines[i];
                }
                _info.SelectedIndex = Math.Min(keep, _info.Items.Count - 1);
            }
            finally { _info.EndUpdate(); }
        }

        /// <summary>Spoken (queued, interrupting the last) and kept as the list's last line.</summary>
        private void Say(string message)
        {
            _lastMessage = message;
            if (_settings.SpeakEnabled) Speech.Speak(message, interrupt: true);
            if (_history != null) Refresh_();
        }

        // ---------------------------------------------------------- keys

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1)
            {
                MessageBox.Show(this, HelpText, "Audio editor keys", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }

            if (keyData == Keys.Escape)
            {
                if (_markStart != null || _markEnd != null)
                {
                    _markStart = _markEnd = null;
                    Say("Marks cleared");
                }
                else Close();
                return true;
            }

            if (_history == null)
            {
                if (IsEditorKey(keyData))
                {
                    Say(_openFailure != null ? _openFailure + ". Press Escape to close" : "Still opening the file");
                    return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            if (_busy && IsEditorKey(keyData))
            {
                Say("Still working on the last command. Wait a moment, or cancel a save in its own window");
                return true;
            }

            switch (keyData)
            {
                case Keys.Space: TogglePlay(); return true;
                case Keys.Shift | Keys.Space: PlaySelection(); return true;
                case Keys.Left: SeekBy(-1); return true;
                case Keys.Right: SeekBy(1); return true;
                case Keys.Control | Keys.Left: SeekBy(-10); return true;
                case Keys.Control | Keys.Right: SeekBy(10); return true;
                case Keys.Home: SeekTo(0); return true;
                case Keys.End: SeekTo(Edits!.Seconds); return true;
                case Keys.P: Say($"Position {EditList.SpokenPrecise(Now)}"); return true;
                case Keys.L: Say($"Length {EditList.SpokenPrecise(Edits!.Seconds)}"); return true;
                case Keys.S: Say(DescribeSelection()); return true;
                case Keys.OemOpenBrackets: SetMark(start: true); return true;
                case Keys.OemCloseBrackets: SetMark(start: false); return true;
                case Keys.Delete: CutSelection(); return true;
                case Keys.T: TrimToSelection(); return true;
                case Keys.V: ChangeVolume(); return true;
                case Keys.N: Normalise(); return true;
                case Keys.Control | Keys.Z: Undo(); return true;
                case Keys.Control | Keys.Y:
                case Keys.Control | Keys.Shift | Keys.Z: Redo(); return true;
                case Keys.Control | Keys.S: SaveAsNew(); return true;
                case Keys.Control | Keys.Shift | Keys.S: ReplaceOriginal(); return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private static bool IsEditorKey(Keys keyData) => keyData switch
        {
            Keys.Space or (Keys.Shift | Keys.Space) or Keys.Left or Keys.Right or
            (Keys.Control | Keys.Left) or (Keys.Control | Keys.Right) or Keys.Home or Keys.End or
            Keys.P or Keys.L or Keys.S or Keys.OemOpenBrackets or Keys.OemCloseBrackets or Keys.Delete or
            Keys.T or Keys.V or Keys.N or (Keys.Control | Keys.Z) or (Keys.Control | Keys.Y) or
            (Keys.Control | Keys.Shift | Keys.Z) or (Keys.Control | Keys.S) or (Keys.Control | Keys.Shift | Keys.S) => true,
            _ => false,
        };

        // ---------------------------------------------------------- playing

        private void TogglePlay()
        {
            if (_playing) { Pause(); Say($"Paused at {EditList.SpokenPrecise(_cursor)}"); return; }
            Play(_cursor, null);
        }

        private void PlaySelection()
        {
            if (Selection() is not { } s) { Say("Nothing is selected. Set a start mark with [ and an end mark with ]"); return; }
            var edits = Edits!;
            Play(s.From / (double)edits.SampleRate, s.To / (double)edits.SampleRate);
        }

        private void Pause()
        {
            // While it is still starting, the cursor already says where it is, and
            // Start would undo a pause; Play pauses it once Start has returned.
            if (_playback != null && _playing && _playbackStart.IsCompleted)
            {
                _cursor = Math.Max(0, _playback.Position);
                try { _playback.Pause(); } catch { }
            }
            _playing = false;
            _stopAt = null;
            Refresh_();
        }

        /// <summary>
        /// Plays from <paramref name="from"/> on the edited timeline, stopping at
        /// <paramref name="stopAt"/> when given. A paused playback of the same
        /// edits is resumed; anything else starts a fresh one, on a worker,
        /// because opening the device and the file is not for the window's thread.
        /// </summary>
        private async void Play(double from, double? stopAt)
        {
            try
            {
                var edits = Edits;
                if (edits == null) return;
                if (edits.Length == 0) { Say("Nothing is left to play"); return; }
                if (from >= edits.Seconds - 0.05) from = 0;

                PauseMainPlayer();
                _cursor = from;
                _stopAt = stopAt;

                if (_playback != null && ReferenceEquals(_playbackEdits, edits))
                {
                    // Still starting: Start plays from its own start point and the
                    // first Play moves it to the cursor once it has returned.
                    if (_playbackStart.IsCompleted)
                    {
                        _playback.SeekTo(from);
                        _playback.Resume();
                    }
                    _playing = true;
                    _timer.Start();
                    Refresh_();
                    return;
                }

                StopPlayback();
                int generation = ++_generation;

                var playback = new WasapiPlayback
                {
                    // The player's own level, so editing sounds as listening does.
                    Gain = PlayerGain(),
                    DecoderFactory = (int rate, int channels, out string why) =>
                    {
                        var inner = TrackDecoder.Open(_path, null, rate, channels, out why);
                        return inner == null ? null : new EditedDecoder(inner, edits);
                    },
                };
                playback.Finished += () =>
                {
                    try { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => Ended(generation)); } catch { }
                };

                _playback = playback;
                _playbackEdits = edits;
                _playing = true;
                _timer.Start();
                Refresh_();

                var device = _settings.AudioOutputDeviceId;
                var starting = Task.Run(() => playback.Start(_path, true, from, null,
                    string.IsNullOrEmpty(device) ? null : device));
                _playbackStart = starting;
                bool started = await starting;

                if (IsDisposed || generation != _generation) return;
                if (!started)
                {
                    _playing = false;
                    var why = playback.Diagnostic;
                    StopPlayback();
                    Say("Could not play: " + why);
                    return;
                }

                // Keys pressed while it was starting: a move, then a pause.
                if (Math.Abs(_cursor - from) > 0.0005)
                {
                    try { playback.SeekTo(_cursor); } catch { }
                }
                if (!_playing)
                {
                    try { playback.Pause(); } catch { }
                }
            }
            catch (Exception ex)
            {
                _playing = false;
                if (!IsDisposed) Say("Could not play: " + ex.Message);
            }
        }

        private void Ended(int generation)
        {
            if (generation != _generation || IsDisposed) return;
            StopPlayback();
            _cursor = Edits?.Seconds ?? 0;
            Say("End");
        }

        /// <summary>Stops a "play the selection" at the end mark.</summary>
        /// <summary>The main player's volume, or full level when there is no player.</summary>
        private static float PlayerGain()
        {
            try { return Program.AudioGain ?? 1f; }
            catch { return 1f; }
        }

        private void Tick()
        {
            // Only while playing: an idle editor has nothing to follow.
            if (!_playing) { _timer.Stop(); return; }

            // Follows the volume keys while editing: they still go to the player.
            if (_playing && _playback != null)
            {
                float gain = PlayerGain();
                if (!(Math.Abs(gain - _lastGain) <= 0.0001f)) { _playback.Gain = gain; _lastGain = gain; }
            }

            if (!_playing || _playback == null || _stopAt is not { } end) return;
            if (_playback.Position < end) return;
            Pause();
            _cursor = end;
            Refresh_();
        }

        /// <summary>Lets go of the device and the file, on a worker; <see cref="_stopping"/> says when it is done.</summary>
        private void StopPlayback()
        {
            _generation++;
            var old = _playback;
            var starting = _playbackStart;
            _playback = null;
            _playbackStart = Task.FromResult(false);
            _playbackEdits = null;
            _playing = false;
            _stopAt = null;
            if (old == null) return;

            // After its own Start has returned, never during it, and after any
            // earlier one has been let go.
            var before = _stopping;
            _stopping = Task.Run(async () =>
            {
                try { await before; } catch { }
                try { await starting; } catch { }
                try { old.Dispose(); } catch { }
            });
        }

        private Task<bool> _playbackStart = Task.FromResult(false);

        /// <summary>The main player is paused while the editor plays, so two things are not heard at once.</summary>
        private static void PauseMainPlayer()
        {
            try
            {
                if (Program.AudioState is { Playing: true }) Program.RunAudioAction(AudioAction.PlayPause);
            }
            catch { }
        }

        private void SeekBy(double seconds) => SeekTo(Now + seconds);

        private void SeekTo(double seconds)
        {
            var edits = Edits!;
            seconds = Math.Clamp(seconds, 0, edits.Seconds);
            _cursor = seconds;
            // While it is still starting, Start's own seek would undo this one;
            // Play seeks to the cursor once Start has returned.
            if (_playback != null && _playbackStart.IsCompleted)
            {
                try { _playback.SeekTo(seconds); } catch { }
            }
            if (_playing && _stopAt is { } end && seconds >= end) _stopAt = null;

            Say(seconds >= edits.Seconds - 0.001 ? $"End, {AudioPlayer.SpokenTime(seconds)}" : AudioPlayer.SpokenTime(seconds));
        }

        // ---------------------------------------------------------- editing

        private void SetMark(bool start)
        {
            double at = Now;
            if (start) _markStart = at; else _markEnd = at;

            var said = $"{(start ? "Start" : "End")} mark at {EditList.SpokenPrecise(at)}";
            if (_markStart != null && _markEnd != null && _markEnd < _markStart) said += ", before the start mark";
            Say(said);
        }

        private (long From, long To)? NeedSelection()
        {
            var selection = Selection();
            if (selection == null) Say("Nothing is selected. Set a start mark with [ and an end mark with ]");
            return selection;
        }

        /// <summary>Makes an edit current, stopping playback that was playing the old ones and starting again from the cursor.</summary>
        private void Apply(EditList next, string what, double cursor, string spoken)
        {
            // Checked first: an edit that changes nothing leaves playback alone.
            if (!_history!.Apply(next, what)) { Say("That changes nothing"); return; }
            bool wasPlaying = _playing;
            StopPlayback();
            _cursor = Math.Clamp(cursor, 0, next.Seconds);
            Say(spoken);
            if (wasPlaying) Play(_cursor, null);
        }

        private void CutSelection()
        {
            if (NeedSelection() is not { } s) return;
            var edits = Edits!;
            var length = Seconds(s.To - s.From);
            var next = edits.Cut(s.From, s.To);
            _markStart = _markEnd = null;
            Apply(next, $"cut of {length}", s.From / (double)edits.SampleRate,
                $"Cut {length}. Now {AudioPlayer.SpokenTime(next.Seconds)} long");
        }

        private void TrimToSelection()
        {
            if (NeedSelection() is not { } s) return;
            var edits = Edits!;
            var next = edits.KeepOnly(s.From, s.To);
            _markStart = _markEnd = null;
            Apply(next, "trim to the selection", 0, $"Trimmed to the selection, {Seconds(next.Length)} long");
        }

        private (long From, long To, string What) Target()
        {
            var edits = Edits!;
            return Selection() is { } s ? (s.From, s.To, "the selection") : (0, edits.Length, "the whole file");
        }

        private void ChangeVolume()
        {
            var (from, to, what) = Target();
            var typed = PromptForm.Ask(this, "Change volume",
                $"Decibels to add to {what}, for example 3 or -6:", "");
            _info.Focus();
            if (typed == null) return;

            if (EditList.ParseDecibels(typed) is not { } decibels)
            {
                Say("That is not a number of decibels between -60 and 30");
                return;
            }

            var next = Edits!.ChangeGain(from, to, decibels);
            Apply(next, $"volume {Decibels(decibels)} on {what}", Now, $"{Decibels(decibels)} applied to {what}");
        }

        private static string Decibels(double value) =>
            (value > 0 ? "+" : "") + value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " decibels";

        /// <summary>Raises (or lowers) the region so its loudest sample is 1 decibel below full scale.</summary>
        private async void Normalise()
        {
            try
            {
                var (from, to, what) = Target();
                var edits = Edits!;
                _busy = true;
                Say($"Measuring {what}");

                float peak = await Task.Run(() => MeasurePeak(_path, edits, from, to));
                _busy = false;
                if (IsDisposed || !ReferenceEquals(edits, Edits)) return;

                if (peak <= 1e-6f) { Say($"{char.ToUpperInvariant(what[0])}{what[1..]} is silent, so there is nothing to normalise"); return; }

                double gain = Math.Round(-1 - 20 * Math.Log10(peak), 2);
                if (Math.Abs(gain) < 0.05) { Say($"{char.ToUpperInvariant(what[0])}{what[1..]} is already normalised"); return; }

                Apply(edits.ChangeGain(from, to, gain), $"normalise {what}", Now,
                    $"Normalised {what}: {Decibels(gain)}");
            }
            catch (Exception ex)
            {
                _busy = false;
                if (!IsDisposed) Say("Could not measure the audio: " + ex.Message);
            }
        }

        /// <summary>The loudest sample in [from, to) of the edited audio. On a worker.</summary>
        private static float MeasurePeak(string path, EditList edits, long from, long to)
        {
            var inner = TrackDecoder.Open(path, null, 0, 0, out string why)
                        ?? throw new IOException(why);
            using var decoder = new EditedDecoder(inner, edits);
            decoder.SeekTo(from / (double)edits.SampleRate);

            int channels = Math.Max(1, decoder.Channels);
            var block = new float[8192 * channels];
            long left = to - from;
            float peak = 0;
            while (left > 0)
            {
                int got = decoder.Read(block, (int)Math.Min(8192, left));
                if (got <= 0) break;
                for (int i = 0; i < got * channels; i++)
                {
                    float v = Math.Abs(block[i]);
                    if (v > peak) peak = v;
                }
                left -= got;
            }
            return peak;
        }

        private void Undo()
        {
            if (!_history!.CanUndo) { Say("Nothing to undo"); return; }
            double here = Now;
            bool wasPlaying = _playing;
            StopPlayback();
            _cursor = here;
            var what = _history!.Undo();
            if (what == null) { Say("Nothing to undo"); return; }
            _cursor = Math.Clamp(here, 0, Edits!.Seconds);
            Say($"Undid the {what}");
            if (wasPlaying) Play(_cursor, null);
        }

        private void Redo()
        {
            if (!_history!.CanRedo) { Say("Nothing to redo"); return; }
            double here = Now;
            bool wasPlaying = _playing;
            StopPlayback();
            _cursor = here;
            var what = _history!.Redo();
            if (what == null) { Say("Nothing to redo"); return; }
            _cursor = Math.Clamp(here, 0, Edits!.Seconds);
            Say($"Redid the {what}");
            if (wasPlaying) Play(_cursor, null);
        }

        // ---------------------------------------------------------- saving

        /// <summary>The format to save in: the original's when it can be written, otherwise asked.</summary>
        private async Task<AudioFormatChoice?> ChooseFormat(bool mayAsk)
        {
            var same = AudioFormats.SameFormatAs(Path.GetExtension(_path), _kbps);
            var unavailable = await Task.Run(AudioConvertForm.Availability);
            if (IsDisposed) return null;

            if (same != null && unavailable[same.Kind] == null) return same;
            if (!mayAsk) return null;

            var request = AudioConvertForm.Ask(this, "Save edited audio",
                $"{Path.GetExtension(_path).TrimStart('.').ToUpperInvariant()} files cannot be written here. Save the edited audio as:",
                AudioFormats.ById(_settings.AudioConvertFormat), unavailable, false, false, "&Save");
            _info.Focus();
            return request?.Choice;
        }

        private async void SaveAsNew() => await BusyWhile(SaveAsNewCore);

        private async void ReplaceOriginal() => await BusyWhile(ReplaceOriginalCore);

        /// <summary>
        /// A save, busy from the first question to the last word. Busy used to
        /// end with the render, so while the original went to the Recycle Bin
        /// and the new file was moved into its place — up to two minutes of
        /// waiting on the shell — Space played the file being replaced, a second
        /// Control+Shift+S rendered from an original already in the bin, and the
        /// window closed under the move.
        /// </summary>
        private async Task BusyWhile(Func<Task> save)
        {
            _busy = true;
            try { await save(); }
            catch (Exception ex)
            {
                if (!IsDisposed) Say("Could not save: " + AudioConverter.Reason(ex));
            }
            finally { _busy = false; }
        }

        private async Task SaveAsNewCore()
        {
            try
            {
                var edits = Edits!;
                if (edits.Length == 0) { Say("Nothing is left to save"); return; }

                var choice = await ChooseFormat(mayAsk: true);
                if (choice == null || IsDisposed) return;

                var folder = Path.GetDirectoryName(_path) ?? "";
                var stem = Path.GetFileNameWithoutExtension(_path);
                var suggested = await Task.Run(() => Path.GetFileName(
                    FileOperations.UniqueName(Path.Combine(folder, $"{stem} (edited){choice.Extension}"), null, folder: false)));
                if (IsDisposed) return;

                var name = PromptForm.Ask(this, "Save edited audio", "Save the edited audio as:", suggested, selectStem: true);
                _info.Focus();
                if (name == null) return;

                if (!NameRules.IsUsableName(name)) { Say("That name cannot be used: " + NameRules.DescribeBadName(name)); return; }
                if (!string.Equals(Path.GetExtension(name), choice.Extension, StringComparison.OrdinalIgnoreCase))
                    name += choice.Extension;

                var target = Path.Combine(folder, name);
                if (string.Equals(target, _path, StringComparison.OrdinalIgnoreCase))
                {
                    Say("That is the original's own name. To replace the original, press Control+Shift+S");
                    return;
                }

                var (file, dir) = await Task.Run(() => (File.Exists(NameRules.ExactPath(target)), Directory.Exists(NameRules.ExactPath(target))));
                if (IsDisposed) return;
                if (dir) { Say($"There is a folder called {name}. Choose another name"); return; }
                if (file)
                {
                    var answer = MessageBox.Show(this, $"{name} already exists. Replace it with the edited audio?",
                        "Save edited audio", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    _info.Focus();
                    if (answer != DialogResult.Yes) return;
                }

                var temp = await Render(edits, choice, AudioConverter.TempPathBeside(target));
                if (temp == null) return;

                string final;
                try { final = await Task.Run(() => AudioConverter.Place(temp, target, replace: file)); }
                catch
                {
                    // Not left in the person's folder under a hidden temp name.
                    AudioEncoders.TryDelete(temp);
                    throw;
                }
                if (IsDisposed) return;
                if (ReferenceEquals(edits, Edits)) _history!.MarkSaved();
                Saved?.Invoke(final);
                Say($"Saved {Path.GetFileName(final)}{_notes}");
            }
            catch (Exception ex)
            {
                if (!IsDisposed) Say("Could not save: " + AudioConverter.Reason(ex));
            }
        }

        /// <summary>
        /// Replaces the original, only when asked twice over: this key, then a
        /// question whose safe answer is the default. The edited audio is written
        /// to a temporary file first, the original goes to the Recycle Bin, and
        /// only then is the new file moved into its place. If the Recycle Bin
        /// will not take the original, nothing is replaced and the edit is kept
        /// under a new name instead.
        /// </summary>
        private async Task ReplaceOriginalCore()
        {
            try
            {
                var edits = Edits!;
                var name = Path.GetFileName(_path);
                if (edits.Length == 0) { Say("Nothing is left to save"); return; }
                if (edits.IsUnchanged) { Say("There are no edits to save"); return; }

                var choice = await ChooseFormat(mayAsk: false);
                if (IsDisposed) return;
                if (choice == null)
                {
                    Say($"{Path.GetExtension(_path).TrimStart('.').ToUpperInvariant()} files cannot be written here, " +
                        "so the original cannot be replaced. Press Control+S to save a copy in another format");
                    return;
                }

                var answer = MessageBox.Show(this,
                    $"Replace {name} with the edited audio? The original goes to the Recycle Bin.",
                    "Replace the original", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                _info.Focus();
                if (answer != DialogResult.Yes) return;

                var temp = await Render(edits, choice, AudioConverter.TempPathBeside(_path));
                if (temp == null) return;

                // The player must let go of the original before it can move, and
                // so must the main player: pausing it keeps the file open.
                StopPlayback();
                await _stopping;
                try
                {
                    if (Program.AudioState is { HasTrack: true } main &&
                        string.Equals(main.Path, _path, StringComparison.OrdinalIgnoreCase))
                        Program.RunAudioAction(AudioAction.Stop);
                }
                catch { }

                string KeepAsEdited() => AudioConverter.Place(temp,
                    FileOperations.UniqueName(Path.Combine(Path.GetDirectoryName(_path) ?? "",
                        $"{Path.GetFileNameWithoutExtension(_path)} (edited){choice.Extension}"), null, folder: false),
                    replace: false);

                string? refused = await Task.Run(() => ShellDelete.RecycleWithoutUi(_path));
                if (refused != null)
                {
                    var kept = await Task.Run(KeepAsEdited);
                    if (IsDisposed) return;
                    _history!.MarkSaved();
                    Saved?.Invoke(kept);
                    Say($"The original was not replaced, because {refused}. The edited audio was saved as {Path.GetFileName(kept)}");
                    return;
                }

                // The original is in the Recycle Bin now. If the move fails (an
                // antivirus or the indexer holding the new file), the edit must
                // not be left under a hidden temp name with nothing at the
                // original's: a few tries, then it is kept as "(edited)".
                Exception? moveFailed = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try { await Task.Run(() => File.Move(NameRules.ExactPath(temp), NameRules.ExactPath(_path))); moveFailed = null; break; }
                    catch (IOException ex) { moveFailed = ex; await Task.Delay(300); }
                    catch (UnauthorizedAccessException ex) { moveFailed = ex; await Task.Delay(300); }
                }
                if (moveFailed != null)
                {
                    var kept = await Task.Run(KeepAsEdited);
                    if (IsDisposed) return;
                    _history!.MarkSaved();
                    Saved?.Invoke(kept);
                    Say($"The edited audio could not take the original's place ({AudioConverter.Reason(moveFailed)}), " +
                        $"so it was saved as {Path.GetFileName(kept)}. The original is in the Recycle Bin");
                    return;
                }
                if (IsDisposed) return;
                Saved?.Invoke(_path);

                // The file on disk is now the edited one: start again from it.
                var (rate, frames, bits, kbps, tags, _) = await Task.Run(() => Probe(_path));
                if (IsDisposed) return;
                _rate = rate > 0 ? rate : _rate;
                _bits = bits;
                _kbps = kbps;
                _tags = tags;
                _history = new EditHistory(new EditList(frames, _rate));
                _cursor = 0;
                _markStart = _markEnd = null;
                Say($"Replaced {name} with the edited audio{_notes}. The original is in the Recycle Bin");
            }
            catch (Exception ex)
            {
                if (!IsDisposed) Say("Could not replace the original: " + AudioConverter.Reason(ex));
            }
        }

        private string _notes = "";

        /// <summary>
        /// Writes the edited audio to <paramref name="temp"/> with a progress
        /// window like a copy's, and returns it; null (with nothing left behind)
        /// when it failed or was cancelled.
        /// </summary>
        private async Task<string?> Render(EditList edits, AudioFormatChoice choice, string temp)
        {
            // Put back as it was, not cleared: a save is still busy after the
            // render, moving the file into place (see BusyWhile).
            bool wasBusy = _busy;
            _busy = true;
            _notes = "";
            _cursor = Now;
            StopPlayback();

            using var cts = new CancellationTokenSource();
            using var window = new ProgressForm($"Saving {Path.GetFileName(_path)} as {choice.FormatName}", _settings, cts);
            var progress = new Progress<TransferProgress>(p => window.Update(p));

            long estimate = 1;
            try { estimate = Math.Max(1, (long)(new FileInfo(NameRules.ExactPath(_path)).Length * (edits.Length / (double)Math.Max(1, edits.SourceFrames)))); }
            catch { }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var notes = new List<string>();
            var path = _path;
            int bits = _bits;
            var tags = _tags;

            try
            {
                var task = Task.Run(() =>
                {
                    var inner = TrackDecoder.Open(path, null, 0, 0, out string why)
                                ?? throw new AudioEncodeException("the original could not be read: " + why);
                    using var decoder = new EditedDecoder(inner, edits);
                    long last = -1000;
                    AudioConverter.Encode(decoder, choice, temp, bits, tags, f =>
                    {
                        if (clock.ElapsedMilliseconds - last < 250 && f < 1) return;
                        last = clock.ElapsedMilliseconds;
                        long done = (long)(estimate * f);
                        ((IProgress<TransferProgress>)progress).Report(new TransferProgress(done, estimate, 0, 1,
                            Path.GetFileName(path), done / Math.Max(0.001, clock.Elapsed.TotalSeconds), clock.Elapsed));
                    }, cts.Token, notes);
                }, cts.Token);

                window.Show();
                await task;
                window.Finish();

                _notes = notes.Count == 0 ? "" : ". " + string.Join(". ", notes);
                return temp;
            }
            catch (OperationCanceledException)
            {
                window.Finish();
                await Task.Run(() => AudioEncoders.TryDelete(temp));
                if (!IsDisposed) Say("Saving cancelled; nothing was written");
                return null;
            }
            catch (Exception ex)
            {
                window.Finish();
                await Task.Run(() => AudioEncoders.TryDelete(temp));
                if (!IsDisposed) Say("Could not save: " + AudioConverter.Reason(ex));
                return null;
            }
            finally { _busy = wasBusy; }
        }

        // ---------------------------------------------------------- closing

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                if (_busy)
                {
                    e.Cancel = true;
                    Say("Wait for the save to finish, or cancel it in its window");
                    return;
                }

                if (_history is { Unsaved: true })
                {
                    var answer = MessageBox.Show(this, "The edits have not been saved. Close and lose them?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes) { e.Cancel = true; _info.Focus(); return; }
                }
            }

            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            _timer.Dispose();
            StopPlayback();
            base.OnFormClosed(e);
        }
    }
}
