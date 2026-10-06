using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Help, Changelog: every release on GitHub, newest first.
    ///
    /// One list per version. Tab moves from version to version, the arrow keys
    /// read the changes in the one you are on, and the list's name says which
    /// version it is and when it came out. After the last version, Tab reaches
    /// Back. Escape or Back closes the window.
    /// </summary>
    internal sealed class ChangelogForm : Form
    {
        private readonly FlowLayoutPanel _versions;
        private readonly TextBox _status;
        private readonly Button _back;
        private readonly CancellationTokenSource _cancel = new();

        public ChangelogForm()
        {
            Text = "Changelog";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(620, 480);
            Font = SystemFonts.MessageBoxFont;

            _status = new TextBox
            {
                Text = "Loading the changelog from GitHub…",
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                Dock = DockStyle.Top,
                AccessibleRole = AccessibleRole.StaticText,
                AccessibleName = "Changelog",
                TabIndex = 0,
            };

            _versions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(8),
                TabIndex = 1,
            };

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 44,
                Padding = new Padding(8),
                TabIndex = 2,
            };
            _back = new Button { Text = "&Back", DialogResult = DialogResult.Cancel, AutoSize = true };
            bar.Controls.Add(_back);

            Controls.Add(_versions);
            Controls.Add(bar);
            Controls.Add(_status);
            CancelButton = _back;

            Shown += async (_, _) =>
            {
                _status.Focus();
                await LoadAsync();
            };
            FormClosed += (_, _) =>
            {
                _cancel.Cancel();
                _cancel.Dispose();
            };
        }

        private async Task LoadAsync()
        {
            List<Updater.ReleaseNotes> releases;
            try
            {
                releases = await Updater.AllReleasesAsync(_cancel.Token);
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                _status.Text = ex switch
                {
                    OperationCanceledException => "GitHub did not answer in time.",
                    // Already a whole sentence: rate limited, not found, unreadable.
                    Updater.UpdateException => "Could not load the changelog. " + ex.Message,
                    System.Net.Http.HttpRequestException =>
                        "Could not load the changelog. GitHub could not be reached: " + ex.Message,
                    _ => "Could not load the changelog: " + ex.Message,
                };
                Speak(_status.Text);
                return;
            }

            if (IsDisposed) return;

            if (releases.Count == 0)
            {
                _status.Text = "No releases have been published yet.";
                Speak(_status.Text);
                return;
            }

            _status.Text = releases.Count == 1
                ? "1 version. Tab moves between versions, the arrow keys read the changes."
                : $"{releases.Count} versions, newest first. Tab moves between versions, the arrow keys read the changes.";

            _versions.SuspendLayout();
            int tab = 0;
            ListBox? first = null;
            foreach (var release in releases)
            {
                var title = $"Version {Updater.Text(release.Version)}" +
                            (release.Published is DateTime d
                                ? ", " + d.ToLocalTime().ToString("MMMM d, yyyy", CultureInfo.CurrentCulture)
                                : "");

                var lines = UpdateForm.Changes(release.Notes);
                if (lines.Count == 0) lines.Add("No list of changes was published for this version.");

                var label = new Label { Text = title, AutoSize = true, Margin = new Padding(0, 8, 0, 2) };
                var list = new ListBox
                {
                    Width = 560,
                    Height = Math.Min(lines.Count, 8) * 18 + 8,
                    HorizontalScrollbar = true,
                    AccessibleName = title,
                    TabIndex = tab++,
                };
                list.Items.AddRange(lines.ToArray());
                list.SelectedIndex = 0;
                list.Enter += (_, _) => _versions.ScrollControlIntoView(list);

                _versions.Controls.Add(label);
                _versions.Controls.Add(list);
                first ??= list;
            }
            _versions.ResumeLayout();

            first?.Focus();
        }

        private static void Speak(string text)
        {
            try { Speech.Speak(text, interrupt: true); } catch { }
        }
    }
}
