using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Shows the genuine Windows shell context menu (Open With, Properties,
    /// Send to, plus whatever extensions are installed) for a selection.
    ///
    /// This is a real native HMENU, so a screen reader reads it exactly as it
    /// reads the menu in File Explorer. Because this process is ARM64, only
    /// ARM64-capable shell extensions load into it — which is precisely the
    /// mismatch that broke the x64 file manager, avoided here by being native.
    /// </summary>
    public static class ShellContextMenu
    {
        private const int CMF_NORMAL = 0x00000000;
        private const int CMF_EXPLORE = 0x00000004;
        private const int CMF_EXTENDEDVERBS = 0x00000100;

        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_LEFTALIGN = 0x0000;

        private const uint MIN_ID = 1;
        private const uint MAX_ID = 0x7FFF;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHParseDisplayName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszName, IntPtr pbc,
            out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll", PreserveSig = false)]
        private static extern void SHBindToParent(
            IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint flags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr pv);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct CMINVOKECOMMANDINFOEX
        {
            public int cbSize;
            public int fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
            [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
            public int nShow;
            public int dwHotKey;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
            public IntPtr lpVerbW;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
            public int ptInvokeX;
            public int ptInvokeY;
        }

        [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellFolder
        {
            void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
                out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
            void EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
            void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
            void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
            void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
            void GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
                ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
            void GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
            void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
                uint uFlags, out IntPtr ppidlOut);
        }

        [ComImport, Guid("000214e4-0000-0000-c000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
            [PreserveSig] int GetCommandString(IntPtr idcmd, uint uflags, IntPtr reserved,
                [MarshalAs(UnmanagedType.LPArray)] byte[] commandstring, int cch);
        }

        [ComImport, Guid("000214f4-0000-0000-c000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu2
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
            [PreserveSig] int GetCommandString(IntPtr idcmd, uint uflags, IntPtr reserved,
                [MarshalAs(UnmanagedType.LPArray)] byte[] commandstring, int cch);
            [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        }

        /// <summary>
        /// Forwards the menu messages that owner-drawn shell extension items
        /// need, otherwise submenus come up blank.
        /// </summary>
        private sealed class MenuMessageWindow : NativeWindow, IDisposable
        {
            private readonly IContextMenu2? _cm2;

            public MenuMessageWindow(IContextMenu2? cm2, IntPtr parent)
            {
                _cm2 = cm2;
                CreateHandle(new CreateParams { Parent = parent });
            }

            protected override void WndProc(ref Message m)
            {
                const int WM_INITMENUPOPUP = 0x0117;
                const int WM_DRAWITEM = 0x002B;
                const int WM_MEASUREITEM = 0x002C;
                const int WM_MENUCHAR = 0x0120;

                if (_cm2 != null &&
                    (m.Msg == WM_INITMENUPOPUP || m.Msg == WM_DRAWITEM ||
                     m.Msg == WM_MEASUREITEM || m.Msg == WM_MENUCHAR))
                {
                    try
                    {
                        if (_cm2.HandleMenuMsg((uint)m.Msg, m.WParam, m.LParam) == 0)
                        {
                            m.Result = IntPtr.Zero;
                            return;
                        }
                    }
                    catch { }
                }

                base.WndProc(ref m);
            }

            public void Dispose()
            {
                try { DestroyHandle(); } catch { }
            }
        }

        private static string ParentOf(string path)
        {
            try { return Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('\\', '/')) ?? ""; }
            catch { return path; }
        }

        /// <summary>What happened when the Windows menu was asked for.</summary>
        public enum MenuOutcome
        {
            /// <summary>It appeared. Whether anything was chosen is not this method's business.</summary>
            Shown,

            /// <summary>The shell would not produce one here at all.</summary>
            Unavailable,

            /// <summary>It was going to take too long, so it was abandoned. See <see cref="BuildBudgetMilliseconds"/>.</summary>
            TooSlow,

            /// <summary>
            /// The items are in more than one folder, which one shell menu cannot
            /// act on. See <see cref="Show"/>.
            /// </summary>
            MixedFolders,
        }

        /// <summary>
        /// How long this may spend turning a selection into something the shell
        /// will build a menu for, before giving up.
        ///
        /// The work is two shell calls per selected item — SHParseDisplayName and
        /// SHBindToParent — and they are not cheap. Measured on this machine:
        /// **8 milliseconds each**. Three thousand selected files is therefore
        /// twenty-four seconds of the window not responding, ten thousand is over
        /// a minute, and a folder of two hundred thousand is not worth writing
        /// down. There was no limit at all, so right-clicking after Ctrl+A did
        /// exactly that.
        ///
        /// A time budget rather than a count, because the per-item cost is a
        /// property of the machine and of whatever shell extensions are installed
        /// — a number of items that is instant here is a freeze somewhere else,
        /// and the reverse. This bounds the freeze instead of guessing at it.
        ///
        /// And it abandons the whole menu rather than building one for the first
        /// N items. A menu that silently applies to part of a selection is the
        /// worse bug by a distance: Delete on "the first two hundred of your
        /// twenty thousand" is not something to offer.
        /// </summary>
        public const int BuildBudgetMilliseconds = 2000;

        /// <summary>
        /// Shows the shell menu for the given paths at a screen position.
        /// All paths must live in the same parent folder (the shell requires it).
        /// </summary>
        /// <param name="budgetMilliseconds">
        /// How long to spend before giving up, defaulting to
        /// <see cref="BuildBudgetMilliseconds"/>. A parameter only so a test can
        /// prove the refusal happens: with the real budget, whether it trips
        /// depends on how fast the machine's shell is, and a test that sometimes
        /// builds the menu instead is a test that sometimes puts a context menu
        /// on screen and waits for somebody to dismiss it.
        /// </param>
        public static MenuOutcome Show(
            IWin32Window owner, IReadOnlyList<string> paths, int screenX, int screenY,
            int budgetMilliseconds = BuildBudgetMilliseconds)
        {
            if (paths == null || paths.Count == 0) return MenuOutcome.Unavailable;

            // One menu is bound to one folder: every item is looked up as a child
            // of the first one's parent. Search results span folders, and a
            // selection of notes.txt in folder A and todo.txt in folder B had
            // Delete act on A's todo.txt — a file nobody had selected — if one
            // existed.
            var parent = ParentOf(paths[0]);
            for (int i = 1; i < paths.Count; i++)
                if (!string.Equals(ParentOf(paths[i]), parent, StringComparison.OrdinalIgnoreCase))
                    return MenuOutcome.MixedFolders;

            // Child PIDLs from SHBindToParent point INTO their full PIDL and must
            // never be freed themselves, nor used after the full PIDL is freed.
            // So the full PIDLs are the things we own and release, at the very end.
            var fullPidls = new List<IntPtr>();
            var childPidls = new List<IntPtr>();
            IntPtr parentFolderPtr = IntPtr.Zero;
            IntPtr contextMenuPtr = IntPtr.Zero;
            IntPtr hMenu = IntPtr.Zero;
            MenuMessageWindow? msgWindow = null;

            // Kept so the wrappers can be released deterministically. Left to the
            // finaliser, a shell extension's COM object stays alive for however
            // long the next collection takes, and its DLL stays loaded in this
            // process with it — one badly behaved extension then hangs around
            // long after the menu it was for has gone.
            object? parentFolderRcw = null;
            object? contextMenuRcw = null;

            try
            {
                // Bind to the parent folder using the first item, then collect
                // the child PIDLs of every selected item.
                var clock = System.Diagnostics.Stopwatch.StartNew();

                SHParseDisplayName(paths[0], IntPtr.Zero, out IntPtr firstPidl, 0, out _);
                if (firstPidl == IntPtr.Zero) return MenuOutcome.Unavailable;
                fullPidls.Add(firstPidl);

                var shellFolderGuid = typeof(IShellFolder).GUID;
                SHBindToParent(firstPidl, ref shellFolderGuid, out parentFolderPtr, out IntPtr firstChild);
                if (parentFolderPtr == IntPtr.Zero || firstChild == IntPtr.Zero) return MenuOutcome.Unavailable;

                var parentFolder = (IShellFolder)Marshal.GetObjectForIUnknown(parentFolderPtr);
                parentFolderRcw = parentFolder;
                childPidls.Add(firstChild);

                for (int i = 1; i < paths.Count; i++)
                {
                    // Checked every item, because the budget is about the total
                    // and one slow extension can make a single item expensive.
                    // The finally below frees everything taken so far.
                    if (clock.ElapsedMilliseconds > budgetMilliseconds)
                        return MenuOutcome.TooSlow;

                    // Every item or none. Skipping one that would not resolve — a
                    // file renamed since the list loaded, a share that blinked —
                    // built a menu for the rest, and a Delete from it acted on
                    // forty-nine of fifty with nothing said: the worse bug the
                    // budget note above already warns about.
                    try
                    {
                        SHParseDisplayName(paths[i], IntPtr.Zero, out IntPtr full, 0, out _);
                        if (full == IntPtr.Zero) return MenuOutcome.Unavailable;
                        fullPidls.Add(full);

                        SHBindToParent(full, ref shellFolderGuid, out IntPtr otherParent, out IntPtr child);
                        if (otherParent != IntPtr.Zero) Marshal.Release(otherParent);
                        if (child == IntPtr.Zero) return MenuOutcome.Unavailable;
                        childPidls.Add(child);
                    }
                    catch
                    {
                        return MenuOutcome.Unavailable;
                    }
                }

                var contextMenuGuid = typeof(IContextMenu).GUID;
                parentFolder.GetUIObjectOf(owner.Handle, (uint)childPidls.Count, childPidls.ToArray(),
                    ref contextMenuGuid, IntPtr.Zero, out contextMenuPtr);
                if (contextMenuPtr == IntPtr.Zero) return MenuOutcome.Unavailable;

                var contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPtr);
                contextMenuRcw = contextMenu;
                var contextMenu2 = contextMenu as IContextMenu2;

                hMenu = CreatePopupMenu();
                int hr = contextMenu.QueryContextMenu(hMenu, 0, MIN_ID, MAX_ID, CMF_NORMAL | CMF_EXPLORE | CMF_EXTENDEDVERBS);
                if (hr < 0) return MenuOutcome.Unavailable;

                msgWindow = new MenuMessageWindow(contextMenu2, owner.Handle);

                uint selected = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_LEFTALIGN,
                    screenX, screenY, msgWindow.Handle, IntPtr.Zero);

                if (selected == 0) return MenuOutcome.Shown; // dismissed without choosing — not a failure

                // ANSI verb only: setting CMIC_MASK_UNICODE means the shell reads
                // the W fields too, and getting that struct layout subtly wrong
                // is a far worse failure than losing nothing at all here.
                var invoke = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    lpVerb = (IntPtr)(selected - MIN_ID),
                    hwnd = owner.Handle,
                    nShow = 1, // SW_SHOWNORMAL
                    fMask = 0,
                };
                contextMenu.InvokeCommand(ref invoke);
                return MenuOutcome.Shown;
            }
            catch
            {
                return MenuOutcome.Unavailable;
            }
            finally
            {
                msgWindow?.Dispose();
                if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);

                // The wrappers first: each took its own reference when it was
                // created, and those have to go before the references we are
                // holding directly.
                if (contextMenuRcw != null) { try { Marshal.ReleaseComObject(contextMenuRcw); } catch { } }
                if (parentFolderRcw != null) { try { Marshal.ReleaseComObject(parentFolderRcw); } catch { } }

                if (contextMenuPtr != IntPtr.Zero) Marshal.Release(contextMenuPtr);
                if (parentFolderPtr != IntPtr.Zero) Marshal.Release(parentFolderPtr);
                // Only the full PIDLs are ours; the child PIDLs live inside them.
                foreach (var p in fullPidls) if (p != IntPtr.Zero) CoTaskMemFree(p);
            }
        }

        /// <summary>
        /// Opens the standard Windows properties sheet for one item, on a thread
        /// of its own in a single-threaded apartment — which is what the shell
        /// requires of anything invoking a verb, because the verb goes through
        /// COM to whichever shell extension handles it. From the thread pool, a
        /// multi-threaded apartment, some extensions refused, which was heard as
        /// "Could not open properties". SEE_MASK_NOASYNC keeps the thread until
        /// the shell has handed the work on, so it does not end under it.
        /// </summary>
        public static System.Threading.Tasks.Task<bool> ShowPropertiesAsync(IntPtr owner, string path)
        {
            var done = new System.Threading.Tasks.TaskCompletionSource<bool>(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var info = new SHELLEXECUTEINFO
                    {
                        cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                        lpVerb = "properties",
                        lpFile = path,
                        nShow = 1,
                        fMask = 0x0000000C | 0x00000100, // SEE_MASK_INVOKEIDLIST | SEE_MASK_NOASYNC
                        hwnd = owner,
                    };
                    done.TrySetResult(ShellExecuteEx(ref info));
                }
                catch (Exception ex) { done.TrySetException(ex); }
            })
            {
                IsBackground = true,
                Name = "ExplorerNative properties",
            };
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            return done.Task;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public int fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);
    }
}
