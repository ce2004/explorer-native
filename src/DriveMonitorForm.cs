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
    /// File, Google Drive sync (while Drive is on): the synced folders, one
    /// sentence each, the buttons for the chosen one beside them (Add a folder,
    /// Change, Pause or Resume, Remove, Sync now, History), its Status below.
    /// Changes are saved the moment they are made.
    /// </summary>
    internal sealed class DriveMonitorForm : Form
    {
        private readonly List<DriveSyncPair> _working;
        private readonly List<DriveSyncPair> _pairs;
        private readonly LineList _list;
        private readonly LineList _details;
        private readonly Button _edit, _remove, _sync, _history, _pause;
        private readonly DriveMonitor? _monitor;

        /// <param name="working">Preferences' own copy of the pairs, kept in step.</param>
        public DriveMonitorForm(List<DriveSyncPair> working)
        {
            _working = working;
            _pairs = working.Select(p => p.Copy()).ToList();
            _monitor = DriveMonitor.Current;

            Text = "Google Drive sync";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(720, 500);
            Font = SystemFonts.MessageBoxFont;

            var label = new Label { Text = "&Synced folders:", AutoSize = true, Location = new Point(12, 12) };
            _list = new LineList
            {
                Location = new Point(12, 34),
                Size = new Size(540, 196),
                AccessibleName = "Synced folders",
                TabIndex = 1,
            };

            // What can be done with the folder chosen, one button under another
            // beside the list; Tab from the list goes through them in this order.
            var side = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Location = new Point(566, 34),
                Size = new Size(142, 236),
                TabIndex = 2,
            };
            Button Side(string text, int gapAbove = 0)
            {
                var button = new Button
                {
                    Text = text, Size = new Size(142, 32), Margin = new Padding(0, gapAbove, 0, 6),
                    TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0),
                };
                side.Controls.Add(button);
                return button;
            }
            var add = Side("&Add a folder...");
            add.Font = new Font(Font, FontStyle.Bold);
            _edit = Side("&Change...");
            _pause = Side("&Pause");
            _remove = Side("&Remove");
            _sync = Side("Sync &now", gapAbove: 8);
            _history = Side("&History...");

            // The chosen folder's sync, one fact a line, so the arrows go
            // through it instead of one sentence too long to follow.
            var detailsLabel = new Label { Text = "S&tatus:", AutoSize = true, Location = new Point(12, 240) };
            _details = new LineList
            {
                Location = new Point(12, 262),
                Size = new Size(540, 180),
                AccessibleName = "Status",
                TabIndex = 3,
            };

            var info = new Label
            {
                Text = "Changes are saved straight away. Removing a folder never deletes any files.",
                AutoSize = true,
                Location = new Point(12, 458),
            };
            var close = new Button
            {
                Text = "Close", DialogResult = DialogResult.Cancel,
                Location = new Point(608, 452), Size = new Size(100, 32), TabIndex = 4,
            };

            add.Click += (_, _) => AddPair();
            _edit.Click += (_, _) => EditPair();
            _pause.Click += (_, _) => PauseOrResume();
            _pause.Leave += (_, _) => BeginInvoke(ShowButtons);
            _remove.Click += (_, _) => RemovePair();
            _history.Click += (_, _) => ShowHistory();
            _sync.Click += (_, _) =>
            {
                if (_monitor == null) { Say("Google Drive sync is not running"); return; }
                Say(_monitor.SyncAllNow());
            };

            _list.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter && _pairs.Count > 0) { EditPair(); e.Handled = e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Delete && _pairs.Count > 0) { RemovePair(); e.Handled = e.SuppressKeyPress = true; }
            };

            _list.SelectedIndexChanged += (_, _) => { if (_list.SelectedIndex >= 0) { FillDetails(); ShowButtons(); } };

            Controls.AddRange(new Control[] { label, _list, side, detailsLabel, _details, info, close });
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

        /// <summary>The buttons that need a folder, and Pause or Resume as the chosen folder is.</summary>
        private void ShowButtons()
        {
            int at = _list.SelectedIndex;
            bool any = at >= 0 && at < _pairs.Count;
            _edit.Enabled = _remove.Enabled = _history.Enabled = _pause.Enabled = any;
            var text = any && _pairs[at].Paused ? "Res&ume" : "&Pause";
            // Never rename the focused control: NVDA talks over the message. It is
            // renamed when focus leaves (see the Leave handler).
            if (_pause.Text != text && !_pause.Focused) _pause.Text = text;
        }

        /// <summary>Pause or Resume for the chosen folder, saved at once.</summary>
        private void PauseOrResume()
        {
            int at = _list.SelectedIndex;
            if (at < 0 || at >= _pairs.Count) return;
            var pair = _pairs[at];
            pair.Paused = !pair.Paused;
            Commit();
            _list.SelectedIndex = at;
            FillDetails();
            ShowButtons();
            Say(pair.Paused
                ? $"Paused {DriveMonitor.DisplayName(pair)}. Nothing syncs until you resume it."
                : $"Resumed {DriveMonitor.DisplayName(pair)}. Syncing now.");
        }


        /// <param name="all">After a folder is added, edited or removed: every line, the one being read too, since the rows have moved.</param>
        private void Fill(bool all = false)
        {
            var rows = _pairs.Count == 0
                ? new List<string> { "No folders are synced yet. Press Add a folder to choose one." }
                : _pairs.Select(p => Describe(p, _monitor?.StatusIfLoaded(p.Id), DateTime.UtcNow)).ToList();

            _list.SetLines(rows, all);

            FillDetails();
            ShowButtons();
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
            if (pair.Paused) lines.Add("Paused. To start it again, press Resume");
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
                // Only the file that started most recently (the list is oldest
                // first), at Conner's request: ten lines of files was clutter.
                foreach (var f in live.Files.Skip(Math.Max(0, live.Files.Count - MovingShown)))
                    // How far along only for a file big enough to take a while.
                    lines.Add($"{(f.Up ? "Uploading" : "Downloading")} {f.Path.Replace('/', '\\')}" +
                              (f.Size >= 1024 * 1024 ? $", {DriveMonitor.Words(f.Done)} of {DriveMonitor.Words(f.Size)}" : ""));
                int more = live.Files.Count - MovingShown;
                if (more > 0) lines.Add(more == 1 ? "And 1 more file moving" : $"And {more:N0} more files moving");
            }
            return lines;
        }

        /// <summary>Files named in Status (the newest); the rest are counted.</summary>
        internal const int MovingShown = 1;

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

        private bool _adding;

        private async void AddPair()
        {
          // Everything waits on Google; a second Add meanwhile would make every pair twice.
          if (_adding) return;
          _adding = true;
          // An async void handler: nothing may escape it, or the application ends.
          try
          {
            var pair = new DriveSyncPair();
            using var dialog = new DrivePairForm(pair, "Add a synced folder") { Others = _pairs.ToList() };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            if (dialog.Pair.DriveFolderId == DriveFolderPicker.EverythingId)
            {
                var made = await EverythingAsync(dialog.Pair);
                if (made == null || IsDisposed) return;
                _pairs.AddRange(made);
                Commit();
                ShowSaved(_pairs.Count - made.Count);
                return;
            }

            _pairs.Add(dialog.Pair);
            Commit();
            ShowSaved(_pairs.Count - 1);
          }
          catch (Exception ex) { Say("The folder could not be added: " + ex.Message); }
          finally { _adding = false; }
        }

        /// <summary>
        /// Everything: My Drive into "My Drive" in the chosen PC folder, and
        /// each shared drive into "Shared drives\name", each its own synced
        /// folder with the settings chosen. One synced folder per drive, because
        /// each drive has its own list of changes in Google. A shared drive
        /// added later is added the same way, with Add a folder.
        /// </summary>
        private async Task<List<DriveSyncPair>?> EverythingAsync(DriveSyncPair chosen)
        {
            var client = DrivePairForm.DriveMonitorClient();
            if (client == null) { Say("Google Drive is not connected, so nothing was added."); return null; }
            Say("Finding your shared drives");
            using var quick = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var root = await client.RootId(quick.Token);
            var shared = await client.ListSharedDrives(quick.Token, interactive: true);

            var baseName = DriveMonitor.DisplayName(chosen);
            DriveSyncPair Make(string id, string drivePath, string under, string name)
            {
                var made = chosen.Copy();
                made.Id = Guid.NewGuid().ToString("N");
                made.DriveFolderId = id;
                made.DriveFolderPath = drivePath;
                made.LocalFolder = Path.Combine(chosen.LocalFolder, under);
                made.Name = $"{baseName}: {name}";
                return made;
            }
            var pairs = new List<DriveSyncPair> { Make(root, "My Drive", "My Drive", "My Drive") };
            // Google lets two shared drives share a name, and Windows folds case
            // and swaps characters, so the second "Team" becomes "Team (2)" on
            // both sides rather than the two overlapping and nothing being added.
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var drive in shared.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                string name = drive.Name, folder = WindowsName(name);
                for (int n = 2; !taken.Add(folder); n++) { name = $"{drive.Name} ({n})"; folder = WindowsName(name); }
                pairs.Add(Make(drive.Id, "Shared drives/" + name, Path.Combine("Shared drives", folder), name));
            }

            var others = _pairs.ToList();
            foreach (var p in pairs)
            {
                if (DriveMonitor.Overlap(p, others) is { } overlap)
                {
                    MessageBox.Show(this, overlap, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }
                others.Add(p);
            }
            await Task.Run(() => { foreach (var p in pairs) Directory.CreateDirectory(p.LocalFolder); });
            return pairs;
        }

        /// <summary>A shared drive's name as a folder on the PC: what Windows refuses in a name becomes "_".</summary>
        internal static string WindowsName(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            var safe = new string(name.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return safe.Length == 0 ? "Shared drive" : safe;
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
                var name = DriveMonitor.DisplayName(_pairs[at]);
                Speech.Speak(_pairs[at].Paused
                    ? $"Saved {name}, paused. Press Resume to start it."
                    : $"Saved {name}. Syncing has started, and Status shows how it is going.");
            }
            catch { }
        }

        private void EditPair()
        {
            int at = _list.SelectedIndex;
            if (at < 0 || at >= _pairs.Count) return;
            using var dialog = new DrivePairForm(_pairs[at].Copy(), "Change a synced folder")
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
    /// Add or change one synced folder, in three groups NVDA names as Tab
    /// enters them: Folders, How it syncs, Options. The main button says what
    /// it will do: Start syncing for a new folder, Save otherwise. It saves
    /// and closes at once; the sync window then lands on the folder and its
    /// Status list shows the first sync as it goes. Running out of room is not
    /// asked about here: every pass checks before each file and pauses itself,
    /// saying so in Status.
    /// </summary>
    internal sealed class DrivePairForm : Form
    {
        private readonly TextBox _name, _local, _drive, _skipTypes, _example;
        private readonly ComboBox _mode, _maxSize, _clash;
        private readonly CheckBox _deletes, _paused, _subfolders;
        private readonly Button _ok;

        public DriveSyncPair Pair { get; }

        private static readonly (SyncMode Mode, string Text)[] Modes =
        {
            (SyncMode.TwoWay, "2-way mirror: both folders stay the same"),
            (SyncMode.UploadOnly, "1-way mirror: this PC to Google Drive, a backup"),
            (SyncMode.DownloadOnly, "1-way mirror: Google Drive to this PC"),
        };

        /// <summary>
        /// What the chosen direction means, in plain sentences with the
        /// person's own folders: "Add or change a file in the Music folder on
        /// this PC, and within a minute the same change is made in the music
        /// folder in Google Drive." Rewritten whenever the direction, either
        /// folder or the delete box changes.
        /// </summary>
        internal static string Example(SyncMode mode, string localFolder, string drivePath, bool deletes)
        {
            var trimmed = localFolder.Trim();
            var leaf = Path.GetFileName(trimmed.TrimEnd('\\', '/'));
            var pc = !string.IsNullOrEmpty(leaf) ? $"the {leaf} folder on this PC"
                : trimmed.Length >= 2 && trimmed[1] == ':' ? $"the {char.ToUpperInvariant(trimmed[0])} drive"
                : "the folder on this PC";
            var driveLeaf = drivePath.Trim().TrimEnd('/');
            driveLeaf = driveLeaf[(driveLeaf.LastIndexOf('/') + 1)..];
            var drive = drivePath == EverythingText ? "Google Drive"
                : driveLeaf == "My Drive" ? "My Drive in Google Drive"
                : string.IsNullOrEmpty(driveLeaf) ? "the folder in Google Drive"
                : $"the {driveLeaf} folder in Google Drive";

            return mode switch
            {
                SyncMode.UploadOnly =>
                    $"Add or change a file in {pc}, and within a minute it is copied to {drive}. " +
                    "Nothing is ever copied from Google Drive to this PC. " +
                    (deletes
                        ? "Delete a file on this PC, and it is moved to the Google Drive trash too. "
                        : "Delete a file on this PC, and Google Drive keeps its copy. ") +
                    "If a file is deleted in Google Drive, it is copied back from this PC.",
                SyncMode.DownloadOnly =>
                    $"Add or change a file in {drive}, and within a minute it is copied to {pc}. " +
                    "Nothing is ever copied from this PC to Google Drive. " +
                    (deletes
                        ? "Delete a file in Google Drive, and it is moved to the Recycle Bin on this PC too. "
                        : "Delete a file in Google Drive, and this PC keeps its copy. ") +
                    "If you delete a file on this PC, it is copied back from Google Drive.",
                _ =>
                    $"Add or change a file in {pc}, and within a minute the same change is made in {drive}. " +
                    "It works the other way too. " +
                    (deletes
                        ? "Delete a file on either side, and it is deleted on the other side too. " +
                          "You can get it back from the Recycle Bin or the Google Drive trash."
                        : "If you delete a file on one side, it is copied back from the other. " +
                          "To delete files on both sides, tick Delete on both sides."),
            };
        }

        /// <summary>What the Google Drive field says when Everything was chosen.</summary>
        internal const string EverythingText = "Everything: My Drive and every shared drive";

        private static readonly (int Megabytes, string Text)[] Sizes =
        {
            (0, "No limit"), (100, "100 megabytes"), (500, "500 megabytes"), (1024, "1 gigabyte"), (4096, "4 gigabytes"),
        };

        /// <summary>
        /// The size choices for a pair whose limit is <paramref name="current"/>:
        /// the usual ones, plus that limit when it is none of them (set in
        /// settings.json). Without it the list showed "No limit", and saving any
        /// other change took the limit away.
        /// </summary>
        internal static (int Megabytes, string Text)[] SizeChoices(int current) =>
            current <= 0 || Sizes.Any(s => s.Megabytes == current)
                ? Sizes
                : Sizes.Append((Megabytes: current, Text: $"{current:N0} megabytes")).OrderBy(s => s.Megabytes).ToArray();

        private readonly (int Megabytes, string Text)[] _sizes;

        private static readonly (ConflictChoice Choice, string Text)[] Clashes =
        {
            (ConflictChoice.KeepBoth, "Keep both copies"),
            (ConflictChoice.PcWins, "Keep this PC's copy"),
            (ConflictChoice.DriveWins, "Keep Google Drive's copy"),
        };

        /// <summary>The other synced folders, which this one may not overlap.</summary>
        public List<DriveSyncPair> Others { get; init; } = new();

        /// <summary>Whether this is a folder being added, for the main button's words.</summary>
        private readonly bool _adding;

        public DrivePairForm(DriveSyncPair pair, string title)
        {
            Pair = pair;
            _adding = string.IsNullOrEmpty(pair.LocalFolder) && string.IsNullOrEmpty(pair.DriveFolderId);

            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            // The page sits 12 in from the top left; this is the same on the right and below.
            Padding = new Padding(0, 0, 12, 12);

            // Each group is a table: a label, its field, and a button where there is one.
            int groupTab = 0;
            (GroupBox Box, TableLayoutPanel Grid) Group(string text)
            {
                var grid = new TableLayoutPanel
                {
                    ColumnCount = 3,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Fill,
                    Padding = new Padding(6, 4, 6, 6),
                };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                var box = new GroupBox
                {
                    Text = text,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Fill,
                    TabIndex = groupTab++,
                    Padding = new Padding(8),
                    Margin = new Padding(0, 0, 0, 10),
                };
                box.Controls.Add(grid);
                return (box, grid);
            }

            Label Caption(string text) => new()
            {
                Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 12, 3),
            };

            TextBox Field(TableLayoutPanel grid, string label, string value, bool readOnly = false)
            {
                var box = new TextBox
                {
                    Text = value, ReadOnly = readOnly, Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4),
                    AccessibleName = label.Replace("&", "").TrimEnd(':'),
                };
                grid.Controls.Add(Caption(label));
                grid.Controls.Add(box);
                return box;
            }

            ComboBox Choice(TableLayoutPanel grid, string label, IEnumerable<string> items, int selected)
            {
                var combo = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4),
                    AccessibleName = label.Replace("&", "").TrimEnd(':'),
                };
                foreach (var item in items) combo.Items.Add(item);
                combo.SelectedIndex = Math.Max(0, selected);
                grid.Controls.Add(Caption(label));
                grid.Controls.Add(combo);
                return combo;
            }

            Button Side(TableLayoutPanel grid, string text)
            {
                var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(110, 28), Margin = new Padding(6, 3, 3, 3) };
                grid.Controls.Add(button);
                return button;
            }

            void Skip(TableLayoutPanel grid) => grid.Controls.Add(new Label { AutoSize = true });

            CheckBox Tick(TableLayoutPanel grid, string text, bool value)
            {
                var box = new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
                box.AccessibleName = text.Replace("&", "");
                grid.Controls.Add(box);
                grid.SetColumnSpan(box, 3);
                return box;
            }

            // ---- Folders ----
            var (folders, f) = Group("Folders");
            _name = Field(f, "&Name:", pair.Name);
            Skip(f);
            _local = Field(f, "Folder on this &PC:", pair.LocalFolder);
            var browseLocal = Side(f, "&Browse...");
            _drive = Field(f, "Folder in &Google Drive:", pair.DriveFolderPath, readOnly: true);
            var browseDrive = Side(f, "C&hoose...");

            // ---- How it syncs ----
            var (how, h) = Group("How it syncs");
            _mode = Choice(h, "&Direction:", Modes.Select(m => m.Text), Array.FindIndex(Modes, m => m.Mode == pair.Mode));
            Skip(h);
            h.Controls.Add(Caption("&What this means:"));
            _example = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 96,
                Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4), AccessibleName = "What this means",
            };
            h.Controls.Add(_example);
            h.SetColumnSpan(_example, 2);
            _deletes = Tick(h, "Delete on b&oth sides: a file I delete is deleted on the other side too", pair.CopyDeletes);
            _clash = Choice(h, "If a file &changed in both places:", Clashes.Select(c => c.Text),
                Array.FindIndex(Clashes, c => c.Choice == pair.WhenBothChanged));
            Skip(h);

            // ---- Options ----
            var (options, o) = Group("Options");
            _subfolders = Tick(o, "Include &subfolders", pair.IncludeSubfolders);
            _sizes = SizeChoices(pair.MaxFileMegabytes);
            _maxSize = Choice(o, "Skip files &larger than:", _sizes.Select(s => s.Text),
                Array.FindIndex(_sizes, s => s.Megabytes == pair.MaxFileMegabytes));
            Skip(o);
            _skipTypes = Field(o, "Skip file and folder &types:", pair.SkipExtensions);
            _skipTypes.AccessibleDescription =
                "Endings or names never synced, separated by spaces, such as .mp3 .sync. Each one skips files and folders " +
                "with that ending or name, and everything inside a skipped folder";
            Skip(o);
            _paused = Tick(o, "Pa&use this folder for now", pair.Paused);

            // ---- the buttons ----
            _ok = new Button { AutoSize = true, MinimumSize = new Size(120, 32), Margin = new Padding(6, 2, 0, 0) };
            var cancel = new Button
            {
                Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(96, 32),
                Margin = new Padding(6, 2, 0, 0),
            };
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.Right, Margin = new Padding(0),
            };
            // Laid out right to left, so Cancel sits at the far right; Tab goes
            // to the main button first all the same.
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_ok);
            _ok.TabIndex = 0;
            cancel.TabIndex = 1;

            // One column: the three groups, then the buttons at the right.
            var page = new TableLayoutPanel
            {
                ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Location = new Point(12, 12), Margin = new Padding(0),
            };
            page.Controls.Add(folders);
            page.Controls.Add(how);
            page.Controls.Add(options);
            page.Controls.Add(buttons);
            Controls.Add(page);
            AcceptButton = _ok;
            CancelButton = cancel;

            void ShowExample() => _example.Text =
                Example(Modes[Math.Max(0, _mode.SelectedIndex)].Mode, _local.Text, _drive.Text, _deletes.Checked);
            void ShowOk() => _ok.Text = _adding && !_paused.Checked ? "&Start syncing" : "&Save";
            ShowExample();
            ShowOk();
            _mode.SelectedIndexChanged += (_, _) => ShowExample();
            _deletes.CheckedChanged += (_, _) => ShowExample();
            _local.TextChanged += (_, _) => ShowExample();
            _drive.TextChanged += (_, _) => ShowExample();
            _paused.CheckedChanged += (_, _) => ShowOk();

            // The folder typed so far is looked at on a worker, for three seconds
            // at most: Directory.Exists on a share that had gone away held the
            // window until Windows gave up. One press at a time.
            bool browsing = false;
            browseLocal.Click += async (_, _) =>
            {
                if (browsing) return;
                browsing = true;
                try
                {
                    var typed = _local.Text;
                    var start = await FolderIfThereAsync(typed);
                    if (IsDisposed) return;
                    using var dialog = new FolderBrowserDialog
                    {
                        Description = "Folder on this PC to sync",
                        UseDescriptionForTitle = true,
                        SelectedPath = start,
                    };
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        _local.Text = dialog.SelectedPath;
                        if (string.IsNullOrWhiteSpace(_name.Text)) _name.Text = Path.GetFileName(dialog.SelectedPath);
                    }
                    _local.Focus();
                }
                catch { }
                finally { browsing = false; }
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
                    if (picker.Everything && !_adding)
                    {
                        MessageBox.Show(this, "Everything can be chosen when adding a synced folder, not when changing one.",
                            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else if (picker.Everything)
                    {
                        // Made into one synced folder per drive when it is saved (DriveMonitorForm.AddPair).
                        Pair.DriveFolderId = DriveFolderPicker.EverythingId;
                        Pair.DriveFolderPath = EverythingText;
                        _drive.Text = EverythingText;
                    }
                    else
                    {
                        Pair.DriveFolderId = picker.FolderId;
                        Pair.DriveFolderPath = picker.FolderPath;
                        _drive.Text = picker.FolderPath;
                    }
                }
                _drive.Focus();
            };

            // Refusal looks at the folder on disk (Directory.Exists), which on a
            // share that has gone away held the window for as long as Windows
            // took to give up. So it runs on a worker; one press at a time.
            bool checking = false;
            _ok.Click += async (_, _) =>
            {
                if (checking) return;
                checking = true;
                try
                {
                    var typed = _local.Text.Trim();
                    var problem = await Task.Run(() => DriveMonitor.Refusal(typed));
                    if (IsDisposed) return;
                    if (problem != null)
                    {
                        MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        _local.Focus();
                        return;
                    }
                    if (string.IsNullOrEmpty(Pair.DriveFolderId))
                    {
                        MessageBox.Show(this, "Choose a folder in Google Drive.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        browseDrive.Focus();
                        return;
                    }

                    var edited = Pair.Copy();
                    // A whole drive stays "D:\" (see FolderPathOf).
                    edited.LocalFolder = DriveMonitor.FolderPathOf(typed);
                    edited.Name = "";
                    edited.Name = string.IsNullOrWhiteSpace(_name.Text) ? DriveMonitor.DisplayName(edited) : _name.Text.Trim();
                    edited.Mode = Modes[Math.Max(0, _mode.SelectedIndex)].Mode;
                    edited.WhenBothChanged = Clashes[Math.Max(0, _clash.SelectedIndex)].Choice;
                    edited.MaxFileMegabytes = _sizes[Math.Max(0, _maxSize.SelectedIndex)].Megabytes;
                    edited.SkipExtensions = _skipTypes.Text.Trim();
                    edited.IncludeSubfolders = _subfolders.Checked;
                    edited.CopyDeletes = _deletes.Checked;
                    edited.Paused = _paused.Checked;

                    // Two synced folders over the same files would each upload them.
                    // (Everything is checked once it is made into its folders, in AddPair.)
                    if (edited.DriveFolderId != DriveFolderPicker.EverythingId && DriveMonitor.Overlap(edited, Others) is { } overlap)
                    {
                        MessageBox.Show(this, overlap, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        _local.Focus();
                        return;
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
                    MessageBox.Show(this, "The folder could not be saved: " + ex.Message, Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally { checking = false; }
            };
        }

        /// <summary>The live Drive client, through the tray's drive. Null when Drive is not connected.</summary>
        internal static Func<DriveClient?> DriveMonitorClient = () => null;

        /// <summary>
        /// <paramref name="folder"/> when it is there, otherwise "": asked on a
        /// worker, and given up on after <paramref name="timeoutMs"/> (a share
        /// that does not answer counts as not there). The worker is never
        /// waited on after that.
        /// </summary>
        internal static async Task<string> FolderIfThereAsync(string folder, int timeoutMs = 3000)
        {
            if (string.IsNullOrWhiteSpace(folder)) return "";
            var probe = Task.Run(() =>
            {
                try { return Directory.Exists(folder); }
                catch { return false; }
            });
            var finished = await Task.WhenAny(probe, Task.Delay(timeoutMs));
            return finished == probe && probe.Result ? folder : "";
        }
    }

    /// <summary>
    /// Chooses a folder in Google Drive, through the API. It opens on Google
    /// Drive itself: My Drive, each shared drive, and Everything (My Drive and
    /// every shared drive, each its own synced folder). Enter opens the
    /// selected folder, Backspace goes up, Use this folder picks the one you
    /// are in (at the top, the one selected), and New folder makes one there.
    /// </summary>
    internal sealed class DriveFolderPicker : Form
    {
        private readonly DriveClient _client;
        private readonly ListBox _list;
        private readonly Label _where;
        private readonly Stack<(string Id, string Name)> _path = new();
        private readonly CancellationTokenSource _cancel = new();
        private List<DriveEntry> _folders = new();

        /// <summary>Stands for My Drive and every shared drive at once (see DriveMonitorForm.AddPair).</summary>
        public const string EverythingId = "*everything*";

        private const string FolderMime = "application/vnd.google-apps.folder";

        /// <summary>Whether Everything was chosen rather than one folder.</summary>
        public bool Everything { get; private set; }

        /// <summary>At the top, Google Drive itself, which is not a folder.</summary>
        private bool AtTop => _path.Count > 0 && _path.Peek().Id.Length == 0;

        public string FolderId => _path.Peek().Id;

        /// <summary>"My Drive/Music" or "Shared drives/Team/Music": the top is not part of it.</summary>
        public string FolderPath => string.Join("/", _path.Reverse().Where(p => p.Id.Length > 0).Select(p => p.Name));

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
                // While a folder is loading, the list and the path disagree: a
                // second Backspace or Enter then started another load, and the
                // one that finished last filled the list, so the folder chosen
                // could be another than the one read out. One load at a time.
                if (_loading && e.KeyCode is Keys.Enter or Keys.Back)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    return;
                }
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    int at = _list.SelectedIndex;
                    if (at >= 0 && at < _folders.Count && _folders[at].Id == EverythingId) Choose(at);
                    else if (at >= 0 && at < _folders.Count)
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
                if (_path.Count == 0 || _loading) return;
                if (AtTop) { Choose(_list.SelectedIndex); return; }
                DialogResult = DialogResult.OK;
                Close();
            };

            make.Click += async (_, _) =>
            {
                if (_path.Count == 0 || _loading) return;
                if (AtTop) { Say("Open My Drive or a shared drive first, then make the folder in it."); return; }
                var name = PromptForm.Ask(this, "New folder", "Name of the new folder in Google Drive:");
                if (name == null) return;
                try
                {
                    // Held like a load: Backspace meanwhile put the new folder
                    // under the wrong parent in the path.
                    string id;
                    _loading = true;
                    try { id = await _client.CreateFolder(name.Trim(), FolderId, _cancel.Token); }
                    finally { _loading = false; }
                    if (IsDisposed) return;
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
                    _path.Push(("", "Google Drive"));
                    await LoadAsync();
                }
                catch (Exception ex)
                {
                    if (!IsDisposed) _where.Text = "Google Drive could not be read: " + ex.Message;
                    Say(_where.Text);
                }
            };
            // Cancelled, never disposed (CLAUDE.md): a load still awaiting asks
            // for its token after this, and a disposed source threw there.
            FormClosed += (_, _) => _cancel.Cancel();
        }

        /// <summary>A folder is being read: Enter, Backspace, Use and New wait for it.</summary>
        private bool _loading;

        private async Task LoadAsync()
        {
            _loading = true;
            try { await LoadCoreAsync(); }
            finally { _loading = false; }
        }

        private async Task LoadCoreAsync()
        {
            _where.Text = "Loading " + FolderPath + "…";
            _list.Items.Clear();
            // The list being read is of no folder until the new one is in.
            _folders = new List<DriveEntry>();
            try
            {
                if (AtTop)
                {
                    // My Drive, the shared drives, and all of them at once.
                    var root = await _client.RootId(_cancel.Token);
                    List<DriveEntry> shared;
                    try { shared = await _client.ListSharedDrives(_cancel.Token, interactive: true); }
                    catch (Exception) when (!_cancel.IsCancellationRequested) { shared = new List<DriveEntry>(); }
                    if (IsDisposed) return;
                    _folders = new List<DriveEntry> { new(root, "My Drive", FolderMime, 0, default) };
                    _folders.AddRange(shared.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                        .Select(d => new DriveEntry(d.Id, "Shared drives/" + d.Name, FolderMime, 0, default, d.Id)));
                    _folders.Add(new DriveEntry(EverythingId,
                        shared.Count == 0 ? "Everything: My Drive" : "Everything: My Drive and every shared drive", FolderMime, 0, default));
                }
                else
                {
                    var entries = await _client.ListChildren(FolderId, _cancel.Token);
                    if (IsDisposed) return;
                    _folders = entries.Where(e => e.IsFolder).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                }
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                _where.Text = "Could not read " + FolderPath + ": " + ex.Message;
                Say(_where.Text);
                return;
            }

            var here = AtTop ? "Google Drive" : FolderPath;
            _where.Text = here;
            _list.AccessibleName = here;
            if (_folders.Count == 0)
            {
                _list.Items.Add("No folders in here. Use this folder, or make a new one.");
            }
            else
            {
                foreach (var folder in _folders)
                    _list.Items.Add(AtTop && folder.Name.StartsWith("Shared drives/", StringComparison.Ordinal)
                        ? "Shared drive: " + folder.Name["Shared drives/".Length..]
                        : folder.Name);
            }
            _list.SelectedIndex = 0;
            _list.Focus();
            Say(_folders.Count == 0 ? $"{here}, no folders" : $"{here}, {Plural(_folders.Count)}");
        }

        /// <summary>At the top: the drive selected (its whole self), or Everything.</summary>
        private void Choose(int at)
        {
            if (at < 0 || at >= _folders.Count) return;
            if (_folders[at].Id == EverythingId) Everything = true;
            else _path.Push((_folders[at].Id, _folders[at].Name));
            DialogResult = DialogResult.OK;
            Close();
        }

        private static string Plural(int n) => n == 1 ? "1 folder" : $"{n} folders";

        private static void Say(string text)
        {
            try { Speech.Speak(text, interrupt: true); } catch { }
        }
    }
}
