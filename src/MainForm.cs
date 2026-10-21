using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

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

            /// <summary>
            /// The name's key for <see cref="NameRules.CompareNames(string, byte[], string, string, byte[], string)"/>,
            /// while a listing is being sorted, and null otherwise. Made on the
            /// worker, read by the merge on the UI thread, and dropped once the
            /// listing is in order: nothing sorts it again, and it would be about
            /// eighty bytes a row kept for nothing. A null key is never wrong,
            /// only slower, so a reader racing the drop is still right.
            /// </summary>
            public byte[]? NameKey;

            /// <summary>
            /// Type-ahead's view of the row, folded once rather than on every
            /// keystroke: <c>TypeAhead.Fold</c> of what the Name column shows with
            /// extensions on. For plain ASCII that is the name itself and costs
            /// nothing. Made on the worker as a folder is listed, or on first use
            /// for rows made anywhere else.
            /// </summary>
            private string? _folded;

            /// <summary>
            /// How much of <see cref="_folded"/> is the name without its
            /// extension, or -1 until it is asked. A slice rather than a second
            /// string, because folding works a character at a time and a plain
            /// extension folds to itself; <see cref="_foldedStem"/> is for the
            /// rare name where that cannot be shown to hold.
            /// </summary>
            private int _stemLength = -1;
            private string? _foldedStem;

            /// <summary>Folds now, off the UI thread, so the first keystroke does not.</summary>
            public void PrepareTypeAhead()
            {
                var whole = _folded = TypeAhead.Fold(DisplayOverride ?? Name);
                MeasureStem(whole);
            }

            /// <summary>
            /// What type-ahead matches against: <c>TypeAhead.Fold(DisplayNameOf(entry))</c>,
            /// without building either string on the keystroke.
            /// </summary>
            public ReadOnlySpan<char> FoldedDisplayName(bool showExtensions)
            {
                var whole = _folded ??= TypeAhead.Fold(DisplayOverride ?? Name);
                if (showExtensions || DisplayOverride != null || IsDir) return whole;

                if (_stemLength < 0) MeasureStem(whole);
                return _foldedStem ?? whole.AsSpan(0, _stemLength);
            }

            private void MeasureStem(string whole)
            {
                int dot = Name.LastIndexOf('.');
                if (DisplayOverride != null || IsDir || dot <= 0) { _stemLength = whole.Length; return; }

                // Plain ASCII: the fold is the name, and the stem is up to the dot.
                if (ReferenceEquals(whole, Name)) { _stemLength = dot; return; }

                // Otherwise the fold of the name is the fold of its stem followed
                // by the fold of its extension, which for a plain extension is
                // the extension. Checked against folding the stem on its own for
                // 591,000 names, real ones and ones built to break it.
                var extension = Name.AsSpan(dot);
                if (System.Text.Ascii.IsValid(extension) && whole.AsSpan().EndsWith(extension, StringComparison.Ordinal))
                {
                    _stemLength = whole.Length - extension.Length;
                    return;
                }

                _foldedStem = TypeAhead.Fold(System.IO.Path.GetFileNameWithoutExtension(Name));
                _stemLength = _foldedStem.Length;
            }
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
            /// The folder a watcher is being built for on a worker, or null.
            /// Building one opens the folder, which on a sleeping share is
            /// seconds, so it is never done on the UI thread.
            /// </summary>
            public string? ArmingPath;

            /// <summary>
            /// Bumped whenever the pane's watcher is let go, so a watcher still
            /// being built for the folder before is thrown away when it arrives,
            /// and its early events are not counted against this one.
            /// </summary>
            public int WatchGeneration;

            /// <summary>When the watcher's changes are to be acted on, and which.</summary>
            public readonly WatchDebounce Debounce = new();

            /// <summary>
            /// Asks Windows to say before the watched folder's drive is removed,
            /// so the watcher can let go of it. Null for a share or no watcher.
            /// </summary>
            public RemovalWatch? Removal;

            /// <summary>
            /// Set from the watcher's thread, cleared on the UI thread. Volatile
            /// because those are different threads and nothing else synchronises
            /// them; a missed flag is a list that stays wrong.
            /// </summary>
            public volatile bool NeedsRefresh;

            /// <summary>
            /// The archive this pane is inside, opened with Enter, or null.
            /// <see cref="CurrentPath"/> is then a path through it —
            /// "C:\Music\album.zip\Disc 1" — that exists nowhere on disk, so
            /// nothing that reads or writes the folder may be pointed at it:
            /// there is no watcher, and every command that would change the
            /// archive refuses (<see cref="RefuseInArchive"/>).
            /// </summary>
            public ArchiveListing? Archive;
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
            /// A list, not a table. Since .NET 9 a details-view ListView tells
            /// screen readers it is a table, and NVDA then reads only the cell
            /// under the cursor: on 3.1.0 every row was its name alone, no size
            /// and no date (Conner, the day it shipped). Said to be a list, it is
            /// read a row at a time again, as on .NET 8.
            /// </summary>
            public FileListView() => AccessibleRole = AccessibleRole.List;

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
        private bool IsDrivesView => Active.CurrentPath == DrivesPath;

        /// <summary>
        /// The Google Drive mount, when there is one. Navigation has to fill a
        /// cloud folder in before listing it — Windows will not ask on our behalf
        /// — so the pane loader needs to be able to reach it.
        /// </summary>
        public GoogleDrive? Drive { get; set; }

        /// <summary>
        /// For walks (sizes, search) on a worker: a folder belonging to the Drive
        /// mount, by the letter or by the sync root it sits on. Walks hand over
        /// the \\?\ form, so it is made plain first.
        /// </summary>
        private bool OnDriveFolder(string path) => Drive?.Owns(NameRules.PlainPath(path)) == true;

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

            // A walk of C:\Users reaches the Drive sync root under AppData, where
            // every folder is a Drive listing. Not walked, as the web app's
            // sizes are not (ConnectFiles).
            _folderSizes.Skip = OnDriveFolder;

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

            // Cheap poll so an unplugged drive is noticed even while idle. Only
            // while the window is shown: OnVisibleChanged starts and stops it.
            _availabilityTimer = new System.Windows.Forms.Timer { Interval = 4000 };
            _availabilityTimer.Tick += (_, _) => CheckCurrentPathStillExists();

            AppleMusic.Grew += OnAppleMusicGrew;
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
                    UpdateTitle();
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
            _settings.FocusedPathTab1 = RealFocusOf(_panes[0]) ?? "";
            _settings.FocusedPathTab2 = RealFocusOf(_panes[1]) ?? "";

            if (_settings.RememberLastFolder)
            {
                _settings.LastFolder = RealFolderOf(_panes[0]);
                _settings.LastFolderTab2 = RealFolderOf(_panes[1]);
            }
        }

        /// <summary>
        /// The folder to come back to: inside an archive, the folder the archive
        /// is in, because the path through it is not a folder anything can open.
        /// In Apple Music, the drives list with the cursor on the Apple Music row:
        /// a list inside it is only known once it has been asked of Apple.
        /// </summary>
        private static string RealFolderOf(Pane pane) =>
            AppleMusic.Is(pane.CurrentPath) ? DrivesPath
            : pane.Archive is { } archive ? SafeParent(archive.ArchivePath) ?? pane.CurrentPath : pane.CurrentPath;

        private static string? RealFocusOf(Pane pane) =>
            AppleMusic.Is(pane.CurrentPath) ? AppleMusic.Root
            : pane.Archive is { } archive ? archive.ArchivePath : FocusedPathOf(pane);

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

        /// <summary>Help, Changelog (Shift+F1): the built-in CHANGES.txt, no network.</summary>
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

            static string Plain(Exception ex) => NameRules.PlainUpdateFailure(ex);
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
            if (_syncConfiguratorItem != null) _syncConfiguratorItem.Visible = DriveMonitorForm.OnFileMenu(settings);

            // Hidden files, sorting and folders-first change the other tab's
            // listing too, and only the front one is reloaded now.
            // Quietly, as the watcher does. Refresh_ was the F5 path: it re-ran a
            // search on screen ("Searching for..."), re-read an archive or a
            // whole Drive listing, and read the first row aloud over
            // "Preferences saved".
            foreach (var pane in _panes) { pane.NeedsRefresh = true; pane.ReloadQuietly = true; }
            ScheduleWatchCheck();
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
                if (folderCheck == FolderProbe.Missing && await Bounded(() => File.Exists(NameRules.ExactPath(target))))
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
            // On the pane the probe was for: a tab switch during the probe would
            // otherwise land the folder in the other tab.
            await NavigateAsync(target, preferPath: select, onPane: pane);
            if (IsDisposed) return;

            if (Visible && pane == Active) pane.List.Focus();
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
                list.KeyUp += List_KeyUp;
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

        private ToolStripMenuItem? _syncConfiguratorItem;

        /// <summary>File, Google Drive sync configurator: the folder pairs, saved as they change.</summary>
        private void OpenSyncConfigurator()
        {
            using (var dialog = new DriveMonitorForm(_settings.DriveSyncPairs)) dialog.ShowDialog(this);
            RestoreListFocus();
        }

        /// <summary>Ctrl+Shift+Y: every synced folder that is not paused, now.</summary>
        private void SyncAllNow()
        {
            var monitor = DriveMonitor.Current;
            if (monitor == null || !DriveMonitorForm.OnFileMenu(_settings)) { Announce("Google Drive is not switched on"); return; }
            AnnounceOperation("sync.all", monitor.SyncAllNow());
        }

        private void PauseOrResumeAll()
        {
            var monitor = DriveMonitor.Current;
            if (monitor == null) return;
            AnnounceOperation("sync.all", monitor.PauseAll(!monitor.AllPaused));
        }

        private void BuildMenu()
        {
            var menu = new MenuStrip();

            // A menu does not take the keyboard from the list, so the list's
            // Leave never runs and a name held back while you moved between
            // folders stayed held back: closing a menu, NVDA announced the list
            // by a folder left long before ("Drives" in Apple Music's Songs).
            // While a menu is open, renaming the list is as silent as on Leave.
            menu.MenuActivate += (_, _) => { _menuOpen = true; foreach (var p in _panes) ApplyPendingName(p); };
            menu.MenuDeactivate += (_, _) => { _menuOpen = false; _menuClosedAt = Environment.TickCount64; };

            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(Item("&Open", Keys.None, ActivateFocused));
            var playCd = Item("Play &CD", Keys.None, PlayCd);
            file.DropDownItems.Add(playCd);
            // Each of these changes the folder on screen, so inside an archive,
            // which is read-only, they are hidden as the menu opens. Their keys
            // still answer, with "Archives are read-only here".
            var changesFolder = new[]
            {
                Item("New f&ile", Keys.F7, CreateNewFile),
                Item("New &folder", Keys.F8, CreateNewFolder),
                Item("&Rename", Keys.F2, RenameSelected),
                Item("&Delete", Keys.Delete, () => DeleteSelected(false)),
                Item("Delete &permanently", Keys.Shift | Keys.Delete, () => DeleteSelected(true)),
            };
            foreach (var item in changesFolder) file.DropDownItems.Add(item);
            _archiveHidden.AddRange(changesFolder);

            // Only while Google Drive is switched on (Connect and Disconnect
            // switch it), decided as the menu opens and after Preferences.
            _syncConfiguratorItem = Item("Google Drive &sync...", Keys.None, OpenSyncConfigurator);
            _syncConfiguratorItem.Visible = DriveMonitorForm.OnFileMenu(_settings);
            file.DropDownItems.Add(_syncConfiguratorItem);
            file.DropDownOpening += (_, _) => _syncConfiguratorItem.Visible = DriveMonitorForm.OnFileMenu(_settings);

            // Every synced folder at once. The key works whenever the window
            // has focus; the items show only while there are synced folders.
            var syncAll = Item("S&ync all Google Drive folders now", Keys.Control | Keys.Shift | Keys.Y, SyncAllNow);
            var pauseAll = Item("Pa&use all syncing", Keys.None, PauseOrResumeAll);
            file.DropDownItems.Add(syncAll);
            file.DropDownItems.Add(pauseAll);
            file.DropDownOpening += (_, _) =>
            {
                var monitor = DriveMonitor.Current;
                bool any = DriveMonitorForm.OnFileMenu(_settings) && monitor != null && monitor.Pairs.Count > 0;
                syncAll.Visible = pauseAll.Visible = any;
                if (any) pauseAll.Text = monitor!.AllPaused ? "Res&ume all syncing" : "Pa&use all syncing";
            };
            file.DropDownOpening += (_, _) => ShowArchiveHidden();

            file.DropDownItems.Add(new ToolStripSeparator());
            var sortMenu = BuildSortMenu();
            file.DropDownItems.Add(sortMenu);
            // Apple Music lists are not folders, so the item does not call them one;
            // and where an Apple Music list has nothing to sort (its top, Library,
            // Search) the item is not offered. A submenu whose only item is
            // disabled leaves NVDA stuck in it.
            file.DropDownOpening += (_, _) =>
            {
                bool apple = AppleMusic.Is(Active.CurrentPath);
                sortMenu.Text = apple ? "Sort &by" : "Sort this folder &by";
                // Not in the drives list either, where its items could only do nothing.
                sortMenu.Visible = !IsDrivesView && (!apple || AppleMusic.SortsFor(Active.CurrentPath).Count > 0);
            };
            var properties = Item("P&roperties (Alt+Enter)", Keys.None, ShowProperties);
            file.DropDownItems.Add(properties);
            _archiveHidden.Add(properties);
            file.DropDownItems.Add(new ToolStripSeparator());
            // Also in Preferences, beside Reset.
            file.DropDownItems.Add(Item("&Export settings...", Keys.None, () => ExportSettings(this)));
            file.DropDownItems.Add(Item("I&mport settings...", Keys.None, () => ImportSettings(this, null)));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Item("E&xit", Keys.None, Close));

            // Always on the menu, drive or no drive, at Conner's request: a USB
            // drive comes and goes, and an entry that appears only while it is
            // plugged in is one you cannot find when you go looking. With no
            // disc, the command says so in a dialog.
            menu.Items.Add(file);

            var edit = new ToolStripMenuItem("&Edit");
            edit.DropDownItems.Add(AppleHidden(Item("&Copy", Keys.Control | Keys.C, () => CopyToClipboard(false))));
            var cut = Item("Cu&t", Keys.Control | Keys.X, () => CopyToClipboard(true));
            var paste = Item("&Paste", Keys.Control | Keys.V, PasteClipboard);
            edit.DropDownItems.Add(cut);
            edit.DropDownItems.Add(paste);
            _archiveHidden.Add(cut);
            _archiveHidden.Add(paste);
            edit.DropDownItems.Add(new ToolStripSeparator());
            edit.DropDownItems.Add(AppleHidden(Item("Copy &path", Keys.Alt | Keys.C, CopyPath)));
            edit.DropDownItems.Add(DriveLinkItem("Copy Drive &link", Keys.Alt | Keys.L, makePublic: false));
            edit.DropDownItems.Add(DriveLinkItem("Copy p&ublic Drive link", Keys.Alt | Keys.Shift | Keys.L, makePublic: true));
            edit.DropDownItems.Add(Item("Select &all", Keys.Control | Keys.A, SelectAll));
            edit.DropDownItems.Add(new ToolStripSeparator());
            edit.DropDownItems.Add(Item("Pre&ferences", Keys.Control | Keys.P, OpenPreferences));

            // The same decision the popup makes, made here for the same reason.
            // A menu is read out one item at a time, and two entries about
            // Google Drive are two entries heard on the way past every other one
            // — on a machine with no Drive mounted at all, twice for nothing.
            edit.DropDownOpening += (_, _) =>
            {
                ShowDriveLinkItems();
                ShowArchiveHidden();
            };
            menu.Items.Add(edit);

            var go = new ToolStripMenuItem("&Go");
            go.DropDownItems.Add(Item("&Up one level", Keys.Alt | Keys.Up, GoUp));
            go.DropDownItems.Add(Item("&Drives", Keys.Alt | Keys.D, () => Navigate(DrivesPath)));
            go.DropDownItems.Add(Item("Go to &path…", Keys.Control | Keys.L, GoToPath));
            go.DropDownItems.Add(Item("&Home", Keys.Alt | Keys.Home,
                () => Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));
            var search = _searchHereItem = Item("&Search here…", Keys.F1, SearchHere);
            go.DropDownItems.Add(search);
            _archiveHidden.Add(search);
            go.DropDownOpening += (_, _) => ShowArchiveHidden();
            go.DropDownItems.Add(Item("&Refresh", Keys.F5, Refresh_));
            go.DropDownItems.Add(Item("Res&tart application", Keys.Control | Keys.R, RestartApp));
            menu.Items.Add(go);

            menu.Items.Add(BuildAudioMenu());

            var tools = new ToolStripMenuItem("T&ools");
            tools.DropDownItems.Add(AppleHidden(Item("Calculate folder &size", Keys.Control | Keys.Shift | Keys.S, CalculateFocusedFolderSize)));
            var windowsMenu = Item("&Windows context menu", Keys.None, ShowShellMenuForSelection);
            tools.DropDownItems.Add(windowsMenu);
            _archiveHidden.Add(windowsMenu);
            tools.DropDownOpening += (_, _) => ShowArchiveHidden();
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
            help.DropDownItems.Add(Item("&Changelog", Keys.Shift | Keys.F1, ShowChangelog));
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
        private ToolStripMenuItem _speedItem = null!;
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
            _speedItem = AudioItem(AudioAction.SpeedDialog);
            audio.DropDownItems.Add(_speedItem);

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

            // Apple Music's, shown only while it plays (4.1.6): H and X are free
            // then, because Pitch (H) is hidden while Apple Music plays and X was
            // never used. Repeat above becomes Apple's off, all, this song.
            _shuffleItem = AudioItem(AudioAction.ToggleShuffle);
            _shuffleItem.Text = "S&huffle";
            _upNextItem = AudioItem(AudioAction.UpNext);
            _upNextItem.Text = "Up ne&xt…";
            audio.DropDownItems.Add(_shuffleItem);
            audio.DropDownItems.Add(_upNextItem);
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

            // "&" in a song or artist is a letter, not an access key.
            _nowPlayingItem.Text = state is { HasTrack: true }
                ? ((playing ? "Playing " : "Paused: ") + state.Name).Replace("&", "&&")
                : "Nothing is playing";

            // Apple Music's sound is sealed: what changes the sound itself, and
            // repeat, could only say "That isn't available for Apple Music", so
            // they are left off while it plays. Output device stays: it works.
            bool apple = AppleMusicPlayer.IsActive;
            foreach (var item in new[] { _speedItem, _pitchItem, _limiterItem, _equaliserItem, _silenceItem })
                item.Visible = !apple;

            // Repeat is Apple's own while it plays: off, all, or this song.
            _shuffleItem.Visible = _upNextItem.Visible = apple;
            _shuffleItem.Checked = AppleMusicPlayer.Shuffle;
            if (apple)
            {
                var repeat = AppleMusicPlayer.Repeat;
                _repeatItem.Text = repeat switch
                {
                    AppleMusicPlayer.RepeatMode.All => "&Repeat: all",
                    AppleMusicPlayer.RepeatMode.One => "&Repeat: this song",
                    _ => "&Repeat: off",
                };
                _repeatItem.Checked = repeat != AppleMusicPlayer.RepeatMode.Off;
            }
            else _repeatItem.Text = AudioActions.For(AudioAction.ToggleRepeat).MenuText;
        }

        private ToolStripMenuItem _shuffleItem = null!;
        private ToolStripMenuItem _upNextItem = null!;

        private void OpenAudioPreferences() => OpenPreferences("Audio");

        /// <summary>
        /// Says something chosen from a menu once the menu has gone. A menu runs
        /// its command before it closes, and NVDA then announces the list getting
        /// the keyboard back, which cut off what the command had just said (an
        /// Apple Music song's details, which open no window of their own).
        /// </summary>
        private void AnnounceAfterMenu(string id, string text)
        {
            _ = Task.Delay(450).ContinueWith(_ =>
            {
                try { BeginInvoke(() => { if (!IsDisposed) AnnounceOperation(id, text); }); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }, TaskScheduler.Default);
        }

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
            var state = Program.AudioState;

            // An Apple Music song has no file to show; what Apple says about it
            // is said instead. The song under the cursor first, as for a file.
            if (AppleMusic.Is(Active.CurrentPath) && FocusedPath() is { } row && AppleMusic.Details(row) is { } here)
            {
                AnnounceAfterMenu("applemusic.details", here);
                return;
            }

            // A row inside an archive is not a file to read tags from; what is
            // playing still is, and may well be the track taken out of it.
            var path = Active.Archive == null && !AppleMusic.Is(Active.CurrentPath) ? FocusedPath() : null;

            if (path == null || !AudioFiles.IsAudio(path, _settings.AudioExtensions))
                path = state is { HasTrack: true } ? state.Path : path;

            if (path == null) { Announce("Nothing selected"); return; }

            if (AppleMusic.Is(path))
            {
                var said = AppleMusic.Details(path) ?? state?.Name;
                AnnounceAfterMenu("applemusic.details", string.IsNullOrEmpty(said) ? "Nothing is playing" : said);
                return;
            }

            // The Edit tags button only where it could do something: not on
            // the Drive letter and not on an Ogg file, decided from the path.
            // Nor on a track taken out of an archive: that is a temporary copy,
            // and its tags would vanish with it.
            var song = path;
            bool fromArchive = song.StartsWith(ArchiveBrowser.CacheRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
            Func<IWin32Window, Task<bool>>? editTags =
                !fromArchive && TagEditing.RefusalFor(song, IsOnDrive, null) == null
                    ? owner => EditTagsAsync(new[] { song }, owner)
                    : null;

            using var dialog = new SongPropertiesForm(path, editTags);
            dialog.ShowDialog(this);
            RestoreListFocus();
        }

        private bool IsOnDrive(string path) => Drive is { } drive && drive.Owns(path);

        /// <summary>Shown on the context menu only over an audio file it could tag.</summary>
        private ToolStripMenuItem _editTagsItem = null!;

        /// <summary>
        /// Edit tags for the selected files, or the focused one. Folders and
        /// files that are not audio are left out. Async void like every menu
        /// command, and guarded the same way.
        /// </summary>
        private async void EditTagsForSelection()
        {
            if (RefuseInArchive()) return;
            try
            {
                var paths = SelectedPaths();
                if (paths.Length == 0 && FocusedPath() is { } focused) paths = new[] { focused };

                var audio = paths.Where(p => !IsDirectoryInView(p) &&
                                             AudioFiles.IsAudio(p, _settings.AudioExtensions)).ToArray();
                if (audio.Length == 0)
                {
                    Announce(paths.Length == 0 ? "Nothing selected" : "Select one or more audio files to edit their tags", isError: true);
                    return;
                }

                await EditTagsAsync(audio, this);
                if (!IsDisposed) RestoreListFocus();
            }
            catch (Exception ex)
            {
                if (!IsDisposed) Announce("Could not edit the tags: " + ex.Message, isError: true);
            }
        }

        /// <summary>
        /// Reads the files' tags on a worker, asks for the new ones, writes only
        /// the fields that were changed on a worker, and says how it went in one
        /// message. Answers whether any file was saved.
        ///
        /// Files on the Google Drive letter are never opened from this process
        /// (a stuck read there cannot be killed), so they are refused by path
        /// before anything is read.
        /// </summary>
        private async Task<bool> EditTagsAsync(string[] paths, IWin32Window owner)
        {
            var drive = paths.Where(IsOnDrive).ToList();
            var local = paths.Where(p => !IsOnDrive(p)).ToArray();
            if (local.Length == 0)
            {
                Announce(TagEditing.DriveRefusal, isError: true);
                return false;
            }

            ShowStatus(local.Length == 1 ? "Reading tags" : $"Reading tags of {local.Length} files");
            var current = await Task.Run(() =>
                local.Select(p => TagEditing.IsOggFamily(p) ? null : AudioTags.ReadFields(p)).ToList());
            if (IsDisposed) return false;

            var fields = TagEditing.Merge(current);
            string title = local.Length == 1
                ? "Edit tags — " + Path.GetFileName(local[0])
                : $"Edit tags — {local.Length} files";

            Dictionary<TagField, string> changes;
            bool numberInOrder;
            using (var dialog = new TagEditorForm(title, fields, local.Length))
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK) { ShowStatus("Tags not changed"); return false; }
                changes = dialog.Changes;
                numberInOrder = dialog.NumberInListOrder;
            }

            if (changes.Count == 0 && !numberInOrder)
            {
                Announce("Nothing was changed");
                return false;
            }

            // The player's track is read on this thread, once, before the worker
            // starts: the worker only compares paths.
            var playing = Program.AudioState is { HasTrack: true } state ? state.Path : null;
            bool IsPlaying(string p) => playing != null && string.Equals(p, playing, StringComparison.OrdinalIgnoreCase);

            ShowStatus(local.Length == 1 ? "Saving tags" : $"Saving tags for {local.Length} files");
            // Drive files are reported, never opened.
            var results = drive.Select(p => new TagResult(p, TagEditing.DriveRefusal)).ToList();
            results.AddRange(await Task.Run(() =>
                TagEditing.Apply(local, changes, numberInOrder, onDrive: null, IsPlaying)));
            if (IsDisposed) return false;

            var summary = TagEditing.Summary(results);
            bool anyFailed = results.Any(r => r.Error != null);
            if (anyFailed)
            {
                // A list of reasons is something to read at one's own pace, not a
                // status line that the next arrow press replaces.
                ShowStatus(summary);
                MessageBox.Show(owner, summary, "Edit tags", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else Announce(summary);

            // The rows follow on their own: the folder is watched, and a file
            // rewritten in place updates its row (size, modified) where it is.
            return results.Any(r => r.Error == null);
        }

        /// <summary>Kept so the popup can decide whether they belong on it.</summary>
        private ToolStripMenuItem _extractItem = null!;
        private ToolStripMenuItem _extractHereItem = null!;
        private ToolStripMenuItem _shortcutItem = null!;
        private ToolStripMenuItem _convertAudioItem = null!;
        private ToolStripMenuItem _editAudioItem = null!;

        /// <summary>
        /// Every "Copy Drive link" entry, on whichever menu it sits.
        ///
        /// One list rather than four fields, because the rule is one rule: these
        /// are shown when the focused row is on Google Drive and hidden when it
        /// is not, and a second copy of that rule on a second menu is how the two
        /// menus end up disagreeing about the same file.
        /// </summary>
        private readonly List<ToolStripMenuItem> _driveLinkItems = new();

        /// <summary>
        /// Every menu entry that can only refuse while the pane is inside an
        /// archive — the ones that would change it, and the ones that need the
        /// item to be a real file on disk — on whichever menu it sits. Hidden as
        /// a menu opens over an archive and shown again everywhere else; a hidden
        /// entry keeps its shortcut, which answers for itself.
        /// </summary>
        private readonly List<ToolStripMenuItem> _archiveHidden = new();

        /// <summary>
        /// Entries that work in an archive (copying takes the file out first) but
        /// can only refuse in Apple Music, which has no files: hidden there.
        /// </summary>
        private readonly List<ToolStripMenuItem> _appleHidden = new();

        private ToolStripMenuItem AppleHidden(ToolStripMenuItem item)
        {
            _appleHidden.Add(item);
            return item;
        }

        /// <summary>Go, Search here: hidden in an archive, kept in Apple Music.</summary>
        private ToolStripMenuItem? _searchHereItem;

        private void ShowArchiveHidden()
        {
            // Apple Music is read-only and not on disk either. Search here stays:
            // there it searches Apple.
            bool apple = AppleMusic.Is(Active.CurrentPath);
            // The Apple Music row in the drives list is not a drive or a file
            // either: Copy, Cut, Rename, Delete, Properties and the rest on it
            // could only refuse.
            bool appleRow = apple || (IsDrivesView && FocusedPath() is { } row && AppleMusic.Is(row));
            bool outside = Active.Archive == null && !appleRow;
            foreach (var item in _archiveHidden)
                item.Visible = item == _searchHereItem ? Active.Archive == null : outside;
            foreach (var item in _appleHidden) item.Visible = !appleRow;
        }

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
        /// Our own context menu rather than the shell's, so the application's own
        /// commands live in it. The genuine Windows menu is still one item away.
        /// </summary>
        private void BuildContextMenu()
        {
            _contextMenu = new ContextMenuStrip();
            _contextMenu.Opened += (_, _) => { _menuOpen = true; foreach (var p in _panes) ApplyPendingName(p); };
            _contextMenu.Closed += (_, _) => { _menuOpen = false; _menuClosedAt = Environment.TickCount64; };
            _contextOpenItem = Item("&Open", Keys.None, ActivateFocused);
            _contextMenu.Items.Add(_contextOpenItem);

            // Apple Music songs only (Conner, 4.1.0). Favorite and Unfavorite are
            // one item whose words say what it will do, shown once Apple has
            // said which the song is; Add to playlist lists your own playlists.
            _playNextItem = Item("Play ne&xt", Keys.None, PlayFocusedAppleSongNext);
            _favoriteItem = Item("Fa&vorite", Keys.None, ToggleFocusedAppleFavorite);
            _addToPlaylistItem = new ToolStripMenuItem("Add to p&laylist");
            // A submenu needs an item from the start or it has no arrow (see BuildSortMenu).
            _addToPlaylistItem.DropDownItems.Add(new ToolStripMenuItem("(none)"));
            _playNextItem.Visible = _favoriteItem.Visible = _addToPlaylistItem.Visible = false;
            _contextMenu.Items.Add(_playNextItem);
            _contextMenu.Items.Add(_favoriteItem);
            _contextMenu.Items.Add(_addToPlaylistItem);

            // 4.1.6 (Conner): a song's album and artist, your library, and the
            // player's shuffle, repeat and Up next, from anywhere in Apple Music.
            // The letters are free here: what uses them elsewhere is hidden in Apple Music.
            _goToArtistItem = Item("Go to &artist", Keys.None, () => GoToFocusedApple(album: false));
            _goToAlbumItem = Item("Go to al&bum", Keys.None, () => GoToFocusedApple(album: true));
            _libraryItem = Item("Add to librar&y", Keys.None, ToggleFocusedAppleLibrary);
            _appleShuffleItem = Item("&Shuffle", Keys.None, () => Program.RunAudioAction(AudioAction.ToggleShuffle));
            _appleRepeatItem = Item("&Repeat", Keys.None, Program.CycleAppleRepeat);
            _appleUpNextItem = Item("&Up next…", Keys.None, () => Program.RunAudioAction(AudioAction.UpNext));
            foreach (var item in new[] { _goToArtistItem, _goToAlbumItem, _libraryItem, _appleShuffleItem, _appleRepeatItem, _appleUpNextItem })
            {
                item.Visible = false;
                _contextMenu.Items.Add(item);
            }
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(AppleHidden(Item("&Copy", Keys.None, () => CopyToClipboard(false))));
            _cutItem = ArchiveHidden(Item("Cu&t", Keys.None, () => CopyToClipboard(true)));
            _pasteItem = ArchiveHidden(Item("&Paste", Keys.None, PasteClipboard));
            _contextMenu.Items.Add(_cutItem);
            _contextMenu.Items.Add(_pasteItem);
            _contextMenu.Items.Add(AppleHidden(Item("Copy &path", Keys.None, CopyPath)));
            _contextMenu.Items.Add(DriveLinkItem("Copy Drive &link", Keys.None, makePublic: false));
            _contextMenu.Items.Add(DriveLinkItem("Copy p&ublic Drive link", Keys.None, makePublic: true));

            // A folder inside a two-way synced folder (Conner, 4.1.3): kept in
            // Google Drive alone, or brought back. Shown only where it applies.
            _keepOnDriveItem = Item("&Keep on Drive only", Keys.None, KeepFocusedOnDriveOnly);
            _putBackItem = Item("Put &back on PC", Keys.None, PutFocusedBackOnPc);
            _keepOnDriveItem.Visible = _putBackItem.Visible = false;
            _contextMenu.Items.Add(_keepOnDriveItem);
            _contextMenu.Items.Add(_putBackItem);
            _contextMenu.Items.Add(new ToolStripSeparator());

            // Where Explorer puts it, between the clipboard and the two commands
            // that change the row itself — which is also where a hand that has
            // used Windows expects to find it.
            _shortcutItem = Item("Create &shortcut", Keys.None, CreateShortcut);
            _contextMenu.Items.Add(_shortcutItem);

            _renameItem = ArchiveHidden(Item("&Rename", Keys.None, RenameSelected));
            _deleteItem = ArchiveHidden(Item("&Delete", Keys.None, () => DeleteSelected(false)));
            _contextMenu.Items.Add(_renameItem);
            _contextMenu.Items.Add(_deleteItem);

            // Google Drive's trash (3.0.16): shown only there, in place of the
            // commands that could only refuse in it.
            _restoreItem = Item("Re&store", Keys.None, RestoreFromDriveTrash);
            _deleteForeverItem = Item("Delete perma&nently…", Keys.None, DeleteFromDriveTrash);
            _emptyTrashItem = Item("Empty Google Drive &trash…", Keys.None, EmptyDriveTrash);
            _contextMenu.Items.Add(_restoreItem);
            _contextMenu.Items.Add(_deleteForeverItem);
            _contextMenu.Items.Add(_emptyTrashItem);
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
            _compressItem = ArchiveHidden(Item("&Compress…", Keys.None, CompressSelection));
            _contextMenu.Items.Add(_compressItem);

            _extractItem = Item("&Extract", Keys.None, () => ExtractFocused(intoNewFolder: true));
            _extractHereItem = Item("Extract h&ere", Keys.None, () => ExtractFocused(intoNewFolder: false));

            _contextMenu.Items.Add(_extractItem);
            _contextMenu.Items.Add(_extractHereItem);

            // Audio, on the same terms as Extract: shown only when the row is
            // something they can work on, decided from the name alone. Convert
            // takes audio files and folders (the audio inside them); Edit takes
            // one audio file.
            _convertAudioItem = Item("Con&vert audio…", Keys.None, ConvertAudio);
            _editAudioItem = Item("Edit &audio…", Keys.None, EditAudio);
            _contextMenu.Items.Add(_convertAudioItem);
            _contextMenu.Items.Add(_editAudioItem);

            _contextMenu.Items.Add(new ToolStripSeparator());

            // Beside Properties, and only over an audio file it could tag: on
            // the Drive letter or over an Ogg file it could only refuse.
            _editTagsItem = Item("Edit ta&gs…", Keys.None, EditTagsForSelection);
            _contextMenu.Items.Add(_editTagsItem);

            // Windows' own Format window, on a drive row only (Conner, 3.0.14).
            _formatItem = Item("&Format…", Keys.None, FormatFocusedDrive);
            _contextMenu.Items.Add(_formatItem);
            _contextMenu.Items.Add(ArchiveHidden(Item("P&roperties", Keys.None, ShowProperties)));
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(ArchiveHidden(Item("&More Windows options…", Keys.None, ShowShellMenuForSelection)));
        }

        private ToolStripMenuItem ArchiveHidden(ToolStripMenuItem item)
        {
            _archiveHidden.Add(item);
            return item;
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
            UpdateTitle();

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

            // Must match the column order set in ApplySettingsToUi. Built from an
            // array in one go: the item sizes its sub-item list once instead of
            // growing it a cell at a time, which with the list the cells used to
            // be gathered in was 40% of the cost of a row.
            var cells = new[]
            {
                DisplayNameOf(entry),
                entry.SizeOverride
                    ?? (entry.IsDir ? "" : SizeFormatter.Format(entry.Size, _settings.SizeUnits)),
                entry.Modified == default
                    ? ""
                    : TimeFormatter.Format(entry.Modified, _settings.VerboseModifiedInfo),
            };

            var item = new ListViewItem(cells) { Tag = entry.Path };

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
                bool extensions = _settings.ShowExtensions;
                int found = TypeAhead.FindFolded(entries.Count, i => entries[i].FoldedDisplayName(extensions), pending, -1);
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

                // The tab you are in leads the title.
                UpdateTitle();
            }
            finally { ResumeLayout(performLayout: false); }

            Active.List.Focus();

            // Deferred, both of them. Totalling the selection and building the
            // announcement are wasted on a tab you are passing through on the way
            // to somewhere else; only the one you stop on is worth the work.
            ScheduleStatusUpdate();
            ScheduleTabAnnouncement();

            // A tab that was not watched while it was behind (the window coming
            // back from the tray watches only the front one) is watched now.
            // Marked after arming, because arming clears the marks — and a folder
            // nobody watched may have changed in any way at all.
            if (Visible && NeedsWatcher(Active))
            {
                ArmWatcher(Active, Active.CurrentPath);
                Active.NeedsRefresh = true;
                Active.ReloadQuietly = true;
            }

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
            if (!_settings.SpeakEnabled) return;

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
            var name = Active.SearchTerm is { } term ? $"Results for {term}"
                : IsDrivesView ? "Drives"
                : FolderDisplayName(Active.CurrentPath);

            // Routed rather than spoken straight at NVDA, so "Switched tab" in
            // Choose what is spoken is the one switch for it.
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

        /// <returns>
        /// True when this navigation landed on <paramref name="path"/> and is still
        /// the pane's latest; false when it fell back, failed or gave way.
        /// </returns>
        private async Task<bool> NavigateAsync(string path, string? preferPath = null,
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

            // Apple Music: folders listed by Apple, nothing on disk to probe or watch.
            if (AppleMusic.Is(path))
            {
                try
                {
                    return await ShowAppleMusicFolderAsync(pane, path, preferPath, cameFrom, mine, quiet, keepFocus);
                }
                catch (Exception ex)
                {
                    Announce($"Could not open {FolderDisplayName(path)}: {ex.Message}", isError: true);
                    return false;
                }
            }

            // Inside the archive the pane is already in: a folder of a listing
            // held in memory, so nothing is probed, watched or read.
            if (path != DrivesPath && pane.Archive is { } archive &&
                ArchiveBrowser.InnerPathOf(archive.ArchivePath, path) is { } inner)
            {
                try
                {
                    return await ShowArchiveFolderAsync(pane, archive, inner, preferPath, cameFrom, mine, quiet, keepFocus);
                }
                catch (Exception ex)
                {
                    Announce($"Could not open folder: {ex.Message}", isError: true);
                    return false;
                }
            }

            if (path == DrivesPath)
            {
                var driveToken = BeginPaneWork(pane);
                pane.Searching = false;
                pane.Archive = null;

                pane.CurrentPath = DrivesPath;
                if (pane == Active) _pathLabel.Text = "Drives";
                UpdateTitle();

                // Nothing to watch: the drive list is not a folder.
                DisarmWatcher(pane);

                // Coming up out of a drive lands on that drive, not on A:; and a
                // reload of the list itself stays on the drive it was on.
                LoadDrivesInto(pane, driveToken,
                    preferPath ?? (cameFrom == DrivesPath ? FocusedPathOf(pane) : SafePathRoot(cameFrom)), quiet);

                return pane.NavigationId == mine;
            }

            try
            {
                // Without a trailing separator, except at a root: "C:\Users\" and
                // "C:\Users" were two different folders to every comparison here.
                // GetFullPath strips a trailing dot, so "Backup." would open "Backup"
                // beside it: a path with such a part is kept as given.
                var full = Path.TrimEndingDirectorySeparator(
                    NameRules.ExactPath(path) != path && Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path));

                pane.Probing = mine;
                var reachable = await ProbeDirectoryAsync(NameRules.ExactPath(full), NavigationProbeMs);
                if (IsDisposed || pane.NavigationId != mine) return false;

                if (reachable != FolderProbe.Present)
                {
                    // The folder may simply be gone — an unplugged drive, a
                    // dropped share. Fall back to the nearest level that is
                    // still there rather than leaving a dead view on screen.
                    var fallback = await NearestExistingAncestorAsync(full);
                    if (IsDisposed || pane.NavigationId != mine) return false;

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
                    return false;
                }

                var token = BeginPaneWork(pane);

                // Replacing the pane's work cancels a search that was running in
                // it, and that search's own clean-up no longer owns the flag.
                pane.Searching = false;

                // Out of any archive: this is a real folder. Kept to put back if
                // the folder will not open and the archive stays on screen.
                var leftArchive = pane.Archive;
                pane.Archive = null;

                pane.CurrentPath = full;
                if (pane == Active) _pathLabel.Text = full;
                UpdateTitle();

                // Watched from before the listing starts, so a file that arrives
                // while a big folder is still being read is not missed; armed
                // after the listing, the changes made during it were simply
                // lost. A folder that turns out unreadable is let go below.
                ArmWatcher(pane, full);

                // The listing about to start sees everything that happened
                // before it, so only those marks are forgotten. Whatever the
                // watcher collects from here on stays, and is acted on once the
                // listing is in. (Forgetting them after the listing, as this
                // did, threw away exactly the changes it could have missed.)
                pane.NeedsRefresh = false;
                pane.ReloadQuietly = false;
                pane.Debounce.Clear();

                bool arrived = await LoadEntriesAsync(
                    pane, full, token, preferPath ?? PreferredRowFor(pane, full, cameFrom), quiet, keepFocus);
                if (token.IsCancellationRequested || IsDisposed) return false;

                // On the Drive letter the listing writes the placeholders itself,
                // so what the watcher saw during it was our own doing.
                if (arrived && pane.CurrentPath == full && Drive?.Owns(full) == true)
                {
                    pane.NeedsRefresh = false;
                    pane.ReloadQuietly = false;
                    pane.Debounce.Clear();
                }

                // A folder we could not read leaves the previous one on screen, so
                // that is where we still are. Without putting the path back, the
                // window claimed to be somewhere it had never managed to open:
                // the list showed the old folder, but Backspace went up from the
                // new one and F5 tried to reload the folder that had just been
                // refused.
                if (!arrived)
                {
                    // On the very first navigation there is nowhere to go back to,
                    // and leaving the path empty would make Directory.GetParent
                    // throw the next time Backspace was pressed. The drive list is
                    // always somewhere valid to be.
                    var restore = string.IsNullOrEmpty(cameFrom) ? DrivesPath : cameFrom;
                    pane.CurrentPath = restore;

                    // Still inside the archive that is still on screen.
                    if (leftArchive != null && ArchiveBrowser.InnerPathOf(leftArchive.ArchivePath, restore) != null)
                        pane.Archive = leftArchive;

                    // Still showing the results, so still in them: Escape, Enter
                    // and the watcher all go by the term.
                    if (wasSearching)
                    {
                        pane.SearchTerm = searchedFor;
                        pane.SearchTruncated = searchTruncated;
                    }

                    if (pane == Active) _pathLabel.Text = PathLabelFor(pane);

                    UpdateTitle();

                    // The watcher went over to the folder that would not open;
                    // it goes back to the one still on screen. That folder went
                    // unwatched meanwhile, and its pending marks were cleared, so
                    // it is read again quietly.
                    ArmWatcher(pane, restore);
                    if (!string.IsNullOrEmpty(restore) && restore != DrivesPath)
                    {
                        pane.NeedsRefresh = true;
                        pane.ReloadQuietly = true;
                        if (pane == Active) ScheduleWatchCheck();
                    }

                    // Nothing was on screen to stay on: the drive list is put
                    // there, on the drive that would not open. Only the path was
                    // set before, so a startup folder that refused to be read
                    // left a tab called Drives with nothing in it.
                    if (string.IsNullOrEmpty(cameFrom) && pane.NavigationId == mine)
                        await NavigateAsync(DrivesPath, preferPath: SafePathRoot(full), onPane: pane, quiet: quiet);
                    return false;
                }

                return pane.NavigationId == mine &&
                       string.Equals(pane.CurrentPath, full, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Announce($"Could not open folder: {ex.Message}", isError: true);
                return false;
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

            // Exact: a folder called "Backup." asked about plainly is "Backup",
            // so the idle poll called it gone and moved the pane out of it.
            var probe = Task.Run(() =>
            {
                try { return Directory.Exists(NameRules.ExactPath(path)); }
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
            // By text, not Directory.GetParent: that strips a trailing dot from
            // every part, so under "Backup." the walk asked about "Backup".
            try
            {
                var current = SafeParent(path);
                while (!string.IsNullOrEmpty(current))
                {
                    if (await DirectoryExistsAsync(current)) return current;
                    current = SafeParent(current);
                }
            }
            catch { }
            return DrivesPath;
        }

        // ---------- Watching the folder ----------

        /// <summary>
        /// Folders a transfer (or a delete) is changing right now, and how many
        /// are changing each.
        ///
        /// Our own commands refresh when they finish, and they know better than
        /// the watcher when that is: a paste of ten thousand files would
        /// otherwise fire events all the way through and have us rebuilding the
        /// list on top of the copy that is still running.
        ///
        /// Per folder, never the whole window. A window-wide counter was
        /// harmless while a transfer held the window modal; now that a copy runs
        /// for an hour while you carry on working, it would mean an hour of no
        /// folder anywhere noticing that anything had changed — a file arriving
        /// from another program, a download finishing, a rename done in
        /// Explorer, none of it appearing until something forced a reload.
        ///
        /// Keyed by folder because that is the folder the ten thousand events
        /// are coming from. Counted rather than a set, because two transfers can
        /// perfectly well be filling the same folder and the first one finishing
        /// is not the end of the noise.
        ///
        /// Raised and released around a try, always: a raise left standing is a
        /// folder that never refreshes
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

        /// <summary>
        /// One-shot, and only running while something is waiting: see
        /// <see cref="WatchDebounce"/>. It was a 600ms poll that never stopped,
        /// hidden in the tray included.
        /// </summary>
        private System.Windows.Forms.Timer? _watchTimer;

        /// <summary>
        /// How soon to look again when a reload is waiting on a listing or a
        /// search already under way in the pane.
        /// </summary>
        private const int WatchRetryMs = 1000;

        /// <summary>
        /// Drive roots whose watchers were let go because Windows asked whether
        /// the drive could be removed, and when. Nothing watches them again
        /// until the removal fails, completes, or long enough has passed that
        /// the drive is plainly staying.
        /// </summary>
        private readonly Dictionary<string, long> _releasedRoots = new(StringComparer.OrdinalIgnoreCase);
        private const int ReleasedRootMs = 30_000;

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
            // Nor inside an archive, whose folders are not on disk to watch.
            // Nor in Apple Music, which is not on disk at all.
            if (string.IsNullOrEmpty(path) || path == DrivesPath || pane.Archive != null || AppleMusic.Is(path)) { DisarmWatcher(pane); return; }

            // A refresh re-navigates to the same folder, so this runs every time
            // the list is rebuilt. Tearing the watcher down and building another
            // one for the same path would mean a new directory handle on every
            // refresh, and a window between them where changes are missed.
            // What is pending is the navigation's business: it forgets only what
            // its listing will have seen.
            if ((pane.Watcher != null &&
                 string.Equals(NameRules.PlainPath(pane.Watcher.Path), path, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(pane.ArmingPath, path, StringComparison.OrdinalIgnoreCase))
                return;

            DisarmWatcher(pane);

            // Hidden in the tray, nothing is watched: a watcher holds a handle on
            // the folder, which is what stops "Safely remove" ejecting a USB
            // drive, and nobody is looking. Showing the window arms it again and
            // reads the folder (OnVisibleChanged).
            if (!Visible) return;

            // Windows asked whether this drive can be removed and was told yes.
            var root = SafePathRoot(path);
            if (root != null && _releasedRoots.TryGetValue(root, out var releasedAt))
            {
                if (Environment.TickCount64 - releasedAt < ReleasedRootMs) return;
                _releasedRoots.Remove(root);
            }

            // Built on a worker: the constructor checks the folder exists, turning
            // it on opens a handle to it, and the removal registration opens
            // another — each a wait of seconds on a sleeping share, and showing
            // the window from the tray did all of it for both tabs on the UI
            // thread. Installed only if the pane is still on that folder.
            int generation = pane.WatchGeneration;
            bool removable = Drive?.Owns(path) != true;
            var window = Handle;
            pane.ArmingPath = path;
            _ = InstallWatcherAsync(pane, path, generation, removable, window);
        }

        private async Task InstallWatcherAsync(Pane pane, string path, int generation, bool removable, IntPtr window)
        {
            (FileSystemWatcher? Watcher, RemovalWatch? Removal) made = (null, null);
            try
            {
                made = await Task.Run(() => BuildWatcher(pane, path, generation, removable, window));
            }
            catch { }

            // Back on the UI thread. Anything that let the pane's watcher go
            // meanwhile — another folder, the window hidden, a drive being
            // removed — bumped the generation, and this one is no longer wanted.
            bool wanted = !IsDisposed && Visible && pane.WatchGeneration == generation &&
                          string.Equals(pane.CurrentPath, path, StringComparison.OrdinalIgnoreCase);
            if (pane.WatchGeneration == generation) pane.ArmingPath = null;
            if (!wanted || made.Watcher == null)
            {
                DisposeWatch(made.Watcher, made.Removal);
                return;
            }

            pane.Watcher = made.Watcher;
            pane.Removal = made.Removal;
        }

        /// <summary>On a worker: the watcher and the removal registration for one folder, or nulls.</summary>
        private (FileSystemWatcher?, RemovalWatch?) BuildWatcher(
            Pane pane, string path, int generation, bool removable, IntPtr window)
        {
            FileSystemWatcher? watcher = null;
            try
            {
                // Exact, so "Backup." is watched and not its sibling "Backup";
                // the paths it reports are made plain again below.
                watcher = new FileSystemWatcher(NameRules.ExactPath(path))
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                    IncludeSubdirectories = false,
                    // Room for a burst. When the buffer overflows the change list
                    // is lost and only an Error arrives, which costs a full reread.
                    InternalBufferSize = 64 * 1024,
                };

                // A watcher that was let go before it was installed says nothing:
                // its events would land on whatever the pane shows now.
                bool Current() => Volatile.Read(ref pane.WatchGeneration) == generation;

                // A file the list already shows, written to: its row is updated
                // in place. Everything else re-reads the folder once the burst
                // is over. Only the first event of a burst wakes the window.
                void Reload(object? _, EventArgs __)
                {
                    if (Current() && pane.Debounce.Reload(Environment.TickCount64)) WakeWatch();
                }

                watcher.Created += Reload;
                watcher.Deleted += Reload;
                watcher.Renamed += Reload;
                watcher.Changed += (_, e) =>
                {
                    if (Current() && pane.Debounce.Changed(NameRules.PlainPath(e.FullPath), Environment.TickCount64)) WakeWatch();
                };

                // An error is usually a lost burst or a share that went away.
                // Both mean the list can no longer be trusted, so re-read it.
                watcher.Error += Reload;

                watcher.EnableRaisingEvents = true;
                return (watcher, removable ? RemovalWatch.TryRegister(window, path) : null);
            }
            catch
            {
                // Some filesystems will not support watching. That is a reason to
                // do without live updates, not a reason to fail to show a folder.
                DisposeWatch(watcher, null);
                return (null, null);
            }
        }

        private static void DisposeWatch(FileSystemWatcher? watcher, RemovalWatch? removal)
        {
            removal?.Dispose();
            if (watcher == null) return;
            try { watcher.EnableRaisingEvents = false; } catch { }
            try { watcher.Dispose(); } catch { }
        }

        private static void DisarmWatcher(Pane pane)
        {
            // A watcher still being built for this pane is not wanted any more.
            pane.WatchGeneration++;
            pane.ArmingPath = null;

            var watcher = pane.Watcher;
            pane.Watcher = null;
            pane.NeedsRefresh = false;
            pane.ReloadQuietly = false;
            pane.Debounce.Clear();

            pane.Removal?.Dispose();
            pane.Removal = null;

            if (watcher == null) return;
            try { watcher.EnableRaisingEvents = false; } catch { }
            try { watcher.Dispose(); } catch { }
        }

        /// <summary>From a watcher's thread: the first change of a burst has arrived.</summary>
        private void WakeWatch()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(ScheduleWatchCheck); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
        }

        /// <summary>
        /// Sets the one-shot timer for whenever the front pane's wait is over,
        /// or stops it when nothing is waiting.
        /// </summary>
        private void ScheduleWatchCheck() => ScheduleWatchCheck(retry: false);

        private void ScheduleWatchCheck(bool retry)
        {
            if (IsDisposed) return;

            int due = Active.Debounce.DueIn(Environment.TickCount64);

            // Marked for a reload with no burst in progress — a tab coming back,
            // the window being shown or brought to the front: at once.
            if (due < 0 && Active.NeedsRefresh) due = 50;
            if (retry) due = Math.Max(due, WatchRetryMs);

            if (!Visible || due < 0)
            {
                _watchTimer?.Stop();
                return;
            }

            if (_watchTimer == null)
            {
                _watchTimer = new System.Windows.Forms.Timer();
                _watchTimer.Tick += (_, _) =>
                {
                    _watchTimer?.Stop();
                    // Woken early by a burst that is still going: wait out the rest.
                    int left = Active.Debounce.DueIn(Environment.TickCount64);
                    if (left > 0) { ScheduleWatchCheck(); return; }
                    ApplyPendingRefresh();
                };
            }

            _watchTimer.Stop();
            _watchTimer.Interval = Math.Max(1, due);
            _watchTimer.Start();
        }

        /// <summary>
        /// Puts new sizes and dates on the rows of files that were written to,
        /// without re-reading the folder: the cursor, the selection and the
        /// order stay exactly as they are and nothing is read aloud. Anything it
        /// cannot do that way — a file it has no row for, one that has gone or
        /// is now hidden — becomes an ordinary reload.
        /// </summary>
        private async void UpdateRowsInPlace(Pane pane, List<string> paths)
        {
            var folder = pane.CurrentPath;
            bool showHidden = _settings.ShowHiddenFiles;
            bool showSystem = _settings.ShowSystemFiles;

            // Off the UI thread: the folder may be on a share.
            List<(string Path, bool Keep, long Size, DateTime Modified)> facts;
            try
            {
                facts = await Task.Run(() =>
                {
                    var found = new List<(string, bool, long, DateTime)>(paths.Count);
                    foreach (var path in paths)
                    {
                        try
                        {
                            // Exact, so "report." is asked about itself and not
                            // its sibling "report".
                            var info = new FileInfo(NameRules.ExactPath(path));
                            var attrs = info.Attributes;
                            bool exists = (int)attrs != -1;
                            bool shown = exists &&
                                         (showHidden || (attrs & FileAttributes.Hidden) == 0) &&
                                         (showSystem || (attrs & FileAttributes.System) == 0);
                            bool dir = exists && (attrs & FileAttributes.Directory) != 0;
                            found.Add((path, shown, dir || !shown ? -1 : info.Length,
                                shown ? info.LastWriteTime : default));
                        }
                        catch { found.Add((path, false, -1, default)); }
                    }
                    return found;
                });
            }
            catch { return; }

            if (IsDisposed) return;

            // The pane has moved on, or is reading its folder afresh: these
            // facts are about a list that is no longer on screen. Marking a
            // reload here marked the *new* folder, for a reload that was not the
            // quiet kind, and announced it as changed when nothing had.
            if (!string.Equals(pane.CurrentPath, folder, StringComparison.OrdinalIgnoreCase) || pane.Loading)
                return;

            bool reload = false;
            {
                foreach (var (path, keep, size, modified) in facts)
                {
                    if (!keep || !pane.IndexByPath.TryGetValue(path, out var i) || i >= pane.Entries.Count)
                    {
                        reload = true;
                        continue;
                    }

                    var old = pane.Entries[i];
                    var now = old with { Size = old.IsDir ? old.Size : size, Modified = modified };
                    if (now.Size == old.Size && now.Modified == old.Modified) continue;

                    pane.Entries[i] = now;
                    EvictItem(pane, i);
                    // Outside the try above, in an async void: a redraw racing a
                    // list that shrank would otherwise take the window with it.
                    try { if (pane.List.IsHandleCreated) pane.List.RedrawItems(i, i, true); } catch { }
                }
                if (pane == Active) ScheduleStatusUpdate();
            }

            if (reload)
            {
                pane.NeedsRefresh = true;
                pane.Debounce.Reload(Environment.TickCount64);
                if (pane == Active) ScheduleWatchCheck();
            }
        }

        /// <summary>
        /// A request to be told before a drive is removed, for the folder a
        /// watcher holds open on it.
        ///
        /// "Safely remove" asks every open handle on the drive whether it may
        /// go, and only applications that registered a handle are asked; the
        /// rest simply make it fail with "this device is currently in use". The
        /// folder watcher's directory handle was one of those, so a USB stick
        /// whose folder was on screen — or merely left in a tab — could not be
        /// ejected. This opens a handle of its own on the same folder, purely to
        /// register it; WM_DEVICECHANGE then says DBT_DEVICEQUERYREMOVE, and the
        /// window lets go of both.
        /// </summary>
        private sealed class RemovalWatch : IDisposable
        {
            public IntPtr Notify { get; private set; }
            public string Root { get; }
            private Microsoft.Win32.SafeHandles.SafeFileHandle? _handle;

            private RemovalWatch(IntPtr notify, string root, Microsoft.Win32.SafeHandles.SafeFileHandle handle)
            {
                Notify = notify;
                Root = root;
                _handle = handle;
            }

            /// <summary>Null for a share, the Drive letter or anything that will not register.</summary>
            public static RemovalWatch? TryRegister(IntPtr window, string path)
            {
                try
                {
                    var root = Path.GetPathRoot(path);
                    if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
                    var type = new DriveInfo(root).DriveType;
                    if (type is not (DriveType.Removable or DriveType.Fixed or DriveType.CDRom)) return null;

                    const uint FILE_READ_ATTRIBUTES = 0x80, SHARE_ALL = 7, OPEN_EXISTING = 3,
                               FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
                    var handle = CreateFileW(NameRules.ExactPath(path), FILE_READ_ATTRIBUTES, SHARE_ALL, IntPtr.Zero,
                        OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
                    if (handle.IsInvalid) { handle.Dispose(); return null; }

                    var filter = new DEV_BROADCAST_HANDLE
                    {
                        dbch_size = System.Runtime.InteropServices.Marshal.SizeOf<DEV_BROADCAST_HANDLE>(),
                        dbch_devicetype = DBT_DEVTYP_HANDLE,
                        dbch_handle = handle.DangerousGetHandle(),
                    };
                    var notify = RegisterDeviceNotificationW(window, ref filter, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
                    if (notify == IntPtr.Zero) { handle.Dispose(); return null; }
                    return new RemovalWatch(notify, root, handle);
                }
                catch { return null; }
            }

            /// <summary>
            /// The drive is about to go: close the handle, and keep the
            /// registration, which is how a failed or completed removal is heard.
            /// </summary>
            public void Release()
            {
                _handle?.Dispose();
                _handle = null;
            }

            public void Dispose()
            {
                Release();
                if (Notify != IntPtr.Zero) UnregisterDeviceNotification(Notify);
                Notify = IntPtr.Zero;
            }
        }

        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEQUERYREMOVE = 0x8001;
        private const int DBT_DEVICEQUERYREMOVEFAILED = 0x8002;
        private const int DBT_DEVICEREMOVEPENDING = 0x8003;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const int DBT_DEVTYP_VOLUME = 2;
        private const int DBT_DEVTYP_HANDLE = 6;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct DEV_BROADCAST_HANDLE
        {
            public int dbch_size;
            public int dbch_devicetype;
            public int dbch_reserved;
            public IntPtr dbch_handle;
            public IntPtr dbch_hdevnotify;
            public Guid dbch_eventguid;
            public int dbch_nameoffset;
            public byte dbch_data;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct DEV_BROADCAST_VOLUME
        {
            public int dbcv_size;
            public int dbcv_devicetype;
            public int dbcv_reserved;
            public int dbcv_unitmask;
            public short dbcv_flags;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterDeviceNotificationW(IntPtr recipient, ref DEV_BROADCAST_HANDLE filter, int flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnregisterDeviceNotification(IntPtr handle);

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE && m.LParam != IntPtr.Zero)
            {
                try { OnDeviceChange((int)(long)m.WParam, m.LParam); }
                catch { }
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// Lets go of a drive Windows is about to remove, and picks it up again
        /// if the removal does not happen.
        /// </summary>
        private void OnDeviceChange(int what, IntPtr data)
        {
            int type = System.Runtime.InteropServices.Marshal.ReadInt32(data, 4);

            if (type == DBT_DEVTYP_HANDLE)
            {
                var header = System.Runtime.InteropServices.Marshal.PtrToStructure<DEV_BROADCAST_HANDLE>(data);
                foreach (var pane in _panes)
                {
                    if (pane.Removal is not { } removal || removal.Notify != header.dbch_hdevnotify) continue;

                    switch (what)
                    {
                        case DBT_DEVICEQUERYREMOVE:
                            // Yes, it may go: the watcher and our handle close now,
                            // and nothing re-arms on this drive meanwhile.
                            _releasedRoots[removal.Root] = Environment.TickCount64;
                            pane.WatchGeneration++;
                            pane.ArmingPath = null;
                            var watcher = pane.Watcher;
                            pane.Watcher = null;
                            if (watcher != null)
                            {
                                try { watcher.EnableRaisingEvents = false; } catch { }
                                try { watcher.Dispose(); } catch { }
                            }
                            pane.Debounce.Clear();
                            removal.Release();
                            break;

                        case DBT_DEVICEQUERYREMOVEFAILED:
                            // Something else held on, so the drive stays: watch it again.
                            removal.Dispose();
                            pane.Removal = null;
                            _releasedRoots.Remove(removal.Root);
                            if (IsUnder(pane.CurrentPath, removal.Root))
                            {
                                ArmWatcher(pane, pane.CurrentPath);
                                pane.NeedsRefresh = true;
                                pane.ReloadQuietly = true;
                                if (pane == Active) ScheduleWatchCheck(retry: true);
                            }
                            break;

                        case DBT_DEVICEREMOVEPENDING:
                        case DBT_DEVICEREMOVECOMPLETE:
                            removal.Dispose();
                            pane.Removal = null;
                            break;
                    }
                }
            }
            else if (type == DBT_DEVTYP_VOLUME && (what == DBT_DEVICEREMOVECOMPLETE || what == DBT_DEVICEARRIVAL))
            {
                // Broadcast to every window. A drive that went, or one that came
                // (back), is no longer one we are holding off.
                int mask = System.Runtime.InteropServices.Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(data).dbcv_unitmask;
                for (int bit = 0; bit < 26; bit++)
                    if ((mask & (1 << bit)) != 0) _releasedRoots.Remove($"{(char)('A' + bit)}:\\");
            }
        }

        /// <summary>
        /// Hidden in the tray, the window does nothing: the availability poll
        /// (a Directory.Exists on a share every four seconds), the watch timer
        /// and both folder watchers all stop. Shown again, each folder is
        /// watched again and the one in front is read quietly, for whatever
        /// changed while nobody was looking.
        /// </summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (IsDisposed || _panes[0] == null) return;

            if (!Visible)
            {
                _availabilityTimer?.Stop();
                _watchTimer?.Stop();
                foreach (var pane in _panes) DisarmWatcher(pane);
                return;
            }

            // Only the tab in front is watched again now; the other is marked,
            // and watched and read when it is switched to (SetActiveTab). Arming
            // both here opened two folders at once, on a share each a wait.
            foreach (var pane in _panes)
            {
                if (!NeedsWatcher(pane)) continue;
                if (pane == Active) ArmWatcher(pane, pane.CurrentPath);
                pane.NeedsRefresh = true;
                pane.ReloadQuietly = true;
            }

            if (WindowState != FormWindowState.Minimized) _availabilityTimer?.Start();
            CheckCurrentPathStillExists();
            ScheduleWatchCheck();
        }

        /// <summary>
        /// A pane showing a folder it has read, with no watcher on it or on the way.
        /// Not one whose watcher was let go for an eject still pending: re-arming
        /// it would drop the removal notification that puts it back.
        /// </summary>
        private static bool NeedsWatcher(Pane pane) =>
            pane.Removal == null && pane.Archive == null && !AppleMusic.Is(pane.CurrentPath) &&
            !string.IsNullOrEmpty(pane.CurrentPath) && pane.CurrentPath != DrivesPath &&
            pane.Watcher == null && pane.ArmingPath == null && !string.IsNullOrEmpty(pane.LastLoadedPath);

        /// <summary>
        /// Minimised, the availability poll stops — a probe of a share every four
        /// seconds for a window nobody is looking at — and starts again on restore.
        /// </summary>
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_availabilityTimer == null || IsDisposed) return;

            if (WindowState == FormWindowState.Minimized) _availabilityTimer.Stop();
            else if (Visible && !_availabilityTimer.Enabled)
            {
                _availabilityTimer.Start();
                CheckCurrentPathStillExists();
            }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            // A change that waited because another window was in front.
            if (_panes[_activeIndex] is { } pane && (pane.NeedsRefresh || pane.Debounce.Pending))
                ScheduleWatchCheck();

            // Back to the quick poll, and catch up on what it missed.
            if (_availabilityTimer is { Enabled: true, Interval: not 4000 })
            {
                _availabilityTimer.Interval = 4000;
                CheckCurrentPathStillExists();
            }
        }

        /// <summary>
        /// Behind another window the drive poll slows to every fifteen seconds:
        /// nobody is looking, and a laptop on battery pays for every wakeup.
        /// </summary>
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (_availabilityTimer != null && !IsDisposed) _availabilityTimer.Interval = 15000;
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
            if (IsDisposed || !Visible) return;

            var pane = Active;

            // While the folder is being read, or a navigation is still finding
            // out whether it is there, what the watcher collected stays where it
            // is. Taken now, a file written during the listing was an in-place
            // update thrown away because the pane was loading; left alone, it is
            // looked at again once the listing is in (the navigation forgets only
            // what happened before its listing began).
            if (pane.Loading || pane.Probing != 0)
            {
                if (pane.NeedsRefresh || pane.Debounce.Pending) ScheduleWatchCheck(retry: true);
                return;
            }

            // What the watcher collected. Rows of files written to are updated
            // where they stand — unless the list is ordered by size or date,
            // where a new size or date can move a row, which is a reload.
            var (reload, changed) = pane.Debounce.Take();
            if (reload) pane.NeedsRefresh = true;
            if (changed.Count > 0)
            {
                bool byName = NameRules.SortsAs(SortFor(pane.CurrentPath).By) == SortColumn.Name;
                // Not on the Drive letter, where the rows come from Google's
                // listing and reading a placeholder's attributes is the
                // provider's business.
                bool onDrive = Drive is { } drive && drive.Owns(pane.CurrentPath);
                if (byName && !onDrive) UpdateRowsInPlace(pane, changed);
                else pane.NeedsRefresh = true;
            }

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
            // Nor while the folder is still arriving, or a navigation is still
            // finding out whether its folder is there; the mark stays for after,
            // and is looked at again in a second.
            if (pane.Searching || pane.Loading || pane.Probing != 0)
            {
                ScheduleWatchCheck(retry: true);
                return;
            }

            // Nor behind somebody's back. With the window not in front the mark
            // stays, and OnActivated looks at it again.
            if (Form.ActiveForm != this) return;

            pane.NeedsRefresh = false;
            bool ours = pane.ReloadQuietly;
            pane.ReloadQuietly = false;

            // Read before the reload: the pane can be switched while it runs, and
            // the folder that changed is the one being named.
            var folder = DescribePath(pane.CurrentPath);

            // Keeps the cursor where it is, so a file appearing elsewhere in the
            // folder does not move you.
            bool landed = await RefreshAsync(folder: pane.CurrentPath, quiet: true);
            if (IsDisposed) return;

            // Said after the reload rather than before it, so the list already
            // agrees with the announcement — and so it is not immediately
            // overwritten in the status bar by the reload's own item count.
            // Only when the reload landed on the folder: one that fell back, or
            // gave way to somebody's navigation, has said something truer, and
            // "changed" spoken over it cut that off.
            if (landed && !ours) AnnounceOperation("nav.changed", $"{folder} changed");
        }

        private static string? SafePathRoot(string? path)
        {
            if (string.IsNullOrEmpty(path) || path == DrivesPath) return null;
            // Coming up out of Apple Music lands back on its row, not on a drive.
            if (AppleMusic.Is(path)) return AppleMusic.Root;
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

            // The pane and its navigation, fixed now. A navigation somebody
            // started meanwhile — still checking its own folder, or reading it —
            // wins: falling back on top of it cancelled where they were going.
            var pane = Active;
            var path = pane.CurrentPath;
            if (string.IsNullOrEmpty(path)) return;

            // A path through an archive is not a folder on disk, and asked about
            // as one it would answer "gone" and take the pane out of it.
            if (pane.Archive != null) return;

            // Apple Music paths are not folders on disk either; probed as one the
            // answer is always "gone", which pulled the pane back to the drives a
            // few seconds after opening any Apple Music list.
            if (AppleMusic.Is(path)) return;
            int navigation = pane.NavigationId;
            bool Overtaken() => IsDisposed || pane.NavigationId != navigation || pane.CurrentPath != path ||
                                pane.Probing != 0 || pane.Loading || pane.Searching;

            _availabilityCheckRunning = true;
            try
            {
                if (await DirectoryExistsAsync(path))
                {
                    _consecutiveProbeFailures = 0;
                    return;
                }
                if (Overtaken()) return;

                if (++_consecutiveProbeFailures < FailedProbesBeforeGivingUp) return;
                _consecutiveProbeFailures = 0;

                var fallback = await NearestExistingAncestorAsync(path);
                if (Overtaken()) return;

                Announce($"{FolderDisplayName(path)} is no longer available, going to {DescribePath(fallback)}", isError: true);
                await NavigateAsync(fallback, onPane: pane);
            }
            catch { }   // async void off a timer: nothing may escape
            finally { _availabilityCheckRunning = false; }
        }

        private static string FolderDisplayName(string path)
        {
            if (path == DrivesPath) return "Drives";
            if (string.Equals(path, AppleMusic.Root, StringComparison.OrdinalIgnoreCase)) return AppleMusic.RootName;
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? path : name;
        }

        private void LoadDrivesInto(Pane pane, CancellationToken token, string? preferPath = null, bool quiet = false)
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (Exception ex) { Announce($"Could not list drives: {ex.Message}", isError: true); return; }

            // A quiet re-read of the list already on screen (Preferences saved)
            // that keeps the cursor on its drive says nothing, as a folder's does.
            var previousRow = pane.LastLoadedPath == DrivesPath ? FocusedPathOf(pane) : null;

            // Letters appear at once; the slow per-drive queries follow.
            var entries = drives
                .Select(d => new Entry(d.Name, d.Name.TrimEnd(Path.DirectorySeparatorChar), true, 0, default)
                {
                    SizeOverride = "",
                    DisplayOverride = DriveRowName(d.Name, "checking"),
                })
                .ToList();

            // Apple Music sits after the drives, browsed with the same keys.
            if (AppleMusic.IsSetUp)
                entries.Add(new Entry(AppleMusic.Root, AppleMusic.RootName, true, 0, default)
                {
                    SizeOverride = "",
                    DisplayOverride = AppleMusic.RootName,
                });

            SetEntries(pane, entries, preferPath);   // totals the status bar itself
            pane.LastLoadedPath = DrivesPath;

            // Read aloud, exactly as arriving in a folder is. Landing on row 0 of
            // the drive list from row 0 of a folder is not a focus change to
            // anything watching from outside — the index has not moved — so
            // Backspace out of C:\ produced a keystroke and not one word.
            bool sameRow = previousRow != null &&
                           string.Equals(previousRow, FocusedPathOf(pane), StringComparison.OrdinalIgnoreCase);
            FocusListAndRow(pane, readRowAloud: !(quiet && sameRow));

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

            // A navigation started after this one — even one still checking that
            // its folder is there, which has cancelled nothing yet — bumps this.
            int navigation = pane.NavigationId;
            bool Overtaken() => pane.NavigationId != navigation;

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
                // share: a navigation somebody started meanwhile wins — including
                // one still checking its folder, which has cancelled nothing yet.
                var fallback = await NearestExistingAncestorAsync(path);
                if (IsDisposed || token.IsCancellationRequested || Overtaken()) return false;
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
                if (IsDisposed || token.IsCancellationRequested || Overtaken()) return false;
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
                if (entries.Count == 0 && pane == Active) AnnounceOperation("nav.empty", "This folder is empty");
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
                AnnounceOperation("nav.empty", "This folder is empty");

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
            private List<Entry> _shown = new();
            private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>The list's order, taken once on the UI thread for the worker to sort batches with.</summary>
            private readonly Comparison<Entry> _order;
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
                _order = form.ListOrder(path);
            }

            /// <summary>
            /// From the worker: some more of the folder, sorted here, on the
            /// worker. The UI thread only merges it in. Re-sorting everything
            /// shown on every tick was 43ms of the UI thread per tick at the end
            /// of a 100,000-entry folder, and three times that with logical names.
            /// </summary>
            public void Offer(List<Entry> batch)
            {
                if (batch.Count == 0 || !_enabled) return;
                var sorted = new List<Entry>(batch);
                PrepareSortKeys(sorted);
                sorted.Sort(_order);
                _arrived.Enqueue(sorted);
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
                // Everything that arrived since the last tick, merged among
                // itself first — those are short — so the long list on screen is
                // merged into once per tick, not once per batch.
                List<Entry>? fresh = null;
                while (_arrived.TryDequeue(out var batch))
                {
                    var unseen = new List<Entry>(batch.Count);
                    foreach (var entry in batch)
                        if (_seen.Add(entry.Path)) unseen.Add(entry);
                    if (unseen.Count == 0) continue;
                    fresh = fresh == null ? unseen : MergeSorted(fresh, unseen, _order);
                }
                if (fresh == null) return;

                // Rows that would land above the cursor, or above any selected
                // row, wait for the finished listing. Let in, they moved the
                // cursor's row down, and putting the cursor back on it was a
                // focus change the screen reader read out again, every tick for
                // as long as the folder was arriving. Held, nothing marked moves.
                if (Shown && LastMarkedIndex(_pane) is int pivot && pivot >= 0 && pivot < _shown.Count)
                {
                    var marked = _shown[pivot];
                    fresh = fresh.FindAll(e => _order(e, marked) >= 0);
                    if (fresh.Count == 0) return;
                }

                // Rows before the first one arriving keep their places, and
                // their cached items and index entries with them.
                int lo = 0, hi = _shown.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) >>> 1;
                    if (_order(_shown[mid], fresh[0]) <= 0) lo = mid + 1;
                    else hi = mid;
                }

                // Each batch is already in order, and so is what is shown:
                // one linear merge, and a new list, because the pane holds
                // the last one.
                _shown = MergeSorted(_shown, fresh, _order);
                Put(_shown, unchanged: lo);
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

            /// <param name="unchanged">How many leading rows are the same rows as last time.</param>
            private void Put(List<Entry> entries, int unchanged = 0)
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

                // Nothing at or above the last marked row moved: the control's
                // focus and selection are already right, and setting them again
                // is read out again.
                bool marksStay = unchanged > LastMarkedIndex(_pane);
                _form.ReplaceEntriesInPlace(_pane, entries, unchanged);

                if (focusedPath != null && _pane.IndexByPath.TryGetValue(focusedPath, out var now))
                {
                    _landed = true;
                    if (!marksStay && (now != CurrentIndex(_pane) || selected.Count > 1))
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
        /// <param name="unchanged">
        /// Leading rows that are the very same rows as before, in the same places.
        /// Their cached items and index entries are kept: rebuilding both for a
        /// whole 100,000-row folder on every tick of a progressive load was the
        /// UI thread's time going on rows nobody had moved. Only valid when
        /// rows are only ever added, which is what a progressive load does.
        /// </param>
        private void ReplaceEntriesInPlace(Pane pane, List<Entry> entries, int unchanged = 0)
        {
            var old = pane.Entries;
            int keep = Math.Min(unchanged, Math.Min(old.Count, entries.Count));
            // Cheap proof the prefix really is the pane's own: same rows at both ends of it.
            if (keep > 0 && (!ReferenceEquals(old[0], entries[0]) || !ReferenceEquals(old[keep - 1], entries[keep - 1])))
                keep = 0;

            var oldCache = pane.ItemCache;
            var cache = new ListViewItem?[entries.Count];
            int cached = 0;
            if (keep > 0)
            {
                int kept = Math.Min(keep, oldCache.Length);
                Array.Copy(oldCache, cache, kept);
                cached = pane.CachedCount;
                for (int i = kept; i < oldCache.Length; i++)
                    if (oldCache[i] != null) cached--;
            }
            pane.Entries = entries;
            pane.ItemCache = cache;
            pane.CachedCount = Math.Max(0, cached);

            if (keep > 0)
            {
                // Every row past the prefix is either new or one that moved
                // down; nothing was removed, so overwriting is the whole update.
                var index = pane.IndexByPath;
                for (int i = keep; i < entries.Count; i++) index[entries[i].Path] = i;
            }
            else
            {
                var index = new Dictionary<string, int>(entries.Count, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < entries.Count; i++) index[entries[i].Path] = i;
                pane.IndexByPath = index;
            }

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

        /// <summary>The highest row that is focused or selected, or -1.</summary>
        private static int LastMarkedIndex(Pane pane)
        {
            int last = CurrentIndex(pane);
            if (!pane.List.IsHandleCreated) return last;
            int i = -1, seen = 0;
            while ((i = (int)SendMessage(pane.List.Handle, LVM_GETNEXTITEM, new IntPtr(i), new IntPtr(LVNI_SELECTED))) >= 0)
            {
                if (i > last && i < pane.Entries.Count) last = i;
                if (++seen > 100_000) break;
            }
            return last;
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
                {
                    var entry = new Entry(Path.Combine(path, item.Name), item.Name,
                        item.IsFolder, item.Size, item.Modified);
                    entry.PrepareTypeAhead();
                    result.Add(entry);
                }
            }
            else
            {
                var batch = offer == null ? null : new List<Entry>(256);
                long lastOffer = Environment.TickCount64;

                // The row is built straight from the directory scan's own record.
                // Going through EnumerateFileSystemInfos made a FileInfo per entry
                // — an object and two strings — only to copy four facts out of it
                // and drop it; at a hundred thousand files that was a third of
                // the listing. The facts are the same ones, read from the same
                // record, and so is every exception the scan can throw.
                var listing = new System.IO.Enumeration.FileSystemEnumerable<Entry>(NameRules.ExactPath(path),
                    static (ref System.IO.Enumeration.FileSystemEntry found) => RowFrom(ref found),
                    ListingOptions)
                {
                    ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry found) =>
                        (showHidden || (found.Attributes & FileAttributes.Hidden) == 0) &&
                        (showSystem || (found.Attributes & FileAttributes.System) == 0),
                };

                foreach (var entry in listing)
                {
                    token.ThrowIfCancellationRequested();

                    if (batch != null && batch.Count > 0 &&
                        (batch.Count >= 500 || Environment.TickCount64 - lastOffer > 150))
                    {
                        offer!(batch);
                        batch = new List<Entry>(256);
                        lastOffer = Environment.TickCount64;
                    }

                    result.Add(entry);
                    batch?.Add(entry);
                }

                if (batch is { Count: > 0 }) offer!(batch);
            }

            return SortLikeAFolder(result, path);
        }

        /// <summary>
        /// What <c>DirectoryInfo.EnumerateFileSystemInfos()</c> asks for — every
        /// entry, nothing skipped, an unreadable folder an exception — with a
        /// bigger buffer: each request to the filesystem brings back sixteen
        /// times as many names, so a big folder is far fewer round trips to a
        /// share. Measured through \\localhost\C$, 100,000 files listed in 105ms
        /// against 224 with the default buffer; on the disk, 74 against 91.
        /// </summary>
        private static readonly EnumerationOptions ListingOptions = new()
        {
            MatchType = MatchType.Win32,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            BufferSize = 64 * 1024,
        };

        /// <summary>One listed entry as a row, with its type-ahead name folded here on the worker.</summary>
        private static Entry RowFrom(ref System.IO.Enumeration.FileSystemEntry found)
        {
            bool isDir = found.IsDirectory;
            DateTime modified;
            try { modified = found.LastWriteTimeUtc.UtcDateTime.ToLocalTime(); }
            catch { modified = default; }

            var entry = new Entry(NameRules.PlainPath(found.ToFullPath()), found.FileName.ToString(), isDir,
                isDir ? -1 : found.Length, modified);
            entry.PrepareTypeAhead();
            return entry;
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
        private List<Entry> SortLikeAFolder(List<Entry> result, string? folder)
        {
            PrepareSortKeys(result);
            result.Sort(ListOrder(folder));

            // In order now, and nothing sorts this list again: a new order is a
            // new listing. The keys would only be memory.
            foreach (var row in result) row.NameKey = null;
            return result;
        }

        /// <summary>
        /// Gives each row the key its name sorts by, so the sort compares bytes
        /// instead of calling into Windows n log n times. Rows that already have
        /// one — a progressive load keyed them batch by batch — keep it. Spread
        /// over the cores for a big folder; this runs on a worker, apart from
        /// search results, which stop at ten thousand.
        /// </summary>
        private static void PrepareSortKeys(List<Entry> rows)
        {
            if (rows.Count >= 8192)
                Parallel.For(0, rows.Count, i => rows[i].NameKey ??= NameRules.LogicalKey(rows[i].Name));
            else
                foreach (var row in rows) row.NameKey ??= NameRules.LogicalKey(row.Name);
        }

        /// <summary>
        /// The whole order as one comparison: folders first when asked, then the
        /// column, then the direction. Total, because every column falls back to
        /// the name and the name to the path — which is what lets a progressive
        /// load sort each batch on the worker and merge it into what is already
        /// shown, instead of re-sorting everything on the UI thread every tick.
        /// </summary>
        /// <summary>
        /// The order a folder is listed in: its own, chosen from File, Sort this
        /// folder by, or the one in Preferences. Safe on a worker, because
        /// FolderSorts is replaced whole and never edited in place.
        /// </summary>
        private (SortColumn By, bool Ascending) SortFor(string? folder)
        {
            if (FolderSortOf(folder) is { } own) return own;
            return (_settings.SortBy, _settings.SortAscending);
        }

        private (SortColumn By, bool Ascending)? FolderSortOf(string? folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            var sorts = _settings.FolderSorts;
            if (sorts == null || sorts.Count == 0) return null;

            var key = FolderSortKey(folder);
            string? value = null;
            foreach (var pair in sorts)
                if (string.Equals(FolderSortKey(pair.Key), key, StringComparison.OrdinalIgnoreCase)) { value = pair.Value; break; }
            if (value == null) return null;

            var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || !Enum.TryParse<SortColumn>(words[0], ignoreCase: true, out var by)) return null;
            bool ascending = words.Length < 2 || !words[1].Equals("descending", StringComparison.OrdinalIgnoreCase);
            return (by, ascending);
        }

        private static string FolderSortKey(string folder)
        {
            var trimmed = Path.TrimEndingDirectorySeparator(folder);
            // "C:" alone means the current directory on C:, so a root keeps its slash.
            return trimmed.EndsWith(':') ? trimmed + "\\" : trimmed;
        }

        /// <summary>
        /// Gives the folder its own order, or with null hands it back to the
        /// Preferences order, then re-sorts it on the spot.
        /// </summary>
        private void SetFolderSort(string folder, (SortColumn By, bool Ascending)? sort)
        {
            var key = FolderSortKey(folder);
            var sorts = new Dictionary<string, string>();
            foreach (var pair in _settings.FolderSorts ?? new())
                if (!string.Equals(FolderSortKey(pair.Key), key, StringComparison.OrdinalIgnoreCase))
                    sorts[pair.Key] = pair.Value;
            if (sort is { } s)
                sorts[key] = s.By + (s.Ascending ? " ascending" : " descending");

            _settings.FolderSorts = sorts;
            _settings.Save();
            ResortActive();

            var (by, ascending) = SortFor(folder);
            AnnounceOperation("nav.sorted",
                (sort == null ? "Default order, " : "") +
                $"sorted by {SortName(by)}, {(ascending ? "ascending" : "descending")}");
        }

        private static bool StartsAscending(SortColumn column) => NameRules.StartsAscending(column);

        /// <summary>
        /// A new order for the rows already on screen, without reading the
        /// folder again. Reloading to re-sort cost a whole listing — on the
        /// Drive letter a round trip to Google — for rows already in hand.
        /// Sorted on a worker, and the cursor stays on the same item. A folder
        /// still loading is reloaded instead, since half its rows are not here.
        /// </summary>
        private async void ResortActive()
        {
            var pane = Active;
            if (pane.Loading) { Refresh_(); return; }

            try
            {
                var shown = pane.Entries;
                var focused = FocusedPathOf(pane);
                var folder = pane.CurrentPath;
                var rows = new List<Entry>(shown);
                var sorted = await Task.Run(() => SortLikeAFolder(rows, folder));

                // Moved on, reloaded or changed while sorting: that listing is
                // already in whatever order is current.
                if (IsDisposed || pane.Entries != shown || pane.CurrentPath != folder) return;

                // The selection moves with its rows. SetEntries keeps only the
                // focused one, so sorting a twelve-file selection left one.
                var selected = SelectedPathsOf(pane);
                SetEntries(pane, sorted, focused);
                if (selected.Count > 1 && focused != null && pane.IndexByPath.TryGetValue(focused, out var at))
                    Reselect(pane, at, selected);
            }
            catch (Exception ex)
            {
                ShowStatus("Could not sort: " + ex.Message);
            }
        }

        private static string SortName(SortColumn column) => NameRules.SortsAs(column) switch
        {
            SortColumn.Size => "size",
            SortColumn.Modified => "date modified",
            _ => "name",
        };

        /// <summary>File, Sort this folder by: ticks set as it opens, from the active pane's folder.</summary>
        private ToolStripMenuItem BuildSortMenu()
        {
            var menu = new ToolStripMenuItem("Sort this folder &by");
            var byName = new ToolStripMenuItem("&Name");
            var bySize = new ToolStripMenuItem("&Size");
            var byDate = new ToolStripMenuItem("Date &modified");
            var ascendingItem = new ToolStripMenuItem("&Ascending");
            var descendingItem = new ToolStripMenuItem("&Descending");
            var useDefault = new ToolStripMenuItem("&Use the default order");

            bool HasFolder() => !IsDrivesView && !string.IsNullOrEmpty(Active.CurrentPath);

            void Column(SortColumn column)
            {
                if (!HasFolder()) return;
                var (by, ascending) = SortFor(Active.CurrentPath);
                // A new column starts its own way round: newest first for dates.
                bool keep = NameRules.SortsAs(by) == NameRules.SortsAs(column);
                SetFolderSort(Active.CurrentPath, (column, keep ? ascending : StartsAscending(column)));
            }

            void Direction(bool ascending)
            {
                if (!HasFolder()) return;
                SetFolderSort(Active.CurrentPath, (SortFor(Active.CurrentPath).By, ascending));
            }

            byName.Click += (_, _) => Column(SortColumn.Name);
            bySize.Click += (_, _) => Column(SortColumn.Size);
            byDate.Click += (_, _) => Column(SortColumn.Modified);
            ascendingItem.Click += (_, _) => Direction(true);
            descendingItem.Click += (_, _) => Direction(false);
            useDefault.Click += (_, _) => { if (HasFolder()) SetFolderSort(Active.CurrentPath, null); };

            var fileItems = new ToolStripItem[]
            {
                byName, bySize, byDate, new ToolStripSeparator(),
                ascendingItem, descendingItem, new ToolStripSeparator(), useDefault,
            };

            // Present from the start. A menu item with no items of its own is
            // drawn without its arrow and is clicked rather than opened, and its
            // DropDownOpening never runs: the submenu could not be reached at all.
            menu.DropDownItems.AddRange(fileItems);

            menu.DropDownOpening += (_, _) =>
            {
                // Inside Apple Music the orders are different (recently added,
                // artist, release date…), so the whole submenu is built for the
                // folder on screen; everywhere else it is the file columns.
                menu.DropDownItems.Clear();
                if (AppleMusic.Is(Active.CurrentPath))
                {
                    PopulateAppleSortMenu(menu, Active.CurrentPath);
                    return;
                }

                menu.DropDownItems.AddRange(fileItems);
                var (by, ascending) = SortFor(Active.CurrentPath);
                var shown = NameRules.SortsAs(by);
                byName.Checked = shown == SortColumn.Name;
                bySize.Checked = shown == SortColumn.Size;
                byDate.Checked = shown == SortColumn.Modified;
                ascendingItem.Checked = ascending;
                descendingItem.Checked = !ascending;
                useDefault.Checked = FolderSortOf(Active.CurrentPath) == null;
            };
            return menu;
        }

        /// <summary>
        /// The Sort submenu for an Apple Music folder: the orders that folder
        /// offers (name, recently added, artist, release date, length…), the two
        /// directions worded for the chosen order, and the default. Built fresh
        /// each time it opens, from what the folder holds.
        /// </summary>
        private void PopulateAppleSortMenu(ToolStripMenuItem menu, string folder)
        {
            var sorts = AppleMusic.SortsFor(folder);
            if (sorts.Count == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem("No sorting here") { Enabled = false });
                return;
            }

            var (currentBy, currentAsc) = AppleSortFor(folder);
            foreach (var sort in sorts)
            {
                string label = sort == AppleMusic.Sort.Apple
                    ? AppleMusic.AppleOrderName(folder) ?? "Apple's order"
                    : Capitalise(AppleMusic.SortName(sort));
                var item = new ToolStripMenuItem(label) { Checked = sort == currentBy };
                var chosen = sort;
                item.Click += (_, _) =>
                {
                    // A new order starts the way round that suits it; the same
                    // order keeps the direction it already had.
                    bool asc = chosen == currentBy ? currentAsc : AppleMusic.StartsAscending(chosen);
                    SetAppleSort(folder, (chosen, asc));
                };
                menu.DropDownItems.Add(item);
            }

            menu.DropDownItems.Add(new ToolStripSeparator());
            var up = new ToolStripMenuItem(Capitalise(AppleDirection(folder, currentBy, true))) { Checked = currentAsc };
            var down = new ToolStripMenuItem(Capitalise(AppleDirection(folder, currentBy, false))) { Checked = !currentAsc };
            up.Click += (_, _) => SetAppleSort(folder, (currentBy, true));
            down.Click += (_, _) => SetAppleSort(folder, (currentBy, false));
            // Oldest first, then newest, whichever way round the order itself runs
            // (Recently added's own order is newest first).
            bool newestUp = currentBy == AppleMusic.Sort.Apple && IsRecentlyAddedOrder(folder);
            menu.DropDownItems.Add(newestUp ? down : up);
            menu.DropDownItems.Add(newestUp ? up : down);

            menu.DropDownItems.Add(new ToolStripSeparator());
            var def = new ToolStripMenuItem("Use the default order") { Checked = AppleSortIsDefault(folder) };
            def.Click += (_, _) => SetAppleSort(folder, null);
            menu.DropDownItems.Add(def);
        }

        private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>True when the folder is on its own default order (nothing chosen for it).</summary>
        private bool AppleSortIsDefault(string folder)
        {
            var key = FolderSortKey(folder);
            foreach (var pair in _settings.FolderSorts ?? new())
                if (string.Equals(FolderSortKey(pair.Key), key, StringComparison.OrdinalIgnoreCase)
                    && pair.Value.StartsWith(AppleSortPrefix, StringComparison.Ordinal))
                    return false;
            return true;
        }

        private Comparison<Entry> ListOrder(string? folder)
        {
            var (by, ascending) = SortFor(folder);
            var cmp = ComparerFor(by);
            bool foldersFirst = _settings.FoldersFirst;
            return (a, b) =>
            {
                if (foldersFirst && a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
                int c = cmp(a, b);
                return ascending ? c : -c;
            };
        }

        /// <summary>
        /// A short sorted batch merged into a long sorted list: each new entry's
        /// place is found by binary search from the last one's, and the rows in
        /// between are copied without being compared. That is m log n
        /// comparisons rather than n + m — which matters because a logical name
        /// comparison is a call into Windows, and a plain merge of 100,000 rows
        /// spent 80ms of the UI thread on one tick doing them.
        /// </summary>
        private static List<Entry> MergeSorted(List<Entry> into, List<Entry> batch, Comparison<Entry> order)
        {
            var merged = new List<Entry>(into.Count + batch.Count);
            int from = 0;
            foreach (var item in batch)
            {
                int lo = from, hi = into.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) >>> 1;
                    if (order(into[mid], item) <= 0) lo = mid + 1;
                    else hi = mid;
                }
                for (int k = from; k < lo; k++) merged.Add(into[k]);
                merged.Add(item);
                from = lo;
            }
            for (int k = from; k < into.Count; k++) merged.Add(into[k]);
            return merged;
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
        private static Comparison<Entry> ComparerFor(SortColumn column) => NameRules.SortsAs(column) switch
        {
            SortColumn.Size => static (a, b) =>
            {
                int c = a.Size.CompareTo(b.Size);
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
            NameRules.CompareNames(a.Name, a.NameKey, a.Path, b.Name, b.NameKey, b.Path);

        // ---------- Archives as folders ----------

        /// <summary>
        /// How long opening or taking something out of an archive runs before
        /// its progress window appears. A zip opens in a few milliseconds and a
        /// window flashing up and away for it would be two announcements of
        /// nothing; a big .tar.gz or a 7z takes seconds and needs a Cancel.
        /// </summary>
        private const int ArchiveWindowDelayMs = 1000;

        /// <summary>
        /// Says that the pane is inside an archive and the command would change
        /// it, and returns true; returns false anywhere else. Every command that
        /// writes to the folder on screen or to the selection asks this first.
        /// </summary>
        private bool RefuseInArchive()
        {
            if (AppleMusic.Is(Active.CurrentPath))
            {
                AnnounceOperation("applemusic.refused", "Apple Music can only be browsed and played here");
                RestoreListFocus();
                return true;
            }
            if (Active.Archive == null) return false;
            AnnounceOperation("archive.refused", ArchiveBrowser.ReadOnlyMessage);
            RestoreListFocus();
            return true;
        }

        /// <summary>
        /// Runs archive work on a worker with a progress window that appears only
        /// if it takes a while, and says how it ended when it did not finish.
        /// Returns whether it finished, and what it produced.
        /// </summary>
        private async Task<(bool Done, T? Value)> RunArchiveWork<T>(
            string title,
            Func<IProgress<TransferProgress>, CancellationToken, T> work,
            string cancelledId, string cancelledText,
            string failedId, string failedText)
        {
            // Cancelled, never disposed: the worker still holds its token.
            var cts = new CancellationTokenSource();
            using var window = new ProgressForm(title, _settings, cts);
            var progress = new Progress<TransferProgress>(p => window.Update(p));

            bool shown = false;
            RunningTransfers++;
            try
            {
                var task = Task.Run(() => work(progress, cts.Token));
                if (await Task.WhenAny(task, Task.Delay(ArchiveWindowDelayMs)) != task && !IsDisposed)
                {
                    window.Show();
                    shown = true;
                }

                try
                {
                    var value = await task;
                    return (true, value);
                }
                catch (OperationCanceledException)
                {
                    if (!IsDisposed) AnnounceOperation(cancelledId, cancelledText);
                    return (false, default);
                }
                catch (Exception ex)
                {
                    if (!IsDisposed) AnnounceOperation(failedId, $"{failedText}: {ex.Message.TrimEnd('.')}");
                    return (false, default);
                }
            }
            finally
            {
                RunningTransfers--;
                window.Finish();
                if (shown) RestoreListFocus();
            }
        }

        /// <summary>
        /// Enter on an archive: reads what is in it on a worker and shows its top
        /// level in the pane, as though it were a folder. Anything that goes
        /// wrong is said and leaves the pane where it was.
        /// </summary>
        private async Task OpenArchiveAsync(Pane pane, string archivePath, string inner, string? preferPath,
            bool keepFocus = false)
        {
            // Navigating anywhere meanwhile wins: the archive is then not shown.
            int navigation = ++pane.NavigationId;
            var name = Path.GetFileName(archivePath);

            // Marked as a navigation in progress, as Go to path is: a big
            // archive takes seconds to read, and the folder's watcher reloading
            // it meanwhile was a navigation that silently threw the archive away.
            pane.Probing = navigation;
            bool done;
            ArchiveListing? listing;
            try
            {
                (done, listing) = await RunArchiveWork($"Opening {name}",
                    (progress, token) => ArchiveBrowser.Open(archivePath, progress, token),
                    "archive.open.cancelled", "Opening cancelled",
                    "archive.open.failed", $"Could not open {name}");
            }
            finally
            {
                if (pane.Probing == navigation) pane.Probing = 0;
            }

            if (IsDisposed) return;
            if (!done || listing == null) { RestoreListFocus(); return; }
            if (pane.NavigationId != navigation) return;

            var before = pane.Archive;
            pane.Archive = listing;
            bool landed = await NavigateAsync(ArchiveBrowser.VirtualPath(archivePath, inner),
                preferPath, onPane: pane, keepFocus: keepFocus);

            // Overtaken before it was shown: the pane is still on what it showed
            // before, and left marked as inside this archive, a real folder
            // refused every command as read-only and went unwatched.
            if (!landed && ReferenceEquals(pane.Archive, listing) &&
                ArchiveBrowser.InnerPathOf(listing.ArchivePath, pane.CurrentPath) == null)
            {
                pane.Archive = before != null && ArchiveBrowser.InnerPathOf(before.ArchivePath, pane.CurrentPath) != null
                    ? before
                    : null;
                if (NeedsWatcher(pane) && pane == Active && Visible) ArmWatcher(pane, pane.CurrentPath);
            }

            // After the first row, never instead of it: queued behind it.
            if (landed && listing.Warning is { } warning) AnnounceOperation("archive.warning", warning);
        }

        private async void OpenArchive(string archivePath)
        {
            try { await OpenArchiveAsync(Active, archivePath, "", null); }
            catch (Exception ex)
            {
                if (!IsDisposed) AnnounceOperation("archive.open.failed",
                    $"Could not open {Path.GetFileName(archivePath)}: {ex.Message}");
            }
        }

        /// <summary>
        /// F5 inside an archive: reads the archive again, in case it changed on
        /// disk, and comes back to the same folder and row.
        /// </summary>
        private async Task RefreshArchiveAsync(Pane pane)
        {
            var archive = pane.Archive;
            if (archive == null) return;
            var inner = ArchiveBrowser.InnerPathOf(archive.ArchivePath, pane.CurrentPath) ?? "";
            await OpenArchiveAsync(pane, archive.ArchivePath, inner, FocusedPathOf(pane), keepFocus: true);
        }

        /// <summary>
        /// One folder of the archive the pane is in, from the listing in memory.
        /// The same landing rules as a real folder: up lands on the folder you
        /// came out of, a folder visited before reopens on its row, and the row
        /// is read aloud on arriving.
        /// </summary>
        private async Task<bool> ShowArchiveFolderAsync(Pane pane, ArchiveListing archive, string inner,
            string? preferPath, string cameFrom, int mine, bool quiet, bool keepFocus)
        {
            // A folder that is not in the archive (any more): the nearest one that is.
            while (inner.Length > 0 && !archive.IsFolder(inner)) inner = ArchiveBrowser.ParentOf(inner);
            var full = ArchiveBrowser.VirtualPath(archive.ArchivePath, inner);

            var token = BeginPaneWork(pane);
            pane.Searching = false;

            bool sameFolder = string.Equals(pane.LastLoadedPath, full, StringComparison.OrdinalIgnoreCase);
            var previousRow = sameFolder ? FocusedPathOf(pane) : null;
            if (keepFocus && sameFolder) preferPath = previousRow ?? preferPath;
            preferPath ??= PreferredRowFor(pane, full, cameFrom);

            bool sizes = _settings.FolderSizes == FolderSizeMode.Automatic;
            var units = _settings.SizeUnits;
            var nodes = archive.Children(inner);

            // The pane is moved only once the rows are in and this is still its
            // navigation. Moved first, a navigation that overtook it without
            // cancelling (Go to path, a folder from outside) and then went
            // nowhere left the path on the archive over the old folder's rows.
            // Marked as in progress meanwhile, so the old folder's watcher
            // waits rather than reloading over it.
            pane.Probing = mine;
            List<Entry> entries;
            try
            {
                // On a worker like any listing: a zip can hold fifty thousand
                // files in one folder, and sorting them is not free.
                entries = await Task.Run(() =>
                {
                    var rows = new List<Entry>(nodes.Count);
                    foreach (var node in nodes)
                    {
                        token.ThrowIfCancellationRequested();
                        var row = new Entry(ArchiveBrowser.VirtualPath(archive.ArchivePath, node.InnerPath),
                            node.Name, node.IsDirectory, node.IsDirectory ? -1 : node.Size, node.Modified);

                        // The listing already knows what each folder holds, so
                        // automatic folder sizes cost nothing here.
                        if (node.IsDirectory && sizes) row.SizeOverride = SizeFormatter.Format(node.Size, units);
                        row.PrepareTypeAhead();
                        rows.Add(row);
                    }
                    return SortLikeAFolder(rows, full);
                }, token);
            }
            catch (OperationCanceledException) { return false; }
            finally
            {
                if (pane.Probing == mine) pane.Probing = 0;
            }

            if (token.IsCancellationRequested || IsDisposed || pane.NavigationId != mine) return false;

            DisarmWatcher(pane);
            pane.CurrentPath = full;
            if (pane == Active) _pathLabel.Text = full;
            UpdateTitle();

            // A quiet reload (Preferences saved) keeps a selection of several,
            // as a folder's does.
            var selectedNow = keepFocus && sameFolder ? SelectedPathsOf(pane) : null;

            SetEntries(pane, entries, preferPath);
            pane.LastLoadedPath = full;

            if (selectedNow is { Count: > 1 } && FocusedPathOf(pane) is { } focusedNow &&
                pane.IndexByPath.TryGetValue(focusedNow, out var keptAt))
                Reselect(pane, keptAt, selectedNow);

            bool sameRow = sameFolder && previousRow != null &&
                           string.Equals(previousRow, FocusedPathOf(pane), StringComparison.OrdinalIgnoreCase);
            FocusListAndRow(pane, readRowAloud: !(quiet && sameRow));

            if (entries.Count == 0 && pane == Active) AnnounceOperation("nav.empty", "This folder is empty");
            return true;
        }

        /// <summary>
        /// One Apple Music folder, listed by Apple (or from this session's memory
        /// of it), in the order chosen for it from File, Sort this folder by.
        /// The same landing rules as a real folder. Slow answers say so after a
        /// moment, so a search that takes two seconds is not two seconds of silence.
        /// </summary>
        private async Task<bool> ShowAppleMusicFolderAsync(Pane pane, string path, string? preferPath,
            string cameFrom, int mine, bool quiet, bool keepFocus)
        {
            var full = path.TrimEnd('\\');
            var token = BeginPaneWork(pane);
            pane.Searching = false;

            // The player starts while you browse, so the first Enter on a song
            // does not also wait for it. It closes itself if nothing is played.
            AppleMusicPlayer.Warm();
            AppleMusic.LoadPlaylistsSoon();   // for Add to playlist on Shift+F10

            bool sameFolder = string.Equals(pane.LastLoadedPath, full, StringComparison.OrdinalIgnoreCase);
            var previousRow = sameFolder ? FocusedPathOf(pane) : null;
            if (keepFocus && sameFolder) preferPath = previousRow ?? preferPath;

            // Up out of a folder lands on it; back into one lands where you were.
            if (preferPath == null && cameFrom.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase))
            {
                int next = cameFrom.IndexOf('\\', full.Length + 1);
                preferPath = next < 0 ? cameFrom : cameFrom.Substring(0, next);
            }
            preferPath ??= pane.Positions.Recall(full);

            pane.Probing = mine;
            List<AppleMusic.Row> rows;
            bool fromMemory = AppleMusic.HasCached(full);
            // Called off once the listing is in. Cancelled, never disposed.
            var loaded = CancellationTokenSource.CreateLinkedTokenSource(token);
            _ = Task.Delay(800, loaded.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                try
                {
                    BeginInvoke(() =>
                    {
                        if (pane.NavigationId == mine && pane == Active && !IsDisposed)
                            AnnounceOperation("applemusic.loading", "Loading from Apple Music");
                    });
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }, TaskScheduler.Default);
            try
            {
                rows = await AppleMusic.ListAsync(full, fresh: false, token);
            }
            catch (OperationCanceledException) { return false; }
            catch (AppleMusicException ex)
            {
                if (pane.NavigationId == mine && !IsDisposed)
                {
                    AnnounceOperation("applemusic.failed", ex.Message);
                    RestoreListFocus();
                }
                return false;
            }
            finally
            {
                if (pane.Probing == mine) pane.Probing = 0;
                loaded.Cancel();
            }

            if (token.IsCancellationRequested || IsDisposed || pane.NavigationId != mine) return false;

            var (by, ascending) = AppleSortFor(full);
            var entries = AppleEntries(AppleMusic.Ordered(full, rows, by, ascending));

            DisarmWatcher(pane);
            pane.Archive = null;
            pane.CurrentPath = full;
            if (pane == Active) _pathLabel.Text = AppleMusic.DisplayPath(full);
            UpdateTitle();

            var selectedNow = keepFocus && sameFolder ? SelectedPathsOf(pane) : null;
            SetEntries(pane, entries, preferPath);
            pane.LastLoadedPath = full;
            if (selectedNow is { Count: > 1 } && FocusedPathOf(pane) is { } focusedNow &&
                pane.IndexByPath.TryGetValue(focusedNow, out var keptAt))
                Reselect(pane, keptAt, selectedNow);

            bool sameRow = sameFolder && previousRow != null &&
                           string.Equals(previousRow, FocusedPathOf(pane), StringComparison.OrdinalIgnoreCase);
            FocusListAndRow(pane, readRowAloud: !(quiet && sameRow));

            if (entries.Count == 0 && pane == Active)
                AnnounceOperation("nav.empty", full.Equals(AppleMusic.SearchFolder, StringComparison.OrdinalIgnoreCase)
                    ? "No searches yet. Press F1 to search Apple Music"
                    : "This list is empty");

            // Shown from memory, so it was instant; now ask Apple behind it, so a
            // playlist changed elsewhere is noticed. Silent either way.
            if (fromMemory && AppleMusic.CanRefresh(full))
                _ = RefreshAppleMusicQuietlyAsync(pane, full, mine, token);
            return true;
        }

        private static List<Entry> AppleEntries(List<AppleMusic.Row> rows)
        {
            var entries = new List<Entry>(rows.Count);
            foreach (var row in rows)
            {
                // -1: no size, so a song is not read out as "0 bytes".
                var entry = new Entry(row.Path, row.Name, row.IsFolder, -1, default)
                {
                    SizeOverride = "",
                    DisplayOverride = row.Name,
                };
                entry.PrepareTypeAhead();
                entries.Add(entry);
            }
            return entries;
        }

        /// <summary>
        /// Asks Apple again for a list that was just shown from memory and, if
        /// anything changed (a song added to a playlist on the phone), puts the
        /// new rows in place without a word: the cursor stays on the row it is
        /// on and nothing is read out (Conner: refresh, but don't make NVDA aware
        /// of it). Given up the moment the pane goes anywhere else, and a failure
        /// leaves what is shown alone, also silently.
        /// </summary>
        private async Task RefreshAppleMusicQuietlyAsync(Pane pane, string folder, int navigation, CancellationToken token)
        {
            List<AppleMusic.Row> rows;
            try { rows = await AppleMusic.ListAsync(folder, fresh: true, token); }
            catch { return; }

            if (IsDisposed || token.IsCancellationRequested || pane.NavigationId != navigation ||
                !string.Equals(pane.CurrentPath, folder, StringComparison.OrdinalIgnoreCase)) return;

            ShowAppleRowsQuietly(pane, folder, rows);
        }

        /// <summary>
        /// A station that played on past its end has more songs in memory: if a
        /// pane is showing it, they go on the end of the list, silently.
        /// </summary>
        private void OnAppleMusicGrew(string folder)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(async () =>
                {
                    foreach (var pane in _panes)
                    {
                        if (IsDisposed || !string.Equals(pane.CurrentPath, folder, StringComparison.OrdinalIgnoreCase)) continue;
                        // Any failure here is only a list not updated: an exception
                        // out of this async lambda would take the window down.
                        try { ShowAppleRowsQuietly(pane, folder, await AppleMusic.ListAsync(folder, fresh: false, CancellationToken.None)); }
                        catch (Exception) { }
                    }
                });
            }
            catch (InvalidOperationException) { }   // no window handle yet, or going away
        }

        /// <summary>
        /// New rows for the list a pane is showing, put in place without a word:
        /// the cursor stays on its row and nothing is read out.
        /// </summary>
        private void ShowAppleRowsQuietly(Pane pane, string folder, List<AppleMusic.Row> rows)
        {
            var (by, ascending) = AppleSortFor(folder);
            var ordered = AppleMusic.Ordered(folder, rows, by, ascending);

            // Nearly always nothing has changed, and then nothing is touched.
            var shown = pane.Entries;
            if (shown.Count == ordered.Count)
            {
                bool same = true;
                for (int i = 0; i < shown.Count && same; i++)
                    same = string.Equals(shown[i].Path, ordered[i].Path, StringComparison.Ordinal) &&
                           string.Equals(shown[i].DisplayOverride, ordered[i].Name, StringComparison.Ordinal);
                if (same) return;
            }

            // In place, so the control keeps its focus and selection states; the
            // cursor is moved only if its row now sits somewhere else.
            var focusedPath = FocusedPathOf(pane);
            var selected = SelectedPathsOf(pane);
            int before = CurrentIndex(pane);
            ReplaceEntriesInPlace(pane, AppleEntries(ordered));
            if (focusedPath != null && pane.IndexByPath.TryGetValue(focusedPath, out var now))
            {
                if (now != before || selected.Count > 1) Reselect(pane, now, selected);
            }
            else if (ordered.Count > 0 && before >= 0)
            {
                // The row the cursor was on is gone (taken out on the phone): the
                // cursor stays where it was, on whatever is there now, rather than
                // off the end of a shorter list with no row focused.
                Reselect(pane, Math.Min(before, ordered.Count - 1), selected);
            }
        }

        /// <summary>The order chosen for an Apple Music folder, or its own when none has been.</summary>
        private (AppleMusic.Sort By, bool Ascending) AppleSortFor(string folder)
        {
            var key = FolderSortKey(folder);
            foreach (var pair in _settings.FolderSorts ?? new())
            {
                if (!string.Equals(FolderSortKey(pair.Key), key, StringComparison.OrdinalIgnoreCase)) continue;
                var words = pair.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 0 && words[0].StartsWith(AppleSortPrefix, StringComparison.Ordinal) &&
                    Enum.TryParse<AppleMusic.Sort>(words[0].Substring(AppleSortPrefix.Length), out var by))
                    return (by, words.Length < 2 || !words[1].Equals("descending", StringComparison.OrdinalIgnoreCase));
            }
            return AppleMusic.DefaultSort(folder);
        }

        /// <summary>Marks an Apple Music order in FolderSorts, where a file folder's SortColumn would be.</summary>
        private const string AppleSortPrefix = "apple:";

        /// <summary>Gives an Apple Music folder its own order (null: its default), and shows it at once.</summary>
        private void SetAppleSort(string folder, (AppleMusic.Sort By, bool Ascending)? sort)
        {
            var key = FolderSortKey(folder);
            var sorts = new Dictionary<string, string>();
            foreach (var pair in _settings.FolderSorts ?? new())
                if (!string.Equals(FolderSortKey(pair.Key), key, StringComparison.OrdinalIgnoreCase))
                    sorts[pair.Key] = pair.Value;
            if (sort is { } s)
                sorts[key] = AppleSortPrefix + s.By + (s.Ascending ? " ascending" : " descending");
            _settings.FolderSorts = sorts;
            _settings.Save();

            var pane = Active;
            _ = NavigateAsync(folder, FocusedPathOf(pane), pane, quiet: true, keepFocus: true);

            var (by, ascending) = AppleSortFor(folder);
            AnnounceOperation("nav.sorted", by == AppleMusic.Sort.Apple
                ? IsRecentlyAddedOrder(folder) ? Capitalise(AppleDirection(folder, by, ascending))
                  : AppleMusic.AppleOrderName(folder) ?? "Apple's order"
                : $"Sorted by {AppleMusic.SortName(by)}, {AppleDirection(folder, by, ascending)}");
        }

        /// <summary>Whether a folder's own order is Apple's newest-added-first (Library, Recently added).</summary>
        private static bool IsRecentlyAddedOrder(string folder) =>
            string.Equals(AppleMusic.AppleOrderName(folder), "Recently added", StringComparison.Ordinal);

        /// <summary>
        /// "newest first" says more than "descending" for a date. Added dates say
        /// so in full (Conner: "oldest added, and then most recently added"), and
        /// so does Recently added's own order, which runs newest first: "last to
        /// first" there meant nothing.
        /// </summary>
        private static string AppleDirection(string folder, AppleMusic.Sort by, bool ascending) => by switch
        {
            AppleMusic.Sort.Apple when IsRecentlyAddedOrder(folder) =>
                ascending ? "most recently added first" : "oldest added first",
            AppleMusic.Sort.RecentlyAdded => ascending ? "oldest added first" : "most recently added first",
            AppleMusic.Sort.RecentlyUpdated or AppleMusic.Sort.ReleaseDate =>
                ascending ? "oldest first" : "newest first",
            AppleMusic.Sort.Length => ascending ? "shortest first" : "longest first",
            AppleMusic.Sort.Apple => ascending ? "first to last" : "last to first",
            _ => ascending ? "A to Z" : "Z to A",
        };

        /// <summary>Enter on an Apple Music row: open a folder, ask what to search for, or play a song.</summary>
        private void OpenAppleMusicRow(Entry entry)
        {
            if (string.Equals(entry.Path, AppleMusic.SearchFolder, StringComparison.OrdinalIgnoreCase))
            {
                AskAppleMusicSearch();
                return;
            }
            // A station plays (Conner, 4.1.2): its songs are a stream, not a list
            // anyone wants to read through first.
            if (AppleMusic.ItemAt(entry.Path) is { Kind: "station" }) { PlayAppleStation(entry.Path); return; }
            if (entry.IsDir) { Navigate(entry.Path); return; }
            Program.PlayAppleMusic(entry.Path);
        }

        /// <summary>
        /// Enter on a station: asks Apple for its next songs and plays the first,
        /// and it plays on through the station from there (fetching more at the
        /// end, as before). The cursor stays on the station.
        /// </summary>
        private async void PlayAppleStation(string station)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var rows = await AppleMusic.ListAsync(station, fresh: true, cts.Token);
                if (IsDisposed) return;
                // Station order is the order it plays on in.
                var ordered = AppleMusic.Ordered(station, rows, AppleMusic.Sort.Apple, true);
                var first = ordered.FirstOrDefault(r => !r.IsFolder);
                if (first.Path == null)
                {
                    AnnounceOperation("applemusic.failed", "That station has nothing to play right now");
                    return;
                }
                Program.PlayAppleMusic(first.Path);
            }
            catch (AppleMusicException ex) { AnnounceOperation("applemusic.failed", ex.Message); }
            catch (OperationCanceledException) { AnnounceOperation("applemusic.failed", "Apple Music took too long to answer."); }
            catch (Exception ex) { AnnounceOperation("applemusic.failed", "Apple Music couldn't start the station: " + ex.Message); }
        }

        private string _lastAppleSearch = "";

        /// <summary>Search, in Apple Music: asks what for, then opens the results.</summary>
        private void AskAppleMusicSearch()
        {
            var term = PromptForm.Ask(this, "Search Apple Music", "Search Apple Music for:", _lastAppleSearch);
            if (string.IsNullOrWhiteSpace(term)) { RestoreListFocus(); return; }
            _lastAppleSearch = term.Trim();
            Navigate(AppleMusic.SearchPath(_lastAppleSearch));
        }

        /// <summary>
        /// Enter on a file inside an archive: just that file is taken out, into
        /// the cache, and then played by the built-in player or opened in its own
        /// app exactly as a file on disk would be.
        /// </summary>
        private async void OpenArchiveMember(ArchiveListing archive, string path, string name, bool openExternally)
        {
            try
            {
                var inner = ArchiveBrowser.InnerPathOf(archive.ArchivePath, path);
                if (inner == null) return;

                var (done, onDisk) = await RunArchiveWork($"Getting {name} out of {Path.GetFileName(archive.ArchivePath)}",
                    (progress, token) => ArchiveBrowser.ExtractOne(archive, inner, progress, token),
                    "extract.cancelled", "Extracting cancelled",
                    "archive.member.failed", $"Could not get {name} out of the archive");
                if (!done || onDisk == null || IsDisposed) return;

                if (!openExternally && _settings.PlayAudioInApp &&
                    AudioFiles.IsAudio(onDisk, _settings.AudioExtensions) &&
                    Program.PlayAudioFile(onDisk))
                    return;

                AnnounceOperation("file.opening", $"Opening {name}");

                await Task.Run(() => FileLauncher.Open(onDisk));
            }
            catch (Exception ex)
            {
                if (!IsDisposed) Announce($"Could not open {name}: {ex.Message}", isError: true);
            }
        }

        /// <summary>
        /// Ctrl+C inside an archive: the selected members, folders with all they
        /// hold, are taken out into a folder of their own and those files go on
        /// the clipboard, so Ctrl+V anywhere pastes them like any other copy.
        /// </summary>
        private async void CopyOutOfArchive(ArchiveListing archive, string[] paths)
        {
            try
            {
                var inners = paths.Select(p => ArchiveBrowser.InnerPathOf(archive.ArchivePath, p))
                    .Where(i => !string.IsNullOrEmpty(i)).Select(i => i!).ToList();
                if (inners.Count == 0) { AnnounceOperation("clip.nothing", "Nothing selected"); return; }

                var errors = new List<string>();
                var what = inners.Count == 1 ? Path.GetFileName(paths[0]) : NameRules.Items(inners.Count);

                var (done, arrived) = await RunArchiveWork(
                    $"Copying {what} out of {Path.GetFileName(archive.ArchivePath)}",
                    (progress, token) =>
                    {
                        var staging = ArchiveBrowser.NewCacheFolder("copy");
                        return ArchiveBrowser.ExtractSelection(archive, inners, staging, progress, errors, token);
                    },
                    "extract.cancelled", "Extracting cancelled",
                    "archive.member.failed", $"Could not get {what} out of the archive");
                if (!done || arrived == null || IsDisposed) return;

                if (arrived.Count == 0)
                {
                    AnnounceOperation("archive.member.failed",
                        $"Could not get {what} out of the archive: {(errors.Count > 0 ? errors[0] : "nothing came out")}");
                    return;
                }

                if (!ClipboardInterop.SetFiles(arrived.ToArray(), cut: false))
                {
                    Announce("Could not take the clipboard, another application is holding it", isError: true);
                    return;
                }

                AnnounceOperation("clip.copied", NameRules.SayCount("Copied", arrived.Count));
                if (errors.Count > 0) ShowErrors(errors);
            }
            catch (Exception ex)
            {
                if (!IsDisposed) AnnounceOperation("archive.member.failed", $"Could not copy out of the archive: {ex.Message}");
            }
        }

        private void GoUp()
        {
            if (IsDrivesView) { Announce("Already at the top"); return; }

            // By text, and never throwing: this runs on a keystroke and has to
            // cope with the pane being in a state nobody expected. Not
            // Directory.GetParent, which strips a trailing dot from every part
            // of the path, so Backspace in "Backup.\Disc 1" went to "Backup".
            var parent = SafeParent(Active.CurrentPath);

            if (string.IsNullOrEmpty(parent)) { Navigate(DrivesPath); return; }
            Navigate(parent);
        }

        /// <summary>
        /// Ctrl+L. Async void from a key, so the whole of it is guarded: anything
        /// escaping goes to Application.ThreadException and takes the window
        /// with it, and every other command on a key already carries this.
        /// </summary>
        private async void GoToPath()
        {
            try { await GoToPathAsync(); }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                Announce($"Could not go to that path: {ex.Message}", isError: true);
                RestoreListFocus();
            }
        }

        private async Task GoToPathAsync()
        {
            bool inApple = AppleMusic.Is(Active.CurrentPath);
            var typed = PromptForm.Ask(this, "Go to path", "Path:",
                IsDrivesView ? "" : AppleMusic.DisplayPath(Active.CurrentPath));
            if (typed == null) { RestoreListFocus(); return; }

            // An Apple Music list is gone to as itself, never looked for on disk.
            if (AppleMusic.FromTyped(typed.Trim().Trim('"'), displayForm: inApple) is { } applePath)
            {
                await NavigateAsync(applePath);
                return;
            }

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
                // "D:Music" is the current folder on D plus Music to Windows; typed, it means D:\Music.
                if (candidate.Length > 2 && candidate[1] == ':' && char.IsAsciiLetter(candidate[0]) && candidate[2] is not ('\\' or '/'))
                    candidate = candidate[..2] + "\\" + candidate[2..];
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
                        Directory.Exists(NameRules.ExactPath(asTyped)) || File.Exists(NameRules.ExactPath(asTyped)));
                    if (Superseded()) return;
                    if (!literalThere) typed = NameRules.NormaliseLaunchPath(Anchor(expanded));
                }

                // Focus, on this way out too. A typed value that is not blank but
                // normalises to nothing — a lone quote, an environment variable that
                // expands to nothing — left the keyboard on the form, where no arrow
                // moves and a screen reader has nothing to read. Every other exit from
                // this method already puts it back.
                if (typed.Length == 0) { RestoreListFocus(); return; }

                // A folder of the archive this tab is inside: no disk to ask.
                if (goingFrom.Archive is { } archive &&
                    ArchiveBrowser.InnerPathOf(archive.ArchivePath, typed) is { } inner)
                {
                    if (archive.IsFolder(inner)) await NavigateAsync(typed);
                    else if (archive.Find(inner) != null)
                        await NavigateAsync(ArchiveBrowser.VirtualPath(archive.ArchivePath, ArchiveBrowser.ParentOf(inner)),
                            preferPath: ArchiveBrowser.VirtualPath(archive.ArchivePath, inner));
                    else Announce("That path is not in this archive", isError: true);
                    RestoreListFocus();
                    return;
                }

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
                bool isFile = parent != null && await Bounded(() => File.Exists(NameRules.ExactPath(typed)));
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

            var pane = Active;

            // In Apple Music, F5 asks Apple again rather than showing what this
            // session already has.
            if (AppleMusic.Is(pane.CurrentPath))
            {
                var folder = pane.CurrentPath;
                AppleMusic.Forget(folder);
                bool shown = await NavigateAsync(folder, FocusedPathOf(pane), pane, quiet: true, keepFocus: true);
                if (!IsDisposed && shown)
                    AnnounceOperation("nav.refreshed", $"Refreshed, {NameRules.Items(pane.Entries.Count)}");
                return;
            }

            // Inside an archive, F5 reads the archive again.
            if (pane.Archive != null)
            {
                try { await RefreshArchiveAsync(pane); }
                catch (Exception ex)
                {
                    if (!IsDisposed) AnnounceOperation("archive.open.failed", $"Could not read the archive again: {ex.Message}");
                }
                return;
            }

            bool landed = await RefreshAsync();
            if (IsDisposed || !landed) return;

            // Only for a reload that landed on the folder, counted from the pane
            // that was reloaded: one that fell back or was overtaken has said
            // something truer, and this cut it off.
            AnnounceOperation("nav.refreshed", $"Refreshed, {NameRules.Items(pane.Entries.Count)}");
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

            // In Apple Music, searching asks Apple, not the disk.
            if (AppleMusic.Is(where))
            {
                AskAppleMusicSearch();
                return;
            }

            // A search walks the disk, and an archive's folders are not on it.
            if (Active.Archive != null)
            {
                AnnounceOperation("search.nowhere", "Searching inside an archive is not available yet");
                return;
            }

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
                        FileSearch.MostResults, token, skip: OnDriveFolder),
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

            entries = SortLikeAFolder(entries, root);

            pane.SearchTerm = term;
            pane.SearchTruncated = result.Truncated;
            pane.TypeAhead.Reset();

            SetEntries(pane, entries);
            UpdatePaneName(pane);
            UpdateTitle();
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
            : AppleMusic.DisplayPath(pane.CurrentPath);

        /// <summary>A tab's place for the title: its path backwards (NameRules.Backwards).</summary>
        private string TitleLabelFor(Pane pane) =>
            pane.SearchTerm is { } term ? $"Results for \"{term}\" in {NameRules.Backwards(pane.CurrentPath)}"
            : pane.CurrentPath == DrivesPath ? "Drives"
            : NameRules.Backwards(AppleMusic.DisplayPath(pane.CurrentPath));

        /// <summary>
        /// At Conner's request: "Explorer Native: Tate McRae, Artists, Music at
        /// D: Tab 2: Downloads, Conner, Users at C". The tab you are in first,
        /// its folder before its parents, so Alt+Tab and NVDA+T say where you
        /// are straight after the name; the other tab after, if anything was
        /// opened in it. The list has focus, not the window, so a change here
        /// is not read out on every folder.
        /// </summary>
        private void UpdateTitle()
        {
            static bool Open(Pane? p) => p != null && !string.IsNullOrEmpty(p.CurrentPath);
            int other = 1 - _activeIndex;
            var title = NameRules.WindowTitle(_settings.DisplayTitle,
                Open(Active) ? TitleLabelFor(Active) : null,
                other >= 0 && other < _panes.Length && Open(_panes[other]) ? TitleLabelFor(_panes[other]) : null,
                other + 1);
            if (Text != title) Text = title;
        }

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
        /// <returns>
        /// True only when the folder was read again and is what the pane now
        /// shows; false when nothing was reloaded, the reload fell back somewhere
        /// else, or a newer navigation took over. Callers announce on true only.
        /// </returns>
        private async Task<bool> RefreshAsync(string? selectAfter = null, string? folder = null, bool quiet = false)
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
                if (pane.SearchTerm == null || !IsUnder(folder, pane.CurrentPath)) return false;
            }

            if (pane.Searching) { pane.NeedsRefresh = true; return false; }

            if (pane.SearchTerm is { } term)
            {
                if (quiet) { pane.NeedsRefresh = true; return false; }
                try { await RunSearchAsync(term); }
                catch (Exception ex) { AnnounceOperation("search.failed", $"Could not search: {ex.Message}"); }
                return false;
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

            return await NavigateAsync(pane.CurrentPath, preferPath: selectAfter,
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
                    ScheduleSelectionAnnouncement();
                    break;

                // Space puts the row in or takes it out, with or without Ctrl,
                // and leaves the rest of the selection alone (Conner's request).
                // Ctrl+arrow moves without selecting, so: hold Ctrl, arrow, Space
                // on each one wanted, let go of Ctrl to hear how many. Left to
                // the control, a plain Space selected that row alone and dropped
                // everything else. Not while a name is being typed: "Program
                // Files" has a space in it.
                // Ctrl moves without selecting, and the control raises no focus
                // event for it: measured with NVDA's log, Ctrl+Down said nothing,
                // so you could not hear where you were while picking items out.
                // Said once the control has moved, with whether it is selected.
                case Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
                    when e.Control && !e.Shift && !e.Alt:
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || !pane.List.Focused) return;
                        int at = FocusedIndex(pane.List);
                        SpeakRow(pane, at, pane.List.SelectedIndices.Contains(at));
                    }));
                    break;

                case Keys.Space when !e.Alt && !e.Shift &&
                                     pane.TypeAhead.Current(Environment.TickCount64).Length == 0:
                    if (ToggleFocusedRow(pane) && e.Control) _selectedWithCtrl = true;
                    e.Handled = e.SuppressKeyPress = true;
                    break;
            }

            switch (e.KeyCode)
            {
                // Not with Ctrl: wrapping selects the row it lands on, which would
                // throw away the items picked out with Ctrl+arrow and Space.
                case Keys.Down or Keys.Up when count > 0 && !e.Shift && !e.Control:
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

        /// <summary>Space was pressed with Ctrl down since Ctrl went down; the total is said when it comes up.</summary>
        private bool _selectedWithCtrl;

        /// <summary>
        /// The focused row in the selection or out of it, the others as they
        /// were. Said as the row and which it is now; with Ctrl held, the total
        /// waits for Ctrl to be let go (<see cref="List_KeyUp"/>).
        /// </summary>
        private bool ToggleFocusedRow(Pane pane)
        {
            int index = FocusedIndex(pane.List);
            if (index < 0 || index >= pane.Entries.Count) return false;
            bool wasIn = pane.List.SelectedIndices.Contains(index);
            var lvi = new LVITEM { mask = LVIF_STATE, state = wasIn ? 0 : LVIS_SELECTED, stateMask = LVIS_SELECTED };
            SendMessage(pane.List.Handle, LVM_SETITEMSTATE, new IntPtr(index), ref lvi);
            ScheduleSelectionAnnouncement();
            return true;
        }

        private void List_KeyUp(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.ControlKey || !_selectedWithCtrl) return;
            _selectedWithCtrl = false;

            // Said after the last row's own "selected", not instead of it.
            if (_selectionSpeechTimer is { Enabled: true } pending)
            {
                pending.Stop();
                AnnounceSelectionChange();
            }
            int selected = Active.List.SelectedIndices.Count;
            AnnounceOperation("select.count", selected == 0 ? "Nothing selected" : $"{NameRules.Items(selected)} selected");
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
            // Each row's name was folded when the folder was listed, so a letter
            // that matches nothing in a hundred thousand rows is a comparison per
            // row rather than a fold per row.
            var rows = pane.Entries;
            bool extensions = _settings.ShowExtensions;
            int found = TypeAhead.FindFolded(rows.Count, i => rows[i].FoldedDisplayName(extensions), query, current);

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
            // Apple Music counts as an archive here: read-only, and nothing in it is on disk.
            bool inArchive = Active.Archive != null || AppleMusic.Is(Active.CurrentPath);
            bool looksLikeArchive = focused != null && !inArchive &&
                                    !IsDirectoryInView(focused) &&
                                    ArchiveFormats.FromName(focused) is { CanExtract: true };

            // Inside an archive, which is read-only: what would change it, and
            // what needs a real file on disk, can only refuse there.
            ShowArchiveHidden();

            // On an Apple Music song, Enter plays it, and the menu says so.
            bool appleSong = focused != null && AppleMusic.ItemAt(focused) is { IsSong: true };
            _contextOpenItem.Text = appleSong ? "&Play" : "&Open";
            _playNextItem.Visible = appleSong;
            var favorite = appleSong ? AppleMusic.IsFavorite(focused!) : null;
            _favoriteItem.Visible = favorite != null;
            _favoriteItem.Text = favorite == true ? "Unfa&vorite" : "Fa&vorite";
            // Not the playlist you are in: the song is already there.
            var here = AppleMusic.ItemAt(Active.CurrentPath);
            var playlists = appleSong
                ? AppleMusic.EditablePlaylists?.Where(p => !(here is { Kind: "playlist" } && here.Id == p.Id)).ToList()
                : null;
            // New playlist is always there for a song, so the submenu is too.
            _addToPlaylistItem.Visible = appleSong;
            if (appleSong)
            {
                foreach (ToolStripItem old in _addToPlaylistItem.DropDownItems.Cast<ToolStripItem>().ToList()) old.Dispose();
                _addToPlaylistItem.DropDownItems.Clear();
                _addToPlaylistItem.DropDownItems.Add(new ToolStripMenuItem("&New playlist…", null, (_, _) => NewPlaylistForFocusedAppleSong()));
                foreach (var playlist in playlists ?? new List<AppleMusicItem>())
                {
                    var chosen = playlist;
                    // "&" in a name is a letter, not an access key ("R&B" read as "RB").
                    _addToPlaylistItem.DropDownItems.Add(new ToolStripMenuItem(playlist.Name.Replace("&", "&&"), null,
                        (_, _) => AddFocusedAppleSongToPlaylist(chosen)));
                }
            }
            if (appleSong && playlists == null) AppleMusic.LoadPlaylistsSoon();   // ready by the next time

            _goToArtistItem.Visible = _goToAlbumItem.Visible = appleSong;
            var inLibrary = appleSong ? AppleMusic.InLibrary(focused!) : null;
            _libraryItem.Visible = inLibrary != null;
            _libraryItem.Text = inLibrary == true ? "Remove from librar&y" : "Add to librar&y";
            bool inApple = AppleMusic.Is(Active.CurrentPath);
            _appleShuffleItem.Visible = _appleRepeatItem.Visible = _appleUpNextItem.Visible = inApple;
            _appleUpNextItem.Visible = inApple && AppleMusicPlayer.IsActive;
            _appleShuffleItem.Checked = AppleMusicPlayer.Shuffle;
            var repeat = AppleMusicPlayer.Repeat;
            _appleRepeatItem.Text = repeat switch
            {
                AppleMusicPlayer.RepeatMode.All => "&Repeat: all",
                AppleMusicPlayer.RepeatMode.One => "&Repeat: this song",
                _ => "&Repeat: off",
            };

            _extractItem.Visible = looksLikeArchive;
            _extractHereItem.Visible = looksLikeArchive;

            _editTagsItem.Visible = focused != null && !inArchive &&
                                    !IsDirectoryInView(focused) &&
                                    AudioFiles.IsAudio(focused, _settings.AudioExtensions) &&
                                    TagEditing.RefusalFor(focused, IsOnDrive, null) == null;

            // The same rule for the audio commands: by name, no reads. Not on
            // the Drive letter or inside an archive, where nothing can be
            // written beside a file.
            bool local = !IsDrivesView && !inArchive &&
                         !(Drive != null && Drive.Owns(Active.CurrentPath));
            bool focusedAudio = focused != null && !IsDirectoryInView(focused) &&
                                AudioFiles.IsAudio(focused, _settings.AudioExtensions);
            _editAudioItem.Visible = local && focusedAudio;
            _convertAudioItem.Visible = local && focused != null &&
                SelectedPaths().Any(p => IsDirectoryInView(p) || AudioFiles.IsAudio(p, _settings.AudioExtensions));

            ShowDriveLinkItems();
            ShowDriveOnlyItems(focused, inArchive);

            // A shortcut is a file, and a file written into the Drive letter by
            // hand is invisible and then gone — the sync root is built from the
            // account's listing and cleared by the next mount, which is the fault
            // New file and New folder each had and each had fixed. There is no
            // way to put a `.lnk` into Drive short of uploading one, so in a
            // Drive folder this command is one that could only ever refuse, and
            // it is left off rather than read out.
            _shortcutItem.Visible =
                focused != null && !IsDrivesView && !inArchive &&
                !(Drive != null && Drive.Owns(Active.CurrentPath));

            _formatItem.Visible = IsDrivesView && FormattableDrive(focused) != null;
            // Google Drive's trash, by name and the mount's own map: no reads.
            // Directly in the Trash folder, Restore and Delete permanently take
            // the place of everything that could only refuse there; inside a
            // trashed folder only what reads is left. Empty is offered in the
            // Trash folder, inside it, and on the Trash folder itself.
            var drive = Drive;
            bool trashHere = drive != null && !IsDrivesView &&
                             (drive.IsTrashFolder(Active.CurrentPath) || drive.IsInTrash(Active.CurrentPath));
            bool topOfTrash = trashHere && drive!.IsTrashFolder(Active.CurrentPath);
            bool onTrashFolder = drive != null && focused != null && drive.IsTrashFolder(focused);
            _restoreItem.Visible = topOfTrash && focused != null;
            _deleteForeverItem.Visible = topOfTrash && focused != null;
            _emptyTrashItem.Visible = trashHere || onTrashFolder;
            if (trashHere || onTrashFolder)
                foreach (var item in new[] { _cutItem, _pasteItem, _renameItem, _deleteItem, _compressItem })
                    item.Visible = false;

            _contextMenu.Show(where);
        }

        private ToolStripMenuItem _formatItem = null!;
        private ToolStripMenuItem _contextOpenItem = null!;

        /// <summary>
        /// The audio files of a folder in the order a pane is showing them, for
        /// next and previous track; null when no pane shows that folder (the
        /// caller then reads it off the UI thread). No reads: the rows on screen.
        /// </summary>
        public List<string>? AudioFilesShown(string folder)
        {
            folder = folder.TrimEnd('\\');
            foreach (var pane in _panes)
            {
                if (pane.Archive != null || pane.SearchTerm != null || string.IsNullOrEmpty(pane.CurrentPath) ||
                    AppleMusic.Is(pane.CurrentPath) || pane.CurrentPath == DrivesPath) continue;
                if (!string.Equals(pane.CurrentPath.TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase)) continue;
                return pane.Entries.Where(e => !e.IsDir && AudioFiles.IsAudio(e.Path, _settings.AudioExtensions))
                    .Select(e => e.Path).ToList();
            }
            return null;
        }
        private ToolStripMenuItem _playNextItem = null!;
        private ToolStripMenuItem _favoriteItem = null!;
        private ToolStripMenuItem _addToPlaylistItem = null!;
        private ToolStripMenuItem _goToArtistItem = null!;
        private ToolStripMenuItem _goToAlbumItem = null!;
        private ToolStripMenuItem _libraryItem = null!;
        private ToolStripMenuItem _appleShuffleItem = null!;
        private ToolStripMenuItem _appleRepeatItem = null!;
        private ToolStripMenuItem _appleUpNextItem = null!;

        /// <summary>Go to album or Go to artist on the focused Apple Music song: asks Apple which, then opens it.</summary>
        private async void GoToFocusedApple(bool album)
        {
            try
            {
                if (FocusedPath() is not { } path || AppleMusic.ItemAt(path) is not { IsSong: true }) return;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var folder = await AppleMusic.GoToAsync(path, album, cts.Token);
                if (!IsDisposed) Navigate(folder);
            }
            catch (AppleMusicException ex) { AnnounceAfterMenu("applemusic.failed", ex.Message); }
            catch (OperationCanceledException) { AnnounceAfterMenu("applemusic.failed", "Apple Music took too long to answer."); }
            catch (Exception ex) { AnnounceAfterMenu("applemusic.failed", "Apple Music couldn't open it: " + ex.Message); }
        }

        /// <summary>Add to library on a song from Apple's catalogue, Remove from library on one of yours.</summary>
        private async void ToggleFocusedAppleLibrary()
        {
            try
            {
                if (FocusedPath() is not { } path || AppleMusic.InLibrary(path) is not { } mine) return;
                if (mine && MessageBox.Show(this, $"Remove {AppleMusic.ItemAt(path)?.Name} from your Apple Music library? It comes out of your playlists too.",
                        "Remove from library", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    RestoreListFocus();
                    return;
                }
                if (mine) RestoreListFocus();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var said = mine ? await AppleMusic.RemoveFromLibraryAsync(path, cts.Token) : await AppleMusic.AddToLibraryAsync(path, cts.Token);
                AnnounceAfterMenu("applemusic.library", said);
            }
            catch (AppleMusicException ex) { AnnounceAfterMenu("applemusic.failed", ex.Message); }
            catch (OperationCanceledException) { AnnounceAfterMenu("applemusic.failed", "Apple Music took too long to answer."); }
            catch (Exception ex) { AnnounceAfterMenu("applemusic.failed", "Apple Music couldn't change your library: " + ex.Message); }
        }

        /// <summary>Add to playlist, New playlist: asks for a name, then makes it with the song in it.</summary>
        private async void NewPlaylistForFocusedAppleSong()
        {
            try
            {
                if (FocusedPath() is not { } path || AppleMusic.ItemAt(path) is not { IsSong: true }) return;
                var name = PromptForm.Ask(this, "New playlist", "Playlist name:");
                RestoreListFocus();
                if (string.IsNullOrWhiteSpace(name)) return;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var said = await AppleMusic.CreatePlaylistAsync(path, name, cts.Token);
                AnnounceAfterMenu("applemusic.playlist", said);
            }
            catch (AppleMusicException ex) { AnnounceAfterMenu("applemusic.failed", ex.Message); }
            catch (OperationCanceledException) { AnnounceAfterMenu("applemusic.failed", "Apple Music took too long to answer."); }
            catch (Exception ex) { AnnounceAfterMenu("applemusic.failed", "Apple Music couldn't make the playlist: " + ex.Message); }
        }
        private ToolStripMenuItem _keepOnDriveItem = null!;
        private ToolStripMenuItem _putBackItem = null!;

        /// <summary>
        /// The synced folder a focused folder belongs to, and its path inside
        /// it, from the PC side or the Drive letter. Path comparisons and the
        /// mount's own id map only: this runs while the menu is drawn.
        /// </summary>
        private (DriveSyncPair Pair, string Relative)? SyncedFolderOf(string path)
        {
            var monitor = DriveMonitor.Current;
            if (monitor == null) return null;
            var pairs = monitor.Pairs;
            if (pairs.Count == 0) return null;
            var drive = Drive;
            if (drive != null && drive.Owns(path))
                return DriveOnlyFolders.OnDrive(path, pairs, drive.Letter, p => drive.KnownIdOf(p));
            return DriveOnlyFolders.OnPc(path, pairs);
        }

        /// <summary>
        /// Keep on Drive only over a folder inside a synced folder on the PC;
        /// Put back on PC over one that is kept on Drive only, from the Drive
        /// letter (where it still is) or the PC (where something was left).
        /// </summary>
        private void ShowDriveOnlyItems(string? focused, bool inArchive)
        {
            _keepOnDriveItem.Visible = _putBackItem.Visible = false;
            if (focused == null || inArchive || IsDrivesView || !IsDirectoryInView(focused)) return;
            if (SyncedFolderOf(focused) is not { } synced) return;
            bool kept = DriveOnlyFolders.Covers(synced.Pair.DriveOnlyFolders, synced.Relative);
            bool onDrive = Drive != null && Drive.Owns(focused);
            _putBackItem.Visible = kept;
            _keepOnDriveItem.Visible = !kept && !onDrive && DriveOnlyFolders.Offered(synced.Pair);
        }

        /// <summary>One Keep on Drive only or Put back on PC at a time.</summary>
        private bool _driveOnlyBusy;

        private async void KeepFocusedOnDriveOnly()
        {
            // Checked outside the try: a second press used to clear the first one's flag in its finally.
            if (_driveOnlyBusy)
            {
                Announce("Still working on the last one. Try again when it has finished");
                return;
            }
            bool mine = false;
            try
            {
                if (FocusedPath() is not { } path || DriveMonitor.Current is not { } monitor) return;
                if (SyncedFolderOf(path) is not { } synced) return;
                var name = Path.GetFileName(path.TrimEnd('\\'));
                if (MessageBox.Show(this,
                        $"Keep {name} on Google Drive only? Every file in it is checked against Google Drive first. " +
                        $"Then it is removed from this PC, and stays in Google Drive. The rest of {DriveMonitor.DisplayName(synced.Pair)} " +
                        "keeps syncing both ways. Put back on PC brings it back.",
                        "Keep on Drive only", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    RestoreListFocus();
                    return;
                }
                _driveOnlyBusy = mine = true;
                RestoreListFocus();
                AnnounceOperation("sync.driveonly", $"Checking {name} against Google Drive");
                var origin = Active.CurrentPath;
                var landOn = RowAfterRemoving(new[] { path });
                DriveMonitor.DriveOnlyOutcome outcome;
                RunningTransfers++;
                try { outcome = await monitor.KeepOnDriveOnlyAsync(synced.Pair.Id, synced.Relative, CancellationToken.None); }
                finally { RunningTransfers--; }
                if (IsDisposed) return;
                AnnounceOperation("sync.driveonly", outcome.Message);
                if (outcome.Done) await RefreshAsync(landOn, folder: origin);
            }
            catch (Exception ex)
            {
                if (!IsDisposed) Announce("Could not keep it on Google Drive only: " + ex.Message, isError: true);
            }
            finally { if (mine) _driveOnlyBusy = false; }
        }

        private async void PutFocusedBackOnPc()
        {
            // Checked outside the try: a second press used to clear the first one's flag in its finally.
            if (_driveOnlyBusy)
            {
                Announce("Still working on the last one. Try again when it has finished");
                return;
            }
            bool mine = false;
            try
            {
                if (FocusedPath() is not { } path || DriveMonitor.Current is not { } monitor) return;
                if (SyncedFolderOf(path) is not { } synced) return;
                _driveOnlyBusy = mine = true;
                var outcome = await monitor.PutBackOnPcAsync(synced.Pair.Id, synced.Relative, CancellationToken.None);
                if (IsDisposed) return;
                AnnounceOperation("sync.putback", outcome.Message);
            }
            catch (Exception ex)
            {
                if (!IsDisposed) Announce("Could not put it back on this PC: " + ex.Message, isError: true);
            }
            finally { if (mine) _driveOnlyBusy = false; }
        }

        private void PlayFocusedAppleSongNext()
        {
            if (FocusedPath() is { } path && AppleMusic.ItemAt(path) is { IsSong: true })
                Program.PlayAppleMusicNext(path);
        }

        /// <summary>Favorite or Unfavorite on the focused Apple Music song: changed at Apple, then said, and the row renamed quietly.</summary>
        private async void ToggleFocusedAppleFavorite()
        {
            string? busy = null;
            try
            {
                if (FocusedPath() is not { } path || AppleMusic.IsFavorite(path) is not { } now) return;
                // A second press while the first is on its way would send the same
                // change again and say it twice.
                if (!_favoriteBusy.Add(path)) return;
                busy = path;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var said = await AppleMusic.SetFavoriteAsync(path, !now, cts.Token);
                AnnounceAfterMenu("applemusic.favorite", said);
            }
            catch (AppleMusicException ex) { AnnounceAfterMenu("applemusic.failed", ex.Message); }
            catch (OperationCanceledException) { AnnounceAfterMenu("applemusic.failed", "Apple Music took too long to answer."); }
            catch (Exception ex) { AnnounceAfterMenu("applemusic.failed", "Apple Music couldn't change that: " + ex.Message); }
            finally { if (busy != null) _favoriteBusy.Remove(busy); }
        }

        private readonly HashSet<string> _favoriteBusy = new(StringComparer.OrdinalIgnoreCase);

        private async void AddFocusedAppleSongToPlaylist(AppleMusicItem playlist)
        {
            try
            {
                if (FocusedPath() is not { } path) return;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var said = await AppleMusic.AddToPlaylistAsync(path, playlist, cts.Token);
                AnnounceAfterMenu("applemusic.playlist", said);
            }
            catch (AppleMusicException ex) { AnnounceAfterMenu("applemusic.failed", ex.Message); }
            catch (OperationCanceledException) { AnnounceAfterMenu("applemusic.failed", "Apple Music took too long to answer."); }
            catch (Exception ex) { AnnounceAfterMenu("applemusic.failed", "Apple Music couldn't add it: " + ex.Message); }
        }

        /// <summary>
        /// The drive letter a drive row could be formatted as, by name alone: a
        /// root such as "E:\", not the Windows drive and not the Google Drive
        /// letter (a folder on C: wearing a letter). Null otherwise.
        /// </summary>
        private string? FormattableDrive(string? path)
        {
            if (path == null || path.Length != 3 || path[1] != ':' || path[2] != Path.DirectorySeparatorChar) return null;
            char letter = char.ToUpperInvariant(path[0]);
            if (letter is < 'A' or > 'Z') return null;
            var system = Path.GetPathRoot(Environment.SystemDirectory);
            if (system != null && char.ToUpperInvariant(system[0]) == letter) return null;
            if (Drive?.Letter is { Length: > 0 } mounted && char.ToUpperInvariant(mounted[0]) == letter) return null;
            return letter + ":";
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern uint SHFormatDrive(IntPtr hwnd, uint drive, uint fmtID, uint options);

        /// <summary>
        /// Opens Windows' Format window for the focused drive. It asks, warns
        /// and formats by itself; this only says how it ended and lists the
        /// drives again, since the label and size may have changed.
        /// </summary>
        private void FormatFocusedDrive()
        {
            try
            {
                if (FormattableDrive(FocusedPath()) is not { } letter) return;
                const uint DefaultFormat = 0xFFFF;
                uint result = SHFormatDrive(Handle, (uint)(letter[0] - 'A'), DefaultFormat, 0);
                ShowStatus(result switch
                {
                    0xFFFFFFFE => $"Format of {letter} cancelled",
                    0xFFFFFFFD => $"{letter} cannot be formatted",
                    0xFFFFFFFF => $"{letter} was not formatted",
                    _ => $"{letter} formatted",
                });
                RestoreListFocus();
                if (IsDrivesView) _ = NavigateAsync(DrivesPath, preferPath: letter + Path.DirectorySeparatorChar);
            }
            catch (Exception ex)
            {
                if (!IsDisposed) Announce("Could not open Format: " + ex.Message, isError: true);
            }
        }

        private ToolStripMenuItem _cutItem = null!, _pasteItem = null!, _renameItem = null!, _deleteItem = null!,
            _compressItem = null!, _restoreItem = null!, _deleteForeverItem = null!, _emptyTrashItem = null!;

        /// <summary>
        /// Puts what is selected in Google Drive's Trash folder back where it was.
        /// Async void like every menu command, and guarded the same way.
        /// </summary>
        private async void RestoreFromDriveTrash()
        {
            try
            {
                if (Drive is not { } drive) return;
                var paths = SelectedPaths();
                if (paths.Length == 0 || !paths.All(drive.IsInTrash)) return;
                var outcome = await EachInDriveTrash(paths, p => drive.RestoreFromTrash(p));
                if (IsDisposed) return;
                if (outcome.Done > 1) AnnounceOperation("drive.restored", $"Restored {NameRules.Items(outcome.Done)}");
                if (outcome.Errors.Count > 0)
                {
                    AnnounceOperation("drive.restore.failed", outcome.Errors.Count == 1
                        ? $"Could not restore {outcome.Errors[0]}"
                        : $"Could not restore {outcome.Errors.Count:N0} items");
                    ShowErrors(outcome.Errors);
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed) Announce("Could not restore: " + ex.Message, isError: true);
            }
        }

        /// <summary>
        /// Deletes what is selected in Google Drive's Trash folder for good, after
        /// asking every time, whatever "Confirm before deleting" says: this is the
        /// one delete in the application nothing can undo. No is the default.
        /// </summary>
        private async void DeleteFromDriveTrash()
        {
            try
            {
                if (Drive is not { } drive) return;
                var paths = SelectedPaths();
                if (paths.Length == 0 || !paths.All(drive.IsInTrash)) return;
                var what = paths.Length == 1 ? $"\"{Path.GetFileName(paths[0].TrimEnd('\\'))}\"" : $"{paths.Length:N0} items";
                if (MessageBox.Show(this, $"Permanently delete {what} from Google Drive? This cannot be undone.",
                        "Delete permanently", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    AnnounceOperation("delete.cancelled", "Delete cancelled");
                    RestoreListFocus();
                    return;
                }
                var outcome = await EachInDriveTrash(paths, p => drive.DeleteForever(p));
                if (IsDisposed) return;
                if (outcome.Done > 1) AnnounceOperation("drive.deleted", $"Deleted {NameRules.Items(outcome.Done)} permanently");
                if (outcome.Errors.Count > 0)
                {
                    AnnounceOperation("delete.failed", outcome.Errors.Count == 1
                        ? $"Could not delete {outcome.Errors[0]}"
                        : $"Could not delete {outcome.Errors.Count:N0} items");
                    ShowErrors(outcome.Errors);
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed) Announce("Could not delete: " + ex.Message, isError: true);
            }
        }

        /// <summary>Empties Google Drive's trash for good, after asking. No is the default.</summary>
        private async void EmptyDriveTrash()
        {
            try
            {
                if (Drive is not { } drive) return;
                if (MessageBox.Show(this,
                        "Permanently delete everything in the Google Drive trash? This cannot be undone.",
                        "Empty Google Drive trash", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    AnnounceOperation("delete.cancelled", "Emptying the trash cancelled");
                    RestoreListFocus();
                    return;
                }

                // Inside a trashed folder, which is about to be gone: back up to
                // the Trash folder itself.
                var origin = Active.CurrentPath;
                var land = origin;
                while (drive.IsInTrash(land) && SafeParent(land) is { } up) land = up;

                RunningTransfers++;
                try { await drive.EmptyTrash(); }
                catch (Exception) { /* said by the Drive layer */ }
                finally { RunningTransfers--; }
                if (IsDisposed) return;
                if (drive.IsTrashFolder(land)) await NavigateAsync(land);
                else await RefreshAsync(null, folder: origin);
            }
            catch (Exception ex)
            {
                if (!IsDisposed) Announce("Could not empty the Google Drive trash: " + ex.Message, isError: true);
            }
        }

        /// <summary>
        /// One trash command over a selection, one item at a time, then the folder
        /// listed again on the row after them. Each item names itself as it goes
        /// (the Drive layer says it); what failed is collected, by name.
        /// </summary>
        private async Task<(int Done, List<string> Errors)> EachInDriveTrash(string[] paths, Func<string, Task<bool>> one)
        {
            var origin = Active.CurrentPath;
            var landOn = RowAfterRemoving(paths);
            int done = 0;
            var errors = new List<string>();
            RunningTransfers++;
            BeginTransferTo(origin);
            try
            {
                foreach (var path in paths)
                {
                    var name = Path.GetFileName(path.TrimEnd('\\'));
                    try
                    {
                        if (await one(path)) done++;
                        else errors.Add(name);
                    }
                    catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
                }
            }
            finally
            {
                RunningTransfers--;
                EndTransferTo(origin);
            }
            if (!IsDisposed) await RefreshAsync(landOn, folder: origin);
            return (done, errors);
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
                    // Not inside an archive, where the path is not a file yet.
                    var path = FocusedPath();
                    if (path != null && Active.Archive == null && !AppleMusic.Is(path)) Program.PrefetchAudioFile(path);
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
            ScheduleDetails();
        }

        /// <summary>How long the cursor rests on a file before its details are said.</summary>
        private const int DetailsSettleMilliseconds = 1000;

        private System.Windows.Forms.Timer? _detailsTimer;
        private int _detailsGeneration;

        /// <summary>
        /// Words and characters, or bitrate, sample rate and length, said once
        /// the cursor has stopped on a file. Restarted on every move, so arrowing
        /// through a folder reads nothing; worked out on a worker, and dropped if
        /// the cursor has moved by the time it is ready. Queued behind the row,
        /// never interrupting it.
        /// </summary>
        private void ScheduleDetails()
        {
            _detailsGeneration++;
            if (_suppressStatus > 0) return;
            if (Notifications.ChannelsFor("nav.details", _settings) != NotificationChannel.Speech) return;

            if (_detailsTimer == null)
            {
                _detailsTimer = new System.Windows.Forms.Timer { Interval = DetailsSettleMilliseconds };
                _detailsTimer.Tick += async (_, _) =>
                {
                    _detailsTimer!.Stop();
                    try { await SayDetailsAsync(); }
                    catch { }
                };
            }

            _detailsTimer.Stop();
            _detailsTimer.Start();
        }

        private async Task SayDetailsAsync()
        {
            if (IsDisposed || !_settings.SpeakEnabled) return;
            var pane = Active;
            if (!pane.List.Focused || pane.List.SelectedIndices.Count > 1) return;

            int index = CurrentIndex(pane);
            if (index < 0 || index >= pane.Entries.Count) return;
            var entry = pane.Entries[index];
            if (entry.IsDir) return;

            var path = entry.Path;
            // Reading a placeholder downloads it.
            if (Drive is { } drive && drive.Owns(path)) return;

            // A member of an archive is not on disk to describe, nor is Apple Music.
            if (pane.Archive != null || AppleMusic.Is(path)) return;

            int generation = _detailsGeneration;
            var audio = _settings.AudioExtensions;
            var details = await Task.Run(() => FileDetails.Describe(path, audio));

            if (details == null || IsDisposed || generation != _detailsGeneration) return;
            if (!string.Equals(FocusedPath(), path, StringComparison.OrdinalIgnoreCase)) return;

            ShowStatus(details);
            Speech.Speak(details, interrupt: false);
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

            // The count only when it adds something: "song.flac selected, 1
            // selected" said the same thing twice. Nor while Ctrl is held: the
            // total is said once, when it is let go (List_KeyUp).
            AnnounceOperation("select.count",
                (isIn && selected == 1) || (ModifierKeys & Keys.Control) != 0
                    ? $"{(isIn ? "Selected" : "Not selected")} {name}"
                : selected == 0 ? $"Not selected {name}"
                : $"{(isIn ? "Selected" : "Not selected")} {name}, {selected:N0} selected in all");
        }

        private void List_ColumnClick(object? sender, ColumnClickEventArgs e)
        {
            // Apple Music has its own orders (File, Sort), not the file columns.
            if (AppleMusic.Is(Active.CurrentPath)) return;

            var header = Active.List.Columns[e.Column].Text;
            var col = header switch
            {
                "Type" => SortColumn.Type,
                "Size" => SortColumn.Size,
                "Modified" => SortColumn.Modified,
                _ => SortColumn.Name,
            };

            // A folder with its own order keeps it, changed by the click.
            if (FolderSortOf(Active.CurrentPath) is { } own)
            {
                SetFolderSort(Active.CurrentPath, own.By == col ? (col, !own.Ascending) : (col, StartsAscending(col)));
                return;
            }

            if (_settings.SortBy == col) _settings.SortAscending = !_settings.SortAscending;
            else { _settings.SortBy = col; _settings.SortAscending = StartsAscending(col); }

            _settings.Save();
            ResortActive();
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

        private const int LVM_GETSELECTEDCOUNT = LVM_FIRST + 50;

        /// <summary>How many rows are selected, as one message.</summary>
        private static int SelectedCountOf(ListView list) =>
            list.IsHandleCreated ? (int)SendMessage(list.Handle, LVM_GETSELECTEDCOUNT, IntPtr.Zero, IntPtr.Zero) : 0;

        /// <summary>
        /// Moves the focus to one row and leaves the selection alone: the
        /// focus taken off every row, then put on this one, which is the
        /// change a screen reader hears.
        /// </summary>
        private void RefocusIndex(Pane pane, int index)
        {
            if (index < 0 || index >= pane.Entries.Count || !pane.List.IsHandleCreated) return;
            var list = pane.List;

            _suppressStatus++;
            try
            {
                var clear = new LVITEM { mask = LVIF_STATE, state = 0, stateMask = LVIS_FOCUSED };
                SendMessage(list.Handle, LVM_SETITEMSTATE, new IntPtr(-1), ref clear);
                var set = new LVITEM { mask = LVIF_STATE, state = LVIS_FOCUSED, stateMask = LVIS_FOCUSED };
                SendMessage(list.Handle, LVM_SETITEMSTATE, new IntPtr(index), ref set);
                try { list.EnsureVisible(index); } catch (ArgumentOutOfRangeException) { }
            }
            finally { _suppressStatus--; }

            ScheduleStatusUpdate();
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

            // A command chosen from a menu (Go, Up one level) changes folder just
            // after the menu closes, when the list counts as focused again but
            // NVDA has yet to read it: held back then, the list was read out by
            // the folder just left ("C:\ list" with the drives on screen). While
            // a menu is open and just after, the name goes on at once.
            bool menuMoment = _menuOpen || Environment.TickCount64 - _menuClosedAt < MenuNameWindowMs;
            if (pane.List.Focused && !menuMoment) { pane.PendingName = name; return; }

            pane.PendingName = null;
            pane.List.AccessibleName = name;
        }

        /// <summary>Whether one of the menus (the menu bar or Shift+F10) is open, and when one last closed.</summary>
        private static bool _menuOpen;
        private static long _menuClosedAt = -MenuNameWindowMs;
        private const int MenuNameWindowMs = 3000;

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

            // Re-applied for the event that names the row. With several rows
            // selected only the focus is re-applied: SelectIndex clears the
            // rest, so a transfer finishing while the keyboard was in its
            // window cut a twelve-file selection down to the focused row.
            if (!alreadyFocused)
            {
                if (SelectedCountOf(list) > 1) RefocusIndex(pane, index);
                else SelectIndex(pane, index);
            }

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
        /// <param name="selected">Said after the name, for Ctrl+arrow, where the screen reader says nothing.</param>
        private void SpeakRow(Pane pane, int index, bool selected = false)
        {
            if (!_settings.SpeakEnabled) return;
            if (index < 0 || index >= pane.Entries.Count) return;

            var entry = pane.Entries[index];
            var parts = new List<string>(4) { DisplayNameOf(entry) };
            if (selected) parts.Add("selected");

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

            // Apple Music is its own place, not files on disk: a folder opens, the
            // Search row asks what to look for, and a song plays in Apple's own
            // player. It must never reach the file player below, which would hand
            // the made-up "...\\Song.m4a" path to whatever owns .m4a and say it
            // could not play it.
            if (AppleMusic.Is(entry.Path))
            {
                OpenAppleMusicRow(entry);
                return;
            }

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

            // Inside an archive the file is not on disk yet: it is taken out
            // first, then played or opened as any other file would be.
            if (Active.Archive is { } archive)
            {
                OpenArchiveMember(archive, path, name, openExternally);
                return;
            }

            // An archive opens as a folder. Shift+Enter is still the way to its
            // own app, as for any file. Not on the Google Drive letter, which
            // this process must never read from (see CLAUDE.md); there Enter
            // opens it in its own app as before.
            if (!openExternally && ArchiveBrowser.CanBrowse(path) &&
                !(Drive != null && Drive.Owns(path)))
            {
                OpenArchive(path);
                return;
            }

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

            AnnounceOperation("file.opening", $"Opening {name}");

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

        /// <summary>
        /// For the commands that need a real file (copy, copy path, properties,
        /// the Windows menu, folder size): says that Apple Music is only browsed
        /// and played here and returns true, in an Apple Music list or on its row
        /// in the drives list. Without it they put "::applemusic" paths on the
        /// clipboard or asked the disk about them.
        /// </summary>
        private bool RefuseForAppleMusic(params string?[] paths)
        {
            if (!AppleMusic.Is(Active.CurrentPath) && !paths.Any(AppleMusic.Is)) return false;
            AnnounceOperation("applemusic.refused", "Apple Music can only be browsed and played here");
            return true;
        }

        private void CopyPath()
        {
            var path = FocusedPath();
            if (path == null) { AnnounceOperation("clip.nothing", "Nothing selected"); return; }
            if (RefuseForAppleMusic(path)) return;

            if (!ClipboardInterop.SetText(path))
            {
                Announce("Could not take the clipboard, another application is holding it", isError: true);
                return;
            }
            AnnounceOperation("clip.path", "Copied the path");
        }

        private void CopyToClipboard(bool cut)
        {
            // Cutting out of an archive would delete from it on the paste.
            if (cut && RefuseInArchive()) return;

            var paths = SelectedPaths();
            if (paths.Length == 0) { AnnounceOperation("clip.nothing", "Nothing selected"); return; }
            if (RefuseForAppleMusic(paths)) return;

            // Members of an archive are not files yet: they are taken out first,
            // and what came out goes on the clipboard.
            if (Active.Archive is { } archive)
            {
                CopyOutOfArchive(archive, paths);
                return;
            }

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
            if (RefuseInArchive()) return;

            if (!ClipboardInterop.TryGetFiles(out var paths, out bool isCut))
            {
                AnnounceOperation("clip.empty", "There are no files on the clipboard");
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

            var cts = new CancellationTokenSource();   // cancelled, never disposed: a worker may still hold its token
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
                    elapsed, PerItemWork: DriveUpload.PerFileWork);

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

            GoogleDrive.Trace($"window paste: upload finished {(DateTime.UtcNow - started).TotalMilliseconds:F0}ms after start");
            var refreshing = System.Diagnostics.Stopwatch.StartNew();
            await RefreshDestination(destination);
            GoogleDrive.Trace($"window paste: refresh afterwards {refreshing.ElapsedMilliseconds}ms");

            int done = upload.FilesUploaded;
            var errors = upload.Errors;
            int total = work.Files.Count;
            // "3 of 12 files", never "1 of 1".
            string soFar = $"{done:N0} of {total:N0} {(total == 1 ? "file" : "files")}";

            // A cancelled upload is not a failed one, for the same reason a
            // cancelled copy is not: silencing a deliberate Escape must not mean
            // silencing genuine failures with it.
            if (cancelled)
            {
                AnnounceOperation(move ? "move.cancelled" : "copy.cancelled",
                    total <= 1 ? "Upload cancelled" : $"Upload cancelled after {soFar}");
                return false;
            }

            // Something stopped the whole run rather than one file. Said first,
            // because it is the reason the count is what it is — and because
            // without it an upload that fell over on its first folder reported
            // "Uploaded 0 files to Google Drive", which reads as success.
            if (failure != null)
            {
                AnnounceOperation("drive.upload.failed",
                    total <= 1 ? $"Could not upload: {failure}" : $"Upload stopped after {soFar}: {failure}");
                ShowErrors(errors.Count > 0 ? errors : new List<string> { failure });
                AnnounceConflicts(upload.Collisions);
                return false;
            }

            if (errors.Count > 0)
            {
                AnnounceOperation("drive.upload.failed",
                    total <= 1 ? $"Could not upload: {errors[0]}"
                    : errors.Count == 1 ? $"Uploaded {soFar}. One could not be uploaded: {errors[0]}"
                    : $"Uploaded {soFar}. The first problem: {errors[0]}");
                ShowErrors(errors);
                AnnounceConflicts(upload.Collisions);
                return false;
            }

            // One file is just the verb, as everywhere (NameRules.SayCount).
            AnnounceOperation("drive.upload.done", work.Folders.Count > 0
                ? $"Uploaded {NameRules.Items(done, "file")} in " +
                  $"{(work.Folders.Count == 1 ? "one folder" : $"{work.Folders.Count:N0} folders")} to Google Drive"
                : done == 1 ? "Uploaded to Google Drive"
                : $"Uploaded {done:N0} files to Google Drive");

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

            var cts = new CancellationTokenSource();   // cancelled, never disposed: a worker may still hold its token
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
                        // On a worker: its first pass reads the listings it
                        // already holds synchronously, before its first await.
                        var (files, bytes) = await Task.Run(() => counting.MeasureOnDrive(paths,
                            name => { current = $"Counting: {name}"; Show(); }, cts.Token));
                        filesTotal = Math.Max(files, 1);
                        bytesTotal = bytes;
                        current = "";
                        Show();
                    }
                    done = await OnDrive(paths, destination, conflicts, move, log, errors, cts.Token, Tick,
                        report: report);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { cancelled = true; }
                catch (Exception ex)
                {
                    // Anything else is this transfer failing, said as one. It used
                    // to escape, skipping the refresh, leaving the window open and
                    // counted, and the paste saying it could not even start.
                    errors.Add(ex.Message);
                }
            }
            finally
            {
                window.Finish();
                RunningTransfers--;
                EndTransferTo(destination);
                foreach (var origin in origins) EndTransferTo(origin);
            }

            foreach (var origin in origins) await RefreshDestination(origin);
            await RefreshDestination(destination);

            var outcomes = log.Snapshot();
            if (cancelled)
            {
                AnnounceOperation(move ? "move.cancelled" : "copy.cancelled", move ? "Move cancelled" : "Copy cancelled");
                AnnounceConflicts(outcomes);
                return false;
            }

            if (errors.Count > 0)
            {
                AnnounceOperation(move ? "move.failed" : "copy.failed",
                    done == 0 ? $"Could not {(move ? "move" : "copy")}: {errors[0]}"
                    : $"{(move ? "Moved" : "Copied")} {NameRules.Items(done)}, but not everything: {errors[0]}");
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
            // Patient: what it says decides what is replaced or merged.
            drive.Populate(folder, interactive: false);
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
                    var target = NameRules.ExactPath(Path.Combine(destination, name));
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
            // Cut and paste into the folder the items are already in is nothing
            // to do. It was a collision of each item with itself, so under keep
            // both everything came back renamed "a (2)" and the clipboard was
            // cleared. A string comparison, so it costs nothing on a share; the
            // engines refuse it as well. The clipboard keeps the cut, as File
            // Explorer's does.
            if (move)
            {
                var elsewhere = paths.Where(p =>
                {
                    var name = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    return string.IsNullOrEmpty(name) || !FileOperations.SamePath(Path.Combine(destination, name), p);
                }).ToArray();

                if (elsewhere.Length == 0)
                {
                    AnnounceOperation("move.done", paths.Length == 1
                        ? "Already in this folder"
                        : "Already in this folder; nothing to move");
                    return false;
                }
                paths = elsewhere;
            }

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

            var asking = System.Diagnostics.Stopwatch.StartNew();
            var chosen = await ResolveConflictPolicy(paths, destination);
            if (Drive != null && Drive.Owns(destination))
                GoogleDrive.Trace($"window paste: conflict check {asking.ElapsedMilliseconds}ms (includes any question asked)");
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

            var cts = new CancellationTokenSource();   // cancelled, never disposed: a worker may still hold its token
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
            // Refreshing while it is still raised is deliberate and safe: the
            // hold is read only by the watcher's own tick, never by an explicit
            // reload, so the refreshes below all happen normally.
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
                // Both asked on the worker: the destination can be a share, and
                // the folder check used to run per name on the UI thread.
                HashSet<string>? alreadyThere = null, merged = null;
                if (move && fromDrive)
                {
                    bool fillGaps = conflicts == PasteConflictPolicy.FillGaps;
                    (alreadyThere, merged) = await Task.Run(() =>
                    {
                        var there = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var p in paths)
                        {
                            var n = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar));
                            var at = NameRules.ExactPath(Path.Combine(destination, n));
                            bool isFolder = Directory.Exists(at);
                            if (isFolder || File.Exists(at)) there.Add(n);
                            if (isFolder) folders.Add(n);
                        }
                        return (there, fillGaps ? folders : null);
                    });
                }
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
                    cloudSource: fromDrive,
                    // A move within one volume is a rename now, and never to or
                    // from the Drive letter, where a rename is not a move in
                    // the account.
                    onCloudLetter: Drive == null ? null : Drive.Owns);

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
                    AnnounceOperation(move ? "move.failed" : "copy.failed", $"Could not {(move ? "move" : "copy")}: {ex.Message}");

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
                if (result.Cancelled) AnnounceOperation(move ? "move.cancelled" : "copy.cancelled", move ? "Move cancelled" : "Copy cancelled");
                else if (result.Failed > 0)
                {
                    // Copied counts files; Failed counts the items that could not
                    // be processed. Said separately because they are different
                    // units — a single failed folder can be thousands of files.
                    // One id for both verbs: a move that half succeeded is the
                    // same event as a copy that half succeeded, and the sentence
                    // already says which it was.
                    AnnounceOperation("copy.partial",
                        result.Copied == 0
                            ? $"Nothing was {(move ? "moved" : "copied")}: {NameRules.Items(result.Failed)} failed"
                            : $"{(move ? "Moved" : "Copied")} {NameRules.Items(result.Copied, "file")}, but " +
                              $"{NameRules.Items(result.Failed)} could not be {(move ? "moved" : "copied")}");
                    ShowErrors(result.Errors);
                }
                // Not "Move complete" over the warnings that some originals stayed.
                else if (keptInDrive > 0)
                    AnnounceOperation("move.done",
                        $"Copied, but {NameRules.Items(keptInDrive)} {(keptInDrive == 1 ? "was" : "were")} kept in Google Drive");

                // Files a merge found already there and left alone. Not "Move
                // complete" over originals that are still in the source: a Fill
                // gaps move said exactly that.
                else if (result.AlreadyThere > 0 && move && !fromDrive)
                    AnnounceOperation("move.done",
                        $"Moved. {NameRules.Items(result.AlreadyThere, "file")} already there " +
                        $"{(result.AlreadyThere == 1 ? "was" : "were")} left in the source folder");
                else if (result.AlreadyThere > 0)
                    AnnounceOperation(move ? "move.done" : "copy.done",
                        $"Copied. {NameRules.Items(result.AlreadyThere, "file")} " +
                        $"{(result.AlreadyThere == 1 ? "was" : "were")} already there");
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
                            // Exact: moved into "Backup." the plain path asked
                            // about "Backup\x", found nothing, and kept every
                            // original in Drive as unconfirmed.
                            var copy = new FileInfo(NameRules.ExactPath(arrivedAt));
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

            static string Was(int n) => n == 1 ? "was" : "were";

            if (kept > 0)
                Announce(kept == 1
                    ? "Copied, but the original could not be removed from Google Drive, so it is still there."
                    : $"Copied, but {kept:N0} originals could not be removed from Google Drive, so they are still there.",
                    isError: true);

            if (renamedKept > 0)
                Announce($"{NameRules.Items(renamedKept)} {Was(renamedKept)} kept in Google Drive, because the copy was saved " +
                         "under another name and could not be checked.", isError: true);

            if (tooBig > 0)
                Announce($"{NameRules.Items(tooBig, "folder")} {Was(tooBig)} kept in Google Drive, because there is too much " +
                         $"in {(tooBig == 1 ? "it" : "them")} to check the copy.", isError: true);

            if (unconfirmed > 0)
                Announce($"{NameRules.Items(unconfirmed)} {Was(unconfirmed)} kept in Google Drive, because the copy could not be " +
                         "shown to hold everything. It may hold Google documents, or something changed on the web.",
                         isError: true);

            if (mergedKept > 0)
                Announce($"{NameRules.Items(mergedKept, "folder")} {Was(mergedKept)} merged into folders already there and kept " +
                         "in Google Drive, because not everything in them was copied.", isError: true);

            int notLooked = paths.Length - looked;
            if (stopped)
                Announce($"Checking was stopped, so {NameRules.Items(notLooked)} not yet checked " +
                         $"{(notLooked == 1 ? "is" : "are")} still in Google Drive.", isError: true);

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

        // ---------- Audio conversion and editing ----------

        /// <summary>
        /// Converts the selected audio files, and the audio inside selected
        /// folders, to another format beside where each one is.
        ///
        /// Built like Compress: one question before anything starts, one
        /// conflict question for the whole batch if any target name is taken,
        /// then a modeless progress window and a one-sentence summary. The
        /// sources are only ever read. Async void and guarded throughout.
        /// </summary>
        private async void ConvertAudio()
        {
            if (RefuseInArchive()) return;
            try
            {
                if (IsDrivesView) { Announce("Cannot convert the drive list", isError: true); return; }

                var extensions = _settings.AudioExtensions;
                var selected = SelectedPaths()
                    .Where(p => IsDirectoryInView(p) || AudioFiles.IsAudio(p, extensions))
                    .ToArray();
                if (selected.Length == 0)
                {
                    AnnounceOperation("convert.nothing", "None of what is selected is audio");
                    return;
                }

                if (Drive != null && (Drive.Owns(Active.CurrentPath) || selected.Any(p => Drive.Owns(p))))
                {
                    Announce("Audio on Google Drive cannot be converted in place. " +
                             "Copy it to a local folder first.", isError: true);
                    RestoreListFocus();
                    return;
                }

                bool hasFolder = selected.Any(IsDirectoryInView);
                var unavailable = await Task.Run(AudioConvertForm.Availability);
                if (IsDisposed) return;

                var what = selected.Length == 1
                    ? $"Converting \"{Path.GetFileName(selected[0].TrimEnd('\\'))}\""
                    : $"Converting {NameRules.Items(selected.Length)}";
                var request = AudioConvertForm.Ask(this, "Convert audio", what,
                    AudioFormats.ById(_settings.AudioConvertFormat), unavailable, hasFolder,
                    _settings.AudioConvertSubfolders);
                if (request == null) { RestoreListFocus(); return; }

                // Remembered once answered, like Compress.
                _settings.AudioConvertFormat = request.Choice.Id;
                if (hasFolder) _settings.AudioConvertSubfolders = request.Subfolders;
                _settings.Save();

                var choice = request.Choice;
                var here = Active.CurrentPath;

                // The walk and every existence check happen on a worker.
                var plan = await Task.Run(() => AudioConverter.Plan(
                    AudioConverter.Gather(selected, request.Subfolders, extensions, CancellationToken.None), choice));
                if (IsDisposed) return;

                if (plan.Jobs.Count == 0)
                {
                    AnnounceOperation("convert.nothing", $"There are no audio files in {FolderDisplayName(selected[0])}");
                    RestoreListFocus();
                    return;
                }

                // Asked once for the batch, never per file, and only about names
                // already taken by the person's own files.
                var answer = FileOperations.ConflictChoice.Rename;
                if (plan.Clashing.Count > 0)
                {
                    answer = ConflictForm.Ask(this, plan.Clashing.Select(j => Path.GetFileName(j.Target)).ToList(),
                        FolderDisplayName(here), filesOnly: true);
                    RestoreListFocus();
                    if (answer == FileOperations.ConflictChoice.Cancel) return;
                }

                var (jobs, skipped) = await Task.Run(() =>
                {
                    var resolved = AudioConverter.Resolve(plan, answer, out int left);
                    return (resolved, left);
                });
                if (IsDisposed) return;

                if (jobs.Count == 0)
                {
                    AnnounceOperation("convert.done", $"Nothing converted; {NameRules.Items(skipped, "file")} skipped");
                    RestoreListFocus();
                    return;
                }

                var title = jobs.Count == 1
                    ? $"Converting {Path.GetFileName(jobs[0].Source)} to {choice.FormatName}"
                    : $"Converting {NameRules.Items(jobs.Count, "file")} to {choice.FormatName}";

                var cts = new CancellationTokenSource();   // cancelled, never disposed: a worker may still hold its token
                using var window = new ProgressForm(title, _settings, cts);
                var progress = new Progress<TransferProgress>(p => window.Update(p));

                BeginTransferTo(here);
                RunningTransfers++;
                try
                {
                    AnnounceOperation("convert.start", $"Converting {NameRules.Items(jobs.Count, "file")} to {choice.Label}");

                    var task = AudioConverter.RunAsync(jobs, choice, progress, cts.Token);
                    window.Show();

                    ConvertResult result;
                    try { result = await task; }
                    catch (Exception ex)
                    {
                        window.Finish();
                        AnnounceOperation("convert.failed", $"Could not convert: {ex.Message}");
                        await RefreshDestination(here);
                        RestoreListFocus();
                        return;
                    }

                    window.Finish();
                    result.Skipped += skipped;

                    // Every folder something landed in, not just the one on screen.
                    foreach (var folder in result.Written.Select(SafeParent).Append(here)
                                 .Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase))
                        await RefreshDestination(folder!);

                    var summary = result.Summary(choice);
                    if (result.Cancelled) AnnounceOperation("convert.cancelled", summary);
                    else if (result.Failures.Count > 0)
                    {
                        AnnounceOperation("convert.failed", summary);
                        if (result.Failures.Count > 3)
                            ShowErrors(result.Failures.Select(f => $"{f.Name}: {f.Reason}").ToList());
                    }
                    else AnnounceOperation("convert.done", summary);
                }
                finally { RunningTransfers--; EndTransferTo(here); }
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                AnnounceOperation("convert.failed", $"Could not convert: {ex.Message}");
                RestoreListFocus();
            }
        }

        /// <summary>
        /// Opens the audio editor on the focused file, in its own window, so the
        /// file manager stays usable behind it. Saving there never replaces the
        /// original unless that is the command chosen.
        /// </summary>
        private void EditAudio()
        {
            if (RefuseInArchive()) return;
            try
            {
                var path = FocusedPath();
                if (path == null || IsDirectoryInView(path) || !AudioFiles.IsAudio(path, _settings.AudioExtensions))
                {
                    Announce("Edit audio works on one audio file", isError: true);
                    return;
                }

                if (Drive != null && Drive.Owns(path))
                {
                    Announce("Audio on Google Drive cannot be edited in place. Copy it to a local folder first.",
                        isError: true);
                    return;
                }

                var editor = new AudioEditorForm(path, _settings);
                editor.Saved += saved =>
                {
                    var folder = SafeParent(saved);
                    if (!IsDisposed && folder != null) _ = RefreshDestination(folder);
                };
                editor.Show();
            }
            catch (Exception ex)
            {
                if (!IsDisposed) Announce("Could not open the audio editor: " + ex.Message, isError: true);
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
            if (RefuseInArchive()) return;

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

            // The dialog and what it is built from were outside every try, in an
            // async void: a dialog throwing on its way up took the window with it.
            ArchiveRequest? request;
            try
            {
                var remembered = ArchiveFormats.ById(_settings.ArchiveFormatId);
                var startFormat = remembered is { CanCreate: true } ? remembered : ArchiveFormats.Zip;

                // Asked before the dialog is built, on a worker: it decides whether a
                // bare .gz is offered, and the answer comes from the disk.
                var single = paths.Length == 1 &&
                             await Task.Run(() => { try { return File.Exists(NameRules.ExactPath(paths[0])); } catch { return false; } });
                if (IsDisposed) return;

                request = ArchiveForm.Ask(this, destination, paths, startFormat,
                    _settings.ArchiveCompression, single);
            }
            catch (Exception ex)
            {
                Announce($"Could not compress: {ex.Message}", isError: true);
                RestoreListFocus();
                return;
            }
            if (request == null) { RestoreListFocus(); return; }

            var cts = new CancellationTokenSource();   // cancelled, never disposed: a worker may still hold its token
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
                        $"Created {name}, but {NameRules.Items(result.Failed)} could not be read");
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
                        ? $"Created {name}{saved}. {NameRules.Items(result.LeftOut, "link")} " +
                          $"{(result.LeftOut == 1 ? "was" : "were")} left out"
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
            if (RefuseInArchive()) return;

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
                    $"{Path.GetFileName(archive)} is not an archive Explorer Native can open");
                RestoreListFocus();
                return;
            }

            // On a worker, like the sniff above. UniqueName is a loop of
            // existence checks, and the folder this is running in may be a share
            // whose server has gone away — which is exactly the moment the window
            // must not freeze.
            var destination = intoNewFolder
                ? await Task.Run(() => FileOperations.UniqueName(
                    Path.Combine(here, ArchiveFormats.BaseName(archive)), folder: true))
                : here;

            var cts = new CancellationTokenSource();   // cancelled, never disposed: a worker may still hold its token
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
                else if (result.Items == 0 && result.Failed > 0)
                {
                    // Nothing came out: a failure, not "Extracted 0 items, but 3 failed".
                    var reason = result.Errors.FirstOrDefault(e => !e.Contains(": ", StringComparison.Ordinal)) ??
                                 result.Errors.FirstOrDefault() ?? "";
                    AnnounceOperation("extract.failed", $"Could not extract {Path.GetFileName(archive)}: {reason.TrimEnd('.')}");
                    ShowErrors(result.Errors);
                }
                else if (result.Failed > 0)
                {
                    AnnounceOperation("extract.partial",
                        $"Extracted {NameRules.Items(result.Items)}, but {result.Failed:N0} failed");
                    ShowErrors(result.Errors);
                }
                else
                {
                    AnnounceOperation("extract.done",
                        result.Items == 1
                            ? $"Extracted to {FolderDisplayName(destination)}"
                            : $"Extracted {result.Items:N0} items to {FolderDisplayName(destination)}");
                }

                // Not after a cancel: "Extracting cancelled" is the whole story,
                // and a "Replaced" or "Saved as" after it reads as though the
                // extract went on. The counts are the engine's own, so a finished
                // extract names exactly what it renamed or replaced.
                if (!result.Cancelled) AnnounceConflicts(result.Collisions);
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
            if (RefuseInArchive()) return;

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
                        var unique = FileOperations.UniqueName(Path.Combine(folder, name), folder: true);
                        // Exact: inside "Backup." a plain path made the folder in "Backup".
                        Directory.CreateDirectory(NameRules.ExactPath(unique));
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
            if (RefuseInArchive()) return;

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
                        var unique = FileOperations.UniqueName(Path.Combine(folder, name), folder: false);

                        // CreateNew rather than Create: a file that appeared
                        // between the unique-name check and here belongs to
                        // somebody else, and truncating it would be the worst
                        // possible outcome of asking for a new one.
                        // Exact, as for New folder: inside "Backup." it went into "Backup".
                        using (new FileStream(NameRules.ExactPath(unique), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                        return unique;
                    });
                }

                await RefreshAsync(path);
                AnnounceOperation("file.created", $"Created file {Path.GetFileName(path)}");
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
            if (RefuseInArchive()) return;

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
            if (RefuseInArchive()) return;

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
                    var exact = NameRules.ExactPath(dest);
                    try { return File.Exists(exact) || Directory.Exists(exact); }
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
                    // Exact when any part of either path ends in a dot or a space
                    // (the name, or a folder it is in), or Windows trims it and
                    // renames a different file.
                    var from = NameRules.ExactPath(path);
                    var to = NameRules.ExactPath(dest);
                    if (Directory.Exists(from)) Directory.Move(from, to);
                    else File.Move(from, to);
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

        /// <summary>Removes sync pairs and what was remembered about them, saved at once.</summary>
        private void RemoveSyncPairs(HashSet<string> ids)
        {
            var monitor = DriveMonitor.Current;
            var remaining = (monitor?.Pairs ?? _settings.DriveSyncPairs).Where(p => !ids.Contains(p.Id)).ToList();
            if (monitor != null) monitor.SetPairs(remaining);
            else
            {
                _settings.DriveSyncPairs = remaining;
                _settings.Save();
            }
            DriveMonitor.ForgetRemovedPairs(remaining);
        }

        /// <summary>
        /// The other side's folder of each pair, after its first side was
        /// deleted: the PC folder to the Recycle Bin, or the Drive folder to the
        /// Drive trash through the API.
        /// </summary>
        private async Task DeleteOtherSyncEnds(List<(DriveSyncPair Pair, bool DriveSide)> ends)
        {
            if (ends.Count == 0) return;
            var owner = Handle;
            var client = Drive?.Client;
            var errors = new List<string>();

            await Task.Run(async () =>
            {
                foreach (var (pair, driveSide) in ends)
                {
                    try
                    {
                        // Exact: a PC folder "Backup." asked about plainly is
                        // "Backup", so one the Recycle Bin had refused read as
                        // gone and its Drive folder was trashed anyway.
                        var local = NameRules.ExactPath(pair.LocalFolder);
                        if (driveSide)
                        {
                            if (!Directory.Exists(local)) { }
                            // The shell would read it as "Backup" and recycle that.
                            else if (!string.Equals(local, pair.LocalFolder, StringComparison.Ordinal))
                                errors.Add($"{pair.LocalFolder}: kept, because Windows cannot put a name ending in a dot or a space in the Recycle Bin");
                            else if (!ShellDelete.Recycle(pair.LocalFolder, owner))
                                errors.Add($"{pair.LocalFolder}: kept, because it could only have been deleted permanently");
                        }
                        // This side was kept (Windows' "delete permanently?" answered No,
                        // or the delete failed): the other side stays too.
                        else if (Directory.Exists(local))
                            errors.Add($"{pair.LocalFolder}: still here, so {pair.DriveFolderPath} in Google Drive was kept too");
                        else if (client == null)
                            errors.Add($"{pair.DriveFolderPath}: Google Drive is not connected, so it was not trashed");
                        else
                            await client.Trash(pair.DriveFolderId, CancellationToken.None);
                    }
                    catch (Exception ex) { errors.Add($"{DriveMonitor.DisplayName(pair)}: {ex.Message}"); }
                }
            });

            if (IsDisposed) return;
            if (errors.Count > 0) Announce("The other copy was not deleted. " + string.Join("; ", errors), isError: true);
            else AnnounceOperation("delete.both", "Deleted on both sides");
        }

        /// <summary>
        /// Sends every path to the Recycle Bin in one shell operation and returns
        /// how many went, adding a sentence to <paramref name="errors"/> for each
        /// one kept.
        ///
        /// One operation, because the shell's cost is per operation: 500 files
        /// took 4.66 s one at a time and 1.28 s together. The shell does not say
        /// which items it did, so what is still there afterwards is the answer.
        /// The permanent-delete warning still comes up for anything the bin cannot
        /// take, and an item still there after somebody answered No or Cancel is
        /// kept. Anything else still there — the shell stops at the first item it
        /// cannot do — goes again on its own, which is also what gives each
        /// failure its own reason.
        /// </summary>
        private static int RecycleAll(List<string> paths, IntPtr owner, List<string> errors)
        {
            if (paths.Count == 0) return 0;
            var batch = ShellDelete.RecycleMany(paths, owner);

            int done = 0;
            foreach (var path in paths)
            {
                if (!Directory.Exists(path) && !File.Exists(path)) { done++; continue; }

                if (batch.Declined)
                {
                    errors.Add($"{Path.GetFileName(path)}: kept, because it could only have been deleted permanently");
                    continue;
                }

                try
                {
                    if (ShellDelete.Recycle(path, owner)) done++;
                    else errors.Add($"{Path.GetFileName(path)}: kept, because it could only have been deleted permanently");
                }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
            }
            return done;
        }

        private async void DeleteSelected(bool permanentOverride)
        {
            if (IsDrivesView) { Announce("Cannot delete a drive", isError: true); return; }
            if (RefuseInArchive()) return;
            if (_deleting) { Announce("A delete is already running"); return; }

            var paths = SelectedPaths();
            if (paths.Length == 0) { Announce("Nothing selected"); return; }

            // In Google Drive's trash, Delete (and Shift+Delete) is Delete
            // permanently, which always asks.
            if (Drive is { } trashDrive && paths.All(trashDrive.IsInTrash))
            {
                DeleteFromDriveTrash();
                return;
            }

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

            // A folder tied to a Google Drive sync pair is asked about first, in
            // one dialog for every pair the selection touches. That dialog is the
            // confirmation, so the ordinary one is not asked as well.
            var monitor = DriveMonitor.Current;
            var touched = SyncSafety.PairsTouchedBy(paths,
                monitor?.Pairs ?? (IEnumerable<DriveSyncPair>)_settings.DriveSyncPairs,
                Drive?.Letter, p => Drive?.KnownIdOf(p));
            var syncChoice = SyncDeleteChoice.Cancel;
            if (touched.Count > 0)
            {
                using (var ask = new SyncDeleteForm(touched.Select(t => t.Pair).ToList(), onDrive))
                {
                    ask.ShowDialog(this);
                    syncChoice = ask.Choice;
                }
                if (syncChoice == SyncDeleteChoice.Cancel)
                {
                    AnnounceOperation("delete.cancelled", "Delete cancelled");
                    RestoreListFocus();
                    return;
                }

                // The pairs go before any file does. Removed afterwards, a
                // monitor pass running during a long Recycle Bin delete would see
                // half the files gone and, with deletes on, copy those deletes to
                // the other side.
                RemoveSyncPairs(touched.Select(t => t.Pair.Id).ToHashSet());
            }

            if (_settings.ConfirmDelete && touched.Count == 0)
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
            (int Done, List<string> Errors) outcome;

            // The folders being deleted from are held off the watcher, one refresh
            // at the end rather than one per file removed — those folders only.
            // It was the whole window, and the Drive trash takes most of a second
            // an item, so a big delete there froze every other folder's updates.
            // Counted as a running transfer, so closing asks first.
            //
            // Everything raised on the last lines before the try, with nothing
            // that can throw between them and it: a raise left standing is a
            // folder that never refreshes again, and `_deleting` stuck true is a
            // Delete key that never works again.
            var windowHandle = Handle;
            var fromFolders = paths.Select(SafeParent).Where(f => f != null).Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            AnnounceOperation("delete.started", paths.Length == 1 ? "Deleting" : $"Deleting {paths.Length} items");

            _deleting = true;
            RunningTransfers++;
            foreach (var folder in fromFolders) BeginTransferTo(folder);
            try
            {
                if (onDrive)
                {
                    var trashing = System.Diagnostics.Stopwatch.StartNew();
                    outcome = await TrashOnDrive(paths);
                    GoogleDrive.Trace($"window delete: {paths.Length} items trashed in {trashing.ElapsedMilliseconds}ms");
                }
                else
                outcome = await Task.Run(() =>
                {
                    int done = 0;
                    var owner = windowHandle;
                    var errors = new List<string>();
                    var toRecycle = new List<string>();

                    foreach (var path in paths)
                    {
                        try
                        {
                            // A name ending in a dot or a space, anywhere in the
                            // path, is one the shell and every ordinary call trim —
                            // "report." would delete "report". Only the exact
                            // \\?\ form reaches it.
                            var exact = NameRules.ExactPath(path);
                            if (!Directory.Exists(exact) && !File.Exists(exact))
                            {
                                errors.Add($"{Path.GetFileName(path)}: no longer there");
                                continue;
                            }

                            // Directory.Delete with read-only cleared, links never
                            // followed, and any name or length; see ShellDelete.
                            if (permanent)
                            {
                                ShellDelete.DeletePermanently(path);
                                done++;
                                continue;
                            }

                            // The Recycle Bin does not take a name only the exact
                            // form reaches.
                            if (!string.Equals(exact, path, StringComparison.Ordinal))
                            {
                                errors.Add($"{Path.GetFileName(path)}: Windows cannot put a name ending in a dot " +
                                           "or a space in the Recycle Bin; Shift+Delete deletes it permanently");
                                continue;
                            }

                            toRecycle.Add(path);
                        }
                        catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
                    }

                    done += RecycleAll(toRecycle, owner, errors);
                    return (done, errors);
                });
            }
            finally
            {
                _deleting = false;
                RunningTransfers--;
                foreach (var folder in fromFolders) EndTransferTo(folder);
            }

            if (IsDisposed) return;

            // Delete anyway, with deletes on: the other end of each pair goes
            // too — to the Recycle Bin or the Drive trash, never for good.
            if (syncChoice == SyncDeleteChoice.DeleteAnyway)
                await DeleteOtherSyncEnds(touched.Where(t => t.Pair.CopyDeletes).ToList());

            var refreshing = System.Diagnostics.Stopwatch.StartNew();
            await RefreshAsync(landOn, folder: origin);
            if (onDrive) GoogleDrive.Trace($"window delete: refresh afterwards {refreshing.ElapsedMilliseconds}ms");
            if (IsDisposed) return;

            if (outcome.Errors.Count > 0)
            {
                int failedCount = outcome.Errors.Count;
                AnnounceOperation("delete.failed",
                    outcome.Done > 0
                        ? $"Deleted {NameRules.Items(outcome.Done)}, but {failedCount:N0} could not be deleted"
                        : failedCount == 1 ? $"Could not delete: {outcome.Errors[0]}"
                        : $"Could not delete {failedCount:N0} items");
                ShowErrors(outcome.Errors);
            }
            else
            {
                // One is just the verb (NameRules.SayCount).
                var said = NameRules.SayCount("Deleted", outcome.Done);

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
            if (RefuseForAppleMusic(path)) return;

            // The shell has nothing to show for a path through an archive.
            if (Active.Archive != null)
            {
                AnnounceOperation("archive.refused", "Properties are not available inside an archive");
                return;
            }

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

        private async void ShowShellMenuForSelection()
        {
            if (RefuseForAppleMusic(SelectedPaths())) return;

            if (Active.Archive != null)
            {
                AnnounceOperation("archive.refused", "The Windows menu is not available inside an archive");
                return;
            }

            var paths = SelectedPaths();
            if (paths.Length == 0)
            {
                if (IsDrivesView) { Announce("Nothing selected"); return; }
                paths = new[] { Active.CurrentPath };
            }

            var pane = Active;
            var list = pane.List;

            // Building the menu binds the folder through the shell on this
            // thread, and on a share that has gone to sleep that is the window
            // frozen with nothing said. Asked first, off the thread, bounded.
            ShellContextMenu.MenuOutcome outcome;
            try
            {
                var folder = SafeParent(paths[0]) ?? paths[0];
                var probe = await ProbeDirectoryAsync(folder);
                if (IsDisposed) return;
                if (probe != FolderProbe.Present)
                {
                    Announce(probe == FolderProbe.NotResponding
                        ? $"{FolderDisplayName(folder)} is not responding, so the Windows menu cannot be built"
                        : $"{FolderDisplayName(folder)} is not available", isError: true);
                    return;
                }

                // Somebody moved on while it was asked: the menu would be for
                // rows that are no longer in front of them.
                if (pane != Active) return;

                Point where = MenuAnchor(list);
                outcome = ShellContextMenu.Show(this, paths, where.X, where.Y);
            }
            catch (Exception ex)
            {
                // async void: an exception here would take the window with it.
                Announce($"Windows menu unavailable: {ex.Message}", isError: true);
                return;
            }

            switch (outcome)
            {
                // A command was chosen. Whatever it does finishes after the shell
                // hands back, so a refresh here was always a moment too early —
                // and in search results it ran the whole search again. The
                // folder watcher picks up what the command changes.
                case ShellContextMenu.MenuOutcome.Shown:
                case ShellContextMenu.MenuOutcome.Dismissed:
                    RestoreListFocus();
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

                // Windows reads "report." as "report", so its Delete could act
                // on a file nobody selected. Refused, with the way round it.
                case ShellContextMenu.MenuOutcome.AwkwardName:
                    Announce("The Windows menu is not offered for a name ending in a dot or a space, " +
                             "because Windows could act on a different file. Use this application's own commands instead.",
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
            if (RefuseForAppleMusic(path)) return;

            // Taken now: the answer belongs to this tab even if another is in
            // front by the time it arrives.
            var pane = Active;

            // Inside an archive the listing already knows, without a walk.
            if (pane.Archive is { } archive)
            {
                var inner = ArchiveBrowser.InnerPathOf(archive.ArchivePath, path);
                if (inner == null || !archive.IsFolder(inner)) { Announce("Select a folder first"); return; }
                var (bytes, files, folders) = archive.Measure(inner);
                var measured = SizeFormatter.Format(bytes, _settings.SizeUnits);
                SetSizeOverride(pane, path, measured);
                AnnounceOperation("size.done", SizeSentence(Path.GetFileName(path), measured, files, folders, true));
                return;
            }

            // Off the UI thread, because the path came from the list and the list
            // can be showing a share whose server has gone away. Directory.Exists
            // blocks for seconds on one of those, and this runs from a keystroke —
            // the exact freeze DirectoryExistsAsync exists to prevent, reached by
            // calling the synchronous one two lines above where the rule is
            // written down.
            // async void from a key: anything escaping takes the window with it.
            try
            {
                if (!await DirectoryExistsAsync(path)) { Announce("Select a folder first"); return; }

                AnnounceOperation("size.calculating", $"Measuring {Path.GetFileName(path)}");

                // Drive answers for its own folders, without populating the tree.
                // When it cannot, the letter is not walked instead: the walk
                // skips Drive folders, so it would be a wrong answer at the cost
                // of a listing per folder.
                FolderSizeResult result;
                if (Drive is { } drive && drive.Owns(path))
                {
                    FolderSizeResult? fromDrive = null;
                    try { fromDrive = await drive.FolderSizeAsync(path); } catch { }
                    if (IsDisposed) return;
                    if (fromDrive is { } known) result = known;
                    else
                    {
                        // Not in the index (the Shared drives list, or a folder whose
                        // id is not known yet): Drive's own listings, folder by
                        // folder, which is slower but still never walks the letter.
                        int listed = 0;
                        var (files, bytes) = await Task.Run(() => drive.MeasureOnDrive(
                            new[] { path }, _ => Interlocked.Increment(ref listed), CancellationToken.None));
                        if (IsDisposed) return;
                        result = new FolderSizeResult(bytes, files, Math.Max(0, listed - 1), true);
                    }
                }
                else result = await _folderSizes.CalculateAsync(path);
                if (IsDisposed) return;

                var size = SizeFormatter.Format(result.Bytes, _settings.SizeUnits);
                SetSizeOverride(pane, path, size + (result.Complete ? "" : "+"));

                // One id for both endings. A walk that ran out of time still answers
                // the question that was asked, and the sentence says it is partial.
                AnnounceOperation("size.done",
                    SizeSentence(Path.GetFileName(path), size, result.Files, result.Folders, result.Complete));
            }
            catch (Exception ex)
            {
                if (!IsDisposed) Announce($"Could not measure {Path.GetFileName(path)}: {ex.Message}", isError: true);
            }
        }

        /// <summary>
        /// "Music is 14.2 GB, 3,120 files in 12 folders" — counts that agree
        /// with their nouns, and no "in 0 folders". A walk that ran out of time
        /// says it is a lower bound.
        /// </summary>
        internal static string SizeSentence(string name, string size, long files, long folders, bool complete)
        {
            if (!complete) return $"{name} is at least {size}; measuring stopped before the end";
            static string Of(long n, string noun) => $"{n:N0} {noun}{(n == 1 ? "" : "s")}";
            var contents = folders > 0 ? $"{Of(files, "file")} in {Of(folders, "folder")}" : Of(files, "file");
            return $"{name} is {size}, {contents}";
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
        /// Never by walking the Drive letter: enumerating a cloud directory that
        /// has not been opened yet *is* a Drive listing (about 1.3 s each), so a
        /// walk is one request per folder in the whole Drive, and it populates
        /// the tree as a side effect. Drive folders are asked of Drive instead: GoogleDrive.FolderSizeAsync
        /// answers from one listing of the whole account, kept current by the
        /// changes feed, and nothing on the letter is walked.
        /// </summary>
        private async Task CalculateAllFolderSizesAsync(Pane pane, CancellationToken token)
        {
            // Snapshot the paths: the entry list is replaced wholesale on the
            // next navigation, and iterating the live one would walk the new
            // folder's contents under the old folder's token.
            var drive = Drive;
            var folders = new List<string>();
            foreach (var e in pane.Entries)
                if (e.IsDir) folders.Add(e.Path);

            foreach (var folder in folders)
            {
                if (IsDisposed || token.IsCancellationRequested) return;

                if (drive != null && drive.Owns(folder))
                {
                    FolderSizeResult? known;
                    try { known = await drive.FolderSizeAsync(folder, token); }
                    catch (OperationCanceledException) { return; }
                    if (IsDisposed || token.IsCancellationRequested) return;
                    if (known is { } size)
                        SetSizeOverride(pane, folder, SizeFormatter.Format(size.Bytes, _settings.SizeUnits));
                    continue;
                }

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

        private void OpenPreferences(string? startCategory, bool openShortcuts = false)
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
            try { OpenPreferencesCore(startCategory, openShortcuts); }
            finally { PreferencesOpen = false; }
        }

        private void OpenPreferencesCore(string? startCategory, bool openShortcuts = false)
        {
            // What the dialog started from, so OK can apply what was changed in it
            // and nothing else. See Settings.WithChanges.
            var before = _settings.Clone();
            using var dialog = new SettingsForm(_settings, startCategory)
            {
                OpenShortcutsWhenShown = openShortcuts,
                ExportRequested = owner => ExportSettings(owner),
            };
            var outcome = dialog.ShowDialog(this);

            // Import closes Preferences first: it replaces every setting, so
            // nothing changed on the dialog's pages could stand anyway.
            if (outcome == DialogResult.Yes && dialog.ImportPath is { } importFrom)
            {
                RestoreListFocus();
                ImportSettings(this, importFrom);
                return;
            }

            // Put the keyboard back in the list.
            //
            // Closing the dialog left focus on the form itself, which is a place
            // no key does anything: arrows did not move, letters did not search,
            // and there was nothing for a screen reader to read. Only the OK path
            // recovered, and then only as a side effect of the refresh it does.
            RestoreListFocus();

            if (outcome == DialogResult.Retry)
            {
                // The window's geometry is taken first, so what is kept is where
                // the window is now rather than where it was at the last save.
                // Not while settings.json cannot be read: what this session holds
                // then are defaults standing in for it, and saving a reset built
                // from them wiped the pairing code and the sync pairs it keeps.
                if (SettingsReset.Refusal(_settings) is { } refused)
                {
                    AnnounceOperation("settings.failed", refused);
                    return;
                }

                CaptureWindowGeometry();
                if (Program.ApplySettings(SettingsReset.Defaults(_settings, _panes[0].CurrentPath, _panes[1].CurrentPath)))
                    AnnounceOperation("settings.reset", "Preferences reset to defaults");
                else
                    AnnounceOperation("settings.failed",
                        "Preferences were reset for this session, but could not be saved. They come back on the next launch.");
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

        /// <summary>
        /// Writes the settings to a file of the person's choosing, as plain JSON
        /// another PC can import (see <see cref="Settings.ToExportJson"/>). The
        /// saved settings, not a page of Preferences still being edited.
        /// </summary>
        private async void ExportSettings(IWin32Window owner)
        {
            try
            {
                string path;
                using (var dialog = new SaveFileDialog
                {
                    Title = "Export settings",
                    FileName = "Explorer Native settings.json",
                    Filter = "Settings files (*.json)|*.json|All files (*.*)|*.*",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    OverwritePrompt = true,
                })
                {
                    var shown = dialog.ShowDialog(owner);
                    if (ReferenceEquals(owner, this)) RestoreListFocus();
                    if (shown != DialogResult.OK) return;
                    path = dialog.FileName;
                }

                var json = _settings.ToExportJson();
                // Off the UI thread: the file may be on a share.
                await Task.Run(() => File.WriteAllText(path, json, new System.Text.UTF8Encoding(false)));
                if (!IsDisposed) AnnounceOperation("settings.exported", $"Settings exported to {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                if (!IsDisposed) AnnounceOperation("settings.export.failed", "Could not export settings: " + ex.Message);
            }
        }

        /// <summary>
        /// Replaces the settings with an export's, after asking. The file goes
        /// through the same salvage and repair as settings.json; anything that is
        /// not Explorer Native settings is refused and nothing changes. This PC's
        /// own values stay (<see cref="Settings.ThisPcOnly"/>), and synced folders
        /// whose PC folder is not here come in paused. Applied like OK in
        /// Preferences, so shortcuts, folder handling and the window follow.
        /// </summary>
        /// <param name="chosen">A file already picked (Preferences' button), or null to ask for one.</param>
        private async void ImportSettings(IWin32Window owner, string? chosen)
        {
            try
            {
                var path = chosen;
                if (path == null)
                {
                    using var dialog = new OpenFileDialog
                    {
                        Title = "Import settings",
                        Filter = "Settings files (*.json)|*.json|All files (*.*)|*.*",
                        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        CheckFileExists = true,
                    };
                    var shown = dialog.ShowDialog(owner);
                    RestoreListFocus();
                    if (shown != DialogResult.OK) return;
                    path = dialog.FileName;
                }

                var (imported, refusal) = await Task.Run(() =>
                {
                    var read = Settings.FromExportJson(Settings.ExportFileText(File.ReadAllBytes(path)), out var why);
                    return (read, why);
                });
                if (IsDisposed) return;
                if (imported == null)
                {
                    AnnounceOperation("settings.import.failed", refusal ?? Settings.NotSettingsSentence);
                    return;
                }

                var name = Path.GetFileName(path);
                var answer = MessageBox.Show(this,
                    $"Replace your current settings with the ones in {name}? " +
                    "Your Google sign-in and synced folders' progress are not changed.",
                    "Import settings", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                RestoreListFocus();
                if (answer != DialogResult.Yes) return;

                // The live values this PC keeps, taken now; the folder checks off
                // the UI thread, since a synced folder may be on a share.
                var live = _settings.Clone();
                int paused = await Task.Run(() => Settings.PrepareImport(live, imported, Directory.Exists));
                if (IsDisposed) return;

                if (!Program.ApplySettings(imported))
                {
                    AnnounceOperation("settings.failed",
                        "The imported settings apply to this session, but could not be saved. They are lost on the next launch.");
                    return;
                }
                var said = $"Settings imported from {name}";
                if (paused == 1) said += ". One synced folder is paused, because its folder is not on this PC";
                else if (paused > 1) said += $". {paused} synced folders are paused, because their folders are not on this PC";
                AnnounceOperation("settings.imported", said);
            }
            catch (Exception ex)
            {
                if (!IsDisposed) AnnounceOperation("settings.import.failed", "Could not import settings: " + ex.Message);
            }
        }

        private void AnnounceFolder()
        {
            var count = Active.Entries.Count;
            var where = IsDrivesView ? "Drives" : AppleMusic.DisplayPath(Active.CurrentPath);
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
                SetStatusText($"{prefix}{NameRules.Items(total)}{more}");
                return;
            }

            // Nothing in Apple Music has a size to add up.
            if (selected > MaxSelectionToSum || AppleMusic.Is(pane.CurrentPath))
            {
                SetStatusText($"{prefix}{NameRules.Items(total)}{more}, {selected} selected");
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

            SetStatusText(
                $"{prefix}{NameRules.Items(total)}{more}, {selected} selected " +
                $"({SizeFormatter.Format(bytes, _settings.SizeUnits)})");
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
        /// <summary>Unchanged text is not set again: each set lays the status strip out, once per arrow press.</summary>
        private void SetStatusText(string text)
        {
            if (!string.Equals(_statusLabel.Text, text, StringComparison.Ordinal)) _statusLabel.Text = text;
        }

        private void ShowStatus(string message)
        {
            _statusTimer?.Stop();
            _statusLabel.Text = message;
        }

        /// <summary>
        /// A message with no catalogue entry of its own. An error goes out under
        /// "error.generic", so "Something went wrong" in Choose what is spoken
        /// decides it, as a finer id would.
        /// </summary>
        private void Announce(string message, bool isError = false)
        {
            if (isError) { AnnounceOperation("error.generic", message); return; }
            ShowStatus(message);
            if (_settings.SpeakEnabled) Speech.Speak(message, _settings.InterruptSpeech);
        }

        /// <summary>
        /// An operation's result, routed by its catalogue id.
        ///
        /// The id decides whether it is spoken and the sentence is what is said —
        /// the caller knows how many items and which one failed, and the
        /// catalogue's own template exists for previewing rather than for saying.
        /// An unknown id is spoken, so nothing goes quiet merely for not being
        /// catalogued yet.
        ///
        /// The status bar gets it either way. That is not a channel any more —
        /// writing a silent line costs nothing and interrupts nobody, and a
        /// message routed nowhere used to leave the bar showing whatever it was
        /// showing before, which is a stale answer to a question somebody has
        /// just asked.
        /// </summary>
        private void AnnounceOperation(string notificationId, string message)
        {
            ShowStatus(message);
            WindowsToasts.Raise(notificationId, message, _settings);
            if (!_settings.SpeakEnabled) return;
            if (Notifications.ById(notificationId) != null &&
                Notifications.ChannelsFor(notificationId, _settings) != NotificationChannel.Speech) return;
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

        /// <param name="openShortcuts">Straight into the audio shortcuts window, after a shortcut was refused at startup.</param>
        public void OpenPreferencesExternally(string? category = null, bool openShortcuts = false) =>
            OpenPreferences(category, openShortcuts);

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

            // Copies, moves, deletes and archives all count here, so the words cover all of them.
            var question = RunningTransfers == 1
                ? $"A file operation is still running. {verb} anyway and abandon it?"
                : $"{RunningTransfers} file operations are still running. {verb} anyway and abandon them?";

            if (MessageBox.Show(this, question, "Still working",
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
                AppleMusic.Grew -= OnAppleMusicGrew;
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

                // Both tick into the window: a details read or a selection
                // count arriving after it has gone touched a disposed list.
                _detailsTimer?.Stop();
                _detailsTimer?.Dispose();
                _selectionSpeechTimer?.Stop();
                _selectionSpeechTimer?.Dispose();
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





