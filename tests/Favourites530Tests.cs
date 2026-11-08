using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// 5.3.0: favourite folders (Ctrl+D, Ctrl+Shift+1 to 9, Organise
    /// favourites) and the window coming back after an update when it was open.
    /// </summary>
    internal static class Favourites530Tests
    {
        public static Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            ListRules(check, equal);
            SettingsKeepThem(check, equal);
            OrganiseWindow(check, equal);
            Reopen(check);
            Wiring(check);
            return Task.CompletedTask;
        }

        private static void ListRules(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var list = new List<string>();
            list = Favourites.Add(list, @"C:\Users\Me\Documents\")!;
            list = Favourites.Add(list, @"C:\Users\Me\Downloads")!;
            equal("favourites: added in order, trailing backslash dropped", @"C:\Users\Me\Documents|C:\Users\Me\Downloads", string.Join("|", list));
            equal("favourites: adding one already there changes nothing", "2", Favourites.Add(list, @"c:\users\me\DOCUMENTS")!.Count.ToString());
            check("favourites: found whatever the case or trailing backslash", Favourites.IndexOf(list, @"c:\users\me\downloads\") == 1, null);
            equal("favourites: a drive root keeps its backslash", @"D:\", Favourites.Key(@"D:\"));

            var full = Enumerable.Range(1, Favourites.Most).Select(i => @"C:\f" + i).ToList();
            check("favourites: nine at most", Favourites.Add(full, @"C:\ten") == null, null);

            equal("favourites: move down", @"C:\Users\Me\Downloads|C:\Users\Me\Documents", string.Join("|", Favourites.Move(list, 0, +1)));
            equal("favourites: move past the end does nothing", @"C:\Users\Me\Documents|C:\Users\Me\Downloads", string.Join("|", Favourites.Move(list, 1, +1)));
            equal("favourites: remove", @"C:\Users\Me\Downloads", string.Join("|", Favourites.RemoveAt(list, 0)));

            equal("favourites: named by the folder", "Downloads", Favourites.Label(list, 1));
            var twins = new List<string> { @"C:\Music", @"D:\Music", @"D:\" };
            equal("favourites: two of one name show the whole path", @"D:\Music", Favourites.Label(twins, 1));
            equal("favourites: a drive is its own name", @"D:\", Favourites.Label(twins, 2));

            var messy = new List<string> { "", @"C:\a", @"c:\A\", "  ", @"C:\b" };
            equal("favourites: blanks and repeats are dropped", @"C:\a|C:\b", string.Join("|", Favourites.Clean(messy)));
        }

        private static void SettingsKeepThem(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var s = new Settings { FavouriteFolders = new List<string> { @"C:\a", @"C:\a\", @"C:\b" } };
            var fixedUp = s.Clone().Validated();
            equal("favourites: a repeat in the file is repaired", @"C:\a|C:\b", string.Join("|", fixedUp.FavouriteFolders));
            var nulled = System.Text.Json.JsonSerializer.Deserialize<Settings>("{\"FavouriteFolders\": null}", Settings.JsonFormat)!.Validated();
            check("favourites: null in the file is an empty list", nulled.FavouriteFolders is { Count: 0 }, null);

            var before = new Settings();
            var after = before.Clone();
            after.FavouriteFolders = new List<string> { @"C:\x" };
            var live = new Settings { FontSize = 15 };
            var merged = Settings.WithChanges(live, before, after);
            check("favourites: OK in Preferences carries a change", merged.FavouriteFolders.SequenceEqual(new[] { @"C:\x" }) && merged.FontSize == 15, null);
            check("favourites: as a list of its own", !ReferenceEquals(merged.FavouriteFolders, after.FavouriteFolders), null);
            var untouched = Settings.WithChanges(new Settings { FavouriteFolders = new() { @"C:\live" } }, new Settings(), new Settings());
            check("favourites: a dialog that did not touch them leaves them alone", untouched.FavouriteFolders.SequenceEqual(new[] { @"C:\live" }), null);
        }

        private static void OrganiseWindow(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            string? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var saved = new List<List<string>>();
                    using var window = new FavouritesForm(new[] { @"C:\Users\Me\Documents", @"C:\Users\Me\Downloads", @"E:\Music" });
                    window.Changed = l => saved.Add(l.ToList());
                    window.Show();
                    Application.DoEvents();
                    var list = Find<ListBox>(window).First();
                    equal("organise: rows say the number, the name, then where", @"1, Documents, C:\Users\Me\Documents", list.Items[0]?.ToString() ?? "");
                    list.SelectedIndex = 1;
                    Press(list, Keys.Alt | Keys.Up);
                    check("organise: Alt+Up moves it and keeps it at once", saved.Count == 1 && saved[0][0] == @"C:\Users\Me\Downloads", string.Join("|", saved.LastOrDefault() ?? new()));
                    check("organise: and the cursor follows it", list.SelectedIndex == 0, null);
                    list.SelectedIndex = 2;
                    Press(list, Keys.Delete);
                    check("organise: Delete removes it", saved.Count == 2 && saved[1].Count == 2 && !saved[1].Contains(@"E:\Music"), null);
                    list.SelectedIndex = 1;
                    Press(list, Keys.Enter);
                    equal("organise: Enter goes there", @"C:\Users\Me\Documents", window.GoTo ?? "");
                    check("organise: and closes with OK", window.DialogResult == DialogResult.OK, null);

                    using var empty = new FavouritesForm(Array.Empty<string>());
                    empty.Show();
                    Application.DoEvents();
                    var none = Find<ListBox>(empty).First();
                    check("organise: with none it says how to add one", none.Items.Count == 1 && (none.Items[0]?.ToString() ?? "").Contains("Ctrl+D"), null);
                    Press(none, Keys.Delete);
                    Press(none, Keys.Enter);
                    check("organise: and Enter or Delete there do nothing", empty.GoTo == null && empty.Visible, null);
                }
                catch (Exception ex) { error = ex.ToString(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(30));
            equal("organise favourites runs", "", error ?? "");
        }

        private static void Press(Control control, Keys keys)
        {
            var e = new KeyEventArgs(keys);
            typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(control, new object[] { e });
            Application.DoEvents();
        }

        private static IEnumerable<T> Find<T>(Control root) where T : Control
        {
            foreach (Control c in root.Controls)
            {
                if (c is T t) yield return t;
                foreach (var inner in Find<T>(c)) yield return inner;
            }
        }

        private static void Reopen(Action<string, bool, string?> check)
        {
            var previous = Settings.OverrideAppDataDir;
            var dir = Path.Combine(Path.GetTempPath(), "en-reopen-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Settings.OverrideAppDataDir = dir;
            try
            {
                check("reopen: no note, no window", !ReopenAfterUpdate.Take(), null);
                ReopenAfterUpdate.Mark();
                check("reopen: a fresh note opens the window", ReopenAfterUpdate.Take(), null);
                check("reopen: once only", !ReopenAfterUpdate.Take() && !File.Exists(ReopenAfterUpdate.NotePath), null);
                ReopenAfterUpdate.Mark(DateTime.UtcNow - TimeSpan.FromHours(3));
                check("reopen: an old note (a quit that never restarted) is ignored, and cleared", !ReopenAfterUpdate.Take() && !File.Exists(ReopenAfterUpdate.NotePath), null);
                File.WriteAllText(ReopenAfterUpdate.NotePath, "rubbish");
                check("reopen: a broken note is ignored", !ReopenAfterUpdate.Take(), null);

                // 5.6.1 (Conner): copies, moves and emptying the trash carry on after an update.
                check("carry on: nothing waiting", !CarryOnTransfers.Waiting() && CarryOnTransfers.Take().Count == 0, null);
                CarryOnTransfers.Save(new[]
                {
                    new CarryOnTransfers.Job(CarryOnTransfers.Kind.Copy, new[] { @"C:\a\one.txt", @"C:\a\Two Folder" }, @"D:\backup"),
                    new CarryOnTransfers.Job(CarryOnTransfers.Kind.Move, new[] { @"E:\x" }, @"G:\My Drive"),
                    new CarryOnTransfers.Job(CarryOnTransfers.Kind.EmptyTrash, Array.Empty<string>(), "G:"),
                });
                check("carry on: a note is waiting", CarryOnTransfers.Waiting(), null);
                var back = CarryOnTransfers.Take();
                check("carry on: all three come back", back.Count == 3, back.Count.ToString());
                if (back.Count == 3)
                {
                    check("carry on: a copy with both sources", back[0].What == CarryOnTransfers.Kind.Copy && back[0].Paths.Length == 2 &&
                        back[0].Paths[1] == @"C:\a\Two Folder" && back[0].Destination == @"D:\backup", null);
                    check("carry on: a move", back[1].Move && back[1].Destination == @"G:\My Drive", null);
                    check("carry on: emptying the trash", back[2].What == CarryOnTransfers.Kind.EmptyTrash, null);
                }
                check("carry on: once only", !CarryOnTransfers.Waiting() && CarryOnTransfers.Take().Count == 0, null);
                CarryOnTransfers.Save(new[] { new CarryOnTransfers.Job(CarryOnTransfers.Kind.Copy, new[] { @"C:\a" }, @"D:\b") },
                    DateTime.UtcNow - TimeSpan.FromHours(2));
                check("carry on: an old note is ignored", CarryOnTransfers.Take().Count == 0, null);
            }
            finally
            {
                Settings.OverrideAppDataDir = previous;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static string Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            try { return dir == null ? "" : File.ReadAllText(Path.Combine(dir.FullName, "src", name)); }
            catch { return ""; }
        }

        private static void Wiring(Action<string, bool, string?> check)
        {
            var tray = Source("TrayApplicationContext.cs");
            int exit = tray.IndexOf("if (request == SingleInstance.ExitCommand)", StringComparison.Ordinal);
            int mark = tray.IndexOf("ReopenAfterUpdate.Mark()", exit, StringComparison.Ordinal);
            int quit = tray.IndexOf("Quit();", exit, StringComparison.Ordinal);
            check("reopen: an install's or update's exit notes an open window before quitting", exit > 0 && mark > exit && mark < quit, null);
            check("reopen: only an open window is noted", tray.Contains("if (_form is { IsDisposed: false, Visible: true }) ReopenAfterUpdate.Mark();", StringComparison.Ordinal), null);
            check("reopen: the next start shows the window for it, even when starting minimised",
                tray.Contains("if (!_settings.StartMinimised || askedForAFolder || wasOpenBeforeUpdate", StringComparison.Ordinal) &&
                tray.IndexOf("ShowWindow();", tray.IndexOf("|| wasOpenBeforeUpdate", StringComparison.Ordinal), StringComparison.Ordinal) > 0, null);

            var main = Source("MainForm.cs");
            check("favourites: Ctrl+D adds", main.Contains("keyData == (Keys.Control | Keys.D)", StringComparison.Ordinal) && main.Contains("AddFavourite();", StringComparison.Ordinal), null);
            check("favourites: Ctrl+Shift+1 to 9 go", main.Contains("is >= Keys.D1 and <= Keys.D9", StringComparison.Ordinal) && main.Contains("GoToFavourite(", StringComparison.Ordinal), null);
            check("favourites: the menu is on the menu bar", main.Contains("menu.Items.Add(BuildFavouritesMenu());", StringComparison.Ordinal), null);
            check("favourites: going to one announces nothing of its own",
                main.IndexOf("Navigate(list[number - 1]);", StringComparison.Ordinal) > main.IndexOf("private void GoToFavourite(int number)", StringComparison.Ordinal), null);
        }

    }
}
