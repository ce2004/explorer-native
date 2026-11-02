using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Installing WinFsp for somebody who turns Google Drive on without it.
    ///
    /// WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos,
    /// https://github.com/winfsp/winfsp. Its own signed installer, fetched from
    /// its own release page and checked against the fingerprint of the copy
    /// this release was tested with, then run with Windows' permission prompt.
    /// Nothing of WinFsp is shipped inside Explorer Native.
    /// </summary>
    internal static class WinFspSetup
    {
        internal const string Version = WinFsp.BundledVersion;
        internal const string Url = "https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi";
        internal const string Sha256 = "073A70E00F77423E34BED98B86E600DEF93393BA5822204FAC57A29324DB9F7A";

        /// <summary>The credit WinFsp asks for wherever it is used, said and shown in Preferences.</summary>
        internal const string Credit = "WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos. " +
                                       "https://github.com/winfsp/winfsp";

        /// <summary>
        /// Downloads and installs WinFsp. Null when it is installed afterwards;
        /// otherwise why not, in words for the person who asked.
        /// </summary>
        public static async Task<string?> InstallAsync(Action<string>? log, CancellationToken token)
        {
            if (WinFsp.Installed) return null;
            var path = Path.Combine(Path.GetTempPath(), $"ExplorerNative-winfsp-{Version}.msi");
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("ExplorerNative");
                    var bytes = await http.GetByteArrayAsync(Url, token).ConfigureAwait(false);
                    var hash = Convert.ToHexString(SHA256.HashData(bytes));
                    if (!hash.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        log?.Invoke($"WinFsp download fingerprint {hash} does not match");
                        return "the download was not the WinFsp installer it should have been";
                    }
                    await File.WriteAllBytesAsync(path, bytes, token).ConfigureAwait(false);
                }

                int code;
                try
                {
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        // Full path and System32 as the working folder: a bare
                        // msiexec.exe is looked for in the current folder first,
                        // and this one is about to be elevated.
                        FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                        WorkingDirectory = Environment.SystemDirectory,
                        Arguments = $"/i \"{path}\" /passive /norestart",
                        UseShellExecute = true,
                        Verb = "runas",
                    });
                    if (process == null) return "the installer did not start";
                    await process.WaitForExitAsync(token).ConfigureAwait(false);
                    code = process.ExitCode;
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    return "Windows was not given permission to install it";
                }

                log?.Invoke($"WinFsp installer finished with {code}");
                if (code == 1602) return "the installer was cancelled";
                if (code != 0 && code != 3010) return $"the installer stopped with code {code}";
                return WinFsp.Installed ? null : "the installer finished, but WinFsp is still not there";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log?.Invoke("WinFsp install: " + ex.Message);
                return GoogleNet.Reason(ex);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
