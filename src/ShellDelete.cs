using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

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
    }
}
