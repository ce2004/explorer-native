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
            WholeRangeKeepsTheVolumeTests();

            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "en500-player2-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                ResetDeviceIsReopenedTests(dir);
                SlowOpenKeepsPlayingTests(dir);
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
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

        // ---- bug 4: a picked device that Windows resets ----

        private static bool RunWithin(int milliseconds, Action action)
        {
            var thread = new Thread(() => { try { action(); } catch { } })
            { IsBackground = true, Name = "Sweep500 player2 audio harness" };
            thread.Start();
            return thread.Join(milliseconds);
        }

        private static bool WaitFor(Func<bool> condition, int milliseconds)
        {
            long until = Environment.TickCount64 + milliseconds;
            while (Environment.TickCount64 < until)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }
            return condition();
        }

        private static WasapiRenderer RendererOf(WasapiPlayback playback) =>
            (WasapiRenderer)typeof(WasapiPlayback).GetField("_renderer",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(playback)!;

        private static void Lose(WasapiRenderer renderer) =>
            typeof(WasapiRenderer).GetMethod("RaiseLost", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(renderer, null);

        /// <summary>
        /// Windows invalidates a stream when a device is reset (format change,
        /// Bluetooth hands-free, driver restart, sleep) as well as when it is
        /// pulled out. A picked device lost that way went straight to the
        /// default for the rest of the track. It must be opened again by name
        /// first, and only once in a while, so one that keeps going quiet
        /// ends on the default rather than in a loop.
        /// </summary>
        private static void ResetDeviceIsReopenedTests(string dir)
        {
            var endpoint = WasapiRenderer.DefaultEndpointId();
            if (endpoint == null)
            {
                Console.WriteLine("  (no playback device here; the reset-device checks are skipped)");
                return;
            }

            var wav = System.IO.Path.Combine(dir, "reset.wav");
            Tests.WriteTone(wav, 20, 0.5, 44100, 2);

            string problem = "";
            string afterFirst = "?", afterSecond = "?";
            int switchesAfterSecond = 0;
            bool finished = RunWithin(60_000, () =>
            {
                using var playback = new WasapiPlayback { Gain = 0f };
                if (!playback.Start(wav, preferRaw: false)) { problem = "did not start: " + playback.Diagnostic; return; }
                if (!WaitFor(() => playback.Position > 0.2, 4000)) { problem = "the clock never moved"; return; }

                int switches = 0;
                playback.DeviceSwitched += (_, ok) => { if (ok) Interlocked.Increment(ref switches); };
                playback.SwitchDevice(endpoint);
                if (!WaitFor(() => Volatile.Read(ref switches) >= 1, 15_000)) { problem = "the pick never landed"; return; }
                if (RendererOf(playback).DeviceId != endpoint) { problem = "the pick was not by name"; return; }

                Lose(RendererOf(playback));
                if (!WaitFor(() => Volatile.Read(ref switches) >= 2, 15_000)) { problem = "nothing took over after the reset"; return; }
                afterFirst = RendererOf(playback).DeviceId;

                Lose(RendererOf(playback));
                if (!WaitFor(() => Volatile.Read(ref switches) >= 3, 15_000)) { problem = "nothing took over after the second reset"; return; }
                afterSecond = RendererOf(playback).DeviceId;
                Thread.Sleep(3000);
                switchesAfterSecond = Volatile.Read(ref switches) - 3;
                playback.Stop();
            });

            Check("the reset-device harness finished", finished && problem.Length == 0,
                finished ? problem : "did not finish in 60 seconds");
            Check("a picked device that Windows resets is opened again by name, not swapped for the default",
                afterFirst == endpoint, $"device after the reset: \"{afterFirst}\"");
            Check("a picked device reset twice within 30 seconds ends on the default",
                afterSecond == "", $"device after the second reset: \"{afterSecond}\"");
            Check("and nothing keeps switching after that", switchesAfterSecond == 0,
                $"{switchesAfterSecond} more switches");
        }

        // ---- bug 5: a slow device open ----

        /// <summary>
        /// The new device opens on the pump thread, the only thing that fills
        /// the ring, and a local track keeps only 0.2s there. While a slow
        /// Bluetooth endpoint opened, the old device played that and then
        /// silence, said as "Buffering". It must keep playing through the open.
        /// </summary>
        private static void SlowOpenKeepsPlayingTests(string dir)
        {
            var endpoint = WasapiRenderer.DefaultEndpointId();
            if (endpoint == null)
            {
                Console.WriteLine("  (no playback device here; the slow-open checks are skipped)");
                return;
            }

            var wav = System.IO.Path.Combine(dir, "slow.wav");
            Tests.WriteTone(wav, 8, 0.5, 44100, 2);

            string problem = "";
            double early = 0, late = 0;
            bool starvedBeforeSwitch = false;
            bool finished = RunWithin(60_000, () =>
            {
                using var playback = new WasapiPlayback { Gain = 0f };
                if (!playback.Start(wav, preferRaw: false)) { problem = "did not start: " + playback.Diagnostic; return; }
                if (!WaitFor(() => playback.Position > 0.3, 4000)) { problem = "the clock never moved"; return; }

                bool switched = false;
                playback.Starved += starving => { if (starving && !Volatile.Read(ref switched)) starvedBeforeSwitch = true; };
                playback.DeviceSwitched += (_, _) => Volatile.Write(ref switched, true);
                WasapiPlayback.BeforeSwitchOpenForTests = () => Thread.Sleep(1500);
                try
                {
                    playback.SwitchDevice(endpoint);
                    Thread.Sleep(100);
                    early = playback.Position;
                    Thread.Sleep(1200);
                    late = playback.Position;
                    if (!WaitFor(() => Volatile.Read(ref switched), 15_000)) problem = "the switch never finished";
                }
                finally { WasapiPlayback.BeforeSwitchOpenForTests = null; }
                playback.Stop();
            });

            Check("the slow-open harness finished", finished && problem.Length == 0,
                finished ? problem : "did not finish in 60 seconds");
            Check($"the old device keeps playing while a slow device opens ({early:0.##}s to {late:0.##}s)",
                late - early >= 1.0);
            Check("and nothing says Buffering while it opens", !starvedBeforeSwitch);
        }

        // ---- bug 6: Same lift at any volume and the volume control ----

        private static float SettledWide(float amplitude, float gain, int seconds = 3)
        {
            var renderer = new WasapiRenderer { Gain = gain, Limiter = true, LimiterWholeRange = true };
            var block = new float[4096];
            float last = 0f;
            int blocks = seconds * 48000 / block.Length;
            for (int b = 0; b < blocks; b++)
            {
                for (int i = 0; i < block.Length; i++) block[i] = amplitude;
                renderer.ApplyGainAndMeasure(block, block.Length);
                last = block[^1];
            }
            return last;
        }

        /// <summary>
        /// The limiter measured the music after the volume and aimed it at the
        /// ceiling, so with "Same lift at any volume" on, turning down (or a
        /// fade) was lifted straight back up to 0.9. The volume must stay a
        /// plain multiply on top of the lift gain 1 would give.
        /// </summary>
        private static void WholeRangeKeepsTheVolumeTests()
        {
            float loudQuiet = SettledWide(0.8f, 0.25f);
            Check($"whole range at 50 percent volume leaves a loud passage turned down ({loudQuiet:0.###})",
                loudQuiet <= 0.25f * 0.91f && loudQuiet >= 0.25f * 0.8f);

            float q1 = SettledWide(0.002f, 1f), q4 = SettledWide(0.002f, 0.25f);
            Check($"whole range gives the same lift at a quarter gain, so the volume still scales it ({q4 / q1:0.###})",
                Math.Abs(q4 / q1 - 0.25f) < 0.02f);

            var renderer = new WasapiRenderer { Gain = 1f, Limiter = true, LimiterWholeRange = true };
            var block = new float[1200];
            for (int b = 0; b < 3 * 48000 / block.Length; b++)
            {
                Array.Fill(block, 0.8f);
                renderer.ApplyGainAndMeasure(block, block.Length);
            }
            float settled = Math.Abs(block[^1]);
            var peaks = new List<float>();
            for (int step = 0; step <= 40; step++)
            {
                renderer.Gain = 1f - step / 40f;
                Array.Fill(block, 0.8f);
                renderer.ApplyGainAndMeasure(block, block.Length);
                peaks.Add(block.Max(Math.Abs));
            }
            bool falling = true;
            for (int i = 1; i < peaks.Count; i++) if (peaks[i] > peaks[i - 1] + 1e-4f) falling = false;
            Check($"a fade out with whole range on is halfway down at half time ({peaks[20]:0.###} of {settled:0.###})",
                peaks[20] <= settled * 0.6f);
            Check("a fade out with whole range on never gets louder on the way down", falling);
        }
    }
}
