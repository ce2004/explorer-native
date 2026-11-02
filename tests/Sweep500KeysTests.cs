using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of keys, speech and search.
    /// </summary>
    internal static class Sweep500KeysTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, keys, speech and search:");
            WinKeyMaskIsNotAPress();
            await Task.CompletedTask;
        }

        /// <summary>
        /// After the hook takes Win+E it presses its unassigned mask key so the
        /// Start menu stays shut. That key reaches the focused shortcut box and
        /// must not be answered with "That key cannot be saved".
        /// </summary>
        private static void WinKeyMaskIsNotAPress()
        {
            var winE = new Shortcut(HotkeyManager.MOD_WIN, (uint)Keys.E);
            using var box = new ShortcutBox("Play", Shortcut.None);
            string said = "";
            box.Captured += s => said = s;
            box.Accept(winE);
            var press = typeof(ShortcutBox).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var msg = new Message();
            var handled = (bool)press.Invoke(box, new object[] { msg, (Keys)WinKeyHook.MaskKeyCode })!;
            Check("the hook's mask key is swallowed by a shortcut box", handled);
            Equal("the mask key after Win+E leaves Win+E in the box", winE.ToString(), box.Value.ToString());
            Equal("and the last thing said is still that Win+E was taken", "Play shortcut is now " + winE.Spoken, said);
        }
    }
}
