using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Which speaker the music comes out of, as one list you arrow through.
    ///
    /// Built to the same shape as <see cref="SpeedForm"/>, deliberately: the
    /// device changes on every arrow press rather than when the dialog is
    /// dismissed, because choosing an output by ear means hearing each one, and
    /// a dialog that only applies its answer at the end makes you guess and then
    /// check. Escape puts back whatever was playing when it opened, so trying
    /// the list costs nothing.
    ///
    /// That only works because switching is quick — the ring buffer and the
    /// decoder survive the move, so the music carries on from where it was. See
    /// WasapiPlayback.SwitchNow.
    /// </summary>
    public sealed class DeviceForm : Form
    {
        private readonly Action<string> _apply;
        private readonly string _original;
        private bool _keep;

        /// <summary>The endpoint id chosen, or empty for the system default.</summary>
        public string SelectedId { get; private set; }

        public DeviceForm(string currentId, IReadOnlyList<AudioDevice> devices,
            Action<string> apply)
        {
            _apply = apply;
            _original = currentId ?? "";
            SelectedId = _original;

            Text = "Output device";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(420, 130);

            var label = new Label
            {
                Text = "&Device:",
                AutoSize = true,
                Location = new Point(14, 20),
            };

            var list = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(80, 16),
                Width = 320,
            };
            list.AccessibleName = "Output device";
            list.AccessibleDescription =
                "Arrow up and down to change which device the audio plays through. " +
                "It applies as you go. Escape puts it back.";

            // The default comes first and is not a device: it is the instruction
            // "follow the system", which is what makes plugging in headphones
            // move the music. Naming today's default device instead would pin the
            // audio to the speakers for ever.
            _ids.Add("");
            list.Items.Add("Follow the system default");

            foreach (var device in devices)
            {
                _ids.Add(device.Id);
                list.Items.Add(device.Name);
            }

            int at = _ids.IndexOf(_original);

            // A saved device that is no longer plugged in is not in the list, so
            // the list falls back to its first entry — "Follow the system
            // default". SelectedId has to fall back with it: it was left holding
            // the missing id, and nothing corrected it, because the answer is
            // only updated by the change event and setting the index to 0 when
            // the field was never anything else raises none. Unplug the device
            // the audio was pinned to, open this dialog, press OK, and the
            // setting still named the device that is not there.
            if (at < 0 && _ids.Count > 0) SelectedId = _ids[0];

            list.SelectedIndex = at >= 0 ? at : 0;

            if (devices.Count == 0)
            {
                var none = new Label
                {
                    Text = "No playback devices are available.",
                    AutoSize = false,
                    Location = new Point(14, 48),
                    Size = new Size(390, 24),
                    ForeColor = SystemColors.GrayText,
                };
                none.AccessibleName = none.Text;
                Controls.Add(none);
            }

            // Applied here, so it fires for arrow keys, for the mouse, and for
            // typing the first letter — every way a list is used.
            list.SelectedIndexChanged += (_, _) =>
            {
                if (list.SelectedIndex < 0 || list.SelectedIndex >= _ids.Count) return;
                SelectedId = _ids[list.SelectedIndex];
                _apply(SelectedId);
            };

            var ok = new Button
            {
                Text = "&OK",
                DialogResult = DialogResult.OK,
                Location = new Point(244, 82),
                AutoSize = true,
            };
            var cancel = new Button
            {
                Text = "&Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(324, 82),
                AutoSize = true,
            };

            ok.Click += (_, _) => _keep = true;

            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(label);
            Controls.Add(list);
            Controls.Add(ok);
            Controls.Add(cancel);

            // Straight onto the list: this dialog is the list.
            Shown += (_, _) => list.Focus();
        }

        private readonly List<string> _ids = new();

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Anything other than OK puts back what was there. Closing with the
            // X, Escape, or Alt+F4 all mean the same thing: never mind.
            if (!_keep)
            {
                SelectedId = _original;
                _apply(_original);
            }

            base.OnFormClosing(e);
        }
    }
}
