using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace ExplorerNative
{
    /// <summary>
    /// Apple Music plays through Microsoft WebView2. Most PCs have it, but not
    /// all, and without it every song failed with "a compatible WebView2
    /// Runtime installation could not be found". Now going into Apple Music
    /// offers to install it, using Microsoft's own small installer.
    /// </summary>
    internal static class WebView2Setup
    {
        // Microsoft's Evergreen bootstrapper. It installs for this user when
        // the app is not elevated, so no admin prompt is needed.
        private const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        private static bool _askedThisRun;
        private static bool _installing;

        /// <summary>True when a WebView2 runtime is installed.</summary>
        public static bool IsInstalled()
        {
            try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
            catch (WebView2RuntimeNotFoundException) { return false; }
            catch (Exception) { return true; }   // some other trouble: let the player say what it is
        }

        /// <summary>
        /// On the UI thread, when going into Apple Music. Asks once a run; a
        /// No is kept until the app restarts, and browsing carries on either way.
        /// </summary>
        public static async Task OfferAsync(IWin32Window owner, Action<string, bool> announce)
        {
            if (_askedThisRun || _installing || IsInstalled()) return;
            _askedThisRun = true;

            var answer = MessageBox.Show(owner,
                "Apple Music needs Microsoft WebView2 to play songs, and it isn't installed on this PC. " +
                "Do you want to download and install it now? It's free from Microsoft and takes about a minute.",
                "Install WebView2", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                announce("You can browse Apple Music, but songs won't play until WebView2 is installed", false);
                return;
            }

            _installing = true;
            try
            {
                announce("Downloading WebView2", false);
                var folder = Path.Combine(Path.GetTempPath(), "ExplorerNative-WebView2");
                Directory.CreateDirectory(folder);
                var setup = Path.Combine(folder, "MicrosoftEdgeWebview2Setup.exe");
                using (var http = new HttpClient(GoogleNet.Handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) })
                {
                    var bytes = await http.GetByteArrayAsync(BootstrapperUrl).ConfigureAwait(true);
                    await File.WriteAllBytesAsync(setup, bytes).ConfigureAwait(true);
                }

                announce("Installing WebView2. This takes about a minute", false);
                int code = await Task.Run(() =>
                {
                    using var process = Process.Start(new ProcessStartInfo(setup, "/silent /install")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }) ?? throw new InvalidOperationException("the installer didn't start");
                    if (!process.WaitForExit(10 * 60 * 1000))
                        throw new TimeoutException("the installer took more than ten minutes");
                    return process.ExitCode;
                }).ConfigureAwait(true);
                try { File.Delete(setup); } catch (IOException) { } catch (UnauthorizedAccessException) { }

                if (IsInstalled())
                    announce("WebView2 is installed. Apple Music can play songs now", false);
                else
                    announce($"WebView2 didn't install. The installer stopped with code {code}", true);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or UnauthorizedAccessException or InvalidOperationException or TimeoutException
                                       or System.ComponentModel.Win32Exception)
            {
                // Asked again next time, since the person did want it.
                _askedThisRun = false;
                announce("WebView2 couldn't be installed: " + ex.Message, true);
            }
            finally { _installing = false; }
        }
    }
}
