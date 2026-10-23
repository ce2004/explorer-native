using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Folder sizes on the Drive letter, worked out from Drive's own listing of
    /// the whole account and kept current by the changes feed.
    /// </summary>
    internal static class DriveSizeTests
    {
        public static async Task RunAll(Action<string, bool, string?> check, Action<string, string, string> equal)
        {
            var fake = new FakeDrive();
            var client = new DriveClient(fake, 0.0005);
            var old = new DateTime(2015, 6, 1, 0, 0, 0, DateTimeKind.Utc);

            var music = fake.AddFolder("Music");
            var artist = fake.AddFolder("Artist", music);
            var album = fake.AddFolder("Album", artist);
            fake.AddFile("a.flac", new byte[100], old, album);
            fake.AddFile("b.flac", new byte[50], DateTime.UtcNow, album);
            fake.AddFile("cover.jpg", new byte[7], old, artist);
            fake.AddFile("Notes", Array.Empty<byte>(), old, music, "application/vnd.google-apps.document");
            var empty = fake.AddFolder("Empty");

            // Over a page, spread across years, so the listing has to split by time.
            var many = fake.AddFolder("Many");
            for (int i = 0; i < 2500; i++)
                fake.AddFile($"t{i}.mp3", new byte[2], new DateTime(2001 + i % 25, 1 + i % 12, 1, 0, 0, 0, DateTimeKind.Utc), many);

            var index = new DriveSizeIndex(null);
            await index.Refresh(client, CancellationToken.None);

            var m = index.SizeOf(music);
            check("Drive sizes: a folder holds everything below it", m is { Bytes: 157, Files: 3, Folders: 2 },
                m?.ToString());
            check("Drive sizes: Google documents are not counted, as the letter does not show them",
                m?.Files == 3, m?.ToString());
            var e = index.SizeOf(empty);
            check("Drive sizes: an empty folder is zero, not unknown", e is { Bytes: 0, Files: 0 }, e?.ToString());
            var all = index.SizeOf(many);
            check("Drive sizes: a listing over a page, split by time, misses nothing",
                all is { Files: 2500, Bytes: 5000 }, all?.ToString());
            var root = index.SizeOf("root");
            check("Drive sizes: the root alias resolves to the real root", root is { Files: 2503 }, root?.ToString());
            check("Drive sizes: an unknown id is unknown", index.SizeOf("nope") == null, null);

            // Changes since: a new file, and one trashed.
            fake.AddFile("c.flac", new byte[1000], DateTime.UtcNow, album);
            fake.Trash(fake.Find(album, "a.flac")!);
            int before = fake.WholeListings;
            index.Expire();
            await index.Refresh(client, CancellationToken.None);
            var after = index.SizeOf(music);
            check("Drive sizes: the changes feed adds and removes", after is { Bytes: 1057, Files: 3 }, after?.ToString());
            equal("Drive sizes: a refresh does not list the account again", "0", (fake.WholeListings - before).ToString());

            // Fresh: a second question within the window asks nothing.
            int requests = fake.Requests;
            await index.Refresh(client, CancellationToken.None);
            equal("Drive sizes: a fresh index asks Drive nothing", "0", (fake.Requests - requests).ToString());

            // 4.2.1 (Conner: "sizes don't show up on Google Drive half the
            // time"): once built, a slow changes feed is not waited out. The held
            // sizes answer within AnswerWithin and the feed finishes behind.
            check("Drive sizes: a listed index says it is built", index.Built, null);
            fake.AddFile("d.flac", new byte[5], DateTime.UtcNow, album);
            index.Expire();
            fake.AnswerDelayMs = 4000;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var slow = index.Refresh(client, CancellationToken.None);
            bool timedOut = false;
            try { await slow.WaitAsync(DriveSizeIndex.AnswerWithin); }
            catch (TimeoutException) { timedOut = true; }
            var held = index.SizeOf(music);
            check("Drive sizes: a slow feed is not waited for", timedOut && clock.Elapsed < TimeSpan.FromSeconds(3),
                clock.Elapsed.ToString());
            check("Drive sizes: and the sizes held still answer", held is { Bytes: 1057 }, held?.ToString());
            fake.AnswerDelayMs = 0;
            await slow;
            check("Drive sizes: the feed catches up behind the answer", index.SizeOf(music) is { Bytes: 1062 },
                index.SizeOf(music)?.ToString());

            var source = SourceText("GoogleDrive.cs");
            check("Drive sizes: a built index answers within AnswerWithin, not after the feed",
                source.Contains("if (index.Built) await refresh.WaitAsync(DriveSizeIndex.AnswerWithin, token)"), null);
            check("Drive sizes: the Shared drives folder adds up its drives",
                source.Contains("IsSharedDrivesFolder(path)) return await SharedDrivesTotalAsync"), null);
            check("Drive sizes: the index is started as Drive connects",
                source.Contains("WarmSizes(mount.Letter ?? mount.Root)"), null);
        }

        private static string SourceText(string name)
        {
            var at = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (at != null && !System.IO.Directory.Exists(System.IO.Path.Combine(at.FullName, "src"))) at = at.Parent;
            if (at == null) return "";
            var path = System.IO.Path.Combine(at.FullName, "src", name);
            try { return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : ""; }
            catch { return ""; }
        }
    }
}
