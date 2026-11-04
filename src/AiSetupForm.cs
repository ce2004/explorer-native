using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// Setting up an AI provider for the picture describer. As a wizard (Set up
    /// an AI provider): choose the provider, paste a key, choose a model. For one
    /// provider (its own button in Preferences): just the key. Either way OK or
    /// Next asks the provider whether the key is real before going on, and a key
    /// it turns down keeps the window open with the reason said.
    /// </summary>
    internal sealed class AiSetupForm : Form
    {
        private readonly bool _wizard;
        private readonly bool _speak;
        private AiProvider _provider;

        private readonly Panel _choosePage = new() { Dock = DockStyle.Fill, Padding = new Padding(12) };
        private readonly Panel _keyPage = new() { Dock = DockStyle.Fill, Padding = new Padding(12) };
        private readonly Panel _modelPage = new() { Dock = DockStyle.Fill, Padding = new Padding(12) };
        private readonly List<RadioButton> _choices = new();
        private readonly Label _keySteps = new() { AutoSize = false, UseMnemonic = false };
        private readonly Button _website = new() { AutoSize = true };
        private readonly TextBox _key = new() { Width = 460, UseSystemPasswordChar = true };
        private readonly SpokenCheckBox _show = new() { Text = "&Show the key", AutoSize = true };
        private readonly Label _status = new() { AutoSize = false, UseMnemonic = false };
        private readonly ComboBox _models = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 460 };
        private readonly SpokenCheckBox _useIt = new() { AutoSize = true, Checked = true };
        private readonly Button _back = new() { Text = "< &Back", AutoSize = true };
        private readonly Button _next = new() { Text = "&Next >", AutoSize = true };
        private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

        private CancellationTokenSource? _checking;
        private IReadOnlyList<AiModel> _found = Array.Empty<AiModel>();

        /// <summary>Who was set up.</summary>
        public AiProvider Provider => _provider;

        /// <summary>The key the provider accepted.</summary>
        public string Key { get; private set; } = "";

        /// <summary>The models that key can use to look at pictures.</summary>
        public IReadOnlyList<AiModel> Models => _found;

        /// <summary>The model chosen on the wizard's last page, or the recommended one.</summary>
        public string Model { get; private set; } = "";

        /// <summary>Whether pictures should go to this provider from now on.</summary>
        public bool MakeActive => !_wizard || _useIt.Checked;

        /// <summary>Asks the provider about a key. The suite swaps it.</summary>
        internal static Func<AiProvider, string, CancellationToken, Task<IReadOnlyList<AiModel>>> Check = AiDescriber.CheckKeyAsync;

        public AiSetupForm(Settings settings, AiProvider? only, string? existingKey = null, Func<AiProvider, string>? keyFor = null)
        {
            _keyFor = keyFor ?? settings.AiKeyFor;
            _wizard = only == null;
            _speak = settings.SpeakEnabled;
            _provider = only ?? settings.AiActive() ?? AiProvider.Claude;

            Text = _wizard ? "Set up an AI provider" : "Set up " + AiDescriber.Name(_provider);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(620, 400);

            // ---- page 1: which provider ----
            var ask = new Label
            {
                Text = "Which AI should describe your pictures? Each one needs an API key from its company, " +
                       "and you pay that company for what you use.",
                AutoSize = false, Dock = DockStyle.Top, Height = 48, UseMnemonic = false,
            };
            var radios = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            foreach (var p in AiDescriber.All)
            {
                var radio = new RadioButton
                {
                    Text = AiDescriber.Name(p),
                    AutoSize = true,
                    Tag = p,
                    Checked = p == _provider,
                    Margin = new Padding(3, 6, 3, 0),
                    UseMnemonic = false,
                };
                // NVDA reads the description after the name, so each choice says what it is.
                radio.AccessibleDescription = AiDescriber.Blurb(p);
                var blurb = new Label
                {
                    Text = AiDescriber.Blurb(p), AutoSize = false, Width = 560, Height = 36,
                    Margin = new Padding(22, 0, 3, 4), ForeColor = SystemColors.GrayText, UseMnemonic = false,
                };
                _choices.Add(radio);
                radios.Controls.Add(radio);
                radios.Controls.Add(blurb);
            }
            _choosePage.Controls.Add(radios);
            _choosePage.Controls.Add(ask);

            // ---- page 2: the key ----
            var keyLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            _keySteps.Size = new Size(580, 64);
            keyLayout.Controls.Add(_keySteps);
            _website.Margin = new Padding(3, 6, 3, 10);
            _website.Click += (_, _) => OpenWebsite();
            keyLayout.Controls.Add(_website);
            var keyLabel = new Label { Text = "API &key:", AutoSize = true, Margin = new Padding(3, 6, 3, 2) };
            keyLayout.Controls.Add(keyLabel);
            _key.AccessibleName = "API key";
            keyLayout.Controls.Add(_key);
            _show.AccessibleName = "Show the key";
            _show.CheckedChanged += (_, _) => _key.UseSystemPasswordChar = !_show.Checked;
            keyLayout.Controls.Add(_show);
            _status.Size = new Size(580, 70);
            _status.Margin = new Padding(3, 10, 3, 3);
            _status.AccessibleName = "Status";
            keyLayout.Controls.Add(_status);
            _keyPage.Controls.Add(keyLayout);

            // ---- page 3: the model ----
            var modelLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            modelLayout.Controls.Add(new Label
            {
                Text = "Your key works. Choose the model that describes your pictures. The recommended one is already chosen; " +
                       "you can change it any time in Preferences, AI image describer.",
                AutoSize = false, Size = new Size(580, 52), UseMnemonic = false,
            });
            modelLayout.Controls.Add(new Label { Text = "&Model:", AutoSize = true, Margin = new Padding(3, 6, 3, 2) });
            _models.AccessibleName = "Model";
            modelLayout.Controls.Add(_models);
            _useIt.Margin = new Padding(3, 12, 3, 3);
            modelLayout.Controls.Add(_useIt);
            _modelPage.Controls.Add(modelLayout);

            // ---- buttons ----
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(8) };
            buttons.Controls.Add(_cancel);
            buttons.Controls.Add(_next);
            buttons.Controls.Add(_back);
            _back.Click += (_, _) => GoBack();
            _next.Click += async (_, _) =>
            {
                try { await GoOnAsync(); }
                catch (Exception ex) { ShowStatus("Something went wrong: " + AiDescriber.Short(ex.Message)); EnableButtons(true); }
            };
            _cancel.Click += (_, _) => { _checking?.Cancel(); };
            AcceptButton = _next;
            CancelButton = _cancel;

            Controls.Add(_modelPage);
            Controls.Add(_keyPage);
            Controls.Add(_choosePage);
            Controls.Add(buttons);

            FormClosing += (_, _) => _checking?.Cancel();
            Shown += (_, _) => { if (_wizard) ShowChoose(); else ShowKey(existingKey ?? settings.AiKeyFor(_provider)); };
        }

        private enum Step { Choose, Key, Model }

        // The wizard is the one way in for every provider, so a provider that
        // already has a key starts with it filled in, ready to change.
        private readonly Func<AiProvider, string> _keyFor;
        private AiProvider? _keyShownFor;
        private Step _step;

        private void ShowChoose()
        {
            _step = Step.Choose;
            _choosePage.Visible = true; _keyPage.Visible = false; _modelPage.Visible = false;
            _back.Visible = false;
            _next.Text = "&Next >";
            var chosen = _choices.FirstOrDefault(c => c.Checked) ?? _choices[0];
            chosen.Checked = true;
            chosen.Focus();
        }

        private void ShowKey(string? existing)
        {
            _step = Step.Key;
            _choosePage.Visible = false; _keyPage.Visible = true; _modelPage.Visible = false;
            _back.Visible = _wizard;
            _next.Text = _wizard ? "&Next >" : "&OK";
            _keySteps.Text = AiDescriber.KeySteps(_provider);
            _website.Text = "Open the " + AiDescriber.Company(_provider) + " &website";
            _website.AccessibleName = "Open the " + AiDescriber.Company(_provider) + " website";
            if (existing != null) _key.Text = existing;
            _status.Text = "When you press " + (_wizard ? "Next" : "OK") + ", Explorer Native checks the key with " +
                           AiDescriber.Company(_provider) + ".";
            _key.Focus();
            _key.SelectAll();
        }

        private void ShowModel()
        {
            _step = Step.Model;
            _choosePage.Visible = false; _keyPage.Visible = false; _modelPage.Visible = true;
            _back.Visible = true;
            _next.Text = "&Finish";
            _useIt.Text = "&Use " + AiDescriber.Name(_provider) + " when I press Enter on a picture";
            _useIt.AccessibleName = _useIt.Text.Replace("&", "");
            FillModels(Model);
            _models.Focus();

            // The labels say about what a picture costs once the price list is in.
            var which = _provider;
            _ = Task.Run(async () =>
            {
                try
                {
                    await AiDescriber.PriceAsync(which, Model, CancellationToken.None);
                    if (IsDisposed) return;
                    BeginInvoke(() => { if (!IsDisposed && _step == Step.Model && which == _provider) FillModels(CurrentModel()); });
                }
                catch { }
            });
        }

        private string CurrentModel() =>
            _models.SelectedIndex >= 0 && _models.SelectedIndex < _found.Count ? _found[_models.SelectedIndex].Id : Model;

        private void FillModels(string selected)
        {
            _models.BeginUpdate();
            try
            {
                _models.Items.Clear();
                foreach (var m in _found) _models.Items.Add(AiDescriber.ModelLabel(_provider, m) +
                    (m.Id == AiDescriber.Recommended(_provider, _found) ? " (recommended)" : ""));
                int at = _found.ToList().FindIndex(m => m.Id == selected);
                _models.SelectedIndex = _models.Items.Count == 0 ? -1 : Math.Max(0, at);
            }
            finally { _models.EndUpdate(); }
        }

        private void GoBack()
        {
            if (_step == Step.Model) ShowKey(null);
            else if (_step == Step.Key && _wizard) ShowChoose();
        }

        private async Task GoOnAsync()
        {
            switch (_step)
            {
                case Step.Choose:
                {
                    var chosen = _choices.FirstOrDefault(c => c.Checked);
                    if (chosen?.Tag is not AiProvider p) { Say("Choose one first."); return; }
                    bool changed = p != _keyShownFor;
                    _provider = p;
                    _keyShownFor = p;
                    ShowKey(changed ? (_keyFor(p) ?? "").Trim() : null);
                    return;
                }
                case Step.Key:
                    await CheckKeyAsync();
                    return;
                case Step.Model:
                    Model = CurrentModel();
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
            }
        }

        private async Task CheckKeyAsync()
        {
            var key = _key.Text.Trim();
            if (AiDescriber.WrongKind(_provider, key) is { } wrong)
            {
                ShowStatus(wrong);
                _key.Focus();
                return;
            }

            _checking?.Cancel();
            var cts = _checking = new CancellationTokenSource();
            EnableButtons(false);
            ShowStatus("Checking your key with " + AiDescriber.Company(_provider) + "…");
            try
            {
                var models = await Check(_provider, key, cts.Token);
                if (IsDisposed || cts.IsCancellationRequested) return;
                _found = models;
                Key = key;
                Model = AiDescriber.Recommended(_provider, models);
                if (_wizard)
                {
                    Say("Your key works.");
                    EnableButtons(true);
                    ShowModel();
                }
                else
                {
                    Say(AiDescriber.Company(_provider) + " accepted the key.");
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
            catch (OperationCanceledException) { }
            catch (AiException ex) { if (!IsDisposed) { ShowStatus(ex.Message); _key.Focus(); _key.SelectAll(); } }
            catch (Exception ex) { if (!IsDisposed) ShowStatus("The key couldn't be checked: " + AiDescriber.Short(ex.Message)); }
            finally { if (!IsDisposed) EnableButtons(true); }
        }

        private void EnableButtons(bool on)
        {
            _next.Enabled = on;
            _back.Enabled = on;
            _key.ReadOnly = !on;
        }

        private void ShowStatus(string text)
        {
            _status.Text = text;
            Say(text);
        }

        private void OpenWebsite()
        {
            var url = AiDescriber.KeyPage(_provider);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
                Say("Opening " + AiDescriber.Company(_provider) + "'s website in your browser.");
            }
            catch (Exception ex) { ShowStatus("The website couldn't be opened: " + AiDescriber.Short(ex.Message) + " The address is " + url); }
        }

        private void Say(string text)
        {
            if (_speak) Speech.Speak(text, interrupt: true);
        }
    }
}
