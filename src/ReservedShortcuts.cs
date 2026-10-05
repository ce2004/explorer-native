using System.Collections.Generic;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Key combinations a global shortcut may never take.
    ///
    /// Every shortcut in the Audio list is registered with RegisterHotKey, which
    /// is exclusive and machine-wide: whoever registers a combination owns it and
    /// no other program ever sees it again. That is the point for Ctrl+Alt+P, and
    /// a disaster for Ctrl+C. Setting "play or pause" to Ctrl+C would stop copying
    /// working in every application on this computer — including in this one,
    /// whose own Copy would never receive the key again — and nothing about the
    /// registration would fail or warn, because from Windows' point of view
    /// nothing went wrong.
    ///
    /// So the refusal has to be here rather than in the dialog: settings.json is a
    /// file people are invited to edit, and <c>Settings.Validated</c> deliberately
    /// leaves shortcut text alone. Both ways in consult this list — the box that
    /// captures the keys, and the registration that runs at startup.
    ///
    /// Two kinds of entry, for two different reasons:
    ///
    /// The window's own keys, because taking one globally means this application
    /// stops being able to do the thing the key is for. These have to stay in step
    /// with the menu, and a test reads <c>MainForm.cs</c> to check that they have.
    ///
    /// And a handful that belong to nobody in particular and everybody in general.
    /// Ctrl+Z is not ours and never appears in our menu, which is exactly why it
    /// would be so quiet a catastrophe: undo would simply stop existing, in every
    /// editor on the machine, with nothing to connect it to a file manager's audio
    /// preferences.
    /// </summary>
    public static class ReservedShortcuts
    {
        private sealed record Reserved(Keys Combination, string Owner);

        /// <summary>
        /// The explicit list. <see cref="All"/> is this plus every function key.
        /// </summary>
        private static readonly Reserved[] Explicit =
        {
            // ---- the window's own, matching MainForm's menu ----
            new(Keys.Control | Keys.C, "Copy"),
            new(Keys.Control | Keys.X, "Cut"),
            new(Keys.Control | Keys.V, "Paste"),
            new(Keys.Alt | Keys.C, "Copy path"),
            new(Keys.Alt | Keys.L, "Copy Drive link"),
            new(Keys.Alt | Keys.Shift | Keys.L, "Copy public Drive link"),
            new(Keys.Control | Keys.A, "Select all"),
            new(Keys.Control | Keys.P, "Preferences"),
            new(Keys.Alt | Keys.Up, "Up one level"),
            new(Keys.Alt | Keys.D, "Drives"),
            new(Keys.Control | Keys.L, "Go to path"),
            new(Keys.Alt | Keys.Home, "Home"),
            new(Keys.F5, "Refresh"),
            new(Keys.Control | Keys.R, "Restart the application"),
            new(Keys.Control | Keys.Shift | Keys.S, "Calculate folder size"),
            new(Keys.Control | Keys.Shift | Keys.A, "Announce the current folder"),
            new(Keys.F7, "New file"),
            new(Keys.F8, "New folder"),
            new(Keys.F2, "Rename"),
            new(Keys.Delete, "Delete"),
            new(Keys.Shift | Keys.Delete, "Delete permanently"),
            new(Keys.Alt | Keys.Enter, "Properties"),
            new(Keys.Control | Keys.Shift | Keys.N, "New folder"),
            new(Keys.Control | Keys.Tab, "Switch tab"),
            new(Keys.Control | Keys.Shift | Keys.Tab, "Switch tab"),
            new(Keys.Shift | Keys.F10, "The Windows context menu"),

            // ---- choosing what is selected ----
            //
            // These carry a modifier, so nothing else refuses them, and losing one
            // means a selection can no longer be built by keyboard at all.
            new(Keys.Space, "Selecting the item you are on"),
            new(Keys.Control | Keys.Space, "Adding one item to the selection"),
            new(Keys.Shift | Keys.Up, "Extending the selection"),
            new(Keys.Shift | Keys.Down, "Extending the selection"),
            new(Keys.Shift | Keys.Home, "Extending the selection"),
            new(Keys.Shift | Keys.End, "Extending the selection"),
            new(Keys.Shift | Keys.PageUp, "Extending the selection"),
            new(Keys.Shift | Keys.PageDown, "Extending the selection"),

            // Ctrl with an arrow — moving the cursor without moving the
            // selection — is deliberately *not* here, and the reason is a real
            // configuration rather than a principle. Somebody was already using
            // Ctrl+Up and Ctrl+Down for the volume, and reserving them would have
            // taken away two shortcuts they had chosen on purpose in order to
            // protect a convenience that the plain arrows already provide.
            //
            // The line this list draws is "would losing it leave no way to do the
            // thing". Extending a selection and toggling one item have no other
            // keyboard route, so they stay. Moving without selecting has one.

            // ---- everybody's, and the quietest to lose ----
            new(Keys.Control | Keys.Z, "Undo, in every application"),
            new(Keys.Control | Keys.Y, "Redo, in every application"),
            new(Keys.Control | Keys.S, "Save, in every application"),
            new(Keys.Control | Keys.F, "Find, in every application"),
            new(Keys.Control | Keys.N, "New, in every application"),
            new(Keys.Control | Keys.O, "Open, in every application"),
            new(Keys.Control | Keys.W, "Close, in every application"),
            new(Keys.Control | Keys.T, "New tab, in every application"),
            new(Keys.Control | Keys.Shift | Keys.Z, "Redo, in every application"),
            new(Keys.Alt | Keys.F4, "Closing a window"),
            new(Keys.Alt | Keys.Tab, "Switching between windows"),
            new(Keys.Alt | Keys.Escape, "Switching between windows"),
            new(Keys.Control | Keys.Escape, "The Start menu"),
            new(Keys.Control | Keys.Shift | Keys.Escape, "Task Manager"),
        };

        /// <summary>
        /// Everything spoken for: the list above, plus every function key on its
        /// own.
        ///
        /// F2, F5, F7 and F8 are already up there by name, because this window
        /// uses them and the menu says so. The rest are here as a group, and the
        /// reason is that a function key is the one kind of key that is *only*
        /// ever a command. Nothing types F6, so nothing gives it back the way a
        /// letter comes back the moment you stop holding Control — a bare F3
        /// handed to Windows is Find Next gone from every program on the machine
        /// until this application is closed, and there is nothing on screen
        /// anywhere to connect the two.
        ///
        /// Generated rather than written out so the group cannot be half-listed.
        /// The four named above keep their own wording: "F5 is Refresh" says
        /// something "F5 is a function key" does not.
        ///
        /// This includes F13 to F24, which <see cref="Shortcut.IsSelfContained"/>
        /// will still say Windows is willing to hand over with no modifier at all.
        /// Both are right about different questions — that one is "will Windows
        /// take it", this one is "should we ask" — and where they disagree, this
        /// one wins, because it is the one that runs first.
        /// </summary>
        private static readonly Reserved[] All = BuildAll();

        private static Reserved[] BuildAll()
        {
            var all = new List<Reserved>(Explicit);

            for (var key = Keys.F1; key <= Keys.F24; key++)
            {
                bool alreadyNamed = false;
                foreach (var reserved in Explicit)
                    if (reserved.Combination == key) { alreadyNamed = true; break; }

                if (!alreadyNamed)
                    all.Add(new Reserved(key, "a function key every application uses"));
            }

            return all.ToArray();
        }

        /// <summary>
        /// What already owns this combination, or null when it is free.
        ///
        /// The sentence is the whole value of the answer — "Ctrl+C is already used
        /// by Copy" sends somebody to pick different keys, where a bare refusal
        /// sends them to wonder whether the box is broken.
        /// </summary>
        public static string? OwnerOf(Shortcut candidate)
        {
            if (!candidate.IsAssigned) return null;

            // Nothing reserved uses the Windows key, and Windows itself already
            // owns most of what it is part of. A combination carrying it cannot
            // collide with anything in the list, and saying it did would be wrong.
            if ((candidate.Modifiers & HotkeyManager.MOD_WIN) != 0) return null;

            var keys = AsKeys(candidate);
            foreach (var reserved in All)
                if (reserved.Combination == keys) return reserved.Owner;

            return null;
        }

        /// <summary>Every combination that is spoken for, for the tests.</summary>
        public static IEnumerable<Keys> Combinations()
        {
            foreach (var reserved in All) yield return reserved.Combination;
        }

        /// <summary>
        /// The same combination in WinForms' terms.
        ///
        /// Deliberately drops the Windows key rather than trying to represent it:
        /// Keys has no bit for it, and the caller above has already established
        /// that a combination carrying one cannot match.
        /// </summary>
        private static Keys AsKeys(Shortcut shortcut)
        {
            var keys = (Keys)shortcut.Key;
            if ((shortcut.Modifiers & HotkeyManager.MOD_CONTROL) != 0) keys |= Keys.Control;
            if ((shortcut.Modifiers & HotkeyManager.MOD_ALT) != 0) keys |= Keys.Alt;
            if ((shortcut.Modifiers & HotkeyManager.MOD_SHIFT) != 0) keys |= Keys.Shift;
            return keys;
        }
    }
}
