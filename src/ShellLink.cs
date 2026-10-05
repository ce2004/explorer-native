using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Windows shortcuts — the `.lnk` files — written and read through the
    /// shell's own <c>IShellLink</c>.
    ///
    /// Called ShellLink and not Shortcut because <see cref="ExplorerNative.Shortcut"/>
    /// is already taken by a key combination, and the two words mean different
    /// things three lines apart in this application: one is "Ctrl+Alt+P" and the
    /// other is a file on disk. A reader who has to work out which is which from
    /// context will eventually get it wrong.
    ///
    /// **The format is not written by hand.** A `.lnk` is a documented binary
    /// structure and it would be perfectly possible to emit one — and it would be
    /// wrong the first time somebody made a shortcut to a UNC path, a junction,
    /// or a folder on a removable drive, because what the shell stores is not the
    /// path but a list of ways of finding the thing again. `CoCreateInstance` of
    /// the shell's own object costs a fraction of a millisecond and is right by
    /// construction.
    ///
    /// **Everything here runs on a thread of its own, and that thread is STA.**
    /// `ShellLink`'s in-process server is registered `ThreadingModel=Apartment`,
    /// so creating one from a thread-pool thread — which .NET leaves in the MTA —
    /// gets a proxy to an object living in some host apartment, and every call
    /// after it is a marshalled round trip. More to the point, `IPersistFile.Save`
    /// writes a file, and it may be writing it to a share that has gone to sleep;
    /// that is exactly the call this codebase does not allow on the thread that
    /// has to redraw. One short-lived STA thread answers both.
    /// </summary>
    public static class ShellLink
    {
        /// <summary>
        /// What Windows adds to the name it is making a shortcut *to*.
        ///
        /// The whole of the original name is kept, extension and all —
        /// "notes.txt - Shortcut.lnk", not "notes - Shortcut.lnk". Explorer's
        /// answer depends on whether the machine is hiding known extensions;
        /// this list never hides one, so the name the shortcut is built from is
        /// the name that was read out on the row it was made from.
        /// </summary>
        public const string Suffix = " - Shortcut";

        public const string Extension = ".lnk";

        private const uint CLSCTX_INPROC_SERVER = 1;
        private const int MAX_PATH = 260;

        private const int STGM_READ = 0x00000000;

        private static readonly Guid CLSID_ShellLink =
            new("00021401-0000-0000-C000-000000000046");

        private static readonly Guid IID_IShellLinkW =
            new("000214F9-0000-0000-C000-000000000046");

        [DllImport("ole32.dll", PreserveSig = false)]
        private static extern void CoCreateInstance(
            ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
                int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
                int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, Guid("0000010B-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName,
                [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        /// <summary>
        /// What a shortcut to <paramref name="targetPath"/> is called — the file
        /// name only, with no folder in front of it.
        ///
        /// Pure, and public, so the naming can be tested without writing
        /// anything: the caller makes it unique against the folder it is going
        /// into, which is a filesystem question and belongs on a worker with the
        /// rest of them.
        /// </summary>
        public static string NameFor(string targetPath)
        {
            var name = Path.GetFileName(targetPath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            // A drive root has no file name — "C:\" trims to "C:" and
            // GetFileName says nothing at all. Its own text is the only name it
            // has, and the colon cannot go into a file name.
            if (string.IsNullOrEmpty(name))
                name = targetPath.Replace(":", "").Trim(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.IsNullOrEmpty(name)) name = "Shortcut";

            return name + Suffix + Extension;
        }

        /// <summary>
        /// Writes a shortcut at <paramref name="linkPath"/> pointing at
        /// <paramref name="targetPath"/>, on an STA thread of its own.
        ///
        /// Faults rather than returning a flag: everything that can go wrong here
        /// — a folder that is read-only, a path too long for the shell, a target
        /// on a share that has gone — is an `HRESULT` turned into an exception on
        /// the way out of the interface, and the caller has a message to say and
        /// a sentence to say it in.
        /// </summary>
        public static Task CreateAsync(string targetPath, string linkPath)
            => OnStaThread(() => Create(targetPath, linkPath));

        /// <summary>
        /// Reads back what a shortcut points at, or an empty string if it points
        /// at nothing a path can describe — a control panel page, say.
        ///
        /// Only the suite calls this. It is here because a shortcut that is
        /// written and never read is a file nobody has checked the inside of, and
        /// "it appeared in the folder" is not the same claim as "it goes where it
        /// was asked to go".
        /// </summary>
        public static Task<string> TargetOfAsync(string linkPath)
            => OnStaThread(() => TargetOf(linkPath));

        private static void Create(string targetPath, string linkPath)
        {
            var link = NewLink();
            try
            {
                link.SetPath(targetPath);

                // What Explorer sets, and for the same reason: a program started
                // from the shortcut should start where the thing it points at lives,
                // not wherever the shortcut happens to be sitting. A target with no
                // parent — a drive root — gets none, rather than an empty string the
                // shell would have to interpret.
                var parent = Path.GetDirectoryName(targetPath.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(parent)) link.SetWorkingDirectory(parent);

                ((IPersistFile)link).Save(linkPath, true);
            }
            finally
            {
                // Released here, on the STA thread that made it. Left to the
                // finaliser it never was: that thread has gone by then.
                Marshal.ReleaseComObject(link);
            }
        }

        private static string TargetOf(string linkPath)
        {
            var link = NewLink();
            try
            {
                ((IPersistFile)link).Load(linkPath, STGM_READ);

                var buffer = new StringBuilder(MAX_PATH);
                link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
                return buffer.ToString();
            }
            finally { Marshal.ReleaseComObject(link); }
        }

        private static IShellLinkW NewLink()
        {
            var clsid = CLSID_ShellLink;
            var iid = IID_IShellLinkW;
            CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out var created);
            return (IShellLinkW)created;
        }

        /// <summary>
        /// Runs one piece of work on a short-lived STA thread and hands back its
        /// result — or rethrows what it threw, with the original stack, because
        /// the whole value of the exceptions above is the message inside them.
        /// </summary>
        private static Task<T> OnStaThread<T>(Func<T> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try { done.SetResult(work()); }
                catch (Exception ex) { done.SetException(ex); }
            })
            { IsBackground = true, Name = "shell-link" };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return done.Task;
        }

        private static Task OnStaThread(Action work) =>
            OnStaThread<bool>(() => { work(); return true; });
    }
}
