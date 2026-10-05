using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ExplorerNative
{
    /// <summary>
    /// Opens a file as fast as Windows allows.
    ///
    /// ShellExecute is not used for the normal path. It resolves the association
    /// through COM every time, and if the association carries a DelegateExecute
    /// handler that cannot load in this process, it spends hundreds of
    /// milliseconds failing before it gives up — which is exactly what foobar's
    /// FLAC association does after a reinstall.
    ///
    /// Instead the handler's command line is read from the association database
    /// once per extension, cached, and launched with a direct CreateProcess. No
    /// COM, no verb resolution, no DelegateExecute, and no touching the file
    /// itself — which also matters on a network share, where a stat is a round
    /// trip. ShellExecute remains the fallback for anything unresolvable.
    /// </summary>
    public static class FileLauncher
    {
        private const uint ASSOCF_NONE = 0;
        private const uint ASSOCSTR_COMMAND = 1;
        private const uint ASSOCSTR_EXECUTABLE = 2;

        [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern uint AssocQueryStringW(
            uint flags, uint str, string pszAssoc, string? pszExtra,
            StringBuilder? pszOut, ref uint pcchOut);

        private sealed record Launch(string Exe, string ArgsTemplate);

        /// <summary>Resolved once per extension; a repeat open costs nothing.</summary>
        private static readonly ConcurrentDictionary<string, Launch?> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Resolves an extension ahead of time so the first Enter is as quick as
        /// the rest. Safe to call from a worker.
        /// </summary>
        public static void Prewarm(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return;
            Cache.GetOrAdd(extension, Resolve);
        }

        /// <summary>
        /// The executable that would be launched for this extension, or null if
        /// the shell fallback would be used. Exposed for diagnostics and tests.
        /// </summary>
        public static string? ResolveHandlerPath(string extension) =>
            string.IsNullOrEmpty(extension) ? null : Cache.GetOrAdd(extension, Resolve)?.Exe;

        /// <summary>
        /// The exact command line this extension would be launched with, or null
        /// if the shell would be used instead. Exposed so the quoting can be
        /// tested directly rather than by opening files and watching what happens.
        /// </summary>
        public static string? DescribeCommandLine(string extension, string path)
        {
            if (string.IsNullOrEmpty(extension)) return null;
            var launch = Cache.GetOrAdd(extension, Resolve);
            return launch == null ? null : $"{Quote(launch.Exe)} {ArgumentsFor(launch, path)}";
        }

        private static string Quote(string s) => "\"" + s + "\"";

        private static string ArgumentsFor(Launch launch, string path) =>
            launch.ArgsTemplate.Replace("%1", path);

        /// <summary>Launches the file. Throws only if every route failed.</summary>
        public static void Open(string path)
        {
            var ext = Path.GetExtension(path);
            var launch = string.IsNullOrEmpty(ext) ? null : Cache.GetOrAdd(ext, Resolve);

            if (launch != null)
            {
                try
                {
                    // Disposed at once: the handle is not needed, and each one
                    // kept waited for a garbage collection to be closed.
                    Process.Start(new ProcessStartInfo(launch.Exe)
                    {
                        Arguments = ArgumentsFor(launch, path),
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                    })?.Dispose();
                    return;
                }
                catch
                {
                    // A stale cached handler (app uninstalled, path changed):
                    // drop it and let the shell have a go.
                    Cache.TryRemove(ext, out _);
                }
            }

            // Fallback: whatever the shell decides, however slow.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }

        private static Launch? Resolve(string extension)
        {
            var command = Query(ASSOCSTR_COMMAND, extension);
            if (!string.IsNullOrWhiteSpace(command))
            {
                var parsed = ParseCommand(command!);
                if (parsed != null) return parsed;
            }

            var exe = Query(ASSOCSTR_EXECUTABLE, extension);
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                return new Launch(exe!, "\"%1\"");

            return null;
        }

        private static string? Query(uint what, string extension)
        {
            try
            {
                uint length = 0;
                // First call sizes the buffer; S_FALSE is the expected result.
                AssocQueryStringW(ASSOCF_NONE, what, extension, null, null, ref length);
                if (length == 0 || length > 4096) return null;

                var sb = new StringBuilder((int)length);
                uint hr = AssocQueryStringW(ASSOCF_NONE, what, extension, null, sb, ref length);
                return hr == 0 ? sb.ToString() : null;
            }
            catch { return null; }
        }

        private static Launch? ParseCommand(string command)
        {
            var parsed = ParseRegisteredCommand(command, File.Exists);
            return parsed == null ? null : new Launch(parsed.Value.Exe, parsed.Value.ArgsTemplate);
        }

        /// <summary>What a registered shell command resolves to.</summary>
        public readonly record struct ParsedCommand(string Exe, string ArgsTemplate);

        /// <summary>
        /// Splits a registered command such as
        ///   "C:\Program Files\foobar2000\foobar2000.exe" "%1"
        /// into the executable and the argument template, or null when it cannot
        /// be launched directly and the shell should be asked instead.
        ///
        /// <paramref name="exists"/> is injected so the parsing — which has to
        /// consult the filesystem to find where an unquoted executable path ends
        /// — can be tested against commands that are not installed here.
        /// </summary>
        public static ParsedCommand? ParseRegisteredCommand(string? command, Func<string, bool> exists)
        {
            if (command == null) return null;
            command = command.Trim();
            if (command.Length == 0) return null;

            string exe;
            string rest;

            if (command[0] == '"')
            {
                int end = command.IndexOf('"', 1);
                if (end < 0) return null;
                exe = command[1..end];
                rest = command[(end + 1)..].Trim();
            }
            else
            {
                // Unquoted: the executable ends at the first space that leaves a
                // path which actually exists, so "C:\Program Files\x.exe %1"
                // without quotes still resolves.
                int space = command.IndexOf(' ');
                while (space > 0 && !exists(Environment.ExpandEnvironmentVariables(command[..space])))
                {
                    int next = command.IndexOf(' ', space + 1);
                    if (next < 0) break;
                    space = next;
                }
                if (space < 0) { exe = command; rest = ""; }
                else { exe = command[..space]; rest = command[(space + 1)..].Trim(); }
            }

            exe = Environment.ExpandEnvironmentVariables(exe);

            // The environment variable has to be expanded before the split is
            // tested, not after: the registered command for text files has always
            // been "%SystemRoot%\system32\NOTEPAD.EXE %1", and %SystemRoot%\... is
            // not a path that exists until it is expanded — so the search for
            // where the executable ended walked straight past it and swallowed
            // the %1 into the executable name.
            if (!exists(exe)) return null;

            // Some handlers use %L or %* rather than %1.
            rest = rest.Replace("%L", "%1").Replace("%l", "%1").Replace("%*", "");
            if (!rest.Contains("%1")) rest = string.IsNullOrEmpty(rest) ? "\"%1\"" : rest + " \"%1\"";

            // Some handlers ask for things only the shell can supply — an ITEMIDLIST
            // handle, the containing directory, a view object. Compressed folders
            // register "Explorer.exe /idlist,%I,%L", and %I is a handle to memory in
            // the calling process, not text. Substituting the path and hoping is how
            // you launch Explorer with a nonsense command line; the shell knows how
            // to satisfy these, so hand the whole thing back to it.
            if (ContainsUnsupportedPlaceholder(rest)) return null;

            // Quote the placeholder, always.
            //
            // A registered command is free to write a bare %1 — Internet Explorer's
            // html verb does, and Notepad's has for decades — because the shell
            // quotes what it substitutes. We build a command line by hand, so an
            // unquoted %1 became several arguments the moment a path contained a
            // space. Any existing quotes are stripped first so this cannot double
            // them up.
            rest = rest.Replace("\"%1\"", "%1").Replace("%1", "\"%1\"");

            return new ParsedCommand(exe, rest);
        }

        /// <summary>
        /// True when the argument template needs a substitution only the shell can
        /// make. %2 to %9 and %S are extra verb arguments we are not given; the
        /// letters are documented shell substitutions for a PIDL, a directory, a
        /// view, a window handle, and so on.
        /// </summary>
        private static bool ContainsUnsupportedPlaceholder(string template)
        {
            for (int i = 0; i + 1 < template.Length; i++)
            {
                if (template[i] != '%') continue;

                char what = template[i + 1];
                if (what == '%') { i++; continue; }   // an escaped percent sign

                if (what is 'I' or 'i' or 'D' or 'd' or 'V' or 'v'
                         or 'W' or 'w' or 'S' or 's' or 'U' or 'u'
                         or (>= '2' and <= '9'))
                    return true;
            }
            return false;
        }
    }
}
