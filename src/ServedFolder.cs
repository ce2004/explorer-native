using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ExplorerNative
{
    /// <summary>What went wrong on the disk, in the few kinds a client can be told about.</summary>
    internal enum ServedProblem { NotFound, Denied, InUse, DiskFull, Other }

    /// <summary>One row of a served folder, read once so a file vanishing later cannot break a listing.</summary>
    internal readonly record struct ServedEntry(string Name, bool IsFolder, long Length, DateTime ModifiedUtc, DateTime CreatedUtc);

    /// <summary>
    /// What the FTP and SFTP servers share: turning a path a client sent into
    /// a path on this PC without ever leaving the folder being served.
    /// </summary>
    internal static class ServedFolder
    {
        private static readonly char[] IllegalInNames = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

        private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "COM¹", "COM²", "COM³",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
            "LPT¹", "LPT²", "LPT³",
        };

        /// <summary>
        /// The local path for a client's path (forward slashes, already decoded),
        /// or null when it must be refused. An empty path is the folder itself.
        /// </summary>
        public static string? Resolve(string root, string relative)
        {
            if (string.IsNullOrEmpty(root) || relative == null) return null;

            string fullRoot;
            try { fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
            catch { return null; }

            var path = new StringBuilder(fullRoot);
            foreach (string segment in relative.Split('/'))
            {
                if (segment.Length == 0) continue;          // "a//b" and a trailing slash
                if (!IsSafeName(segment)) return null;
                if (path[path.Length - 1] != Path.DirectorySeparatorChar) path.Append(Path.DirectorySeparatorChar);
                path.Append(segment);
            }

            // Every name was checked above, so this should change nothing. It is
            // asked anyway: Windows has more ways to spell a path than one list
            // of rules can be trusted to know.
            string full;
            try { full = Path.GetFullPath(path.ToString()); }
            catch { return null; }

            if (!IsInside(fullRoot, full)) return null;
            if (LeavesThroughLink(fullRoot, full)) return null;
            return full;
        }

        /// <summary>Whether one name between slashes is safe to hand to Windows.</summary>
        public static bool IsSafeName(string name)
        {
            if (name.Length == 0 || name.Length > 255) return false;
            if (name == "." || name == "..") return false;

            // Windows quietly drops a trailing dot or space, so "secret.txt."
            // would open "secret.txt" behind the back of any check on the name.
            char last = name[name.Length - 1];
            if (last == '.' || last == ' ') return false;

            foreach (char c in name)
            {
                if (c < 32 || c == 127) return false;
            }
            if (name.IndexOfAny(IllegalInNames) >= 0) return false;

            // "NUL.txt" and "con .log" are still the device.
            int dot = name.IndexOf('.');
            string stem = (dot < 0 ? name : name.Substring(0, dot)).TrimEnd(' ');
            return !DeviceNames.Contains(stem);
        }

        /// <summary>The path the client sees for a local path: "" for the folder itself, else "a/b".</summary>
        public static string RelativeOf(string root, string full)
        {
            string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (full.Length <= fullRoot.Length) return "";
            return full.Substring(fullRoot.Length).Replace('\\', '/').Trim('/');
        }

        private static bool IsInside(string fullRoot, string full)
        {
            if (full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) return true;
            string prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A junction or symbolic link inside the folder can point anywhere on
        /// the PC. One that points back inside is fine; one that leaves is not
        /// followed. Cloud placeholders are reparse points too but have no
        /// target, and are left alone.
        /// </summary>
        private static bool LeavesThroughLink(string fullRoot, string full)
        {
            if (full.Length <= fullRoot.Length) return false;

            string rest = full.Substring(fullRoot.Length).Trim(Path.DirectorySeparatorChar);
            string at = fullRoot;
            foreach (string part in rest.Split(Path.DirectorySeparatorChar))
            {
                at = at.EndsWith(Path.DirectorySeparatorChar) ? at + part : at + Path.DirectorySeparatorChar + part;

                FileAttributes attributes;
                try { attributes = File.GetAttributes(at); }
                catch { return false; }                     // nothing there yet, so nothing below it either

                if ((attributes & FileAttributes.ReparsePoint) != 0 && LinkLeaves(fullRoot, at, attributes)) return true;
            }
            return false;
        }

        private static bool LinkLeaves(string fullRoot, string path, FileAttributes attributes)
        {
            try
            {
                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
                if (info.LinkTarget == null) return false;  // not a link
                FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                return target == null || !IsInside(fullRoot, Path.GetFullPath(target.FullName));
            }
            catch
            {
                return true;                                // a link that cannot be read is not followed
            }
        }

        // ---- listing ----------------------------------------------------

        /// <summary>The facts about one file or folder, or null when it is not there.</summary>
        public static ServedEntry? Describe(string path)
        {
            try
            {
                var folder = new DirectoryInfo(path);
                if (folder.Exists) return new ServedEntry(folder.Name, true, 0, folder.LastWriteTimeUtc, folder.CreationTimeUtc);
                var file = new FileInfo(path);
                if (file.Exists) return new ServedEntry(file.Name, false, file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// What a client is shown in a folder: everything, hidden and system
        /// files included, except names it could never ask for, links that
        /// leave the folder, and uploads still arriving. Opening the folder
        /// throws; trouble part way through just ends the list.
        /// </summary>
        public static IEnumerable<ServedEntry> List(string root, string folder)
        {
            // Opened here, not on the first row: this used to be one iterator,
            // so a folder that could not be opened only said so after the
            // caller had told its client the list was coming.
            string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            IEnumerator<FileSystemInfo> rows = new DirectoryInfo(folder).EnumerateFileSystemInfos().GetEnumerator();
            return Rows(fullRoot, rows);
        }

        private static IEnumerable<ServedEntry> Rows(string fullRoot, IEnumerator<FileSystemInfo> rows)
        {
            try
            {
                while (true)
                {
                    ServedEntry entry;
                    try
                    {
                        if (!rows.MoveNext()) break;
                        FileSystemInfo row = rows.Current;
                        if (!IsSafeName(row.Name) || IsTempName(row.Name)) continue;
                        if ((row.Attributes & FileAttributes.ReparsePoint) != 0 && LinkLeaves(fullRoot, row.FullName, row.Attributes)) continue;

                        bool isFolder = (row.Attributes & FileAttributes.Directory) != 0;
                        entry = new ServedEntry(row.Name, isFolder, isFolder ? 0 : ((FileInfo)row).Length,
                            row.LastWriteTimeUtc, row.CreationTimeUtc);
                    }
                    catch (FileNotFoundException) { continue; }     // deleted while we were listing
                    catch (IOException) { break; }
                    catch (UnauthorizedAccessException) { break; }
                    yield return entry;
                }
            }
            finally
            {
                rows.Dispose();
            }
        }

        // ---- uploads ----------------------------------------------------

        private const string TempPrefix = ".~upload-";
        private const string TempSuffix = ".part";

        /// <summary>
        /// A name beside the target for an upload to land in. It is moved over
        /// the target only once every byte has arrived, so an upload that is
        /// cut off never leaves half a file where a good one was.
        /// </summary>
        public static string TempNameFor(string target)
        {
            string folder = Path.GetDirectoryName(target) ?? target;
            return Path.Combine(folder, TempPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant() + TempSuffix);
        }

        public static bool IsTempName(string name) =>
            name.StartsWith(TempPrefix, StringComparison.Ordinal) && name.EndsWith(TempSuffix, StringComparison.Ordinal);

        /// <summary>
        /// Puts a finished upload where it belongs, replacing what was there.
        /// Windows answers "access denied" when the old file is merely open in
        /// another program; that is told apart here so the client hears "in
        /// use, try again" and not "you may not".
        /// </summary>
        public static void MoveIntoPlace(string temp, string target)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
            }
            catch (UnauthorizedAccessException) when (IsHeldOpen(target))
            {
                throw new IOException("The file is in use.", unchecked((int)0x80070020));
            }
        }

        private static bool IsHeldOpen(string path)
        {
            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return true; }
            catch { return false; }
        }

        public static void DeleteQuietly(string? path)
        {
            if (path == null) return;
            try { File.Delete(path); } catch { }
        }

        // ---- errors -----------------------------------------------------

        public static ServedProblem Classify(Exception error)
        {
            switch (error)
            {
                case FileNotFoundException:
                case DirectoryNotFoundException:
                case DriveNotFoundException:
                    return ServedProblem.NotFound;
                case UnauthorizedAccessException:
                case System.Security.SecurityException:
                    return ServedProblem.Denied;
                case IOException io:
                    switch (io.HResult & 0xFFFF)
                    {
                        case 32:    // sharing violation
                        case 33:    // lock violation
                            return ServedProblem.InUse;
                        case 39:    // handle disk full
                        case 112:   // disk full
                        case 1295:  // quota
                            return ServedProblem.DiskFull;
                        case 2:
                        case 3:
                            return ServedProblem.NotFound;
                        case 5:
                        case 19:    // write protected
                            return ServedProblem.Denied;
                    }
                    return ServedProblem.Other;
                default:
                    return ServedProblem.Other;
            }
        }

        // ---- sign-in ----------------------------------------------------

        /// <summary>
        /// Compares two secrets without the time taken saying where they first
        /// differ, or how long the right one is.
        /// </summary>
        public static bool SameSecret(string given, string expected)
        {
            byte[] a = SHA256.HashData(Encoding.UTF8.GetBytes(given ?? ""));
            byte[] b = SHA256.HashData(Encoding.UTF8.GetBytes(expected ?? ""));
            return CryptographicOperations.FixedTimeEquals(a, b);
        }

        /// <summary>
        /// The address as one thing to compare: an IPv4 caller on a socket that
        /// takes both families arrives as "::ffff:1.2.3.4".
        /// </summary>
        public static IPAddress Plain(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0) return new IPAddress(address.GetAddressBytes());
            return address;
        }
    }

    /// <summary>
    /// Slows down password guessing. After ten wrong sign-ins from one address
    /// inside ten minutes, that address is refused for five minutes.
    /// </summary>
    internal sealed class SignInThrottle
    {
        public const int MaxFailures = 10;
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);

        // Somebody trying from thousands of addresses must not be able to fill
        // memory with our notes about them.
        private const int MaxAddresses = 2048;

        private sealed class Entry
        {
            public readonly Queue<DateTime> Failures = new();
            public DateTime BlockedUntil;
            public DateTime LastSeen;
        }

        private readonly Dictionary<IPAddress, Entry> _entries = new();
        private readonly Func<DateTime> _now;

        public SignInThrottle(Func<DateTime>? clock = null) => _now = clock ?? (() => DateTime.UtcNow);

        public bool IsBlocked(IPAddress address)
        {
            DateTime now = _now();
            lock (_entries)
            {
                return _entries.TryGetValue(KeyFor(address), out Entry? entry) && entry.BlockedUntil > now;
            }
        }

        /// <summary>Notes one wrong sign-in. True when the address is now shut out.</summary>
        public bool Failed(IPAddress address)
        {
            DateTime now = _now();
            IPAddress key = KeyFor(address);
            lock (_entries)
            {
                while (_recent.Count > 0 && now - _recent.Peek() > BrakeWindow) _recent.Dequeue();
                if (_recent.Count < BrakeAfter * 4) _recent.Enqueue(now);

                if (!_entries.TryGetValue(key, out Entry? entry))
                {
                    if (_entries.Count >= MaxAddresses) MakeRoom(now);
                    // Still full: every note left is an address shut out right
                    // now. None of those is thrown away to make room (cycling
                    // through thousands of addresses used to wipe the guesser's
                    // own lockouts); the newcomer is refused instead.
                    if (_entries.Count >= MaxAddresses) return true;
                    entry = new Entry();
                    _entries[key] = entry;
                }

                entry.LastSeen = now;
                while (entry.Failures.Count > 0 && now - entry.Failures.Peek() > Window) entry.Failures.Dequeue();
                entry.Failures.Enqueue(now);

                if (entry.Failures.Count >= MaxFailures)
                {
                    entry.Failures.Clear();
                    entry.BlockedUntil = now + Lockout;
                }
                return entry.BlockedUntil > now;
            }
        }

        /// <summary>A right password wipes the slate, so a few typos never add up over a day.</summary>
        public void Succeeded(IPAddress address)
        {
            lock (_entries) _entries.Remove(KeyFor(address));
        }

        internal int TrackedAddresses { get { lock (_entries) return _entries.Count; } }

        private void MakeRoom(DateTime now)
        {
            var stale = new List<IPAddress>();
            IPAddress? oldest = null;
            DateTime oldestSeen = DateTime.MaxValue;

            foreach (KeyValuePair<IPAddress, Entry> pair in _entries)
            {
                if (pair.Value.BlockedUntil <= now && now - pair.Value.LastSeen > Window) stale.Add(pair.Key);
                // Never one that is shut out right now.
                if (pair.Value.BlockedUntil <= now && pair.Value.LastSeen < oldestSeen) { oldestSeen = pair.Value.LastSeen; oldest = pair.Key; }
            }

            foreach (IPAddress key in stale) _entries.Remove(key);
            if (_entries.Count >= MaxAddresses && oldest != null) _entries.Remove(oldest);
        }

        // ---- everybody at once ----------------------------------------------

        /// <summary>Wrong sign-ins from anywhere, inside <see cref="BrakeWindow"/>, before every answer is slowed.</summary>
        public const int BrakeAfter = 30;
        public static readonly TimeSpan BrakeWindow = TimeSpan.FromMinutes(1);

        /// <summary>How long an answer to a password waits while the brake is on.</summary>
        public static readonly TimeSpan BrakeDelay = TimeSpan.FromSeconds(2);

        private readonly Queue<DateTime> _recent = new();

        /// <summary>
        /// Whether passwords are being guessed from many addresses at once.
        /// The limit per address stops one PC; somebody with a whole block of
        /// IPv6 addresses has thousands of them. While this is true every
        /// answer to a password, right or wrong, should wait
        /// <see cref="BrakeDelay"/>: the same for both, so the wait says nothing.
        /// </summary>
        public bool Braking
        {
            get
            {
                DateTime now = _now();
                lock (_entries)
                {
                    while (_recent.Count > 0 && now - _recent.Peek() > BrakeWindow) _recent.Dequeue();
                    return _recent.Count >= BrakeAfter;
                }
            }
        }

        /// <summary>Waits out the brake, when it is on. Call before answering any password.</summary>
        public void Slow()
        {
            if (Braking) System.Threading.Thread.Sleep(BrakeDelay);
        }

        /// <summary>
        /// One IPv4 address, or one IPv6 /64: a single home or server is handed
        /// a whole /64, and could otherwise try from a fresh address each time.
        /// </summary>
        private static IPAddress KeyFor(IPAddress address)
        {
            address = ServedFolder.Plain(address);
            if (address.AddressFamily != AddressFamily.InterNetworkV6) return address;

            byte[] bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return new IPAddress(bytes);
        }
    }
}
