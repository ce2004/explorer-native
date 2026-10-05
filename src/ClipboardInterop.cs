using System;
using System.Collections.Specialized;
using System.IO;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Real Windows clipboard file formats (CF_HDROP + Preferred DropEffect),
    /// so copy/cut/paste works between this app, File Explorer, and anything
    /// else that speaks the shell clipboard.
    /// </summary>
    public static class ClipboardInterop
    {
        private const uint DROPEFFECT_COPY = 1;
        private const uint DROPEFFECT_MOVE = 2;
        private const string PreferredDropEffect = "Preferred DropEffect";

        /// <summary>
        /// Puts a file selection on the clipboard. Returns false if the clipboard
        /// could not be taken.
        ///
        /// The result is reported rather than swallowed: five failed attempts used
        /// to end in silence, so the app announced "Copied 3 items" and then
        /// pasted nothing, with no indication anywhere that the copy never
        /// happened.
        /// </summary>
        public static bool SetFiles(string[] paths, bool cut)
        {
            var list = new StringCollection();
            list.AddRange(paths);

            var data = new DataObject();
            data.SetFileDropList(list);

            using var stream = new MemoryStream(BitConverter.GetBytes(cut ? DROPEFFECT_MOVE : DROPEFFECT_COPY));
            data.SetData(PreferredDropEffect, stream);

            // Retry: the clipboard is a shared, lockable resource and another
            // app holding it briefly is normal, not exceptional.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    // Rewound every time, because a failed attempt may already
                    // have read it. Setting the clipboard reads this stream from
                    // wherever the position happens to be, so a second attempt
                    // handed over four bytes the first time and *nothing* the
                    // second — and an empty Preferred DropEffect is not "no
                    // opinion", it is the absence of the flag that makes this a
                    // cut. The retry that rescued the copy turned it into one:
                    // Ctrl+X, Ctrl+V, and the file is still where it was, with
                    // "Cut 3 items" having been announced.
                    stream.Position = 0;

                    Clipboard.SetDataObject(data, copy: true);
                    return true;
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    System.Threading.Thread.Sleep(60);
                }
            }
            return false;
        }

        public static bool TryGetFiles(out string[] paths, out bool isCut)
        {
            paths = Array.Empty<string>();
            isCut = false;

            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return false;

                if (data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
                    return false;
                paths = files;

                if (data.GetDataPresent(PreferredDropEffect) &&
                    data.GetData(PreferredDropEffect) is MemoryStream ms)
                {
                    var bytes = ms.ToArray();
                    if (bytes.Length >= 4)
                        isCut = (BitConverter.ToUInt32(bytes, 0) & DROPEFFECT_MOVE) != 0;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Empties the clipboard, but only if it still holds the same cut that
        /// has now been carried out.
        ///
        /// The check matters: between the cut and the paste finishing, another
        /// application may have put something on the clipboard. Clearing blindly
        /// would throw away whatever they copied, which is a far more annoying
        /// bug than the one this is fixing.
        /// </summary>
        public static bool ClearIfStillOurs(string[] expected)
        {
            try
            {
                if (!TryGetFiles(out var current, out bool isCut)) return false;
                if (!isCut) return false;
                if (current.Length != expected.Length) return false;

                for (int i = 0; i < current.Length; i++)
                    if (!string.Equals(current[i], expected[i], StringComparison.OrdinalIgnoreCase))
                        return false;

                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try { Clipboard.Clear(); return true; }
                    catch (System.Runtime.InteropServices.ExternalException)
                    {
                        System.Threading.Thread.Sleep(60);
                    }
                }
            }
            catch { }
            return false;
        }

        public static bool SetText(string text)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (string.IsNullOrEmpty(text)) Clipboard.Clear();
                    else Clipboard.SetText(text);
                    return true;
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    System.Threading.Thread.Sleep(60);
                }
            }
            return false;
        }
    }
}
