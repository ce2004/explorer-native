using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>What the Google Drive account button offers, from the live drive.</summary>
    public enum DriveAccountState
    {
        /// <summary>No saved sign-in, or Drive switched off: the button connects.</summary>
        NotConnected,

        /// <summary>Mounted and working: the button disconnects.</summary>
        Connected,

        /// <summary>Signed in, but Google wants the sign-in again: the button reconnects.</summary>
        NeedsSignIn,
    }

    /// <summary>
    /// Preferences (Ctrl+P).
    ///
    /// A category list plus a panel, not a TabControl. WinForms re-fires
    /// accessibility events on the tab control every time the selected tab
    /// changes, so a screen reader announces "tab control" on every arrow press
    /// on top of the tab's own name. A ListBox announces only the category and
    /// its position, which is also the shape NVDA's own settings dialog uses.
    /// </summary>
    public sealed class SettingsForm : Form
    {
        private readonly Settings _working;
        private readonly List<Action> _applies = new();

        /// <summary>
        /// The live Google Drive's state, for the account button. Set by the
        /// tray, which owns the drive; null (and so Connect) in tests.
        /// </summary>
        internal static Func<DriveAccountState>? AccountState;

        /// <summary>Takes the live drive off its letter, keeping the sign-in. Set by the tray.</summary>
        internal static Action? DisconnectDrive;

        /// <summary>Checked before OK closes the dialog; false keeps it open.</summary>
        private readonly List<Func<System.Threading.Tasks.Task<bool>>> _beforeOkChecks = new();

        /// <summary>
        /// Every page's check, in order; false (or a check that throws, which is
        /// said in a message) keeps the dialog open.
        /// </summary>
        private async System.Threading.Tasks.Task<bool> RunBeforeOk()
        {
            foreach (var check in _beforeOkChecks)
            {
                bool ok;
                try { ok = await check(); }
                catch (Exception ex)
                {
                    if (!IsDisposed)
                        MessageBox.Show(this, "Preferences could not be saved: " + ex.Message, "Preferences",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                if (!ok || IsDisposed) return false;
            }
            return true;
        }

        /// <summary>True while Google is being asked; one check at a time, and no closing.</summary>
        private bool _checkingGoogle;

        /// <summary>Switches to a category page by name.</summary>
        private Action<string>? _showCategory;
        private readonly List<Control> _panels = new();
        private Action? _buildAllPages;

        /// <summary>
        /// Builds every page now. The window builds a page only when it is first
        /// shown; the tests use this to look at every control at once.
        /// </summary>
        internal void BuildAllPages() => _buildAllPages?.Invoke();

        /// <summary>
        /// Creates the window of a hidden page and of everything on it now,
        /// rather than when the page is first shown.
        /// </summary>
        private static void CreateWindows(Control root)
        {
            var pending = new Stack<Control>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var control = pending.Pop();
                _ = control.Handle;
                foreach (Control child in control.Controls) pending.Push(child);
            }
        }

        /// <summary>
        /// Double-buffers a page and every panel in it, so a page shown for the
        /// first time or scrolled is drawn once rather than piece by piece.
        /// DoubleBuffered is protected, hence the reflection.
        /// </summary>
        private static void BufferDrawing(Control root)
        {
            var property = typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (property == null) return;

            var pending = new Stack<Control>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var control = pending.Pop();
                if (control is Panel) property.SetValue(control, true);
                foreach (Control child in control.Controls) pending.Push(child);
            }
        }

        public Settings Result => _working;

        public SettingsForm(Settings current, string? startCategory)
        {
            _working = current.Clone();

            Text = "Explorer Native Preferences";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MinimizeBox = false;
            MaximizeBox = false;
            Width = 720;
            Height = 620;
            DoubleBuffered = true;
            SuspendLayout();

            // Each page is built the first time it is shown, not all of them up
            // front. Building every page made the dialog take half a second to
            // open and stall on the first visit to each category. A page never
            // opened has had nothing changed on it, so it has nothing to apply on
            // OK and nothing to check: its applies and checks are registered by
            // its builder, which simply never ran.
            var categories = new (string Name, Func<Control> Build)[]
            {
                ("Speech", BuildSpeechTab),
                ("Display", BuildDisplayTab),
                ("Behaviour", BuildBehaviourTab),
                ("Folder sizes", BuildFolderSizeTab),
                ("Hotkey", BuildHotkeyTab),
                ("Audio", BuildAudioTab),
                ("Google Drive", BuildGoogleDriveTab),
                ("Web app", BuildWebAppTab),
                ("Integration", BuildIntegrationTab),
            };

            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0) };
            Control?[] pages = new Control?[categories.Length];
            Control? showing = null;

            Control PageAt(int index)
            {
                if (pages[index] is { } built) return built;

                host.SuspendLayout();
                try
                {
                    var page = categories[index].Build();
                    page.Dock = DockStyle.Fill;
                    page.Visible = false;
                    page.AccessibleName = categories[index].Name + " settings";
                    BufferDrawing(page);
                    pages[index] = page;
                    _panels.Add(page);
                    host.Controls.Add(page);
                    return page;
                }
                finally { host.ResumeLayout(false); }
            }

            _buildAllPages = () => { for (int i = 0; i < pages.Length; i++) PageAt(i); };

            // The rest of the pages are built while nobody is waiting: one per
            // idle moment once the window is up, windows and all, so the first
            // visit to a page only has to show it. A page's controls get their
            // windows when it is first made visible otherwise, which for the
            // Audio page was over a tenth of a second on the arrow press.
            // A timer rather than Application.Idle: Windows only delivers a timer
            // message when nothing else is waiting, which is the same thing, and
            // it also works under a plain message pump.
            var prebuild = new System.Windows.Forms.Timer { Interval = 1 };
            prebuild.Tick += (_, _) =>
            {
                int next = Array.FindIndex(pages, p => p == null);
                if (next < 0 || IsDisposed) { prebuild.Stop(); return; }
                try { CreateWindows(PageAt(next)); }
                catch { prebuild.Stop(); }
            };
            Shown += (_, _) => prebuild.Start();
            FormClosed += (_, _) => prebuild.Dispose();

            var list = new ListBox
            {
                Dock = DockStyle.Left,
                Width = 180,
                IntegralHeight = false,
            };
            list.AccessibleName = "Category";
            foreach (var (name, _) in categories) list.Items.Add(name);

            list.SelectedIndexChanged += (_, _) =>
            {
                if (list.SelectedIndex < 0) return;
                var next = PageAt(list.SelectedIndex);
                if (ReferenceEquals(next, showing)) return;

                host.SuspendLayout();
                try
                {
                    next.Visible = true;
                    if (showing != null) showing.Visible = false;
                    showing = next;
                }
                finally { host.ResumeLayout(true); }
            };
            // Opened from the Audio menu, the dialog should already be on the
            // audio page rather than six categories above it.
            int start = startCategory == null
                ? 0
                : Array.FindIndex(categories, c =>
                    string.Equals(c.Name, startCategory, StringComparison.OrdinalIgnoreCase));
            list.SelectedIndex = start >= 0 ? start : 0;

            _showCategory = name =>
            {
                int at = Array.FindIndex(categories, c => c.Name == name);
                if (at >= 0) list.SelectedIndex = at;
            };

            // Nothing closes the dialog while Google is being asked about the
            // client ID and secret: closing mid-check saved (or refused) them
            // after the person had cancelled, on a form already disposed.
            FormClosing += (_, e) =>
            {
                if (!_checkingGoogle) return;
                e.Cancel = true;
                Say("Still checking with Google");
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 48,
                Padding = new Padding(8),
            };

            var ok = new Button { Text = "&OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "&Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var reset = new Button { Text = "&Reset to defaults", AutoSize = true };
            reset.Click += (_, _) =>
            {
                // Spelled out, because a reset quietly hands folders back to File
                // Explorer and removes the context-menu entry — the two settings
                // here that change things outside this application, and the two
                // nobody expects "reset preferences" to touch.
                var warning = "Reset every preference to its default?";
                if (ShellRegistration.IsDefaultFileExplorer())
                    warning += "\n\nThis will also stop Explorer Native opening folders; " +
                               "Windows Explorer will take them back.";
                if (ShellRegistration.IsContextMenuRegistered())
                    warning += "\n\nThe \"Open in Explorer Native\" menu entry will be removed.";

                if (MessageBox.Show(this, warning, "Reset",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    DialogResult = DialogResult.Retry; // caller treats Retry as "reset"
                    Close();
                }
            };

            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(reset);

            // Not a plain DialogResult button: the Google client ID and secret
            // are checked with Google first, and a refusal keeps the dialog open.
            ok.DialogResult = DialogResult.None;
            bool closing = false;
            ok.Click += async (_, _) =>
            {
                if (closing) return;
                closing = true;
                try
                {
                    if (!await RunBeforeOk()) return;
                    foreach (var apply in _applies) apply();
                    DialogResult = DialogResult.OK;
                    Close();
                }
                catch (Exception ex)
                {
                    // An async void handler: anything escaping it would end the application.
                    if (!IsDisposed)
                        MessageBox.Show(this, "Preferences could not be saved: " + ex.Message, "Preferences",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally { closing = false; }
            };

            AcceptButton = ok;
            CancelButton = cancel;

            // Tab order puts OK, Cancel and Reset ahead of the category list, so
            // shift-tabbing back from the list reaches them immediately rather
            // than walking the whole settings panel first.
            ok.TabIndex = 0;
            cancel.TabIndex = 1;
            reset.TabIndex = 2;
            buttons.TabIndex = 0;
            list.TabIndex = 1;
            host.TabIndex = 2;

            Controls.Add(host);
            Controls.Add(list);
            Controls.Add(buttons);

            // Focus still starts on the category list so arrowing works at once.
            Shown += (_, _) => list.Focus();
        }

        // ---------- Tab builders ----------

        private FlowLayoutPanel BuildSpeechTab()
        {
            var panel = NewPage();

            // The master switch. It was honoured everywhere in the app but had no
            // control anywhere in this dialog, so the only way to silence the app
            // was to hand-edit settings.json.
            AddCheck(panel, "Enable spoken announcements", _working.SpeakEnabled, v => _working.SpeakEnabled = v);
            AddCheck(panel, "Announce operations (copied, cut, pasted, deleted)", _working.SpeakOperations, v => _working.SpeakOperations = v);
            AddInfo(panel,
                "Position in the list (\"69 of 2170\") is announced by NVDA itself, at the end of the row. " +
                "This app deliberately does not announce it too — doing so only ever produced an echo. " +
                "Control it in NVDA: Preferences, Settings, Object Presentation, \"Report object position information\".");
            AddCheck(panel, "Interrupt current speech for new announcements", _working.InterruptSpeech, v => _working.InterruptSpeech = v);
            AddCheck(panel, "Announce errors", _working.SpeakErrors, v => _working.SpeakErrors = v);
            AddCheck(panel, "Announce progress during long operations", _working.SpeakProgress, v => _working.SpeakProgress = v);
            AddCheck(panel, "Say \"Opening\" and the name when launching a file",
                _working.SpeakOnOpen, v => _working.SpeakOnOpen = v);

            // The whole catalogue, one message at a time, on the page that owns
            // the subject. It had a category of its own called "Notifications",
            // which was the wrong name for it twice over: nothing pops up any
            // more, and the only question it ever asked was whether a message is
            // spoken.
            int total = Notifications.All.Count;
            int on = Notifications.All.Count(n => n.Default != NotificationChannel.None);

            AddButton(panel, "Choose what is s&poken...", () =>
            {
                using var dialog = new SpeechForm(_working);
                dialog.ShowDialog(this);
            });

            AddInfo(panel,
                $"Every message this application can produce — {total} of them across " +
                $"{Notifications.Categories.Count} categories — with a choice for each of whether it " +
                "is spoken. Everything is written to the status bar either way; that costs nothing " +
                "and interrupts nobody, so it is not a setting.");

            AddInfo(panel,
                $"{on} of the {total} are spoken by default. The rest are silent on purpose: anything " +
                "you can already tell by other means is off, because three things already speak when " +
                "you move and a fourth is an echo. Anything you asked for directly, and anything that " +
                "went wrong, is on.");

            AddInfo(panel,
                "Each one has a Preview button, and it says the message whatever the setting is — " +
                "including when the setting is off, because a preview that refuses to demonstrate the " +
                "thing you are configuring is a broken button. Half of these only happen when " +
                "something has gone wrong, and a setting you cannot check without breaking something " +
                "is a setting nobody trusts.");

            // Progress deliberately has no second box in that dialog, though it
            // obviously belongs to the same subject. It had one, bound to the
            // same setting as the box on this page — and because every page's
            // changes are applied in the order the pages were built, that one
            // ran last and wrote its own untouched value straight over this one.
            // Unticking "Announce progress during long operations" did nothing
            // at all, with no way to tell from either page why.
            //
            // One setting, one control. It is spoken every ten percent rather
            // than on every update; anything finer is unusable noise.

            AddInfo(panel, "NVDA controller client: " + Speech.LoadDiagnostic);

            return panel;
        }

        private FlowLayoutPanel BuildDisplayTab()
        {
            var panel = NewPage();

            AddCheck(panel, "Show hidden files", _working.ShowHiddenFiles, v => _working.ShowHiddenFiles = v);
            AddCheck(panel, "Show system files", _working.ShowSystemFiles, v => _working.ShowSystemFiles = v);
            AddCheck(panel, "Show file extensions", _working.ShowExtensions, v => _working.ShowExtensions = v);
            AddCheck(panel, "List folders before files", _working.FoldersFirst, v => _working.FoldersFirst = v);

            AddEnum<SortColumn>(panel, "Sort by", _working.SortBy, v => _working.SortBy = v);
            AddCheck(panel, "Sort ascending", _working.SortAscending, v => _working.SortAscending = v);

            AddText(panel, "Window title", _working.WindowTitle, v => _working.WindowTitle = v);
            AddInfo(panel, "What the window is called in its title bar, the taskbar and the tray. " +
                           "Applied as soon as you press OK. Left empty it goes back to \"" +
                           Settings.DefaultWindowTitle + "\".");

            AddEnum<SizeUnitStyle>(panel, "Size units", _working.SizeUnits, v => _working.SizeUnits = v);
            AddNumber(panel, "Font size", _working.FontSize, 7, 24, v => _working.FontSize = v);

            AddCheck(panel, "Verbose modification info (exact date and time)",
                _working.VerboseModifiedInfo, v => _working.VerboseModifiedInfo = v);
            AddInfo(panel, "With verbose off, the Modified column reads as \"today\", \"yesterday\", " +
                           "\"3 days ago\", \"2 weeks ago\", and anything past ten years as \"a long time ago\".");

            return panel;
        }

        private FlowLayoutPanel BuildBehaviourTab()
        {
            var panel = NewPage();

            AddCheck(panel, "Arrow keys wrap around at the ends of the list", _working.WrapArrowNavigation, v => _working.WrapArrowNavigation = v);

            AddNumber(panel, "Multi letter navigation threshold (milliseconds)",
                _working.TypeAheadMilliseconds, 100, 3000,
                v => _working.TypeAheadMilliseconds = v);
            AddInfo(panel,
                "Typing letters jumps to the first matching name. Letters typed within this threshold " +
                "build one word — \"s\", \"y\", \"s\" becomes \"sys\" — and a longer gap starts again. " +
                "Raise it if letters typed as a word are being treated separately; lower it if a single " +
                "letter feels slow to act on its own.");
            AddCheck(panel, "Announce tab when switching between the two tabs", _working.SpeakTabSwitch, v => _working.SpeakTabSwitch = v);
            AddCheck(panel, "Confirm before deleting", _working.ConfirmDelete, v => _working.ConfirmDelete = v);
            AddEnum<DeleteMode>(panel, "Delete sends files to", _working.Delete, v => _working.Delete = v);
            AddEnum<PasteConflictPolicy>(panel, "When a pasted name already exists", _working.PasteConflict, v => _working.PasteConflict = v);
            AddCheck(panel, "Remember last folder between sessions", _working.RememberLastFolder, v => _working.RememberLastFolder = v);
            AddCheck(panel, "Closing the window hides it to the tray", _working.CloseToTray, v => _working.CloseToTray = v);
            AddCheck(panel, "Select the first item when entering a folder",
                _working.SelectFirstItemOnNavigate, v => _working.SelectFirstItemOnNavigate = v);

            return panel;
        }

        private FlowLayoutPanel BuildFolderSizeTab()
        {
            var panel = NewPage();

            AddEnum<FolderSizeMode>(panel, "Calculate folder sizes", _working.FolderSizes, v => _working.FolderSizes = v);
            AddNumber(panel, "Give up after (seconds)", _working.FolderSizeTimeoutSeconds, 1, 600, v => _working.FolderSizeTimeoutSeconds = v);

            AddInfo(panel,
                "Sizes are measured on a background thread, so the list stays responsive, and a " +
                "measured folder is remembered until it changes. " +
                "On demand means press Ctrl+Shift+S on a folder to measure just that one.");

            return panel;
        }

        private FlowLayoutPanel BuildHotkeyTab()
        {
            var panel = NewPage();

            AddCheck(panel, "Enable the global hotkey", _working.GlobalHotkeyEnabled, v => _working.GlobalHotkeyEnabled = v);

            bool ctrl = (_working.HotkeyModifiers & HotkeyManager.MOD_CONTROL) != 0;
            bool alt = (_working.HotkeyModifiers & HotkeyManager.MOD_ALT) != 0;
            bool shift = (_working.HotkeyModifiers & HotkeyManager.MOD_SHIFT) != 0;
            bool win = (_working.HotkeyModifiers & HotkeyManager.MOD_WIN) != 0;

            bool nCtrl = ctrl, nAlt = alt, nShift = shift, nWin = win;
            AddCheck(panel, "Control", ctrl, v => nCtrl = v);
            AddCheck(panel, "Alt", alt, v => nAlt = v);
            AddCheck(panel, "Shift", shift, v => nShift = v);
            AddCheck(panel, "Windows key", win, v => nWin = v);

            var keyBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            keyBox.AccessibleName = "Hotkey letter or key";

            // The list and the key each entry stands for, side by side, so the
            // answer is read back from the index rather than parsed out of text.
            var keys = new List<uint>();

            // No key at all is a choice of its own. Shown as E, an unset shortcut
            // either turned itself on at the next OK or — guarded — could never
            // be set to E on purpose.
            keyBox.Items.Add("(none)");
            keys.Add(0);
            for (char c = 'A'; c <= 'Z'; c++) { keyBox.Items.Add(c.ToString()); keys.Add((uint)(Keys)c); }
            for (int f = 1; f <= 12; f++) { keyBox.Items.Add("F" + f); keys.Add((uint)(Keys.F1 + f - 1)); }

            // A key the list does not offer — a digit, set in the file by hand —
            // is added rather than replaced. Falling back to the first entry
            // meant pressing OK on any page of this dialog changed the window's
            // shortcut to A without anybody touching it.
            int currentAt = keys.IndexOf(_working.HotkeyKey);
            if (currentAt < 0 && _working.HotkeyKey != 0)
            {
                keyBox.Items.Add(new Shortcut(0, _working.HotkeyKey).ToString());
                keys.Add(_working.HotkeyKey);
                currentAt = keys.Count - 1;
            }
            keyBox.SelectedIndex = currentAt >= 0 ? currentAt : 0;

            AddLabelled(panel, "Key", keyBox);

            _applies.Add(() =>
            {
                uint mods = 0;
                if (nCtrl) mods |= HotkeyManager.MOD_CONTROL;
                if (nAlt) mods |= HotkeyManager.MOD_ALT;
                if (nShift) mods |= HotkeyManager.MOD_SHIFT;
                if (nWin) mods |= HotkeyManager.MOD_WIN;

                uint key = keyBox.SelectedIndex >= 0 ? keys[keyBox.SelectedIndex] : 0;

                // No key: nothing to check, and nothing is registered.
                if (key == 0)
                {
                    _working.HotkeyModifiers = mods;
                    _working.HotkeyKey = 0;
                    return;
                }

                // Untouched: nothing to check and nothing to say about it.
                if (mods == _working.HotkeyModifiers && key == _working.HotkeyKey) return;

                // This page builds a combination out of four tick boxes and a
                // list, so it can produce Ctrl+C as easily as Ctrl+Alt+E — and
                // this one is registered globally like the audio shortcuts are.
                // Taking Ctrl+C machine-wide would stop copying working
                // everywhere, so the old setting is kept and the reason said.
                var candidate = new Shortcut(mods, key);
                if (ReservedShortcuts.OwnerOf(candidate) is { } owner)
                {
                    Say($"{candidate.Spoken} is already used by {owner}. The window shortcut was left as it was.");
                    return;
                }

                // With every modifier unticked this is a bare letter, and a bare
                // letter registered globally is that letter gone from every
                // program on the machine.
                if (!candidate.CanRegister)
                {
                    Say($"{candidate.Spoken} needs Control, Alt, Shift or the Windows key with it. " +
                        "The window shortcut was left as it was.");
                    return;
                }

                // An audio shortcut already on these keys would lose them to the
                // window, silently, and the "not set" dialog would follow.
                foreach (var action in AudioActions.All)
                {
                    var audio = Shortcut.Parse(action.Get(_working));
                    if (audio.IsAssigned && audio.ToString() == candidate.ToString())
                    {
                        Say($"{candidate.Spoken} is already the shortcut for {action.Label}. " +
                            "The window shortcut was left as it was.");
                        return;
                    }
                }

                _working.HotkeyModifiers = mods;
                _working.HotkeyKey = key;
            });

            AddInfo(panel, "A hotkey already claimed by another application will fail to register; " +
                           "the tray icon still opens the window either way.");

            return panel;
        }

        /// <summary>
        /// Google Drive on a drive letter: whether to mount it, which letter to
        /// try, and how much downloaded audio to keep in memory. The summary that
        /// used to sit here described the audio player, which is the page below.
        /// </summary>
        private FlowLayoutPanel BuildGoogleDriveTab()
        {
            var panel = NewPage();

            // No switch: Drive is on whenever you are signed in. Connect and OK
            // (with an accepted client ID and secret) turn it on, Sign out turns
            // it off. The box is never shown; it carries that state to OK.
            var driveBox = new CheckBox { Checked = _working.GoogleDriveEnabled };
            _applies.Add(() => _working.GoogleDriveEnabled = driveBox.Checked);

            AddDriveLetter(panel);

            AddGoogleAccountControls(panel, driveBox);

            // This said the opposite until the scope changed under it: "nothing
            // this application does can change or delete anything in your Drive".
            // Copy, move and delete were added, the scope became full Drive
            // access, and the sentence stayed — telling somebody their files were
            // safe on a page whose Delete key now reaches the account.
            AddInfo(panel,
                "This can change your Drive. Copying a file to the drive uploads it, moving one " +
                "moves it in Drive, and deleting sends it to the Google Drive trash — recoverable " +
                "from drive.google.com, but gone from here. Reading and playing change nothing.");

            AddInfo(panel,
                "Folders fill in as they are opened, costing one listing each — about a second the " +
                "first time, nothing afterwards. This works for any program, not just this one, so " +
                "the drive behaves normally in File Explorer and in a command prompt too.");

            return panel;
        }

        /// <summary>
        /// Signing in and out, as two buttons rather than a paragraph about a
        /// JSON file.
        ///
        /// The page used to say "sign-in needs a client_secret.json from your
        /// own Google Cloud project" and name a folder — which is an accurate
        /// instruction and a hopeless one for anybody who was handed this
        /// application. So the file is a button now: Choose credentials file
        /// takes the JSON exactly as the Cloud console downloads it, checks it
        /// and copies it into place. Nothing is compiled in; the published
        /// build ships with no Google client of its own.
        ///
        /// Signing in is deliberately *not* done from here. It opens a browser
        /// and waits on consent, which is a thing to do from the main window
        /// with the drive being mounted around it — so the button closes
        /// Preferences and hands the job to the tray, which is what the tick box
        /// at the top of this page does too. Signing out is local and instant,
        /// so it happens here.
        /// </summary>
        private void AddGoogleAccountControls(FlowLayoutPanel panel, CheckBox driveBox)
        {
            bool signedIn = GoogleDrive.HasSavedSignIn;

            // The client ID and secret from a Desktop app OAuth client in the
            // person's own Google Cloud project. Nothing is compiled in. Checked
            // with Google when OK is pressed, and saved encrypted only if Google
            // accepts them.
            AddInfo(panel,
                "Google client ID and secret: from an OAuth client of type Desktop app in your own " +
                "Google Cloud project. They are checked with Google when you press OK.");
            var current = GoogleAuth.Credentials(GoogleDrive.CredentialsDirectory);
            var idBox = new TextBox { Text = current?.ClientId ?? "", Width = 360, AccessibleName = "Google client ID" };
            var secretBox = new TextBox
            {
                Text = current?.ClientSecret ?? "",
                Width = 360,
                UseSystemPasswordChar = true,
                AccessibleName = "Google client secret",
            };
            AddLabelled(panel, "Google client &ID", idBox);
            AddLabelled(panel, "Google client s&ecret", secretBox);

            AddInfo(panel, signedIn
                ? "Signed in to Google. The sign-in is remembered, so this will not be asked for again."
                : "Not signed in to Google yet. Connecting opens a browser once and remembers it "
                  + "afterwards.");

            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 4, 0, 10),
                WrapContents = false,
            };

            var connect = new Button
            {
                AutoSize = true,
                Margin = new Padding(0, 0, 10, 0),
            };

            // The button says what pressing it will do, from the live drive's
            // state: Disconnect while it is working, Reconnect when Google wants
            // the sign-in again, Connect otherwise.
            var state = AccountState?.Invoke() ?? DriveAccountState.NotConnected;
            void Show(DriveAccountState now)
            {
                state = now;
                (connect.Text, connect.AccessibleDescription) = now switch
                {
                    DriveAccountState.Connected =>
                        ("&Disconnect Google Drive", "Takes Google Drive off its drive letter. You stay signed in."),
                    DriveAccountState.NeedsSignIn =>
                        ("&Reconnect Google Drive", "Google needs you to sign in again."),
                    _ => ("&Connect Google Drive", "Puts Google Drive on its drive letter."),
                };
            }
            Show(state);

            connect.Click += async (_, _) =>
            {
              // An async void handler: nothing may escape it, or the application ends.
              try
              {
                if (state == DriveAccountState.Connected)
                {
                    // Off its letter and switched off, signed in still, so
                    // connecting again later needs no browser. Preferences stays
                    // open; the drive says "Google Drive disconnected" itself.
                    DisconnectDrive?.Invoke();
                    driveBox.Checked = false;
                    _working.GoogleDriveEnabled = false;
                    Show(DriveAccountState.NotConnected);
                    return;
                }

                if (!await RunBeforeOk()) return;
                if (GoogleAuth.Credentials(GoogleDrive.CredentialsDirectory) == null)
                {
                    MessageBox.Show(this,
                        "Enter your Google client ID and client secret first.",
                        "Connect Google Drive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    idBox.Focus();
                    return;
                }

                // Closing with OK means the caller saves and says "Preferences
                // saved", so this has to do what OK does before it leaves.
                // Without it every other change on every other page — the font
                // size, a column, a shortcut — was quietly discarded by a button
                // that says it connects a drive.
                driveBox.Checked = true;
                foreach (var apply in _applies) apply();

                DialogResult = DialogResult.OK;
                Close();
              }
              catch (Exception ex)
              {
                if (!IsDisposed)
                    MessageBox.Show(this, "Google Drive could not be connected: " + ex.Message, "Google Drive",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
              }
            };

            var signOut = new Button
            {
                Text = "Sign &out of Google",
                AutoSize = true,
                Enabled = signedIn,
            };
            signOut.AccessibleDescription =
                "Forgets the saved sign-in on this computer. It does not delete anything in Drive, "
                + "and it does not withdraw the application's access at Google.";
            signOut.Click += (_, _) =>
            {
                // Asked, because it is not obvious from the button that the
                // drive goes away with the sign-in.
                if (MessageBox.Show(this,
                        "Forget the saved Google sign-in on this computer?\r\n\r\n"
                        + "Nothing in your Drive is changed or deleted. The drive letter goes away "
                        + "until you connect again.",
                        "Sign out of Google",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;

                bool gone = GoogleDrive.ForgetSignIn();

                // The box, not the field: OK applies from the box, so setting
                // the field alone was undone by pressing OK — signing out and
                // then saving left Drive switched on, and the next mount opened
                // the browser for a new sign-in rather than leaving the drive
                // away as the message promises.
                driveBox.Checked = false;
                _working.GoogleDriveEnabled = false;
                signOut.Enabled = false;
                Show(DriveAccountState.NotConnected);

                MessageBox.Show(this,
                    gone
                        ? "Signed out. The drive letter is removed the next time the application starts, "
                          + "or immediately if Google Drive was switched off just now."
                        : "There was no saved sign-in to remove.",
                    "Sign out of Google", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            // Runs before OK (and Connect) close the dialog. Unchanged or empty
            // fields pass straight through; anything new is checked with Google
            // and the dialog stays open, on the field, if Google refuses it.
            string savedId = idBox.Text, savedSecret = secretBox.Text;
            _beforeOkChecks.Add(async () =>
            {
                var id = idBox.Text.Trim();
                var secret = secretBox.Text.Trim();
                if (id == savedId && secret == savedSecret) return true;
                if (id.Length == 0 && secret.Length == 0) return true;

                if (id.Length == 0 || secret.Length == 0)
                {
                    MessageBox.Show(this, "Enter both the Google client ID and the client secret.",
                        "Google Drive", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _showCategory?.Invoke("Google Drive");
                    (id.Length == 0 ? idBox : secretBox).Focus();
                    return false;
                }

                // One check at a time: OK and Connect both come through here.
                if (_checkingGoogle) return false;
                _checkingGoogle = true;
                Say("Checking with Google");
                UseWaitCursor = true;
                string? problem;
                try { problem = await GoogleAuth.CheckClientAsync(id, secret, System.Threading.CancellationToken.None); }
                finally
                {
                    _checkingGoogle = false;
                    UseWaitCursor = false;
                }
                if (IsDisposed) return false;

                bool changed = false;
                problem ??= GoogleAuth.SaveClient(id, secret, GoogleDrive.CredentialsDirectory, out changed);
                if (problem != null)
                {
                    MessageBox.Show(this, problem, "Google Drive", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    // On the page with the field, or Focus does nothing.
                    _showCategory?.Invoke("Google Drive");
                    idBox.Focus();
                    return false;
                }

                // A saved sign-in was granted to the previous client, and Google
                // refuses it for this one.
                if (changed && GoogleDrive.HasSavedSignIn)
                {
                    GoogleDrive.ForgetSignIn();
                    signOut.Enabled = false;
                    Show(DriveAccountState.NotConnected);
                }

                savedId = id;
                savedSecret = secret;

                // Valid: switch Drive on, so closing Preferences signs in.
                driveBox.Checked = true;
                Say("Google accepted them. Signing in");
                return true;
            });

            row.Controls.Add(connect);
            row.Controls.Add(signOut);
            panel.Controls.Add(row);

            AddInfo(panel,
                "To withdraw this application's access to your account entirely, rather than only on "
                + "this computer, use myaccount.google.com/permissions. Signing out here removes the "
                + "saved token from this machine and nothing else.");
        }

        private FlowLayoutPanel BuildAudioTab()
        {
            var panel = NewPage();

            AddCheck(panel, "Enable the audio player", _working.AudioPlayerEnabled,
                v => _working.AudioPlayerEnabled = v);

            AddCheck(panel, "Play audio files in Explorer Native instead of the associated application",
                _working.PlayAudioInApp, v => _working.PlayAudioInApp = v);
            AddInfo(panel,
                "With this on, Enter on an audio file plays it here and Shift+Enter still opens it in " +
                "whatever normally owns it. Anything Windows turns out not to be able to decode is " +
                "handed to that application automatically, so the extension list below can be generous.");

            AddButton(panel, "Choose &formats to play...", () =>
            {
                using var dialog = new FormatsForm(_working.AudioExtensions);
                if (dialog.ShowDialog(this) == DialogResult.OK) _working.AudioExtensions = dialog.Extensions;
            });

            AddCheck(panel, "Repeat the track when it ends", _working.AudioRepeatTrack,
                v => _working.AudioRepeatTrack = v);

            AudioVolumeSection(panel);
            AudioSpeechSection(panel);

            AddButton(panel, "Configure keyboard &shortcuts...", () =>
            {
                using var dialog = new ShortcutsForm(_working);
                dialog.ShowDialog(this);
            });
            AddInfo(panel,
                "Eighteen actions, each with its own global shortcut. They open in their own window " +
                "rather than filling this page, and a combination already used by another action is " +
                "refused there rather than quietly taking it over.");

            AddInfo(panel,
                "Enter always starts a track from the beginning. There is no carrying on from where " +
                "you left off: a player that sometimes begins two minutes in, because of something " +
                "you did an hour ago, is one you cannot predict.");

            AddNumber(panel, "Jump back when resuming (seconds)", _working.AudioRewindOnResumeSeconds, 0, 30,
                v => _working.AudioRewindOnResumeSeconds = v);
            AddInfo(panel,
                "Unpausing rewinds this far first, so picking a track back up does not drop you into " +
                "the middle of a word. Zero carries straight on.");

            AddNumber(panel, "Skip by (seconds)", _working.AudioSeekSeconds, 1, 300,
                v => _working.AudioSeekSeconds = v);
            AddNumber(panel, "Long skip by (seconds)", _working.AudioLongSeekSeconds, 1, 3600,
                v => _working.AudioLongSeekSeconds = v);
            AddInfo(panel, "The long skip has its own pair of shortcuts, for moving through " +
                           "something an hour long without holding a key down.");

            AddReadAhead(panel);
            AddNumber(panel, "Give the memory back after paused (minutes)",
                _working.AudioReleaseAfterMinutes, 0, 1440, v => _working.AudioReleaseAfterMinutes = v);
            AddInfo(panel,
                "Music on a network drive is slow to start and slow to skip through, and it is the " +
                "round trips rather than the bytes. Reading ahead from the selected file makes " +
                "pressing Enter quicker; zero turns it off. A track on a network drive is also " +
                "downloaded into memory as it plays, which is what makes skipping instant — anything " +
                "already fetched costs nothing to jump into, and anything not yet reached is read " +
                "straight from the share. Only one track is held at a time and starting another " +
                "releases the last. Zero minutes keeps a paused track in memory for ever.");

            AudioLimiterSection(panel);

            AddInfo(panel, "Playing through: " + AudioReport.OutputText);

            // Only when it has something to say. "Media engine: not started" sat
            // here for a while after the media engine stopped existing — a label
            // naming a thing that is gone, reporting a state that means nothing,
            // directly above the line that actually answers the question.
            var problem = AudioReport.ProblemText;
            if (problem.Length > 0 && problem != "not started" && problem != "ready")
                AddInfo(panel, "Last problem starting a track: " + problem);

            return panel;
        }

        /// <summary>
        /// What the limiter and the equaliser are doing, and where to set them.
        ///
        /// **No controls.** Both of them live entirely on the Audio menu now,
        /// and this page only says so.
        ///
        /// The switch used to be here and the four things that shape it were on
        /// the menu, which meant one effect with two homes and a rule connecting
        /// them — the menu entry appeared only while the tick box was ticked, so
        /// finding the controls meant knowing that a preferences page you were
        /// not on governed whether a menu entry existed. Every one of those
        /// controls has to be judged by ear against music that is playing, and
        /// the switch is the one you most want to flick back and forth while
        /// listening, which makes it the *last* thing that should be behind
        /// Ctrl+P, a category list and an OK button.
        ///
        /// What is left here is the meter, which is a report rather than a
        /// control, and a sentence saying where the controls went.
        /// </summary>
        private void AudioLimiterSection(FlowLayoutPanel panel)
        {
            AddInfo(panel,
                "The limiter lifts whatever is quiet at that moment up towards full scale, so the " +
                "things living thirty or forty decibels under the music come up where they can be " +
                "heard — room tone, reverb tails, the breath before a line, the second guitar in " +
                "the far corner of the mix. Loud passages are left exactly where the volume put " +
                "them, clipping included: protecting the peaks would flatten the difference it " +
                "exists to open up.");

            AddInfo(panel,
                "Normally it does nothing at or below 100 percent volume. Above that the lift it is " +
                "allowed grows with the square of the volume — at 200 percent it is a leveller, and " +
                "at 800 it is a different recording. \"Same lift at any volume\" is what makes it " +
                "work below 100 as well, and that is the setting for listening into a recording " +
                "rather than for making one loud.");

            AddInfo(panel,
                "All of it is on the Audio menu, under \"Limiter…\" — the switch, the attack, the " +
                "release, the ceiling and that last option. The twenty-band equaliser is beside it " +
                "under \"Equaliser…\". Neither is here, because every control in both of them " +
                "changes the sound as you arrow onto it, and none of them means anything except " +
                "against music that is actually playing.");

            // The meter, which existed from the first version of the renderer
            // and was never once asked a question. Peak and clipping were both
            // measured on every buffer, by a player whose whole reason for
            // owning the render loop was to be able to answer "is it clipping" —
            // and there was nowhere at all that the answer came out.
            AddInfo(panel, "Right now: " + AudioReport.LevelText);
        }

        private void AudioVolumeSection(FlowLayoutPanel panel)
        {

            AddNumber(panel, "Volume (percent)", _working.AudioVolumePercent, 0, AudioPlayer.LoudestPercent,
                v => _working.AudioVolumePercent = v);

            AddNumber(panel, "Fade in (milliseconds)", _working.AudioFadeInMilliseconds, 0, 10000,
                v => _working.AudioFadeInMilliseconds = v);
            AddNumber(panel, "Fade out (milliseconds)", _working.AudioFadeOutMilliseconds, 0, 10000,
                v => _working.AudioFadeOutMilliseconds = v);
            AddInfo(panel,
                "Fading in applies when a track starts and when it resumes; fading out happens before " +
                "a pause actually takes effect. Zero for neither. Stop never fades — stop means stop.");

            AddInfo(panel,
                "This is the player's own volume and nothing else's. It does not touch the Windows " +
                "volume, and the level is remembered between sessions because there is no window to " +
                "set it in on the next run.");
        }

        private void AudioSpeechSection(FlowLayoutPanel panel)
        {

            AddInfo(panel,
                "What the player says out loud, and when. Everything here is off: the player's " +
                "shortcuts all do something you can already hear — the volume moves, the track " +
                "jumps, the music stops — so saying it as well is narration over the thing being " +
                "narrated, and over the screen reader reading whatever you were on. " +
                "These switches control speech only; the status bar shows what happened either way.");

            AddCheck(panel, "Say the name when a track starts", _working.AudioAnnounceStart,
                v => _working.AudioAnnounceStart = v);
            AddCheck(panel, "Say when a track finishes", _working.AudioAnnounceTrackEnd,
                v => _working.AudioAnnounceTrackEnd = v);
            AddCheck(panel, "Say play, pause, stop, mute, repeat and speed", _working.AudioAnnounceTransport,
                v => _working.AudioAnnounceTransport = v);
            AddCheck(panel, "Say the volume when it changes", _working.AudioAnnounceVolume,
                v => _working.AudioAnnounceVolume = v);
            AddCheck(panel, "Say the position when skipping", _working.AudioAnnounceSeek,
                v => _working.AudioAnnounceSeek = v);

            AddInfo(panel,
                "Two things always speak, whatever these are set to. \"Say what is playing\" exists " +
                "to be asked, and a command that could not run — pressing pause with nothing loaded " +
                "— says so, or the key would simply seem broken.");

            AddCheck(panel, "Name tracks by their Title tag rather than the file name",
                _working.AudioAnnounceUsesTitleTag, v => _working.AudioAnnounceUsesTitleTag = v);
            AddInfo(panel,
                "Reads the tag in the background, so a file with no tags or one on a slow share costs " +
                "nothing — the file name is used until a title turns up, and stays if none does.");
        }

        /// <summary>
        /// Turns the web app's Tailscale Serve handler on or off right now, off the
        /// UI thread. Set by the tray; null in tests.
        /// </summary>
        internal static Func<bool, TailscaleWeb.EnableResult>? WebAppApply;

        /// <summary>The port the web app's server is listening on now. Set by the tray; null in tests.</summary>
        internal static Func<int>? CurrentWebPort;

        /// <summary>
        /// Preferences, Web app: Explorer Connect in a browser, over Tailscale only.
        ///
        /// It finds Tailscale for itself and says, in one sentence, what is missing
        /// and which button fixes it: Install Tailscale, Sign in to Tailscale,
        /// Enable HTTPS in Tailscale. The status is a read-only box, so it is a tab
        /// stop NVDA reads, and it is checked again every few seconds while
        /// Preferences is open. Turning the box on or off acts at once; Cancel puts
        /// it back. The port is checked when OK is pressed.
        /// </summary>
        private FlowLayoutPanel BuildWebAppTab()
        {
            var panel = NewPage();
            AddInfo(panel,
                "Use this PC's files and music from Safari or any browser on your phone, tablet or another " +
                "computer, with nothing to install but Tailscale. It only works over your Tailscale network: " +
                "nothing is reachable from the internet.");

            var status = new TextBox
            {
                ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control,
                Width = 540, Height = 64, Text = "Checking Tailscale…", AccessibleName = "Web app status",
                Margin = new Padding(3, 8, 3, 4),
            };
            panel.Controls.Add(status);

            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 4) };
            Button Make(string text)
            {
                var b = new Button { Text = text, AutoSize = true, Visible = false, Margin = new Padding(3, 4, 8, 4) };
                b.AccessibleName = text.Replace("&", "");
                row.Controls.Add(b);
                return b;
            }
            var install = Make("&Install Tailscale");
            var signIn = Make("&Sign in to Tailscale");
            var https = Make("Enable &HTTPS in Tailscale");
            panel.Controls.Add(row);

            bool original = _working.WebAppEnabled;
            var enable = new CheckBox
            {
                Text = "&Turn on the web app", Checked = _working.WebAppEnabled, AutoSize = true, Enabled = false,
                Margin = new Padding(3, 8, 3, 4),
            };
            enable.AccessibleName = "Turn on the web app";
            panel.Controls.Add(enable);
            _applies.Add(() => _working.WebAppEnabled = enable.Checked);

            // The link and Copy link are there whenever the link is known, on or
            // off, so the button is always found in the same place. Off, it says
            // why there is nothing to copy rather than being greyed out, which
            // would take it out of the tab order and out of NVDA's reach.
            var linkBox = new TextBox
            {
                ReadOnly = true, Width = 420, AccessibleName = "Web app link", Visible = false,
                Margin = new Padding(3, 4, 3, 2),
            };
            panel.Controls.Add(linkBox);
            var linkRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 4), Visible = false };
            var copy = new Button { Text = "&Copy link", AutoSize = true, AccessibleName = "Copy link" };
            var open = new Button { Text = "&Open in browser", AutoSize = true, AccessibleName = "Open in browser" };
            linkRow.Controls.Add(copy);
            linkRow.Controls.Add(open);
            panel.Controls.Add(linkRow);

            AddInfo(panel,
                "On your phone: install Tailscale and sign in with the same account as this PC. Open the link in " +
                "Safari. Tap Share, then Add to Home Screen. That's all; it opens like an app. Anybody else on your " +
                "Tailscale network is asked for this PC's pairing code once: " + PairingCodeText() + ".");

            // The port. Any free one in range works; the default is 47810.
            int startPort = _working.WebAppPort;
            var portBox = new TextBox
            {
                Text = startPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Width = 90, MaxLength = 5, AccessibleName = "Port",
                AccessibleDescription = $"The port the web app uses on this PC, {ConnectServer.LowestPort} to " +
                                        $"{ConnectServer.HighestPort}. Normally {ConnectServer.Port}.",
            };
            portBox.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            AddLabelled(panel, "&Port", portBox);
            AddInfo(panel, $"Normally {ConnectServer.Port}. Change it only if another program needs that port. " +
                           "It is checked when you press OK.");

            _beforeOkChecks.Add(() =>
            {
                var text = portBox.Text.Trim();
                int current = CurrentWebPort?.Invoke() ?? startPort;
                string? refusal = !int.TryParse(text, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var port)
                    ? $"Enter a port number from {ConnectServer.LowestPort} to {ConnectServer.HighestPort}."
                    : ConnectServer.CheckPort(port, current);
                if (refusal == null)
                {
                    _working.WebAppPort = port;
                    return System.Threading.Tasks.Task.FromResult(true);
                }
                MessageBox.Show(this, refusal + " Choose another port, or put back " + ConnectServer.Port + ".",
                    "Web app", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _showCategory?.Invoke("Web app");
                portBox.Focus();
                portBox.SelectAll();
                return System.Threading.Tasks.Task.FromResult(false);
            });

            string? httpsUrl = null, problem = null, link = null;
            bool checking = false, applying = false, isOn = false;
            DateTime lastTry = DateTime.MinValue;

            // Back on the window's thread, unless the window has gone meanwhile.
            void OnUi(Action action)
            {
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke(action); } catch (InvalidOperationException) { }
            }

            void Apply(bool on)
            {
                if (WebAppApply == null || applying) return;
                applying = true;
                lastTry = DateTime.UtcNow;
                status.Text = on ? "Turning the web app on…" : "Turning the web app off…";
                System.Threading.Tasks.Task.Run(() => WebAppApply(on)).ContinueWith(t => OnUi(() =>
                {
                    applying = false;
                    var r = t.IsCompletedSuccessfully
                        ? t.Result
                        : new TailscaleWeb.EnableResult(false,
                            "Tailscale did not answer: " + (t.Exception?.InnerException?.Message ?? "no reply") + ".", null);
                    httpsUrl = r.EnableHttpsUrl;
                    problem = r.Ok ? null : r.Problem;
                    if (on) Say(r.Ok ? "The web app is on" : (r.EnableHttpsUrl != null ? "HTTPS needs switching on in Tailscale" : "The web app could not be turned on"));
                    else Say("The web app is off");
                    Refresh();
                }));
            }

            void Show(TailscaleWeb.Status s, TailscaleWeb.ServeState serve)
            {
                install.Visible = s.State == TailscaleWeb.State.NotInstalled;
                signIn.Visible = s.State is TailscaleWeb.State.NotRunning or TailscaleWeb.State.NeedsLogin;
                enable.Enabled = s.State == TailscaleWeb.State.Running;

                string text;
                link = s.State == TailscaleWeb.State.Running ? TailscaleWeb.Link(s) : null;
                isOn = serve == TailscaleWeb.ServeState.Ours;
                switch (s.State)
                {
                    case TailscaleWeb.State.NotInstalled:
                        text = "Tailscale is not installed on this PC. Press Install Tailscale, then sign in when it opens.";
                        break;
                    case TailscaleWeb.State.NotRunning:
                        text = "Tailscale is installed but not running. Press Sign in to Tailscale.";
                        break;
                    case TailscaleWeb.State.NeedsLogin:
                        text = "Tailscale is installed but not signed in. Press Sign in to Tailscale.";
                        break;
                    default:
                        text = $"Tailscale is signed in as {s.Login ?? "you"}. This PC is {s.HostName ?? Environment.MachineName}.";
                        if (enable.Checked)
                        {
                            if (isOn) { text += " The web app is on."; problem = null; httpsUrl = null; }
                            else if (applying) text += " Turning the web app on…";
                            else if (problem != null) text += " " + problem;
                            else text += " The web app is not on yet.";

                            // Waiting for HTTPS to be switched on, or for Tailscale to come up: try again by itself.
                            if (!isOn && !applying && serve != TailscaleWeb.ServeState.TakenByOther &&
                                DateTime.UtcNow - lastTry > TimeSpan.FromSeconds(10))
                                Apply(true);
                        }
                        else if (isOn) text += " The web app is off, but its link is still served; it is turned off now.";
                        else text += " Turn on the web app to use it from your phone.";
                        break;
                }

                https.Visible = httpsUrl != null && enable.Checked && !isOn;
                linkBox.Visible = linkRow.Visible = link != null;
                if (link != null && linkBox.Text != link) linkBox.Text = link;
                copy.AccessibleDescription = isOn ? link : "Turn on the web app to get a link.";
                open.AccessibleDescription = isOn ? null : "Turn on the web app first.";
                if (status.Text != text) status.Text = text;
            }

            void Refresh()
            {
                if (checking || IsDisposed) return;
                checking = true;
                int port = CurrentWebPort?.Invoke() ?? startPort;
                System.Threading.Tasks.Task.Run(() =>
                {
                    var s = TailscaleWeb.Read();
                    var serve = s.State == TailscaleWeb.State.Running ? TailscaleWeb.ReadServe(port) : TailscaleWeb.ServeState.Off;
                    return (s, serve);
                }).ContinueWith(t =>
                {
                    if (IsDisposed || !IsHandleCreated) { checking = false; return; }
                    OnUi(() =>
                    {
                        checking = false;
                        if (t.IsCompletedSuccessfully) Show(t.Result.s, t.Result.serve);
                        else status.Text = "Tailscale could not be checked: " +
                                           (t.Exception?.InnerException?.Message ?? "no reply") + ".";
                    });
                });
            }

            install.Click += (_, _) => { TailscaleWeb.Install(); Say("Installing Tailscale"); };
            signIn.Click += (_, _) => { TailscaleWeb.SignIn(); Say("Opening the Tailscale sign-in"); };
            https.Click += (_, _) => { if (httpsUrl != null) TailscaleWeb.Open(httpsUrl); };
            copy.Click += (_, _) =>
            {
                if (!isOn || link == null)
                {
                    Speech.Speak("Turn on the web app to get a link", interrupt: true);
                    status.Text = "Turn on the web app to get a link.";
                    return;
                }
                try
                {
                    Clipboard.SetText(link);
                    Speech.Speak("Link copied", interrupt: true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "The link could not be copied: " + ex.Message, "Web app",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            open.Click += (_, _) =>
            {
                if (!isOn || link == null)
                {
                    Speech.Speak("Turn on the web app first", interrupt: true);
                    status.Text = "Turn on the web app first.";
                    return;
                }
                TailscaleWeb.Open(link);
            };
            enable.CheckedChanged += (_, _) => { problem = null; httpsUrl = null; Apply(enable.Checked); };

            // Only while Preferences is on screen: nothing runs tailscale.exe for a
            // dialog that was built and never shown, as the tests do.
            var timer = new System.Windows.Forms.Timer { Interval = 4000 };
            timer.Tick += (_, _) => { if (panel.Visible) Refresh(); };
            // The page is built when it is first shown, which is usually after the
            // window's Shown has already happened.
            if (IsHandleCreated && Visible) timer.Start();
            else Shown += (_, _) => { Refresh(); timer.Start(); };
            panel.VisibleChanged += (_, _) => { if (panel.Visible && IsHandleCreated) Refresh(); };
            FormClosed += (_, _) =>
            {
                timer.Dispose();
                // Cancel puts back what was there before the box was touched.
                if (DialogResult != DialogResult.OK && enable.Checked != original && WebAppApply != null)
                {
                    var apply = WebAppApply;
                    System.Threading.Tasks.Task.Run(() => { try { apply(original); } catch { } });
                }
            };

            return panel;
        }

        /// <summary>The pairing code as it is read out, in two groups of four.</summary>
        private string PairingCodeText()
        {
            var code = _working.ConnectCode ?? "";
            return code.Length == 8 ? code[..4] + " " + code[4..] : "shown here once Explorer Native has made one";
        }

        private FlowLayoutPanel BuildIntegrationTab()
        {
            var panel = NewPage();

            AddCheck(panel, "Start with Windows", _working.StartWithWindows, v => _working.StartWithWindows = v);
            AddCheck(panel, "Start minimised to the tray", _working.StartMinimised, v => _working.StartMinimised = v);

            AddCheck(panel, "Add \"Open in Explorer Native\" to the Windows menu for folders and drives",
                _working.RegisterContextMenu, v => _working.RegisterContextMenu = v);

            AddInfo(panel,
                "This writes only under your own user account, so it needs no administrator rights " +
                "and cannot affect other users. Unticking the box removes exactly what ticking it added.");

            // The takeover is the one setting in here that changes how the whole
            // desktop behaves, so it is confirmed at the moment it is ticked
            // rather than silently applied when the dialog is dismissed.
            var takeover = new CheckBox
            {
                Text = "Open every folder in Explorer Native instead of File Explorer",
                Checked = _working.SetAsDefaultFileExplorer,
                AutoSize = true,
                Margin = new Padding(3, 12, 3, 4),
            };
            takeover.AccessibleName = takeover.Text;
            takeover.CheckedChanged += (_, _) =>
            {
                if (!takeover.Checked) return;
                var answer = MessageBox.Show(this,
                    "Double-clicking a folder and opening a drive will open Explorer Native " +
                    "from now on.\n\n" +
                    "Windows+E is not affected — Windows handles that shortcut inside the shell " +
                    "itself, and taking it over breaks ordinary navigation elsewhere. Use this " +
                    "application's own hotkey instead (Preferences, Hotkey).\n\n" +
                    "A copy of the application is installed to\n" + AppInstall.Directory +
                    "\nand that is the copy Windows will open folders with, so rebuilding or moving " +
                    "the original does not break anything.\n\n" +
                    "The desktop, taskbar and Start menu still belong to Windows Explorer — those " +
                    "cannot be replaced safely and are left alone.\n\n" +
                    "Everything is written under your own user account only, and unticking this box " +
                    "puts it all back. Go ahead?",
                    "Open folders in Explorer Native",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (answer != DialogResult.Yes) takeover.Checked = false;
            };
            panel.Controls.Add(takeover);
            _applies.Add(() => _working.SetAsDefaultFileExplorer = takeover.Checked);

            AddInfo(panel,
                "If the app is ever unreachable while it holds the folder association, run it once from " +
                "a command prompt with --unregister-default to hand folders back to File Explorer, or " +
                "--unregister-all to remove every registry entry this app has made.");
            AddInfo(panel,
                "This app is portable: the shell entries record wherever the executable currently is, " +
                "and are rewritten automatically if you move the folder and run it again.");

            return panel;
        }

        // ---------- Control helpers ----------

        private static FlowLayoutPanel NewPage() => new()
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(12),
        };

        private CheckBox AddCheck(Control parent, string text, bool value, Action<bool> apply)
        {
            var box = new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
            box.AccessibleName = text;
            parent.Controls.Add(box);
            _applies.Add(() => apply(box.Checked));
            return box;
        }

        /// <summary>
        /// A number, as a list of the numbers that are allowed.
        ///
        /// Not a spin box. A spin box is an edit field with arrows on it, so it
        /// can be typed into — and what gets typed is not always a number, is not
        /// always in range, and on the way to being either it passes through
        /// states like empty and "-" that the rest of the application then has to
        /// survive. A drop-down list cannot hold a value that was never offered.
        ///
        /// It also reads better: a screen reader announces a combo box with its
        /// value and its position in the list, where a spin box announces an edit
        /// field and leaves you to discover the bounds by hitting them.
        /// </summary>
        private void AddNumber(Control parent, string label, int value, int min, int max, Action<int> apply)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
            combo.AccessibleName = label;

            var choices = SettingChoices.Between(min, max, value);
            int chosen = Math.Max(0, choices.IndexOf(Math.Clamp(value, min, max)));

            // Only the current value until the list is reached. Windows loads
            // every item of every list when the page appears, and the Audio page
            // alone has hundreds, which was most of the time it took to open. The
            // rest are added when the list gets the keyboard, before the screen
            // reader asks it anything, so it still reads "5 of 183".
            combo.Items.Add(choices[chosen].ToString());
            combo.SelectedIndex = 0;
            bool filled = false;

            void Fill()
            {
                if (filled) return;
                filled = true;
                combo.BeginUpdate();
                try
                {
                    var all = new object[choices.Count];
                    for (int i = 0; i < all.Length; i++) all[i] = choices[i].ToString();
                    combo.Items.Clear();
                    combo.Items.AddRange(all);
                    combo.SelectedIndex = chosen;
                }
                finally { combo.EndUpdate(); }
            }

            combo.Enter += (_, _) => Fill();
            combo.DropDown += (_, _) => Fill();
            combo.MouseDown += (_, _) => Fill();

            AddLabelled(parent, label, combo);
            _applies.Add(() => apply(filled ? choices[Math.Max(0, combo.SelectedIndex)] : choices[chosen]));
        }

        // The ladder itself is SettingChoices.Between, in a file of its own. It
        // lived here as a private helper, which meant the one part of this
        // dialog that is pure arithmetic — and the part that decides whether any
        // of its controls are usable at all — was the part with no test on it,
        // because this file needs a message loop and is not compiled into the
        // suite. It was wrong for a long time; see that file for how.

        /// <summary>
        /// A button that opens something else. Its own row, so a screen reader
        /// reads it as a button and not as part of whatever precedes it.
        /// </summary>
        private static void AddButton(Control parent, string text, Action click)
        {
            var button = new Button { Text = text, AutoSize = true, Margin = new Padding(3, 10, 3, 4) };
            button.AccessibleName = text.Replace("&", "");
            button.Click += (_, _) => click();
            parent.Controls.Add(button);
        }

        private void AddText(Control parent, string label, string value, Action<string> apply)
        {
            var box = new TextBox { Text = value, Width = 200 };
            box.AccessibleName = label;
            AddLabelled(parent, label, box);
            _applies.Add(() => apply(box.Text));
        }

        private void Say(string message)
        {
            if (!_working.SpeakEnabled) return;
            Speech.Speak(message, interrupt: true);
        }

        private void AddEnum<T>(Control parent, string label, T value, Action<T> apply) where T : struct, Enum
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
            combo.AccessibleName = label;
            foreach (var name in Enum.GetNames<T>()) combo.Items.Add(Humanise(name));
            combo.SelectedIndex = Array.IndexOf(Enum.GetNames<T>(), value.ToString());
            if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;

            AddLabelled(parent, label, combo);
            _applies.Add(() => apply(Enum.GetValues<T>()[combo.SelectedIndex]));
        }

        /// <summary>
        /// The drive letter for Google Drive, as a list of the letters nothing
        /// else is using. The one already chosen stays in the list, since the
        /// mounted drive itself is what is using it.
        /// </summary>
        private void AddDriveLetter(Control parent)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
            combo.AccessibleName = "Google Drive letter";

            var current = (_working.GoogleDriveLetter ?? "").Trim().TrimEnd(':').ToUpperInvariant();
            var used = new HashSet<char>();
            try
            {
                foreach (var d in System.IO.DriveInfo.GetDrives())
                    if (d.Name.Length > 0) used.Add(char.ToUpperInvariant(d.Name[0]));
            }
            catch { }

            var letters = new List<string>();
            for (char c = 'D'; c <= 'Z'; c++)
                if (!used.Contains(c) || current == c.ToString()) letters.Add(c.ToString());
            if (letters.Count == 0 && current.Length == 1) letters.Add(current);

            foreach (var l in letters) combo.Items.Add(l + ":");
            int at = letters.IndexOf(current);
            combo.SelectedIndex = at >= 0 ? at : (letters.Count > 0 ? 0 : -1);

            AddLabelled(parent, "Google Drive letter", combo);
            _applies.Add(() =>
            {
                if (combo.SelectedIndex >= 0) _working.GoogleDriveLetter = letters[combo.SelectedIndex];
            });
        }

        /// <summary>Read-ahead sizes offered, in kilobytes, up to one gigabyte.</summary>
        internal static readonly int[] ReadAheadKilobytes =
            { 0, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576 };

        internal static string ReadAheadText(int kilobytes) =>
            kilobytes == 0 ? "Off"
            : kilobytes >= 1048576 ? (kilobytes / 1048576) + " gigabyte"
            : (kilobytes / 1024) + " megabytes";

        private void AddReadAhead(Control parent)
        {
            const string label = "Read ahead from the selected file";
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
            combo.AccessibleName = label;
            foreach (var kb in ReadAheadKilobytes)
                combo.Items.Add(kb == 1024 ? "1 megabyte" : ReadAheadText(kb));

            // The nearest offered size to whatever was set before.
            int best = 0;
            for (int i = 0; i < ReadAheadKilobytes.Length; i++)
                if (Math.Abs(ReadAheadKilobytes[i] - _working.AudioPrefetchKilobytes) <
                    Math.Abs(ReadAheadKilobytes[best] - _working.AudioPrefetchKilobytes)) best = i;
            combo.SelectedIndex = best;

            AddLabelled(parent, label, combo);
            _applies.Add(() => _working.AudioPrefetchKilobytes = ReadAheadKilobytes[Math.Max(0, combo.SelectedIndex)]);
        }

        private static void AddLabelled(Control parent, string label, Control control)
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(3, 4, 3, 4),
            };
            var lbl = new Label { Text = label + ":", AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
            row.Controls.Add(lbl);
            row.Controls.Add(control);
            parent.Controls.Add(row);
        }

        private static Label AddInfo(Control parent, string text)
        {
            var label = new InfoLabel
            {
                Margin = new Padding(3, 12, 3, 4),
                ForeColor = SystemColors.GrayText,
            };
            label.Text = text;
            parent.Controls.Add(label);
            return label;
        }

        /// <summary>
        /// A wrapping paragraph of help text, measured once when its text or
        /// font changes. An AutoSize label with a MaximumSize is measured again
        /// on every layout pass, and a page with two dozen of them spent most of
        /// its time doing that each time it was shown.
        /// </summary>
        private sealed class InfoLabel : Label
        {
            private const int Width540 = 540;

            public InfoLabel() => AutoSize = false;

            protected override void OnTextChanged(EventArgs e)
            {
                base.OnTextChanged(e);
                Fit();
            }

            protected override void OnFontChanged(EventArgs e)
            {
                base.OnFontChanged(e);
                Fit();
            }

            private void Fit()
            {
                var measured = TextRenderer.MeasureText(Text ?? "", Font, new Size(Width540, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                Size = new Size(Width540, Math.Max(measured.Height, Font.Height));
            }
        }

        /// <summary>"AutoRename" -> "Auto rename", so the combo reads as words.</summary>
        private static string Humanise(string enumName)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < enumName.Length; i++)
            {
                if (i > 0 && char.IsUpper(enumName[i]) && !char.IsUpper(enumName[i - 1]))
                    sb.Append(' ').Append(char.ToLowerInvariant(enumName[i]));
                else sb.Append(enumName[i]);
            }
            return sb.ToString();
        }
    }
}






