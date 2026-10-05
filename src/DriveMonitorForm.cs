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
        private readonly ListBox _list;
        private readonly Button _edit, _remove, _sync;
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
            ClientSize = new Size(640, 360);
            Font = SystemFonts.MessageBoxFont;

            var label = new Label { Text = "&Monitored folders:", AutoSize = true, Location = new Point(12, 12) };
            _list = new ListBox
            {
                Location = new Point(12, 32),
                Size = new Size(616, 220),
                HorizontalScrollbar = true,
                AccessibleName = "Monitored folders",
                TabIndex = 0,
            };

            var info = new Label
            {
                Text = "Changes here are saved straight away. Folders sync while Explorer Native is running " +
                       "and Google Drive is connected. Enter edits a folder, Delete removes it; removing " +
                       "never deletes any files.",
                Location = new Point(12, 258),
                Size = new Size(616, 48),
            };

            var add = MakeButton("&Add...", 12, 1);
            _edit = MakeButton("&Edit...", 112, 2);
            _remove = MakeButton("&Remove", 212, 3);
            _sync = MakeButton("&Sync now", 312, 4);
            var close = MakeButton("Close", 548, 5);
            close.DialogResult = DialogResult.Cancel;

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

            Controls.AddRange(new Control[] { label, _list, info, add, _edit, _remove, _sync, close });
            CancelButton = close;

            Action refresh = () =>
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(Fill));
            };
            if (_monitor != null) _monitor.Changed += refresh;
            FormClosed += (_, _) => { if (_monitor != null) _monitor.Changed -= refresh; };

            Fill();
        }

        private Button MakeButton(string text, int x, int tab) =>
            new() { Text = text, Location = new Point(x, 316), Size = new Size(92, 30), TabIndex = tab };

        private void Fill()
        {
            var rows = _pairs.Count == 0
                ? new List<string> { "No folders are monitored yet. Press Add to choose one." }
                : _pairs.Select(p => Describe(p, _monitor?.StatusOf(p.Id), DateTime.UtcNow)).ToList();

            // In place when only the words changed (live progress), so the list
            // is not rebuilt under the screen reader twice a second.
            if (rows.Count == _list.Items.Count)
            {
                for (int i = 0; i < rows.Count; i++)
                    if (!Equals(_list.Items[i], rows[i])) _list.Items[i] = rows[i];
            }
            else
            {
                int at = Math.Max(0, _list.SelectedIndex);
                _list.BeginUpdate();
                _list.Items.Clear();
                _list.Items.AddRange(rows.ToArray());
                _list.EndUpdate();
                _list.SelectedIndex = Math.Min(at, _list.Items.Count - 1);
            }

            bool any = _pairs.Count > 0;
            _edit.Enabled = any;
            _remove.Enabled = any;
        }

        /// <summary>
        /// One pair as one sentence: "Music: C:\Users\Conner\Music and Drive
        /// folder My Drive/Music, two-way, deletes not copied, last synced 2
        /// minutes ago".
        /// </summary>
        internal static string Describe(DriveSyncPair pair, DriveMonitor.PairStatus? status, DateTime nowUtc)
        {
            var mode = pair.Mode switch
            {
                SyncMode.UploadOnly => "upload only, PC to Drive",
                SyncMode.DownloadOnly => "download only, Drive to PC",
                _ => "two-way",
            };
            var deletes = pair.CopyDeletes ? "deletes copied" : "deletes not copied";

            string state;
            if (pair.Paused) state = "paused";
            else if (status?.Problem is { } waiting && waiting.StartsWith("waiting", StringComparison.Ordinal))
                state = waiting;
            else if (status is { Running: true, CheckTotal: > 0 })
                state = $"checking what is already there, {status.CheckDone:N0} of {status.CheckTotal:N0} files";
            else if (status is { Running: true, TotalBytes: > 0 })
                state = $"syncing, {DriveMonitor.Words(status.DoneBytes)} of {DriveMonitor.Words(status.TotalBytes)}";
            else if (status is { Running: true }) state = "syncing now";
            else if (status?.Problem is { } problem)
                state = problem.StartsWith("paused", StringComparison.Ordinal) ||
                        problem.StartsWith("offline", StringComparison.Ordinal)
                    ? problem
                    : "problem: " + problem;
            else if (status?.LastSyncedUtc is DateTime when) state = "last synced " + Ago(nowUtc - when);
            else state = "not synced yet";

            var check = pair.CheckMinutes switch
            {
                1 => "",
                <= 0 => ", Drive checked only on Sync now",
                60 => ", checks Drive every hour",
                var m => $", checks Drive every {m} minutes",
            };

            return $"{DriveMonitor.DisplayName(pair)}: {pair.LocalFolder} and Drive folder {pair.DriveFolderPath}, " +
                   $"{mode}, {deletes}{check}, {state}";
        }

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
            using var dialog = new DrivePairForm(pair, "Add a monitored folder");
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _pairs.Add(dialog.Pair);
            Commit();
            _list.SelectedIndex = _pairs.Count - 1;
            Say($"Added {DriveMonitor.DisplayName(dialog.Pair)}");
        }

        private void EditPair()
        {
            int at = _list.SelectedIndex;
            if (at < 0 || at >= _pairs.Count) return;
            using var dialog = new DrivePairForm(_pairs[at].Copy(), "Edit a monitored folder");
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _pairs[at] = dialog.Pair;
            Commit();
            _list.SelectedIndex = at;
        }

        private void RemovePair()
        {
            int at = _list.SelectedIndex;
            if (at < 0 || at >= _pairs.Count) return;
            var name = DriveMonitor.DisplayName(_pairs[at]);
            _pairs.RemoveAt(at);
            Commit();
            Say($"Removed {name}. No files were deleted.");
        }

        /// <summary>Saves straight away, and keeps Preferences' copy in step so its OK does not undo it.</summary>
        private void Commit()
        {
            _working.Clear();
            _working.AddRange(_pairs.Select(p => p.Copy()));
            _monitor?.SetPairs(_pairs);
            Fill();
        }

        private static void Say(string text)
        {
            try { Speech.Speak(text, interrupt: true); } catch { }
        }
    }

    /// <summary>
    /// Add or edit one folder pair. OK checks there is room for it (the PC's
    /// disk for what comes down, Drive's quota for what goes up) before it is
    /// saved, and offers to save it paused when there is not.
    /// </summary>
    internal sealed class DrivePairForm : Form
    {
        private readonly TextBox _name, _local, _drive, _skipTypes;
        private readonly ComboBox _mode, _maxSize, _clash, _every;
        private readonly CheckBox _deletes, _paused, _subfolders;
        private readonly Label _status;
        private readonly Button _ok;
        private bool _checking;

        public DriveSyncPair Pair { get; }

        private static readonly (SyncMode Mode, string Text)[] Modes =
        {
            (SyncMode.TwoWay, "Two-way: keep both the same"),
            (SyncMode.UploadOnly, "Upload only: PC to Drive, a backup"),
            (SyncMode.DownloadOnly, "Download only: Drive to PC"),
        };

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

        private static readonly (int Minutes, string Text)[] Intervals =
        {
            (1, "1 minute"), (5, "5 minutes"), (15, "15 minutes"), (60, "60 minutes"), (0, "Only when I press Sync now"),
        };

        public DrivePairForm(DriveSyncPair pair, string title)
        {
            Pair = pair;

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
            y += 34;
            _every = Choice("Check Drive e&very:", Intervals.Select(i => i.Text),
                Array.FindIndex(Intervals, i => i.Minutes == pair.CheckMinutes));
            y += 34;
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

            _status = new Label { AutoSize = false, Location = new Point(12, y), Size = new Size(576, 40) };
            Controls.Add(_status);

            _ok = new Button { Text = "OK", Location = new Point(408, 458), Size = new Size(84, 28), TabIndex = tab++ };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(504, 458),
                Size = new Size(84, 28),
                TabIndex = tab++,
            };
            Controls.Add(_ok);
            Controls.Add(cancel);
            AcceptButton = _ok;
            CancelButton = cancel;

            // Closing while Drive is being totalled up would save half an answer.
            FormClosing += (_, e) =>
            {
                if (_checking && DialogResult != DialogResult.OK) e.Cancel = true;
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
                edited.CheckMinutes = Intervals[Math.Max(0, _every.SelectedIndex)].Minutes;
                edited.WhenBothChanged = Clashes[Math.Max(0, _clash.SelectedIndex)].Choice;
                edited.MaxFileMegabytes = Sizes[Math.Max(0, _maxSize.SelectedIndex)].Megabytes;
                edited.SkipExtensions = _skipTypes.Text.Trim();
                edited.IncludeSubfolders = _subfolders.Checked;
                edited.CopyDeletes = _deletes.Checked;
                edited.Paused = _paused.Checked;

                // Never start a sync that cannot finish: what would come down
                // against the PC's free space, what would go up against Drive's.
                if (!edited.Paused)
                {
                    var shortage = await RoomProblemAsync(edited);
                    if (IsDisposed) return;
                    if (shortage != null)
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
                }

                Pair.LocalFolder = edited.LocalFolder;
                Pair.Name = edited.Name;
                Pair.Mode = edited.Mode;
                Pair.CheckMinutes = edited.CheckMinutes;
                Pair.WhenBothChanged = edited.WhenBothChanged;
                Pair.MaxFileMegabytes = edited.MaxFileMegabytes;
                Pair.SkipExtensions = edited.SkipExtensions;
                Pair.IncludeSubfolders = edited.IncludeSubfolders;
                Pair.CopyDeletes = edited.CopyDeletes;
                Pair.Paused = edited.Paused;
                DialogResult = DialogResult.OK;
                Close();
            };
        }

        /// <summary>
        /// Lists both sides through the API and says, plainly, if what would be
        /// copied does not fit; null when it does or cannot be told.
        /// </summary>
        private async Task<string?> RoomProblemAsync(DriveSyncPair pair)
        {
            var client = DriveMonitorClient();
            if (client == null) return null;

            _checking = true;
            _ok.Enabled = false;
            UseWaitCursor = true;
            _status.Text = "Checking how much space this needs…";
            Say(_status.Text);
            try
            {
                var (down, up) = await DriveMonitor.NeedsAsync(client, pair, CancellationToken.None);

                if (down > 0)
                {
                    var (free, total, letter) = DriveMonitor.DiskOf(pair.LocalFolder);
                    if (total > 0 && !SyncSpace.Fits(down, free, total))
                        return $"This needs {DriveMonitor.Words(down)} and {letter} has " +
                               $"{DriveMonitor.Words(free)} free, keeping {DriveMonitor.Words(SyncSpace.Margin(total))} spare.";
                }

                var drive = DriveMonitor.Current?.Drive;
                if (up > 0 && drive is { QuotaLimit: > 0 })
                {
                    long room = drive.QuotaLimit - drive.QuotaUsed;
                    if (up > room)
                        return $"This needs {DriveMonitor.Words(up)} in Google Drive, which has " +
                               $"{DriveMonitor.Words(Math.Max(0, room))} free.";
                }

                _status.Text = "";
                return null;
            }
            catch (Exception ex)
            {
                // Not knowing is not a reason to refuse: every pass checks again
                // before each file and pauses itself if it runs out of room.
                _status.Text = "Could not check the space needed: " + ex.Message;
                return null;
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
