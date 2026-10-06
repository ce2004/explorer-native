using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>
    /// Sends something to the Recycle Bin, and never quietly anywhere else.
    ///
    /// The VB helper this replaced asks the shell for no confirmation at all,
    /// and without confirmation the shell does not warn when a file cannot be
    /// recycled — on a share, on most USB sticks, or when it is bigger than the
    /// bin — it simply deletes it for good. The application had just asked
    /// "Really send it to the Recycle Bin?" and then announced "Deleted".
    ///
    /// FOF_WANTNUKEWARNING is the flag that brings the shell's own "this will be
    /// deleted permanently" question back, and only in that case: an ordinary
    /// recycle still asks nothing, because the application already has.
    /// </summary>
    internal static class ShellDelete
    {
        private const uint FO_DELETE = 3;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOERRORUI = 0x0400;
        private const ushort FOF_WANTNUKEWARNING = 0x4000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCT operation);

        /// <summary>
        /// Recycles <paramref name="path"/>. False when somebody answered No to
        /// deleting it permanently; throws when it could not be done.
        /// </summary>
        public static bool Recycle(string path, IntPtr owner)
        {
            var operation = new SHFILEOPSTRUCT
            {
                hwnd = owner,
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT |
                                  FOF_NOERRORUI | FOF_WANTNUKEWARNING),
            };

            int result = SHFileOperationW(ref operation);
            if (operation.fAnyOperationsAborted != 0) return false;
            if (result != 0)
                throw new Win32Exception(result, $"the shell could not delete it (error 0x{result:X})");
            return true;
        }

        // ---------------- with no window at all ----------------

        /// <summary>
        /// Recycles <paramref name="path"/> without ever putting anything on the
        /// screen, for callers nobody is watching (the Drive folder monitor).
        /// Null when it went to the Recycle Bin; otherwise why it was kept.
        ///
        /// <see cref="Recycle"/> is right for a person who has just pressed
        /// Delete: when the file cannot be recycled the shell asks before
        /// deleting it for good. In the background that question is a dialog on
        /// a worker thread with no owner, and the answer to it is always "keep
        /// it". So this never deletes for good and never asks: a drive with no
        /// Recycle Bin, or a file the bin will not take, is kept and reported.
        ///
        /// IFileOperation rather than SHFileOperation, because it says, item by
        /// item and before acting, whether the item would be recycled
        /// (TSF_DELETE_RECYCLE_IF_POSSIBLE in PreDeleteItem), and lets the delete
        /// be refused there.
        /// </summary>
        public static string? RecycleWithoutUi(string path)
        {
            if (!ConnectFiles.HasRecycleBin(path))
                return "that drive has no Recycle Bin, so it would have been deleted for good";

            string? answer = "it could not be sent to the Recycle Bin";
            var thread = new Thread(() =>
            {
                try { answer = RecycleOnSta(path); }
                catch (Exception ex) { answer = "it could not be sent to the Recycle Bin: " + ex.Message; }
            })
            {
                IsBackground = true,
                Name = "recycle without a window",
            };

            // The shell's file operations are apartment-threaded.
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromMinutes(2)))
                return "the Recycle Bin did not answer in time";
            return answer;
        }

        private const uint FOF_NOCONFIRMMKDIR = 0x0200;
        private const uint FOFX_RECYCLEONDELETE = 0x00080000;
        private const uint TSF_DELETE_RECYCLE_IF_POSSIBLE = 0x80;
        private const int E_ABORT = unchecked((int)0x80004004);

        private static string? RecycleOnSta(string path)
        {
            var guid = typeof(IShellItemLite).GUID;
            int hr = SHCreateItemFromParsingName(System.IO.Path.GetFullPath(path), IntPtr.Zero, ref guid, out var item);
            if (hr < 0 || item == IntPtr.Zero)
                return $"it could not be found to delete (error 0x{hr:X})";

            object? created = null;
            try
            {
                created = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"))!);
                var operation = (IFileOperation)created!;

                var sink = new RecycleOnly();
                operation.SetOperationFlags((uint)(FOF_ALLOWUNDO | FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI) |
                                            FOF_NOCONFIRMMKDIR | FOFX_RECYCLEONDELETE);
                operation.DeleteItem(item, sink);
                int performed = operation.PerformOperations();

                if (sink.Refused) return "it could only have been deleted for good, so it was kept";
                if (sink.Result < 0) return $"the shell could not recycle it (error 0x{sink.Result:X})";
                if (performed < 0 && performed != E_ABORT) return $"the shell could not recycle it (error 0x{performed:X})";
                if (!sink.Deleted) return "the shell did not delete it";
                return null;
            }
            finally
            {
                Marshal.Release(item);
                if (created != null) try { Marshal.FinalReleaseComObject(created); } catch { }
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(
            string path, IntPtr bindContext, ref Guid riid, out IntPtr item);

        /// <summary>Only its IID is used: the item is passed on as a raw pointer.</summary>
        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemLite { }

        /// <summary>
        /// IFileOperation, every slot in order. A slot declared out of place
        /// calls its neighbour, so all twenty are written out, and only the
        /// ones used have real signatures.
        /// </summary>
        [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperation
        {
            void Advise(IntPtr sink, out uint cookie);                                    // 3
            void Unadvise(uint cookie);                                                   // 4
            void SetOperationFlags(uint flags);                                           // 5
            void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);    // 6
            void SetProgressDialog(IntPtr dialog);                                        // 7
            void SetProperties(IntPtr properties);                                        // 8
            void SetOwnerWindow(IntPtr owner);                                            // 9
            void ApplyPropertiesToItem(IntPtr item);                                      // 10
            void ApplyPropertiesToItems(IntPtr items);                                    // 11
            void RenameItem(IntPtr item, IntPtr newName, IntPtr sink);                    // 12
            void RenameItems(IntPtr items, IntPtr newName);                               // 13
            void MoveItem(IntPtr item, IntPtr folder, IntPtr newName, IntPtr sink);       // 14
            void MoveItems(IntPtr items, IntPtr folder);                                  // 15
            void CopyItem(IntPtr item, IntPtr folder, IntPtr copyName, IntPtr sink);      // 16
            void CopyItems(IntPtr items, IntPtr folder);                                  // 17
            void DeleteItem(IntPtr item, IFileOperationProgressSink sink);                // 18
            void DeleteItems(IntPtr items);                                               // 19
            void NewItem(IntPtr folder, uint attributes, IntPtr name, IntPtr template, IntPtr sink); // 20
            [PreserveSig] int PerformOperations();                                        // 21
            [PreserveSig] int GetAnyOperationsAborted(out int aborted);                   // 22
        }

        [ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperationProgressSink
        {
            [PreserveSig] int StartOperations();
            [PreserveSig] int FinishOperations(int result);
            [PreserveSig] int PreRenameItem(uint flags, IntPtr item, IntPtr newName);
            [PreserveSig] int PostRenameItem(uint flags, IntPtr item, IntPtr newName, int result, IntPtr created);
            [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName);
            [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName, int result, IntPtr created);
            [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName);
            [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName, int result, IntPtr created);
            [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
            [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created);
            [PreserveSig] int PreNewItem(uint flags, IntPtr folder, IntPtr newName);
            [PreserveSig] int PostNewItem(uint flags, IntPtr folder, IntPtr newName, IntPtr template, uint attributes, int result, IntPtr created);
            [PreserveSig] int UpdateProgress(uint total, uint done);
            [PreserveSig] int ResetTimer();
            [PreserveSig] int PauseTimer();
            [PreserveSig] int ResumeTimer();
        }

        /// <summary>Lets a delete through only when the shell says it will be recycled.</summary>
        [ComVisible(true)]
        private sealed class RecycleOnly : IFileOperationProgressSink
        {
            public bool Refused;
            public bool Deleted;
            public int Result;

            public int PreDeleteItem(uint flags, IntPtr item)
            {
                if ((flags & TSF_DELETE_RECYCLE_IF_POSSIBLE) != 0) return 0;
                Refused = true;
                return E_ABORT;
            }

            public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created)
            {
                Result = result;
                Deleted = result >= 0;
                return 0;
            }

            public int StartOperations() => 0;
            public int FinishOperations(int result) => 0;
            public int PreRenameItem(uint flags, IntPtr item, IntPtr newName) => 0;
            public int PostRenameItem(uint flags, IntPtr item, IntPtr newName, int result, IntPtr created) => 0;
            public int PreMoveItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName) => 0;
            public int PostMoveItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName, int result, IntPtr created) => 0;
            public int PreCopyItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName) => 0;
            public int PostCopyItem(uint flags, IntPtr item, IntPtr folder, IntPtr newName, int result, IntPtr created) => 0;
            public int PreNewItem(uint flags, IntPtr folder, IntPtr newName) => 0;
            public int PostNewItem(uint flags, IntPtr folder, IntPtr newName, IntPtr template, uint attributes, int result, IntPtr created) => 0;
            public int UpdateProgress(uint total, uint done) => 0;
            public int ResetTimer() => 0;
            public int PauseTimer() => 0;
            public int ResumeTimer() => 0;
        }
    }
}
