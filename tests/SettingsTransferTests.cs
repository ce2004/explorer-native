using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ExplorerNative
{
    /// <summary>
    /// Export and import of settings (Preferences and the File menu): plain JSON
    /// that comes back as the same settings, nothing secret or this-PC-only in
    /// it, anything that is not settings refused, and an import read through the
    /// same salvage and repair as settings.json.
    /// </summary>
    internal static class SettingsTransferTests
    {
        public static void RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var dir = Path.Combine(Path.GetTempPath(), "en-settings-transfer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var before = Settings.OverrideAppDataDir;
            Settings.OverrideAppDataDir = dir;
            try
            {
                RoundTrip(check, equal);
                Refusals(check, dir);
                Validation(check, equal);
                Pairs(check, equal);
                Messages(check);
            }
            finally
            {
                Settings.OverrideAppDataDir = before;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static IEnumerable<System.Reflection.PropertyInfo> PersistedProperties() =>
            typeof(Settings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 &&
                            !Attribute.IsDefined(p, typeof(System.Text.Json.Serialization.JsonIgnoreAttribute)) &&
                            p.Name != nameof(Settings.UnknownSettings));

        private static string ValueOf(System.Reflection.PropertyInfo p, Settings s) =>
            JsonSerializer.Serialize(p.GetValue(s), Settings.PlainFormat);

        private static Settings Unusual() => new()
        {
            FontSize = 14,
            WindowTitle = "Music room",
            AudioVolumePercent = 47,
            AudioPlayPauseShortcut = "Ctrl+Alt+P",
            SpeechOverrides = "nav.tab=0",
            GoogleDriveLetter = "K",
            ConnectCode = "73914562",
            RegisteredExePath = @"C:\Somewhere\ExplorerNative.exe",
            AudioOutputDeviceId = "{0.0.0.00000000}.{secret-device}",
            StartPath = @"E:\Start here",
            AppleMusicUserToken = "apple-token",
            WindowX = 1234,
            DriveSyncPairs = new List<DriveSyncPair>
            {
                new() { Name = "Books é", LocalFolder = @"D:\Books", DriveFolderId = "abc", Mode = SyncMode.UploadOnly },
            },
        };

        private static void RoundTrip(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var original = Unusual();
            var json = original.ToExportJson();

            check("settings export: plain JSON, readable on any PC", json.TrimStart().StartsWith("{", StringComparison.Ordinal), json[..Math.Min(40, json.Length)]);
            check("settings export: says what it is first",
                json.TrimStart().TrimStart('{').TrimStart().StartsWith("\"" + Settings.ExportMarker + "\"", StringComparison.Ordinal), null);
            using (var doc = JsonDocument.Parse(json))
            {
                var missing = PersistedProperties().Select(p => p.Name)
                    .Where(n => !doc.RootElement.TryGetProperty(n, out _)).ToList();
                equal("settings export: every setting is in it", "", string.Join(",", missing));
                check("settings export: no Google client when there is none",
                    !doc.RootElement.TryGetProperty(Settings.ExportGoogleClient, out _), null);
            }
            check("settings export: names keep their accents, unescaped", json.Contains("Books é", StringComparison.Ordinal), null);

            var back = Settings.FromExportJson(json, out var refusal);
            check("settings import: an export reads back", back != null && refusal == null, refusal);
            if (back == null) return;

            var differ = PersistedProperties()
                .Where(p => !Settings.ThisPcOnly.Contains(p.Name) && p.Name != nameof(Settings.SettingsVersion))
                .Where(p => ValueOf(p, original) != ValueOf(p, back))
                .Select(p => $"{p.Name}: {ValueOf(p, original)} became {ValueOf(p, back)}").ToList();
            equal("settings import: every setting comes back as it was exported", "", string.Join("; ", differ));

            // This PC keeps its window and install record; every option comes in.
            var live = new Settings { ConnectCode = "11112222", WindowX = 50, RegisteredExePath = @"C:\Here\ExplorerNative.exe" };
            Settings.PrepareImport(live, back, _ => true);
            check("settings import: this PC keeps its own window place and program path",
                back.WindowX == 50 && back.RegisteredExePath == @"C:\Here\ExplorerNative.exe",
                $"{back.WindowX}, {back.RegisteredExePath}");
            check("settings import: and takes every option from the file, pairing code, sound device, start folder and Apple Music sign-in too",
                back.FontSize == 14 && back.WindowTitle == "Music room" && back.AudioPlayPauseShortcut == "Ctrl+Alt+P" &&
                back.ConnectCode == "73914562" && back.AudioOutputDeviceId == "{0.0.0.00000000}.{secret-device}" &&
                back.StartPath == @"E:\Start here" && back.AppleMusicUserToken == "apple-token",
                $"{back.ConnectCode}, {back.AudioOutputDeviceId}, {back.StartPath}, {back.AppleMusicUserToken}");

            // Every setting, one at a time, set to something other than its
            // default, survives export, import and PrepareImport: nothing is
            // left out by accident when a setting is added later.
            var lost = new List<string>();
            foreach (var p in PersistedProperties().Where(p => !Settings.ThisPcOnly.Contains(p.Name) && p.Name != nameof(Settings.SettingsVersion)))
            {
                var s = new Settings();
                if (!TrySetUnusual(p, s)) continue;
                var round = Settings.FromExportJson(s.ToExportJson(), out _);
                if (round == null) { lost.Add(p.Name + " (refused)"); continue; }
                Settings.PrepareImport(new Settings(), round, _ => true);
                if (ValueOf(p, s) != ValueOf(p, round)) lost.Add($"{p.Name}: {ValueOf(p, s)} became {ValueOf(p, round)}");
            }
            equal("settings import: each setting changed on its own comes back", "", string.Join("; ", lost));

            // The Google client typed into Preferences travels with the rest.
            var withClient = original.ToExportJson(("12345.apps.googleusercontent.com", "GOCSPX-secret"));
            var client = Settings.GoogleClientInExport(withClient);
            check("settings export: the Google client ID and secret are in it, and read back",
                client is { } c && c.ClientId == "12345.apps.googleusercontent.com" && c.ClientSecret == "GOCSPX-secret", null);
            var backWithClient = Settings.FromExportJson(withClient, out var why);
            check("settings import: the Google client is not taken for a setting",
                backWithClient != null && (backWithClient.UnknownSettings == null ||
                                           !backWithClient.UnknownSettings.ContainsKey(Settings.ExportGoogleClient)), why);
            check("settings import: a file without a client says so",
                Settings.GoogleClientInExport(json) == null && Settings.GoogleClientInExport("not json") == null, null);
        }

        /// <summary>
        /// Gives <paramref name="p"/> a value that is not its default and that
        /// Validated keeps. False for a type this does not know how to vary.
        /// </summary>
        private static bool TrySetUnusual(System.Reflection.PropertyInfo p, Settings s)
        {
            var now = p.GetValue(s);
            var t = p.PropertyType;
            object? value = null;
            if (t == typeof(bool)) value = !(bool)now!;
            else if (t == typeof(string)) value = p.Name.Contains("Shortcut", StringComparison.Ordinal) ? "" : "x" + p.Name;
            else if (t == typeof(int)) value = (int)now! + 1;
            else if (t == typeof(long)) value = (long)now! + 1;
            else if (t == typeof(double)) value = (double)now! + 0.5;
            else if (t.IsEnum)
            {
                var all = Enum.GetValues(t);
                if (all.Length < 2) return false;
                value = Equals(all.GetValue(0), now) ? all.GetValue(1) : all.GetValue(0);
            }
            else return false;
            p.SetValue(s, value);
            // What Validated would do to it, so a range clamp is not called a loss.
            var settled = s.Clone().Validated();
            if (ValueOf(p, settled) == ValueOf(p, new Settings())) return false;
            p.SetValue(s, p.GetValue(settled));
            return true;
        }

        private static void Refusals(Action<string, bool, string?> check, string dir)
        {
            // The real settings file, to show an import that is refused changes nothing.
            var saved = new Settings { FontSize = 11 };
            saved.Save();
            var path = Path.Combine(dir, "settings.json");
            var bytesBefore = File.ReadAllBytes(path);

            foreach (var (label, text) in new[]
                     {
                         ("words", "hello there"),
                         ("an array", "[1, 2, 3]"),
                         ("some other program's JSON", "{\"theme\": \"dark\", \"FontSize\": 20}"),
                         ("an empty file", ""),
                         ("a number", "42"),
                     })
            {
                var read = Settings.FromExportJson(text, out var refusal);
                check($"settings import: {label} is refused, with a sentence",
                    read == null && refusal == Settings.NotSettingsSentence, refusal);
            }
            check("settings import: a refused file leaves the settings file alone",
                File.ReadAllBytes(path).AsSpan().SequenceEqual(bytesBefore), null);

            // A settings.json still encrypted for this account is taken too.
            var encrypted = Path.Combine(dir, "copy.json");
            ProtectedFile.WriteAllText(encrypted, JsonSerializer.Serialize(new Settings { FontSize = 16 }, Settings.JsonFormat));
            var fromSaved = Settings.FromExportJson(Settings.ExportFileText(File.ReadAllBytes(encrypted)), out var why);
            check("settings import: a settings.json from this account is read too", fromSaved?.FontSize == 16, why);
        }

        private static void Validation(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            // Broken values are repaired or dropped one by one, never the whole file.
            var broken = "{ \"" + Settings.ExportMarker + "\": 1, \"SettingsVersion\": " + Settings.CurrentSettingsVersion + "," +
                         " \"FontSize\": 999, \"WindowTitle\": null, \"AudioSeekSeconds\": \"lots\", \"AudioVolumePercent\": 33," +
                         " // a comment somebody left\n \"DriveSyncPairs\": [null], }";
            var read = Settings.FromExportJson(broken, out var refusal);
            check("settings import: a file with broken values still imports", read != null, refusal);
            if (read != null)
            {
                check("settings import: a value out of range is brought into range", read.FontSize == 24, read.FontSize.ToString());
                check("settings import: a null title is given the default", read.WindowTitle == new Settings().WindowTitle, read.WindowTitle);
                check("settings import: a value of the wrong type is dropped on its own",
                    read.AudioSeekSeconds == new Settings().AudioSeekSeconds && read.AudioVolumePercent == 33,
                    $"{read.AudioSeekSeconds}, {read.AudioVolumePercent}");
                equal("settings import: an empty synced folder entry is dropped", "0", read.DriveSyncPairs.Count.ToString());
            }

            // A newer build's settings are kept for it, as settings.json does.
            var newer = "{ \"" + Settings.ExportMarker + "\": 1, \"SettingsVersion\": " + (Settings.CurrentSettingsVersion + 5) +
                        ", \"FutureSetting\": 7, \"FontSize\": 12 }";
            var kept = Settings.FromExportJson(newer, out var why);
            check("settings import: settings from a newer version are kept for it",
                kept?.UnknownSettings != null && kept.UnknownSettings.ContainsKey("FutureSetting") && kept.FontSize == 12, why);
            check("settings import: and saved with the rest",
                kept != null && JsonSerializer.Serialize(kept, Settings.JsonFormat).Contains("FutureSetting", StringComparison.Ordinal), null);

            // An older export is brought up to this version.
            var older = "{ \"" + Settings.ExportMarker + "\": 1, \"SettingsVersion\": 3, \"FontSize\": 12, \"RemovedLongAgo\": true }";
            var old = Settings.FromExportJson(older, out _);
            check("settings import: an older export is brought up to date",
                old != null && old.SettingsVersion == Settings.CurrentSettingsVersion && old.FontSize == 12 &&
                (old.UnknownSettings == null || !old.UnknownSettings.ContainsKey("RemovedLongAgo")),
                old?.SettingsVersion.ToString());
        }

        private static void Pairs(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var imported = new Settings
            {
                DriveSyncPairs = new List<DriveSyncPair>
                {
                    new() { Name = "Here", LocalFolder = @"C:\Here" },
                    new() { Name = "Elsewhere", LocalFolder = @"Q:\Not on this PC" },
                    new() { Name = "Already paused", LocalFolder = @"Q:\Also gone", Paused = true },
                    new() { Name = "No folder", LocalFolder = "" },
                },
            };
            int paused = Settings.PrepareImport(new Settings(), imported, p => p == @"C:\Here");
            equal("settings import: synced folders not on this PC are paused, and counted", "2", paused.ToString());
            check("settings import: the one whose folder is here carries on",
                !imported.DriveSyncPairs[0].Paused && imported.DriveSyncPairs[1].Paused && imported.DriveSyncPairs[3].Paused, null);
            check("settings import: a folder check that throws counts as not there",
                Settings.PrepareImport(new Settings(),
                    new Settings { DriveSyncPairs = new() { new() { LocalFolder = @"\\gone\share" } } },
                    _ => throw new IOException("network path not found")) == 1, null);
        }

        private static void Messages(Action<string, bool, string?> check)
        {
            foreach (var id in new[] { "settings.exported", "settings.export.failed", "settings.imported", "settings.import.failed" })
                check($"settings transfer: {id} is in the message list", Notifications.ById(id) != null, null);
        }
    }
}
