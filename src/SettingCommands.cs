using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;

namespace ExplorerNative
{
    /// <summary>
    /// Every preference from the command line (7.0.1, Conner: "make sure every
    /// feature has commands"):
    ///
    ///   ExplorerNative --do settings                     every setting and its value
    ///   ExplorerNative --do setting smb-server-port      one setting's value
    ///   ExplorerNative --do setting smb-server-port 4455 change it
    ///
    /// The names are the settings' own, with dashes or without and in any
    /// case: SmbServerPort, smb-server-port and "smb server port" are one. So a
    /// setting added to <see cref="Settings"/> has a command the moment it
    /// exists. Only plain values are offered (yes or no, a number, text, one of
    /// a list); the few that are lists of things have their own windows.
    ///
    /// Nothing secret is ever written out: a password, a sign-in or a key
    /// answers "set" or "not set". They can still be changed.
    /// </summary>
    internal static class SettingCommands
    {
        /// <summary>Settings no command reads or writes: they describe this run, not a choice.</summary>
        private static readonly HashSet<string> NotOffered = new(StringComparer.Ordinal)
        {
            nameof(Settings.RegisteredExePath), nameof(Settings.FoldersTakenAt),
            // Made by the app for itself; a typed one would only break the server.
            nameof(Settings.SftpServerHostKey), nameof(Settings.FtpServerCertificate),
        };

        private static bool IsPlain(Type type) =>
            type == typeof(bool) || type == typeof(int) || type == typeof(long) || type == typeof(double) ||
            type == typeof(string) || type.IsEnum;

        /// <summary>Every setting a command can read and change, by name.</summary>
        public static IReadOnlyList<PropertyInfo> All { get; } = typeof(Settings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && IsPlain(p.PropertyType) &&
                        p.GetCustomAttribute<JsonIgnoreAttribute>() == null && !NotOffered.Contains(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        /// <summary>Whether a setting's value is never shown.</summary>
        public static bool IsSecret(string name) =>
            name.Contains("Password", StringComparison.Ordinal) || name.Contains("Token", StringComparison.Ordinal) ||
            name.Contains("Secret", StringComparison.Ordinal) || name.Contains("ApiKey", StringComparison.Ordinal) ||
            name.EndsWith("Key", StringComparison.Ordinal) && name.StartsWith("Ai", StringComparison.Ordinal) ||
            name == nameof(Settings.ConnectCode);

        /// <summary>SmbServerPort as smb-server-port.</summary>
        public static string Dashed(string name)
        {
            var text = new StringBuilder(name.Length + 6);
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) text.Append('-');
                text.Append(char.ToLowerInvariant(name[i]));
            }
            return text.ToString();
        }

        private static string Bare(string name) =>
            new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        public static PropertyInfo? Find(string name)
        {
            var bare = Bare(name);
            return bare.Length == 0 ? null : All.FirstOrDefault(p => Bare(p.Name) == bare);
        }

        /// <summary>A value as a person would type it back: yes or no, a number, the text.</summary>
        public static string Shown(PropertyInfo setting, Settings from)
        {
            var value = setting.GetValue(from);
            if (IsSecret(setting.Name)) return value is string { Length: > 0 } ? "set" : "not set";
            return value switch
            {
                null => "",
                bool yes => yes ? "yes" : "no",
                double number => number.ToString("0.###", CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? "",
            };
        }

        /// <summary>Every setting and its value, one to a line.</summary>
        public static string List(Settings from)
        {
            var text = new StringBuilder();
            text.AppendLine("Read one with: ExplorerNative --do setting <name>. Change one with: ExplorerNative --do setting <name> <value>.");
            text.AppendLine("Passwords, sign-ins and keys are shown only as set or not set.");
            text.AppendLine();
            foreach (var setting in All)
                text.Append(Dashed(setting.Name)).Append(" = ").AppendLine(Shown(setting, from));
            return text.ToString().TrimEnd();
        }

        /// <summary>"smb-server-port is 4450", or why not.</summary>
        public static string Read(Settings from, string name)
        {
            var setting = Find(name);
            if (setting == null) return NotASetting(name);
            return $"{Dashed(setting.Name)} is {Shown(setting, from)}";
        }

        private static string NotASetting(string name)
        {
            var words = Dashed(name.Replace(" ", "")).Split('-', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToArray();
            // The ones sharing most words first: "smb-server-prot" is nearer smb-server-port than ftp-server-port.
            var near = All.Select(p => Dashed(p.Name))
                .Select(n => (Name: n, Shared: words.Count(w => n.Split('-').Contains(w, StringComparer.OrdinalIgnoreCase))))
                .Where(n => n.Shared > 0).OrderByDescending(n => n.Shared).ThenBy(n => n.Name, StringComparer.Ordinal)
                .Take(8).Select(n => n.Name).ToList();
            return $"\"{name}\" is not a setting." + (near.Count > 0 ? " Close to it: " + string.Join(", ", near) + "." : "") +
                   " Run ExplorerNative --do settings for the whole list.";
        }

        /// <summary>
        /// A copy of <paramref name="live"/> with one setting changed, or null
        /// with the reason in <paramref name="refusal"/>. The copy has been
        /// through <see cref="Settings.Validated"/>, as a settings file is, so
        /// a number out of range comes back as the number it was put to.
        /// </summary>
        public static Settings? Change(Settings live, string name, string typed, out string refusal, out string said)
        {
            refusal = said = "";
            var setting = Find(name);
            if (setting == null) { refusal = NotASetting(name); return null; }

            object value;
            var type = setting.PropertyType;
            var text = typed.Trim();
            if (type == typeof(bool))
            {
                switch (text.ToLowerInvariant())
                {
                    case "yes": case "on": case "true": case "1": value = true; break;
                    case "no": case "off": case "false": case "0": value = false; break;
                    default: refusal = $"{Dashed(setting.Name)} is yes or no."; return null;
                }
            }
            else if (type == typeof(int))
            {
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                { refusal = $"{Dashed(setting.Name)} is a whole number."; return null; }
                value = number;
            }
            else if (type == typeof(long))
            {
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                { refusal = $"{Dashed(setting.Name)} is a whole number."; return null; }
                value = number;
            }
            else if (type == typeof(double))
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                { refusal = $"{Dashed(setting.Name)} is a number."; return null; }
                value = number;
            }
            else if (type.IsEnum)
            {
                var names = Enum.GetNames(type);
                var match = names.FirstOrDefault(n => Bare(n) == Bare(text));
                if (match == null)
                { refusal = $"{Dashed(setting.Name)} is one of: {string.Join(", ", names.Select(Dashed))}."; return null; }
                value = Enum.Parse(type, match);
            }
            else value = typed;   // text, exactly as given: a password may begin or end with a space

            Settings changed;
            try
            {
                changed = live.Clone();
                setting.SetValue(changed, value);
                changed = changed.Validated();
            }
            catch (Exception ex)
            {
                refusal = $"{Dashed(setting.Name)} could not be changed: {ex.Message}";
                return null;
            }

            said = $"{Dashed(setting.Name)} is now {Shown(setting, changed)}";
            if (!IsSecret(setting.Name) && value is not string && Shown(setting, changed) != Shown(setting, WithValue(live, setting, value)))
                said += ", the nearest value allowed";
            return changed;
        }

        /// <summary>
        /// A copy of <paramref name="live"/> with one setting back at what a
        /// new installation has: the way to empty a text setting, which a
        /// command line cannot be handed an empty value for.
        /// </summary>
        public static Settings? Reset(Settings live, string name, out string refusal, out string said)
        {
            refusal = said = "";
            var setting = Find(name);
            if (setting == null) { refusal = NotASetting(name); return null; }
            try
            {
                var changed = live.Clone();
                setting.SetValue(changed, setting.GetValue(new Settings()));
                changed = changed.Validated();
                said = $"{Dashed(setting.Name)} is back to {(Shown(setting, changed) is { Length: > 0 } shown ? shown : "empty")}";
                return changed;
            }
            catch (Exception ex)
            {
                refusal = $"{Dashed(setting.Name)} could not be changed: {ex.Message}";
                return null;
            }
        }

        private static Settings WithValue(Settings live, PropertyInfo setting, object value)
        {
            var copy = live.Clone();
            setting.SetValue(copy, value);
            return copy;
        }
    }
}
