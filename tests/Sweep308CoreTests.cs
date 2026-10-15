using System;
using System.IO;
using System.Linq;
using System.Text;

namespace ExplorerNative
{
    /// <summary>
    /// The 3.0.8 sweep of the settings and core files: a settings file that
    /// would not decrypt being merged as defaults, a stray temp file left by the
    /// re-encryption of a plain file, device names with a space before the
    /// extension, and Preferences speaking with spoken announcements turned off.
    /// </summary>
    internal static class Sweep308CoreTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        private static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 3.0.8, settings and core:");

            var previous = Settings.OverrideAppDataDir;
            try
            {
                UndecryptableFileIsNotMergedAsDefaults();
                ReEncryptionLeavesNoTempFile();
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
            }

            DeviceNamesWithASpaceBeforeTheExtension();
            PreferencesHonoursTheMasterSpeechSwitch();

            Console.WriteLine();
        }

        private static string NewSettingsDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-sweep308-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Settings.OverrideAppDataDir = dir;
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        /// <summary>
        /// If settings.json stopped decrypting while the application ran, a reload
        /// (a command-line flag, or the last save on the way out) read it as the
        /// defaults and merged that over the running copy: every setting went back
        /// to its default and was then saved.
        /// </summary>
        private static void UndecryptableFileIsNotMergedAsDefaults()
        {
            var dir = NewSettingsDir();
            try
            {
                var path = Path.Combine(dir, "settings.json");
                new Settings { FontSize = 14, SetAsDefaultFileExplorer = true, WindowTitle = "Mine" }.Save();
                var live = Settings.Load();
                Check("undecryptable: the file loads as saved", live.FontSize == 14 && live.SetAsDefaultFileExplorer);

                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("ENDPAPI1this is not a DPAPI blob"));
                var merged = Settings.ReloadOnto(live);
                Check("undecryptable: a reload keeps the running settings rather than the defaults",
                    merged.FontSize == 14 && merged.SetAsDefaultFileExplorer && merged.WindowTitle == "Mine",
                    $"FontSize {merged.FontSize}, takeover {merged.SetAsDefaultFileExplorer}, title {merged.WindowTitle}");

                // Unchanged: the next save may still replace it, and a copy is kept.
                var loaded = Settings.Load(out bool failed);
                Check("undecryptable: Load still lets the next save replace it",
                    !failed && !loaded.UnreadableOnDisk);
                Check("undecryptable: and a copy is kept beside it",
                    Directory.GetFiles(dir).Any(f => Path.GetFileName(f).Contains(".undecryptable-")));
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// A plain settings.json left by an older build is encrypted when it is
        /// read. If another program held it at that moment the swap failed and
        /// settings.json.&lt;pid&gt;.enc.tmp stayed in AppData for good.
        /// </summary>
        private static void ReEncryptionLeavesNoTempFile()
        {
            var dir = NewSettingsDir();
            try
            {
                var path = Path.Combine(dir, "settings.json");
                File.WriteAllText(path, "{\"FontSize\": 12}");

                Settings loaded;
                // Read-shared, not delete-shared: the read works, the swap cannot.
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    loaded = Settings.Load();

                Equal("re-encrypt: the plain file still reads", "12", loaded.FontSize.ToString());
                var strays = Directory.GetFiles(dir).Where(f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)).ToList();
                Check("re-encrypt: a failed swap leaves no temp file behind", strays.Count == 0,
                    string.Join(", ", strays.Select(Path.GetFileName)));
            }
            finally { Cleanup(dir); }
        }

        /// <summary>
        /// Windows drops spaces at the end of a name's stem before it looks for a
        /// device, so "CON .txt" was accepted as a name and then wrote to the
        /// console rather than to a file.
        /// </summary>
        private static void DeviceNamesWithASpaceBeforeTheExtension()
        {
            Check("device name: CON .txt is refused", NameRules.DescribeBadName("CON .txt") != null);
            Check("device name: nul  .tar.gz is refused", NameRules.DescribeBadName("nul  .tar.gz") != null);
            Check("device name: CON.txt is still refused", NameRules.DescribeBadName("CON.txt") != null);
            Check("device name: Console .txt is fine", NameRules.DescribeBadName("Console .txt") == null);
            Check("device name: a name with an inner space is fine", NameRules.DescribeBadName("COM 1.txt") == null);
        }

        /// <summary>
        /// With "Enable spoken announcements" off, the Windows notifications and
        /// web app buttons in Preferences still spoke, because they called the
        /// speech queue directly. Everything Preferences says goes through its one
        /// Say, which asks the master switch.
        /// </summary>
        private static void PreferencesHonoursTheMasterSpeechSwitch()
        {
            var source = SourceFile("SettingsForm.cs");
            if (source == null)
            {
                Check("speech switch: SettingsForm.cs found", false);
                return;
            }

            int speaks = 0;
            for (int at = source.IndexOf("Speech.Speak(", StringComparison.Ordinal); at >= 0;
                 at = source.IndexOf("Speech.Speak(", at + 1, StringComparison.Ordinal))
                speaks++;

            int say = source.IndexOf("private void Say(string message)", StringComparison.Ordinal);
            var sayBody = say >= 0 ? source.Substring(say, Math.Min(200, source.Length - say)) : "";
            Check("speech switch: Preferences speaks only through Say",
                speaks == 1 && sayBody.Contains("Speech.Speak(") && sayBody.Contains("SpeakEnabled"),
                speaks + " direct calls");
        }

        private static string? SourceFile(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            if (dir == null) return null;
            var path = Path.Combine(dir.FullName, "src", name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }
}
