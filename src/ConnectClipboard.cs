using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>The PC clipboard as the web app sees it. Absent fields are left out of the JSON.</summary>
    public sealed record ClipState(
        long Seq, string Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Files = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ImageBytes = null);

    /// <summary>The clipboard behind <c>/api/clipboard</c>; an interface so the routes can be tested without
    /// touching the real one, which may be holding something of Conner's.</summary>
    public interface IConnectClipboard
    {
        ClipState Current { get; }

        /// <summary>The state as soon as its seq passes <paramref name="since"/>, or the unchanged state at the timeout.</summary>
        Task<ClipState> WaitAsync(long since, TimeSpan timeout, CancellationToken token);

        /// <summary>The clipboard image as PNG, or null when it holds none.</summary>
        byte[]? Png();

        Task<long> SetTextAsync(string text);
        Task<long> SetImageAsync(byte[] image);
        Task<long> SetFilesAsync(IReadOnlyList<string> paths);
    }

    /// <summary>
    /// The PC clipboard, watched and written from one STA thread of its own — never the window's.
    ///
    /// Change comes from <c>AddClipboardFormatListener</c> on a message-only window that thread owns, so
    /// nothing polls: each <c>WM_CLIPBOARDUPDATE</c> takes a snapshot, bumps <c>seq</c>
    /// and wakes the long polls. Writes are queued onto the same thread and go through
    /// <see cref="ClipboardInterop"/>, whose retries (another application holding the clipboard is normal) and
    /// rewound Preferred DropEffect are the window's own.
    ///
    /// No history is kept: only what is on the clipboard now, in memory.
    /// </summary>
    public sealed class ConnectClipboard : IConnectClipboard, IDisposable
    {
        private readonly object _gate = new();
        private readonly Queue<Action> _work = new();
        private ClipState _current = new(0, "empty");
        private byte[]? _png;
        private TaskCompletionSource<bool> _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Listener? _window;
        private Thread? _thread;
        private readonly ManualResetEventSlim _ready = new();

        public ClipState Current { get { lock (_gate) return _current; } }

        public byte[]? Png() { lock (_gate) return _png; }

        public void Start()
        {
            _thread = new Thread(() =>
            {
                try
                {
                    _window = new Listener(this);
                    Observe(Read());
                }
                catch { }
                finally { _ready.Set(); }
                Application.Run();
            })
            { IsBackground = true, Name = "Connect clipboard" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(5));
        }

        // MARK: Watching

        /// <summary>What the clipboard holds now. On the STA thread.</summary>
        private static (ClipState State, byte[]? Png) Read()
        {
            try
            {
                if (ClipboardInterop.TryGetFiles(out var files, out _))
                    return (new ClipState(0, "files", Files: files), null);

                var data = Clipboard.GetDataObject();
                if (data != null)
                {
                    byte[]? png = null;
                    if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream ms) png = ms.ToArray();
                    else if (data.GetDataPresent(DataFormats.Dib) || data.GetDataPresent(DataFormats.Bitmap))
                    {
                        using var image = Clipboard.GetImage();
                        if (image != null)
                        {
                            using var output = new MemoryStream();
                            image.Save(output, ImageFormat.Png);
                            png = output.ToArray();
                        }
                    }
                    if (png != null) return (new ClipState(0, "image", ImageBytes: png.Length), png);

                    if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
                    {
                        var text = Clipboard.GetText();
                        if (!string.IsNullOrEmpty(text)) return (new ClipState(0, "text", Text: text), null);
                    }
                }
            }
            catch { }
            return (new ClipState(0, "empty"), null);
        }

        /// <summary>Takes a snapshot in as the next change. Public to the suite, which feeds it by hand.</summary>
        internal void Observe((ClipState State, byte[]? Png) read)
        {
            TaskCompletionSource<bool> wake;
            lock (_gate)
            {
                var state = read.State with { Seq = _current.Seq + 1 };
                _current = state;
                _png = read.Png;
                wake = _changed;
                _changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            wake.TrySetResult(true);
        }

        public async Task<ClipState> WaitAsync(long since, TimeSpan timeout, CancellationToken token)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(timeout);
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_current.Seq > since) return _current;
                    changed = _changed.Task;
                }
                try { await changed.WaitAsync(limit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Current; }
            }
        }

        // MARK: Writing

        /// <summary>Runs on the STA thread, then waits for the change it made to arrive; returns its seq.</summary>
        private async Task<long> Write(Func<bool> set, string refusal)
        {
            long before = Current.Seq;
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_work) _work.Enqueue(() =>
            {
                try { done.TrySetResult(set()); }
                catch (Exception e) { done.TrySetException(e); }
            });
            _window?.Wake();
            if (!await done.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
                throw new ConnectException(500, refusal);
            return (await WaitAsync(before, TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false)).Seq;
        }

        public Task<long> SetTextAsync(string text) =>
            Write(() => ClipboardInterop.SetText(text), "Another program is holding the clipboard; try again.");

        public Task<long> SetFilesAsync(IReadOnlyList<string> paths) =>
            Write(() => ClipboardInterop.SetFiles(paths.ToArray(), cut: false), "Another program is holding the clipboard; try again.");

        public Task<long> SetImageAsync(byte[] image)
        {
            Bitmap bitmap;
            try
            {
                using var input = new MemoryStream(image);
                using var decoded = Image.FromStream(input);
                bitmap = new Bitmap(decoded);
            }
            catch { throw new ConnectException(400, "That is not a PNG or JPEG image."); }

            byte[] png;
            using (var output = new MemoryStream()) { bitmap.Save(output, ImageFormat.Png); png = output.ToArray(); }

            return Write(() =>
            {
                try
                {
                    // Both: a bitmap for everything that pastes a DIB, and PNG for what keeps transparency.
                    var data = new DataObject();
                    data.SetImage(bitmap);
                    var stream = new MemoryStream(png);
                    data.SetData("PNG", false, stream);
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        try
                        {
                            stream.Position = 0;
                            Clipboard.SetDataObject(data, copy: true);
                            return true;
                        }
                        catch (ExternalException) { Thread.Sleep(60); }
                    }
                    return false;
                }
                finally { bitmap.Dispose(); }
            }, "Another program is holding the clipboard; try again.");
        }

        private void Drain()
        {
            while (true)
            {
                Action? next;
                lock (_work) next = _work.Count > 0 ? _work.Dequeue() : null;
                if (next == null) return;
                next();
            }
        }

        public void Dispose()
        {
            lock (_work) _work.Enqueue(() =>
            {
                try { _window?.Close(); } catch { }
                Application.ExitThread();
            });
            _window?.Wake();
        }

        /// <summary>The message-only window the listener is registered on, owned by the STA thread.</summary>
        private sealed class Listener : NativeWindow
        {
            private const int WM_CLIPBOARDUPDATE = 0x031D;
            private const int WM_WORK = 0x8000 + 0x31;
            private static readonly IntPtr HWND_MESSAGE = new(-3);

            private readonly ConnectClipboard _owner;

            public Listener(ConnectClipboard owner)
            {
                _owner = owner;
                CreateHandle(new CreateParams { Parent = HWND_MESSAGE, Caption = "Explorer Native Connect clipboard" });
                if (!AddClipboardFormatListener(Handle))
                    throw new InvalidOperationException("the clipboard listener could not be registered");
            }

            public void Wake()
            {
                try { PostMessage(Handle, WM_WORK, IntPtr.Zero, IntPtr.Zero); } catch { }
            }

            public void Close()
            {
                try { RemoveClipboardFormatListener(Handle); } catch { }
                DestroyHandle();
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_CLIPBOARDUPDATE) { _owner.Observe(Read()); return; }
                if (m.Msg == WM_WORK) { _owner.Drain(); return; }
                base.WndProc(ref m);
            }

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool AddClipboardFormatListener(IntPtr hwnd);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

            [DllImport("user32.dll")]
            private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        }
    }
}
