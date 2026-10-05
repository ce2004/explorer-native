using System;
using System.IO;
using Microsoft.Win32;

namespace ExplorerNative
{
    /// <summary>
    /// Registers/deregisters this app with the Windows shell.
    ///
    /// Everything lives under HKCU\Software\Classes, so none of it needs
    /// administrator rights and none of it can damage another user's setup.
    /// Deregistering removes exactly what registering added and nothing else.
    /// </summary>
    public static class ShellRegistration
    {
        private const string VerbName = "OpenInExplorerNative";
        private const string VerbLabel = "Open in Explorer Native";

        // The name only. The label went with the feature — nothing writes this
        // verb any more, and the name is kept solely so an old one can be swept
        // off a machine that has it (see UnregisterSendToIPhone).
        private const string SendVerbName = "SendToIPhoneViaExplorerNative";

        private static readonly string[] ContextTargets =
        {
            @"Software\Classes\Directory\shell",
            @"Software\Classes\Directory\Background\shell",
            @"Software\Classes\Drive\shell",
        };

        /// <summary>Send to iPhone is offered on files as well as folders.</summary>
        private static readonly string[] SendTargets =
        {
            @"Software\Classes\*\shell",
            @"Software\Classes\Directory\shell",
        };

        private const string DirectoryDefaultCommand = @"Software\Classes\Directory\shell\OpenInExplorerNative\command";
        private const string StartupRun = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValue = "ExplorerNative";

        /// <summary>
        /// The path the shell entries name.
        ///
        /// The installed copy whenever there is one — never a build output. A
        /// registry entry pointing into bin\ stops working the moment the project
        /// is rebuilt, and every folder on the machine goes with it.
        /// </summary>
        public static string ExePath => AppInstall.PreferredRegistrationPath;

        /// <summary>
        /// Raised when a claim actually changes hands, with the notification id
        /// and the sentence to say.
        ///
        /// Reported from here rather than from the caller because this is the
        /// only place that knows whether anything moved. Reconcile rewrites the
        /// context-menu verb whenever the executable has moved, repairs a
        /// folder-open registration that already named us, and calls SetStartup
        /// on every single launch whether or not the Run value was already
        /// there — so a caller comparing its own settings would announce a
        /// takeover that has been in place for months, on every start.
        ///
        /// Nothing subscribes in a --register-default or --uninstall run, which
        /// has no tray to say anything with; the invocation is then a null check.
        /// </summary>
        public static event Action<string, string>? Changed;

        /// <summary>
        /// True when a registered command line launches this program.
        ///
        /// Matched on the executable's file name rather than the literal text
        /// "ExplorerNative". The app is portable, so the folder in the command
        /// changes whenever it is moved and a full-path comparison would report
        /// its own entries as somebody else's — which is how a disabled setting
        /// failed to clean up after a move, leaving a dead verb behind.
        /// </summary>
        private static bool PointsAtUs(string? command)
        {
            if (string.IsNullOrEmpty(command)) return false;
            var us = Path.GetFileName(ExePath);
            return !string.IsNullOrEmpty(us) &&
                   command.Contains(us, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The executable named by a registered command, if it can be read out.
        /// </summary>
        public static string? RegisteredCommandTarget(string commandKeyPath)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(commandKeyPath);
                if (key?.GetValue(null) is not string command || command.Length == 0) return null;

                // Every command this app writes is `"<exe>" "%1"`, so the target
                // is whatever sits between the first pair of quotes.
                if (command[0] != '"') return null;
                int end = command.IndexOf('"', 1);
                return end < 0 ? null : command[1..end];
            }
            catch { return null; }
        }

        /// <summary>
        /// True when folders are registered to open an executable that is no
        /// longer there.
        ///
        /// This is the state that leaves a machine unable to open any folder at
        /// all, and it is exactly what happens when the registration named a
        /// build output and the build output was cleaned away.
        /// </summary>
        public static bool RegistrationIsBroken()
        {
            var target = RegisteredCommandTarget(@"Software\Classes\Directory\shell\open\command");
            if (target == null) return false;
            try { return !File.Exists(target); } catch { return false; }
        }

        // ---------- Context menu verbs ----------

        public static bool IsContextMenuRegistered()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(DirectoryDefaultCommand);
                return PointsAtUs(key?.GetValue(null) as string);
            }
            catch { return false; }
        }

        public static void RegisterContextMenu()
        {
            // Asked before the write, because after it the answer is always yes.
            // Reconcile calls this whenever the executable has moved as well as
            // when the entry is missing, and a rewrite in the same place is not
            // something to tell anybody about.
            bool was = IsContextMenuRegistered();

            WriteVerb(ContextTargets, VerbName, VerbLabel, $"\"{ExePath}\" \"%V\"");
            NotifyShell();

            if (!was) Changed?.Invoke("shell.menu.on", $"\"{VerbLabel}\" added to the folder context menu");
        }

        public static void UnregisterContextMenu()
        {
            bool was = IsContextMenuRegistered();

            RemoveVerb(ContextTargets, VerbName);
            NotifyShell();

            if (was) Changed?.Invoke("shell.menu.off", $"\"{VerbLabel}\" removed from the folder context menu");
        }

        // ---------- Send to iPhone verb ----------

        /// <summary>
        /// Removes the old "Send to iPhone" verb. The feature is gone, but a
        /// previous version may have written this, and a menu entry pointing at
        /// a command that no longer exists is worse than none.
        /// </summary>
        public static void UnregisterSendToIPhone()
        {
            RemoveVerb(SendTargets, SendVerbName);
            NotifyShell();
        }

        /// <summary>
        /// Brings the registry in line with the settings, rewriting the stored
        /// command whenever the executable has moved. That is what lets the app
        /// stay portable: move the folder, run it once, and the shell entries
        /// point at the new location instead of a file that is gone.
        /// </summary>
        public static bool Reconcile(Settings settings)
        {
            bool moved = !string.Equals(settings.RegisteredExePath, ExePath, StringComparison.OrdinalIgnoreCase);

            if (settings.RegisterContextMenu)
            {
                // The same rule the folder verb has lived by, which this had
                // somehow never been given: a copy that is not the installed one
                // never rewrites the registration.
                //
                // Without it, running the debug build once pointed "Open in
                // Explorer Native" at bin\Debug — and the next rebuild, or a
                // dotnet clean, left a context-menu entry aimed at a file that no
                // longer exists. ReconcileFolderOpen guards exactly this; the
                // context menu was writing whatever executable happened to be
                // running.
                //
                // Missing entirely is still repaired by anyone, because a menu
                // entry that does nothing is worse than one pointing at a build.
                bool mayRewrite = AppInstall.IsRunningInstalledCopy || !AppInstall.IsInstalled;

                // And what the entry names, not only whether RegisteredExePath
                // moved: an install records the new path without rewriting the
                // entry, so an entry written by a build before anything was
                // installed kept naming the build for good.
                var named = RegisteredCommandTarget(DirectoryDefaultCommand);
                bool stale = false;
                if (named != null)
                {
                    try
                    {
                        stale = !File.Exists(named) ||
                                (mayRewrite && !string.Equals(Path.GetFullPath(named), Path.GetFullPath(ExePath),
                                    StringComparison.OrdinalIgnoreCase));
                    }
                    catch { stale = false; }
                }

                if (!IsContextMenuRegistered() || (moved && mayRewrite) || stale) RegisterContextMenu();
            }
            else if (IsContextMenuRegistered()) UnregisterContextMenu();

            ReconcileFolderOpen(settings);

            // Always cleared, whatever the settings say: an older build claimed
            // the base Folder class and so took over Control Panel, Settings and
            // everything else the shell calls a folder. Anyone who ran that build
            // still has it, and it has to come off their machine even if they now
            // have the takeover switched off entirely.
            ReleaseOverreachingFolderClass();
            ReleaseWindowsEShortcut();

            // Always cleared: the send feature was removed, so any verb left
            // behind by an older build is swept up here.
            UnregisterSendToIPhone();

            bool changed = settings.RegisteredExePath != ExePath;
            settings.RegisteredExePath = ExePath;
            return changed;
        }

        /// <summary>
        /// Brings the folder-open takeover in line with the settings, without
        /// letting the wrong copy of the application claim it.
        ///
        /// The rule is that the registration belongs to the *installed* copy.
        /// Every launch used to rewrite the entries whenever the executable had
        /// moved, which meant running the project's own debug build silently
        /// re-pointed every folder on the machine at bin\Debug — and the next
        /// rebuild then broke them. A copy running from anywhere else now leaves
        /// the registration alone, with one exception: if it points at an
        /// executable that no longer exists, folders open nothing at all, and
        /// repairing that is worth more than the rule.
        /// </summary>
        private static void ReconcileFolderOpen(Settings settings)
        {
            if (!settings.SetAsDefaultFileExplorer)
            {
                if (IsDefaultFileExplorer()) SetDefaultFileExplorer(false);
                return;
            }

            if (RegistrationIsBroken())
            {
                SetDefaultFileExplorer(true);
                return;
            }

            if (!IsDefaultFileExplorer())
            {
                SetDefaultFileExplorer(true);
                return;
            }

            // Already registered. Only refresh the path when this really is the
            // copy the registration is meant to name.
            var target = RegisteredCommandTarget(@"Software\Classes\Directory\shell\open\command");
            bool namesTheRightCopy = target != null &&
                string.Equals(Path.GetFullPath(target), Path.GetFullPath(ExePath),
                              StringComparison.OrdinalIgnoreCase);

            if (!namesTheRightCopy && AppInstall.IsRunningInstalledCopy) SetDefaultFileExplorer(true);
        }

        private static void WriteVerb(string[] targets, string verbName, string label, string command)
        {
            foreach (var basePath in targets)
            {
                try
                {
                    using var shell = Registry.CurrentUser.CreateSubKey(basePath);
                    if (shell == null) continue;

                    using var verb = shell.CreateSubKey(verbName);
                    if (verb == null) continue;
                    verb.SetValue(null, label);
                    verb.SetValue("Icon", $"\"{ExePath}\",0");

                    using var cmd = verb.CreateSubKey("command");
                    cmd?.SetValue(null, command);
                }
                catch { }
            }
        }

        private static void RemoveVerb(string[] targets, string verbName)
        {
            foreach (var basePath in targets)
            {
                try
                {
                    using var shell = Registry.CurrentUser.OpenSubKey(basePath, writable: true);
                    shell?.DeleteSubKeyTree(verbName, throwOnMissingSubKey: false);
                }
                catch { }
            }
        }

        // ---------- Start with Windows ----------

        public static bool IsStartupRegistered()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupRun);
                return key?.GetValue(StartupValue) != null;
            }
            catch { return false; }
        }

        public static void SetStartup(bool enabled)
        {
            bool changed = false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupRun, writable: true);
                if (key == null) return;

                // Read from the key that is already open rather than through
                // IsStartupRegistered. This is called unconditionally on every
                // launch, and every launch is a folder somebody is waiting to
                // see — a second OpenSubKey to learn something the open handle
                // already knows is not worth a notification.
                bool was = key.GetValue(StartupValue) != null;

                // The same rule the folder registration lives by: never write a
                // path inside bin\. ExePath falls back to this process when
                // nothing is installed, so running a build once wrote a logon
                // entry naming that build — and a clean, or deleting bin\, left
                // Windows starting a file that is not there, silently, with
                // nothing to repair it. Startup is on by default, so it did not
                // even take a decision to get into that state.
                bool namesABuild = !AppInstall.IsInstalled &&
                                   !AppInstall.IsRunningInstalledCopy;

                bool now = enabled && !namesABuild;

                if (now) key.SetValue(StartupValue, $"\"{ExePath}\"");
                else if (!enabled) key.DeleteValue(StartupValue, throwOnMissingValue: false);

                // What the key holds now, not what was asked for — a request
                // that was declined is not a change to announce.
                changed = was != now;
            }
            catch { }

            // Outside the catch, so a subscriber that throws is not mistaken for
            // a registry failure and quietly swallowed.
            if (!changed) return;

            Changed?.Invoke(enabled ? "startup.on" : "startup.off",
                enabled
                    ? "Explorer Native will start with Windows"
                    : "Explorer Native will no longer start with Windows");
        }

        // ---------- Full deregistration ----------

        // ---------- Default file explorer ----------

        /// <summary>
        /// The shell classes whose "open" verb decides what happens when you
        /// double-click a folder on disk.
        ///
        /// Directory is a filesystem folder and Drive is a volume root. Those are
        /// the two things this application can actually show.
        ///
        /// The base class "Folder" is deliberately NOT here, and must not be added
        /// back. Every shell folder inherits from it — Control Panel, Settings,
        /// This PC, Recycle Bin, Network, and every applet inside them — so
        /// claiming its open verb does not mean "all folders", it means "all of
        /// Windows". Opening a Control Panel item launched this application with a
        /// path that is not a path at all. Directory and Drive define their own
        /// open verb, which takes precedence over the inherited one, so nothing on
        /// disk is lost by leaving Folder alone.
        /// </summary>
        private static readonly string[] OpenVerbClasses = { "Directory", "Drive" };

        /// <summary>
        /// Claimed by an earlier version, and swept up on every launch. See above
        /// for why it should never have been claimed.
        /// </summary>
        private const string OverreachingFolderClass = "Folder";

        /// <summary>
        /// The File Explorer shell folder's "open a new window" verb, which Win+E
        /// goes through.
        ///
        /// Claimed once, and it must not be claimed again. Windows implements this
        /// verb *in process*: it has no command line at all, only a DelegateExecute
        /// pointing at a COM object inside the shell. Taking it over therefore
        /// means blanking that DelegateExecute — and the shell uses this verb for
        /// far more than the Win+E shortcut. With it blanked, ordinary navigation
        /// that expected to be handled in process instead launched this
        /// application: opening Settings and moving to the Power page closed
        /// Settings and put a file manager on screen.
        ///
        /// There is no safe version of this. Win+E belongs to File Explorer; the
        /// application's own global hotkey is the way to summon this window.
        /// Swept off every launch for anyone who ran the build that claimed it.
        /// </summary>
        private const string ExplorerClsidRoot =
            @"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";

        private const string ExplorerClsidCommand = ExplorerClsidRoot + @"\shell\opennewwindow\command";

        /// <summary>Where the previous values are kept so removal can put them back.</summary>
        private const string BackupKey = @"Software\ExplorerNative\ShellBackup";

        private const string AppRegistration = @"Software\Classes\Applications\ExplorerNative.exe";

        /// <summary>
        /// True when this executable is currently the handler for opening folders.
        /// </summary>
        public static bool IsDefaultFileExplorer()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell\open\command");
                return PointsAtUs(key?.GetValue(null) as string);
            }
            catch { return false; }
        }

        /// <summary>
        /// Makes this app — or stops it being — what Windows opens folders with.
        ///
        /// Everything is written under HKCU\Software\Classes, which the shell
        /// merges *over* the machine-wide definitions in HKLM. Windows' own
        /// handlers are never touched, so removing our keys restores the stock
        /// behaviour exactly rather than reconstructing it from memory. Any value
        /// that did already exist under HKCU is copied aside first and put back on
        /// removal.
        ///
        /// This does not, and cannot safely, replace the desktop, the taskbar or
        /// the Start menu: those are explorer.exe itself, swapped only through the
        /// Winlogon shell value, which leaves a machine with no desktop at all if
        /// the replacement does not implement one. What it does cover is every
        /// route that opens a folder window.
        /// </summary>
        public static void SetDefaultFileExplorer(bool enabled)
        {
            bool was = IsDefaultFileExplorer();

            if (enabled) ClaimFolderOpen();
            else ReleaseFolderOpen();
            NotifyShell();

            // ReconcileFolderOpen also comes through here to *repair* a
            // registration that already named this application but pointed at an
            // executable that is gone. Nothing changed hands there, and saying
            // "Explorer Native now opens folders" would be telling somebody about
            // a takeover they did months ago, every time they open a folder.
            if (was == enabled) return;

            Changed?.Invoke(enabled ? "shell.default.on" : "shell.default.off",
                enabled
                    ? "Explorer Native now opens folders"
                    : "Windows Explorer opens folders again");
        }

        private static void ClaimFolderOpen()
        {
            var command = $"\"{ExePath}\" \"%1\"";

            foreach (var cls in OpenVerbClasses)
            {
                var commandKey = $@"Software\Classes\{cls}\shell\open\command";

                // Decided once, before anything is written, and shared by all
                // three values so the verb name under \shell is judged by the
                // command rather than by the word "open", which says nothing
                // about who put it there.
                bool alreadyOurs = CommandIsOurs(commandKey);

                try
                {
                    Backup(commandKey, null, alreadyOurs, command);
                    Backup(commandKey, "DelegateExecute", alreadyOurs, "");

                    using var key = Registry.CurrentUser.CreateSubKey(commandKey);
                    if (key == null) continue;
                    key.SetValue(null, command);

                    // DelegateExecute wins over the command string. The machine-wide
                    // definition carries one pointing at the shell's own handler, so
                    // without blanking it here the command below is never reached and
                    // the whole registration silently does nothing.
                    key.SetValue("DelegateExecute", "");
                }
                catch { }

                // Name the verb explicitly. Some of these classes ship with a
                // different default verb, and the one we just wrote has to be the
                // one a plain double-click picks.
                try
                {
                    var shellKey = $@"Software\Classes\{cls}\shell";
                    Backup(shellKey, null, alreadyOurs, "open");
                    using var key = Registry.CurrentUser.CreateSubKey(shellKey);
                    key?.SetValue(null, "open");
                }
                catch { }
            }

            // Win+E is deliberately not claimed. See ExplorerClsidRoot.

            // Lets the app be picked from "Open with" and appear as a real
            // application to the shell, rather than only as a hijacked verb.
            try
            {
                using var app = Registry.CurrentUser.CreateSubKey($@"{AppRegistration}\shell\open\command");
                app?.SetValue(null, $"\"{ExePath}\" \"%1\"");
                using var friendly = Registry.CurrentUser.CreateSubKey(AppRegistration);
                friendly?.SetValue("FriendlyAppName", "Explorer Native");
            }
            catch { }
        }

        /// <summary>
        /// Hands Win+E back to File Explorer.
        ///
        /// Run unconditionally on every launch, for the same reason as the Folder
        /// class: while this was claimed, the shell could not perform an ordinary
        /// in-process navigation, and switching the takeover off is not something
        /// a person should have to think of before Settings works again.
        /// </summary>
        public static void ReleaseWindowsEShortcut()
        {
            bool ours = CommandIsOurs(ExplorerClsidCommand);

            if (!ours)
            {
                ForgetBackup(ExplorerClsidCommand, null);
                ForgetBackup(ExplorerClsidCommand, "DelegateExecute");
                return;
            }

            RestoreOrRemove(ExplorerClsidCommand, null, ours: true);
            RestoreOrRemove(ExplorerClsidCommand, "DelegateExecute", ours: true);

            PruneIfEmpty(ExplorerClsidCommand);
            PruneIfEmpty(ExplorerClsidRoot + @"\shell\opennewwindow");
            PruneIfEmpty(ExplorerClsidRoot + @"\shell");
            PruneIfEmpty(ExplorerClsidRoot);

            NotifyShell();
        }

        /// <summary>
        /// Hands the base Folder class back to Windows.
        ///
        /// Run unconditionally on every launch, because the damage is not
        /// proportionate to the setting: while this was claimed, opening anything
        /// the shell calls a folder — a Control Panel applet, a Settings page —
        /// started this application instead. Only removed if it is ours; another
        /// application's registration is left alone, same as everywhere else.
        /// </summary>
        public static void ReleaseOverreachingFolderClass()
        {
            const string cls = OverreachingFolderClass;
            var commandKey = $@"Software\Classes\{cls}\shell\open\command";

            if (!CommandIsOurs(commandKey))
            {
                // Not ours to remove — but our notes about what used to be there
                // are stale and would only mislead a later restore.
                ForgetBackup(commandKey, null);
                ForgetBackup(commandKey, "DelegateExecute");
                ForgetBackup($@"Software\Classes\{cls}\shell", null);
                return;
            }

            RestoreOrRemove(commandKey, null, ours: true);
            RestoreOrRemove(commandKey, "DelegateExecute", ours: true);
            PruneIfEmpty(commandKey);
            PruneIfEmpty($@"Software\Classes\{cls}\shell\open");

            RestoreOrRemove($@"Software\Classes\{cls}\shell", null, ours: true);
            PruneIfEmpty($@"Software\Classes\{cls}\shell");

            NotifyShell();
        }

        private static void ForgetBackup(string keyPath, string? valueName)
        {
            try
            {
                using var store = Registry.CurrentUser.OpenSubKey(BackupKey, writable: true);
                store?.DeleteValue(BackupSlot(keyPath, valueName), throwOnMissingValue: false);
            }
            catch { }
        }

        private static void ReleaseFolderOpen()
        {
            foreach (var cls in OpenVerbClasses)
            {
                var commandKey = $@"Software\Classes\{cls}\shell\open\command";

                // Decided before anything is written, and from the command — the
                // only value here that names an executable. The verb name under
                // \shell is just the word "open", which says nothing about who
                // put it there.
                bool ours = CommandIsOurs(commandKey);

                RestoreOrRemove(commandKey, null, ours);
                RestoreOrRemove(commandKey, "DelegateExecute", ours);
                PruneIfEmpty(commandKey);
                PruneIfEmpty($@"Software\Classes\{cls}\shell\open");

                RestoreOrRemove($@"Software\Classes\{cls}\shell", null, ours);
                PruneIfEmpty($@"Software\Classes\{cls}\shell");
            }

            ReleaseWindowsEShortcut();

            try { Registry.CurrentUser.DeleteSubKeyTree(AppRegistration, throwOnMissingSubKey: false); } catch { }
        }

        /// <summary>
        /// Copies a value aside before we overwrite it, once.
        ///
        /// Recorded even when there is nothing there: "this did not exist" is
        /// exactly what removal needs to know, and is the usual case. Backing up
        /// twice would overwrite the original with our own value.
        ///
        /// <paramref name="alreadyOurs"/> says the key is already registered to
        /// this application, in which case there is nothing of anyone else's to
        /// preserve and the recorded state is "absent".
        ///
        /// That distinction matters most when repairing. A registration left
        /// pointing at a copy that has since been deleted still names this
        /// application, so recording it as the previous state would make a later
        /// deregistration faithfully restore a dead path — putting the machine
        /// straight back to opening no folders at all.
        /// </summary>
        private static void Backup(string keyPath, string? valueName, bool alreadyOurs, string ourValue)
        {
            try
            {
                var slot = BackupSlot(keyPath, valueName);
                using var store = Registry.CurrentUser.CreateSubKey(BackupKey);
                if (store == null) return;

                // Once ours, the note already says what was there before us and
                // writing again would record ourselves. But when the key belongs
                // to somebody else *now*, whatever the note says is older than
                // them — our first claim, before another application took folders
                // over — and keeping it meant turning this off later restored
                // "nothing was here", deleting that application's registration
                // instead of handing folders back to it.
                using var source = Registry.CurrentUser.OpenSubKey(keyPath);
                var current = source?.GetValue(valueName) as string;

                if (store.GetValue(slot) != null)
                {
                    if (alreadyOurs) return;

                    // A value that is still the one we write is our own leftover —
                    // the verb name, the blanked DelegateExecute — not something
                    // the other application put there, and recording it would make
                    // standing down leave it behind.
                    if (current == ourValue) return;
                }

                var existing = alreadyOurs ? null : current;
                store.SetValue(slot, existing ?? "\0absent");
            }
            catch { }
        }

        /// <summary>True when the command in this key launches this program.</summary>
        private static bool CommandIsOurs(string commandKeyPath)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(commandKeyPath);
                return PointsAtUs(key?.GetValue(null) as string);
            }
            catch { return false; }
        }

        /// <summary>
        /// Puts one value back the way it was before we claimed it.
        ///
        /// <paramref name="ours"/> is the guard against vandalising somebody
        /// else's registration. Deregistering used to delete the value whenever no
        /// backup was recorded — which is exactly the state after an earlier
        /// deregistration — so a second --unregister-default, or one run from a
        /// copy of this app that never registered anything, wiped whatever handler
        /// was actually installed. Now a value that is not ours is left alone,
        /// which also makes deregistering twice genuinely harmless.
        /// </summary>
        private static void RestoreOrRemove(string keyPath, string? valueName, bool ours)
        {
            try
            {
                var slot = BackupSlot(keyPath, valueName);
                using var store = Registry.CurrentUser.OpenSubKey(BackupKey, writable: true);
                var saved = store?.GetValue(slot) as string;

                if (!ours)
                {
                    // Someone else owns this now. Drop our stale note about what
                    // used to be here — it describes a world two handlers ago —
                    // and leave their registration untouched.
                    store?.DeleteValue(slot, throwOnMissingValue: false);
                    return;
                }

                using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);

                if (saved == null || saved == "\0absent")
                {
                    // Nothing was there before us, so the correct restoration is to
                    // take our value away and let the machine-wide definition show
                    // through again.
                    key?.DeleteValue(valueName ?? "", throwOnMissingValue: false);
                }
                else key?.SetValue(valueName, saved);

                store?.DeleteValue(slot, throwOnMissingValue: false);
            }
            catch { }
        }

        private static string BackupSlot(string keyPath, string? valueName) =>
            keyPath + "|" + (valueName ?? "(default)");

        /// <summary>Removes a key we created if nothing is left in it.</summary>
        private static void PruneIfEmpty(string keyPath)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                {
                    if (key == null) return;
                    if (key.SubKeyCount > 0 || key.ValueCount > 0) return;
                }
                Registry.CurrentUser.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
            }
            catch { }
        }

        /// <summary>Removes every trace this app added to the registry.</summary>
        public static void UnregisterEverything()
        {
            UnregisterContextMenu();
            UnregisterSendToIPhone();
            ReleaseOverreachingFolderClass();
            ReleaseWindowsEShortcut();
            SetDefaultFileExplorer(false);
            SetStartup(false);
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\ExplorerNative", throwOnMissingSubKey: false); } catch { }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private static void NotifyShell()
        {
            const int SHCNE_ASSOCCHANGED = 0x08000000;
            const uint SHCNF_IDLIST = 0x0000;
            try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); } catch { }
        }
    }
}
