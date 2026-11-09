using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace ExplorerNative
{
    /// <summary>
    /// Apple Music plays through Microsoft WebView2. Most PCs have it, but not
    /// all, and without it every song failed with "a compatible WebView2
    /// Runtime installation could not be found". Now Apple Music does not open
    /// until it is installed: each time it is opened the person is asked, and
    /// Yes downloads Microsoft's own small installer and runs it, in a window
    /// with a progress bar.
    /// </summary>
    internal static class WebView2Setup
    {
        // Microsoft's Evergreen bootstrapper. It installs for this user when
        // the app is not elevated, so no admin prompt is needed.
        private const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        private const int InstallMostMs = 10 * 60 * 1000;

        private static SetupWindow? _window;

        /// <summary>True while the download or the install is running.</summary>
        public static bool Installing => _window != null;

        /// <summary>True when a WebView2 runtime is installed.</summary>
        public static bool IsInstalled()
        {
            try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
            catch (WebView2RuntimeNotFoundException) { return false; }
            catch (Exception) { return true; }   // some other trouble: let the player say what it is
        }

        /// <summary>
        /// On the UI thread, each time Apple Music is opened without WebView2.
        /// Apple Music stays shut either way; once the install finishes it says
        /// so, and the next open goes in.
        /// </summary>
        public static void Offer(IWin32Window owner, Action<string, bool> announce)
        {
            if (_window != null)
            {
                announce("WebView2 is still installing. Apple Music opens once it's done", false);
                _window.Activate();
                return;
            }

            var answer = MessageBox.Show(owner,
                "Apple Music needs Microsoft WebView2, and it isn't installed on this PC. " +
                "Do you want to download and install it now? It's free from Microsoft and takes about a minute.",
                "Install WebView2", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
            if (answer != DialogResult.Yes)
            {
                announce("Apple Music can't open until WebView2 is installed", false);
                return;
            }

            var window = new SetupWindow();
            _window = window;
            window.FormClosed += (_, _) => { if (_window == window) _window = null; };
            window.Show();
            _ = RunAsync(window, owner, announce);
        }

        private static async Task RunAsync(SetupWindow window, IWin32Window owner, Action<string, bool> announce)
        {
            string? failure = null;
            string? setup = null;
            try
            {
                var folder = Path.Combine(Path.GetTempPath(), "ExplorerNative-WebView2");
                Directory.CreateDirectory(folder);
                setup = Path.Combine(folder, "MicrosoftEdgeWebview2Setup.exe");
                window.Downloading(0);
                await DownloadAsync(setup, window, window.Token).ConfigureAwait(true);
                // Cancel pressed as the last bytes came in still means cancel.
                window.Token.ThrowIfCancellationRequested();

                window.InstallingNow();
                int code = await Task.Run(() => RunInstaller(setup)).ConfigureAwait(true);

                if (!IsInstalled())
                    failure = code == 0
                        ? "The installer finished, but WebView2 still isn't there. Try again, or restart the PC first."
                        : $"Microsoft's installer stopped with error code 0x{code:X8}.";
            }
            catch (OperationCanceledException) when (window.Token.IsCancellationRequested)
            {
                failure = "";   // cancelled: nothing to explain
            }
            catch (HttpRequestException ex)
            {
                failure = "It couldn't be downloaded. Check the internet connection. " + GoogleNet.Reason(ex);
            }
            catch (OperationCanceledException)
            {
                failure = "The download took too long. Check the internet connection and try again.";
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                failure = "Windows asked for permission and it was refused.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
                                       or InvalidOperationException or TimeoutException)
            {
                failure = ex.Message;
            }
            finally
            {
                if (setup != null) try { File.Delete(setup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                window.Finish();
            }

            if (failure == null)
                announce("WebView2 is installed. You can open Apple Music now", false);
            else if (failure.Length == 0)
                announce("WebView2 install cancelled", false);
            else
            {
                announce("WebView2 couldn't be installed. " + failure, true);
                MessageBox.Show(owner, "WebView2 couldn't be installed. " + failure +
                    "\n\nOpen Apple Music again to try once more.",
                    "Install WebView2", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static async Task DownloadAsync(string path, SetupWindow window, CancellationToken cancel)
        {
            // The client's timeout ends with the headers: this covers the
            // file itself, so a connection that stalls part way is said.
            var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromMinutes(5));
            var token = limit.Token;
            using var http = new HttpClient(GoogleNet.Handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await http.GetAsync(BootstrapperUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Microsoft's site answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            long? total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(true);
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long got = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(true)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(true);
                got += read;
                if (total is > 0) window.Downloading((int)(got * 100 / total.Value));
            }
            if (got == 0) throw new HttpRequestException("Microsoft's site sent an empty file.");
        }

        private static int RunInstaller(string setup)
        {
            using var process = Process.Start(new ProcessStartInfo(setup, "/silent /install")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("The installer didn't start.");
            if (!process.WaitForExit(InstallMostMs))
                throw new TimeoutException("The installer took more than ten minutes.");
            return process.ExitCode;
        }

        /// <summary>The progress window: a sentence, a bar, and Cancel while downloading.</summary>
        private sealed class SetupWindow : Form
        {
            private readonly Label _label;
            private readonly ProgressBar _bar;
            private readonly Button _cancel;
            private readonly CancellationTokenSource _cts = new();
            private bool _done;
            private int _shown = -1;

            public CancellationToken Token => _cts.Token;

            public SetupWindow()
            {
                Text = "Installing WebView2";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = true;
                StartPosition = FormStartPosition.CenterScreen;
                ShowInTaskbar = true;
                AutoScaleMode = AutoScaleMode.Font;
                Padding = new Padding(12);

                var layout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                };
                _label = new Label { AutoSize = true, MaximumSize = new Size(420, 0), Text = "Starting", Margin = new Padding(0, 0, 0, 8) };
                _bar = new ProgressBar
                {
                    Width = 420, Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous,
                    AccessibleName = "WebView2 progress", TabStop = true, Margin = new Padding(0, 0, 0, 8),
                };
                _cancel = new Button { Text = "Cancel", AutoSize = true, Anchor = AnchorStyles.Right };
                _cancel.Click += (_, _) => CancelDownload();
                layout.Controls.Add(_label, 0, 0);
                layout.Controls.Add(_bar, 0, 1);
                layout.Controls.Add(_cancel, 0, 2);
                Controls.Add(layout);
                CancelButton = _cancel;
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                _bar.Focus();
            }

            public void Downloading(int percent)
            {
                percent = Math.Clamp(percent, 0, 100);
                if (percent == _shown) return;
                _shown = percent;
                _bar.Value = percent;
                _label.Text = $"Downloading WebView2 from Microsoft, {percent} percent";
                _bar.AccessibleDescription = _label.Text;
            }

            public void InstallingNow()
            {
                _cancel.Visible = false;   // Microsoft's installer can't be stopped part way
                CancelButton = null;
                _bar.Style = ProgressBarStyle.Marquee;
                _label.Text = "Installing WebView2. This takes about a minute";
                _bar.AccessibleDescription = _label.Text;
                _bar.Focus();
            }

            public void Finish()
            {
                _done = true;
                Close();
            }

            private void CancelDownload()
            {
                if (!_cancel.Visible) return;
                _label.Text = "Cancelling";
                _cts.Cancel();
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                // Closing while downloading cancels; while installing it waits,
                // because the installer goes on whatever happens to this window.
                if (!_done && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    if (_cancel.Visible) CancelDownload();
                }
                base.OnFormClosing(e);
            }
        }
    }
}
