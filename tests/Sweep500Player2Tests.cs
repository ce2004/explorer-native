using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// The 5.0.0 sweep of the audio output and effect dialogs.
    /// </summary>
    internal static class Sweep500Player2Tests
    {
        private static Action<string, bool, string?> _check = null!;
        private static Action<string, string, string> _equal = null!;

        internal static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);
        internal static void Equal(string what, string expected, string actual) => _equal(what, expected, actual);

        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            _equal = equal;

            Console.WriteLine("Sweep 5.0.0, the audio output and effect dialogs:");
            EffectDialogAccessKeyTests();
            await Task.CompletedTask;
        }

        // ---- helpers ----

        private static string OnSta(Action work, int seconds = 60)
        {
            string error = "";
            var thread = new Thread(() =>
            {
                try { work(); }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(seconds))) return "timed out";
            return error;
        }

        private static List<Control> FindAll(Control root)
        {
            var found = new List<Control>();
            foreach (Control child in root.Controls)
            {
                found.Add(child);
                found.AddRange(FindAll(child));
            }
            return found;
        }

        private static char? AccessKey(Control control)
        {
            bool mnemonic = control is ButtonBase b ? b.UseMnemonic : control is Label l && l.UseMnemonic;
            if (!mnemonic) return null;
            var text = control.Text ?? "";
            for (int i = 0; i < text.Length - 1; i++)
            {
                if (text[i] != '&') continue;
                if (text[i + 1] == '&') { i++; continue; }
                return char.ToUpperInvariant(text[i + 1]);
            }
            return null;
        }

        private static IEnumerable<string> Clashes(string dialog, Control root)
        {
            var byKey = new Dictionary<char, List<string>>();
            foreach (var c in FindAll(root))
            {
                if (AccessKey(c) is not { } key) continue;
                if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<string>();
                list.Add(c.Text.Replace("&", ""));
            }
            return byKey.Where(p => p.Value.Count > 1)
                .Select(p => $"{dialog}: Alt+{p.Key}: {string.Join(" and ", p.Value)}");
        }

        private static bool PressAlt(Form form, char key)
        {
            var method = typeof(ContainerControl).GetMethod("ProcessMnemonic",
                BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(char) }, null)!;
            return (bool)method.Invoke(form, new object[] { key })!;
        }

        // ---- bugs 1, 2 and 3: access keys in the effect dialogs ----

        /// <summary>
        /// Alt+O pressed OK only if nothing before OK in the tab order used O,
        /// and WinForms gives a shared key to the next control after the focus.
        /// The on boxes used O (so Alt+O switched the effect instead of
        /// pressing OK), and the Limiter's Ceiling used C (Cancel threw the
        /// tuning away) and Same lift used A (it toggled instead of moving to
        /// Attack). Every key in these dialogs must be its own.
        /// </summary>
        private static void EffectDialogAccessKeyTests()
        {
            var clashes = new List<string>();
            bool eqStillOn = false, eqClosedOk = false;
            bool limiterOnKept = false, limiterOk = false, ceilingFocused = false, limiterOpen = false;
            string error = OnSta(() =>
            {
                using (var eq = new EqualiserForm(new int[Equaliser.BandCount], true, (_, _) => { }, _ => { }))
                    clashes.AddRange(Clashes("Equaliser", eq));
                using (var silence = new SilenceForm(-48, 400, 150, 20, 20, true, (_, _, _, _, _, _) => { }))
                    clashes.AddRange(Clashes("Silence reduction", silence));
                using (var limiter = new LimiterForm(500, 250, 90, false, true, (_, _, _, _, _) => { }))
                    clashes.AddRange(Clashes("Limiter", limiter));

                // The behaviour itself: Alt+O from a slider presses OK and
                // leaves the equaliser on.
                using (var eq = new EqualiserForm(new int[Equaliser.BandCount], true, (_, _) => { }, _ => { }))
                {
                    eq.StartPosition = FormStartPosition.Manual;
                    eq.Location = new System.Drawing.Point(-3000, -3000);
                    eq.ShowInTaskbar = false;
                    eq.Show();
                    Application.DoEvents();
                    var slider = FindAll(eq).OfType<TrackBar>().First();
                    slider.Focus();
                    eq.ActiveControl = slider;
                    PressAlt(eq, 'o');
                    Application.DoEvents();
                    eqStillOn = eq.EqualiserOn;
                    eqClosedOk = eq.DialogResult == DialogResult.OK;
                }

                // Alt+C from the Ceiling list's neighbour comes back to the
                // list instead of pressing Cancel; Alt+O presses OK.
                using (var limiter = new LimiterForm(500, 250, 90, false, true, (_, _, _, _, _) => { }))
                {
                    limiter.StartPosition = FormStartPosition.Manual;
                    limiter.Location = new System.Drawing.Point(-3000, -3000);
                    limiter.ShowInTaskbar = false;
                    limiter.Show();
                    Application.DoEvents();
                    var lists = FindAll(limiter).OfType<ComboBox>().ToList();
                    var boxes = FindAll(limiter).OfType<CheckBox>().ToList();
                    var ceiling = lists[2];
                    var after = boxes[0];
                    after.Focus();
                    limiter.ActiveControl = after;
                    PressAlt(limiter, limiter.Controls.OfType<Label>()
                        .First(l => l.Text.Replace("&", "") == "Ceiling:").Text
                        is var t && t.IndexOf('&') >= 0 ? char.ToLowerInvariant(t[t.IndexOf('&') + 1]) : 'c');
                    Application.DoEvents();
                    ceilingFocused = limiter.ActiveControl == ceiling;
                    limiterOpen = limiter.DialogResult == DialogResult.None;

                    lists[0].Focus();
                    limiter.ActiveControl = lists[0];
                    PressAlt(limiter, 'o');
                    Application.DoEvents();
                    limiterOnKept = boxes.All(b => b.AccessibleName != "Limiter on" || b.Checked);
                    limiterOk = limiter.DialogResult == DialogResult.OK;
                }
            });
            Equal("the effect dialogs build without an error", "", error);
            Equal("the Equaliser, Silence reduction and Limiter dialogs share no access keys",
                "", string.Join("; ", clashes));
            Check("Alt+O from an equaliser slider leaves the equaliser on", eqStillOn);
            Check("Alt+O from an equaliser slider presses OK", eqClosedOk);
            Check("the Ceiling access key from the Same lift box moves to the Ceiling list", ceilingFocused);
            Check("the Ceiling access key does not press Cancel", limiterOpen);
            Check("Alt+O from the Attack list leaves the limiter on", limiterOnKept);
            Check("Alt+O from the Attack list presses OK", limiterOk);
        }
    }
}
