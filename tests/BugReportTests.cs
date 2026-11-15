using System;
using System.IO;
using System.Linq;

namespace ExplorerNative
{
    /// <summary>
    /// ExplorerNative --do bug-report (7.1.0). Group "bugreport".
    /// </summary>
    internal static class BugReportTests
    {
        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            Scrubbing(check, equal);
            WholeReport(check);
            Wiring(check);
        }

        private static void Scrubbing(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            string S(string line) => BugReport.Scrub(line);

            check("bug report: a Google access token is taken out",
                !S("GET files?x=1 Authorization ya29.a0AfH6SMBx-abc_def.ghi failed").Contains("ya29"), null);
            check("bug report: a Google refresh token is taken out",
                !S("refresh 1//0gAbCdEfGhIjKlMnOp-qr failed").Contains("1//0g"), null);
            check("bug report: a Google client secret is taken out",
                !S("client GOCSPX-abcdefghijklmnop rejected").Contains("GOCSPX"), null);
            check("bug report: an AI key is taken out",
                !S("key sk-ant-api03-abcdefghijklmnopqrst refused").Contains("sk-ant"), null);
            check("bug report: a bearer header is taken out",
                !S("Authorization: Bearer abc.def.ghi").Contains("abc.def"), null);
            check("bug report: a JWT is taken out",
                !S("token eyJhbGciOiJFUzI1NiJ9.eyJpc3MiOiJBQkMifQ.c2lnbmF0dXJl here").Contains("eyJ"), null);
            equal("bug report: password=value keeps the name and loses the value",
                "sign-in password=[removed] for x", S("sign-in password=hunter22 for x"));
            equal("bug report: a setting that only says set or not set is left as it is",
                "ftp-server-password = not set  smb-server-password = set",
                S("ftp-server-password = not set  smb-server-password = set"));
            equal("bug report: a JSON secret keeps the name and loses the value",
                "{\"refresh_token\": \"[removed]\", \"n\": 1}", S("{\"refresh_token\": \"1abc\", \"n\": 1}"));
            equal("bug report: a key in a query string is taken out",
                "https://x.test/v1?alt=json&key=[removed]&q=1", S("https://x.test/v1?alt=json&key=AbCd1234&q=1"));
            equal("bug report: an email address (a sign-in) is taken out",
                "signed in as [email removed]", S("signed in as someone.else@example.com"));
            equal("bug report: ordinary lines are left alone",
                "2026-11-15 10:00:00  DriveMonitor.Pass: IOException (0x80070020): in use, exit code 5",
                S("2026-11-15 10:00:00  DriveMonitor.Pass: IOException (0x80070020): in use, exit code 5"));
            equal("bug report: a secret setting's value is taken out wherever it appears",
                "connect with [removed] now", BugReport.Scrub("connect with Tr0ub4dor&3 now", new[] { "Tr0ub4dor&3" }));
        }

        private static void WholeReport(Action<string, bool, string?> check)
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-bugreport-" + Guid.NewGuid().ToString("N")[..8]);
            var was = Settings.OverrideAppDataDir;
            Directory.CreateDirectory(dir);
            try
            {
                Settings.OverrideAppDataDir = dir;
                var settings = new Settings { SmbServerPassword = "VerySecret99", SmbServerUser = "conner" };

                for (int i = 0; i < 400; i++)
                    ProtectedFile.AppendLine(Path.Combine(dir, "install.log"), $"line {i} ok");
                ProtectedFile.AppendLine(Path.Combine(dir, "install.log"), "oops the password VerySecret99 was typed here");
                ProtectedFile.AppendLine(Path.Combine(dir, Diag.FileName), "2026-11-15 10:00:00  X.Y: IOException: token=abc123");

                var text = BugReport.Build(settings, dir, new DateTime(2026, 11, 15, 10, 0, 0));
                check("bug report: says the version", text.Contains("Version: " + Updater.Text(Updater.Current)), null);
                check("bug report: says the Windows build", text.Contains("Windows: ") && text.Contains("build "), null);
                check("bug report: says the architecture", text.Contains("Architecture: "), null);
                check("bug report: lists the settings", text.Contains("smb-server-user = conner"), null);
                check("bug report: a secret setting only says set", text.Contains("smb-server-password = set"), null);
                check("bug report: the secret's value is nowhere in it", !text.Contains("VerySecret99"), null);
                check("bug report: the log is decrypted", text.Contains("line 399 ok"), null);
                check("bug report: only the last lines of a long log",
                    !text.Contains("line 150 ok") && text.Contains("earlier lines left out"), null);
                check("bug report: errors.log is in it, scrubbed",
                    text.Contains("X.Y: IOException: token=[removed]"), null);
                check("bug report: a log that is not there is not mentioned", !text.Contains("crash.log"), null);

                var file = BugReport.WriteFile(new[] { Path.Combine(dir, "report.txt") }, settings);
                check("bug report: written to the file asked for", File.Exists(file) && file.EndsWith("report.txt"), file);
                check("bug report: the file is plain text, not encrypted",
                    File.ReadAllText(file).StartsWith("Explorer Native bug report"), null);
            }
            finally
            {
                Settings.OverrideAppDataDir = was;
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        private static void Wiring(Action<string, bool, string?> check)
        {
            check("bug report: --commands lists it", Commands.Find(BugReport.CommandName) != null, null);
            check("bug report: --help mentions it", LaunchArguments.HelpText().Contains("bug-report"), null);
            var program = File.ReadAllText(Path.Combine(SourceDir(), "Program.cs"));
            int local = program.IndexOf("BugReport.CommandName", StringComparison.Ordinal);
            int send = program.IndexOf("SendCommand(wanted.Name", StringComparison.Ordinal);
            check("bug report: written by the launched copy, before anything is sent to the running one",
                local > 0 && send > local, null);
        }

        private static string SourceDir()
        {
            var at = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && at != null; i++)
            {
                var src = Path.Combine(at, "src");
                if (File.Exists(Path.Combine(src, "Program.cs"))) return src;
                at = Path.GetDirectoryName(at.TrimEnd(Path.DirectorySeparatorChar));
            }
            return Path.Combine(Directory.GetCurrentDirectory(), "src");
        }
    }
}
