using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace ExplorerNative
{
    /// <summary>
    /// The connection to WinFsp, the free file system driver that puts Google
    /// Drive on a real drive letter (winfsp.dev, GPLv3 with an exception for
    /// open source programs such as this one).
    ///
    /// Our own bindings rather than WinFsp's .NET assembly: the application is
    /// one file, the DLL has to be the one for the processor it runs on (ARM64
    /// or x64), and every callback has to be caught so a mistake here fails
    /// one request rather than the whole process. The layouts below are checked
    /// against the sizes winfsp.h asserts (<see cref="CheckLayout"/>).
    /// </summary>
    internal static unsafe class WinFsp
    {
        // ---- status codes -------------------------------------------------

        public const int Success = 0;
        public const int BufferOverflow = unchecked((int)0x80000005);
        public const int Unsuccessful = unchecked((int)0xC0000001);
        public const int NotImplemented = unchecked((int)0xC0000002);
        public const int InvalidParameter = unchecked((int)0xC000000D);
        public const int InvalidDeviceRequest = unchecked((int)0xC0000010);
        public const int EndOfFile = unchecked((int)0xC0000011);
        public const int AccessDenied = unchecked((int)0xC0000022);
        public const int ObjectNameInvalid = unchecked((int)0xC0000033);
        public const int ObjectNameNotFound = unchecked((int)0xC0000034);
        public const int ObjectNameCollision = unchecked((int)0xC0000035);
        public const int ObjectPathNotFound = unchecked((int)0xC000003A);
        public const int DiskFull = unchecked((int)0xC000007F);
        public const int InsufficientResources = unchecked((int)0xC000009A);
        public const int MediaWriteProtected = unchecked((int)0xC00000A2);
        public const int FileIsADirectory = unchecked((int)0xC00000BA);
        public const int IoTimeout = unchecked((int)0xC00000B5);
        public const int UnexpectedNetworkError = unchecked((int)0xC00000C4);
        public const int UnexpectedIoError = unchecked((int)0xC00000E9);
        public const int DirectoryNotEmpty = unchecked((int)0xC0000101);
        public const int NotADirectory = unchecked((int)0xC0000103);
        public const int CannotDelete = unchecked((int)0xC0000121);
        public const int NetworkUnreachable = unchecked((int)0xC000023C);
        public const int CantWait = unchecked((int)0xC00000D8);

        // ---- request flags ------------------------------------------------

        public const uint FileDirectoryFile = 0x00000001;
        public const uint FileNonDirectoryFile = 0x00000040;
        public const uint FileDeleteOnClose = 0x00001000;

        public const uint FileWriteData = 0x0002;
        public const uint FileAppendData = 0x0004;

        public const uint CleanupDelete = 0x01;
        public const uint CleanupSetAllocationSize = 0x02;
        public const uint CleanupSetArchiveBit = 0x10;
        public const uint CleanupSetLastAccessTime = 0x20;
        public const uint CleanupSetLastWriteTime = 0x40;
        public const uint CleanupSetChangeTime = 0x80;

        public const uint AttributeReadOnly = 0x01;
        public const uint AttributeHidden = 0x02;
        public const uint AttributeDirectory = 0x10;
        public const uint AttributeArchive = 0x20;
        public const uint AttributeNormal = 0x80;
        public const uint InvalidFileAttributes = 0xFFFFFFFF;

        // FILE_NOTIFY_CHANGE_* and FILE_ACTION_*, for Notify.
        public const uint ChangeFileName = 0x01;
        public const uint ChangeDirName = 0x02;
        public const uint ChangeAttributes = 0x04;
        public const uint ChangeSize = 0x08;
        public const uint ChangeLastWrite = 0x10;
        public const uint ActionAdded = 1;
        public const uint ActionRemoved = 2;
        public const uint ActionModified = 3;

        // ---- layouts --------------------------------------------------------

        /// <summary>FSP_FSCTL_FILE_INFO: 72 bytes.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct FileInfo
        {
            public uint FileAttributes;
            public uint ReparseTag;
            public ulong AllocationSize;
            public ulong FileSize;
            public ulong CreationTime;
            public ulong LastAccessTime;
            public ulong LastWriteTime;
            public ulong ChangeTime;
            public ulong IndexNumber;
            public uint HardLinks;
            public uint EaSize;
        }

        /// <summary>FSP_FSCTL_VOLUME_INFO: 88 bytes.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct VolumeInfo
        {
            public ulong TotalSize;
            public ulong FreeSize;
            public ushort VolumeLabelLength;
            public fixed char VolumeLabel[32];
        }

        /// <summary>FSP_FSCTL_VOLUME_PARAMS, version 1: 504 bytes. Bit fields as plain words.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 504)]
        public struct VolumeParams
        {
            [FieldOffset(0)] public ushort Version;
            [FieldOffset(2)] public ushort SectorSize;
            [FieldOffset(4)] public ushort SectorsPerAllocationUnit;
            [FieldOffset(6)] public ushort MaxComponentLength;
            [FieldOffset(8)] public ulong VolumeCreationTime;
            [FieldOffset(16)] public uint VolumeSerialNumber;
            [FieldOffset(20)] public uint TransactTimeout;
            [FieldOffset(24)] public uint IrpTimeout;
            [FieldOffset(28)] public uint IrpCapacity;
            [FieldOffset(32)] public uint FileInfoTimeout;
            [FieldOffset(36)] public uint Flags;
            [FieldOffset(40)] public fixed char Prefix[192];
            [FieldOffset(424)] public fixed char FileSystemName[16];
            [FieldOffset(456)] public uint AdditionalFlags;
            [FieldOffset(460)] public uint VolumeInfoTimeout;
            [FieldOffset(464)] public uint DirInfoTimeout;
            [FieldOffset(468)] public uint SecurityTimeout;
            [FieldOffset(472)] public uint StreamInfoTimeout;
            [FieldOffset(476)] public uint EaTimeout;
            [FieldOffset(480)] public uint FsextControlCode;
        }

        // VolumeParams.Flags, in winfsp.h's bit order.
        public const uint CaseSensitiveSearch = 1u << 0;
        public const uint CasePreservedNames = 1u << 1;
        public const uint UnicodeOnDisk = 1u << 2;
        public const uint PersistentAcls = 1u << 3;
        public const uint PostCleanupWhenModifiedOnly = 1u << 10;
        public const uint FlushAndPurgeOnCleanup = 1u << 14;
        public const uint UmFileContextIsUserContext2 = 1u << 16;

        // VolumeParams.AdditionalFlags.
        public const uint VolumeInfoTimeoutValid = 1u << 0;
        public const uint DirInfoTimeoutValid = 1u << 1;
        public const uint SecurityTimeoutValid = 1u << 2;

        /// <summary>The fixed part of FSP_FSCTL_DIR_INFO; the name follows it.</summary>
        public const int DirInfoHeader = 104;

        /// <summary>The fixed part of FSP_FSCTL_NOTIFY_INFO; the name follows it.</summary>
        public const int NotifyInfoHeader = 12;

        /// <summary>Where UserContext sits in FSP_FILE_SYSTEM on a 64-bit process.</summary>
        private const int UserContextOffset = 8;

        /// <summary>
        /// Null when every layout here has the size winfsp.h asserts, otherwise
        /// which one does not. A wrong size is a wrong offset, and a wrong
        /// offset in a file system is a file somebody loses.
        /// </summary>
        internal static string? CheckLayout()
        {
            var problems = new List<string>();
            void Expect(string name, int expected, int actual)
            {
                if (expected != actual) problems.Add($"{name} is {actual}, not {expected}");
            }
            Expect("FSP_FSCTL_FILE_INFO", 72, sizeof(FileInfo));
            Expect("FSP_FSCTL_VOLUME_INFO", 88, sizeof(VolumeInfo));
            Expect("FSP_FSCTL_VOLUME_PARAMS", 504, sizeof(VolumeParams));
            Expect("FSP_FSCTL_DIR_INFO", 104, 8 + sizeof(FileInfo) + 24);
            Expect("pointer", 8, IntPtr.Size);
            return problems.Count == 0 ? null : string.Join("; ", problems);
        }

        // ---- finding and loading the DLL -------------------------------------

        private static readonly object LoadGate = new();
        private static IntPtr _library;
        private static string? _loadProblem;

        private static delegate* unmanaged<char*, VolumeParams*, IntPtr, IntPtr*, int> _create;
        private static delegate* unmanaged<IntPtr, void> _delete;
        private static delegate* unmanaged<IntPtr, char*, int> _setMountPoint;
        private static delegate* unmanaged<IntPtr, void> _removeMountPoint;
        private static delegate* unmanaged<IntPtr, uint, int> _startDispatcher;
        private static delegate* unmanaged<IntPtr, void> _stopDispatcher;
        private static delegate* unmanaged<IntPtr*, byte, int*, byte> _acquireDirBuffer;
        private static delegate* unmanaged<IntPtr*, byte*, int*, byte> _fillDirBuffer;
        private static delegate* unmanaged<IntPtr*, void> _releaseDirBuffer;
        private static delegate* unmanaged<IntPtr*, char*, IntPtr, uint, uint*, void> _readDirBuffer;
        private static delegate* unmanaged<IntPtr*, void> _deleteDirBuffer;
        private static delegate* unmanaged<IntPtr, uint, int> _notifyBegin;
        private static delegate* unmanaged<IntPtr, byte*, nuint, int> _notify;
        private static delegate* unmanaged<IntPtr, int> _notifyEnd;
        private static delegate* unmanaged<uint, int> _ntStatusFromWin32;

        /// <summary>The release of WinFsp this application ships and installs.</summary>
        internal const string BundledVersion = "2.1.25156";

        /// <summary>
        /// Where WinFsp's DLL for this process's processor is, or null when
        /// WinFsp is not installed. The installer records its folder in the
        /// 32-bit view of the registry, whichever processor the machine has.
        /// </summary>
        internal static string? DllPath()
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                using var key = hive.OpenSubKey(@"SOFTWARE\WinFsp");
                if (key?.GetValue("InstallDir") is not string folder || folder.Length == 0) return null;
                var arch = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.Arm64 => "a64",
                    Architecture.X64 => "x64",
                    Architecture.X86 => "x86",
                    _ => null,
                };
                if (arch == null) return null;
                var path = Path.Combine(folder, "bin", $"winfsp-{arch}.dll");
                return File.Exists(path) ? path : null;
            }
            catch { return null; }
        }

        /// <summary>Whether WinFsp is installed on this machine at all.</summary>
        internal static bool Installed => DllPath() != null;

        /// <summary>
        /// Loads WinFsp, once. False with <paramref name="problem"/> saying why
        /// when it is missing or will not load.
        /// </summary>
        internal static bool TryLoad(out string? problem)
        {
            lock (LoadGate)
            {
                if (_library != IntPtr.Zero) { problem = null; return true; }

                var path = DllPath();
                if (path == null)
                {
                    problem = _loadProblem = "WinFsp is not installed";
                    return false;
                }

                if (CheckLayout() is { } wrong)
                {
                    problem = _loadProblem = "WinFsp layout check failed: " + wrong;
                    return false;
                }

                try
                {
                    var lib = NativeLibrary.Load(path);
                    IntPtr Get(string name) => NativeLibrary.GetExport(lib, name);

                    _create = (delegate* unmanaged<char*, VolumeParams*, IntPtr, IntPtr*, int>)Get("FspFileSystemCreate");
                    _delete = (delegate* unmanaged<IntPtr, void>)Get("FspFileSystemDelete");
                    _setMountPoint = (delegate* unmanaged<IntPtr, char*, int>)Get("FspFileSystemSetMountPoint");
                    _removeMountPoint = (delegate* unmanaged<IntPtr, void>)Get("FspFileSystemRemoveMountPoint");
                    _startDispatcher = (delegate* unmanaged<IntPtr, uint, int>)Get("FspFileSystemStartDispatcher");
                    _stopDispatcher = (delegate* unmanaged<IntPtr, void>)Get("FspFileSystemStopDispatcher");
                    _acquireDirBuffer = (delegate* unmanaged<IntPtr*, byte, int*, byte>)Get("FspFileSystemAcquireDirectoryBuffer");
                    _fillDirBuffer = (delegate* unmanaged<IntPtr*, byte*, int*, byte>)Get("FspFileSystemFillDirectoryBuffer");
                    _releaseDirBuffer = (delegate* unmanaged<IntPtr*, void>)Get("FspFileSystemReleaseDirectoryBuffer");
                    _readDirBuffer = (delegate* unmanaged<IntPtr*, char*, IntPtr, uint, uint*, void>)Get("FspFileSystemReadDirectoryBuffer");
                    _deleteDirBuffer = (delegate* unmanaged<IntPtr*, void>)Get("FspFileSystemDeleteDirectoryBuffer");
                    _notifyBegin = (delegate* unmanaged<IntPtr, uint, int>)Get("FspFileSystemNotifyBegin");
                    _notify = (delegate* unmanaged<IntPtr, byte*, nuint, int>)Get("FspFileSystemNotify");
                    _notifyEnd = (delegate* unmanaged<IntPtr, int>)Get("FspFileSystemNotifyEnd");
                    _ntStatusFromWin32 = (delegate* unmanaged<uint, int>)Get("FspNtStatusFromWin32");

                    _library = lib;
                    problem = _loadProblem = null;
                    return true;
                }
                catch (Exception ex)
                {
                    problem = _loadProblem = "WinFsp would not load: " + ex.Message;
                    return false;
                }
            }
        }

        /// <summary>An NTSTATUS for a Win32 error, as WinFsp maps it.</summary>
        internal static int FromWin32(int error)
        {
            if (error == 0) return Success;
            return _ntStatusFromWin32 != null ? _ntStatusFromWin32((uint)error) : Unsuccessful;
        }

        /// <summary>The NTSTATUS for an exception from file I/O on a local file.</summary>
        internal static int FromException(Exception error) => error switch
        {
            FileNotFoundException => ObjectNameNotFound,
            DirectoryNotFoundException => ObjectPathNotFound,
            UnauthorizedAccessException => AccessDenied,
            IOException io when (io.HResult & 0xFFFF) == 112 || (io.HResult & 0xFFFF) == 39 => DiskFull,
            IOException io when (io.HResult & 0xFFFF0000) == 0x80070000 => FromWin32(io.HResult & 0xFFFF),
            OperationCanceledException => IoTimeout,
            TimeoutException => IoTimeout,
            _ => UnexpectedIoError,
        };

        // ---- the host --------------------------------------------------------

        /// <summary>
        /// One mounted file system: WinFsp's object, our callbacks, and the
        /// letter it is on. Created, mounted, dispatched, and torn down in the
        /// reverse order by <see cref="Dispose"/>.
        /// </summary>
        internal sealed class Host : IDisposable
        {
            private readonly FileSystem _owner;
            private IntPtr _fileSystem;
            private GCHandle _self;
            private IntPtr _interface;
            private bool _dispatching;
            private readonly object _gate = new();

            /// <summary>The letter it is on now ("G:"), or null while it is on none.</summary>
            public string? MountPoint { get; private set; }

            public Host(FileSystem owner) => _owner = owner;

            /// <summary>
            /// Creates the volume: a network-type file system (WinFsp.Net), so
            /// Windows does not make a Recycle Bin or an index on it, with
            /// <paramref name="prefix"/> as its \\server\share name.
            /// </summary>
            public void Create(string prefix, string fileSystemName, uint fileInfoTimeout, uint threads)
            {
                if (!TryLoad(out var problem)) throw new InvalidOperationException(problem);

                var vp = new VolumeParams
                {
                    Version = (ushort)sizeof(VolumeParams),
                    SectorSize = 4096,
                    SectorsPerAllocationUnit = 1,
                    MaxComponentLength = 255,
                    VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc(),
                    VolumeSerialNumber = (uint)(DateTime.UtcNow.Ticks / 10000),
                    IrpTimeout = 300000,
                    IrpCapacity = 1000,
                    FileInfoTimeout = fileInfoTimeout,
                    Flags = CasePreservedNames | UnicodeOnDisk |
                            PostCleanupWhenModifiedOnly | FlushAndPurgeOnCleanup |
                            UmFileContextIsUserContext2,
                    AdditionalFlags = VolumeInfoTimeoutValid | SecurityTimeoutValid,
                    VolumeInfoTimeout = 10000,
                    SecurityTimeout = 60000,
                };
                Copy(prefix, vp.Prefix, 191);
                Copy(fileSystemName, vp.FileSystemName, 15);

                _interface = BuildInterface();
                _self = GCHandle.Alloc(_owner);

                IntPtr fs;
                int status;
                fixed (char* device = "WinFsp.Net")
                    status = _create(device, &vp, _interface, &fs);
                if (status != Success)
                {
                    Release();
                    throw new InvalidOperationException($"WinFsp would not create the volume (0x{status:X8})");
                }

                *(IntPtr*)((byte*)fs + UserContextOffset) = GCHandle.ToIntPtr(_self);
                _fileSystem = fs;

                status = _startDispatcher(fs, threads);
                if (status != Success)
                {
                    Release();
                    throw new InvalidOperationException($"WinFsp would not start (0x{status:X8})");
                }
                _dispatching = true;
            }

            private static void Copy(string text, char* into, int most)
            {
                int n = Math.Min(text.Length, most);
                for (int i = 0; i < n; i++) into[i] = text[i];
                into[n] = '\0';
            }

            /// <summary>Puts the volume on <paramref name="letter"/> ("G:"). False when it is taken.</summary>
            public bool Mount(string letter, out int status)
            {
                lock (_gate)
                {
                    if (_fileSystem == IntPtr.Zero) { status = Unsuccessful; return false; }
                    if (MountPoint != null) { status = Success; return true; }
                    fixed (char* point = letter)
                        status = _setMountPoint(_fileSystem, point);
                    if (status != Success) return false;
                    MountPoint = letter;
                    return true;
                }
            }

            /// <summary>
            /// Takes the volume off its letter. Handles already open keep working;
            /// nothing new can be opened through the letter until it is mounted again.
            /// </summary>
            public void Unmount()
            {
                lock (_gate)
                {
                    if (_fileSystem == IntPtr.Zero || MountPoint == null) return;
                    _removeMountPoint(_fileSystem);
                    MountPoint = null;
                }
            }

            /// <summary>
            /// Tells Windows that something under the volume changed by itself
            /// (on the web, or through the Drive API), so File Explorer windows
            /// and the kernel's caches catch up. Names start with a backslash and
            /// are relative to the volume. False when a rename held it off.
            /// </summary>
            public bool Notify(IReadOnlyList<(string Path, uint Filter, uint Action)> changes)
            {
                IntPtr fs;
                lock (_gate) fs = _fileSystem;
                if (fs == IntPtr.Zero || changes.Count == 0) return true;

                int size = 0;
                foreach (var c in changes) size += Align8(NotifyInfoHeader + c.Path.Length * 2);
                var buffer = new byte[size];
                fixed (byte* start = buffer)
                {
                    int at = 0;
                    foreach (var (path, filter, action) in changes)
                    {
                        int one = NotifyInfoHeader + path.Length * 2;
                        byte* p = start + at;
                        *(ushort*)p = (ushort)one;
                        *(uint*)(p + 4) = filter;
                        *(uint*)(p + 8) = action;
                        fixed (char* name = path)
                            Buffer.MemoryCopy(name, p + NotifyInfoHeader, path.Length * 2, path.Length * 2);
                        at += Align8(one);
                    }

                    if (_notifyBegin(fs, 500) != Success) return false;
                    try { _notify(fs, start, (nuint)at); }
                    finally { _notifyEnd(fs); }
                }
                return true;
            }

            private static int Align8(int n) => (n + 7) & ~7;

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_fileSystem != IntPtr.Zero && MountPoint != null)
                    {
                        try { _removeMountPoint(_fileSystem); } catch { }
                        MountPoint = null;
                    }
                }
                Release();
            }

            private void Release()
            {
                IntPtr fs;
                lock (_gate) { fs = _fileSystem; _fileSystem = IntPtr.Zero; }
                if (fs != IntPtr.Zero)
                {
                    if (_dispatching) { try { _stopDispatcher(fs); } catch { } _dispatching = false; }
                    try { _delete(fs); } catch { }
                }
                if (_self.IsAllocated) _self.Free();
                if (_interface != IntPtr.Zero) { Marshal.FreeHGlobal(_interface); _interface = IntPtr.Zero; }
            }
        }

        /// <summary>Directory listing buffers, so marker handling and sorting are WinFsp's own.</summary>
        internal static class DirBuffer
        {
            /// <summary>
            /// Answers a ReadDirectory from <paramref name="buffer"/>, filling it
            /// first from <paramref name="list"/> when this is a fresh read (no
            /// marker) or the buffer is empty.
            /// </summary>
            public static int Read(ref IntPtr buffer, char* marker, IntPtr output, uint length, uint* transferred,
                Func<IEnumerable<(string Name, FileInfo Info)>> list)
            {
                int status = Success;
                IntPtr local = buffer;
                if (_acquireDirBuffer(&local, marker == null ? (byte)1 : (byte)0, &status) != 0)
                {
                    try
                    {
                        var block = new byte[DirInfoHeader + 512 * 2];
                        foreach (var (name, info) in list())
                        {
                            int bytes = DirInfoHeader + name.Length * 2;
                            if (bytes > block.Length) block = new byte[bytes];
                            fixed (byte* p = block)
                            {
                                new Span<byte>(p, DirInfoHeader).Clear();
                                *(ushort*)p = (ushort)bytes;
                                *(FileInfo*)(p + 8) = info;
                                fixed (char* n = name)
                                    Buffer.MemoryCopy(n, p + DirInfoHeader, name.Length * 2, name.Length * 2);
                                if (_fillDirBuffer(&local, p, &status) == 0) break;
                            }
                        }
                    }
                    finally { _releaseDirBuffer(&local); }
                }
                buffer = local;
                if (status != Success) return status;
                _readDirBuffer(&local, marker, output, length, transferred);
                return Success;
            }

            public static void Delete(ref IntPtr buffer)
            {
                if (buffer == IntPtr.Zero) return;
                IntPtr local = buffer;
                try { _deleteDirBuffer(&local); } catch { }
                buffer = IntPtr.Zero;
            }
        }

        // ---- the file system, as managed code sees it --------------------------

        /// <summary>
        /// What a file system built on WinFsp implements. Every method answers an
        /// NTSTATUS; none may throw (the trampolines catch anyway). File contexts
        /// are ordinary objects, kept alive by the host between Open and Close.
        /// </summary>
        internal abstract class FileSystem
        {
            /// <summary>The security descriptor every file has, self-relative.</summary>
            public abstract byte[] Security { get; }

            public abstract int GetVolumeInfo(out ulong total, out ulong free, out string label);
            public abstract int GetAttributes(string path, out uint attributes);
            public abstract int Create(string path, uint createOptions, uint grantedAccess, uint attributes,
                out object? context, out FileInfo info, out string? normalized);
            public abstract int Open(string path, uint createOptions, uint grantedAccess,
                out object? context, out FileInfo info, out string? normalized);
            public abstract int Overwrite(object context, uint attributes, bool replaceAttributes, out FileInfo info);
            public abstract void Cleanup(object context, string? path, uint flags);
            public abstract void Close(object context);
            public abstract int Read(object context, IntPtr buffer, ulong offset, uint length, out uint transferred);
            public abstract int Write(object context, IntPtr buffer, ulong offset, uint length, bool toEnd,
                bool constrained, out uint transferred, out FileInfo info);
            public abstract int Flush(object? context, out FileInfo info);
            public abstract int GetFileInfo(object context, out FileInfo info);
            public abstract int SetBasicInfo(object context, uint attributes, ulong created, ulong accessed,
                ulong written, ulong changed, out FileInfo info);
            public abstract int SetFileSize(object context, ulong size, bool allocation, out FileInfo info);
            public abstract int CanDelete(object context, string path);
            public abstract int Rename(object context, string path, string newPath, bool replace);
            public abstract int ReadDirectory(object context, char* pattern, char* marker, IntPtr buffer,
                uint length, out uint transferred);
            public virtual void DispatcherStopped(bool normally) { }
        }

        // ---- trampolines ---------------------------------------------------------

        private static FileSystem Owner(IntPtr fs) =>
            (FileSystem)GCHandle.FromIntPtr(*(IntPtr*)((byte*)fs + UserContextOffset)).Target!;

        private static object Context(IntPtr handle) => GCHandle.FromIntPtr(handle).Target!;

        private static string? Text(char* p) => p == null ? null : new string(p);

        private static IntPtr BuildInterface()
        {
            var table = (IntPtr*)Marshal.AllocHGlobal(64 * IntPtr.Size);
            for (int i = 0; i < 64; i++) table[i] = IntPtr.Zero;
            table[0] = (IntPtr)(delegate* unmanaged<IntPtr, VolumeInfo*, int>)&OnGetVolumeInfo;
            table[2] = (IntPtr)(delegate* unmanaged<IntPtr, char*, uint*, IntPtr, nuint*, int>)&OnGetSecurityByName;
            table[3] = (IntPtr)(delegate* unmanaged<IntPtr, char*, uint, uint, uint, IntPtr, ulong, IntPtr*, FileInfo*, int>)&OnCreate;
            table[4] = (IntPtr)(delegate* unmanaged<IntPtr, char*, uint, uint, IntPtr*, FileInfo*, int>)&OnOpen;
            table[5] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, uint, byte, ulong, FileInfo*, int>)&OnOverwrite;
            table[6] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, char*, uint, void>)&OnCleanup;
            table[7] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnClose;
            table[8] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, ulong, uint, uint*, int>)&OnRead;
            table[9] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, ulong, uint, byte, byte, uint*, FileInfo*, int>)&OnWrite;
            table[10] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, FileInfo*, int>)&OnFlush;
            table[11] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, FileInfo*, int>)&OnGetFileInfo;
            table[12] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, uint, ulong, ulong, ulong, ulong, FileInfo*, int>)&OnSetBasicInfo;
            table[13] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, ulong, byte, FileInfo*, int>)&OnSetFileSize;
            table[14] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, char*, int>)&OnCanDelete;
            table[15] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, char*, char*, byte, int>)&OnRename;
            table[16] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, nuint*, int>)&OnGetSecurity;
            table[17] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr, int>)&OnSetSecurity;
            table[18] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, char*, char*, IntPtr, uint, uint*, int>)&OnReadDirectory;
            table[32] = (IntPtr)(delegate* unmanaged<IntPtr, byte, void>)&OnDispatcherStopped;
            return (IntPtr)table;
        }

        [UnmanagedCallersOnly]
        private static int OnGetVolumeInfo(IntPtr fs, VolumeInfo* info)
        {
            try
            {
                int status = Owner(fs).GetVolumeInfo(out var total, out var free, out var label);
                if (status != Success) return status;
                info->TotalSize = total;
                info->FreeSize = free;
                int n = Math.Min(label.Length, 32);
                for (int i = 0; i < n; i++) info->VolumeLabel[i] = label[i];
                info->VolumeLabelLength = (ushort)(n * 2);
                return Success;
            }
            catch { return UnexpectedIoError; }
        }

        private static int CopySecurity(byte[] sd, IntPtr into, nuint* size)
        {
            if (size == null) return Success;
            if (into == IntPtr.Zero || *size < (nuint)sd.Length)
            {
                bool wanted = into != IntPtr.Zero;
                *size = (nuint)sd.Length;
                return wanted ? BufferOverflow : Success;
            }
            Marshal.Copy(sd, 0, into, sd.Length);
            *size = (nuint)sd.Length;
            return Success;
        }

        [UnmanagedCallersOnly]
        private static int OnGetSecurityByName(IntPtr fs, char* name, uint* attributes, IntPtr sd, nuint* sdSize)
        {
            try
            {
                var owner = Owner(fs);
                int status = owner.GetAttributes(Text(name) ?? "\\", out var attrs);
                if (status != Success) return status;
                if (attributes != null) *attributes = attrs;
                return CopySecurity(owner.Security, sd, sdSize);
            }
            catch { return UnexpectedIoError; }
        }

        private static void Normalize(FileInfo* info, string? normalized)
        {
            // FSP_FSCTL_OPEN_FILE_INFO: the FileInfo, then the buffer and its size.
            if (normalized == null) return;
            var buffer = *(char**)((byte*)info + 72);
            ushort* room = (ushort*)((byte*)info + 80);
            if (buffer == null || *room < normalized.Length * 2) return;
            for (int i = 0; i < normalized.Length; i++) buffer[i] = normalized[i];
            *room = (ushort)(normalized.Length * 2);
        }

        [UnmanagedCallersOnly]
        private static int OnCreate(IntPtr fs, char* name, uint options, uint access, uint attributes,
            IntPtr sd, ulong allocation, IntPtr* context, FileInfo* info)
        {
            try
            {
                int status = Owner(fs).Create(Text(name) ?? "\\", options, access, attributes,
                    out var made, out var fi, out var normalized);
                if (status != Success) return status;
                *context = GCHandle.ToIntPtr(GCHandle.Alloc(made));
                *info = fi;
                Normalize(info, normalized);
                return Success;
            }
            catch { return UnexpectedIoError; }
        }

        [UnmanagedCallersOnly]
        private static int OnOpen(IntPtr fs, char* name, uint options, uint access, IntPtr* context, FileInfo* info)
        {
            try
            {
                int status = Owner(fs).Open(Text(name) ?? "\\", options, access,
                    out var opened, out var fi, out var normalized);
                if (status != Success) return status;
                *context = GCHandle.ToIntPtr(GCHandle.Alloc(opened));
                *info = fi;
                Normalize(info, normalized);
                return Success;
            }
            catch { return UnexpectedIoError; }
        }

        [UnmanagedCallersOnly]
        private static int OnOverwrite(IntPtr fs, IntPtr context, uint attributes, byte replace, ulong allocation, FileInfo* info)
        {
            try
            {
                int status = Owner(fs).Overwrite(Context(context), attributes, replace != 0, out var fi);
                if (status == Success) *info = fi;
                return status;
            }
            catch { return UnexpectedIoError; }
        }

        [UnmanagedCallersOnly]
        private static void OnCleanup(IntPtr fs, IntPtr context, char* name, uint flags)
        {
            try { Owner(fs).Cleanup(Context(context), Text(name), flags); }
            catch { }
        }

        [UnmanagedCallersOnly]
        private static void OnClose(IntPtr fs, IntPtr context)
        {
            try
            {
                var handle = GCHandle.FromIntPtr(context);
                try { Owner(fs).Close(handle.Target!); }
                finally { handle.Free(); }
            }
            catch { }
        }

        [UnmanagedCallersOnly]
        private static int OnRead(IntPtr fs, IntPtr context, IntPtr buffer, ulong offset, uint length, uint* transferred)
        {
            try
            {
                int status = Owner(fs).Read(Context(context), buffer, offset, length, out var n);
                *transferred = n;
                return status;
            }
            catch { return UnexpectedIoError; }
        }

        [UnmanagedCallersOnly]
        private static int OnWrite(IntPtr fs, IntPtr context, IntPtr buffer, ulong offset, uint length,
            byte toEnd, byte constrained, uint* transferred, FileInfo* info)
        {
            try
            {
                int status = Owner(fs).Write(Context(context), buffer, offset, length, toEnd != 0, constrained != 0,
                    out var n, out var fi);
                *transferred = n;
                if (status == Success) *info = fi;
                return status;
            }
            catch { return UnexpectedIoError; }
        }

        [UnmanagedCallersOnly]
        private static int OnFlush(IntPtr fs, IntPtr context, FileInfo* info)
        {
            try
            {
                int status = Owner(fs).Flush(context == IntPtr.Zero ? null : Context(context), out var fi);
                if (status == Success && info != null && context != IntPtr.Zero) *info = fi;
                return status;
            }
            catch { return UnexpectedIoError; }
        }

        [UnmanagedCallersOnly]
        private static int OnGetFileInfo(IntPtr fs, IntPtr context, FileInfo* info)
        {
            try
            {
                int status = Owner(fs).GetFileInfo(Context(context), out var fi);
                if (status == Success) *info = fi;
                return status;
            }
            catch { return UnexpectedIoError; }
        }

        [UnmanagedCallersOnly]
        private static int OnSetBasicInfo(IntPtr fs, IntPtr context, uint attributes, ulong created, ulong accessed,
            ulong written, ulong changed, FileInfo* info)
        {
            try
            {
                int status = Owner(fs).SetBasicInfo(Context(context), attributes, created, accessed, written, changed,
                    out var fi);
                if (status == Success) *info = fi;
                return status;
            }
            catch { return UnexpectedIoError; }
        }

        [UnmanagedCallersOnly]
        private static int OnSetFileSize(IntPtr fs, IntPtr context, ulong size, byte allocation, FileInfo* info)
        {
            try
            {
                int status = Owner(fs).SetFileSize(Context(context), size, allocation != 0, out var fi);
                if (status == Success) *info = fi;
                return status;
            }
            catch { return UnexpectedIoError; }
        }

        [UnmanagedCallersOnly]
        private static int OnCanDelete(IntPtr fs, IntPtr context, char* name)
        {
            try { return Owner(fs).CanDelete(Context(context), Text(name) ?? "\\"); }
            catch { return UnexpectedIoError; }
        }

        [UnmanagedCallersOnly]
        private static int OnRename(IntPtr fs, IntPtr context, char* name, char* newName, byte replace)
        {
            try { return Owner(fs).Rename(Context(context), Text(name) ?? "\\", Text(newName) ?? "\\", replace != 0); }
            catch { return UnexpectedIoError; }
        }

        [UnmanagedCallersOnly]
        private static int OnGetSecurity(IntPtr fs, IntPtr context, IntPtr sd, nuint* size)
        {
            try { return CopySecurity(Owner(fs).Security, sd, size); }
            catch { return UnexpectedIoError; }
        }

        [UnmanagedCallersOnly]
        private static int OnSetSecurity(IntPtr fs, IntPtr context, uint what, IntPtr descriptor)
        {
            // Google Drive has no Windows permissions to change. Accepted and
            // ignored, so a copy that carries security along does not fail on it.
            return Success;
        }

        [UnmanagedCallersOnly]
        private static int OnReadDirectory(IntPtr fs, IntPtr context, char* pattern, char* marker,
            IntPtr buffer, uint length, uint* transferred)
        {
            try
            {
                int status = Owner(fs).ReadDirectory(Context(context), pattern, marker, buffer, length, out var n);
                *transferred = n;
                return status;
            }
            catch { return UnexpectedIoError; }
        }

        [UnmanagedCallersOnly]
        private static void OnDispatcherStopped(IntPtr fs, byte normally)
        {
            try { Owner(fs).DispatcherStopped(normally != 0); }
            catch { }
        }

        // ---- security -------------------------------------------------------------

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
            string sddl, uint revision, out IntPtr descriptor, out uint size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        /// <summary>
        /// A self-relative security descriptor from SDDL. The drive's is the
        /// signed-in user and SYSTEM, full control, nobody else: the UNC name a
        /// network file system has is visible to every session on the machine,
        /// and somebody else's session has no business in this Drive.
        /// </summary>
        internal static byte[] SecurityFor(string sddl)
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var sd, out var size))
                throw new InvalidOperationException($"security descriptor refused ({Marshal.GetLastWin32Error()})");
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(sd, bytes, 0, (int)size);
                return bytes;
            }
            finally { LocalFree(sd); }
        }

        /// <summary>The descriptor for the signed-in user's own drive.</summary>
        internal static byte[] UserOnlySecurity()
        {
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            var sid = me.User!.Value;
            return SecurityFor($"O:{sid}G:{sid}D:P(A;;FA;;;{sid})(A;;FA;;;SY)");
        }
    }
}
