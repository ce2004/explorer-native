using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace ExplorerNative
{
    /// <summary>
    /// The installed copy of the application.
    ///
    /// Shell registration writes an absolute path into the registry, and that
    /// path has to keep working. Pointed at a build output it does not: a rebuild
    /// replaces the executable while the whole desktop is holding a reference to
    /// it, running the debug build re-points every folder on the machine at
    /// bin\Debug, and deleting bin leaves folders that open nothing at all.
    ///
    /// So the registration never names a build output. It names a copy in the
    /// user's own program directory, which nothing but an explicit install ever
    /// touches. Build and rebuild as much as you like; folders keep opening the
    /// copy that was installed, and they keep opening it while the rebuild is
    /// happening.
    ///
    /// <para><b>Installing does not require the application to be closed.</b></para>
    ///
    /// It used to, and that was the whole of what went wrong. Windows will not
    /// let a running image be *overwritten*, so the install asked the live
    /// instance to stand down and then copied over it — which made a file copy
    /// depend on inter-process co-operation, and therefore on the one thing that
    /// had already failed. Four instances were running with no single-instance
    /// pipe server between them; the install asked the pipe, got no answer, read
    /// that as "nothing is running", and then could not write the file. What it
    /// said about it went into a MessageBox on a machine nobody was looking at,
    /// so the caller waited three minutes and learned nothing.
    ///
    /// Windows will not let a running image be overwritten. It will happily let
    /// one be **renamed** — measured, not assumed: a process keeps running
    /// perfectly after its own executable is moved aside, because the mapping
    /// follows the file rather than the name. So the new build is staged beside
    /// the old one, the old one is renamed out of the way, and the new one is
    /// renamed into its place. Nothing has to close and nothing has to answer a
    /// pipe. There is a window between the two renames in which the path names
    /// nothing — a folder opened in that instant opens nothing — but it is two
    /// renames of files already on the volume, and the alternative is a copy
    /// that cannot happen at all while the application is running. If the second
    /// rename fails the first is put back.
    ///
    /// What a running instance keeps is the *old* binary, which is correct and
    /// is what every updater on this platform does: it carries on out of the file
    /// that has been renamed, and the next launch — the next folder anybody
    /// opens — gets the new one. See <see cref="InstallResult.RunningCopyIsStale"/>,
    /// which is how the caller knows to say so rather than implying otherwise.
    /// </summary>
    public static class AppInstall
    {
        public const string ProductName = "ExplorerNative";

        /// <summary>
        /// What a renamed-aside binary is called.
        ///
        /// Deliberately not ending in ".exe": the file is still a program and
        /// will still run, and a folder full of things called
        /// "ExplorerNative.old.exe" is a folder where somebody eventually starts
        /// the wrong one. This name cannot be double-clicked into a running
        /// application by accident, and it sorts next to the real one so it is
        /// obvious what it came from.
        /// </summary>
        public const string RetiredMarker = ".retired-";

        /// <summary>The half-written new copy, before it is renamed into place.</summary>
        private const string StagingMarker = ".installing-";

        /// <summary>
        /// Per-user, so installing needs no administrator rights and cannot
        /// affect anyone else's machine.
        /// </summary>
        public static string Directory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", ProductName);

        public static string ExePath => Path.Combine(Directory, ProductName + ".exe");

        /// <summary>Where this running process actually lives.</summary>
        public static string RunningDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        public static bool IsInstalled
        {
            get { try { return File.Exists(ExePath); } catch { return false; } }
        }

        /// <summary>True when this process is the installed copy.</summary>
        public static bool IsRunningInstalledCopy
        {
            get
            {
                try
                {
                    return string.Equals(
                        Path.GetFullPath(RunningDirectory).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(Directory).TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// What an install did.
        /// </summary>
        /// <param name="RunningCopyIsStale">
        /// True when the executable had to be renamed aside because something was
        /// running from it. The install worked; the process that was running is
        /// still executing the previous build until it is restarted, and saying
        /// so is the difference between an honest report and one that implies a
        /// change is live when it is not.
        /// </param>
        /// <param name="Hash">
        /// SHA-256 of the executable now at <paramref name="ExePath"/>, read back
        /// off the disk after the install rather than carried over from the copy.
        /// The old procedure had a step 5 that said "prove it, by hash — the
        /// dialog not complaining is not proof"; a tool that can prove its own
        /// work should not leave that to whoever remembers to do it.
        /// </param>
        public sealed record InstallResult(
            bool Ok,
            string ExePath,
            string? Problem,
            bool RunningCopyIsStale = false,
            string? Hash = null);

        /// <summary>
        /// Copies this build into the install directory, replacing whatever is
        /// there — running or not. Returns what happened rather than throwing,
        /// because every caller has somebody to tell.
        /// </summary>
        public static InstallResult Install()
        {
            try
            {
                if (IsRunningInstalledCopy)
                    return new InstallResult(true, ExePath, null, false, HashOf(ExePath));

                var source = RunningDirectory;
                System.IO.Directory.CreateDirectory(Directory);

                // Anything a previous install had to rename aside. Done first, so
                // a machine that has been updated twenty times is not carrying
                // twenty copies of a hundred and sixty megabytes — and done
                // best-effort, because the one belonging to a process that is
                // still running cannot go yet and is not a failure.
                SweepRetired();

                bool anyRenamed = false;

                // The application's own files and nothing else. It copied every
                // file beside the executable, which for a copy run from Downloads
                // is the whole of Downloads, installed and then kept in step.
                foreach (var file in FilesToInstall(source))
                {
                    var name = Path.GetFileName(file);
                    var target = Path.Combine(Directory, name);

                    try
                    {
                        if (ReplaceFile(file, target)) anyRenamed = true;
                    }
                    catch (Exception ex)
                    {
                        return new InstallResult(false, ExePath, $"{name}: {ex.Message}");
                    }
                }

                if (!File.Exists(ExePath))
                    return new InstallResult(false, ExePath, "The executable was not copied.");

                // Read back off the disk and compared against what went in. A
                // copy that half-wrote, or that landed somewhere else because a
                // path was wrong, otherwise reports success and is found later by
                // somebody wondering why their change is not in the application.
                var wanted = HashOf(Path.Combine(source, ProductName + ".exe"));
                var landed = HashOf(ExePath);

                if (wanted != null && landed != null && wanted != landed)
                    return new InstallResult(false, ExePath,
                        "The installed executable does not match the one that was installed. " +
                        "Nothing has been left half-written, but the copy did not take.");

                SweepStale(FilesToInstall(source));

                return new InstallResult(true, ExePath, null, anyRenamed, landed);
            }
            catch (Exception ex)
            {
                return new InstallResult(false, ExePath, ex.Message);
            }
        }

        /// <summary>
        /// Puts <paramref name="source"/> at <paramref name="target"/>, whether or
        /// not something is running from it. True when the old file had to be
        /// renamed aside to do it.
        ///
        /// Three steps, in the only order that never leaves the path empty:
        /// stage the new bytes beside the target under a name nothing looks for,
        /// rename the live file out of the way, rename the staged file in. If the
        /// last step fails the second is undone, so the worst case is the old
        /// file exactly where it was.
        ///
        /// The plain overwrite is tried first because it is what happens when
        /// nothing is running, it is one operation instead of three, and it
        /// leaves nothing to sweep.
        ///
        /// Internal rather than private so the suite can point it at a temporary
        /// folder and a process of its own. This is the one piece of the install
        /// that had to be *measured* rather than reasoned about — Windows
        /// refusing an overwrite and allowing a rename of the same running file
        /// is not something the type system knows — so it is also the one piece
        /// that must be exercised against a real running executable rather than
        /// against a file with a handle open on it, which behaves differently.
        /// </summary>
        internal static bool ReplaceFile(string source, string target)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var staged = target + StagingMarker + stamp;
            var retired = target + RetiredMarker + stamp;

            // Always written beside the target first, never straight over it. A
            // copy over the live file that stops part way — a full disk, a
            // cancelled session — left the registered handler of every folder on
            // the machine as half an executable, and a folder opened during the
            // copy started one.
            File.Copy(source, staged, overwrite: true);

            // Then one rename over the top, which either happens or does not. It
            // is refused for a running image, which is the case below.
            try
            {
                File.Move(staged, target, overwrite: true);
                return false;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            try
            {
                // Allowed on a running image, and measured to be: the process
                // carries on out of the renamed file without noticing.
                File.Move(target, retired);
            }
            catch
            {
                TryDelete(staged);
                throw;
            }

            try
            {
                File.Move(staged, target);
            }
            catch
            {
                // Put back exactly what was there. Better a failed install than a
                // path that names nothing, which for the registered handler of
                // every folder on the machine means no file manager at all.
                try { File.Move(retired, target); } catch { }
                TryDelete(staged);
                throw;
            }

            TryDelete(retired);
            return true;
        }

        /// <summary>
        /// Removes binaries renamed aside by an earlier install, and any staging
        /// file a failed one left behind.
        ///
        /// Best-effort by design: the one a live process is still executing
        /// cannot be deleted until that process exits, which is exactly the
        /// normal case straight after an update. It goes on the next sweep
        /// instead — which is why this runs at startup as well as at install
        /// time, since by then whatever was holding it has usually gone.
        /// </summary>
        public static int SweepRetired() => SweepRetired(Directory);

        /// <summary>
        /// The same, in a named folder. Internal so the suite can sweep its own
        /// scratch directory instead of the real install.
        /// </summary>
        internal static int SweepRetired(string folder)
        {
            int removed = 0;

            try
            {
                if (!System.IO.Directory.Exists(folder)) return 0;

                foreach (var file in System.IO.Directory.GetFiles(folder))
                {
                    var name = Path.GetFileName(file);
                    if (!name.Contains(RetiredMarker, StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains(StagingMarker, StringComparison.OrdinalIgnoreCase)) continue;

                    if (TryDelete(file)) removed++;
                }
            }
            catch { }

            return removed;
        }

        /// <summary>
        /// Anything left over from a previous install goes.
        ///
        /// The copy alone only ever adds, so installing a single-file build over
        /// the older multi-file one left its DLLs, deps.json and runtime config
        /// sitting beside the new executable — dead weight that looks like part
        /// of the application and would be loaded in preference to what is inside
        /// it if the layout ever changed back.
        /// </summary>
        private static void SweepStale(IEnumerable<string> installed)
        {
            var expected = new HashSet<string>(
                installed.Select(Path.GetFileName)!,
                StringComparer.OrdinalIgnoreCase);

            foreach (var stale in System.IO.Directory.GetFiles(Directory))
            {
                var name = Path.GetFileName(stale);
                if (expected.Contains(name)) continue;

                // Not the ones this install just made. A retired binary is still
                // mapped by whatever is running from it, and trying to delete it
                // here would be a guaranteed failure on the one path where the
                // install has otherwise just succeeded.
                if (name.Contains(RetiredMarker, StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(StagingMarker, StringComparison.OrdinalIgnoreCase)) continue;

                TryDelete(stale);
            }
        }

        /// <summary>
        /// Which files beside the running executable are the application.
        ///
        /// A single-file build is exactly one: everything else is inside it. A
        /// build that is not single-file is its executable plus the libraries and
        /// runtime files the build put beside it — named for what they are, so a
        /// text file or a download that happens to share the folder is not
        /// mistaken for part of the program.
        /// </summary>
        internal static List<string> FilesToInstall(string source)
        {
            var exe = Path.Combine(source, ProductName + ".exe");
            var files = new List<string>();

            // A single-file bundle carries its own assembly inside the executable,
            // so there is no ExplorerNative.dll beside it.
            if (!File.Exists(Path.Combine(source, ProductName + ".dll")))
            {
                if (File.Exists(exe)) files.Add(exe);
                return files;
            }

            foreach (var file in System.IO.Directory.GetFiles(source))
            {
                var name = Path.GetFileName(file);
                var extension = Path.GetExtension(name);
                bool ours = name.StartsWith(ProductName + ".", StringComparison.OrdinalIgnoreCase) &&
                            !name.Contains(RetiredMarker, StringComparison.OrdinalIgnoreCase) &&
                            !name.Contains(StagingMarker, StringComparison.OrdinalIgnoreCase);
                bool library = extension.Equals(".dll", StringComparison.OrdinalIgnoreCase);
                if (ours || library) files.Add(file);
            }

            return files;
        }

        private static bool TryDelete(string path)
        {
            try { File.Delete(path); return true; }
            catch { return false; }
        }

        /// <summary>SHA-256 of a file, or null if it cannot be read.</summary>
        public static string? HashOf(string path)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);

                return Convert.ToHexString(SHA256.HashData(file));
            }
            catch { return null; }
        }

        /// <summary>
        /// Whether something is running out of the installed copy.
        ///
        /// Asked of the filesystem rather than of the process list, because the
        /// question is "can this file be written", which is the thing that
        /// actually decides what the install has to do — and because a process
        /// list can be answered "no" by a copy that is running under another
        /// name.
        /// </summary>
        public static bool InstalledCopyIsRunning()
        {
            try
            {
                if (!File.Exists(ExePath)) return false;
                using var probe = new FileStream(ExePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException) { return true; }
            catch { return false; }
        }

        /// <summary>
        /// Removes the installed copy. Refuses while it is the copy running, so a
        /// process cannot delete the files out from underneath itself.
        /// </summary>
        public static bool Uninstall()
        {
            try
            {
                if (IsRunningInstalledCopy) return false;
                if (!System.IO.Directory.Exists(Directory)) return true;
                System.IO.Directory.Delete(Directory, recursive: true);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// The path that should be written into the registry: the installed copy
        /// if there is one, otherwise this process, so that registering still
        /// does something sensible before an install has ever happened.
        /// </summary>
        public static string PreferredRegistrationPath =>
            IsInstalled ? ExePath : System.Windows.Forms.Application.ExecutablePath;
    }
}
