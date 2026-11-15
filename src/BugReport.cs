using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ExplorerNative
{
    /// <summary>
    /// ExplorerNative --do bug-report: one plain text file to attach to a bug
    /// report. The version, Windows build and architecture, a summary of the
    /// settings (secrets only say set or not set, as --do settings does), and
    /// the last lines of each log, decrypted, with tokens, keys, passwords and
    /// sign-ins taken out.
    ///
    /// Run by the copy that was launched, not sent down the pipe, so it works
    /// when the running copy is stuck or has crashed, which is when it is needed.
    /// </summary>
    internal static class BugReport
    {
        public const string CommandName = "bug-report";

        /// <summary>The logs read, and how many of their last lines are kept.</summary>
        public static readonly (string Name, int Lines)[] Logs =
        {
            (Diag.FileName, 300),
            ("errors.old.log", 100),
            ("crash.log", 80),
            ("install.log", 200),
        };

        public const string Removed = "[removed]";

        /// <summary>
        /// Writes the report and returns its path: the path given, or a dated
        /// file on the desktop.
        /// </summary>
        public static string WriteFile(string[] arguments, Settings? settings = null)
        {
            var path = arguments.Length > 0 && arguments[0].Trim().Length > 0
                ? Path.GetFullPath(arguments[0].Trim())
                : DefaultPath(DateTime.Now);
            if (Directory.Exists(path)) path = Path.Combine(path, Path.GetFileName(DefaultPath(DateTime.Now)));
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            try { Diag.Flush(); } catch { }
            File.WriteAllText(path, Build(settings ?? Settings.Load(), Settings.AppDataDir, DateTime.Now), new UTF8Encoding(false));
            return path;
        }

        public static string DefaultPath(DateTime now)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
                desktop = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(desktop, $"Explorer Native bug report {now:yyyy-MM-dd HHmm}.txt");
        }

        /// <summary>The whole report as text.</summary>
        public static string Build(Settings settings, string appData, DateTime now)
        {
            var secrets = SecretValues(settings, appData);
            var text = new StringBuilder();
            text.AppendLine("Explorer Native bug report");
            text.AppendLine($"Written {now:yyyy-MM-dd HH:mm:ss} ({TimeZoneInfo.Local.StandardName})");
            text.AppendLine();
            text.AppendLine("About this PC");
            foreach (var line in AboutLines()) text.AppendLine(line);
            text.AppendLine();

            text.AppendLine("Settings (passwords, sign-ins and keys only say set or not set)");
            string list;
            try { list = SettingCommands.List(settings); }
            catch (Exception ex) { list = "Could not list the settings: " + ex.GetType().Name; }
            // The first lines of the list explain how to use --do setting; not wanted here.
            foreach (var line in list.Split('\n').Select(l => l.TrimEnd('\r')).SkipWhile(l => !l.Contains(" = ")))
                // Secret settings already say only set or not set; the rest may still hold a sign-in.
                text.AppendLine(Scrub(line, secrets, byName: false));
            text.AppendLine();

            foreach (var (name, keep) in Logs)
            {
                var path = Path.Combine(appData, name);
                if (!File.Exists(path)) continue;
                text.AppendLine($"Last lines of {name}");
                try
                {
                    var lines = ProtectedFile.ReadLog(path).ToList();
                    if (lines.Count > keep) text.AppendLine($"({lines.Count - keep} earlier lines left out)");
                    foreach (var line in lines.Skip(Math.Max(0, lines.Count - keep)))
                        text.AppendLine(Scrub(line, secrets));
                }
                catch (Exception ex)
                {
                    text.AppendLine("Could not read it: " + ex.GetType().Name);
                }
                text.AppendLine();
            }
            return text.ToString();
        }

        public static IEnumerable<string> AboutLines()
        {
            yield return "Version: " + Updater.Text(Updater.Current);
            yield return "Windows: " + WindowsDescription();
            yield return "Architecture: " + RuntimeInformation.ProcessArchitecture +
                         " (Windows is " + RuntimeInformation.OSArchitecture + ")";
            yield return ".NET: " + RuntimeInformation.FrameworkDescription;
            yield return "Processors: " + Environment.ProcessorCount;
            string? exe = null;
            try { exe = Environment.ProcessPath; } catch { }
            yield return "Program: " + (exe == null ? "unknown" : string.Equals(Path.GetDirectoryName(exe),
                AppInstall.Directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                ? "the installed copy" : "a copy outside the install folder");
        }

        public static string WindowsDescription()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var product = key?.GetValue("ProductName") as string ?? "Windows";
                var display = key?.GetValue("DisplayVersion") as string;
                var build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
                var ubr = key?.GetValue("UBR") is int u ? "." + u : "";
                // Windows 11 still says "Windows 10" in ProductName.
                if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
                return $"{product}{(display != null ? " " + display : "")}, build {build}{ubr}";
            }
            catch
            {
                return Environment.OSVersion.VersionString;
            }
        }

        /// <summary>The values of every secret setting and the Google client secret, to take out wherever they appear.</summary>
        internal static List<string> SecretValues(Settings settings, string appData)
        {
            var found = new List<string>();
            foreach (var setting in typeof(Settings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (setting.PropertyType != typeof(string) || !setting.CanRead || setting.GetIndexParameters().Length > 0) continue;
                if (!SettingCommands.IsSecret(setting.Name) && !setting.Name.EndsWith("HostKey", StringComparison.Ordinal) &&
                    !setting.Name.EndsWith("Certificate", StringComparison.Ordinal)) continue;
                try
                {
                    if (setting.GetValue(settings) is string { Length: >= 4 } value) found.Add(value);
                }
                catch { }
            }
            try
            {
                var client = Path.Combine(appData, "client_secret.json");
                if (File.Exists(client))
                {
                    var json = ProtectedFile.ReadAllText(client);
                    foreach (Match m in Regex.Matches(json, "\"client_secret\"\\s*:\\s*\"([^\"]+)\""))
                        found.Add(m.Groups[1].Value);
                }
            }
            catch { }
            return found.Where(v => v.Trim().Length >= 4).Distinct().OrderByDescending(v => v.Length).ToList();
        }

        private static readonly Regex[] Patterns =
        {
            // Google: access tokens, refresh tokens, client secrets, API keys.
            new(@"ya29\.[A-Za-z0-9_\-\.]+", RegexOptions.Compiled),
            new(@"1//[A-Za-z0-9_\-]{10,}", RegexOptions.Compiled),
            new(@"GOCSPX-[A-Za-z0-9_\-]+", RegexOptions.Compiled),
            new(@"AIza[0-9A-Za-z_\-]{20,}", RegexOptions.Compiled),
            // AI providers and other bearer keys.
            new(@"\bsk-[A-Za-z0-9_\-]{10,}", RegexOptions.Compiled),
            new(@"(?i)\bbearer\s+[A-Za-z0-9_\-\.=+/]+", RegexOptions.Compiled),
            // JWTs (Apple Music's developer token and others).
            new(@"eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]+", RegexOptions.Compiled),
        };

        /// <summary>name=value or "name": "value" where the name says it is a secret.</summary>
        private static readonly Regex Named = new(
            "(?i)(\"?\\b(?:[a-z_\\-]*(?:password|passwd|pwd|secret|token|api[_\\-]?key|apikey|access[_\\-]?key|cookie|authorization|code_verifier|auth_code|connectcode|pairing_?code))\\b\"?\\s*[:=]\\s*)(?!(?:set|not|yes|no|is|now|back|\\[removed\\])\\b)(\"[^\"]*\"|[^\\s,;&]+)",
            RegexOptions.Compiled);

        /// <summary>Query string values that carry a key.</summary>
        private static readonly Regex QueryKey = new(
            "(?i)([?&](?:key|access_token|refresh_token|token|code|client_secret|sig|signature)=)[^&\\s\"]+",
            RegexOptions.Compiled);

        /// <summary>Email addresses: a sign-in is usually one.</summary>
        private static readonly Regex Email = new(
            @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

        /// <summary>A line with tokens, keys, passwords and sign-ins taken out.</summary>
        public static string Scrub(string line, IReadOnlyList<string>? secrets = null, bool byName = true)
        {
            if (string.IsNullOrEmpty(line)) return line;
            if (secrets != null)
                foreach (var secret in secrets)
                    if (secret.Length >= 4) line = line.Replace(secret, Removed, StringComparison.Ordinal);
            foreach (var pattern in Patterns) line = pattern.Replace(line, Removed);
            line = QueryKey.Replace(line, m => m.Groups[1].Value + Removed);
            if (byName) line = Named.Replace(line, m => m.Groups[2].Value.StartsWith('"')
                ? m.Groups[1].Value + "\"" + Removed + "\""
                : m.Groups[1].Value + Removed);
            line = Email.Replace(line, "[email removed]");
            return line;
        }
    }
}
