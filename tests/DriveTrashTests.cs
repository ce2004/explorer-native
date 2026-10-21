using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Google Drive's Trash folder on the letter (3.0.16), against the fake
    /// Drive over a scratch sync root: what it lists, Restore, Delete
    /// permanently, Empty, the setting, and above all that nothing outside the
    /// trash can ever be deleted for good from here.
    /// </summary>
    internal static class DriveTrashTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static void Check(string what, bool ok, string? detail = null) => _check("Drive trash: " + what, ok, detail);

        private static readonly DateTime When = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        public static Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            _check = check;
            Console.WriteLine("Google Drive's Trash folder, against a fake Drive:");
            SettingDefaults();
            TrashFolder();
            return Task.CompletedTask;
        }

        private static void SettingDefaults()
        {
            Check("the Trash folder is shown by default", new Settings().ShowDriveTrash);
        }

        private static string Names(IReadOnlyList<DrivePlaced>? placed) =>
            placed == null ? "(not listed)" : string.Join(",", placed.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        private static void TrashFolder()
        {
            var fake = new FakeDrive();
            var client = new DriveClient(fake, 0.0005);
            var root = Path.Combine(Path.GetTempPath(), "en-trash-" + Guid.NewGuid().ToString("N")[..8]);
            var music = fake.AddFolder("Music");
            var keep = fake.AddFile("keep.flac", new byte[] { 1, 2 }, When, music);
            var song = fake.AddFile("song.flac", new byte[] { 3, 4, 5 }, When, music);
            var album = fake.AddFolder("Old album", music);
            var track = fake.AddFile("01.flac", new byte[] { 6 }, When, album);
            var note = fake.AddFile("note.txt", new byte[] { 7 }, When, FakeDrive.Root);
            fake.Trash(song);
            fake.Trash(album);
            fake.Trash(note);

            var said = new List<string>();
            using var drive = new GoogleDrive();
            drive.Notification += (id, message) => { lock (said) said.Add(id + ": " + message); };
            DriveMount? mount = null;
            try
            {
                try
                {
                    mount = new DriveMount(client, root, 16L * 1024 * 1024, _ => { });
                    mount.Start("root", assignLetter: false);
                }
                catch (Exception ex)
                {
                    Check("a provider stands up over a scratch folder", false, ex.Message);
                    return;
                }
                drive.UseForTests(mount, client);

                string P(params string[] parts) => Path.Combine(new[] { root }.Concat(parts).ToArray());
                IReadOnlyList<DrivePlaced>? Listed(string path)
                {
                    mount!.Populate(path);
                    return mount.TryListing(path, out var entries) ? entries : null;
                }

                // ---- what is shown ----
                mount.Forget(root);
                var top = Listed(root);
                Check("the root shows Trash beside Shared drives and My Drive's own folders",
                    Names(top) == "Music,Shared drives,Trash", Names(top));
                var trash = P("Trash");
                Check("the Trash folder is known as the trash", drive.IsTrashFolder(trash) && !drive.IsInTrash(trash));
                var inside = Listed(trash);
                Check("it lists what was put in the trash, and not what is inside a trashed folder",
                    Names(inside) == "note.txt,Old album,song.flac", Names(inside));
                var folderInside = Listed(P("Trash", "Old album"));
                Check("a trashed folder opens on what was in it", Names(folderInside) == "01.flac", Names(folderInside));
                Check("things in it are in the trash, at any depth",
                    drive.IsInTrash(P("Trash", "song.flac")) && drive.IsInTrash(P("Trash", "Old album", "01.flac")));
                Check("and nothing outside it is", !drive.IsInTrash(P("Music", "keep.flac")) && !drive.IsInTrash(P("Music")));

                // ---- nothing outside the trash is ever deleted for good ----
                Listed(P("Music"));
                bool deletedOutside;
                try { deletedOutside = drive.DeleteForever(P("Music", "keep.flac")).GetAwaiter().GetResult(); }
                catch { deletedOutside = true; }
                Check("Delete permanently refuses anything not in the trash",
                    !deletedOutside && fake.PermanentDeletes == 0 && fake.Get(keep) is { Trashed: false },
                    $"{fake.PermanentDeletes} deleted");
                bool deletedNested;
                try { deletedNested = drive.DeleteForever(P("Trash", "Old album", "01.flac")).GetAwaiter().GetResult(); }
                catch { deletedNested = true; }
                Check("and one file inside a trashed folder: the folder is what goes",
                    !deletedNested && fake.PermanentDeletes == 0 && fake.Get(track) != null);

                // ---- nothing can be put in it ----
                var local = Path.Combine(Path.GetTempPath(), "en-trash-up-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
                File.WriteAllText(local, "up");
                try
                {
                    int uploads = fake.Uploads, creates = fake.Creates;
                    var up = drive.UploadInto(local, trash).GetAwaiter().GetResult();
                    var made = drive.CreateFolderIn(trash, "New", announce: false).GetAwaiter().GetResult();
                    var madeDeeper = drive.CreateFolderIn(P("Trash", "Old album"), "New", announce: false).GetAwaiter().GetResult();
                    Check("nothing can be uploaded or made in the trash, at any depth",
                        up == null && made == null && madeDeeper == null && fake.Uploads == uploads && fake.Creates == creates,
                        $"{fake.Uploads - uploads} uploads, {fake.Creates - creates} folders");
                }
                finally { try { File.Delete(local); } catch { } }
                var renamed = drive.RenameOnDrive(P("Trash", "song.flac"), "other.flac").GetAwaiter().GetResult();
                Check("nor renamed there", renamed == null && fake.Renames == 0);
                bool trashedAgain = drive.TrashPath(P("Trash", "song.flac")).GetAwaiter().GetResult();
                Check("Delete on something already in the trash does not send it again", !trashedAgain);

                // ---- Restore and Delete permanently ----
                bool restored = drive.RestoreFromTrash(P("Trash", "song.flac")).GetAwaiter().GetResult();
                Check("Restore puts it back where it was",
                    restored && fake.Get(song) is { Trashed: false } s && s.Parent == music && fake.Untrashes == 1);
                Check("and it leaves the Trash folder", Names(Listed(trash)) == "note.txt,Old album", Names(Listed(trash)));
                lock (said) Check("and says so", said.Any(m => m == "drive.restored: Restored song.flac"), string.Join(" | ", said));

                bool gone = drive.DeleteForever(P("Trash", "Old album")).GetAwaiter().GetResult();
                Check("Delete permanently deletes it and what was in it",
                    gone && fake.Get(album) == null && fake.Get(track) == null && fake.PermanentDeletes == 1);
                lock (said) Check("and says so", said.Any(m => m == "drive.deleted: Deleted Old album permanently"), string.Join(" | ", said));

                // Restored elsewhere (the web, the phone) while the Trash folder
                // still shows it: Delete permanently must not delete a live file.
                var noted = fake.Get(note)!;
                noted.Trashed = noted.ExplicitlyTrashed = false;
                bool deletedLive;
                try { deletedLive = drive.DeleteForever(P("Trash", "note.txt")).GetAwaiter().GetResult(); }
                catch (Exception) { deletedLive = false; }
                Check("Delete permanently leaves alone something restored elsewhere",
                    !deletedLive && fake.Get(note) != null, $"permanent deletes {fake.PermanentDeletes}");
                noted.Trashed = noted.ExplicitlyTrashed = true;

                // ---- Empty ----
                bool emptied = drive.EmptyTrash().GetAwaiter().GetResult();
                Check("Empty Google Drive trash deletes what is left in it",
                    emptied && fake.TrashEmptied == 1 && fake.Get(note) == null && fake.Get(keep) != null && fake.Get(song) != null);
                Check("and the Trash folder is empty afterwards", Names(Listed(trash)) == "", Names(Listed(trash)));
                lock (said) Check("and says so", said.Any(m => m == "drive.trash.emptied: Google Drive trash emptied"));

                // ---- the setting ----
                drive.SetShowTrash(false);
                Check("with the setting off, the root has no Trash folder", Names(Listed(root)) == "Music,Shared drives",
                    Names(Listed(root)));
                drive.SetShowTrash(true);
                Check("and on again, it is back", Names(Listed(root)) == "Music,Shared drives,Trash", Names(Listed(root)));
            }
            finally
            {
                try { mount?.Dispose(); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
