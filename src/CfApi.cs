using System;
using System.Runtime.InteropServices;

namespace ExplorerNative
{
    /// <summary>
    /// The Windows Cloud Files API (<c>cldapi.dll</c>) — the usermode half of
    /// what OneDrive is built on, and the reason a cloud provider needs no
    /// kernel driver of its own.
    ///
    /// The same warning applies here as to the media engine's vtables: these are
    /// structures whose fields are read by offset. One field declared in the
    /// wrong place, or one missing pad, and the callback reads a length out of
    /// what was meant to be a pointer. Nothing about that is visible in the
    /// source, so every structure carries its own expected size and
    /// <see cref="CfApi.CheckLayout"/> compares them before anything is called.
    /// </summary>
    internal static class CfApi
    {
        private const string Dll = "cldapi.dll";

        // ---- enumerations -------------------------------------------------

        public enum CF_CALLBACK_TYPE : uint
        {
            FETCH_DATA = 0,
            CANCEL_FETCH_DATA = 2,
            FETCH_PLACEHOLDERS = 3,
            NONE = 0xffffffff,
        }

        public enum CF_OPERATION_TYPE : uint
        {
            TRANSFER_DATA = 0,
            TRANSFER_PLACEHOLDERS = 4,
        }

        [Flags]
        public enum CF_REGISTER_FLAGS : uint
        {
            DISABLE_ON_DEMAND_POPULATION_ON_ROOT = 2,
            MARK_IN_SYNC_ON_ROOT = 4,
        }

        [Flags]
        public enum CF_CONNECT_FLAGS : uint
        {
            // Checked against cfapi.h: this was declared as 2, which is
            // REQUIRE_PROCESS_INFO, and the path was only ever there because
            // Windows happens to supply it anyway.
            REQUIRE_PROCESS_INFO = 2,
            REQUIRE_FULL_FILE_PATH = 4,
        }

        /// <summary>Offset of ProcessId in CF_PROCESS_INFO, after the DWORD StructSize.</summary>
        public const int ProcessInfoProcessIdOffset = 4;

        [Flags]
        public enum CF_PLACEHOLDER_CREATE_FLAGS : uint
        {
            NONE = 0,
            MARK_IN_SYNC = 2,
        }

        [Flags]
        public enum CF_UPDATE_FLAGS : uint
        {
            MARK_IN_SYNC = 2,
            DEHYDRATE = 4,
        }

        /// <summary>
        /// Rewrites a placeholder's size, dates and identity in place. Measured:
        /// a handle opened for attributes alone is enough, and
        /// CF_PLACEHOLDER_CREATE_FLAG_SUPERSEDE is not a substitute — it took
        /// the new size and kept the old identity.
        /// </summary>
        [DllImport(Dll)]
        public static extern int CfUpdatePlaceholder(
            Microsoft.Win32.SafeHandles.SafeFileHandle fileHandle,
            ref CF_FS_METADATA fsMetadata,
            IntPtr fileIdentity,
            uint fileIdentityLength,
            IntPtr dehydrateRangeArray,
            uint dehydrateRangeCount,
            CF_UPDATE_FLAGS updateFlags,
            IntPtr updateUsn,
            IntPtr overlapped);

        /// <summary>
        /// How much of a file Windows insists on having before a read is allowed
        /// to succeed. PARTIAL is the one that matters to us: it lets a read of
        /// the middle of a file fetch only the middle, which is the whole point
        /// of streaming rather than downloading.
        /// </summary>
        public enum CF_HYDRATION_POLICY_PRIMARY : ushort
        {
            PARTIAL = 0,
        }

        [Flags]
        public enum CF_HYDRATION_POLICY_MODIFIER : ushort
        {
            STREAMING_ALLOWED = 2,
        }

        public enum CF_POPULATION_POLICY_PRIMARY : ushort
        {
            // FULL is 2 in cfapi.h, not the 1 it was once declared as — which may
            // be all "FULL refuses CfCreatePlaceholders on the root" ever measured.
            PARTIAL = 0,
        }

        // ---- structures ---------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        public struct CF_HYDRATION_POLICY
        {
            public CF_HYDRATION_POLICY_PRIMARY Primary;
            public CF_HYDRATION_POLICY_MODIFIER Modifier;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CF_POPULATION_POLICY
        {
            public CF_POPULATION_POLICY_PRIMARY Primary;
            public ushort Modifier;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CF_SYNC_POLICIES
        {
            public uint StructSize;
            public CF_HYDRATION_POLICY Hydration;
            public CF_POPULATION_POLICY Population;
            public uint InSync;
            public uint HardLink;
            public uint PlaceholderManagement;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CF_SYNC_REGISTRATION
        {
            public uint StructSize;
            public IntPtr ProviderName;
            public IntPtr ProviderVersion;
            public IntPtr SyncRootIdentity;
            public uint SyncRootIdentityLength;
            public IntPtr FileIdentity;
            public uint FileIdentityLength;
            public Guid ProviderId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FILE_BASIC_INFO
        {
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public long ChangeTime;
            public uint FileAttributes;
            private readonly uint _pad;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CF_FS_METADATA
        {
            public FILE_BASIC_INFO BasicInfo;
            public long FileSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CF_PLACEHOLDER_CREATE_INFO
        {
            public IntPtr RelativeFileName;
            public CF_FS_METADATA FsMetadata;
            public IntPtr FileIdentity;
            public uint FileIdentityLength;
            public CF_PLACEHOLDER_CREATE_FLAGS Flags;
            public int Result;
            public long CreateUsn;
        }

        /// <summary>
        /// What Windows hands the callback. Only a few fields are wanted — the
        /// transfer key, the identity blob we stored, and the size — but every
        /// field before them has to be declared or the offsets are wrong.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct CF_CALLBACK_INFO
        {
            public uint StructSize;
            private readonly uint _pad0;
            public long ConnectionKey;
            public IntPtr CallbackContext;
            public IntPtr VolumeGuidName;
            public IntPtr VolumeDosName;
            public uint VolumeSerialNumber;
            private readonly uint _pad1;
            public long SyncRootFileId;
            public IntPtr SyncRootIdentity;
            public uint SyncRootIdentityLength;
            private readonly uint _pad2;
            public long FileId;
            public long FileSize;
            public IntPtr FileIdentity;
            public uint FileIdentityLength;
            private readonly uint _pad3;
            public IntPtr NormalizedPath;
            public long TransferKey;
            public byte PriorityHint;
            private readonly byte _pad4a;
            private readonly ushort _pad4b;
            private readonly uint _pad4c;
            public IntPtr CorrelationVector;
            public IntPtr ProcessInfo;
            public long RequestKey;
        }

        /// <summary>
        /// A union in C. Declared with explicit offsets for the one arm we serve,
        /// because guessing at the padding of a union is how the wrong number
        /// ends up in the length.
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        public struct CF_CALLBACK_PARAMETERS_FETCH_DATA
        {
            [FieldOffset(0)] public uint ParamSize;
            [FieldOffset(8)] public uint Flags;
            [FieldOffset(16)] public long RequiredFileOffset;
            [FieldOffset(24)] public long RequiredLength;
            [FieldOffset(32)] public long OptionalFileOffset;
            [FieldOffset(40)] public long OptionalLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CF_OPERATION_INFO
        {
            public uint StructSize;
            public CF_OPERATION_TYPE Type;
            public long ConnectionKey;
            public long TransferKey;
            public IntPtr CorrelationVector;
            public IntPtr SyncStatus;
            public long RequestKey;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct CF_OPERATION_PARAMETERS_TRANSFER_DATA
        {
            [FieldOffset(0)] public uint ParamSize;
            [FieldOffset(8)] public uint Flags;
            [FieldOffset(12)] public int CompletionStatus;
            [FieldOffset(16)] public IntPtr Buffer;
            [FieldOffset(24)] public long Offset;
            [FieldOffset(32)] public long Length;
        }

        /// <summary>
        /// Windows asking what is inside a directory. Arrives only when the
        /// population policy is PARTIAL — with FULL, the provider is expected to
        /// have placed everything up front, which for a whole Drive would be
        /// thousands of listings at mount time for a folder nobody opened.
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        public struct CF_CALLBACK_PARAMETERS_FETCH_PLACEHOLDERS
        {
            [FieldOffset(0)] public uint ParamSize;
            [FieldOffset(8)] public uint Flags;
            [FieldOffset(16)] public IntPtr Pattern;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct CF_CALLBACK_PARAMETERS_CANCEL
        {
            [FieldOffset(0)] public uint ParamSize;
            [FieldOffset(8)] public uint Flags;
            [FieldOffset(16)] public long FileOffset;
            [FieldOffset(24)] public long Length;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct CF_OPERATION_PARAMETERS_TRANSFER_PLACEHOLDERS
        {
            [FieldOffset(0)] public uint ParamSize;
            [FieldOffset(8)] public uint Flags;
            [FieldOffset(12)] public int CompletionStatus;
            [FieldOffset(16)] public long PlaceholderTotalCount;
            [FieldOffset(24)] public IntPtr PlaceholderArray;
            [FieldOffset(32)] public uint PlaceholderCount;
            [FieldOffset(36)] public uint EntriesProcessed;
        }

        [Flags]
        public enum CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS : uint
        {
            DISABLE_ON_DEMAND_POPULATION = 2,
        }

        /// <summary>
        /// "The cloud operation was canceled by user" — the request went away
        /// because the handle closed or the reader gave up, and answering it now
        /// is an error rather than a late success. Windows reads ahead
        /// speculatively and cancels what it turns out not to need, so this is
        /// ordinary traffic and not a fault.
        /// </summary>
        public const int ErrorCloudFileCancelled = unchecked((int)0x8007018E);

        /// <summary>
        /// A placeholder that is already there.
        ///
        /// Not a failure. The sync root directory cannot reliably be emptied on
        /// startup — deleting a registered sync root's placeholders throws and
        /// the throw is swallowed — so the next run finds them still present and
        /// CfCreatePlaceholders reports this for the batch. Treating it as fatal
        /// aborts the whole mount: no drive letter, and a log line that blames
        /// placeholder creation for what is really a delete that did not happen.
        /// </summary>
        public const int ErrorAlreadyExists = unchecked((int)0x800700B7);

        /// <summary>
        /// "Already exists" as a per-entry result, in any of the spellings it
        /// can take: CfCreatePlaceholders reports the HRESULT, and a transfer
        /// through CfExecute may carry the NTSTATUS or ERROR_FILE_EXISTS form.
        /// </summary>
        public static bool IsAlreadyThere(int result) =>
            result == ErrorAlreadyExists ||
            result == unchecked((int)0xC0000035) ||   // STATUS_OBJECT_NAME_COLLISION
            result == unchecked((int)0x80070050);     // ERROR_FILE_EXISTS

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void CF_CALLBACK(
            ref CF_CALLBACK_INFO callbackInfo,
            ref CF_CALLBACK_PARAMETERS_FETCH_DATA callbackParameters);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void CF_CALLBACK_PLACEHOLDERS(
            ref CF_CALLBACK_INFO callbackInfo,
            ref CF_CALLBACK_PARAMETERS_FETCH_PLACEHOLDERS callbackParameters);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void CF_CALLBACK_CANCEL(
            ref CF_CALLBACK_INFO callbackInfo,
            ref CF_CALLBACK_PARAMETERS_CANCEL callbackParameters);

        [DllImport(Dll)]
        public static extern int CfExecute(
            ref CF_OPERATION_INFO opInfo,
            ref CF_OPERATION_PARAMETERS_TRANSFER_PLACEHOLDERS opParams);

        /// <summary>
        /// "Still working on it."
        ///
        /// Windows puts a clock on every FETCH_DATA callback, and a read it
        /// decides has taken too long comes back to whoever asked as
        /// <c>0x8007018D</c> — "the cloud operation was not completed before the
        /// time-out period expired". Nothing in the provider is told; the file
        /// simply fails to copy.
        ///
        /// This is the documented way to hold that clock off, and the only one:
        /// a provider that reports progress is a provider that has not hung. It
        /// costs one call before the fetch that might be slow, which is the one
        /// where the bytes are not already in memory.
        /// </summary>
        [DllImport(Dll)]
        public static extern int CfReportProviderProgress(
            long connectionKey,
            long transferKey,
            long providerProgressTotal,
            long providerProgressCompleted);

        // CfConvertToPlaceholder was declared here, with the CreateFileW import
        // and the access constants that existed only to open a handle to pass to
        // it. Converting the *sync root* to a placeholder is the one call that
        // would let the root record having been populated, and Windows refuses
        // it outright with 0x8007017C, "the cloud operation is invalid" — so the
        // root is seeded by us at mount and DISABLE_ON_DEMAND_POPULATION_ON_ROOT
        // stops Windows asking a question it can never record as answered. None
        // of it was ever called. It is gone rather than kept "in case", because
        // a declared P/Invoke reads as a thing this code does.

        // ---- a drive letter -------------------------------------------------

        [Flags]
        public enum DosDeviceFlags : uint
        {
            RAW_TARGET_PATH = 0x00000001,
            REMOVE_DEFINITION = 0x00000002,
            EXACT_MATCH_ON_REMOVE = 0x00000004,
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DefineDosDeviceW(
            DosDeviceFlags flags, string deviceName, string? targetPath);

        /// <summary>
        /// What a DOS device name currently points at — "\??\C:\some\directory"
        /// for a subst'd letter, a real device path for an actual volume.
        ///
        /// This is how an orphaned letter is recognised without having to guess
        /// which one it might be: ask each letter what it is, and act on the ones
        /// that name a directory of ours.
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint QueryDosDeviceW(
            string? deviceName, [Out] char[] targetPath, uint max);

        [StructLayout(LayoutKind.Sequential)]
        public struct CF_CALLBACK_REGISTRATION
        {
            public CF_CALLBACK_TYPE Type;
            private readonly uint _pad;
            public IntPtr Callback;
        }

        // ---- entry points -------------------------------------------------

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        public static extern int CfRegisterSyncRoot(
            string syncRootPath,
            ref CF_SYNC_REGISTRATION registration,
            ref CF_SYNC_POLICIES policies,
            CF_REGISTER_FLAGS registerFlags);

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        public static extern int CfUnregisterSyncRoot(string syncRootPath);

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        public static extern int CfConnectSyncRoot(
            string syncRootPath,
            [In] CF_CALLBACK_REGISTRATION[] callbackTable,
            IntPtr callbackContext,
            CF_CONNECT_FLAGS connectFlags,
            out long connectionKey);

        [DllImport(Dll)]
        public static extern int CfDisconnectSyncRoot(long connectionKey);

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        public static extern int CfCreatePlaceholders(
            string baseDirectoryPath,
            [In, Out] CF_PLACEHOLDER_CREATE_INFO[] placeholderArray,
            uint placeholderCount,
            CF_PLACEHOLDER_CREATE_FLAGS createFlags,
            out uint entriesProcessed);

        [DllImport(Dll)]
        public static extern int CfExecute(
            ref CF_OPERATION_INFO opInfo,
            ref CF_OPERATION_PARAMETERS_TRANSFER_DATA opParams);

        /// <summary>CF_PLACEHOLDER_INFO_BASIC: pin state, sync state, two ids, then the identity.</summary>
        public const int PlaceholderInfoBasic = 0;

        /// <summary>Where the identity blob starts in CF_PLACEHOLDER_BASIC_INFO.</summary>
        public const int BasicInfoIdentityLengthOffset = 24;

        [DllImport(Dll)]
        public static extern int CfGetPlaceholderInfo(
            Microsoft.Win32.SafeHandles.SafeFileHandle fileHandle,
            int infoClass,
            [Out] byte[] infoBuffer,
            uint infoBufferLength,
            out uint returnedLength);

        public const uint FileReadAttributes = 0x80;
        public const uint FileFlagBackupSemantics = 0x02000000;
        public const uint FileFlagOpenReparsePoint = 0x00200000;

        /// <summary>
        /// Opened for its attributes only, which reads nothing of a placeholder's
        /// contents and so fetches nothing.
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string fileName, uint desiredAccess, System.IO.FileShare shareMode, IntPtr securityAttributes,
            System.IO.FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        // ---- layout self-check --------------------------------------------

        /// <summary>
        /// Every structure above, against the size the C header produces on a
        /// 64-bit target. This is the same argument as
        /// <c>AudioPlayer.SelfCheck</c>: none of it can be reasoned about from
        /// the source, so it is measured instead — and a mismatch here is caught
        /// before a callback reads a pointer out of the wrong eight bytes.
        /// </summary>
        public static string? CheckLayout()
        {
            (string Name, int Actual, int Expected)[] sizes =
            {
                ("CF_SYNC_POLICIES", Marshal.SizeOf<CF_SYNC_POLICIES>(), 24),
                // 72, not 64: StructSize and both identity lengths each leave
                // four bytes of padding behind them, so ProviderId starts at 56.
                ("CF_SYNC_REGISTRATION", Marshal.SizeOf<CF_SYNC_REGISTRATION>(), 72),
                ("FILE_BASIC_INFO", Marshal.SizeOf<FILE_BASIC_INFO>(), 40),
                ("CF_FS_METADATA", Marshal.SizeOf<CF_FS_METADATA>(), 48),
                ("CF_PLACEHOLDER_CREATE_INFO", Marshal.SizeOf<CF_PLACEHOLDER_CREATE_INFO>(), 88),
                ("CF_CALLBACK_INFO", Marshal.SizeOf<CF_CALLBACK_INFO>(), 152),
                ("CF_OPERATION_INFO", Marshal.SizeOf<CF_OPERATION_INFO>(), 48),
                ("CF_CALLBACK_REGISTRATION", Marshal.SizeOf<CF_CALLBACK_REGISTRATION>(), 16),
            };

            foreach (var (name, actual, expected) in sizes)
                if (actual != expected)
                    return $"{name} is {actual} bytes, expected {expected}";

            return null;
        }
    }
}

