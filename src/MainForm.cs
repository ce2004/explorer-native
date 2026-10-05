using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;

namespace ExplorerNative
{
    public sealed class MainForm : Form
    {
        /// <summary>One row. Built once during enumeration, formatted on demand.</summary>
        private sealed record Entry(string Path, string Name, bool IsDir, long Size, DateTime Modified)
        {
            public string? SizeOverride { get; set; }

            /// <summary>
            /// What the Name column says, when that is not simply the file's
            /// name. Search results use it to carry the folder a hit came out of
            /// — "Lovesick.flac, in 2019" — because a results list gathers names
            /// from all over a tree and three rows called the same thing with no
            /// way to tell them apart is the one thing it must not do.
            /// </summary>
            public string? DisplayOverride { get; set; }
        }

        private sealed class Pane
        {
            public required ListView List;
            public string CurrentPath = "";
            public CancellationTokenSource? EnumCts;

            /// <summary>
            /// Backing data for the virtual list. The ListView holds no items of
            /// its own, so a folder with a hundred thousand files costs one
            /// enumeration and nothing else — no per-row control construction.
            /// </summary>
            public List<Entry> Entries = new();
            public ListViewItem?[] ItemCache = Array.Empty<ListViewItem?>();

            /// <summary>How many slots of <see cref="ItemCache"/> are filled.</summary>
            public int CachedCount;

            /// <summary>
            /// Path to row index. Folder sizes arrive one at a time and each one
            /// has to find its row; scanning the list for every arrival is O(n)
            /// per folder, so filling in a folder of ten thousand subfolders was
            /// quadratic. This makes each arrival a lookup.
            /// </summary>
            public Dictionary<string, int> IndexByPath = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>Where we were in each folder this tab has visited.</summary>
            public readonly FolderPositionMemory Positions = new();

            /// <summary>Letters typed recently, for jump-to-name.</summary>
            public readonly TypeAheadBuffer TypeAhead = new();

            /// <summary>The folder this pane last finished loading.</summary>
            public string LastLoadedPath = "";

            /// <summary>
            /// What was searched for, when this pane is showing results rather
            /// than a folder. Null the rest of the time, and that is the flag:
            /// it is what Escape looks at to know there is something to come back
            /// out of, and what stops the folder watcher replacing the results
            /// with the folder they were found in.
            ///
            /// <see cref="CurrentPath"/> stays the folder that was searched
            /// throughout, so paste, refresh and everything else that acts on
            /// "where I am" still have a real answer.
            /// </summary>
            public string? SearchTerm;

            /// <summary>Whether the results stopped at the cap rather than running out.</summary>
            public bool SearchTruncated;

            /// <summary>
            /// A search running right now. Separate from
            /// <see cref="SearchTerm"/>, which is only set once results arrive:
            /// between pressing Enter in the dialog and the answer coming back
            /// there is nothing showing yet and Escape still has to mean stop.
            /// A walk of a whole disk is minutes, and one that cannot be
            /// abandoned is a window somebody has to wait out.
            /// </summary>
            public bool Searching;

            /// <summary>
            /// A listing is on its way in. The folder watcher leaves its mark
            /// standing rather than cancel it and start again — on a slow share with
            /// a download growing in it, that was a reload that never finished.
            /// </summary>
            public bool Loading;

            /// <summary>
            /// Which navigation of this pane is the latest. A navigation checks
            /// it after every wait and gives way if a newer one has started — see
            /// <see cref="NavigateAsync"/>.
            /// </summary>
            public int NavigationId;

            /// <summary>
            /// The navigation still checking that its folder is there, or 0.
            /// Nothing marks the pane busy during that check — up to eight
            /// seconds on a sleeping share — and a watcher reload in that gap
            /// started a navigation of its own, which the first then gave way to.
            /// </summary>
            public int Probing;

            /// <summary>
            /// The pending reload is ours — new preferences, a mount arriving —
            /// and nothing on disk changed, so "changed" is not said for it.
            /// </summary>
            public bool ReloadQuietly;

            /// <summary>
            /// A name for the list that is waiting for the keyboard to leave it.
            ///
            /// Renaming a control that currently has focus is an announcement:
            /// the screen reader is watching that object and reads its new name
            /// out. Null when the name on the control is already correct.
            /// </summary>
            public string? PendingName;

            /// <summary>Tells us when this folder changes underneath us.</summary>
            public FileSystemWatcher? Watcher;

            /// <summary>
            /// Set from the watcher's thread, cleared on the UI thread. Volatile
            /// because those are different threads and nothing else synchronises
            /// them; a missed flag is a list that stays wrong.
            /// </summary>
            public volatile bool NeedsRefresh;
        }

        private const string DrivesPath = "::drives";

        /// <summary>
        /// The file list. Its UIA provider is left alone, and that is a decision
        /// rather than an omission.
        ///
        /// WinForms answers WM_GETOBJECT with a UIA provider, and for a list in
        /// virtual mode that provider is not sound: asked for a property it
        /// claims to support, it throws, and the screen reader receives
        /// "Catastrophic failure" in the middle of announcing the row, which
        /// aborts the whole announcement. It is in the NVDA log:
        ///
        ///   NVDAObjects\UIA\__init__.pyc, line 2310, in _get_value
        ///   _ctypes.COMError: (-2147418113, 'Catastrophic failure', ...)
        ///
        /// The obvious answer is to decline the provider — answer WM_GETOBJECT
        /// (0x003D) with zero for UiaRootObjectId (-25) — so the reader falls
        /// back to the control's own MSAA implementation, which list views used
        /// for twenty years before UIA existed. **It was tried and it is worse.**
        /// There is no fallback: it takes the list away from the screen reader
        /// altogether and every key goes silent, arrows included.
        ///
        /// So there is no WndProc override here. There was one for a while that
        /// did nothing but call base, kept to hold this paragraph, which is a
        /// comment pretending to be code. The paragraph is the whole of what is
        /// worth keeping. The intermittent fault is not worked around.
        /// </summary>
        private sealed class FileListView : ListView
        {
            /// <summary>
            /// Says that this row now has the focus, whether or not the control
            /// thinks anything changed.
            ///
            /// In a virtual list an item's identity is its index — there is no
            /// object behind row 0, only the number 0 — so replacing the whole
            /// folder and landing on row 0 while row 0 was already focused is not
            /// a focus change to anything watching from outside. The control
            /// raises nothing, and the screen reader has no reason to say
            /// anything, so entering a folder was silent.
            ///
            /// Raising it here is not a trick: the focus really is on that row and
            /// it really is a different file. A second event for a row the reader
            /// already announced is ignored, which is what makes this safe to send
            /// unconditionally rather than having to guess when the control will
            /// send its own.
            /// </summary>
            public void NotifyRowFocused(int index) =>
                AccessibilityNotifyClients(AccessibleEvents.Focus, index);
        }

        private Settings _settings;
        private readonly FolderSizeCalculator _folderSizes = new();
        private readonly Pane[] _panes = new Pane[2];
        private int _activeIndex;

        private Label _pathLabel = null!;
        private Font? _ownedFont;
        private ToolStripStatusLabel _statusLabel = null!;
        private ContextMenuStrip _contextMenu = null!;
        private System.Windows.Forms.Timer _availabilityTimer = null!;

        private Pane Active => _panes[_activeIndex];
        private Pane Other => _panes[1 - _activeIndex];
        private bool IsDrivesView => Active.CurrentPath == DrivesPath;

        /// <summary>
        /// The Google Drive mount, when there is one. Navigation has to fill a
        /// cloud folder in before listing it — Windows will not ask on our behalf
        /// — so the pane loader needs to be able to reach it.
        /// </summary>
        public GoogleDrive? Drive { get; set; }

        // TrayNotification was here — the hook that let this window ask the tray
        // to put up a balloon. There are no balloons any more: a Windows
        // notification is something you have to be looking at a screen to know
        // about, and this application is driven by ear. Everything it used to
        // carry is either spoken or in the status bar, both of which are here.

        /// <summary>
        /// Re-reads a pane that is already sitting on the Drive letter.
        ///
        /// The mount arrives a second or two after the window, so a pane restored
        /// onto that letter by position memory listed it while it was still an
        /// empty folder. Without this it stays empty until somebody navigates
        /// away and back.
        /// </summary>
        public void RefreshIfShowingDrive(string? letter)
        {
            if (string.IsNullOrEmpty(letter) || IsDisposed) return;

            foreach (var pane in _panes)
                if (pane?.CurrentPath != null &&
                    pane.CurrentPath.StartsWith(letter, StringComparison.OrdinalIgnoreCase))
                {
                    // Only the active pane can be navigated; the other is marked
                    // and re-read when it is switched to, which is the same rule
                    // the folder watcher follows.
                    // Results under the letter are not the folder, and
                    // re-navigating threw them away.
                    if (pane.SearchTerm != null || pane.Searching) pane.NeedsRefresh = true;
                    else if (pane == Active) Navigate(pane.CurrentPath);
                    else { pane.NeedsRefresh = true; pane.ReloadQuietly = true; }
                }
        }

        public MainForm(Settings settings)
        {
            _settings = settings;

            StartPosition = FormStartPosition.CenterScreen;

            BuildLayout();
            BuildMenu();
            BuildContextMenu();
            ApplySettingsToUi();
            RestoreWindowGeometry();

            // Which folders to try, not which folder to open. Deciding that here
            // meant a synchronous Directory.Exists per candidate *inside the
            // constructor*, and one of those candidates is wherever you were last
            // — routinely a share. A sleeping NAS blocks that call for as long as
            // the SMB stack takes to give up, and this application is the shell's
            // handler for every folder on the machine, started afresh each time
            // one is opened. So the window did not merely take a moment to fill
            // in, it did not appear at all.
            //
            // The choosing is done on the way in to InitialiseTabsAsync instead,
            // through the same bounded probe navigation already uses.
            var tab1 = new[]
            {
                _settings.InitialFolderOverride,
                _settings.RememberLastFolder ? _settings.LastFolder : null,
                _settings.StartPath,
            };

            var tab2 = new[]
            {
                _settings.RememberLastFolder ? _settings.LastFolderTab2 : null,
                _settings.StartPath,
            };

            _ = InitialiseTabsAsync(tab1, tab2);

            // Cheap poll so an unplugged drive is noticed even while idle.
            _availabilityTimer = new System.Windows.Forms.Timer { Interval = 4000 };
            _availabilityTimer.Tick += (_, _) => CheckCurrentPathStillExists();
            _availabilityTimer.Start();
        }

        /// <summary>
        /// Loads both tabs and puts you back exactly where you were — same tab in
        /// front, same item focused — so a restart is invisible if the folder and
        /// file are still there, and a graceful fallback if they are not.
        /// </summary>
        private bool _initialising;

        private async Task InitialiseTabsAsync(string?[] tab1Candidates, string?[] tab2Candidates)
        {
            // Tab switching is locked out until both panes are loaded: this
            // routine moves _activeIndex around to fill each pane, and a Tab
            // press landing in the middle of that would be overwritten a moment
            // later, leaving the user on a tab they did not choose.
            _initialising = true;
            try
            {
                // Off the constructor and behind a timeout, so a share that has
                // gone to sleep costs a moment on a worker rather than a window
                // that never appears.
                var tab1 = await FirstExistingFolderAsync(tab1Candidates);
                if (IsDisposed) return;
                var tab2 = await FirstExistingFolderAsync(tab2Candidates);
                if (IsDisposed) return;

                // The remembered row is chosen while the list is built, not moved
                // to afterwards, so startup settles on one row instead of
                // announcing the first one on the way past.
                _activeIndex = 1;
                await NavigateAsync(tab2, preferPath: NullIfBlank(_settings.FocusedPathTab2));
                if (IsDisposed) return;

                _activeIndex = 0;
                ShowActivePane();
                await NavigateAsync(tab1, preferPath: NullIfBlank(_settings.FocusedPathTab1));
                if (IsDisposed) return;

                // Whichever tab was in front last time comes back in front —
                // unless a folder was named on the command line, which is a
                // direct request to look at something and belongs in front of
                // anything remembered. The folder goes to tab one, so restoring
                // tab two over the top of it meant double-clicking a folder
                // opened a window showing a different folder entirely.
                if (_settings.ActiveTab == 1 && string.IsNullOrEmpty(_settings.InitialFolderOverride))
                {
                    _activeIndex = 1;
                    ShowActivePane();
                    _pathLabel.Text = PathLabelFor(Active);
                    UpdateStatus();
                }

                // Used. A window built later in this session — closing to nothing
                // rather than to the tray, then the window shortcut — reopened on
                // the folder that started the process, not where it was left.
                _settings.InitialFolderOverride = null;

                if (!IsDisposed && Visible) Active.List.Focus();
            }
            finally { _initialising = false; }

            // A folder handed over while the tabs were loading, now that they
            // have. Acted on then, it went to whichever tab was loading, and the
            // end of startup put the other one in front of it.
            if (_waitingOutside is { } later && !IsDisposed)
            {
                _waitingOutside = null;
                ShowFolderFromOutside(later);
            }
        }

        /// <summary>A folder handed over during startup, held until it has finished.</summary>
        private string? _waitingOutside;

        /// <summary>Captures where we are, so a restart can land back on it.</summary>
        private void CaptureState()
        {
            _settings.ActiveTab = _activeIndex;

            // Reads each pane directly. Temporarily reassigning _activeIndex to
            // reuse FocusedPath() would make every selection handler briefly
            // believe the wrong pane was active.
            _settings.FocusedPathTab1 = FocusedPathOf(_panes[0]) ?? "";
            _settings.FocusedPathTab2 = FocusedPathOf(_panes[1]) ?? "";

            if (_settings.RememberLastFolder)
            {
                _settings.LastFolder = _panes[0].CurrentPath;
                _settings.LastFolderTab2 = _panes[1].CurrentPath;
            }
        }

        private static string? NullIfBlank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;

        private static string? FocusedPathOf(Pane pane)
        {
            try
            {
                int index = CurrentIndex(pane);
                return index >= 0 && index < pane.Entries.Count ? pane.Entries[index].Path : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Ctrl+R: relaunch the application and return to this exact spot.
        /// The replacement is started with --restart so it waits for this
        /// process to release the single-instance lock instead of bailing out.
        /// </summary>
        private void ShowChangelog()
        {
            using (var log = new ChangelogForm()) log.ShowDialog(this);
            RestoreListFocus();
        }

        private void ShowAbout()
        {
            MessageBox.Show(this,
                $"Explorer Native version {Updater.CurrentText}, " +
                $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()}.\n" +
                $"github.com/{Updater.Repository}",
                "About Explorer Native", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RestoreListFocus();
        }

        private bool _checkingForUpdates;

        /// <summary>
        /// Help, Check for updates. Asks GitHub, says what it found, and on Yes
        /// downloads the new build and hands it to the installer, which replaces
        /// this executable while it runs and restarts onto the new one. See
        /// <see cref="Updater"/>.
        /// </summary>
        private async void CheckForUpdates()
        {
            if (_checkingForUpdates)
            {
                Announce("Already checking for updates");
                return;
            }

            _checkingForUpdates = true;
            try
            {
                Announce("Checking for updates");

                Updater.Release? release;
                try
                {
                    release = await Updater.CheckAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Could not check for updates. {Plain(ex)}",
                        "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (release == null)
                {
                    MessageBox.Show(this, $"You have the latest version, {Updater.CurrentText}.",
                        "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                using (var ask = new UpdateForm(release))
                {
                    if (ask.ShowDialog(this) != DialogResult.OK) return;
                }

                if (!ConfirmAbandonTransfers("Update")) return;

                string exe;
                try
                {
                    Announce("Downloading update");
                    exe = await Updater.DownloadAsync(release);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"The update could not be downloaded. {Plain(ex)}",
                        "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                Announce("Installing update. Explorer Native will restart");
                CaptureState();
                CaptureWindowGeometry();
                _settings.Save();

                Process installer;
                try
                {
                    installer = Updater.StartInstall(exe, release);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"The update could not be started. {Plain(ex)}",
                        "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // On success the installer asks this copy to close and starts
                // the new one, so this await never sees the end. On failure this
                // copy is left running, and should say why.
                using (installer)
                {
                    await installer.WaitForExitAsync();
                    if (installer.ExitCode != 0 && !IsDisposed)
                    {
                        MessageBox.Show(this,
                            "The update could not be installed. The details are in " +
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                "ExplorerNative", "install.log") + ".",
                            "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            finally
            {
                _checkingForUpdates = false;
                if (!IsDisposed) RestoreListFocus();
            }

            static string Plain(Exception ex) =>
                ex is TaskCanceledException or OperationCanceledException
                    ? "GitHub did not answer in time."
                    : ex is System.Net.Http.HttpRequestException ? "GitHub could not be reached: " + ex.Message : ex.Message;
        }

        private void RestartApp()
        {
            // Before the replacement is launched, not after. Quit itself cannot
            // ask on this path: by the time it is reached the new process is
            // already coming up, and answering "no" there would leave two of
            // them fighting over the single-instance lock.
            if (!ConfirmAbandonTransfers("Restart")) return;

            try
            {
                CaptureState();
                CaptureWindowGeometry();
                _settings.Save();

                Announce("Restarting");

                Process.Start(new ProcessStartInfo(Application.ExecutablePath)
                {
                    Arguments = "--restart",
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                Announce($"Could not restart: {ex.Message}", isError: true);
                return;
            }

            Program.QuitForRestart();
        }

        /// <summary>
        /// How long one remembered folder may take to answer at startup.
        ///
        /// Shorter than the deliberate-navigation probe, because nobody asked for
        /// this one: it is where you happened to be last time, and the home
        /// folder behind it is always there. Waiting eight seconds for a share to
        /// wake up before showing any window at all is the wrong trade when the
        /// fallback is instant.
        /// </summary>
        private const int StartupProbeMs = 2000;

        /// <summary>
        /// The first candidate that answers, or the home folder.
        ///
        /// Bounded and off the calling thread, which is the whole point — see the
        /// constructor. The synchronous version of this ran inside it.
        /// </summary>
        private static async Task<string> FirstExistingFolderAsync(params string?[] candidates)
        {
            foreach (var c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;

                // The drive list is always there. Probed as a folder it was not,
                // so a window closed on the drive list came back somewhere else.
                if (c == DrivesPath) return c;
                if (await ProbeDirectoryAsync(c, StartupProbeMs) == FolderProbe.Present) return c!;
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        /// <summary>
        /// Google Drive has moved from one letter to another. A tab showing a
        /// folder on the old letter follows it to the same folder on the new
        /// one, on the same row, rather than finding its folder gone.
        /// </summary>
        public void DriveLetterMoved(string from, string to)
        {
            static string? Moved(string? path, string from, string to) =>
                path != null && path.StartsWith(from, StringComparison.OrdinalIgnoreCase)
                    ? to + path[from.Length..]
                    : null;

            foreach (var pane in _panes)
            {
                if (pane == null) continue;
                var folder = Moved(pane.CurrentPath, from, to);
                if (folder == null) continue;
                var row = Moved(FocusedPathOf(pane), from, to);
                _ = NavigateAsync(folder, preferPath: row, onPane: pane, quiet: true, keepFocus: pane != Active);
            }
        }

        public void ApplyNewSettings(Settings settings)
        {
            _settings = settings;
            ApplySettingsToUi();

            // Hidden files, sorting and folders-first change the other tab's
            // listing too, and only the front one is reloaded now.
            foreach (var pane in _panes)
                if (pane != Active) { pane.NeedsRefresh = true; pane.ReloadQuietly = true; }
            Refresh_();
        }

        /// <summary>
        /// Shows a folder that arrived from outside — the shell opening one in
        /// this app, or a second launch handing its argument over.
        ///
        /// This is the whole point of the single-instance handoff: once the app is
        /// the default handler for folders, every double-click on a folder lands
        /// here, and a request that arrived while a window already existed must
        /// move that window rather than be dropped on the floor.
        /// </summary>
        public async void ShowFolderFromOutside(string path)
        {
            if (IsDisposed || string.IsNullOrWhiteSpace(path)) return;
            if (_initialising) { _waitingOutside = path; return; }

            string? select = null;
            var target = NameRules.NormaliseLaunchPath(path);
            if (target.Length == 0) return;

            // A file was handed to us: show its folder with it picked out.
            //
            // Bounded, and given up if anything navigated meanwhile: on a
            // sleeping share these waited twenty seconds and more, and then took
            // the window back from wherever somebody had gone in the meantime.
            var pane = Active;
            int navigation = ++pane.NavigationId;

            // Marked as a navigation in progress, so the folder watcher's reload
            // waits rather than counting as somebody having gone elsewhere.
            pane.Probing = navigation;
            try
            {
                var folderCheck = await ProbeDirectoryAsync(target, NavigationProbeMs);
                if (folderCheck == FolderProbe.Missing && await Bounded(() => File.Exists(target)))
                {
                    select = target;
                    target = Path.GetDirectoryName(target) ?? target;
                }
            }
            finally
            {
                if (pane.Probing == navigation) pane.Probing = 0;
            }

            if (IsDisposed || pane.NavigationId != navigation) return;

            // The file is the row to land on, in the one move: selecting it after
            // the folder had already landed and been read out read two rows.
            await NavigateAsync(target, preferPath: select);
            if (IsDisposed) return;

            if (Visible) Active.List.Focus();
        }

        // ---------- UI ----------

        private void BuildLayout()
        {
            _pathLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0),
            };
            _pathLabel.AccessibleName = "Current folder";

            for (int i = 0; i < 2; i++)
            {
                var list = new FileListView
                {
                    Dock = DockStyle.Fill,
                    View = View.Details,
                    FullRowSelect = true,
                    MultiSelect = true,
                    HideSelection = false,
                    LabelEdit = false,
                    Visible = false,
                    VirtualMode = true,
                    VirtualListSize = 0,
                };
                // Replaced with the folder's name as soon as one is loaded. See
                // UpdatePaneName: this is only what an empty pane is called.
                list.AccessibleName = "Files and folders";
                list.KeyDown += List_KeyDown;
                list.KeyPress += List_KeyPress;
                list.DoubleClick += (_, _) => ActivateFocused();
                list.ColumnClick += List_ColumnClick;

                int index = i;
                list.RetrieveVirtualItem += (_, e) => e.Item = BuildVirtualItem(_panes[index], e.ItemIndex);
                list.SelectedIndexChanged += (_, _) => { if (_panes[index] == Active) OnSelectionChanged(); };

                // A virtual list reports Shift+arrow as a range, and only as a
                // range — without this the status bar kept the old "N selected"
                // while the selection grew.
                list.VirtualItemsSelectionRangeChanged += (_, _) => { if (_panes[index] == Active) OnSelectionChanged(); };

                // The only moment renaming this list is silent. Navigation holds
                // the new folder's name back while the keyboard is in here; this
                // is where it gets put on.
                list.Leave += (_, _) => ApplyPendingName(_panes[index]);

                _panes[i] = new Pane { List = list };
                Controls.Add(list);
            }

            _statusLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            var status = new StatusStrip();
            status.Items.Add(_statusLabel);

            Controls.Add(_pathLabel);
            Controls.Add(status);
        }

        private void BuildMenu()
        {
            var menu = new MenuStrip();

            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(Item("&Open", Keys.None, ActivateFocused));
            var playCd = Item("Play &CD", Keys.None, PlayCd);
            file.DropDownItems.Add(playCd);
            file.DropDownItems.Add(Item("New f&ile", Keys.F7, CreateNewFile));
            file.DropDownItems.Add(Item("New &folder", Keys.F8, CreateNewFolder));
            file.DropDownItems.Add(Item("&Rename", Keys.F2, RenameSelected));
            file.DropDownItems.Add(Item("&Delete", Keys.Delete, () => DeleteSelected(false)));
            file.DropDownItems.Add(Item("Delete &permanently", Keys.Shift | Keys.Delete, () => DeleteSelected(true)));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Item("P&roperties (Alt+Enter)", Keys.None, ShowProperties));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Item("E&xit", Keys.None, Close));

            // Always on the menu, drive or no drive, at Conner's request: a USB
            // drive comes and goes, and an entry that appears only while it is
            // plugged in is one you cannot find when you go looking. With no
            // disc, the command says so in a dialog.
            menu.Items.Add(file);

            var edit = new ToolStripMenuItem("&Edit");
            edit.DropDownItems.Add(Item("&Copy", Keys.Control | Keys.C, () => CopyToClipboard(false)));
            edit.DropDownItems.Add(Item("Cu&t", Keys.Control | Keys.X, () => CopyToClipboard(true)));
            edit.DropDownItems.Add(Item("&Paste", Keys.Control | Keys.V, PasteClipboard));
            edit.DropDownItems.Add(new ToolStripSeparator());
            edit.DropDownItems.Add(Item("Copy &path", Keys.Alt | Keys.C, CopyPath));
            edit.DropDownItems.Add(DriveLinkItem("Copy Drive &link", Keys.Alt | Keys.L, makePublic: false));
            edit.DropDownItems.Add(DriveLinkItem("Copy p&ublic Drive link", Keys.Alt | Keys.Shift | Keys.L, makePublic: true));
            edit.DropDownItems.Add(Item("Select &all", Keys.Control | Keys.A, SelectAll));
            edit.DropDownItems.Add(new ToolStripSeparator());
            edit.DropDownItems.Add(Item("Pre&ferences", Keys.Control | Keys.P, OpenPreferences));

            // The same decision the popup makes, made here for the same reason.
            // A menu is read out one item at a time, and two entries about
            // Google Drive are two entries heard on the way past every other one
            // — on a machine with no Drive mounted at all, twice for nothing.
            edit.DropDownOpening += (_, _) => ShowDriveLinkItems();
            menu.Items.Add(edit);

            var go = new ToolStripMenuItem("&Go");
            go.DropDownItems.Add(Item("&Up one level", Keys.Alt | Keys.Up, GoUp));
            go.DropDownItems.Add(Item("&Drives", Keys.Alt | Keys.D, () => Navigate(DrivesPath)));
            go.DropDownItems.Add(Item("Go to &path…", Keys.Control | Keys.L, GoToPath));
            go.DropDownItems.Add(Item("&Home", Keys.Alt | Keys.Home,
                () => Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));
            go.DropDownItems.Add(Item("&Search here…", Keys.F1, SearchHere));
            go.DropDownItems.Add(Item("&Refresh", Keys.F5, Refresh_));
            go.DropDownItems.Add(Item("Res&tart application", Keys.Control | Keys.R, RestartApp));
            menu.Items.Add(go);

            menu.Items.Add(BuildAudioMenu());

            var tools = new ToolStripMenuItem("T&ools");
            tools.DropDownItems.Add(Item("Calculate folder &size", Keys.Control | Keys.Shift | Keys.S, CalculateFocusedFolderSize));
            tools.DropDownItems.Add(Item("&Windows context menu", Keys.None, ShowShellMenuForSelection));
            tools.DropDownItems.Add(new ToolStripSeparator());

            // Compressing and extracting are not here. They live on the context
            // menu, which is where a command that acts on the selection belongs:
            // Shift+F10 is one keystroke from the row it is about, and the Tools
            // menu is three keystrokes and a different mental place. See
            // BuildContextMenu.
            tools.DropDownItems.Add(Item("&Announce current folder", Keys.Control | Keys.Shift | Keys.A, AnnounceFolder));
            menu.Items.Add(tools);

            // Updates are only ever checked from here, when asked. There is no
            // automatic check and no setting for one.
            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add(Item("Check for &updates…", Keys.None, CheckForUpdates));
            help.DropDownItems.Add(Item("&Changelog", Keys.None, ShowChangelog));
            help.DropDownItems.Add(Item("&About Explorer Native", Keys.None, ShowAbout));
            menu.Items.Add(help);

            MainMenuStrip = menu;
            Controls.Add(menu);
        }

        private ToolStripMenuItem _playPauseItem = null!;
        private ToolStripMenuItem _muteItem = null!;
        private ToolStripMenuItem _repeatItem = null!;
        private ToolStripMenuItem _limiterItem = null!;
        private ToolStripMenuItem _equaliserItem = null!;
        private ToolStripMenuItem _silenceItem = null!;
        private ToolStripMenuItem _pitchItem = null!;
        private ToolStripMenuItem _nowPlayingItem = null!;

        /// <summary>
        /// The Audio menu.
        ///
        /// The player has no window, which is the point of it — but "no window"
        /// should not mean "no way to find out what it can do". A menu is how a
        /// keyboard user discovers a feature and reads its shortcut off the right
        /// hand side, and it is the only route to the player that cannot be taken
        /// away by another application claiming a hotkey.
        ///
        /// Every item is driven from <see cref="AudioActions.All"/>, so the
        /// shortcut shown here is the shortcut that is actually registered — the
        /// two cannot drift apart.
        /// </summary>
        private ToolStripMenuItem BuildAudioMenu()
        {
            var audio = new ToolStripMenuItem("&Audio");

            _playPauseItem = AudioItem(AudioAction.PlayPause);
            _muteItem = AudioItem(AudioAction.Mute);
            _repeatItem = AudioItem(AudioAction.ToggleRepeat);

            _nowPlayingItem = new ToolStripMenuItem("Nothing is playing") { Enabled = false };
            audio.DropDownItems.Add(_nowPlayingItem);
            audio.DropDownItems.Add(new ToolStripSeparator());

            audio.DropDownItems.Add(_playPauseItem);
            audio.DropDownItems.Add(AudioItem(AudioAction.Stop));
            audio.DropDownItems.Add(new ToolStripSeparator());

            audio.DropDownItems.Add(AudioItem(AudioAction.VolumeUp));
            audio.DropDownItems.Add(AudioItem(AudioAction.VolumeDown));
            audio.DropDownItems.Add(_muteItem);
            audio.DropDownItems.Add(new ToolStripSeparator());

            audio.DropDownItems.Add(AudioItem(AudioAction.SeekBackward));
            audio.DropDownItems.Add(AudioItem(AudioAction.SeekForward));
            audio.DropDownItems.Add(AudioItem(AudioAction.SeekBackwardLong));
            audio.DropDownItems.Add(AudioItem(AudioAction.SeekForwardLong));
            audio.DropDownItems.Add(AudioItem(AudioAction.RestartTrack));
            audio.DropDownItems.Add(new ToolStripSeparator());

            // Speed up and slow down are shortcuts only. Nudging by a step is
            // something you do with a key while listening, not something you go
            // to a menu for; the menu offers the list instead, which is the thing
            // a menu is good at.
            audio.DropDownItems.Add(AudioItem(AudioAction.SpeedDialog));

            // Beside the speed, because they are the pair somebody thinks of
            // together and the whole point of the audio path keeping a stretcher
            // and a resampler is that these two move independently.
            _pitchItem = AudioItem(AudioAction.PitchDialog);
            audio.DropDownItems.Add(_pitchItem);

            audio.DropDownItems.Add(AudioItem(AudioAction.DeviceDialog));

            // Always there now, like the equaliser below it.
            //
            // It used to be hidden while the limiter was switched off, on the
            // reasoning that controls shaping an effect that is not running are
            // controls that do nothing. That was true while the switch was a
            // tick box on the Audio preferences page — and it meant the way to
            // find the limiter's controls was to know that a page you were not
            // on decided whether this entry existed, which is worse than the
            // problem it solved. The switch is inside the dialog now, so the
            // entry always leads somewhere that does something.
            _limiterItem = AudioItem(AudioAction.LimiterDialog);
            audio.DropDownItems.Add(_limiterItem);

            // Beside the limiter and on the same terms: always there, ticked
            // when it is doing something. Both dialogs carry their own switch,
            // so both entries always lead somewhere that does something.
            _equaliserItem = AudioItem(AudioAction.EqualiserDialog);
            audio.DropDownItems.Add(_equaliserItem);

            // And silence reduction, on the same terms as the two above it:
            // always there, ticked when it is doing something, carrying its own
            // switch inside the dialog so the entry always leads somewhere that
            // does something.
            _silenceItem = AudioItem(AudioAction.SilenceDialog);
            audio.DropDownItems.Add(_silenceItem);

            audio.DropDownItems.Add(_repeatItem);
            audio.DropDownItems.Add(new ToolStripSeparator());

            audio.DropDownItems.Add(AudioItem(AudioAction.WhatIsPlaying));
            audio.DropDownItems.Add(AudioItem(AudioAction.SayElapsed));
            audio.DropDownItems.Add(AudioItem(AudioAction.SayRemaining));
            audio.DropDownItems.Add(AudioItem(AudioAction.SayTotal));
            audio.DropDownItems.Add(Item(AudioActions.SongPropertiesMenuText, Keys.None, ShowSongProperties));
            audio.DropDownItems.Add(new ToolStripSeparator());
            audio.DropDownItems.Add(Item(AudioActions.PreferencesMenuText, Keys.None, OpenAudioPreferences));

            // Read the moment it opens rather than kept up to date from
            // elsewhere: the player is driven by global shortcuts that fire while
            // this window is not even on screen, so anything cached here would be
            // wrong more often than right.
            audio.DropDownOpening += (_, _) => RefreshAudioMenu();

            return audio;
        }

        private ToolStripMenuItem AudioItem(AudioAction action)
        {
            var info = AudioActions.For(action);
            var item = new ToolStripMenuItem(info.MenuText);

            // The registered shortcut, shown as text. ShortcutKeys cannot be used:
            // these are global hotkeys owned by the tray, and letting WinForms
            // claim them as menu accelerators too would give one key two owners.
            var shortcut = Shortcut.Parse(info.Get(_settings));
            if (shortcut.IsAssigned)
            {
                item.ShortcutKeyDisplayString = shortcut.ToString();
                item.ShowShortcutKeys = true;
            }

            item.Click += (_, _) => Program.RunAudioAction(action);
            return item;
        }

        /// <summary>
        /// Makes the menu say what the next press will do rather than what the
        /// last one did. A menu offering "Pause" for something already paused is
        /// a menu that has to be read twice.
        /// </summary>
        private void RefreshAudioMenu()
        {
            var state = Program.AudioState;

            var playPause = AudioActions.For(AudioAction.PlayPause);
            bool playing = state is { Playing: true };
            _playPauseItem.Text = playing ? playPause.MenuText : playPause.AlternateMenuText;
            _playPauseItem.AccessibleName = playing ? "Pause" : "Play";

            var mute = AudioActions.For(AudioAction.Mute);
            bool muted = state is { Muted: true };
            _muteItem.Text = muted ? mute.AlternateMenuText : mute.MenuText;
            _muteItem.AccessibleName = muted ? "Unmute" : "Mute";

            _repeatItem.Checked = state is { Repeating: true };

            // Ticked when the limiter is running, read on every open rather than
            // kept up to date — the dialog it opens can switch it on and off,
            // and so can a global shortcut with this menu never touched. It says
            // whether the effect is on without having to open anything, which is
            // what the tick box on the preferences page was really for.
            _limiterItem.Checked = _settings.AudioLimiter;
            _equaliserItem.Checked = _settings.AudioEqualiser;
            _silenceItem.Checked = _settings.AudioSilenceReduction;

            // Ticked when the pitch is anywhere but normal. There is no switch
            // to report — zero semitones *is* off — so the tick answers "is
            // something being done to the sound" without opening anything,
            // which is what it is for on the two entries above.
            _pitchItem.Checked = _settings.AudioPitchSemitones != 0;

            _nowPlayingItem.Text = state is { HasTrack: true }
                ? (playing ? "Playing " : "Paused: ") + state.Name
                : "Nothing is playing";
        }

        private void OpenAudioPreferences() => OpenPreferences("Audio");

        /// <summary>
        /// Everything the file says about itself: tags, length, bitrate, sample
        /// rate. The Windows properties sheet (Alt+Enter) shows the same data in
        /// a tab that has to be found; this is one keystroke and reads as a list.
        ///
        /// The focused row if there is one, otherwise whatever is playing — so
        /// this works from the Audio menu while the list is somewhere else
        /// entirely.
        /// </summary>
        private void ShowSongProperties()
        {
            var path = FocusedPath();
            var state = Program.AudioState;

            if (path == null || !AudioFiles.IsAudio(path, _settings.AudioExtensions))
                path = state is { HasTrack: true } ? state.Path : path;

            if (path == null) { Announce("Nothing selected"); return; }

            using var dialog = new SongPropertiesForm(path);
            dialog.ShowDialog(this);
            RestoreListFocus();
        }

        /// <summary>Kept so the popup can decide whether they belong on it.</summary>
        private ToolStripMenuItem _extractItem = null!;
        private ToolStripMenuItem _extractHereItem = null!;
        private ToolStripMenuItem _shortcutItem = null!;

        /// <summary>
        /// Every "Copy Drive link" entry, on whichever menu it sits.
        ///
        /// One list rather than four fields, because the rule is one rule: these
        /// are shown when the focused row is on Google Drive and hidden when it
        /// is not, and a second copy of that rule on a second menu is how the two
        /// menus end up disagreeing about the same file.
        /// </summary>
        private readonly List<ToolStripMenuItem> _driveLinkItems = new();

        private ToolStripMenuItem DriveLinkItem(string text, Keys shortcut, bool makePublic)
        {
            var item = Item(text, shortcut, () => CopyDriveLink(makePublic));
            _driveLinkItems.Add(item);
            return item;
        }

        /// <summary>
        /// Puts the Drive link entries on a menu only when the row it is about is
        /// really on Google Drive.
        ///
        /// They used to be on every menu over every row on the machine, so a
        /// folder of local files answered Shift+F10 with two entries that could
        /// only ever say "that is not on Google Drive" — two more items to hear
        /// past on the way to Rename, every single time. Same reasoning as the
        /// Extract entries next to them, and the same cost: this menu is read
        /// aloud one item at a time.
        ///
        /// <see cref="GoogleDrive.Owns(string)"/> is a path comparison against the mount,
        /// not a question asked of Drive, so deciding this costs no network and no
        /// read — which is the bar for anything that runs while a menu is being
        /// drawn.
        ///
        /// The keyboard shortcuts are unaffected. A hidden `ToolStripMenuItem`
        /// still claims its shortcut, so Alt+L goes on working from anywhere and
        /// answers for itself when the row is not on Drive.
        /// </summary>
        private void ShowDriveLinkItems()
        {
            bool onDrive = FocusedPath() is { } path && Drive != null && Drive.Owns(path);
            foreach (var item in _driveLinkItems) item.Visible = onDrive;
        }

        /// <summary>
        /// Our own context menu rather than the shell's, so Send to iPhone can
        /// live in it. The genuine Windows menu is still one item away.
        /// </summary>
        private void BuildContextMenu()
        {
            _contextMenu = new ContextMenuStrip();
            _contextMenu.Items.Add(Item("&Open", Keys.None, ActivateFocused));
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(Item("&Copy", Keys.None, () => CopyToClipboard(false)));
            _contextMenu.Items.Add(Item("Cu&t", Keys.None, () => CopyToClipboard(true)));
            _contextMenu.Items.Add(Item("&Paste", Keys.None, PasteClipboard));
            _contextMenu.Items.Add(Item("Copy &path", Keys.None, CopyPath));
            _contextMenu.Items.Add(DriveLinkItem("Copy Drive &link", Keys.None, makePublic: false));
            _contextMenu.Items.Add(DriveLinkItem("Copy p&ublic Drive link", Keys.None, makePublic: true));
            _contextMenu.Items.Add(new ToolStripSeparator());

            // Where Explorer puts it, between the clipboard and the two commands
            // that change the row itself — which is also where a hand that has
            // used Windows expects to find it.
            _shortcutItem = Item("Create &shortcut", Keys.None, CreateShortcut);
            _contextMenu.Items.Add(_shortcutItem);

            _contextMenu.Items.Add(Item("&Rename", Keys.None, RenameSelected));
            _contextMenu.Items.Add(Item("&Delete", Keys.None, () => DeleteSelected(false)));
            _contextMenu.Items.Add(new ToolStripSeparator());

            // Compress asks a question and Extract does not, which is why only
            // one of them has an ellipsis. Extract makes a folder named after the
            // archive and puts everything in it, so there is nothing to decide
            // and no way for it to land on top of anything; "Extract here" is the
            // one that can collide, and it asks whatever the paste conflict
            // setting says to ask.
            //
            // The two Extract entries are only on the menu when the row is
            // something they could work on. That is not tidiness: this menu is
            // read out one item at a time, so every entry on it is paid for on
            // the way past every other entry, and an Extract that can only ever
            // answer "that is not an archive" is a word heard on every single
            // opening of the menu to be useful on almost none of them. Compress
            // stays, because anything selected can be compressed.
            _contextMenu.Items.Add(Item("&Compress…", Keys.None, CompressSelection));

            _extractItem = Item("&Extract", Keys.None, () => ExtractFocused(intoNewFolder: true));
            _extractHereItem = Item("Extract h&ere", Keys.None, () => ExtractFocused(intoNewFolder: false));

            _contextMenu.Items.Add(_extractItem);
            _contextMenu.Items.Add(_extractHereItem);

            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(Item("P&roperties", Keys.None, ShowProperties));
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(Item("&More Windows options…", Keys.None, ShowShellMenuForSelection));
        }

        /// <summary>
        /// Plays the audio CD from its first track, and shows its tracks with
        /// the cursor on that one, so the rest of the disc is an arrow away.
        ///
        /// Finding the disc spins the drive up, which is seconds, so it is done
        /// on a worker. Async void like every menu command, and guarded the same
        /// way.
        /// </summary>
        private async void PlayCd()
        {
            try
            {
                var first = await Task.Run(CdTrackStream.FirstTrackOnAnyDisc);
                if (IsDisposed) return;
                if (first == null)
                {
                    ShowStatus("No audio CDs found");
                    MessageBox.Show(this, "No audio CDs found.", "Play CD",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    RestoreListFocus();
                    return;
                }

                var root = Path.GetPathRoot(first);
                if (root != null) _ = NavigateAsync(root, preferPath: first);

                if (!_settings.PlayAudioInApp || !Program.PlayAudioFile(first))
                    _ = Task.Run(() => { try { FileLauncher.Open(first); } catch { } });
            }
            catch (Exception ex)
            {
                if (!IsDisposed) Announce("Could not play the CD: " + ex.Message, isError: true);
            }
        }

        private static ToolStripMenuItem Item(string text, Keys shortcut, Action action)
        {
            var item = new ToolStripMenuItem(text);
            if (shortcut != Keys.None)
            {
                item.ShortcutKeys = shortcut;
                item.ShowShortcutKeys = true;
            }
            item.Click += (_, _) => action();
            return item;
        }

        private void ApplySettingsToUi()
        {
            // Set here rather than in the constructor so it follows the setting
            // the instant it is changed, without needing a restart.
            var title = _settings.DisplayTitle;
            if (Text != title) Text = title;

            _folderSizes.TimeoutSeconds = _settings.FolderSizeTimeoutSeconds;

            // FontSize was persisted, clamped and offered in the settings file,
            // but never actually reached a control — changing it did nothing at
            // all. The list is where it matters: that is what gets read.
            var wanted = Math.Clamp(_settings.FontSize, 7, 24);
            if (Math.Abs(Font.SizeInPoints - wanted) > 0.01f)
            {
                var scaled = new Font(Font.FontFamily, wanted, Font.Style);
                Font = scaled;

                // Only fonts we made are ours to release; the form's original
                // belongs to WinForms. Swapping settings repeatedly would
                // otherwise leak a GDI handle every time.
                _ownedFont?.Dispose();
                _ownedFont = scaled;
            }

            // Name, then Size, then Type, then Modified. A screen reader reads a
            // row in column order, so this is literally the sentence you hear on
            // every arrow press: "file.flac, Size 33.5 megabytes, Type FLAC file,
            // Modified 4 months ago". Size sits directly after the name because
            // it is the detail worth hearing first.
            foreach (var pane in _panes)
            {
                pane.TypeAhead.TimeoutMilliseconds = _settings.TypeAheadMilliseconds;

                pane.List.Columns.Clear();
                pane.List.Columns.Add("Name", 420);
                pane.List.Columns.Add("Size", 150);
                pane.List.Columns.Add("Modified", 160);
                InvalidateItemCache(pane);
            }
        }

        /// <summary>
        /// Puts the window back where it was, at the size it was.
        ///
        /// There was a preference for this and there is not any more: a window
        /// that comes back where you left it is not a taste, it is what a window
        /// does, and the switch only ever had one useful setting. A position
        /// that is no longer on any screen — a monitor unplugged since — is
        /// ignored rather than restored, which is the case the switch was
        /// really there for.
        /// </summary>
        private void RestoreWindowGeometry()
        {
            // A Form defaults to 300 by 300, which is far too small to read a file
            // list in.
            MinimumSize = new Size(520, 380);
            Width = Math.Max(_settings.WindowWidth, MinimumSize.Width);
            Height = Math.Max(_settings.WindowHeight, MinimumSize.Height);

            if (_settings.WindowX >= 0 && _settings.WindowY >= 0 &&
                IsOnAScreen(_settings.WindowX, _settings.WindowY))
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(_settings.WindowX, _settings.WindowY);
            }
        }

        /// <summary>
        /// Writes the window's size and position into the settings, if it has
        /// either — a minimised or maximised window's Location is not where it
        /// will be when it comes back.
        ///
        /// One method, because it was two copies of the same four lines in the
        /// two places that close the window, and a third would have been added
        /// the next time something else did.
        /// </summary>
        private void CaptureWindowGeometry()
        {
            if (WindowState != FormWindowState.Normal) return;

            _settings.WindowX = Location.X;
            _settings.WindowY = Location.Y;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }

        private static bool IsOnAScreen(int x, int y) =>
            Screen.AllScreens.Any(s => s.WorkingArea.Contains(new Point(x + 40, y + 40)));

        // ---------- Virtual list plumbing ----------

        private ListViewItem BuildVirtualItem(Pane pane, int index)
        {
            PerfCounters.RetrieveCall();

            if (index < 0 || index >= pane.Entries.Count)
                return new ListViewItem(string.Empty);

            if (index < pane.ItemCache.Length && pane.ItemCache[index] is { } cached)
            {
                PerfCounters.CacheHit();
                return cached;
            }

            long startedAt = PerfCounters.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var entry = pane.Entries[index];

            // Must match the column order set in ApplySettingsToUi.
            var cells = new List<string>(4) { DisplayNameOf(entry) };

            cells.Add(entry.SizeOverride
                ?? (entry.IsDir ? "" : SizeFormatter.Format(entry.Size, _settings.SizeUnits)));

            cells.Add(entry.Modified == default
                    ? ""
                    : TimeFormatter.Format(entry.Modified, _settings.VerboseModifiedInfo));

            var item = new ListViewItem(cells[0]) { Tag = entry.Path };
            for (int c = 1; c < cells.Count; c++) item.SubItems.Add(cells[c]);

            if (index < pane.ItemCache.Length)
            {
                // An unbounded cache is the one thing a virtual list must not
                // have: it keeps a ListViewItem and its sub-items alive for every
                // row ever painted, so a thirty-thousand file folder held hundreds
                // of megabytes for rows nobody was looking at. Bounded to far more
                // than any viewport, and simply emptied when it fills — cheap, and
                // rare enough not to matter.
                if (pane.CachedCount >= MaxCachedItems) ClearItemCache(pane);

                if (pane.ItemCache[index] == null) pane.CachedCount++;
                pane.ItemCache[index] = item;
            }

            if (PerfCounters.Enabled)
                PerfCounters.ItemBuilt(System.Diagnostics.Stopwatch.GetTimestamp() - startedAt);

            return item;
        }

        /// <summary>
        /// Comfortably more rows than any window can show, so scrolling never
        /// evicts something about to be asked for again.
        /// </summary>
        private const int MaxCachedItems = 4096;

        private static void ClearItemCache(Pane pane)
        {
            Array.Clear(pane.ItemCache, 0, pane.ItemCache.Length);
            pane.CachedCount = 0;
        }

        /// <summary>Drops one row's cached rendering so it is rebuilt on next paint.</summary>
        private static void EvictItem(Pane pane, int index)
        {
            if (index < 0 || index >= pane.ItemCache.Length) return;
            if (pane.ItemCache[index] == null) return;
            pane.ItemCache[index] = null;
            pane.CachedCount--;
        }

        private static void InvalidateItemCache(Pane pane)
        {
            pane.ItemCache = new ListViewItem?[pane.Entries.Count];
            pane.CachedCount = 0;
            if (pane.List.IsHandleCreated) pane.List.Invalidate();
        }

        /// <summary>
        /// Replaces a pane's contents and decides which row to land on.
        ///
        /// <paramref name="preferPath"/> is how you come back to where you were:
        /// the folder you stepped up out of, the row you were on before a
        /// refresh, or a file that was just created. Only when there is nothing to
        /// return to does this fall back to the first row.
        /// </summary>
        private void SetEntries(Pane pane, List<Entry> entries, string? preferPath = null)
        {
            pane.Entries = entries;
            pane.ItemCache = new ListViewItem?[entries.Count];
            pane.CachedCount = 0;

            var index = new Dictionary<string, int>(entries.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++) index[entries[i].Path] = i;
            pane.IndexByPath = index;

            // Resizing a virtual list fires a selection change per affected row.
            // Each of those used to walk the whole selection to re-total the
            // status bar, so replacing twenty thousand rows cost twenty thousand
            // walks of twenty thousand rows. Held off until the list is settled.
            _suppressStatus++;
            try
            {
                pane.List.BeginUpdate();
                try
                {
                    pane.List.VirtualListSize = 0;   // drop stale selection first
                    pane.List.VirtualListSize = entries.Count;
                }
                finally { pane.List.EndUpdate(); }
            }
            finally { _suppressStatus--; }

            // One decision, one move. Selecting the first row and then correcting
            // it would be two focus changes, and a screen reader reads both.
            int target = -1;

            // Letters typed while the folder was still loading. They beat the
            // remembered row, because they are the more recent instruction — you
            // asked for this folder and then asked for a name inside it, and only
            // the network made those two things arrive in the wrong order.
            var pending = pane.TypeAhead.Current(Environment.TickCount64);
            if (pending.Length > 0 && entries.Count > 0)
            {
                int found = TypeAhead.Find(entries.Count, i => DisplayNameOf(entries[i]), pending, -1);
                if (found >= 0) target = found;
            }

            if (target < 0 && preferPath != null &&
                pane.IndexByPath.TryGetValue(preferPath, out int remembered))
                target = remembered;
            else if (target < 0 && _settings.SelectFirstItemOnNavigate && entries.Count > 0)
                target = 0;

            if (target >= 0) SelectIndex(pane, target);

            if (pane == Active) UpdateStatus();
        }

        // ---------- Tabs ----------

        /// <summary>
        /// Gives both lists a window handle as soon as the form has one.
        ///
        /// A hidden control does not create its handle until it is shown, and a
        /// virtual ListView with no handle cannot hold selection state — the
        /// selection is a property of the native control, not of anything
        /// managed. So the second tab was loaded and its row chosen while it had
        /// nowhere to put that, and switching to it for the first time landed on
        /// a list with nothing selected and nothing for a screen reader to read.
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            foreach (var pane in _panes)
            {
                try
                {
                    var handle = pane.List.Handle;

                    // WinForms leaves the list control unbuffered, so every row
                    // that scrolls into view is painted straight to the screen and
                    // a held arrow key turns into a flicker of partial repaints
                    // that the window then has to keep up with. One message makes
                    // the common control draw each frame off-screen first, which
                    // is the single biggest difference to how fast scrolling feels.
                    SendMessage(handle, LVM_SETEXTENDEDLISTVIEWSTYLE,
                        new IntPtr(LVS_EX_DOUBLEBUFFER), new IntPtr(LVS_EX_DOUBLEBUFFER));

                    // The same theme File Explorer puts on its own list. Besides
                    // looking right, it selects the lighter-weight drawing path in
                    // the common control — full-row hot tracking and selection
                    // painted by the theme rather than by the classic code.
                    SetWindowTheme(handle, "Explorer", null);
                }
                catch (ObjectDisposedException) { }
            }
        }

        private void ShowActivePane()
        {
            for (int i = 0; i < _panes.Length; i++)
                _panes[i].List.Visible = i == _activeIndex;
        }

        private void SwitchTab() => SetActiveTab(1 - _activeIndex);

        private void SetActiveTab(int index)
        {
            if (_initialising) return;
            if (index < 0 || index >= _panes.Length || index == _activeIndex) return;

            _activeIndex = index;

            // Showing one list and hiding the other is a layout change, and the
            // form would otherwise re-lay-out every child — menu, status bar, both
            // lists — twice per switch. Held down, Tab repeats about thirty times
            // a second, and the relayouts were the lag.
            SuspendLayout();
            try
            {
                ShowActivePane();
                _pathLabel.Text = PathLabelFor(Active);

                // Named before focus arrives, so what gets read is this pane's
                // folder and not whatever it was called last time.
                UpdatePaneName(Active);
            }
            finally { ResumeLayout(performLayout: false); }

            Active.List.Focus();

            // Deferred, both of them. Totalling the selection and building the
            // announcement are wasted on a tab you are passing through on the way
            // to somewhere else; only the one you stop on is worth the work.
            ScheduleStatusUpdate();
            ScheduleTabAnnouncement();

            // A tab that changed while it was hidden is re-read now that it is
            // being looked at.
            ApplyPendingRefresh();
        }

        private System.Windows.Forms.Timer? _tabAnnounceTimer;

        /// <summary>
        /// Says which tab you landed on, once you have landed.
        ///
        /// Announcing on every switch meant holding Tab queued an announcement per
        /// repeat; each one interrupts the last, so nothing was ever actually
        /// heard and the screen reader spent the whole time being cut off.
        /// </summary>
        private void ScheduleTabAnnouncement()
        {
            if (IsDisposed) return;
            if (!_settings.SpeakEnabled || !_settings.SpeakTabSwitch) return;

            if (_tabAnnounceTimer == null)
            {
                _tabAnnounceTimer = new System.Windows.Forms.Timer { Interval = 120 };
                _tabAnnounceTimer.Tick += (_, _) =>
                {
                    _tabAnnounceTimer?.Stop();
                    if (!IsDisposed) AnnounceTab();
                };
            }

            _tabAnnounceTimer.Stop();
            _tabAnnounceTimer.Start();
        }

        private void AnnounceTab()
        {
            if (!_settings.SpeakEnabled || !_settings.SpeakTabSwitch) return;
            var name = Active.SearchTerm is { } term ? $"Results for {term}"
                : IsDrivesView ? "Drives"
                : FolderDisplayName(Active.CurrentPath);

            // Routed rather than spoken straight at NVDA, so where this goes is
            // decided in the one place that decides it for everything else.
            // SpeakTabSwitch still gates the group in front of the catalogue, so
            // both have to agree before anything is said.
            AnnounceOperation("nav.tab",
                $"Tab {_activeIndex + 1}, {name}, {NameRules.Items(Active.Entries.Count)}");
        }

        // ---------- Navigation ----------

        private void Navigate(string path) => _ = NavigateAsync(path);

        /// <summary>
        /// Retires everything the pane had in flight and hands back the token for
        /// what replaces it.
        ///
        /// The retired source is cancelled but never disposed. Enumerations and
        /// folder-size walks already running hold tokens taken from it, and
        /// linking a token from a disposed source throws — so disposing here
        /// turned a fast double navigation into an ObjectDisposedException on a
        /// background thread. It holds no unmanaged handle to reclaim.
        ///
        /// Only this pane's work is cancelled. The old code reached for
        /// FolderSizeCalculator.CancelAll here, which also stopped the *other*
        /// tab's sizes for no reason.
        /// </summary>
        private static CancellationToken BeginPaneWork(Pane pane)
        {
            try { pane.EnumCts?.Cancel(); } catch { }
            var fresh = new CancellationTokenSource();
            pane.EnumCts = fresh;

            // Whatever was loading has been called off. A cancelled load clears
            // the flag only if it is still the pane's work — see LoadEntriesAsync
            // — so the replacement is where it goes down.
            pane.Loading = false;
            return fresh.Token;
        }

        /// <summary>
        /// Notes which row this pane is on, so returning to the folder later can
        /// put the cursor back on it.
        /// </summary>
        private static void RememberPosition(Pane pane)
        {
            if (string.IsNullOrEmpty(pane.CurrentPath) || pane.CurrentPath == DrivesPath) return;
            pane.Positions.Remember(pane.CurrentPath, FocusedPathOf(pane));
        }

        /// <summary>
        /// Which row to land on when arriving at <paramref name="destination"/>
        /// from <paramref name="cameFrom"/>.
        ///
        /// Stepping up wins over anything else: leaving a folder should put the
        /// cursor on the folder just left, which is both where the attention
        /// already is and the row you would need to get back. Otherwise, a folder
        /// visited before reopens on the row it was left on.
        /// </summary>
        private static string? PreferredRowFor(Pane pane, string destination, string? cameFrom) =>
            NavigationHistory.ChildOnPathTo(destination, cameFrom) ?? pane.Positions.Recall(destination);

        private async Task NavigateAsync(string path, string? preferPath = null,
            Pane? onPane = null, bool quiet = false, bool keepFocus = false)
        {
            // The pane is fixed at the start. "Whichever tab is in front" was
            // read again after waits of up to eight seconds, so a navigation
            // that fell back from a dead share landed in the other tab if
            // somebody had switched meanwhile.
            var pane = onPane ?? Active;
            var cameFrom = pane.CurrentPath;

            // And a navigation that is no longer the latest one gives way. The
            // existence check ran before anything else was cancelled, so a slow
            // one finishing late took the window back to a folder somebody had
            // already moved on from.
            int mine = ++pane.NavigationId;

            // Going anywhere is leaving the results. Cleared here rather than at
            // each of the places that navigate, because every one of them ends up
            // in an ordinary folder and a flag left set would keep the watcher
            // switched off and Escape claiming there was somewhere to go back to.
            bool wasSearching = pane.SearchTerm != null;
            var searchedFor = pane.SearchTerm;
            bool searchTruncated = pane.SearchTruncated;
            pane.SearchTerm = null;
            pane.SearchTruncated = false;

            // Recorded before the list is replaced, while the focused row still
            // belongs to the folder being left — but never *from* a set of
            // results, whose rows belong to folders all over the tree and would
            // overwrite the position the search was started from with one of them.
            if (!wasSearching) RememberPosition(pane);

            // Typing continues into a new folder otherwise, and the letters from
            // the last one are never what was meant.
            pane.TypeAhead.Reset();

            // A new folder starts with a clean record: failures counted against
            // the last one say nothing about this one.
            _consecutiveProbeFailures = 0;

            if (path == DrivesPath)
            {
                var driveToken = BeginPaneWork(pane);
                pane.Searching = false;

                pane.CurrentPath = DrivesPath;
                if (pane == Active) _pathLabel.Text = "Drives";

                // Nothing to watch: the drive list is not a folder.
                DisarmWatcher(pane);

                // Coming up out of a drive lands on that drive, not on A:; and a
                // reload of the list itself stays on the drive it was on.
                LoadDrivesInto(pane, driveToken,
                    preferPath ?? (cameFrom == DrivesPath ? FocusedPathOf(pane) : SafePathRoot(cameFrom)));

                return;
            }

            try
            {
                // Without a trailing separator, except at a root: "C:\Users\" and
                // "C:\Users" were two different folders to every comparison here.
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

                pane.Probing = mine;
                var reachable = await ProbeDirectoryAsync(full, NavigationProbeMs);
                if (IsDisposed || pane.NavigationId != mine) return;

                if (reachable != FolderProbe.Present)
                {
                    // The folder may simply be gone — an unplugged drive, a
                    // dropped share. Fall back to the nearest level that is
                    // still there rather than leaving a dead view on screen.
                    var fallback = await NearestExistingAncestorAsync(full);
                    if (IsDisposed || pane.NavigationId != mine) return;

                    // Which of the two it was is the useful half of the sentence.
                    // A share that timed out is still there and will answer again
                    // when the server wakes up; one that answered "no" is not.
                    if (reachable == FolderProbe.NotResponding)
                        AnnounceOperation("nav.offline",
                            $"{FolderDisplayName(full)} is not responding, going to {DescribePath(fallback)}");
                    else
                        AnnounceOperation("nav.gone",
                            $"{FolderDisplayName(full)} is not available, going to {DescribePath(fallback)}");

                    // Back to the drive list on the drive that was not ready, not on A:.
                    if (fallback != full)
                        await NavigateAsync(fallback,
                            preferPath: fallback == DrivesPath ? Path.GetPathRoot(full) : null, onPane: pane);
                    return;
                }

                var token = BeginPaneWork(pane);

                // Replacing the pane's work cancels a search that was running in
                // it, and that search's own clean-up no longer owns the flag.
                pane.Searching = false;

                pane.CurrentPath = full;
                if (pane == Active) _pathLabel.Text = full;

                bool arrived = await LoadEntriesAsync(
                    pane, full, token, preferPath ?? PreferredRowFor(pane, full, cameFrom), quiet, keepFocus);
                if (token.IsCancellationRequested || IsDisposed) return;

                // A folder we could not read leaves the previous one on screen, so
                // that is where we still are. Without putting the path back, the
                // window claimed to be somewhere it had never managed to open:
                // the list showed the old folder, but Backspace went up from the
                // new one and F5 tried to reload the folder that had just been
                // refused.
                // Watch what we actually landed on, and only once we know we
                // landed on it — arming before the load would leave a watcher on
                // a folder that turned out to be unreadable.
                if (arrived && pane.CurrentPath == full) ArmWatcher(pane, full);

                if (!arrived)
                {
                    // On the very first navigation there is nowhere to go back to,
                    // and leaving the path empty would make Directory.GetParent
                    // throw the next time Backspace was pressed. The drive list is
                    // always somewhere valid to be.
                    var restore = string.IsNullOrEmpty(cameFrom) ? DrivesPath : cameFrom;
                    pane.CurrentPath = restore;

                    // Still showing the results, so still in them: Escape, Enter
                    // and the watcher all go by the term.
                    if (wasSearching)
                    {
                        pane.SearchTerm = searchedFor;
                        pane.SearchTruncated = searchTruncated;
                    }

                    if (pane == Active) _pathLabel.Text = PathLabelFor(pane);
                    return;
                }

            }
            catch (Exception ex)
            {
                Announce($"Could not open folder: {ex.Message}", isError: true);
            }
            finally
            {
                if (pane.Probing == mine) pane.Probing = 0;
            }
        }

        /// <summary>
        /// Existence check that cannot hang the window.
        ///
        /// Directory.Exists on a share whose server has gone away blocks until
        /// the SMB stack gives up, which is many seconds. Called from the UI
        /// thread that is exactly the freeze this app exists to avoid — and it
        /// would happen during a disconnect, the one moment it matters most.
        /// </summary>
        private static async Task<bool> DirectoryExistsAsync(string path, int timeoutMs = 3000) =>
            await ProbeDirectoryAsync(path, timeoutMs) == FolderProbe.Present;

        /// <summary>
        /// The three answers an existence check can really give.
        ///
        /// <see cref="DirectoryExistsAsync"/> collapses the last two into false,
        /// which is right for a caller that only wants somewhere to go — but it
        /// throws away the one distinction worth telling a person about. A folder
        /// that is <em>gone</em> and a server that is <em>not answering</em> are
        /// different situations with different remedies, and saying "not
        /// available" for a NAS that is merely asleep sends somebody looking for
        /// a file they have not lost.
        /// </summary>
        private enum FolderProbe { Present, Missing, NotResponding }

        private static async Task<FolderProbe> ProbeDirectoryAsync(string path, int timeoutMs = 3000)
        {
            if (string.IsNullOrEmpty(path)) return FolderProbe.Missing;

            var probe = Task.Run(() =>
            {
                try { return Directory.Exists(path); }
                catch { return false; }
            });

            var finished = await Task.WhenAny(probe, Task.Delay(timeoutMs));

            // A probe that has not answered in time is treated as unavailable;
            // the worker is left to finish and be collected on its own. It is
            // never waited on again — reading probe.Result here would reintroduce
            // exactly the block the timeout exists to escape.
            if (finished != probe) return FolderProbe.NotResponding;
            return probe.Result ? FolderProbe.Present : FolderProbe.Missing;
        }

        /// <summary>Walks up until it finds somewhere that still answers.</summary>
        private static async Task<string> NearestExistingAncestorAsync(string path)
        {
            try
            {
                var current = Directory.GetParent(path);
                while (current != null)
                {
                    if (await DirectoryExistsAsync(current.FullName)) return current.FullName;
                    current = current.Parent;
                }
            }
            catch { }
            return DrivesPath;
        }

        // ---------- Watching the folder ----------

        /// <summary>
        /// Non-zero while this application is itself changing the folder.
        ///
        /// Our own commands refresh when they finish, and they know better than
        /// the watcher when that is: a paste of ten thousand files would
        /// otherwise fire events all the way through and have us rebuilding the
        /// list on top of the copy that is still running.
        /// </summary>
        private int _suppressWatch;

        /// <summary>
        /// Folders a transfer is writing into right now, and how many transfers
        /// are writing into each.
        ///
        /// This is <see cref="_suppressWatch"/> narrowed to the one folder it
        /// was ever about, and narrowing it is what makes a background copy
        /// bearable. The counter above stops the watcher for the whole window,
        /// which was harmless while a transfer held the window modal and nobody
        /// could be looking at anything anyway. Now that a copy runs for an hour
        /// while you carry on working, that same counter would mean an hour of
        /// no folder anywhere noticing that anything had changed — a file
        /// arriving from another program, a download finishing, a rename done in
        /// Explorer, none of it appearing until something forced a reload.
        ///
        /// Keyed by folder because that is the folder the ten thousand events
        /// are coming from. Counted rather than a set, because two transfers can
        /// perfectly well be filling the same folder and the first one finishing
        /// is not the end of the noise.
        ///
        /// Raised and released around a try, always, for the same reason the
        /// counter is: a raise left standing is a folder that never refreshes
        /// again, with nothing on screen to connect it back to the copy that
        /// went wrong.
        /// </summary>
        private readonly Dictionary<string, int> _transferTargets =
            new(StringComparer.OrdinalIgnoreCase);

        private void BeginTransferTo(string destination)
        {
            if (string.IsNullOrEmpty(destination)) return;
            _transferTargets.TryGetValue(destination, out int running);
            _transferTargets[destination] = running + 1;
        }

        private void EndTransferTo(string destination)
        {
            if (string.IsNullOrEmpty(destination)) return;
            if (!_transferTargets.TryGetValue(destination, out int running)) return;

            if (running <= 1) _transferTargets.Remove(destination);
            else _transferTargets[destination] = running - 1;
        }

        private bool IsTransferTarget(string? path) =>
            !string.IsNullOrEmpty(path) && _transferTargets.ContainsKey(path);

        /// <summary>How many transfers have a window open. Named for the sentence it ends up in.</summary>
        public int RunningTransfers { get; private set; }

        private System.Windows.Forms.Timer? _watchTimer;

        /// <summary>
        /// Coarse on purpose. File operations arrive as bursts of events — one
        /// paste is hundreds — and the only thing worth doing is reading the
        /// folder once after they stop.
        /// </summary>
        private const int WatchSettleMs = 600;

        /// <summary>
        /// Starts watching a folder for changes made by anything other than us.
        ///
        /// The app already refreshed after its own copy, delete or rename, which
        /// covers its own commands and nothing else. Anything done through the
        /// Windows context menu finishes *after* the shell hands control back, so
        /// refreshing at that point was always a moment too early; and a file
        /// written by another program never showed up at all. Either way the list
        /// disagreed with the disk until it was refreshed by hand.
        /// </summary>
        private void ArmWatcher(Pane pane, string path)
        {
            if (string.IsNullOrEmpty(path) || path == DrivesPath) { DisarmWatcher(pane); return; }

            // A refresh re-navigates to the same folder, so this runs every time
            // the list is rebuilt. Tearing the watcher down and building another
            // one for the same path would mean a new directory handle on every
            // refresh, and a window between them where changes are missed.
            if (pane.Watcher != null &&
                string.Equals(pane.Watcher.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                // The rebuild just took account of whatever was pending.
                pane.NeedsRefresh = false;
                pane.ReloadQuietly = false;
                return;
            }

            DisarmWatcher(pane);

            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                    IncludeSubdirectories = false,
                    // Room for a burst. When the buffer overflows the change list
                    // is lost and only an Error arrives, which costs a full reread.
                    InternalBufferSize = 64 * 1024,
                };

                void Touched(object? _, FileSystemEventArgs __) => pane.NeedsRefresh = true;

                watcher.Created += Touched;
                watcher.Deleted += Touched;
                watcher.Changed += Touched;
                watcher.Renamed += (_, _) => pane.NeedsRefresh = true;

                // An error is usually a lost burst or a share that went away.
                // Both mean the list can no longer be trusted, so re-read it.
                watcher.Error += (_, _) => pane.NeedsRefresh = true;

                watcher.EnableRaisingEvents = true;
                pane.Watcher = watcher;
            }
            catch
            {
                // Some filesystems will not support watching. That is a reason to
                // do without live updates, not a reason to fail to show a folder.
            }

            EnsureWatchTimer();
        }

        private static void DisarmWatcher(Pane pane)
        {
            var watcher = pane.Watcher;
            pane.Watcher = null;
            pane.NeedsRefresh = false;
            pane.ReloadQuietly = false;

            if (watcher == null) return;
            try { watcher.EnableRaisingEvents = false; } catch { }
            try { watcher.Dispose(); } catch { }
        }

        private void EnsureWatchTimer()
        {
            if (_watchTimer != null || IsDisposed) return;

            _watchTimer = new System.Windows.Forms.Timer { Interval = WatchSettleMs };
            _watchTimer.Tick += (_, _) => ApplyPendingRefresh();
            _watchTimer.Start();
        }

        /// <summary>
        /// Re-reads the folder if anything has touched it since we last looked.
        ///
        /// Only the pane on screen is refreshed here. The other one is left
        /// marked and re-read when it is switched to, because rebuilding a list
        /// nobody is looking at is work for its own sake — and because navigation
        /// is written in terms of the active pane.
        /// </summary>
        private async void ApplyPendingRefresh()
        {
            if (IsDisposed || _suppressWatch > 0) return;

            var pane = Active;
            if (!pane.NeedsRefresh) return;

            // A folder being copied into stays marked and is re-read once, by
            // the transfer, when it finishes. Rebuilding it on the way through
            // is a list that moves under the cursor a hundred times and is wrong
            // at every one of them. Everything else on screen keeps watching,
            // which is the point of asking about this folder rather than about
            // whether any transfer is running at all.
            if (IsTransferTarget(pane.CurrentPath)) return;

            // A pane showing results is not showing this folder, and rebuilding
            // it from the folder would throw the results away — silently, because
            // something changed on disk that nobody in front of the screen did.
            // The mark is left standing, so leaving the results re-reads then.
            if (pane.SearchTerm != null) return;

            // Nor while a search is still gathering: a reload is a navigation,
            // and a navigation cancels it — silently, so the results never came.
            if (pane.Searching) return;

            // Nor while the folder is still arriving, or a navigation is still
            // finding out whether its folder is there; the mark stays for after.
            if (pane.Loading || pane.Probing != 0) return;

            // Nor behind somebody's back. With the window not in front the mark
            // stays, and this runs again on the next tick after it is.
            if (Form.ActiveForm != this) return;

            pane.NeedsRefresh = false;
            bool ours = pane.ReloadQuietly;
            pane.ReloadQuietly = false;

            // Read before the reload: the pane can be switched while it runs, and
            // the folder that changed is the one being named.
            var folder = DescribePath(pane.CurrentPath);

            // Keeps the cursor where it is, so a file appearing elsewhere in the
            // folder does not move you.
            await RefreshAsync(folder: pane.CurrentPath, quiet: true);
            if (IsDisposed) return;

            // Said after the reload rather than before it, so the list already
            // agrees with the announcement — and so it is not immediately
            // overwritten in the status bar by the reload's own item count.
            if (!ours) AnnounceOperation("nav.changed", $"{folder} changed");
        }

        private static string? SafePathRoot(string? path)
        {
            if (string.IsNullOrEmpty(path) || path == DrivesPath) return null;
            try { return Path.GetPathRoot(path); } catch { return null; }
        }

        private static string DescribePath(string path) =>
            path == DrivesPath ? "Drives" : FolderDisplayName(path);

        private bool _availabilityCheckRunning;

        /// <summary>
        /// How long the user is willing to wait for a folder they asked to open.
        /// Longer than the idle poll: they made a deliberate request, and the
        /// window stays responsive while it runs, so giving up after three
        /// seconds on a share that was merely busy helped nobody.
        /// </summary>
        private const int NavigationProbeMs = 8000;

        /// <summary>
        /// Consecutive failed idle probes before the folder is treated as gone.
        ///
        /// One slow answer is not a missing share. A busy server can easily take
        /// longer than the probe allows, and acting on a single timeout meant a
        /// share under load kicked the user up a level — repeatedly, every few
        /// seconds, in exactly the situation this app exists to handle well.
        /// </summary>
        private const int FailedProbesBeforeGivingUp = 2;

        private int _consecutiveProbeFailures;

        /// <summary>
        /// Idle check so a drive pulled while nothing is happening is noticed.
        /// Entirely off the UI thread, and never overlapping itself: on a dead
        /// share each probe can take seconds, and a 4-second timer would
        /// otherwise pile them up faster than they complete.
        /// </summary>
        private async void CheckCurrentPathStillExists()
        {
            if (IsDisposed || IsDrivesView || _availabilityCheckRunning) return;

            var path = Active.CurrentPath;
            if (string.IsNullOrEmpty(path)) return;

            _availabilityCheckRunning = true;
            try
            {
                if (await DirectoryExistsAsync(path))
                {
                    _consecutiveProbeFailures = 0;
                    return;
                }
                if (IsDisposed) return;

                // Confirm the pane is still showing the folder that vanished;
                // the user may have navigated away while the probe was running.
                if (Active.CurrentPath != path || IsDrivesView) return;

                if (++_consecutiveProbeFailures < FailedProbesBeforeGivingUp) return;
                _consecutiveProbeFailures = 0;

                var fallback = await NearestExistingAncestorAsync(path);
                if (IsDisposed || Active.CurrentPath != path) return;

                Announce($"{FolderDisplayName(path)} is no longer available, going to {DescribePath(fallback)}", isError: true);
                await NavigateAsync(fallback);
            }
            finally { _availabilityCheckRunning = false; }
        }

        private static string FolderDisplayName(string path)
        {
            if (path == DrivesPath) return "Drives";
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? path : name;
        }

        private void LoadDrivesInto(Pane pane, CancellationToken token, string? preferPath = null)
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (Exception ex) { Announce($"Could not list drives: {ex.Message}", isError: true); return; }

            // Letters appear at once; the slow per-drive queries follow.
            var entries = drives
                .Select(d => new Entry(d.Name, d.Name.TrimEnd(Path.DirectorySeparatorChar), true, 0, default)
                {
                    SizeOverride = "",
                    DisplayOverride = DriveRowName(d.Name, "checking"),
                })
                .ToList();

            SetEntries(pane, entries, preferPath);   // totals the status bar itself
            pane.LastLoadedPath = DrivesPath;

            // Read aloud, exactly as arriving in a folder is. Landing on row 0 of
            // the drive list from row 0 of a folder is not a focus change to
            // anything watching from outside — the index has not moved — so
            // Backspace out of C:\ produced a keystroke and not one word.
            FocusListAndRow(pane, readRowAloud: true);

            foreach (var drive in drives)
                _ = FillDriveDetailsAsync(pane, drive, token);
        }

        /// <summary>
        /// A drive's whole row, as Conner asked for it: the letter, its name if it
        /// has one, and how full it is — "D, Google Drive, 1 terabyte of 5
        /// terabytes used". All of it is in the name, and the size column is
        /// left empty, so nothing is said twice.
        /// </summary>
        internal static string DriveRowName(string root, string detail) =>
            root.TrimEnd(Path.DirectorySeparatorChar).TrimEnd(':') + ", " + detail;

        /// <summary>
        /// "1 terabyte of 5 terabytes used", always in words: a drive's size is
        /// read out, and "TB" is read as two letters.
        /// </summary>
        internal static string UsedOf(long used, long total) =>
            $"{SizeFormatter.Format(Math.Max(0, used), SizeUnitStyle.FullWords)} of " +
            $"{SizeFormatter.Format(total, SizeUnitStyle.FullWords)} used";

        private async Task FillDriveDetailsAsync(Pane pane, DriveInfo drive, CancellationToken token)
        {
            string size = "", free;

            // Google Drive answers for itself, because Windows cannot answer for
            // it. The letter is `subst` onto a directory and a subst drive is not
            // a volume, so DriveInfo hands back the *host* disk's size, free
            // space and label — a 5TB Drive on a 500GB machine listed as "431 GB
            // free of 475 GB, Local Disk", which is the C: drive wearing a
            // different letter. The real numbers came back in the same About
            // response that proved the token at mount.
            if (DriveQuotaFor(drive) is (long used, long limit) && limit > 0)
            {
                ApplyDetails(pane, drive, "Google Drive, " + UsedOf(used, limit), "", token);
                return;
            }

            try
            {
                var probe = Task.Run(() =>
                {
                    if (!drive.IsReady) return ("not ready", "");
                    long total = drive.TotalSize;
                    // The volume's name, so two USB sticks are told apart by
                    // more than their letter: "E: Backup, 12 GB free".
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "" : drive.VolumeLabel.Trim() + ", ";
                    return (label + UsedOf(total - drive.TotalFreeSpace, total), "");
                }, token);

                var finished = await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(8), token));
                if (finished != probe) free = "not responding";
                else (free, size) = await probe;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { free = ex.Message; }

            ApplyDetails(pane, drive, free, size, token);
        }

        /// <summary>
        /// Drive's own used and total bytes when this letter is the Drive mount,
        /// or null for an ordinary volume.
        ///
        /// Matched on the letter rather than on the drive type, because the type
        /// is the giveaway that it cannot be trusted: a subst drive reports
        /// itself as Fixed, exactly like the disk underneath it.
        /// </summary>
        private (long Used, long Limit)? DriveQuotaFor(DriveInfo drive)
        {
            var letter = Drive?.Letter;
            if (string.IsNullOrEmpty(letter) || Drive == null) return null;
            if (Drive.QuotaLimit <= 0) return null;

            // "G:" against "G:\".
            var trimmed = drive.Name.TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(trimmed, letter.TrimEnd(Path.DirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase)
                ? (Drive.QuotaUsed, Drive.QuotaLimit)
                : null;
        }

        /// <summary>Writes one drive's details into the row, on the UI thread.</summary>
        private void ApplyDetails(Pane pane, DriveInfo drive,
            string free, string size, CancellationToken token)
        {
            if (token.IsCancellationRequested || IsDisposed) return;

            void Apply()
            {
                if (pane.CurrentPath != DrivesPath) return;
                for (int i = 0; i < pane.Entries.Count; i++)
                {
                    if (!string.Equals(pane.Entries[i].Path, drive.Name, StringComparison.OrdinalIgnoreCase)) continue;

                    pane.Entries[i].DisplayOverride = DriveRowName(drive.Name, free);
                    pane.Entries[i].SizeOverride = size;

                    EvictItem(pane, i);
                    if (pane.List.IsHandleCreated) pane.List.RedrawItems(i, i, true);
                    break;
                }
            }

            if (InvokeRequired)
            {
                try { BeginInvoke(Apply); } catch (ObjectDisposedException) { }
            }
            else Apply();
        }

        /// <summary>
        /// Fills a pane with a folder's contents.
        ///
        /// Returns false when the folder was not opened and the pane is still
        /// showing whatever it was showing before, so the caller can put the
        /// recorded path back to match.
        /// </summary>
        private async Task<bool> LoadEntriesAsync(
            Pane pane, string path, CancellationToken token, string? preferPath = null,
            bool quiet = false, bool keepFocus = false)
        {
            List<Entry> entries;

            // Captured before anything is shown, for the decisions further down.
            int previousCount = pane.Entries.Count;
            bool sameFolder = string.Equals(pane.LastLoadedPath, path, StringComparison.OrdinalIgnoreCase);
            var previousRow = sameFolder ? FocusedPathOf(pane) : null;
            if (keepFocus && sameFolder) preferPath = previousRow ?? preferPath;

            // Rows go on screen as they arrive. The listing is produced on a
            // worker a batch at a time — a page from Google, a few hundred
            // entries from the disk — and the window shows what there is so far,
            // so a big folder or a slow share fills in rather than showing the
            // last folder until the whole answer is in. A folder that lists
            // quickly still appears in one go: nothing is shown until the first
            // pause is over, and by then a fast listing has finished.
            // Only for arriving somewhere. A reload of the folder already on screen
            // keeps what is there until the new listing is whole: shown in pieces
            // it cut the list short, dropped the selection and read the row out,
            // which is the folder watcher talking over everything.
            var partial = new ProgressiveLoad(this, pane, path, preferPath, token, enabled: !sameFolder);

            // Rows of this folder already on screen when the listing failed are
            // this folder, not the last one: returning false put the path back
            // under a list showing the new folder, and a paste or Backspace then
            // acted on the other. The part is kept as what the folder showed.
            bool PartOfIt()
            {
                if (!partial.Shown || token.IsCancellationRequested) return false;
                pane.LastLoadedPath = path;

                // The first showing may have been waiting for the row to come
                // back to, which never came: land somewhere, and say it.
                if (CurrentIndex(pane) < 0 && pane.Entries.Count > 0)
                {
                    int at = preferPath != null && pane.IndexByPath.TryGetValue(preferPath, out var i) ? i : 0;
                    SelectIndex(pane, at);
                    if (pane == Active) FocusListAndRow(pane, readRowAloud: true);
                }
                return true;
            }

            pane.Loading = true;
            try
            {
                // A Google Drive folder has nothing in it until it is asked for,
                // and asking is a network listing and a placeholder per entry —
                // seconds for a big one — so it is always on a worker, and its
                // pages are what fills the list.
                var drive = Drive;
                var producing = Task.Run(() =>
                {
                    if (drive != null)
                        drive.Populate(path, page =>
                        {
                            var batch = new List<Entry>(page.Count);
                            foreach (var item in page)
                                batch.Add(new Entry(Path.Combine(path, item.Name), item.Name,
                                    item.IsFolder, item.Size, item.Modified));
                            partial.Offer(batch);
                        });

                    return EnumerateSorted(path, partial.Offer, token);
                }, token);

                entries = await partial.RunUntil(producing);
            }
            catch (OperationCanceledException) { return false; }
            catch (DirectoryNotFoundException)
            {
                // Checked after the wait, which can be seconds a level on a dead
                // share: a navigation somebody started meanwhile wins.
                var fallback = await NearestExistingAncestorAsync(path);
                if (IsDisposed || token.IsCancellationRequested) return false;
                AnnounceOperation("nav.gone",
                    $"{FolderDisplayName(path)} disappeared, going to {DescribePath(fallback)}");
                await NavigateAsync(fallback, onPane: pane);

                // True because we did end up somewhere, and the caller must not
                // overwrite the path that the fallback navigation just set.
                return true;
            }
            catch (IOException ex)
            {
                var fallback = await NearestExistingAncestorAsync(path);
                if (IsDisposed || token.IsCancellationRequested) return false;
                Announce($"{FolderDisplayName(path)} is unreadable ({ex.Message}), going to {DescribePath(fallback)}", isError: true);
                await NavigateAsync(fallback, onPane: pane);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // Readable parent, unreadable child: stay put rather than
                // bouncing the user somewhere they did not ask to go.
                AnnounceOperation("nav.denied", $"No permission to read {FolderDisplayName(path)}");
                return PartOfIt();
            }
            catch (Exception ex)
            {
                Announce($"Could not list folder: {ex.Message}", isError: true);
                return PartOfIt();
            }

            finally
            {
                // Only for the load that is still the pane's. A cancelled reload
                // of the same folder finishes its listing regardless, seconds
                // later, and clearing the flag then switched it off under the
                // load that replaced it.
                if (pane.EnumCts?.Token == token) pane.Loading = false;
            }

            if (token.IsCancellationRequested || IsDisposed) return false;

            // Captured above, before anything was shown: "this folder is empty"
            // against "this folder was already empty and still is", and where the
            // cursor was, which decides whether the screen reader will say
            // anything at all about where it lands. A reload that keeps the
            // cursor keeps it where it is *now* — taken before the reload, it was
            // wherever the cursor had been when a slow listing began, and arrowing
            // on while it ran was undone when it finished.
            if (partial.Shown)
            {
                // Already on screen, and possibly already arrowed through: the
                // final list goes in under the cursor, wherever it now is.
                partial.Finish(entries);
                pane.LastLoadedPath = path;
                if (_settings.FolderSizes == FolderSizeMode.Automatic)
                    _ = CalculateAllFolderSizesAsync(pane, token);
                PrewarmAssociations(entries);
                if (entries.Count == 0 && pane == Active) AnnounceOperation("nav.empty", "No items");
                return true;
            }

            // A reload that keeps the cursor keeps it where it is *now*, and the
            // selection with it. Read before the listing, this put the cursor
            // back where it had been when a slow listing began, and every quiet
            // reload cut a twelve-file selection down to the focused row with
            // nothing said — the next Delete acting on one file.
            List<string>? selectedNow = null;
            if (keepFocus && sameFolder)
            {
                previousRow = FocusedPathOf(pane) ?? previousRow;
                preferPath = previousRow ?? preferPath;
                selectedNow = SelectedPathsOf(pane);
            }

            SetEntries(pane, entries, preferPath);
            pane.LastLoadedPath = path;

            if (selectedNow is { Count: > 1 } && previousRow != null &&
                pane.IndexByPath.TryGetValue(previousRow, out var keptAt))
                Reselect(pane, keptAt, selectedNow);

            // Say the row, every time, on arriving anywhere.
            //
            // Three attempts at being cleverer than this all failed, and the
            // measurements are why. Relying on the control's own focus event left
            // five of six nested folders silent, because in a virtual list an
            // item's identity is its index and landing on row 0 from row 0 is not
            // a change to anything watching. Predicting when it *would* fire was
            // wrong twice — once from the landing row, once from whether the list
            // grew — leaving folders silent one way and saying the name twice the
            // other. Raising the event explicitly works in some trees and not
            // others, because the reader de-duplicates by that same unchanged
            // identity and drops it exactly when it is needed.
            //
            // So the guarantee is taken back into this application, which is the
            // only place that reliably knows a different folder is now on screen.
            // The cost is that where the reader does announce it too you hear the
            // name twice; the cost of the alternative is not hearing it at all,
            // and that is the one somebody actually reported.
            //
            // Except a quiet reload of the same folder that left the cursor on the
            // same item: that is the folder watcher, and saying the row again cut
            // the screen reader off every time something in the folder changed —
            // a download being written, a log growing — even from another window.
            bool sameRow = sameFolder && previousRow != null &&
                           string.Equals(previousRow, FocusedPathOf(pane), StringComparison.OrdinalIgnoreCase);
            FocusListAndRow(pane, readRowAloud: !(quiet && sameRow));

            // An empty folder has no row to land on, so nothing is focused and a
            // screen reader has nothing to read: arriving in one was complete
            // silence, indistinguishable from the key not having worked. Said
            // regardless of the navigation announcement setting, because this is
            // not the folder's name being repeated — it is the only thing there
            // is to say.
            //
            // Not repeated when a folder that was already empty is merely
            // re-read, which happens on every refresh and every watcher tick.
            if (entries.Count == 0 && pane == Active && (!sameFolder || previousCount > 0))
                AnnounceOperation("nav.empty", "No items");

            if (_settings.FolderSizes == FolderSizeMode.Automatic)
                _ = CalculateAllFolderSizesAsync(pane, token);

            PrewarmAssociations(entries);
            return true;
        }

        /// <summary>
        /// Resolves the handler for each extension in the folder up front, on a
        /// worker, so the very first Enter is as immediate as every one after it
        /// rather than paying a one-off association lookup.
        /// </summary>
        private static void PrewarmAssociations(List<Entry> entries)
        {
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                if (e.IsDir) continue;
                var ext = Path.GetExtension(e.Name);
                if (!string.IsNullOrEmpty(ext)) extensions.Add(ext);
                if (extensions.Count >= 40) break; // a folder of mixed junk isn't worth more
            }
            if (extensions.Count == 0) return;

            Task.Run(() =>
            {
                foreach (var ext in extensions) FileLauncher.Prewarm(ext);
            });
        }

        /// <summary>
        /// A folder listing shown while it is still arriving.
        ///
        /// The worker offers batches; the UI thread takes whatever has arrived
        /// every so often and puts it on screen in sorted order. Three rules make
        /// it something a screen reader can live with:
        ///
        /// - **Nothing is shown for the first moment.** A folder that lists in
        ///   that time appears in one go, exactly as it always did, so the common
        ///   case is not a list that grows under the reader.
        /// - **The cursor stays on its item.** Rows arriving above it move it
        ///   down with them; it is never left on whatever slid into its place.
        /// - **Only one landing is chosen.** The row to come back to may not have
        ///   arrived yet; until it does, the cursor is not put anywhere, and it is
        ///   put there the moment it does — unless somebody has already moved it,
        ///   in which case their choice stands.
        /// </summary>
        private sealed class ProgressiveLoad
        {
            /// <summary>How long a folder has to list before anything is shown.</summary>
            private const int FirstShowMilliseconds = 250;

            /// <summary>How often more rows are put on screen after that.</summary>
            private const int RefreshMilliseconds = 300;

            private readonly MainForm _form;
            private readonly Pane _pane;
            private readonly string _path;
            private readonly string? _prefer;
            private readonly CancellationToken _token;
            private readonly System.Collections.Concurrent.ConcurrentQueue<List<Entry>> _arrived = new();
            private readonly List<Entry> _shown = new();
            private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
            private bool _landed;
            private readonly bool _enabled;

            public bool Shown { get; private set; }

            public ProgressiveLoad(MainForm form, Pane pane, string path, string? prefer, CancellationToken token,
                bool enabled = true)
            {
                _enabled = enabled;
                _form = form;
                _pane = pane;
                _path = path;
                _prefer = prefer;
                _token = token;
            }

            /// <summary>From the worker: some more of the folder.</summary>
            public void Offer(List<Entry> batch)
            {
                if (batch.Count > 0) _arrived.Enqueue(batch);
            }

            /// <summary>
            /// Shows batches as they come until <paramref name="producing"/> is
            /// done, and hands back its answer.
            /// </summary>
            public async Task<List<Entry>> RunUntil(Task<List<Entry>> producing)
            {
                if (!_enabled) return await producing;

                int wait = FirstShowMilliseconds;
                while (true)
                {
                    var finished = await Task.WhenAny(producing, Task.Delay(wait, _token));
                    if (finished == producing) return await producing;
                    _token.ThrowIfCancellationRequested();
                    if (_form.IsDisposed) throw new OperationCanceledException();

                    ShowArrived();
                    wait = RefreshMilliseconds;
                }
            }

            private void ShowArrived()
            {
                bool any = false;
                while (_arrived.TryDequeue(out var batch))
                    foreach (var entry in batch)
                        if (_seen.Add(entry.Path)) { _shown.Add(entry); any = true; }

                if (!any) return;

                var sorted = _form.SortLikeAFolder(new List<Entry>(_shown));
                Put(sorted);
            }

            /// <summary>The whole listing, in place of the partial one.</summary>
            public void Finish(List<Entry> entries)
            {
                Put(entries);

                // Nothing ever arrived to land on — the remembered row is gone, and
                // nobody has moved — so land where a finished load would have.
                if (!_landed && CurrentIndex(_pane) < 0)
                {
                    _landed = true;
                    int target = _prefer != null && _pane.IndexByPath.TryGetValue(_prefer, out var i) ? i
                        : _form._settings.SelectFirstItemOnNavigate && entries.Count > 0 ? 0 : -1;
                    if (target >= 0)
                    {
                        _form.SelectIndex(_pane, target);
                        _form.FocusListAndRow(_pane, readRowAloud: true);
                    }
                }
            }

            private void Put(List<Entry> entries)
            {
                if (!Shown)
                {
                    // The first showing is an ordinary arrival, except that the
                    // row to come back to may not be here yet: then nothing is
                    // chosen, rather than row one now and the right row later.
                    Shown = true;
                    bool preferHere = _prefer == null ||
                                      entries.Exists(e => string.Equals(e.Path, _prefer, StringComparison.OrdinalIgnoreCase));
                    if (preferHere)
                    {
                        _form.SetEntries(_pane, entries, _prefer);
                        _landed = true;
                        _form.FocusListAndRow(_pane, readRowAloud: true);
                    }
                    else
                    {
                        _form.SetEntriesWithoutLanding(_pane, entries);
                    }

                    // Not LastLoadedPath: a part of the folder is not the folder,
                    // and a load called off after this showing has to be reloaded.
                    return;
                }

                // Later showings keep the cursor on the item it is on.
                var focusedPath = FocusedPathOf(_pane);
                var selected = _form.SelectedPathsOf(_pane);
                _form.ReplaceEntriesInPlace(_pane, entries);

                if (focusedPath != null && _pane.IndexByPath.TryGetValue(focusedPath, out var now))
                {
                    _landed = true;
                    if (now != CurrentIndex(_pane) || selected.Count > 1)
                        _form.Reselect(_pane, now, selected);
                }
                else if (!_landed && _prefer != null && _pane.IndexByPath.TryGetValue(_prefer, out var wanted))
                {
                    // The row to come back to has arrived, and nobody has moved yet.
                    _landed = true;
                    _form.SelectIndex(_pane, wanted);
                    _form.FocusListAndRow(_pane, readRowAloud: true);
                }
            }
        }

        /// <summary>
        /// Replaces a pane's rows without resetting the control, so the focus and
        /// selection states the control holds are not thrown away and nothing is
        /// announced. The caller puts the cursor back where it belongs.
        /// </summary>
        private void ReplaceEntriesInPlace(Pane pane, List<Entry> entries)
        {
            pane.Entries = entries;
            pane.ItemCache = new ListViewItem?[entries.Count];
            pane.CachedCount = 0;

            var index = new Dictionary<string, int>(entries.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++) index[entries[i].Path] = i;
            pane.IndexByPath = index;

            _suppressStatus++;
            try
            {
                if (pane.List.VirtualListSize != entries.Count)
                {
                    // Growing keeps the control's selection; shrinking past the
                    // cursor cannot, and the caller re-selects either way.
                    pane.List.VirtualListSize = entries.Count;
                }
                if (pane.List.IsHandleCreated) pane.List.Invalidate();
            }
            finally { _suppressStatus--; }

            if (pane == Active) ScheduleStatusUpdate();
        }

        /// <summary>A first showing that chooses no row. See ProgressiveLoad.</summary>
        private void SetEntriesWithoutLanding(Pane pane, List<Entry> entries)
        {
            pane.Entries = entries;
            pane.ItemCache = new ListViewItem?[entries.Count];
            pane.CachedCount = 0;

            var index = new Dictionary<string, int>(entries.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++) index[entries[i].Path] = i;
            pane.IndexByPath = index;

            _suppressStatus++;
            try
            {
                pane.List.BeginUpdate();
                try
                {
                    pane.List.VirtualListSize = 0;
                    pane.List.VirtualListSize = entries.Count;
                }
                finally { pane.List.EndUpdate(); }
            }
            finally { _suppressStatus--; }

            if (pane == Active) UpdateStatus();
        }

        /// <summary>The paths of the selected rows, in order.</summary>
        private List<string> SelectedPathsOf(Pane pane)
        {
            var paths = new List<string>();
            if (!pane.List.IsHandleCreated) return paths;
            int i = -1;
            while ((i = (int)SendMessage(pane.List.Handle, LVM_GETNEXTITEM, new IntPtr(i), new IntPtr(LVNI_SELECTED))) >= 0)
            {
                if (i < pane.Entries.Count) paths.Add(pane.Entries[i].Path);
                if (paths.Count > 100_000) break;
            }
            return paths;
        }

        /// <summary>
        /// Puts the cursor on <paramref name="focus"/> and the selection back on
        /// <paramref name="selected"/> after the rows under them moved.
        /// </summary>
        private void Reselect(Pane pane, int focus, List<string> selected)
        {
            SelectIndex(pane, focus);
            if (selected.Count <= 1 || !pane.List.IsHandleCreated) return;

            _suppressStatus++;
            try
            {
                foreach (var path in selected)
                {
                    if (!pane.IndexByPath.TryGetValue(path, out var i) || i == focus) continue;
                    var set = new LVITEM { mask = LVIF_STATE, state = LVIS_SELECTED, stateMask = LVIS_SELECTED };
                    SendMessage(pane.List.Handle, LVM_SETITEMSTATE, new IntPtr(i), ref set);
                }
            }
            finally { _suppressStatus--; }
            ScheduleStatusUpdate();
        }

        /// <summary>
        /// The same, offering what it has found every few hundred entries to
        /// <paramref name="offer"/> so it can be shown before the scan is done.
        /// </summary>
        private List<Entry> EnumerateSorted(string path, Action<List<Entry>>? offer, CancellationToken token)
        {
            // One directory scan, not two. GetDirectories followed by GetFiles
            // walks the same folder twice — two full round trips on a share, and
            // twice the work for the same answer on a folder of a hundred
            // thousand entries. Attributes, length and timestamp all come out of
            // the single scan that found the entry, so nothing is asked twice.
            var result = new List<Entry>();

            // Read once: these are properties on a settings object, and the loop
            // below runs per directory entry.
            bool showHidden = _settings.ShowHiddenFiles;
            bool showSystem = _settings.ShowSystemFiles;

            // A Drive folder is answered from the listing that populated it,
            // without touching the filesystem at all.
            //
            // Enumerating cloud placeholders costs about fifteen milliseconds
            // each and is never cached — a folder of 2118 tracks measured 33
            // seconds, every visit, against 310ms for a local folder of twice
            // the size. Every fact the loop below wants was already fetched from
            // Drive to create those placeholders and was being thrown away, so
            // this is not a cache in front of the work: it is the work, not
            // being done twice. Nothing on a Drive placeholder is ever hidden or
            // system, so those two filters have nothing to say here.
            if (Drive is { } drive && drive.TryListing(path, out var listed))
            {
                foreach (var item in listed)
                    result.Add(new Entry(Path.Combine(path, item.Name), item.Name,
                        item.IsFolder, item.Size, item.Modified));
            }
            else
            {
                var batch = offer == null ? null : new List<Entry>(256);
                long lastOffer = Environment.TickCount64;

                foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();

                    if (batch != null && batch.Count > 0 &&
                        (batch.Count >= 500 || Environment.TickCount64 - lastOffer > 150))
                    {
                        offer!(batch);
                        batch = new List<Entry>(256);
                        lastOffer = Environment.TickCount64;
                    }

                    FileAttributes attrs;
                    try { attrs = info.Attributes; }
                    catch { continue; }

                    if (!showHidden && (attrs & FileAttributes.Hidden) != 0) continue;
                    if (!showSystem && (attrs & FileAttributes.System) != 0) continue;

                    bool isDir = (attrs & FileAttributes.Directory) != 0;
                    long size = isDir ? -1 : (info is FileInfo f ? SafeLength(f) : 0);
                    var entry = new Entry(info.FullName, info.Name, isDir, size, SafeWriteTime(info));
                    result.Add(entry);
                    batch?.Add(entry);
                }

                if (batch is { Count: > 0 }) offer!(batch);
            }

            return SortLikeAFolder(result);
        }

        /// <summary>
        /// The sort order every list in this application uses: the chosen column,
        /// the chosen direction, and folders first when that is asked for.
        ///
        /// Shared with search results rather than written twice. Results came
        /// back sorted by name and direction only, so somebody with "folders
        /// first" set got it everywhere except the one list where a folder and a
        /// file are most easily confused for each other.
        /// </summary>
        private List<Entry> SortLikeAFolder(List<Entry> result)
        {
            var cmp = ComparerFor(_settings.SortBy);

            if (_settings.FoldersFirst)
            {
                var dirs = new List<Entry>();
                var files = new List<Entry>();
                foreach (var e in result) (e.IsDir ? dirs : files).Add(e);

                dirs.Sort(cmp);
                files.Sort(cmp);
                if (!_settings.SortAscending) { dirs.Reverse(); files.Reverse(); }
                dirs.AddRange(files);
                return dirs;
            }

            result.Sort(cmp);
            if (!_settings.SortAscending) result.Reverse();
            return result;
        }

        /// <summary>
        /// The comparison, chosen once rather than re-decided inside every
        /// comparison. Sorting is n log n calls, so a switch on a settings
        /// property in the body was evaluated a quarter of a million times on a
        /// twenty-thousand file folder for an answer that never changes.
        ///
        /// Every comparison falls back to the name, so the order is total: List
        /// .Sort is unstable, and without a tie-break equal-sized files shuffled
        /// on every refresh.
        /// </summary>
        private static Comparison<Entry> ComparerFor(SortColumn column) => column switch
        {
            SortColumn.Size => static (a, b) =>
            {
                int c = a.Size.CompareTo(b.Size);
                return c != 0 ? c : CompareNames(a, b);
            },
            SortColumn.Type => static (a, b) =>
            {
                int c = NameRules.CompareExtensions(a.Name, b.Name);
                return c != 0 ? c : CompareNames(a, b);
            },
            SortColumn.Modified => static (a, b) =>
            {
                int c = a.Modified.CompareTo(b.Modified);
                return c != 0 ? c : CompareNames(a, b);
            },
            _ => static (a, b) => CompareNames(a, b),
        };

        private static int CompareNames(Entry a, Entry b) =>
            NameRules.CompareNames(a.Name, a.Path, b.Name, b.Path);

        private static long SafeLength(FileInfo f) { try { return f.Length; } catch { return 0; } }
        private static DateTime SafeWriteTime(FileSystemInfo i) { try { return i.LastWriteTime; } catch { return default; } }

        private void GoUp()
        {
            if (IsDrivesView) { Announce("Already at the top"); return; }

            // GetParent throws on an empty or malformed path rather than
            // returning null, and this runs on a keystroke, so it has to cope
            // with the pane being in a state nobody expected.
            DirectoryInfo? parent;
            try { parent = Directory.GetParent(Active.CurrentPath); }
            catch { parent = null; }

            if (parent == null) { Navigate(DrivesPath); return; }
            Navigate(parent.FullName);
        }

        private async void GoToPath()
        {
            var typed = PromptForm.Ask(this, "Go to path", "Path:",
                IsDrivesView ? "" : Active.CurrentPath);
            if (typed == null) { RestoreListFocus(); return; }

            // Normalised for the same reason as a launch argument: someone typing
            // "D:" means the root of D, not whatever directory this process last
            // happened to be in on that drive.
            // Relative to the folder on screen, not to wherever this process was
            // started; and the path as typed before any %VARIABLE% in it is
            // expanded, because a folder can be called "%USERNAME%".
            var literal = typed.Trim().Trim('"');
            var expanded = Environment.ExpandEnvironmentVariables(literal);
            string Anchor(string candidate)
            {
                if (candidate.Length == 0 || IsDrivesView) return candidate;
                try
                {
                    return Path.IsPathFullyQualified(candidate) || candidate.StartsWith(@"\\")
                        || (candidate.Length >= 2 && candidate[1] == ':')
                        ? candidate
                        : Path.GetFullPath(Path.Combine(Active.CurrentPath, candidate));
                }
                catch { return candidate; }
            }

            // Which navigation this is, so a slow answer below does not take the
            // window back from wherever it has gone since.
            var goingFrom = Active;
            int navigation = ++goingFrom.NavigationId;
            bool Superseded() => IsDisposed || Active != goingFrom || goingFrom.NavigationId != navigation;

            // As in NavigateAsync: the watcher's reload waits for this rather
            // than superseding it.
            goingFrom.Probing = navigation;
            try
            {

                typed = NameRules.NormaliseLaunchPath(Anchor(literal));
                if (expanded != literal)
                {
                    var asTyped = typed;
                    bool literalThere = asTyped.Length > 0 && await Bounded(() =>
                        Directory.Exists(asTyped) || File.Exists(asTyped));
                    if (Superseded()) return;
                    if (!literalThere) typed = NameRules.NormaliseLaunchPath(Anchor(expanded));
                }

                // Focus, on this way out too. A typed value that is not blank but
                // normalises to nothing — a lone quote, an environment variable that
                // expands to nothing — left the keyboard on the form, where no arrow
                // moves and a screen reader has nothing to read. Every other exit from
                // this method already puts it back.
                if (typed.Length == 0) { RestoreListFocus(); return; }

                // Typed paths are the likeliest way to reach a dead server, so this
                // check is bounded too rather than freezing on a bad UNC name.
                var folderCheck = await ProbeDirectoryAsync(typed, NavigationProbeMs);
                if (Superseded()) return;

                if (folderCheck == FolderProbe.NotResponding)
                {
                    Announce($"{FolderDisplayName(typed)} is not responding", isError: true);
                    RestoreListFocus();
                    return;
                }

                if (folderCheck == FolderProbe.Present)
                {
                    // Focus on this way out too. NavigateAsync only restores it when
                    // it lands: a folder that exists and cannot be read — "C:\System
                    // Volume Information" is one keystroke away — announces "No
                    // permission to read" and returns, with the prompt already closed
                    // and the keyboard on the form, where no arrow moves and the
                    // screen reader has nothing to read.
                    await NavigateAsync(typed);

                    // Cheap and idempotent: focusing a control that already has the
                    // keyboard does nothing, and a navigation that did not land
                    // leaves it on the form otherwise.
                    RestoreListFocus();
                    return;
                }

                var parent = Path.GetDirectoryName(typed);
                bool isFile = parent != null && await Bounded(() => File.Exists(typed));
                if (Superseded()) return;
                if (isFile)
                {
                    await NavigateAsync(parent!, preferPath: typed);
                    RestoreListFocus();
                    return;
                }

                Announce("That path is not reachable", isError: true);
                RestoreListFocus();
            }
            finally
            {
                if (goingFrom.Probing == navigation) goingFrom.Probing = 0;
            }
        }

        /// <summary>
        /// F5, and everything else that asks for this folder to be read again.
        ///
        /// The count is reported after the reload, not before it: the number of
        /// items is the one thing a refresh can change, so it is the only part
        /// worth hearing. The operations that refresh as part of finishing —
        /// delete, paste, rename — go straight to <see cref="RefreshAsync"/> and
        /// keep their own announcement as the last word.
        /// </summary>
        private async void Refresh_()
        {
            // In a set of results, refreshing means running the search again —
            // not throwing them away and reloading the folder they came from,
            // which is what "refresh" would otherwise quietly do to the list
            // somebody is looking at.
            if (Active.SearchTerm is { } term)
            {
                // async void, same as SearchHere — see the guard there.
                try { await RunSearchAsync(term); }
                catch (Exception ex)
                {
                    AnnounceOperation("search.failed", $"Could not search: {ex.Message}");
                }
                return;
            }

            // A search still gathering defers the reload, and "Refreshed" would
            // be said for a list nothing had touched.
            if (Active.Searching)
            {
                Active.NeedsRefresh = true;
                Active.ReloadQuietly = true;
                Announce("A search is still running; the folder will be read again when it finishes");
                return;
            }

            await RefreshAsync();
            if (IsDisposed) return;

            AnnounceOperation("nav.refreshed", $"Refreshed, {NameRules.Items(Active.Entries.Count)}");
        }

        // ---------- Search ----------

        /// <summary>What was typed last, so searching again starts from it.</summary>
        private string _lastSearch = "";

        /// <summary>
        /// Asks what to look for and then looks for it. F1.
        ///
        /// The dialog is <see cref="PromptForm"/>, which is already a labelled
        /// field with OK and Cancel that opens with the caret in it — a second
        /// dialog class saying the same thing would be one more window to keep
        /// accessible and no better for it.
        /// </summary>
        private async void SearchHere()
        {
            if (IsDrivesView)
            {
                AnnounceOperation("search.nowhere", "There is nothing to search in the drive list");
                return;
            }

            var where = Active.CurrentPath;
            if (string.IsNullOrEmpty(where)) return;

            var term = PromptForm.Ask(this, "Search",
                $"Search in {FolderDisplayName(where)} and below:", _lastSearch);
            if (term == null) { RestoreListFocus(); return; }

            _lastSearch = term;

            // This method is `async void`, so anything escaping it goes to
            // Application.ThreadException and puts the crash dialog up. The
            // search itself catches its own failures, but building the rows,
            // naming the pane and announcing the count are all outside that —
            // and PasteClipboard already carries this exact guard, for this exact
            // reason. A search that goes wrong should say so and leave the window
            // standing.
            try { await RunSearchAsync(term); }
            catch (Exception ex)
            {
                AnnounceOperation("search.failed", $"Could not search: {ex.Message}");
            }

            // The prompt left the keyboard on the form, where no arrow does
            // anything, whatever the search came to.
            if (!IsDisposed && Form.ActiveForm == this) RestoreListFocus();
        }

        /// <summary>
        /// Runs the search and puts the results in the pane.
        ///
        /// Everything here is off the UI thread except putting the rows in. The
        /// local half walks a tree and the Drive half is two round trips to
        /// Google, and this is a keystroke handler.
        /// </summary>
        /// <summary>
        /// Puts the folder back on screen when what is showing is not it.
        ///
        /// A search started while a folder was still arriving cancelled that
        /// load, and a search that then failed left the rows of the folder before
        /// under the new folder's name — so a paste, Backspace or the remembered
        /// row all acted on one folder while the list showed another.
        /// </summary>
        private void ReloadIfUnfinished(Pane pane)
        {
            if (IsDisposed || pane.SearchTerm != null) return;
            if (string.Equals(pane.LastLoadedPath, pane.CurrentPath, StringComparison.OrdinalIgnoreCase)) return;
            _ = NavigateAsync(pane.CurrentPath, onPane: pane);
        }

        private async Task RunSearchAsync(string term)
        {
            var pane = Active;
            var root = pane.CurrentPath;

            // Cancels whatever this pane was doing, which is also how a second
            // search called while the first is still walking replaces it rather
            // than racing it. Escape uses the same door.
            var token = BeginPaneWork(pane);

            // And a navigation still checking its folder gives way to it, rather
            // than cancelling the search the moment its check comes back.
            pane.NavigationId++;

            // Whose search this is. A second F1 while the first is still walking
            // cancels it and starts again — and the loser then runs its finally
            // and would put the *winner's* flag down, leaving a search in flight
            // that Escape no longer stops. The same shape as the in-flight
            // counter the provider keeps: a flag given back by somebody who no
            // longer owns it is worse than one never taken.
            var mine = pane.EnumCts;

            // Recorded before the list is replaced, so coming back out of the
            // results lands where the search was started from.
            if (pane.SearchTerm == null) RememberPosition(pane);

            AnnounceOperation("search.started", $"Searching for {term}");

            SearchResult result;
            pane.Searching = true;
            try
            {
                var drive = Drive;

                // Null means this is not on the mounted letter, which is the
                // signal to walk the filesystem instead. The Drive half cannot be
                // used for a local folder and the walk must not be used for a
                // cloud one — see GoogleDrive.SearchAsync.
                var onDrive = drive == null
                    ? null
                    : await drive.SearchAsync(root, term, FileSearch.MostResults, token);

                result = onDrive ?? await Task.Run(
                    () => FileSearch.Local(root, term,
                        _settings.ShowHiddenFiles, _settings.ShowSystemFiles,
                        FileSearch.MostResults, token),
                    token);
            }
            catch (OperationCanceledException)
            {
                // Stopped with Escape, rather than replaced by something else:
                // the folder it interrupted is put back.
                if (ReferenceEquals(pane.EnumCts, mine)) ReloadIfUnfinished(pane);
                return;
            }
            catch (GoogleSignInRequiredException)
            {
                AnnounceOperation("search.failed", "Could not search: sign in to Google Drive again");
                if (ReferenceEquals(pane.EnumCts, mine)) ReloadIfUnfinished(pane);
                return;
            }
            catch (Exception ex)
            {
                // A search replaced by a navigation can surface its cancellation
                // as something else; that navigation is the pane's now.
                if (!ReferenceEquals(pane.EnumCts, mine)) return;

                // Stopped with Escape: "Search stopped" has been said, and a
                // failure after it would be a second, wrong, answer.
                if (token.IsCancellationRequested) { ReloadIfUnfinished(pane); return; }
                AnnounceOperation("search.failed", $"Could not search: {ex.Message}");
                ReloadIfUnfinished(pane);
                return;
            }
            finally
            {
                // In a finally, and only if this is still the search that is
                // running. A flag left standing is Escape cancelling a search
                // that finished minutes ago instead of leaving the results it
                // produced; a flag put down by a superseded search is Escape
                // doing nothing while one is still going.
                if (ReferenceEquals(pane.EnumCts, mine)) pane.Searching = false;
            }

            if (IsDisposed) return;
            if (token.IsCancellationRequested)
            {
                // Stopped, and finished anyway — a Drive search's listing stage
                // does not stop mid-request. The folder it interrupted goes back.
                if (ReferenceEquals(pane.EnumCts, mine)) ReloadIfUnfinished(pane);
                return;
            }

            // The pane may have been navigated while the search ran. Switched away
            // from is fine: the results go into it, where they will be on the
            // way back, rather than being dropped with the folder half loaded.
            if (pane.CurrentPath != root) return;

            var entries = new List<Entry>(result.Hits.Count);
            foreach (var hit in result.Hits)
                entries.Add(new Entry(hit.Path, hit.Name, hit.IsDir, hit.Size, hit.Modified)
                {
                    DisplayOverride = FileSearch.Label(root, hit.Path),
                });

            entries = SortLikeAFolder(entries);

            pane.SearchTerm = term;
            pane.SearchTruncated = result.Truncated;
            pane.TypeAhead.Reset();

            SetEntries(pane, entries);
            UpdatePaneName(pane);
            if (pane == Active)
            {
                _pathLabel.Text = PathLabelFor(pane);

                // On the list, and the first result read: landing on row one
                // from row one is silent otherwise, as arriving in a folder is.
                // Only with the window in front — focusing activates it, and a
                // long search finishing would take the keyboard from a progress
                // window and read a row over whatever was being done there.
                if (Form.ActiveForm == this) FocusListAndRow(pane, readRowAloud: entries.Count > 0);
            }

            if (entries.Count == 0)
                AnnounceOperation("search.none", $"Nothing matching {term} in {FolderDisplayName(root)}");
            else if (result.Truncated)
                AnnounceOperation("search.truncated",
                    $"{entries.Count} results for {term}, and there are more");
            else
                AnnounceOperation("search.results",
                    $"{entries.Count} result{(entries.Count == 1 ? "" : "s")} for {term}");
        }

        /// <summary>
        /// What the path bar says for a pane.
        ///
        /// It is a function of the pane rather than a line written at each of the
        /// places that navigate, because two of those set it from CurrentPath
        /// alone — and a pane showing results has a CurrentPath that is the
        /// folder they came from. Switching tabs away from a set of results and
        /// back relabelled it as an ordinary folder while the list still held the
        /// results and the pane was still *named* "Results for love", so the path
        /// bar and the screen reader disagreed about what was on screen.
        /// </summary>
        private string PathLabelFor(Pane pane) =>
            pane.SearchTerm is { } term ? $"Results for \"{term}\" in {pane.CurrentPath}"
            : pane.CurrentPath == DrivesPath ? "Drives"
            : pane.CurrentPath;

        /// <summary>
        /// Stops a search that is still gathering, leaving the pane showing
        /// whatever it was showing before it started.
        /// </summary>
        private void CancelSearch()
        {
            var pane = Active;
            if (!pane.Searching) return;

            pane.Searching = false;
            try { pane.EnumCts?.Cancel(); } catch { }
            AnnounceOperation("search.cancelled", "Search stopped");
        }

        /// <summary>
        /// Leaves a set of results and goes back to the folder they were found
        /// in, landing on the row the search was started from.
        ///
        /// Returns false when there were no results showing, so the key that
        /// asked can fall through to whatever else it does.
        /// </summary>
        private bool LeaveSearch()
        {
            var pane = Active;
            if (pane.SearchTerm == null) return false;

            var term = pane.SearchTerm;
            AnnounceOperation("search.left", $"Left the results for {term}");

            // NavigateAsync clears SearchTerm, so the folder it lands in is an
            // ordinary folder again — including for the watcher, which is held
            // off for exactly as long as results are showing.
            _ = NavigateAsync(pane.CurrentPath,
                              preferPath: pane.Positions.Recall(pane.CurrentPath));
            return true;
        }

        /// <summary>
        /// Reloads the current folder and puts the cursor back exactly where it
        /// was, or on <paramref name="selectAfter"/> if something specific should
        /// be picked out (a folder that was just created, a file just renamed).
        ///
        /// The row is chosen as the list is built rather than corrected
        /// afterwards. Selecting the first row and then moving is two focus
        /// changes, and a screen reader reads both — so a refresh announced a row
        /// nobody asked for before announcing the right one.
        /// </summary>
        private async Task RefreshAsync(string? selectAfter = null, string? folder = null, bool quiet = false)
        {
            // The folder the operation happened in — given, or the one the item
            // to land on is in, or where the window is — and every pane showing
            // it. An operation that finished after a tab switch used to reload
            // whichever tab was in front, looking for a row that was not there.
            folder ??= selectAfter != null ? SafeParent(selectAfter) ?? Active.CurrentPath : Active.CurrentPath;

            bool here = false;
            foreach (var other in _panes)
            {
                if (!string.Equals(other.CurrentPath, folder, StringComparison.OrdinalIgnoreCase)) continue;
                if (other == Active) here = true;
                else other.NeedsRefresh = true;
            }

            var pane = Active;
            if (!here)
            {
                // Showing results rather than the folder: the results are what
                // is on screen, and an operation on one of them re-runs the
                // search rather than replacing them with a folder. Only one
                // that happened under the folder searched — an operation
                // finishing in the other tab has nothing to do with them.
                if (pane.SearchTerm == null || !IsUnder(folder, pane.CurrentPath)) return;
            }

            if (pane.Searching) { pane.NeedsRefresh = true; return; }

            if (pane.SearchTerm is { } term)
            {
                if (quiet) { pane.NeedsRefresh = true; return; }
                try { await RunSearchAsync(term); }
                catch (Exception ex) { AnnounceOperation("search.failed", $"Could not search: {ex.Message}"); }
                return;
            }

            // On the Drive, a refresh has to go back to Google. The listing a
            // folder was populated from is what the pane is built out of now, so
            // without this a refresh would rebuild the same rows from the same
            // remembered answer and look like a refresh that does nothing. The
            // alternative — re-walking the placeholders — reads a picture that is
            // exactly as stale, thirty seconds more slowly.
            // Not for the watcher's quiet reload: on the Drive letter that is a
            // full listing from Google on every tick, and it throws away the
            // search's folder cache too.
            if (!quiet) Drive?.Forget(pane.CurrentPath);

            await NavigateAsync(pane.CurrentPath, preferPath: selectAfter,
                onPane: pane, quiet: quiet, keepFocus: selectAfter == null);
        }

        /// <summary>
        /// A yes-or-no question for the disk, off this thread and given up on
        /// after the navigation probe's time — as no.
        /// </summary>
        private static async Task<bool> Bounded(Func<bool> question)
        {
            var asking = Task.Run(() => { try { return question(); } catch { return false; } });
            return await Task.WhenAny(asking, Task.Delay(NavigationProbeMs)) == asking && asking.Result;
        }

        private static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(root) || root == DrivesPath) return false;
            var trimmed = root.TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), trimmed, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string? SafeParent(string path)
        {
            try { return Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar)); }
            catch { return null; }
        }

        // ---------- Keyboard ----------

        private void List_KeyDown(object? sender, KeyEventArgs e)
        {
            // Everything on this path runs on every repeat of a held arrow key, so
            // it does the least work that answers the question. In particular the
            // focused row is asked for as an index, never as an item.
            PerfCounters.KeyDown();

            var pane = Active;
            int count = pane.Entries.Count;

            switch (e.KeyCode)
            {
                case Keys.Enter:
                    // Shift is the way past the built-in audio player: whatever
                    // the preference says, this opens the file in the application
                    // that owns it. Without an escape hatch, turning the player on
                    // would mean losing the ability to send a track to a real one.
                    ActivateFocused(openExternally: e.Shift);
                    e.Handled = e.SuppressKeyPress = true;
                    break;

                // The way out of a set of results — and, while one is still
                // being gathered, the way to stop. Nothing else in the list has
                // ever used Escape, and it is what every other dialog and mode in
                // this application means by "take me back".
                case Keys.Escape when pane.Searching:
                    CancelSearch();
                    e.Handled = e.SuppressKeyPress = true;
                    break;

                case Keys.Escape when pane.SearchTerm != null:
                    LeaveSearch();
                    e.Handled = e.SuppressKeyPress = true;
                    break;

                case Keys.Back:
                    GoUp();
                    e.Handled = e.SuppressKeyPress = true;
                    break;

                // Extending or toggling a selection says nothing by itself.
                //
                // Measured: a plain arrow moves the focus and the screen reader
                // reads the new row, but Shift+arrow and Ctrl+Space are silent —
                // the control reports those as a change to a *range* rather than
                // a change of focus, and a range change is not something a reader
                // announces. The selection was correct throughout and there was
                // no way to hear it, which for building a selection by keyboard
                // is the whole of the task.
                //
                // Scheduled rather than said here: the control has not acted on
                // the key yet, so what is selected is still the old answer.
                case Keys.Up or Keys.Down or Keys.Home or Keys.End
                     or Keys.PageUp or Keys.PageDown when e.Shift:
                case Keys.Space when e.Control:
                    ScheduleSelectionAnnouncement();
                    break;
            }

            switch (e.KeyCode)
            {
                case Keys.Down or Keys.Up when count > 0 && !e.Shift:
                {
                    int index = FocusedIndex(pane.List);
                    if (index < 0) break;

                    bool atEnd = e.KeyCode == Keys.Down ? index == count - 1 : index == 0;
                    if (!atEnd) break;   // the overwhelmingly common case: let the list move

                    if (_settings.WrapArrowNavigation)
                        SelectIndex(pane, e.KeyCode == Keys.Down ? 0 : count - 1);

                    e.Handled = e.SuppressKeyPress = true;
                    break;
                }
            }
        }

        /// <summary>
        /// Jump-to-name: type a few letters and land on the first row that starts
        /// with them.
        ///
        /// Handled here rather than by the ListView because the list is virtual:
        /// the control has no items of its own to search, so its built-in
        /// incremental search only ever managed one letter at a time. Typing "sy"
        /// jumped to something starting with "s", then to something starting with
        /// "y" — which in a large folder is worse than not having it.
        /// </summary>
        private void List_KeyPress(object? sender, KeyPressEventArgs e)
        {
            // Ctrl+letter and the like arrive here as control characters, and
            // belong to the shortcuts, not to a search.
            if (char.IsControl(e.KeyChar)) return;

            var pane = Active;

            // Space toggles selection unless a word is already being typed, in
            // which case it is part of the name — "Program Files" has one.
            // Asked of the word as it stands *now*, not as it stood whenever the
            // last letter was typed. Space belongs to a name while one is being
            // typed and to the selection otherwise, and a buffer that never
            // expires until the next keystroke answers "still typing" for ever —
            // so a pause after any letter left Space unable to select again.
            if (e.KeyChar == ' ' && pane.TypeAhead.Current(Environment.TickCount64).Length == 0) return;

            // Ours now. Left unhandled, the control would run its own search over
            // the same keystroke and move the cursor a second time.
            e.Handled = true;

            // Accepted before the list is checked, deliberately. A folder that is
            // still loading used to swallow every letter typed into it and throw
            // them away — invisible on a local disk, where loading is instant,
            // and a second of dead keyboard on a Google Drive folder that has to
            // be fetched. The letters go into the buffer regardless and
            // SetEntries applies them the moment there is something to search.
            var query = pane.TypeAhead.Accept(e.KeyChar, Environment.TickCount64);

            if (pane.Entries.Count == 0) return;

            int current = CurrentIndex(pane);

            // Matched against what is actually displayed, so searching for what
            // you can hear works whether or not extensions are shown.
            int found = TypeAhead.Find(pane.Entries.Count, i => DisplayNameOf(pane.Entries[i]), query, current);

            // No match leaves the cursor where it is, as the shell does. The row
            // the user is on stays the row the screen reader last read, which is
            // less confusing than being moved somewhere arbitrary — but then
            // nothing at all has happened, and a dead key and a name that is not
            // there sound exactly alike. This is the only thing that separates
            // them, which is why it is the one message here that is not off.
            if (found < 0)
            {
                AnnounceOperation("find.nomatch", $"Nothing starts with {query}");
                return;
            }

            SelectIndex(pane, found);

            // A match above the cursor is the search having come round to the top.
            // Exclusive with find.typed rather than on top of it: one jump is one
            // announcement, and "wrapped" is the more specific of the two. Both
            // are off by default because the row that was jumped to is read by the
            // screen reader anyway.
            if (found < current) AnnounceOperation("find.wrapped", "Wrapped to the top");
            else AnnounceOperation("find.typed", DisplayNameOf(pane.Entries[found]));
        }

        private string DisplayNameOf(Entry entry) =>
            // A search result says where it came from, and always with its
            // extension: hiding it is right in a folder, where the column is a
            // list of names, and wrong in results somebody may well have found
            // *by* typing ".flac".
            entry.DisplayOverride
            ?? (_settings.ShowExtensions || entry.IsDir || entry.Name.LastIndexOf('.') <= 0
                ? entry.Name
                : Path.GetFileNameWithoutExtension(entry.Name));

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Ctrl+Tab works from anywhere in the window, which plain Tab cannot:
            // Tab only switches tabs while the list has focus, so from the menu or
            // the status bar there was previously no way to change tab at all.
            if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab))
            {
                SwitchTab();
                return true;
            }

            if ((keyData == Keys.Tab || keyData == (Keys.Shift | Keys.Tab)) && Active.List.Focused)
            {
                SwitchTab();
                return true;
            }

            if ((keyData == (Keys.Shift | Keys.F10) || keyData == Keys.Apps) && Active.List.Focused)
            {
                ShowOurContextMenu();
                return true;
            }

            if (keyData == (Keys.Alt | Keys.Enter))
            {
                ShowProperties();
                return true;
            }

            // The old binding for a new folder, kept working alongside F8 so
            // fingers that already know it are not retrained for nothing.
            if (keyData == (Keys.Control | Keys.Shift | Keys.N))
            {
                CreateNewFolder();
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ShowOurContextMenu()
        {
            var list = Active.List;
            Point where = MenuAnchor(list);

            // By name, never by looking inside the file. Deciding what to put on
            // a menu is not allowed to cost a read: the row may be on a share or
            // on the Drive letter, where opening it is a round trip, and this
            // runs on the UI thread every time somebody presses Shift+F10. The
            // name is free and is right about every archive anybody actually
            // has. The bytes are still checked when Extract is chosen, which is
            // the moment there is somebody waiting for an answer rather than a
            // menu waiting to be drawn.
            var focused = FocusedPath();
            bool looksLikeArchive = focused != null &&
                                    !IsDirectoryInView(focused) &&
                                    ArchiveFormats.FromName(focused) is { CanExtract: true };

            _extractItem.Visible = looksLikeArchive;
            _extractHereItem.Visible = looksLikeArchive;

            ShowDriveLinkItems();

            // A shortcut is a file, and a file written into the Drive letter by
            // hand is invisible and then gone — the sync root is built from the
            // account's listing and cleared by the next mount, which is the fault
            // New file and New folder each had and each had fixed. There is no
            // way to put a `.lnk` into Drive short of uploading one, so in a
            // Drive folder this command is one that could only ever refuse, and
            // it is left off rather than read out.
            _shortcutItem.Visible =
                focused != null && !IsDrivesView &&
                !(Drive != null && Drive.Owns(Active.CurrentPath));

            _contextMenu.Show(where);
        }

        /// <summary>
        /// Restarted on every selection change, so it only fires once the cursor
        /// has stopped somewhere. Arrowing through two thousand tracks must not
        /// open two thousand files.
        /// </summary>
        private System.Windows.Forms.Timer? _prefetchTimer;

        /// <summary>
        /// Long enough that passing over a row costs nothing, short enough to be
        /// finished before a hand that stopped on a track reaches Enter.
        ///
        /// Halved, from 350. The number was set against the cost of getting it
        /// wrong on a *local* file, where a wasted warm-up is a few milliseconds
        /// of disk nobody notices — but the case it exists for is the opposite
        /// one: a track on a share or on the Drive letter, where the first read
        /// is most of a second of round trips and every one of those started
        /// earlier is a second off the wait before the music begins. On a slow
        /// connection 350ms of deliberate hesitation is 350ms added to the thing
        /// this feature was written to shorten.
        ///
        /// Arrowing through a folder still costs nothing, because the timer is
        /// restarted on every selection change and only a cursor that has
        /// actually stopped ever reaches the tick.
        /// </summary>
        private const int PrefetchSettleMilliseconds = 175;

        private void SchedulePrefetch()
        {
            if (_suppressStatus > 0) return;   // a bulk selection is not a choice
            if (!_settings.AudioPlayerEnabled || !_settings.PlayAudioInApp) return;
            if (_settings.AudioPrefetchKilobytes <= 0) return;

            if (_prefetchTimer == null)
            {
                _prefetchTimer = new System.Windows.Forms.Timer { Interval = PrefetchSettleMilliseconds };
                _prefetchTimer.Tick += (_, _) =>
                {
                    _prefetchTimer!.Stop();

                    // Asked for here and read on a worker: the path comes from the
                    // list we already have, and nothing about deciding to warm a
                    // file touches the disk on this thread.
                    var path = FocusedPath();
                    if (path != null) Program.PrefetchAudioFile(path);
                };
            }

            _prefetchTimer.Stop();
            _prefetchTimer.Start();
        }

        private void OnSelectionChanged()
        {
            PerfCounters.SelectionChange();
            ScheduleStatusUpdate();
            SchedulePrefetch();
        }

        private System.Windows.Forms.Timer? _selectionSpeechTimer;

        /// <summary>
        /// Says what a selection gesture just did, once the control has done it.
        ///
        /// Only for the keys that are otherwise silent — Shift with an arrow, and
        /// Ctrl+Space. A plain arrow is left alone, because the reader announces
        /// that row itself and saying it again would be the echo this application
        /// spends most of its design avoiding.
        ///
        /// Short, because it is read on every press while a selection is being
        /// built: the row, whether it is now in or out, and how many there are.
        /// </summary>
        private void ScheduleSelectionAnnouncement()
        {
            if (IsDisposed) return;

            if (_selectionSpeechTimer == null)
            {
                // Long enough for the control to have acted on the key, short
                // enough to keep up with a held Shift+Down.
                _selectionSpeechTimer = new System.Windows.Forms.Timer { Interval = 90 };
                _selectionSpeechTimer.Tick += (_, _) =>
                {
                    _selectionSpeechTimer?.Stop();
                    if (!IsDisposed) AnnounceSelectionChange();
                };
            }

            _selectionSpeechTimer.Stop();
            _selectionSpeechTimer.Start();
        }

        private void AnnounceSelectionChange()
        {
            var pane = Active;
            if (!pane.List.Focused) return;

            int index = CurrentIndex(pane);
            if (index < 0 || index >= pane.Entries.Count) return;

            int selected = pane.List.SelectedIndices.Count;
            bool isIn = pane.List.SelectedIndices.Contains(index);
            var name = DisplayNameOf(pane.Entries[index]);

            AnnounceOperation("select.count",
                $"{name} {(isIn ? "selected" : "not selected")}, {selected} selected");
        }

        private void List_ColumnClick(object? sender, ColumnClickEventArgs e)
        {
            var header = Active.List.Columns[e.Column].Text;
            var col = header switch
            {
                "Type" => SortColumn.Type,
                "Size" => SortColumn.Size,
                "Modified" => SortColumn.Modified,
                _ => SortColumn.Name,
            };

            if (_settings.SortBy == col) _settings.SortAscending = !_settings.SortAscending;
            else { _settings.SortBy = col; _settings.SortAscending = true; }

            _settings.Save();
            Refresh_();
            AnnounceOperation("nav.sorted",
                $"Sorted by {header}, {(_settings.SortAscending ? "ascending" : "descending")}");
        }

        private const int LVM_FIRST = 0x1000;
        private const int LVM_SETITEMSTATE = LVM_FIRST + 43;
        private const int LVM_GETNEXTITEM = LVM_FIRST + 12;
        private const int LVM_SETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 54;
        private const int LVIS_FOCUSED = 0x0001;
        private const int LVIS_SELECTED = 0x0002;
        private const int LVIF_STATE = 0x0008;
        private const int LVNI_FOCUSED = 0x0001;
        private const int LVNI_SELECTED = 0x0002;
        private const int LVS_EX_DOUBLEBUFFER = 0x00010000;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string? subAppName, string? subIdList);

        /// <summary>
        /// Which row has the focus, as a number.
        ///
        /// ListView.FocusedItem looks like the obvious way to ask, and it is the
        /// single most expensive thing on the arrow-key path: in virtual mode its
        /// getter finds the index and then goes and *builds a ListViewItem* for it
        /// — raising RetrieveVirtualItem, formatting every column — purely so the
        /// caller can read .Index off the result and throw the rest away. Holding
        /// an arrow key did that on every repeat. This is the same question asked
        /// as one message that returns an integer.
        /// </summary>
        private static int FocusedIndex(ListView list)
        {
            if (!list.IsHandleCreated) return -1;
            return (int)SendMessage(list.Handle, LVM_GETNEXTITEM, new IntPtr(-1), new IntPtr(LVNI_FOCUSED));
        }

        /// <summary>The focused row, or the first selected one if focus says nothing.</summary>
        private static int CurrentIndex(Pane pane)
        {
            int index = FocusedIndex(pane.List);
            if (index >= 0) return index;

            var selected = pane.List.SelectedIndices;
            return selected.Count > 0 ? selected[0] : -1;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct LVITEM
        {
            public int mask; public int iItem; public int iSubItem;
            public int state; public int stateMask;
            public IntPtr pszText; public int cchTextMax;
            public int iImage; public IntPtr lParam;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref LVITEM lvi);

        /// <summary>
        /// Selects everything in one message rather than adding indices one at a
        /// time: on a folder of twenty thousand files the loop version takes
        /// seconds, this is instant.
        /// </summary>
        private void SelectAll()
        {
            var list = Active.List;
            int count = Active.Entries.Count;
            if (count == 0) return;

            // Every one of those selections raises a selection-changed event, and
            // each event used to re-total the selection. Suppressed for the
            // duration and totalled once, afterwards.
            _suppressStatus++;
            try
            {
                if (list.IsHandleCreated)
                {
                    var lvi = new LVITEM { mask = LVIF_STATE, state = LVIS_SELECTED, stateMask = LVIS_SELECTED };
                    SendMessage(list.Handle, LVM_SETITEMSTATE, new IntPtr(-1), ref lvi);
                }
                else
                {
                    for (int i = 0; i < count; i++) list.SelectedIndices.Add(i);
                }
            }
            finally { _suppressStatus--; }

            UpdateStatus();

            // select.all, not select.count: this is Ctrl+A reporting what it did.
            // select.count is the running selection total in the status bar, which
            // is a different message with a different reason to be off.
            AnnounceOperation("select.all", $"Selected {NameRules.Items(count)}");
        }

        /// <summary>
        /// Selects and focuses one row.
        ///
        /// Driven by LVM_SETITEMSTATE rather than the managed collections. In
        /// virtual mode ListView.Items[i] hands back the item our own retrieve
        /// handler built, which is a cached instance not owned by the control, so
        /// setting Focused on it set a flag on an object the list never consults.
        /// That is why the first row of a folder was selected but not focused, and
        /// why the first arrow press then started from the top again.
        /// </summary>
        private void SelectIndex(Pane pane, int index)
        {
            if (index < 0 || index >= pane.Entries.Count) return;
            var list = pane.List;

            _suppressStatus++;
            try
            {
                if (list.IsHandleCreated)
                {
                    // Clear the old state in one message, then set the new one.
                    var clear = new LVITEM
                    {
                        mask = LVIF_STATE,
                        state = 0,
                        stateMask = LVIS_SELECTED | LVIS_FOCUSED,
                    };
                    SendMessage(list.Handle, LVM_SETITEMSTATE, new IntPtr(-1), ref clear);

                    var set = new LVITEM
                    {
                        mask = LVIF_STATE,
                        state = LVIS_SELECTED | LVIS_FOCUSED,
                        stateMask = LVIS_SELECTED | LVIS_FOCUSED,
                    };
                    SendMessage(list.Handle, LVM_SETITEMSTATE, new IntPtr(index), ref set);

                    try { list.EnsureVisible(index); } catch (ArgumentOutOfRangeException) { }
                }
                else
                {
                    try
                    {
                        list.SelectedIndices.Clear();
                        list.SelectedIndices.Add(index);
                    }
                    catch (ArgumentOutOfRangeException) { }
                    catch (InvalidOperationException) { }
                }
            }
            finally { _suppressStatus--; }

            ScheduleStatusUpdate();

            // A jump is a choice of one row, even though the selection events it
            // raised were held off as bulk: typing a track's name and pressing
            // Enter should find it already warming, as arrowing to it does.
            if (pane == Active) SchedulePrefetch();
        }

        /// <summary>
        /// Names the pane after the folder it is showing.
        ///
        /// Switching tabs moves focus between two separate list controls, and a
        /// screen reader always names the control it lands on — there is no
        /// setting anywhere that suppresses that, because it is not an
        /// announcement the application makes. So these were called "Tab 1 files
        /// and folders" and "Tab 2 files and folders", which is why turning the
        /// tab announcement off did not stop the word "tab" being said: the
        /// setting was working and something else was talking.
        ///
        /// Named after the folder instead, the unavoidable announcement at least
        /// says something worth hearing. Ctrl+Shift+A still reports which tab you
        /// are on, for when that is the question.
        ///
        /// **Never while the list has the keyboard.** That is the whole of the
        /// second half of this, and it is the same trap as the first: renaming a
        /// control a screen reader is sitting on is an announcement, because the
        /// name of the focused object changed and that is an event NVDA reads.
        /// Navigation calls this on the way into every folder, and the list is
        /// already focused every time — so opening a folder spoke its name over
        /// the row that had just been focused, which is the exact thing
        /// SpeakNavigation is off by default to prevent. The setting was working
        /// and something else was talking, again.
        ///
        /// The name still has to end up right, or coming back to this pane names
        /// the folder it was showing two navigations ago. So it is held and
        /// applied when the keyboard leaves — see <see cref="ApplyPendingName"/>,
        /// which runs on the list's Leave — and a tab switch, which names the
        /// pane *before* moving focus to it, still finds it correct on arrival.
        /// </summary>
        private static void UpdatePaneName(Pane pane)
        {
            var name =
                pane.SearchTerm is { } term ? $"Results for {term}"
                : string.IsNullOrEmpty(pane.CurrentPath) ? "Files and folders"
                : DescribePath(pane.CurrentPath);

            if (pane.List.AccessibleName == name) { pane.PendingName = null; return; }

            if (pane.List.Focused) { pane.PendingName = name; return; }

            pane.PendingName = null;
            pane.List.AccessibleName = name;
        }

        /// <summary>
        /// Puts the deferred name on once the keyboard has gone, where changing
        /// it is silent.
        /// </summary>
        private static void ApplyPendingName(Pane pane)
        {
            var name = pane.PendingName;
            if (name == null) return;

            pane.PendingName = null;
            if (pane.List.AccessibleName != name) pane.List.AccessibleName = name;
        }

        /// <summary>
        /// Puts the keyboard in the list and re-asserts the chosen row, so what
        /// gets announced on arriving in a folder is the row you landed on.
        ///
        /// The order is the point. SetEntries picks the row while the list may not
        /// have focus yet, and a row focused in an unfocused control announces
        /// nothing; the control is then focused, and all a screen reader has to
        /// report at that moment is the list itself — which is where "Conner,
        /// 7 items" came from instead of the name of the first file. Re-applying
        /// the row *after* the control has focus produces the event that names it.
        /// </summary>
        private void FocusListAndRow(Pane pane, bool readRowAloud = false)
        {
            // Named before it is focused, so the name a screen reader reads on
            // arrival is the folder that is actually there.
            UpdatePaneName(pane);

            if (!Visible || pane != Active || IsDisposed) return;

            var list = pane.List;
            bool alreadyFocused = list.Focused;
            if (!alreadyFocused) list.Focus();

            int index = FocusedIndex(list);
            if (index < 0) return;

            if (!alreadyFocused) SelectIndex(pane, index);

            // Tell the accessibility layer where the focus is, every time.
            //
            // Whether the control raises this itself turns out not to be
            // predictable: the same shape of move — same landing row, list
            // growing — produced an announcement on one folder and silence on
            // another. Guessing was tried twice and was wrong both times, once
            // leaving folders silent and once saying the name twice. Sending it
            // unconditionally removes the guess: the focus really is on this row,
            // and an event for a row the reader has already announced is ignored.
            if (pane.List is FileListView view) view.NotifyRowFocused(index);

            // Read the row out only where the screen reader will not.
            //
            // This is measured, not guessed. With NVDA's own speech logging on,
            // going *into* a folder produces a focus event and NVDA announces the
            // row; arrow keys likewise. Going *up* produces no focus event at all
            // — NVDA's log records the keystroke and nothing else — so whatever
            // the cursor lands on is never spoken. That one case is ours to fill,
            // and only that one: saying it anywhere else talks over NVDA.
            if (readRowAloud) SpeakRow(pane, index);
        }

        /// <summary>
        /// Says a row the way it reads: the name, then whatever the columns show.
        /// </summary>
        private void SpeakRow(Pane pane, int index)
        {
            if (!_settings.SpeakEnabled) return;
            if (index < 0 || index >= pane.Entries.Count) return;

            var entry = pane.Entries[index];
            var parts = new List<string>(3) { DisplayNameOf(entry) };

            if (!entry.IsDir && entry.Size >= 0)
                parts.Add(SizeFormatter.Format(entry.Size, _settings.SizeUnits));

            if (entry.Modified != default)
                parts.Add(TimeFormatter.Format(entry.Modified, _settings.VerboseModifiedInfo));

            PerfCounters.RowSpoken();
            Speech.Speak(string.Join(", ", parts), _settings.InterruptSpeech);
        }

        // ---------- Actions ----------

        private string? FocusedPath()
        {
            int index = CurrentIndex(Active);
            return index >= 0 && index < Active.Entries.Count ? Active.Entries[index].Path : null;
        }

        private string[] SelectedPaths()
        {
            var list = Active.List;
            var result = new List<string>(list.SelectedIndices.Count);
            foreach (int i in list.SelectedIndices)
                if (i >= 0 && i < Active.Entries.Count) result.Add(Active.Entries[i].Path);
            return result.ToArray();
        }

        /// <summary>
        /// Opens the focused item.
        ///
        /// Two things make this fast. The directory/file question is answered
        /// from the entry we already enumerated instead of by calling
        /// Directory.Exists then File.Exists — on a remote share those are two
        /// round trips, roughly 200ms of dead time before anything starts. And
        /// ShellExecute runs on a worker, because resolving a file association
        /// can block for a noticeable moment and there is no reason for the
        /// window to freeze while it does.
        /// </summary>
        private void ActivateFocused() => ActivateFocused(openExternally: false);

        private void ActivateFocused(bool openExternally)
        {
            int index = CurrentIndex(Active);
            if (index < 0 || index >= Active.Entries.Count) return;

            var entry = Active.Entries[index];

            // In a set of results, Enter goes to where the thing lives and lands
            // on it, rather than opening it. A result is an answer to "where did
            // I put that", and the folder it is in is the rest of that answer —
            // the tracks either side of it, and somewhere to press Enter a second
            // time if opening it was what was wanted after all.
            if (Active.SearchTerm != null)
            {
                var folder = Path.GetDirectoryName(entry.Path);
                if (!string.IsNullOrEmpty(folder))
                {
                    _ = NavigateAsync(folder, preferPath: entry.Path);
                    return;
                }
            }

            if (entry.IsDir) { Navigate(entry.Path); return; }

            var path = entry.Path;
            var name = entry.Name;

            // The built-in player, if this is something it takes. It says nothing
            // unless the preferences ask it to: the row was read out a moment ago
            // and the music starting says the rest.
            //
            // True means the player has taken responsibility for the file, not
            // that it has started playing it — the opening happens on a worker,
            // because on a cloud or network track it is seconds of waiting on a
            // header and this is the keystroke handler. If it turns out nothing
            // can decode the file, the player hands it on to whatever owns the
            // extension itself; that decision is not known yet and cannot be
            // waited for here without putting the freeze back.
            if (!openExternally && _settings.PlayAudioInApp &&
                AudioFiles.IsAudio(path, _settings.AudioExtensions) &&
                Program.PlayAudioFile(path))
                return;

            if (_settings.SpeakOnOpen) AnnounceOperation($"Opening {name}");

            Task.Run(() =>
            {
                try
                {
                    FileLauncher.Open(path);
                }
                catch (Exception ex)
                {
                    if (IsDisposed) return;
                    try { BeginInvoke(() => Announce($"Could not open {name}: {ex.Message}", isError: true)); }
                    catch (ObjectDisposedException) { }
                }
            });
        }

        /// <summary>
        /// Puts a Google Drive link to the focused item on the clipboard.
        ///
        /// Two commands rather than one with a question, because they are
        /// different acts and only one of them changes anything. "Copy Drive
        /// link" reads an address that already exists; "Copy public link" grants
        /// read access to anyone who has that address, and goes on doing so until
        /// somebody revokes it at drive.google.com. Hiding that behind a dialog
        /// box on a shared command is how a file ends up public because somebody
        /// pressed Enter.
        ///
        /// Async void, like the other commands a menu item invokes directly, and
        /// with the same guard around it: an exception escaping one of these goes
        /// to Application.ThreadException and puts the crash dialog up.
        /// </summary>
        private async void CopyDriveLink(bool makePublic)
        {
            var path = FocusedPath();
            if (path == null) { AnnounceOperation("clip.nothing", "Nothing selected"); return; }

            if (Drive == null || !Drive.Owns(path))
            {
                Announce("That is not on Google Drive", isError: true);
                return;
            }

            AnnounceOperation("drive.link.asking", "Asking Google Drive for a link");

            string? link;
            try { link = await Drive.LinkFor(path, makePublic); }
            catch (Exception ex)
            {
                Announce($"Could not get a link: {ex.Message}", isError: true);
                return;
            }

            // Null means LinkFor has already said why.
            if (link == null) return;

            if (!ClipboardInterop.SetText(link))
            {
                Announce("Could not take the clipboard, another application is holding it", isError: true);
                return;
            }

            AnnounceOperation(makePublic ? "drive.link.public" : "drive.link.copied",
                makePublic ? "Copied a public link" : "Copied the Drive link");
        }

        private void CopyPath()
        {
            var path = FocusedPath();
            if (path == null) { AnnounceOperation("clip.nothing", "Nothing selected"); return; }

            if (!ClipboardInterop.SetText(path))
            {
                Announce("Could not take the clipboard, another application is holding it", isError: true);
                return;
            }
            AnnounceOperation("clip.path", "Copied file path");
        }

        private void CopyToClipboard(bool cut)
        {
            var paths = SelectedPaths();
            if (paths.Length == 0) { AnnounceOperation("clip.nothing", "Nothing selected"); return; }

            if (!ClipboardInterop.SetFiles(paths, cut))
            {
                // Saying "copied" when nothing was is worse than saying nothing:
                // the paste that follows would take the previous selection.
                Announce("Could not take the clipboard, another application is holding it", isError: true);
                return;
            }

            // The clipboard now holds placeholder paths, and whoever pastes them
            // will read those files from a standing start on the thread that
            // paints its own window. Start the download here instead, in the
            // seconds it takes to find that window: measured on a 16.8MB track,
            // 3.9 seconds of frozen application against 111ms. Nothing is waited
            // for and nothing can fail the copy — see GoogleDrive.Warm.
            //
            // Only on a copy, never on a cut. A cut whose paste lands back inside
            // Drive is a move, which the application does as one metadata request
            // without reading a byte; warming it would download the whole file to
            // throw it away.
            if (!cut) Drive?.Warm(paths);

            // One item is just "Copied". It used to be "Copied file" or "Copied
            // folder", which said what the row that had *just been read out* had
            // already said, and cost a round trip to a share to work out — the
            // type was looked up in the pane rather than asked of the filesystem
            // precisely because asking was too expensive to be worth it, which is
            // a good sign the word was not worth having.
            //
            // Two or more says how many, because that is the one thing listening
            // cannot tell you. See NameRules.SayCount.
            AnnounceOperation(cut ? "clip.cut" : "clip.copied",
                NameRules.SayCount(cut ? "Cut" : "Copied", paths.Length));
        }

        private bool IsDirectoryInView(string path) =>
            Active.IndexByPath.TryGetValue(path, out int i) &&
            i >= 0 && i < Active.Entries.Count && Active.Entries[i].IsDir;

        private async void PasteClipboard()
        {
            if (IsDrivesView) { Announce("Cannot paste into the drive list", isError: true); return; }

            if (!ClipboardInterop.TryGetFiles(out var paths, out bool isCut))
            {
                AnnounceOperation("clip.empty", "Clipboard has no files");
                return;
            }
            bool moved;
            try
            {
                moved = await RunTransfer(paths, Active.CurrentPath, isCut);
            }
            catch (Exception ex)
            {
                // This method is `async void`, so anything escaping it goes to
                // Application.ThreadException and puts the crash dialog up. Most
                // of RunTransfer is already inside a try, but the setup before it
                // is not — and "a dialog that throws on its way up" is a case
                // this file already has a rule about, one paragraph down, for the
                // suppression counter. A paste that cannot start should say so
                // and leave the window standing.
                AnnounceOperation(isCut ? "move.failed" : "copy.failed",
                    $"Could not start: {ex.Message}");
                return;
            }

            // A cut is spent once it has been pasted. Leaving it on the clipboard
            // means a second paste tries to move files that are no longer where
            // the clipboard says they are, and reports a screenful of failures
            // for what looked like a reasonable thing to do. This is what File
            // Explorer does too.
            if (isCut && moved) ClipboardInterop.ClearIfStillOurs(paths);
        }

        /// <summary>
        /// Copy or move through robocopy, with a progress window. Returns true
        /// when the transfer ran to completion without failures.
        /// </summary>
        /// <summary>
        /// Copies into a Drive folder by uploading, with the same dialog the
        /// local path uses.
        ///
        /// Robocopy cannot do this. There is nothing to copy *to* — a placeholder
        /// is not a file with room in it — so the bytes go to Google and the
        /// placeholder is made afterwards, from the id that comes back.
        ///
        /// Folders used to be refused here rather than half-copied, and the
        /// missing half was never the API — it was knowing which Drive id each
        /// created directory got, so the files below it had somewhere to be
        /// addressed to. <see cref="DriveUpload"/> is that, and this method is now
        /// the window around it: survey, dialog, refresh, sentence.
        /// </summary>
        private async Task<bool> UploadToDrive(
            string[] paths, string destination, bool move, PasteConflictPolicy conflicts)
        {
            // On a worker, and the whole walk in one go. These paths can be on a
            // share that has gone away, where a single Directory.Exists blocks for
            // seconds — and the sources of a paste into Drive are exactly the
            // paths most likely to be somewhere slow. Everything the dialog needs
            // to open with real numbers in it comes out of this one pass.
            var work = await Task.Run(() => DriveUpload.Survey(paths));

            if (work.IsEmpty)
            {
                AnnounceOperation(move ? "move.failed" : "copy.failed",
                    "Nothing there to upload");
                return false;
            }

            using var cts = new CancellationTokenSource();
            var verb = move ? "Moving to Google Drive" : "Copying to Google Drive";
            // Named a window rather than a dialog, because that is what it is
            // now. Every other `dialog` in this file is modal and should stay
            // that way; this one must not be, and the name is the first thing a
            // later edit reads before reaching for ShowDialog out of habit.
            using var window = new ProgressForm(
                TransferTitle(move ? "Moving" : "Copying", destination), _settings, cts);
            AnnounceOperation(move ? "move.start" : "copy.start", verb);

            var started = DateTime.UtcNow;
            var upload = new DriveUpload(Drive!, destination, move, conflicts, tally =>
            {
                if (window.IsDisposed) return;

                var elapsed = DateTime.UtcNow - started;
                var snapshot = new TransferProgress(
                    tally.BytesDone, tally.BytesTotal, tally.FilesDone, tally.FilesTotal,
                    tally.CurrentItem,
                    elapsed.TotalSeconds > 0.5 ? tally.BytesDone / elapsed.TotalSeconds : 0,
                    elapsed);

                // Already throttled to ten a second inside DriveUpload, which is
                // where it has to be: the reports arrive from four upload streams
                // at once and this is the thread they all queue on.
                //
                // Before there is a handle there is nothing to invoke on, and the
                // report that arrives then is the one carrying the totals — the
                // survey is finished and the window has not opened yet. Handed
                // straight over, it is held and drawn on Shown.
                try
                {
                    if (window.IsHandleCreated) window.BeginInvoke(() => window.Update(snapshot));
                    else window.Update(snapshot);
                }
                catch { }
            });

            // The destination only, not the whole watcher — see _transferTargets.
            // An upload to Drive is the longest thing this application does, and
            // it is the one that used to take the entire file manager with it.
            //
            // On the last line before the try, which is the rule this file states
            // for exactly this pair and the one place it was not followed: raised
            // thirty lines earlier, with a constructor and a Task.Run in between,
            // anything thrown in the gap leaves the folder never refreshing again
            // and every close, quit and restart asking about a transfer that is
            // not running.
            bool cancelled = false;

            // What ended the upload, when something other than cancelling did.
            //
            // This used to be `catch { }` — every failure that reached the top
            // was swallowed, and the sentence afterwards was assembled from the
            // per-file error list. Which is fine for a per-file failure and
            // silent for the one that stops the whole run: an upload that died
            // on its first folder had no per-file errors to report and
            // announced "Uploaded 0 files to Google Drive".
            string? failure = null;

            // The window's handle made here, on the UI thread, before any report
            // can arrive. Otherwise the first one — from a worker — could find no
            // handle, and call Update on the window from that worker while Show
            // was creating it here, or be stored just after Shown had looked.
            _ = window.Handle;

            var running = Task.Run(() => upload.Run(work, cts.Token));

            BeginTransferTo(destination);
            RunningTransfers++;

            try
            {
                // Modeless, like the local path next door. An upload of forty
                // gigabytes is most of an afternoon, and it used to be most of an
                // afternoon with no file manager.
                window.Show();

                try { await running; }

                // Only when it really was cancelled. The guard matters: an
                // OperationCanceledException reaching here with the token
                // untouched is a timeout wearing cancellation's clothes, and
                // reporting "Cancelled after 216 of 1134" to somebody who
                // pressed nothing is how a broken upload got read as a working
                // Cancel button for weeks.
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { cancelled = true; }
                catch (Exception ex) { failure = ex.Message; }
            }
            finally
            {
                // In a finally, not after the await. A raise left standing used
                // to mean the folder watcher never refreshed anything again — a
                // window that silently stops noticing changes, with nothing to
                // connect it back to a failed upload. That is still true of the
                // destination it is now scoped to.
                RunningTransfers--;
                EndTransferTo(destination);
                window.Finish();
            }

            await RefreshDestination(destination);

            int done = upload.FilesUploaded;
            var errors = upload.Errors;

            // A cancelled upload is not a failed one, for the same reason a
            // cancelled copy is not: silencing a deliberate Escape must not mean
            // silencing genuine failures with it.
            if (cancelled)
            {
                AnnounceOperation(move ? "move.cancelled" : "copy.cancelled",
                    $"Cancelled after {done} of {work.Files.Count}");
                return false;
            }

            // Something stopped the whole run rather than one file. Said first,
            // because it is the reason the count is what it is — and because
            // without it an upload that fell over on its first folder reported
            // "Uploaded 0 files to Google Drive", which reads as success.
            if (failure != null)
            {
                AnnounceOperation("drive.upload.failed",
                    $"Upload stopped after {done} of {work.Files.Count}: {failure}");
                ShowErrors(errors.Count > 0 ? errors : new List<string> { failure });
                AnnounceConflicts(upload.Collisions);
                return false;
            }

            if (errors.Count > 0)
            {
                AnnounceOperation("drive.upload.failed",
                    $"Uploaded {done} of {work.Files.Count}. First problem: {errors[0]}");
                ShowErrors(errors);
                AnnounceConflicts(upload.Collisions);
                return false;
            }

            AnnounceOperation("drive.upload.done", work.Folders.Count > 0
                ? $"Uploaded {done} {(done == 1 ? "file" : "files")} in " +
                  $"{work.Folders.Count} {(work.Folders.Count == 1 ? "folder" : "folders")} to Google Drive"
                : $"Uploaded {done} {(done == 1 ? "file" : "files")} to Google Drive");

            // Last, and after the outcome, for the same reason the local path says
            // it last: "Uploaded 12 files" is not much use to somebody about to go
            // looking for one that is now called something else, or that was
            // replaced because they asked for it to be.
            AnnounceConflicts(upload.Collisions);

            return true;
        }

        /// <summary>
        /// Moves items from one Drive folder to another, through Drive.
        ///
        /// It takes the conflict setting like every other paste. It used to be
        /// handed nothing — not even the answer somebody had just given in the
        /// Ask dialog — and moved regardless, so "Skip" left Drive holding two
        /// files of one name.
        /// </summary>
        private Task<bool> MoveWithinDrive(string[] paths, string destination, PasteConflictPolicy conflicts) =>
            DriveToDrive(paths, destination, conflicts, move: true);

        /// <summary>
        /// Copies from the drive to the drive, which Drive does for itself.
        ///
        /// What this adds to a move is the name, because a copy is the one
        /// operation whose destination nearly always already holds the name it is
        /// bringing — duplicating a file in place is the ordinary case. Drive is
        /// perfectly happy with two files of one name in one folder; a drive
        /// letter is not. So a clash is always resolved before the request, and
        /// what it is resolved *to* is the paste conflict setting.
        /// </summary>
        private Task<bool> CopyWithinDrive(string[] paths, string destination, PasteConflictPolicy conflicts) =>
            DriveToDrive(paths, destination, conflicts, move: false);

        /// <summary>
        /// A copy or move inside Drive, as a transfer: no bytes cross this
        /// machine, but a tree is a request per item — minutes for a big one — so
        /// it has a window with Cancel, is counted among the running transfers so
        /// quitting asks, and stands down only the watchers of the folders it is
        /// changing rather than every folder in the window.
        /// </summary>
        private async Task<bool> DriveToDrive(
            string[] paths, string destination, PasteConflictPolicy conflicts, bool move)
        {
            var verb = move ? "Moving" : "Copying";
            AnnounceOperation(move ? "move.start" : "copy.start", paths.Length == 1
                ? $"{verb} {Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar))}"
                : $"{verb} {paths.Length} items");

            var origins = move
                ? paths.Select(SafeParent).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!)
                       .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();

            using var cts = new CancellationTokenSource();
            using var window = new ProgressForm(TransferTitle(verb, destination), _settings, cts);
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // Reported as Google answers. A move is one request per item picked,
            // so it counts those. A copy is one request per file inside them,
            // counted and sized from Drive's listings first, then ticked off as
            // each file is made — the window used to say "0 of 1" for the whole
            // of a folder copy, because only the folder picked was ever reported.
            var ui = new Progress<TransferProgress>(window.Update);
            IProgress<TransferProgress> shown = ui;
            var rate = new RecentRate();
            int handled = 0, filesDone = 0, filesTotal = paths.Length;
            long bytesDone = 0, bytesTotal = 0;
            string current = "";
            void Show() => shown.Report(new TransferProgress(
                Interlocked.Read(ref bytesDone), bytesTotal,
                move ? handled : filesDone, move ? paths.Length : filesTotal,
                current, rate.Observe(Interlocked.Read(ref bytesDone), clock.Elapsed), clock.Elapsed,
                ItemsKnown: true));
            void Tick(string name)
            {
                current = name;
                if (move) handled++;
                Show();
            }
            var report = new DriveCopyReport
            {
                OnStarting = name => { current = name; Show(); },
                OnCopied = (name, bytes) =>
                {
                    Interlocked.Increment(ref filesDone);
                    Interlocked.Add(ref bytesDone, bytes);
                    current = name;
                    Show();
                },
            };

            var log = new ConflictLog();
            var errors = new List<string>();
            int done = 0;
            bool cancelled = false;

            BeginTransferTo(destination);
            foreach (var origin in origins) BeginTransferTo(origin);
            RunningTransfers++;
            try
            {
                window.Show();
                try
                {
                    if (!move && Drive is { } counting)
                    {
                        current = "Counting what to copy";
                        Show();
                        var (files, bytes) = await counting.MeasureOnDrive(paths,
                            name => { current = $"Counting: {name}"; Show(); }, cts.Token);
                        filesTotal = Math.Max(files, 1);
                        bytesTotal = bytes;
                        current = "";
                        Show();
                    }
                    done = await OnDrive(paths, destination, conflicts, move, log, errors, cts.Token, Tick,
                        report: report);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { cancelled = true; }
                window.Finish();
            }
            finally
            {
                RunningTransfers--;
                EndTransferTo(destination);
                foreach (var origin in origins) EndTransferTo(origin);
            }

            foreach (var origin in origins) await RefreshDestination(origin);
            await RefreshDestination(destination);

            var outcomes = log.Snapshot();
            if (cancelled)
            {
                AnnounceOperation(move ? "move.cancelled" : "copy.cancelled", "Cancelled");
                AnnounceConflicts(outcomes);
                return false;
            }

            if (errors.Count > 0)
            {
                AnnounceOperation(move ? "move.failed" : "copy.failed",
                    $"{(move ? "Moved" : "Copied")} {done}. {errors[0]}");
                ShowErrors(errors);
                AnnounceConflicts(outcomes);
                return false;
            }

            AnnounceOperation(move ? "move.done" : "copy.done", NameRules.SayCount(move ? "Moved" : "Copied", done));
            AnnounceConflicts(outcomes);
            return true;
        }

        /// <summary>
        /// Copies or moves items into a Drive folder, one level, applying the
        /// conflict setting the way the local engines do:
        ///
        /// - a name nothing holds goes across under its own name;
        /// - Skip leaves a clash alone, and so does Fill gaps unless both are
        ///   folders, which it merges;
        /// - Overwrite replaces a file with a file — the copy made under a free
        ///   name first and the old one trashed after, so a failure takes nothing
        ///   away — and merges a folder into a folder;
        /// - a file and a folder of one name are never swapped for each other;
        /// - keep both uses a free name.
        ///
        /// A destination Drive would not list is a failure for every item. Taken
        /// as empty, everything went across under its own name — duplicates in
        /// Drive under exactly the settings meant to prevent them.
        /// </summary>
        private async Task<int> OnDrive(string[] paths, string destination, PasteConflictPolicy conflicts,
            bool move, ConflictLog log, List<string> errors, CancellationToken token, Action<string>? progress = null,
            IReadOnlyCollection<string>? selection = null, HashSet<string>? relisted = null,
            DriveCopyReport? report = null)
        {
            // What was selected, carried into the folders a merge walks: a
            // selected item inside a folder being merged into holds its name
            // there too.
            selection ??= paths;
            relisted ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var drive = Drive;
            if (drive == null)
            {
                errors.Add("Google Drive is not connected");
                return 0;
            }

            // Listed afresh: a name added on the web since the folder was last
            // listed is a clash as much as any other.
            var taken = await Task.Run(() =>
            {
                drive.Forget(destination);
                return drive.NamesIn(destination);
            }, token);
            if (taken == null)
            {
                foreach (var path in paths)
                    errors.Add($"{Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}: " +
                               "Google Drive would not say what is already in the folder");
                return 0;
            }

            // Names this paste has handed out, apart from what was already there:
            // two selected items of one Drive name are not a clash with something
            // the person had, and replacing or skipping between them lost one.
            // A selected item that already lives here holds its name for the
            // selection as well: another item of that name must not replace it,
            // skip past it, or merge into it.
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in selection)
                if (ParentIs(path, destination))
                    claimed.Add(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));

            int done = 0;
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                var wanted = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
                var target = Path.Combine(destination, wanted);
                progress?.Invoke(wanted);

                try
                {
                    bool sameFolder = ParentIs(path, destination);
                    bool folder = await Task.Run(() => Directory.Exists(path));

                    // Its folder listed recently enough to name it from — once per
                    // folder in this paste, when the first item from it comes up.
                    var source = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
                    if (!string.IsNullOrEmpty(source) && relisted.Add(source))
                        await Task.Run(() => drive.RelistUnlessFresh(source), token);
                    token.ThrowIfCancellationRequested();

                    // The name it arrives under is its name in Drive, which the
                    // letter may show differently — cleaned up, or numbered as
                    // the second of two. Asked, and passed explicitly, so what is
                    // checked for a clash is what lands.
                    var driveName = await drive.DriveNameOf(path, token)
                        ?? throw new IOException("Google Drive would not say what it is called");
                    var landing = DriveMount.Sanitise(driveName);
                    target = Path.Combine(destination, landing);
                    bool clash = taken.Contains(landing) || (sameFolder && taken.Contains(wanted));
                    // The item itself is dealt with below as the item itself.
                    bool ownClash = !sameFolder && claimed.Contains(landing);

                    if (ownClash)
                    {
                        // Another item of this paste has the name: kept both,
                        // whatever the setting says about what was there before.
                    }
                    else if (clash && sameFolder)
                    {
                        // The item itself. Moving it where it is does nothing, and
                        // replacing it with a copy of itself trashed the original
                        // — its id, its sharing and its history with it.
                        if (move) continue;
                        if (conflicts != PasteConflictPolicy.AutoRename)
                        {
                            errors.Add($"{wanted}: it is already in this folder, and only keeping both can copy it here");
                            continue;
                        }
                    }
                    else if (clash)
                    {
                        bool existingFolder = await Task.Run(() => Directory.Exists(target));
                        bool merge = folder && existingFolder &&
                                     conflicts is PasteConflictPolicy.Overwrite or PasteConflictPolicy.FillGaps;

                        if (conflicts == PasteConflictPolicy.Skip ||
                            (conflicts == PasteConflictPolicy.FillGaps && !merge))
                        {
                            log.Skipped(target);
                            continue;
                        }

                        if (conflicts == PasteConflictPolicy.Overwrite && folder != existingFolder)
                        {
                            errors.Add($"{wanted}: a {(existingFolder ? "folder" : "file")} of that name is already there");
                            continue;
                        }

                        if (merge)
                        {
                            var children = await Task.Run(() => ChildrenOnDrive(drive, path));
                            if (children == null)
                            {
                                errors.Add($"{wanted}: Google Drive would not list it");
                                continue;
                            }

                            // Merged into, so this paste has claimed the name: a
                            // second selected folder of it is kept beside, not
                            // merged over what the first brought.
                            claimed.Add(landing);

                            int failedBefore = errors.Count, skippedBefore = log.SkippedCount;
                            done += await OnDrive(children, target, conflicts, move, log, errors, token,
                                selection: selection, relisted: relisted, report: report);

                            // Everything inside went, so the emptied folder follows
                            // it — to the Drive bin. Anything left behind keeps it.
                            // Asked of Drive, not of the listing: the listing leaves
                            // out Google documents, which were trashed with the
                            // "empty" folder.
                            if (move && errors.Count == failedBefore && log.SkippedCount == skippedBefore)
                            {
                                if (!await drive.IsEmptyOnDrive(path, token))
                                    errors.Add($"{wanted}: what is not a file here — Google documents, shortcuts — " +
                                               "was left in it, so it stays");
                                else if (!await drive.TrashPath(path, token))
                                    errors.Add($"{wanted}: everything in it moved, and the empty folder could not be removed");
                            }
                            else if (!move && await drive.HasDocumentsIn(path, token))
                            {
                                // The merge walks the letter, which does not show them.
                                errors.Add($"{wanted}: Google documents and shortcuts in it were not copied into the folder already there");
                            }
                            continue;
                        }
                    }

                    // Numbered from Drive's own name, checked as the letter would
                    // show it — and short enough that the letter keeps the number.
                    bool numbered = clash || ownClash;
                    var name = numbered
                        ? NameRules.UniqueAmong(DriveMount.RoomForNumber(driveName), n =>
                        {
                            var shown = DriveMount.Sanitise(n);
                            return taken.Contains(shown) || claimed.Contains(shown);
                        }, folder)
                        : driveName;
                    bool replacing = clash && !ownClash && !sameFolder && conflicts == PasteConflictPolicy.Overwrite;

                    if (move)
                    {
                        // Drive's own name unless a clash chose another: the name
                        // here may be the letter's cleaned-up one — "Q1: x" shows
                        // as "Q1_ x" — and passing that renamed the file.
                        if (!await drive.MoveOnDrive(path, destination, name, token))
                        {
                            errors.Add($"{wanted}: Drive would not move it");
                            continue;
                        }
                        done++;
                    }
                    else
                    {
                        // Named explicitly, as for a move.
                        int made = await drive.CopyOnDrive(path, destination, name, token, report);
                        if (made <= 0)
                        {
                            errors.Add($"{wanted}: Drive would not copy it");
                            continue;
                        }
                        done += made;
                    }

                    var arrived = DriveMount.Sanitise(name);
                    taken.Add(arrived);
                    claimed.Add(arrived);

                    if (replacing)
                    {
                        await ReplaceOnDrive(destination, landing, arrived, token);
                        taken.Remove(arrived);
                        claimed.Remove(arrived);
                        claimed.Add(landing);
                        log.Overwritten(target);
                    }
                    else if (numbered)
                    {
                        log.Renamed(Path.Combine(destination, arrived));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    errors.Add($"{wanted}: {ex.Message}");
                }
            }

            return done;
        }

        /// <summary>What is inside a Drive folder, as paths. Not on the UI thread.</summary>
        /// <summary>
        /// Whether <paramref name="path"/> sits directly in
        /// <paramref name="folder"/>. Compared as full paths with the root's
        /// separator kept: trimmed by hand, "G:\" and "G:" never matched, and an
        /// item pasted onto itself at the top of the letter was replaced — or,
        /// merged, trashed. Text only; nothing is asked of the disk.
        /// </summary>
        private static bool ParentIs(string path, string folder)
        {
            try
            {
                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
                return parent != null && string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string[]? ChildrenOnDrive(GoogleDrive drive, string folder)
        {
            drive.Forget(folder);
            drive.Populate(folder);
            if (!drive.TryListing(folder, out var entries)) return null;
            return entries.Select(e => Path.Combine(folder, e.Name)).ToArray();
        }

        /// <summary>
        /// Finishes an overwriting copy: the old one goes to the Drive bin and
        /// the new one takes its name.
        ///
        /// In that order and not the other, so there is no moment where neither
        /// exists. Trashed rather than deleted, because the whole application
        /// treats "replace" as something somebody may want back — the Drive bin
        /// is the recycle bin here.
        ///
        /// Each step is checked. A trash that did not happen, followed by the
        /// rename anyway, left two items with one name in Drive and reported the
        /// replacement as done.
        /// </summary>
        private async Task ReplaceOnDrive(string folder, string wanted, string temporary,
            CancellationToken token = default)
        {
            var drive = Drive ?? throw new IOException("Google Drive is not connected");

            // The name the replaced item has in Drive, which the replacement
            // takes: the letter's may be a cleaned-up version of it.
            // Not guessed: falling back to the letter's name renamed the file to
            // a cleaned-up version of itself. Stopped before anything is trashed.
            var driveName = await drive.DriveNameOf(Path.Combine(folder, wanted), token)
                ?? throw new IOException($"the one already there could not be replaced; the new one is {temporary}");

            if (!await drive.TrashPath(Path.Combine(folder, wanted), token))
                throw new IOException($"the one already there could not be replaced; the new one is {temporary}");

            // Not cancellable: with the old one in the bin, stopping here would
            // leave the new one under its temporary name with nothing said.
            if (await drive.RenameOnDrive(Path.Combine(folder, temporary), driveName, CancellationToken.None) == null)
                throw new IOException($"the old one was replaced, and the new one is still called {temporary}");
        }

        /// <summary>
        /// Turns the configured conflict policy into one the engines can act on.
        ///
        /// `Ask` was never resolved anywhere. `RoboCopyEngine` carried a comment
        /// saying "Ask, which the caller resolves before us" and this is the
        /// caller — it passed the setting straight through, the engine's own
        /// branch treated anything that was not Overwrite or Skip as auto-rename,
        /// and so the option quietly meant "keep both". Somebody choosing it to be
        /// warned before a file was replaced was never warned and never told why.
        ///
        /// Null means the whole transfer is off.
        /// </summary>
        private async Task<PasteConflictPolicy?> ResolveConflictPolicy(string[] paths, string destination)
        {
            var policy = _settings.PasteConflict;
            if (policy != PasteConflictPolicy.Ask) return policy;

            // On a worker: on the Drive this fetches the destination listing, and
            // locally it is one existence check per source against paths that may
            // be on a share that has gone to sleep.
            var (clashing, checkedIt) = await Task.Run(() => CollidingNames(paths, destination));

            // Nothing to decide. Asking anyway is a dialog in front of a paste
            // that was never going to overwrite anything.
            //
            // But "nothing clashed" and "nothing could be looked up" are not the
            // same answer, and treating them as one is what made this setting
            // look broken on Google Drive. A destination that would not list
            // resolved to "keep both" without a word — and "keep both" on Drive
            // then checked nothing either, because a folder there may hold two
            // files with the same name. Two silent assumptions in a row, and
            // what came out the far end was a duplicate.
            if (clashing.Count == 0 && checkedIt) return PasteConflictPolicy.AutoRename;

            var choice = ConflictForm.Ask(this, clashing, FolderDisplayName(destination));
            RestoreListFocus();

            return choice switch
            {
                FileOperations.ConflictChoice.Overwrite => PasteConflictPolicy.Overwrite,
                FileOperations.ConflictChoice.Skip => PasteConflictPolicy.Skip,
                FileOperations.ConflictChoice.Rename => PasteConflictPolicy.AutoRename,
                FileOperations.ConflictChoice.FillGaps => PasteConflictPolicy.FillGaps,
                _ => null,
            };
        }

        /// <summary>
        /// Which of these would land on a name the destination already has.
        ///
        /// Top-level names only, which is what both engines decide on: robocopy
        /// splits colliding *sources* off before it runs, and the managed engine
        /// resolves one destination per source and then copies the tree under it.
        /// A question asked about anything finer would be a question about a
        /// decision nothing downstream makes.
        /// </summary>
        private (List<string> Clashing, bool Checked) CollidingNames(string[] paths, string destination)
        {
            var clashing = new List<string>();

            bool onDrive = Drive != null && Drive.Owns(destination);
            var there = onDrive ? Drive!.NamesIn(destination) : null;

            // Whether the question could be answered at all. On Drive a
            // destination that will not list leaves us knowing nothing, and the
            // caller has to be able to tell that apart from "looked, found
            // nothing" — see ResolveConflictPolicy.
            bool checkedIt = !onDrive || there != null;

            foreach (var path in paths)
            {
                var name = Path.GetFileName(path.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) continue;

                bool clashes;
                if (onDrive)
                {
                    // Null means the destination could not be listed, which is not
                    // the same as "nothing is there". Refusing to guess keeps the
                    // dialog from claiming a collision that may not exist.
                    clashes = there != null && there.Contains(name);
                }
                else
                {
                    var target = Path.Combine(destination, name);
                    try { clashes = File.Exists(target) || Directory.Exists(target); }
                    catch { clashes = false; }
                }

                if (clashes) clashing.Add(name);
            }

            return (clashing, checkedIt);
        }

        /// <summary>
        /// How many files to copy at once, which is a different question when the
        /// files are coming off Google Drive.
        ///
        /// Everywhere else the answer is "as many as the machine can usefully
        /// have in flight" — a NAS spends its time waiting on round trips and
        /// eight or sixteen requests outstanding is the difference between usable
        /// and not. Reading a placeholder is round trips too, so the same
        /// reasoning looks like it should apply, and it is exactly backwards.
        ///
        /// A Drive read is not waiting on latency, it is *using the link*, and
        /// the link is about three megabytes a second however many ways it is
        /// split. Sixteen at once does not make the copy faster; it makes every
        /// one of the sixteen sixteen times slower, and a read slow enough is a
        /// read Windows abandons — "the cloud operation was not completed before
        /// the time-out period expired". Sixteen files failing one at a time is
        /// what a folder copied off the drive actually looked like.
        ///
        /// One, then. The whole link goes to the file being copied, that file
        /// finishes at the speed of the link, and the next one starts. The total
        /// is the same and nothing times out.
        /// </summary>
        private int TransferThreads(string[] paths)
        {
            bool fromDrive = Drive != null && paths.Any(p => Drive.Owns(p));
            return fromDrive ? 1 : FileOperations.RecommendedThreads;
        }

        private async Task<bool> RunTransfer(string[] paths, string destination, bool move)
        {
            // A move is finished on Drive by trashing and elsewhere by robocopy,
            // and a selection holding both went down one path or the other for
            // all of it: the local half copied rather than moved, was then counted
            // as "could not remove from Google Drive", and "Move complete" was
            // said with the originals still in place. Search results are where
            // such a selection comes from.
            if (move && Drive is { } drive)
            {
                int onDrive = paths.Count(p => drive.Owns(p));
                if (onDrive > 0 && onDrive < paths.Length)
                {
                    AnnounceOperation("move.failed",
                        "Some of these are on Google Drive and some are not; move them separately");
                    return false;
                }
            }

            // Out of Drive, a move ends with the originals trashed. Something the
            // account may not remove — a file shared by somebody else, a shared
            // drive where it is not a manager — was copied and then reported
            // as failed. Refused before anything is copied.
            if (move && Drive is { } source && !source.Owns(destination) && paths.All(p => source.Owns(p)))
            {
                var refused = await source.WhyNotRemovable(paths);
                if (refused.Count > 0)
                {
                    AnnounceOperation("move.failed", "Not moved: you do not have permission to remove these from Google Drive");
                    ShowErrors(refused);
                    RestoreListFocus();
                    return false;
                }
            }

            var chosen = await ResolveConflictPolicy(paths, destination);
            if (chosen == null)
            {
                AnnounceOperation(move ? "move.cancelled" : "copy.cancelled", "Cancelled");
                return false;
            }
            var conflicts = chosen.Value;

            // Drive is not a filesystem robocopy can write to. There are two
            // shapes of that, and only one of them used to be caught.
            if (Drive != null && Drive.Owns(destination))
            {
                bool allFromDrive = paths.All(p => Drive.Owns(p));

                // Both ends on the drive. This fell through to robocopy, which
                // cannot write into a placeholder tree — so moving a track from
                // one Drive folder to another quietly did nothing useful. Drive
                // has no move of its own either: a file's parents are a property,
                // so it is one PATCH that adds one and removes the other.
                if (allFromDrive && move) return await MoveWithinDrive(paths, destination, conflicts);

                // And copying, which used to be refused outright on the grounds
                // that it "means downloading and uploading the same bytes". It
                // does not: Drive copies server-side, so nothing crosses the link
                // at all — see DriveClient.Copy. The refusal was telling somebody
                // to copy several gigabytes out of Drive and back in to avoid a
                // request that moves none.
                if (allFromDrive) return await CopyWithinDrive(paths, destination, conflicts);

                return await UploadToDrive(paths, destination, move, conflicts);
            }

            using var cts = new CancellationTokenSource();
            var verb = move ? "Moving" : "Copying";
            // A window, not a dialog — see UploadToDrive above for why the name
            // is worth the two extra characters.
            using var window = new ProgressForm(
                TransferTitle(verb, destination), _settings, cts);

            // Out of the Drive letter, the window is told what Drive has actually
            // handed over rather than waiting for robocopy's late, whole-file
            // lines — see RoboCopyEngine.WithDownloads for why both are needed.
            // The watch costs nothing when it is null, and nothing on the fetch
            // path unless one exists.
            bool fromDrive = Drive != null && paths.Any(p => Drive.Owns(p));
            using var fetches = fromDrive ? Drive!.WatchFetches(paths) : null;
            long shown = 0;
            var downloadRate = new RecentRate();

            var progress = new Progress<TransferProgress>(p =>
            {
                if (fetches != null)
                {
                    p = RoboCopyEngine.WithDownloads(p, fetches.BytesServed, fetches.Current, shown, downloadRate);
                    shown = p.BytesDone;
                }
                window.Update(p);
            });
            AnnounceOperation(move ? "move.start" : "copy.start", verb);

            // Our own copy fires an event per file. The refresh at the end of this
            // method is the one that matters, so the watcher stands down until
            // then rather than rebuilding the list all the way through.
            //
            // Raised and lowered around a try, for the reason written out in
            // UploadToDrive below — which had this bug, had it fixed, and left
            // the same shape here untouched. Two ordinary decrements on two
            // ordinary paths cover neither ShowDialog throwing on its way up nor
            // an announcement failing in between, and a counter left raised means
            // the folder watcher never refreshes anything again for the life of
            // the process. Nothing connects that back to a copy that went wrong.
            //
            // Refreshing while it is still raised is deliberate and safe:
            // _suppressWatch is read only by the watcher's own tick, never by an
            // explicit reload, so the refreshes below all happen normally.
            //
            // Only the destination is held down, not the whole watcher. See
            // _transferTargets: this transfer may now run for an hour with
            // somebody working in another folder the entire time, and stopping
            // every folder on screen from noticing anything for that hour is not
            // a trade worth making to silence one of them.
            BeginTransferTo(destination);
            RunningTransfers++;
            try
            {
                // Out of the Drive letter. The engine strips the placeholder's
                // Offline flag from what it writes — see RoboCopyEngine — and a
                // move is never handed to robocopy, because deleting a placeholder
                // is not deleting the file: nothing here answers the delete
                // callback, so the file stays in Drive and comes straight back on
                // the next mount. The move is finished below, through Drive.

                // Under Fill gaps a folder already at the destination is merged
                // into, and robocopy leaves out whatever is there already — so
                // the Drive original is not all copied and must not be trashed.
                //
                // And under keep both, what already held a name means the copy
                // went elsewhere — "X (2)" — so "X" is not where to look for it.
                var alreadyThere = move && fromDrive
                    ? await Task.Run(() => paths
                        .Select(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar)))
                        .Where(n => File.Exists(Path.Combine(destination, n)) ||
                                    Directory.Exists(Path.Combine(destination, n)))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase))
                    : null;
                var merged = conflicts == PasteConflictPolicy.FillGaps
                    ? alreadyThere?.Where(n => Directory.Exists(Path.Combine(destination, n)))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : null;
                var renamedAway = conflicts == PasteConflictPolicy.AutoRename ? alreadyThere : null;

                // Two sources of one name: the second arrives numbered, whatever
                // the setting, so neither can be checked under the plain name.
                if (move && fromDrive)
                {
                    var twice = paths.GroupBy(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar)),
                            StringComparer.OrdinalIgnoreCase)
                        .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                    if (twice.Count > 0)
                    {
                        renamedAway ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        renamedAway.UnionWith(twice);
                    }
                }

                var task = RoboCopyEngine.RunAsync(
                    paths, destination, move && !fromDrive, TransferThreads(paths),
                    conflicts, progress, cts.Token, _settings.CopyBufferKilobytes,
                    cloudSource: fromDrive);

                // Show, not ShowDialog. The window is its own top-level window
                // now and the file manager stays live behind it — which is the
                // whole change: a copy is something you start and then get on
                // with, and there is no reason the application should be gone
                // while one runs. Awaiting on the UI thread returns to the
                // message loop, so both windows keep answering the keyboard.
                window.Show();

                TransferResult result;
                try { result = await task; }
                catch (Exception ex)
                {
                    window.Finish();
                    AnnounceOperation(move ? "move.failed" : "copy.failed", $"{verb} failed: {ex.Message}");

                    // Something may still have been written before it failed, so the
                    // list is re-read even on the way out.
                    await RefreshDestination(destination);
                    return false;
                }

                // While the window is still up, with its Cancel: checking what
                // arrived is a Drive listing per folder, and that is not done
                // behind a window that already says finished.
                int keptInDrive = 0;
                if (move && fromDrive && !result.Cancelled && result.Failed == 0)
                {
                    window.Update(new TransferProgress(0, 0, 0, 0,
                        "Checking the copies before removing the originals", 0, TimeSpan.Zero));
                    try
                    {
                        keptInDrive = await FinishMoveOutOfDrive(paths, destination, result.Collisions, merged, renamedAway, cts.Token);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        keptInDrive = paths.Length;
                        Announce("Copied out of Google Drive; checking was stopped, so the originals are still there.",
                            isError: true);
                    }
                }

                window.Finish();

                await RefreshDestination(destination);

                // A cancelled move is not a failed one. Reporting it as
                // "move.failed" meant the only way to silence a deliberate Escape
                // was to silence genuine failures with it.
                if (result.Cancelled) AnnounceOperation(move ? "move.cancelled" : "copy.cancelled", "Cancelled");
                else if (result.Failed > 0)
                {
                    // Copied counts files; Failed counts the items that could not
                    // be processed. Said separately because they are different
                    // units — a single failed folder can be thousands of files.
                    // One id for both verbs: a move that half succeeded is the
                    // same event as a copy that half succeeded, and the sentence
                    // already says which it was.
                    AnnounceOperation("copy.partial",
                        $"{result.Copied} file{(result.Copied == 1 ? "" : "s")} transferred, " +
                        $"{result.Failed} item{(result.Failed == 1 ? "" : "s")} failed");
                    ShowErrors(result.Errors);
                }
                // Not "Move complete" over the warnings that some originals stayed.
                else if (keptInDrive > 0)
                    AnnounceOperation("move.done", $"Copied; {NameRules.Items(keptInDrive)} kept in Google Drive");
                else AnnounceOperation(move ? "move.done" : "copy.done", move ? "Move complete" : "Copy complete");

                // Last, and after the outcome, because it is the more specific
                // sentence: "Copy complete" is not much use to somebody about to
                // go looking for a file that is now called something else.
                AnnounceConflicts(result.Collisions);

                return !result.Cancelled && result.Failed == 0;
            }
            finally { RunningTransfers--; EndTransferTo(destination); }
        }

        /// <summary>
        /// The second half of moving something out of Google Drive: sending the
        /// originals to the Drive trash, now that the copies are on disk.
        ///
        /// Only after every file has copied — the caller checks — and never for a
        /// source the conflict policy skipped, because a skipped source was not
        /// copied and trashing it would lose it. If more were skipped than the
        /// outcome remembers by name, nothing is trashed at all: it cannot be told
        /// which ones are safe, and leaving a file in Drive is recoverable where
        /// trashing the only copy is not. The trash rather than a delete for the
        /// same reason the Delete key uses it — it can be undone from
        /// drive.google.com.
        /// </summary>
        /// <returns>How many originals were left in Drive.</returns>
        private async Task<int> FinishMoveOutOfDrive(string[] paths, string destination, ConflictOutcomes collisions,
            HashSet<string>? merged, HashSet<string>? renamedAway, CancellationToken token)
        {
            if (Drive == null) return paths.Length;

            if (collisions.SkippedCount > collisions.Skipped.Count)
            {
                Announce("Copied out of Google Drive. Some items were skipped, so nothing was " +
                         "removed from Drive.", isError: true);
                return paths.Length;
            }

            var skipped = new HashSet<string>(collisions.Skipped, StringComparer.OrdinalIgnoreCase);
            int kept = 0, mergedKept = 0, unconfirmed = 0, renamedKept = 0, tooBig = 0, skippedKept = 0;
            int looked = 0;
            bool stopped = false;

            foreach (var path in paths)
            {
                var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

                // Stopped: what has been trashed stays trashed, and everything
                // not yet looked at is still in Drive — said as that, not as
                // "the originals are still there" for all of them.
                if (token.IsCancellationRequested) { stopped = true; break; }
                looked++;

                if (merged != null && merged.Contains(name)) { mergedKept++; continue; }
                if (renamedAway != null && renamedAway.Contains(name)) { renamedKept++; continue; }

                // Left because the setting said so — not a failure to report.
                if (skipped.Contains(name)) { skippedKept++; continue; }
                if (!Drive.Owns(path)) { kept++; continue; }

                // Only what can be shown to have arrived. Robocopy copies what
                // the letter shows, and that is not always all there is: Google
                // documents are not files here, a file added on the web since
                // the folder was listed is not a placeholder yet, and a clash
                // deeper down is reported by robocopy as success. Each of those
                // went to the trash with its folder.
                var arrivedAt = Path.Combine(destination, name);
                bool? confirmed;
                try
                {
                    bool isFolder = await Task.Run(() => Directory.Exists(path), token);
                    confirmed = isFolder
                        ? await Drive.VerifyCopied(path, arrivedAt, token)
                        : await Task.Run(() =>
                        {
                            var copy = new FileInfo(arrivedAt);
                            return copy.Exists && copy.Length == new FileInfo(path).Length;
                        }, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    stopped = true;
                    looked--;
                    break;
                }
                catch { confirmed = false; }
                if (confirmed == null) { tooBig++; continue; }
                if (confirmed == false) { unconfirmed++; continue; }

                bool trashed;
                try { trashed = await Drive.TrashPath(path); }
                catch { trashed = false; }

                if (!trashed) kept++;
            }

            if (kept > 0)
                Announce(NameRules.SayCount("Copied, but could not remove from Google Drive", kept) +
                         ". The originals are still there.", isError: true);

            if (renamedKept > 0)
                Announce($"{NameRules.Items(renamedKept)} kept in Google Drive, because the copy was saved " +
                         "under another name and could not be checked.", isError: true);

            if (tooBig > 0)
                Announce($"{NameRules.Items(tooBig, "folder")} kept in Google Drive, because there is too much " +
                         "in them to check the copy.", isError: true);

            if (unconfirmed > 0)
                Announce($"{NameRules.Items(unconfirmed)} kept in Google Drive, because the copy could not be " +
                         "shown to hold everything — Google documents, or something changed on the web.",
                         isError: true);

            if (mergedKept > 0)
                Announce($"{NameRules.Items(mergedKept, "folder")} merged into folders already there and kept " +
                         "in Google Drive, because not everything in them was copied.", isError: true);

            int notLooked = paths.Length - looked;
            if (stopped)
                Announce($"Checking was stopped; {NameRules.Items(notLooked)} not yet checked " +
                         "are still in Google Drive.", isError: true);

            return kept + mergedKept + unconfirmed + renamedKept + tooBig + skippedKept + notLooked;
        }

        /// <summary>
        /// The title on a transfer's window, which is also its Alt+Tab entry.
        ///
        /// "Copying" was enough when there could only ever be one of these and it
        /// was in front of everything. With several open at once, three windows
        /// all called "Copying" is a list you have to click through to read, so
        /// the destination goes in the title — it is the one thing that
        /// distinguishes two copies started a minute apart.
        /// </summary>
        private string TransferTitle(string verb, string destination)
        {
            var where = FolderDisplayName(destination);
            return string.IsNullOrEmpty(where) ? verb : $"{verb} to {where}";
        }

        /// <summary>
        /// Re-reads the destination once a transfer has finished with it.
        ///
        /// Not simply <see cref="RefreshAsync"/> any more, which reloads
        /// whatever is on screen. That was right while the window was held modal
        /// — what was on screen could only be the folder the paste happened in —
        /// and it is wrong now: a copy that takes twenty minutes finishes with
        /// somebody three folders away, and reloading *their* folder underneath
        /// them is a list rebuilding itself for no reason they can see, with the
        /// cursor moving as it goes.
        ///
        /// So the destination is refreshed if it is the folder being looked at,
        /// and otherwise marked, which is exactly what the watcher does for a
        /// change made by anything else. The pane is re-read when it is next
        /// switched to, and it is never left showing a listing from before the
        /// copy.
        /// </summary>
        private async Task RefreshDestination(string destination)
        {
            if (IsDisposed) return;

            foreach (var pane in _panes)
                if (string.Equals(pane.CurrentPath, destination, StringComparison.OrdinalIgnoreCase))
                    pane.NeedsRefresh = true;

            if (string.Equals(Active.CurrentPath, destination, StringComparison.OrdinalIgnoreCase))
            {
                Active.NeedsRefresh = false;
                await RefreshAsync();
            }
        }

        // ---------- Archives ----------

        /// <summary>
        /// Compresses the selection into an archive in the folder on screen.
        ///
        /// Built on the same three pieces a paste is: a question asked once
        /// before anything starts, a non-modal <see cref="ProgressForm"/> that
        /// the file manager stays alive behind, and a refresh of the
        /// *destination* rather than of whatever happens to be on screen when it
        /// finishes. Compressing forty gigabytes takes as long as copying it and
        /// there is no more reason to hold the window hostage for one than for
        /// the other.
        ///
        /// async void, and safe because everything between here and the end is
        /// inside a try — see the rule about an async void that throws taking the
        /// window with it. The setup before the try is a settings save and a
        /// dialog, and both are inside it.
        /// </summary>
        private async void CompressSelection()
        {
            if (IsDrivesView) { Announce("Cannot compress the drive list", isError: true); return; }

            var paths = SelectedPaths();
            if (paths.Length == 0) { AnnounceOperation("clip.nothing", "Nothing selected"); return; }

            // Beside what is being compressed. In search results that is the
            // folder the items share, when they share one, rather than the folder
            // the search started from.
            var destination = Active.CurrentPath;
            if (Active.SearchTerm != null)
            {
                var parents = paths.Select(SafeParent).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (parents.Count == 1 && !string.IsNullOrEmpty(parents[0])) destination = parents[0]!;
            }

            // Robocopy cannot write into a placeholder tree and neither can this.
            // Said out loud rather than attempted, because the failure otherwise
            // is an archive that appears to have been created and holds nothing.
            if (Drive != null && Drive.Owns(destination))
            {
                Announce("An archive cannot be written into Google Drive. " +
                         "Make it in a local folder and copy it up.", isError: true);
                RestoreListFocus();
                return;
            }

            var remembered = ArchiveFormats.ById(_settings.ArchiveFormatId);
            var startFormat = remembered is { CanCreate: true } ? remembered : ArchiveFormats.Zip;

            // Asked before the dialog is built, on a worker: it decides whether a
            // bare .gz is offered, and the answer comes from the disk.
            var single = paths.Length == 1 &&
                         await Task.Run(() => { try { return File.Exists(paths[0]); } catch { return false; } });

            var request = ArchiveForm.Ask(this, destination, paths, startFormat,
                _settings.ArchiveCompression, single);
            if (request == null) { RestoreListFocus(); return; }

            using var cts = new CancellationTokenSource();
            using var window = new ProgressForm(
                TransferTitle("Compressing", destination), _settings, cts);

            var progress = new Progress<TransferProgress>(p => window.Update(p));

            BeginTransferTo(destination);
            RunningTransfers++;
            try
            {
                // Remembered only once the dialog has been answered, so a
                // cancelled Compress does not change what the next one offers.
                _settings.ArchiveFormatId = request.Format.Id;
                _settings.ArchiveCompression = request.Level;
                _settings.Save();

                AnnounceOperation("archive.start", NameRules.SayCount("Compressing", paths.Length));

                var task = ArchiveEngine.CompressAsync(paths, request.Path, request.Format,
                    request.Level, ArchiveEngine.RecommendedThreads, progress, cts.Token);

                window.Show();

                ArchiveResult result;
                try { result = await task; }
                catch (Exception ex)
                {
                    window.Finish();
                    AnnounceOperation("archive.failed", $"Could not compress: {ex.Message}");

                    // Something may have been written before it failed, so the
                    // list is re-read even on the way out.
                    await RefreshDestination(destination);
                    RestoreListFocus();
                    return;
                }

                window.Finish();
                await RefreshDestination(destination);

                var name = Path.GetFileName(request.Path);

                if (result.Cancelled) AnnounceOperation("archive.cancelled", "Compressing cancelled");
                else if (result.Failed > 0)
                {
                    AnnounceOperation("archive.partial",
                        $"Created {name}; {result.Failed} item{(result.Failed == 1 ? "" : "s")} " +
                        "could not be read");
                    ShowErrors(result.Errors);
                }
                else
                {
                    // The ratio, because it is the one number somebody made an
                    // archive to find out, and the moment it finishes is the only
                    // place it is ever available: afterwards the file is one size
                    // and what went into it is not recorded anywhere.
                    var saved = result.Saved is { } percent && percent >= 1
                        ? $", {percent:0} percent smaller"
                        : "";

                    AnnounceOperation("archive.done", result.LeftOut > 0
                        ? $"Created {name}{saved}; {NameRules.Items(result.LeftOut, "link")} left out"
                        : $"Created {name}{saved}");

                    // Which ones, since a link left out is something somebody may
                    // go looking for inside the archive.
                    if (result.LeftOut > 0) ShowErrors(result.Errors);
                }
            }
            catch (Exception ex)
            {
                AnnounceOperation("archive.failed", $"Could not compress: {ex.Message}");
                RestoreListFocus();
            }
            finally { RunningTransfers--; EndTransferTo(destination); }
        }

        /// <summary>
        /// Extracts the archive the cursor is on.
        ///
        /// <paramref name="intoNewFolder"/> is the difference between the two
        /// menu entries and it is the difference between a command that can
        /// collide and one that cannot. Extract makes a folder named after the
        /// archive — uniquely, so a second attempt does not merge into the first
        /// — and puts everything inside it, which is what unpacking a download
        /// almost always means and which no conflict question applies to.
        /// Extract here puts the contents straight into the folder on screen, and
        /// that is where the paste conflict setting comes in, asked once about
        /// the top-level names exactly as a paste asks it.
        /// </summary>
        private async void ExtractFocused(bool intoNewFolder)
        {
            if (IsDrivesView) { Announce("Cannot extract from the drive list", isError: true); return; }

            var archive = FocusedPath();
            if (archive == null) { AnnounceOperation("clip.nothing", "Nothing selected"); return; }

            // The folder the archive is in: in search results that is not the
            // folder the search started from.
            var here = Active.SearchTerm != null ? SafeParent(archive) ?? Active.CurrentPath : Active.CurrentPath;

            if (Drive != null && (Drive.Owns(here) || Drive.Owns(archive)))
            {
                Announce("Archives on Google Drive cannot be extracted in place. " +
                         "Copy the archive to a local folder first.", isError: true);
                RestoreListFocus();
                return;
            }

            // Reading the first block of a file is a disk touch, and on a share it
            // is a round trip, so it is not done on the thread drawing the window.
            var format = await Task.Run(() => ArchiveFormats.Identify(archive));

            if (format == null || !format.CanExtract)
            {
                AnnounceOperation("archive.unknown",
                    $"{Path.GetFileName(archive)} is not an archive this can open");
                RestoreListFocus();
                return;
            }

            // On a worker, like the sniff above. UniqueName is a loop of
            // existence checks, and the folder this is running in may be a share
            // whose server has gone away — which is exactly the moment the window
            // must not freeze.
            var destination = intoNewFolder
                ? await Task.Run(() => FileOperations.UniqueName(
                    Path.Combine(here, ArchiveFormats.BaseName(archive))))
                : here;

            using var cts = new CancellationTokenSource();
            using var window = new ProgressForm(
                TransferTitle("Extracting", destination), _settings, cts);

            var progress = new Progress<TransferProgress>(p => window.Update(p));

            // The conflict question is asked from a worker, and a modal dialog
            // belongs to the thread that owns the window. Invoke rather than
            // BeginInvoke because the answer is what the worker is waiting for,
            // and it does not deadlock: the UI thread is back in the message loop
            // at this point, awaiting the task below.
            Func<IReadOnlyList<string>, FileOperations.ConflictChoice> ask = names =>
                (FileOperations.ConflictChoice)Invoke(new Func<FileOperations.ConflictChoice>(() =>
                {
                    var choice = ConflictForm.Ask(this, names, FolderDisplayName(destination));
                    RestoreListFocus();
                    return choice;
                }));

            BeginTransferTo(destination);
            RunningTransfers++;
            try
            {
                AnnounceOperation("extract.start", $"Extracting {Path.GetFileName(archive)}");

                var task = ArchiveEngine.ExtractAsync(archive, destination,
                    intoNewFolder ? PasteConflictPolicy.AutoRename : _settings.PasteConflict,
                    ask, ArchiveEngine.RecommendedThreads, progress, cts.Token);

                window.Show();

                ArchiveResult result;
                try { result = await task; }
                catch (Exception ex)
                {
                    window.Finish();
                    AnnounceOperation("extract.failed", $"Could not extract: {ex.Message}");
                    await RefreshDestination(intoNewFolder ? here : destination);
                    RestoreListFocus();
                    return;
                }

                window.Finish();

                // A new folder appears in the folder on screen; extracting here
                // changes the folder on screen itself. Either way it is the place
                // that changed rather than whatever is in view when it ends.
                await RefreshDestination(intoNewFolder ? here : destination);

                if (result.Cancelled) AnnounceOperation("extract.cancelled", "Extracting cancelled");
                else if (result.Failed > 0)
                {
                    AnnounceOperation("extract.partial",
                        $"Extracted {result.Items} item{(result.Items == 1 ? "" : "s")}; " +
                        $"{result.Failed} failed");
                    ShowErrors(result.Errors);
                }
                else
                {
                    AnnounceOperation("extract.done",
                        $"Extracted {result.Items} item{(result.Items == 1 ? "" : "s")} " +
                        $"to {FolderDisplayName(destination)}");
                }

                AnnounceConflicts(result.Collisions);
            }
            catch (Exception ex)
            {
                AnnounceOperation("extract.failed", $"Could not extract: {ex.Message}");
                RestoreListFocus();
            }
            finally { RunningTransfers--; EndTransferTo(destination); }
        }

        /// <summary>
        /// Says what the conflict policy did, when it did anything.
        ///
        /// These were in the catalogue and raised by nothing: the policy is
        /// applied inside RoboCopyEngine and FileOperations, and neither used to
        /// carry back what it had decided — so a paste that quietly renamed or
        /// quietly replaced a file said only "Copy complete". Replacing is the
        /// one that destroys something and renaming is the one that moves the
        /// file out from under the name you were expecting; both are worth a
        /// sentence.
        ///
        /// Ordered so the rename lands last, because it is the one that leaves
        /// something to go and find.
        /// </summary>
        private void AnnounceConflicts(ConflictOutcomes conflicts)
        {
            if (conflicts.Total == 0) return;

            if (conflicts.OverwrittenCount > 0)
                AnnounceOperation("conflict.overwrite",
                    Describe("Replaced", conflicts.Overwritten, conflicts.OverwrittenCount));

            if (conflicts.SkippedCount > 0)
                AnnounceOperation("conflict.skip",
                    Describe("Skipped", conflicts.Skipped, conflicts.SkippedCount));

            if (conflicts.RenamedCount > 0)
                AnnounceOperation("conflict.rename",
                    conflicts.RenamedCount == 1 && conflicts.Renamed.Count == 1
                        ? $"Saved as {conflicts.Renamed[0]}"
                        : $"{conflicts.RenamedCount} items saved under new names");

            // One collision is worth naming; a hundred is a number. The list is
            // capped, so the name is only used when the count says it is the
            // whole story.
            static string Describe(string verb, IReadOnlyList<string> names, int count) =>
                count == 1 && names.Count == 1
                    ? $"{verb} {names[0]}"
                    : $"{verb} {count} existing item{(count == 1 ? "" : "s")}";
        }

        private async void CreateNewFolder()
        {
            if (IsDrivesView) { Announce("Cannot create a folder in the drive list", isError: true); return; }

            var name = PromptForm.Ask(this, "New folder", "Folder name:", "New folder");
            if (name == null) { RestoreListFocus(); return; }

            if (DescribeBadName(name) is { } complaint)
            {
                Announce(complaint, isError: true);
                RestoreListFocus();
                return;
            }

            try
            {
                string path;

                // On the Drive letter a folder has to be made *in Drive*.
                // Directory.CreateDirectory here made a real local directory
                // inside the sync root that the account knew nothing about — so it
                // never appeared, because the pane is built from the Drive
                // listing, and it was deleted by the next mount, which clears the
                // sync root. "Created folder" was announced every time.
                if (Drive != null && Drive.Owns(Active.CurrentPath))
                {
                    var placed = await Drive.CreateFolderIn(
                        Active.CurrentPath,
                        await UniqueDriveName(Active.CurrentPath, name, folderName: true),
                        announce: false);

                    if (placed == null)
                    {
                        Announce("Could not create the folder in Google Drive", isError: true);
                        RestoreListFocus();
                        return;
                    }
                    path = placed;
                }
                else
                {
                    // On a worker, like the rename beside it. Both calls are
                    // filesystem work — a loop of existence checks and then the
                    // create — and on a share that has gone to sleep they freeze
                    // the window for the SMB timeout with the prompt already
                    // dismissed and nothing on screen to say why.
                    var folder = Active.CurrentPath;
                    path = await Task.Run(() =>
                    {
                        var unique = FileOperations.UniqueName(Path.Combine(folder, name));
                        Directory.CreateDirectory(unique);
                        return unique;
                    });
                }

                await RefreshAsync(path);
                AnnounceOperation("folder.created", $"Created folder {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                // Focus, on every way out. A PromptForm has been up, so the
                // keyboard is on the form — where no arrow works and a screen
                // reader has nothing to read.
                Announce($"Could not create folder: {ex.Message}", isError: true);
                RestoreListFocus();
            }
        }

        /// <summary>
        /// A name nothing in this Drive folder is using yet.
        ///
        /// The local path gets this from the filesystem; here the folder's own
        /// listing is the only cheap answer, and it is already in hand because the
        /// pane was built from it. On a worker because a folder nobody has opened
        /// yet costs a Drive listing to answer, and because the answer is worth
        /// having anyway: without it, F8 twice merges into the folder made the
        /// first time rather than making a second.
        /// </summary>
        private async Task<string> UniqueDriveName(string folder, string name, bool folderName)
        {
            var taken = await Task.Run(() => Drive?.NamesIn(folder));
            return taken == null ? name : NameRules.UniqueAmong(name, taken.Contains, folderName);
        }

        /// <summary>
        /// Creates an empty file and puts the cursor on it.
        ///
        /// The name is offered with its extension selected off the end, so typing
        /// replaces "New file" and leaves ".txt" — the same bargain renaming makes.
        /// </summary>
        private async void CreateNewFile()
        {
            if (IsDrivesView) { Announce("Cannot create a file in the drive list", isError: true); return; }

            var name = PromptForm.Ask(this, "New file", "File name:", "New file.txt", selectStem: true);
            if (name == null) { RestoreListFocus(); return; }

            if (DescribeBadName(name) is { } complaint)
            {
                Announce(complaint, isError: true);
                RestoreListFocus();
                return;
            }

            try
            {
                string path;

                // The same fault as New folder, one step worse: the local file
                // really was created, was invisible from the moment it appeared,
                // and was deleted by the next mount clearing the sync root — so
                // anything typed into it in between went with it.
                if (Drive != null && Drive.Owns(Active.CurrentPath))
                {
                    // Taken once: these are network waits, and a tab switched in the
                    // middle of them is not where the file went.
                    var driveFolder = Active.CurrentPath;
                    var placed = await Drive.CreateFileIn(
                        driveFolder,
                        await UniqueDriveName(driveFolder, name, folderName: false));

                    if (placed == null)
                    {
                        Announce("Could not create the file in Google Drive", isError: true);
                        RestoreListFocus();
                        return;
                    }
                    path = Path.Combine(driveFolder, placed);
                }
                else
                {
                    // On a worker, for the reason the rename below it is: these
                    // are filesystem calls, and on a sleeping share they freeze
                    // the window with the prompt already gone.
                    var folder = Active.CurrentPath;
                    path = await Task.Run(() =>
                    {
                        var unique = FileOperations.UniqueName(Path.Combine(folder, name));

                        // CreateNew rather than Create: a file that appeared
                        // between the unique-name check and here belongs to
                        // somebody else, and truncating it would be the worst
                        // possible outcome of asking for a new one.
                        using (new FileStream(unique, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                        return unique;
                    });
                }

                await RefreshAsync(path);
                AnnounceOperation("folder.created", $"Created file {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                Announce($"Could not create file: {ex.Message}", isError: true);
                RestoreListFocus();
            }
        }

        /// <summary>
        /// Makes a Windows shortcut to the focused row, in the folder that row is
        /// in, and lands on it.
        ///
        /// It asks nothing. There is one sensible name — the target's, with
        /// " - Shortcut" on the end, made unique if that is taken — and one
        /// sensible place, which is next to the thing it points at. A dialog here
        /// would be a question with a right answer already in it, and this file
        /// has a rule about those.
        ///
        /// Everything that touches the filesystem is on a worker: the unique-name
        /// search is a loop of existence checks, and writing the `.lnk` is the
        /// shell opening a file — both of them on a path that may be a share whose
        /// server has gone to sleep, which is the case this rule exists for.
        ///
        /// Async void, like every other command a menu item invokes, so the try
        /// has to cover the whole of it: what it awaits does not catch for itself
        /// the way NavigateAsync and RefreshAsync do, and an exception escaping
        /// here goes to Application.ThreadException and puts the crash dialog up.
        /// </summary>
        private async void CreateShortcut()
        {
            if (IsDrivesView) { Announce("Cannot create a shortcut in the drive list", isError: true); return; }

            var target = FocusedPath();
            if (target == null) { AnnounceOperation("clip.nothing", "Nothing selected"); return; }

            // The folder the row is in, which in search results is not the
            // folder the search started from.
            var folder = SafeParent(target) ?? Active.CurrentPath;

            // See ShowOurContextMenu for why Drive is refused rather than
            // attempted: a `.lnk` written into the sync root by hand is invisible
            // from the moment it appears and is swept by the next mount.
            if (Drive != null && Drive.Owns(folder))
            {
                Announce("Cannot create a shortcut in Google Drive", isError: true);
                return;
            }

            try
            {
                var linkPath = await Task.Run(() =>
                    FileOperations.UniqueName(Path.Combine(folder, ShellLink.NameFor(target))));

                await ShellLink.CreateAsync(target, linkPath);

                if (IsDisposed) return;

                await RefreshAsync(linkPath);
                AnnounceOperation("shortcut.created", $"Created shortcut {Path.GetFileName(linkPath)}");
            }
            catch (Exception ex)
            {
                Announce($"Could not create shortcut: {ex.Message}", isError: true);
            }
        }

        private async void RenameSelected()
        {
            if (IsDrivesView) { Announce("Cannot rename a drive here", isError: true); return; }

            var path = FocusedPath();
            if (path == null) { Announce("Nothing selected"); return; }

            var oldName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

            // The extension is left out of the selection, so typing replaces the
            // name and keeps the type — folders have no extension to spare.
            bool isFolder = IsDirectoryInView(path);
            var newName = PromptForm.Ask(this, "Rename", "New name:", oldName, selectStem: !isFolder);

            // The prompt trims what comes back, so an untouched " notes.txt" comes
            // back as "notes.txt" — which is not a rename anybody asked for.
            if (newName == null || newName == oldName || newName == oldName.Trim()) { RestoreListFocus(); return; }

            if (DescribeBadName(newName) is { } complaint)
            {
                // NameRules already says which character is the problem, which is
                // the whole of this message; the id only decides where it goes.
                AnnounceOperation("rename.invalid", complaint);
                RestoreListFocus();
                return;
            }

            try
            {
                var dest = Path.Combine(Path.GetDirectoryName(path)!, newName);

                // A rename that changes only capitalisation is a rename, and it
                // was impossible: the guard above compares ordinally so "A.txt"
                // to "a.txt" gets this far, and then NTFS says the destination
                // exists — because it is the same file. Windows renames it
                // perfectly well when simply asked.
                bool sameFileDifferentCase =
                    string.Equals(Path.GetFileName(path), newName, StringComparison.OrdinalIgnoreCase);

                // On a worker, both of them.
                //
                // This is the rule the rest of the window already follows and
                // this method did not: File.Exists on a share whose server has
                // gone to sleep blocks until the SMB stack gives up, and so does
                // Directory.Exists, and both of them are asked here about a path
                // the user has just typed. Two probes, one after the other, on
                // the thread that has to redraw — so renaming a file on a NAS
                // that had gone quiet froze the whole window for as long as it
                // took, with the rename dialog already gone and nothing on
                // screen to say why.
                bool taken = await Task.Run(() =>
                {
                    try { return File.Exists(dest) || Directory.Exists(dest); }
                    catch { return false; }
                });

                if (IsDisposed) return;

                if (!sameFileDifferentCase && taken)
                {
                    AnnounceOperation("rename.failed", "A file with that name already exists");
                    RestoreListFocus();
                    return;
                }

                // On the Drive letter the name lives in the account, not on this
                // machine. File.Move renamed the placeholder and told Google
                // nothing — so the next listing put the old name straight back,
                // and the renamed placeholder was left on disk under a name no
                // listing mentions, invisible and in the way. "Renamed to X" was
                // announced every time and nothing had been.
                if (Drive != null && Drive.Owns(path))
                {
                    var placed = await Drive.RenameOnDrive(path, newName.Trim());
                    if (placed == null) { RestoreListFocus(); return; }

                    await RefreshAsync(Path.Combine(Path.GetDirectoryName(path)!, placed));
                    AnnounceOperation("rename.done", $"Renamed to {placed}");
                    return;
                }

                // And the move itself, for the same reason and rather more of
                // it: renaming a directory on a share is a round trip, and
                // renaming one the indexer happens to be walking is several.
                //
                // The exception is carried back out rather than caught in here,
                // so the message on screen is still the one the filesystem gave
                // — "access denied", "the file is in use by another process" —
                // and not a sentence this method made up about it.
                await Task.Run(() =>
                {
                    // From the literal path when the old name ends in a dot or a
                    // space, or Windows trims it and renames a different file.
                    var from = NameRules.NeedsLiteralPath(path) ? NameRules.LiteralPath(path) : path;
                    if (Directory.Exists(from)) Directory.Move(from, dest);
                    else File.Move(from, dest);
                });

                if (IsDisposed) return;

                await RefreshAsync(dest);
                AnnounceOperation("rename.done", $"Renamed to {newName.Trim()}");
            }
            catch (Exception ex)
            {
                AnnounceOperation("rename.failed", $"Could not rename: {ex.Message}");
                RestoreListFocus();
            }
        }

        private static string? DescribeBadName(string name) => NameRules.DescribeBadName(name);

        private bool _deleting;

        /// <summary>
        /// Sends Drive items to the Drive trash, one at a time, collecting
        /// failures the same way the local path does.
        ///
        /// One at a time rather than in parallel: these are network calls against
        /// somebody's own files, and a burst of them that half succeeds is harder
        /// to explain afterwards than a sequence that stops where it stopped.
        /// </summary>
        private async Task<(int Done, List<string> Errors)> TrashOnDrive(string[] paths)
        {
            int done = 0;
            var errors = new List<string>();

            foreach (var path in paths)
            {
                try
                {
                    if (Drive != null && await Drive.TrashPath(path)) done++;
                    else errors.Add($"{Path.GetFileName(path)}: Drive would not take it");
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }

            // The listing this folder was built from is now wrong by however
            // many were taken out of it, and the placeholders are gone from
            // disk. Dropped here rather than left for the refresh to notice,
            // because a pane rebuilt from a remembered listing would show every
            // deleted file still sitting there — which reads as a delete that
            // did nothing, whatever the count said.
            // Every folder something was taken from: results from several
            // folders left the others' listings still showing the trashed files.
            if (done > 0)
            {
                foreach (var folder in paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (!string.IsNullOrEmpty(folder)) Drive?.Forget(folder);
            }

            return (done, errors);
        }

        private string? RowAfterRemoving(string[] removing)
        {
            var pane = Active;
            var gone = new HashSet<string>(removing, StringComparer.OrdinalIgnoreCase);
            int focused = CurrentIndex(pane);
            if (focused < 0) return null;

            for (int i = focused + 1; i < pane.Entries.Count; i++)
                if (!gone.Contains(pane.Entries[i].Path)) return pane.Entries[i].Path;
            for (int i = Math.Min(focused, pane.Entries.Count) - 1; i >= 0; i--)
                if (!gone.Contains(pane.Entries[i].Path)) return pane.Entries[i].Path;
            return null;
        }

        private async void DeleteSelected(bool permanentOverride)
        {
            if (IsDrivesView) { Announce("Cannot delete a drive", isError: true); return; }
            if (_deleting) { Announce("A delete is already running"); return; }

            var paths = SelectedPaths();
            if (paths.Length == 0) { Announce("Nothing selected"); return; }

            bool permanent = permanentOverride || _settings.Delete == DeleteMode.Permanent;

            // Where to land afterwards, decided while the rows are still there:
            // the first row after the focused one that is not being deleted, or
            // failing that the nearest one before it. Landing on the deleted
            // file's own name found nothing, and the cursor went back to the top
            // of the folder.
            var origin = Active.CurrentPath;
            var landOn = RowAfterRemoving(paths);

            // Anything on the Drive letter goes to the Drive trash instead. The
            // Recycle Bin cannot hold it — there is no local file to move, only a
            // placeholder — and deleting the placeholder would leave the file
            // sitting in Drive with nothing on this machine to show for it.
            bool onDrive = Drive != null && paths.All(p => Drive.Owns(p));

            // Half and half went the local way for all of it, which deletes the
            // Drive placeholders and leaves the files in Drive — back on the next
            // mount, after being counted as deleted.
            if (!onDrive && Drive != null && paths.Any(p => Drive.Owns(p)))
            {
                Announce("Some of these are on Google Drive and some are not; delete them separately", isError: true);
                return;
            }

            if (_settings.ConfirmDelete)
            {
                var what = paths.Length == 1 ? $"\"{Path.GetFileName(paths[0])}\"" : $"{paths.Length} items";

                // The name goes in the middle, not on the end. Built as
                // "Really {verb} {what}?" this read "Really send to the Recycle
                // Bin "song.flac"?" — the object after its own destination,
                // which is not a sentence, and this one is read out loud.
                var question = onDrive
                    ? $"Really move {what} to the Google Drive trash?"
                    : permanent
                        ? $"Really permanently delete {what}?"
                        : $"Really send {what} to the Recycle Bin?";

                if (MessageBox.Show(this, question, "Confirm delete",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    AnnounceOperation("delete.cancelled", "Delete cancelled");
                    RestoreListFocus();
                    return;
                }
            }

            // Off the UI thread. Recycling a large tree walks every file in it to
            // build the undo record, and permanent deletion walks it to unlink
            // each one — either can take tens of seconds, and doing it inline froze
            // the window solid with no way to tell whether it had crashed.
            var option = permanent ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;
            (int Done, List<string> Errors) outcome;

            // Both flags raised on the last line before the try, with nothing
            // between them and it. They used to be raised three statements
            // earlier, with an announcement in the gap — and an announcement that
            // throws there leaves `_suppressWatch` raised for the life of the
            // process, so the folder watcher never refreshes anything again, and
            // `_deleting` stuck true, so Delete never works again either. Neither
            // has anything on screen to connect it back to the delete that went
            // wrong. That is the exact shape of bug this file already has a rule
            // about, one method along, for the same counter.
            var windowHandle = Handle;
            AnnounceOperation(paths.Length == 1 ? "Deleting" : $"Deleting {paths.Length} items");

            _deleting = true;
            _suppressWatch++;   // one refresh at the end, not one per file removed
            try
            {
                if (onDrive)
                {
                    outcome = await TrashOnDrive(paths);
                }
                else
                outcome = await Task.Run(() =>
                {
                    int done = 0;
                    var owner = windowHandle;
                    var errors = new List<string>();

                    foreach (var path in paths)
                    {
                        try
                        {
                            var probe = NameRules.NeedsLiteralPath(path) ? NameRules.LiteralPath(path) : path;
                            if (!Directory.Exists(probe) && !File.Exists(probe))
                            {
                                errors.Add($"{Path.GetFileName(path)}: no longer there");
                                continue;
                            }

                            // A name ending in a dot or a space is one the shell and
                            // every ordinary call trim — "report." would delete
                            // "report". Only the literal path reaches it, and the
                            // Recycle Bin does not take those.
                            if (NameRules.NeedsLiteralPath(path))
                            {
                                if (!permanent)
                                {
                                    errors.Add($"{Path.GetFileName(path)}: Windows cannot put a name ending in a dot " +
                                               "or a space in the Recycle Bin; Shift+Delete deletes it permanently");
                                    continue;
                                }

                                var literal = NameRules.LiteralPath(path);
                                if (Directory.Exists(literal)) Directory.Delete(literal, recursive: true);
                                else File.Delete(literal);
                                done++;
                                continue;
                            }

                            // Recycling goes through the shell with its "this will
                            // be deleted permanently" warning switched on — see
                            // ShellDelete. Answered No, the item stays.
                            if (!permanent)
                            {
                                if (!ShellDelete.Recycle(path, owner))
                                {
                                    errors.Add($"{Path.GetFileName(path)}: kept, because it could only have been deleted permanently");
                                    continue;
                                }
                            }
                            else if (Directory.Exists(path))
                                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, option);
                            else
                                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, option);
                            done++;
                        }
                        catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
                    }

                    return (done, errors);
                });
            }
            finally { _deleting = false; _suppressWatch--; }

            if (IsDisposed) return;

            await RefreshAsync(landOn, folder: origin);
            if (IsDisposed) return;

            if (outcome.Errors.Count > 0)
            {
                AnnounceOperation("delete.failed", $"Deleted {outcome.Done}, {outcome.Errors.Count} failed");
                ShowErrors(outcome.Errors);
            }
            else
            {
                var said = $"Deleted {outcome.Done} item{(outcome.Done == 1 ? "" : "s")}";

                // Where they went is the only difference between these, and this
                // is the one place that knows it: the Drive trash, gone for good,
                // or the Recycle Bin. The sentence is the same either way — the id
                // is what says which, and what decides whether anybody hears it.
                //
                // The Drive case has its own pair, and only the batch belongs
                // here: the Drive layer already names each file as it goes, so
                // for a single item this would be the same event said twice.
                // Claiming delete.recycled for something that never reached a
                // Recycle Bin would be wrong twice over.
                if (onDrive)
                {
                    if (outcome.Done > 1)
                        AnnounceOperation("drive.trash.many",
                            $"{outcome.Done} items moved to the Google Drive trash");
                }
                else if (permanent) AnnounceOperation("delete.permanent", said);
                else AnnounceOperation(outcome.Done == 1 ? "delete.recycled" : "delete.recycled.many", said);
            }
        }

        /// <summary>
        /// Returns the keyboard to the list.
        ///
        /// Anything modal — a properties sheet, an input box, an error — leaves
        /// focus on the form rather than on the list when it closes, and the form
        /// is a place where no key does anything at all. The paths that end in a
        /// refresh get this for free; the ones that do not have to ask.
        /// </summary>
        /// <summary>
        /// Where a context menu should appear: just under the focused row.
        ///
        /// Asks the control for the row's rectangle by index rather than through
        /// FocusedItem. In virtual mode that property's getter *constructs* the
        /// item — raising RetrieveVirtualItem and formatting every column so the
        /// caller can read one rectangle — and worse, it hands back our own
        /// cached object, whose Bounds belong to wherever it last happened to be
        /// drawn.
        /// </summary>
        private static Point MenuAnchor(ListView list)
        {
            int index = FocusedIndex(list);
            if (index >= 0 && index < list.VirtualListSize)
            {
                try
                {
                    var r = list.GetItemRect(index);
                    return list.PointToScreen(new Point(r.Left + 20, r.Bottom));
                }
                catch
                {
                    // A row that has scrolled out from under us has no rectangle.
                }
            }
            return list.PointToScreen(new Point(20, 20));
        }

        private void RestoreListFocus()
        {
            if (!IsDisposed && Visible) Active.List.Focus();
        }

        private async void ShowProperties()
        {
            var path = FocusedPath();
            if (path == null) { Announce("Nothing selected"); return; }

            try
            {
                // On a worker. ShowProperties is ShellExecuteEx with
                // SEE_MASK_INVOKEIDLIST, which resolves the shell's id list for
                // the path before it returns — and on a share that is not
                // answering, that resolution is the window not repainting and
                // not taking keys, with nothing said. Every other per-item shell
                // touch on this keystroke path is already off the UI thread.
                //
                // The handle is read here, where it is safe to, and the sheet
                // that opens is the shell's own window rather than one of ours.
                var owner = Handle;
                bool shown = await ShellContextMenu.ShowPropertiesAsync(owner, path);

                if (!shown) Announce("Could not open properties", isError: true);
            }
            catch (Exception ex)
            {
                // async void: nothing above catches, and an unhandled one here
                // takes the window with it.
                Announce($"Could not open properties: {ex.Message}", isError: true);
            }

            RestoreListFocus();
        }

        private void ShowShellMenuForSelection()
        {
            var paths = SelectedPaths();
            if (paths.Length == 0)
            {
                if (IsDrivesView) { Announce("Nothing selected"); return; }
                paths = new[] { Active.CurrentPath };
            }

            var list = Active.List;
            Point where = MenuAnchor(list);

            switch (ShellContextMenu.Show(this, paths, where.X, where.Y))
            {
                case ShellContextMenu.MenuOutcome.Shown:
                    Refresh_();
                    break;

                // Building it is two shell calls per selected item, measured at
                // 8ms each on this machine — so the whole window used to stop
                // for twenty-four seconds on three thousand files and much
                // longer on a real folder. Refused rather than half-built: a
                // Windows menu that quietly applied to the first few hundred of
                // a selection would be the worse bug by a distance.
                case ShellContextMenu.MenuOutcome.TooSlow:
                    AnnounceOperation("shell.menu.slow",
                        $"Windows would take too long to build a menu for {paths.Length} items. " +
                        "Copy, Cut, Paste, Delete and Rename in this application have no such limit.");
                    RestoreListFocus();
                    break;

                case ShellContextMenu.MenuOutcome.MixedFolders:
                    Announce("The Windows menu can only act on items from one folder. " +
                             "Select items from a single folder, or use this application's own commands.",
                             isError: true);
                    break;

                default:
                    Announce("Windows menu unavailable here", isError: true);
                    break;
            }
        }

        private async void CalculateFocusedFolderSize()
        {
            // A drive is not a folder to walk: the whole volume, uncancellable,
            // over the drive's own size column.
            if (IsDrivesView) { Announce("Folder size is not available for drives", isError: true); return; }

            var path = FocusedPath();
            if (path == null) { Announce("Select a folder first"); return; }

            // Taken now: the answer belongs to this tab even if another is in
            // front by the time it arrives.
            var pane = Active;

            // Off the UI thread, because the path came from the list and the list
            // can be showing a share whose server has gone away. Directory.Exists
            // blocks for seconds on one of those, and this runs from a keystroke —
            // the exact freeze DirectoryExistsAsync exists to prevent, reached by
            // calling the synchronous one two lines above where the rule is
            // written down.
            if (!await DirectoryExistsAsync(path)) { Announce("Select a folder first"); return; }

            AnnounceOperation("size.calculating", $"Measuring {Path.GetFileName(path)}");
            var result = await _folderSizes.CalculateAsync(path);
            if (IsDisposed) return;

            var size = SizeFormatter.Format(result.Bytes, _settings.SizeUnits);
            SetSizeOverride(pane, path, size + (result.Complete ? "" : "+"));

            // One id for both endings. A walk that ran out of time still answers
            // the question that was asked, and the sentence says it is partial.
            AnnounceOperation("size.done", result.Complete
                ? $"{Path.GetFileName(path)}: {size}, {result.Files} files in {result.Folders} folders"
                : $"{Path.GetFileName(path)}: {size} so far, incomplete");
        }

        /// <summary>
        /// Fills the Size column for every folder on screen, one at a time.
        ///
        /// Bound to the token the pane was loaded with, so navigating away stops
        /// it. Without that it kept walking the folders of a view the user had
        /// already left — the single worst offender for the app feeling busy
        /// long after it should have gone quiet — and wrote its answers into a
        /// pane showing something else entirely.
        ///
        /// Never onto the Drive letter. Measuring a folder means walking its
        /// whole subtree, and enumerating a cloud directory that has not been
        /// opened yet *is* a Drive listing — about 1.3 seconds each. So switching
        /// folder sizes to Automatic quietly asked for one network request per
        /// directory in the entire Drive, restarted on every navigation, which is
        /// exactly the thing the provider is built not to do: "Placing the whole
        /// tree at mount time would be one Drive listing per folder for a library
        /// nobody has opened yet." It also populates the tree as a side effect,
        /// so the cost does not even show up as a folder size anybody asked for.
        ///
        /// Only the automatic sweep is refused. Ctrl+Shift+S on a Drive folder
        /// still measures it, because that is somebody asking.
        /// </summary>
        private async Task CalculateAllFolderSizesAsync(Pane pane, CancellationToken token)
        {
            // Snapshot the paths: the entry list is replaced wholesale on the
            // next navigation, and iterating the live one would walk the new
            // folder's contents under the old folder's token.
            var drive = Drive;
            var folders = new List<string>();
            foreach (var e in pane.Entries)
            {
                if (!e.IsDir) continue;
                if (drive != null && drive.Owns(e.Path)) continue;
                folders.Add(e.Path);
            }

            foreach (var folder in folders)
            {
                if (IsDisposed || token.IsCancellationRequested) return;

                // No cache probe here, deliberately. TryGetCached looks cheap and
                // is not: it asks the filesystem for the folder's last-write time,
                // which is a syscall, and this loop runs on the UI thread once per
                // subfolder on screen. A folder of five hundred already-measured
                // subfolders on a share that has gone to sleep is five hundred
                // blocking round trips on the thread that paints the window —
                // arrived at through a *cache hit*, which is the path that is
                // supposed to be the fast one.
                //
                // CalculateAsync already checks the same cache on the worker and
                // returns the same answer, Complete and all. The only thing given
                // up is a Task.Run per cached folder, which is microseconds.
                var result = await _folderSizes.CalculateAsync(folder, token);
                if (IsDisposed || token.IsCancellationRequested) return;

                SetSizeOverride(pane, folder,
                    SizeFormatter.Format(result.Bytes, _settings.SizeUnits) + (result.Complete ? "" : "+"));
            }
        }

        private void SetSizeOverride(Pane pane, string path, string text)
        {
            if (!pane.IndexByPath.TryGetValue(path, out int i)) return;
            if (i < 0 || i >= pane.Entries.Count) return;

            pane.Entries[i].SizeOverride = text;
            EvictItem(pane, i);
            try { if (pane.List.IsHandleCreated) pane.List.RedrawItems(i, i, true); } catch { }
        }

        private void OpenPreferences() => OpenPreferences(null);

        /// <summary>
        /// <paramref name="startCategory"/> opens the dialog already on that page.
        /// The audio preferences are reached from the Audio menu, and landing on
        /// Speech and arrowing down six categories to get there is six things to
        /// listen to on the way.
        /// </summary>
        /// <summary>Whether Preferences is up, so nothing opens another on top of it.</summary>
        public bool PreferencesOpen { get; private set; }

        /// <summary>
        /// Set by the tray while one of the audio dialogs is up. Opened with the
        /// window hidden, such a dialog has no owner, and a window shown
        /// afterwards is not disabled by it — so Ctrl+P could open Preferences
        /// on top of it.
        /// </summary>
        public static bool AudioDialogOpen { get; set; }

        private void OpenPreferences(string? startCategory)
        {
            if (PreferencesOpen) return;

            // Said: the audio dialog may have no owner and be behind the window,
            // and a key that does nothing reads as a broken one.
            if (AudioDialogOpen)
            {
                Announce("Close the audio dialog first", isError: true);
                return;
            }
            PreferencesOpen = true;
            try { OpenPreferencesCore(startCategory); }
            finally { PreferencesOpen = false; }
        }

        private void OpenPreferencesCore(string? startCategory)
        {
            // What the dialog started from, so OK can apply what was changed in it
            // and nothing else. See Settings.WithChanges.
            var before = _settings.Clone();
            using var dialog = new SettingsForm(_settings, startCategory);
            var outcome = dialog.ShowDialog(this);

            // Put the keyboard back in the list.
            //
            // Closing the dialog left focus on the form itself, which is a place
            // no key does anything: arrows did not move, letters did not search,
            // and there was nothing for a screen reader to read. Only the OK path
            // recovered, and then only as a side effect of the refresh it does.
            RestoreListFocus();

            if (outcome == DialogResult.Retry)
            {
                Program.ApplySettings(new Settings
                {
                    LastFolder = Active.CurrentPath,
                    LastFolderTab2 = Other.CurrentPath,
                });
                AnnounceOperation("settings.reset", "Preferences reset to defaults");
                return;
            }

            if (outcome != DialogResult.OK) return;

            // Announced from what the write actually did. It said "saved"
            // unconditionally, because Save swallowed its own failure and
            // returned void — so a preferences file that could not be written
            // reported success and then lost the change on the next launch.
            if (Program.ApplySettings(Settings.WithChanges(_settings, before, dialog.Result)))
                AnnounceOperation("settings.saved", "Preferences saved");
            else
                AnnounceOperation("settings.failed",
                    "Preferences could not be saved. The change applies to this session only.");
        }

        private void AnnounceFolder()
        {
            var count = Active.Entries.Count;
            var where = IsDrivesView ? "Drives" : Active.CurrentPath;
            Announce($"Tab {_activeIndex + 1}, {where}, {count} item{(count == 1 ? "" : "s")}");
        }

        // ---------- Status & speech ----------

        /// <summary>Summing sizes across a very large selection is not worth the pause it causes.</summary>
        private const int MaxSelectionToSum = 5000;

        /// <summary>
        /// Non-zero while the list is being rebuilt or bulk-selected, so the
        /// storm of selection changes that causes does not each trigger a walk
        /// of the whole selection. One update is done at the end instead.
        /// </summary>
        private int _suppressStatus;

        private System.Windows.Forms.Timer? _statusTimer;

        /// <summary>
        /// Coalesces status updates onto the next idle moment.
        ///
        /// Holding an arrow key fires two selection changes per row, and totalling
        /// the selection is proportional to how much is selected. Doing that
        /// synchronously meant a held arrow key in a large folder spent all its
        /// time re-totalling rather than moving. The status bar being 60ms stale
        /// is invisible; the lag was not.
        /// </summary>
        private void ScheduleStatusUpdate()
        {
            if (_suppressStatus > 0 || IsDisposed) return;

            // Subscribed once, not on every keystroke. Adding and removing a
            // delegate per arrow press allocates and walks the invocation list for
            // no reason at all.
            if (_statusTimer == null)
            {
                _statusTimer = new System.Windows.Forms.Timer { Interval = 60 };
                _statusTimer.Tick += StatusTimerTick;
            }

            _statusTimer.Stop();
            _statusTimer.Start();
        }

        private void StatusTimerTick(object? sender, EventArgs e)
        {
            _statusTimer?.Stop();
            if (!IsDisposed) UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (IsDisposed) return;

            // Once. It was counted twice, so every figure the perf log reported
            // for status updates was double — and that log is how this codebase
            // decides what is actually slow.
            PerfCounters.StatusUpdate();
            _statusTimer?.Stop();   // a pending tick would only repeat this

            var pane = Active;
            int total = pane.Entries.Count;
            int selected = pane.List.SelectedIndices.Count;
            var prefix = $"Tab {_activeIndex + 1} — ";

            // A list that stopped at the search cap says so, every time it is
            // read rather than once when the search finished. The announcement is
            // gone the moment anything else speaks, and "10000 items" is a
            // straight untruth to somebody who cannot see that the list runs off
            // the end — which is the whole reason the cap is reported at all.
            var more = pane.SearchTruncated ? ", and there are more" : "";

            if (selected == 0)
            {
                _statusLabel.Text = $"{prefix}{NameRules.Items(total)}{more}";
                return;
            }

            if (selected > MaxSelectionToSum)
            {
                _statusLabel.Text = $"{prefix}{NameRules.Items(total)}{more}, {selected} selected";
                return;
            }

            long bytes = 0;
            var entries = pane.Entries;
            foreach (int i in pane.List.SelectedIndices)
            {
                if (i < 0 || i >= entries.Count) continue;
                var entry = entries[i];
                // Folders carry -1 as "not measured"; adding it would understate
                // the total by one byte per folder in the selection.
                if (!entry.IsDir && entry.Size > 0) bytes += entry.Size;
            }

            _statusLabel.Text =
                $"{prefix}{NameRules.Items(total)}{more}, {selected} selected " +
                $"({SizeFormatter.Format(bytes, _settings.SizeUnits)})";
        }

        /// <summary>
        /// Puts a message in the status bar and keeps it there.
        ///
        /// Cancelling the pending status update is the point. Status updates are
        /// coalesced onto a short timer, so a selection change that happened just
        /// before an announcement would fire a moment afterwards and quietly
        /// replace "Deleted 3 items" with the item count — the message would
        /// appear and vanish before it could be read.
        /// </summary>
        private void ShowStatus(string message)
        {
            _statusTimer?.Stop();
            _statusLabel.Text = message;
        }

        private void Announce(string message, bool isError = false)
        {
            ShowStatus(message);
            if (!_settings.SpeakEnabled) return;
            if (isError && !_settings.SpeakErrors) return;
            Speech.Speak(message, _settings.InterruptSpeech);
        }

        private void AnnounceOperation(string message) => AnnounceOperation(null, message);

        /// <summary>
        /// An operation's result, routed by its catalogue id.
        ///
        /// The id decides where it may go and the sentence is what goes there —
        /// the caller knows how many items and which one failed, and the
        /// catalogue's own template exists for previewing rather than for saying.
        ///
        /// The old "announce operations" switch still applies on top: it silences
        /// the whole group, and the catalogue silences one message inside a group
        /// that is otherwise on. A message with no id keeps the old behaviour
        /// exactly, so nothing goes quiet merely for not being catalogued yet.
        ///
        /// The status bar gets it either way. That is not a channel any more —
        /// writing a silent line costs nothing and interrupts nobody, and a
        /// message routed nowhere used to leave the bar showing whatever it was
        /// showing before, which is a stale answer to a question somebody has
        /// just asked.
        /// </summary>
        private void AnnounceOperation(string? notificationId, string message)
        {
            ShowStatus(message);

            bool speak = _settings.SpeakEnabled && _settings.SpeakOperations;

            if (!string.IsNullOrEmpty(notificationId) &&
                Notifications.ById(notificationId) != null)
            {
                speak = speak &&
                    Notifications.ChannelsFor(notificationId, _settings) == NotificationChannel.Speech;
            }

            if (!speak) return;
            Speech.Speak(message, _settings.InterruptSpeech);
        }

        /// <summary>
        /// The failures from an operation, as a list somebody can get to the end
        /// of.
        ///
        /// Each line is capped, and that is not tidiness. Google answers a 502
        /// with a full HTML page — doctype, stylesheet, a link to a picture of a
        /// robot — and one upload failure carrying that pushed the other
        /// nineteen off the screen and, read aloud, was a minute of markup
        /// before the sentence saying which file it was. The Drive layer cuts
        /// these down at the source now; this is the second line of defence, for
        /// every other thing that can hand back an error message of unbounded
        /// length.
        ///
        /// The file's name is at the front of every one of these, so a line is
        /// cut from the end: what is thrown away is the tail of somebody else's
        /// explanation, and what is kept is which file it was about.
        /// </summary>
        private void ShowErrors(IReadOnlyList<string> errors)
        {
            if (errors.Count == 0) return;

            const int longestLine = 300;

            var lines = errors.Take(20).Select(one =>
            {
                one = (one ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                return one.Length <= longestLine ? one : one[..longestLine] + "…";
            });

            var text = string.Join(Environment.NewLine, lines);
            if (errors.Count > 20) text += $"{Environment.NewLine}… and {errors.Count - 20} more";

            MessageBox.Show(this, text, "Some items could not be processed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RestoreListFocus();
        }

        // ---------- Lifetime ----------

        /// <summary>
        /// Something the audio player did, reported here rather than straight to
        /// NVDA so the status bar agrees with it. The player's shortcuts are
        /// global, so they fire whether or not this window has the keyboard.
        ///
        /// <paramref name="speak"/> false writes the line without saying it. Most
        /// of the player's shortcuts are set that way: turning the volume down is
        /// already audible, and announcing it talks over the music it just
        /// changed. The status bar still carries it for anyone reading.
        /// </summary>
        public void AnnounceAudio(string message, bool speak)
        {
            if (speak) { Announce(message); return; }
            ShowStatus(message);
        }

        /// <summary>
        /// Puts the keyboard back in the list after something modal the tray put
        /// on screen. Anything that closes leaves focus on the form itself, where
        /// no key does anything at all.
        /// </summary>
        public void RestoreListFocusExternally() => RestoreListFocus();

        public void OpenPreferencesExternally() => OpenPreferences();

        /// <summary>
        /// Takes on settings changed from outside while quitting, so the save on
        /// the way out writes them rather than the ones this window started with.
        /// </summary>
        internal void AdoptSettings(Settings settings) => _settings = settings;

        private bool _forceClosing;

        public void ForceClose()
        {
            _forceClosing = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            CaptureWindowGeometry();
            CaptureState();

            // Quitting saves once, last, with anything a command-line change
            // wrote meanwhile merged in. A save of its own here went over such a
            // change and hid it from that merge.
            if (!_forceClosing) _settings.Save();

            if (!_forceClosing && _settings.CloseToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            // A transfer used to hold this window modal, so there was no way to
            // reach the close box while one was running. Now that there is, it
            // has to be asked about: closing takes the process down with it and
            // a copy dies wherever it had got to, which for a move means files
            // deleted from one end and not yet written to the other.
            //
            // Only for a close somebody actually asked for. A shutdown or a task
            // manager is not a question anybody is there to answer, and a dialog
            // nobody can see is a process that will not exit.
            if (!_forceClosing && e.CloseReason == CloseReason.UserClosing &&
                !ConfirmAbandonTransfers("Close"))
            {
                e.Cancel = true;
                return;
            }

            base.OnFormClosing(e);
        }

        /// <summary>
        /// Asks before something ends the process out from under a transfer.
        /// True when there is nothing running, or when it was agreed to.
        ///
        /// <paramref name="verb"/> is the word for what is about to happen —
        /// "Close", "Quit", "Restart" — because "abandon it?" needs to say what
        /// would be doing the abandoning, and all three reach here.
        /// </summary>
        public bool ConfirmAbandonTransfers(string verb)
        {
            if (IsDisposed || RunningTransfers <= 0) return true;

            var question = RunningTransfers == 1
                ? $"A copy is still running. {verb} anyway and abandon it?"
                : $"{RunningTransfers} copies are still running. {verb} anyway and abandon them?";

            if (MessageBox.Show(this, question, "Still copying",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                return true;

            // Anything modal leaves the keyboard on the form, where no key does
            // anything at all.
            RestoreListFocus();
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _availabilityTimer?.Stop();
                _availabilityTimer?.Dispose();
                _statusTimer?.Stop();
                _statusTimer?.Dispose();
                _prefetchTimer?.Stop();
                _prefetchTimer?.Dispose();
                _tabAnnounceTimer?.Stop();
                _tabAnnounceTimer?.Dispose();
                _watchTimer?.Stop();
                _watchTimer?.Dispose();
                foreach (var pane in _panes) DisarmWatcher(pane);

                // Cancelled, not disposed: background enumerations and size walks
                // are still holding these tokens, and pulling the source out from
                // under them throws on a thread with nobody to catch it.
                foreach (var pane in _panes)
                {
                    try { pane.EnumCts?.Cancel(); } catch { }
                }

                _folderSizes.Dispose();
                _contextMenu?.Dispose();
                _ownedFont?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}





