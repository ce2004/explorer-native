using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// File, Google Drive sync configurator (while Drive is on): the folder
    /// pairs, one sentence each, and Add, Edit, Remove, Sync now and Close.
    /// Changes are saved the moment they are made.
    /// </summary>
    internal sealed class DriveMonitorForm : Form
    {
        private readonly List<DriveSyncPair> _working;
        private readonly List<DriveSyncPair> _pairs;
        private readonly LineList _list;
        private readonly LineList _details;
        private readonly Button _edit, _remove, _sync, _history;
        private readonly DriveMonitor? _monitor;

        /// <param name="working">Preferences' own copy of the pairs, kept in step.</param>
        public DriveMonitorForm(List<DriveSyncPair> working)
        {
            _working = working;
            _pairs = working.Select(p => p.Copy()).ToList();
            _monitor = DriveMonitor.Current;

            Text = "Google Drive sync configurator";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(640, 470);
            Font = SystemFonts.MessageBoxFont;

            var label = new Label { Text = "&Monitored folders:", AutoSize = true, Location = new Point(12, 12) };
            _list = new LineList
            {
                Location = new Point(12, 32),
                Size = new Size(616, 150),
                AccessibleName = "Monitored folders",
                TabIndex = 0,
            };

            // The chosen folder's sync, one fact a line, so the arrows go
            // through it instead of one sentence too long to follow.
            var detailsLabel = new Label { Text = "S&tatus:", AutoSize = true, Location = new Point(12, 190) };
            _details = new LineList
            {
                Location = new Point(12, 210),
                Size = new Size(616, 140),
                AccessibleName = "Status",
                TabIndex = 1,
            };

            var info = new Label
            {
                Text = "Changes here are saved straight away. Folders sync while Explorer Native is running " +
                       "and Google Drive is connected. Enter edits a folder, Delete removes it; removing " +
                       "never deletes any files.",
                Location = new Point(12, 358),
                Size = new Size(616, 48),
            };

            var add = MakeButton("&Add...", 12, 2);
            _edit = MakeButton("&Edit...", 112, 3);
            _remove = MakeButton("&Remove", 212, 4);
            _sync = MakeButton("&Sync now", 312, 5);
            _history = MakeButton("&History...", 412, 6);
            var close = MakeButton("Close", 548, 7);
            close.DialogResult = DialogResult.Cancel;
            _history.Click += (_, _) => ShowHistory();

            add.Click += (_, _) => AddPair();
            _edit.Click += (_, _) => EditPair();
            _remove.Click += (_, _) => RemovePair();
            _sync.Click += (_, _) =>
            {
                if (_monitor == null) { Say("The monitor is not running"); return; }
                _monitor.SyncNow();
                Say(_pairs.Count == 0 ? "No folders to sync" : "Syncing");
            };

            _list.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter && _pairs.Count > 0) { EditPair(); e.Handled = e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Delete && _pairs.Count > 0) { RemovePair(); e.Handled = e.SuppressKeyPress = true; }
            };

            _list.SelectedIndexChanged += (_, _) => { if (_list.SelectedIndex >= 0) FillDetails(); };

            Controls.AddRange(new Control[] { label, _list, detailsLabel, _details, info, add, _edit, _remove, _sync, _history, close });
            CancelButton = close;

            Action refresh = () =>
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => Fill()));
            };
            if (_monitor != null) _monitor.Changed += refresh;
            FormClosed += (_, _) => { if (_monitor != null) _monitor.Changed -= refresh; };

            // Speeds change with no event to say so, so once a second while a
            // pass runs and this window is open; nothing at all otherwise.
            var tick = new System.Windows.Forms.Timer { Interval = 1000 };
            tick.Tick += (_, _) =>
            {
                if (_monitor != null && _pairs.Any(p => _monitor.StatusIfLoaded(p.Id) is { Running: true })) Fill();
            };
            tick.Start();
            FormClosed += (_, _) => tick.Dispose();

            Fill();
        }

        private Button MakeButton(string text, int x, int tab) =>
            new() { Text = text, Location = new Point(x, 426), Size = new Size(92, 30), TabIndex = tab };

        /// <param name="all">After a folder is added, edited or removed: every line, the one being read too, since the rows have moved.</param>
        private void Fill(bool all = false)
        {
            var rows = _pairs.Count == 0
                ? new List<string> { "No folders are monitored yet. Press Add to choose one." }
                : _pairs.Select(p => Describe(p, _monitor?.StatusIfLoaded(p.Id), DateTime.UtcNow)).ToList();

            _list.SetLines(rows, all);

            bool any = _pairs.Count > 0;
            _edit.Enabled = any;
            _remove.Enabled = any;
            _history.Enabled = any;
            FillDetails();
        }

        /// <summary>What the chosen folder did, file by file, newest first. Read on a worker.</summary>
        private async void ShowHistory()
        {
          // An async void handler: nothing may escape it, or the application ends.
          try
          {
            int at = _list.SelectedIndex;
            if (at < 0 || at >= _pairs.Count) return;
            var pair = _pairs[at];
            var entries = await Task.Run(() => SyncHistory.Read(pair.Id));
            if (IsDisposed) return;
            var now = DateTime.Now;
            var lines = entries.Count == 0
                ? new List<string> { "Nothing has been copied, moved or deleted yet." }
                : entries.Select(e => SyncHistory.Describe(e, now)).ToList();
            using var dialog = new SyncHistoryForm($"History of {DriveMonitor.DisplayName(pair)}", lines);
            dialog.ShowDialog(this);
            _list.Focus();
          }
          catch { }
        }

        /// <summary>The status lines of the folder chosen above, changed in place so the arrows keep their line.</summary>
        private void FillDetails()
        {
            int at = _list.SelectedIndex;
            var lines = at >= 0 && at < _pairs.Count
                ? Details(_pairs[at], _monitor?.StatusIfLoaded(_pairs[at].Id), DateTime.UtcNow, _monitor?.LiveOf(_pairs[at].Id))
                : new List<string> { "No folder chosen." };

            _details.SetLines(lines);
        }

        /// <summary>
        /// A list of lines whose words can change while it is read. A list box
        /// cannot change a line: it takes it out and puts it back, which drops
        /// the selection and which NVDA hears as landing on it again, so a
        /// sync talked over itself every second and the arrows were thrown
        /// back to the top. A list view changes a line's words in place. The
        /// line under the keyboard is still left alone until it is left, so
        /// sitting on the speed is not read again every second; arrowing to a
        /// line finds it up to date.
        /// </summary>
        internal sealed class LineList : ListView
        {
            public LineList()
            {
                View = View.Details;
                HeaderStyle = ColumnHeaderStyle.None;
                FullRowSelect = true;
                MultiSelect = false;
                HideSelection = false;
                Columns.Add("", 600);
                Resize += (_, _) => Columns[0].Width = Math.Max(600, ClientSize.Width - 4);
            }

            public int SelectedIndex
            {
                get => SelectedIndices.Count > 0 ? SelectedIndices[0] : -1;
                set
                {
                    if (value < 0 || value >= Items.Count) return;
                    Items[value].Selected = true;
                    Items[value].Focused = true;
                    Items[value].EnsureVisible();
                }
            }

            /// <param name="all">Every line, the one being read too: after the rows themselves moved.</param>
            public void SetLines(IReadOnlyList<string> rows, bool all = false)
            {
                int hold = Focused && !all ? SelectedIndex : -1;
                int common = Math.Min(rows.Count, Items.Count);
                for (int i = 0; i < common; i++)
                    if (i != hold && Items[i].Text != rows[i]) Items[i].Text = rows[i];
                while (Items.Count > rows.Count) Items.RemoveAt(Items.Count - 1);
                for (int i = Items.Count; i < rows.Count; i++) Items.Add(rows[i]);
                if (SelectedIndex < 0 && Items.Count > 0) SelectedIndex = Math.Min(Math.Max(hold, 0), Items.Count - 1);
            }
        }

        /// <summary>
        /// One pair, what it is doing first, then what it does in plain words:
        /// "Music, last synced 2 minutes ago. Mirrors C:\Users\Conner\Music and
        /// My Drive/Music both ways."
        /// </summary>
        internal static string Describe(DriveSyncPair pair, DriveMonitor.PairStatus? status, DateTime nowUtc)
        {
            var does = pair.Mode switch
            {
                SyncMode.UploadOnly => $"Copies {pair.LocalFolder} up to {pair.DriveFolderPath}.",
                SyncMode.DownloadOnly => $"Copies {pair.DriveFolderPath} down to {pair.LocalFolder}.",
                _ => $"Mirrors {pair.LocalFolder} and {pair.DriveFolderPath} both ways.",
            };
            var deletes = pair.CopyDeletes ? " Files you delete are deleted on the other side too." : "";

            string state;
            if (pair.Paused) state = "paused";
            else if (status?.Problem is { } waiting && waiting.StartsWith("waiting", StringComparison.Ordinal))
                state = waiting;
            else if (status is { Running: true, CheckTotal: > 0 })
                state = $"checking with Google what is already there, {status.CheckDone:N0} of {status.CheckTotal:N0} " +
                        $"files compared, {status.LocalFound:N0} on the PC and {status.DriveFound:N0} on Drive";
            else if (status is { Running: true, Phase: DriveMonitor.PhaseScanning })
                state = $"reading the PC folder, {status.LocalFound:N0} files found so far";
            else if (status is { Running: true, Phase: DriveMonitor.PhaseListing })
                state = $"{status.LocalFound:N0} files on the PC, reading Google Drive, {status.DriveFound:N0} files found so far";
            else if (status is { Running: true, Phase: DriveMonitor.PhaseSyncing })
                state = Progress(status);
            else if (status is { Running: true, TotalBytes: > 0 })
                state = $"syncing, {DriveMonitor.Words(status.DoneBytes)} of {DriveMonitor.Words(status.TotalBytes)}";
            else if (status is { Running: true }) state = "syncing now";
            else if (status?.Problem is { } problem)
                state = problem.StartsWith("paused", StringComparison.Ordinal) ||
                        problem.StartsWith("offline", StringComparison.Ordinal)
                    ? problem
                    : "problem: " + problem;
            else if (status?.LastSyncedUtc is DateTime when)
            {
                state = "last synced " + Ago(nowUtc - when);
                if (status.Matched + status.Uploaded + status.Downloaded + status.Deleted + status.Failed + status.Moved > 0)
                {
                    var did = new List<string> { $"{status.Matched:N0} on both sides" };
                    if (status.Moved > 0) did.Add($"{status.Moved:N0} moved");
                    if (status.Uploaded > 0) did.Add($"{status.Uploaded:N0} uploaded");
                    if (status.Downloaded > 0) did.Add($"{status.Downloaded:N0} downloaded");
                    if (status.Deleted > 0) did.Add($"{status.Deleted:N0} deleted");
                    if (status.Failed > 0) did.Add($"{status.Failed:N0} failed");
                    state += ", " + string.Join(", ", did);
                }
            }
            else state = "waiting to sync";

            return $"{DriveMonitor.DisplayName(pair)}, {state}. {does}{deletes}";
        }

        /// <summary>
        /// "1,995,500 already on both sides, change 1,204 of 4,500: 1,000
        /// uploaded, 200 downloaded, 3 failed, 1.2 gigabytes of 4 gigabytes".
        /// </summary>
        internal static string Progress(DriveMonitor.PairStatus status)
        {
            var text = $"{status.Matched:N0} already on both sides";
            if (status.StepTotal == 0) return text + ", nothing to copy";

            text += $", change {Math.Min(status.StepDone + 1, status.StepTotal):N0} of {status.StepTotal:N0}";
            var done = new List<string>();
            if (status.Moved > 0) done.Add($"{status.Moved:N0} moved");
            if (status.Uploaded > 0) done.Add($"{status.Uploaded:N0} uploaded");
            if (status.Downloaded > 0) done.Add($"{status.Downloaded:N0} downloaded");
            if (status.Deleted > 0) done.Add($"{status.Deleted:N0} deleted");
            if (status.Failed > 0) done.Add($"{status.Failed:N0} failed");
            if (done.Count > 0) text += ": " + string.Join(", ", done);
            if (status.TotalBytes > 0)
                text += $", {DriveMonitor.Words(status.DoneBytes)} of {DriveMonitor.Words(status.TotalBytes)}";
            return text;
        }

        /// <summary>
        /// The Status list, one fact a line, for example:
        /// "Syncing now", "On the PC: 2,000,000 files", "On Google Drive:
        /// 1,996,000 files", "Already in both folders: 1,995,500 files",
        /// "Working on change 1,204 of 4,500", "Uploaded: 1,000 files",
        /// "Copied: 1.2 gigabytes of 4 gigabytes", "Upload speed: 2.4 megabytes
        /// a second", "Uploading Music\Live.flac, 12 megabytes of 40 megabytes",
        /// "And 14 more files moving".
        /// </summary>
        internal static List<string> Details(DriveSyncPair pair, DriveMonitor.PairStatus? status, DateTime nowUtc,
            SyncLive.Snapshot? live)
        {
            static string Files(int n) => n == 1 ? "1 file" : $"{n:N0} files";
            static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

            var lines = new List<string>();
            bool running = status is { Running: true };
            if (pair.Paused) lines.Add("Paused. To start it, press Enter on it above and untick Paused");
            else if (status?.Problem is { } waiting && waiting.StartsWith("waiting", StringComparison.Ordinal))
                lines.Add(Capital(waiting));
            else if (running)
                lines.Add(status!.CheckTotal > 0 ? "Checking with Google what is already there"
                    : status.Phase switch
                    {
                        DriveMonitor.PhaseScanning => "Reading the PC folder",
                        DriveMonitor.PhaseListing => "Reading Google Drive",
                        _ => "Syncing now",
                    });
            else if (status?.Problem is { } problem)
                lines.Add(problem.StartsWith("paused", StringComparison.Ordinal) ||
                          problem.StartsWith("offline", StringComparison.Ordinal)
                    ? Capital(problem) : "Problem: " + problem);
            else if (status?.LastSyncedUtc is DateTime when) lines.Add("Last synced " + Ago(nowUtc - when));
            else lines.Add("Waiting to sync");

            if (status == null) return lines;

            bool stillCounting = running && status.Phase is DriveMonitor.PhaseScanning or DriveMonitor.PhaseListing;
            if (status.LocalFound > 0)
                lines.Add($"On the PC: {Files(status.LocalFound)}" + (stillCounting && status.Phase == DriveMonitor.PhaseScanning ? " so far" : ""));
            if (status.DriveFound > 0)
                lines.Add($"On Google Drive: {Files(status.DriveFound)}" + (stillCounting ? " so far" : ""));
            if (running && status.CheckTotal > 0)
                lines.Add($"Compared with Google: {status.CheckDone:N0} of {Files(status.CheckTotal)}");
            if (status.Matched > 0) lines.Add($"Already in both folders: {Files(status.Matched)}");

            if (running && status.Phase == DriveMonitor.PhaseSyncing)
                lines.Add(status.StepTotal == 0 ? "Nothing to copy"
                    : $"Working on change {Math.Min(status.StepDone + 1, status.StepTotal):N0} of {status.StepTotal:N0}");
            if (status.Uploaded > 0) lines.Add($"Uploaded: {Files(status.Uploaded)}");
            if (status.Downloaded > 0) lines.Add($"Downloaded: {Files(status.Downloaded)}");
            if (status.Moved > 0) lines.Add($"Moved or renamed: {Files(status.Moved)}");
            if (status.Deleted > 0) lines.Add($"Deleted: {Files(status.Deleted)}");
            if (status.Failed > 0) lines.Add($"Failed: {Files(status.Failed)}");
            if (running && status.TotalBytes > 0)
                lines.Add($"Copied: {DriveMonitor.Words(status.DoneBytes)} of {DriveMonitor.Words(status.TotalBytes)}");

            if (running && live != null)
            {
                if (live.UpPerSecond > 0) lines.Add($"Upload speed: {DriveMonitor.Words(live.UpPerSecond)} a second");
                if (live.DownPerSecond > 0) lines.Add($"Download speed: {DriveMonitor.Words(live.DownPerSecond)} a second");
                foreach (var f in live.Files.Take(MovingShown))
                    // How far along only for a file big enough to take a while.
                    lines.Add($"{(f.Up ? "Uploading" : "Downloading")} {f.Path.Replace('/', '\\')}" +
                              (f.Size >= 1024 * 1024 ? $", {DriveMonitor.Words(f.Done)} of {DriveMonitor.Words(f.Size)}" : ""));
                int more = live.Files.Count - MovingShown;
                if (more > 0) lines.Add(more == 1 ? "And 1 more file moving" : $"And {more:N0} more files moving");
            }
            return lines;
        }

        /// <summary>Files named one a line in Status; the rest are counted.</summary>
        internal const int MovingShown = 10;

        /// <summary>Whether File shows "Google Drive sync configurator": only while Drive is switched on.</summary>
        internal static bool OnFileMenu(Settings settings) => settings.GoogleDriveEnabled;

        internal static string Ago(TimeSpan span)
        {
            if (span.TotalSeconds < 60) return "just now";
            if (span.TotalMinutes < 60) return Plural((int)span.TotalMinutes, "minute") + " ago";
            if (span.TotalHours < 24) return Plural((int)span.TotalHours, "hour") + " ago";
            return Plural((int)span.TotalDays, "day") + " ago";
        }

        private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

        private void AddPair()
        {
            var pair = new DriveSyncPair();
            using var dialog = new DrivePairForm(pair, "Add a monitored folder") { Others = _pairs.ToList() };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _pairs.Add(dialog.Pair);
            Commit();
            ShowSaved(_pairs.Count - 1);
        }

        /// <summary>
        /// Back on the list, on the folder just saved, so its status is what is
        /// read next; then whether it is syncing, after that line, not over it.
        /// </summary>
        private void ShowSaved(int at)
        {
            _list.SelectedIndex = at;
            FillDetails();
            _list.Focus();
            try
            {
                Speech.Speak(_pairs[at].Paused
                    ? "Saved. It is paused, so nothing is copied until you untick Paused."
                    : "Saved. Syncing has started. The Status list shows how it is going.");
            }
            catch { }
        }

        private void EditPair()
        {
            int at = _list.SelectedIndex;
            if (at < 0 || at >= _pairs.Count) return;
            using var dialog = new DrivePairForm(_pairs[at].Copy(), "Edit a monitored folder")
                { Others = _pairs.Where((_, i) => i != at).ToList() };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _pairs[at] = dialog.Pair;
            Commit();
            ShowSaved(at);
        }

        private async void RemovePair()
        {
          // An async void handler: nothing may escape it, or the application ends.
          try
          {
            int at = _list.SelectedIndex;
            if (at < 0 || at >= _pairs.Count) return;
            var id = _pairs[at].Id;
            var name = DriveMonitor.DisplayName(_pairs[at]);
            _pairs.RemoveAt(at);
            Commit();

            // Its pass, if one is running, stops at its next file; "removed" is
            // said once it has, or after ten seconds at most.
            if (_monitor != null) await _monitor.WaitForPassAsync(id, TimeSpan.FromSeconds(10));
            Say($"Removed {name}. No files were deleted.");
          }
          catch { }
        }

        /// <summary>Saves straight away, and keeps Preferences' copy in step so its OK does not undo it.</summary>
        private void Commit()
        {
            _working.Clear();
            _working.AddRange(_pairs.Select(p => p.Copy()));
            _monitor?.SetPairs(_pairs);
            Fill(all: true);
        }

        private static void Say(string text)
        {
            try { Speech.Speak(text, interrupt: true); } catch { }
        }
    }

    /// <summary>
    /// One synced folder's history, newest first, a line a file: "Uploaded
    /// Album/song.mp3, today at 8:31 PM". Arrows read it; Escape closes.
    /// </summary>
    internal sealed class SyncHistoryForm : Form
    {
        public SyncHistoryForm(string title, IReadOnlyList<string> lines)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(640, 420);
            Font = SystemFonts.MessageBoxFont;

            var label = new Label { Text = "&What happened, newest first:", AutoSize = true, Location = new Point(12, 12) };
            var list = new DriveMonitorForm.LineList
            {
                Location = new Point(12, 32),
                Size = new Size(616, 340),
                AccessibleName = "What happened, newest first",
                TabIndex = 0,
            };
            list.SetLines(lines);
            list.SelectedIndex = 0;
            var close = new Button
            {
                Text = "Close", DialogResult = DialogResult.Cancel,
                Location = new Point(548, 382), Size = new Size(80, 28), TabIndex = 1,
            };
            Controls.AddRange(new Control[] { label, list, close });
            CancelButton = close;
        }
    }

    /// <summary>
    /// Add or edit one folder pair. OK looks at both folders first: before a
    /// first sync it puts what would happen (how many files on each side, how
    /// many already match, what would be uploaded, downloaded, kept twice or
    /// deleted) in a box above OK, and OK again starts it; and for any start it checks there is room (the PC's
    /// disk for what comes down, Drive's quota for what goes up), offering to
    /// save the pair paused when there is not.
    /// </summary>
    internal sealed class DrivePairForm : Form
    {
        private readonly TextBox _name, _local, _drive, _skipTypes;
        private readonly ComboBox _mode, _maxSize, _clash;
        private readonly TextBox _example;
        private readonly CheckBox _deletes, _paused, _subfolders;
        private readonly Label _status, _summaryLabel;
        private readonly TextBox _summary;
        private readonly Button _ok;
        private bool _checking;

        /// <summary>The settings the summary was made for: OK again with these starts without looking twice.</summary>
        private string? _previewedKey;

        /// <summary>Ends the space check: Cancel, Escape and closing the window all do.</summary>
        private readonly CancellationTokenSource _closing = new();

        public DriveSyncPair Pair { get; }

        private static readonly (SyncMode Mode, string Text)[] Modes =
        {
            (SyncMode.TwoWay, "2-way mirror: a change on either side is made on the other"),
            (SyncMode.UploadOnly, "1-way mirror, PC to Drive: a backup in Drive"),
            (SyncMode.DownloadOnly, "1-way mirror, Drive to PC: a copy on this PC"),
        };

        /// <summary>
        /// What the chosen direction does, as one example with the person's own
        /// folders: "Put notes.txt in Music on this PC, and within a
        /// minute it is in music in Google Drive too..." Shown under Direction and
        /// rewritten whenever the direction, either folder or the delete box changes.
        /// </summary>
        internal static string Example(SyncMode mode, string localFolder, string drivePath, bool deletes)
        {
            var leaf = Path.GetFileName(localFolder.Trim().TrimEnd('\\', '/'));
            var pc = string.IsNullOrEmpty(leaf) ? "your folder on this PC" : leaf + " on this PC";
            var driveLeaf = drivePath.Trim().TrimEnd('/');
            driveLeaf = driveLeaf[(driveLeaf.LastIndexOf('/') + 1)..];
            var drive = string.IsNullOrEmpty(driveLeaf) ? "your folder in Google Drive" : driveLeaf + " in Google Drive";

            return mode switch
            {
                SyncMode.UploadOnly =>
                    $"Put notes.txt in {pc}, and within a minute it is in {drive} too. " +
                    "Edit it on the PC, and Drive gets the new version. " +
                    (deletes
                        ? "Delete it on the PC, and Drive's copy goes to the Drive trash. "
                        : "Delete it on the PC, and Drive keeps its copy, as a backup. ") +
                    "Nothing added or changed in Drive ever comes down to this PC, and a file deleted in Drive is put back from the PC.",
                SyncMode.DownloadOnly =>
                    $"Add notes.txt to {drive}, and within a minute it is in {pc} too. " +
                    "Edit it in Drive, and the PC gets the new version. " +
                    (deletes
                        ? "Delete it in Drive, and the PC's copy goes to the Recycle Bin. "
                        : "Delete it in Drive, and the PC keeps its copy. ") +
                    "Nothing added or changed on this PC ever goes up to Drive, and a file deleted on the PC comes back from Drive.",
                _ =>
                    $"Put notes.txt in {pc}, and within a minute it is in {drive} too. " +
                    $"Edit it in Drive, and the change comes down to the PC. " +
                    (deletes
                        ? "Delete it on either side, and the other side's copy goes to the Recycle Bin or the Drive trash."
                        : "Delete it on one side, and it comes back from the other. To have deletes copied too, tick Also delete on the other side."),
            };
        }

        private static readonly (int Megabytes, string Text)[] Sizes =
        {
            (0, "No limit"), (100, "100 megabytes"), (500, "500 megabytes"), (1024, "1 gigabyte"), (4096, "4 gigabytes"),
        };

        private static readonly (ConflictChoice Choice, string Text)[] Clashes =
        {
            (ConflictChoice.KeepBoth, "Keep both copies"),
            (ConflictChoice.PcWins, "This PC's copy wins"),
            (ConflictChoice.DriveWins, "Google Drive's copy wins"),
        };

        /// <summary>The other monitored folders, which this one may not overlap.</summary>
        public List<DriveSyncPair> Others { get; init; } = new();

        /// <summary>The pair as it was when the dialog opened, to tell an edit from a new start.</summary>
        private readonly DriveSyncPair _original;

        public DrivePairForm(DriveSyncPair pair, string title)
        {
            Pair = pair;
            _original = pair.Copy();

            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(600, 500);
            Font = SystemFonts.MessageBoxFont;

            int y = 12, tab = 0;
            void Caption(string text) =>
                Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(12, y + 4) });

            TextBox Field(string label, string value, int width, bool readOnly = false)
            {
                Caption(label);
                var box = new TextBox
                {
                    Text = value,
                    Location = new Point(220, y),
                    Width = width,
                    ReadOnly = readOnly,
                    AccessibleName = label.Replace("&", "").TrimEnd(':'),
                    TabIndex = tab++,
                };
                Controls.Add(box);
                return box;
            }

            ComboBox Choice(string label, IEnumerable<string> items, int selected)
            {
                Caption(label);
                var combo = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Location = new Point(220, y),
                    Width = 260,
                    AccessibleName = label.Replace("&", "").TrimEnd(':'),
                    TabIndex = tab++,
                };
                foreach (var item in items) combo.Items.Add(item);
                combo.SelectedIndex = Math.Max(0, selected);
                Controls.Add(combo);
                return combo;
            }

            CheckBox Check(string text, bool value)
            {
                var box = new CheckBox
                {
                    Text = text,
                    Checked = value,
                    AutoSize = true,
                    Location = new Point(12, y),
                    TabIndex = tab++,
                };
                box.AccessibleName = text.Replace("&", "");
                Controls.Add(box);
                return box;
            }

            _name = Field("&Name:", pair.Name, 260);
            y += 34;
            _local = Field("Folder on this &PC:", pair.LocalFolder, 250);
            var browseLocal = new Button { Text = "&Browse...", Location = new Point(480, y - 1), Size = new Size(108, 27), TabIndex = tab++ };
            Controls.Add(browseLocal);
            y += 34;
            _drive = Field("Folder in &Google Drive:", pair.DriveFolderPath, 250, readOnly: true);
            var browseDrive = new Button { Text = "C&hoose...", Location = new Point(480, y - 1), Size = new Size(108, 27), TabIndex = tab++ };
            Controls.Add(browseDrive);
            y += 34;

            _mode = Choice("&Direction:", Modes.Select(m => m.Text), Array.FindIndex(Modes, m => m.Mode == pair.Mode));
            _mode.Width = 368;
            y += 34;

            // What the direction does, with the person's own folders, next in
            // the tab order: arrow through Direction, Tab, and read the example.
            Controls.Add(new Label { Text = "E&xample:", AutoSize = true, Location = new Point(12, y) });
            _example = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(12, y + 22),
                Size = new Size(576, 84),
                AccessibleName = "Example",
                TabIndex = tab++,
            };
            Controls.Add(_example);
            y += 116;
            _clash = Choice("Wh&en a file changed in both places:", Clashes.Select(c => c.Text),
                Array.FindIndex(Clashes, c => c.Choice == pair.WhenBothChanged));
            y += 34;
            _maxSize = Choice("Skip files &larger than:", Sizes.Select(s => s.Text),
                Array.FindIndex(Sizes, s => s.Megabytes == pair.MaxFileMegabytes));
            y += 34;
            _skipTypes = Field("Skip file &types:", pair.SkipExtensions, 260);
            _skipTypes.AccessibleDescription = "File extensions never synced, separated by spaces, such as .iso .tmp";
            y += 36;

            _subfolders = Check("Include &subfolders", pair.IncludeSubfolders);
            y += 28;
            _deletes = Check("Als&o delete on the other side (to the Recycle Bin or the Drive trash)", pair.CopyDeletes);
            y += 28;
            _paused = Check("Pa&used", pair.Paused);
            y += 32;

            void ShowExample() => _example.Text =
                Example(Modes[Math.Max(0, _mode.SelectedIndex)].Mode, _local.Text, _drive.Text, _deletes.Checked);
            ShowExample();
            _mode.SelectedIndexChanged += (_, _) => ShowExample();
            _deletes.CheckedChanged += (_, _) => ShowExample();
            _local.TextChanged += (_, _) => ShowExample();
            _drive.TextChanged += (_, _) => ShowExample();

            // Before a first sync, what it will do: a read-only box in the tab
            // order, so it can be read line by line and gone back to. It used to
            // be a Yes, No, Cancel message box that vanished once answered.
            _summaryLabel = new Label { Text = "&What the first sync will do:", AutoSize = true, Location = new Point(12, y), Visible = false };
            _summary = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(12, y + 20),
                Size = new Size(576, 120),
                AccessibleName = "What the first sync will do",
                TabIndex = tab++,
                Visible = false,
            };
            Controls.Add(_summaryLabel);
            Controls.Add(_summary);
            y += 148;

            _status = new Label { AutoSize = false, Location = new Point(12, y), Size = new Size(576, 40) };
            Controls.Add(_status);
            y += 44;

            _ok = new Button { Text = "OK", Location = new Point(408, y), Size = new Size(84, 28), TabIndex = tab++ };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(504, y),
                Size = new Size(84, 28),
                TabIndex = tab++,
            };
            Controls.Add(_ok);
            Controls.Add(cancel);
            ClientSize = new Size(600, y + 40);
            AcceptButton = _ok;
            CancelButton = cancel;

            // Closing while Drive is being totalled up ends the check, and
            // nothing is saved: OK's handler sees the window closing and stops.
            // It used to refuse to close until a listing of a big Drive folder
            // finished, which could be minutes.
            FormClosing += (_, _) =>
            {
                if (DialogResult != DialogResult.OK) _closing.Cancel();
            };

            browseLocal.Click += (_, _) =>
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "Folder on this PC to monitor",
                    UseDescriptionForTitle = true,
                    SelectedPath = Directory.Exists(_local.Text) ? _local.Text : "",
                };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _local.Text = dialog.SelectedPath;
                    if (string.IsNullOrWhiteSpace(_name.Text)) _name.Text = Path.GetFileName(dialog.SelectedPath);
                }
                _local.Focus();
            };

            browseDrive.Click += (_, _) =>
            {
                var client = DriveMonitorClient();
                if (client == null)
                {
                    MessageBox.Show(this, "Connect Google Drive first, then choose a folder in it.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                using var picker = new DriveFolderPicker(client);
                if (picker.ShowDialog(this) == DialogResult.OK)
                {
                    Pair.DriveFolderId = picker.FolderId;
                    Pair.DriveFolderPath = picker.FolderPath;
                    _drive.Text = picker.FolderPath;
                }
                _drive.Focus();
            };

            _ok.Click += async (_, _) =>
            {
              // An async void handler: nothing may escape it, or the application ends.
              try
              {
                if (_checking) return;

                var problem = DriveMonitor.Refusal(_local.Text.Trim());
                if (problem != null)
                {
                    MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _local.Focus();
                    return;
                }
                if (string.IsNullOrEmpty(Pair.DriveFolderId))
                {
                    MessageBox.Show(this, "Choose a folder in Google Drive.", Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    browseDrive.Focus();
                    return;
                }

                var edited = Pair.Copy();
                edited.LocalFolder = Path.GetFullPath(_local.Text.Trim()).TrimEnd(Path.DirectorySeparatorChar);
                edited.Name = string.IsNullOrWhiteSpace(_name.Text) ? Path.GetFileName(edited.LocalFolder) : _name.Text.Trim();
                edited.Mode = Modes[Math.Max(0, _mode.SelectedIndex)].Mode;
                edited.WhenBothChanged = Clashes[Math.Max(0, _clash.SelectedIndex)].Choice;
                edited.MaxFileMegabytes = Sizes[Math.Max(0, _maxSize.SelectedIndex)].Megabytes;
                edited.SkipExtensions = _skipTypes.Text.Trim();
                edited.IncludeSubfolders = _subfolders.Checked;
                edited.CopyDeletes = _deletes.Checked;
                edited.Paused = _paused.Checked;

                // Two pairs over the same files would each upload them.
                if (DriveMonitor.Overlap(edited, Others) is { } overlap)
                {
                    MessageBox.Show(this, overlap, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _local.Focus();
                    return;
                }

                // Never start a sync that cannot finish: what would come down
                // against the PC's free space, what would go up against Drive's.
                // And never start a first sync (a new pair, or new folders) before
                // the person has seen what it would do and said yes.
                // The second OK on a summary already read starts it: the look is
                // not made again (on a big Drive it can take minutes), unless a
                // setting it depended on was changed since.
                bool seen = _previewedKey != null && _previewedKey == PlanKey(edited);
                if (!edited.Paused && !seen)
                {
                    bool first = IsFirstSync(_original, edited);
                    var (shortage, preview, failure) = await CheckAsync(edited);
                    if (IsDisposed || _closing.IsCancellationRequested) return;

                    if (first && preview == null)
                    {
                        var answer = MessageBox.Show(this,
                            "Explorer Native could not look at both folders first" +
                            (failure != null ? ": " + failure : ".") + "\n\n" +
                            "A first sync never starts without that look. Save this folder paused, and sync it later?",
                            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        if (answer != DialogResult.Yes) { _local.Focus(); return; }
                        edited.Paused = true;
                    }
                    else if (shortage != null)
                    {
                        var answer = MessageBox.Show(this,
                            shortage + "\n\nSave this folder paused, and sync it later when there is room?",
                            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        if (answer != DialogResult.Yes)
                        {
                            _status.Text = shortage;
                            _local.Focus();
                            return;
                        }
                        edited.Paused = true;
                    }
                    else if (first && preview != null)
                    {
                        ShowSummary(preview);
                        _previewedKey = PlanKey(edited);
                        return;
                    }
                }

                Pair.LocalFolder = edited.LocalFolder;
                Pair.Name = edited.Name;
                Pair.Mode = edited.Mode;
                Pair.WhenBothChanged = edited.WhenBothChanged;
                Pair.MaxFileMegabytes = edited.MaxFileMegabytes;
                Pair.SkipExtensions = edited.SkipExtensions;
                Pair.IncludeSubfolders = edited.IncludeSubfolders;
                Pair.CopyDeletes = edited.CopyDeletes;
                Pair.Paused = edited.Paused;
                DialogResult = DialogResult.OK;
                Close();
              }
              catch (Exception ex)
              {
                if (IsDisposed) return;
                _status.Text = "The folder pair could not be saved: " + ex.Message;
                MessageBox.Show(this, _status.Text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
              }
            };
        }

        /// <summary>Puts what the first sync will do in the box above OK, and moves there to read it.</summary>
        private void ShowSummary(DriveMonitor.SyncPreview preview)
        {
            _summary.Text = DriveMonitor.Describe(preview) + Environment.NewLine + Environment.NewLine +
                            "Nothing has been copied yet. Press OK to start syncing. " +
                            "To save it without starting, tick Paused, then press OK.";
            _summaryLabel.Visible = true;
            _summary.Visible = true;
            _status.Text = "Read what the first sync will do, then press OK to start it.";
            _summary.Focus();
            _summary.Select(0, 0);
        }

        /// <summary>The settings a summary depends on: change one and OK looks again.</summary>
        private static string PlanKey(DriveSyncPair p) =>
            string.Join("|", p.LocalFolder, p.DriveFolderId, p.Mode, p.WhenBothChanged, p.MaxFileMegabytes,
                p.SkipExtensions, p.IncludeSubfolders, p.CopyDeletes);

        /// <summary>
        /// Whether saving <paramref name="edited"/> starts a sync from nothing:
        /// a new pair, or an edit that changes either folder, which throws the
        /// records away (see DriveMonitor.StateFits).
        /// </summary>
        internal static bool IsFirstSync(DriveSyncPair original, DriveSyncPair edited) =>
            string.IsNullOrEmpty(original.LocalFolder) || string.IsNullOrEmpty(original.DriveFolderId) ||
            !string.Equals(original.LocalFolder.TrimEnd('\\'), edited.LocalFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(original.DriveFolderId, edited.DriveFolderId, StringComparison.Ordinal);

        /// <summary>
        /// Lists both sides through the API and plans the pair, reading and
        /// writing nothing else: what it would do (the preview), and, plainly,
        /// whether what it would copy does not fit. The preview is null when
        /// it could not be made, with the reason.
        /// </summary>
        private async Task<(string? Shortage, DriveMonitor.SyncPreview? Preview, string? Failure)> CheckAsync(DriveSyncPair pair)
        {
            var client = DriveMonitorClient();
            if (client == null) return (null, null, "Google Drive is not connected");

            _checking = true;
            _ok.Enabled = false;
            UseWaitCursor = true;
            _status.Text = "Looking at both folders before anything is copied…";
            Say(_status.Text);
            var progress = new Progress<string>(line => { if (!IsDisposed) _status.Text = line; });
            try
            {
                // On a worker: the PC folder walk and the free-space question
                // are file system calls, and the PC folder can be a big library
                // or a share that has gone away.
                var token = _closing.Token;
                var (preview, free, total, letter) = await Task.Run(async () =>
                {
                    var p = await DriveMonitor.PreviewAsync(client, pair, token, ((IProgress<string>)progress).Report)
                        .ConfigureAwait(false);
                    var (f, t, l) = p.DownBytes > 0 ? DriveMonitor.DiskOf(pair.LocalFolder) : (0L, 0L, "");
                    return (p, f, t, l);
                }, token);
                if (IsDisposed || token.IsCancellationRequested) return (null, null, null);

                // The same numbers, in one line under the fields.
                _status.Text = DriveMonitor.Describe(preview).Replace(Environment.NewLine, " ");

                if (preview.DownBytes > 0 && total > 0 && !SyncSpace.Fits(preview.DownBytes, free, total))
                    return ($"This needs {DriveMonitor.Words(preview.DownBytes)} and {letter} has " +
                            $"{DriveMonitor.Words(free)} free, keeping {DriveMonitor.Words(SyncSpace.Margin(total))} spare.",
                        preview, null);

                var drive = DriveMonitor.Current?.Drive;
                if (preview.UpBytes > 0 && drive is { QuotaLimit: > 0 })
                {
                    long room = drive.QuotaLimit - drive.QuotaUsed;
                    if (preview.UpBytes > room)
                        return ($"This needs {DriveMonitor.Words(preview.UpBytes)} in Google Drive, which has " +
                                $"{DriveMonitor.Words(Math.Max(0, room))} free.", preview, null);
                }
                return (null, preview, null);
            }
            catch (OperationCanceledException) when (_closing.IsCancellationRequested)
            {
                return (null, null, null);
            }
            catch (Exception ex)
            {
                // Not knowing the space is not a reason to refuse an edit: every
                // pass checks again before each file and pauses itself if it runs
                // out of room. A first sync is another matter: see the caller.
                var reason = GoogleNet.Reason(ex);
                if (!IsDisposed) _status.Text = "Could not look at both folders: " + reason;
                return (null, null, reason);
            }
            finally
            {
                _checking = false;
                if (!IsDisposed)
                {
                    _ok.Enabled = true;
                    UseWaitCursor = false;
                }
            }
        }

        private static void Say(string text)
        {
            try { Speech.Speak(text, interrupt: true); } catch { }
        }

        /// <summary>The live Drive client, through the tray's drive. Null when Drive is not connected.</summary>
        internal static Func<DriveClient?> DriveMonitorClient = () => null;
    }

    /// <summary>
    /// Chooses a folder in Google Drive, through the API. Enter opens the
    /// selected folder, Backspace goes up, Use this folder picks the one you
    /// are in, and New folder makes one there.
    /// </summary>
    internal sealed class DriveFolderPicker : Form
    {
        private readonly DriveClient _client;
        private readonly ListBox _list;
        private readonly Label _where;
        private readonly Stack<(string Id, string Name)> _path = new();
        private readonly CancellationTokenSource _cancel = new();
        private List<DriveEntry> _folders = new();

        public string FolderId => _path.Peek().Id;
        public string FolderPath => string.Join("/", _path.Reverse().Select(p => p.Name));

        public DriveFolderPicker(DriveClient client)
        {
            _client = client;

            Text = "Choose a folder in Google Drive";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 380);
            Font = SystemFonts.MessageBoxFont;

            _where = new Label { Text = "Loading…", AutoSize = true, Location = new Point(12, 12) };
            _list = new ListBox
            {
                Location = new Point(12, 34),
                Size = new Size(436, 290),
                AccessibleName = "Folders",
                TabIndex = 0,
            };
            var use = new Button { Text = "&Use this folder", Location = new Point(12, 338), Size = new Size(130, 30), TabIndex = 1 };
            var make = new Button { Text = "&New folder...", Location = new Point(150, 338), Size = new Size(120, 30), TabIndex = 2 };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(364, 338),
                Size = new Size(84, 30),
                TabIndex = 3,
            };
            Controls.AddRange(new Control[] { _where, _list, use, make, cancel });
            CancelButton = cancel;

            _list.KeyDown += async (_, e) =>
            {
              try
              {
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    int at = _list.SelectedIndex;
                    if (at >= 0 && at < _folders.Count)
                    {
                        _path.Push((_folders[at].Id, _folders[at].Name));
                        await LoadAsync();
                    }
                }
                else if (e.KeyCode == Keys.Back)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    if (_path.Count > 1)
                    {
                        _path.Pop();
                        await LoadAsync();
                    }
                }
              }
              catch (Exception ex)
              {
                if (!IsDisposed) { _where.Text = "Google Drive could not be read: " + ex.Message; Say(_where.Text); }
              }
            };

            use.Click += (_, _) =>
            {
                if (_path.Count == 0) return;
                DialogResult = DialogResult.OK;
                Close();
            };

            make.Click += async (_, _) =>
            {
                if (_path.Count == 0) return;
                var name = PromptForm.Ask(this, "New folder", "Name of the new folder in Google Drive:");
                if (name == null) return;
                try
                {
                    var id = await _client.CreateFolder(name.Trim(), FolderId, _cancel.Token);
                    _path.Push((id, name.Trim()));
                    await LoadAsync();
                }
                catch (Exception ex)
                {
                    if (!IsDisposed)
                        MessageBox.Show(this, "The folder could not be made: " + ex.Message, Text,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            Shown += async (_, _) =>
            {
                try
                {
                    var root = await _client.RootId(_cancel.Token);
                    _path.Push((root, "My Drive"));
                    await LoadAsync();
                }
                catch (Exception ex)
                {
                    if (!IsDisposed) _where.Text = "Google Drive could not be read: " + ex.Message;
                    Say(_where.Text);
                }
            };
            FormClosed += (_, _) =>
            {
                _cancel.Cancel();
                _cancel.Dispose();
            };
        }

        private async Task LoadAsync()
        {
            _where.Text = "Loading " + FolderPath + "…";
            _list.Items.Clear();
            try
            {
                var entries = await _client.ListChildren(FolderId, _cancel.Token);
                if (IsDisposed) return;
                _folders = entries.Where(e => e.IsFolder).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                _where.Text = "Could not read " + FolderPath + ": " + ex.Message;
                Say(_where.Text);
                return;
            }

            _where.Text = FolderPath;
            _list.AccessibleName = FolderPath;
            if (_folders.Count == 0)
            {
                _list.Items.Add("No folders in here. Use this folder, or make a new one.");
            }
            else
            {
                foreach (var folder in _folders) _list.Items.Add(folder.Name);
            }
            _list.SelectedIndex = 0;
            _list.Focus();
            Say(_folders.Count == 0 ? $"{FolderPath}, no folders" : $"{FolderPath}, {Plural(_folders.Count)}");
        }

        private static string Plural(int n) => n == 1 ? "1 folder" : $"{n} folders";

        private static void Say(string text)
        {
            try { Speech.Speak(text, interrupt: true); } catch { }
        }
    }
}
