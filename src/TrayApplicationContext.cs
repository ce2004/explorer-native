using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Owns the tray icon and the global hotkey so the app survives the window
    /// being closed, and rebuilds both when preferences change.
    /// </summary>
    public sealed class TrayApplicationContext : ApplicationContext
    {
        /// <summary>One percent a press, always; holding the key speeds it up.</summary>
        private const int VolumeStepPercent = 1;

        private readonly NotifyIcon _tray;
        private readonly HotkeyManager _hotkeys = new();
        private readonly AudioPlayer _audio = new();

        /// <summary>Warms the head of whatever the cursor is sitting on, so Enter is quick.</summary>
        private readonly AudioPrefetch _browsePrefetch = new();

        /// <summary>Repeats a shortcut for as long as it is held down.</summary>
        private readonly HoldRepeat _hold = new();

        /// <summary>
        /// Google Drive on a drive letter, when it is switched on. Owned here
        /// rather than by the window because the window comes and goes and the
        /// letter must not.
        /// </summary>
        private readonly GoogleDrive _drive = new();
        private readonly DriveMonitor _monitor;
        private ConnectServer? _connect;
        private ConnectStreams? _connectStreams;
        private ConnectClipboard? _connectClipboard;
        private Settings _settings;
        private MainForm? _form;

        /// <summary>
        /// Marshals work from the single-instance listener onto the UI thread.
        ///
        /// Captured in the constructor, which runs on the UI thread before the
        /// message loop starts. A NotifyIcon has no window handle of its own to
        /// BeginInvoke through, and the form may not exist yet — so the
        /// synchronisation context is the only handle onto the right thread.
        /// </summary>
        private readonly SynchronizationContext _ui;

        public TrayApplicationContext(Settings settings)
        {
            _settings = settings;
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

            var menu = new ContextMenuStrip();
            menu.Items.Add("&Open", null, (_, _) => ShowWindow());
            // Not over another dialog: the tray menu still works while one is up,
            // and a second on top of the first saved over what the first applied.
            menu.Items.Add("&Preferences", null, (_, _) =>
            {
                if (_audioDialogOpen) { Notify("error.generic", "Close the audio dialog first"); return; }
                if (_form?.PreferencesOpen == true) return;
                ShowWindow();
                _form?.OpenPreferencesExternally();
            });
            menu.Items.Add(new ToolStripSeparator());

            // The way to reach the player when a shortcut does not work — and a
            // shortcut can fail without saying so. RegisterHotKey reports a
            // conflict only against another RegisterHotKey; anything with a
            // low-level keyboard hook above it (a screen reader, a graphics
            // driver) takes the keystroke first and leaves a registration that
            // succeeded and never fires.
            menu.Items.Add("Play or &pause", null, (_, _) => RunAudioAction(AudioAction.PlayPause));
            menu.Items.Add("S&top playing", null, (_, _) => RunAudioAction(AudioAction.Stop));
            menu.Items.Add("&What is playing", null, (_, _) => RunAudioAction(AudioAction.WhatIsPlaying));
            menu.Items.Add(new ToolStripSeparator());

            // The web app's link, Copy link, status and pairing code all live on
            // Preferences' Web app page; this is the short way there.
            menu.Items.Add("&Web app…", null, (_, _) =>
            {
                if (_audioDialogOpen) { Notify("error.generic", "Close the audio dialog first"); return; }
                if (_form?.PreferencesOpen == true) return;
                ShowWindow();
                _form?.OpenPreferencesExternally("Web app");
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("E&xit", null, (_, _) => Quit(ask: true));

            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = TrayTooltip(settings),
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.DoubleClick += (_, _) => ShowWindow();

            _hotkeys.HotkeyPressed += OnHotkey;

            // Both of these arrive on a Media Foundation worker thread.
            _audio.Finished += path => PostUi(() => OnTrackFinished(path));
            _audio.Failed += path => PostUi(() => OnTrackFailed(path));

            // The engine is built on this thread and belongs to it, so the
            // player's own timers — the seek coalescer and the volume fade — come
            // back here rather than calling across an apartment boundary.
            _audio.Post = work => PostUi(() => work());

            // The four notification hooks the layers below expose. Each is null
            // until something subscribes, and each layer checks that before
            // building a string — the fetch callback alone runs 891 times for one
            // 232MB track, on a thread that is holding up whoever is reading.
            _audio.Notify = OnPlayerNotification;
            _browsePrefetch.Notify = OnPlayerNotification;
            AudioTags.Notify = OnPlayerNotification;
            _drive.Notification += (id, message) => PostUi(() => Notify(id, message));

            // Preferences' account button says Connect, Disconnect or Reconnect
            // from the drive itself, and Disconnect acts on it directly.
            SettingsForm.AccountState = () =>
                !_settings.GoogleDriveEnabled || !GoogleDrive.HasSavedSignIn ? DriveAccountState.NotConnected
                : _drive.Mounted && !_drive.NeedsSignIn ? DriveAccountState.Connected
                : DriveAccountState.NeedsSignIn;
            SettingsForm.DisconnectDrive = () =>
            {
                _settings.GoogleDriveEnabled = false;
                _settings.Save();
                _drive.Unmount();
            };

            // Preferences, Web app: Tailscale Serve on or off, from a worker.
            SettingsForm.WebAppApply = on =>
            {
                int port = _connect?.ListenPort ?? _settings.WebAppPort;
                if (on) return TailscaleWeb.Enable(port);
                TailscaleWeb.Disable(port);
                return new TailscaleWeb.EnableResult(true, null, null);
            };
            SettingsForm.CurrentWebPort = () => _connect?.ListenPort ?? _settings.WebAppPort;

            // The folder monitor: pairs live in settings, saved at once when the
            // monitor dialog changes them.
            _monitor = new DriveMonitor(_drive,
                (id, message) => PostUi(() => Notify(id, message)),
                pairs => PostUi(() =>
                {
                    _settings.DriveSyncPairs = pairs;
                    _settings.Save();
                    DriveMonitor.ForgetRemovedPairs(pairs);
                }));
            _monitor.Reload(_settings.DriveSyncPairs);
            DrivePairForm.DriveMonitorClient = () => _drive.Client;

            // A track on the Drive letter downloads straight from Google.
            AudioPlayer.RangeSourceFor = path => _drive.OpenRange(path);

            StartConnect();

            // A cancelled copy out of Drive ends robocopy; not mid-read.
            RoboCopyEngine.BeforeKill = _drive.StandDownReads;
            RoboCopyEngine.AfterKill = _drive.ForgetReader;

            // Anything left by a run that was killed before it could tidy up.
            StreamingSource.SweepOldCopies();

            // Same idea, one layer down: a sync root registration and a drive
            // letter both outlive the process that made them, so a run that was
            // killed rather than closed leaves a letter pointing at a folder full
            // of placeholders with nobody left to hydrate them.
            GoogleDrive.SweepOrphans(_settings);

            // Google expires a refresh token after seven days for an app that has
            // not been through verification, and verification for a Drive scope
            // means a third-party security assessment — so this is a weekly fact
            // of life, not a fault. Said once, with the way back in the same
            // sentence.
            // A cloud drive going offline is a Tuesday, not a fault — a lid
            // closed, a tunnel, a router rebooting. Said once when it happens and
            // once when it comes back, rather than once per file that would not
            // open.
            _drive.ConnectionChanged += state => PostUi(() =>
            {
                if (state == DriveState.Offline)
                    Notify("drive.offline",
                        "Google Drive is offline. " + _drive.Letter + " will not open folders or play " +
                        "tracks until the connection comes back.");
                else if (state == DriveState.Online)
                    Notify("drive.online", "Google Drive is back online.");
            });

            _drive.SignInRequired += () => PostUi(() =>
                Notify("drive.signin.needed",
                    "Google Drive sign-in has expired. Open Preferences, Google Drive, to sign in again."));

            // The registry is the only thing that knows whether a claim actually
            // changed hands — everything here is reconciled on every launch and
            // rewrites entries that were already ours — so the report comes from
            // where the write happens rather than from a comparison of settings.
            ShellRegistration.Changed += OnShellRegistrationChanged;

            // NVDA going away takes every announcement with it, silently. The
            // pump already asks whether it is there before each utterance; this
            // only listens to the answer, and arrives on that thread.
            Speech.Notification += OnSpeechAvailabilityChanged;

            if (_settings.GoogleDriveEnabled) MountGoogleDrive();
            ApplyAudioSettings();
            if (_settings.AudioPlayerEnabled) WarmAudio();

            RegisterHotkeys();

            // Not against defaults that stand in for a file that could not be
            // opened: that took folders, the context menu and the startup entry
            // away because another program held settings.json for a moment. The
            // file is read again shortly, and the registry is settled then.
            if (_settings.UnreadableOnDisk)
            {
                RetrySettings();
            }
            else
            {
                EnsureInstalledIfTakingOverFolders();
                ShellRegistration.SetStartup(_settings.StartWithWindows);

                // Brings the shell entries in line, and repairs them if they point at
                // an executable that is no longer there.
                if (ShellRegistration.Reconcile(_settings)) _settings.Save();
            }

            // "Start minimised" is about an unprompted startup — signing in, or
            // the Run key firing. Being handed a folder is a direct request to
            // look at something, so it wins: without this, double-clicking a
            // folder while the app is the default handler started a process that
            // put nothing at all on screen.
            bool askedForAFolder = !string.IsNullOrEmpty(_settings.InitialFolderOverride);
            if (!_settings.StartMinimised || askedForAFolder) ShowWindow();

            // Posted, not said. Everything above is the path to the window, and
            // this application is the shell's handler for every folder on the
            // machine — started afresh on every folder open, ~420ms cold. The
            // catalogue is 167 entries and looking one up builds it, so the
            // lookup waits for the message loop and lands after the folder is on
            // screen. The post itself is a queued delegate and nothing else.
            PostUi(() => Notify("app.started", _settings.DisplayTitle + " is ready"));
        }

        private void OnShellRegistrationChanged(string id, string message) => Notify(id, message);

        private System.Threading.Timer? _settingsRetry;
        /// <summary>What a recovery flag switches off.</summary>
        [Flags]
        private enum Released { None = 0, Folders = 1, ContextMenu = 2, Startup = 4 }

        /// <summary>
        /// Settings a recovery flag switched off while settings.json could not be
        /// read, each until it is written down or chosen again. A release is the
        /// default value, so a merge that carries only what differs from the
        /// defaults cannot carry it. Touched on the UI thread only.
        /// </summary>
        private Released _pendingRelease;

        /// <summary>When the last of those arrived, in UTC ticks.</summary>
        private long _releasedAt;

        /// <summary>
        /// The waiting releases, laid over <paramref name="merged"/>. The folder
        /// release gives way to a takeover the command line recorded after it
        /// (<see cref="Settings.FoldersTakenAt"/>) — a decision, where the file's
        /// own date is any write at all.
        /// </summary>
        private void ApplyPendingReleases(Settings merged)
        {
            if (merged.FoldersTakenAt > _releasedAt) _pendingRelease &= ~Released.Folders;
            if (_pendingRelease.HasFlag(Released.Folders)) merged.SetAsDefaultFileExplorer = false;
            if (_pendingRelease.HasFlag(Released.ContextMenu)) merged.RegisterContextMenu = false;
            if (_pendingRelease.HasFlag(Released.Startup)) merged.StartWithWindows = false;
        }

        /// <summary>
        /// Reads settings.json again every two seconds, for a minute, until it
        /// opens; then takes it on, with whatever was changed in this session
        /// laid over it.
        /// </summary>
        private void RetrySettings()
        {
            int tries = 0;
            _settingsRetry = new System.Threading.Timer(_ =>
            {
                if (_quitting || ++tries > 30)
                {
                    _settingsRetry?.Dispose();
                    return;
                }

                // Still held: try again. Opened and damaged is an answer too — a
                // copy has been kept aside and the defaults are what there is.
                var fresh = Settings.Load(out bool _);
                if (fresh.UnreadableOnDisk) return;

                _settingsRetry?.Dispose();
                PostUi(() =>
                {
                    // A reload from the command line may have taken the file on
                    // already; merging against the defaults again would undo it.
                    if (!_settings.UnreadableOnDisk) return;

                    var merged = Settings.WithChanges(fresh, new Settings(), _settings);
                    // The file's own pairing code; a new one only if it had none.
                    merged.EnsureConnectCode();
                    ApplyPendingReleases(merged);
                    ApplySettings(merged, reload: true);
                    merged.Save();
                    _pendingRelease = Released.None;
                    EnsureInstalledIfTakingOverFolders();
                });
            }, null, 2000, 2000);
        }

        /// <summary>Set by <see cref="Quit"/>; nothing posted after it runs.</summary>
        private volatile bool _quitting;

        /// <summary>
        /// Runs <paramref name="work"/> on the UI thread, unless the application
        /// is on its way out.
        ///
        /// Workers keep raising things after Quit — a Drive notification, a track
        /// failing as its source is torn down — and posting then either threw on
        /// the worker, which is an unhandled exception during exit, or ran against
        /// a tray icon and a window that had already been disposed.
        /// </summary>
        private void PostUi(Action work)
        {
            if (_quitting) return;
            try
            {
                _ui.Post(_ =>
                {
                    if (_quitting) return;
                    work();
                }, null);
            }
            catch
            {
                // The loop that would have run it has gone.
            }
        }

        /// <summary>
        /// Raised on the speech pump thread, so it comes home before it touches
        /// the tray or the window.
        /// </summary>
        private void OnSpeechAvailabilityChanged(string id, string message) =>
            PostUi(() => Notify(id, message));

        /// <summary>
        /// Delivers a message under its catalogue id: to the status bar always,
        /// and spoken as well when Preferences says so.
        ///
        /// An id the catalogue does not know still gets through. A message that
        /// exists in the code and not in the catalogue is a gap in the
        /// catalogue, and swallowing it would hide the very thing that says so.
        /// </summary>
        private void Notify(string id, string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            // An id nothing knows is shown and not spoken, which is what it was
            // before: a gap in the catalogue should be visible without being
            // able to interrupt anybody.
            bool speak = Notifications.ChannelsFor(id, _settings) == NotificationChannel.Speech;

            if (_form is { IsDisposed: false, Visible: true }) _form.AnnounceAudio(message, speak);
            else if (speak && _settings.SpeakEnabled) Speech.Speak(message, _settings.InterruptSpeech);
        }

        /// <summary>
        /// Puts Google Drive on a letter, in the background.
        ///
        /// Never on the way in to the UI thread: this signs in over the network
        /// and lists the top of the Drive, and this application is the shell
        /// handler for every folder on the machine — it is started afresh on
        /// every folder open, and nothing that waits on Google belongs in that
        /// path.
        /// </summary>
        private void MountGoogleDrive()
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                bool ok = await _drive.Mount(_settings);
                PostUi(() =>
                {
                    if (ok)
                    {
                        Notify("drive.mounted", $"Google Drive is on {_drive.Letter}");
                        _monitor.SyncNow();

                        // The pane may already be showing the letter from a
                        // previous session's position memory, in which case it is
                        // sitting on an empty folder that is no longer empty.
                        _form?.RefreshIfShowingDrive(_drive.Letter);
                    }
                    else if (_settings.GoogleDriveEnabled)
                    {
                        // Not when Drive was switched off while this was on its way
                        // up: that is a mount being called off, not failing.
                        Notify("drive.signin.refused",
                            "Google Drive did not connect. " + _drive.Status);
                    }
                });
            });
        }

        /// <param name="reload">
        /// The file was changed from outside — a command-line flag — rather
        /// than by somebody pressing OK in Preferences. Nothing is announced,
        /// no shortcut dialog is put up again, and Drive is only connected when
        /// the change is what switched it on: a reload used to open a Google
        /// sign-in page from an unattended install.
        /// </param>
        public void ApplySettings(Settings settings, bool reload = false)
        {
            bool driveWasOn = _settings.GoogleDriveEnabled;
            bool webWasOn = _settings.WebAppEnabled;
            int portWas = _connect?.ListenPort ?? _settings.WebAppPort;
            // Only a port setting that changed is acted on. On the fallback port
            // for this session (see StartConnect) the saved port differs from
            // the one listening, and OK on any page tried it, failed, and saved
            // the fallback over it.
            bool portSettingChanged = settings.WebAppPort != _settings.WebAppPort;
            int savedPortWas = _settings.WebAppPort;
            _settings = settings;
            bool portMoved = false;
            if (_connect != null && portSettingChanged && settings.WebAppPort != portWas)
            {
                // Preferences checked the port before OK; it can still have been
                // taken since, in which case the old one is kept and said.
                if (_connect.Rebind(settings.WebAppPort)) portMoved = true;
                else if (reload)
                {
                    // The file's port arriving late (settings.json was locked at
                    // startup, or a command-line reload). Busy now, so the port in
                    // use carries on for this session only, exactly as at startup.
                    // Never written back: that wrote the defaults' 47810 over the
                    // port the person chose.
                    Notify("webapp.port.fallback",
                        $"Port {settings.WebAppPort} is in use by another program, so the web app is using port " +
                        $"{portWas} until Explorer Native next starts. Port {settings.WebAppPort} is still your setting");
                }
                else
                {
                    Notify("webapp.port.failed", (_connect.ListenProblem ?? "That port could not be used.") +
                                                 $" The web app stays on port {portWas}.");
                    // What was saved before OK, not the port listening: on this
                    // session's fallback port the two differ, and saving the
                    // fallback lost the person's own port for good.
                    settings.WebAppPort = savedPortWas;
                    settings.Save();
                }
            }
            if (webWasOn != settings.WebAppEnabled || portMoved) ReconcileWebApp(portWas);

            // Switched back on in Preferences since: that choice stands over a
            // release still waiting for the file to open — that one setting,
            // and only when it is on. OK on anything else leaves the rest.
            if (!reload)
            {
                if (settings.SetAsDefaultFileExplorer) _pendingRelease &= ~Released.Folders;
                if (settings.RegisterContextMenu) _pendingRelease &= ~Released.ContextMenu;
                if (settings.StartWithWindows) _pendingRelease &= ~Released.Startup;
            }
            _tray.Text = TrayTooltip(settings);

            // Program's own two dialogs have no Settings to read, so the name is
            // handed forward whenever it changes rather than looked up.
            Program.RememberTitle(settings);

            ApplyAudioSettings();

            // Somebody has just been in Preferences, so what the shortcuts ended
            // up being is an answer rather than chatter.
            RegisterHotkeys(announce: !reload, dialog: !reload);
            // Not against defaults standing in for a file that could not be
            // opened; see the constructor. RetrySettings settles it once it opens.
            if (!_settings.UnreadableOnDisk)
            {
                EnsureInstalledIfTakingOverFolders();
                ShellRegistration.SetStartup(_settings.StartWithWindows);
            }

            // Reconcile records where the executable was when the registry was
            // written, and that has to be saved or it is recomputed as "moved" on
            // every single launch — rewriting the shell entries each time for no
            // reason. The constructor already did this; here it was dropped,
            // because Program.ApplySettings saves *before* calling us.
            if (!_settings.UnreadableOnDisk && ShellRegistration.Reconcile(_settings)) _settings.Save();

            // Reconnecting is what OK on the Google Drive page means. It is also
            // the way back from an expired sign-in, because consent is allowed on
            // a deliberate connect and nowhere else.
            if (_settings.GoogleDriveEnabled)
            {
                if ((!reload || !driveWasOn) && (!_drive.Mounted || _drive.NeedsSignIn)) MountGoogleDrive();

                // A new letter in Preferences moves a mounted drive straight
                // away. It used to wait for the next start, so changing it
                // appeared to do nothing.
                if (_drive.MoveLetter(_settings.GoogleDriveLetter) is (string from, string to))
                {
                    _form?.DriveLetterMoved(from, to);
                    Notify("drive.mounted", $"Google Drive is on {to}");
                }
            }
            else
            {
                // Unconditionally: a mount still on its way up is not Mounted yet,
                // and would otherwise arrive after Drive had been switched off.
                _drive.Unmount();
            }

            _monitor.Reload(_settings.DriveSyncPairs);
            _form?.ApplyNewSettings(settings);
        }

        /// <summary>
        /// Makes sure there is an installed copy before folders are pointed at
        /// one.
        ///
        /// Ticking the box in Preferences would otherwise register whichever
        /// executable happened to be running — and if that is a build output,
        /// the next rebuild replaces it and the next clean deletes it, taking
        /// every folder on the machine with it. Installing first means the
        /// registry names a copy that only an explicit install ever touches.
        /// </summary>
        private void EnsureInstalledIfTakingOverFolders()
        {
            if (!_settings.SetAsDefaultFileExplorer) return;
            if (AppInstall.IsInstalled) return;

            var result = AppInstall.Install();
            if (result.Ok)
            {
                // Worth saying because it happened without being asked for: the
                // box that was ticked said "open folders with this", and a copy
                // of the application appearing somewhere else on disk is not
                // what that sounds like.
                Notify("install.done", "Installed to " + AppInstall.Directory);
                return;
            }

            Notify("install.failed",
                "Could not install a permanent copy, so folders will open this build instead. " +
                "Rebuilding the project will break that. " + result.Problem);
        }

        /// <summary>
        /// The tray tooltip follows the window title, and is capped: Windows
        /// silently discards a NotifyIcon.Text over 63 characters, tooltip and
        /// all, leaving an icon with no name at all.
        /// </summary>
        private static string TrayTooltip(Settings settings)
        {
            var title = settings.DisplayTitle;
            return title.Length <= 63 ? title : title[..63];
        }

        /// <summary>
        /// Claims every global shortcut the application wants, and reports the
        /// ones Windows would not give it in a single message.
        ///
        /// One balloon rather than one per failure: changing a modifier in
        /// Preferences can easily invalidate several audio shortcuts at once, and
        /// eight notifications in a row is not a report, it is a punishment.
        ///
        /// <paramref name="announce"/> is false on the way in. What succeeded is
        /// only news when something has just been changed, and the constructor
        /// runs on the path that opens every folder on the machine — a catalogue
        /// lookup per shortcut there would be nine of them before the window.
        /// What *failed* is still reported on both paths, because a shortcut that
        /// was working yesterday and is not today is worth hearing about whether
        /// or not anybody touched it.
        /// </summary>
        private void RegisterHotkeys(bool announce = false, bool dialog = true)
        {
            _hotkeys.UnregisterAll();

            var taken = new List<string>();
            var registered = new List<string>();

            // Two actions given the same keys is not another application's
            // fault, and saying that it is sends somebody hunting for a program
            // that does not exist. The Preferences dialog refuses a duplicate,
            // but settings.json is a text file people are meant to be able to
            // edit, and Settings.Validated deliberately leaves shortcuts alone.
            var claimedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new List<string>();

            // Keys Windows was never asked for, because they are not the sort of
            // thing it will take. Kept apart from `taken` for the same reason
            // duplicates are: a bare letter is refused here, by Shortcut
            // .CanRegister, and reporting that as "another application already
            // owns it" sends somebody hunting for a program that does not exist.
            // Preferences will not offer one, but settings.json is a file people
            // are meant to be able to edit and Settings.Validated deliberately
            // leaves shortcuts alone.
            var unusable = new List<string>();

            // Combinations the application will not hand to Windows whatever the
            // settings file says. A global registration is exclusive machine-wide,
            // so a shortcut of Ctrl+C would take copying away from every program
            // on the computer — this one included, whose own Copy would then never
            // see the key. Preferences refuses these as they are typed;
            // settings.json is a text file people are invited to edit, and
            // Settings.Validated deliberately leaves shortcut text alone, so the
            // refusal has to exist here too.
            var reserved = new List<string>();

            HotkeyTrace($"RegisterHotkeys: player={_settings.AudioPlayerEnabled} " +
                        $"window={_settings.GlobalHotkeyEnabled}");

            // An unset window shortcut is not one another application has taken.
            if (_settings.GlobalHotkeyEnabled &&
                new Shortcut(_settings.HotkeyModifiers, _settings.HotkeyKey).IsAssigned)
            {
                var window = new Shortcut(_settings.HotkeyModifiers, _settings.HotkeyKey);
                if (window.IsAssigned) claimedBy[window.ToString()] = "show the window";

                if (window.IsAssigned && ReservedShortcuts.OwnerOf(window) is { } windowOwner)
                    reserved.Add($"{window} (show the window) is {windowOwner}");
                else if (window.IsAssigned && !window.CanRegister)
                    unusable.Add($"{window} (show the window)");
                else if (!_hotkeys.Register(HotkeyManager.MainWindowHotkeyId, window))
                    taken.Add(window.IsAssigned ? window.ToString() + " (show the window)" : "the window shortcut");
                else if (window.IsAssigned) registered.Add(window.ToString());
            }

            if (_settings.AudioPlayerEnabled)
            {
                foreach (var action in AudioActions.All)
                {
                    var shortcut = Shortcut.Parse(action.Get(_settings));

                    // Not set at all is a choice, not a failure. Only something
                    // the user asked for and did not get is worth reporting.
                    if (!shortcut.IsAssigned) continue;

                    var label = action.Label.ToLowerInvariant();
                    var keys = shortcut.ToString();

                    // Compared as text because that is how they are stored and
                    // how Preferences shows them, and because parsing normalises
                    // "ctrl+alt+p" and "Ctrl+Alt+P" to the same thing.
                    if (claimedBy.TryGetValue(keys, out var owner))
                    {
                        duplicates.Add($"{keys} is already used by {owner}, so {label} did not get it.");
                        continue;
                    }
                    claimedBy[keys] = label;

                    // Before anything is offered to Windows, because the whole
                    // problem is that Windows would accept it.
                    if (ReservedShortcuts.OwnerOf(shortcut) is { } spokenFor)
                    {
                        reserved.Add($"{keys} ({label}) is {spokenFor}");
                        continue;
                    }

                    if (!shortcut.CanRegister)
                    {
                        unusable.Add($"{keys} ({label})");
                        continue;
                    }

                    if (!_hotkeys.Register(AudioActions.HotkeyId(action.Action), shortcut))
                        taken.Add($"{keys} ({label})");
                    else registered.Add(keys);
                }
            }

            if (reserved.Count > 0)
                Notify("hotkey.duplicate",
                    string.Join(" ", reserved) +
                    ". A global shortcut is taken from every application on this computer, " +
                    "so these were left unregistered. Pick different keys in Preferences.");

            if (unusable.Count > 0)
                Notify("hotkey.failed",
                    string.Join(", ", unusable) +
                    " cannot be a global shortcut: one needs Control, Alt or the Windows key, or a media key. " +
                    "On its own that key would stop working everywhere else.");

            if (duplicates.Count > 0)
                Notify("hotkey.duplicate",
                    string.Join(" ", duplicates) + " Pick different keys in Preferences.");

            // One dialog for all three, never one each. Two of these posted
            // together arrive about ten milliseconds apart, and the second opens
            // *inside the first one's modal loop* — so they stack, and a screen
            // reader announces only the one on top. Measured with NVDA's own
            // speech log: the second dialog was read out in full, title, body and
            // button, and the first was never announced as a dialog at all. It
            // was still there underneath, waiting to be found by somebody who had
            // no way of knowing it existed.
            if (dialog && (reserved.Count > 0 || duplicates.Count > 0 || unusable.Count > 0))
                ShowShortcutDialog(reserved, duplicates, unusable);

            if (taken.Count > 0)
                Notify("hotkey.failed",
                    "Another application already owns " + string.Join(", ", taken) +
                    ". Pick different keys in Preferences.");

            if (announce && registered.Count > 0)
                Notify("hotkey.registered", string.Join(", ", registered) + " registered");

            HotkeyTrace($"RegisterHotkeys done: registered={registered.Count} " +
                        $"reserved={reserved.Count} unusable={unusable.Count} " +
                        $"duplicate={duplicates.Count} taken={taken.Count}");
        }

        /// <summary>
        /// Shortcut registration, written down as it happens.
        ///
        /// Null unless a harness is watching, exactly like
        /// <see cref="PerfCounters"/>. This
        /// path decides what every global key on the machine does and it reports
        /// its outcomes through notifications and a dialog — which is to say,
        /// through the very things that were not appearing when it needed
        /// investigating. Set EXPLORERNATIVE_HOTKEYLOG to a file path.
        /// </summary>
        private static void HotkeyTrace(string line)
        {
            var path = Environment.GetEnvironmentVariable("EXPLORERNATIVE_HOTKEYLOG");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                System.IO.File.AppendAllText(path,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
            }
            catch
            {
                // A diagnostic that cannot be written is not a reason to stop.
            }
        }

        /// <summary>
        /// Says out loud that a shortcut did not take, in a window that has to be
        /// answered.
        ///
        /// Preferences refuses all three of these as they are typed, so anything
        /// reaching here arrived by editing settings.json — which is the one route
        /// where silence is worst. Somebody who has just hand-edited a file to get
        /// a shortcut they were told they could not have will conclude, from a key
        /// that does nothing, that the edit did not take: and the natural next
        /// move is to try harder, not to look in the status bar. A status line is
        /// gone in four seconds and a tray balloon may never appear at all, and
        /// this is exactly the moment somebody is watching for an answer.
        ///
        /// The duplicate case is the quietest of the three and the most misleading.
        /// One of the two keys works perfectly, so what it looks like is not "my
        /// settings clash" but "this action is broken" — and which of the two
        /// survives depends on the order this walks the list, which is nothing
        /// anybody can see from the file.
        ///
        /// **One dialog, never one per problem.** Two posted together arrive about
        /// ten milliseconds apart and the second opens inside the first's modal
        /// loop, so they stack. Measured with NVDA's speech log: the second was
        /// read out in full — title, body, button — and the first was never
        /// announced as a dialog at all, while still sitting underneath waiting to
        /// be found by somebody with no way of knowing it was there.
        ///
        /// `taken` is deliberately not here. Another application owning a key is
        /// not a mistake in the settings file and is not always true twice running,
        /// so it stays a notification: a modal dialog on every single launch,
        /// because something else on the machine claimed a combination first, is
        /// how a warning teaches people to dismiss warnings.
        ///
        /// Posted rather than shown inline. This runs from the constructor, before
        /// there is a window to own a dialog or a message loop to pump one, and a
        /// modal window put up at that point stops the application appearing at
        /// all until it is dismissed — a file manager that looks like it failed to
        /// start, in answer to a shortcut being wrong.
        /// </summary>
        private void ShowShortcutDialog(
            List<string> reserved, List<string> duplicates, List<string> unusable)
        {
            const string Title = "Some shortcuts were not set";

            var lines = new List<string>();
            lines.AddRange(reserved);
            lines.AddRange(duplicates);
            foreach (var one in unusable)
                lines.Add($"{one} has no Control, Alt or Windows key.");

            // Only the reasons that apply. A paragraph about keys being used
            // twice, in a dialog raised because a key was a function key, is one
            // more thing to listen through before reaching the part that is
            // about you.
            var why = new List<string>();

            if (reserved.Count > 0)
                why.Add("A global shortcut is taken from every application on this computer, " +
                        "this one included. Setting one of these would stop that key doing its " +
                        "usual job everywhere — and nothing would warn you, because as far as " +
                        "Windows is concerned nothing went wrong.");

            if (duplicates.Count > 0)
                why.Add("Windows gives a combination to one registration only, so the second " +
                        "action never receives the key at all. Which one keeps it is decided " +
                        "by the order the settings are read, not by anything you can see.");

            if (unusable.Count > 0)
                why.Add("A global shortcut needs Control, Alt or the Windows key, or else a media key. " +
                        "On its own, a letter would stop typing that letter everywhere else.");

            var body =
                (lines.Count == 1
                    ? "This shortcut was not registered:"
                    : "These shortcuts were not registered:") +
                Environment.NewLine + Environment.NewLine +
                "    " + string.Join(Environment.NewLine + "    ", lines) +
                Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine + Environment.NewLine, why) +
                Environment.NewLine + Environment.NewLine +
                "Everything else in the settings file was applied. To choose different " +
                "keys, open Preferences and go to Shortcuts.";

            HotkeyTrace($"dialog queued: {lines.Count} lines " +
                        $"(reserved={reserved.Count} duplicate={duplicates.Count} unusable={unusable.Count})");

            PostUi(() =>
            {
                try
                {
                    HotkeyTrace("dialog delegate running");
                    var owner = _form is { IsDisposed: false, Visible: true } ? _form : null;
                    if (owner != null)
                        MessageBox.Show(owner, body, Title,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else
                        MessageBox.Show(body, Title,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    HotkeyTrace("dialog dismissed");
                }
                catch (Exception ex)
                {
                    // A dialog that cannot be shown must not take the startup it
                    // was reporting on down with it. The notification above has
                    // already said the same thing.
                    HotkeyTrace("dialog threw: " + ex);
                }
            });
        }

        private void OnHotkey(int id)
        {
            // Any hotkey press ends the previous key's repeat, including this
            // one's own — a second press is a new hold, not a continuation.
            _hold.Stop();

            if (id == HotkeyManager.MainWindowHotkeyId) { ToggleWindow(); return; }

            var action = AudioActions.ByHotkeyId(id);
            if (action == null) return;

            RunAudioAction(action.Action);

            // Every hotkey is registered with MOD_NOREPEAT, so this press is the
            // only message Windows will send. Holding the key repeats from here.
            if (action.AllowRepeat)
            {
                _hold.Start(
                    Shortcut.Parse(action.Get(_settings)),
                    action.HoldDelayMilliseconds > 0
                        ? action.HoldDelayMilliseconds
                        : HoldRepeat.Delay,
                    action.RepeatMilliseconds > 0
                        ? action.RepeatMilliseconds
                        : HoldRepeat.Interval,
                    repeat => RunAudioAction(action.Action, repeat));
            }
        }

        /// <summary>
        /// One step of a held key, grown by how long it has been held — with the
        /// ceiling taken from the action's own entry, so the list stays the one
        /// place that says how an action behaves.
        /// </summary>
        private int Step(AudioAction action, int step, int repeat)
        {
            // Always accelerating. It was a preference, and without it the
            // repeat rate is beside the point — one percent of volume fifty
            // times a second still takes two seconds to cross the dial. A tap is
            // exactly one step whatever this does, so there was never anything
            // for the switch to protect.
            var info = AudioActions.For(action);
            return AudioActions.Accelerated(step, repeat, accelerate: true,
                info.AccelerationCap, info.AccelerationEvery);
        }

        public void RunAudioAction(AudioAction action) => RunAudioAction(action, 0);

        /// <summary>
        /// <paramref name="repeat"/> is 0 for a press and counts up while the key
        /// is held, so the steps that should accelerate can.
        /// </summary>
        public void RunAudioAction(AudioAction action, int repeat)
        {
            // The dialogs are modal, and global shortcuts still arrive while one
            // is up — so pressing a dialog's key again opened a second on top,
            // and cancelling the outer one then put back values the inner one
            // had just applied and saved. One at a time.
            //
            // And they do not claim the player's messages. A command's thread is
            // marked so the message it causes can replace its generic one; held
            // for the whole of a dialog, that mark swallowed everything the player
            // said in the meantime but the last.
            if (action is AudioAction.SpeedDialog or AudioAction.DeviceDialog or AudioAction.LimiterDialog
                or AudioAction.EqualiserDialog or AudioAction.PitchDialog or AudioAction.SilenceDialog)
            {
                if (_audioDialogOpen) return;
                if (_form?.PreferencesOpen == true)
                {
                    Notify("error.generic", "Close Preferences first");
                    return;
                }
                _audioDialogOpen = true;
                ExplorerNative.MainForm.AudioDialogOpen = true;
                try { RunAudioActionCore(action, repeat); }
                finally
                {
                    _audioDialogOpen = false;
                    ExplorerNative.MainForm.AudioDialogOpen = false;

                    // Whatever was held back while the dialog was up — a reload,
                    // a track started — goes to the player now.
                    if (_audioApplyPending)
                    {
                        _audioApplyPending = false;
                        ApplyAudioSettings();
                    }
                }
                return;
            }

            // Marks this thread as the one whose notifications belong to this
            // command. Restored rather than cleared, because the tray menu can
            // run an action from inside another one.
            int previousThread = _actionThread;
            _actionThread = Environment.CurrentManagedThreadId;
            try { RunAudioActionCore(action, repeat); }
            finally { _actionThread = previousThread; }
        }

        private void RunAudioActionCore(AudioAction action, int repeat)
        {
            string message = action switch
            {
                AudioAction.PlayPause => _audio.TogglePlayPause(),
                AudioAction.Stop => _audio.Stop(),
                AudioAction.VolumeUp => ChangeVolume(Step(action, VolumeStepPercent, repeat)),
                AudioAction.VolumeDown => ChangeVolume(-Step(action, VolumeStepPercent, repeat)),
                AudioAction.Mute => _audio.ToggleMute(),
                AudioAction.SeekBackward => _audio.Seek(-Step(action, _settings.AudioSeekSeconds, repeat)),
                AudioAction.SeekForward => _audio.Seek(Step(action, _settings.AudioSeekSeconds, repeat)),
                AudioAction.SeekBackwardLong => _audio.Seek(-Step(action, _settings.AudioLongSeekSeconds, repeat)),
                AudioAction.SeekForwardLong => _audio.Seek(Step(action, _settings.AudioLongSeekSeconds, repeat)),
                AudioAction.RestartTrack => _audio.SeekToFraction(0),
                AudioAction.SpeedUp => StepSpeed(1),
                AudioAction.SpeedDown => StepSpeed(-1),
                AudioAction.ToggleRepeat => ToggleRepeat(),
                AudioAction.WhatIsPlaying => _audio.Describe(),
                AudioAction.SayElapsed => _audio.DescribeElapsed(),
                AudioAction.SayRemaining => _audio.DescribeRemaining(),
                AudioAction.SayTotal => _audio.DescribeTotal(),
                AudioAction.SpeedDialog => ShowSpeedDialog(),
                AudioAction.DeviceDialog => ShowDeviceDialog(),
                AudioAction.LimiterDialog => ShowLimiterDialog(),
                AudioAction.EqualiserDialog => ShowEqualiserDialog(),
                AudioAction.PitchDialog => ShowPitchDialog(),
                AudioAction.SilenceDialog => ShowSilenceDialog(),
                _ => "",
            };

            // "Nothing is loaded" is an answer to a command that could not run,
            // not chatter about one that did — it is said whatever the category
            // is switched to, or the key just appears to be broken.
            bool refusal = message.StartsWith("Nothing is", StringComparison.Ordinal);
            var info = AudioActions.For(action);

            // The player may have named this event more precisely on the way
            // past — "volume at maximum" rather than "volume up", "the format
            // will not go faster" rather than "speed down". That finer name
            // replaces the action's generic one; announcing both would say the
            // same thing twice, once under each id.
            var (finerId, finerMessage) = TakePlayerNotification();

            if (finerId != null && finerMessage == message)
            {
                SayAudio(finerId, message, speak: refusal || WantsToHear(info.Say));
                return;
            }

            SayAudio(info.NotificationId, message, speak: refusal || WantsToHear(info.Say));

            // A different sentence is a different event that happened to land
            // during this one — a fade starting, a track finishing — so it is
            // delivered on its own account rather than dropped.
            //
            // Once per hold, by id. Held at the end of a ladder, "Already at 400
            // percent" was raised on every repeat and started itself over eight
            // times a second — and a hold is also when most of these happen, so
            // they are not simply dropped after the first press.
            if (repeat == 0) _saidThisHold.Clear();
            if (finerId != null && !string.IsNullOrEmpty(finerMessage) && _saidThisHold.Add(finerId))
                Notify(finerId, finerMessage!);
        }

        /// <summary>The separate messages already delivered during the current hold.</summary>
        private readonly HashSet<string> _saidThisHold = new(StringComparer.Ordinal);

        /// <summary>
        /// The thread currently running a key-driven audio action, or -1.
        ///
        /// The player raises its notifications on whatever thread the event
        /// happened on. One raised synchronously by the command we just called is
        /// a finer name for that command's own answer and must replace it; one
        /// arriving from a Media Foundation worker is a separate event and must
        /// be delivered. Comparing the thread is what tells them apart — a flag
        /// alone would capture a worker's event whenever a key press happened to
        /// be in flight.
        /// </summary>
        private int _actionThread = -1;

        /// <summary>Whether one of the audio dialogs is showing. See RunAudioAction.</summary>
        private bool _audioDialogOpen;
        private bool _audioApplyPending;
        private string? _pendingPlayerId;
        private string? _pendingPlayerMessage;

        private (string? Id, string? Message) TakePlayerNotification()
        {
            var result = (_pendingPlayerId, _pendingPlayerMessage);
            _pendingPlayerId = null;
            _pendingPlayerMessage = null;
            return result;
        }

        /// <summary>
        /// Everything the player, the tag reader and the read-ahead have to say.
        /// Held back only while the command that caused it is still deciding what
        /// to announce; otherwise delivered straight away, on the UI thread.
        /// </summary>
        private void OnPlayerNotification(string id, string message)
        {
            if (Environment.CurrentManagedThreadId == _actionThread)
            {
                _pendingPlayerId = id;
                _pendingPlayerMessage = message;
                return;
            }

            PostUi(() => Notify(id, message));
        }

        private bool WantsToHear(AudioSay category) => category switch
        {
            AudioSay.Transport => _settings.AudioAnnounceTransport,
            AudioSay.Volume => _settings.AudioAnnounceVolume,
            AudioSay.Seek => _settings.AudioAnnounceSeek,
            _ => true,
        };

        private string StepSpeed(int direction)
        {
            var message = _audio.StepSpeed(direction);
            _settings.AudioPlaybackRatePercent = _audio.PlaybackRatePercent;
            return message;
        }

        /// <summary>
        /// Opens the speed list, which applies each speed as it is arrowed onto.
        ///
        /// Returns nothing to announce: the dialog is the feedback, and a screen
        /// reader reads the list entry on every press anyway. The chosen speed is
        /// written back to the settings whichever way the dialog is dismissed,
        /// because cancelling puts the old one back and that is a choice too.
        /// </summary>
        private string ShowSpeedDialog()
        {
            using var dialog = new SpeedForm(
                _audio.PlaybackRatePercent,
                percent => _audio.PlaybackRatePercent = percent);

            if (_form is { IsDisposed: false, Visible: true }) dialog.ShowDialog(_form);
            else dialog.ShowDialog();

            _settings.AudioPlaybackRatePercent = _audio.PlaybackRatePercent;

            // Anything modal leaves the keyboard on the form, where no key does
            // anything. Put it back in the list.
            _form?.RestoreListFocusExternally();
            return "";
        }

        /// <summary>
        /// Opens the output device list, which moves the audio as it is arrowed
        /// onto. Same shape as the speed dialog and for the same reason.
        /// </summary>
        private string ShowDeviceDialog()
        {
            using var dialog = new DeviceForm(
                _audio.OutputDeviceId,
                AudioPlayer.OutputDevices(),
                id => _audio.OutputDeviceId = id);

            var outcome = _form is { IsDisposed: false, Visible: true }
                ? dialog.ShowDialog(_form)
                : dialog.ShowDialog();

            // OK saves what the dialog chose, not what the player happens to hold.
            // They differ exactly when the saved device has been unplugged: the
            // list shows "Follow the system default", nothing was arrowed, and the
            // player still names the missing device — so OK kept it pinned there.
            if (outcome == DialogResult.OK) _audio.OutputDeviceId = dialog.SelectedId;
            _settings.AudioOutputDeviceId = _audio.OutputDeviceId;

            _form?.RestoreListFocusExternally();
            return "";
        }

        /// <summary>
        /// Opens the limiter's three controls, which take effect on the arrow
        /// press. Same shape again, and here the shape is the whole point: an
        /// attack time means nothing except against music that is playing.
        ///
        /// The limiter is switched on for the duration if it was not already.
        /// Adjusting an effect that is switched off is three lists that do
        /// nothing, and this dialog is only reachable while it is on — but a
        /// shortcut is a shortcut and can be pressed at any moment. Switched
        /// back on the way out, unless OK was pressed.
        /// </summary>
        private string ShowLimiterDialog()
        {
            // Not switched on for the duration any more. The switch is a control
            // in the dialog now rather than a tick box on a preferences page, so
            // there is nothing to work around: it opens showing whatever the
            // limiter is actually set to, and turning it on is one press of a
            // box that is right there.
            using var dialog = new LimiterForm(
                _audio.LimiterAttackMicroseconds,
                _audio.LimiterReleaseMilliseconds,
                _audio.LimiterCeilingPercent,
                _audio.LimiterWholeRange,
                _audio.Limiter,
                (attack, release, ceiling, wholeRange, on) =>
                {
                    _audio.LimiterAttackMicroseconds = attack;
                    _audio.LimiterReleaseMilliseconds = release;
                    _audio.LimiterCeilingPercent = ceiling;
                    _audio.LimiterWholeRange = wholeRange;
                    _audio.Limiter = on;
                });

            var answer = _form is { IsDisposed: false, Visible: true }
                ? dialog.ShowDialog(_form)
                : dialog.ShowDialog();

            if (answer == DialogResult.OK)
            {
                _settings.AudioLimiter = _audio.Limiter;
                _settings.AudioLimiterAttackMicroseconds = _audio.LimiterAttackMicroseconds;
                _settings.AudioLimiterReleaseMilliseconds = _audio.LimiterReleaseMilliseconds;
                _settings.AudioLimiterCeilingPercent = _audio.LimiterCeilingPercent;
                _settings.AudioLimiterWholeRange = _audio.LimiterWholeRange;
                _settings.Save();
            }

            // Nothing to undo on the other branch: the form has already put all
            // five back through the apply callback.

            _form?.RestoreListFocusExternally();
            return "";
        }

        /// <summary>
        /// Opens silence reduction, whose six controls land on the sound as
        /// they are moved.
        ///
        /// Same shape as the limiter and equaliser dialogs beside it, and it
        /// opens showing whatever it is actually set to rather than
        /// switching anything on behind your back — the switch is a tick box
        /// inside the dialog, one press away.
        /// </summary>
        private string ShowSilenceDialog()
        {
            using var dialog = new SilenceForm(
                _audio.SilenceThresholdDecibels,
                _audio.SilenceMinimumMilliseconds,
                _audio.SilenceLeaveMilliseconds,
                _audio.SilenceFadeOutMilliseconds,
                _audio.SilenceFadeInMilliseconds,
                _audio.SilenceReduction,
                (threshold, minimum, leave, fadeOut, fadeIn, on) =>
                {
                    _audio.SilenceThresholdDecibels = threshold;
                    _audio.SilenceMinimumMilliseconds = minimum;
                    _audio.SilenceLeaveMilliseconds = leave;
                    _audio.SilenceFadeOutMilliseconds = fadeOut;
                    _audio.SilenceFadeInMilliseconds = fadeIn;
                    _audio.SilenceReduction = on;
                })
            {
                MaySpeak = () => _settings.SpeakEnabled,
            };

            var answer = _form is { IsDisposed: false, Visible: true }
                ? dialog.ShowDialog(_form)
                : dialog.ShowDialog();

            if (answer == DialogResult.OK)
            {
                _settings.AudioSilenceReduction = _audio.SilenceReduction;
                _settings.AudioSilenceThresholdDecibels = _audio.SilenceThresholdDecibels;
                _settings.AudioSilenceMinimumMilliseconds = _audio.SilenceMinimumMilliseconds;
                _settings.AudioSilenceLeaveMilliseconds = _audio.SilenceLeaveMilliseconds;
                _settings.AudioSilenceFadeOutMilliseconds = _audio.SilenceFadeOutMilliseconds;
                _settings.AudioSilenceFadeInMilliseconds = _audio.SilenceFadeInMilliseconds;
                _settings.Save();
            }

            // Nothing to undo on the other branch: the form has already put all
            // six back through the apply callback.

            _form?.RestoreListFocusExternally();
            return "";
        }

        /// <summary>
        /// Opens the twenty bands, which land on the sound as they are moved.
        ///
        /// It opens showing whatever the equaliser is actually set to, and does
        /// not switch it on behind your back. An earlier version did — the same
        /// trick the limiter used to need, from when its switch lived on a
        /// preferences page — and it is wrong here for the reason it was wrong
        /// there: the switch is a control in this dialog, so there is nothing to
        /// work around, and a window that silently changes the sound on the way
        /// up is a window you cannot use to find out what the sound was.
        /// </summary>
        private string ShowEqualiserDialog()
        {
            using var dialog = new EqualiserForm(
                _audio.EqualiserGains,
                _audio.Equaliser,
                (band, decibels) => _audio.SetEqualiserBand(band, decibels),
                on => _audio.Equaliser = on);

            var answer = _form is { IsDisposed: false, Visible: true }
                ? dialog.ShowDialog(_form)
                : dialog.ShowDialog();

            if (answer == DialogResult.OK)
            {
                _settings.AudioEqualiser = _audio.Equaliser;
                _settings.AudioEqualiserBands = _audio.EqualiserGains;
                _settings.Save();
            }

            // Nothing to undo on the other branch: the form has already put
            // every band and the switch back through the apply callbacks.

            _form?.RestoreListFocusExternally();
            return "";
        }

        /// <summary>
        /// Opens the pitch control, which lands on the sound as it is arrowed.
        ///
        /// Same shape as the speed, limiter and equaliser dialogs, and it opens
        /// showing what the pitch actually is rather than switching anything on
        /// — there is nothing to switch on. Zero semitones is already a bypass.
        /// </summary>
        private string ShowPitchDialog()
        {
            using var dialog = new PitchForm(
                _audio.PitchSemitones,
                _audio.PitchMovesTempo,
                (semitones, movesTempo) =>
                {
                    _audio.PitchSemitones = semitones;
                    _audio.PitchMovesTempo = movesTempo;
                });

            var answer = _form is { IsDisposed: false, Visible: true }
                ? dialog.ShowDialog(_form)
                : dialog.ShowDialog();

            if (answer == DialogResult.OK)
            {
                _settings.AudioPitchSemitones = _audio.PitchSemitones;
                _settings.AudioPitchMovesTempo = _audio.PitchMovesTempo;
                _settings.Save();
            }

            // Nothing to undo otherwise: the form has already put both back
            // through the apply callback.

            _form?.RestoreListFocusExternally();
            return "";
        }

        private string ToggleRepeat()
        {
            var message = _audio.ToggleRepeat();
            _settings.AudioRepeatTrack = _audio.RepeatTrack;
            return message;
        }

        public string AudioDiagnostic => _audio.Diagnostic;

        /// <summary>
        /// Which way the sound is actually leaving, and whether the effects
        /// bypass was granted.
        ///
        /// Two of the settings on that page are requests rather than
        /// instructions — the device is allowed to refuse raw mode, and the
        /// direct path stands aside for the media engine whenever the speed is
        /// not normal — so a tick box is not an answer to "is it doing it".
        /// A setting whose effect cannot be seen is one nobody can tell is
        /// broken, which this codebase has now shipped twice.
        /// </summary>
        public string AudioOutputDiagnostic => _audio.OutputDiagnostic;

        /// <summary>Whether the limiter is on, and how much is clipping.</summary>
        public string AudioLevelDiagnostic => _audio.DescribeLevel();

        /// <summary>What the Audio menu needs to label itself.</summary>
        public AudioSnapshot AudioState => new(
            _audio.HasTrack, _audio.IsPlaying, _audio.IsMuted, _audio.RepeatTrack,
            _audio.NowPlayingPath, _audio.DisplayName);

        /// <summary>
        /// The level is kept in the settings object the window also holds, so it
        /// is written out by whichever of them saves last. Writing settings.json
        /// on every press of the volume key is a file write per keystroke for a
        /// value nobody needs until the next launch.
        /// </summary>
        private string ChangeVolume(int delta)
        {
            var message = _audio.ChangeVolume(delta);
            _settings.AudioVolumePercent = _audio.VolumePercent;
            return message;
        }

        /// <summary>
        /// Audio feedback goes to the window when there is one, so the status bar
        /// agrees with what happened, and straight to NVDA when there is not —
        /// which is most of the time, this being a player with no window.
        ///
        /// Showing and speaking are separate. A shortcut that is set not to talk
        /// still updates the status bar: writing a line nobody has to listen to
        /// costs nothing, and it is there to be read by anyone who looks.
        ///
        /// The catalogue id supplies the routing and the sentence supplies the
        /// words: the player knows the real volume and the real time, and the
        /// catalogue's own template exists for previewing rather than for saying.
        /// An id set to Off in Preferences is still shown and simply not spoken;
        /// a null id keeps the old behaviour, so nothing goes quiet just because
        /// it has not been catalogued yet.
        ///
        /// It used to return early when the id was set to Off, which contradicts
        /// the paragraph above it: "volume down" is off by default, so holding
        /// the volume key moved the level and left the status bar reading
        /// whatever it said before — the one place a sighted user could have
        /// checked what the level actually was.
        /// </summary>
        private void SayAudio(string? notificationId, string? message, bool speak = true)
        {
            if (string.IsNullOrEmpty(message)) return;

            if (!string.IsNullOrEmpty(notificationId))
            {
                // Speaking is the union of the two: the old per-category switches
                // still silence a whole group, and the catalogue can silence one
                // message inside a group that is otherwise on.
                speak = speak &&
                    Notifications.ChannelsFor(notificationId, _settings) == NotificationChannel.Speech;
            }

            if (_form is { IsDisposed: false, Visible: true })
            {
                _form.AnnounceAudio(message, speak);
                return;
            }

            if (speak && _settings.SpeakEnabled) Speech.Speak(message, _settings.InterruptSpeech);
        }

        /// <summary>
        /// Gets the player ready before anyone asks it for anything.
        ///
        /// Media Foundation is started on a worker, because loading it is a dozen
        /// DLLs and none of that needs this thread. The engine itself is built on
        /// the UI thread — it is the thread that will drive it, and an engine
        /// created anywhere else has to be marshalled to from here on every
        /// single call.
        ///
        /// Posted rather than called, so it runs once the window is up. This is
        /// the shell's handler for every folder on the machine and it is started
        /// afresh on every folder open; a hundred milliseconds spent here would
        /// be a hundred milliseconds before the folder appears.
        /// </summary>
        private void WarmAudio()
        {
            AudioPlayer.Prewarm();

            PostUi(() =>
            {
                try
                {
                    // The last track, if it is still there, gets Media Foundation
                    // to resolve a source of the right kind. That cost is
                    // per-process, not per-file, so this makes the *next* track
                    // start fast whichever one it turns out to be.
                    var preload = _settings.AudioLastPath;
                    _audio.Warm(string.IsNullOrEmpty(preload) ? null : preload);
                }
                catch { }
            });
        }

        /// <summary>
        /// Pushes every audio preference into the player. Called wherever the
        /// settings can have changed, so there is one place that knows the list.
        /// </summary>
        private void ApplyAudioSettings()
        {
            // An effect dialog is applying values as they are arrowed through,
            // and the saved ones put back underneath it — by a track starting,
            // or a reload — were then what its OK saved.
            if (_audioDialogOpen) { _audioApplyPending = true; return; }

            _audio.OutputDeviceId = _settings.AudioOutputDeviceId;
            _audio.Limiter = _settings.AudioLimiter;
            _audio.LimiterWholeRange = _settings.AudioLimiterWholeRange;
            _audio.LimiterAttackMicroseconds = _settings.AudioLimiterAttackMicroseconds;
            _audio.LimiterReleaseMilliseconds = _settings.AudioLimiterReleaseMilliseconds;
            _audio.LimiterCeilingPercent = _settings.AudioLimiterCeilingPercent;
            _audio.Equaliser = _settings.AudioEqualiser;
            _audio.EqualiserGains = _settings.AudioEqualiserBands;
            _audio.SilenceThresholdDecibels = _settings.AudioSilenceThresholdDecibels;
            _audio.SilenceMinimumMilliseconds = _settings.AudioSilenceMinimumMilliseconds;
            _audio.SilenceLeaveMilliseconds = _settings.AudioSilenceLeaveMilliseconds;
            _audio.SilenceFadeOutMilliseconds = _settings.AudioSilenceFadeOutMilliseconds;
            _audio.SilenceFadeInMilliseconds = _settings.AudioSilenceFadeInMilliseconds;
            _audio.SilenceReduction = _settings.AudioSilenceReduction;
            _audio.PitchSemitones = _settings.AudioPitchSemitones;
            _audio.PitchMovesTempo = _settings.AudioPitchMovesTempo;
            _audio.ReleaseAfter = TimeSpan.FromMinutes(_settings.AudioReleaseAfterMinutes);

            // PreferRawOutput, StreamWhilePlaying and CacheBytes are not pushed
            // in any more. All three had a preference behind them and all three
            // had one right answer — see the notes where those settings used to
            // be in Settings.cs — so the player's own defaults are the whole of
            // it now.

            _audio.VolumePercent = _settings.AudioVolumePercent;
            _audio.Curve = VolumeCurve.Perceptual;
            _audio.FadeInMilliseconds = _settings.AudioFadeInMilliseconds;
            _audio.FadeOutMilliseconds = _settings.AudioFadeOutMilliseconds;
            _audio.RewindOnResumeSeconds = _settings.AudioRewindOnResumeSeconds;
            _audio.PlaybackRatePercent = _settings.AudioPlaybackRatePercent;
            _audio.RepeatTrack = _settings.AudioRepeatTrack;
        }

        /// <summary>
        /// Starts a file in the built-in player. Returns false if the player is
        /// switched off or unavailable — the caller then opens the file the
        /// ordinary way rather than leaving the user with silence.
        ///
        /// Nothing is announced unless the preference asks for it. Pressing Enter
        /// on a track should play the track; the row was read aloud a moment ago
        /// and the music starting says the rest.
        /// </summary>
        /// <summary>
        /// Starts a file in the built-in player, without waiting for it.
        ///
        /// The waiting is the whole point of this method's shape. Opening a
        /// track means opening a decoder, and opening a decoder on a cloud or
        /// network file means Media Foundation reading a header through a
        /// connection — measured here at 800ms to 2.7 seconds for a cold track
        /// on the Drive letter, and `WasapiPlayback.Start` will wait fifteen
        /// seconds before giving up on one. All of that used to happen on the UI
        /// thread, because Enter called straight through: press Enter on a song
        /// and the window stopped repainting until it began. That is the whole
        /// of "the app lags trying to load an audio file", and it is the same
        /// rule this codebase already applies to enumeration, folder sizing and
        /// existence checks — it had simply never been applied to the one thing
        /// that is guaranteed to touch the network.
        ///
        /// So this answers the only question the caller genuinely needs
        /// synchronously — would the player take this file at all — and does the
        /// opening on a worker. True means "it is being dealt with here", and
        /// the fallback to whatever else owns the extension moves in here with
        /// it, because by the time that is known the caller has long returned.
        /// </summary>
        public bool PlayAudioFile(string path)
        {
            if (!_settings.AudioPlayerEnabled) return false;

            ApplyAudioSettings();

            // The head of this file is very likely already warm — it is the row
            // the cursor was on — and the player is about to download the rest
            // of it as it plays, so there is nothing left for that one to do.
            _browsePrefetch.Cancel();

            // Which Enter this is. Pressing Enter on three tracks in a row used
            // to be serialised by the UI thread simply because it was the UI
            // thread doing the work; off it, three workers would race to start
            // three tracks on one player. The newest press wins and the older
            // ones give up at the gate rather than queueing — a track nobody is
            // waiting for any more should not be opened at all, let alone opened
            // and then immediately disposed by the next one.
            int mine = System.Threading.Interlocked.Increment(ref _playRequest);

            // The track still opening is not wanted any more either.
            _audio.CancelOpening();

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string? message;

                lock (_playGate)
                {
                    if (System.Threading.Volatile.Read(ref _playRequest) != mine) return;

                    // From the beginning, always. Enter on a track means play
                    // that track, not resume something you were doing an hour
                    // ago.
                    try { message = _audio.Play(path); }
                    catch { message = null; }
                }

                // Nothing here could open it. The player has raised Failed,
                // and OnTrackFailed hands the file to whichever application owns
                // the extension — once. This used to open it as well, so a file
                // the player could not decode opened twice.
                if (message == null) return;

                // Stopped before it opened: nothing to say or remember.
                if (message.Length == 0) return;

                // Back to the thread that owns the settings and the window
                // before touching either.
                PostUi(() =>
                {
                    _settings.AudioLastPath = path;
                    BeginReadingTitle(path);
                    if (_settings.AudioAnnounceStart) SayAudio("audio.play", message);
                });
            });

            return true;
        }

        /// <summary>Which Enter is the current one, and the gate that lets one through.</summary>
        private int _playRequest;
        private readonly object _playGate = new();

        /// <summary>
        /// Warms the file the cursor has settled on, so that pressing Enter does
        /// not begin by waiting for a network.
        ///
        /// Only the head of it: enough for the engine to open the file and start
        /// decoding, which measured on a NAS is the difference between about 1.3
        /// seconds and about 0.6. Reading the whole thing on the off chance would
        /// be minutes of traffic for a folder somebody is only arrowing through.
        /// </summary>
        public void PrefetchAudioFile(string path)
        {
            if (!_settings.AudioPlayerEnabled || !_settings.PlayAudioInApp) return;
            if (_settings.AudioPrefetchKilobytes <= 0) return;

            // A row that is not a track lets go of the one being readied.
            if (!AudioFiles.IsAudio(path, _settings.AudioExtensions))
            {
                // Inline, so it keeps its place ahead of the next row's Prepare;
                // letting go of a download is a flag and a closed handle.
                try { _audio.CancelPrepare(); } catch { }
                return;
            }

            // Two different things, and only one of them keeps what it reads.
            //
            // For a track that will play through a download, the download itself
            // is started here — so by the time Enter is pressed the head of the
            // file is already in memory rather than needing a fresh network read
            // per header field. Measured on a cold Drive track: 2.7 seconds to
            // open a decoder against 186ms once the bytes were held.
            //
            // The plain read-ahead stays for everything else it covers: a file
            // on a share that will not be streamed, and warming the operating
            // system's own cache. Network drives only — on a local disk it buys
            // nothing worth the reading and competes with everything else that
            // wants the disk, which was measured rather than assumed.
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { _audio.Prepare(path); } catch { }
            });

            // Not for a track on the Drive letter: Prepare is already
            // downloading it from Google, and reading the letter as well started
            // a second download of the same file.
            if (_drive.Owns(path)) return;

            _browsePrefetch.Begin(path,
                _settings.AudioPrefetchKilobytes * 1024L,
                remoteOnly: true);
        }

        /// <summary>
        /// Fetches the Title tag on a worker and hands it to the player to use as
        /// the track's spoken name.
        ///
        /// On a worker because the property store opens the file, and the file
        /// can be on a share whose server has gone away — the exact case the rest
        /// of this application goes to such lengths to keep off the UI thread.
        /// </summary>
        private void BeginReadingTitle(string path)
        {
            if (!_settings.AudioAnnounceUsesTitleTag) return;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var title = AudioTags.TitleOf(path);
                if (title != null) _audio.SetDisplayName(path, title);
            });
        }

        private void OnTrackFinished(string path)
        {
            if (_settings.AudioAnnounceTrackEnd)
                SayAudio("audio.ended", "Finished " + _audio.DisplayName);

            if (CdTrackStream.IsCdaName(System.IO.Path.GetExtension(path)))
                PlayNextCdTrack(path);
        }

        /// <summary>
        /// A CD plays through, where a folder of files does not: the disc is an
        /// album and the tracks on it are one recording cut into pieces.
        ///
        /// The listing is read on a worker because it is the disc, and the next
        /// track only starts if nothing else has in the meantime — Enter on
        /// another file, or Stop, is somebody saying they are done with the CD.
        /// </summary>
        private void PlayNextCdTrack(string finished)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                var next = CdTrackStream.NextTrack(finished);
                if (next == null) return;

                PostUi(() =>
                {
                    if (!string.Equals(_audio.NowPlayingPath, finished, StringComparison.OrdinalIgnoreCase)) return;
                    PlayAudioFile(next);
                });
            });
        }

        /// <summary>
        /// Windows had no decoder for it. The file goes to whichever application
        /// owns the extension, which is where it would have gone had the player
        /// not been switched on — a wrong guess in the extension list costs a
        /// moment, not a file that will not open.
        /// </summary>
        private void OnTrackFailed(string path)
        {
            SayAudio("audio.handedover", $"Cannot play {System.IO.Path.GetFileName(path)}, opening it instead");
            // Off this thread: opening it resolves the folder and may start a
            // process there, and the track failed because that share went away.
            System.Threading.Tasks.Task.Run(() => { try { FileLauncher.Open(path); } catch { } });
        }

        /// <summary>
        /// Folders accepted from other launches after quitting had begun. The
        /// launcher has already exited believing they were taken, so they are
        /// opened by a fresh copy once this one has gone.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _unserved = new();

        private static bool IsFolderRequest(string request) =>
            request != SingleInstance.ReloadSettingsCommand &&
            request != SingleInstance.ReleaseFoldersCommand &&
            request != SingleInstance.ReleaseAllCommand &&
            request != SingleInstance.ExitCommand &&
            request != SingleInstance.ShowCommand;

        private static bool IsSettingsRequest(string request) =>
            request == SingleInstance.ReloadSettingsCommand ||
            request == SingleInstance.ReleaseFoldersCommand ||
            request == SingleInstance.ReleaseAllCommand;

        /// <summary>
        /// The settings as a command-line request leaves them: what the file now
        /// says, and for a recovery flag its change applied whatever the file
        /// says — the file may be unreadable, and the flag must hold regardless.
        /// </summary>
        private Settings SettingsAfter(string request, Settings live)
        {
            // Remembered while the file cannot be read, and laid over it when it
            // can: a release is the default value, so a merge that only carries
            // what differs from the defaults would lose it.
            bool release = request == SingleInstance.ReleaseFoldersCommand || request == SingleInstance.ReleaseAllCommand;
            if (live.UnreadableOnDisk && release)
            {
                _pendingRelease |= request == SingleInstance.ReleaseAllCommand
                    ? Released.Folders | Released.ContextMenu | Released.Startup
                    : Released.Folders;
                _releasedAt = DateTime.UtcNow.Ticks;
            }

            var merged = Settings.ReloadOnto(live);
            if (release) Release(merged, request);

            // The file reads again: the waiting releases are carried by this
            // merge, written down, and done with. Left waiting, every later quit
            // applied them over whatever was chosen since — a takeover switched
            // back on was handed back again on the way out.
            if (!merged.UnreadableOnDisk && _pendingRelease != Released.None)
            {
                ApplyPendingReleases(merged);
                merged.Save();
                _pendingRelease = Released.None;
            }
            return merged;
        }

        /// <summary>What a recovery flag changes.</summary>
        private static void Release(Settings settings, string request)
        {
            settings.SetAsDefaultFileExplorer = false;
            if (request == SingleInstance.ReleaseAllCommand)
            {
                settings.RegisterContextMenu = false;
                settings.StartWithWindows = false;
            }
        }

        /// <summary>
        /// A folder handed over by a second launch. Called on the listener thread.
        /// </summary>
        public void HandleExternalRequest(string request)
        {
            if (_quitting)
            {
                if (IsFolderRequest(request) || request == SingleInstance.ShowCommand) _unserved.Enqueue(request);
                else if (IsSettingsRequest(request)) _settingsWhileQuitting.Enqueue(request);
                return;
            }

            // Not PostUi, which drops work once quitting has begun. What is in
            // the inbox when quitting ends is opened by the copy that comes
            // after this one — a posted message still queued then is never run.
            _inbox.Enqueue(request);
            try { _ui.Post(_ => DrainInbox(), null); } catch { }
        }

        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _inbox = new();

        /// <summary>
        /// Command-line settings changes that arrived while quitting. Taken on
        /// before the last save, or that save put the old values back — and the
        /// next launch undid, say, a --register-default made a moment earlier.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _settingsWhileQuitting = new();

        /// <summary>
        /// The live settings with every settings request still waiting — in the
        /// inbox, or turned away while quitting — and a plain reload regardless,
        /// which merges only what differs and keeps the live values when the file
        /// cannot be read.
        /// </summary>
        private Settings WithWaitingSettings(Settings live)
        {
            var merged = Settings.ReloadOnto(live);
            foreach (var request in System.Linq.Enumerable.Concat(System.Linq.Enumerable.Where(_inbox, IsSettingsRequest), _settingsWhileQuitting))
                merged = SettingsAfter(request, merged);

            // And the releases made while the file could not be read, which a
            // merge against the defaults cannot carry — whether or not the retry
            // got as far as taking the file on.
            ApplyPendingReleases(merged);
            return merged;
        }

        private void DrainInbox()
        {
            while (!_quitting && _inbox.TryDequeue(out var request)) Serve(request);
        }

        private void Serve(string request)
        {
            {
                try
                {
                    // A settings change made from the command line must not also
                    // put a window on screen — the point of those flags is that
                    // they work without one.
                    if (IsSettingsRequest(request))
                    {
                        ApplySettings(SettingsAfter(request, _settings), reload: true);
                        return;
                    }

                    if (request == SingleInstance.ExitCommand)
                    {
                        Quit();
                        return;
                    }

                    ShowWindow();
                    if (request != SingleInstance.ShowCommand)
                    {
                        _form?.ShowFolderFromOutside(request);

                        // Once this is the default handler, every double-click on
                        // a folder anywhere in Windows comes through here: a
                        // second copy started, found the lock taken, and posted
                        // its folder over rather than opening a window of its own.
                        var leaf = System.IO.Path.GetFileName(
                            request.TrimEnd(System.IO.Path.DirectorySeparatorChar));
                        Notify("instance.handoff", "Opened " + (leaf.Length == 0 ? request : leaf));
                    }
                }
                catch (Exception ex)
                {
                    // This used to swallow everything, which from the outside is
                    // a folder that was double-clicked and then simply never
                    // opened — no window, no message, nothing to go on.
                    try { Notify("error.generic", ex.Message); } catch { }
                }
            }
        }

        /// <summary>
        /// Explorer Native Connect: this machine's drives for the web app, through Tailscale Serve only. Drive folders
        /// come from the listing the mount already holds. A Drive file is read straight from Google through the
        /// player's own range source and download window, one shared per path (ConnectStreams), so a browser
        /// scrubbing a track hits memory instead of starting a download per drag; a local file is read as a
        /// file. The file actions are ConnectFiles, the application's own machinery.
        /// </summary>
        private void StartConnect()
        {
            // Not while settings.json could not be read: the code is in that
            // file, and RetrySettings makes one only if it turns out to have none.
            if (_settings.EnsureConnectCode()) _settings.Save();
            _connectStreams = new ConnectStreams(path => _drive.OpenRange(path));
            var streams = _connectStreams;
            // Its own STA thread and message-only window: never the UI thread.
            _connectClipboard = new ConnectClipboard();
            _connectClipboard.Start();
            _connect = new ConnectServer(
                path =>
                {
                    // A Drive folder nobody has opened this session has no listing yet,
                    // and walking its placeholders reads as empty. Populate asks Drive,
                    // and costs nothing when the listing is already held.
                    if (_drive.Owns(path) && !_drive.TryListing(path, out _)) _drive.Populate(path);
                    return _drive.TryListing(path, out var placed)
                        ? placed.Select(e => new ConnectEntry(e.Name, e.IsFolder, e.Size, e.Modified)).ToList()
                        : null;
                },
                path => (_drive.Owns(path) ? streams.Lease(path) : null) ?? new FileRangeSource(path),
                () => _settings.ConnectCode,
                isDrive: root => _drive.Owns(root),
                files: new ConnectFiles(_drive, _settings),
                remote: path => _drive.Owns(path) ? streams.Lease(path) : null,
                clipboard: _connectClipboard,
                port: _settings.WebAppPort);

            // Said out loud on the PC, once per guesser: somebody on the tailnet
            // keeps entering a wrong pairing code, and each try now waits.
            _connect.CodeGuessing += who => PostUi(() => Notify("webapp.guessing",
                $"Someone ({who}) keeps entering a wrong web app pairing code. They are being slowed down."));
            _connect.Start();

            // A saved port another program has taken since: the default for this
            // session only, said once. Not saved: the port chosen is still the one
            // asked for, and it was written over with 47810 for good because some
            // other program happened to hold it at one startup.
            if (_connect.ListenProblem != null && _settings.WebAppPort != ConnectServer.Port)
            {
                int wanted = _settings.WebAppPort;
                if (_connect.Rebind(ConnectServer.Port))
                    Notify("webapp.port.fallback",
                        $"Port {wanted} is in use by another program, so the web app is using port " +
                        $"{ConnectServer.Port} until Explorer Native next starts. Port {wanted} is still your setting");
            }
            if (_connect.ListenProblem != null)
                Notify("webapp.port.failed", "The web app could not start. " + _connect.ListenProblem);
            ReconcileWebApp(_connect.ListenPort);
        }

        /// <summary>
        /// Makes Tailscale Serve match the Web app setting, on a worker: on puts the
        /// web app's handler on 443 if it is not there, off takes only ours away.
        /// Run at start and whenever the setting changes.
        /// </summary>
        private void ReconcileWebApp(int previousPort)
        {
            bool on = _settings.WebAppEnabled;
            int port = _connect?.ListenPort ?? _settings.WebAppPort;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (!TailscaleWeb.Installed) return;
                    var serve = TailscaleWeb.ReadServe(port, previousPort);
                    if (on && serve != TailscaleWeb.ServeState.Ours)
                    {
                        var r = TailscaleWeb.Enable(port, previousPort);
                        if (!r.Ok && r.Problem != null) PostUi(() => Notify("webapp.failed", r.Problem));
                    }
                    else if (!on && serve is TailscaleWeb.ServeState.Ours or TailscaleWeb.ServeState.OursOtherPort)
                        TailscaleWeb.Disable(port, previousPort);
                }
                catch (Exception ex)
                {
                    PostUi(() => Notify("webapp.failed", "Tailscale could not be set up for the web app: " + ex.Message));
                }
            });
        }

        private void ShowWindow()
        {
            // Only a window that had gone away can come back. Asked before
            // anything is shown, and false on the first show of the session —
            // which is a start, not a restore, and happens on the way in where a
            // catalogue lookup does not belong.
            bool restoring = _form is { IsDisposed: false } &&
                             (!_form.Visible || _form.WindowState == FormWindowState.Minimized);

            if (_form == null || _form.IsDisposed)
                _form = new MainForm(_settings) { Drive = _drive };

            if (!_form.Visible) _form.Show();
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;

            _form.Activate();
            _form.BringToFront();

            if (restoring) Notify("app.restored", _settings.DisplayTitle);
        }

        private void ToggleWindow()
        {
            // Pressing the hotkey while it already has focus tucks it away again.
            if (_form is { IsDisposed: false, Visible: true } && _form.ContainsFocus)
            {
                if (_settings.CloseToTray) _form.Hide();
                else _form.WindowState = FormWindowState.Minimized;

                // Said after the window has gone, because that is when it is
                // true — and it is why a status-bar-only setting delivers
                // nothing here: there is no bar left to read it from. The two
                // outcomes are different places, so they are different words.
                Notify("app.minimised", _settings.CloseToTray
                    ? "Minimised to the notification area"
                    : "Minimised");
                return;
            }
            ShowWindow();
        }

        /// <summary>Shuts down without the "hide to tray" behaviour, for Ctrl+R.</summary>
        public void QuitImmediately() => Quit();

        private void Quit(bool ask = false)
        {
            // Only where somebody chose to quit with the application in front of
            // them. A transfer running in its own window is something quitting
            // destroys, and that was unreachable while a transfer held the file
            // manager modal — it is easy now, with a forty gigabyte upload two
            // thirds through and its window minimised.
            //
            // Deliberately *not* asked on every path into here. The other caller
            // is the exit command a new launch sends down the pipe when it wants
            // this instance to stand down — which is how --install-only replaces
            // the running copy. A modal question on that path is an installer
            // waiting out its thirty seconds against a dialog nobody is looking
            // at, and then reporting that the file is in use.
            if (ask && _form is { IsDisposed: false } window &&
                !window.ConfirmAbandonTransfers("Quit")) return;

            // First, while the tray icon and the window are both still there to
            // say it with. Anything below this point has already taken away one
            // of the two places it could have gone.
            Notify("app.quitting", "Closing " + _settings.DisplayTitle);

            // Stop answering handoffs first of all.
            //
            // Quitting runs on the UI thread, so from here to the end nothing is
            // pumping — while the pipe listener is still up and still saying yes.
            // A folder double-clicked during those seconds (Drive unmounting and
            // reads coming back is the slow part) was accepted by a copy that
            // could no longer act on it: the launcher took the answer as success
            // and exited, the posted work was discarded with the message loop,
            // and the folder simply never opened. Refusing lets the launcher fall
            // through to its own fallback instead.
            try { SingleInstance.StopServer(); } catch { }
            _quitting = true;

            // And off the screen. A launcher that gets no answer from the pipe
            // looks for this window to bring forward, and finding it — still up
            // for the seconds teardown takes — it raised a window that was
            // closing and never opened the folder it carried.
            try { if (_form is { IsDisposed: false } closing) closing.Hide(); } catch { }

            // Static events outlive the instance that subscribed to them, and
            // the pump these arrive on is still running.
            ShellRegistration.Changed -= OnShellRegistrationChanged;
            Speech.Notification -= OnSpeechAvailabilityChanged;

            try
            {
                // The volume, the speed and where the track had got to can all
                // have been changed by a global shortcut with no window ever
                // open, and the window is what normally writes the settings out.
                // Save before anything is torn down.
                _settings.AudioVolumePercent = _audio.VolumePercent;
                _settings.AudioPlaybackRatePercent = _audio.PlaybackRatePercent;
                _settings.AudioRepeatTrack = _audio.RepeatTrack;

                _settings = WithWaitingSettings(_settings);
                _form?.AdoptSettings(_settings);
                _settings.Save();

                _hold.Dispose();
                _hotkeys.Dispose();
                _browsePrefetch.Dispose();

                // Disposing the player stops the download and deletes whatever
                // had been fetched of the track.
                //
                // A track still opening on a worker is told it is no longer
                // wanted, and given a moment to come out of the gate: disposed
                // underneath it, it went on to install a live player that was
                // still reading the letter when the provider was torn down.
                System.Threading.Interlocked.Increment(ref _playRequest);

                // And an open in progress is called off rather than waited out.
                try { _audio.CancelOpening(); } catch { }
                bool gated = System.Threading.Monitor.TryEnter(_playGate, TimeSpan.FromSeconds(5));
                try { _audio.Dispose(); }
                finally { if (gated) System.Threading.Monitor.Exit(_playGate); }

                // Anything the player had in flight against the letter has to
                // have come back before the provider that answers it goes. The
                // player does not wait for its own download at any other moment,
                // deliberately; here the alternative is worse than a pause.
                StreamingSource.WaitForReads(TimeSpan.FromSeconds(5));

                // The letter and the sync root both outlive this process, so they
                // are taken down deliberately rather than left for the next launch
                // to sweep — but **after** everything here that reads from them,
                // never before, and that order is the difference between quitting
                // and a process nobody can kill.
                //
                // It used to be the first thing this method did, while the player
                // was still playing a track off the letter. A read of a
                // placeholder is answered by this process, so pulling the provider
                // out from under one leaves a read waiting for an answer that can
                // never come; Windows will not let a process exit with I/O still
                // outstanding, so the process stopped halfway out — exit code set,
                // runtime gone, unkillable, and only a reboot cleared it. The
                // player goes first, and DriveMount.Dispose then waits for any
                // callback still being answered before it disconnects.
                _connect?.Dispose();
                _connectStreams?.Dispose();
                _connectClipboard?.Dispose();
                _monitor.Dispose();
                _drive.Dispose();

                _tray.Visible = false;
                _tray.Dispose();

                if (_form is { IsDisposed: false })
                {
                    _form.ForceClose();
                    _form.Dispose();
                }
            }
            catch { }

            // Anything handed over while quitting, to a copy that will wait for
            // this one's lock. Through the shell, so it inherits nothing.
            SingleInstance.WaitForServer();

            while (_inbox.TryDequeue(out var waiting))
            {
                if (IsFolderRequest(waiting) || waiting == SingleInstance.ShowCommand) _unserved.Enqueue(waiting);
                else if (IsSettingsRequest(waiting)) _settingsWhileQuitting.Enqueue(waiting);
            }

            // The last write, after the window's own: anything that arrived since
            // the save above, and the file as it now is.
            try { WithWaitingSettings(_settings).Save(); } catch { }

            // --restart: the new copy waits for this one's lock rather than
            // handing the folder to a window that is closing. A plain launch —
            // no folder — comes back as a plain start.
            while (_unserved.TryDequeue(out var folder))
            {
                try
                {
                    var arguments = folder == SingleInstance.ShowCommand
                        ? "--restart"
                        // A trailing backslash would escape the closing quote.
                        : "--restart \"" + (folder.EndsWith('\\') ? folder + "." : folder) + "\"";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        Application.ExecutablePath, arguments)
                    {
                        UseShellExecute = true,
                    });
                }
                catch { }
            }

            ExitThread();
        }
    }
}







