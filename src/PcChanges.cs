using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ExplorerNative
{
    /// <summary>
    /// Which folders of a pair's PC folder changed since its last pass, by
    /// path relative to the pair's folder ("" is the folder itself).
    /// <see cref="Folders"/> are listed again on their own; <see cref="Deep"/>
    /// are listed with everything under them (a folder moved or copied in
    /// arrives as one event, its contents as none).
    /// </summary>
    internal sealed class PcMarks
    {
        public HashSet<string> Folders { get; } = new(SyncNameComparer.Instance);
        public HashSet<string> Deep { get; } = new(SyncNameComparer.Instance);

        public int Count => Folders.Count + Deep.Count;

        public void Include(PcMarks other)
        {
            Folders.UnionWith(other.Folders);
            Deep.UnionWith(other.Deep);
        }
    }

    /// <summary>
    /// What a pair's folder watcher has seen since the pass before, for a PC
    /// folder with no change journal (a USB stick, a network share). Trusted
    /// only once a whole walk of the folder has finished with the watcher on
    /// the whole time: until then, and after the watcher overflows or fails,
    /// the next pass walks everything again.
    /// </summary>
    internal sealed class PcWatchJournal
    {
        private readonly object _gate = new();
        private PcMarks _marks = new();
        private bool _trusted;
        private int _epoch;

        /// <summary>A path the watcher reported, relative to the pair's folder, and what happened to it.</summary>
        public void Note(string relative, WatcherChangeTypes kind)
        {
            lock (_gate)
            {
                _marks.Folders.Add(SyncMoves.Parent(relative));
                // A folder that arrived (made, moved or renamed in) is new from top
                // to bottom; one that changed is listed again itself. A path that
                // was a file, or is gone, costs nothing either way.
                if (kind is WatcherChangeTypes.Created or WatcherChangeTypes.Renamed) _marks.Deep.Add(relative);
                else if (kind == WatcherChangeTypes.Changed) _marks.Folders.Add(relative);
            }
        }

        /// <summary>The watcher lost events (its buffer overflowed, or it failed): only a whole walk is sure.</summary>
        public void Lose()
        {
            lock (_gate)
            {
                _trusted = false;
                _epoch++;
                _marks = new PcMarks();
            }
        }

        /// <summary>
        /// What changed since the last pass, taken for this one, or null when
        /// the watcher cannot be trusted and the pass must walk everything.
        /// The epoch goes to <see cref="Walked"/> or <see cref="PutBack"/>.
        /// </summary>
        public PcMarks? Take(out int epoch)
        {
            lock (_gate)
            {
                epoch = _epoch;
                var taken = _marks;
                _marks = new PcMarks();
                return _trusted ? taken : null;
            }
        }

        /// <summary>The pass that took these did not get as far as planning: they are wanted next time.</summary>
        public void PutBack(PcMarks? taken, int epoch)
        {
            if (taken == null) return;
            lock (_gate) if (epoch == _epoch) _marks.Include(taken);
        }

        /// <summary>A whole walk finished; trusted from here on if nothing was lost while it ran.</summary>
        public void Walked(int epoch)
        {
            lock (_gate) if (epoch == _epoch) _trusted = true;
        }
    }

    /// <summary>
    /// The NTFS change journal: every change on a volume, in order, kept by
    /// Windows whether or not the app is running. Read without administrator
    /// rights through a handle to the pair's own folder, which leaves the
    /// names out but keeps which folder each change was in. That is all a
    /// pass needs: those folders are listed again, and every other folder is
    /// as the records say. Only volumes with a journal (NTFS, ReFS); anything
    /// else answers null and the pass walks the folder.
    /// </summary>
    internal static class UsnJournal
    {
        /// <summary>Where the journal of a folder's volume is now, and the folder's own path as Windows has it.</summary>
        internal sealed record Position(ulong JournalId, long First, long Next, string FinalRoot);

        /// <summary>Changes read at most, and folders looked up at most, before a whole walk is cheaper.</summary>
        internal static int MostRecords { get; set; } = 4_000_000;
        internal static int MostFolders { get; set; } = 200_000;

        public static Position? Where(string root)
        {
            try
            {
                using var handle = OpenFolder(root);
                if (handle == null) return null;
                var final = FinalPath(handle);
                if (final == null) return null;
                var buf = Marshal.AllocHGlobal(80);
                try
                {
                    if (!DeviceIoControl(handle, FsctlQueryUsnJournal, IntPtr.Zero, 0, buf, 80, out _, IntPtr.Zero)) return null;
                    return new Position((ulong)Marshal.ReadInt64(buf, 0), Marshal.ReadInt64(buf, 8), Marshal.ReadInt64(buf, 16), final);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return null; }
        }

        /// <summary>
        /// The folders under <paramref name="root"/> with a change between
        /// <paramref name="from"/> and <paramref name="to"/>, or null when that
        /// stretch cannot be read whole (a new journal, one that has wrapped
        /// past it, too much to read). Null means walk everything.
        /// </summary>
        public static PcMarks? Since(string root, ulong journalId, long from, long to)
        {
            try
            {
                using var handle = OpenFolder(root);
                if (handle == null) return null;
                var final = FinalPath(handle);
                var now = Where(root);
                if (final == null || now == null || now.JournalId != journalId || from < now.First || from > to) return null;

                var parents = new HashSet<FileId>();
                var arrived = new HashSet<FileId>();
                if (!Read(handle, journalId, from, to, parents, arrived)) return null;
                if (parents.Count + arrived.Count > MostFolders) return null;

                var marks = new PcMarks();
                foreach (var id in parents)
                    if (Relative(handle, id, final) is { } rel) marks.Folders.Add(rel);
                foreach (var id in arrived)
                    if (Relative(handle, id, final) is { } rel) marks.Deep.Add(rel);
                return marks;
            }
            catch { return null; }
        }

        // ---- reading ----

        private const uint FsctlQueryUsnJournal = 0x000900F4;
        private const uint FsctlReadUnprivilegedUsnJournal = 0x000903AB;
        private const uint ReasonFileCreate = 0x100, ReasonRenameNewName = 0x2000;
        private const uint AttributeDirectory = 0x10;

        private readonly record struct FileId(ulong Low, ulong High, bool Wide);

        private static bool Read(SafeFileHandle handle, ulong journalId, long from, long to,
            HashSet<FileId> parents, HashSet<FileId> arrived)
        {
            const int size = 1 << 16;
            var input = Marshal.AllocHGlobal(48);
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                long at = from;
                int records = 0;
                while (at < to)
                {
                    // READ_USN_JOURNAL_DATA_V0: StartUsn, ReasonMask, ReturnOnlyOnClose,
                    // Timeout, BytesToWaitFor, UsnJournalID.
                    for (int i = 0; i < 48; i += 8) Marshal.WriteInt64(input, i, 0);
                    Marshal.WriteInt64(input, 0, at);
                    Marshal.WriteInt32(input, 8, unchecked((int)0xFFFFFFFF));
                    Marshal.WriteInt64(input, 32, (long)journalId);
                    if (!DeviceIoControl(handle, FsctlReadUnprivilegedUsnJournal, input, 40, buf, size, out int got, IntPtr.Zero))
                        return false;
                    if (got <= 8) return true;
                    long next = Marshal.ReadInt64(buf, 0);
                    int offset = 8;
                    while (offset + 8 <= got)
                    {
                        int length = Marshal.ReadInt32(buf, offset);
                        if (length <= 0 || offset + length > got) break;
                        short major = Marshal.ReadInt16(buf, offset + 4);
                        long usn;
                        uint reason, attributes;
                        FileId self, parent;
                        if (major == 2)
                        {
                            self = new FileId((ulong)Marshal.ReadInt64(buf, offset + 8), 0, false);
                            parent = new FileId((ulong)Marshal.ReadInt64(buf, offset + 16), 0, false);
                            usn = Marshal.ReadInt64(buf, offset + 24);
                            reason = (uint)Marshal.ReadInt32(buf, offset + 40);
                            attributes = (uint)Marshal.ReadInt32(buf, offset + 52);
                        }
                        else if (major == 3)
                        {
                            self = new FileId((ulong)Marshal.ReadInt64(buf, offset + 8), (ulong)Marshal.ReadInt64(buf, offset + 16), true);
                            parent = new FileId((ulong)Marshal.ReadInt64(buf, offset + 24), (ulong)Marshal.ReadInt64(buf, offset + 32), true);
                            usn = Marshal.ReadInt64(buf, offset + 40);
                            reason = (uint)Marshal.ReadInt32(buf, offset + 56);
                            attributes = (uint)Marshal.ReadInt32(buf, offset + 68);
                        }
                        else return false;     // a layout this does not know: walk instead

                        if (usn >= to) return true;
                        parents.Add(parent);
                        if ((attributes & AttributeDirectory) != 0 && (reason & (ReasonFileCreate | ReasonRenameNewName)) != 0)
                            arrived.Add(self);
                        if (++records > MostRecords) return false;
                        offset += length;
                    }
                    if (next <= at) return true;
                    at = next;
                }
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(input);
                Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>
        /// A folder's path under the pair's folder, or null when it is
        /// elsewhere or gone. A gone folder is never needed: its own removal
        /// or move out is a change in its parent, which is still there.
        /// </summary>
        private static string? Relative(SafeFileHandle hint, FileId id, string finalRoot)
        {
            var descriptor = new FileIdDescriptor
            {
                Size = Marshal.SizeOf<FileIdDescriptor>(),
                Type = id.Wide ? 2 : 0,
                Low = id.Low,
                High = id.High,
            };
            using var handle = OpenFileById(hint, ref descriptor, 0, 7, IntPtr.Zero, 0x02000000);
            if (handle.IsInvalid) return null;
            var path = FinalPath(handle);
            if (path == null) return null;
            if (string.Equals(path, finalRoot, StringComparison.OrdinalIgnoreCase)) return "";
            var top = finalRoot.TrimEnd('\\') + "\\";
            if (!path.StartsWith(top, StringComparison.OrdinalIgnoreCase)) return null;
            return path[top.Length..].Replace('\\', '/');
        }

        private static SafeFileHandle? OpenFolder(string path)
        {
            var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            handle.Dispose();
            return null;
        }

        /// <summary>The path Windows has for an open file, without the "\\?\" in front.</summary>
        /// <remarks>
        /// A path longer than the buffer is answered with the size it needs,
        /// and asked again. Taken as no path, a change in a folder more than
        /// 1,023 characters deep read as one outside the pair, and was not
        /// synced until the daily walk.
        /// </remarks>
        private static string? FinalPath(SafeFileHandle handle)
        {
            var text = new char[1024];
            int n = GetFinalPathNameByHandleW(handle, text, text.Length, 0);
            if (n >= text.Length && n <= 32_768)
            {
                text = new char[n + 1];
                n = GetFinalPathNameByHandleW(handle, text, text.Length, 0);
            }
            if (n <= 0 || n >= text.Length) return null;
            var path = new string(text, 0, n);
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
            return path;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdDescriptor
        {
            public int Size;
            public int Type;      // 0 a 64-bit file id, 2 a 128-bit one
            public ulong Low;     // FileId (64-bit) or the low half of ExtendedFileId
            public ulong High;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle OpenFileById(SafeFileHandle hint, ref FileIdDescriptor id, uint access,
            uint share, IntPtr security, uint flags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, int length, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize,
            IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    }
}
