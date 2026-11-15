using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Server;
using SMBLibrary.Win32;

namespace ExplorerNative
{
    /// <summary>
    /// One folder or drive shared over SMB 2 and 3, on a port of the person's
    /// choosing, to one user name and password (7.0.0, Conner: "add samba or
    /// something... you choose the port, the drive or folder to host").
    ///
    /// The protocol is SMBLibrary's (Tal Aloni, LGPL; the third package
    /// exception, because an SMB server is not something to write in an
    /// afternoon). Windows' own file sharing was not an answer: it only
    /// listens on port 445, needs an administrator for every share, and
    /// cannot share the Google Drive letter at all, which is a network drive
    /// to Windows. This one opens files the way any program on this PC does,
    /// so the Drive letter is a folder like any other.
    ///
    /// SMB 1 is never offered.
    /// </summary>
    internal sealed class SmbFileServer : IDisposable
    {
        private readonly string _folder;
        private readonly int _port;
        private readonly string _share;
        private readonly string _user;
        private readonly string _password;
        private readonly bool _readOnly;
        private SMBServer? _server;

        /// <summary>Wrong passwords from one address before it is refused for a while.</summary>
        private const int TriesInWindow = 10;
        private static readonly TimeSpan TryWindow = TimeSpan.FromMinutes(10);

        public SmbFileServer(string folder, int port, string share, string user, string password, bool readOnly)
        {
            _folder = folder;
            _port = port;
            _share = share;
            _user = user;
            _password = password;
            _readOnly = readOnly;
        }

        /// <summary>
        /// The library only offers ports 445 and 139 in public, and has the
        /// same Start with a port in private. Looked up once; a test fails if
        /// an update of the package takes it away.
        /// </summary>
        internal static readonly MethodInfo? StartOnPort = typeof(SMBServer).GetMethod("Start",
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(IPAddress), typeof(SMBTransportType), typeof(int), typeof(bool), typeof(bool), typeof(bool), typeof(TimeSpan?) },
            null);

        /// <summary>Throws InvalidOperationException with a whole sentence when it cannot listen.</summary>
        public void Start()
        {
            if (StartOnPort == null)
                throw new InvalidOperationException("The SMB server could not start: this build cannot choose its port.");

            var full = Path.GetFullPath(_folder);
            INTFileStore store = new NTDirectoryFileSystem(full);
            // A whole drive: see DriveRootStore.
            if (full.EndsWith(Path.DirectorySeparatorChar)) store = new DriveRootStore((NTDirectoryFileSystem)store, full);

            var share = new FileSystemShare(_share, store);
            share.AccessRequested += (_, e) =>
            {
                e.Allow = string.Equals(e.UserName, _user, StringComparison.OrdinalIgnoreCase) &&
                          (!_readOnly || e.RequestedAccess == FileAccess.Read);
            };
            var shares = new SMBShareCollection { share };

            var accounts = new IndependentNTLMAuthenticationProvider(
                name => string.Equals(name, _user, StringComparison.OrdinalIgnoreCase) ? _password : null,
                TriesInWindow, TryWindow);
            var server = new SMBServer(shares, new GSSProvider(accounts));
            try
            {
                StartOnPort.Invoke(server, new object?[]
                {
                    IPAddress.Any, SMBTransportType.DirectTCPTransport, _port,
                    false,  // SMB 1: never
                    true, true, (TimeSpan?)null,
                });
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                throw new InvalidOperationException(inner is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                    ? $"Port {_port} is already in use by another program."
                    : inner is SocketException { SocketErrorCode: SocketError.AccessDenied }
                        ? $"Windows would not let Explorer Native listen on port {_port}."
                        : $"The SMB server could not start: {inner.Message}", inner);
            }
            _server = server;
        }

        public void Dispose()
        {
            var server = _server;
            _server = null;
            try { server?.Stop(); } catch { }
        }

        /// <summary>
        /// A drive's own top folder. The library builds every path as the
        /// folder, a backslash, and the rest, and a drive's folder already
        /// ends in one ("G:\"), so every open of anything on a shared drive
        /// failed with "name invalid". This opens and renames with the path
        /// put together properly and hands everything else to the library's
        /// store, which only ever sees the handles.
        /// </summary>
        private sealed class DriveRootStore : INTFileStore
        {
            private delegate NTStatus Opener(out IntPtr handle, out FileStatus fileStatus, string nativePath,
                AccessMask desiredAccess, long allocationSize, SMBLibrary.FileAttributes fileAttributes, ShareAccess shareAccess,
                CreateDisposition createDisposition, CreateOptions createOptions);

            [DllImport("ntdll.dll", ExactSpelling = true, SetLastError = false)]
            private static extern NTStatus NtSetInformationFile(IntPtr handle, out IO_STATUS_BLOCK ioStatusBlock,
                byte[] fileInformation, uint length, uint fileInformationClass);

            private readonly NTDirectoryFileSystem _inner;
            private readonly Opener _open;

            /// <summary>The NT path of the drive, with no backslash on the end: "\??\G:".</summary>
            private readonly string _root;

            public DriveRootStore(NTDirectoryFileSystem inner, string drive)
            {
                _inner = inner;
                _root = @"\??\" + drive.TrimEnd(Path.DirectorySeparatorChar);
                var method = typeof(NTDirectoryFileSystem).GetMethod("CreateFile", BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new[]
                    {
                        typeof(IntPtr).MakeByRefType(), typeof(FileStatus).MakeByRefType(), typeof(string), typeof(AccessMask),
                        typeof(long), typeof(SMBLibrary.FileAttributes), typeof(ShareAccess), typeof(CreateDisposition), typeof(CreateOptions),
                    }, null)
                    ?? throw new InvalidOperationException("The SMB server cannot share a whole drive in this build.");
                _open = (Opener)method.CreateDelegate(typeof(Opener), inner);
            }

            private string Native(string path) => path.StartsWith('\\') ? _root + path : _root + "\\" + path;

            public NTStatus CreateFile(out object handle, out FileStatus fileStatus, string path, AccessMask desiredAccess,
                SMBLibrary.FileAttributes fileAttributes, ShareAccess shareAccess, CreateDisposition createDisposition,
                CreateOptions createOptions, SecurityContext securityContext)
            {
                // As the library's own open does, for the same reasons: its
                // directory listing and change notices need a synchronous handle.
                desiredAccess |= AccessMask.SYNCHRONIZE;
                createOptions &= ~CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT;
                createOptions |= CreateOptions.FILE_SYNCHRONOUS_IO_ALERT;
                if ((createOptions & CreateOptions.FILE_NO_INTERMEDIATE_BUFFERING) > 0 &&
                    ((FileAccessMask)desiredAccess & FileAccessMask.FILE_APPEND_DATA) > 0)
                    desiredAccess = (AccessMask)((uint)desiredAccess & ~(uint)FileAccessMask.FILE_APPEND_DATA);

                var status = _open(out var opened, out fileStatus, Native(path), desiredAccess, 0, fileAttributes, shareAccess,
                    createDisposition, createOptions);
                handle = opened;
                return status;
            }

            public NTStatus SetFileInformation(object handle, FileInformation information)
            {
                if (information is FileRenameInformationType2 rename)
                {
                    var local = new FileRenameInformationType2
                    {
                        ReplaceIfExists = rename.ReplaceIfExists,
                        FileName = Native(rename.FileName),
                    };
                    var buffer = local.GetBytes();
                    return NtSetInformationFile((IntPtr)handle, out _, buffer, (uint)buffer.Length, (uint)local.FileInformationClass);
                }
                return _inner.SetFileInformation(handle, information);
            }

            public NTStatus CloseFile(object handle) => _inner.CloseFile(handle);
            public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount) =>
                _inner.ReadFile(out data, handle, offset, maxCount);
            public NTStatus WriteFile(out int numberOfBytesWritten, object handle, long offset, byte[] data) =>
                _inner.WriteFile(out numberOfBytesWritten, handle, offset, data);
            public NTStatus FlushFileBuffers(object handle) => _inner.FlushFileBuffers(handle);
            public NTStatus LockFile(object handle, long byteOffset, long length, bool exclusiveLock) =>
                _inner.LockFile(handle, byteOffset, length, exclusiveLock);
            public NTStatus UnlockFile(object handle, long byteOffset, long length) => _inner.UnlockFile(handle, byteOffset, length);
            public NTStatus QueryDirectory(out List<QueryDirectoryFileInformation> result, object handle, string fileName,
                FileInformationClass informationClass) => _inner.QueryDirectory(out result, handle, fileName, informationClass);
            public NTStatus GetFileInformation(out FileInformation result, object handle, FileInformationClass informationClass) =>
                _inner.GetFileInformation(out result, handle, informationClass);
            public NTStatus GetFileSystemInformation(out FileSystemInformation result, FileSystemInformationClass informationClass) =>
                _inner.GetFileSystemInformation(out result, informationClass);
            public NTStatus SetFileSystemInformation(FileSystemInformation information) => _inner.SetFileSystemInformation(information);
            public NTStatus GetSecurityInformation(out SecurityDescriptor result, object handle, SecurityInformation securityInformation) =>
                _inner.GetSecurityInformation(out result, handle, securityInformation);
            public NTStatus SetSecurityInformation(object handle, SecurityInformation securityInformation, SecurityDescriptor securityDescriptor) =>
                _inner.SetSecurityInformation(handle, securityInformation, securityDescriptor);
            public NTStatus NotifyChange(out object ioRequest, object handle, NotifyChangeFilter completionFilter, bool watchTree,
                int outputBufferSize, OnNotifyChangeCompleted onNotifyChangeCompleted, object context) =>
                _inner.NotifyChange(out ioRequest, handle, completionFilter, watchTree, outputBufferSize, onNotifyChangeCompleted, context);
            public NTStatus Cancel(object ioRequest) => _inner.Cancel(ioRequest);
            public NTStatus DeviceIOControl(object handle, uint ctlCode, byte[] input, out byte[] output, int maxOutputLength) =>
                _inner.DeviceIOControl(handle, ctlCode, input, out output, maxOutputLength);
        }
    }
}
