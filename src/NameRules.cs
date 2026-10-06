using System;
using System.IO;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The rules for what a file name may be, and for what order a list of them
    /// comes out in.
    ///
    /// Kept away from the window deliberately: both are pure decisions about
    /// strings with no UI in them, and both are the sort of thing that is worth
    /// being able to test directly rather than by driving a list control.
    /// </summary>
    public static class NameRules
    {
        /// <summary>
        /// An action and how many things it happened to, said the way somebody
        /// listening to it wants to hear it.
        ///
        /// **One is just the verb.** "Copied file" names the thing a second time:
        /// the row was read out a moment ago, name and all, so the type is the
        /// least informative word available and it is in the way of the one word
        /// that matters. This is the same rule the Type column failed — is this
        /// worth hearing every single time? — applied to a sentence.
        ///
        /// **Above one, the number is the whole message.** A selection is the one
        /// thing that cannot be seen by listening: the rows were read as they were
        /// picked, but nothing ever says how many there are now. "Copied 12 items"
        /// is the only confirmation that the twelve you meant were the twelve you
        /// got, and it is the difference between noticing a wrong selection here
        /// and noticing it after the paste.
        /// </summary>
        public static string SayCount(string verb, int count, string noun = "item") =>
            count == 1 ? verb : $"{verb} {count} {noun}s";

        /// <summary>
        /// A count and its noun, agreeing: "1 item", "7 items", "no items".
        ///
        /// Different from <see cref="SayCount"/>, which drops the number at one
        /// because "Copied" already says everything. Here the number *is* the
        /// message — the status bar and the tab announcement exist to say how
        /// many — so one has to be spelled out rather than left implied.
        ///
        /// It is a rule rather than an interpolation at each site because the
        /// sites that matter are spoken. "Tab 1, Documents, 1 items" was read out
        /// on every tab switch, and "1 items" on the status bar on every folder
        /// with one thing in it, in an application where every word said aloud is
        /// argued over.
        /// </summary>
        /// <summary>
        /// Whether an entry is a link — a junction or a symbolic link — as
        /// against any other reparse point.
        ///
        /// Walks here decline to follow links, and used to decline every reparse
        /// point to do it. A cloud placeholder is one — every file and folder
        /// under OneDrive's Files On-Demand, and everything on the Drive letter —
        /// and so is a deduplicated file, so a copy, an upload, an archive, a
        /// search or a size of such a folder quietly left out what was in it.
        /// </summary>
        public static bool IsLink(FileSystemInfo entry)
        {
            try
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) return false;
                return entry.LinkTarget != null;
            }
            catch { return true; }
        }

        public static string Items(int count, string noun = "item") =>
            $"{count} {noun}{(count == 1 ? "" : "s")}";

        /// <summary>
        /// A name nothing has already claimed, by adding " (2)", " (3)" and so on.
        ///
        /// The rule rather than the filesystem, because there is more than one
        /// place a name can already be taken. On disk it is File.Exists; on the
        /// Drive letter it is the listing the folder was populated from, which is
        /// the only cheap answer there — enumerating placeholders to find out is
        /// fifteen milliseconds each and the reason browsing Drive used to be
        /// slow. Both want the same arithmetic and neither should own it.
        ///
        /// <paramref name="folder"/> matters and is not a nicety. A folder has no
        /// extension whatever the dots in its name say, and splitting at the last
        /// one turned a second copy of "Backup.2024" into "Backup (2).2024" — then
        /// announced it, naming something nobody would recognise as their folder.
        /// </summary>
        /// <summary>
        /// Whether a path's last name ends in a dot or a space — which Windows
        /// quietly strips from an ordinary path, so "report." is taken to mean
        /// "report" and a delete or a rename acts on a different file.
        /// </summary>
        public static bool NeedsLiteralPath(string path)
        {
            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            return name.Length > 0 && (name[^1] == '.' || name[^1] == ' ');
        }

        /// <summary>
        /// The path in the form Windows takes exactly as written: "\\?\C:\x." or
        /// "\\?\UNC\server\share\x.".
        /// </summary>
        public static string LiteralPath(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
            return @"\\?\" + path;
        }

        /// <summary>
        /// The path to hand to a filesystem call so that it reaches exactly this
        /// entry: the literal form when any part of it ends in a dot or a space,
        /// and the path untouched otherwise.
        ///
        /// Any part, not only the last. Inside a folder called "fold." every
        /// ordinary path silently means "fold", so a copy walked the sibling
        /// folder's files; and "report." beside "report" had a move take the
        /// wrong one. Only fully qualified paths are converted, because the
        /// literal form resolves nothing — no current directory, no "..".
        /// </summary>
        public static string ExactPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;

            bool needs = false;
            foreach (var part in path.Split('\\', '/'))
            {
                if (part.Length == 0 || part == "." || part == "..") continue;
                if (part[^1] == '.' || part[^1] == ' ') { needs = true; break; }
            }
            if (!needs) return path;

            try { if (!Path.IsPathFullyQualified(path)) return path; }
            catch { return path; }

            return LiteralPath(path.Replace('/', '\\'));
        }

        /// <summary>
        /// The ordinary form of a path that came back from a call given
        /// <see cref="ExactPath"/>, so names and messages read normally. Feed it
        /// back through <c>ExactPath</c> before using it again.
        /// </summary>
        public static string PlainPath(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
            return path;
        }

        public static string UniqueAmong(string desired, Func<string, bool> taken, bool folder)
        {
            if (!taken(desired)) return desired;

            // A name that starts with its only dot — ".gitignore", ".env" — is
            // all stem. Split as an extension it numbered as " (2).gitignore",
            // with a leading space.
            int dot = desired.LastIndexOf('.');
            bool whole = folder || dot <= 0;
            var stem = whole ? desired : desired[..dot];
            var extension = whole ? "" : desired[dot..];

            for (int i = 2; i < int.MaxValue; i++)
            {
                var candidate = $"{stem} ({i}){extension}";
                if (!taken(candidate)) return candidate;
            }
            throw new IOException("Could not find an unused name.");
        }

        /// <summary>
        /// Windows refuses these as a file name anywhere on the disk, with or
        /// without an extension.
        /// </summary>
        private static readonly string[] ReservedDeviceNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// Says what is wrong with a typed file or folder name, or null if it is
        /// usable.
        ///
        /// Path.Combine takes its second argument as a path, not as a name, so a
        /// typed "..\..\thing" or "C:\Windows\thing" walked straight out of the
        /// folder the user was looking at — a rename could land anywhere on the
        /// disk, and "New folder" created in a directory nobody had opened. Names
        /// are checked before they are ever combined with a directory.
        /// </summary>
        public static string? DescribeBadName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "That name is empty";

            if (name.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
                return "A name cannot contain a slash";

            // Not covered by GetInvalidFileNameChars on every runtime, and it is
            // how "C:whatever" reaches a different drive's current directory.
            if (name.Contains(':')) return "A name cannot contain a colon";

            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "That name contains a character Windows does not allow";

            if (name == "." || name == "..") return "That name is reserved";

            var stem = name;
            int dot = stem.IndexOf('.');
            if (dot > 0) stem = stem[..dot];

            foreach (var reserved in ReservedDeviceNames)
                if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
                    return $"\"{reserved}\" is a name Windows reserves for a device";

            // A trailing dot or space is silently stripped by the filesystem, so
            // what appears afterwards is not the name that was asked for.
            if (name.EndsWith('.') || name.EndsWith(' '))
                return "A name cannot end with a dot or a space";

            return null;
        }

        /// <summary>Convenience for callers that only want a yes or no.</summary>
        public static bool IsUsableName(string? name) => DescribeBadName(name) == null;

        /// <summary>
        /// Repairs a path as it arrives from a command line.
        ///
        /// Two traps, and the shell walks into both when this app is the handler
        /// for folders.
        ///
        /// The registered command is `"app.exe" "%1"`, and for a drive the shell
        /// substitutes `D:\`, producing `"app.exe" "D:\"` on the command line.
        /// Windows parses a backslash before a quote as an escape, so the closing
        /// quote is swallowed and the argument actually delivered is `D:"`.
        ///
        /// Trimming that quote leaves `D:`, which is not the drive. A bare drive
        /// letter means "the current directory on D", and the current directory is
        /// wherever the process happened to be started from — so opening a drive
        /// showed the contents of the application's own folder instead. (This is
        /// the same trap RoboCopyEngine.QuoteDir guards against from the other
        /// direction, when writing a path out to a command line.)
        /// </summary>
        public static string NormaliseLaunchPath(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            var path = raw.Trim().Trim('"').Trim();
            if (path.Length == 0) return "";

            // "D:" -> "D:\". A filename can never contain a colon, so a two
            // character path ending in one is unambiguously a drive.
            if (path.Length == 2 && path[1] == ':' && char.IsLetter(path[0]))
                return path + Path.DirectorySeparatorChar;

            return path;
        }

        /// <summary>
        /// Orders two entries by name the way File Explorer does, falling back to
        /// the full path.
        ///
        /// StrCmpLogicalW is Explorer's own comparison: numbers inside a name
        /// compare as numbers, so "Track 2" comes before "Track 10" and this
        /// application's own "file (2).txt" before "file (10).txt"; and it is
        /// linguistic, so "Éclair" sorts among the E names rather than after
        /// "Zebra" and "~tilde", where an ordinal comparison put every accented
        /// and every CJK name. Accents are a secondary difference to it, which is
        /// the same folding <see cref="TypeAhead"/> applies, so typing "e" lands
        /// on the first E name in this order whether or not it has an accent.
        ///
        /// The fallback is what makes the order total. List.Sort is unstable, so
        /// without a decisive tie-break two names the comparison calls equal —
        /// differing only in case, or only in an accent — swapped places between
        /// refreshes of the same unchanged folder.
        /// </summary>
        public static int CompareNames(string aName, string aPath, string bName, string bPath)
        {
            int c = StrCmpLogicalW(aName, bName);
            return c != 0 ? c : string.CompareOrdinal(aPath, bPath);
        }

        /// <summary>
        /// The same order, using sort keys made by <see cref="LogicalKey"/> where
        /// both names have one, and asking Windows where either does not.
        ///
        /// A key comparison is a byte compare; StrCmpLogicalW is a call into
        /// Windows that does the linguistic work again for every pair. Sorting
        /// 100,000 names is n log n of those, so making each name's key once
        /// and comparing bytes took the sort from about 155ms to about 55ms.
        /// The answer is the same pair by pair — see LogicalKey for how that
        /// was established — so the order cannot differ, and a missing key on
        /// either side is never wrong, only slower.
        /// </summary>
        public static int CompareNames(string aName, byte[]? aKey, string aPath, string bName, byte[]? bKey, string bPath)
        {
            int c = aKey != null && bKey != null
                ? aKey.AsSpan().SequenceCompareTo(bKey)
                : StrCmpLogicalW(aName, bName);
            return c != 0 ? c : string.CompareOrdinal(aPath, bPath);
        }

        /// <summary>
        /// A key that orders exactly as StrCmpLogicalW orders the name, or null
        /// when this name has to be compared by asking Windows.
        ///
        /// StrCmpLogicalW is CompareString with NORM_IGNORECASE and
        /// SORT_DIGITSASNUMBERS in the user's locale: measured against it on
        /// 400,000 pairs of real and made-up names, they never disagreed. The
        /// sort key of that same comparison agrees too, with one exception —
        /// numbers written in characters outside ASCII ("²", "½", "١", fullwidth
        /// "１") are weighed differently in a key than in a comparison. So a
        /// name holding one gets no key. With that rule, 2.5 million pairs —
        /// random, near-identical, and every neighbour in the true order —
        /// disagreed nowhere, and 82% of a mixed corpus had a key.
        ///
        /// And in case a locale somewhere does something those names did not,
        /// keys are only used at all after a fixed set of awkward names has been
        /// checked against StrCmpLogicalW on this machine, in this locale.
        /// </summary>
        public static byte[]? LogicalKey(string name)
        {
            if (!KeysAgree) return null;
            return KeyOf(name);
        }

        private static unsafe byte[]? KeyOf(string name)
        {
            // A key costs in proportion to the whole name and a comparison stops
            // at the first difference, so a very long name is cheaper asked
            // about than keyed: 10,000 names of 200 characters sorted faster
            // without keys than with them.
            if (name.Length == 0 || name.Length > LongestKeyedName) return null;
            foreach (char c in name)
                if (c >= 0x80 && char.IsNumber(c)) return null;

            try
            {
                const uint Flags = LcmapSortKey | NormIgnoreCase | SortDigitsAsNumbers;
                int size = LCMapStringEx(null, Flags, name, name.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (size <= 0) return null;
                var key = new byte[size];
                fixed (byte* p = key)
                {
                    if (LCMapStringEx(null, Flags, name, name.Length, p, size, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != size)
                        return null;
                }
                return key;
            }
            catch { return null; }
        }

        /// <summary>0 not yet checked, 1 keys agree, 2 they do not. An int so threads read it whole.</summary>
        private static int _keysAgree;

        /// <summary>Whether keys order this machine's awkward names as StrCmpLogicalW does.</summary>
        private static bool KeysAgree
        {
            get
            {
                int known = System.Threading.Volatile.Read(ref _keysAgree);
                if (known == 0)
                {
                    known = CheckKeys() ? 1 : 2;
                    System.Threading.Volatile.Write(ref _keysAgree, known);
                }
                return known == 1;
            }
        }

        private static bool CheckKeys()
        {
            string[] names =
            {
                "", "a", "A", "b", "Z", "z", "aa", "ab", "a b", "a-b", "a_b", "a.b", "a'b", "ab-", "-ab", "'ab",
                "1", "01", "001", "2", "10", "9", "99", "100", "1.10", "1.9", "a1", "a01", "a2", "a10", "a 2", "a 10",
                "Track 2", "Track 10", "track 02", "file (2).txt", "file (10).txt", "file.txt", "file1.txt",
                "18446744073709551616", "18446744073709551615", "99999999999999999999x",
                "Éclair", "eclair", "Eclair", "étoile", "Ärger", "Arger", "Æble", "AEble", "Straße", "Strasse",
                "Øresund", "Łódź", "Lodz", "naïve", "café", "cafe", "日本語", "あ", "ア", "ｱ", "😀", "😀 a",
                "~tilde", "!bang", "#hash", "(paren", "[bracket", "_under", " space", "a b", "a​b",
                "ﬁle", "file", "ss", "ß", "Zebra", "zebra", "aa10b", "aa9b", "x-1", "x-01", "x1-1",
                "CamelCase", "camelcase", "IMG_0001.jpg", "IMG_0010.jpg", "img_1.jpg", "dk", "aa",
            };

            var keys = new byte[]?[names.Length];
            for (int i = 0; i < names.Length; i++) keys[i] = KeyOf(names[i]);

            for (int i = 0; i < names.Length; i++)
                for (int j = 0; j < names.Length; j++)
                {
                    if (keys[i] == null || keys[j] == null) continue;
                    int byKey = Math.Sign(keys[i]!.AsSpan().SequenceCompareTo(keys[j]));
                    if (byKey != Math.Sign(StrCmpLogicalW(names[i], names[j]))) return false;
                }
            return true;
        }

        /// <summary>StrCmpLogicalW itself, for anything that has to agree with it.</summary>
        internal static int CompareLogical(string a, string b) => StrCmpLogicalW(a, b);

        private const int LongestKeyedName = 100;

        private const uint LcmapSortKey = 0x00000400;
        private const uint NormIgnoreCase = 0x00000001;
        private const uint SortDigitsAsNumbers = 0x00000008;

        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
        private static extern unsafe int LCMapStringEx(string? locale, uint flags, string source, int sourceLength,
            byte* destination, int destinationLength, IntPtr version, IntPtr reserved, IntPtr param);

        [System.Runtime.InteropServices.DllImport("shlwapi.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string a, string b);

        /// <summary>
        /// The column a list is actually sorted by.
        ///
        /// There is no Type column, so a settings file still saying Type sorts by
        /// name: an order by something the row does not show is an order nobody
        /// can hear.
        /// </summary>
        public static SortColumn SortsAs(SortColumn column) =>
            column == SortColumn.Type ? SortColumn.Name : column;

        /// <summary>
        /// The sentence for an update that could not be checked for, downloaded
        /// or started.
        ///
        /// The updater's own refusals are already sentences ("GitHub is limiting
        /// requests…") and are shown as they are. Only a network failure gets
        /// "GitHub could not be reached" in front, and a timeout says so.
        /// </summary>
        public static string PlainUpdateFailure(Exception ex) => ex switch
        {
            Updater.UpdateException => ex.Message,
            TaskCanceledException or OperationCanceledException => "GitHub did not answer in time.",
            System.Net.Http.HttpRequestException => "GitHub could not be reached: " + ex.Message,
            _ => ex.Message,
        };
    }
}
